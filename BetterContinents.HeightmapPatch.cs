// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0), and on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // Heightmap.GetBiomeColor(float ix, float iy) - Heightmap.cs:632, the colour of one terrain vertex (the texture
  // weights the terrain shader reads). RebuildRenderMesh calls it with ix = SmoothStep(0, 1, column / width) and iy
  // likewise. This one prefix owns the target (PatchBiomeColor): the terrain map wins where it has a colour, then the
  // biome precision grid, and otherwise vanilla blends the zone's four corners.
  public static bool GetBiomeColorPatch(Heightmap __instance, float ix, float iy, ref Color __result)
  {
    if (Settings.HasTerrainMap)
    {
      // Unchanged since 0.7.x: this takes ix as a linear position, so the terrain map is read up to about 6 m
      // nearer the zone's edges than the vertex it colours (see VertexPosition).
      var x = __instance.transform.position.x + ix * 64f - 32f;
      var y = __instance.transform.position.z + iy * 64f - 32f;
      if (Settings.ApplyTerrainMap(x, y, ref __result))
        return false;
    }
    if (BiomePrecisionGrid.For(__instance.m_cornerBiomes) is { } grid)
    {
      __result = grid.GetColor(ix, iy, __instance.m_width);
      return false;
    }
    return true;
  }

  // ------------------------------------------------------------------------------------------------------------------
  // Biome precision (config "Biome precision", console "bc b p"). Vanilla decides the biome anywhere inside a 64 m
  // terrain zone from the zone's four corners alone (Heightmap.GetBiome and GetBiomeColor), so biome borders on the
  // ground - textures, grass, vegetation, spawn points - follow the 64 m zone grid, whatever the biome map says.
  // Precision N samples the biome on (N + 1) x (N + 1) cells per zone instead and applies vanilla's corner rules
  // inside the cell that holds the point. N = 0 is vanilla: nothing is patched or sampled.
  //
  // The grid is kept beside the zone's corner array, never in it: HeightmapBuilder.Build still builds everything
  // exactly as vanilla (heights, paint mask, the four corner sectors), and m_cornerBiomes keeps its four entries, so
  // every vanilla reader of it (HaveBiome, IsBiomeEdge, Generate's alt-biome list that SpawnSystem reads) and every
  // other mod sees what vanilla built. Only the three patches below read the grid.
  // ------------------------------------------------------------------------------------------------------------------

  // Heightmap.GetBiome(Vector3 point, float oceanLevel, bool waterAlwaysOcean) - Heightmap.cs:478. Grass
  // (ClutterSystem), vegetation and spawn points (ZoneSystem.GetGroundData), Heightmap.FindBiome and building
  // placement ask this. Distant LOD and waterAlwaysOcean keep vanilla's answer from WorldGenerator, as does a
  // heightmap built while precision was off.
  internal static bool GetBiomePatch(Heightmap __instance, Vector3 point, bool waterAlwaysOcean, ref Heightmap.Biome __result)
  {
    if (__instance.m_isDistantLod || waterAlwaysOcean || BiomePrecisionGrid.For(__instance.m_cornerBiomes) is not { } grid)
      return true;
    __instance.WorldToNormalizedHM(point, out var x, out var y);
    __result = grid.GetBiome(x, y);
    return false;
  }

  // Heightmap.HaveBiome(Biome biome) - Heightmap.cs:469, the "is this biome in the zone" test before vanilla places
  // a vegetation type (ZoneSystem.PlaceVegetation) or runs a spawner (SpawnSystem.UpdateSpawnList). It reads the four
  // corners; a biome the grid finds between them passes too, or GetBiome would name a biome that never gets its
  // vegetation or spawns. Only ever widens: each point is still checked with GetBiome.
  internal static void HaveBiomePatch(Heightmap __instance, Heightmap.Biome biome, ref bool __result)
  {
    if (!__result && BiomePrecisionGrid.For(__instance.m_cornerBiomes) is { } grid)
      __result = (grid.Biomes & biome) != 0;
  }

  // HeightmapBuilder.Build(HMBuildData data) - HeightmapBuilder.cs:148, on the game's terrain builder thread. Bound
  // once by PatchAll at load and never re-patched: a precision change at run time (bc b p) only changes
  // BiomePrecisionGrid.Active, which a build reads once, and every grid records its own size. Inert while it is 0.
  [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.Build))]
  internal static class HeightmapBuilderBuildPatch
  {
    private static void Postfix(HeightmapBuilder.HMBuildData data)
    {
      // An exception would end the builder thread, and every later zone would wait for it forever.
      try
      {
        BiomePrecisionGrid.Attach(data);
      }
      catch (Exception e)
      {
        BiomePrecisionGrid.Failed(e);
      }
    }
  }

  // The configured precision where it applies: 0 unless Better Continents is on for the world, at most MaxPrecision.
  internal static int EffectiveBiomePrecision(BetterContinentsSettings settings) =>
    settings.EnabledForThisWorld ? Mathf.Clamp(settings.BiomePrecision, 0, BiomePrecisionGrid.MaxPrecision) : 0;

  // Valheim 1.0 stores biome sectors (biome plus alt biome data) in the corner array instead of
  // plain biomes. Sectors for points where the world data doesn't match the Better Continents biome.
  internal static readonly BiomeSector[] PlainSectors =
    [.. Enumerable.Range(0, (int)Heightmap.BiomeIndex.Count).Select(i => new BiomeSector(null, ((Heightmap.BiomeIndex)i).ToBiome()))];

  // The world sector data has a resolution of 12 meters and is built from the patched GetBiome,
  // so it's used when it agrees with the biome here, to keep any alt biomes of that sector.
  internal static BiomeSector GetSector(WorldGenerator worldGen, Heightmap.Biome biome, float wx, float wy)
  {
    var sector = worldGen.GetBiomeSector(wx, wy);
    if (sector != null && sector.Biome == biome)
      return sector;
    return PlainSectors[ImageMapBiome.ToSafeIndex(biome)];
  }

  // The biome samples of one zone heightmap: (Cells + 1) x (Cells + 1) sectors from its south-west corner, one row
  // (constant z) after another. Made on the builder thread, read on the main thread, never changed.
  internal sealed class BiomePrecisionGrid
  {
    public const int MaxPrecision = 5;

    // The precision new builds sample at, 0 = off. Set on the main thread (PatchHeightmap), read once per build on
    // the builder thread.
    internal static volatile int Active;

    public readonly int Cells;
    public readonly int Size;
    public readonly BiomeSector[] Sectors;
    // Every biome in the grid (HaveBiome), and whether there is only one (GetBiome's shortcut).
    public readonly Heightmap.Biome Biomes;
    public readonly bool Uniform;

    internal BiomePrecisionGrid(int cells, BiomeSector[] sectors)
    {
      if (cells < 1 || sectors.Length != (cells + 1) * (cells + 1))
        throw new ArgumentException($"a biome precision grid of {cells} cells needs {(cells + 1) * (cells + 1)} sectors, not {sectors.Length}");
      Cells = cells;
      Size = cells + 1;
      Sectors = sectors;
      Uniform = true;
      foreach (var sector in sectors)
      {
        Biomes |= sector.Biome;
        if (sector.Biome != sectors[0].Biome)
          Uniform = false;
      }
    }

    // Samples the zone of a finished build, keyed by the corner array that build made.
    internal static void Attach(HeightmapBuilder.HMBuildData data)
    {
      int precision = Active;
      if (precision <= 0 || data.m_distantLod || data.m_cornerBiomes == null || data.m_worldGen == null)
        return;
      // The zone's south-west corner, computed as HeightmapBuilder.cs:152 does.
      var origin = data.m_center + new Vector3((float)data.m_width * data.m_scale * -0.5f, 0f, (float)data.m_width * data.m_scale * -0.5f);
      var grid = Sample(data.m_worldGen, origin.x, origin.z, (double)data.m_width * (double)data.m_scale, precision + 1);
      Add(data.m_cornerBiomes, grid);
    }

    // Each sample takes the biome WorldGenerator gives for that point (the biome map's where it has one) and the
    // world's 12 m sector there when that sector agrees, so alt biomes are kept (GetSector).
    internal static BiomePrecisionGrid Sample(WorldGenerator worldGen, float x0, float z0, double span, int cells)
    {
      int size = cells + 1;
      var sectors = new BiomeSector[size * size];
      for (int row = 0; row < size; row++)
      {
        float wz = SamplePosition(z0, span, row, cells);
        for (int col = 0; col < size; col++)
        {
          float wx = SamplePosition(x0, span, col, cells);
          sectors[row * size + col] = GetSector(worldGen, worldGen.GetBiome(wx, wz), wx, wz);
        }
      }
      return new BiomePrecisionGrid(cells, sectors);
    }

    // Vanilla's corner arithmetic (HeightmapBuilder.cs:156-162): the outer samples are exactly the points vanilla
    // takes its corner biomes from, and neighbouring zones sample their shared edge at the same points.
    internal static float SamplePosition(float origin, double span, int i, int cells) =>
      (float)((double)origin + span * ((double)i / cells));

    [ThreadStatic] private static float[]? Weights;

    // Vanilla's rule (Heightmap.cs:484-509) inside the cell that holds the point: the cell's four corners, weighted
    // by Heightmap.Distance in cell units; the heaviest biome wins, a tie going to the lower BiomeIndex. x and y run
    // 0 to 1 across the zone (WorldToNormalizedHM).
    public Heightmap.Biome GetBiome(float x, float y)
    {
      if (Uniform)
        return Sectors[0].Biome;
      Cell(x, out int cx, out float fx);
      Cell(y, out int cy, out float fy);
      int i = cy * Size + cx;
      var b0 = Sectors[i].Biome;
      var b1 = Sectors[i + 1].Biome;
      var b2 = Sectors[i + Size].Biome;
      var b3 = Sectors[i + Size + 1].Biome;
      if (b0 == b1 && b0 == b2 && b0 == b3)
        return b0;
      var weights = Weights ??= new float[(int)Heightmap.BiomeIndex.Count];
      Array.Clear(weights, 0, weights.Length);
      weights[ImageMapBiome.ToSafeIndex(b0)] += Heightmap.Distance(fx, fy, 0f, 0f);
      weights[ImageMapBiome.ToSafeIndex(b1)] += Heightmap.Distance(fx, fy, 1f, 0f);
      weights[ImageMapBiome.ToSafeIndex(b2)] += Heightmap.Distance(fx, fy, 0f, 1f);
      weights[ImageMapBiome.ToSafeIndex(b3)] += Heightmap.Distance(fx, fy, 1f, 1f);
      int best = 0;
      float most = -99999f;
      for (int j = 1; j < weights.Length; j++)
      {
        if (weights[j] > most)
        {
          best = j;
          most = weights[j];
        }
      }
      return ((Heightmap.BiomeIndex)best).ToBiome();
    }

    // The cell along one axis that holds t (0 to 1 across the zone), and t's position inside it.
    private void Cell(float t, out int cell, out float f)
    {
      float g = t * Cells;
      cell = Mathf.Clamp(Mathf.FloorToInt(g), 0, Cells - 1);
      f = g - cell;
    }

    // Vanilla's blend (Heightmap.cs:632-645) inside the cell of one vertex. The vertex's position is recovered from
    // ix and iy (see VertexPosition) to find the cell, and the blend across the cell is SmoothStep again, so a grid
    // of one cell per zone gives exactly vanilla's colours.
    public Color GetColor(float ix, float iy, int width)
    {
      ColorCell(ix, width, out int cx, out float u);
      ColorCell(iy, width, out int cy, out float v);
      int i = cy * Size + cx;
      var s0 = Sectors[i];
      var s1 = Sectors[i + 1];
      var s2 = Sectors[i + Size];
      var s3 = Sectors[i + Size + 1];
      if (s0 == s1 && s0 == s2 && s0 == s3)
        return Heightmap.GetBiomeColor(s0);
      Color32 a = Color32.Lerp(Heightmap.GetBiomeColor(s0), Heightmap.GetBiomeColor(s1), u);
      Color32 b = Color32.Lerp(Heightmap.GetBiomeColor(s2), Heightmap.GetBiomeColor(s3), u);
      return Color32.Lerp(a, b, v);
    }

    private void ColorCell(float s, int width, out int cell, out float blend)
    {
      double g = VertexPosition(s, width) * Cells;
      cell = Math.Min(Math.Max((int)Math.Floor(g), 0), Cells - 1);
      blend = DUtils.SmoothStep(0f, 1f, (float)(g - cell));
    }

    // The t with s = SmoothStep(0, 1, t) = 3t^2 - 2t^3 on 0 to 1, and exactly vertex / width when s is the value
    // RebuildRenderMesh (Heightmap.cs:750) computes for a vertex of a heightmap that wide.
    internal static double VertexPosition(float s, int width)
    {
      double t = 0.5 - Math.Sin(Math.Asin(1.0 - 2.0 * Mathf.Clamp01(s)) / 3.0);
      if (width > 0)
      {
        double vertex = Math.Round(t * width);
        if (DUtils.SmoothStep(0f, 1f, (float)(vertex / width)) == s)
          return vertex / width;
      }
      return t;
    }

    // ---- grids by corner array ---------------------------------------------------------------------------------
    // Heightmap.Generate hands a build's corner array to its heightmap (m_cornerBiomes = m_buildData.m_cornerBiomes,
    // Heightmap.cs:434), so the array identifies the build a heightmap currently holds, and the grid can never be
    // paired with other corners. The array is held weakly, and an entry goes once its array is gone (the heightmap
    // was rebuilt, or unloaded with its zone). Not a ConditionalWeakTable: the game's Mono runs the Boehm collector
    // (libmonobdwgc), which has no ephemerons, so there a ConditionalWeakTable never lets a key or value go.
    private sealed class Entry(BiomeSector[] corners, BiomePrecisionGrid grid, Entry? next)
    {
      public readonly WeakReference Corners = new(corners);
      public readonly BiomePrecisionGrid Grid = grid;
      public Entry? Next = next;
    }

    private static readonly Dictionary<int, Entry> Table = [];
    private static int AddsSincePrune;
    private const int PruneEvery = 256;

    internal static void Add(BiomeSector[] corners, BiomePrecisionGrid grid)
    {
      int hash = RuntimeHelpers.GetHashCode(corners);
      lock (Table)
      {
        Table.TryGetValue(hash, out var head);
        Table[hash] = new Entry(corners, grid, Live(head, corners));
        if (++AddsSincePrune >= PruneEvery)
          Prune();
      }
    }

    // The grid of the build these corners came from, while precision is on.
    internal static BiomePrecisionGrid? For(BiomeSector[]? corners)
    {
      if (Active <= 0 || corners == null)
        return null;
      int hash = RuntimeHelpers.GetHashCode(corners);
      lock (Table)
      {
        if (!Table.TryGetValue(hash, out var entry))
          return null;
        for (; entry != null; entry = entry.Next)
        {
          if (ReferenceEquals(entry.Corners.Target, corners))
            return entry.Grid;
        }
      }
      return null;
    }

    internal static void Clear()
    {
      lock (Table)
      {
        Table.Clear();
        AddsSincePrune = 0;
      }
    }

    // Drops the entries whose corner arrays are gone.
    internal static void Prune()
    {
      lock (Table)
      {
        AddsSincePrune = 0;
        foreach (var hash in Table.Keys.ToList())
        {
          var head = Live(Table[hash], null);
          if (head == null)
            Table.Remove(hash);
          else
            Table[hash] = head;
        }
      }
    }

    internal static int Count
    {
      get
      {
        lock (Table)
        {
          int count = 0;
          foreach (var head in Table.Values)
          {
            for (var entry = head; entry != null; entry = entry.Next)
              count++;
          }
          return count;
        }
      }
    }

    // A chain without its dead entries and without the one for `except`, relinked in place (callers hold the lock).
    private static Entry? Live(Entry? head, object? except)
    {
      static bool Drop(Entry entry, object? except) => entry.Corners.Target is not { } target || ReferenceEquals(target, except);
      while (head != null && Drop(head, except))
        head = head.Next;
      for (var entry = head; entry != null; entry = entry.Next)
      {
        while (entry.Next != null && Drop(entry.Next, except))
          entry.Next = entry.Next.Next;
      }
      return head;
    }

    private static bool LoggedFailure;

    internal static void Failed(Exception e)
    {
      if (LoggedFailure)
        return;
      LoggedFailure = true;
      LogError($"Biome precision: sampling a terrain zone failed ({e.Message}); zones that fail keep vanilla's corner biomes.");
    }
  }
}

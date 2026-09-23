// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

// The offline test program in tools/altbiome-harness checks these internals directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AltBiomeHarness")]

namespace BetterContinents;

public partial class BetterContinents
{
  // Thrown when planting fails: the world load stops instead of generating zones with the wrong alt biomes.
  public sealed class AltBiomeLoadException(string message, Exception? inner) : Exception(message, inner);

  // Valheim 1.0 decides alt biomes per BiomeSector: a 4-connected region of one biome on a grid of 12 m points
  // (AshLands, DeepNorth and Ocean are one world-wide sector each). Every consumer - terrain corner biomes,
  // vegetation, spawns, locations, environments, music - reads the result through WorldGenerator.GetBiomeSector
  // or Heightmap.m_cornerAltBiomes. This class makes that pipeline correct on Better Continents worlds, gives
  // authors and server owners control over it, and plants alt biomes where a planting layer says so. See
  // ALTBIOMES.md.
  public static class AltBiomeControl
  {
    public const int VanillaGridSize = 2048;
    public const float VanillaPixelSize = 12f;
    public const float VanillaSampleRadius = 10500f;
    // AltBiomeWorldData.MapSpaceToWorldSpace(0) on an unmodified 1.0.15 grid: (0 - 1024) * 12 + 6.
    private const float VanillaGridOrigin = -12282f;

    public enum RebuildLevel
    {
      // Re-run alt-biome placement on the existing sectors (alt-biome options changed).
      Assignment,
      // Rebuild sectors from the existing point grid (planting or mode changed).
      Sectors,
      // Resample the point grid (maps, heights or grid options changed).
      Points,
    }

    // Integration point for the planting layer: called on every Configure() with the active settings. The
    // default plants from the world's baked alt-biome map.
    public static Func<BetterContinentsSettings, IAltBiomePlanting?>? PlantingProvider = DefaultPlanting;
    public static IAltBiomePlanting? Planting { get; private set; }

    private static IAltBiomePlanting? DefaultPlanting(BetterContinentsSettings settings) =>
      settings.AltBiomeMapData is { } map ? ColourPlanting.For(map) : null;

    // ---- configuration (refreshed by Configure) ----
    internal static bool WorldEnabled;
    internal static AltBiomeSettings Active = AltBiomeSettings.Legacy;
    internal static bool ExternalGrid;
    internal static float CutoffRadius;
    internal static float SampledRadius = VanillaSampleRadius;
    internal static bool FallbackActive;
    internal static float FallbackRadiusSq = VanillaSampleRadius * VanillaSampleRadius;
    internal static bool CustomEligibility;

    // Called at the end of DynamicPatch (settings loaded or changed) and again right before the grid is
    // verified at world load, because Expand World Size applies its grid transpilers per world.
    public static void Configure()
    {
      WorldEnabled = Settings.EnabledForThisWorld;
      Active = Settings.EffectiveAltBiomes;
      ExternalGrid = DetectExternalGrid();
      CutoffRadius = 0f;
      SampledRadius = ExternalGrid ? TotalRadius : VanillaSampleRadius;
      FallbackActive = false;
      CustomEligibility = false;
      Planting = null;
      if (!WorldEnabled)
        return;
      if (!ExternalGrid)
      {
        // Better Continents alone never moves its maps: TotalRadius only changes when Expand World Size calls
        // SetSize, in which case EWS owns the grid (ExternalGrid). So here the content always spans the vanilla
        // 10500 m disc, and "World Size" only moves the edge of the world. When that edge is inside the disc,
        // WorldEdge stops sampling at it. With the drop-off disabled there is no edge to stop at.
        var edge = Settings.WorldSize + Settings.EdgeSize;
        if (Active.Grid == AltBiomeGridMode.WorldEdge && !Settings.DisableMapEdgeDropoff && edge > 0f && edge < VanillaSampleRadius)
          CutoffRadius = edge;
        SampledRadius = CutoffRadius > 0f ? CutoffRadius : VanillaSampleRadius;
        // Past the sampled disc every grid point is forced to Ocean. That is right while the terrain is ocean
        // there too; it is wrong beyond a WorldEdge cut-off and on a world without the edge drop-off, where
        // BC terrain can continue. There GetBiomeSector falls back to the real biome, without alt biomes.
        FallbackActive = CutoffRadius > 0f || Settings.DisableMapEdgeDropoff;
        FallbackRadiusSq = SampledRadius * SampledRadius;
      }
      CustomEligibility = Active.FixNeighbourCheck || Active.MeanSectorHeight || Active.MinSectorThickness > 0f;
      try
      {
        Planting = PlantingProvider?.Invoke(Settings);
      }
      catch (Exception e)
      {
        LogError($"Alt biomes: the planting provider failed: {e.Message}");
        Planting = null;
      }
    }

    // Expand World Size retargets MapSpaceToWorldSpace / WorldSpaceToMapSpace / GenerateBiomePoints itself and
    // pushes its radius into BC through SetSize. When either is visible, the grid geometry is not ours.
    private static bool DetectExternalGrid()
    {
      if (TotalRadius != VanillaSampleRadius)
        return true;
      try
      {
        var p0 = AltBiomeWorldData.MapSpaceToWorldSpace(0f);
        var p1 = AltBiomeWorldData.MapSpaceToWorldSpace(1f);
        return p0 != VanillaGridOrigin || p1 - p0 != VanillaPixelSize;
      }
      catch
      {
        return true;
      }
    }

    // Everything about the grid geometry that shapes the point grid: the cache fingerprint includes it.
    internal static string GridProbe()
    {
      var p0 = AltBiomeWorldData.MapSpaceToWorldSpace(0f);
      var p1 = AltBiomeWorldData.MapSpaceToWorldSpace(1f);
      return string.Format(CultureInfo.InvariantCulture, "{0:R}/{1:R}/{2:R}", p0, p1, CutoffRadius);
    }

    internal static float GridPixelSize() =>
      AltBiomeWorldData.MapSpaceToWorldSpace(1f) - AltBiomeWorldData.MapSpaceToWorldSpace(0f);

    // ---------------------------------------------------------------------------------------------------
    // World load: VerifyBiomeData prefix and postfix. The cache itself is validated in BiomeCachePatch.
    // ---------------------------------------------------------------------------------------------------
    internal static void BeforeVerifyBiomeData(World world)
    {
      Configure();
      Warned.Clear();
      LastFailure = null;
      // A planted map the settings could not read would silently plant nothing: that is a planting error too.
      if (WorldEnabled && Active.Mode != AltBiomeMode.Off && Settings.AltBiomeMapError != null)
        throw FailLoad("reading the alt-biome map", new System.IO.InvalidDataException(Settings.AltBiomeMapError));
    }

    internal static void AfterVerifyBiomeData(World world) => OnBiomeDataReady(world, "world load");

    // ---------------------------------------------------------------------------------------------------
    // WorldEdge cut-off: applied after GenerateBiomePoints, exactly as vanilla marks points outside its disc.
    // ---------------------------------------------------------------------------------------------------
    internal static void ApplyCutoff(AltBiomeWorldData data)
    {
      var r = CutoffRadius;
      if (r <= 0f)
        return;
      float r2 = r * r;
      var ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
      int size = data.Size;
      int cut = 0;
      for (int i = 0; i < size; i++)
      {
        float y = AltBiomeWorldData.MapSpaceToWorldSpace(i);
        for (int j = 0; j < size; j++)
        {
          float x = AltBiomeWorldData.MapSpaceToWorldSpace(j);
          if (x * x + y * y > r2 && data.PointHeights[j, i] != -1000f)
          {
            data.PointBiomes[j, i] = ocean;
            data.PointHeights[j, i] = -1000f;
            cut++;
          }
        }
      }
      Log($"Alt biomes: grid stops at the world edge ({r:0} m); {cut} grid points beyond it marked as ocean.");
    }

    // Plain sectors (no alt biomes) for positions the grid does not describe. Shared with the biome
    // precision code in HeightmapPatch.
    internal static BiomeSector PlainSector(Heightmap.Biome biome) => PlainSectors[ImageMapBiome.ToSafeIndex(biome)];

    internal static bool IsGlobal(Heightmap.Biome biome) =>
      biome == Heightmap.Biome.AshLands || biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Ocean;

    // Vanilla's GetBiomeSector(int, int) (WorldGenerator.cs:845-875) with the clamp at the grid's real size
    // instead of the literal 2047: a grid Expand World Size resized either throws (smaller) or maps every
    // lookup past index 2047 onto the wrong column or row (bigger).
    internal static BiomeSector SectorAtGrid(AltBiomeWorldData data, int gridx, int gridy)
    {
      if (!data.IsReady)
        return BiomeSector.EmptyMeadows;
      int maxX = Math.Min(data.Size, data.PointSectors.GetLength(0)) - 1;
      int maxY = Math.Min(data.Size, data.PointSectors.GetLength(1)) - 1;
      return data.PointSectors[Mathf.Clamp(gridx, 0, maxX), Mathf.Clamp(gridy, 0, maxY)] ?? BiomeSector.EmptyMeadows;
    }

    // ---------------------------------------------------------------------------------------------------
    // Per-sector facts vanilla does not keep: area, a stable id (first grid point in scan order), the mean
    // height over the whole region, and what was planted on it.
    // ---------------------------------------------------------------------------------------------------
    internal sealed class SectorInfo
    {
      public int Id = -1;
      public int Area;
      public int SeedX = -1;
      public int SeedY = -1;
      public double HeightSum;
      // The painted key the region was split out for (0 = none).
      public int PlantKey;
      // Keys of point plants that landed on the region.
      public List<int>? PointKeys;
      public bool Planted => PlantKey != 0 || PointKeys != null;
      // Vanilla's HeightAvg is (min + max) / 2 over the sector's EDGE points only (AltBiomeWorldData.cs:233-255).
      public float MeanHeight => Area > 0 ? (float)(HeightSum / Area) : 0f;
      public float Thickness(BiomeSector sector) => sector.EdgeCount > 0 ? (float)Area / sector.EdgeCount : Area;
    }

    internal static readonly Dictionary<BiomeSector, SectorInfo> Info = [];
    private static AltBiomeWorldData? InfoFor;
    private static int InfoSectorCount;

    internal static SectorInfo GetInfo(BiomeSector sector) =>
      Info.TryGetValue(sector, out var info) ? info : new SectorInfo();

    internal static void EnsureSectorInfo(AltBiomeWorldData data)
    {
      if (InfoFor != data || InfoSectorCount != data.Sectors.Count)
      {
        Info.Clear();
        for (int i = 0; i < data.Sectors.Count; i++)
          Info[data.Sectors[i]] = new SectorInfo { Id = i };
        int size = data.Size;
        BiomeSector? last = null;
        SectorInfo? lastInfo = null;
        // Scan order matches GenerateSectors' creation scan (y outer, x inner), so a flood-filled sector's
        // seed is the point it was created from.
        for (int y = 0; y < size; y++)
        {
          for (int x = 0; x < size; x++)
          {
            var s = data.PointSectors[x, y];
            if (s == null)
              continue;
            if (!ReferenceEquals(s, last))
            {
              last = s;
              if (!Info.TryGetValue(s, out lastInfo))
                Info[s] = lastInfo = new SectorInfo();
            }
            lastInfo!.Area++;
            lastInfo.HeightSum += data.PointHeights[x, y];
            if (lastInfo.SeedX < 0)
            {
              lastInfo.SeedX = x;
              lastInfo.SeedY = y;
            }
          }
        }
        InfoFor = data;
        InfoSectorCount = data.Sectors.Count;
      }
      // Planting changes between runs without changing the sectors (point plants are resolved per run).
      foreach (var info in Info.Values)
      {
        info.PlantKey = 0;
        info.PointKeys = null;
      }
      if (PartitionFor == data)
        foreach (var kv in PartitionKeys)
          if (Info.TryGetValue(kv.Key, out var info))
            info.PlantKey = kv.Value;
      if (PinnedFor == data)
        foreach (var kv in Pinned)
          if (Info.TryGetValue(kv.Key, out var info))
            info.PointKeys = kv.Value;
    }

    // ---------------------------------------------------------------------------------------------------
    // Planted regions. When the planting layer has painted pixels, GenerateSectors is replaced by:
    //   1. a faithful copy of vanilla's flood fill (AltBiomeWorldData.cs:150-200), so every unplanted point keeps
    //      exactly the region object, list position and point lists vanilla would give it;
    //   2. splitting: each 4-connected patch of one key inside one vanilla region becomes a region of its own;
    //      on AshLands, DeepNorth and Ocean all patches of one key form one region. Nothing is ever merged. A
    //      vanilla region that ends up fully planted is reused for its first planted patch instead of being
    //      left behind empty;
    //   3. a faithful copy of vanilla's statistics pass (:202-293), run once over the final regions.
    // With no planted point this is vanilla's GenerateSectors exactly (the harness compares them).
    // ---------------------------------------------------------------------------------------------------
    private static AltBiomeWorldData? PartitionFor;
    // Key of every planted region: split-out ones and reused fully planted vanilla ones.
    internal static readonly Dictionary<BiomeSector, int> PartitionKeys = [];
    // Split-out regions: sectors vanilla's own partition does not have. Random placement never sees them.
    internal static readonly HashSet<BiomeSector> SplitOut = [];
    internal static readonly Dictionary<int, int> PlantedPointsByKey = [];
    internal static readonly Dictionary<int, int> IgnoredPointsByKey = [];
    internal static int PlantedPointCount;
    internal static string LastPartitionSummary = "";

    private static void ResetPartition(AltBiomeWorldData? data)
    {
      PartitionFor = data;
      PartitionKeys.Clear();
      SplitOut.Clear();
      PlantedPointsByKey.Clear();
      IgnoredPointsByKey.Clear();
      PlantedPointCount = 0;
      LastPartitionSummary = "";
      InfoFor = null;
    }

    internal static bool TryGeneratePlantedSectors(AltBiomeWorldData data)
    {
      if (!TryBuildPlantedSectors(data))
        return false;
      // Outside the build on purpose: a placement failure must surface exactly as it would from vanilla's own
      // GenerateSectors, not be retried on a second, unplanted set of sectors.
      data.GenerateAltBiomes();
      return true;
    }

    // Everything the GenerateSectors replacement does before its final GenerateAltBiomes() call. False = no
    // planted point: vanilla's own GenerateSectors runs.
    internal static bool TryBuildPlantedSectors(AltBiomeWorldData data)
    {
      ResetPartition(data);
      var planting = Planting;
      if (!WorldEnabled || Active.Mode == AltBiomeMode.Off || planting == null || !planting.HasPaintedRegions)
        return false;
      int[] keys;
      try
      {
        planting.Prepare();
        keys = SampleKeys(data, planting);
      }
      catch (Exception e) when (e is not AltBiomeLoadException)
      {
        throw FailLoad("sampling the alt-biome map", e);
      }
      if (PlantedPointCount == 0)
      {
        LastPartitionSummary = "no painted pixel lands on land its colour can change";
        return false;
      }
      try
      {
        BuildSectors(data, keys);
      }
      catch (Exception e) when (e is not AltBiomeLoadException)
      {
        throw FailLoad("splitting out the planted regions", e);
      }
      return true;
    }

    // Plant key per grid point, in the same row-major layout as the flood fill (y * size + x). A painted point
    // whose key does not take over its biome is stored as -key: unplanted, but counted for the report.
    internal static int[] SampleKeys(AltBiomeWorldData data, IAltBiomePlanting planting)
    {
      int size = data.Size;
      var keys = new int[size * size];
      float total = TotalSize;
      GameUtils.SimpleParallelFor(4, 0, size, y =>
      {
        float mapY = AltBiomeWorldData.MapSpaceToWorldSpace(y) / total + 0.5f;
        for (int x = 0; x < size; x++)
        {
          // Points outside the sampled disc stay part of the one outside-the-world ocean.
          if (data.PointHeights[x, y] == -1000f)
            continue;
          int key = planting.GetPlantKey(AltBiomeWorldData.MapSpaceToWorldSpace(x) / total + 0.5f, mapY);
          if (key == 0)
            continue;
          keys[y * size + x] = planting.Claims(key, data.PointBiomes[x, y].ToBiome()) ? key : -key;
        }
      });
      for (int i = 0; i < keys.Length; i++)
      {
        int k = keys[i];
        if (k > 0)
        {
          PlantedPointCount++;
          PlantedPointsByKey[k] = PlantedPointsByKey.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        else if (k < 0)
          IgnoredPointsByKey[-k] = IgnoredPointsByKey.TryGetValue(-k, out var n) ? n + 1 : 1;
      }
      return keys;
    }

    private static void AddPoint(AltBiomeWorldData data, BiomeSector sector, short x, short y)
    {
      var type = data.Biomes[sector.Biome];
      type.AllPoints.Add(new BiomePointCoordinate(x, y));
      if (data.PointHeights[x, y] >= 30f)
        type.AllPointsAboveSeaLevel.Add(new BiomePointCoordinate(x, y));
    }

    private static void TryFill(AltBiomeWorldData data, bool[,] visited, Stack<BiomePointCoordinate> open,
      Heightmap.BiomeIndex biome, BiomeSector sector, short x, short y)
    {
      int size = data.Size;
      if (x >= 0 && y >= 0 && x < size && y < size && !visited[x, y] && data.PointBiomes[x, y] == biome)
      {
        visited[x, y] = true;
        data.PointSectors[x, y] = sector;
        AddPoint(data, sector, x, y);
        open.Push(new BiomePointCoordinate(x, y));
      }
    }

    // Everything GenerateSectors does before its final GenerateAltBiomes() call, with planted regions split out.
    internal static void BuildSectors(AltBiomeWorldData data, int[] keys)
    {
      int size = data.Size;

      // 1. Vanilla's flood fill (AltBiomeWorldData.cs:150-200), keys ignored.
      var open = new Stack<BiomePointCoordinate>(1024);
      var visited = new bool[size, size];
      var global = new Dictionary<Heightmap.BiomeIndex, BiomeSector>(3);
      // Global sectors first, as in vanilla: EnvMan reads Biomes[AshLands / DeepNorth].Sectors[0] (EnvMan.cs:705).
      data.Sectors.Add(new BiomeSector(data, Heightmap.Biome.AshLands));
      global.Add(Heightmap.BiomeIndex.AshLands, data.Sectors[data.Sectors.Count - 1]);
      data.Sectors.Add(new BiomeSector(data, Heightmap.Biome.DeepNorth));
      global.Add(Heightmap.BiomeIndex.DeepNorth, data.Sectors[data.Sectors.Count - 1]);
      data.Sectors.Add(new BiomeSector(data, Heightmap.Biome.Ocean));
      global.Add(Heightmap.BiomeIndex.Ocean, data.Sectors[data.Sectors.Count - 1]);
      for (short y = 0; y < size; y++)
      {
        for (short x = 0; x < size; x++)
        {
          if (global.TryGetValue(data.PointBiomes[x, y], out var g))
          {
            data.PointSectors[x, y] = g;
            visited[x, y] = true;
            AddPoint(data, g, x, y);
          }
        }
      }
      for (short y = 0; y < size; y++)
      {
        for (short x = 0; x < size; x++)
        {
          if (visited[x, y])
            continue;
          visited[x, y] = true;
          var sector = new BiomeSector(data, data.PointBiomes[x, y].ToBiome());
          data.Sectors.Add(sector);
          data.PointSectors[x, y] = sector;
          // Vanilla does not add a sector's seed point to AllPoints either; kept for parity.
          open.Push(new BiomePointCoordinate(x, y));
          while (open.Count > 0)
          {
            var p = open.Pop();
            var pBiome = data.PointBiomes[p.x, p.y];
            var pSector = data.PointSectors[p.x, p.y];
            TryFill(data, visited, open, pBiome, pSector, (short)(p.x + 1), p.y);
            TryFill(data, visited, open, pBiome, pSector, (short)(p.x - 1), p.y);
            TryFill(data, visited, open, pBiome, pSector, p.x, (short)(p.y + 1));
            TryFill(data, visited, open, pBiome, pSector, p.x, (short)(p.y - 1));
          }
        }
      }

      // 2. Split the planted patches out of the vanilla regions they fell in.
      SplitPlanted(data, keys);

      // 3. Vanilla's statistics pass (AltBiomeWorldData.cs:202-293).
      ComputeStats(data);
    }

    internal static void SplitPlanted(AltBiomeWorldData data, int[] keys)
    {
      int size = data.Size;
      var total = new Dictionary<BiomeSector, int>();
      var planted = new Dictionary<BiomeSector, int>();
      for (int y = 0; y < size; y++)
      {
        for (int x = 0; x < size; x++)
        {
          var s = data.PointSectors[x, y];
          total[s] = total.TryGetValue(s, out var t) ? t + 1 : 1;
          if (keys[y * size + x] > 0)
            planted[s] = planted.TryGetValue(s, out var p) ? p + 1 : 1;
        }
      }
      if (planted.Count == 0)
        return;
      // A vanilla region with no unplanted point left is reused for its first planted patch, so no empty region
      // is left behind and the region keeps its place in the game's lists.
      var reusable = new HashSet<BiomeSector>(planted.Where(kv => kv.Value == total[kv.Key]).Select(kv => kv.Key));
      int touched = planted.Count, reused = 0;
      var done = new bool[size, size];
      var globals = new Dictionary<(Heightmap.Biome, int), BiomeSector>();
      var stack = new Stack<BiomePointCoordinate>(1024);

      BiomeSector Claim(BiomeSector vanilla, int key)
      {
        if (reusable.Remove(vanilla))
        {
          PartitionKeys[vanilla] = key;
          reused++;
          return vanilla;
        }
        // The constructor also appends the sector to data.Biomes[biome].Sectors, after vanilla's own, which keeps
        // Biomes[AshLands/DeepNorth].Sectors[0] - the sector EnvMan substitutes at sea level - the vanilla one.
        var t = new BiomeSector(data, vanilla.Biome);
        data.Sectors.Add(t);
        SplitOut.Add(t);
        PartitionKeys[t] = key;
        return t;
      }

      for (short y = 0; y < size; y++)
      {
        for (short x = 0; x < size; x++)
        {
          int key = keys[y * size + x];
          if (key <= 0 || done[x, y])
            continue;
          var s = data.PointSectors[x, y];
          if (IsGlobal(s.Biome))
          {
            if (!globals.TryGetValue((s.Biome, key), out var g))
              globals.Add((s.Biome, key), g = Claim(s, key));
            data.PointSectors[x, y] = g;
            done[x, y] = true;
            continue;
          }
          var target = Claim(s, key);
          data.PointSectors[x, y] = target;
          done[x, y] = true;
          stack.Push(new BiomePointCoordinate(x, y));
          while (stack.Count > 0)
          {
            var pt = stack.Pop();
            Carve(pt.x + 1, pt.y);
            Carve(pt.x - 1, pt.y);
            Carve(pt.x, pt.y + 1);
            Carve(pt.x, pt.y - 1);
          }

          void Carve(int nx, int ny)
          {
            if (nx < 0 || ny < 0 || nx >= size || ny >= size || done[nx, ny])
              return;
            if (keys[ny * size + nx] != key || data.PointSectors[nx, ny] != s)
              return;
            data.PointSectors[nx, ny] = target;
            done[nx, ny] = true;
            stack.Push(new BiomePointCoordinate((short)nx, (short)ny));
          }
        }
      }
      LastPartitionSummary = $"split {PartitionKeys.Count} planted region(s) out of {touched} region(s) ({reused} fully planted and reused), {PlantedPointCount} of {size * size} grid points planted";
      Log($"Alt biomes: {LastPartitionSummary}.");
    }

    // Vanilla's statistics, line for line, including its quirks: Min starts at (0,0) and so never moves, both
    // zones are computed from Min, only the first differing neighbour of an edge point is recorded.
    internal static void ComputeStats(AltBiomeWorldData data)
    {
      int size = data.Size;
      for (int i = 1; i < size - 1; i++)
      {
        for (int j = 1; j < size - 1; j++)
        {
          float h = data.PointHeights[j, i];
          var s = data.PointSectors[j, i];
          BiomeSector item;
          if ((item = data.PointSectors[j - 1, i]) != s || (item = data.PointSectors[j + 1, i]) != s || (item = data.PointSectors[j, i - 1]) != s || (item = data.PointSectors[j, i + 1]) != s)
          {
            s.EdgeCount++;
            s.Center += new Vector2(j, i);
            if (j < s.Min.x) s.Min.x = j;
            if (j > s.Max.x) s.Max.x = j;
            if (i < s.Min.y) s.Min.y = i;
            if (i > s.Max.y) s.Max.y = i;
            if (!s.Neighbors.Contains(item))
              s.Neighbors.Add(item);
            if (h < s.HeightMin) s.HeightMin = h;
            if (h > s.HeightMax) s.HeightMax = h;
          }
        }
      }
      foreach (var biome in data.Biomes)
      {
        foreach (var s in biome.Value.Sectors)
        {
          if (s.EdgeCount > 0)
          {
            s.Center = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(s.Center.x / s.EdgeCount), AltBiomeWorldData.MapSpaceToWorldSpace(s.Center.y / s.EdgeCount));
            s.Min = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(s.Min.x), AltBiomeWorldData.MapSpaceToWorldSpace(s.Min.y));
            s.Max = new Vector2(AltBiomeWorldData.MapSpaceToWorldSpace(s.Max.x), AltBiomeWorldData.MapSpaceToWorldSpace(s.Max.y));
            // Vanilla computes both zones from Min and drops the second coordinate (AltBiomeWorldData.cs:253-254);
            // kept identical so planted and unplanted worlds report the same fields.
            s.MinZone = ZoneSystem.GetZone(new Vector3(s.Min.x, s.Min.y));
            s.MaxZone = ZoneSystem.GetZone(new Vector3(s.Min.x, s.Min.y));
            s.HeightAvg = (s.HeightMin + s.HeightMax) / 2f;
          }
        }
      }
      data.SectorsCalculated = true;
      // Vanilla's IsDiscovered pass (:261-285) never iterates because MaxZone == MinZone, so it is omitted.
      foreach (var biome in data.Biomes)
        foreach (var s in biome.Value.Sectors)
          s.DistanceFromCenter = Vector2.Distance(s.Center, Vector2.zero);
    }

    // ---------------------------------------------------------------------------------------------------
    // Placement around vanilla GenerateAltBiomes: planted alt biomes first (so they count toward the game's
    // per-alt-biome minimum and maximum), then vanilla's random placement under the world's options, on
    // unplanted regions only.
    // ---------------------------------------------------------------------------------------------------
    internal struct AltBiomeParams
    {
      public bool Enabled;
      public float Chance;
      public int Min;
      public int Max;
      public float MinDist;
      public int MinEdge;
      public int MaxEdge;
      public float MinH;
      public float MaxH;
      public float BelowX;
      public float AboveX;
      public float BelowY;
      public float AboveY;

      public static AltBiomeParams From(AltBiome a) => new()
      {
        Enabled = a.m_enabled,
        Chance = a.m_chance,
        Min = a.m_minAmountSpawned,
        Max = a.m_maxAmountSpawned,
        MinDist = a.m_minDistanceFromCenter,
        MinEdge = a.m_minEdgeSize,
        MaxEdge = a.m_maxEdgeSize,
        MinH = a.m_minAvgHeight,
        MaxH = a.m_maxAvgHeight,
        BelowX = a.m_belowWorldX,
        AboveX = a.m_aboveWorldX,
        BelowY = a.m_belowWorldY,
        AboveY = a.m_aboveWorldY,
      };

      public readonly void ApplyTo(AltBiome a)
      {
        a.m_enabled = Enabled;
        a.m_chance = Chance;
        a.m_minAmountSpawned = Min;
        a.m_maxAmountSpawned = Max;
        a.m_minDistanceFromCenter = MinDist;
        a.m_minEdgeSize = MinEdge;
        a.m_maxEdgeSize = MaxEdge;
        a.m_minAvgHeight = MinH;
        a.m_maxAvgHeight = MaxH;
        a.m_belowWorldX = BelowX;
        a.m_aboveWorldX = AboveX;
        a.m_belowWorldY = BelowY;
        a.m_aboveWorldY = AboveY;
      }

      public readonly bool SameAs(AltBiomeParams o) =>
        Enabled == o.Enabled && Chance == o.Chance && Min == o.Min && Max == o.Max && MinDist == o.MinDist
        && MinEdge == o.MinEdge && MaxEdge == o.MaxEdge && MinH == o.MinH && MaxH == o.MaxH
        && BelowX == o.BelowX && AboveX == o.AboveX && BelowY == o.BelowY && AboveY == o.AboveY;
    }

    internal sealed class AltBiomeRun
    {
      public bool RunVanilla = true;
      public bool RestoreOrderAfter;
      public readonly List<KeyValuePair<AltBiome, AltBiomeParams>> Changed = [];

      public void RestoreOverrides()
      {
        foreach (var kv in Changed)
          kv.Value.ApplyTo(kv.Key);
        Changed.Clear();
      }
    }

    // What the last run used, for reports.
    internal static readonly Dictionary<AltBiome, AltBiomeParams> LastVanilla = [];
    internal static readonly Dictionary<AltBiome, AltBiomeParams> LastEffective = [];
    internal static readonly List<string> LastPlantingNotes = [];
    // Every region that carries planting this run (split out, reused or under a point plant).
    internal static readonly HashSet<BiomeSector> PlantedSectors = [];
    // Point plants, resolved per run against the final regions.
    private static AltBiomeWorldData? PinnedFor;
    internal static readonly Dictionary<BiomeSector, List<int>> Pinned = [];
    // While vanilla's random placement runs: regions it must not give anything to.
    internal static bool RandomPhase;
    internal static readonly HashSet<BiomeSector> RandomExcluded = [];
    // Warnings given once per world load.
    private static readonly HashSet<string> Warned = [];

    private static void WarnOnce(string key, string message)
    {
      if (Warned.Add(key))
        LogWarning(message);
    }

    internal static AltBiomeRun BeginRun(AltBiomeWorldData data)
    {
      var run = new AltBiomeRun();
      RandomPhase = false;
      RandomExcluded.Clear();
      // Vanilla never clears these, so a second run (vanilla's own `genloc alt`, or a BC rebuild) would stack a
      // second set of modifiers on top of the first and count the first set toward every limit.
      ClearAssignments(data);
      // Vanilla shuffles each biome's sector list in place, so a second run would start from the first run's
      // permutation. Restoring creation order makes every run equal to the one a fresh world load performs.
      RestoreCreationOrder(data);
      LastVanilla.Clear();
      LastEffective.Clear();
      LastPlantingNotes.Clear();
      PlantedSectors.Clear();
      Pinned.Clear();
      PinnedFor = data;
      foreach (var alt in AltBiomeList.m_altBiomes)
        LastVanilla[alt] = AltBiomeParams.From(alt);
      if (PartitionFor != data)
        ResetPartition(data);
      if (Active.Mode == AltBiomeMode.Off)
      {
        EnsureSectorInfo(data);
        run.RunVanilla = false;
        return run;
      }
      var planting = Planting;
      if (planting != null && planting.HasPlanting)
      {
        try
        {
          planting.Prepare();
          ResolvePoints(data, planting);
          ApplyPlanted(data, planting);
        }
        catch (Exception e) when (e is not AltBiomeLoadException)
        {
          throw FailLoad("planting the alt biomes", e);
        }
      }
      EnsureSectorInfo(data);
      if (Active.Mode == AltBiomeMode.PlantedOnly)
      {
        run.RunVanilla = false;
        return run;
      }
      if (PlantedSectors.Count > 0)
      {
        // Split-out regions are not in vanilla's partition: hiding them makes the game shuffle exactly the lists
        // it would have shuffled without planting, so random placement elsewhere stays what it would have been.
        // Reused and point-planted regions stay in the lists and are refused by the CanAddModifier gate instead.
        if (SplitOut.Count > 0)
        {
          foreach (var type in data.Biomes.Values)
            type.Sectors.RemoveAll(SplitOut.Contains);
          run.RestoreOrderAfter = true;
        }
        RandomExcluded.UnionWith(PlantedSectors);
      }
      RandomPhase = true;
      ApplyOverrides(run);
      return run;
    }

    internal static void EndRun(AltBiomeWorldData data, AltBiomeRun run)
    {
      RandomPhase = false;
      RandomExcluded.Clear();
      run.RestoreOverrides();
      if (run.RestoreOrderAfter)
        RestoreCreationOrder(data);
      try
      {
        ComputeHashes(data);
      }
      catch (Exception e)
      {
        LogWarning($"Alt biomes: could not compute the agreement hashes: {e.Message}");
      }
    }

    internal static void ClearAssignments(AltBiomeWorldData data)
    {
      foreach (var alt in AltBiomeList.m_altBiomes)
        alt.Sectors.Clear();
      foreach (var s in data.Sectors)
        s.AltBiomes.Clear();
    }

    private static void RestoreCreationOrder(AltBiomeWorldData data)
    {
      var byType = new Dictionary<BiomeTypeInfo, List<BiomeSector>>();
      foreach (var s in data.Sectors)
      {
        if (!byType.TryGetValue(s.BiomeType, out var list))
          byType[s.BiomeType] = list = [];
        list.Add(s);
      }
      foreach (var type in data.Biomes.Values)
      {
        type.Sectors.Clear();
        if (byType.TryGetValue(type, out var list))
          type.Sectors.AddRange(list);
      }
    }

    // Point plants apply to the whole region under their position, after splitting: they can add to a planted
    // region or claim an unplanted one.
    private static void ResolvePoints(AltBiomeWorldData data, IAltBiomePlanting planting)
    {
      foreach (var p in planting.Points)
      {
        var where = $"{p.X.ToString("0", CultureInfo.InvariantCulture)}, {p.Z.ToString("0", CultureInfo.InvariantCulture)}";
        int gx = AltBiomeWorldData.WorldSpaceToMapSpace(p.X);
        int gy = AltBiomeWorldData.WorldSpaceToMapSpace(p.Z);
        if (gx < 0 || gy < 0 || gx >= data.Size || gy >= data.Size || data.PointHeights[gx, gy] == -1000f)
        {
          WarnOnce("pointout:" + where, $"Alt biomes: the point plant at {where} ({planting.DescribeKey(p.Key)}) is outside the sampled world; it is ignored.");
          continue;
        }
        var sector = data.PointSectors[gx, gy];
        if (sector == null)
          continue;
        if (!planting.Claims(p.Key, sector.Biome))
        {
          WarnOnce("point:" + where, $"Alt biomes: the point plant at {where} ({planting.DescribeKey(p.Key)}) lands on {sector.Biome}, which none of its alt biomes can change; it is ignored.");
          continue;
        }
        if (IsGlobal(sector.Biome) && !PartitionKeys.ContainsKey(sector))
          WarnOnce("pointglobal:" + where, $"Alt biomes: the point plant at {where} ({planting.DescribeKey(p.Key)}) lands on {sector.Biome}, which the game treats as ONE world-wide region: it applies to all unplanted {sector.Biome}. Paint the area instead to plant only part of it.");
        if (!Pinned.TryGetValue(sector, out var list))
          Pinned[sector] = list = [];
        if (!list.Contains(p.Key))
          list.Add(p.Key);
      }
    }

    private static AltBiome? FindIncompatible(BiomeSector sector, AltBiome mod)
    {
      foreach (var a in sector.AltBiomes)
      {
        if ((a.m_incompatibleAltBiomes?.Contains(mod.m_name) ?? false) || (mod.m_incompatibleAltBiomes?.Contains(a.m_name) ?? false))
          return a;
      }
      return null;
    }

    // Planting ignores every OCCURRENCE rule of the game (edge size, distance from centre, average height, world
    // position, neighbours, chance): the author has chosen the place. It keeps the three CONTENT rules by default
    // - disabled, not one of this alt biome's base biomes, incompatible with one already on the region - because
    // breaking those is almost always a mistake; "!Name" in the legend overrides all three.
    private static void ApplyPlanted(AltBiomeWorldData data, IAltBiomePlanting planting)
    {
      int applied = 0, protectedRegions = 0, mismatched = 0, refused = 0;
      foreach (var s in data.Sectors)
      {
        int colourKey = PartitionFor == data && PartitionKeys.TryGetValue(s, out var k) ? k : 0;
        Pinned.TryGetValue(s, out var pointKeys);
        if (colourKey == 0 && pointKeys == null)
          continue;
        PlantedSectors.Add(s);
        bool anything = false;
        void Plant(int key)
        {
          var mods = planting.GetAltBiomes(key);
          if (mods.Count == 0)
            return;
          anything = true;
          foreach (var entry in mods)
          {
            var mod = entry.AltBiome;
            if (s.AltBiomes.Contains(mod))
              continue;
            if (!entry.Force)
            {
              if (!mod.m_enabled)
              {
                refused++;
                WarnOnce($"disabled:{mod.m_name}", $"Alt biomes: '{mod.m_name}' ({planting.DescribeKey(key)}) is disabled in this game and is not planted; write !{mod.m_name} to force it.");
                continue;
              }
              if ((mod.m_biome & s.Biome) == 0)
              {
                mismatched++;
                continue;
              }
              var clash = FindIncompatible(s, mod);
              if (clash != null)
              {
                refused++;
                WarnOnce($"clash:{mod.m_name}:{clash.m_name}",
                  $"Alt biomes: '{mod.m_name}' ({planting.DescribeKey(key)}) is incompatible with '{clash.m_name}' already on the same region, so it is not planted there; write !{mod.m_name} to force it.");
                continue;
              }
            }
            s.AddModifier(mod);
            applied++;
          }
        }
        if (colourKey != 0)
          Plant(colourKey);
        if (pointKeys != null)
          foreach (var key in pointKeys)
            Plant(key);
        if (!anything)
          protectedRegions++;
      }
      LastPlantingNotes.Add($"planted: {PlantedSectors.Count} region(s), {applied} alt biome(s) applied, {protectedRegions} protected (none), {mismatched} skipped for the base biome, {refused} refused (disabled or incompatible)");
    }

    private static void ApplyOverrides(AltBiomeRun run)
    {
      var s = Active;
      foreach (var alt in AltBiomeList.m_altBiomes)
      {
        var orig = LastVanilla.TryGetValue(alt, out var v) ? v : AltBiomeParams.From(alt);
        var o = s.Resolve(alt.m_name ?? "");
        var eff = orig;
        eff.Enabled = o?.Enabled ?? orig.Enabled;
        eff.Chance = o?.Chance ?? Mathf.Clamp01(orig.Chance * s.ChanceMultiplier);
        eff.Min = o?.MinAmount ?? Mathf.RoundToInt(orig.Min * s.AmountMultiplier);
        eff.Max = o?.MaxAmount ?? Mathf.RoundToInt(orig.Max * s.AmountMultiplier);
        if (eff.Max < eff.Min)
          eff.Max = eff.Min;
        eff.MinDist = o?.MinDistanceFromCenter ?? orig.MinDist * s.DistanceScale;
        eff.MinEdge = o?.MinEdgeSize ?? Mathf.RoundToInt(orig.MinEdge * s.EdgeScale);
        eff.MaxEdge = o?.MaxEdgeSize ?? Mathf.RoundToInt(orig.MaxEdge * s.EdgeScale);
        eff.MinH = o?.MinAvgHeight ?? orig.MinH;
        eff.MaxH = o?.MaxAvgHeight ?? orig.MaxH;
        if (o?.IgnoreWorldBounds ?? false)
        {
          eff.BelowX = eff.AboveX = eff.BelowY = eff.AboveY = 0f;
        }
        else
        {
          // World bounds are coordinates measured from the centre, so they scale with distances.
          eff.BelowX = orig.BelowX * s.DistanceScale;
          eff.AboveX = orig.AboveX * s.DistanceScale;
          eff.BelowY = orig.BelowY * s.DistanceScale;
          eff.AboveY = orig.AboveY * s.DistanceScale;
        }
        // A disabled entry must not make vanilla warn that it placed fewer than its minimum.
        if (!eff.Enabled)
          eff.Min = 0;
        LastEffective[alt] = eff;
        if (!eff.SameAs(orig))
        {
          run.Changed.Add(new KeyValuePair<AltBiome, AltBiomeParams>(alt, orig));
          eff.ApplyTo(alt);
        }
      }
    }

    internal static AltBiome? FindAltBiome(string name) =>
      AltBiomeList.m_altBiomes.FirstOrDefault(a => string.Equals(a.m_name, name, StringComparison.OrdinalIgnoreCase));

    // Replaces vanilla's GetSeed() inside GenerateAltBiomes (transpiler).
    public static int PlacementSeed(WorldGenerator worldGenerator)
    {
      if (WorldEnabled && Active.UseFixedSeed)
        return Active.Seed;
      return worldGenerator.GetSeed();
    }

    // The CanAddModifier prefix's decision; null runs vanilla's own test.
    internal static bool? CanAddModifierOverride(BiomeSector sector, AltBiome modifier)
    {
      if (!WorldEnabled)
        return null;
      if (RandomPhase && RandomExcluded.Contains(sector))
        return false;
      if (!CustomEligibility)
        return null;
      return CanAddModifier(sector, modifier);
    }

    // Replacement for BiomeSector.CanAddModifier when a BC eligibility option is on. With every option off
    // this is vanilla's test (BiomeSector.cs:96-179), including its neighbour bug.
    internal static bool CanAddModifier(BiomeSector sector, AltBiome modifier)
    {
      var info = GetInfo(sector);
      if (Active.MinSectorThickness > 0f && info.Thickness(sector) < Active.MinSectorThickness)
        return false;
      if (sector.DistanceFromCenter < modifier.m_minDistanceFromCenter)
        return false;
      if (sector.EdgeCount < modifier.m_minEdgeSize)
        return false;
      if (sector.EdgeCount >= modifier.m_maxEdgeSize)
        return false;
      float height = Active.MeanSectorHeight ? info.MeanHeight : sector.HeightAvg;
      if (height < modifier.m_minAvgHeight || height >= modifier.m_maxAvgHeight)
        return false;
      if ((modifier.m_aboveWorldX != 0f && sector.Center.x < modifier.m_aboveWorldX) || (modifier.m_belowWorldX != 0f && sector.Center.x > modifier.m_belowWorldX)
          || (modifier.m_aboveWorldY != 0f && sector.Center.y < modifier.m_aboveWorldY) || (modifier.m_belowWorldY != 0f && sector.Center.y > modifier.m_belowWorldY))
        return false;
      foreach (var existing in sector.AltBiomes)
        foreach (var incompatible in existing.m_incompatibleAltBiomes)
          if (incompatible == modifier.m_name)
            return false;
      foreach (var incompatible in modifier.m_incompatibleAltBiomes)
        foreach (var existing in sector.AltBiomes)
          if (existing.m_name == incompatible)
            return false;
      return Active.FixNeighbourCheck ? NeighboursOk(sector, modifier) : VanillaNeighboursOk(sector, modifier);
    }

    // Vanilla, verbatim: index 0 is None and HasFlag(None) is always true, so any require mask fails; the
    // comparison then casts the index, not the flag, to Biome, so only Meadows (1) and Swamp (2) match.
    internal static bool VanillaNeighboursOk(BiomeSector sector, AltBiome modifier)
    {
      if (modifier.m_requireNeighbor != 0)
      {
        for (int i = 0; i < 10; i++)
        {
          if (!modifier.m_requireNeighbor.HasFlag(((Heightmap.BiomeIndex)i).ToBiome()))
            continue;
          bool found = false;
          foreach (var n in sector.Neighbors)
          {
            if (n.Biome == (Heightmap.Biome)i)
            {
              found = true;
              break;
            }
          }
          if (!found)
            return false;
        }
      }
      if (modifier.m_notNeighbor != 0)
      {
        for (int j = 0; j < 10; j++)
        {
          if (!modifier.m_notNeighbor.HasFlag(((Heightmap.BiomeIndex)j).ToBiome()))
            continue;
          foreach (var n in sector.Neighbors)
            if (n.Biome == (Heightmap.Biome)j)
              return false;
        }
      }
      return true;
    }

    // What the vanilla loop evidently means: every biome in m_requireNeighbor must border the sector, and
    // no biome in m_notNeighbor may.
    internal static bool NeighboursOk(BiomeSector sector, AltBiome modifier)
    {
      for (int i = 1; i < (int)Heightmap.BiomeIndex.Count; i++)
      {
        var biome = ((Heightmap.BiomeIndex)i).ToBiome();
        if ((modifier.m_requireNeighbor & biome) != 0 && !sector.Neighbors.Any(n => n.Biome == biome))
          return false;
        if ((modifier.m_notNeighbor & biome) != 0 && sector.Neighbors.Any(n => n.Biome == biome))
          return false;
      }
      return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // A planting error fails the world load: zones generated with the wrong alt biomes would be permanent.
    // ---------------------------------------------------------------------------------------------------
    internal static string? LastFailure;
    // True while a debug-mode rebuild runs: a planting error then keeps the previous data instead of stopping.
    private static bool RuntimeRebuild;

    internal static AltBiomeLoadException FailLoad(string what, Exception e)
    {
      var world = ZNet.World?.m_name ?? WorldGenerator.instance?.m_world?.m_name ?? "?";
      // Worker threads report through an AggregateException, one inner exception per thread.
      var cause = (e is AggregateException ae ? ae.Flatten().InnerExceptions.FirstOrDefault() ?? e : e).GetBaseException();
      if (RuntimeRebuild)
      {
        var edit = $"Better Continents: {what} failed for world '{world}': {cause.Message}. "
                   + "The world keeps its previous alt biomes; fix the alt-biome map or its legend and run 'bc reload ab'.";
        LogError(edit);
        LastFailure = edit;
        return new AltBiomeLoadException(edit, e);
      }
      // The planted map is baked into the world's settings, so editing the source image or legend cannot repair a
      // world that fails here; only its settings file (or a fix in Better Continents) can.
      var advice = e is System.IO.InvalidDataException && Settings.AltBiomeMapError != null
        ? "The alt-biome map stored in the world's Better Continents settings is damaged: restore the settings from a backup (the previous save's copy ends in .old)."
        : "This is a Better Continents error: please report it with this log.";
      var message = $"Better Continents: {what} failed for world '{world}': {cause.Message}. "
                    + "The world load was stopped so that no zone is generated with the wrong alt biomes. " + advice;
      LogError(message);
      Debug.LogException(e);
      LastFailure = message;
      LastConnectionError = message;
      try
      {
        var net = ZNet.instance;
        bool server = net == null || net.IsServer();
        if (server)
          // Vanilla then refuses to save the world (ZNet.Save), so the half-loaded state never reaches disk.
          ZNet.m_loadError = true;
        if (net != null && instance != null)
          instance.StartCoroutine(StopAfterFailedLoad(net.IsDedicated(), server));
      }
      catch (Exception inner)
      {
        LogWarning($"Alt biomes: could not stop the session cleanly: {inner.Message}");
      }
      return new AltBiomeLoadException(message, e);
    }

    private static IEnumerator StopAfterFailedLoad(bool dedicated, bool server)
    {
      yield return null;
      if (dedicated)
      {
        LogError("Better Continents: stopping the dedicated server because the world failed to load (see the error above).");
        Application.Quit(1);
        yield break;
      }
      ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
      if (server)
      {
        Game.instance?.Logout(save: false);
        yield break;
      }
      var peer = ServerPeer;
      if (peer != null && ZNet.instance != null)
        ZNet.instance.Disconnect(peer);
    }

    // ---------------------------------------------------------------------------------------------------
    // Agreement: two hashes per machine, the server's assignment pushed to clients, and each client's result
    // reported back to the server's log. A mismatch is a warning, never a kick.
    // ---------------------------------------------------------------------------------------------------
    public static int LastGridHash { get; private set; }
    public static int LastAssignmentHash { get; private set; }
    public static int ServerGridHash { get; private set; }
    public static int ServerAssignmentHash { get; private set; }
    public static bool HaveServerHashes { get; private set; }
    public static string LastAgreement { get; private set; } = "";
    // Client side: the connection to the server, for the result report.
    internal static ZNetPeer? ServerPeer;

    internal sealed class AssignmentEntry
    {
      public int X;
      public int Y;
      public int Biome;
      public int Area;
      public readonly List<string> Names = [];
    }

    private static List<AssignmentEntry>? PendingServerAssignment;

    private static void Mix(ref uint h, int value)
    {
      unchecked
      {
        for (int i = 0; i < 4; i++)
        {
          h ^= (byte)(value >> (i * 8));
          h *= 16777619;
        }
      }
    }

    private static void Mix(ref uint h, string value)
    {
      unchecked
      {
        foreach (var c in value)
        {
          h ^= c;
          h *= 16777619;
        }
        h ^= 0xff;
        h *= 16777619;
      }
    }

    // FNV-1a. The grid hash covers the regions (biome, edge count, area, first point); the assignment hash
    // covers which region has which alt biomes.
    internal static void ComputeHashes(AltBiomeWorldData data)
    {
      EnsureSectorInfo(data);
      uint grid = 2166136261, assignment = 2166136261;
      Mix(ref grid, data.Size);
      Mix(ref grid, data.Sectors.Count);
      foreach (var s in data.Sectors)
      {
        var info = GetInfo(s);
        Mix(ref grid, (int)s.Biome);
        Mix(ref grid, s.EdgeCount);
        Mix(ref grid, info.Area);
        Mix(ref grid, info.SeedX);
        Mix(ref grid, info.SeedY);
        if (s.AltBiomes.Count == 0)
          continue;
        Mix(ref assignment, info.SeedX);
        Mix(ref assignment, info.SeedY);
        Mix(ref assignment, (int)s.Biome);
        foreach (var alt in s.AltBiomes)
          Mix(ref assignment, alt.m_name ?? "");
      }
      LastGridHash = unchecked((int)grid);
      LastAssignmentHash = unchecked((int)assignment);
    }

    public static void ResetServerAssignment()
    {
      PendingServerAssignment = null;
      HaveServerHashes = false;
      LastAgreement = "";
    }

    // Server: the alt biomes of every sector that has any, keyed by the sector's first grid point.
    public static ZPackage? BuildServerAssignmentPackage()
    {
      var data = ZNet.World?.m_biomeData;
      if (!WorldEnabled || data == null || !data.IsReady)
        return null;
      EnsureSectorInfo(data);
      ComputeHashes(data);
      var pkg = new ZPackage();
      pkg.Write(1);
      pkg.Write(LastGridHash);
      pkg.Write(LastAssignmentHash);
      var assigned = data.Sectors.Where(s => s.AltBiomes.Count > 0).ToList();
      pkg.Write(assigned.Count);
      foreach (var s in assigned)
      {
        var info = GetInfo(s);
        pkg.Write(info.SeedX);
        pkg.Write(info.SeedY);
        pkg.Write((int)s.Biome);
        pkg.Write(info.Area);
        pkg.Write(s.AltBiomes.Count);
        foreach (var alt in s.AltBiomes)
          pkg.Write(alt.m_name ?? "");
      }
      return pkg;
    }

    public static void ReceiveServerAssignment(ZPackage pkg)
    {
      PendingServerAssignment = null;
      HaveServerHashes = false;
      try
      {
        if (!ParseServerAssignment(pkg, out var gridHash, out var assignmentHash, out var entries))
        {
          LogWarning("Alt biomes: unknown alt-biome placement format from the server; using the local placement.");
          return;
        }
        ServerGridHash = gridHash;
        ServerAssignmentHash = assignmentHash;
        HaveServerHashes = true;
        PendingServerAssignment = entries;
      }
      catch (Exception e)
      {
        LogWarning($"Alt biomes: could not read the server's placement ({e.Message}); using the local placement.");
        return;
      }
      Log($"Alt biomes: received the server's placement ({PendingServerAssignment.Count} sectors with alt biomes).");
    }

    internal static bool ParseServerAssignment(ZPackage pkg, out int gridHash, out int assignmentHash, out List<AssignmentEntry> entries)
    {
      entries = [];
      gridHash = assignmentHash = 0;
      if (pkg.ReadInt() != 1)
        return false;
      gridHash = pkg.ReadInt();
      assignmentHash = pkg.ReadInt();
      int count = pkg.ReadInt();
      for (int i = 0; i < count; i++)
      {
        var e = new AssignmentEntry { X = pkg.ReadInt(), Y = pkg.ReadInt(), Biome = pkg.ReadInt(), Area = pkg.ReadInt() };
        int names = pkg.ReadInt();
        for (int n = 0; n < names; n++)
          e.Names.Add(pkg.ReadString());
        entries.Add(e);
      }
      return true;
    }

    internal static void ApplyServerAssignment(AltBiomeWorldData data, List<AssignmentEntry> entries)
    {
      ClearAssignments(data);
      int missingSector = 0, missingAlt = 0;
      foreach (var e in entries)
      {
        if (e.X < 0 || e.Y < 0 || e.X >= data.Size || e.Y >= data.Size)
        {
          missingSector++;
          continue;
        }
        var s = data.PointSectors[e.X, e.Y];
        // The sector under the server's seed point must be the same region here: same biome and about the
        // same size. A region that merged with a neighbour on this machine (different planting data, or a
        // different grid) would otherwise spread the alt biome over land the server never gave it.
        int localArea = s != null ? GetInfo(s).Area : 0;
        if (s == null || (int)s.Biome != e.Biome || Math.Abs(localArea - e.Area) > Math.Max(16, e.Area / 20))
        {
          missingSector++;
          continue;
        }
        foreach (var name in e.Names)
        {
          var alt = FindAltBiome(name);
          if (alt == null)
          {
            missingAlt++;
            continue;
          }
          if (!s.AltBiomes.Contains(alt))
            s.AddModifier(alt);
        }
      }
      if (missingSector > 0 || missingAlt > 0)
        LogWarning($"Alt biomes: {missingSector} of the server's alt-biome sectors have no matching sector here and {missingAlt} alt biome name(s) are unknown here; those regions have no alt biome on this client.");
    }

    // After the grid, sectors and placement exist: apply the server's placement on clients, compute the
    // hashes, report back to the server and log the report.
    internal static void OnBiomeDataReady(World world, string reason)
    {
      var data = world.m_biomeData;
      if (!WorldEnabled || data == null || !data.IsReady)
        return;
      try
      {
        EnsureSectorInfo(data);
        bool client = ZNet.instance != null && !ZNet.instance.IsServer();
        if (client && PendingServerAssignment != null)
          ApplyServerAssignment(data, PendingServerAssignment);
        ComputeHashes(data);
        if (client && HaveServerHashes)
        {
          LastAgreement = LastGridHash == ServerGridHash && LastAssignmentHash == ServerAssignmentHash
            ? "matches the server"
            : LastGridHash != ServerGridHash
              ? $"SECTOR GRID DIFFERS from the server (grid {LastGridHash:x8} here, {ServerGridHash:x8} there); the server's placement was applied where sectors match"
              : $"placement differs from the server (assignment {LastAssignmentHash:x8} here, {ServerAssignmentHash:x8} there)";
          if (LastAgreement == "matches the server")
            Log($"Alt biomes: {LastAgreement} (grid {LastGridHash:x8}, assignment {LastAssignmentHash:x8}).");
          else
            LogWarning($"Alt biomes: {LastAgreement}.");
          SendResultToServer();
        }
        foreach (var line in AltBiomeReport.Summary(detailed: false, reason))
          Log(line);
        foreach (var line in AltBiomeReport.PlantedLines(100))
          Log(line);
      }
      catch (Exception e)
      {
        LogError($"Alt biomes: the world-load report failed: {e}");
      }
    }

    private static void SendResultToServer()
    {
      var peer = ServerPeer;
      if (peer?.m_rpc == null || peer.m_socket == null || !peer.m_socket.IsConnected())
        return;
      peer.m_rpc.Invoke("BetterContinentsAltBiomesResult", LastGridHash, LastAssignmentHash);
    }

    // Server: a client's hashes, after it applied the server's placement.
    internal static void OnClientResult(ZNetPeer peer, int gridHash, int assignmentHash)
    {
      var who = string.IsNullOrEmpty(peer.m_playerName) ? peer.m_socket?.GetEndPointString() ?? "?" : peer.m_playerName;
      var data = ZNet.World?.m_biomeData;
      if (data != null && data.IsReady)
        ComputeHashes(data);
      if (gridHash == LastGridHash && assignmentHash == LastAssignmentHash)
        Log($"Alt biomes: client {who} matches the server (grid {gridHash:x8}, assignment {assignmentHash:x8}).");
      else if (gridHash != LastGridHash)
        LogWarning($"Alt biomes: client {who} has a DIFFERENT sector grid (grid {gridHash:x8} there, {LastGridHash:x8} here). It uses the server's placement where its sectors match; the rest has no alt biome on that client. Different maps, settings, mods or game versions cause this.");
      else
        LogWarning($"Alt biomes: client {who} has a different placement (assignment {assignmentHash:x8} there, {LastAssignmentHash:x8} here): some alt-biome names are unknown on that client.");
    }

    // ---------------------------------------------------------------------------------------------------
    // Runtime rebuild, for debug-mode edits. Vanilla only builds this data at world load.
    // ---------------------------------------------------------------------------------------------------
    private static int RebuildGeneration;

    public static void RequestRebuild(RebuildLevel level, string reason, Action? after = null)
    {
      var world = WorldGenerator.instance?.m_world;
      var data = world?.m_biomeData;
      if (world == null || data == null || !data.IsReady || world.m_menu || !WorldEnabled)
      {
        after?.Invoke();
        return;
      }
      int generation = ++RebuildGeneration;
      Warned.Clear();
      if (level == RebuildLevel.Points)
      {
        instance.StartCoroutine(RebuildPoints(world, generation, reason, after));
        return;
      }
      RuntimeRebuild = true;
      try
      {
        if (level == RebuildLevel.Assignment)
        {
          data.GenerateAltBiomes();
        }
        else
        {
          var fresh = CopyPoints(world, data);
          fresh.GenerateSectors();
          world.m_biomeData = fresh;
        }
      }
      catch (AltBiomeLoadException)
      {
        // FailLoad has logged it. A failed Sectors rebuild is discarded; a failed Assignment re-run leaves the
        // regions without alt biomes until the author fixes the map and reloads it.
        RuntimeRebuild = false;
        after?.Invoke();
        return;
      }
      finally
      {
        RuntimeRebuild = false;
      }
      Finish(world, reason, after);
    }

    private static AltBiomeWorldData CopyPoints(World world, AltBiomeWorldData data)
    {
      var fresh = new AltBiomeWorldData(data.Size);
      Array.Copy(data.PointBiomes, fresh.PointBiomes, data.PointBiomes.Length);
      Array.Copy(data.PointHeights, fresh.PointHeights, data.PointHeights.Length);
      fresh.PointsGenerated = true;
      fresh.m_world = world;
      return fresh;
    }

    private static IEnumerator RebuildPoints(World world, int generation, string reason, Action? after)
    {
      // GenerateBiomePoints writes its result into the World it is given; a scratch World keeps a slow,
      // superseded rebuild from ever replacing the live data.
      var scratch = new World { m_name = world.m_name };
      var task = Task.Run(() =>
      {
        AltBiomeWorldData.GenerateBiomePoints(scratch);
        return scratch.m_biomeData;
      });
      try
      {
        UI.Add("AltBiomeRebuild", () => UI.DisplayMessage("Better Continents: rebuilding alt-biome sectors ..."));
        yield return new WaitUntil(() => task.IsCompleted);
      }
      finally
      {
        UI.Remove("AltBiomeRebuild");
      }
      if (generation != RebuildGeneration)
        yield break;
      if (task.IsFaulted || task.Result == null)
      {
        LogError($"Alt biomes: rebuilding the point grid failed: {task.Exception?.GetBaseException().Message}");
        after?.Invoke();
        yield break;
      }
      var fresh = task.Result;
      fresh.m_world = world;
      RuntimeRebuild = true;
      try
      {
        fresh.GenerateSectors();
      }
      catch (AltBiomeLoadException)
      {
        RuntimeRebuild = false;
        after?.Invoke();
        yield break;
      }
      finally
      {
        RuntimeRebuild = false;
      }
      // Not written to the biome cache: 1.0.15 deletes that file and regenerates on every load (VerifyBiomeData ->
      // RemoveCache), so it would be 20 MB written for nothing. Should a later game version read the cache again,
      // the fingerprint trailer rejects the older file for these new settings.
      world.m_biomeData = fresh;
      Finish(world, reason, after);
    }

    private static void Finish(World world, string reason, Action? after)
    {
      InfoFor = null;
      OnBiomeDataReady(world, reason);
      after?.Invoke();
    }
  }
}

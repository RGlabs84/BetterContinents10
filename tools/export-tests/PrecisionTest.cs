// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Grid = BetterContinents.BetterContinents.BiomePrecisionGrid;

namespace ExportTest;

// Biome precision (BetterContinents.HeightmapPatch.cs, Patcher.PatchHeightmap) against the game's own
// HeightmapBuilder.Build, run offline with TerrainTest's synthetic biome and height functions (TerrainTest.Run must have
// patched them in first) and a synthetic 12 m sector grid, and against independent copies of vanilla's
// Heightmap.GetBiome and GetBiomeColor rules.
internal static class PrecisionTest
{
  static void C(bool ok, string what) => Program.C(ok, what);
  static void Section(string s) => System.Console.WriteLine("== " + s);

  const int Width = 64;

  // ---- a 12 m sector grid like AltBiomeWorldData's -------------------------------------------------------------
  // A point's sector is the region of TerrainTest's biome checkerboard that holds the centre of its 12 m cell, so
  // near a border the sector's biome can differ from the point's own, as in the game. Every third region carries an
  // alt biome with a terrain texture override.
  static readonly Dictionary<(int, int), BiomeSector> Regions = [];
  static bool StubSectors;

  static BiomeSector Sector(float wx, float wz)
  {
    float cx = (Mathf.Floor(wx / 12f) + 0.5f) * 12f, cz = (Mathf.Floor(wz / 12f) + 0.5f) * 12f;
    var key = ((int)Math.Floor((cx + 37f) / 211f), (int)Math.Floor((cz - 11f) / 157f));
    lock (Regions)
    {
      if (!Regions.TryGetValue(key, out var sector))
      {
        sector = new BiomeSector(null, TerrainTest.Biome(cx, cz));
        if (((key.Item1 * 7 + key.Item2 * 3) % 3 + 3) % 3 == 0)
          sector.AltBiomes.Add(new AltBiome { m_name = $"Alt {key}", m_terrainTextureOverride = Heightmap.Biome.Plains });
        Regions[key] = sector;
      }
      return sector;
    }
  }

  static bool GetBiomeSectorPrefix(float wx, float wy, ref BiomeSector __result)
  {
    if (!StubSectors)
      return true;
    __result = Sector(wx, wy);
    return false;
  }

  // Heightmap.WorldToNormalizedHM reads transform.position, a native call: the fake heightmaps here have their
  // centre in this table instead.
  static readonly Dictionary<Heightmap, Vector3> Centres = new(ReferenceEqualityComparer.Instance);

  static bool WorldToNormalizedHMPrefix(Heightmap __instance, Vector3 worldPos, out float x, out float y)
  {
    float span = __instance.m_width * __instance.m_scale;
    var v = worldPos - Centres[__instance];
    x = v.x / span + 0.5f;
    y = v.z / span + 0.5f;
    return false;
  }

  // ---- reflection into the game ----------------------------------------------------------------------------------
  static readonly FieldInfo CornerBiomes = typeof(Heightmap).GetField("m_cornerBiomes", BindingFlags.NonPublic | BindingFlags.Instance)!;
  static readonly FieldInfo DistantLod = typeof(Heightmap).GetField("m_isDistantLod", BindingFlags.NonPublic | BindingFlags.Instance)!;
  static readonly MethodInfo VanillaColor = typeof(Heightmap).GetMethod("GetBiomeColor", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(float), typeof(float)], null)!;
  static readonly Func<float, float, float, float, float> Distance = (Func<float, float, float, float, float>)Delegate.CreateDelegate(
    typeof(Func<float, float, float, float, float>), typeof(Heightmap).GetMethod("Distance", BindingFlags.NonPublic | BindingFlags.Static)!);

  static Heightmap FakeHeightmap(Vector3 centre, BiomeSector[] corners, bool distantLod = false)
  {
    var hm = (Heightmap)RuntimeHelpers.GetUninitializedObject(typeof(Heightmap));
    hm.m_width = Width;
    hm.m_scale = 1f;
    CornerBiomes.SetValue(hm, corners);
    DistantLod.SetValue(hm, distantLod);
    Centres[hm] = centre;
    return hm;
  }

  // Vanilla's Heightmap.GetBiome rule (Heightmap.cs:484-509) for four corners, x and y 0 to 1 across them.
  static Heightmap.Biome VanillaRule(Heightmap.Biome b0, Heightmap.Biome b1, Heightmap.Biome b2, Heightmap.Biome b3, float x, float y)
  {
    if (b0 == b1 && b0 == b2 && b0 == b3)
      return b0;
    var w = new float[10];
    w[b0.ToIndex()] += Distance(x, y, 0f, 0f);
    w[b1.ToIndex()] += Distance(x, y, 1f, 0f);
    w[b2.ToIndex()] += Distance(x, y, 0f, 1f);
    w[b3.ToIndex()] += Distance(x, y, 1f, 1f);
    int best = 0;
    float most = -99999f;
    for (int j = 1; j < w.Length; j++)
      if (w[j] > most) { best = j; most = w[j]; }
    return ((Heightmap.BiomeIndex)best).ToBiome();
  }

  // The same rule inside the grid cell that holds (x, y).
  static Heightmap.Biome GridRule(Grid g, float x, float y)
  {
    int n = g.Cells + 1;
    float gx = x * g.Cells, gy = y * g.Cells;
    int cx = Math.Clamp((int)MathF.Floor(gx), 0, g.Cells - 1), cy = Math.Clamp((int)MathF.Floor(gy), 0, g.Cells - 1);
    var s = g.Sectors;
    return VanillaRule(s[cy * n + cx].Biome, s[cy * n + cx + 1].Biome, s[(cy + 1) * n + cx].Biome, s[(cy + 1) * n + cx + 1].Biome, gx - cx, gy - cy);
  }

  // Vanilla's GetBiomeColor blend (Heightmap.cs:632-645) inside the cell of vertex (col, row) of a heightmap `width`
  // wide: the cell from the vertex's linear position, SmoothStep across the cell.
  static Color GridColor(Grid g, int col, int row, int width)
  {
    int n = g.Cells + 1;
    void Axis(int v, out int cell, out float blend)
    {
      double t = (double)v / width * g.Cells;
      cell = Math.Clamp((int)Math.Floor(t), 0, g.Cells - 1);
      blend = DUtils.SmoothStep(0f, 1f, (float)(t - cell));
    }
    Axis(col, out int cx, out float u);
    Axis(row, out int cy, out float v);
    var s = g.Sectors;
    BiomeSector s0 = s[cy * n + cx], s1 = s[cy * n + cx + 1], s2 = s[(cy + 1) * n + cx], s3 = s[(cy + 1) * n + cx + 1];
    if (s0 == s1 && s0 == s2 && s0 == s3)
      return Heightmap.GetBiomeColor(s0);
    Color32 a = Color32.Lerp(Heightmap.GetBiomeColor(s0), Heightmap.GetBiomeColor(s1), u);
    Color32 b = Color32.Lerp(Heightmap.GetBiomeColor(s2), Heightmap.GetBiomeColor(s3), u);
    return Color32.Lerp(a, b, v);
  }

  static float VertexS(int v, int width) => DUtils.SmoothStep(0f, 1f, (float)((double)v / (double)width));

  static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

  // ---- the run -----------------------------------------------------------------------------------------------------
  public static void Run()
  {
    Section("biome precision: the builder patch, the grid and the Heightmap readers");
    var harmony = new Harmony("export-test-precision");
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), [typeof(float), typeof(float), typeof(bool)]),
      prefix: new HarmonyMethod(typeof(PrecisionTest), nameof(GetBiomeSectorPrefix)));
    harmony.Patch(AccessTools.Method(typeof(Heightmap), "WorldToNormalizedHM"),
      prefix: new HarmonyMethod(typeof(PrecisionTest), nameof(WorldToNormalizedHMPrefix)));
    var savedSettings = BC.Settings;
    var savedHarmony = BC.HarmonyInstance;
    BC.HarmonyInstance = new Harmony("export-test-bc");
    // No terrain map: its branch of the GetBiomeColor prefix reads transform.position, a native call.
    BC.Settings = new BC.BetterContinentsSettings();
    StubSectors = true;
    try
    {
      // The builder patch is a static [HarmonyPatch]: bind it from its attributes, as PatchAll does in the game.
      BC.HarmonyInstance.CreateClassProcessor(typeof(BC.HeightmapBuilderBuildPatch)).Patch();
      var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(HeightmapBuilder), "Build"));
      C(info != null && info.Postfixes.Count(p => p.owner == BC.HarmonyInstance.Id) == 1, "the HeightmapBuilder.Build postfix binds from its [HarmonyPatch] attribute");
      Builds();
      Readers();
      Patcher();
      Table();
    }
    finally
    {
      StubSectors = false;
      Grid.Active = 0;
      Grid.Clear();
      BC.Settings = savedSettings;
      BC.HarmonyInstance = savedHarmony;
    }
  }

  static readonly WorldGenerator Wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
  static readonly object Builder = RuntimeHelpers.GetUninitializedObject(typeof(HeightmapBuilder));
  static readonly MethodInfo BuildMethod = typeof(HeightmapBuilder).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;

  // The game's HeightmapBuilder.Build (with Better Continents' postfix bound) for one zone.
  static HeightmapBuilder.HMBuildData Build(Vector3 centre, int precision, bool distantLod = false, int width = Width, float scale = 1f)
  {
    Grid.Active = precision;
    var data = new HeightmapBuilder.HMBuildData(centre, width, scale, distantLod, Wg);
    BuildMethod.Invoke(Builder, [data]);
    return data;
  }

  static Grid? GridOf(BiomeSector[] corners, int active = 1)
  {
    int saved = Grid.Active;
    Grid.Active = active;
    var grid = Grid.For(corners);
    Grid.Active = saved;
    return grid;
  }

  // Zones of the synthetic world: 15 x 15 around the origin, 64 m apart, so borders cross them at every angle.
  static IEnumerable<Vector3> Zones()
  {
    for (int j = -7; j <= 7; j++)
      for (int i = -7; i <= 7; i++)
        yield return new Vector3(i * 64f, 0f, j * 64f);
  }

  static readonly List<(Vector3 Centre, HeightmapBuilder.HMBuildData Vanilla, HeightmapBuilder.HMBuildData[] ByPrecision)> Built = [];

  static void Builds()
  {
    Section("biome precision: HeightmapBuilder.Build output and the grids it gets");
    int zones = 0, mixed = 0, refBad = 0, noGridAt0 = 0, identicalBad = 0, sizeBad = 0, sampleBad = 0, cornerBad = 0;
    int keptSectors = 0, plainSectors = 0, keptAlt = 0, plainWhereDisagree = 0, disagree = 0;
    foreach (var centre in Zones())
    {
      zones++;
      var d0 = Build(centre, 0);
      if (GridOf(d0.m_cornerBiomes) == null) noGridAt0++;
      // The export's reference copy of HeightmapBuilder.Build's blend (TerrainTest), evaluated at every vertex.
      float x0 = centre.x - 32f, z0 = centre.z - 32f;
      var b0 = TerrainTest.Biome(x0, z0);
      var b1 = TerrainTest.Biome((float)((double)x0 + 64.0), z0);
      var b2 = TerrainTest.Biome(x0, (float)((double)z0 + 64.0));
      var b3 = TerrainTest.Biome((float)((double)x0 + 64.0), (float)((double)z0 + 64.0));
      bool uniform = b2 == b0 && b1 == b0 && b3 == b0;
      if (!uniform) mixed++;
      for (int k = 0; k <= Width; k++)
      {
        float wz = (float)((double)z0 + (double)k * 1.0);
        float t = DUtils.SmoothStep(0f, 1f, (float)((double)k / Width));
        for (int l = 0; l <= Width; l++)
        {
          float wx = (float)((double)x0 + (double)l * 1.0);
          float t2 = DUtils.SmoothStep(0f, 1f, (float)((double)l / Width));
          float h;
          Color mask;
          if (uniform)
            h = TerrainTest.Height(b0, wx, wz, out mask);
          else
          {
            var m = new Color[4];
            float h0 = TerrainTest.Height(b0, wx, wz, out m[0]), h1 = TerrainTest.Height(b1, wx, wz, out m[1]), h2 = TerrainTest.Height(b2, wx, wz, out m[2]), h3 = TerrainTest.Height(b3, wx, wz, out m[3]);
            h = DUtils.Lerp(DUtils.Lerp(h0, h1, t2), DUtils.Lerp(h2, h3, t2), t);
            mask = Color.Lerp(Color.Lerp(m[0], m[1], t2), Color.Lerp(m[2], m[3], t2), t);
          }
          int i = k * (Width + 1) + l;
          if (d0.m_baseHeights[i] != h || !Same(d0.m_baseMask[i], mask)) refBad++;
        }
      }
      var corners = new[] { Sector(x0, z0), Sector((float)((double)x0 + 64.0), z0), Sector(x0, (float)((double)z0 + 64.0)), Sector((float)((double)x0 + 64.0), (float)((double)z0 + 64.0)) };
      if (!corners.SequenceEqual(d0.m_cornerBiomes)) refBad++;

      var byPrecision = new HeightmapBuilder.HMBuildData[Grid.MaxPrecision + 1];
      byPrecision[0] = d0;
      for (int p = 1; p <= Grid.MaxPrecision; p++)
      {
        var d = Build(centre, p);
        byPrecision[p] = d;
        // Build's own output does not depend on the precision at all.
        if (d.m_cornerBiomes.Length != 4 || !d.m_cornerBiomes.SequenceEqual(d0.m_cornerBiomes) || !d.m_baseHeights.SequenceEqual(d0.m_baseHeights)
            || d.m_baseMask.Length != d0.m_baseMask.Length || d.m_baseMask.Where((c, i) => !Same(c, d0.m_baseMask[i])).Any())
          identicalBad++;
        var g = GridOf(d.m_cornerBiomes, p);
        int cells = p + 1, n = cells + 1;
        if (g == null || g.Cells != cells || g.Sectors.Length != n * n) { sizeBad++; continue; }
        for (int row = 0; row < n; row++)
          for (int col = 0; col < n; col++)
          {
            float wx = (float)((double)x0 + 64.0 * ((double)col / cells)), wz = (float)((double)z0 + 64.0 * ((double)row / cells));
            var biome = TerrainTest.Biome(wx, wz);
            var world = Sector(wx, wz);
            var want = world.Biome == biome ? world : BC.PlainSectors[biome.ToIndex()];
            var got = g.Sectors[row * n + col];
            if (!ReferenceEquals(got, want)) sampleBad++;
            if (world.Biome == biome) { keptSectors++; if (got.AltBiomes.Count > 0) keptAlt++; }
            else { plainSectors++; disagree++; if (got.AltBiomes.Count == 0 && got.Biome == biome) plainWhereDisagree++; }
          }
        // The four outer samples are vanilla's corner points: the biomes its height blend uses.
        if (g.Sectors[0].Biome != b0 || g.Sectors[cells].Biome != b1 || g.Sectors[cells * n].Biome != b2 || g.Sectors[n * n - 1].Biome != b3) cornerBad++;
      }
      Built.Add((centre, d0, byPrecision));
    }
    C(refBad == 0, $"precision 0: every height, paint mask and corner sector equals the reference copy of HeightmapBuilder.Build ({refBad} differ over {zones} zones, {mixed} of them mixed)");
    C(noGridAt0 == zones, "precision 0: no zone gets a grid");
    C(identicalBad == 0, $"precision 1-{Grid.MaxPrecision}: Build's heights, paint mask and four corner sectors are identical to precision 0 ({identicalBad} builds differ)");
    C(sizeBad == 0, "precision N: every zone gets a grid of (N + 2) x (N + 2) samples");
    C(sampleBad == 0, $"every sample is the biome function at its point, with the world's sector when it agrees and a plain sector when not ({sampleBad} wrong)");
    C(keptAlt > 0 && plainWhereDisagree == disagree && disagree > 0, $"alt-biome sectors are kept where the sector's biome agrees ({keptSectors} kept, {keptAlt} with alt biomes; {plainSectors} plain where the 12 m sector disagrees)");
    C(cornerBad == 0, "the outer samples are the biomes vanilla takes at the zone's corners");

    // Distant LOD is never touched.
    int lodBad = 0;
    foreach (var centre in new[] { new Vector3(0f, 0f, 0f), new Vector3(1024f, 0f, -512f), new Vector3(-2048f, 0f, 3072f) })
    {
      var l0 = Build(centre, 0, distantLod: true, scale: 16f);
      var l3 = Build(centre, 3, distantLod: true, scale: 16f);
      if (GridOf(l3.m_cornerBiomes) != null || !l3.m_baseHeights.SequenceEqual(l0.m_baseHeights) || l3.m_baseMask.Where((c, i) => !Same(c, l0.m_baseMask[i])).Any() || !l3.m_cornerBiomes.SequenceEqual(l0.m_cornerBiomes))
        lodBad++;
    }
    C(lodBad == 0, "distant LOD builds get no grid and the same output at any precision");
  }

  static void Readers()
  {
    Section("biome precision: GetBiome, GetBiomeColor and HaveBiome on the grid");
    var rng = new System.Random(20260924);
    // One cell per zone is vanilla: GetBiome with the zone's own corner sectors, GetBiomeColor at every vertex.
    int oneBiomeBad = 0, oneColourBad = 0, oneColours = 0;
    foreach (var (centre, d0, _) in Built.Take(60))
    {
      var one = new Grid(1, [.. d0.m_cornerBiomes]);
      var c = d0.m_cornerBiomes;
      for (int s = 0; s < 200; s++)
      {
        float x = (float)rng.NextDouble(), y = (float)rng.NextDouble();
        if (one.GetBiome(x, y) != VanillaRule(c[0].Biome, c[1].Biome, c[2].Biome, c[3].Biome, x, y)) oneBiomeBad++;
      }
      var hm = FakeHeightmap(centre, c);
      for (int row = 0; row <= Width; row++)
        for (int col = 0; col <= Width; col++)
        {
          float ix = VertexS(col, Width), iy = VertexS(row, Width);
          oneColours++;
          if (!Same(one.GetColor(ix, iy, Width), (Color)VanillaColor.Invoke(hm, [ix, iy])!)) oneColourBad++;
        }
    }
    C(oneBiomeBad == 0, "a grid of one cell per zone gives vanilla's GetBiome at every point");
    C(oneColourBad == 0, $"a grid of one cell per zone gives vanilla's GetBiomeColor at every vertex ({oneColours} vertices)");

    int vertexBad = 0, pointBad = 0, colourBad = 0, colours = 0;
    var mismatch = new long[Grid.MaxPrecision + 1];
    long samples = 0;
    foreach (var (centre, d0, byPrecision) in Built)
    {
      float x0 = centre.x - 32f, z0 = centre.z - 32f;
      var c = d0.m_cornerBiomes;
      var points = Enumerable.Range(0, 64).Select(_ => ((float)rng.NextDouble(), (float)rng.NextDouble())).ToArray();
      foreach (var (x, y) in points)
      {
        var truth = TerrainTest.Biome(x0 + x * 64f, z0 + y * 64f);
        if (VanillaRule(c[0].Biome, c[1].Biome, c[2].Biome, c[3].Biome, x, y) != truth) mismatch[0]++;
      }
      samples += points.Length;
      for (int p = 1; p <= Grid.MaxPrecision; p++)
      {
        var g = GridOf(byPrecision[p].m_cornerBiomes, p)!;
        int n = g.Cells + 1;
        // At a sample the answer is that sample's biome.
        for (int row = 0; row < n; row++)
          for (int col = 0; col < n; col++)
            if (g.GetBiome((float)col / g.Cells, (float)row / g.Cells) != g.Sectors[row * n + col].Biome) vertexBad++;
        foreach (var (x, y) in points)
        {
          var got = g.GetBiome(x, y);
          if (got != GridRule(g, x, y)) pointBad++;
          if (got != TerrainTest.Biome(x0 + x * 64f, z0 + y * 64f)) mismatch[p]++;
        }
        for (int row = 0; row <= Width; row += 4)
          for (int col = 0; col <= Width; col++)
          {
            colours++;
            if (!Same(g.GetColor(VertexS(col, Width), VertexS(row, Width), Width), GridColor(g, col, row, Width))) colourBad++;
          }
      }
    }
    C(vertexBad == 0, "GetBiome at each sample of the grid is that sample's biome, at every precision");
    C(pointBad == 0, "GetBiome between samples is vanilla's corner rule inside the sample cell");
    C(colourBad == 0, $"GetBiomeColor at every vertex is vanilla's blend inside the vertex's cell ({colours} vertices)");
    System.Console.WriteLine($"    points whose biome differs from the biome function: {string.Join(", ", mismatch.Select((m, p) => $"precision {p}: {100.0 * m / samples:0.00}%"))}");
    bool better = true;
    for (int p = 1; p <= Grid.MaxPrecision; p++)
      if (mismatch[p] >= mismatch[0]) better = false;
    C(better && mismatch[Grid.MaxPrecision] * 3 < mismatch[0], "every precision follows the biome function more closely than vanilla, precision 5 at least 3x more");

    // Sub-cell centres: vertices of a heightmap 120 wide land on every centre for 1 to 6 cells.
    int centreBad = 0, centres = 0;
    var palette = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands };
    for (int cells = 1; cells <= Grid.MaxPrecision + 1; cells++)
    {
      int n = cells + 1;
      var sectors = Enumerable.Range(0, n * n).Select(i => BC.PlainSectors[palette[(i * 5 + i / n) % palette.Length].ToIndex()]).ToArray();
      var g = new Grid(cells, sectors);
      const int w = 120;
      int step = w / cells;
      for (int cy = 0; cy < cells; cy++)
        for (int cx = 0; cx < cells; cx++)
        {
          int col = cx * step + step / 2, row = cy * step + step / 2;
          Color32 a = Color32.Lerp(Heightmap.GetBiomeColor(sectors[cy * n + cx]), Heightmap.GetBiomeColor(sectors[cy * n + cx + 1]), 0.5f);
          Color32 b = Color32.Lerp(Heightmap.GetBiomeColor(sectors[(cy + 1) * n + cx]), Heightmap.GetBiomeColor(sectors[(cy + 1) * n + cx + 1]), 0.5f);
          centres++;
          if (!Same(g.GetColor(VertexS(col, w), VertexS(row, w), w), Color32.Lerp(a, b, 0.5f))) centreBad++;
        }
    }
    C(centreBad == 0, $"GetBiomeColor at the centre of every cell is the even mix of its four samples, 1 to 6 cells ({centres} centres)");

    // The vertex a colour call is for is recovered exactly.
    int posBad = 0;
    foreach (var w in new[] { 32, 64, 120, 128 })
      for (int v = 0; v <= w; v++)
        if (Grid.VertexPosition(VertexS(v, w), w) != (double)v / w) posBad++;
    double worst = 0;
    for (int s = 0; s <= 1000; s++)
    {
      float x = s / 1000f;
      worst = Math.Max(worst, Math.Abs(DUtils.SmoothStep(0f, 1f, (float)Grid.VertexPosition(x, 0)) - x));
    }
    C(posBad == 0 && worst < 1e-6, $"VertexPosition inverts RebuildRenderMesh's SmoothStep exactly at vertices (worst elsewhere {worst:E1})");

    // HaveBiome: a biome the grid finds between the corners passes; one it does not stays out.
    var meadows = BC.PlainSectors[Heightmap.Biome.Meadows.ToIndex()];
    var swamp = BC.PlainSectors[Heightmap.Biome.Swamp.ToIndex()];
    var corners = new[] { meadows, meadows, meadows, meadows };
    var inner = Enumerable.Repeat(meadows, 16).ToArray();
    inner[5] = swamp;
    Grid.Add(corners, new Grid(3, inner));
    var hmh = FakeHeightmap(Vector3.zero, corners);
    bool Have(Heightmap.Biome b, bool vanilla)
    {
      bool r = vanilla;
      BC.HaveBiomePatch(hmh, b, ref r);
      return r;
    }
    Grid.Active = 3;
    bool widened = Have(Heightmap.Biome.Swamp, false) && !Have(Heightmap.Biome.Mountain, false) && Have(Heightmap.Biome.Meadows, true) && Have(Heightmap.Biome.Swamp | Heightmap.Biome.Mountain, false);
    Grid.Active = 0;
    bool offVanilla = !Have(Heightmap.Biome.Swamp, false);
    C(widened && offVanilla, "HaveBiome also passes a biome found only inside the zone, never one that is absent, and not with precision off");

    // The patch methods on (fake) heightmaps: the GetBiome prefix, the GetBiomeColor prefix.
    int glueBad = 0;
    foreach (var (centre, _, byPrecision) in Built.Take(40))
    {
      var d = byPrecision[3];
      Grid.Active = 3;
      var g = Grid.For(d.m_cornerBiomes)!;
      var hm = FakeHeightmap(centre, d.m_cornerBiomes);
      for (int s = 0; s < 50; s++)
      {
        var point = centre + new Vector3((float)rng.NextDouble() * 64f - 32f, 10f, (float)rng.NextDouble() * 64f - 32f);
        var r = Heightmap.Biome.None;
        if (BC.GetBiomePatch(hm, point, false, ref r) || r != g.GetBiome((point.x - centre.x) / 64f + 0.5f, (point.z - centre.z) / 64f + 0.5f)) glueBad++;
        var dummy = Heightmap.Biome.None;
        if (!BC.GetBiomePatch(hm, point, true, ref dummy)) glueBad++;
      }
      var color = Color.clear;
      float ix = VertexS(17, Width), iy = VertexS(40, Width);
      if (BC.GetBiomeColorPatch(hm, ix, iy, ref color) || !Same(color, g.GetColor(ix, iy, Width))) glueBad++;
      var lod = FakeHeightmap(centre, d.m_cornerBiomes, distantLod: true);
      var lr = Heightmap.Biome.None;
      if (!BC.GetBiomePatch(lod, centre, false, ref lr)) glueBad++;
      var bare = FakeHeightmap(centre, [.. d.m_cornerBiomes]);
      if (!BC.GetBiomePatch(bare, centre, false, ref lr) || !BC.GetBiomeColorPatch(bare, ix, iy, ref color)) glueBad++;
      Grid.Active = 0;
      if (!BC.GetBiomePatch(hm, centre, false, ref lr) || !BC.GetBiomeColorPatch(hm, ix, iy, ref color)) glueBad++;
    }
    C(glueBad == 0, "the GetBiome and GetBiomeColor prefixes use the grid of the heightmap's own corner array, and leave distant LOD, waterAlwaysOcean, grid-less heightmaps and precision 0 to vanilla");
  }

  static int Owned(MethodBase method, Func<Patches, IEnumerable<Patch>> kind)
  {
    var info = Harmony.GetPatchInfo(method);
    return info == null ? 0 : kind(info).Count(p => p.owner == BC.HarmonyInstance.Id);
  }

  static void Patcher()
  {
    Section("biome precision: PatchHeightmap and PatchBiomeColor");
    var getBiome = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.GetBiome), [typeof(Vector3), typeof(float), typeof(bool)]);
    var haveBiome = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.HaveBiome), [typeof(Heightmap.Biome)]);
    var getColor = AccessTools.Method(typeof(Heightmap), "GetBiomeColor", [typeof(float), typeof(float)]);
    (int, int, int, int) State() => (Owned(getBiome, i => i.Prefixes), Owned(haveBiome, i => i.Postfixes), Owned(getColor, i => i.Prefixes), Grid.Active);
    void Apply(bool enabled, int precision)
    {
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = enabled, BiomePrecision = precision };
      BC.PatchHeightmap();
      BC.PatchBiomeColor();
    }
    Apply(true, 0);
    C(State() == (0, 0, 0, 0), $"precision 0: Heightmap.GetBiome, HaveBiome and GetBiomeColor stay vanilla {State()}");
    Apply(true, 3);
    C(State() == (1, 1, 1, 3), $"precision 3: one GetBiome prefix, one HaveBiome postfix, one GetBiomeColor prefix, builds sample 3 {State()}");
    Apply(true, 1);
    C(State() == (1, 1, 1, 1), $"3 -> 1 at run time: the same patches, new builds sample 1 {State()}");
    Apply(true, 9);
    C(State() == (1, 1, 1, Grid.MaxPrecision), $"a stored 9 is clamped to {Grid.MaxPrecision} {State()}");
    Grid.Add(new BiomeSector[4], new Grid(1, new BiomeSector[4].Select(_ => BC.PlainSectors[1]).ToArray()));
    Apply(false, 3);
    C(State() == (0, 0, 0, 0) && Grid.Count == 0, $"a world without Better Continents: everything unpatched and the grids dropped {State()}");
    C(BC.EffectiveBiomePrecision(new BC.BetterContinentsSettings { EnabledForThisWorld = true, BiomePrecision = -2 }) == 0, "a negative precision counts as 0");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void AddGarbage(int count)
  {
    for (int i = 0; i < count; i++)
      Grid.Add(new BiomeSector[4], new Grid(1, new BiomeSector[4].Select(_ => BC.PlainSectors[1]).ToArray()));
  }

  static void Table()
  {
    Section("biome precision: the grid table");
    Grid.Clear();
    var keep = Enumerable.Range(0, 50).Select(_ => new BiomeSector[4]).ToArray();
    var grids = keep.Select(_ => new Grid(1, new BiomeSector[4].Select(_ => BC.PlainSectors[2]).ToArray())).ToArray();
    for (int i = 0; i < keep.Length; i++)
      Grid.Add(keep[i], grids[i]);
    AddGarbage(1000);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Grid.Prune();
    Grid.Active = 1;
    bool found = keep.Select((k, i) => ReferenceEquals(Grid.For(k), grids[i])).All(x => x);
    C(found && Grid.Count == keep.Length, $"entries go with their corner arrays and the live ones stay ({Grid.Count} left of {keep.Length + 1000})");
    var again = new Grid(1, new BiomeSector[4].Select(_ => BC.PlainSectors[3]).ToArray());
    Grid.Add(keep[0], again);
    C(ReferenceEquals(Grid.For(keep[0]), again) && Grid.Count == keep.Length, "a second grid for the same array replaces the first");

    // The builder thread adds while the main thread reads.
    int missing = 0;
    var arrays = Enumerable.Range(0, 4000).Select(_ => new BiomeSector[4]).ToArray();
    var writer = Task.Run(() =>
    {
      foreach (var a in arrays)
        Grid.Add(a, new Grid(1, new BiomeSector[4].Select(_ => BC.PlainSectors[4]).ToArray()));
    });
    while (!writer.IsCompleted)
      foreach (var k in keep.Skip(1))
        if (Grid.For(k) == null) Interlocked.Increment(ref missing);
    writer.Wait();
    C(missing == 0 && arrays.All(a => Grid.For(a) != null), "reads while another thread adds never lose an entry");
    Grid.Active = 0;
    Grid.Clear();
    GC.KeepAlive(keep);
  }
}

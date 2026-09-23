// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Control = BetterContinents.BetterContinents.AltBiomeControl;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

internal static partial class Tests
{
  // ------------------------------------------------------------------------------------------------ grid + parity
  // Control's synthetic world: a Meadows disc, forest, mountain, plains, two Ashlands islands, a Deep North band,
  // a 1-cell diagonal Swamp line, speckles, a low swamp, and an island whose Meadows paint runs 60 m out to sea.
  private static void FillGrid(AltBiomeWorldData d)
  {
    var rnd = new System.Random(1234);
    var ocean = Heightmap.BiomeIndex.Ocean;
    for (int y = 0; y < Size; y++)
    {
      for (int x = 0; x < Size; x++)
      {
        float wx = AltBiomeWorldData.MapSpaceToWorldSpace(x), wz = AltBiomeWorldData.MapSpaceToWorldSpace(y);
        if (wx * wx + wz * wz > 110250000f)
        {
          d.PointBiomes[x, y] = ocean;
          d.PointHeights[x, y] = -1000f;
          continue;
        }
        float r = Mathf.Sqrt(wx * wx + wz * wz);
        Heightmap.BiomeIndex b = ocean;
        float h = -20f;
        if (r < 3000f) { b = Heightmap.BiomeIndex.Meadows; h = 35f + r / 100f; }
        if (r >= 3000f && r < 4000f && wx > 0) { b = Heightmap.BiomeIndex.BlackForest; h = 60f; }
        if (Mathf.Abs(wx - 5000f) < 800f && Mathf.Abs(wz) < 800f) { b = Heightmap.BiomeIndex.Mountain; h = 220f; }
        if (Mathf.Abs(wx + 6000f) < 1500f && Mathf.Abs(wz - 2000f) < 1000f) { b = Heightmap.BiomeIndex.Plains; h = 45f; }
        if ((wx + 8000f) * (wx + 8000f) + (wz + 6000f) * (wz + 6000f) < 900f * 900f) { b = Heightmap.BiomeIndex.AshLands; h = 50f; }
        if ((wx - 7000f) * (wx - 7000f) + (wz + 7000f) * (wz + 7000f) < 600f * 600f) { b = Heightmap.BiomeIndex.AshLands; h = 50f; }
        if (wz > 8000f) { b = Heightmap.BiomeIndex.DeepNorth; h = 70f; }
        if (Mathf.Abs(wx - wz) < 8f && r < 2500f) { b = Heightmap.BiomeIndex.Swamp; h = 31f; }
        if (r >= 4000f && r < 6000f && wx < -1000f && wz < 0f && rnd.NextDouble() < 0.02) { b = Heightmap.BiomeIndex.Mistlands; h = 80f; }
        if (Mathf.Abs(wx + 3000f) < 200f && Mathf.Abs(wz + 3000f) < 200f) { b = Heightmap.BiomeIndex.Swamp; h = 29f; }
        float ir = Mathf.Sqrt((wx - 2000f) * (wx - 2000f) + (wz - 6000f) * (wz - 6000f));
        if (ir < 410f) { b = Heightmap.BiomeIndex.Meadows; h = ir < 350f ? 50f : -10f; }
        d.PointBiomes[x, y] = b;
        d.PointHeights[x, y] = h;
      }
    }
    d.PointsGenerated = true;
  }

  private static AltBiomeWorldData ParityTest()
  {
    var a = new AltBiomeWorldData(Size);
    FillGrid(a);
    VanillaGenerateSectors(a);
    var b = new AltBiomeWorldData(Size);
    FillGrid(b);
    Control.BuildSectors(b, new int[Size * Size]);
    Check(a.SectorsCalculated && b.SectorsCalculated, "both paths finished sector calculation");
    CompareSectors(a, b, "BC's build (nothing planted) vs vanilla");
    Note($"parity: {a.Sectors.Count} sectors compared");
    Use(NewSettings());
    var c = new AltBiomeWorldData(Size);
    FillGrid(c);
    Check(!Control.TryBuildPlantedSectors(c) && c.Sectors.Count == 0, "without planting, the GenerateSectors prefix leaves vanilla's own to run");
    return a;
  }

  private static void CompareSectors(AltBiomeWorldData a, AltBiomeWorldData b, string label)
  {
    Check(a.Sectors.Count == b.Sectors.Count, $"{label}: sector count {a.Sectors.Count} vs {b.Sectors.Count}");
    if (a.Sectors.Count != b.Sectors.Count) return;
    var ia = new Dictionary<BiomeSector, int>();
    var ib = new Dictionary<BiomeSector, int>();
    for (int i = 0; i < a.Sectors.Count; i++) { ia[a.Sectors[i]] = i; ib[b.Sectors[i]] = i; }
    int bad = 0;
    for (int i = 0; i < a.Sectors.Count && bad < 5; i++)
    {
      var x = a.Sectors[i];
      var y = b.Sectors[i];
      bool same = x.Biome == y.Biome && x.EdgeCount == y.EdgeCount && x.Center.Equals(y.Center) && x.Min.Equals(y.Min) && x.Max.Equals(y.Max)
                  && x.MinZone.x == y.MinZone.x && x.MinZone.y == y.MinZone.y && x.MaxZone.x == y.MaxZone.x && x.MaxZone.y == y.MaxZone.y
                  && x.HeightMin == y.HeightMin && x.HeightMax == y.HeightMax && x.HeightAvg == y.HeightAvg && x.DistanceFromCenter == y.DistanceFromCenter
                  && x.Neighbors.Select(n => ia[n]).SequenceEqual(y.Neighbors.Select(n => ib[n]));
      if (!same) { bad++; Check(false, $"{label}: sector {i} differs ({x.Biome} e{x.EdgeCount} vs {y.Biome} e{y.EdgeCount})"); }
    }
    Check(bad == 0, $"{label}: every sector field and neighbour list");
    int pointBad = 0;
    for (int yy = 0; yy < a.Size && pointBad == 0; yy++)
      for (int xx = 0; xx < a.Size; xx++)
        if (ia[a.PointSectors[xx, yy]] != ib[b.PointSectors[xx, yy]]) { pointBad++; break; }
    Check(pointBad == 0, $"{label}: point -> sector mapping");
    bool lists = true;
    foreach (var biome in a.Biomes.Keys)
    {
      var ta = a.Biomes[biome];
      var tb = b.Biomes[biome];
      lists &= ta.Sectors.Select(s => ia[s]).SequenceEqual(tb.Sectors.Select(s => ib[s]))
               && ta.AllPoints.Select(p => (p.x, p.y)).SequenceEqual(tb.AllPoints.Select(p => (p.x, p.y)))
               && ta.AllPointsAboveSeaLevel.Select(p => (p.x, p.y)).SequenceEqual(tb.AllPointsAboveSeaLevel.Select(p => (p.x, p.y)));
    }
    Check(lists, $"{label}: every biome's sector list order, AllPoints and AllPointsAboveSeaLevel");
  }

  // ------------------------------------------------------------------------------------------------ per-sector info
  private static void InfoTest(AltBiomeWorldData a)
  {
    Control.EnsureSectorInfo(a);
    long total = Control.Info.Values.Sum(i => (long)i.Area);
    var seen = new HashSet<BiomeSector>();
    int seedBad = 0;
    for (int y = 0; y < Size; y++)
    {
      for (int x = 0; x < Size; x++)
      {
        var s = a.PointSectors[x, y];
        if (seen.Add(s))
        {
          var i = Control.GetInfo(s);
          if (i.SeedX != x || i.SeedY != y) seedBad++;
        }
      }
    }
    Check(total == (long)Size * Size, $"sector areas sum to the grid ({total})");
    Check(seedBad == 0, "seed = first point of the sector in scan order");
    var slivers = a.Sectors.Count(s => s.Biome == Heightmap.Biome.Swamp);
    Note($"info: {a.Sectors.Count} sectors, {slivers} swamp pieces from a 1-cell diagonal line");
    Check(slivers > 100, "a 1-cell diagonal line fragments into many 4-connected sectors (audit: fragmentation)");
  }

  // Numbers for the audit: what vanilla's metrics make of typical hand-drawn shapes.
  private static void Evidence(AltBiomeWorldData a, List<AltBiome> alts)
  {
    Control.EnsureSectorInfo(a);
    string Windows(BiomeSector s) => string.Join(", ", alts.Where(x => (x.m_biome & s.Biome) != 0 && s.EdgeCount >= x.m_minEdgeSize && s.EdgeCount < x.m_maxEdgeSize).Select(x => x.m_name));
    var disc = a.Sectors.Where(s => s.Biome == Heightmap.Biome.Meadows).OrderByDescending(s => Control.GetInfo(s).Area).First();
    Note($"evidence: 6 km Meadows disc: edge {disc.EdgeCount}, area {Control.GetInfo(disc).Area}, vanilla h {disc.HeightAvg:0.#}, mean {Control.GetInfo(disc).MeanHeight:0.#}; windows accepting it: [{Windows(disc)}]");
    Check(Windows(disc) == "", "a 6 km hand-drawn region is too big for every vanilla alt-biome window");
    var island = a.PointSectors[G(2000f), G(6000f)];
    Note($"evidence: haloed island: edge {island.EdgeCount}, vanilla h {island.HeightAvg:0.#} (border {island.HeightMin:0.#}..{island.HeightMax:0.#}), mean {Control.GetInfo(island).MeanHeight:0.#}");
    Check(island.HeightAvg < 30f && Control.GetInfo(island).MeanHeight >= 30f, "painted halo: vanilla height fails the 30 m minimum, the mean passes");
    var ash = a.Sectors.First(s => s.Biome == Heightmap.Biome.AshLands);
    Note($"evidence: two Ashlands islands = one global sector: edge {ash.EdgeCount}, centre ({ash.Center.x:0}, {ash.Center.y:0}), distance {ash.DistanceFromCenter:0}");
    var dn = a.Sectors.First(s => s.Biome == Heightmap.Biome.DeepNorth);
    Note($"evidence: Deep North band = one global sector: edge {dn.EdgeCount} (Lantern needs 66-198)");
  }

  private static void CanAddModifierParity(AltBiomeWorldData a, List<AltBiome> alts)
  {
    Use(NewSettings());
    Control.EnsureSectorInfo(a);
    var variants = new List<AltBiome>(alts);
    foreach (var (req, not) in new[] { (Heightmap.Biome.Ocean, Heightmap.Biome.None), (Heightmap.Biome.Meadows, Heightmap.Biome.None), (Heightmap.Biome.None, Heightmap.Biome.BlackForest), (Heightmap.Biome.None, Heightmap.Biome.Swamp), (Heightmap.Biome.None, Heightmap.Biome.Ocean) })
    {
      variants.Add(new AltBiome { m_name = $"test {req}/{not}", m_biome = Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest | Heightmap.Biome.Swamp, m_requireNeighbor = req, m_notNeighbor = not, m_minEdgeSize = 0, m_maxEdgeSize = 100000000, m_minDistanceFromCenter = 0, m_minAvgHeight = -10000f });
    }
    variants.Add(new AltBiome { m_name = "bounds", m_biome = Heightmap.Biome.Meadows, m_aboveWorldX = 100f, m_belowWorldY = 50f, m_minEdgeSize = 0, m_minAvgHeight = -1000f, m_minDistanceFromCenter = 0 });
    var birch = alts.First(x => x.m_name == "Birch Meadows");
    foreach (var s in a.Sectors.Where(s => s.Biome == Heightmap.Biome.Meadows).Take(3))
      s.AltBiomes.Add(birch);
    int compared = 0, mismatched = 0;
    foreach (var s in a.Sectors)
    {
      foreach (var alt in variants)
      {
        bool vanilla = s.CanAddModifier(alt);
        bool mine = Control.CanAddModifier(s, alt);
        compared++;
        if (vanilla != mine && mismatched++ < 5)
          Check(false, $"CanAddModifier differs for {alt.m_name} on {s.Biome} e{s.EdgeCount}: vanilla {vanilla}, BC {mine}");
      }
    }
    foreach (var s in a.Sectors) s.AltBiomes.Clear();
    Check(mismatched == 0, $"CanAddModifier replica agrees with vanilla on {compared} sector/alt pairs");

    var ocean = a.Sectors.First(s => s.Biome == Heightmap.Biome.Ocean);
    var coastal = a.Sectors.First(s => s.Biome == Heightmap.Biome.Meadows && s.Neighbors.Contains(ocean));
    var needOcean = variants.First(v => v.m_requireNeighbor == Heightmap.Biome.Ocean);
    Check(!coastal.CanAddModifier(needOcean), "vanilla: requireNeighbor=Ocean fails even on a coastal sector (HasFlag(None) bug)");
    Use(new BC.AltBiomeSettings { FixNeighbourCheck = true });
    Check(Control.CanAddModifier(coastal, needOcean), "fix: requireNeighbor=Ocean passes on a coastal sector");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ BeginRun / EndRun
  private static void ControlRunTests(AltBiomeWorldData a, List<AltBiome> alts)
  {
    SetAltBiomes(alts);
    var custom = new BC.AltBiomeSettings { ChanceMultiplier = 2f, AmountMultiplier = 2f, EdgeScale = 3f, DistanceScale = 0.5f };
    BC.AltBiomeSettings.TryParseOverrides("Dark Meadows: enabled=false; *: maxheight=900; Wolf Mountain: maxedge=12345", custom.Overrides, out _);
    Use(custom);
    var before = alts.ToDictionary(x => x, x => (x.m_chance, x.m_minAmountSpawned, x.m_maxAmountSpawned, x.m_minEdgeSize, x.m_maxEdgeSize, x.m_minDistanceFromCenter, x.m_maxAvgHeight, x.m_enabled));
    var meadows = a.Biomes[Heightmap.Biome.Meadows].Sectors;
    var creation = a.Sectors.Where(s => s.Biome == Heightmap.Biome.Meadows).ToList();
    meadows.Reverse();
    var run = Control.BeginRun(a);
    Check(meadows.SequenceEqual(creation), "BeginRun restores the creation order of each biome's sector list");
    var mushroom = alts.First(x => x.m_name == "Mushroom");
    var dark = alts.First(x => x.m_name == "Dark Meadows");
    var wolf = alts.First(x => x.m_name == "Wolf Mountain");
    Check(Math.Abs(mushroom.m_chance - 0.4f) < 1e-6 && mushroom.m_minAmountSpawned == 2 && mushroom.m_maxAmountSpawned == 6
          && mushroom.m_minEdgeSize == 198 && mushroom.m_maxEdgeSize == 597 && mushroom.m_minDistanceFromCenter == 250f && mushroom.m_maxAvgHeight == 900f,
      $"multipliers + wildcard applied (chance {mushroom.m_chance}, amount {mushroom.m_minAmountSpawned}-{mushroom.m_maxAmountSpawned}, edge {mushroom.m_minEdgeSize}-{mushroom.m_maxEdgeSize}, dist {mushroom.m_minDistanceFromCenter}, maxh {mushroom.m_maxAvgHeight})");
    Check(!dark.m_enabled && dark.m_minAmountSpawned == 0, "per-alt disable (and min 0 so vanilla does not warn)");
    Check(wolf.m_maxEdgeSize == 12345 && wolf.m_minEdgeSize == 597, "an explicit per-alt value wins over the multipliers");
    Check(Control.RandomPhase, "the random phase is open while vanilla's body runs");
    Control.EndRun(a, run);
    Check(!Control.RandomPhase && alts.All(x => before[x] == (x.m_chance, x.m_minAmountSpawned, x.m_maxAmountSpawned, x.m_minEdgeSize, x.m_maxEdgeSize, x.m_minDistanceFromCenter, x.m_maxAvgHeight, x.m_enabled)),
      "EndRun restores every overridden field and closes the random phase");

    Use(new BC.AltBiomeSettings());
    var run2 = Control.BeginRun(a);
    Check(run2.Changed.Count == 0, "default settings override nothing (vanilla-identical placement)");
    Control.EndRun(a, run2);

    Use(new BC.AltBiomeSettings { UseFixedSeed = true, Seed = 777 });
    Check(Control.PlacementSeed(null!) == 777, "fixed placement seed");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ foundation
  // A planting layer of world-space circles that claims every biome, to test the foundation on its own.
  private sealed class TestPlanting : BC.IAltBiomePlanting
  {
    public readonly List<(float x, float z, float r, string alt)> Circles = [];
    public readonly List<BC.PlantPoint> PointList = [];
    public bool Throw;
    public bool HasPlanting => Circles.Count > 0 || PointList.Count > 0;
    public bool HasPaintedRegions => Circles.Count > 0;
    public void Prepare() { }

    public int GetPlantKey(float mapX, float mapY)
    {
      if (Throw)
        throw new InvalidOperationException("test provider failure");
      float x = (mapX - 0.5f) * TotalSize, z = (mapY - 0.5f) * TotalSize;
      for (int i = Circles.Count - 1; i >= 0; i--)
        if (InCircle(x, z, Circles[i].x, Circles[i].z, Circles[i].r))
          return i + 1;
      return 0;
    }

    public bool Claims(int key, Heightmap.Biome biome) => true;

    public IReadOnlyList<BC.PlantedAltBiome> GetAltBiomes(int key)
    {
      if (key < 1 || key > Circles.Count || Circles[key - 1].alt == "")
        return [];
      return [new BC.PlantedAltBiome(AltBiomeList.m_altBiomes.First(a => a.m_name == Circles[key - 1].alt), false)];
    }

    public IReadOnlyList<BC.PlantPoint> Points => PointList;
    public string DescribeKey(int key) => $"circle {key}";
  }

  private static void CirclePlantingTests(List<AltBiome> alts)
  {
    SetAltBiomes(alts);
    var planting = new TestPlanting();
    planting.Circles.Add((-500f, 1000f, 400f, "Dark Meadows"));  // inside the Meadows disc
    planting.Circles.Add((0f, -3000f, 300f, ""));                // across the Meadows/ocean coast: protected
    planting.Circles.Add((-8000f, -6000f, 300f, "Mushroom"));     // on an Ashlands island: biome mismatch
    Use(NewSettings(), planting);

    var c = new AltBiomeWorldData(Size);
    FillGrid(c);
    Check(Control.TryBuildPlantedSectors(c) && c.SectorsCalculated, "planted sectors built");
    Control.EnsureSectorInfo(c);
    int Key(BiomeSector s) => Control.GetInfo(s).PlantKey;
    var k1 = c.Sectors.Where(s => Key(s) == 1).ToList();
    var k2 = c.Sectors.Where(s => Key(s) == 2).ToList();
    var k3 = c.Sectors.Where(s => Key(s) == 3).ToList();
    Check(k1.Count == 1 && k1[0].Biome == Heightmap.Biome.Meadows, $"circle 1 is one Meadows region ({k1.Count})");
    Check(k2.Select(s => s.Biome).Distinct().Count() >= 2 && k2.Any(s => s.Biome == Heightmap.Biome.Ocean), $"circle 2 splits by biome, including out of the global ocean ({string.Join(",", k2.Select(s => s.Biome))})");
    Check(k3.Count == 1 && k3[0].Biome == Heightmap.Biome.AshLands, "circle 3 splits an Ashlands island out of the world-wide Ashlands sector");
    Check(c.Biomes[Heightmap.Biome.AshLands].Sectors[0] == c.Sectors[0] && Key(c.Sectors[0]) == 0, "the world-wide Ashlands sector stays Sectors[0] (EnvMan reads index 0)");
    int expected = 0;
    for (int y = 0; y < Size; y++)
      for (int x = 0; x < Size; x++)
        if (c.PointBiomes[x, y] == Heightmap.BiomeIndex.Meadows && InCircle(AltBiomeWorldData.MapSpaceToWorldSpace(x), AltBiomeWorldData.MapSpaceToWorldSpace(y), -500f, 1000f, 400f))
          expected++;
    Check(expected == Control.GetInfo(k1[0]).Area && expected > 0, $"planted region area {Control.GetInfo(k1[0]).Area} = planted Meadows points {expected}");
    var meadowRest = c.Sectors.Where(s => s.Biome == Heightmap.Biome.Meadows && Key(s) == 0).OrderByDescending(s => Control.GetInfo(s).Area).First();
    Check(meadowRest.Neighbors.Contains(k1[0]), "the planted region borders the unplanted remainder");

    var run = Control.BeginRun(c);
    Check(!c.Biomes.Values.Any(t => t.Sectors.Any(s => Control.SplitOut.Contains(s))) && Control.SplitOut.Count >= 4,
      "BeginRun hides the split-out planted regions from the random candidates");
    Check(k1[0].AltBiomes.Count == 1 && k1[0].AltBiomes[0].m_name == "Dark Meadows", "planted Dark Meadows is applied before random placement");
    Control.EndRun(c, run);
    Check(c.Biomes[Heightmap.Biome.Meadows].Sectors.Contains(k1[0]), "EndRun puts the planted regions back");
    Check(k2.All(s => s.AltBiomes.Count == 0), "protected (empty) planting gets no alt biome");
    Check(k3[0].AltBiomes.Count == 0, "planted Mushroom on Ashlands refused for the base biome");
    Note("planting: " + string.Join(" | ", Control.LastPlantingNotes));

    Use(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly }, planting);
    var run3 = Control.BeginRun(c);
    Check(!run3.RunVanilla, "PlantedOnly skips vanilla random placement");
    Control.EndRun(c, run3);
    Check(k1[0].AltBiomes.Count == 1, "PlantedOnly still applies planted alt biomes");
    Use(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Off }, planting);
    var run4 = Control.BeginRun(c);
    Control.EndRun(c, run4);
    Check(!run4.RunVanilla && c.Sectors.All(s => s.AltBiomes.Count == 0), "Off clears and places nothing, planted included");
    var off = new AltBiomeWorldData(Size);
    FillGrid(off);
    Check(!Control.TryBuildPlantedSectors(off), "Off: the planting is not even sampled (vanilla regions)");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ colour planting
  // Authored's world: two Meadows islands (one with a Black Forest core), a mountain, a plains island, Deep North
  // in the north and Ashlands in the south.
  private static AltBiomeWorldData ColourWorld()
  {
    var data = new AltBiomeWorldData(Size);
    for (int i = 0; i < Size; i++)
    {
      for (int j = 0; j < Size; j++)
      {
        float x = AltBiomeWorldData.MapSpaceToWorldSpace(j), z = AltBiomeWorldData.MapSpaceToWorldSpace(i);
        Heightmap.Biome b;
        float h;
        if (x * x + z * z > 110250000f) { b = Heightmap.Biome.Ocean; h = -1000f; }
        else if (z > 8000f) { b = Heightmap.Biome.DeepNorth; h = 60f; }
        else if (z < -8500f) { b = Heightmap.Biome.AshLands; h = 40f; }
        else if (InCircle(x, z, 2600, 0, 400)) { b = Heightmap.Biome.BlackForest; h = 50f + (x % 7); }
        else if (InCircle(x, z, 2000, 0, 1500)) { b = Heightmap.Biome.Meadows; h = 40f + (z % 5); }
        else if (InCircle(x, z, -3000, 2000, 800)) { b = Heightmap.Biome.Meadows; h = 38f; }
        else if (InCircle(x, z, 0, -4000, 700)) { b = Heightmap.Biome.Mountain; h = 150f; }
        else if (InCircle(x, z, -5000, -3000, 600)) { b = Heightmap.Biome.Plains; h = 35f + (x % 3); }
        else { b = Heightmap.Biome.Ocean; h = -50f; }
        data.PointBiomes[j, i] = b.ToBiomeIndex();
        data.PointHeights[j, i] = h;
      }
    }
    data.PointsGenerated = true;
    return data;
  }

  private static BetterContinents.ImageMapAltBiome ColourMap()
  {
    Rgba32? Paint(float x, float z)
    {
      bool In(float x0, float x1, float z0, float z1) => x >= x0 && x <= x1 && z >= z0 && z <= z1;
      if (In(1500, 1600, 800, 900)) return new Rgba32(0x12, 0x34, 0x56);            // unknown colour
      if (In(900, 1000, -900, -700)) return new Rgba32(0x2D + 6, 0x46 - 5, 0x13 + 4); // drifted Dark Meadows, separate patch
      if (In(800, 1800, -600, 600)) return Hex("2D4613");                            // Dark Meadows (+ Peaceful, incompatible)
      if (In(2150, 3050, -450, 450)) return Hex("0080C0");                           // Troll Black Forest + Dark Meadows
      if (In(-3900, -2100, 1100, 2900)) return Hex("C030F0");                        // none over island 2 and its sea
      if (In(-800, 800, -4800, -3200)) return Hex("93C7BC");                         // Wolf + Drake Mountain (incompatible)
      if (In(-2000, 2000, 8500, 9500)) return Hex("F2A300");                         // Lantern over part of Deep North
      return null;
    }
    var png = WritePng(Path.Combine(Work, "colour"), 512, Paint, string.Join("\n",
    [
      "Dark Meadows: 2D4613",
      "Peaceful Meadows: 2D4613",
      "Troll Black Forest + Dark Meadows: 0080C0",
      "none: C030F0",
      "Wolf Mountain + Drake Mountain: 93C7BC",
      "Lantern: F2A300",
      "Lantern: at 2000, 1200",
      "Nonexistent Biome: 5AAAC2",
    ]));
    int mark = CapturingLogHandler.Mark();
    var map = BetterContinents.ImageMapAltBiome.Create(png);
    Check(map != null, "alt-biome map created from PNG + legend");
    Check(LogContains(mark, "match no legend colour", "[Warning]"), "unknown colour reported with the nearest legend colour");
    return map!;
  }

  private static void FakeAltBiomes()
  {
    AltBiome A(string name, Heightmap.Biome biome, params string[] incompatible) =>
      new() { m_name = name, m_biome = biome, m_incompatibleAltBiomes = incompatible.ToList() };
    SetAltBiomes(
    [
      A("Lantern", (Heightmap.Biome)607),
      A("Dark Meadows", Heightmap.Biome.Meadows),
      A("Peaceful Meadows", Heightmap.Biome.Meadows, "Bones", "Dark Meadows"),
      A("Troll Black Forest", Heightmap.Biome.BlackForest),
      A("Wolf Mountain", Heightmap.Biome.Mountain, "Drake Mountain", "Fortress Mountain"),
      A("Drake Mountain", Heightmap.Biome.Mountain, "Wolf Mountain", "Fortress Mountain"),
    ]);
  }

  private static void ColourPlantingTests()
  {
    var vanilla = ColourWorld();
    VanillaGenerateSectors(vanilla);
    var map = ColourMap();
    FakeAltBiomes();
    var settings = NewSettings(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly });
    settings.SetAltBiomeMap(map);
    Use(settings);
    Check(Control.Planting is BC.ColourPlanting, "the colour map is the IAltBiomePlanting provider");

    var data = ColourWorld();
    int mark = CapturingLogHandler.Mark();
    Check(BuildSectorsAsPatched(data), "planted regions split out of the regions they were painted on");
    var run = Control.BeginRun(data);
    Check(!run.RunVanilla, "PlantedOnly: the game's random placement is skipped");
    Control.EndRun(data, run);

    BiomeSector At(float x, float z) => data.PointSectors[G(x), G(z)];
    var lantern = AltBiomeList.m_altBiomes.First(a => a.m_name == "Lantern");
    var p1 = At(1300, 0);
    Check(p1.Biome == Heightmap.Biome.Meadows && Mods(p1) == "Dark Meadows" && Control.SplitOut.Contains(p1),
      $"planted meadow gets Dark Meadows only; the stacked incompatible Peaceful Meadows is refused ({Mods(p1)})");
    Check(LogContains(mark, "'Peaceful Meadows'", "incompatible"), "the refused stack is warned");
    var p12 = At(950, -800);
    Check(p12 != p1 && Mods(p12) == "Dark Meadows", "a drifted colour (within tolerance) plants; a separate patch is a separate region");
    var p2 = At(2600, 0);
    Check(p2.Biome == Heightmap.Biome.BlackForest && Mods(p2) == "Troll Black Forest" && Control.PartitionKeys.ContainsKey(p2) && !Control.SplitOut.Contains(p2),
      $"a stack over forest: the fully painted forest keeps its own region and gets Troll Black Forest ({Mods(p2)})");
    var p3 = At(2200, 420);
    Check(p3.Biome == Heightmap.Biome.Meadows && Mods(p3) == "Dark Meadows" && p3 != p1, $"the same stack's meadow ring is its own region with Dark Meadows ({Mods(p3)})");
    var p4 = At(-3000, 2000);
    Check(p4.Biome == Heightmap.Biome.Meadows && p4.AltBiomes.Count == 0 && Control.PlantedSectors.Contains(p4) && !Control.SplitOut.Contains(p4),
      "none: island 2 is a protected planted region (its own region, reused, no alt biome)");
    var vanillaOcean = data.Biomes[Heightmap.Biome.Ocean].Sectors[0];
    var p5 = At(-3000, 2850);
    Check(p5.Biome == Heightmap.Biome.Ocean && p5 != vanillaOcean && Control.SplitOut.Contains(p5) && p5.AltBiomes.Count == 0, "none also takes the sea it covers (split out of the global Ocean)");
    var p6 = At(0, -4000);
    Check(Mods(p6) == "Wolf Mountain", $"incompatible stack: only the first is planted ({Mods(p6)})");
    Check(LogContains(mark, "'Drake Mountain'", "incompatible"), "incompatibility warned");
    var vanillaDeepNorth = data.Biomes[Heightmap.Biome.DeepNorth].Sectors[0];
    var p7 = At(0, 9000);
    Check(p7.Biome == Heightmap.Biome.DeepNorth && p7 != vanillaDeepNorth && Mods(p7) == "Lantern", "Lantern splits the world-wide Deep North sector");
    Check(At(0, 9800) == vanillaDeepNorth && vanillaDeepNorth.AltBiomes.Count == 0, "unpainted Deep North stays the world-wide sector, unmodified");
    Check(data.Biomes[Heightmap.Biome.DeepNorth].Sectors[0] == vanillaDeepNorth && data.Sectors.IndexOf(vanillaDeepNorth) == 1,
      "Biomes[DeepNorth].Sectors[0] is still the world-wide sector (EnvMan)");
    var p9 = At(2000, 1200);
    Check(Mods(p9) == "Lantern" && Control.Pinned.ContainsKey(p9) && !Control.PartitionKeys.ContainsKey(p9),
      "the point plant gives Lantern to the whole unplanted remainder of island 1");
    Check(At(1550, 850) == p9, "pixels of an unknown colour stay unplanted");
    var plains = At(-5000, -3000);
    var vplains = vanilla.PointSectors[G(-5000), G(-3000)];
    Check(plains.AltBiomes.Count == 0 && plains.EdgeCount == vplains.EdgeCount && plains.Center == vplains.Center && plains.HeightAvg == vplains.HeightAvg
          && plains.DistanceFromCenter == vplains.DistanceFromCenter && data.Sectors.IndexOf(plains) == vanilla.Sectors.IndexOf(vplains),
      "an untouched region keeps vanilla's index and statistics exactly");
    Check(LogContains(mark, "Nonexistent Biome", "does not exist"), "an unknown alt-biome name is warned");
    Check(At(5000, 5000) == vanillaOcean && vanillaOcean.AltBiomes.Count == 0, "unplanted ocean untouched");
    Check(data.Sectors.Where(s => s.AltBiomes.Count > 0).All(s => Control.PlantedSectors.Contains(s)), "PlantedOnly: no alt biome anywhere that was not planted");
    Check(lantern.Sectors.Count == 2 && lantern.Sectors.Contains(p7) && lantern.Sectors.Contains(p9), "AltBiome.Sectors bookkeeping matches");
    Check(Control.SplitOut.All(s => data.Sectors.Contains(s) && s.BiomeType.Sectors.Contains(s) && s.EdgeCount > 0), "split-out regions are registered and have statistics");
    int vanillaCount = vanilla.Sectors.Count;
    Check(data.Sectors.Count == vanillaCount + Control.SplitOut.Count && data.Sectors.Skip(vanillaCount).All(Control.SplitOut.Contains),
      $"vanilla regions keep their indices; split-out regions are appended ({Control.SplitOut.Count})");
    bool consistent = true;
    for (int y = 0; y < Size && consistent; y++)
      for (int x = 0; x < Size; x++)
        if (data.PointSectors[x, y].Biome != data.PointBiomes[x, y].ToBiome()) { consistent = false; break; }
    Check(consistent, "every point's region has the point's biome");
    var before = data.Sectors.Select(Mods).ToList();
    var again = Control.BeginRun(data);
    Control.EndRun(data, again);
    Check(before.SequenceEqual(data.Sectors.Select(Mods)) && lantern.Sectors.Count == 2, "re-running placement is idempotent");

    // Random mode (authored's "Elsewhere"): the game's placement runs, but never on planted land.
    settings.AltBiomes = new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Random };
    Use(settings);
    var randomRun = Control.BeginRun(data);
    Check(randomRun.RunVanilla, "Random: the game's random placement runs");
    Check(Control.SplitOut.All(s => !s.BiomeType.Sectors.Contains(s)), "split-out regions are hidden from the per-biome lists during it");
    Check(data.Biomes[Heightmap.Biome.Meadows].Sectors.Contains(p9) && data.Biomes[Heightmap.Biome.BlackForest].Sectors.Contains(p2),
      "point-planted and reused regions stay in the lists (the shuffle is unchanged)");
    Check(Control.CanAddModifierOverride(p9, lantern) == false && Control.CanAddModifierOverride(p2, lantern) == false && Control.CanAddModifierOverride(p4, lantern) == false,
      "the CanAddModifier gate refuses planted regions");
    Check(Control.CanAddModifierOverride(plains, lantern) == null, "unplanted regions are left to vanilla's own test");
    Control.EndRun(data, randomRun);
    Check(Control.SplitOut.All(s => s.BiomeType.Sectors.Contains(s)) && Control.CanAddModifierOverride(p2, lantern) == null, "after it: lists restored, gate inert");
    var meadowList = data.Biomes[Heightmap.Biome.Meadows].Sectors;
    Check(meadowList.Skip(meadowList.Count - Control.SplitOut.Count(s => s.Biome == Heightmap.Biome.Meadows)).All(Control.SplitOut.Contains),
      "split-out regions come back at the end of their lists");

    // Authored's "Never Random" is an override now: it stops random placement, planting still works.
    settings.AltBiomes = new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Random };
    BC.AltBiomeSettings.TryParseOverrides("Lant*: enabled=false", settings.AltBiomes.Overrides, out _);
    Use(settings);
    var nr = Control.BeginRun(data);
    Check(!lantern.m_enabled && p7.AltBiomes.Contains(lantern) && p9.AltBiomes.Contains(lantern),
      "'Lant*: enabled=false' keeps Lantern out of random placement while planted Lantern stays");
    Control.EndRun(data, nr);
    Check(lantern.m_enabled, "the override is undone after the run");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ split semantics
  private static void SplitSemanticsTests()
  {
    SetAltBiomes(LoadAltBiomes());
    var islands = new List<(float x, float z, float r, Heightmap.Biome b, float h)>
    {
      (-3000f, 0f, 700f, Heightmap.Biome.Meadows, 45f), // A: two separate patches of one colour
      (3000f, 0f, 700f, Heightmap.Biome.Meadows, 45f),  // B: one patch of the same colour
      (0f, -3000f, 700f, Heightmap.Biome.Meadows, 45f), // C: fully painted, one colour
      (0f, 3000f, 700f, Heightmap.Biome.Meadows, 45f),  // D: fully painted, two colours
    };
    AltBiomeWorldData World()
    {
      var d = IslandWorld(islands);
      for (int i = 0; i < Size; i++)
        for (int j = 0; j < Size; j++)
          if (d.PointHeights[j, i] != -1000f && AltBiomeWorldData.MapSpaceToWorldSpace(i) > 8000f)
          {
            d.PointBiomes[j, i] = Heightmap.BiomeIndex.DeepNorth;
            d.PointHeights[j, i] = 60f;
          }
      return d;
    }
    Rgba32? Paint(float x, float z)
    {
      if (InCircle(x, z, -3250, 0, 150) || InCircle(x, z, -2750, 0, 150) || InCircle(x, z, 3000, 0, 200) || InCircle(x, z, 0, -3000, 900))
        return Hex("2D4613"); // Dark Meadows (C's disc also covers 200 m of sea)
      if (InCircle(x, z, 0, 3000, 900))
        return x < 0 ? Hex("2D4613") : Hex("CCFFAC"); // Dark Meadows / Birch Meadows
      if (InCircle(x, z, -3000, 9000, 300) || InCircle(x, z, 3000, 9000, 300))
        return Hex("F2A300"); // Lantern, twice in Deep North
      if (InCircle(x, z, 0, 9500, 300))
        return Hex("C030F0"); // none in Deep North
      return null;
    }
    var png = WritePng(Path.Combine(Work, "split"), 2048, Paint, "Dark Meadows: 2D4613\nBirch Meadows: CCFFAC\nLantern: F2A300\nnone: C030F0\n");
    var settings = NewSettings(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly });
    settings.SetAltBiomeMap(BetterContinents.ImageMapAltBiome.Create(png));
    Use(settings);
    var vanilla = World();
    VanillaGenerateSectors(vanilla);
    var data = World();
    BuildSectorsAsPatched(data);
    var run = Control.BeginRun(data);
    Control.EndRun(data, run);
    BiomeSector At(float x, float z) => data.PointSectors[G(x), G(z)];
    BiomeSector VAt(float x, float z) => vanilla.PointSectors[G(x), G(z)];
    int Idx(BiomeSector s) => data.Sectors.IndexOf(s);

    var a1 = At(-3250, 0);
    var a2 = At(-2750, 0);
    var aRest = At(-3000, 500);
    Check(a1 != a2 && Mods(a1) == "Dark Meadows" && Mods(a2) == "Dark Meadows", "two disconnected patches of one colour in one region become two regions");
    Check(aRest.AltBiomes.Count == 0 && Idx(aRest) == vanilla.Sectors.IndexOf(VAt(-3000, 500)), "the unplanted remainder keeps the vanilla region, at its index");
    var b1 = At(3000, 0);
    Check(b1 != a1 && b1 != a2 && Mods(b1) == "Dark Meadows", "a patch of the same colour in another region is its own region (never merged)");
    var c = At(0, -3000);
    Check(Idx(c) == vanilla.Sectors.IndexOf(VAt(0, -3000)) && Mods(c) == "Dark Meadows" && Control.PartitionKeys.ContainsKey(c) && !Control.SplitOut.Contains(c),
      "a fully planted region is reused: same index, planted, no empty region left behind");
    var dLeft = At(-300, 3000);
    var dRight = At(300, 3000);
    Check(dLeft != dRight && Mods(dLeft) == "Dark Meadows" && Mods(dRight) == "Birch Meadows"
          && (Idx(dLeft) == vanilla.Sectors.IndexOf(VAt(0, 3000)) || Idx(dRight) == vanilla.Sectors.IndexOf(VAt(0, 3000))),
      "a region fully planted with two colours: one reuses it, the other is split out");
    var dn1 = At(-3000, 9000);
    var dn2 = At(3000, 9000);
    var dnNone = At(0, 9500);
    Check(dn1 == dn2 && Mods(dn1) == "Lantern" && dn1 != data.Biomes[Heightmap.Biome.DeepNorth].Sectors[0], "in Deep North all patches of one colour form one region");
    Check(dnNone != dn1 && dnNone.Biome == Heightmap.Biome.DeepNorth && dnNone.AltBiomes.Count == 0 && Control.PlantedSectors.Contains(dnNone),
      "another colour in Deep North is another region");
    // Vanilla always creates the three world-wide sectors, even for a biome the map lacks (Ashlands here).
    Control.EnsureSectorInfo(vanilla);
    var vanillaEmpty = vanilla.Sectors.Where(s => Control.GetInfo(s).Area == 0).Select(s => vanilla.Sectors.IndexOf(s)).ToList();
    Control.EnsureSectorInfo(data);
    var plantedEmpty = data.Sectors.Where(s => Control.GetInfo(s).Area == 0).Select(Idx).ToList();
    Check(plantedEmpty.SequenceEqual(vanillaEmpty) && Control.PlantedSectors.All(s => Control.GetInfo(s).Area > 0),
      $"planting leaves no empty region behind (the only empty ones are vanilla's own: {string.Join(", ", vanillaEmpty.Select(i => vanilla.Sectors[i].Biome))})");
    var dmKey = Control.PartitionKeys[c];
    Check(Control.IgnoredPointsByKey.TryGetValue(dmKey, out var ignored) && ignored > 0 && At(0, -3750) == data.Biomes[Heightmap.Biome.Ocean].Sectors[0],
      $"painted sea under Dark Meadows is left alone ({ignored} points), and counted");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ serialization
  private static void MapSerializationTests()
  {
    var map = ColourMap();
    var block = map.ToBlock();
    var back = BetterContinents.ImageMapAltBiome.FromBlock(block);
    Note($"map {map.Size}^2 = {map.Map.Length} bytes baked to a {block.Length}-byte block");
    Check(back.Map.SequenceEqual(map.Map), "class map survives the run-length round trip");
    string Describe(BetterContinents.ImageMapAltBiome m) =>
      string.Join(" | ", m.Classes.Select(c => $"{c.Label} pin={c.IsPin} none={c.IsNone}")) + " || "
      + string.Join(" | ", m.Pins.Select(p => $"{p.X},{p.Z}->{p.Class}")) + " || " + m.Size + " || " + m.Legend.Length;
    Check(Describe(back) == Describe(map), "colours, names, flags, points, size and legend identical after the round trip");

    var settings = NewSettings();
    settings.SetAltBiomeMap(map);
    var disk = new ZPackage();
    settings.Serialize(disk, false);
    var loaded = BC.BetterContinentsSettings.Load(new ZPackage(disk.GetArray()));
    Check(loaded.EnabledForThisWorld && loaded.HasAltBiomeMap && loaded.AltBiomeMapData!.Map.SequenceEqual(map.Map), "settings round trip keeps the alt-biome map");
    Check(loaded.AltBiomeMapData!.FilePath.EndsWith("altbiomemap.png"), "disk settings keep the file path (AltBiomeMapPath)");
    var net = new ZPackage();
    settings.Serialize(net, true);
    var loadedNet = BC.BetterContinentsSettings.Load(new ZPackage(net.GetArray()));
    Check(loadedNet.HasAltBiomeMap && loadedNet.AltBiomeMapData!.FilePath == "", "network settings carry the map but no path");
    var without = NewSettings();
    var wa = new ZPackage();
    without.Serialize(wa, true);
    var wb = new ZPackage();
    settings.Serialize(wb, true, includeAltBiomes: false);
    Check(wa.GetArray().SequenceEqual(wb.GetArray()), "includeAltBiomes:false == settings without a map (the map is purely additive)");
    var old = BC.BetterContinentsSettings.Load(new ZPackage(wa.GetArray()));
    Check(old.EnabledForThisWorld && !old.HasAltBiomeMap, "settings without the key load with no alt-biome map");

    // A newer block is read (known fields) and saved back unchanged.
    var newer = new ZPackage(block);
    var newerBytes = newer.GetArray();
    newerBytes[0] = 2; // block version 2, same known fields
    var withNewer = BaseSettingsPackage();
    withNewer.Write((int)BC.DataKey.AltBiomeMap);
    withNewer.Write(newerBytes);
    int mark = CapturingLogHandler.Mark();
    var readNewer = BC.BetterContinentsSettings.Load(new ZPackage(withNewer.GetArray()));
    var again = new ZPackage();
    readNewer.Serialize(again, true);
    Check(readNewer.HasAltBiomeMap && readNewer.AltBiomeMapData!.Map.SequenceEqual(map.Map) && LogContains(mark, "newer Better Continents")
          && again.GetArray().SequenceEqual(withNewer.GetArray()), "a newer map block is planted from the fields this build knows and saved back unchanged");
  }

  // ------------------------------------------------------------------------------------------------ server placement
  private static void ServerAssignmentTest(AltBiomeWorldData a, List<AltBiome> alts)
  {
    SetAltBiomes(alts);
    Use(NewSettings());
    Control.EnsureSectorInfo(a);
    var target = a.Sectors.Where(s => s.Biome == Heightmap.Biome.Plains).OrderByDescending(s => s.EdgeCount).First();
    var ti = Control.GetInfo(target);
    var pkg = new ZPackage();
    pkg.Write(1); pkg.Write(0); pkg.Write(0);
    pkg.Write(2);
    pkg.Write(ti.SeedX); pkg.Write(ti.SeedY); pkg.Write((int)Heightmap.Biome.Plains); pkg.Write(ti.Area); pkg.Write(1); pkg.Write("Goblin Plains");
    pkg.Write(ti.SeedX); pkg.Write(ti.SeedY); pkg.Write((int)Heightmap.Biome.Plains); pkg.Write(ti.Area * 2); pkg.Write(1); pkg.Write("Lox Plains"); // area mismatch
    Check(Control.ParseServerAssignment(new ZPackage(pkg.GetArray()), out _, out _, out var entries) && entries.Count == 2, "server placement package parses");
    var local = a.Sectors.First(s => s.Biome == Heightmap.Biome.Meadows);
    local.AddModifier(alts.First(x => x.m_name == "Mushroom"));
    int mark = CapturingLogHandler.Mark();
    Control.ApplyServerAssignment(a, entries);
    Check(target.AltBiomes.Count == 1 && target.AltBiomes[0].m_name == "Goblin Plains", "server placement applied to the sector under its seed point");
    Check(local.AltBiomes.Count == 0, "local-only placement cleared (the server is authoritative)");
    Check(!target.AltBiomes.Any(x => x.m_name == "Lox Plains") && LogContains(mark, "no matching sector here"), "an entry whose area does not match is not applied, and warned");
    var built = Control.BuildServerAssignmentPackage();
    Check(built == null, "no package without a loaded Better Continents world");
    Control.ClearAssignments(a);
  }

  // ------------------------------------------------------------------------------------------------ hashes
  private static void HashTests()
  {
    FakeAltBiomes();
    var map = ColourMap();
    var settings = NewSettings(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly });
    settings.SetAltBiomeMap(map);
    Use(settings);
    var d1 = ColourWorld();
    BuildSectorsAsPatched(d1);
    Control.EndRun(d1, Control.BeginRun(d1));
    int g1 = Control.LastGridHash, p1 = Control.LastAssignmentHash;
    var d2 = ColourWorld();
    BuildSectorsAsPatched(d2);
    Control.EndRun(d2, Control.BeginRun(d2));
    Check(g1 == Control.LastGridHash && p1 == Control.LastAssignmentHash, $"two independent loads give the same hashes (grid {g1:x8}, assignment {p1:x8})");
    var some = d2.Sectors.First(s => s.AltBiomes.Count > 0);
    some.AltBiomes.RemoveAt(0);
    Control.ComputeHashes(d2);
    Check(g1 == Control.LastGridHash && p1 != Control.LastAssignmentHash, "removing one alt biome changes the assignment hash only");
    var vanilla = ColourWorld();
    VanillaGenerateSectors(vanilla);
    Control.ComputeHashes(vanilla);
    Check(Control.LastGridHash != g1, "a different partition (no planting) changes the grid hash");
    Use(NewSettings());
  }
}

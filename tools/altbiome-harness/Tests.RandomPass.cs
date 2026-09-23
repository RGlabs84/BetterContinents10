// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.PixelFormats;
using BC = BetterContinents.BetterContinents;
using Control = BetterContinents.BetterContinents.AltBiomeControl;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

internal static partial class Tests
{
  // Unity's RNG is a native ECall that cannot even be JIT-compiled outside the engine, so vanilla's GenerateAltBiomes
  // body is transcribed below with a seeded managed RNG in its place.
  private static System.Random Rng = new(0);
  private static void InitState(int seed) => Rng = new System.Random(seed);
  private static float RangeF(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

  private static void Shuffle<T>(IList<T> list)
  {
    for (int num = list.Count - 1; num > 0; num--)
    {
      int index = Rng.Next(0, num + 1);
      (list[num], list[index]) = (list[index], list[num]);
    }
  }

  // AltBiomeWorldData.GenerateAltBiomes (AltBiomeWorldData.cs:301-345, 1.0.15), transcribed from the decompile and
  // checked against the IL (Utils.Shuffle from assembly_utils, CanAddModifier via callvirt), with Better Continents'
  // transpiler applied (PlacementSeed in place of GetSeed). CanAddModifier is the real vanilla method with Better
  // Continents' real Harmony prefix applied.
  private static void VanillaBody(AltBiomeWorldData data)
  {
    InitState(Control.PlacementSeed(WorldGenerator.instance) + 920);
    foreach (var altBiome in AltBiomeList.m_altBiomes)
    {
      altBiome.ValidPlacementSectors = 0;
      altBiome.ValidPlacementSectorCombos = 0;
    }
    var valid = new List<AltBiome>();
    foreach (var biome in data.Biomes)
    {
      AltBiomeList.GetValidAltBiomes(ref valid, biome.Key);
      foreach (var v in valid)
        v.ValidPlacementSectorCombos++;
      foreach (var v in valid)
      {
        InitState(unchecked((int)biome.Key + v.m_name.GetStableHashCode() + Control.PlacementSeed(WorldGenerator.instance)));
        Shuffle(biome.Value.Sectors);
        foreach (var sector in biome.Value.Sectors)
        {
          if (v.Sectors.Count < v.m_maxAmountSpawned)
          {
            float num = RangeF(0f, 1f);
            if ((v.Sectors.Count < v.m_minAmountSpawned || v.m_chance >= num) && sector.CanAddModifier(v))
            {
              v.ValidPlacementSectors++;
              sector.AddModifier(v);
            }
          }
        }
      }
    }
  }

  // What Harmony does around GenerateAltBiomes in game: prefix, body if the prefix allows, postfix/finalizer.
  private static void GenerateAltBiomesAsPatched(AltBiomeWorldData data)
  {
    var run = Control.BeginRun(data);
    try
    {
      if (run.RunVanilla)
        VanillaBody(data);
    }
    finally
    {
      Control.EndRun(data, run);
    }
  }

  private static HarmonyLib.Harmony HarmonyInstance;

  private static void PatchCanAddModifier()
  {
    if (HarmonyInstance != null)
      return;
    HarmonyInstance = new HarmonyLib.Harmony("bc.altbiome.harness");
    // Better Continents' real attribute patch on BiomeSector.CanAddModifier, applied the way PatchAll applies it.
    HarmonyInstance.CreateClassProcessor(typeof(BC).GetNestedType("BiomeSectorCanAddModifierPatch", BindingFlags.NonPublic)!).Patch();
    var info = HarmonyLib.Harmony.GetPatchInfo(typeof(BiomeSector).GetMethod("CanAddModifier")!);
    Check(info != null && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod.Name == "Prefix",
      "Harmony binds Better Continents' CanAddModifier prefix (target, __result and the 'modifier' parameter resolve)");
  }

  private static void SetWorldSeed(int seed)
  {
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    wg.m_world = new World { m_seed = seed };
    typeof(WorldGenerator).GetField("m_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, wg);
  }

  private static readonly (string name, Heightmap.Biome biome, int min, int max, float chance, int e0, int e1, float dist, string[] incompatible)[] VanillaLike =
  [
    ("Mushroom", Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest, 1, 3, 0.2f, 66, 199, 500, []),
    ("Lantern", (Heightmap.Biome)607, 3, 5, 0.2f, 66, 199, 500, []),
    ("Dark Meadows", Heightmap.Biome.Meadows, 2, 5, 0.5f, 66, 299, 1900, []),
    ("Peaceful Meadows", Heightmap.Biome.Meadows, 1, 2, 0.2f, 99, 299, 1000, ["Bones", "Dark Meadows"]),
    ("Dandelion Meadows", Heightmap.Biome.Meadows, 1, 3, 0.2f, 66, 199, 500, []),
    ("Raspberry Meadows", Heightmap.Biome.Meadows, 1, 3, 0.2f, 66, 199, 500, []),
    ("Troll Black Forest", Heightmap.Biome.BlackForest, 1, 3, 0.2f, 66, 199, 750, []),
  ];

  private static void VanillaLikeAltBiomes() =>
    SetAltBiomes(VanillaLike.Select(v => new AltBiome
    {
      m_name = v.name, m_biome = v.biome, m_minAmountSpawned = v.min, m_maxAmountSpawned = v.max, m_chance = v.chance,
      m_minEdgeSize = v.e0, m_maxEdgeSize = v.e1, m_minDistanceFromCenter = v.dist, m_minAvgHeight = 30f, m_maxAvgHeight = 10000f,
      m_incompatibleAltBiomes = v.incompatible.ToList(),
    }));

  private static List<(float x, float z, float r, Heightmap.Biome b, float h)> SixtyIslands()
  {
    var islands = new List<(float x, float z, float r, Heightmap.Biome b, float h)>();
    var rnd = new System.Random(7);
    for (int k = 0; k < 60; k++)
    {
      double ang = k * 2.39996;
      float dist = 1300 + 110 * k;
      float r = 130 + rnd.Next(0, 430);
      islands.Add(((float)(Math.Cos(ang) * dist), (float)(Math.Sin(ang) * dist), r, k % 3 == 0 ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows, 40f));
    }
    return islands;
  }

  private static (float x, float z, float r) Extent(AltBiomeWorldData data, BiomeSector sector)
  {
    double cx = 0, cz = 0;
    int n = 0;
    for (int i = 0; i < data.Size; i++)
      for (int j = 0; j < data.Size; j++)
        if (data.PointSectors[j, i] == sector)
        {
          cx += AltBiomeWorldData.MapSpaceToWorldSpace(j);
          cz += AltBiomeWorldData.MapSpaceToWorldSpace(i);
          n++;
        }
    cx /= n;
    cz /= n;
    double maxR = 0;
    for (int i = 0; i < data.Size; i++)
      for (int j = 0; j < data.Size; j++)
        if (data.PointSectors[j, i] == sector)
          maxR = Math.Max(maxR, Math.Sqrt(Math.Pow(AltBiomeWorldData.MapSpaceToWorldSpace(j) - cx, 2) + Math.Pow(AltBiomeWorldData.MapSpaceToWorldSpace(i) - cz, 2)));
    return ((float)cx, (float)cz, (float)maxR);
  }

  // A map that paints discs slightly larger than the regions (painting past the coast is harmless: the sea is not a
  // base biome of these alt biomes).
  private static BetterContinents.ImageMapAltBiome DiscMap(string name, IReadOnlyList<(float x, float z, float r, string hex)> discs, string legend)
  {
    var png = WritePng(Path.Combine(Work, name), 2048, (x, z) =>
    {
      foreach (var d in discs)
        if (InCircle(x, z, d.x, d.z, d.r + 60f))
          return Hex(d.hex);
      return null;
    }, legend);
    return BetterContinents.ImageMapAltBiome.Create(png)!;
  }

  private static void RandomPassTests()
  {
    PatchCanAddModifier();
    SetWorldSeed(424242);
    VanillaLikeAltBiomes();
    var islands = SixtyIslands();

    // Run 1: a Better Continents world without an alt-biome map: the game's placement, untouched.
    Use(NewSettings());
    var w1 = IslandWorld(islands);
    VanillaGenerateSectors(w1);
    GenerateAltBiomesAsPatched(w1);
    int vanillaCount = w1.Sectors.Count;
    var r1 = w1.Sectors.Select(SortedMods).ToList();
    int modified1 = r1.Count(m => m != "");
    Check(modified1 > 5, $"vanilla random placement ran: {modified1} of {vanillaCount} sectors got alt biomes");
    var w1b = IslandWorld(islands);
    VanillaGenerateSectors(w1b);
    GenerateAltBiomesAsPatched(w1b);
    Check(w1b.Sectors.Select(SortedMods).SequenceEqual(r1), "vanilla placement is deterministic with the shimmed RNG (baseline for the comparisons)");

    // Plant Dark Meadows over one meadow island that got something else at random.
    int target = Enumerable.Range(0, vanillaCount).First(i => w1.Sectors[i].Biome == Heightmap.Biome.Meadows && r1[i] != "" && !r1[i].Contains("Dark Meadows"));
    var ext = Extent(w1, w1.Sectors[target]);
    Note($"target sector {target}: Meadows at ({ext.x:0}, {ext.z:0}), r {ext.r:0}, edge {w1.Sectors[target].EdgeCount}, vanilla gave [{r1[target]}]");
    var withMap = NewSettings();
    withMap.SetAltBiomeMap(DiscMap("rand1", [(ext.x, ext.z, ext.r, "2D4613")], "Dark Meadows: 2D4613\n"));
    Use(withMap);
    var w2 = IslandWorld(islands);
    BuildSectorsAsPatched(w2);
    GenerateAltBiomesAsPatched(w2);
    var planted = w2.Sectors.Where(Control.PlantedSectors.Contains).ToList();
    Check(planted.Count == 1 && SortedMods(planted[0]) == "Dark Meadows", $"planted island: exactly [Dark Meadows] ({planted.Count} planted)");
    Check(w2.Sectors.Count == vanillaCount && w2.Sectors[target] == planted[0], "the fully painted island keeps its own region and index (reused; nothing appended or left empty)");
    var r2 = w2.Sectors.Select(SortedMods).ToList();
    foreach (var m in new[] { "Mushroom", "Lantern", "Dandelion Meadows", "Raspberry Meadows", "Troll Black Forest" }.Where(m => !r1[target].Contains(m)))
    {
      var s1 = Enumerable.Range(0, vanillaCount).Where(i => r1[i].Split(',').Contains(m)).ToList();
      var s2 = Enumerable.Range(0, vanillaCount).Where(i => r2[i].Split(',').Contains(m)).ToList();
      Check(s1.SequenceEqual(s2), $"Random mode: random '{m}' lands on exactly the same {s1.Count} sectors as without the map");
    }
    var dark = AltBiomeList.m_altBiomes.First(a => a.m_name == "Dark Meadows");
    Check(dark.Sectors.Count <= dark.m_maxAmountSpawned && dark.Sectors.Contains(planted[0]), $"planted Dark Meadows counts toward its limit ({dark.Sectors.Count}/{dark.m_maxAmountSpawned} including the planted one)");
    Note($"sectors whose alt biomes differ from the unplanted world: {Enumerable.Range(0, vanillaCount).Count(i => r1[i] != r2[i])} of {vanillaCount} (the island, plus knock-on of Dark Meadows' limit and Peaceful's incompatibility)");

    withMap.AltBiomes = new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly };
    Use(withMap);
    var w3 = IslandWorld(islands);
    BuildSectorsAsPatched(w3);
    GenerateAltBiomesAsPatched(w3);
    var mod3 = w3.Sectors.Where(s => s.AltBiomes.Count > 0).ToList();
    Check(mod3.Count == 1 && SortedMods(mod3[0]) == "Dark Meadows", $"PlantedOnly: the planted island is the only alt biome in the world ({mod3.Count})");

    // Plant Dark Meadows 5 times (its maximum) and the game places no more at random.
    var five = Enumerable.Range(0, vanillaCount).Where(i => w1.Sectors[i].Biome == Heightmap.Biome.Meadows).Take(5)
      .Select(i => Extent(w1, w1.Sectors[i])).Select(e => (e.x, e.z, e.r, "2D4613")).ToList();
    var fiveMap = NewSettings();
    fiveMap.SetAltBiomeMap(DiscMap("rand5", five, "Dark Meadows: 2D4613\n"));
    Use(fiveMap);
    var w5 = IslandWorld(islands);
    BuildSectorsAsPatched(w5);
    GenerateAltBiomesAsPatched(w5);
    var planted5 = w5.Sectors.Where(Control.PlantedSectors.Contains).ToList();
    var randomDark = w5.Sectors.Count(s => !Control.PlantedSectors.Contains(s) && s.AltBiomes.Any(a => a.m_name == "Dark Meadows"));
    Check(planted5.Count == 5 && planted5.All(s => Mods(s) == "Dark Meadows") && randomDark == 0,
      $"5 planted Dark Meadows (= its maximum) leave none for random placement (planted {planted5.Count}, random {randomDark})");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ quota
  private static List<(float x, float z, float r, Heightmap.Biome b, float h)> QuotaIslands()
  {
    var list = new List<(float x, float z, float r, Heightmap.Biome b, float h)>();
    for (int k = 0; k < 12; k++)
    {
      double a = k * Math.PI * 2 / 12;
      list.Add(((float)(Math.Cos(a) * 2800), (float)(Math.Sin(a) * 2800), 350f, k % 2 == 0 ? Heightmap.Biome.Meadows : Heightmap.Biome.BlackForest, 45f));
    }
    for (int k = 0; k < 16; k++)
    {
      double a = (k + 0.5) * Math.PI * 2 / 16;
      list.Add(((float)(Math.Cos(a) * 4500), (float)(Math.Sin(a) * 4500), 520f, Heightmap.Biome.Mountain, 80f));
    }
    for (int k = 0; k < 12; k++)
    {
      double a = (k + 0.25) * Math.PI * 2 / 12;
      list.Add(((float)(Math.Cos(a) * 7000), (float)(Math.Sin(a) * 7000), 400f, Heightmap.Biome.Plains, 50f));
    }
    return list;
  }

  // Real 1.0.15 data, with Fortress Mountain's and Lox Plains' chance raised to 1 so random placement always fills
  // them up to their maximum: the test then measures exactly how many slots planting leaves over.
  private static BC.AltBiomeSettings QuotaOptions(BC.AltBiomeMode mode = BC.AltBiomeMode.Random)
  {
    var o = new BC.AltBiomeSettings { Mode = mode };
    BC.AltBiomeSettings.TryParseOverrides("Fortress Mountain: chance=1; Lox Plains: chance=1", o.Overrides, out _);
    return o;
  }

  private static AltBiomeWorldData RunQuotaWorld(List<(float x, float z, float r, Heightmap.Biome b, float h)> islands, BetterContinents.ImageMapAltBiome map, BC.AltBiomeSettings options)
  {
    SetAltBiomes(LoadAltBiomes());
    var settings = NewSettings(options);
    settings.SetAltBiomeMap(map);
    Use(settings);
    var w = IslandWorld(islands);
    BuildSectorsAsPatched(w);
    GenerateAltBiomesAsPatched(w);
    return w;
  }

  private static (int planted, int random) Count(AltBiomeWorldData w, string alt)
  {
    int planted = w.Sectors.Count(s => Control.PlantedSectors.Contains(s) && s.AltBiomes.Any(a => a.m_name == alt));
    int random = w.Sectors.Count(s => !Control.PlantedSectors.Contains(s) && s.AltBiomes.Any(a => a.m_name == alt));
    return (planted, random);
  }

  private static void QuotaTests(List<AltBiome> reference)
  {
    PatchCanAddModifier();
    SetWorldSeed(1717);
    var fortress = reference.First(a => a.m_name == "Fortress Mountain");
    var lox = reference.First(a => a.m_name == "Lox Plains");
    Note($"1.0.15 data: Fortress Mountain {fortress.m_minAmountSpawned}-{fortress.m_maxAmountSpawned}, Lox Plains {lox.m_minAmountSpawned}-{lox.m_maxAmountSpawned}, Dark Meadows {reference.First(a => a.m_name == "Dark Meadows").m_minAmountSpawned}-{reference.First(a => a.m_name == "Dark Meadows").m_maxAmountSpawned}");
    Check(fortress.m_maxAmountSpawned == 2 && lox.m_maxAmountSpawned == 1, "the game allows at most 2 Fortress Mountain and 1 Lox Plains per world");
    var islands = QuotaIslands();
    var mountains = islands.Where(i => i.b == Heightmap.Biome.Mountain).ToList();
    var plains = islands.Where(i => i.b == Heightmap.Biome.Plains).ToList();

    var w0 = RunQuotaWorld(islands, null, QuotaOptions());
    var f0 = Count(w0, "Fortress Mountain");
    var l0 = Count(w0, "Lox Plains");
    var edges = w0.Sectors.Where(s => s.Biome == Heightmap.Biome.Mountain).Select(s => s.EdgeCount).ToList();
    Note($"mountain edge counts {edges.Min()}-{edges.Max()} (Fortress window 199-298); baseline Fortress {f0.random}, Lox {l0.random}");
    Check(f0 == (0, 2) && l0 == (0, 1), "no planting: random placement fills Fortress Mountain to 2 and Lox Plains to 1");

    var one = DiscMap("q1", [(mountains[0].x, mountains[0].z, mountains[0].r, "B697AF")], "Fortress Mountain: B697AF\n");
    var w1 = RunQuotaWorld(islands, one, QuotaOptions());
    var f1 = Count(w1, "Fortress Mountain");
    Check(f1 == (1, 1), $"one planted Fortress Mountain leaves room for exactly one random one (planted {f1.planted}, random {f1.random})");

    var two = DiscMap("q2", [(mountains[0].x, mountains[0].z, mountains[0].r, "B697AF"), (mountains[5].x, mountains[5].z, mountains[5].r, "B697AF")], "Fortress Mountain: B697AF\n");
    var w2 = RunQuotaWorld(islands, two, QuotaOptions());
    var f2 = Count(w2, "Fortress Mountain");
    Check(f2 == (2, 0), $"two planted Fortress Mountain reach the maximum: random placement adds none (planted {f2.planted}, random {f2.random})");

    var loxMap = DiscMap("q3", [(plains[3].x, plains[3].z, plains[3].r, "CC605F")], "Lox Plains: CC605F\n");
    var w3 = RunQuotaWorld(islands, loxMap, QuotaOptions());
    var l3 = Count(w3, "Lox Plains");
    Check(l3 == (1, 0), $"one planted Lox Plains reaches its maximum of 1: no random one (planted {l3.planted}, random {l3.random})");

    // Planting past the maximum is allowed: the author's regions are not capped, random placement just stops.
    var three = DiscMap("q4", mountains.Take(3).Select(m => (m.x, m.z, m.r, "B697AF")).ToList(), "Fortress Mountain: B697AF\n");
    var w4 = RunQuotaWorld(islands, three, QuotaOptions());
    Check(Count(w4, "Fortress Mountain") == (3, 0), "three planted Fortress Mountain are all planted (planting is not capped); random adds none");
    Use(NewSettings());
  }

  // ------------------------------------------------------------------------------------------------ modes
  private static void ModeTests(List<AltBiome> reference)
  {
    PatchCanAddModifier();
    SetWorldSeed(99);
    var islands = QuotaIslands();
    var mountain = islands.First(i => i.b == Heightmap.Biome.Mountain);
    var meadow = islands.First(i => i.b == Heightmap.Biome.Meadows);
    var map = DiscMap("modes", [(mountain.x, mountain.z, mountain.r, "B697AF"), (meadow.x, meadow.z, meadow.r, "2D4613")],
      "Fortress Mountain: B697AF\nDark Meadows: 2D4613\n");

    var random = RunQuotaWorld(islands, map, new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Random });
    int plantedR = random.Sectors.Count(s => Control.PlantedSectors.Contains(s) && s.AltBiomes.Count > 0);
    int randomR = random.Sectors.Count(s => !Control.PlantedSectors.Contains(s) && s.AltBiomes.Count > 0);
    Check(plantedR == 2 && randomR > 0, $"Random: the planted regions carry their alt biomes and the game places others on unplanted land (planted {plantedR}, random {randomR})");

    var plantedOnly = RunQuotaWorld(islands, map, new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly });
    var withAlt = plantedOnly.Sectors.Where(s => s.AltBiomes.Count > 0).ToList();
    Check(withAlt.Count == 2 && withAlt.All(Control.PlantedSectors.Contains) && withAlt.Select(Mods).OrderBy(m => m).SequenceEqual(new[] { "Dark Meadows", "Fortress Mountain" }),
      $"PlantedOnly: only the planted regions have alt biomes ({withAlt.Count})");

    var off = RunQuotaWorld(islands, map, new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Off });
    Check(off.Sectors.All(s => s.AltBiomes.Count == 0) && Control.PlantedSectors.Count == 0 && AltBiomeList.m_altBiomes.All(a => a.Sectors.Count == 0),
      "Off: no alt biome anywhere, planted ones included");
    Use(NewSettings());
  }
}

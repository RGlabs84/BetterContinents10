// Added by Wubarrk on 2026-10-07 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Control = BetterContinents.BetterContinents.AltBiomeControl;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

// The alt-biome grid at world load, made sooner: the points sampled on every core (AltBiomeControl.GeneratePoints) and the
// regions flooded on flat arrays (FloodFill and ComputeStats), each against the game's own code, point for point and field
// for field.
internal static partial class Tests
{
  // GeneratePoints against the game's GenerateBiomePoints. The generator's two questions are answered by a stand-in (the real
  // ones call the engine), a pure function of the point that gives every biome and a height that differs from point to point.
  private static void PointsParityTest()
  {
    // The JIT would inline DUtils.PerlinNoise into the patched methods it compiles, and cannot compile the engine call it holds
    // (an ECall): MonoMod (inside Harmony) marks it not inlinable, as tools/ewd-tests does.
    var triple = typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple")!;
    var current = triple.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    var disable = triple.GetMethod("TryDisableInlining", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(System.Reflection.MethodBase)])!;
    foreach (var noise in typeof(DUtils).GetMethods().Where(m => m.Name == nameof(DUtils.PerlinNoise)))
      disable.Invoke(current, [noise]);
    var harmony = new Harmony("bc.altbiome.harness.points");
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), [typeof(float), typeof(float), typeof(float), typeof(bool)]),
      prefix: new HarmonyMethod(typeof(Tests), nameof(StandInBiome)));
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)),
      prefix: new HarmonyMethod(typeof(Tests), nameof(StandInHeight)));
    try
    {
      SetWorldSeed(1234);
      var vanilla = new World { m_name = "vanilla" };
      AltBiomeWorldData.GenerateBiomePoints(vanilla);
      var a = vanilla.m_biomeData;
      Check(a != null && a.PointsGenerated && a.Size == AltBiomeWorldData.c_textureSize, "the game's GenerateBiomePoints ran with the stand-in generator");
      int ocean = 0, land = 0;
      foreach (var b in a!.PointBiomes)
        if (b == Heightmap.BiomeIndex.Ocean) ocean++; else land++;
      Note($"vanilla grid: {a.Size} x {a.Size}, {land} land points, {ocean} ocean points");
      foreach (int workers in new[] { 1, 3, 32 })
      {
        var ours = new World { m_name = "ours" };
        Control.GeneratePoints(ours, AltBiomeWorldData.c_textureSize, 110250000f, workers);
        var b = ours.m_biomeData;
        var owner = typeof(AltBiomeWorldData).GetField("m_world", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(b);
        Check(b != null && b.PointsGenerated && b.Size == a.Size && ReferenceEquals(owner, ours), $"{workers} worker(s): the grid is made, marked generated and given to its world");
        Check(SamePoints(a, b!), $"{workers} worker(s): every point's biome and height are the game's, bit for bit");
      }
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
    }
  }

  private static bool SamePoints(AltBiomeWorldData a, AltBiomeWorldData b)
  {
    for (int x = 0; x < a.Size; x++)
      for (int y = 0; y < a.Size; y++)
        if (a.PointBiomes[x, y] != b.PointBiomes[x, y] || BitConverter.SingleToInt32Bits(a.PointHeights[x, y]) != BitConverter.SingleToInt32Bits(b.PointHeights[x, y]))
          return false;
    return true;
  }

  private static readonly Heightmap.Biome[] StandInBiomes =
  [
    Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains,
    Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
  ];

  private static bool StandInBiome(float wx, float wy, ref Heightmap.Biome __result)
  {
    // Patches about 300 m across, with a noisy edge.
    int cx = (int)Math.Floor(wx / 300f), cy = (int)Math.Floor(wy / 300f);
    uint h = unchecked((uint)(cx * 73856093) ^ (uint)(cy * 19349663) ^ (uint)BitConverter.SingleToInt32Bits(wx * wy) & 3u);
    __result = StandInBiomes[h % (uint)StandInBiomes.Length];
    return false;
  }

  private static bool StandInHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, ref float __result)
  {
    mask = Color.black;
    __result = (int)biome * 7.25f + wx * 0.0013f - wy * 0.0007f + (float)Math.Sin(wx * 0.01f);
    return false;
  }

  // FloodFill and ComputeStats (the unplanted GenerateSectors replacement) against the game's GenerateSectors, on a grid of
  // many small regions: noisy patches of every biome, so that thousands of regions touch the ocean and each other.
  private static void NoisySectorsParityTest()
  {
    const int size = 1024;
    var a = NoisyGrid(size);
    VanillaGenerateSectors(a);
    var b = NoisyGrid(size);
    Control.FloodFill(b);
    Control.ComputeStats(b);
    Check(a.SectorsCalculated && b.SectorsCalculated, "noisy grid: both finished the regions");
    CompareSectors(a, b, "noisy grid: flat-array flood and statistics vs vanilla");
    var ocean = a.Biomes[Heightmap.Biome.Ocean].Sectors[0];
    Note($"noisy grid: {a.Sectors.Count} regions, the ocean touches {ocean.Neighbors.Count} of them along {ocean.EdgeCount} edge points");
    Check(a.Sectors.Count > 1000 && ocean.Neighbors.Count > 100, "noisy grid: many regions, and an ocean with many neighbours");
  }

  private static AltBiomeWorldData NoisyGrid(int size)
  {
    var d = new AltBiomeWorldData(size);
    var rnd = new System.Random(20261007);
    var cell = new Heightmap.BiomeIndex[size / 8 + 1, size / 8 + 1];
    for (int x = 0; x < cell.GetLength(0); x++)
      for (int y = 0; y < cell.GetLength(1); y++)
        cell[x, y] = StandInBiomes[rnd.Next(StandInBiomes.Length)].ToBiomeIndex();
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
      {
        // Cells of 8 points, their edges frayed by single points of another biome.
        var b = rnd.NextDouble() < 0.03 ? StandInBiomes[rnd.Next(StandInBiomes.Length)].ToBiomeIndex() : cell[x / 8, y / 8];
        d.PointBiomes[x, y] = b;
        d.PointHeights[x, y] = rnd.Next(-40, 200) + (float)rnd.NextDouble();
      }
    d.PointsGenerated = true;
    return d;
  }
}

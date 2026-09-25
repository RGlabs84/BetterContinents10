// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-25 for version-agnostic wording (0.9.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Control = BetterContinents.BetterContinents.AltBiomeControl;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

internal static partial class Tests
{
  private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

  // ------------------------------------------------------------------------------------------------ cache
  private static void CacheTests()
  {
    var settings = NewSettings();
    Use(settings);
    var world = new World { m_seed = 12345, m_worldGenVersion = 2 };
    var fp = BC.BiomeCacheFingerprint.Compute(world);
    Check(fp.Length == 32, "fingerprint is SHA-256 (32 bytes)");
    var path = Path.Combine(Work, "w_biomedatacache.bin");
    void WriteVanilla()
    {
      using var bw = new BinaryWriter(File.Create(path));
      bw.Write(0);
      bw.Write(41);
      bw.Write(16);
      for (int i = 0; i < 256; i++)
      {
        bw.Write((float)i);
        bw.Write((byte)(i % 10));
      }
    }
    WriteVanilla();
    string reason = "";
    bool Matches(byte[] expected) => BC.BiomeCacheFingerprint.Matches(path, expected, out reason);
    Check(!Matches(fp) && reason.Contains("no Better Continents fingerprint"), "a vanilla cache without the trailer is rejected for a BC world");
    Check(!BC.BiomeCacheFingerprint.HasTrailer(path), "a vanilla cache has no trailer (a vanilla world keeps using it)");
    Check(BC.BiomeCacheFingerprint.Append(path, 16, fp) && Matches(fp), "the fingerprinted cache matches");
    Check(new FileInfo(path).Length == BC.BiomeCacheFingerprint.VanillaLength(16) + 40 && BC.BiomeCacheFingerprint.HasTrailer(path),
      "the trailer is 40 bytes (SHA-256, version, \"BCFP\") after vanilla's payload");
    using (var br = new BinaryReader(File.OpenRead(path)))
    {
      br.ReadInt32();
      br.ReadInt32();
      var size = br.ReadInt32();
      Check(size == 16 && br.ReadSingle() == 0f && br.ReadByte() == 0, "vanilla's payload is unchanged in front of the trailer (the game can still read it)");
    }
    Check(!Matches(BC.BiomeCacheFingerprint.Compute(new World { m_seed = 999, m_worldGenVersion = 2 })) && reason.Contains("fingerprint mismatch"), "another seed does not match");
    settings.SeaLevelAdjustment += 0.25f;
    Check(!Matches(BC.BiomeCacheFingerprint.Compute(world)), "changed Better Continents settings do not match");
    settings.SeaLevelAdjustment -= 0.25f;
    Check(Matches(BC.BiomeCacheFingerprint.Compute(world)), "restored settings match again");
    settings.AltBiomes = new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Off, ChanceMultiplier = 3f };
    settings.SetAltBiomeMap(BetterContinents.ImageMapAltBiome.FromBlock(ColourMap().ToBlock()));
    Check(Matches(BC.BiomeCacheFingerprint.Compute(world)), "alt-biome options and the planted map are not part of the fingerprint (they never change the grid)");
    settings.AltBiomes = null;
    settings.SetAltBiomeMap(null);
    Control.CutoffRadius = 5500f;
    Check(!Matches(BC.BiomeCacheFingerprint.Compute(world)), "the grid geometry is part of it (a WorldEdge cut-off changes the grid)");
    Control.CutoffRadius = 0f;
    int mark = CapturingLogHandler.Mark();
    Check(!BC.BiomeCacheFingerprint.Append(path, 16, fp) && Matches(fp) && LogContains(mark, "not fingerprinting"), "Append never stacks a second trailer");
    var bytes = File.ReadAllBytes(path);
    File.WriteAllBytes(path, bytes.Take(bytes.Length - 1).ToArray());
    Check(!Matches(fp) && reason.Contains("unexpected length"), "a truncated cache is rejected, not trusted");
    File.WriteAllBytes(path, bytes.Take(bytes.Length - 4).Concat(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X' }).ToArray());
    Check(!Matches(fp) && reason.Contains("unknown fingerprint format"), "a damaged trailer is rejected");
  }

  // ------------------------------------------------------------------------------------------------ EWS clamp
  private static void GridClampTests()
  {
    var prefix = typeof(BC).GetNestedType("WorldGeneratorBiomeSectorPatch", BindingFlags.NonPublic)!.GetMethod("GetBiomeSectorGridPrefix", Any)!;
    foreach (var radius in new[] { 6000f, 10500f, 15000f })
    {
      int size = (int)Math.Ceiling(2048.0 * radius / 10500.0);
      var data = new AltBiomeWorldData(size);
      // One sector per grid column, so every lookup can be checked exactly.
      var columns = new BiomeSector[size];
      for (int x = 0; x < size; x++)
        columns[x] = new BiomeSector(data, Heightmap.Biome.Meadows);
      for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
          data.PointSectors[x, y] = columns[x];
      data.PointsGenerated = true;
      data.SectorsCalculated = true;
      var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
      wg.m_world = new World { m_biomeData = data };
      // Expand World Size's WorldSpaceToMapSpace for this grid: 12 m points centred on the world.
      int Ews(float w) => (int)((w - 6f) / 12f + size / 2f);
      BiomeSector Lookup(int gx, int gy, out bool ranOriginal)
      {
        var args = new object[] { wg, gx, gy, null! };
        ranOriginal = (bool)prefix.Invoke(null, args)!;
        return ranOriginal ? wg.GetBiomeSector(gx, gy) : (BiomeSector)args[3];
      }
      var positions = new[] { -radius + 50f, -1000f, 0f, 1164f, 1500f, radius * 0.8f, radius - 50f };
      int wrong = 0;
      bool anyOriginal = false;
      foreach (var w in positions)
      {
        int gx = Ews(w);
        var got = Lookup(gx, Ews(0f), out var ran);
        anyOriginal |= ran;
        if (got != columns[Math.Min(Math.Max(gx, 0), size - 1)])
          wrong++;
      }
      Check(wrong == 0, $"radius {radius:0} (grid {size}): every in-world position maps to its own grid column");
      bool threw = false;
      BiomeSector low = null!, high = null!;
      try
      {
        low = Lookup(-5, 0, out _);
        high = Lookup(size + 3000, 0, out _);
      }
      catch (Exception)
      {
        threw = true;
      }
      Check(!threw && low == columns[0] && high == columns[size - 1], $"radius {radius:0}: out-of-grid indices clamp to the real edge, no exception");
      if (size == 2048)
        Check(anyOriginal, "radius 10500: a vanilla-sized grid runs vanilla's own GetBiomeSector untouched");
      else
        Check(!anyOriginal && Control.SectorAtGrid(data, Ews(radius - 50f), 0) == columns[Ews(radius - 50f)], $"radius {radius:0}: the resized grid is looked up by Better Continents' size-aware clamp");
      if (radius == 6000f)
      {
        bool vanillaThrew = false;
        try
        {
          wg.GetBiomeSector(2000, 10);
        }
        catch (IndexOutOfRangeException)
        {
          vanillaThrew = true;
        }
        Check(vanillaThrew, "radius 6000: vanilla's 2047 clamp indexes past a 1171-point grid (EWS issue #26)");
      }
      if (radius == 15000f)
      {
        int gx = Ews(14000f);
        Check(wg.GetBiomeSector(gx, 10) == columns[2047] && columns[2047] != columns[gx] && Lookup(gx, 10, out _) == columns[gx],
          $"radius 15000: vanilla maps x = 14000 m (index {gx}) onto column 2047 (x = {(2047 - size / 2f) * 12f + 6f:0} m); the clamp maps it to its own column");
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ cut-off
  private static void CutoffTest()
  {
    var d = new AltBiomeWorldData(Size);
    FillGrid(d);
    Control.CutoffRadius = 5500f;
    Control.ApplyCutoff(d);
    Control.CutoffRadius = 0f;
    int wrong = 0;
    for (int y = 0; y < Size; y += 7)
    {
      for (int x = 0; x < Size; x += 7)
      {
        float wx = AltBiomeWorldData.MapSpaceToWorldSpace(x), wz = AltBiomeWorldData.MapSpaceToWorldSpace(y);
        bool outside = wx * wx + wz * wz > 5500f * 5500f;
        if (outside && (d.PointBiomes[x, y] != Heightmap.BiomeIndex.Ocean || d.PointHeights[x, y] != -1000f)) wrong++;
        if (!outside && d.PointHeights[x, y] == -1000f) wrong++;
      }
    }
    Check(wrong == 0, "WorldEdge cut-off marks exactly the points beyond the edge as outside-the-world ocean");
    var small = NewSettings(BC.AltBiomeSettings.FromConfig());
    small.WorldSize = 5000f;
    Use(small);
    Check(Control.CutoffRadius == 5500f && Control.FallbackActive && Control.SampledRadius == 5500f, "Configure: World Size 5000 + Edge 500 with Grid WorldEdge cuts the grid at 5500 m");
    small.AltBiomes!.Grid = BC.AltBiomeGridMode.Vanilla;
    Use(small);
    Check(Control.CutoffRadius == 0f && !Control.FallbackActive, "Grid Vanilla keeps the 10500 m disc");
    var noDropOff = NewSettings();
    noDropOff.DisableMapEdgeDropoff = true;
    Use(noDropOff);
    Check(Control.CutoffRadius == 0f && Control.FallbackActive && Control.SampledRadius == 10500f,
      "map edge drop-off disabled (0.8.0 worlds too): the grid keeps 10500 m and GetBiomeSector falls back to the real biome beyond it");
    Use(NewSettings());
    Check(Control.CutoffRadius == 0f && !Control.ExternalGrid, "a standard world: no cut-off, BC owns the grid");
  }

  // ------------------------------------------------------------------------------------------------ Deep North
  private static void DeepNorthTests()
  {
    var useZ = BC.DeepNorthWeather.UseZ;
    var samples = new[] { (x: 0f, y: 30f, z: 12500f), (x: 2000f, y: 50f, z: -9000f), (x: 0f, y: 12500f, z: 30f), (x: -8000f, y: 35f, z: 9000f), (x: 300f, y: 80f, z: 11800f) };
    BC.DeepNorthWeather.UseZ = false;
    Check(samples.All(s => BC.DeepNorthWeather.IsDeepnorth(s.x, s.y, s.z) == WorldGenerator.IsDeepnorth(s.x, s.y)),
      "no biome map: the helper asks IsDeepnorth(x, height) exactly like vanilla, bug included");
    BC.DeepNorthWeather.UseZ = true;
    Check(samples.All(s => BC.DeepNorthWeather.IsDeepnorth(s.x, s.y, s.z) == WorldGenerator.IsDeepnorth(s.x, s.z)),
      "BC world with a biome map: the helper asks IsDeepnorth(x, z), as IsAshlands is asked");
    Check(WorldGenerator.IsDeepnorth(0f, 30f) != WorldGenerator.IsDeepnorth(0f, 12500f), "the choice matters: (0, height 30) and (0, z 12500) answer differently");
    BC.DeepNorthWeather.UseZ = useZ;

    var helper = AccessTools.Method(typeof(BC.DeepNorthWeather), nameof(BC.DeepNorthWeather.IsDeepnorth), [typeof(float), typeof(float), typeof(float)]);
    var vanilla = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth));
    var ashlands = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsAshlands));
    var fx = AccessTools.Field(typeof(Vector3), "x");
    var fy = AccessTools.Field(typeof(Vector3), "y");
    var fz = AccessTools.Field(typeof(Vector3), "z");
    foreach (var (name, local) in new[] { ("UpdateEnvironment", OpCodes.Ldloc_3), ("GetBiome", OpCodes.Ldloc_1) })
    {
      var method = AccessTools.Method(typeof(EnvMan), name);
      var original = PatchProcessor.GetOriginalInstructions(method);
      Check(original.Count(i => i.Calls(vanilla)) == 1, $"EnvMan.{name} (installed IL): one IsDeepnorth call");
      int oi = original.FindIndex(i => i.Calls(vanilla));
      Check(original[oi - 1].LoadsField(fy) && original[oi - 2].opcode == local && original[oi - 3].LoadsField(fx) && original[oi - 4].opcode == local,
        $"EnvMan.{name}: it is IsDeepnorth(position.x, position.y) with position in {local} (matches the decompile)");
      int ai = original.FindIndex(i => i.Calls(ashlands));
      Check(ai >= 0 && original[ai - 1].LoadsField(fz) && original[ai - 3].LoadsField(fx), $"EnvMan.{name}: IsAshlands is asked with (x, z)");
      var rewritten = BC.DeepNorthWeather.Rewrite(original.Select(i => i.Clone()), "EnvMan." + name, out int n);
      int k = rewritten.FindIndex(i => i.Calls(helper));
      Check(n == 1 && k > 5 && rewritten.Count == original.Count + 2 && !rewritten.Any(i => i.Calls(vanilla)),
        $"EnvMan.{name}: the call is rewritten to DeepNorthWeather.IsDeepnorth(x, y, z)");
      Check(rewritten[k - 1].LoadsField(fz) && rewritten[k - 2].opcode == local && rewritten[k - 3].LoadsField(fy) && rewritten[k - 4].opcode == local
            && rewritten[k - 5].LoadsField(fx) && rewritten[k - 6].opcode == local,
        $"EnvMan.{name}: the new sequence is ldloc; ldfld x; ldloc; ldfld y; ldloc; ldfld z; call (same local)");
      Check(rewritten.Count(i => i.Calls(ashlands)) == 1 && rewritten.Take(ai + 1).Select(i => (i.opcode, i.operand)).SequenceEqual(original.Take(ai + 1).Select(i => (i.opcode, i.operand))),
        $"EnvMan.{name}: nothing before the IsDeepnorth call changes");
    }
  }

  // ------------------------------------------------------------------------------------------------ seed transpiler
  private static void SeedTranspilerTest()
  {
    var method = AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateAltBiomes));
    var original = PatchProcessor.GetOriginalInstructions(method);
    var transpiler = typeof(BC).GetNestedType("AltBiomeControlPatch", BindingFlags.NonPublic)!.GetMethod("GenerateAltBiomesTranspiler", Any)!;
    var result = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, [original.Select(i => i.Clone()).ToList()])!).ToList();
    var getSeed = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetSeed));
    var placement = AccessTools.Method(typeof(Control), nameof(Control.PlacementSeed));
    Check(original.Count(i => i.Calls(getSeed)) == 2 && result.Count(i => i.Calls(getSeed)) == 0 && result.Count(i => i.Calls(placement)) == 2 && result.Count == original.Count,
      "both GetSeed calls in GenerateAltBiomes become PlacementSeed (same stack shape)");
  }

  // ------------------------------------------------------------------------------------------------ failures
  private static void FailLoadTests()
  {
    SetAltBiomes(LoadAltBiomes());
    var planting = new TestPlanting { Throw = true };
    planting.Circles.Add((0f, 0f, 500f, "Dark Meadows"));
    Use(NewSettings(), planting);
    var d = new AltBiomeWorldData(Size);
    FillGrid(d);
    ZNet.m_loadError = false;
    string message = "";
    try
    {
      Control.TryBuildPlantedSectors(d);
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    Check(message.Contains("sampling the alt-biome map failed") && message.Contains("test provider failure") && message.Contains("world load was stopped")
          && message.Contains("please report it with this log"),
      "a planting error fails the world load with a clear message: " + message);
    Check(ZNet.m_loadError, "and disables saving on the server or host (ZNet.m_loadError), so the half-loaded world never reaches disk");
    ZNet.m_loadError = false;

    var bad = new BadAltBiomes();
    Use(NewSettings(), bad);
    var d2 = new AltBiomeWorldData(Size);
    FillGrid(d2);
    VanillaGenerateSectors(d2);
    message = "";
    try
    {
      Control.BeginRun(d2);
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    Check(message.Contains("planting the alt biomes failed"), "an error while applying planted alt biomes fails the load too");
    Control.RandomPhase = false;
    ZNet.m_loadError = false;

    var corrupt = BaseSettingsPackage();
    corrupt.Write((int)BC.DataKey.AltBiomeMap);
    corrupt.Write(new byte[] { 1, 0, 0, 0, 5 });
    var s = BC.BetterContinentsSettings.Load(new ZPackage(corrupt.GetArray()));
    var again = new ZPackage();
    s.Serialize(again, true);
    Check(s.EnabledForThisWorld && s.AltBiomeMapError != null && !s.HasAltBiomeMap && again.GetArray().SequenceEqual(corrupt.GetArray()),
      "an unreadable baked map is recorded (not silently dropped) and kept unchanged for the next save");
    BC.Settings = s;
    message = "";
    try
    {
      Control.BeforeVerifyBiomeData(new World { m_name = "corrupt" });
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    Check(message.Contains("reading the alt-biome map failed") && message.Contains("restore the settings from a backup"),
      "loading a world whose planted map cannot be read stops the load, and says how to recover: " + message);
    ZNet.m_loadError = false;
    s.AltBiomes = new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.Off };
    bool threw = false;
    try
    {
      Control.BeforeVerifyBiomeData(new World { m_name = "corrupt" });
    }
    catch (BC.AltBiomeLoadException)
    {
      threw = true;
    }
    Check(!threw, "with mode Off there is nothing to plant, so that world loads");
    ZNet.m_loadError = false;
    Use(NewSettings());
  }

  // A provider whose resolution breaks while planted alt biomes are applied.
  private sealed class BadAltBiomes : BC.IAltBiomePlanting
  {
    public bool HasPlanting => true;
    public bool HasPaintedRegions => false;
    public void Prepare() { }
    public int GetPlantKey(float mapX, float mapY) => 0;
    public bool Claims(int key, Heightmap.Biome biome) => true;
    public IReadOnlyList<BC.PlantedAltBiome> GetAltBiomes(int key) => throw new InvalidOperationException("broken provider");
    public IReadOnlyList<BC.PlantPoint> Points => [new BC.PlantPoint(0f, 0f, 1)];
    public string DescribeKey(int key) => "bad";
  }
}

// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The real export coroutine (WorldExport.Drive + Job.Run) against a fake loaded world: every pass, every file, the
// manifest and the config, then read back through Better Continents' own loaders.
internal static class EndToEnd
{
  static float Target(Vector3 p) => Vanilla(p * 0.004f) - (p.x > 0f ? 0.3f : 0f);
  static float Vanilla(Vector3 q) => 1.0f + 0.6f * Mathf.Sin(q.x * 3f) * Mathf.Cos(q.z * 2f);

  static bool ForestPrefix(Vector3 pos, ref float __result) { __result = Target(pos); return false; }
  static bool FbmPrefix(Vector3 p, ref float __result) { __result = Vanilla(p); return false; }

  static void SetStatic(Type t, string field, object value) =>
    t.GetField(field, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!.SetValue(null, value);

  static object MakeJob(WorldExport.Options o, string dir, WorldGenerator wg)
  {
    // What Job's constructor sets, minus its ZNet / ZoneSystem reads (Unity objects cannot exist offline).
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var job = RuntimeHelpers.GetUninitializedObject(jobType);
    void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
    var probe = new BC.BetterContinentsSettings { ForestScale = 1f };
    probe.ForestScaleFactor = 0.5f;
    Set("O", o);
    Set("Dir", dir);
    Set("Wg", wg);
    Set("World", wg.m_world);
    Set("Settings", BC.Settings);
    Set("BcWorld", false);
    Set("Size", o.Size);
    Set("Total", BC.TotalSize);
    Set("WorldR", BC.WorldRadius);
    Set("TotalR", BC.TotalRadius);
    Set("Sla", M.SeaLevelAdjustment(o.SeaLevel));
    Set("EdgeDropoff", o.EdgeDropoff ?? true);
    Set("ZoneBlend", o.ZoneBlend);
    Set("OnServer", true);
    Set("Role", "host");
    Set("Workers", Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2)));
    Set("SourceForestScale", 1f);
    Set("ImportForestScale", probe.ForestScale);
    Set("ForestScaleText", "0.5");
    Set("SourceForestAmount", 0.5f);
    Set("ForestOverridesAllTrees", false);
    Set("WorldSizeSetting", 10000f);
    Set("EdgeSizeSetting", 500f);
    Set("AltGrid", BC.AltBiomeGridMode.Vanilla);
    Set("Clock", System.Diagnostics.Stopwatch.StartNew());
    Set("Started", DateTime.Now);
    Set("Written", new List<string>());
    Set("Notes", new List<string>());
    Set("Tracked", new List<string>());
    Set("BiomePixels", new long[10]);
    Set("MinMetres", float.NaN);
    Set("MaxMetres", float.NaN);
    Set("LocationsGenerated", true);
    return job;
  }

  // A 2048 x 2048 sector grid of 128-cell blocks, biomes in a pattern, some blocks carrying alt biomes. Returns the
  // legend key each sector should come back with.
  static Func<BiomeSector, string> BuildAltBiomes(World world)
  {
    AltBiome Alt(string name, Heightmap.Biome biomes, params string[] incompatible)
    {
      var a = new AltBiome { m_name = name, m_biome = biomes, m_enabled = true };
      a.m_incompatibleAltBiomes.AddRange(incompatible);
      return a;
    }
    var dark = Alt("Dark Meadows", Heightmap.Biome.Meadows);
    var wolf = Alt("Wolf Mountain", Heightmap.Biome.Mountain);
    var drake = Alt("Drake Mountain", Heightmap.Biome.Mountain, "Wolf Mountain");
    var lantern = Alt("Lantern", Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest | Heightmap.Biome.Mountain);
    var mushroom = Alt("Mushroom", Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest);
    var data = new AltBiomeWorldData(2048);
    var biomes = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Mountain };
    var blocks = new BiomeSector[16, 16];
    var keys = new Dictionary<BiomeSector, string>();
    for (int by = 0; by < 16; by++)
      for (int bx = 0; bx < 16; bx++)
      {
        var biome = biomes[(bx + 2 * by) % 3];
        var sector = new BiomeSector(data, biome);
        var key = "";
        int pick = (bx * 7 + by * 3) % 6;
        if (biome == Heightmap.Biome.Meadows && pick == 0) { sector.AltBiomes.Add(dark); key = "Dark Meadows"; }
        else if (biome == Heightmap.Biome.Meadows && pick == 1) { sector.AltBiomes.Add(lantern); sector.AltBiomes.Add(mushroom); key = "Lantern + Mushroom"; }
        else if (biome == Heightmap.Biome.Mountain && pick == 2) { sector.AltBiomes.Add(wolf); key = "Wolf Mountain"; }
        else if (biome == Heightmap.Biome.Mountain && pick == 3) { sector.AltBiomes.Add(wolf); sector.AltBiomes.Add(drake); key = "Wolf Mountain + !Drake Mountain"; }
        else if (biome == Heightmap.Biome.BlackForest && pick == 4) { sector.AltBiomes.Add(dark); key = "!Dark Meadows"; }
        blocks[bx, by] = sector;
        keys[sector] = key;
        data.Sectors.Add(sector);
      }
    for (int y = 0; y < 2048; y++)
      for (int x = 0; x < 2048; x++)
      {
        data.PointSectors[x, y] = blocks[x / 128, y / 128];
        float wx = AltBiomeWorldData.MapSpaceToWorldSpace((float)x), wz = AltBiomeWorldData.MapSpaceToWorldSpace((float)y);
        data.PointHeights[x, y] = wx * wx + wz * wz > 110250000f ? -1000f : 50f;
      }
    data.PointsGenerated = true;
    data.SectorsCalculated = true;
    world.m_biomeData = data;
    return s => s != null && keys.TryGetValue(s, out var k) ? k : "";
  }

  static bool unloaded;
  static bool CheckWorldPrefix()
  {
    if (unloaded)
      throw new InvalidOperationException("the world was unloaded during the export");
    return false;
  }

  static IEnumerator Drive(object job) =>
    (IEnumerator)typeof(WorldExport).GetMethod("Drive", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [job])!;

  static void SetRunning(bool running) =>
    typeof(WorldExport).GetProperty("IsRunning")!.GetSetMethod(true)!.Invoke(null, [running]);

  public static void Run(string work)
  {
    System.Console.WriteLine("== end to end: the export coroutine on a fake world");
    var harmony = new Harmony("export-e2e");
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetForestFactor)), prefix: new HarmonyMethod(typeof(EndToEnd), nameof(ForestPrefix)));
    harmony.Patch(AccessTools.Method(typeof(DUtils), nameof(DUtils.Fbm), [typeof(Vector3), typeof(int), typeof(float), typeof(float)]), prefix: new HarmonyMethod(typeof(EndToEnd), nameof(FbmPrefix)));
    var jobT = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    harmony.Patch(AccessTools.Method(jobT, "CheckWorld"), prefix: new HarmonyMethod(typeof(EndToEnd), nameof(CheckWorldPrefix)));

    var world = new World { m_name = "E2E World", m_seedName = "E2ESeed", m_seed = 42 };
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    var expectedKey = BuildAltBiomes(world);
    BC.Settings = new BC.BetterContinentsSettings();

    const int n = 2048;
    const float T = 21000f;
    var o = WorldExport.Options.Default();
    o.Size = n;
    o.Paint = true;
    o.Locations = false;   // ZoneSystem is a Unity object
    var dir = Path.Combine(work, "e2e-export");
    var job = MakeJob(o, dir, wg);
    SetRunning(true);
    var drive = Drive(job);
    int frames = 0;
    var phases = new List<string>();
    float lastOverall = -1f;
    bool monotonic = true;
    while (drive.MoveNext())
    {
      frames++;
      if (phases.Count == 0 || phases[^1] != WorldExport.Phase) phases.Add(WorldExport.Phase);
      if (WorldExport.Overall + 1e-3f < lastOverall) monotonic = false;
      lastOverall = WorldExport.Overall;
      Thread.Sleep(1);
    }
    System.Console.WriteLine($"    {frames} frames; phases: {string.Join(" > ", phases)}");
    foreach (var line in LogHandler.Lines.Where(l => l.Contains("World export")).TakeLast(6))
      System.Console.WriteLine("    log " + line);
    Program.C(!WorldExport.IsRunning && WorldExport.Phase == "Done" && WorldExport.LastError == null && WorldExport.LastExportDir == dir, $"the export finishes: phase {WorldExport.Phase}, error {WorldExport.LastError ?? "none"}");
    Program.C(monotonic && WorldExport.Overall == 100f, "overall progress only rises, and ends at 100");
    var expected = new[] { "altbiomemap.png", "altbiomemap.txt", "heightmap.png", "biomemap.png", "biomemap.txt", "forestmap.png", "heatmap.png", "lavamap.png", "mossmap.png", "paintmap.png", "paintmap.txt", "export.cfg", "README.txt", "manifest.json" };
    var present = Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet() : new HashSet<string>();
    var missing = expected.Where(f => !present.Contains(f)).ToList();
    Program.C(missing.Count == 0, "every map, legend and text file is written" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
    Program.C(!present.Any(f => f.EndsWith(".tmp")), "no .tmp files are left");
    Program.C(!present.Contains("locationmap.png"), "no location map without locations");
    if (missing.Count > 0) return;

    // Alt biomes through ImageMapAltBiome, at the game's own grid points.
    var ab = ImageMapAltBiome.Create(Path.Combine(dir, "altbiomemap.png"))!;
    int abBad = 0, abChecked = 0;
    var data = world.m_biomeData;
    for (int gy = 0; gy < 2048; gy += 2)
      for (int gx = 0; gx < 2048; gx += 2)
      {
        if (data.PointHeights[gx, gy] == -1000f) continue;
        float wx = AltBiomeWorldData.MapSpaceToWorldSpace((float)gx), wz = AltBiomeWorldData.MapSpaceToWorldSpace((float)gy);
        int cls = ab.GetClass(wx / T + 0.5f, wz / T + 0.5f);
        var want = expectedKey(data.PointSectors[gx, gy]);
        var got = cls == 0 ? "" : ab.Classes[cls].Names;
        abChecked++;
        if (got != want) { if (abBad++ < 5) System.Console.WriteLine($"    grid ({gx},{gy}) got '{got}' want '{want}'"); }
      }
    Program.C(abBad == 0, $"at {n} px the alt-biome map gives {abChecked - abBad} of {abChecked} grid points their region's alt biomes (exact from 2048 px)");
    var legendText = File.ReadAllText(Path.Combine(dir, "altbiomemap.txt"));
    Program.C(legendText.Contains("!Dark Meadows") && legendText.Contains("Wolf Mountain + !Drake Mountain") && legendText.Contains("Lantern + Mushroom"),
      "the legend forces exactly the names the content rules would refuse (wrong base biome, incompatible)");
    Program.C(new BepInEx.Configuration.ConfigFile(Path.Combine(dir, "export.cfg"), false).Bind("08 BetterContinents.AltBiomes", "Mode", "Random").Value == "PlantedOnly", "export.cfg sets alt biomes to PlantedOnly");

    // Heights through ImageMapFloat, against the zone blend.
    var hm = ImageMapFloat.Create(File.ReadAllBytes(Path.Combine(dir, "heightmap.png")), false)!;
    float worst = 0f;
    for (int r = 0; r < n; r += 3)
      for (int c = 0; c < n; c += 3)
      {
        float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
        if (Mathf.Sqrt(wx * wx + wz * wz) > 10000f) continue;
        int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
        float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
        var b = new[] { TerrainTest.Biome(x0, z0), TerrainTest.Biome(x0 + 64f, z0), TerrainTest.Biome(x0, z0 + 64f), TerrainTest.Biome(x0 + 64f, z0 + 64f) };
        float tx = DUtils.SmoothStep(0f, 1f, (float)((wx - (double)x0) / 64.0)), tz = DUtils.SmoothStep(0f, 1f, (float)((wz - (double)z0) / 64.0));
        float h = b.All(x => x == b[0]) ? TerrainTest.Height(b[0], wx, wz, out _)
          : DUtils.Lerp(DUtils.Lerp(TerrainTest.Height(b[0], wx, wz, out _), TerrainTest.Height(b[1], wx, wz, out _), tx),
                        DUtils.Lerp(TerrainTest.Height(b[2], wx, wz, out _), TerrainTest.Height(b[3], wx, wz, out _), tx), tz);
        float got = M.ValueToMetres(hm.GetValue(wx / T + 0.5f, wz / T + 0.5f), 2f, 0.5f);
        worst = Mathf.Max(worst, Mathf.Abs(got - h));
      }
    Program.C(worst < 0.02f, $"the heightmap rebuilds the blended terrain inside the world (worst {worst * 100:0.##} cm)");

    // Biomes through ImageMapBiome with the written legend.
    var bm = ImageMapBiome.Create(Path.Combine(dir, "biomemap.png"))!;
    int biomeBad = 0;
    for (int r = 0; r < n; r += 2)
      for (int c = 0; c < n; c += 2)
      {
        float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
        if (bm.GetValue(wx / T + 0.5f, wz / T + 0.5f) != TerrainTest.Biome(wx, wz)) biomeBad++;
      }
    Program.C(biomeBad == 0, $"the biome map rebuilds GetBiome at every pixel ({biomeBad} differ)");

    // Forest: additive encoding against BC's ApplyForest with the written map.
    var fm = ImageMapFloat.Create(File.ReadAllBytes(Path.Combine(dir, "forestmap.png")), false)!;
    var s = new BC.BetterContinentsSettings { ForestmapMultiply = 0f, ForestmapAdd = 1f, ForestAmountOffset = 0f };
    typeof(BC.BetterContinentsSettings).GetField("ForestMap", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, fm);
    float forestWorst = 0f;
    int sparser = 0;
    for (int r = 0; r < n; r += 4)
      for (int c = 0; c < n; c += 4)
      {
        var pos = new Vector3(M.PixelToWorld(c, n, T), 0f, M.FileRowToWorldZ(r, n, T));
        var scaled = pos * 0.99999994f;   // the scale a vanilla world's Forest Scale 0.5 gives back
        float vanilla = Vanilla(scaled * 0.01f * 0.4f);
        float target = Mathf.Clamp(Target(pos), M.ForestDense, M.ForestSparse);
        float got = s.ApplyForest(pos.x / T + 0.5f, pos.z / T + 0.5f, vanilla);
        if (M.ForestNormalized(target) < M.ForestNormalized(vanilla) - 1e-4f) { sparser++; continue; }
        forestWorst = Mathf.Max(forestWorst, Mathf.Abs(got - target));
      }
    Program.C(forestWorst < 2e-3f, $"the forest map rebuilds the forest factor wherever it is at least the game's (worst {forestWorst:g3}; {sparser} sparser samples skipped)");

    // Heat and the 8-bit masks decode.
    var heat = ImageMapFloat.Create(File.ReadAllBytes(Path.Combine(dir, "heatmap.png")), false)!;
    float g = WorldGenerator.GetAshlandsOceanGradient(0f, -10400f);
    float back = M.ValueToHeat(heat.GetValue(0.5f, (-10400f) / T + 0.5f), 10f);
    Program.C(g > 0f && Mathf.Abs(back - g) < 0.01f, $"the heat map rebuilds the Ashlands gradient at the southern rim ({g:0.###} vs {back:0.###})");
    Program.C(ImageMapFloat.Create(File.ReadAllBytes(Path.Combine(dir, "lavamap.png")), false) != null
      && ImageMapFloat.Create(File.ReadAllBytes(Path.Combine(dir, "mossmap.png")), false) != null
      && ImageMapPaint.Create(Path.Combine(dir, "paintmap.png")) != null, "lava, moss and paint maps load");

    // The config through BepInEx, and the manifest.
    var cf = new BepInEx.Configuration.ConfigFile(Path.Combine(dir, "export.cfg"), false);
    Program.C(cf.Bind("00 BetterContinents.Debug", "Directory", "").Value == dir.Replace('\\', '/'), "export.cfg points Directory at the export");
    Program.C(cf.Bind("01 BetterContinents.Global", "Skip Default Locations", true).Value == false, "without a location map the game places the locations");
    Program.C(cf.Bind("05 BetterContinents.StartPosition", "Override Start Position", true).Value == false, "no start position override");
    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))))
    {
      var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
      Program.C(expected.All(files.Contains), "manifest.json lists every file, itself included");
      // No game config is bound here, so the New World preset cannot be built: the export must finish anyway and say so.
      var preset = doc.RootElement.GetProperty("preset");
      Program.C(!preset.GetProperty("made").GetBoolean() && (preset.GetProperty("error").GetString() ?? "").Contains("config"),
        $"a preset that cannot be made does not fail the export, and the manifest says why ({preset.GetProperty("error").GetString()})");
      System.Console.WriteLine("    notes: " + string.Join(" | ", doc.RootElement.GetProperty("notes").EnumerateArray().Select(e => e.GetString())));
    }

    // Cancel half way: the partial files and the folder go.
    var dir2 = Path.Combine(work, "e2e-cancel");
    var o2 = WorldExport.Options.Default();
    o2.Size = 1024;
    o2.Locations = false;
    var job2 = MakeJob(o2, dir2, wg);
    SetRunning(true);
    var drive2 = Drive(job2);
    int steps = 0;
    bool sawFile = false;
    while (drive2.MoveNext())
    {
      if (Directory.Exists(dir2) && Directory.GetFiles(dir2).Length > 0) sawFile = true;
      if (sawFile && steps++ == 3) WorldExport.Cancel();
      Thread.Sleep(1);
    }
    Program.C(sawFile, "the cancelled export had written files before the cancel");
    Program.C(WorldExport.Phase == "Cancelled" && !WorldExport.IsRunning, $"cancel ends in 'Cancelled' ({WorldExport.Phase})");
    Program.C(!Directory.Exists(dir2), "a cancelled export leaves no folder behind");

    // A world that goes away mid-export fails cleanly.
    var dir3 = Path.Combine(work, "e2e-unload");
    var job3 = MakeJob(o2, dir3, wg);
    SetRunning(true);
    var drive3 = Drive(job3);
    int k = 0;
    while (drive3.MoveNext())
    {
      if (k++ == 5) unloaded = true;
      Thread.Sleep(1);
    }
    Program.C(WorldExport.Phase == "Failed" && (WorldExport.LastError ?? "").Contains("unloaded") && !Directory.Exists(dir3), $"an unloaded world fails the export and cleans up ({WorldExport.LastError})");
    unloaded = false;
  }
}

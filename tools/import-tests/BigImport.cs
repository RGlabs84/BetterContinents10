// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using M = BetterContinents.WorldExportMath;

namespace ImportTest;

// A world export at Heightmap Amount 81 read back as a preset, as the Directory and as the config; and (BCIMPORT_BIG) an export
// folder of 8192 or 16384 px made into a preset with its memory measured. The folders are written with the export's own row
// writer, so a 16384 px map never exists whole in this process either.
//
// The memory is measured by a sampler of the process's RSS: BUILDER says how much the builder (MakePlan and BuildPreset, what the export's
// preset step runs) added at its peak to what the process held when it began, and what the export window says for that size
// (WorldExport.PresetMemoryMb, which follows these numbers: a process of its own for each run, BCIMPORT_ONLY=1 BCIMPORT_BIG=<size>
// BCIMPORT_ALL=1, and BCIMPORT_NOPAINT=1 for an export without the paint map). Runs of one size differ by about a tenth; run after the other
// checks of this suite, 2048 and 4096 px came out at 40 and 197 MB where a process of its own gave 44 to 57 and 202 to 227.
internal static partial class Program
{
  static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
  static string R(float v) => v.ToString("R", Inv);

  // The metres the test heightmap holds at pixel (x, y) of an n x n picture: a mountain climbing from the sea at the edge to
  // `peak` m in the middle, with a ripple, so no two neighbours are alike but the whole is smooth.
  static float Metres(int x, int y, int n, float peak)
  {
    float u = x / (n - 1f) - 0.5f, v = y / (n - 1f) - 0.5f;
    float r = Mathf.Min(1f, Mathf.Sqrt(u * u + v * v) * 2f);
    float ripple = 0.02f * Mathf.Sin(x * 0.37f) * Mathf.Cos(y * 0.29f);
    return -20f + (peak + 20f) * Mathf.Clamp01(1f - r + ripple);
  }

  // The 16-bit grey that holds `metres` at this amount and sea level.
  static ushort GreyFor(float metres, float amount, float seaLevel) => M.ValueToUShort(M.MetresToValue(metres, amount, seaLevel), out _);

  static void WriteHeightmap(string path, int n, float amount, float seaLevel, float peak)
  {
    WorldExportPng.SaveRows(path, n, 1, 16, (fileRow, row) =>
    {
      // File row 0 is the north edge; the picture is symmetric enough that only the fill's own row matters for the check.
      for (int x = 0; x < n; x++)
      {
        var g = GreyFor(Metres(x, fileRow, n, peak), amount, seaLevel);
        row[2 * x] = (byte)(g >> 8);
        row[2 * x + 1] = (byte)g;
      }
    }, new HeightmapRecord(amount, seaLevel));
  }

  static void WriteConfig(string dir, int n, float worldSize, float edgeSize, float amount, float seaLevel, bool manifest)
  {
    File.WriteAllLines(Path.Combine(dir, "export.cfg"),
    [
      "## A test export at a large Heightmap Amount.",
      "[00 BetterContinents.Debug]", "Enabled = true", "Directory = /somewhere/it/was/exported", "Override version = ",
      "[01 BetterContinents.Global]", $"World Size = {R(worldSize)}", $"Edge Size = {R(edgeSize)}", $"Sea Level Adjustment = {R(seaLevel)}", "Map Edge Drop-off = true",
      "[02 BetterContinents.Heightmap]", $"Heightmap Amount = {R(amount)}", "Heightmap Blend = 1", "Heightmap Add = 0", "Heightmap Mask = 0", "Heightmap Override All = true", "Heightmap Alpha = false",
    ]);
    if (manifest)
      File.WriteAllText(Path.Combine(dir, "manifest.json"),
        $"{{\n  \"format\": \"bc-export/1\",\n  \"worldName\": \"Big\",\n  \"exportedAt\": \"2026-10-06T12:00:00\",\n  \"size\": {n},\n  \"totalSize\": {R(2f * (worldSize + edgeSize))},\n  \"heightmapAmount\": {R(amount)},\n  \"files\": []\n}}");
  }

  static void Amount81()
  {
    Section("a world export at Heightmap Amount 81 (a 16 km mountain) read back: the preset, the Directory and the config");
    const int n = 128;
    const float amount = 81f, seaLevel = 0.5f, peak = 16000f;
    var dir = Path.Combine(root, "Amount81", "export-2026-10-06-12-00-00");
    Directory.CreateDirectory(dir);
    WriteHeightmap(Path.Combine(dir, "heightmap.png"), n, amount, seaLevel, peak);
    WriteConfig(dir, n, 15850f, 500f, amount, seaLevel, manifest: true);
    var before = ConfigState();
    lock (LogHandler.Lines) LogHandler.Lines.Clear();

    // bc_import: the preset.
    var plan = WorldImport.MakePlan(dir);
    C(plan.Applied.Any(l => l.EndsWith("Heightmap Amount = 81")) && !plan.Ignored.Any(l => l.Contains("Heightmap Amount")), "export.cfg's Heightmap Amount = 81 is applied, not ignored");
    var outcome = Task.Run(() => WorldImport.BuildPreset(plan)).Result;
    C(outcome.Error == null && outcome.Warnings.Count == 0, $"the preset is made ({outcome.Error ?? "no warning"})");
    var s = BC.BetterContinentsSettings.Load(plan.PresetPath);
    C(s.HeightmapAmount == 81f && s.SeaLevel == seaLevel, $"the preset reads Heightmap Amount 81, not the config's range of before (81 here: {s.HeightmapAmount}), sea level {s.SeaLevel}");
    C(s.Version == 12 && s.MapsSpanWorldSize && s.WorldSize == 15850f && s.EdgeSize == 500f, $"a 16k-world export (32700 m maps) is a version 12 world ({s.Version}), World Size 15850, Edge Size 500");
    var hm = Map<ImageMapFloat>(s, "heightmap.png");
    C(hm.Record is { } record && record.Amount == 81f && record.SeaLevel == seaLevel && !LogHandler.Has("was made by a world export for"),
      "the heightmap's own record says Amount 81, and the preset reads it at 81: nothing to warn about");
    float step = (M.ValueToMetres(1f, amount, seaLevel) - M.ValueToMetres(0f, amount, seaLevel)) / 65535f;
    float worst = 0f;
    int checkedPixels = 0;
    for (int y = 0; y < n; y += 3)
      for (int x = 0; x < n; x += 3)
      {
        float want = Metres(x, y, n, peak);
        float got = M.ValueToMetres(hm.GetValue(x / (n - 1f), (n - 1 - y) / (n - 1f)), s.HeightmapAmount, s.SeaLevel);
        worst = Mathf.Max(worst, Mathf.Abs(got - want));
        checkedPixels++;
      }
    C(worst <= step * 0.51f + 0.01f, $"{checkedPixels} heights come back within half a grey step ({step:0.###} m) of the written metres (worst {worst:0.###} m), the top of the mountain at {M.ValueToMetres(1f, amount, seaLevel):0} m");

    // The Directory way (a new world made From Config with Directory set to the folder): export.cfg's amount over the config's.
    BC.ConfigMapSourceDir.Value = dir;
    BC.ConfigHeightmapAmount.Value = 1f;
    var values = WorldImport.DirectoryValues();
    C(values != null && values.Get(BC.ConfigHeightmapAmount) == 81f && values.SettingsVersion == null, "Directory set to the folder: the new world's Heightmap Amount is 81 and its version the newest");
    var created = BC.BetterContinentsSettings.Create();
    C(created.HeightmapAmount == 81f && created.HasHeightMap && created.Version == 12, $"a world made From Config with that Directory has Heightmap Amount 81 ({created.HeightmapAmount})");
    BC.ConfigMapSourceDir.Value = "";

    // The config way: bc_import <folder> config.
    var applied = WorldImport.ApplyToConfig(dir);
    C(applied.Error == null && BC.ConfigHeightmapAmount.Value == 81f && BC.ConfigWorldSize.Value == 15850f, $"the config way writes Heightmap Amount 81 into BetterContinents.cfg ({applied.Error ?? BC.ConfigHeightmapAmount.Value.ToString(Inv)})");
    foreach (var kv in before)
      kv.Key.BoxedValue = kv.Value;
    C(BC.ConfigHeightmapAmount.Value == 1f, "(the config put back as it was)");

    // The export's own limits.
    var o = WorldExport.Options.Default();
    o.HeightmapAmount = 81f;
    o.Size = 16384;
    C(o.Validate() == null, "an export of 16384 px at Heightmap Amount 81 is valid");
    o.HeightmapAmount = 81.5f;
    C(o.Validate() != null && o.Validate()!.Contains("81"), $"81.5 is not ({o.Validate()})");
    o.HeightmapAmount = 81f;
    o.Size = 16385;
    C(o.Validate() != null && o.Validate()!.Contains("16384"), $"16385 px is not ({o.Validate()})");
    C(RangeOf(BC.ConfigExportHeightmapAmount) == (0.01f, 81f), $"the Default Heightmap Amount's range is 0.01 to 81 ({RangeOf(BC.ConfigExportHeightmapAmount)})");
    C(RangeOf(BC.ConfigHeightmapAmount) == (0f, 81f), $"the world's Heightmap Amount range is 0 to 81 ({RangeOf(BC.ConfigHeightmapAmount)})");
    C(RangeOf(BC.ConfigExportSize) == (128f, 16384f), $"the Default Size's range is 128 to 16384 ({RangeOf(BC.ConfigExportSize)})");
  }

  static (long Rss, long Hwm) Memory()
  {
    long rss = 0, hwm = 0;
    try
    {
      foreach (var line in File.ReadAllLines("/proc/self/status"))
      {
        if (line.StartsWith("VmRSS:")) rss = long.Parse(line.Substring(6).Trim().Split(' ')[0]);
        else if (line.StartsWith("VmHWM:")) hwm = long.Parse(line.Substring(6).Trim().Split(' ')[0]);
      }
    }
    catch
    {
      // Not Linux: no numbers.
    }
    return (rss, hwm);
  }

  static void BigImport(int n, bool all)
  {
    // BCIMPORT_NOPAINT=1: every map but the paint map (an export with Paint off), to measure what the paint map takes.
    bool paint = Environment.GetEnvironmentVariable("BCIMPORT_NOPAINT") != "1";
    Section($"an export folder of {n} x {n} px made into a preset (the heightmap{(all ? paint ? " and every other map" : " and every other map but the paint map" : " alone")}), memory measured");
    const float amount = 81f, seaLevel = 0.5f, peak = 16000f;
    var dir = Path.Combine(root, "Big", $"export-{n}");
    Directory.CreateDirectory(dir);
    var colours = ImageMapBiome.DefaultColorTable();

    // Memory per phase, from a sampler thread.
    var peaks = new List<(string Phase, double Seconds, long PeakKb)>();
    var clock = Stopwatch.StartNew();
    string phase = "";
    long phasePeak = 0;
    double phaseStart = 0;
    bool stop = false;
    var gate = new object();
    void Phase(string next)
    {
      lock (gate)
      {
        if (phase.Length > 0)
          peaks.Add((phase, clock.Elapsed.TotalSeconds - phaseStart, phasePeak));
        phase = next;
        phasePeak = 0;
        phaseStart = clock.Elapsed.TotalSeconds;
      }
    }
    var sampler = new Thread(() =>
    {
      while (!Volatile.Read(ref stop))
      {
        var rss = Memory().Rss;
        lock (gate)
          if (rss > phasePeak) phasePeak = rss;
        Thread.Sleep(100);
      }
    }) { IsBackground = true };
    var before = Memory();
    sampler.Start();

    Phase("writing the folder");
    WriteHeightmap(Path.Combine(dir, "heightmap.png"), n, amount, seaLevel, peak);
    if (all)
    {
      void Rows(string file, int channels, int bits, Action<int, byte[]> fill) => WorldExportPng.SaveRows(Path.Combine(dir, file), n, channels, bits, fill);
      Rows("forestmap.png", 1, 16, (y, row) => { for (int x = 0; x < n; x++) { var g = (ushort)((x * 40000L / (n - 1)) + (y % 64) * 8); row[2 * x] = (byte)(g >> 8); row[2 * x + 1] = (byte)g; } });
      Rows("heatmap.png", 1, 16, (y, row) => { if (y > n * 3 / 4) for (int x = 0; x < n; x++) { var g = (ushort)Math.Min(65535, (y - n * 3 / 4) * 60000L / (n / 4)); row[2 * x] = (byte)(g >> 8); row[2 * x + 1] = (byte)g; } });
      Rows("lavamap.png", 1, 8, (y, row) => { if (y > n * 3 / 4) for (int x = n * 3 / 4; x < n; x++) row[x] = 200; });
      Rows("mossmap.png", 1, 8, (y, row) => { if (y < n / 4) for (int x = 0; x < n / 4; x++) row[x] = 150; });
      var biomeAt = (Func<int, int, Heightmap.Biome>)((x, y) => HeightWet(x, y, n) ? (x < n / 2 ? (y < n / 2 ? Heightmap.Biome.Meadows : Heightmap.Biome.Plains) : (y < n / 2 ? Heightmap.Biome.BlackForest : Heightmap.Biome.Mountain)) : Heightmap.Biome.Ocean);
      Rows("biomemap.png", 3, 8, (y, row) => { for (int x = 0; x < n; x++) { var c = colours[biomeAt(x, y)]; row[3 * x] = c.r; row[3 * x + 1] = c.g; row[3 * x + 2] = c.b; } });
      if (paint)
        Rows("paintmap.png", 3, 8, (y, row) => { for (int x = 0; x < n; x++) { row[3 * x] = (byte)(x * 255L / (n - 1)); row[3 * x + 1] = (byte)(y * 255L / (n - 1)); row[3 * x + 2] = 128; } });
      Rows("altbiomemap.png", 3, 8, (y, row) =>
      {
        for (int x = 0; x < n; x++)
          if (x >= n / 6 && x < n / 3 && y >= n / 6 && y < n / 3) { row[3 * x] = 0xAA; row[3 * x + 1] = 0x33; row[3 * x + 2] = 0x77; }
          else if (x >= n * 2 / 3 && x < n * 5 / 6 && y >= n * 2 / 3 && y < n * 5 / 6) { row[3 * x] = 0x33; row[3 * x + 1] = 0xAA; row[3 * x + 2] = 0x77; }
      });
      Rows("locationmap.png", 3, 8, (y, row) => { if (y == n / 2) row[3 * (n / 2)] = 255; if (y == n / 4) { row[3 * (n / 4)] = 255; row[3 * (n / 4) + 1] = 153; } });
      File.WriteAllLines(Path.Combine(dir, "biomemap.txt"), ImageMapBiome.DefaultColors.Split('|'));
      if (paint)
        File.WriteAllLines(Path.Combine(dir, "paintmap.txt"), ["# The paint map needs no colour list. Every pixel is used as it is"]);
      File.WriteAllLines(Path.Combine(dir, "altbiomemap.txt"), ["# test legend", "Dark Meadows: AA3377", "Wolf Mountain: 33AA77"]);
      File.WriteAllLines(Path.Combine(dir, "locationmap.txt"), ["StartTemple: 255,0,0", "Eikthyrnir: 255,153,0"]);
    }
    WriteConfig(dir, n, 15850f, 500f, amount, seaLevel, manifest: true);
    long onDisk = Directory.GetFiles(dir, "*.png").Sum(f => new FileInfo(f).Length);

    // What the process holds as the builder begins, to say how much the builder adds.
    long beforeBuild = Memory().Rss;
    Phase("MakePlan and BuildPreset (the builder, as the export's preset step runs it)");
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var plan = WorldImport.MakePlan(dir);
    var outcome = Task.Run(() => WorldImport.BuildPreset(plan)).Result;

    Phase("loading the preset");
    var s = BC.BetterContinentsSettings.Load(plan.PresetPath);
    var hm = Map<ImageMapFloat>(s, "heightmap.png");

    Phase("");
    Volatile.Write(ref stop, true);
    sampler.Join();
    var after = Memory();
    foreach (var p in peaks)
      System.Console.WriteLine($"    {p.Phase,-82} {p.Seconds,7:0.0} s, peak {p.PeakKb / 1024,6} MB");
    System.Console.WriteLine($"    MEASURED {n}{(all ? paint ? " all maps" : " all maps but paint" : " heightmap")}: {clock.Elapsed.TotalSeconds:0.0} s; process RSS {before.Rss / 1024} MB before, peak (VmHWM) {after.Hwm / 1024} MB; "
                             + $"PNGs {onDisk / 1048576.0:0.0} MB on disk, preset {new FileInfo(plan.PresetPath).Length / 1048576.0:0.0} MB");

    var builder = peaks.First(p => p.Phase.StartsWith("MakePlan"));
    System.Console.WriteLine($"    BUILDER {n}{(all ? paint ? " all maps" : " all maps but paint" : " heightmap")}: {builder.Seconds:0.0} s, peak {builder.PeakKb / 1024} MB, {(builder.PeakKb - beforeBuild) / 1024} MB above the {beforeBuild / 1024} MB the process held when it began{(all ? $"; the export window says {WorldExport.PresetMemoryMb(n, paint):0} MB" : "")}");

    C(outcome.Error == null && outcome.Warnings.Count == 0, $"the preset of the {n} px folder is made ({outcome.Error ?? string.Join("; ", outcome.Warnings.DefaultIfEmpty("no warning"))})");
    C(s.HeightmapAmount == 81f && s.Version == 12 && hm != null && hm.Size == n && hm.Record is { Amount: 81f }, $"it loads at Heightmap Amount 81, version {s.Version}, a {hm?.Size} px heightmap with its record");
    float step = (M.ValueToMetres(1f, amount, seaLevel) - M.ValueToMetres(0f, amount, seaLevel)) / 65535f;
    float worst = 0f;
    int count = 0;
    for (int y = 0; y < n; y += 331)
      for (int x = 0; x < n; x += 337)
      {
        float want = Metres(x, y, n, peak);
        float got = M.ValueToMetres(hm!.GetValue(x / (n - 1f), (n - 1 - y) / (n - 1f)), s.HeightmapAmount, s.SeaLevel);
        worst = Mathf.Max(worst, Mathf.Abs(got - want));
        count++;
      }
    C(worst <= step * 0.51f + 0.01f && count > 40, $"{count} sampled heights come back within half a grey step ({step:0.###} m); worst {worst:0.###} m");
    if (all)
    {
      var names = s.LoadedImageMaps().Select(m => m.FileName).ToHashSet();
      string[] want = ["heightmap.png", "biomemap.png", "locationmap.png", "forestmap.png", "heatmap.png", .. (paint ? new[] { "paintmap.png" } : Array.Empty<string>()), "lavamap.png", "mossmap.png", "altbiomemap.png"];
      C(want.All(names.Contains), "every map is in the preset: " + string.Join(", ", want.Where(w => !names.Contains(w)).DefaultIfEmpty("none missing")));
    }
    try { Directory.Delete(dir, true); } catch { }
  }

  // A real export (BCIMPORT_FOLDER: one a dedicated server made at 16384 px): its heightmap, export.cfg and manifest.json, linked into a
  // folder of their own, made into a preset and read back; every sampled pixel of the PNG, decoded here by ImageSharp, against what
  // ImageMapFloat gives for it.
  static void RealImport(string source)
  {
    Section($"a real export, {source}: heightmap, export.cfg and manifest.json made into a preset");
    var dir = Path.Combine(root, "Real", "export-real");
    Directory.CreateDirectory(dir);
    foreach (var file in new[] { "heightmap.png", "export.cfg", "manifest.json" })
      File.CreateSymbolicLink(Path.Combine(dir, file), Path.Combine(source, file));
    var before = Memory();
    var clock = Stopwatch.StartNew();
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var plan = WorldImport.MakePlan(dir);
    var outcome = Task.Run(() => WorldImport.BuildPreset(plan)).Result;
    var s = BC.BetterContinentsSettings.Load(plan.PresetPath);
    var hm = Map<ImageMapFloat>(s, "heightmap.png");
    var after = Memory();
    System.Console.WriteLine($"    {clock.Elapsed.TotalSeconds:0.0} s; process RSS {before.Rss / 1024} MB before, peak (VmHWM) {after.Hwm / 1024} MB; world {s.WorldSize} m + {s.EdgeSize} m edge, version {s.Version}, Heightmap Amount {s.HeightmapAmount}");
    C(outcome.Error == null && outcome.Warnings.Count == 0 && !LogHandler.Has("was made by a world export for"), $"the preset is made, and the heightmap's own record agrees with export.cfg ({outcome.Error ?? "no warning"})");
    C(s.HeightmapAmount == 81f && s.Version == 12 && s.WorldSize == 15850f && s.EdgeSize == 500f, "Heightmap Amount 81, a version 12 world, World Size 15850, Edge Size 500: as the exported world");
    int n = hm!.Size, bad = 0, count = 0;
    float worstValue = 0f;
    using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.L16>(Path.Combine(source, "heightmap.png")))
    {
      C(image.Width == n && image.Height == n, $"the preset's heightmap is {n} x {n}, as the PNG is ({image.Width} x {image.Height})");
      image.ProcessPixelRows(acc =>
      {
        for (int y = 0; y < n; y += 331)
        {
          var row = acc.GetRowSpan(y);
          for (int x = 0; x < n; x += 337)
          {
            float want = row[x].PackedValue / 65535f;
            float got = hm.GetValue(x / (n - 1f), (n - 1 - y) / (n - 1f));
            worstValue = Mathf.Max(worstValue, Mathf.Abs(got - want));
            if (Mathf.Abs(got - want) > 1e-6f) bad++;
            count++;
          }
        }
      });
    }
    C(bad == 0 && count > 1000, $"{count} sampled pixels of the real heightmap read back as the PNG holds them ({bad} differ, worst {worstValue:g3})");
    try { Directory.Delete(dir, true); } catch { }
  }

  // Land (not ocean) in the test pictures' biome layout: inside a disc.
  static bool HeightWet(int x, int y, int n)
  {
    float u = x / (n - 1f) - 0.5f, v = y / (n - 1f) - 0.5f;
    return u * u + v * v < 0.2f;
  }

  // BepInEx's AcceptableValueRange<T> (its type is generic) as a pair of floats.
  static (float Low, float High)? RangeOf(BepInEx.Configuration.ConfigEntryBase entry)
  {
    var range = entry.Description?.AcceptableValues;
    if (range == null)
      return null;
    var type = range.GetType();
    return (Convert.ToSingle(type.GetProperty("MinValue")!.GetValue(range), Inv), Convert.ToSingle(type.GetProperty("MaxValue")!.GetValue(range), Inv));
  }
}

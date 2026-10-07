// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// "dotnet run -c Release -- measure16k <folder> <kind> <compact 0|1> [alpha]": one map kind of a folder of big maps, made as a
// new world makes it, then sampled, saved (the world file, and what a joining client is sent), and read back, with the
// process's resident memory (and its peak since the last reset: /proc/self/clear_refs 5) at every step. One kind per process,
// so each line starts from a clean baseline. Prints one TSV line for the report. Read only on the folder. gen16k.py (beside
// this file) makes a folder of synthetic 16384 px maps to measure.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static (long Rss, long Hwm) Proc()
  {
    long rss = 0, hwm = 0;
    foreach (var line in File.ReadLines("/proc/self/status"))
    {
      if (line.StartsWith("VmRSS:")) rss = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
      else if (line.StartsWith("VmHWM:")) hwm = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
    }
    return (rss, hwm);
  }

  private static void ResetPeak()
  {
    try { File.WriteAllText("/proc/self/clear_refs", "5"); } catch { }
  }

  private static void Settle()
  {
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
  }

  private static int Measure16k(string[] args)
  {
    string folder = args[1], kind = args[2];
    bool compact = args[3] == "1", alpha = args.Length > 4 && args[4] == "alpha";
    var (fileName, field) = kind switch
    {
      "height" => ("heightmap.png", "HeightMap"),
      "forest" => ("forestmap.png", "ForestMap"),
      "rough" => ("roughmap.png", "RoughMap"),
      "heat" => ("heatmap.png", "HeatMap"),
      "lava" => ("lavamap.png", "LavaMap"),
      "moss" => ("mossmap.png", "MossMap"),
      "biome" => ("biomemap.png", "BiomeMap"),
      "location" => ("locationmap.png", "LocationMap"),
      "spawn" => ("spawnmap.png", "SpawnMap"),
      "vegetation" => ("vegetationmap.png", "VegetationMap"),
      "paint" => ("paintmap.png", "PaintMap"),
      "terrain" => ("terrainmap.png", "TerrainMap"),
      "paintnoisy" => ("paintnoisy.png", "PaintMap"),
      "altbiome" => ("altbiomemap.png", "AltBiomeMap"),
      _ => throw new ArgumentException("unknown kind " + kind),
    };
    var path = Path.Combine(folder, fileName);
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, CompactMaps = compact, HeightMapAlpha = alpha };
    var cols = new List<string> { kind, compact ? "compact" : "plain" + (alpha ? "+alpha" : "") };
    void Note(string name, object value) => cols.Add($"{name}={value}");
    var baseline = Proc();
    Note("base_mb", baseline.Rss);
    Note("file_b", new FileInfo(path).Length);

    // 1. create
    ResetPeak();
    long allocBefore = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew();
    ImageMapBase map = kind switch
    {
      "height" => ImageMapFloat.Create(path, s.HeightmapAlphaMode, compact),
      "forest" or "rough" or "heat" or "lava" or "moss" => ImageMapFloat.Create(path, false, compact),
      "biome" => ImageMapBiome.Create(path, compact),
      "location" => ImageMapLocation.Create(path, true),
      "spawn" or "vegetation" => ImageMapSpawn.Create(path, compact),
      "paint" or "paintnoisy" => ImageMapPaint.Create(path, compact),
      "terrain" => ImageMapTerrain.Create(path, false, compact),
      _ => ImageMapAltBiome.Create(path),
    };
    sw.Stop();
    if (map == null)
    {
      System.Console.WriteLine($"{kind}\tFAILED to create");
      return 1;
    }
    Field(field).SetValue(s, map);
    var p1 = Proc();
    Note("create_ms", sw.ElapsedMilliseconds);
    Note("create_peak_mb", p1.Hwm);
    Note("alloc_mb", (GC.GetTotalAllocatedBytes(true) - allocBefore) >> 20);
    Settle();
    Note("create_held_mb", Proc().Rss);
    Note("size", map.Size);

    // 2. sample
    ResetPeak();
    var random = new Random(5);
    sw.Restart();
    float sink = 0;
    for (int i = 0; i < 200000; i++)
    {
      float x = (float)random.NextDouble(), y = (float)random.NextDouble();
      switch (map)
      {
        case ImageMapFloat f: sink += f.GetValue(x, y); break;
        case ImageMapBiome b: sink += (int)b.GetValue(x, y); break;
        case ImageMapSpawn sp: sink += sp.GetEntry(x, y)?.Data.Length ?? 0; break;
        case ImageMapColor c: sink += c.TryGetValue(x, y, out var col) ? col.r : 0; break;
        case ImageMapAltBiome a: sink += a.GetClass(x, y); break;
      }
    }
    sw.Stop();
    if (sink == 12345.678f) System.Console.WriteLine();
    Note("sample_ms_200k", sw.ElapsedMilliseconds);
    Note("sample_peak_mb", Proc().Hwm);
    Note("sample_held_mb", Proc().Rss);

    // 3. serialize: the world file, then what a client is sent
    byte[] disk, net;
    ResetPeak();
    sw.Restart();
    var pkg = new ZPackage();
    s.Serialize(pkg, false, true, 12);
    disk = pkg.GetArray();
    pkg = null;
    sw.Stop();
    Note("save_ms", sw.ElapsedMilliseconds);
    Note("save_peak_mb", Proc().Hwm);
    Note("disk_b", disk.Length);
    ResetPeak();
    sw.Restart();
    pkg = new ZPackage();
    s.Serialize(pkg, true, true, 12);
    net = pkg.GetArray();
    pkg = null;
    sw.Stop();
    Note("net_ms", sw.ElapsedMilliseconds);
    Note("net_peak_mb", Proc().Hwm);
    Note("net_b", net.Length);

    // 4. a client or a server loading it: in a process of its own, so what the creation left behind is not in its numbers
    var file = Path.Combine(Path.GetTempPath(), $"m16k-{kind}-{(compact ? "c" : "p")}-{Environment.ProcessId}.bin");
    using (var stream = File.Create(file))
    {
      stream.Write(BitConverter.GetBytes(disk.Length));
      stream.Write(disk);
    }
    Field(field).SetValue(s, null);
    map = null;
    s = null;
    disk = null;
    net = null;
    try
    {
      var child = Process.Start(new ProcessStartInfo("dotnet", $"\"{typeof(Program).Assembly.Location}\" measure16k-load \"{file}\" {field}")
      {
        RedirectStandardOutput = true,
        UseShellExecute = false,
      })!;
      var line = child.StandardOutput.ReadToEnd().Trim();
      child.WaitForExit();
      foreach (var part in line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
        cols.Add(part);
    }
    finally
    {
      File.Delete(file);
    }
    System.Console.WriteLine(string.Join("\t", cols));
    return 0;
  }

  // The child of measure16k: a world file read as the game reads it (BetterContinentsSettings.Load of the file), then every
  // map sampled at random points, the way a world that has run for a while has read most of its tiles.
  private static int Measure16kLoad(string[] args)
  {
    var cols = new List<string>();
    void Note(string name, object value) => cols.Add($"{name}={value}");
    var baseline = Proc();
    Note("load_base_mb", baseline.Rss);
    var sw = Stopwatch.StartNew();
    var world = BC.BetterContinentsSettings.Load(args[1]);
    sw.Stop();
    Note("load_ms", sw.ElapsedMilliseconds);
    Note("load_peak_mb", Proc().Hwm);
    Settle();
    Note("load_held_mb", Proc().Rss);
    var map = (ImageMapBase)Field(args[2]).GetValue(world);
    Note("load_size", map?.Size ?? -1);
    Note("load_compact", map?.Compact);
    if (map != null && map.Size > 0)
    {
      ResetPeak();
      var random = new Random(5);
      sw.Restart();
      float sink = 0;
      for (int i = 0; i < 200000; i++)
      {
        float x = (float)random.NextDouble(), y = (float)random.NextDouble();
        switch (map)
        {
          case ImageMapFloat f: sink += f.GetValue(x, y); break;
          case ImageMapBiome b: sink += (int)b.GetValue(x, y); break;
          case ImageMapSpawn sp: sink += sp.GetEntry(x, y)?.Data.Length ?? 0; break;
          case ImageMapColor c: sink += c.TryGetValue(x, y, out var col) ? col.r : 0; break;
          case ImageMapAltBiome a: sink += a.GetClass(x, y); break;
        }
      }
      sw.Stop();
      if (sink == 12345.678f) System.Console.WriteLine();
      Note("loaded_sample_ms_200k", sw.ElapsedMilliseconds);
      Note("loaded_sample_peak_mb", Proc().Hwm);
    }
    GC.KeepAlive(world);
    System.Console.WriteLine(string.Join("\t", cols));
    return 0;
  }
}

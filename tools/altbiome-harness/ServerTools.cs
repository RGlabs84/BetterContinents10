// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using BC = BetterContinents.BetterContinents;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

// Tools for the dedicated-server test. They only ever touch the settings file they are given, which must be a copy:
// a world's BetterContinents file is [int32 length][payload], the payload being BetterContinentsSettings.Serialize.
internal static class ServerTools
{
  public static int Run(string mode, string[] args)
  {
    CapturingLogHandler.Echo = true;
    if (args.Length < 1)
    {
      System.Console.WriteLine("missing <settings file>");
      return 2;
    }
    var path = args[0];
    if (path.Contains("/.config/unity3d/IronGate/"))
    {
      System.Console.WriteLine("refusing to touch a real save; work on a copy");
      return 2;
    }
    var settings = Load(path);
    switch (mode)
    {
      case "inspect":
        settings.Dump(s => System.Console.WriteLine("  " + s));
        Histogram(settings);
        return 0;
      case "decode":
        if (settings.AltBiomeMapData is not { } map)
        {
          System.Console.WriteLine("no alt-biome map in these settings");
          return 1;
        }
        map.Dump(s => System.Console.WriteLine("  " + s));
        System.Console.WriteLine($"  planted pixels: {map.Map.Count(b => b != 0)} of {map.Map.Length}");
        return 0;
      case "sealevel":
        var delta = float.Parse(args[1], CultureInfo.InvariantCulture);
        System.Console.WriteLine($"sea level adjustment {settings.SeaLevelAdjustment} -> {settings.SeaLevelAdjustment + delta}");
        settings.SeaLevelAdjustment += delta;
        Save(settings, path);
        return 0;
      case "plant":
        return Plant(settings, path, args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(path)!, "altbiome-source"));
      case "breakmap":
      {
        // The settings as they are, plus an alt-biome map block that cannot be read: the load must stop.
        var pkg = new ZPackage();
        settings.Serialize(pkg, false);
        pkg.Write((int)BC.DataKey.AltBiomeMap);
        pkg.Write(new byte[] { 1, 0, 0, 0, 5 });
        var data = pkg.GetArray();
        using (var bw = new BinaryWriter(File.Create(path + ".tmp")))
        {
          bw.Write(data.Length);
          bw.Write(data);
        }
        File.Move(path + ".tmp", path, true);
        System.Console.WriteLine($"wrote {path} with an unreadable alt-biome map block");
        return 0;
      }
      default:
        return 2;
    }
  }

  private static BC.BetterContinentsSettings Load(string path)
  {
    using var br = new BinaryReader(File.OpenRead(path));
    int count = br.ReadInt32();
    return BC.BetterContinentsSettings.Load(new ZPackage(br.ReadBytes(count)));
  }

  // The same layout BetterContinentsSettings.Save and SaveToSource write, replacing the file in one move.
  private static void Save(BC.BetterContinentsSettings settings, string path)
  {
    var pkg = new ZPackage();
    settings.Serialize(pkg, false);
    var data = pkg.GetArray();
    using (var bw = new BinaryWriter(File.Create(path + ".tmp")))
    {
      bw.Write(data.Length);
      bw.Write(data);
    }
    File.Move(path + ".tmp", path, true);
    System.Console.WriteLine($"saved {path} ({data.Length} bytes of settings)");
  }

  private static readonly Heightmap.Biome[] Biomes =
  [
    Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains,
    Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
  ];

  private static void Histogram(BC.BetterContinentsSettings settings)
  {
    if (!settings.HasBiomeMap)
    {
      System.Console.WriteLine("  no biome map");
      return;
    }
    var counts = new Dictionary<Heightmap.Biome, int>();
    const int n = 256;
    for (int y = 0; y < n; y++)
      for (int x = 0; x < n; x++)
      {
        var b = settings.GetBiomeOverride((x + 0.5f) / n, (y + 0.5f) / n);
        counts[b] = counts.TryGetValue(b, out var c) ? c + 1 : 1;
      }
    System.Console.WriteLine("  biome map: " + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100.0 * kv.Value / (n * n):0.#}%")));
  }

  // Paints discs of the default palette's colours on land of the right biome, found in the world's own biome map, and
  // bakes the result into the settings exactly as world creation does (ImageMapAltBiome.Create: legend + PNG).
  private static int Plant(BC.BetterContinentsSettings settings, string path, string dir)
  {
    if (!settings.EnabledForThisWorld || !settings.HasBiomeMap)
    {
      System.Console.WriteLine("these settings have no biome map to plant on");
      return 1;
    }
    const float total = 21000f;
    const int grid = 200;
    // Biome at world position, from the baked biome map (the same lookup the game's GetBiome patch uses).
    Heightmap.Biome At(float x, float z) => settings.GetBiomeOverride(x / total + 0.5f, z / total + 0.5f);
    var samples = new List<(float x, float z, Heightmap.Biome b)>();
    for (int j = 0; j < grid; j++)
      for (int i = 0; i < grid; i++)
      {
        float x = (i + 0.5f) / grid * total - total / 2f, z = (j + 0.5f) / grid * total - total / 2f;
        float d = MathF.Sqrt(x * x + z * z);
        if (d > 1500f && d < 9000f)
          samples.Add((x, z, At(x, z)));
      }
    var used = new List<(float x, float z)>();
    // How much of a disc of this radius around (x, z) is the biome, sampled on a ring pattern.
    float Purity(float x, float z, float r, Heightmap.Biome b)
    {
      int hit = 0, all = 0;
      for (int k = 0; k < 24; k++)
        for (float f = 0.25f; f <= 1f; f += 0.25f)
        {
          float a = k * MathF.PI * 2 / 24;
          all++;
          if (At(x + MathF.Cos(a) * r * f, z + MathF.Sin(a) * r * f) == b)
            hit++;
        }
      return hit / (float)all;
    }
    (float x, float z)? Find(Heightmap.Biome b, float r)
    {
      var best = samples.Where(s => s.b == b && used.All(u => (u.x - s.x) * (u.x - s.x) + (u.z - s.z) * (u.z - s.z) > 1500f * 1500f))
        .Select(s => (s.x, s.z, p: Purity(s.x, s.z, r, b)))
        .OrderByDescending(s => s.p).ThenBy(s => s.x * s.x + s.z * s.z).FirstOrDefault();
      if (best.p <= 0.5f)
        return null;
      used.Add((best.x, best.z));
      return (best.x, best.z);
    }
    var plan = new List<(float x, float z, float r, string hex, string what)>();
    var extra = new StringBuilder();
    void Disc(Heightmap.Biome b, string alt, float r = 450f)
    {
      var hex = BetterContinents.ImageMapAltBiome.DefaultColorFor(alt)!;
      if (Find(b, r) is { } p)
        plan.Add((p.x, p.z, r, hex, alt));
      else
        System.Console.WriteLine($"  no {b} to plant {alt} on");
    }
    Disc(Heightmap.Biome.Meadows, "Dark Meadows");
    Disc(Heightmap.Biome.BlackForest, "Troll Black Forest");
    Disc(Heightmap.Biome.Mountain, "Fortress Mountain");
    Disc(Heightmap.Biome.Plains, "Goblin Plains");
    Disc(Heightmap.Biome.Swamp, "Hut Swamp");
    Disc(Heightmap.Biome.Mistlands, "Trees Mistlands");
    Disc(Heightmap.Biome.DeepNorth, "Lantern", 700f);
    if (Find(Heightmap.Biome.Meadows, 350f) is { } none)
    {
      plan.Add((none.x, none.z, 350f, "C030F0", "none"));
      extra.Append("\n# Added for the dedicated-server test\nnone: C030F0\n");
    }
    if (Find(Heightmap.Biome.Swamp, 200f) is { } point)
      extra.Append($"Bat Swamp: at {point.x.ToString("0", CultureInfo.InvariantCulture)}, {point.z.ToString("0", CultureInfo.InvariantCulture)}\n");
    Directory.CreateDirectory(dir);
    var png = Path.Combine(dir, "altbiomemap.png");
    const int size = 1024;
    using (var img = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 0)))
    {
      for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
          float x = (px / (float)(size - 1) - 0.5f) * total, z = ((size - 1 - py) / (float)(size - 1) - 0.5f) * total;
          foreach (var d in plan)
            if ((x - d.x) * (x - d.x) + (z - d.z) * (z - d.z) <= d.r * d.r)
            {
              img[px, py] = Hex(d.hex);
              break;
            }
        }
      img.SaveAsPng(png);
    }
    File.WriteAllText(Path.Combine(dir, "altbiomemap.txt"), BetterContinents.ImageMapAltBiome.DefaultLegend + extra);
    foreach (var d in plan)
      System.Console.WriteLine($"  {d.what} (#{d.hex}): disc r {d.r:0} m at ({d.x:0}, {d.z:0}), {Purity(d.x, d.z, d.r, At(d.x, d.z)) * 100:0}% {At(d.x, d.z)}");
    System.Console.Write(extra.ToString().Replace("\n", "\n  "));
    var map = BetterContinents.ImageMapAltBiome.Create(png);
    if (map == null)
    {
      System.Console.WriteLine("could not create the alt-biome map");
      return 1;
    }
    settings.SetAltBiomeMap(map);
    Save(settings, path);
    var check = Load(path);
    System.Console.WriteLine($"reloaded: alt-biome map {check.HasAltBiomeMap}, {check.AltBiomeMapData?.Map.Count(b => b != 0)} planted pixels, {check.AltBiomeMapData?.Pins.Count} point plant(s)");
    return 0;
  }
}

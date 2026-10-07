// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3).
//
// A world made since 0.10 saving and reading its maps: as pictures when it is not compact, as before, and as tiles when it
// is; the cache under threads and a tiny budget; the speed of a sample against the whole decoded picture; and
// "measure", for a folder of real maps.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using ISImage = SixLabors.ImageSharp.Image;
using ISConfiguration = SixLabors.ImageSharp.Configuration;

internal static partial class Program
{
  private static FieldInfo Field(string name) =>
    typeof(BC.BetterContinentsSettings).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;

  private static readonly string[] FloatFields = ["HeightMap", "RoughMap", "FlatMap", "ForestMap", "HeatMap", "LavaMap", "MossMap"];

  // A spawn or vegetation picture: white (index 0, "none"), red (-Boar), green (+Neck) and black (nothing).
  private static byte[] SpawnPicture(int size, int seed) =>
    Png(size, (x, y) => ((y / 17 + x / 23 + seed) % 4) switch
    {
      0 => new Rgba32(255, 255, 255, 255),
      1 => new Rgba32(255, 0, 0, 255),
      2 => new Rgba32(0, 255, 0, 255),
      _ => new Rgba32(0, 0, 0, 255),
    }, PngColorType.RgbWithAlpha, PngBitDepth.Bit8);

  // Every map kind a world saves in tiles, at size, read from their pictures as a new world made From Config reads
  // them: with Compact Maps on, compressed.
  private static BC.BetterContinentsSettings World(int size, bool alpha, bool compact)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, HeightMapAlpha = alpha, CompactMaps = compact };
    var dir = $"world-{size}-{alpha}-{compact}";
    Directory.CreateDirectory(Path.Combine(Work, dir));
    string Map(string file, byte[] png, string legend = null) => WriteMap(Path.Combine(dir, file), png, legend);
    int seed = 0;
    foreach (var name in FloatFields)
    {
      seed += 11;
      var png = name == "HeightMap" && alpha
        ? Png(size, (x, y) => new La32(Height(x, y, seed), (ushort)(x * 97 + y)), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16)
        : Png(size, (x, y) => new L16(Height(x, y, seed)), PngColorType.Grayscale, PngBitDepth.Bit16);
      var map = ImageMapFloat.Create(Map(name.ToLowerInvariant() + ".png", png), name == "HeightMap" ? s.HeightmapAlphaMode : ImageMapFloat.HeightAlpha.None, compact);
      Field(name).SetValue(s, map);
    }
    var biomes = Png(size, (x, y) => (x / 40 + y / 30) % 3 == 0 ? new Rgba32(0, 255, 0) : (x / 40 + y / 30) % 3 == 1 ? new Rgba32(0, 0, 255) : new Rgba32(255, 255, 255), PngColorType.Rgb, PngBitDepth.Bit8);
    Field("BiomeMap").SetValue(s, ImageMapBiome.Create(Map("biomemap.png", biomes, "Meadows: 00FF00|Ocean: 0000FF|Mountain: FFFFFF"), compact));
    foreach (var name in new[] { "SpawnMap", "VegetationMap" })
      Field(name).SetValue(s, ImageMapSpawn.Create(Map(name.ToLowerInvariant() + ".png", SpawnPicture(size, name.Length), "255,0,0,255: -Boar|0,255,0,255: +Neck"), compact));
    var colours = Png(size, (x, y) => (x + y) % 9 == 0 ? new Rgba32(255, 0, 0, 255) : new Rgba32((byte)(x * 3), (byte)(y * 2), 90, 255), PngColorType.RgbWithAlpha, PngBitDepth.Bit8);
    Field("PaintMap").SetValue(s, ImageMapPaint.Create(Map("paintmap.png", colours, "0,0,0,0: 255,0,0,255"), compact));
    Field("TerrainMap").SetValue(s, ImageMapTerrain.Create(Map("terrainmap.png", colours, "Meadows: FF0000"), false, compact));
    return s;
  }

  private static IEnumerable<ImageMapBase> Maps(BC.BetterContinentsSettings s) => s.LoadedImageMaps().Select(m => m.Map);

  // Every map of a against b at many points, bit for bit; the name of the first that differs.
  private static string Differs(BC.BetterContinentsSettings a, BC.BetterContinentsSettings b, int size, bool alpha = true)
  {
    var points = Points(size, 77, 1500).ToList();
    foreach (var name in FloatFields)
    {
      var ma = (ImageMapFloat)Field(name).GetValue(a)!;
      var mb = (ImageMapFloat)Field(name).GetValue(b)!;
      if (ma.Size != mb.Size || points.Any(p => !Same(ma.GetValue(p.x, p.y), mb.GetValue(p.x, p.y)) || (alpha && !Same(ma.GetAlpha(p.x, p.y), mb.GetAlpha(p.x, p.y)))))
        return name;
    }
    var ba = (ImageMapBiome)Field("BiomeMap").GetValue(a)!;
    var bb = (ImageMapBiome)Field("BiomeMap").GetValue(b)!;
    if (points.Any(p => ba.GetValue(p.x, p.y) != bb.GetValue(p.x, p.y)))
      return "BiomeMap";
    foreach (var name in new[] { "SpawnMap", "VegetationMap" })
    {
      var sa = (ImageMapSpawn)Field(name).GetValue(a)!;
      var sb = (ImageMapSpawn)Field(name).GetValue(b)!;
      if (!sa.Indices.SequenceEqual(sb.Indices) || points.Where(p => p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1).Any(p => sa.GetEntry(p.x, p.y)?.Data != sb.GetEntry(p.x, p.y)?.Data))
        return name;
    }
    foreach (var name in new[] { "PaintMap", "TerrainMap" })
    {
      var ca = (ImageMapColor)Field(name).GetValue(a)!;
      var cb = (ImageMapColor)Field(name).GetValue(b)!;
      foreach (var (x, y) in points)
        if (ca.TryGetValue(x, y, out var u) != cb.TryGetValue(x, y, out var v) || u != v)
          return name;
    }
    return null;
  }

  private static byte[] Save(BC.BetterContinentsSettings s, bool network, int version)
  {
    var pkg = new ZPackage();
    s.Serialize(pkg, network, true, version);
    return pkg.GetArray();
  }

  private static BC.BetterContinentsSettings Read(byte[] saved) => BC.BetterContinentsSettings.Load(new ZPackage(saved));

  private static void Settings()
  {
    Section("Compact Maps off (the default): a world made since 0.10 saves and sends its maps' pictures, as before");
    const int N = 300;
    var plain = World(N, alpha: false, compact: false);
    var p12 = Save(plain, false, 12);
    var pBack = Read(p12);
    C(pBack.EnabledForThisWorld && !pBack.CompactMaps && Maps(pBack).All(m => !m.Compact) && Differs(plain, pBack, N) == null,
      "read back: not compact, every map decoded, every sample bit for bit as the world it was saved from");
    C(Save(pBack, false, 12).SequenceEqual(p12), "and it saves the same bytes again");
    C(FloatFields.All(name => ((ImageMapFloat)Field(name).GetValue(pBack)!).SourceData.SequenceEqual(File.ReadAllBytes(((ImageMapFloat)Field(name).GetValue(plain)!).FilePath))),
      "every float map is saved as its file's bytes, as before");
    var pNet = Read(Save(plain, true, 12));
    C(!pNet.CompactMaps && Differs(plain, pNet, N) == null, "sent to a client, it reads the same, decoded");
    var p11 = Read(Save(plain, false, 11));
    C(!p11.CompactMaps && Differs(plain, p11, N) == null, "saved in version 11, it reads the same");
    // The setting: Auto unless chosen, read when a world is made, and On makes only a world of the newest settings version compact
    // (Auto leaves it to the maps, Off to none: CompactForLargeMaps and NoteFine, tools/import-tests).
    var s11 = new BC.BetterContinentsSettings { Version = 11 };
    var s12 = new BC.BetterContinentsSettings { Version = 12 };
    var sAuto = new BC.BetterContinentsSettings { Version = 12 };
    var sOff = new BC.BetterContinentsSettings { Version = 12 };
    SettingsSchema.CompactMaps.Set!(s11, CompactMapsMode.On);
    SettingsSchema.CompactMaps.Set!(s12, CompactMapsMode.On);
    SettingsSchema.CompactMaps.Set!(sAuto, CompactMapsMode.Auto);
    SettingsSchema.CompactMaps.Set!(sOff, CompactMapsMode.Off);
    C(SettingsSchema.CompactMaps.Default == CompactMapsMode.Auto && SettingsSchema.CompactMaps.Scope == SettingScope.World && SettingsSchema.CompactMaps.Section == "07 BetterContinents.Misc"
      && SettingsSchema.Scalars.Contains(SettingsSchema.CompactMaps) && !s11.CompactMaps && s12.CompactMaps && !sAuto.CompactMaps && !sOff.CompactMaps
      && s12.CompactMapsChoice == CompactMapsMode.On && sAuto.CompactMapsChoice == CompactMapsMode.Auto && sOff.CompactMapsChoice == CompactMapsMode.Off,
      "[07 BetterContinents.Misc] Compact Maps: Auto by default, read when a world is made; On makes only a world of settings version 12 compact, Auto and Off leave it to the maps");

    Section("Compact Maps on: a world made since 0.10 saves, sends and reads its maps as tiles");
    var world = World(N, alpha: false, compact: true);
    var v12 = Save(world, false, 12);
    var v11 = Save(world, false, 11);
    var back = Read(v12);
    C(back.EnabledForThisWorld && back.CompactMaps && Maps(back).All(m => m.Compact) && Differs(world, back, N) == null,
      "read back: still compact, every sample bit for bit as the world it was saved from");
    C(Differs(plain, back, N) == null, "and as the same world made with Compact Maps off");
    C(Save(back, false, 12).SequenceEqual(v12), "and saves the same bytes again (its tiles are kept, not encoded again)");
    C(v12.Length < v11.Length, $"the tiles are smaller: {v12.Length:N0} bytes, against {v11.Length:N0} for the pictures (noisy test maps; real ones compress far better)");
    var net = Save(world, true, 12);
    var client = Read(net);
    C(client.CompactMaps && Differs(world, client, N) == null && net.Length < v12.Length, "sent to a client (no paths), it samples the same, compact");

    // The same world saved by an older format (Override version 11): the pictures written back out from the tiles.
    var old = Save(back, false, 11);
    var oldBack = Read(old);
    C(!oldBack.CompactMaps && Maps(oldBack).All(m => !m.Compact) && Differs(world, oldBack, N) == null,
      "saved from its tiles in version 11 (the pictures rebuilt from the tiles), it reads the same, decoded");

    var heights = (ImageMapFloat)Field("HeightMap").GetValue(back)!;
    C(heights.SourceData.Length == 0 && heights.SourceBytes().Length > 0, "a map read from tiles holds no picture, and writes one when asked");
    // The rebuilt picture holds the map's own values, rows from the north as a file has them.
    using (var image = ISImage.Load<L16>(ISConfiguration.Default, heights.SourceBytes()))
    {
      bool same = image.Width == N;
      image.ProcessPixelRows(accessor =>
      {
        for (int y = 0; y < N && same; y++)
        {
          var row = accessor.GetRowSpan(N - 1 - y);
          for (int x = 0; x < N; x++)
            same &= row[x].PackedValue == Height(x, N - 1 - y, 11);
        }
      });
      var rebuilt = ImageMapFloat.Create(heights.SourceBytes(), ImageMapFloat.HeightAlpha.None);
      C(same && Points(N, 3).All(p => Same(rebuilt.GetValue(p.x, p.y), heights.GetValue(p.x, p.y))), "the rebuilt picture is the original, pixel for pixel");
    }

    // Heightmap Alpha on a new world: the alpha is a grid of its own.
    var blending = World(160, alpha: true, compact: true);
    var alphaBack = Read(Save(blending, false, 12));
    var hm = (ImageMapFloat)Field("HeightMap").GetValue(alphaBack)!;
    C(hm.HasAlpha && Differs(blending, alphaBack, 160) == null, "a blending heightmap keeps its alpha in tiles and reads back the same");

    // The record a world export writes into its heightmap.png stays with the tiles.
    var recorded = Path.Combine(Path.GetTempPath(), $"bc-tile-record-{Environment.ProcessId}.png");
    WorldExportPng.SaveHeightmap(recorded, Enumerable.Range(0, 64 * 64).Select(i => new L16((ushort)(i * 13))).ToArray(), 64, new HeightmapRecord(1.25f, 0.4f));
    var withRecord = ImageMapFloat.Create(recorded, ImageMapFloat.HeightAlpha.None, compact: true)!;
    File.Delete(recorded);
    var recordBack = ImageMapFloat.FromBlock(withRecord.ToBlock());
    var reread = ImageMapFloat.Create(recordBack.SourceBytes(), ImageMapFloat.HeightAlpha.None)!;
    C(recordBack.Record is { Amount: 1.25f, SeaLevel: 0.4f } && reread.Record is { Amount: 1.25f, SeaLevel: 0.4f },
      "a heightmap's record is saved with its tiles, and written back into the picture rebuilt from them");

    // Better Continents 0.9 stops at the tiles' key: it is new, so it reads the world as vanilla instead of misreading it. (The keys of
    // 0.10.3 follow it: 68 for wide sectors, 69 for High Terrain.)
    C((int)BC.DataKey.TiledMap == 67 && Enumerable.Range(0, 68).All(key => Enum.IsDefined(typeof(BC.DataKey), key)), "the tiles' key is 67, after every key 0.9 knows (0 to 66, none missing)");
  }

  // ---- the cache ----------------------------------------------------------------------------------------------------

  private static void Cache()
  {
    Section("the cache: many threads, a budget of four tiles, every value right");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int N = 1024;
    var png = Png(N, (x, y) => new L16(Height(x, y, 5)), PngColorType.Grayscale, PngBitDepth.Bit16);
    var map = FloatMap(png, ImageMapFloat.HeightAlpha.None, compact: true)!;
    var old = OldFloat.From<L16>(png);
    long tile = 2L * TileBlock.Pixels;
    TileCache.Override = 4 * tile;
    // The first tile decoded under the budget sweeps away what the sections before left.
    map.GetValue(0.5f, 0.5f);
    long peak = TileCache.Resident;
    bool done = false;
    var watcher = new Thread(() =>
    {
      while (!Volatile.Read(ref done))
      {
        long r = TileCache.Resident;
        if (r > peak) peak = r;
        Thread.Sleep(0);
      }
    });
    watcher.Start();
    int wrong = 0;
    const int Threads = 8, Each = 60000;
    Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, k =>
    {
      var random = new System.Random(k);
      for (int i = 0; i < Each; i++)
      {
        float x = (float)random.NextDouble(), y = (float)random.NextDouble();
        if (!Same(map.GetValue(x, y), old.Value(x, y)))
          Interlocked.Increment(ref wrong);
      }
    });
    Volatile.Write(ref done, true);
    watcher.Join();
    C(wrong == 0, $"{Threads * Each:N0} reads from {Threads} threads over 64 tiles, every one right");
    // Past a quarter over the budget a thread waits to sweep, so at most each thread's tile in flight is added on top.
    C(peak <= TileCache.Override * 5 / 4 + 2 * Threads * tile,
      $"the decoded tiles stayed near the budget (peak {peak / 1024:N0} KB, budget {TileCache.Override / 1024:N0} KB, the whole map {64 * tile / 1024:N0} KB)");
    C(map.DecodedTiles <= 4 + 2 * Threads, $"tiles were dropped as others were read ({map.DecodedTiles} decoded now)");
    map.ReleasePixels();
    C(map.DecodedTiles == 0 && Same(map.GetValue(0.25f, 0.75f), old.Value(0.25f, 0.75f)), "dropping them all leaves the map reading the same");
    TileCache.Override = null;

    // Uniform tiles are free: a map that is all sea outside a small area decodes only that area's tiles.
    var island = Png(N, (x, y) => new L16(x > 300 && x < 420 && y > 500 && y < 610 ? Height(x, y, 9) : (ushort)0), PngColorType.Grayscale, PngBitDepth.Bit16);
    var sea = FloatMap(island, ImageMapFloat.HeightAlpha.None, compact: true)!;
    for (int y = 0; y < N; y += 16)
      for (int x = 0; x < N; x += 16)
        sea.GetValue(x / (float)(N - 1), y / (float)(N - 1));
    C(sea.DecodedTiles <= 4, $"a map read everywhere, sea but for one island, decodes only the island's tiles ({sea.DecodedTiles})");
  }

  // ---- speed --------------------------------------------------------------------------------------------------------

  private static void Speed()
  {
    Section("speed: a sample against the whole decoded picture (informational)");
    const int N = 2048;
    var png = Png(N, (x, y) => new L16(Height(x, y, 1)), PngColorType.Grayscale, PngBitDepth.Bit16);
    var decoded = FloatMap(png, ImageMapFloat.HeightAlpha.None, compact: false)!;
    var compact = FloatMap(png, ImageMapFloat.HeightAlpha.None, compact: true)!;
    var old = OldFloat.From<L16>(png);
    const int Count = 4_000_000;
    var xs = new float[Count];
    var ys = new float[Count];
    var random = new System.Random(3);
    for (int i = 0; i < Count; i++)
    {
      xs[i] = (float)random.NextDouble();
      ys[i] = (float)random.NextDouble();
    }
    double Time(Func<float, float, float> sample, bool sequential)
    {
      float sink = 0;
      var sw = Stopwatch.StartNew();
      for (int i = 0; i < Count; i++)
        sink += sequential ? sample(i % 4096 / 4096f, i / 4096 / 1024f) : sample(xs[i], ys[i]);
      sw.Stop();
      if (sink == 1234.5f) System.Console.WriteLine();
      return sw.Elapsed.TotalMilliseconds * 1e6 / Count;
    }
    // Warm each (the compact tiles decoded once, as in a running world).
    Time(decoded.GetValue, false);
    Time(compact.GetValue, false);
    Time(old.Value, false);
    double oldRandom = Time(old.Value, false), decodedRandom = Time(decoded.GetValue, false), compactRandom = Time(compact.GetValue, false);
    double oldRow = Time(old.Value, true), decodedRow = Time(decoded.GetValue, true), compactRow = Time(compact.GetValue, true);
    System.Console.WriteLine($"    random points: whole picture {oldRandom:F1} ns, decoded tiles {decodedRandom:F1} ns, compact tiles {compactRandom:F1} ns");
    System.Console.WriteLine($"    along rows:    whole picture {oldRow:F1} ns, decoded tiles {decodedRow:F1} ns, compact tiles {compactRow:F1} ns");
    C(decodedRandom < 8 * oldRandom + 50 && decodedRow < 8 * oldRow + 50 && compactRandom < 8 * oldRandom + 50 && compactRow < 8 * oldRow + 50,
      "a sample through the tiles costs a few nanoseconds more at most");
  }

  // ---- measure --------------------------------------------------------------------------------------------------------

  private static int Measure(string folder)
  {
    System.Console.WriteLine($"== measuring the maps in {folder} (read only)");
    MeasureDecoded(folder);
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, CompactMaps = true };
    long before = GC.GetTotalMemory(true);
    long oldBytes = 0;
    var floats = new[] { ("heightmap.png", "HeightMap"), ("roughmap.png", "RoughMap"), ("flatmap.png", "FlatMap"), ("forestmap.png", "ForestMap"), ("heatmap.png", "HeatMap"), ("lavamap.png", "LavaMap"), ("mossmap.png", "MossMap") };
    var loaded = new List<(string file, object map, int size)>();
    foreach (var (file, field) in floats)
    {
      var path = Path.Combine(folder, file);
      if (!File.Exists(path)) continue;
      var sw = Stopwatch.StartNew();
      var map = ImageMapFloat.Create(path, ImageMapFloat.HeightAlpha.None, compact: true)!;
      Field(field).SetValue(s, map);
      oldBytes += 4L * map.Size * map.Size;
      loaded.Add((file, map, map.Size));
      System.Console.WriteLine($"   {file}: {map.Size} px, png {new FileInfo(path).Length:N0} B, tiles {map.ToBlock().Length:N0} B, read and tiled in {sw.ElapsedMilliseconds} ms");
    }
    var biomePath = Path.Combine(folder, "biomemap.png");
    if (File.Exists(biomePath))
    {
      var sw = Stopwatch.StartNew();
      // A legend this measure does not know: the tool reads the picture's own legend file beside it, as the game does.
      var biome = ImageMapBiome.Create(biomePath, compact: true)!;
      Field("BiomeMap").SetValue(s, biome);
      oldBytes += 4L * biome.Size * biome.Size;
      loaded.Add(("biomemap.png", biome, biome.Size));
      System.Console.WriteLine($"   biomemap.png: {biome.Size} px, png {new FileInfo(biomePath).Length:N0} B, saved raw before {(long)biome.Size * biome.Size:N0} B, tiles {biome.ToBlock(false).Length:N0} B, read in {sw.ElapsedMilliseconds} ms");
    }
    // The pixels as the maps held them before, against the tiles: everything decoded once (the whole world read).
    long compressed = GC.GetTotalMemory(true) - before;
    var all = Stopwatch.StartNew();
    foreach (var (file, map, size) in loaded)
    {
      int tiles = TileBlock.TilesFor(size);
      for (int ty = 0; ty < tiles; ty++)
        for (int tx = 0; tx < tiles; tx++)
        {
          float x = Math.Min(size - 1, tx * 128 + 64) / (float)(size - 1), y = Math.Min(size - 1, ty * 128 + 64) / (float)(size - 1);
          if (map is ImageMapFloat f) f.GetValue(x, y);
          else if (map is ImageMapBiome b) b.GetValue(x, y);
        }
    }
    all.Stop();
    long decoded = GC.GetTotalMemory(true) - before;
    var v12 = Save(s, false, 12);
    var v11 = Save(s, false, 11);
    var net = Save(s, true, 12);
    System.Console.WriteLine($"   before 0.10: the pixels in memory {oldBytes:N0} B, plus the pictures' bytes; the world file {v11.Length:N0} B");
    System.Console.WriteLine($"   0.10 Compact Maps: the world file {v12.Length:N0} B, sent to a client {net.Length:N0} B");
    System.Console.WriteLine($"   0.10 Compact Maps, the session that makes the world: {compressed:N0} B held after reading the pictures (tiles and the pictures' bytes); {decoded:N0} B with every tile decoded ({all.ElapsedMilliseconds} ms to read every tile once)");

    s = null;
    loaded.Clear();
    v11 = null;
    net = null;
    MeasureLoad(v12);
    return 0;
  }

  // Compact Maps off: the float and biome maps read as a world holds them, every tile decoded at once, and the world
  // file they make (the pictures, as before).
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void MeasureDecoded(string folder)
  {
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long before = GC.GetTotalMemory(true);
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var sw = Stopwatch.StartNew();
    foreach (var (file, field) in new[] { ("heightmap.png", "HeightMap"), ("roughmap.png", "RoughMap"), ("flatmap.png", "FlatMap"), ("forestmap.png", "ForestMap"), ("heatmap.png", "HeatMap"), ("lavamap.png", "LavaMap"), ("mossmap.png", "MossMap") })
      if (File.Exists(Path.Combine(folder, file)))
        Field(field).SetValue(s, ImageMapFloat.Create(Path.Combine(folder, file), ImageMapFloat.HeightAlpha.None));
    if (File.Exists(Path.Combine(folder, "biomemap.png")))
      Field("BiomeMap").SetValue(s, ImageMapBiome.Create(Path.Combine(folder, "biomemap.png")));
    sw.Stop();
    long held = GC.GetTotalMemory(true) - before;
    long pictures = Maps(s).Sum(m => (long)m.SourceData.Length);
    System.Console.WriteLine($"   0.10 Compact Maps off: read in {sw.ElapsedMilliseconds} ms; {held:N0} B held, of which {pictures:N0} B the pictures' bytes (kept for the save) and the rest the decoded tiles; the world file {Save(s, false, 12).Length:N0} B");
    GC.KeepAlive(s);
  }

  // The world as a server or a client holds it afterwards: read from its file (or the transfer), no picture decoded.
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void MeasureLoad(byte[] v12)
  {
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long baseline = GC.GetTotalMemory(true);
    var load = Stopwatch.StartNew();
    var world = BC.BetterContinentsSettings.Load(new ZPackage(v12));
    load.Stop();
    long held = GC.GetTotalMemory(true) - baseline;
    var read = Stopwatch.StartNew();
    foreach (var name in FloatFields.Append("BiomeMap"))
    {
      var map = Field(name).GetValue(world);
      if (map == null) continue;
      int size = ((ImageMapBase)map).Size, tiles = TileBlock.TilesFor(size);
      for (int ty = 0; ty < tiles; ty++)
        for (int tx = 0; tx < tiles; tx++)
        {
          float x = Math.Min(size - 1, tx * 128 + 64) / (float)(size - 1), y = Math.Min(size - 1, ty * 128 + 64) / (float)(size - 1);
          if (map is ImageMapFloat f) f.GetValue(x, y);
          else if (map is ImageMapBiome b) b.GetValue(x, y);
        }
    }
    read.Stop();
    long full = GC.GetTotalMemory(true) - baseline;
    System.Console.WriteLine($"   0.10 Compact Maps, a server or client loading the world: read in {load.ElapsedMilliseconds} ms, {held:N0} B held; {full:N0} B with every tile decoded ({read.ElapsedMilliseconds} ms to decode them all)");
    GC.KeepAlive(world);
    GC.KeepAlive(v12);
  }
}

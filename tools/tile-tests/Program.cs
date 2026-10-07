// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3).
//
// Offline checks of the maps' tiles (MapTiles.cs): the codec round trip in every shape and transform; damage refused;
// every map kind sampling bit for bit as the whole decoded picture did before (the old code is kept here as the
// oracle), with Compact Maps off (every tile decoded at once, the default) and on (compressed tiles); a world saving its
// maps as pictures, as before, with it off, and as tiles with it on, and writing them back out as pictures; and the
// cache, many threads reading under a budget far too small, every value right.
// "dotnet run -c Release -- measure <folder>" measures a folder of real maps (read only).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using ISImage = SixLabors.ImageSharp.Image;
using ISConfiguration = SixLabors.ImageSharp.Configuration;

// The game's log, kept here (Unity's own needs the engine); errors and warnings are printed as they come.
internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    var line = $"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args));
    lock (Lines) Lines.Add(line);
    if (logType is LogType.Error or LogType.Warning or LogType.Exception)
      System.Console.WriteLine("    log: " + line);
  }
  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines) Lines.Add("[Exception] " + exception.Message);
    System.Console.WriteLine("    log: [Exception] " + exception.Message);
  }
}

internal static partial class Program
{
  private static int checks, failures;
  // A folder for the pictures and legends the maps are read from, as a new world reads them.
  private static string Work;

  private static void C(bool ok, string what)
  {
    checks++;
    if (ok)
    {
      System.Console.WriteLine("  PASS " + what);
      return;
    }
    failures++;
    System.Console.WriteLine("  FAIL " + what);
  }

  private static void Section(string title) => System.Console.WriteLine("== " + title);

  private static int Main(string[] args)
  {
    var libs = Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools");
    // As the other suites: the game's own assemblies, read only (Unity's Color32.ToString needs its shared internals).
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(libs, "1.0", "client"), libs, Path.Combine(libs, "BepInEx", "core"),
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed" };
    AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
      foreach (var dir in dirs)
      {
        var path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path))
          return context.LoadFromAssemblyPath(Path.GetFullPath(path));
      }
      return null;
    };
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    if (args.Length > 1 && args[0] == "measure")
      return Measure(args[1]);
    if (args.Length > 3 && args[0] == "measure16k")
      return Measure16k(args);
    if (args.Length > 2 && args[0] == "measure16k-load")
      return Measure16kLoad(args);
    if (args.Length > 0 && args[0] == "big")
      return Big16k();
    if (args.Length > 2 && args[0] == "big-kind")
      return BigKind(args[1], args[2] == "1");
    Work = Path.Combine(Path.GetTempPath(), "bc-tile-tests-" + Environment.ProcessId);
    Directory.CreateDirectory(Work);
    try
    {
      Codec();
      PngRowsTests();
      PngWriterTests();
      LargeSizes();
      MaxMapSizeSetting();
      LocationMaps();
      PackageTests();
      Damage();
      FloatMaps();
      DecodedMaps();
      FineHeightsTests();
      BiomeMaps();
      SpawnMaps();
      ColourMaps();
      Settings();
      Cache();
      Speed();
    }
    catch (Exception e)
    {
      failures++;
      // Exception.ToString would resolve the game's attributes (UnityEngine.SharedInternalsModule is not here).
      System.Console.WriteLine($"CRASH {e.GetType().FullName}: {e.Message}");
      foreach (var frame in new StackTrace(e, true).GetFrames() ?? [])
        System.Console.WriteLine($"   at {frame.GetMethod()?.DeclaringType?.FullName}.{frame.GetMethod()?.Name} line {frame.GetFileLineNumber()}");
    }
    finally
    {
      TileCache.Override = null;
      try { Directory.Delete(Work, true); } catch { }
    }
    System.Console.WriteLine($"tile-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  // ---- the codec --------------------------------------------------------------------------------------------------

  // Value patterns: (x, y, channel, random) -> a value, masked to the value's bytes.
  private static readonly (string name, Func<int, int, int, System.Random, int> value)[] Patterns =
  [
    ("random", (x, y, c, r) => r.Next(65536)),
    ("smooth", (x, y, c, r) => (int)(30000 + 12000 * Math.Sin(x * 0.031 + c) + 9000 * Math.Cos(y * 0.027))),
    ("constant", (x, y, c, r) => 4242 + c),
    ("blocky", (x, y, c, r) => (x / 128 * 7 + y / 128 * 13 + c) * 811),
    ("two values", (x, y, c, r) => r.Next(10) == 0 ? 65535 : 0),
    ("terraced", (x, y, c, r) => (x / 7 + y / 11) % 50 * 1000),
  ];

  private static void Codec()
  {
    Section("tiles: every shape and pattern comes back exactly");
    var random = new System.Random(1234);
    var modes = new int[4];
    int encoded = 0, wrong = 0;
    string firstWrong = null;
    foreach (int size in new[] { 1, 2, 5, 127, 128, 129, 255, 300 })
      foreach (int channels in new[] { 1, 2, 4 })
        foreach (int bytes in new[] { 1, 2 })
          foreach (var (name, value) in Patterns)
            foreach (bool median in new[] { false, true })
            {
              int mask = bytes == 2 ? 0xFFFF : 0xFF;
              var values = new ushort[channels][];
              for (int c = 0; c < channels; c++)
              {
                values[c] = new ushort[size * size];
                for (int y = 0; y < size; y++)
                  for (int x = 0; x < size; x++)
                    values[c][y * size + x] = (ushort)(value(x, y, c, random) & mask);
              }
              var block = TileBlock.Encode(size, channels, bytes, median, (y0, rows, band) =>
              {
                for (int c = 0; c < channels; c++)
                  for (int r = 0; r < rows; r++)
                    Array.Copy(values[c], (y0 + r) * size, band, (c * TileBlock.Side + r) * size, size);
              });
              encoded++;
              var read = TileBlock.Read(block.Data);
              var tile = new ushort[channels * TileBlock.Pixels];
              for (int t = 0; t < read.Count; t++)
              {
                modes[read.Mode(t)]++;
                read.Decode(t, tile);
                int tx = t % read.Tiles, ty = t / read.Tiles;
                for (int c = 0; c < channels; c++)
                  for (int y = 0; y < read.Extent(ty); y++)
                    for (int x = 0; x < read.Extent(tx); x++)
                    {
                      int gx = (tx << TileBlock.Shift) + x, gy = (ty << TileBlock.Shift) + y;
                      if (tile[c * TileBlock.Pixels + (y << TileBlock.Shift) + x] != values[c][gy * size + gx])
                      {
                        wrong++;
                        firstWrong ??= $"{size}px {channels}ch {bytes}B {name} median={median} at {gx},{gy} channel {c}";
                      }
                    }
              }
            }
    C(wrong == 0, $"{encoded} maps encoded and decoded, every value the same" + (firstWrong == null ? "" : $" (first wrong: {firstWrong}; {wrong} wrong)"));
    C(modes.All(m => m > 0), $"every way of storing a tile was used: uniform {modes[0]}, raw {modes[1]}, planes {modes[2]}, median {modes[3]}");

    // The same picture always gives the same bytes, whatever the threads did.
    var smooth = Patterns[1].value;
    TileBlock Smooth() => TileBlock.Encode(700, 1, 2, true, (y0, rows, band) =>
    {
      for (int r = 0; r < rows; r++)
        for (int x = 0; x < 700; x++)
          band[r * 700 + x] = (ushort)smooth(x, y0 + r, 0, null);
    });
    C(Smooth().Data.SequenceEqual(Smooth().Data), "encoding is deterministic (the same bytes twice, tiles encoded in parallel)");
    var smoothBlock = Smooth();
    int stored = Enumerable.Range(0, smoothBlock.Count).Sum(smoothBlock.StoredLength);
    C(stored < 700 * 700 * 2 / 3, $"a smooth 16-bit map compresses ({stored:N0} bytes for {700 * 700 * 2:N0} raw)");
  }

  private static void Damage()
  {
    Section("tiles: damage is refused, never read as a map");
    var block = TileBlock.Encode(200, 1, 2, true, (y0, rows, band) =>
    {
      for (int i = 0; i < rows * 200; i++)
        band[i] = (ushort)(i * 37 + y0);
    });
    bool Refused(byte[] data)
    {
      try
      {
        TileBlock.Read(data);
        return false;
      }
      catch (InvalidDataException)
      {
        return true;
      }
    }
    byte[] Resealed(byte[] body)
    {
      var crc = Crc32.Compute(body, 0, body.Length);
      return [.. body, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)];
    }
    var data = block.Data;
    var flipped = (byte[])data.Clone();
    flipped[data.Length / 2] ^= 0x10;
    C(Refused(flipped), "a byte changed in the middle: the checksum refuses it");
    C(Refused(data[..^7]), "cut short: refused");
    C(Refused([]), "empty: refused");
    var body = data[..^4];
    var newer = (byte[])body.Clone();
    newer[0] = 2;
    C(Refused(Resealed(newer)), "a newer format: refused");
    C(Refused(Resealed([.. body, 0])), "data past the tiles: refused");
    var badMode = (byte[])body.Clone();
    badMode[8] = 9;
    C(Refused(Resealed(badMode)), "a tile stored in an unknown way: refused");
    var huge = (byte[])body.Clone();
    BitConverter.GetBytes(TileBlock.MaxSize + 1).CopyTo(huge, 1);
    C(Refused(Resealed(huge)), "an impossible size: refused");
    C(!Refused(data), "and the block itself still reads");
  }

  // ---- the old code, as the oracle ---------------------------------------------------------------------------------

  // ImageMapFloat before the tiles: the whole picture decoded, flipped, every pixel converted with ToVector4().X (and
  // a blending alpha / 65535f), sampled bilinearly between the pixel centres.
  private sealed class OldFloat
  {
    public int Size;
    public float[] Map = [], Alpha = [];

    public static OldFloat From<T>(byte[] png) where T : unmanaged, IPixel<T>
    {
      using var image = ISImage.Load<T>(ISConfiguration.Default, png);
      image.Mutate(x => x.Flip(FlipMode.Vertical));
      var old = new OldFloat { Size = image.Width };
      old.Map = Pixels(image, p => p.ToVector4().X);
      if (image is Image<La32> alpha)
        old.Alpha = Pixels(alpha, p => p.A / 65535f);
      return old;
    }

    public float Value(float x, float y) => Sample(Map, x, y);
    public float GetAlpha(float x, float y) => Alpha.Length > 0 ? Sample(Alpha, x, y) : 1f;

    private float Sample(float[] map, float x, float y)
    {
      float xa = x * (Size - 1);
      float ya = y * (Size - 1);
      int xi = Mathf.FloorToInt(xa);
      int yi = Mathf.FloorToInt(ya);
      float xd = xa - xi;
      float yd = ya - yi;
      int x0 = Mathf.Clamp(xi, 0, Size - 1);
      int x1 = Mathf.Clamp(xi + 1, 0, Size - 1);
      int y0 = Mathf.Clamp(yi, 0, Size - 1);
      int y1 = Mathf.Clamp(yi + 1, 0, Size - 1);
      float p00 = map[y0 * Size + x0];
      float p10 = map[y0 * Size + x1];
      float p01 = map[y1 * Size + x0];
      float p11 = map[y1 * Size + x1];
      return Mathf.Lerp(Mathf.Lerp(p00, p10, xd), Mathf.Lerp(p01, p11, xd), yd);
    }
  }

  private static R[] Pixels<T, R>(Image<T> image, Func<T, R> convert) where T : unmanaged, IPixel<T>
  {
    var pixels = new R[image.Width * image.Height];
    image.ProcessPixelRows(accessor =>
    {
      for (int y = 0; y < accessor.Height; y++)
      {
        var row = accessor.GetRowSpan(y);
        for (int x = 0; x < row.Length; x++)
          pixels[y * row.Length + x] = convert(row[x]);
      }
    });
    return pixels;
  }

  // Where a map is sampled: a grid reaching past every edge, random points, every pixel centre of a small map, the
  // corners, and the values that break arithmetic.
  private static IEnumerable<(float x, float y)> Points(int size, int seed, int randomCount = 3000)
  {
    var random = new System.Random(seed);
    for (int i = 0; i <= 40; i++)
      for (int j = 0; j <= 40; j++)
        yield return (-0.2f + 1.4f * i / 40, -0.2f + 1.4f * j / 40);
    for (int i = 0; i < randomCount; i++)
      yield return ((float)random.NextDouble(), (float)random.NextDouble());
    if (size <= 160)
      for (int i = 0; i < size; i++)
        for (int j = 0; j < size; j += Math.Max(1, size / 20))
          yield return (size > 1 ? i / (float)(size - 1) : 0.5f, size > 1 ? j / (float)(size - 1) : 0.5f);
    foreach (var v in new[] { 0f, 1f, 0.5f, 1e-7f, 0.9999999f, -1e-7f, 1.0000001f })
      foreach (var w in new[] { 0f, 1f, 0.5f })
        yield return (v, w);
    yield return (float.NaN, 0.5f);
    yield return (0.5f, float.PositiveInfinity);
    yield return (float.NegativeInfinity, float.NaN);
  }

  private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

  // ---- float maps ---------------------------------------------------------------------------------------------------

  private static byte[] Png<T>(int size, Func<int, int, T> pixel, PngColorType type, PngBitDepth depth) where T : unmanaged, IPixel<T>
  {
    using var image = new Image<T>(size, size);
    image.ProcessPixelRows(accessor =>
    {
      for (int y = 0; y < size; y++)
      {
        var row = accessor.GetRowSpan(y);
        for (int x = 0; x < size; x++)
          row[x] = pixel(x, y);
      }
    });
    using var stream = new MemoryStream();
    image.Save(stream, new PngEncoder { ColorType = type, BitDepth = depth, ChunkFilter = PngChunkFilter.ExcludeAll });
    return stream.ToArray();
  }

  // A picture in the work folder, with its legend beside it when it has one (each '|' entry a line), as a new world finds
  // its maps; the path.
  private static string WriteMap(string name, byte[] png, string legend = null)
  {
    var path = Path.Combine(Work, name);
    File.WriteAllBytes(path, png);
    var txt = Path.ChangeExtension(path, ".txt");
    if (legend != null)
      File.WriteAllLines(txt, legend.Split('|'));
    else if (File.Exists(txt))
      File.Delete(txt);
    return path;
  }

  // A float map read from its picture as a new world reads it: Compact Maps off (every tile decoded at once) or on.
  private static ImageMapFloat FloatMap(byte[] png, ImageMapFloat.HeightAlpha alpha, bool compact)
  {
    var map = new ImageMapFloat { SourceData = png, Compact = compact };
    return map.CreateMap(alpha) ? map : null;
  }

  private static string Mode(bool compact) => compact ? "compact" : "decoded";

  private static ushort Height(int x, int y, int seed) =>
    (ushort)(32768 + 20000 * Math.Sin((x + seed) * 0.05) * Math.Cos(y * 0.043) + (x * 7919 + y * 104729 + seed) % 2000);

  private static void FloatMaps()
  {
    Section("float maps sample bit for bit as the whole decoded picture did");
    foreach (int size in new[] { 1, 2, 3, 97, 128, 129, 300 })
    {
      int seed = size * 31;
      var grey16 = Png(size, (x, y) => new L16(Height(x, y, seed)), PngColorType.Grayscale, PngBitDepth.Bit16);
      var grey8 = Png(size, (x, y) => new L8((byte)Height(x, y, seed)), PngColorType.Grayscale, PngBitDepth.Bit8);
      var rgb = Png(size, (x, y) => new Rgb24((byte)(x * 3), (byte)(y * 5), (byte)Height(x, y, seed)), PngColorType.Rgb, PngBitDepth.Bit8);
      var greyAlpha = Png(size, (x, y) => new La32(Height(x, y, seed), (ushort)(x * 211 + y * 17)), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16);
      var rgba = Png(size, (x, y) => new Rgba32((byte)Height(x, y, seed), (byte)x, (byte)y, 200), PngColorType.RgbWithAlpha, PngBitDepth.Bit8);
      ImageMapFloat Oldest(bool compact)
      {
        var m = new ImageMapFloat { SourceData = rgba, FilePath = "old.png", Compact = compact };
        return m.CreateMapLegacy() ? m : null;
      }
      var cases = new (string name, Func<bool, ImageMapFloat> make, OldFloat old)[]
      {
        ("16-bit grey", c => FloatMap(grey16, ImageMapFloat.HeightAlpha.None, c), OldFloat.From<L16>(grey16)),
        ("8-bit grey read as 16", c => FloatMap(grey8, ImageMapFloat.HeightAlpha.None, c), OldFloat.From<L16>(grey8)),
        ("RGB read as grey", c => FloatMap(rgb, ImageMapFloat.HeightAlpha.None, c), OldFloat.From<L16>(rgb)),
        ("grey + alpha, blending (La32)", c => FloatMap(greyAlpha, ImageMapFloat.HeightAlpha.Blend, c), OldFloat.From<La32>(greyAlpha)),
        ("grey + alpha, legacy (La16)", c => FloatMap(greyAlpha, ImageMapFloat.HeightAlpha.Legacy, c), OldFloat.From<La16>(greyAlpha)),
        ("the oldest maps (RGBA, red)", Oldest, OldFloat.From<Rgba32>(rgba)),
      };
      foreach (var (name, make, old) in cases)
        foreach (bool compact in new[] { false, true })
        {
          var map = make(compact);
          int n = 0, differ = 0;
          string first = null;
          foreach (var (x, y) in Points(size, seed))
          {
            n++;
            float a = map.GetValue(x, y), b = old.Value(x, y), c = map.GetAlpha(x, y), d = old.GetAlpha(x, y);
            if (!Same(a, b) || !Same(c, d))
            {
              differ++;
              first ??= $"({x}, {y}): {a} vs {b}, alpha {c} vs {d}";
            }
          }
          C(map.Size == old.Size && map.HasAlpha == (old.Alpha.Length > 0) && differ == 0,
            $"{size} px, {name}, {Mode(compact)}: {n} samples the same" + (first == null ? "" : $" ({differ} differ, first {first})"));
        }
    }
  }

  // Compact Maps off: no tile compressed, every one decoded when the picture is read (a tile of one value held as that
  // value), none dropped, and nothing in the cache.
  private static void DecodedMaps()
  {
    Section("Compact Maps off (the default): every tile decoded at once and kept, nothing compressed, nothing in the cache");
    const int N = 600;
    bool Island(int x, int y) => x > 200 && x < 330 && y > 100 && y < 260;
    var png = Png(N, (x, y) => new L16(Island(x, y) ? Height(x, y, 4) : (ushort)7), PngColorType.Grayscale, PngBitDepth.Bit16);
    // The tiles the island touches (rows flipped as maps hold them), the only ones that are not one value.
    int tiles = TileBlock.TilesFor(N), mixed = 0;
    for (int ty = 0; ty < tiles; ty++)
      for (int tx = 0; tx < tiles; tx++)
      {
        bool any = false;
        for (int y = ty * 128; y < Math.Min(N, ty * 128 + 128) && !any; y++)
          for (int x = tx * 128; x < Math.Min(N, tx * 128 + 128) && !any; x++)
            any = Island(x, N - 1 - y);
        if (any)
          mixed++;
      }
    long resident = TileCache.Resident;
    var map = FloatMap(png, ImageMapFloat.HeightAlpha.None, compact: false);
    var old = OldFloat.From<L16>(png);
    C(map.DecodedTiles == mixed && mixed > 0 && mixed < tiles * tiles, $"read: the {mixed} tiles the island touches are decoded, the other {tiles * tiles - mixed} held as their one value");
    bool same = true;
    for (int y = 0; y < N; y += 3)
      for (int x = 0; x < N; x += 3)
        same &= Same(map.GetValue(x / (float)(N - 1), y / (float)(N - 1)), old.Value(x / (float)(N - 1), y / (float)(N - 1)));
    C(same && map.DecodedTiles == mixed && TileCache.Resident == resident, "read everywhere: every value as before, no tile decoded again or dropped, nothing in the cache");
    C(map.SourceBytes() == map.SourceData && map.SourceData.SequenceEqual(png), "its picture is kept, and a world saves those bytes, as before");
    bool threw = false;
    try { map.ToBlock(); } catch (InvalidOperationException) { threw = true; }
    C(threw, "it has no compressed tiles to save");
    map.ReleasePixels();
    C(map.DecodedTiles == 0 && !map.HasAlpha, "a lean world import lets its pixels go, as before");
  }

  // ---- biome maps ---------------------------------------------------------------------------------------------------

  // ImageMapBiome.GetValue before the tiles, over the decoded biomes.
  private static Heightmap.Biome OldBiome(Heightmap.Biome[] map, int size, float x, float y)
  {
    int top = 0, count = 0;
    float xa = x * (size - 1), ya = y * (size - 1);
    int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
    float xd = xa - xi, yd = ya - yi;
    var biomes = new Heightmap.Biome[4];
    var weights = new float[4];
    var usable = BiomeRegistry.Usable;
    void Weighted(int xs, int ys, float weight)
    {
      var biome = map[Mathf.Clamp(ys, 0, size - 1) * size + Mathf.Clamp(xs, 0, size - 1)] & usable;
      int i = 0;
      for (; i < count; ++i)
        if (biomes[i] == biome)
        {
          if (weights[i] + weight > weights[top])
            top = i;
          weights[i] += weight;
          return;
        }
      if (weights[count] + weight > weights[top])
        top = count;
      biomes[count] = biome;
      weights[count++] = weight;
    }
    Weighted(xi, yi, (1 - xd) * (1 - yd));
    Weighted(xi + 1, yi, xd * (1 - yd));
    Weighted(xi, yi + 1, (1 - xd) * yd);
    Weighted(xi + 1, yi + 1, xd * yd);
    return biomes[top];
  }

  private static int Distance(Color32 a, Color32 b) => (a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b);

  private static void BiomeMaps()
  {
    Section("biome maps sample as the whole decoded map did");
    var legend = "Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF|Plains: FFFF00|Ocean: 0000FF|Mistlands: 7F7F7F|NotABiome: 123456";
    var colours = new[] { new Rgba32(0, 255, 0), new Rgba32(0, 127, 0), new Rgba32(127, 127, 0), new Rgba32(255, 255, 255), new Rgba32(255, 255, 0), new Rgba32(0, 0, 255), new Rgba32(127, 127, 127), new Rgba32(0x12, 0x34, 0x56), new Rgba32(10, 200, 30), new Rgba32(250, 250, 240) };
    foreach (int size in new[] { 1, 2, 63, 128, 129, 260 })
    {
      var random = new System.Random(size);
      var png = Png(size, (x, y) => colours[(x / 9 + y / 13 + random.Next(3)) % colours.Length], PngColorType.Rgb, PngBitDepth.Bit8);
      // As a world's oldest settings read it (the picture and its legend), and as a new world reads its file with
      // Compact Maps on.
      var map = ImageMapBiome.Create(png, legend, "biomes.png");
      var compactMap = ImageMapBiome.Create(WriteMap("biomemap.png", png, legend), compact: true);
      // The old decode: each colour to its nearest legend colour, unresolved entries reading as None.
      var candidates = map.LegendColors.Select(kv => (Biome: kv.Key, Color: kv.Value)).Concat(map.UnresolvedLegend.Select(u => (Biome: Heightmap.Biome.None, u.Color))).ToList();
      Heightmap.Biome[] old;
      using (var image = ISImage.Load<Rgba32>(ISConfiguration.Default, png))
      {
        image.Mutate(x => x.Flip(FlipMode.Vertical));
        old = Pixels(image, p =>
        {
          var c = new Color32(p.R, p.G, p.B, p.A);
          return candidates.OrderBy(d => Distance(c, d.Color)).First().Biome;
        });
      }
      foreach (var (compact, m) in new[] { (false, map), (true, compactMap) })
      {
        int differ = 0;
        foreach (var (x, y) in Points(size, size))
          if (m.GetValue(x, y) != OldBiome(old, size, x, y))
            differ++;
        C(m.Compact == compact && differ == 0 && m.Biomes.SequenceEqual(old) && m.Serialize().SequenceEqual(old.Select(BiomeRegistry.ToByte)),
          $"{size} px picture, {Mode(compact)}: every sample, every pixel and the saved bytes as before ({differ} differ)");
      }
    }
    // A saved map: bytes that are no biome read as None and are saved back as 0; bytes past the square are kept.
    var saved = new byte[50 * 50 + 7];
    var r2 = new System.Random(5);
    for (int i = 0; i < saved.Length; i++)
      saved[i] = (byte)(r2.Next(4) == 0 ? r2.Next(256) : r2.Next(11));
    var fromBytes = ImageMapBiome.Create(saved);
    var oldMap = saved.Select(BiomeRegistry.FromByte).ToArray();
    int wrong = 0;
    foreach (var (x, y) in Points(50, 50))
      if (fromBytes.GetValue(x, y) != OldBiome(oldMap, 50, x, y))
        wrong++;
    C(wrong == 0 && fromBytes.Size == 50 && fromBytes.Biomes.SequenceEqual(oldMap) && fromBytes.HasTail,
      "a saved map with bytes past its square and bytes that are no biome samples as before");
    C(fromBytes.Serialize().SequenceEqual(oldMap.Select(BiomeRegistry.ToByte)) && fromBytes.Serialize(true).SequenceEqual(oldMap.Select(BiomeRegistry.ToByte)),
      "and saves (and sends) the bytes it always did");
    // The same bytes as a compact world's tiles (its block written out here, as ImageMapBiome.ToBlock writes it).
    var block = BiomeBlock(50, saved);
    var back = ImageMapBiome.FromBlock(block);
    C(back.Compact && back.HasTail && back.Serialize().SequenceEqual(fromBytes.Serialize()) && Points(50, 51).All(p => back.GetValue(p.x, p.y) == fromBytes.GetValue(p.x, p.y)),
      $"in tiles, the bytes past its square included, it reads the same ({block.Length:N0} bytes for {saved.Length:N0})");
    C(back.ToBlock(false).SequenceEqual(block), "and saves the same tiles back");
    bool threw = false;
    try { fromBytes.ToBlock(false); } catch (InvalidOperationException) { threw = true; }
    C(threw && !fromBytes.Compact, "a map read from a world's bytes is decoded, and has no tiles to save");
  }

  // A biome map's block as a compact world saves it (ImageMapBiome.ToBlock): format 1, the tiles of the stored bytes,
  // the bytes past the square, no translation.
  private static byte[] BiomeBlock(int size, byte[] data)
  {
    static byte Stored(byte b) => b >= 1 && b <= 32 ? b : (byte)0;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)1);
    TileBlock.Encode(size, 1, 1, false, (y0, rows, band) =>
    {
      for (int r = 0; r < rows; r++)
        for (int x = 0; x < size; x++)
          band[r * size + x] = Stored(data[(y0 + r) * size + x]);
    }).WriteTo(writer);
    var tail = data.Skip(size * size).Select(Stored).ToArray();
    writer.Write(tail.Length);
    writer.Write(tail);
    writer.Write(false);
    writer.Flush();
    return stream.ToArray();
  }

  // ---- spawn maps ---------------------------------------------------------------------------------------------------

  private static void SpawnMaps()
  {
    Section("spawn and vegetation maps read as the whole decoded map did, past the edge too");
    var path = Path.Combine(Work, "spawnmap.png");
    var colours = new[] { new Rgba32(255, 0, 0), new Rgba32(0, 255, 0), new Rgba32(0, 0, 0), new Rgba32(1, 2, 3), new Rgba32(255, 255, 255) };
    const int N = 140;
    File.WriteAllBytes(path, Png(N, (x, y) => colours[(x / 11 + y / 5) % colours.Length], PngColorType.Rgb, PngBitDepth.Bit8));
    File.WriteAllText(Path.ChangeExtension(path, ".txt"), "255,0,0,255: -Boar\n0,255,0,255: +Neck, -Greyling\n");
    var map = ImageMapSpawn.Create(path);
    var compactMap = ImageMapSpawn.Create(path, compact: true);
    var indices = map.Indices;
    bool Agree(ImageMapSpawn m, byte[] flat, int size, float x, float y)
    {
      SpawnEntry got = null, want = null;
      Exception gotError = null, wantError = null;
      try { got = m.GetEntry(x, y); } catch (Exception e) { gotError = e; }
      try
      {
        // The old GetEntry over the flat array.
        int xi = Mathf.RoundToInt(x * (size - 1)), yi = Mathf.RoundToInt(y * (size - 1));
        var index = flat[yi * size + xi];
        want = index >= m.LegendEntries.Count ? null : m.LegendEntries[index];
      }
      catch (Exception e) { wantError = e; }
      return gotError != null ? wantError != null && gotError.GetType() == wantError.GetType() : wantError == null && got == want;
    }
    C(indices.Length == N * N && Points(N, 7).All(p => Agree(map, indices, N, p.x, p.y)), "a picture, decoded: every point, inside and outside the map, reads as before");
    C(compactMap.Compact && compactMap.Indices.SequenceEqual(indices) && Points(N, 7).All(p => Agree(compactMap, indices, N, p.x, p.y)),
      "a picture, compact: the same indices, every point as before");
    // A saved map whose length is no square: the bytes past it are read where a point past the edge lands on them.
    var saved = new byte[30 * 30 + 12];
    for (int i = 0; i < saved.Length; i++)
      saved[i] = (byte)(i % 7 == 0 ? 255 : i % 3);
    var pkg = new ZPackage();
    pkg.Write(2);
    pkg.Write((byte)255); pkg.Write((byte)255); pkg.Write((byte)255); pkg.Write((byte)255); pkg.Write("none");
    pkg.Write((byte)9); pkg.Write((byte)9); pkg.Write((byte)9); pkg.Write((byte)255); pkg.Write("-Boar");
    pkg.Write(saved);
    pkg.SetPos(0);
    var fromSave = ImageMapSpawn.Create(pkg, "");
    C(fromSave.Size == 30 && fromSave.Indices.SequenceEqual(saved) && Points(30, 9).All(p => Agree(fromSave, saved, 30, p.x, p.y))
      && Agree(fromSave, saved, 30, 1.02f, 1f) && Agree(fromSave, saved, 30, 1.2f, 1.2f),
      "a saved map with bytes past its square reads every point as before, the wrap past the edge and the throw past the end included");
    C(!fromSave.Compact && fromSave.SourceBytes().SequenceEqual(saved), "its source is the indices as saved, as always (a world export makes a picture of them)");
    var round = new ZPackage();
    fromSave.Serialize(round);
    round.SetPos(0);
    var again = ImageMapSpawn.Create(round, "");
    var back = ImageMapSpawn.FromBlock(compactMap.ToBlock());
    C(again.Indices.SequenceEqual(saved) && back.Compact && back.Indices.SequenceEqual(indices) && back.LegendEntries.Select(e => e.Data).SequenceEqual(compactMap.LegendEntries.Select(e => e.Data)),
      "and saves the same bytes as before; a compact map saves its tiles and legend, and reads back the same");
  }

  // ---- colour maps --------------------------------------------------------------------------------------------------

  private static void ColourMaps()
  {
    Section("paint and terrain maps sample as the whole decoded map did");
    var colours = new[] { new Rgba32(255, 0, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(10, 20, 30, 40), new Rgba32(0, 255, 0, 255), new Rgba32(200, 100, 50, 255) };
    foreach (int size in new[] { 1, 2, 64, 129, 200 })
    {
      var random = new System.Random(size + 3);
      var png = Png(size, (x, y) => random.Next(5) == 0 ? new Rgba32((byte)x, (byte)y, 7, 255) : colours[(x / 6 + y / 4) % colours.Length], PngColorType.RgbWithAlpha, PngBitDepth.Bit8);
      const string paint = "0,0,0,0: 255,0,0,255|1,1,1,1: 0,0,255,255", terrain = "Meadows: FF0000|Default: 0000FF|0,255,0,255: 00FF00";
      // As a world's settings read them (the picture and its legend), and as a new world reads their files with Compact
      // Maps on.
      foreach (var (name, map, compactMap) in new (string, ImageMapColor, ImageMapColor)[]
      {
        ("paint", ImageMapPaint.Create(png, paint), ImageMapPaint.Create(WriteMap("paintmap.png", png, paint), compact: true)),
        ("terrain", ImageMapTerrain.Create(png, terrain), ImageMapTerrain.Create(WriteMap("terrainmap.png", png, terrain), false, compact: true)),
      })
      {
        var legend = (Dictionary<Rgba32, Color32?>)typeof(ImageMapColor).GetField("Colors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(map)!;
        Color32?[] old;
        using (var image = ISImage.Load<Rgba32>(ISConfiguration.Default, png))
        {
          image.Mutate(x => x.Flip(FlipMode.Vertical));
          old = Pixels(image, p => legend.TryGetValue(p, out var c) ? c : new Color32(p.R, p.G, p.B, p.A));
        }
        foreach (var (compact, m) in new[] { (false, map), (true, compactMap) })
        {
          int differ = 0;
          foreach (var (x, y) in Points(size, size + 1))
          {
            bool got = m.TryGetValue(x, y, out var a);
            bool want = OldColour(old, size, x, y, out var b);
            if (got != want || a != b)
              differ++;
          }
          C(m.Compact == compact && differ == 0, $"{size} px {name} map, {Mode(compact)}: every sample as before ({differ} differ)");
        }
      }
    }
  }

  private static bool OldColour(Color32?[] map, int size, float x, float y, out UnityEngine.Color color)
  {
    float xa = x * (size - 1), ya = y * (size - 1);
    int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
    float xd = xa - xi, yd = ya - yi;
    int x0 = Mathf.Clamp(xi, 0, size - 1), x1 = Mathf.Clamp(xi + 1, 0, size - 1);
    int y0 = Mathf.Clamp(yi, 0, size - 1), y1 = Mathf.Clamp(yi + 1, 0, size - 1);
    var p00 = map[y0 * size + x0];
    var p10 = map[y0 * size + x1];
    var p01 = map[y1 * size + x0];
    var p11 = map[y1 * size + x1];
    if (p00 == null || p10 == null || p01 == null || p11 == null)
    {
      color = UnityEngine.Color.black;
      return false;
    }
    var a = Color32.Lerp(p00.Value, p10.Value, xd);
    var b = Color32.Lerp(p01.Value, p11.Value, xd);
    var c = Color32.Lerp(a, b, yd);
    color = new UnityEngine.Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    return true;
  }
}

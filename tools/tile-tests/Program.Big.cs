// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// "dotnet run -c Release -- big": every map kind at 16,384 px across (the largest a map is), one kind in a process of its own so
// each has the memory to itself (under 6 GB in all: run it with DOTNET_GCHeapHardLimit=0x140000000 under heavy.sh 6G). A picture
// is written row by row from a function of the pixel's position (no whole image is ever held), the map is made from the file as
// a new world makes it, with Compact Maps off and on, and then:
//   - its samples are the function's (an oracle that holds no map: the same arithmetic as the map's bilinear read);
//   - the peak memory of making it stays far below what holding the decoded picture too would take;
//   - a compact map saved and read back samples the same, and the world file holds what the tiles do;
//   - the overflow guards: a map too large for one array's worth of bytes is refused when its tiles are read.
// "dotnet run -c Release -- big-kind <kind> <compact 0|1>" is one of them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private const int Big = 16384;

  private static readonly string[] BigKinds = ["height", "height-alpha", "forest8", "biome", "spawn", "paint", "terrain", "location", "altbiome"];

  // ---- the pictures: functions of a pixel's position (file row y counts from the north) ---------------------------------------

  // 16-bit heights: a slope, with plateaus of one value (tiles that are all one value, as sea is) and a ripple.
  private static ushort BigHeight(int x, int y) =>
    (x / 128 + y / 128) % 5 == 0 ? (ushort)1234 : (ushort)(((x * 7 + y * 13) & 0x3FFF) * 3 + ((x ^ y) & 7));
  private static ushort BigAlpha(int x, int y) => (ushort)((x * 3 + y * 5) & 0xFFFF);
  private static byte BigGrey8(int x, int y) => (byte)((x / 3 + y / 5) & 0xFF);

  private static readonly Rgba32Colour[] BigBiomeColours = [new Rgba32Colour(0, 255, 0), new Rgba32Colour(0, 0, 255), new Rgba32Colour(255, 255, 255), new Rgba32Colour(255, 255, 0)];
  private static Rgba32Colour BigBiome(int x, int y) => BigBiomeColours[(x / 300 + y / 200) % 4];

  // A spawn picture: legend colours in bands, black (nothing) between.
  private static Rgba32Colour BigSpawn(int x, int y) => ((x / 97 + y / 61) % 5) switch
  {
    0 => new Rgba32Colour(255, 0, 0), 1 => new Rgba32Colour(0, 255, 0), 2 => new Rgba32Colour(0, 0, 0), 3 => new Rgba32Colour(255, 255, 255), _ => new Rgba32Colour(0, 0, 255),
  };

  // Paint and terrain: regions with odd pixels in them, so that no tile is one value.
  private static Rgba32Colour BigPaint(int x, int y) =>
    (x * 31 + y * 17) % 97 == 0 ? new Rgba32Colour(10, 20, 30, 40) : ((x / 211 + y / 173) % 3) switch { 0 => new Rgba32Colour(255, 0, 0), 1 => new Rgba32Colour(0, 255, 0), _ => new Rgba32Colour(0, 0, 255) };

  // The location pins: one pixel each, a colour of its own (so a pin is that location), 5 of each at fixed places.
  private static readonly string[] BigLocations = ["StartTemple", "Eikthyrnir", "GDKing", "GoblinKing", "Bonemass", "Dragonqueen", "Vendor_BlackForest", "StoneTower1"];
  private static readonly Rgba32Colour[] BigLocationColours = [new Rgba32Colour(255, 0, 0), new Rgba32Colour(255, 153, 0), new Rgba32Colour(0, 255, 0), new Rgba32Colour(255, 255, 0), new Rgba32Colour(0, 255, 255), new Rgba32Colour(74, 134, 232), new Rgba32Colour(0, 0, 255), new Rgba32Colour(204, 65, 37)];
  private static (int x, int y) BigPin(int location, int n) => (200 + (location * 1999 + n * 3001) % 15900, 300 + (n * 2749 + location * 331 + (location * n) % 7) % 15700);

  // The alt-biome picture: three of the default palette's colours in bands, black between.
  private static Rgba32Colour BigAlt(int x, int y) => ((x / 211 + y / 197) % 4) switch { 0 => new Rgba32Colour(0x2D, 0x46, 0x13), 1 => new Rgba32Colour(0xE5, 0xC5, 0x4B), 2 => new Rgba32Colour(0, 0, 0), _ => new Rgba32Colour(0xC2, 0x18, 0x5B) };

  // A colour in a PNG, written as bytes (so no ImageSharp pixel type is needed here).
  private readonly record struct Rgba32Colour(byte R, byte G, byte B, byte A = 255);

  // ---- writing a PNG row by row ------------------------------------------------------------------------------------------------

  // A PNG of width x height: signature, IHDR, one zlib stream in IDAT chunks (each row filtered with Sub), IEND. fill(y, row)
  // writes file row y's raw bytes (from the north). Memory: a row, and an IDAT chunk's worth of compressed bytes.
  private static void WriteBigPng(string path, int width, int height, int colorType, int depth, Action<int, byte[]> fill)
  {
    int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
    int bpp = channels * depth / 8, rowBytes = width * bpp;
    using var file = new BufferedStream(File.Create(path), 1 << 20);
    file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    void Chunk(string type, byte[] data, int length)
    {
      var header = new byte[8];
      header[0] = (byte)(length >> 24); header[1] = (byte)(length >> 16); header[2] = (byte)(length >> 8); header[3] = (byte)length;
      for (int i = 0; i < 4; i++)
        header[4 + i] = (byte)type[i];
      file.Write(header);
      file.Write(data, 0, length);
      var crc = Crc32.Compute([.. header.Skip(4), .. data.Take(length)], 0, 4 + length);
      file.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }
    var ihdr = new byte[13];
    ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
    ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
    ihdr[8] = (byte)depth;
    ihdr[9] = (byte)colorType;
    Chunk("IHDR", ihdr, 13);
    var sink = new ChunkingStream(file, (type, data, length) => Chunk(type, data, length));
    sink.Write([0x78, 0x01]);
    uint a = 1, b = 0;
    using (var deflate = new DeflateStream(sink, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
    {
      var raw = new byte[rowBytes];
      var line = new byte[rowBytes + 1];
      for (int y = 0; y < height; y++)
      {
        fill(y, raw);
        line[0] = 1;
        for (int i = 0; i < rowBytes; i++)
          line[1 + i] = (byte)(raw[i] - (i >= bpp ? raw[i - bpp] : 0));
        for (int i = 0; i < line.Length; i++)
        {
          a = (a + line[i]) % 65521;
          b = (b + a) % 65521;
        }
        deflate.Write(line, 0, line.Length);
      }
    }
    var adler = (b << 16) | a;
    sink.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
    sink.Flush();
    Chunk("IEND", [], 0);
  }

  // Compressed bytes collected into IDAT chunks of about 1 MB.
  private sealed class ChunkingStream(Stream into, Action<string, byte[], int> chunk) : Stream
  {
    private readonly byte[] buffer = new byte[1 << 20];
    private int held;
    public override void Write(byte[] data, int offset, int count)
    {
      while (count > 0)
      {
        int n = Math.Min(count, buffer.Length - held);
        Buffer.BlockCopy(data, offset, buffer, held, n);
        held += n;
        offset += n;
        count -= n;
        if (held == buffer.Length)
          Flush();
      }
    }
    public override void Flush()
    {
      if (held > 0)
        chunk("IDAT", buffer, held);
      held = 0;
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  private static void Put16(byte[] row, int pixel, int channel, int channels, ushort value)
  {
    int at = (pixel * channels + channel) * 2;
    row[at] = (byte)(value >> 8);
    row[at + 1] = (byte)value;
  }

  private static void PutRgba(byte[] row, int pixel, Rgba32Colour c)
  {
    row[4 * pixel] = c.R; row[4 * pixel + 1] = c.G; row[4 * pixel + 2] = c.B; row[4 * pixel + 3] = c.A;
  }

  // ---- the oracle: the maps' own bilinear read, over a function ------------------------------------------------------------------

  private static float OracleFloat(Func<int, int, float> pixel, int size, float x, float y)
  {
    float xa = x * (size - 1), ya = y * (size - 1);
    int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
    float xd = xa - xi, yd = ya - yi;
    int x0 = Mathf.Clamp(xi, 0, size - 1), x1 = Mathf.Clamp(xi + 1, 0, size - 1);
    int y0 = Mathf.Clamp(yi, 0, size - 1), y1 = Mathf.Clamp(yi + 1, 0, size - 1);
    return Mathf.Lerp(Mathf.Lerp(pixel(x0, y0), pixel(x1, y0), xd), Mathf.Lerp(pixel(x0, y1), pixel(x1, y1), xd), yd);
  }

  // Map row y (0 = south) is file row size - 1 - y.
  private static int FileRow(int y) => Big - 1 - y;

  private static IEnumerable<(float x, float y)> BigPoints(int count, int seed)
  {
    var random = new System.Random(seed);
    for (int i = 0; i < count; i++)
      yield return ((float)random.NextDouble(), (float)random.NextDouble());
    foreach (var v in new[] { 0f, 1f, 0.5f, 0.99999f, 1e-5f })
      foreach (var w in new[] { 0f, 1f, 0.25f })
        yield return (v, w);
  }

  // ---- one kind, in this process --------------------------------------------------------------------------------------------------

  private static int BigKind(string kind, bool compact)
  {
    var dir = Path.Combine(Path.GetTempPath(), $"bc-big-{kind}-{(compact ? "c" : "p")}-{Environment.ProcessId}");
    Directory.CreateDirectory(dir);
    try
    {
      var baseline = Proc();
      long startRss = 0;
      void Begin() { GC.Collect(); ResetPeak(); startRss = Proc().Rss; }
      var sw = Stopwatch.StartNew();
      string P(string name) => Path.Combine(dir, name);
      long peakLimitMb;
      ImageMapBase map;
      Func<(float x, float y), bool> sameAsOracle;
      var failures = new List<string>();
      switch (kind)
      {
        case "height":
        case "height-alpha":
        {
          bool alpha = kind == "height-alpha";
          WriteBigPng(P("heightmap.png"), Big, Big, alpha ? 4 : 0, 16, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
            {
              Put16(row, x, 0, alpha ? 2 : 1, BigHeight(x, y));
              if (alpha)
                Put16(row, x, 1, 2, BigAlpha(x, y));
            }
          });
          Report($"{kind}: picture written in {sw.ElapsedMilliseconds} ms, {new FileInfo(P("heightmap.png")).Length:N0} bytes");
          Begin();
          var mode = alpha ? ImageMapFloat.HeightAlpha.Blend : ImageMapFloat.HeightAlpha.None;
          var float16 = ImageMapFloat.Create(P("heightmap.png"), mode, compact)!;
          map = float16;
          peakLimitMb = compact ? (alpha ? 800 : 700) : alpha ? 1600 : 900;
          float Value(int x, int y) => alpha ? new SixLabors.ImageSharp.PixelFormats.La32(BigHeight(x, FileRow(y)), 65535).ToVector4().X : new SixLabors.ImageSharp.PixelFormats.L16(BigHeight(x, FileRow(y))).ToVector4().X;
          sameAsOracle = p => Same(float16.GetValue(p.x, p.y), OracleFloat(Value, Big, p.x, p.y))
            && (!alpha || Same(float16.GetAlpha(p.x, p.y), OracleFloat((x, y) => BigAlpha(x, FileRow(y)) / 65535f, Big, p.x, p.y)));
          break;
        }
        case "forest8":
        {
          WriteBigPng(P("forestmap.png"), Big, Big, 0, 8, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
              row[x] = BigGrey8(x, y);
          });
          Begin();
          var float8 = ImageMapFloat.Create(P("forestmap.png"), false, compact)!;
          map = float8;
          peakLimitMb = compact ? 600 : 900;
          float Value(int x, int y) => new SixLabors.ImageSharp.PixelFormats.L16((ushort)(BigGrey8(x, FileRow(y)) * 257)).ToVector4().X;
          sameAsOracle = p => Same(float8.GetValue(p.x, p.y), OracleFloat(Value, Big, p.x, p.y));
          break;
        }
        case "biome":
        {
          WriteBigPng(P("biomemap.png"), Big, Big, 2, 8, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
            {
              var c = BigBiome(x, y);
              row[3 * x] = c.R; row[3 * x + 1] = c.G; row[3 * x + 2] = c.B;
            }
          });
          File.WriteAllLines(P("biomemap.txt"), "Meadows: 00FF00|Ocean: 0000FF|Mountain: FFFFFF|Plains: FFFF00".Split('|'));
          Begin();
          var biome = ImageMapBiome.Create(P("biomemap.png"), compact)!;
          map = biome;
          peakLimitMb = compact ? 450 : 700;
          var biomeOf = new Dictionary<Rgba32Colour, Heightmap.Biome>
          {
            [new(0, 255, 0)] = Heightmap.Biome.Meadows, [new(0, 0, 255)] = Heightmap.Biome.Ocean,
            [new(255, 255, 255)] = Heightmap.Biome.Mountain, [new(255, 255, 0)] = Heightmap.Biome.Plains,
          };
          // The biome at a point: the map's own rule (SampleBiomeWeighted), over the four pixels around it.
          sameAsOracle = p =>
          {
            float xa = p.x * (Big - 1), ya = p.y * (Big - 1);
            int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
            float xd = xa - xi, yd = ya - yi;
            var corners = new[] { (xi, yi, (1 - xd) * (1 - yd)), (xi + 1, yi, xd * (1 - yd)), (xi, yi + 1, (1 - xd) * yd), (xi + 1, yi + 1, xd * yd) };
            var biomes = new Heightmap.Biome[4];
            var weights = new float[4];
            int count = 0, top = 0;
            foreach (var (cx, cy, w) in corners)
            {
              var b = biomeOf[BigBiome(Mathf.Clamp(cx, 0, Big - 1), FileRow(Mathf.Clamp(cy, 0, Big - 1)))] & BiomeRegistry.Usable;
              int i = 0;
              for (; i < count; i++)
                if (biomes[i] == b)
                {
                  if (weights[i] + w > weights[top])
                    top = i;
                  weights[i] += w;
                  break;
                }
              if (i == count)
              {
                if (weights[count] + w > weights[top])
                  top = count;
                biomes[count] = b;
                weights[count++] = w;
              }
            }
            return biome.GetValue(p.x, p.y) == biomes[top];
          };
          break;
        }
        case "spawn":
        {
          WriteBigPng(P("spawnmap.png"), Big, Big, 6, 8, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
              PutRgba(row, x, BigSpawn(x, y));
          });
          File.WriteAllText(P("spawnmap.txt"), "255,0,0,255: -Boar\n0,255,0,255: +Neck\n0,0,255,255: Deer\n");
          Begin();
          var spawn = ImageMapSpawn.Create(P("spawnmap.png"), compact)!;
          map = spawn;
          peakLimitMb = compact ? 450 : 700;
          // The legend: white is entry 0 ("none"), then the file's lines in order; black is nothing.
          string Entry(Rgba32Colour c) => c switch
          {
            { R: 255, G: 0, B: 0 } => "-Boar", { R: 0, G: 255, B: 0 } => "+Neck", { R: 0, G: 0, B: 255 } => "Deer", { R: 255, G: 255, B: 255 } => "none", _ => null,
          };
          sameAsOracle = p =>
          {
            int xi = Mathf.RoundToInt(p.x * (Big - 1)), yi = Mathf.RoundToInt(p.y * (Big - 1));
            return spawn.GetEntry(p.x, p.y)?.Data == Entry(BigSpawn(xi, FileRow(yi)));
          };
          break;
        }
        case "paint":
        case "terrain":
        {
          var name = kind == "paint" ? "paintmap" : "terrainmap";
          WriteBigPng(P(name + ".png"), Big, Big, 6, 8, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
              PutRgba(row, x, BigPaint(x, y));
          });
          // The legend turns red into green (paint) or into Meadows' ground (terrain); every other colour, the odd pixel's too, reads as itself.
          File.WriteAllText(P(name + ".txt"), kind == "paint" ? "00FF00FF: FF0000FF\n" : "Meadows: FF0000\n");
          Begin();
          ImageMapColor color = kind == "paint" ? ImageMapPaint.Create(P(name + ".png"), compact)! : ImageMapTerrain.Create(P(name + ".png"), false, compact)!;
          map = color;
          peakLimitMb = compact ? 600 : 1600;
          // Compared with the same map read through ImageSharp's whole decode at a small corner is not possible at this size:
          // the oracle is the picture's function through the legend, by the colour's own rule (exact colour match).
          Color32? Resolve(Rgba32Colour c)
          {
            if (kind == "paint")
              return c is { R: 255, G: 0, B: 0, A: 255 } ? new Color32(0, 255, 0, 255) : new Color32(c.R, c.G, c.B, c.A);
            return c is { R: 255, G: 0, B: 0, A: 255 } ? new Color32(0, 0, 0, 0) : new Color32(c.R, c.G, c.B, c.A);
          }
          sameAsOracle = p =>
          {
            float xa = p.x * (Big - 1), ya = p.y * (Big - 1);
            int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
            float xd = xa - xi, yd = ya - yi;
            int x0 = Mathf.Clamp(xi, 0, Big - 1), x1 = Mathf.Clamp(xi + 1, 0, Big - 1), y0 = Mathf.Clamp(yi, 0, Big - 1), y1 = Mathf.Clamp(yi + 1, 0, Big - 1);
            var p00 = Resolve(BigPaint(x0, FileRow(y0))); var p10 = Resolve(BigPaint(x1, FileRow(y0)));
            var p01 = Resolve(BigPaint(x0, FileRow(y1))); var p11 = Resolve(BigPaint(x1, FileRow(y1)));
            bool got = color.TryGetValue(p.x, p.y, out var c);
            if (p00 == null || p10 == null || p01 == null || p11 == null)
              return !got;
            var a = Color32.Lerp(p00.Value, p10.Value, xd);
            var b = Color32.Lerp(p01.Value, p11.Value, xd);
            var d = Color32.Lerp(a, b, yd);
            return got && c == new UnityEngine.Color(d.r / 255f, d.g / 255f, d.b / 255f, d.a / 255f);
          };
          break;
        }
        case "location":
        {
          WriteBigPng(P("locationmap.png"), Big, Big, 2, 8, (y, row) =>
          {
            Array.Clear(row, 0, row.Length);
            for (int l = 0; l < BigLocations.Length; l++)
              for (int n = 0; n < 5; n++)
              {
                var (px, py) = BigPin(l, n);
                if (FileRow(py) == y)
                {
                  row[3 * px] = BigLocationColours[l].R; row[3 * px + 1] = BigLocationColours[l].G; row[3 * px + 2] = BigLocationColours[l].B;
                }
              }
          });
          File.WriteAllLines(P("locationmap.txt"), BigLocations.Select((n, i) => $"{n}: {BigLocationColours[i].R},{BigLocationColours[i].G},{BigLocationColours[i].B}"));
          Begin();
          var location = ImageMapLocation.Create(P("locationmap.png"), exactPins: true)!;
          map = location;
          peakLimitMb = 450;
          sameAsOracle = _ => true;
          // The pins: each location's five pixels, in the order the scan finds them (south first, west to east).
          for (int l = 0; l < BigLocations.Length; l++)
          {
            var want = Enumerable.Range(0, 5).Select(n => BigPin(l, n)).OrderBy(pin => pin.y).ThenBy(pin => pin.x)
              .Select(pin => new Vector2(pin.x / (float)(Big - 1), pin.y / (float)(Big - 1))).ToList();
            var got = location.GetAllSpawns(BigLocations[l]).ToList();
            if (!want.SequenceEqual(got))
              failures.Add($"{BigLocations[l]}: {got.Count} pins, not the {want.Count} planted ({string.Join(" ", got.Take(2))} ...)");
          }
          break;
        }
        case "altbiome":
        {
          WriteBigPng(P("altbiomemap.png"), Big, Big, 6, 8, (y, row) =>
          {
            for (int x = 0; x < Big; x++)
              PutRgba(row, x, BigAlt(x, y));
          });
          Begin();
          var alt = ImageMapAltBiome.Create(P("altbiomemap.png"))!;
          map = alt;
          peakLimitMb = 700;
          var classOf = alt.Classes.Select((c, i) => (c, i)).Where(t => t.i > 0 && !t.c.IsPin).ToDictionary(t => new Rgba32Colour(t.c.Color.r, t.c.Color.g, t.c.Color.b), t => (byte)t.i);
          sameAsOracle = p =>
          {
            int xi = Mathf.Clamp(Mathf.RoundToInt(p.x * (Big - 1)), 0, Big - 1), yi = Mathf.Clamp(Mathf.RoundToInt(p.y * (Big - 1)), 0, Big - 1);
            var c = BigAlt(xi, FileRow(yi));
            return alt.GetClass(p.x, p.y) == (classOf.TryGetValue(c, out var k) ? k : (byte)0);
          };
          break;
        }
        default:
          throw new ArgumentException(kind);
      }
      var made = Proc();
      long createdMs = sw.ElapsedMilliseconds;
      Report($"{kind} {(compact ? "compact" : "decoded")}: made in {createdMs} ms; size {map.Size}; peak {made.Hwm} MB over {startRss} MB when it began");
      if (map.Size != Big && kind != "location")
        failures.Add($"the map is {map.Size} px across, not {Big}");
      if (made.Hwm - startRss > peakLimitMb)
        failures.Add($"making it took {made.Hwm - startRss} MB at its peak, more than the {peakLimitMb} MB the tiles and a few rows need");
      if (map.Compact != compact && kind is not ("location" or "altbiome"))
        failures.Add("the map is not as compact as asked");
      int wrong = BigPoints(20000, 5).Count(p => !sameAsOracle(p));
      if (wrong > 0)
        failures.Add($"{wrong} of the samples differ from the picture");

      // Saved and read back (a world made since 0.10): what a joining client is sent, and what a server reads.
      if (kind is not ("location" or "altbiome"))
      {
        var settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, CompactMaps = compact, HeightMapAlpha = kind == "height-alpha" };
        string field = kind switch
        {
          "height" or "height-alpha" => "HeightMap", "forest8" => "ForestMap", "biome" => "BiomeMap", "spawn" => "SpawnMap", "paint" => "PaintMap", _ => "TerrainMap",
        };
        Field(field).SetValue(settings, map);
        var pkg = new ZPackage();
        settings.Serialize(pkg, true, true, 12);
        var bytes = PackageBytes.Buffer(pkg, out int length);
        Report($"{kind} {(compact ? "compact" : "decoded")}: sent to a client as {length:N0} bytes");
        Field(field).SetValue(settings, null);
        var back = BC.BetterContinentsSettings.Load(PackageBytes.Read(new MemoryStream(bytes, 0, length), length));
        var read = (ImageMapBase)Field(field).GetValue(back)!;
        if (read == null || read.Size != Big)
          failures.Add("read back from the package it is not a 16384 px map");
        else if (compact && !read.Compact)
          failures.Add("read back from the package it is not compact");
        else if (kind is "height" or "height-alpha" or "forest8")
        {
          var f = (ImageMapFloat)read;
          int wrongBack = BigPoints(5000, 6).Count(p => !Same(f.GetValue(p.x, p.y), ((ImageMapFloat)map).GetValue(p.x, p.y)));
          if (wrongBack > 0)
            failures.Add($"{wrongBack} samples differ after the package is read back");
        }
        else if (kind == "biome")
        {
          int wrongBack = BigPoints(5000, 6).Count(p => ((ImageMapBiome)read).GetValue(p.x, p.y) != ((ImageMapBiome)map).GetValue(p.x, p.y));
          if (wrongBack > 0)
            failures.Add($"{wrongBack} samples differ after the package is read back");
        }
        if (compact && length > 400_000_000)
          failures.Add($"the compact map's package is {length:N0} bytes");
        // A map that holds only its tiles (as a loaded world's does) written back out as a picture (a world export's sources, a
        // save in an older format): rows, never the whole image, and every pixel the picture's.
        if (compact && read != null && read.Compact && kind is "height" or "paint" or "terrain")
        {
          var sourceWatch = Stopwatch.StartNew();
          var picture = read.SourceBytes();
          Report($"{kind} compact: written back out from its tiles as {picture.Length:N0} bytes in {sourceWatch.ElapsedMilliseconds} ms; peak {Proc().Hwm} MB");
          long wrongPixels = 0;
          if (kind == "height")
          {
            using var rows = PngRows<SixLabors.ImageSharp.PixelFormats.L16>.TryOpen(picture);
            if (rows == null)
              failures.Add("the picture written from the tiles is not one PngRows reads");
            else
              rows.ReadRows(0, Big, (r, row) =>
              {
                int file = FileRow(r);
                for (int x = 0; x < row.Length; x++)
                  if (row[x].PackedValue != BigHeight(x, file))
                    wrongPixels++;
              });
          }
          else
          {
            using var rows = PngRows<SixLabors.ImageSharp.PixelFormats.Rgba32>.TryOpen(picture);
            if (rows == null)
              failures.Add("the picture written from the tiles is not one PngRows reads");
            else
              rows.ReadRows(0, Big, (r, row) =>
              {
                int file = FileRow(r);
                for (int x = 0; x < row.Length; x++)
                {
                  var want = BigPaint(x, file);
                  if (row[x].R != want.R || row[x].G != want.G || row[x].B != want.B || row[x].A != want.A)
                    wrongPixels++;
                }
              });
          }
          if (wrongPixels > 0)
            failures.Add($"{wrongPixels} pixels of the picture written from the tiles differ from the picture");
        }
        if (!compact && kind is "biome" or "spawn" && length < (long)Big * Big)
          failures.Add("a decoded biome or spawn map should save its bytes, one a pixel");
      }
      foreach (var f in failures)
        System.Console.WriteLine("  FAIL " + kind + " " + (compact ? "compact" : "decoded") + ": " + f);
      if (failures.Count == 0)
        System.Console.WriteLine($"  PASS {kind} {(compact ? "compact" : "decoded")}: 16384 px, {made.Hwm - startRss} MB peak (limit {peakLimitMb}), every sample the picture's");
      return failures.Count == 0 ? 0 : 1;
    }
    finally
    {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  private static void Report(string line) => System.Console.WriteLine("    " + line);

  // ---- all of them, each in a process of its own --------------------------------------------------------------------------------------

  private static int Big16k()
  {
    int failed = 0, ran = 0;
    foreach (var kind in BigKinds)
      foreach (bool compact in (kind is "location" or "altbiome") ? new[] { false } : new[] { true, false })
      {
        var child = Process.Start(new ProcessStartInfo("dotnet", $"\"{typeof(Program).Assembly.Location}\" big-kind {kind} {(compact ? 1 : 0)}")
        {
          RedirectStandardOutput = true,
          UseShellExecute = false,
        })!;
        string output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        System.Console.Write(output);
        ran++;
        if (child.ExitCode != 0)
        {
          failed++;
          if (!output.Contains("FAIL"))
            System.Console.WriteLine($"  FAIL {kind} {(compact ? "compact" : "decoded")}: the process ended with {child.ExitCode}");
        }
      }
    System.Console.WriteLine("== the overflow guards");
    failed += BigGuards();
    System.Console.WriteLine($"tile-tests big: {ran - failed} of {ran} 16384 px runs passed");
    return failed == 0 ? 0 : 1;
  }

  // A block whose map would need more bytes than an array can hold is refused when it is read, not read as a map that wraps.
  private static int BigGuards()
  {
    int failed = 0;
    void Check(bool ok, string what)
    {
      System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
      if (!ok) failed++;
    }
    // 65536 px across is the largest a block may declare (TileBlock.MaxSize), and 65536 * 65536 bytes is more than any array.
    const int Huge = 65536;
    var block = TileBlock.Encode(Huge, 1, 1, false, (y0, rows, band) => Array.Clear(band, 0, rows * Huge));
    Check(block.Size == Huge && block.Data.Length < 4_000_000, $"a 65536 px block of one value is {block.Data.Length:N0} bytes");
    byte[] biomeBlock, spawnBlock;
    using (var stream = new MemoryStream())
    using (var writer = new BinaryWriter(stream))
    {
      writer.Write((byte)1);
      block.WriteTo(writer);
      writer.Write(0);
      writer.Write(false);
      writer.Flush();
      biomeBlock = stream.ToArray();
    }
    using (var stream = new MemoryStream())
    using (var writer = new BinaryWriter(stream))
    {
      writer.Write((byte)1);
      var legend = new ZPackage();
      legend.Write(0);
      var legendBytes = legend.GetArray();
      writer.Write(legendBytes.Length);
      writer.Write(legendBytes);
      block.WriteTo(writer);
      writer.Write(0);
      writer.Flush();
      spawnBlock = stream.ToArray();
    }
    bool Refused(Action read)
    {
      try { read(); return false; }
      catch (InvalidDataException) { return true; }
    }
    Check(Refused(() => ImageMapBiome.FromBlock(biomeBlock)), "a biome map of 65536 x 65536 pixels (no array holds it) is refused, not read as a map that wraps");
    Check(Refused(() => ImageMapSpawn.FromBlock(spawnBlock)), "so is a spawn or vegetation map");
    return failed;
  }
}

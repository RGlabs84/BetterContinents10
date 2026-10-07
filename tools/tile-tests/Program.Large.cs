// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// What 16384 px maps ask of the code around the tiles, at sizes a test can afford: a picture too large is refused from its
// header, the location map's sparse pixels find the pins the whole array did, a settings package is handled without the copies
// ZPackage makes and refused with a message past its limit, and the stream's room is the size of the last package.
// The maps themselves at 16384 px are "big" (below).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // A PNG header and nothing else: its signature and an IHDR of this size (16-bit grey).
  private static byte[] HeaderOnlyPng(int width, int height)
  {
    var ihdr = new byte[13];
    void Be(int at, int v) { ihdr[at] = (byte)(v >> 24); ihdr[at + 1] = (byte)(v >> 16); ihdr[at + 2] = (byte)(v >> 8); ihdr[at + 3] = (byte)v; }
    Be(0, width);
    Be(4, height);
    ihdr[8] = 16;
    ihdr[9] = 0;
    using var stream = new MemoryStream();
    stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    stream.Write([0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R']);
    stream.Write(ihdr);
    var crc = Crc32.Compute([(byte)'I', (byte)'H', (byte)'D', (byte)'R', .. ihdr], 0, 17);
    stream.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    return stream.ToArray();
  }

  private static void LargeSizes()
  {
    Section("a picture larger than 16384 px is refused from its header, with a message");
    var big = WriteMap("too-big.png", HeaderOnlyPng(16385, 16385));
    var wide = WriteMap("too-wide.png", HeaderOnlyPng(20000, 8));
    var full = WriteMap("full.png", HeaderOnlyPng(16384, 16384));
    C(ImageMapBase.PictureSize(big) == (16385, 16385) && ImageMapBase.PictureSize(full) == (16384, 16384) && ImageMapBase.MaxMapSize == 16384,
      "the size is read from the first 24 bytes of the file; 16384 is the largest");
    var jpeg = Path.Combine(Work, "not-a-png.jpg");
    using (var image = new Image<Rgb24>(40, 30))
      image.SaveAsJpeg(jpeg);
    var text = Path.Combine(Work, "not-a-picture.png");
    File.WriteAllText(text, "this is no picture at all, only text that is long enough to be read as a header");
    C(ImageMapBase.PictureSize(jpeg) == (40, 30) && ImageMapBase.PictureSize(text) == null,
      "a JPEG's size comes from ImageSharp's reading of its start, and a file that is no picture has none");
    foreach (var (name, path) in new[] { ("a 16385 px square", big), ("20000 x 8", wide) })
    {
      lock (LogHandler.Lines) LogHandler.Lines.Clear();
      var made = new ImageMapBase[]
      {
        ImageMapFloat.Create(path, ImageMapFloat.HeightAlpha.None), ImageMapFloat.Create(path, false, compact: true), ImageMapBiome.Create(path),
        ImageMapSpawn.Create(path), ImageMapPaint.Create(path), ImageMapTerrain.Create(path), ImageMapLocation.Create(path), ImageMapAltBiome.Create(path),
      };
      string[] lines;
      lock (LogHandler.Lines) lines = LogHandler.Lines.Where(l => l.Contains("largest map")).ToArray();
      C(made.All(m => m == null) && lines.Length == made.Length && lines[0].Contains("16384") && lines[0].Contains(Path.GetFileName(path)),
        $"{name}: no map of any kind is made, and each says so (\"{lines.FirstOrDefault()?.Substring(0, Math.Min(150, lines[0].Length))}...\")");
    }
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    // 16384 is allowed: this one has no data, so it fails as a damaged picture would, not as a large one (and allocates nothing).
    var empty = ImageMapFloat.Create(full, ImageMapFloat.HeightAlpha.None);
    string[] errors;
    lock (LogHandler.Lines) errors = LogHandler.Lines.Where(l => l.Contains("Cannot load texture")).ToArray();
    C(empty == null && errors.Length == 1 && !LogHandler.Lines.Any(l => l.Contains("largest map")), "a 16384 px header passes the size check (the picture then fails to read as a damaged one does)");
    // A map made from a world's own settings is never refused for its size.
    var pixels = Png(8, (x, y) => new L16((ushort)(x * 1000)), SixLabors.ImageSharp.Formats.Png.PngColorType.Grayscale, SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit16);
    C(ImageMapFloat.Create(pixels, ImageMapFloat.HeightAlpha.None) != null, "a world's saved picture is read as it always was");
  }

  // ImageMapLocation before the sparse pixels: every pixel in an array, found one area at a time (the same flood fill, the
  // same scan), each area's pin at the pixel nearest its middle. The pins per colour, in the order they were found.
  private static Dictionary<(byte, byte, byte, byte), List<Vector2>> OldPins(Rgba32[] pixels, int size)
  {
    var black = new Color32(0, 0, 0, 255);
    var colours = pixels.Select(p => new Color32(p.R, p.G, p.B, p.A)).ToArray();
    int Index(int x, int y) => y * size + x;
    bool Compare(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;
    var result = new Dictionary<(byte, byte, byte, byte), List<Vector2>>();
    var q = new Queue<(int x, int y)>();
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
      {
        var color = colours[Index(x, y)];
        if (color.r == 0 && color.g == 0 && color.b == 0 && color.a == 255)
          continue;
        var area = new List<Vector2Int>();
        var source = color;
        q.Clear();
        colours[Index(x, y)] = black;
        q.Enqueue((x, y));
        while (q.Count > 0)
        {
          var (px, py) = q.Dequeue();
          area.Add(new Vector2Int(px, py));
          foreach (var (nx, ny) in new[] { (px + 1, py), (px - 1, py), (px, py + 1), (px, py - 1) })
            if (nx >= 0 && nx < size && ny >= 0 && ny < size && Compare(colours[Index(nx, ny)], source))
            {
              colours[Index(nx, ny)] = black;
              q.Enqueue((nx, ny));
            }
        }
        var pin = new ImageMapLocation { Size = size }.PinPosition(ImageMapLocation.Middle(area));
        var key = (color.r, color.g, color.b, color.a);
        if (!result.TryGetValue(key, out var list))
          result[key] = list = [];
        list.Add(pin);
      }
    return result;
  }

  private static void LocationMaps()
  {
    Section("the location map's sparse pixels find the pins the whole array did");
    foreach (int size in new[] { 1, 5, 127, 128, 129, 300, 520 })
    {
      var random = new System.Random(size);
      var palette = new[] { new Rgba32(255, 0, 0), new Rgba32(0, 255, 0), new Rgba32(255, 153, 0), new Rgba32(0, 0, 255), new Rgba32(255, 255, 0), new Rgba32(1, 1, 1) };
      var pixels = new Rgba32[size * size];
      for (int i = 0; i < pixels.Length; i++)
        pixels[i] = new Rgba32(0, 0, 0, 255);
      void Disc(int cx, int cy, int radius, Rgba32 colour)
      {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(size - 1, cy + radius); y++)
          for (int x = Math.Max(0, cx - radius); x <= Math.Min(size - 1, cx + radius); x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
              pixels[y * size + x] = colour;
      }
      for (int k = 0; k < Math.Max(1, size / 12); k++)
        Disc(random.Next(size), random.Next(size), random.Next(1, Math.Max(2, size / 14)), palette[random.Next(palette.Length)]);
      // Pins across the tile borders at 127 / 128, and one in the corner.
      foreach (int edge in new[] { 128, 256, 384 })
        if (edge < size)
          Disc(edge - 1, edge, 6, palette[1]);
      Disc(0, 0, 3, palette[2]);
      Disc(size - 1, size - 1, 3, palette[3]);
      if (size >= 20)
        for (int y = 10; y < 14; y++)
          for (int x = 10; x < 14; x++)
            pixels[y * size + x] = new Rgba32(10, 10, 10, 0);
      var legend = string.Join("\n", palette.Select((p, i) => $"Pin{i}: {p.R},{p.G},{p.B}").Append("Clear: 10,10,10").Append("Backdrop: 0,0,0,255"));
      var path = WriteMap("locationmap.png", Png(size, (x, y) => pixels[(size - 1 - y) * size + x], SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha, SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8), legend.Replace("\n", "|"));
      var map = ImageMapLocation.Create(path, exactPins: true);
      var old = OldPins(pixels, size);
      var names = map.LegendColors;
      bool same = true;
      string why = "";
      foreach (var (key, pins) in old)
      {
        var name = names.Where(kv => kv.Value.r == key.Item1 && kv.Value.g == key.Item2 && kv.Value.b == key.Item3).Select(kv => kv.Key).Reverse().FirstOrDefault();
        if (name == null)
          continue;
        var got = map.GetAllSpawns(name).ToList();
        // Colours that share a legend name are dealt out among them; here each colour has a name of its own (the last wins).
        if (names.Count(kv => kv.Value.r == key.Item1 && kv.Value.g == key.Item2 && kv.Value.b == key.Item3) == 1 && !pins.SequenceEqual(got))
        {
          same = false;
          why = $"{name}: {pins.Count} pins against {got.Count}";
        }
      }
      C(same && map.RemainingAreas.Count > 0, $"{size} px: every pin of every colour, in the order they were found, as the whole array found them" + (same ? "" : $" ({why})"));
    }
  }

  private static void PackageTests()
  {
    Section("a settings package without ZPackage's copies, refused with a message past its limit");
    var pkg = new ZPackage();
    var random = new System.Random(8);
    var data = new byte[100_000];
    random.NextBytes(data);
    pkg.Write(12);
    pkg.Write(data);
    pkg.Write("a string");
    var buffer = PackageBytes.Buffer(pkg, out int length);
    C(length == pkg.GetArray().Length && buffer.Length >= length && buffer.Take(length).SequenceEqual(pkg.GetArray()), "the package's own buffer holds the same bytes GetArray copies");
    C(PackageBytes.Hash(pkg).SequenceEqual(pkg.GenerateHash()) && PackageBytes.Hash(buffer, length).SequenceEqual(pkg.GenerateHash()),
      "its SHA-512 is GenerateHash's");
    C(BC.ZNetPatch.WorldCache.PackageID(pkg) == BC.ZNetPatch.WorldCache.PackageID(pkg.GetArray(), length)
      && BC.ZNetPatch.WorldCache.PackageID(pkg).Length == 32, "and so is the cache's id, from the package or from its bytes");
    using (var stream = new MemoryStream(pkg.GetArray()))
    {
      var back = PackageBytes.Read(stream, length);
      C(back.ReadInt() == 12 && back.ReadByteArray().SequenceEqual(data) && back.ReadString() == "a string", "a package read from a stream reads back as it was written");
    }
    bool refused = false;
    try { PackageBytes.Read(new MemoryStream(new byte[10]), 11); } catch (EndOfStreamException) { refused = true; }
    bool negative = false;
    try { PackageBytes.Read(new MemoryStream(new byte[10]), -1); } catch (InvalidDataException) { negative = true; }
    C(refused && negative, "a package longer than its stream, or of a negative length, is refused");

    // The cache writes and reads the bytes it was given.
    var dir = Path.Combine(Work, "cache");
    Directory.CreateDirectory(dir);
    BC.ZNetPatch.WorldCache.WorldCachePath = dir;
    var id = BC.ZNetPatch.WorldCache.Add(buffer, length);
    var cached = BC.ZNetPatch.WorldCache.LoadCacheItem(id);
    C(id == BC.ZNetPatch.WorldCache.PackageID(pkg) && File.ReadAllBytes(BC.ZNetPatch.WorldCache.GetCachePath(id)).SequenceEqual(pkg.GetArray()) && cached.GetArray().SequenceEqual(pkg.GetArray()),
      "the cache writes exactly the package's bytes under its id, and reads them back");

    // The limit: a world whose settings pass it says which map it was writing, instead of the stream failing.
    var world = World(120, alpha: false, compact: false);
    int normal = Save(world, false, 12).Length;
    int limit = PackageBytes.Limit;
    try
    {
      PackageBytes.Limit = normal / 3;
      string message = null;
      try { Save(world, false, 12); } catch (WorldTooLargeException e) { message = e.Message; }
      C(message != null && message.Contains("come to") && message.Contains("Compact Maps") && message.Contains("map") && !message.Contains(" 0 MB"), $"settings past the limit are refused with a message ({message})");
      PackageBytes.Limit = normal + 10;
      C(Save(world, false, 12).Length == normal, "and settings just inside it are saved");
    }
    finally
    {
      PackageBytes.Limit = limit;
    }
    // The stream's room is the last package's size: a second save does not grow the stream by doubling.
    var again = new ZPackage();
    world.Serialize(again, false, true, 12);
    var room = PackageBytes.Buffer(again, out int again_length);
    C(room.Length <= normal + 8192 && again_length == normal, $"the second package starts with the room the first took ({room.Length:N0} for {normal:N0})");
    C(PackageBytes.MaxLength < int.MaxValue / 1000 * 800 && PackageBytes.MaxLength > 1_000_000_000, "the limit leaves room below the stream's own (2.1 GB) for the next map");
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using BetterContinents;

namespace ExportTest;

// PngRowWriter (the export's PNG writer: a few rows at a time, so a 16384 px map never has to be a whole image in memory):
// what it writes is a PNG every decoder reads, pixel for pixel, whatever way the rows are split; its chunks are well formed
// (lengths, CRCs, the zlib stream and its Adler-32); it picks every scanline filter where it pays; its files are about as
// small as ImageSharp's; an unfinished file is never left behind; and rows of 16384 px wide go through.
internal static class PngRowWriterTest
{
  static void C(bool ok, string what) => Program.C(ok, what);

  // A deterministic picture: smooth ramps and waves (so every filter has a place) plus a little noise.
  static byte[] Picture(int width, int height, int channels, int bits, int seed)
  {
    int bpp = channels * bits / 8;
    var rnd = new Random(seed);
    var bytes = new byte[width * height * bpp];
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++)
        for (int c = 0; c < channels; c++)
        {
          double v = c switch
          {
            0 => x * 0.37 + y * 0.11,
            1 => 40.0 + 30.0 * Math.Sin(x * 0.05) * Math.Cos(y * 0.07),
            2 => y * 0.5,
            _ => 255,
          };
          int noise = (x * 31 + y * 17 + c) % 11 == 0 ? rnd.Next(-3, 4) : 0;
          if (bits == 16)
          {
            int s = (int)Math.Clamp(v * 200.0 + noise * 3, 0, 65535);
            int o = (y * width + x) * bpp + c * 2;
            bytes[o] = (byte)(s >> 8);
            bytes[o + 1] = (byte)s;
          }
          else
            bytes[(y * width + x) * bpp + c] = (byte)Math.Clamp((int)v + noise, 0, 255);
        }
    return bytes;
  }

  // Writes the picture with the rows cut into bands of the given sizes (cycled).
  static void Write(string path, byte[] picture, int width, int height, int channels, int bits, int[] bands, string? key = null, string? text = null)
  {
    using var writer = new PngRowWriter(path, width, height, channels, bits, key, text);
    int row = 0, k = 0;
    while (row < height)
    {
      int rows = Math.Min(bands[k++ % bands.Length], height - row);
      writer.WriteRows(picture, row * writer.RowBytes, rows);
      row += rows;
    }
    writer.Complete();
  }

  // The chunks of a PNG file, checking each CRC: (type, data).
  internal static List<(string Type, byte[] Data)> Chunks(byte[] file, out bool crcOk)
  {
    crcOk = file.Length > 8 && file.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    var list = new List<(string, byte[])>();
    int at = 8;
    while (at + 12 <= file.Length)
    {
      int length = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
      var type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
      if (at + 12 + length > file.Length) { crcOk = false; break; }
      var data = file.AsSpan(at + 8, length).ToArray();
      uint crc = PngRowWriter.Crc32(0, file, at + 4, 4 + length);
      uint stored = ((uint)file[at + 8 + length] << 24) | ((uint)file[at + 9 + length] << 16) | ((uint)file[at + 10 + length] << 8) | file[at + 11 + length];
      if (crc != stored) crcOk = false;
      list.Add((type, data));
      at += 12 + length;
    }
    if (at != file.Length) crcOk = false;
    return list;
  }

  public static void Run(string work)
  {
    System.Console.WriteLine("== the streaming PNG writer");
    var filtersUsed = new HashSet<int>();
    long mine = 0, theirs = 0;
    foreach (var (width, height) in new[] { (1, 1), (3, 2), (37, 53), (257, 129), (600, 40) })
      foreach (var (channels, bits) in new[] { (1, 8), (1, 16), (3, 8), (4, 8) })
      {
        var picture = Picture(width, height, channels, bits, width * 7 + height);
        var path = Path.Combine(work, $"stream-{width}x{height}-{channels}x{bits}.png");
        Write(path, picture, width, height, channels, bits, [1, 5, 7, 64]);
        var file = File.ReadAllBytes(path);
        var chunks = Chunks(file, out var crcOk);
        bool wellFormed = crcOk && chunks[0].Type == "IHDR" && chunks[^1].Type == "IEND" && chunks.Count(c => c.Type == "IDAT") >= 1
          && chunks.Where(c => c.Type != "IDAT").Select(c => c.Type).SequenceEqual(["IHDR", "IEND"]);
        // The zlib stream: inflate (which checks its own Adler-32) and read the filter bytes.
        var idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        int rowBytes = width * channels * bits / 8;
        byte[] inflated;
        try
        {
          using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
          using var all = new MemoryStream();
          z.CopyTo(all);
          inflated = all.ToArray();
        }
        catch (Exception e)
        {
          inflated = [];
          System.Console.WriteLine("    zlib: " + e.Message);
        }
        bool sized = inflated.Length == (rowBytes + 1) * height;
        if (sized)
          for (int y = 0; y < height; y++)
            filtersUsed.Add(inflated[y * (rowBytes + 1)]);
        var header = chunks[0].Data;
        int w = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3], h = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
        bool headerOk = w == width && h == height && header[8] == bits && header[9] == (channels == 1 ? 0 : channels == 3 ? 2 : 6) && header[10] == 0 && header[11] == 0 && header[12] == 0;
        // Decoded by ImageSharp, pixel for pixel.
        bool same;
        switch (channels, bits)
        {
          case (1, 8):
            using (var image = Image.Load<L8>(path))
              same = image.Width == width && image.Height == height && Enumerable.Range(0, width * height).All(i => image[i % width, i / width].PackedValue == picture[i]);
            break;
          case (1, 16):
            using (var image = Image.Load<L16>(path))
              same = image.Width == width && image.Height == height && Enumerable.Range(0, width * height).All(i => image[i % width, i / width].PackedValue == ((picture[2 * i] << 8) | picture[2 * i + 1]));
            break;
          case (3, 8):
            using (var image = Image.Load<Rgb24>(path))
              same = image.Width == width && image.Height == height && Enumerable.Range(0, width * height).All(i =>
              {
                var p = image[i % width, i / width];
                return p.R == picture[3 * i] && p.G == picture[3 * i + 1] && p.B == picture[3 * i + 2];
              });
            break;
          default:
            using (var image = Image.Load<Rgba32>(path))
              same = image.Width == width && image.Height == height && Enumerable.Range(0, width * height).All(i =>
              {
                var p = image[i % width, i / width];
                return p.R == picture[4 * i] && p.G == picture[4 * i + 1] && p.B == picture[4 * i + 2] && p.A == picture[4 * i + 3];
              });
            break;
        }
        C(wellFormed && sized && headerOk && same, $"{width} x {height}, {channels} channel(s) of {bits} bits, in bands of 1, 5, 7 and 64 rows: well formed, {file.Length} bytes, every pixel decodes");
        if (width >= 257)
        {
          // ImageSharp's own encoding of the same picture, for the size.
          var reference = Path.Combine(work, "reference.png");
          var encoder = new PngEncoder { ColorType = channels == 1 ? PngColorType.Grayscale : channels == 3 ? PngColorType.Rgb : PngColorType.RgbWithAlpha, BitDepth = bits == 16 ? PngBitDepth.Bit16 : PngBitDepth.Bit8, ChunkFilter = PngChunkFilter.ExcludeAll };
          switch (channels, bits)
          {
            case (1, 8): using (var i = Image.Load<L8>(path)) i.SaveAsPng(reference, encoder); break;
            case (1, 16): using (var i = Image.Load<L16>(path)) i.SaveAsPng(reference, encoder); break;
            case (3, 8): using (var i = Image.Load<Rgb24>(path)) i.SaveAsPng(reference, encoder); break;
            default: using (var i = Image.Load<Rgba32>(path)) i.SaveAsPng(reference, encoder); break;
          }
          mine += file.Length;
          theirs += new FileInfo(reference).Length;
        }
      }
    C(filtersUsed.SetEquals([0, 1, 2, 3, 4]), $"every scanline filter is chosen somewhere ({string.Join(", ", filtersUsed.OrderBy(f => f))})");
    C(mine <= theirs * 1.2, $"the files are about as small as ImageSharp's ({mine} bytes against {theirs})");

    // The split of the rows makes no difference to the file.
    {
      var picture = Picture(300, 90, 3, 8, 5);
      Write(Path.Combine(work, "split-a.png"), picture, 300, 90, 3, 8, [90]);
      Write(Path.Combine(work, "split-b.png"), picture, 300, 90, 3, 8, [1]);
      Write(Path.Combine(work, "split-c.png"), picture, 300, 90, 3, 8, [13, 2]);
      var a = File.ReadAllBytes(Path.Combine(work, "split-a.png"));
      C(a.SequenceEqual(File.ReadAllBytes(Path.Combine(work, "split-b.png"))) && a.SequenceEqual(File.ReadAllBytes(Path.Combine(work, "split-c.png"))),
        "however the rows are split, the file is the same, byte for byte");
    }

    // The text chunk: the heightmap's record, before the pixels, read back as HeightmapRecord does.
    {
      var picture = Picture(40, 40, 1, 16, 9);
      var path = Path.Combine(work, "record.png");
      var record = new HeightmapRecord(81f, 0.5f);
      Write(path, picture, 40, 40, 1, 16, [10], HeightmapRecord.Keyword, record.Text);
      var chunks = Chunks(File.ReadAllBytes(path), out var ok);
      using var image = Image.Load<L16>(path);
      var back = HeightmapRecord.From(image.Metadata.GetPngMetadata().TextData);
      C(ok && chunks.Select(c => c.Type).First(t => t != "IHDR") == "tEXt" && chunks.Count(c => c.Type == "tEXt") == 1, "the text chunk is one tEXt chunk before the pixels");
      C(back != null && back.Amount == 81f && back.SeaLevel == 0.5f, $"it reads back as the record of Heightmap Amount 81 ({back?.Text})");
      C(chunks.All(c => c.Type is "IHDR" or "tEXt" or "IDAT" or "IEND"), "no other chunk is written (no gAMA, pHYs, sRGB)");
    }

    // A text chunk after the pixels (the fine heights file's record, whose CRC-32 is only known once the heightmap is finished):
    // between the last IDAT and IEND, beside a text chunk before the pixels, read by ImageSharp; and a file completed without
    // one is the file it always was.
    {
      var picture = Picture(61, 33, 1, 8, 11);
      var plain = Path.Combine(work, "after-plain.png");
      var both = Path.Combine(work, "after-both.png");
      var viaNull = Path.Combine(work, "after-null.png");
      const string text = "Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 0A1B2C3D";
      Write(plain, picture, 61, 33, 1, 8, [10]);
      using (var writer = new PngRowWriter(viaNull, 61, 33, 1, 8))
      {
        writer.WriteRows(picture, 0, 33);
        writer.Complete(null, null);
      }
      using (var writer = new PngRowWriter(both, 61, 33, 1, 8, HeightmapRecord.Keyword, new HeightmapRecord(81f, 0.5f).Text))
      {
        writer.WriteRows(picture, 0, 17);
        writer.WriteRows(picture, 17 * writer.RowBytes, 16);
        writer.Complete(HeightmapRecord.Keyword, text);
        writer.Complete(HeightmapRecord.Keyword, "ignored: it is complete");
      }
      var chunks = Chunks(File.ReadAllBytes(both), out var ok);
      var types = chunks.Select(c => c.Type).ToList();
      int lastIdat = types.LastIndexOf("IDAT");
      C(ok && types[0] == "IHDR" && types[1] == "tEXt" && types[2] == "IDAT" && types[lastIdat + 1] == "tEXt" && types[lastIdat + 2] == "IEND" && types.Count == lastIdat + 3 && types.Count(t => t == "tEXt") == 2,
        $"a text chunk after the pixels sits between the last IDAT and IEND, beside the one before them ({string.Join(" ", types.Distinct())})");
      string Text(byte[] data) { int z = Array.IndexOf(data, (byte)0); return System.Text.Encoding.Latin1.GetString(data, z + 1, data.Length - z - 1); }
      C(Text(chunks[lastIdat + 1].Data) == text && System.Text.Encoding.Latin1.GetString(chunks[lastIdat + 1].Data, 0, HeightmapRecord.Keyword.Length) == HeightmapRecord.Keyword, "its keyword and text are what was given");
      using (var image = Image.Load<L8>(both))
      {
        var meta = image.Metadata.GetPngMetadata().TextData.ToList();
        C(meta.Count == 2 && meta.Any(t => t.Keyword == HeightmapRecord.Keyword && t.Value == text) && HeightmapRecord.From(meta) is { Amount: 81f },
          "ImageSharp reads both chunks, and the heightmap's own record is still found");
        C(Enumerable.Range(0, 61 * 33).All(i => image[i % 61, i / 61].PackedValue == picture[i]), "the pixels are untouched");
      }
      C(File.ReadAllBytes(plain).SequenceEqual(File.ReadAllBytes(viaNull)), "Complete(null, null) writes the file Complete() always did, byte for byte");
      uint Bitwise(byte[] bytes)
      {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
          crc ^= b;
          for (int k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
      }
      var all = File.ReadAllBytes(both);
      C(PngRowWriter.FileCrc32(both) == Bitwise(all) && PngRowWriter.FileCrc32(plain) == Bitwise(File.ReadAllBytes(plain)), "FileCrc32 is the CRC-32 of the file's bytes (checked against a bitwise one)");
      var wide = new byte[3 * (1 << 20) + 12345];
      new Random(5).NextBytes(wide);
      File.WriteAllBytes(Path.Combine(work, "crc-wide.bin"), wide);
      C(PngRowWriter.FileCrc32(Path.Combine(work, "crc-wide.bin")) == Bitwise(wide), "and over a file of several megabytes read in blocks");
    }

    // An unfinished file is not left behind, and an incomplete image cannot be completed.
    {
      var path = Path.Combine(work, "unfinished.png");
      var picture = Picture(50, 50, 1, 8, 3);
      using (var writer = new PngRowWriter(path, 50, 50, 1, 8))
      {
        writer.WriteRows(picture, 0, 20);
        C(File.Exists(path + ".tmp") && !File.Exists(path), "while it is written the file is <name>.tmp");
        bool threw = false;
        try { writer.Complete(); } catch (InvalidOperationException) { threw = true; }
        C(threw, "an image of 20 rows out of 50 cannot be completed");
      }
      C(!File.Exists(path) && !File.Exists(path + ".tmp"), "closing it unfinished deletes the file");
      File.WriteAllText(path, "an older map");
      using (var writer = new PngRowWriter(path, 50, 50, 1, 8))
      {
        writer.WriteRows(picture, 0, 50);
        writer.Complete();
      }
      C(!File.Exists(path + ".tmp") && new FileInfo(path).Length > 20 && Image.Load<L8>(path).Width == 50, "completing it replaces a file of that name");
    }

    // The widest rows: 16384 px of every kind (an int row of 16384 x 4 bytes), a few rows, and the checksums over big buffers.
    foreach (var (channels, bits) in new[] { (1, 8), (1, 16), (3, 8), (4, 8) })
    {
      const int w = 16384, h = 6;
      var picture = Picture(w, h, channels, bits, 1);
      var path = Path.Combine(work, $"wide-{channels}x{bits}.png");
      Write(path, picture, w, h, channels, bits, [4]);
      using var image = Image.Load(path);
      var chunks = Chunks(File.ReadAllBytes(path), out var ok);
      C(ok && image.Width == w && image.Height == h && chunks.Count(c => c.Type == "IDAT") >= 1, $"{w} px wide, {channels} channel(s) of {bits} bits: well formed, {image.Width} x {image.Height}");
    }
    C(PngRowWriter.Adler32(1, System.Text.Encoding.ASCII.GetBytes("Wikipedia"), 0, 9) == 0x11E60398u && PngRowWriter.Crc32(0, System.Text.Encoding.ASCII.GetBytes("123456789"), 0, 9) == 0xCBF43926u,
      "Adler-32 and CRC-32 give their published check values (Wikipedia, 123456789)");
    {
      // 20000 bytes of 255: the Adler sums would overflow 32 bits without the 5552-byte steps.
      var big = Enumerable.Repeat((byte)255, 20000).ToArray();
      uint a = 1, b = 0;
      foreach (var v in big) { a = (a + v) % 65521; b = (b + a) % 65521; }
      C(PngRowWriter.Adler32(1, big, 0, big.Length) == ((b << 16) | a), "Adler-32 over long runs of 255 matches the plain definition");
    }
  }
}

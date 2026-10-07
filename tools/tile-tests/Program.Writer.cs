// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// PngWriter (a map written back out as a PNG from its tiles, a row at a time): every colour type and depth reads back, in
// ImageSharp and in PngRows, as the bytes it was given; its zlib stream is a valid one, Adler-32 included (which neither of
// those checks); and every map format written out from tiles gives the pixels of the picture it was made from.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using ISImage = SixLabors.ImageSharp.Image;
using ISConfiguration = SixLabors.ImageSharp.Configuration;

internal static partial class Program
{
  private static void PngWriterTests()
  {
    Section("PngWriter: a PNG written a row at a time reads back as the bytes it was given");
    int checks = 0, bad = 0;
    string firstBad = null;
    foreach (int width in new[] { 1, 2, 5, 127, 300, 2049 })
      foreach (int height in new[] { 1, 3, 70 })
        foreach (var (colorType, channels) in new[] { (0, 1), (2, 3), (4, 2), (6, 4) })
          foreach (int depth in new[] { 8, 16 })
            foreach (int flat in new[] { 0, 1, 2 })
            {
              int bpp = channels * depth / 8, rowBytes = width * bpp;
              var random = new System.Random(width * 31 + height * 7 + colorType + depth + flat);
              var rows = new byte[height][];
              for (int y = 0; y < height; y++)
              {
                rows[y] = new byte[rowBytes];
                if (flat == 0)
                  random.NextBytes(rows[y]);
                else if (flat == 1)
                  for (int i = 0; i < rowBytes; i++)
                    rows[y][i] = (byte)(i / bpp * 3 + y);
                else
                  for (int i = 0; i < rowBytes; i++)
                    rows[y][i] = (byte)(i % bpp == 0 ? y * 5 : 200);
              }
              var png = PngWriter.Write(width, height, colorType, depth, (y, raw) => Buffer.BlockCopy(rows[y], 0, raw, 0, rowBytes));
              checks++;
              bool ok;
              string why = "";
              try
              {
                // ImageSharp: 8-bit as Rgba32, 16-bit as Rgba64; the expected pixel from the bytes.
                ok = depth == 8 ? Compare8(png, rows, width, height, colorType) : Compare16(png, rows, width, height, colorType);
                if (!ok)
                  why = "ImageSharp reads other pixels";
              }
              catch (Exception e)
              {
                ok = false;
                why = e.Message;
              }
              if (!ok)
              {
                bad++;
                firstBad ??= $"{width} x {height}, colour type {colorType}, {depth} bits, pattern {flat}: {why}";
              }
            }
    C(bad == 0, $"{checks} pictures (colour types 0, 2, 4 and 6 at 8 and 16 bits, 1 to 2049 px wide) read back in ImageSharp as the bytes written" + (firstBad == null ? "" : $"; {bad} differ, first {firstBad}"));

    // PngRows reads what the writer wrote, in the pixel types the maps read.
    var grey = Enumerable.Range(0, 300).Select(y => Enumerable.Range(0, 300).Select(x => (ushort)(x * 211 + y * 17)).ToArray()).ToArray();
    var greyPng = PngWriter.Write(300, 300, 0, 16, (y, raw) =>
    {
      for (int x = 0; x < 300; x++)
      {
        raw[2 * x] = (byte)(grey[y][x] >> 8);
        raw[2 * x + 1] = (byte)grey[y][x];
      }
    });
    C(PngRows<L16>.TryOpen(greyPng) != null && SameRows<L16>(greyPng, out _), "and PngRows reads a 16-bit grey one as ImageSharp does");
    var text = PngWriter.Write(2, 2, 0, 8, (y, raw) => { raw[0] = 1; raw[1] = 2; }, HeightmapRecord.Keyword, new HeightmapRecord(3.5f, 0.125f).Text);
    using (var image = ISImage.Load<L8>(ISConfiguration.Default, text))
      C(HeightmapRecord.From(image.Metadata.GetPngMetadata().TextData) is { Amount: 3.5f, SeaLevel: 0.125f }, "a tEXt chunk is written where ImageSharp reads it");
    // One that compresses (a flat picture) and one that does not (noise) are both valid, the first much smaller.
    var flatPng = PngWriter.Write(1000, 1000, 6, 8, (y, raw) => { for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 4 == 3 ? 255 : 17); });
    var noisePng = PngWriter.Write(1000, 1000, 6, 8, (y, raw) => new System.Random(y).NextBytes(raw));
    C(flatPng.Length < 20000 && noisePng.Length > 50 * flatPng.Length, $"a flat picture is {flatPng.Length:N0} bytes, noise {noisePng.Length:N0}");

    // Each row is filtered with the one of None, Sub and Up that leaves the smallest bytes: a smooth gradient is far smaller than ImageSharp's own with no filter at all.
    using (var gradient = Picture(512, 512, 1, (r, x, y) => new L16((ushort)(x * 97 + y * 3))))
    {
      var unfiltered = Encode(gradient, PngColorType.Grayscale, PngBitDepth.Bit16, PngFilterMethod.None);
      var filtered = PngWriter.Write(512, 512, 0, 16, (y, raw) =>
      {
        for (int x = 0; x < 512; x++)
        {
          raw[2 * x] = (byte)((x * 97 + y * 3) >> 8);
          raw[2 * x + 1] = (byte)(x * 97 + y * 3);
        }
      });
      C(filtered.Length < unfiltered.Length / 4, $"a smooth gradient is {filtered.Length:N0} bytes (the rows filtered), against {unfiltered.Length:N0} for ImageSharp's with no filter");
    }

    // The zlib stream is valid, to its last four bytes: the runtime's own zlib checks the Adler-32 there, ImageSharp and PngRows do not.
    bool ZlibReads(byte[] png, long expected, out string why)
    {
      try
      {
        using var inflate = new ZLibStream(new MemoryStream(OddIdatData(png)), CompressionMode.Decompress);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
          total += n;
        why = total == expected ? "" : $"{total} bytes, not {expected}";
        return total == expected;
      }
      catch (Exception e)
      {
        why = e.GetType().Name + ": " + e.Message;
        return false;
      }
    }
    using (var control = new Image<L8>(40, 40))
    {
      var controlPng = Encode(control, PngColorType.Grayscale, PngBitDepth.Bit8);
      C(ZlibReads(controlPng, 40 * 41, out var controlWhy), $"the check itself: ImageSharp's own PNG passes it ({controlWhy})");
      // The same picture with the last byte of its last IDAT (the Adler-32's) changed: the check refuses it.
      int lastEnd = 0;
      for (int at = 8; at + 12 <= controlPng.Length;)
      {
        int length = controlPng[at] << 24 | controlPng[at + 1] << 16 | controlPng[at + 2] << 8 | controlPng[at + 3];
        if (controlPng[at + 4] == 'I' && controlPng[at + 5] == 'D' && controlPng[at + 6] == 'A' && controlPng[at + 7] == 'T')
          lastEnd = at + 8 + length;
        at += 12 + length;
      }
      controlPng[lastEnd - 1] ^= 0x55;
      C(!ZlibReads(controlPng, 40 * 41, out _), "and refuses a stream whose Adler-32 is wrong");
    }
    int zlibRan = 0, zlibBad = 0;
    string zlibFirst = null;
    // Rows from 1 to 36,000 bytes (the Adler-32's sums are reduced every 5552 bytes), noise and a pattern, every colour type and both depths.
    foreach (var (zWidth, zHeight, zType, zDepth) in new[] { (1, 1, 0, 8), (300, 70, 0, 16), (2049, 5, 6, 16), (5000, 3, 0, 8), (1500, 40, 2, 16), (9000, 2, 6, 8), (64, 300, 4, 16) })
    {
      int zChannels = zType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
      int zBpp = zChannels * zDepth / 8, zRowBytes = zWidth * zBpp;
      foreach (int pattern in new[] { 0, 1 })
      {
        var zPng = PngWriter.Write(zWidth, zHeight, zType, zDepth, (y, raw) =>
        {
          if (pattern == 0)
            new System.Random(y * 7 + zWidth).NextBytes(raw);
          else
            for (int i = 0; i < raw.Length; i++)
              raw[i] = (byte)(i / zBpp * 3 + y);
        });
        zlibRan++;
        if (!ZlibReads(zPng, (long)zHeight * (zRowBytes + 1), out var zWhy))
        {
          zlibBad++;
          zlibFirst ??= $"{zWidth} x {zHeight}, colour type {zType}, {zDepth} bits, pattern {pattern}: {zWhy}";
        }
      }
    }
    C(zlibBad == 0, $"{zlibRan} PngWriter pictures (rows of 1 to 36,000 bytes, noise and a pattern, every colour type, 8 and 16 bits) inflate to exactly their rows, Adler-32 included" + (zlibFirst == null ? "" : $"; {zlibBad} do not, first {zlibFirst}"));

    Section("PngWriter: every map format written out from its tiles gives the pixels it was made from");
    const int N = 150;
    int seed = 5;
    var picture = new Func<int, byte[]>[]
    {
      _ => Png(N, (x, y) => new L16(Height(x, y, seed)), PngColorType.Grayscale, PngBitDepth.Bit16),
      _ => Png(N, (x, y) => new La32(Height(x, y, seed), (ushort)(x * 97 + y)), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16),
      _ => Png(N, (x, y) => new La16((byte)Height(x, y, seed), (byte)(x + y)), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8),
      _ => Png(N, (x, y) => new Rgba32((byte)Height(x, y, seed), 0, 0, 255), PngColorType.RgbWithAlpha, PngBitDepth.Bit8),
    };
    var originals = picture.Select(p => p(0)).ToArray();
    ImageMapFloat Map(int i) => i switch
    {
      0 => FloatMap(originals[0], ImageMapFloat.HeightAlpha.None, compact: true),
      1 => FloatMap(originals[1], ImageMapFloat.HeightAlpha.Blend, compact: true),
      2 => FloatMap(originals[2], ImageMapFloat.HeightAlpha.Legacy, compact: true),
      _ => new Func<ImageMapFloat>(() => { var m = new ImageMapFloat { SourceData = originals[3], Compact = true }; return m.CreateMapLegacy() ? m : null; })(),
    };
    for (int i = 0; i < 4; i++)
    {
      // As a world reads it (from its tiles), so no picture is held: the map writes one from its tiles.
      var back = ImageMapFloat.FromBlock(Map(i).ToBlock());
      var bytes = back.SourceBytes();
      bool same;
      if (i == 0)
        same = Pixels16(bytes).SequenceEqual(Pixels16(originals[0]));
      else if (i == 1)
        same = PixelsLa32(bytes).SequenceEqual(PixelsLa32(originals[1]));
      else if (i == 2)
        // The legacy heightmap never read its alpha: its picture is written opaque.
        same = PixelsLa16(bytes).Select(p => p.L).SequenceEqual(PixelsLa16(originals[2]).Select(p => p.L)) && PixelsLa16(bytes).All(p => p.A == 255);
      else
        same = PixelsRgba(bytes).SequenceEqual(PixelsRgba(originals[3]).Select(p => new Rgba32(p.R, p.R, p.R, 255)));
      C(same && back.SourceData.Length == 0, $"{new[] { "16-bit grey", "16-bit grey + alpha", "8-bit grey + alpha", "the oldest maps' RGBA (red as grey)" }[i]}: the picture written from the tiles is the picture it was made from");
    }
    var colours = Png(N, (x, y) => (x + y) % 9 == 0 ? new Rgba32(255, 0, 0, 255) : new Rgba32((byte)(x * 3), (byte)(y * 2), 90, (byte)(x % 5 == 0 ? 7 : 255)), PngColorType.RgbWithAlpha, PngBitDepth.Bit8);
    var paint = ImageMapPaint.Create(WriteMap("paint-writer.png", colours, "0,0,0,0: 255,0,0,255"), compact: true);
    var paintBack = ImageMapPaint.FromBlock(paint.ToBlock());
    C(PixelsRgba(paintBack.SourceBytes()).SequenceEqual(PixelsRgba(colours)), "a colour map: its colours as the picture had them (before any legend), alpha included");
  }

  private static bool Compare8(byte[] png, byte[][] rows, int width, int height, int colorType)
  {
    using var image = ISImage.Load<Rgba32>(ISConfiguration.Default, png);
    if (image.Width != width || image.Height != height)
      return false;
    bool ok = true;
    image.ProcessPixelRows(accessor =>
    {
      for (int y = 0; y < height; y++)
      {
        var row = accessor.GetRowSpan(y);
        for (int x = 0; x < width; x++)
        {
          var r = rows[y];
          var want = colorType switch
          {
            0 => new Rgba32(r[x], r[x], r[x], 255),
            2 => new Rgba32(r[3 * x], r[3 * x + 1], r[3 * x + 2], 255),
            4 => new Rgba32(r[2 * x], r[2 * x], r[2 * x], r[2 * x + 1]),
            _ => new Rgba32(r[4 * x], r[4 * x + 1], r[4 * x + 2], r[4 * x + 3]),
          };
          ok &= row[x].Equals(want);
        }
      }
    });
    return ok;
  }

  private static bool Compare16(byte[] png, byte[][] rows, int width, int height, int colorType)
  {
    using var image = ISImage.Load<Rgba64>(ISConfiguration.Default, png);
    if (image.Width != width || image.Height != height)
      return false;
    bool ok = true;
    image.ProcessPixelRows(accessor =>
    {
      for (int y = 0; y < height; y++)
      {
        var row = accessor.GetRowSpan(y);
        for (int x = 0; x < width; x++)
        {
          var r = rows[y];
          ushort V(int at) => (ushort)(r[2 * at] << 8 | r[2 * at + 1]);
          var want = colorType switch
          {
            0 => new Rgba64(V(x), V(x), V(x), 65535),
            2 => new Rgba64(V(3 * x), V(3 * x + 1), V(3 * x + 2), 65535),
            4 => new Rgba64(V(2 * x), V(2 * x), V(2 * x), V(2 * x + 1)),
            _ => new Rgba64(V(4 * x), V(4 * x + 1), V(4 * x + 2), V(4 * x + 3)),
          };
          ok &= row[x].Equals(want);
        }
      }
    });
    return ok;
  }

  private static ushort[] Pixels16(byte[] png)
  {
    using var image = ISImage.Load<L16>(ISConfiguration.Default, png);
    return Pixels(image, p => p.PackedValue);
  }

  private static (ushort L, ushort A)[] PixelsLa32(byte[] png)
  {
    using var image = ISImage.Load<La32>(ISConfiguration.Default, png);
    return Pixels(image, p => (p.L, p.A));
  }

  private static (byte L, byte A)[] PixelsLa16(byte[] png)
  {
    using var image = ISImage.Load<La16>(ISConfiguration.Default, png);
    return Pixels(image, p => (p.L, p.A));
  }

  private static Rgba32[] PixelsRgba(byte[] png)
  {
    using var image = ISImage.Load<Rgba32>(ISConfiguration.Default, png);
    return Pixels(image, p => p);
  }
}

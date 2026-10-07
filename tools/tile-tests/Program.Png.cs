// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// PngRows (a PNG read a row at a time, never whole in memory) against ImageSharp, pixel for pixel: every colour type and
// bit depth ImageSharp's encoder writes, every filter, sizes that are no multiple of a tile, a band at a time from the top
// of the map down, again from the start, a palette with transparency; and that every picture it does not read exactly as
// ImageSharp would (interlaced, colour keys, 16-bit colour, damaged, other formats) is declined, so ImageSharp reads it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UnityEngine;
using ISImage = SixLabors.ImageSharp.Image;
using ISConfiguration = SixLabors.ImageSharp.Configuration;

internal static partial class Program
{
  private static byte[] Encode<T>(Image<T> image, PngColorType type, PngBitDepth depth, PngFilterMethod filter = PngFilterMethod.Adaptive, bool chunks = false) where T : unmanaged, IPixel<T>
  {
    using var stream = new MemoryStream();
    image.Save(stream, new PngEncoder { ColorType = type, BitDepth = depth, FilterMethod = filter, ChunkFilter = chunks ? PngChunkFilter.None : PngChunkFilter.ExcludeAll });
    return stream.ToArray();
  }

  // An image of random-looking but compressible pixels (runs, gradients, noise), so every filter has rows it suits.
  private static Image<T> Picture<T>(int width, int height, int seed, Func<System.Random, int, int, T> pixel) where T : unmanaged, IPixel<T>
  {
    var random = new System.Random(seed);
    var image = new Image<T>(width, height);
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++)
        image[x, y] = pixel(random, x, y);
    return image;
  }

  private static int Bits(System.Random r, int x, int y, int mix) => mix switch
  {
    0 => r.Next(256),
    1 => (x * 3 + y * 5) & 255,
    2 => x / 7 % 2 == 0 ? 17 : 200,
    _ => (x + y) % 11 == 0 ? r.Next(256) : (x / 3 + y / 2) & 255,
  };

  // Every row of a picture as ImageSharp holds it (the oracle), against PngRows' bands taken from the top of the map down, the
  // way the tiles ask for them, then again from the start.
  private static bool SameRows<T>(byte[] png, out string why) where T : unmanaged, IPixel<T>
  {
    why = "";
    using var image = ISImage.Load<T>(ISConfiguration.Default, png);
    using var oracle = new ImagePicture<T>(image);
    using var rows = PngRows<T>.TryOpen(png) ?? throw new InvalidOperationException("not read as rows");
    if (rows.Width != oracle.Width || rows.Height != oracle.Height)
    {
      why = "size";
      return false;
    }
    int size = oracle.Height;
    for (int pass = 0; pass < 2; pass++)
      for (int top = size; top > 0;)
      {
        int count = Math.Min(top, pass == 0 ? 128 : 37), y0 = top - count;
        var want = new T[count][];
        oracle.ReadRows(y0, count, (r, row) => want[r] = row.ToArray());
        bool ok = true;
        int seen = 0;
        rows.ReadRows(y0, count, (r, row) =>
        {
          seen++;
          ok &= row.Length == want[r].Length && row.SequenceEqual(want[r]);
        });
        if (!ok || seen != count)
        {
          why = $"pass {pass}, rows {y0}..{y0 + count - 1}";
          return false;
        }
        top = y0;
      }
    return true;
  }

  private static void PngRowsTests()
  {
    Section("PngRows: a PNG read a row at a time gives ImageSharp's pixels, every kind, every filter");
    var filters = new[] { PngFilterMethod.None, PngFilterMethod.Sub, PngFilterMethod.Up, PngFilterMethod.Average, PngFilterMethod.Paeth, PngFilterMethod.Adaptive };
    int checks = 0, bad = 0;
    string firstBad = null;
    void Check<T>(string name, byte[] png) where T : unmanaged, IPixel<T>
    {
      checks++;
      bool ok;
      string why;
      try { ok = SameRows<T>(png, out why); }
      catch (Exception e) { ok = false; why = e.Message; }
      if (!ok)
      {
        bad++;
        firstBad ??= $"{name} as {typeof(T).Name}: {why}";
      }
    }
    foreach (int size in new[] { 1, 2, 3, 7, 31, 127, 128, 129, 300 })
      foreach (var filter in filters)
        for (int mix = 0; mix < 4; mix++)
        {
          int seed = size * 13 + mix;
          using (var grey16 = Picture(size, size, seed, (r, x, y) => new L16((ushort)(Bits(r, x, y, mix) * 257 + (mix == 0 ? r.Next(257) : 0)))))
          {
            var png = Encode(grey16, PngColorType.Grayscale, PngBitDepth.Bit16, filter);
            Check<L16>($"{size} px 16-bit grey {filter} mix {mix}", png);
            Check<La32>($"{size} px 16-bit grey {filter} mix {mix}", png);
          }
          using (var grey8 = Picture(size, size, seed, (r, x, y) => new L8((byte)Bits(r, x, y, mix))))
          {
            var png = Encode(grey8, PngColorType.Grayscale, PngBitDepth.Bit8, filter);
            Check<L16>($"{size} px 8-bit grey {filter} mix {mix}", png);
            Check<La32>($"{size} px 8-bit grey {filter} mix {mix}", png);
            Check<La16>($"{size} px 8-bit grey {filter} mix {mix}", png);
            Check<Rgba32>($"{size} px 8-bit grey {filter} mix {mix}", png);
          }
          using (var greyAlpha16 = Picture(size, size, seed, (r, x, y) => new La32((ushort)(Bits(r, x, y, mix) * 257), (ushort)(Bits(r, y, x, 3 - mix) * 257 + 3))))
            Check<La32>($"{size} px 16-bit grey+alpha {filter} mix {mix}", Encode(greyAlpha16, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16, filter));
          using (var greyAlpha8 = Picture(size, size, seed, (r, x, y) => new La16((byte)Bits(r, x, y, mix), (byte)Bits(r, y, x, 3 - mix))))
          {
            var png = Encode(greyAlpha8, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8, filter);
            Check<La32>($"{size} px 8-bit grey+alpha {filter} mix {mix}", png);
            Check<La16>($"{size} px 8-bit grey+alpha {filter} mix {mix}", png);
            Check<Rgba32>($"{size} px 8-bit grey+alpha {filter} mix {mix}", png);
          }
          using (var rgb = Picture(size, size, seed, (r, x, y) => new Rgb24((byte)Bits(r, x, y, mix), (byte)Bits(r, y, x, mix), (byte)Bits(r, x + 1, y, (mix + 1) % 4))))
            Check<Rgba32>($"{size} px RGB {filter} mix {mix}", Encode(rgb, PngColorType.Rgb, PngBitDepth.Bit8, filter));
          using (var rgba = Picture(size, size, seed, (r, x, y) => new Rgba32((byte)Bits(r, x, y, mix), (byte)Bits(r, y, x, mix), (byte)Bits(r, x + 1, y, (mix + 1) % 4), (byte)(mix == 2 ? 255 : Bits(r, x, y + 1, 1)))))
            Check<Rgba32>($"{size} px RGBA {filter} mix {mix}", Encode(rgba, PngColorType.RgbWithAlpha, PngBitDepth.Bit8, filter));
        }
    C(bad == 0, $"{checks} pictures (every kind the maps read, as every pixel type they are read as, all filters, 1 to 300 px) give the same rows as ImageSharp" + (firstBad == null ? "" : $"; {bad} differ, first {firstBad}"));

    // Palettes, with and without transparency, at every bit depth ImageSharp writes.
    checks = bad = 0;
    firstBad = null;
    foreach (int size in new[] { 1, 5, 17, 130 })
      foreach (var (depth, colours) in new[] { (PngBitDepth.Bit1, 2), (PngBitDepth.Bit2, 4), (PngBitDepth.Bit4, 16), (PngBitDepth.Bit8, 200) })
        foreach (bool alpha in new[] { false, true })
          foreach (var filter in new[] { PngFilterMethod.Sub, PngFilterMethod.Paeth, PngFilterMethod.Adaptive })
          {
            var palette = Enumerable.Range(0, colours).Select(i => new Rgba32((byte)(i * 37 + 11), (byte)(i * 91 + 5), (byte)(i * 13 + 200), alpha ? (byte)(i % 3 == 0 ? 255 : i * 5 + 1) : (byte)255)).ToArray();
            using var image = Picture(size, size, size * colours, (r, x, y) => palette[(x / 2 + y * 3 + (r.Next(5) == 0 ? r.Next(colours) : 0)) % colours]);
            using var stream = new MemoryStream();
            image.Save(stream, new PngEncoder
            {
              ColorType = PngColorType.Palette, BitDepth = depth, FilterMethod = filter, ChunkFilter = PngChunkFilter.ExcludeAll,
              Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.PaletteQuantizer(palette.Select(c => (SixLabors.ImageSharp.Color)c).ToArray(), new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions { Dither = null }),
            });
            checks++;
            bool ok;
            string why;
            try { ok = SameRows<Rgba32>(stream.ToArray(), out why); }
            catch (Exception e) { ok = false; why = e.Message; }
            if (!ok)
            {
              bad++;
              firstBad ??= $"{size} px {depth} alpha {alpha} {filter}: {why}";
            }
          }
    C(bad == 0, $"{checks} palette pictures (1, 2, 4 and 8 bits, with and without a tRNS chunk) give the same Rgba32 rows as ImageSharp" + (firstBad == null ? "" : $"; {bad} differ, first {firstBad}"));

    // What is declined: ImageSharp reads those, as before.
    using var plain = Picture(40, 40, 1, (r, x, y) => new L16((ushort)(x * 1000 + y)));
    var greyPng = Encode(plain, PngColorType.Grayscale, PngBitDepth.Bit16);
    C(PngRows<L16>.TryOpen(greyPng) != null && PngRows<L16>.TryOpen(greyPng[..^20]) == null && PngRows<L16>.TryOpen(greyPng[..40]) == null && PngRows<L16>.TryOpen([]) == null,
      "a whole PNG is read; one with no end, or cut short, or empty, is declined");
    using var rgba16 = Picture(40, 40, 1, (r, x, y) => new Rgba64((ushort)(x * 1000), (ushort)(y * 1000), 7, 65535));
    using var rgb16 = Picture(40, 40, 1, (r, x, y) => new Rgb48((ushort)(x * 1000), (ushort)(y * 1000), 7));
    using var grey4 = Picture(40, 40, 1, (r, x, y) => new L8((byte)((x + y) % 16 * 17)));
    using var grey1 = Picture(40, 40, 1, (r, x, y) => new L8((byte)(x % 2 * 255)));
    C(PngRows<Rgba32>.TryOpen(Encode(rgba16, PngColorType.RgbWithAlpha, PngBitDepth.Bit16)) == null
      && PngRows<Rgba32>.TryOpen(Encode(rgb16, PngColorType.Rgb, PngBitDepth.Bit16)) == null
      && PngRows<L16>.TryOpen(Encode(grey4, PngColorType.Grayscale, PngBitDepth.Bit4)) == null
      && PngRows<L16>.TryOpen(Encode(grey1, PngColorType.Grayscale, PngBitDepth.Bit1)) == null,
      "16-bit colour and 1, 2 and 4-bit grey (whose scaling is ImageSharp's) are declined");
    using var rgb8 = Picture(40, 40, 1, (r, x, y) => new Rgb24((byte)x, (byte)y, 7));
    using var smallGreyAlpha = Picture(40, 40, 1, (r, x, y) => new La16((byte)x, (byte)y));
    C(PngRows<L16>.TryOpen(Encode(rgb8, PngColorType.Rgb, PngBitDepth.Bit8)) == null
      && PngRows<La32>.TryOpen(Encode(rgb8, PngColorType.Rgb, PngBitDepth.Bit8)) == null
      && PngRows<L16>.TryOpen(Encode(smallGreyAlpha, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8)) == null
      && PngRows<Rgba32>.TryOpen(greyPng) == null,
      "a pixel type a picture is not read as (colour as grey, 16-bit grey as Rgba32) is declined");
    var interlaced = (byte[])greyPng.Clone();
    interlaced[16 + 12] = 1;
    var crc = Crc32.Compute(interlaced, 12, 17);
    interlaced[29] = (byte)(crc >> 24); interlaced[30] = (byte)(crc >> 16); interlaced[31] = (byte)(crc >> 8); interlaced[32] = (byte)crc;
    C(PngRows<L16>.TryOpen(interlaced) == null, "an interlaced (Adam7) picture is declined");
    var badHeader = (byte[])greyPng.Clone();
    badHeader[17] ^= 1;
    C(PngRows<L16>.TryOpen(badHeader) == null, "a header whose checksum is wrong is declined");
    var bmp = new MemoryStream();
    plain.SaveAsBmp(bmp);
    var jpeg = new MemoryStream();
    plain.SaveAsJpeg(jpeg);
    C(PngRows<L16>.TryOpen(bmp.ToArray()) == null && PngRows<L16>.TryOpen(jpeg.ToArray()) == null, "a BMP and a JPEG are declined");
    // Text: tEXt is read; compressed or international text is ImageSharp's.
    using var withText = Picture(40, 40, 1, (r, x, y) => new L16((ushort)(x * 1000 + y)));
    withText.Metadata.GetPngMetadata().TextData.Add(new PngTextData(HeightmapRecord.Keyword, new HeightmapRecord(2.5f, 0.25f).Text, "", ""));
    var textPng = Encode(withText, PngColorType.Grayscale, PngBitDepth.Bit16, chunks: true);
    using (var read = PngRows<L16>.TryOpen(textPng))
    {
      C(read != null && HeightmapRecord.From(read.Text) is { Amount: 2.5f, SeaLevel: 0.25f }, "a heightmap's record in a tEXt chunk is read");
    }
    using var international = Picture(40, 40, 1, (r, x, y) => new L16((ushort)(x * 1000 + y)));
    international.Metadata.GetPngMetadata().TextData.Add(new PngTextData("Title", "größe", "de", "Titel"));
    C(PngRows<L16>.TryOpen(Encode(international, PngColorType.Grayscale, PngBitDepth.Bit16, chunks: true)) == null, "international text (iTXt) is declined");
    using var gamma = Picture(40, 40, 1, (r, x, y) => new L16((ushort)(x * 1000 + y)));
    gamma.Metadata.GetPngMetadata().Gamma = 2.2f;
    var gammaPng = Encode(gamma, PngColorType.Grayscale, PngBitDepth.Bit16, chunks: true);
    C(PngRows<L16>.TryOpen(gammaPng) != null && SameRows<L16>(gammaPng, out _), "a gAMA chunk changes nothing, as in ImageSharp");

    // Every chunk's checksum is checked when the picture is opened: a byte changed in the compressed data is a picture ImageSharp
    // reads (and says what it makes of).
    var flippedData = (byte[])greyPng.Clone();
    flippedData[flippedData.Length / 2] ^= 0x04;
    C(PngRows<L16>.TryOpen(flippedData) == null && PngRows<L16>.TryOpen(greyPng) != null, "a byte changed in the compressed data: declined by the chunk's checksum");

    // The CRC-32 eight bytes at a time is the one a byte at a time gives, at every length and offset.
    var crcRandom = new System.Random(77);
    var crcData = new byte[5000];
    crcRandom.NextBytes(crcData);
    uint Plain(byte[] data, int offset, int count)
    {
      uint crc = 0xFFFFFFFFu;
      for (int i = offset; i < offset + count; i++)
      {
        crc ^= data[i];
        for (int k = 0; k < 8; k++)
          crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
      }
      return ~crc;
    }
    int crcWrong = 0;
    for (int offset = 0; offset < 9; offset++)
      for (int count = 0; count < 70; count++)
        if (Crc32.Compute(crcData, offset, count) != Plain(crcData, offset, count))
          crcWrong++;
    for (int count = 4000; count < 4100; count++)
      if (Crc32.Compute(crcData, 3, count) != Plain(crcData, 3, count))
        crcWrong++;
    C(crcWrong == 0 && Crc32.Compute([(byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9'], 0, 9) == 0xCBF43926u,
      "the CRC-32 eight bytes at a time is the plain one at every offset and length (and 0xCBF43926 for \"123456789\")");

    // A damaged stream is found while reading, and reported as such (the map then goes to ImageSharp).
    using (var noisy = Picture(300, 300, 3, (r, x, y) => new L16((ushort)r.Next(65536))))
    {
      var png = Encode(noisy, PngColorType.Grayscale, PngBitDepth.Bit16);
      // Damage the middle of the compressed data (a deflate stream that no longer holds its rows) and seal each IDAT chunk's
      // checksum again, as a file that was damaged when it was written would have it.
      var cut = (byte[])png.Clone();
      for (int i = png.Length / 2; i < png.Length / 2 + 400; i++)
        cut[i] = 0xFF;
      for (int at = 8; at + 12 <= cut.Length;)
      {
        int length = cut[at] << 24 | cut[at + 1] << 16 | cut[at + 2] << 8 | cut[at + 3];
        if (cut[at + 4] == (byte)'I' && cut[at + 5] == (byte)'D' && cut[at + 6] == (byte)'A' && cut[at + 7] == (byte)'T')
        {
          var sealedCrc = Crc32.Compute(cut, at + 4, length + 4);
          cut[at + 8 + length] = (byte)(sealedCrc >> 24); cut[at + 9 + length] = (byte)(sealedCrc >> 16); cut[at + 10 + length] = (byte)(sealedCrc >> 8); cut[at + 11 + length] = (byte)sealedCrc;
        }
        at += 12 + length;
      }
      bool reported = false;
      using (var rows = PngRows<L16>.TryOpen(cut))
      {
        try
        {
          for (int top = 300; top > 0; top -= 100)
            rows?.ReadRows(top - 100, 100, (r, row) => { });
        }
        catch (PngRowsException)
        {
          reported = true;
        }
      }
      C(reported, "a damaged compressed stream raises PngRowsException (ImageMapBase then reads the picture whole)");
    }

    // Whole maps made from the picture, both ways: a picture that ImageSharp reads whole (here: 16-bit RGBA) and one read as
    // rows give maps that sample alike (the oracle tests above cover the rest).
    Section("PngRows: the map kinds made from rows, and from a picture ImageSharp reads whole");
    var palettePng = new MemoryStream();
    {
      var colours = new[] { new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(255, 255, 255, 255) };
      using var indexed = Picture(150, 150, 2, (r, x, y) => colours[(x / 13 + y / 7 + (r.Next(9) == 0 ? 1 : 0)) % 4]);
      indexed.Save(palettePng, new PngEncoder { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit2, ChunkFilter = PngChunkFilter.ExcludeAll, Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.PaletteQuantizer(colours.Select(c => (SixLabors.ImageSharp.Color)c).ToArray(), new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions { Dither = null }) });
    }
    var legend = "Meadows: FF0000|Ocean: 00FF00|Mountain: 0000FF|Plains: FFFFFF";
    var paletteMap = ImageMapBiome.Create(palettePng.ToArray(), legend, "palette.png");
    using (var image = ISImage.Load<Rgba32>(ISConfiguration.Default, palettePng.ToArray()))
    {
      image.Mutate(x => x.Flip(FlipMode.Vertical));
      var candidates = paletteMap.LegendColors.Select(kv => (Biome: kv.Key, Color: kv.Value)).ToList();
      var old = Pixels(image, p => candidates.OrderBy(d => Distance(new Color32(p.R, p.G, p.B, p.A), d.Color)).First().Biome);
      C(paletteMap.Biomes.SequenceEqual(old), "a palette picture read as a biome map gives the biomes the whole picture did");
    }
  }
}

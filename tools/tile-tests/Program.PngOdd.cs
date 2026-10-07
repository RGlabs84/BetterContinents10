// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Pictures as no encoder writes them, and how a map is made from a picture. PngRows reads a PNG only where its pixels are exactly the ones
// ImageSharp gives, so each of these is ImageSharp's reading (or declined to it): a palette index past the PLTE's last colour, image data
// another chunk interrupts, a colour key, a damaged checksum. And that a map's picture comes by rows where PngRows can read it and whole
// where it cannot, that one whose stream ends early is read whole from the start (the fallback), and in what order the tiles ask for rows.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using ISImage = SixLabors.ImageSharp.Image;
using ISConfiguration = SixLabors.ImageSharp.Configuration;

internal static partial class Program
{
  // ---- PNG files made by hand ---------------------------------------------------------------------------------------

  private static void OddBe(byte[] to, int at, uint value)
  {
    to[at] = (byte)(value >> 24);
    to[at + 1] = (byte)(value >> 16);
    to[at + 2] = (byte)(value >> 8);
    to[at + 3] = (byte)value;
  }

  // A chunk: its length, type, data and CRC-32 (spoiled when asked).
  private static byte[] OddChunk(string type, byte[] data, bool badCrc = false)
  {
    var chunk = new byte[12 + data.Length];
    OddBe(chunk, 0, (uint)data.Length);
    for (int i = 0; i < 4; i++)
      chunk[4 + i] = (byte)type[i];
    Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
    uint crc = Crc32.Compute(chunk, 4, 4 + data.Length);
    OddBe(chunk, 8 + data.Length, badCrc ? ~crc : crc);
    return chunk;
  }

  private static byte[] OddFile(IEnumerable<byte[]> chunks) => [137, 80, 78, 71, 13, 10, 26, 10, .. chunks.SelectMany(c => c)];

  private static byte[] OddHeader(int width, int height, int depth, int colorType)
  {
    var ihdr = new byte[13];
    OddBe(ihdr, 0, (uint)width);
    OddBe(ihdr, 4, (uint)height);
    ihdr[8] = (byte)depth;
    ihdr[9] = (byte)colorType;
    return OddChunk("IHDR", ihdr);
  }

  // The image data of rows (unfiltered bytes): each row's filter byte (0 to 4 in turn, so every filter is used) and the row filtered.
  private static byte[] OddScanlines(byte[][] rows, int bpp)
  {
    var data = new List<byte>();
    var above = new byte[rows[0].Length];
    for (int y = 0; y < rows.Length; y++)
    {
      var row = rows[y];
      int filter = y % 5;
      data.Add((byte)filter);
      for (int i = 0; i < row.Length; i++)
      {
        int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        int predicted = filter switch { 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => 0 };
        data.Add((byte)(row[i] - predicted));
      }
      above = row;
    }
    return [.. data];
  }

  private static byte[] OddZlib(byte[] raw)
  {
    using var memory = new MemoryStream();
    using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true))
      zlib.Write(raw, 0, raw.Length);
    return memory.ToArray();
  }

  // The zlib stream in IDAT chunks of at most `size` bytes.
  private static List<byte[]> OddIdats(byte[] zlib, int size)
  {
    var chunks = new List<byte[]>();
    for (int at = 0; at < zlib.Length; at += size)
      chunks.Add(OddChunk("IDAT", zlib[at..Math.Min(zlib.Length, at + size)]));
    return chunks;
  }

  private static byte[][] OddRandomRows(int height, int rowBytes, System.Random random)
  {
    var rows = new byte[height][];
    for (int y = 0; y < height; y++)
    {
      rows[y] = new byte[rowBytes];
      random.NextBytes(rows[y]);
    }
    return rows;
  }

  // A palette picture: the colours of plte (three bytes each), the alphas of trns (null for no tRNS chunk), pixel (x, y) the colour index(x, y)
  // packed into depth bits, the leftmost in the highest bits of its byte.
  private static byte[] OddPalettePng(int width, int height, int depth, byte[] plte, byte[] trns, Func<int, int, int> index)
  {
    int rowBytes = (width * depth + 7) / 8, perByte = 8 / depth;
    var rows = new byte[height][];
    for (int y = 0; y < height; y++)
    {
      rows[y] = new byte[rowBytes];
      for (int x = 0; x < width; x++)
        rows[y][x / perByte] |= (byte)(index(x, y) << (8 - depth * (x % perByte + 1)));
    }
    var chunks = new List<byte[]> { OddHeader(width, height, depth, 3), OddChunk("PLTE", plte) };
    if (trns != null)
      chunks.Add(OddChunk("tRNS", trns));
    chunks.AddRange(OddIdats(OddZlib(OddScanlines(rows, 1)), 4096));
    chunks.Add(OddChunk("IEND", []));
    return OddFile(chunks);
  }

  // Where the first chunk of this type starts in a PNG (at its length); -1 for none.
  private static int OddFind(byte[] png, string type)
  {
    for (int at = 8; at + 12 <= png.Length;)
    {
      if (png[at + 4] == type[0] && png[at + 5] == type[1] && png[at + 6] == type[2] && png[at + 7] == type[3])
        return at;
      at += 12 + (png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3]);
    }
    return -1;
  }

  // The data of every IDAT chunk, one after the other: the zlib stream.
  private static byte[] OddIdatData(byte[] png)
  {
    using var data = new MemoryStream();
    for (int at = 8; at + 12 <= png.Length;)
    {
      int length = png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
      if (png[at + 4] == 'I' && png[at + 5] == 'D' && png[at + 6] == 'A' && png[at + 7] == 'T')
        data.Write(png, at + 8, length);
      at += 12 + length;
    }
    return data.ToArray();
  }

  // The chunk put in after the header (the first 33 bytes).
  private static byte[] OddInsertAfterHeader(byte[] png, byte[] chunk) => [.. png[..33], .. chunk, .. png[33..]];

  // ---- a palette index past the PLTE ----------------------------------------------------------------------------------

  private static void PngRowsPaletteIndices()
  {
    Section("PngRows: a palette index past the PLTE's last colour is that colour, as in ImageSharp");
    var random = new System.Random(2026);
    int checks = 0, bad = 0;
    string firstBad = null;
    foreach (int depth in new[] { 1, 2, 4, 8 })
      foreach (int colours in new[] { 1, (1 << depth) / 3 + 1, (1 << depth) - 1 }.Distinct())
        foreach (int alphas in new[] { 0, 1, 1000 })
        {
          var plte = new byte[3 * colours];
          random.NextBytes(plte);
          // No tRNS chunk, one entry of it, or one for every colour.
          byte[] trns = alphas == 0 ? null : Enumerable.Range(0, Math.Min(alphas, colours)).Select(i => (byte)(i * 53 + 7)).ToArray();
          // The first row is all the highest index the depth has, past the PLTE's end for any palette short of it.
          var png = OddPalettePng(37, 23, depth, plte, trns, (x, y) => y == 0 ? (1 << depth) - 1 : random.Next(1 << depth));
          checks++;
          bool ok;
          string why;
          try { ok = SameRows<Rgba32>(png, out why); }
          catch (Exception e) { ok = false; why = e.Message; }
          if (!ok)
          {
            bad++;
            firstBad ??= $"{depth}-bit, {colours} colours, tRNS of {trns?.Length ?? 0}: {why}";
          }
        }
    C(bad == 0, $"{checks} palette pictures whose indices run past the PLTE (1, 2, 4 and 8 bits, with no tRNS, one entry of it, or all) give the rows ImageSharp gives" + (firstBad == null ? "" : $"; {bad} differ, first {firstBad}"));

    // What that is, written out: a palette of two colours, an 8-bit row of every index 0 to 255.
    Rgba32[] Row(byte[] trns)
    {
      var png = OddPalettePng(256, 1, 8, [10, 20, 30, 200, 100, 50], trns, (x, y) => x);
      var got = new Rgba32[256];
      using var rows = PngRows<Rgba32>.TryOpen(png);
      rows?.ReadRows(0, 1, (r, row) => row.CopyTo(got));
      return rows == null ? null : got;
    }
    var opaque = Row(null);
    C(opaque != null && opaque[0] == new Rgba32(10, 20, 30, 255) && opaque.Skip(1).All(p => p == new Rgba32(200, 100, 50, 255)),
      "two colours: index 0 is the first, every index from 1 to 255 the second");
    var clear = Row([7, 99]);
    C(clear != null && clear[0] == new Rgba32(10, 20, 30, 7) && clear.Skip(1).All(p => p == new Rgba32(200, 100, 50, 99)),
      "and with a tRNS: each index its entry's alpha, the indices past the end the last entry's");
    var short1 = Row([7]);
    C(short1 != null && short1[0] == new Rgba32(10, 20, 30, 7) && short1.Skip(1).All(p => p == new Rgba32(200, 100, 50, 255)),
      "and with a tRNS of fewer entries than colours: the colours past it are opaque");
  }

  // ---- image data another chunk interrupts ------------------------------------------------------------------------------

  private static void PngRowsInterruptedData()
  {
    Section("PngRows: a picture whose image data another chunk interrupts is left to ImageSharp, as before");
    var random = new System.Random(77);
    (string Name, int Depth, int ColorType, int Channels)[] kinds = [("16-bit grey", 16, 0, 1), ("8-bit grey", 8, 0, 1), ("16-bit grey + alpha", 16, 4, 2), ("RGB", 8, 2, 3), ("RGBA", 8, 6, 4)];
    const int N = 64;
    // Between the two halves of the image data: text, a chunk this does not know (ancillary), and two the PNG spec lists.
    var between = new (string Name, byte[] Chunk)[]
    {
      ("a tEXt", OddChunk("tEXt", Encoding.Latin1.GetBytes("Comment\0hello"))),
      ("an unknown ancillary chunk", OddChunk("abCD", [1, 2, 3])),
      ("a gAMA", OddChunk("gAMA", [0, 1, 0x86, 0xA0])),
      ("a tIME", OddChunk("tIME", [7, 234, 10, 6, 12, 0, 0])),
      ("an empty ancillary chunk", OddChunk("zzZz", [])),
    };
    int declined = 0, expected = 0, controls = 0, controlsOk = 0;
    string firstOpened = null, firstControl = null;
    foreach (var (name, depth, colorType, channels) in kinds)
    {
      var rows = OddRandomRows(N, (N * channels * depth + 7) / 8, random);
      var zlib = OddZlib(OddScanlines(rows, Math.Max(1, channels * depth / 8)));
      var head = OddHeader(N, N, depth, colorType);
      var end = OddChunk("IEND", []);
      var first = OddChunk("IDAT", zlib[..(zlib.Length / 2)]);
      var second = OddChunk("IDAT", zlib[(zlib.Length / 2)..]);
      byte[] With(params byte[][] chunks) => OddFile([head, first, .. chunks, second, end]);
      bool Opens(byte[] png) => (colorType, depth) switch
      {
        (0, _) => PngRows<L16>.TryOpen(png) != null,
        (4, _) => PngRows<La32>.TryOpen(png) != null,
        _ => PngRows<Rgba32>.TryOpen(png) != null,
      };
      bool Reads(byte[] png) => (colorType, depth) switch
      {
        (0, _) => SameRows<L16>(png, out _),
        (4, _) => SameRows<La32>(png, out _),
        _ => SameRows<Rgba32>(png, out _),
      };
      foreach (var (what, chunk) in between)
      {
        expected++;
        if (!Opens(With(chunk)))
          declined++;
        else
          firstOpened ??= $"{name} with {what} between";
      }
      // The same two chunks of image data side by side, or with an empty one of them between (still one run): read as ImageSharp reads them.
      controls += 2;
      if (Opens(With()) && Reads(With()))
        controlsOk++;
      else
        firstControl ??= $"{name} in two chunks";
      if (Opens(With(OddChunk("IDAT", []))) && Reads(With(OddChunk("IDAT", []))))
        controlsOk++;
      else
        firstControl ??= $"{name} with an empty IDAT between";
    }
    C(declined == expected, $"{declined} of {expected} pictures (every kind the maps read, with each of five chunks between the two parts of the image data) are declined" + (firstOpened == null ? "" : $"; opened: {firstOpened}"));
    C(controlsOk == controls, $"{controlsOk} of {controls} of the same pictures with the image data in one run (two chunks, or three with an empty one) are read, as ImageSharp reads them" + (firstControl == null ? "" : $"; not read: {firstControl}"));

    // As a map: ImageSharp's picture (what it makes of the interrupted data: the rows it had not reached left as they were, or a refusal).
    var grey = OddRandomRows(N, 2 * N, random);
    var greyZlib = OddZlib(OddScanlines(grey, 2));
    var greyPng = OddFile([OddHeader(N, N, 16, 0), OddChunk("IDAT", greyZlib[..(greyZlib.Length / 2)]), OddChunk("tEXt", Encoding.Latin1.GetBytes("Comment\0hello")),
      OddChunk("IDAT", greyZlib[(greyZlib.Length / 2)..]), OddChunk("IEND", [])]);
    OldFloat old = null;
    try { old = OldFloat.From<L16>(greyPng); }
    catch (Exception) { }
    var path = WriteMap("interrupted.png", greyPng);
    foreach (bool compact in new[] { false, true })
    {
      var map = ImageMapFloat.Create(path, ImageMapFloat.HeightAlpha.None, compact);
      bool same = (map != null) == (old != null) && (old == null || Points(N, 5).All(p => Same(map.GetValue(p.x, p.y), old.Value(p.x, p.y))));
      C(same, $"{Mode(compact)}: the map made of it is the map ImageSharp alone makes ({(old == null ? "ImageSharp refuses the picture, and so is no map made" : "every sample, bit for bit")})");
    }
  }

  // ---- a colour key -------------------------------------------------------------------------------------------------------

  private static void PngRowsColourKey()
  {
    Section("PngRows: a colour key (a tRNS chunk in a grey or RGB picture) is declined: ImageSharp makes those pixels transparent");
    using var grey8 = Picture(40, 40, 1, (r, x, y) => new L8((byte)((x + y) % 8 * 32)));
    using var rgb8 = Picture(40, 40, 1, (r, x, y) => new Rgb24((byte)((x + y) % 8 * 32), 7, 9));
    using var grey16 = Picture(40, 40, 1, (r, x, y) => new L16((ushort)((x + y) % 8 * 8192)));
    var plainGrey8 = Encode(grey8, PngColorType.Grayscale, PngBitDepth.Bit8);
    var plainRgb8 = Encode(rgb8, PngColorType.Rgb, PngBitDepth.Bit8);
    var plainGrey16 = Encode(grey16, PngColorType.Grayscale, PngBitDepth.Bit16);
    var greyKey = OddInsertAfterHeader(plainGrey8, OddChunk("tRNS", [0, 64]));
    var rgbKey = OddInsertAfterHeader(plainRgb8, OddChunk("tRNS", [0, 64, 0, 7, 0, 9]));
    var grey16Key = OddInsertAfterHeader(plainGrey16, OddChunk("tRNS", [0x40, 0x00]));
    // The key matters: ImageSharp's pixels of the same picture are transparent where the key says, and opaque without it.
    int Transparent(byte[] png)
    {
      using var image = ISImage.Load<Rgba32>(ISConfiguration.Default, png);
      int count = 0;
      image.ProcessPixelRows(accessor =>
      {
        for (int y = 0; y < accessor.Height; y++)
          foreach (var p in accessor.GetRowSpan(y))
            if (p.A == 0)
              count++;
      });
      return count;
    }
    C(Transparent(greyKey) == 200 && Transparent(plainGrey8) == 0, $"ImageSharp makes the keyed pixels transparent ({Transparent(greyKey)} of 1600, with none in the same picture without the key)");
    int taken = 0;
    string first = null;
    void Declined<T>(string name, byte[] png) where T : unmanaged, IPixel<T>
    {
      using var rows = PngRows<T>.TryOpen(png);
      if (rows != null)
      {
        taken++;
        first ??= $"{name} as {typeof(T).Name}";
      }
    }
    foreach (var (name, png) in new[] { ("8-bit grey", greyKey), ("RGB", rgbKey), ("16-bit grey", grey16Key) })
    {
      Declined<L16>(name, png);
      Declined<La32>(name, png);
      Declined<La16>(name, png);
      Declined<Rgba32>(name, png);
    }
    C(taken == 0, "an 8-bit grey, an RGB and a 16-bit grey picture with a colour key are declined, as every pixel type the maps read" + (first == null ? "" : $" (read: {first})"));
    C(PngRows<Rgba32>.TryOpen(plainGrey8) != null && PngRows<Rgba32>.TryOpen(plainRgb8) != null && PngRows<L16>.TryOpen(plainGrey16) != null,
      "and the same three pictures without the key are read");
  }

  // ---- every chunk's checksum -----------------------------------------------------------------------------------------------

  private static void PngRowsChunkChecksums()
  {
    Section("PngRows: the checksum of every chunk it reads is checked (a damaged picture is ImageSharp's)");
    var png = OddPalettePng(20, 10, 4, [10, 20, 30, 200, 100, 50, 0, 255, 0, 5, 5, 5, 99, 98, 97], [10, 20, 30], (x, y) => (x + y) % 5);
    png = OddInsertAfterHeader(png, OddChunk("tEXt", Encoding.Latin1.GetBytes("Comment\0hello")));
    C(PngRows<Rgba32>.TryOpen(png) is { Text.Count: 1 } && SameRows<Rgba32>(png, out _), "a palette picture with a tRNS and a tEXt chunk is read");
    foreach (var type in new[] { "IHDR", "PLTE", "tRNS", "tEXt", "IDAT" })
    {
      var damaged = (byte[])png.Clone();
      int at = OddFind(png, type);
      // One byte of the chunk's data (its first, or the zlib stream's second byte for the image data), checksum left as it was.
      damaged[at + 8 + (type == "IDAT" ? 5 : 0)] ^= 0x01;
      using var rows = PngRows<Rgba32>.TryOpen(damaged);
      C(at > 0 && rows == null, $"a byte changed in its {type} chunk: declined by the chunk's checksum");
    }
  }

  // ---- how a map's picture comes ---------------------------------------------------------------------------------------------

  // A map kind that only watches how its picture arrives: by rows (PngRows) or whole (ImageSharp's).
  private sealed class PictureProbe : ImageMapBase
  {
    public readonly List<string> Pictures = [];
    public long Pixels;

    protected override bool LoadTextureToMap<T>(MapPicture<T> picture)
    {
      Pictures.Add(picture is PngRows<T> ? "rows" : "whole");
      // Every row, a band at a time from the top of the map down, as the tiles are made.
      for (int top = picture.Height; top > 0;)
      {
        int count = Math.Min(top, 128), y0 = top - count;
        picture.ReadRows(y0, count, (r, row) => Pixels += row.Length);
        top = y0;
      }
      return true;
    }

    public (bool Made, string Pictures, long Pixels) Make<T>(byte[] png) where T : unmanaged, IPixel<T>
    {
      SourceData = png;
      FilePath = "probe.png";
      Pictures.Clear();
      Pixels = 0;
      bool made = CreateMap<T>();
      return (made, string.Join(" then ", Pictures), Pixels);
    }
  }

  // A picture that says in which order its rows are asked for.
  private sealed class RecordingPicture(int size) : MapPicture<L16>
  {
    public readonly List<int> Starts = [];
    public override int Width => size;
    public override int Height => size;
    internal override IReadOnlyList<PngTextData> Text => [];

    internal override void ReadRows(int y0, int count, RowReader<L16> read)
    {
      Starts.Add(y0);
      var row = new L16[size];
      for (int r = 0; r < count; r++)
      {
        for (int x = 0; x < size; x++)
          row[x] = new L16((ushort)(x * 37 + (y0 + r) * 11));
        read(r, row);
      }
    }
  }

  private sealed class FloatProbe : ImageMapFloat
  {
    internal bool Load(MapPicture<L16> picture) => LoadTextureToMap(picture);
  }

  private static void MapsAreReadByRows()
  {
    Section("maps: a picture PngRows reads comes by rows, any other whole, and one whose stream ends early whole from the start");
    var random = new System.Random(4);
    byte[] Raw(int colorType, int depth, int channels) =>
      OddFile([OddHeader(300, 300, depth, colorType), .. OddIdats(OddZlib(OddScanlines(OddRandomRows(300, (300 * channels * depth + 7) / 8, random), Math.Max(1, channels * depth / 8))), 8192), OddChunk("IEND", [])]);
    var probe = new PictureProbe();
    const long Total = 300 * 300;
    bool Is((bool Made, string Pictures, long Pixels) made, string how) => made.Made && made.Pictures == how && made.Pixels == Total;
    (string Name, bool Ok)[] kinds =
    [
      ("a 16-bit grey picture as a heightmap (L16) comes by rows", Is(probe.Make<L16>(Raw(0, 16, 1)), "rows")),
      ("an 8-bit grey one as L16 comes by rows", Is(probe.Make<L16>(Raw(0, 8, 1)), "rows")),
      ("a 16-bit grey + alpha one as a blending heightmap (La32) comes by rows", Is(probe.Make<La32>(Raw(4, 16, 2)), "rows")),
      ("an 8-bit grey + alpha one as La16 comes by rows", Is(probe.Make<La16>(Raw(4, 8, 2)), "rows")),
      ("an RGBA one as a colour or biome map (Rgba32) comes by rows", Is(probe.Make<Rgba32>(Raw(6, 8, 4)), "rows")),
      ("an RGB one as Rgba32 comes by rows", Is(probe.Make<Rgba32>(Raw(2, 8, 3)), "rows")),
      ("a palette one as Rgba32 comes by rows", Is(probe.Make<Rgba32>(OddPalettePng(300, 300, 4, [1, 2, 3, 4, 5, 6, 7, 8, 9], null, (x, y) => (x + y) % 3)), "rows")),
      // What PngRows leaves to ImageSharp: 16-bit colour, a colour picture as a grey map, 4-bit grey.
      ("a 16-bit RGBA one as Rgba32 comes whole", Is(probe.Make<Rgba32>(Raw(6, 16, 4)), "whole")),
      ("an RGB one as a heightmap (L16) comes whole", Is(probe.Make<L16>(Raw(2, 8, 3)), "whole")),
      ("a 4-bit grey one as L16 comes whole", Is(probe.Make<L16>(Raw(0, 4, 1)), "whole")),
    ];
    foreach (var (name, ok) in kinds)
      C(ok, $"{name}, every one of its {Total:N0} pixels read");

    // A picture whose stream ends early (every chunk's checksum right): PngRows says so mid-read, and the picture is read whole, from the start.
    var full = Png(120, (x, y) => new L16((ushort)(Height(x, y, 2) ^ (x * 977 + y * 31))), PngColorType.Grayscale, PngBitDepth.Bit16);
    var zlib = OddIdatData(full);
    byte[] Cut(int keep) => OddFile([full[8..33], OddChunk("IDAT", zlib[..keep]), OddChunk("IEND", [])]);
    foreach (var (name, keep) in new[] { ("60%", zlib.Length * 6 / 10), ("10%", zlib.Length / 10), ("all but the Adler-32", zlib.Length - 4) })
    {
      var cut = Cut(keep);
      var made = new PictureProbe().Make<L16>(cut);
      OldFloat old = null;
      try { old = OldFloat.From<L16>(cut); }
      catch (Exception) { }
      var path = WriteMap("cut.png", cut);
      var results = new List<bool>();
      bool fellBack = false;
      foreach (bool compact in new[] { false, true })
      {
        lock (LogHandler.Lines) LogHandler.Lines.Clear();
        var map = ImageMapFloat.Create(path, ImageMapFloat.HeightAlpha.None, compact);
        lock (LogHandler.Lines) fellBack = LogHandler.Lines.Any(l => l.Contains("reading it whole instead"));
        results.Add((map != null) == (old != null) && (old == null || Points(120, 5).All(p => Same(map.GetValue(p.x, p.y), old.Value(p.x, p.y)))));
      }
      // Cut before the end of the deflate data, PngRows reads rows until the data runs out, and the picture is read whole from the start.
      bool streamEnds = keep < zlib.Length - 4;
      string pictures = streamEnds && old != null ? "rows then whole" : "rows";
      C(results.All(r => r) && made.Made == (old != null) && made.Pictures == pictures && fellBack == (streamEnds && old != null),
        $"the zlib stream cut to {name}: read as {made.Pictures}, and the map is {(old != null ? "the one ImageSharp alone makes (every sample the same, decoded and compact)" : "refused, as ImageSharp refuses the picture")}");
    }

    // A real map at a size where reading it whole is a cost the log names (4096 px: 32 MB of 16-bit grey): it is not read whole.
    var big = Png(4096, (x, y) => new L16((ushort)(x * 3 + (y >> 3))), PngColorType.Grayscale, PngBitDepth.Bit16);
    var bigPath = WriteMap("rows4096.png", big);
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var bigMap = ImageMapFloat.Create(bigPath, ImageMapFloat.HeightAlpha.None, compact: true);
    bool saidWhole;
    lock (LogHandler.Lines) saidWhole = LogHandler.Lines.Any(l => l.Contains("is a picture of a kind that is read whole"));
    C(bigMap != null && bigMap.Size == 4096 && !saidWhole, "a 4096 px 16-bit grey PNG is made into a heightmap by rows: the log does not say it was read whole");

    // The tiles ask for the rows from the top of the map down (the file's order, which PngRows streams: asked for from the south it
    // would start again at every band): compressed (Compact Maps) and decoded.
    foreach (bool compact in new[] { false, true })
    {
      var picture = new RecordingPicture(600);
      var map = new FloatProbe { Compact = compact };
      bool loaded = map.Load(picture);
      C(loaded && picture.Starts.SequenceEqual([512, 384, 256, 128, 0]), $"{Mode(compact)}: the bands are asked for from the north down ({string.Join(", ", picture.Starts)})");
    }
  }
}

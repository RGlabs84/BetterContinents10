// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace BetterContinents;

// A picture's rows as a map reads them: ReadRows(y0, count, read) gives read(r, pixels) for each of the map's rows y0 to
// y0 + count - 1, r counting from 0, in no particular order. Map row 0 is the south, the last row of the file.
internal delegate void RowReader<TPixel>(int row, Span<TPixel> pixels) where TPixel : unmanaged, IPixel<TPixel>;

internal abstract class MapPicture<TPixel> : IDisposable where TPixel : unmanaged, IPixel<TPixel>
{
  public abstract int Width { get; }
  public abstract int Height { get; }
  // The PNG's text chunks (a world export's heightmap record is one).
  internal abstract IReadOnlyList<PngTextData> Text { get; }
  internal abstract void ReadRows(int y0, int count, RowReader<TPixel> read);
  public virtual void Dispose()
  {
  }
}

// A picture decoded whole by ImageSharp: any format, any colour type, any order of reading. Holds the whole image.
internal sealed class ImagePicture<TPixel>(Image<TPixel> image) : MapPicture<TPixel> where TPixel : unmanaged, IPixel<TPixel>
{
  public override int Width => image.Width;
  public override int Height => image.Height;
  internal override IReadOnlyList<PngTextData> Text => [.. SixLabors.ImageSharp.MetadataExtensions.GetPngMetadata(image.Metadata).TextData];

  internal override void ReadRows(int y0, int count, RowReader<TPixel> read)
  {
    int height = image.Height;
    image.ProcessPixelRows(accessor =>
    {
      for (int r = 0; r < count; r++)
        read(r, accessor.GetRowSpan(height - 1 - (y0 + r)));
    });
  }
}

// A PNG's file decoding a damaged or surprising stream: the picture is read whole by ImageSharp instead, which makes of it
// what it always did.
internal sealed class PngRowsException(string message) : Exception(message)
{
}

// A PNG read a row at a time from the file's bytes, never whole in memory: what makes a 16384 px map's creation cost its
// tiles and a few rows, where ImageSharp holds the whole decoded image (1 GB for four 8-bit channels) beside them. Only
// the cases where the pixels it gives are exactly the ones ImageSharp gives (tests in tools/tile-tests compare every one,
// pixel for pixel) are read here; anything else (other formats, Adam7 interlacing, colour keys, 16-bit colour, bit depths
// below 8 but for a palette's, damaged chunks, ...) is TryOpen's null, and the picture goes to ImageSharp as before.
//
// The rows come from the stream in file order, the north first. A map's rows are the file's the other way round, so the
// tiles are asked for from the top of the map down (ReadRows); a request for earlier rows starts the stream again.
internal sealed class PngRows<TPixel> : MapPicture<TPixel> where TPixel : unmanaged, IPixel<TPixel>
{
  private delegate void RowConverter(byte[] raw, Span<TPixel> row);

  private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
  private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Trns = 0x74524E53, Idat = 0x49444154, Iend = 0x49454E44, TextChunk = 0x74455874;

  private readonly byte[] file;
  private readonly List<(int At, int Length)> idat = [];
  private readonly List<PngTextData> text = [];
  private readonly int width, height, rowBytes, bpp;
  private readonly RowConverter? convert;
  // A palette picture's colours, with the alpha of its tRNS chunk.
  private readonly Rgba32[]? palette;
  private readonly int paletteBits;

  private Stream? inflate;
  // The row just read, unfiltered (what the converters read; its first byte is the filter's), and the one being read.
  private byte[] last = [], next = [];
  private TPixel[] pixels = [];
  private int rowsRead;

  public override int Width => width;
  public override int Height => height;
  internal override IReadOnlyList<PngTextData> Text => text;

  private PngRows(byte[] file, int width, int height, int rowBytes, int bpp, RowConverter? convert, Rgba32[]? palette, int paletteBits)
  {
    this.file = file;
    this.width = width;
    this.height = height;
    this.rowBytes = rowBytes;
    this.bpp = bpp;
    this.convert = convert;
    this.palette = palette;
    this.paletteBits = paletteBits;
  }

  private static uint Be32(byte[] data, int at) => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);

  // The picture as rows, or null when it is not a PNG this reads exactly as ImageSharp would.
  internal static PngRows<TPixel>? TryOpen(byte[] file)
  {
    try
    {
      return Open(file);
    }
    catch (Exception e) when (e is PngRowsException or IndexOutOfRangeException or ArgumentException or OverflowException)
    {
      return null;
    }
  }

  private static PngRows<TPixel>? Open(byte[] file)
  {
    if (file.Length < 8 + 25 + 12 || !file.AsSpan(0, 8).SequenceEqual(Signature))
      return null;
    int width = 0, height = 0, depth = 0, colorType = -1, at = 8;
    bool header = false, end = false;
    byte[]? plte = null, trns = null;
    var idat = new List<(int At, int Length)>();
    var text = new List<PngTextData>();
    while (!end && at + 12 <= file.Length)
    {
      uint length = Be32(file, at);
      uint type = Be32(file, at + 4);
      long next = (long)at + 12 + length;
      if (next > file.Length)
        return null;
      int data = at + 8, size = (int)length;
      switch (type)
      {
        case Ihdr:
          if (header || size != 13 || at != 8 || !CrcMatches(file, at, size))
            return null;
          width = (int)Be32(file, data);
          height = (int)Be32(file, data + 4);
          depth = file[data + 8];
          colorType = file[data + 9];
          // Compression and filter method 0 are the only ones there are; interlacing (Adam7) is ImageSharp's.
          if (file[data + 10] != 0 || file[data + 11] != 0 || file[data + 12] != 0 || width < 1 || height < 1)
            return null;
          header = true;
          break;
        case Plte:
          if (!header || plte != null || idat.Count > 0 || size % 3 != 0 || size == 0 || size > 768 || !CrcMatches(file, at, size))
            return null;
          plte = file.AsSpan(data, size).ToArray();
          break;
        case Trns:
          if (!header || trns != null || idat.Count > 0 || colorType != 3 || !CrcMatches(file, at, size))
            return null;
          trns = file.AsSpan(data, size).ToArray();
          break;
        case Idat:
          // Checked here, as ImageSharp does, so a damaged file is found before a pixel of it is used (and left to ImageSharp).
          if (!header || !CrcMatches(file, at, size))
            return null;
          idat.Add((data, size));
          break;
        case Iend:
          end = true;
          break;
        case TextChunk:
          if (!CrcMatches(file, at, size))
            return null;
          AddText(text, file, data, size);
          break;
        default:
          // zTXt and iTXt are decoded by ImageSharp (compression, languages); a chunk this does not know and cannot do
          // without (the first letter capital) is for ImageSharp to refuse; any other ancillary chunk does not touch pixels.
          if (type is 0x7A545874 or 0x69545874 || (file[at + 4] & 0x20) == 0)
            return null;
          break;
      }
      at = (int)next;
    }
    if (!header || !end || idat.Count == 0 || (colorType == 3 && plte == null))
      return null;

    int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
    if (channels == 0)
      return null;
    long bits = (long)channels * depth;
    long rowBytes = (width * bits + 7) / 8;
    if (rowBytes + 1 > int.MaxValue / 2 || width > TileBlock.MaxSize * 4)
      return null;
    int bpp = (int)Math.Max(1, bits / 8);

    Rgba32[]? colours = null;
    int paletteBits = 0;
    RowConverter? convert;
    if (colorType == 3)
    {
      if (depth is not (1 or 2 or 4 or 8) || typeof(TPixel) != typeof(Rgba32) || (trns != null && trns.Length > plte!.Length / 3) || plte!.Length / 3 > 1 << depth)
        return null;
      colours = new Rgba32[1 << depth];
      for (int i = 0; i < plte!.Length / 3; i++)
        colours[i] = new Rgba32(plte[3 * i], plte[3 * i + 1], plte[3 * i + 2], trns != null && i < trns.Length ? trns[i] : (byte)255);
      paletteBits = depth;
      convert = null;
    }
    else
    {
      convert = Converter(colorType, depth);
      if (convert == null)
        return null;
    }
    // The zlib header: deflate with a window of at most 32 KB, a valid check value, no preset dictionary.
    var first = idat[0];
    if (first.Length < 2)
      return null;
    int cmf = file[first.At], flg = file[first.At + 1];
    if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
      return null;

    var rows = new PngRows<TPixel>(file, width, height, (int)rowBytes, bpp, convert, colours, paletteBits);
    rows.idat.AddRange(idat);
    rows.text.AddRange(text);
    return rows;
  }

  private static bool CrcMatches(byte[] file, int at, int size) =>
    Crc32.Compute(file, at + 4, size + 4) == Be32(file, at + 8 + size);

  // tEXt: a keyword (Latin-1), a zero byte, the text.
  private static void AddText(List<PngTextData> into, byte[] file, int at, int size)
  {
    int zero = Array.IndexOf(file, (byte)0, at, size);
    if (zero <= at || zero - at > 79)
      return;
    var keyword = new char[zero - at];
    for (int i = 0; i < keyword.Length; i++)
      keyword[i] = (char)file[at + i];
    var value = new char[at + size - zero - 1];
    for (int i = 0; i < value.Length; i++)
      value[i] = (char)file[zero + 1 + i];
    into.Add(new PngTextData(new string(keyword), new string(value), string.Empty, string.Empty));
  }

  // ---- the conversions: the pixel ImageSharp makes of each colour type and depth, for the pixel types the maps read -------

  private static RowConverter? Converter(int colorType, int depth)
  {
    if (typeof(TPixel) == typeof(L16))
      return (colorType, depth) switch
      {
        (0, 16) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, L16>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new L16((ushort)(raw[2 * x + 1] << 8 | raw[2 * x + 2]));
        },
        (0, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, L16>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new L16((ushort)(raw[x + 1] * 257));
        },
        _ => null,
      };
    if (typeof(TPixel) == typeof(La32))
      return (colorType, depth) switch
      {
        (4, 16) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La32((ushort)(raw[4 * x + 1] << 8 | raw[4 * x + 2]), (ushort)(raw[4 * x + 3] << 8 | raw[4 * x + 4]));
        },
        (0, 16) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La32((ushort)(raw[2 * x + 1] << 8 | raw[2 * x + 2]), ushort.MaxValue);
        },
        (4, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La32((ushort)(raw[2 * x + 1] * 257), (ushort)(raw[2 * x + 2] * 257));
        },
        (0, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La32((ushort)(raw[x + 1] * 257), ushort.MaxValue);
        },
        _ => null,
      };
    // Fine heights (heightmap-fine.png): 8-bit grey as it is, the byte a signed offset in 1/256 of a step.
    if (typeof(TPixel) == typeof(L8))
      return (colorType, depth) switch
      {
        (0, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, L8>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new L8(raw[x + 1]);
        },
        _ => null,
      };
    if (typeof(TPixel) == typeof(La16))
      return (colorType, depth) switch
      {
        (4, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La16>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La16(raw[2 * x + 1], raw[2 * x + 2]);
        },
        (0, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, La16>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new La16(raw[x + 1], 255);
        },
        _ => null,
      };
    if (typeof(TPixel) == typeof(Rgba32))
      return (colorType, depth) switch
      {
        (6, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, Rgba32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new Rgba32(raw[4 * x + 1], raw[4 * x + 2], raw[4 * x + 3], raw[4 * x + 4]);
        },
        (2, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, Rgba32>(row);
          for (int x = 0; x < to.Length; x++)
            to[x] = new Rgba32(raw[3 * x + 1], raw[3 * x + 2], raw[3 * x + 3], 255);
        },
        (0, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, Rgba32>(row);
          for (int x = 0; x < to.Length; x++)
          {
            var v = raw[x + 1];
            to[x] = new Rgba32(v, v, v, 255);
          }
        },
        (4, 8) => (raw, row) =>
        {
          var to = MemoryMarshal.Cast<TPixel, Rgba32>(row);
          for (int x = 0; x < to.Length; x++)
          {
            var v = raw[2 * x + 1];
            to[x] = new Rgba32(v, v, v, raw[2 * x + 2]);
          }
        },
        _ => null,
      };
    return null;
  }

  // A palette picture: each pixel's index in 1, 2, 4 or 8 bits, the leftmost in the highest bits of its byte.
  private void ConvertPalette(byte[] raw, Span<TPixel> row)
  {
    var to = MemoryMarshal.Cast<TPixel, Rgba32>(row);
    var colours = palette!;
    int bits = paletteBits, mask = (1 << bits) - 1, perByte = 8 / bits;
    for (int x = 0; x < to.Length; x++)
    {
      int shift = 8 - bits * (x % perByte + 1);
      to[x] = colours[(raw[1 + x / perByte] >> shift) & mask];
    }
  }

  // ---- reading --------------------------------------------------------------------------------------------------------

  internal override void ReadRows(int y0, int count, RowReader<TPixel> read)
  {
    if (y0 < 0 || count < 1 || y0 + count > height)
      throw new ArgumentOutOfRangeException(nameof(y0));
    // The map's rows y0 .. y0 + count - 1 are the file's rows from `first` on.
    int first = height - y0 - count;
    if (first < rowsRead)
      Restart();
    while (rowsRead < first)
      NextRow();
    if (pixels.Length != width)
      pixels = new TPixel[width];
    for (int i = 0; i < count; i++)
    {
      NextRow();
      if (palette != null)
        ConvertPalette(last, pixels);
      else
        convert!(last, pixels);
      read(count - 1 - i, pixels);
    }
  }

  private void Restart()
  {
    inflate?.Dispose();
    inflate = null;
    rowsRead = 0;
  }

  // The next row of the file, unfiltered, in `last`.
  private void NextRow()
  {
    if (inflate == null)
    {
      inflate = new DeflateStream(new IdatStream(file, idat, 2), CompressionMode.Decompress);
      last = new byte[rowBytes + 1];
      next = new byte[rowBytes + 1];
    }
    // Before the first row there is no row above: all zero.
    if (rowsRead == 0)
      Array.Clear(last, 0, last.Length);
    int got = 0;
    try
    {
      while (got < next.Length)
      {
        int n = inflate.Read(next, got, next.Length - got);
        if (n <= 0)
          throw new PngRowsException("the picture's data ends early");
        got += n;
      }
    }
    catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
    {
      // The runtime's own inflater says so in its own way (the game's Mono is not .NET's).
      throw new PngRowsException("the picture's data is damaged: " + e.Message);
    }
    Unfilter(next, last, next[0], bpp, rowBytes);
    (last, next) = (next, last);
    rowsRead++;
  }

  // The filters of PNG: each byte is stored as its difference from a prediction (a: the pixel to the left, b: the one above,
  // c: the one above that).
  private static void Unfilter(byte[] row, byte[] above, int filter, int bpp, int count)
  {
    switch (filter)
    {
      case 0:
        break;
      case 1:
        for (int i = 1 + bpp; i <= count; i++)
          row[i] += row[i - bpp];
        break;
      case 2:
        for (int i = 1; i <= count; i++)
          row[i] += above[i];
        break;
      case 3:
        for (int i = 1; i <= count; i++)
          row[i] += (byte)(((i > bpp ? row[i - bpp] : 0) + above[i]) >> 1);
        break;
      case 4:
        for (int i = 1; i <= count; i++)
        {
          int a = i > bpp ? row[i - bpp] : 0, b = above[i], c = i > bpp ? above[i - bpp] : 0;
          int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
          row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
        }
        break;
      default:
        throw new PngRowsException($"the picture uses filter {filter}, which PNG does not have");
    }
  }

  public override void Dispose()
  {
    inflate?.Dispose();
    inflate = null;
  }

  // The IDAT chunks' data one after the other as one stream, from `start` bytes in (the zlib header is skipped).
  private sealed class IdatStream : Stream
  {
    private readonly byte[] file;
    private readonly List<(int At, int Length)> chunks;
    private int chunk, offset;

    internal IdatStream(byte[] file, List<(int At, int Length)> chunks, int start)
    {
      this.file = file;
      this.chunks = chunks;
      while (start > 0 && chunk < chunks.Count)
      {
        int skip = Math.Min(start, chunks[chunk].Length - offset);
        offset += skip;
        start -= skip;
        if (offset >= chunks[chunk].Length)
        {
          chunk++;
          offset = 0;
        }
      }
    }

    public override int Read(byte[] buffer, int at, int count)
    {
      int total = 0;
      while (count > 0 && chunk < chunks.Count)
      {
        var (start, length) = chunks[chunk];
        int n = Math.Min(count, length - offset);
        Buffer.BlockCopy(file, start + offset, buffer, at, n);
        offset += n;
        at += n;
        count -= n;
        total += n;
        if (offset >= length)
        {
          chunk++;
          offset = 0;
        }
      }
      return total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}

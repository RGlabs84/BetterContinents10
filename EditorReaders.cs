// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;

namespace BetterContinents;

// What an editor hands back is a picture of one number per pixel: a float TIFF, a headerless .r32 / .raw, or a 16-bit PNG.
// A HeightSource reads it one row at a time, in any order, and never as a whole (a 16384 px float TIFF is 1 GB), and gives
// every pixel as a float: floats as they are, unsigned integers as their share of the largest (8 bit / 255, 16 bit / 65535).
// ImageSharp's TIFF decoder is not used: it hands pixels back as 16-bit steps and refuses the floating-point predictor, so it
// cannot give an editor's floats back exactly.
internal sealed class EditorReadException(string message) : Exception(message)
{
}

internal abstract class HeightSource : IDisposable
{
  /// <summary>The largest picture read (BetterContinents reads maps up to this wide).</summary>
  public const int MaxSide = 16384;

  public int Width { get; protected set; }
  public int Height { get; protected set; }

  /// <summary>What the file is, for the report: "32-bit float TIFF, uncompressed, strips".</summary>
  public string Kind { get; protected set; } = "";

  /// <summary>Warnings about what was ignored (an Orientation tag, ...).</summary>
  public List<string> Warnings { get; } = [];

  /// <summary>Row <paramref name="row"/> (0 = the top of the file) into <paramref name="to"/>, Width values.</summary>
  public abstract void ReadRow(int row, float[] to);

  public virtual void Dispose()
  {
  }

  /// <summary>The picture at <paramref name="path"/> by its extension: .tif/.tiff, .r32, .raw/.r16, .png. <paramref name="size"/>: the side of a headerless file when it
  /// is not the square root of its length.</summary>
  public static HeightSource Open(string path, int size = 0)
  {
    var ext = Path.GetExtension(path).ToLowerInvariant();
    switch (ext)
    {
      case ".tif":
      case ".tiff":
        return new TiffSource(path);
      case ".r32":
        return new RawSource(path, 4, size);
      case ".raw":
      case ".r16":
        return new RawSource(path, 2, size);
      case ".png":
        return new PngSource(path);
      case ".exr":
        throw new EditorReadException("EXR files are not read yet: build the height again as TIFF 32-bit or R32");
      default:
        throw new EditorReadException($"cannot tell what {Path.GetFileName(path)} is (TIFF, .r32, .raw or 16-bit PNG are read)");
    }
  }
}

// Headerless: 32-bit floats (.r32) or 16-bit unsigned (.raw), little endian, as wide as high.
internal sealed class RawSource : HeightSource
{
  private readonly FileStream file;
  private readonly int bytes;
  private byte[] row = [];

  public RawSource(string path, int bytesPerSample, int size)
  {
    bytes = bytesPerSample;
    file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    try
    {
      long length = file.Length;
      long samples = length / bytesPerSample;
      if (length % bytesPerSample != 0)
        throw new EditorReadException($"{Path.GetFileName(path)} is {length} bytes, not a whole number of {bytesPerSample}-byte samples");
      if (size <= 0)
      {
        size = (int)Math.Round(Math.Sqrt(samples));
        if ((long)size * size != samples)
          throw new EditorReadException($"{Path.GetFileName(path)} holds {samples} samples, which is not a square picture; give the side with size=");
      }
      else if ((long)size * size != samples)
        throw new EditorReadException($"{Path.GetFileName(path)} holds {samples} samples, not {size} x {size}");
      if (size < 1 || size > MaxSide)
        throw new EditorReadException($"{size} x {size} pixels: Better Continents reads pictures up to {MaxSide} wide");
      Width = Height = size;
      Kind = bytesPerSample == 4 ? "32-bit float, headerless" : "16-bit unsigned, headerless";
      row = new byte[bytesPerSample * size];
    }
    catch
    {
      file.Dispose();
      throw;
    }
  }

  public override void ReadRow(int r, float[] to)
  {
    if (r < 0 || r >= Height)
      throw new ArgumentOutOfRangeException(nameof(r));
    file.Seek((long)r * row.Length, SeekOrigin.Begin);
    TiffSource.ReadFully(file, row, row.Length);
    if (bytes == 4)
    {
      Buffer.BlockCopy(row, 0, to, 0, row.Length);
      if (!BitConverter.IsLittleEndian)
        throw new NotSupportedException("big-endian machine");
    }
    else
      for (int x = 0; x < Width; x++)
        to[x] = (row[2 * x] | row[2 * x + 1] << 8) / 65535f;
  }

  public override void Dispose() => file.Dispose();
}

// A 16-bit grey PNG (the other direction of a world export's heightmap.png), a pixel as its share of 65535.
internal sealed class PngSource : HeightSource
{
  private readonly MapPicture<L16> picture;
  private readonly ushort[] row;

  public PngSource(string path)
  {
    var data = File.ReadAllBytes(path);
    MapPicture<L16>? opened = PngRows<L16>.TryOpen(data);
    if (opened == null)
    {
      try
      {
        opened = new ImagePicture<L16>(SixLabors.ImageSharp.Image.Load<L16>(SixLabors.ImageSharp.Configuration.Default, data));
      }
      catch (Exception e)
      {
        throw new EditorReadException($"{Path.GetFileName(path)} is not a picture that can be read: {e.Message}");
      }
    }
    picture = opened;
    if (picture.Width != picture.Height || picture.Width > MaxSide)
      throw new EditorReadException($"{picture.Width} x {picture.Height} pixels: a square picture up to {MaxSide} wide is needed");
    Width = picture.Width;
    Height = picture.Height;
    Kind = "16-bit grey PNG";
    row = new ushort[Width];
  }

  public override void ReadRow(int r, float[] to)
  {
    if (r < 0 || r >= Height)
      throw new ArgumentOutOfRangeException(nameof(r));
    var local = row;
    // The file's row r is the map's row Height - 1 - r.
    picture.ReadRows(Height - 1 - r, 1, (_, pixels) =>
    {
      for (int x = 0; x < pixels.Length; x++)
        local[x] = pixels[x].PackedValue;
    });
    for (int x = 0; x < Width; x++)
      to[x] = local[x] / 65535f;
  }

  public override void Dispose() => picture.Dispose();
}

// Classic TIFF and BigTIFF, little or big endian, one grey sample per pixel (8 / 16 bit unsigned, 32-bit unsigned or float),
// strips or tiles, uncompressed or Deflate (zlib: compression 8 and 32946), predictor 1, 2 (8 / 16 bit) or 3 (floating point).
internal sealed class TiffSource : HeightSource
{
  private readonly FileStream file;
  private readonly bool big, bigEndian;
  private readonly int bits, format, compression, predictor, rowsPerStrip, tileW, tileH;
  private readonly long[] offsets, counts;
  private readonly bool tiled;
  private readonly int bytesPerSample;

  // The last block decoded: a strip (rows) or a tile row (tileH rows), with the first row it holds.
  private byte[]? decoded;
  private int decodedFirst = -1, decodedRows;

  private long ReadU(byte[] b, int at, int size)
  {
    long v = 0;
    if (bigEndian)
      for (int i = 0; i < size; i++)
        v = v << 8 | b[at + i];
    else
      for (int i = size - 1; i >= 0; i--)
        v = v << 8 | b[at + i];
    return v;
  }

  internal static void ReadFully(Stream s, byte[] to, int count)
  {
    int got = 0;
    while (got < count)
    {
      int n = s.Read(to, got, count - got);
      if (n <= 0)
        throw new EditorReadException("the file ends early");
      got += n;
    }
  }

  public TiffSource(string path)
  {
    file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    try
    {
      var name = Path.GetFileName(path);
      long length = file.Length;
      if (length < 16)
        throw new EditorReadException($"{name} is too short to be a TIFF");
      var head = new byte[16];
      ReadFully(file, head, 16);
      if (head[0] == 0x49 && head[1] == 0x49) bigEndian = false;
      else if (head[0] == 0x4D && head[1] == 0x4D) bigEndian = true;
      else throw new EditorReadException($"{name} is not a TIFF (no II or MM)");
      long magic = ReadU(head, 2, 2);
      long ifd;
      if (magic == 42)
        ifd = ReadU(head, 4, 4);
      else if (magic == 43)
      {
        big = true;
        if (ReadU(head, 4, 2) != 8)
          throw new EditorReadException("a BigTIFF with offsets that are not 8 bytes");
        ifd = ReadU(head, 8, 8);
      }
      else
        throw new EditorReadException($"{name} is not a TIFF (magic {magic})");
      if (ifd < 8 || ifd >= length)
        throw new EditorReadException("the TIFF's first directory is outside the file");

      file.Seek(ifd, SeekOrigin.Begin);
      int countSize = big ? 8 : 2, entrySize = big ? 20 : 12;
      var cb = new byte[8];
      ReadFully(file, cb, countSize);
      long entries = ReadU(cb, 0, countSize);
      if (entries < 1 || entries > 4096 || ifd + countSize + entries * entrySize > length)
        throw new EditorReadException("the TIFF's directory is damaged");
      var table = new byte[entries * entrySize];
      ReadFully(file, table, table.Length);

      var tags = new Dictionary<int, long[]>();
      for (int i = 0; i < entries; i++)
      {
        int at = i * entrySize;
        int tag = (int)ReadU(table, at, 2), type = (int)ReadU(table, at + 2, 2);
        long count = ReadU(table, at + 4, big ? 8 : 4);
        int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 0 };
        if (size == 0 || count < 0 || count > (1L << 26))
          continue;
        long total = count * size;
        int inlineBytes = big ? 8 : 4;
        long where;
        byte[] data;
        if (total <= inlineBytes)
        {
          data = new byte[inlineBytes];
          Buffer.BlockCopy(table, at + (big ? 12 : 8), data, 0, inlineBytes);
          where = -1;
        }
        else
        {
          where = ReadU(table, at + (big ? 12 : 8), inlineBytes);
          if (where < 0 || where + total > length)
            throw new EditorReadException($"the TIFF's tag {tag} points outside the file");
          var pos = file.Position;
          data = new byte[total];
          file.Seek(where, SeekOrigin.Begin);
          ReadFully(file, data, (int)total);
          file.Seek(pos, SeekOrigin.Begin);
        }
        var values = new long[count];
        for (int k = 0; k < count; k++)
          values[k] = type is 5 or 10 ? 0 : ReadU(data, k * size, size);
        tags[tag] = values;
      }

      long One(int tag, long fallback) => tags.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : fallback;
      long w = One(256, 0), h = One(257, 0);
      if (w < 1 || h < 1 || w > MaxSide * 2L || h > MaxSide * 2L)
        throw new EditorReadException($"{w} x {h} pixels: Better Continents reads pictures up to {MaxSide} wide");
      if (w != h)
        throw new EditorReadException($"{w} x {h} pixels: a square picture is needed");
      if (w > MaxSide)
        throw new EditorReadException($"{w} x {h} pixels: Better Continents reads pictures up to {MaxSide} wide (a bigger result needs a later version)");
      Width = (int)w;
      Height = (int)h;
      long spp = One(277, 1);
      if (spp != 1)
        throw new EditorReadException($"the TIFF has {spp} samples per pixel; one grey channel is needed (save the height as a single-channel image)");
      bits = (int)One(258, 1);
      format = (int)One(339, 1);
      long photometric = One(262, 1);
      if (photometric > 1)
        throw new EditorReadException($"the TIFF's photometric interpretation {photometric} is not grey");
      if (One(284, 1) != 1)
        throw new EditorReadException("a TIFF with separate planes is not read");
      if (!((bits == 8 || bits == 16 || bits == 32) && (format == 1 || (format == 3 && bits == 32))))
        throw new EditorReadException($"{bits}-bit samples of format {format}: 8, 16 or 32-bit unsigned or 32-bit float are read");
      bytesPerSample = bits / 8;
      compression = (int)One(259, 1);
      if (compression != 1 && compression != 8 && compression != 32946)
        throw new EditorReadException($"the TIFF is compressed with {CompressionName(compression)}; save it uncompressed or with Deflate, or as R32");
      predictor = (int)One(317, 1);
      if (predictor == 3 && !(format == 3 && bits == 32))
        throw new EditorReadException("the floating point predictor on samples that are not 32-bit floats");
      if (predictor == 2 && !(format == 1 && bits <= 16))
        throw new EditorReadException("the horizontal predictor is read for 8 and 16-bit integers only");
      if (predictor != 1 && predictor != 2 && predictor != 3)
        throw new EditorReadException($"the TIFF's predictor {predictor} is not read");
      if (predictor != 1 && compression == 1)
        throw new EditorReadException("a predictor without compression");
      if (One(274, 1) != 1)
        Warnings.Add($"the TIFF's Orientation tag ({One(274, 1)}) is ignored; the rows are read as stored");

      tiled = tags.ContainsKey(322);
      if (tiled)
      {
        tileW = (int)One(322, 0);
        tileH = (int)One(323, 0);
        if (tileW < 1 || tileH < 1 || tileW > 65536 || tileH > 65536)
          throw new EditorReadException("the TIFF's tiles are damaged");
        if (!tags.TryGetValue(324, out offsets!) || !tags.TryGetValue(325, out counts!))
          throw new EditorReadException("the TIFF has tiles but no tile offsets");
        long perRow = (Width + tileW - 1) / tileW, rowsOfTiles = (Height + tileH - 1) / tileH;
        if (offsets.Length != counts.Length || offsets.Length < perRow * rowsOfTiles)
          throw new EditorReadException("the TIFF's tile arrays are too short");
        if ((long)tileW * tileH * bytesPerSample > (1L << 30))
          throw new EditorReadException("the TIFF's tiles are too big");
      }
      else
      {
        if (!tags.TryGetValue(273, out offsets!))
          throw new EditorReadException("the TIFF has no strip offsets");
        rowsPerStrip = (int)Math.Min(One(278, Height), Height);
        if (rowsPerStrip < 1)
          rowsPerStrip = Height;
        long strips = (Height + rowsPerStrip - 1) / rowsPerStrip;
        if (!tags.TryGetValue(279, out counts!))
        {
          if (strips != 1 || compression != 1)
            throw new EditorReadException("the TIFF has no strip byte counts");
          counts = [(long)Width * Height * bytesPerSample];
        }
        if (offsets.Length < strips || counts.Length < strips)
          throw new EditorReadException("the TIFF's strip arrays are too short");
        if ((long)rowsPerStrip * Width * bytesPerSample > (1L << 31) - 64)
          throw new EditorReadException("the TIFF's strips are too big to decode");
      }
      for (int i = 0; i < offsets.Length; i++)
        if (offsets[i] < 0 || counts[i] < 0 || offsets[i] + counts[i] > length)
          throw new EditorReadException("a strip or tile of the TIFF lies past the end of the file");
      Kind = $"{bits}-bit {(format == 3 ? "float" : "unsigned")} TIFF, {(compression == 1 ? "uncompressed" : "Deflate" + (predictor == 3 ? " with the floating point predictor" : predictor == 2 ? " with the horizontal predictor" : ""))}, {(tiled ? "tiles" : "strips")}{(big ? ", BigTIFF" : "")}";
    }
    catch
    {
      file.Dispose();
      throw;
    }
  }

  internal static string CompressionName(int c) => c switch
  {
    5 => "LZW",
    6 or 7 => "JPEG",
    32773 => "PackBits",
    34925 => "LZMA",
    50000 => "Zstandard",
    _ => "compression " + c,
  };

  public override void Dispose() => file.Dispose();

  public override void ReadRow(int row, float[] to)
  {
    if (row < 0 || row >= Height)
      throw new ArgumentOutOfRangeException(nameof(row));
    if (tiled)
      ReadTiledRow(row, to);
    else
      ReadStripRow(row, to);
  }

  private void ReadStripRow(int row, float[] to)
  {
    int rowBytes = Width * bytesPerSample;
    int strip = row / rowsPerStrip;
    var raw = new byte[rowBytes];
    if (compression == 1)
    {
      long at = offsets[strip] + (long)(row % rowsPerStrip) * rowBytes;
      if ((long)(row % rowsPerStrip + 1) * rowBytes > counts[strip])
        throw new EditorReadException("a strip of the TIFF is shorter than its rows");
      file.Seek(at, SeekOrigin.Begin);
      ReadFully(file, raw, rowBytes);
      Convert(raw, 0, to);
      return;
    }
    int first = strip * rowsPerStrip;
    if (decodedFirst != first)
    {
      int rows = Math.Min(rowsPerStrip, Height - first);
      decoded = Inflate(offsets[strip], counts[strip], rows, Width, rowBytes);
      decodedFirst = first;
      decodedRows = rows;
    }
    Convert(decoded!, (row - first) * rowBytes, to);
  }

  private void ReadTiledRow(int row, float[] to)
  {
    int rowBytes = Width * bytesPerSample;
    int tileRow = row / tileH;
    int first = tileRow * tileH;
    if (decodedFirst != first)
    {
      int rows = Math.Min(tileH, Height - first);
      int perRow = (Width + tileW - 1) / tileW;
      var rowsBuffer = new byte[(long)tileH * (long)perRow * tileW * bytesPerSample];
      int tileBytesPerRow = tileW * bytesPerSample;
      for (int t = 0; t < perRow; t++)
      {
        int index = tileRow * perRow + t;
        byte[] tile = compression == 1 ? ReadRaw(offsets[index], counts[index], tileH * tileBytesPerRow) : Inflate(offsets[index], counts[index], tileH, tileW, tileBytesPerRow);
        for (int r = 0; r < tileH; r++)
          Buffer.BlockCopy(tile, r * tileBytesPerRow, rowsBuffer, r * perRow * tileBytesPerRow + t * tileBytesPerRow, tileBytesPerRow);
      }
      // A row of the assembled tile row is perRow * tileW samples wide; keep Width of them.
      decoded = new byte[(long)rows * rowBytes];
      for (int r = 0; r < rows; r++)
        Buffer.BlockCopy(rowsBuffer, r * perRow * tileBytesPerRow, decoded, r * rowBytes, rowBytes);
      decodedFirst = first;
      decodedRows = rows;
    }
    Convert(decoded!, (row - first) * rowBytes, to);
  }

  private byte[] ReadRaw(long at, long count, int expected)
  {
    var data = new byte[expected];
    if (count < expected)
      throw new EditorReadException("a tile of the TIFF is shorter than its pixels");
    file.Seek(at, SeekOrigin.Begin);
    ReadFully(file, data, expected);
    return data;
  }

  // A zlib stream of `rows` rows of `rowBytes` bytes, with the predictor undone.
  private byte[] Inflate(long at, long count, int rows, int pixelsPerRow, int rowBytes)
  {
    if (count < 6)
      throw new EditorReadException("a compressed strip of the TIFF is empty");
    var packed = new byte[count];
    file.Seek(at, SeekOrigin.Begin);
    ReadFully(file, packed, (int)count);
    long expected = (long)rows * rowBytes;
    if (expected > int.MaxValue - 64)
      throw new EditorReadException("a strip of the TIFF is too big to decode");
    var data = new byte[expected];
    try
    {
      // zlib: two header bytes, the deflate stream, the Adler-32.
      using var input = new MemoryStream(packed, 2, packed.Length - 2);
      using var inflate = new DeflateStream(input, CompressionMode.Decompress);
      ReadFully(inflate, data, data.Length);
    }
    catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
    {
      throw new EditorReadException("a compressed strip of the TIFF is damaged: " + e.Message);
    }
    if (predictor == 3)
    {
      for (int r = 0; r < rows; r++)
        UndoFloatPredictor(data, r * rowBytes, pixelsPerRow);
    }
    else if (predictor == 2)
    {
      for (int r = 0; r < rows; r++)
        UndoHorizontalPredictor(data, r * rowBytes, pixelsPerRow);
    }
    return data;
  }

  // TIFF Technical Note 3: the bytes of a row are added up along the row, and the row is the high bytes of all its samples, then the
  // next bytes, down to the low ones (most significant first, whatever the file's byte order).
  private void UndoFloatPredictor(byte[] data, int at, int pixels)
  {
    int n = pixels * 4;
    for (int i = 1; i < n; i++)
      data[at + i] += data[at + i - 1];
    var tmp = new byte[n];
    Buffer.BlockCopy(data, at, tmp, 0, n);
    for (int x = 0; x < pixels; x++)
      for (int b = 0; b < 4; b++)
      {
        // Byte b of the sample in the file's order: most significant first when the file is big endian.
        int plane = bigEndian ? b : 3 - b;
        data[at + 4 * x + b] = tmp[plane * pixels + x];
      }
  }

  private void UndoHorizontalPredictor(byte[] data, int at, int pixels)
  {
    if (bits == 8)
    {
      for (int x = 1; x < pixels; x++)
        data[at + x] += data[at + x - 1];
      return;
    }
    for (int x = 1; x < pixels; x++)
    {
      int prev = bigEndian ? data[at + 2 * (x - 1)] << 8 | data[at + 2 * (x - 1) + 1] : data[at + 2 * (x - 1) + 1] << 8 | data[at + 2 * (x - 1)];
      int cur = bigEndian ? data[at + 2 * x] << 8 | data[at + 2 * x + 1] : data[at + 2 * x + 1] << 8 | data[at + 2 * x];
      int sum = (prev + cur) & 0xFFFF;
      if (bigEndian) { data[at + 2 * x] = (byte)(sum >> 8); data[at + 2 * x + 1] = (byte)sum; }
      else { data[at + 2 * x] = (byte)sum; data[at + 2 * x + 1] = (byte)(sum >> 8); }
    }
  }

  private void Convert(byte[] data, int at, float[] to)
  {
    int w = Width;
    switch (bits)
    {
      case 8:
        for (int x = 0; x < w; x++)
          to[x] = data[at + x] / 255f;
        break;
      case 16:
        for (int x = 0; x < w; x++)
          to[x] = (bigEndian ? data[at + 2 * x] << 8 | data[at + 2 * x + 1] : data[at + 2 * x + 1] << 8 | data[at + 2 * x]) / 65535f;
        break;
      default:
        if (format == 3)
        {
          if (!bigEndian && BitConverter.IsLittleEndian)
            Buffer.BlockCopy(data, at, to, 0, 4 * w);
          else
            for (int x = 0; x < w; x++)
            {
              uint u = bigEndian ? (uint)(data[at + 4 * x] << 24 | data[at + 4 * x + 1] << 16 | data[at + 4 * x + 2] << 8 | data[at + 4 * x + 3])
                : (uint)(data[at + 4 * x + 3] << 24 | data[at + 4 * x + 2] << 16 | data[at + 4 * x + 1] << 8 | data[at + 4 * x]);
              to[x] = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
            }
        }
        else
          for (int x = 0; x < w; x++)
          {
            uint u = bigEndian ? (uint)(data[at + 4 * x] << 24 | data[at + 4 * x + 1] << 16 | data[at + 4 * x + 2] << 8 | data[at + 4 * x + 3])
              : (uint)(data[at + 4 * x + 3] << 24 | data[at + 4 * x + 2] << 16 | data[at + 4 * x + 1] << 8 | data[at + 4 * x]);
            to[x] = (float)(u / 4294967295.0);
          }
        break;
    }
  }
}

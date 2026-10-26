// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BetterContinents;

// A picture written a few rows at a time, whatever its container: what the editor pack's band runner feeds. PngRowWriter
// has the same three calls; PngRowSink adapts it. A sink is a file called <path>.tmp until Complete, so a crash never
// leaves a half file under a name an editor would load. (A later EXR or tiled writer is one more class of this kind.)
internal interface IRowSink : IDisposable
{
  /// <summary>Bytes of one row as <see cref="WriteRows"/> takes it.</summary>
  int RowBytes { get; }

  /// <summary>Appends <paramref name="rows"/> rows of <see cref="RowBytes"/> bytes each from <paramref name="data"/> at <paramref name="offset"/>, top (north) row first.</summary>
  void WriteRows(byte[] data, int offset, int rows);

  /// <summary>Finishes the file and gives it its name. All the rows must have been written.</summary>
  void Complete();
}

internal sealed class PngRowSink(PngRowWriter writer) : IRowSink
{
  public int RowBytes => writer.RowBytes;
  public void WriteRows(byte[] data, int offset, int rows) => writer.WriteRows(data, offset, rows);
  public void Complete() => writer.Complete();
  public void Dispose() => writer.Dispose();
}

// Classic TIFF 6.0, little endian, uncompressed, one sample per pixel, in strips of up to 64 rows. The size of the image is
// known before the first pixel, so every offset of the header is worked out up front and the file is written strictly in order:
//   0   49 49 2A 00, then the offset of the one IFD (8)
//   8   entry count (14), 14 entries of 12 bytes in ascending tag order, offset of the next IFD (0)
//       the arrays and texts that do not fit an entry (strip offsets and byte counts, ImageDescription, Software)
//       the strips, top row first, from an offset that is a multiple of 4
// 32-bit samples are IEEE floats (SampleFormat 3); 8 and 16 bits are unsigned (SampleFormat 1). Rows come little endian.
internal sealed class TiffRowWriter : IRowSink
{
  public const int RowsPerStripMax = 64;

  private readonly string path, tmp;
  private readonly int width, height, bits;
  private readonly FileStream file;
  private int rowsWritten;
  private bool completed, disposed;

  public int RowBytes { get; }

  /// <summary>The byte length of the header (everything before the first strip) for an image of this size and these texts.</summary>
  public static int HeaderLength(int height, string description, string software) => Layout(height, description, software, out _, out _);

  /// <summary>The whole file's length: the header and the pixels.</summary>
  public static long FileLength(int width, int height, int bits, string description, string software) =>
    HeaderLength(height, description, software) + (long)width * height * (bits / 8);

  public TiffRowWriter(string path, int width, int height, int bits, string description, string software)
  {
    if (width < 1 || height < 1)
      throw new ArgumentException("a TIFF needs at least one pixel");
    if (bits != 8 && bits != 16 && bits != 32)
      throw new ArgumentException("8, 16 or 32 bits");
    long bytes = (long)width * (bits / 8);
    if (bytes > int.MaxValue / 2)
      throw new ArgumentException("a row of this width does not fit");
    this.path = path;
    tmp = path + ".tmp";
    this.width = width;
    this.height = height;
    this.bits = bits;
    RowBytes = (int)bytes;
    int header = Layout(height, description, software, out _, out _);
    if ((long)header + bytes * height > uint.MaxValue)
      throw new ArgumentException("a classic TIFF holds at most 4 GiB; use smaller tiles");
    file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
    try
    {
      var head = Header(width, height, bits, description, software);
      file.Write(head, 0, head.Length);
    }
    catch
    {
      file.Dispose();
      TryDelete(tmp);
      throw;
    }
  }

  public void WriteRows(byte[] data, int offset, int rows)
  {
    if (completed || disposed)
      throw new InvalidOperationException("the TIFF is closed");
    if (rows < 0 || rowsWritten + rows > height || offset < 0 || (long)offset + (long)rows * RowBytes > data.Length)
      throw new ArgumentException("more rows than the image has, or than the buffer holds");
    file.Write(data, offset, rows * RowBytes);
    rowsWritten += rows;
  }

  public void Complete()
  {
    if (completed)
      return;
    if (disposed)
      throw new InvalidOperationException("the TIFF is closed");
    if (rowsWritten != height)
      throw new InvalidOperationException($"the TIFF has {rowsWritten} of {height} rows");
    file.Dispose();
    completed = true;
    disposed = true;
    if (File.Exists(path))
      File.Delete(path);
    File.Move(tmp, path);
  }

  public void Dispose()
  {
    if (disposed)
      return;
    disposed = true;
    try { file.Dispose(); } catch { }
    TryDelete(tmp);
  }

  private static void TryDelete(string f)
  {
    try
    {
      if (File.Exists(f))
        File.Delete(f);
    }
    catch
    {
      // The export deletes what it tracked as well.
    }
  }

  // Where the pieces go: returns the offset of the first strip; the offsets of the strip arrays and texts as out values.
  private static int Layout(int height, string description, string software, out int[] places, out int strips)
  {
    strips = (height + RowsPerStripMax - 1) / RowsPerStripMax;
    int at = 8 + 2 + 14 * 12 + 4;
    var d = Ascii(description);
    var s = Ascii(software);
    // Order: strip offsets, strip byte counts (only when more than one strip: one fits the entry), description, software.
    int offsets = 0, counts = 0, desc = 0, soft = 0;
    if (strips > 1)
    {
      offsets = at;
      at += 4 * strips;
      counts = at;
      at += 4 * strips;
    }
    if (d.Length > 4)
    {
      desc = at;
      at += (d.Length + 1) & ~1;
    }
    if (s.Length > 4)
    {
      soft = at;
      at += (s.Length + 1) & ~1;
    }
    places = [offsets, counts, desc, soft];
    return (at + 3) & ~3;
  }

  // Description and software as ASCII with their terminating zero.
  private static byte[] Ascii(string text)
  {
    var bytes = new byte[text.Length + 1];
    for (int i = 0; i < text.Length; i++)
      bytes[i] = text[i] < 128 && text[i] >= 32 ? (byte)text[i] : (byte)'?';
    return bytes;
  }

  internal static byte[] Header(int width, int height, int bits, string description, string software)
  {
    int first = Layout(height, description, software, out var places, out int strips);
    var b = new byte[first];
    int rowsPerStrip = Math.Min(height, RowsPerStripMax);
    long rowBytes = (long)width * (bits / 8);
    var d = Ascii(description);
    var s = Ascii(software);
    b[0] = 0x49; b[1] = 0x49; b[2] = 0x2A; b[3] = 0x00;
    Put32(b, 4, 8);
    Put16(b, 8, 14);
    int e = 10;
    void Entry(int tag, int type, int count, uint value)
    {
      Put16(b, e, tag);
      Put16(b, e + 2, type);
      Put32(b, e + 4, (uint)count);
      if (type == 3 && count == 1)
        Put16(b, e + 8, (int)value);
      else
        Put32(b, e + 8, value);
      e += 12;
    }
    Entry(256, 4, 1, (uint)width);
    Entry(257, 4, 1, (uint)height);
    Entry(258, 3, 1, (uint)bits);
    Entry(259, 3, 1, 1);
    Entry(262, 3, 1, 1);
    Entry(270, 2, d.Length, d.Length > 4 ? (uint)places[2] : Inline(d));
    Entry(273, 4, strips, strips > 1 ? (uint)places[0] : (uint)first);
    Entry(274, 3, 1, 1);
    Entry(277, 3, 1, 1);
    Entry(278, 4, 1, (uint)rowsPerStrip);
    Entry(279, 4, strips, strips > 1 ? (uint)places[1] : (uint)(rowBytes * height));
    Entry(284, 3, 1, 1);
    Entry(305, 2, s.Length, s.Length > 4 ? (uint)places[3] : Inline(s));
    Entry(339, 3, 1, bits == 32 ? 3u : 1u);
    Put32(b, e, 0);
    if (strips > 1)
    {
      long at = first;
      for (int k = 0; k < strips; k++)
      {
        int rows = Math.Min(rowsPerStrip, height - k * rowsPerStrip);
        Put32(b, places[0] + 4 * k, (uint)at);
        Put32(b, places[1] + 4 * k, (uint)(rows * rowBytes));
        at += rows * rowBytes;
      }
    }
    if (d.Length > 4)
      Buffer.BlockCopy(d, 0, b, places[2], d.Length);
    if (s.Length > 4)
      Buffer.BlockCopy(s, 0, b, places[3], s.Length);
    return b;
  }

  private static uint Inline(byte[] text)
  {
    uint v = 0;
    for (int i = 0; i < text.Length && i < 4; i++)
      v |= (uint)text[i] << (8 * i);
    return v;
  }

  private static void Put16(byte[] b, int at, int v)
  {
    b[at] = (byte)v;
    b[at + 1] = (byte)(v >> 8);
  }

  private static void Put32(byte[] b, int at, uint v)
  {
    b[at] = (byte)v;
    b[at + 1] = (byte)(v >> 8);
    b[at + 2] = (byte)(v >> 16);
    b[at + 3] = (byte)(v >> 24);
  }
}

// Headerless 32-bit floats, little endian, row after row, top row first: a Gaea .r32 (the image is as wide as it is high, so
// the reader works the size out from the file's length).
internal sealed class RawFloatRowWriter : IRowSink
{
  private readonly string path, tmp;
  private readonly int width, height;
  private readonly FileStream file;
  private int rowsWritten;
  private bool completed, disposed;

  public int RowBytes { get; }

  public RawFloatRowWriter(string path, int width, int height)
  {
    if (width < 1 || height < 1)
      throw new ArgumentException("an image needs at least one pixel");
    this.path = path;
    tmp = path + ".tmp";
    this.width = width;
    this.height = height;
    RowBytes = checked(4 * width);
    file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
  }

  public void WriteRows(byte[] data, int offset, int rows)
  {
    if (completed || disposed)
      throw new InvalidOperationException("the file is closed");
    if (rows < 0 || rowsWritten + rows > height || offset < 0 || (long)offset + (long)rows * RowBytes > data.Length)
      throw new ArgumentException("more rows than the image has, or than the buffer holds");
    file.Write(data, offset, rows * RowBytes);
    rowsWritten += rows;
  }

  public void Complete()
  {
    if (completed)
      return;
    if (disposed)
      throw new InvalidOperationException("the file is closed");
    if (rowsWritten != height)
      throw new InvalidOperationException($"the file has {rowsWritten} of {height} rows");
    file.Dispose();
    completed = true;
    disposed = true;
    if (File.Exists(path))
      File.Delete(path);
    File.Move(tmp, path);
  }

  public void Dispose()
  {
    if (disposed)
      return;
    disposed = true;
    try { file.Dispose(); } catch { }
    try
    {
      if (File.Exists(tmp))
        File.Delete(tmp);
    }
    catch
    {
      // The export deletes what it tracked as well.
    }
  }
}

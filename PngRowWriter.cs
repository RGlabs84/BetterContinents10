// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.IO;
using System.IO.Compression;

namespace BetterContinents;

// A PNG written a few rows at a time, the way a world export at 16384 x 16384 px needs. ImageSharp's encoder takes a
// whole image, and a whole 16384 px map is 0.27 GB (8 bits), 0.54 GB (16 bits) or 0.81 GB (colour) before any other map
// is counted; this keeps the rows being sampled and nothing else, whatever the size.
//
// It writes the PNG of the usual export maps (8 or 16 bits a sample, grey, RGB or RGBA, no palette, no interlacing): a
// scanline filter chosen per row (the one with the smallest sum of absolute values, the way the PNG specification
// suggests and ImageSharp does by default), the filtered rows deflated by System.IO.Compression (the codec MapTiles already
// uses in the game) inside a zlib stream, IDAT chunks of 128 KB, and up to two tEXt chunks, which is all the metadata an
// export writes: one before the pixels (the heightmap's record) and one after them (the fine heights file's, which holds the
// CRC-32 of a file that is finished later). The file is <path>.tmp until Complete, so a crash never leaves a half-written map
// under a name Better Continents would load.
internal sealed class PngRowWriter : IDisposable
{
  private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
  private const int ChunkBytes = 128 * 1024;
  // Filtered rows wait here until this much is ready: one call into the deflater per ~256 KB, not per row.
  private const int StageBytes = 256 * 1024;

  private readonly string path, tmp;
  private readonly int width, height, bytesPerPixel, rowBytes;
  private readonly FileStream file;
  private readonly IdatStream idat;
  private readonly DeflateStream deflate;
  private readonly byte[] previous;
  private readonly byte[] current;
  private readonly byte[][] candidates = new byte[5][];
  private readonly byte[] stage;
  private int staged;
  private uint adler = 1;
  private int rowsWritten;
  private bool completed, disposed;

  /// <summary>Bytes of one row as <see cref="WriteRows"/> takes it: width x channels x (bit depth / 8), 16-bit samples big endian.</summary>
  public int RowBytes => rowBytes;

  /// <summary>Opens <paramref name="path"/>.tmp for a <paramref name="width"/> x <paramref name="height"/> image of
  /// <paramref name="channels"/> samples (1 grey, 3 RGB, 4 RGBA) of <paramref name="bitDepth"/> bits (8 or 16). A text chunk
  /// <paramref name="textKeyword"/> = <paramref name="text"/> goes before the pixels when both are given.</summary>
  public PngRowWriter(string path, int width, int height, int channels, int bitDepth, string? textKeyword = null, string? text = null)
  {
    if (width < 1 || height < 1)
      throw new ArgumentException("a PNG needs at least one pixel");
    if (channels != 1 && channels != 3 && channels != 4)
      throw new ArgumentException("1, 3 or 4 channels");
    if (bitDepth != 8 && bitDepth != 16)
      throw new ArgumentException("8 or 16 bits");
    this.path = path;
    tmp = path + ".tmp";
    this.width = width;
    this.height = height;
    bytesPerPixel = channels * bitDepth / 8;
    long bytes = (long)width * bytesPerPixel;
    if (bytes > int.MaxValue / 2)
      throw new ArgumentException("a row of this width does not fit");
    rowBytes = (int)bytes;
    previous = new byte[rowBytes];
    current = new byte[rowBytes];
    for (int k = 0; k < candidates.Length; k++)
    {
      candidates[k] = new byte[rowBytes + 1];
      candidates[k][0] = (byte)k;
    }
    stage = new byte[StageBytes + rowBytes + 1];
    file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
    try
    {
      file.Write(Signature, 0, Signature.Length);
      var header = new byte[13];
      BigEndian(header, 0, (uint)width);
      BigEndian(header, 4, (uint)height);
      header[8] = (byte)bitDepth;
      header[9] = channels == 1 ? (byte)0 : channels == 3 ? (byte)2 : (byte)6;
      Chunk(file, "IHDR", header, header.Length);
      if (textKeyword != null && text != null)
        TextChunk(file, textKeyword, text);
      idat = new IdatStream(file, ChunkBytes);
      // The zlib header: deflate, a 32 KB window, the default level, no preset dictionary.
      idat.WriteByte(0x78);
      idat.WriteByte(0x9C);
      deflate = new DeflateStream(idat, CompressionLevel.Optimal, true);
    }
    catch
    {
      file.Dispose();
      TryDelete(tmp);
      throw;
    }
  }

  /// <summary>Appends <paramref name="rows"/> rows of <see cref="RowBytes"/> bytes each, taken from
  /// <paramref name="data"/> at <paramref name="offset"/>, top row first.</summary>
  public void WriteRows(byte[] data, int offset, int rows)
  {
    if (completed || disposed)
      throw new InvalidOperationException("the PNG is closed");
    if (rows < 0 || rowsWritten + rows > height || offset < 0 || (long)offset + (long)rows * rowBytes > data.Length)
      throw new ArgumentException("more rows than the image has, or than the buffer holds");
    for (int r = 0; r < rows; r++)
    {
      Buffer.BlockCopy(data, offset + r * rowBytes, current, 0, rowBytes);
      Row();
    }
  }

  /// <summary>Writes the end of the file, closes it and renames it to its name. The image must be complete.</summary>
  public void Complete() => Complete(null, null);

  /// <summary>Like <see cref="Complete()"/>, with a text chunk <paramref name="textKeyword"/> = <paramref name="text"/> after the
  /// pixels (before the end of the file), for a record that can only be written once the image is: the fine heights file holds
  /// the CRC-32 of the heightmap that is finished after it, and PNG allows a text chunk anywhere between the header and the end.</summary>
  public void Complete(string? textKeyword, string? text)
  {
    if (completed)
      return;
    if (disposed)
      throw new InvalidOperationException("the PNG is closed");
    if (rowsWritten != height)
      throw new InvalidOperationException($"the PNG has {rowsWritten} of {height} rows");
    FlushStage();
    deflate.Dispose();
    var trailer = new byte[4];
    BigEndian(trailer, 0, adler);
    idat.Write(trailer, 0, 4);
    idat.Finish();
    if (textKeyword != null && text != null)
      TextChunk(file, textKeyword, text);
    Chunk(file, "IEND", [], 0);
    file.Dispose();
    completed = true;
    disposed = true;
    if (File.Exists(path))
      File.Delete(path);
    File.Move(tmp, path);
  }

  /// <summary>Closes the file and, unless <see cref="Complete"/> ran, deletes it.</summary>
  public void Dispose()
  {
    if (disposed)
      return;
    disposed = true;
    try { deflate.Dispose(); } catch { }
    try { file.Dispose(); } catch { }
    TryDelete(tmp);
  }

  private static void TryDelete(string file)
  {
    try
    {
      if (File.Exists(file))
        File.Delete(file);
    }
    catch
    {
      // The export deletes what it tracked as well.
    }
  }

  // ---- one row --------------------------------------------------------------------------------------------------------

  private void Row()
  {
    int bpp = bytesPerPixel, n = rowBytes;
    var raw = current;
    var up = previous;
    // The five filters, each with the sum of its output bytes read as signed numbers (a small sum is a row that deflates well).
    long best = 0;
    var none = candidates[0];
    for (int i = 0; i < n; i++)
    {
      byte v = raw[i];
      none[i + 1] = v;
      best += v < 128 ? v : 256 - v;
    }
    int pick = 0;
    long sum = 0;
    var sub = candidates[1];
    for (int i = 0; i < bpp && i < n; i++)
    {
      byte v = raw[i];
      sub[i + 1] = v;
      sum += v < 128 ? v : 256 - v;
    }
    for (int i = bpp; i < n; i++)
    {
      byte v = (byte)(raw[i] - raw[i - bpp]);
      sub[i + 1] = v;
      sum += v < 128 ? v : 256 - v;
    }
    if (sum < best) { best = sum; pick = 1; }
    sum = 0;
    var upc = candidates[2];
    for (int i = 0; i < n; i++)
    {
      byte v = (byte)(raw[i] - up[i]);
      upc[i + 1] = v;
      sum += v < 128 ? v : 256 - v;
    }
    if (sum < best) { best = sum; pick = 2; }
    sum = 0;
    var avg = candidates[3];
    for (int i = 0; i < n; i++)
    {
      int left = i >= bpp ? raw[i - bpp] : 0;
      byte v = (byte)(raw[i] - ((left + up[i]) >> 1));
      avg[i + 1] = v;
      sum += v < 128 ? v : 256 - v;
    }
    if (sum < best) { best = sum; pick = 3; }
    sum = 0;
    var paeth = candidates[4];
    for (int i = 0; i < n; i++)
    {
      int a = i >= bpp ? raw[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
      int p = a + b - c;
      int pa = p > a ? p - a : a - p, pb = p > b ? p - b : b - p, pc = p > c ? p - c : c - p;
      int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      byte v = (byte)(raw[i] - predictor);
      paeth[i + 1] = v;
      sum += v < 128 ? v : 256 - v;
    }
    if (sum < best) { best = sum; pick = 4; }

    var chosen = candidates[pick];
    adler = Adler32(adler, chosen, 0, n + 1);
    Buffer.BlockCopy(chosen, 0, stage, staged, n + 1);
    staged += n + 1;
    if (staged >= StageBytes)
      FlushStage();
    Buffer.BlockCopy(raw, 0, previous, 0, n);
    rowsWritten++;
  }

  private void FlushStage()
  {
    if (staged > 0)
      deflate.Write(stage, 0, staged);
    staged = 0;
  }

  // ---- the pieces of a PNG -------------------------------------------------------------------------------------------

  // The text as ISO 8859-1 bytes (the encoding of a tEXt chunk); what it cannot hold becomes '?'.
  private static byte[] Latin1(string text)
  {
    var bytes = new byte[text.Length];
    for (int i = 0; i < bytes.Length; i++)
      bytes[i] = text[i] < 256 ? (byte)text[i] : (byte)'?';
    return bytes;
  }

  // A tEXt chunk: the keyword, a zero byte and the text, both as ISO 8859-1.
  private static void TextChunk(Stream to, string textKeyword, string text)
  {
    var keyword = Latin1(textKeyword);
    var value = Latin1(text);
    var body = new byte[keyword.Length + 1 + value.Length];
    Buffer.BlockCopy(keyword, 0, body, 0, keyword.Length);
    Buffer.BlockCopy(value, 0, body, keyword.Length + 1, value.Length);
    Chunk(to, "tEXt", body, body.Length);
  }

  private static void BigEndian(byte[] to, int at, uint v)
  {
    to[at] = (byte)(v >> 24);
    to[at + 1] = (byte)(v >> 16);
    to[at + 2] = (byte)(v >> 8);
    to[at + 3] = (byte)v;
  }

  // length, type, data, CRC-32 of the type and the data.
  private static void Chunk(Stream to, string type, byte[] data, int count)
  {
    var head = new byte[8];
    BigEndian(head, 0, (uint)count);
    for (int i = 0; i < 4; i++)
      head[4 + i] = (byte)type[i];
    to.Write(head, 0, 8);
    uint crc = Crc32(0, head, 4, 4);
    if (count > 0)
    {
      to.Write(data, 0, count);
      crc = Crc32(crc, data, 0, count);
    }
    var tail = new byte[4];
    BigEndian(tail, 0, crc);
    to.Write(tail, 0, 4);
  }

  // Collects the compressed bytes and writes them as IDAT chunks.
  private sealed class IdatStream(Stream file, int chunk) : Stream
  {
    private readonly byte[] buffer = new byte[chunk];
    private int filled;

    public override void Write(byte[] data, int offset, int count)
    {
      while (count > 0)
      {
        int take = Math.Min(count, buffer.Length - filled);
        Buffer.BlockCopy(data, offset, buffer, filled, take);
        filled += take;
        offset += take;
        count -= take;
        if (filled == buffer.Length)
          Emit();
      }
    }

    public override void WriteByte(byte value) => Write([value], 0, 1);

    private void Emit()
    {
      if (filled > 0)
        Chunk(file, "IDAT", buffer, filled);
      filled = 0;
    }

    public void Finish() => Emit();

    public override void Flush()
    {
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

  private static readonly uint[] CrcTable = BuildCrcTable();

  private static uint[] BuildCrcTable()
  {
    var table = new uint[256];
    for (uint n = 0; n < 256; n++)
    {
      uint c = n;
      for (int k = 0; k < 8; k++)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }

  /// <summary>CRC-32 (the PNG and zip one) of a byte range, continued from <paramref name="crc"/> (0 to start).</summary>
  internal static uint Crc32(uint crc, byte[] data, int offset, int count)
  {
    uint c = ~crc;
    for (int i = 0; i < count; i++)
      c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
    return ~c;
  }

  /// <summary>CRC-32 of a whole file (what zlib.crc32 gives for its bytes), read a megabyte at a time.</summary>
  internal static uint FileCrc32(string path)
  {
    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    var buffer = new byte[1 << 20];
    uint crc = 0;
    int read;
    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
      crc = Crc32(crc, buffer, 0, read);
    return crc;
  }

  /// <summary>Adler-32 (the zlib one) of a byte range, continued from <paramref name="adler"/> (1 to start).</summary>
  internal static uint Adler32(uint adler, byte[] data, int offset, int count)
  {
    uint a = adler & 0xFFFF, b = adler >> 16;
    while (count > 0)
    {
      // 5552 bytes at most before the sums can overflow 32 bits.
      int k = Math.Min(count, 5552);
      count -= k;
      for (; k > 0; k--)
      {
        a += data[offset++];
        b += a;
      }
      a %= 65521;
      b %= 65521;
    }
    return (b << 16) | a;
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.IO;
using System.IO.Compression;

namespace BetterContinents;

// A PNG written a row at a time: the signature, IHDR, an optional tEXt chunk, one zlib stream of the rows in IDAT chunks, IEND.
// ImageSharp's encoder takes a whole image, and writing a map back out from its tiles (ImageMapBase.Png: a world saved in an
// older format, a world export's sources) held one: 537 MB for a 16384 px 16-bit map, 1 GB for a colour one. Each row is
// filtered with the one of None, Sub and Up that leaves the smallest bytes, and deflated as it comes; no metadata but the
// text asked for is written (gAMA would shift legend colours).
internal static class PngWriter
{
  private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

  // width x height; colorType and depth as PNG numbers them (0 grey, 2 RGB, 4 grey + alpha, 6 RGBA; 8 or 16 bits); row(y, raw)
  // writes the raw bytes of file row y (from the north) as the type has them, big-endian for 16 bits.
  internal static byte[] Write(int width, int height, int colorType, int depth, Action<int, byte[]> row, string? textKeyword = null, string? text = null)
  {
    int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG colour type {colorType}") };
    if (depth is not (8 or 16))
      throw new NotSupportedException($"PNG bit depth {depth}");
    int bpp = channels * depth / 8, rowBytes = checked(width * bpp);
    using var stream = new MemoryStream();
    stream.Write(Signature, 0, Signature.Length);

    var ihdr = new byte[13];
    PutInt(ihdr, 0, width);
    PutInt(ihdr, 4, height);
    ihdr[8] = (byte)depth;
    ihdr[9] = (byte)colorType;
    Chunk(stream, "IHDR", ihdr, ihdr.Length);
    if (text != null)
    {
      // Latin-1, as tEXt is (and as ImageSharp writes it).
      var data = new byte[textKeyword!.Length + 1 + text.Length];
      for (int i = 0; i < textKeyword.Length; i++)
        data[i] = (byte)textKeyword[i];
      for (int i = 0; i < text.Length; i++)
        data[textKeyword.Length + 1 + i] = (byte)text[i];
      Chunk(stream, "tEXt", data, data.Length);
    }

    var sink = new IdatSink(stream);
    sink.Write([0x78, 0x9C], 0, 2);
    uint a = 1, b = 0;
    using (var deflate = new DeflateStream(sink, CompressionLevel.Optimal, leaveOpen: true))
    {
      var raw = new byte[rowBytes];
      var above = new byte[rowBytes];
      // The filter byte, then the row: three candidates, the best is written.
      var none = new byte[rowBytes + 1];
      var sub = new byte[rowBytes + 1];
      var up = new byte[rowBytes + 1];
      sub[0] = 1;
      up[0] = 2;
      for (int y = 0; y < height; y++)
      {
        row(y, raw);
        Buffer.BlockCopy(raw, 0, none, 1, rowBytes);
        long costNone = 0, costSub = 0, costUp = 0;
        for (int i = 0; i < rowBytes; i++)
        {
          byte v = raw[i];
          costNone += Math.Abs((int)(sbyte)v);
          byte s = (byte)(v - (i >= bpp ? raw[i - bpp] : 0));
          sub[1 + i] = s;
          costSub += Math.Abs((int)(sbyte)s);
          byte u = (byte)(v - above[i]);
          up[1 + i] = u;
          costUp += Math.Abs((int)(sbyte)u);
        }
        var best = costSub <= costNone && costSub <= costUp ? sub : costUp < costNone ? up : none;
        deflate.Write(best, 0, best.Length);
        for (int i = 0; i < best.Length; i++)
        {
          a += best[i];
          b += a;
          // 5552 is the most bytes that cannot overflow the sums before the modulo (zlib's NMAX).
          if ((i & 0xFFF) == 0xFFF)
          {
            a %= 65521;
            b %= 65521;
          }
        }
        a %= 65521;
        b %= 65521;
        (raw, above) = (above, raw);
      }
    }
    var adler = (b << 16) | a;
    sink.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler], 0, 4);
    sink.Flush();
    Chunk(stream, "IEND", [], 0);
    return stream.ToArray();
  }

  private static void PutInt(byte[] to, int at, int v)
  {
    to[at] = (byte)(v >> 24);
    to[at + 1] = (byte)(v >> 16);
    to[at + 2] = (byte)(v >> 8);
    to[at + 3] = (byte)v;
  }

  private static void Chunk(Stream to, string type, byte[] data, int length)
  {
    var header = new byte[8];
    PutInt(header, 0, length);
    for (int i = 0; i < 4; i++)
      header[4 + i] = (byte)type[i];
    to.Write(header, 0, 8);
    to.Write(data, 0, length);
    // The CRC covers the type and the data.
    var all = new byte[4 + length];
    Buffer.BlockCopy(header, 4, all, 0, 4);
    Buffer.BlockCopy(data, 0, all, 4, length);
    var crc = Crc32.Compute(all, 0, all.Length);
    var tail = new byte[4];
    PutInt(tail, 0, (int)crc);
    to.Write(tail, 0, 4);
  }

  // The compressed bytes, cut into IDAT chunks of 64 KB.
  private sealed class IdatSink(Stream to) : Stream
  {
    private readonly byte[] buffer = new byte[1 << 16];
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
        Chunk(to, "IDAT", buffer, held);
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
}

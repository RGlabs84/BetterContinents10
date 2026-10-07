// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace BetterContinents;

// A map's pixels held as tiles of 128 x 128 (TileGrid). A tile whose pixels all hold the same value (open sea, the
// canvas around a painted area) is kept as that one value and costs nothing to read.
//
// A grid holds the values the picture gave, unconverted (16-bit grey, an 8-bit channel, a biome's byte, a legend index,
// RGBA). Each map converts them exactly as it did when it held its whole picture decoded, so every sample is the same.
//
// By default every tile is decoded when the map is read and stays (MapRows, TileGrid<T>.Fill), and a world saves its
// maps' pictures as it always did. With Compact Maps (a world made since 0.10 with it on, or on Auto with a map of more than 8192
// pixels across or a heightmap with fine heights), each tile is
// kept compressed and decoded only when something reads it, the decoded tiles of every map under one memory budget
// (TileCache): a world's maps stream in and out of memory the way its zones do. The compressed tiles (TileBlock) are
// then what the world saves and sends. Each tile is compressed with Deflate, which the game has natively, after
// whichever transform makes it smallest: none, its bytes split into planes, or the residuals of the median predictor
// (LOCO-I's, from the pixels to the left, below and below-left), which suits heights.
internal sealed class TileBlock
{
  internal const byte FormatVersion = 1;
  internal const int Shift = 7;
  internal const int Side = 1 << Shift;
  internal const int Mask = Side - 1;
  internal const int Pixels = Side * Side;
  // The largest side a block may declare: a check against a damaged block, far beyond any world (Valheim keeps its
  // objects within about 16 km of the centre).
  internal const int MaxSize = 1 << 16;

  // How a tile is stored.
  internal const byte Uniform = 0, Raw = 1, Planes = 2, Median = 3;

  internal readonly int Size;
  internal readonly int Channels;
  internal readonly int Bytes;
  internal readonly int Tiles;
  // The block as written: its header, every tile, and a CRC-32 of all that.
  internal readonly byte[] Data;
  private readonly byte[] modes;
  private readonly int[] offsets;
  private readonly int[] lengths;

  private TileBlock(byte[] data, int size, int channels, int bytes, byte[] modes, int[] offsets, int[] lengths)
  {
    Data = data;
    Size = size;
    Channels = channels;
    Bytes = bytes;
    Tiles = TilesFor(size);
    this.modes = modes;
    this.offsets = offsets;
    this.lengths = lengths;
  }

  internal static int TilesFor(int size) => (size + Mask) >> Shift;
  internal int Count => Tiles * Tiles;
  internal byte Mode(int t) => modes[t];
  internal bool IsUniform(int t) => modes[t] == Uniform;
  // The pixels of tile column (or row) i: a full tile's 128, fewer for the last.
  internal int Extent(int i) => Math.Min(Side, Size - (i << Shift));
  // The compressed size of tile t (0 for a uniform one).
  internal int StoredLength(int t) => modes[t] == Uniform ? 0 : lengths[t];

  // The value of channel c in uniform tile t.
  internal ushort UniformValue(int t, int c)
  {
    int at = offsets[t] + c * Bytes;
    return Bytes == 2 ? (ushort)(Data[at] | Data[at + 1] << 8) : Data[at];
  }

  // ---- reading -----------------------------------------------------------------------------------------------------

  // Within a map's own block (ImageMapFloat.ToBlock and the others): the block, length first.
  internal void WriteTo(BinaryWriter writer)
  {
    writer.Write(Data.Length);
    writer.Write(Data);
  }

  internal static TileBlock ReadFrom(BinaryReader reader)
  {
    int length = reader.ReadInt32();
    if (length < 12 || length > reader.BaseStream.Length - reader.BaseStream.Position)
      throw new InvalidDataException("a map's tiles end early");
    return Read(reader.ReadBytes(length));
  }

  // A block as saved. Throws InvalidDataException when it is damaged or from a newer format.
  internal static TileBlock Read(byte[] data)
  {
    if (data == null || data.Length < 12)
      throw new InvalidDataException("a map's tiles are missing");
    int body = data.Length - 4;
    uint crc = (uint)(data[body] | data[body + 1] << 8 | data[body + 2] << 16 | data[body + 3] << 24);
    if (Crc32.Compute(data, 0, body) != crc)
      throw new InvalidDataException("a map's tiles are damaged (their checksum does not match)");
    using var reader = new BinaryReader(new MemoryStream(data, 0, body, false));
    var version = reader.ReadByte();
    if (version != FormatVersion)
      throw new InvalidDataException($"a map's tiles are in format {version}, which this version of Better Continents cannot read");
    int size = reader.ReadInt32();
    int channels = reader.ReadByte();
    int bytes = reader.ReadByte();
    int shift = reader.ReadByte();
    if (size < 1 || size > MaxSize || channels < 1 || channels > 4 || (bytes != 1 && bytes != 2) || shift != Shift)
      throw new InvalidDataException($"a map's tiles declare an impossible shape ({size} pixels, {channels} channels of {bytes} bytes, tiles of 2^{shift})");
    int count = TilesFor(size) * TilesFor(size);
    var modes = new byte[count];
    var offsets = new int[count];
    var lengths = new int[count];
    var stream = reader.BaseStream;
    for (int t = 0; t < count; t++)
    {
      var mode = reader.ReadByte();
      int length;
      if (mode == Uniform)
        length = channels * bytes;
      else if (mode >= Raw && mode <= Median)
        length = reader.ReadInt32();
      else
        throw new InvalidDataException($"a map tile is stored in an unknown way ({mode})");
      if (length < 0 || stream.Position + length > body)
        throw new InvalidDataException("a map's tiles end early");
      modes[t] = mode;
      offsets[t] = (int)stream.Position;
      lengths[t] = length;
      stream.Position += length;
    }
    if (stream.Position != body)
      throw new InvalidDataException("a map's tiles are followed by data they do not describe");
    return new TileBlock(data, size, channels, bytes, modes, offsets, lengths);
  }

  // Decodes tile t into values: channel c's value at (x, y) of the tile at values[c * Pixels + (y << Shift) + x]. Only
  // the tile's own extent is written (a tile on the map's last row or column of tiles can be smaller).
  internal void Decode(int t, ushort[] values)
  {
    int w = Extent(t % Tiles), h = Extent(t / Tiles);
    var mode = modes[t];
    if (mode == Uniform)
    {
      for (int c = 0; c < Channels; c++)
      {
        var v = UniformValue(t, c);
        int start = c * Pixels;
        for (int y = 0; y < h; y++)
          for (int i = start + (y << Shift), end = i + w; i < end; i++)
            values[i] = v;
      }
      return;
    }
    var scratch = Scratch.Get();
    int count = w * h * Channels * Bytes;
    Inflate(Data, offsets[t], lengths[t], scratch.Raw, count);
    if (mode == Raw)
    {
      FromInterleaved(scratch.Raw, values, w, h, Channels, Bytes);
      return;
    }
    if (mode == Planes)
    {
      FromPlanes(scratch.Raw, values, w, h, Channels, Bytes);
      return;
    }
    FromPlanes(scratch.Raw, values, w, h, Channels, Bytes);
    for (int c = 0; c < Channels; c++)
      UndoMedian(values, c * Pixels, w, h, Bytes);
  }

  // ---- writing -----------------------------------------------------------------------------------------------------

  // Reads rows y0 to y0 + rows - 1 of a map into band: channel c's value at column x of the band's row r goes to
  // band[(c * Side + r) * size + x].
  internal delegate void BandReader(int y0, int rows, ushort[] band);

  internal static TileBlock Encode(MapRows rows) => Encode(rows.Size, rows.Channels, rows.Bytes, rows.Median, rows.Read);

  // Encodes a map read a band of 128 rows at a time (each band's tiles in parallel). median: whether the median
  // predictor may be tried (values that are amounts, not categories).
  internal static TileBlock Encode(int size, int channels, int bytes, bool median, BandReader read)
  {
    if (size < 1 || size > MaxSize)
      throw new ArgumentOutOfRangeException(nameof(size), $"a map of {size} pixels across");
    if (channels < 1 || channels > 4 || (bytes != 1 && bytes != 2))
      throw new ArgumentOutOfRangeException(nameof(channels));
    int tiles = TilesFor(size);
    var modes = new byte[tiles * tiles];
    var payloads = new byte[tiles * tiles][];
    var band = new ushort[channels * Side * size];
    // From the top of the map down: a PNG's rows come from its file in that order (MapRows.Read), and the tiles do not mind.
    for (int ty = tiles - 1; ty >= 0; ty--)
    {
      int y0 = ty << Shift, rows = Math.Min(Side, size - y0);
      read(y0, rows, band);
      int first = ty * tiles;
      Parallel.For(0, tiles, tx =>
      {
        int x0 = tx << Shift;
        payloads[first + tx] = EncodeTile(band, size, x0, Math.Min(Side, size - x0), rows, channels, bytes, median, out modes[first + tx]);
      });
    }
    return Assemble(size, channels, bytes, modes, payloads);
  }

  private static TileBlock Assemble(int size, int channels, int bytes, byte[] modes, byte[][] payloads)
  {
    // The block's exact length first, so it is written once into an array of its own (a stream and a copy of it held the
    // compressed tiles of a 16384 px map two to three times over).
    long length = 3 + 4 + 1 + 4;
    for (int t = 0; t < modes.Length; t++)
      length += 1 + (modes[t] != Uniform ? 4 : 0) + payloads[t].Length;
    if (length > int.MaxValue)
      throw new InvalidOperationException($"a map of {size} pixels across is {length:N0} bytes of tiles, more than a block can hold");
    var data = new byte[length];
    int at = 0;
    void Put(byte b) => data[at++] = b;
    void PutInt(int v)
    {
      data[at++] = (byte)v;
      data[at++] = (byte)(v >> 8);
      data[at++] = (byte)(v >> 16);
      data[at++] = (byte)(v >> 24);
    }
    Put(FormatVersion);
    PutInt(size);
    Put((byte)channels);
    Put((byte)bytes);
    Put((byte)Shift);
    for (int t = 0; t < modes.Length; t++)
    {
      Put(modes[t]);
      if (modes[t] != Uniform)
        PutInt(payloads[t].Length);
      Buffer.BlockCopy(payloads[t], 0, data, at, payloads[t].Length);
      at += payloads[t].Length;
      payloads[t] = null!;
    }
    PutInt((int)Crc32.Compute(data, 0, at));
    return Read(data);
  }

  // The tile at column x0 of a band (w by h pixels) into values, as Decode writes a tile.
  internal static void FromBand(ushort[] band, int size, int x0, int w, int h, int channels, ushort[] values)
  {
    for (int c = 0; c < channels; c++)
      for (int y = 0; y < h; y++)
        Array.Copy(band, (c * Side + y) * size + x0, values, c * Pixels + (y << Shift), w);
  }

  private static byte[] EncodeTile(ushort[] band, int size, int x0, int w, int h, int channels, int bytes, bool median, out byte mode)
  {
    var scratch = Scratch.Get();
    var values = scratch.Values;
    FromBand(band, size, x0, w, h, channels, values);

    if (AllSame(values, w, h, channels))
    {
      mode = Uniform;
      var value = new byte[channels * bytes];
      for (int c = 0; c < channels; c++)
      {
        var v = values[c * Pixels];
        value[c * bytes] = (byte)v;
        if (bytes == 2)
          value[c * bytes + 1] = (byte)(v >> 8);
      }
      return value;
    }

    // The smallest of the transforms tried; the first of equals. Each is compressed into one of two streams the thread keeps
    // (the better stays, the other is written over), and only the winner is copied out: a 16384 px map is 16384 tiles, and a
    // stream and an array for each of three tries were two gigabytes of garbage.
    mode = Raw;
    var best = scratch.First;
    var other = scratch.Second;
    int bestLength = Deflate(scratch.Raw, ToInterleaved(values, scratch.Raw, w, h, channels, bytes), best);
    if (channels > 1 || bytes > 1)
    {
      int planesLength = Deflate(scratch.Raw, ToPlanes(values, scratch.Raw, w, h, channels, bytes), other);
      if (planesLength < bestLength)
      {
        (best, other) = (other, best);
        bestLength = planesLength;
        mode = Planes;
      }
    }
    if (median)
    {
      var residuals = scratch.Residuals;
      for (int c = 0; c < channels; c++)
        ApplyMedian(values, residuals, c * Pixels, w, h, bytes);
      int predictedLength = Deflate(scratch.Raw, ToPlanes(residuals, scratch.Raw, w, h, channels, bytes), other);
      if (predictedLength < bestLength)
      {
        (best, other) = (other, best);
        bestLength = predictedLength;
        mode = Median;
      }
    }
    var result = new byte[bestLength];
    Buffer.BlockCopy(best.GetBuffer(), 0, result, 0, bestLength);
    return result;
  }

  // Whether every pixel of a tile (w by h, as Decode writes it) holds the same value in each channel.
  internal static bool AllSame(ushort[] values, int w, int h, int channels)
  {
    for (int c = 0; c < channels; c++)
    {
      int start = c * Pixels;
      var first = values[start];
      for (int y = 0; y < h; y++)
        for (int i = start + (y << Shift), end = i + w; i < end; i++)
          if (values[i] != first)
            return false;
    }
    return true;
  }

  // Pixel by pixel, each pixel's channels in order, each value low byte first.
  private static int ToInterleaved(ushort[] values, byte[] raw, int w, int h, int channels, int bytes)
  {
    int n = 0;
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++)
        for (int c = 0; c < channels; c++)
        {
          var v = values[c * Pixels + (y << Shift) + x];
          raw[n++] = (byte)v;
          if (bytes == 2)
            raw[n++] = (byte)(v >> 8);
        }
    return n;
  }

  private static void FromInterleaved(byte[] raw, ushort[] values, int w, int h, int channels, int bytes)
  {
    int n = 0;
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++)
        for (int c = 0; c < channels; c++)
        {
          int v = raw[n++];
          if (bytes == 2)
            v |= raw[n++] << 8;
          values[c * Pixels + (y << Shift) + x] = (ushort)v;
        }
  }

  // Channel by channel, and within a channel all low bytes, then all high bytes.
  private static int ToPlanes(ushort[] values, byte[] raw, int w, int h, int channels, int bytes)
  {
    int n = 0;
    for (int c = 0; c < channels; c++)
      for (int b = 0; b < bytes; b++)
      {
        int shift = b * 8;
        for (int y = 0; y < h; y++)
          for (int i = c * Pixels + (y << Shift), end = i + w; i < end; i++)
            raw[n++] = (byte)(values[i] >> shift);
      }
    return n;
  }

  private static void FromPlanes(byte[] raw, ushort[] values, int w, int h, int channels, int bytes)
  {
    int n = 0;
    for (int c = 0; c < channels; c++)
    {
      for (int y = 0; y < h; y++)
        for (int i = c * Pixels + (y << Shift), end = i + w; i < end; i++)
          values[i] = raw[n++];
      if (bytes == 2)
        for (int y = 0; y < h; y++)
          for (int i = c * Pixels + (y << Shift), end = i + w; i < end; i++)
            values[i] |= (ushort)(raw[n++] << 8);
    }
  }

  // The median predictor: from the pixel before (a), the one in the row before (b) and the one before that (c), c
  // past both picks the nearer edge, else a + b - c; the first row predicts from a, the first column from b.
  private static int Predict(ushort[] values, int i, int x, int y)
  {
    if (y == 0)
      return x == 0 ? 0 : values[i - 1];
    if (x == 0)
      return values[i - Side];
    int a = values[i - 1], b = values[i - Side], c = values[i - Side - 1];
    int max = a > b ? a : b, min = a > b ? b : a;
    return c >= max ? min : c <= min ? max : a + b - c;
  }

  // The residuals of one channel's values (from start), zigzagged so that small differences either way are small
  // numbers: in 16 or 8 bits as the values are.
  private static void ApplyMedian(ushort[] values, ushort[] residuals, int start, int w, int h, int bytes)
  {
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++)
      {
        int i = start + (y << Shift) + x;
        int d = values[i] - Predict(values, i, x, y);
        residuals[i] = bytes == 2
          ? (ushort)(((short)d << 1) ^ ((short)d >> 15))
          : (ushort)(byte)(((sbyte)d << 1) ^ ((sbyte)d >> 7));
      }
  }

  // Back from the residuals, in place, in the order the predictor needs.
  private static void UndoMedian(ushort[] values, int start, int w, int h, int bytes)
  {
    for (int y = 0; y < h; y++)
      for (int x = 0; x < w; x++)
      {
        int i = start + (y << Shift) + x;
        int z = values[i];
        int d = (z >> 1) ^ -(z & 1);
        int v = Predict(values, i, x, y) + d;
        values[i] = bytes == 2 ? (ushort)v : (byte)v;
      }
  }

  // raw[0 .. count) compressed into `into` (emptied first); how long the result is.
  private static int Deflate(byte[] raw, int count, MemoryStream into)
  {
    into.SetLength(0);
    using (var deflate = new DeflateStream(into, CompressionMode.Compress, true))
      deflate.Write(raw, 0, count);
    return (int)into.Length;
  }

  private static void Inflate(byte[] data, int offset, int length, byte[] into, int count)
  {
    using var inflate = new DeflateStream(new MemoryStream(data, offset, length, false), CompressionMode.Decompress);
    int read = 0;
    while (read < count)
    {
      int n = inflate.Read(into, read, count - read);
      if (n <= 0)
        throw new InvalidDataException("a map tile ends early");
      read += n;
    }
  }

  // Each thread's working buffers, big enough for a tile of four 16-bit channels.
  private sealed class Scratch
  {
    [ThreadStatic] private static Scratch? current;
    internal readonly ushort[] Values = new ushort[4 * Pixels];
    internal readonly ushort[] Residuals = new ushort[4 * Pixels];
    internal readonly byte[] Raw = new byte[8 * Pixels];
    // The compressed bytes of the tries of EncodeTile.
    internal readonly MemoryStream First = new(), Second = new();
    internal static Scratch Get() => current ??= new Scratch();
  }
}

// CRC-32 (IEEE 802.3, as zip and PNG use), eight bytes at a time (a 16384 px world's tiles and pictures are hundreds of
// megabytes, and every one is checked when it is read: a byte at a time took a second for 400 MB).
internal static class Crc32
{
  private static readonly uint[] Table = Build();

  private static uint[] Build()
  {
    // Table[k * 256 + n]: the CRC of byte n followed by k zero bytes; k = 0 is the plain table.
    var table = new uint[8 * 256];
    for (uint n = 0; n < 256; n++)
    {
      uint c = n;
      for (int k = 0; k < 8; k++)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    for (int n = 0; n < 256; n++)
      for (int k = 1; k < 8; k++)
      {
        uint c = table[(k - 1) * 256 + n];
        table[k * 256 + n] = table[c & 0xFF] ^ (c >> 8);
      }
    return table;
  }

  internal static uint Compute(byte[] data, int offset, int count) => Continue(0, data, offset, count);

  // The CRC-32 of what came before (0 for nothing) followed by data[offset .. offset + count): a file's, a block at a time.
  internal static uint Continue(uint before, byte[] data, int offset, int count)
  {
    var t = Table;
    uint crc = ~before;
    int i = offset, end = offset + count;
    for (int fast = end - 8; i <= fast; i += 8)
    {
      uint low = crc ^ (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
      crc = t[7 * 256 + (low & 0xFF)] ^ t[6 * 256 + ((low >> 8) & 0xFF)] ^ t[5 * 256 + ((low >> 16) & 0xFF)] ^ t[4 * 256 + (low >> 24)]
          ^ t[3 * 256 + data[i + 4]] ^ t[2 * 256 + data[i + 5]] ^ t[256 + data[i + 6]] ^ t[data[i + 7]];
    }
    for (; i < end; i++)
      crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
    return ~crc;
  }
}

// A map's values row by row, as a picture (or the bytes a world saved) gives them: what its tiles are made from, either
// compressed (TileBlock.Encode) or decoded (TileGrid<T>.Fill). Channels of Bytes each; Median: whether compressing may try
// the median predictor (values that are amounts, not categories).
internal sealed class MapRows(int size, int channels, int bytes, bool median, TileBlock.BandReader read)
{
  internal readonly int Size = size;
  internal readonly int Channels = channels;
  internal readonly int Bytes = bytes;
  internal readonly bool Median = median;
  internal readonly TileBlock.BandReader Read = read;
}

// The tiles of one map, read through the tile they fall in. A compressed grid (Compact Maps) decodes a tile when it is
// first read, and TileCache may drop it again (then it is decoded again when read again); a uniform tile is a single
// value and stays. A decoded grid (Compact Maps off) holds every tile decoded from the start, and keeps them.
internal abstract class TileGrid
{
  internal const int Shift = TileBlock.Shift;
  internal const int Mask = TileBlock.Mask;

  // The compressed tiles; null when every tile is decoded and kept.
  internal readonly TileBlock? Block;
  internal readonly int Size;
  protected readonly int Tiles;
  internal int Count => Tiles * Tiles;
  // Set when a tile is read; TileCache clears it and drops a decoded tile that stayed unread since (second chance).
  protected readonly byte[] Used;
  // This grid's share of TileCache's budget: its entry there, once it has decoded a tile.
  internal TileCache.Account? Account;

  protected TileGrid(TileBlock block) : this(block, block.Size)
  {
  }

  protected TileGrid(int size) : this(null, size)
  {
  }

  private TileGrid(TileBlock? block, int size)
  {
    Block = block;
    Size = size;
    Tiles = TileBlock.TilesFor(size);
    Used = new byte[Count];
  }

  // Whether the tiles are kept compressed and decoded when read (Compact Maps).
  internal bool Compressed => Block != null;

  // The memory one decoded tile takes.
  internal abstract long TileBytes { get; }
  // TileCache: whether tile t is decoded (and not uniform), and dropping it.
  internal abstract bool IsDecoded(int t);
  internal abstract bool Drop(int t);
  // Clears tile t's read mark; whether it was set.
  internal bool TakeUsed(int t)
  {
    if (Used[t] == 0)
      return false;
    Used[t] = 0;
    return true;
  }

  // How many of the tiles are decoded now, uniform ones aside (tests and the console).
  internal int DecodedTiles()
  {
    int n = 0;
    for (int t = 0; t < Count; t++)
      if (IsDecoded(t))
        n++;
    return n;
  }

  // Drops every decoded tile of a compressed grid (a lean world import, which only saves the map); they are decoded
  // again if read. A decoded grid keeps its tiles.
  internal void DropAll()
  {
    for (int t = 0; t < Count; t++)
      if (Drop(t))
        TileCache.Removed(this);
  }
}

internal abstract class TileGrid<T> : TileGrid
{
  // Each tile: TileBlock.Pixels values, index (y << Shift) | x; or, for a uniform tile, its one value, which
  // index & (length - 1) reads whatever the index.
  private readonly T[]?[] decoded;
  private int failures;

  // Compressed: tiles decoded when read.
  protected TileGrid(TileBlock block) : base(block) => decoded = new T[]?[block.Count];

  // Decoded: the tiles come from Fill.
  protected TileGrid(int size) : base(size) => decoded = new T[]?[Count];

  // A tile from its values, channel c's value at (x, y) of the tile at values[c * Pixels + (y << Shift) + x], as
  // TileBlock.Decode writes them. values is a working buffer: the tile copies what it keeps.
  protected abstract T[] FromValues(ushort[] values);
  // The value of a uniform tile, from its channels' values.
  protected abstract T UniformValue(ushort[] channels);

  // Each thread's buffer for a tile's values, big enough for four channels.
  [ThreadStatic] private static ushort[]? buffer;
  private static ushort[] Buffer() => buffer ??= new ushort[4 * TileBlock.Pixels];

  private T[] DecodeTile(int t)
  {
    var values = Buffer();
    Block!.Decode(t, values);
    return FromValues(values);
  }

  // A decoded grid's tiles, every one decoded from the map's rows a band of 128 rows at a time; nothing is compressed.
  protected void Fill(MapRows rows)
  {
    int size = Size, channels = rows.Channels;
    var band = new ushort[channels * TileBlock.Side * size];
    var values = Buffer();
    var uniform = new ushort[channels];
    for (int ty = Tiles - 1; ty >= 0; ty--)
    {
      int y0 = ty << Shift, h = Math.Min(TileBlock.Side, size - y0);
      rows.Read(y0, h, band);
      for (int tx = 0; tx < Tiles; tx++)
      {
        int x0 = tx << Shift, w = Math.Min(TileBlock.Side, size - x0);
        TileBlock.FromBand(band, size, x0, w, h, channels, values);
        if (TileBlock.AllSame(values, w, h, channels))
        {
          for (int c = 0; c < channels; c++)
            uniform[c] = values[c * TileBlock.Pixels];
          decoded[ty * Tiles + tx] = [UniformValue(uniform)];
        }
        else
          decoded[ty * Tiles + tx] = FromValues(values);
      }
    }
  }

  internal T[] Tile(int t)
  {
    var tile = decoded[t] ?? Load(t);
    if (Used[t] == 0)
      Used[t] = 1;
    return tile;
  }

  internal T Get(int x, int y)
  {
    var tile = Tile((y >> Shift) * Tiles + (x >> Shift));
    return tile[(((y & Mask) << Shift) | (x & Mask)) & (tile.Length - 1)];
  }

  // Row y of the map into into[offset ..], tile by tile (writing a whole map back out).
  internal void CopyRow(int y, T[] into, int offset)
  {
    int ty = y >> Shift, row = (y & Mask) << Shift;
    for (int tx = 0; tx < Tiles; tx++)
    {
      var tile = Tile(ty * Tiles + tx);
      int x0 = tx << Shift, w = Math.Min(TileBlock.Side, Size - x0);
      if (tile.Length == 1)
        for (int x = 0; x < w; x++)
          into[offset + x0 + x] = tile[0];
      else
        Array.Copy(tile, row, into, offset + x0, w);
    }
  }

  // The four pixels a bilinear sample reads: (x0, y0), (x1, y0), (x0, y1), (x1, y1), with x1 and y1 at most one past.
  internal void Quad(int x0, int x1, int y0, int y1, out T v00, out T v10, out T v01, out T v11)
  {
    int tx = x0 >> Shift, ty = y0 >> Shift;
    if (x1 >> Shift == tx && y1 >> Shift == ty)
    {
      var tile = Tile(ty * Tiles + tx);
      int m = tile.Length - 1;
      int r0 = (y0 & Mask) << Shift, r1 = (y1 & Mask) << Shift, c0 = x0 & Mask, c1 = x1 & Mask;
      v00 = tile[(r0 | c0) & m];
      v10 = tile[(r0 | c1) & m];
      v01 = tile[(r1 | c0) & m];
      v11 = tile[(r1 | c1) & m];
      return;
    }
    v00 = Get(x0, y0);
    v10 = Get(x1, y0);
    v01 = Get(x0, y1);
    v11 = Get(x1, y1);
  }

  private T[] Load(int t)
  {
    // A decoded grid holds every tile: only a compressed one gets here.
    var block = Block ?? throw new InvalidOperationException("a map's decoded tile is missing");
    T[] tile;
    bool counted = false;
    if (block.IsUniform(t))
    {
      var channels = new ushort[block.Channels];
      for (int c = 0; c < channels.Length; c++)
        channels[c] = block.UniformValue(t, c);
      tile = [UniformValue(channels)];
    }
    else
    {
      try
      {
        tile = DecodeTile(t);
        counted = true;
      }
      catch (Exception e)
      {
        // Cannot happen to a block that passed its checksum; should it, the tile reads as its first channels' zero
        // instead of throwing inside the game's terrain builder.
        if (Interlocked.Increment(ref failures) == 1)
          BetterContinents.LogError($"A map tile could not be decoded ({e.Message}); it reads as 0. The world's settings may be damaged.");
        tile = [UniformValue(new ushort[block.Channels])];
      }
    }
    var prior = Interlocked.CompareExchange(ref decoded[t], tile, null);
    if (prior != null)
      return prior;
    if (counted)
      TileCache.Added(this);
    return tile;
  }

  internal override bool IsDecoded(int t) => decoded[t] is { Length: > 1 };

  // Only a compressed grid's tiles are dropped: a decoded grid could not decode them again.
  internal override bool Drop(int t)
  {
    var tile = decoded[t];
    return Block != null && tile is { Length: > 1 } && Interlocked.CompareExchange(ref decoded[t], null, tile) == tile;
  }
}

// One channel of 8 or 16 bits, read as it was stored.
internal sealed class ValueGrid : TileGrid<ushort>
{
  internal ValueGrid(TileBlock block) : base(block)
  {
  }

  private ValueGrid(int size) : base(size)
  {
  }

  // From a map's rows: compressed (Compact Maps), or every tile decoded.
  internal static ValueGrid From(MapRows rows, bool compact)
  {
    if (compact)
      return new ValueGrid(TileBlock.Encode(rows));
    var grid = new ValueGrid(rows.Size);
    grid.Fill(rows);
    return grid;
  }

  internal override long TileBytes => 2L * TileBlock.Pixels;

  protected override ushort[] FromValues(ushort[] values)
  {
    var tile = new ushort[TileBlock.Pixels];
    Array.Copy(values, tile, tile.Length);
    return tile;
  }

  protected override ushort UniformValue(ushort[] channels) => channels[0];
}

// One channel of 8 bits (a biome's byte, a legend index), a byte a pixel when decoded.
internal sealed class ByteGrid : TileGrid<byte>
{
  internal ByteGrid(TileBlock block) : base(block)
  {
  }

  private ByteGrid(int size) : base(size)
  {
  }

  // From a map's rows: compressed (Compact Maps), or every tile decoded.
  internal static ByteGrid From(MapRows rows, bool compact)
  {
    if (compact)
      return new ByteGrid(TileBlock.Encode(rows));
    var grid = new ByteGrid(rows.Size);
    grid.Fill(rows);
    return grid;
  }

  // From bytes as a world saved them (a biome or spawn map), row by row, each byte normalized first if asked.
  internal static ByteGrid FromBytes(int size, byte[] data, Func<byte, byte>? normalize, bool compact) =>
    From(new MapRows(size, 1, 1, false, (y0, rows, band) =>
    {
      for (int r = 0; r < rows; r++)
      {
        int from = (y0 + r) * size, to = r * size;
        for (int x = 0; x < size; x++)
          band[to + x] = normalize == null ? data[from + x] : normalize(data[from + x]);
      }
    }), compact);

  internal override long TileBytes => TileBlock.Pixels;

  protected override byte[] FromValues(ushort[] values)
  {
    var tile = new byte[TileBlock.Pixels];
    for (int i = 0; i < tile.Length; i++)
      tile[i] = (byte)values[i];
    return tile;
  }

  protected override byte UniformValue(ushort[] channels) => (byte)channels[0];
}

// The decoded tiles of every compressed map together (Compact Maps), under one budget: past it, tiles that were not read
// since the last sweep are dropped (second chance), and decoded again when read again. A decoded grid is never here.
internal static class TileCache
{
  // The default budget, in bytes: holds every tile of a large world's maps, so a world normally decodes each once.
  internal const long DefaultBudget = 512L << 20;
  // The budget: Map Memory ([07 BetterContinents.Misc]), read at each sweep, so a change applies at once; tests set
  // Override.
  internal static long? Override;
  internal static long Budget => Override ?? (BetterContinents.ConfigMapMemory is { Value: > 0 } memory ? memory.Value * (1L << 20) : DefaultBudget);
  private static long resident;
  internal static long Resident => Interlocked.Read(ref resident);

  internal sealed class Account
  {
    internal long Bytes;
  }

  private sealed class Entry(TileGrid grid, Account account)
  {
    internal readonly WeakReference<TileGrid> Grid = new(grid);
    internal readonly Account Account = account;
  }

  private static readonly List<Entry> entries = [];
  private static readonly object gate = new();
  private static int hand, tileHand;

  internal static void Added(TileGrid grid)
  {
    var account = grid.Account;
    if (account == null)
    {
      lock (gate)
      {
        account = grid.Account;
        if (account == null)
        {
          account = new Account();
          entries.Add(new Entry(grid, account));
          grid.Account = account;
        }
      }
    }
    Interlocked.Add(ref account.Bytes, grid.TileBytes);
    long budget = Budget;
    long now = Interlocked.Add(ref resident, grid.TileBytes);
    if (now > budget)
      Trim(wait: now > budget + (budget >> 2));
  }

  internal static void Removed(TileGrid grid)
  {
    if (grid.Account is { } account)
      Interlocked.Add(ref account.Bytes, -grid.TileBytes);
    Interlocked.Add(ref resident, -grid.TileBytes);
  }

  // Down to seven eighths of the budget, so a sweep is not needed again at once. One thread sweeps and the others go
  // on, unless the tiles are already a quarter past the budget: then a thread waits its turn to sweep, which keeps a
  // flood of reads (or a sweeping thread the system set aside) from running far past it.
  private static void Trim(bool wait)
  {
    if (wait)
      Monitor.Enter(gate);
    else if (!Monitor.TryEnter(gate))
      return;
    try
    {
      long target = Budget - (Budget >> 3);
      // Maps no longer in use first: their tiles went with them.
      for (int i = entries.Count - 1; i >= 0; i--)
        if (!entries[i].Grid.TryGetTarget(out _))
        {
          Interlocked.Add(ref resident, -Interlocked.Exchange(ref entries[i].Account.Bytes, 0));
          entries.RemoveAt(i);
          if (hand > i)
            hand--;
          else if (hand == i)
            tileHand = 0;
        }
      // Two full turns of the clock clear every read mark and then drop what stayed unread; should reads keep marking
      // them faster than that, a third turn drops tiles whatever their marks (a reader keeps the tile it holds).
      int visits = 0, marked = 2 * entries.Count + 2, limit = 3 * entries.Count + 3;
      while (Resident > target && entries.Count > 0 && visits++ < limit)
      {
        bool force = visits > marked;
        if (hand >= entries.Count)
        {
          hand = 0;
          tileHand = 0;
        }
        var entry = entries[hand];
        if (!entry.Grid.TryGetTarget(out var grid))
        {
          // A map no longer in use: its tiles went with it.
          Interlocked.Add(ref resident, -Interlocked.Exchange(ref entry.Account.Bytes, 0));
          entries.RemoveAt(hand);
          tileHand = 0;
          continue;
        }
        int count = grid.Count;
        while (tileHand < count && Resident > target)
        {
          int t = tileHand++;
          if (!grid.IsDecoded(t) || (grid.TakeUsed(t) && !force))
            continue;
          if (grid.Drop(t))
            Removed(grid);
        }
        if (tileHand >= count)
        {
          tileHand = 0;
          hand++;
        }
      }
    }
    finally
    {
      Monitor.Exit(gate);
    }
  }
}

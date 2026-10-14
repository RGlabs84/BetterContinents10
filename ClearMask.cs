// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BetterContinents;

// THE CONTRACT of the clearing mask (BUILD-SPEC-TARN.md 2.1, 2.11), the data agent's (A): a bitmask over the world's ground, square, Size cells a
// side, spanning -Extent..+Extent in x and z, where a set bit means "no tree, rock or bush here". Immutable; the bits are decoded lazily and only on the
// machine that runs the world (the one that places vegetation).
//
// Tarn's ".clear" file, byte for byte [T: Clearing.cs, T-HANDOFF "File formats"]:
//   offset 0   the ASCII bytes TARNCLR1
//   offset 8   int32 size (4096 for Ravenrest)
//   offset 12  float32 half-extent in metres (10500)
//   offset 16  size x size bits, row 0 the NORTH edge, high bit first (numpy packbits), ceil(size x size / 8) bytes
// Pixel (r, c) is centred at x = (c + 0.5) x mpx - extent, z = extent - (r + 0.5) x mpx, with mpx = 2 x extent / size. A marked pixel is one square clear
// area of half-width mpx / 2 + 0.25 m (a hair over, so neighbours leave no seam): a point is covered when it lies strictly inside one.
//
// Blob 73 (DataKey.Clearing), format 1: int version = 1; int size; float extent; int rawLength; int packedLength; the packed bytes. The packed bytes are
// the raw deflate of the bit rows exactly as the file holds them after its header. The deflated bytes are made once and kept (a mask read from a blob keeps
// the blob as it was read), because two runtimes deflate the same bits into different bytes: a re-save must not change the settings package's id.
internal sealed class ClearMask
{
  public const string Magic = "TARNCLR1";
  public const int HeaderLength = 16;
  // The most pixels across a mask may have (the same as Max Map Size's largest).
  public const int MaxSize = 16384;
  // The format of blob 73. The first int32 of the blob; a newer one is kept as it was read (Serialize).
  public const int BlockVersion = 1;
  // How much wider than its pixel a marked pixel's clear area is on each side, in metres [T: Clearing.AreasFor].
  public const float Margin = 0.25f;

  private readonly float mpx;
  private readonly float half;
  private readonly int rawLength;
  private readonly object gate = new();
  // The bit rows (rawLength bytes), once known: from the file at once, from a blob when first asked for (lazily).
  private volatile byte[]? bits;
  // The blob, once known: as read, or made the first time ToBlock is asked.
  private volatile byte[]? block;
  private double? km2;
  private string? decodeError;

  public int Size { get; }
  public float Extent { get; }

  // How many square kilometres the marked pixels come to (Tarn logs this: "11.22 km2 kept free of vegetation"). Needs the bits, so the first ask decodes them.
  public double Km2
  {
    get
    {
      lock (gate)
      {
        km2 ??= Marked * (double)mpx * mpx / 1e6;
        return km2.Value;
      }
    }
  }

  // The size of one pixel in metres [T: Clearing.Km2].
  public float MetresPerPixel => mpx;

  private ClearMask(int size, float extent, byte[]? bits, byte[]? block)
  {
    Size = size;
    Extent = extent;
    mpx = 2f * extent / size;
    half = mpx * 0.5f + Margin;
    rawLength = RawLengthOf(size);
    this.bits = bits;
    this.block = block;
  }

  public static int RawLengthOf(int size) => (int)(((long)size * size + 7) / 8);

  // The smallest a pixel may be, in metres: a mask whose pixel is smaller is refused (a mistyped half-extent). Covers looks at the pixels within a quarter of a metre of a
  // point, so a 16384 px mask of 1 m half-extent (a pixel of 0.1 mm) would scan the whole mask for every plant. 1 cm is far under any real map (Ravenrest's is 5.13 m,
  // a 16384 px mask of the world's 21 km is 1.28 m) and still allows the 255 px / 3 m mask (2.3 cm) the area test is checked on; at 1 cm a lookup scans 50 x 50 pixels.
  public const float MinPixelMetres = 0.01f;
  // The largest half-extent: 10,000 km, far past a Valheim world (65 km).
  public const float MaxExtent = 1e7f;

  private static void CheckShape(long size, float extent)
  {
    if (size <= 0)
      throw new LakeFormatException($"the clearing mask's size is {size}");
    if (size > MaxSize)
      throw new LakeFormatException($"the clearing mask is {size} pixels across, more than the {MaxSize} Better Continents takes");
    if (float.IsNaN(extent) || float.IsInfinity(extent) || extent <= 0f)
      throw new LakeFormatException($"the clearing mask's half-extent is {extent}");
    if (extent > MaxExtent)
      throw new LakeFormatException($"the clearing mask's half-extent is {extent} m, more than the {MaxExtent:0} m Better Continents takes");
    float mpx = 2f * extent / size;
    if (mpx < MinPixelMetres)
      throw new LakeFormatException($"the clearing mask's pixels are {mpx:0.#####} m across ({size} pixels over +-{extent} m), under the {MinPixelMetres * 100:0} cm Better Continents takes: its half-extent looks mistyped (a {size} px mask needs one of at least {MinPixelMetres * size / 2:0.#} m)");
  }

  // ---- Tarn's file ------------------------------------------------------------------------------------------------------------------------------

  // The bytes of a ".clear" file. Anything after the bits is ignored, as Tarn ignores it.
  public static ClearMask FromTarnFile(byte[] file)
  {
    if (file.Length < HeaderLength)
      throw new LakeFormatException($"not a TARNCLR1 file ({file.Length} bytes is shorter than its 16-byte header)");
    for (int i = 0; i < 8; i++)
      if (file[i] != (byte)Magic[i])
        throw new LakeFormatException("not a TARNCLR1 file (its first 8 bytes are not TARNCLR1)");
    int size = BitConverter.ToInt32(file, 8);
    float extent = BitConverter.ToSingle(file, 12);
    CheckShape(size, extent);
    int length = RawLengthOf(size);
    if (file.Length - HeaderLength < length)
      throw new LakeFormatException($"size {size}, extent {extent}, {file.Length - HeaderLength} of {length} bytes of bits");
    var bits = new byte[length];
    Buffer.BlockCopy(file, HeaderLength, bits, 0, length);
    return new ClearMask(size, extent, bits, null);
  }

  public byte[] ToTarnFile()
  {
    var bits = Bits();
    var file = new byte[HeaderLength + rawLength];
    for (int i = 0; i < 8; i++)
      file[i] = (byte)Magic[i];
    BitConverter.GetBytes(Size).CopyTo(file, 8);
    BitConverter.GetBytes(Extent).CopyTo(file, 12);
    Buffer.BlockCopy(bits, 0, file, HeaderLength, rawLength);
    return file;
  }

  // ---- blob 73 ----------------------------------------------------------------------------------------------------------------------------------

  public byte[] ToBlock()
  {
    var made = block;
    if (made != null)
      return made;
    lock (gate)
    {
      if (block != null)
        return block;
      var raw = Bits();
      byte[] packed;
      using (var ms = new MemoryStream(raw.Length / 8 + 64))
      {
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
          deflate.Write(raw, 0, raw.Length);
        packed = ms.ToArray();
      }
      using var output = new MemoryStream(20 + packed.Length);
      using (var w = new BinaryWriter(output, new UTF8Encoding(false), true))
      {
        w.Write(BlockVersion);
        w.Write(Size);
        w.Write(Extent);
        w.Write(rawLength);
        w.Write(packed.Length);
        w.Write(packed);
      }
      return block = output.ToArray();
    }
  }

  // Reads the header and checks the lengths; the deflated bits are not opened until something asks for them (Covers, Km2, ToTarnFile, Verify).
  public static ClearMask FromBlock(byte[] blob)
  {
    if (blob.Length < 4)
      throw new LakeFormatException("the clearing mask's blob is cut short");
    int version = BitConverter.ToInt32(blob, 0);
    if (version > BlockVersion)
      throw new LakeFormatException($"the clearing mask is format {version}, made by a newer Better Continents than this one (it reads up to {BlockVersion})");
    if (version < 1)
      throw new LakeFormatException($"the clearing mask's format number is {version}");
    if (blob.Length < 20)
      throw new LakeFormatException("the clearing mask's blob is cut short");
    int size = BitConverter.ToInt32(blob, 4);
    float extent = BitConverter.ToSingle(blob, 8);
    CheckShape(size, extent);
    int raw = BitConverter.ToInt32(blob, 12);
    int packed = BitConverter.ToInt32(blob, 16);
    if (raw != RawLengthOf(size))
      throw new LakeFormatException($"the clearing mask says {raw} bytes of bits where {size} x {size} pixels take {RawLengthOf(size)}");
    if (packed < 0 || packed != blob.Length - 20)
      throw new LakeFormatException($"the clearing mask says {packed} packed bytes and the blob holds {blob.Length - 20}");
    return new ClearMask(size, extent, null, blob);
  }

  // ---- the bits -----------------------------------------------------------------------------------------------------------------------------------

  // The bit rows, decoded the first time (a mask made from a file has them already). Throws LakeFormatException when the deflated bytes do not open.
  private byte[] Bits()
  {
    var have = bits;
    if (have != null)
      return have;
    lock (gate)
    {
      if (bits != null)
        return bits;
      if (decodeError != null)
        throw new LakeFormatException(decodeError);
      try
      {
        var blob = block!;
        var raw = new byte[rawLength];
        using (var input = new MemoryStream(blob, 20, blob.Length - 20, false))
        using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
        {
          int got = 0;
          while (got < raw.Length)
          {
            int n = inflate.Read(raw, got, raw.Length - got);
            if (n <= 0)
              throw new LakeFormatException($"the clearing mask's bits end after {got} of {raw.Length} bytes");
            got += n;
          }
          if (inflate.ReadByte() >= 0)
            throw new LakeFormatException($"the clearing mask's bits go on past {raw.Length} bytes");
        }
        return bits = raw;
      }
      catch (InvalidDataException e)
      {
        decodeError = "the clearing mask's bits do not open (" + e.Message + ")";
        throw new LakeFormatException(decodeError);
      }
      catch (LakeFormatException e)
      {
        decodeError = e.Message;
        throw;
      }
      catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or OutOfMemoryException)
      {
        decodeError = "the clearing mask's bits do not open (" + e.Message + ")";
        throw new LakeFormatException(decodeError);
      }
    }
  }

  // Opens the bits now and says whether they open: the machine that runs the world calls it when the world loads, so that a damaged mask stops the load
  // there and not in the middle of the first zone it clears.
  public void Verify() => Bits();

  // Whether the bits are decoded (a client that only holds the blob has not).
  public bool Decoded => bits != null;

  // How many pixels are marked.
  public long Marked
  {
    get
    {
      var b = Bits();
      long set = 0;
      long total = (long)Size * Size;
      int full = (int)(total >> 3);
      for (int i = 0; i < full; i++)
        set += PopCount[b[i]];
      int rest = (int)(total & 7);
      if (rest != 0)
        set += PopCount[b[full] & (0xFF << (8 - rest))];
      return set;
    }
  }

  private static readonly byte[] PopCount = MakePopCount();

  private static byte[] MakePopCount()
  {
    var t = new byte[256];
    for (int i = 0; i < 256; i++)
      for (int k = 0; k < 8; k++)
        t[i] += (byte)((i >> k) & 1);
    return t;
  }

  // Whether pixel (row, column) is marked; row 0 is the north edge [T: Clearing.At].
  public bool At(int row, int column)
  {
    long i = (long)row * Size + column;
    return (Bits()[i >> 3] & (0x80 >> (int)(i & 7))) != 0;
  }

  // Where a pixel's centre is, in world metres [T: Clearing.AreasFor].
  public (float X, float Z) CentreOf(int row, int column) => ((column + 0.5f) * mpx - Extent, Extent - (row + 0.5f) * mpx);

  // The pixel a point falls in, or null outside the mask.
  public (int Row, int Column)? PixelAt(double x, double z)
  {
    double c = Math.Floor((x + Extent) / mpx), r = Math.Floor((Extent - z) / mpx);
    if (!(c >= 0 && c < Size && r >= 0 && r < Size))
      return null;
    return ((int)r, (int)c);
  }

  // The test of the InsideClearArea postfix: is the point inside the clear area of a marked pixel. Tarn made one area per marked pixel (centre as
  // CentreOf, half-width mpx / 2 + 0.25 m) and the game's ZoneSystem.InsideClearArea asks, with strict inequalities and in single precision,
  // x > cx - r && x < cx + r && z > cz - r && z < cz + r [G: ZoneSystem.cs:1593]; this asks the same, with the same float arithmetic, of the few pixels
  // that can reach the point (a pixel's square reaches a quarter of a metre past its edge, so a point's own pixel and its neighbours' are the candidates).
  public bool Covers(double x, double z)
  {
    float px = (float)x, pz = (float)z;
    if (float.IsNaN(px) || float.IsNaN(pz))
      return false;
    double e = Extent;
    // cx = (c + 0.5) x mpx - e lies in (px - half, px + half): the columns that can reach; likewise the rows, whose z runs the other way. One more on each
    // side, for the rounding; the exact single-precision test below decides.
    double c0 = Math.Floor((px - half + e) / mpx - 0.5) - 1, c1 = Math.Ceiling((px + half + e) / mpx - 0.5) + 1;
    double r0 = Math.Floor((e - pz - half) / mpx - 0.5) - 1, r1 = Math.Ceiling((e - pz + half) / mpx - 0.5) + 1;
    if (!(c1 >= 0 && c0 <= Size - 1 && r1 >= 0 && r0 <= Size - 1))
      return false;
    int cLow = (int)Math.Max(c0, 0), cHigh = (int)Math.Min(c1, Size - 1);
    int rLow = (int)Math.Max(r0, 0), rHigh = (int)Math.Min(r1, Size - 1);
    var b = Bits();
    float extent = Extent;
    for (int r = rLow; r <= rHigh; r++)
    {
      long row = (long)r * Size;
      float cz = (float)(extent - (float)((r + 0.5f) * mpx));
      if (!(pz > (float)(cz - half) && pz < (float)(cz + half)))
        continue;
      for (int c = cLow; c <= cHigh; c++)
      {
        long i = row + c;
        if ((b[i >> 3] & (0x80 >> (int)(i & 7))) == 0)
          continue;
        float cx = (float)((float)((c + 0.5f) * mpx) - extent);
        if (px > (float)(cx - half) && px < (float)(cx + half))
          return true;
      }
    }
    return false;
  }

  // Whether any marked pixel's clear area touches this rectangle (world metres, x from minX to maxX and z from minZ to maxZ): bc_lake regen counts the
  // generated zones it touches with this.
  public bool Touches(double minX, double minZ, double maxX, double maxZ)
  {
    double e = Extent;
    double c0 = Math.Floor((minX - half + e) / mpx - 0.5) - 1, c1 = Math.Ceiling((maxX + half + e) / mpx - 0.5) + 1;
    double r0 = Math.Floor((e - maxZ - half) / mpx - 0.5) - 1, r1 = Math.Ceiling((e - minZ + half) / mpx - 0.5) + 1;
    if (!(c1 >= 0 && c0 <= Size - 1 && r1 >= 0 && r0 <= Size - 1))
      return false;
    int cLow = (int)Math.Max(c0, 0), cHigh = (int)Math.Min(c1, Size - 1);
    int rLow = (int)Math.Max(r0, 0), rHigh = (int)Math.Min(r1, Size - 1);
    var b = Bits();
    for (int r = rLow; r <= rHigh; r++)
    {
      long row = (long)r * Size;
      double cz = e - (r + 0.5) * mpx;
      if (cz + half <= minZ || cz - half >= maxZ)
        continue;
      for (int c = cLow; c <= cHigh; c++)
      {
        long i = row + c;
        if ((b[i >> 3] & (0x80 >> (int)(i & 7))) == 0)
          continue;
        double cx = (c + 0.5) * mpx - e;
        if (cx + half > minX && cx - half < maxX)
          return true;
      }
    }
    return false;
  }

  // "4096 x 4096 over +-10500 m, 11.22 km2 kept free of vegetation" [T: Clearing.Ensure's log line].
  public string Summary() => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} x {0} over +-{1:0} m, {2:0.00} km2 kept free of vegetation", Size, Extent, Km2);
}

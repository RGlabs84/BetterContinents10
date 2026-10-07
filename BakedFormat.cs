// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace BetterContinents;

// Format 1 of a baked layer (placements.bcp), exactly as section 2 of the 0.10.4 build spec lays it out: the header, the producer,
// the palette, the registry (header flag 8), the zone index, one Deflate block per zone, and a CRC-32. This file is the bytes: the
// codecs for every part of it, the checks a reader makes (spec 2.2), and the numbers that turn a record's columns into a place in
// the world. BakedLayer.cs holds what the bytes mean (the snapshot, the edits that write a new layer).

// What is wrong with a layer, and where. A reader stops at the first problem and names it (spec 2.2); Where is the zone and record,
// or the palette entry, when there is one, and empty when it is the whole file.
internal sealed class BakedFormatException : Exception
{
  public string What { get; }
  public string Where { get; }

  public BakedFormatException(string what, string where = "") : base(where.Length == 0 ? what : where + ": " + what)
  {
    What = what;
    Where = where;
  }
}

// What a palette entry's records are to the game (spec 2.1).
public enum BakedRole : byte
{
  // Drawn by every client, solid as its collision says, not an object.
  Static = 0,
  // A real piece the server seeds (doors, stations); BC draws nothing for it.
  Live = 1,
  // A lit, stripped copy near the player; solid as its collision says.
  Copy = 2,
  // Drawn like a Static; a local chair near the player.
  Seat = 3,
}

internal enum BakedCollision : byte
{
  None = 0,
  // The palette entry's boxes, in the frame of its first candidate (R1).
  Boxes = 1,
  // A 0.6 x 3 m box at the foot.
  Trunk = 2,
  // The colliders of the prefab the client draws.
  Prefab = 3,
}

[Flags]
internal enum PaletteFlags : byte
{
  None = 0,
  UniformScale = 1,
  NoShadows = 2,
  Protect = 4,
  LOD0Only = 8,
  NoCopyLight = 16,
  // Every bit this version knows.
  All = 31,
}

[Flags]
internal enum RecordFlags : byte
{
  None = 0,
  FullRotation = 1,
  Scale = 2,
  Id = 4,
  // BC: the in-game bake that owns the record, and its value set (a registry is needed).
  Source = 8,
  // BC: RandomMaterialValues' seed.
  Seed = 16,
  All = 31,
}

[Flags]
internal enum ZoneFlags : byte
{
  None = 0,
  Ground = 1,
  Paint = 2,
  ClearMask = 4,
  // Kept, no effect (U4: BC does not protect footprints).
  ProtectFootprints = 8,
  // No vegetation anywhere in the zone.
  NoVegetation = 16,
  All = 31,
}

[Flags]
internal enum HeaderFlags : ushort
{
  None = 0,
  Ground = 1,
  Paint = 2,
  ClearMask = 4,
  // BC: a registry follows the palette.
  Registry = 8,
  All = 15,
}

internal enum TagType : byte
{
  Bool = 0,
  Int = 1,
  Float = 2,
  String = 3,
}

internal static class BakedFormat
{
  public const int FormatVersion = 1;
  public const int HeaderBytes = 32;
  public const int ZoneRowBytes = 28;
  // The trailer, and the smallest file there is: a header and a CRC.
  public const int MinimumBytes = 36;
  public const int MaxPalette = 65535;
  public const int MaxInflated = 16 * 1024 * 1024;
  public const int MaxLayer = 256 * 1024 * 1024;
  public const int FirstZone = -1024;
  public const int LastZone = 1023;
  // A zone's ground is 65 x 65 vertices 1 m apart, its paint 64 x 64 cells of 1 m, its clear mask 16 x 16 cells of 4 m (32 bytes).
  public const int GroundSide = 65;
  public const int GroundVertices = GroundSide * GroundSide;
  public const int PaintCells = 64 * 64;
  public const int MaskBytes = 32;
  // RandomMaterialValues' seed is Random.Range(0, 12345).
  public const int MaxSeed = 12344;
  // The registry keeps at most this many operations (spec 2.4).
  public const int MaxOperations = 1000;
  public const int RegistryFormat = 1;
  public const string Magic = "BCPL";
  // The layer is a BetterContinents file part, never an assembly of a name: a Better Continents version that writes it.
  public const string ProducerPrefix = "Better Continents ";

  // A record's x and z are a u16 of 1/1024 m from its zone's south-west corner.
  public const double UnitsPerMetre = 1024.0;

  static BakedFormat()
  {
    if (!BitConverter.IsLittleEndian)
      throw new PlatformNotSupportedException("baked layers are little-endian");
  }

  // ------------------------------------------------------------------------------------------------ CRC-32

  private static readonly uint[] CrcTable = MakeCrcTable();

  private static uint[] MakeCrcTable()
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

  // The zlib / IEEE CRC-32 (reflected polynomial 0xEDB88320) of data[offset .. offset + count).
  public static uint Crc32(byte[] data, int offset, int count)
  {
    uint c = 0xFFFFFFFFu;
    for (int i = offset, end = offset + count; i < end; i++)
      c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFFu;
  }

  // ------------------------------------------------------------------------------------------------ Deflate

  // Raw Deflate (no zlib header), optimal level: what every zone block and the registry are. Mono in the game and .NET 8 in the
  // tests may produce different bytes for the same input (spec 2.6).
  public static byte[] Deflate(byte[] data, int length)
  {
    using var output = new MemoryStream(Math.Max(64, length / 3));
    using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
      deflate.Write(data, 0, length);
    return output.ToArray();
  }

  // Inflates source[offset .. offset + count) into buffer (which grows as it must, up to limit bytes) and returns how many bytes it
  // holds. Passing the limit is an error, so that a block cannot be made to take the machine's memory (spec 2.2).
  public static int Inflate(byte[] source, int offset, int count, ref byte[] buffer, int limit, string where)
  {
    int total = 0;
    try
    {
      using var input = new MemoryStream(source, offset, count, false);
      using var inflate = new DeflateStream(input, CompressionMode.Decompress);
      while (true)
      {
        if (total == buffer.Length)
        {
          if (total > limit)
            throw new BakedFormatException($"inflating it passes {limit / (1024 * 1024)} MB", where);
          Array.Resize(ref buffer, (int)Math.Min((long)Math.Max(buffer.Length * 2, 4096), (long)limit + 1));
        }
        int n = inflate.Read(buffer, total, buffer.Length - total);
        if (n <= 0)
          break;
        total += n;
        if (total > limit)
          throw new BakedFormatException($"inflating it passes {limit / (1024 * 1024)} MB", where);
      }
    }
    catch (InvalidDataException e)
    {
      throw new BakedFormatException("it cannot be inflated (" + e.Message + ")", where);
    }
    return total;
  }

  // ------------------------------------------------------------------------------------------------ places and angles

  // The zone a point is in (the game's ZoneSystem.GetZone, in double precision): zone (zx, zz) is centred on (zx * 64, zz * 64).
  public static int ZoneOf(double world) => (int)Math.Floor((world + 32.0) / 64.0);

  // A zone's south-west corner on an axis.
  public static double ZoneOrigin(int zone) => zone * 64.0 - 32.0;

  private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;
  private static double Clamp(double value, double low, double high) => value < low ? low : value > high ? high : value;

  // x or z in a zone as a u16 of 1/1024 m, rounded and clamped (spec 2.1).
  public static ushort Quantize(double world, int zone) => (ushort)Clamp(Math.Round((world - ZoneOrigin(zone)) * UnitsPerMetre), 0.0, 65535.0);

  // y in mm.
  public static int QuantizeY(double world) => (int)Clamp(Math.Round(world * 1000.0), int.MinValue, int.MaxValue);

  // A yaw in degrees as one of 65,536 steps of a turn.
  public static ushort QuantizeYaw(double degrees)
  {
    long steps = (long)Math.Round(degrees / 360.0 * 65536.0);
    return (ushort)(((steps % 65536) + 65536) % 65536);
  }

  public static double YawDegrees(ushort yaw) => yaw * 360.0 / 65536.0;

  // Unity's Quaternion.Euler(0, yaw, 0), without the call into the engine.
  public static Quaternion YawRotation(ushort yaw)
  {
    double half = yaw * Math.PI / 65536.0;
    return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
  }

  // The yaw of a rotation: the heading of its forward vector, which is what Quaternion.Euler's y gives for an upright object.
  public static double YawOf(Quaternion q)
  {
    double x = q.x, y = q.y, z = q.z, w = q.w;
    double fx = 2 * (x * z + w * y), fz = 1 - 2 * (x * x + y * y);
    return Math.Atan2(fx, fz) * 180.0 / Math.PI;
  }

  // How far a rotation leans from upright, in degrees (the angle of its up vector from the world's).
  public static double TiltOf(Quaternion q)
  {
    double x = q.x, z = q.z, w = q.w, y = q.y;
    double len = Math.Sqrt(x * x + y * y + z * z + w * w);
    if (len < 1e-12)
      return 0;
    double upY = 1 - 2 * ((x * x + z * z) / (len * len));
    return Math.Acos(Clamp(upY, -1.0, 1.0)) * 180.0 / Math.PI;
  }

  // ------------------------------------------------------------------------------------------------ the look of a record (spec 2.5)

  // FNV-1a 32 over a record's decoded anchor point in world mm, computed in double precision, as three little-endian i32 (x, y and z at
  // 1/1000 m). Every machine gets the same value, so a record derives the same look wherever it is drawn.
  public static uint LookHash(double x, double y, double z)
  {
    uint h = 2166136261u;
    h = Fnv(h, (int)Math.Round(x * 1000.0));
    h = Fnv(h, (int)Math.Round(y * 1000.0));
    h = Fnv(h, (int)Math.Round(z * 1000.0));
    return h;
  }

  private static uint Fnv(uint h, int value)
  {
    unchecked
    {
      uint v = (uint)value;
      for (int i = 0; i < 4; i++)
      {
        h ^= (v >> (8 * i)) & 0xFFu;
        h *= 16777619u;
      }
      return h;
    }
  }

  // The seed a record without a stored one gets: h mod 12,345.
  public static int DerivedSeed(uint hash) => (int)(hash % 12345u);

  // u = ((h xor (slot * 0x9E3779B9)) mod 65,536) / 65,536, in u32 arithmetic: the variant of MaterialVariation slot `slot` is the first
  // whose running weight passes u x the total weight.
  public static double VariantUnit(uint hash, int slot)
  {
    unchecked
    {
      uint mixed = hash ^ ((uint)slot * 0x9E3779B9u);
      return (mixed % 65536u) / 65536.0;
    }
  }

  // ------------------------------------------------------------------------------------------------ the keys of a record's ZDO (spec 6.4)

  public const string KeySource = "bc_bake_src";
  public const string KeyId = "bc_bake_id";
  public const string KeyRevision = "bc_bake_rev";
  public const string KeyProtect = "bc_protect";
  public const string KeyMatVar = "MatVar";
  public const string KeySeed = "RandMatSeed";

  // ------------------------------------------------------------------------------------------------ strings

  private static string Describe(string what, int position) => $"{what} at byte {position}";

  // A string of the format: a u8 length and that many ASCII bytes. Names outside ASCII or past 255 bytes cannot be written.
  public static bool IsWritable(string text) => text != null && text.Length <= 255 && IsAscii(text);

  public static bool IsAscii(string text)
  {
    foreach (char c in text)
      if (c > 127)
        return false;
    return true;
  }

  // ------------------------------------------------------------------------------------------------ a layer's own numbers

  // The (source, id) of a Live record as one number: source 0 is the compiler's file.
  public static ulong LiveKey(int source, uint id) => ((ulong)(uint)source << 32) | id;
}

// A cursor over a range of bytes. It stops, saying where, when the range ends before what is read.
internal sealed class BakedReader
{
  private readonly byte[] data;
  public int Pos;
  public readonly int End;
  // What is being read, for the message of a failure ("palette entry 5").
  public string Where = "";

  public BakedReader(byte[] data, int start, int end)
  {
    this.data = data;
    Pos = start;
    End = end;
  }

  public int Remaining => End - Pos;

  // The bytes being read (a nested Deflate stream is inflated from them).
  public byte[] Source => data;

  private void Need(int count)
  {
    if (count < 0 || count > End - Pos)
      throw new BakedFormatException($"it runs past the end of the data (byte {Pos} of {End})", Where);
  }

  // The count is checked against what is left before anything is allocated for it.
  public void Skip(int count)
  {
    Need(count);
    Pos += count;
  }

  public byte U8()
  {
    Need(1);
    return data[Pos++];
  }

  public ushort U16()
  {
    Need(2);
    ushort v = (ushort)(data[Pos] | (data[Pos + 1] << 8));
    Pos += 2;
    return v;
  }

  public short I16() => unchecked((short)U16());

  public uint U32()
  {
    Need(4);
    uint v = (uint)(data[Pos] | (data[Pos + 1] << 8) | (data[Pos + 2] << 16) | (data[Pos + 3] << 24));
    Pos += 4;
    return v;
  }

  public int I32() => unchecked((int)U32());

  public ulong U64()
  {
    ulong low = U32();
    ulong high = U32();
    return low | (high << 32);
  }

  public long I64() => unchecked((long)U64());

  public float F32()
  {
    Need(4);
    float v = BitConverter.ToSingle(data, Pos);
    Pos += 4;
    return v;
  }

  // A u8 length and that many ASCII bytes.
  public string Str()
  {
    int n = U8();
    Need(n);
    for (int i = 0; i < n; i++)
      if (data[Pos + i] > 127)
        throw new BakedFormatException($"a string holds a byte outside ASCII ({data[Pos + i]}) at byte {Pos + i}", Where);
    string s = Encoding.ASCII.GetString(data, Pos, n);
    Pos += n;
    return s;
  }

  // A u16 length and that many UTF-8 bytes.
  public string Str16()
  {
    int n = U16();
    Need(n);
    string s = Encoding.UTF8.GetString(data, Pos, n);
    Pos += n;
    return s;
  }

  public byte[] Bytes(int count)
  {
    Need(count);
    var copy = new byte[count];
    Buffer.BlockCopy(data, Pos, copy, 0, count);
    Pos += count;
    return copy;
  }

  public void BytesInto(byte[] target, int count)
  {
    Need(count);
    Buffer.BlockCopy(data, Pos, target, 0, count);
    Pos += count;
  }
}

// A growing buffer the format is written into.
internal sealed class BakedWriter
{
  private byte[] buffer;
  private int length;

  public BakedWriter(int capacity = 1024)
  {
    buffer = new byte[Math.Max(16, capacity)];
  }

  public int Length => length;
  public byte[] Buffer_ => buffer;

  private void Room(int more)
  {
    if (length + more <= buffer.Length)
      return;
    long size = Math.Max((long)buffer.Length * 2, (long)length + more);
    if (size > int.MaxValue)
      throw new BakedFormatException("the data is over 2 GB");
    Array.Resize(ref buffer, (int)size);
  }

  public void U8(int v)
  {
    Room(1);
    buffer[length++] = (byte)v;
  }

  public void U16(int v)
  {
    Room(2);
    buffer[length++] = (byte)v;
    buffer[length++] = (byte)(v >> 8);
  }

  public void I16(int v) => U16(v & 0xFFFF);

  public void U32(uint v)
  {
    Room(4);
    buffer[length++] = (byte)v;
    buffer[length++] = (byte)(v >> 8);
    buffer[length++] = (byte)(v >> 16);
    buffer[length++] = (byte)(v >> 24);
  }

  public void I32(int v) => U32(unchecked((uint)v));

  public void U64(ulong v)
  {
    U32((uint)v);
    U32((uint)(v >> 32));
  }

  public void I64(long v) => U64(unchecked((ulong)v));

  public void F32(float v)
  {
    Room(4);
    var bytes = BitConverter.GetBytes(v);
    System.Buffer.BlockCopy(bytes, 0, buffer, length, 4);
    length += 4;
  }

  public void Bytes(byte[] source, int offset, int count)
  {
    Room(count);
    System.Buffer.BlockCopy(source, offset, buffer, length, count);
    length += count;
  }

  public void Bytes(byte[] source) => Bytes(source, 0, source.Length);

  // A u8 length and the ASCII bytes. A name the format cannot hold is an error here, not a damaged name in the file.
  public void Str(string text)
  {
    if (text == null || text.Length > 255)
      throw new BakedFormatException($"a name over 255 characters cannot be written ('{Shorten(text)}')");
    foreach (char c in text)
      if (c > 127)
        throw new BakedFormatException($"'{Shorten(text)}' has a character outside ASCII, which the format cannot hold");
    U8(text.Length);
    Room(text.Length);
    for (int i = 0; i < text.Length; i++)
      buffer[length++] = (byte)text[i];
  }

  public void Str16(string text)
  {
    var bytes = Encoding.UTF8.GetBytes(text ?? "");
    if (bytes.Length > 65535)
      throw new BakedFormatException("a text over 65,535 bytes cannot be written");
    U16(bytes.Length);
    Bytes(bytes);
  }

  private static string Shorten(string? text) => text == null ? "" : text.Length <= 40 ? text : text.Substring(0, 40) + "...";

  // Overwrites four bytes already written.
  public void PatchU32(int at, uint v)
  {
    buffer[at] = (byte)v;
    buffer[at + 1] = (byte)(v >> 8);
    buffer[at + 2] = (byte)(v >> 16);
    buffer[at + 3] = (byte)(v >> 24);
  }

  public byte[] ToArray()
  {
    var copy = new byte[length];
    System.Buffer.BlockCopy(buffer, 0, copy, 0, length);
    return copy;
  }
}

// ======================================================================================================== the data of a layer

// A zone as the game numbers it: the zone (x, z) is centred on (x * 64, z * 64) m. A layer holds zones -1024 to 1023 on each axis.
internal readonly struct ZoneKey : IEquatable<ZoneKey>, IComparable<ZoneKey>
{
  public readonly int X;
  public readonly int Z;

  public ZoneKey(int x, int z)
  {
    X = x;
    Z = z;
  }

  public static ZoneKey Of(Vector2s zone) => new(zone.x, zone.y);

  // The zone a point is in (the game's ZoneSystem.GetZone, in double precision).
  public static ZoneKey OfPoint(double x, double z) => new(BakedFormat.ZoneOf(x), BakedFormat.ZoneOf(z));

  public static ZoneKey OfPoint(Vector3 point) => OfPoint(point.x, point.z);

  public Vector2s ToVector2s() => new(X, Z);

  public bool InRange => X >= BakedFormat.FirstZone && X <= BakedFormat.LastZone && Z >= BakedFormat.FirstZone && Z <= BakedFormat.LastZone;

  // The south-west corner of the zone, on each axis.
  public double OriginX => BakedFormat.ZoneOrigin(X);
  public double OriginZ => BakedFormat.ZoneOrigin(Z);

  // The zone's centre, at y = 0.
  public Vector3 Centre => new(X * 64f, 0f, Z * 64f);

  // The index's order: by z, then by x.
  public int CompareTo(ZoneKey other) => Z != other.Z ? Z.CompareTo(other.Z) : X.CompareTo(other.X);

  public bool Equals(ZoneKey other) => X == other.X && Z == other.Z;
  public override bool Equals(object? obj) => obj is ZoneKey other && Equals(other);
  public override int GetHashCode() => (Z << 16) ^ (X & 0xFFFF);
  public override string ToString() => X + "," + Z;
  public static bool operator ==(ZoneKey a, ZoneKey b) => a.Equals(b);
  public static bool operator !=(ZoneKey a, ZoneKey b) => !a.Equals(b);
}

// A record's full rotation in format 1's "smallest three": the index of the largest component of the unit quaternion (0 x, 1 y, 2 z,
// 3 w; the quaternion is negated first when that component is negative), and the other three in index order, times 32767.
internal readonly struct PackedRotation : IEquatable<PackedRotation>
{
  public readonly byte Largest;
  public readonly short A;
  public readonly short B;
  public readonly short C;

  public PackedRotation(byte largest, short a, short b, short c)
  {
    Largest = largest;
    A = a;
    B = b;
    C = c;
  }

  public static PackedRotation Of(Quaternion q)
  {
    double x = q.x, y = q.y, z = q.z, w = q.w;
    double len = Math.Sqrt(x * x + y * y + z * z + w * w);
    if (len < 1e-12)
    {
      x = y = z = 0;
      w = 1;
      len = 1;
    }
    var c = new[] { x / len, y / len, z / len, w / len };
    int largest = 0;
    for (int i = 1; i < 4; i++)
      if (Math.Abs(c[i]) > Math.Abs(c[largest]))
        largest = i;
    double sign = c[largest] < 0 ? -1.0 : 1.0;
    var rest = new short[3];
    for (int i = 0, n = 0; i < 4; i++)
    {
      if (i == largest)
        continue;
      double v = Math.Round(c[i] * sign * 32767.0);
      rest[n++] = (short)(v < -32767.0 ? -32767 : v > 32767.0 ? 32767 : (int)v);
    }
    return new PackedRotation((byte)largest, rest[0], rest[1], rest[2]);
  }

  public Quaternion ToQuaternion()
  {
    double a = A / 32767.0, b = B / 32767.0, c = C / 32767.0;
    double big = Math.Sqrt(Math.Max(0.0, 1.0 - a * a - b * b - c * c));
    switch (Largest)
    {
      case 0: return new Quaternion((float)big, (float)a, (float)b, (float)c);
      case 1: return new Quaternion((float)a, (float)big, (float)b, (float)c);
      case 2: return new Quaternion((float)a, (float)b, (float)big, (float)c);
      default: return new Quaternion((float)a, (float)b, (float)c, (float)big);
    }
  }

  public bool Equals(PackedRotation other) => Largest == other.Largest && A == other.A && B == other.B && C == other.C;
  public override bool Equals(object? obj) => obj is PackedRotation other && Equals(other);
  public override int GetHashCode() => (Largest << 24) ^ (A << 16) ^ (B << 8) ^ C;
}

// ---- the palette

// A prefab a palette entry may draw, and where the entry's point sits in it: the anchor, in mm, in the candidate's own frame.
internal readonly struct Candidate : IEquatable<Candidate>
{
  public readonly string Name;
  public readonly short AnchorX;
  public readonly short AnchorY;
  public readonly short AnchorZ;

  public Candidate(string name, short anchorX, short anchorY, short anchorZ)
  {
    Name = name;
    AnchorX = anchorX;
    AnchorY = anchorY;
    AnchorZ = anchorZ;
  }

  public Candidate(string name, Vector3 anchor) : this(name, Mm(anchor.x), Mm(anchor.y), Mm(anchor.z))
  {
  }

  private static short Mm(float metres) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(metres * 1000.0)));

  public Vector3 Anchor => new(AnchorX / 1000f, AnchorY / 1000f, AnchorZ / 1000f);

  public bool Equals(Candidate other) => Name == other.Name && AnchorX == other.AnchorX && AnchorY == other.AnchorY && AnchorZ == other.AnchorZ;
  public override bool Equals(object? obj) => obj is Candidate other && Equals(other);
  public override int GetHashCode() => (Name ?? "").GetHashCode() ^ (AnchorX << 16) ^ (AnchorY << 8) ^ AnchorZ;
}

// A collision box, in mm: centre and size, in the frame of the entry's first candidate (R1), positioned by that candidate's anchor.
internal readonly struct Box : IEquatable<Box>
{
  public readonly short CentreX, CentreY, CentreZ;
  public readonly short SizeX, SizeY, SizeZ;

  public Box(short centreX, short centreY, short centreZ, short sizeX, short sizeY, short sizeZ)
  {
    CentreX = centreX;
    CentreY = centreY;
    CentreZ = centreZ;
    SizeX = sizeX;
    SizeY = sizeY;
    SizeZ = sizeZ;
  }

  public Vector3 Centre => new(CentreX / 1000f, CentreY / 1000f, CentreZ / 1000f);
  public Vector3 Size => new(SizeX / 1000f, SizeY / 1000f, SizeZ / 1000f);

  public bool Equals(Box other) =>
    CentreX == other.CentreX && CentreY == other.CentreY && CentreZ == other.CentreZ && SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ;
  public override bool Equals(object? obj) => obj is Box other && Equals(other);
  public override int GetHashCode() => (CentreX << 16) ^ (CentreY << 8) ^ CentreZ ^ (SizeX << 12) ^ (SizeY << 4) ^ SizeZ;
}

// A ZDO value every piece of an entry has when it is a real piece (spec 2.3): a bool (stored as 0 or 1), an int, a float or a string.
internal readonly struct Tag : IEquatable<Tag>
{
  public readonly string Key;
  public readonly TagType Type;
  // The bool (0 or 1) and the int.
  public readonly int Number;
  public readonly float Float;
  public readonly string Text;

  private Tag(string key, TagType type, int number, float f, string text)
  {
    Key = key;
    Type = type;
    Number = number;
    Float = f;
    Text = text;
  }

  public static Tag OfBool(string key, bool value) => new(key, TagType.Bool, value ? 1 : 0, 0f, "");
  public static Tag OfInt(string key, int value) => new(key, TagType.Int, value, 0f, "");
  public static Tag OfFloat(string key, float value) => new(key, TagType.Float, 0, value, "");
  public static Tag OfString(string key, string value) => new(key, TagType.String, 0, 0f, value ?? "");

  public bool Bool => Number != 0;

  public bool Equals(Tag other) =>
    Key == other.Key && Type == other.Type && Number == other.Number && BitConverter.ToInt32(BitConverter.GetBytes(Float), 0) == BitConverter.ToInt32(BitConverter.GetBytes(other.Float), 0) && Text == other.Text;
  public override bool Equals(object? obj) => obj is Tag other && Equals(other);
  public override int GetHashCode() => (Key ?? "").GetHashCode() ^ ((int)Type << 28) ^ Number ^ (Text ?? "").GetHashCode();
  public override string ToString() => Type switch
  {
    TagType.Bool => Key + "=" + (Bool ? "true" : "false"),
    TagType.Int => Key + "=" + Number,
    TagType.Float => Key + "=" + Float.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
    _ => Key + "=" + Text,
  };
}

// One kind of piece in the layer: what to draw, how it is solid, what the server and the clients do with it (spec 2.1). Immutable.
internal sealed class PaletteEntry : IEquatable<PaletteEntry>
{
  // 1 to 8 prefabs; the first the game has is drawn, and an entry's boxes are in the first one's frame.
  public Candidate[] Candidates { get; }
  public BakedRole Role { get; }
  public BakedCollision Collision { get; }
  // 0 is the game's `piece` layer, else a Unity layer index.
  public byte Layer { get; }
  public PaletteFlags Flags { get; }
  // Only with Collision = Boxes.
  public Box[] Boxes { get; }
  // "" for no tint; else the colour (r, g, b times 1000) and the filter on material names.
  public string Tint { get; }
  public ushort TintR { get; }
  public ushort TintG { get; }
  public ushort TintB { get; }
  public string TintFilter { get; }
  public Tag[] Tags { get; }

  public PaletteEntry(Candidate[] candidates, BakedRole role, BakedCollision collision, byte layer = 0, PaletteFlags flags = PaletteFlags.None,
    Box[]? boxes = null, string tint = "", ushort tintR = 1000, ushort tintG = 1000, ushort tintB = 1000, string tintFilter = "", Tag[]? tags = null)
  {
    Candidates = (Candidate[])candidates.Clone();
    Role = role;
    Collision = collision;
    Layer = layer;
    Flags = flags;
    // Only an entry with collision Boxes has boxes, and only a tinted one has a colour and a filter: the file holds nothing else, so
    // an entry made here equals the same entry read back.
    Boxes = boxes == null || collision != BakedCollision.Boxes ? Array.Empty<Box>() : (Box[])boxes.Clone();
    Tint = tint ?? "";
    bool tinted = Tint.Length > 0;
    TintR = tinted ? tintR : (ushort)1000;
    TintG = tinted ? tintG : (ushort)1000;
    TintB = tinted ? tintB : (ushort)1000;
    TintFilter = tinted ? tintFilter ?? "" : "";
    Tags = tags == null ? Array.Empty<Tag>() : (Tag[])tags.Clone();
  }

  // The prefab that names the entry (its first candidate).
  public string Name => Candidates[0].Name;
  public bool Protected => (Flags & PaletteFlags.Protect) != 0;
  public bool HasTint => Tint.Length > 0;

  public bool TryGetTag(string key, out Tag tag)
  {
    foreach (var t in Tags)
      if (t.Key == key)
      {
        tag = t;
        return true;
      }
    tag = default;
    return false;
  }

  public bool Equals(PaletteEntry? other)
  {
    if (other == null)
      return false;
    if (ReferenceEquals(this, other))
      return true;
    if (Role != other.Role || Collision != other.Collision || Layer != other.Layer || Flags != other.Flags || Tint != other.Tint
        || TintFilter != other.TintFilter || Candidates.Length != other.Candidates.Length || Boxes.Length != other.Boxes.Length || Tags.Length != other.Tags.Length)
      return false;
    if (HasTint && (TintR != other.TintR || TintG != other.TintG || TintB != other.TintB))
      return false;
    for (int i = 0; i < Candidates.Length; i++)
      if (!Candidates[i].Equals(other.Candidates[i]))
        return false;
    for (int i = 0; i < Boxes.Length; i++)
      if (!Boxes[i].Equals(other.Boxes[i]))
        return false;
    for (int i = 0; i < Tags.Length; i++)
      if (!Tags[i].Equals(other.Tags[i]))
        return false;
    return true;
  }

  public override bool Equals(object? obj) => Equals(obj as PaletteEntry);

  public override int GetHashCode()
  {
    int h = Candidates[0].GetHashCode() ^ ((int)Role << 28) ^ ((int)Collision << 24) ^ (Layer << 16) ^ (int)Flags ^ Candidates.Length;
    foreach (var tag in Tags)
      h = h * 31 + tag.GetHashCode();
    return h;
  }

  public override string ToString() => $"{Name} ({Role}, {Collision})";
}

// ---- the registry (spec 2.4)

internal enum ZdoValueType : byte
{
  Float = 0,
  Vec3 = 1,
  Quat = 2,
  Int = 3,
  Long = 4,
  String = 5,
  Bytes = 6,
}

// One ZDO value a baked piece had, which BC gives back when the record becomes a real piece again: the key's stable hash, its type
// and the value.
internal readonly struct ZdoValue : IEquatable<ZdoValue>
{
  public readonly int Key;
  public readonly ZdoValueType Type;
  // Float: A. Vec3: A, B, C. Quat: A, B, C, D (x, y, z, w).
  public readonly float A, B, C, D;
  // Int and Long.
  public readonly long Number;
  public readonly string Text;
  public readonly byte[] Data;

  private ZdoValue(int key, ZdoValueType type, float a, float b, float c, float d, long number, string text, byte[] data)
  {
    Key = key;
    Type = type;
    A = a;
    B = b;
    C = c;
    D = d;
    Number = number;
    Text = text;
    Data = data;
  }

  public static ZdoValue OfFloat(int key, float v) => new(key, ZdoValueType.Float, v, 0, 0, 0, 0, "", Array.Empty<byte>());
  public static ZdoValue OfVec3(int key, float x, float y, float z) => new(key, ZdoValueType.Vec3, x, y, z, 0, 0, "", Array.Empty<byte>());
  public static ZdoValue OfQuat(int key, float x, float y, float z, float w) => new(key, ZdoValueType.Quat, x, y, z, w, 0, "", Array.Empty<byte>());
  public static ZdoValue OfInt(int key, int v) => new(key, ZdoValueType.Int, 0, 0, 0, 0, v, "", Array.Empty<byte>());
  public static ZdoValue OfLong(int key, long v) => new(key, ZdoValueType.Long, 0, 0, 0, 0, v, "", Array.Empty<byte>());
  public static ZdoValue OfString(int key, string v) => new(key, ZdoValueType.String, 0, 0, 0, 0, 0, v ?? "", Array.Empty<byte>());
  public static ZdoValue OfBytes(int key, byte[] v) => new(key, ZdoValueType.Bytes, 0, 0, 0, 0, 0, "", v ?? Array.Empty<byte>());

  private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

  public bool Equals(ZdoValue other)
  {
    if (Key != other.Key || Type != other.Type || Number != other.Number || Text != other.Text || Bits(A) != Bits(other.A) || Bits(B) != Bits(other.B)
        || Bits(C) != Bits(other.C) || Bits(D) != Bits(other.D) || Data.Length != other.Data.Length)
      return false;
    for (int i = 0; i < Data.Length; i++)
      if (Data[i] != other.Data[i])
        return false;
    return true;
  }

  public override bool Equals(object? obj) => obj is ZdoValue other && Equals(other);
  public override int GetHashCode() => Key ^ ((int)Type << 28) ^ (int)Number ^ Bits(A) ^ Text.GetHashCode() ^ Data.Length;
}

// The ZDO values a group of baked pieces share (one builder's pieces at full health are one set). Immutable.
internal sealed class ValueSet : IEquatable<ValueSet>
{
  public ZdoValue[] Values { get; }

  public ValueSet(ZdoValue[] values)
  {
    Values = (ZdoValue[])values.Clone();
  }

  public bool Equals(ValueSet? other)
  {
    if (other == null || Values.Length != other.Values.Length)
      return false;
    for (int i = 0; i < Values.Length; i++)
      if (!Values[i].Equals(other.Values[i]))
        return false;
    return true;
  }

  public override bool Equals(object? obj) => Equals(obj as ValueSet);

  public override int GetHashCode()
  {
    int h = Values.Length;
    foreach (var v in Values)
      h = h * 31 + v.GetHashCode();
    return h;
  }
}

public enum OperationKind : byte
{
  // `bc_bake area`.
  BakeArea = 1,
  // `bc_bake box`.
  BakeBox = 2,
  Unbake = 3,
  // `bc_bake load`: a compiler's file.
  Load = 4,
  Drop = 5,
}

public enum OperationState : byte
{
  InLayer = 1,
  Undone = 2,
}

// One operation of the registry: what an admin did to the layer. Immutable; a bake's number is also its records' source.
internal sealed class OperationInfo
{
  public ushort Number { get; }
  public OperationKind Kind { get; }
  public OperationState State { get; }
  // Unix seconds, UTC.
  public long Time { get; }
  // The character's name, or "server console".
  public string Who { get; }
  public float X1 { get; }
  public float Z1 { get; }
  public float X2 { get; }
  public float Z2 { get; }
  // 0 for a box, a load or a drop.
  public float Radius { get; }
  public uint Added { get; }
  public uint Removed { get; }
  public uint Adopted { get; }
  // The Better Continents version that made it.
  public string Version { get; }
  // A bake's value sets, which its records point to; empty for the others.
  public ValueSet[] ValueSets { get; }

  public bool IsBake => Kind == OperationKind.BakeArea || Kind == OperationKind.BakeBox;

  public OperationInfo(ushort number, OperationKind kind, OperationState state, long time, string who, float x1, float z1, float x2, float z2, float radius,
    uint added, uint removed, uint adopted, string version, ValueSet[]? valueSets = null)
  {
    Number = number;
    Kind = kind;
    State = state;
    Time = time;
    Who = who ?? "";
    X1 = x1;
    Z1 = z1;
    X2 = x2;
    Z2 = z2;
    Radius = radius;
    Added = added;
    Removed = removed;
    Adopted = adopted;
    Version = version ?? "";
    ValueSets = valueSets == null ? Array.Empty<ValueSet>() : (ValueSet[])valueSets.Clone();
  }

  public OperationInfo WithState(OperationState state) =>
    new(Number, Kind, state, Time, Who, X1, Z1, X2, Z2, Radius, Added, Removed, Adopted, Version, ValueSets);

  public OperationInfo WithValueSets(ValueSet[] valueSets) =>
    new(Number, Kind, State, Time, Who, X1, Z1, X2, Z2, Radius, Added, Removed, Adopted, Version, valueSets);

  public OperationInfo WithCounts(uint added, uint removed, uint adopted) =>
    new(Number, Kind, State, Time, Who, X1, Z1, X2, Z2, Radius, added, removed, adopted, Version, ValueSets);
}

// The in-game operations of a layer, and the number the next one takes (operations share one counter; a number is never reused in a
// world). A layer without header flag 8 has no registry: Empty stands in for it. Immutable.
internal sealed class Registry
{
  public static readonly Registry Empty = new(1, Array.Empty<OperationInfo>());

  public ushort NextOperation { get; }
  public IReadOnlyList<OperationInfo> Operations { get; }

  public Registry(ushort nextOperation, OperationInfo[] operations)
  {
    NextOperation = nextOperation;
    Operations = operations;
  }

  public bool TryGet(int number, out OperationInfo operation)
  {
    foreach (var op in Operations)
      if (op.Number == number)
      {
        operation = op;
        return true;
      }
    operation = null!;
    return false;
  }

  // Whether a record may name this bake and value set: the registry lists a bake of that number, and the value set is one of its.
  public bool Knows(int source, int valueSet) =>
    TryGet(source, out var op) && op.IsBake && valueSet >= 0 && valueSet < op.ValueSets.Length;
}

// ---- zones

// A zone's row of the index, and what the snapshot keeps of it: its clear mask (32 bytes, 16 x 16 cells of 4 m from the zone's
// south-west corner, row 0 south; cell (row, col) is bit row * 16 + col, in byte bit >> 3, value 1 << (bit & 7); a set bit grows no
// vegetation), null without zone flag 4.
internal readonly struct ZoneRow
{
  public readonly ZoneKey Key;
  // Its place in Zones.
  public readonly int Index;
  // The zone's block: where it starts in the file, and how long it is.
  public readonly int Offset;
  public readonly int Length;
  public readonly int Placements;
  // The records whose entry has role Live.
  public readonly int Live;
  public readonly ZoneFlags Flags;
  // The records' y and the pieces' heights, in m: culling bounds (R5).
  public readonly float YMin;
  public readonly float YMax;
  public readonly byte[]? ClearMask;

  public ZoneRow(ZoneKey key, int index, int offset, int length, int placements, int live, ZoneFlags flags, float yMin, float yMax, byte[]? clearMask)
  {
    Key = key;
    Index = index;
    Offset = offset;
    Length = length;
    Placements = placements;
    Live = live;
    Flags = flags;
    YMin = yMin;
    YMax = yMax;
    ClearMask = clearMask;
  }

  public bool HasGround => (Flags & ZoneFlags.Ground) != 0;
  public bool HasPaint => (Flags & ZoneFlags.Paint) != 0;
  public bool HasClearMask => (Flags & ZoneFlags.ClearMask) != 0;
  // Zone flag 16: no vegetation anywhere in the zone.
  public bool NoVegetation => (Flags & ZoneFlags.NoVegetation) != 0;

  // Whether the 4 m cell holding the zone-local point (x, z in m from the south-west corner) is set in the file's clear mask.
  public bool MaskCovers(double localX, double localZ)
  {
    if (ClearMask == null)
      return false;
    int col = (int)Math.Floor(localX / 4.0), row = (int)Math.Floor(localZ / 4.0);
    if (col < 0 || col > 15 || row < 0 || row > 15)
      return false;
    int bit = row * 16 + col;
    return (ClearMask[bit >> 3] & (1 << (bit & 7))) != 0;
  }
}

// A zone's ground (spec 2.1): the base height in m, and per vertex of the 65 x 65 grid of 1 m (vertex i at (x0 + i % 65, z0 + i / 65), row 0
// south) the height above the base in cm and the weight (0 the world's ground, 255 this ground). Immutable once decoded.
internal sealed class ZoneGround
{
  public float Base { get; }
  public short[] Heights { get; }
  public byte[] Weights { get; }

  public ZoneGround(float baseMetres, short[] heights, byte[] weights)
  {
    if (heights.Length != BakedFormat.GroundVertices || weights.Length != BakedFormat.GroundVertices)
      throw new ArgumentException("a zone's ground has 65 x 65 vertices");
    Base = baseMetres;
    Heights = heights;
    Weights = weights;
  }

  // The ground's height at vertex i, in m (base + cm / 100: the way VALtima's writer made them).
  public float HeightAt(int vertex) => Base + Heights[vertex] * 0.01f;

  // The weight at vertex i, 0 to 1.
  public float WeightAt(int vertex) => Weights[vertex] / 255f;
}

// Every zone's ground in a layer, decoded when its snapshot is made, so that any thread can ask without a lock: nothing in it changes.
internal sealed class GroundSet
{
  public static readonly GroundSet Empty = new(new Dictionary<ZoneKey, ZoneGround>());

  private readonly Dictionary<ZoneKey, ZoneGround> zones;

  public GroundSet(Dictionary<ZoneKey, ZoneGround> zones)
  {
    this.zones = zones;
  }

  public int Count => zones.Count;
  public bool Any => zones.Count > 0;
  public IEnumerable<ZoneKey> Zones => zones.Keys;

  public bool TryGet(ZoneKey zone, out ZoneGround ground) => zones.TryGetValue(zone, out ground!);

  // The ground at a world position: the bilinear height and the bilinear weight of the four 1 m vertices around it (spec 8.1).
  // False when the position's zone has no ground. Safe on any thread.
  public bool TrySample(double worldX, double worldZ, out float height, out float weight)
  {
    height = 0f;
    weight = 0f;
    if (zones.Count == 0)
      return false;
    int zx = BakedFormat.ZoneOf(worldX), zz = BakedFormat.ZoneOf(worldZ);
    if (!zones.TryGetValue(new ZoneKey(zx, zz), out var ground))
      return false;
    double lx = worldX - BakedFormat.ZoneOrigin(zx), lz = worldZ - BakedFormat.ZoneOrigin(zz);
    int ix = (int)Math.Floor(lx), iz = (int)Math.Floor(lz);
    // The last vertex of the grid is the next zone's first: the position at the very edge uses the last cell.
    if (ix < 0) ix = 0;
    if (iz < 0) iz = 0;
    if (ix > BakedFormat.GroundSide - 2) ix = BakedFormat.GroundSide - 2;
    if (iz > BakedFormat.GroundSide - 2) iz = BakedFormat.GroundSide - 2;
    float fx = (float)(lx - ix), fz = (float)(lz - iz);
    if (fx < 0f) fx = 0f; else if (fx > 1f) fx = 1f;
    if (fz < 0f) fz = 0f; else if (fz > 1f) fz = 1f;
    int v00 = iz * BakedFormat.GroundSide + ix, v10 = v00 + 1, v01 = v00 + BakedFormat.GroundSide, v11 = v01 + 1;
    float h0 = ground.HeightAt(v00) + (ground.HeightAt(v10) - ground.HeightAt(v00)) * fx;
    float h1 = ground.HeightAt(v01) + (ground.HeightAt(v11) - ground.HeightAt(v01)) * fx;
    height = h0 + (h1 - h0) * fz;
    float w0 = ground.WeightAt(v00) + (ground.WeightAt(v10) - ground.WeightAt(v00)) * fx;
    float w1 = ground.WeightAt(v01) + (ground.WeightAt(v11) - ground.WeightAt(v01)) * fx;
    weight = w0 + (w1 - w0) * fz;
    return true;
  }
}

// Bytes a tool put in a zone that BC does not read (R4): kept as they are.
internal sealed class ZoneExtra
{
  public string Tag { get; }
  public byte[] Data { get; }

  public ZoneExtra(string tag, byte[] data)
  {
    Tag = tag;
    Data = data;
  }
}

// One record of a zone, in the form it has in the file: its columns, and the values of the runs it has (zero for a run it has not).
// World values come from the properties. Records are equal when every field is.
internal struct ZoneRecord : IEquatable<ZoneRecord>, IComparable<ZoneRecord>
{
  public ZoneKey Zone;
  // The palette entry.
  public ushort Palette;
  public RecordFlags Flags;
  // x and z in 1/1024 m from the zone's south-west corner; y in mm.
  public ushort X;
  public ushort Z;
  public int Y;
  // 65,536 steps of a turn. With FullRotation it is the rotation's own yaw and is not used to place the record.
  public ushort Yaw;
  // With FullRotation.
  public PackedRotation Packed;
  // With Scale: x, y and z times 1000.
  public ushort ScaleX, ScaleY, ScaleZ;
  // With Id (a Live record's lasting id).
  public uint Id;
  // With Source: the in-game bake that owns the record (1 to 65535) and its value set within that bake's. Without it, source 0: the
  // compiler's file.
  public ushort Source;
  public ushort ValueSet;
  // With Seed: RandomMaterialValues' seed, 0 to 12344.
  public ushort Seed;

  public bool HasFullRotation => (Flags & RecordFlags.FullRotation) != 0;
  public bool HasScale => (Flags & RecordFlags.Scale) != 0;
  public bool HasId => (Flags & RecordFlags.Id) != 0;
  public bool HasSource => (Flags & RecordFlags.Source) != 0;
  public bool HasSeed => (Flags & RecordFlags.Seed) != 0;

  // The source as the layer's owner sees it: 0 for a compiler's record.
  public int SourceNumber => HasSource ? Source : 0;

  // The point the record stands for (the anchor of its palette entry), in the world.
  public double WorldX => Zone.OriginX + X / BakedFormat.UnitsPerMetre;
  public double WorldZ => Zone.OriginZ + Z / BakedFormat.UnitsPerMetre;
  public double WorldY => Y / 1000.0;
  public Vector3 Position => new((float)WorldX, (float)WorldY, (float)WorldZ);

  public Quaternion Rotation => HasFullRotation ? Packed.ToQuaternion() : BakedFormat.YawRotation(Yaw);
  public Vector3 Scale => HasScale ? new Vector3(ScaleX / 1000f, ScaleY / 1000f, ScaleZ / 1000f) : Vector3.one;

  // The hash that gives the record its derived look (spec 2.5): over its point in world mm.
  public uint LookHash => BakedFormat.LookHash(WorldX, WorldY, WorldZ);

  // RandMatSeed: the stored one, else h mod 12,345.
  public int EffectiveSeed => HasSeed ? Seed : BakedFormat.DerivedSeed(LookHash);

  // A record from world values. rotation null: the yaw alone; a rotation that leans under 0.005 degrees also keeps only its yaw (10.5).
  // scale null: none stored. id: a Live record's. source 0: the compiler's. seed -1: none stored.
  public static ZoneRecord Create(int palette, double x, double y, double z, Quaternion? rotation = null, Vector3? scale = null, uint? id = null,
    int source = 0, int valueSet = 0, int seed = -1)
  {
    var record = new ZoneRecord { Zone = ZoneKey.OfPoint(x, z), Palette = (ushort)palette };
    record.X = BakedFormat.Quantize(x, record.Zone.X);
    record.Z = BakedFormat.Quantize(z, record.Zone.Z);
    record.Y = BakedFormat.QuantizeY(y);
    if (rotation is { } q)
    {
      record.Yaw = BakedFormat.QuantizeYaw(BakedFormat.YawOf(q));
      if (BakedFormat.TiltOf(q) >= 0.005)
      {
        record.Flags |= RecordFlags.FullRotation;
        record.Packed = PackedRotation.Of(q);
      }
    }
    if (scale is { } s)
    {
      record.Flags |= RecordFlags.Scale;
      record.ScaleX = QuantizeScale(s.x);
      record.ScaleY = QuantizeScale(s.y);
      record.ScaleZ = QuantizeScale(s.z);
    }
    if (id is { } lasting)
    {
      record.Flags |= RecordFlags.Id;
      record.Id = lasting;
    }
    if (source != 0)
    {
      record.Flags |= RecordFlags.Source;
      record.Source = (ushort)source;
      record.ValueSet = (ushort)valueSet;
    }
    if (seed >= 0)
    {
      record.Flags |= RecordFlags.Seed;
      record.Seed = (ushort)seed;
    }
    return record;
  }

  // A yaw in degrees instead of a rotation.
  public static ZoneRecord CreateYaw(int palette, double x, double y, double z, double yawDegrees, Vector3? scale = null, uint? id = null, int source = 0,
    int valueSet = 0, int seed = -1)
  {
    var record = Create(palette, x, y, z, null, scale, id, source, valueSet, seed);
    record.Yaw = BakedFormat.QuantizeYaw(yawDegrees);
    return record;
  }

  // A scale as the run holds it: x1000, 0.001 to 65.535.
  public static ushort QuantizeScale(float scale)
  {
    double v = Math.Round(scale * 1000.0);
    return (ushort)(v < 1.0 ? 1 : v > 65535.0 ? 65535 : v);
  }

  // The order a block holds its records in (spec 2.6): palette index, then z, x, y, yaw, then the run values.
  public int CompareTo(ZoneRecord other)
  {
    int c = Palette.CompareTo(other.Palette);
    if (c != 0) return c;
    c = Z.CompareTo(other.Z);
    if (c != 0) return c;
    c = X.CompareTo(other.X);
    if (c != 0) return c;
    c = Y.CompareTo(other.Y);
    if (c != 0) return c;
    c = Yaw.CompareTo(other.Yaw);
    if (c != 0) return c;
    c = ((byte)Flags).CompareTo((byte)other.Flags);
    if (c != 0) return c;
    c = Packed.Largest.CompareTo(other.Packed.Largest);
    if (c != 0) return c;
    c = Packed.A.CompareTo(other.Packed.A);
    if (c != 0) return c;
    c = Packed.B.CompareTo(other.Packed.B);
    if (c != 0) return c;
    c = Packed.C.CompareTo(other.Packed.C);
    if (c != 0) return c;
    c = ScaleX.CompareTo(other.ScaleX);
    if (c != 0) return c;
    c = ScaleY.CompareTo(other.ScaleY);
    if (c != 0) return c;
    c = ScaleZ.CompareTo(other.ScaleZ);
    if (c != 0) return c;
    c = Id.CompareTo(other.Id);
    if (c != 0) return c;
    c = Source.CompareTo(other.Source);
    if (c != 0) return c;
    c = ValueSet.CompareTo(other.ValueSet);
    if (c != 0) return c;
    return Seed.CompareTo(other.Seed);
  }

  public bool Equals(ZoneRecord other) => Zone.Equals(other.Zone) && CompareTo(other) == 0;
  public override bool Equals(object? obj) => obj is ZoneRecord other && Equals(other);
  public override int GetHashCode() => (Palette << 16) ^ (Z << 8) ^ X ^ Y ^ (int)Id ^ (Yaw << 4);

  public override string ToString() => $"#{Palette} at {WorldX:0.###}, {WorldY:0.###}, {WorldZ:0.###} in zone {Zone}";
}

// A zone's block, decoded: its records as the columns the file holds them in (record k's palette entry is Palette[k], and so on), the
// runs in the order the file holds them (only the records with the flag have a value), and the zone's sections. Record(k) and Records()
// give a record with its runs resolved. Not shared between threads while it is used: a decode makes a new one.
internal sealed class ZoneData
{
  public ZoneKey Zone { get; }
  public int Count { get; }
  public ZoneFlags ZoneFlags { get; internal set; }

  // The columns.
  public ushort[] Palette { get; }
  public RecordFlags[] Flags { get; }
  public ushort[] X { get; }
  public ushort[] Z { get; }
  public int[] Y { get; }
  public ushort[] Yaw { get; }

  // The runs (R2), each in record order and only for the records with the flag.
  public PackedRotation[] RotationRun { get; internal set; } = Array.Empty<PackedRotation>();
  // x, y, z times 1000, three to a record.
  public ushort[] ScaleRun { get; internal set; } = Array.Empty<ushort>();
  public uint[] IdRun { get; internal set; } = Array.Empty<uint>();
  public ushort[] SourceRun { get; internal set; } = Array.Empty<ushort>();
  public ushort[] ValueSetRun { get; internal set; } = Array.Empty<ushort>();
  public ushort[] SeedRun { get; internal set; } = Array.Empty<ushort>();

  // The sections: null when the zone has none of that kind.
  public byte[]? ClearMask { get; internal set; }
  public ZoneGround? Ground { get; internal set; }
  // 64 x 64 cells of 1 m from the south-west corner, row 0 south: 0 none, 1 dirt, 2 paved, 3 cultivated.
  public byte[]? Paint { get; internal set; }
  public ZoneExtra[] Extras { get; internal set; } = Array.Empty<ZoneExtra>();

  // Where each record's value is in each run, built on first use.
  private int[]? rotationAt, scaleAt, idAt, sourceAt, seedAt;

  public ZoneData(ZoneKey zone, int count)
  {
    Zone = zone;
    Count = count;
    Palette = new ushort[count];
    Flags = new RecordFlags[count];
    X = new ushort[count];
    Z = new ushort[count];
    Y = new int[count];
    Yaw = new ushort[count];
  }

  private static int[]? Positions(RecordFlags[] flags, RecordFlags flag)
  {
    int n = 0;
    var at = new int[flags.Length];
    for (int k = 0; k < flags.Length; k++)
      at[k] = (flags[k] & flag) != 0 ? n++ : -1;
    return n == 0 ? null : at;
  }

  private void BuildPositions()
  {
    if (rotationAt != null || scaleAt != null || idAt != null || sourceAt != null || seedAt != null || Count == 0)
      return;
    rotationAt = Positions(Flags, RecordFlags.FullRotation);
    scaleAt = Positions(Flags, RecordFlags.Scale);
    idAt = Positions(Flags, RecordFlags.Id);
    sourceAt = Positions(Flags, RecordFlags.Source);
    seedAt = Positions(Flags, RecordFlags.Seed);
  }

  // Record k with its runs resolved.
  public ZoneRecord Record(int k)
  {
    BuildPositions();
    var r = new ZoneRecord { Zone = Zone, Palette = Palette[k], Flags = Flags[k], X = X[k], Z = Z[k], Y = Y[k], Yaw = Yaw[k] };
    if (rotationAt != null && rotationAt[k] >= 0)
      r.Packed = RotationRun[rotationAt[k]];
    if (scaleAt != null && scaleAt[k] >= 0)
    {
      int i = scaleAt[k] * 3;
      r.ScaleX = ScaleRun[i];
      r.ScaleY = ScaleRun[i + 1];
      r.ScaleZ = ScaleRun[i + 2];
    }
    if (idAt != null && idAt[k] >= 0)
      r.Id = IdRun[idAt[k]];
    if (sourceAt != null && sourceAt[k] >= 0)
    {
      r.Source = SourceRun[sourceAt[k]];
      r.ValueSet = ValueSetRun[sourceAt[k]];
    }
    if (seedAt != null && seedAt[k] >= 0)
      r.Seed = SeedRun[seedAt[k]];
    return r;
  }

  // Every record in the order the block holds them.
  public IEnumerable<ZoneRecord> Records()
  {
    for (int k = 0; k < Count; k++)
      yield return Record(k);
  }

  // The same, one value per record, for a caller that wants arrays: the rotation (a yaw-only record's yaw), the scale (1 for none), the id
  // (0 for none: check Flags), the source (0: the compiler's), the seed (the derived one for a record without one) and the value set
  // (-1 for none). Each is built on first use.
  private Quaternion[]? rotations;
  private Vector3[]? scales;
  private uint[]? ids;
  private int[]? sources, seeds, valueSets;

  public Quaternion[] Rotations
  {
    get
    {
      if (rotations != null)
        return rotations;
      var all = new Quaternion[Count];
      for (int k = 0; k < Count; k++)
        all[k] = Record(k).Rotation;
      return rotations = all;
    }
  }

  public Vector3[] Scales
  {
    get
    {
      if (scales != null)
        return scales;
      var all = new Vector3[Count];
      for (int k = 0; k < Count; k++)
        all[k] = Record(k).Scale;
      return scales = all;
    }
  }

  public uint[] Ids
  {
    get
    {
      if (ids != null)
        return ids;
      var all = new uint[Count];
      for (int k = 0; k < Count; k++)
        all[k] = Record(k).Id;
      return ids = all;
    }
  }

  public int[] Sources
  {
    get
    {
      if (sources != null)
        return sources;
      var all = new int[Count];
      for (int k = 0; k < Count; k++)
        all[k] = Record(k).SourceNumber;
      return sources = all;
    }
  }

  public int[] Seeds
  {
    get
    {
      if (seeds != null)
        return seeds;
      var all = new int[Count];
      for (int k = 0; k < Count; k++)
        all[k] = Record(k).EffectiveSeed;
      return seeds = all;
    }
  }

  public int[] ValueSets
  {
    get
    {
      if (valueSets != null)
        return valueSets;
      var all = new int[Count];
      for (int k = 0; k < Count; k++)
      {
        var r = Record(k);
        all[k] = r.HasSource ? r.ValueSet : -1;
      }
      return valueSets = all;
    }
  }

  // The point of record k in the world, and its yaw, without a struct per record.
  public double WorldX(int k) => Zone.OriginX + X[k] / BakedFormat.UnitsPerMetre;
  public double WorldZ(int k) => Zone.OriginZ + Z[k] / BakedFormat.UnitsPerMetre;
  public double WorldY(int k) => Y[k] / 1000.0;
}

// ======================================================================================================== the codecs

// What a block needs to know of its layer to be checked: how many palette entries there are, which are Live, and the registry (null when
// the layer has none, so that no record may name a bake).
internal sealed class BlockContext
{
  public readonly int PaletteCount;
  public readonly BakedRole[] Roles;
  public readonly Registry? Registry;

  public BlockContext(IReadOnlyList<PaletteEntry> palette, Registry? registry)
  {
    PaletteCount = palette.Count;
    Roles = new BakedRole[palette.Count];
    for (int i = 0; i < Roles.Length; i++)
      Roles[i] = palette[i].Role;
    Registry = registry;
  }
}

// The sections and flags of a zone that are not records, as one value to hand to the encoder.
internal sealed class ZoneSections
{
  // Only the flags the sections do not decide: ProtectFootprints and NoVegetation. The others follow from what is present.
  public ZoneFlags Flags;
  public byte[]? Mask;
  public ZoneGround? Ground;
  public byte[]? Paint;
  public ZoneExtra[] Extras = Array.Empty<ZoneExtra>();

  public ZoneFlags AllFlags =>
    (Flags & (ZoneFlags.ProtectFootprints | ZoneFlags.NoVegetation)) | (Mask != null ? ZoneFlags.ClearMask : 0) | (Ground != null ? ZoneFlags.Ground : 0)
    | (Paint != null ? ZoneFlags.Paint : 0);

  public bool IsEmpty => AllFlags == ZoneFlags.None && Extras.Length == 0;

  public static ZoneSections Of(ZoneData data) => new()
  {
    Flags = data.ZoneFlags & (ZoneFlags.ProtectFootprints | ZoneFlags.NoVegetation),
    Mask = data.ClearMask,
    Ground = data.Ground,
    Paint = data.Paint,
    Extras = data.Extras,
  };
}

internal static class BakedCodec
{
  // ------------------------------------------------------------------------------------------------ the palette (spec 2.1)

  public static PaletteEntry[] ReadPalette(BakedReader r, int count)
  {
    var palette = new PaletteEntry[count];
    for (int e = 0; e < count; e++)
    {
      r.Where = "palette entry " + e;
      int candidateCount = r.U8();
      if (candidateCount < 1 || candidateCount > 8)
        throw new BakedFormatException($"it has {candidateCount} candidates (1 to 8)", r.Where);
      var candidates = new Candidate[candidateCount];
      for (int i = 0; i < candidateCount; i++)
      {
        string name = r.Str();
        short ax = r.I16(), ay = r.I16(), az = r.I16();
        candidates[i] = new Candidate(name, ax, ay, az);
      }
      int role = r.U8(), collision = r.U8(), layer = r.U8(), flags = r.U8();
      if (role > 3)
        throw new BakedFormatException($"its role is {role} (0 to 3)", r.Where);
      if (collision > 3)
        throw new BakedFormatException($"its collision is {collision} (0 to 3)", r.Where);
      if (layer > 31)
        throw new BakedFormatException($"its layer is {layer} (0 to 31)", r.Where);
      if ((flags & ~(int)PaletteFlags.All) != 0)
        throw new BakedFormatException($"made by a newer Better Continents or tool: palette flag bit {LowestBit(flags & ~(int)PaletteFlags.All)}", r.Where);
      Box[]? boxes = null;
      if (collision == (int)BakedCollision.Boxes)
      {
        int boxCount = r.U8();
        boxes = new Box[boxCount];
        for (int i = 0; i < boxCount; i++)
          boxes[i] = new Box(r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16());
      }
      string tint = r.Str();
      ushort tintR = 1000, tintG = 1000, tintB = 1000;
      string filter = "";
      if (tint.Length > 0)
      {
        tintR = r.U16();
        tintG = r.U16();
        tintB = r.U16();
        filter = r.Str();
      }
      int tagCount = r.U8();
      var tags = new Tag[tagCount];
      for (int i = 0; i < tagCount; i++)
      {
        string key = r.Str();
        int type = r.U8();
        switch (type)
        {
          case (int)TagType.Bool: tags[i] = Tag.OfBool(key, r.U8() != 0); break;
          case (int)TagType.Int: tags[i] = Tag.OfInt(key, r.I32()); break;
          case (int)TagType.Float: tags[i] = Tag.OfFloat(key, r.F32()); break;
          case (int)TagType.String: tags[i] = Tag.OfString(key, r.Str()); break;
          default: throw new BakedFormatException($"the tag '{key}' has type {type} (0 to 3)", r.Where);
        }
      }
      palette[e] = new PaletteEntry(candidates, (BakedRole)role, (BakedCollision)collision, (byte)layer, (PaletteFlags)flags, boxes, tint, tintR, tintG, tintB, filter, tags);
    }
    return palette;
  }

  // What a palette entry must be for the file to hold it; throws BakedFormatException naming the entry otherwise.
  public static void Validate(PaletteEntry entry, string where)
  {
    if (entry.Candidates.Length < 1 || entry.Candidates.Length > 8)
      throw new BakedFormatException($"it has {entry.Candidates.Length} candidates (1 to 8)", where);
    if (entry.Layer > 31)
      throw new BakedFormatException($"its layer is {entry.Layer} (0 to 31)", where);
    if ((entry.Flags & ~PaletteFlags.All) != 0)
      throw new BakedFormatException("it has a flag format 1 does not know", where);
    if (entry.Boxes.Length > 255 || entry.Tags.Length > 255)
      throw new BakedFormatException("it has over 255 boxes or tags", where);
    foreach (var c in entry.Candidates)
      if (!BakedFormat.IsWritable(c.Name))
        throw new BakedFormatException($"the prefab name '{c.Name}' is over 255 characters or has one outside ASCII, which the format cannot hold", where);
    if (!BakedFormat.IsWritable(entry.Tint) || !BakedFormat.IsWritable(entry.TintFilter))
      throw new BakedFormatException("its tint or material filter is over 255 characters or has one outside ASCII", where);
    foreach (var tag in entry.Tags)
      if (!BakedFormat.IsWritable(tag.Key) || (tag.Type == TagType.String && !BakedFormat.IsWritable(tag.Text)))
        throw new BakedFormatException($"the tag '{tag.Key}' is over 255 characters or has one outside ASCII", where);
  }

  public static void WritePalette(BakedWriter w, IReadOnlyList<PaletteEntry> palette)
  {
    if (palette.Count > BakedFormat.MaxPalette)
      throw new BakedFormatException($"the palette has {palette.Count} entries; format 1 holds {BakedFormat.MaxPalette}");
    for (int e = 0; e < palette.Count; e++)
    {
      var entry = palette[e];
      Validate(entry, "palette entry " + e + " (" + entry.Name + ")");
      w.U8(entry.Candidates.Length);
      foreach (var c in entry.Candidates)
      {
        w.Str(c.Name);
        w.I16(c.AnchorX);
        w.I16(c.AnchorY);
        w.I16(c.AnchorZ);
      }
      w.U8((int)entry.Role);
      w.U8((int)entry.Collision);
      w.U8(entry.Layer);
      w.U8((int)entry.Flags);
      if (entry.Collision == BakedCollision.Boxes)
      {
        w.U8(entry.Boxes.Length);
        foreach (var b in entry.Boxes)
        {
          w.I16(b.CentreX);
          w.I16(b.CentreY);
          w.I16(b.CentreZ);
          w.I16(b.SizeX);
          w.I16(b.SizeY);
          w.I16(b.SizeZ);
        }
      }
      w.Str(entry.Tint);
      if (entry.HasTint)
      {
        w.U16(entry.TintR);
        w.U16(entry.TintG);
        w.U16(entry.TintB);
        w.Str(entry.TintFilter);
      }
      w.U8(entry.Tags.Length);
      foreach (var tag in entry.Tags)
      {
        w.Str(tag.Key);
        w.U8((int)tag.Type);
        switch (tag.Type)
        {
          case TagType.Bool: w.U8(tag.Number != 0 ? 1 : 0); break;
          case TagType.Int: w.I32(tag.Number); break;
          case TagType.Float: w.F32(tag.Float); break;
          default: w.Str(tag.Text); break;
        }
      }
    }
  }

  private static int LowestBit(int bits)
  {
    int n = 0;
    while ((bits & 1) == 0)
    {
      bits >>= 1;
      n++;
    }
    return n;
  }

  public static int LowestUnknownBit(int flags, int known) => LowestBit(flags & ~known);

  // ------------------------------------------------------------------------------------------------ the registry (spec 2.4)

  public static Registry ReadRegistry(BakedReader r, ref byte[] scratch)
  {
    r.Where = "registry";
    uint length = r.U32();
    if (length > (uint)r.Remaining)
      throw new BakedFormatException($"its Deflate stream of {length} bytes runs past the end of the file", r.Where);
    int start = r.Pos;
    r.Skip((int)length);
    int inflated = BakedFormat.Inflate(r.Source, start, (int)length, ref scratch, BakedFormat.MaxInflated, r.Where);
    var q = new BakedReader(scratch, 0, inflated) { Where = r.Where };
    int format = q.U16();
    if (format != BakedFormat.RegistryFormat)
      throw new BakedFormatException($"its format is {format}, not {BakedFormat.RegistryFormat}", r.Where);
    ushort next = q.U16();
    int operationCount = q.U16();
    var operations = new OperationInfo[operationCount];
    var seen = new HashSet<int>();
    for (int i = 0; i < operationCount; i++)
    {
      q.Where = "registry, operation " + i;
      ushort number = q.U16();
      int kind = q.U8(), state = q.U8();
      if (kind < 1 || kind > 5)
        throw new BakedFormatException($"its kind is {kind} (1 to 5)", q.Where);
      if (state < 1 || state > 2)
        throw new BakedFormatException($"its state is {state} (1 or 2)", q.Where);
      if (!seen.Add(number))
        throw new BakedFormatException($"operation {number} is listed twice", q.Where);
      long time = q.I64();
      string who = q.Str16();
      float x1 = q.F32(), z1 = q.F32(), x2 = q.F32(), z2 = q.F32(), radius = q.F32();
      uint added = q.U32(), removed = q.U32(), adopted = q.U32();
      string version = q.Str16();
      int setCount = q.U16();
      var sets = new ValueSet[setCount];
      for (int s = 0; s < setCount; s++)
      {
        int valueCount = q.U16();
        var values = new ZdoValue[valueCount];
        for (int v = 0; v < valueCount; v++)
        {
          int key = q.I32();
          int type = q.U8();
          switch (type)
          {
            case (int)ZdoValueType.Float: values[v] = ZdoValue.OfFloat(key, q.F32()); break;
            case (int)ZdoValueType.Vec3: values[v] = ZdoValue.OfVec3(key, q.F32(), q.F32(), q.F32()); break;
            case (int)ZdoValueType.Quat: values[v] = ZdoValue.OfQuat(key, q.F32(), q.F32(), q.F32(), q.F32()); break;
            case (int)ZdoValueType.Int: values[v] = ZdoValue.OfInt(key, q.I32()); break;
            case (int)ZdoValueType.Long: values[v] = ZdoValue.OfLong(key, q.I64()); break;
            case (int)ZdoValueType.String: values[v] = ZdoValue.OfString(key, q.Str16()); break;
            case (int)ZdoValueType.Bytes:
              uint size = q.U32();
              if (size > (uint)q.Remaining)
                throw new BakedFormatException($"a value of {size} bytes runs past the end", q.Where);
              values[v] = ZdoValue.OfBytes(key, q.Bytes((int)size));
              break;
            default: throw new BakedFormatException($"a value has type {type} (0 to 6)", q.Where);
          }
        }
        sets[s] = new ValueSet(values);
      }
      operations[i] = new OperationInfo(number, (OperationKind)kind, (OperationState)state, time, who, x1, z1, x2, z2, radius, added, removed, adopted, version, sets);
    }
    if (q.Remaining != 0)
      throw new BakedFormatException($"{q.Remaining} bytes are left over after its operations", r.Where);
    return new Registry(next, operations);
  }

  public static void WriteRegistry(BakedWriter w, Registry registry)
  {
    var q = new BakedWriter(256);
    q.U16(BakedFormat.RegistryFormat);
    q.U16(registry.NextOperation);
    if (registry.Operations.Count > ushort.MaxValue)
      throw new BakedFormatException("the registry holds over 65,535 operations");
    q.U16(registry.Operations.Count);
    foreach (var op in registry.Operations)
    {
      q.U16(op.Number);
      q.U8((int)op.Kind);
      q.U8((int)op.State);
      q.I64(op.Time);
      q.Str16(op.Who);
      q.F32(op.X1);
      q.F32(op.Z1);
      q.F32(op.X2);
      q.F32(op.Z2);
      q.F32(op.Radius);
      q.U32(op.Added);
      q.U32(op.Removed);
      q.U32(op.Adopted);
      q.Str16(op.Version);
      if (op.ValueSets.Length > ushort.MaxValue)
        throw new BakedFormatException($"operation {op.Number} has over 65,535 value sets");
      q.U16(op.ValueSets.Length);
      foreach (var set in op.ValueSets)
      {
        if (set.Values.Length > ushort.MaxValue)
          throw new BakedFormatException($"a value set of operation {op.Number} has over 65,535 values");
        q.U16(set.Values.Length);
        foreach (var v in set.Values)
        {
          q.I32(v.Key);
          q.U8((int)v.Type);
          switch (v.Type)
          {
            case ZdoValueType.Float: q.F32(v.A); break;
            case ZdoValueType.Vec3: q.F32(v.A); q.F32(v.B); q.F32(v.C); break;
            case ZdoValueType.Quat: q.F32(v.A); q.F32(v.B); q.F32(v.C); q.F32(v.D); break;
            case ZdoValueType.Int: q.I32((int)v.Number); break;
            case ZdoValueType.Long: q.I64(v.Number); break;
            case ZdoValueType.String: q.Str16(v.Text); break;
            default:
              q.U32((uint)v.Data.Length);
              q.Bytes(v.Data);
              break;
          }
        }
      }
    }
    var deflated = BakedFormat.Deflate(q.Buffer_, q.Length);
    w.U32((uint)deflated.Length);
    w.Bytes(deflated);
  }

  // ------------------------------------------------------------------------------------------------ the zone index (spec 2.1)

  public static void WriteIndexRow(BakedWriter w, ZoneKey key, int offset, int length, int placements, int live, ZoneFlags flags, float yMin, float yMax)
  {
    if (live > ushort.MaxValue)
      throw new BakedFormatException($"the zone holds {live} live pieces; format 1 holds 65,535 in a zone", "zone " + key);
    w.I16(key.X);
    w.I16(key.Z);
    w.U32((uint)offset);
    w.U32((uint)length);
    w.U32((uint)placements);
    w.U16(live);
    w.U8((int)flags);
    w.U8(0);
    w.F32(yMin);
    w.F32(yMax);
  }

  // ------------------------------------------------------------------------------------------------ a zone's block (spec 2.1)

  // Reads a zone's inflated block, checking what spec 2.2 asks of a block. `expected` is the index's count of records. `live` is the
  // number of records whose palette entry is Live.
  public static ZoneData ParseBlock(byte[] raw, int length, ZoneKey zone, int expected, ZoneFlags zoneFlags, BlockContext ctx, out int live)
  {
    string where = "zone " + zone;
    var r = new BakedReader(raw, 0, length) { Where = where };
    uint stored = r.U32();
    if (stored != (uint)expected)
      throw new BakedFormatException($"the block holds {stored} records, the index says {expected}", where);
    int n = expected;
    if ((long)n * 13 > r.Remaining)
      throw new BakedFormatException($"the block is too short for its {n} records", where);
    var data = new ZoneData(zone, n) { ZoneFlags = zoneFlags };

    for (int k = 0; k < n; k++)
    {
      ushort p = r.U16();
      if (p >= ctx.PaletteCount)
        throw new BakedFormatException($"its palette index {p} is outside the palette of {ctx.PaletteCount} entries", where + ", record " + k);
      data.Palette[k] = p;
    }
    int rotations = 0, scales = 0, ids = 0, sources = 0, seeds = 0;
    live = 0;
    for (int k = 0; k < n; k++)
    {
      byte f = r.U8();
      if ((f & ~(int)RecordFlags.All) != 0)
        throw new BakedFormatException($"made by a newer Better Continents or tool: record bit {BakedCodec.LowestUnknownBit(f, (int)RecordFlags.All)}", where + ", record " + k);
      var flags = (RecordFlags)f;
      if ((flags & RecordFlags.Source) != 0 && ctx.Registry == null)
        throw new BakedFormatException("it names a bake (flag 8) but the layer has no registry", where + ", record " + k);
      if (ctx.Roles[data.Palette[k]] == BakedRole.Live)
      {
        live++;
        if ((flags & RecordFlags.Id) == 0)
          throw new BakedFormatException("a Live record has no id", where + ", record " + k);
      }
      data.Flags[k] = flags;
      if ((flags & RecordFlags.FullRotation) != 0) rotations++;
      if ((flags & RecordFlags.Scale) != 0) scales++;
      if ((flags & RecordFlags.Id) != 0) ids++;
      if ((flags & RecordFlags.Source) != 0) sources++;
      if ((flags & RecordFlags.Seed) != 0) seeds++;
    }
    for (int k = 0; k < n; k++)
      data.X[k] = r.U16();
    for (int k = 0; k < n; k++)
      data.Z[k] = r.U16();
    for (int k = 0; k < n; k++)
      data.Y[k] = r.I32();
    for (int k = 0; k < n; k++)
      data.Yaw[k] = r.U16();

    // The runs, each in record order and only for the records with the flag.
    if (rotations > 0)
    {
      data.RotationRun = new PackedRotation[rotations];
      for (int i = 0; i < rotations; i++)
      {
        byte largest = r.U8();
        if (largest > 3)
          throw new BakedFormatException($"a rotation names component {largest} as its largest (0 to 3)", where);
        data.RotationRun[i] = new PackedRotation(largest, r.I16(), r.I16(), r.I16());
      }
    }
    if (scales > 0)
    {
      data.ScaleRun = new ushort[scales * 3];
      for (int i = 0; i < data.ScaleRun.Length; i++)
      {
        ushort s = r.U16();
        if (s == 0)
          throw new BakedFormatException("a scale is 0 (0.001 to 65.535)", where);
        data.ScaleRun[i] = s;
      }
    }
    if (ids > 0)
    {
      data.IdRun = new uint[ids];
      for (int i = 0; i < ids; i++)
        data.IdRun[i] = r.U32();
    }
    if (sources > 0)
    {
      data.SourceRun = new ushort[sources];
      data.ValueSetRun = new ushort[sources];
      for (int i = 0; i < sources; i++)
      {
        ushort source = r.U16();
        if (source == 0)
          throw new BakedFormatException("a record names bake 0 (1 to 65535)", where);
        data.SourceRun[i] = source;
      }
      for (int i = 0; i < sources; i++)
        data.ValueSetRun[i] = r.U16();
      for (int i = 0; i < sources; i++)
        if (!ctx.Registry!.Knows(data.SourceRun[i], data.ValueSetRun[i]))
          throw new BakedFormatException($"it names bake {data.SourceRun[i]} value set {data.ValueSetRun[i]}, which the registry does not list", where);
    }
    if (seeds > 0)
    {
      data.SeedRun = new ushort[seeds];
      for (int i = 0; i < seeds; i++)
      {
        ushort seed = r.U16();
        if (seed > BakedFormat.MaxSeed)
          throw new BakedFormatException($"a seed is {seed} (0 to {BakedFormat.MaxSeed})", where);
        data.SeedRun[i] = seed;
      }
    }

    // The sections, in the order the zone's flags say.
    if ((zoneFlags & ZoneFlags.ClearMask) != 0)
      data.ClearMask = r.Bytes(BakedFormat.MaskBytes);
    if ((zoneFlags & ZoneFlags.Ground) != 0)
    {
      float baseMetres = r.F32();
      var heights = new short[BakedFormat.GroundVertices];
      for (int i = 0; i < heights.Length; i++)
        heights[i] = r.I16();
      var weights = r.Bytes(BakedFormat.GroundVertices);
      data.Ground = new ZoneGround(baseMetres, heights, weights);
    }
    if ((zoneFlags & ZoneFlags.Paint) != 0)
      data.Paint = r.Bytes(BakedFormat.PaintCells);
    // R4: the extras, always written.
    int extraCount = r.U8();
    if (extraCount > 0)
    {
      var extras = new ZoneExtra[extraCount];
      for (int i = 0; i < extraCount; i++)
      {
        string tag = r.Str();
        uint size = r.U32();
        if (size > (uint)r.Remaining)
          throw new BakedFormatException($"an extra of {size} bytes runs past the end", where);
        extras[i] = new ZoneExtra(tag, r.Bytes((int)size));
      }
      data.Extras = extras;
    }
    if (r.Remaining != 0)
      throw new BakedFormatException($"{r.Remaining} bytes are left over after the extras", where);
    return data;
  }

  // The block of a zone as format 1 holds it: the records (already in the order they are to be held in) and the sections, as raw bytes
  // before Deflate. The zone flags that follow from the sections come back in `flags`.
  public static byte[] EncodeBlockRaw(IReadOnlyList<ZoneRecord> records, ZoneSections sections, out int rawLength, out ZoneFlags flags)
  {
    int n = records.Count;
    var w = new BakedWriter(16 + n * 20 + (sections.Ground != null ? 13000 : 0) + (sections.Paint != null ? 4100 : 0));
    w.U32((uint)n);
    for (int k = 0; k < n; k++)
      w.U16(records[k].Palette);
    int rotations = 0, scales = 0, ids = 0, sources = 0, seeds = 0;
    for (int k = 0; k < n; k++)
    {
      var f = records[k].Flags;
      if ((f & ~RecordFlags.All) != 0)
        throw new BakedFormatException("a record has a flag format 1 does not know", "zone " + records[k].Zone + ", record " + k);
      w.U8((int)f);
      if ((f & RecordFlags.FullRotation) != 0) rotations++;
      if ((f & RecordFlags.Scale) != 0) scales++;
      if ((f & RecordFlags.Id) != 0) ids++;
      if ((f & RecordFlags.Source) != 0) sources++;
      if ((f & RecordFlags.Seed) != 0) seeds++;
    }
    for (int k = 0; k < n; k++)
      w.U16(records[k].X);
    for (int k = 0; k < n; k++)
      w.U16(records[k].Z);
    for (int k = 0; k < n; k++)
      w.I32(records[k].Y);
    for (int k = 0; k < n; k++)
      w.U16(records[k].Yaw);
    if (rotations > 0)
      for (int k = 0; k < n; k++)
        if (records[k].HasFullRotation)
        {
          var p = records[k].Packed;
          w.U8(p.Largest);
          w.I16(p.A);
          w.I16(p.B);
          w.I16(p.C);
        }
    if (scales > 0)
      for (int k = 0; k < n; k++)
        if (records[k].HasScale)
        {
          w.U16(records[k].ScaleX);
          w.U16(records[k].ScaleY);
          w.U16(records[k].ScaleZ);
        }
    if (ids > 0)
      for (int k = 0; k < n; k++)
        if (records[k].HasId)
          w.U32(records[k].Id);
    if (sources > 0)
    {
      for (int k = 0; k < n; k++)
        if (records[k].HasSource)
          w.U16(records[k].Source);
      for (int k = 0; k < n; k++)
        if (records[k].HasSource)
          w.U16(records[k].ValueSet);
    }
    if (seeds > 0)
      for (int k = 0; k < n; k++)
        if (records[k].HasSeed)
          w.U16(records[k].Seed);
    flags = sections.AllFlags;
    if (sections.Mask != null)
    {
      if (sections.Mask.Length != BakedFormat.MaskBytes)
        throw new BakedFormatException("a clear mask is not 32 bytes");
      w.Bytes(sections.Mask);
    }
    if (sections.Ground != null)
    {
      w.F32(sections.Ground.Base);
      foreach (var h in sections.Ground.Heights)
        w.I16(h);
      w.Bytes(sections.Ground.Weights);
    }
    if (sections.Paint != null)
    {
      if (sections.Paint.Length != BakedFormat.PaintCells)
        throw new BakedFormatException("a paint section is not 64 x 64 cells");
      w.Bytes(sections.Paint);
    }
    if (sections.Extras.Length > 255)
      throw new BakedFormatException("a zone has over 255 extras");
    w.U8(sections.Extras.Length);
    foreach (var extra in sections.Extras)
    {
      w.Str(extra.Tag);
      w.U32((uint)extra.Data.Length);
      w.Bytes(extra.Data);
    }
    rawLength = w.Length;
    return w.Buffer_;
  }
}

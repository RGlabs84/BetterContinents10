// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// A second writer of format 1, in the tests, written from the spec's layout and nothing else: it makes the files the reader is held to,
// deliberately damaged ones included (a field past its limit, a count that does not add up), and the files BC's writer is compared with.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BetterContinents;

namespace PlacementTests;

// One palette entry as bytes.
internal sealed class RawEntry
{
  public List<(string Name, short[] Anchor)> Candidates = [("piece_a", [0, 0, 0])];
  public byte Role, Collision, Layer, Flags;
  public List<short[]> Boxes = [];
  public string Tint = "";
  public ushort[] Rgb = [1000, 1000, 1000];
  public string Filter = "";
  public List<(string Key, byte Type, object Value)> Tags = [];
  // Written as they are instead of from the fields above (a deliberately wrong entry).
  public byte[] Override;

  public byte[] Bytes()
  {
    if (Override != null)
      return Override;
    var w = new MemoryStream();
    void U8(int v) => w.WriteByte((byte)v);
    void I16(int v) { U8(v & 0xFF); U8((v >> 8) & 0xFF); }
    void Str(string s) { U8(s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }
    U8(Candidates.Count);
    foreach (var (name, anchor) in Candidates)
    {
      Str(name);
      foreach (var a in anchor)
        I16(a);
    }
    U8(Role);
    U8(Collision);
    U8(Layer);
    U8(Flags);
    if (Collision == 1)
    {
      U8(Boxes.Count);
      foreach (var box in Boxes)
        foreach (var v in box)
          I16(v);
    }
    Str(Tint);
    if (Tint.Length > 0)
    {
      foreach (var c in Rgb)
        I16(c);
      Str(Filter);
    }
    U8(Tags.Count);
    foreach (var (key, type, value) in Tags)
    {
      Str(key);
      U8(type);
      switch (type)
      {
        case 0: U8((bool)value ? 1 : 0); break;
        case 1: w.Write(BitConverter.GetBytes((int)value)); break;
        case 2: w.Write(BitConverter.GetBytes((float)value)); break;
        default: Str((string)value); break;
      }
    }
    return w.ToArray();
  }
}

// One record as the columns and runs of a block hold it.
internal sealed class RawRecord
{
  public ushort Palette, X, Z, Yaw;
  public int Y;
  public byte Flags;
  public (byte Largest, short A, short B, short C)? Rotation;
  public ushort[] Scale;
  public uint? Id;
  public ushort? Source, ValueSet, Seed;
}

// One zone: its index row and its block.
internal sealed class RawZone
{
  public int X, Z;
  public List<RawRecord> Records = [];
  public byte ZoneFlagsExtra;
  public byte[] Mask;
  public byte[] GroundBytes;
  public byte[] Paint;
  public List<(string Tag, byte[] Data)> Extras = [];
  public float YMin, YMax;
  // Overrides of what the index row says, for a damaged file.
  public int? IndexCount, IndexLive, IndexFlags, IndexPad;
  public uint? Offset, Length;
  // The inflated block as it is (a damaged block), instead of from the fields above.
  public byte[] RawBlock;
  // The Deflate stream as it is.
  public byte[] Deflated;

  public int ZoneFlags => (GroundBytes != null ? 1 : 0) | (Paint != null ? 2 : 0) | (Mask != null ? 4 : 0) | ZoneFlagsExtra;

  public byte[] Raw(bool extrasCount = true)
  {
    if (RawBlock != null)
      return RawBlock;
    var w = new MemoryStream();
    void U8(int v) => w.WriteByte((byte)v);
    void U16(int v) { U8(v & 0xFF); U8((v >> 8) & 0xFF); }
    void U32(uint v) => w.Write(BitConverter.GetBytes(v));
    void I32(int v) => w.Write(BitConverter.GetBytes(v));
    var recs = Records;
    U32((uint)recs.Count);
    foreach (var r in recs) U16(r.Palette);
    foreach (var r in recs) U8(r.Flags);
    foreach (var r in recs) U16(r.X);
    foreach (var r in recs) U16(r.Z);
    foreach (var r in recs) I32(r.Y);
    foreach (var r in recs) U16(r.Yaw);
    foreach (var r in recs.Where(r => (r.Flags & 1) != 0))
    {
      U8(r.Rotation.Value.Largest);
      U16(r.Rotation.Value.A);
      U16(r.Rotation.Value.B);
      U16(r.Rotation.Value.C);
    }
    foreach (var r in recs.Where(r => (r.Flags & 2) != 0))
      foreach (var s in r.Scale)
        U16(s);
    foreach (var r in recs.Where(r => (r.Flags & 4) != 0))
      U32(r.Id.Value);
    foreach (var r in recs.Where(r => (r.Flags & 8) != 0))
      U16(r.Source.Value);
    foreach (var r in recs.Where(r => (r.Flags & 8) != 0))
      U16(r.ValueSet.Value);
    foreach (var r in recs.Where(r => (r.Flags & 16) != 0))
      U16(r.Seed.Value);
    if (Mask != null) w.Write(Mask);
    if (GroundBytes != null) w.Write(GroundBytes);
    if (Paint != null) w.Write(Paint);
    if (extrasCount)
    {
      U8(Extras.Count);
      foreach (var (tag, data) in Extras)
      {
        U8(tag.Length);
        w.Write(Encoding.ASCII.GetBytes(tag));
        U32((uint)data.Length);
        w.Write(data);
      }
    }
    return w.ToArray();
  }
}

internal sealed class RawFile
{
  public byte[] Magic = Encoding.ASCII.GetBytes("BCPL");
  public ushort Format = 1;
  public ushort HeaderFlags;
  public uint Revision = 1;
  public uint? PaletteCount, ZoneCount;
  public ulong? Placements;
  public uint Reserved;
  public string Producer = "raw test";
  public List<RawEntry> Palette = [];
  // The registry's Deflate stream (HeaderFlags 8 must be set by the caller), or null.
  public byte[] Registry;
  public uint? RegistryLength;
  public List<RawZone> Zones = [];
  public bool FixCrc = true;
  // Bytes to append before the CRC.
  public byte[] Trailing = [];

  public static byte[] Deflate(byte[] raw)
  {
    using var output = new MemoryStream();
    using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
      deflate.Write(raw, 0, raw.Length);
    return output.ToArray();
  }

  public static byte[] Inflate(byte[] block)
  {
    using var input = new MemoryStream(block);
    using var inflate = new DeflateStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    inflate.CopyTo(output);
    return output.ToArray();
  }

  public static uint Crc32(byte[] data, int count)
  {
    uint crc = 0xFFFFFFFFu;
    for (int i = 0; i < count; i++)
    {
      crc ^= data[i];
      for (int k = 0; k < 8; k++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return ~crc;
  }

  // The registry as bytes: `ops` are (number, kind, state, who, bake value sets as lists of (key, int value)).
  public static byte[] RegistryBytes(ushort format, ushort next, params (ushort Number, byte Kind, byte State, string Who, int ValueSets)[] ops)
  {
    var w = new MemoryStream();
    void U8(int v) => w.WriteByte((byte)v);
    void U16(int v) { U8(v & 0xFF); U8((v >> 8) & 0xFF); }
    void U32(uint v) => w.Write(BitConverter.GetBytes(v));
    void Str16(string s) { var b = Encoding.UTF8.GetBytes(s); U16(b.Length); w.Write(b); }
    U16(format);
    U16(next);
    U16(ops.Length);
    foreach (var op in ops)
    {
      U16(op.Number);
      U8(op.Kind);
      U8(op.State);
      w.Write(BitConverter.GetBytes(1_700_000_000L));
      Str16(op.Who);
      for (int i = 0; i < 5; i++)
        w.Write(BitConverter.GetBytes(0f));
      U32(1);
      U32(0);
      U32(0);
      Str16("0.10.4");
      U16(op.ValueSets);
      for (int s = 0; s < op.ValueSets; s++)
      {
        U16(1);
        w.Write(BitConverter.GetBytes(12345));
        U8(3);
        w.Write(BitConverter.GetBytes(7));
      }
    }
    return Deflate(w.ToArray());
  }

  public byte[] Build(out int length)
  {
    var w = new MemoryStream();
    void U8(int v) => w.WriteByte((byte)v);
    void U16(int v) { U8(v & 0xFF); U8((v >> 8) & 0xFF); }
    void U32(uint v) => w.Write(BitConverter.GetBytes(v));
    w.Write(Magic);
    U16(Format);
    U16(HeaderFlags);
    U32(Revision);
    U32(PaletteCount ?? (uint)Palette.Count);
    U32(ZoneCount ?? (uint)Zones.Count);
    w.Write(BitConverter.GetBytes(Placements ?? (ulong)Zones.Sum(z => z.RawBlock != null || z.Deflated != null ? z.IndexCount ?? 0 : z.Records.Count)));
    U32(Reserved);
    U8(Producer.Length);
    w.Write(Encoding.ASCII.GetBytes(Producer));
    foreach (var entry in Palette)
      w.Write(entry.Bytes());
    if (Registry != null)
    {
      U32(RegistryLength ?? (uint)Registry.Length);
      w.Write(Registry);
    }
    var blocks = Zones.Select(z => z.Deflated ?? Deflate(z.Raw())).ToList();
    long offset = w.Length + Zones.Count * 28L;
    for (int i = 0; i < Zones.Count; i++)
    {
      var z = Zones[i];
      U16(z.X);
      U16(z.Z);
      U32(z.Offset ?? (uint)offset);
      U32(z.Length ?? (uint)blocks[i].Length);
      U32((uint)(z.IndexCount ?? z.Records.Count));
      U16(z.IndexLive ?? z.Records.Count(r => r.Palette < Palette.Count && Palette[r.Palette].Role == 1));
      U8(z.IndexFlags ?? z.ZoneFlags);
      U8(z.IndexPad ?? 0);
      w.Write(BitConverter.GetBytes(z.YMin));
      w.Write(BitConverter.GetBytes(z.YMax));
      offset += blocks[i].Length;
    }
    foreach (var b in blocks)
      w.Write(b);
    w.Write(Trailing);
    var bytes = w.ToArray();
    var withCrc = new byte[bytes.Length + 4];
    Buffer.BlockCopy(bytes, 0, withCrc, 0, bytes.Length);
    uint crc = Crc32(bytes, bytes.Length);
    if (!FixCrc)
      crc ^= 0xDEADBEEF;
    Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, withCrc, bytes.Length, 4);
    length = withCrc.Length;
    return withCrc;
  }

  public byte[] Build() => Build(out _);
}

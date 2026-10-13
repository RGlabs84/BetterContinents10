// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;

namespace BetterContinents;

// A change of a layer, as the server sends it (spec 5.3): the new header, producer, palette, registry and zone index, and the blocks of the
// zones that changed, so a client that has the old layer can lay out the new one byte for byte. Making one is deterministic (the same two
// layers give the same bytes); applying one to a layer it was not made for, or a damaged one, throws.
//
//   "BCPD"  u16 patch format (1)  u16 0
//   u32 revision of the layer it applies to, u32 revision it makes
//   u32 length of the head, u32 where in the head the zone index starts
//   the head: the new layer's bytes from its start to the end of its zone index
//   u32 blocks; per block: i16 zx, i16 zz, u32 length, bytes
//   u32 CRC-32 of everything before it
internal static class BakedPatch
{
  private const int Format = 1;

  // The patch that turns `from` into `to`. `changed` is what LayerEdit.Build said; every zone whose block differs between the two layers
  // is in the patch whether it is named or not.
  public static byte[] Make(BakedLayer from, BakedLayer to, ZoneKey[] changed)
  {
    var named = new HashSet<ZoneKey>(changed ?? Array.Empty<ZoneKey>());
    var blocks = new List<ZoneRow>();
    foreach (var row in to.Zones)
    {
      if (named.Contains(row.Key) || !from.TryGetZoneRow(row.Key, out var old) || old.Length != row.Length || !SameBytes(from.Bytes, old.Offset, to.Bytes, row.Offset, row.Length))
        blocks.Add(row);
    }
    long size = 32 + to.IndexEnd + 8;
    foreach (var row in blocks)
      size += 8 + row.Length;
    var w = new BakedWriter((int)Math.Min(size + 64, int.MaxValue - 1));
    foreach (char c in "BCPD")
      w.U8(c);
    w.U16(Format);
    w.U16(0);
    w.U32(from.Revision);
    w.U32(to.Revision);
    w.U32((uint)to.IndexEnd);
    w.U32((uint)to.IndexStart);
    w.Bytes(to.Bytes, 0, to.IndexEnd);
    w.U32((uint)blocks.Count);
    foreach (var row in blocks)
    {
      w.I16(row.Key.X);
      w.I16(row.Key.Z);
      w.U32((uint)row.Length);
      w.Bytes(to.Bytes, row.Offset, row.Length);
    }
    uint crc = BakedFormat.Crc32(w.Buffer_, 0, w.Length);
    w.U32(crc);
    return w.ToArray();
  }

  // The layer `patch` makes of `from`. Throws BakedFormatException when the patch is damaged or does not fit `from`; the caller then asks
  // for the whole layer. The id of the result is for the caller to compare with the one the server sent.
  public static BakedLayer Apply(BakedLayer from, byte[] patch)
  {
    try
    {
      return ApplyChecked(from, patch);
    }
    catch (BakedFormatException)
    {
      throw;
    }
    catch (Exception e) when (e is OverflowException || e is ArgumentException || e is IndexOutOfRangeException || e is InvalidOperationException)
    {
      // Whatever the damage did to the numbers it was read as, it is a damaged patch.
      throw new BakedFormatException("it is damaged (" + e.Message + ")", "patch");
    }
  }

  private static BakedLayer ApplyChecked(BakedLayer from, byte[] patch)
  {
    if (patch == null || patch.Length < 40)
      throw new BakedFormatException("the patch is too short", "patch");
    uint stored = (uint)(patch[patch.Length - 4] | (patch[patch.Length - 3] << 8) | (patch[patch.Length - 2] << 16) | (patch[patch.Length - 1] << 24));
    uint actual = BakedFormat.Crc32(patch, 0, patch.Length - 4);
    if (stored != actual)
      throw new BakedFormatException($"the CRC differs: the patch says {stored:X8}, its bytes come to {actual:X8}", "patch");
    var r = new BakedReader(patch, 0, patch.Length - 4) { Where = "patch" };
    if (r.U8() != 'B' || r.U8() != 'C' || r.U8() != 'P' || r.U8() != 'D')
      throw new BakedFormatException("it does not start with BCPD", "patch");
    int format = r.U16();
    if (format != Format)
      throw new BakedFormatException($"its format is {format}, not {Format}", "patch");
    r.U16();
    uint fromRevision = r.U32();
    uint toRevision = r.U32();
    if (fromRevision != from.Revision)
      throw new BakedFormatException($"it applies to revision {fromRevision}, and this layer is revision {from.Revision}", "patch");
    uint headLength = r.U32();
    uint indexStart = r.U32();
    if (headLength > (uint)r.Remaining || indexStart < BakedFormat.HeaderBytes || indexStart > headLength
        || (headLength - indexStart) % BakedFormat.ZoneRowBytes != 0)
      throw new BakedFormatException("its head does not fit", "patch");
    int headAt = r.Pos;
    r.Skip((int)headLength);
    int rows = (int)((headLength - indexStart) / BakedFormat.ZoneRowBytes);
    uint blockCount = r.U32();
    if (blockCount > rows)
      throw new BakedFormatException($"it carries {blockCount} blocks for {rows} zones", "patch");
    var carried = new Dictionary<ZoneKey, (int At, int Length)>();
    for (uint i = 0; i < blockCount; i++)
    {
      var key = new ZoneKey(r.I16(), r.I16());
      uint length = r.U32();
      if (length > (uint)r.Remaining)
        throw new BakedFormatException($"the block of zone {key} runs past the end", "patch");
      if (carried.ContainsKey(key))
        throw new BakedFormatException($"the block of zone {key} is carried twice", "patch");
      carried[key] = (r.Pos, (int)length);
      r.Skip((int)length);
    }
    if (r.Remaining != 0)
      throw new BakedFormatException($"{r.Remaining} bytes are left over", "patch");

    // The new layer: the head, then each zone's block, from the patch or from the layer it applies to, then the CRC.
    long total = headLength + 4;
    var index = new BakedReader(patch, headAt + (int)indexStart, headAt + (int)headLength);
    var sources = new List<(byte[] Bytes, int At, int Length)>(rows);
    for (int i = 0; i < rows; i++)
    {
      var key = new ZoneKey(index.I16(), index.I16());
      index.Skip(4);
      int length = checked((int)index.U32());
      index.Skip(BakedFormat.ZoneRowBytes - 12);
      if (carried.TryGetValue(key, out var block))
      {
        if (block.Length != length)
          throw new BakedFormatException($"the block of zone {key} is {block.Length} bytes, and its row says {length}", "patch");
        sources.Add((patch, block.At, length));
      }
      else
      {
        if (!from.TryGetZoneRow(key, out var old) || old.Length != length)
          throw new BakedFormatException($"zone {key} is not in the patch, and this layer has no block of that length for it", "patch");
        sources.Add((from.Bytes, old.Offset, length));
      }
      total += length;
    }
    if (total > BakedFormat.MaxLayer)
      throw new BakedFormatException("the layer it makes is over 256 MB", "patch");
    var bytes = new byte[total];
    Buffer.BlockCopy(patch, headAt, bytes, 0, (int)headLength);
    int at = (int)headLength;
    foreach (var (source, from_, length) in sources)
    {
      Buffer.BlockCopy(source, from_, bytes, at, length);
      at += length;
    }
    uint crc = BakedFormat.Crc32(bytes, 0, at);
    bytes[at++] = (byte)crc;
    bytes[at++] = (byte)(crc >> 8);
    bytes[at++] = (byte)(crc >> 16);
    bytes[at++] = (byte)(crc >> 24);
    var layer = BakedLayer.Parse(bytes, bytes.Length, from);
    if (layer.Revision != toRevision)
      throw new BakedFormatException($"it says it makes revision {toRevision}, and the layer is revision {layer.Revision}", "patch");
    return layer;
  }

  // Apply without the exception: false and the reason when the patch does not make the layer with that id.
  public static bool TryApply(BakedLayer from, byte[] patch, string expectedId, out BakedLayer? layer, out string problem)
  {
    layer = null;
    problem = "";
    try
    {
      var made = Apply(from, patch);
      if (made.Id != expectedId)
      {
        problem = $"the patch makes layer {made.Id}, and the server has {expectedId}";
        return false;
      }
      layer = made;
      return true;
    }
    catch (BakedFormatException e)
    {
      problem = e.Message;
      return false;
    }
  }

  private static bool SameBytes(byte[] a, int aAt, byte[] b, int bAt, int count)
  {
    for (int i = 0; i < count; i++)
      if (a[aAt + i] != b[bAt + i])
        return false;
    return true;
  }
}

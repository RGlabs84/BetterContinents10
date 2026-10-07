// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// What a snapshot keeps of one zone beyond its row, once its block has been checked: its ground (decoded), the (source, id) of every Live
// record, and how many records each in-game bake owns in it.
internal sealed class ZoneInfo
{
  public readonly ZoneGround? Ground;
  public readonly ulong[] LiveKeys;
  // (source, records), for the records with flag 8.
  public readonly KeyValuePair<ushort, int>[] SourceCounts;

  public ZoneInfo(ZoneGround? ground, ulong[] liveKeys, KeyValuePair<ushort, int>[] sourceCounts)
  {
    Ground = ground;
    LiveKeys = liveKeys;
    SourceCounts = sourceCounts;
  }

  // What a checked block leaves behind. `roles` is the palette's roles: only Live records have to have a unique (source, id).
  public static ZoneInfo Of(ZoneData data, BakedRole[] roles)
  {
    List<ulong>? live = null;
    Dictionary<ushort, int>? counts = null;
    int idAt = 0, sourceAt = 0;
    for (int k = 0; k < data.Count; k++)
    {
      var flags = data.Flags[k];
      uint id = 0;
      int source = 0;
      if ((flags & RecordFlags.Id) != 0)
        id = data.IdRun[idAt++];
      if ((flags & RecordFlags.Source) != 0)
      {
        ushort s = data.SourceRun[sourceAt++];
        source = s;
        counts ??= new Dictionary<ushort, int>();
        counts.TryGetValue(s, out int n);
        counts[s] = n + 1;
      }
      if (roles[data.Palette[k]] == BakedRole.Live)
        (live ??= new List<ulong>()).Add(BakedFormat.LiveKey(source, id));
    }
    return new ZoneInfo(data.Ground, live == null ? Array.Empty<ulong>() : live.ToArray(),
      counts == null ? Array.Empty<KeyValuePair<ushort, int>>() : counts.ToArray());
  }
}

// A layer of baked placements: one immutable snapshot of a placements.bcp as format 1 holds it (spec 2). Reading checks all of it once
// (spec 2.2); after that any thread may ask anything of it. A zone's records and paint are inflated when asked (Decode). Changes are made
// with Edit(), which writes a new layer and leaves this one alone.
internal sealed class BakedLayer
{
  private readonly byte[] bytes;
  private readonly int length;
  private readonly PaletteEntry[] palette;
  private readonly ZoneRow[] rows;
  private readonly ZoneInfo[] infos;
  private readonly Dictionary<ZoneKey, int> rowOf;
  private readonly int indexStart;
  private readonly int indexEnd;
  private string? id;

  public uint Revision { get; }
  public string Producer { get; }
  public HeaderFlags Flags { get; }
  // The records in every zone.
  public long Placements { get; }
  public Registry Registry { get; }
  // Whether the file has a registry (header flag 8); Registry is the empty one when it does not.
  public bool HasRegistry { get; }
  public GroundSet Ground { get; }

  // The layer's bytes: Length of them are the file. Shared with the settings, the cache and the push code: nothing writes to them.
  public byte[] Bytes => bytes;
  public int Length => length;
  public IReadOnlyList<PaletteEntry> Palette => palette;
  public IReadOnlyList<ZoneRow> Zones => rows;

  // Where the zone index starts and ends in the file (the blocks follow it), for patches.
  internal int IndexStart => indexStart;
  internal int IndexEnd => indexEnd;
  internal ZoneInfo InfoOf(int index) => infos[index];

  // The id a layer goes by in caches and on the wire: the first 32 hex digits of the SHA-512 of its bytes, as a settings package's.
  public string Id
  {
    get
    {
      var known = id;
      if (known != null)
        return known;
      return id = BetterContinents.ZNetPatch.WorldCache.PackageID(bytes, length);
    }
  }

  private BakedLayer(byte[] bytes, int length, uint revision, string producer, HeaderFlags flags, long placements, PaletteEntry[] palette, Registry registry,
    bool hasRegistry, ZoneRow[] rows, ZoneInfo[] infos, int indexStart, int indexEnd)
  {
    this.bytes = bytes;
    this.length = length;
    Revision = revision;
    Producer = producer;
    Flags = flags;
    Placements = placements;
    this.palette = palette;
    Registry = registry;
    HasRegistry = hasRegistry;
    this.rows = rows;
    this.infos = infos;
    this.indexStart = indexStart;
    this.indexEnd = indexEnd;
    rowOf = new Dictionary<ZoneKey, int>(rows.Length);
    var ground = new Dictionary<ZoneKey, ZoneGround>();
    for (int i = 0; i < rows.Length; i++)
    {
      rowOf[rows[i].Key] = i;
      if (infos[i].Ground != null)
        ground[rows[i].Key] = infos[i].Ground!;
    }
    Ground = ground.Count == 0 ? GroundSet.Empty : new GroundSet(ground);
  }

  // ------------------------------------------------------------------------------------------------ asking

  public bool TryGetZoneRow(int zx, int zz, out ZoneRow row)
  {
    if (rowOf.TryGetValue(new ZoneKey(zx, zz), out int i))
    {
      row = rows[i];
      return true;
    }
    row = default;
    return false;
  }

  public bool TryGetZoneRow(ZoneKey zone, out ZoneRow row) => TryGetZoneRow(zone.X, zone.Z, out row);

  public bool Has(ZoneKey zone) => rowOf.ContainsKey(zone);

  // The records and sections of a zone, inflated from the layer's bytes and decoded. Any thread; a new ZoneData each time.
  public ZoneData Decode(in ZoneRow row)
  {
    var scratch = Scratch ??= new byte[16384];
    int inflated = BakedFormat.Inflate(bytes, row.Offset, row.Length, ref scratch, BakedFormat.MaxInflated, "zone " + row.Key);
    Scratch = scratch;
    var context = new BlockContext(palette, HasRegistry ? Registry : null);
    return BakedCodec.ParseBlock(scratch, inflated, row.Key, row.Placements, row.Flags, context, out _);
  }

  [ThreadStatic] private static byte[]? Scratch;

  // Every record of the layer in zone order, decoded one zone at a time (the tests, `bc_bake check`, the exporters).
  public IEnumerable<ZoneRecord> AllRecords()
  {
    for (int i = 0; i < rows.Length; i++)
    {
      if (rows[i].Placements == 0)
        continue;
      var data = Decode(rows[i]);
      for (int k = 0; k < data.Count; k++)
        yield return data.Record(k);
    }
  }

  // How many records an in-game bake owns (0: the compiler's file), counted when the layer was read.
  public long RecordsOfSource(int source)
  {
    long total = 0;
    if (source == 0)
    {
      long baked = 0;
      for (int i = 0; i < rows.Length; i++)
        foreach (var kv in infos[i].SourceCounts)
          baked += kv.Value;
      return Placements - baked;
    }
    for (int i = 0; i < rows.Length; i++)
      foreach (var kv in infos[i].SourceCounts)
        if (kv.Key == source)
          total += kv.Value;
    return total;
  }

  // Every source that has records here, with how many (the compiler's file as source 0).
  public SortedDictionary<int, long> RecordsBySource()
  {
    var all = new SortedDictionary<int, long>();
    long baked = 0;
    for (int i = 0; i < rows.Length; i++)
      foreach (var kv in infos[i].SourceCounts)
      {
        all.TryGetValue(kv.Key, out long n);
        all[kv.Key] = n + kv.Value;
        baked += kv.Value;
      }
    if (Placements - baked > 0)
      all[0] = Placements - baked;
    return all;
  }

  // ------------------------------------------------------------------------------------------------ reading

  // Reads and checks a layer: the first `length` bytes of `bytes` are the file. Throws BakedFormatException, naming the first problem and
  // where it is (spec 2.2). The layer keeps `bytes`: nothing may write to them afterwards.
  public static BakedLayer Read(byte[] bytes, int length) => Parse(bytes, length, null);

  // As Read; when `trusted` is a layer this one grows from (its palette and registry only grew), a zone whose block is the same bytes is
  // not inflated and checked again: what a patch or an edit makes is read in time for the zones it touched.
  internal static BakedLayer Parse(byte[] bytes, int length, BakedLayer? trusted)
  {
    if (bytes == null)
      throw new ArgumentNullException(nameof(bytes));
    if (length > bytes.Length || length < 0)
      throw new ArgumentOutOfRangeException(nameof(length));
    if (length < BakedFormat.MinimumBytes)
      throw new BakedFormatException($"the file has {length} bytes, under the {BakedFormat.MinimumBytes} of a header and a CRC");
    if (length > BakedFormat.MaxLayer)
      throw new BakedFormatException($"the layer has {length:N0} bytes, over the {BakedFormat.MaxLayer / (1024 * 1024)} MB format 1 allows");
    uint stored = (uint)(bytes[length - 4] | (bytes[length - 3] << 8) | (bytes[length - 2] << 16) | (bytes[length - 1] << 24));
    uint actual = BakedFormat.Crc32(bytes, 0, length - 4);
    if (stored != actual)
      throw new BakedFormatException($"the CRC differs: the file says {stored:X8}, its bytes come to {actual:X8}");

    var r = new BakedReader(bytes, 0, length - 4) { Where = "header" };
    if (r.U8() != 'B' || r.U8() != 'C' || r.U8() != 'P' || r.U8() != 'L')
      throw new BakedFormatException("the file does not start with BCPL", "header");
    int format = r.U16();
    if (format != BakedFormat.FormatVersion)
      throw new BakedFormatException($"the format is {format}, not {BakedFormat.FormatVersion} (made by a newer Better Continents or tool?)", "header");
    int flagBits = r.U16();
    if ((flagBits & ~(int)HeaderFlags.All) != 0)
      throw new BakedFormatException($"made by a newer Better Continents or tool: header bit {BakedCodec.LowestUnknownBit(flagBits, (int)HeaderFlags.All)}", "header");
    var flags = (HeaderFlags)flagBits;
    uint revision = r.U32();
    uint paletteCount = r.U32();
    uint zoneCount = r.U32();
    ulong placements = r.U64();
    uint reserved = r.U32();
    if (reserved != 0)
      throw new BakedFormatException($"the reserved word is {reserved}, not 0", "header");
    if (paletteCount > BakedFormat.MaxPalette)
      throw new BakedFormatException($"the palette has {paletteCount} entries, over the {BakedFormat.MaxPalette} format 1 holds", "header");

    r.Where = "producer";
    string producer = r.Str();
    var palette = BakedCodec.ReadPalette(r, (int)paletteCount);
    var scratch = Scratch ??= new byte[16384];
    Registry registry = Registry.Empty;
    bool hasRegistry = (flags & HeaderFlags.Registry) != 0;
    if (hasRegistry)
      registry = BakedCodec.ReadRegistry(r, ref scratch);

    // The zone index.
    r.Where = "zone index";
    int indexStart = r.Pos;
    if ((long)zoneCount * BakedFormat.ZoneRowBytes > r.Remaining)
      throw new BakedFormatException($"the index of {zoneCount} zones runs past the end of the file", "zone index");
    int zones = (int)zoneCount;
    var rows = new ZoneRow[zones];
    int indexEnd = indexStart + zones * BakedFormat.ZoneRowBytes;
    ZoneKey previous = default;
    for (int i = 0; i < zones; i++)
    {
      int zx = r.I16(), zz = r.I16();
      uint offset = r.U32(), size = r.U32(), count = r.U32();
      int live = r.U16();
      int zoneFlagBits = r.U8();
      int pad = r.U8();
      float yMin = r.F32(), yMax = r.F32();
      var key = new ZoneKey(zx, zz);
      string where = "zone " + key;
      if (!key.InRange)
        throw new BakedFormatException($"the zone is outside {BakedFormat.FirstZone} to {BakedFormat.LastZone}", where);
      if (i > 0 && key.CompareTo(previous) <= 0)
        throw new BakedFormatException($"the rows of the index are not in strictly increasing order (after zone {previous})", where);
      previous = key;
      if ((zoneFlagBits & ~(int)ZoneFlags.All) != 0)
        throw new BakedFormatException($"made by a newer Better Continents or tool: zone bit {BakedCodec.LowestUnknownBit(zoneFlagBits, (int)ZoneFlags.All)}", where);
      if (pad != 0)
        throw new BakedFormatException($"the byte after the flags is {pad}, not 0", where);
      if (offset < (uint)indexEnd || (long)offset + size > length - 4)
        throw new BakedFormatException($"its block ({size} bytes at {offset}) is not inside the file's blocks ({indexEnd} to {length - 4})", where);
      if (count > int.MaxValue)
        throw new BakedFormatException($"it holds {count} records", where);
      rows[i] = new ZoneRow(key, i, (int)offset, (int)size, (int)count, live, (ZoneFlags)zoneFlagBits, yMin, yMax, null);
    }

    // The blocks: each inflated and checked once, unless the layer this one grows from has the same bytes.
    var context = new BlockContext(palette, hasRegistry ? registry : null);
    bool grows = trusted != null && GrowsFrom(trusted, palette, registry, hasRegistry);
    var infos = new ZoneInfo[zones];
    long total = 0;
    var liveSeen = new HashSet<ulong>();
    try
    {
      for (int i = 0; i < zones; i++)
      {
        var row = rows[i];
        string where = "zone " + row.Key;
        ZoneInfo info;
        byte[]? mask;
        if (grows && trusted!.TryGetZoneRow(row.Key, out var known) && known.Length == row.Length && known.Flags == row.Flags && known.Placements == row.Placements
            && known.Live == row.Live && SameBytes(trusted.bytes, known.Offset, bytes, row.Offset, row.Length))
        {
          info = trusted.infos[known.Index];
          mask = known.ClearMask;
        }
        else
        {
          int inflated = BakedFormat.Inflate(bytes, row.Offset, row.Length, ref scratch, BakedFormat.MaxInflated, where);
          var data = BakedCodec.ParseBlock(scratch, inflated, row.Key, row.Placements, row.Flags, context, out int live);
          if (live != row.Live)
            throw new BakedFormatException($"it holds {live} Live records, the index says {row.Live}", where);
          info = ZoneInfo.Of(data, context.Roles);
          mask = data.ClearMask;
        }
        infos[i] = info;
        rows[i] = new ZoneRow(row.Key, row.Index, row.Offset, row.Length, row.Placements, row.Live, row.Flags, row.YMin, row.YMax, mask);
        total += row.Placements;
        foreach (var key in info.LiveKeys)
          if (!liveSeen.Add(key))
            throw new BakedFormatException($"a Live record repeats an id already in the layer (source {(int)(key >> 32)}, id {(uint)key})", where);
      }
    }
    finally
    {
      Scratch = scratch;
    }
    if (total != (long)placements)
      throw new BakedFormatException($"the zones hold {total} records, the header says {placements}", "totals");
    return new BakedLayer(bytes, length, revision, producer, flags, total, palette, registry, hasRegistry, rows, infos, indexStart, indexEnd);
  }

  // Whether a layer made from `trusted` by appending to its palette and registry can keep what was checked of its blocks.
  private static bool GrowsFrom(BakedLayer trusted, PaletteEntry[] palette, Registry registry, bool hasRegistry)
  {
    if (palette.Length < trusted.palette.Length)
      return false;
    for (int i = 0; i < trusted.palette.Length; i++)
      if (!palette[i].Equals(trusted.palette[i]))
        return false;
    if (trusted.HasRegistry && !hasRegistry)
      return false;
    foreach (var old in trusted.Registry.Operations)
    {
      if (!registry.TryGet(old.Number, out var now) || now.IsBake != old.IsBake || now.ValueSets.Length < old.ValueSets.Length)
        return false;
    }
    return true;
  }

  private static bool SameBytes(byte[] a, int aAt, byte[] b, int bAt, int count)
  {
    for (int i = 0; i < count; i++)
      if (a[aAt + i] != b[bAt + i])
        return false;
    return true;
  }

  // ------------------------------------------------------------------------------------------------ changing

  // A new layer from this one (spec 2.6): see LayerEdit.
  public LayerEdit Edit() => new(this);

  public override string ToString() => $"layer r{Revision}: {Placements:N0} records in {rows.Length} zones, {palette.Length} palette entries, {Length:N0} bytes";
}

// The changes to a layer, collected, and written as a new one (spec 2.6, 6.2, 10): palette entries are only ever appended, records are
// added and removed by value, a compiler's new file replaces source 0, and Build writes the layer: the zones it touched are encoded
// again and every other block is copied byte for byte, the revision is the old one plus 1, and the zones whose look changed come back
// with it. One edit builds one layer; make another with Edit() for the next change.
internal sealed partial class LayerEdit
{
  private readonly BakedLayer? basis;

  internal LayerEdit(BakedLayer? basis)
  {
    this.basis = basis;
  }

  // A layer made in game where there was none: the first change makes revision 1.
  public static LayerEdit New(string producer) => throw new NotImplementedException();

  public BakedLayer? Basis => basis;

  // The number the next operation takes (a bake's number is also its records' source). Peek before making records; AddOperation takes it.
  public ushort NextOperationNumber => throw new NotImplementedException();

  // A number the journal on disk has used for an operation that never reached a layer: the next one is above it, and none is reused.
  public void EnsureNextOperation(int atLeast) => throw new NotImplementedException();

  // The palette index of an entry: an equal entry's, or a new one appended after the last (indices never move). Throws past 65,535.
  public int PaletteIndexFor(PaletteEntry entry) => throw new NotImplementedException();

  // Adds records (each in the zone it names, with its palette index from PaletteIndexFor). `height`: how far above its y a record
  // reaches, for the zone's y bounds (0 when not known).
  public void AddRecords(IEnumerable<ZoneRecord> records, float height = 0f) => throw new NotImplementedException();

  // Removes records by value, one for each given; returns how many were found (a record that is not there is not an error).
  public int RemoveRecords(IEnumerable<ZoneRecord> records) => throw new NotImplementedException();

  // Removes the records of the given zones (all zones when null) that the test accepts; returns how many.
  public int RemoveWhere(IEnumerable<ZoneKey>? zones, Func<ZoneRecord, bool> test) => throw new NotImplementedException();

  // Removes every record of an in-game bake, in every zone; returns how many.
  public int RemoveSource(int source) => throw new NotImplementedException();

  // `bc_bake load` (spec 6.2): the file's compiler records and zone sections replace the layer's; every in-game record stays; the palette
  // is rebuilt (the file's entries in its order, then the entries in-game records still use, in first-use order). Returns the records of
  // the file that an in-game bake owns, which are left out.
  public int ReplaceSource0(BakedLayer file) => throw new NotImplementedException();

  // Adds an operation to the registry; its number must be NextOperationNumber.
  public void AddOperation(OperationInfo operation) => throw new NotImplementedException();

  // Marks an operation undone (or in the layer again), and drops its value sets when it is undone.
  public void SetOperationState(int number, OperationState state) => throw new NotImplementedException();

  // Writes the layer. Changed: the zones to rebuild on a client (their block, sections or palette entries differ), by (z, x).
  public (BakedLayer Layer, ZoneKey[] Changed) Build() => throw new NotImplementedException();
}

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

  // The layer a preset keeps (spec 3.3): the compiler's records and sections, without the records baked in game and the registry. This one
  // itself when there are none of either. `dropped` is how many in-game records are left out.
  public BakedLayer WithoutInGame(out int dropped)
  {
    dropped = (int)(Placements - RecordsOfSource(0));
    if (dropped == 0 && !HasRegistry)
      return this;
    var edit = Edit();
    foreach (var source in RecordsBySource().Keys.Where(k => k != 0).ToList())
      edit.RemoveSource(source);
    edit.DropRegistry();
    return edit.Build().Layer;
  }

  public override string ToString() => $"layer r{Revision}: {Placements:N0} records in {rows.Length} zones, {palette.Length} palette entries, {Length:N0} bytes";
}

// The changes to a layer, collected, and written as a new one (spec 2.6, 6.2, 10): palette entries are only ever appended, records are
// added and removed by value, a compiler's new file replaces source 0, and Build writes the layer: the zones it touched are encoded
// again and every other block is copied byte for byte, the revision is the old one plus 1, and the zones whose look changed come back
// with it. One edit builds one layer; make another with Edit() for the next change. Not for several threads at once.
internal sealed class LayerEdit
{
  // What an edit knows of one zone it was asked about: a block to copy while nothing has touched it, or the zone decoded and changed.
  private sealed class ZoneWork
  {
    public readonly ZoneKey Key;
    // While the zone is still a copy of a block: the layer it is in and its row there.
    public BakedLayer? Source;
    public int SourceRow = -1;
    // A file's zone: its records that an in-game bake owns are left out when it is decoded.
    public bool DropInGame;
    public List<ZoneRecord>? Records;
    public ZoneSections Sections = new();
    // The records' y and the pieces' heights, in m; valid with HasBounds (the zone has records).
    public double YMin, YMax;
    public bool HasBounds;

    public ZoneWork(ZoneKey key)
    {
      Key = key;
    }

    public bool IsCopy => Records == null && !DropInGame;

    public void Materialize()
    {
      if (Records != null)
        return;
      if (Source == null)
      {
        Records = new List<ZoneRecord>();
        return;
      }
      var row = Source.Zones[SourceRow];
      var data = Source.Decode(row);
      Records = new List<ZoneRecord>(data.Count);
      for (int k = 0; k < data.Count; k++)
      {
        var r = data.Record(k);
        if (DropInGame && r.HasSource)
          continue;
        Records.Add(r);
      }
      Sections = ZoneSections.Of(data);
      HasBounds = row.Placements > 0;
      YMin = row.YMin;
      YMax = row.YMax;
      Source = null;
    }

    // The zone's bounds take in a record at y (m) that reaches `height` above it.
    public void Grow(double y, double height)
    {
      if (!HasBounds)
      {
        YMin = y;
        YMax = y + height;
        HasBounds = true;
        return;
      }
      if (y < YMin) YMin = y;
      if (y + height > YMax) YMax = y + height;
    }

    // A conservative union with a row's bounds (a zone that gets records from two layers).
    public void Grow(ZoneRow row)
    {
      if (row.Placements == 0)
        return;
      if (!HasBounds)
      {
        YMin = row.YMin;
        YMax = row.YMax;
        HasBounds = true;
        return;
      }
      if (row.YMin < YMin) YMin = row.YMin;
      if (row.YMax > YMax) YMax = row.YMax;
    }
  }

  // One zone of the layer being written.
  private sealed class FinalZone
  {
    public ZoneKey Key;
    public byte[] Block = Array.Empty<byte>();
    public int Offset, Length;
    public int Count, Live;
    public ZoneFlags Flags;
    public float YMin, YMax;
    // Whether the block is a copy of one in another layer (and which): nothing is compared if it is the basis' own.
    public BakedLayer? CopyOf;
  }

  private readonly BakedLayer? basis;
  private string producer;
  private readonly List<PaletteEntry> palette = new();
  private readonly Dictionary<PaletteEntry, int> paletteIndex = new();
  private readonly List<OperationInfo> operations = new();
  private int nextOperation = 1;
  private bool registryWanted;
  private readonly Dictionary<ZoneKey, ZoneWork> work = new();
  private bool replaced, mutated, built;
  private BakedLayer? file;

  internal LayerEdit(BakedLayer? basis, string? producer = null)
  {
    this.basis = basis;
    this.producer = basis?.Producer ?? producer ?? $"{BakedFormat.ProducerPrefix}{ModInfo.Version} (bc_bake)";
    if (basis != null)
    {
      foreach (var entry in basis.Palette)
      {
        palette.Add(entry);
        if (!paletteIndex.ContainsKey(entry))
          paletteIndex[entry] = palette.Count - 1;
      }
      operations.AddRange(basis.Registry.Operations);
      nextOperation = basis.Registry.NextOperation;
      registryWanted = basis.HasRegistry;
    }
  }

  // A layer made in game where there was none: the first change makes revision 1. The producer reads "Better Continents <version> (bc_bake)"
  // when none is given.
  public static LayerEdit New(string? producer = null) => new(null, producer);

  public BakedLayer? Basis => basis;

  // The number the next operation takes (a bake's number is also its records' source). Peek before making records; AddOperation takes it.
  public ushort NextOperationNumber
  {
    get
    {
      if (nextOperation > ushort.MaxValue)
        throw new InvalidOperationException("the world has used all 65,535 operation numbers");
      return (ushort)nextOperation;
    }
  }

  // Makes n the next operation's number: a number the journal on disk has taken (a new operation, or one rolled forward after a
  // crash). Numbers below n are skipped and never reused. Afterwards NextOperationNumber == n. A counter already past n refuses:
  // when the registry holds operation n it was applied and there is nothing to roll; otherwise n can no longer be used.
  public void EnsureNextOperation(int n)
  {
    if (n < 1 || n > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(n), n, "operation numbers run from 1 to 65,535");
    if (n < nextOperation)
      throw new InvalidOperationException(HasOperation(n)
        ? $"operation {n} is already in the layer (the next is {nextOperation})"
        : $"operation number {n} is already passed (the next is {nextOperation})");
    nextOperation = n;
  }

  // Whether the registry being edited holds operation n.
  public bool HasOperation(int n) => operations.Exists(o => o.Number == n);

  // The palette index of an entry: an equal entry's, or a new one appended after the last (indices never move). Throws past 65,535.
  public int PaletteIndexFor(PaletteEntry entry)
  {
    if (entry == null)
      throw new ArgumentNullException(nameof(entry));
    if (paletteIndex.TryGetValue(entry, out int known))
      return known;
    if (palette.Count >= BakedFormat.MaxPalette)
      throw new BakedFormatException($"the palette would pass {BakedFormat.MaxPalette} entries");
    BakedCodec.Validate(entry, "palette entry " + palette.Count + " (" + entry.Name + ")");
    Mutated();
    palette.Add(entry);
    paletteIndex[entry] = palette.Count - 1;
    return palette.Count - 1;
  }

  private void Mutated()
  {
    if (built)
      throw new InvalidOperationException("an edit builds one layer");
    mutated = true;
  }

  private ZoneWork Work(ZoneKey key, bool materialize)
  {
    if (!work.TryGetValue(key, out var w))
    {
      w = new ZoneWork(key);
      if (!replaced && basis != null && basis.TryGetZoneRow(key, out var row))
      {
        w.Source = basis;
        w.SourceRow = row.Index;
      }
      work[key] = w;
    }
    if (materialize)
      w.Materialize();
    return w;
  }

  // Adds records (each in the zone it names, with its palette index from PaletteIndexFor). `height`: how far above its y a record
  // reaches, for the zone's y bounds (0 when not known).
  public void AddRecords(IEnumerable<ZoneRecord> records, float height = 0f)
  {
    Mutated();
    foreach (var r in records)
    {
      if (r.Palette >= palette.Count)
        throw new BakedFormatException($"a record names palette entry {r.Palette}, and the palette has {palette.Count}", "zone " + r.Zone);
      if (!r.Zone.InRange)
        throw new BakedFormatException($"the zone is outside {BakedFormat.FirstZone} to {BakedFormat.LastZone}", "zone " + r.Zone);
      if ((r.Flags & ~RecordFlags.All) != 0)
        throw new BakedFormatException("a record has a flag format 1 does not know", "zone " + r.Zone);
      var w = Work(r.Zone, true);
      w.Records!.Add(r);
      w.Grow(r.Y / 1000.0, height);
    }
  }

  // Removes records by value, one for each given; returns how many were found (a record that is not there is not an error).
  public int RemoveRecords(IEnumerable<ZoneRecord> records)
  {
    Mutated();
    var wanted = new Dictionary<ZoneKey, Dictionary<ZoneRecord, int>>();
    foreach (var r in records)
    {
      if (!wanted.TryGetValue(r.Zone, out var counts))
        wanted[r.Zone] = counts = new Dictionary<ZoneRecord, int>();
      counts.TryGetValue(r, out int n);
      counts[r] = n + 1;
    }
    int removed = 0;
    foreach (var request in wanted)
    {
      var zone = request.Key;
      var counts = request.Value;
      if (!work.ContainsKey(zone) && (replaced || basis == null || !basis.Has(zone)))
        continue;
      var w = Work(zone, true);
      var kept = new List<ZoneRecord>(w.Records!.Count);
      foreach (var r in w.Records!)
      {
        if (counts.TryGetValue(r, out int left) && left > 0)
        {
          counts[r] = left - 1;
          removed++;
        }
        else
          kept.Add(r);
      }
      w.Records = kept;
    }
    return removed;
  }

  // Removes the records of the given zones (all zones when null) that the test accepts; returns how many.
  public int RemoveWhere(IEnumerable<ZoneKey>? zones, Func<ZoneRecord, bool> test)
  {
    Mutated();
    IEnumerable<ZoneKey> keys = zones ?? AllZones();
    int removed = 0;
    foreach (var zone in keys.Distinct().ToList())
    {
      if (!work.ContainsKey(zone) && (replaced || basis == null || !basis.Has(zone)))
        continue;
      var w = Work(zone, true);
      int before = w.Records!.Count;
      if (before == 0)
        continue;
      w.Records = w.Records.Where(r => !test(r)).ToList();
      removed += before - w.Records.Count;
    }
    return removed;
  }

  // Every zone this edit has, as the layer it builds will have it: the basis' and the ones the edit made.
  private IEnumerable<ZoneKey> AllZones()
  {
    var keys = new HashSet<ZoneKey>(work.Keys);
    if (!replaced && basis != null)
      foreach (var row in basis.Zones)
        keys.Add(row.Key);
    return keys;
  }

  // Removes every record of an in-game bake, in every zone; returns how many.
  public int RemoveSource(int source)
  {
    if (source <= 0 || source > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(source), "an in-game bake is 1 to 65535");
    // Only the zones the layer says hold records of that bake, and the ones already changed.
    var zones = new HashSet<ZoneKey>(work.Keys);
    if (!replaced && basis != null)
      for (int i = 0; i < basis.Zones.Count; i++)
        foreach (var kv in basis.InfoOf(i).SourceCounts)
          if (kv.Key == source)
            zones.Add(basis.Zones[i].Key);
    return RemoveWhere(zones, r => r.HasSource && r.Source == source);
  }

  // `bc_bake load` (spec 6.2): the file's compiler records and zone sections replace the layer's; every in-game record stays; the palette
  // is rebuilt (the file's entries in its order, then the entries in-game records still use, in first-use order). Returns the records of
  // the file that an in-game bake owns, which are left out. It comes before any other change of the edit.
  public int ReplaceSource0(BakedLayer fileLayer)
  {
    if (mutated || replaced)
      throw new InvalidOperationException("ReplaceSource0 comes before any other change");
    Mutated();
    replaced = true;
    file = fileLayer;
    producer = fileLayer.Producer;
    palette.Clear();
    paletteIndex.Clear();
    foreach (var entry in fileLayer.Palette)
    {
      palette.Add(entry);
      if (!paletteIndex.ContainsKey(entry))
        paletteIndex[entry] = palette.Count - 1;
    }
    int leftOut = 0;
    foreach (var row in fileLayer.Zones)
    {
      var sources = fileLayer.InfoOf(row.Index).SourceCounts;
      var w = new ZoneWork(row.Key) { Source = fileLayer, SourceRow = row.Index, DropInGame = sources.Length > 0 };
      foreach (var kv in sources)
        leftOut += kv.Value;
      work[row.Key] = w;
    }
    // The in-game records of the layer being replaced stay, on the new palette.
    if (basis != null)
      for (int i = 0; i < basis.Zones.Count; i++)
      {
        var row = basis.Zones[i];
        if (basis.InfoOf(i).SourceCounts.Length == 0)
          continue;
        var data = basis.Decode(row);
        var w = Work(row.Key, true);
        for (int k = 0; k < data.Count; k++)
        {
          var r = data.Record(k);
          if (!r.HasSource)
            continue;
          r.Palette = (ushort)PaletteIndexFor(basis.Palette[r.Palette]);
          w.Records!.Add(r);
        }
        w.Grow(row);
      }
    return leftOut;
  }

  // For tools and tests that make a compiler's layer: sets a zone's sections (its clear mask, ground, paint and extras, and the two flags the
  // sections do not decide). In-game operations write no section.
  internal void SetSections(ZoneKey zone, ZoneSections sections)
  {
    Mutated();
    Work(zone, true).Sections = sections;
  }

  // For tests: the blocks of every zone are encoded again instead of copied.
  internal void ReencodeAll()
  {
    Mutated();
    foreach (var key in AllZones().ToList())
      Work(key, true);
  }

  // Leaves the registry out of the layer (a preset's: the world made from it has no history). No record may name a bake by then.
  internal void DropRegistry()
  {
    Mutated();
    operations.Clear();
    nextOperation = 1;
    registryWanted = false;
  }

  // Adds an operation to the registry; its number must be NextOperationNumber.
  public void AddOperation(OperationInfo operation)
  {
    if (operation == null)
      throw new ArgumentNullException(nameof(operation));
    if (operation.Number != NextOperationNumber)
      throw new ArgumentException($"the next operation is number {NextOperationNumber}, not {operation.Number}", nameof(operation));
    Mutated();
    operations.Add(operation);
    nextOperation++;
    registryWanted = true;
  }

  // Marks an operation undone (or in the layer again), and drops its value sets when it is undone.
  public void SetOperationState(int number, OperationState state)
  {
    Mutated();
    int at = operations.FindIndex(o => o.Number == number);
    if (at < 0)
      throw new ArgumentException($"there is no operation {number}", nameof(number));
    var op = operations[at].WithState(state);
    operations[at] = state == OperationState.Undone ? op.WithValueSets(Array.Empty<ValueSet>()) : op;
  }

  // ------------------------------------------------------------------------------------------------ writing

  // Writes the layer. Changed: the zones to rebuild on a client (their block, sections or palette entries differ), by (z, x).
  public (BakedLayer Layer, ZoneKey[] Changed) Build()
  {
    if (built)
      throw new InvalidOperationException("an edit builds one layer");
    built = true;
    var roles = new BakedRole[palette.Count];
    for (int i = 0; i < roles.Length; i++)
      roles[i] = palette[i].Role;

    // The zones, in the index's order.
    var keys = new List<ZoneKey>(replaced ? work.Keys : AllZones());
    keys.Sort();
    var zones = new List<FinalZone>(keys.Count);
    foreach (var key in keys)
    {
      var zone = Finish(key, roles);
      if (zone != null)
        zones.Add(zone);
    }

    // The registry keeps at most 1,000 operations; older ones that no record points to go.
    var kept = TrimOperations(zones);
    bool writeRegistry = registryWanted || kept.Count > 0;
    var registry = new Registry((ushort)Math.Min(nextOperation, ushort.MaxValue), kept.ToArray());

    var flags = HeaderFlags.None;
    long placements = 0;
    foreach (var zone in zones)
    {
      placements += zone.Count;
      if ((zone.Flags & ZoneFlags.Ground) != 0) flags |= HeaderFlags.Ground;
      if ((zone.Flags & ZoneFlags.Paint) != 0) flags |= HeaderFlags.Paint;
      if ((zone.Flags & ZoneFlags.ClearMask) != 0) flags |= HeaderFlags.ClearMask;
    }
    if (writeRegistry)
      flags |= HeaderFlags.Registry;
    uint revision = basis == null ? 1u : basis.Revision + 1;

    var bytes = Assemble(revision, flags, placements, registry, writeRegistry, zones);
    var layer = BakedLayer.Parse(bytes, bytes.Length, replaced ? file : basis);
    return (layer, ChangedZones(layer, zones));
  }

  // One zone of the layer being written: a block copied from the layer it was in, or the zone's records encoded again. Null when the zone
  // has nothing left (no records, no sections, no flags): it leaves the index.
  private FinalZone? Finish(ZoneKey key, BakedRole[] roles)
  {
    ZoneWork? w = null;
    work.TryGetValue(key, out w);
    if (w == null || w.IsCopy)
    {
      BakedLayer source;
      ZoneRow row;
      if (w != null)
      {
        source = w.Source!;
        row = source.Zones[w.SourceRow];
      }
      else
      {
        source = basis!;
        source.TryGetZoneRow(key, out row);
      }
      return new FinalZone
      {
        Key = key, Block = source.Bytes, Offset = row.Offset, Length = row.Length, Count = row.Placements, Live = row.Live, Flags = row.Flags,
        YMin = row.YMin, YMax = row.YMax, CopyOf = source,
      };
    }
    w.Materialize();
    var records = w.Records!;
    if (records.Count == 0 && w.Sections.IsEmpty)
      return null;
    records.Sort();
    int live = 0;
    foreach (var r in records)
      if (roles[r.Palette] == BakedRole.Live)
        live++;
    var raw = BakedCodec.EncodeBlockRaw(records, w.Sections, out int rawLength, out var zoneFlags);
    var block = BakedFormat.Deflate(raw, rawLength);
    return new FinalZone
    {
      Key = key, Block = block, Offset = 0, Length = block.Length, Count = records.Count, Live = live, Flags = zoneFlags,
      YMin = records.Count > 0 && w.HasBounds ? (float)w.YMin : 0f, YMax = records.Count > 0 && w.HasBounds ? (float)w.YMax : 0f,
    };
  }

  // The operations to write: all, or the newest 1,000 and every older one that a record still points to.
  private List<OperationInfo> TrimOperations(List<FinalZone> zones)
  {
    if (operations.Count <= BakedFormat.MaxOperations)
      return operations;
    var inUse = new HashSet<int>();
    foreach (var zone in zones)
    {
      // A copied block's bakes are known from its layer's index; an encoded one's from its records.
      if (zone.CopyOf != null)
      {
        if (zone.CopyOf.TryGetZoneRow(zone.Key, out var row))
          foreach (var kv in zone.CopyOf.InfoOf(row.Index).SourceCounts)
            inUse.Add(kv.Key);
      }
      else if (work.TryGetValue(zone.Key, out var w) && w.Records != null)
        foreach (var r in w.Records)
          if (r.HasSource)
            inUse.Add(r.Source);
    }
    var kept = new List<OperationInfo>(operations);
    for (int i = 0; i < kept.Count && kept.Count > BakedFormat.MaxOperations;)
    {
      if (inUse.Contains(kept[i].Number))
        i++;
      else
        kept.RemoveAt(i);
    }
    return kept;
  }

  private byte[] Assemble(uint revision, HeaderFlags flags, long placements, Registry registry, bool writeRegistry, List<FinalZone> zones)
  {
    long estimate = 4096 + zones.Count * (long)BakedFormat.ZoneRowBytes;
    foreach (var zone in zones)
      estimate += zone.Length;
    if (estimate > BakedFormat.MaxLayer)
      throw new BakedFormatException($"the layer would be about {estimate / (1024 * 1024)} MB, over the {BakedFormat.MaxLayer / (1024 * 1024)} MB format 1 allows");
    var w = new BakedWriter((int)Math.Min(estimate + 65536, int.MaxValue - 1));
    foreach (char c in BakedFormat.Magic)
      w.U8(c);
    w.U16(BakedFormat.FormatVersion);
    w.U16((int)flags);
    w.U32(revision);
    w.U32((uint)palette.Count);
    w.U32((uint)zones.Count);
    w.U64((ulong)placements);
    w.U32(0);
    w.Str(producer);
    BakedCodec.WritePalette(w, palette);
    if (writeRegistry)
      BakedCodec.WriteRegistry(w, registry);
    long offset = w.Length + zones.Count * (long)BakedFormat.ZoneRowBytes;
    foreach (var zone in zones)
    {
      BakedCodec.WriteIndexRow(w, zone.Key, checked((int)offset), zone.Length, zone.Count, zone.Live, zone.Flags, zone.YMin, zone.YMax);
      offset += zone.Length;
    }
    foreach (var zone in zones)
      w.Bytes(zone.Block, zone.Offset, zone.Length);
    uint crc = BakedFormat.Crc32(w.Buffer_, 0, w.Length);
    w.U32(crc);
    if (w.Length > BakedFormat.MaxLayer)
      throw new BakedFormatException($"the layer is {w.Length / (1024 * 1024)} MB, over the {BakedFormat.MaxLayer / (1024 * 1024)} MB format 1 allows");
    return w.ToArray();
  }

  // The zones whose look differs from the basis': new, gone, or with another block, other sections, or a palette entry that changed.
  private ZoneKey[] ChangedZones(BakedLayer layer, List<FinalZone> zones)
  {
    var changed = new List<ZoneKey>();
    if (basis == null)
    {
      foreach (var zone in zones)
        changed.Add(zone.Key);
      return changed.ToArray();
    }
    // Entries that are not what the basis' palette held at their index (only a palette that was rebuilt can have them).
    bool[]? differs = null;
    for (int i = 0; i < basis.Palette.Count; i++)
    {
      if (i < layer.Palette.Count && basis.Palette[i].Equals(layer.Palette[i]))
        continue;
      differs ??= new bool[basis.Palette.Count];
      differs[i] = true;
    }
    foreach (var zone in zones)
    {
      if (!basis.TryGetZoneRow(zone.Key, out var old))
      {
        changed.Add(zone.Key);
        continue;
      }
      if (ReferenceEquals(zone.CopyOf, basis))
        continue;
      layer.TryGetZoneRow(zone.Key, out var now);
      if (now.Length != old.Length || now.Flags != old.Flags || now.Placements != old.Placements || now.Live != old.Live
          || !SameBytes(basis.Bytes, old.Offset, layer.Bytes, now.Offset, now.Length))
      {
        changed.Add(zone.Key);
        continue;
      }
      if (differs != null && UsesAny(basis, old, differs))
        changed.Add(zone.Key);
    }
    // A zone the edit took out of the index.
    var present = new HashSet<ZoneKey>(zones.Select(z => z.Key));
    foreach (var row in basis.Zones)
      if (!present.Contains(row.Key))
        changed.Add(row.Key);
    changed.Sort();
    return changed.ToArray();
  }

  private static bool SameBytes(byte[] a, int aAt, byte[] b, int bAt, int count)
  {
    for (int i = 0; i < count; i++)
      if (a[aAt + i] != b[bAt + i])
        return false;
    return true;
  }

  // Whether a zone's records use any of the marked palette entries.
  private static bool UsesAny(BakedLayer layer, ZoneRow row, bool[] marked)
  {
    if (row.Placements == 0)
      return false;
    var scratch = new byte[Math.Max(4096, row.Length * 4)];
    int n = BakedFormat.Inflate(layer.Bytes, row.Offset, row.Length, ref scratch, BakedFormat.MaxInflated, "zone " + row.Key);
    var r = new BakedReader(scratch, 4, n);
    for (int k = 0; k < row.Placements; k++)
    {
      int p = r.U16();
      if (p < marked.Length && marked[p])
        return true;
    }
    return false;
  }
}

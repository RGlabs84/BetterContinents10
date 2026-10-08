// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BetterContinents;

// The undo file of an in-game operation, and the rules that settle one a crash or a cut save left half done (10.6, 10.8).
//
// Three stores hold a bake (10.8): the journal (J), on disk before the layer changes; the world's settings file with the layer (S),
// written by a world save after the objects; and the world's objects (W), written by the same save before S. A save can happen at
// any step, and a crash or a power cut can leave S and W from different moments. The journal makes every such state recoverable:
// it holds everything the operation took out of the world (the whole ZDO of every static it removed) and everything it put into
// the layer. At the next world load, BakeCrash.Plan says what finishes the operation, and each case leaves every piece in one
// place, never in neither.

// "op-<n>.state": where an operation is, one word, rewritten before each step's changes (so the file is never behind the world).
internal enum BakeState
{
  // The journal is written; nothing else has changed.
  Prepared,
  // The layer holds the new records (in memory; a save writes it).
  Applied,
  // The statics are being removed.
  Removing,
  // Every static is removed.
  Removed,
  // An unbake is making its pieces, and then taking its records out.
  Unbaking,
  // Done; the operation can still be undone.
  Settled,
  // A crash or a cut save stopped it before it changed anything that mattered: the world is as it was.
  Abandoned,
  // An undo is putting the pieces or records back.
  Undoing,
  // Undone.
  Undone,
}

internal static class BakeStates
{
  internal static bool Final(this BakeState state) => state is BakeState.Settled or BakeState.Abandoned or BakeState.Undone;

  internal static string Name(this BakeState state) => state.ToString();

  internal static bool TryParse(string? text, out BakeState state) => Enum.TryParse(text?.Trim(), false, out state) && Enum.IsDefined(typeof(BakeState), state);
}

// A town piece: a real object that stays, and is made a protected Live record of the bake. The journal knows where it stands, so
// that a crash can finish the tagging, or undo it.
internal sealed class AdoptedPiece
{
  public PieceCopy Place { get; set; } = new();
  // Its id within the operation, which is also the Live record's.
  public uint Id { get; set; }
  // The bake that owns the Live record: this operation's number for a bake; the record's own source for a piece an unbake releases.
  public int Source { get; set; }
}

// Everything an operation needs to be undone, or finished after a stop.
internal sealed class BakeJournalData
{
  public int Number { get; set; }
  // BakeArea, BakeBox, Unbake or Drop (a load keeps a copy of the layer instead).
  public OperationKind Kind { get; set; }
  // Unix seconds, UTC.
  public long Time { get; set; }
  // The character's name, or "server console".
  public string Who { get; set; } = "";
  // The admin's platform id: kept here only (the layer goes to every player).
  public string PlatformId { get; set; } = "";
  public float X1 { get; set; }
  public float Z1 { get; set; }
  public float X2 { get; set; }
  public float Z2 { get; set; }
  public float Radius { get; set; }
  public string WorldName { get; set; } = "";
  public long WorldUid { get; set; }
  public uint RevisionBefore { get; set; }
  public uint RevisionAfter { get; set; }
  public string Version { get; set; } = "";
  // The command's words, for the log: "town any".
  public string Words { get; set; } = "";
  // A bake: the operation's own value sets, which its records point to (a record's ValueSet is an index into this list).
  public List<ValueSet> ValueSets { get; set; } = [];
  // A bake: the records it adds. An unbake or a drop: the records it takes out, as the layer had them.
  public List<BakeRecord> Records { get; set; } = [];
  // A bake: the statics it removes, each a whole ZDO.
  public List<PieceCopy> Statics { get; set; } = [];
  // An unbake: the pieces it makes, each a whole ZDO as it will be made.
  public List<PieceCopy> Created { get; set; } = [];
  // A town bake: the real pieces it adopts.
  public List<AdoptedPiece> Adopted { get; set; } = [];

  public bool IsBake => Kind is OperationKind.BakeArea or OperationKind.BakeBox;

  // The pieces the crash rules count: what a bake removes, what an unbake makes.
  public IReadOnlyList<PieceCopy> Pieces => Kind == OperationKind.Unbake ? Created : Kind == OperationKind.Drop ? [] : Statics;

  // The registry's entry for the operation, as it goes into the layer; the same whether the operation is carried out or finished
  // after a stop. A bake adds its records and adopts its Live ones; an unbake and a drop remove records.
  public OperationInfo ToOperation(OperationState state = OperationState.InLayer) => new(
    (ushort)Number, Kind, state, Time, Who, X1, Z1, X2, Z2, Radius,
    IsBake ? (uint)Records.Count : 0u, IsBake ? 0u : (uint)Records.Count, (uint)Adopted.Count, Version, IsBake ? ValueSets.ToArray() : null);
}

internal sealed class BakeJournalException : Exception
{
  public BakeJournalException(string message) : base(message)
  {
  }
}

// "BCBJ", format 1: the journal's bytes. A header, the operation, the value sets, the palette entries the records use, the records,
// the statics, the pieces it makes, the adopted pieces, and a CRC-32 of everything before it. Little-endian. A string is a u16 length
// and UTF-8 (a value's own text, which can be a chest's whole inventory, has an i32 length).
internal static class BakeJournalFile
{
  internal const string Magic = "BCBJ";
  internal const ushort Format = 1;
  // The most one section may hold, so that a damaged count cannot ask for gigabytes.
  internal const int MaxItems = 20_000_000;

  internal static byte[] Encode(BakeJournalData data)
  {
    var stream = new MemoryStream(1 << 16);
    var w = new BinaryWriter(stream, new UTF8Encoding(false));
    foreach (var c in Magic)
      w.Write((byte)c);
    w.Write(Format);
    w.Write((ushort)0);
    w.Write(data.Number);
    w.Write((byte)data.Kind);
    w.Write(data.Time);
    Str(w, data.Who);
    Str(w, data.PlatformId);
    w.Write(data.X1);
    w.Write(data.Z1);
    w.Write(data.X2);
    w.Write(data.Z2);
    w.Write(data.Radius);
    Str(w, data.WorldName);
    w.Write(data.WorldUid);
    w.Write(data.RevisionBefore);
    w.Write(data.RevisionAfter);
    Str(w, data.Version);
    Str(w, data.Words);

    // The value sets the records point to: the bake's own, then any a record of another bake has (an unbake takes records of
    // several). Each distinct set once; a record refers to its set by its place in this table.
    var sets = new List<ValueSet>(data.ValueSets);
    var setIndex = new Dictionary<ValueSet, int>();
    for (int i = 0; i < sets.Count; i++)
      if (!setIndex.ContainsKey(sets[i]))
        setIndex[sets[i]] = i;
    foreach (var record in data.Records)
      if (!setIndex.ContainsKey(record.Values))
      {
        setIndex[record.Values] = sets.Count;
        sets.Add(record.Values);
      }
    w.Write(data.ValueSets.Count);
    w.Write(sets.Count);
    foreach (var set in sets)
      WriteValues(w, set.Values);

    // The palette entries, in first-use order, each once.
    var entries = new List<PaletteEntry>();
    var entryIndex = new Dictionary<PaletteEntry, int>();
    foreach (var record in data.Records)
      if (!entryIndex.ContainsKey(record.Entry))
      {
        entryIndex[record.Entry] = entries.Count;
        entries.Add(record.Entry);
      }
    var palette = new BakedWriter(256);
    try
    {
      BakedCodec.WritePalette(palette, entries);
    }
    catch (BakedFormatException e)
    {
      throw new BakeJournalException("a palette entry cannot be written: " + e.Message);
    }
    w.Write(entries.Count);
    w.Write(palette.Length);
    w.Write(palette.Buffer_, 0, palette.Length);

    w.Write(data.Records.Count);
    foreach (var record in data.Records)
      WriteRecord(w, record, entryIndex[record.Entry], setIndex[record.Values]);

    w.Write(data.Statics.Count);
    foreach (var p in data.Statics)
      WritePiece(w, p);

    w.Write(data.Created.Count);
    foreach (var p in data.Created)
      WritePiece(w, p);

    w.Write(data.Adopted.Count);
    foreach (var a in data.Adopted)
    {
      WritePiece(w, a.Place);
      w.Write(a.Id);
      w.Write(a.Source);
    }
    w.Flush();
    var bytes = stream.ToArray();
    var crc = Crc32.Compute(bytes, 0, bytes.Length);
    var result = new byte[bytes.Length + 4];
    Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
    result[bytes.Length] = (byte)crc;
    result[bytes.Length + 1] = (byte)(crc >> 8);
    result[bytes.Length + 2] = (byte)(crc >> 16);
    result[bytes.Length + 3] = (byte)(crc >> 24);
    return result;
  }

  internal static BakeJournalData Decode(byte[] bytes)
  {
    if (bytes.Length < 8 + 4)
      throw new BakeJournalException("the journal is too short");
    var body = bytes.Length - 4;
    uint stored = (uint)(bytes[body] | bytes[body + 1] << 8 | bytes[body + 2] << 16 | bytes[body + 3] << 24);
    if (Crc32.Compute(bytes, 0, body) != stored)
      throw new BakeJournalException("the journal's checksum does not match: the file is damaged");
    var r = new BinaryReader(new MemoryStream(bytes, 0, body), new UTF8Encoding(false));
    try
    {
      for (int i = 0; i < Magic.Length; i++)
        if (r.ReadByte() != Magic[i])
          throw new BakeJournalException("not a journal (the first four bytes are not BCBJ)");
      var format = r.ReadUInt16();
      if (format != Format)
        throw new BakeJournalException($"a journal of format {format}: made by a newer Better Continents");
      if (r.ReadUInt16() != 0)
        throw new BakeJournalException("a journal with flags this version does not know");
      var data = new BakeJournalData
      {
        Number = r.ReadInt32(),
        Kind = (OperationKind)r.ReadByte(),
        Time = r.ReadInt64(),
        Who = Str(r),
        PlatformId = Str(r),
        X1 = r.ReadSingle(),
        Z1 = r.ReadSingle(),
        X2 = r.ReadSingle(),
        Z2 = r.ReadSingle(),
        Radius = r.ReadSingle(),
        WorldName = Str(r),
        WorldUid = r.ReadInt64(),
        RevisionBefore = r.ReadUInt32(),
        RevisionAfter = r.ReadUInt32(),
        Version = Str(r),
        Words = Str(r),
      };
      if (data.Kind is not (OperationKind.BakeArea or OperationKind.BakeBox or OperationKind.Unbake or OperationKind.Drop))
        throw new BakeJournalException($"an operation of kind {(int)data.Kind}, which a journal does not hold");
      int own = Count(r);
      int setCount = Count(r);
      if (own > setCount)
        throw new BakeJournalException($"{own} value sets of the operation's own among {setCount}");
      var sets = new List<ValueSet>(Math.Min(setCount, 4096));
      for (int i = 0; i < setCount; i++)
        sets.Add(new ValueSet(ReadValues(r).ToArray()));
      data.ValueSets = sets.GetRange(0, own);
      int entryCount = Count(r);
      int paletteBytes = Count(r);
      var paletteData = r.ReadBytes(paletteBytes);
      if (paletteData.Length != paletteBytes)
        throw new EndOfStreamException();
      PaletteEntry[] entries;
      try
      {
        entries = BakedCodec.ReadPalette(new BakedReader(paletteData, 0, paletteData.Length), entryCount);
      }
      catch (BakedFormatException e)
      {
        throw new BakeJournalException("a palette entry is damaged: " + e.Message);
      }
      int records = Count(r);
      for (int i = 0; i < records; i++)
        data.Records.Add(ReadRecord(r, entries, sets));
      int statics = Count(r);
      for (int i = 0; i < statics; i++)
        data.Statics.Add(ReadPiece(r));
      int created = Count(r);
      for (int i = 0; i < created; i++)
        data.Created.Add(ReadPiece(r));
      int adopted = Count(r);
      for (int i = 0; i < adopted; i++)
        data.Adopted.Add(new AdoptedPiece { Place = ReadPiece(r), Id = r.ReadUInt32(), Source = r.ReadInt32() });
      if (r.BaseStream.Position != r.BaseStream.Length)
        throw new BakeJournalException("the journal has bytes left over after its last section");
      return data;
    }
    catch (EndOfStreamException)
    {
      throw new BakeJournalException("the journal ends in the middle of a section");
    }
  }

  private static int Count(BinaryReader r)
  {
    int n = r.ReadInt32();
    if (n < 0 || n > MaxItems)
      throw new BakeJournalException($"a count of {n} in the journal");
    return n;
  }

  private static void Str(BinaryWriter w, string s)
  {
    var bytes = Encoding.UTF8.GetBytes(s);
    if (bytes.Length > ushort.MaxValue)
      throw new BakeJournalException("a name longer than 65,535 bytes");
    w.Write((ushort)bytes.Length);
    w.Write(bytes);
  }

  private static string Str(BinaryReader r)
  {
    int n = r.ReadUInt16();
    var bytes = r.ReadBytes(n);
    if (bytes.Length != n)
      throw new EndOfStreamException();
    return Encoding.UTF8.GetString(bytes);
  }

  private static void WriteValues(BinaryWriter w, IReadOnlyList<ZdoValue> values)
  {
    w.Write(values.Count);
    foreach (var v in values)
    {
      w.Write(v.Key);
      w.Write((byte)v.Type);
      switch (v.Type)
      {
        case ZdoValueType.Float:
          w.Write(v.A);
          break;
        case ZdoValueType.Vec3:
          w.Write(v.A);
          w.Write(v.B);
          w.Write(v.C);
          break;
        case ZdoValueType.Quat:
          w.Write(v.A);
          w.Write(v.B);
          w.Write(v.C);
          w.Write(v.D);
          break;
        case ZdoValueType.Int:
        case ZdoValueType.Long:
          w.Write(v.Number);
          break;
        case ZdoValueType.String:
          var text = Encoding.UTF8.GetBytes(v.Text);
          w.Write(text.Length);
          w.Write(text);
          break;
        default:
          w.Write(v.Data.Length);
          w.Write(v.Data);
          break;
      }
    }
  }

  private static List<ZdoValue> ReadValues(BinaryReader r)
  {
    int n = Count(r);
    var values = new List<ZdoValue>(Math.Min(n, 4096));
    for (int i = 0; i < n; i++)
    {
      int key = r.ReadInt32();
      var type = (ZdoValueType)r.ReadByte();
      switch (type)
      {
        case ZdoValueType.Float:
          values.Add(ZdoValue.OfFloat(key, r.ReadSingle()));
          break;
        case ZdoValueType.Vec3:
          values.Add(ZdoValue.OfVec3(key, r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
          break;
        case ZdoValueType.Quat:
          values.Add(ZdoValue.OfQuat(key, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
          break;
        case ZdoValueType.Int:
          values.Add(ZdoValue.OfInt(key, (int)r.ReadInt64()));
          break;
        case ZdoValueType.Long:
          values.Add(ZdoValue.OfLong(key, r.ReadInt64()));
          break;
        case ZdoValueType.String:
          values.Add(ZdoValue.OfString(key, Encoding.UTF8.GetString(Blob(r))));
          break;
        case ZdoValueType.Bytes:
          values.Add(ZdoValue.OfBytes(key, Blob(r)));
          break;
        default:
          throw new BakeJournalException($"a value of type {(int)type}, which this version does not know");
      }
    }
    return values;
  }

  private static byte[] Blob(BinaryReader r)
  {
    int n = r.ReadInt32();
    if (n < 0 || n > 1 << 28)
      throw new BakeJournalException($"a value {n} bytes long");
    var bytes = r.ReadBytes(n);
    if (bytes.Length != n)
      throw new EndOfStreamException();
    return bytes;
  }

  // A record as the layer holds it, field by field, so that it can be put back by value: the zone, the palette entry (by its place in
  // the journal's own table), the flags, the columns and the runs it has, and where its value set is in the journal's table.
  private static void WriteRecord(BinaryWriter w, BakeRecord rec, int entry, int set)
  {
    var r = rec.Record;
    w.Write((short)r.Zone.X);
    w.Write((short)r.Zone.Z);
    w.Write(entry);
    w.Write(set);
    w.Write((byte)r.Flags);
    w.Write(r.X);
    w.Write(r.Z);
    w.Write(r.Y);
    w.Write(r.Yaw);
    if (r.HasFullRotation)
    {
      w.Write(r.Packed.Largest);
      w.Write(r.Packed.A);
      w.Write(r.Packed.B);
      w.Write(r.Packed.C);
    }
    if (r.HasScale)
    {
      w.Write(r.ScaleX);
      w.Write(r.ScaleY);
      w.Write(r.ScaleZ);
    }
    if (r.HasId)
      w.Write(r.Id);
    if (r.HasSource)
    {
      w.Write(r.Source);
      w.Write(r.ValueSet);
    }
    if (r.HasSeed)
      w.Write(r.Seed);
  }

  private static BakeRecord ReadRecord(BinaryReader r, PaletteEntry[] entries, List<ValueSet> sets)
  {
    var zone = new ZoneKey(r.ReadInt16(), r.ReadInt16());
    int entry = r.ReadInt32();
    int set = r.ReadInt32();
    if (entry < 0 || entry >= entries.Length)
      throw new BakeJournalException($"a record points to palette entry {entry}, and the journal has {entries.Length}");
    if (set < 0 || set >= sets.Count)
      throw new BakeJournalException($"a record points to value set {set}, and the journal has {sets.Count}");
    var rec = new ZoneRecord { Zone = zone, Palette = (ushort)entry, Flags = (RecordFlags)r.ReadByte() };
    if (((int)rec.Flags & ~(int)RecordFlags.All) != 0)
      throw new BakeJournalException("a record with flags this version does not know");
    rec.X = r.ReadUInt16();
    rec.Z = r.ReadUInt16();
    rec.Y = r.ReadInt32();
    rec.Yaw = r.ReadUInt16();
    if (rec.HasFullRotation)
      rec.Packed = new PackedRotation(r.ReadByte(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16());
    if (rec.HasScale)
    {
      rec.ScaleX = r.ReadUInt16();
      rec.ScaleY = r.ReadUInt16();
      rec.ScaleZ = r.ReadUInt16();
    }
    if (rec.HasId)
      rec.Id = r.ReadUInt32();
    if (rec.HasSource)
    {
      rec.Source = r.ReadUInt16();
      rec.ValueSet = r.ReadUInt16();
    }
    if (rec.HasSeed)
      rec.Seed = r.ReadUInt16();
    return new BakeRecord(entries[entry], rec, sets[set]);
  }

  // Flags of a piece: 1 persistent, 2 distant.
  private static void WritePiece(BinaryWriter w, PieceCopy p)
  {
    w.Write(p.Prefab);
    Str(w, p.PrefabName);
    w.Write(p.X);
    w.Write(p.Y);
    w.Write(p.Z);
    w.Write(p.RotX);
    w.Write(p.RotY);
    w.Write(p.RotZ);
    w.Write((byte)((p.Persistent ? 1 : 0) | (p.Distant ? 2 : 0)));
    w.Write(p.Type);
    w.Write(p.UserId);
    w.Write(p.Id);
    WriteValues(w, p.Values);
  }

  private static PieceCopy ReadPiece(BinaryReader r)
  {
    var p = new PieceCopy { Prefab = r.ReadInt32(), PrefabName = Str(r), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle() };
    p.RotX = r.ReadSingle();
    p.RotY = r.ReadSingle();
    p.RotZ = r.ReadSingle();
    int flags = r.ReadByte();
    p.Persistent = (flags & 1) != 0;
    p.Distant = (flags & 2) != 0;
    p.Type = r.ReadByte();
    p.UserId = r.ReadInt64();
    p.Id = r.ReadUInt32();
    p.Values = [.. ReadValues(r)];
    return p;
  }
}

// An operation's folder on the machine that runs the world: <save data>/BetterContinents/bakes/<world name>-<world uid>/. Never
// inside the world's own folder: the game's rename and backup delete or skip the files they do not know (SaveSystemPatch), and the
// export keeps its files out of it for the same reason.
internal class BakeJournal
{
  // How many finished operations are kept, besides every one that is not finished (10.6).
  internal const int KeepOperations = 20;
  // How many copies of the layer from before a load are kept: each is the whole layer.
  internal const int KeepLayers = 5;

  public string Folder { get; }

  public BakeJournal(string folder)
  {
    Folder = folder;
  }

  // The folder of a world: its name and uid, so that two worlds with one name keep apart.
  internal static string FolderFor(string root, string worldName, long worldUid) =>
    Path.Combine(root, "bakes", SafeName(worldName) + "-" + worldUid.ToString(CultureInfo.InvariantCulture));

  internal static string SafeName(string name)
  {
    var bad = Path.GetInvalidFileNameChars();
    var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    return s.Length == 0 ? "world" : s;
  }

  internal string JournalPath(int number) => Path.Combine(Folder, $"op-{number}.bcj");
  internal string StatePath(int number) => Path.Combine(Folder, $"op-{number}.state");
  internal string LayerPath(uint revision) => Path.Combine(Folder, $"layer-r{revision}.bcp");

  // To a .tmp file, flushed to the disk, then put in place: a stop in the middle leaves the old file or the new one, never half of one and
  // never none (File.Replace swaps them in one step; deleting the old file first would leave a moment with no state file at all, which the
  // check reads as "Prepared": the state that says no piece was taken out of the world).
  // A journal that cannot be written stops the operation before anything changes, so this throws.
  internal static void WriteAtomic(string path, byte[] bytes, int length = -1)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var tmp = path + ".tmp";
    using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
    {
      file.Write(bytes, 0, length < 0 ? bytes.Length : length);
      file.Flush(true);
    }
    if (File.Exists(path))
      File.Replace(tmp, path, null);
    else
      File.Move(tmp, path);
  }

  // Virtual so that the tests can stop the operation at every write (Tests.BakeRunner.cs): a write is one mutation of the undo file.
  internal virtual void Write(BakeJournalData data) => WriteAtomic(JournalPath(data.Number), BakeJournalFile.Encode(data));

  internal BakeJournalData Read(int number)
  {
    byte[] bytes;
    try
    {
      bytes = File.ReadAllBytes(JournalPath(number));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      throw new BakeJournalException($"op-{number}.bcj cannot be read: {e.Message}");
    }
    var data = BakeJournalFile.Decode(bytes);
    if (data.Number != number)
      throw new BakeJournalException($"op-{number}.bcj holds operation {data.Number}");
    return data;
  }

  internal virtual void SetState(int number, BakeState state) => WriteAtomic(StatePath(number), Encoding.ASCII.GetBytes(state.Name() + "\n"));

  // null when there is no state file, or it holds something this version does not know.
  internal BakeState? GetState(int number)
  {
    try
    {
      var path = StatePath(number);
      if (File.Exists(path) && BakeStates.TryParse(File.ReadAllText(path), out var state))
        return state;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
    }
    return null;
  }

  // The operations that have a journal, oldest first.
  internal List<int> Numbers()
  {
    var numbers = new List<int>();
    if (!Directory.Exists(Folder))
      return numbers;
    foreach (var file in Directory.GetFiles(Folder, "op-*.bcj"))
    {
      var name = Path.GetFileNameWithoutExtension(file);
      if (int.TryParse(name.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        numbers.Add(n);
    }
    numbers.Sort();
    return numbers;
  }

  // The operations that were not finished: a crash, or a save that cut one, left them for the check at the next world load.
  // Operations with a journal and no state file count too: the state is written right after the journal, and a stop between
  // the two left the journal alone, which is "Prepared".
  internal List<int> Pending() => Numbers().Where(n => !(GetState(n) ?? BakeState.Prepared).Final()).ToList();

  // The number the next operation may take: after every journal in the folder, whatever the layer's registry says, so that a
  // journal an abandoned operation left behind is never overwritten.
  internal int NextNumber(int registryNext)
  {
    var numbers = Numbers();
    return Math.Max(registryNext, numbers.Count == 0 ? 1 : numbers[numbers.Count - 1] + 1);
  }

  // Deletes the files of old operations (10.6): the newest 20 stay, and every one that is not finished. Done when a new
  // operation starts. The copies of the layer from before a load: the newest few stay.
  internal int Prune()
  {
    int deleted = 0;
    var numbers = Numbers();
    var newest = new HashSet<int>(numbers.AsEnumerable().Reverse().Take(KeepOperations));
    foreach (var n in numbers)
    {
      if (newest.Contains(n) || !(GetState(n) ?? BakeState.Prepared).Final())
        continue;
      TryDelete(JournalPath(n));
      TryDelete(StatePath(n));
      deleted++;
    }
    if (Directory.Exists(Folder))
    {
      var layers = Directory.GetFiles(Folder, "layer-r*.bcp")
        .Select(f => (File: f, Revision: uint.TryParse(Path.GetFileNameWithoutExtension(f).Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0u))
        .OrderByDescending(p => p.Revision).Skip(KeepLayers).ToList();
      foreach (var (file, _) in layers)
        TryDelete(file);
      deleted += layers.Count;
      // A stop in the middle of a write leaves a .tmp file: it is not any operation's.
      foreach (var tmp in Directory.GetFiles(Folder, "*.tmp"))
        TryDelete(tmp);
    }
    return deleted;
  }

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
    }
  }
}

// What finishes an operation a stop left half done (10.8). The check runs in the ZNet.LoadWorld postfix before reconciliation, for
// every journal not yet Settled, Abandoned or Undone. It looks at what is true then, never at what the state says should be
// true: the state is only how far the operation had got, and it is never behind the world (it is written before each step's
// changes), so it says which of the two places could have lost a piece.
internal sealed class BakeCrashPlan
{
  // The operation's state once the plan is carried out.
  public BakeState Final { get; init; }
  // Put the journal's records into the layer (revision + 1, with the operation in the registry), push is not needed at load.
  public bool AddRecords { get; init; }
  // Take them out.
  public bool RemoveRecords { get; init; }
  // The journal's pieces that stand and must go (their indices in BakeJournalData.Pieces).
  public int[] RemoveStatics { get; init; } = [];
  // The journal's pieces that are missing and must be made.
  public int[] CreatePieces { get; init; } = [];
  // Town pieces: write the bake keys on every adopted piece, or take them off.
  public bool CompleteAdoption { get; init; }
  public bool ReleaseAdopted { get; init; }
  // Which row of 10.8's table (2 to 9) a bake's plan is; 0 for the other operations.
  public int Row { get; init; }
  // What happened, in a few words, for the log and the admin.
  public string Why { get; init; } = "";

  public bool Changes => AddRecords || RemoveRecords || RemoveStatics.Length > 0 || CreatePieces.Length > 0 || CompleteAdoption || ReleaseAdopted;
}

internal static class BakeCrash
{
  // recordsPresent: the layer holds the records the journal lists (a bake's records are in; an unbake's or drop's are not yet out).
  // standing[i]: piece i of the journal (BakeJournalData.Pieces) is in the world. hasAdopted: the journal has town pieces.
  internal static BakeCrashPlan Plan(OperationKind kind, BakeState state, bool recordsPresent, IReadOnlyList<bool> standing, bool hasAdopted)
  {
    if (state.Final())
      return new BakeCrashPlan { Final = state, Why = "already finished" };
    var standingIndices = Where(standing, true);
    var missingIndices = Where(standing, false);
    switch (kind)
    {
      case OperationKind.BakeArea:
      case OperationKind.BakeBox:
        return PlanBake(state, recordsPresent, hasAdopted, standingIndices, missingIndices);
      case OperationKind.Unbake:
        return PlanUnbake(state, recordsPresent, standingIndices, missingIndices);
      case OperationKind.Drop:
        return PlanDrop(state, recordsPresent);
      default:
        return new BakeCrashPlan { Final = BakeState.Abandoned, Why = "an operation of a kind this version does not know" };
    }
  }

  private static BakeCrashPlan PlanBake(BakeState state, bool recordsPresent, bool hasAdopted, int[] standingIndices, int[] missingIndices)
  {
    switch (state)
    {
      case BakeState.Undoing:
        // An undo: the pieces first (every static the bake removed, made again), then the records go.
        return new BakeCrashPlan
        {
          Final = BakeState.Undone,
          CreatePieces = missingIndices,
          RemoveRecords = recordsPresent,
          ReleaseAdopted = hasAdopted,
          Why = "finishing the undo",
        };
      case BakeState.Prepared:
      case BakeState.Applied:
      case BakeState.Removing:
      case BakeState.Removed:
        break;
      default:
        return new BakeCrashPlan { Final = BakeState.Abandoned, Why = $"a bake cannot be in the state {state}" };
    }
    // Rows 4, 6, 8, 9: the records are in the layer. Finish the removal; nothing is lost, at worst a piece was drawn and standing.
    if (recordsPresent)
    {
      int row = state == BakeState.Removed ? (standingIndices.Length == 0 ? 9 : 8) : (missingIndices.Length == 0 ? 4 : 6);
      return new BakeCrashPlan
      {
        Final = BakeState.Settled,
        RemoveStatics = standingIndices,
        CompleteAdoption = hasAdopted,
        Row = row,
        Why = standingIndices.Length == 0 ? "the layer holds the records and no static stands: done" : "the layer holds the records: removing the statics that still stand",
      };
    }
    // The layer lacks the records. Until the removal began nothing was taken out of the world, so whatever is missing is somebody
    // else's doing and the bake never took effect (rows 2, 3).
    if (state is BakeState.Prepared or BakeState.Applied)
      return new BakeCrashPlan { Final = BakeState.Abandoned, ReleaseAdopted = hasAdopted, Row = state == BakeState.Prepared ? 2 : 3, Why = "stopped before the removal: the world is as it was" };
    // The removal had begun. All statics standing: the removals were never saved (row 5, and row 7 when the game fell back to the
    // previous save): the bake is abandoned. Some or all gone: the objects reached the disk and the layer did not (row 7): the
    // records come back from the journal, and the statics that still stand go.
    if (missingIndices.Length == 0)
      return new BakeCrashPlan { Final = BakeState.Abandoned, ReleaseAdopted = hasAdopted, Row = state == BakeState.Removing ? 5 : 7, Why = "every static still stands: the removal was not saved" };
    return new BakeCrashPlan
    {
      Final = BakeState.Settled,
      AddRecords = true,
      RemoveStatics = standingIndices,
      CompleteAdoption = hasAdopted,
      Row = 7,
      Why = "the statics are gone and the layer lacks their records: putting the records back from the journal",
    };
  }

  private static BakeCrashPlan PlanUnbake(BakeState state, bool recordsPresent, int[] standingIndices, int[] missingIndices)
  {
    switch (state)
    {
      case BakeState.Prepared:
        return new BakeCrashPlan { Final = BakeState.Abandoned, Why = "stopped before any piece was made: nothing changed" };
      case BakeState.Unbaking:
        // Pieces first, then the records go; each step can be done again.
        return new BakeCrashPlan
        {
          Final = BakeState.Settled,
          CreatePieces = missingIndices,
          RemoveRecords = recordsPresent,
          Why = "finishing the unbake: making the missing pieces, then taking the records out",
        };
      case BakeState.Undoing:
        // Undoing an unbake: the records first, then the pieces it made go.
        return new BakeCrashPlan
        {
          Final = BakeState.Undone,
          AddRecords = !recordsPresent,
          RemoveStatics = standingIndices,
          Why = "finishing the undo: the records back, then the pieces it made go",
        };
      default:
        return new BakeCrashPlan { Final = BakeState.Abandoned, Why = $"an unbake cannot be in the state {state}" };
    }
  }

  private static BakeCrashPlan PlanDrop(BakeState state, bool recordsPresent)
  {
    switch (state)
    {
      case BakeState.Undoing:
        return new BakeCrashPlan { Final = BakeState.Undone, AddRecords = !recordsPresent, Why = "finishing the undo: the records back" };
      case BakeState.Prepared:
      case BakeState.Applied:
        // A drop takes records out and makes no piece: whether it happened is whether the layer still has them.
        return recordsPresent
          ? new BakeCrashPlan { Final = BakeState.Abandoned, Why = "the layer still holds the records: the drop did not take effect" }
          : new BakeCrashPlan { Final = BakeState.Settled, Why = "the layer lacks the records: the drop took effect" };
      default:
        return new BakeCrashPlan { Final = BakeState.Abandoned, Why = $"a drop cannot be in the state {state}" };
    }
  }

  private static int[] Where(IReadOnlyList<bool> flags, bool value)
  {
    var list = new List<int>();
    for (int i = 0; i < flags.Count; i++)
      if (flags[i] == value)
        list.Add(i);
    return [.. list];
  }
}

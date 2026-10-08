// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of the in-game bake's journal (BakeJournal.cs): the file's format written and read back field for field, every kind
// of damage refused by name, the folder on disk (atomic writes, states, the numbers, what is kept), and the registry entry an
// operation makes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static PieceCopy BakePiece(int n, float x = 0f, float z = 0f, string name = "stone_wall_2x1")
  {
    var piece = BakeCopyAt(name.GetStableHashCode(), 10f + x, 30f + n * 0.25f, 20f + z, 22.5f * (n % 16));
    piece.PrefabName = name;
    piece.UserId = 1000 + n;
    piece.Id = (uint)(100 + n);
    piece.Type = 2;
    piece.Values = [ZdoValue.OfLong(BakeKey("creator"), 987654321L + n % 3), ZdoValue.OfInt(BakeKey("creatorIndex"), 2)];
    Array.Sort(piece.Values, BakeValues.Compare);
    return piece;
  }

  // A journal of a bake with every kind of thing in it.
  private static BakeJournalData BakeRichJournal()
  {
    var pool = new BakeValueSetPool();
    var data = new BakeJournalData
    {
      Number = 7,
      Kind = OperationKind.BakeArea,
      Time = 1791400000,
      Who = "Wübarrk",
      PlatformId = "Steam_76561198000000000",
      X1 = 812f,
      Z1 = -1206f,
      X2 = 812f,
      Z2 = -1206f,
      Radius = 40f,
      WorldName = "Mundo é 世",
      WorldUid = -1234567890123L,
      RevisionBefore = 12,
      RevisionAfter = 13,
      Version = "0.10.4",
      Words = "town any",
    };
    // Statics with the values a real piece has, one of them heavy.
    for (int i = 0; i < 6; i++)
    {
      var piece = BakePiece(i, i * 3f, i * 2f, i % 2 == 0 ? "stone_wall_2x1" : "wood_floor");
      if (i == 2)
        piece.Values = [.. piece.Values, ZdoValue.OfString(BakeKey("items"), new string('z', 70000) + "é"), ZdoValue.OfBytes(BakeKey("blob"), Enumerable.Range(0, 5000).Select(k => (byte)(k * 7)).ToArray())];
      if (i == 3)
        piece.Values = [.. piece.Values, ZdoValue.OfVec3(BakeKey("pos"), 1f, float.NaN, -0f), ZdoValue.OfQuat(BakeKey("rot"), 0.1f, 0.2f, 0.3f, 0.9f), ZdoValue.OfFloat(BakeKey("health"), 12.5f), ZdoValue.OfInt(BakeKey("MatVar0"), 3)];
      piece.Values = [.. piece.Values.OrderBy(v => v.Key).ThenBy(v => v.Type)];
      data.Statics.Add(piece);
      var facts = BakeFacts(piece.PrefabName, true);
      data.Records.Add(BakeCapture.MakeRecord(piece, facts, i == 5 ? BakedRole.Seat : BakedRole.Static, data.Number, 0, pool));
    }
    // A tilted piece, a scaled one, one with a seed.
    var tilted = BakePiece(10, 3f, 3f);
    tilted.RotX = 30f;
    tilted.RotZ = 10f;
    tilted.Values = [.. tilted.Values, ZdoValue.OfVec3(BakeKey("scale"), 2f, 2f, 2.5f), ZdoValue.OfInt(BakeKey("RandMatSeed"), 4321)];
    data.Statics.Add(tilted);
    data.Records.Add(BakeCapture.MakeRecord(tilted, BakeFacts("tilted", true), BakedRole.Static, data.Number, 0, pool));
    // A town piece.
    var door = BakePiece(11, 5f, 5f, "wood_door");
    data.Adopted.Add(new AdoptedPiece { Place = door, Id = 1, Source = data.Number });
    data.Records.Add(BakeCapture.MakeRecord(door, BakeFacts("wood_door"), BakedRole.Live, data.Number, 1, pool));
    // Records of other sources, as an unbake would take them out: a compiler's, and one of bake 3 with a set of its own.
    var other = BakeCapture.MakeRecord(BakePiece(12, 9f, 9f, "darkwood_roof"), BakeFacts("darkwood_roof"), BakedRole.Static, 3, 0, new BakeValueSetPool());
    other.Record.ValueSet = 5;
    data.Records.Add(other);
    var compiler = BakeCapture.MakeRecord(BakePiece(13), BakeFacts("stone_wall_2x1"), BakedRole.Static, 0, 0, new BakeValueSetPool());
    data.Records.Add(compiler);
    data.ValueSets = pool.Sets;
    data.Created.Add(BakePiece(20, 1f, 1f, "wood_beam"));
    data.Created.Add(BakePiece(21, 2f, 2f, "wood_beam"));
    return data;
  }

  private static string BakeJournalDifference(BakeJournalData a, BakeJournalData b)
  {
    if (a.Number != b.Number || a.Kind != b.Kind || a.Time != b.Time || a.Who != b.Who || a.PlatformId != b.PlatformId)
      return "the operation's number, kind, time or who";
    if (a.X1 != b.X1 || a.Z1 != b.Z1 || a.X2 != b.X2 || a.Z2 != b.Z2 || a.Radius != b.Radius)
      return "the area";
    if (a.WorldName != b.WorldName || a.WorldUid != b.WorldUid || a.RevisionBefore != b.RevisionBefore || a.RevisionAfter != b.RevisionAfter || a.Version != b.Version || a.Words != b.Words)
      return "the world, the revisions, the version or the words";
    if (a.ValueSets.Count != b.ValueSets.Count || !a.ValueSets.Zip(b.ValueSets).All(p => p.First.Equals(p.Second)))
      return "the value sets";
    if (a.Records.Count != b.Records.Count)
      return "the number of records";
    for (int i = 0; i < a.Records.Count; i++)
    {
      var x = a.Records[i];
      var y = b.Records[i];
      // The palette index is the table's, so the records compare by entry and by every other field.
      var rx = x.Record;
      var ry = y.Record;
      rx.Palette = ry.Palette = 0;
      if (!x.Entry.Equals(y.Entry) || !rx.Equals(ry) || !x.Values.Equals(y.Values) || x.Record.Zone != y.Record.Zone)
        return $"record {i} ({x} against {y})";
    }
    string Pieces(List<PieceCopy> p, List<PieceCopy> q, string what)
    {
      if (p.Count != q.Count)
        return "the number of " + what;
      for (int i = 0; i < p.Count; i++)
      {
        var s = p[i];
        var t = q[i];
        if (s.Prefab != t.Prefab || s.PrefabName != t.PrefabName || s.X != t.X || s.Y != t.Y || s.Z != t.Z || s.RotX != t.RotX || s.RotY != t.RotY || s.RotZ != t.RotZ
            || s.Persistent != t.Persistent || s.Distant != t.Distant || s.Type != t.Type || s.UserId != t.UserId || s.Id != t.Id || s.Values.Length != t.Values.Length)
          return $"{what} {i}";
        for (int k = 0; k < s.Values.Length; k++)
          if (!s.Values[k].Equals(t.Values[k]))
            return $"{what} {i}, value {k} ({s.Values[k].Describe()} against {t.Values[k].Describe()})";
      }
      return "";
    }
    var d = Pieces(a.Statics, b.Statics, "statics");
    if (d.Length > 0) return d;
    d = Pieces(a.Created, b.Created, "created pieces");
    if (d.Length > 0) return d;
    if (a.Adopted.Count != b.Adopted.Count)
      return "the number of adopted pieces";
    for (int i = 0; i < a.Adopted.Count; i++)
    {
      if (a.Adopted[i].Id != b.Adopted[i].Id || a.Adopted[i].Source != b.Adopted[i].Source)
        return $"adopted piece {i}'s id or source";
      d = Pieces([a.Adopted[i].Place], [b.Adopted[i].Place], "adopted place");
      if (d.Length > 0) return d;
    }
    return "";
  }

  // The bytes with their CRC made again, so that a test can damage one thing and keep the rest valid.
  private static byte[] BakeWithCrc(byte[] bytes, int length = -1)
  {
    int body = length < 0 ? bytes.Length - 4 : length;
    var result = new byte[body + 4];
    Buffer.BlockCopy(bytes, 0, result, 0, body);
    var crc = Crc32.Compute(result, 0, body);
    result[body] = (byte)crc;
    result[body + 1] = (byte)(crc >> 8);
    result[body + 2] = (byte)(crc >> 16);
    result[body + 3] = (byte)(crc >> 24);
    return result;
  }

  private static string BakeJournalRefusal(byte[] bytes)
  {
    try
    {
      BakeJournalFile.Decode(bytes);
    }
    catch (BakeJournalException e)
    {
      return e.Message;
    }
    return null;
  }

  private static void BakeJournalFileTest()
  {
    Section("journal: the file written and read back, field for field");
    var data = BakeRichJournal();
    var bytes = BakeJournalFile.Encode(data);
    var back = BakeJournalFile.Decode(bytes);
    var difference = BakeJournalDifference(data, back);
    C(difference.Length == 0, "every field comes back (first difference: " + difference + ")");
    C(back.Records.Count == 10 && back.Statics.Count == 7 && back.Created.Count == 2 && back.Adopted.Count == 1, "the counts: records 10, statics 7, created 2, adopted 1");
    C(back.Statics[2].Values.Find(BakeKey("items"), ZdoValueType.String)?.Text.Length == 70001, "a text of 70,001 characters (a chest's inventory) comes back whole");
    C(back.Statics[3].Values.Find(BakeKey("pos"), ZdoValueType.Vec3) is { A: 1f } v && float.IsNaN(v.B), "a NaN and a -0 inside a vector come back as they were");
    C(back.Records.Any(r => r.Record.HasFullRotation) && back.Records.Any(r => r.Record.HasScale) && back.Records.Any(r => r.Record.HasSeed) && back.Records.Any(r => r.Record.HasId)
      && back.Records.Any(r => !r.Record.HasSource), "records with a full rotation, a scale, a seed, an id, and the compiler's without a source all come back");
    C(back.Records[9].Record.SourceNumber == 0 && back.Records[8].Record.Source == 3 && back.Records[8].Record.ValueSet == 5, "a record of another source keeps its source and its value set index as the layer had them");
    C(back.Records[8].Values.Values.Length == 2, "and its value set, which is not the operation's own");
    C(back.ValueSets.Count == data.ValueSets.Count, "the operation's own value sets are the first ones, and only they");
    C(BakeJournalFile.Encode(back).SequenceEqual(bytes), "writing what was read gives the same bytes");
    C(BakeJournalFile.Encode(data).SequenceEqual(bytes), "the same journal gives the same bytes");
    C(bytes.Length < 200_000, $"the journal is {bytes.Length:N0} bytes");

    foreach (var kind in new[] { OperationKind.BakeBox, OperationKind.Unbake, OperationKind.Drop })
    {
      var other = BakeRichJournal();
      other.Kind = kind;
      C(BakeJournalDifference(other, BakeJournalFile.Decode(BakeJournalFile.Encode(other))).Length == 0, $"a journal of kind {kind} too");
    }
    var empty = new BakeJournalData { Number = 1, Kind = OperationKind.BakeArea };
    C(BakeJournalDifference(empty, BakeJournalFile.Decode(BakeJournalFile.Encode(empty))).Length == 0, "an empty journal");

    Section("journal: each kind of damage is refused, and says what it is");
    var flipped = 0;
    foreach (var at in new[] { 0, 3, 5, 9, 20, 100, bytes.Length / 4, bytes.Length / 2, bytes.Length - 5, bytes.Length - 4, bytes.Length - 1 })
    {
      var bad = (byte[])bytes.Clone();
      bad[at] ^= 0x10;
      if (BakeJournalRefusal(bad) != null)
        flipped++;
    }
    C(flipped == 11, $"a flipped byte anywhere is refused ({flipped} of 11)");
    C((BakeJournalRefusal(bytes.Take(bytes.Length - 1).ToArray()) ?? "").Length > 0, "a file one byte short is refused");
    C(BakeJournalRefusal(new byte[5]) != null && BakeJournalRefusal(Array.Empty<byte>()) != null, "a file with nothing in it is refused");
    C(BakeJournalRefusal(bytes.Take(bytes.Length / 2).ToArray()) != null, "half a file is refused");
    C((BakeJournalRefusal(((byte[])bytes.Clone()).Also(b => b[bytes.Length - 6] ^= 1)) ?? "").Contains("checksum"), "damage is named as a checksum that does not match");
    var wrongMagic = (byte[])bytes.Clone();
    wrongMagic[0] = (byte)'X';
    C((BakeJournalRefusal(BakeWithCrc(wrongMagic)) ?? "").Contains("BCBJ"), "the first four bytes must be BCBJ");
    var newer = (byte[])bytes.Clone();
    newer[4] = 2;
    C((BakeJournalRefusal(BakeWithCrc(newer)) ?? "").Contains("newer"), "a format 2 is a newer Better Continents's");
    var flags = (byte[])bytes.Clone();
    flags[6] = 1;
    C((BakeJournalRefusal(BakeWithCrc(flags)) ?? "").Contains("flags"), "flags this version does not know");
    var kind3 = (byte[])bytes.Clone();
    kind3[12] = 4;
    C((BakeJournalRefusal(BakeWithCrc(kind3)) ?? "").Contains("kind 4"), "an operation of kind 4 (a load) is not a journal's");
    var extra = bytes.Take(bytes.Length - 4).Concat(new byte[] { 1, 2, 3 }).ToArray();
    C((BakeJournalRefusal(BakeWithCrc(extra, extra.Length)) ?? "").Contains("left over"), "bytes left over after the last section");
    var absurd = BakeJournalFile.Encode(empty);
    // The counts sit at the end of an empty journal: value sets, sets, entries, palette bytes, records, statics, created, adopted.
    var absurdBody = absurd.Take(absurd.Length - 4).ToArray();
    absurdBody[absurdBody.Length - 4 * 3] = 0xFF;
    absurdBody[absurdBody.Length - 4 * 3 + 3] = 0x7F;
    C((BakeJournalRefusal(BakeWithCrc(absurdBody, absurdBody.Length)) ?? "").Contains("count"), "a count of two billion is refused before anything is asked for");
  }

  private static byte[] Also(this byte[] bytes, Action<byte[]> change)
  {
    change(bytes);
    return bytes;
  }

  private static void BakeJournalStoreTest()
  {
    Section("journal: the folder on disk");
    var root = Path.Combine(Work, "bakes-root");
    var folder = BakeJournal.FolderFor(root, "My World/a", 123456789L);
    C(folder.EndsWith("bakes" + Path.DirectorySeparatorChar + "My World_a-123456789"), "the folder is bakes/<world name>-<uid>, with unsafe characters in the name replaced: " + folder);
    C(BakeJournal.FolderFor(root, "", 5).EndsWith("world-5") && BakeJournal.FolderFor(root, "A", 1) != BakeJournal.FolderFor(root, "A", 2), "an empty name is 'world', and two worlds with one name keep apart");
    var journal = new BakeJournal(folder);
    C(journal.Numbers().Count == 0 && journal.Pending().Count == 0 && journal.NextNumber(1) == 1 && journal.GetState(1) == null, "a folder that does not exist yet holds nothing");

    var data = BakeRichJournal();
    journal.Write(data);
    C(File.Exists(journal.JournalPath(7)) && !File.Exists(journal.JournalPath(7) + ".tmp"), "the journal is written, and no .tmp file is left");
    C(BakeJournalDifference(data, journal.Read(7)).Length == 0, "and read back");
    C(journal.Numbers().SequenceEqual([7]) && journal.Pending().SequenceEqual([7]) && journal.GetState(7) == null, "a journal with no state file is pending (it is Prepared)");
    foreach (var state in Enum.GetValues(typeof(BakeState)).Cast<BakeState>())
    {
      journal.SetState(7, state);
      C(journal.GetState(7) == state && !File.Exists(journal.StatePath(7) + ".tmp"), $"state {state} is written and read back");
      C(journal.Pending().Contains(7) == !state.Final(), $"{state} is {(state.Final() ? "finished" : "pending")}");
    }
    File.WriteAllText(journal.StatePath(7), "Sleeping\n");
    C(journal.GetState(7) == null && journal.Pending().Contains(7), "a state this version does not know is no state: the operation stays pending");
    File.WriteAllText(journal.StatePath(7), "  Removing  \r\n");
    C(journal.GetState(7) == BakeState.Removing, "spaces and a line end around the word do not matter");
    journal.SetState(7, BakeState.Settled);

    C(journal.NextNumber(3) == 8 && journal.NextNumber(20) == 20, "the next number is above every journal and the registry's");
    data.Number = 9;
    journal.Write(data);
    C(journal.Numbers().SequenceEqual([7, 9]) && journal.NextNumber(3) == 10, "a journal of an operation that never reached a layer still holds its number");

    // Damaged and missing journals are named, never thrown past.
    File.WriteAllBytes(journal.JournalPath(9), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
    try
    {
      journal.Read(9);
      C(false, "a damaged journal is refused");
    }
    catch (BakeJournalException e)
    {
      C(e.Message.Length > 0, "a damaged journal is refused, with a reason: " + e.Message);
    }
    try
    {
      journal.Read(42);
      C(false, "a missing journal is refused");
    }
    catch (BakeJournalException e)
    {
      C(e.Message.Contains("op-42.bcj"), "a missing journal is refused by name: " + e.Message);
    }
    File.Copy(journal.JournalPath(7), journal.JournalPath(5));
    try
    {
      journal.Read(5);
      C(false, "a journal of another operation is refused");
    }
    catch (BakeJournalException e)
    {
      C(e.Message.Contains("holds operation 7"), "op-5.bcj that holds operation 7 is refused: " + e.Message);
    }

    Section("journal: a write that fails stops the operation");
    var blocked = Path.Combine(Work, "blocked");
    File.WriteAllText(blocked, "a file where the folder should be");
    var failing = new BakeJournal(Path.Combine(blocked, "world"));
    bool threw = false;
    try
    {
      failing.Write(BakeRichJournal());
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      threw = true;
    }
    C(threw, "writing under a file throws, so nothing changes in the world");

    Section("journal: what is kept");
    var keep = new BakeJournal(Path.Combine(root, "keep"));
    for (int n = 1; n <= 30; n++)
    {
      var op = new BakeJournalData { Number = n, Kind = OperationKind.BakeBox };
      keep.Write(op);
      keep.SetState(n, n == 4 || n == 6 ? BakeState.Removing : n == 8 ? BakeState.Undoing : n % 2 == 0 ? BakeState.Settled : BakeState.Abandoned);
    }
    File.WriteAllBytes(keep.JournalPath(31), [0]);
    File.WriteAllBytes(Path.Combine(keep.Folder, "op-32.bcj.tmp"), [0]);
    for (uint r = 1; r <= 8; r++)
      File.WriteAllBytes(keep.LayerPath(r * 3), [(byte)r]);
    int deleted = keep.Prune();
    var kept = keep.Numbers();
    C(kept.Count == 23, $"the 20 newest (12 to 31) and 4, 6 and 8, which are not finished (4 and 6 removing, 8 undoing): {string.Join(" ", kept)}");
    C(kept.Contains(31) && kept.Contains(4) && kept.Contains(6) && kept.Contains(8), "the pending ones are kept however old");
    C(Enumerable.Range(12, 19).All(kept.Contains), "the newest 20 operations are kept: 11 to 30 and 31");
    C(!kept.Contains(1) && !kept.Contains(2) && !kept.Contains(10) && !File.Exists(keep.StatePath(2)), "older finished ones go, with their state files");
    C(!File.Exists(Path.Combine(keep.Folder, "op-32.bcj.tmp")), "a .tmp file a stop left is cleaned up");
    var layers = Directory.GetFiles(keep.Folder, "layer-r*.bcp").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    C(layers.Count == 5 && layers.Contains("layer-r24.bcp") && layers.Contains("layer-r12.bcp") && !layers.Contains("layer-r9.bcp"), $"the newest five copies of the layer are kept: {string.Join(" ", layers)}");
    C(deleted > 0, $"{deleted} files went");

    Section("journal: the registry entry of an operation");
    var bake = BakeRichJournal();
    var op7 = bake.ToOperation();
    C(op7.Number == 7 && op7.Kind == OperationKind.BakeArea && op7.State == OperationState.InLayer && op7.Time == 1791400000 && op7.Who == "Wübarrk", "the number, kind, state, time and who");
    C(op7.Radius == 40f && op7.X1 == 812f && op7.Z1 == -1206f && op7.Version == "0.10.4", "the area and the version");
    C(op7.Added == 10 && op7.Removed == 0 && op7.Adopted == 1, $"a bake adds its records, removes none, and adopts its town pieces (added {op7.Added}, adopted {op7.Adopted})");
    C(op7.ValueSets.Length == bake.ValueSets.Count && op7.ValueSets[0].Equals(bake.ValueSets[0]), "and keeps its value sets");
    bake.Kind = OperationKind.Unbake;
    var unbake = bake.ToOperation();
    C(unbake.Added == 0 && unbake.Removed == 10 && unbake.ValueSets.Length == 0, "an unbake adds none, removes its records, and has no value sets of its own");
    C(bake.ToOperation(OperationState.Undone).State == OperationState.Undone, "an undone state is as asked");
    C(BakeJournalFile.Decode(BakeJournalFile.Encode(bake)).ToOperation().Removed == 10, "the same entry after the journal is read back");
  }
}

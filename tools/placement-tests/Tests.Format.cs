// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Format 1 (spec 2): every field written and read back, each refusal of 2.2 by name, the same input giving the same bytes, and a layer BC
// writes read by a second writer's rules (RawFile).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // The sample layer, read back field by field against what it was made from.
  private static void FormatRoundTripTest()
  {
    Section("format: every field written and read back");
    var sample = MakeSample();
    var layer = sample.Layer;
    C(layer.Revision == 1u, "a layer made in game is revision 1");
    C(layer.Producer == "sample producer", "the producer is kept");
    C(layer.HasRegistry && (layer.Flags & HeaderFlags.Registry) != 0, "the registry is flagged");
    C((layer.Flags & HeaderFlags.Ground) != 0 && (layer.Flags & HeaderFlags.Paint) != 0 && (layer.Flags & HeaderFlags.ClearMask) != 0, "ground, paint and clear masks are flagged");
    C(layer.Palette.Count == sample.Palette.Length && layer.Palette.Zip(sample.Palette, (a, b) => a.Equals(b)).All(x => x), "the palette reads back, entry by entry");
    var tinted = layer.Palette[0];
    C(tinted.Candidates.Length == 2 && tinted.Candidates[1].Name == "wood_wall" && tinted.Candidates[1].AnchorX == 100 && tinted.Candidates[1].AnchorY == -200 && tinted.Candidates[1].AnchorZ == 300,
      "candidates and anchors");
    C(tinted.Boxes.Length == 2 && tinted.Boxes[1].CentreX == -300 && tinted.Boxes[0].SizeX == 2000, "boxes");
    C(tinted.Tint == "stone" && tinted.TintR == 900 && tinted.TintG == 800 && tinted.TintB == 700 && tinted.TintFilter == "all", "tint, colour and filter");
    var door = layer.Palette[1];
    C(door.Tags.Length == 4 && door.Tags[0].Type == TagType.Bool && door.Tags[0].Bool && door.Tags[1].Text == "door" && door.Tags[2].Number == 5 && door.Tags[3].Float == 1.5f,
      "tags of all four types");
    C(door.Role == BakedRole.Live && door.Protected && layer.Palette[3].Layer == 5 && layer.Palette[3].Collision == BakedCollision.Trunk, "role, collision, layer and flags");

    // The registry.
    var ops = layer.Registry.Operations;
    C(ops.Count == 5 && layer.Registry.NextOperation == 6, "five operations, the next is 6");
    C(ops[0].ValueSets.Length == 2 && ops[0].ValueSets[1].Equals(SampleValueSet(1)) && ops[1].ValueSets.Length == 1, "value sets of every type read back");
    C(ops[0].Who == "Tester 1" && ops[0].Time == 1_700_000_001L && ops[0].Radius == 40f && ops[0].Version == "0.10.4" && ops[0].Added == 10, "an operation's fields");
    C(layer.Registry.Knows(1, 1) && !layer.Registry.Knows(1, 2) && !layer.Registry.Knows(3, 0) && !layer.Registry.Knows(9, 0), "which bakes and value sets the registry knows");

    // The zones and records.
    var expectedZones = sample.Records.Keys.Concat([new ZoneKey(2, 2), new ZoneKey(-7, 9)]).OrderBy(k => k).ToList();
    C(layer.Zones.Select(z => z.Key).SequenceEqual(expectedZones), "the zones are those that have records or sections, in (z, x) order");
    long total = 0;
    foreach (var pair in sample.Records)
    {
      layer.TryGetZoneRow(pair.Key, out var row);
      var data = layer.Decode(row);
      var read = data.Records().ToList();
      read.Sort();
      var given = pair.Value.ToList();
      given.Sort();
      C(read.Count == given.Count && read.Zip(given, (a, b) => a.Equals(b)).All(x => x), $"zone {pair.Key}: {given.Count} records read back as written");
      C(row.Placements == given.Count && row.Live == given.Count(r => sample.Palette[r.Palette].Role == BakedRole.Live), $"zone {pair.Key}: the index counts");
      total += given.Count;
      double low = given.Min(r => r.WorldY), high = given.Max(r => r.WorldY) + 3;
      C(Math.Abs(row.YMin - low) < 0.001 && Math.Abs(row.YMax - high) < 0.001, $"zone {pair.Key}: y bounds are the records' y and the pieces' height");
    }
    C(layer.Placements == total, "the header's placements");
    var first = sample.Records.Values.First()[0];
    layer.TryGetZoneRow(first.Zone, out var firstRow);
    var firstData = layer.Decode(firstRow);
    C(firstRow.NoVegetation && firstRow.HasClearMask && firstRow.HasGround && firstRow.HasPaint && (firstRow.Flags & ZoneFlags.ProtectFootprints) != 0, "zone (0, 0): its flags");
    C(firstData.ClearMask.SequenceEqual(MaskOf(0, 5, 255)) && firstRow.ClearMask.SequenceEqual(firstData.ClearMask), "the clear mask");
    C(firstRow.MaskCovers(2, 2) && !firstRow.MaskCovers(6, 2) && firstRow.MaskCovers(63.9, 63.9) && !firstRow.MaskCovers(-1, 0), "mask cells are 4 m, row 0 south");
    C(firstData.Paint.Length == 4096 && firstData.Paint[5] == 1 && layer.Ground.Count == 2, "paint and ground are read back (ground in two zones)");
    C(firstData.Extras.Length == 2 && firstData.Extras[0].Tag == "note" && firstData.Extras[0].Data.SequenceEqual(new byte[] { 9, 8, 7 }) && firstData.Extras[1].Data.Length == 0, "extras are kept");
    layer.TryGetZoneRow(2, 2, out var maskOnly);
    C(maskOnly.Placements == 0 && maskOnly.HasClearMask && !maskOnly.HasGround && layer.Decode(maskOnly).Count == 0, "a zone that holds only a clear mask");
    layer.TryGetZoneRow(-7, 9, out var flagOnly);
    C(flagOnly.Placements == 0 && flagOnly.NoVegetation && flagOnly.ClearMask == null, "a zone that holds only zone flag 16");

    // Read again from its bytes: the same layer.
    var again = BakedLayer.Read(layer.Bytes, layer.Length);
    C(Differ(layer, again) == null, "reading its own bytes gives the same content: " + Differ(layer, again));
    C(again.Id == layer.Id && layer.Id.Length == 32, "a 32 character id");
    var fromCopy = BakedLayer.Read((byte[])layer.Bytes.Clone(), layer.Length);
    C(fromCopy.Id == layer.Id, "the id depends on the bytes only");
    // A buffer longer than the layer, as a package's buffer is.
    var longer = new byte[layer.Length + 100];
    Buffer.BlockCopy(layer.Bytes, 0, longer, 0, layer.Length);
    C(Differ(layer, BakedLayer.Read(longer, layer.Length)) == null, "a buffer longer than the layer reads as the layer");
    // A ZDO-side check: the records' places.
    var rec = sample.Records.Values.First()[3];
    layer.TryGetZoneRow(rec.Zone, out var r2);
    var d2 = layer.Decode(r2);
    var back = d2.Records().First(r => r.Equals(rec));
    C(Math.Abs(back.WorldX - rec.WorldX) < 1e-9 && back.Position == rec.Position, "position decodes to what was encoded");
    C(d2.Rotations.Length == d2.Count && d2.Scales.Length == d2.Count && d2.Ids.Length == d2.Count && d2.Sources.Length == d2.Count && d2.Seeds.Length == d2.Count && d2.ValueSets.Length == d2.Count,
      "the per-record arrays have a value for every record");
    C(Enumerable.Range(0, d2.Count).All(k => d2.Seeds[k] >= 0 && d2.Seeds[k] <= BakedFormat.MaxSeed), "seeds are in 0 to 12344, derived or stored");
    C(Enumerable.Range(0, d2.Count).All(k => (d2.ValueSets[k] >= 0) == ((d2.Flags[k] & RecordFlags.Source) != 0) && (d2.Sources[k] != 0) == ((d2.Flags[k] & RecordFlags.Source) != 0)),
      "source and value set are given where the record has them");
  }

  // The same input, the same bytes (within one runtime), however the work is ordered.
  private static void FormatDeterminismTest()
  {
    Section("format: the same input gives the same bytes");
    var a = MakeSample(7);
    var b = MakeSample(7);
    C(a.Layer.Length == b.Layer.Length && a.Layer.Bytes.Take(a.Layer.Length).SequenceEqual(b.Layer.Bytes.Take(b.Layer.Length)), "two layers made the same way are the same bytes");
    C(a.Layer.Id == b.Layer.Id, "and have the same id");
    // Records added in another order give the same layer: a block holds its records sorted.
    var palette = SamplePalette();
    var random = new Lcg(5);
    var records = new List<ZoneRecord>();
    uint id = 1;
    for (int i = 0; i < 300; i++)
    {
      int entry = random.Int(palette.Length);
      records.Add(ZoneRecord.CreateYaw(entry, random.Range(-40, 90), random.Range(30, 90), random.Range(-40, 90), random.Range(0, 360), id: palette[entry].Role == BakedRole.Live ? id++ : null));
    }
    BakedLayer Make(IEnumerable<ZoneRecord> order)
    {
      var edit = LayerEdit.New("order");
      foreach (var entry in palette)
        edit.PaletteIndexFor(entry);
      edit.AddRecords(order);
      return edit.Build().Layer;
    }
    var forward = Make(records);
    var backward = Make(Enumerable.Reverse(records));
    var shuffled = Make(records.OrderBy(r => r.GetHashCode()));
    C(forward.Id == backward.Id && forward.Id == shuffled.Id, "records added in any order give one layer");
    // Rereading a layer and rebuilding it with nothing changed (every block encoded again) gives the same blocks.
    var edit2 = a.Layer.Edit();
    edit2.ReencodeAll();
    var (rebuilt, changed) = edit2.Build();
    C(Differ(a.Layer, rebuilt) == null, "encoding every block again keeps the content: " + Differ(a.Layer, rebuilt));
    C(rebuilt.Revision == a.Layer.Revision + 1, "and counts as a change (revision + 1)");
    var body = Enumerable.Range(0, rebuilt.Zones.Count).All(i => rebuilt.Bytes.Skip(rebuilt.Zones[i].Offset).Take(rebuilt.Zones[i].Length)
      .SequenceEqual(a.Layer.Bytes.Skip(a.Layer.Zones[i].Offset).Take(a.Layer.Zones[i].Length)));
    C(body, "each block comes out as the same bytes");
    C(changed.Length == 0, "no zone is reported changed when no block differs");
  }

  // The sections of a zone, in the order and layout the spec gives, through the second writer.
  private static void FormatSectionsTest()
  {
    Section("format: a zone's sections, written by the second writer, read by BC");
    var f = ValidRaw();
    var zone = f.Zones[0];
    zone.Mask = MaskOf(3, 4, 200);
    var ground = new MemoryStream();
    ground.Write(BitConverter.GetBytes(31.5f));
    for (int i = 0; i < BakedFormat.GroundVertices; i++)
      ground.Write(BitConverter.GetBytes((short)(i * 7 % 1000 - 100)));
    for (int i = 0; i < BakedFormat.GroundVertices; i++)
      ground.WriteByte((byte)(i % 256));
    zone.GroundBytes = ground.ToArray();
    zone.Paint = Enumerable.Range(0, 4096).Select(i => (byte)(i / 64 % 4)).ToArray();
    zone.Extras.Add(("note", [1, 2, 3]));
    zone.Extras.Add(("z", []));
    zone.ZoneFlagsExtra = 8 | 16;
    f.HeaderFlags = 7;
    var bytes = f.Build(out int length);
    var layer = BakedLayer.Read(bytes, length);
    var row = layer.Zones[0];
    C(row.Flags == (ZoneFlags)31 && layer.Flags == (HeaderFlags)7, "all five zone flags and three header flags");
    var data = layer.Decode(row);
    C(data.ClearMask.SequenceEqual(MaskOf(3, 4, 200)), "clear mask: 32 bytes first");
    C(data.Ground.Base == 31.5f && data.Ground.Heights[0] == -100 && data.Ground.Heights[1] == -93 && data.Ground.Weights[300] == 300 % 256 && data.Ground.Weights[4224] == 4224 % 256,
      "ground: base, then 65 x 65 heights in cm, then 65 x 65 weights");
    C(data.Paint[0] == 0 && data.Paint[64] == 1 && data.Paint[64 * 3] == 3 && data.Paint[64 * 4] == 0, "paint: 64 x 64 cells, row 0 south");
    C(data.Extras.Length == 2 && data.Extras[0].Tag == "note" && data.Extras[0].Data.SequenceEqual(new byte[] { 1, 2, 3 }) && data.Extras[1].Tag == "z", "extras: tag, length, bytes");
    C(data.Records().Count() == 3, "and the records before them");
    // An extra that runs past the end of the block.
    f = ValidRaw();
    f.Zones[0].RawBlock = f.Zones[0].Raw(false).Concat(new byte[] { 1, 1, (byte)'x', 0xFF, 0xFF, 0, 0 }).ToArray();
    bytes = f.Build(out length);
    C(Refuses(() => BakedLayer.Read(bytes, length), "extra", "past the end"), "refused: an extra running past the end");
  }

  // ------------------------------------------------------------------------------------------------ the refusals of 2.2

  private static RawFile ValidRaw()
  {
    var f = new RawFile();
    f.Palette.Add(new RawEntry { Role = 0, Collision = 3, Candidates = [("piece_a", [0, 0, 0])] });
    f.Palette.Add(new RawEntry { Role = 1, Collision = 3, Flags = 4, Candidates = [("door", [0, 0, 0])], Tags = [("VALtima_TownPiece", 0, true), ("VALtima_Kind", 3, "door")] });
    var zone = new RawZone { X = 0, Z = 0, YMin = 30, YMax = 40 };
    zone.Records.Add(new RawRecord { Palette = 0, X = 1000, Z = 2000, Y = 31000, Yaw = 100, Flags = 0 });
    zone.Records.Add(new RawRecord { Palette = 0, X = 3000, Z = 2000, Y = 31000, Yaw = 200, Flags = 2, Scale = [1000, 2000, 1000] });
    zone.Records.Add(new RawRecord { Palette = 1, X = 5000, Z = 6000, Y = 31000, Yaw = 300, Flags = 4, Id = 7 });
    f.Zones.Add(zone);
    return f;
  }

  private static void Refusal(string name, Action<RawFile> damage, params string[] parts)
  {
    var f = ValidRaw();
    damage(f);
    var bytes = f.Build(out int length);
    var message = RefusalOf(() => BakedLayer.Read(bytes, length));
    C(message != null && parts.All(p => message.Contains(p, StringComparison.OrdinalIgnoreCase)),
      $"refused: {name} (wanted '{string.Join("', '", parts)}'; got {(message == null ? "no refusal" : "'" + message + "'")})");
  }

  private static void FormatRefusalsTest()
  {
    Section("format: what a reader refuses (spec 2.2), each by name");
    // The control: the undamaged file reads.
    var good = ValidRaw().Build(out int goodLength);
    var layer = BakedLayer.Read(good, goodLength);
    C(layer.Placements == 3 && layer.Zones.Count == 1 && layer.Palette.Count == 2, "the second writer's file reads");
    C(layer.Zones[0].Live == 1 && layer.Decode(layer.Zones[0]).Records().Last().Id == 7u, "with its Live record and id");
    C(BakedFormat.Crc32(good, 0, goodLength - 4) == BitConverter.ToUInt32(good, goodLength - 4), "the two writers' CRC-32 agree");

    // Size and trailer.
    C(Refuses(() => BakedLayer.Read(new byte[35], 35), "under"), "refused: a file under 36 bytes");
    Refusal("the CRC differs", f => f.FixCrc = false, "CRC");
    // Header.
    Refusal("the magic is not BCPL", f => f.Magic = [(byte)'B', (byte)'C', (byte)'P', (byte)'X'], "BCPL");
    Refusal("the format is not 1", f => f.Format = 2, "format", "not 1");
    Refusal("the reserved word is not 0", f => f.Reserved = 5, "reserved");
    Refusal("an unknown header flag", f => f.HeaderFlags = 16, "newer", "header bit 4");
    Refusal("another unknown header flag", f => f.HeaderFlags = 32 | 1, "header bit 5");
    // Palette.
    Refusal("more than 65,535 palette entries", f => f.PaletteCount = 70000, "65535");
    Refusal("no candidate", f => f.Palette[0].Candidates = [], "candidates");
    Refusal("nine candidates", f => f.Palette[0].Candidates = Enumerable.Range(0, 9).Select(i => ("p" + i, new short[] { 0, 0, 0 })).ToList(), "candidates");
    Refusal("role above 3", f => f.Palette[0].Role = 4, "role");
    Refusal("collision above 3", f => f.Palette[0].Collision = 4, "collision");
    Refusal("layer above 31", f => f.Palette[0].Layer = 32, "layer");
    Refusal("a palette flag other than 1, 2, 4, 8, 16, 32", f => f.Palette[0].Flags = 64, "palette flag bit 6");
    Refusal("a tag type above 3", f => f.Palette[1].Tags = [("k", 4, "")], "type");
    Refusal("a string running past the end", f =>
    {
      f.Zones.Clear();
      f.Palette.Clear();
      f.Palette.Add(new RawEntry { Override = [1, 250, (byte)'x'] });
    }, "past the end");
    Refusal("a box list running past the end", f =>
    {
      f.Zones.Clear();
      f.Palette.Clear();
      f.Palette.Add(new RawEntry { Override = [1, 1, (byte)'a', 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 255] });
    }, "past the end");
    Refusal("a non-ASCII name", f => f.Palette[0].Override = [1, 2, 0xC3, 0xA9, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0], "ASCII");
    // Index.
    Refusal("rows not in order", f =>
    {
      f.Zones.Add(new RawZone { X = 1, Z = 0 });
      f.Zones[0].X = 1;
      f.Zones[1].X = 0;
      f.Zones[1].Z = 0;
    }, "increasing");
    Refusal("the same zone twice", f => f.Zones.Add(new RawZone { X = 0, Z = 0 }), "increasing");
    Refusal("a zone outside -1024..1023", f => f.Zones[0].X = 1024, "outside");
    Refusal("a zone below -1024", f => f.Zones[0].Z = -1025, "outside");
    Refusal("a block outside the file", f => f.Zones[0].Offset = 100000, "not inside");
    Refusal("a block before the zone index", f => f.Zones[0].Offset = 10, "not inside");
    Refusal("a block running past the trailer", f => f.Zones[0].Length = 100000, "not inside");
    Refusal("the zone count over what the file holds", f => f.ZoneCount = 50, "past the end");
    Refusal("an unknown zone flag", f => f.Zones[0].IndexFlags = 32 | 0, "zone bit 5");
    Refusal("a reserved index byte that is not 0", f => f.Zones[0].IndexPad = 1, "after the flags");
    // Blocks.
    Refusal("inflating a block passes 16 MB", f => f.Zones[0].Deflated = RawFile.Deflate(new byte[17 * 1024 * 1024]), "16 MB");
    Refusal("the count differs from the index", f => f.Zones[0].IndexCount = 5, "index says");
    Refusal("a palette index out of range", f => f.Zones[0].Records[0].Palette = 9, "palette index");
    Refusal("a record flag 32", f => f.Zones[0].Records[0].Flags = 32, "record bit 5");
    Refusal("a record flag 64", f => f.Zones[0].Records[0].Flags = 64, "record bit 6");
    Refusal("a Live record without an id", f => f.Zones[0].Records[2].Flags = 0, "Live record has no id");
    Refusal("the index's live count differs", f => f.Zones[0].IndexLive = 5, "Live records");
    Refusal("a record that names a bake in a layer without a registry", f => { f.Zones[0].Records[0].Flags = 8; f.Zones[0].Records[0].Source = 1; f.Zones[0].Records[0].ValueSet = 0; }, "registry");
    Refusal("a bake the registry does not list", f =>
    {
      f.HeaderFlags = 8;
      f.Registry = RawFile.RegistryBytes(1, 2, (1, 1, 1, "who", 1));
      f.Zones[0].Records[0].Flags = 8;
      f.Zones[0].Records[0].Source = 2;
      f.Zones[0].Records[0].ValueSet = 0;
    }, "does not list");
    Refusal("a value set the registry does not list", f =>
    {
      f.HeaderFlags = 8;
      f.Registry = RawFile.RegistryBytes(1, 2, (1, 1, 1, "who", 1));
      f.Zones[0].Records[0].Flags = 8;
      f.Zones[0].Records[0].Source = 1;
      f.Zones[0].Records[0].ValueSet = 1;
    }, "does not list");
    Refusal("a bake of kind unbake", f =>
    {
      f.HeaderFlags = 8;
      f.Registry = RawFile.RegistryBytes(1, 2, (1, 3, 1, "who", 0));
      f.Zones[0].Records[0].Flags = 8;
      f.Zones[0].Records[0].Source = 1;
      f.Zones[0].Records[0].ValueSet = 0;
    }, "does not list");
    Refusal("a record naming bake 0", f =>
    {
      f.HeaderFlags = 8;
      f.Registry = RawFile.RegistryBytes(1, 2, (1, 1, 1, "who", 1));
      f.Zones[0].Records[0].Flags = 8;
      f.Zones[0].Records[0].Source = 0;
      f.Zones[0].Records[0].ValueSet = 0;
    }, "bake 0");
    Refusal("a seed above 12344", f => { f.Zones[0].Records[0].Flags = 16; f.Zones[0].Records[0].Seed = 12345; }, "seed");
    Refusal("bytes left over after the extras", f => f.Zones[0].RawBlock = f.Zones[0].Raw().Concat(new byte[] { 0 }).ToArray(), "left over");
    Refusal("a rotation naming component 4", f => { f.Zones[0].Records[0].Flags = 1; f.Zones[0].Records[0].Rotation = (4, 0, 0, 0); }, "largest");
    Refusal("a scale of 0", f => f.Zones[0].Records[1].Scale = [1000, 0, 1000], "scale is 0");
    Refusal("a duplicate (source, id) among Live records", f =>
    {
      var other = new RawZone { X = 1, Z = 0, YMin = 30, YMax = 40 };
      other.Records.Add(new RawRecord { Palette = 1, X = 5000, Z = 6000, Y = 31000, Yaw = 300, Flags = 4, Id = 7 });
      f.Zones.Add(other);
    }, "repeats an id");
    Refusal("a block that is not a Deflate stream", f => f.Zones[0].Deflated = [1, 2, 3, 4, 5, 6, 7, 8], "inflated");
    Refusal("a block too short for its records", f => f.Zones[0].RawBlock = [3, 0, 0, 0, 1, 0], "too short");
    // Totals.
    Refusal("the records do not add up to the header", f => f.Placements = 99, "header says");
    // Registry.
    Refusal("a registry of format 2", f => { f.HeaderFlags = 8; f.Registry = RawFile.RegistryBytes(2, 1); }, "format");
    Refusal("a registry that runs past its length", f => { f.HeaderFlags = 8; f.Registry = RawFile.Deflate([1, 0, 1, 0, 1, 0]); }, "past the end");
    Refusal("a registry whose Deflate stream runs past the file", f => { f.HeaderFlags = 8; f.Registry = RawFile.RegistryBytes(1, 1); f.RegistryLength = 100000; }, "past the end");
    // The unknown bits of 14.1: header 16 (above), record 32 (above), zone 32 (above) are refused by name.
    // Over 256 MB.
    var huge = new byte[BakedFormat.MaxLayer + 1];
    C(Refuses(() => BakedLayer.Read(huge, huge.Length), "256"), "refused: a layer over 256 MB");
    huge = null;
  }
}

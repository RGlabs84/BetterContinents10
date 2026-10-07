// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// LayerEdit (spec 2.6, 10): adding and removing records, the palette that only grows, the registry and its operation numbers, the blocks that
// are not touched copied byte for byte, and the zones reported changed.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  public static bool SameBlock(BakedLayer a, BakedLayer b, ZoneKey zone)
  {
    if (!a.TryGetZoneRow(zone, out var x) || !b.TryGetZoneRow(zone, out var y) || x.Length != y.Length)
      return false;
    for (int i = 0; i < x.Length; i++)
      if (a.Bytes[x.Offset + i] != b.Bytes[y.Offset + i])
        return false;
    return true;
  }

  public static List<ZoneRecord> RecordsOf(BakedLayer layer, ZoneKey zone)
  {
    if (!layer.TryGetZoneRow(zone, out var row))
      return [];
    var list = layer.Decode(row).Records().ToList();
    list.Sort();
    return list;
  }

  private static void EditAddAndRemoveTest()
  {
    Section("edit: records added and removed, blocks not touched copied, zones reported changed");
    var sample = MakeSample(11);
    var baseLayer = sample.Layer;
    var zones = sample.Records.Keys.ToList();

    // Add records to a zone that has some, and to a new one.
    var edit = baseLayer.Edit();
    var added = new List<ZoneRecord>();
    var inZone = zones[1];
    for (int i = 0; i < 5; i++)
      added.Add(ZoneRecord.CreateYaw(0, inZone.OriginX + 5 + i, 50, inZone.OriginZ + 9, 10 * i));
    var fresh = new ZoneKey(700, -300);
    added.Add(ZoneRecord.CreateYaw(2, fresh.OriginX + 3, 61.5, fresh.OriginZ + 4, 90));
    edit.AddRecords(added, 4f);
    var (next, changed) = edit.Build();
    C(next.Revision == baseLayer.Revision + 1, "the revision is the old one plus 1");
    C(changed.SequenceEqual(new[] { inZone, fresh }.OrderBy(k => k)), "only the zone with new records and the new zone are changed: " + string.Join(" ", changed));
    foreach (var zone in zones.Where(z => z != inZone))
      C(SameBlock(baseLayer, next, zone), $"zone {zone}: the block is copied byte for byte");
    var expected = sample.Records[inZone].Concat(added.Where(r => r.Zone == inZone)).OrderBy(r => r).ToList();
    C(RecordsOf(next, inZone).SequenceEqual(expected), "the zone holds its old records and the new ones");
    C(RecordsOf(next, fresh).Count == 1 && next.Placements == baseLayer.Placements + 6, "a new zone has its record; the layer holds six more");
    next.TryGetZoneRow(inZone, out var row);
    baseLayer.TryGetZoneRow(inZone, out var oldRow);
    C(row.YMin <= oldRow.YMin + 1e-6 && row.YMax >= 54f, $"the zone's y bounds take in the new records and their height ({row.YMin}..{row.YMax})");
    next.TryGetZoneRow(fresh, out var freshRow);
    C(Math.Abs(freshRow.YMin - 61.5f) < 0.001 && Math.Abs(freshRow.YMax - 65.5f) < 0.001, "a new zone's bounds are its record's y and height");
    C(next.Zones.Select(z => z.Key).SequenceEqual(next.Zones.Select(z => z.Key).OrderBy(k => k)), "the index stays in (z, x) order");
    C(next.Palette.Count == baseLayer.Palette.Count && next.Palette.Zip(baseLayer.Palette, (a, b) => a.Equals(b)).All(x => x), "the palette is as it was");
    C(ThrowsInvalid(() => edit.Build()), "an edit builds one layer");

    // Remove some of them again, and an in-game bake's records.
    var edit2 = next.Edit();
    int removed = edit2.RemoveRecords(added.Take(3).Concat(new[] { added[0] }));
    C(removed == 3, "a record removed twice is removed once (3 of 4 given)");
    var (next2, changed2) = edit2.Build();
    C(changed2.SequenceEqual(new[] { inZone }), "only the zone that lost records is changed");
    C(RecordsOf(next2, inZone).SequenceEqual(sample.Records[inZone].Concat(added.Skip(3).Take(2)).OrderBy(r => r).ToList()), "the zone holds what is left");
    C(next2.Revision == baseLayer.Revision + 2, "revision + 2 after two changes");

    // A zone left with nothing leaves the index.
    var edit3 = next2.Edit();
    edit3.RemoveRecords(RecordsOf(next2, fresh));
    var (next3, changed3) = edit3.Build();
    C(!next3.Has(fresh) && changed3.SequenceEqual(new[] { fresh }), "a zone with no records and no sections leaves the index, and is reported");
    // A zone with sections stays when its records go.
    var withSections = new ZoneKey(0, 0);
    var edit4 = next3.Edit();
    int gone = edit4.RemoveWhere(new[] { withSections }, _ => true);
    var (next4, _) = edit4.Build();
    next4.TryGetZoneRow(withSections, out var kept);
    C(gone == sample.Records[withSections].Count && next4.Has(withSections) && kept.Placements == 0 && kept.HasGround && kept.HasClearMask && kept.HasPaint,
      "a zone with sections stays, without records, when they go");
    C(kept.YMin == 0f && kept.YMax == 0f, "and has no y bounds");
    var data = next4.Decode(kept);
    C(data.Extras.Length == 2 && data.Ground != null && data.Paint != null && data.ClearMask != null, "its sections are intact");

    // RemoveSource: every record of a bake, everywhere.
    int total1 = sample.Records.Values.Sum(l => l.Count(r => r.HasSource && r.Source == 1));
    var edit5 = baseLayer.Edit();
    int n = edit5.RemoveSource(1);
    var (next5, changed5) = edit5.Build();
    C(n == total1 && total1 > 0 && next5.RecordsOfSource(1) == 0 && next5.RecordsOfSource(2) == baseLayer.RecordsOfSource(2), $"RemoveSource(1) removes its {total1} records and no other");
    C(changed5.All(z => sample.Records[z].Any(r => r.HasSource && r.Source == 1)), "and only zones that had them are changed");
    C(ThrowsOther(() => baseLayer.Edit().RemoveSource(0)), "source 0 is not an in-game bake");
    // RemoveWhere over every zone: the compiler's records.
    var edit6 = baseLayer.Edit();
    int compilers = edit6.RemoveWhere(null, r => !r.HasSource);
    var (next6, _) = edit6.Build();
    C(compilers == baseLayer.RecordsOfSource(0) && next6.RecordsOfSource(0) == 0 && next6.Placements == baseLayer.Placements - compilers, "RemoveWhere over every zone takes the compiler's records");
  }

  private static bool ThrowsInvalid(Action action)
  {
    try
    {
      action();
    }
    catch (InvalidOperationException)
    {
      return true;
    }
    return false;
  }

  private static bool ThrowsOther(Action action)
  {
    try
    {
      action();
    }
    catch (ArgumentException)
    {
      return true;
    }
    return false;
  }

  private static void EditPaletteTest()
  {
    Section("edit: the palette only grows, and an equal entry is the same entry");
    var sample = MakeSample(12);
    var edit = sample.Layer.Edit();
    int count = sample.Layer.Palette.Count;
    C(edit.PaletteIndexFor(sample.Palette[2]) == 2 && edit.PaletteIndexFor(sample.Palette[0]) == 0, "an entry the palette has keeps its index");
    var copy = new PaletteEntry(sample.Palette[1].Candidates, sample.Palette[1].Role, sample.Palette[1].Collision, sample.Palette[1].Layer, sample.Palette[1].Flags,
      tags: sample.Palette[1].Tags);
    C(edit.PaletteIndexFor(copy) == 1, "an equal entry made another way is the same entry");
    var other = Entry("oak_beam", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 0, 0, 100, 100, 3000)], tags: [Tag.OfInt("MatVar0", 1)]);
    int at = edit.PaletteIndexFor(other);
    C(at == count && edit.PaletteIndexFor(other) == count, "a new entry is appended after the last, once");
    var variant = Entry("oak_beam", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 0, 0, 100, 100, 3000)], tags: [Tag.OfInt("MatVar0", 2)]);
    C(edit.PaletteIndexFor(variant) == count + 1, "an entry that differs in a tag is another entry (a palette entry per prefab and variants)");
    C(Refuses(() => edit.PaletteIndexFor(Entry("café")), "ASCII"), "a prefab name outside ASCII is refused at once");
    C(Refuses(() => edit.PaletteIndexFor(Entry(new string('x', 256))), "255"), "and a name over 255 characters");
    edit.AddRecords([ZoneRecord.CreateYaw(count, 5, 40, 5, 0)]);
    var (next, changed) = edit.Build();
    C(next.Palette.Count == count + 2 && next.Palette.Take(count).Zip(sample.Layer.Palette, (a, b) => a.Equals(b)).All(x => x), "the old entries are where they were");
    C(changed.Length == 1, "only the zone that got a record is reported (a palette that only grew changes no other zone)");
    var tooFar = LayerEdit.New("full");
    C(Refuses(() => tooFar.AddRecords([ZoneRecord.CreateYaw(3, 5, 40, 5, 0)]), "palette entry 3"), "a record naming an entry the palette lacks is refused");
    // 65,535 entries fit and the next does not.
    var big = LayerEdit.New("big");
    for (int i = 0; i < BakedFormat.MaxPalette; i++)
      big.PaletteIndexFor(Entry("p" + i));
    C(Refuses(() => big.PaletteIndexFor(Entry("one_more")), "65535"), "the 65,536th entry is refused");
    var built = big.Build().Layer;
    C(built.Palette.Count == BakedFormat.MaxPalette && BakedLayer.Read(built.Bytes, built.Length).Palette.Count == BakedFormat.MaxPalette, "a palette of 65,535 entries is written and read");
  }

  private static void EditRegistryTest()
  {
    Section("edit: operations, their numbers and value sets");
    var edit = LayerEdit.New("registry");
    edit.PaletteIndexFor(Entry("a"));
    C(edit.NextOperationNumber == 1, "operations start at 1");
    C(ThrowsOther(() => edit.AddOperation(SampleOperation(2, OperationKind.BakeArea))), "an operation takes the next number, not another");
    edit.AddOperation(SampleOperation(1, OperationKind.BakeArea, 2));
    C(edit.NextOperationNumber == 2, "and the counter moves");
    edit.EnsureNextOperation(5);
    C(edit.NextOperationNumber == 5, "a number the journal used is skipped");
    edit.EnsureNextOperation(3);
    C(edit.NextOperationNumber == 5, "the counter never goes back");
    edit.AddOperation(SampleOperation(5, OperationKind.BakeBox, 1));
    edit.AddRecords([ZoneRecord.CreateYaw(0, 1, 40, 1, 0, source: 1, valueSet: 1), ZoneRecord.CreateYaw(0, 2, 40, 1, 0, source: 5, valueSet: 0)]);
    var layer = edit.Build().Layer;
    C(layer.Registry.NextOperation == 6 && layer.Registry.Operations.Select(o => (int)o.Number).SequenceEqual(new[] { 1, 5 }), "the registry keeps its numbers (1 and 5) and the next is 6");

    // An undo: the records go, the operation is marked undone and its value sets are dropped. Numbers are not reused.
    var undo = layer.Edit();
    undo.RemoveSource(1);
    undo.SetOperationState(1, OperationState.Undone);
    var undone = undo.Build().Layer;
    C(undone.Registry.Operations[0].State == OperationState.Undone && undone.Registry.Operations[0].ValueSets.Length == 0, "an undone operation has no value sets");
    C(undone.Registry.NextOperation == 6 && undone.RecordsOfSource(1) == 0 && undone.RecordsOfSource(5) == 1, "the next number is still 6, the other bake's record stays");
    // Marking an operation undone while records still point to its value sets is refused by the layer's own check.
    var wrong = layer.Edit();
    wrong.SetOperationState(1, OperationState.Undone);
    C(Refuses(() => wrong.Build(), "does not list"), "a layer whose records name a value set the registry dropped is refused");
    C(ThrowsOther(() => layer.Edit().SetOperationState(9, OperationState.Undone)), "an operation that is not there cannot be marked");

    // At most 1,000 operations: older ones that no record points to go.
    var many = LayerEdit.New("many");
    many.PaletteIndexFor(Entry("a"));
    many.AddOperation(SampleOperation(1, OperationKind.BakeArea, 1));
    many.AddRecords([ZoneRecord.CreateYaw(0, 1, 40, 1, 0, source: 1, valueSet: 0)]);
    for (int i = 2; i <= 1005; i++)
      many.AddOperation(SampleOperation(i, OperationKind.Unbake));
    var trimmed = many.Build().Layer;
    var numbers = trimmed.Registry.Operations.Select(o => (int)o.Number).ToList();
    C(numbers.Count == 1000 && numbers.Contains(1) && numbers.Contains(1005) && !numbers.Contains(2) && !numbers.Contains(6) && numbers.Contains(7), "1,000 operations are kept: the bake with records, and the newest");
    C(trimmed.Registry.NextOperation == 1006, "the numbers go on (1006)");
    C(BakedLayer.Read(trimmed.Bytes, trimmed.Length).Registry.Operations.Count == 1000, "and read back");
  }

  private static void EditSectionsTest()
  {
    Section("edit: a compiler's sections are kept when a zone's records change");
    var sample = MakeSample(13);
    var layer = sample.Layer;
    var zone = new ZoneKey(0, 0);
    var edit = layer.Edit();
    edit.AddRecords([ZoneRecord.CreateYaw(0, zone.OriginX + 10, 50, zone.OriginZ + 10, 0)]);
    var next = edit.Build().Layer;
    layer.TryGetZoneRow(zone, out var before);
    next.TryGetZoneRow(zone, out var after);
    var a = layer.Decode(before);
    var b = next.Decode(after);
    C(before.Flags == after.Flags && a.ClearMask.SequenceEqual(b.ClearMask) && a.Paint.SequenceEqual(b.Paint) && a.Ground.Heights.SequenceEqual(b.Ground.Heights)
      && a.Ground.Weights.SequenceEqual(b.Ground.Weights) && a.Ground.Base == b.Ground.Base && b.Extras.Length == 2, "mask, ground, paint, extras and the zone flags survive");
    C(next.Ground.Count == layer.Ground.Count && next.Ground.TryGet(zone, out var g) && g.Base == a.Ground.Base, "the ground set follows");
    // Ground sampling: bilinear, exact on the vertices.
    var ground = layer.Ground;
    C(ground.TrySample(zone.OriginX + 10, zone.OriginZ + 20, out float h, out float w) && Math.Abs(h - a.Ground.HeightAt(20 * 65 + 10)) < 1e-4 && Math.Abs(w - a.Ground.WeightAt(20 * 65 + 10)) < 1e-6,
      "the ground at a vertex is the vertex's");
    ground.TrySample(zone.OriginX + 10.5, zone.OriginZ + 20, out float h2, out float w2);
    float expected = (a.Ground.HeightAt(20 * 65 + 10) + a.Ground.HeightAt(20 * 65 + 11)) / 2;
    float expectedW = (a.Ground.WeightAt(20 * 65 + 10) + a.Ground.WeightAt(20 * 65 + 11)) / 2;
    C(Math.Abs(h2 - expected) < 1e-4 && Math.Abs(w2 - expectedW) < 1e-6, "halfway between two vertices is their mean");
    C(!ground.TrySample(zone.OriginX + 80, zone.OriginZ + 80, out _, out _), "no ground in a zone that has none");
    C(ground.TrySample(zone.OriginX + 64 - 1e-9, zone.OriginZ + 5, out float nearEdge, out _) && Math.Abs(nearEdge - a.Ground.HeightAt(5 * 65 + 64)) < 1e-2, "at the zone's edge the last vertex");
  }
}

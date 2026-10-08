// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// What a checked block leaves in the snapshot (ZoneInfo: the (source, id) of every Live record, how many records each in-game bake owns, the
// zone's ground), that an edit shares it with the layer it grows from, and that a (source, id) is unique among the Live records of the whole layer
// across an edit (spec 2.2, 6.4): one the edit adds to a zone, one that stands in a block the edit copies, one that comes with the other bake's
// number, and one that goes in the same edit that its twin arrives.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static void InfoZoneTest()
  {
    Section("zone info: Live keys, a bake's records and the ground, per zone, shared with the layer an edit grows from");
    var sample = MakeSample(51);
    var layer = sample.Layer;
    string problem = null;
    long liveTotal = 0;
    foreach (var row in layer.Zones)
    {
      var info = layer.InfoOf(row.Index);
      var records = sample.Records.TryGetValue(row.Key, out var list) ? list : new List<ZoneRecord>();
      var live = records.Where(r => layer.Palette[r.Palette].Role == BakedRole.Live).Select(r => BakedFormat.LiveKey(r.SourceNumber, r.Id)).OrderBy(k => k).ToList();
      liveTotal += live.Count;
      if (!info.LiveKeys.OrderBy(k => k).SequenceEqual(live))
        problem ??= $"zone {row.Key}: Live keys {info.LiveKeys.Length}, expected {live.Count}";
      var counts = records.Where(r => r.HasSource).GroupBy(r => (ushort)r.Source).ToDictionary(g => g.Key, g => g.Count());
      if (info.SourceCounts.Length != counts.Count || info.SourceCounts.Any(kv => !counts.TryGetValue(kv.Key, out int n) || n != kv.Value))
        problem ??= $"zone {row.Key}: the bakes' records {string.Join(",", info.SourceCounts.Select(kv => kv.Key + ":" + kv.Value))}, expected {string.Join(",", counts.Select(kv => kv.Key + ":" + kv.Value))}";
      if ((info.Ground != null) != row.HasGround)
        problem ??= $"zone {row.Key}: ground {(info.Ground != null)}, the index says {row.HasGround}";
      if (info.Ground != null && layer.Ground.TryGet(row.Key, out var ground) && !ReferenceEquals(ground, info.Ground))
        problem ??= $"zone {row.Key}: the layer's ground set holds another ground than the zone's";
    }
    C(problem == null, problem ?? "every zone: its Live keys are those of its Live records (source and id), its bakes' counts are the records with flag 8, its ground is the index's");
    C(liveTotal > 0 && layer.Zones.Sum(z => layer.InfoOf(z.Index).LiveKeys.Length) == liveTotal, $"the layer holds {liveTotal} Live keys in all");
    C(layer.Ground.Count == layer.Zones.Count(z => z.HasGround) && layer.Zones.Where(z => z.HasGround).All(z => layer.Ground.TryGet(z.Key, out _)), "the layer's ground set has the zones with ground");
    var bySource = layer.RecordsBySource();
    C(bySource.All(kv => kv.Value == sample.Records.Values.Sum(l => l.Count(r => r.SourceNumber == kv.Key))) && bySource.Values.Sum() == layer.Placements, "RecordsBySource adds up from the zones' counts");
    C(layer.RecordsOfSource(1) + layer.RecordsOfSource(2) + layer.RecordsOfSource(0) == layer.Placements && layer.RecordsOfSource(3) == 0, "and RecordsOfSource: source 0 is what no bake owns");

    // An edit that leaves a zone alone shares that zone's info; the zone it changes has a new one.
    var edit = layer.Edit();
    var touched = layer.Zones.First(z => z.Placements > 0).Key;
    edit.AddRecords([ZoneRecord.CreateYaw(0, touched.OriginX + 1, 70, touched.OriginZ + 1, 0)]);
    var (next, changed) = edit.Build();
    C(changed.SequenceEqual(new[] { touched }), "the edit changed the one zone");
    bool shared = true, fresh = false;
    foreach (var row in layer.Zones)
    {
      next.TryGetZoneRow(row.Key, out var now);
      bool same = ReferenceEquals(next.InfoOf(now.Index), layer.InfoOf(row.Index));
      if (row.Key == touched)
        fresh = !same;
      else
        shared &= same;
    }
    C(shared && fresh, "the zones the edit did not touch share the old layer's ZoneInfo, and the zone it touched has its own");
    C(next.Zones.Sum(z => next.InfoOf(z.Index).LiveKeys.Length) == liveTotal, "and the Live keys still add up");
  }

  // A layer with two Live pieces in one zone and one in another, a Static, and a registry with a bake.
  private static (BakedLayer Layer, ZoneKey A, ZoneKey B) InfoLayer()
  {
    var edit = LayerEdit.New("info");
    edit.PaletteIndexFor(Entry("door", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect));
    edit.PaletteIndexFor(Entry("wall", BakedRole.Static));
    edit.AddOperation(SampleOperation(1, OperationKind.BakeArea, 2));
    var a = new ZoneKey(0, 0);
    var b = new ZoneKey(5, 5);
    edit.AddRecords([
      ZoneRecord.CreateYaw(0, a.OriginX + 4, 40, a.OriginZ + 4, 0, id: 1),
      ZoneRecord.CreateYaw(0, a.OriginX + 8, 40, a.OriginZ + 4, 0, id: 2),
      ZoneRecord.CreateYaw(1, a.OriginX + 12, 40, a.OriginZ + 4, 0),
      ZoneRecord.CreateYaw(0, b.OriginX + 4, 40, b.OriginZ + 4, 0, id: 3),
    ]);
    return (edit.Build().Layer, a, b);
  }

  private static void InfoDuplicateIdsTest()
  {
    Section("duplicate ids: a Live (source, id) is unique in the whole layer, also across an edit");
    var (layer, a, b) = InfoLayer();
    C(layer.Placements == 4 && layer.Zones.Sum(z => layer.InfoOf(z.Index).LiveKeys.Length) == 3, "the sample layer: four records, three Live keys");
    ZoneRecord Live(ZoneKey zone, uint id, int source = 0, double dx = 20) =>
      ZoneRecord.CreateYaw(0, zone.OriginX + dx, 40, zone.OriginZ + 20, 0, id: id, source: source);

    // Another zone's id, added into a zone the edit rewrites, while the other zone's block is copied.
    var edit = layer.Edit();
    edit.AddRecords([Live(a, 3)]);
    var refusal = RefusalOf(() => edit.Build());
    C(refusal != null && refusal.Contains("repeats an id", StringComparison.OrdinalIgnoreCase) && refusal.Contains("source 0") && refusal.Contains("id 3"),
      "an id that stands in a block the edit copies is refused, naming source and id: " + refusal);
    C(refusal != null && refusal.Contains("zone "), "and says in which zone it was found: " + refusal);
    C(layer.Placements == 4 && layer.Edit().Build().Layer.Placements == 4, "the layer it started from is as it was, and can be edited again");
    C(ThrowsInvalid(() => edit.Build()), "a failed edit cannot be built again");

    // The same id twice in one new zone.
    var edit2 = layer.Edit();
    var c = new ZoneKey(9, 9);
    edit2.AddRecords([Live(c, 9, dx: 5), Live(c, 9, dx: 30)]);
    C(Refuses(() => edit2.Build(), "repeats an id", "id 9"), "the same id twice in a new zone is refused");

    // The same id in the same zone as its twin.
    var edit3 = layer.Edit();
    edit3.AddRecords([Live(a, 2, dx: 40)]);
    C(Refuses(() => edit3.Build(), "repeats an id", "id 2"), "and in the zone of its twin");

    // Another bake's number makes another key: (source 1, id 3) is not (source 0, id 3).
    var edit4 = layer.Edit();
    edit4.AddRecords([Live(a, 3, source: 1)]);
    var other = edit4.Build().Layer;
    C(other.Placements == 5 && other.Zones.Sum(z => other.InfoOf(z.Index).LiveKeys.Length) == 4, "the same id under bake 1 is another key, and is accepted");
    C(other.Zones.SelectMany(z => other.InfoOf(z.Index).LiveKeys).Contains(BakedFormat.LiveKey(1, 3)) && other.Zones.SelectMany(z => other.InfoOf(z.Index).LiveKeys).Contains(BakedFormat.LiveKey(0, 3)),
      "both keys are in the layer");
    var edit5 = other.Edit();
    edit5.AddRecords([Live(b, 3, source: 1, dx: 40)]);
    C(Refuses(() => edit5.Build(), "repeats an id", "source 1", "id 3"), "but not twice under bake 1");

    // Removing the twin in the same edit makes room.
    var edit6 = layer.Edit();
    var twin = ZoneRecord.CreateYaw(0, b.OriginX + 4, 40, b.OriginZ + 4, 0, id: 3);
    C(edit6.RemoveRecords([twin]) == 1, "the twin is found by value");
    edit6.AddRecords([Live(a, 3)]);
    var moved = edit6.Build().Layer;
    C(moved.Placements == 4 && moved.Zones.SelectMany(z => moved.InfoOf(z.Index).LiveKeys).Count(k => k == BakedFormat.LiveKey(0, 3)) == 1 && !moved.Has(b),
      "removing the twin in the same edit lets the id move to another zone (and the emptied zone goes)");
    var edit7 = layer.Edit();
    edit7.RemoveRecords([ZoneRecord.CreateYaw(0, a.OriginX + 4, 40, a.OriginZ + 4, 0, id: 1)]);
    edit7.AddRecords([ZoneRecord.CreateYaw(0, a.OriginX + 4, 41, a.OriginZ + 4, 90, id: 1)]);
    var moved2 = edit7.Build().Layer;
    C(moved2.Placements == 4 && RecordsOf(moved2, a).Count(r => r.HasId && r.Id == 1) == 1, "a Live piece taken out and put back in one zone (a rebake) keeps its id once");

    // Only Live records need a unique id.
    var edit8 = layer.Edit();
    edit8.AddRecords([ZoneRecord.CreateYaw(1, a.OriginX + 30, 40, a.OriginZ + 30, 0, id: 3), ZoneRecord.CreateYaw(1, a.OriginX + 31, 40, a.OriginZ + 30, 0, id: 3)]);
    var statics = edit8.Build().Layer;
    C(statics.Placements == 6 && statics.Zones.Sum(z => statics.InfoOf(z.Index).LiveKeys.Length) == 3, "Static records may repeat an id, and add no Live key");

    // A Live record needs an id at all.
    var edit9 = layer.Edit();
    edit9.AddRecords([ZoneRecord.CreateYaw(0, a.OriginX + 50, 40, a.OriginZ + 50, 0)]);
    C(Refuses(() => edit9.Build(), "Live record has no id"), "a Live record without an id is refused");

    // A bake's record needs the registry to list that bake and value set.
    var edit10 = layer.Edit();
    edit10.AddRecords([Live(c, 50, source: 2)]);
    C(Refuses(() => edit10.Build(), "registry"), "a Live record under a bake the registry does not list is refused");
    var edit11 = layer.Edit();
    edit11.AddRecords([ZoneRecord.CreateYaw(0, c.OriginX + 20, 40, c.OriginZ + 20, 0, id: 51, source: 1, valueSet: 1), ZoneRecord.CreateYaw(0, c.OriginX + 22, 40, c.OriginZ + 20, 0, id: 52, source: 1, valueSet: 0)]);
    var ok = edit11.Build().Layer;
    C(ok.Placements == 6 && ok.RecordsOfSource(1) == 2, "and one under bake 1, whose two value sets the registry lists, is accepted");
    var edit12 = layer.Edit();
    edit12.AddRecords([ZoneRecord.CreateYaw(0, c.OriginX + 20, 40, c.OriginZ + 20, 0, id: 53, source: 1, valueSet: 2)]);
    C(Refuses(() => edit12.Build(), "value set"), "a value set the bake does not have is refused");

    // A compiler's new file loaded over a layer with an in-game Live piece that has the file's id: different sources, so both stay.
    var fileEdit = LayerEdit.New("file");
    fileEdit.PaletteIndexFor(Entry("door", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect));
    fileEdit.AddRecords([ZoneRecord.CreateYaw(0, a.OriginX + 4, 40, a.OriginZ + 4, 0, id: 1), ZoneRecord.CreateYaw(0, a.OriginX + 8, 40, a.OriginZ + 4, 0, id: 2)]);
    var file = fileEdit.Build().Layer;
    var inGame = layer.Edit();
    inGame.AddRecords([Live(b, 1, source: 1, dx: 30)]);
    var withBake = inGame.Build().Layer;
    var load = withBake.Edit();
    int leftOut = load.ReplaceSource0(file);
    var loaded = load.Build().Layer;
    var keys = loaded.Zones.SelectMany(z => loaded.InfoOf(z.Index).LiveKeys).OrderBy(k => k).ToList();
    C(leftOut == 0 && keys.SequenceEqual(new[] { BakedFormat.LiveKey(0, 1), BakedFormat.LiveKey(0, 2), BakedFormat.LiveKey(1, 1) }.OrderBy(k => k)),
      "a loaded file's (source 0, id 1) and an in-game piece's (source 1, id 1) are two keys: " + string.Join(" ", keys.Select(k => (int)(k >> 32) + "/" + (uint)k)));
  }
}

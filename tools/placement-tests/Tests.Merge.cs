// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// `bc_bake load` (spec 6.2): a compiler's new file replaces source 0 and its zone sections; every in-game record stays; the palette is rebuilt;
// the revision is the old one plus 1 whatever the file says; operation numbers are never reused.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static PaletteEntry Wall() => Entry("stone_wall", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 500, 0, 2000, 1000, 400)]);
  private static PaletteEntry Door() => Entry("wood_door", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect, tags: [Tag.OfBool("VALtima_TownPiece", true), Tag.OfString("VALtima_Kind", "door")]);
  private static PaletteEntry Roof() => Entry("roof", BakedRole.Static, BakedCollision.None);
  private static PaletteEntry Gate() => Entry("iron_gate", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect, tags: [Tag.OfString("VALtima_Kind", "door")]);
  private static PaletteEntry Beam() => Entry("oak_beam", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 0, 0, 100, 100, 3000)], tags: [Tag.OfInt("MatVar0", 1)]);
  private static PaletteEntry Chest() => Entry("chest_live", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect, tags: [Tag.OfBool("bc", true)]);

  // A compiler's file: the palette in the order given, a few zones with sections, Live records with ids. `variant` changes what is in the zones.
  private static BakedLayer CompilerFile(PaletteEntry[] palette, int variant, int zoneCount = 3)
  {
    var edit = LayerEdit.New("Uo2Bc (merge test)");
    foreach (var e in palette)
      edit.PaletteIndexFor(e);
    int Index(string name) => Array.FindIndex(palette, e => e.Name == name);
    var random = new Lcg(99);
    uint id = 1000;
    var main = new ZoneKey(10, 10);
    var side = new ZoneKey(11, 10);
    for (int i = 0; i < 20; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(Index("stone_wall"), main.OriginX + 2 + i * 2 + (variant == 1 && i < 10 ? 1 : 0), 31, main.OriginZ + 10, 90)], 3f);
    for (int i = 0; i < 3; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(Index(variant == 1 && i == 0 ? "iron_gate" : "wood_door"), main.OriginX + 5 + i * 10, 31, main.OriginZ + 20, 0, id: id++)], 3f);
    for (int i = 0; i < 5; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(Index("roof"), main.OriginX + 4 + i * 6, 35, main.OriginZ + 10, 0)], 3f);
    for (int i = 0; i < 15; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(Index("stone_wall"), side.OriginX + 4 + i * 3, 31, side.OriginZ + 7, 0)], 3f);
    edit.SetSections(main, new ZoneSections { Flags = ZoneFlags.NoVegetation, Mask = MaskOf(1, 2), Ground = GroundOf(random, variant == 1 ? 33f : 31f), Paint = Enumerable.Range(0, 4096).Select(i => (byte)(i % 3)).ToArray() });
    if (variant == 0)
      edit.SetSections(new ZoneKey(12, 10), new ZoneSections { Mask = MaskOf(7) });
    else
      edit.AddRecords([ZoneRecord.CreateYaw(Index("stone_wall"), new ZoneKey(13, 10).OriginX + 8, 31, new ZoneKey(13, 10).OriginZ + 8, 0)], 3f);
    return edit.Build().Layer;
  }

  // One line for each record of a layer with its entry's name and tags, the zone and everything the record holds: what a record is, apart from
  // the number of its palette entry (which a rebuilt palette changes).
  private static List<string> Lines(BakedLayer layer, Func<ZoneRecord, bool> filter)
  {
    var lines = new List<string>();
    foreach (var zone in layer.Zones)
    {
      var data = layer.Decode(zone);
      foreach (var r in data.Records().Where(filter))
      {
        var entry = layer.Palette[r.Palette];
        lines.Add($"{zone.Key}|{entry.Name}|{string.Join(",", entry.Tags.Select(t => t.ToString()))}|{r.X}|{r.Z}|{r.Y}|{r.Yaw}|{r.Flags}|{r.ScaleX}|{r.Id}|{r.Source}|{r.ValueSet}|{r.Seed}");
      }
    }
    lines.Sort(StringComparer.Ordinal);
    return lines;
  }

  private static void MergeLoadTest()
  {
    Section("merge: bc_bake load keeps every in-game record, rebuilds the palette, replaces the compiler's");
    var fileA = CompilerFile([Wall(), Door(), Roof()], 0);
    // The world made from the compiler's file: it keeps the file's bytes and revision.
    C(fileA.Revision == 1u, "the compiler's file is revision 1");
    // Two in-game bakes on it.
    var edit1 = fileA.Edit();
    int beam = edit1.PaletteIndexFor(Beam()), chest = edit1.PaletteIndexFor(Chest()), wall = edit1.PaletteIndexFor(Wall());
    C(beam == 3 && chest == 4 && wall == 0, "bake 1 appends its entries after the file's (3, 4) and reuses the file's wall (0)");
    ushort op1 = edit1.NextOperationNumber;
    edit1.AddOperation(SampleOperation(op1, OperationKind.BakeArea, 2));
    var main = new ZoneKey(10, 10);
    var far = new ZoneKey(14, 14);
    edit1.AddRecords(
    [
      ZoneRecord.CreateYaw(beam, main.OriginX + 30, 40, main.OriginZ + 30, 45, source: op1, valueSet: 1, seed: 100),
      ZoneRecord.CreateYaw(wall, main.OriginX + 31, 40, main.OriginZ + 30, 45, source: op1, valueSet: 0),
      ZoneRecord.CreateYaw(chest, main.OriginX + 32, 40, main.OriginZ + 30, 0, id: 1, source: op1, valueSet: 0),
      ZoneRecord.CreateYaw(beam, far.OriginX + 3, 41, far.OriginZ + 3, 10, source: op1, valueSet: 1),
      ZoneRecord.CreateYaw(beam, far.OriginX + 6, 41, far.OriginZ + 3, 10, source: op1, valueSet: 0, scale: new Vector3(1, 2, 1)),
    ], 3f);
    var world1 = edit1.Build().Layer;
    var edit2 = world1.Edit();
    ushort op2 = edit2.NextOperationNumber;
    edit2.AddOperation(SampleOperation(op2, OperationKind.BakeBox, 1));
    edit2.AddRecords([ZoneRecord.CreateYaw(beam, far.OriginX + 9, 41, far.OriginZ + 3, 10, source: op2, valueSet: 0), ZoneRecord.CreateYaw(chest, far.OriginX + 12, 41, far.OriginZ + 3, 0, id: 1, source: op2, valueSet: 0)], 3f);
    var world = edit2.Build().Layer;
    C(op1 == 1 && op2 == 2 && world.Revision == 3u && world.RecordsOfSource(1) == 5 && world.RecordsOfSource(2) == 2, "two bakes on the file: revision 3, 5 and 2 in-game records");
    var inGameBefore = Lines(world, r => r.HasSource);
    var compilerBefore = Lines(world, r => !r.HasSource);

    // A newer compiler's file: another palette order (a new entry in the middle), another ground, a zone gone, a zone new, one zone unchanged.
    var fileB = CompilerFile([Wall(), Gate(), Door(), Roof()], 1);
    var load = world.Edit();
    int leftOut = load.ReplaceSource0(fileB);
    ushort loadOp = load.NextOperationNumber;
    load.AddOperation(new OperationInfo(loadOp, OperationKind.Load, OperationState.InLayer, 1_700_000_100L, "server console", 0, 0, 0, 0, 0, 0, 0, 0, "0.10.4"));
    var (merged, changed) = load.Build();
    C(leftOut == 0, "the file holds no in-game record");
    C(merged.Revision == world.Revision + 1, $"the revision is the old one plus 1 ({merged.Revision}), whatever the file says ({fileB.Revision})");
    C(loadOp == 3 && merged.Registry.NextOperation == 4 && merged.Registry.Operations.Select(o => (int)o.Number).SequenceEqual(new[] { 1, 2, 3 }), "operation numbers go on (the load is 3) and none is reused");
    C(merged.Registry.Operations[0].ValueSets.Length == 2 && merged.Registry.Operations[1].ValueSets.Length == 1, "the bakes' value sets are kept");
    C(merged.Producer == fileB.Producer, "the producer is the compiler's");

    // The palette: the file's entries in its order, then the entries in-game records still use, in first use order.
    var names = merged.Palette.Select(e => e.Name).ToList();
    C(names.Take(4).SequenceEqual(new[] { "stone_wall", "iron_gate", "wood_door", "roof" }) && names.Skip(4).SequenceEqual(new[] { "oak_beam", "chest_live" }),
      "the palette is the file's four, then oak_beam and chest_live: " + string.Join(", ", names));
    C(merged.Palette.Take(4).Zip(fileB.Palette, (a, b) => a.Equals(b)).All(x => x), "the file's entries are as the file has them");

    // In-game records: every one is there, as it was.
    C(Lines(merged, r => r.HasSource).SequenceEqual(inGameBefore), "every in-game record is there, unchanged (zone, entry, place, turn, scale, id, source, value set, seed)");
    // The compiler's: the file's.
    C(Lines(merged, r => !r.HasSource).SequenceEqual(Lines(fileB, r => !r.HasSource)), "the compiler's records are the new file's");
    C(Lines(merged, r => !r.HasSource).SequenceEqual(compilerBefore) == false, "and not the old file's");

    // The sections: the file's.
    var mainRowB = fileB.Zones.First(z => z.Key == main);
    var mainRow = merged.Zones.First(z => z.Key == main);
    var sectionsB = fileB.Decode(mainRowB);
    var sectionsMerged = merged.Decode(mainRow);
    C(sectionsMerged.Ground.Base == 33f && sectionsMerged.Ground.Heights.SequenceEqual(sectionsB.Ground.Heights) && sectionsMerged.ClearMask.SequenceEqual(sectionsB.ClearMask)
      && sectionsMerged.Paint.SequenceEqual(sectionsB.Paint) && (mainRow.Flags & ZoneFlags.NoVegetation) != 0, "the zone's ground, mask, paint and flags are the new file's");
    C(!merged.Has(new ZoneKey(12, 10)), "a zone only the old file had (a mask) is gone");
    C(merged.Has(new ZoneKey(13, 10)) && merged.Has(far), "a zone only the new file has is there, and the zone with only in-game records stays");
    merged.TryGetZoneRow(far, out var farRow);
    C(farRow.Placements == 4 && !farRow.HasGround && farRow.Flags == ZoneFlags.None, "a zone of in-game records only has no section");

    // The Live records of the new file are copied as they are (with their ids) and the registry's value sets are still valid.
    var liveIds = merged.AllRecords().Where(r => merged.Palette[r.Palette].Role == BakedRole.Live && !r.HasSource).Select(r => r.Id).OrderBy(x => x).ToList();
    C(liveIds.SequenceEqual(new uint[] { 1000, 1001, 1002 }), "the new file's Live ids");
    C(merged.AllRecords().Count(r => r.HasId && r.HasSource) == 2 && merged.AllRecords().Where(r => r.HasId && r.HasSource).Select(r => (r.Source, r.Id)).Distinct().Count() == 2,
      "two in-game Live records share id 1, under bakes 1 and 2");

    // Changed: the zone whose block is the same as before (side), is not; the others are.
    var side = new ZoneKey(11, 10);
    C(!changed.Contains(side), "the zone (11, 10), identical in both files, is not changed");
    C(changed.Contains(main) && changed.Contains(new ZoneKey(12, 10)) && changed.Contains(new ZoneKey(13, 10)), "the zones that differ or went or came are changed");
    C(changed.SequenceEqual(changed.OrderBy(k => k)), "the changed zones come in (z, x) order");
    C(merged.Id != world.Id && merged.Length > 0, "a different layer");
    C(Differ(BakedLayer.Read(merged.Bytes, merged.Length), merged) == null, "and one that reads back as it is");

    // A load into the same file again: nothing changes but the revision and the registry.
    var again = merged.Edit();
    again.ReplaceSource0(fileB);
    var (merged2, changed2) = again.Build();
    C(Lines(merged2, _ => true).SequenceEqual(Lines(merged, _ => true)), "loading the same file again changes no record");
    C(changed2.Length == 0, "and no zone is reported changed (" + string.Join(" ", changed2) + ")");
    C(merged2.Revision == merged.Revision + 1, "though the revision goes up");

    // Records of the file that an in-game bake owns (a file from an exported world) are left out, and counted.
    var exportEdit = fileB.Edit();
    exportEdit.PaletteIndexFor(Beam());
    exportEdit.AddOperation(SampleOperation(exportEdit.NextOperationNumber, OperationKind.BakeArea, 1));
    exportEdit.AddRecords([ZoneRecord.CreateYaw(4, main.OriginX + 50, 40, main.OriginZ + 50, 0, source: 1, valueSet: 0), ZoneRecord.CreateYaw(4, far.OriginX + 50 - 64, 40, far.OriginZ + 5, 0, source: 1, valueSet: 0)], 3f);
    var exported = exportEdit.Build().Layer;
    C(exported.RecordsOfSource(1) == 2, "the exported layer has two in-game records");
    var fresh = LayerEdit.New();
    int dropped = fresh.ReplaceSource0(exported);
    var (fromExport, _) = fresh.Build();
    C(dropped == 2 && fromExport.RecordsOfSource(1) == 0 && fromExport.Placements == fileB.Placements, "loading it leaves the two in-game records out and says so");
    C(!fromExport.HasRegistry, "and no registry is carried over from the file");
    var intoWorld = world.Edit();
    int dropped2 = intoWorld.ReplaceSource0(exported);
    var (inWorld, _) = intoWorld.Build();
    C(dropped2 == 2 && inWorld.RecordsOfSource(1) == 5 && inWorld.RecordsOfSource(2) == 2, "into a world with bakes of its own, its records are kept and the file's are left out");

    // Order of use: ReplaceSource0 comes first.
    var late = world.Edit();
    late.AddRecords([ZoneRecord.CreateYaw(0, 1, 30, 1, 0)]);
    C(ThrowsInvalid(() => late.ReplaceSource0(fileB)), "ReplaceSource0 after another change is refused");
  }

  private static void MergeEditAfterLoadTest()
  {
    Section("merge: edits go on after a load, in the same edit");
    var fileA = CompilerFile([Wall(), Door(), Roof()], 0);
    var edit = LayerEdit.New();
    edit.ReplaceSource0(fileA);
    int beam = edit.PaletteIndexFor(Beam());
    edit.AddOperation(SampleOperation(edit.NextOperationNumber, OperationKind.BakeArea, 1));
    var main = new ZoneKey(10, 10);
    edit.AddRecords([ZoneRecord.CreateYaw(beam, main.OriginX + 40, 40, main.OriginZ + 40, 0, source: 1, valueSet: 0)], 3f);
    int removed = edit.RemoveWhere([main], r => !r.HasSource && fileA.Palette[r.Palette].Name == "roof");
    var layer = edit.Build().Layer;
    C(removed == 5 && layer.Placements == fileA.Placements - 5 + 1 && layer.RecordsOfSource(1) == 1, "a load, an added bake and a removal in one edit");
    C(layer.Revision == 1u && layer.Palette.Count == 4, "a world with no layer before it is revision 1, and has the beam appended");
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of unbake, undo and drop (BakeRunner.Unbake.cs, BakeRunner.Undo.cs) on the stand-ins of Tests.BakeRunner.cs: the pieces come
// back as they were (to a millimetre, a hundredth of a degree, every value), the order of the steps, what each refuses, and the way out of 4.5.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static int BakeUnbakeRun(BakeFx fx, UnbakeScope scope, string words = "confirm", string echo = "unbake world") =>
    BakeDrive(BakeRunner.Unbake(fx.Ctx, scope, BakeWordsOf(words), echo), fx, "an unbake");

  private static int BakeUndoRun(BakeFx fx, int? number, string words = "confirm") =>
    BakeDrive(BakeRunner.Undo(fx.Ctx, number, BakeWordsOf(words)), fx, "an undo");

  private static int BakeDropRun(BakeFx fx, DropScope scope, string words = "confirm") =>
    BakeDrive(BakeRunner.Drop(fx.Ctx, scope, BakeWordsOf(words)), fx, "a drop");

  private static readonly UnbakeScope BakeWholeWorld = new();

  // The object that is this piece again: its prefab, its place to a millimetre, its turn to a hundredth of a degree and every value it had.
  private static PieceCopy BakeFindAgain(BakeFx fx, PieceCopy piece) =>
    fx.World.Objects.Values.FirstOrDefault(o => o.Prefab == piece.Prefab && Math.Abs(o.X - piece.X) < 0.001 && Math.Abs(o.Y - piece.Y) < 0.001 && Math.Abs(o.Z - piece.Z) < 0.001
      && BakeMath.AngleBetween(BakeMath.EulerToQuaternion(o.RotX, o.RotY, o.RotZ), BakeMath.EulerToQuaternion(piece.RotX, piece.RotY, piece.RotZ)) < 0.01
      && o.Values.SequenceEqual(piece.Values));

  private static string BakeDescribeValues(PieceCopy piece) => string.Join(",", piece.Values.Select(v => v.Describe()));

  private static void BakeUnbakeTest()
  {
    Section("unbake: records become the pieces they were");
    BakeRunner.Reset();
    var fx = BakeNewFx("unbake", BakeStandardWorld, out var taken);
    var door0 = BakeClone(fx.World.Objects.Values.First(o => o.PrefabName == "wood_door"));
    var chest0 = BakeClone(fx.World.Objects.Values.First(o => o.PrefabName == "piece_chest"));
    BakeRun(fx, BakeStandardArea(), "town confirm");
    var recordsOfBake = BakeKeysOfSource(fx.Port, 1);
    int objectsBaked = fx.World.Objects.Count;
    int events = fx.Clock.Events.Count;

    BakeRunner.Reset();
    fx.Said.Clear();
    BakeUnbakeRun(fx, new UnbakeScope { Area = BakeStandardArea() }, "", "unbake 30 at 30 0");
    var dry = string.Join("\n", fx.Said);
    C(fx.Clock.Events.Count == events && fx.Said[0] == "bc_bake unbake 30 at 30 0: a dry run, nothing changes. 'bc_bake unbake 30 at 30 0 confirm' unbakes.", "a dry run says so and changes nothing");
    C(dry.Contains("Found 10 records here: 10 baked in game.") && dry.Contains("To unbake: 8 pieces (stone_wall_2x1 4, wood_floor 2, wood_beam 1 and 1 more kinds). 2 live pieces lose their bake keys and stay where they are."), "it counts the records and the live pieces: " + dry);
    C(dry.Contains("Objects in these zones: 4 now, 12 after.") && dry.Contains("Undo file: about") && dry.Contains("2 players connected"), "the objects before and after, the undo file and the players");

    fx.Said.Clear();
    int frames = BakeUnbakeRun(fx, new UnbakeScope { Area = BakeStandardArea() }, "confirm", "unbake 30 at 30 0");
    var text = string.Join("\n", fx.Said);
    var events2 = fx.Clock.Events.Skip(events).ToList();
    var want = new List<string> { "write the undo file of 2", "state Prepared", "state Unbaking" };
    want.AddRange(Enumerable.Repeat("make", 8));
    want.AddRange(Enumerable.Repeat("key off", 8));
    want.AddRange(["layer", "push r2", "reconcile r2"]);
    C(events2.Select(e => e.StartsWith("make ") ? "make" : e).SequenceEqual(want), "the undo file, the state, the pieces made, the town pieces freed, then the layer and the push (and the reconciliation of the live pieces): " + string.Join(" > ", events2));
    C(frames >= 3, $"it took {frames} frames");
    C(text.Contains("bc_bake: unbake 2 started, 8 pieces. Undo file written.") && text.Contains("unbake 2 done in") && text.Contains("8 pieces made, 2 live pieces freed, 10 records out of the layer.") && text.Contains("'bc_bake undo' puts the records back"), "what it says: " + text);

    foreach (var piece in taken)
      C(BakeFindAgain(fx, piece) != null, $"the {piece.PrefabName} at {piece.X}, {piece.Z} is an object again with every value ({BakeDescribeValues(piece)})");
    C(fx.World.Objects.Count == objectsBaked + 8, "eight pieces more");
    var door = fx.World.Objects.Values.First(o => o.PrefabName == "wood_door");
    var chest = fx.World.Objects.Values.First(o => o.PrefabName == "piece_chest");
    C(!BakeHasAnyKey(door) && !BakeHasAnyKey(chest), "the town pieces have lost their keys");
    C(door.Values.SequenceEqual(door0.Values) && chest.Values.SequenceEqual(chest0.Values), "and nothing else: their state, contents and creator are as they were");
    C(fx.Layer.Placements == 0 && fx.Layer.Registry.Operations.Count == 2 && fx.Layer.Revision == 2, "the layer holds no record: revision 2");
    var op = fx.Layer.Registry.Operations[1];
    C(op.Number == 2 && op.Kind == OperationKind.Unbake && op.State == OperationState.InLayer && op.Added == 0 && op.Removed == 10 && op.Adopted == 2, "the registry says an unbake took out 10 records and freed 2 live pieces");
    var journal = new BakeJournal(fx.Folder);
    var data = journal.Read(2);
    C(journal.GetState(2) == BakeState.Unbaking && data.Kind == OperationKind.Unbake && data.Created.Count == 8 && data.Records.Count == 10 && data.Adopted.Count == 2, "the undo file: Unbaking, 8 pieces, 10 records, 2 freed pieces");
    C(data.Adopted.Any(a => a.Place.PrefabName == "wood_door" && a.Source == 1 && a.Id == 1), "it says where each freed piece stands and which record it was");

    Section("undo: an unbake is put back, the records first and then the pieces go");
    int before = fx.Clock.Events.Count;
    BakeRunner.Reset();
    fx.Said.Clear();
    BakeUndoRun(fx, null, "");
    C(fx.Said.Count > 0 && fx.Said[0] == "bc_bake undo 2: a dry run, nothing changes. 'bc_bake undo 2 confirm' undoes it." && fx.Clock.Events.Count == before, "a dry run names the newest operation and changes nothing: " + fx.Said[0]);
    C(fx.Said.Any(l => l.Contains("Unbake 2") && l.Contains("the records it took out come back, and the pieces it made go")) && fx.Said.Any(l => l.Contains("2 town pieces get their bake keys back")), "and says what it would do");
    fx.Said.Clear();
    BakeUndoRun(fx, null);
    var undo = fx.Clock.Events.Skip(before).ToList();
    var wantUndo = new List<string> { "state Undoing", "layer", "push r3", "reconcile r3" };
    wantUndo.AddRange(Enumerable.Repeat("key", 8));
    wantUndo.AddRange(Enumerable.Repeat("remove", 8));
    C(undo.Select(e => e.StartsWith("key ") ? "key" : e.StartsWith("remove ") ? "remove" : e).SequenceEqual(wantUndo),
      "the state, the records back and the players told, then the keys, then the pieces the unbake made go: " + string.Join(" > ", undo));
    C(fx.Said.Any(l => l.StartsWith("bc_bake: undo of unbake 2 done in") && l.Contains("8 pieces removed") && l.Contains("10 records put back")), "what it says: " + string.Join(" | ", fx.Said));
    C(BakeKeysOfSource(fx.Port, 1).SequenceEqual(recordsOfBake), "the ten records are back exactly as the bake made them");
    C(fx.World.Objects.Count == objectsBaked && taken.All(p => !BakeStands(fx, p)), "the eight pieces are gone again as objects");
    C(BakeHasKeys(fx.World.Objects.Values.First(o => o.PrefabName == "wood_door")) && BakeHasKeys(fx.World.Objects.Values.First(o => o.PrefabName == "piece_chest")), "and the town pieces have their keys again");
    C(fx.Layer.Registry.Operations[1].State == OperationState.Undone && fx.Layer.Registry.Operations[0].State == OperationState.InLayer && fx.Layer.Placements == 10, "the registry marks the unbake undone; the bake is in the layer");
    C(journal.GetState(2) == BakeState.Undoing, "the end state is Undoing, which the next load settles");

    Section("undo: a bake is put back as it was");
    BakeRunner.Reset();
    fx.Said.Clear();
    int before2 = fx.Clock.Events.Count;
    BakeUndoRun(fx, 1);
    foreach (var piece in taken)
      C(BakeFindAgain(fx, piece) != null, $"the {piece.PrefabName} at {piece.X}, {piece.Z} is back with every value");
    C(fx.Layer.Placements == 0 && fx.Layer.Registry.Operations[0].State == OperationState.Undone && fx.Layer.Registry.Operations[0].ValueSets.Length == 0, "the records are out and the bake's value sets are dropped from the registry");
    C(fx.World.Objects.Values.All(o => !BakeHasAnyKey(o)), "the town pieces lose their keys: they are as they were before the bake");
    C(fx.World.Objects.Values.First(o => o.PrefabName == "wood_door").Values.SequenceEqual(door0.Values), "with their state and creator");
    var undoEvents = fx.Clock.Events.Skip(before2).ToList();
    C(undoEvents[0] == "state Undoing" && undoEvents.FindIndex(e => e.StartsWith("make ")) < undoEvents.IndexOf("layer") && undoEvents.FindIndex(e => e == "key off") < undoEvents.IndexOf("layer"),
      "the state, the pieces made, the keys taken off, and only then the records out: " + string.Join(" > ", undoEvents));
    C(fx.World.Objects.Count == 13, "and the world is the one the test began with: 13 objects");

    Section("undo: what it refuses");
    fx.Said.Clear();
    BakeUndoRun(fx, 1);
    C(fx.Said.Count == 1 && fx.Said[0].Contains("bake 1 was undone already"), "an operation that was undone: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUndoRun(fx, null);
    C(fx.Said.Count == 1 && fx.Said[0].Contains("there is nothing to undo"), "with nothing in the layer to undo: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUndoRun(fx, 99);
    C(fx.Said.Count == 1 && fx.Said[0].Contains("there is no undo file for operation 99"), "an operation that has no undo file: " + string.Join(" | ", fx.Said));
  }

  private static void BakeUnbakePartTest()
  {
    Section("unbake: part of a bake, and what an undo of the whole then says");
    BakeRunner.Reset();
    var fx = BakeNewFx("unbake-part", BakeStandardWorld, out var taken);
    BakeRun(fx, BakeStandardArea());
    // One wall only (radius 4 around it).
    BakeUnbakeRun(fx, new UnbakeScope { Area = BakeArea.OfCircle(5f, 8.5f, 4f) }, "confirm", "unbake 4 at 5 8.5");
    C(fx.Layer.Placements == 7 && BakeStands(fx, taken[0]) && !BakeStands(fx, taken[1]), "one record came out and one piece was made");
    fx.Said.Clear();
    BakeUndoRun(fx, 1);
    C(fx.Said.Count == 1 && fx.Said[0].Contains("part of bake 1 was unbaked since (1 of its 8 records are out of the layer): 'bc_bake unbake' takes the rest back."), "undo of the bake is refused: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUndoRun(fx, 2);
    C(fx.Layer.Placements == 8 && !BakeStands(fx, taken[0]), "undo of the unbake puts the record back and takes the piece away");
    fx.Said.Clear();
    BakeUndoRun(fx, 1);
    C(fx.Layer.Placements == 0 && taken.All(p => BakeFindAgain(fx, p) != null), "and then the bake can be undone");

    Section("unbake: a box, the world, and the compiler's records");
    BakeRunner.Reset();
    var fy = BakeNewFx("unbake-box", BakeStandardWorld, out var taken2);
    BakeRun(fy, BakeStandardArea());
    fy.Said.Clear();
    BakeUnbakeRun(fy, new UnbakeScope { Area = BakeArea.OfBox(0f, 0f, 20f, 20f) }, "confirm", "unbake box 0 0 20 20");
    C(fy.Layer.Placements == 2 && taken2.Count(p => BakeStands(fy, p)) == 6, "a box takes the six pieces in it: the two floors stay records");
    fy.Said.Clear();
    BakeUnbakeRun(fy, BakeWholeWorld, "", "unbake world");
    C(fy.Said.Any(l => l.StartsWith("bc_bake unbake world: a dry run")) && fy.Said.Any(l => l == "World: every zone that has records.") && fy.Said.Any(l => l.Contains("Found 2 records here: 2 baked in game.")), "the world: every zone that has records");
    fy.Said.Clear();
    BakeUnbakeRun(fy, BakeWholeWorld, "confirm", "unbake world");
    C(fy.Layer.Placements == 0 && taken2.All(p => BakeFindAgain(fy, p) != null), "the way out: the world's whole layer is pieces again");
    fy.Said.Clear();
    BakeUnbakeRun(fy, BakeWholeWorld, "confirm", "unbake world");
    C(fy.Said.Count == 1 && fy.Said[0].Contains("no records"), "and a second time there is nothing: " + string.Join(" | ", fy.Said));

    BakeRunner.Reset();
    var none = BakeNewFx("unbake-none", BakeStandardWorld, out _);
    BakeUnbakeRun(none, BakeWholeWorld, "confirm");
    C(none.Said.Count == 1 && none.Said[0].Contains("this world's layer holds no records") && none.Clock.Count == 0, "a world without a layer: " + string.Join(" | ", none.Said));
  }

  // The compiler's layer, with the Live pieces its doors stand for seeded in the world.
  private static BakeFx BakeCompilerWorld(string name, bool missingRoof = false)
  {
    var fx = BakeNewFx(name, world =>
    {
      foreach (var facts in BakeFakePrefabs())
        world.Facts[facts.Name.GetStableHashCode()] = facts;
      foreach (var extra in new[] { "stone_wall", "roof", "oak_beam", "iron_gate" })
        if (!(missingRoof && extra == "roof"))
          world.Facts[extra.GetStableHashCode()] = new PrefabFacts(extra, BakeSafeParts, height: 3f);
      return [];
    }, out _, CompilerFile([Wall(), Door(), Roof()], 0));
    // A seeded door for each Live record: its prefab, its place, its keys, and the tags the compiler gave it.
    foreach (var record in BakeRecordsOf(fx).Where(r => r.Role == BakedRole.Live))
    {
      var door = BakeObject("wood_door", (float)record.Record.WorldX, (float)record.Record.WorldY, (float)record.Record.WorldZ, (float)BakedFormat.YawDegrees(record.Record.Yaw), 0);
      var values = door.Values.ToList();
      values.Add(ZdoValue.OfInt(BakedKeys.Src, 0));
      values.Add(ZdoValue.OfInt(BakedKeys.Id, unchecked((int)record.Record.Id)));
      values.Add(ZdoValue.OfInt(BakedKeys.Rev, 1));
      values.Add(ZdoValue.OfInt(BakedKeys.Protect, 1));
      values.Add(ZdoValue.OfInt("VALtima_TownPiece".GetStableHashCode(), 1));
      values.Sort(BakeValues.Compare);
      door.Values = [.. values];
      fx.World.Add(door);
    }
    return fx;
  }

  private static void BakeUnbakeCompilerTest()
  {
    Section("unbake: the compiler's records only with 'all'");
    BakeRunner.Reset();
    var fx = BakeCompilerWorld("unbake-compiler");
    C(fx.Layer.Placements == 43 && fx.World.Objects.Count == 3, "a compiler's layer of 43 records, and its three doors seeded");
    BakeUnbakeRun(fx, BakeWholeWorld, "", "unbake world");
    C(fx.Said.Any(l => l.Contains("Found 43 records here: 0 baked in game, 43 from the compiler's file (add 'all' to take those too).")) && fx.Said.Any(l => l == "Nothing to unbake."), "without 'all' nothing: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "confirm");
    C(fx.Said.Count == 1 && fx.Said[0].Contains("nothing to unbake here") && fx.Said[0].Contains("add 'all'") && fx.Clock.Count == 0, "and 'confirm' says why: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "all", "unbake world all");
    C(fx.Said.Any(l => l.Contains("To unbake: 40 pieces (stone_wall 35, roof 5). 3 live pieces lose their bake keys")) && fx.Said.Any(l => l.StartsWith("With 'all' the compiler's records come out too")), "with 'all' the dry run warns that a load brings them back: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "all confirm", "unbake world all");
    C(fx.Layer.Placements == 0 && fx.World.Objects.Count == 3 + 40, "all 43 records are out of the layer; 40 pieces are made");
    var made = fx.World.Objects.Values.Where(o => o.PrefabName != "wood_door").ToList();
    C(made.All(o => o.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == fx.Creator && o.Find(BakeKey("health"), ZdoValueType.Float) == null), "a compiler's piece gets the admin as its creator and full health (no health value)");
    var doors = fx.World.Objects.Values.Where(o => o.PrefabName == "wood_door").ToList();
    C(doors.All(d => !BakeHasAnyKey(d) && d.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == fx.Creator && d.Find("VALtima_TownPiece".GetStableHashCode(), ZdoValueType.Int) != null), "the doors lose their keys, get the admin as creator, and keep the compiler's tags");
    var op = fx.Layer.Registry.Operations.Single();
    C(op.Kind == OperationKind.Unbake && op.Removed == 43 && op.Adopted == 3, "the registry: an unbake of 43 records, 3 live pieces freed");
    var wall = made.First(o => o.PrefabName == "stone_wall");
    C(Math.Abs(wall.RotY - 90.0) < 0.01 || Math.Abs(wall.RotY) < 0.01, $"a record's yaw is the piece's: {wall.RotY}");

    Section("unbake: a record whose prefab this game lacks stays in the layer");
    BakeRunner.Reset();
    var fz = BakeCompilerWorld("unbake-missing", missingRoof: true);
    BakeUnbakeRun(fz, BakeWholeWorld, "all", "unbake world all");
    C(fz.Said.Any(l => l.Contains("Not unbaked: 5 records stay in the layer (roof 5): this game cannot make them as pieces.")), "the dry run says which stay: " + string.Join(" | ", fz.Said));
    fz.Said.Clear();
    BakeUnbakeRun(fz, BakeWholeWorld, "all confirm", "unbake world all");
    C(fz.Layer.Placements == 5 && BakeRecordsOf(fz).All(r => r.Prefab == "roof"), "the five records that could not be made stay; the others are pieces");
  }

  private static void BakeUnbakePieceTest()
  {
    Section("unbake: the piece a record makes (its place, scale, look and values)");
    BakeRunner.Reset();
    var fx = BakeNewFx("piece-of", BakeStandardWorld, out _);
    fx.World.Facts["crate".GetStableHashCode()] = new PrefabFacts("crate", BakeSafeParts, syncsScale: true, height: 2f);
    fx.World.Facts["crate_alt".GetStableHashCode()] = new PrefabFacts("crate_alt", BakeSafeParts, height: 2f);
    var rotate = BakeMath.EulerToQuaternion(0, 90, 0);
    // An entry with two candidates (the first is not in the game) and an anchor 1 m up the piece's own y; a yaw of 90 and a scale of 2.
    var entry = new PaletteEntry([new Candidate("crate_missing", 0, 0, 0), new Candidate("crate", 0, 1000, 0)], BakedRole.Static, BakedCollision.Prefab);
    var record = ZoneRecord.CreateYaw(0, 10.0, 20.0, 30.0, 90.0, new Vector3(2f, 2f, 2f), null, 0, 0, -1);
    var made = BakeRunner.PieceOf(fx.Ctx, new BakeRecord(entry, record, new ValueSet([])), 77, out var problem);
    C(made != null && problem == null && made.PrefabName == "crate", "the first prefab the game has is made: " + problem);
    C(Math.Abs(made.X - 10.0) < 0.001 && Math.Abs(made.Y - 18.0) < 0.001 && Math.Abs(made.Z - 30.0) < 0.001, $"the pivot is the point less the anchor, turned and scaled: ({made.X}, {made.Y}, {made.Z}) for (10, 20, 30) and an anchor of 1 m up at scale 2");
    C(Math.Abs(made.RotY - 90f) < 0.01 && made.RotX < 0.01 && made.RotZ < 0.01 || made.RotX > 359.99, $"a yaw of 90: ({made.RotX}, {made.RotY}, {made.RotZ})");
    C(made.Find(BakeCapture.ScaleKey, ZdoValueType.Vec3) is { A: 2f, B: 2f, C: 2f } && made.Find(BakeCapture.CreatorKey, ZdoValueType.Long)?.Number == 77, "a scale the prefab syncs is a value, and the creator given is the creator");
    var plain = BakeRunner.PieceOf(fx.Ctx, new BakeRecord(new PaletteEntry([new Candidate("crate_alt", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab), record, new ValueSet([])), 0, out _);
    C(plain != null && plain.Find(BakeCapture.ScaleKey, ZdoValueType.Vec3) == null && plain.Find(BakeCapture.CreatorKey, ZdoValueType.Long) == null && Math.Abs(plain.Y - 20.0) < 0.001,
      "a prefab that does not sync scale is made at scale 1, and a creator of 0 adds none");
    var none = BakeRunner.PieceOf(fx.Ctx, new BakeRecord(new PaletteEntry([new Candidate("nowhere", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab), record, new ValueSet([])), 0, out var why);
    C(none == null && why.Contains("no prefab of nowhere"), "a prefab the game lacks: " + why);
    fx.World.Facts["no_view".GetStableHashCode()] = new PrefabFacts("no_view", ["Piece"]);
    C(BakeRunner.PieceOf(fx.Ctx, new BakeRecord(new PaletteEntry([new Candidate("no_view", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab), record, new ValueSet([])), 0, out why) == null && why.Contains("no ZNetView"), "a prefab with no ZNetView: " + why);

    // Looks: the entry's tags, and a variant derived from the place for a slot the entry does not name.
    var floor = new LookInfo { Variations = [new KeyValuePair<int, float[]>(0, [1f, 2f, 1f]), new KeyValuePair<int, float[]>(1, [1f, 1f])], RandomValues = true };
    fx.World.Looks["crate_alt".GetStableHashCode()] = floor;
    var tagged = new PaletteEntry([new Candidate("crate_alt", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab, tags: [Tag.OfInt("MatVar0", 2)]);
    var seeded = ZoneRecord.CreateYaw(0, 10.0, 20.0, 30.0, 0.0, null, null, 3, 0, 4321);
    var look = BakeRunner.PieceOf(fx.Ctx, new BakeRecord(tagged, seeded, new ValueSet([])), 0, out _);
    uint hash = seeded.LookHash;
    int derived = BakeRunner.DerivedVariant(BakedFormat.VariantUnit(hash, 1), [1f, 1f]);
    C(look.Find(BakedKeys.MatVar(0), ZdoValueType.Int)?.Number == 2 && look.Find(BakedKeys.MatVar(1), ZdoValueType.Int)?.Number == derived && look.Find(BakedKeys.Seed, ZdoValueType.Int)?.Number == 4321,
      "slot 0 is the entry's tag, slot 1 is derived from the place, and the seed is the record's");
    var noSeed = ZoneRecord.CreateYaw(0, 10.0, 20.0, 30.0, 0.0, null, null, 3, 0, -1);
    C(BakeRunner.PieceOf(fx.Ctx, new BakeRecord(tagged, noSeed, new ValueSet([])), 0, out _).Find(BakedKeys.Seed, ZdoValueType.Int)?.Number == noSeed.EffectiveSeed, "a record with no seed gets the one derived from its place");
    // The derived variant: the first whose running weight passes u times the total.
    C(BakeRunner.DerivedVariant(0.0, [1f, 2f, 1f]) == 0 && BakeRunner.DerivedVariant(0.24, [1f, 2f, 1f]) == 0 && BakeRunner.DerivedVariant(0.25, [1f, 2f, 1f]) == 1 && BakeRunner.DerivedVariant(0.74, [1f, 2f, 1f]) == 1
      && BakeRunner.DerivedVariant(0.75, [1f, 2f, 1f]) == 2 && BakeRunner.DerivedVariant(0.999, [1f, 2f, 1f]) == 2 && BakeRunner.DerivedVariant(0.5, []) == 0, "the weights decide the variant, as the server's seeding does");
  }

  private static void BakeDropTest()
  {
    Section("drop: records leave the layer without making pieces");
    BakeRunner.Reset();
    var fx = BakeNewFx("drop", BakeStandardWorld, out var taken);
    BakeRun(fx, BakeStandardArea(), "town confirm");
    int events = fx.Clock.Events.Count;
    int objects = fx.World.Objects.Count;
    fx.Said.Clear();
    BakeDropRun(fx, new DropScope { Bake = 1 }, "");
    var dry = string.Join("\n", fx.Said);
    C(fx.Clock.Events.Count == events && dry.Contains("bc_bake drop bake 1: a dry run") && dry.Contains("This layer holds 10 pieces baked in bake 1 (stone_wall_2x1 4, wood_floor 2, piece_chest 1 and 3 more kinds). Dropping takes them out of the world: they are not made into pieces.")
      && dry.Contains("2 of them are Live records: their pieces go too") && dry.Contains("'bc_bake unbake world' first"), "a dry run says what goes, and how to keep it: " + dry);
    fx.Said.Clear();
    BakeDropRun(fx, new DropScope { Bake = 7 });
    C(fx.Said.Single().Contains("does not list a bake 7"), "a bake the layer does not list: " + string.Join(" | ", fx.Said));
    fx.Said.Clear();
    BakeDropRun(fx, new DropScope { Compiler = true });
    C(fx.Said.Single().Contains("holds no compiler's records") && fx.Clock.Events.Count == events, "no compiler's records here: " + string.Join(" | ", fx.Said));

    fx.Said.Clear();
    BakeDropRun(fx, new DropScope { Bake = 1 });
    var run = fx.Clock.Events.Skip(events).ToList();
    C(run.SequenceEqual(["write the undo file of 2", "state Prepared", "state Applied", "layer", "push r2", "reconcile r2"]), "the undo file, the state, then the layer, the push and the reconciliation of the live pieces: " + string.Join(" > ", run));
    C(fx.Layer.Placements == 0 && fx.World.Objects.Count == objects && fx.Layer.Registry.Operations[1].Kind == OperationKind.Drop && fx.Layer.Registry.Operations[1].Removed == 10, "ten records out, no piece made (the reconciliation, not the drop, removes the live pieces)");
    C(fx.Said.Any(l => l.StartsWith("bc_bake: drop 2 done in") && l.Contains("10 records out of the layer (revision 2)")), "what it says: " + string.Join(" | ", fx.Said));
    var journal = new BakeJournal(fx.Folder);
    C(journal.GetState(2) == BakeState.Applied && journal.Read(2).Records.Count == 10, "Applied, with the ten records in the undo file");

    Section("drop: undone, the records come back");
    BakeRunner.Reset();
    fx.Said.Clear();
    BakeUndoRun(fx, null);
    C(fx.Layer.Placements == 10 && fx.Layer.Registry.Operations[1].State == OperationState.Undone && BakeKeysOfSource(fx.Port, 1).Count == 10, "the ten records are back");
    C(journal.GetState(2) == BakeState.Undoing, "Undoing, until the next load");

    Section("drop: the compiler's records");
    BakeRunner.Reset();
    var cf = BakeCompilerWorld("drop-compiler");
    BakeDropRun(cf, new DropScope { Compiler = true }, "");
    C(cf.Said.Any(l => l.Contains("This layer holds 43 records from the compiler's file")) && cf.Said.Any(l => l.Contains("3 of them are Live records")) && cf.Said.Any(l => l.StartsWith("The next 'bc_bake load' of the compiler's file brings its records back.")), "the dry run: " + string.Join(" | ", cf.Said));
    cf.Said.Clear();
    BakeDropRun(cf, new DropScope { Compiler = true });
    C(cf.Layer.Placements == 0 && cf.Layer.Registry.Operations.Single().Kind == OperationKind.Drop, "dropped");
    cf.Said.Clear();
    BakeUndoRun(cf, null);
    C(cf.Layer.Placements == 43 && BakeRecordsOf(cf).Count(r => r.Role == BakedRole.Live) == 3, "and undone: the compiler's 43 records are back");
  }
}

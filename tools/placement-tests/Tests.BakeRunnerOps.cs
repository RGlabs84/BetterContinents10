// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of what the runner does (BakeRunner.Bake): the dry run, a whole bake in the order 10.7 gives, town pieces, and the refusals.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static void BakeDryRunTest()
  {
    Section("runner: a dry run says what would happen and changes nothing");
    var fx = BakeNewFx("dry", BakeStandardWorld, out var taken);
    int objects = fx.World.Objects.Count;
    BakeRunner.Reset();
    BakeRun(fx, BakeStandardArea(), "");
    var text = string.Join("\n", fx.Said);
    C(fx.Clock.Count == 0 && fx.World.Objects.Count == objects && fx.Layer == null && !Directory.Exists(fx.Folder), "nothing changed: no object, no layer, no undo file");
    C(fx.Said[0] == "bc_bake area 30: a dry run, nothing changes. 'bc_bake area 30 confirm' bakes.", "the first line says it is a dry run, and how to run it: " + fx.Said[0]);
    C(text.Contains("Area: 30 m around 30, 0 (2 zones)."), "the area");
    C(text.Contains("Found 11 pieces. To bake: 8 (stone_wall_2x1 4, wood_floor 2, wood_beam 1 and 1 more kinds)."), "the pieces found and the kinds that would be baked: " + text);
    C(text.Contains("Stay pieces: 2 (doors 1, chests 1). Add 'town' to make them protected parts of the layer."), "the pieces that stay, and the word for town mode");
    C(text.Contains("Not touched: 1 (not built by a player 1, add 'any' to take them)."), "the pieces that are not touched");
    C(text.Contains("Objects in these zones: 12 now, 4 after.") && text.Contains("Layer: +") && text.Contains("revision 1") && text.Contains("Undo file: about"), "the objects before and after, the layer's growth and revision, and the undo file's size");
    C(text.Contains("2 players connected; each gets the change"), "the players who would be sent the change");

    BakeRunner.Reset();
    var town = BakeNewFx("dry-town", BakeStandardWorld, out _);
    BakeRun(town, BakeStandardArea(), "town");
    var townText = string.Join("\n", town.Said);
    C(townText.Contains("bc_bake area 30 town: a dry run") && townText.Contains("Town pieces: 2 (doors 1, chests 1). They stay real pieces and become protected parts of the layer."), "with 'town' the pieces that stay are said to become protected parts of the layer");

    BakeRunner.Reset();
    var empty = BakeNewFx("dry-empty", BakeStandardWorld, out _);
    BakeRun(empty, BakeArea.OfCircle(1000f, 1000f, 20f), "");
    C(empty.Said.Any(l => l.Contains("Nothing to bake.")) && empty.Clock.Count == 0, "an empty area: 'Nothing to bake.'");
    BakeRunner.Reset();
    empty.Said.Clear();
    BakeRun(empty, BakeArea.OfCircle(1000f, 1000f, 20f));
    C(empty.Said.Count == 1 && empty.Said[0].StartsWith("bc_bake: nothing to bake in 20 m around 1000, 1000") && empty.Clock.Count == 0, "and with 'confirm' it says so and starts nothing: " + string.Join(" | ", empty.Said));

    BakeRunner.Reset();
    var bad = BakeNewFx("dry-bad", BakeStandardWorld, out _);
    BakeRun(bad, BakeArea.OfCircle(0f, 0f, 3f));
    C(bad.Said.Count == 1 && bad.Said[0].Contains("a radius is 4 to 256 m") && bad.Clock.Count == 0, "a radius that is out of range is said, and nothing starts");
  }

  private static void BakeRunTest()
  {
    Section("runner: a whole bake, in the order of 10.7");
    BakeRunner.Reset();
    var fx = BakeNewFx("run", BakeStandardWorld, out var taken);
    var ended = new List<(int, BakeState)>();
    fx.Ctx = new BakeContext
    {
      World = fx.Ctx.World, Layer = fx.Ctx.Layer, Transport = fx.Ctx.Transport, Reconcile = fx.Ctx.Reconcile, Convert = fx.Ctx.Convert, Journal = fx.Ctx.Journal, Say = fx.Ctx.Say,
      Clock = fx.Ctx.Clock, Who = fx.Ctx.Who, Version = fx.Ctx.Version, UnixTime = fx.Ctx.UnixTime, Ended = (n, state) => ended.Add((n, state)),
    };
    int frames = BakeRun(fx, BakeStandardArea());
    var text = string.Join("\n", fx.Said);
    var expected = new List<string> { "write the undo file of 1", "state Prepared", "state Applied", "layer", "push r1", "state Removing" };
    // The statics go in the order the zones were read (z, then x), and within a zone the order the world holds them.
    var inOrder = taken.OrderBy(p => ZoneKey.OfPoint(p.X, p.Z)).ToList();
    expected.AddRange(inOrder.Select(p => "remove " + p.PrefabName));
    expected.Add("state Removed");
    C(fx.Clock.Events.SequenceEqual(expected), "the undo file is written first, then the state before each step, the layer, the push, and the removals; the operation ends in Removed: " + string.Join(" > ", fx.Clock.Events));
    C(frames >= 3, $"it took {frames} frames: it reads the zones a few at a time and waits for the players, and does not stop the game");
    C(fx.Said.Any(l => l == "bc_bake: bake 1 started, 8 pieces. Undo file written."), "it says the undo file is written before the pieces go");
    C(fx.Said.Any(l => l.StartsWith("bc_bake: layer revision 1 sent to 2 players")), "and what the players were told: " + text);
    C(fx.Said.Any(l => l == "bc_bake: removing 8 pieces, 1,000 a frame."), "and that it is removing");
    C(fx.Said.Any(l => l.StartsWith("bc_bake: bake 1 done in ") && l.Contains("8 pieces baked, 2 left as pieces") && l.Contains("Objects here: 12 -> 4.")), "and the end: " + text);
    C(fx.Said.Last() == "'bc_bake undo' puts them back. The world saves as usual ('save' saves now).", "and how to undo");
    C(ended.SequenceEqual([(1, BakeState.Settled)]), "the end is announced with the state a later complete save settles it to");

    Section("runner: what a bake leaves in the world");
    foreach (var piece in taken)
      C(!BakeStands(fx, piece), $"the {piece.PrefabName} at {piece.X}, {piece.Z} is gone as an object");
    C(fx.World.Objects.Count == 12 - 8 + 1, "the objects that stay: a door, a chest, a tree, a piece nobody built, and the far wall");
    C(BakeCountObjects(fx, "wood_door") == 1 && BakeCountObjects(fx, "piece_chest") == 1 && BakeCountObjects(fx, "Pine_tree") == 1 && BakeCountObjects(fx, "stone_wall_2x1") == 2, "and they are the right ones");
    C(fx.World.Objects.Values.All(o => !BakeHasAnyKey(o)), "without town mode no piece is given a key");

    Section("runner: what a bake puts in the layer");
    var layer = fx.Layer;
    C(layer != null && layer.Revision == 1 && layer.Placements == 8 && layer.Registry.Operations.Count == 1 && layer.Registry.NextOperation == 2, "revision 1, eight records, one operation, the next number 2");
    var op = layer.Registry.Operations[0];
    C(op.Number == 1 && op.Kind == OperationKind.BakeArea && op.State == OperationState.InLayer && op.Added == 8 && op.Removed == 0 && op.Adopted == 0 && op.Who == "Tester" && op.Radius == 30f && op.Version == "0.10.4",
      "the registry entry: bake 1, an area of 30 m, 8 added, by Tester");
    var records = BakeRecordsOf(fx);
    C(records.Count == 8 && taken.All(p => records.Any(r => BakeIsRecord(r, p))), "every piece taken has a record at its place, to a millimetre");
    C(records.All(r => r.Record.HasSource && r.Record.Source == 1 && !r.Record.HasId), "each is a record of bake 1, none has an id");
    C(records.Count(r => r.Role == BakedRole.Seat) == 1 && records.Count(r => r.Role == BakedRole.Static) == 7, "the chair is a Seat, the others Statics");
    var floor = records.First(r => r.Prefab == "wood_floor" && r.Record.WorldX > 39.9 && r.Record.WorldX < 40.1);
    C(floor.Entry.Tags.Length == 1 && floor.Entry.Tags[0].Key == "MatVar0" && floor.Entry.Tags[0].Number == 2, "a floor's material variant is its palette entry's tag");
    C(floor.Values.Values.Find(BakeKey("health"), ZdoValueType.Float)?.A == 80f && floor.Values.Values.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == 1001, "and its health and creator are in its value set");
    var beam = records.First(r => r.Prefab == "wood_beam");
    C(beam.Record.HasFullRotation && beam.Record.HasScale && beam.Record.ScaleZ == 2500, "a tilted, scaled beam keeps a full rotation and its scale");
    var wall = records.First(r => r.Prefab == "stone_wall_2x1" && r.Record.WorldX > 10.9 && r.Record.WorldX < 11.1);
    C(Math.Abs(BakedFormat.YawDegrees(wall.Record.Yaw) - 37.3217) < 0.004 && Math.Abs(wall.Record.WorldY - 10.25) < 0.001, "a wall keeps its yaw and its height");
    C(layer.Registry.Knows(1, 0) && op.ValueSets.Length > 0, "the registry keeps the bake's value sets");
    C(layer.Palette.Count == records.Select(r => r.Entry).Distinct().Count(), "the palette holds one entry for each kind of piece and look, and none more");
    C(fx.Transport.Pushed.Count == 1 && fx.Transport.Pushed[0].Layer == layer && fx.Transport.Pushed[0].Changed.Length == 2, "the players were sent this layer with its two changed zones");

    Section("runner: what a bake leaves in the undo folder");
    var journal = new BakeJournal(fx.Folder);
    var data = journal.Read(1);
    C(journal.GetState(1) == BakeState.Removed && journal.Pending().SequenceEqual([1]), "the state is Removed, and the operation waits for the next world load to settle it");
    C(data.Number == 1 && data.Kind == OperationKind.BakeArea && data.Statics.Count == 8 && data.Records.Count == 8 && data.Adopted.Count == 0 && data.WorldName == "Fake World" && data.WorldUid == 777
      && data.RevisionBefore == 0 && data.RevisionAfter == 1 && data.PlatformId == "Steam_1" && data.Time == 1791400000, "the undo file holds the operation, the world, the revisions and who");
    C(taken.All(p => data.Statics.Any(s => s.Prefab == p.Prefab && s.X == p.X && s.Y == p.Y && s.Z == p.Z && s.RotY == p.RotY && s.RotX == p.RotX && s.RotZ == p.RotZ && s.Values.SequenceEqual(p.Values))),
      "and every piece that was taken, whole, with every value");
    C(Directory.GetFiles(fx.Folder).Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(["op-1.bcj", "op-1.state"]), "no .tmp file is left");
    C(BakeRunner.Running == null && BakeRunner.Stuck == null, "the runner is free again");
  }

  private static void BakeTownTest()
  {
    Section("runner: town mode adopts the pieces that stay");
    BakeRunner.Reset();
    var fx = BakeNewFx("town", BakeStandardWorld, out var taken);
    BakeRun(fx, BakeStandardArea(), "town confirm");
    var text = string.Join("\n", fx.Said);
    var expected = new List<string> { "write the undo file of 1", "state Prepared", "state Applied", "layer", "push r1" };
    for (int i = 0; i < 2; i++)
      expected.AddRange(["key src", "key id", "key rev", "key protect"]);
    expected.Add("state Removing");
    expected.AddRange(taken.OrderBy(p => ZoneKey.OfPoint(p.X, p.Z)).Select(p => "remove " + p.PrefabName));
    expected.Add("state Removed");
    C(fx.Clock.Events.SequenceEqual(expected), "the layer and the push come first, then each town piece gets its four keys, then the statics go: " + string.Join(" > ", fx.Clock.Events));
    var door = fx.World.Objects.Values.First(o => o.PrefabName == "wood_door");
    var chest = fx.World.Objects.Values.First(o => o.PrefabName == "piece_chest");
    C(BakeHasKeys(door) && BakeHasKeys(chest), "the door and the chest carry the four keys");
    C(door.Find(BakedKeys.Src, ZdoValueType.Int)?.Number == 1 && door.Find(BakedKeys.Id, ZdoValueType.Int)?.Number == 1 && chest.Find(BakedKeys.Id, ZdoValueType.Int)?.Number == 2
      && door.Find(BakedKeys.Rev, ZdoValueType.Int)?.Number == 1 && door.Find(BakedKeys.Protect, ZdoValueType.Int)?.Number == 1, "source 1, ids 1 and 2 in the order met, revision 1, protected");
    C(door.Find(BakeKey("state"), ZdoValueType.Int)?.Number == 1 && chest.Find(BakeKey("items"), ZdoValueType.String)?.Text == "sword x1" && door.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == 1001,
      "the pieces are the same objects: their state, their contents and their creator stay");
    var live = BakeRecordsOf(fx).Where(r => r.Role == BakedRole.Live).ToList();
    C(live.Count == 2 && live.All(r => r.Entry.Protected && r.Record.HasId && r.Record.Source == 1) && live.Select(r => r.Record.Id).OrderBy(i => i).SequenceEqual([1u, 2u]), "two Live records of bake 1, protected, with ids 1 and 2");
    C(fx.Layer.Registry.Operations[0].Adopted == 2 && fx.Layer.Registry.Operations[0].Added == 10 && fx.Layer.Placements == 10, "the registry counts 2 adopted and 10 added");
    C(text.Contains("2 left as pieces (town pieces, protected parts of the layer)"), "the end says the pieces that stayed are town pieces: " + text);
    var data = new BakeJournal(fx.Folder).Read(1);
    C(data.Adopted.Count == 2 && data.Adopted.All(a => a.Source == 1) && data.Adopted.Select(a => a.Id).OrderBy(i => i).SequenceEqual([1u, 2u])
      && data.Adopted.Any(a => a.Place.PrefabName == "wood_door" && a.Place.X == 3f), "the undo file says where each adopted piece stands, and its id");
    C(fx.Reconcile.Runs == 0, "no reconciliation runs between the layer and the keys (it would seed a second piece)");
  }

  private static void BakeRefusalTest()
  {
    Section("runner: what stops a bake before it changes anything");
    BakeRunner.Reset();
    var unavailable = BakeNewFx("refuse-transport", BakeStandardWorld, out _);
    unavailable.Transport.Unavailable = "sending a changed layer to the players is not available in this build";
    BakeRun(unavailable, BakeStandardArea(), "");
    C(unavailable.Said.Any(l => l.StartsWith("This build cannot bake yet: sending a changed layer")), "a dry run still works, and says the build cannot bake yet");
    unavailable.Said.Clear();
    BakeRun(unavailable, BakeStandardArea());
    C(unavailable.Clock.Count == 0 && unavailable.Said.Count == 1 && unavailable.Said[0].Contains("not available in this build") && unavailable.Said[0].EndsWith("Nothing is changed."), "'confirm' without a transport changes nothing: " + string.Join(" | ", unavailable.Said));
    C(BakeRunner.Running == null, "and leaves the runner free");

    BakeRunner.Reset();
    var busy = BakeNewFx("refuse-busy", BakeStandardWorld, out _);
    var first = BakeRunner.Guard(BakeRunner.Bake(busy.Ctx, BakeStandardArea(), BakeWordsOf("confirm"), "area 30"), busy.Ctx.Say, "a bake");
    for (int i = 0; i < 2 && first.MoveNext(); i++)
    {
    }
    C(BakeRunner.Running == "a bake" && busy.Clock.Count > 0, "a bake that is under way holds the runner");
    var second = BakeNewFx("refuse-busy2", BakeStandardWorld, out _);
    BakeRun(second, BakeStandardArea());
    C(second.Said.Count == 1 && second.Said[0].Contains("is still running") && second.Clock.Count == 0, "another is refused while it runs: " + string.Join(" | ", second.Said));
    BakeRun(second, BakeStandardArea(), "");
    C(second.Said.Count > 1 && second.Said.Any(l => l.Contains("a dry run")), "a dry run may always go");
    while (first.MoveNext())
    {
    }
    C(BakeRunner.Running == null, "the first ends and frees it");

    Section("runner: a world that is not a Better Continents world");
    BakeRunner.Reset();
    var vanilla = BakeNewFx("refuse-vanilla", BakeStandardWorld, out _);
    vanilla.Convert.Kind = BakeWorldKind.Vanilla;
    BakeRun(vanilla, BakeStandardArea(), "");
    C(vanilla.Said.Any(l => l.StartsWith("This world is not a Better Continents world. Baking makes it one:") && l.EndsWith("Add 'convert' to go ahead.")), "the dry run says what baking makes of the world, and the word to go ahead");
    vanilla.Said.Clear();
    BakeRun(vanilla, BakeStandardArea());
    C(vanilla.Clock.Count == 0 && vanilla.Convert.Converted == 0 && vanilla.Said.Any(l => l.Contains("Add 'convert' to go ahead.")), "'confirm' without 'convert' changes nothing");
    vanilla.Transport.PeerList.Add(("Bob", null));
    vanilla.Transport.PeerList.Add(("Eve", "0.0.1"));
    vanilla.Transport.PeerList.Add(("Ann", ModInfo.Version));
    vanilla.Said.Clear();
    BakeRun(vanilla, BakeStandardArea(), "convert confirm");
    C(vanilla.Clock.Count == 0 && vanilla.Said.Any(l => l.Contains("Bob (no Better Continents)") && l.Contains("Eve (Better Continents 0.0.1)") && !l.Contains("Ann")), "players on another version are named and the bake does not start: " + string.Join(" | ", vanilla.Said));
    vanilla.Transport.PeerList.Clear();
    vanilla.Convert.Unavailable = "making a world a Better Continents world that keeps the game's terrain is not available in this build";
    vanilla.Said.Clear();
    BakeRun(vanilla, BakeStandardArea(), "convert confirm");
    C(vanilla.Clock.Count == 0 && vanilla.Convert.Converted == 0 && vanilla.Said.Any(l => l.Contains("not available in this build") && l.EndsWith("Nothing is changed.")), "a build that cannot convert says so and changes nothing");
    vanilla.Convert.Unavailable = null;
    vanilla.Said.Clear();
    BakeRun(vanilla, BakeStandardArea(), "convert confirm");
    C(vanilla.Convert.Converted == 1 && vanilla.Layer != null && vanilla.Layer.Placements == 8, "with 'convert' and everyone on this version, the world is converted and the bake goes on");
    C(vanilla.Clock.Events[0].StartsWith("write the undo file") && vanilla.Said.Any(l => l.StartsWith("bc_bake: this world is now a Better Continents world")), "the undo file is written before the world is converted");

    BakeRunner.Reset();
    var broken = BakeNewFx("refuse-unreadable", BakeStandardWorld, out _);
    broken.Convert.Kind = BakeWorldKind.Unreadable;
    BakeRun(broken, BakeStandardArea(), "convert confirm");
    C(broken.Clock.Count == 0 && broken.Convert.Converted == 0 && broken.Said.Any(l => l.Contains("settings file that cannot be read") && l.Contains("nothing is changed")), "a world whose settings cannot be read is never converted: " + string.Join(" | ", broken.Said));

    Section("runner: an undo file that cannot be written stops the bake");
    BakeRunner.Reset();
    var blocked = BakeNewFx("refuse-journal", BakeStandardWorld, out _);
    File.WriteAllText(blocked.Folder, "a file where the folder should be");
    BakeRun(blocked, BakeStandardArea());
    C(blocked.Said.Any(l => l.Contains("the undo file could not be written")) && blocked.World.Objects.Count == 13 && blocked.Layer == null && blocked.Clock.Count == 0, "no object and no layer changes: " + string.Join(" | ", blocked.Said));
    C(BakeRunner.Running == null && BakeRunner.Stuck == null, "and the runner is free: nothing was begun");
  }

  private static void BakeChangedMeanwhileTest()
  {
    Section("runner: a piece that changes while the bake runs is not removed");
    BakeRunner.Reset();
    var fx = BakeNewFx("meanwhile", BakeStandardWorld, out var taken);
    // A player moves a wall and demolishes a floor between the dry run and the removal (during the wait for the players).
    var routine = BakeRunner.Guard(BakeRunner.Bake(fx.Ctx, BakeStandardArea(), BakeWordsOf("confirm"), "area 30"), fx.Ctx.Say, "a bake");
    bool changed = false;
    while (routine.MoveNext())
      if (!changed && fx.Clock.Events.Contains("push r1"))
      {
        changed = true;
        var wall = fx.World.Objects.First(o => o.Value.PrefabName == "stone_wall_2x1" && o.Value.X == 7f).Key;
        fx.World.Objects[wall].X += 3f;
        var floor = fx.World.Objects.First(o => o.Value.PrefabName == "wood_floor" && o.Value.X == 42f).Key;
        fx.World.Objects.Remove(floor);
      }
    var text = string.Join("\n", fx.Said);
    C(changed && text.Contains("2 pieces changed while the bake ran: their records stay"), "the two are counted and said: " + text);
    C(text.Contains("6 pieces baked"), "the other six are baked");
    C(fx.Layer.Placements == 8 && BakeCountObjects(fx, "wood_floor") == 0 && fx.World.Objects.Values.Any(o => o.PrefabName == "stone_wall_2x1" && o.X == 10f), "all eight records are in the layer, and the moved wall still stands where it was moved to");
    C(new BakeJournal(fx.Folder).GetState(1) == BakeState.Removed, "the bake ends as any other");
  }

  private static void BakeStuckTest()
  {
    Section("runner: an operation that stops half done holds the world until it is loaded again");
    BakeRunner.Reset();
    var fx = BakeNewFx("stuck", BakeStandardWorld, out var taken);
    // The game stops in the middle of the removals.
    fx.Clock.Limit = 9;
    BakeRun(fx, BakeStandardArea());
    C(fx.Clock.Count == 9 && fx.Said.Any(l => l.StartsWith("bc_bake: a bake stopped on an error:") && l.Contains("The next world load settles")), "the error is said, with what to do: " + fx.Said.Last());
    C(BakeRunner.Running == null && BakeRunner.Stuck == "a bake", "the runner is free, and remembers what stopped");
    var other = BakeNewFx("stuck2", BakeStandardWorld, out _);
    BakeRun(other, BakeStandardArea());
    C(other.Clock.Count == 0 && other.Said.Count == 1 && other.Said[0].Contains("a bake stopped half done") && other.Said[0].Contains("Load the world again"), "nothing else changes the world: " + string.Join(" | ", other.Said));
    BakeRun(other, BakeStandardArea(), "");
    C(other.Said.Count > 1 && other.Said.Any(l => l.Contains("a dry run")), "a dry run still goes");
    BakeRunner.Reset();
    BakeRun(other, BakeStandardArea());
    C(other.Clock.Count > 0, "after a load (Reset) it goes again");

    BakeRunner.Reset();
    var closing = BakeNewFx("closing", BakeStandardWorld, out _);
    closing.World.CloseAfterRemovals = 3;
    BakeRun(closing, BakeStandardArea());
    C(closing.Said.Any(l => l.Contains("a bake stopped: the world closed after 3 removals. The next world load settles the rest.")) && BakeRunner.Stuck == "a bake", "a world that closes in the middle stops the removals: " + closing.Said.Last());

    BakeRunner.Reset();
    var early = BakeNewFx("early", BakeStandardWorld, out _);
    early.World.Facts.Clear();
    BakeRun(early, BakeStandardArea());
    C(BakeRunner.Stuck == null, "a stop before the undo file is written leaves nothing half done to hold the world for");
    BakeRunner.Reset();
  }
}

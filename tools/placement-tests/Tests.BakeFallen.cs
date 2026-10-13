// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// Offline checks of what an undo and the check at world load do with a piece that an unbake made and that then moved (phase F1: 29,968 pieces
// made, 29,967 found by the undo, one left real beside its own record). BakeMatch finds a piece of an unbake's that only dropped or was lifted
// (StaticPhysics.CheckFall and PushUp) and never one that players built; every operation that cannot deal with a piece says so in its done line and
// the log; and 'bc_bake check' lists the real pieces that stand on, under or close to a record.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static ObjectPose FallPose(int id, string prefab, float x, float y, float z, float yaw = 0f) => new()
  {
    Id = new ZDOID(9, (uint)id), Prefab = prefab.GetStableHashCode(), X = x, Y = y, Z = z, RotY = yaw,
  };

  private static PieceCopy FallPiece(string prefab, float x, float y, float z, float yaw = 0f) => BakeObject(prefab, x, y, z, yaw);

  // The objects an unbake made, found by prefab and x.
  private static ZDOID FallMade(BakeFx fx, string prefab, float x) =>
    fx.World.Objects.First(o => o.Value.PrefabName == prefab && Math.Abs(o.Value.X - x) < 0.001f).Key;

  // A bake of the standard world, then an unbake of all of it: the eight pieces stand again as objects.
  private static BakeFx FallUnbaked(string name, out List<PieceCopy> taken)
  {
    BakeRunner.Reset();
    var fx = BakeNewFx(name, BakeStandardWorld, out taken);
    BakeRun(fx, BakeStandardArea());
    BakeRunner.Reset();
    BakeUnbakeRun(fx, BakeWholeWorld);
    BakeRunner.Reset();
    return fx;
  }

  private static void BakeFallenMatchTest()
  {
    Section("fallen: BakeMatch finds a piece that only dropped, and only when asked");
    var piece = FallPiece("stone_wall_2x1", 5f, 10f, 5f, 90f);
    var exact = FallPose(1, "stone_wall_2x1", 5f, 10f, 5f, 90f);
    var dropped = FallPose(2, "stone_wall_2x1", 5f, 7f, 5f, 90f);
    C(BakeMatch.Find([dropped], [piece])[0] == -1, "a piece that dropped 3 m is not found by the exact search");
    C(BakeMatch.Find([dropped], [piece], true)[0] == 0, "and is found by the search for pieces of BC's own");
    C(BakeMatch.Find([exact, dropped], [piece], true)[0] == 0, "the exact one wins when both stand");
    C(BakeMatch.Find([dropped, FallPose(3, "stone_wall_2x1", 5f, 9f, 5f, 90f)], [piece], true)[0] == 1, "of two that dropped the nearer one is taken (the one 1 m down, not 3)");
    C(BakeMatch.Find([FallPose(4, "stone_wall_2x1", 5f, 14f, 5f, 90f)], [piece], true)[0] == -1, "a piece higher up is not it (somebody else's, standing on top)");
    C(BakeMatch.Find([FallPose(5, "stone_wall_2x1", 5.1f, 7f, 5f, 90f)], [piece], true)[0] == -1, "dropped and moved 10 cm sideways is not it");
    C(BakeMatch.Find([FallPose(6, "stone_wall_2x1", 5.005f, 7f, 5f, 90f)], [piece], true)[0] == 0, "dropped and 5 mm off is it (within the centimetre)");
    C(BakeMatch.Find([FallPose(7, "stone_wall_2x1", 5f, 7f, 5f, 95f)], [piece], true)[0] == -1, "dropped and turned 5 degrees is not it");
    C(BakeMatch.Find([FallPose(8, "stone_wall_1x1", 5f, 7f, 5f, 90f)], [piece], true)[0] == -1, "another prefab is not it");
    // each object answers for one piece; the exact search for all comes first
    var upper = FallPiece("stone_wall_2x1", 5f, 12f, 5f, 90f);
    var lower = FallPiece("stone_wall_2x1", 5f, 8f, 5f, 90f);
    var oLower = FallPose(10, "stone_wall_2x1", 5f, 8f, 5f, 90f);
    var oUpperFell = FallPose(11, "stone_wall_2x1", 5f, 9f, 5f, 90f);
    var found = BakeMatch.Find([oUpperFell, oLower], [upper, lower], true);
    C(found[0] == 0 && found[1] == 1, "a column of two: the lower piece keeps its exact object, the upper one takes the one that dropped to 9 m");
    found = BakeMatch.Find([oLower], [upper, lower], true);
    C(found[0] == -1 && found[1] == 0, "and an object the exact search gave to one piece is not taken by another's lenient search");
    var twin = BakeMatch.Find([dropped], [piece, FallPiece("stone_wall_2x1", 5f, 10f, 5f, 90f)], true);
    C(twin.Count(i => i >= 0) == 1, "one object answers for one piece only");
    C(BakeMath.SameSpot(piece, "stone_wall_2x1".GetStableHashCode(), 5f, 5f, 0f, 90f, 0f) && !BakeMath.SameSpot(piece, "stone_wall_2x1".GetStableHashCode(), 5.02f, 5f, 0f, 90f, 0f),
      "SameSpot is the place without the height");

    Section("fallen: a piece in the ground that was lifted to it (StaticPhysics.PushUp)");
    var buried = FallPiece("stone_wall_2x1", 5f, 9.5f, 5f, 90f);
    var lifted = FallPose(20, "stone_wall_2x1", 5f, 10f, 5f, 90f);
    C(BakeMatch.Find([lifted], [buried], true)[0] == -1, "without the ground's height a higher piece is not looked for");
    C(BakeMatch.Find([lifted], [buried], true, (x, z) => 10.0)[0] == 0, "with the ground at 10 m the piece that stands there is the one that was lifted");
    C(BakeMatch.Find([lifted], [buried], true, (x, z) => 12.0)[0] == -1, "not when the ground is somewhere else");
    C(BakeMatch.Find([lifted], [buried], true, (x, z) => null)[0] == -1, "not when the ground is not known");
    C(BakeMatch.Find([lifted], [FallPiece("stone_wall_2x1", 5f, 9.98f, 5f, 90f)], true, (x, z) => 10.0)[0] == -1, "a piece that was above the ground (or a hair under) was not lifted");
    C(BakeMatch.Find([lifted], [buried])[0] == -1, "and the exact search never lifts");
  }

  private static void BakeFallenUndoTest()
  {
    Section("fallen: an undo of an unbake removes a piece that dropped 3 m");
    var fx = FallUnbaked("fallen-undo", out var taken);
    int objects = fx.World.Objects.Count;
    var wall = FallMade(fx, "stone_wall_2x1", 5f);
    fx.World.Objects[wall].Y -= 3f;
    fx.Said.Clear();
    BakeUndoRun(fx, null, "");
    var dry = string.Join("\n", fx.Said);
    C(dry.Contains("0 pieces to make, 8 to remove") && !dry.Contains("no longer where it put"), "the dry run counts the piece that dropped among the eight to remove: " + dry);
    fx.Said.Clear();
    BakeRunner.Reset();
    BakeUndoRun(fx, null);
    var text = string.Join("\n", fx.Said);
    C(text.Contains("8 pieces removed (1 of them had dropped or been lifted from where they were put)"), "the done line counts the eight and says one had dropped: " + text);
    C(!text.Contains("no longer where") && fx.Logged.Count == 0, "and nothing is left over to say");
    C(fx.World.Objects.Count == objects - 8 && taken.All(p => !BakeStands(fx, p)), "none of the eight is left as an object, the fallen one included");
    C(fx.Layer.Placements == 8, "the eight records are back in the layer");
    C(fx.World.LenientSearches >= 1, "the search for BC's own pieces was the one used");

    Section("fallen: lifted from the ground, with the ground known");
    BakeRunner.Reset();
    var fl = FallUnbaked("fallen-lift", out _);
    fl.World.Ground = (x, z) => 10.5;
    // the unbake's floor at 10 m is in the ground (ground 10.5): the game lifts it
    var floor = FallMade(fl, "wood_floor", 40f);
    fl.World.Objects[floor].Y = 10.5f;
    int before = fl.World.Objects.Count;
    BakeUndoRun(fl, null);
    C(fl.World.Objects.Count == before - 8 && BakeCountObjects(fl, "wood_floor") == 0, "the lifted floor is removed with the rest");
  }

  private static void BakeFallenReportTest()
  {
    Section("fallen: a piece moved sideways 10 cm is not removed, and the undo says so");
    var fx = FallUnbaked("fallen-side", out _);
    var wall = FallMade(fx, "stone_wall_2x1", 5f);
    fx.World.Objects[wall].X += 0.1f;
    int objects = fx.World.Objects.Count;
    fx.Said.Clear();
    BakeUndoRun(fx, null, "");
    var dry = string.Join("\n", fx.Said);
    C(dry.Contains("0 pieces to make, 7 to remove"), "the dry run counts seven: " + dry);
    C(dry.Contains("1 piece it made is no longer where it put it (moved or gone): it stays a real piece, and its record is drawn too. 'bc_bake check' lists the real pieces that stand on, under or close to a record (e.g. stone_wall_2x1 at 5, 10, 5)."),
      "and names the one it cannot find: " + dry);
    fx.Said.Clear();
    BakeRunner.Reset();
    BakeUndoRun(fx, null);
    var text = string.Join("\n", fx.Said);
    C(text.Contains("7 pieces removed,") && !text.Contains("dropped or been lifted"), "the done line has seven removed: " + text);
    C(text.Contains("bc_bake: 1 piece it made was no longer where it put it (moved or gone): it stays a real piece, and its record is drawn too."), "and the sentence for the one that is left: " + text);
    C(fx.World.Objects.Count == objects - 7 && fx.World.Objects.ContainsKey(wall), "the wall that moved is the only one of the eight still standing");
    C(fx.Logged.Count == 1 && fx.Logged[0].StartsWith("bc_bake: made stone_wall_2x1 at 5, 10, 5 turned 0: the nearest of that kind is +0.1 / 0 / 0 m away (x / y / z), turned 0 degrees."),
      "the log names it and where the nearest of its kind stands now: " + string.Join(" | ", fx.Logged));
    var check = BakeCheck.Run(fx.Ctx, fx.Layer, fx.Port.ZonesWithRecords(), false, out _);
    C(check.Count(f => f.Kind.StartsWith("Records with a real piece of the same kind close by")) == 1, "'bc_bake check' lists it: " + string.Join("; ", check.Select(f => f.Kind.Substring(0, Math.Min(50, f.Kind.Length)))));

    Section("fallen: a piece that is gone, and one turned 5 degrees");
    BakeRunner.Reset();
    var gone = FallUnbaked("fallen-gone", out _);
    gone.World.Objects.Remove(FallMade(gone, "wood_floor", 40f));
    gone.World.Objects.Remove(FallMade(gone, "wood_floor", 42f));
    gone.World.Objects[FallMade(gone, "stone_wall_2x1", 9f)].RotY += 5f;
    gone.Said.Clear();
    BakeUndoRun(gone, null);
    var goneText = string.Join("\n", gone.Said);
    C(goneText.Contains("5 pieces removed,") && goneText.Contains("3 pieces it made were no longer where it put them (moved or gone): they stay real pieces, and their records are drawn too."), "three are left, and said: " + goneText);
    C(gone.Logged.Count == 3 && gone.Logged.Count(l => l.Contains("wood_floor at ") && l.Contains("no wood_floor within 3 m")) == 2 && gone.Logged.Any(l => l.Contains("stone_wall_2x1 at 9, 10, 5") && l.Contains("turned 5 degrees")),
      "the log tells the gone ones from the turned one: " + string.Join(" | ", gone.Logged));

    Section("fallen: the town pieces an unbake freed, and where they stand");
    BakeRunner.Reset();
    var town = BakeNewFx("fallen-town", BakeStandardWorld, out _);
    BakeRun(town, BakeStandardArea(), "town confirm");
    BakeRunner.Reset();
    BakeUnbakeRun(town, BakeWholeWorld);
    BakeRunner.Reset();
    var door = town.World.Objects.First(o => o.Value.PrefabName == "wood_door").Key;
    town.World.Objects[door].Y -= 2f;
    town.Said.Clear();
    BakeUndoRun(town, null);
    var townText = string.Join("\n", town.Said);
    C(!townText.Contains("no longer where"), "a freed town piece that dropped is found where it fell and keyed again: " + townText);
    C(town.World.Objects[door].Find(BakedKeys.Id, ZdoValueType.Int) != null, "it carries its bake keys again");
    BakeRunner.Reset();
    var town2 = BakeNewFx("fallen-town2", BakeStandardWorld, out _);
    BakeRun(town2, BakeStandardArea(), "town confirm");
    BakeRunner.Reset();
    BakeUnbakeRun(town2, BakeWholeWorld);
    BakeRunner.Reset();
    var chest = town2.World.Objects.First(o => o.Value.PrefabName == "piece_chest").Key;
    town2.World.Objects[chest].X += 0.5f;
    town2.Said.Clear();
    BakeUndoRun(town2, null);
    var town2Text = string.Join("\n", town2.Said);
    C(town2Text.Contains("bc_bake: 1 town piece is no longer where the unbake left it (moved or gone): it gets no bake keys back, so the layer seeds a second piece for its Live record."),
      "one that moved sideways is said to get no keys back: " + town2Text);
  }

  private static void BakeFallenMeanwhileTest()
  {
    Section("fallen: a piece that moves while the undo runs is left, and said");
    var fx = FallUnbaked("fallen-meanwhile", out _);
    var routine = BakeRunner.Guard(BakeRunner.Undo(fx.Ctx, null, BakeWordsOf("confirm")), fx.Ctx.Say, "an undo");
    bool moved = false;
    ZDOID wall = default;
    while (routine.MoveNext())
      if (!moved && fx.Clock.Events.Contains("push r3"))
      {
        // between the plan and the removal: somebody shifts a wall 20 cm
        moved = true;
        wall = FallMade(fx, "stone_wall_2x1", 11f);
        fx.World.Objects[wall].Z += 0.2f;
      }
    var text = string.Join("\n", fx.Said);
    C(moved && text.Contains("7 pieces removed,") && text.Contains("1 piece it made was no longer where it put it (moved or gone)"), "seven are removed and the one that moved is said: " + text);
    C(fx.World.Objects.ContainsKey(wall) && fx.Logged.Count == 1 && fx.Logged[0].Contains("stone_wall_2x1 at 11, 10.25, 5") && fx.Logged[0].Contains("0 / 0 / +0.2 m away"), "it stays, and the log has it: " + string.Join(" | ", fx.Logged));

    Section("fallen: the world-load check of an undo that was cut says what it could not find");
    var cut = FallUnbaked("fallen-cut", out _);
    cut.Journal.SetState(1, BakeState.Settled);
    cut.Journal.SetState(2, BakeState.Undoing);
    cut.World.Objects[FallMade(cut, "stone_wall_2x1", 5f)].X += 0.1f;
    cut.Said.Clear();
    var result = BakeRunner.CheckPending(cut.Ctx);
    var cutText = string.Join("\n", cut.Said);
    C(result.Undone == 1 && cutText.Contains("7 pieces removed") && cutText.Contains("bc_bake: 1 piece it made was no longer where it put it (moved or gone): it stays a real piece"), "the check finishes the undo and says it: " + cutText);
  }

  private static void BakeFallenCheckTest()
  {
    Section("fallen: the world-load check finishes an unbake without making a piece twice");
    var fx = FallUnbaked("fallen-check", out _);
    // the unbake ended in the state a world load settles; one of its pieces dropped meanwhile
    var wall = FallMade(fx, "stone_wall_2x1", 5f);
    fx.World.Objects[wall].Y -= 3f;
    fx.Journal.SetState(1, BakeState.Settled);   // the bake before it was settled by a save
    int objects = fx.World.Objects.Count;
    fx.Said.Clear();
    var result = BakeRunner.CheckPending(fx.Ctx);
    C(result.Settled == 1 && fx.World.Objects.Count == objects, $"the dropped piece counts as made: nothing is made again ({objects} objects before, {fx.World.Objects.Count} after; settled {result.Settled})");
    C(!fx.Said.Any(l => l.Contains("pieces made")), "and the check does not say it made any: " + string.Join(" | ", fx.Said));

    Section("fallen: a bake's own statics are the players' and are found only where they were");
    BakeRunner.Reset();
    var bake = BakeNewFx("fallen-bake", BakeStandardWorld, out var taken);
    BakeRun(bake, BakeStandardArea());
    BakeRunner.Reset();
    // the bake was cut after the layer and before the last removals: two statics stand, and one of them fell 3 m
    bake.World.Add(taken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 5f));
    var fell = BakeClone(taken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 7f));
    fell.Y -= 3f;
    bake.World.Add(fell);
    bake.Journal.SetState(1, BakeState.Removing);
    int before = bake.World.Objects.Count;
    bake.World.LenientSearches = 0;
    var recovered = BakeRunner.CheckPending(bake.Ctx);
    C(recovered.Settled == 1 && bake.World.Objects.Count == before - 1, $"only the wall that stands where the bake found it is taken ({before} -> {bake.World.Objects.Count})");
    C(bake.World.Objects.Values.Any(o => o.PrefabName == "stone_wall_2x1" && Math.Abs(o.X - 7f) < 0.001f && Math.Abs(o.Y - fell.Y) < 0.001f), "the fallen one is a player's piece: it stays");
    C(bake.World.LenientSearches == 0, "no lenient search was made for a bake's pieces");

    Section("fallen: bc_bake check lists what stands on, under or close to a record");
    BakeRunner.Reset();
    var c = BakeNewFx("fallen-listing", BakeStandardWorld, out var cTaken);
    BakeRun(c, BakeStandardArea());
    c.World.Add(cTaken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 5f));                                                  // on its record
    var under = BakeClone(cTaken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 7f));
    under.Y -= 3f;
    c.World.Add(under);                                                                                                          // 3 m under its record
    var near = BakeClone(cTaken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 9f));
    near.X += 0.1f;
    c.World.Add(near);                                                                                                           // 10 cm off
    var turned = BakeClone(cTaken.First(p => p.PrefabName == "stone_wall_2x1" && p.X == 11f));
    turned.Y -= 3f;
    turned.RotY += 20f;
    c.World.Add(turned);                                                                                                         // under, but turned 20 degrees
    var floorUnder = BakeClone(cTaken.First(p => p.PrefabName == "wood_floor" && p.X == 40f));
    floorUnder.PrefabName = "wood_beam";
    floorUnder.Prefab = "wood_beam".GetStableHashCode();
    floorUnder.Y -= 3f;
    c.World.Add(floorUnder);                                                                                                     // under, another kind
    var findings = BakeCheck.Run(c.Ctx, c.Layer, c.Port.ZonesWithRecords(), false, out _);
    var onIt = findings.Where(f => f.Kind.StartsWith("Records with a real piece of the same kind standing there")).ToList();
    var underIt = findings.Where(f => f.Kind.StartsWith("Records with a real piece of the same kind straight under")).ToList();
    var closeBy = findings.Where(f => f.Kind.StartsWith("Records with a real piece of the same kind close by")).ToList();
    C(onIt.Count == 1 && onIt[0].Where == "stone_wall_2x1 at 5, 10, 5", "one stands on its record: " + string.Join("; ", onIt.Select(f => f.Where)));
    C(underIt.Count == 1 && underIt[0].Where == "stone_wall_2x1 at 7, 10, 5, a real one 3 m lower", "one stands straight under its record, 3 m: " + string.Join("; ", underIt.Select(f => f.Where)));
    C(closeBy.Count == 1 && closeBy[0].Where == "stone_wall_2x1 at 9, 10, 5", "one is 10 cm off: " + string.Join("; ", closeBy.Select(f => f.Where)));
    C(findings.Count == 3, "a piece under a record but turned 20 degrees, and one of another kind, are not listed: " + findings.Count + " findings");
    var lines = BakeCheck.Lines(c.Ctx, c.Layer, c.Port.ZonesWithRecords(), "the whole layer", false);
    C(lines.Count == 4 && lines.Any(l => l.StartsWith("Records with a real piece of the same kind straight under them") && l.Contains("e.g. stone_wall_2x1 at 7, 10, 5, a real one 3 m lower")),
      "and the command says them with an example each: " + string.Join(" | ", lines));
  }
}

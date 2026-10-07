// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of the crash rules of the in-game bake (BakeCrash in BakeJournal.cs, spec 10.8): every row of the table, the same
// for undo, unbake and drop, and a proof that no row leaves a piece in neither place. The proof runs the operations as small
// machines (memory, the layer file S, the objects W, the journal's state), stops them at every step, lets the disk hold S and W from
// any two moments up to then (a whole save, a save cut between W and S, a save cut after S before the marker), and checks what
// the rule makes of each.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // "#" a piece stands, "." it is missing: "#.#" is three pieces, the middle one missing.
  private static BakeCrashPlan BakePlan(OperationKind kind, BakeState state, bool records, string standing, bool adopted = false) =>
    BakeCrash.Plan(kind, state, records, standing.Select(ch => ch == '#').ToArray(), adopted);

  private static string BakeCrashSay(BakeCrashPlan p) =>
    $"{p.Final}{(p.AddRecords ? " +records" : "")}{(p.RemoveRecords ? " -records" : "")}{(p.RemoveStatics.Length > 0 ? " remove " + string.Join(",", p.RemoveStatics) : "")}"
    + $"{(p.CreatePieces.Length > 0 ? " create " + string.Join(",", p.CreatePieces) : "")}{(p.CompleteAdoption ? " +keys" : "")}{(p.ReleaseAdopted ? " -keys" : "")} (row {p.Row})";

  private static void BakeCrashRowsTest()
  {
    const OperationKind Bake = OperationKind.BakeArea;
    Section("crash rules: the table of 10.8, row by row (a bake)");
    var r2 = BakePlan(Bake, BakeState.Prepared, false, "###");
    C(r2.Final == BakeState.Abandoned && !r2.Changes && r2.Row == 2, "row 2, after step 2, no save: Prepared, old layer, statics: abandoned. " + BakeCrashSay(r2));
    var r3 = BakePlan(Bake, BakeState.Applied, false, "###");
    C(r3.Final == BakeState.Abandoned && !r3.Changes && r3.Row == 3, "row 3, after step 3 or 4, no save: Applied, old layer, statics: abandoned. " + BakeCrashSay(r3));
    var r4 = BakePlan(Bake, BakeState.Applied, true, "###");
    C(r4.Final == BakeState.Settled && r4.RemoveStatics.SequenceEqual([0, 1, 2]) && !r4.AddRecords && !r4.RemoveRecords && r4.Row == 4, "row 4, a save during steps 3 to 6: both present: remove the statics. " + BakeCrashSay(r4));
    var r5a = BakePlan(Bake, BakeState.Removing, false, "###");
    C(r5a.Final == BakeState.Abandoned && !r5a.Changes && r5a.Row == 5, "row 5, during step 7, no save since: abandoned. " + BakeCrashSay(r5a));
    var r5b = BakePlan(Bake, BakeState.Removing, true, "###");
    C(r5b.Final == BakeState.Settled && r5b.RemoveStatics.Length == 3 && r5b.Row == 4, "row 5, when a save ran in steps 3 to 6: roll forward as the row above. " + BakeCrashSay(r5b));
    var r6 = BakePlan(Bake, BakeState.Removing, true, "#.#");
    C(r6.Final == BakeState.Settled && r6.RemoveStatics.SequenceEqual([0, 2]) && !r6.AddRecords && r6.Row == 6, "row 6, a save during step 7: both, partly: remove the statics left. " + BakeCrashSay(r6));
    var r7a = BakePlan(Bake, BakeState.Removed, false, "...");
    C(r7a.Final == BakeState.Settled && r7a.AddRecords && r7a.RemoveStatics.Length == 0 && r7a.Row == 7, "row 7, after step 8, a save cut between W and S: neither present: add the records from the journal. " + BakeCrashSay(r7a));
    var r7b = BakePlan(Bake, BakeState.Removed, false, "###");
    C(r7b.Final == BakeState.Abandoned && !r7b.Changes && r7b.Row == 7, "row 7, when the game fell back to the previous save: statics only: abandoned. " + BakeCrashSay(r7b));
    var r7c = BakePlan(Bake, BakeState.Removed, false, ".#.");
    C(r7c.Final == BakeState.Settled && r7c.AddRecords && r7c.RemoveStatics.SequenceEqual([1]), "row 7, some statics left and no records: records back, the rest removed. " + BakeCrashSay(r7c));
    var r7d = BakePlan(Bake, BakeState.Removing, false, "..#");
    C(r7d.Final == BakeState.Settled && r7d.AddRecords && r7d.RemoveStatics.SequenceEqual([2]), "the same during step 7. " + BakeCrashSay(r7d));
    var r8 = BakePlan(Bake, BakeState.Removed, true, "###");
    C(r8.Final == BakeState.Settled && r8.RemoveStatics.Length == 3 && !r8.AddRecords && r8.Row == 8, "row 8, after step 8, a save cut after S before the marker: both: remove the statics. " + BakeCrashSay(r8));
    var r9 = BakePlan(Bake, BakeState.Removed, true, "...");
    C(r9.Final == BakeState.Settled && !r9.Changes && r9.Row == 9, "row 9, after step 8, a whole save: records only: settled, nothing to do. " + BakeCrashSay(r9));

    Section("crash rules: what the state says, and what is true");
    var behind = BakePlan(Bake, BakeState.Prepared, true, "###");
    C(behind.Final == BakeState.Settled && behind.RemoveStatics.Length == 3, "a state behind the layer (Prepared, records in): the layer decides: roll forward. " + BakeCrashSay(behind));
    var elsewhere = BakePlan(Bake, BakeState.Applied, false, "#.#");
    C(elsewhere.Final == BakeState.Abandoned && !elsewhere.Changes, "before the removal began, a missing static is somebody else's doing: abandoned, the records are not put back. " + BakeCrashSay(elsewhere));
    var none = BakePlan(Bake, BakeState.Removed, false, "");
    C(none.Final == BakeState.Abandoned && !none.Changes, "a bake with no static at all has nothing to roll forward: abandoned");
    foreach (var state in new[] { BakeState.Settled, BakeState.Abandoned, BakeState.Undone })
    {
      var done = BakePlan(Bake, state, false, "..#");
      C(done.Final == state && !done.Changes, $"a journal that is {state} is left alone");
    }

    Section("crash rules: town pieces");
    var keysForward = BakePlan(Bake, BakeState.Removed, true, "...", adopted: true);
    C(keysForward.Final == BakeState.Settled && keysForward.CompleteAdoption && !keysForward.ReleaseAdopted && keysForward.Changes, "records in: the keys are completed on every adopted piece");
    var keysRecords = BakePlan(Bake, BakeState.Removed, false, "..#", adopted: true);
    C(keysRecords.AddRecords && keysRecords.CompleteAdoption, "records put back from the journal: the keys are completed too");
    var keysBack = BakePlan(Bake, BakeState.Removing, false, "###", adopted: true);
    C(keysBack.Final == BakeState.Abandoned && keysBack.ReleaseAdopted && !keysBack.CompleteAdoption, "abandoned: the keys come off, or the next reconciliation would destroy a piece no record owns");
    C(!BakePlan(Bake, BakeState.Removing, false, "###").ReleaseAdopted && !BakePlan(Bake, BakeState.Removed, true, "...").CompleteAdoption, "a bake without town pieces touches no keys");

    Section("crash rules: undo of a bake");
    var u1 = BakePlan(Bake, BakeState.Undoing, true, "...");
    C(u1.Final == BakeState.Undone && u1.CreatePieces.SequenceEqual([0, 1, 2]) && u1.RemoveRecords && !u1.AddRecords, "Undoing, records in, no statics: make them, then take the records out. " + BakeCrashSay(u1));
    var u2 = BakePlan(Bake, BakeState.Undoing, false, "..#");
    C(u2.Final == BakeState.Undone && u2.CreatePieces.SequenceEqual([0, 1]) && !u2.RemoveRecords, "Undoing, records out: make the missing ones. " + BakeCrashSay(u2));
    var u3 = BakePlan(Bake, BakeState.Undoing, false, "###");
    C(u3.Final == BakeState.Undone && !u3.Changes, "Undoing, everything done: undone, nothing to do");
    C(BakePlan(Bake, BakeState.Undoing, true, "###", adopted: true).ReleaseAdopted, "an undo takes the bake keys off the adopted pieces");

    Section("crash rules: unbake");
    const OperationKind Unbake = OperationKind.Unbake;
    C(BakePlan(Unbake, BakeState.Prepared, true, "...").Final == BakeState.Abandoned && !BakePlan(Unbake, BakeState.Prepared, true, "...").Changes, "Prepared: nothing was made: abandoned");
    var b1 = BakePlan(Unbake, BakeState.Unbaking, true, "...");
    C(b1.Final == BakeState.Settled && b1.CreatePieces.SequenceEqual([0, 1, 2]) && b1.RemoveRecords && !b1.AddRecords, "Unbaking, records still in, no pieces: make them, then take the records out. " + BakeCrashSay(b1));
    var b2 = BakePlan(Unbake, BakeState.Unbaking, false, "#.#");
    C(b2.Final == BakeState.Settled && b2.CreatePieces.SequenceEqual([1]) && !b2.RemoveRecords, "Unbaking, records out, one piece missing (the objects were not saved): make it. " + BakeCrashSay(b2));
    var b3 = BakePlan(Unbake, BakeState.Unbaking, true, "###");
    C(b3.Final == BakeState.Settled && b3.CreatePieces.Length == 0 && b3.RemoveRecords, "Unbaking, both present: take the records out. " + BakeCrashSay(b3));
    var b4 = BakePlan(Unbake, BakeState.Undoing, false, "###");
    C(b4.Final == BakeState.Undone && b4.AddRecords && b4.RemoveStatics.SequenceEqual([0, 1, 2]), "undoing an unbake: the records first, then the pieces it made go. " + BakeCrashSay(b4));
    var b5 = BakePlan(Unbake, BakeState.Undoing, true, "..#");
    C(b5.Final == BakeState.Undone && !b5.AddRecords && b5.RemoveStatics.SequenceEqual([2]), "undoing an unbake, records back: the pieces left go. " + BakeCrashSay(b5));
    C(!BakePlan(Unbake, BakeState.Removing, true, "###").Changes && BakePlan(Unbake, BakeState.Removing, true, "###").Final == BakeState.Abandoned, "a state an unbake cannot be in is abandoned, touching nothing");

    Section("crash rules: drop");
    const OperationKind Drop = OperationKind.Drop;
    C(BakePlan(Drop, BakeState.Prepared, true, "").Final == BakeState.Abandoned, "a drop whose layer still has the records did not take effect");
    C(BakePlan(Drop, BakeState.Applied, false, "").Final == BakeState.Settled && !BakePlan(Drop, BakeState.Applied, false, "").Changes, "a drop whose layer lacks the records took effect");
    var d1 = BakePlan(Drop, BakeState.Undoing, false, "");
    C(d1.Final == BakeState.Undone && d1.AddRecords, "undoing a drop: the records come back");
    C(BakePlan(Drop, BakeState.Undoing, true, "").Final == BakeState.Undone && !BakePlan(Drop, BakeState.Undoing, true, "").Changes, "... or are back already");
    C(BakePlan(OperationKind.Load, BakeState.Applied, true, "").Final == BakeState.Abandoned, "a kind a journal does not hold is abandoned");
  }

  // ------------------------------------------------------------------------------------------------ the proof

  private sealed class BakeSim
  {
    // The journal's state: written at once, so it is the same in memory and on disk.
    public BakeState State;
    // The layer holds the journal's records.
    public bool Records;
    // Piece i stands in the world.
    public bool[] World = [];
    // Adopted piece j carries the bake keys.
    public bool[] Keyed = [];

    public BakeSim Clone() => new() { State = State, Records = Records, World = (bool[])World.Clone(), Keyed = (bool[])Keyed.Clone() };
  }

  private sealed class BakeScenario
  {
    public string Name = "";
    public OperationKind Kind;
    public BakeSim Initial = new();
    public List<Action<BakeSim>> Steps = [];
    public bool Adopted;
    // Pieces that exist in two forms (as an object and as a record) and must never exist in none.
    public bool Conserves = true;
    // What must be true once the rule has done its work: null, or what is wrong.
    public Func<BakeState, bool, bool[], bool[], string> Expect = (_, _, _, _) => null;
  }

  private static string BakeAll(bool[] flags, bool value, string what) => flags.All(f => f == value) ? null : $"{what} should all be {value}: {string.Join("", flags.Select(f => f ? "#" : "."))}";

  private static int BakeRunScenario(BakeScenario sc, List<string> problems)
  {
    var snapshots = new List<BakeSim> { sc.Initial.Clone() };
    var sim = sc.Initial.Clone();
    foreach (var step in sc.Steps)
    {
      step(sim);
      snapshots.Add(sim.Clone());
    }
    int combos = 0;
    // Crash after c steps (c = 1 is the first: before it there is no journal). The disk holds the layer as it was at moment a and
    // the objects as they were at moment b, whatever the two moments (a whole save is a = b; a cut save mixes the saved moment with
    // an earlier one); the state file is as it was at the crash.
    for (int c = 1; c <= sc.Steps.Count; c++)
      for (int a = 0; a <= c; a++)
        for (int b = 0; b <= c; b++)
        {
          combos++;
          var state = snapshots[c].State;
          bool records = snapshots[a].Records;
          var world = snapshots[b].World;
          var keyed = snapshots[b].Keyed;
          var plan = BakeCrash.Plan(sc.Kind, state, records, world, sc.Adopted);
          var at = $"{sc.Name}: after step {c}, layer of moment {a}, objects of moment {b}, state {state}: {BakeCrashSay(plan)}";
          if (plan.RemoveStatics.Any(i => !world[i]) || plan.CreatePieces.Any(i => world[i]))
          {
            problems.Add(at + " | removes what is not there or makes what is");
            continue;
          }
          if (plan.RemoveStatics.Intersect(plan.CreatePieces).Any())
          {
            problems.Add(at + " | removes and makes one piece");
            continue;
          }
          var worldAfter = world.Select((w, i) => (w && !plan.RemoveStatics.Contains(i)) || plan.CreatePieces.Contains(i)).ToArray();
          bool recordsAfter = (records || plan.AddRecords) && !plan.RemoveRecords;
          var keyedAfter = keyed.Select(k => plan.CompleteAdoption ? true : plan.ReleaseAdopted ? false : k).ToArray();
          if (!(plan.Final.Final()))
          {
            problems.Add(at + " | the rule leaves the operation unfinished");
            continue;
          }
          if (sc.Conserves)
            for (int i = 0; i < world.Length; i++)
              if ((world[i] || records) && !(worldAfter[i] || recordsAfter))
              {
                problems.Add(at + $" | piece {i} was somewhere and is nowhere");
                break;
              }
          var wrong = sc.Expect(plan.Final, recordsAfter, worldAfter, keyedAfter);
          if (wrong != null)
            problems.Add(at + " | " + wrong);
        }
    return combos;
  }

  private static BakeScenario BakeScenarioBake(bool town, bool stateFirst)
  {
    var sc = new BakeScenario
    {
      Name = "bake" + (town ? " with town pieces" : "") + (stateFirst ? "" : ", layer before state"),
      Kind = OperationKind.BakeArea,
      Adopted = town,
      Initial = new BakeSim { State = BakeState.Settled, Records = false, World = [true, true, true], Keyed = town ? [false, false] : [] },
    };
    sc.Steps.Add(s => s.State = BakeState.Prepared);
    if (stateFirst)
    {
      sc.Steps.Add(s => s.State = BakeState.Applied);
      sc.Steps.Add(s => s.Records = true);
    }
    else
    {
      sc.Steps.Add(s => s.Records = true);
      sc.Steps.Add(s => s.State = BakeState.Applied);
    }
    if (town)
    {
      sc.Steps.Add(s => s.Keyed[0] = true);
      sc.Steps.Add(s => s.Keyed[1] = true);
    }
    sc.Steps.Add(s => s.State = BakeState.Removing);
    for (int i = 0; i < 3; i++)
    {
      int k = i;
      sc.Steps.Add(s => s.World[k] = false);
    }
    // A bake ends in Removed, not Settled: the next world load settles it, once the layer and the objects are known to be on disk.
    sc.Steps.Add(s => s.State = BakeState.Removed);
    sc.Expect = (final, records, world, keyed) =>
    {
      if (final == BakeState.Settled)
        return (records ? null : "settled with no records") ?? BakeAll(world, false, "the statics") ?? (town ? BakeAll(keyed, true, "the keys") : null);
      if (final == BakeState.Abandoned)
        return (!records ? null : "abandoned with records") ?? BakeAll(world, true, "the statics") ?? (town ? BakeAll(keyed, false, "the keys") : null);
      return "a bake ends " + final;
    };
    return sc;
  }

  private static BakeScenario BakeScenarioUndoBake(bool town)
  {
    var sc = new BakeScenario
    {
      Name = "undo of a bake" + (town ? " with town pieces" : ""),
      Kind = OperationKind.BakeBox,
      Adopted = town,
      Initial = new BakeSim { State = BakeState.Removed, Records = true, World = [false, false, false], Keyed = town ? [true, true] : [] },
    };
    sc.Steps.Add(s => s.State = BakeState.Undoing);
    for (int i = 0; i < 3; i++)
    {
      int k = i;
      sc.Steps.Add(s => s.World[k] = true);
    }
    if (town)
    {
      sc.Steps.Add(s => s.Keyed[0] = false);
      sc.Steps.Add(s => s.Keyed[1] = false);
    }
    sc.Steps.Add(s => s.Records = false);
    sc.Expect = (final, records, world, keyed) =>
      final != BakeState.Undone ? "an undo ends " + final : (!records ? null : "undone with records") ?? BakeAll(world, true, "the statics") ?? (town ? BakeAll(keyed, false, "the keys") : null);
    return sc;
  }

  private static BakeScenario BakeScenarioUnbake()
  {
    var sc = new BakeScenario
    {
      Name = "unbake",
      Kind = OperationKind.Unbake,
      Initial = new BakeSim { State = BakeState.Settled, Records = true, World = [false, false, false] },
    };
    sc.Steps.Add(s => s.State = BakeState.Prepared);
    sc.Steps.Add(s => s.State = BakeState.Unbaking);
    for (int i = 0; i < 3; i++)
    {
      int k = i;
      sc.Steps.Add(s => s.World[k] = true);
    }
    sc.Steps.Add(s => s.Records = false);
    // An unbake ends in Unbaking as well: a save cut between the objects and the layer file would otherwise lose the pieces.
    sc.Expect = (final, records, world, keyed) =>
    {
      if (final == BakeState.Settled)
        return (!records ? null : "settled with the records in") ?? BakeAll(world, true, "the pieces");
      if (final == BakeState.Abandoned)
        return (records ? null : "abandoned without the records") ?? BakeAll(world, false, "the pieces");
      return "an unbake ends " + final;
    };
    return sc;
  }

  private static BakeScenario BakeScenarioUndoUnbake()
  {
    var sc = new BakeScenario
    {
      Name = "undo of an unbake",
      Kind = OperationKind.Unbake,
      Initial = new BakeSim { State = BakeState.Unbaking, Records = false, World = [true, true, true] },
    };
    sc.Steps.Add(s => s.State = BakeState.Undoing);
    sc.Steps.Add(s => s.Records = true);
    for (int i = 0; i < 3; i++)
    {
      int k = i;
      sc.Steps.Add(s => s.World[k] = false);
    }
    sc.Expect = (final, records, world, keyed) =>
      final != BakeState.Undone ? "an undo ends " + final : (records ? null : "undone without the records") ?? BakeAll(world, false, "the pieces");
    return sc;
  }

  private static BakeScenario BakeScenarioDrop(bool undo)
  {
    var sc = new BakeScenario
    {
      Name = undo ? "undo of a drop" : "drop",
      Kind = OperationKind.Drop,
      Conserves = false,
      Initial = new BakeSim { State = undo ? BakeState.Applied : BakeState.Settled, Records = !undo },
    };
    if (undo)
    {
      sc.Steps.Add(s => s.State = BakeState.Undoing);
      sc.Steps.Add(s => s.Records = true);
      sc.Expect = (final, records, world, keyed) => final != BakeState.Undone ? "an undo ends " + final : records ? null : "undone without the records";
    }
    else
    {
      sc.Steps.Add(s => s.State = BakeState.Prepared);
      sc.Steps.Add(s => s.Records = false);
      sc.Steps.Add(s => s.State = BakeState.Applied);
      sc.Expect = (final, records, world, keyed) =>
        final == BakeState.Settled ? (!records ? null : "settled with the records in") : final == BakeState.Abandoned ? (records ? null : "abandoned without the records") : "a drop ends " + final;
    }
    return sc;
  }

  private static void BakeCrashProofTest()
  {
    Section("crash rules: no row leaves a piece in neither place (every stop, every pair of saved moments)");
    var scenarios = new[]
    {
      BakeScenarioBake(false, true), BakeScenarioBake(false, false), BakeScenarioBake(true, true), BakeScenarioBake(true, false),
      BakeScenarioUndoBake(false), BakeScenarioUndoBake(true), BakeScenarioUnbake(), BakeScenarioUndoUnbake(), BakeScenarioDrop(false), BakeScenarioDrop(true),
    };
    int total = 0;
    foreach (var sc in scenarios)
    {
      var problems = new List<string>();
      int combos = BakeRunScenario(sc, problems);
      total += combos;
      C(problems.Count == 0, $"{sc.Name}: {combos} states a stop can leave, every one settled with each piece in one place" + (problems.Count > 0 ? "; first problems: " + string.Join(" || ", problems.Take(3)) : ""));
    }
    System.Console.WriteLine($"   {total} stops and pairs of saved moments in all");

    Section("crash rules: whatever the disk holds, no piece that was somewhere ends nowhere");
    // Every combination of state, layer and objects, reachable or not, for three pieces: the rule may refuse to change, never lose.
    int checkedStates = 0, lost = 0;
    foreach (var kind in new[] { OperationKind.BakeArea, OperationKind.BakeBox, OperationKind.Unbake })
      foreach (var state in Enum.GetValues(typeof(BakeState)).Cast<BakeState>())
        foreach (var records in new[] { false, true })
          for (int mask = 0; mask < 8; mask++)
          {
            var world = new[] { (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0 };
            var plan = BakeCrash.Plan(kind, state, records, world, false);
            checkedStates++;
            var worldAfter = world.Select((w, i) => (w && !plan.RemoveStatics.Contains(i)) || plan.CreatePieces.Contains(i)).ToArray();
            bool recordsAfter = (records || plan.AddRecords) && !plan.RemoveRecords;
            for (int i = 0; i < 3; i++)
              if ((world[i] || records) && !(worldAfter[i] || recordsAfter))
              {
                lost++;
                if (lost < 4)
                  System.Console.WriteLine($"   lost: {kind} {state} records {records} world {mask}: {BakeCrashSay(plan)}");
              }
            if (plan.RemoveStatics.Any(i => !world[i]) || plan.CreatePieces.Any(i => world[i]))
              lost++;
          }
    C(lost == 0, $"{checkedStates} combinations of kind, state, records and objects: {lost} pieces lost");
  }
}

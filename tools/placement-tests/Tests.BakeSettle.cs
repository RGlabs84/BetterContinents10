// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of BakeSettle: an operation that ended in a state the next world load would settle is settled by a complete save that began after
// it ended, and what that keeps a load from doing (making again the pieces that players demolished since).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static void BakeSettleTest()
  {
    Section("settle: a complete save that began after the operation ended settles it");
    BakeSettle.Clear();
    BakeRunner.Reset();
    var fx = BakeNewFx("settle", BakeStandardWorld, out _);
    BakeRun(fx, BakeStandardArea());
    var journal = new BakeJournal(fx.Folder);
    C(journal.GetState(1) == BakeState.Removed && BakeSettle.Count == 1, "a bake ends in Removed and waits for a save");
    BakeSettle.Poll(fx.Saves);
    C(journal.GetState(1) == BakeState.Removed, "no save yet: it waits");
    fx.Saves.Completed++;
    BakeSettle.Poll(fx.Saves);
    C(journal.GetState(1) == BakeState.Settled && BakeSettle.Count == 0, "one complete save: Settled");

    BakeSettle.Clear();
    BakeRunner.Reset();
    var busy = BakeNewFx("settle-busy", BakeStandardWorld, out _);
    busy.Saves.Saving = true;
    BakeRun(busy, BakeStandardArea());
    var j2 = new BakeJournal(busy.Folder);
    busy.Saves.Saving = false;
    busy.Saves.Completed++;
    BakeSettle.Poll(busy.Saves);
    C(j2.GetState(1) == BakeState.Removed, "a save that was under way when the operation ended copied its objects before the end: it does not settle it");
    busy.Saves.Completed++;
    BakeSettle.Poll(busy.Saves);
    C(j2.GetState(1) == BakeState.Settled, "the next complete save does");

    BakeSettle.Clear();
    BakeRunner.Reset();
    var undo = BakeNewFx("settle-undo", BakeStandardWorld, out _);
    BakeRun(undo, BakeStandardArea());
    undo.Saves.Completed++;
    BakeUndoRun(undo, 1);
    var j3 = new BakeJournal(undo.Folder);
    C(j3.GetState(1) == BakeState.Undoing && BakeSettle.Count == 1, "an undo takes the undo file: the bake's wait is over, the undo's begins");
    undo.Saves.Completed++;
    BakeSettle.Poll(undo.Saves);
    C(j3.GetState(1) == BakeState.Undone, "and a save settles it as Undone");

    BakeSettle.Clear();
    BakeRunner.Reset();
    var taken = BakeNewFx("settle-taken", BakeStandardWorld, out _);
    BakeRun(taken, BakeStandardArea());
    var j4 = new BakeJournal(taken.Folder);
    j4.SetState(1, BakeState.Undoing);
    taken.Saves.Completed++;
    BakeSettle.Poll(taken.Saves);
    C(j4.GetState(1) == BakeState.Undoing, "a state file that another operation has taken is not written over");

    BakeSettle.Clear();
    BakeRunner.Reset();
    var closed = BakeNewFx("settle-closed", BakeStandardWorld, out _);
    BakeRun(closed, BakeStandardArea());
    closed.Saves.Completed += 2;
    closed.Saves.WorldReady = false;
    BakeSettle.Poll(closed.Saves);
    C(new BakeJournal(closed.Folder).GetState(1) == BakeState.Removed && BakeSettle.Count == 0, "a world that is gone settles nothing: the next load does");

    Section("settle: players demolish unbaked pieces, a save, a restart: they stay demolished");
    foreach (bool saved in new[] { true, false })
    {
      BakeSettle.Clear();
      BakeRunner.Reset();
      var fy = BakeNewFx("settle-demolish" + saved, BakeStandardWorld, out var pieces);
      BakeRun(fy, BakeStandardArea());
      fy.Saves.Completed++;
      BakeSettle.Poll(fy.Saves);
      BakeUnbakeRun(fy, BakeWholeWorld);
      // The players take two of the new pieces down.
      var gone = fy.World.Objects.Where(o => o.Value.PrefabName == "stone_wall_2x1" && o.Value.X < 8f).Select(o => o.Key).ToList();
      C(gone.Count == 2, "(two walls to demolish)");
      foreach (var id in gone)
        fy.World.Objects.Remove(id);
      if (saved)
      {
        fy.Saves.Completed++;
        BakeSettle.Poll(fy.Saves);
      }
      var now = fy.Snap("saved");
      var reloaded = BakeReload(fy, now, now, now, "settle-demolish" + saved, 31000);
      BakeRunner.CheckPending(reloaded.Ctx);
      int walls = reloaded.World.Objects.Values.Count(o => o.PrefabName == "stone_wall_2x1");
      if (saved)
        C(walls == pieces.Count(p => p.PrefabName == "stone_wall_2x1") - 2 + 2 && reloaded.World.Objects.Count == 13 - 2, $"after a complete save the demolished walls stay down on the next load (the world holds {reloaded.World.Objects.Count} objects)");
      else
        C(reloaded.World.Objects.Count == 13, $"with no save in between, the load finishes the unbake and makes them again: the unsettled state cannot tell a demolished piece from one a cut save lost ({reloaded.World.Objects.Count} objects)");
    }
    BakeSettle.Clear();
    BakeRunner.Reset();
  }
}

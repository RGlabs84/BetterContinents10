// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

// What a check of the unfinished operations did, for the log and the tests.
internal sealed class CheckResult
{
  public int Settled { get; set; }
  public int Abandoned { get; set; }
  public int Undone { get; set; }
  // Left as they were: the file could not be read, the world cannot take the operation's records, or something threw.
  public int Failed { get; set; }
  public int Examined => Settled + Abandoned + Undone + Failed;
}

internal static partial class BakeRunner
{
  // The check at world load (10.8; spec 13.2): B's ZNet.LoadWorld postfix calls this on the machine that runs the world, once the save's objects
  // and the world's settings (the layer) are loaded and before the reconciliation of Live pieces, synchronously (no player is connected yet).
  // It finishes whatever a crash or a save cut left half done, so that every piece is in exactly one place: an object or a record.
  internal static void AfterWorldLoad()
  {
    // A world is loaded: whatever an earlier world left half done is not this one's, and no operation waits for a save any more.
    Reset();
    BakeSettle.Clear();
    try
    {
      var ctx = BakeRuntime.ContextForCheck();
      if (ctx != null)
        CheckPending(ctx);
    }
    catch (Exception e)
    {
      // Loading the world goes on; the next load tries again, and the undo files are untouched.
      BetterContinents.LogError($"bc_bake: the check of unfinished operations failed: {e}");
    }
  }

  // Settles every operation of the world's undo folder that is not Settled, Abandoned or Undone, oldest first (a later one can rest on an earlier
  // one's records). Each is decided by what is true (the layer's registry, the objects that stand), not by what its state says should be true; see
  // BakeCrash. An operation that cannot be settled stays as it is and is said, and the others go on.
  internal static CheckResult CheckPending(BakeContext ctx)
  {
    var result = new CheckResult();
    if (!ctx.World.Ready)
      return result;
    List<int> pending;
    try
    {
      pending = ctx.Journal.Pending();
    }
    catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
    {
      ctx.Say($"bc_bake: the undo files of this world cannot be listed ({e.Message}); nothing was checked.");
      return result;
    }
    // A settings file that is there and was not read has no layer to settle against: the records may be in it. Whatever is pending waits.
    if (pending.Count > 0 && ctx.Convert?.Kind == BakeWorldKind.Unreadable)
    {
      ctx.Say($"bc_bake: this world has a Better Continents settings file that was not read (a newer Better Continents wrote it, or it is damaged), so its layer is not here to settle {pending.Count} unfinished "
        + $"operation{(pending.Count == 1 ? "" : "s")} against. Nothing is changed; the undo files wait.");
      return result;
    }
    foreach (int number in pending)
    {
      try
      {
        SettleOne(ctx, number, result);
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"bc_bake: operation {number} could not be settled: {e}");
        ctx.Say($"bc_bake: operation {number} could not be settled ({e.Message}); it stays unsettled and the next world load tries again.");
        result.Failed++;
      }
    }
    if (result.Examined > 0)
      ctx.Say($"bc_bake: checked {result.Examined} unfinished operation{(result.Examined == 1 ? "" : "s")}: {result.Settled} settled, {result.Abandoned} abandoned, {result.Undone} undone"
        + (result.Failed > 0 ? $", {result.Failed} left as they were" : "") + ".");
    return result;
  }

  private static void SettleOne(BakeContext ctx, int number, CheckResult result)
  {
    var say = ctx.Say;
    BakeJournalData data;
    try
    {
      data = ctx.Journal.Read(number);
    }
    catch (BakeJournalException e)
    {
      say($"bc_bake: the undo file of operation {number} cannot be read ({e.Message}). That operation stays unsettled, and nothing is changed for it.");
      result.Failed++;
      return;
    }
    var state = ctx.Journal.GetState(number) ?? BakeState.Prepared;
    if (state.Final())
      return;
    var work = Prepare(ctx, data, state, live: false);
    var plan = work.Plan;
    var word = KindWord(data.Kind);
    // The records of a bake that reached the disk in the world's objects and not in its settings cannot go into a world that has no settings
    // (a first save after 'convert' that was cut): they stay in the undo file, and 'bc_bake undo' makes the pieces again.
    if (plan.AddRecords && (ctx.Convert?.Kind ?? BakeWorldKind.BetterContinents) != BakeWorldKind.BetterContinents)
    {
      say($"bc_bake: {word} {number} was stopped in the state {state}, and this world's settings did not reach the disk, so it has no layer to put the records back into. "
        + $"The operation stays unsettled; 'bc_bake undo {number} confirm' makes its pieces again.");
      result.Failed++;
      return;
    }
    Drain(Apply(ctx, work));
    ctx.Journal.SetState(number, plan.Final);
    switch (plan.Final)
    {
      case BakeState.Settled: result.Settled++; break;
      case BakeState.Abandoned: result.Abandoned++; break;
      case BakeState.Undone: result.Undone++; break;
    }
    say($"bc_bake: {word} {number} had stopped in the state {state}; {plan.Why}. {Report(work)}It is {plan.Final.ToString().ToLowerInvariant()}.");
    foreach (var line in LostLines(ctx, work))
      say(line);
  }

  // What a plan did, in a sentence that ends with a space, or nothing when it did nothing.
  private static string Report(BakeWork w)
  {
    var parts = new List<string>();
    if (w.RecordsPut > 0) parts.Add($"{Num(w.RecordsPut)} records put into the layer");
    if (w.RecordsTaken > 0) parts.Add($"{Num(w.RecordsTaken)} records taken out of it");
    if (w.Made > 0) parts.Add($"{Num(w.Made)} pieces made");
    if (w.Removed > 0) parts.Add($"{Num(w.Removed)} pieces removed" + (w.Fallen > 0 ? $" ({Num(w.Fallen)} had dropped or been lifted from where they were put)" : ""));
    if (w.Tagged > 0) parts.Add($"{Num(w.Tagged)} town pieces tagged");
    if (w.Released > 0) parts.Add($"{Num(w.Released)} town pieces freed");
    return parts.Count == 0 ? "" : string.Join(", ", parts) + ". ";
  }
}

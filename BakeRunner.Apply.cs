// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

// What carrying out a plan of BakeCrash needs: the operation's undo file, the plan, and the object that is each of its pieces now.
internal sealed class BakeWork
{
  public BakeJournalData Data { get; init; } = null!;
  public BakeCrashPlan Plan { get; init; } = null!;
  // For each of Data.Pieces and each of Data.Adopted: the object that is it now (BakeMatch), or null.
  public ZDOID?[] Standing { get; init; } = [];
  public ZDOID?[] AdoptedStanding { get; init; } = [];
  // A command, which spreads the work over frames, waits for the players and pushes each change of the layer; or the check at world load,
  // which does it all at once because nobody is connected yet.
  public bool Live { get; init; }

  // What was done, for the report.
  public int RecordsPut { get; set; }
  public int RecordsTaken { get; set; }
  public int Made { get; set; }
  public int Removed { get; set; }
  public int Gone { get; set; }
  public int Tagged { get; set; }
  public int Released { get; set; }
}

internal static partial class BakeRunner
{
  // Carries out a plan. One path for a command (undo, unbake, drop) and for the check at world load, so that what the crash proof shows of the
  // check holds for the commands too. Every step puts a piece in its new place before it takes it out of the old one, and a step that is done
  // already is skipped, so a stop in the middle of any of them is finished by the next run:
  //   1 the records that are missing go in, and the players have them
  //   2 the town pieces get their keys (a Live record needs a piece that carries them)
  //   3 the pieces that are missing are made
  //   4 (a command) the players have the new pieces, before the records that stood for them go
  //   5 the pieces that must go are removed
  //   6 the town pieces lose their keys (before their records go: a keyed piece with no record is a piece the reconciliation destroys)
  //   7 the records that must go come out, and the players have that
  internal static IEnumerator Apply(BakeContext ctx, BakeWork w)
  {
    var data = w.Data;
    var plan = w.Plan;
    var world = ctx.World;
    var layer = ctx.Layer;
    var pieces = data.Pieces;
    bool live = w.Live;
    var before = layer.Current;

    if (plan.AddRecords)
    {
      var change = data.IsBake ? layer.Add(data) : layer.PutBack(data);
      w.RecordsPut = data.Records.Count;
      if (live)
      {
        yield return PushAndSay(ctx, change);
        yield return ReconcileFor(ctx, data, before, change);
      }
      before = change.Layer;
    }

    if (plan.CompleteAdoption)
      for (int j = 0; j < data.Adopted.Count; j++)
      {
        if (w.AdoptedStanding[j] is { } id)
        {
          world.Adopt(id, data.Adopted[j].Source, data.Adopted[j].Id, layer.Revision);
          w.Tagged++;
        }
        if (live && (j + 1) % AdoptionsPerFrame == 0)
          yield return null;
      }

    var made = new List<ZDOID>(plan.CreatePieces.Length);
    int count = 0;
    foreach (int i in plan.CreatePieces)
    {
      if (!world.Ready)
        throw new BakeStopException($"the world closed after {Num(made.Count)} pieces were made. The next world load settles the rest.");
      made.Add(world.Create(pieces[i]));
      if (live && ++count % PiecesPerFrame == 0)
        yield return null;
    }
    w.Made = made.Count;

    if (live && made.Count > 0 && plan.RemoveRecords)
      yield return WaitSent(ctx, made, "the pieces");

    count = 0;
    foreach (int i in plan.RemoveStatics)
    {
      if (!world.Ready)
        throw new BakeStopException($"the world closed after {Num(w.Removed)} pieces were removed. The next world load settles the rest.");
      if (w.Standing[i] is { } target && world.Remove(target, pieces[i]) == RemoveOutcome.Removed)
        w.Removed++;
      else
        w.Gone++;
      if (live && ++count % PiecesPerFrame == 0)
        yield return null;
    }

    if (plan.ReleaseAdopted)
    {
      // Only an unbake gives a piece a creator (so that a zone reset keeps it, design 3.6); an undo and an abandoned bake give it back as it was.
      long creator = data.Kind == OperationKind.Unbake ? (live ? ctx.Who.Creator : ConsoleCreator) : 0L;
      for (int j = 0; j < data.Adopted.Count; j++)
      {
        if (w.AdoptedStanding[j] is { } id)
        {
          world.Release(id, creator);
          w.Released++;
        }
        if (live && (j + 1) % AdoptionsPerFrame == 0)
          yield return null;
      }
    }

    if (plan.RemoveRecords)
    {
      var change = data.IsBake ? layer.UndoBake(data.Number) : layer.Remove(data);
      w.RecordsTaken = data.Records.Count;
      if (live)
      {
        yield return PushAndSay(ctx, change);
        yield return ReconcileFor(ctx, data, before, change);
      }
    }
  }

  // The Live pieces of the layer follow a change of it where they can have been affected: a drop (its pieces go, or stand as orphans), and an
  // operation that held town pieces.
  private static IEnumerator ReconcileFor(BakeContext ctx, BakeJournalData data, BakedLayer? before, LayerChange change)
  {
    if (data.Kind == OperationKind.Drop || data.Adopted.Count > 0)
      yield return ReconcileAfter(ctx, before, change);
  }

  // Runs a routine to its end at once, for the check at world load (nothing in it waits).
  internal static void Drain(IEnumerator routine)
  {
    while (routine.MoveNext())
      if (routine.Current is IEnumerator nested)
        Drain(nested);
  }

  // The plan of an operation as it stands now: what is in the world and in the layer, set against the state the undo file says it had reached.
  internal static BakeWork Prepare(BakeContext ctx, BakeJournalData data, BakeState state, bool live, bool freshPieces = false)
  {
    var pieces = data.Pieces;
    var standing = freshPieces || pieces.Count == 0 ? new ZDOID?[pieces.Count] : ctx.World.FindStanding(pieces);
    var adopted = data.Adopted.Count == 0 ? [] : ctx.World.FindStanding(data.Adopted.Select(a => a.Place).ToList());
    bool present = RecordsPresent(ctx.Layer, data)
      ?? throw new InvalidOperationException($"the layer lists operation {data.Number} as another kind of operation than the undo file does: they are not of one world's history");
    var plan = BakeCrash.Plan(data.Kind, state, present, standing.Select(s => s != null).ToArray(), data.Adopted.Count > 0);
    return new BakeWork { Data = data, Plan = plan, Standing = standing, AdoptedStanding = adopted, Live = live };
  }

  // Whether the layer holds the records the operation's undo file lists: a bake's are in once its entry is in the registry (an edit enters the
  // records and the entry together), an unbake's and a drop's are out once theirs is. Null when the registry names the number as another kind of
  // operation: the file and the layer are not of one history.
  internal static bool? RecordsPresent(IBakeLayerPort layer, BakeJournalData data)
  {
    bool entered = false;
    if (layer.TryGetOperation(data.Number, out var operation))
    {
      if (operation.Kind != data.Kind)
        return null;
      entered = operation.State == OperationState.InLayer;
    }
    return data.IsBake ? entered : !entered;
  }

  internal static string KindWord(OperationKind kind) => kind switch
  {
    OperationKind.BakeArea or OperationKind.BakeBox => "bake",
    OperationKind.Unbake => "unbake",
    OperationKind.Drop => "drop",
    OperationKind.Load => "load",
    _ => "operation",
  };
}

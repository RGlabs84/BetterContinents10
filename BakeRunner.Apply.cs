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
  // The state the plan was made for.
  public BakeState State { get; init; }
  // For an unbake's pieces (BC made them): the y of the object found for each piece, which is not the piece's own when StaticPhysics dropped it
  // or lifted it; null for pieces that players built (they are found only where they were).
  public float[]? StandingY { get; init; }

  // What was done, for the report.
  public int RecordsPut { get; set; }
  public int RecordsTaken { get; set; }
  public int Made { get; set; }
  public int Removed { get; set; }
  public int Gone { get; set; }
  // Found at another height than they were put at (dropped or lifted), and removed all the same.
  public int Fallen { get; set; }
  // Pieces (indices into Data.Pieces) the removal found changed since the plan was made, in the order met.
  public List<int> GoneIndices { get; } = [];
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
    // The layer as it was and as it ends, for the reconciliation of the Live pieces, which comes last: between a change of the layer and the keys
    // of the town pieces it would find records with no keyed piece, and seed a second one.
    var first = layer.Current;
    LayerChange? last = null;

    if (plan.AddRecords)
    {
      var change = data.IsBake ? layer.Add(data) : layer.PutBack(data);
      w.RecordsPut = data.Records.Count;
      last = change;
      if (live)
        yield return PushAndSay(ctx, change);
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
      var expected = pieces[i];
      bool moved = w.StandingY is { } heights && Math.Abs(heights[i] - expected.Y) > BakeMath.PlaceMetres;
      if (moved)
        expected = expected.AtHeight(w.StandingY![i]);
      if (w.Standing[i] is { } target && world.Remove(target, expected) == RemoveOutcome.Removed)
      {
        w.Removed++;
        if (moved)
          w.Fallen++;
      }
      else
      {
        w.Gone++;
        w.GoneIndices.Add(i);
      }
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

    // A world with no layer has no records to take out (an operation whose records never reached the settings: its bake was abandoned).
    if (plan.RemoveRecords && layer.HasLayer)
    {
      var change = data.IsBake ? layer.UndoBake(data.Number) : layer.Remove(data);
      w.RecordsTaken = data.Records.Count;
      last = change;
      if (live)
        yield return PushAndSay(ctx, change);
    }

    if (live && last != null)
      yield return ReconcileFor(ctx, data, first, last);
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
    // An unbake's pieces are BC's own: one that StaticPhysics dropped or lifted where it stood is still that piece (BakeMatch.Find). The pieces of
    // a bake are the players': they are found where they were put and nowhere else.
    bool ours = data.Kind == OperationKind.Unbake;
    float[]? heights = ours && !freshPieces && pieces.Count > 0 ? new float[pieces.Count] : null;
    var standing = freshPieces || pieces.Count == 0 ? new ZDOID?[pieces.Count] : ctx.World.FindStanding(pieces, heights);
    var adopted = data.Adopted.Count == 0 ? [] : ctx.World.FindStanding(data.Adopted.Select(a => a.Place).ToList(), ours ? new float[data.Adopted.Count] : null);
    bool present = RecordsPresent(ctx.Layer, data)
      ?? throw new InvalidOperationException($"the layer lists operation {data.Number} as another kind of operation than the undo file does: they are not of one world's history");
    var plan = BakeCrash.Plan(data.Kind, state, present, standing.Select(s => s != null).ToArray(), data.Adopted.Count > 0);
    return new BakeWork { Data = data, Plan = plan, Standing = standing, AdoptedStanding = adopted, Live = live, State = state, StandingY = heights };
  }

  // The journal's pieces that cannot be dealt with because they are no longer where the operation put them or found them: for an undo of an
  // unbake (and the check that finishes one), the pieces it made that no object answers for (moved sideways, turned, or gone), and in any removal the
  // pieces that changed between the plan and the removal. Indices into Data.Pieces, in order.
  internal static List<int> LostPieces(BakeWork w)
  {
    var lost = new List<int>();
    if (w.Data.Kind == OperationKind.Unbake && w.State == BakeState.Undoing)
      for (int i = 0; i < w.Standing.Length; i++)
        if (w.Standing[i] == null)
          lost.Add(i);
    lost.AddRange(w.GoneIndices);
    return lost;
  }

  // Town pieces the operation looks for by their place and does not find (moved or gone). Only where it would act on them.
  internal static int LostTownPieces(BakeWork w) =>
    w.Plan.CompleteAdoption || w.Plan.ReleaseAdopted ? w.AdoptedStanding.Count(a => a == null) : 0;

  private static string FirstOf(BakeWork w, IReadOnlyList<int> lost, int show = 3) =>
    string.Join("; ", lost.Take(show).Select(i =>
    {
      var piece = w.Data.Pieces[i];
      return FormattableString.Invariant($"{(piece.PrefabName.Length > 0 ? piece.PrefabName : piece.Prefab.ToString())} at {piece.X:0.##}, {piece.Y:0.##}, {piece.Z:0.##}");
    })) + (lost.Count > show ? "; ..." : "");

  // What an operation says of the pieces it could not deal with, as sentences: the done line's addition. It also writes the first few to the log, each
  // with where the nearest object of its kind stands now. Empty when there is none. `would` is the dry run's tense.
  internal static List<string> LostLines(BakeContext ctx, BakeWork w, bool would = false, bool toLog = true)
  {
    var lines = new List<string>();
    var lost = LostPieces(w);
    var data = w.Data;
    if (lost.Count > 0)
    {
      bool ours = data.Kind == OperationKind.Unbake;
      int n = lost.Count;
      string them = n == 1 ? "it" : "them";
      string subject = ours
        ? $"{Num(n)} piece{(n == 1 ? "" : "s")} it made {(would ? (n == 1 ? "is" : "are") : (n == 1 ? "was" : "were"))} no longer where it put {them} (moved or gone)"
        : $"{Num(n)} piece{(n == 1 ? "" : "s")} {(would ? (n == 1 ? "is" : "are") : (n == 1 ? "was" : "were"))} no longer where the {KindWord(data.Kind)} found {them} (changed since)";
      lines.Add($"bc_bake: {subject}: {(n == 1 ? "it stays a real piece" : "they stay real pieces")}, and {(n == 1 ? "its record is" : "their records are")} drawn too. "
        + $"'bc_bake check' lists the real pieces that stand on, under or close to a record (e.g. {FirstOf(w, lost)}).");
      if (toLog)
        foreach (int i in lost.Take(5))
        {
          var piece = data.Pieces[i];
          ctx.Log(FormattableString.Invariant($"bc_bake: {(ours ? "made" : "found")} {piece.PrefabName} at {piece.X:0.###}, {piece.Y:0.###}, {piece.Z:0.###} turned {piece.RotY:0.#}: {ctx.World.Whereabouts(piece)}."));
        }
    }
    int town = LostTownPieces(w);
    if (town > 0)
      lines.Add($"bc_bake: {Num(town)} town piece{(town == 1 ? "" : "s")} {(town == 1 ? "is" : "are")} no longer where the {KindWord(data.Kind)} left {(town == 1 ? "it" : "them")} (moved or gone): "
        + (w.Plan.CompleteAdoption ? $"{(town == 1 ? "it gets" : "they get")} no bake keys back, so the layer seeds {(town == 1 ? "a second piece" : "second pieces")} for {(town == 1 ? "its" : "their")} Live record{(town == 1 ? "" : "s")}." : "nothing to free there."));
    return lines;
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

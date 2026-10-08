// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

// Which records a drop takes out: the compiler's, or those of one in-game bake.
internal sealed class DropScope
{
  public bool Compiler { get; init; }
  public int Bake { get; init; }

  public string Words => Compiler ? "compiler" : "bake " + Bake;
}

internal static partial class BakeRunner
{
  // ------------------------------------------------------------------------------------------------ undo

  // `bc_bake undo [<n>] [confirm]` (10.9): the newest operation, or operation n, put back as it was. Everything is decided by the operation's
  // undo file and by what the layer and the world hold now, with the same plan and the same steps as the check at world load.
  internal static IEnumerator Undo(BakeContext ctx, int? requested, BakeWords words)
  {
    var say = ctx.Say;
    if (!ctx.World.Ready)
    {
      say("bc_bake: load a world first.");
      yield break;
    }
    bool entered = false;
    try
    {
      if (words.Confirm)
      {
        if (!Enter("an undo", true, out var refusal))
        {
          say(refusal);
          yield break;
        }
        entered = true;
      }
      int number;
      if (requested is { } asked)
        number = asked;
      else if (!NewestUndoable(ctx, out number, out var none))
      {
        say(none);
        yield break;
      }
      var echo = $"undo {number}";
      if (!ReadUndoFile(ctx, number, out var data, out var state, out var why))
      {
        say($"bc_bake: {why}");
        yield break;
      }
      if (UndoRefusal(ctx, data, state) is { } refused)
      {
        say($"bc_bake: {refused}");
        yield break;
      }
      var work = Prepare(ctx, data, BakeState.Undoing, live: true);
      var plan = work.Plan;
      var word = KindWord(data.Kind);
      int make = plan.CreatePieces.Length, remove = plan.RemoveStatics.Length;
      var lines = new List<string>
      {
        $"bc_bake {echo}: a dry run, nothing changes. 'bc_bake {echo} confirm' undoes it.",
        $"{Capital(word)} {number} ({Describe(data)}) is undone: "
        + data.Kind switch
        {
          OperationKind.BakeArea or OperationKind.BakeBox => $"its pieces come back as they were, with new object ids (the 30 s of grace before a piece's wear is counted start again), and its records leave the layer.",
          OperationKind.Unbake => "the records it took out come back, and the pieces it made go.",
          _ => "the records it took out come back.",
        },
        $"{Num(make)} pieces to make, {Num(remove)} to remove" + (plan.AddRecords ? $", {Num(data.Records.Count)} records back into the layer" : "") + (plan.RemoveRecords ? $", {Num(data.Records.Count)} records out of it" : "") + ".",
      };
      if (data.Adopted.Count > 0)
        lines.Add($"{Num(data.Adopted.Count)} town pieces " + (plan.ReleaseAdopted ? "lose their bake keys." : "get their bake keys back."));
      lines.AddRange(LostLines(ctx, work, would: true, toLog: false));
      if (!words.Confirm)
      {
        foreach (var line in lines)
          say(line);
        if (ctx.Transport.Unavailable is { } soon)
          say($"This build cannot undo yet: {soon}.");
        yield break;
      }
      if (ctx.Transport.Unavailable is { } nope && (plan.AddRecords || plan.RemoveRecords))
      {
        say($"bc_bake: {nope}, and an undo changes what the players have of the layer. Nothing is changed.");
        yield break;
      }
      double began = ctx.Clock();
      say($"bc_bake: undo of {word} {number} started: {Num(make)} pieces to make, {Num(remove)} to remove.");
      touched = true;
      ctx.Began?.Invoke(number);
      ctx.Journal.SetState(number, BakeState.Undoing);
      yield return Apply(ctx, work);
      // The end state is Undoing, which the next world load settles into Undone.
      ctx.Ended?.Invoke(number, BakeState.Undone);
      if (data.Kind != OperationKind.Drop)
        BakedApi.RaiseUnbaked(data.ToOperation());
      say($"bc_bake: undo of {word} {number} done in {Seconds(ctx.Clock() - began)}:" + (work.Made > 0 ? $" {Num(work.Made)} pieces made," : "")
        + (work.Removed > 0 ? $" {Num(work.Removed)} pieces removed" + (work.Fallen > 0 ? $" ({Num(work.Fallen)} of them had dropped or been lifted from where they were put)" : "") + "," : "")
        + $" {Num(work.RecordsPut)} records put back, {Num(work.RecordsTaken)} taken out. The world saves as usual ('save' saves now).");
      foreach (var line in LostLines(ctx, work))
        say(line);
      ctx.Who.Notify?.Invoke($"Undo of {word} {number} done.");
    }
    finally
    {
      Leave(entered);
    }
  }

  // The operation `bc_bake undo` takes without a number: the newest the layer lists that is not undone, if it is one that has an undo file.
  private static bool NewestUndoable(BakeContext ctx, out int number, out string none)
  {
    number = 0;
    none = "bc_bake: there is nothing to undo.";
    var newest = ctx.Layer.Operations.Where(o => o.State == OperationState.InLayer).OrderByDescending(o => o.Number).FirstOrDefault();
    if (newest != null)
    {
      if (newest.Kind == OperationKind.Load)
      {
        none = $"bc_bake: the newest operation is load {newest.Number}, which an undo does not cover. 'bc_bake load' of the layer file kept before it (in the undo folder, layer-r<revision>.bcp) goes back.";
        return false;
      }
      number = newest.Number;
      return true;
    }
    // A layer that lists nothing: an operation that stopped before the layer could hold its records, and is waiting to be undone.
    var pending = ctx.Journal.Numbers().Where(n => !(ctx.Journal.GetState(n) ?? BakeState.Prepared).Final()
      && !(ctx.Layer.TryGetOperation(n, out var listed) && listed.State == OperationState.Undone)).ToList();
    if (pending.Count > 0)
    {
      number = pending.Max();
      return true;
    }
    return false;
  }

  private static bool ReadUndoFile(BakeContext ctx, int number, out BakeJournalData data, out BakeState state, out string why)
  {
    data = null!;
    state = BakeState.Prepared;
    why = "";
    if (!ctx.Journal.Numbers().Contains(number))
    {
      why = $"there is no undo file for operation {number} (the newest {BakeJournal.KeepOperations} are kept, and every one that is not finished).";
      return false;
    }
    try
    {
      data = ctx.Journal.Read(number);
    }
    catch (BakeJournalException e)
    {
      why = $"the undo file of operation {number} cannot be read ({e.Message}).";
      return false;
    }
    state = ctx.Journal.GetState(number) ?? BakeState.Prepared;
    return true;
  }

  // Why an operation cannot be undone now, or null (10.9).
  private static string? UndoRefusal(BakeContext ctx, BakeJournalData data, BakeState state)
  {
    int n = data.Number;
    var word = KindWord(data.Kind);
    bool listed = ctx.Layer.TryGetOperation(n, out var entry);
    bool inLayer = listed && entry.State == OperationState.InLayer;
    if (listed && entry.State == OperationState.Undone || state == BakeState.Undone)
      return $"{word} {n} was undone already.";
    if (state == BakeState.Abandoned)
      return $"{word} {n} was abandoned when the world was loaded: it had changed nothing, so there is nothing to undo.";
    if (state == BakeState.Prepared)
      return $"{word} {n} stopped before it changed anything; the next world load abandons it. There is nothing to undo.";
    switch (data.Kind)
    {
      case OperationKind.BakeArea:
      case OperationKind.BakeBox:
        if (inLayer)
        {
          if (state is not (BakeState.Removed or BakeState.Settled))
            return $"bake {n} is not finished (it is {state}): load the world again, which settles it, and undo it then.";
          long left = ctx.Layer.RecordsOfSource(n);
          if (left == 0)
            return $"all the records of bake {n} are out of the layer already (unbaked or dropped since): undo that first.";
          if (left != data.Records.Count)
            return $"part of bake {n} was unbaked since ({Num(data.Records.Count - left)} of its {Num(data.Records.Count)} records are out of the layer): 'bc_bake unbake' takes the rest back.";
          return null;
        }
        // The layer does not list it: the bake stopped, and the world's settings never got its records. The pieces it removed can still be made again.
        return state is BakeState.Removing or BakeState.Removed ? null : $"bake {n} is not in the layer, and it had not taken any piece out of the world: there is nothing to undo.";
      case OperationKind.Unbake:
        if (!inLayer)
          return $"unbake {n} is not in the layer (it stopped before the records came out): load the world again, which settles it.";
        return state is BakeState.Unbaking or BakeState.Settled ? null : $"unbake {n} is not finished (it is {state}): load the world again, which settles it.";
      case OperationKind.Drop:
        if (!inLayer)
          return $"drop {n} is not in the layer (it stopped before the records came out): load the world again, which settles it.";
        return state is BakeState.Applied or BakeState.Settled ? null : $"drop {n} is not finished (it is {state}): load the world again, which settles it.";
      default:
        return $"an operation of this kind has no undo file ({word} {n}).";
    }
  }

  private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

  // "area 40 m around 812, -1206, by Wubarrk".
  private static string Describe(BakeJournalData data)
  {
    var who = string.IsNullOrEmpty(data.Who) ? "" : ", by " + data.Who;
    return data.Kind switch
    {
      OperationKind.BakeArea => $"a circle of {data.Radius:0.#} m around {Math.Round((data.X1 + data.X2) / 2f)}, {Math.Round((data.Z1 + data.Z2) / 2f)}{who}",
      OperationKind.BakeBox => $"the box {Math.Round(data.X1)}, {Math.Round(data.Z1)} to {Math.Round(data.X2)}, {Math.Round(data.Z2)}{who}",
      OperationKind.Unbake when data.X1 == 0f && data.Z1 == 0f && data.X2 == 0f && data.Z2 == 0f => $"the whole world{who}",
      OperationKind.Unbake when data.Radius > 0f => $"a circle of {data.Radius:0.#} m around {Math.Round((data.X1 + data.X2) / 2f)}, {Math.Round((data.Z1 + data.Z2) / 2f)}{who}",
      OperationKind.Unbake => $"the box {Math.Round(data.X1)}, {Math.Round(data.Z1)} to {Math.Round(data.X2)}, {Math.Round(data.Z2)}{who}",
      _ => (data.Words + who).TrimStart(',', ' '),
    };
  }

  // ------------------------------------------------------------------------------------------------ drop

  // `bc_bake drop compiler | bake <n> [confirm]` (11.1): the records leave the layer without making pieces. Their Live pieces go with them,
  // as the reconciliation removes any piece whose record is gone (a chest that holds items stays as an orphan). `bc_bake undo` puts them back.
  internal static IEnumerator Drop(BakeContext ctx, DropScope scope, BakeWords words)
  {
    var say = ctx.Say;
    var layer = ctx.Layer;
    if (!ctx.World.Ready)
    {
      say("bc_bake: load a world first.");
      yield break;
    }
    if (!layer.HasLayer)
    {
      say("bc_bake: this world has no layer.");
      yield break;
    }
    if (!scope.Compiler && !(layer.TryGetOperation(scope.Bake, out var bake) && bake.IsBake && bake.State == OperationState.InLayer))
    {
      say($"bc_bake: the layer does not list a bake {scope.Bake} that is in it.");
      yield break;
    }
    bool entered = false;
    try
    {
      if (words.Confirm)
      {
        if (!Enter("a drop", true, out var refusal))
        {
          say(refusal);
          yield break;
        }
        entered = true;
      }
      var records = layer.RecordsIn(layer.ZonesWithRecords(), r => scope.Compiler ? !r.HasSource : r.HasSource && r.Source == scope.Bake);
      var echo = "drop " + scope.Words;
      if (records.Count == 0)
      {
        say($"bc_bake: nothing to drop: the layer holds no {(scope.Compiler ? "compiler's records" : "records of bake " + scope.Bake)}.");
        yield break;
      }
      int live = records.Count(r => r.Role == BakedRole.Live);
      var kinds = new Dictionary<string, int>();
      foreach (var record in records)
        kinds[record.Prefab] = kinds.TryGetValue(record.Prefab, out var n) ? n + 1 : 1;
      var lines = new List<string>
      {
        $"bc_bake {echo}: a dry run, nothing changes. 'bc_bake {echo} confirm' drops.",
        $"This layer holds {Num(records.Count)} {(scope.Compiler ? "records from the compiler's file" : "pieces baked in bake " + scope.Bake)} ({KindsText(kinds)}). Dropping takes them out of the world: they are not made into pieces.",
        "To keep them as real pieces, 'bc_bake unbake" + (scope.Compiler ? " world all" : " world") + "' first. 'bc_bake undo' brings a drop back while its undo file is kept.",
      };
      if (live > 0)
        lines.Add($"{Num(live)} of them are Live records: their pieces go too (a chest that holds items stays where it is, as an orphan: 'bc_bake orphans').");
      if (scope.Compiler)
        lines.Add("The next 'bc_bake load' of the compiler's file brings its records back.");
      if (!words.Confirm)
      {
        foreach (var line in lines)
          say(line);
        if (ctx.Transport.Unavailable is { } soon)
          say($"This build cannot drop yet: {soon}.");
        yield break;
      }
      if (ctx.Transport.Unavailable is { } nope)
      {
        say($"bc_bake: {nope}. Nothing is changed.");
        yield break;
      }
      int number = ctx.Journal.NextNumber(layer.NextOperation);
      var data = new BakeJournalData
      {
        Number = number,
        Kind = OperationKind.Drop,
        Time = ctx.UnixTime(),
        Who = ctx.Who.Name,
        PlatformId = ctx.Who.PlatformId,
        WorldName = ctx.World.WorldName,
        WorldUid = ctx.World.WorldUid,
        RevisionBefore = layer.Revision,
        RevisionAfter = layer.Revision + 1,
        Version = ctx.Version,
        Words = echo,
        Records = records,
      };
      double began = ctx.Clock();
      if (!WriteJournal(ctx, data, out var written))
      {
        say(written);
        yield break;
      }
      say($"bc_bake: drop {number} started, {Num(records.Count)} records. Undo file written.");
      var before = layer.Current;
      // The state first, then the layer: a stop between the two leaves a drop that did not happen, which the check says.
      ctx.Journal.SetState(number, BakeState.Applied);
      var change = layer.Remove(data);
      yield return PushAndSay(ctx, change);
      yield return ReconcileAfter(ctx, before, change);
      // The end state is Applied, which the next world load settles.
      ctx.Who.Notify?.Invoke($"Drop {number} done: {Num(records.Count)} records.");
      ctx.Ended?.Invoke(number, BakeState.Settled);
      say($"bc_bake: drop {number} done in {Seconds(ctx.Clock() - began)}: {Num(records.Count)} records out of the layer (revision {change.Revision}). 'bc_bake undo' puts them back.");
    }
    finally
    {
      Leave(entered);
    }
  }
}

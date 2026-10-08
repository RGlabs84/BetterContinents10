// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

internal static partial class BakeRunner
{
  // `bc_bake load [<file>] [convert] [confirm]` (6.2, U5): a compiler's new file in a running world. The file's records without a source and its zone
  // sections replace the layer's; every in-game record stays; the palette is rebuilt. The layer as it was is kept in the undo folder first
  // (layer-r<revision>.bcp), so that loading that file goes back. No undo file and no state: the new layer replaces the old in one step, and a
  // save writes whichever is current; the Live pieces follow by the reconciliation, which also runs at every world load.
  internal static IEnumerator Load(BakeContext ctx, string label, byte[] bytes, BakeWords words)
  {
    var say = ctx.Say;
    var layer = ctx.Layer;
    if (!ctx.World.Ready)
    {
      say("bc_bake: load a world first.");
      yield break;
    }
    BakedLayer file;
    try
    {
      file = BakedLayer.Read(bytes, bytes.Length);
    }
    catch (BakedFormatException e)
    {
      say($"bc_bake: {label} is not a layer this version can read ({e.Message}). Nothing is changed.");
      yield break;
    }
    bool entered = false;
    try
    {
      if (words.Confirm)
      {
        if (!Enter("a load", true, out var refusal))
        {
          say(refusal);
          yield break;
        }
        entered = true;
      }
      long incoming = file.RecordsOfSource(0), inGameInFile = file.Placements - incoming;
      long current = layer.RecordsOfSource(0);
      long staying = layer.Records - current;
      var echo = $"load {label}" + (words.Convert ? " convert" : "");
      var lines = new List<string>
      {
        $"bc_bake {echo}: a dry run, nothing changes. 'bc_bake {echo} confirm' loads it.",
        $"File: revision {file.Revision}, {Num(file.Placements)} records in {Num(file.Zones.Count)} zones, {Bytes(file.Length)}, made by {file.Producer}.",
        $"It replaces the layer's {Num(current)} compiler records with its {Num(incoming)}, and the zones' ground, paint and masks with its own; the {Num(staying)} records baked in game stay. "
        + (layer.HasLayer ? $"The layer as it is now (revision {layer.Revision}) is kept in the undo folder first." : "This world has no layer yet."),
      };
      if (inGameInFile > 0)
        lines.Add($"{Num(inGameInFile)} records baked in game are in this file; load replaces only the compiler's records.");
      if (!words.Confirm)
      {
        foreach (var line in lines)
          say(line);
        WorldAllows(ctx, words, confirm: false, saysInDryRun: true);
        if (ctx.Transport.Unavailable is { } soon)
          say($"This build cannot load yet: {soon}.");
        yield break;
      }
      if (ctx.Transport.Unavailable is { } nope)
      {
        say($"bc_bake: {nope}. Nothing is changed.");
        yield break;
      }
      if (!WorldAllows(ctx, words, confirm: true, saysInDryRun: true))
        yield break;
      int number = ctx.Journal.NextNumber(layer.NextOperation);
      double began = ctx.Clock();
      try
      {
        ctx.Journal.Prune();
        if (layer.Current is { } now)
          ctx.Journal.KeepLayer(now);
      }
      catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
      {
        say($"bc_bake: the layer as it is could not be kept in the undo folder ({e.Message}), so nothing was changed.");
        yield break;
      }
      if (ctx.Convert?.Kind == BakeWorldKind.Vanilla)
      {
        ctx.Convert.Convert();
        say("bc_bake: this world is now a Better Continents world that keeps the game's own ground and biomes.");
      }
      var operation = new OperationInfo((ushort)number, OperationKind.Load, OperationState.InLayer, ctx.UnixTime(), ctx.Who.Name, 0f, 0f, 0f, 0f, 0f,
        (uint)Math.Min(incoming, uint.MaxValue), (uint)Math.Min(current, uint.MaxValue), 0, ctx.Version);
      var before = layer.Current;
      var change = layer.Load(file, operation, out int leftOut);
      say($"bc_bake: load {number}: layer revision {change.Revision} ({Bytes(change.Bytes)}), {Num(incoming)} compiler records." + (leftOut > 0 ? $" {Num(leftOut)} records baked in game in the file were left out." : ""));
      yield return PushAndSay(ctx, change);
      yield return ReconcileAfter(ctx, before, change);
      say($"bc_bake: load {number} done in {Seconds(ctx.Clock() - began)}." + (before != null ? $" To go back, 'bc_bake load' the file layer-r{before.Revision}.bcp in the undo folder." : ""));
      ctx.Who.Notify?.Invoke($"Load {number} done.");
    }
    finally
    {
      Leave(entered);
    }
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

// The bake's seams, over the real parts: the network and server work of baked placements (BakedTransfer, BakedReconcile) and the client renderer
// (BakedClient, BakedKinds). Thin on purpose: what they decide is theirs, and is tested where they are.

// The players' side of a change of the layer: who is connected, and sending the new layer and waiting for it (5.3).
internal sealed class BakedTransport : IBakeTransport
{
  // A command runs only where the world is, which is where the clients are known.
  public string? Unavailable => BakedTransfer.RunsWorld() ? null : "this machine does not run the world, so it has no players to send a layer to";

  // The players whose join is over: a push reaches them.
  public int Players => BakedTransfer.Clients.Count(c => c.JoinDone && c.Link.Connected);

  // Everyone connected (a player who is still joining too), with the Better Continents version their handshake told (null: they sent none): a
  // conversion needs them all on this version.
  public IReadOnlyList<(string Name, string? Version)> Peers => BakedTransfer.Clients.Where(c => c.Link.Connected).Select(c => (c.Name, c.Version)).ToList();

  public IEnumerator Push(LayerChange change, Action<BakePush> done)
  {
    // A change that leaves no layer has nothing to send.
    if (change.Layer == null)
    {
      done(new BakePush { Summary = "no layer is left to send" });
      yield break;
    }
    BakedTransfer.PushReport? report = null;
    yield return BakedTransfer.Push(change.Layer, change.Changed ?? [], made => report = made);
    done(new BakePush { Summary = report?.Summary() ?? "", Seconds = report?.Seconds ?? 0.0 });
  }
}

// The Live pieces follow the layer (7.3).
internal sealed class BakedReconciler : IBakeReconcile
{
  public IEnumerator Run(BakedLayer? old, LayerChange change, Action<string> say) => BakedReconcile.Reconcile(old, change.Layer, say);

  // What a run would do, said and not done: the world's pieces and the layer's records, planned.
  public IEnumerator Preview(BakedLayer? old, BakedLayer next, Action<string> say)
  {
    ReconcileInput? input = null;
    var gathering = BakedReconcile.Gather(old, next, made => input = made);
    while (gathering.MoveNext())
      yield return gathering.Current;
    if (input == null)
      yield break;
    var plan = BakedReconcile.Plan(input);
    say("The live pieces would follow: " + plan.Summary() + ".");
  }
}

// A world that is not a Better Continents world becomes one that keeps the game's terrain (4.3): its settings are the game's, with no layer yet, the
// patches follow, and every player who is in it is sent the new settings. The settings file is written at the next save, as for any world.
internal sealed class BakedConvert : IBakeConvert
{
  public string? Unavailable => null;

  // The patches follow the settings (the offline suite has no Harmony to switch them with: it counts the calls).
  internal static Action Patch = BetterContinents.DynamicPatch;

  public BakeWorldKind Kind => BakeRuntime.KindOfWorld();

  public void Convert()
  {
    var old = BetterContinents.Settings;
    var made = new BetterContinents.BetterContinentsSettings
    {
      EnabledForThisWorld = true,
      Version = BetterContinents.BetterContinentsSettings.UnifiedVersion,
      GameTerrain = true,
      WideSectors = old.WideSectors,
      Layer = old.Layer,
    };
    BetterContinents.Settings = made;
    Patch();
    made.Dump();
    var package = new ZPackage();
    // The newest format, which a world on the game's terrain is written in whatever the Override version setting says.
    made.Serialize(package, true, formatVersion: BetterContinents.BetterContinentsSettings.UnifiedVersion);
    int sent = BakedTransfer.BroadcastSettings(package);
    BetterContinents.Log($"Baked pieces: this world keeps the game's own terrain and is a Better Continents world now; its settings went to {sent} player{(sent == 1 ? "" : "s")}.");
  }
}

// The drawing on this machine (C's): counts, off and on, and what the layer names that this game lacks.
internal sealed class BakedClientTools : IBakeClientTools
{
  public string? Unavailable => null;

  public void Stats(Action<string> say) => BakedClient.Stats(say);
  public void Hide() => BakedClient.Hide();
  public void Show() => BakedClient.Show();
  public IReadOnlyList<string>? MissingKinds() => BakedKinds.Missing;
}

internal sealed class BakedOrphans : IBakeOrphans
{
  public string? Unavailable => null;

  public IReadOnlyList<BakeOrphan> List() => BakedReconcile.Orphans()
    .Select(o => new BakeOrphan { Id = o.Id, Prefab = o.Prefab, Position = o.Position, Reason = o.Reason, HoldsItems = o.HoldsItems }).ToList();

  public void Destroy(IEnumerable<ZDOID> ids, Action<string> say) => BakedReconcile.DestroyOrphans(ids, say);
}

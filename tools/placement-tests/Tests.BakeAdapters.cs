// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The bake's adapters over the real parts (BakeAdapters.cs): who the transport counts as a player and as a peer, the conversion's settings and the
// push of them, the reconciliation's preview against B's planner, the client tools' answers, and the unbake dry run that states how many real
// objects it makes (VALtima's layer would make 702,700).

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // A connection that records what it is sent.
  private sealed class BakeStubLink : BakedTransfer.IBakedLink
  {
    public bool Connected { get; set; } = true;
    public readonly List<(string Method, object[] Parameters)> Sent = [];
    public int SendQueueSize => 0;
    public float Now => 0f;
    public void Invoke(string method, params object[] parameters) => Sent.Add((method, parameters));
    public void Flush() { }
    public bool RaiseSendRate(string what, int length) => false;
    public void RestoreSendRate(string why) { }
  }

  private static BakedTransfer.LayerClient BakeStubClient(string name, string version, bool joined, bool connected, out BakeStubLink link)
  {
    link = new BakeStubLink { Connected = connected };
    return new BakedTransfer.LayerClient(name, link, _ => { }) { Version = version, JoinDone = joined };
  }

  private static void BakeAdaptersTest()
  {
    Section("adapters: the transport counts players and peers as BakedTransfer knows them");
    var saved = BakedTransfer.Clients.ToList();
    var savedRuns = BakedTransfer.RunsWorld;
    try
    {
      BakedTransfer.Clients.Clear();
      var transport = new BakedTransport();
      BakedTransfer.RunsWorld = () => false;
      C(transport.Unavailable != null && transport.Unavailable.Contains("does not run the world"), "a machine that does not run the world cannot send a layer: " + transport.Unavailable);
      BakedTransfer.RunsWorld = () => true;
      C(transport.Unavailable == null && transport.Players == 0 && transport.Peers.Count == 0, "a world with nobody in it: no players, no peers");
      BakedTransfer.Clients.Add(BakeStubClient("alice", "0.10.4", joined: true, connected: true, out _));
      BakedTransfer.Clients.Add(BakeStubClient("bob", null, joined: true, connected: true, out _));
      BakedTransfer.Clients.Add(BakeStubClient("(connecting)", "0.10.4", joined: false, connected: true, out _));
      BakedTransfer.Clients.Add(BakeStubClient("gone", "0.10.4", joined: true, connected: false, out _));
      C(transport.Players == 2, $"players are those whose join is over and are connected: alice and bob (got {transport.Players})");
      C(transport.Peers.Select(p => p.Name).SequenceEqual(["alice", "bob", "(connecting)"]) && transport.Peers[1].Version == null && transport.Peers[0].Version == "0.10.4",
        "the peers are everyone connected, a player still joining too, with the version their handshake told (bob sent none): " + string.Join(", ", transport.Peers.Select(p => $"{p.Name} {p.Version ?? "none"}")));
    }
    finally
    {
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.AddRange(saved);
      BakedTransfer.RunsWorld = savedRuns;
    }

    Section("adapters: the conversion makes GameTerrain settings and sends them to the players");
    var savedSettings = BC.Settings;
    saved = BakedTransfer.Clients.ToList();
    try
    {
      BakedTransfer.Clients.Clear();
      var joined = BakeStubClient("alice", "0.10.4", joined: true, connected: true, out var aliceLink);
      var joining = BakeStubClient("carol", "0.10.4", joined: false, connected: true, out var carolLink);
      var left = BakeStubClient("dave", "0.10.4", joined: true, connected: false, out var daveLink);
      BakedTransfer.Clients.AddRange([joined, joining, left]);
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false, WideSectors = true };
      int patched = 0;
      BakedConvert.Patch = () => patched++;
      var convert = new BakedConvert();
      C(convert.Unavailable == null, "the real conversion is available");
      convert.Convert();
      var made = BC.Settings;
      C(patched == 1, "the patches follow the new settings, once");
      C(made.EnabledForThisWorld && made.GameTerrain && made.Version == BC.BetterContinentsSettings.UnifiedVersion && made.WideSectors,
        $"the world is a Better Continents world now, on the game's terrain, at the unified version, with its sector setting kept (enabled {made.EnabledForThisWorld}, GameTerrain {made.GameTerrain}, version {made.Version}, wide {made.WideSectors})");
      C(aliceLink.Sent.Count == 1 && aliceLink.Sent[0].Method == BakedTransfer.RpcSettingsUpdate && aliceLink.Sent[0].Parameters[0] is ZPackage, "alice, whose join is over, is sent the settings");
      C(carolLink.Sent.Count == 0 && daveLink.Sent.Count == 0, "a player still joining gets them from the join, and one who left gets nothing");
      var back = BC.BetterContinentsSettings.Load(new ZPackage(((ZPackage)aliceLink.Sent[0].Parameters[0]).GetArray()));
      C(back.EnabledForThisWorld && back.GameTerrain && back.WideSectors, "and what is sent reads back as the same settings");
    }
    finally
    {
      BC.Settings = savedSettings;
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.AddRange(saved);
      BakedConvert.Patch = BC.DynamicPatch;
    }

    Section("adapters: the client tools, the orphans and the hook");
    var tools = new BakedClientTools();
    C(tools.Unavailable == null, "the client tools exist in this build");
    C(tools.MissingKinds() == BakedKinds.Missing, "the missing kinds are the renderer's");
    var orphans = new BakedOrphans();
    C(orphans.Unavailable == null && orphans.List().Count == 0, "no orphans in a world that has none");
    C(BakedReconcile.AfterWorldLoadCheck.Method.Name == nameof(BakeRunner.AfterWorldLoad) && BakedReconcile.AfterWorldLoadCheck.Method.DeclaringType == typeof(BakeRunner),
      "the reconciliation's world-load hook is the bake's check: " + BakedReconcile.AfterWorldLoadCheck.Method);
    C(BakeServices.Transport is BakedTransport && BakeServices.Reconcile is BakedReconciler && BakeServices.Convert is BakedConvert && BakeServices.Client is BakedClientTools && BakeServices.Orphans is BakedOrphans,
      "the defaults of BakeServices are the real adapters");
  }
}

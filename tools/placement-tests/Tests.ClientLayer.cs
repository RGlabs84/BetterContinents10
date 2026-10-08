// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The client renderer against the rest of the mod (slice C after the merge of slices A and B):
//   - the zone hold from Take to Release, on the game's own SetLoadingInZone, and the grass that is cut again where a hold ends (the lead's R5-1);
//   - which layer the manager works for: it reads BakedLayerStore.Current every frame, so a Settings swap that raises no Changed event, a layer that
//     is cleared, a world that ends and a new session cannot leave it drawing the world before (the lead's check of note 1);
//   - that the manager reports Applied after every change of the layer, the first layer of a running world included (BakedTransfer.Install leaves
//     the answer to it as soon as it runs, and the machine that runs the world waits for it), over B's made-up connection.
// The manager's frame (Step) needs a camera and the engine: its pieces that matter here are called one by one, and their order in Step is read from
// its IL. Names that only this file uses start with ClientLayer or ClientHold.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private const BindingFlags ClientLayerAny = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

  // ------------------------------------------------------------------------------------------------ the hold, from Take to Release

  private static void ClientHoldLifecycleTest()
  {
    Section("client: a zone is held from Take to Release on the game's own loading list, and the grass is cut again where a hold ends");
    var system = ApiAlive<ZoneSystem>();
    var loading = new Dictionary<Vector2s, List<ZDO>>();
    typeof(ZoneSystem).GetField("m_loadingObjectsInZones", ClientLayerAny)!.SetValue(system, loading);
    var seams = (BakedZoneHold.GameZones, BakedZoneHold.NewZdo, BakedZoneHold.ResetGrass, BakedClient.Now);
    float clock = 100f;
    var grass = new List<(Vector3 Centre, float Radius)>();
    ZDO Fresh() => (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
    try
    {
      BakedZoneHold.GameZones = () => system;
      BakedZoneHold.NewZdo = Fresh;
      BakedZoneHold.ResetGrass = (centre, radius) => grass.Add((centre, radius));
      BakedClient.Now = () => clock;
      var a = new ZoneKey(4, -2);
      var b = new ZoneKey(5, -2);
      var c = new ZoneKey(-3, 7);
      var d = new ZoneKey(-3, 8);

      // Take: our placeholder is in the game's list, in the zone's sector, as a Solid object with no id.
      BakedZoneHold.Take(a);
      var listA = loading.GetValueOrDefault(a.ToVector2s());
      C(BakedZoneHold.Holding(a) && BakedZoneHold.Count == 1 && listA is { Count: 1 } && listA[0].Type == ZDO.ObjectType.Solid && listA[0].GetSector() == a.ToVector2s() && listA[0].m_uid == ZDOID.None,
        "Take puts a Solid placeholder with no id into the game's loading list, in the zone's sector");
      BakedZoneHold.Take(a);
      C(BakedZoneHold.Count == 1 && loading[a.ToVector2s()].Count == 1 && grass.Count == 0, "taking a held zone again changes nothing, and the grass is not touched while a zone is held");
      // A LocationProxy's real object holds the zone already.
      var other = Fresh();
      other.Init();
      other.m_uid = new ZDOID(1234L, 5u);
      typeof(ZDO).GetField("m_position", ClientLayerAny)!.SetValue(other, b.Centre);
      other.Type = ZDO.ObjectType.Solid;
      loading[b.ToVector2s()] = [other];
      BakedZoneHold.Take(b);
      C(BakedZoneHold.Count == 2 && loading[b.ToVector2s()].Count == 2 && ReferenceEquals(loading[b.ToVector2s()][0], other), "in a zone something else holds, the placeholder is added beside it");

      // Release: ours comes out by reference, the zone's entry goes when it was the last, and the grass round the zone is cut again, once.
      BakedZoneHold.Release(a);
      C(!BakedZoneHold.Holding(a) && !loading.ContainsKey(a.ToVector2s()) && BakedZoneHold.Count == 1, "Release takes the placeholder out, and the zone's entry with it when nothing else holds the zone");
      C(grass.Count == 1 && grass[0].Centre == a.Centre && grass[0].Radius == BakedZoneHold.GrassRadius && BakedZoneHold.GrassRadius >= 64f,
        $"and cuts the grass again round the zone's centre, {BakedZoneHold.GrassRadius:0} m (R5-1)");
      BakedZoneHold.Release(a);
      BakedZoneHold.Release(c);
      C(grass.Count == 1, "a zone that is not held is not released again, and cuts no grass");
      BakedZoneHold.Release(b);
      C(loading[b.ToVector2s()].Count == 1 && ReferenceEquals(loading[b.ToVector2s()][0], other) && grass.Count == 2 && grass[1].Centre == b.Centre, "the other holder stays; the grass is cut for this zone");

      // The timeout: a zone waits 10 s, says so once, lets go, tells the manager it gave up, and its grass is cut.
      var slot = BakedClient.SlotOf(c);
      BakedZoneHold.Take(c);
      clock = 106f;
      BakedZoneHold.Take(d);
      clock = 109.9f;
      BakedZoneHold.Expire();
      C(BakedZoneHold.Count == 2 && grass.Count == 2 && !slot.HoldGaveUp, "9.9 s on, nothing has timed out");
      clock = 110.1f;
      var lines = LogHandler.During(BakedZoneHold.Expire);
      C(!BakedZoneHold.Holding(c) && BakedZoneHold.Holding(d) && BakedZoneHold.Count == 1 && slot.HoldGaveUp, "10.1 s on, the older hold has timed out and the younger has not; the manager is told");
      C(grass.Count == 3 && grass[2].Centre == c.Centre && lines.Count(l => l.Contains("not built after 10 s")) == 1, "its grass is cut and it is said once: " + string.Join(" | ", lines));
      BakedZoneHold.Expire();
      C(grass.Count == 3, "and not again");

      // ReleaseAll: a world ends or the layer is replaced.
      BakedZoneHold.ReleaseAll();
      C(BakedZoneHold.Count == 0 && loading.ContainsKey(b.ToVector2s()) && !loading.ContainsKey(d.ToVector2s()) && grass.Count == 4 && grass[3].Centre == d.Centre, "ReleaseAll lets every zone go, grass included, and leaves other holders");

      // No world: nothing to hold, and a hold that outlives its world cuts no grass.
      BakedZoneHold.GameZones = () => null;
      BakedZoneHold.Take(a);
      C(BakedZoneHold.Count == 0, "with no zone system nothing is held");
      BakedZoneHold.GameZones = () => system;
      BakedZoneHold.Take(a);
      BakedZoneHold.GameZones = () => null;
      int before = grass.Count;
      BakedZoneHold.Release(a);
      C(BakedZoneHold.Count == 0 && grass.Count == before, "a hold released after its world went is dropped, with no grass to cut");
    }
    finally
    {
      (BakedZoneHold.GameZones, BakedZoneHold.NewZdo, BakedZoneHold.ResetGrass, BakedClient.Now) = seams;
      BakedClient.OnLayerChanged(null, null, null);
    }
  }

  // ------------------------------------------------------------------------------------------------ which layer the manager works for

  // The manager as the offline tests can run it: it is active whenever there is a layer and a world (a scene), and its clock is the test's.
  private sealed class ClientLayerRig : IDisposable
  {
    private readonly (Func<bool>, Func<float>) seams = (BakedClient.Active, BakedClient.Now);
    private readonly BC.BetterContinentsSettings settings = BC.Settings;
    public float Clock = 50f;

    public ClientLayerRig()
    {
      BakedClient.Active = () => BakedLayerStore.Current != null && ZNetScene.instance != null;
      BakedClient.Now = () => Clock;
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
      BakedClient.OnLayerChanged(null, null, null);
    }

    // The settings swapped the way ZNetPatch swaps them: a plain assignment, no event.
    public void Swap(BakedLayer layer) => BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };

    public void Dispose()
    {
      BakedClient.OnLayerChanged(null, null, null);
      (BakedClient.Active, BakedClient.Now) = seams;
      BC.Settings = settings;
      BakedTransfer.ClientReports = false;
    }
  }

  private static List<ZoneKey> ClientLayerDirty() => BakedClient.SlotsNow.Values.Where(s => s.Dirty).Select(s => s.Key).OrderBy(k => k).ToList();

  private static void ClientLayerTrackingTest()
  {
    Section("client: the manager follows BakedLayerStore.Current, whatever raised no event: a swap of the settings, a layer cleared, a world over, a new session");
    var a = MakeSample(61).Layer;
    var b = MakeSample(62).Layer;
    var c = MakeSample(63).Layer;
    C(a.Id != b.Id && b.Id != c.Id, "(three layers that differ)");
    using var rig = new ClientLayerRig();
    BakedLayerStore.Changed += BakedClient.OnLayerChanged;
    try
    {
      // No world, a layer in the settings: nothing is up to draw it.
      rig.Swap(a);
      C(!BakedClient.Prepare() && BakedClient.CurrentLayer == null && !BakedTransfer.ClientReports, "in the menu, with a layer in the settings, the manager has none and does not report");

      // A world: the manager takes the settings' layer, and from then on answers for pushes.
      var zoneA = new ZoneKey(1, 1);
      var zoneB = new ZoneKey(2, 1);
      using (var world = new ApiScene())
      {
        C(BakedClient.Prepare() && ReferenceEquals(BakedClient.CurrentLayer, a) && BakedTransfer.ClientReports, "in a world it adopts the layer the settings hold, and reports Applied for pushes from now");
        BakedClient.SlotOf(zoneA).Dirty = false;
        BakedClient.SlotOf(zoneB).Dirty = false;

        // The settings are swapped for another layer without an event (ZNetPatch does this in four places): it is the next frame's layer.
        rig.Swap(b);
        C(ReferenceEquals(BakedClient.CurrentLayer, a), "(a swap with no event changes nothing by itself)");
        C(BakedClient.Prepare() && ReferenceEquals(BakedClient.CurrentLayer, b) && ClientLayerDirty().SequenceEqual([zoneA, zoneB]), "the next frame has the new layer, and every zone is built again");

        // A Set raises the event: only the zones it names are built again.
        BakedClient.SlotOf(zoneA).Dirty = false;
        BakedClient.SlotOf(zoneB).Dirty = false;
        BakedLayerStore.Set(c, [zoneB]);
        C(ReferenceEquals(BakedClient.CurrentLayer, c) && ClientLayerDirty().SequenceEqual([zoneB]), "a pushed layer is adopted at once, and only the zone it names is built again");
        BakedClient.SlotOf(zoneB).Dirty = false;
        BakedLayerStore.Set(a, null);
        C(ReferenceEquals(BakedClient.CurrentLayer, a) && ClientLayerDirty().SequenceEqual([zoneA, zoneB]), "a layer with no list of zones (a first one, a reset) builds them all again");

        // The layer is cleared (FejdStartup, a client's session start): it goes at once, before any frame, though no world is "active" without it.
        BakedLayerStore.Set(null, null);
        C(BakedClient.CurrentLayer == null && BakedClient.SlotsNow.Count == 0 && !BakedTransfer.ClientReports, "Set(null, null) tears the manager down at once: no layer, no zone, no reporting");
        C(!BakedClient.Prepare() && BakedClient.CurrentLayer == null, "and the next frame has nothing to do");
        var lines = new List<string>();
        BakedClient.Stats(lines.Add);
        C(lines.Count == 1 && lines[0].Contains("no layer"), "bc_bake stats says this world has none: " + string.Join(" | ", lines));
      }

      // The settings are swapped for no layer without an event while the manager has one (a session starts for a world without a layer).
      using (var world = new ApiScene())
      {
        rig.Swap(a);
        BakedClient.Prepare();
        BakedClient.SlotOf(zoneA);
        C(ReferenceEquals(BakedClient.CurrentLayer, a) && BakedClient.SlotsNow.Count == 1, "(the manager has a layer and a zone)");
        rig.Swap(null);
        C(!BakedClient.Prepare() && BakedClient.CurrentLayer == null && BakedClient.SlotsNow.Count == 0 && !BakedTransfer.ClientReports, "a swap to no layer is found by the next frame, and everything of the layer goes");
      }

      // The world ends while the settings still hold its layer (the menu after a hosted game): the manager lets go.
      rig.Swap(a);
      using (var world = new ApiScene())
      {
        BakedClient.Prepare();
        BakedClient.SlotOf(zoneB);
        C(ReferenceEquals(BakedClient.CurrentLayer, a), "(a world with the layer)");
      }
      C(!BakedClient.Prepare() && BakedClient.CurrentLayer == null && BakedClient.SlotsNow.Count == 0, "the world is gone, the settings still hold its layer: the manager has let go of it");

      // Another world with the same layer: nothing of the first is kept (a new scene is a new world).
      using (var first = new ApiScene())
      {
        BakedClient.Prepare();
        BakedClient.SlotOf(zoneA).Dirty = false;
        using (var second = new ApiScene())
        {
          BakedClient.Prepare();
          C(ReferenceEquals(BakedClient.CurrentLayer, a) && BakedClient.SlotsNow.Count == 0, "a new scene while the old one was never torn down: the old world's zones are gone, the layer is adopted afresh");
        }
      }
      BakedClient.OnLayerChanged(null, null, null);

      // A client leaves one server and joins another: the session start clears the store (an event), the settings are swapped (none), the new
      // server's layer is downloaded and set before its world exists (an event the manager cannot use yet), and then the world comes.
      using (var old = new ApiScene())
      {
        rig.Swap(a);
        BakedClient.Prepare();
        BakedClient.SlotOf(zoneA);
      }
      BakedTransfer.SessionStarts(false);
      BakedLayerStore.Changed -= BakedGround.OnLayerChanged;
      C(BakedClient.CurrentLayer == null && BakedClient.SlotsNow.Count == 0, "a client's session start clears the layer, and the manager with it");
      BC.Settings = new BC.BetterContinentsSettings();
      BakedLayerStore.Set(b, null);
      C(BakedClient.CurrentLayer == null && !BakedTransfer.ClientReports, "the new server's layer arrives before its world: the manager does not take it yet");
      using (var world = new ApiScene())
      {
        C(BakedClient.Prepare() && ReferenceEquals(BakedClient.CurrentLayer, b) && BakedClient.SlotsNow.Count == 0, "when the world is up it takes the new server's layer, not the old one");
        var lines = new List<string>();
        BakedClient.Stats(lines.Add);
        C(lines[0].Contains($"layer r{b.Revision}") && lines[0].Contains($"{b.Placements:N0} records"), "bc_bake stats names that layer: " + lines[0]);
      }
    }
    finally
    {
      BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
      BakedLayerStore.Changed -= BakedGround.OnLayerChanged;
    }
  }

  // ------------------------------------------------------------------------------------------------ Applied, after every change

  private static void ClientLayerAppliedTest()
  {
    Section("client: the manager answers Applied for every layer it is given, the first of a running world included");
    var first = MakeSample(71).Layer;
    var edit = first.Edit();
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3.5, 40, 4.5, 0)]);
    var (second, changed) = edit.Build();
    C(second.Revision == first.Revision + 1, "(the second layer is the next revision)");
    using var world = new XferWorld();
    var rig = new ClientLayerRig();
    BakedLayerStore.Changed += BakedClient.OnLayerChanged;
    try
    {
      using var scene = new ApiScene();
      BakedClient.Active = () => BakedLayerStore.Current != null && ZNetScene.instance != null;
      BakedClient.Now = () => world.Time;
      // A server pushes the first layer there is to a client that is in its world and has none: the whole layer, outside a join.
      var pair = world.Joined("pushed", null, scripted: false);
      BakedPush(world, pair, first);
      C(world.Until(() => BakedLayerStore.Current != null && BakedLayerStore.Current.Id == first.Id, 30f), "the first layer arrives and is current");
      for (int n = 0; n < 10; n++)
        world.Tick();
      C(ReferenceEquals(BakedClient.CurrentLayer, BakedLayerStore.Current) && BakedTransfer.ClientReports, "the manager took it, and from then on it answers for pushes");
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 0, "BakedTransfer left the answer to it: nothing was sent at once");
      // The manager's frame: the rings are made, the first layer's zones are taken in, and the colliders stand (none are wanted here).
      BakedClient.FillApplying(world.Time);
      BakedClient.CheckApplied(world.Time);
      world.Tick();
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == first.Revision, "and the server hears Applied for the first layer, once");
      BakedClient.CheckApplied(world.Time + 1f);
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1, "not twice");

      // The next revision: a ring zone that is built again holds the answer back until its colliders stand, or 10 s.
      var zone = new ZoneKey(0, 0);
      var slot = BakedClient.SlotOf(zone);
      slot.InCollider = true;
      slot.Dirty = false;
      pair.Client.Pending = second;
      var sent = new BakedTransfer.SendResult();
      world.Run(BakedTransfer.SendLayer(pair.Client, second, kind: 1, ClientLayerPatch(first, second, changed), ClientLayerPatch(first, second, changed).Length, fromId: first.Id, changed, joining: false, sent));
      C(world.Until(() => BakedLayerStore.Current.Id == second.Id, 30f), "the next revision arrives (a patch)");
      for (int n = 0; n < 5; n++)
        world.Tick();
      C(slot.Dirty && pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1, "the zone it changed is built again (dirty), and nothing is answered yet");
      float now = world.Time;
      BakedClient.CheckApplied(now + 5f);
      world.Tick();
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1, "5 s on, the colliders still do not stand: no answer");
      BakedClient.CheckApplied(now + 10.5f);
      world.Tick();
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 2 && pair.Client.AppliedRevision == second.Revision, "after 10 s it answers all the same, once");

      // A layer that goes cancels what was waiting: the server's own wait times out, and nothing is answered for a world that is over.
      BakedLayerStore.Set(null, null);
      BakedClient.CheckApplied(now + 30f);
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 2 && !BakedTransfer.ClientReports, "a manager torn down answers nothing more, and does not claim to report");
    }
    finally
    {
      BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
      rig.Dispose();
    }
  }

  // What Install does when the manager did not take the layer on, whatever ClientReports says (it must not depend on a flag the manager sets when it
  // runs, not when it takes a layer).
  private static void ClientLayerClaimTest()
  {
    Section("client: a pushed layer the manager did not take on is answered Applied at once, whatever ClientReports says; one it took on waits for it");
    var first = MakeSample(81).Layer;
    var other = MakeSample(82).Layer;
    C(first.Revision == other.Revision && first.Id != other.Id, "(two layers of one revision)");

    // A manager that is up (the flag is set) but cannot take the layer on: a world that just ended, an error in its Adopt.
    using (var world = new XferWorld())
    {
      var rig = new ClientLayerRig();
      BakedLayerStore.Changed += BakedClient.OnLayerChanged;
      try
      {
        BakedTransfer.ClientReports = true;
        BakedClient.Active = () => false;
        var pair = world.Joined("stale", null, scripted: false);
        BakedPush(world, pair, first);
        C(world.Until(() => BakedLayerStore.Current != null && BakedLayerStore.Current.Id == first.Id, 30f), "the layer is current");
        C(world.Until(() => pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == first.Revision, 10f)
          && BakedTransfer.ClaimedRevision == 0, "the manager claimed nothing, so the client said Applied itself, once");
      }
      finally
      {
        BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
        rig.Dispose();
      }
    }

    // A claim of an earlier layer with the same revision number is not this layer's.
    using (var world = new XferWorld())
    {
      BakedTransfer.Claim(other.Revision);
      var pair = world.Joined("old claim", null, scripted: false);
      BakedPush(world, pair, other);
      C(world.Until(() => pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == other.Revision, 30f), "a claim left by an earlier layer of that revision does not keep the answer back");
    }

    // A manager that takes it on claims the revision in Adopt, and the answer is its own.
    using (var world = new XferWorld())
    {
      var rig = new ClientLayerRig();
      BakedLayerStore.Changed += BakedClient.OnLayerChanged;
      try
      {
        using var scene = new ApiScene();
        BakedClient.Now = () => world.Time;
        var pair = world.Joined("taken", null, scripted: false);
        BakedPush(world, pair, first);
        C(world.Until(() => BakedLayerStore.Current != null && BakedLayerStore.Current.Id == first.Id, 30f), "(the layer is current)");
        for (int n = 0; n < 10; n++)
          world.Tick();
        C(BakedTransfer.ClaimedRevision == first.Revision && pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 0, "the manager's Adopt claimed the revision, and nothing was sent at once");
        BakedClient.FillApplying(world.Time);
        BakedClient.CheckApplied(world.Time);
        world.Tick();
        world.Tick();
        C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == first.Revision, "and the manager's own report is the one answer");
      }
      finally
      {
        BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
        rig.Dispose();
      }
    }
  }

  private static byte[] ClientLayerPatch(BakedLayer from, BakedLayer to, ZoneKey[] changed) => BakedPatch.Make(from, to, changed);

  // The order of a frame: the rings, then the first layer's zones taken in, then the checks of Applied (read from Step's IL).
  private static void ClientLayerFrameOrderTest()
  {
    Section("client: in a frame the rings are made before the first layer's zones are taken in, and that before Applied is checked");
    var step = typeof(BakedClient).GetMethod("Step", ClientLayerAny)!;
    var instructions = PatchProcessor.GetOriginalInstructions(step);
    int Call(string name)
    {
      for (int i = 0; i < instructions.Count; i++)
        if ((instructions[i].opcode == OpCodes.Call || instructions[i].opcode == OpCodes.Callvirt) && instructions[i].operand is MethodBase m && m.Name == name && m.DeclaringType == typeof(BakedClient))
          return i;
      return -1;
    }
    int prepare = Call("Prepare"), rings = Call("Rings"), fill = Call("FillApplying"), expire = -1, check = Call("CheckApplied");
    for (int i = 0; i < instructions.Count; i++)
      if (instructions[i].operand is MethodBase m && m.Name == "Expire" && m.DeclaringType == typeof(BakedZoneHold))
        expire = i;
    C(prepare >= 0 && rings > prepare && fill > rings && check > fill && expire > fill, $"Prepare ({prepare}), Rings ({rings}), FillApplying ({fill}), BakedZoneHold.Expire ({expire}), CheckApplied ({check}), in that order");
  }
}

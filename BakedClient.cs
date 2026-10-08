// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownChunk.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// THE CLIENT SIDE OF THE LAYER (section 9, U1, U8): one manager that draws a world's baked placements and stands their colliders up. It
// replaces VALtima's chunk object per zone (TownChunk) with the layer's own zone index: no network object, no MonoBehaviour per zone.
//
// Rings (9.1). The draw ring is the zones with records within Draw Distance of the camera's zone; the collider ring is the zones within the
// game's near distance of its reference position (5 x 5 by default, the zones it loads terrain for). A zone's built data is freed 4 s after
// it leaves the draw ring, its colliders 4 s after it leaves the collider ring.
//
// Building (9.2). At most 4 zones at once, collider-ring zones first, nearest first, then the draw ring by distance: inflate and decode on a
// worker, resolve the zone's palette entries on the main thread (once each per session), build on a worker; then, at most 2 finished zones a
// frame get their Unity objects, and their colliders are cooked off the main thread.
//
// The hold (9.3) keeps a zone from being created before its floors exist (BakedZoneHold). The hand-over (9.7) keeps a rebuilt zone's old drawing
// and colliders until the pieces that replace its records have instances.

/// <summary>Where the client side reads its settings (section 11.2): bound by SettingsSchema, null where the config is not (the offline suite).</summary>
internal static class BakedSettings
{
  internal static ConfigEntry<int>? DrawDistance, LightCount, LightShadows;
  internal static ConfigEntry<float>? DrawScale, DetailScale, LightDistance, SeatDistance;
  internal static ConfigEntry<BakedShadows>? Shadows;
  internal static ConfigEntry<string>? Tints;
  internal static ConfigEntry<bool>? Diagnostics;
}

/// <summary>One zone of the layer on this client: what it was built from, what is drawn and stands for it, and the build in flight.</summary>
internal sealed class ZoneSlot(ZoneKey key)
{
  internal readonly ZoneKey Key = key;
  /// <summary>The build that is drawn now, and its Unity objects (the colliders, and the lit copies and seats).</summary>
  internal BuiltZone? Current;
  internal ZoneObjects? Objects;
  /// <summary>A rebuilt zone, finished and waiting for its hand-over; its objects are made (inactive) while it waits.</summary>
  internal BuiltZone? Next;
  internal ZoneObjects? NextObjects;
  internal float NextSince, NextPolledAt;
  internal BuildJob? Job;
  internal bool InDraw, InCollider;
  /// <summary>When the zone left each ring (seconds; -1 while inside).</summary>
  internal float LeftDraw = -1f, LeftCollider = -1f;
  /// <summary>Someone asked for this zone's colliders until this time (IsAreaReady, a teleport).</summary>
  internal float DemandUntil;
  /// <summary>The layer changed under it: it is built again.</summary>
  internal bool Dirty;
  /// <summary>The revision of the layer whose build of this zone failed (0: none), and whether its hold timed out.</summary>
  internal uint FailedAt;
  internal bool HoldGaveUp;
  internal bool Disposed;

  internal bool WantsColliders(float now) => InCollider || now < DemandUntil;
}

/// <summary>A zone's build: decode (worker), resolve (main thread), build (worker), then its result waits for integration.</summary>
internal sealed class BuildJob
{
  internal ZoneSlot Slot = null!;
  internal BakedLayer Layer = null!;
  internal ZoneRow Row;
  internal uint Revision;
  internal BakedKind?[] Kinds = [];
  internal ZoneData? Data;
  internal int[] Used = [];
  internal BuiltZone? Result;
  internal Exception? Error;
  /// <summary>0 decoding, 1 decoded (waiting for the main thread to resolve its palette entries), 2 building, 3 built, 4 failed.</summary>
  internal volatile int Stage;
  internal bool Counted;
  internal float Started;
}

internal static class BakedClient
{
  internal const int MaxBuilds = 4, MaxIntegrationsPerFrame = 2;
  /// <summary>How long (seconds) a zone's data and colliders stay after it leaves its ring.</summary>
  internal const float Linger = 4f;
  /// <summary>How long a rebuilt zone waits for its replacement pieces, and a changed zone's collider for the Applied report.</summary>
  internal const float HandoverCap = 10f, AppliedCap = 10f;
  private const float ResolveBudgetMs = 4f;

  // The game's side of the manager, as fields for the offline tests to stand in for: the clock, and whether this machine has a world, a layer and a
  // screen to draw on (a headless server, a machine with no graphics, the menu and the loading screen have none).
  internal static Func<float> Now = () => Time.realtimeSinceStartup;
  internal static Func<bool> Active = () =>
    BakedLayerStore.Current != null && ZNetScene.instance != null && ZoneSystem.instance != null && ZNet.instance != null && !ZNet.instance.IsDedicated()
    && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

  private static BakedClientHost? host;
  private static bool subscribed;
  private static BakedLayer? layer;
  private static BakedKind?[] kinds = [];
  private static object? scene;
  private static readonly Dictionary<ZoneKey, ZoneSlot> Slots = new();
  private static readonly List<BuildJob> Jobs = [];
  private static readonly List<ZoneSlot> Scratch = [];
  private static int building;
  private static ZoneKey camZone = new(int.MinValue, int.MinValue), refZone = new(int.MinValue, int.MinValue);
  private static bool ringsDirty = true, drawDirty;
  private static BuiltZone[] ring = [];
  private static int ringVersion;
  private static int integratedThisFrame;
  private static readonly HashSet<string> Reported = [];
  private static readonly List<ZDO> ZdoScratch = [];
  private static readonly List<BakedCopies.Props> PropsNow = [];

  // ---- what the offline tests look at ----------------------------------------------------------------------------------------------

  /// <summary>The layer this manager works for now, and its zones' slots (for the offline tests).</summary>
  internal static BakedLayer? CurrentLayer => layer;
  internal static IReadOnlyDictionary<ZoneKey, ZoneSlot> SlotsNow => Slots;
  internal static ZoneSlot SlotOf(ZoneKey key) => Slot(key);

  // ---- what the rest of the mod calls ---------------------------------------------------------------------------------------------

  /// <summary>
  /// Whether the zone's collider is in place on this client: true when it has no records here, when it is outside this client's collider ring
  /// (colliders are only built inside it), or when its build of the current layer has its colliders assigned.
  /// </summary>
  public static bool ColliderReady(Vector2s zone)
  {
    var key = ZoneKey.Of(zone);
    var current = layer;
    if (current == null || !current.TryGetZoneRow(key, out var row) || row.Placements == 0)
      return true;
    if (!Slots.TryGetValue(key, out var slot) || !slot.WantsColliders(Now()))
      return true;
    return slot.Dirty ? false : SlotReady(slot);
  }

  /// <summary>
  /// The changed zones inside this client's collider ring have their new colliders in place (section 9.7, 10.7 step 4-5): tells the server.
  /// BakedTransfer sends the Applied call on a client, and counts it toward the server's own wait on the machine that runs the world.
  /// </summary>
  public static void ReportApplied(int revision) => BakedTransfer.ReportApplied(revision);

  /// <summary>bc_bake stats: what is in view now, per kind (the lines go to <paramref name="say"/> one at a time).</summary>
  public static void Stats(Action<string> say)
  {
    if (layer == null)
    {
      say(BakedLayerStore.Current == null ? "Baked placements: this world has no layer." : "Baked placements: this machine draws nothing (it has no screen, or no world is up yet).");
      return;
    }
    int drawn = 0, withColliders = 0, waiting = 0;
    foreach (var s in Slots.Values)
    {
      if (s.Current != null)
        drawn++;
      if (s.Objects is { CollidersAssigned: true })
        withColliders++;
      if (s.Job != null || s.Next != null)
        waiting++;
    }
    say($"Baked placements: layer r{layer.Revision}, {layer.Placements:N0} records in {layer.Zones.Count:N0} zones. This client: {Slots.Count} zones " +
        $"({drawn} built, {withColliders} with colliders standing, {waiting} being built or handed over), {BakedZoneHold.Count} held, " +
        $"{BakedPaint.ZonesPainted} painted, {BakedKinds.All.Count} kinds drawn.");
    int consumables = 0;
    foreach (var s in Slots.Values)
      consumables += s.Current?.Consumables ?? 0;
    if (consumables > 0 || BakedConsumables.Kinds.Count > 0)
      say($"Baked placements: {BakedConsumables.Kinds.Count} consumable kinds ({string.Join(", ", BakedConsumables.Kinds.Take(8))}{(BakedConsumables.Kinds.Count > 8 ? ", ..." : "")}) " +
          $"and {consumables:N0} of their records in the zones built here are placed as real objects, never drawn.");
    int decor = 0;
    foreach (var s in Slots.Values)
      decor += s.Current?.DecorRecords ?? 0;
    if (decor > 0 || BakedConsumables.DecorKinds.Count > 0)
      say($"Baked placements: {BakedConsumables.DecorKinds.Count} decor kinds ({string.Join(", ", BakedConsumables.DecorKinds.Take(8))}{(BakedConsumables.DecorKinds.Count > 8 ? ", ..." : "")}) " +
          $"and {decor:N0} of their records in the zones built here are drawn as scenery, not placed as objects and not pickable.");
    int copyRecords = 0, lit = 0;
    foreach (var s in Slots.Values)
    {
      copyRecords += s.Current?.Copies.Length ?? 0;
      lit += s.Objects?.Props?.LitCount ?? 0;
    }
    if (copyRecords > 0)
      say(BakedLightCount.StatsLine(copyRecords, lit, BakedSettings.LightCount?.Value ?? BakedLightCount.Default, BakedSettings.LightDistance?.Value ?? 90f, BakedDraw.Hidden));
    BakedDraw.Report(say, ring.Length);
  }

  /// <summary>bc_bake hide: this client stops drawing the layer's pieces (and its lit copies); the colliders and the seats stay.</summary>
  public static void Hide()
  {
    BakedDraw.Hidden = true;
  }

  /// <summary>bc_bake show: this client draws the layer again.</summary>
  public static void Show()
  {
    BakedDraw.Hidden = false;
    drawDirty = true;
  }

  // ---- starting ------------------------------------------------------------------------------------------------------------------

  // The first world of the session: the manager's host and its listeners (ZNetScene.Awake, once per world; a headless server has none).
  internal static void Attach()
  {
    if (host != null)
      return;
    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || (ZNet.instance != null && ZNet.instance.IsDedicated()))
      return;
    var go = new GameObject("BC baked placements");
    UnityEngine.Object.DontDestroyOnLoad(go);
    host = go.AddComponent<BakedClientHost>();
    Subscribe();
  }

  private static void Subscribe()
  {
    if (subscribed)
      return;
    subscribed = true;
    BakedLayerStore.Changed += OnLayerChanged;
    if (BakedSettings.DrawDistance != null)
      BakedSettings.DrawDistance.SettingChanged += (_, _) => ringsDirty = true;
    if (BakedSettings.Tints != null)
      BakedSettings.Tints.SettingChanged += (_, _) => TintsChanged();
  }

  // Tints are read when a kind is resolved: the zones are built again with the new colours.
  private static void TintsChanged()
  {
    BakedKinds.Forget();
    Array.Clear(kinds, 0, kinds.Length);
    foreach (var s in Slots.Values)
      s.Dirty = true;
  }

  // ---- the layer -------------------------------------------------------------------------------------------------------------------

  // A layer or a patch is installed (BakedLayerStore.Changed, on the main thread; zones null: all of them). Where this client is not drawing
  // anything (no world up, or a machine with no screen) BakedTransfer answers Applied itself: the manager has claimed no revision (Adopt does).
  internal static void OnLayerChanged(BakedLayer? old, BakedLayer? next, ZoneKey[]? changed)
  {
    try
    {
      // The layer goes (a world is left, a session starts, a layer is dropped): all of it goes with it now, also where there is nothing up to
      // draw it (the menu, a loading screen). The next frame would find it out in Prepare, but nothing waits for a frame.
      if (next == null)
      {
        if (scene != null || layer != null || Slots.Count > 0)
          Teardown();
        return;
      }
      if (!Active())
        return;
      // the manager is not running in this world yet: this is its first layer here (Prepare adopts it, and Adopt queues the report of Applied
      // for it: a push that made it is waited for)
      if (!ReferenceEquals(scene, ZNetScene.instance) || layer == null)
      {
        Prepare();
        return;
      }
      Adopt(next, changed);
    }
    catch (Exception e)
    {
      LogOnce("layer change", e);
    }
  }

  private static void Adopt(BakedLayer? next, ZoneKey[]? changed)
  {
    var old = layer;
    layer = next;
    if (next == null)
    {
      Teardown();
      return;
    }
    kinds = new BakedKind?[next.Palette.Count];
    if (old == null || changed == null)
    {
      foreach (var s in Slots.Values)
        s.Dirty = true;
    }
    else
      foreach (var zone in changed)
        if (Slots.TryGetValue(zone, out var s))
          s.Dirty = true;
    foreach (var s in Slots.Values)
      if (s.FailedAt != next.Revision)
        s.FailedAt = 0;
    BakedPaint.Changed(next, changed);
    ringsDirty = true;
    // the changed zones inside the collider ring must have their new colliders before the server hears Applied
    var wait = new HashSet<ZoneKey>();
    float now = Now();
    foreach (var s in Slots.Values)
      if (s.WantsColliders(now) && (changed == null || Array.IndexOf(changed, s.Key) >= 0))
        wait.Add(s.Key);
    // After EVERY change, the first layer of a world too: BakedTransfer.Install leaves the answer to this manager when it has claimed the
    // revision (below), and the machine that runs the world waits for it (Push). The zones of a first layer are not known until the rings are
    // made, a moment from now (FillApplying).
    Pending.Add(new Applying(next.Revision, wait, now) { Fill = old == null });
    // BakedTransfer.Install asks, right after the layer is current: this manager will say Applied for it, nothing else need.
    BakedTransfer.Claim(next.Revision);
  }

  private sealed class Applying(uint revision, HashSet<ZoneKey> zones, float since)
  {
    internal readonly uint Revision = revision;
    internal readonly HashSet<ZoneKey> Zones = zones;
    internal readonly float Since = since;
    /// <summary>The zones are taken in when the rings have been made (the first layer of a world).</summary>
    internal bool Fill;
  }

  private static readonly List<Applying> Pending = [];

  // The first layer of a world is waited for in the zones of the collider ring, once the rings are made.
  internal static void FillApplying(float now)
  {
    foreach (var p in Pending)
    {
      if (!p.Fill)
        continue;
      p.Fill = false;
      foreach (var s in Slots.Values)
        if (s.WantsColliders(now))
          p.Zones.Add(s.Key);
    }
  }

  // every changed zone inside the collider ring has its new build with its colliders standing, or has left the ring, or 10 s have passed
  internal static void CheckApplied(float now)
  {
    for (int i = 0; i < Pending.Count; i++)
    {
      var p = Pending[i];
      p.Zones.RemoveWhere(z => Applied(z, p.Revision, now));
      if (p.Zones.Count > 0 && now - p.Since < AppliedCap)
        continue;
      Pending.RemoveAt(i--);
      ReportApplied((int)p.Revision);
    }
  }

  private static bool Applied(ZoneKey zone, uint revision, float now)
  {
    if (!Slots.TryGetValue(zone, out var s) || s.Disposed || !s.WantsColliders(now))
      return true;
    if (s.Next != null || s.Dirty || s.Job != null)
      return false;
    if (s.Current == null)
      return layer != null && (!layer.TryGetZoneRow(zone, out var row) || row.Placements == 0);
    return s.Current.Revision >= revision && SlotReady(s);
  }

  // ---- the frame -------------------------------------------------------------------------------------------------------------------

  // LateUpdate of the host
  internal static void Frame()
  {
    try
    {
      Step();
    }
    catch (Exception e)
    {
      LogOnce("frame", e);
    }
  }

  // The world and the layer this manager works for: starts it in a new world, takes in a layer swapped without a Changed event, and lets go of
  // everything when there is no world or no layer. False when there is nothing to do.
  internal static bool Prepare()
  {
    var current = BakedLayerStore.Current;
    if (current == null || !Active())
    {
      if (scene != null || layer != null || Slots.Count > 0)
        Teardown();
      return false;
    }
    // a new world (a new ZNetScene): nothing of the last one stays
    if (!ReferenceEquals(scene, ZNetScene.instance))
    {
      if (scene != null)
        Teardown();
      scene = ZNetScene.instance;
      layer = null;
    }
    if (!ReferenceEquals(layer, current))
      Adopt(current, null);
    // from here this client will say when a push's colliders stand (BakedTransfer would answer for it at once otherwise)
    BakedTransfer.ClientReports = layer != null;
    return layer != null;
  }

  private static void Step()
  {
    if (!Prepare())
      return;
    var cam = Utils.GetMainCamera();
    if (cam == null)
      return;
    float now = Now();
    integratedThisFrame = 0;

    var cz = ZoneKey.OfPoint(cam.transform.position);
    var rz = ZoneKey.OfPoint(ZNet.instance.GetReferencePosition());
    if (ringsDirty || cz != camZone || rz != refZone)
    {
      camZone = cz;
      refZone = rz;
      ringsDirty = false;
      Rings(now);
    }
    FillApplying(now);

    BakedZoneHold.Expire();
    Advance(now);
    foreach (var s in Slots.Values)
    {
      Work(s, now);
      SyncHold(s, now);
    }
    Free(now);
    if (drawDirty)
      RebuildRing();
    CheckApplied(now);
    BakedPaint.Tick(layer, refZone);

    // lit copies and seats: of the Copy records within Light Distance the Light Count nearest burn (one ranking across the zones, a few times a
    // second); every other Copy record is drawn unlit by the instancing, which skips the ones whose lit copy stands
    Vector3 eye = cam.transform.position;
    float light = BakedDraw.Hidden ? 0f : BakedSettings.LightDistance?.Value ?? 90f;
    int lights = BakedDraw.Hidden ? 0 : BakedSettings.LightCount?.Value ?? BakedLightCount.Default;
    float seat = BakedSettings.SeatDistance?.Value ?? 12f;
    Vector3 at = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : eye;
    PropsNow.Clear();
    foreach (var s in Slots.Values)
    {
      var props = s.Objects?.Props;
      if (props == null || s.Objects?.Root == null)
        continue;
      PropsNow.Add(props);
      props.UpdateSeats(at, seat);
    }
    BakedLightCount.Update(PropsNow, eye, light, lights, Time.unscaledTime);
    BakedLightCount.MakePending();
    foreach (var props in PropsNow)
      props.UpdateLit();
    // only the Light Shadows nearest lit copies keep their lights' shadows (one ranking across the zones, a few times a second)
    BakedLightShadows.Update(eye, BakedSettings.LightShadows?.Value ?? 4, Time.unscaledTime);

    BakedDraw.Tick(cam, ring, ringVersion);
    BakedDraw.Submit();
  }

  // ---- rings ------------------------------------------------------------------------------------------------------------------------

  private static void Rings(float now)
  {
    var l = layer!;
    int radius = Math.Max(1, Math.Min(8, BakedSettings.DrawDistance?.Value ?? 4));
    var wantDraw = new HashSet<ZoneKey>();
    for (int dz = -radius; dz <= radius; dz++)
      for (int dx = -radius; dx <= radius; dx++)
      {
        var key = new ZoneKey(camZone.X + dx, camZone.Z + dz);
        if (l.TryGetZoneRow(key, out var row) && row.Placements > 0)
          wantDraw.Add(key);
      }
    var wantCollider = new HashSet<ZoneKey>();
    var system = ZoneSystem.instance;
    int near = system.m_simulationDistance.NearSimulationDistance;
    bool classic = system.m_simulationDistance.IsClassic;
    var rz = refZone.ToVector2s();
    for (int dz = -near; dz <= near; dz++)
      for (int dx = -near; dx <= near; dx++)
      {
        var key = new ZoneKey(refZone.X + dx, refZone.Z + dz);
        if (!classic && !system.ZonesWithinRadius(rz, key.ToVector2s(), near))
          continue;
        if (l.TryGetZoneRow(key, out var row) && row.Placements > 0)
          wantCollider.Add(key);
      }
    foreach (var key in wantDraw)
      Slot(key);
    foreach (var key in wantCollider)
      Slot(key);
    bool changed = false;
    foreach (var s in Slots.Values)
    {
      bool draw = wantDraw.Contains(s.Key), collide = wantCollider.Contains(s.Key);
      if (draw != s.InDraw)
      {
        s.InDraw = draw;
        s.LeftDraw = draw ? -1f : now;
        changed = true;
      }
      if (collide != s.InCollider)
      {
        s.InCollider = collide;
        s.LeftCollider = collide ? -1f : now;
      }
    }
    if (changed)
      drawDirty = true;
  }

  private static ZoneSlot Slot(ZoneKey key)
  {
    if (!Slots.TryGetValue(key, out var s))
    {
      float now = Now();
      Slots[key] = s = new ZoneSlot(key) { LeftDraw = now, LeftCollider = now };
    }
    return s;
  }

  private static void RebuildRing()
  {
    drawDirty = false;
    var list = new List<BuiltZone>();
    foreach (var s in Slots.Values)
      // a zone that waits for its hand-over keeps its old drawing, even where its records are gone (9.7)
      if ((s.InDraw || s.Next != null) && s.Current != null && s.Current.Drawn > 0)
        list.Add(s.Current);
    ring = list.ToArray();
    ringVersion++;
  }

  // ---- the builds ---------------------------------------------------------------------------------------------------------------------

  private static void Advance(float now)
  {
    // start what is wanted, best first, while there are free places
    while (building < MaxBuilds)
    {
      var best = PickToBuild(now);
      if (best == null)
        break;
      Start(best, now);
    }
    var clock = Stopwatch.StartNew();
    for (int i = 0; i < Jobs.Count; i++)
    {
      var job = Jobs[i];
      if (job.Stage == 1)
        Resolve(job, clock);
      if (job.Stage >= 3 && integratedThisFrame < MaxIntegrationsPerFrame)
      {
        Jobs.RemoveAt(i--);
        integratedThisFrame++;
        Integrate(job, now);
      }
    }
  }

  // collider-ring zones first, nearest to the player first; then the draw ring, nearest to the camera first
  private static ZoneSlot? PickToBuild(float now)
  {
    ZoneSlot? best = null;
    long bestKey = long.MaxValue;
    foreach (var s in Slots.Values)
    {
      if (s.Job != null || s.Disposed || (s.FailedAt != 0 && s.FailedAt == layer!.Revision))
        continue;
      // a zone that has lost its records is built too (an empty zone, at no cost): its old drawing and colliders then go through the hand-over
      bool vanished = s.Dirty && s.Current != null && !(layer!.TryGetZoneRow(s.Key, out var row) && row.Placements > 0);
      bool wants = s.InDraw || s.WantsColliders(now) || vanished;
      if (!wants || !(s.Dirty || (s.Current == null && s.Next == null)))
        continue;
      bool collider = s.WantsColliders(now);
      var from = collider ? refZone : camZone;
      long dx = s.Key.X - from.X, dz = s.Key.Z - from.Z;
      long key = (collider ? 0L : 1L << 40) + dx * dx + dz * dz;
      if (key < bestKey)
      {
        bestKey = key;
        best = s;
      }
    }
    return best;
  }

  private static void Start(ZoneSlot s, float now)
  {
    s.Dirty = false;
    var job = new BuildJob { Slot = s, Layer = layer!, Revision = layer!.Revision, Kinds = kinds, Started = now };
    s.Job = job;
    if (!layer.TryGetZoneRow(s.Key, out var row) || row.Placements == 0)
    {
      // the zone has no records any more: its build is an empty zone, and needs no worker
      job.Result = BuiltZone.Empty(s.Key.X, s.Key.Z, job.Revision);
      job.Stage = 3;
      Jobs.Add(job);
      return;
    }
    job.Row = row;
    job.Counted = true;
    building++;
    Jobs.Add(job);
    ThreadPool.QueueUserWorkItem(_ =>
    {
      try
      {
        var data = job.Layer.Decode(job.Row);
        var used = new HashSet<int>();
        for (int i = 0; i < data.Count; i++)
          used.Add(data.Palette[i]);
        var list = new List<int>(used);
        list.Sort();
        job.Data = data;
        job.Used = list.ToArray();
        job.Stage = 1;
      }
      catch (Exception e)
      {
        job.Error = e;
        job.Stage = 4;
      }
    });
  }

  // the zone's palette entries are resolved on the main thread, once each per session, within a few milliseconds a frame
  private static void Resolve(BuildJob job, Stopwatch clock)
  {
    if (job.Slot.Disposed || !ReferenceEquals(job.Layer, layer))
    {
      job.Stage = 4;
      return;
    }
    foreach (int e in job.Used)
    {
      if (job.Kinds[e] != null)
        continue;
      if (clock.Elapsed.TotalMilliseconds > ResolveBudgetMs)
        return;
      try
      {
        job.Kinds[e] = BakedKinds.Resolve(EntryDef.From(job.Layer.Palette[e]));
      }
      catch (Exception ex)
      {
        LogOnce("palette entry " + job.Layer.Palette[e], ex);
        job.Kinds[e] = BakedKinds.Resolve(new EntryDef { Names = job.Layer.Palette[e].Candidates.Length > 0 ? [job.Layer.Palette[e].Candidates[0].Name] : [], Role = BakedRole.Live });
      }
    }
    job.Stage = 2;
    ThreadPool.QueueUserWorkItem(_ =>
    {
      try
      {
        job.Result = BakedZoneBuild.Build(job.Data!, job.Kinds, job.Revision);
        job.Stage = 3;
      }
      catch (Exception ex)
      {
        job.Error = ex;
        job.Stage = 4;
      }
    });
  }

  private static void Integrate(BuildJob job, float now)
  {
    var s = job.Slot;
    if (job.Counted)
      building--;
    job.Counted = false;
    s.Job = null;
    if (s.Disposed || !ReferenceEquals(job.Layer, layer))
      return;   // the layer changed since: the zone is Dirty and builds again
    if (job.Stage == 4 || job.Result == null)
    {
      if (job.Error != null)
        LogOnce($"zone {s.Key}", job.Error);
      s.FailedAt = job.Revision;
      BakedZoneHold.Release(s.Key);
      return;
    }
    var built = job.Result;
    BakedPaint.Offer(s.Key, job.Data?.Paint, job.Revision);
    job.Data = null;
    if (s.Current == null)
    {
      s.Current = built;
      drawDirty = true;
      MakeObjects(s, now);
    }
    else
    {
      // a rebuilt zone: the old drawing and colliders stand until its replacement pieces have instances (9.7)
      DropNext(s);
      s.Next = built;
      s.NextSince = now;
      s.NextPolledAt = 0f;
    }
    if (built.Skipped > 0 && Reported.Add("skipped:" + s.Key))
      Log($"baked placements: zone {s.Key}: {built.Skipped} records have no usable palette entry here and are left out");
  }

  // ---- a zone, each frame --------------------------------------------------------------------------------------------------------------

  private static void Work(ZoneSlot s, float now)
  {
    s.Objects?.Poll();
    if (s.Next != null)
      Handover(s, now);
    if (s.Current != null && s.Objects == null && s.WantsColliders(now) && integratedThisFrame < MaxIntegrationsPerFrame)
      MakeObjects(s, now);
    // a lit copy is only worth making where there are copies, and the zone's object exists only where the colliders are wanted
    if (s.Objects is { Root: not null } objects && objects.Props == null && s.Current != null && s.Current.HasProps)
      objects.Props = new BakedCopies.Props(s.Current, objects.Root.transform);
  }

  // the zone's colliders (and the root its lit copies and seats stand under), made when the zone is built or enters the collider ring
  private static void MakeObjects(ZoneSlot s, float now)
  {
    var built = s.Current;
    if (built == null || s.Objects != null || !s.WantsColliders(now) || (!built.HasColliders && !built.HasProps))
      return;
    integratedThisFrame++;
    var objects = new ZoneObjects(built);
    objects.Create(true);
    s.Objects = objects;
  }

  private static void Handover(ZoneSlot s, float now)
  {
    var next = s.Next!;
    if (s.NextObjects == null && s.WantsColliders(now) && next.HasColliders && integratedThisFrame < MaxIntegrationsPerFrame)
    {
      integratedThisFrame++;
      s.NextObjects = new ZoneObjects(next);
      s.NextObjects.Create(false);
    }
    s.NextObjects?.Poll();
    bool timeUp = now - s.NextSince >= HandoverCap;
    bool cooked = s.NextObjects == null ? !(next.HasColliders && s.WantsColliders(now)) : s.NextObjects.CollidersAssigned;
    if (!timeUp)
    {
      if (!cooked)
        return;
      // the zone's real objects: polled a few times a second
      if (now - s.NextPolledAt < 0.25f)
        return;
      s.NextPolledAt = now;
      if (!ObjectsInstanced(s.Key))
        return;
    }
    // the new build stands, and the old one goes
    var old = s.Objects;
    s.NextObjects?.Activate();
    s.Current = next;
    s.Objects = s.NextObjects;
    s.Next = null;
    s.NextObjects = null;
    old?.Destroy();
    drawDirty = true;
  }

  private static void DropNext(ZoneSlot s)
  {
    s.NextObjects?.Destroy();
    s.NextObjects = null;
    s.Next = null;
  }

  // Every object of the zone that ZNetScene should create has an instance (the test IsAreaReady makes of its 3 x 3, for one zone).
  private static bool ObjectsInstanced(ZoneKey zone)
  {
    var scene = ZNetScene.instance;
    var objects = ZDOMan.instance;
    if (scene == null || objects == null)
      return true;
    ZdoScratch.Clear();
    var at = zone.ToVector2s();
    objects.FindSectorObjects(at, new SimulationDistance(0, 0), ZdoScratch);
    bool ready = true;
    foreach (var zdo in ZdoScratch)
    {
      // a sector holds every zone the game files with it (all beyond its map share one): this zone's own only
      if (ZoneSystem.GetZone(zdo.GetPosition()) != at)
        continue;
      if (scene.IsPrefabZDOValid(zdo) && !scene.FindInstance(zdo))
      {
        ready = false;
        break;
      }
    }
    ZdoScratch.Clear();
    return ready;
  }

  private static bool SlotReady(ZoneSlot s) =>
    s.Current != null && (s.Current.HasColliders ? s.Objects is { CollidersAssigned: true } : true);

  // a zone whose colliders are wanted and not standing holds its zone until they stand; a rebuilt zone keeps its old colliders and needs no hold
  private static void SyncHold(ZoneSlot s, float now)
  {
    bool needs = !s.Disposed && !s.HoldGaveUp && s.FailedAt == 0 && s.WantsColliders(now) && !SlotReady(s);
    if (needs)
      BakedZoneHold.Take(s.Key);
    else if (BakedZoneHold.Holding(s.Key))
      BakedZoneHold.Release(s.Key);
  }

  internal static void HoldTimedOut(ZoneKey zone)
  {
    if (Slots.TryGetValue(zone, out var s))
      s.HoldGaveUp = true;
  }

  /// <summary>
  /// Whether a zone of the 3 x 3 around a point still waits for its colliders: the IsAreaReady postfix (9.3). A zone that has records and is
  /// not in a ring yet is asked for, so a login or a teleport into a town builds it at once.
  /// </summary>
  internal static bool WaitsAround(Vector3 point)
  {
    if (!Prepare())
      return false;
    float now = Now();
    var centre = ZoneKey.OfPoint(point);
    bool waits = false;
    for (int dz = -1; dz <= 1; dz++)
      for (int dx = -1; dx <= 1; dx++)
      {
        var key = new ZoneKey(centre.X + dx, centre.Z + dz);
        if (!layer!.TryGetZoneRow(key, out var row) || row.Placements == 0)
          continue;
        var s = Slot(key);
        s.DemandUntil = now + 15f;
        if (s.HoldGaveUp || s.Disposed || s.FailedAt != 0)
          continue;
        if (!SlotReady(s))
        {
          waits = true;
          BakedZoneHold.Take(key);
        }
      }
    return waits;
  }

  // ---- letting go ---------------------------------------------------------------------------------------------------------------------------

  private static void Free(float now)
  {
    Scratch.Clear();
    foreach (var s in Slots.Values)
    {
      // a zone that is being built or handed over keeps what it has until that is done
      if (s.Job != null || s.Next != null)
        continue;
      bool wantsColliders = s.WantsColliders(now);
      if (!wantsColliders && s.LeftCollider >= 0f && now - s.LeftCollider >= Linger && s.Objects != null && !(s.Objects.Props?.SatOn() ?? false))
      {
        s.Objects.Destroy();
        s.Objects = null;
        s.NextObjects?.Destroy();
        s.NextObjects = null;
      }
      if (!s.InDraw && !wantsColliders && s.LeftDraw >= 0f && now - s.LeftDraw >= Linger && s.Objects == null)
        Scratch.Add(s);
    }
    foreach (var s in Scratch)
    {
      s.Disposed = true;
      BakedZoneHold.Release(s.Key);
      DropNext(s);
      Slots.Remove(s.Key);
      drawDirty = drawDirty || s.Current != null;
      s.Current = null;
    }
    Scratch.Clear();
  }

  // a world ends, or the layer goes: every zone's objects, holds, paint and drawing
  private static void Teardown()
  {
    BakedTransfer.ClientReports = false;
    foreach (var s in Slots.Values)
    {
      s.Disposed = true;
      s.Objects?.Destroy();
      s.NextObjects?.Destroy();
    }
    Slots.Clear();
    Jobs.Clear();
    Pending.Clear();
    building = 0;
    BakedZoneHold.ReleaseAll();
    BakedPaint.Clear();
    BakedLightShadows.Clear();
    BakedLightCount.Clear();
    BakedDraw.Reset();
    BakedKinds.ResetAll();
    ring = [];
    ringVersion++;
    kinds = [];
    layer = null;
    scene = null;
    camZone = new ZoneKey(int.MinValue, int.MinValue);
    refZone = camZone;
    ringsDirty = true;
    drawDirty = false;
  }

  private static void LogOnce(string what, Exception e)
  {
    string key = what + ": " + e.Message;
    if (Reported.Add(key))
      LogError($"baked placements: {what}: {e}");
  }

  /// <summary>The host's OnDestroy: the manager lets go of everything.</summary>
  internal static void HostGone()
  {
    host = null;
    Teardown();
  }
}

/// <summary>The one MonoBehaviour of the client side: calls the manager once a frame, after the game's own updates.</summary>
internal sealed class BakedClientHost : MonoBehaviour
{
  private void LateUpdate() => BakedClient.Frame();

  private void OnDestroy() => BakedClient.HostGone();
}

// the manager starts with the first world of the session
[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class BakedClientAttach
{
  private static void Postfix() => BakedClient.Attach();
}

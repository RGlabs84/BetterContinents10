// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// A whole regeneration of a made-up world, built from the game's own classes (ZDO, ZDOMan, ZNet, ZoneSystem, ZNetScene and
// ZNetPeer, made without their Unity parts): the scan for what protects, the plan, the work on the real ZDOs, and the
// twin guard's view of the zone afterwards.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Edges = BetterContinents.ZoneReset.Edges;
using Kind = BetterContinents.ZoneReset.Kind;

internal static partial class Program
{
  private static readonly FieldInfo FPrefab = typeof(ZDO).GetField("m_prefab", Any)!;
  private static readonly FieldInfo FPosition = typeof(ZDO).GetField("m_position", Any)!;

  private static ZDO NewZdo(string prefab, uint id, float x = 0f, float y = 30f, float z = 0f, long user = 1000L)
  {
    // Not ZDOPool.Create: the constructor of ZDO calls into Unity (Quaternion.eulerAngles).
    var zdo = Make<ZDO>();
    zdo.m_uid = new ZDOID(user, id);
    FPrefab.SetValue(zdo, prefab.GetStableHashCode());
    FPosition.SetValue(zdo, new Vector3(x, y, z));
    return zdo;
  }

  private static ZDO Creator(ZDO zdo, long creator)
  {
    ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_creator, creator);
    return zdo;
  }

  private static ZDO Tame(ZDO zdo)
  {
    ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_tamed, 1);
    return zdo;
  }

  // ------------------------------------------------------------------------------------------------ what kind an object is

  private static void KindTests()
  {
    Section("kinds: what the rules are told about real ZDOs");
    ZDOExtraData.Reset();
    var local = new ZDOID(600L, 1u);
    var none = new ZDOID();
    C(ZoneRegen.KindOf(NewZdo("Pine", 1), local) == Kind.None, "a tree is nothing special");
    C(ZoneRegen.KindOf(Creator(NewZdo("piece_chest", 2), 42L), local) == Kind.Piece, "an object with a creator is a piece");
    C(ZoneRegen.KindOf(Creator(NewZdo("Pine", 3), 0L), local) == Kind.None, "a creator of 0 is none");
    C(ZoneRegen.KindOf(NewZdo("Player_tombstone", 4), local) == Kind.Tombstone, "Player_tombstone is a tombstone");
    C(ZoneRegen.KindOf(Tame(NewZdo("Boar", 5)), local) == Kind.Tamed, "a creature with tamed set is tamed");
    C(ZoneRegen.KindOf(NewZdo("Boar", 6), local) == Kind.None, "a wild one is not");
    var me = NewZdo("Player", 1, user: 600L);
    C(ZoneRegen.KindOf(me, local) == (Kind.Player | Kind.Local), "the player whose character this machine holds: player and local");
    var other = NewZdo("Player", 2, user: 700L);
    C(ZoneRegen.KindOf(other, local) == Kind.Player, "another machine's player is just a player");
    C(ZoneRegen.KindOf(other, none) == Kind.Player, "on a dedicated server (no local character) every player is another machine's");
    var deep = NewZdo("Player", 3, y: 5100f, user: 700L);
    C(ZoneRegen.KindOf(deep, local) == (Kind.Player | Kind.Interior), "a player above 3000 m is in a dungeon");
    var low = NewZdo("Player", 4, y: 3000f, user: 700L);
    C(ZoneRegen.KindOf(low, local) == Kind.Player, "exactly 3000 m is not above it");
    C(ZoneRegen.KindOf(Tame(Creator(NewZdo("piece_x", 7), 9L)), local) == (Kind.Piece | Kind.Tamed), "kinds combine");
    C(ZoneRegen.KindOf(Creator(NewZdo("Player", 5, user: 700L), 9L), local) == (Kind.Player | Kind.Piece), "a creator on a player is both");
  }

  // ------------------------------------------------------------------------------------------------ a whole world

  private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
  private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Any)!.SetValue(target, value);
  private static void SetStatic(Type type, string name, object? value) => type.GetField(name, Any)!.SetValue(null, value);
  private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, Any)!.GetValue(target)!;
  private static T GetStatic<T>(Type type, string name) => (T)type.GetField(name, Any)!.GetValue(null)!;

  private sealed class World
  {
    public readonly ZDOMan Man = Make<ZDOMan>();
    public readonly ZNet Net = Make<ZNet>();
    public readonly ZoneSystem Zones = Make<ZoneSystem>();
    public readonly ZNetScene Scene = Make<ZNetScene>();
    public readonly Dictionary<Vector2s, List<ZDO>> ByZone = [];
    public readonly HashSet<ZDOID> Survivors = [];
    public uint Next = 1;

    public List<ZDOID> DestroyQueue => GetField<List<ZDOID>>(Man, "m_destroySendList");
    // What earlier frames queued, which the clients were told of (Flush).
    public readonly List<ZDOID> Sent = [];
    public IEnumerable<ZDOID> All => Sent.Concat(DestroyQueue);
    public HashSet<Vector2s> Generated => GetField<HashSet<Vector2s>>(Zones, "m_generatedZones");
    public System.Collections.IDictionary Loaded => GetField<System.Collections.IDictionary>(Zones, "m_zones");

    public World()
    {
      SetField(Man, "m_sessionID", 77L);
      SetField(Man, "m_objectsBySector", new List<ZDO>[512 * 512]);
      SetField(Man, "m_objectsByID", new Dictionary<ZDOID, ZDO>());
      SetField(Man, "m_portalObjects", new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
      SetField(Man, "m_destroySendList", new List<ZDOID>());
      SetStatic(typeof(ZDOMan), "s_instance", Man);
      SetField(Net, "m_peers", new List<ZNetPeer>());
      SetField(Net, "m_zdoMan", Man);
      SetStatic(typeof(ZNet), "m_instance", Net);
      var zoneData = typeof(ZoneSystem).GetNestedType("ZoneData", BindingFlags.NonPublic)!;
      SetField(Zones, "m_generatedZones", new HashSet<Vector2s>());
      SetField(Zones, "m_zones", Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Vector2s), zoneData)));
      SetField(Zones, "m_locationInstances", new Dictionary<Vector2s, ZoneSystem.LocationInstance>());
      // ZoneSystem.PrepareSave copies it.
      SetField(Zones, "m_globalKeys", new HashSet<string>());
      SetStatic(typeof(ZoneSystem), "s_instance", Zones);
      SetField(Scene, "m_instances", new Dictionary<ZDO, ZNetView>());
      SetStatic(typeof(ZNetScene), "s_instance", Scene);
    }

    public ZDO Add(Vector2s zone, string prefab, float dx = 0f, float dz = 0f, float y = 30f, long user = 1000L)
    {
      var zdo = NewZdo(prefab, Next++, zone.x * 64f + dx, y, zone.y * 64f + dz, user);
      GetField<Dictionary<ZDOID, ZDO>>(Man, "m_objectsByID")[zdo.m_uid] = zdo;
      var sectors = GetField<List<ZDO>[]>(Man, "m_objectsBySector");
      var index = ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector;
      (sectors[index] ??= []).Add(zdo);
      if (!ByZone.TryGetValue(zone, out var list))
        ByZone[zone] = list = [];
      list.Add(zdo);
      return zdo;
    }

    public ZNetPeer Peer(ZDO character, long id)
    {
      var peer = Make<ZNetPeer>();
      peer.m_uid = id;
      peer.m_characterID = character.m_uid;
      peer.m_refPos = character.GetPosition();
      GetField<List<ZNetPeer>>(Net, "m_peers").Add(peer);
      return peer;
    }

    public void Place(Vector2s zone, bool placed)
    {
      var instance = new ZoneSystem.LocationInstance { m_position = new Vector3(zone.x * 64f, 30f, zone.y * 64f), m_placed = placed };
      Zones.m_locationInstances[zone] = instance;
    }

    // A location the game has placed (or not), with the radius its objects reach.
    public void PlaceLocation(Vector2s zone, bool placed, float radius, float dx = 0f, float dz = 0f)
    {
      var location = new ZoneSystem.ZoneLocation { m_exteriorRadius = radius, m_interiorRadius = radius / 2f };
      Zones.m_locationInstances[zone] = new ZoneSystem.LocationInstance { m_location = location, m_position = new Vector3(zone.x * 64f + dx, 30f, zone.y * 64f + dz), m_placed = placed };
    }

    public void Load(Vector2s zone) =>
      Loaded.Add(zone, Activator.CreateInstance(typeof(ZoneSystem).GetNestedType("ZoneData", BindingFlags.NonPublic)!, true));

    // What the game does on the next frame: ZDOMan.SendDestroyed hands the queue to every machine, this one included, and
    // HandleDestroyedZDO takes each object off its sector's list.
    public void Flush()
    {
      var byId = GetField<Dictionary<ZDOID, ZDO>>(Man, "m_objectsByID");
      var sectors = GetField<List<ZDO>[]>(Man, "m_objectsBySector");
      foreach (var id in DestroyQueue)
      {
        Sent.Add(id);
        if (byId.Remove(id, out var zdo))
          sectors[ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector]?.Remove(zdo);
      }
      DestroyQueue.Clear();
    }

    // Every object that was ever added to the world, by id.
    public Dictionary<ZDOID, ZDO> Everything => ByZone.Values.SelectMany(list => list).ToDictionary(zdo => zdo.m_uid);

    // The objects of a zone that are still in the world.
    public List<ZDO> LiveIn(Vector2s zone)
    {
      var byId = GetField<Dictionary<ZDOID, ZDO>>(Man, "m_objectsByID");
      return ByZone.TryGetValue(zone, out var list) ? list.Where(zdo => byId.ContainsKey(zdo.m_uid)).ToList() : [];
    }

    // A portal is kept apart from the sector lists (ZDOMan.AddIfPortal).
    public ZDO AddPortal(Vector2s zone, long creator)
    {
      var zdo = NewZdo("portal_wood", Next++, zone.x * 64f, 30f, zone.y * 64f);
      Creator(zdo, creator);
      GetField<Dictionary<ZDOID, ZDO>>(Man, "m_objectsByID")[zdo.m_uid] = zdo;
      var portals = GetField<Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>(Man, "m_portalObjects");
      var index = ZoneSystem.GetSectorIndex(zdo.GetPosition());
      if (!portals.TryGetValue(index, out var list))
        portals[index] = list = [];
      list.Add(zdo);
      return zdo;
    }

    public static void Close()
    {
      SetStatic(typeof(ZDOMan), "s_instance", null);
      SetStatic(typeof(ZNet), "m_instance", null);
      SetStatic(typeof(ZoneSystem), "s_instance", null);
      SetStatic(typeof(ZNetScene), "s_instance", null);
    }
  }

  // Runs ZoneRegen's steps to the end; the lines it said, and the frames it took. With a world, the clients are told of what a frame
  // destroyed before the next frame, as the game does (ZNet.Update runs before the coroutine continues); what the last frame
  // destroyed is still queued when this returns.
  private static (List<string> Lines, int Frames, ZoneRegen.Job Job) Regenerate(World? world = null, Action? afterPlan = null)
  {
    var lines = new List<string>();
    var job = new ZoneRegen.Job(lines.Add);
    var steps = ZoneRegen.Steps(job);
    int frames = 1;
    while (steps.MoveNext())
    {
      frames++;
      world?.Flush();
      // The frame the plan was made in has gone by: the work begins in the next.
      if (afterPlan != null && job.Progress.Total > 0)
      {
        afterPlan();
        afterPlan = null;
      }
    }
    return (lines, frames, job);
  }

  // The zones of a set that are within one zone of any of the others, worked out the long way round.
  private static HashSet<Vector2s> Near(IEnumerable<Vector2s> zones, IEnumerable<Vector2s> of) =>
    zones.Where(z => of.Any(p => Math.Max(Math.Abs(z.x - p.x), Math.Abs(z.y - p.y)) <= 1)).ToHashSet();

  private static void WorldTests()
  {
    using var seams = new Seams();
    // The clock is Unity's, which the tests do not have: a frame is a sixtieth of a second, and no frame runs out of time (the
    // frames are the objects' count and the players' wall time, so that these checks do not depend on the machine's speed).
    ZoneRegen.DeltaTime = () => 1f / 60f;
    ZoneRegen.FrameClock = () => 0.0;
    BigWorld();
    TwinGuardTests();
    SmallWorlds();
    GameWorldTests();
    ClosingWorld();
  }

  private static void BigWorld()
  {
    Section("a whole regeneration of a made-up world");
    ZDOExtraData.Reset();
    BC.ConfigDebugResetCommand.Value = "";
    var finishDefault = ZoneRegen.Finish;
    var w = new World();
    try
    {
      var block = Square(3);
      var tc = new Dictionary<Vector2s, ZDO>();
      foreach (var zone in block)
      {
        for (int i = 0; i < 4; i++)
          w.Add(zone, "Pine", dx: i * 5f - 7f, dz: i * 3f - 4f);
        w.Add(zone, "_ZoneCtrl");
        var compiler = w.Add(zone, "_TerrainCompiler");
        // The ground of the piece's zone was worked on, and protects its zones like the piece; no other has an edit yet.
        ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => zone.Equals(Z(2, 2))));
        tc[zone] = compiler;
        w.Generated.Add(zone);
      }
      // Something a player built, a tombstone, a player from another machine, the player at this machine, a tamed boar.
      var chest = Creator(w.Add(Z(2, 2), "piece_chest", dx: 3f, dz: 3f), 42L);
      var tomb = w.Add(Z(-3, -3), "Player_tombstone", dx: 2f);
      var remote = w.Add(Z(-2, 2), "Player", dx: 4f, user: 700L);
      w.Peer(remote, 700L);
      var me = w.Add(Z(0, 0), "Player", dx: 1f, user: 600L);
      SetField(w.Net, "m_characterID", me.m_uid);
      var boar = Tame(w.Add(Z(0, 1), "Boar", dx: 3f));
      // Two players who joined and have not entered the world: one reports nowhere (0, 0, 0), which is not a place; one is in the
      // south east, where it protects the zones around it.
      var nowhere = Make<ZNetPeer>();
      nowhere.m_uid = 800L;
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(nowhere);
      var loading = Make<ZNetPeer>();
      loading.m_uid = 801L;
      loading.m_refPos = new Vector3(3 * 64f, 30f, -3 * 64f);
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(loading);
      // Far out: past the 256 zones the sector grid reaches, where all objects share one sector list.
      var farReset = Z(312, 0);
      var farKept = Z(-400, 5);
      var farTrees = new[] { w.Add(farReset, "Pine"), w.Add(farReset, "Pine", dx: 3f) };
      var farOther = w.Add(farKept, "Pine");
      var farChest = Creator(w.Add(farKept, "piece_chest", dx: 2f), 42L);
      w.Generated.Add(farReset);
      w.Generated.Add(farKept);
      // Spawners and the creatures they made: one has walked into another zone that is reset, one into a zone that is kept, one
      // was tamed.
      ZDO Spawner(Vector2s zone, ZDO creature, float dx = 0f)
      {
        var spawner = w.Add(zone, "Spawner_Draugr", dx: dx);
        ZDOExtraData.SetConnection(spawner.m_uid, ZDOExtraData.ConnectionType.Spawned, creature.m_uid);
        return spawner;
      }
      var walked = w.Add(Z(-3, 0), "Draugr");
      var stranded = w.Add(Z(2, 1), "Draugr", dx: 5f);
      var pet = Tame(w.Add(Z(-2, 0), "Draugr", dx: 5f));
      var spawners = new[] { Spawner(Z(0, -2), walked), Spawner(Z(0, -3), stranded), Spawner(Z(-1, 0), pet, dx: 2f) };
      // Locations: one in a zone that is reset, one in a kept zone; and loaded copies of two zones.
      w.Place(Z(-1, -1), true);
      w.Place(Z(2, 1), true);
      w.Load(Z(0, 0));
      w.Load(Z(1, 1));

      int finished = 0;
      var finish = ZoneRegen.Finish;
      ZoneRegen.Finish = () => finished++;
      var tcBefore = tc[Z(2, 2)].GetByteArray(ZDOVars.s_TCData);
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrameWithPlayers, "with a player connected the frames are small, so the clients are not flooded");
      var generated = w.Generated.ToList();
      var protectorZones = new[] { Z(2, 2), Z(-3, -3), Z(-2, 2), Z(3, -3), farKept };
      var kept = Near(generated, protectorZones);
      // After the scan, somebody hoes in a zone that is left alone and meets zones that are reset.
      int doneAtPlan = -1;
      var (lines, frames, job) = Regenerate(w, () =>
      {
        doneAtPlan = w.All.Count();
        ZDOExtraData.Set(tc[Z(1, 1)].m_uid, ZDOVars.s_TCData, TcData((r, c) => true));
      });

      C(w.Generated.SetEquals(kept) && kept.Count == 9 + 4 + 9 + 4 + 1, $"the generated zones left are the 27 near the piece, the tombstone, the two players and the far chest, and no others (left: {w.Generated.Count})");
      var sent = w.All.ToList();
      var queued = sent.ToHashSet();
      var expectedVictims = new HashSet<ZDOID>();
      foreach (var (zone, zdos) in w.ByZone)
        if (!kept.Contains(zone))
          foreach (var zdo in zdos)
            if (zdo.m_uid != me.m_uid && zdo.m_uid != boar.m_uid && zdo.m_uid != pet.m_uid)
              expectedVictims.Add(zdo.m_uid);
      // The creature that walked into a zone that is kept goes with nothing else of that zone; the one in a reset zone is a victim already.
      C(queued.SetEquals(expectedVictims) && queued.Count == sent.Count, $"exactly the objects of the reset zones are destroyed, each once ({queued.Count}, {sent.Count} messages)");
      C(queued.Count == 23 * 6 + 2 + 4, "23 zones of 6 objects, the far zone's two trees, and the three spawners and the creature that walked");
      C(frames > 3 && w.Sent.Count > 0, $"with a player connected the work takes several frames ({frames}), the clients told after each");
      var everything = w.Everything;
      C(expectedVictims.All(id => everything[id].IsOwner() && everything[id].GetOwner() == ZDOMan.GetSessionID()) && ZDOMan.GetSessionID() == 77L,
        "each was taken over by this machine before it was destroyed, as the game requires (only the owner can destroy an object)");
      C(!queued.Contains(me.m_uid) && !queued.Contains(boar.m_uid), "the player at this machine and the tamed boar stay, in zones that are reset");
      C(queued.Contains(walked.m_uid) && !queued.Contains(stranded.m_uid) && !queued.Contains(pet.m_uid) && spawners.All(sp => queued.Contains(sp.m_uid)),
        "a spawner's creature goes with it, in the zone it walked to; not the one in a zone that is kept, not the tamed one");
      C(job.Progress.Spawned == 1 && job.Progress.SpawnedKept == 2, $"counted: one taken along, two that stay ({job.Progress.Spawned}, {job.Progress.SpawnedKept})");
      C(!queued.Contains(chest.m_uid) && !queued.Contains(tomb.m_uid) && !queued.Contains(remote.m_uid), "the piece, the tombstone and the other player stay");
      C(farTrees.All(t => queued.Contains(t.m_uid)) && !queued.Contains(farOther.m_uid) && !queued.Contains(farChest.m_uid),
        "far out the zone's trees go and the next zone's, in the same sector list, stay");
      C(w.ByZone.Where(z => kept.Contains(z.Key)).All(z => z.Value.All(zdo => !queued.Contains(zdo.m_uid))), "nothing in a kept zone is destroyed, its terrain compilers and zone controls included");
      C(w.Generated.Contains(Z(3, -2)) && w.Generated.Contains(Z(2, -3)) && !w.Generated.Contains(Z(1, -2)),
        "a player who has joined but not entered the world keeps the zones where their client says they are; one who reports (0, 0, 0) keeps nothing");

      // Locations and loaded copies.
      C(!w.Zones.m_locationInstances[Z(-1, -1)].m_placed && w.Zones.m_locationInstances[Z(2, 1)].m_placed, "the location of a reset zone is to be placed again; the kept zone's stays placed");
      C(w.Zones.m_locationInstances[Z(-1, -1)].m_position.y == 30f, "(no world generator here: its height is left as it was)");
      C(!w.Loaded.Contains(Z(0, 0)) && w.Loaded.Contains(Z(1, 1)), "the loaded copy of the reset zone is gone, the kept zone's stays");

      // The ground at the edges: (1,1) meets reset zones to the west and south; (2,2) meets none.
      var edited = ReadTc(tc[Z(1, 1)].GetByteArray(ZDOVars.s_TCData));
      bool shape = true;
      for (int i = 0; i < Pitch * Pitch; i++)
      {
        bool onMeeting = i / Pitch == 0 || i % Pitch == 0;
        if (edited.Edited[i] == onMeeting)
          shape = false;
      }
      C(shape && edited.Edited.Count(e => e) == Pitch * Pitch - 129, "(1,1)'s edits on its south and west edges are gone, all others stay (129 vertices)");
      C(tc[Z(2, 2)].GetByteArray(ZDOVars.s_TCData).SequenceEqual(tcBefore), "a kept zone that meets no reset zone has its data untouched, byte for byte");
      C(job.Progress.BorderZones > 0 && job.Progress.BorderVertices > 0, $"the border work is counted ({job.Progress.BorderZones} zones, {job.Progress.BorderVertices} vertices)");

      // What it said.
      C(lines.Count >= 2 && lines[0].Contains("resetting 24 of 51 generated zones") && lines[0].Contains("27 left alone"), "the first line says 24 of 51 zones: " + lines.FirstOrDefault());
      C(lines.Last().Contains("24 zones reset") && lines.Last().Contains("144 objects") && lines.Last().Contains("3 objects stay") && lines.Last().Contains("adjusted"), "the last line has the counts: " + lines.LastOrDefault());
      C(job.Errors == 0, "no errors");
      C(finished == 1, "the grass and the minimap's location icons are redrawn once, at the end");
      C(doneAtPlan == 0 && lines[0].Contains("resetting 24 of 51"), $"the plan is made and said in a frame of its own: nothing was destroyed by the end of it, the work follows in the next ({frames} frames)");

      // The game fills two zones once more: the player's, which is beside the field that was hoed, and one with nothing near it. Only the
      // second is reset again, and its two new trees.
      w.Flush();
      C(!w.LiveIn(Z(0, 0)).Any(zdo => zdo.GetPrefab() == "Pine".GetStableHashCode()) && w.LiveIn(Z(0, 0)).Contains(me), "(the clients have heard: the old trees are gone, the player is not)");
      var kept0 = w.Add(Z(0, 0), "Pine", dx: 9f);
      var refill = w.Add(Z(-1, -1), "Pine", dx: 9f);
      w.Add(Z(-1, -1), "Pine", dx: 10f);
      w.Generated.Add(Z(0, 0));
      w.Generated.Add(Z(-1, -1));
      var before = w.All.Count();
      var (lines2, _, job2) = Regenerate(w);
      var again = w.All.Skip(before).ToList();
      C(job2.Progress.Reset == 1 && job2.Progress.Destroyed == 2 && again.Count == 2 && again.Contains(refill.m_uid), "a second run resets only the zone that generated again, and its two new trees");
      C(w.Generated.Contains(Z(0, 0)) && !again.Contains(kept0.m_uid), "the ground that was hoed since keeps the player's zone beside it: the second run does not reset it");
      C(lines2.Last().Contains("1 zone reset"), "and says so: " + lines2.Last());
    }
    finally
    {
      ZoneRegen.Finish = finishDefault;
      World.Close();
    }
  }

  // The twin guard (VegetationTwins.Begin) lists a zone's objects before a refill, leaving out those queued to be destroyed. The
  // reset queues a zone's whole fill in one go and the zone can fill again before the queue is sent, so a refill must not find the
  // old trees in the way of the new ones.
  private static void TwinGuardTests()
  {
    Section("the vegetation twin guard after a reset");
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var zone = Z(0, 0);
      var old = new[] { w.Add(zone, "Pine", dx: 5f, dz: 5f), w.Add(zone, "Pine", dx: -8f, dz: 3f) };
      w.Generated.Add(zone);
      var (_, _, job) = Regenerate();
      C(job.Progress.Reset == 1 && w.DestroyQueue.Count == 2, "the zone's trees are queued for the clients, and not yet gone from the world's lists");
      var pine = "Pine".GetStableHashCode();
      var guard = typeof(VegetationTwins);
      void Entry()
      {
        SetStatic(guard, "entryPrefab", pine);
        SetStatic(guard, "entryTurn", 1);
      }

      // The refill may stand where the queued trees stood.
      VegetationTwins.Begin(zone);
      Entry();
      bool blockedByQueued = VegetationTwins.Skip(old[0].GetPosition()) || VegetationTwins.Skip(old[1].GetPosition());
      VegetationTwins.End();
      C(!blockedByQueued, "a refill is not blocked by the trees the reset has queued and the clients have not been told of yet");

      // A tree that is not leaving does block a copy on its spot: the guard still keeps a zone filled twice from doubling.
      var queue = w.DestroyQueue.ToList();
      w.DestroyQueue.Clear();
      VegetationTwins.Begin(zone);
      Entry();
      bool blockedByLive = VegetationTwins.Skip(old[0].GetPosition());
      VegetationTwins.End();
      w.DestroyQueue.AddRange(queue);
      C(blockedByLive, "the same spot is blocked when that tree is not leaving: a zone filled twice still does not double");

      // Once the clients have heard, the zone is empty and a refill is plain.
      w.Flush();
      VegetationTwins.Begin(zone);
      Entry();
      bool free = !VegetationTwins.Skip(old[0].GetPosition());
      VegetationTwins.End();
      C(free && w.LiveIn(zone).Count == 0, "and when the clients have heard, nothing of the old fill is left to hold the refill back");
    }
    finally
    {
      World.Close();
    }
  }

  private static void SmallWorlds()
  {
    Section("small worlds: dungeons, portals, and the surface");
    foreach (var (y, expectedKept) in new[] { (5100f, 9), (30f, 0) })
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        foreach (var zone in Square(2))
        {
          w.Add(zone, "Pine");
          w.Generated.Add(zone);
        }
        var me = w.Add(Z(0, 0), "Player", user: 600L, y: y);
        SetField(w.Net, "m_characterID", me.m_uid);
        C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame, "alone in the world the frames are as big as they may be");
        var (_, _, job) = Regenerate();
        C(job.Progress.Reset == 25 - expectedKept && w.Generated.Count == expectedKept && !w.DestroyQueue.Contains(me.m_uid),
          y > 3000 ? "the player at this machine in a dungeon: the 3 x 3 zones around the dungeon's entrance are left whole" : "the player at this machine on the ground: every zone is reset, theirs too, and they are not touched");
      }
      finally
      {
        World.Close();
      }
    }

    // The same player is another machine's: the zones around them are kept, on the ground and in a dungeon.
    foreach (var y in new[] { 30f, 5100f })
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        foreach (var zone in Square(2))
        {
          w.Add(zone, "Pine");
          w.Generated.Add(zone);
        }
        w.Add(Z(1, 1), "Player", user: 700L, y: y);
        var (_, _, job) = Regenerate();
        C(job.Progress.Reset == 16 && w.Generated.SetEquals(Square(1, 1, 1).Where(z => Math.Abs(z.x) <= 2 && Math.Abs(z.y) <= 2)), "a player from another machine keeps the 3 x 3 zones around them, at any height");
      }
      finally
      {
        World.Close();
      }
    }

    // Dedicated server: nobody is the local player, so every player's surroundings are kept.
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        foreach (var zone in Square(2))
        {
          w.Add(zone, "Pine");
          w.Generated.Add(zone);
        }
        w.Add(Z(-2, -2), "Player", user: 700L);
        w.Add(Z(2, 2), "Player", user: 701L);
        var (_, _, job) = Regenerate();
        C(job.Progress.Reset == 25 - 8 && w.Generated.Count == 8, "on a dedicated server (no local character) each player keeps the zones around them: two corners of the world");
      }
      finally
      {
        World.Close();
      }
    }

    // A portal is not in the sector lists; its creator still keeps its zones.
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        foreach (var zone in Square(2))
        {
          w.Add(zone, "Pine");
          w.Generated.Add(zone);
        }
        var portal = w.AddPortal(Z(0, 0), 42L);
        var (_, _, job) = Regenerate();
        C(job.Progress.Reset == 16 && w.Generated.SetEquals(Square(1)) && !w.DestroyQueue.Contains(portal.m_uid), "a portal a player placed keeps the zones around it, though it is in no sector list");
      }
      finally
      {
        World.Close();
      }
    }

    // A piece placed in a zone while the work is under way: the scan has gone by, the zone's own turn finds it.
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        foreach (var zone in Square(2))
        {
          w.Add(zone, "Pine");
          w.Generated.Add(zone);
        }
        var job = new ZoneRegen.Job(_ => { });
        var steps = ZoneRegen.Steps(job);
        // Run to the end of the plan's frame, then place a piece in the zone that comes first, before any is reset.
        int guard = 0;
        while (job.Progress.Total == 0 && guard++ < 1000 && steps.MoveNext())
        {
        }
        var first = Z(0, 0);
        Creator(w.Add(first, "piece_chest"), 42L);
        while (steps.MoveNext())
        {
        }
        C(job.Progress.Skipped == 9 && w.Generated.SetEquals(Square(1)) && job.Progress.Reset == 16,
          "a piece placed after the scan, in the next zone to be reset: that zone is left alone and so are the 8 around it, the others are reset");
        C(w.DestroyQueue.Count == 16, "only the trees of the other zones are destroyed");
      }
      finally
      {
        World.Close();
      }
    }
  }

  // The adapter on its own: what it does with an object that is not what it was when it was listed.
  private static void GameWorldTests()
  {
    Section("the game's side: objects that changed since they were listed");
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var job = new ZoneRegen.Job(_ => { });
      var world = new ZoneRegen.GameWorld(job);
      var zone = Z(4, 4);
      var pine = w.Add(zone, "Pine");
      var rock = w.Add(zone, "rock");
      var late = w.Add(zone, "Boar");
      var listed = world.ObjectsIn(zone);
      C(listed.Count == 3 && listed.Select(o => o.Id).SequenceEqual([pine.m_uid, rock.m_uid, late.m_uid]), "an object is listed in the order the game lists them");
      C(listed.All(o => o.Kind == Kind.None), "plain objects have no kind");

      // Tamed since it was listed: it is not destroyed.
      Tame(late);
      C(!world.Destroy(listed[2], out _) && w.DestroyQueue.Count == 0, "a creature tamed after it was listed is not destroyed");
      // Its ZDO was recycled for another object: not destroyed either.
      FPrefab.SetValue(rock, "Boar".GetStableHashCode());
      C(!world.Destroy(listed[1], out _) && w.DestroyQueue.Count == 0, "an object whose ZDO is another kind by now is left alone");
      FPrefab.SetValue(rock, "rock".GetStableHashCode());
      // A plain object: taken over and queued, once.
      C(!pine.IsOwner(), "(it is not ours to begin with)");
      C(world.Destroy(listed[0], out _) && w.DestroyQueue.SequenceEqual([pine.m_uid]) && pine.IsOwner() && pine.GetOwner() == ZDOMan.GetSessionID(), "a plain object is taken over and queued for every machine");
      // Asked for again in the same frame (a spawner and its creature, listed in two zones): queued once.
      C(!world.Destroy(listed[0], out _) && w.DestroyQueue.Count == 1, "an object asked for twice in a frame is queued once");
      // Gone already.
      w.Flush();
      world.NewFrame();
      C(!world.Destroy(listed[0], out _) && w.DestroyQueue.Count == 0, "an object that is gone is not destroyed twice");

      // A spawner knows its creature; the creature can be found wherever it is.
      var other = Z(9, 9);
      var creature = w.Add(other, "Draugr", dx: 3f);
      var spawner = w.Add(zone, "Spawner_Draugr", dx: 4f);
      ZDOExtraData.SetConnection(spawner.m_uid, ZDOExtraData.ConnectionType.Spawned, creature.m_uid);
      var relisted = world.ObjectsIn(zone);
      C(world.Destroy(relisted.Single(o => o.Id == spawner.m_uid), out var held) && held == creature.m_uid && world.Destroy(relisted.Single(o => o.Id == rock.m_uid), out var none) && none.IsNone(),
        "a spawner hands back the creature it made when it is destroyed, anything else hands back none");
      var found = world.Find(creature.m_uid);
      C(found is { } f && f.Zone.Equals(other) && f.Prefab == "Draugr".GetStableHashCode() && f.Kind == Kind.None, "the creature is found in the zone it is in now");
      Tame(creature);
      C(world.Find(creature.m_uid)!.Value.Kind == Kind.Tamed, "and as the world has it now: tamed");
      C(world.Find(new ZDOID(1000L, 99999u)) == null, "an id that is not in the world is not found");

      // Whether the game has generated a zone: its own list, as it is now (the work asks at a stray's turn).
      var spot = Z(7, -3);
      C(!world.IsGenerated(spot), "a zone nobody has generated is not generated");
      w.Generated.Add(spot);
      C(world.IsGenerated(spot) && !world.IsGenerated(Z(-7, 3)) && !world.IsGenerated(Z(7, 3)) && !world.IsGenerated(Z(3, -7)), "a zone the game has generated is, and the zones beside it and across are not");
      world.Ungenerate(spot);
      C(!world.IsGenerated(spot) && w.Generated.Count == 0, "and it is not any more once it is no longer generated");

      // A zone with no terrain compiler has no border to mend.
      C(world.AdjustBorder(zone, Edges.North) == 0, "a zone without a terrain compiler: nothing to mend");
      // A compiler with no data yet.
      var compiler = w.Add(zone, "_TerrainCompiler");
      C(world.AdjustBorder(zone, Edges.North) == 0, "a compiler with no data: nothing to mend");
      ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, new byte[] { 1, 2, 3 });
      C(world.AdjustBorder(zone, Edges.North) == 0 && compiler.GetByteArray(ZDOVars.s_TCData).SequenceEqual(new byte[] { 1, 2, 3 }) && job.Errors == 0, "a compiler whose data is not recognised is left as it is");
      ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => true));
      C(world.AdjustBorder(zone, Edges.North) == 65 && compiler.IsOwner() && ReadTc(compiler.GetByteArray(ZDOVars.s_TCData)).Edited.Count(e => e) == Pitch * Pitch - 65, "a compiler with edits on the north side: they are cleared, and the compiler is ours");

      // No unplace for a zone without a location; the root of a zone that is not loaded is nothing to destroy.
      world.Unplace(zone);
      world.DestroyRoot(zone);
      world.Ungenerate(zone);
      C(w.Zones.m_locationInstances.Count == 0 && w.Loaded.Count == 0 && w.Generated.Count == 0, "a zone with no location, no loaded copy and not generated: nothing happens, nothing throws");
    }
    finally
    {
      World.Close();
    }
  }

  private static void ClosingWorld()
  {
    Section("a world that closes");
    ZDOExtraData.Reset();
    int touched = 0;
    var finishDefault = ZoneRegen.Finish;
    ZoneRegen.Finish = () => touched++;
    var w = new World();
    try
    {
      foreach (var zone in Square(1))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var job = new ZoneRegen.Job(_ => { });
      var steps = ZoneRegen.Steps(job);
      steps.MoveNext();
      World.Close();
      while (steps.MoveNext())
      {
      }
      C(job.Progress.Reset == 0 && w.DestroyQueue.Count == 0, "a world closed after the first frame: nothing is destroyed afterwards");
      var lines = new List<string>();
      var again = new ZoneRegen.Job(lines.Add);
      var w2 = new World();
      foreach (var zone in Square(1))
      {
        w2.Add(zone, "Pine");
        w2.Generated.Add(zone);
      }
      var stepsAgain = ZoneRegen.Steps(again);
      // To the end of the frame that makes the plan; the work itself would come in the next.
      while (again.Progress.Total == 0 && stepsAgain.MoveNext())
      {
      }
      World.Close();
      while (stepsAgain.MoveNext())
      {
      }
      C(again.Progress.Aborted && again.Progress.Reset == 0 && lines.Last().Contains("stopped after 0 of 9 zones"), "a world that closes after the plan, before the work: it says it stopped: " + lines.LastOrDefault());
      C(touched == 0, "and does not redraw anything");
    }
    finally
    {
      ZoneRegen.Finish = finishDefault;
      World.Close();
    }
  }
}

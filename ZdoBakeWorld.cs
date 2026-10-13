// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// The game's objects, as the runner needs them (IBakeWorld): the ZDOs of ZDOMan and the instances of ZNetScene. Main thread only. What the
// runner does with them is checked offline against a stand-in (Tests.BakeRunner.cs); this class is read against the game's code (ZDOMan, ZDO,
// ZNetScene, ZNetView: 1.0.17) and run in the game.
internal sealed class ZdoBakeWorld : IBakeWorld
{
  public static readonly ZdoBakeWorld Instance = new();

  private readonly Dictionary<int, LookInfo> looks = [];
  private ZNetScene? looksOf;

  // The objects exist (ZDOMan is made and the world's save is loaded); the prefabs may not yet at the check of a world load.
  public bool Ready => ZNet.instance != null && ZNet.World != null && ZDOMan.instance != null;

  public string WorldName => ZNet.World?.m_worldName ?? "";
  public long WorldUid => ZNet.World?.m_uid ?? 0L;

  public PrefabFacts? FactsOf(int prefab) => BakeClassifier.Facts(prefab);

  public int PrefabOf(string name)
  {
    var scene = ZNetScene.instance;
    int hash = name.GetStableHashCode();
    return scene != null && scene.HasPrefab(hash) ? hash : 0;
  }

  // What the prefab's own ZNetView gives its objects (ZNetView.Awake): flags a ZDO has to carry for the game to treat it as that piece.
  public (bool Persistent, bool Distant, byte Type) FlagsOf(int prefab)
  {
    var scene = ZNetScene.instance;
    var go = scene != null ? scene.GetPrefab(prefab) : null;
    var view = go != null ? go.GetComponent<ZNetView>() : null;
    return view != null ? (view.m_persistent, view.m_distant, (byte)view.m_type) : (true, false, (byte)ZDO.ObjectType.Default);
  }

  public LookInfo LookOf(int prefab)
  {
    var scene = ZNetScene.instance;
    if (scene == null)
      return LookInfo.None;
    if (!ReferenceEquals(looksOf, scene))
    {
      looks.Clear();
      looksOf = scene;
    }
    if (looks.TryGetValue(prefab, out var known))
      return known;
    var go = scene.GetPrefab(prefab);
    if (go == null)
      return looks[prefab] = LookInfo.None;
    var variations = new List<KeyValuePair<int, float[]>>();
    foreach (var variation in go.GetComponentsInChildren<MaterialVariation>(true))
      if (variation.m_materials.Count > 0)
        variations.Add(new KeyValuePair<int, float[]>(variation.m_materialIndex, variation.m_materials.Select(m => m.m_weight).ToArray()));
    return looks[prefab] = new LookInfo { Variations = [.. variations], RandomValues = go.GetComponentInChildren<RandomMaterialValues>(true) != null };
  }

  // ------------------------------------------------------------------------------------------------ reading

  // The objects whose position is in the zone. A zone's sector list also holds what the game files under it for want of a sector: with the
  // game's own sectors, everything beyond 256 zones out (ZoneSystem.SectorToIndex files all of that under sector 0, which is also zone
  // (-256, -256)), with Wide Sectors what is beyond 1024. So each position is looked at, as ZoneRegen's does.
  private static IEnumerable<ZDO> ZdosIn(ZoneKey zone)
  {
    var man = ZDOMan.instance;
    var sector = zone.ToVector2s();
    var index = ZoneSystem.SectorToIndex(sector);
    var seen = new HashSet<ZDOID>();
    var list = man.m_objectsBySector[index.Sector];
    if (list != null)
      for (int i = 0; i < list.Count; i++)
      {
        var zdo = list[i];
        if (zdo != null && zdo.IsValid() && ZoneSystem.GetZone(zdo.GetPosition()) == sector && seen.Add(zdo.m_uid))
          yield return zdo;
      }
    if (man.m_portalObjects.TryGetValue(index, out var portals))
      for (int i = 0; i < portals.Count; i++)
      {
        var zdo = portals[i];
        if (zdo != null && zdo.IsValid() && ZoneSystem.GetZone(zdo.GetPosition()) == sector && seen.Add(zdo.m_uid))
          yield return zdo;
      }
  }

  public void Gather(IReadOnlyList<ZoneKey> zones, List<WorldObject> into)
  {
    foreach (var zone in zones)
      foreach (var zdo in ZdosIn(zone))
      {
        var p = zdo.GetPosition();
        var r = zdo.m_rotation;
        into.Add(new WorldObject
        {
          Id = zdo.m_uid,
          Prefab = zdo.GetPrefab(),
          X = p.x,
          Y = p.y,
          Z = p.z,
          RotX = r.x,
          RotY = r.y,
          RotZ = r.z,
          Creator = zdo.GetLong(ZDOVars.s_creator, 0L),
          AlreadyBaked = ZDOExtraData.GetInt(zdo.m_uid, BakedKeys.Id, out _),
          HasConnection = ZDOExtraData.GetConnectionType(zdo.m_uid) != ZDOExtraData.ConnectionType.None,
        });
      }
  }

  public PieceCopy Copy(in WorldObject piece)
  {
    var zdo = ZDOMan.instance.GetZDO(piece.Id);
    if (zdo == null || !zdo.IsValid())
      throw new InvalidOperationException("the object is gone");
    return BakeCapture.CopyOf(zdo, "");
  }

  public Dictionary<ulong, ZDOID> LiveIn(ZoneKey zone)
  {
    var live = new Dictionary<ulong, ZDOID>();
    foreach (var zdo in ZdosIn(zone))
      if (ZDOExtraData.GetInt(zdo.m_uid, BakedKeys.Id, out int id))
      {
        var key = BakedFormat.LiveKey(ZDOExtraData.GetInt(zdo.m_uid, BakedKeys.Src, 0), unchecked((uint)id));
        if (!live.ContainsKey(key))
          live[key] = zdo.m_uid;
      }
    return live;
  }

  public ZDOID?[] FindStanding(IReadOnlyList<PieceCopy> pieces, float[]? heights = null)
  {
    var result = new ZDOID?[pieces.Count];
    // The zones the pieces are in, each looked at once: its objects, filed by the matching.
    foreach (var group in Enumerable.Range(0, pieces.Count).GroupBy(i => pieces[i].Zone))
    {
      var poses = new List<ObjectPose>();
      foreach (var zdo in ZdosIn(group.Key))
      {
        var p = zdo.GetPosition();
        var r = zdo.m_rotation;
        poses.Add(new ObjectPose { Id = zdo.m_uid, Prefab = zdo.GetPrefab(), X = p.x, Y = p.y, Z = p.z, RotX = r.x, RotY = r.y, RotZ = r.z });
      }
      var indices = group.ToList();
      var found = BakeMatch.Find(poses, indices.Select(i => pieces[i]).ToList(), heights != null, heights != null ? GroundAt : null);
      for (int k = 0; k < indices.Count; k++)
        if (found[k] >= 0)
        {
          result[indices[k]] = poses[found[k]].Id;
          if (heights != null)
            heights[indices[k]] = poses[found[k]].Y;
        }
    }
    return result;
  }

  // The ground's height at a point (the game's terrain collider; null where the zone's terrain is not loaded), for a piece StaticPhysics lifted.
  private static double? GroundAt(double x, double z)
  {
    var system = ZoneSystem.instance;
    return system != null && system.GetGroundHeight(new Vector3((float)x, 0f, (float)z), out float height) ? height : null;
  }

  public string Whereabouts(PieceCopy piece)
  {
    var poses = new List<ObjectPose>();
    // The piece's zone, and the zones next to it when it is within 3 m of an edge.
    var seen = new HashSet<ZoneKey>();
    foreach (double dx in new[] { -3.0, 0.0, 3.0 })
      foreach (double dz in new[] { -3.0, 0.0, 3.0 })
      {
        var zone = ZoneKey.OfPoint(piece.X + dx, piece.Z + dz);
        if (!seen.Add(zone))
          continue;
        foreach (var zdo in ZdosIn(zone))
        {
          var p = zdo.GetPosition();
          var r = zdo.m_rotation;
          poses.Add(new ObjectPose { Id = zdo.m_uid, Prefab = zdo.GetPrefab(), X = p.x, Y = p.y, Z = p.z, RotX = r.x, RotY = r.y, RotZ = r.z });
        }
      }
    return BakeMatch.Whereabouts(piece, poses);
  }

  // ------------------------------------------------------------------------------------------------ changing

  public RemoveOutcome Remove(ZDOID id, PieceCopy expected)
  {
    var man = ZDOMan.instance;
    // ZDOs are pooled: look the object up again, and make sure it is still the piece that was taken.
    var zdo = man.GetZDO(id);
    if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != expected.Prefab)
      return RemoveOutcome.Changed;
    var p = zdo.GetPosition();
    double dx = p.x - expected.X, dy = p.y - expected.Y, dz = p.z - expected.Z;
    if (dx * dx + dy * dy + dz * dz > BakeMath.PlaceMetres * BakeMath.PlaceMetres)
      return RemoveOutcome.Changed;
    // Owned here without a revision bump (SetOwner would bump it): no peer is sent the object once more just before it goes (10.7 step 7).
    zdo.SetOwnerInternal(ZDOMan.GetSessionID());
    var scene = ZNetScene.instance;
    var view = scene != null ? scene.FindInstance(zdo) : null;
    if (view != null)
      scene!.Destroy(view.gameObject);
    else
      man.DestroyZDO(zdo);
    return RemoveOutcome.Removed;
  }

  public ZDOID Create(PieceCopy copy) => BakeCapture.Create(copy).m_uid;

  // Every one of these objects is known to every client whose area holds it, as it is now: ZDOMan keeps for each peer what it sent and at
  // which revision, and sends an object again while the peer's revision is behind.
  public bool AllSent(IReadOnlyList<ZDOID> ids)
  {
    var man = ZDOMan.instance;
    var system = ZoneSystem.instance;
    if (man == null || system == null)
      return true;
    foreach (var peer in man.m_peers)
    {
      var netPeer = peer.m_peer;
      if (netPeer == null)
        continue;
      var peerZone = ZoneSystem.GetZone(netPeer.GetRefPos());
      var distance = netPeer.m_simulationDistance;
      foreach (var id in ids)
      {
        var zdo = man.GetZDO(id);
        if (zdo == null || !zdo.IsValid())
          continue;
        var zone = ZoneSystem.GetZone(zdo.GetPosition());
        bool near = distance.IsClassic
          ? Math.Abs(zone.x - peerZone.x) <= distance.NearSimulationDistance && Math.Abs(zone.y - peerZone.y) <= distance.NearSimulationDistance
          : system.ZonesWithinRadius(peerZone, zone, distance.NearSimulationDistance);
        if (!near)
          continue;
        if (!peer.m_zdos.TryGetValue(id, out var info) || info.m_dataRevision < zdo.DataRevision)
          return false;
      }
    }
    return true;
  }

  // Town mode: the server owns the piece (a revision bump, so that its client learns it is no longer the owner), and the piece gets the four keys.
  public void Adopt(ZDOID id, int source, uint bakeId, uint revision)
  {
    var zdo = ZDOMan.instance.GetZDO(id);
    if (zdo == null || !zdo.IsValid())
      return;
    zdo.SetOwner(ZDOMan.GetSessionID());
    zdo.Set(BakedKeys.Src, source);
    zdo.Set(BakedKeys.Id, unchecked((int)bakeId));
    zdo.Set(BakedKeys.Rev, unchecked((int)revision));
    zdo.Set(BakedKeys.Protect, true);
    BakedKeys.Changed(zdo);
  }

  // The four keys off a piece, and the creator on it when it has none (so that a zone reset keeps it, design 3.6). Removing a value does not
  // bump the object's revision, which is what tells the save and the peers that it changed, so it is bumped here.
  public void Release(ZDOID id, long creatorIfNone)
  {
    var zdo = ZDOMan.instance.GetZDO(id);
    if (zdo == null || !zdo.IsValid())
      return;
    zdo.SetOwner(ZDOMan.GetSessionID());
    foreach (int key in BakedKeys.BakeKeys)
      zdo.RemoveInt(key);
    if (creatorIfNone != 0L && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
      zdo.Set(ZDOVars.s_creator, creatorIfNone);
    zdo.IncreaseDataRevision();
    BakedKeys.Changed(zdo);
  }

  public (float X, float Y, float Z) EulerOf(Quaternion rotation)
  {
    var e = rotation.eulerAngles;
    return (e.x, e.y, e.z);
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownChunk.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// THE ZONE HOLD (section 9.3). A zone of baked buildings waits for its floors: until the zone's collider is in place, the game must not create
// its creatures and items (they would fall through a floor that is not built yet), and a login or a teleport into the town must wait too.
// Two parts:
//   - A placeholder in the game's loading list. For each zone in the collider ring that has colliders to build, a ZDO that is never registered
//     with ZDOMan (it only has to look like one to ZoneSystem: a position and a type) is put into ZoneSystem.m_loadingObjectsInZones, the list
//     Valheim's LocationProxy and DungeonGenerator hold their zone with. While it is there the zone does not count as loaded, and ZNetScene
//     creates none of the zone's objects of a lower type than Solid. It comes out when the zone's collider is assigned, or after 10 s.
//   - An IsAreaReady postfix over the 3 x 3 zones around the point: false while a zone of them holds records with colliders that are not
//     built yet and its hold has not timed out. The game checks "loading" for the arrival zone only, and logins, respawns and teleports wait
//     on IsAreaReady.
// UnsetLoadingInZone indexes the list without looking, so only our own placeholder is ever taken out, and the zone's entry only when it is empty.
internal static class BakedZoneHold
{
  /// <summary>A zone waits at most this long (seconds) for its colliders: a login at a logout point has no timeout of its own.</summary>
  internal const float Timeout = 10f;

  private sealed class Hold(ZDO placeholder, Vector2s zone, float since)
  {
    internal readonly ZDO Placeholder = placeholder;
    internal readonly Vector2s Zone = zone;
    internal readonly float Since = since;
  }

  private static readonly Dictionary<ZoneKey, Hold> Holds = new();
  private static readonly List<ZoneKey> Expired = [];

  /// <summary>How many zones are held now.</summary>
  internal static int Count => Holds.Count;

  /// <summary>Whether a zone is held, and its hold has not timed out.</summary>
  internal static bool Holding(ZoneKey zone) => Holds.ContainsKey(zone);

  /// <summary>A placeholder for a zone: a ZDO that is never registered with ZDOMan. ZoneSystem only reads its type and its position (the sector
  /// it files the zone's hold under), so that is all it has.</summary>
  internal static ZDO MakePlaceholder(ZoneKey zone)
  {
    var zdo = new ZDO();
    zdo.Init();
    zdo.m_position = zone.Centre;
    zdo.Type = ZDO.ObjectType.Solid;
    return zdo;
  }

  /// <summary>Holds a zone (if it is not held already).</summary>
  internal static void Take(ZoneKey zone)
  {
    var system = ZoneSystem.instance;
    if (system == null || Holds.ContainsKey(zone))
      return;
    var zdo = MakePlaceholder(zone);
    system.SetLoadingInZone(zdo);
    Holds[zone] = new Hold(zdo, zone.ToVector2s(), Time.realtimeSinceStartup);
  }

  /// <summary>Lets a zone go: its placeholder comes out of the game's list, and the zone's entry with it when nothing else holds the zone.</summary>
  internal static void Release(ZoneKey zone)
  {
    if (!Holds.TryGetValue(zone, out var hold))
      return;
    Holds.Remove(zone);
    var system = ZoneSystem.instance;
    var zones = system != null ? system.m_loadingObjectsInZones : null;
    if (zones != null)
      Remove(zones, hold.Zone, hold.Placeholder);
  }

  /// <summary>Takes one placeholder out of a loading list and the zone's entry when it was the last: by reference, since ZDO equality is by id and
  /// a placeholder has none; whatever else holds the zone (a LocationProxy, a dungeon) stays.</summary>
  internal static void Remove(Dictionary<Vector2s, List<ZDO>> loading, Vector2s zone, ZDO placeholder)
  {
    if (!loading.TryGetValue(zone, out var list))
      return;
    for (int i = 0; i < list.Count; i++)
      if (ReferenceEquals(list[i], placeholder))
      {
        list.RemoveAt(i);
        break;
      }
    if (list.Count == 0)
      loading.Remove(zone);
  }

  /// <summary>Lets every zone go (a world ends, or the layer is replaced).</summary>
  internal static void ReleaseAll()
  {
    foreach (var zone in new List<ZoneKey>(Holds.Keys))
      Release(zone);
  }

  /// <summary>Once a frame: lets go of a zone whose hold has outlived its time, and says so once.</summary>
  internal static void Expire()
  {
    if (Holds.Count == 0)
      return;
    float now = Time.realtimeSinceStartup;
    Expired.Clear();
    foreach (var kv in Holds)
      if (now - kv.Value.Since >= Timeout)
        Expired.Add(kv.Key);
    foreach (var zone in Expired)
    {
      LogWarning($"baked placements: zone {zone} is not built after {Timeout:0} s: it no longer waits for its colliders");
      Release(zone);
      BakedClient.HoldTimedOut(zone);
    }
  }

  // Logins, respawns and teleports wait here: the area around the point is not ready while a zone of the 3 x 3 still waits for its colliders.
  [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
  internal static class IsAreaReadyPatch
  {
    private static void Postfix(Vector3 point, ref bool __result)
    {
      if (__result && BakedClient.WaitsAround(point))
        __result = false;
    }
  }
}

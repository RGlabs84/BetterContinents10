// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownLiveProps.cs and the seats of Towns/TownUsables.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// THE TWO ROLES THAT ARE REAL NEAR THE PLAYER (section 9.6).
//
// Copy: wall torches, braziers, fire pits and candles are not instanced. Within Light Distance of the camera each is a lit copy of its prefab,
// made a few per frame and dropped again 20 m farther out, so their flames burn and their light falls on the street. A copy is made the way
// Valheim makes its building ghost (Player.SetupPlacementGhost): with ZNetView.m_forceDisableInit and TerrainOp.m_forceDisableTerrainOps, so
// it has no ZDO, is nobody's network object and never edits the terrain. Every component but the visual ones (meshes, LODs, lights, particles,
// sounds, animators) is removed before the fire is shown, so the flames' own heat, damage and smoke never wake: no fuel, no warmth, no damage,
// no collider, nothing to interact with, and the fire never goes out. Palette flag 16 (NoCopyLight) takes the Light away too.
//
// Seat: a chair, bench, stool or throne is drawn like any piece (the weld keeps it solid), and within Seat Distance of the player a real
// seat stands over it: a copy of the prefab stripped to its colliders and its Chair, made the same way, a few per frame, dropped again 3 m
// farther out, and never while the local player sits on it. A Chair needs no ZDO: sitting is Player.AttachStart on the Chair's attach
// point. The weld holds the seat's solid boxes, so the copy's boxes are made 4 cm larger on every side: a hover ray enters them first
// (Player.FindHoverObject takes the first collider it meets) and finds the Chair.
//
// Copies and seats are children of their zone's object and go with it.
internal static class BakedCopies
{
  internal const int LightsPerFrame = 4, SeatsPerFrame = 6;
  /// <summary>A copy goes this much (metres) farther out than it comes, so none flickers at the edge.</summary>
  internal const float LightHysteresis = 20f, SeatHysteresis = 3f;

  // what a lit copy keeps, by type name (the particle, audio and animation modules are not all references of this mod)
  private static readonly HashSet<string> KeepLit =
  [
    "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup", "Light", "LightLod", "LightFlicker",
    "ParticleSystem", "ParticleSystemRenderer", "AudioSource", "ZSFX", "Animator", "SpriteRenderer",
  ];

  private static readonly HashSet<string> LightTypes = ["Light", "LightLod", "LightFlicker"];

  // what a seat's copy keeps: its transforms, its colliders and its Chair(s)
  private static readonly HashSet<string> KeepSeat = ["Transform", "BoxCollider", "MeshCollider", "SphereCollider", "CapsuleCollider", "Chair"];

  private static readonly Dictionary<Type, RequireComponent[]> Requires = new();
  private static int lightsFrame = -1, lightsMade, seatsFrame = -1, seatsMade;

  private static bool CanMakeLight()
  {
    if (Time.frameCount != lightsFrame)
    {
      lightsFrame = Time.frameCount;
      lightsMade = 0;
    }
    return lightsMade < LightsPerFrame;
  }

  private static bool CanMakeSeat()
  {
    if (Time.frameCount != seatsFrame)
    {
      seatsFrame = Time.frameCount;
      seatsMade = 0;
    }
    return seatsMade < SeatsPerFrame;
  }

  /// <summary>A lit, inert copy of the prefab placed by <paramref name="m"/> (the piece's world matrix), under <paramref name="parent"/>.</summary>
  internal static GameObject MakeLit(BakedKind kind, Matrix4x4 m, Transform parent)
  {
    lightsMade++;
    var prefab = kind.Prefab!;
    GameObject go;
    ZNetView.m_forceDisableInit = true;
    TerrainOp.m_forceDisableTerrainOps = true;
    try
    {
      go = UnityEngine.Object.Instantiate(prefab, m.GetColumn(3), m.rotation);
      var show = new List<GameObject>();
      var hide = new List<GameObject>();
      Fire(go, show, hide);
      Strip(go, KeepLit, kind.NoCopyLight ? LightTypes : null);
      foreach (var h in hide)
        if (h != null)
          h.SetActive(false);
      foreach (var s in show)
        if (s != null)
          s.SetActive(true);
    }
    finally
    {
      ZNetView.m_forceDisableInit = false;
      TerrainOp.m_forceDisableTerrainOps = false;
    }
    go.name = prefab.name + " (baked)";
    go.transform.SetParent(parent, true);
    go.transform.localScale = Vector3.Scale(prefab.transform.localScale, m.lossyScale);
    return go;
  }

  /// <summary>A real seat over a baked chair: the prefab, stripped to its colliders and its Chair.</summary>
  internal static GameObject MakeSeat(BakedKind kind, Matrix4x4 m, Transform parent)
  {
    seatsMade++;
    var prefab = kind.Prefab!;
    GameObject go;
    ZNetView.m_forceDisableInit = true;
    TerrainOp.m_forceDisableTerrainOps = true;
    try
    {
      go = UnityEngine.Object.Instantiate(prefab, m.GetColumn(3), m.rotation);
      Strip(go, KeepSeat, null);
    }
    finally
    {
      ZNetView.m_forceDisableInit = false;
      TerrainOp.m_forceDisableTerrainOps = false;
    }
    go.name = prefab.name + " (baked seat)";
    go.transform.SetParent(parent, true);
    go.transform.localScale = Vector3.Scale(prefab.transform.localScale, m.lossyScale);
    Inflate(go);   // after the scale, which its sizes are measured in
    return go;
  }

  // what a burning, dry fireplace shows (Fireplace.UpdateState): the flames (the high-quality set, not the wet one) and the full log pile
  private static void Fire(GameObject go, List<GameObject> show, List<GameObject> hide)
  {
    foreach (var fp in go.GetComponentsInChildren<Fireplace>(true))
    {
      Add(show, fp.m_enabledObject);
      Add(show, fp.m_enabledObjectHigh);
      Add(hide, fp.m_enabledObjectLow);
      Add(show, fp.m_fullObject);
      Add(hide, fp.m_halfObject);
      Add(hide, fp.m_emptyObject);
      Add(hide, fp.m_playerBaseObject);
    }
  }

  private static void Add(List<GameObject> list, GameObject? o)
  {
    if (o != null)
      list.Add(o);
  }

  // Every component but the ones to keep: scripts first, then joints, then the rest (colliders, bodies), and never one that another component
  // still on the object requires (Unity refuses that with an error), so a pass may leave some for the next. `except` names kept types to
  // strip all the same (the Light of a copy flagged NoCopyLight).
  internal static void Strip(GameObject go, HashSet<string> keep, HashSet<string>? except)
  {
    foreach (var t in go.GetComponentsInChildren<Transform>(true))
    {
      var on = new List<Component>(t.GetComponents<Component>());
      for (int pass = 0; pass < 6; pass++)
      {
        bool removed = false;
        for (int stage = 0; stage < 3; stage++)
          for (int i = on.Count - 1; i >= 0; i--)
          {
            Component c = on[i];
            if (c == null)
            {
              on.RemoveAt(i);
              continue;
            }
            string name = c.GetType().Name;
            if (keep.Contains(name) && (except == null || !except.Contains(name)) || Stage(c) != stage || Required(c, on))
              continue;
            UnityEngine.Object.DestroyImmediate(c);
            on.RemoveAt(i);
            removed = true;
          }
        if (!removed)
          break;
      }
    }
  }

  private static int Stage(Component c) => c is MonoBehaviour ? 0 : c is Joint ? 1 : 2;

  private static bool Required(Component c, List<Component> on)
  {
    Type type = c.GetType();
    foreach (var other in on)
    {
      if (other == null || ReferenceEquals(other, c))
        continue;
      foreach (var rc in RequiresOf(other.GetType()))
        if (Is(type, rc.m_Type0) || Is(type, rc.m_Type1) || Is(type, rc.m_Type2))
          return true;
    }
    return false;
  }

  private static bool Is(Type type, Type? required) => required != null && required.IsAssignableFrom(type);

  private static RequireComponent[] RequiresOf(Type type)
  {
    if (!Requires.TryGetValue(type, out var list))
    {
      try
      {
        list = (RequireComponent[])type.GetCustomAttributes(typeof(RequireComponent), true);
      }
      catch (Exception)
      {
        list = [];
      }
      Requires[type] = list;
    }
    return list;
  }

  // Every box collider under a Chair (a chair's whole body, a bench's seat boxes: the bench's own box carries no Chair) grows 4 cm on each
  // side in the world's metres, so that a hover ray meets the Chair's box before the weld of the same shape.
  private static void Inflate(GameObject go)
  {
    var done = new HashSet<BoxCollider>();
    foreach (var chair in go.GetComponentsInChildren<Chair>(true))
      foreach (var box in chair.GetComponentsInChildren<BoxCollider>(true))
      {
        if (!done.Add(box))
          continue;
        Vector3 scale = box.transform.lossyScale;
        box.size += new Vector3(0.08f / Mathf.Max(Mathf.Abs(scale.x), 0.05f), 0.08f / Mathf.Max(Mathf.Abs(scale.y), 0.05f), 0.08f / Mathf.Max(Mathf.Abs(scale.z), 0.05f));
      }
  }

  /// <summary>The distance from a point to a box of six floats (minimum, then maximum), squared; 0 inside it.</summary>
  internal static float SqrDistance(float[] box, Vector3 p)
  {
    float dx = Math.Max(Math.Max(box[0] - p.x, 0f), p.x - box[3]);
    float dy = Math.Max(Math.Max(box[1] - p.y, 0f), p.y - box[4]);
    float dz = Math.Max(Math.Max(box[2] - p.z, 0f), p.z - box[5]);
    return dx * dx + dy * dy + dz * dz;
  }

  /// <summary>One zone's lit copies and seats: per piece its entry, its world matrix and, while you are near, its copy (a child of the zone's object).</summary>
  internal sealed class Props
  {
    private readonly BuiltZone zone;
    private readonly Transform parent;
    private readonly GameObject?[] lit, seats;
    private int litShown, seatsShown;
    private bool litReported, seatReported;
    private readonly bool[] litBroken, seatBroken;

    internal Props(BuiltZone zone, Transform parent)
    {
      this.zone = zone;
      this.parent = parent;
      lit = new GameObject[zone.Copies.Length];
      seats = new GameObject[zone.Seats.Length];
      litBroken = new bool[lit.Length];
      seatBroken = new bool[seats.Length];
    }

    internal bool Shown => litShown > 0 || seatsShown > 0;

    /// <summary>Makes the lit copies within <paramref name="near"/> metres of the camera (0: none, and the ones made go) and drops those
    /// <see cref="LightHysteresis"/> farther out.</summary>
    internal void UpdateLit(Vector3 eye, float near)
    {
      if (lit.Length == 0)
        return;
      float far = near + LightHysteresis;
      if (litShown == 0 && (near <= 0f || SqrDistance(zone.CopyBox, eye) > near * near))
        return;
      for (int i = 0; i < lit.Length; i++)
      {
        GameObject? copy = lit[i];
        if (copy == null && !ReferenceEquals(copy, null))   // destroyed by someone else
        {
          lit[i] = null;
          litShown--;
        }
        var p = zone.Copies[i].Matrix;
        float dx = p.m03 - eye.x, dy = p.m13 - eye.y, dz = p.m23 - eye.z;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (lit[i] == null)
        {
          if (near <= 0f || d2 >= near * near || litBroken[i] || !CanMakeLight())
            continue;
          try
          {
            lit[i] = MakeLit(zone.Copies[i].Kind, p, parent);
            litShown++;
          }
          catch (Exception e)
          {
            if (!litReported)
              LogWarning($"baked placements: could not light \"{zone.Copies[i].Kind.Name}\": {e.Message}");
            litReported = true;
            litBroken[i] = true;
          }
        }
        else if (near <= 0f || d2 > far * far)
        {
          UnityEngine.Object.Destroy(lit[i]);
          lit[i] = null;
          litShown--;
        }
      }
    }

    /// <summary>Makes the seats within <paramref name="near"/> metres of the player and drops those 3 m farther out, never one the player sits on.</summary>
    internal void UpdateSeats(Vector3 at, float near)
    {
      if (seats.Length == 0)
        return;
      float far = near + SeatHysteresis;
      if (seatsShown == 0 && (near <= 0f || SqrDistance(zone.SeatBox, at) > near * near))
        return;
      Transform? sitting = Player.m_localPlayer != null ? Player.m_localPlayer.GetAttachPoint() : null;
      for (int i = 0; i < seats.Length; i++)
      {
        GameObject? copy = seats[i];
        if (copy == null && !ReferenceEquals(copy, null))   // destroyed by someone else
        {
          seats[i] = null;
          seatsShown--;
        }
        var p = zone.Seats[i].Matrix;
        float dx = p.m03 - at.x, dy = p.m13 - at.y, dz = p.m23 - at.z;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (seats[i] == null)
        {
          if (near <= 0f || d2 >= near * near || seatBroken[i] || !CanMakeSeat())
            continue;
          try
          {
            seats[i] = MakeSeat(zone.Seats[i].Kind, p, parent);
            seatsShown++;
          }
          catch (Exception e)
          {
            if (!seatReported)
              LogWarning($"baked placements: could not make a seat of \"{zone.Seats[i].Kind.Name}\": {e.Message}");
            seatReported = true;
            seatBroken[i] = true;
          }
        }
        else if ((near <= 0f || d2 > far * far) && (sitting == null || !sitting.IsChildOf(seats[i]!.transform)))
        {
          UnityEngine.Object.Destroy(seats[i]);
          seats[i] = null;
          seatsShown--;
        }
      }
    }

    /// <summary>Whether the local player sits on one of this zone's seats (the zone then stays).</summary>
    internal bool SatOn()
    {
      if (seatsShown == 0 || Player.m_localPlayer == null)
        return false;
      var sitting = Player.m_localPlayer.GetAttachPoint();
      if (sitting == null)
        return false;
      foreach (var s in seats)
        if (s != null && sitting.IsChildOf(s.transform))
          return true;
      return false;
    }

    /// <summary>The copies go with the zone's object; this lets go of them.</summary>
    internal void Release()
    {
      Array.Clear(lit, 0, lit.Length);
      Array.Clear(seats, 0, seats.Length);
      litShown = seatsShown = 0;
    }
  }
}

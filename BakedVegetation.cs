// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownSeeding.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterContinents;

// The vegetation mask (build spec 7.4): where the layer says the game's own trees, rocks and bushes grow none. VALtimaOnline kept them out
// of its town zones with a prefix on PlaceVegetation that read its own pack; here it is the layer's data, read by a postfix on
// ZoneSystem.InsideClearArea, which only PlaceVegetation calls (ZoneSystem.cs:1522, 1589), the last test before a plant is placed. The
// game's own placement runs on the machine that runs the world, so this does too; a client never asks.
//
// A point is covered when
//  * its zone has zone flag 16 (no vegetation anywhere in it: the compiler's town zones), or
//  * its 4 m cell is set in the zone's effective mask: the file's clear mask (the compiler's, written for the cells around its pieces and
//    where its ground moved), joined with the cells within 4 m of the footprint of every record an in-game bake owns (the record's
//    point and the radius of its prefab's colliders, read once per prefab; prefabs and their colliders exist on a dedicated server too).
//    An in-game bake writes no section: its footprints are known from its records.
//
// The effective mask of a zone is worked out the first time the zone is asked for and kept for the layer it was made from (a new layer
// starts over). It is a toggle of its own (Patcher.LayerToggles), on for every world with a layer, a GameTerrain world included.
internal static class BakedVegetation
{
  /// <summary>How far past a footprint no vegetation grows, in metres (build spec 7.4).</summary>
  internal const double Margin = 4.0;

  /// <summary>A cell is 4 m wide: a zone is 16 x 16 of them.</summary>
  internal const int CellMetres = 4, Cells = 16;

  // The postfix on ZoneSystem.InsideClearArea(List<ClearArea>, Vector3), of the shape of ZoneSystemPatch.InsideClearAreaPostfix: true when
  // the point is in a zone with flag 16 or in a set cell of its effective mask.
  public static bool InsideClearAreaPostfix(bool __result, Vector3 point) => __result || Covers(point.x, point.z);

  // Whether a point is inside a zone with flag 16 or a set cell of its effective mask (BakedPlacements.IsFootprint).
  public static bool IsFootprint(float x, float z) => Covers(x, z);

  internal static bool Covers(double x, double z)
  {
    var layer = BakedLayerStore.Current;
    if (layer == null)
      return false;
    var zone = ZoneKey.OfPoint(x, z);
    return EffectiveMask(layer, zone).Covers(x - zone.OriginX, z - zone.OriginZ);
  }

  // ---- a zone's mask --------------------------------------------------------------------------------------------------------------

  /// <summary>The cells of a zone that grow no vegetation: all of them, or those whose bit is set (16 x 16 cells of 4 m from the zone's
  /// south-west corner, row 0 south; cell (row, col) is bit row * 16 + col, in byte bit >> 3, value 1 << (bit & 7)), or none.</summary>
  internal sealed class ZoneMask
  {
    public static readonly ZoneMask None = new(false, null);
    public static readonly ZoneMask Everywhere = new(true, null);

    public readonly bool All;
    public readonly byte[]? Bits;

    public ZoneMask(bool all, byte[]? bits)
    {
      All = all;
      Bits = bits;
    }

    public bool Any => All || Bits != null;

    /// <summary>The mask of these bits: none when no bit is set.</summary>
    public static ZoneMask Of(byte[]? bits)
    {
      if (bits != null)
        foreach (var b in bits)
          if (b != 0)
            return new ZoneMask(false, bits);
      return None;
    }

    /// <summary>Whether the cell holding the zone-local point (metres from the south-west corner) is covered.</summary>
    public bool Covers(double localX, double localZ)
    {
      if (All)
        return true;
      if (Bits == null)
        return false;
      int col = (int)Math.Floor(localX / CellMetres), row = (int)Math.Floor(localZ / CellMetres);
      return col >= 0 && col < Cells && row >= 0 && row < Cells && Has(Bits, row * Cells + col);
    }

    internal static bool Has(byte[] bits, int bit) => (bits[bit >> 3] & (1 << (bit & 7))) != 0;

    /// <summary>The cells this covers that <paramref name="old"/> did not: what a new layer adds (null: none).</summary>
    public ZoneMask Grown(ZoneMask old)
    {
      if (old.All || !Any)
        return None;
      if (All)
        return old.Bits == null ? Everywhere : Complement(old.Bits);
      if (old.Bits == null)
        return this;
      var grown = new byte[BakedFormat.MaskBytes];
      for (int i = 0; i < grown.Length; i++)
        grown[i] = (byte)(Bits![i] & ~old.Bits[i]);
      return Of(grown);
    }

    private static ZoneMask Complement(byte[] bits)
    {
      var rest = new byte[BakedFormat.MaskBytes];
      for (int i = 0; i < rest.Length; i++)
        rest[i] = (byte)~bits[i];
      return Of(rest);
    }
  }

  /// <summary>What the file says of a zone, as the compiler wrote it: zone flag 16 and its clear mask; nothing for an in-game bake's records.</summary>
  internal static ZoneMask FileMask(BakedLayer? layer, ZoneKey zone)
  {
    if (layer == null || !layer.TryGetZoneRow(zone, out var row))
      return ZoneMask.None;
    if (row.NoVegetation)
      return ZoneMask.Everywhere;
    return ZoneMask.Of(row.ClearMask);
  }

  /// <summary>The file's mask joined with the cells within 4 m of the footprints of the in-game records around: what the zone grows no
  /// vegetation in.</summary>
  internal static ZoneMask EffectiveMask(BakedLayer layer, ZoneKey zone)
  {
    lock (gate)
    {
      if (!ReferenceEquals(made?.Layer, layer))
        made = new Made(layer);
      if (made.Masks.TryGetValue(zone, out var known))
        return known;
      return made.Masks[zone] = Compose(layer, zone);
    }
  }

  private sealed class Made(BakedLayer layer)
  {
    public readonly BakedLayer Layer = layer;
    public readonly Dictionary<ZoneKey, ZoneMask> Masks = [];
    public readonly Dictionary<ZoneKey, Footprint[]> Footprints = [];
  }

  private static readonly object gate = new();
  private static Made? made;

  private static ZoneMask Compose(BakedLayer layer, ZoneKey zone)
  {
    var file = FileMask(layer, zone);
    if (file.All)
      return file;
    byte[]? bits = null;
    for (int dz = -1; dz <= 1; dz++)
      for (int dx = -1; dx <= 1; dx++)
        foreach (var footprint in FootprintsOf(layer, new ZoneKey(zone.X + dx, zone.Z + dz)))
          bits = Mark(bits, zone, footprint);
    if (bits == null)
      return file;
    if (file.Bits != null)
      for (int i = 0; i < bits.Length; i++)
        bits[i] |= file.Bits[i];
    return ZoneMask.Of(bits);
  }

  /// <summary>Where an in-game record stands and how far its pieces reach: a circle in the world.</summary>
  internal readonly struct Footprint(double x, double z, double radius)
  {
    public readonly double X = x, Z = z, Radius = radius;
  }

  // The footprints of a zone's in-game records, once per zone and layer. A zone whose records are all the compiler's has none, and is not
  // even read (the layer knows how many records each bake owns in a zone).
  private static Footprint[] FootprintsOf(BakedLayer layer, ZoneKey zone)
  {
    if (made!.Footprints.TryGetValue(zone, out var known))
      return known;
    if (!layer.TryGetZoneRow(zone, out var row) || row.Placements == 0 || layer.InfoOf(row.Index).SourceCounts.Length == 0)
      return Array.Empty<Footprint>();
    return made.Footprints[zone] = FootprintsIn(layer, layer.Decode(row), RadiusOf);
  }

  /// <summary>The footprints of the in-game records (those with flag 8) of a decoded zone. <paramref name="radiusOf"/> says how far a
  /// palette entry's prefab reaches from its origin; a record's scale grows it.</summary>
  internal static Footprint[] FootprintsIn(BakedLayer layer, ZoneData data, Func<PaletteEntry, float> radiusOf)
  {
    var list = new List<Footprint>();
    var radii = new Dictionary<int, float>();
    for (int k = 0; k < data.Count; k++)
    {
      if ((data.Flags[k] & RecordFlags.Source) == 0)
        continue;
      int entry = data.Palette[k];
      if (!radii.TryGetValue(entry, out float radius))
        radii[entry] = radius = radiusOf(layer.Palette[entry]);
      var scale = (data.Flags[k] & RecordFlags.Scale) != 0 ? data.Record(k).Scale : Vector3.one;
      list.Add(new Footprint(data.WorldX(k), data.WorldZ(k), radius * Math.Max(Math.Abs(scale.x), Math.Abs(scale.z))));
    }
    return list.ToArray();
  }

  /// <summary>Sets, in a zone's bits (made when null), the cells within 4 m of a footprint.</summary>
  internal static byte[]? Mark(byte[]? bits, ZoneKey zone, Footprint footprint)
  {
    double reach = footprint.Radius + Margin;
    double lx = footprint.X - zone.OriginX, lz = footprint.Z - zone.OriginZ;
    int col0 = Math.Max(0, (int)Math.Floor((lx - reach) / CellMetres)), col1 = Math.Min(Cells - 1, (int)Math.Floor((lx + reach) / CellMetres));
    int row0 = Math.Max(0, (int)Math.Floor((lz - reach) / CellMetres)), row1 = Math.Min(Cells - 1, (int)Math.Floor((lz + reach) / CellMetres));
    for (int row = row0; row <= row1; row++)
      for (int col = col0; col <= col1; col++)
      {
        // The distance from the point to the cell's rectangle.
        double dx = Math.Max(Math.Max(col * CellMetres - lx, 0.0), lx - (col + 1) * CellMetres);
        double dz = Math.Max(Math.Max(row * CellMetres - lz, 0.0), lz - (row + 1) * CellMetres);
        if (dx * dx + dz * dz > reach * reach)
          continue;
        bits ??= new byte[BakedFormat.MaskBytes];
        int bit = row * Cells + col;
        bits[bit >> 3] |= (byte)(1 << (bit & 7));
      }
    return bits;
  }

  // ---- how far a prefab reaches ----------------------------------------------------------------------------------------------------

  /// <summary>How far the pieces of a palette entry reach from the record's point, in metres: the entry's boxes or its trunk when it has
  /// them, else the colliders of the first of its prefabs the game has (read once per prefab). (A field, for the offline tests.)</summary>
  internal static Func<PaletteEntry, float> RadiusOf = DefaultRadius;

  private static readonly Dictionary<string, float> prefabRadii = [];

  /// <summary>Forgets the radii read (a new session may have other prefabs).</summary>
  internal static void ForgetRadii()
  {
    lock (gate)
    {
      prefabRadii.Clear();
      made = null;
    }
  }

  private static float DefaultRadius(PaletteEntry entry)
  {
    if (entry.Collision == BakedCollision.Trunk)
      return 0.3f;
    if (entry.Collision == BakedCollision.Boxes && entry.Boxes.Length > 0)
    {
      var anchor = entry.Candidates[0].Anchor;
      float reach = 0f;
      foreach (var box in entry.Boxes)
      {
        var centre = box.Centre - anchor;
        var half = box.Size * 0.5f;
        reach = Math.Max(reach, new Vector2(Math.Abs(centre.x) + half.x, Math.Abs(centre.z) + half.z).magnitude);
      }
      return reach;
    }
    var scene = ZNetScene.instance;
    if (scene == null)
      return 0f;
    foreach (var candidate in entry.Candidates)
    {
      var prefab = scene.GetPrefab(candidate.Name.GetStableHashCode());
      if (prefab == null)
        continue;
      lock (gate)
      {
        if (prefabRadii.TryGetValue(candidate.Name, out float known))
          return known;
        return prefabRadii[candidate.Name] = ColliderReach(prefab);
      }
    }
    return 0f;
  }

  // The farthest point of a prefab's colliders from its origin, on the ground plane. The prefab is an asset, not an object in a scene, so
  // each collider's bounds are made from its shape and its place under the prefab's root.
  private static float ColliderReach(GameObject prefab)
  {
    float reach = 0f;
    var root = prefab.transform;
    foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
    {
      var t = collider.transform;
      void Take(Vector3 local)
      {
        var p = root.InverseTransformPoint(t.TransformPoint(local));
        reach = Math.Max(reach, new Vector2(p.x, p.z).magnitude);
      }
      switch (collider)
      {
        case BoxCollider box:
          for (int i = 0; i < 8; i++)
            Take(box.center + Vector3.Scale(box.size, new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f)));
          break;
        case SphereCollider sphere:
          for (int i = 0; i < 4; i++)
            Take(sphere.center + new Vector3(i == 0 ? sphere.radius : i == 1 ? -sphere.radius : 0f, 0f, i == 2 ? sphere.radius : i == 3 ? -sphere.radius : 0f));
          break;
        case CapsuleCollider capsule:
          for (int i = 0; i < 4; i++)
            Take(capsule.center + new Vector3(i == 0 ? capsule.radius : i == 1 ? -capsule.radius : 0f, 0f, i == 2 ? capsule.radius : i == 3 ? -capsule.radius : 0f));
          break;
        case MeshCollider mesh when mesh.sharedMesh != null:
          var bounds = mesh.sharedMesh.bounds;
          for (int i = 0; i < 8; i++)
            Take(bounds.center + Vector3.Scale(bounds.size, new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f)));
          break;
      }
    }
    return reach;
  }
}

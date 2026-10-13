// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// The keys of the ZDO values that make a piece a baked layer's (spec 6.4): its source and id, the revision that made or kept it, and
// whether it is protected; and the key of a piece that was a layer's and is not any more (an orphan). Names for the logs and tools, and the
// game's stable hashes of them for ZDO.Set and ZDO.Get.
internal static class BakedKeys
{
  public const string SrcName = BakedFormat.KeySource;
  public const string IdName = BakedFormat.KeyId;
  public const string RevName = BakedFormat.KeyRevision;
  public const string ProtectName = BakedFormat.KeyProtect;
  // A string "<source>/<id>": the piece a layer once held and no longer does, which stays where it is (a container that holds items).
  public const string OrphanName = "bc_bake_orphan";
  public const string SeedName = BakedFormat.KeySeed;
  public const string MatVarName = BakedFormat.KeyMatVar;

  // bc_bake_src (int): the in-game bake that owns the piece, 0 for the compiler's file.
  public static readonly int Src = SrcName.GetStableHashCode();
  // bc_bake_id (int): the record's lasting id (the u32 cast unchecked).
  public static readonly int Id = IdName.GetStableHashCode();
  // bc_bake_rev (int): the revision of the layer that seeded or last kept the piece.
  public static readonly int Rev = RevName.GetStableHashCode();
  // bc_protect (bool, stored as an int 1 or 0): no damage, no removal.
  public static readonly int Protect = ProtectName.GetStableHashCode();
  public static readonly int Orphan = OrphanName.GetStableHashCode();
  // RandMatSeed (int, 0 to 12344): RandomMaterialValues' seed.
  public static readonly int Seed = SeedName.GetStableHashCode();

  // Valheim saves its portals in a file of their own, which a save rewrites only when a portal was made, removed or relinked
  // (ZDOMan.SetDirtyPortals); a value set on a portal that exists marks only its sector, which holds no portal, so the change is lost at the
  // next save and the portal loads without it (a town portal adopted by a bake would load without its keys and be seeded a second time).
  // Every place that changes a key on an object that exists already calls this after it. Nothing for any other object.
  public static void Changed(ZDO zdo)
  {
    var game = Game.instance;
    if (game != null && ZDOMan.instance != null && game.PortalPrefabHash.Contains(zdo.GetPrefab()))
      ZDOMan.instance.SetDirtyPortals();
  }

  // The key of MaterialVariation slot i ("MatVar" + i).
  public static int MatVar(int slot) => (MatVarName + slot).GetStableHashCode();
  public static string MatVarKey(int slot) => MatVarName + slot;

  // What an orphan's key holds, and back.
  public static string OrphanValue(int source, uint id) => source + "/" + id;

  public static bool TryParseOrphan(string? value, out int source, out uint id)
  {
    source = 0;
    id = 0;
    if (string.IsNullOrEmpty(value))
      return false;
    int slash = value!.IndexOf('/');
    return slash > 0 && int.TryParse(value.Substring(0, slash), out source) && uint.TryParse(value.Substring(slash + 1), out id);
  }

  // The four keys a seeded piece carries (its bake keys), in the order they are written.
  public static IEnumerable<int> BakeKeys => new[] { Src, Id, Rev, Protect };
}

// What the rest of the mod calls to raise the public events of BakedPlacements, and a few lookups only Better Continents makes.
internal static class BakedApi
{
  // Slice B: after every key of a seeded piece is written, while the seeding instance still exists (spec 0.1 item 4). `instance` is null
  // where none exists.
  internal static void RaiseLiveSeeded(ZDO zdo, GameObject? instance, int source, uint id) => BakedPlacements.RaiseLiveSeeded(zdo, instance, source, id);

  internal static void RaiseLayerChanged() => BakedPlacements.RaiseLayerChanged();

  // Slice D: after step 8 of a bake, and after an unbake or an undo.
  internal static void RaiseBaked(OperationInfo operation) => BakedPlacements.RaiseBaked(BakeInfo.Of(operation));
  internal static void RaiseUnbaked(OperationInfo operation) => BakedPlacements.RaiseUnbaked(BakeInfo.Of(operation));

  // Calls the handlers of an event one by one: a handler of another mod that throws must stop neither the work of this one nor the handlers
  // after it. A failure is logged, with `what` before it.
  internal static void Each<T>(T? handlers, Action<T> call, string what = "A handler of the baked placements API threw") where T : Delegate
  {
    if (handlers == null)
      return;
    foreach (var handler in handlers.GetInvocationList())
    {
      try
      {
        call((T)handler);
      }
      catch (Exception e)
      {
        BetterContinents.LogError(what + ": " + e);
      }
    }
  }
}

// One record of a zone, for other mods (spec 11.3).
public sealed class BakedRecord
{
  // The first candidate prefab the game has; the entry's first when it has none.
  public string Prefab { get; }
  public BakedRole Role { get; }
  // The record's point, and where the prefab's own origin goes (the point less the candidate's anchor, turned and scaled).
  public Vector3 Point { get; }
  public Vector3 Position { get; }
  public Quaternion Rotation { get; }
  public Vector3 Scale { get; }
  // The lasting id of a Live record; HasId says whether there is one.
  public bool HasId { get; }
  public uint Id { get; }
  // The in-game bake that owns the record; 0 is the compiler's file.
  public int Source { get; }
  // The ZDO values every piece of the record's kind has: the entry's tags (bool, int, float or string).
  public IReadOnlyDictionary<string, object> Tags { get; }

  internal BakedRecord(string prefab, BakedRole role, Vector3 point, Vector3 position, Quaternion rotation, Vector3 scale, bool hasId, uint id, int source,
    IReadOnlyDictionary<string, object> tags)
  {
    Prefab = prefab;
    Role = role;
    Point = point;
    Position = position;
    Rotation = rotation;
    Scale = scale;
    HasId = hasId;
    Id = id;
    Source = source;
    Tags = tags;
  }
}

// One operation of the layer's registry, for other mods.
public sealed class BakeInfo
{
  public int Number { get; }
  public OperationKind Kind { get; }
  public OperationState State { get; }
  public DateTime Time { get; }
  public string Who { get; }
  public float X1 { get; }
  public float Z1 { get; }
  public float X2 { get; }
  public float Z2 { get; }
  public float Radius { get; }
  public int Added { get; }
  public int Removed { get; }
  public int Adopted { get; }
  public string Version { get; }

  private BakeInfo(OperationInfo op)
  {
    Number = op.Number;
    Kind = op.Kind;
    State = op.State;
    Time = DateTimeOffset.FromUnixTimeSeconds(op.Time).UtcDateTime;
    Who = op.Who;
    X1 = op.X1;
    Z1 = op.Z1;
    X2 = op.X2;
    Z2 = op.Z2;
    Radius = op.Radius;
    Added = (int)Math.Min(op.Added, int.MaxValue);
    Removed = (int)Math.Min(op.Removed, int.MaxValue);
    Adopted = (int)Math.Min(op.Adopted, int.MaxValue);
    Version = op.Version;
  }

  internal static BakeInfo Of(OperationInfo op) => new(op);
}

// The read-only API of the baked layer for other mods (spec 11.3). Everything is backed by the layer this machine holds (BakedLayerStore);
// with no layer, the answers are those of a world without one.
public static class BakedPlacements
{
  public static bool HasLayer => BakedLayerStore.Current != null;

  // The current layer's revision; 0 without one.
  public static int Revision => (int)(BakedLayerStore.Current?.Revision ?? 0);

  // After a new layer is current.
  public static event Action? LayerChanged;

  // After every key of a piece the server seeded is written: (zdo, the instance that exists while it is seeded or null, source, id), on
  // the machine that runs the world. A host in Full mode makes the instance; the seeding instance is also ZNetScene.FindInstance(zdo).
  public static event Action<ZDO, GameObject?, int, uint>? LiveSeeded;

  // After a bake is done, and after an unbake or an undo.
  public static event Action<BakeInfo>? Baked;
  public static event Action<BakeInfo>? Unbaked;

  internal static void RaiseLayerChanged() => BakedApi.Each(LayerChanged, handler => handler());
  internal static void RaiseLiveSeeded(ZDO zdo, GameObject? instance, int source, uint id) => BakedApi.Each(LiveSeeded, handler => handler(zdo, instance, source, id));
  internal static void RaiseBaked(BakeInfo info) => BakedApi.Each(Baked, handler => handler(info));
  internal static void RaiseUnbaked(BakeInfo info) => BakedApi.Each(Unbaked, handler => handler(info));

  // The registry of the current layer: what was baked, unbaked, loaded and dropped, in order.
  public static IReadOnlyList<BakeInfo> Operations =>
    BakedLayerStore.Current?.Registry.Operations.Select(BakeInfo.Of).ToList() ?? (IReadOnlyList<BakeInfo>)Array.Empty<BakeInfo>();

  // The records of a zone, with their prefab, role, place, id, source and tags. False when the layer has none there.
  public static bool TryGetZone(int zx, int zz, out IReadOnlyList<BakedRecord> records)
  {
    records = Array.Empty<BakedRecord>();
    var layer = BakedLayerStore.Current;
    if (layer == null || !layer.TryGetZoneRow(zx, zz, out var row) || row.Placements == 0)
      return false;
    var data = layer.Decode(row);
    var list = new List<BakedRecord>(data.Count);
    var resolved = new Dictionary<int, (string Prefab, Vector3 Anchor, IReadOnlyDictionary<string, object> Tags)>();
    for (int k = 0; k < data.Count; k++)
    {
      var record = data.Record(k);
      if (!resolved.TryGetValue(record.Palette, out var entry))
      {
        var palette = layer.Palette[record.Palette];
        var candidate = Resolve(palette);
        entry = (candidate.Name, candidate.Anchor, TagsOf(palette));
        resolved[record.Palette] = entry;
      }
      var rotation = record.Rotation;
      var scale = record.Scale;
      var point = record.Position;
      var pivot = point - rotation * Vector3.Scale(scale, entry.Anchor);
      list.Add(new BakedRecord(entry.Prefab, layer.Palette[record.Palette].Role, point, pivot, rotation, scale, record.HasId, record.Id, record.SourceNumber, entry.Tags));
    }
    records = list;
    return true;
  }

  // The first candidate the game has (the game's prefab table is asked when it exists), else the entry's first.
  private static Candidate Resolve(PaletteEntry entry)
  {
    var scene = ZNetScene.instance;
    if (scene != null)
      foreach (var candidate in entry.Candidates)
        if (scene.GetPrefab(candidate.Name.GetStableHashCode()) != null)
          return candidate;
    return entry.Candidates[0];
  }

  private static IReadOnlyDictionary<string, object> TagsOf(PaletteEntry entry)
  {
    var tags = new Dictionary<string, object>();
    foreach (var tag in entry.Tags)
      tags[tag.Key] = tag.Type switch
      {
        TagType.Bool => tag.Bool,
        TagType.Int => tag.Number,
        TagType.Float => tag.Float,
        _ => tag.Text,
      };
    return tags;
  }

  // The ground at a point with the layer's ground: the game's own height answer, which every machine gives the same once the layer's
  // ground is in its GetBiomeHeight (V8). 0 where there is no world.
  public static float GroundHeight(float x, float z) => WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;

  // The highest baked collider top at or below fromY on the vertical line through (x, z), from the layer's data (collision Boxes and
  // Trunk; a record whose collision is the prefab's own has no data here). Any machine.
  public static bool SurfaceHeight(float x, float z, float fromY, out float y)
  {
    y = 0f;
    var layer = BakedLayerStore.Current;
    if (layer == null)
      return false;
    double best = double.NegativeInfinity;
    var centre = ZoneKey.OfPoint(x, z);
    // A collider can reach over a zone's edge, so the zones around are asked too.
    for (int dz = -1; dz <= 1; dz++)
      for (int dx = -1; dx <= 1; dx++)
      {
        if (!layer.TryGetZoneRow(centre.X + dx, centre.Z + dz, out var row) || row.Placements == 0)
          continue;
        foreach (var top in SurfaceTops(layer, row, x, z, fromY))
          if (top > best)
            best = top;
      }
    if (double.IsNegativeInfinity(best))
      return false;
    y = (float)best;
    return true;
  }

  // The tops of the colliders of one zone's records on the vertical line through (x, z), at or below fromY.
  internal static IEnumerable<double> SurfaceTops(BakedLayer layer, ZoneRow row, double x, double z, double fromY)
  {
    var data = layer.Decode(row);
    for (int k = 0; k < data.Count; k++)
    {
      var palette = layer.Palette[data.Palette[k]];
      if (palette.Collision != BakedCollision.Boxes && palette.Collision != BakedCollision.Trunk)
        continue;
      // A cheap test first: nothing farther from the record's point than its colliders can reach (the entry's reach, times the largest scale)
      // is on the line. A box can be longer than a zone's 12 m: a wall scaled up reaches far.
      double px = data.WorldX(k), pz = data.WorldZ(k);
      double reach = palette.ColliderReach;
      if ((data.Flags[k] & RecordFlags.Scale) != 0)
      {
        var s = data.Record(k).Scale;
        reach *= Math.Max(1.0, Math.Max(s.x, Math.Max(s.y, s.z)));
      }
      if (Math.Abs(px - x) > reach || Math.Abs(pz - z) > reach)
        continue;
      var record = data.Record(k);
      var rotation = record.Rotation;
      var scale = record.Scale;
      var point = new Vector3((float)px, (float)data.WorldY(k), (float)pz);
      var anchor = palette.Candidates[0].Anchor;
      if (palette.Collision == BakedCollision.Trunk)
      {
        // A tree's trunk: 0.6 m across and 3 m up from its foot, whatever the tree's size and turn (VALtima's town pack makes it so, and so does the
        // client's collider). The foot is the piece's pivot: the point less the first candidate's anchor, turned and scaled.
        var foot = point - rotation * Vector3.Scale(scale, anchor);
        if (BoxTop(foot, new Vector3(0f, 1.5f, 0f), new Vector3(0.6f, 3f, 0.6f), new Quaternion(0f, 0f, 0f, 1f), Vector3.one, x, z, fromY, out double trunk))
          yield return trunk;
        continue;
      }
      foreach (var box in palette.Boxes)
        if (BoxTop(point, box.Centre - anchor, box.Size, rotation, scale, x, z, fromY, out double top))
          yield return top;
    }
  }

  // The top, at or below fromY, of an oriented box on the vertical line through (x, z). The box's centre is `offset` from the record's
  // point in the prefab's frame (turned and scaled with the record), and `size` is its size there.
  private static bool BoxTop(Vector3 point, Vector3 offset, Vector3 size, Quaternion rotation, Vector3 scale, double x, double z, double fromY, out double top)
  {
    top = 0;
    var centre = point + rotation * Vector3.Scale(scale, offset);
    double hx = Math.Abs(size.x * scale.x) / 2.0, hy = Math.Abs(size.y * scale.y) / 2.0, hz = Math.Abs(size.z * scale.z) / 2.0;
    // The line in the box's frame: a + y * d (y is the world height).
    double qx = -rotation.x, qy = -rotation.y, qz = -rotation.z, qw = rotation.w;
    var a = Rotate(qx, qy, qz, qw, x - centre.x, -(double)centre.y, z - centre.z);
    var d = Rotate(qx, qy, qz, qw, 0.0, 1.0, 0.0);
    double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
    var half = new[] { hx, hy, hz };
    for (int i = 0; i < 3; i++)
    {
      if (Math.Abs(d[i]) < 1e-9)
      {
        if (Math.Abs(a[i]) > half[i])
          return false;
        continue;
      }
      double t0 = (-half[i] - a[i]) / d[i], t1 = (half[i] - a[i]) / d[i];
      if (t0 > t1)
      {
        double swap = t0;
        t0 = t1;
        t1 = swap;
      }
      lo = Math.Max(lo, t0);
      hi = Math.Min(hi, t1);
    }
    if (lo > hi || hi > fromY + 1e-4)
      return false;
    top = hi;
    return true;
  }

  // v rotated by the unit quaternion (qx, qy, qz, qw).
  private static double[] Rotate(double qx, double qy, double qz, double qw, double vx, double vy, double vz)
  {
    double tx = 2 * (qy * vz - qz * vy), ty = 2 * (qz * vx - qx * vz), tz = 2 * (qx * vy - qy * vx);
    return new[]
    {
      vx + qw * tx + (qy * tz - qz * ty),
      vy + qw * ty + (qz * tx - qx * tz),
      vz + qw * tz + (qx * ty - qy * tx),
    };
  }

  // Whether a point is inside a zone with flag 16 (no vegetation) or in a set cell of the zone's effective mask.
  public static bool IsFootprint(float x, float z) => BakedVegetation.IsFootprint(x, z);

  // The lasting id and the source of a seeded piece; 0 when it has none.
  public static uint BakeIdOf(ZDO zdo) => zdo == null ? 0u : unchecked((uint)zdo.GetInt(BakedKeys.Id, 0));
  public static int SourceOf(ZDO zdo) => zdo == null ? 0 : zdo.GetInt(BakedKeys.Src, 0);
}

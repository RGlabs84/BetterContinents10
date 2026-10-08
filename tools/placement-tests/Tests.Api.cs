// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The read-only API of the baked layer for other mods (spec 11.3): BakedPlacements.TryGetZone (the candidate the game has, the pivot, the tags by
// type), SurfaceHeight (boxes and trunks, turned, scaled and over a zone's edge, against a walk down the line that shares none of its maths),
// BakeIdOf and SourceOf, the events and the operations. Names that only this file uses start with Api so that the slices' files can share
// one partial class.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // ------------------------------------------------------------------------------------------------ stand-ins for the game's objects

  // Unity's `if (obj)` is false for an object whose native half is gone: its pointer is zero. A made-up object has none, so it is dead; one that
  // has a pointer looks alive. Reflection's SetValue would run UnityEngine.Object's type initializer, which reaches into the engine; IL does not.
  private static readonly Action<UnityEngine.Object, IntPtr> ApiSetPointer = ApiMakePointerSetter();

  private static Action<UnityEngine.Object, IntPtr> ApiMakePointerSetter()
  {
    var field = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var method = new DynamicMethod("ApiSetPointer", typeof(void), [typeof(UnityEngine.Object), typeof(IntPtr)], typeof(Tests).Module, true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Stfld, field);
    il.Emit(OpCodes.Ret);
    return (Action<UnityEngine.Object, IntPtr>)method.CreateDelegate(typeof(Action<UnityEngine.Object, IntPtr>));
  }

  // An object of the game's that looks alive to `if (obj)`. It has none of Unity's insides: it is never compared or put in a collection that compares.
  private static T ApiAlive<T>() where T : UnityEngine.Object
  {
    var made = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    ApiSetPointer(made, (IntPtr)1);
    return made;
  }

  // ZNetScene.instance with the prefabs the game "has": GetPrefab(hash) answers from m_namedPrefabs.
  private sealed class ApiScene : IDisposable
  {
    private static readonly FieldInfo InstanceField = typeof(ZNetScene).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object before;
    public readonly Dictionary<int, GameObject> Prefabs = new();

    public ApiScene()
    {
      var scene = ApiAlive<ZNetScene>();
      typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, Prefabs);
      before = InstanceField.GetValue(null);
      InstanceField.SetValue(null, scene);
    }

    public void Add(string name) => Prefabs[name.GetStableHashCode()] = ApiAlive<GameObject>();

    public void Dispose() => InstanceField.SetValue(null, before);
  }

  // A ZDO made without Unity, with the bake keys it is given in the game's own store.
  private static ZDO ApiZdo(uint number, int? source = null, int? id = null)
  {
    var zdo = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
    zdo.m_uid = new ZDOID(4000L, number);
    if (source is { } s)
      ZDOExtraData.Set(zdo.m_uid, BakedKeys.Src, s);
    if (id is { } i)
      ZDOExtraData.Set(zdo.m_uid, BakedKeys.Id, i);
    return zdo;
  }

  // ------------------------------------------------------------------------------------------------ TryGetZone

  private static void ApiZoneTest()
  {
    Section("api: TryGetZone gives a zone's records, with the first candidate the game has, the pivot, the ids and the tags by type");
    var previous = BC.Settings;
    try
    {
      BC.Settings = new BC.BetterContinentsSettings();
      C(!BakedPlacements.HasLayer && BakedPlacements.Revision == 0, "without a layer: HasLayer is false and Revision is 0");
      C(!BakedPlacements.TryGetZone(0, 0, out var nothing) && nothing.Count == 0, "without a layer no zone has records, and the list is empty");

      // Entry 0: three candidates (the first has an anchor of its own), tags of every type. Entry 1: a Live door with an anchor of 0.
      var wall = new PaletteEntry([new Candidate("stone_wall_2x1", 100, -200, 300), new Candidate("wood_wall", 0, 0, 0), new Candidate("wood_wall_b", 50, 0, 0)],
        BakedRole.Static, BakedCollision.Prefab, 0, PaletteFlags.None, null, "", 1000, 1000, 1000, "",
        [Tag.OfBool("flag", true), Tag.OfInt("number", -7), Tag.OfFloat("amount", 2.5f), Tag.OfString("text", "hello")]);
      var door = new PaletteEntry([new Candidate("wood_door", 0, 0, 0)], BakedRole.Live, BakedCollision.Prefab, 0, PaletteFlags.Protect, tags: [Tag.OfBool("VALtima_TownPiece", true)]);
      var edit = LayerEdit.New("api zone");
      edit.PaletteIndexFor(wall);
      edit.PaletteIndexFor(door);
      edit.AddOperation(SampleOperation(1, OperationKind.BakeArea, 1));
      var turned = new Quaternion(0.2f, 0.4f, 0.1f, 0.88f);
      var zone = new ZoneKey(3, -2);
      edit.AddRecords([
        ZoneRecord.CreateYaw(0, 170.5, 40, -150.25, 90, new Vector3(2f, 1f, 3f)),
        ZoneRecord.Create(0, 180, 41, -140, turned, null, null, 1, 0, 321),
        ZoneRecord.CreateYaw(1, 175, 40.5, -155, 10, id: 77),
      ]);
      // A neighbour that holds only a clear mask: a row without records.
      edit.SetSections(new ZoneKey(4, -2), new ZoneSections { Mask = MaskOf(1, 2) });
      var layer = edit.Build().Layer;
      BC.Settings = World(layer);
      C(BakedPlacements.HasLayer && BakedPlacements.Revision == (int)layer.Revision, "with a layer: HasLayer, and Revision is the layer's");
      C(!BakedPlacements.TryGetZone(9, 9, out var missing) && missing.Count == 0, "a zone the layer does not list has no records");
      C(!BakedPlacements.TryGetZone(4, -2, out var sectionsOnly) && sectionsOnly.Count == 0, "a zone with a row but no records has none either");

      layer.TryGetZoneRow(zone, out var row);
      var truth = layer.Decode(row).Records().ToList();
      C(BakedPlacements.TryGetZone(3, -2, out var records) && records.Count == 3 && truth.Count == 3, "the zone gives its three records");

      string Check(IReadOnlyList<BakedRecord> list, string wallName, Vector3 wallAnchor, string label)
      {
        for (int k = 0; k < truth.Count; k++)
        {
          var t = truth[k];
          var r = list[k];
          bool isDoor = t.Palette == 1;
          string expectedPrefab = isDoor ? "wood_door" : wallName;
          var anchor = isDoor ? Vector3.zero : wallAnchor;
          if (r.Prefab != expectedPrefab)
            return $"{label}: record {k} draws {r.Prefab}, not {expectedPrefab}";
          if (r.Point != t.Position || r.Rotation != t.Rotation || r.Scale != t.Scale)
            return $"{label}: record {k}: point, rotation or scale differ from the layer's";
          // The prefab's own origin is the point less the anchor, turned and scaled.
          var offset = ApiTurn(t.Rotation, anchor.x * t.Scale.x, anchor.y * t.Scale.y, anchor.z * t.Scale.z);
          if (Math.Abs(r.Position.x - (t.Position.x - offset[0])) > 1e-3 || Math.Abs(r.Position.y - (t.Position.y - offset[1])) > 1e-3
              || Math.Abs(r.Position.z - (t.Position.z - offset[2])) > 1e-3)
            return $"{label}: record {k}: the pivot is {r.Position}, the point less the anchor is ({t.Position.x - offset[0]}, {t.Position.y - offset[1]}, {t.Position.z - offset[2]})";
          if (r.Role != (isDoor ? BakedRole.Live : BakedRole.Static) || r.HasId != t.HasId || r.Id != t.Id || r.Source != t.SourceNumber)
            return $"{label}: record {k}: role, id or source differ";
        }
        return null;
      }

      // Without the game's prefab table the first candidate is the one.
      C(records.Count == 3 && ZNetScene.instance == null, "no ZNetScene in this process");
      var bad = Check(records, "stone_wall_2x1", new Vector3(0.1f, -0.2f, 0.3f), "no scene");
      C(bad == null, bad ?? "with no prefab table the first candidate is named, and the pivot follows its anchor (0.1, -0.2, 0.3)");
      var wallRecords = records.Where(r => r.Role == BakedRole.Static).ToList();
      C(wallRecords.Count == 2 && wallRecords.All(r => r.Tags.Count == 4 && r.Tags["flag"] is true && r.Tags["number"] is -7 && r.Tags["amount"] is 2.5f && r.Tags["text"] is "hello"),
        "an entry's tags come as a bool, an int, a float and a string");
      C(wallRecords.All(r => r.Tags["flag"] is bool && r.Tags["number"] is int && r.Tags["amount"] is float && r.Tags["text"] is string), "and each is of its type");
      var doorRecord = records.Single(r => r.Role == BakedRole.Live);
      C(doorRecord.HasId && doorRecord.Id == 77 && doorRecord.Source == 0 && doorRecord.Tags.Count == 1 && doorRecord.Tags["VALtima_TownPiece"] is true, "a Live record has its id, source 0 and its bool tag");
      var inGame = wallRecords.Single(r => r.Source == 1);
      C(!inGame.HasId && inGame.Source == 1, "an in-game record names its bake and has no id");
      C(records.Select(r => r.Tags).Distinct().Count() == 2, "the records of one entry share its tag table");

      // With the table: the first candidate the game has, in the entry's order.
      using (var scene = new ApiScene())
      {
        scene.Add("unrelated_prefab");
        C(BakedPlacements.TryGetZone(3, -2, out var none) && Check(none, "stone_wall_2x1", new Vector3(0.1f, -0.2f, 0.3f), "scene without them") == null,
          "a prefab table that has none of an entry's candidates leaves the first one");
        scene.Add("wood_wall_b");
        C(BakedPlacements.TryGetZone(3, -2, out var third) && (bad = Check(third, "wood_wall_b", new Vector3(0.05f, 0f, 0f), "third")) == null,
          bad ?? "the third candidate when only it exists: its name, and the pivot follows its own anchor (0.05, 0, 0)");
        scene.Add("wood_wall");
        C(BakedPlacements.TryGetZone(3, -2, out var second) && (bad = Check(second, "wood_wall", Vector3.zero, "second")) == null,
          bad ?? "the second when the second and third exist: the order of the entry decides");
        scene.Add("stone_wall_2x1");
        C(BakedPlacements.TryGetZone(3, -2, out var first) && (bad = Check(first, "stone_wall_2x1", new Vector3(0.1f, -0.2f, 0.3f), "first")) == null, bad ?? "the first when it exists");
        C(first.Single(r => r.Role == BakedRole.Live).Prefab == "wood_door", "an entry whose only candidate the table lacks keeps it");
      }
      C(ZNetScene.instance == null, "the stand-in scene is gone again");
    }
    finally
    {
      BC.Settings = previous;
    }
  }

  // ------------------------------------------------------------------------------------------------ SurfaceHeight

  // v turned by the unit quaternion q.
  private static double[] ApiTurn(Quaternion q, double x, double y, double z)
  {
    double ux = q.x, uy = q.y, uz = q.z, w = q.w;
    double cx = uy * z - uz * y, cy = uz * x - ux * z, cz = ux * y - uy * x;
    double dx = uy * cz - uz * cy, dy = uz * cx - ux * cz, dz = ux * cy - uy * cx;
    return [x + 2 * (w * cx + dx), y + 2 * (w * cy + dy), z + 2 * (w * cz + dz)];
  }

  private static Quaternion ApiMul(Quaternion a, Quaternion b) => new(
    a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
    a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

  private static Quaternion ApiAbout(double x, double y, double z, double degrees)
  {
    double half = degrees * Math.PI / 360.0, s = Math.Sin(half);
    return new Quaternion((float)(x * s), (float)(y * s), (float)(z * s), (float)Math.Cos(half));
  }

  // Whether a world point is inside the box with this centre, half sizes and turn.
  private static bool ApiInside(double px, double py, double pz, double[] centre, double[] half, Quaternion turn)
  {
    var v = ApiTurn(new Quaternion(-turn.x, -turn.y, -turn.z, turn.w), px - centre[0], py - centre[1], pz - centre[2]);
    return Math.Abs(v[0]) <= half[0] && Math.Abs(v[1]) <= half[1] && Math.Abs(v[2]) <= half[2];
  }

  // The highest top at or below fromY of a box, by walking down the line in 1 mm steps (so it shares nothing with the interval maths of the code
  // it checks): null when the line misses the box or starts inside it (the box's top is then above fromY).
  private static double? ApiWalkTop(double x, double z, double fromY, double[] centre, double[] half, Quaternion turn)
  {
    // Every box of these scenarios is between y = -30 and y = 30, so the walk starts no higher than 30.
    double start = Math.Min(fromY, 30.0);
    if (ApiInside(x, start, z, centre, half, turn))
      return null;
    for (double y = start; y > start - 60; y -= 0.001)
      if (ApiInside(x, y, z, centre, half, turn))
        return y + 0.0005;
    return null;
  }

  // The palette of the SurfaceHeight scenarios.
  private static PaletteEntry[] ApiSurfacePalette() =>
  [
    // 0: a wall 4 m long (x), 2 m high, 0.4 m thick; its point at the foot of its middle.
    Entry("wall", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 1000, 0, 4000, 2000, 400)]),
    // 1: a slab whose point is off its middle: the anchor (0.5, 0, -0.3) m, the box centred there 0.1 m up.
    new PaletteEntry([new Candidate("slab", 500, 0, -300)], BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(500, 100, -300, 2000, 200, 1000)]),
    // 2: a tree whose point is not at its foot: the anchor is (0.5, 0, 0) m.
    new PaletteEntry([new Candidate("tree", 500, 0, 0)], BakedRole.Static, BakedCollision.Trunk),
    // 3: a tree whose point is its foot.
    Entry("tree_foot", BakedRole.Static, BakedCollision.Trunk),
    // 4 and 5: no collider data here.
    Entry("prefab_piece", BakedRole.Static, BakedCollision.Prefab),
    Entry("bush", BakedRole.Static, BakedCollision.None),
    // 6: a table, two boxes: a top and a leg.
    Entry("table", BakedRole.Static, BakedCollision.Boxes, boxes: [new Box(0, 800, 0, 1200, 100, 800), new Box(500, 400, 0, 100, 800, 100)]),
  ];

  private static BakedLayer ApiSurfaceLayer(params ZoneRecord[] records)
  {
    var edit = LayerEdit.New("api surface");
    foreach (var entry in ApiSurfacePalette())
      edit.PaletteIndexFor(entry);
    edit.AddRecords(records, 3f);
    return edit.Build().Layer;
  }

  private static List<ZoneRecord> ApiRecordsOf(BakedLayer layer)
  {
    var list = new List<ZoneRecord>();
    foreach (var row in layer.Zones)
      if (row.Placements > 0)
        list.AddRange(layer.Decode(row).Records());
    return list;
  }

  // The box of a wall-like record in the world, as the collider would have it: the point less the first candidate's anchor, turned and scaled.
  private static (double[] Centre, double[] Half) ApiBoxOf(ZoneRecord r, PaletteEntry entry, Box box)
  {
    var scale = r.Scale;
    var anchor = entry.Candidates[0].Anchor;
    var offset = ApiTurn(r.Rotation, (box.Centre.x - anchor.x) * scale.x, (box.Centre.y - anchor.y) * scale.y, (box.Centre.z - anchor.z) * scale.z);
    var p = r.Position;
    return ([p.x + offset[0], p.y + offset[1], p.z + offset[2]],
      [Math.Abs(box.Size.x * scale.x) / 2.0, Math.Abs(box.Size.y * scale.y) / 2.0, Math.Abs(box.Size.z * scale.z) / 2.0]);
  }

  // The box of a tree's trunk: 0.6 m across and 3 m up from the foot (the pivot), whatever the tree's size, as VALtima's town pack and the
  // renderer's collider make it.
  private static (double[] Centre, double[] Half) ApiTrunkOf(ZoneRecord r, PaletteEntry entry)
  {
    var scale = r.Scale;
    var anchor = entry.Candidates[0].Anchor;
    var offset = ApiTurn(r.Rotation, anchor.x * scale.x, anchor.y * scale.y, anchor.z * scale.z);
    var p = r.Position;
    return ([p.x - offset[0], p.y - offset[1] + 1.5, p.z - offset[2]], [0.3, 1.5, 0.3]);
  }

  private static void ApiSurfaceTest()
  {
    Section("api: SurfaceHeight is the highest collider top at or below fromY, for boxes and trunks, turned, scaled, and over a zone's edge");
    var previous = BC.Settings;
    var palette = ApiSurfacePalette();
    try
    {
      BC.Settings = new BC.BetterContinentsSettings();
      C(!BakedPlacements.SurfaceHeight(0, 0, 100, out var none) && none == 0f, "without a layer there is no surface");

      string Compare(BakedLayer layer, double x, double z, double fromY, double? expected, string what)
      {
        BC.Settings = World(layer);
        bool found = BakedPlacements.SurfaceHeight((float)x, (float)z, (float)fromY, out float y);
        if (expected is null)
          return found ? $"{what}: found {y} where nothing is" : y != 0f ? $"{what}: a miss left y at {y}" : null;
        if (!found)
          return $"{what}: nothing found, expected {expected:0.###}";
        return Math.Abs(y - expected.Value) <= 0.002 ? null : $"{what}: {y:0.####}, expected {expected:0.####}";
      }

      void Expect(BakedLayer layer, double x, double z, double fromY, double? expected, string what)
      {
        var problem = Compare(layer, x, z, fromY, expected, what);
        C(problem == null, problem ?? what);
      }

      // The expectation of a record's boxes by the walk down the line: the highest top among them.
      double? Walk(BakedLayer layer, double x, double z, double fromY)
      {
        double? best = null;
        foreach (var r in ApiRecordsOf(layer))
        {
          var entry = layer.Palette[r.Palette];
          IEnumerable<(double[] Centre, double[] Half)> boxes = entry.Collision switch
          {
            BakedCollision.Boxes => entry.Boxes.Select(b => ApiBoxOf(r, entry, b)),
            BakedCollision.Trunk => [ApiTrunkOf(r, entry)],
            _ => [],
          };
          foreach (var (centre, half) in boxes)
          {
            // A trunk is not turned; a box is, with its record.
            var turn = entry.Collision == BakedCollision.Trunk ? new Quaternion(0f, 0f, 0f, 1f) : r.Rotation;
            var top = ApiWalkTop(x, z, fromY, centre, half, turn);
            if (top is { } t && (best is null || t > best))
              best = t;
          }
        }
        return best;
      }

      // 1. An upright wall: x 8..12, y 5..7, z 9.8..10.2.
      var wall = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 10, 5, 10, 0));
      Expect(wall, 11, 10, 100, 7, "an upright wall: the top, 2 m above its foot");
      Expect(wall, 12.1, 10, 100, null, "0.1 m past the wall's end: nothing");
      Expect(wall, 11, 10.3, 100, null, "0.1 m beside the wall: nothing");
      Expect(wall, 11, 10, 7.0, 7, "fromY at the top: the top is at or below it");
      Expect(wall, 11, 10, 6.9, null, "fromY below the top: that top is not at or below it");
      Expect(wall, 11, 10, 4.9, null, "fromY below the foot: nothing");

      // 2. Turned a quarter: the wall's length runs along z, and 11 m east of the point is outside it.
      var quarter = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 10, 5, 10, 90));
      Expect(quarter, 10.1, 11.5, 100, 7, "a wall turned 90 degrees reaches along z");
      Expect(quarter, 11.5, 10, 100, null, "and no longer along x");
      Expect(quarter, 10.3, 11.5, 100, null, "and is 0.2 m either side of its line");
      var eighth = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 10, 5, 10, 45));
      foreach (var (x, z) in new[] { (11.0, 11.0), (11.4, 10.6), (9.0, 9.0), (11.4, 11.6), (10.0, 11.0), (12.0, 10.0) })
        Expect(eighth, x, z, 100, Walk(eighth, x, z, 100), $"a wall turned 45 degrees, at ({x}, {z}), as the walk finds it");

      // 3. Scaled: x2, y3, z1 makes it 8 m long, 6 m high.
      var scaled = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 10, 5, 10, 0, new Vector3(2f, 3f, 1f)));
      Expect(scaled, 13.5, 10, 100, 11, "a wall scaled 2 x 3 x 1 is 6 m high and reaches 4 m either way");
      Expect(scaled, 14.1, 10, 100, null, "and not 4.1");
      // A wall long enough to reach past 12 m from its point (8 times 2 m is 16 m).
      var tall = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 0, 5, 0, 0, new Vector3(8f, 1f, 1f)));
      Expect(tall, 14, 0, 100, 7, "a wall scaled 8 times reaches 14 m from its point (a box can be longer than 12 m)");
      Expect(tall, -15.9, 0, 100, 7, "and 15.9 m the other way");
      Expect(tall, 16.1, 0, 100, null, "but not 16.1");

      // 4. Leaning: a full rotation, yawed 40 degrees and tipped 30 degrees about z; the top varies along the line of its length.
      var lean = ApiMul(ApiAbout(0, 1, 0, 40), ApiAbout(0, 0, 1, 30));
      var tilted = ApiSurfaceLayer(ZoneRecord.Create(0, 10, 5, 10, lean));
      var tiltedRecord = ApiRecordsOf(tilted).Single();
      C(tiltedRecord.HasFullRotation, "the leaning wall keeps its full rotation");
      var heights = new List<double>();
      for (double dx = -2.2; dx <= 2.2; dx += 0.37)
        for (double dz = -1.3; dz <= 1.3; dz += 0.41)
        {
          var walk = Walk(tilted, 10 + dx, 10 + dz, 100);
          if (walk is { } w)
            heights.Add(w);
          Expect(tilted, 10 + dx, 10 + dz, 100, walk, $"a leaning wall at ({10 + dx:0.##}, {10 + dz:0.##}), as the walk finds it");
        }
      C(heights.Count > 8 && heights.Max() - heights.Min() > 0.5, $"the leaning wall's top is not level ({heights.Count} hits from {heights.DefaultIfEmpty().Min():0.##} to {heights.DefaultIfEmpty().Max():0.##})");

      // 5. Off the middle: the slab's point is its anchor, not its centre, and its box is 0.5 m east and 0.3 m south of the point... less the anchor.
      var slab = ApiSurfaceLayer(ZoneRecord.CreateYaw(1, 10, 5, 10, 0));
      foreach (var (x, z) in new[] { (10.0, 10.0), (10.9, 10.4), (11.1, 10.0), (9.1, 10.4), (10.0, 10.6), (10.0, 9.4) })
        Expect(slab, x, z, 100, Walk(slab, x, z, 100), $"a slab at ({x}, {z}), as the walk finds it");
      Expect(slab, 10, 10, 100, 5.2, "a slab's box is centred on its point (centre less anchor is 0.1 m up): top 0.2 m above the point's y");
      Expect(slab, 10.95, 10.45, 100, 5.2, "its corner is in");
      Expect(slab, 11.05, 10.0, 100, null, "and 1.05 m east is out");

      // 6. A tree: 0.6 m across, 3 m up from its foot, whatever its size and turn.
      var tree = ApiSurfaceLayer(ZoneRecord.CreateYaw(3, 20, 3, 20, 0));
      Expect(tree, 20.25, 20.25, 100, 6, "a trunk reaches 3 m above its foot");
      Expect(tree, 20.35, 20, 100, null, "and is 0.3 m either side");
      var bigTree = ApiSurfaceLayer(ZoneRecord.CreateYaw(3, 20, 3, 20, 45, new Vector3(3f, 3f, 3f)));
      Expect(bigTree, 20.25, 20.25, 100, 6, "a tree of three times the size has the same trunk: 3 m high (whatever the tree's size)");
      Expect(bigTree, 20.29, 20.29, 100, 6, "and the same corner: the box is not turned with the tree");
      Expect(bigTree, 20.5, 20, 100, null, "and no wider");
      var anchored = ApiSurfaceLayer(ZoneRecord.CreateYaw(2, 20, 3, 20, 0));
      Expect(anchored, 19.5, 20, 100, 6, "a trunk stands at the pivot (the point less the anchor), here 0.5 m west of the point");
      Expect(anchored, 20.0, 20, 100, null, "so the point itself is outside it");
      var anchoredBig = ApiSurfaceLayer(ZoneRecord.CreateYaw(2, 20, 3, 20, 90, new Vector3(2f, 2f, 2f)));
      var scaledTree = ApiRecordsOf(anchoredBig).Single();
      var turnedOffset = ApiTurn(scaledTree.Rotation, 0.5 * 2, 0, 0);
      Expect(anchoredBig, 20 - turnedOffset[0], 20 - turnedOffset[2], 100, 6, "a scaled, turned tree's pivot is the point less the turned, scaled anchor");

      // 7. Several at one place: the highest at or below.
      var stack = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 10, 5, 10, 0), ZoneRecord.CreateYaw(0, 10, 10, 10, 0));
      Expect(stack, 11, 10, 100, 12, "a roof above a wall: the highest top");
      Expect(stack, 11, 10, 11, 7, "from between them: the wall's");
      Expect(stack, 11, 10, 6, null, "from below both: none");

      // 8. A table: the top and a leg.
      var table = ApiSurfaceLayer(ZoneRecord.CreateYaw(6, 10, 5, 10, 0));
      Expect(table, 10, 10, 100, 5.85, "a table's top");
      Expect(table, 10.5, 10, 100, 5.85, "at its leg, the top above it is the highest");
      Expect(table, 10.55, 10, 100, 5.85, "and the leg's corner too");
      Expect(table, 10.7, 10, 100, null, "past both: nothing");

      // 9. Over a zone's edge: a wall at x = 31.5 (zone 0 ends at 32) stands in zone 1 too.
      var edge = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 31.5, 5, 0, 0));
      Expect(edge, 32.7, 0, 100, 7, "a wall over its zone's edge is found from the next zone");
      Expect(edge, 33.6, 0, 100, null, "and ends where it ends");
      var edgeZ = ApiSurfaceLayer(ZoneRecord.CreateYaw(0, 0, 5, -31.5, 90));
      Expect(edgeZ, 0, -32.8, 100, 7, "and over the south edge too");
      Expect(edgeZ, 0, -35.6, 100, null, "ending where it ends");

      // 10. Entries whose collision is not in the data are not here.
      var others = ApiSurfaceLayer(ZoneRecord.CreateYaw(4, 10, 5, 10, 0), ZoneRecord.CreateYaw(5, 10, 5, 10, 0));
      Expect(others, 10, 10, 100, null, "collision Prefab and None have no boxes here: nothing");
      // 11. Far away from every record, and in an empty zone.
      Expect(wall, 200, 200, 100, null, "far from the records: nothing");
    }
    finally
    {
      BC.Settings = previous;
    }
  }

  // ------------------------------------------------------------------------------------------------ keys, events, operations

  private static void ApiKeysTest()
  {
    Section("api: BakeIdOf and SourceOf read a piece's bake keys; BakedKeys has the names and stable hashes");
    ZDOExtraData.Reset();
    try
    {
      var both = ApiZdo(1, source: 3, id: 5);
      C(BakedPlacements.BakeIdOf(both) == 5u && BakedPlacements.SourceOf(both) == 3, "a seeded piece: its id and its source");
      var big = ApiZdo(2, source: 0, id: -1);
      C(BakedPlacements.BakeIdOf(big) == uint.MaxValue && BakedPlacements.SourceOf(big) == 0, "an id above 2 billion is stored as a negative int and read back unchecked");
      var plain = ApiZdo(3);
      C(BakedPlacements.BakeIdOf(plain) == 0u && BakedPlacements.SourceOf(plain) == 0, "a piece without the keys has id 0 and source 0");
      C(BakedPlacements.BakeIdOf(null) == 0u && BakedPlacements.SourceOf(null) == 0, "no ZDO: 0 and 0");
      var idOnly = ApiZdo(4, id: 77);
      C(BakedPlacements.BakeIdOf(idOnly) == 77u && BakedPlacements.SourceOf(idOnly) == 0, "an id without a source is the compiler's (source 0)");
    }
    finally
    {
      ZDOExtraData.Reset();
    }
    C(BakedKeys.Src == "bc_bake_src".GetStableHashCode() && BakedKeys.Id == "bc_bake_id".GetStableHashCode() && BakedKeys.Rev == "bc_bake_rev".GetStableHashCode()
      && BakedKeys.Protect == "bc_protect".GetStableHashCode() && BakedKeys.Orphan == "bc_bake_orphan".GetStableHashCode() && BakedKeys.Seed == "RandMatSeed".GetStableHashCode(),
      "the keys are the game's stable hashes of the names the spec gives");
    C(BakedKeys.MatVar(0) == "MatVar0".GetStableHashCode() && BakedKeys.MatVar(3) == "MatVar3".GetStableHashCode() && BakedKeys.MatVarKey(12) == "MatVar12", "MatVar<i> is the name with the slot after it");
    C(BakedKeys.BakeKeys.SequenceEqual(new[] { BakedKeys.Src, BakedKeys.Id, BakedKeys.Rev, BakedKeys.Protect }), "the four bake keys, in the order they are written");
    foreach (var (source, id) in new[] { (0, 0u), (1, 5u), (65535, uint.MaxValue), (7, 2147483648u) })
    {
      var text = BakedKeys.OrphanValue(source, id);
      C(BakedKeys.TryParseOrphan(text, out int s, out uint i) && s == source && i == id && text == source + "/" + id, $"an orphan's mark '{text}' reads back");
    }
    C(!BakedKeys.TryParseOrphan(null, out _, out _) && !BakedKeys.TryParseOrphan("", out _, out _) && !BakedKeys.TryParseOrphan("12", out _, out _) && !BakedKeys.TryParseOrphan("/5", out _, out _)
      && !BakedKeys.TryParseOrphan("a/5", out _, out _) && !BakedKeys.TryParseOrphan("5/-1", out _, out _) && !BakedKeys.TryParseOrphan("5/", out _, out _), "an orphan's mark that is not 'source/id' is refused");
  }

  private static void ApiEventsTest()
  {
    Section("api: LayerChanged, LiveSeeded, Baked and Unbaked reach every handler, and the layer store says what changed");
    var previous = BC.Settings;
    var layerChanged = 0;
    var storeCalls = new List<(BakedLayer Old, BakedLayer New, ZoneKey[] Zones)>();
    void OnLayerChanged() => layerChanged++;
    void OnStore(BakedLayer o, BakedLayer n, ZoneKey[] z) => storeCalls.Add((o, n, z));
    BakedPlacements.LayerChanged += OnLayerChanged;
    BakedLayerStore.Changed += OnStore;
    try
    {
      BC.Settings = new BC.BetterContinentsSettings();
      var sampleA = MakeSample(41);
      var sampleB = MakeSample(42);
      BakedLayerStore.Set(sampleA.Layer, null);
      C(layerChanged == 1 && storeCalls.Count == 1 && storeCalls[0].Old == null && storeCalls[0].New == sampleA.Layer && storeCalls[0].Zones == null, "a first layer: LayerChanged once, the store's event with no old layer and all zones (null)");
      C(BakedLayerStore.Current == sampleA.Layer && BakedLayerStore.HasLayer && BakedPlacements.HasLayer && BakedPlacements.Revision == (int)sampleA.Layer.Revision, "and it is current");
      BakedLayerStore.Set(sampleA.Layer, null);
      C(layerChanged == 1 && storeCalls.Count == 1, "the same layer again raises nothing");
      var changed = new[] { new ZoneKey(1, 2) };
      BakedLayerStore.Set(sampleB.Layer, changed);
      C(layerChanged == 2 && storeCalls.Count == 2 && storeCalls[1].Old == sampleA.Layer && storeCalls[1].New == sampleB.Layer && storeCalls[1].Zones.SequenceEqual(changed), "another layer: both events, with the old one and the zones given");
      BC.Settings = World(sampleA.Layer);
      C(layerChanged == 2 && storeCalls.Count == 2 && BakedLayerStore.Current == sampleA.Layer && BakedPlacements.Revision == (int)sampleA.Layer.Revision,
        "settings swapped for another world's raise no event (a watcher reads Current), but Current is the new world's layer");
      BakedLayerStore.Set(null, null);
      C(layerChanged == 3 && storeCalls.Count == 3 && storeCalls[2].Old == sampleA.Layer && storeCalls[2].New == null && !BakedPlacements.HasLayer && BakedPlacements.Revision == 0
        && BakedPlacements.Operations.Count == 0, "clearing the layer raises both, and the API answers as for a world without one");

      // A handler of another mod that throws stops neither the work of this mod nor the handlers after it.
      var order = new List<string>();
      void Bad() { order.Add("bad"); throw new InvalidOperationException("a handler's bug"); }
      void Good() => order.Add("good");
      BakedPlacements.LayerChanged += Bad;
      BakedPlacements.LayerChanged += Good;
      var lines = LogHandler.During(() => BakedLayerStore.Set(sampleB.Layer, null));
      BakedPlacements.LayerChanged -= Bad;
      BakedPlacements.LayerChanged -= Good;
      C(BakedLayerStore.Current == sampleB.Layer && layerChanged == 4, "a handler that throws does not undo the change, and this mod's own handler still runs");
      C(order.SequenceEqual(new[] { "bad", "good" }), "the handler after the one that threw is still called: " + string.Join(",", order));
      C(lines.Any(l => l.Contains("A handler of the baked placements API threw") && l.Contains("a handler's bug")), "and the failure is logged with its message");

      // An internal subscriber to the store that throws must not keep the others (or the API's event) from hearing of the change.
      int later = 0;
      void BadStore(BakedLayer o, BakedLayer n, ZoneKey[] z) => throw new InvalidOperationException("an internal handler's bug");
      void LaterStore(BakedLayer o, BakedLayer n, ZoneKey[] z) => later++;
      BakedLayerStore.Changed += BadStore;
      BakedLayerStore.Changed += LaterStore;
      string escaped = null;
      lines = LogHandler.During(() =>
      {
        try
        {
          BakedLayerStore.Set(sampleA.Layer, null);
        }
        catch (Exception e)
        {
          escaped = e.Message;
        }
      });
      BakedLayerStore.Changed -= BadStore;
      BakedLayerStore.Changed -= LaterStore;
      C(escaped == null, "a store subscriber that throws does not escape from Set" + (escaped == null ? "" : ": " + escaped));
      C(BakedLayerStore.Current == sampleA.Layer && later == 1 && layerChanged == 5, "and does not keep the next subscriber, nor LayerChanged, from running");
      C(lines.Any(l => l.Contains("an internal handler's bug")), "and its failure is logged");

      // LiveSeeded.
      ZDOExtraData.Reset();
      var zdo = ApiZdo(9, 2, 6);
      var seen = new List<(ZDO Zdo, GameObject Instance, int Source, uint Id)>();
      void OnSeeded(ZDO z, GameObject g, int s, uint i) => seen.Add((z, g, s, i));
      Action<ZDO, GameObject, int, uint> thrower = (z, g, s, i) => throw new InvalidOperationException("another mod");
      BakedPlacements.LiveSeeded += OnSeeded;
      BakedPlacements.LiveSeeded += thrower;
      BakedPlacements.LiveSeeded += OnSeeded;
      var instance = ApiAlive<GameObject>();
      lines = LogHandler.During(() => BakedApi.RaiseLiveSeeded(zdo, instance, 2, 6));
      BakedApi.RaiseLiveSeeded(zdo, null, 0, uint.MaxValue);
      BakedPlacements.LiveSeeded -= OnSeeded;
      BakedPlacements.LiveSeeded -= thrower;
      BakedPlacements.LiveSeeded -= OnSeeded;
      C(seen.Count >= 2 && seen[0].Zdo == zdo && ReferenceEquals(seen[0].Instance, instance) && seen[0].Source == 2 && seen[0].Id == 6, "LiveSeeded carries the ZDO, the seeding instance, the source and the id");
      C(seen.Count == 4 && seen[3].Instance == null && seen[3].Source == 0 && seen[3].Id == uint.MaxValue, "with no instance, the instance is null (and a handler after a throwing one still hears of it)");
      C(lines.Any(l => l.Contains("another mod")), "the throwing handler is logged");
      ZDOExtraData.Reset();

      // Baked and Unbaked.
      var baked = new List<BakeInfo>();
      var unbaked = new List<BakeInfo>();
      BakedPlacements.Baked += baked.Add;
      BakedPlacements.Unbaked += unbaked.Add;
      var op = new OperationInfo(7, OperationKind.BakeBox, OperationState.InLayer, 1_700_000_123L, "Tester", 1f, 2f, 3f, 4f, 0f, uint.MaxValue, 12, 3, "0.10.4");
      BakedApi.RaiseBaked(op);
      BakedApi.RaiseUnbaked(op.WithState(OperationState.Undone));
      C(baked.Count == 1 && unbaked.Count == 1, "Baked and Unbaked each reach their handler once");
      var info = baked[0];
      C(info.Number == 7 && info.Kind == OperationKind.BakeBox && info.State == OperationState.InLayer && info.Who == "Tester" && info.X1 == 1f && info.Z1 == 2f && info.X2 == 3f && info.Z2 == 4f
        && info.Radius == 0f && info.Removed == 12 && info.Adopted == 3 && info.Version == "0.10.4", "the BakeInfo carries the operation's number, kind, state, who, bounds, counts and version");
      C(info.Added == int.MaxValue, "a count over 2 billion is clamped to int.MaxValue");
      C(info.Time == new DateTime(2023, 11, 14, 22, 15, 23, DateTimeKind.Utc) && info.Time.Kind == DateTimeKind.Utc, "the time is the Unix seconds, in UTC: " + info.Time.ToString("o"));
      C(unbaked[0].State == OperationState.Undone, "an unbake or undo reports the state it left");
    }
    finally
    {
      BakedPlacements.LayerChanged -= OnLayerChanged;
      BakedLayerStore.Changed -= OnStore;
      BC.Settings = previous;
      ZDOExtraData.Reset();
    }
  }

  private static void ApiOperationsTest()
  {
    Section("api: Operations lists the registry in order, and is empty without one");
    var previous = BC.Settings;
    try
    {
      BC.Settings = new BC.BetterContinentsSettings();
      C(BakedPlacements.Operations.Count == 0, "without a layer: no operations");
      var plain = LayerEdit.New("plain");
      plain.PaletteIndexFor(Entry("piece_a"));
      plain.AddRecords([ZoneRecord.CreateYaw(0, 1, 2, 3, 0)]);
      BC.Settings = World(plain.Build().Layer);
      C(BakedPlacements.HasLayer && BakedPlacements.Operations.Count == 0, "a layer without a registry: no operations");

      var sample = MakeSample(43);
      BC.Settings = World(sample.Layer);
      var operations = BakedPlacements.Operations;
      var truth = sample.Layer.Registry.Operations;
      C(operations.Count == 5 && truth.Count == 5, "the sample's five operations");
      bool same = true;
      for (int i = 0; i < truth.Count; i++)
      {
        var a = operations[i];
        var b = truth[i];
        same &= a.Number == b.Number && a.Kind == b.Kind && a.State == b.State && a.Who == b.Who && a.X1 == b.X1 && a.Z1 == b.Z1 && a.X2 == b.X2 && a.Z2 == b.Z2 && a.Radius == b.Radius
                && a.Added == b.Added && a.Removed == b.Removed && a.Adopted == b.Adopted && a.Version == b.Version
                && a.Time == DateTimeOffset.FromUnixTimeSeconds(b.Time).UtcDateTime;
      }
      C(same, "each with its number, kind, state, who, bounds, radius, counts, version and time as the registry has them");
      C(operations.Select(o => o.Number).SequenceEqual(new[] { 1, 2, 3, 4, 5 }) && operations.Select(o => o.Kind).SequenceEqual(new[]
        { OperationKind.BakeArea, OperationKind.BakeBox, OperationKind.Unbake, OperationKind.Load, OperationKind.Drop }), "in the order they were made");
      C(operations[0].Radius == 40f && operations[1].Radius == 0f, "a bake of an area has its radius, a box none");

      // The list follows the layer that is current.
      var edit = sample.Layer.Edit();
      edit.SetOperationState(4, OperationState.Undone);
      edit.AddOperation(SampleOperation(6, OperationKind.BakeArea, 1));
      BC.Settings = World(edit.Build().Layer);
      var after = BakedPlacements.Operations;
      C(after.Count == 6 && after[3].State == OperationState.Undone && after[0].State == OperationState.InLayer && after[5].Number == 6 && after[5].State == OperationState.InLayer,
        "an operation marked undone and a new one show in the current layer's list");
      C(BakedPlacements.Operations != BakedPlacements.Operations, "each call makes its own list (a caller cannot change the registry through it)");
    }
    finally
    {
      BC.Settings = previous;
    }
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The client renderer's zone build (slice C): what BakedZoneBuild.Build makes of a zone's records, with no engine: instances sorted into cells,
// the collider mesh welded from boxes, trunks and the drawn prefab's colliders (one per Unity layer, relative to the zone object, wound outward),
// the lit copies and seats, and VALtima's whole file through it.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // The triangles of a welded mesh face outward from the box they came from: Unity's front faces are clockwise seen from outside, and for those the
  // cross product of two edges points outward (VALtima's cube table, which the game's floors have stood on).
  private static bool FacesOutward(Vector3[] v, int[] t, int first, int count, Vector3 centre)
  {
    for (int i = first; i < first + count; i += 3)
    {
      var a = v[t[i]]; var b = v[t[i + 1]]; var c = v[t[i + 2]];
      if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - centre) <= 0f)
        return false;
    }
    return true;
  }

  private static (Vector3 Min, Vector3 Max) Extent(Vector3[] v, int first, int count)
  {
    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
    for (int i = first; i < first + count; i++)
    {
      min = Vector3.Min(min, v[i]);
      max = Vector3.Max(max, v[i]);
    }
    return (min, max);
  }

  private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-3f) => (a - b).magnitude <= tolerance;

  // Vector3.ToString goes through the engine's string formatting, which the harness does not have
  private static string V(Vector3 v) => FormattableString.Invariant($"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})");

  // Boxes, trunks and prefab colliders are welded into one mesh a layer, relative to the zone object.
  private static void ClientColliderTest()
  {
    Section("client: the welded colliders");
    // canonical boxes follow the FIRST candidate's anchor, whichever is drawn (R1)
    var wallPiece = ClientKit.NoLods("wall", 2f);
    var boxKind = ClientKit.Kind("wall_a", wallPiece, collision: BakedCollision.Boxes, anchor: new Vector3(0, 0.5f, 0), anchor0: new Vector3(1, 0, 0),
      boxes: [new KindBox(new Vector3(0, 1, 0), new Vector3(2, 2, 0.2f))]);
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 10, 5, 20, 90, null)), [boxKind], 7);
    C(zone.Colliders.Length == 1 && zone.Colliders[0].Layer == 10 && zone.Colliders[0].Vertices.Length == 8 && zone.Colliders[0].Triangles.Length == 36, "one box: 8 vertices and 12 triangles on layer 10");
    var (min, max) = Extent(zone.Colliders[0].Vertices, 0, 8);
    // pivot = point less the first candidate's anchor turned by 90 degrees (+x becomes -z): (10, 5, 21); the box (2 x 2 x 0.2 at (0, 1, 0)) lies along z
    C(Near(min, new Vector3(9.9f, 5f, 20f)) && Near(max, new Vector3(10.1f, 7f, 22f)), $"the box stands where the first candidate's anchor puts it ({V(min)} to {V(max)})");
    C(FacesOutward(zone.Colliders[0].Vertices, zone.Colliders[0].Triangles, 0, 36, (min + max) * 0.5f), "every triangle faces outward");
    C(zone.Kinds.Length == 1 && zone.Kinds[0].Count == 1 && zone.Revision == 7u, "the drawn instance and the revision are kept");
    // the collider is relative to the zone object: a zone 100 zones out
    var farZone = BakedZoneBuild.Build(ClientKit.Zone(100, -90, (0, 6410, 5, -5750, 0, null)), [boxKind], 1);
    var (fmin, fmax) = Extent(farZone.Colliders[0].Vertices, 0, 8);
    C(Near(fmin, new Vector3(8f, 5f, 9.9f)) && Near(fmax, new Vector3(10f, 7f, 10.1f)), $"in a zone 6.4 km out the numbers are relative to the zone object ({V(fmin)} to {V(fmax)})");

    // a mirrored box is wound the other way and still faces outward
    var mirrored = BakedMath.Mul(BakedMath.Place(BakedMath.FromYaw(0, -1, 1, 1), 0, 0, 0, Vector3.zero), BakedMath.BoxMatrix(new Vector3(0, 0, 0), new Vector3(2, 2, 2)));
    var verts = new List<Vector3>();
    var tris = new List<int>();
    BakedMath.AddBox(mirrored, verts, tris);
    C(verts.Count == 8 && tris.Count == 36 && FacesOutward(verts.ToArray(), tris.ToArray(), 0, 36, Vector3.zero), "a mirrored box faces outward too");

    // a trunk: 0.6 x 3 m at the foot, whatever the tree's scale or turn
    var tree = ClientKit.Kind("tree", ClientKit.NoLods("tree", 3f), collision: BakedCollision.Trunk);
    var trunk = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 3, 2, 4, 33, new Vector3(2, 2, 2))), [tree], 1);
    var (tmin, tmax) = Extent(trunk.Colliders[0].Vertices, 0, 8);
    C(Near(tmin, new Vector3(2.7f, 2f, 3.7f)) && Near(tmax, new Vector3(3.3f, 5f, 4.3f)), $"a trunk is 0.6 x 3 x 0.6 m standing on the point ({V(tmin)} to {V(tmax)})");

    // a missing candidate: no piece to draw, and the canonical boxes still stand (9.8)
    var missing = ClientKit.Kind("gone", null, collision: BakedCollision.Boxes, boxes: [new KindBox(Vector3.zero, new Vector3(1, 1, 1))]);
    var gone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1, 0, 1, 0, null)), [missing], 1);
    C(gone.Drawn == 0 && gone.Kinds.Length == 0 && gone.Colliders.Length == 1 && gone.Colliders[0].Triangles.Length == 36, "a record with no candidate in this game draws nothing, and its boxes still stand");

    // the drawn prefab's own colliders (collision 3): boxes, a readable mesh, a convex mesh that stays real
    var prefabPiece = ClientKit.NoLods("hearth", 2f);
    prefabPiece.Boxes = [BakedMath.BoxMatrix(new Vector3(0, 1, 0), new Vector3(1, 2, 1))];
    prefabPiece.MeshVertices = [[new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1)]];
    prefabPiece.MeshTriangles = [[0, 1, 2]];
    prefabPiece.ConvexMeshes = [null];
    prefabPiece.ConvexLocal = [BakedMath.Place(BakedMath.FromYaw(0, 1, 1, 1), 0, 0.5, 0, Vector3.zero)];
    var prefab = ClientKit.Kind("hearth", prefabPiece, collision: BakedCollision.Prefab);
    var pz = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 8, 1, 8, 0, null)), [prefab], 1);
    C(pz.Colliders.Length == 1 && pz.Colliders[0].Vertices.Length == 8 + 3 && pz.Colliders[0].Triangles.Length == 36 + 3, "the prefab's box and mesh are welded (8 + 3 vertices, 12 + 1 triangles)");
    C(pz.Colliders[0].Triangles[36] == 8 && pz.Colliders[0].Triangles[37] == 9 && pz.Colliders[0].Triangles[38] == 10, "a mesh's triangles are numbered after the vertices already welded");
    C(pz.Convex.Length == 1 && Math.Abs(pz.Convex[0].Matrix.m03 - 8f) < 1e-4f && Math.Abs(pz.Convex[0].Matrix.m13 - 1.5f) < 1e-4f && pz.Convex[0].Layer == 10, "a convex collider stays real, placed with the record");
    C(pz.HasColliders && pz.ColliderTriangles == 13, "the zone knows it has colliders");

    // one mesh a Unity layer
    var onStatic = ClientKit.Kind("rock", ClientKit.NoLods("rock", 2f), collision: BakedCollision.Boxes, boxes: [new KindBox(Vector3.zero, Vector3.one)], layer: 15);
    var layers = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1, 0, 1, 0, null), (1, 3, 0, 3, 0, null), (0, 5, 0, 5, 0, null)), [boxKind, onStatic], 1);
    C(layers.Colliders.Length == 2 && layers.Colliders.Select(c => c.Layer).OrderBy(l => l).SequenceEqual([10, 15])
      && layers.Colliders.First(c => c.Layer == 10).Triangles.Length == 72 && layers.Colliders.First(c => c.Layer == 15).Triangles.Length == 36, "colliders are welded one mesh for each layer");

    // roles: a live record is the real piece (nothing here), a copy and a seat are listed for their local objects, a null entry is skipped
    var live = ClientKit.Kind("door", null, role: BakedRole.Live, collision: BakedCollision.Prefab);
    var noPrefab = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1, 0, 1, 0, null), (1, 2, 0, 2, 0, null)), [live, null!], 1);
    C(noPrefab.Drawn == 0 && !noPrefab.HasColliders && noPrefab.Skipped == 1 && noPrefab.Records == 2, "a live record makes neither a drawing nor a collider; an entry that could not be resolved is counted and skipped");
    var torchPiece = ClientKit.NoLods("torch", 0.5f);
    var torch = ClientKit.Kind("torch", null, role: BakedRole.Copy);
    torch.Prefab = null;
    var copies = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1, 0, 1, 0, null)), [torch], 1);
    C(copies.Copies.Length == 0, "a copy with no prefab in this game is not listed (nothing to light)");
  }

  // Instances are sorted into the zone's 16 m cells, kind by kind; a cell's box holds what stands in it.
  private static void ClientCellTest()
  {
    Section("client: cells of 16 m");
    var piece = ClientKit.TwoLods("post", 2f, 0.2f, 0.05f, radius: 1.5f);
    piece.Locals = [BakedMath.Place(BakedMath.FromYaw(0, 1, 1, 1), 0, 2, 0, Vector3.zero)];
    piece.Parts[1].LocalIndex = 0;
    var kind = ClientKit.Kind("post", piece);
    var other = ClientKit.Kind("beam", ClientKit.NoLods("beam", 2f));
    // zone (1, 1) spans x and z 32..96; cells 16 m: column = floor((x - 32) / 16)
    var records = new List<(int, double, double, double, double, Vector3?)>();
    for (int cz = 0; cz < 4; cz++)
      for (int cx = 0; cx < 4; cx++)
        for (int n = 0; n <= (cx + cz) % 3; n++)
          records.Add((0, 32 + cx * 16 + 8 + n * 0.5, 3, 32 + cz * 16 + 8, 0, null));
    records.Add((1, 33, 1, 33, 0, null));
    records.Add((1, 95.9, 1, 95.9, 0, null));
    var zone = BakedZoneBuild.Build(ClientKit.Zone(1, 1, records.ToArray()), [kind, other], 1);
    C(zone.Kinds.Length == 2 && zone.Kinds[0].Kind == kind && zone.Kinds[1].Kind == other, "two kinds, in the order they first appear");
    var posts = zone.Kinds[0];
    bool sortedRight = posts.CellStart[0] == 0 && posts.CellStart[16] == posts.Count;
    for (int cz = 0; cz < 4; cz++)
      for (int cx = 0; cx < 4; cx++)
      {
        int c = cz * 4 + cx, expected = (cx + cz) % 3 + 1;
        sortedRight &= posts.CellStart[c + 1] - posts.CellStart[c] == expected;
        for (int i = posts.CellStart[c]; i < posts.CellStart[c + 1]; i++)
          sortedRight &= (int)Math.Floor((posts.Root[i].m03 - 32) / 16) == cx && (int)Math.Floor((posts.Root[i].m23 - 32) / 16) == cz;
      }
    C(sortedRight, "each cell holds exactly the posts standing in it, in the order of the cells (row 0 south)");
    var beams = zone.Kinds[1];
    C(beams.Count == 2 && beams.CellStart[0 + 1] - beams.CellStart[0] == 1 && beams.CellStart[16] - beams.CellStart[15] == 1, "pieces at the zone's corners fall in its first and last cells");
    // the second matrix a part with an offset needs: root times its local
    C(posts.Locals.Length == 1 && Math.Abs(posts.Locals[0][0].m13 - (posts.Root[0].m13 + 2f)) < 1e-4f, "a part with its own place has its own matrix for every instance (root x local)");
    // the LOD reference point and the squared scale
    var r = posts.Lod[0];
    C(Math.Abs(r.x - posts.Root[0].m03) < 1e-4f && Math.Abs(r.y - posts.Root[0].m13) < 1e-4f && Math.Abs(r.w - 1f) < 1e-6f, "an instance's LOD point is its piece's LOD centre in the world, with its scale squared");
    // boxes: every instance's centre and radius is inside its cell's box, and the zone's box holds every cell's
    bool inside = true;
    for (int c = 0; c < 16; c++)
      for (int i = posts.CellStart[c]; i < posts.CellStart[c + 1]; i++)
      {
        float x = posts.Root[i].m03, y = posts.Root[i].m13, z = posts.Root[i].m23;
        inside &= posts.CellBox[c * 6] <= x - 1.5f + 1e-4f && posts.CellBox[c * 6 + 3] >= x + 1.5f - 1e-4f && posts.CellBox[c * 6 + 1] <= y - 1.5f + 1e-4f
                  && posts.CellBox[c * 6 + 4] >= y + 1.5f - 1e-4f && posts.CellBox[c * 6 + 2] <= z - 1.5f + 1e-4f && posts.CellBox[c * 6 + 5] >= z + 1.5f - 1e-4f
                  && zone.CellBox[c * 6] <= posts.CellBox[c * 6] && zone.CellBox[c * 6 + 3] >= posts.CellBox[c * 6 + 3] && zone.CellUsed[c];
      }
    C(inside, "a cell's box holds its instances and their radius, and the zone's box holds the cell's");
    // an empty zone
    var empty = BakedZoneBuild.Build(ClientKit.Zone(3, 3), [kind], 1);
    C(empty.Records == 0 && empty.Kinds.Length == 0 && !empty.CellUsed.Any(u => u) && !empty.HasColliders && !empty.HasProps, "a zone with no records builds to nothing");
    var made = BuiltZone.Empty(4, 5, 9);
    C(made.Zx == 4 && made.Zz == 5 && made.Revision == 9u && made.Kinds.Length == 0, "BuiltZone.Empty is a zone that has nothing");
  }

  // The palette entry as the client reads it, and two entries that say the same thing are one kind.
  private static void ClientEntryTest()
  {
    Section("client: palette entries");
    var entry = new PaletteEntry([new Candidate("BCP_Wall", 100, -200, 300), new Candidate("stone_wall_2x1", 0, 0, 0)], BakedRole.Seat, BakedCollision.Boxes, layer: 15,
      flags: PaletteFlags.NoShadows | PaletteFlags.LOD0Only, boxes: [new Box(0, 1000, 0, 2000, 2000, 200)], tint: "plaster", tintR: 1650, tintG: 1580, tintB: 1480,
      tintFilter: "bfp_clay", tags: [Tag.OfInt("MatVar0", 2), Tag.OfInt("MatVar3", 1), Tag.OfBool("VALtima_TownPiece", true), Tag.OfInt("Other", 7), Tag.OfString("MatVarX", "no")]);
    var def = EntryDef.From(entry);
    C(def.Names.SequenceEqual(["BCP_Wall", "stone_wall_2x1"]) && Near(def.Anchors[0], new Vector3(0.1f, -0.2f, 0.3f)) && def.Anchors[1] == Vector3.zero, "the candidates and their anchors in metres");
    C(def.Role == BakedRole.Seat && def.Collision == BakedCollision.Boxes && def.Layer == 15 && def.Flags == (PaletteFlags.NoShadows | PaletteFlags.LOD0Only), "role, collision, layer and flags");
    C(def.Boxes.Length == 1 && Near(def.Boxes[0].Centre, new Vector3(0, 1, 0)) && Near(def.Boxes[0].Size, new Vector3(2, 2, 0.2f)), "the boxes in metres");
    C(def.Tint == "plaster" && Math.Abs(def.TintColor.r - 1.65f) < 1e-4f && Math.Abs(def.TintColor.g - 1.58f) < 1e-4f && Math.Abs(def.TintColor.b - 1.48f) < 1e-4f && def.TintFilter == "bfp_clay", "the tint, its colour and its material filter");
    C(def.MatVar != null && def.MatVar.Count == 2 && def.MatVar[0] == 2 && def.MatVar[3] == 1, "only the int tags named MatVar<n> are look variants");
    var same = EntryDef.From(new PaletteEntry(entry.Candidates, entry.Role, entry.Collision, entry.Layer, entry.Flags, entry.Boxes, entry.Tint, entry.TintR, entry.TintG, entry.TintB,
      entry.TintFilter, [Tag.OfBool("VALtima_TownPiece", true), Tag.OfInt("MatVar3", 1), Tag.OfInt("MatVar0", 2), Tag.OfInt("Other", 9)]));
    C(same.Signature == def.Signature, "the same entry (tags in another order, a tag that is not a look) has the same signature: one kind");
    var otherAnchor = EntryDef.From(new PaletteEntry([new Candidate("BCP_Wall", 101, -200, 300), entry.Candidates[1]], entry.Role, entry.Collision, entry.Layer, entry.Flags, entry.Boxes,
      entry.Tint, entry.TintR, entry.TintG, entry.TintB, entry.TintFilter, entry.Tags));
    C(otherAnchor.Signature != def.Signature, "another anchor is another kind");
    var plain = EntryDef.From(new PaletteEntry([new Candidate("a", 0, 0, 0)], BakedRole.Static, BakedCollision.None));
    C(plain.MatVar == null && plain.Tint == "" && plain.Boxes.Length == 0, "a plain entry has no tint, boxes or looks");
    // the Tints setting
    var tints = BakedKinds.ParseTints("plaster=1.65,1.58,1.48; brick = 1.25, 0.72, 0.56 ;bad=1,2;=1,2,3;also bad");
    C(tints.Count == 2 && Math.Abs(tints["plaster"].g - 1.58f) < 1e-5f && Math.Abs(tints["BRICK"].b - 0.56f) < 1e-5f, "the Tints setting: name=r,g,b pairs, names without case, bad pairs left out");
  }

  // VALtima's whole file through the zone build: every record of every zone, as the game's pieces would be drawn and stood on.
  private static void ClientValtimaBuildTest()
  {
    Section("client: VALtima's file through the zone build");
    if (!NeedValtima("ClientValtimaBuildTest"))
      return;
    var layer = BakedLayer.Read(Valtima(), Valtima().Length);
    // each palette entry resolved by hand, as the first candidate would be: a piece of 3 m radius for what is drawn
    var kinds = new BakedKind?[layer.Palette.Count];
    for (int i = 0; i < kinds.Length; i++)
    {
      var e = layer.Palette[i];
      var def = EntryDef.From(e);
      var drawn = e.Role == BakedRole.Static || e.Role == BakedRole.Seat;
      var kind = new BakedKind
      {
        Name = e.Name, Role = e.Role, Collision = e.Collision, Flags = e.Flags, Layer = e.Layer, UnityLayer = 10,
        Anchor = def.Anchors[0], Anchor0 = def.Anchors[0],
        Boxes = def.Boxes.Select(b => BakedMath.BoxMatrix(b.Centre, b.Size)).ToArray(),
        Piece = drawn ? ClientKit.TwoLods(e.Name, 3f, 0.2f, 0.02f, 3f) : null,
      };
      if (kind.Piece != null)
      {
        BakedKinds.MakeBatches(kind);
        kind.FixedVariant = [];
      }
      kinds[i] = kind;
    }
    long records = 0, drawnExpected = 0, boxesExpected = 0, skipped = 0, drawnGot = 0, trianglesGot = 0;
    var densest = layer.Zones.OrderByDescending(z => z.Placements).First();
    double worstOffset = 0;
    int checkedRecords = 0;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    double densestMs = 0;
    foreach (var row in layer.Zones)
    {
      if (row.Placements == 0)
        continue;
      var data = layer.Decode(row);
      var t0 = watch.Elapsed.TotalMilliseconds;
      var built = BakedZoneBuild.Build(data, kinds, 1);
      if (row.Key == densest.Key)
        densestMs = watch.Elapsed.TotalMilliseconds - t0;
      records += data.Count;
      skipped += built.Skipped;
      drawnGot += built.Drawn;
      trianglesGot += built.ColliderTriangles;
      for (int k = 0; k < data.Count; k++)
      {
        var kind = kinds[data.Palette[k]]!;
        if (kind.Role == BakedRole.Static || kind.Role == BakedRole.Seat)
          drawnExpected++;
        if (kind.Role != BakedRole.Live)
          boxesExpected += kind.Collision == BakedCollision.Boxes ? kind.Boxes.Length : kind.Collision == BakedCollision.Trunk ? 1 : 0;
      }
      // a sample of the records: some instance of the kind lands its anchor on the record's point
      if (checkedRecords < 4_000 && (row.Key == densest.Key || row.Index % 40 == 0))
        for (int k = 0; k < data.Count; k += Math.Max(1, data.Count / 25))
        {
          var kind = kinds[data.Palette[k]]!;
          if (!kind.Draws)
            continue;
          var group = built.Kinds.First(g => g.Kind == kind);
          var target = new Vector3((float)data.WorldX(k), (float)data.WorldY(k), (float)data.WorldZ(k));
          double best = double.MaxValue;
          for (int i = 0; i < group.Count; i++)
            best = Math.Min(best, (BakedMath.Point(group.Root[i], kind.Anchor) - target).magnitude);
          worstOffset = Math.Max(worstOffset, best);
          checkedRecords++;
        }
    }
    System.Console.WriteLine($"   built {records:N0} records of {layer.Zones.Count(z => z.Placements > 0):N0} zones in {watch.ElapsedMilliseconds:N0} ms (decode included); the densest zone ({densest.Placements:N0} records) in {densestMs:0.0} ms");
    C(records == 702_700 && skipped == 0, $"all 702,700 records are built and none is skipped ({records:N0}, skipped {skipped})");
    C(drawnGot == drawnExpected && drawnExpected == 697_696 + 3_562, $"the Static and Seat records are the drawn ones: {drawnGot:N0} of {drawnExpected:N0}");
    C(trianglesGot == boxesExpected * 12, $"every box of every Boxes and Trunk entry is welded: {trianglesGot / 12:N0} boxes of {boxesExpected:N0}");
    C(checkedRecords > 500 && worstOffset < 0.003, $"each of {checkedRecords:N0} sampled records has an instance whose anchor is on its point (worst {worstOffset * 1000:0.00} mm)");
  }
}

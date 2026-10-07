// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownBuild.cs and Towns/TownChunk.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// ONE ZONE'S PIECES, worked out on a worker thread from its records and its resolved palette: every piece's matrix and LOD reference point,
// sorted into 16 m cells by kind; the collider mesh welded from the pieces' collision boxes, one per Unity layer; the convex colliders that
// stay real; the lit copies and the seats (roles Copy and Seat). Pure arithmetic over data the main thread already read: no Unity object is
// made or read in Build (tools/placement-tests runs it without the game). The second half of this file is the main thread's side: the
// zone's Unity objects, and the collider's cook off the main thread.

/// <summary>One palette entry's instances in one zone, sorted by cell: the matrices the batches take, and what the cull-and-LOD job decides by.</summary>
internal sealed class KindInstances
{
  internal BakedKind Kind = null!;
  internal int Count;
  /// <summary>The piece's own matrix of each instance.</summary>
  internal Matrix4x4[] Root = [];
  /// <summary>Per distinct non-identity part matrix of the piece (PieceKind.Locals): Root times it, for each instance.</summary>
  internal Matrix4x4[][] Locals = [];
  /// <summary>Each instance's LOD reference point (world x, y, z) and the square of its largest scale.</summary>
  internal Vector4[] Lod = [];
  /// <summary>Each instance's combination of look variants and seed bucket; null for a piece with none.</summary>
  internal byte[]? Combo, Bucket;
  /// <summary>The instances of cell c (4 x 4 cells of 16 m, row 0 south) are [CellStart[c], CellStart[c + 1]).</summary>
  internal readonly int[] CellStart = new int[BakedDraw.Cells + 1];
  /// <summary>Per cell, the world box of its instances (a piece's radius around its origin): minimum x, y, z, then maximum.</summary>
  internal readonly float[] CellBox = new float[BakedDraw.Cells * 6];
}

/// <summary>The welded collision of one Unity layer of a zone, relative to the zone object.</summary>
internal sealed class ColliderMesh
{
  internal int Layer;
  internal Vector3[] Vertices = [];
  internal int[] Triangles = [];
}

/// <summary>A convex collider that stays a real component (stairs, glass): the mesh and where it stands relative to the zone object.</summary>
internal readonly struct ConvexCollider(Mesh? mesh, Matrix4x4 matrix, int layer)
{
  internal readonly Mesh? Mesh = mesh;
  internal readonly Matrix4x4 Matrix = matrix;
  internal readonly int Layer = layer;
}

/// <summary>A lit copy (role Copy) or a seat: the entry and the world matrix of its prefab.</summary>
internal readonly struct LocalProp(BakedKind kind, Matrix4x4 matrix)
{
  internal readonly BakedKind Kind = kind;
  internal readonly Matrix4x4 Matrix = matrix;
}

/// <summary>What the worker makes of one zone.</summary>
internal sealed class BuiltZone
{
  internal int Zx, Zz;
  /// <summary>The revision of the layer it was built from.</summary>
  internal uint Revision;
  internal KindInstances[] Kinds = [];
  /// <summary>Per cell, whether it holds an instance, and the world box of all of them.</summary>
  internal readonly bool[] CellUsed = new bool[BakedDraw.Cells];
  internal readonly float[] CellBox = new float[BakedDraw.Cells * 6];
  internal ColliderMesh[] Colliders = [];
  internal ConvexCollider[] Convex = [];
  internal LocalProp[] Copies = [], Seats = [];
  /// <summary>The world box of the copies' and the seats' positions (minimum x, y, z, then maximum), for the early-out of the near test.</summary>
  internal readonly float[] CopyBox = new float[6], SeatBox = new float[6];
  internal int Records, Drawn, Skipped;

  internal bool HasColliders => Colliders.Length > 0 || Convex.Length > 0;
  internal bool HasProps => Copies.Length > 0 || Seats.Length > 0;
  internal int ColliderTriangles
  {
    get
    {
      int n = 0;
      foreach (var c in Colliders)
        n += c.Triangles.Length / 3;
      return n;
    }
  }

  /// <summary>A zone with nothing in it, for a zone whose records are gone.</summary>
  internal static BuiltZone Empty(int zx, int zz, uint revision) => new() { Zx = zx, Zz = zz, Revision = revision };
}

/// <summary>Placement and welding arithmetic, free of the game: plain floats and Unity's value types, none of their native calls.</summary>
internal static class BakedMath
{
  /// <summary>A piece's rotation times its scale, as a 3 x 3 (row, column): A = R * S, so a column is the rotation's, as long as its axis' scale.</summary>
  internal struct Linear
  {
    internal float M00, M01, M02, M10, M11, M12, M20, M21, M22;
  }

  /// <summary>A turn about y by yaw degrees (Unity's: its +z toward (sin, cos)), scaled along its own axes.</summary>
  internal static Linear FromYaw(double yawDegrees, float sx, float sy, float sz)
  {
    double r = yawDegrees * (Math.PI / 180.0);
    float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
    return new Linear { M00 = c * sx, M02 = s * sz, M11 = sy, M20 = -s * sx, M22 = c * sz };
  }

  /// <summary>A rotation by a quaternion (x, y, z, w; normalised here), scaled along its own axes.</summary>
  internal static Linear FromQuaternion(float qx, float qy, float qz, float qw, float sx, float sy, float sz)
  {
    double n = Math.Sqrt((double)qx * qx + (double)qy * qy + (double)qz * qz + (double)qw * qw);
    if (n < 1e-9)
      return FromYaw(0, sx, sy, sz);
    double x = qx / n, y = qy / n, z = qz / n, w = qw / n;
    return new Linear
    {
      M00 = (float)(1 - 2 * (y * y + z * z)) * sx, M01 = (float)(2 * (x * y - z * w)) * sy, M02 = (float)(2 * (x * z + y * w)) * sz,
      M10 = (float)(2 * (x * y + z * w)) * sx, M11 = (float)(1 - 2 * (x * x + z * z)) * sy, M12 = (float)(2 * (y * z - x * w)) * sz,
      M20 = (float)(2 * (x * z - y * w)) * sx, M21 = (float)(2 * (y * z + x * w)) * sy, M22 = (float)(1 - 2 * (x * x + y * y)) * sz,
    };
  }

  /// <summary>The matrix of a piece placed so that its anchor lands on the point (px, py, pz): the pivot is the point less the anchor
  /// turned and scaled, worked out in double precision; <paramref name="originX"/> and <paramref name="originZ"/> are subtracted from x and z
  /// (a zone object's position for colliders, which are made relative to it so their floats stay small).</summary>
  internal static Matrix4x4 Place(in Linear a, double px, double py, double pz, Vector3 anchor, double originX = 0, double originZ = 0)
  {
    double tx = px - ((double)a.M00 * anchor.x + (double)a.M01 * anchor.y + (double)a.M02 * anchor.z) - originX;
    double ty = py - ((double)a.M10 * anchor.x + (double)a.M11 * anchor.y + (double)a.M12 * anchor.z);
    double tz = pz - ((double)a.M20 * anchor.x + (double)a.M21 * anchor.y + (double)a.M22 * anchor.z) - originZ;
    var m = new Matrix4x4();
    m.m00 = a.M00; m.m01 = a.M01; m.m02 = a.M02; m.m03 = (float)tx;
    m.m10 = a.M10; m.m11 = a.M11; m.m12 = a.M12; m.m13 = (float)ty;
    m.m20 = a.M20; m.m21 = a.M21; m.m22 = a.M22; m.m23 = (float)tz;
    m.m33 = 1f;
    return m;
  }

  /// <summary>The unit cube [-0.5, 0.5] moved to a box: scaled by its size, centred at its centre.</summary>
  internal static Matrix4x4 BoxMatrix(Vector3 centre, Vector3 size)
  {
    var m = new Matrix4x4();
    m.m00 = size.x; m.m11 = size.y; m.m22 = size.z; m.m33 = 1f;
    m.m03 = centre.x; m.m13 = centre.y; m.m23 = centre.z;
    return m;
  }

  /// <summary>a * b, for matrices whose last row is (0, 0, 0, 1).</summary>
  internal static Matrix4x4 Mul(in Matrix4x4 a, in Matrix4x4 b)
  {
    var m = new Matrix4x4();
    m.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20;
    m.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21;
    m.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22;
    m.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03;
    m.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20;
    m.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21;
    m.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22;
    m.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13;
    m.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20;
    m.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21;
    m.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22;
    m.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23;
    m.m33 = 1f;
    return m;
  }

  /// <summary>m applied to a point.</summary>
  internal static Vector3 Point(in Matrix4x4 m, Vector3 p) => new(
    m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
    m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
    m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);

  /// <summary>The determinant of the matrix's 3 x 3: negative for a mirror.</summary>
  internal static float Det3(in Matrix4x4 m) =>
    m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

  // the unit cube's corners (index = x + 2y + 4z, each -0.5 or +0.5) and its twelve triangles, wound to face outward
  private static readonly int[] CubeTriangles =
  [
    1, 3, 7, 1, 7, 5, 0, 4, 6, 0, 6, 2, 2, 6, 7, 2, 7, 3,
    0, 1, 5, 0, 5, 4, 4, 5, 7, 4, 7, 6, 0, 2, 3, 0, 3, 1,
  ];

  /// <summary>Welds a box (the unit cube moved by <paramref name="box"/>) into a mesh under construction; a mirrored box is wound the other way.</summary>
  internal static void AddBox(in Matrix4x4 box, List<Vector3> verts, List<int> tris)
  {
    int start = verts.Count;
    for (int i = 0; i < 8; i++)
      verts.Add(Point(box, new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f)));
    bool mirrored = Det3(box) < 0f;
    for (int t = 0; t < CubeTriangles.Length; t += 3)
    {
      tris.Add(start + CubeTriangles[t]);
      tris.Add(start + CubeTriangles[mirrored ? t + 2 : t + 1]);
      tris.Add(start + CubeTriangles[mirrored ? t + 1 : t + 2]);
    }
  }
}

internal static class BakedZoneBuild
{
  /// <summary>A tree's trunk: 0.6 m across and 3 m up from its foot, whatever the tree's size.</summary>
  internal static readonly Vector3 TrunkSize = new(0.6f, 3f, 0.6f);

  // a growing list of one layer's collision, and the group of instances one palette entry makes
  private sealed class Weld
  {
    internal readonly List<Vector3> Vertices = [];
    internal readonly List<int> Triangles = [];
  }

  private sealed class Group
  {
    internal BakedKind Kind = null!;
    internal int Count;
    internal readonly int[] PerCell = new int[BakedDraw.Cells];
  }

  /// <summary>
  /// Works out one zone. <paramref name="kinds"/> holds each palette entry resolved (null: the entry is not usable; its records are counted and
  /// skipped). A live record is the real piece the server seeds: it is neither drawn nor solid here.
  /// </summary>
  internal static BuiltZone Build(ZoneData data, BakedKind?[] kinds, uint revision)
  {
    var built = new BuiltZone { Zx = data.Zone.X, Zz = data.Zone.Z, Revision = revision, Records = data.Count };
    int n = data.Count;
    if (n == 0)
      return built;
    double ox = data.Zone.X * 64.0, oz = data.Zone.Z * 64.0;       // the zone object's position
    double x0 = data.Zone.OriginX, z0 = data.Zone.OriginZ;         // the zone's south-west corner

    var groups = new List<Group>();
    var groupOf = new Dictionary<BakedKind, int>();
    var group = new int[n];
    var cellOf = new byte[n];
    var root = new Matrix4x4[n];
    var lod = new Vector4[n];
    var combo = new byte[n];
    var bucket = new byte[n];
    var welds = new Dictionary<int, Weld>();
    var convex = new List<ConvexCollider>();
    var copies = new List<LocalProp>();
    var seats = new List<LocalProp>();
    var boxes = new float[BakedDraw.Cells * 6];          // per cell, all kinds
    var copyBox = NewBox();
    var seatBox = NewBox();
    for (int c = 0; c < BakedDraw.Cells; c++)
      Reset(boxes, c * 6);

    for (int i = 0; i < n; i++)
    {
      group[i] = -1;
      int e = data.Palette[i];
      var kind = e < kinds.Length ? kinds[e] : null;
      if (kind == null)
      {
        built.Skipped++;
        continue;
      }
      if (kind.Role == BakedRole.Live)
        continue;
      var flags = data.Flags[i];
      double px = x0 + data.X[i] / BakedFormat.UnitsPerMetre, py = data.Y[i] / 1000.0, pz = z0 + data.Z[i] / BakedFormat.UnitsPerMetre;
      float sx = 1f, sy = 1f, sz = 1f;
      ZoneRecord record = default;
      if ((flags & (RecordFlags.FullRotation | RecordFlags.Scale)) != 0)
      {
        record = data.Record(i);
        if (record.HasScale)
        {
          sx = record.ScaleX / 1000f; sy = record.ScaleY / 1000f; sz = record.ScaleZ / 1000f;
        }
      }
      BakedMath.Linear a;
      if ((flags & RecordFlags.FullRotation) != 0)
      {
        var q = record.Packed.ToQuaternion();
        a = BakedMath.FromQuaternion(q.x, q.y, q.z, q.w, sx, sy, sz);
      }
      else
        a = BakedMath.FromYaw(BakedFormat.YawDegrees(data.Yaw[i]), sx, sy, sz);

      bool draws = kind.Draws;
      bool needsFrame = draws || kind.Prefab != null && (kind.Role == BakedRole.Copy || kind.Role == BakedRole.Seat) || kind.Collision == BakedCollision.Prefab;
      Matrix4x4 m = default;
      if (needsFrame)
        m = BakedMath.Place(a, px, py, pz, kind.Anchor);

      // the pieces' own drawing: sorted into cells and kinds
      if (draws)
      {
        var piece = kind.Piece!;
        float s = Math.Max(sx, Math.Max(sy, sz));
        int cx = Math.Max(0, Math.Min(BakedDraw.CellsAcross - 1, (int)Math.Floor((px - x0) / BakedDraw.CellSize)));
        int cz = Math.Max(0, Math.Min(BakedDraw.CellsAcross - 1, (int)Math.Floor((pz - z0) / BakedDraw.CellSize)));
        int cell = cz * BakedDraw.CellsAcross + cx;
        if (!groupOf.TryGetValue(kind, out int g))
        {
          g = groups.Count;
          groupOf[kind] = g;
          groups.Add(new Group { Kind = kind });
        }
        groups[g].Count++;
        groups[g].PerCell[cell]++;
        group[i] = g;
        cellOf[i] = (byte)cell;
        root[i] = m;
        var r = BakedMath.Point(m, piece.LodCenter);
        lod[i] = new Vector4(r.x, r.y, r.z, s * s);
        Grow(boxes, cell * 6, m.m03, m.m13, m.m23, piece.Radius * s);
        if (piece.Slots.Length > 0 || piece.HasBuckets)
        {
          uint h = BakedFormat.LookHash(px, py, pz);
          int c = 0;
          for (int k = 0; k < piece.Slots.Length; k++)
          {
            int fixedVariant = k < kind.FixedVariant.Length ? kind.FixedVariant[k] : -1;
            int v = fixedVariant >= 0 ? fixedVariant : BakedLook.Variant(h, piece.Slots[k].Key, piece.Slots[k].Weights);
            c += v * piece.Slots[k].Stride;
          }
          combo[i] = (byte)c;
          int seed = (flags & RecordFlags.Seed) != 0 ? data.Record(i).Seed : BakedFormat.DerivedSeed(h);
          bucket[i] = (byte)BakedLook.Bucket(seed);
        }
        built.Drawn++;
      }

      if (kind.Role == BakedRole.Copy && kind.Prefab != null)
      {
        copies.Add(new LocalProp(kind, m));
        Grow(copyBox, 0, m.m03, m.m13, m.m23, 0f);
      }
      else if (kind.Role == BakedRole.Seat && kind.Prefab != null && kind.Piece != null && kind.Piece.HasChair)
      {
        seats.Add(new LocalProp(kind, m));
        Grow(seatBox, 0, m.m03, m.m13, m.m23, 0f);
      }

      Collide(kind, a, px, py, pz, ox, oz, welds, convex);
    }

    // the instances, sorted into cells within each kind
    var all = new KindInstances[groups.Count];
    for (int g = 0; g < groups.Count; g++)
    {
      var gr = groups[g];
      var piece = gr.Kind.Piece!;
      var ki = new KindInstances
      {
        Kind = gr.Kind, Count = gr.Count,
        Root = new Matrix4x4[gr.Count], Lod = new Vector4[gr.Count],
        Locals = new Matrix4x4[piece.Locals.Length][],
        Combo = piece.Slots.Length > 0 ? new byte[gr.Count] : null,
        Bucket = piece.HasBuckets ? new byte[gr.Count] : null,
      };
      for (int j = 0; j < piece.Locals.Length; j++)
        ki.Locals[j] = new Matrix4x4[gr.Count];
      int at = 0;
      for (int c = 0; c < BakedDraw.Cells; c++)
      {
        ki.CellStart[c] = at;
        at += gr.PerCell[c];
        Reset(ki.CellBox, c * 6);
      }
      ki.CellStart[BakedDraw.Cells] = at;
      all[g] = ki;
    }
    var next = new int[groups.Count][];
    for (int g = 0; g < groups.Count; g++)
      next[g] = (int[])all[g].CellStart.Clone();
    for (int i = 0; i < n; i++)
    {
      int g = group[i];
      if (g < 0)
        continue;
      var ki = all[g];
      var piece = ki.Kind.Piece!;
      int cell = cellOf[i];
      int pos = next[g][cell]++;
      ki.Root[pos] = root[i];
      ki.Lod[pos] = lod[i];
      if (ki.Combo != null) ki.Combo[pos] = combo[i];
      if (ki.Bucket != null) ki.Bucket[pos] = bucket[i];
      for (int j = 0; j < piece.Locals.Length; j++)
        ki.Locals[j][pos] = BakedMath.Mul(root[i], piece.Locals[j]);
      float reach = piece.Radius * (float)Math.Sqrt(lod[i].w);
      Grow(ki.CellBox, cell * 6, root[i].m03, root[i].m13, root[i].m23, reach);
    }
    built.Kinds = all;
    for (int c = 0; c < BakedDraw.Cells; c++)
    {
      built.CellUsed[c] = boxes[c * 6] <= boxes[c * 6 + 3];
      Array.Copy(boxes, c * 6, built.CellBox, c * 6, 6);
    }

    var meshes = new List<ColliderMesh>();
    foreach (var kv in welds)
      if (kv.Value.Triangles.Count > 0)
        meshes.Add(new ColliderMesh { Layer = kv.Key, Vertices = kv.Value.Vertices.ToArray(), Triangles = kv.Value.Triangles.ToArray() });
    built.Colliders = meshes.ToArray();
    built.Convex = convex.ToArray();
    built.Copies = copies.ToArray();
    built.Seats = seats.ToArray();
    Array.Copy(copyBox, built.CopyBox, 6);
    Array.Copy(seatBox, built.SeatBox, 6);
    return built;
  }

  // what a record is to the physics: canonical boxes (in the first candidate's frame), a trunk, or the drawn prefab's own colliders
  private static void Collide(BakedKind kind, in BakedMath.Linear a, double px, double py, double pz, double ox, double oz,
    Dictionary<int, Weld> welds, List<ConvexCollider> convex)
  {
    switch (kind.Collision)
    {
      case BakedCollision.Boxes:
      {
        if (kind.Boxes.Length == 0)
          return;
        // R1: the boxes follow the first candidate's anchor, whichever candidate is drawn
        var frame = BakedMath.Place(a, px, py, pz, kind.Anchor0, ox, oz);
        var weld = WeldOf(welds, kind.UnityLayer);
        foreach (var box in kind.Boxes)
          BakedMath.AddBox(BakedMath.Mul(frame, box), weld.Vertices, weld.Triangles);
        return;
      }
      case BakedCollision.Trunk:
      {
        var foot = BakedMath.Place(a, px, py, pz, kind.Anchor0, ox, oz);
        var weld = WeldOf(welds, kind.UnityLayer);
        BakedMath.AddBox(BakedMath.BoxMatrix(new Vector3(foot.m03, foot.m13 + TrunkSize.y * 0.5f, foot.m23), TrunkSize), weld.Vertices, weld.Triangles);
        return;
      }
      case BakedCollision.Prefab:
      {
        var piece = kind.Piece;
        if (piece == null)
          return;
        var rel = BakedMath.Place(a, px, py, pz, kind.Anchor, ox, oz);
        var weld = WeldOf(welds, kind.UnityLayer);
        foreach (var box in piece.Boxes)
          BakedMath.AddBox(BakedMath.Mul(rel, box), weld.Vertices, weld.Triangles);
        for (int c = 0; c < piece.MeshVertices.Length; c++)
        {
          int start = weld.Vertices.Count;
          foreach (var v in piece.MeshVertices[c])
            weld.Vertices.Add(BakedMath.Point(rel, v));
          foreach (int t in piece.MeshTriangles[c])
            weld.Triangles.Add(start + t);
        }
        for (int c = 0; c < piece.ConvexMeshes.Length; c++)
          convex.Add(new ConvexCollider(piece.ConvexMeshes[c], BakedMath.Mul(rel, piece.ConvexLocal[c]), kind.UnityLayer));
        return;
      }
    }
  }

  private static Weld WeldOf(Dictionary<int, Weld> welds, int layer)
  {
    if (!welds.TryGetValue(layer, out var w))
      welds[layer] = w = new Weld();
    return w;
  }

  private static float[] NewBox()
  {
    var box = new float[6];
    Reset(box, 0);
    return box;
  }

  private static void Reset(float[] box, int at)
  {
    box[at] = box[at + 1] = box[at + 2] = float.MaxValue;
    box[at + 3] = box[at + 4] = box[at + 5] = float.MinValue;
  }

  // a box takes in the cube of side 2 * reach around a point
  private static void Grow(float[] box, int at, float x, float y, float z, float reach)
  {
    if (x - reach < box[at]) box[at] = x - reach;
    if (y - reach < box[at + 1]) box[at + 1] = y - reach;
    if (z - reach < box[at + 2]) box[at + 2] = z - reach;
    if (x + reach > box[at + 3]) box[at + 3] = x + reach;
    if (y + reach > box[at + 4]) box[at + 4] = y + reach;
    if (z + reach > box[at + 5]) box[at + 5] = z + reach;
  }
}

/// <summary>
/// The Unity objects of one built zone: one GameObject at the zone's origin, with its welded colliders, its convex colliders and, for the zone
/// that is drawn, its lit copies and seats (BakedCopies). The welded meshes are cooked off the main thread (Physics.BakeMesh is thread safe)
/// and assigned to their MeshColliders on the main thread; until then a MeshCollider has no mesh and does nothing.
/// </summary>
internal sealed class ZoneObjects
{
  internal readonly BuiltZone Zone;
  internal GameObject? Root;
  private readonly List<Mesh> meshes = [];
  private readonly List<(MeshCollider Collider, Mesh Mesh)> waiting = [];
  private int cooking;
  private bool dead;

  /// <summary>Every welded collider has its mesh (a zone with none is ready at once).</summary>
  internal bool CollidersAssigned { get; private set; }

  /// <summary>The copies and seats of the zone that are made near the player.</summary>
  internal BakedCopies.Props? Props;

  internal ZoneObjects(BuiltZone zone)
  {
    Zone = zone;
  }

  /// <summary>The root and the colliders, on the main thread. The root is inactive when <paramref name="active"/> is false (a zone waiting for its
  /// hand-over: its colliders are cooked but do not stand yet).</summary>
  internal void Create(bool active)
  {
    var root = new GameObject($"BC baked {Zone.Zx},{Zone.Zz}");
    root.transform.position = new Vector3(Zone.Zx * 64f, 0f, Zone.Zz * 64f);
    root.SetActive(active);
    Root = root;
    foreach (var cm in Zone.Colliders)
    {
      var mesh = new Mesh { name = "BC baked collider", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
      mesh.SetVertices(cm.Vertices);
      mesh.SetTriangles(cm.Triangles, 0);
      meshes.Add(mesh);
      var go = new GameObject("collider") { layer = cm.Layer };
      go.transform.SetParent(root.transform, false);
      waiting.Add((go.AddComponent<MeshCollider>(), mesh));
    }
    foreach (var c in Zone.Convex)
    {
      if (c.Mesh == null)
        continue;
      var go = new GameObject("convex") { layer = c.Layer };
      go.transform.SetParent(root.transform, false);
      go.transform.localPosition = new Vector3(c.Matrix.m03, c.Matrix.m13, c.Matrix.m23);
      go.transform.localRotation = c.Matrix.rotation;
      go.transform.localScale = c.Matrix.lossyScale;
      var mc = go.AddComponent<MeshCollider>();
      mc.convex = true;
      mc.sharedMesh = c.Mesh;
    }
    // the cook, off the main thread (a coroutine's guard in VALtima's chunk; the manager's poll here)
    foreach (var (_, mesh) in waiting)
    {
      int id = mesh.GetInstanceID();
      Interlocked.Increment(ref cooking);
      ThreadPool.QueueUserWorkItem(_ =>
      {
        try
        {
          Physics.BakeMesh(id, false);
        }
        catch (Exception)
        {
          // the mesh is assigned all the same, and cooked on the main thread then
        }
        Interlocked.Decrement(ref cooking);
      });
    }
    if (waiting.Count == 0)
      CollidersAssigned = true;
  }

  /// <summary>Main thread, every frame: assigns the cooked meshes. True once all are in place.</summary>
  internal bool Poll()
  {
    if (CollidersAssigned || dead)
      return CollidersAssigned;
    if (Volatile.Read(ref cooking) > 0)
      return false;
    foreach (var (collider, mesh) in waiting)
      if (collider != null)
        collider.sharedMesh = mesh;
    waiting.Clear();
    CollidersAssigned = true;
    return true;
  }

  /// <summary>Stands the zone's colliders up (a zone that was waiting for its hand-over).</summary>
  internal void Activate()
  {
    if (Root != null)
      Root.SetActive(true);
  }

  /// <summary>Takes the zone's objects away. A cook still running is waited for (the mesh must outlive it).</summary>
  internal void Destroy()
  {
    if (dead)
      return;
    dead = true;
    Props?.Release();
    Props = null;
    if (Root != null)
      UnityEngine.Object.Destroy(Root);
    Root = null;
    if (Volatile.Read(ref cooking) == 0)
      FreeMeshes();
    else if (BetterContinents.instance != null)
      BetterContinents.instance.StartCoroutine(FreeLater());
  }

  private System.Collections.IEnumerator FreeLater()
  {
    float until = Time.realtimeSinceStartup + 10f;
    while (Volatile.Read(ref cooking) > 0 && Time.realtimeSinceStartup < until)
      yield return null;
    FreeMeshes();
  }

  private void FreeMeshes()
  {
    foreach (var mesh in meshes)
      if (mesh != null)
        UnityEngine.Object.Destroy(mesh);
    meshes.Clear();
  }
}

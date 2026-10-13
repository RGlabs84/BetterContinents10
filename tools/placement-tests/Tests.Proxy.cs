// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// The shadow proxies (BakedProxyMerge.cs, BakedProxyBook.cs; the engine's side, BakedProxy.cs, needs the GPU and is not here): the merge of known
// parts and matrices into vertices, normals and indices (a mirrored matrix turns the triangles round), the chunks and their layers, the level of
// detail by distance, what a proxy leaves to the instancing (cutout materials, unreadable meshes, parts that do not cast), the numbers read back from
// a GPU, how the drawing job routes a covered zone's instances, and the book's life of a proxy: made in steps, drawn, taken back only after a job
// that knows has been adopted, freed when its zone is rebuilt, unloaded or out of the ring. The merge is two steps (a plan of the chunks, then the vertices
// packed into stores a worker owns): every store made is released or handed on, on every path, and none is released while a worker is in its build.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlacementTests;

internal static class ProxyKit
{
  /// <summary>A triangle in the x-y plane facing +z: (0,0,0), (1,0,0), (0,1,0), counter-clockwise seen from +z.</summary>
  public static ProxyShape Triangle() =>
    ProxyShape.Make([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], [Vector3.forward, Vector3.forward, Vector3.forward], [0, 1, 2])!;

  /// <summary>A fan of <paramref name="triangles"/> triangles from one corner: triangles + 2 vertices, all facing +z.</summary>
  public static ProxyShape Fan(int triangles)
  {
    var v = new List<Vector3> { Vector3.zero };
    var t = new List<int>();
    for (int i = 0; i <= triangles; i++)
      v.Add(new Vector3((float)Math.Cos(i * 0.1), (float)Math.Sin(i * 0.1), 0f));
    for (int i = 0; i < triangles; i++)
    {
      t.Add(0);
      t.Add(i + 1);
      t.Add(i + 2);
    }
    return ProxyShape.Make(v.ToArray(), null, t.ToArray())!;
  }

  /// <summary>A part the proxy can take: settled, with its shape.</summary>
  public static RenderPart Take(RenderPart part, ProxyShape shape)
  {
    part.Shape = shape;
    part.ProxyState = RenderPart.ProxyOk;
    return part;
  }

  /// <summary>A kind with one part at LOD0 (shape near) and one at the last LOD (shape far), as ClientKit.TwoLods lays them out.</summary>
  public static BakedKind TwoLodKind(string name, ProxyShape near, ProxyShape far, int layer = 10, PaletteFlags flags = PaletteFlags.None)
  {
    var piece = ClientKit.TwoLods(name, 4f, 0.2f, 0.05f, radius: 2f);
    Take(piece.Parts[0], near);
    Take(piece.Parts[1], far);
    return ClientKit.Kind(name, piece, layer: layer, flags: flags);
  }

  public static ProxyFrame Frame(BuiltZone[] ring, int ringVersion, Vector3 camera, BakedShadows policy = BakedShadows.All, bool enabled = true, bool hidden = false,
    float reach = 5000f, float now = 0f) => new()
    {
      Camera = camera, ZoneX = (int)Math.Round(camera.x / 64.0), ZoneZ = (int)Math.Round(camera.z / 64.0), Ring = ring, RingVersion = ringVersion,
      Policy = policy, Enabled = enabled, Hidden = hidden, Reach = reach, Now = now,
    };

  /// <summary>A store that remembers what became of it.</summary>
  internal sealed class TrackedStore : ByteStore
  {
    public bool Consumed;
    public int Releases;
    internal override void Release()
    {
      Releases++;
      base.Release();
    }
  }

  /// <summary>A book with the engine stood in for: workers run at once, every part is settled, uploads and frees are noted, stores are tracked.</summary>
  public sealed class Fake
  {
    public readonly List<ProxyChunk> Uploaded = [], Freed = [], Drawn = [];
    public readonly List<TrackedStore> Stores = [];
    public readonly ProxyBook Book;
    public Fake()
    {
      Book = new ProxyBook
      {
        Work = job => job(),
        Settle = _ => true,
        NewStore = _ =>
        {
          var store = new TrackedStore();
          Stores.Add(store);
          return store;
        },
        Upload = (chunk, _) =>
        {
          Consume(chunk);
          Uploaded.Add(chunk);
          return true;
        },
        Free = Freed.Add,
        Draw = (chunk, _) => Drawn.Add(chunk),
      };
    }

    /// <summary>What the engine does with a store it makes a mesh from.</summary>
    public static void Consume(ProxyChunk chunk)
    {
      if (chunk.Store is TrackedStore tracked)
        tracked.Consumed = true;
      chunk.Store = null;
    }

    /// <summary>Every store made was either handed on to a mesh or released, none twice, once the book has nothing in flight.</summary>
    public bool NoLeaks(out string why)
    {
      foreach (var s in Stores)
      {
        if (s.Consumed && s.Releases > 0)
        {
          why = "a store was handed on and released";
          return false;
        }
        if (s.Releases > 1)
        {
          why = "a store was released twice";
          return false;
        }
        if (!s.Consumed && s.Releases == 0)
        {
          why = "a store was neither handed on nor released";
          return false;
        }
      }
      why = "";
      return true;
    }
  }

  /// <summary>
  /// A chunk's numbers read back from its store, as the GPU and the shader would read them: positions less the zone's origin on x and z (fractions of the chunk's box
  /// carried to it), normals as the shader's inverse transpose of the chunk's matrix lands on them, indices.
  /// </summary>
  public static (Vector3[] Positions, Vector3[] Normals, int[] Indices) Read(ProxyChunk c)
  {
    var store = (ByteStore)c.Store!;
    var layout = store.Layout!;
    var p = new Vector3[store.VertexCount];
    var n = new Vector3[store.VertexCount];
    var pos = layout.Find(UnityEngine.Rendering.VertexAttribute.Position)!.Value;
    var nrm = layout.Find(UnityEngine.Rendering.VertexAttribute.Normal)!.Value;
    var box = c.Quant;
    for (int v = 0; v < p.Length; v++)
    {
      int at = v * layout.Stride;
      if (pos.Format == UnityEngine.Rendering.VertexAttributeFormat.Float32)
        p[v] = new Vector3(BitConverter.ToSingle(store.Vertices, at + pos.Offset), BitConverter.ToSingle(store.Vertices, at + pos.Offset + 4), BitConverter.ToSingle(store.Vertices, at + pos.Offset + 8));
      else
        p[v] = new Vector3(box.MinX + BitConverter.ToUInt16(store.Vertices, at + pos.Offset) / 65535f * box.SizeX, box.MinY + BitConverter.ToUInt16(store.Vertices, at + pos.Offset + 2) / 65535f * box.SizeY,
          box.MinZ + BitConverter.ToUInt16(store.Vertices, at + pos.Offset + 4) / 65535f * box.SizeZ);
      if (nrm.Format == UnityEngine.Rendering.VertexAttributeFormat.Float32)
        n[v] = new Vector3(BitConverter.ToSingle(store.Vertices, at + nrm.Offset), BitConverter.ToSingle(store.Vertices, at + nrm.Offset + 4), BitConverter.ToSingle(store.Vertices, at + nrm.Offset + 8));
      else
        n[v] = Shader(box, Snorm(store.Vertices, at + nrm.Offset));
    }
    var idx = new int[store.IndexCount];
    for (int i = 0; i < idx.Length; i++)
      idx[i] = store.Wide ? BitConverter.ToInt32(store.Indices, i * 4) : BitConverter.ToUInt16(store.Indices, i * 2);
    return (p, n, idx);
  }

  /// <summary>Three signed bytes as the GPU reads them: value / 127, at least -1.</summary>
  public static Vector3 Snorm(byte[] bytes, int at) =>
    new(Math.Max((sbyte)bytes[at] / 127f, -1f), Math.Max((sbyte)bytes[at + 1] / 127f, -1f), Math.Max((sbyte)bytes[at + 2] / 127f, -1f));

  /// <summary>What the piece shader makes of a stored normal: the inverse transpose of the chunk's matrix (its box's scale, so the stored value divided by the box's size), made unit.</summary>
  public static Vector3 Shader(QuantBox box, Vector3 stored)
  {
    var m = box.ToWorld(0f, 0f);
    double x = stored.x / m.m00, y = stored.y / m.m11, z = stored.z / m.m22;
    double len = Math.Sqrt(x * x + y * y + z * z);
    return len < 1e-20 ? Vector3.up : new Vector3((float)(x / len), (float)(y / len), (float)(z / len));
  }

  /// <summary>The geometric normal of a triangle of a chunk, by its winding (counter-clockwise seen from the front).</summary>
  public static Vector3 Facing(Vector3[] v, int[] t, int at)
  {
    Vector3 a = v[t[at]], b = v[t[at + 1]], c = v[t[at + 2]];
    var u = b - a;
    var w = c - a;
    return new Vector3(u.y * w.z - u.z * w.y, u.z * w.x - u.x * w.z, u.x * w.y - u.y * w.x);
  }

  public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
}

internal static partial class Tests
{
  // Unity's Vector3.ToString needs a module that is not here
  private static string Fmt3(Vector3 v) => FormattableString.Invariant($"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})");

  private static bool ProxyNear(Vector3 a, Vector3 b, float tolerance = 0.01f) => Math.Abs(a.x - b.x) < tolerance && Math.Abs(a.y - b.y) < tolerance && Math.Abs(a.z - b.z) < tolerance;

  // ---- the merge --------------------------------------------------------------------------------------------------------------------------

  private static void ProxyMergeTest()
  {
    Section("proxy: merging parts and matrices");
    var tri = ProxyKit.Triangle();
    var piece = ClientKit.NoLods("post", 1f);
    ProxyKit.Take(piece.Parts[0], tri);
    var kind = ClientKit.Kind("post", piece);
    // a record at (10, 2, 20) in zone (1, 0), whose origin is x = 64: the vertices come out relative to it
    var zone = BakedZoneBuild.Build(ClientKit.Zone(1, 0, (0, 74.0, 2.0, 20.0, 0.0, null)), [kind], 1);
    var built = ProxyMerge.Merge(zone, true, BakedShadows.All);
    var chunk = built.Chunks.Single();
    var (cv, cn, ci) = ProxyKit.Read(chunk);
    C(chunk.VertexCount == 3 && chunk.IndexCount == 3 && chunk.Layer == 10, "one instance of one triangle: one chunk of 3 vertices and 3 indices");
    C(ProxyNear(cv[0], new Vector3(10, 2, 20)) && ProxyNear(cv[1], new Vector3(11, 2, 20)) && ProxyNear(cv[2], new Vector3(10, 3, 20)),
      $"the vertices are the instance's, relative to the zone's origin on x and z ({Fmt3(cv[0])}, {Fmt3(cv[1])}, {Fmt3(cv[2])})");
    C(cn.All(n => ProxyNear(n, Vector3.forward)) && ci.SequenceEqual([0, 1, 2]), "the normals face +z and the winding is as it was");
    C(ProxyNear(new Vector3(chunk.MinX, chunk.MinY, chunk.MinZ), new Vector3(10, 2, 20)) && ProxyNear(new Vector3(chunk.MaxX, chunk.MaxY, chunk.MaxZ), new Vector3(11, 3, 20)),
      "the chunk's box is that of its vertices");
    C(built.KindCovered.SequenceEqual([true]) && built.Triangles == 1 && built.Vertices == 3 && built.Bytes == 3 * 12 + 3 * 2, "the kind is covered; triangles, vertices and bytes are counted (a 12 byte vertex, a 16 bit index)");

    // a turn of 90 degrees about y carries x to -z and the normal to +x
    var turned = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 1.0, 5.0, 90.0, null)), [kind], 1);
    var (tv, tn, _) = ProxyKit.Read(ProxyMerge.Merge(turned, true, BakedShadows.All).Chunks.Single());
    C(ProxyNear(tv[1], new Vector3(5, 1, 4)) && ProxyNear(tv[2], new Vector3(5, 2, 5)), $"yaw 90: the triangle's x edge points along -z ({Fmt3(tv[1])})");
    C(tn.All(n => ProxyNear(n, Vector3.right, 0.02f)), "...and its normal along +x");

    // a part that stands in the piece at a place of its own (a local matrix): the instance's matrix times it
    var movedPiece = ClientKit.NoLods("door", 1f);
    ProxyKit.Take(movedPiece.Parts[0], tri);
    var local = BakedMath.Frame(BakedMath.FromYaw(0, 1f, 1f, 1f), new Vector3(0, 10, 0));
    movedPiece.Locals = [local];
    movedPiece.Parts[0].LocalIndex = 0;
    movedPiece.Parts[0].Local = local;
    movedPiece.Parts[0].LocalIsIdentity = false;
    var door = ClientKit.Kind("door", movedPiece);
    var doorZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 3.0, 1.0, 4.0, 0.0, null)), [door], 1);
    var (dv, _, _) = ProxyKit.Read(ProxyMerge.Merge(doorZone, true, BakedShadows.All).Chunks.Single());
    C(ProxyNear(dv[0], new Vector3(3, 11, 4)), $"a part's own matrix is applied below the instance's ({Fmt3(dv[0])})");

    // the transform itself, over many matrices with mirrors and uneven scales: the winding stays outward and the normals are the inverse transpose
    var rnd = new System.Random(20261008);
    int wrong = 0, mirrored = 0;
    for (int n = 0; n < 400; n++)
    {
      var rot = BakedMath.Euler((float)(rnd.NextDouble() * 360), (float)(rnd.NextDouble() * 360), (float)(rnd.NextDouble() * 360),
        (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)), (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)),
        (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)));
      var m = BakedMath.Frame(rot, new Vector3((float)(rnd.NextDouble() * 100 - 50), (float)(rnd.NextDouble() * 10), (float)(rnd.NextDouble() * 100 - 50)));
      if (BakedMath.Det3(m) < 0f)
        mirrored++;
      var v = new Vector3[3];
      var nm = new Vector3[3];
      var ix = new int[3];
      C(ProxyMerge.Transform(tri, m, 0f, 0f, v, nm, 0, ix, 0), "a matrix with an inverse is transformed");
      var facing = ProxyKit.Facing(v, ix, 0);
      var expected = InverseTransposeNormal(m, Vector3.forward);
      float len = (float)Math.Sqrt(ProxyKit.Dot(facing, facing));
      bool outward = ProxyKit.Dot(facing, nm[0]) > 0f && ProxyKit.Dot(facing, facing) > 0f;
      bool parallel = len > 1e-6f && ProxyKit.Dot(facing, expected) / len > 0.9999f;
      bool normalOk = ProxyNear(nm[0], expected, 0.001f) && ProxyNear(nm[1], expected, 0.001f);
      if (!(outward && parallel && normalOk))
        wrong++;
    }
    C(wrong == 0 && mirrored > 50, $"400 random matrices ({mirrored} mirrored): every triangle still faces its normal, and every normal is the inverse transpose's ({wrong} wrong)");

    // a mirrored matrix, by hand: x -> -x turns the triangle round
    var mirror = BakedMath.Frame(BakedMath.FromYaw(0, -1f, 1f, 1f), Vector3.zero);
    var mv = new Vector3[3];
    var mn = new Vector3[3];
    var mi = new int[3];
    ProxyMerge.Transform(tri, mirror, 0f, 0f, mv, mn, 0, mi, 0);
    C(mi.SequenceEqual([0, 2, 1]) && ProxyNear(mv[1], new Vector3(-1, 0, 0)) && ProxyNear(mn[0], Vector3.forward), "a mirrored scale (-1, 1, 1): the second and third indices swap, the normal still faces +z");
    var unmirrored = BakedMath.Frame(BakedMath.FromYaw(0, 2f, 3f, 1f), Vector3.zero);
    ProxyMerge.Transform(tri, unmirrored, 0f, 0f, mv, mn, 0, mi, 0);
    C(mi.SequenceEqual([0, 1, 2]), "an unmirrored one keeps the order");
    var flat = BakedMath.Frame(BakedMath.FromYaw(0, 1f, 0f, 1f), Vector3.zero);
    C(!ProxyMerge.Transform(tri, flat, 0f, 0f, mv, mn, 0, mi, 0), "a matrix with no inverse adds nothing");
    // vertices are written where asked, with their indices counted from there
    var big = new Vector3[10];
    var bign = new Vector3[10];
    var bigi = new int[10];
    ProxyMerge.Transform(tri, unmirrored, 0f, 0f, big, bign, 4, bigi, 6);
    C(bigi[6] == 4 && bigi[7] == 5 && bigi[8] == 6 && big[5].x > 1.9f, "a second shape is written after the first, its indices offset to match");
  }

  private static Vector3 InverseTransposeNormal(Matrix4x4 m, Vector3 n)
  {
    double a = m.m00, b = m.m01, c = m.m02, d = m.m10, e = m.m11, f = m.m12, g = m.m20, h = m.m21, i = m.m22;
    double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    // inverse = adjugate / det; its transpose times n
    double[,] inv =
    {
      { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
      { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
      { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
    };
    double x = inv[0, 0] * n.x + inv[1, 0] * n.y + inv[2, 0] * n.z;
    double y = inv[0, 1] * n.x + inv[1, 1] * n.y + inv[2, 1] * n.z;
    double z = inv[0, 2] * n.x + inv[1, 2] * n.y + inv[2, 2] * n.z;
    double len = Math.Sqrt(x * x + y * y + z * z);
    return new Vector3((float)(x / len), (float)(y / len), (float)(z / len));
  }

  // ---- chunks ----------------------------------------------------------------------------------------------------------------------------

  private static void ProxyChunkTest()
  {
    Section("proxy: chunks");
    var fan = ProxyKit.Fan(98);   // 100 vertices
    var piece = ClientKit.NoLods("wall", 1f);
    ProxyKit.Take(piece.Parts[0], fan);
    var wall = ClientKit.Kind("wall", piece, layer: 10);
    var piece2 = ClientKit.NoLods("beam", 1f);
    ProxyKit.Take(piece2.Parts[0], fan);
    var beam = ClientKit.Kind("beam", piece2, layer: 12);
    var records = Enumerable.Range(0, 50).Select(i => (i % 2, -30.0 + i % 60, 0.0, -30.0 + i * 7 % 60, 0.0, (Vector3?)null)).ToArray();
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), [wall, beam], 1);
    var built = ProxyMerge.Merge(zone, true, BakedShadows.All, maxVertices: 1000);
    C(built.Vertices == 5000 && built.Triangles == 50 * 98, $"all 50 instances are merged ({built.Vertices} vertices, {built.Triangles} triangles)");
    C(built.Chunks.All(c => c.VertexCount <= 1000 && c.VertexCount > 0), "no chunk takes more than the cap of 1,000 vertices");
    C(built.Chunks.Length == 6 && built.Chunks.Select(c => c.Layer).Distinct().Count() == 2, $"two layers, three chunks each ({built.Chunks.Length} chunks)");
    bool clean = true, boxed = true;
    foreach (var c in built.Chunks)
    {
      var (v, _, ix) = ProxyKit.Read(c);
      clean &= ix.All(i => i >= 0 && i < c.VertexCount) && v.Length == c.VertexCount && ix.Length == c.IndexCount;
      boxed &= v.All(p => p.x >= c.MinX - 1e-3f && p.x <= c.MaxX + 1e-3f && p.y >= c.MinY - 1e-3f && p.y <= c.MaxY + 1e-3f && p.z >= c.MinZ - 1e-3f && p.z <= c.MaxZ + 1e-3f);
    }
    C(clean, "each chunk's indices stay inside its own vertices");
    C(boxed, "each chunk's box holds its vertices");
    C(built.Chunks.Where(c => c.Layer == 10).All(c => c.Layer == 10) && built.Chunks.Count(c => c.Layer == 12) == 3, "a chunk holds the pieces of one layer");
    // an instance bigger than the cap stands alone; the cap never splits an instance
    var huge = ProxyMerge.Merge(zone, true, BakedShadows.All, maxVertices: 50);
    C(huge.Chunks.Length == 50 && huge.Chunks.All(c => c.VertexCount == 100), "an instance larger than the cap is a chunk of its own");
    // the cells order the chunks: pieces that stand together are merged together, so a chunk's box is small
    var spread = BakedZoneBuild.Build(ClientKit.Zone(0, 0, Enumerable.Range(0, 16).Select(i => (0, -30.0 + (i % 4) * 16 + 1, 0.0, -30.0 + (i / 4) * 16 + 1, 0.0, (Vector3?)null)).ToArray()), [wall], 1);
    var cells = ProxyMerge.Merge(spread, true, BakedShadows.All, maxVertices: 250);
    C(cells.Chunks.Length == 8 && cells.Chunks.All(c => c.MaxX - c.MinX < 40f && c.MaxZ - c.MinZ < 40f),
      $"16 pieces, two to a chunk: each chunk is two neighbouring cells ({cells.Chunks.Length} chunks, widest {cells.Chunks.Max(c => c.MaxX - c.MinX):0} m)");
    // nothing to merge
    var none = ProxyMerge.Merge(BuiltZone.Empty(0, 0, 1), true, BakedShadows.All);
    C(none.Chunks.Length == 0 && none.Bytes == 0, "an empty zone makes no chunk");
  }

  // ---- level of detail, and what casts ---------------------------------------------------------------------------------------------

  private static void ProxyLodTest()
  {
    Section("proxy: level of detail and who casts");
    C(ProxyRules.IsNear(0, 0) && ProxyRules.IsNear(1, -1) && ProxyRules.IsNear(-1, 0) && !ProxyRules.IsNear(2, 0) && !ProxyRules.IsNear(0, -2) && !ProxyRules.IsNear(3, 3),
      "a zone within one zone of the camera's (Chebyshev) is a near one");
    var near = ProxyKit.Fan(10);
    var far = ProxyKit.Triangle();
    var kind = ProxyKit.TwoLodKind("wall", near, far);
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null), (0, 9.0, 0.0, 5.0, 0.0, null)), [kind], 1);
    var nearBuilt = ProxyMerge.Merge(zone, true, BakedShadows.All);
    var farBuilt = ProxyMerge.Merge(zone, false, BakedShadows.All);
    C(nearBuilt.Vertices == 2 * near.Vertices.Length && nearBuilt.Triangles == 20, $"the near proxy is the first level of detail ({nearBuilt.Vertices} vertices, {nearBuilt.Triangles} triangles)");
    C(farBuilt.Vertices == 6 && farBuilt.Triangles == 2, $"the far proxy is the last ({farBuilt.Vertices} vertices, {farBuilt.Triangles} triangles)");
    // Near: the last level never casts, so there is no far proxy
    var nearOnly = ProxyMerge.Merge(zone, false, BakedShadows.Near);
    C(nearOnly.Chunks.Length == 0 && !nearOnly.KindCovered[0], "under Near the last level of detail casts nothing, so the far proxy holds nothing");
    C(ProxyMerge.Merge(zone, true, BakedShadows.Near).Triangles == 20, "...and the near proxy is as under All");
    C(ProxyMerge.Merge(zone, true, BakedShadows.Off).Chunks.Length == 0, "under Off nothing is merged");
    // a piece with one set of parts is the same in both
    var single = ClientKit.NoLods("post", 1f);
    ProxyKit.Take(single.Parts[0], near);
    var post = ClientKit.Kind("post", single);
    var postZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [post], 1);
    C(ProxyMerge.Merge(postZone, true, BakedShadows.All).Triangles == 10 && ProxyMerge.Merge(postZone, false, BakedShadows.All).Triangles == 10, "a piece with no level of detail to switch has its one set of parts in both");
    C(ProxyMerge.Merge(postZone, false, BakedShadows.Near).Triangles == 10, "...and under Near too, as the instancing casts it at LOD0");
    // a piece flagged LOD0Only never switches
    var lodOnly = ProxyKit.TwoLodKind("only", near, far, flags: PaletteFlags.LOD0Only);
    var onlyZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [lodOnly], 1);
    C(ProxyMerge.Merge(onlyZone, false, BakedShadows.All).Triangles == 10, "a piece flagged LOD0Only has its first level of detail in the far proxy");
    C(ProxyRules.IsSingle(lodOnly) && ProxyRules.IsSingle(post) && !ProxyRules.IsSingle(kind), "which kinds do not switch");

    // who casts: the palette flag, a renderer that casts none, a shadow-only renderer
    var noShadow = ProxyKit.TwoLodKind("flat", near, far, flags: PaletteFlags.NoShadows);
    var flatZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [noShadow], 1);
    C(ProxyMerge.Merge(flatZone, true, BakedShadows.All).Chunks.Length == 0, "a kind flagged NoShadows is not merged");
    var offPiece = ClientKit.NoLods("decal", 1f);
    offPiece.Parts[0].Shadows = ShadowCastingMode.Off;
    ProxyKit.Take(offPiece.Parts[0], near);
    var offZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [ClientKit.Kind("decal", offPiece)], 1);
    C(ProxyMerge.Merge(offZone, true, BakedShadows.All).Chunks.Length == 0, "a renderer that casts no shadow in the game is not merged");
    var shadowOnly = ClientKit.NoLods("ghost", 1f);
    shadowOnly.Parts[0].Shadows = ShadowCastingMode.ShadowsOnly;
    ProxyKit.Take(shadowOnly.Parts[0], near);
    var ghostZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [ClientKit.Kind("ghost", shadowOnly)], 1);
    C(ProxyMerge.Merge(ghostZone, true, BakedShadows.All).Triangles == 10, "a shadow-only renderer is merged");
  }

  // ---- what a proxy leaves to the instancing -----------------------------------------------------------------------------------------

  private static void ProxyExclusionTest()
  {
    Section("proxy: what stays instanced");
    ProxyRules.MaterialFacts Piece(int queue = 2000, string type = "Opaque", bool test = false, bool blend = false, bool premultiply = false, float? cutoff = 0.5f, string shader = "Custom/Piece", int mode = -1) =>
      new(shader, queue, type, test, blend, premultiply, cutoff.HasValue, cutoff ?? 0f, mode);
    byte Of(ProxyRules.MaterialFacts f) => ProxyRules.Classify(f, out _);
    C(Of(Piece()) == RenderPart.ProxyOk, "an opaque Custom/Piece material (every one has _Cutoff 0.5) is merged");
    C(Of(Piece(cutoff: 0f)) == RenderPart.ProxyOk && Of(Piece(cutoff: null)) == RenderPart.ProxyOk, "...also with _Cutoff 0 or none");
    C(Of(Piece(test: true)) == RenderPart.ProxyCutout, "the keyword _ALPHATEST_ON: a cutout");
    C(Of(Piece(blend: true)) == RenderPart.ProxyCutout && Of(Piece(premultiply: true)) == RenderPart.ProxyCutout, "_ALPHABLEND_ON and _ALPHAPREMULTIPLY_ON: left to the instancing");
    C(Of(Piece(queue: 2450)) == RenderPart.ProxyCutout && Of(Piece(queue: 3000)) == RenderPart.ProxyCutout && Of(Piece(queue: 2449)) == RenderPart.ProxyOk, "a render queue of 2450 or more: not merged");
    C(Of(Piece(type: "TransparentCutout")) == RenderPart.ProxyCutout && Of(Piece(type: "Transparent")) == RenderPart.ProxyCutout, "a RenderType that is not Opaque: not merged");
    C(Of(Piece(cutoff: 0.35f)) == RenderPart.ProxyCutout && Of(Piece(cutoff: 0.2f)) == RenderPart.ProxyCutout, "a _Cutoff set to something other than 0 or 0.5 was set up for a cutout");
    C(Of(Piece(shader: "Custom/Vegetation", cutoff: 0.5f)) == RenderPart.ProxyOther && Of(Piece(shader: "Custom/Grass")) == RenderPart.ProxyOther, "vegetation and grass shaders: not merged");
    C(Of(Piece(shader: "Valheim/Snow Mesh")) == RenderPart.ProxyOther && Of(Piece(shader: "SomeMod/Shader")) == RenderPart.ProxyOther, "a shader the proxy does not know: not merged");
    C(Of(Piece(shader: "Standard", cutoff: 0.5f, mode: 0)) == RenderPart.ProxyOk && Of(Piece(shader: "Standard", cutoff: 0.5f, mode: 1)) == RenderPart.ProxyCutout
      && Of(Piece(shader: "Standard", cutoff: 0.5f)) == RenderPart.ProxyOk, "Unity's Standard is merged in its opaque mode, not in the others");
    C(Of(Piece(shader: "Custom/StaticRock")) == RenderPart.ProxyOk && Of(Piece(shader: "Custom/Trilinearmap")) == RenderPart.ProxyOk, "the game's rock and trilinear shaders are merged");
    ProxyRules.Classify(Piece(test: true), out string why);
    C(why.Contains("_ALPHATEST_ON"), $"the reason is said ({why})");
    C(ProxyRules.Worse(RenderPart.ProxyOk, RenderPart.ProxyCutout) == RenderPart.ProxyCutout && ProxyRules.Worse(RenderPart.ProxyOther, RenderPart.ProxyOk) == RenderPart.ProxyOther, "a part with several materials is as good as its worst");

    // parts the proxy does not hold: unsettled, unreadable, cutout, no shape
    var near = ProxyKit.Fan(10);
    var piece = ClientKit.NoLods("house", 1f);
    piece.Parts = [ClientKit.Part(true, true), ClientKit.Part(true, true), ClientKit.Part(true, true), ClientKit.Part(true, true), ClientKit.Part(true, true)];
    piece.NearParts = piece.FarParts = [0, 1, 2, 3, 4];
    ProxyKit.Take(piece.Parts[0], near);                                      // taken
    piece.Parts[1].ProxyState = RenderPart.ProxyUnreadable;                    // its mesh could not be read
    piece.Parts[2].ProxyState = RenderPart.ProxyCutout;                        // a cutout
    piece.Parts[3].ProxyState = RenderPart.ProxyOk;                            // ok, but no shape (cannot happen; never merged)
    piece.Parts[4].ProxyState = RenderPart.ProxyUnknown;                       // not settled
    piece.Parts[4].Shape = near;
    var kind = ClientKit.Kind("house", piece);
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [kind], 1);
    var built = ProxyMerge.Merge(zone, true, BakedShadows.All);
    C(built.PartsMerged == 1 && built.Triangles == 10, $"only the settled part with a shape is merged ({built.PartsMerged} parts)");
    C(ProxyRules.MergedParts(kind, true, BakedShadows.All).Count == 1, "MergedParts says the same");
    // no part taken: the kind is not covered, and nothing is built
    var bare = ClientKit.NoLods("bare", 1f);
    bare.Parts[0].ProxyState = RenderPart.ProxyCutout;
    var bareKind = ClientKit.Kind("bare", bare);
    var bareBuilt = ProxyMerge.Merge(BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 0.0, 5.0, 0.0, null)), [bareKind], 1), true, BakedShadows.All);
    C(bareBuilt.Chunks.Length == 0 && !bareBuilt.KindCovered[0], "a kind whose parts are all left out is not covered");
  }

  // ---- numbers read back from a GPU ---------------------------------------------------------------------------------------------------

  private static void ProxyDecodeTest()
  {
    Section("proxy: shapes from numbers");
    C(ProxyDecode.Half(0x3C00) == 1f && ProxyDecode.Half(0xC000) == -2f && ProxyDecode.Half(0x3800) == 0.5f && ProxyDecode.Half(0) == 0f && ProxyDecode.Half(0x7BFF) == 65504f,
      "half precision: 1, -2, 0.5, 0 and the largest");
    C(Math.Abs(ProxyDecode.Half(0x0001) - 5.9604645e-8f) < 1e-12f && Math.Abs(ProxyDecode.Half(0x0400) - 6.1035156e-5f) < 1e-11f && float.IsPositiveInfinity(ProxyDecode.Half(0x7C00)) && float.IsNaN(ProxyDecode.Half(0x7E00)),
      "...the smallest subnormal, the smallest normal, infinity and not a number");
    C(Math.Abs(ProxyDecode.Half(0x3555) - 0.33325f) < 1e-4f, "...and a third");

    // an interleaved buffer of 3 vertices: stride 24, position (Float32 x3) at 0, normal (Float32 x3) at 12
    var bytes = new byte[72];
    float[] values = [0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 1];
    Buffer.BlockCopy(values, 0, bytes, 0, 72);
    var pos = ProxyDecode.ReadVectors(bytes, 3, 24, 0, VertexAttributeFormat.Float32, 3, false);
    var nrm = ProxyDecode.ReadVectors(bytes, 3, 24, 12, VertexAttributeFormat.Float32, 3, true);
    C(pos != null && ProxyNear(pos[1], new Vector3(1, 0, 0)) && ProxyNear(pos[2], new Vector3(0, 1, 0)) && nrm != null && nrm.All(n => ProxyNear(n, Vector3.forward)), "positions and normals are read from their offsets in a stride");
    C(ProxyDecode.ReadVectors(bytes, 3, 24, 0, VertexAttributeFormat.Float32, 3, false)!.Length == 3 && ProxyDecode.ReadVectors(bytes, 4, 24, 0, VertexAttributeFormat.Float32, 3, false) == null,
      "too few bytes for the vertices claimed: nothing");
    C(ProxyDecode.ReadVectors(bytes, 3, 24, 20, VertexAttributeFormat.Float32, 3, false) == null && ProxyDecode.ReadVectors(bytes, 3, 24, 0, VertexAttributeFormat.Float32, 2, false) == null, "an attribute that overruns its stride, or has fewer than three components: nothing");
    // half precision positions, four components, stride 16: position at 0, normal as SNorm8 x4 at 8
    var h = new byte[32];
    ushort[] half = [0x3C00, 0x4000, 0xC000, 0x3C00];       // 1, 2, -2, 1
    Buffer.BlockCopy(half, 0, h, 0, 8);
    h[8] = 0; h[9] = 127; h[10] = 0; h[11] = 0;               // (0, 1, 0)
    Buffer.BlockCopy(half, 0, h, 16, 8);
    h[24] = 127; h[25] = 0; h[26] = 0x81; h[27] = 0;          // (1, 0, -1)
    var hp = ProxyDecode.ReadVectors(h, 2, 16, 0, VertexAttributeFormat.Float16, 4, false);
    var hn = ProxyDecode.ReadVectors(h, 2, 16, 8, VertexAttributeFormat.SNorm8, 4, true);
    C(hp != null && ProxyNear(hp[0], new Vector3(1, 2, -2)) && hp[1] == hp[0] && hn != null && ProxyNear(hn[0], Vector3.up) && ProxyNear(hn[1], new Vector3(1, 0, -1)), "half precision positions and SNorm8 normals");
    C(ProxyDecode.ReadVectors(h, 2, 16, 8, VertexAttributeFormat.SNorm8, 4, false) == null && ProxyDecode.ReadVectors(h, 2, 16, 0, VertexAttributeFormat.UNorm8, 4, true) == null, "a normalised format is not a position; a format that is not read is not read");

    // indices
    var narrow = new byte[12];
    Buffer.BlockCopy(new ushort[] { 9, 9, 0, 1, 2, 9 }, 0, narrow, 0, 12);
    var wide = new byte[24];
    Buffer.BlockCopy(new int[] { 9, 9, 0, 1, 2, 9 }, 0, wide, 0, 24);
    C(ProxyDecode.ReadIndices(narrow, false, 2, 3, 0, 3)!.SequenceEqual([0, 1, 2]) && ProxyDecode.ReadIndices(wide, true, 2, 3, 0, 3)!.SequenceEqual([0, 1, 2]), "16 and 32 bit indices, from a start");
    C(ProxyDecode.ReadIndices(narrow, false, 2, 3, 10, 13)!.SequenceEqual([10, 11, 12]), "the submesh's base vertex is added");
    C(ProxyDecode.ReadIndices(narrow, false, 2, 3, 0, 2) == null && ProxyDecode.ReadIndices(narrow, false, 4, 3, 0, 3) == null, "an index outside the vertices, or bytes too few: nothing");

    // a shape from a mesh with two submeshes: only the vertices its triangles use
    Vector3[] vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(5, 5, 5), new(6, 5, 5), new(5, 6, 5)];
    var second = ProxyShape.Make(vertices, null, [3, 4, 5]);
    C(second != null && second.Vertices.Length == 3 && second.Vertices[0] == new Vector3(5, 5, 5) && second.Indices.SequenceEqual([0, 1, 2]), "only the vertices the submesh's triangles use are kept");
    C(second!.Normals.All(n => ProxyNear(n, Vector3.forward)), "missing normals are made up from the triangles");
    Vector3[] given = [Vector3.up, Vector3.up, Vector3.up, Vector3.right, Vector3.right, Vector3.zero];
    var kept = ProxyShape.Make(vertices, given, [3, 4, 5]);
    C(ProxyNear(kept!.Normals[0], Vector3.right) && ProxyNear(kept.Normals[2], Vector3.up), "given normals are kept, in the vertices' new places; one of no length becomes up");
    C(ProxyShape.Make(vertices, null, [0, 1, 9]) == null && ProxyShape.Make(vertices, null, []) == null && ProxyShape.Make([], null, [0, 1, 2]) == null, "an index outside the vertices, or no triangle: no shape");
    C(ProxyShape.Make(vertices, null, [0, 1, 2, 3])!.Triangles == 1, "a stray index after the last whole triangle is left out");
  }

  // ---- the drawing job ----------------------------------------------------------------------------------------------------------------

  private static void ProxyFillTest()
  {
    Section("proxy: the drawing job sends what a proxy covers to draws that cast nothing");
    var piece = new PieceKind { Name = "house", Label = "house", Radius = 2f, SingleLod = true, Parts = [ClientKit.Part(true, true), ClientKit.Part(true, true), ClientKit.Part(true, true)] };
    piece.Parts[2].Shadows = ShadowCastingMode.ShadowsOnly;
    piece.NearParts = piece.FarParts = [0, 1, 2];
    ProxyKit.Take(piece.Parts[0], ProxyKit.Triangle());           // the wall: taken by the proxy
    piece.Parts[1].ProxyState = RenderPart.ProxyCutout;           // the thatch: a cutout, stays instanced
    ProxyKit.Take(piece.Parts[2], ProxyKit.Triangle());           // a shadow-only part: taken
    var kind = ClientKit.Kind("house", piece);
    var kinds = new[] { kind };
    var wall = ClientKit.BatchOf(kind, 0, 0);
    var thatch = ClientKit.BatchOf(kind, 0, 1);
    var ghost = ClientKit.BatchOf(kind, 0, 2);
    var covered = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1.0, 0.0, 1.0, 0.0, null), (0, 3.0, 0.0, 1.0, 0.0, null)), kinds, 1);
    var bare = BakedZoneBuild.Build(ClientKit.Zone(1, 0, (0, 65.0, 0.0, 1.0, 0.0, null), (0, 67.0, 0.0, 1.0, 0.0, null), (0, 69.0, 0.0, 1.0, 0.0, null)), kinds, 1);
    covered.ProxyCover = [true];
    var job = ClientKit.Job([bare, covered], kinds, 0f, 0f, 0f, 2f, drawScale: 1f);
    BakedDraw.Fill(job);
    C(job.Done && job.Error == null, "the job ends without an error");
    var wallBuf = wall.Buf[1 - wall.Front];
    var thatchBuf = thatch.Buf[1 - thatch.Front];
    var ghostBuf = ghost.Buf[1 - ghost.Front];
    C(wallBuf.Count == 5 && wallBuf.Segs == 2, $"the wall's instances are all drawn, in two runs ({wallBuf.Count} in {wallBuf.Segs})");
    C(wallBuf.SegKey[0] == BatchBuf.NoCast && wallBuf.SegEnd(0) - wallBuf.SegStart[0] == 2 && wallBuf.SegKey[1] >= 0 && wallBuf.SegEnd(1) - wallBuf.SegStart[1] == 3,
      "...the covered zone's two first, in a run that casts nothing, then the bare zone's three in one that casts");
    C(thatchBuf.Count == 5 && thatchBuf.SegKey.Take(thatchBuf.Segs).All(k => k >= 0), "the cutout part casts by instancing in both zones");
    C(ghostBuf.Count == 3 && ghostBuf.Segs == 1 && ghostBuf.SegKey[0] >= 0, "a shadow-only part is drawn only where nothing covers it: the covered zone's are skipped");
    C(wallBuf.Calls(BakedDraw.MaxPerCall) == 2 && wallBuf.Calls(BakedDraw.MaxPerCall, castingOnly: true) == 1, "calls are counted for all runs and for the casting ones apart");
    // the cull and the shadows: what sits outside the view is drawn only for the shadow it casts
    var (fw, rt, up) = ClientKit.Basis(90, 0);
    var pos = new Vector3(-40, 0, 0);
    var edge = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0.0, 0.0, 0.0, 0.0, null), (0, 0.0, 0.0, -14.0, 0.0, null)), kinds, 1);
    edge.ProxyCover = [true];
    var seen = ClientKit.Job([edge], kinds, pos.x, pos.y, pos.z, 2f, drawScale: 4f, detailScale: 10f, shadows: BakedShadows.All);
    BakedFrustum.Make(seen.Planes, pos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(seen);
    C(ClientKit.Filled(wall, false).Count == 1 && ClientKit.Filled(thatch, false).Count == 2,
      "a piece beside the view is drawn for its shadow only when the proxy does not cast it: the wall (covered) is not, the thatch (a cutout) is");
    edge.ProxyCover = null;
    var uncovered = ClientKit.Job([edge], kinds, pos.x, pos.y, pos.z, 2f, drawScale: 4f, detailScale: 10f, shadows: BakedShadows.All);
    BakedFrustum.Make(uncovered.Planes, pos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(uncovered);
    C(ClientKit.Filled(wall, false).Count == 2, "...and with no proxy the wall beside the view is drawn too");

    // Shadows Off: nothing casts, the coverage does not matter, one run
    covered.ProxyCover = [true];
    var off = ClientKit.Job([bare, covered], kinds, 0f, 0f, 0f, 2f, drawScale: 1f, shadows: BakedShadows.Off);
    BakedDraw.Fill(off);
    var offBuf = wall.Buf[1 - wall.Front];
    C(offBuf.Count == 5 && offBuf.Segs == 1 && offBuf.SegKey[0] == BatchBuf.NoCast, "with Shadows Off a batch is one run that casts nothing");
    // a kind the zone's coverage does not name casts as before
    covered.ProxyCover = [false];
    var notCovered = ClientKit.Job([bare, covered], kinds, 0f, 0f, 0f, 2f, drawScale: 1f);
    BakedDraw.Fill(notCovered);
    var nb = wall.Buf[1 - wall.Front];
    C(nb.SegKey.Take(nb.Segs).All(k => k >= 0), "a kind the proxy does not cover keeps casting by instancing");
    covered.ProxyCover = null;
    // the covered zones come first and are one run, however the ring is ordered
    var many = new[] { 5, 3, 4, 2 }.Select(z => BakedZoneBuild.Build(ClientKit.Zone(z, 0, (0, z * 64.0 + 1, 0.0, 1.0, 0.0, null)), kinds, 1)).ToArray();
    many[1].ProxyCover = [true];
    many[3].ProxyCover = [true];
    var mixed = ClientKit.Job([many[0], many[1], many[2], many[3]], kinds, 0f, 0f, 0f, 2f, drawScale: 4f);
    BakedDraw.Fill(mixed);
    var mb = wall.Buf[1 - wall.Front];
    C(mb.Count == 4 && mb.Segs == 2 && mb.SegKey[0] == BatchBuf.NoCast && mb.SegEnd(0) == 2 && mb.SegKey[1] >= 0, $"the covered zones' instances are one run, however the zones come ({mb.Segs} runs)");
    // the job records the version it was made at, and adopting it moves ProxyAdopted
    mixed.ProxyVersion = BakedDraw.ProxyVersion + 7;
    BakedDraw.Adopt(mixed);
    C(BakedDraw.ProxyAdopted >= mixed.ProxyVersion, "adopting a job moves ProxyAdopted up to the version it was made at");
  }

  // ---- the book --------------------------------------------------------------------------------------------------------------------------

  private static void ProxyBookTest()
  {
    Section("proxy: the life of a zone's proxies");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    var kind = ProxyKit.TwoLodKind("wall", ProxyKit.Fan(10), ProxyKit.Triangle());
    var kinds = new[] { kind };
    BuiltZone Make(int zx, int zz, uint rev = 1) => BakedZoneBuild.Build(ClientKit.Zone(zx, zz, (0, zx * 64.0 + 5, 0.0, zz * 64.0 + 5, 0.0, null), (0, zx * 64.0 + 9, 0.0, zz * 64.0 + 5, 0.0, null)), kinds, rev);
    var home = Make(0, 0);
    var distant = Make(5, 0);
    var fake = new ProxyKit.Fake();
    var book = fake.Book;
    var ring = new[] { home, distant };
    int v0 = BakedDraw.ProxyVersion;
    // two merges at a time (ProxyBook.MaxBuilds): the third starts on the next tick
    book.Tick(ProxyKit.Frame(ring, 1, Vector3.zero, now: 0f));
    book.Tick(ProxyKit.Frame(ring, 1, Vector3.zero, now: 0f));
    C(home.Proxy != null && distant.Proxy != null && home.Proxy.Near.IsReady && home.Proxy.Far.IsReady && !distant.Proxy.Near.IsReady && distant.Proxy.Far.IsReady,
      "the camera's zone has both proxies, a far one only the far");
    C(fake.Uploaded.Count == 3, $"three meshes were made ({fake.Uploaded.Count})");
    C(home.ProxyCover != null && home.ProxyCover.SequenceEqual([true]) && distant.ProxyCover != null, "each zone's coverage is published: its one kind");
    C(BakedDraw.ProxyVersion >= v0 + 3, "...and every publication counts, so that a drawing job is started for it");
    C(ReferenceEquals(book.Drawn(home.Proxy!), home.Proxy.Near) && ReferenceEquals(book.Drawn(distant.Proxy!), distant.Proxy.Far), "the camera's zone draws its near proxy, the distant one its far");
    book.Submit(ProxyKit.Frame(ring, 1, Vector3.zero));
    C(fake.Drawn.Count == 2 && fake.Drawn.Contains(home.Proxy.Near.Chunks[0]) && fake.Drawn.Contains(distant.Proxy.Far.Chunks[0]), $"one mesh a zone is drawn ({fake.Drawn.Count})");
    var numbers = book.Numbers(2);
    C(numbers.ZonesWithProxy == 2 && numbers.Near == 1 && numbers.Far == 2 && numbers.Triangles == 24 && numbers.Vertices == 36 && numbers.Bytes == 36 * 12L + 72 * 2L,
      $"the numbers: {numbers.Line()}");

    // the second proxy of a zone waits to be drawn until a job that knows the narrower coverage has been adopted
    C(home.Proxy.Far.ActiveAt > BakedDraw.ProxyAdopted && home.Proxy.Near.ActiveAt == 0, "the far proxy of the camera's zone is not drawn until a job knows of it");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    C(ReferenceEquals(book.Drawn(home.Proxy), home.Proxy.Near), "...and the near one is still the one drawn while the zone is near");

    // the camera moves away: the near proxy stays Linger seconds, then goes, at once, because the far one stands
    var away = new Vector3(3 * 64f, 0f, 0f);
    book.Tick(ProxyKit.Frame(ring, 1, away, now: 1f));
    C(home.Proxy.Near.IsReady && ReferenceEquals(book.Drawn(home.Proxy), home.Proxy.Far), "three zones away the near proxy is still held for a while, the far one is drawn");
    int freedBefore = fake.Freed.Count;
    int versionBefore = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame(ring, 1, away, now: 1f + ProxyBook.Linger + 0.5f));
    C(!home.Proxy.Near.IsReady && fake.Freed.Count == freedBefore + 1 && home.Proxy.Far.IsReady, "after Linger the near proxy is freed");
    C(home.ProxyCover != null, "the zone is covered still, by the far proxy");
    C(BakedDraw.ProxyVersion > versionBefore, "...which is published, as the coverage may have grown");

    // coming back makes the near proxy again
    book.Tick(ProxyKit.Frame(ring, 1, Vector3.zero, now: 10f));
    C(home.Proxy.Near.IsReady, "back at the camera's zone, the near proxy is made again");

    // a rebuilt zone (a new BuiltZone for the same place): the old proxies are taken back only after a job that knows
    var rebuilt = Make(0, 0, rev: 2);
    var oldChunks = home.Proxy.Near.Chunks.Concat(home.Proxy.Far.Chunks).ToArray();
    var oldProxy = home.Proxy;
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    fake.Freed.Clear();
    var ring2 = new[] { rebuilt, distant };
    book.Tick(ProxyKit.Frame(ring2, 2, Vector3.zero, now: 11f));
    C(oldProxy.Dead && home.Proxy == null && home.ProxyCover == null, "the old zone's coverage is withdrawn at once");
    C(rebuilt.Proxy != null && rebuilt.Proxy.Near.IsReady, "the new zone has proxies of its own");
    C(fake.Freed.Count == 0 && book.Retiring == 1, "the old meshes are kept: the frame in front may still have its instances not casting");
    fake.Drawn.Clear();
    book.Submit(ProxyKit.Frame(ring2, 2, Vector3.zero));
    C(oldChunks.Length == 2 && oldChunks.All(c => fake.Drawn.Contains(c)), "...and drawn meanwhile, so that those instances have a shadow");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame(ring2, 2, Vector3.zero, now: 12f));
    C(oldChunks.All(c => fake.Freed.Contains(c)) && book.Retiring == 0, "once a job made after has been adopted, they are freed");
    fake.Drawn.Clear();
    book.Submit(ProxyKit.Frame(ring2, 2, Vector3.zero));
    C(oldChunks.All(c => !fake.Drawn.Contains(c)), "...and no longer drawn");

    // a zone that leaves the ring
    fake.Freed.Clear();
    var gone = distant.Proxy!;
    var goneChunks = gone.Far.Chunks.ToArray();
    book.Tick(ProxyKit.Frame([rebuilt], 3, Vector3.zero, now: 13f));
    C(gone.Dead && distant.ProxyCover == null && distant.Proxy == null && fake.Freed.Count == 0, "a zone out of the ring loses its coverage; its mesh is kept until a job knows");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame([rebuilt], 3, Vector3.zero, now: 14f));
    C(goneChunks.All(c => fake.Freed.Contains(c)), "then freed");

    // the Shadows setting changes: proxies are made again for it (under Near there is no far proxy)
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, now: 15f));
    C(rebuilt.Proxy!.Near.IsReady && rebuilt.Proxy.Far.IsReady && distant.Proxy!.Far.IsReady, "back in the ring: proxies again");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, BakedShadows.Near, now: 16f));
    C(rebuilt.Proxy!.Policy == BakedShadows.Near && rebuilt.Proxy.Near.IsReady && rebuilt.Proxy.Far.State == ProxyVariant.None, "under Near the camera's zone has a near proxy and no far one");
    C(distant.Proxy!.Policy == BakedShadows.Near && !distant.Proxy.AnyReady && distant.ProxyCover == null, "...and a zone farther off has none: it casts by instancing, LOD0 only");

    // off
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, enabled: false, now: 17f));
    C(rebuilt.Proxy == null && rebuilt.ProxyCover == null && book.Zones.Count == 0, "switched off: every proxy is withdrawn");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, enabled: false, now: 18f));
    C(book.Retiring == 0, "...and freed");
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, BakedShadows.Off, now: 19f));
    C(book.Zones.Count == 0, "Shadows Off: none");
    book.Tick(ProxyKit.Frame([rebuilt, distant], 4, Vector3.zero, reach: 0f, now: 19f));
    C(book.Zones.Count == 0, "a game with shadows off (no reach): none");
  }

  private static void ProxyBookStepsTest()
  {
    Section("proxy: made in steps");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    var kind = ProxyKit.TwoLodKind("wall", ProxyKit.Fan(10), ProxyKit.Triangle());
    BuiltZone Make(int zx, int zz) => BakedZoneBuild.Build(ClientKit.Zone(zx, zz, (0, zx * 64.0 + 5, 0.0, zz * 64.0 + 5, 0.0, null)), [kind], 1);

    // a part that takes frames to settle (a read back from the GPU) holds the proxy in the first step
    var zone = Make(0, 0);
    var fake = new ProxyKit.Fake();
    int asked = 0;
    bool ready = false;
    fake.Book.Settle = _ => ++asked > 0 && ready;
    fake.Book.Tick(ProxyKit.Frame([zone], 1, Vector3.zero));
    C(zone.Proxy!.Near.State == ProxyVariant.Shapes && zone.ProxyCover == null && fake.Uploaded.Count == 0, "a part not settled yet: the proxy waits, the zone casts by instancing");
    ready = true;
    fake.Book.Tick(ProxyKit.Frame([zone], 1, Vector3.zero));
    C(zone.Proxy.Near.IsReady && zone.ProxyCover != null, "settled: the proxy is made");

    // workers: one build at a time up to MaxBuilds; a build that finishes after its zone left is dropped
    var queue = new List<Action>();
    var held = Make(0, 0);
    var other = Make(1, 0);
    var third = Make(2, 0);
    var slow = new ProxyKit.Fake();
    slow.Book.Work = queue.Add;
    var slowRing = new[] { held, other, third };
    slow.Book.Tick(ProxyKit.Frame(slowRing, 1, Vector3.zero));
    C(queue.Count == ProxyBook.MaxBuilds && slow.Book.Building == ProxyBook.MaxBuilds, $"at most {ProxyBook.MaxBuilds} merges run at once ({queue.Count})");
    C(held.Proxy!.Near.State == ProxyVariant.Building, "the nearest zone's near proxy is first");
    slow.Book.Tick(ProxyKit.Frame([other, third], 2, Vector3.zero));
    C(held.Proxy == null && held.ProxyCover == null, "a zone that leaves while its merge is running loses its proxy");
    foreach (var job in queue.ToArray())
      job();
    queue.Clear();
    slow.Book.Tick(ProxyKit.Frame([other, third], 2, Vector3.zero));
    C(slow.Book.Building <= ProxyBook.MaxBuilds, "the merge that finished late is dropped, the others go on");
    int guard = 0;
    while (queue.Count > 0 || slow.Book.Building > 0 || slow.Book.Zones.Any(z => z.Both.Any(v => v.State != ProxyVariant.None && v.State != ProxyVariant.Ready)))
    {
      foreach (var job in queue.ToArray())
        job();
      queue.Clear();
      slow.Book.Tick(ProxyKit.Frame([other, third], 2, Vector3.zero));
      if (++guard > 20)
        break;
    }
    C(guard <= 20 && other.ProxyCover != null && third.ProxyCover != null, $"every merge in the end becomes a proxy ({guard} ticks)");
    C(slow.Uploaded.Count == slow.Book.Numbers(2).Near + slow.Book.Numbers(2).Far, "...and no mesh was made for the zone that left");

    // a mesh that cannot be made: the proxy fails, the meshes it already has go, the zone is not covered
    var wide = ProxyKit.Fan(98);
    var big = ClientKit.NoLods("big", 1f);
    ProxyKit.Take(big.Parts[0], wide);
    var bigKind = ClientKit.Kind("big", big);
    var bigZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, Enumerable.Range(0, 1300).Select(i => (0, -30.0 + i % 60, 0.0, -30.0 + i / 60 % 60, 0.0, (Vector3?)null)).ToArray()), [bigKind], 1);
    var chunks = ProxyMerge.Merge(bigZone, true, BakedShadows.All);
    C(chunks.Chunks.Length == 2 && chunks.Chunks[0].VertexCount == 65500 && chunks.Chunks[1].VertexCount == 64500, $"1,300 pieces of 100 vertices are two chunks at the default cap of 65,535 ({chunks.Chunks.Length})");
    int made = 0;
    var failing = new ProxyKit.Fake();
    failing.Book.Upload = (c, _) =>
    {
      if (++made >= 2)
        return false;
      ProxyKit.Fake.Consume(c);
      return true;
    };
    failing.Book.Tick(ProxyKit.Frame([bigZone], 1, Vector3.zero));
    C(bigZone.Proxy!.Near.State == ProxyVariant.Failed && bigZone.ProxyCover == null, "the second mesh cannot be made: the proxy fails and the zone is not covered");
    C(failing.Freed.Count == 1 && bigZone.Proxy.Near.Chunks.Length == 0, "...the first mesh, which was made, is freed");
    C(failing.Book.Numbers(1).Failures >= 1, "...and the numbers count the failure");

    // the near proxies' memory budget
    var budget = new ProxyKit.Fake();
    budget.Book.NearBudget = 100;
    var tiny = Make(0, 0);
    budget.Book.Tick(ProxyKit.Frame([tiny], 1, Vector3.zero));
    C(tiny.Proxy!.Near.State == ProxyVariant.Failed && tiny.Proxy.Far.IsReady && tiny.ProxyCover != null, "over the near proxies' budget: no near proxy, the far one stands");
    C(budget.Book.Numbers(1).BudgetSkips == 1 && budget.Book.Numbers(1).FailedNow == 1, "...and the numbers say so");
    // hidden: what was taken back is freed at once
    var hide = new ProxyKit.Fake();
    var hideZone = Make(0, 0);
    hide.Book.Tick(ProxyKit.Frame([hideZone], 1, Vector3.zero));
    hide.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(hide.Book.Retiring > 0, "a zone that left waits for a job");
    hide.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero, hidden: true));
    C(hide.Book.Retiring == 0, "with drawing hidden nothing waits");
    hide.Book.Submit(ProxyKit.Frame([], 2, Vector3.zero, hidden: true));
    C(hide.Drawn.Count == 0, "...and nothing is drawn");

    // Reset frees everything
    var reset = new ProxyKit.Fake();
    var resetZone = Make(0, 0);
    reset.Book.Tick(ProxyKit.Frame([resetZone], 1, Vector3.zero));
    int meshes = reset.Uploaded.Count;
    reset.Book.Reset();
    C(meshes == 2 && reset.Freed.Count == 2 && resetZone.ProxyCover == null && resetZone.Proxy == null && reset.Book.Zones.Count == 0, "Reset frees every mesh and withdraws every coverage");
  }

  // ---- the two steps of a merge ---------------------------------------------------------------------------------------------------------

  private static BuiltZone ProxyPlanZone(out BakedKind wall, out BakedKind beam, int walls = 40, int beams = 10, int fan = 98)
  {
    var shape = ProxyKit.Fan(fan);   // fan + 2 vertices
    var piece = ClientKit.NoLods("wall", 1f);
    ProxyKit.Take(piece.Parts[0], shape);
    wall = ClientKit.Kind("wall", piece, layer: 10);
    var piece2 = ClientKit.NoLods("beam", 1f);
    ProxyKit.Take(piece2.Parts[0], ProxyKit.Triangle());
    beam = ClientKit.Kind("beam", piece2, layer: 10);
    var records = Enumerable.Range(0, walls + beams).Select(i => (i < walls ? 0 : 1, -30.0 + i % 60, 0.0, -30.0 + i * 7 % 60, 0.0, (Vector3?)null)).ToArray();
    return BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), [wall, beam], 1);
  }

  private static void ProxyPlanWriteTest()
  {
    Section("proxy: a merge in two steps");
    var zone = ProxyPlanZone(out _, out _);
    var plan = ProxyMerge.Plan(zone, true, BakedShadows.All, VertexLayout.Plain, maxVertices: 1000);
    C(plan.Chunks.Length > 1 && plan.Chunks.All(c => c.Store == null) && plan.Chunks.All(c => c.VertexCount > 0 && c.VertexCount <= 1000), "the plan names the chunks and makes no store");
    C(plan.Vertices == 40 * 100 + 10 * 3 && plan.Triangles == 40 * 98 + 10, $"...and counts what goes in them ({plan.Vertices} vertices, {plan.Triangles} triangles)");
    C(plan.KindCovered.SequenceEqual([true, true]) && plan.PartsMerged == 50, "...and which kinds are covered");
    C(plan.Layout == VertexLayout.Plain && plan.MaxVertices == 1000 && plan.Chunks.All(c => c.Stride == 24 && !c.Wide), "...with the layout and the cap it was made for");
    // the write fills exactly what the plan counted
    foreach (var c in plan.Chunks)
      c.Store = new ByteStore();
    bool done = ProxyMerge.Write(zone, true, BakedShadows.All, plan);
    var stores = plan.Chunks.Select(c => (ByteStore)c.Store!).ToArray();
    C(done && stores.All(s => s.Closed), "the write returns true and closes every chunk");
    C(plan.Chunks.Zip(stores, (c, s) => c.VertexCount == s.VertexCount && c.IndexCount == s.IndexCount && s.Vertices.Length == c.VertexCount * 24 && s.Indices.Length == c.IndexCount * 2).All(b => b),
      "each store holds the vertices and 16 bit indices the plan counted");
    C(plan.WriteMs >= 0 && plan.PlanMs >= 0 && plan.Ms == plan.PlanMs + plan.WriteMs, "the worker time of both steps is counted");
    // the same through Merge
    var merged = ProxyMerge.Merge(zone, true, BakedShadows.All, 1000, VertexLayout.Plain);
    C(merged.Chunks.Length == plan.Chunks.Length && merged.Bytes == plan.Bytes, "Merge does the two steps into byte arrays");

    // what each kind costs: the sum is the whole
    C(plan.KindVertices.Sum() == plan.Vertices && plan.KindBytes.Sum() == plan.Bytes && plan.KindVertices[0] == 4000 && plan.KindVertices[1] == 30,
      $"the kinds' vertices and bytes add up to the proxy's ({plan.KindBytes[0]} and {plan.KindBytes[1]} bytes)");

    // a matrix with no inverse: neither step takes it, and they still agree
    var singular = ProxyPlanZone(out _, out _, walls: 3, beams: 0);
    singular.Kinds[0].Root[1] = new Matrix4x4();
    var sp = ProxyMerge.Merge(singular, true, BakedShadows.All);
    C(sp.Vertices == 200 && sp.KindCovered[0] && sp.Chunks.Single().VertexCount == 200, "an instance with a matrix that has no inverse adds nothing, in the plan as in the write");

    // an instance bigger than the 16 bit index limit is a chunk of its own with 32 bit indices
    var big = ProxyPlanZone(out _, out _, walls: 1, beams: 3, fan: 70_000);
    var bp = ProxyMerge.Merge(big, true, BakedShadows.All, 65_535, VertexLayout.Plain);
    var wideChunk = bp.Chunks.Single(c => c.Wide);
    var (_, _, wideIdx) = ProxyKit.Read(wideChunk);
    C(wideChunk.VertexCount == 70_002 && wideIdx.Length == 70_000 * 3 && wideIdx.All(i => i >= 0 && i < 70_002) && wideChunk.Bytes == 70_002L * 24 + 70_000L * 3 * 4,
      "a chunk of more than 65,535 vertices has 32 bit indices, and says its bytes so");
    C(bp.Chunks.Where(c => !c.Wide).Sum(c => c.VertexCount) == 9 && bp.Chunks.Where(c => !c.Wide).All(c => c.Bytes == c.VertexCount * 24L + c.IndexCount * 2L), "...and the smaller pieces are in narrow ones");

    // cancelling: stops at a chunk boundary, the rest untouched
    var cancel = ProxyMerge.Plan(zone, true, BakedShadows.All, VertexLayout.Plain, 1000);
    foreach (var c in cancel.Chunks)
      c.Store = new ByteStore();
    int asked = 0;
    bool finished = ProxyMerge.Write(zone, true, BakedShadows.All, cancel, () => ++asked > 1);
    var cs = cancel.Chunks.Select(c => (ByteStore)c.Store!).ToArray();
    C(!finished && cs[0].Closed && cs.Skip(1).All(s => !s.Closed), "a write that is cancelled after the first chunk leaves the others unwritten");
    // a write against a plan that does not fit the zone is refused, not guessed at
    var other = ProxyPlanZone(out _, out _, walls: 5, beams: 0);
    var wrong = ProxyMerge.Plan(zone, true, BakedShadows.All, VertexLayout.Plain, 1000);
    foreach (var c in wrong.Chunks)
      c.Store = new ByteStore();
    bool refused = false;
    try
    {
      ProxyMerge.Write(other, true, BakedShadows.All, wrong);
    }
    catch (InvalidOperationException)
    {
      refused = true;
    }
    C(refused, "a plan made for another zone is refused");
  }

  private static void ProxyPackTest()
  {
    Section("proxy: packing vertices and indices");
    var plain = VertexLayout.Plain;
    C(plain.Stride == 24 && plain.Fields[0].Offset == 0 && plain.Fields[1].Offset == 12 && plain.Find(UnityEngine.Rendering.VertexAttribute.Normal)!.Value.Offset == 12 && plain.Find(UnityEngine.Rendering.VertexAttribute.Tangent) == null,
      "the plain layout: position at 0, normal at 12, 24 bytes");
    var rich = new VertexLayout(
      new VertexField(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
      new VertexField(UnityEngine.Rendering.VertexAttribute.Normal, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3),
      new VertexField(UnityEngine.Rendering.VertexAttribute.Tangent, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4),
      new VertexField(UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2));
    C(rich.Stride == 48 && rich.Fields.Select(f => f.Offset).SequenceEqual([0, 12, 24, 40]), "a layout with tangents and uv0 follows on: offsets 0, 12, 24, 40 in 48 bytes");
    bool refused = false;
    try
    {
      _ = new VertexLayout(new VertexField(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 3));
    }
    catch (ArgumentException)
    {
      refused = true;
    }
    C(refused, "an attribute that does not fill whole 4-byte words is refused");

    Vector3[] p = [new(1, 2, 3), new(-4, 5.5f, 6), new(7, 8, -9)];
    Vector3[] n = [Vector3.up, Vector3.right, Vector3.forward];
    Vector2[] uv = [new(0, 1), new(0.5f, 0.25f), new(1, 0)];
    Vector4[] t = [new(1, 0, 0, 1), new(0, 1, 0, -1), new(0, 0, 1, 1)];
    var bytes = new byte[3 * rich.Stride];
    VertexPacker.Pack(rich, new VertexSource(p, n, uv, t), 3, bytes);
    float F(int vertex, int offset, int k) => BitConverter.ToSingle(bytes, vertex * rich.Stride + offset + k * 4);
    C(F(1, 0, 0) == -4f && F(1, 0, 1) == 5.5f && F(2, 0, 2) == -9f && F(0, 12, 1) == 1f && F(1, 12, 0) == 1f && F(2, 12, 2) == 1f, "positions and normals are written at their offsets");
    C(F(1, 24, 1) == 1f && F(1, 24, 3) == -1f && F(1, 40, 0) == 0.5f && F(1, 40, 1) == 0.25f && F(2, 40, 0) == 1f, "...tangents and uv0 too");
    var posOnly = new VertexLayout(new VertexField(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 4));
    var pb = new byte[3 * 16];
    VertexPacker.Pack(posOnly, new VertexSource(p), 3, pb);
    C(BitConverter.ToSingle(pb, 16 + 12) == 1f && BitConverter.ToSingle(pb, 16) == -4f, "a four component position has w = 1");
    bool missing = false, unsupported = false, tooSmall = false;
    try { VertexPacker.Pack(plain, new VertexSource(p), 3, new byte[72]); } catch (ArgumentException) { missing = true; }
    try
    {
      var half = new VertexLayout(new VertexField(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float16, 4));
      VertexPacker.Pack(half, new VertexSource(p), 3, new byte[24]);
    }
    catch (NotSupportedException) { unsupported = true; }
    try { VertexPacker.Pack(plain, new VertexSource(p, n), 3, new byte[71]); } catch (ArgumentException) { tooSmall = true; }
    C(missing && unsupported && tooSmall, "an attribute the source does not carry, a format not packed, or too few bytes: an error, never a guess");

    int[] ix = [0, 1, 2, 65535, 3, 4];
    var narrow = new byte[12];
    var wide = new byte[24];
    VertexPacker.PackIndices(ix, 6, false, narrow);
    VertexPacker.PackIndices(ix, 6, true, wide);
    C(BitConverter.ToUInt16(narrow, 6) == 65535 && BitConverter.ToUInt16(narrow, 4) == 2 && BitConverter.ToInt32(wide, 12) == 65535, "indices as 16 or 32 bit numbers");
    bool overflow = false;
    try { VertexPacker.PackIndices([65536], 1, false, new byte[2]); } catch (ArgumentException) { overflow = true; }
    C(overflow, "a 16 bit chunk refuses an index past 65,535");
  }

  // ---- the stores of a merge, on every path -------------------------------------------------------------------------------------------------

  private static void ProxyStoreLifeTest()
  {
    Section("proxy: no store is leaked or released under a worker");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    var kind = ProxyKit.TwoLodKind("wall", ProxyKit.Fan(10), ProxyKit.Triangle());
    BuiltZone Make(int zx, int zz) => BakedZoneBuild.Build(ClientKit.Zone(zx, zz, (0, zx * 64.0 + 5, 0.0, zz * 64.0 + 5, 0.0, null)), [kind], 1);
    string why;

    // a merge that goes through: every store ends in a mesh
    var fine = new ProxyKit.Fake();
    var a = Make(0, 0);
    fine.Book.Tick(ProxyKit.Frame([a], 1, Vector3.zero));
    fine.Book.Reset();
    C(fine.Stores.Count == 2 && fine.Stores.All(s => s.Consumed && s.Releases == 0) && fine.NoLeaks(out why), "a merge that is made: its stores became meshes");

    // workers that are slow: the plan runs, the main thread gives the stores, the write waits in the queue
    var queue = new List<Action>();
    var slow = new ProxyKit.Fake();
    slow.Book.Work = queue.Add;
    var zone = Make(0, 0);
    slow.Book.Tick(ProxyKit.Frame([zone], 1, Vector3.zero));
    C(queue.Count == 2 && slow.Stores.Count == 0 && slow.Book.Building == 2, "the plan is queued; no store exists before it has run");
    var plans = queue.ToArray();
    queue.Clear();
    foreach (var job in plans)
      job();
    slow.Book.Tick(ProxyKit.Frame([zone], 1, Vector3.zero));
    C(slow.Stores.Count == 2 && queue.Count == 2 && slow.Book.Building == 2, "the plan is in: the main thread made the stores and queued the writes");

    // the zone leaves the ring while the writes are queued: the stores stay (a worker may be in them) until the workers have stopped
    slow.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(slow.Stores.All(s => s.Releases == 0) && slow.Book.Building == 2, "a zone that leaves while its write is queued: nothing is released under the worker");
    foreach (var job in queue.ToArray())
      job();
    queue.Clear();
    C(slow.Book.Building == 0 && slow.Stores.All(s => s.Releases == 0), "the writes stop at once (they were told), and give their slots back");
    slow.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(slow.Stores.All(s => s.Releases == 1 && !s.Consumed) && slow.NoLeaks(out why), $"the next tick releases the stores ({(slow.NoLeaks(out why) ? "no leak" : why)})");

    // Reset while a write is queued: the stores wait for the worker, then go
    var queued = new List<Action>();
    var held = new ProxyKit.Fake();
    held.Book.Work = queued.Add;
    held.Book.OrphanWaitMs = 20;
    var z2 = Make(0, 0);
    held.Book.Tick(ProxyKit.Frame([z2], 1, Vector3.zero));
    foreach (var job in queued.ToArray())
      job();
    queued.Clear();
    held.Book.Tick(ProxyKit.Frame([z2], 1, Vector3.zero));
    C(held.Stores.Count == 2 && queued.Count == 2, "(again) two writes are queued");
    held.Book.Reset();
    C(held.Stores.All(s => s.Releases == 0) && held.Book.Building == 2, "Reset with a write still queued waits, then leaves the stores to the next tick");
    foreach (var job in queued.ToArray())
      job();
    queued.Clear();
    held.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(held.Stores.All(s => s.Releases == 1) && held.Book.Building == 0 && held.NoLeaks(out why), $"...which releases them once the workers are done ({(held.NoLeaks(out why) ? "no leak" : why)})");

    // the zone leaves while only the plan was made (before the stores): nothing to release, the slot is given back
    var early = new List<Action>();
    var e = new ProxyKit.Fake();
    e.Book.Work = early.Add;
    var z3 = Make(0, 0);
    e.Book.Tick(ProxyKit.Frame([z3], 1, Vector3.zero));
    foreach (var job in early.ToArray())
      job();
    early.Clear();
    e.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(e.Stores.Count == 0 && e.Book.Building == 0 && early.Count == 0, "a zone that leaves with its plan made: no store is ever made, the slot is back");

    // a plan that is still being made when the zone leaves: its slot comes back when it ends
    var running = new List<Action>();
    var r = new ProxyKit.Fake();
    r.Book.Work = running.Add;
    var z4 = Make(0, 0);
    r.Book.Tick(ProxyKit.Frame([z4], 1, Vector3.zero));
    r.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    foreach (var job in running.ToArray())
      job();
    running.Clear();
    r.Book.Tick(ProxyKit.Frame([], 2, Vector3.zero));
    C(r.Stores.Count == 0 && r.Book.Building == 0, "a plan that was still queued when the zone left does not run, and gives its slot back");

    // the near memory budget: refused on the plan, so no store is made for the near proxy
    var budget = new ProxyKit.Fake();
    budget.Book.NearBudget = 100;
    budget.Book.Tick(ProxyKit.Frame([Make(0, 0)], 1, Vector3.zero));
    C(budget.Stores.Count == 1 && budget.Stores[0].Consumed && budget.NoLeaks(out why), "over the near budget the near proxy gets no store; the far one's became a mesh");

    // a mesh that cannot be made: the stores of the chunks not yet made go
    var failing = new ProxyKit.Fake();
    var bigPiece = ClientKit.NoLods("big", 1f);
    ProxyKit.Take(bigPiece.Parts[0], ProxyKit.Fan(98));
    var bigKind = ClientKit.Kind("big", bigPiece);
    var bigZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, Enumerable.Range(0, 1500).Select(i => (0, -30.0 + i % 60, 0.0, -30.0 + i / 60 % 60, 0.0, (Vector3?)null)).ToArray()), [bigKind], 1);
    int made = 0;
    failing.Book.Upload = (c, _) =>
    {
      if (++made >= 2)
        return false;
      ProxyKit.Fake.Consume(c);
      return true;
    };
    failing.Book.Tick(ProxyKit.Frame([bigZone], 1, Vector3.zero));
    failing.Book.Tick(ProxyKit.Frame([bigZone], 1, Vector3.zero));
    var consumed = failing.Stores.Count(s => s.Consumed);
    C(bigZone.Proxy!.Near.State == ProxyVariant.Failed && failing.Stores.Count >= 3 && consumed == 1 && failing.Stores.Where(s => !s.Consumed).All(s => s.Releases == 1), "a mesh that cannot be made: the stores of all the chunks after it are released");

    // memory for a store that cannot be made: the stores already made go, the proxy fails
    var noMemory = new ProxyKit.Fake();
    int stores = 0;
    var inner = noMemory.Book.NewStore;
    noMemory.Book.NewStore = ch =>
    {
      if (++stores == 2)
        throw new InvalidOperationException("no memory");
      return inner(ch);
    };
    var z5 = BakedZoneBuild.Build(ClientKit.Zone(0, 0, Enumerable.Range(0, 1500).Select(i => (0, -30.0 + i % 60, 0.0, -30.0 + i / 60 % 60, 0.0, (Vector3?)null)).ToArray()), [bigKind], 1);
    noMemory.Book.Tick(ProxyKit.Frame([z5], 1, Vector3.zero));
    C(z5.Proxy!.Near.State == ProxyVariant.Failed && noMemory.Stores.Where(s => !s.Consumed).All(s => s.Releases == 1) && noMemory.Stores.Count(s => !s.Consumed) == 1 && noMemory.NoLeaks(out why) && noMemory.Book.Building == 0,
      "a store that cannot be made: the ones made are released, the proxy fails, the slot is back");
  }

  // ---- compact vertices ---------------------------------------------------------------------------------------------------------------------

  private static float AngleBetween(Vector3 a, Vector3 b)
  {
    double dot = (a.x * (double)b.x + a.y * (double)b.y + a.z * (double)b.z) / (Math.Sqrt(a.x * (double)a.x + a.y * (double)a.y + a.z * (double)a.z) * Math.Sqrt(b.x * (double)b.x + b.y * (double)b.y + b.z * (double)b.z));
    return (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI);
  }

  private static void ProxyQuantTest()
  {
    Section("proxy: compact vertices");
    var compact = VertexLayout.Compact;
    C(compact.Stride == 12 && compact.Quantised && !VertexLayout.Plain.Quantised && ProxyMerge.Layout == compact && ProxyMerge.MaxChunkVertices == 65_535,
      "the proxy's vertex is 12 bytes: four 16 bit fractions and four signed bytes; a chunk takes 65,535 vertices at most");

    // the box: a tight box is kept, a side that is too thin is widened about the centre
    var tight = QuantBox.Of(-30f, 0f, -32f, 30f, 40f, 32f);
    C(tight.MinX == -30f && tight.SizeX == 60f && tight.SizeY == 40f && tight.SizeZ == 64f && tight.MinY == 0f, "a box whose sides are within 2:1 of the longest is kept");
    var flat = QuantBox.Of(0f, 5f, 0f, 64f, 5.001f, 64f);
    C(flat.SizeX == 64f && flat.SizeZ == 64f && flat.SizeY == 32f && Math.Abs(flat.MinY + flat.SizeY * 0.5f - 5.0005f) < 1e-4f, $"a flat box is widened to half the longest side about its centre ({flat.SizeY} m)");
    var point = QuantBox.Of(3f, 4f, 5f, 3f, 4f, 5f);
    C(point.SizeX == QuantBox.MinSide && point.SizeY == QuantBox.MinSide && point.SizeZ == QuantBox.MinSide && Math.Abs(point.MinX + point.SizeX * 0.5f - 3f) < 1e-6f, "a box of no size gets the minimum side, so the matrix has an inverse");
    var m = flat.ToWorld(128f, -64f);
    C(m.m00 == 64f && m.m11 == 32f && m.m22 == 64f && m.m03 == 128f + flat.MinX && m.m13 == flat.MinY && m.m23 == -64f + flat.MinZ && m.m33 == 1f && ProxyMerge.HasInverse(m), "the matrix scales the unit box to the box and puts it at the zone's place");

    // positions: 1 mm on a 64 m box, w = 1
    var rnd = new System.Random(20261009);
    var box = QuantBox.Of(-32f, 0f, -32f, 32f, 24f, 32f);
    int n = 20_000;
    var pts = new Vector3[n];
    var nrm = new Vector3[n];
    for (int i = 0; i < n; i++)
    {
      pts[i] = new Vector3((float)(rnd.NextDouble() * 64 - 32), (float)(rnd.NextDouble() * 24), (float)(rnd.NextDouble() * 64 - 32));
      nrm[i] = Vector3.up;
    }
    var bytes = new byte[n * 12];
    VertexPacker.Pack(compact, new VertexSource(pts, nrm), n, box, bytes);
    double worst = 0;
    bool whole = true;
    var world = box.ToWorld(0f, 0f);
    for (int i = 0; i < n; i++)
    {
      var q = (BitConverter.ToUInt16(bytes, i * 12), BitConverter.ToUInt16(bytes, i * 12 + 2), BitConverter.ToUInt16(bytes, i * 12 + 4));
      whole &= BitConverter.ToUInt16(bytes, i * 12 + 6) == 65535;
      // through the matrix, as the GPU does: unit fraction to world
      double x = world.m00 * (q.Item1 / 65535.0) + world.m03, y = world.m11 * (q.Item2 / 65535.0) + world.m13, z = world.m22 * (q.Item3 / 65535.0) + world.m23;
      worst = Math.Max(worst, Math.Max(Math.Abs(x - pts[i].x), Math.Max(Math.Abs(y - pts[i].y), Math.Abs(z - pts[i].z))));
    }
    C(worst <= 0.001, $"20,000 positions through the matrix are within 1 mm of what was packed (worst {worst * 1000:0.000} mm)");
    C(whole, "the fourth component of every position is 65535, which reads as 1 (the piece shader multiplies it into the translation)");
    // the ends of the box
    var ends = new byte[24];
    VertexPacker.Pack(compact, new VertexSource([new Vector3(-30, 0, -32), new Vector3(30, 40, 32)], [Vector3.up, Vector3.up]), 2, tight, ends);
    C(BitConverter.ToUInt16(ends, 0) == 0 && BitConverter.ToUInt16(ends, 2) == 0 && BitConverter.ToUInt16(ends, 4) == 0 && BitConverter.ToUInt16(ends, 12) == 65535 && BitConverter.ToUInt16(ends, 12 + 2) == 65535 && BitConverter.ToUInt16(ends, 12 + 4) == 65535,
      "the box's corners are fractions 0 and 65535");

    // normals: after the shader's inverse transpose, within one degree, in thin boxes too
    double worstAngle = 0;
    int checkedNormals = 0;
    for (int t = 0; t < 400; t++)
    {
      float Side() => rnd.Next(5) == 0 ? 0f : (float)Math.Exp(rnd.NextDouble() * Math.Log(64.0 / 0.001) + Math.Log(0.001));
      float sx = Side(), sy = Side(), sz = Side();
      var b = QuantBox.Of(-sx / 2, -sy / 2, -sz / 2, sx / 2, sy / 2, sz / 2);
      var ns = new Vector3[20];
      for (int i = 0; i < ns.Length; i++)
      {
        double u = rnd.NextDouble() * 2 - 1, phi = rnd.NextDouble() * 2 * Math.PI, r = Math.Sqrt(1 - u * u);
        ns[i] = new Vector3((float)(r * Math.Cos(phi)), (float)u, (float)(r * Math.Sin(phi)));
      }
      var pb = new byte[ns.Length * 12];
      VertexPacker.Pack(compact, new VertexSource(new Vector3[ns.Length], ns), ns.Length, b, pb);
      for (int i = 0; i < ns.Length; i++)
      {
        var landed = ProxyKit.Shader(b, ProxyKit.Snorm(pb, i * 12 + 8));
        worstAngle = Math.Max(worstAngle, AngleBetween(landed, ns[i]));
        checkedNormals++;
      }
    }
    C(worstAngle <= 1.0, $"{checkedNormals} normals in 400 random boxes, thin and flat ones among them: the shader's normal is within {worstAngle:0.00} degrees of the true one");
    // why the widening: the same normals in a 64 x 0.02 x 64 box that was not widened
    var thin = new QuantBox(0f, 0f, 0f, 64f, 0.02f, 64f);
    var slant = new[] { new Vector3(0.6f, 0.8f, 0f) };
    var tb = new byte[12];
    VertexPacker.Pack(compact, new VertexSource(new Vector3[1], slant), 1, thin, tb);
    C(AngleBetween(ProxyKit.Shader(thin, ProxyKit.Snorm(tb, 8)), slant[0]) > 1.0, "...and a 64 x 0.02 x 64 box that was not widened would have put this normal more than a degree off");
    // the unit box leaves a normal alone (float layouts)
    C(QuantBox.Unit.Compensate(new Vector3(0.6f, 0.8f, 0f)) == new Vector3(0.6f, 0.8f, 0f), "the unit box does not touch a normal");

    // winding survives the quantising: random matrices, mirrored ones among them
    var tri = ProxyKit.Triangle();
    int flipped = 0, mirrored = 0;
    for (int k = 0; k < 400; k++)
    {
      var rot = BakedMath.Euler((float)(rnd.NextDouble() * 360), (float)(rnd.NextDouble() * 360), (float)(rnd.NextDouble() * 360),
        (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)), (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)), (float)((rnd.NextDouble() * 2.7 + 0.3) * (rnd.Next(3) == 0 ? -1 : 1)));
      var mat = BakedMath.Frame(rot, new Vector3((float)(rnd.NextDouble() * 60 - 30), (float)(rnd.NextDouble() * 10), (float)(rnd.NextDouble() * 60 - 30)));
      if (BakedMath.Det3(mat) < 0f)
        mirrored++;
      var v = new Vector3[3];
      var nn = new Vector3[3];
      var ix = new int[3];
      ProxyMerge.Transform(tri, mat, 0f, 0f, v, nn, 0, ix, 0);
      var tb2 = QuantBox.Of(Math.Min(v[0].x, Math.Min(v[1].x, v[2].x)), Math.Min(v[0].y, Math.Min(v[1].y, v[2].y)), Math.Min(v[0].z, Math.Min(v[1].z, v[2].z)),
        Math.Max(v[0].x, Math.Max(v[1].x, v[2].x)), Math.Max(v[0].y, Math.Max(v[1].y, v[2].y)), Math.Max(v[0].z, Math.Max(v[1].z, v[2].z)));
      var pk = new byte[36];
      VertexPacker.Pack(compact, new VertexSource(v, nn), 3, tb2, pk);
      var back = new Vector3[3];
      for (int i = 0; i < 3; i++)
        back[i] = new Vector3(tb2.MinX + BitConverter.ToUInt16(pk, i * 12) / 65535f * tb2.SizeX, tb2.MinY + BitConverter.ToUInt16(pk, i * 12 + 2) / 65535f * tb2.SizeY, tb2.MinZ + BitConverter.ToUInt16(pk, i * 12 + 4) / 65535f * tb2.SizeZ);
      var before = ProxyKit.Facing(v, ix, 0);
      var after = ProxyKit.Facing(back, ix, 0);
      if (!(ProxyKit.Dot(before, after) > 0f && AngleBetween(before, after) < 1f))
        flipped++;
    }
    C(flipped == 0 && mirrored > 50, $"400 random matrices ({mirrored} mirrored): every triangle faces the way it did before the quantising ({flipped} changed)");

    // the whole merge, compact against plain: same chunks, positions through the matrix within a millimetre, normals within a degree
    var zone = BakedZoneBuild.Build(ClientKit.Zone(2, -1,
      Enumerable.Range(0, 60).Select(i => (0, 2 * 64.0 + 4 + i % 50, i % 7 * 0.5, -64.0 + 3 + i * 5 % 55, i * 37.0 % 360, (Vector3?)null)).ToArray()), [ProxyKit.TwoLodKind("rock", ProxyKit.Fan(40), ProxyKit.Triangle(), flags: PaletteFlags.None)], 1);
    var a = ProxyMerge.Merge(zone, true, BakedShadows.All, ProxyMerge.MaxChunkVertices, compact);
    var b2 = ProxyMerge.Merge(zone, true, BakedShadows.All, ProxyMerge.MaxChunkVertices, VertexLayout.Plain);
    C(a.Chunks.Length == b2.Chunks.Length && a.Vertices == b2.Vertices && a.Chunks.Zip(b2.Chunks, (x, y) => x.VertexCount == y.VertexCount && x.IndexCount == y.IndexCount).All(t => t),
      "compact and plain merges split into the same chunks");
    float ox = 2 * 64f, oz = -64f;
    double posWorst = 0, nrmWorst = 0;
    for (int c = 0; c < a.Chunks.Length; c++)
    {
      var (_, an, ai) = ProxyKit.Read(a.Chunks[c]);
      var (pp, pn, pi) = ProxyKit.Read(b2.Chunks[c]);
      var mat = a.Chunks[c].ToWorld(ox, oz);
      var st = (ByteStore)a.Chunks[c].Store!;
      for (int v = 0; v < pp.Length; v++)
      {
        double x = mat.m00 * (BitConverter.ToUInt16(st.Vertices, v * 12) / 65535.0) + mat.m03, y = mat.m11 * (BitConverter.ToUInt16(st.Vertices, v * 12 + 2) / 65535.0) + mat.m13, z = mat.m22 * (BitConverter.ToUInt16(st.Vertices, v * 12 + 4) / 65535.0) + mat.m23;
        posWorst = Math.Max(posWorst, Math.Max(Math.Abs(x - (pp[v].x + ox)), Math.Max(Math.Abs(y - pp[v].y), Math.Abs(z - (pp[v].z + oz)))));
        nrmWorst = Math.Max(nrmWorst, AngleBetween(an[v], pn[v]));
      }
      C(ai.SequenceEqual(pi), $"chunk {c}: the same indices");
    }
    C(posWorst <= 0.001 && nrmWorst <= 1.0, $"...positions through the chunk's matrix are within {posWorst * 1000:0.000} mm and normals within {nrmWorst:0.00} degrees of the plain merge's");
    C(a.Bytes * 2 > b2.Bytes && a.Bytes * 5 < b2.Bytes * 3, $"...and the compact proxy takes {a.Bytes:N0} bytes where the plain one takes {b2.Bytes:N0}");

    System.Console.WriteLine($"   compact vertices: worst position error {worst * 1000:0.000} mm over 20,000 points, worst normal {worstAngle:0.00} degrees over {checkedNormals} (merge: {posWorst * 1000:0.000} mm, {nrmWorst:0.00} degrees); {a.Bytes:N0} bytes against {b2.Bytes:N0}");

    // the index width follows the chunk's vertex count
    BuiltZone One(int fan) => ProxyPlanZone(out _, out _, walls: 1, beams: 0, fan: fan);
    var edge = ProxyMerge.Plan(One(65_533), true, BakedShadows.All, compact);
    var over = ProxyMerge.Plan(One(65_534), true, BakedShadows.All, compact);
    C(edge.Chunks.Single().VertexCount == 65_535 && !edge.Chunks.Single().Wide && edge.Chunks.Single().Bytes == 65_535L * 12 + 65_533L * 3 * 2, "a chunk of 65,535 vertices has 16 bit indices");
    C(over.Chunks.Single().VertexCount == 65_536 && over.Chunks.Single().Wide && over.Chunks.Single().Bytes == 65_536L * 12 + 65_534L * 3 * 4, "a lone mesh of 65,536 has 32 bit indices, and says its bytes so");
    // two meshes of 32,002 share a chunk; a third starts the next
    var three = ProxyMerge.Plan(ProxyPlanZone(out _, out _, walls: 3, beams: 0, fan: 32_000), true, BakedShadows.All, compact);
    C(three.Chunks.Length == 2 && three.Chunks[0].VertexCount == 64_004 && three.Chunks[1].VertexCount == 32_002 && three.Chunks.All(c => !c.Wide), "three meshes of 32,002 vertices: two share a chunk, the third has the next");
  }

  // ---- the numbers bc_bake stats prints -------------------------------------------------------------------------------------------------------

  private static void ProxyStatsTest()
  {
    Section("proxy: the numbers in bc_bake stats");
    BakedDraw.ProxyAdopted = BakedDraw.ProxyVersion;
    // eight kinds of different sizes in one zone, the biggest first by bytes
    var kinds = Enumerable.Range(1, 8).Select(i =>
    {
      var piece = ClientKit.NoLods("kind" + i, 1f);
      ProxyKit.Take(piece.Parts[0], ProxyKit.Fan(i * 10));
      return ClientKit.Kind("kind" + i, piece);
    }).ToArray();
    var records = Enumerable.Range(0, 8).Select(i => (i, 1.0 + i * 5, 0.0, 1.0, 0.0, (Vector3?)null)).ToArray();
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), kinds, 1);
    var fake = new ProxyKit.Fake();
    fake.Book.Upload = (c, _) =>
    {
      System.Threading.Thread.Sleep(c.VertexCount > 100 ? 4 : 0);
      ProxyKit.Fake.Consume(c);
      return true;
    };
    for (int i = 0; i < 3; i++)
      fake.Book.Tick(ProxyKit.Frame([zone], 1, Vector3.zero));
    var n = fake.Book.Numbers(1);
    C(n.Near == 1 && n.Far == 1, "(a near and a far proxy are ready)");
    C(n.NearBytes > 0 && n.FarBytes > 0 && n.NearBytes + n.FarBytes == n.Bytes && n.RetiringBytes == 0, "graphics memory is split into near and far");
    C(n.TopKinds.Length == 6, $"the six kinds with the most proxy memory are listed ({n.TopKinds.Length})");
    // kind i has i * 10 triangles in 12 + 10 (i - 1) vertices, and is merged in both the near and the far proxy (no LOD to switch)
    C(n.TopKinds.Select(k => k.Name).SequenceEqual(["kind8", "kind7", "kind6", "kind5", "kind4", "kind3"]) && n.TopKinds.Zip(n.TopKinds.Skip(1), (a, b) => a.Bytes > b.Bytes).All(x => x),
      $"...the biggest first ({string.Join(", ", n.TopKinds.Select(k => k.Name))})");
    var k8 = n.TopKinds[0];
    C(k8.Vertices == 2 * 82 && k8.Bytes == 2 * (82L * 12 + 80 * 3 * 2), $"...with its vertices and bytes over both proxies ({k8.Vertices} vertices, {k8.Bytes} bytes)");
    var line = n.KindsLine();
    C(line.StartsWith("Shadow proxy memory by kind") && line.Contains("kind8") && line.Contains("MB") && line.Contains("vertices") && !line.Contains("kind2"), $"the line says it ({line})");
    C(n.Line().Contains("MB near") && n.Line().Contains("MB far"), "the proxies line gives the near and far memory");
    C(n.ApplyMaxMs >= 3.5 && n.UploadMaxFrameMs >= n.ApplyMaxMs && n.UploadAverageMs <= n.ApplyMaxMs && n.TimeLine().Contains("worst single mesh"), $"the worst single apply is kept ({n.ApplyMaxMs:0.0} ms)");
    C(n.PlanMsTotal >= 0 && n.WriteMsTotal >= 0 && n.TimeLine().Contains("on workers") && n.TimeLine().Contains("on the main thread"), "the worker time and the main thread's are told apart");
    var none = new ProxyKit.Fake().Book.Numbers(0);
    C(none.KindsLine().Contains("none yet"), "with no proxy ready the kinds line says so");
  }
}

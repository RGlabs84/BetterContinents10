// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// The shadow proxies (BakedProxyMerge.cs, BakedProxyBook.cs; the engine's side, BakedProxy.cs, needs the GPU and is not here): the merge of known
// parts and matrices into vertices, normals and indices (a mirrored matrix turns the triangles round), the chunks and their layers, the level of
// detail by distance, what a proxy leaves to the instancing (cutout materials, unreadable meshes, parts that do not cast), the numbers read back from
// a GPU, how the drawing job routes a covered zone's instances, and the book's life of a proxy: made in steps, drawn, taken back only after a job
// that knows has been adopted, freed when its zone is rebuilt, unloaded or out of the ring.
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

  /// <summary>A book with the engine stood in for: workers run at once, every part is settled, uploads and frees are noted.</summary>
  public sealed class Fake
  {
    public readonly List<ProxyChunk> Uploaded = [], Freed = [], Drawn = [];
    public readonly ProxyBook Book;
    public Fake()
    {
      Book = new ProxyBook
      {
        Work = job => job(),
        Settle = _ => true,
        Upload = (chunk, _) =>
        {
          chunk.Vertices = chunk.Normals = null;
          chunk.Indices = null;
          Uploaded.Add(chunk);
          return true;
        },
        Free = Freed.Add,
        Draw = (chunk, _) => Drawn.Add(chunk),
      };
    }
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
    C(chunk.VertexCount == 3 && chunk.IndexCount == 3 && chunk.Layer == 10, "one instance of one triangle: one chunk of 3 vertices and 3 indices");
    C(ProxyNear(chunk.Vertices![0], new Vector3(10, 2, 20)) && ProxyNear(chunk.Vertices[1], new Vector3(11, 2, 20)) && ProxyNear(chunk.Vertices[2], new Vector3(10, 3, 20)),
      $"the vertices are the instance's, relative to the zone's origin on x and z ({Fmt3(chunk.Vertices[0])}, {Fmt3(chunk.Vertices[1])}, {Fmt3(chunk.Vertices[2])})");
    C(chunk.Normals!.All(n => ProxyNear(n, Vector3.forward)) && chunk.Indices!.SequenceEqual([0, 1, 2]), "the normals face +z and the winding is as it was");
    C(ProxyNear(new Vector3(chunk.MinX, chunk.MinY, chunk.MinZ), new Vector3(10, 2, 20)) && ProxyNear(new Vector3(chunk.MaxX, chunk.MaxY, chunk.MaxZ), new Vector3(11, 3, 20)),
      "the chunk's box is that of its vertices");
    C(built.KindCovered.SequenceEqual([true]) && built.Triangles == 1 && built.Vertices == 3 && built.Bytes == 3 * 24 + 3 * 4, "the kind is covered; triangles, vertices and bytes are counted");

    // a turn of 90 degrees about y carries x to -z and the normal to +x
    var turned = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5.0, 1.0, 5.0, 90.0, null)), [kind], 1);
    var tc = ProxyMerge.Merge(turned, true, BakedShadows.All).Chunks.Single();
    C(ProxyNear(tc.Vertices![1], new Vector3(5, 1, 4)) && ProxyNear(tc.Vertices[2], new Vector3(5, 2, 5)), $"yaw 90: the triangle's x edge points along -z ({Fmt3(tc.Vertices[1])})");
    C(tc.Normals!.All(n => ProxyNear(n, Vector3.right, 0.02f)), "...and its normal along +x");

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
    var dc = ProxyMerge.Merge(doorZone, true, BakedShadows.All).Chunks.Single();
    C(ProxyNear(dc.Vertices![0], new Vector3(3, 11, 4)), $"a part's own matrix is applied below the instance's ({Fmt3(dc.Vertices[0])})");

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
      clean &= c.Indices!.Take(c.IndexCount).All(i => i >= 0 && i < c.VertexCount) && c.Vertices!.Length == c.VertexCount && c.Indices.Length == c.IndexCount;
      boxed &= c.Vertices!.All(v => v.x >= c.MinX - 1e-4f && v.x <= c.MaxX + 1e-4f && v.y >= c.MinY - 1e-4f && v.y <= c.MaxY + 1e-4f && v.z >= c.MinZ - 1e-4f && v.z <= c.MaxZ + 1e-4f);
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
    C(numbers.ZonesWithProxy == 2 && numbers.Near == 1 && numbers.Far == 2 && numbers.Triangles == 24 && numbers.Vertices == 36 && numbers.Bytes == 36 * 24L + 72 * 4L,
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
    C(chunks.Chunks.Length == 2 && chunks.Chunks[0].VertexCount == 120000 && chunks.Chunks[1].VertexCount == 10000, $"1,300 pieces of 100 vertices are two chunks at the default cap ({chunks.Chunks.Length})");
    int made = 0;
    var failing = new ProxyKit.Fake();
    failing.Book.Upload = (c, _) => ++made < 2;
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
}

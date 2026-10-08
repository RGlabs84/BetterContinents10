// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterContinents;

// THE SHADOW PROXY, ARITHMETIC (no Unity object is made or read here; tools/placement-tests runs it without the game).
//
// A baked zone's pieces are drawn by GPU instancing, and every one of those draws is repeated in each shadow pass (each cascade, each face of each
// point light that casts): VALtima's blueprint district sent thousands of shadow draws a frame. A shadow proxy is the zone's casters merged into a
// few meshes (positions and normals only) that are drawn for shadows alone; the pieces themselves keep their instanced drawing, which then casts
// nothing where a proxy covers it. See BakedProxyBook.cs for when a zone has one, and BakedProxy.cs for the Unity side.

/// <summary>
/// One submesh of a mesh as a proxy merges it: the vertices its triangles use, their normals and the triangles, in the mesh's own frame.
/// Plain arrays, so a worker can read them.
/// </summary>
internal sealed class ProxyShape
{
  internal Vector3[] Vertices = [];
  internal Vector3[] Normals = [];
  /// <summary>Three indices a triangle, into Vertices.</summary>
  internal int[] Indices = [];

  internal int Triangles => Indices.Length / 3;

  /// <summary>The CPU memory this holds (bytes).</summary>
  internal long Bytes => Vertices.Length * 24L + Indices.Length * 4L;

  /// <summary>
  /// A shape from a mesh's vertex arrays and one submesh's triangles: only the vertices the triangles use are kept (a mesh with several submeshes
  /// shares one vertex array), a missing or short normal array is made up from the triangles (area weighted), and a normal of no length becomes up.
  /// Null when there is nothing to draw or an index lies outside the vertices.
  /// </summary>
  internal static ProxyShape? Make(Vector3[] vertices, Vector3[]? normals, int[] triangles)
  {
    int n = triangles.Length / 3 * 3;
    if (n == 0 || vertices.Length == 0)
      return null;
    var map = new int[vertices.Length];
    for (int i = 0; i < map.Length; i++)
      map[i] = -1;
    int used = 0;
    for (int i = 0; i < n; i++)
    {
      int v = triangles[i];
      if ((uint)v >= (uint)vertices.Length)
        return null;
      if (map[v] < 0)
        map[v] = used++;
    }
    var shape = new ProxyShape { Vertices = new Vector3[used], Normals = new Vector3[used], Indices = new int[n] };
    bool haveNormals = normals != null && normals.Length == vertices.Length;
    for (int v = 0; v < map.Length; v++)
    {
      int at = map[v];
      if (at < 0)
        continue;
      shape.Vertices[at] = vertices[v];
      if (haveNormals)
        shape.Normals[at] = normals![v];
    }
    for (int i = 0; i < n; i++)
      shape.Indices[i] = map[triangles[i]];
    if (!haveNormals)
      FaceNormals(shape);
    for (int v = 0; v < used; v++)
    {
      var nv = shape.Normals[v];
      float len = (float)Math.Sqrt(nv.x * nv.x + nv.y * nv.y + nv.z * nv.z);
      shape.Normals[v] = len > 1e-12f ? new Vector3(nv.x / len, nv.y / len, nv.z / len) : new Vector3(0f, 1f, 0f);
    }
    return shape;
  }

  // the area weighted sum of the normals of the triangles at each vertex
  private static void FaceNormals(ProxyShape s)
  {
    for (int i = 0; i + 2 < s.Indices.Length; i += 3)
    {
      int a = s.Indices[i], b = s.Indices[i + 1], c = s.Indices[i + 2];
      Vector3 p = s.Vertices[a], q = s.Vertices[b], r = s.Vertices[c];
      float ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
      var cross = new Vector3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
      s.Normals[a] += cross;
      s.Normals[b] += cross;
      s.Normals[c] += cross;
    }
  }
}

/// <summary>A mesh's vertex and index bytes, read back from the GPU, turned into numbers (the shapes of meshes the CPU cannot read).</summary>
internal static class ProxyDecode
{
  /// <summary>An IEEE half precision number (1 sign, 5 exponent, 10 fraction bits) as a float.</summary>
  internal static float Half(ushort h)
  {
    int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, frac = h & 0x3FF;
    float value;
    if (exp == 0)
      value = frac * (1f / 1024f) * (1f / 16384f);              // zero and subnormals: frac / 1024 * 2^-14
    else if (exp == 31)
      value = frac == 0 ? float.PositiveInfinity : float.NaN;
    else
      value = (1f + frac / 1024f) * (float)Math.Pow(2.0, exp - 15);
    return sign == 1 ? -value : value;
  }

  /// <summary>How many bytes one component of a format takes; 0 for a format that is not read here.</summary>
  internal static int ComponentSize(VertexAttributeFormat format) => format switch
  {
    VertexAttributeFormat.Float32 => 4,
    VertexAttributeFormat.Float16 => 2,
    VertexAttributeFormat.SNorm8 => 1,
    VertexAttributeFormat.SNorm16 => 2,
    _ => 0,
  };

  /// <summary>
  /// The first three components of an attribute (position or normal) of every vertex, from the bytes of the stream it lives in: vertex v is at
  /// v * stride, the attribute offset bytes into it. Float32 and Float16 for both; SNorm8 and SNorm16 are normals' formats and only read when
  /// <paramref name="normalised"/> is set. Null when the format is not one of these, there are fewer than three components, or the bytes are too few.
  /// </summary>
  internal static Vector3[]? ReadVectors(byte[] data, int vertexCount, int stride, int offset, VertexAttributeFormat format, int dimension, bool normalised)
  {
    int size = ComponentSize(format);
    if (size == 0 || dimension < 3 || stride <= 0 || offset < 0 || vertexCount <= 0)
      return null;
    if ((format == VertexAttributeFormat.SNorm8 || format == VertexAttributeFormat.SNorm16) && !normalised)
      return null;
    if (offset + dimension * size > stride || (long)(vertexCount - 1) * stride + offset + dimension * size > data.Length)
      return null;
    var result = new Vector3[vertexCount];
    for (int v = 0; v < vertexCount; v++)
    {
      int at = v * stride + offset;
      result[v] = new Vector3(Component(data, at, format), Component(data, at + size, format), Component(data, at + 2 * size, format));
    }
    return result;
  }

  private static float Component(byte[] data, int at, VertexAttributeFormat format)
  {
    switch (format)
    {
      case VertexAttributeFormat.Float32:
        return BitConverter.ToSingle(data, at);
      case VertexAttributeFormat.Float16:
        return Half(BitConverter.ToUInt16(data, at));
      case VertexAttributeFormat.SNorm8:
        return Math.Max((sbyte)data[at] / 127f, -1f);
      default:
        return Math.Max(BitConverter.ToInt16(data, at) / 32767f, -1f);
    }
  }

  /// <summary>
  /// The indices of one submesh: <paramref name="count"/> of them from index <paramref name="start"/> in the index buffer's bytes (16 or 32 bit),
  /// each plus the submesh's base vertex. Null when the bytes are too few or an index lies outside the vertices.
  /// </summary>
  internal static int[]? ReadIndices(byte[] data, bool wide, int start, int count, int baseVertex, int vertexCount)
  {
    int size = wide ? 4 : 2;
    if (start < 0 || count < 0 || (long)(start + count) * size > data.Length)
      return null;
    var result = new int[count];
    for (int i = 0; i < count; i++)
    {
      int index = (wide ? (int)BitConverter.ToUInt32(data, (start + i) * 4) : BitConverter.ToUInt16(data, (start + i) * 2)) + baseVertex;
      if ((uint)index >= (uint)vertexCount)
        return null;
      result[i] = index;
    }
    return result;
  }
}

/// <summary>The rules for what a proxy merges: which parts, which level of detail, which materials.</summary>
internal static class ProxyRules
{
  /// <summary>A zone within this many zones of the camera's (Chebyshev) has a proxy made of the pieces' first level of detail.</summary>
  internal const int NearZones = 1;

  /// <summary>Whether a zone this far from the camera's (in zones) is a near one.</summary>
  internal static bool IsNear(int dx, int dz) => Math.Max(Math.Abs(dx), Math.Abs(dz)) <= NearZones;

  /// <summary>
  /// Whether a kind has no level of detail to switch between, the way the cull-and-LOD job decides it (BakedDraw.Memo): one set of parts, a
  /// piece flagged LOD0Only, or a piece with no LOD group. Its proxies are the same for both variants.
  /// </summary>
  internal static bool IsSingle(BakedKind kind)
  {
    var piece = kind.Piece;
    return piece == null || piece.SingleLod || piece.EffectiveNearScreen <= 0f || kind.Lod0Only;
  }

  /// <summary>The level of detail whose parts a proxy variant draws for a kind: 0 (the first) for a near proxy or a kind that does not switch, else 1.</summary>
  internal static int LodOf(BakedKind kind, bool near) => near || IsSingle(kind) ? 0 : 1;

  /// <summary>The indexes into the piece's Parts that a proxy variant draws for a kind.</summary>
  internal static int[] PartsOf(BakedKind kind, bool near)
  {
    var piece = kind.Piece;
    if (piece == null)
      return [];
    return LodOf(kind, near) == 0 ? piece.NearParts : piece.FarParts;
  }

  /// <summary>The parts of a kind that a proxy variant merges: those that cast under the setting (the instanced drawing's own rule, BakedDraw.CastsFor)
  /// and that the proxy can take (RenderPart.ProxyOk, with a shape).</summary>
  internal static List<RenderPart> MergedParts(BakedKind kind, bool near, BakedShadows policy)
  {
    var result = new List<RenderPart>();
    var piece = kind.Piece;
    if (piece == null)
      return result;
    int lod = LodOf(kind, near);
    foreach (int index in PartsOf(kind, near))
    {
      var part = piece.Parts[index];
      if (part.ProxyState == RenderPart.ProxyOk && part.Shape != null && BakedDraw.CastsFor(kind, part, lod, policy))
        result.Add(part);
    }
    return result;
  }

  // ---- materials ----------------------------------------------------------------------------------------------------------------

  /// <summary>What a material says that decides whether its shadow needs its texture (read off the Material by BakedMeshShapes).</summary>
  internal readonly struct MaterialFacts(string shader, int queue, string renderType, bool alphaTest, bool alphaBlend, bool alphaPremultiply,
    bool hasCutoff, float cutoff, int mode)
  {
    internal readonly string Shader = shader;
    internal readonly int Queue = queue;
    internal readonly string RenderType = renderType;
    /// <summary>Whether the material has the keyword _ALPHATEST_ON, _ALPHABLEND_ON or _ALPHAPREMULTIPLY_ON.</summary>
    internal readonly bool AlphaTest = alphaTest, AlphaBlend = alphaBlend, AlphaPremultiply = alphaPremultiply;
    internal readonly bool HasCutoff = hasCutoff;
    internal readonly float Cutoff = cutoff;
    /// <summary>The Standard shader's _Mode (0 opaque), or -1 when the material has none.</summary>
    internal readonly int Mode = mode;
  }

  /// <summary>
  /// The shaders whose opaque materials a proxy takes: the game's piece, rock and trilinear shaders, and Unity's Standard in its opaque mode. Another
  /// shader (vegetation and grass clip their leaves by the texture's alpha, a mod's shader may do anything) keeps its shadows to the instancing.
  /// </summary>
  internal static bool OpaqueShader(string shader) =>
    shader == "Custom/Piece" || shader == "Custom/StaticRock" || shader == "Custom/Trilinearmap" || shader == "Standard";

  /// <summary>
  /// Whether a material's shadow is its shape alone. Not, when it is alpha tested or blended (the keywords _ALPHATEST_ON, _ALPHABLEND_ON and
  /// _ALPHAPREMULTIPLY_ON, a render queue of 2450 or more, a RenderType that is not Opaque): the shadow then needs the texture. Every Custom/Piece
  /// material has _Cutoff 0.5 and its texture's alpha is 1, so the value alone says nothing; one with another value above 0 was set up for a cutout
  /// and is left alone too. Returns RenderPart.ProxyOk, ProxyCutout or ProxyOther (a shader the proxy does not take), and says why.
  /// </summary>
  internal static byte Classify(in MaterialFacts f, out string why)
  {
    if (f.AlphaTest || f.AlphaBlend || f.AlphaPremultiply)
    {
      why = f.AlphaTest ? "keyword _ALPHATEST_ON" : f.AlphaBlend ? "keyword _ALPHABLEND_ON" : "keyword _ALPHAPREMULTIPLY_ON";
      return RenderPart.ProxyCutout;
    }
    if (f.Queue >= 2450)
    {
      why = $"render queue {f.Queue}";
      return RenderPart.ProxyCutout;
    }
    if (!string.Equals(f.RenderType, "Opaque", StringComparison.OrdinalIgnoreCase))
    {
      why = $"RenderType {f.RenderType}";
      return RenderPart.ProxyCutout;
    }
    if (!OpaqueShader(f.Shader))
    {
      why = $"shader {f.Shader}";
      return RenderPart.ProxyOther;
    }
    if (f.Shader == "Standard")
    {
      if (f.Mode > 0)
      {
        why = $"Standard _Mode {f.Mode}";
        return RenderPart.ProxyCutout;
      }
    }
    else if (f.HasCutoff && f.Cutoff > 0.001f && Math.Abs(f.Cutoff - 0.5f) > 0.001f)
    {
      why = $"_Cutoff {f.Cutoff:0.###}";
      return RenderPart.ProxyCutout;
    }
    why = "";
    return RenderPart.ProxyOk;
  }

  /// <summary>The worse of two classifications (a part with several materials is as good as its worst).</summary>
  internal static byte Worse(byte a, byte b) => a == RenderPart.ProxyOk ? b : a;
}

/// <summary>One mesh of a proxy: the merged vertices of some of a zone's pieces (relative to the zone's origin, so the floats stay small), until it is uploaded.</summary>
internal sealed class ProxyChunk
{
  /// <summary>The Unity layer of the pieces in it (a proxy is drawn on the layer its pieces are).</summary>
  internal int Layer;
  /// <summary>The numbers, until the main thread has made the mesh from them (then null).</summary>
  internal Vector3[]? Vertices, Normals;
  internal int[]? Indices;
  internal int VertexCount, IndexCount;
  /// <summary>The box of the vertices, relative to the zone's origin (minimum then maximum).</summary>
  internal float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
  /// <summary>Main thread: the mesh and how it is drawn, once uploaded.</summary>
  internal Mesh? Mesh;
  internal RenderParams Params;
  internal bool Live;

  internal int Triangles => IndexCount / 3;
  /// <summary>Graphics memory (bytes): a position and a normal of three floats a vertex, and four bytes an index.</summary>
  internal long Bytes => VertexCount * 24L + IndexCount * 4L;
}

/// <summary>What one merge made of a zone.</summary>
internal sealed class ProxyBuilt
{
  internal bool Near;
  internal ProxyChunk[] Chunks = [];
  /// <summary>Per entry of the zone's Kinds: the proxy holds at least one part of it.</summary>
  internal bool[] KindCovered = [];
  /// <summary>Instances merged (one for each part of each instance).</summary>
  internal int PartsMerged;
  internal long Vertices, Triangles, Bytes;
  internal double Ms;
}

internal static class ProxyMerge
{
  /// <summary>The most vertices in a chunk (one more instance's mesh may take it past): a mesh is made, uploaded and sized for one chunk at a time.</summary>
  internal const int MaxChunkVertices = 120_000;

  /// <summary>
  /// Merges the casters of a built zone into chunks, for the near variant (the pieces' first level of detail) or the far one (the last): every
  /// instance of every kind, each part that <see cref="ProxyRules.MergedParts"/> names, moved by the instance's matrix. Cell by cell, so a chunk
  /// holds pieces that stand together and its box is tight. Pure: no Unity object is touched.
  /// </summary>
  internal static ProxyBuilt Merge(BuiltZone zone, bool near, BakedShadows policy, int maxVertices = MaxChunkVertices)
  {
    long started = Stopwatch.GetTimestamp();
    var built = new ProxyBuilt { Near = near, KindCovered = new bool[zone.Kinds.Length] };
    float ox = zone.Zx * 64f, oz = zone.Zz * 64f;
    var use = new List<RenderPart>[zone.Kinds.Length];
    var layers = new SortedSet<int>();
    for (int g = 0; g < use.Length; g++)
    {
      use[g] = ProxyRules.MergedParts(zone.Kinds[g].Kind, near, policy);
      if (use[g].Count > 0)
        layers.Add(zone.Kinds[g].Kind.UnityLayer);
    }
    var chunks = new List<ProxyChunk>();
    foreach (int layer in layers)
    {
      var writer = new Writer(layer, maxVertices, chunks);
      for (int cell = 0; cell < BakedDraw.Cells; cell++)
        for (int g = 0; g < use.Length; g++)
        {
          var ki = zone.Kinds[g];
          var parts = use[g];
          if (parts.Count == 0 || ki.Kind.UnityLayer != layer)
            continue;
          for (int i = ki.CellStart[cell]; i < ki.CellStart[cell + 1]; i++)
            foreach (var part in parts)
            {
              writer.Add(part.Shape!, part.LocalIndex < 0 ? ki.Root[i] : ki.Locals[part.LocalIndex][i], ox, oz);
              built.KindCovered[g] = true;
              built.PartsMerged++;
            }
        }
      writer.Close();
    }
    built.Chunks = chunks.ToArray();
    foreach (var c in built.Chunks)
    {
      built.Vertices += c.VertexCount;
      built.Triangles += c.Triangles;
      built.Bytes += c.Bytes;
    }
    built.Ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    return built;
  }

  /// <summary>
  /// A shape moved by a matrix: positions by the matrix, less the zone's origin on x and z; normals by the inverse transpose of its 3 x 3 (its
  /// cofactor matrix, turned round when the matrix mirrors) and made unit length; a mirrored matrix (negative determinant) turns every triangle
  /// round, so that it still faces outward. False when the matrix has no inverse. The arrays are written from <paramref name="vAt"/> and
  /// <paramref name="iAt"/>.
  /// </summary>
  internal static bool Transform(ProxyShape shape, in Matrix4x4 m, float ox, float oz, Vector3[] vertices, Vector3[] normals, int vAt, int[] indices, int iAt)
  {
    float a00 = m.m00, a01 = m.m01, a02 = m.m02, a10 = m.m10, a11 = m.m11, a12 = m.m12, a20 = m.m20, a21 = m.m21, a22 = m.m22;
    float c00 = a11 * a22 - a12 * a21, c01 = a12 * a20 - a10 * a22, c02 = a10 * a21 - a11 * a20;
    float c10 = a02 * a21 - a01 * a22, c11 = a00 * a22 - a02 * a20, c12 = a01 * a20 - a00 * a21;
    float c20 = a01 * a12 - a02 * a11, c21 = a02 * a10 - a00 * a12, c22 = a00 * a11 - a01 * a10;
    float det = a00 * c00 + a01 * c01 + a02 * c02;
    if (!(Math.Abs(det) > 1e-18f))
      return false;
    float sign = det < 0f ? -1f : 1f;
    float tx = m.m03 - ox, ty = m.m13, tz = m.m23 - oz;
    var sv = shape.Vertices;
    var sn = shape.Normals;
    for (int k = 0; k < sv.Length; k++)
    {
      Vector3 p = sv[k], n = sn[k];
      vertices[vAt + k] = new Vector3(a00 * p.x + a01 * p.y + a02 * p.z + tx, a10 * p.x + a11 * p.y + a12 * p.z + ty, a20 * p.x + a21 * p.y + a22 * p.z + tz);
      float nx = (c00 * n.x + c01 * n.y + c02 * n.z) * sign, ny = (c10 * n.x + c11 * n.y + c12 * n.z) * sign, nz = (c20 * n.x + c21 * n.y + c22 * n.z) * sign;
      float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
      normals[vAt + k] = len > 1e-20f ? new Vector3(nx / len, ny / len, nz / len) : new Vector3(0f, 1f, 0f);
    }
    var si = shape.Indices;
    if (det > 0f)
      for (int t = 0; t < si.Length; t++)
        indices[iAt + t] = vAt + si[t];
    else
      for (int t = 0; t + 2 < si.Length; t += 3)
      {
        indices[iAt + t] = vAt + si[t];
        indices[iAt + t + 1] = vAt + si[t + 2];
        indices[iAt + t + 2] = vAt + si[t + 1];
      }
    return true;
  }

  // The chunk being filled; closes it, and starts the next, when the next mesh would take it past the vertex cap.
  private sealed class Writer(int layer, int maxVertices, List<ProxyChunk> into)
  {
    private Vector3[] vertices = [], normals = [];
    private int[] indices = [];
    private int vCount, iCount;
    private float minX, minY, minZ, maxX, maxY, maxZ;

    internal void Add(ProxyShape shape, in Matrix4x4 m, float ox, float oz)
    {
      int nv = shape.Vertices.Length, ni = shape.Indices.Length;
      if (nv == 0 || ni == 0)
        return;
      if (vCount > 0 && vCount + nv > maxVertices)
        Close();
      Reserve(vCount + nv, iCount + ni);
      if (!Transform(shape, m, ox, oz, vertices, normals, vCount, indices, iCount))
        return;
      if (vCount == 0)
      {
        minX = minY = minZ = float.MaxValue;
        maxX = maxY = maxZ = float.MinValue;
      }
      for (int k = vCount; k < vCount + nv; k++)
      {
        var p = vertices[k];
        if (p.x < minX) minX = p.x;
        if (p.y < minY) minY = p.y;
        if (p.z < minZ) minZ = p.z;
        if (p.x > maxX) maxX = p.x;
        if (p.y > maxY) maxY = p.y;
        if (p.z > maxZ) maxZ = p.z;
      }
      vCount += nv;
      iCount += ni;
    }

    private void Reserve(int v, int i)
    {
      if (v > vertices.Length)
      {
        int size = Math.Max(v, Math.Max(1024, vertices.Length * 2));
        Array.Resize(ref vertices, size);
        Array.Resize(ref normals, size);
      }
      if (i > indices.Length)
        Array.Resize(ref indices, Math.Max(i, Math.Max(3072, indices.Length * 2)));
    }

    internal void Close()
    {
      if (vCount == 0)
        return;
      var chunk = new ProxyChunk
      {
        Layer = layer, VertexCount = vCount, IndexCount = iCount,
        Vertices = new Vector3[vCount], Normals = new Vector3[vCount], Indices = new int[iCount],
        MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
      };
      Array.Copy(vertices, chunk.Vertices, vCount);
      Array.Copy(normals, chunk.Normals, vCount);
      Array.Copy(indices, chunk.Indices, iCount);
      into.Add(chunk);
      vCount = iCount = 0;
    }
  }
}

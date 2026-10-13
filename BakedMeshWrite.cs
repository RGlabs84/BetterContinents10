// Added by Wubarrk on 2026-10-09 for baked placements (0.10.4).

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterContinents;

// WRITING A MESH'S VERTICES AND INDICES INTO MEMORY A WORKER OWNS (no Unity object is made or read here; tools/placement-tests runs it without the game).
//
// A merge (the shadow proxies now, a merge of the busy zones into real meshes later) builds its chunks on a worker. What a chunk is written into is a
// ChunkStore: the engine's is a writable Mesh.MeshData (BakedMeshData.cs), whose memory the main thread then hands to a Mesh without a copy; the tests' is
// two byte arrays. What is written is described by a VertexLayout (an interleaved vertex: which attributes, in which format) and packed by VertexPacker
// from plain arrays, so a merge that wants more attributes names them in its layout and fills more arrays of its VertexSource; nothing here knows what a
// proxy is.
//
// A compact layout stores positions as 16 bit fractions of a box (a QuantBox: the box of the chunk's own vertices) and normals as signed bytes; the chunk is
// then drawn with a matrix that scales the unit box back to the box (QuantBox.ToWorld). That matrix is not uniform, and Unity moves a normal by the inverse
// transpose of the object matrix, so the normals are stored already divided the other way (see QuantBox.Compensate): the shader then lands on the true one.

/// <summary>One attribute of an interleaved vertex.</summary>
internal readonly struct VertexField(VertexAttribute attribute, VertexAttributeFormat format, int dimension, int offset = 0)
{
  internal readonly VertexAttribute Attribute = attribute;
  internal readonly VertexAttributeFormat Format = format;
  internal readonly int Dimension = dimension;
  /// <summary>Bytes from the start of the vertex.</summary>
  internal readonly int Offset = offset;

  internal int Size => Dimension * VertexLayout.FormatSize(Format);
  internal VertexField At(int offset) => new(Attribute, Format, Dimension, offset);
}

/// <summary>An interleaved vertex: its attributes in order, each starting where the one before ends, in one stream.</summary>
internal sealed class VertexLayout
{
  internal readonly VertexField[] Fields;
  /// <summary>Bytes a vertex takes.</summary>
  internal readonly int Stride;

  /// <summary>
  /// The attributes, in the order Unity wants them (position, normal, tangent, colour, then the uv channels). Every attribute starts and ends on a
  /// multiple of 4 bytes (the graphics APIs read a vertex stream that way).
  /// </summary>
  internal VertexLayout(params VertexField[] fields)
  {
    Fields = new VertexField[fields.Length];
    int at = 0;
    for (int i = 0; i < fields.Length; i++)
    {
      var f = fields[i].At(at);
      if (f.Dimension < 1 || f.Dimension > 4 || VertexLayout.FormatSize(f.Format) == 0 || f.Size % 4 != 0)
        throw new ArgumentException($"vertex attribute {f.Attribute} ({f.Format} x {f.Dimension}) does not fill whole 4-byte words");
      Fields[i] = f;
      at += f.Size;
    }
    Stride = at;
  }

  /// <summary>The field of an attribute, or null.</summary>
  internal VertexField? Find(VertexAttribute attribute)
  {
    foreach (var f in Fields)
      if (f.Attribute == attribute)
        return f;
    return null;
  }

  /// <summary>Bytes of one component of a format; 0 for a format that is not known.</summary>
  internal static int FormatSize(VertexAttributeFormat format) => format switch
  {
    VertexAttributeFormat.Float32 or VertexAttributeFormat.UInt32 or VertexAttributeFormat.SInt32 => 4,
    VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16 or VertexAttributeFormat.UInt16 or VertexAttributeFormat.SInt16 => 2,
    VertexAttributeFormat.UNorm8 or VertexAttributeFormat.SNorm8 or VertexAttributeFormat.UInt8 or VertexAttributeFormat.SInt8 => 1,
    _ => 0,
  };

  /// <summary>Position and normal as three floats each: 24 bytes a vertex.</summary>
  internal static readonly VertexLayout Plain = new(
    new VertexField(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
    new VertexField(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3));

  /// <summary>
  /// Position as four 16 bit fractions of the chunk's box (the w is 1: the game's piece shader reads in_POSITION0.www) and normal as four signed bytes
  /// (the last is unused): 12 bytes a vertex.
  /// </summary>
  internal static readonly VertexLayout Compact = new(
    new VertexField(VertexAttribute.Position, VertexAttributeFormat.UNorm16, 4),
    new VertexField(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4));

  /// <summary>Whether positions are stored as fractions of a box, so that the chunk needs the matrix of its <see cref="QuantBox"/> to be drawn.</summary>
  internal bool Quantised => Find(VertexAttribute.Position)?.Format == VertexAttributeFormat.UNorm16;
}

/// <summary>
/// The box a chunk's positions are stored in (16 bit fractions of it), as an object-to-world matrix: the chunk is drawn with
/// <see cref="ToWorld"/>, which carries the unit box the mesh holds to this box in the world.
/// </summary>
internal readonly struct QuantBox(float minX, float minY, float minZ, float sizeX, float sizeY, float sizeZ)
{
  /// <summary>A box side is at least this (metres), so that the matrix has an inverse.</summary>
  internal const float MinSide = 0.01f;

  /// <summary>
  /// No side is shorter than the longest divided by this. Normals are stored multiplied by the box's size and the shader divides them by it again; a byte
  /// of error in the thinnest side's share is made larger by (longest / thinnest) on the way out. At 2 the worst case is 0.78 degrees, below the one degree
  /// asked; a thinner box costs nothing to widen, as its fractions only get finer.
  /// </summary>
  internal const float SkewLimit = 2f;

  internal readonly float MinX = minX, MinY = minY, MinZ = minZ, SizeX = sizeX, SizeY = sizeY, SizeZ = sizeZ;

  /// <summary>The box of a layout that stores its numbers as they are: the matrix is just the zone's place.</summary>
  internal static readonly QuantBox Unit = new(0f, 0f, 0f, 1f, 1f, 1f);

  internal bool IsUnit => MinX == 0f && MinY == 0f && MinZ == 0f && SizeX == 1f && SizeY == 1f && SizeZ == 1f;

  /// <summary>
  /// The box that stores the vertices of this tight box: the tight box itself, widened (about its centre) where a side is under MinSide or under the
  /// longest side divided by <see cref="SkewLimit"/>.
  /// </summary>
  internal static QuantBox Of(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
  {
    float dx = Math.Max(maxX - minX, 0f), dy = Math.Max(maxY - minY, 0f), dz = Math.Max(maxZ - minZ, 0f);
    float floor = Math.Max(MinSide, Math.Max(dx, Math.Max(dy, dz)) / SkewLimit);
    float sx = Math.Max(dx, floor), sy = Math.Max(dy, floor), sz = Math.Max(dz, floor);
    return new QuantBox((minX + maxX) * 0.5f - sx * 0.5f, (minY + maxY) * 0.5f - sy * 0.5f, (minZ + maxZ) * 0.5f - sz * 0.5f, sx, sy, sz);
  }

  /// <summary>
  /// The matrix a chunk is drawn with: the unit box to this box, with the zone's origin (<paramref name="ox"/>, <paramref name="oz"/>) added, as the chunk's
  /// vertices are relative to the zone.
  /// </summary>
  internal Matrix4x4 ToWorld(float ox, float oz)
  {
    var m = Matrix4x4.identity;
    m.m00 = SizeX;
    m.m11 = SizeY;
    m.m22 = SizeZ;
    m.m03 = ox + MinX;
    m.m13 = MinY;
    m.m23 = oz + MinZ;
    return m;
  }

  /// <summary>A position (relative to the zone) as a 16 bit fraction of the box on one axis.</summary>
  internal static ushort Fraction(float value, float min, float size)
  {
    float f = (value - min) / size * 65535f + 0.5f;
    return f <= 0f ? (ushort)0 : f >= 65535f ? ushort.MaxValue : (ushort)f;
  }

  /// <summary>
  /// A unit normal as it must be stored for a chunk drawn with <see cref="ToWorld"/>: Unity moves a normal by the inverse transpose of the matrix, which for this
  /// matrix divides each component by the box's size, so the stored one is multiplied by it (and made unit length again). Unchanged for the unit box.
  /// </summary>
  internal Vector3 Compensate(Vector3 n)
  {
    if (IsUnit)
      return n;
    float x = n.x * SizeX, y = n.y * SizeY, z = n.z * SizeZ;
    float len = (float)Math.Sqrt(x * x + y * y + z * z);
    return len > 1e-20f ? new Vector3(x / len, y / len, z / len) : new Vector3(0f, 1f, 0f);
  }
}

/// <summary>The numbers of a chunk's vertices, as plain arrays (the first <c>count</c> entries of each). Positions are needed; the rest only where the layout names the attribute.</summary>
internal readonly struct VertexSource(Vector3[] positions, Vector3[]? normals = null, Vector2[]? uv0 = null, Vector4[]? tangents = null)
{
  internal readonly Vector3[] Positions = positions;
  internal readonly Vector3[]? Normals = normals;
  internal readonly Vector2[]? Uv0 = uv0;
  internal readonly Vector4[]? Tangents = tangents;
}

/// <summary>Packs vertices and indices into the bytes of a layout. Plain loops over arrays, safe on a worker.</summary>
internal static unsafe class VertexPacker
{
  /// <summary>
  /// Writes <paramref name="count"/> vertices of <paramref name="src"/> in the layout into <paramref name="dst"/> (which must hold count * stride bytes).
  /// Supported: Position as Float32 x 3 or x 4, or as UNorm16 x 4 (fractions of <paramref name="box"/>, w = 1); Normal as Float32 x 3 or SNorm8 x 4 (both
  /// compensated for the box, see <see cref="QuantBox.Compensate"/>); TexCoord0 as Float32 x 2; Tangent as Float32 x 4. An attribute the layout names and the
  /// source does not carry, or a format not listed here, is an error: add the encoder here, with its test.
  /// </summary>
  internal static void Pack(VertexLayout layout, in VertexSource src, int count, in QuantBox box, Span<byte> dst)
  {
    if (dst.Length < (long)count * layout.Stride)
      throw new ArgumentException($"{dst.Length} bytes cannot hold {count} vertices of {layout.Stride} bytes");
    int stride = layout.Stride;
    fixed (byte* basePtr = dst)
    {
      foreach (var f in layout.Fields)
      {
        byte* at = basePtr + f.Offset;
        switch (f.Attribute, f.Format, f.Dimension)
        {
          case (VertexAttribute.Position, VertexAttributeFormat.Float32, 3 or 4):
            for (int v = 0; v < count; v++)
            {
              float* p = (float*)(at + (long)v * stride);
              var s = src.Positions[v];
              p[0] = s.x;
              p[1] = s.y;
              p[2] = s.z;
              if (f.Dimension == 4)
                p[3] = 1f;
            }
            break;
          case (VertexAttribute.Position, VertexAttributeFormat.UNorm16, 4):
            for (int v = 0; v < count; v++)
            {
              ushort* p = (ushort*)(at + (long)v * stride);
              var s = src.Positions[v];
              p[0] = QuantBox.Fraction(s.x, box.MinX, box.SizeX);
              p[1] = QuantBox.Fraction(s.y, box.MinY, box.SizeY);
              p[2] = QuantBox.Fraction(s.z, box.MinZ, box.SizeZ);
              p[3] = ushort.MaxValue;
            }
            break;
          case (VertexAttribute.Normal, VertexAttributeFormat.Float32, 3):
            var normals = src.Normals ?? throw new ArgumentException("the layout has normals, the source none");
            for (int v = 0; v < count; v++)
            {
              float* p = (float*)(at + (long)v * stride);
              var s = box.Compensate(normals[v]);
              p[0] = s.x;
              p[1] = s.y;
              p[2] = s.z;
            }
            break;
          case (VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4):
            var snormals = src.Normals ?? throw new ArgumentException("the layout has normals, the source none");
            for (int v = 0; v < count; v++)
            {
              sbyte* p = (sbyte*)(at + (long)v * stride);
              var s = box.Compensate(snormals[v]);
              p[0] = SNorm8(s.x);
              p[1] = SNorm8(s.y);
              p[2] = SNorm8(s.z);
              p[3] = 0;
            }
            break;
          case (VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2):
            var uv = src.Uv0 ?? throw new ArgumentException("the layout has uv0, the source none");
            for (int v = 0; v < count; v++)
            {
              float* p = (float*)(at + (long)v * stride);
              p[0] = uv[v].x;
              p[1] = uv[v].y;
            }
            break;
          case (VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4):
            var tangents = src.Tangents ?? throw new ArgumentException("the layout has tangents, the source none");
            for (int v = 0; v < count; v++)
            {
              float* p = (float*)(at + (long)v * stride);
              p[0] = tangents[v].x;
              p[1] = tangents[v].y;
              p[2] = tangents[v].z;
              p[3] = tangents[v].w;
            }
            break;
          default:
            throw new NotSupportedException($"vertex attribute {f.Attribute} as {f.Format} x {f.Dimension} is not packed");
        }
      }
    }
  }

  // a number in [-1, 1] as a signed byte, the way the GPU reads it back (value / 127)
  private static sbyte SNorm8(float value)
  {
    float f = value * 127f;
    return (sbyte)Math.Max(-127f, Math.Min(127f, f >= 0f ? f + 0.5f : f - 0.5f));
  }

  /// <summary>Packs with the unit box: for layouts that store their numbers as they are.</summary>
  internal static void Pack(VertexLayout layout, in VertexSource src, int count, Span<byte> dst) => Pack(layout, src, count, QuantBox.Unit, dst);

  /// <summary>
  /// Writes the first <paramref name="count"/> indices as 16 or 32 bit numbers. A 16 bit chunk cannot hold an index past 65,535: that is an error, not a wrap.
  /// </summary>
  internal static void PackIndices(int[] indices, int count, bool wide, Span<byte> dst)
  {
    if (dst.Length < (long)count * (wide ? 4 : 2))
      throw new ArgumentException($"{dst.Length} bytes cannot hold {count} indices");
    fixed (byte* basePtr = dst)
    {
      if (wide)
      {
        int* p = (int*)basePtr;
        for (int i = 0; i < count; i++)
          p[i] = indices[i];
      }
      else
      {
        ushort* p = (ushort*)basePtr;
        for (int i = 0; i < count; i++)
        {
          int index = indices[i];
          if ((uint)index > ushort.MaxValue)
            throw new ArgumentException($"index {index} does not fit 16 bits");
          p[i] = (ushort)index;
        }
      }
    }
  }
}

/// <summary>
/// Where the bytes of one chunk are written. <see cref="Open"/> and <see cref="Close"/> run on a worker (the engine's store makes its memory with MeshData
/// calls that are safe off the main thread); the store itself is made, applied and released on the main thread, and never released while a worker may still
/// be writing into it.
/// </summary>
internal abstract class ChunkStore
{
  /// <summary>The memory for the vertices (count * stride bytes) and the indices (16 or 32 bit).</summary>
  internal abstract void Open(VertexLayout layout, int vertexCount, int indexCount, bool wide, out Span<byte> vertices, out Span<byte> indices);

  /// <summary>The chunk is written; <paramref name="bounds"/> is the box of its positions as the mesh will read them (before the matrix it is drawn with).</summary>
  internal abstract void Close(Bounds bounds, int vertexCount, int indexCount);

  /// <summary>Main thread: lets go of the memory (if it was not handed on). Safe to call twice.</summary>
  internal abstract void Release();
}

/// <summary>A store in managed byte arrays: what the tests use, and a book's default.</summary>
internal class ByteStore : ChunkStore
{
  internal byte[] Vertices = [], Indices = [];
  internal VertexLayout? Layout;
  internal Bounds Bounds;
  internal int VertexCount, IndexCount;
  internal bool Wide, Closed, Released;

  internal override void Open(VertexLayout layout, int vertexCount, int indexCount, bool wide, out Span<byte> vertices, out Span<byte> indices)
  {
    Layout = layout;
    VertexCount = vertexCount;
    IndexCount = indexCount;
    Wide = wide;
    Vertices = new byte[vertexCount * layout.Stride];
    Indices = new byte[indexCount * (wide ? 4 : 2)];
    vertices = Vertices;
    indices = Indices;
  }

  internal override void Close(Bounds bounds, int vertexCount, int indexCount)
  {
    Bounds = bounds;
    Closed = true;
  }

  internal override void Release()
  {
    Released = true;
    Vertices = Indices = [];
  }
}

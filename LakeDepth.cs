// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;

namespace BetterContinents;

// THE DEPTH OF A LAKE'S WATER, DRAWN AND FELT (spec 3.4). Pure: no engine types.
//
// Drawn: the game's water shader reads the four depths of a tile (_depth[4], vanilla's order [0] minX,maxZ [1] maxX,maxZ [2] maxX,minZ [3] minX,minZ, each
// the ground's depth over 10 m, clamped to 0..1) and interpolates them over the tile's mesh by its uv, 0..0.9999 across the zone's 64.2 m of water
// (WATER-SHADER-FACTS.md sections 4 and 9). Felt: WaterVolume.Depth(point) interpolates the four values of the volume the point is in over that
// volume's collider (WaterVolume.cs:232-245), and the waves a boat feels are sized by it (GetWaterSurface -> CalcWave).
//
// A lake's tile has the depths of its four corners [Tarn: LakeBuilder.cs:224-229, Water.cs:50-58]. The water you swim in is a set of trigger boxes
// (LakeBuild), which Tarn gave one averaged depth each, so what was drawn and what was felt differed. Here a box lies in one tile, and its four values are
// the tile's own interpolated function at the box's corners: a bilinear function restricted to a rectangle and interpolated from its corner values is the
// same function, so the depth a box makes felt is the depth the shader draws at that place (to the 0.9999 of the uv, and float rounding).
internal static class LakeDepth
{
  /// <summary>Half the width of the zone water's mesh: 32.1 m (the mesh is scaled 32.1 over -1..1), and its uv at the far edge.</summary>
  public const float MeshHalf = 32.1f, UvSpan = 0.9999f;

  /// <summary>The shader's depth from metres: vanilla divides by 10 m and clamps; the world's Lake Depth Scale multiplies [Tarn: Water.cs:53-55].</summary>
  public static float Normalize(float metres, float scale) => Math.Max(0f, Math.Min(1f, metres * (scale / 10f)));

  public static float[] Normalize(float[] metres, float scale) =>
    [Normalize(metres[0], scale), Normalize(metres[1], scale), Normalize(metres[2], scale), Normalize(metres[3], scale)];

  /// <summary>The uv a point gets on a tile whose water is centred at the given coordinate (u along x, v along z): the mesh's uv.</summary>
  public static float Uv(float coordinate, float centre) => UvSpan * (coordinate - (centre - MeshHalf)) / (2f * MeshHalf);

  private static float Lerp(float a, float b, float t) => a + (b - a) * (t < 0f ? 0f : t > 1f ? 1f : t);

  /// <summary>The shader's interpolation of a tile's four normalised depths at a uv: lerp(lerp(d3, d2, u), lerp(d0, d1, u), v).</summary>
  public static float Drawn(float[] n, float u, float v) => Lerp(Lerp(n[3], n[2], u), Lerp(n[0], n[1], u), v);

  /// <summary>WaterVolume.Depth(point) for a volume whose collider spans minX..maxX, minZ..maxZ [WaterVolume.cs:232-245], not its m_oneDepth shortcut.</summary>
  public static float Felt(float[] n, float minX, float minZ, float maxX, float maxZ, float x, float z)
  {
    float sx = maxX - minX, sz = maxZ - minZ;
    float t = (x - (minX + maxX) / 2f + sx / 2f) / sx;
    float t2 = (z - (minZ + maxZ) / 2f + sz / 2f) / sz;
    float a = Lerp(n[3], n[2], t);
    float b = Lerp(n[0], n[1], t);
    return Lerp(a, b, t2);
  }

  /// <summary>The four normalised depths a box of a tile gets: the tile's (drawn) depth at the box's corners, in vanilla's order. tileNorm: the tile's own four;
  /// centreX, centreZ: the tile's centre; the box's rectangle in world metres.</summary>
  public static float[] BoxCorners(float[] tileNorm, float centreX, float centreZ, float minX, float minZ, float maxX, float maxZ)
  {
    float u0 = Uv(minX, centreX), u1 = Uv(maxX, centreX), v0 = Uv(minZ, centreZ), v1 = Uv(maxZ, centreZ);
    // Outside 0..1 (a box's cells reach up to 2 m past the tile's border) the function goes on in the same line, and a depth is never beyond 0..1.
    return
    [
      Clamp01(Line(tileNorm, u0, v1)),
      Clamp01(Line(tileNorm, u1, v1)),
      Clamp01(Line(tileNorm, u1, v0)),
      Clamp01(Line(tileNorm, u0, v0)),
    ];
  }

  // The bilinear function through the tile's corners, extended past 0..1 (Drawn clamps t, which the box's corners must not).
  private static float Line(float[] n, float u, float v)
  {
    float a = n[3] + (n[2] - n[3]) * u;
    float b = n[0] + (n[1] - n[0]) * u;
    return a + (b - a) * v;
  }

  private static float Clamp01(float f) => f < 0f ? 0f : f > 1f ? 1f : f;
}

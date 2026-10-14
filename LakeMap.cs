// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using UnityEngine;

namespace BetterContinents;

// LAKES ON THE MAP [Tarn: MapLakes.cs]. Vanilla draws water where the minimap's height texture is under the sea (30 m): Minimap.GenerateWorldMap fills m_heightTexture with the
// ground's height at every map pixel (and caches it, reloaded by TryLoadMinimapTextureData), and its forest mask (m_forestMaskTexture) draws no trees under 30 m. A lake 60 m
// up is land to it. So once the map exists, each lake's pixels (a flood from its point over the map's own heights, inside its box) get the height of a sea as deep as the lake is
// (the map's shading follows depth) and no forest. Drawing only: nothing in the world reads these textures.
//
// Changed from Tarn: the textures are read and written in place through their raw data (the whole of them as Color[] was 64 MB each at 2048 x 2048), only the pixels of each
// lake's window; the map's pixel size and size are read when painting (Better Continents scales the minimap for a big world and Compact Maps makes it smaller); a flood
// per lake on a bool window; and when the world's lakes change after they were painted, the map is made again (the paint cannot be taken off), by the game's own call.
internal static class LakeMapPaint
{
  /// <summary>Paints one lake into a map of size x size pixels of pixelSize metres (pixel (row i, column j) is centred at x = (j - size/2) x px + px/2, z likewise with i).
  /// get/set: the height of the pixel k = i x size + j; clear: the forest mask of it. The flood starts at the lowest pixel within 2 of the lake's point (else the lowest of the lake's window), inside its box (plus 2
  /// pixels), over pixels under the lake's level and not under the sea; each gets the height of a sea as deep as the lake there (at least 1 m) and no forest. Returns the
  /// pixels painted (0: the lake is smaller than a pixel).</summary>
  public static int Paint(Lake lake, int size, float pixelSize, float sea, Func<int, float> get, Action<int, float> set, Action<int> clear)
  {
    int half = size / 2;
    int Col(float w) => (int)Math.Floor((w - pixelSize / 2f) / pixelSize + half + 0.5f);
    int j0 = Math.Max(0, Col(lake.MinX) - 2), j1 = Math.Min(size - 1, Col(lake.MaxX) + 2);
    int i0 = Math.Max(0, Col(lake.MinZ) - 2), i1 = Math.Min(size - 1, Col(lake.MaxZ) + 2);
    if (j1 < j0 || i1 < i0)
      return 0;
    int sj = Math.Min(Math.Max(Col(lake.X), j0), j1), si = Math.Min(Math.Max(Col(lake.Z), i0), i1);
    // the point may sit a pixel off the low ground at this coarse scale
    float best = float.MaxValue;
    for (int di = -2; di <= 2; di++)
      for (int dj = -2; dj <= 2; dj++)
      {
        int i = si + di, j = sj + dj;
        if (i < i0 || i > i1 || j < j0 || j > j1)
          continue;
        float v = get(i * size + j);
        if (v < best)
        {
          best = v;
          sj = j;
          si = i;
        }
      }
    if (!(best < lake.Level))
    {
      // Nothing under the level within two pixels of the point (it may stand at the rim of a pit the pixels sample badly): the lowest pixel of the lake's own window.
      // Changed from Tarn, whose point is the deepest one.
      for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
        {
          float v = get(i * size + j);
          if (v < best)
          {
            best = v;
            sj = j;
            si = i;
          }
        }
      if (!(best < lake.Level))
        return 0;
    }
    int w = j1 - j0 + 1, h = i1 - i0 + 1;
    var seen = new bool[w * h];
    var stack = new Stack<int>();
    stack.Push(si * size + sj);
    seen[(si - i0) * w + (sj - j0)] = true;
    int painted = 0;
    while (stack.Count > 0)
    {
      int k = stack.Pop();
      int i = k / size, j = k % size;
      float depth = Math.Max(1f, lake.Level - get(k));
      set(k, sea - depth);
      clear(k);
      painted++;
      Visit(i + 1, j);
      Visit(i - 1, j);
      Visit(i, j + 1);
      Visit(i, j - 1);

      void Visit(int a, int b)
      {
        if (a < i0 || a > i1 || b < j0 || b > j1)
          return;
        int cell = (a - i0) * w + (b - j0);
        if (seen[cell])
          return;
        float v = get(a * size + b);
        if (!(v < lake.Level) || v < sea)
          return;
        seen[cell] = true;
        stack.Push(a * size + b);
      }
    }
    return painted;
  }

  // ---- half floats: the map's height texture is RHalf ------------------------------------------------------------------------------

  /// <summary>IEEE 754 binary16 to float.</summary>
  public static float HalfToFloat(ushort half)
  {
    int sign = (half >> 15) & 1, exponent = (half >> 10) & 0x1F, mantissa = half & 0x3FF;
    float value;
    if (exponent == 0)
      value = mantissa * (1f / 16777216f);                                         // 2^-14 x mantissa / 1024, a subnormal
    else if (exponent == 31)
      value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
    else
      value = (1f + mantissa / 1024f) * (float)Math.Pow(2, exponent - 15);
    return sign == 1 ? -value : value;
  }

  /// <summary>Float to IEEE 754 binary16, rounded to the nearest (ties to even); too large becomes infinity.</summary>
  public static ushort FloatToHalf(float value)
  {
    if (float.IsNaN(value))
      return 0x7E00;
    int sign = value < 0f || (value == 0f && 1f / value < 0f) ? 0x8000 : 0;
    double a = Math.Abs((double)value);
    if (double.IsInfinity(a) || a >= 65520.0)
      return (ushort)(sign | 0x7C00);
    if (a < 6.103515625e-05)
    {
      // subnormal: a multiple of 2^-24
      double scaled = a * 16777216.0;
      long m = (long)Math.Round(scaled, MidpointRounding.ToEven);
      return (ushort)(sign | (int)m);
    }
    int exponent = (int)Math.Floor(Math.Log(a, 2));
    // guard the rounding of Log for exact powers of two
    if (Math.Pow(2, exponent + 1) <= a) exponent++;
    else if (Math.Pow(2, exponent) > a) exponent--;
    double fraction = a / Math.Pow(2, exponent) - 1.0;
    long mant = (long)Math.Round(fraction * 1024.0, MidpointRounding.ToEven);
    if (mant == 1024)
    {
      mant = 0;
      exponent++;
    }
    if (exponent + 15 >= 31)
      return (ushort)(sign | 0x7C00);
    return (ushort)(sign | ((exponent + 15) << 10) | (int)mant);
  }
}

internal static class LakeMap
{
  // The map was made (or loaded) and the lakes are to be painted onto it; the world's lakes were painted before and the map is to be made again.
  private static bool dirty, painted, regenerate;

  /// <summary>Minimap.GenerateWorldMap postfix: the map was made again, without any lakes [Tarn: MapLakes.cs:90-94].</summary>
  internal static void GenerateWorldMapPostfix() => dirty = true;

  /// <summary>Minimap.TryLoadMinimapTextureData postfix: the map was loaded from its files, without any lakes [Tarn: MapLakes.cs:96-100].</summary>
  internal static void TryLoadMinimapTextureDataPostfix(bool __result)
  {
    if (__result)
      dirty = true;
  }

  /// <summary>The world's lakes changed (a command, a push, the world loaded): paint them, or if the map had them painted, make the map again.</summary>
  internal static void LakesChanged()
  {
    if (painted)
      regenerate = true;
    else
      dirty = true;
  }

  /// <summary>The world was left.</summary>
  internal static void Reset()
  {
    dirty = painted = regenerate = false;
  }

  /// <summary>Whether the map has the lakes painted on it now (bc_lake info).</summary>
  internal static bool Painted => painted;

  /// <summary>Every frame (LakeStreamer.Tick), on a client.</summary>
  internal static void Tick()
  {
    if (!dirty && !regenerate)
      return;
    var mm = Minimap.instance;
    // The game makes the map (or reads it) in Minimap.Update, after the world has loaded: before that its textures are empty.
    if (mm == null || !mm.m_hasGenerated)
      return;
    try
    {
      if (regenerate)
      {
        regenerate = false;
        painted = false;
        if (WorldGenerator.instance != null)
        {
          BetterContinents.Log("Lakes: the world's lakes changed: the map is made again.");
          // The postfix on it marks the new map to be painted.
          mm.ForceRegen();
        }
        return;
      }
      dirty = false;
      var settings = BetterContinents.Settings;
      if (!LakeWater.Wanted(settings) || !LakeSettings.PaintOnMap)
        return;
      Paint(mm, settings.Lakes!);
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Lakes: drawing the lakes on the map failed ({e.GetType().Name}: {e.Message})");
    }
  }

  private static void Paint(Minimap mm, LakeSet set)
  {
    var height = mm.m_heightTexture;
    var mask = mm.m_forestMaskTexture;
    if (height == null || mask == null)
      return;
    int size = mm.m_textureSize;
    float pixel = mm.m_pixelSize;
    if (height.format != TextureFormat.RHalf)
    {
      BetterContinents.LogWarning($"Lakes: the map's height texture is {height.format}, not RHalf: lakes stay off the map");
      return;
    }
    var h = height.GetRawTextureData<ushort>();
    var m = mask.GetRawTextureData<byte>();
    if (h.Length != size * size || m.Length % (size * size) != 0 || m.Length == 0)
    {
      BetterContinents.LogWarning("Lakes: the map's textures are not the size the map says: lakes stay off the map");
      return;
    }
    int bytes = m.Length / (size * size);
    float sea = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
    var watch = Stopwatch.StartNew();
    int pixels = 0, shown = 0;
    foreach (var lake in set.Lakes)
    {
      int n = LakeMapPaint.Paint(lake, size, pixel, sea,
        k => LakeMapPaint.HalfToFloat(h[k]),
        (k, v) => h[k] = LakeMapPaint.FloatToHalf(v),
        k =>
        {
          for (int b = 0; b < bytes; b++)
            m[k * bytes + b] = 0;
        });
      if (n > 0)
      {
        pixels += n;
        shown++;
      }
    }
    height.Apply(false);
    mask.Apply(false);
    painted = shown > 0;
    BetterContinents.Log($"Lakes: map: {shown} of {set.Lakes.Count} lake(s) drawn as water, {pixels} pixel(s) of {pixel:0.#} m ({watch.ElapsedMilliseconds} ms)");
  }
}

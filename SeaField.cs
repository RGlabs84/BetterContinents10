// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEngine;

namespace BetterContinents;

// THE SEA FIELD: a grid over the whole world that says, per texel, how far it lies inside the world's Deep North (the calm sea) and how far
// inside or outside the world's hot sea (the Ashlands' red). Both are worked out from the world's own rules, never saved: the field is built
// from WorldGenerator.IsDeepnorth and WorldGenerator.GetAshlandsOceanGradient > 0 at texel centres, with every patch on them (Better Continents'
// maps and layout, Expand World Size, Expand World Data). Every machine that has water builds it on workers; a dedicated server only the calm
// half (no hot channel, no texture, no depth grid).
//
// THE CONTRACT WITH THE SHADER (unity/Sea/SeaField.hlsl is this file's twin; the numbers below are exact):
//   Grid      N x N texels (N <= 4096) of Spacing metres: the square [OriginX, OriginX + N * Spacing] x [OriginZ, OriginZ + N * Spacing],
//             OriginX = OriginZ = -Radius, Radius = the world's edge (World Size + Edge Size; at most 65,536 m). Spacing = max(8, 2 * Radius / 4096)
//             as a float; N = ceil(2 * Radius / Spacing), at most 4096. Texel (i, j) (i along +x, j along +z) has its CENTRE at
//             (OriginX + (i + 0.5) * Spacing, OriginZ + (j + 0.5) * Spacing).
//   Texture   RG16 (TextureFormat.RG16), N x N, no mip maps, point filter, clamp, linear. Bytes (SeaField.Rg16): pixel (i, j) at byte 2 * (j * N + i):
//             first R, then G. Row j = 0 is the first row of the data, which is v = 0 and Load(int3(i, 0, 0)) on every graphics API (Unity's
//             convention). The globals: _BCSeaField (the texture) and _BCSeaRect = (OriginX, OriginZ, InvSpacing, N), InvSpacing = 1f / Spacing as
//             a float, the very value the C# side multiplies by.
//   G (calm)  whole metres 0..255: how far the texel's centre lies inside the world's Deep North; 0 outside it. Read it as round(G * 255.0).
//   R (hot)   signed whole metres, positive inside the world's hot sea: stored as 128 + clamp(metres, -127, 127). Read it as round(R * 255.0) - 128.
//             Inside, the distance from the texel's centre to the nearest centre of a texel outside, less half a texel; outside, the same
//             the other way round and negative: across a straight border the two texels next to it read +Spacing / 2 and -Spacing / 2.
//   Calm      the same way for G: distance to the nearest centre outside the Deep North, less half a texel, in metres, rounded and clamped.
//   Waves(x, z) = 1 - saturate(calm * 0.005), where calm is bilinear over the four nearest texel centres by exactly this arithmetic, in floats:
//                   u = (x - OriginX) * InvSpacing - 0.5;   v = (z - OriginZ) * InvSpacing - 0.5;   u = clamp(u, -1, N);  v = clamp(v, -1, N);
//                   iu = floor(u);  iv = floor(v);  tx = u - iu;  tz = v - iv;
//                   x0 = clamp(iu, 0, N - 1);  x1 = clamp(iu + 1, 0, N - 1);  z0, z1 likewise;           (outside the grid the border texels apply)
//                   a = c00 + (c10 - c00) * tx;   b = c01 + (c11 - c01) * tx;   calm = a + (b - a) * tz;
//                 with cXZ = the G value (as metres) of texel (xX, zZ). The shader's wave block takes this as W.
//   Red mask  m(x, z) = saturate(0.5 + hot / 25), hot = the hardware bilinear read of R (metres, as above) at (x, z): the game's 25 m edge,
//             centred on the border. SeaField.Mask is its C# counterpart (explicit bilinear).
//   Depth     the far sea's depth (Soft Far Sea): an R8 texture, M x M, bilinear, clamp, one texel per zone corner (every 64 m): texel (i, j) has its
//             centre at (DepthOriginX + 64 * i, DepthOriginZ + 64 * j) and holds clamp01(max(0, 30 - ground height) / 10), the zone water's own
//             rule (Heightmap.UpdateCornerDepths, WaterVolume.DetectWaterDepth), rounded to a byte. Globals _BCSeaDepth and
//             _BCSeaDepthRect = (DepthOriginX, DepthOriginZ, 1/64, M), and _BCSeaSoft = 1 when Soft Far Sea is on and the grid is there.
//
// The distances are exact: an integer Euclidean distance transform (Meijster, Roerdink and Hesselink, 2000) of the two masks, in whole squared
// texels, so every machine gets the same bytes whatever its core count, and a parallel build equals a single-worker one.

/// <summary>What a sea field is built from. The defaults are the game's functions as they stand with every patch on them.</summary>
internal sealed class SeaFieldInput
{
  /// <summary>The world's edge in metres from the centre (World Size + Edge Size).</summary>
  public float Radius = 10500f;
  public Func<float, float, bool> Deep = WorldGenerator.IsDeepnorth;
  public Func<float, float, bool> Hot = (x, z) => WorldGenerator.GetAshlandsOceanGradient(x, z) > 0f;
  /// <summary>The game's own circles, as the shader draws them (null: whether the world's sea is the game's is not worked out).</summary>
  public Func<float, float, bool>? GameDeep, GameHot;
  /// <summary>The ground's height at a place, for the depth grid (null: no depth grid).</summary>
  public Func<float, float, float>? Height;
  /// <summary>Threads to use; 0 is every core but one.</summary>
  public int Workers;
}

internal sealed class SeaField
{
  /// <summary>The field of the world that is loaded, or null before it is ready (and in a world whose sea is not a candidate). Swapped whole.</summary>
  public static volatile SeaField? Current;

  public const int MaxSize = 4096;
  public const float MinSpacing = 8f;
  public const float MaxRadius = 65536f;

  // The grid a radius has: N and Spacing.
  internal static void GridFor(float radius, out int size, out float spacing)
  {
    double r = ClampRadius(radius);
    float s = (float)Math.Max(MinSpacing, 2.0 * r / MaxSize);
    size = (int)Math.Min(MaxSize, Math.Ceiling(2.0 * r / s));
    spacing = s;
  }

  internal static float ClampRadius(float radius) => float.IsNaN(radius) ? 16f : Math.Min(Math.Max(radius, 16f), MaxRadius);

  public int Size;
  public float Spacing, InvSpacing, OriginX, OriginZ, Radius;
  /// <summary>G: the distance into the Deep North in metres, per texel (row after row, along +x).</summary>
  public byte[] Calm = [];
  /// <summary>R: 128 + the signed distance to the hot sea's border; null on a machine that draws nothing.</summary>
  public byte[]? Hot;
  /// <summary>The Deep North is the game's own at every texel centre (so the felt waves are the game's: the same rule on every machine).</summary>
  public bool CalmSameAsGame;
  /// <summary>The hot sea is the game's own at every texel centre (true when no hot channel was built: there is nothing to compare).</summary>
  public bool HotSameAsGame;
  /// <summary>Both: the world's sea is the game's, and nothing is swapped.</summary>
  public bool SameAsGame => CalmSameAsGame && HotSameAsGame;

  /// <summary>The far sea's depth grid (R8); null where none was built.</summary>
  public byte[]? Depth;
  public int DepthSize;
  public float DepthOriginX, DepthOriginZ;
  public const float DepthSpacing = 64f;

  // What the build found, for 'bc water' and the log.
  public double CalmShare, HotShare;
  public long BuildMs;
  public int Workers;

  // ---- reading ----------------------------------------------------------------------------------------------------

  // The four texels and weights of a bilinear read (the contract's arithmetic), over any channel.
  private void Corners(float x, float z, out int i00, out int i10, out int i01, out int i11, out float tx, out float tz)
  {
    int n = Size;
    float u = (x - OriginX) * InvSpacing - 0.5f;
    float v = (z - OriginZ) * InvSpacing - 0.5f;
    u = Mathf.Clamp(u, -1f, n);
    v = Mathf.Clamp(v, -1f, n);
    float iu = Mathf.Floor(u), iv = Mathf.Floor(v);
    tx = u - iu;
    tz = v - iv;
    int x0 = Mathf.Clamp((int)iu, 0, n - 1), x1 = Mathf.Clamp((int)iu + 1, 0, n - 1);
    int z0 = Mathf.Clamp((int)iv, 0, n - 1), z1 = Mathf.Clamp((int)iv + 1, 0, n - 1);
    i00 = z0 * n + x0;
    i10 = z0 * n + x1;
    i01 = z1 * n + x0;
    i11 = z1 * n + x1;
  }

  /// <summary>How far inside the Deep North a place is, in metres, between the texels' centres.</summary>
  public float CalmAt(float x, float z)
  {
    Corners(x, z, out int i00, out int i10, out int i01, out int i11, out float tx, out float tz);
    float c00 = Calm[i00], c10 = Calm[i10], c01 = Calm[i01], c11 = Calm[i11];
    float a = c00 + (c10 - c00) * tx;
    float b = c01 + (c11 - c01) * tx;
    return a + (b - a) * tz;
  }

  /// <summary>W, the world's wave strength at a place: 1 in the open sea and at the Deep North's border, 0 from 200 m inside it. The same
  /// arithmetic in the same order as BCSeaWaves in SeaField.hlsl.</summary>
  public float Waves(float x, float z) => 1f - Mathf.Clamp01(CalmAt(x, z) * 0.005f);

  /// <summary>The signed distance to the hot sea's border in metres (positive inside), between the texels' centres; 0 without a hot channel.</summary>
  public float HotAt(float x, float z)
  {
    if (Hot == null)
      return 0f;
    Corners(x, z, out int i00, out int i10, out int i01, out int i11, out float tx, out float tz);
    float c00 = Hot[i00] - 128, c10 = Hot[i10] - 128, c01 = Hot[i01] - 128, c11 = Hot[i11] - 128;
    float a = c00 + (c10 - c00) * tx;
    float b = c01 + (c11 - c01) * tx;
    return a + (b - a) * tz;
  }

  /// <summary>The red mask at a place (0 plain sea, 1 the Ashlands' red), with the game's 25 m edge centred on the border.</summary>
  public float Mask(float x, float z) => Mathf.Clamp01(0.5f + HotAt(x, z) / 25f);

  /// <summary>The field as the shader's RG16 texture holds it: R (hot) then G (calm) per texel, row after row. A server has no R (128).</summary>
  public byte[] Rg16()
  {
    var bytes = new byte[Size * Size * 2];
    var calm = Calm;
    var hot = Hot;
    for (int i = 0; i < calm.Length; i++)
    {
      bytes[2 * i] = hot != null ? hot[i] : (byte)128;
      bytes[2 * i + 1] = calm[i];
    }
    return bytes;
  }

  public override string ToString() =>
    string.Format(CultureInfo.InvariantCulture, "{0} x {0} texels of {1:0.###} m over +-{2:0} m{3}, built in {4} ms on {5} thread(s)",
      Size, Spacing, Radius, Hot != null ? "" : " (calm only)", BuildMs, Workers);

  // ---- building ---------------------------------------------------------------------------------------------------

  private const int RowBand = 16, ColumnBand = 64;
  // A column with no feature at all: far enough that any real distance beats it, and small enough that its square fits an int.
  private const int Far = 16384;
  private const byte DeepBit = 1, HotBit = 2;

  /// <summary>Builds the field of a world: the masks from its own functions on workers, then the exact distances. hot: also the hot channel
  /// (a client; a dedicated server builds the calm half only). Throws OperationCanceledException when stopped.</summary>
  public static SeaField Build(SeaFieldInput input, bool hot, CancellationToken stop)
  {
    var watch = Stopwatch.StartNew();
    GridFor(input.Radius, out int n, out float spacing);
    float radius = ClampRadius(input.Radius);
    int workers = input.Workers > 0 ? input.Workers : Math.Max(1, Environment.ProcessorCount - 1);
    var field = new SeaField
    {
      Size = n, Spacing = spacing, InvSpacing = 1f / spacing, OriginX = -radius, OriginZ = -radius, Radius = radius, Workers = workers,
    };
    long cells = (long)n * n;

    // The masks: bit 0 where the world's Deep North is, bit 1 where its hot sea is. Texel centres in doubles, then floats.
    var xs = new float[n];
    var zs = new float[n];
    for (int k = 0; k < n; k++)
    {
      xs[k] = (float)(field.OriginX + (k + 0.5) * (double)spacing);
      zs[k] = (float)(field.OriginZ + (k + 0.5) * (double)spacing);
    }
    var mask = new byte[cells];
    long deepCount = 0, hotCount = 0;
    int deepDiffers = 0, hotDiffers = 0;
    var deep = input.Deep;
    var hotFn = input.Hot;
    var gameDeep = input.GameDeep;
    var gameHot = hot ? input.GameHot : null;
    ForBands((n + RowBand - 1) / RowBand, workers, stop, band =>
    {
      int j1 = Math.Min(n, (band + 1) * RowBand);
      long deepHere = 0, hotHere = 0;
      bool compareDeep = gameDeep != null && Volatile.Read(ref deepDiffers) == 0;
      bool compareHot = gameHot != null && Volatile.Read(ref hotDiffers) == 0;
      for (int j = band * RowBand; j < j1; j++)
      {
        float z = zs[j];
        long row = (long)j * n;
        for (int i = 0; i < n; i++)
        {
          float x = xs[i];
          bool isDeep = deep(x, z);
          bool isHot = hot && hotFn(x, z);
          mask[row + i] = (byte)((isDeep ? DeepBit : 0) | (isHot ? HotBit : 0));
          if (isDeep) deepHere++;
          if (isHot) hotHere++;
          if (compareDeep && isDeep != gameDeep!(x, z))
          {
            compareDeep = false;
            Volatile.Write(ref deepDiffers, 1);
          }
          if (compareHot && isHot != gameHot!(x, z))
          {
            compareHot = false;
            Volatile.Write(ref hotDiffers, 1);
          }
        }
      }
      Interlocked.Add(ref deepCount, deepHere);
      Interlocked.Add(ref hotCount, hotHere);
    });
    field.CalmSameAsGame = gameDeep != null && deepDiffers == 0;
    field.HotSameAsGame = !hot || (gameHot != null && hotDiffers == 0);
    field.CalmShare = (double)deepCount / cells;
    field.HotShare = hot ? (double)hotCount / cells : 0.0;

    // The distances. One buffer of per-column distances serves every pass.
    var g = new ushort[cells];
    double sp = spacing;
    var calm = new byte[cells];
    field.Calm = calm;
    if (deepCount > 0)
    {
      // Inside the Deep North: the distance to the nearest texel outside it.
      Transform(mask, DeepBit, 0, n, g, workers, stop, (row, d2) =>
      {
        long at = (long)row * n;
        for (int i = 0; i < n; i++)
          if ((mask[at + i] & DeepBit) != 0)
            calm[at + i] = Metres(d2[i], sp, 255);
      });
    }
    if (hot)
    {
      var bytes = new byte[cells];
      field.Hot = bytes;
      if (hotCount == 0 || hotCount == cells)
      {
        // Nothing to measure from: all of it is outside the hot sea, or all of it inside.
        byte fill = hotCount == 0 ? (byte)1 : (byte)255;
        for (long k = 0; k < cells; k++)
          bytes[k] = fill;
      }
      else
      {
        // Inside: the distance to the nearest texel outside (positive).
        Transform(mask, HotBit, 0, n, g, workers, stop, (row, d2) =>
        {
          long at = (long)row * n;
          for (int i = 0; i < n; i++)
            if ((mask[at + i] & HotBit) != 0)
              bytes[at + i] = (byte)(128 + Metres(d2[i], sp, 127));
        });
        // Outside: the distance to the nearest texel inside (negative).
        Transform(mask, HotBit, HotBit, n, g, workers, stop, (row, d2) =>
        {
          long at = (long)row * n;
          for (int i = 0; i < n; i++)
            if ((mask[at + i] & HotBit) == 0)
              bytes[at + i] = (byte)(128 - Metres(d2[i], sp, 127));
        });
      }
    }
    if (hot && input.Height is { } height)
      BuildDepth(field, height, radius, workers, stop);
    watch.Stop();
    field.BuildMs = watch.ElapsedMilliseconds;
    return field;
  }

  // A squared distance in texels as the whole metres the texel's centre lies from the border: less half a texel, rounded, clamped. Doubles
  // throughout, and a square root is exact in them, so it is the same on every machine.
  private static byte Metres(int d2, double spacing, int cap) =>
    (byte)Math.Min(cap, Math.Max(0.0, Math.Floor((Math.Sqrt(d2) - 0.5) * spacing + 0.5)));

  // The depth grid: one texel per zone corner (the corners of the 64 m zones lie at 32 m plus a multiple of 64).
  private static void BuildDepth(SeaField field, Func<float, float, float> height, float radius, int workers, CancellationToken stop)
  {
    int first = (int)Math.Floor((-radius - 32.0) / DepthSpacing), last = (int)Math.Ceiling((radius - 32.0) / DepthSpacing);
    int m = last - first + 1;
    field.DepthSize = m;
    field.DepthOriginX = field.DepthOriginZ = (float)(32.0 + DepthSpacing * first);
    var depth = new byte[(long)m * m];
    float originX = field.DepthOriginX, originZ = field.DepthOriginZ;
    ForBands(m, workers, stop, j =>
    {
      float z = originZ + DepthSpacing * j;
      for (int i = 0; i < m; i++)
      {
        float h = height(originX + DepthSpacing * i, z);
        float d = Mathf.Clamp01(Mathf.Max(0f, 30f - h) / 10f);
        depth[(long)j * m + i] = (byte)Mathf.Clamp(Mathf.FloorToInt(d * 255f + 0.5f), 0, 255);
      }
    });
    field.Depth = depth;
  }

  // The squared distance, in texels, from every texel to the nearest texel that is a feature: one whose mask bits, masked by `bit`, equal
  // `featureBits`. Meijster, Roerdink and Hesselink's two scans, in integers (exact): per column the distance to the nearest feature in that
  // column, then per row the lower envelope of the parabolas. `consume` is given each row's squared distances. A mask with no feature at all
  // gives distances of at least Far squared.
  private static void Transform(byte[] mask, byte bit, byte featureBits, int n, ushort[] g, int workers, CancellationToken stop, Action<int, int[]> consume)
  {
    // 1. Per column (bands of columns, row after row, so that each worker reads a stripe of every row).
    ForBands((n + ColumnBand - 1) / ColumnBand, workers, stop, band =>
    {
      int x0 = band * ColumnBand, x1 = Math.Min(n, x0 + ColumnBand);
      for (int x = x0; x < x1; x++)
        g[x] = (mask[x] & bit) == featureBits ? (ushort)0 : (ushort)Far;
      for (int y = 1; y < n; y++)
      {
        long row = (long)y * n;
        for (int x = x0; x < x1; x++)
          g[row + x] = (mask[row + x] & bit) == featureBits ? (ushort)0 : (ushort)Math.Min(Far, g[row - n + x] + 1);
      }
      for (int y = n - 2; y >= 0; y--)
      {
        long row = (long)y * n;
        for (int x = x0; x < x1; x++)
        {
          int below = g[row + n + x] + 1;
          if (below < g[row + x])
            g[row + x] = (ushort)below;
        }
      }
    });
    // 2. Per row.
    ForBands((n + RowBand - 1) / RowBand, workers, stop, band =>
    {
      var scratch = Scratch;
      if (scratch == null || scratch[0].Length != n)
        Scratch = scratch = [new int[n], new int[n], new int[n], new int[n]];
      var s = scratch[0];
      var t = scratch[1];
      var gg = scratch[2];
      var d2 = scratch[3];
      int y1 = Math.Min(n, (band + 1) * RowBand);
      for (int y = band * RowBand; y < y1; y++)
      {
        long row = (long)y * n;
        for (int i = 0; i < n; i++)
        {
          int gi = g[row + i];
          gg[i] = gi * gi;
        }
        int q = 0;
        s[0] = 0;
        t[0] = 0;
        for (int u = 1; u < n; u++)
        {
          // f(x, i) = (x - i)^2 + g(i)^2; the parabola of a column the nearest feature is on is dropped while the new one is no worse at its start.
          while (q >= 0 && F(t[q], s[q], gg) > F(t[q], u, gg))
            q--;
          if (q < 0)
          {
            q = 0;
            s[0] = u;
          }
          else
          {
            // Where u's parabola first beats s[q]'s: the numerator is not negative here, since s[q] was no worse at t[q] >= 0.
            int i = s[q];
            int w = 1 + (u * u - i * i + gg[u] - gg[i]) / (2 * (u - i));
            if (w < n)
            {
              q++;
              s[q] = u;
              t[q] = w;
            }
          }
        }
        for (int u = n - 1; u >= 0; u--)
        {
          d2[u] = F(u, s[q], gg);
          if (u == t[q])
            q--;
        }
        consume(y, d2);
      }
    });
  }

  // One worker's rows of working space (the lower envelope's stack and breaks, the squared columns, a row of results).
  [ThreadStatic]
  private static int[][]? Scratch;

  private static int F(int x, int i, int[] gg) => (x - i) * (x - i) + gg[i];

  // Runs body for every band 0..bands-1 on `workers` threads (this one included), each taking the next band. A failure, or the stop, ends the
  // others and is thrown here. The helpers run below normal priority: they must not take the game's frames.
  internal static void ForBands(int bands, int workers, CancellationToken stop, Action<int> body)
  {
    workers = Math.Max(1, Math.Min(workers, bands));
    int next = -1;
    Exception? failure = null;
    void Work()
    {
      try
      {
        for (int band; (band = Interlocked.Increment(ref next)) < bands && Volatile.Read(ref failure) == null;)
        {
          stop.ThrowIfCancellationRequested();
          body(band);
        }
      }
      catch (Exception e)
      {
        Interlocked.CompareExchange(ref failure, e, null);
      }
    }
    var helpers = new Thread[workers - 1];
    for (int h = 0; h < helpers.Length; h++)
    {
      helpers[h] = new Thread(Work) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "BC sea field" };
      helpers[h].Start();
    }
    Work();
    foreach (var helper in helpers)
      helper.Join();
    if (failure != null)
      ExceptionDispatchInfo.Capture(failure).Throw();
  }
}

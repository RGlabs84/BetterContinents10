// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace BetterContinents;

// THE FLOOD OF A LAKE [Tarn: LakeBuilder.cs, phases 0 to 3], as a pure function: no engine types, so it runs on a worker and the offline suite
// (tools/lake-tests) can check it against a brute-force reference on made-up ground.
//
// 1. SAMPLE the ground on a 4 m grid over the lake's box and 12 m more, from a height function (the world generator's, never the loaded
//    terrain: the same answer on every machine, and callable off the main thread).
// 2. FLOOD from the lake's point: every cell below the level (strictly) that is joined to it by a side (4-connected) is water. If the point is
//    dry, the lowest cell within 3 cells (12 m) of it starts the flood; if that is dry too the lake fails. A flood that reaches the edge of the
//    grid only warns: the basin is not closed there, and the shore is cut at the box. The MASK is the wet cells grown by one cell (the 8
//    neighbours), so the surface reaches just under the shore and the terrain cuts it where the ground rises.
// 3. TILES: the 64 m squares of the zone grid (square k spans k x 64 +- 32) that the mask touches, a cell belonging to the tile of its centre.
// 4. BOXES [changed from Tarn]: the mask as rectangles of cells (a run along a row, extended down while the rows below hold exactly that run),
//    cut at the borders between tiles, so that a box lies in one tile and the water it makes can be given the tile's own depths (LakeDepth).
//    Tarn's boxes ran across tiles with one averaged depth each.
internal sealed class LakeShape
{
  /// <summary>The grid's step, the margin added to the lake's box, and a tile's size, in metres [Tarn: LakeBuilder.cs:25].</summary>
  public const float Step = 4f, Edge = 12f, TileSize = 64f;

  /// <summary>The water surface's mesh reaches this far from a tile's centre at most (the zone's: 31 x 31 quads over 64.2 m, WATER-SHADER-FACTS section 9, and a
  /// metre more); a tile whose cells within it are all in the mask is a FULL tile, which needs no mesh of its own (LakeBuild).</summary>
  public const float FullHalf = 33f;

  /// <summary>The cells the grid may hold before a lake is refused (a box about 23 km square): the sample grid is 4 bytes a cell, transient.</summary>
  public const long MaxCells = 1L << 25;

  /// <summary>One 64 m square of water: its place on the zone grid, the ground's depth under the water at its four corners in metres, in vanilla's order
  /// ([0] minX,maxZ [1] maxX,maxZ [2] maxX,minZ [3] minX,minZ; max(0, level - ground) at the corners of the 64 m square, the generator's ground), whether
  /// it is FULL, and its boxes (Boxes[BoxStart .. BoxStart + BoxCount]).</summary>
  public readonly record struct ShapeTile(int Tx, int Tz, bool Full, float D0, float D1, float D2, float D3, int BoxStart, int BoxCount)
  {
    public float CentreX => Tx * TileSize;
    public float CentreZ => Tz * TileSize;
    public float[] Metres() => [D0, D1, D2, D3];
  }

  /// <summary>One box of swimmable water: a rectangle of whole cells inside one tile, its bottom (the lowest ground in it, less 4 m; the top is the level
  /// plus 10 m) and the tile it belongs to.</summary>
  public readonly record struct ShapeBox(int Tx, int Tz, float MinX, float MinZ, float MaxX, float MaxZ, float Bottom);

  public Lake Lake { get; }
  /// <summary>The grid: cell (i, j) is centred at (X0 + i x Step, Z0 + j x Step); Nx x Nz cells. 0 when the lake failed.</summary>
  public float X0 { get; }
  public float Z0 { get; }
  public int Nx { get; }
  public int Nz { get; }
  /// <summary>Cells of water, before the mask grew them.</summary>
  public int WetCells { get; init; }
  /// <summary>The flood reached the grid's edge: the basin is not closed there.</summary>
  public bool TouchedEdge { get; init; }
  /// <summary>Why the lake has no water (its point is dry, its box is empty or too large); null when it has.</summary>
  public string? Failed { get; init; }
  /// <summary>What to tell about a lake that was made (the basin is not closed); null when nothing.</summary>
  public string? Warning { get; init; }
  public ShapeTile[] Tiles { get; init; } = [];
  public ShapeBox[] Boxes { get; init; } = [];
  // The mask, one bit a cell, row j first; and the wet cells when the caller asked to keep them (the tests).
  internal ulong[]? MaskBits { get; init; }
  internal ulong[]? WetBits { get; init; }

  public LakeShape(Lake lake, float x0, float z0, int nx, int nz)
  {
    Lake = lake;
    X0 = x0;
    Z0 = z0;
    Nx = nx;
    Nz = nz;
  }

  public float Level => Lake.Level;
  public bool Ok => Failed == null;
  public float AreaM2 => WetCells * Step * Step;

  public float Cx(int i) => X0 + i * Step;
  public float Cz(int j) => Z0 + j * Step;

  /// <summary>Whether the cell (i, j) is in the mask (the water's surface reaches it).</summary>
  public bool CellInMask(int i, int j) => MaskBits != null && i >= 0 && j >= 0 && i < Nx && j < Nz && Bit(MaskBits, j * Nx + i);

  public bool CellIsWet(int i, int j) => WetBits != null && i >= 0 && j >= 0 && i < Nx && j < Nz && Bit(WetBits, j * Nx + i);

  /// <summary>Whether a point is in the mask: the cell nearest to it is [Tarn: LakeBuilder.cs:161-165].</summary>
  public bool InMask(float x, float z)
  {
    double fi = Math.Round((x - X0) / Step), fj = Math.Round((z - Z0) / Step);
    return fi >= 0 && fj >= 0 && fi < Nx && fj < Nz && MaskBits != null && Bit(MaskBits, (int)fj * Nx + (int)fi);
  }

  /// <summary>The tile of a point on the zone grid.</summary>
  public static int TileOf(float coordinate) => (int)Math.Floor((coordinate + TileSize / 2f) / TileSize);

  /// <summary>A hash of everything the shape holds, for the tests: the same input must give the same value on every run and thread.</summary>
  public ulong Hash()
  {
    ulong h = 14695981039346656037UL;
    void Add(ulong v) { h = (h ^ v) * 1099511628211UL; }
    void AddF(float f) => Add((ulong)BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
    Add((ulong)Nx);
    Add((ulong)Nz);
    Add((ulong)WetCells);
    Add(TouchedEdge ? 1UL : 0UL);
    if (Failed != null)
      foreach (char c in Failed)
        Add(c);
    AddF(X0);
    AddF(Z0);
    if (MaskBits != null)
      foreach (var w in MaskBits)
        Add(w);
    foreach (var t in Tiles)
    {
      Add((ulong)(uint)t.Tx);
      Add((ulong)(uint)t.Tz);
      Add(t.Full ? 1UL : 0UL);
      AddF(t.D0);
      AddF(t.D1);
      AddF(t.D2);
      AddF(t.D3);
      Add((ulong)t.BoxStart);
      Add((ulong)t.BoxCount);
    }
    foreach (var b in Boxes)
    {
      Add((ulong)(uint)b.Tx);
      Add((ulong)(uint)b.Tz);
      AddF(b.MinX);
      AddF(b.MinZ);
      AddF(b.MaxX);
      AddF(b.MaxZ);
      AddF(b.Bottom);
    }
    return h;
  }

  internal static bool Bit(ulong[] bits, int k) => (bits[k >> 6] & (1UL << (k & 63))) != 0;
  internal static void SetBit(ulong[] bits, int k) => bits[k >> 6] |= 1UL << (k & 63);
}

internal static class LakeFlood
{
  /// <summary>The shape of a lake over the ground the height function gives. Pure; throws OperationCanceledException when the token is cancelled
  /// (checked every row, and every few thousand cells of the flood). keepWet: also keeps the wet cells in the shape (the tests).</summary>
  public static LakeShape Compute(Func<float, float, float> height, Lake lake, CancellationToken ct = default, bool keepWet = false)
  {
    // Tarn: _x0 = MinX - Edge, _nx = ceil((MaxX + Edge - _x0) / Step) + 1 (LakeBuilder.cs:46-48), in float.
    float x0 = lake.MinX - LakeShape.Edge, z0 = lake.MinZ - LakeShape.Edge;
    if (!IsFinite(lake.X) || !IsFinite(lake.Z) || !IsFinite(lake.Level) || !IsFinite(x0) || !IsFinite(z0) || !IsFinite(lake.MaxX) || !IsFinite(lake.MaxZ))
      return Failed(lake, x0, z0, "its numbers are not numbers");
    double nxd = Math.Ceiling((lake.MaxX + LakeShape.Edge - x0) / LakeShape.Step) + 1, nzd = Math.Ceiling((lake.MaxZ + LakeShape.Edge - z0) / LakeShape.Step) + 1;
    if (nxd < 2 || nzd < 2)
      return Failed(lake, x0, z0, "its box is empty");
    if (nxd * nzd > LakeShape.MaxCells)
      return Failed(lake, x0, z0, FormattableString.Invariant($"its box is too large to flood ({nxd:0} x {nzd:0} cells of {LakeShape.Step:0} m; the most is {LakeShape.MaxCells} cells)"));
    int nx = (int)nxd, nz = (int)nzd;
    float level = lake.Level;

    // 1. sample [Tarn: phase 0]
    var h = new float[nx * nz];
    for (int j = 0; j < nz; j++)
    {
      ct.ThrowIfCancellationRequested();
      float z = z0 + j * LakeShape.Step;
      for (int i = 0; i < nx; i++)
        h[j * nx + i] = height(x0 + i * LakeShape.Step, z);
    }

    // 2. the start cell, and the flood [Tarn: phase 1, LakeBuilder.cs:97-159]
    int si = RoundClamp((lake.X - x0) / LakeShape.Step, nx - 1);
    int sj = RoundClamp((lake.Z - z0) / LakeShape.Step, nz - 1);
    if (!(h[sj * nx + si] < level))
    {
      // the point may sit a cell off the deepest ground: take the lowest cell within 12 m
      float best = float.MaxValue;
      int bi = si, bj = sj;
      for (int dj = -3; dj <= 3; dj++)
        for (int di = -3; di <= 3; di++)
        {
          int i = si + di, j = sj + dj;
          if (i < 0 || j < 0 || i >= nx || j >= nz)
            continue;
          if (h[j * nx + i] < best)
          {
            best = h[j * nx + i];
            bi = i;
            bj = j;
          }
        }
      si = bi;
      sj = bj;
      if (!(best < level))
        return Failed(lake, x0, z0, FormattableString.Invariant($"its point ({lake.X:0}, {lake.Z:0}) is dry: ground {best:0.0} m, level {level:0.0} m"));
    }
    var wet = new bool[nx * nz];
    var stack = new int[1024];
    int top = 0, count = 0;
    bool touchedEdge = false;
    wet[sj * nx + si] = true;
    stack[top++] = sj * nx + si;
    while (top > 0)
    {
      int k = stack[--top];
      count++;
      if ((count & 0x3FFF) == 0)
        ct.ThrowIfCancellationRequested();
      int i = k % nx, j = k / nx;
      if (i == 0 || j == 0 || i == nx - 1 || j == nz - 1)
        touchedEdge = true;
      // a side neighbour that is below the level and not yet wet
      if (top + 4 > stack.Length)
        Array.Resize(ref stack, stack.Length * 2);
      if (i + 1 < nx && !wet[k + 1] && h[k + 1] < level) { wet[k + 1] = true; stack[top++] = k + 1; }
      if (i > 0 && !wet[k - 1] && h[k - 1] < level) { wet[k - 1] = true; stack[top++] = k - 1; }
      if (j + 1 < nz && !wet[k + nx] && h[k + nx] < level) { wet[k + nx] = true; stack[top++] = k + nx; }
      if (j > 0 && !wet[k - nx] && h[k - nx] < level) { wet[k - nx] = true; stack[top++] = k - nx; }
    }

    // The mask: the wet cells and their 8 neighbours [Tarn: LakeBuilder.cs:136-147].
    var mask = new bool[nx * nz];
    for (int j = 0; j < nz; j++)
    {
      ct.ThrowIfCancellationRequested();
      for (int i = 0; i < nx; i++)
      {
        if (!wet[j * nx + i])
          continue;
        for (int dj = -1; dj <= 1; dj++)
          for (int di = -1; di <= 1; di++)
          {
            int a = i + di, b = j + dj;
            if (a >= 0 && b >= 0 && a < nx && b < nz)
              mask[b * nx + a] = true;
          }
      }
    }

    // 3. the tiles: the tile of a cell is the tile of its centre [Tarn: LakeBuilder.cs:149-155]
    var tileCol = new int[nx];
    var tileRow = new int[nz];
    for (int i = 0; i < nx; i++)
      tileCol[i] = LakeShape.TileOf(x0 + i * LakeShape.Step);
    for (int j = 0; j < nz; j++)
      tileRow[j] = LakeShape.TileOf(z0 + j * LakeShape.Step);
    var tileSet = new HashSet<long>();
    for (int j = 0; j < nz; j++)
    {
      long previous = long.MinValue;
      for (int i = 0; i < nx; i++)
      {
        if (!mask[j * nx + i])
          continue;
        long key = Key(tileCol[i], tileRow[j]);
        if (key == previous)
          continue;
        tileSet.Add(key);
        previous = key;
      }
    }
    var tileKeys = new List<long>(tileSet);
    tileKeys.Sort((a, b) =>
    {
      // by tile row, then column (the order of a map)
      int c = Unkey(a).tz.CompareTo(Unkey(b).tz);
      return c != 0 ? c : Unkey(a).tx.CompareTo(Unkey(b).tx);
    });
    var tileIndex = new Dictionary<long, int>(tileKeys.Count);
    for (int t = 0; t < tileKeys.Count; t++)
      tileIndex[tileKeys[t]] = t;

    // 4. the boxes: Tarn's rectangles, cut at the borders between tiles [Tarn: LakeBuilder.cs:277-301]
    var rects = Rectangles(mask, nx, nz, tileCol, tileRow);
    var perTile = new int[tileKeys.Count];
    var boxTile = new int[rects.Count];
    for (int b = 0; b < rects.Count; b++)
    {
      var r = rects[b];
      boxTile[b] = tileIndex[Key(tileCol[r.I], tileRow[r.J])];
      perTile[boxTile[b]]++;
    }
    var start = new int[tileKeys.Count];
    for (int t = 1; t < start.Length; t++)
      start[t] = start[t - 1] + perTile[t - 1];
    var fill = (int[])start.Clone();
    var boxes = new LakeShape.ShapeBox[rects.Count];
    for (int b = 0; b < rects.Count; b++)
    {
      var r = rects[b];
      float bed = level;
      for (int j = r.J; j < r.J + r.H; j++)
        for (int i = r.I; i < r.I + r.W; i++)
          bed = Math.Min(bed, h[j * nx + i]);
      float bx0 = x0 + r.I * LakeShape.Step - LakeShape.Step / 2f, bx1 = x0 + (r.I + r.W - 1) * LakeShape.Step + LakeShape.Step / 2f;
      float bz0 = z0 + r.J * LakeShape.Step - LakeShape.Step / 2f, bz1 = z0 + (r.J + r.H - 1) * LakeShape.Step + LakeShape.Step / 2f;
      var (tx, tz) = Unkey(tileKeys[boxTile[b]]);
      // vanilla's box reaches 10 m above its surface too; the bottom is 4 m under the lowest ground [Tarn: LakeBuilder.cs:248]
      boxes[fill[boxTile[b]]++] = new LakeShape.ShapeBox(tx, tz, bx0, bz0, bx1, bz1, bed - 4f);
    }

    // The tiles with their corner depths and whether the mask covers all their water; the packed bits.
    var tiles = new LakeShape.ShapeTile[tileKeys.Count];
    float hs = LakeShape.TileSize / 2f;
    for (int t = 0; t < tiles.Length; t++)
    {
      var (tx, tz) = Unkey(tileKeys[t]);
      float cx = tx * LakeShape.TileSize, cz = tz * LakeShape.TileSize;
      tiles[t] = new LakeShape.ShapeTile(tx, tz, CoversTile(mask, nx, nz, x0, z0, cx, cz),
        Math.Max(0f, level - height(cx - hs, cz + hs)),
        Math.Max(0f, level - height(cx + hs, cz + hs)),
        Math.Max(0f, level - height(cx + hs, cz - hs)),
        Math.Max(0f, level - height(cx - hs, cz - hs)),
        start[t], perTile[t]);
    }
    return new LakeShape(lake, x0, z0, nx, nz)
    {
      WetCells = count,
      TouchedEdge = touchedEdge,
      Warning = touchedEdge ? "its water reaches the edge of its box - the basin is not closed there, so the shore is cut off at the box" : null,
      Tiles = tiles,
      Boxes = boxes,
      MaskBits = Pack(mask),
      WetBits = keepWet ? Pack(wet) : null,
    };
  }

  // A made-up shape for a lake that cannot be made.
  private static LakeShape Failed(Lake lake, float x0, float z0, string why) => new(lake, x0, z0, 0, 0) { Failed = why };

  private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

  // Mathf.RoundToInt then Mathf.Clamp(0, hi), without an int overflow on a point far outside the box.
  private static int RoundClamp(double v, int hi)
  {
    double r = Math.Round(v);
    return r <= 0 ? 0 : r >= hi ? hi : (int)r;
  }

  private static long Key(int tx, int tz) => ((long)tz << 32) | (uint)tx;

  private static (int tx, int tz) Unkey(long key) => ((int)(uint)(key & 0xFFFFFFFFL), (int)(key >> 32));

  // Whether every cell the tile's water mesh can reach (its centre +- FullHalf) is in the mask: then every triangle of the mesh is kept, and the
  // tile can use the game's mesh as it is.
  private static bool CoversTile(bool[] mask, int nx, int nz, float x0, float z0, float cx, float cz)
  {
    int i0 = (int)Math.Round((cx - LakeShape.FullHalf - x0) / LakeShape.Step), i1 = (int)Math.Round((cx + LakeShape.FullHalf - x0) / LakeShape.Step);
    int j0 = (int)Math.Round((cz - LakeShape.FullHalf - z0) / LakeShape.Step), j1 = (int)Math.Round((cz + LakeShape.FullHalf - z0) / LakeShape.Step);
    if (i0 < 0 || j0 < 0 || i1 >= nx || j1 >= nz)
      return false;
    for (int j = j0; j <= j1; j++)
      for (int i = i0; i <= i1; i++)
        if (!mask[j * nx + i])
          return false;
    return true;
  }

  private static ulong[] Pack(bool[] cells)
  {
    var bits = new ulong[(cells.Length + 63) >> 6];
    for (int k = 0; k < cells.Length; k++)
      if (cells[k])
        bits[k >> 6] |= 1UL << (k & 63);
    return bits;
  }

  /// <summary>A rectangle of whole cells: column I, row J, W wide, H tall.</summary>
  internal readonly record struct Rect(int I, int J, int W, int H);

  /// <summary>The mask as rectangles [Tarn: LakeBuilder.Rectangles, :277-301]: runs along each row, a run extended down while the rows below hold exactly
  /// it; not minimal. New: a run ends where the cells' tile changes (tileCol), and the rows below are only taken inside the run's tile row (tileRow),
  /// so that a rectangle lies in one tile.</summary>
  internal static List<Rect> Rectangles(bool[] mask, int nx, int nz, int[] tileCol, int[] tileRow)
  {
    var used = new bool[mask.Length];
    var list = new List<Rect>();
    for (int j = 0; j < nz; j++)
      for (int i = 0; i < nx; i++)
      {
        int k = j * nx + i;
        if (!mask[k] || used[k])
          continue;
        int i1 = i;
        while (i1 < nx && mask[j * nx + i1] && !used[j * nx + i1] && tileCol[i1] == tileCol[i])
          i1++;
        int j1 = j + 1;
        for (; j1 < nz && tileRow[j1] == tileRow[j]; j1++)
        {
          bool full = true;
          for (int a = i; a < i1 && full; a++)
            full = mask[j1 * nx + a] && !used[j1 * nx + a];
          if (!full)
            break;
        }
        for (int b = j; b < j1; b++)
          for (int a = i; a < i1; a++)
            used[b * nx + a] = true;
        list.Add(new Rect(i, j, i1 - i, j1 - j));
      }
    return list;
  }
}

// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace BetterContinents;

// HOW HIGH WATER CAN STAND in the hollow around a point before it spills out [Tarn: Spill.cs], and the lake that makes (bc_lake here, bc_lake at).
// Pure logic, no engine types, so the offline suite runs it on any height function.
//
// ENTRY POINT FOR bc_lake here / at (the data agent's word calls it):
//
//     LakeSpill.HereResult LakeSpill.Here(Func<float, float, float> height, float x, float z, float radius, float margin, float seaLevel, Func<string, bool> nameTaken, CancellationToken ct = default)
//     Task<LakeSpill.HereResult> LakeSpill.HereAsync(<the same>)          // the same on a worker (Task.Run): the flood takes a fraction of a second to seconds
//
// height: the ground's height at world x, z (WorldGenerator.instance.GetHeight; it may be called from any thread). seaLevel: ZoneSystem.m_waterLevel (30).
// nameTaken: whether a lake of the world already has that name (the new lake is named "lake1", "lake2" ... the first free). Defaults of Tarn: radius 120,
// margin 0.3. The result says whether a lake was made (Lake) and always has a sentence for the console (Message). Nothing is added to the world here: the
// word adds the lake to the world's LakeSet and says so.
internal static class LakeSpill
{
  public const float DefaultRadius = 120f, DefaultMargin = 0.3f;

  /// <summary>The cells of the spill search are 2 m [Tarn: Plugin.cs:311]; a radius outside these is refused.</summary>
  public const float Cell = 2f, MinRadius = 8f, MaxRadius = 2000f;

  /// <summary>The closing rule: the level goes down by this much at a time, this many times at most, until the builder's 4 m flood no longer reaches the edge of its grid.</summary>
  public const float CloseStep = 0.2f;
  public const int CloseTries = 10;

  /// <summary>A hollow whose level is within this of the sea drains (the sea level is at 30 m) [Tarn: Plugin.cs:314].</summary>
  public const float SeaGap = 0.2f;

  public sealed class Result
  {
    public float SpillHeight;     // where it would overflow
    public float Level;           // the water surface: SpillHeight - margin
    public float BedHeight;       // the lowest ground under the lake
    public float MinX, MinZ, MaxX, MaxZ;   // extent of the wet cells, world metres
    public int WetCells;
    public float Step;
    public float AreaM2 => WetCells * Step * Step;
    public float Depth => Level - BedHeight;
  }

  /// <param name="height">ground height (absolute world y) at world x, z</param>
  /// <returns>null when the flood never reaches the edge of the square (the radius gives a square of at least 3 x 3 cells, so that does not happen)</returns>
  public static Result? Compute(Func<float, float, float> height, float cx, float cz, float radius, float step, float margin, CancellationToken ct = default)
  {
    int half = (int)Math.Ceiling(radius / step);
    int n = 2 * half + 1;
    var h = new float[n * n];
    for (int j = 0; j < n; j++)
    {
      ct.ThrowIfCancellationRequested();
      for (int i = 0; i < n; i++)
        h[j * n + i] = height(cx + (i - half) * step, cz + (j - half) * step);
    }

    // the centre cell starts the flood
    int start = half * n + half;
    var cost = new float[n * n];
    for (int k = 0; k < cost.Length; k++)
      cost[k] = float.PositiveInfinity;
    var heap = new MinHeap();
    cost[start] = h[start];
    heap.Push(h[start], start);
    float spill = float.NaN;
    int popped = 0;
    while (heap.Count > 0)
    {
      heap.Pop(out float c, out int idx);
      if ((++popped & 0x3FFF) == 0)
        ct.ThrowIfCancellationRequested();
      if (c > cost[idx])
        continue;
      int x = idx % n, y = idx / n;
      if (x == 0 || y == 0 || x == n - 1 || y == n - 1)
      {
        spill = c;
        break;
      }
      Relax(x + 1, y);
      Relax(x - 1, y);
      Relax(x, y + 1);
      Relax(x, y - 1);

      void Relax(int ax, int ay)
      {
        int a = ay * n + ax;
        float nc = Math.Max(c, h[a]);
        if (nc < cost[a])
        {
          cost[a] = nc;
          heap.Push(nc, a);
        }
      }
    }
    if (float.IsNaN(spill))
      return null;

    var r = new Result
    {
      SpillHeight = spill, Level = spill - margin, Step = step, BedHeight = float.PositiveInfinity,
      MinX = float.PositiveInfinity, MinZ = float.PositiveInfinity,
      MaxX = float.NegativeInfinity, MaxZ = float.NegativeInfinity,
    };
    if (!(h[start] < r.Level))
    {
      // no water at the centre: WetCells 0
      r.BedHeight = h[start];
      return r;
    }

    var seen = new bool[n * n];
    var stack = new Stack<int>();
    stack.Push(start);
    seen[start] = true;
    while (stack.Count > 0)
    {
      int idx = stack.Pop();
      int x = idx % n, y = idx / n;
      r.WetCells++;
      r.BedHeight = Math.Min(r.BedHeight, h[idx]);
      float wx = cx + (x - half) * step, wz = cz + (y - half) * step;
      r.MinX = Math.Min(r.MinX, wx);
      r.MaxX = Math.Max(r.MaxX, wx);
      r.MinZ = Math.Min(r.MinZ, wz);
      r.MaxZ = Math.Max(r.MaxZ, wz);
      Visit(x + 1, y);
      Visit(x - 1, y);
      Visit(x, y + 1);
      Visit(x, y - 1);

      void Visit(int ax, int ay)
      {
        if (ax < 0 || ay < 0 || ax >= n || ay >= n)
          return;
        int a = ay * n + ax;
        if (seen[a] || !(h[a] < r.Level))
          return;
        seen[a] = true;
        stack.Push(a);
      }
    }
    return r;
  }

  // ---- bc_lake here / at ------------------------------------------------------------------------------------------------------

  /// <summary>What <see cref="Here"/> made of a point: a lake, or the reason there is none.</summary>
  public sealed class HereResult
  {
    /// <summary>The lake to add to the world; null when none was made.</summary>
    public Lake? Lake;
    /// <summary>A sentence for the console, whether a lake was made or not (the reason).</summary>
    public string Message = "";
    /// <summary>Where the hollow spills, the level of the lake, how far the closing rule lowered it (0 when it did not), the area of its water.</summary>
    public float SpillHeight, Level, Lowered, AreaM2, Depth;
    /// <summary>How many times the level was lowered by <see cref="CloseStep"/>.</summary>
    public int Drops;
    /// <summary>The shape the builder will make of the lake (as the closing rule last flooded it); null when no lake was made.</summary>
    public LakeShape? Shape;
    public bool Made => Lake != null;
  }

  /// <summary>Tarn's "here"/"at": find the spill of the hollow around (x, z), make a lake of it, and close it under the builder's own sampling.</summary>
  public static HereResult Here(Func<float, float, float> height, float x, float z, float radius, float margin, float seaLevel, Func<string, bool> nameTaken,
    CancellationToken ct = default)
  {
    if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z))
      return Refused("that point is not a place in the world");
    if (float.IsNaN(radius) || radius < MinRadius || radius > MaxRadius)
      return Refused(Inv($"the radius is {radius:0.##} m: it has to be between {MinRadius:0} and {MaxRadius:0} m"));
    if (float.IsNaN(margin) || float.IsInfinity(margin) || margin < 0f || margin > 50f)
      return Refused(Inv($"the margin is {margin:0.##} m: it has to be between 0 and 50 m"));
    var r = Compute(height, x, z, radius, Cell, margin, ct);
    if (r == null)
      return Refused("no spill found (unexpected)");
    if (r.Level <= seaLevel + SeaGap)
      return Refused(Inv($"this hollow drains to the sea: it spills at {r.SpillHeight:0.0} m, the sea is {seaLevel:0.0} m"), r.SpillHeight);
    if (r.WetCells == 0)
      return Refused(Inv($"you are not in a hollow: it would spill at {r.SpillHeight:0.0} m and the ground here is higher. Stand in the low part."), r.SpillHeight);
    int k = 1;
    while (nameTaken("lake" + k.ToString(CultureInfo.InvariantCulture)))
      k++;
    string name = "lake" + k.ToString(CultureInfo.InvariantCulture);
    float level = r.Level;
    Lake lake = new(name, x, z, level, r.MinX, r.MinZ, r.MaxX, r.MaxZ, r.Depth);

    // The closing rule (spec 2.12): the spill search floods 2 m cells and the builder 4 m ones, and a thin saddle between 4 m samples can let the builder's
    // water leak past its box. The level goes down 0.2 m at a time until the builder's own flood is closed.
    int drops = 0;
    var shape = LakeFlood.Compute(height, lake, ct);
    while (shape.Ok && shape.TouchedEdge && drops < CloseTries)
    {
      drops++;
      level = r.Level - drops * CloseStep;
      if (level <= seaLevel + SeaGap)
        break;
      lake = lake with { Level = level, Depth = Math.Max(0f, level - r.BedHeight) };
      shape = LakeFlood.Compute(height, lake, ct);
    }
    if (!shape.Ok)
      return Refused(Inv($"the lake would be dry on the builder's grid: {shape.Failed}"), r.SpillHeight);
    if (level <= seaLevel + SeaGap)
      return Refused(Inv($"the water leaks past the hollow's rim on the 4 m grid, and closing it needs a level within {SeaGap:0.0} m of the sea ({seaLevel:0.0} m): no lake made"), r.SpillHeight);
    if (shape.TouchedEdge)
      return Refused(Inv($"the water still reaches the edge of the hollow after the level was lowered {drops} times by {CloseStep:0.0} m ({drops * CloseStep:0.0} m in all): no lake made. Try a smaller margin or another place."), r.SpillHeight);

    var sb = new System.Text.StringBuilder();
    sb.Append(Inv($"{name}: level {level:0.00} m (spills at {r.SpillHeight:0.00}), deepest {lake.Depth:0.0} m, {r.AreaM2:0} m2 wet"));
    if (drops > 0)
      sb.Append(Inv($". The water leaked past the rim on the builder's 4 m grid, so the level was lowered {drops} x {CloseStep:0.0} m to {level:0.00} m (the spill search said {r.Level:0.00} m)"));
    return new HereResult { Lake = lake, Message = sb.ToString(), SpillHeight = r.SpillHeight, Level = level, Lowered = r.Level - level, Drops = drops, AreaM2 = r.AreaM2, Depth = lake.Depth, Shape = shape };
  }

  /// <summary>Here on a worker.</summary>
  public static Task<HereResult> HereAsync(Func<float, float, float> height, float x, float z, float radius, float margin, float seaLevel, Func<string, bool> nameTaken,
    CancellationToken ct = default) =>
    Task.Run(() => Here(height, x, z, radius, margin, seaLevel, nameTaken, ct), ct);

  private static HereResult Refused(string why, float spill = float.NaN) => new() { Message = why, SpillHeight = spill };

  private static string Inv(FormattableString text) => FormattableString.Invariant(text);

  // ---- the heap of the priority flood [Tarn: Spill.cs:95-132] -----------------------------------------------------------------

  private sealed class MinHeap
  {
    private readonly List<float> _k = [];
    private readonly List<int> _v = [];
    public int Count => _k.Count;

    public void Push(float k, int v)
    {
      _k.Add(k);
      _v.Add(v);
      int i = _k.Count - 1;
      while (i > 0)
      {
        int p = (i - 1) / 2;
        if (_k[p] <= _k[i])
          break;
        Swap(i, p);
        i = p;
      }
    }

    public void Pop(out float k, out int v)
    {
      k = _k[0];
      v = _v[0];
      int last = _k.Count - 1;
      _k[0] = _k[last];
      _v[0] = _v[last];
      _k.RemoveAt(last);
      _v.RemoveAt(last);
      int i = 0;
      while (true)
      {
        int l = 2 * i + 1, r = l + 1, m = i;
        if (l < _k.Count && _k[l] < _k[m])
          m = l;
        if (r < _k.Count && _k[r] < _k[m])
          m = r;
        if (m == i)
          break;
        Swap(i, m);
        i = m;
      }
    }

    private void Swap(int a, int b)
    {
      (_k[a], _k[b]) = (_k[b], _k[a]);
      (_v[a], _v[b]) = (_v[b], _v[a]);
    }
  }
}

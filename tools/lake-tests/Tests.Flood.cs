// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// The flood of a lake (LakeFlood.cs, Tarn's LakeBuilder phases 0 to 3) on made-up ground, against a brute-force reference written another way: fixtures with
// known answers, 5,000 seeded random fields, the same bytes whatever the thread, the limits and the words of the refusals (BUILD-SPEC-TARN.md 4.3 items 1, 2).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  // ---- made-up ground ----------------------------------------------------------------------------------------------------------

  internal sealed class Rng
  {
    private ulong s;
    public Rng(ulong seed) => s = seed * 0x9E3779B97F4A7C15UL + 0x1234567UL;
    public ulong Next()
    {
      s ^= s << 13;
      s ^= s >> 7;
      s ^= s << 17;
      return s;
    }
    public double Double() => (Next() >> 11) / (double)(1UL << 53);
    public double Range(double lo, double hi) => lo + (hi - lo) * Double();
    public int Int(int lo, int hi) => lo + (int)(Next() % (ulong)(hi - lo));
  }

  // Value noise: a hashed lattice, smoothly interpolated, in two octaves; heights in about 20 .. 100 m.
  internal sealed class Noise
  {
    private readonly ulong seed;
    private readonly float baseHeight, amplitude, wavelength;
    public Noise(ulong seed, float baseHeight, float amplitude, float wavelength)
    {
      this.seed = seed;
      this.baseHeight = baseHeight;
      this.amplitude = amplitude;
      this.wavelength = wavelength;
    }

    private float Lattice(int ix, int iz)
    {
      ulong h = seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)ix * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)iz * 0x165667B19E3779F9UL);
      h ^= h >> 33;
      h *= 0xFF51AFD7ED558CCDUL;
      h ^= h >> 33;
      return (float)((h >> 40) / (double)(1UL << 24)) * 2f - 1f;
    }

    private float Octave(float x, float z, float w)
    {
      float fx = x / w, fz = z / w;
      int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
      float tx = fx - ix, tz = fz - iz;
      tx = tx * tx * (3f - 2f * tx);
      tz = tz * tz * (3f - 2f * tz);
      float a = Lattice(ix, iz) + (Lattice(ix + 1, iz) - Lattice(ix, iz)) * tx;
      float b = Lattice(ix, iz + 1) + (Lattice(ix + 1, iz + 1) - Lattice(ix, iz + 1)) * tx;
      return a + (b - a) * tz;
    }

    public float Height(float x, float z) => baseHeight + amplitude * (0.8f * Octave(x, z, wavelength) + 0.2f * Octave(x + 1000f, z - 1000f, wavelength / 3.1f));
  }

  // A bowl: 40 m at the middle, 80 m at radius 100 and beyond; an optional channel (a notch) of the given height and half-width running out from the rim along +x.
  internal static Func<float, float, float> Bowl(float notchHeight = float.NaN, float notchHalfWidth = 6f, float notchZ = 0f) => (x, z) =>
  {
    float r = (float)Math.Sqrt(x * x + z * z);
    float h = 40f + 40f * Math.Min(1f, (r / 100f) * (r / 100f));
    if (!float.IsNaN(notchHeight) && x > 0f && Math.Abs(z - notchZ) < notchHalfWidth)
      h = Math.Min(h, notchHeight);
    return h;
  };

  internal static Lake MakeLake(float x, float z, float level, float half, string name = "t") => new(name, x, z, level, x - half, z - half, x + half, z + half, 10f);

  // ---- a brute-force reference, written another way ---------------------------------------------------------------------------

  internal sealed class RefFlood
  {
    public int Nx, Nz;
    public float X0, Z0;
    public float[,] H = new float[0, 0];
    public bool[,] Wet = new bool[0, 0], Mask = new bool[0, 0];
    public bool Fails, TouchedEdge;
    public int WetCount;
    public (int i, int j) Start;
  }

  // The grid is defined by Tarn's rule in words: the cells run from the box's minimum less 12 m, 4 m apart, to the first one at or past the maximum plus 12 m.
  internal static RefFlood Reference(Func<float, float, float> height, Lake l)
  {
    var r = new RefFlood { X0 = l.MinX - 12f, Z0 = l.MinZ - 12f };
    int nx = 1, nz = 1;
    while (r.X0 + 4f * (nx - 1) < l.MaxX + 12f) nx++;
    while (r.Z0 + 4f * (nz - 1) < l.MaxZ + 12f) nz++;
    r.Nx = nx;
    r.Nz = nz;
    r.H = new float[nx, nz];
    for (int i = 0; i < nx; i++)
      for (int j = 0; j < nz; j++)
        r.H[i, j] = height(r.X0 + 4f * i, r.Z0 + 4f * j);
    // the start: the cell nearest the point (ties to the even one), or if it is dry the lowest within 3 cells (the first in row-major order of dz, dx)
    int si = Math.Clamp((int)Math.Round((l.X - r.X0) / 4.0), 0, nx - 1), sj = Math.Clamp((int)Math.Round((l.Z - r.Z0) / 4.0), 0, nz - 1);
    if (!(r.H[si, sj] < l.Level))
    {
      float best = float.MaxValue;
      var at = (si, sj);
      for (int dj = -3; dj <= 3; dj++)
        for (int di = -3; di <= 3; di++)
        {
          int i = si + di, j = sj + dj;
          if (i >= 0 && j >= 0 && i < nx && j < nz && r.H[i, j] < best)
          {
            best = r.H[i, j];
            at = (i, j);
          }
        }
      si = at.Item1;
      sj = at.Item2;
      if (!(best < l.Level))
      {
        r.Fails = true;
        return r;
      }
    }
    r.Start = (si, sj);
    // a breadth-first flood over the four sides
    r.Wet = new bool[nx, nz];
    var queue = new Queue<(int, int)>();
    queue.Enqueue((si, sj));
    r.Wet[si, sj] = true;
    var sides = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
    while (queue.Count > 0)
    {
      var (i, j) = queue.Dequeue();
      r.WetCount++;
      if (i == 0 || j == 0 || i == nx - 1 || j == nz - 1)
        r.TouchedEdge = true;
      foreach (var (di, dj) in sides)
      {
        int a = i + di, b = j + dj;
        if (a >= 0 && b >= 0 && a < nx && b < nz && !r.Wet[a, b] && r.H[a, b] < l.Level)
        {
          r.Wet[a, b] = true;
          queue.Enqueue((a, b));
        }
      }
    }
    // the mask: a cell is in it when any of the nine cells around it (itself included) is wet
    r.Mask = new bool[nx, nz];
    for (int i = 0; i < nx; i++)
      for (int j = 0; j < nz; j++)
        for (int di = -1; di <= 1 && !r.Mask[i, j]; di++)
          for (int dj = -1; dj <= 1; dj++)
          {
            int a = i + di, b = j + dj;
            if (a >= 0 && b >= 0 && a < nx && b < nz && r.Wet[a, b])
            {
              r.Mask[i, j] = true;
              break;
            }
          }
    return r;
  }

  // The tile of a cell centre on the zone grid, in integers: square k spans k x 64 +- 32.
  internal static int TileIndex(float centre) => (int)Math.Floor((centre + 32.0) / 64.0);

  // Compares a shape with the reference; the problems are returned (empty = equal).
  internal static List<string> Compare(Func<float, float, float> height, Lake l, LakeShape s, RefFlood r)
  {
    var bad = new List<string>();
    if (r.Fails)
    {
      if (s.Ok)
        bad.Add("the shape was made, the reference says the point is dry");
      else if (!s.Failed.Contains("is dry"))
        bad.Add("the failure is not the dry point: " + s.Failed);
      return bad;
    }
    if (!s.Ok)
    {
      bad.Add("the lake failed: " + s.Failed);
      return bad;
    }
    if (s.Nx != r.Nx || s.Nz != r.Nz || s.X0 != r.X0 || s.Z0 != r.Z0)
      bad.Add($"grid {s.Nx}x{s.Nz} at {s.X0},{s.Z0} against {r.Nx}x{r.Nz} at {r.X0},{r.Z0}");
    if (s.WetCells != r.WetCount)
      bad.Add($"wet cells {s.WetCells} against {r.WetCount}");
    if (s.TouchedEdge != r.TouchedEdge)
      bad.Add($"touched edge {s.TouchedEdge} against {r.TouchedEdge}");
    if ((s.Warning != null) != r.TouchedEdge)
      bad.Add("the warning does not follow the edge");
    int wrongWet = 0, wrongMask = 0;
    for (int i = 0; i < r.Nx; i++)
      for (int j = 0; j < r.Nz; j++)
      {
        if (s.CellIsWet(i, j) != r.Wet[i, j])
          wrongWet++;
        if (s.CellInMask(i, j) != r.Mask[i, j])
          wrongMask++;
      }
    if (wrongWet > 0)
      bad.Add($"{wrongWet} cells differ in the wet set");
    if (wrongMask > 0)
      bad.Add($"{wrongMask} cells differ in the mask");

    // tiles: exactly those a mask cell's centre falls in
    var wantTiles = new SortedSet<(int tz, int tx)>();
    for (int i = 0; i < r.Nx; i++)
      for (int j = 0; j < r.Nz; j++)
        if (r.Mask[i, j])
          wantTiles.Add((TileIndex(r.Z0 + 4f * j), TileIndex(r.X0 + 4f * i)));
    var gotTiles = s.Tiles.Select(t => (t.Tz, t.Tx)).ToList();
    if (!gotTiles.SequenceEqual(wantTiles))
      bad.Add($"tiles differ: {gotTiles.Count} against {wantTiles.Count}");
    if (gotTiles.Distinct().Count() != gotTiles.Count)
      bad.Add("a tile twice");

    // boxes: disjoint rectangles of whole cells, each cell in the mask, together exactly the mask, each inside one tile, grouped by tile
    var covered = new int[r.Nx, r.Nz];
    int boxIndex = 0;
    int next = 0;
    foreach (var t in s.Tiles)
    {
      if (t.BoxStart != next)
        bad.Add($"tile ({t.Tx},{t.Tz}) boxes start at {t.BoxStart}, expected {next}");
      next = t.BoxStart + t.BoxCount;
      for (int b = t.BoxStart; b < t.BoxStart + t.BoxCount && b < s.Boxes.Length; b++, boxIndex++)
      {
        var box = s.Boxes[b];
        if (box.Tx != t.Tx || box.Tz != t.Tz)
          bad.Add($"box {b} says tile ({box.Tx},{box.Tz}), it is in the group of ({t.Tx},{t.Tz})");
        // the cells the box covers: centres within its rectangle
        int i0 = (int)Math.Round((box.MinX + 2f - r.X0) / 4f), i1 = (int)Math.Round((box.MaxX - 2f - r.X0) / 4f);
        int j0 = (int)Math.Round((box.MinZ + 2f - r.Z0) / 4f), j1 = (int)Math.Round((box.MaxZ - 2f - r.Z0) / 4f);
        float bed = l.Level;
        for (int i = i0; i <= i1; i++)
          for (int j = j0; j <= j1; j++)
          {
            if (i < 0 || j < 0 || i >= r.Nx || j >= r.Nz || !r.Mask[i, j])
            {
              bad.Add($"box {b} holds a cell ({i},{j}) that is not in the mask");
              continue;
            }
            covered[i, j]++;
            if (TileIndex(r.X0 + 4f * i) != t.Tx || TileIndex(r.Z0 + 4f * j) != t.Tz)
              bad.Add($"box {b} holds a cell ({i},{j}) of another tile");
            bed = Math.Min(bed, r.H[i, j]);
          }
        if (Math.Abs(box.Bottom - (bed - 4f)) > 1e-4)
          bad.Add($"box {b} bottom {box.Bottom} against {bed - 4f}");
      }
    }
    if (boxIndex != s.Boxes.Length)
      bad.Add($"{boxIndex} boxes in the tiles' groups, {s.Boxes.Length} boxes");
    int uncovered = 0, twice = 0;
    for (int i = 0; i < r.Nx; i++)
      for (int j = 0; j < r.Nz; j++)
      {
        if (r.Mask[i, j] && covered[i, j] == 0) uncovered++;
        if (covered[i, j] > 1) twice++;
      }
    if (uncovered > 0)
      bad.Add($"{uncovered} mask cells in no box");
    if (twice > 0)
      bad.Add($"{twice} cells in two boxes");

    // corner depths
    foreach (var t in s.Tiles)
    {
      float cx = t.Tx * 64f, cz = t.Tz * 64f;
      float[] want =
      [
        Math.Max(0f, l.Level - height(cx - 32f, cz + 32f)), Math.Max(0f, l.Level - height(cx + 32f, cz + 32f)),
        Math.Max(0f, l.Level - height(cx + 32f, cz - 32f)), Math.Max(0f, l.Level - height(cx - 32f, cz - 32f)),
      ];
      if (t.D0 != want[0] || t.D1 != want[1] || t.D2 != want[2] || t.D3 != want[3])
        bad.Add($"tile ({t.Tx},{t.Tz}) depths {t.D0},{t.D1},{t.D2},{t.D3} against {string.Join(",", want)}");
    }
    return bad;
  }

  // ---- fixtures with known answers ---------------------------------------------------------------------------------------------

  public static void FloodFixturesTest()
  {
    Section("flood: a round bowl, a sill, two basins, an island, a ridge, a bump, a dry point, a plateau");
    var bowl = Bowl();
    // level 60 in a bowl of 40 + 40 (r/100)^2: water inside r = 70.7 m, closed (the box reaches 130 m)
    var closed = LakeFlood.Compute(bowl, MakeLake(0, 0, 60f, 100f), default, keepWet: true);
    C(closed.Ok && !closed.TouchedEdge && closed.Warning == null, "a bowl below its rim is a closed lake with no warning");
    int expectCells = 0;
    for (int i = 0; i < closed.Nx; i++)
      for (int j = 0; j < closed.Nz; j++)
        if (bowl(closed.Cx(i), closed.Cz(j)) < 60f)
          expectCells++;
    C(closed.WetCells == expectCells && closed.WetCells > 900, $"a bowl's wet cells are the cells under the level: {closed.WetCells} against {expectCells}");
    C(closed.Boxes.Length > 0 && closed.Tiles.Length >= 4, $"it spans tiles: {closed.Tiles.Length} tiles, {closed.Boxes.Length} boxes");
    C(closed.InMask(0f, 0f) && !closed.InMask(100f, 100f) && closed.InMask(70f, 0f) && !closed.InMask(80f, 0f), "the mask holds the water and one cell more");

    // a sill (a channel at 70 m): the level below it keeps the lake in, above it the water runs out along the channel to the box's edge
    var sill = Bowl(70f);
    var below = LakeFlood.Compute(sill, MakeLake(0, 0, 69.5f, 100f), default, keepWet: true);
    var above = LakeFlood.Compute(sill, MakeLake(0, 0, 70.5f, 100f), default, keepWet: true);
    C(below.Ok && !below.TouchedEdge, "below the sill the lake is closed");
    C(above.Ok && above.TouchedEdge && above.Warning != null && above.Warning.Contains("not closed"), "above the sill the flood leaks to the box's edge and the lake warns that the basin is not closed");
    C(above.WetCells > below.WetCells, "the leak wets more");

    // two basins joined by a saddle: bowls at x = -60 and x = +60 (30 m low point), a saddle at 55 m between them, rim 80
    var bowlAt = Bowl();
    Func<float, float, float> twin = (x, z) => Math.Min(bowlAt(x + 60f, z), bowlAt(x - 60f, z));
    var left = MakeLake(-60f, 0f, 45f, 140f);
    var onlyLeft = LakeFlood.Compute(twin, left, default, keepWet: true);
    var both = LakeFlood.Compute(twin, left with { Level = 70f }, default, keepWet: true);
    C(onlyLeft.Ok && both.Ok && both.WetCells > onlyLeft.WetCells, $"basins joined over their saddle: below it {onlyLeft.WetCells} cells, above {both.WetCells}");
    bool rightDry = true;
    for (int i = 0; i < onlyLeft.Nx; i++)
      for (int j = 0; j < onlyLeft.Nz; j++)
        if (onlyLeft.Cx(i) > 20f && onlyLeft.CellIsWet(i, j))
          rightDry = false;
    C(rightDry, "below the saddle the other basin stays dry");

    // an island: a mound 20 m wide in the middle of the lake stands above the level
    Func<float, float, float> island = (x, z) =>
    {
      float h = bowl(x, z);
      float r = (float)Math.Sqrt(x * x + z * z);
      return r < 20f ? 70f : h;
    };
    var islandLake = new Lake("i", 50f, 0f, 60f, -100f, -100f, 100f, 100f, 10f);
    var withIsland = LakeFlood.Compute(island, islandLake, default, keepWet: true);
    int ci = (int)Math.Round((0f - withIsland.X0) / 4f), cj = (int)Math.Round((0f - withIsland.Z0) / 4f);
    C(withIsland.Ok && !withIsland.TouchedEdge && !withIsland.CellIsWet(ci, cj), $"an island is not wet (ok {withIsland.Ok}, edge {withIsland.TouchedEdge}, cell {ci},{cj} wet {withIsland.CellIsWet(ci, cj)}, x0 {withIsland.X0} z0 {withIsland.Z0}, ground there {island(withIsland.Cx(ci), withIsland.Cz(cj))})");
    C(!withIsland.InMask(0f, 0f) && withIsland.InMask(24f, 0f), "its inside is not in the mask, the cell around it is");
    var cmp = Compare(island, islandLake, withIsland, Reference(island, islandLake));
    C(cmp.Count == 0, "the island equals the reference: " + string.Join("; ", cmp));

    // a ridge narrower than the grid: 2 m wide at 62 m between two basins at 40 m; the ridge falls on a sample column, or between two
    foreach (float ridgeX in new[] { 0f, 2f })
    {
      Func<float, float, float> ridge = (x, z) => Math.Abs(x - ridgeX) < 1f ? 62f : (Math.Abs(x) < 70f && Math.Abs(z) < 70f ? 40f : 90f);
      var lake = MakeLake(-30f, 0f, 55f, 70f);
      var shape = LakeFlood.Compute(ridge, lake, default, keepWet: true);
      var cmp2 = Compare(ridge, lake, shape, Reference(ridge, lake));
      C(cmp2.Count == 0, $"a ridge at x = {ridgeX} equals the reference: " + string.Join("; ", cmp2));
      // the lake's x0 = -30 - 70 - 12 = -112: columns at -112 + 4i, so x = 0 is a column and x = 2 is between two
      bool otherSide = false;
      for (int i = 0; i < shape.Nx; i++)
        for (int j = 0; j < shape.Nz; j++)
          if (shape.Cx(i) > 4f && shape.CellIsWet(i, j))
            otherSide = true;
      C(otherSide == (ridgeX == 2f), ridgeX == 0f ? "a ridge on a sample column holds the water" : "a ridge between two sample columns is not seen: the water crosses it");
    }

    // a point on a bump: the point stands on ground above the level, the lowest cell within 12 m starts the flood; further than that the lake fails
    Func<float, float, float> bump = (x, z) => (x - 5f) * (x - 5f) + z * z < 36f ? 62f : bowl(x, z);
    var onBump = LakeFlood.Compute(bump, MakeLake(5f, 0f, 60f, 60f), default, keepWet: true);
    C(onBump.Ok && onBump.WetCells > 100, "a point on a bump starts at the lowest cell within 12 m");
    Func<float, float, float> farBump = (x, z) => (x - 5f) * (x - 5f) + z * z < 400f ? 62f : bowl(x, z);
    var dry = LakeFlood.Compute(farBump, MakeLake(5f, 0f, 60f, 60f), default);
    C(!dry.Ok && dry.Failed.Contains("is dry") && dry.Failed.Contains("level 60.0") && dry.Tiles.Length == 0 && dry.Boxes.Length == 0, "a point further than 12 m from water fails: " + dry.Failed);
    var dry2 = LakeFlood.Compute((x, z) => 100f, MakeLake(0f, 0f, 60f, 50f), default);
    C(!dry2.Ok && dry2.Failed.Contains("(0, 0)") && dry2.Failed.Contains("ground 100.0 m"), "a flat height above the level has a dry point: " + dry2.Failed);

    // a plateau: every cell is below the level, so the flood touches the edge of the grid and wets all of it
    var plateau = LakeFlood.Compute((x, z) => 40f, MakeLake(0f, 0f, 60f, 50f), default, keepWet: true);
    C(plateau.Ok && plateau.TouchedEdge && plateau.WetCells == plateau.Nx * plateau.Nz, "a plateau under the level is all wet and touches the edge");

    // the level is strict: ground exactly at the level is dry
    var strict = LakeFlood.Compute((x, z) => 60f, MakeLake(0f, 0f, 60f, 50f), default);
    C(!strict.Ok, "ground at the level is not under it: the point is dry");
  }

  // ---- 5,000 seeded random fields ----------------------------------------------------------------------------------------------

  public static void FloodRandomFieldsTest()
  {
    Section("flood: 5,000 random fields against the brute-force reference");
    var rng = new Rng(2026101301);
    int made = 0, failed = 0, leaked = 0, problems = 0, tilesSeen = 0, boxesSeen = 0;
    for (int n = 0; n < 5000; n++)
    {
      var noise = new Noise((ulong)(n + 1), (float)rng.Range(35, 70), (float)rng.Range(15, 50), (float)rng.Range(40, 140));
      float half = (float)rng.Range(12, 130);
      float cx = (float)Math.Round(rng.Range(-300, 300)), cz = (float)Math.Round(rng.Range(-300, 300));
      // the lake's point is in the middle of the box, as a map maker's is; the level is somewhere in the field's range
      var lake = new Lake("r" + n, cx + (float)Math.Round(rng.Range(-half / 2, half / 2)), cz + (float)Math.Round(rng.Range(-half / 2, half / 2)),
        (float)Math.Round(rng.Range(30, 80) * 4) / 4f, cx - half, cz - half, cx + half, cz + half, 10f);
      var shape = LakeFlood.Compute(noise.Height, lake, default, keepWet: true);
      var reference = Reference(noise.Height, lake);
      var bad = Compare(noise.Height, lake, shape, reference);
      if (shape.Ok)
      {
        made++;
        tilesSeen += shape.Tiles.Length;
        boxesSeen += shape.Boxes.Length;
        if (shape.TouchedEdge) leaked++;
      }
      else
        failed++;
      if (bad.Count > 0)
      {
        problems++;
        if (problems <= 5)
          C(false, $"random field {n}: " + string.Join("; ", bad.Take(4)));
      }
    }
    C(problems == 0, $"5,000 random fields: {problems} differ from the reference");
    C(made > 1000 && failed > 100 && leaked > 100, $"the fields are varied: {made} made, {failed} with a dry point, {leaked} that touch the edge ({tilesSeen} tiles, {boxesSeen} boxes)");
  }

  // ---- the same bytes whatever the thread -------------------------------------------------------------------------------------

  public static void FloodDeterminismTest()
  {
    Section("flood: the same bytes on repeated runs and on many threads");
    var lakes = new List<(Func<float, float, float>, Lake)>();
    var rng = new Rng(77);
    for (int n = 0; n < 24; n++)
    {
      var noise = new Noise((ulong)(900 + n), 52f, 30f, 90f);
      lakes.Add((noise.Height, new Lake("d" + n, 0, 0, (float)rng.Range(40, 70), -100, -100, 100 + n * 7, 100, 10f)));
    }
    ulong[] first = lakes.Select(l => LakeFlood.Compute(l.Item1, l.Item2).Hash()).ToArray();
    C(first.SequenceEqual(lakes.Select(l => LakeFlood.Compute(l.Item1, l.Item2).Hash())), "a second run gives the same hash for 24 lakes");
    var all = new ulong[lakes.Count * 4];
    Parallel.For(0, all.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
    {
      int i = k % lakes.Count;
      all[k] = LakeFlood.Compute(lakes[i].Item1, lakes[i].Item2).Hash();
    });
    C(Enumerable.Range(0, all.Length).All(k => all[k] == first[k % lakes.Count]), "the same lakes on 8 threads, four times each, give the same hashes");
    C(first.Distinct().Count() == first.Length, "and different lakes give different hashes");
  }

  public static void FloodLimitsTest()
  {
    Section("flood: refusals and cancellation");
    var empty = LakeFlood.Compute((x, z) => 40f, new Lake("e", 0, 0, 60, 100, 100, 0, 0, 5));
    C(!empty.Ok && empty.Failed.Contains("empty") && empty.Nx == 0, "an inverted box is refused as empty: " + empty.Failed);
    var nan = LakeFlood.Compute((x, z) => 40f, new Lake("n", float.NaN, 0, 60, -50, -50, 50, 50, 5));
    C(!nan.Ok && nan.Failed.Contains("not numbers"), "a point that is not a number is refused: " + nan.Failed);
    var inf = LakeFlood.Compute((x, z) => 40f, new Lake("n", 0, 0, float.PositiveInfinity, -50, -50, 50, 50, 5));
    C(!inf.Ok, "an infinite level is refused");
    int calls = 0;
    var huge = LakeFlood.Compute((x, z) => { calls++; return 40f; }, new Lake("h", 0, 0, 60, -30000, -30000, 30000, 30000, 5));
    C(!huge.Ok && huge.Failed.Contains("too large") && calls == 0, "a box of 60 km is refused before any ground is asked: " + huge.Failed);
    var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool threw = false;
    try
    {
      LakeFlood.Compute((x, z) => 40f, MakeLake(0, 0, 60, 100), cancelled.Token);
    }
    catch (OperationCanceledException)
    {
      threw = true;
    }
    C(threw, "a cancelled token ends the flood with OperationCanceledException");
    var cts = new CancellationTokenSource();
    int asked = 0;
    threw = false;
    try
    {
      LakeFlood.Compute((x, z) => { if (++asked == 300) cts.Cancel(); return 40f; }, MakeLake(0, 0, 60, 200), cts.Token);
    }
    catch (OperationCanceledException)
    {
      threw = true;
    }
    C(threw && asked < 4000, $"cancelled in the middle of the sampling it stops at the next row ({asked} samples asked)");
    // far outside of the box the point is clamped to the nearest cell, as Tarn's is
    var outside = LakeFlood.Compute((x, z) => 40f, new Lake("o", 1e9f, -1e9f, 60, -50, -50, 50, 50, 5));
    C(outside.Ok, "a point far outside its box still starts at the nearest cell");
  }
}

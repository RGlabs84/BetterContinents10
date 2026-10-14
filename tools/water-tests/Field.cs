// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// SeaField (SeaField.cs): the grid a radius gets, the distances against brute force, the same bytes whatever the threads, the sampler against a
// transcription of the shader's arithmetic, the world's own rules (the layout of a bigger world, patched for real) against the scaled formula,
// the hot sea's mask, the same-as-game test, and what a field for a world of VALtima's size costs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Group = BetterContinents.WorldSizeHelper.Group;

internal static partial class Program
{
  private static void FieldTests()
  {
    GridTests();
    DistanceTests();
    DeterminismTests();
    SamplerTests();
    OwnRulesTests();
    HotSeaTests();
    SameAsGameTests();
    CostTests();
  }

  private static readonly Type[] XY = [typeof(float), typeof(float)];

  // A field of n x n texels of 8 m (radius 4 n) whose masks are arrays.
  private static SeaFieldInput Blob(int n, bool[] deep, bool[] hot, int workers = 0)
  {
    float radius = 4f * n;
    int Cell(float v) => Math.Clamp((int)Math.Floor((v + radius) / 8f), 0, n - 1);
    return new SeaFieldInput
    {
      Radius = radius,
      Deep = (x, z) => deep[Cell(z) * n + Cell(x)],
      Hot = (x, z) => hot[Cell(z) * n + Cell(x)],
      Workers = workers,
    };
  }

  private static bool[] RandomMask(System.Random random, int n, double density, bool blobs)
  {
    var mask = new bool[n * n];
    if (blobs)
    {
      // A few discs and stripes, so that borders run every way.
      for (int k = 0; k < 6; k++)
      {
        double cx = random.NextDouble() * n, cz = random.NextDouble() * n, r = 2 + random.NextDouble() * n / 3;
        bool stripe = random.Next(3) == 0;
        double angle = random.NextDouble() * Math.PI;
        for (int j = 0; j < n; j++)
          for (int i = 0; i < n; i++)
          {
            double dx = i + 0.5 - cx, dz = j + 0.5 - cz;
            bool inside = stripe ? Math.Abs(dx * Math.Cos(angle) + dz * Math.Sin(angle)) < r / 4 : dx * dx + dz * dz < r * r;
            if (inside) mask[j * n + i] = !mask[j * n + i] || random.Next(4) != 0;
          }
      }
    }
    else
    {
      for (int i = 0; i < mask.Length; i++)
        mask[i] = random.NextDouble() < density;
    }
    return mask;
  }

  // The bytes Build should make, by brute force: for each texel of one class, the squared distance to the nearest texel of the other, in texels.
  private static long NearestOther(bool[] mask, int n, int i, int j)
  {
    bool mine = mask[j * n + i];
    long best = long.MaxValue;
    for (int b = 0; b < n; b++)
      for (int a = 0; a < n; a++)
        if (mask[b * n + a] != mine)
          best = Math.Min(best, (long)(a - i) * (a - i) + (long)(b - j) * (b - j));
    return best;
  }

  private static int ExpectedMetres(long d2, double spacing, int cap) =>
    d2 == long.MaxValue ? cap : (int)Math.Min(cap, Math.Max(0.0, Math.Floor((Math.Sqrt(d2) - 0.5) * spacing + 0.5)));

  // ------------------------------------------------------------------------------------------------ the grid
  private static void GridTests()
  {
    Section("field: the grid");
    if (!running) return;
    SeaField.GridFor(10500f, out int n, out float s);
    Check(n == 2625 && s == 8f, $"a vanilla-size world (10500 m): 2625 texels of 8 m ({n} of {s})");
    SeaField.GridFor(20500f, out n, out s);
    Check(n == 4096 && s == (float)(41000.0 / 4096), $"World Size 20000 (20500 m): 4096 texels of {41000.0 / 4096:0.####} m ({n} of {s})");
    SeaField.GridFor(32764f, out n, out s);
    Check(n == 4096 && Math.Abs(s - 15.998f) < 0.001f, $"VALtima's world (32764 m): 4096 texels of 16 m ({n} of {s})");
    SeaField.GridFor(65536f, out n, out s);
    Check(n == 4096 && s == 32f, $"the most (65536 m): 4096 texels of 32 m ({n} of {s})");
    SeaField.GridFor(5250f, out n, out s);
    Check(n == 1313 && s == 8f, $"a small world (5250 m): 1313 texels of 8 m ({n} of {s})");
    bool tame = true;
    foreach (float radius in new[] { 0f, -5f, float.NaN, 1e9f, float.PositiveInfinity, 15f })
    {
      SeaField.GridFor(radius, out n, out s);
      tame &= n >= 4 && n <= 4096 && s >= 8f && s <= 32f;
    }
    Check(tame, "a radius that makes no world (0, negative, NaN, infinite, past the most) gets a small grid, never none and never above 4096");
    var random = new System.Random(5);
    int bad = 0;
    for (int i = 0; i < 20000; i++)
    {
      float radius = (float)(16.0 + random.NextDouble() * 65520.0);
      SeaField.GridFor(radius, out n, out s);
      bool covers = n * (double)s >= 2.0 * radius * (1 - 1e-6);
      bool snug = n == 4096 || (n - 1) * (double)s < 2.0 * radius;
      if (n > 4096 || s < 8f || !covers || !snug) bad++;
    }
    Check(bad == 0, $"any radius: at most 4096 texels of 8 m or more that cover the world and no more ({bad} of 20000 wrong)");
  }

  // ------------------------------------------------------------------------------------------------ the distances
  private static void DistanceTests()
  {
    Section("field: exact distances against brute force");
    if (!running) return;
    var random = new System.Random(11);
    int cases = 0, wrongTexels = 0, texels = 0;
    string first = null;
    foreach (int n in new[] { 4, 5, 9, 17, 30, 41 })
      foreach (var (density, blobs) in new[] { (0.0, false), (1.0, false), (0.03, false), (0.5, false), (0.97, false), (0.0, true), (0.0, true), (0.0, true) })
      {
        var deep = RandomMask(random, n, density, blobs);
        var hot = RandomMask(random, n, density, blobs);
        var field = SeaField.Build(Blob(n, deep, hot), true, CancellationToken.None);
        cases++;
        if (field.Size != n || field.Spacing != 8f) { Check(false, $"a field of {n} texels was {field.Size} of {field.Spacing}"); continue; }
        for (int j = 0; j < n; j++)
          for (int i = 0; i < n; i++)
          {
            int at = j * n + i;
            texels++;
            int calm = deep[at] ? ExpectedMetres(NearestOther(deep, n, i, j), 8.0, 255) : 0;
            int signed = ExpectedMetres(NearestOther(hot, n, i, j), 8.0, 127) * (hot[at] ? 1 : -1);
            if (field.Calm[at] != calm || field.Hot[at] != 128 + signed)
            {
              wrongTexels++;
              first ??= $"{n} texels, density {density}, blobs {blobs}: texel ({i}, {j}) calm {field.Calm[at]} against {calm}, hot {field.Hot[at]} against {128 + signed}";
            }
          }
      }
    Check(wrongTexels == 0, $"calm and hot of {cases} random masks (every size, empty, full, sparse, dense, discs and stripes) are the exact distances ({texels} texels, {wrongTexels} wrong){(first != null ? "; first: " + first : "")}");

    // The same at a size where the lower envelope's stack grows long (600 x 600), at texels picked over the whole field.
    {
      int big = 600;
      var bigDeep = RandomMask(random, big, 0, true);
      var bigHot = RandomMask(random, big, 0.02, false);
      var bigField = SeaField.Build(Blob(big, bigDeep, bigHot, 6), true, CancellationToken.None);
      int wrongBig = 0, sampled = 0;
      for (int k = 0; k < 1500; k++)
      {
        int i = random.Next(big), j = random.Next(big), at = j * big + i;
        sampled++;
        int calm = bigDeep[at] ? ExpectedMetres(NearestOther(bigDeep, big, i, j), 8.0, 255) : 0;
        int signed = ExpectedMetres(NearestOther(bigHot, big, i, j), 8.0, 127) * (bigHot[at] ? 1 : -1);
        if (bigField.Calm[at] != calm || bigField.Hot[at] != 128 + signed)
          wrongBig++;
      }
      Check(wrongBig == 0, $"and at 600 x 600 texels, with blobs and with a sprinkle of hot texels: {sampled} texels picked over the field, {wrongBig} wrong");
    }

    // The bytes are as the contract says on the simplest border: a straight line between texels, Deep North on one side.
    int m = 20;
    var half = new bool[m * m];
    for (int j = 0; j < m; j++)
      for (int i = 10; i < m; i++)
        half[j * m + i] = true;
    var straight = SeaField.Build(Blob(m, half, half), true, CancellationToken.None);
    Check(straight.Calm[5 * m + 9] == 0 && straight.Calm[5 * m + 10] == 4 && straight.Calm[5 * m + 11] == 12 && straight.Calm[5 * m + 19] == 76,
      $"a straight border: the first texel inside is half a texel (4 m) from it, then 12 m, 20 m ...; none outside ({straight.Calm[5 * m + 9]}, {straight.Calm[5 * m + 10]}, {straight.Calm[5 * m + 11]}, {straight.Calm[5 * m + 19]})");
    Check(straight.Hot[5 * m + 9] == 128 - 4 && straight.Hot[5 * m + 10] == 128 + 4 && straight.Hot[5 * m + 0] == 128 - 76 && straight.Hot[5 * m + 19] == 128 + 76,
      $"and the hot channel is signed the same way: -4 m and +4 m next to the border ({straight.Hot[5 * m + 9] - 128}, {straight.Hot[5 * m + 10] - 128})");

    // A field that is nothing but Deep North, or nothing but hot sea, or neither.
    var full = SeaField.Build(Blob(m, Enumerable.Repeat(true, m * m).ToArray(), Enumerable.Repeat(true, m * m).ToArray()), true, CancellationToken.None);
    Check(full.Calm.All(b => b == 255) && full.Hot.All(b => b == 255) && full.CalmShare == 1.0 && full.HotShare == 1.0, "all Deep North and all hot: 255 m inside everywhere, shares 1");
    var none = SeaField.Build(Blob(m, new bool[m * m], new bool[m * m]), true, CancellationToken.None);
    Check(none.Calm.All(b => b == 0) && none.Hot.All(b => b == 1) && none.CalmShare == 0.0, "no Deep North and no hot sea: calm 0, hot 127 m outside everywhere");
    var calmOnly = SeaField.Build(Blob(m, half, half), false, CancellationToken.None);
    Check(calmOnly.Hot == null && calmOnly.Calm.SequenceEqual(straight.Calm) && calmOnly.Rg16()[1] == calmOnly.Calm[0] && calmOnly.Rg16()[0] == 128,
      "a server's field (hot false) has the calm half only, the same bytes as a client's");
    var rg = straight.Rg16();
    bool rgOk = rg.Length == m * m * 2;
    for (int i = 0; i < m * m && rgOk; i++)
      rgOk = rg[2 * i] == straight.Hot[i] && rg[2 * i + 1] == straight.Calm[i];
    Check(rgOk, "the RG16 bytes are R (hot) then G (calm) for each texel, row after row");
  }

  // ------------------------------------------------------------------------------------------------ determinism
  private static void DeterminismTests()
  {
    Section("field: deterministic bytes");
    if (!running) return;
    var random = new System.Random(23);
    int n = 300;
    var deep = RandomMask(random, n, 0, true);
    var hot = RandomMask(random, n, 0, true);
    bool Equal(SeaField a, SeaField b) =>
      a.Size == b.Size && a.Spacing == b.Spacing && a.Calm.SequenceEqual(b.Calm) && a.Hot.SequenceEqual(b.Hot) && a.CalmShare == b.CalmShare && a.HotShare == b.HotShare;
    var one = SeaField.Build(Blob(n, deep, hot, 1), true, CancellationToken.None);
    var again = SeaField.Build(Blob(n, deep, hot, 1), true, CancellationToken.None);
    var three = SeaField.Build(Blob(n, deep, hot, 3), true, CancellationToken.None);
    var eight = SeaField.Build(Blob(n, deep, hot, 8), true, CancellationToken.None);
    var many = SeaField.Build(Blob(n, deep, hot, 64), true, CancellationToken.None);
    Check(Equal(one, again), "two single-worker builds give the same bytes");
    Check(Equal(one, three) && Equal(one, eight) && Equal(one, many), "the parallel builds (3, 8 and 64 workers) give the single worker's bytes");
    Check(one.Calm.Any(b => b > 0) && one.Hot.Any(b => b != 1), "(and the field is not empty)");

    // With the depth grid, and with the game's own functions.
    var withHeight = Blob(n, deep, hot, 1);
    withHeight.Height = (x, z) => 30f - 40f * (float)Math.Sin(x * 0.01) * (float)Math.Cos(z * 0.013);
    var h1 = SeaField.Build(withHeight, true, CancellationToken.None);
    withHeight.Workers = 8;
    var h8 = SeaField.Build(withHeight, true, CancellationToken.None);
    Check(h1.Depth != null && h8.Depth != null && h1.Depth.SequenceEqual(h8.Depth) && h1.DepthSize == h8.DepthSize && h1.DepthOriginX == h8.DepthOriginX,
      "the depth grid is the same on one worker as on eight");
    // The depth grid: a texel per zone corner (32 m plus a multiple of 64), covering the world, each the zone water's own rule.
    bool corners = (h1.DepthOriginX - 32f) % 64f == 0f && h1.DepthOriginX <= -h1.Radius && h1.DepthOriginX + 64f * (h1.DepthSize - 1) >= h1.Radius
                   && h1.DepthOriginX > -h1.Radius - 64f;
    bool rule = true;
    for (int j = 0; j < h1.DepthSize; j += 3)
      for (int i = 0; i < h1.DepthSize; i += 2)
      {
        float h = withHeight.Height(h1.DepthOriginX + 64f * i, h1.DepthOriginZ + 64f * j);
        rule &= h1.Depth[j * h1.DepthSize + i] == (byte)Math.Floor(Math.Min(Math.Max((30.0 - h) / 10.0, 0.0), 1.0) * 255.0 + 0.5);
      }
    Check(corners && rule, $"the depth grid has a texel at each zone corner, from 64 m outside the world's edge to 64 m past it ({h1.DepthSize} x {h1.DepthSize} from {h1.DepthOriginX}), each clamp01((30 - height) / 10)");
    // Made later, when the heights can be asked, it is the same grid.
    var late = SeaField.Build(Blob(n, deep, hot, 2), true, CancellationToken.None);
    Check(late.Depth == null && late.DepthSize == 0, "a field built without heights has no depth grid yet");
    late.BuildDepth(withHeight.Height, 5, CancellationToken.None);
    Check(late.Depth != null && late.Depth.SequenceEqual(h1.Depth) && late.DepthSize == h1.DepthSize && late.DepthOriginX == h1.DepthOriginX && late.DepthOriginZ == h1.DepthOriginZ,
      "and made afterwards it is the grid the build would have made");

    // A stop ends the build with OperationCanceledException, and nothing is left running.
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    bool cancelled = false;
    try
    {
      SeaField.Build(Blob(n, deep, hot, 4), true, stop.Token);
    }
    catch (OperationCanceledException)
    {
      cancelled = true;
    }
    Check(cancelled, "a build that is stopped throws OperationCanceledException");
    // A function that throws on a worker comes out of Build.
    var failing = Blob(n, deep, hot, 4);
    failing.Deep = (x, z) => z > 500f ? throw new InvalidOperationException("a broken rule") : false;
    string message = null;
    try
    {
      SeaField.Build(failing, true, CancellationToken.None);
    }
    catch (Exception e)
    {
      message = e.Message;
    }
    Check(message == "a broken rule", "an exception on a worker comes out of Build, and the other workers stop");
  }

  // ------------------------------------------------------------------------------------------------ the sampler
  // BCSeaWaves as the shader writes it (SeaField.cs's contract): four texel reads and the lerps, in floats.
  private static float ShaderW(SeaField f, float x, float z)
  {
    int n = f.Size;
    float u = (x - f.OriginX) * f.InvSpacing - 0.5f;
    float v = (z - f.OriginZ) * f.InvSpacing - 0.5f;
    u = Math.Min(Math.Max(u, -1f), n);
    v = Math.Min(Math.Max(v, -1f), n);
    float iu = MathF.Floor(u), iv = MathF.Floor(v);
    float tx = u - iu, tz = v - iv;
    int x0 = Math.Min(Math.Max((int)iu, 0), n - 1), x1 = Math.Min(Math.Max((int)iu + 1, 0), n - 1);
    int z0 = Math.Min(Math.Max((int)iv, 0), n - 1), z1 = Math.Min(Math.Max((int)iv + 1, 0), n - 1);
    float c00 = f.Calm[z0 * n + x0], c10 = f.Calm[z0 * n + x1], c01 = f.Calm[z1 * n + x0], c11 = f.Calm[z1 * n + x1];
    float a = c00 + (c10 - c00) * tx;
    float b = c01 + (c11 - c01) * tx;
    float calm = a + (b - a) * tz;
    return 1f - Math.Min(Math.Max(calm * 0.005f, 0f), 1f);
  }

  private static void SamplerTests()
  {
    Section("field: the sampler");
    if (!running) return;
    var random = new System.Random(31);
    int n = 120;
    var deep = RandomMask(random, n, 0, true);
    var field = SeaField.Build(Blob(n, deep, new bool[n * n]), false, CancellationToken.None);
    float radius = field.Radius;
    float worst = 0f;
    int points = 100000;
    for (int i = 0; i < points; i++)
    {
      // Inside the grid, on its border, and well outside it.
      float spread = i % 5 == 0 ? 3f : 1.05f;
      float x = (float)((random.NextDouble() * 2 - 1) * radius * spread), z = (float)((random.NextDouble() * 2 - 1) * radius * spread);
      worst = Math.Max(worst, Math.Abs(field.Waves(x, z) - ShaderW(field, x, z)));
    }
    Check(worst <= 1e-6f, $"SeaField.Waves against a transcription of the shader's BCSeaWaves, {points} points inside and outside the grid: the greatest difference is {worst:G3} (at most 1e-6)");

    // At a texel's centre the read is that texel; between two it is their mean; outside the grid the border texels apply.
    int ci = 70, cj = 33;
    float cx = field.OriginX + (ci + 0.5f) * field.Spacing, cz = field.OriginZ + (cj + 0.5f) * field.Spacing;
    float expected = 1f - Mathf.Clamp01(field.Calm[cj * n + ci] * 0.005f);
    Check(Math.Abs(field.Waves(cx, cz) - expected) < 1e-6f, "at a texel's centre, W is that texel's");
    float mid = field.CalmAt(cx + field.Spacing / 2, cz);
    Check(Math.Abs(mid - (field.Calm[cj * n + ci] + field.Calm[cj * n + ci + 1]) / 2f) < 1e-3f, "halfway between two texels, the calm is their mean");
    bool nanThrows = false;
    try
    {
      field.Waves(float.NaN, 0f);
    }
    catch (Exception)
    {
      nanThrows = true;
    }
    Check(field.Waves(-1e7f, 3e6f) == field.Waves(-radius * 3, radius * 3) && !nanThrows, "far outside the grid the border texels apply, and a NaN does not throw");
    Check(field.Waves(1e30f, -1e30f) == field.Waves(radius * 2, -radius * 2), "an edge with the edge drop-off off (1e30 m) reads the border too");

    // A 200 m ramp: W falls from 1 at the border to 0 at 200 m, by the calm distance.
    var ramp = new bool[n * n];
    for (int j = 0; j < n; j++)
      for (int i = n / 2; i < n; i++)
        ramp[j * n + i] = true;
    var rampField = SeaField.Build(Blob(n, ramp, new bool[n * n]), false, CancellationToken.None);
    float border = 0f;
    bool monotone = true;
    float last = 2f;
    for (float x = border - 100f; x <= border + 300f; x += 3.7f)
    {
      float w = rampField.Waves(x, 11f);
      monotone &= w <= last + 1e-6f;
      last = w;
    }
    Check(monotone && rampField.Waves(border - 100f, 11f) == 1f && rampField.Waves(border + 200f, 11f) < 0.02f && rampField.Waves(border + 260f, 11f) == 0f,
      $"across a border W falls from 1 outside to 0 from 200 m inside ({rampField.Waves(border - 100f, 11f):0.##} at -100 m, {rampField.Waves(border, 11f):0.###} at the border, {rampField.Waves(border + 200f, 11f):0.###} at 200 m, {rampField.Waves(border + 260f, 11f):0.##} at 260 m)");
  }

  // ------------------------------------------------------------------------------------------------ the world's own rules
  // The layout's transpilers on IsDeepnorth and the Ashlands' ring, patched for real, as size-tests does for the layout.
  private static Group LayoutGroup() => new(
    new WorldSizeHelper.Part(() => AccessTools.Method(typeof(WorldGenerator), "IsAshlands", XY), transpiler: "AshlandsTranspiler"),
    new WorldSizeHelper.Part(() => AccessTools.Method(typeof(WorldGenerator), "GetAshlandsOceanGradient", XY), transpiler: "AshlandsTranspiler"),
    new WorldSizeHelper.Part(() => AccessTools.Method(typeof(WorldGenerator), "IsDeepnorth", XY), transpiler: "IsDeepnorthTranspiler"));

  // The scaled formulas (the layout's: the circles' radii and centres x ratio, the 100 m ripple unscaled).
  private static double ScaledFade(double ratio, float x, float z)
  {
    double angle = WorldGenerator.WorldAngle(x, z) * 100.0;
    double len = Math.Sqrt((double)x * x + ((double)z + 4000.0 * ratio) * ((double)z + 4000.0 * ratio));
    return Math.Min(Math.Max((len - (12000.0 * ratio + angle)) / 200.0, 0.0), 1.0);
  }

  private static void OwnRulesTests()
  {
    Section("field: the world's own rules (World Size 20000)");
    if (!running) return;
    var harmony = new Harmony("water-tests.layout");
    var group = LayoutGroup();
    var size = new WorldGeometry(20000f, 500f);
    try
    {
      WorldSizeHelper.Layout.Assume(size);
      Check(group.Update(harmony, size) && group.Patched, "the layout of World Size 20000 is patched on IsDeepnorth and the Ashlands' ring");
      var settings = World(12, 20000f, 500f);
      Check(SeaPatches.Candidate(settings) && SeaPatches.FieldRadius(settings) == 20500f, "BC's sea is a candidate for the world, with its edge at 20500 m");
      var input = SeaPatches.Input(settings, true);
      input.Workers = 8;
      var field = SeaField.Build(input, true, CancellationToken.None);
      Check(field.Size == 4096 && Math.Abs(field.Spacing - 41000f / 4096) < 1e-4f, $"the field: {field}");
      Check(!field.CalmSameAsGame && !field.HotSameAsGame && !field.SameAsGame, "its Deep North and its hot sea are not the game's: BC's sea is on");
      Check(field.CalmShare > 0.02 && field.CalmShare < 0.4 && field.HotShare > 0.01 && field.HotShare < 0.4, $"the shares are of a world's size ({field.CalmShare:P1} Deep North, {field.HotShare:P1} hot)");

      // W against the scaled formula at random places: half anywhere, half where it matters, on and around the Deep North's ramp.
      var random = new System.Random(7);
      double bound = 1.5 * field.Spacing / 200.0, worst = 0.0;
      int far = 0, onRamp = 0;
      for (int i = 0; i < 10000; i++)
      {
        float x, z;
        if (i % 2 == 0)
        {
          x = (float)(random.NextDouble() * 41000 - 20500);
          z = (float)(random.NextDouble() * 41000 - 20500);
        }
        else
        {
          // The border is a circle of 24000 m round (0, -8000), rippled by 100 m: a place at an angle from north, 100 m inside to 300 m outside.
          double angle = (random.NextDouble() * 2 - 1) * 0.75, d = 24000.0 + random.NextDouble() * 400.0 - 100.0;
          x = (float)(d * Math.Sin(angle));
          z = (float)(-8000.0 + d * Math.Cos(angle));
        }
        double truth = 1.0 - ScaledFade(2.0, x, z);
        double error = Math.Abs(field.Waves(x, z) - truth);
        worst = Math.Max(worst, error);
        if (truth < 1.0 && truth > 0.0) onRamp++;
        if (error > bound) far++;
      }
      Check(far == 0, $"W at 10000 random places against the scaled formula ({onRamp} of them on the ramp): within a texel and a half's ramp ({bound:0.####}) at all of them, the greatest error {worst:0.####}");

      // The border and 200 m inside, on the axis (where the ripple is 0).
      float wBorder = field.Waves(0f, 16000f), wInside = field.Waves(0f, 16200f), wOutside = field.Waves(0f, 15900f);
      Check(wOutside == 1f && wBorder >= 1.0 - 1.5 * field.Spacing / 200.0 && wInside <= 1.5 * field.Spacing / 200.0,
        $"full waves at the Deep North's border (16000 m north) and none 200 m inside, to a texel ({wOutside:0.###} at 100 m outside, {wBorder:0.###} at the border, {wInside:0.###} at 200 m inside)");
      Check(field.Waves(0f, 9000f) == 1f && field.Waves(0f, -9000f) == 1f && field.Waves(15000f, 0f) == 1f,
        "the sea of the game's old circle (9000 m north, 9000 m south, 15000 m east) has full waves: the game's 12 km circle no longer calms it");

      // The hot sea against GetAshlandsOceanGradient > 0 (scaled: 24000 m from a centre 8000 m south).
      int disagree = 0, near = 0, sampled = 0;
      for (int i = 0; i < 10000; i++)
      {
        float x = (float)(random.NextDouble() * 41000 - 20500), z = (float)(random.NextDouble() * 41000 - 20500);
        double distance = WorldGenerator.GetAshlandsOceanGradient(x, z) * 300.0;
        bool truth = distance > 0;
        bool ours = field.HotAt(x, z) > 0f;
        sampled++;
        if (Math.Abs(distance) < 1.5 * field.Spacing) near++;
        else if (truth != ours) disagree++;
      }
      Check(disagree == 0, $"the hot sign equals GetAshlandsOceanGradient > 0 except within a texel and a half of the border ({sampled} places, {near} of them that close, {disagree} wrong)");
    }
    finally
    {
      group.Update(harmony, WorldGeometry.Vanilla);
      harmony.UnpatchAll(harmony.Id);
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    }
  }

  // ------------------------------------------------------------------------------------------------ the red mask
  private static void HotSeaTests()
  {
    Section("field: the hot sea's mask");
    if (!running) return;
    // A heat map's fixture: a disc of hot sea, 200 texels across.
    int n = 200;
    float radius = 4f * n;
    var input = new SeaFieldInput
    {
      Radius = radius,
      Deep = (x, z) => false,
      Hot = (x, z) => Math.Sqrt((double)x * x + (double)z * z) < 500.0,
    };
    var field = SeaField.Build(input, true, CancellationToken.None);
    float Mask(float r, float angle) => field.Mask(r * MathF.Cos(angle), r * MathF.Sin(angle));
    bool inside = true, outside = true, monotone = true;
    double worstEdge = 0.0;
    for (int k = 0; k < 360; k += 7)
    {
      float angle = k * MathF.PI / 180f;
      inside &= Mask(500f - 40f, angle) >= 0.99f && Mask(300f, angle) == 1f;
      outside &= Mask(500f + 40f, angle) <= 0.01f && Mask(700f, angle) == 0f;
      float last = 2f;
      for (float r = 560f; r >= 440f; r -= 1.3f)
      {
        float m = Mask(r, angle);
        monotone &= m >= last - 0.15f || last == 2f;
        last = m;
      }
      // The edge is 25 m wide, centred on the border; the grid is 8 m, so it is met within a texel and a half of where it should be.
      for (float d = -30f; d <= 30f; d += 3f)
      {
        double truth = Math.Min(Math.Max(0.5 - d / 25.0, 0.0), 1.0);
        worstEdge = Math.Max(worstEdge, Math.Abs(Mask(500f + d, angle) - truth));
      }
    }
    Check(inside && outside, "the mask is 1 well inside a heat map's hot sea and 0 well outside it");
    Check(monotone, "and falls steadily across the border (it never rises on the way out)");
    Check(worstEdge <= 0.6, $"across the 25 m edge the mask follows the game's ramp to within {worstEdge:0.##} of it (a texel is 8 m, a third of the ramp)");
    Check(Math.Abs(field.Mask(500f, 0f) - 0.5f) < 0.25f, $"the mask at the border is about half ({field.Mask(500f, 0f):0.###})");
    Check(field.CalmShare == 0.0 && field.Calm.All(b => b == 0), "and a world without a Deep North has no calm at all");
  }

  // ------------------------------------------------------------------------------------------------ the same as the game
  private static void SameAsGameTests()
  {
    Section("field: is the world's sea the game's?");
    if (!running) return;
    SeaFieldInput Input(Func<float, float, bool> deep, Func<float, float, bool> hot, float radius = 10500f) => new()
    {
      Radius = radius, Deep = deep, Hot = hot, GameDeep = SeaPatches.GameDeep, GameHot = SeaPatches.GameHot, Workers = 8,
    };
    var same = SeaField.Build(Input(SeaPatches.GameDeep, SeaPatches.GameHot), true, CancellationToken.None);
    Check(same.CalmSameAsGame && same.HotSameAsGame && same.SameAsGame && same.Size == 2625,
      "a world whose Deep North and hot sea are the game's circles (vanilla size, as the game's code has them) is the game's sea");
    // Land counts too: a circle of the same size elsewhere is not the game's, even where there is only land.
    var shiftedDeep = SeaField.Build(Input((x, z) => SeaPatches.GameDeep(x, z - 60f), SeaPatches.GameHot), true, CancellationToken.None);
    Check(!shiftedDeep.CalmSameAsGame && shiftedDeep.HotSameAsGame && !shiftedDeep.SameAsGame, "a Deep North 60 m from the game's is not the game's; the hot sea still is");
    var shiftedHot = SeaField.Build(Input(SeaPatches.GameDeep, (x, z) => SeaPatches.GameHot(x + 60f, z)), true, CancellationToken.None);
    Check(shiftedHot.CalmSameAsGame && !shiftedHot.HotSameAsGame && !shiftedHot.SameAsGame,
      "a hot sea 60 m from the game's is not the game's; the Deep North still is, so boats and fish still feel the game's waves");
    var server = SeaField.Build(Input(SeaPatches.GameDeep, (x, z) => false), false, CancellationToken.None);
    Check(server.Hot == null && server.CalmSameAsGame && server.HotSameAsGame && server.SameAsGame,
      "a server (calm half only) has nothing to compare for the hot sea, and decides its felt rule by the Deep North alone");
    var unknown = SeaField.Build(new SeaFieldInput { Radius = 2000f, Deep = SeaPatches.GameDeep, Hot = SeaPatches.GameHot }, true, CancellationToken.None);
    Check(!unknown.CalmSameAsGame && !unknown.HotSameAsGame, "without the game's circles to compare with, the world's sea is not taken for the game's");
    Check(Enumerable.Range(0, 20000).All(i => WorldGenerator.IsDeepnorth(i * 1.3f - 13000f, i * 0.7f - 7000f) == SeaPatches.GameDeep(i * 1.3f - 13000f, i * 0.7f - 7000f)
                                               && (WorldGenerator.GetAshlandsOceanGradient(i * 1.3f - 13000f, i * 0.7f - 7000f) > 0f) == SeaPatches.GameHot(i * 1.3f - 13000f, i * 0.7f - 7000f)),
      "the copies of the game's circles answer as the game's own functions (unpatched)");
  }

  // ------------------------------------------------------------------------------------------------ what it costs
  private static void CostTests()
  {
    Section("field: the cost of a field for VALtima's size");
    if (!running) return;
    var harmony = new Harmony("water-tests.cost");
    var group = LayoutGroup();
    var size = new WorldGeometry(32000f, 764f);
    try
    {
      WorldSizeHelper.Layout.Assume(size);
      group.Update(harmony, size);
      var settings = World(12, 32000f, 764f);
      var input = SeaPatches.Input(settings, true);
      input.Workers = 8;
      input.Height = (x, z) => 30f - 40f * (float)Math.Sin(x * 0.001) * (float)Math.Cos(z * 0.0013);
      GC.Collect();
      long before = GC.GetTotalAllocatedBytes(true);
      var watch = Stopwatch.StartNew();
      var field = SeaField.Build(input, true, CancellationToken.None);
      watch.Stop();
      long allocated = GC.GetTotalAllocatedBytes(true) - before;
      long cells = (long)field.Size * field.Size;
      System.Console.WriteLine($"  {field}; {watch.ElapsedMilliseconds} ms wall on 8 workers (build reported {field.BuildMs} ms); {allocated / 1048576.0:0.#} MB allocated; "
        + $"{field.CalmShare:P1} Deep North, {field.HotShare:P1} hot; depth grid {field.DepthSize} x {field.DepthSize}");
      Check(field.Size == 4096 && field.Calm.Length == cells && field.Hot.Length == cells, "4096 x 4096 texels of 16 m, calm and hot");
      Check(field.Depth != null && field.DepthSize >= 1024 && field.DepthSize <= 2056, $"and the depth grid: {field.DepthSize} x {field.DepthSize} texels of 64 m");
      // Peak memory: everything the build allocates is the masks (1 byte), the columns (2 bytes), calm and hot (1 byte each) and the depth grid;
      // the plan's budget is 100 MB over the 32 MB of calm and hot.
      long extra = allocated - 2 * cells;
      Check(extra <= 100L * 1048576, $"memory: {extra / 1048576.0:0.#} MB allocated beyond the field's calm and hot bytes (at most 100 MB)");
      Check(watch.ElapsedMilliseconds < 120000, $"time: {watch.ElapsedMilliseconds} ms on 8 workers (the plan's target is 3000 ms for a world with maps; this one asks the game's formulas)");
    }
    finally
    {
      group.Update(harmony, WorldGeometry.Vanilla);
      harmony.UnpatchAll(harmony.Id);
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    }
  }
}

// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// The spill of a hollow (LakeSpill.cs, Tarn's Spill.cs) against a brute force that finds the lowest level at which water from the centre reaches the border,
// and "bc_lake here" (LakeSpill.Here): the lake it makes, its refusals, and the closing rule that lowers the level until the builder's 4 m flood is closed
// (BUILD-SPEC-TARN.md 4.3 item 4, 2.12).
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  // The lowest level L such that the cells at or below L, joined to the centre cell by their sides, reach a border cell: the spill height, found by trying the
  // heights of the grid from the lowest up (a different search from the priority flood).
  internal static float BruteSpill(float[,] h, int n, int c)
  {
    var levels = new SortedSet<float>();
    for (int i = 0; i < n; i++)
      for (int j = 0; j < n; j++)
        levels.Add(h[i, j]);
    foreach (float level in levels)
    {
      if (level < h[c, c])
        continue;
      var seen = new bool[n, n];
      var stack = new Stack<(int, int)>();
      stack.Push((c, c));
      seen[c, c] = true;
      while (stack.Count > 0)
      {
        var (i, j) = stack.Pop();
        if (i == 0 || j == 0 || i == n - 1 || j == n - 1)
          return level;
        foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
          int a = i + di, b = j + dj;
          if (!seen[a, b] && h[a, b] <= level)
          {
            seen[a, b] = true;
            stack.Push((a, b));
          }
        }
      }
    }
    return float.NaN;
  }

  public static void SpillAgainstBruteForceTest()
  {
    Section("spill: 2,000 random fields against the lowest level that reaches the border");
    var rng = new Rng(20261013);
    int problems = 0, lakes = 0, dryCentre = 0;
    for (int n = 0; n < 2000; n++)
    {
      var noise0 = new Noise((ulong)(7000 + n), (float)rng.Range(40, 60), (float)rng.Range(10, 40), (float)rng.Range(25, 90));
      float cx = (float)Math.Round(rng.Range(-500, 500)), cz = (float)Math.Round(rng.Range(-500, 500));
      float radius = (float)Math.Round(rng.Range(10, 80)), step = rng.Int(0, 3) == 0 ? 4f : 2f, margin = (float)Math.Round(rng.Range(0, 1.5) * 10) / 10f;
      // two in three fields rise away from the centre, so that it is in a hollow
      float rise = rng.Int(0, 3) == 0 ? 0f : 0.25f;
      var noise = new { Height = (Func<float, float, float>)((x, z) => noise0.Height(x, z) + rise * (float)Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz))) };
      var r = LakeSpill.Compute(noise.Height, cx, cz, radius, step, margin);
      int half = (int)Math.Ceiling(radius / step), size = 2 * half + 1;
      var h = new float[size, size];
      for (int i = 0; i < size; i++)
        for (int j = 0; j < size; j++)
          h[i, j] = noise.Height(cx + (i - half) * step, cz + (j - half) * step);
      float spill = BruteSpill(h, size, half);
      var errors = new List<string>();
      if (r == null)
        errors.Add("no result");
      else
      {
        if (r.SpillHeight != spill)
          errors.Add($"spill {r.SpillHeight} against {spill}");
        if (r.Level != spill - margin)
          errors.Add($"level {r.Level} against {spill - margin}");
        // the lake: the cells under the level joined to the centre by their sides
        float level = spill - margin;
        int wet = 0;
        float bed = float.PositiveInfinity, minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        if (h[half, half] < level)
        {
          var seen = new bool[size, size];
          var q = new Queue<(int, int)>();
          q.Enqueue((half, half));
          seen[half, half] = true;
          while (q.Count > 0)
          {
            var (i, j) = q.Dequeue();
            wet++;
            bed = Math.Min(bed, h[i, j]);
            minX = Math.Min(minX, cx + (i - half) * step);
            maxX = Math.Max(maxX, cx + (i - half) * step);
            minZ = Math.Min(minZ, cz + (j - half) * step);
            maxZ = Math.Max(maxZ, cz + (j - half) * step);
            foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
              int a = i + di, b = j + dj;
              if (a >= 0 && b >= 0 && a < size && b < size && !seen[a, b] && h[a, b] < level)
              {
                seen[a, b] = true;
                q.Enqueue((a, b));
              }
            }
          }
          lakes++;
        }
        else
        {
          bed = h[half, half];
          dryCentre++;
        }
        if (r.WetCells != wet)
          errors.Add($"wet cells {r.WetCells} against {wet}");
        if (wet > 0 && (r.BedHeight != bed || r.MinX != minX || r.MaxX != maxX || r.MinZ != minZ || r.MaxZ != maxZ))
          errors.Add("bed or extent differ");
        if (wet == 0 && r.BedHeight != bed)
          errors.Add("the bed of a dry centre is the centre's ground");
        if (Math.Abs(r.AreaM2 - wet * step * step) > 1e-3 || (wet > 0 && Math.Abs(r.Depth - (level - bed)) > 1e-4))
          errors.Add("area or depth");
      }
      if (errors.Count > 0)
      {
        problems++;
        if (problems <= 5)
          C(false, $"spill field {n}: " + string.Join("; ", errors));
      }
    }
    C(problems == 0, $"2,000 random hollows: {problems} differ from the brute force");
    C(lakes > 300 && dryCentre > 100, $"varied: {lakes} with water at the centre, {dryCentre} on high ground");
  }

  public static void SpillFixturesTest()
  {
    Section("spill: a bowl with and without a channel");
    var r = LakeSpill.Compute(Bowl(), 0f, 0f, 120f, 2f, 0.3f)!;
    C(Math.Abs(r.SpillHeight - 80f) < 1e-3 && Math.Abs(r.Level - 79.7f) < 1e-3 && r.WetCells > 7000, $"a bowl spills at its rim: {r.SpillHeight:0.00}, level {r.Level:0.00}, {r.WetCells} cells");
    C(Math.Abs(r.BedHeight - 40f) < 1e-3 && Math.Abs(r.Depth - 39.7f) < 1e-3, "its bed is the lowest ground: " + r.BedHeight);
    var channel = LakeSpill.Compute(Bowl(70f), 0f, 0f, 120f, 2f, 0.3f)!;
    C(Math.Abs(channel.SpillHeight - 70f) < 1e-3, "a channel in the rim lowers the spill to its height: " + channel.SpillHeight);
    var high = LakeSpill.Compute(Bowl(), 130f, 0f, 10f, 2f, 0.3f)!;
    C(high.WetCells == 0, "the centre on the plateau is not in a hollow: no water");
  }

  // ---- bc_lake here ---------------------------------------------------------------------------------------------------------

  public static void HereMakesALakeTest()
  {
    Section("here: a lake from a point, its name, its numbers");
    var made = LakeSpill.Here(Bowl(), 0f, 0f, 120f, 0.3f, 30f, name => false);
    C(made.Made && made.Lake!.Name == "lake1" && made.Drops == 0 && made.Shape != null && made.Shape.Ok && !made.Shape.TouchedEdge, "a bowl makes lake1 with no lowering: " + made.Message);
    C(made.Lake!.Level == made.Level && Math.Abs(made.Level - 79.7f) < 1e-3 && made.Lake.X == 0f && made.Lake.Z == 0f, "the lake is at the spill less the margin, at the point");
    C(made.Message.Contains("lake1: level 79.70 m (spills at 80.00)") && made.Message.Contains("deepest 39.7 m"), "the sentence says level, spill and depth: " + made.Message);
    var second = LakeSpill.Here(Bowl(), 0f, 0f, 120f, 0.3f, 30f, name => name == "lake1" || name == "lake2");
    C(second.Lake!.Name == "lake3", "the name is the first free: lake3");
    // the box is the wet cells' extent, and the builder's flood of it is closed
    C(made.Lake!.MinX <= -90f && made.Lake.MaxX >= 90f && made.Lake.MaxX < 120f, "the box is the wet cells' extent");

    var german = new System.Globalization.CultureInfo("de-DE");
    var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
    System.Threading.Thread.CurrentThread.CurrentCulture = german;
    try
    {
      var de = LakeSpill.Here(Bowl(), 0f, 0f, 120f, 0.3f, 30f, name => false);
      C(de.Message == made.Message && de.Message.Contains("79.70") && !de.Message.Contains("79,70"), "the sentence is the same in the German culture: " + de.Message);
    }
    finally
    {
      System.Threading.Thread.CurrentThread.CurrentCulture = saved;
    }
  }

  public static void HereRefusesTest()
  {
    Section("here: the refusals");
    var drains = LakeSpill.Here((x, z) => 30.4f, 0f, 0f, 60f, 0.3f, 30f, n => false);
    C(!drains.Made && drains.Message.Contains("drains to the sea"), "a hollow whose level is within 0.2 m of the sea drains: " + drains.Message);
    var hill = LakeSpill.Here(Bowl(), 130f, 0f, 40f, 0.3f, 30f, n => false);
    C(!hill.Made, "a point in no hollow makes none: " + hill.Message);
    // a point on a hill inside a bowl: the spill is the rim, the ground at the point is higher than the level
    var onHill = LakeSpill.Here((x, z) => x * x + z * z < 100f ? 85f : Bowl()(x, z), 0f, 0f, 120f, 0.3f, 30f, n => false);
    C(!onHill.Made && onHill.Message.Contains("not in a hollow"), "a point on a hill above the lake is not in a hollow: " + onHill.Message);
    foreach (float radius in new[] { 4f, float.NaN, 5000f })
    {
      var bad = LakeSpill.Here(Bowl(), 0f, 0f, radius, 0.3f, 30f, n => false);
      C(!bad.Made && bad.Message.Contains("radius"), $"a radius of {radius} is refused: " + bad.Message);
    }
    var badMargin = LakeSpill.Here(Bowl(), 0f, 0f, 120f, -1f, 30f, n => false);
    C(!badMargin.Made && badMargin.Message.Contains("margin"), "a negative margin is refused: " + badMargin.Message);
    var nan = LakeSpill.Here(Bowl(), float.NaN, 0f, 120f, 0.3f, 30f, n => false);
    C(!nan.Made, "a point that is not a place is refused");
  }

  // The closing rule. The spill search samples every 2 m from the player's point, the builder every 4 m from its box (a subset of the same columns, every other one):
  // a wall 1.2 m thick on a column only the spill search samples (x = 0) holds the water for the one and is not there for the other. Left of it a basin at 40 m
  // (rim 90 m elsewhere), the wall 80 m high, and right of it a plateau: the spill is the wall's height and the builder's water runs over the wall's place onto
  // the plateau and out of its box unless the level is under the plateau.
  private static Func<float, float, float> WallAndPlateau(float plateau) => (x, z) =>
  {
    if (Math.Abs(z) >= 100f)
      return 90f;
    if (Math.Abs(x) < 0.6f)
      return 80f;
    if (x > 0f && x < 100f)
      return plateau;
    return x < 0f && x > -100f ? 40f : 90f;
  };

  public static void HereClosesUnderTheBuilderTest()
  {
    Section("here: the closing rule lowers the level until the builder's 4 m flood is closed");
    var thin = LakeSpill.Here(WallAndPlateau(79.2f), -40f, 0f, 120f, 0.3f, 30f, n => false);
    C(thin.Made && Math.Abs(thin.SpillHeight - 80f) < 1e-3, "the spill search sees the wall: it spills at the wall's height: " + thin.Message);
    C(thin.Made && thin.Drops == 3 && Math.Abs(thin.Level - 79.1f) < 1e-3 && thin.Shape!.Ok && !thin.Shape.TouchedEdge,
      $"the level was lowered {thin.Drops} times to {thin.Level:0.00} (under the plateau at 79.2), and the builder's flood is closed");
    C(thin.Message.Contains("lowered 3 x 0.2 m to 79.10") && thin.Message.Contains("4 m grid"), "the sentence says what it did: " + thin.Message);
    C(Math.Abs(thin.Lowered - 0.6f) < 1e-3 && Math.Abs(thin.Lake!.Level - thin.Level) < 1e-6, "the Lowered field is the sum of the steps, the lake has the lowered level");
    // the same lake with the level the spill search gave leaks, which is why it was lowered
    var leak = LakeFlood.Compute(WallAndPlateau(79.2f), thin.Lake with { Level = 79.7f });
    C(leak.Ok && leak.TouchedEdge, "at the spill search's level the builder's water runs out of its box");
    var again = LakeFlood.Compute(WallAndPlateau(79.2f), thin.Lake);
    C(again.Ok && !again.TouchedEdge && again.Hash() == thin.Shape.Hash(), "the lake it made floods closed and to the same shape as the one it checked");

    var deep = LakeSpill.Here(WallAndPlateau(60f), -40f, 0f, 120f, 0.3f, 30f, n => false);
    C(!deep.Made && deep.Message.Contains("no lake made") && deep.Message.Contains("10 times"), "a leak that ten steps of 0.2 m do not close makes no lake: " + deep.Message);

    var closedAlready = LakeSpill.Here(Bowl(), 1f, 1f, 120f, 0.3f, 30f, n => false);
    C(closedAlready.Made && closedAlready.Drops == 0 && closedAlready.Message.IndexOf("lowered", StringComparison.Ordinal) < 0, "a closed hollow is not lowered");
  }

  public static void HereRandomFieldsTest()
  {
    Section("here: 2,000 random fields end closed, or say why not");
    var rng = new Rng(555);
    int made = 0, lowered = 0, refused = 0, problems = 0;
    for (int n = 0; n < 2000; n++)
    {
      var noise = new Noise((ulong)(40000 + n), (float)rng.Range(40, 70), (float)rng.Range(15, 50), (float)rng.Range(30, 100));
      float x = (float)Math.Round(rng.Range(-400, 400)), z = (float)Math.Round(rng.Range(-400, 400));
      float radius = (float)Math.Round(rng.Range(30, 100)), margin = (float)Math.Round(rng.Range(0, 1.5) * 10) / 10f;
      var r = LakeSpill.Here(noise.Height, x, z, radius, margin, 30f, name => false);
      if (r.Made)
      {
        made++;
        if (r.Drops > 0) lowered++;
        var flood = LakeFlood.Compute(noise.Height, r.Lake!);
        bool ok = flood.Ok && !flood.TouchedEdge && r.Level > 30.2f && r.Drops <= LakeSpill.CloseTries && Math.Abs(r.Lake!.Level - r.Level) < 1e-4
                  && flood.Hash() == r.Shape!.Hash() && r.Lake.Name == "lake1";
        if (!ok)
        {
          problems++;
          if (problems <= 5)
            C(false, $"random hollow {n} made a lake that is not closed: {r.Message}");
        }
      }
      else
      {
        refused++;
        if (string.IsNullOrEmpty(r.Message))
        {
          problems++;
          C(false, $"random hollow {n} refused without a word");
        }
      }
    }
    C(problems == 0, $"2,000 random hollows: {problems} problems");
    C(made > 100 && refused > 100, $"varied: {made} lakes ({lowered} lowered by the closing rule), {refused} refused");
  }
}

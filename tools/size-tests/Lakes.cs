// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// LakeMerge (the game's WorldGenerator.MergePoints, found faster): it answers exactly as the game's own code does, on random
// points, points on the 128 m grid FindLakes makes (many of them equally near), duplicates, clusters, points that are no number,
// and the lake points of a made-up sea map, every one compared bit for bit with a copy of the game's code (assembly_valheim
// 1.0.17, client and dedicated server: the same); that a long list of the size a 32 km world finds is merged in seconds; that the
// prefix is bound to the game's method (patched through Harmony here, which refuses a prefix whose parameters do not match); that
// a short list is left to the game; and that the prefix belongs to the groups the mod ships as it should (the layout group, patched
// for real, and no other), so a world that is not laid out to its own size keeps the game's code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
  private static void LakeTests()
  {
    LakeMergeEqualTests();
    LakeMergeSpeedTests();
    LakeMergeBindingTests();
    LakeMergeScopeTests();
  }

  // ------------------------------------------------------------------------------------------------ the game's own code
  // WorldGenerator.MergePoints and FindClosest as 1.0.17 has them, with UnityEngine's own Vector2.
  private static List<Vector2> GameMergePoints(List<Vector2> points, float range)
  {
    List<Vector2> list = new List<Vector2>();
    while (points.Count > 0)
    {
      Vector2 vector = points[0];
      points.RemoveAt(0);
      while (points.Count > 0)
      {
        int num = GameFindClosest(points, vector, range);
        if (num == -1)
        {
          break;
        }
        vector = (vector + points[num]) * 0.5f;
        points[num] = points[points.Count - 1];
        points.RemoveAt(points.Count - 1);
      }
      list.Add(vector);
    }
    return list;
  }

  private static int GameFindClosest(List<Vector2> points, Vector2 p, float maxDistance)
  {
    int result = -1;
    float num = 99999f;
    for (int i = 0; i < points.Count; i++)
    {
      if (!(points[i] == p))
      {
        float num2 = Vector2.Distance(p, points[i]);
        if (num2 < maxDistance && num2 < num)
        {
          result = i;
          num = num2;
        }
      }
    }
    return result;
  }

  private static bool SameLakes(List<Vector2> a, List<Vector2> b) =>
    a.Count == b.Count && a.Zip(b, (p, q) => BitConverter.SingleToInt32Bits(p.x) == BitConverter.SingleToInt32Bits(q.x)
                                           && BitConverter.SingleToInt32Bits(p.y) == BitConverter.SingleToInt32Bits(q.y)).All(same => same);

  // ------------------------------------------------------------------------------------------------ the same lakes
  private static void LakeMergeEqualTests()
  {
    int runs = 0, wrong = 0, lakes = 0, emptied = 0;
    string why = "";
    void Run(string what, List<Vector2> points, float range)
    {
      var forGame = new List<Vector2>(points);
      var forUs = new List<Vector2>(points);
      var game = GameMergePoints(forGame, range);
      var ours = LakeMerge.Merge(forUs, range);
      runs++;
      lakes += game.Count;
      if (forUs.Count == 0 && forGame.Count == 0)
        emptied++;
      if (ours == null || !SameLakes(game, ours) || forUs.Count != 0)
      {
        wrong++;
        if (why == "")
          why = $"{what}: {points.Count} points, range {range}: the game {game.Count} lakes, ours {ours?.Count.ToString() ?? "null"}";
      }
    }

    var random = new System.Random(2026);
    // Random clouds: sizes, extents (dense to sparse) and ranges.
    foreach (int n in new[] { 0, 1, 2, 3, 7, 40, 300, 2500 })
      foreach (float extent in new[] { 50f, 2000f, 30000f })
        foreach (float range in new[] { 800f, 128f, 5000f, 1f })
        {
          var points = new List<Vector2>();
          for (int i = 0; i < n; i++)
            points.Add(new Vector2((float)(random.NextDouble() * 2 - 1) * extent, (float)(random.NextDouble() * 2 - 1) * extent));
          Run("random", points, range);
        }

    // The grid FindLakes walks, every 128 m within a circle (many points are equally near each other), rows first as the game lists them,
    // and the same shuffled.
    foreach (var (radius, range) in new[] { (1000f, 800f), (2500f, 800f), (2500f, 256f), (4000f, 800f), (4000f, 130f), (4000f, 127.9f), (8000f, 800f) })
    {
      var grid = new List<Vector2>();
      for (float z = -radius; z <= radius; z = (float)(z + 128.0))
        for (float x = -radius; x <= radius; x = (float)(x + 128.0))
          if (new Vector2(x, z).magnitude <= radius)
            grid.Add(new Vector2(x, z));
      Run($"grid of {radius}", grid, range);
      var shuffled = grid.OrderBy(_ => random.Next()).ToList();
      Run($"shuffled grid of {radius}", shuffled, range);
      // Only the points of a made-up sea: where a smooth function of the position is low, as a map with a coast and islands has them.
      var sea = grid.Where(p => Math.Sin(p.x / 700.0) + Math.Cos(p.y / 900.0) + 0.5 * Math.Sin((p.x + p.y) / 311.0) < 0.6).ToList();
      Run($"sea in a grid of {radius}", sea, range);
    }

    // Duplicates and near duplicates (Vector2's == is approximate), clusters, negative zero, and points that are no number or no end.
    var odd = new List<Vector2>();
    for (int i = 0; i < 60; i++)
    {
      var p = new Vector2((float)random.NextDouble() * 3000f - 1500f, (float)random.NextDouble() * 3000f - 1500f);
      odd.Add(p);
      odd.Add(p);
      odd.Add(p + new Vector2(1e-6f, -1e-6f));
    }
    for (int i = 0; i < 200; i++)
      odd.Add(new Vector2(500f + (float)random.NextDouble() * 20f, -300f + (float)random.NextDouble() * 20f));
    odd.Add(new Vector2(-0f, 0f));
    odd.Add(new Vector2(0f, -0f));
    odd.Add(new Vector2(float.NaN, 0f));
    odd.Add(new Vector2(0f, float.NaN));
    odd.Add(new Vector2(float.PositiveInfinity, 3f));
    odd.Add(new Vector2(float.NegativeInfinity, float.PositiveInfinity));
    odd.Add(new Vector2(float.MaxValue, float.MinValue));
    odd.Add(new Vector2(1e30f, 1e30f));
    odd = odd.OrderBy(_ => random.Next()).ToList();
    foreach (float range in new[] { 800f, 64f, 20f })
      Run("duplicates, clusters and no-number points", odd, range);
    Run("a first point that is no number", [new Vector2(float.NaN, float.NaN), new Vector2(1f, 1f), new Vector2(2f, 2f)], 800f);

    // Two points just inside the range (796 m for 800 m) in every direction and at every place across the cells' borders (a cell is 808 m: a
    // pair 796 m apart is in neighbouring cells, and in cells two apart if the cells are narrower than the range).
    foreach (double angle in new[] { 0.0, 30.0, 45.0, 60.0, 90.0, 135.0 })
      for (float x = 0f; x < 1700f; x += 0.7f)
      {
        float dx = (float)(796.0 * Math.Cos(angle * Math.PI / 180.0)), dy = (float)(796.0 * Math.Sin(angle * Math.PI / 180.0));
        Run($"a pair 796 m apart at {angle} degrees from x = {x}", [new Vector2(x, x * 0.37f), new Vector2(x + dx, x * 0.37f + dy)], 800f);
      }

    // A range the game's search cannot use as it is: left to the game's own code (null).
    foreach (float bad in new[] { 0f, -1f, float.NaN, 99999f, float.PositiveInfinity })
      Check(LakeMerge.Merge([new Vector2(0f, 0f), new Vector2(1f, 1f)], bad) == null, $"a range of {bad}: not ours to answer");

    Check(wrong == 0, $"the lakes are the game's, bit for bit, on {runs} lists ({lakes:N0} lakes, {emptied} lists emptied as the game's code empties them){(wrong == 0 ? "" : $": {wrong} wrong, the first {why}")}");
  }

  // ------------------------------------------------------------------------------------------------ the time
  private static void LakeMergeSpeedTests()
  {
    // The lake points of a world of 32264 m on a map that is mostly sea: every 128 m within the radius, 85% of them.
    var points = new List<Vector2>();
    var random = new System.Random(5);
    const float radius = 32264f;
    for (float z = -radius; z <= radius; z = (float)(z + 128.0))
      for (float x = -radius; x <= radius; x = (float)(x + 128.0))
        if (new Vector2(x, z).magnitude <= radius && random.NextDouble() < 0.857)
          points.Add(new Vector2(x, z));
    int n = points.Count;
    var watch = Stopwatch.StartNew();
    var lakes = LakeMerge.Merge(points, 800f);
    long ms = watch.ElapsedMilliseconds;
    Check(lakes != null && points.Count == 0 && lakes.Count > 100 && lakes.Count < n / 10 && ms < 30000,
      $"{n:N0} lake points (a 32264 m world, mostly sea) merged into {lakes?.Count:N0} lakes in {ms:N0} ms; the game's own takes minutes");
    System.Console.WriteLine($"    {n:N0} points -> {lakes?.Count:N0} lakes in {ms} ms");
  }

  // ------------------------------------------------------------------------------------------------ bound to the game
  private static void LakeMergeBindingTests()
  {
    var method = (MethodInfo)Target(typeof(WorldGenerator), "MergePoints");
    var parameters = method?.GetParameters();
    Check(method != null && !method.IsStatic && method.ReturnType == typeof(List<Vector2>) && parameters.Length == 2
          && parameters[0].Name == "points" && parameters[0].ParameterType == typeof(List<Vector2>)
          && parameters[1].Name == "range" && parameters[1].ParameterType == typeof(float),
      "WorldGenerator.MergePoints(List<Vector2> points, float range) -> List<Vector2> is in the installed game (the dedicated server's has the same)");

    // Patched as the layout group patches it (Harmony refuses a prefix whose parameters it cannot bind), and taken off again.
    var harmony = new Harmony("size-tests.lakes");
    var group = new WorldSizeHelper.Group(new WorldSizeHelper.Part(() => method, prefix: "MergePointsPrefix"));
    int mark = CapturingLogHandler.Lines.Count;
    try
    {
      var size = new WorldGeometry(32264f, 500f);
      Check(group.Update(harmony, size) && group.Patched, "World Size 32264: the merge's part is patched");
      var info = Harmony.GetPatchInfo(method);
      Check(info != null && info.Prefixes.Count(p => p.owner == harmony.Id) == 1, "the game's MergePoints has the prefix");
      Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("could not")), "no part failed to patch");
    }
    catch (Exception e)
    {
      Check(false, $"patching MergePoints: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      group.Update(harmony, WorldGeometry.Vanilla);
      var info = Harmony.GetPatchInfo(method);
      Check(info == null || info.Prefixes.Count(p => p.owner == harmony.Id) == 0, "vanilla's size again: the prefix is off");
      harmony.UnpatchAll(harmony.Id);
    }

    // The prefix itself: a long list is answered (and the game's code skipped), a short one is left to the game.
    var prefix = typeof(WorldSizeHelper).GetMethod("MergePointsPrefix", BindingFlags.NonPublic | BindingFlags.Static);
    var random = new System.Random(9);
    var many = new List<Vector2>();
    for (int i = 0; i < LakeMerge.FastAbove + 500; i++)
      many.Add(new Vector2((float)random.NextDouble() * 4000f, (float)random.NextDouble() * 4000f));
    var expected = GameMergePoints(new List<Vector2>(many), 800f);
    var arguments = new object[] { many, 800f, null };
    var skipGame = (bool)prefix.Invoke(null, arguments);
    Check(!skipGame && arguments[2] is List<Vector2> answer && SameLakes(expected, answer), "a long list: the prefix answers with the game's lakes and skips the game's code");
    var few = new List<Vector2>();
    for (int i = 0; i < 50; i++)
      few.Add(new Vector2((float)random.NextDouble() * 4000f, (float)random.NextDouble() * 4000f));
    var fewArguments = new object[] { few, 800f, null };
    Check((bool)prefix.Invoke(null, fewArguments) && fewArguments[2] == null && few.Count == 50, "a short list: the game's own code runs, the list untouched");
    var badArguments = new object[] { new List<Vector2>(many), float.NaN, null };
    Check((bool)prefix.Invoke(null, badArguments) && badArguments[2] == null, "a range that is no number: the game's own code runs");
  }

  // ------------------------------------------------------------------------------------------------ which worlds get it
  // The prefix is a part of the layout group (WorldSizeHelper.Layout): on for a world made since 0.10 whose own size is not vanilla's, while
  // Expand World Size is not installed. The two other groups are on for every world of any other size than vanilla's, one made before 0.10
  // among them, which keeps the game's own code. These look at the groups the mod ships, not at a group built here.
  private static int mergeCalls;
  private static void CountMerges() => mergeCalls++;

  private static void LakeMergeScopeTests()
  {
    var method = (MethodInfo)Target(typeof(WorldGenerator), "MergePoints");
    var partsField = typeof(WorldSizeHelper.Group).GetField("parts", BindingFlags.NonPublic | BindingFlags.Instance)!;
    List<string> Names(WorldSizeHelper.Group group) => ((WorldSizeHelper.Part[])partsField.GetValue(group)!).Select(p => p.ToString()).ToList();
    Check(Names(WorldSizeHelper.Layout).Count(n => n == "WorldGenerator.MergePoints") == 1, "the layout group has a part on WorldGenerator.MergePoints");
    Check(!Names(WorldSizeHelper.EdgeChecks).Contains("WorldGenerator.MergePoints") && !Names(WorldSizeHelper.WorldSize).Contains("WorldGenerator.MergePoints"),
      "and neither the edge checks (every size but vanilla's) nor the Ashlands' limit have one");

    // The layout group, as shipped, patched for the size of a 32 km world on a real Harmony: the game's own MergePoints, called as the game
    // calls it, goes through the prefix for a long list (LakeMerge.Merge is called), and runs its own code for a short one (it is not).
    var harmony = new Harmony("size-tests.lakes-scope");
    var instance = RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    List<Vector2> Call(List<Vector2> points) => (List<Vector2>)method.Invoke(instance, [points, 800f])!;
    var random = new System.Random(31);
    var many = new List<Vector2>();
    // 5000 points, whatever FastAbove is (the game's own code takes a fraction of a second for them; it would take minutes for 200,000).
    for (int i = 0; i < 5000; i++)
      many.Add(new Vector2((float)random.NextDouble() * 4000f, (float)random.NextDouble() * 4000f));
    var few = many.Take(200).ToList();
    var expectedMany = GameMergePoints(new List<Vector2>(many), 800f);
    var expectedFew = GameMergePoints(new List<Vector2>(few), 800f);
    int mark = CapturingLogHandler.Lines.Count;
    try
    {
      harmony.Patch(AccessTools.Method(typeof(LakeMerge), "Merge"), prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(CountMerges), BindingFlags.NonPublic | BindingFlags.Static)));
      mergeCalls = 0;
      var unpatched = Call(new List<Vector2>(many));
      Check(mergeCalls == 0 && SameLakes(expectedMany, unpatched), "vanilla's size (the layout group off): a long list goes through the game's own code");
      var size = new WorldGeometry(32264f, 500f);
      Check(WorldSizeHelper.Layout.Update(harmony, size) && WorldSizeHelper.Layout.Patched && Harmony.GetPatchInfo(method) is { } info && info.Prefixes.Count(p => p.owner == harmony.Id) == 1,
        "World Size 32264: the shipped layout group puts the prefix on the game's MergePoints");
      mergeCalls = 0;
      var longList = new List<Vector2>(many);
      var viaPrefix = Call(longList);
      Check(mergeCalls == 1 && SameLakes(expectedMany, viaPrefix) && longList.Count == 0, "a long list is answered by LakeMerge, with the game's lakes");
      mergeCalls = 0;
      var shortList = new List<Vector2>(few);
      var viaGame = Call(shortList);
      Check(mergeCalls == 0 && SameLakes(expectedFew, viaGame) && shortList.Count == 0, "a short list is the game's own code, with the game's lakes");
      Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("MergePoints")), "no part on MergePoints failed to patch");
      Check(WorldSizeHelper.Layout.Update(harmony, WorldGeometry.Vanilla) && !WorldSizeHelper.Layout.Patched, "vanilla's size again: the layout group is off");
      mergeCalls = 0;
      Call(new List<Vector2>(many));
      Check(mergeCalls == 0 && (Harmony.GetPatchInfo(method)?.Prefixes.Count(p => p.owner == harmony.Id) ?? 0) == 0, "and the game's own code runs for a long list again");
    }
    catch (Exception e)
    {
      Check(false, $"the layout group patched for real: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      WorldSizeHelper.Layout.Update(harmony, WorldGeometry.Vanilla);
      harmony.UnpatchAll(harmony.Id);
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    }

    // The prefix answers a list of 5000 points whatever the threshold's own value is (the speed test above asks for FastAbove + 500).
    var prefix = typeof(WorldSizeHelper).GetMethod("MergePointsPrefix", BindingFlags.NonPublic | BindingFlags.Static)!;
    var five = new List<Vector2>();
    for (int i = 0; i < 5000; i++)
      five.Add(new Vector2((float)random.NextDouble() * 6000f, (float)random.NextDouble() * 6000f));
    var arguments = new object[] { five, 800f, null };
    Check(!(bool)prefix.Invoke(null, arguments)! && arguments[2] is List<Vector2> { Count: > 0 }, "5000 points: LakeMerge answers, the game's code is skipped (the threshold is no higher than that)");
  }
}

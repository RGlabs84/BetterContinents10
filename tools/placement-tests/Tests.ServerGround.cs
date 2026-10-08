// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BakedGround (spec 8.1, 8.3, 14.1 "Ground"): the layer's 1 m ground against VALtimaOnline's TownGround rule at every vertex of VALtima's
// ground zones, bilinear between vertices, a weight of 0 and a zone without ground leaving the world's height, the postfix on many
// threads while the layer is swapped, the postfix stepping aside, and what a change of ground does to the loaded terrain.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // VALtimaOnline's per-vertex rule (Towns/TownGround.cs:76-83): a weight of 0 leaves the terrain's height, 255 is the town's, anything
  // between blends them (Mathf.Lerp(old, ground, w / 255)).
  private static double TownGroundRule(double old, double ground, int weight) =>
    weight == 0 ? old : weight == 255 ? ground : old + (ground - old) * (weight / 255.0);

  // A zone's ground vertex in the world's metres, as VALtimaOnline's own payload says it (base plus centimetres).
  private static double VertexHeight(ZoneGround ground, int vertex) => ground.Base + ground.Heights[vertex] / 100.0;

  public static void ServerGroundVertexRuleTest()
  {
    Section("ground: every vertex of VALtima's ground zones gives TownGround's rule within 1 mm");
    var layer = ServerValtima(nameof(ServerGroundVertexRuleTest));
    if (layer == null)
      return;
    var ground = layer.Ground;
    C(ground.Count == 1354, $"VALtima's file has 1,354 zones with ground ({ground.Count})");
    long checkedVertices = 0, wrong = 0, atBorder = 0, borderDisagree = 0, changed = 0;
    double worst = 0;
    string first = null;
    foreach (var zone in ground.Zones.OrderBy(z => z.Z).ThenBy(z => z.X))
    {
      ground.TryGet(zone, out var zg);
      double x0 = zone.OriginX, z0 = zone.OriginZ;
      for (int iz = 0; iz < BakedFormat.GroundSide; iz++)
        for (int ix = 0; ix < BakedFormat.GroundSide; ix++)
        {
          int i = iz * BakedFormat.GroundSide + ix;
          // The terrain's own height here, some number between 20 and 60 m that differs from vertex to vertex.
          float old = 20f + (i * 37 % 400) / 10f;
          double wx = x0 + ix, wz = z0 + iz;
          var owner = ZoneKey.OfPoint(wx, wz);
          double expected;
          if (owner == zone)
          {
            expected = TownGroundRule(old, VertexHeight(zg, i), zg.Weights[i]);
            if (zg.Weights[i] != 0)
              changed++;
          }
          else
          {
            // The last row and column of a zone's grid are the next zone's first: a position there is asked of that zone, and of the
            // world's own ground where it has none.
            atBorder++;
            int vertex = (iz == BakedFormat.GroundSide - 1 ? 0 : iz) * BakedFormat.GroundSide + (ix == BakedFormat.GroundSide - 1 ? 0 : ix);
            if (ground.TryGet(owner, out var next))
            {
              expected = TownGroundRule(old, VertexHeight(next, vertex), next.Weights[vertex]);
              if (Math.Abs(VertexHeight(next, vertex) - VertexHeight(zg, i)) > 0.0105 || next.Weights[vertex] != zg.Weights[i])
                borderDisagree++;
            }
            else
            {
              expected = old;
              if (zg.Weights[i] != 0)
                borderDisagree++;
            }
          }
          double got = BakedGround.Apply(ground, wx, wz, old);
          double off = Math.Abs(got - expected);
          worst = Math.Max(worst, off);
          checkedVertices++;
          if (off > 0.001)
          {
            wrong++;
            first ??= $"zone {zone} vertex ({ix}, {iz}): {got:F4} against {expected:F4}";
          }
        }
    }
    C(checkedVertices == 1354L * 65 * 65, $"every vertex of every ground zone was looked at ({checkedVertices:N0})");
    C(wrong == 0, $"each is within 1 mm of TownGround's rule (worst {worst * 1000:F3} mm; {wrong} wrong; first: {first})");
    C(changed > 1_000_000, $"and the ground moves the terrain at many of them ({changed:N0} vertices with a weight over 0)");
    System.Console.WriteLine($"   ground: {atBorder:N0} vertices on a zone's last row or column; {borderDisagree} of them differ from the zone beside (the owning zone's answer stands)");
  }

  public static void ServerGroundBilinearTest()
  {
    Section("ground: between the vertices height and weight are bilinear");
    var layer = ServerValtima(nameof(ServerGroundBilinearTest));
    if (layer == null)
      return;
    var ground = layer.Ground;
    var zones = ground.Zones.OrderBy(z => z.Z).ThenBy(z => z.X).ToList();
    var random = new System.Random(20261007);
    int wrong = 0, between = 0;
    double worst = 0;
    for (int n = 0; n < 20000; n++)
    {
      var zone = zones[random.Next(zones.Count)];
      ground.TryGet(zone, out var zg);
      // Inside the zone's own cells (0 to 64 in each direction), at a fraction of a metre.
      double lx = random.NextDouble() * 64.0, lz = random.NextDouble() * 64.0;
      int ix = (int)Math.Floor(lx), iz = (int)Math.Floor(lz);
      double fx = lx - ix, fz = lz - iz;
      int v00 = iz * 65 + ix;
      double Mix(Func<int, double> value) => (value(v00) * (1 - fx) + value(v00 + 1) * fx) * (1 - fz) + (value(v00 + 65) * (1 - fx) + value(v00 + 66) * fx) * fz;
      double h = Mix(v => VertexHeight(zg, v)), w = Mix(v => zg.Weights[v] / 255.0);
      double old = 31.25;
      double expected = old + (h - old) * w;
      double got = BakedGround.Apply(ground, zone.OriginX + lx, zone.OriginZ + lz, (float)old);
      worst = Math.Max(worst, Math.Abs(got - expected));
      if (Math.Abs(got - expected) > 0.002)
        wrong++;
      if (fx > 0.01 && fx < 0.99 && fz > 0.01 && fz < 0.99)
        between++;
    }
    C(wrong == 0, $"20,000 places inside the zones are within 2 mm of the bilinear height blended by the bilinear weight (worst {worst * 1000:F3} mm; {wrong} wrong; {between} between vertices)");

    // No step. Where the formula could break - across the edge between two cells, and across the edge between two rows of cells - a place a
    // micrometre either side of the edge gives the same height to within the ground's own slope (a hard edge of the town's weight, 0 to 255 over
    // 1 m, against ground 100 m off the world's, is 100 m per metre: 0.0002 over 2 micrometres).
    int steps = 0;
    double steepest = 0;
    for (int n = 0; n < 5000; n++)
    {
      var zone = zones[random.Next(zones.Count)];
      int edge = 1 + random.Next(63);
      double along = 0.5 + random.NextDouble() * 63;
      bool acrossX = (n & 1) == 0;
      double ex = zone.OriginX + (acrossX ? edge : along), ez = zone.OriginZ + (acrossX ? along : edge);
      double a = BakedGround.Apply(ground, acrossX ? ex - 1e-6 : ex, acrossX ? ez : ez - 1e-6, 40f);
      double b = BakedGround.Apply(ground, acrossX ? ex + 1e-6 : ex, acrossX ? ez : ez + 1e-6, 40f);
      steepest = Math.Max(steepest, Math.Abs(a - b) / 2e-6);
      if (Math.Abs(a - b) > 1e-3)
        steps++;
    }
    C(steps == 0, $"the ground is continuous across the edges of its cells: no step over 1 mm of height at 5,000 edges (steepest slope {steepest:F1} m per metre)");
  }

  public static void ServerGroundOutsideTest()
  {
    Section("ground: a weight of 0 and a zone without ground leave the world's height");
    var layer = ServerValtima(nameof(ServerGroundOutsideTest));
    if (layer == null)
      return;
    var ground = layer.Ground;
    C(BakedGround.Apply(ground, 0, 0, 77.5f) == 77.5f && BakedGround.Apply(ground, 9000, -9000, -2f) == -2f, "far from every town the height is the world's, exactly");
    C(BakedGround.Apply(ground, 1e7, 1e7, 12f) == 12f && BakedGround.Apply(GroundSet.Empty, -9509.6, 750.9, 12f) == 12f, "and out of the layer's range, and with no ground at all");
    // A vertex with weight 0 in a ground zone: the world's height, exactly.
    int zero = 0, tested = 0;
    foreach (var zone in ground.Zones.Take(200))
    {
      ground.TryGet(zone, out var zg);
      for (int i = 0; i < zg.Weights.Length && tested < 3000; i++)
        if (zg.Weights[i] == 0 && i % 65 < 64 && i / 65 < 64)
        {
          tested++;
          if (BakedGround.Apply(ground, zone.OriginX + i % 65, zone.OriginZ + i / 65, 33.3f) == 33.3f)
            zero++;
        }
    }
    C(tested > 100 && zero == tested, $"a vertex of weight 0 gives the world's height exactly ({zero} of {tested})");
    // A weight of 255 gives the town's height, whatever the world's was.
    ZoneKey full = default;
    int fullVertex = -1;
    foreach (var zone in ground.Zones)
    {
      ground.TryGet(zone, out var zg);
      int v = Array.FindIndex(zg.Weights, w => w == 255);
      if (v >= 0 && v % 65 < 64 && v / 65 < 64)
      {
        full = zone;
        fullVertex = v;
        break;
      }
    }
    C(fullVertex >= 0, "(a vertex of weight 255 was found)");
    if (fullVertex >= 0)
    {
      ground.TryGet(full, out var fg);
      double town = VertexHeight(fg, fullVertex);
      C(Math.Abs(BakedGround.Apply(ground, full.OriginX + fullVertex % 65, full.OriginZ + fullVertex / 65, -50f) - town) < 0.001
        && Math.Abs(BakedGround.Apply(ground, full.OriginX + fullVertex % 65, full.OriginZ + fullVertex / 65, 400f) - town) < 0.001,
        "a vertex of weight 255 gives the town's height whatever the world's was");
    }
  }

  public static void ServerGroundPostfixTest()
  {
    Section("ground: the postfix reads the store, steps aside while paused, and nests");
    var layer = ServerValtima(nameof(ServerGroundPostfixTest));
    if (layer == null)
      return;
    // A place with ground: a vertex of a ground zone with a weight of 255.
    var zone = layer.Ground.Zones.OrderBy(z => z.Z).ThenBy(z => z.X).First(z => { layer.Ground.TryGet(z, out var g); return g.Weights.Contains((byte)255); });
    layer.Ground.TryGet(zone, out var zg);
    int v = Array.FindIndex(zg.Weights, w => w == 255);
    while (v % 65 == 64 || v / 65 == 64)
      v = Array.FindIndex(zg.Weights, v + 1, w => w == 255);
    float x = (float)(zone.OriginX + v % 65), z = (float)(zone.OriginZ + v / 65);
    float town = (float)VertexHeight(zg, v);
    using (ServerStore(layer))
    {
      C(Math.Abs(BakedGround.GetBiomeHeightPostfix(10f, x, z) - town) < 0.001f, "with the layer current, the postfix gives the town's height at a weight-255 vertex");
      using (BakedGround.Pause())
      {
        C(BakedGround.IsPaused && BakedGround.GetBiomeHeightPostfix(10f, x, z) == 10f, "paused, it gives the height it was given");
        using (BakedGround.Pause())
          C(BakedGround.GetBiomeHeightPostfix(10f, x, z) == 10f, "paused twice, too");
        C(BakedGround.GetBiomeHeightPostfix(10f, x, z) == 10f, "and one pause ending does not end the other");
      }
      C(!BakedGround.IsPaused && Math.Abs(BakedGround.GetBiomeHeightPostfix(10f, x, z) - town) < 0.001f, "when both end, it is back");
      var pause = BakedGround.Pause();
      pause.Dispose();
      pause.Dispose();
      C(!BakedGround.IsPaused, "a pause disposed twice ends once");
    }
    using (ServerStore(null))
      C(BakedGround.GetBiomeHeightPostfix(10f, x, z) == 10f, "with no layer current it gives the height it was given");
  }

  public static void ServerGroundThreadsTest()
  {
    Section("ground: the postfix on many threads while the layer is swapped whole");
    var layer = ServerValtima(nameof(ServerGroundThreadsTest));
    if (layer == null)
      return;
    var other = ServerWithRevision(layer, 2);
    var zones = layer.Ground.Zones.OrderBy(z => z.Z).ThenBy(z => z.X).Take(60).ToList();
    using var store = ServerStore(layer);
    long errors = 0, runs = 0, bad = 0;
    var stop = new CancellationTokenSource();
    var workers = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
    {
      var random = new System.Random(t);
      while (!stop.IsCancellationRequested)
      {
        var zone = zones[random.Next(zones.Count)];
        double x = zone.OriginX + random.NextDouble() * 64, z = zone.OriginZ + random.NextDouble() * 64;
        try
        {
          float got = BakedGround.GetBiomeHeightPostfix(50f, (float)x, (float)z);
          // Both layers have the same ground, and a swap to none answers the height it was given: nothing else may come out.
          float expected = (float)BakedGround.Apply(layer.Ground, (float)x, (float)z, 50f);
          if (got != 50f && Math.Abs(got - expected) > 1e-4f)
            Interlocked.Increment(ref bad);
          Interlocked.Increment(ref runs);
        }
        catch (Exception)
        {
          Interlocked.Increment(ref errors);
        }
      }
    })).ToArray();
    for (int n = 0; n < 3000; n++)
    {
      BC.Settings.Layer = n % 3 == 0 ? other : n % 3 == 1 ? layer : null;
      if (n % 50 == 0)
        Thread.Sleep(1);
    }
    stop.Cancel();
    Task.WaitAll(workers);
    C(errors == 0 && bad == 0 && runs > 10000, $"no exception and no wrong answer in {runs:N0} calls on six threads over 3,000 swaps ({errors} errors, {bad} wrong)");
  }

  public static void ServerGroundChangesTest()
  {
    Section("ground: which zones' terrain a new layer rebuilds (8.3)");
    var layer = ServerValtima(nameof(ServerGroundChangesTest));
    if (layer == null)
      return;
    var same = ServerWithRevision(layer, 5);
    var withTerrain = layer.Zones.Where(r => r.HasGround || r.HasPaint).Select(r => r.Key).ToHashSet();
    C(withTerrain.Count == 1354, $"1,354 zones of the file have ground or paint ({withTerrain.Count})");
    C(BakedGround.TerrainChanges(null, layer, null).SetEquals(withTerrain), "a first layer changes the terrain in every zone that has ground or paint");
    C(BakedGround.TerrainChanges(layer, null, null).SetEquals(withTerrain), "and so does a layer that goes");
    C(BakedGround.TerrainChanges(layer, same, null).Count == 0, "a layer that only has another revision changes none (the zones' ground and paint are the same)");
    C(BakedGround.TerrainChanges(null, null, null).Count == 0, "no layer to no layer: none");
    var some = withTerrain.Take(3).ToArray();
    var outside = new ZoneKey(-1000, 1000);
    C(BakedGround.TerrainChanges(null, layer, some.Append(outside).ToArray()).SetEquals(some), "only the zones a change names are asked, and one without ground or paint is not one");
    C(BakedGround.TerrainChanges(layer, same, some).Count == 0, "named, and the same: none");

    // What a change does: the loaded terrain of those zones is built again, the distant terrain too, the grass is cut again a frame
    // later, and the machine that runs the world deletes its minimap cache.
    var pokes = new List<ZoneKey>();
    int distant = 0, builds = 0, minimap = 0;
    var grass = new List<Vector3>();
    var loaded = new List<(ZoneKey, Heightmap)> { (some[0], null), (some[1], null), (new ZoneKey(500, 500), null) };
    var was = (BakedGround.LoadedTerrain, BakedGround.PokeTerrain, BakedGround.PokeDistant, BakedGround.ClearBuilds, BakedGround.ResetGrass, BakedGround.DeleteMinimapCache, BakedGround.RunsWorld);
    try
    {
      BakedGround.LoadedTerrain = () => loaded;
      BakedGround.PokeTerrain = hm => pokes.Add(default);
      BakedGround.PokeDistant = () => distant++;
      BakedGround.ClearBuilds = () => builds++;
      BakedGround.ResetGrass = (centre, radius) => grass.Add(centre);
      BakedGround.DeleteMinimapCache = () => minimap++;
      BakedGround.RunsWorld = () => true;
      BakedGround.OnLayerChanged(null, layer, some);
      C(pokes.Count == 2 && distant == 1 && builds == 1 && minimap == 1, $"two loaded zones that changed are poked and one that did not is not ({pokes.Count}); the distant terrain once ({distant}); the builds cleared ({builds}); the minimap cache deleted ({minimap})");
      pokes.Clear();
      distant = builds = minimap = 0;
      BakedGround.RunsWorld = () => false;
      BakedGround.OnLayerChanged(null, layer, some);
      C(minimap == 0 && pokes.Count == 2, "a client has no minimap cache of the world to delete, and rebuilds its terrain all the same");
      pokes.Clear();
      distant = builds = minimap = 0;
      BakedGround.OnLayerChanged(layer, same, null);
      C(pokes.Count == 0 && distant == 0 && builds == 0 && minimap == 0, "a layer with the same ground rebuilds nothing");
      BakedGround.PokeTerrain = hm => throw new InvalidOperationException("the game said no");
      BakedGround.RunsWorld = () => true;
      var lines = LogHandler.During(() => BakedGround.OnLayerChanged(null, layer, some));
      C(lines.Any(l => l.Contains("could not be rebuilt")), "a terrain that cannot be rebuilt is logged, and does not stop whoever replaced the layer");
    }
    finally
    {
      (BakedGround.LoadedTerrain, BakedGround.PokeTerrain, BakedGround.PokeDistant, BakedGround.ClearBuilds, BakedGround.ResetGrass, BakedGround.DeleteMinimapCache, BakedGround.RunsWorld) = was;
    }
  }
}

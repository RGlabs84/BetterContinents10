// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// Light Count (BakedLightCount): only the nearest N baked lights burn, every other Copy record is drawn unlit. Offline: the ranking (pure) with its
// hysteresis, when it is made, the cull-and-LOD job's skip of the records whose lit copy stands, and the whole flow of a zone's props with the game's
// part (making a copy, destroying one, the frame and the clock) stood in for: the nearest N across two zones, a change of the setting at run time, Light
// Count 0, a copy that cannot be made, the per-frame limit, a dropped copy waiting for the drawing, and Copy records beyond Light Distance drawn.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // The x of every record with a lit copy standing, by the zones' published snapshots.
  private static string LitXsOf(IEnumerable<BuiltZone> zones) =>
    string.Join(",", zones.SelectMany(z => z.Lit == null ? [] : Enumerable.Range(0, z.Copies.Length).Where(i => z.Lit[i]).Select(i => (float)Math.Round(z.Copies[i].Matrix.m03, 1))).OrderBy(x => x));

  private static string Burns(bool[] flags) => string.Join(",", flags.Select((f, i) => f ? i : -1).Where(i => i >= 0));

  private static void LightCountChooseTest()
  {
    Section("light count: the nearest N burn");
    // distances 2, 5, 11, 20, 30, 95 m: squared
    var d2 = new List<float> { 4f, 25f, 121f, 400f, 900f, 9025f };
    var none = Enumerable.Repeat(false, d2.Count).ToList();
    C(Burns(BakedLightCount.Choose(d2, none, 0, 90f, 20f)) == "", "Light Count 0: none burns");
    C(Burns(BakedLightCount.Choose(d2, none, -4, 90f, 20f)) == "", "a negative count is none");
    C(Burns(BakedLightCount.Choose(d2, none, 3, 0f, 20f)) == "", "Light Distance 0: none burns");
    C(Burns(BakedLightCount.Choose(d2, none, 1, 90f, 20f)) == "0", "1: the nearest (2 m)");
    C(Burns(BakedLightCount.Choose(d2, none, 3, 90f, 20f)) == "0,1,2", "3: 2, 5 and 11 m");
    C(Burns(BakedLightCount.Choose(d2, none, 24, 90f, 20f)) == "0,1,2,3,4", "24 and five within Light Distance: those five (the one at 95 m is too far)");
    C(Burns(BakedLightCount.Choose(d2, none, 24, 100f, 20f)) == "0,1,2,3,4,5", "...and with Light Distance 100 m all six");
    C(Burns(BakedLightCount.Choose(d2, none, 24, 30f, 20f)) == "0,1,2,3", "a copy at exactly Light Distance is not within it (30 m)");
    // equal ranks by place in the list; a distance that is not a number is never eligible
    C(Burns(BakedLightCount.Choose([25f, 25f, 25f, 25f], [false, false, false, false], 2, 90f, 20f)) == "0,1", "four copies 5 m away: the first two in the list");
    C(Burns(BakedLightCount.Choose([float.NaN, 9f, 4f], [false, false, false], 5, 90f, 20f)) == "1,2", "a place that is not a number never burns");
    // against a brute-force ranking: no copy burns, so the nearest N within Light Distance by distance, then by place
    var rng = new System.Random(11);
    var places = Enumerable.Range(0, 400).Select(_ => (float)(rng.NextDouble() * 140)).Select(d => d * d).ToList();
    var off = Enumerable.Repeat(false, 400).ToList();
    bool agrees = true;
    foreach (int n in new[] { 1, 3, 8, 24, 100, 128 })
    {
      var want = Enumerable.Range(0, 400).Where(i => places[i] < 90f * 90f).OrderBy(i => places[i]).ThenBy(i => i).Take(n).ToHashSet();
      var got = BakedLightCount.Choose(places, off, n, 90f, 20f);
      agrees &= got.Count(f => f) == want.Count && Enumerable.Range(0, 400).All(i => got[i] == want.Contains(i));
    }
    C(agrees, "400 copies, six values of N: the same as sorting by distance and taking the first N within Light Distance");

    Section("light count: a copy that burns is hard to displace");
    // N = 2: A (10 m) and B (12 m) burn; C (11 m) does not. C is nearer than B but not by the margin.
    C(Burns(BakedLightCount.Choose([100f, 144f, 121f], [true, true, false], 2, 90f, 20f)) == "0,1", "a copy not burning 1 m nearer than one that burns does not take its place");
    C(Burns(BakedLightCount.Choose([100f, 144f, 121f], [false, false, false], 2, 90f, 20f)) == "0,2", "...which it does when none burns");
    C(Burns(BakedLightCount.Choose([100f, 144f, 90.25f], [true, true, false], 2, 90f, 20f)) == "0,2", "9.5 m: 2.5 m nearer than B, more than the margin: it does (B at 12 m counts as 10)");
    C(BakedLightCount.Margin == 2f, "the margin is 2 m");
    // edge of Light Distance: a copy that burns stays out to Light Distance + 20 m, one that does not is not made
    C(Burns(BakedLightCount.Choose([95f * 95f, 109f * 109f, 111f * 111f], [true, true, true], 5, 90f, 20f)) == "0,1", "a copy that burns stays out to 110 m, and goes beyond");
    C(Burns(BakedLightCount.Choose([95f * 95f, 109f * 109f], [false, false], 5, 90f, 20f)) == "", "...but one that does not burn is not made out there");
    // the ranking does not flip at the edge: records 1 m apart along a line, an eye that steps back and forth over the boundary of the 3rd place
    var line = Enumerable.Range(0, 8).Select(i => 10f + i).ToList();
    var burning = Enumerable.Repeat(false, 8).ToList();
    int flips = 0, flipsWithout = 0;
    bool[] last = null, lastPlain = null;
    for (int step = 0; step < 40; step++)
    {
      float eye = 10.4f + (step % 2) * 1.2f;   // 10.4, 11.6, 10.4 ...: the 3rd and 4th records trade places by a hair
      var squared = line.Select(x => (x - eye) * (x - eye)).ToList();
      var sticky = BakedLightCount.Choose(squared, burning, 3, 90f, 20f);
      var plain = BakedLightCount.Choose(squared, Enumerable.Repeat(false, 8).ToList(), 3, 90f, 20f);
      if (last != null)
        flips += Enumerable.Range(0, 8).Count(i => last[i] != sticky[i]);
      if (lastPlain != null)
        flipsWithout += Enumerable.Range(0, 8).Count(i => lastPlain[i] != plain[i]);
      last = sticky;
      lastPlain = plain;
      burning = sticky.ToList();
    }
    C(flips < flipsWithout && flips <= 6, $"an eye stepping 1.2 m back and forth: {flips} changes with the margin, {flipsWithout} without");
    C(BakedLightCount.StatsLine(335, 24, 24, 90f, false) == "Baked lights: 24 of 335 Copy records burn as lit copies, 311 are drawn unlit (Light Count 24, Light Distance 90 m)."
      && BakedLightCount.StatsLine(10, 0, 0, 90f, true).EndsWith(", drawing hidden)."), "the line of bc_bake stats");

    Section("light count: when the ranking is made");
    BakedLightCount.Clear();
    C(BakedLightCount.Due(10f, 24, 90f, 2), "the first time");
    BakedLightCount.Update([], Vector3.zero, 90f, 24, 10f);
    C(!BakedLightCount.Due(10.1f, 24, 90f, 0), "not every frame");
    C(BakedLightCount.Due(10.25f, 24, 90f, 0), "0.25 s after the last");
    C(BakedLightCount.Due(10.01f, 3, 90f, 0) && BakedLightCount.Due(10.01f, 24, 60f, 0) && BakedLightCount.Due(10.01f, 24, 90f, 1), "at once when Light Count, Light Distance or the zones that have props change");
    BakedLightCount.Clear();
  }

  private static void LightCountSettingTest()
  {
    Section("light count: the setting");
    var s = SettingsSchema.BakedLightCount;
    C(s.Key == "Light Count" && s.Default == 24 && s.Limits == (0, 128) && s.Scope == SettingScope.Live && s.Section == "10 BetterContinents.BakedPlacements", "Light Count: 24, 0 to 128, read live, in section 10");
    C(BakedLightCount.Default == 24 && BakedLightCount.Highest == 128, "the class says the same");
    C(s.Description.Contains("Light Distance") && s.Description.Contains("drawn unlit") && s.Description.Contains("0 = none burns") && s.Description.Contains("Applies at once"), "its description says what it counts, what the rest are and what 0 does");
    C(SettingsSchema.BakedLightDistance.Description.Contains("Light Count") && SettingsSchema.BakedLightDistance.Description.Contains("drawn unlit"), "Light Distance's description no longer says the rest are not shown");
    var keys = SettingsSchema.Groups[^1].Settings.Select(x => x.Key).ToList();
    C(keys.IndexOf("Light Count") > keys.IndexOf("Light Distance") && keys.IndexOf("Light Count") == keys.IndexOf("Light Shadows") - 1, "it stands between Light Distance and Light Shadows");
  }

  // Two zones of Copy records on a line: zone (0, 0) holds x -20, 0, 10, 20, 28 and zone (1, 0) x 34, 41, 60; every record 1 m up on z 0.
  private static (BakedKind Kind, BuiltZone[] Zones) LightCountZones()
  {
    var piece = ClientKit.TwoLods("torch", 1f, 0.2f, 0.05f, radius: 0.5f);
    var kind = ClientKit.Kind("piece_walltorch", piece, role: BakedRole.Copy, collision: BakedCollision.Prefab);
    kind.Prefab = ApiAlive<GameObject>();
    var a = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, -20, 1, 0, 0, null), (0, 0, 1, 0, 0, null), (0, 10, 1, 0, 0, null), (0, 20, 1, 0, 0, null), (0, 28, 1, 0, 0, null)), [kind], 1);
    var b = BakedZoneBuild.Build(ClientKit.Zone(1, 0, (0, 34, 1, 0, 0, null), (0, 41, 1, 0, 0, null), (0, 60, 1, 0, 0, null)), [kind], 1);
    return (kind, [a, b]);
  }

  private static void LightCountCullTest()
  {
    Section("light count: the drawing skips a record whose lit copy stands, and draws the rest");
    var (kind, zones) = LightCountZones();
    var a = zones[0];
    C(a.Copies.Length == 5 && a.Drawn == 5 && a.Kinds.Length == 1 && a.Kinds[0].CopyIndex != null && a.Kinds[0].CopyIndex!.OrderBy(i => i).SequenceEqual([0, 1, 2, 3, 4]),
      "a Copy record is built as an instance and as a copy to light, and each instance knows its copy's place");
    var near = ClientKit.BatchOf(kind, 0, 0);
    var far = ClientKit.BatchOf(kind, 1, 0);
    // a camera 40 m west of the zone's centre: the torch (size 1) is LOD0 within 10 m * 2 / ... , the last LOD out to 40 m x Draw Scale; use a wide Draw Scale
    List<float> Xs(Batch batch) => ClientKit.Filled(batch, false).Select(m => (float)Math.Round(m.m03, 1)).OrderBy(x => x).ToList();
    var job = ClientKit.Job([a], [kind], 0f, 0f, 0f, 2f, drawScale: 4f);
    BakedDraw.Fill(job);
    int all = Xs(near).Count + Xs(far).Count;
    C(job.Done && job.Error == null && all == 5, $"with no lit copy standing (Lit is null) all five are drawn ({all})");
    // the copies of -20 and 20 stand: indices follow the order of Copies, which is the order of the records
    var lit = new bool[5];
    for (int i = 0; i < a.Copies.Length; i++)
      lit[i] = Math.Abs(Math.Abs(a.Copies[i].Matrix.m03) - 20f) < 0.1f;
    a.Lit = lit;
    BakedDraw.Fill(ClientKit.Job([a], [kind], 0f, 0f, 0f, 2f, drawScale: 4f));
    var drawn = Xs(near).Concat(Xs(far)).OrderBy(x => x).ToList();
    C(drawn.SequenceEqual([0f, 10f, 28f]), $"with the two at -20 and 20 lit they are not drawn: {string.Join(", ", drawn)}");
    a.Lit = Enumerable.Repeat(true, 5).ToArray();
    BakedDraw.Fill(ClientKit.Job([a], [kind], 0f, 0f, 0f, 2f, drawScale: 4f));
    C(Xs(near).Count + Xs(far).Count == 0, "all five lit: none is drawn");
    a.Lit = new bool[2] { true, true };
    BakedDraw.Fill(ClientKit.Job([a], [kind], 0f, 0f, 0f, 2f, drawScale: 4f));
    C(Xs(near).Count + Xs(far).Count == 3, "a snapshot shorter than the zone's copies (an old one) skips only the records it covers");
    a.Lit = null;

    // a Copy record far beyond Light Distance is drawn like any piece (the drawing knows nothing of Light Distance), and out to the cull distance only
    var (kind2, z2) = LightCountZones();
    var farJob = ClientKit.Job([z2[1]], [kind2], -150f, 0f, 0f, 2f, drawScale: 8f);
    BakedDraw.Fill(farJob);
    var batchFar = ClientKit.BatchOf(kind2, 1, 0);
    C(farJob.InstancesDrawn == 3 && ClientKit.Filled(batchFar, false).Count == 3, $"three Copy records 184 to 210 m away (past Light Distance's 90 m) are drawn: {farJob.InstancesDrawn}");

    // a Static in the same zone is never skipped by the lit snapshot
    var wall = ClientKit.Kind("wall", ClientKit.TwoLods("wall", 4f, 0.2f, 0.05f, 2f));
    var mixed = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 1, 1, 0, 0, null), (1, 2, 1, 0, 0, null)), [kind2, wall], 1);
    mixed.Lit = [true, true];
    var mixedJob = ClientKit.Job([mixed], [kind2, wall], 0f, 0f, 0f, 2f, drawScale: 4f);
    BakedDraw.Fill(mixedJob);
    C(mixedJob.InstancesDrawn == 1 && mixed.Kinds.Single(k => k.Kind == wall).CopyIndex == null, "the Copy is skipped, the Static beside it is drawn, and a Static has no copy index");
  }

  private static void LightCountFlowTest()
  {
    Section("light count: the zones' props light the nearest N across zones, a few per frame");
    BakedLightCount.Clear();
    var (kind, zones) = LightCountZones();
    var made = new List<(string Name, float X)>();
    var destroyed = new List<GameObject>();
    var madeObjects = new List<GameObject>();
    int frame = 0;
    float clock = 100f;
    var brokenX = new HashSet<float>();
    var oldFrame = BakedCopies.Frame;
    var oldClock = BakedCopies.Clock;
    var oldMaker = BakedCopies.Maker;
    var oldDestroyer = BakedCopies.Destroyer;
    int litVersion0 = BakedDraw.LitVersion, adopted0 = BakedDraw.LitAdopted;
    try
    {
      BakedCopies.Frame = () => frame;
      BakedCopies.Clock = () => clock;
      BakedCopies.Maker = (k, m, parent) =>
      {
        if (brokenX.Contains((float)Math.Round(m.m03, 1)))
          throw new InvalidOperationException("no light");
        var go = ApiAlive<GameObject>();
        made.Add((k.Name, (float)Math.Round(m.m03, 1)));
        madeObjects.Add(go);
        return go;
      };
      BakedCopies.Destroyer = go => destroyed.Add(go);
      var props = zones.Select(z => new BakedCopies.Props(z, null!)).ToList();
      var eye = new Vector3(30f, 1f, 0f);
      string LitXs() => LitXsOf(zones);
      void Frame(int count, float near = 90f, float step = 0.0f)
      {
        clock += step;
        frame++;
        BakedLightCount.Update(props, eye, near, count, clock);
        BakedLightCount.MakePending();
        foreach (var p in props)
          p.UpdateLit();
      }

      // eye at x = 30: distances 50, 30, 20, 10, 2 (zone 0) and 4, 11, 30 (zone 1)
      Frame(3);
      C(LitXs() == "20,28,34", $"Light Count 3: the three nearest across both zones burn (x {LitXs()})");
      C(props.Sum(p => p.LitCount) == 3 && made.Count == 3 && BakedDraw.LitVersion > litVersion0, "three copies made, and the drawing was told");
      C(zones[0].Lit != null && zones[1].Lit != null && zones[0].Lit!.Count(b => b) == 2 && zones[1].Lit!.Count(b => b) == 1, "each zone's snapshot holds its own: two in the first, one in the second");

      // a change of the setting at run time: at once, and the copies that stop burning wait for the drawing
      int version = BakedDraw.LitVersion;
      Frame(1);
      C(LitXs() == "28", $"Light Count 1: only the nearest burns (x {LitXs()})");
      C(destroyed.Count == 0 && props.Sum(p => p.LitCount) == 1 && BakedDraw.LitVersion > version, "the two dropped are not destroyed yet: the drawing is told first");
      Frame(1, step: 0.1f);
      C(destroyed.Count == 0, "and they wait while no job that knows has been adopted");
      BakedDraw.LitAdopted = BakedDraw.LitVersion;
      Frame(1, step: 0.1f);
      C(destroyed.Count == 2 && destroyed.All(d => madeObjects.Contains(d)), "when one is adopted they go");
      // the cap: no job runs (drawing hidden or stalled) for a second
      Frame(3);
      int before = destroyed.Count;
      Frame(1);
      C(LitXs() == "28" && destroyed.Count == before, "dropped again, and waiting");
      Frame(1, step: 1.2f);
      C(destroyed.Count == before + 2, "...and a second later they go whatever the drawing did");

      // Light Count 0, and Light Distance 0
      Frame(0);
      C(LitXs() == "" && zones.All(z => z.Lit == null), "Light Count 0: none burns, and every Copy record is drawn (no snapshot)");
      BakedDraw.LitAdopted = BakedDraw.LitVersion;
      Frame(0, step: 0.3f);
      C(destroyed.Count == before + 3, "...and the last copy went");
      Frame(5, near: 0f);
      C(LitXs() == "", "Light Distance 0: none burns");
      Frame(5, near: 90f, step: 0.3f);
      C(LitXs() == "20,28,34,41", $"back to 5 within 90 m: four are made in the frame, the nearest first (x {LitXs()})");
      Frame(5);
      C(LitXs() == "10,20,28,34,41", $"and the fifth in the next (x {LitXs()})");
    }
    finally
    {
      BakedCopies.Frame = oldFrame;
      BakedCopies.Clock = oldClock;
      BakedCopies.Maker = oldMaker;
      BakedCopies.Destroyer = oldDestroyer;
      BakedLightCount.Clear();
      BakedDraw.LitAdopted = Math.Max(adopted0, BakedDraw.LitVersion);
    }
  }

  private static void LightCountLimitsTest()
  {
    Section("light count: per frame, beyond Light Distance, a copy that fails, and the zone going");
    BakedLightCount.Clear();
    var (kind, zones) = LightCountZones();
    var made = new List<float>();
    int frame = 0;
    float clock = 500f;
    var broken = new HashSet<float>();
    var oldFrame = BakedCopies.Frame;
    var oldClock = BakedCopies.Clock;
    var oldMaker = BakedCopies.Maker;
    var oldDestroyer = BakedCopies.Destroyer;
    try
    {
      BakedCopies.Frame = () => frame;
      BakedCopies.Clock = () => clock;
      BakedCopies.Maker = (k, m, parent) =>
      {
        float x = (float)Math.Round(m.m03, 1);
        if (broken.Contains(x))
          throw new InvalidOperationException("no light");
        made.Add(x);
        return ApiAlive<GameObject>();
      };
      BakedCopies.Destroyer = go => { };
      var props = zones.Select(z => new BakedCopies.Props(z, null!)).ToList();
      var eye = new Vector3(30f, 1f, 0f);
      string LitXs() => LitXsOf(zones);
      void Frame(int count, float near = 90f, float step = 0f)
      {
        clock += step;
        frame++;
        BakedLightCount.Update(props, eye, near, count, clock);
        BakedLightCount.MakePending();
        foreach (var p in props)
          p.UpdateLit();
      }

      // all eight within 90 m of the eye, a limit of 4 made a frame
      Frame(128);
      C(made.Count == 4 && LitXs() == "20,28,34,41", $"128 allowed and eight near: four are made in the first frame, the nearest four ({LitXs()})");
      Frame(128);
      C(made.Count == 8 && props.Sum(p => p.LitCount) == 8, "and the other four in the second");
      C(zones.SelectMany(z => z.Lit!).Count(b => b) == 8, "each published as lit once made");

      // a copy that cannot be made: tried once, never again, and does not take a place
      var (kind2, zones2) = LightCountZones();
      var props2 = zones2.Select(z => new BakedCopies.Props(z, null!)).ToList();
      made.Clear();
      broken.Add(28f);
      BakedLightCount.Clear();
      void Frame2(int count, float step = 0f)
      {
        clock += step;
        frame++;
        BakedLightCount.Update(props2, eye, 90f, count, clock);
        BakedLightCount.MakePending();
        foreach (var p in props2)
          p.UpdateLit();
      }
      Frame2(2);
      C(made.SequenceEqual([34f]) && LitXsOf(zones2) == "34", $"the nearest cannot be made (x 28) and uses up a try: only 34 is made in that frame ({string.Join(",", made)})");
      Frame2(2, step: 0.3f);
      var lit2 = LitXsOf(zones2);
      C(lit2 == "20,34" && !made.Contains(28f), $"it is never tried again, and the two that burn are 20 and 34 ({lit2})");

      // Light Distance: beyond it nothing is made, but a copy that burns stays out to 20 m more
      var (kind3, zones3) = LightCountZones();
      var props3 = zones3.Select(z => new BakedCopies.Props(z, null!)).ToList();
      made.Clear();
      BakedLightCount.Clear();
      eye = new Vector3(30f, 1f, 0f);
      void Frame3(float near, float step)
      {
        clock += step;
        frame++;
        BakedLightCount.Update(props3, eye, near, 128, clock);
        BakedLightCount.MakePending();
        foreach (var p in props3)
          p.UpdateLit();
      }
      broken.Clear();
      Frame3(25f, 0f);   // within 25 m of x = 30: 28 (2 m), 34 (4), 20 (10), 41 (11) and 10 (20): four are made in a frame, the nearest first
      string Lit3() => LitXsOf(zones3);
      C(Lit3() == "20,28,34,41", $"Light Distance 25 m: four are made in the frame, the nearest first ({Lit3()})");
      Frame3(25f, 0f);
      C(Lit3() == "10,20,28,34,41", $"and the fifth in the next: the five within it burn, the records at 30 m and 50 m do not ({Lit3()})");
      eye = new Vector3(60f, 1f, 0f);   // now 10 is 50 m away, 20 is 40 m, 28 is 32 m, 34 is 26 m, 41 is 19 m, 60 is under the eye
      Frame3(25f, 0.3f);
      C(Lit3() == "20,28,34,41,60", $"moving east: the copies that burn stay out to 45 m (25 + 20) and the one at 50 m is dropped; the one under the eye is made ({Lit3()})");

      // the zone goes: its records are drawn unlit again, whatever draws it next
      int version = BakedDraw.LitVersion;
      props3[1].Release();
      C(zones3[1].Lit == null && BakedDraw.LitVersion > version, "when a zone's props are let go its snapshot is gone and the drawing is told");
    }
    finally
    {
      BakedCopies.Frame = oldFrame;
      BakedCopies.Clock = oldClock;
      BakedCopies.Maker = oldMaker;
      BakedCopies.Destroyer = oldDestroyer;
      BakedLightCount.Clear();
    }
  }
}

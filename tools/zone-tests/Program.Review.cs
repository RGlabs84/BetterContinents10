// Added by Wubarrk on 2026-10-05 for the unifying refactor (0.10.0).
//
// What the last review of zone regeneration asked for: a stray has its turn right after the nearest zone of its group that is reset (so a
// save never finds a stray cleared and the rest of its group untouched), a zone has its turn once even when a save took it while the work
// waited for the next frame, a stray the game generated before its turn is reset like any zone, the save's drain sends each zone's objects
// on their own, and an error before the work was made finishes the zones an earlier run left waiting (telling at most three failures).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterContinents;
using UnityEngine;
using Budget = BetterContinents.ZoneReset.Budget;
using Kind = BetterContinents.ZoneReset.Kind;
using Plan = BetterContinents.ZoneReset.Plan;
using Progress = BetterContinents.ZoneReset.Progress;

internal static partial class Program
{
  private static void ReviewTests()
  {
    ZoneRegen.DeltaTime = () => 1f / 60f;
    ReviewOrderTests();
    ReviewSaveTests();
    ReviewTurnTests();
    ReviewPromoteTests();
    ReviewDrainTests();
    ReviewErrorTests();
  }

  // ------------------------------------------------------------------------------------------------ the order of the turns

  // The distance of a zone to the nearest of the people, as the plan measures it.
  private static long ReviewDistance(Vector2s zone, IReadOnlyList<Vector2s> focus) =>
    focus.Min(f => (long)(zone.x - f.x) * (zone.x - f.x) + (long)(zone.y - f.y) * (zone.y - f.y));

  private static void ReviewOrderTests()
  {
    Section("review: a stray has its turn right after the nearest zone of its group that is reset");
    var reach = LocIn(0, 0, 20, 0, 20);

    // The players are at the stray, the nearest zone of all: the home is first all the same, and the stray follows it.
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [], [Z(1, 0)], [reach]);
    C(Show(plan.Turns) == "0,0 1,0 5,5", "the players are in the stray: the home first, then the stray, then the zone of no group: " + Show(plan.Turns));
    plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [], [], [reach]);
    C(Show(plan.Turns) == "0,0 1,0 5,5", "with nobody in the world, the centre is the nearest: the same order");
    // A pin at the corner of four zones, the one it belongs to the only zone generated: the three strays are as near the players as that zone
    // or nearer, and smaller by x and y, and still follow it, in x then y.
    plan = ZoneReset.MakePlan([Z(1, 0), Z(4, 4)], [], [Z(0, 0)], [LocIn(1, 0, -30, 30, 10)]);
    C(Show(plan.Strays) == "0,0 0,1 1,1" && Show(plan.Turns) == "1,0 0,0 0,1 1,1 4,4", "three strays as near as their home, or nearer, come after it: " + Show(plan.Turns));

    // A group of three in a row, the middle one never generated: the stray comes after the nearer of the two zones to reset, whichever that is.
    var (_, chain) = FinalChain(3);
    plan = ZoneReset.MakePlan([Z(0, 0), Z(2, 0)], [], [Z(1, 0)], chain);
    C(Show(plan.Strays) == "1,0" && Show(plan.Turns) == "0,0 1,0 2,0", "players in the stray, the two ends equally near: the end with the smaller x, then the stray: " + Show(plan.Turns));
    plan = ZoneReset.MakePlan([Z(0, 0), Z(2, 0)], [], [Z(5, 0)], chain);
    C(Show(plan.Turns) == "2,0 1,0 0,0", "players beyond the east end: that end first, the stray after it, the west end last: " + Show(plan.Turns));
    // Several strays of one anchor, in x then y; a zone that is nearer than the anchor's group still goes before all of them.
    var rowLocations = Enumerable.Range(0, 4).Select(x => LocIn(x, 0, 20, 0, 20)).ToList();
    plan = ZoneReset.MakePlan([Z(0, 0), Z(2, 0), Z(4, 0), Z(2, 3)], [], [Z(2, 3)], rowLocations);
    C(Show(plan.Strays) == "1,0 3,0" && Show(plan.Turns) == "2,3 2,0 1,0 3,0 0,0 4,0", "two strays of one anchor come together after it, in x order: " + Show(plan.Turns));
    plan = ZoneReset.MakePlan([Z(2, 0)], [], [Z(2, 0)], rowLocations);
    C(Show(plan.Strays) == "0,0 1,0 3,0 4,0" && Show(plan.Turns) == "2,0 0,0 1,0 3,0 4,0", "a group with one zone that is reset: that zone, then its four strays in x order: " + Show(plan.Turns));

    // With no location at all, the turns are the zones to reset, nearest first, as ever.
    plan = ZoneReset.MakePlan(Square(2), [], [Z(0, 0)]);
    C(Show(plan.Turns) == Show(plan.Reset) && plan.Strays.Count == 0, "with no location the turns are the zones to reset, nearest first, as ever");

    // Random worlds: every stray is behind the first zone to reset of its group, with only strays of that group between; the zones to reset
    // come in the order of their distance; nothing is lost or listed twice.
    int worlds = 0, strays = 0, bad = 0;
    var detail = "";
    for (int seed = 1; seed <= 600; seed++)
    {
      var scene = FinalScene.Make(seed, partlyGenerated: true, quiet: true);
      var random = ZoneReset.MakePlan(scene.Generated, [], scene.Focus, scene.Locations);
      if (random.Strays.Count == 0)
        continue;
      worlds++;
      strays += random.Strays.Count;
      var at = new Dictionary<Vector2s, int>(ZoneReset.Comparer);
      for (int i = 0; i < random.Turns.Count; i++)
        at[random.Turns[i]] = i;
      var firstOfGroup = new Dictionary<int, int>();
      foreach (var zone in random.Reset)
        if (random.GroupOf.TryGetValue(zone, out var g) && !firstOfGroup.ContainsKey(g))
          firstOfGroup[g] = at[zone];
      bool ok = at.Count == random.Turns.Count && random.Turns.Count == random.Reset.Count + random.Strays.Count;
      for (int i = 0; i < random.Turns.Count && ok; i++)
      {
        var zone = random.Turns[i];
        if (!random.Strays.Contains(zone))
          continue;
        int group = random.GroupOf[zone];
        ok = firstOfGroup.TryGetValue(group, out var anchor) && anchor < i;
        for (int k = anchor + 1; ok && k < i; k++)
          ok = random.Strays.Contains(random.Turns[k]) && random.GroupOf[random.Turns[k]] == group;
      }
      var distances = random.Reset.Select(z => (ReviewDistance(z, scene.Focus), z.x, z.y)).ToList();
      ok = ok && distances.SequenceEqual(distances.OrderBy(d => d.Item1).ThenBy(d => d.x).ThenBy(d => d.y));
      if (!ok && ++bad == 1)
        detail = $"seed {seed}: " + Show(random.Turns) + " (strays " + Show(random.Strays) + ")";
    }
    C(worlds > 150 && strays > 300, $"({worlds} random worlds had {strays} strays)");
    C(bad == 0, "in each of them every stray follows the first zone to reset of its group, only strays of that group stand between, and the zones to reset keep their order of distance " + detail);

    // A group of 20,000 zones in a row, every other one never generated: the anchor is found once for the group, not once for each of its
    // 10,000 strays (which would be 200 million looks).
    var big = Enumerable.Range(0, 20000).Select(x => Z(x, 0)).ToList();
    var bigLocations = Enumerable.Range(0, 19999).Select(x => LocIn(x, 0, 20, 0, 20)).ToList();
    var watch = Stopwatch.StartNew();
    var bigPlan = ZoneReset.MakePlan(big.Where(z => z.x % 2 == 0), [], [Z(10000, 0)], bigLocations);
    watch.Stop();
    C(bigPlan.Groups.Count == 1 && bigPlan.Groups[0].Length == 20000 && bigPlan.Strays.Count == 10000 && bigPlan.Reset.Count == 10000, "(one group of 20,000 zones, 10,000 of them strays)");
    C(Show(bigPlan.Turns.Take(3)) == "10000,0 1,0 3,0" && bigPlan.Turns.Count == 20000, "the nearest zone to reset first, the strays of its group after it: " + Show(bigPlan.Turns.Take(3)));
    C(watch.ElapsedMilliseconds < 500, $"the plan of that group takes {watch.ElapsedMilliseconds} ms: not the square of its size");
  }

  // ------------------------------------------------------------------------------------------------ a save before the home's turn

  private static void ReviewSaveTests()
  {
    Section("review: a save with the stray nearest the players finds the home waiting, and the file holds the group wholly emptied");
    var home = Z(0, 0);
    var stray = Z(1, 0);
    var reach = LocIn(0, 0, 20, 0, 20);

    // The first frame empties the home (the listing and its 2 objects are the budget of 3), and a save comes before the stray's turn. The
    // players are at the stray: before, its turn was the first, and a save then found nothing waiting and left the file with the location
    // placed and the parts in the stray gone.
    FakeWorld Make(out ZoneReset.Memory memory, out ZoneReset.Work work, out IEnumerator steps, out Plan plan, out Progress progress)
    {
      var world = new FakeWorld();
      world.Add(home, O(1), O(2)).Add(stray, O(3), O(4)).Add(Z(9, 0), O(5), O(6));
      world.Generated.UnionWith([home, Z(9, 0)]);
      memory = new ZoneReset.Memory();
      plan = ZoneReset.MakePlan(world.Generated, [], [stray], [reach]);
      progress = new Progress();
      work = new ZoneReset.Work(world, plan, new Budget(3, 1_000_000, () => 0), progress, memory);
      steps = work.Steps();
      steps.MoveNext();
      return world;
    }

    var w = Make(out var memory, out var work, out var steps, out var plan, out _);
    C(Show(plan.Turns) == "0,0 1,0 9,0" && memory.Pending.SetEquals([home]) && w.Destroyed.SetEquals([Id(1), Id(2)]) && !w.Calls.Contains("list 1,0"),
      "(the first frame emptied the home, which waits for the stray, though the players are at the stray)");
    w.Flush();
    C(FinalHalfGroup(w, memory, plan.GroupMembers(home)), "(control: a save with nothing done about it would hold the group half done: the home emptied, the stray untouched)");
    work.BeforeSave();
    C(!FinalHalfGroup(w, memory, plan.GroupMembers(home)) && w.Destroyed.Contains(Id(3)) && w.Destroyed.Contains(Id(4)) && memory.Pending.Count == 0 && w.Calls.Contains("unplace 0,0"),
      "a save takes the stray's turn: the group is wholly emptied in what is saved, and the home is finished");
    C(!w.Destroyed.Contains(Id(5)) && !w.Destroyed.Contains(Id(6)), "(and the zone of no group is not touched)");
    while (steps.MoveNext())
    {
    }
    C(w.Calls.Count(c => c == "list 1,0") == 1 && w.Calls.Count(c => c == "list 0,0") == 1 && w.Generated.Count == 0, "the run goes on from there: no turn is taken twice");

    // The same with a second request in place of the save: the zone that waits is carried into the next run, whose plan has the stray again.
    w = Make(out memory, out work, out steps, out plan, out _);
    work.FinishInFlight();
    var again = ZoneReset.MakePlan(w.Generated, [], [stray], [reach], memory.Pending);
    C(again.Carried.SetEquals([home]) && Show(again.Strays) == "1,0" && Show(again.Turns) == "0,0 1,0 9,0", "after a second request the home is carried into the next plan, and the stray is in it as before: " + Show(again.Turns));
    var (_, second, _) = Go(w, again, Roomy(), memory);
    C(w.Destroyed.Count == 6 && memory.Pending.Count == 0 && second.Reset == 2 && second.Done == second.Total, "the next run empties the stray and finishes the home: nothing is left waiting");

    // A save when the nearest zone of the group is left alone at its turn: the whole group is left alone, the stray included, and the file is as it was.
    w = new FakeWorld();
    w.Add(home, O(1), O(2, Kind.Piece)).Add(stray, O(3), O(4));
    w.Generated.Add(home);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [stray], [reach]);
    work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    work.BeforeSave();
    C(w.Destroyed.Count == 0 && memory.Pending.Count == 0 && !w.Calls.Contains("list 1,0"), "a piece in the home at its turn: the group is left alone with the stray, which is not listed, and a save has nothing to take");
  }

  // ------------------------------------------------------------------------------------------------ a zone has its turn once

  private static void ReviewTurnTests()
  {
    Section("review: a zone has its turn once, though a save took it while the work waited for the next frame");
    var home = Z(0, 0);
    var beside = Z(1, 0);
    var far = Z(9, 0);
    var reach = LocIn(0, 0, 20, 0, 20);
    var w = new FakeWorld();
    w.Add(home, O(1), O(2)).Add(beside, O(3), O(4)).Add(far, O(5), O(6));
    w.Generated.UnionWith([home, beside, far]);
    var memory = new ZoneReset.Memory();
    var plan = ZoneReset.MakePlan(w.Generated, [], [home], [reach]);
    var progress = new Progress();
    var work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    // The listing and the home's two objects spend the frame: the work waits at the top of its loop, for the zone beside the home.
    steps.MoveNext();
    C(Show(plan.Turns) == "0,0 1,0 9,0" && memory.Pending.SetEquals([home]) && !w.Calls.Contains("list 1,0"), "(the work waits for the next frame with the zone beside the home next in line)");
    work.BeforeSave();
    C(w.Calls.Count(c => c == "list 1,0") == 1 && w.Destroyed.Contains(Id(3)) && memory.Pending.Count == 0, "(the save has taken that zone's turn, and finished the home)");
    while (steps.MoveNext())
    {
    }
    C(w.Calls.Count(c => c == "list 0,0") == 1 && w.Calls.Count(c => c == "list 1,0") == 1 && w.Calls.Count(c => c == "list 9,0") == 1, "when the work goes on, no zone is listed twice");
    C(w.Calls.Count(c => c == "unplace 0,0") == 1 && w.Calls.Count(c => c == "unplace 1,0") == 1 && w.Calls.Count(c => c == "unplace 9,0") == 1, "... nor finished twice");
    C(progress.Total == 3 && progress.Done == 3 && progress.Reset == 3 && progress.Percent == 100 && memory.Pending.Count == 0 && w.Destroyed.Count == 6,
      $"the progress ends at exactly 100%: {progress.Done} of {progress.Total} done, {progress.Reset} reset");

    // The same while the work waits among a group's zones, with the save taking several turns: each is taken once.
    var (rowZones, rowLocations) = FinalChain(4);
    w = new FakeWorld();
    uint next = 1;
    w.Fill(rowZones, 2, ref next);
    w.Fill([far], 2, ref next);
    w.Generated.UnionWith(rowZones);
    w.Generated.Add(far);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [Z(0, 0)], rowLocations);
    progress = new Progress();
    work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), progress, memory);
    steps = work.Steps();
    steps.MoveNext();
    work.BeforeSave();
    while (steps.MoveNext())
    {
    }
    C(rowZones.All(z => w.Calls.Count(c => c == $"list {z.x},{z.y}") == 1 && w.Calls.Count(c => c == $"unplace {z.x},{z.y}") == 1) && progress.Done == 5 && progress.Total == 5,
      "a group of four whose three other zones a save took: each listed once, finished once, and the progress is 5 of 5");
  }

  // ------------------------------------------------------------------------------------------------ a stray the game generated

  private static void ReviewPromoteTests()
  {
    Section("review: a stray the game generated before its turn is a zone to reset like the others");
    var home = Z(0, 0);
    var stray = Z(1, 0);
    var reach = LocIn(0, 0, 20, 0, 20);

    // Something that protects was put in it by the time of its turn: it is left alone, and counted for it: the zone is one of the plan now.
    // (The home fills the first frame; the stray is generated as the second begins, and a player's square is looked at then.)
    foreach (var how in new[] { "a piece", "a player's square" })
    {
      var w = new FakeWorld();
      w.Add(home, O(1), O(2)).Add(stray, O(3), O(4));
      w.Generated.Add(home);
      w.OnNewFrame = () =>
      {
        if (w.Frames != 2)
          return;
        w.Generated.Add(stray);
        w.Add(stray, O(60), O(61));
        if (how == "a piece")
          w.Add(stray, O(70, Kind.Piece));
        else
          w.Live.Add(new ZoneReset.Protector(stray, 0));
      };
      var plan = ZoneReset.MakePlan(w.Generated, [], [home], [reach]);
      var (_, progress, _) = Go(w, plan, new Budget(3, 1_000_000, () => 0));
      C(new uint[] { 3, 4, 60, 61, 70 }.All(n => !w.Destroyed.Contains(Id(n))) && w.Generated.Contains(stray) && !w.Calls.Contains("ungenerate 1,0"),
        $"{how} in the generated stray at its turn: it is left as it is, not emptied, not unplaced, ungenerated or rooted");
      C(progress.Total == 2 && progress.Done == 2 && progress.Reset == 1 && progress.Skipped == 1 && progress.SkippedForLocations == 0,
        $"and it is counted from the moment it is generated: two zones, one reset and one left alone after all ({progress.Done} of {progress.Total}, {progress.Reset} reset, {progress.Skipped} left alone)");
      C(w.Calls.Contains("unplace 0,0") && ZoneReset.FinishLine(progress, 1, 0).Contains("1 zone was left alone after all"), "(the home is finished, the case that cannot be undone; the finish line counts the zone left alone)");
    }

    // The zones waiting beside it wait for its turn too: the plan did not count the stray among the zones that A and the home wait for, and
    // once the game has generated it they do. The stray (1,0) is in the 3 x 3 of A (1,-1) and of the home (0,0), and A waits for B (2,0) as well.
    var a = Z(1, -1);
    var b = Z(2, 0);
    var scene = new FakeWorld();
    scene.Add(a, O(10), O(11)).Add(home, O(1), O(2)).Add(stray, O(3), O(4)).Add(b, O(20), O(21));
    scene.Generated.UnionWith([a, home, b]);
    scene.OnList = zone =>
    {
      if (!zone.Equals(home))
        return;
      scene.Generated.Add(stray);
      scene.Add(stray, O(60), O(61));
    };
    var sceneMemory = new ZoneReset.Memory();
    var scenePlan = ZoneReset.MakePlan(scene.Generated, [], [Z(1, -6)], [reach]);
    C(Show(scenePlan.Turns) == "1,-1 0,0 1,0 2,0" && Show(scenePlan.Strays) == "1,0", "(A is first, then the home and its stray, then B: " + Show(scenePlan.Turns) + ")");
    var (_, sceneProgress, _) = Go(scene, scenePlan, Roomy(), sceneMemory);
    int lastOfB = scene.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is 20 or 21);
    int lastOfStray = scene.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is 3 or 4 or 60 or 61);
    C(scene.Calls.IndexOf("unplace 1,-1") > lastOfB && scene.Calls.IndexOf("unplace 1,0") > lastOfB, "A is finished after B's turn, and so is the stray: both wait for B, and A for the stray: the stray adds to what A waits for");
    C(scene.Calls.IndexOf("unplace 0,0") > lastOfStray && scene.Calls.IndexOf("unplace 0,0") < scene.Calls.IndexOf("list 2,0"), "the home is finished as soon as the stray's turn is over, and does not wait for B: it is not beside it");
    C(sceneProgress.Total == 4 && sceneProgress.Done == 4 && sceneProgress.Reset == 4 && sceneMemory.Pending.Count == 0, $"all four are reset and nothing is left waiting: {sceneProgress.Done} of {sceneProgress.Total}");
    C(scene.Calls.Count(c => c.StartsWith("unplace")) == 4 && new[] { "unplace 1,-1", "unplace 0,0", "unplace 1,0", "unplace 2,0" }.All(scene.Calls.Contains), "each is finished once");

    // The counters stay right when it is generated and left alone: the zones that waited for it do not wait for ever.
    scene = new FakeWorld();
    scene.Add(a, O(10), O(11)).Add(home, O(1), O(2)).Add(stray, O(3), O(4)).Add(b, O(20), O(21));
    scene.Generated.UnionWith([a, home, b]);
    scene.OnList = zone =>
    {
      if (!zone.Equals(home))
        return;
      scene.Generated.Add(stray);
      scene.Add(stray, O(70, Kind.Piece));
    };
    sceneMemory = new ZoneReset.Memory();
    (_, sceneProgress, _) = Go(scene, ZoneReset.MakePlan(scene.Generated, [], [Z(1, -6)], [reach]), Roomy(), sceneMemory);
    C(sceneProgress.Total == 4 && sceneProgress.Done == 4 && sceneProgress.Reset == 2 && sceneProgress.Skipped == 2 && sceneMemory.Pending.Count == 0
      && scene.Calls.Contains("unplace 1,-1") && scene.Calls.Contains("unplace 0,0") && !scene.Destroyed.Contains(Id(20)),
      $"a generated stray that is left alone, and B with it (it is beside the stray): A and the home are finished, none is stuck ({sceneProgress.Done} of {sceneProgress.Total}, {sceneProgress.Reset} reset)");

    // A stray the game never generates is as it was: not counted, emptied with its group, and never finished. (One generated before a save
    // takes its turn is in the final round's drain test.)
    var plain = new FakeWorld();
    plain.Add(home, O(1), O(2)).Add(stray, O(3), O(4));
    plain.Generated.Add(home);
    var plainPlan = ZoneReset.MakePlan(plain.Generated, [], [home], [reach]);
    var (_, plainProgress, _) = Go(plain, plainPlan, Roomy());
    C(plainProgress.Total == 1 && plainProgress.Done == 1 && plain.Destroyed.Count == 4 && !plain.Calls.Contains("unplace 1,0"), "a stray the game never generated: not counted, listed, emptied, and never finished");
  }

  // ------------------------------------------------------------------------------------------------ the drain, zone by zone

  private static void ReviewDrainTests()
  {
    Section("review: a save sends each zone's objects on their own, so that no message holds a whole group's");
    var (rowZones, rowLocations) = FinalChain(5);
    var w = new FakeWorld();
    uint next = 1;
    w.Fill(rowZones, 10, ref next);
    w.Generated.UnionWith(rowZones);
    var memory = new ZoneReset.Memory();
    var plan = ZoneReset.MakePlan(w.Generated, [], [Z(0, 0)], rowLocations);
    // The first zone has the frame's 11 (the listing and its 10 objects); it waits for the other four, which a save takes the turns of.
    var work = new ZoneReset.Work(w, plan, new Budget(11, 1_000_000, () => 0), new Progress(), memory);
    var steps = work.Steps();
    steps.MoveNext();
    C(memory.Pending.SetEquals([Z(0, 0)]) && w.Destroyed.Count == 10 && !w.Calls.Contains("list 1,0"), "(the first zone of five is emptied and waits for the other four)");
    // The game sends what a frame destroyed as the next begins.
    w.Flush();
    var messages = new List<int>();
    w.OnFlush = () => messages.Add(w.Destroyed.Count - w.Flushed.Count);
    work.BeforeSave();
    C(w.Destroyed.Count == 50 && memory.Pending.Count == 0 && w.Flushed.Count == 50, "the save takes the turns of the other four: all 50 objects are gone and sent");
    C(messages.Take(4).SequenceEqual([10, 10, 10, 10]) && messages.Skip(4).All(n => n == 0), $"each zone's 10 objects are sent after its turn, and not 40 at the end: {string.Join(", ", messages)}");

    // A zone with nothing in it sends nothing: the queue is empty, and the game's send returns at once.
    w = new FakeWorld();
    next = 1;
    w.Fill([Z(0, 0)], 3, ref next);
    w.Add(Z(1, 0));
    w.Generated.UnionWith([Z(0, 0)]);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [Z(0, 0)], [LocIn(0, 0, 20, 0, 20)]);
    work = new ZoneReset.Work(w, plan, new Budget(4, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    w.Flush();
    messages.Clear();
    w.OnFlush = () => messages.Add(w.Destroyed.Count - w.Flushed.Count);
    work.BeforeSave();
    C(memory.Pending.Count == 0 && messages.Count > 0 && messages.All(n => n == 0), "a stray with nothing in it queues nothing: every message is empty");
  }

  // ------------------------------------------------------------------------------------------------ an error before the work

  private static void ReviewErrorTests()
  {
    Section("review: an error before the work was made finishes the zones an earlier run left waiting, and tells at most three failures");
    using var seams = new Seams();
    var oldDelta = ZoneRegen.DeltaTime;
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      // A player far away keeps the frames small and makes the first frame ask the clock, which fails at once: before any plan.
      var away = w.Add(Z(-40, -40), "Player", user: 700L);
      away.SetOwner(700L);
      w.Peer(away, 700L);
      ZoneRegen.DeltaTime = () => throw new InvalidOperationException("the clock failed at once");
      ZoneRegen.FrameClock = () => 0.0;

      // An earlier run emptied a zone and was stopped by a second request: it waits, bare and generated, with its location placed and its
      // loaded copy there. The run that fails before it has a plan must not leave it so.
      var home = Z(0, 0);
      w.Generated.Add(home);
      w.PlaceLocation(home, true, 20f, dx: 20f);
      w.Load(home);
      w.Generated.Add(Z(7, 7));
      w.Add(Z(7, 7), "Pine");
      ZoneRegen.MemoryFor(w.Zones).Pending.Add(home);
      var lines = new List<string>();
      var job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      var run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      var log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
          w.Flush();
      });
      C(job.Work == null && lines.Contains("Zone regeneration stopped by an error, see the log."), "(the error came before there was any work: " + string.Join(" | ", lines) + ")");
      C(!w.Generated.Contains(home) && !w.Zones.m_locationInstances[home].m_placed && !w.Loaded.Contains(home) && ZoneRegen.MemoryFor(w.Zones).Pending.Count == 0,
        "the zone the earlier run left waiting is finished: not generated, its location to be placed again, its loaded copy gone, no longer remembered as waiting");
      C(log.Any(l => l.Contains(ZoneReset.PendingLine(1))) && w.Generated.Contains(Z(7, 7)) && w.LiveIn(Z(7, 7)).Count == 1, "the log says what was finished, and the zones nobody reached are as they were");
      C(GetStatic<ZoneRegen.Job?>(typeof(ZoneRegen), "job") == null, "(the run is over)");

      // Nothing waits: nothing is claimed, as before.
      ZDOExtraData.Reset();
      w = new World();
      var away2 = w.Add(Z(-40, -40), "Player", user: 700L);
      away2.SetOwner(700L);
      w.Peer(away2, 700L);
      w.Generated.Add(home);
      lines = [];
      job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
          w.Flush();
      });
      C(lines.Contains("Zone regeneration stopped by an error, see the log.") && w.Generated.Contains(home) && !log.Any(l => l.Contains("it had emptied") || l.Contains("could not be finished")),
        "with no zone waiting, an error before the work finishes nothing and says nothing of it");

      // A world that is gone: nothing is finished, and nothing throws.
      ZDOExtraData.Reset();
      w = new World();
      var away3 = w.Add(Z(-40, -40), "Player", user: 700L);
      away3.SetOwner(700L);
      w.Peer(away3, 700L);
      w.Generated.Add(home);
      ZoneRegen.MemoryFor(w.Zones).Pending.Add(home);
      lines = [];
      job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      World.Close();
      log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
        {
        }
      });
      C(lines.Contains("Zone regeneration stopped by an error, see the log.") && !log.Any(l => l.Contains("it had emptied")) && w.Generated.Contains(home),
        "in a world that is gone nothing is finished, and the error is told all the same: " + string.Join(" | ", lines.Concat(log.Select(l => l.Length > 100 ? l[..100] : l))));

      // Five zones that cannot be finished (the ground's height cannot be asked for): the first three are told with their error, the rest counted.
      ZDOExtraData.Reset();
      w = new World();
      var away4 = w.Add(Z(-40, -40), "Player", user: 700L);
      away4.SetOwner(700L);
      w.Peer(away4, 700L);
      var waiting = Enumerable.Range(0, 5).Select(x => Z(x, 0)).ToList();
      foreach (var zone in waiting)
      {
        w.Generated.Add(zone);
        w.PlaceLocation(zone, true, 0f);
        ZoneRegen.MemoryFor(w.Zones).Pending.Add(zone);
      }
      ZoneRegen.GroundHeight = (x, z) => throw new InvalidOperationException($"no ground at {x}");
      lines = [];
      job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
          w.Flush();
      });
      var told = log.Where(l => l.StartsWith("[Error]") && l.Contains("could not be finished:")).ToList();
      C(told.Count == 3 && told.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"zone (\d+),0").Groups[1].Value).SequenceEqual(["0", "1", "2"]) && told.All(l => l.Contains("InvalidOperationException") && l.Contains("no ground at")),
        "five zones cannot be finished: the first three, in order, are told with their error: " + string.Join(" | ", told.Select(l => l.Length > 110 ? l[..110] : l)));
      C(log.Count(l => l.Contains(ZoneReset.UnfinishedLine(2))) == 1 && !log.Any(l => l.Contains("it had emptied")), "and the other two are counted in one line, and nothing is said to be finished: " + string.Join(" | ", log.Where(l => l.Contains("more zone")).Select(l => l.Length > 140 ? l[..140] : l)));
      C(ZoneRegen.MemoryFor(w.Zones).Pending.SetEquals(waiting) && waiting.All(w.Generated.Contains), "(they wait as they were, for the next request to carry them)");
      // Three or fewer: no count line.
      ZDOExtraData.Reset();
      w = new World();
      var away5 = w.Add(Z(-40, -40), "Player", user: 700L);
      away5.SetOwner(700L);
      w.Peer(away5, 700L);
      foreach (var zone in waiting.Take(3))
      {
        w.Generated.Add(zone);
        w.PlaceLocation(zone, true, 0f);
        ZoneRegen.MemoryFor(w.Zones).Pending.Add(zone);
      }
      lines = [];
      job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
          w.Flush();
      });
      C(log.Count(l => l.Contains("could not be finished:")) == 3 && !log.Any(l => l.Contains("more zone")), "three failures are all told, and no count line follows");
    }
    finally
    {
      ZoneRegen.DeltaTime = oldDelta;
      SetStatic(typeof(ZoneRegen), "job", null);
      World.Close();
    }

    // The words.
    C(ZoneReset.UnfinishedLine(1) == "Zone regeneration: 1 more zone could not be finished either. 'bc regen' tries it again." && ZoneReset.UnfinishedLine(1234) == "Zone regeneration: 1,234 more zones could not be finished either. 'bc regen' tries them again.",
      "the count line in the singular and the plural: " + ZoneReset.UnfinishedLine(1) + " | " + ZoneReset.UnfinishedLine(1234));
    string[] banned = ["Upgrade World", "Expand World", "replace", "obsolete", "no longer need", "instead of"];
    C(banned.All(x => !ZoneReset.UnfinishedLine(2).Contains(x, StringComparison.OrdinalIgnoreCase)) && !System.Text.RegularExpressions.Regex.IsMatch(ZoneReset.UnfinishedLine(2), @"\d+\.\d+\.\d+"), "it names no other mod and no version");
    C(ZoneRegen.MaxFailuresLogged == 3, "(three failures are told one by one)");
  }
}

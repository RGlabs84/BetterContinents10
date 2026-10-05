// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// What the final round of zone regeneration changed, and the glue the review found unchecked: a save takes the turns of the zones of a
// location group (a file holds a group wholly emptied or untouched), the work counts what a zone waits for instead of walking the
// group (against the reviewed work as a reference model), a location is linked by its exterior radius only, the zones a location
// reaches that were never generated are cleared with it (strays), an error finishes the zones it had left waiting, the debug reset
// drops the builds and the grass made from the old settings (distant terrain, the builder's ready list, the grass a frame later),
// a zone's loaded copy leaves the game's lists at once, and the calls between ZoneRegen's pieces that no check had reached.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Budget = BetterContinents.ZoneReset.Budget;
using Edges = BetterContinents.ZoneReset.Edges;
using Kind = BetterContinents.ZoneReset.Kind;
using Plan = BetterContinents.ZoneReset.Plan;
using Progress = BetterContinents.ZoneReset.Progress;

internal static partial class Program
{
  private static void FinalTests()
  {
    // The clock is Unity's, which the tests do not have: a frame is a sixtieth of a second.
    ZoneRegen.DeltaTime = () => 1f / 60f;
    FinalRadiusTests();
    FinalStrayPlanTests();
    FinalStrayWorkTests();
    FinalCounterTests();
    FinalReferenceTests();
    FinalDrainTests();
    FinalErrorTests();
    FinalPokeTests();
    FinalRootTests();
    FinalGlueTests();
    FinalFactTests();
  }

  // ------------------------------------------------------------------------------------------------ helpers

  // The calls a method makes, in order (call and callvirt), by name and declaring type.
  private static List<(string Name, Type? Type)> FinalCalls(MethodBase method) =>
    Callees(method).Select(m => (m.Name, m.DeclaringType)).ToList();

  // Unity's `if (obj)` is false for an object whose native half is gone (its pointer is zero), which is what a made-up one is. Reflection's
  // SetValue on the pointer would run UnityEngine.Object's type initializer, which reaches into the engine; IL does not.
  private static readonly Action<UnityEngine.Object, IntPtr> FinalSetPointer = FinalMakePointerSetter();

  private static Action<UnityEngine.Object, IntPtr> FinalMakePointerSetter()
  {
    var field = typeof(UnityEngine.Object).GetField("m_CachedPtr", Any)!;
    var method = new DynamicMethod("FinalSetPointer", typeof(void), [typeof(UnityEngine.Object), typeof(IntPtr)], typeof(Program).Module, true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Stfld, field);
    il.Emit(OpCodes.Ret);
    return (Action<UnityEngine.Object, IntPtr>)method.CreateDelegate(typeof(Action<UnityEngine.Object, IntPtr>));
  }

  // An object of the game's that looks alive to `if (obj)`. It has none of Unity's insides: asking it for its id, as Equals and GetHashCode do,
  // would read memory at its pointer, so it is never compared or put in a collection that compares.
  private static T FinalAlive<T>() where T : UnityEngine.Object
  {
    var made = Make<T>();
    FinalSetPointer(made, (IntPtr)1);
    return made;
  }

  // The zones a location group of `length` zones in a row makes: each one's location reaches the next.
  private static (List<Vector2s> Zones, List<ZoneReset.PlacedLocation> Locations) FinalChain(int length, int y = 0)
  {
    var zones = Enumerable.Range(0, length).Select(x => Z(x, y)).ToList();
    return (zones, Enumerable.Range(0, length - 1).Select(x => LocIn(x, y, 20, 0, 20)).ToList());
  }

  // ------------------------------------------------------------------------------------------------ F2: the radius that links

  private static void FinalRadiusTests()
  {
    Section("final: a location is linked by its exterior radius, and TouchedZones' edges");
    // The interior of a dungeon is built inside one zone: a location whose interior radius is the larger of its two links no more than
    // its exterior does.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      w.Zones.m_locationInstances[Z(0, 0)] = new ZoneSystem.LocationInstance
      {
        m_location = new ZoneSystem.ZoneLocation { m_exteriorRadius = 12f, m_interiorRadius = 90f }, m_position = new Vector3(0f, 30f, 0f), m_placed = true
      };
      w.Zones.m_locationInstances[Z(5, 0)] = new ZoneSystem.LocationInstance
      {
        m_location = new ZoneSystem.ZoneLocation { m_exteriorRadius = 30f, m_interiorRadius = 8f }, m_position = new Vector3(5 * 64f + 20f, 30f, 0f), m_placed = true
      };
      var placed = ZoneRegen.PlacedLocations(w.Zones).OrderBy(l => l.Zone.x).ToList();
      C(placed[0].Radius == 12f && placed[1].Radius == 30f, "a location reaches as far as its exterior radius, whichever of its radii is larger");
      var plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 0), Z(6, 0)], [], [Z(0, 0)], placed);
      C(plan.Groups.Count == 1 && Sorted(plan.GroupMembers(Z(5, 0))) == "5,0 6,0" && plan.GroupMembers(Z(0, 0)).Count == 0,
        "the group is made by the exterior radius: an interior of 90 m links nothing, an exterior of 30 m links its neighbour");
    }
    finally
    {
      World.Close();
    }

    // TouchedZones at the edge: a zone spans [n * 64 - 32, n * 64 + 32) (ZoneSystem.GetZone floors (x + 32) / 64), so a circle that just
    // touches the lower edge of the zone is still in its own zone, and one that touches the upper edge is in the next.
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 32f))) == "0,0 0,1 1,0", "a radius of exactly 32 m from the middle: it ends where the zones to the east and north begin and reaches them, and does not reach the zones to the west and south");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 32.01f))) == "-1,0 0,-1 0,0 0,1 1,0", "a hair more reaches the west and south too (and not the corners)");
    C(!ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 52f)).Contains(Z(-1, 0)) && ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 52f)).Contains(Z(1, 0)),
      "20 m east of the middle with a radius of 52, its west edge is exactly at the zone's: the zone to the west is not reached, the one to the east is");
    C(ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 52.01f)).Contains(Z(-1, 0)), "a hair more and the zone to the west is");
    C(!ZoneReset.TouchedZones(LocIn(0, 0, 0, 20, 52f)).Contains(Z(0, -1)) && ZoneReset.TouchedZones(LocIn(0, 0, 0, 20, 52f)).Contains(Z(0, 1)),
      "the same in z: with the south edge exactly at the zone's, the zone to the south is not reached, the one to the north is");
    C(ZoneReset.TouchedZones(LocIn(0, 0, 0, 20, 52.01f)).Contains(Z(0, -1)), "a hair more and the zone to the south is");
    C(ZoneReset.TouchedZones(LocIn(0, 0, -20, 0, 52f)).Contains(Z(1, 0)) && !ZoneReset.TouchedZones(LocIn(0, 0, -20, 0, 51.99f)).Contains(Z(1, 0)),
      "(20 m west with a radius of 52, the east edge ends exactly where the zone to the east begins, and reaches it: the upper side counts; a hair less does not)");
  }

  // ------------------------------------------------------------------------------------------------ F5: strays, in the plan

  private static void FinalStrayPlanTests()
  {
    Section("final: strays in the plan, the zones a location reaches that nobody generated");
    var reach = LocIn(0, 0, 20, 0, 20);

    // A group that nothing covers is reset whole: the zone it reaches into that is not generated is a stray, with a turn of its own.
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [], [Z(1, 0)], [reach]);
    C(Show(plan.Strays) == "1,0" && Show(plan.Reset) == "0,0 5,5" && Show(plan.Turns) == "0,0 1,0 5,5",
      "the stray is not among the zones to reset, and has its turn right after the zone of its group that is reset, though it is nearer the players itself: " + Show(plan.Turns));
    C(plan.Generated == 2 && plan.Kept.Count == 0 && ZoneReset.StartLine(plan).Contains("resetting 2 of 2 generated zones"), "it is no generated zone, and no count of the plan or its first line includes it: " + ZoneReset.StartLine(plan));
    var without = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [], [Z(1, 0)]);
    C(without.Strays.Count == 0 && Show(without.Reset) == Show(plan.Reset) && Show(without.Turns) == Show(without.Reset), "the zones to reset come in the same order with or without it");

    // A group something covers is kept whole, and nothing of it is touched, strays included.
    plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [Z(2, 0)], [], [reach]);
    C(Sorted(plan.Kept) == "0,0" && plan.Strays.Count == 0 && Show(plan.Turns) == "5,5", "something near the stray keeps the home, and the stray is left as it is");
    plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 5)], [Z(-1, 0)], [], [reach]);
    C(Sorted(plan.Kept) == "0,0" && plan.Strays.Count == 0, "something near the home keeps the group: no strays");
    // The home is carried (emptied by an earlier run) and covered: it is reset all the same, but the group is covered, so its stray is left.
    plan = ZoneReset.MakePlan([Z(0, 0)], [Z(-1, 0)], [], [reach], [Z(0, 0)]);
    C(Show(plan.Reset) == "0,0" && plan.Strays.Count == 0, "a group a protector covers has no strays even when its only generated zone is a carried one");

    // A group with no generated zone has no home to put the location down again: nothing of it is cleared.
    plan = ZoneReset.MakePlan([Z(5, 5)], [], [], [reach]);
    C(plan.Strays.Count == 0 && Show(plan.Reset) == "5,5", "a location whose home is not generated is nothing to clear");
    // Every zone of the group is generated: nothing is a stray.
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0)], [], [], [reach]);
    C(plan.Strays.Count == 0 && Show(plan.Reset) == "0,0 1,0", "a group whose zones are all generated has none");

    // A chain: the home reaches the second zone, which reaches a third that nobody generated.
    var (_, chain) = FinalChain(3);
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0)], [], [Z(0, 0)], chain);
    C(Show(plan.Strays) == "2,0" && plan.GroupMembers(Z(0, 0)).Count == 3, "a chain of two locations: the third zone is a stray of the whole group");
    // Two groups, each with its own; the turns are in one order by distance, ties by x then y.
    var second = LocIn(0, 9, -20, 0, 20);
    plan = ZoneReset.MakePlan([Z(0, 0), Z(0, 9)], [], [Z(0, 5)], [reach, second]);
    C(Show(plan.Strays) == "-1,9 1,0" && Show(plan.Turns) == "0,9 -1,9 0,0 1,0", "two groups: the strays are in the one order with the zones to reset, by distance: " + Show(plan.Turns));
    // The zones of a group already generated and the others are told apart by the generated set, not by who is protected.
    plan = ZoneReset.MakePlan([Z(1, 0)], [], [], [reach]);
    C(Show(plan.Strays) == "0,0" && Show(plan.Reset) == "1,0", "(any zone of the group that is not generated is a stray: here the home)");
    // A location that links nothing makes no group: nothing is a stray.
    plan = ZoneReset.MakePlan([Z(0, 0)], [], [], [LocIn(0, 0, 0, 0, 5f)]);
    C(plan.Strays.Count == 0 && plan.Turns.Count == 1, "a location inside its zone has no strays");
  }

  // ------------------------------------------------------------------------------------------------ F5: strays, in the work

  private static void FinalStrayWorkTests()
  {
    Section("final: strays in the work, a location reaching an ungenerated zone is not copied by every reset");
    var home = Z(0, 0);
    var stray = Z(1, 0);
    var reach = LocIn(0, 0, 20, 0, 20);

    // The game: when the home generates again it puts the whole location down, its parts in the zone beside it too.
    FakeWorld Game(out Func<Vector2s, int> live)
    {
      var game = new FakeWorld();
      game.Add(home, O(1), O(2)).Add(stray, O(3), O(4));
      game.Generated.Add(home);
      uint fresh = 100;
      game.OnUngenerate = zone =>
      {
        if (!zone.Equals(home))
          return;
        game.Add(home, O(fresh++), O(fresh++));
        game.Add(stray, O(fresh++), O(fresh++));
        game.Generated.Add(home);
      };
      live = zone => game.Objects[zone].Count(o => !game.Destroyed.Contains(o.Id));
      return game;
    }

    var world = Game(out var live);
    for (int run = 1; run <= 3; run++)
    {
      var plan = ZoneReset.MakePlan(world.Generated, [], [home], [reach]);
      var (_, progress, _) = Go(world, plan, Roomy());
      C(live(stray) == 2 && live(home) == 2 && progress.Reset == 1 && progress.Done == 1 && progress.Total == 1,
        $"regeneration {run}: exactly one copy of the location, in the home and in the zone beside it ({live(home)} and {live(stray)} objects); the stray is no zone of the run");
    }
    C(!world.Calls.Contains("unplace 1,0") && !world.Calls.Contains("ungenerate 1,0") && !world.Calls.Contains("root 1,0") && world.Calls.Count(c => c == "list 1,0") == 3,
      "the stray is listed and emptied each time and never unplaced, ungenerated or rooted: it has none of that");

    // The control: with no location handed to the plan, as it was, the parts stand on one another and every reset adds a copy.
    world = Game(out live);
    for (int run = 1; run <= 2; run++)
    {
      Go(world, ZoneReset.MakePlan(world.Generated, [], [home]), Roomy());
      C(live(stray) == 2 + 2 * run, $"(control, run {run}: without the stray the zone beside holds {live(stray)} objects, a copy more every time)");
    }

    // The home waits for the stray's turn, whoever the players are nearest to: the stray's turn is the one after the home's.
    foreach (var focus in new[] { home, stray })
    {
      world = Game(out live);
      var plan = ZoneReset.MakePlan(world.Generated, [], [focus], [reach]);
      Go(world, plan, Roomy());
      int lastPart = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is >= 1 and <= 4);
      C(Show(plan.Turns) == "0,0 1,0" && world.Calls.IndexOf("unplace 0,0") > lastPart && world.Calls.IndexOf("list 0,0") >= 0 && world.Calls.IndexOf("list 1,0") > world.Calls.IndexOf("list 0,0"),
        $"players at the {(focus.Equals(home) ? "home" : "stray")}: the home's turn comes first, and it is finished only after the last old part, in both zones, is gone");
    }

    // A stray that somebody has come near since the plan is generated by its turn: it holds a fill of its own besides the old parts of the
    // location, and left as it was it would hold two copies of them once the home generates again. It is a zone of the plan from then on:
    // counted, listed, emptied and finished like any, and the home waits for its turn.
    world = Game(out live);
    world.OnList = zone =>
    {
      if (zone.Equals(home))
      {
        world.Generated.Add(stray);
        world.Add(stray, O(60), O(61));
      }
    };
    world.Add(Z(9, 0), O(80));
    world.Generated.Add(Z(9, 0));
    var strayPlan = ZoneReset.MakePlan([home, Z(9, 0)], [], [home], [reach]);
    C(Show(strayPlan.Strays) == "1,0" && Show(strayPlan.Turns) == "0,0 1,0 9,0", "(the stray, when the plan is made)");
    var (_, generatedProgress, _) = Go(world, strayPlan, Roomy());
    C(world.Calls.Contains("list 1,0") && new uint[] { 3, 4, 60, 61 }.All(n => world.Destroyed.Contains(Id(n))),
      "a stray that is generated by its turn is listed and emptied like any zone: the old parts of the location and the fill the game made go");
    C(generatedProgress.Total == 3 && generatedProgress.Reset == 3 && generatedProgress.Done == 3 && generatedProgress.Skipped == 0,
      $"and it is counted from then on: three zones to reset, three reset, two of them planned ({generatedProgress.Total}, {generatedProgress.Reset}, {generatedProgress.Done})");
    int lastOfStray = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is 3 or 4 or 60 or 61);
    C(world.Calls.IndexOf("unplace 0,0") > lastOfStray && world.Calls.IndexOf("unplace 0,0") < world.Calls.IndexOf("list 9,0"),
      "the home waits for its turn, and is finished as soon as that is over, before the next zone is looked at");
    C(world.Calls.Contains("unplace 1,0") && world.Calls.Contains("ungenerate 1,0") && world.Calls.Contains("root 1,0") && !world.Generated.Contains(stray),
      "the stray is finished too: its location is to be placed again (it has none), it is not generated, its loaded copy is gone");
    C(live(stray) == 2 && live(home) == 2, $"when the home generates again there is one copy of the location, in the home and in the zone beside it ({live(home)} and {live(stray)} objects), not two");

    // Something that protects stands in the stray at its turn: it is left alone. Nothing of the run counts it.
    world = Game(out live);
    world.Add(stray, O(50, Kind.Piece));
    var (_, protectedProgress, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [home], [reach]), Roomy());
    C(!world.Destroyed.Contains(Id(3)) && !world.Destroyed.Contains(Id(4)) && !world.Destroyed.Contains(Id(50)), "a piece in the stray at its turn: the stray is left as it is, the piece and the old parts");
    C(protectedProgress.Skipped == 0 && protectedProgress.Done == 1 && protectedProgress.Reset == 1 && protectedProgress.SkippedForLocations == 0,
      "a stray left alone adds to no count: no zone is left alone after all");
    C(world.Calls.IndexOf("unplace 0,0") >= 0, "(and the emptied home, which cannot be undone, is finished: the case that is documented)");

    // The home holds a piece at its turn: it is left alone, and the stray with it, which is not counted and not touched.
    world = Game(out live);
    world.Add(home, O(50, Kind.Piece));
    var (_, homeAlone, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [home], [reach]), Roomy());
    C(world.Destroyed.Count == 0 && !world.Calls.Contains("list 1,0") && homeAlone.Skipped == 1 && homeAlone.SkippedForLocations == 0 && homeAlone.Done == 1 && homeAlone.Reset == 0,
      "left alone first, the home takes the stray with it: its parts are not touched and the stray adds to no count");

    // The strays that are not beside the home go with it too: a location with a radius of 100 m reaches zones two away, past the 3 x 3 that
    // a zone left alone keeps. None of them is listed or touched, whichever it is.
    world = new FakeWorld();
    world.Add(home, O(1), O(2), O(50, Kind.Piece)).Add(stray, O(3), O(4)).Add(Z(2, 0), O(5), O(6)).Add(Z(0, 2), O(7));
    world.Generated.Add(home);
    var wide = ZoneReset.MakePlan(world.Generated, [], [home], [LocIn(0, 0, 0, 0, 100f)]);
    C(wide.Strays.Contains(Z(2, 0)) && wide.Strays.Contains(Z(0, 2)) && wide.Strays.Contains(stray) && wide.Strays.Count > 8, "(a location with a radius of 100 m reaches " + wide.Strays.Count + " zones that nobody generated)");
    var (_, wideProgress, _) = Go(world, wide, Roomy());
    C(world.Destroyed.Count == 0 && world.Calls.Where(c => c.StartsWith("list")).SequenceEqual(["list 0,0"]) && wideProgress.Skipped == 1 && wideProgress.SkippedForLocations == 0 && wideProgress.Done == 1,
      "a piece in the home at its turn: every stray of its group is left alone with it, the ones two zones away too, and none is listed");

    // The players are nearest the stray, and something protects there that the plan did not know: the home has its turn first all the same
    // (a stray never comes before it), and is emptied; the stray is left alone then, the case that cannot be undone.
    world = Game(out live);
    world.Add(stray, O(50, Kind.Piece));
    var firstPlan = ZoneReset.MakePlan(world.Generated, [], [stray], [reach]);
    C(Show(firstPlan.Turns) == "0,0 1,0", "(the players are nearest the stray, and the home still has its turn first)");
    var (_, aloneProgress, _) = Go(world, firstPlan, Roomy());
    C(world.Destroyed.SetEquals([Id(1), Id(2)]) && aloneProgress.Skipped == 0 && aloneProgress.Done == 1 && aloneProgress.Reset == 1 && world.Calls.Contains("unplace 0,0"),
      "the home is emptied and finished, the stray with the piece is left as it is, and no count includes it");

    // The same for somebody who is near the stray now, or the zone beside a zone left alone.
    world = Game(out live);
    world.Live.Add(new ZoneReset.Protector(stray, 1));
    var (_, nearProgress, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [stray], [reach]), Roomy());
    C(live(home) == 2 && live(stray) == 2 && nearProgress.Skipped == 1 && nearProgress.SkippedForLocations == 0 && nearProgress.Done == 1,
      "a player near the stray (the plan did not know), whose square covers the home too: the home is left alone, and the stray with it, which adds to no count");
    world = Game(out live);
    world.Live.Add(new ZoneReset.Protector(stray, 0));
    (_, nearProgress, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [stray], [reach]), Roomy());
    C(world.Destroyed.SetEquals([Id(1), Id(2)]) && nearProgress.Skipped == 0 && nearProgress.Reset == 1,
      "a player in the stray alone: the home had its turn first and is emptied, and the stray is left alone (the case that cannot be undone)");
    world = Game(out live);
    world.Live.Add(new ZoneReset.Protector(Z(2, 0), 0));
    var late = ZoneReset.MakePlan(world.Generated, [], [home], [reach]);
    (_, nearProgress, _) = Go(world, late, Roomy());
    C(nearProgress.Skipped == 0 && live(home) == 2 && live(stray) == 2 && world.Destroyed.Count == 4, "(a player two zones away protects only their own zone: the group is reset)");
    world = Game(out live);
    world.Live.Add(new ZoneReset.Protector(Z(2, 0), 1));
    (_, nearProgress, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [home], [reach]), Roomy());
    C(world.Destroyed.SetEquals([Id(1), Id(2)]) && nearProgress.Skipped == 0 && nearProgress.Reset == 1,
      "a player whose square covers the stray and not the home: the home had its turn first and is emptied, the stray is left alone (the case that cannot be undone, as for any zone of a group)");

    // Survivors in a stray stay, the rest goes; the stray is no zone to mend the ground beside: a zone that stays on the far side of the
    // stray (something protects the zone after it) meets the stray, and its ground is not touched for it.
    world = Game(out live);
    world.Add(stray, O(60, Kind.Tamed), O(61, Kind.Player | Kind.Local));
    world.Add(Z(2, 0), O(70));
    world.Generated.Add(Z(2, 0));
    var memory = new ZoneReset.Memory();
    var survivors = ZoneReset.MakePlan(world.Generated, [Z(3, 0)], [home], [reach]);
    C(Sorted(survivors.Kept) == "2,0" && Show(survivors.Strays) == "1,0", "(a zone that stays on the other side of the stray)");
    var (_, survivorProgress, _) = Go(world, survivors, Roomy(), memory);
    C(!world.Destroyed.Contains(Id(60)) && !world.Destroyed.Contains(Id(61)) && world.Destroyed.Contains(Id(3)) && survivorProgress.Kept == 2 && survivorProgress.Reset == 1,
      "a tamed animal and a player in a stray stay, counted as objects that stay; the old parts go");
    C(memory.Pending.Count == 0 && world.BorderCalls.Count == 0, "a stray is never waiting, and no ground is mended for it: the zone that stays beside it is not touched");

    // A chain whose last link nobody generated: the stray is the fourth zone, and the group is emptied whole.
    world = new FakeWorld();
    var (chainZones, chainLocations) = FinalChain(4);
    uint next = 1;
    world.Fill(chainZones, 2, ref next);
    world.Add(Z(4, 0), O(next++), O(next++));
    world.Generated.UnionWith(chainZones.Take(3));
    var chainPlan = ZoneReset.MakePlan(world.Generated, [], [Z(0, 0)], chainLocations);
    C(Show(chainPlan.Strays) == "3,0", "(a chain of four zones, the last of which was never generated)");
    Go(world, chainPlan, Roomy());
    C(world.Objects.Where(p => p.Key.x <= 3).All(p => p.Value.All(o => world.Destroyed.Contains(o.Id))) && !world.Destroyed.Contains(Id(9)), "the whole chain is emptied, the stray with it, and the zone beside the chain that no location reaches is not");
  }

  // ------------------------------------------------------------------------------------------------ F2: counters, in the work

  private static void FinalCounterTests()
  {
    Section("final: what a zone waits for is counted, not walked");
    // A group of zones in a row: one location's circle reaches the next. Nothing is finished before the last zone has had its turn, and then
    // all of them are, at once, before the zone beyond the group is touched.
    var (zones, locations) = FinalChain(1500);
    var world = new FakeWorld();
    uint next = 1;
    world.Fill(zones, 1, ref next);
    world.Fill([Z(2000, 0)], 1, ref next);
    world.Generated.UnionWith(zones);
    var plan = ZoneReset.MakePlan(world.Generated.Append(Z(2000, 0)), [], [Z(0, 0)], locations);
    C(plan.Groups.Count == 1 && plan.Groups[0].Length == 1500 && plan.Strays.Count == 0, "(one group of 1,500 zones)");
    var (work, progress, _) = Go(world, plan, Roomy());
    int firstUnplace = world.Calls.FindIndex(c => c.StartsWith("unplace"));
    int lastDestroyOfGroup = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) <= 1500);
    int far = world.Calls.IndexOf("list 2000,0");
    C(progress.Reset == 1501 && firstUnplace > lastDestroyOfGroup && world.Calls.Count(c => c.StartsWith("unplace")) == 1501, "no zone of the group is finished before the last has been emptied, and all of them are in the end");
    C(world.Calls.FindLastIndex(c => c.StartsWith("unplace") && c != "unplace 2000,0") < far, "and they are all finished before the zone beyond the group is looked at: not at the end of the run");
    C(work.Looks < 30 * 1500, $"finishing a group of 1,500 zones looked at zones {work.Looks:N0} times: a few for each, not 1,500 for each ({1500 * 1500:N0})");

    // The same when the first zone of the group finds a piece: all the others are left alone with it.
    world = new FakeWorld();
    next = 1;
    world.Fill(zones, 1, ref next);
    world.Add(Z(0, 0), O(5000, Kind.Piece));
    world.Generated.UnionWith(zones);
    (work, progress, _) = Go(world, ZoneReset.MakePlan(world.Generated, [], [Z(0, 0)], locations), Roomy());
    C(progress.Skipped == 1500 && progress.SkippedForLocations == 1499 && progress.Reset == 0 && world.Destroyed.Count == 0, "a piece in the first zone: the whole group of 1,500 is left alone with it");
    C(work.Looks < 30 * 1500, $"and that is looked at {work.Looks:N0} times, not the square of the group");

    // What a zone waits for: its 3 x 3, the diagonals too, and nothing farther.
    var a = Z(0, 0);
    world = new FakeWorld();
    next = 1;
    var block = new[] { a, Z(1, 1), Z(2, 0), Z(30, 0) };
    world.Fill(block, 1, ref next);
    world.Generated.UnionWith(block);
    Go(world, ZoneReset.MakePlan(world.Generated, [], [a]), Roomy());
    int diagonal = world.Calls.FindIndex(c => c == "destroy 2");
    int apart = world.Calls.IndexOf("list 2,0");
    C(world.Calls.IndexOf("unplace 0,0") > diagonal && world.Calls.IndexOf("unplace 0,0") < apart, "a zone waits for its diagonal neighbour's turn and for no zone two away: it is finished before (2,0) is looked at");
    C(world.Calls.IndexOf("unplace 1,1") > world.Calls.IndexOf("destroy 3") && world.Calls.IndexOf("unplace 1,1") < world.Calls.IndexOf("list 30,0"), "(and the neighbour that came last, a zone before the far one)");

    // Two groups at once, each finished when its own last zone has had its turn.
    var (rowA, locationsA) = FinalChain(3, y: 0);
    var (rowB, locationsB) = FinalChain(3, y: 8);
    world = new FakeWorld();
    next = 1;
    world.Fill(rowA.Concat(rowB), 1, ref next);
    world.Generated.UnionWith(rowA.Concat(rowB));
    // The rows are done side by side: (0,0) and (0,8) first, and so on, since the players are in both.
    var twoPlan = ZoneReset.MakePlan(world.Generated, [], [Z(0, 0), Z(0, 8)], locationsA.Concat(locationsB));
    C(twoPlan.Groups.Count == 2, "(two groups)");
    Go(world, twoPlan, Roomy());
    var lastA = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is >= 1 and <= 3);
    var lastB = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) is >= 4 and <= 6);
    int unplaceA = world.Calls.FindIndex(c => c == "unplace 0,0" || c == "unplace 1,0" || c == "unplace 2,0");
    int unplaceB = world.Calls.FindIndex(c => c == "unplace 0,8" || c == "unplace 1,8" || c == "unplace 2,8");
    C(unplaceA > lastA && unplaceB > lastB && unplaceA < world.Calls.IndexOf("list 2,8") && world.Calls.Count(c => c.StartsWith("unplace")) == 6, "two groups: each is finished after its own last zone, and none is held back by the other");
  }

  // ------------------------------------------------------------------------------------------------ the reviewed work, as a reference model

  // The work as it was at the review (3aae2aa), written here as it was, for the random worlds below: the new work must do exactly what it did,
  // call for call, wherever there are no strays (which it did not know).
  private sealed class OldWork(ZoneReset.IZoneWorld world, Plan plan, Budget budget, Progress progress, ZoneReset.Memory memory)
  {
    private readonly HashSet<Vector2s> planned = new(plan.Reset, ZoneReset.Comparer);
    private readonly HashSet<Vector2s> done = new(ZoneReset.Comparer);
    private readonly HashSet<Vector2s> late = new(ZoneReset.Comparer);
    private readonly HashSet<Vector2s> finished = new(ZoneReset.Comparer);
    private HashSet<Vector2s> live = new(ZoneReset.Comparer);
    private bool inFlight;
    private Vector2s flightZone;
    private List<ZoneReset.Obj> doomed = [];
    private int at;

    public bool InFlight => inFlight;

    private static void AddSquare(HashSet<Vector2s> into, Vector2s zone, int radius)
    {
      for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
          into.Add(new Vector2s(zone.x + dx, zone.y + dy));
    }

    private void StartFrame()
    {
      budget.NewFrame();
      world.NewFrame();
      live = ZoneReset.Dilate(world.LiveProtectors());
    }

    public IEnumerator Steps()
    {
      progress.Total = plan.Reset.Count;
      memory.Pending.IntersectWith(plan.Carried);
      StartFrame();
      foreach (var zone in plan.Reset)
      {
        if (done.Contains(zone))
          continue;
        if (budget.Spent)
        {
          yield return null;
          StartFrame();
        }
        if (!world.Alive)
        {
          progress.Aborted = true;
          yield break;
        }
        if (!Begin(zone))
          continue;
        while (inFlight)
        {
          Slice(Math.Min(ZoneReset.Work.SliceSize, Math.Max(1, budget.Room)));
          if (inFlight && budget.Spent)
          {
            yield return null;
            StartFrame();
            if (!world.Alive)
            {
              progress.Aborted = true;
              yield break;
            }
          }
        }
      }
      foreach (var zone in memory.Pending.OrderBy(z => z.x).ThenBy(z => z.y).ToList())
        if (done.Contains(zone))
          Finish(zone);
      foreach (var border in Owed())
      {
        if (budget.Spent)
        {
          yield return null;
          StartFrame();
        }
        if (!world.Alive)
        {
          progress.Aborted = true;
          yield break;
        }
        Mend(border);
        budget.Use(ZoneReset.BorderCost);
      }
      memory.Cleared.Clear();
    }

    private bool Begin(Vector2s zone)
    {
      bool carried = plan.Carried.Contains(zone);
      if (!carried && (live.Contains(zone) || late.Contains(zone)))
      {
        LeaveAlone(zone, holdsProtector: false);
        return false;
      }
      var objects = world.ObjectsIn(zone);
      budget.Use(1);
      if (!carried && objects.Any(o => ZoneReset.Rules.Protects(o.Kind)))
      {
        LeaveAlone(zone, holdsProtector: true);
        return false;
      }
      doomed = new List<ZoneReset.Obj>(objects.Count);
      foreach (var o in objects)
      {
        if (ZoneReset.Rules.Survives(o.Kind))
          progress.Kept++;
        else
          doomed.Add(o);
      }
      inFlight = true;
      flightZone = zone;
      at = 0;
      if (doomed.Count == 0)
        Complete();
      return true;
    }

    private void Slice(int limit)
    {
      int n = Math.Min(limit, doomed.Count - at);
      for (int i = 0; i < n; i++)
        if (world.Destroy(doomed[at + i], out var spawned))
        {
          progress.Destroyed++;
          FollowSpawn(spawned);
        }
      at += Math.Max(0, n);
      budget.Use(Math.Max(0, n));
      if (at >= doomed.Count)
        Complete();
    }

    private void FollowSpawn(ZDOID spawned)
    {
      if (spawned.IsNone() || world.Find(spawned) is not { } creature)
        return;
      if (ZoneReset.Rules.Survives(creature.Kind) || ZoneReset.Rules.Protects(creature.Kind) || plan.Covered.Contains(creature.Zone) || live.Contains(creature.Zone) || late.Contains(creature.Zone))
      {
        progress.SpawnedKept++;
        return;
      }
      if (world.Destroy(creature, out _))
      {
        progress.Destroyed++;
        progress.Spawned++;
      }
    }

    private void Complete()
    {
      inFlight = false;
      doomed = [];
      done.Add(flightZone);
      memory.Cleared.Add(flightZone);
      memory.Pending.Add(flightZone);
      Settle(flightZone);
    }

    private void LeaveAlone(Vector2s zone, bool holdsProtector)
    {
      done.Add(zone);
      progress.Skipped++;
      progress.Done++;
      if (holdsProtector)
        AddSquare(late, zone, ZoneReset.Rules.ProtectRadius);
      var settled = new List<Vector2s> { zone };
      foreach (var member in plan.GroupMembers(zone))
        if (planned.Contains(member) && !plan.Carried.Contains(member) && done.Add(member))
        {
          progress.Skipped++;
          progress.SkippedForLocations++;
          progress.Done++;
          settled.Add(member);
        }
      foreach (var left in settled)
        Settle(left);
    }

    private void Settle(Vector2s turned)
    {
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
          FinishIfReady(new Vector2s(turned.x + dx, turned.y + dy));
      foreach (var member in plan.GroupMembers(turned))
        FinishIfReady(member);
    }

    private void FinishIfReady(Vector2s zone)
    {
      if (memory.Pending.Contains(zone) && done.Contains(zone) && Ready(zone))
        Finish(zone);
    }

    private bool Ready(Vector2s zone)
    {
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
          var beside = new Vector2s(zone.x + dx, zone.y + dy);
          if (planned.Contains(beside) && !done.Contains(beside))
            return false;
        }
      foreach (var member in plan.GroupMembers(zone))
        if (planned.Contains(member) && !done.Contains(member))
          return false;
      return true;
    }

    private void Finish(Vector2s zone)
    {
      world.Unplace(zone);
      world.Ungenerate(zone);
      world.DestroyRoot(zone);
      memory.Pending.Remove(zone);
      finished.Add(zone);
      progress.Reset++;
      progress.Done++;
    }

    private List<KeyValuePair<Vector2s, Edges>> Owed()
    {
      var gone = new HashSet<Vector2s>(memory.Pending, ZoneReset.Comparer);
      gone.UnionWith(finished);
      return ZoneReset.BordersOf(memory.Cleared, plan.GeneratedZones, gone).OrderBy(b => b.Key.x).ThenBy(b => b.Key.y).ToList();
    }

    private void Mend(KeyValuePair<Vector2s, Edges> border)
    {
      int vertices = world.AdjustBorder(border.Key, border.Value);
      if (vertices > 0)
      {
        progress.BorderZones++;
        progress.BorderVertices += vertices;
      }
    }

    public void FinishInFlight()
    {
      while (inFlight)
        Slice(int.MaxValue);
    }

    public void BeforeSave()
    {
      if (!world.Alive)
        return;
      FinishInFlight();
      world.Flush();
      foreach (var border in Owed())
        Mend(border);
      memory.Cleared.Clear();
    }
  }

  // One random world: zones in a square, some generated, objects in them, locations of every size with their homes in generated zones.
  // Pieces appear in some zones when they are listed, and players walk in at some frames.
  private sealed class FinalScene
  {
    public readonly List<Vector2s> All = [];
    public readonly List<Vector2s> Generated = [];
    public readonly List<ZoneReset.PlacedLocation> Locations = [];
    public readonly List<Vector2s> Focus = [];
    public readonly Dictionary<Vector2s, int> Counts = [];
    public readonly HashSet<Vector2s> PieceAtListing = new(ZoneReset.Comparer);
    public readonly Dictionary<int, Vector2s> WalkInAtFrame = [];
    public int ObjectsPerFrame;

    public static FinalScene Make(int seed, bool partlyGenerated, bool quiet = false)
    {
      var rng = new System.Random(seed);
      var scene = new FinalScene();
      int side = 5 + rng.Next(8);
      for (int x = 0; x < side; x++)
        for (int y = 0; y < side; y++)
          scene.All.Add(Z(x, y));
      foreach (var zone in scene.All)
      {
        if (!partlyGenerated || rng.NextDouble() < 0.7)
          scene.Generated.Add(zone);
        scene.Counts[zone] = rng.Next(0, 4);
      }
      var radii = new[] { 0f, 8f, 20f, 26f, 40f, 70f, 110f };
      int locations = rng.Next(0, side * 2);
      for (int i = 0; i < locations; i++)
      {
        var home = scene.Generated[rng.Next(scene.Generated.Count)];
        scene.Locations.Add(LocIn(home.x, home.y, rng.Next(-30, 31), rng.Next(-30, 31), radii[rng.Next(radii.Length)]));
      }
      int foci = 1 + rng.Next(2);
      for (int i = 0; i < foci; i++)
        scene.Focus.Add(scene.All[rng.Next(scene.All.Count)]);
      int pieces = rng.Next(0, 4);
      int walkers = rng.Next(0, 3);
      for (int i = 0; i < pieces; i++)
      {
        var zone = scene.All[rng.Next(scene.All.Count)];
        if (!quiet)
          scene.PieceAtListing.Add(zone);
      }
      for (int i = 0; i < walkers; i++)
      {
        int frame = 2 + rng.Next(8);
        var zone = scene.All[rng.Next(scene.All.Count)];
        if (!quiet)
          scene.WalkInAtFrame[frame] = zone;
      }
      scene.ObjectsPerFrame = new[] { 2, 3, 7, 25, 1000 }[rng.Next(5)];
      return scene;
    }

    public FakeWorld Build()
    {
      var world = new FakeWorld();
      uint next = 1;
      // (Zones nobody generated may hold what a location left there too.)
      foreach (var zone in All)
        for (int i = 0; i < Counts[zone]; i++)
          world.Add(zone, O(next++));
      world.Generated.UnionWith(Generated);
      var listed = new HashSet<Vector2s>(ZoneReset.Comparer);
      world.OnList = zone =>
      {
        if (PieceAtListing.Contains(zone) && listed.Add(zone))
          world.Add(zone, O(900000 + (uint)(zone.x * 100 + zone.y), Kind.Piece));
      };
      world.OnNewFrame = () =>
      {
        if (WalkInAtFrame.TryGetValue(world.Frames, out var walker))
          world.Live.Add(new ZoneReset.Protector(walker, 1));
      };
      return world;
    }
  }

  private static (FakeWorld World, Plan Plan, Progress Progress, int Frames, ZoneReset.Memory Memory) FinalRun(FinalScene scene, bool reference)
  {
    var world = scene.Build();
    var plan = ZoneReset.MakePlan(world.Generated, [], scene.Focus, scene.Locations);
    var progress = new Progress();
    var memory = new ZoneReset.Memory();
    var budget = new Budget(scene.ObjectsPerFrame, 1_000_000, () => 0);
    IEnumerator steps = reference ? new OldWork(world, plan, budget, progress, memory).Steps() : new ZoneReset.Work(world, plan, budget, progress, memory).Steps();
    int frames = 1;
    while (steps.MoveNext())
    {
      frames++;
      world.Frame = frames - 1;
    }
    return (world, plan, progress, frames, memory);
  }

  private static void FinalReferenceTests()
  {
    Section("final: the work against the reviewed work, call for call, on random worlds (no strays)");
    int worlds = 0, different = 0, skippedZones = 0, groups = 0, finishedZones = 0;
    var firstDifference = "";
    for (int seed = 1; seed <= 400; seed++)
    {
      var scene = FinalScene.Make(seed, partlyGenerated: false);
      var old = FinalRun(scene, reference: true);
      var now = FinalRun(scene, reference: false);
      if (now.Plan.Strays.Count > 0)
        continue;
      worlds++;
      groups += now.Plan.Groups.Count;
      skippedZones += now.Progress.Skipped;
      finishedZones += now.Progress.Reset;
      bool same = old.World.Calls.SequenceEqual(now.World.Calls) && old.Frames == now.Frames && old.World.Generated.SetEquals(now.World.Generated) && old.Memory.Pending.SetEquals(now.Memory.Pending)
        && old.Progress.Reset == now.Progress.Reset && old.Progress.Skipped == now.Progress.Skipped && old.Progress.SkippedForLocations == now.Progress.SkippedForLocations
        && old.Progress.Done == now.Progress.Done && old.Progress.Destroyed == now.Progress.Destroyed && old.Progress.Kept == now.Progress.Kept;
      if (!same && ++different == 1)
        firstDifference = $"seed {seed}: {old.World.Calls.Count} calls then, {now.World.Calls.Count} now; first apart at {old.World.Calls.Zip(now.World.Calls, (a, b) => a == b).TakeWhile(x => x).Count()}";
    }
    C(different == 0, $"the work does what the reviewed work did on {worlds} random worlds: the same calls in the same order, the same counts ({firstDifference})");
    C(worlds >= 80 && groups > 100 && skippedZones > 50 && finishedZones > 1000, $"(those worlds had {groups:N0} location groups, {skippedZones:N0} zones left alone after all and {finishedZones:N0} finished)");

    // The same kind of worlds with zones nobody generated, so with strays: the reviewed work did not know them, so they are held to what
    // they must do. With players and pieces turning up, no zone is left waiting, no stray is unplaced, ungenerated or rooted, and every zone
    // of the plan is done.
    int withStrays = 0, bad = 0, strayZones = 0;
    var detail = "";
    for (int seed = 1; seed <= 400; seed++)
    {
      var scene = FinalScene.Make(seed, partlyGenerated: true);
      var run = FinalRun(scene, reference: false);
      if (run.Plan.Strays.Count == 0)
        continue;
      withStrays++;
      strayZones += run.Plan.Strays.Count;
      var strayCalls = run.Plan.Strays.SelectMany(s => new[] { $"unplace {s.x},{s.y}", $"ungenerate {s.x},{s.y}", $"root {s.x},{s.y}" }).ToHashSet();
      bool ok = run.Memory.Pending.Count == 0 && !run.World.Calls.Any(strayCalls.Contains) && run.Progress.Done == run.Progress.Total && run.Progress.Total == run.Plan.Reset.Count;
      if (!ok && ++bad == 1)
        detail = $"seed {seed}: pending {run.Memory.Pending.Count}, done {run.Progress.Done} of {run.Progress.Total}";
    }
    C(withStrays > 100 && strayZones > 150, $"({withStrays} random worlds had {strayZones} strays)");
    C(bad == 0, $"each of them ends with no zone waiting, no stray unplaced, ungenerated or rooted, and every zone of the plan done ({detail})");

    // And with nothing turning up, each zone is finished at once when what it waits for has had its turn, and not before: the zones of its
    // 3 x 3 that are reset, and the zones of its group, strays included.
    int checkedZones = 0, early = 0, late = 0;
    var example = "";
    for (int seed = 1; seed <= 300; seed++)
    {
      var scene = FinalScene.Make(seed, partlyGenerated: true, quiet: true);
      var run = FinalRun(scene, reference: false);
      var calls = run.World.Calls;
      int TurnEnd(Vector2s zone)
      {
        int last = calls.IndexOf($"list {zone.x},{zone.y}");
        foreach (var o in run.World.Objects.TryGetValue(zone, out var list) ? list : [])
          last = Math.Max(last, calls.IndexOf($"destroy {o.Id.ID}"));
        return last;
      }
      var turns = run.Plan.Turns.ToHashSet(ZoneReset.Comparer);
      var reset = run.Plan.Reset.ToHashSet(ZoneReset.Comparer);
      foreach (var zone in run.Plan.Reset)
      {
        int at = calls.IndexOf($"unplace {zone.x},{zone.y}");
        var needs = new HashSet<Vector2s>(ZoneReset.Comparer) { zone };
        for (int dx = -1; dx <= 1; dx++)
          for (int dy = -1; dy <= 1; dy++)
            if (reset.Contains(Z(zone.x + dx, zone.y + dy)))
              needs.Add(Z(zone.x + dx, zone.y + dy));
        foreach (var member in run.Plan.GroupMembers(zone))
          if (turns.Contains(member))
            needs.Add(member);
        int ready = needs.Max(TurnEnd);
        checkedZones++;
        if (at <= ready)
        {
          if (++early == 1)
            example = $"seed {seed}: zone {zone.x},{zone.y} finished at {at} before its last wait ended at {ready}";
        }
        else if (calls.Skip(ready + 1).Take(at - ready - 1).Any(c => !(c.StartsWith("unplace") || c.StartsWith("ungenerate") || c.StartsWith("root"))))
        {
          if (++late == 1)
            example = $"seed {seed}: zone {zone.x},{zone.y} finished at {at}, after other work, though it was ready at {ready}";
        }
      }
    }
    C(checkedZones > 3000 && early == 0 && late == 0, $"on {checkedZones:N0} zones of random worlds with strays: none finished before what it waits for was done, none later than the moment it was ({example})");
  }

  // ------------------------------------------------------------------------------------------------ F1: a save takes the turns of the group

  // Whether the saved state holds a location group half done: some of its zones emptied (not generated, no object left) and some untouched
  // (generated, with objects). Zones with no objects are no evidence either way.
  private static bool FinalHalfGroup(FakeWorld world, ZoneReset.Memory memory, IEnumerable<Vector2s> group)
  {
    var saved = Saved(world, memory);
    bool emptied = false, untouched = false;
    foreach (var zone in group)
    {
      var objects = world.Objects.TryGetValue(zone, out var list) ? list : [];
      if (objects.Count == 0)
        continue;
      int present = objects.Count(o => saved.Objects.Contains(o.Id));
      if (present == 0)
        emptied = true;
      else if (present == objects.Count)
        untouched = true;
    }
    return emptied && untouched;
  }

  private static void FinalDrainTests()
  {
    Section("final: a save takes the turns of the zones of the group of a zone that is emptied and waiting");
    var home = Z(0, 0);
    var beside = Z(1, 0);
    var far = Z(9, 0);
    var reach = LocIn(0, 0, 20, 0, 20);

    // The first frame empties the home (2 objects and the listing fill the budget of 3); the zone beside it, the same location's, waits.
    FakeWorld Make(out ZoneReset.Memory memory, out ZoneReset.Work work, out IEnumerator steps, out Plan plan)
    {
      var world = new FakeWorld();
      world.Add(home, O(1), O(2)).Add(beside, O(3), O(4)).Add(far, O(5), O(6));
      world.Generated.UnionWith([home, beside, far]);
      memory = new ZoneReset.Memory();
      plan = ZoneReset.MakePlan(world.Generated, [], [home], [reach]);
      work = new ZoneReset.Work(world, plan, new Budget(3, 1_000_000, () => 0), new Progress(), memory);
      steps = work.Steps();
      steps.MoveNext();
      return world;
    }

    var w = Make(out var memory, out var work, out var steps, out var plan);
    C(Show(plan.Turns) == "0,0 1,0 9,0" && memory.Pending.SetEquals([home]) && w.Destroyed.Count == 2 && !w.Calls.Contains("list 1,0"), "(the first frame emptied the home, which waits for the zone beside it, whose turn has not come)");
    w.Flush();
    C(FinalHalfGroup(w, memory, plan.GroupMembers(home)), "(control: saved a frame later with nothing done about it, the group would be half done: the home emptied and not generated, the zone beside it generated with its old parts)");
    work.BeforeSave();
    C(!FinalHalfGroup(w, memory, plan.GroupMembers(home)) && w.Destroyed.Contains(Id(3)) && w.Destroyed.Contains(Id(4)), "a save takes the turn of the zone beside it: the group is wholly emptied in what is saved");
    C(!w.Destroyed.Contains(Id(5)) && !w.Destroyed.Contains(Id(6)) && w.Generated.Contains(far), "and no other zone is touched: the zone of no group that has a zone waiting is left as it was");
    C(w.Flushed.SetEquals(w.Destroyed) && w.Flushed.Count == 4, "(the queue was sent after: the objects of both zones are out of the world's lists)");
    C(w.Calls.IndexOf("unplace 0,0") > w.Calls.IndexOf("destroy 4") && w.Calls.IndexOf("ungenerate 1,0") >= 0 && memory.Pending.Count == 0, "the home is finished when the group is: both zones are, the live game has them as the save has them");
    C(w.Calls.Count(c => c == "list 0,0") == 1 && w.Calls.Count(c => c == "list 1,0") == 1, "(and no zone had its turn twice)");
    while (steps.MoveNext())
    {
    }
    C(w.Destroyed.Count == 6 && w.Generated.Count == 0 && memory.Pending.Count == 0, "the run goes on to its end from there, each object destroyed once");

    // A group of three zones with two that have not had their turn: both take it.
    var (chainZones, chainLocations) = FinalChain(3);
    w = new FakeWorld();
    uint chainNext = 1;
    w.Fill(chainZones, 2, ref chainNext);
    w.Generated.UnionWith(chainZones);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [Z(0, 0)], chainLocations);
    work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    C(plan.GroupMembers(Z(0, 0)).Count == 3 && memory.Pending.SetEquals([Z(0, 0)]) && w.Destroyed.Count == 2, "(the first of three zones of a group is emptied)");
    work.BeforeSave();
    C(w.Destroyed.Count == 6 && memory.Pending.Count == 0 && w.Generated.Count == 0 && w.Calls.Count(c => c.StartsWith("list")) == 3, "a save takes the turns of both zones that had not had theirs, once each");

    // The zone being emptied when the save comes: it is finished first, and then its group takes its turns.
    w = new FakeWorld();
    uint next = 1;
    w.Fill([home], 100, ref next);
    w.Fill([beside], 5, ref next);
    w.Generated.UnionWith([home, beside]);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [home], [reach]);
    work = new ZoneReset.Work(w, plan, new Budget(30, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    C(work.InFlight && w.Destroyed.Count < 100, "(the home is half emptied)");
    work.BeforeSave();
    C(!work.InFlight && w.Destroyed.Count == 105 && !FinalHalfGroup(w, memory, plan.GroupMembers(home)) && w.Flushed.Count == 105, "a save finishes the zone being emptied, and then the group takes its turns: all of it is gone");

    // A zone that something protects at that moment is left alone: the case that cannot be undone, which the file shows as it is.
    w = Make(out memory, out work, out steps, out plan);
    w.Add(beside, O(70, Kind.Piece));
    work.BeforeSave();
    C(!w.Destroyed.Contains(Id(3)) && !w.Destroyed.Contains(Id(4)) && !w.Destroyed.Contains(Id(70)) && w.Generated.Contains(beside),
      "a piece in the zone beside by the time of the save: it is left alone, with its old parts and the piece");
    C(memory.Pending.Count == 0 && w.Calls.Contains("unplace 0,0"), "and the home, which waited for its turn, is finished, as it is in a run when the zone it waits for is left alone");

    // Somebody who walked in since the frame began counts: the save looks again.
    w = Make(out memory, out work, out steps, out plan);
    w.Live.Add(new ZoneReset.Protector(beside, 0));
    work.BeforeSave();
    C(!w.Destroyed.Contains(Id(3)) && w.Generated.Contains(beside), "a player who has come into the zone beside since the frame began keeps it: the save does not use what the frame saw");

    // A stray of the group takes its turn too, and a stray that is generated by then is a zone to reset like the others.
    w = new FakeWorld();
    w.Add(home, O(1), O(2)).Add(beside, O(3), O(4));
    w.Generated.Add(home);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [home], [reach]);
    work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    C(Show(plan.Strays) == "1,0" && memory.Pending.SetEquals([home]), "(the zone beside is a stray, and the home waits for it)");
    work.BeforeSave();
    C(w.Destroyed.Contains(Id(3)) && w.Destroyed.Contains(Id(4)) && memory.Pending.Count == 0 && w.Calls.Contains("unplace 0,0") && !w.Calls.Contains("unplace 1,0"), "a save has the stray cleared, and the home finished: nothing of the location is left beside it");
    w = new FakeWorld();
    w.Add(home, O(1), O(2)).Add(beside, O(3), O(4));
    w.Generated.Add(home);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [home], [reach]);
    work = new ZoneReset.Work(w, plan, new Budget(3, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    w.Generated.Add(beside);
    work.BeforeSave();
    C(w.Destroyed.Contains(Id(3)) && w.Destroyed.Contains(Id(4)) && memory.Pending.Count == 0 && w.Calls.Contains("unplace 0,0") && w.Calls.Contains("unplace 1,0") && !w.Generated.Contains(beside),
      "a stray that somebody has come near by the save is a zone to reset like the others: its old parts go with the home's, and both are finished");

    // A group with nothing waiting is not touched by a save; nor is a group whose zones have all had their turn.
    var farNext = Z(10, 0);
    w = new FakeWorld();
    w.Add(home, O(1), O(2)).Add(beside, O(3), O(4)).Add(far, O(5)).Add(farNext, O(7));
    w.Generated.UnionWith([home, beside, far, farNext]);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(w.Generated, [], [far], [reach]);
    work = new ZoneReset.Work(w, plan, new Budget(2, 1_000_000, () => 0), new Progress(), memory);
    steps = work.Steps();
    steps.MoveNext();
    C(memory.Pending.SetEquals([far]) && !w.Calls.Contains("list 10,0"), "(the run began with a zone of no group, which waits for the zone beside it)");
    work.BeforeSave();
    C(!w.Destroyed.Contains(Id(1)) && !w.Destroyed.Contains(Id(3)) && !w.Destroyed.Contains(Id(7)) && w.Generated.Contains(home) && memory.Pending.SetEquals([far]),
      "a save with only a zone of no group waiting takes no turn: the groups that have not begun are untouched, and so is the zone beside the one waiting");

    // The save of a world that is gone does nothing at all.
    w = Make(out memory, out work, out steps, out plan);
    w.IsAlive = () => false;
    int calls = w.Calls.Count;
    work.BeforeSave();
    C(w.Calls.Count == calls, "a save in a world that closed takes no turn");
  }

  // ------------------------------------------------------------------------------------------------ F3: an error finishes the zones left waiting

  private static void FinalErrorTests()
  {
    Section("final: an error finishes the zones the run had emptied and left waiting");
    var home = Z(0, 0);
    var beside = Z(1, 0);

    // The work on its own: what is waiting is finished, in a fixed order; what failed is told, and the rest goes on.
    var world = new FakeWorld();
    uint next = 1;
    world.Fill([home, beside, Z(5, 0), Z(9, 9)], 2, ref next);
    world.Generated.UnionWith([home, beside, Z(5, 0), Z(9, 9)]);
    var memory = new ZoneReset.Memory();
    var plan = ZoneReset.MakePlan(world.Generated, [], [home], [LocIn(0, 0, 20, 0, 20)]);
    var progress = new Progress();
    var work = new ZoneReset.Work(world, plan, new Budget(3, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    steps.MoveNext();
    work.FinishInFlight();
    C(memory.Pending.SetEquals([home]), "(the home is emptied, and waits for the zone beside it, whose turn will not come)");
    int finished = work.FinishPending();
    C(finished == 1 && memory.Pending.Count == 0 && !world.Generated.Contains(home) && world.Generated.Contains(beside) && world.Calls.Contains("unplace 0,0") && world.Calls.Contains("root 0,0"),
      "the zone waiting is finished: its location unplaced, not generated, its loaded copy gone; the zone it waited for is as it was");
    C(!world.Destroyed.Contains(Id(3)) && progress.Reset == 1, "(and nothing beside it was touched, though its location reaches there)");
    C(work.FinishPending() == 0, "nothing is left to finish the second time");

    // Several, and one that cannot be finished.
    world = new FakeWorld();
    next = 1;
    var row = Enumerable.Range(0, 4).Select(x => Z(x * 4, 0)).ToList();
    world.Fill(row, 2, ref next);
    world.Generated.UnionWith(row);
    memory = new ZoneReset.Memory();
    foreach (var zone in row)
      memory.Pending.Add(zone);
    plan = ZoneReset.MakePlan(world.Generated, [], [Z(0, 0)], null, row);
    work = new ZoneReset.Work(world, plan, new Budget(1_000_000, 1_000_000, () => 0), new Progress(), memory);
    world.OnUngenerate = zone =>
    {
      if (zone.Equals(Z(4, 0)))
        throw new InvalidOperationException("this one cannot");
    };
    var failed = new List<(Vector2s, string)>();
    finished = work.FinishPending((zone, e) => failed.Add((zone, e.Message)));
    C(finished == 3 && failed.Count == 1 && failed[0].Item1.Equals(Z(4, 0)) && failed[0].Item2 == "this one cannot" && memory.Pending.SetEquals([Z(4, 0)]),
      "a zone that cannot be finished is reported and left waiting; the others are finished");
    C(world.Calls.Where(c => c.StartsWith("unplace")).SequenceEqual(["unplace 0,0", "unplace 4,0", "unplace 8,0", "unplace 12,0"]), "in a fixed order, x then y");
    world.OnUngenerate = null;
    world.IsAlive = () => false;
    int callsBefore = world.Calls.Count;
    int asked = 0;
    C(work.FinishPending((_, _) => asked++) == 0 && asked == 0 && world.Calls.Count == callsBefore && memory.Pending.SetEquals([Z(4, 0)]),
      "in a world that is gone nothing is finished, though a zone waits");
    world.IsAlive = () => true;
    C(work.FinishPending() == 1 && memory.Pending.Count == 0, "(and once the world is back, it is)");

    // Through ZoneRegen.Run, on the game's own classes: an error in the work, with a zone emptied and waiting.
    ZDOExtraData.Reset();
    var w = new World();
    var oldDelta = ZoneRegen.DeltaTime;
    try
    {
      // A player from another machine far away keeps the frames small (100 objects) and makes each frame ask the clock.
      var away = w.Add(Z(-40, -40), "Player", user: 700L);
      away.SetOwner(700L);
      w.Peer(away, 700L);
      for (int i = 0; i < 150; i++)
        w.Add(home, "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
      for (int i = 0; i < 5; i++)
        w.Add(beside, "Pine", dx: i - 2f);
      w.Generated.Add(home);
      w.Generated.Add(beside);
      w.PlaceLocation(home, true, 20f, dx: 20f);
      w.Load(home);
      ZoneRegen.DeltaTime = () => 1f / 60f;
      var lines = new List<string>();
      var job = new ZoneRegen.Job(lines.Add);
      SetStatic(typeof(ZoneRegen), "job", job);
      var run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", Any)!.Invoke(null, [job])!;
      bool armed = false;
      var log = LogHandler.During(() =>
      {
        int guard = 0;
        while (guard++ < 1000 && run.MoveNext())
        {
          w.Flush();
          if (!armed && job.Work is { InFlight: true })
          {
            armed = true;
            ZoneRegen.DeltaTime = () => throw new InvalidOperationException("the clock failed");
          }
        }
      });
      w.Flush();
      C(armed && lines.Contains("Zone regeneration stopped by an error, see the log."), "(the run is in the middle of the home when the error comes, and says so on the console: " + string.Join(" | ", lines) + ")");
      C(!w.Generated.Contains(home) && w.LiveIn(home).Count == 0 && !w.Zones.m_locationInstances[home].m_placed && !w.Loaded.Contains(home),
        "the zone it had emptied is finished: the rest of it was emptied first, and it is no longer generated, its location is to be placed again and its loaded copy is gone");
      C(w.Generated.Contains(beside) && w.LiveIn(beside).Count == 5, "the zone it had not reached is as it was");
      C(log.Any(l => l.Contains("Zone regeneration stopped:")) && log.Any(l => l.Contains(ZoneReset.PendingLine(1))), "the log says what was finished and how to reset the rest: " + string.Join(" | ", log.Where(SaysPendingFinished)));
      C(GetStatic<ZoneRegen.Job?>(typeof(ZoneRegen), "job") == null, "(the run is over)");

      // An error before there is any work: nothing waits, nothing is claimed, nothing throws.
      ZDOExtraData.Reset();
      w = new World();
      var away2 = w.Add(Z(-40, -40), "Player", user: 700L);
      away2.SetOwner(700L);
      w.Peer(away2, 700L);
      w.Add(home, "Pine");
      w.Generated.Add(home);
      ZoneRegen.DeltaTime = () => throw new InvalidOperationException("the clock failed at once");
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
      C(lines.Contains("Zone regeneration stopped by an error, see the log.") && w.Generated.Contains(home) && !log.Any(SaysPendingFinished) && !log.Any(l => l.Contains("could not be finished")),
        "an error before the work began: the console says so, the world is as it was, and the log claims no zone finished");
    }
    finally
    {
      ZoneRegen.DeltaTime = oldDelta;
      SetStatic(typeof(ZoneRegen), "job", null);
      World.Close();
    }
    var text = ZoneReset.PendingLine(1) + " | " + ZoneReset.PendingLine(3);
    C(ZoneReset.PendingLine(1) == "Zone regeneration: 1 zone left waiting was finished, so it generates again with the new settings when somebody comes near. The zones it had not reached are as they were: 'bc regen' resets them."
      && ZoneReset.PendingLine(3) == "Zone regeneration: 3 zones left waiting were finished, so they generate again with the new settings when somebody comes near. The zones it had not reached are as they were: 'bc regen' resets them.",
      "the line in the singular and the plural, in words that fit the zones of an earlier run as well as the run's own: " + text);
    C(!text.Contains("it had emptied") && SaysPendingFinished(ZoneReset.PendingLine(1)) && SaysPendingFinished(ZoneReset.PendingLine(3)), "(the checks that an error finished nothing look for these words, and the line has them)");
    string[] banned = ["Upgrade World", "Expand World", "replace", "obsolete", "no longer need"];
    C(banned.All(x => !text.Contains(x, StringComparison.OrdinalIgnoreCase)) && !text.Contains("1.0") && !text.Contains("0.9"), "and it names no other mod and no version of the game");
  }

  // ------------------------------------------------------------------------------------------------ F6: what the debug reset drops

  // A stand-in that is none of the game's: the walk over Heightmap.Instances meets only heightmaps, but the list holds interfaces.
  private sealed class FinalUpdater : IMonoUpdater
  {
  }

  private static readonly FieldInfo FinalBuildData = typeof(Heightmap).GetField("m_buildData", Any)!;

  private static Heightmap FinalHeightmap(bool distant, bool built)
  {
    var heightmap = Make<Heightmap>();
    SetField(heightmap, "m_isDistantLod", distant);
    FinalBuildData.SetValue(heightmap, built ? new HeightmapBuilder.HMBuildData(Vector3.zero, 80, 10f, distant, null) : null);
    return heightmap;
  }

  private static object? FinalBuild(Heightmap heightmap) => FinalBuildData.GetValue(heightmap);

  private static HeightmapBuilder FinalBuilder(int ready, int queued, object? padlock)
  {
    var builder = Make<HeightmapBuilder>();
    SetField(builder, "m_ready", Enumerable.Range(0, ready).Select(_ => new HeightmapBuilder.HMBuildData(Vector3.zero, 1, 1f, false, null)).ToList());
    SetField(builder, "m_toBuild", Enumerable.Range(0, queued).Select(_ => new HeightmapBuilder.HMBuildData(Vector3.zero, 1, 1f, false, null)).ToList());
    SetField(builder, "m_lock", padlock);
    return builder;
  }

  private static List<HeightmapBuilder.HMBuildData> FinalReady(HeightmapBuilder builder) => GetField<List<HeightmapBuilder.HMBuildData>>(builder, "m_ready");
  private static List<HeightmapBuilder.HMBuildData> FinalQueued(HeightmapBuilder builder) => GetField<List<HeightmapBuilder.HMBuildData>>(builder, "m_toBuild");

  private static void FinalPokeTests()
  {
    Section("final: the debug reset pokes the distant terrain and drops the builds and the grass made from the old settings");

    // The distant terrain: the heightmaps TerrainLod has built and no others.
    var built = FinalHeightmap(distant: true, built: true);
    var fresh = FinalHeightmap(distant: true, built: false);
    var near = FinalHeightmap(distant: false, built: true);
    var built2 = FinalHeightmap(distant: true, built: true);
    var updaters = new List<IMonoUpdater> { near, built, new FinalUpdater(), fresh, built2 };
    var poked = new List<Heightmap>();
    int count = GameUtils.PokeDistantTerrain(updaters, poked.Add);
    C(count == 2 && poked.Count == 2 && ReferenceEquals(poked[0], built) && ReferenceEquals(poked[1], built2), "the distant heightmaps TerrainLod has built are poked, in the order they are listed, and no others");
    C(FinalBuild(built) == null && FinalBuild(built2) == null, "their build data is dropped, so that the rebuild asks for the terrain again");
    C(FinalBuild(near) != null && FinalBuild(fresh) == null && !poked.Any(h => ReferenceEquals(h, near) || ReferenceEquals(h, fresh)), "a zone's own heightmap is left to the other loop, and a distant one that was never built has nothing old to show");
    C(GameUtils.PokeDistantTerrain([], poked.Add) == 0 && GameUtils.PokeDistantTerrain([new FinalUpdater()], poked.Add) == 0, "no heightmaps, or none of them: nothing happens");

    // The builder's ready list.
    var oldInstance = GetStatic<object?>(typeof(HeightmapBuilder), "m_instance");
    try
    {
      var builder = FinalBuilder(ready: 5, queued: 3, new object());
      SetStatic(typeof(HeightmapBuilder), "m_instance", builder);
      GameUtils.ClearReadyBuilds();
      C(FinalReady(builder).Count == 0 && FinalQueued(builder).Count == 3, "the ready list is emptied and the queue is not: the build thread reads the queue's first in a second hold of the lock, and a queue emptied between would end the thread");

      // The lock: the list is not touched while somebody else holds it.
      var padlock = new object();
      builder = FinalBuilder(ready: 4, queued: 0, padlock);
      SetStatic(typeof(HeightmapBuilder), "m_instance", builder);
      using var holding = new ManualResetEventSlim();
      using var release = new ManualResetEventSlim();
      var holder = Task.Run(() =>
      {
        lock (padlock)
        {
          holding.Set();
          release.Wait();
        }
      });
      holding.Wait();
      var clearing = Task.Run(GameUtils.ClearReadyBuilds);
      Thread.Sleep(150);
      bool waited = !clearing.IsCompleted && FinalReady(builder).Count == 4;
      release.Set();
      holder.Wait();
      clearing.Wait(5000);
      C(waited && FinalReady(builder).Count == 0, "it takes the builder's lock: while the build thread holds it the list is not touched, and it is emptied when it lets go");

      // A builder that was disposed of has no lock; one that was never made is not made.
      builder = FinalBuilder(ready: 2, queued: 2, null);
      SetStatic(typeof(HeightmapBuilder), "m_instance", builder);
      GameUtils.ClearReadyBuilds();
      C(FinalReady(builder).Count == 2, "a builder that was disposed of (its lock is cleared) has nothing to clear, and nothing throws");
      SetStatic(typeof(HeightmapBuilder), "m_instance", null);
      GameUtils.ClearReadyBuilds();
      C(GetStatic<object?>(typeof(HeightmapBuilder), "m_instance") == null, "with no builder made yet none is made (HeightmapBuilder.instance would start a thread)");

      // The reset: the builds now, the grass and the builds again a frame later.
      var clutter = FinalAlive<ClutterSystem>();
      SetField(clutter, "m_patches", Activator.CreateInstance(typeof(ClutterSystem).GetField("m_patches", Any)!.FieldType));
      var oldClutter = GetStatic<object?>(typeof(ClutterSystem), "m_instance");
      try
      {
        SetStatic(typeof(ClutterSystem), "m_instance", clutter);
        builder = FinalBuilder(ready: 3, queued: 2, new object());
        SetStatic(typeof(HeightmapBuilder), "m_instance", builder);
        C(BC.instance == null, "(the plugin is not a running object here: the coroutine is not started, and the frame is driven by hand)");
        GameUtils.DropStaleTerrain();
        C(FinalReady(builder).Count == 0 && FinalQueued(builder).Count == 2 && !GetField<bool>(clutter, "m_forceRebuild"), "the builds are dropped at once, and the grass is not cleared in the frame of the pokes");
        var later = (IEnumerator)typeof(GameUtils).GetMethod("DropStaleTerrainLater", Any)!.Invoke(null, null)!;
        C(later.MoveNext() && !GetField<bool>(clutter, "m_forceRebuild"), "it waits for the next frame");
        FinalReady(builder).AddRange([new HeightmapBuilder.HMBuildData(Vector3.zero, 1, 1f, false, null), new HeightmapBuilder.HMBuildData(Vector3.zero, 1, 1f, false, null)]);
        C(!later.MoveNext(), "and is over after it");
        C(FinalReady(builder).Count == 0 && GetField<bool>(clutter, "m_forceRebuild"), "a build that was under way and landed meanwhile is dropped, and the grass is cleared: it is cut from the rebuilt ground");

        // A closed world: the grass singleton is destroyed (it looks null), or gone.
        var dead = Make<ClutterSystem>();
        SetStatic(typeof(ClutterSystem), "m_instance", dead);
        later = (IEnumerator)typeof(GameUtils).GetMethod("DropStaleTerrainLater", Any)!.Invoke(null, null)!;
        later.MoveNext();
        bool threw = false;
        try
        {
          later.MoveNext();
        }
        catch (Exception)
        {
          threw = true;
        }
        C(!threw, "a destroyed grass system (the world was closed) is not asked to clear anything");
        SetStatic(typeof(ClutterSystem), "m_instance", null);
        SetStatic(typeof(HeightmapBuilder), "m_instance", null);
        later = (IEnumerator)typeof(GameUtils).GetMethod("DropStaleTerrainLater", Any)!.Invoke(null, null)!;
        later.MoveNext();
        C(!later.MoveNext(), "and neither is a game with no grass system, or no builder, at all (the menu)");
      }
      finally
      {
        SetStatic(typeof(ClutterSystem), "m_instance", oldClutter);
      }
    }
    finally
    {
      SetStatic(typeof(HeightmapBuilder), "m_instance", oldInstance);
    }

    // Where it is called from: every poke of the loaded terrain, and the biome precision's rebuild, which cleared the grass in the frame of the pokes.
    var pokes = FinalCalls(typeof(GameUtils).GetMethod("PokeHeightmaps", Any)!);
    int loop = pokes.FindIndex(c => c.Name == "Poke" || (c.Name == "Invoke" && c.Type == typeof(Action<Heightmap>)));
    int distant = pokes.FindIndex(c => c.Name == "PokeDistantTerrain");
    int drop = pokes.FindIndex(c => c.Name == "DropStaleTerrain");
    C(loop >= 0 && distant > loop && drop > distant, "PokeHeightmaps pokes the zones' heightmaps, then the distant ones, then drops what is stale");
    C(FinalCalls(typeof(GameUtils).GetMethod("ResetZones")!).Any(c => c.Name == "PokeHeightmaps"), "(and ResetZones, for the command and for Better Continents' own regeneration alike, goes through it)");
    var precision = FinalCalls(typeof(BC).GetMethod("RegenerateLoadedTerrain", Any)!);
    C(precision.Any(c => c.Name == "DropStaleTerrain" && c.Type == typeof(GameUtils)) && !precision.Any(c => c.Name == "ClearAll") && precision.Count(c => c.Name == "Poke") == 1,
      "the biome precision's rebuild no longer clears the grass in the frame of its pokes: it drops what is stale, a frame later");
    var dropCalls = FinalCalls(typeof(GameUtils).GetMethod("DropStaleTerrain", Any)!);
    C(dropCalls.Any(c => c.Name == "ClearReadyBuilds") && dropCalls.Any(c => c.Name == "StartCoroutine"), "dropping starts the frame-later part on the plugin");
    var laterCalls = FinalCalls(typeof(GameUtils).GetNestedTypes(Any).Single(t => t.Name.Contains("DropStaleTerrainLater")).GetMethod("MoveNext", Any)!);
    C(laterCalls.Any(c => c.Name == "ClearReadyBuilds") && laterCalls.Any(c => c.Name == "ClearAll" && c.Type == typeof(ClutterSystem)), "... which drops the builds once more and clears the grass");
    var clearCalls = FinalCalls(typeof(GameUtils).GetMethod("ClearReadyBuilds", Any)!);
    C(clearCalls.Any(c => c.Name == "Enter" && c.Type == typeof(Monitor)) && clearCalls.Any(c => c.Name == "Exit" && c.Type == typeof(Monitor)) && clearCalls.Any(c => c.Name == "Clear")
      && !clearCalls.Any(c => c.Name == "get_instance"), "ClearReadyBuilds takes the lock, clears, and does not ask the game for the builder");
  }

  // ------------------------------------------------------------------------------------------------ F6: a zone's loaded copy leaves at once

  private static void FinalRootTests()
  {
    Section("final: a zone's loaded copy is out of the game's lists in the frame it is thrown away");
    var list = GetStatic<List<Heightmap>>(typeof(Heightmap), "s_heightmaps");
    var before = list.ToList();
    list.Clear();
    try
    {
      // (Made-up objects are all equal to each other, having no id: List.Remove takes the first of them, so only how many go can be told.)
      var a = Make<Heightmap>();
      var b = Make<Heightmap>();
      var c = Make<Heightmap>();
      list.AddRange([a, b, c]);
      ZoneRegen.GameWorld.RetireHeightmaps([b, c]);
      C(list.Count == 1, "the heightmaps of the root leave Heightmap's list: two of three");
      ZoneRegen.GameWorld.RetireHeightmaps([]);
      C(list.Count == 1, "none, changes nothing");
    }
    finally
    {
      list.Clear();
      list.AddRange(before);
    }

    var destroy = FinalCalls(typeof(ZoneRegen.GameWorld).GetMethod("DestroyRoot", Any)!);
    int retire = destroy.FindIndex(c => c.Name == "Retire");
    int gone = destroy.FindIndex(c => c.Name == "Destroy" || (c.Name == "Invoke" && c.Type == typeof(Action<GameObject>)));
    C(retire >= 0 && gone > retire, "DestroyRoot retires the root before it destroys it");
    var steps = FinalCalls(typeof(ZoneRegen.GameWorld).GetMethod("Retire", Any)!);
    int children = steps.FindIndex(c => c.Name == "GetComponentsInChildren");
    int leave = steps.FindIndex(c => c.Name == "RetireHeightmaps");
    int off = steps.FindIndex(c => c.Name == "SetActive" && c.Type == typeof(GameObject));
    C(children >= 0 && leave > children && off > leave, "it takes the heightmaps of the root, lets them leave the list, and then switches the root off");
    var retireIL = FinalIL(typeof(ZoneRegen.GameWorld).GetMethod("Retire", Any)!);
    int getChildren = retireIL.FindIndex(i => i.Operand is MethodInfo m && m.Name == "GetComponentsInChildren");
    C(getChildren > 0 && retireIL[getChildren - 1].Opcode == OpCodes.Ldc_I4_1, "(the inactive ones too: a zone's heightmap may be switched off)");
    C(FinalCalls(typeof(ZoneRegen.GameWorld).GetMethod("RetireHeightmaps", Any)!).Any(c => c.Name == "Remove" && c.Type == typeof(List<Heightmap>)), "(the list is Heightmap's own)");

    // If the engine says no (here it is not there at all), the root is still destroyed: retiring it is only for the rest of the frame.
    var job = new ZoneRegen.Job(_ => { });
    var gameWorld = new ZoneRegen.GameWorld(job);
    Exception? escaped = null;
    var said = LogHandler.During(() =>
    {
      try
      {
        gameWorld.Retire(FinalAlive<GameObject>());
      }
      catch (Exception e)
      {
        escaped = e;
      }
    });
    C(escaped == null && job.Errors == 1 && said.Any(l => l.Contains("a loaded zone could not be switched off before it was destroyed")), "a root that cannot be retired is counted as an error and logged, and nothing is thrown at the destroy: " + escaped?.GetType().Name);
    escaped = null;
    gameWorld.Retire(Make<GameObject>());
    C(job.Errors == 1, "(a root that is already gone is nothing to retire)");
  }

  // ------------------------------------------------------------------------------------------------ F4: the glue between the pieces

  private static void FinalGlueTests()
  {
    Section("final: the calls between ZoneRegen's pieces");
    var any = Any;

    // A second request stops the first: the zone being emptied is finished before the coroutine is stopped, since nothing resumes it.
    var stopCalls = FinalCalls(typeof(ZoneRegen).GetMethod("Stop", any)!);
    int finish = stopCalls.FindIndex(c => c.Name == "FinishInFlight" && c.Type == typeof(ZoneRegen));
    int stopRoutine = stopCalls.FindIndex(c => c.Name == "StopCoroutine" || (c.Name == "Invoke" && c.Type == typeof(Action<Coroutine>)));
    C(finish >= 0 && stopRoutine > finish, "Stop finishes the zone being emptied before it stops the coroutine");
    C(FinalCalls(typeof(ZoneRegen).GetMethod("FinishInFlight", any)!).Any(c => c.Name == "FinishInFlight" && c.Type == typeof(ZoneReset.Work)), "(and that is the work's own FinishInFlight)");

    // Stop, run: a zone half emptied is finished, the job and what it noted are forgotten; another job's are not.
    ZDOExtraData.Reset();
    var fake = new FakeWorld();
    uint next = 1;
    fake.Fill([Z(0, 0)], 100, ref next);
    fake.Generated.Add(Z(0, 0));
    var running = new ZoneRegen.Job(_ => { });
    running.Work = new ZoneReset.Work(fake, ZoneReset.MakePlan(fake.Generated, [], [Z(0, 0)]), new Budget(30, 1_000_000, () => 0), running.Progress, new ZoneReset.Memory());
    var half = running.Work.Steps();
    half.MoveNext();
    var noted = GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun");
    try
    {
      C(running.Work.InFlight, "(a zone of 100 objects is half emptied)");
      SetStatic(typeof(ZoneRegen), "job", running);
      noted.Add(Z(3, 3));
      var said = LogHandler.During(() => typeof(ZoneRegen).GetMethod("Stop", any)!.Invoke(null, [running, "starting over"]));
      C(!running.Work.InFlight && fake.Destroyed.Count == 100 && GetStatic<object?>(typeof(ZoneRegen), "job") == null && noted.Count == 0 && said.Any(l => l.Contains("Zone regeneration: starting over.")),
        "stopping the job finishes the zone, forgets the job and the zones it noted, and says why");
      var other = new ZoneRegen.Job(_ => { });
      SetStatic(typeof(ZoneRegen), "job", other);
      noted.Add(Z(3, 3));
      typeof(ZoneRegen).GetMethod("Stop", any)!.Invoke(null, [new ZoneRegen.Job(_ => { }), "starting over"]);
      C(ReferenceEquals(GetStatic<object?>(typeof(ZoneRegen), "job"), other) && noted.Count == 1, "stopping a job that is not the current one leaves the current one and its zones alone");
    }
    finally
    {
      SetStatic(typeof(ZoneRegen), "job", null);
      noted.Clear();
    }

    // A run that is over forgets the zones it noted: the same, from the coroutine's end.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(1))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      foreach (var keep in new[] { false, true })
      {
        var job = new ZoneRegen.Job(_ => { });
        var other = new ZoneRegen.Job(_ => { });
        SetStatic(typeof(ZoneRegen), "job", job);
        var run = (IEnumerator)typeof(ZoneRegen).GetMethod("Run", any)!.Invoke(null, [job])!;
        int guard = 0;
        bool placed = false;
        while (guard++ < 1000 && run.MoveNext())
        {
          w.Flush();
          if (!placed && job.Progress.Total > 0)
          {
            placed = true;
            ZoneRegen.NotePlaced(new Vector3(50 * 64f, 30f, 0f));
            if (keep)
              SetStatic(typeof(ZoneRegen), "job", other);
          }
        }
        if (!keep)
          C(placed && GetStatic<object?>(typeof(ZoneRegen), "job") == null && noted.Count == 0, "a run that is over leaves no job and no zone noted");
        else
          C(placed && ReferenceEquals(GetStatic<object?>(typeof(ZoneRegen), "job"), other) && noted.Count == 1, "a run that is over, when another job has taken its place, leaves that one and what it has noted");
        SetStatic(typeof(ZoneRegen), "job", null);
        noted.Clear();
        foreach (var zone in Square(1))
          w.Generated.Add(zone);
      }
    }
    finally
    {
      SetStatic(typeof(ZoneRegen), "job", null);
      noted.Clear();
      World.Close();
    }

    // Steps hands the plan the locations the game has placed, and the zones a stopped run left emptied.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in new[] { Z(0, 0), Z(1, 0), Z(8, 8) })
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      // A location in (0, 0) that reaches into (1, 0), and a piece in (2, 0), which keeps (1, 0) and not (0, 0).
      w.PlaceLocation(Z(0, 0), true, 40f, dx: 25f);
      Creator(w.Add(Z(2, 0), "piece_chest"), 42L);
      Regenerate(w);
      C(w.Generated.SetEquals([Z(0, 0), Z(1, 0)]), "the plan is made with the locations that are placed: the piece beside the zone the location reaches into keeps the home as well (left: " + Sorted(w.Generated) + ")");
    }
    finally
    {
      World.Close();
    }
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in new[] { Z(0, 0), Z(1, 0) })
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      Creator(w.Add(Z(1, 0), "piece_chest"), 42L);
      ZoneRegen.MemoryFor(w.Zones).Pending.Add(Z(0, 0));
      Regenerate(w);
      C(w.Generated.SetEquals([Z(1, 0)]), "the plan is made with the zones an earlier run emptied: the one beside the piece is reset all the same (left: " + Sorted(w.Generated) + ")");
    }
    finally
    {
      World.Close();
    }

    // Each peer's own radius: the near simulation distance of its game.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      var two = w.Add(Z(0, 0), "Player", user: 700L);
      two.SetOwner(700L);
      w.Peer(two, 700L).m_simulationDistance = new SimulationDistance(2, 4);
      var three = w.Add(Z(9, 0), "Player", user: 701L);
      three.SetOwner(701L);
      w.Peer(three, 701L).m_simulationDistance = new SimulationDistance(3, 4);
      var stranger = w.Add(Z(5, 5), "Player", user: 999L);
      stranger.SetOwner(999L);
      var radii = new ZoneRegen.PeerRadii(w.Net);
      var peers = GetField<List<ZNetPeer>>(w.Net, "m_peers");
      C(radii.For(two) == 2 && radii.For(three) == 3 && radii.Of(peers[0]) == 2 && radii.Of(peers[1]) == 3, "two peers of near distance 2 and 3: each player and each peer gets its own radius");
      C(radii.For(stranger) == 3, "a player whose peer is not found gets the widest of those connected");
      var lonely = Make<ZNetPeer>();
      lonely.m_uid = 702L;
      lonely.m_simulationDistance = new SimulationDistance(1, 4);
      C(radii.Of(lonely) == 3, "and so does a peer that was not there when the radii were made");
    }
    finally
    {
      World.Close();
    }

    // A player who has joined and has no character yet: the scan and the live look both give them their own game's square.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(6))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var loading = Make<ZNetPeer>();
      loading.m_uid = 701L;
      loading.m_refPos = new Vector3(4 * 64f, 30f, 4 * 64f);
      loading.m_simulationDistance = new SimulationDistance(3, 2);
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(loading);
      var live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().ToList();
      C(live.Count == 1 && live[0].Zone.Equals(Z(4, 4)) && live[0].Radius == 3, "the live look gives a player with no character the near distance of their game");
      var (lines, _, job) = Regenerate(w);
      var square = Square(6).Where(z => Math.Max(Math.Abs(z.x - 4), Math.Abs(z.y - 4)) <= 3).ToList();
      // (The live look would keep the same zones at their turns whatever the scan said; the plan and what the run left alone tell them apart.)
      C(w.Generated.SetEquals(square) && square.Count == 36 && lines[0].Contains("resetting 133 of 169 generated zones; 36 left alone") && job.Progress.Skipped == 0,
        $"the scan keeps the same square, 7 x 7 where the world has it, in the plan: {lines[0]}");
    }
    finally
    {
      World.Close();
    }

    // The save leaves out the zones of this world's run, and no other world's.
    ZDOExtraData.Reset();
    var first = new World();
    var memory = ZoneRegen.MemoryFor(first.Zones);
    memory.Pending.Add(Z(0, 0));
    first.Generated.Add(Z(0, 0));
    first.PlaceLocation(Z(0, 0), true, 0f);
    var second = new World();
    try
    {
      second.Generated.Add(Z(0, 0));
      second.PlaceLocation(Z(0, 0), true, 0f);
      second.Zones.PrepareSave();
      ZoneRegen.HidePendingFromSave(second.Zones);
      C(GetField<HashSet<Vector2s>>(second.Zones, "m_tempGeneratedZonesSaveClone").Contains(Z(0, 0)) && GetField<List<ZoneSystem.LocationInstance>>(second.Zones, "m_tempLocationsSaveClone").Single().m_placed,
        "a save of another world, while zones of this one wait: nothing is left out");
      first.Zones.PrepareSave();
      ZoneRegen.HidePendingFromSave(first.Zones);
      C(!GetField<HashSet<Vector2s>>(first.Zones, "m_tempGeneratedZonesSaveClone").Contains(Z(0, 0)) && !GetField<List<ZoneSystem.LocationInstance>>(first.Zones, "m_tempLocationsSaveClone").Single().m_placed,
        "(control: the save of the world whose zones wait leaves the zone out, and its location unplaced)");
    }
    finally
    {
      memory.Pending.Clear();
      World.Close();
    }

    // A spawner's creature that stands in the 3 x 3 around a zone left alone at its turn stays, though no plan or player covers it.
    var p = Z(0, 0);
    var fakeWorld = new FakeWorld();
    fakeWorld.Add(p, O(1)).Add(Z(5, 0), O(2)).Add(Z(1, 0), O(3));
    fakeWorld.Spawns(2, 3);
    fakeWorld.OnList = zone =>
    {
      if (zone.Equals(p) && fakeWorld.Objects[zone].Count == 1)
        fakeWorld.Add(zone, O(99, Kind.Piece));
    };
    var (_, spawnProgress, _) = Go(fakeWorld, ZoneReset.MakePlan([p, Z(5, 0)], [], [p]), Roomy());
    C(fakeWorld.Destroyed.Contains(Id(2)) && !fakeWorld.Destroyed.Contains(Id(3)) && spawnProgress.SpawnedKept == 1 && spawnProgress.Spawned == 0,
      "the spawner goes, the creature it made stays: it stands beside a zone that was left alone at its turn");

    // How many objects a frame may destroy: a peer that is not ready counts for nothing.
    ZDOExtraData.Reset();
    w = new World();
    var oldDelta = ZoneRegen.DeltaTime;
    try
    {
      SetStatic(typeof(ZoneRegen), "frameFraction", 0.0);
      var connecting = Make<ZNetPeer>();
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(connecting);
      bool asked = false;
      ZoneRegen.DeltaTime = () =>
      {
        asked = true;
        return 1f / 60f;
      };
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame && !asked, "a peer that is connecting (not ready yet) does not hold the frames to the allowance, and the clock is not asked");
      var ready = Make<ZNetPeer>();
      ready.m_uid = 5L;
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(ready);
      ZoneRegen.DeltaTime = () => 1f / 60f;
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrameWithPlayers, "a peer that is ready does");
    }
    finally
    {
      ZoneRegen.DeltaTime = oldDelta;
      SetStatic(typeof(ZoneRegen), "frameFraction", 0.0);
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ the game's own code the final round relies on

  private static List<Instruction> FinalIL(MethodBase method) =>
    PatchProcessor.GetOriginalInstructions(method).Select(i => new Instruction(i.opcode, i.operand)).ToList();

  private readonly record struct Instruction(OpCode Opcode, object? Operand);

  private static void FinalFactTests()
  {
    Section("final: the game's own code the final round relies on (the installed game)");
    var heightmap = typeof(Heightmap);

    // Distant terrain is not in s_heightmaps: Awake adds a heightmap only if it is not distant, OnDestroy takes it off the same way, and
    // the property moves it in and out; the list of every enabled one is another list.
    bool Reads(MethodBase method, string field) => FinalIL(method).Any(i => i.Opcode == OpCodes.Ldfld && i.Operand is FieldInfo f && f.Name == field);
    var awake = heightmap.GetMethod("Awake", Any)!;
    var destroyed = heightmap.GetMethod("OnDestroy", Any)!;
    C(Reads(awake, "m_isDistantLod") && FinalCalls(awake).Any(c => c.Name == "Add" && c.Type == typeof(List<Heightmap>)), "Heightmap.Awake adds the heightmap to s_heightmaps only when it reads m_isDistantLod");
    C(Reads(destroyed, "m_isDistantLod") && FinalCalls(destroyed).Any(c => c.Name == "Remove" && c.Type == typeof(List<Heightmap>)), "Heightmap.OnDestroy takes it off only when it reads m_isDistantLod");
    var setter = heightmap.GetProperty("IsDistantLod")!.SetMethod!;
    C(FinalCalls(setter).Any(c => c.Name == "Remove" && c.Type == typeof(List<Heightmap>)) && FinalCalls(setter).Any(c => c.Name == "Add" && c.Type == typeof(List<Heightmap>)), "IsDistantLod moves a heightmap out of s_heightmaps and back");
    C(heightmap.GetProperty("Instances")?.PropertyType == typeof(List<IMonoUpdater>) && FinalCalls(heightmap.GetMethod("OnEnable", Any)!).Any(c => c.Name == "Add" && c.Type == typeof(List<IMonoUpdater>))
      && FinalCalls(heightmap.GetMethod("OnDisable", Any)!).Any(c => c.Name == "Remove" && c.Type == typeof(List<IMonoUpdater>)), "Heightmap.Instances is a List<IMonoUpdater> that OnEnable fills and OnDisable empties: the distant ones are in it");
    C(heightmap.GetField("m_buildData", Any)?.FieldType == typeof(HeightmapBuilder.HMBuildData) && FinalCalls(heightmap.GetMethod("Generate", Any)!).Any(c => c.Name == "RequestTerrainSync"),
      "Heightmap.Generate asks the builder for the terrain when its build data is gone");

    // TerrainLod builds its heightmaps again only when the camera has moved 256 m, and rebuilds them with Regenerate.
    var lod = typeof(TerrainLod);
    C(FinalIL(lod.GetConstructors(Any).First()).Any(i => i.Opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.Operand) == 256f) && Reads(lod.GetMethod("NeedsRebuild", Any)!, "m_updateStepDistance"),
      "TerrainLod rebuilds when the camera has moved m_updateStepDistance, 256 m by default");
    C(FinalCalls(lod.GetMethod("RebuildHeightmap", Any)!).Any(c => c.Name == "Regenerate" && c.Type == heightmap) && FinalCalls(lod.GetMethod("CreateMesh", Any)!).Any(c => c.Name == "set_IsDistantLod"),
      "its nine heightmaps are distant ones that it regenerates itself");

    // The builder: two lists and one lock; the thread adds a finished build to the ready list and trims it in one hold, and reads the first
    // queued build in a second hold after it has seen the queue is not empty; a request takes a ready build out of the list.
    var builder = typeof(HeightmapBuilder);
    C(builder.GetField("m_ready", Any)?.FieldType == typeof(List<HeightmapBuilder.HMBuildData>) && builder.GetField("m_toBuild", Any)?.FieldType == typeof(List<HeightmapBuilder.HMBuildData>)
      && builder.GetField("m_lock", Any)?.FieldType == typeof(object) && builder.GetField("m_instance", Any)?.IsStatic == true, "HeightmapBuilder: m_ready and m_toBuild lists, an object m_lock, and the static m_instance");
    var thread = FinalIL(builder.GetMethod("BuildThread", Any)!);
    int count = thread.FindIndex(i => i.Operand is MethodInfo m && m.Name == "get_Count" && m.DeclaringType == typeof(List<HeightmapBuilder.HMBuildData>));
    int exit = thread.FindIndex(count, i => i.Operand is MethodInfo m && m.Name == "Exit" && m.DeclaringType == typeof(Monitor));
    int item = thread.FindIndex(count, i => i.Operand is MethodInfo m && m.Name == "get_Item" && m.DeclaringType == typeof(List<HeightmapBuilder.HMBuildData>));
    C(count >= 0 && exit > count && item > exit, "BuildThread looks at the queue's count in one hold of the lock and reads its first build in a later one: a queue emptied between would throw there");
    var added = FinalCalls(builder.GetMethod("BuildThread", Any)!);
    C(added.Any(c => c.Name == "Remove") && added.Any(c => c.Name == "Add") && added.Any(c => c.Name == "RemoveAt"), "it removes the finished build from the queue, adds it to the ready list and trims that");
    C(FinalCalls(builder.GetMethod("RequestTerrain", Any)!).Any(c => c.Name == "RemoveAt") && FinalIL(builder.GetMethod("Dispose")!).Any(i => i.Opcode == OpCodes.Stfld && i.Operand is FieldInfo f && f.Name == "m_lock"),
      "a request takes an equal build out of the ready list, and Dispose clears the lock");

    // The grass: ClearAll sets the flag that LateUpdate rebuilds from, and the grass waits only for heightmaps with a rebuild queued for the
    // custom update (2): the delayed poke (1) is not one.
    var clutter = typeof(ClutterSystem);
    C(FinalIL(clutter.GetMethod("ClearAll", Any)!).Any(i => i.Opcode == OpCodes.Stfld && i.Operand is FieldInfo f && f.Name == "m_forceRebuild") && FinalCalls(clutter.GetMethod("LateUpdate", Any)!).Any(c => c.Name == "UpdateGrass"),
      "ClutterSystem.ClearAll sets m_forceRebuild, and the grass is rebuilt from the ground in LateUpdate");
    var queued = FinalIL(heightmap.GetMethod("HaveQueuedRebuild", Any, null, Type.EmptyTypes, null)!);
    C(queued.Any(i => i.Opcode == OpCodes.Ldc_I4_2) && !queued.Any(i => i.Opcode == OpCodes.Ldc_I4_1), "a heightmap counts as queued only when m_doLateUpdate is 2: the grass does not wait for a Poke(1)");

    // A dungeon is built inside one zone: the generator's bounds are that zone's cube, and a room must lie inside them; the interior's
    // volume is one zone wide, at that zone's centre.
    var dungeon = typeof(DungeonGenerator);
    var generate = FinalCalls(dungeon.GetMethod("Generate", Any, null, [typeof(int), typeof(ZoneSystem.SpawnMode)], null)!);
    C(generate.Any(c => c.Name == "GetZone" && c.Type == typeof(ZoneSystem)) && generate.Any(c => c.Name == "GetZonePos" && c.Type == typeof(ZoneSystem)), "DungeonGenerator.Generate takes the zone of the generator's position and its centre");
    int contains = FinalCalls(dungeon.GetMethod("IsInsideDungeon", Any)!).Count(c => c.Name == "Contains" && c.Type == typeof(Bounds));
    C(contains == 8 && FinalCalls(dungeon.GetMethod("TestCollision", Any)!).Any(c => c.Name == "IsInsideDungeon"), "IsInsideDungeon asks whether all eight corners of a room are inside the bounds, and a room that is not collides");
    C(dungeon.GetField("m_zoneSize")?.FieldType == typeof(Vector3) && FinalIL(dungeon.GetConstructors(Any).First(c => !c.IsStatic)).Count(i => i.Opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.Operand) == 64f) >= 3, "m_zoneSize is 64 m on each side by default");
    var locationAwake = FinalIL(typeof(Location).GetMethod("Awake", Any)!);
    C(locationAwake.Any(i => i.Opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.Operand) == 5000f) && locationAwake.Count(i => i.Opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.Operand) == 64f) >= 2
      && FinalCalls(typeof(Location).GetMethod("GetZoneCenter", Any)!).Any(c => c.Name == "GetZonePos"), "Location.Awake puts the interior's volume 5000 m up, 64 m wide, at the centre of the location's zone");

    // The water of a zone that is switched off leaves WaterVolume.Instances.
    var water = typeof(WaterVolume);
    C(FinalCalls(water.GetMethod("OnEnable", Any)!).Any(c => c.Name == "Add" && c.Type == typeof(List<WaterVolume>)) && FinalCalls(water.GetMethod("OnDisable", Any)!).Any(c => c.Name == "Remove" && c.Type == typeof(List<WaterVolume>)),
      "WaterVolume.Instances gains a volume when it is enabled and loses it when it is disabled");
  }
}

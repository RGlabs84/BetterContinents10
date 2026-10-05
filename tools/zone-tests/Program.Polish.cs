// Added by Wubarrk on 2026-10-05 for the unifying refactor (0.10.0).
//
// What the polish round asked for: a save between a second request and the work of the run that took its place (the new run has no
// work until its scan and its plan are over, and the stopped run's work takes the turns of the groups of the zones it left waiting, so a
// file never holds half a location group), and Work.Promote's counts for every one of the eight zones around a stray that the game
// generated before its turn.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using BetterContinents;
using UnityEngine;

internal static partial class Program
{
  private static void PolishTests()
  {
    ZoneRegen.DeltaTime = () => 1f / 60f;
    PolishWindowTests();
    PolishForgetTests();
    PolishPromoteTests();
    PolishReplanTests();
  }

  // A player placed a chest beside the zone next to the home after the first run's scan, and a save comes before the second run has its
  // work: in the frame between its plan and its work, or while its scan goes on. The zone beside the home must not be left empty and
  // generated (a save in the plan's frame leaves it waiting where the plan did not carry it, so the run plans again).
  private static void PolishReplanTests()
  {
    Section("polish: a save in the frame between the second run's plan and its work: no zone is left empty and generated");
    foreach (var variant in new[] { "save in the frame between the plan and the work", "save while the scan goes on" })
    {
      using var seams = new Seams();
      ZoneRegen.DeltaTime = () => 1f / 60f;
      var scene = new PolishScene();
      var harmony = new Harmony("zone-tests.replan");
      try
      {
        PatchPrepareSave(harmony);
        var w = scene.W;
        var n = Z(2, 0);
        for (int i = 0; i < 5; i++)
          w.Add(n, "Pine", dx: i);
        w.Generated.Add(n);
        C(scene.RunFirstUntilHomeWaits(), $"[{variant}] the first run has emptied the home, which waits for the zone beside it");
        Creator(w.Add(Z(1, 1), "piece_chest"), 42L);
        if (variant == "save while the scan goes on")
        {
          int looks = 0;
          ZoneRegen.FrameClock = () => ++looks == 50 ? 100.0 : 0.0;
        }
        ZoneRegen.Request(scene.Lines.Add);
        scene.Save();
        scene.Sched.Finish(w);
        w.Flush();
        bool bare = w.Generated.Contains(scene.Beside) && w.LiveIn(scene.Beside).Count == 0;
        C(!bare && scene.Memory.Pending.Count == 0, $"[{variant}] the zone beside the home is not left empty and generated, and nothing waits at the end (generated {Show(w.Generated)})");
      }
      finally
      {
        harmony.UnpatchAll("zone-tests.replan");
        World.Close();
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ a save between two runs

  private static ZoneReset.Work? StoppedWork => GetStatic<ZoneReset.Work?>(typeof(ZoneRegen), "stoppedWork");

  // The world of these scenes, on the game's own classes: a player far away (a frame destroys 100 objects at most), a home of 299 objects
  // whose location reaches the zone beside it (5 objects), which makes a group of two that is done in the order home, beside, and a
  // zone of no group (250 objects) that is done last. Run until the home is emptied and waits for the zone beside it, which has not had
  // its turn.
  private sealed class PolishScene
  {
    public readonly World W;
    public readonly Scheduler Sched = new();
    public readonly List<string> Lines = [];
    public readonly Vector2s Home = Z(0, 0), Beside = Z(1, 0), Other = Z(9, 0);
    public const int HomeObjects = 299, BesideObjects = 5, OtherObjects = 250;
    public ZoneRegen.Job First = null!;
    public ZoneReset.Memory Memory = null!;

    public PolishScene()
    {
      ZoneRegen.StartRoutine = Sched.Start;
      ZoneRegen.StopRoutine = Sched.Stop;
      ZoneRegen.FrameClock = () => 0.0;
      ZoneRegen.Finish = () => { };
      ZDOExtraData.Reset();
      W = ServerWorld();
      // What the game does with the destroy queue: every machine is told, and the objects leave the world's lists.
      ZoneRegen.SendDestroyQueue = W.Flush;
      var far = W.Add(Z(-40, -40), "Player", user: 700L);
      far.SetOwner(700L);
      W.Peer(far, 700L);
      for (int i = 0; i < HomeObjects; i++)
        W.Add(Home, "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
      for (int i = 0; i < BesideObjects; i++)
        W.Add(Beside, "Pine", dx: i, dz: 0f);
      for (int i = 0; i < OtherObjects; i++)
        W.Add(Other, "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
      foreach (var zone in new[] { Home, Beside, Other })
        W.Generated.Add(zone);
      // The location of the home reaches the zone beside it.
      W.PlaceLocation(Home, true, 20f, dx: 20f);
    }

    // The first run, to the frame in which the home waits.
    public bool RunFirstUntilHomeWaits()
    {
      ZoneRegen.Request(Lines.Add);
      First = CurrentJob!;
      Memory = GetStatic<ZoneReset.Memory>(typeof(ZoneRegen), "memory");
      int guard = 0;
      while (guard++ < 100 && !(Memory.Pending.Contains(Home) && W.LiveIn(Beside).Count == BesideObjects))
      {
        Sched.Tick();
        W.Flush();
      }
      return First.Work != null && Memory.Pending.SetEquals([Home]) && W.LiveIn(Home).Count == 0 && W.LiveIn(Beside).Count == BesideObjects && W.Generated.Contains(Home);
    }

    // What ZNet.SaveWorld does at the start, and ZoneSystem.PrepareSave: the copies the save thread writes.
    public (HashSet<Vector2s> Generated, ZoneSystem.LocationInstance HomeLocation) Save()
    {
      ZoneRegen.BeforeSave();
      W.Zones.PrepareSave();
      var generated = GetField<HashSet<Vector2s>>(W.Zones, "m_tempGeneratedZonesSaveClone");
      var locations = GetField<List<ZoneSystem.LocationInstance>>(W.Zones, "m_tempLocationsSaveClone");
      return (generated, locations.Single(l => ZoneSystem.GetZone(l.m_position) == Home));
    }
  }

  private static void PolishWindowTests()
  {
    Section("polish: a save between a second request and the work of the new run: the file holds the group wholly emptied");
    // (0) no second request: the run's own work takes the turns; the others: one, two requests, and one made while the scan is still going.
    foreach (var how in new[] { "no second request", "one second request", "two second requests", "a second request in the middle of its scan" })
    {
      using var seams = new Seams();
      var scene = new PolishScene();
      var harmony = new Harmony("zone-tests.polish");
      try
      {
        PatchPrepareSave(harmony);
        C(scene.RunFirstUntilHomeWaits(), $"[{how}] (the first run has emptied the home, which waits for the zone beside it, which has not had its turn)");
        var first = scene.First.Work;
        var w = scene.W;
        var requests = how switch { "no second request" => 0, "two second requests" => 2, _ => 1 };
        if (how == "a second request in the middle of its scan")
        {
          // The clock says the frame is over at its 50th look: the second run's scan ends its first frame before there is a plan.
          int looks = 0;
          ZoneRegen.FrameClock = () => ++looks == 50 ? 100.0 : 0.0;
        }
        for (int i = 0; i < requests; i++)
          ZoneRegen.Request(scene.Lines.Add);
        var latest = CurrentJob!;
        if (requests > 0)
        {
          C(latest.Work == null && !ReferenceEquals(latest, scene.First) && ReferenceEquals(StoppedWork, first),
            $"[{how}] the new run has no work yet, and the work of the run that was stopped is kept");
          C(how != "a second request in the middle of its scan" || latest.Progress.Total == 0, $"[{how}] (the scan has not ended: no plan yet)");
        }

        // ZNet.SaveWorld, first, and ZoneSystem.PrepareSave.
        var (generated, homeLocation) = scene.Save();
        bool half = !generated.Contains(scene.Home) && generated.Contains(scene.Beside) && w.LiveIn(scene.Beside).Count == PolishScene.BesideObjects;
        C(!half, $"[{how}] the file does not hold the home not generated while the zone beside it is generated with its objects");
        C(!generated.Contains(scene.Home) && !generated.Contains(scene.Beside) && !homeLocation.m_placed && w.LiveIn(scene.Home).Count == 0 && w.LiveIn(scene.Beside).Count == 0,
          $"[{how}] the group is wholly emptied in the file: neither zone generated, the location not placed, no object of either in the world's lists");
        C(generated.Contains(scene.Other) && w.LiveIn(scene.Other).Count == PolishScene.OtherObjects, $"[{how}] the zone of no group is as it was");
        C(scene.First.Progress.Reset == 2, $"[{how}] (the work of the first run finished the two zones of the group: {scene.First.Progress.Reset})");

        // The new run goes on from there: its work is made, the stopped one is forgotten, and no object is destroyed twice.
        if (requests > 0)
        {
          int guard = 0;
          while (guard++ < 50 && latest.Work == null && scene.Sched.Running.Count > 0)
          {
            scene.Sched.Tick();
            w.Flush();
          }
          C(latest.Work != null && CurrentJob == latest && StoppedWork == null, $"[{how}] when the new run has made its work, and goes on with it, the stopped run's is forgotten");
        }
        scene.Sched.Finish(w);
        w.Flush();
        var all = w.All.ToList();
        C(scene.Lines.Last().StartsWith("Zone regeneration finished in") && CurrentJob == null && StoppedWork == null && scene.Sched.Running.Count == 0,
          $"[{how}] the run ends, and nothing is kept: " + scene.Lines.Last());
        int objects = PolishScene.HomeObjects + PolishScene.BesideObjects + PolishScene.OtherObjects;
        C(w.Generated.Count == 0 && scene.Memory.Pending.Count == 0 && all.Count == objects && all.Distinct().Count() == objects,
          $"[{how}] every zone is reset in the end, nothing is left waiting, and each of the {objects} objects was destroyed once ({all.Count} sent, {all.Distinct().Count()} different)");
      }
      finally
      {
        harmony.UnpatchAll("zone-tests.polish");
        World.Close();
      }
    }
  }

  // The stopped run's work is kept for a save and for nothing else: it is forgotten when the run that took its place ends, or fails, before it
  // has made its own; and a world that was left does not give it to a save in the next.
  private static void PolishForgetTests()
  {
    Section("polish: the stopped run's work is forgotten when the new run ends or fails before it makes its own, and it is for its own world only");

    // The new run ends in its scan: the world closes.
    using (var seams = new Seams())
    {
      var scene = new PolishScene();
      try
      {
        C(scene.RunFirstUntilHomeWaits(), "(the first run has emptied the home, which waits)");
        int looks = 0;
        ZoneRegen.FrameClock = () => ++looks == 50 ? 100.0 : 0.0;
        ZoneRegen.Request(scene.Lines.Add);
        C(CurrentJob is { Work: null } && StoppedWork != null, "(a second request: the scan is under way, and the first run's work is kept)");
        World.Close();
        var log = LogHandler.During(() => scene.Sched.Finish(null, 50));
        C(CurrentJob == null && StoppedWork == null && scene.Sched.Running.Count == 0 && log.Contains("[Log] [BetterContinents] Zone regeneration: stopped, the world closed."),
          "the world closes during the scan: the run ends and the first run's work is forgotten with it: " + string.Join(" | ", log));
      }
      finally
      {
        World.Close();
      }
    }

    // The new run fails before it has any work: it finishes the zones left waiting itself, and nothing is kept.
    using (var seams = new Seams())
    {
      var scene = new PolishScene();
      try
      {
        C(scene.RunFirstUntilHomeWaits(), "(the first run has emptied the home, which waits)");
        ZoneRegen.DeltaTime = () => throw new InvalidOperationException("the clock failed at once");
        var log = LogHandler.During(() => ZoneRegen.Request(scene.Lines.Add));
        C(scene.Lines.Contains("Zone regeneration stopped by an error, see the log.") && CurrentJob == null && StoppedWork == null,
          "an error in the new run's first frame: it ends, and the first run's work is forgotten: " + string.Join(" | ", scene.Lines));
        C(!scene.W.Generated.Contains(scene.Home) && scene.Memory.Pending.Count == 0 && log.Any(l => l.Contains(ZoneReset.PendingLine(1))) && scene.W.Generated.Contains(scene.Beside),
          "(the zone left waiting was finished by the error, as always, and the zone nobody reached is as it was)");
        var saved = LogHandler.During(ZoneRegen.BeforeSave);
        C(!saved.Any(l => l.StartsWith("[Error]")) && scene.W.LiveIn(scene.Beside).Count == PolishScene.BesideObjects, "a save then takes nobody's turns: it has nothing left to finish");
      }
      finally
      {
        World.Close();
      }
    }

    // A world that was left: a save in the next one is not given the work of a run of the last.
    using (var seams = new Seams())
    {
      var scene = new PolishScene();
      try
      {
        C(scene.RunFirstUntilHomeWaits(), "(the first run has emptied the home, which waits)");
        ZoneRegen.Request(scene.Lines.Add);
        C(CurrentJob is { Work: null } && StoppedWork != null, "(a second request: the new run has no work yet)");
        World.Close();
        var next = new World();
        foreach (var zone in new[] { scene.Home, scene.Beside })
        {
          for (int i = 0; i < 4; i++)
            next.Add(zone, "Pine", dx: i);
          next.Generated.Add(zone);
        }
        var log = LogHandler.During(ZoneRegen.BeforeSave);
        C(!log.Any(l => l.StartsWith("[Error]")) && next.LiveIn(scene.Home).Count == 4 && next.LiveIn(scene.Beside).Count == 4 && next.Generated.Count == 2,
          "a save in another world touches none of its zones, and logs no error: " + string.Join(" | ", log));
      }
      finally
      {
        World.Close();
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ Work.Promote's square

  private static void PolishPromoteTests()
  {
    Section("polish: a stray the game generated before its turn is a neighbour to the zones waiting on any side of it");
    // The stray (0,0) is reached by the location of W, the zone on one of its eight sides. W is the first of the plan (the players are in
    // it), so the nearest zone of the stray's group, and the stray has its turn right after it; O, the zone beyond W, has its turn after
    // the stray's. The game generates the stray as W is listed. W is emptied and waits for O, and once the stray is generated for the stray
    // as well: Work.Promote counts that for the zones waiting around it. A side it misses lets W finish as soon as the stray's turn is over,
    // before O's, with O's objects about to go.
    var stray = Z(0, 0);
    int scenes = 0;
    foreach (var (dx, dy, side) in new[]
    {
      (1, 0, "east"), (-1, 0, "west"), (0, 1, "north"), (0, -1, "south"),
      (1, 1, "north-east"), (-1, 1, "north-west"), (1, -1, "south-east"), (-1, -1, "south-west"),
    })
    {
      var waiting = Z(dx, dy);
      var outer = Z(2 * dx, 2 * dy);
      // The location's centre is 30 m from the middle of W towards the stray; its circle of 10 m reaches into the stray (on a diagonal it
      // reaches the two zones that share the corner as well, which are not generated: strays that the game never generates).
      var reach = LocIn(dx, dy, -30f * dx, -30f * dy, 10f);
      var scene = new FakeWorld();
      scene.Add(waiting, O(1), O(2)).Add(stray, O(3), O(4)).Add(outer, O(20), O(21));
      scene.Generated.UnionWith([waiting, outer]);
      scene.OnList = zone =>
      {
        if (!zone.Equals(waiting))
          return;
        scene.Generated.Add(stray);
        scene.Add(stray, O(60), O(61));
      };
      var memory = new ZoneReset.Memory();
      var plan = ZoneReset.MakePlan(scene.Generated, [], [waiting], [reach]);
      bool shaped = plan.Strays.Contains(stray) && plan.Turns[0].Equals(waiting) && plan.Turns[^1].Equals(outer) && plan.Turns.Count == plan.Reset.Count + plan.Strays.Count
                    && plan.GroupMembers(waiting).Contains(stray) && !plan.GroupMembers(waiting).Contains(outer);
      C(shaped, $"({side}: W first, its stray after it, O last: {Show(plan.Turns)}; strays {Show(plan.Strays)})");
      var (_, progress, _) = Go(scene, plan, Roomy(), memory);
      scenes++;
      int lastOfOuter = scene.Calls.FindLastIndex(c => c == "destroy 20" || c == "destroy 21");
      int finishW = scene.Calls.IndexOf($"unplace {dx},{dy}");
      C(scene.Calls.Contains("destroy 60") && scene.Calls.Contains("destroy 3") && scene.Calls.Contains("unplace 0,0"),
        $"{side}: the stray the game generated is emptied (its fill and the old parts of the location) and finished like a zone of the plan");
      C(lastOfOuter > 0 && finishW > lastOfOuter,
        $"{side}: the zone waiting on that side of the stray is finished after the zone beyond it has had its turn, not as soon as the stray has had its: " + string.Join(" ", scene.Calls));
      C(progress.Total == 3 && progress.Done == 3 && progress.Reset == 3 && memory.Pending.Count == 0 && scene.Calls.Count(c => c.StartsWith("unplace")) == 3,
        $"{side}: three zones are counted and reset, each finished once, and none is left waiting ({progress.Done} of {progress.Total}, {progress.Reset} reset)");
    }
    C(scenes == 8, "(a scene for every one of the eight zones around the stray)");
  }
}

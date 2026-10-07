// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// Zone regeneration for debug mode, the game's side: what the rules in ZoneReset decide is done here, on the objects of the
// real world, a frame's worth at a time. GameUtils.ResetZones starts it after every "bc" change (once the alt-biome grid is
// rebuilt: the zones must generate against the new grid), "bc regen" starts it on demand, and a Debug Reset Command that is
// not empty runs that console command instead of it.
//
// It runs in single player or on the host: the "bc" commands that start it are valid only on the machine that runs the world, so a
// client cannot run them (isNetwork false and onlyServer, DebugUtils.cs:43-44; ConsoleCommand.IsValid, Terminal.cs:243-258). A client
// has none of the world's objects and never generates a zone (ZoneSystem.PokeLocalZone: SpawnMode.Client).
//
// The order: (1) one pass over every object of the world looks for what protects a zone (ZoneReset.Rules), (2) ZoneReset.MakePlan
// decides which zones are reset, nearest the players first, (3) ZoneReset.Work resets them. Each step is split over frames
// (ZoneReset.Budget) so that a world of millions of objects never freezes the game. A second request while one is running
// starts over from the world as it is then: the zone being emptied is finished first, a zone the first run emptied and did not
// finish is reset by the second whatever protects it by then, and one that has generated again is reset again, which the newer
// settings need anyway.
//
// A save in the middle of a run (ZoneRegenPatch): ZNet.SaveWorld, first, has the zone being emptied finished, the zones of the location
// groups of those emptied and waiting given their turns (the strays too, which the plan never puts before the first zone of their group
// that is reset, so a group that has lost a zone has one waiting), the destroy queue sent after each of those zones and the ground mended
// (BeforeSave); ZoneSystem.PrepareSave, last, has the zones that are emptied and not finished left out of the copy of m_generatedZones
// that is written, with their locations written as not placed (HidePendingFromSave). The world itself keeps them until the run finishes
// them. Both patches do nothing when no run has left anything. A second request drops the work of the run it stops, and the run that
// takes its place has none until its scan and its plan are over, which take frames; the zones the stopped run left waiting are left out
// of the file in that time all the same, so a save then has the stopped run's work do all of that (stoppedWork).
//
// It works with the vegetation twin guard (VegetationTwins): a reset queues a zone's objects for destruction, the clients are told
// within the frame, and a zone that generates again before that is not held back by the objects that are leaving (the guard's
// Begin leaves out what ZDOMan has queued). It runs after the alt-biome grid is rebuilt (AltBiomeControl.RequestRebuild calls
// GameUtils.ResetZones when the new grid is in place).
internal static class ZoneRegen
{
  /// <summary>Objects destroyed per frame at most. They go to every client as one message of 12 bytes each, which tops out at
  /// 512 KiB (ZDOMan.SendDestroyed), and ZDOMan takes each of them off its sector's list one by one when it hears of it.</summary>
  internal const int ObjectsPerFrame = 1500;
  /// <summary>The most a frame destroys with other players connected, however long it is.</summary>
  internal const int ObjectsPerFrameWithPlayers = 100;
  /// <summary>With other players connected the work is held to this many objects a second, by the clock rather than by the frame:
  /// each object is 12 bytes to every client, so 6000 are 72 KB/s of the 150 KB/s Valheim sends a connection (ZSteamSocket.cs:111),
  /// which leaves the rest of the game's traffic its room at any frame rate.</summary>
  internal const int ObjectsPerSecondWithPlayers = 6000;
  /// <summary>Milliseconds per frame at most, of the 16 a frame at 60 per second has: the game does its own work around this
  /// (taking each destroyed object off its lists costs about as much again as destroying it).</summary>
  internal const double MillisecondsPerFrame = 6.0;
  /// <summary>Zones that cannot be finished after an error stopped a run are told one by one, with their error, up to this many; the rest
  /// are counted.</summary>
  internal const int MaxFailuresLogged = 3;

  private const string UiKey = "ZoneRegen";

  private static readonly int PlayerPrefab = "Player".GetStableHashCode();
  private static readonly int TombstonePrefab = "Player_tombstone".GetStableHashCode();
  private static readonly int TerrainCompilerPrefab = "_TerrainCompiler".GetStableHashCode();
  // The key BakedServer writes on every piece it seeds (and an in-game bake on every piece it adopts): the id of its record.
  private static readonly int BakeIdKey = "bc_bake_id".GetStableHashCode();

  /// <summary>Whether the Debug Reset Command setting leaves the regeneration to Better Continents: when it is empty.</summary>
  internal static bool RunsOwn(string? command) => string.IsNullOrWhiteSpace(command);

  /// <summary>"bc regen": no arguments (its help is answered by the command tree itself). It removes objects, so a mistyped word
  /// must not start it.</summary>
  internal static void RunCommand(string? args, Action<string> output, Action start)
  {
    if (!string.IsNullOrWhiteSpace(args))
    {
      output($"bc regen takes no arguments ('{args!.Trim()}'). 'bc regen help' says what it does.");
      return;
    }
    start();
  }

  internal sealed class Job(Action<string> output)
  {
    public readonly Action<string> Output = output;
    public readonly ZoneReset.Progress Progress = new();
    public readonly Stopwatch Clock = Stopwatch.StartNew();
    public Coroutine? Coroutine;
    /// <summary>The work, once the plan is made: what a save or a second request finishes the zone being emptied through.</summary>
    public ZoneReset.Work? Work;
    public int Errors;
  }

  private static Job? job;

  // The work of the run that a second request stopped, kept until the run that took its place has made its own (after its scan and its
  // plan) or ends: a save before that still has the groups of the zones left waiting given their turns (BeforeSave).
  private static ZoneReset.Work? stoppedWork;

  // The calls into Unity's engine that the offline tests (tools/zone-tests) stand in for, each a field that holds what the game does:
  // the coroutines are Unity's own scheduler's, a frame's time comes from the system clock.
  internal static Func<IEnumerator, Coroutine> StartRoutine = routine => BetterContinents.instance.StartCoroutine(routine);
  internal static Action<Coroutine> StopRoutine = routine => BetterContinents.instance.StopCoroutine(routine);
  /// <summary>The clock a frame's time is taken by, in milliseconds; null is the system's.</summary>
  internal static Func<double>? FrameClock;

  // What the runs of this world leave for the next one, and the world it is for (the ZoneSystem of its session): a new world starts
  // with nothing. The owner is only a reference to compare with, so that a world that was left is not kept alive by it.
  private static ZoneReset.Memory memory = new();
  private static WeakReference? memoryOwner;

  // The zones where the player at this machine placed something since the request (Piece.SetCreator, ZoneRegenPatch).
  private static readonly HashSet<Vector2s> placedDuringRun = new(ZoneReset.Comparer);

  // Something this machine destroyed may still be in ZDOMan's queue, which the game sends at the start of the next frame.
  private static bool flushOwed;

  internal static ZoneReset.Memory MemoryFor(ZoneSystem zones)
  {
    if (!ReferenceEquals(memoryOwner?.Target, zones))
    {
      memoryOwner = new WeakReference(zones);
      memory = new ZoneReset.Memory();
    }
    return memory;
  }

  /// <summary>Starts the regeneration, or starts it over when one is running. Says what it does, and why not, to
  /// <paramref name="output"/> (default: the console and the log; a server does not run the "bc" commands for an admin).</summary>
  internal static void Request(Action<string>? output = null)
  {
    output ??= ConsoleTools.Output(Console.instance);
    if (!CanRun(out var why))
    {
      output("Zone regeneration: " + why);
      return;
    }
    if (job != null)
      Stop(job, "starting over");
    var mine = new Job(output);
    job = mine;
    // The first frame's work runs inside StartCoroutine, and a small world may be done in it.
    mine.Coroutine = StartRoutine(Run(mine));
  }

  // The game clears these singletons when it destroys them (their OnDestroy), so one that is still set is a world that is there.
  private static bool Gone(object? singleton) => singleton == null;

  private static bool CanRun(out string why)
  {
    var net = ZNet.instance;
    if (Gone(net) || Gone(ZoneSystem.instance) || Gone(ZDOMan.instance) || Gone(ZNetScene.instance))
    {
      why = "load a world first.";
      return false;
    }
    if (!net!.IsServer())
    {
      why = "it runs on the machine that runs the world: in single player, or on the host.";
      return false;
    }
    // A world without Better Continents is the game's own: Better Continents never empties its zones.
    if (!BetterContinents.Settings.EnabledForThisWorld)
    {
      why = "this world does not use Better Continents.";
      return false;
    }
    why = "";
    return true;
  }

  private static void Stop(Job running, string why)
  {
    // A zone must not be left half empty and generated: the one being emptied goes on to its end first.
    FinishInFlight(running);
    if (running.Coroutine != null)
      StopRoutine(running.Coroutine);
    UI.Remove(UiKey);
    if (job == running)
    {
      job = null;
      placedDuringRun.Clear();
    }
    // The work stays for a save that comes before the next run has made its own. (A run stopped before it made one leaves the earlier
    // run's, which is the one that knows what the zones left waiting are waiting for.)
    stoppedWork = running.Work ?? stoppedWork;
    BetterContinents.Log($"Zone regeneration: {why}.");
  }

  // An error stopped the run: the zones it had emptied and left waiting for their neighbours are finished, since nothing will reach
  // those neighbours now and a zone left bare and generated stays so for the session. The rest is as it was; 'bc regen' resets it.
  private static void FinishPending(Job running)
  {
    try
    {
      var work = running.Work ?? WorkForPending(running);
      if (work == null)
        return;
      // The first few are told with their error; a persistent failure over many zones would be one stack trace for each.
      int failed = 0;
      int finished = work.FinishPending((zone, e) =>
      {
        if (++failed <= MaxFailuresLogged)
          BetterContinents.LogError($"Zone regeneration: zone {zone.x},{zone.y} could not be finished: {e}");
      });
      if (failed > MaxFailuresLogged)
        BetterContinents.LogError(ZoneReset.UnfinishedLine(failed - MaxFailuresLogged));
      if (finished > 0)
        BetterContinents.Log(ZoneReset.PendingLine(finished));
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Zone regeneration: the zones left waiting could not be finished: {e}");
    }
  }

  // An error before the work was made (in the scan or the plan) has no Work to finish the zones with, but zones an earlier run emptied
  // and a second request stopped are still remembered as waiting: bare and generated, as the work would find them. A work over no plan
  // does nothing but finish them (and nothing when none waits). None when the world is gone.
  private static ZoneReset.Work? WorkForPending(Job running)
  {
    var zones = ZoneSystem.instance;
    if (Gone(zones))
      return null;
    return new ZoneReset.Work(new GameWorld(running), new ZoneReset.Plan(), new ZoneReset.Budget(ObjectsPerFrame, MillisecondsPerFrame, FrameClock), running.Progress, MemoryFor(zones));
  }

  private static void FinishInFlight(Job running)
  {
    try
    {
      running.Work?.FinishInFlight();
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Zone regeneration: the zone being emptied could not be finished: {e}");
    }
  }

  /// <summary>What the screen says while the work goes on: that it is preparing until the plan is made, then how far it is.</summary>
  internal static string ProgressText(ZoneReset.Progress progress) => progress.Total == 0
    ? "Better Continents: preparing to regenerate the zones ..."
    : $"Better Continents: regenerating the zones, {progress.Percent}% ...";

  // The coroutine Unity runs. Unity does not run an abandoned coroutine's finally, so a run that is stopped is cleaned up by Stop.
  private static IEnumerator Run(Job mine)
  {
    try
    {
      UI.Add(UiKey, () => UI.DisplayMessage(ProgressText(mine.Progress)));
      var steps = Steps(mine);
      while (true)
      {
        bool more;
        try
        {
          more = steps.MoveNext();
        }
        catch (Exception e)
        {
          BetterContinents.LogError($"Zone regeneration stopped: {e}");
          mine.Output("Zone regeneration stopped by an error, see the log.");
          FinishInFlight(mine);
          FinishPending(mine);
          yield break;
        }
        if (!more)
          yield break;
        yield return null;
      }
    }
    finally
    {
      UI.Remove(UiKey);
      if (job == mine)
      {
        job = null;
        placedDuringRun.Clear();
        // The run is over, ended or failed, and nothing is left for a save to finish (a run that is stopped does not come here: Stop keeps
        // its work).
        stoppedWork = null;
      }
    }
  }

  internal static IEnumerator Steps(Job mine)
  {
    var zones = ZoneSystem.instance;
    var net = ZNet.instance;
    var remembered = MemoryFor(zones);
    placedDuringRun.Clear();
    var world = new GameWorld(mine);
    var budget = new ZoneReset.Budget(ObjectsPerFrameNow, MillisecondsPerFrame, FrameClock);
    budget.NewFrame();

    var protectors = new ZoneReset.Protectors();
    var scan = Scan(world, protectors, budget);
    while (scan.MoveNext())
      yield return null;
    if (!world.Alive)
    {
      BetterContinents.Log("Zone regeneration: stopped, the world closed.");
      yield break;
    }

    // From here to the plan nothing yields: the zones it lists are the world's generated zones of one moment.
    var focus = new List<Vector2s>();
    foreach (var zdo in net.GetAllCharacterZDOS())
      focus.Add(ZoneSystem.GetZone(zdo.GetPosition()));
    focus.AddRange(Peers(net).Select(peer => ZoneSystem.GetZone(peer.GetRefPos())));
    var plan = ZoneReset.MakePlan(zones.m_generatedZones, protectors, focus.Distinct().ToList(), PlacedLocations(zones), remembered.Pending);
    mine.Progress.Total = plan.Reset.Count;
    mine.Output(ZoneReset.StartLine(plan));
    // Making the plan of a big world takes a while: the first resets wait for the next frame.
    yield return null;
    // A save in that frame has the stopped run's work give the groups of the zones left waiting their turns (BeforeSave), and a zone it
    // empties then may wait in its turn: one the plan does not carry would be dropped by the new work and left empty and generated. Plan again.
    if (world.Alive && remembered.Pending.Any(zone => !plan.Carried.Contains(zone) && zones.m_generatedZones.Contains(zone)))
    {
      plan = ZoneReset.MakePlan(zones.m_generatedZones, protectors, focus.Distinct().ToList(), PlacedLocations(zones), remembered.Pending);
      mine.Progress.Total = plan.Reset.Count;
    }

    var work = new ZoneReset.Work(world, plan, budget, mine.Progress, remembered);
    mine.Work = work;
    // The new work carries the zones left waiting (the plan has them as carried), so a save has it do what the stopped one did.
    stoppedWork = null;
    var steps = work.Steps();
    int tens = 0;
    while (steps.MoveNext())
    {
      // A line every tenth of the way, in a world with enough zones for that to mean something.
      if (mine.Progress.Total >= 20 && mine.Progress.Percent / 10 > tens && mine.Progress.Percent < 100)
      {
        tens = mine.Progress.Percent / 10;
        mine.Output(ZoneReset.ProgressLine(mine.Progress));
      }
      yield return null;
    }
    if (!world.Alive)
      mine.Progress.Aborted = true;
    if (!mine.Progress.Aborted)
      Finish();
    mine.Output(ZoneReset.FinishLine(mine.Progress, mine.Clock.Elapsed.TotalSeconds, mine.Errors));
  }

  /// <summary>The length of the last frame in real seconds. The tests have their own, since the clock is Unity's.</summary>
  internal static Func<float> DeltaTime = () => Time.unscaledDeltaTime;

  private static double frameFraction;

  internal static int ObjectsPerFrameNow()
  {
    var net = ZNet.instance;
    if (Gone(net) || !net!.GetPeers().Any(peer => peer.IsReady()))
    {
      frameFraction = 0.0;
      return ObjectsPerFrame;
    }
    return PerFrameWithPlayers(DeltaTime(), ref frameFraction);
  }

  /// <summary>How many objects a frame of <paramref name="deltaSeconds"/> destroys with other players connected: the share of
  /// ObjectsPerSecondWithPlayers that the frame is, rounded, with the fraction left over carried to the next frame, and at least
  /// one and at most ObjectsPerFrameWithPlayers.</summary>
  internal static int PerFrameWithPlayers(double deltaSeconds, ref double fraction)
  {
    // (A clock that says less than nothing, or nothing at all, is no time: the fraction carried must stay a number.)
    double seconds = deltaSeconds > 0.0 && !double.IsInfinity(deltaSeconds) ? deltaSeconds : 0.0;
    double want = ObjectsPerSecondWithPlayers * seconds + fraction;
    int whole = (int)Math.Round(want);
    fraction = want - whole;
    return Math.Max(1, Math.Min(ObjectsPerFrameWithPlayers, whole));
  }

  /// <summary>What is done when the work is done (the offline tests count it).</summary>
  internal static Action Finish = FinishTouches;

  // The game's calls that do it, as fields for the offline tests to stand in for (the minimap's update runs when its timer is over).
  internal static Action<ClutterSystem> ClearGrass = clutter => clutter.ClearAll();
  internal static Action<Minimap> RedrawLocationPins = map => map.UpdateLocationPins(1000f);

  // What the game has to redraw once zones are gone and the ground at a border has changed: the grass, which is cut from the
  // ground's old shape, and the location icons of the minimap, which follow the locations that are placed.
  internal static void FinishTouches()
  {
    var clutter = ClutterSystem.instance;
    if (clutter)
      ClearGrass(clutter);
    var map = Minimap.instance;
    if (map)
      RedrawLocationPins(map);
  }

  // ------------------------------------------------------------------------------------------------ saving in the middle of a run

  /// <summary>What ZNet.SaveWorld starts with while a run is under way, or has left something in the destroy queue: the zone being
  /// emptied is finished, the queue is sent (ZDOMan.PrepareSave copies the objects next, and a destroyed one that is still in
  /// the lists would be saved), and the ground is mended. An error here is logged and the save goes on.</summary>
  internal static void BeforeSave()
  {
    var running = job;
    if (running == null && !flushOwed)
      return;
    try
    {
      if ((running?.Work ?? WorkOfStoppedRun()) is { } work)
        work.BeforeSave();
      else
        SendQueued();
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Zone regeneration: the world could not be made ready to save: {e}");
    }
  }

  // The run that a second request started has no work yet (its scan and its plan take frames), and HidePendingFromSave leaves out the zones
  // the stopped run left waiting all the same: their location groups are given their turns by that run's work, until the new one has its own.
  // Only for the world those zones are in, and only while some are remembered as waiting. (The world terms are a second guard: the work
  // itself does nothing in a world that is gone, Work.BeforeSave.)
  private static ZoneReset.Work? WorkOfStoppedRun()
  {
    var zones = ZoneSystem.instance;
    if (stoppedWork == null || Gone(zones) || !ReferenceEquals(memoryOwner?.Target, zones) || memory.Pending.Count == 0)
      return null;
    return stoppedWork;
  }

  /// <summary>ZDOMan.SendDestroyed, which ZDOMan.Update runs every frame: the destroy queue goes to every machine, this one
  /// included at once (ZRoutedRpc.InvokeRoutedRPC handles a message for everybody here first, ZRoutedRpc.cs:120-138), and
  /// ZDOMan.HandleDestroyedZDO takes each object off its sector's list. The tests have their own, which does what the game does.</summary>
  internal static Action SendDestroyQueue = () => ZDOMan.instance.SendDestroyed();

  internal static void SendQueued()
  {
    SendDestroyQueue();
    flushOwed = false;
  }

  /// <summary>What ZoneSystem.PrepareSave has just copied for the save, with the zones that were emptied and not finished taken
  /// out: they are saved as not generated, with their location not placed, so that they fill again when the world loads. The
  /// live world is left as it is (the run finishes them).</summary>
  internal static void HidePendingFromSave(ZoneSystem zones)
  {
    if (!ReferenceEquals(memoryOwner?.Target, zones) || memory.Pending.Count == 0)
      return;
    try
    {
      var generated = zones.m_tempGeneratedZonesSaveClone;
      var locations = zones.m_tempLocationsSaveClone;
      foreach (var zone in memory.Pending)
        generated.Remove(zone);
      for (int i = 0; i < locations.Count; i++)
      {
        var instance = locations[i];
        if (instance.m_placed && memory.Pending.Contains(ZoneSystem.GetZone(instance.m_position)))
        {
          instance.m_placed = false;
          locations[i] = instance;
        }
      }
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Zone regeneration: the zones emptied so far could not be left out of the save: {e}");
    }
  }

  // Where a piece is: its transform's, which needs Unity (a field for the offline tests to stand in for).
  internal static Func<Piece, Vector3> PiecePosition = piece => piece.transform.position;

  /// <summary>Piece.SetCreator, after: the player at this machine placed something here. The zones around it are not reset
  /// while the run goes on. Nothing here may stop the piece from being placed, so an error is logged and goes no further.</summary>
  internal static void NotePlaced(Piece piece)
  {
    if (job == null)
      return;
    try
    {
      NotePlaced(PiecePosition(piece));
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Zone regeneration: a piece placed could not be noted: {e}");
    }
  }

  internal static void NotePlaced(Vector3 position)
  {
    if (job != null)
      placedDuringRun.Add(ZoneSystem.GetZone(position));
  }

  // ------------------------------------------------------------------------------------------------ what the objects are

  /// <summary>What the rules need to know about an object of the world; <paramref name="local"/> is the character of the
  /// player at this machine (none on a dedicated server).</summary>
  internal static ZoneReset.Kind KindOf(ZDO zdo, ZDOID local)
  {
    var kind = ZoneReset.Kind.None;
    int prefab = zdo.GetPrefab();
    if (prefab == PlayerPrefab)
    {
      kind |= ZoneReset.Kind.Player;
      if (zdo.m_uid == local)
        kind |= ZoneReset.Kind.Local;
      // Character.InInterior's rule, which a world of high terrain changes (HighTerrain.Interior: above 3000 m AND well above the
      // ground below); anywhere else it is "above 3000 m" (ZoneReset.Rules.InteriorHeight).
      if (HighTerrain.Interior(zdo.GetPosition()))
        kind |= ZoneReset.Kind.Interior;
    }
    else if (prefab == TombstonePrefab)
    {
      kind |= ZoneReset.Kind.Tombstone;
    }
    else if (prefab == TerrainCompilerPrefab)
    {
      // Only compilers are unpacked: a hoed field or a road is in their data, and nowhere else.
      if (ZoneReset.TerrainBorder.HasEdits(zdo.GetByteArray(ZDOVars.s_TCData)))
        kind |= ZoneReset.Kind.Ground;
    }
    if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L)
      kind |= ZoneReset.Kind.Piece;
    if (zdo.GetBool(ZDOVars.s_tamed))
      kind |= ZoneReset.Kind.Tamed;
    // A piece of a baked layer, whatever its id (a u32 that may be 0): the key being there is what says so.
    if (zdo.GetInt(BakeIdKey, out _))
      kind |= ZoneReset.Kind.Baked;
    return kind;
  }

  /// <summary>The objects of the world whose position is in the zone, found the way a zone's objects are found everywhere here (the
  /// reset, BakedServer's seeding, BakedReconcile): from the zone's sector list and its portals, each by its own position. A zone's
  /// sector list also holds what the game files under it for want of a sector: with the game's own sectors, everything beyond 256 zones
  /// out (ZoneSystem.SectorToIndex files all of that under sector 0, which is also zone (-256, -256)); with WorldSectors', what is
  /// beyond 1024 zones. So each position is looked at.</summary>
  internal static List<ZDO> ZdosIn(ZDOMan man, Vector2s zone)
  {
    var result = new List<ZDO>();
    var index = ZoneSystem.SectorToIndex(zone);
    var list = man.m_objectsBySector[index.Sector];
    if (list != null)
      for (int i = 0; i < list.Count; i++)
      {
        var zdo = list[i];
        if (zdo != null && zdo.IsValid() && ZoneSystem.GetZone(zdo.GetPosition()) == zone)
          result.Add(zdo);
      }
    if (man.m_portalObjects.TryGetValue(index, out var portals))
      for (int i = 0; i < portals.Count; i++)
      {
        var zdo = portals[i];
        if (zdo != null && zdo.IsValid() && ZoneSystem.GetZone(zdo.GetPosition()) == zone)
          result.Add(zdo);
      }
    return result;
  }

  /// <summary>How many zones around each player connected from another machine are kept: the near simulation distance of their
  /// game, and never less than a piece's. A player's character is the peer's by its owner.</summary>
  internal sealed class PeerRadii
  {
    private readonly Dictionary<long, int> byPeer = [];
    private readonly int fallback;

    internal PeerRadii(ZNet net)
    {
      var near = new List<int>();
      foreach (var peer in net.GetPeers())
        if (peer.IsReady())
        {
          int distance = peer.m_simulationDistance.NearSimulationDistance;
          byPeer[peer.m_uid] = ZoneReset.Rules.PeerRadius(distance);
          near.Add(distance);
        }
      fallback = ZoneReset.Rules.FallbackRadius(near);
    }

    internal int Of(ZNetPeer peer) => byPeer.TryGetValue(peer.m_uid, out var radius) ? radius : fallback;

    internal int For(ZDO player) => byPeer.TryGetValue(player.GetOwner(), out var radius) ? radius : fallback;
  }

  // One pass over every object of the world, for the zones the rules protect. By sector, index by index, because the lists
  // change between frames; a piece placed while it runs is caught when its zone's turn comes (ZoneReset.Work).
  private static IEnumerator Scan(GameWorld world, ZoneReset.Protectors protectors, ZoneReset.Budget budget)
  {
    var man = ZDOMan.instance;
    var net = ZNet.instance;
    var local = net.LocalPlayerCharacterID;
    var radii = new PeerRadii(net);
    var sectors = man.m_objectsBySector;
    for (int s = 0; s < sectors.Length; s++)
    {
      var list = sectors[s];
      if (list != null)
        for (int i = 0; i < list.Count; i++)
          Consider(list[i], local, radii, protectors);
      if (budget.Spent)
      {
        yield return null;
        budget.NewFrame();
        if (!world.Alive)
          yield break;
      }
    }
    // Portals are kept apart from the sectors (ZDOMan.AddIfPortal).
    foreach (var portal in man.GetPortalList())
      Consider(portal, local, radii, protectors);
    // A player who has joined but whose character is not in the world yet: where their client says they are.
    if (ZoneReset.Rules.Protects(ZoneReset.Kind.Player))
      foreach (var peer in Peers(net))
        protectors.Add(ZoneSystem.GetZone(peer.GetRefPos()), radii.Of(peer));
  }

  // The players connected whose client says where they are. A player who has not picked a character yet reports nowhere
  // (0, 0, 0), which is not a place they are at.
  private static IEnumerable<ZNetPeer> Peers(ZNet net)
  {
    foreach (var peer in net.GetPeers())
      if (peer.IsReady() && (!peer.m_characterID.IsNone() || peer.GetRefPos() != Vector3.zero))
        yield return peer;
  }

  private static void Consider(ZDO? zdo, ZDOID local, PeerRadii radii, ZoneReset.Protectors protectors)
  {
    if (zdo == null || !zdo.IsValid())
      return;
    int prefab = zdo.GetPrefab();
    // Most objects are none of these: one lookup finds that out.
    if (prefab != PlayerPrefab && prefab != TombstonePrefab && prefab != TerrainCompilerPrefab && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
      return;
    var kind = KindOf(zdo, local);
    if (!ZoneReset.Rules.Protects(kind))
      return;
    protectors.Add(ZoneSystem.GetZone(zdo.GetPosition()), (kind & ZoneReset.Kind.Player) != 0 ? radii.For(zdo) : ZoneReset.Rules.ProtectRadius);
  }

  /// <summary>The locations the game has placed, for the plan: a placed location's objects stand around its position, as far as
  /// its exterior radius (a dungeon's interior is built inside one zone, so its radius reaches no other: DungeonGenerator.cs:211-217,
  /// :967-1010), and the pins of a Better Continents map are not kept inside their zone the way the game keeps its own.</summary>
  internal static IEnumerable<ZoneReset.PlacedLocation> PlacedLocations(ZoneSystem zones)
  {
    foreach (var pair in zones.m_locationInstances)
    {
      var instance = pair.Value;
      if (!instance.m_placed)
        continue;
      var location = instance.m_location;
      float radius = location == null ? 0f : location.m_exteriorRadius;
      yield return new ZoneReset.PlacedLocation(pair.Key, instance.m_position.x, instance.m_position.z, radius);
    }
  }

  // ------------------------------------------------------------------------------------------------ the world

  // The game's own calls that need Unity's engine, for the offline tests to stand in for: the loaded object of a ZDO and how it is
  // destroyed (ZNetScene.Destroy hands an owned one to ZDOMan), a loaded zone's root, and the ground's height from the world generator.
  internal static Func<ZDO, ZNetView?> FindView = zdo => ZNetScene.instance.FindInstance(zdo);
  internal static Action<ZNetView> DestroyView = view => ZNetScene.instance.Destroy(view.gameObject);
  internal static Action<GameObject> DestroyObject = root => UnityEngine.Object.Destroy(root);
  internal static Func<float, float, float?> GroundHeight = (x, z) => WorldGenerator.instance?.GetHeight(x, z);

  internal sealed class GameWorld(Job mine) : ZoneReset.IZoneWorld
  {
    private readonly ZDOMan man = ZDOMan.instance;

    // Objects this run has queued for destruction in this frame. ZDOMan sends the queue every frame, in ZNet.Update, before a
    // coroutine continues; until then an object is still in its lists and can be asked for again (a spawner and its creature,
    // listed in two zones), which must not queue it twice.
    private readonly HashSet<ZDOID> queued = [];

    public void NewFrame() => queued.Clear();

    public void Flush()
    {
      SendQueued();
      queued.Clear();
    }

    public bool Alive => ZDOMan.instance == man && !Gone(ZNet.instance) && !Gone(ZoneSystem.instance) && !Gone(ZNetScene.instance);

    public bool IsGenerated(Vector2s zone) => ZoneSystem.instance.m_generatedZones.Contains(zone);

    // The connected players' characters that protect (a remote player, or the local one in a dungeon), and where each player's
    // client says they are, each with the zones their game keeps; and where this machine's player has placed something.
    public IEnumerable<ZoneReset.Protector> LiveProtectors()
    {
      var result = new List<ZoneReset.Protector>();
      if (!Alive)
        return result;
      var net = ZNet.instance;
      var radii = new PeerRadii(net);
      var local = net.LocalPlayerCharacterID;
      foreach (var zdo in net.GetAllCharacterZDOS())
        if (zdo.GetPrefab() == PlayerPrefab && ZoneReset.Rules.Protects(KindOf(zdo, local)))
          result.Add(new ZoneReset.Protector(ZoneSystem.GetZone(zdo.GetPosition()), radii.For(zdo)));
      if (ZoneReset.Rules.Protects(ZoneReset.Kind.Player))
        foreach (var peer in Peers(net))
          result.Add(new ZoneReset.Protector(ZoneSystem.GetZone(peer.GetRefPos()), radii.Of(peer)));
      foreach (var zone in placedDuringRun)
        result.Add(new ZoneReset.Protector(zone, ZoneReset.Rules.ProtectRadius));
      return result;
    }

    // The objects whose position is in the zone (ZoneRegen.ZdosIn).
    private List<ZDO> ZdosIn(Vector2s zone) => ZoneRegen.ZdosIn(man, zone);

    public List<ZoneReset.Obj> ObjectsIn(Vector2s zone)
    {
      var local = ZNet.instance.LocalPlayerCharacterID;
      var zdos = ZdosIn(zone);
      var result = new List<ZoneReset.Obj>(zdos.Count);
      foreach (var zdo in zdos)
        result.Add(new ZoneReset.Obj(zdo.m_uid, zdo.GetPrefab(), KindOf(zdo, local), zone));
      return result;
    }

    public ZoneReset.Obj? Find(ZDOID id)
    {
      var zdo = man.GetZDO(id);
      if (zdo == null || !zdo.IsValid())
        return null;
      return new ZoneReset.Obj(zdo.m_uid, zdo.GetPrefab(), KindOf(zdo, ZNet.instance.LocalPlayerCharacterID), ZoneSystem.GetZone(zdo.GetPosition()));
    }

    public bool Destroy(ZoneReset.Obj obj, out ZDOID spawned)
    {
      spawned = default;
      try
      {
        var zdo = man.GetZDO(obj.Id);
        // ZDOs are pooled: look it up again, and make sure it is still the same kind of object, and still not one to keep.
        if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != obj.Prefab)
          return false;
        if (ZoneReset.Rules.Survives(KindOf(zdo, ZNet.instance.LocalPlayerCharacterID)))
          return false;
        if (!queued.Add(zdo.m_uid))
          return false;
        // A spawner's creature as it is now: it may have made one since the spawner was listed, and a destroyed object loses its links.
        spawned = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
        // Only the owner can destroy an object; this machine takes it over first, as the game does.
        zdo.SetOwner(ZDOMan.GetSessionID());
        var view = FindView(zdo);
        if (view)
          DestroyView(view);
        else
          man.DestroyZDO(zdo);
        flushOwed = true;
        return true;
      }
      catch (Exception e)
      {
        if (++mine.Errors <= 3)
          BetterContinents.LogError($"Zone regeneration: an object could not be destroyed: {e}");
        return false;
      }
    }

    public void Unplace(Vector2s zone)
    {
      var zones = ZoneSystem.instance;
      if (!zones.m_locationInstances.TryGetValue(zone, out var location))
        return;
      location.m_placed = false;
      // The ground may have moved; the game puts the location on the ground again when it places it.
      if (GroundHeight(location.m_position.x, location.m_position.z) is { } height)
        location.m_position = new Vector3(location.m_position.x, height, location.m_position.z);
      zones.m_locationInstances[zone] = location;
    }

    public void Ungenerate(Vector2s zone) => ZoneSystem.instance.m_generatedZones.Remove(zone);

    public void DestroyRoot(Vector2s zone)
    {
      var zones = ZoneSystem.instance;
      if (!zones.m_zones.TryGetValue(zone, out var data))
        return;
      Retire(data.m_root);
      if (data.m_root)
        DestroyObject(data.m_root);
      zones.m_zones.Remove(zone);
    }

    // Object.Destroy waits for the end of the frame, and until then the dying zone is still what Heightmap.FindHeightmap answers with
    // (Heightmap.OnDestroy takes it off the list, Heightmap.cs:220-225) and what WaterVolume.Instances holds, and its collider is what
    // a ray hits: a zone that generates again in this frame would read the old ground. So its heightmaps leave the list now, and the
    // root is switched off, which takes its collider and its water (OnDisable, WaterVolume.cs:95-98) out of the game's reach as well.
    // That is for the rest of this frame only: if it cannot be done the root is destroyed all the same.
    internal void Retire(GameObject root)
    {
      if (!root)
        return;
      try
      {
        RetireHeightmaps(root.GetComponentsInChildren<Heightmap>(true));
        root.SetActive(false);
      }
      catch (Exception e)
      {
        if (++mine.Errors <= 3)
          BetterContinents.LogError($"Zone regeneration: a loaded zone could not be switched off before it was destroyed: {e.GetType().Name}: {e.Message}");
      }
    }

    internal static void RetireHeightmaps(IEnumerable<Heightmap> heightmaps)
    {
      foreach (var heightmap in heightmaps)
        Heightmap.s_heightmaps.Remove(heightmap);
    }

    public int AdjustBorder(Vector2s zone, ZoneReset.Edges edges)
    {
      int cleared = 0;
      try
      {
        foreach (var zdo in ZdosIn(zone))
        {
          if (zdo.GetPrefab() != TerrainCompilerPrefab)
            continue;
          var data = zdo.GetByteArray(ZDOVars.s_TCData);
          if (data == null)
            continue;
          var edited = ZoneReset.TerrainBorder.Clear(data, edges, out var vertices);
          if (edited == null)
            continue;
          zdo.SetOwner(ZDOMan.GetSessionID());
          zdo.Set(ZDOVars.s_TCData, edited);
          cleared += vertices;
        }
      }
      catch (Exception e)
      {
        if (++mine.Errors <= 3)
          BetterContinents.LogError($"Zone regeneration: the ground at the edge of zone {zone} could not be adjusted: {e}");
      }
      return cleared;
    }
  }
}

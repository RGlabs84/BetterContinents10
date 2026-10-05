// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// Offline checks of zone regeneration (ZoneReset.cs, ZoneRegen.cs): the rules for what protects a zone and what is never
// destroyed, which zones a plan resets and in what order, the work against a made-up world (what is destroyed, in what order,
// how it is spread over frames, what happens when the world goes or something is placed meanwhile), the ground at a border
// against TerrainComp's own data format, the Debug Reset Command migration, the "bc regen" wiring, the game members and
// code the regeneration relies on (read from the installed game's IL), and one whole regeneration of a made-up world
// built from the game's own ZDO, ZDOMan, ZNet and ZoneSystem classes. Program.Fixes.cs has the checks of what the review round
// asked for: ground work, locations kept whole, saves and stops in the middle, who is near again every frame, the borders, the
// peers' radius, the frame and bandwidth limits, the spawner's creature, the words and the patches. Program.Harden.cs has what a
// mutation run of the production code found the others let through: the calls that need Unity (stood in for through the seams of
// ZoneRegen and GameUtils), requests, stops and errors, a world that closes in every phase, and the corners of the work.
// Program.Review.cs has what the last review asked for: strays after their group, a save's turns taken once, strays the game
// generated, the drain flushed zone by zone, and an error before the work.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Budget = BetterContinents.ZoneReset.Budget;
using Edges = BetterContinents.ZoneReset.Edges;
using Kind = BetterContinents.ZoneReset.Kind;
using Plan = BetterContinents.ZoneReset.Plan;
using Progress = BetterContinents.ZoneReset.Progress;
using Rules = BetterContinents.ZoneReset.Rules;

// Unity's Debug.Log ends in a native call; keep the game's log here instead. Lines are kept as "[Log] text".
internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];

  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines)
      Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }

  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines)
      Lines.Add("[Exception] " + exception.GetType().Name + ": " + exception.Message);
  }

  // The lines logged while an action ran.
  public static List<string> During(Action action)
  {
    int start;
    lock (Lines)
      start = Lines.Count;
    action();
    lock (Lines)
      return [.. Lines.Skip(start)];
  }
}

internal static partial class Program
{
  private static int checks, failures;
  private static string Work = "";

  private static void C(bool ok, string what)
  {
    checks++;
    if (ok)
      return;
    failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  private static void Section(string title) => System.Console.WriteLine("== " + title);

  private static int Main()
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the
    // same name and the API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    var libs = Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools");
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(libs, "1.0", "client"), libs, Path.Combine(libs, "BepInEx", "core"), Path.Combine(libs, "1.0", "server") };
    AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
      // The game's Steam wrapper is the file steamworks.net.dll and the assembly com.rlabrecque.steamworks.net.
      var file = name.Name == "com.rlabrecque.steamworks.net" ? "steamworks.net" : name.Name;
      foreach (var dir in dirs)
      {
        var path = Path.Combine(dir, file + ".dll");
        if (File.Exists(path))
          return context.LoadFromAssemblyPath(Path.GetFullPath(path));
      }
      return null;
    };
    return RunAll();
  }

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    // What the production code's seams hold before any test stands in for them.
    Defaults = new Seams();
    Work = Path.Combine(Path.GetTempPath(), "bc-zone-tests-" + Environment.ProcessId);
    Directory.CreateDirectory(Work);
    try
    {
      RulesTests();
      PlanTests();
      ExecuteTests();
      BudgetTests();
      MessageTests();
      GroundTests();
      MigrationTests();
      WiringTests();
      GameFactTests();
      KindTests();
      WorldTests();
      FinalTests();
      FixesTests();
      HardenTests();
      ReviewTests();
    }
    finally
    {
      try
      {
        Directory.Delete(Work, true);
      }
      catch (IOException)
      {
      }
    }
    System.Console.WriteLine($"zone-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------------------------------------ helpers

  private static Vector2s Z(int x, int y) => new(x, y);
  private static ZDOID Id(uint n) => new(1000L, n);
  private static ZoneReset.Obj O(uint n, Kind kind = Kind.None, int prefab = 100) => new(Id(n), prefab, kind);
  private static string Show(IEnumerable<Vector2s> zones) => string.Join(" ", zones.Select(z => $"{z.x},{z.y}"));

  // Every zone of the square of the given radius around a centre.
  private static List<Vector2s> Square(int radius, int cx = 0, int cy = 0)
  {
    var zones = new List<Vector2s>();
    for (int x = cx - radius; x <= cx + radius; x++)
      for (int y = cy - radius; y <= cy + radius; y++)
        zones.Add(Z(x, y));
    return zones;
  }

  // ------------------------------------------------------------------------------------------------ the rules

  private static void RulesTests()
  {
    Section("rules: what protects a zone, what is never destroyed");
    var all = Enum.GetValues<Kind>().Where(k => k != Kind.None).ToArray();
    C(!Rules.Protects(Kind.None) && !Rules.Survives(Kind.None), "an ordinary object protects nothing and does not survive");
    C(Rules.Protects(Kind.Piece) && Rules.Survives(Kind.Piece), "what a player placed protects its zones and is never destroyed");
    C(Rules.Protects(Kind.Tombstone) && Rules.Survives(Kind.Tombstone), "a tombstone protects its zones and is never destroyed");
    C(Rules.Protects(Kind.Player) && Rules.Survives(Kind.Player), "a player connected from another machine protects its zones and is never destroyed");
    C(!Rules.Protects(Kind.Player | Kind.Local) && Rules.Survives(Kind.Player | Kind.Local),
      "the player at this machine on the ground protects nothing (debug mode is about their surroundings) but is never destroyed");
    C(Rules.Protects(Kind.Player | Kind.Local | Kind.Interior) && Rules.Survives(Kind.Player | Kind.Local | Kind.Interior),
      "the player at this machine in a dungeon protects its zones");
    C(Rules.Protects(Kind.Player | Kind.Interior), "a player from another machine in a dungeon protects its zones");
    C(!Rules.Protects(Kind.Tamed) && Rules.Survives(Kind.Tamed), "a tamed creature is never destroyed but protects nothing");
    C(!Rules.Protects(Kind.Interior) && !Rules.Survives(Kind.Interior), "height alone means nothing for an object that is not a player");
    // Anything that protects also survives, so a protected zone's objects are never half-kept by a rule that disagrees.
    C(all.All(k => !Rules.Protects(k) || Rules.Survives(k)), "everything that protects is also never destroyed");
    C(Rules.ProtectRadius == 1, "a zone within one zone (3 x 3) of a protector is left alone");
    C(Rules.Protects(Kind.Ground) && Rules.Survives(Kind.Ground), "ground a player worked protects its zones like a piece and is never destroyed");
    // Combined kinds, stated as what the rules say about the player's four cases and about everything else, and as relations that
    // hold between all 128 combinations (the rules are not written out a second time here).
    var playerCases = new (Kind Flags, bool Protects, string Name)[]
    {
      (Kind.Player, true, "another machine's player on the ground"),
      (Kind.Player | Kind.Interior, true, "another machine's player in a dungeon"),
      (Kind.Player | Kind.Local, false, "the player at this machine on the ground"),
      (Kind.Player | Kind.Local | Kind.Interior, true, "the player at this machine in a dungeon"),
    };
    foreach (var (flags, protects, name) in playerCases)
    {
      C(Rules.Survives(flags) && Rules.Survives(flags | Kind.Tamed), $"{name} is never destroyed");
      C(Rules.Protects(flags) == protects && Rules.Protects(flags | Kind.Tamed) == protects, $"{name} {(protects ? "protects" : "protects nothing")}, tamed or not");
      C(new[] { Kind.Piece, Kind.Tombstone, Kind.Ground }.All(strong => Rules.Protects(flags | strong) && Rules.Survives(flags | strong)), $"{name} with something a player made, a tombstone or worked ground in the same object: it protects");
    }
    foreach (var strong in new[] { Kind.Piece, Kind.Tombstone, Kind.Ground })
      foreach (var company in new[] { Kind.None, Kind.Tamed, Kind.Local, Kind.Interior, Kind.Local | Kind.Interior, Kind.Tamed | Kind.Local | Kind.Interior })
        C(Rules.Protects(strong | company) && Rules.Survives(strong | company), $"{strong} with {company} protects and survives");
    foreach (var nothing in new[] { Kind.None, Kind.Local, Kind.Interior, Kind.Local | Kind.Interior })
      C(!Rules.Protects(nothing) && !Rules.Survives(nothing), $"{nothing} on its own, without a player, is no reason to keep anything");
    C(Rules.Survives(Kind.Tamed | Kind.Local | Kind.Interior) && !Rules.Protects(Kind.Tamed | Kind.Local | Kind.Interior), "a tamed creature survives and protects nothing, at any height");
    var every = Enumerable.Range(0, 128).Select(bits => (Kind)bits).ToList();
    C(every.All(k => !Rules.Protects(k) || Rules.Survives(k)), "of all 128 combinations, whatever protects also survives");
    C(every.All(k => Rules.Protects(k) == Rules.Protects(k | Kind.Tamed)), "a tamed creature protects what it did not already protect: never more");
    C(every.Where(k => (k & Kind.Player) == 0).All(k => Rules.Protects(k) == Rules.Protects(k | Kind.Local) && Rules.Protects(k) == Rules.Protects(k | Kind.Interior)
      && Rules.Survives(k) == Rules.Survives(k | Kind.Local) && Rules.Survives(k) == Rules.Survives(k | Kind.Interior)), "without a player, Local and Interior change nothing");
    C(every.Where(k => !Rules.Survives(k)).All(k => (k & ~(Kind.Local | Kind.Interior)) == 0), "only a kind made of Local and Interior alone is destroyed like any object");
  }

  // ------------------------------------------------------------------------------------------------ the plan

  // The sides of the zones that stay which meet the zones a plan resets, were all of them cleared.
  private static Dictionary<Vector2s, Edges> Borders(Plan plan) => ZoneReset.BordersOf(plan.Reset, plan.Kept);

  private static void PlanTests()
  {
    Section("plan: which zones are reset, in what order, and which are left alone");
    // Nothing protects: every generated zone, the nearest to the player first, ties by x then y.
    var plan = ZoneReset.MakePlan(Square(2), [], [Z(0, 0)]);
    C(plan.Reset.Count == 25 && plan.Kept.Count == 0 && Borders(plan).Count == 0 && plan.Generated == 25, "no protector: all 25 generated zones are reset");
    C(Show(plan.Reset.Take(6)) == "0,0 -1,0 0,-1 0,1 1,0 -1,-1", "the player's own zone first, then the rest nearest first, ties by x and y");
    C(plan.Reset.Distinct().Count() == 25, "every zone once");

    // A piece: the zone and the 8 around it.
    var big = Square(10);
    plan = ZoneReset.MakePlan(big, [Z(3, 4)], [Z(0, 0)]);
    var covered = Square(1, 3, 4);
    C(plan.Kept.SetEquals(covered) && plan.Reset.Count == 441 - 9, "a protector keeps its zone and the 8 around it, nothing else");
    C(!plan.Reset.Intersect(covered).Any(), "no kept zone is in the list to reset");

    // Overlapping protectors, two zones apart: the union.
    plan = ZoneReset.MakePlan(big, [Z(0, 0), Z(2, 0)], []);
    C(plan.Kept.Count == 15 && plan.Kept.Contains(Z(3, 1)) && !plan.Kept.Contains(Z(4, 0)), "protectors two zones apart keep the 5 x 3 between and around them");

    // The protector's own zone need not be generated: its neighbours still are kept. Zones not generated are not listed.
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(5, 5)], [Z(2, 0)], [Z(0, 0)]);
    C(Show(plan.Kept) == "1,0" && Show(plan.Reset) == "0,0 5,5", "a protector in a zone that is not generated still keeps the generated zone next to it");
    C(plan.Generated == 3, "only generated zones are counted");

    // Negative ids (the world is centred on 0).
    plan = ZoneReset.MakePlan(Square(3), [Z(-1, -1)], [Z(0, 0)]);
    C(plan.Kept.SetEquals(Square(1, -1, -1)), "negative zone ids are kept as well as positive ones");

    // Where the world's edge cuts the protected block, only generated zones count.
    plan = ZoneReset.MakePlan(Square(1), [Z(1, 1)], [Z(0, 0)]);
    C(plan.Kept.Count == 4 && plan.Reset.Count == 5, "a block cut by the edge of the generated zones keeps only what is generated");

    // A duplicate in the generated list is one zone.
    plan = ZoneReset.MakePlan([Z(1, 1), Z(1, 1), Z(2, 2)], [], []);
    C(plan.Reset.Count == 2, "a zone listed twice is reset once");

    // The order follows the nearest of several people; with nobody, the world's centre.
    plan = ZoneReset.MakePlan(Square(6), [], [Z(-5, -5), Z(5, 5)]);
    C(plan.Reset[0].Equals(Z(-5, -5)) && plan.Reset[1].Equals(Z(5, 5)), "with two people the first zones are theirs, in x order");
    var near = plan.Reset.Take(2 + 8).ToList();
    C(near.All(z => Math.Min(Math.Abs(z.x + 5) + Math.Abs(z.y + 5), Math.Abs(z.x - 5) + Math.Abs(z.y - 5)) <= 2), "then the zones around them, not the world's centre");
    plan = ZoneReset.MakePlan(Square(3), [], []);
    C(plan.Reset[0].Equals(Z(0, 0)), "with nobody in the world the centre is done first");

    // Borders: a kept block in a world of reset zones. The side of each kept zone that meets a reset zone.
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    C(plan.Kept.Count == 9 && plan.Reset.Count == 16, "a 3 x 3 block in a 5 x 5 world");
    C(!Borders(plan).ContainsKey(Z(0, 0)), "the middle of the block meets no reset zone");
    C(Borders(plan)[Z(1, 0)] == (Edges.East | Edges.NorthEast | Edges.SouthEast), "the block's east side meets reset zones to the east, north east and south east");
    C(Borders(plan)[Z(0, 1)] == (Edges.North | Edges.NorthEast | Edges.NorthWest), "the north side: north, north east, north west");
    C(Borders(plan)[Z(-1, 0)] == (Edges.West | Edges.NorthWest | Edges.SouthWest), "the west side");
    C(Borders(plan)[Z(0, -1)] == (Edges.South | Edges.SouthEast | Edges.SouthWest), "the south side");
    C(Borders(plan)[Z(1, 1)] == (Edges.East | Edges.NorthEast | Edges.North | Edges.SouthEast | Edges.NorthWest), "the north east corner of the block meets five reset zones");
    C(Borders(plan)[Z(-1, -1)] == (Edges.West | Edges.SouthWest | Edges.South | Edges.NorthWest | Edges.SouthEast), "the south west corner meets five as well");
    C(Borders(plan).Count == 8, "eight kept zones touch a reset zone");

    // A block next to a hole in the generated zones: no reset zone there, so no border to mend.
    plan = ZoneReset.MakePlan(Square(1).Where(z => z.x != 1), [Z(0, 0)], []);
    C(plan.Reset.Count == 0 && Borders(plan).Count == 0, "kept zones next to ungenerated ones have no border");
    // Two blocks, a reset zone between them: both meet it.
    plan = ZoneReset.MakePlan(new[] { Z(0, 0), Z(1, 0), Z(2, 0) }, [Z(0, 0), Z(2, 0)], [Z(1, 0)]);
    C(plan.Kept.Count == 3 && plan.Reset.Count == 0, "protectors two zones apart leave no zone between them");
    plan = ZoneReset.MakePlan(new[] { Z(0, 0), Z(1, 0), Z(2, 0), Z(3, 0), Z(4, 0) }, [Z(0, 0), Z(4, 0)], [Z(2, 0)]);
    C(Show(plan.Reset) == "2,0" && Borders(plan)[Z(1, 0)] == Edges.East && Borders(plan)[Z(3, 0)] == Edges.West, "a single reset zone between two blocks: each meets it on its own side");

    // A world's worth: 100,000 zones, 2,000 protectors.
    var rng = new System.Random(3);
    var huge = Square(158);
    var protectors = Enumerable.Range(0, 2000).Select(_ => Z(rng.Next(-158, 159), rng.Next(-158, 159))).ToList();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    plan = ZoneReset.MakePlan(huge, protectors.Select(p => (ZoneReset.Protector)p), [Z(0, 0), Z(40, -40)]);
    watch.Stop();
    var expectedKept = ZoneReset.Dilate(protectors, 1).Intersect(huge).Count();
    C(plan.Kept.Count == expectedKept && plan.Reset.Count == huge.Count - expectedKept, $"a world of {huge.Count:N0} zones with 2,000 protectors: {plan.Kept.Count:N0} kept ({watch.ElapsedMilliseconds} ms)");
    C(plan.Reset.Zip(plan.Reset.Skip(1), (a, b) => (a, b)).All(p => Dist(p.a) <= Dist(p.b)), "the big plan is in order of distance");
    long Dist(Vector2s z) => Math.Min((long)z.x * z.x + (long)z.y * z.y, (long)(z.x - 40) * (z.x - 40) + (long)(z.y + 40) * (z.y + 40));
  }

  // ------------------------------------------------------------------------------------------------ a made-up world

  private sealed class FakeWorld : ZoneReset.IZoneWorld
  {
    public readonly Dictionary<Vector2s, List<ZoneReset.Obj>> Objects = [];
    public readonly List<string> Calls = [];
    // What was destroyed: queued for the clients, as the game's list is. A frame later it is Flushed: told to every machine, out of
    // the world's lists and out of a save.
    public readonly HashSet<ZDOID> Destroyed = [];
    public readonly HashSet<ZDOID> Flushed = [];
    public readonly Dictionary<Vector2s, int> BorderVertices = [];
    public readonly List<(Vector2s Zone, Edges Edges)> BorderCalls = [];
    // The zones the game has generated: the tests that care fill it, and Ungenerate takes from it.
    public readonly HashSet<Vector2s> Generated = [];
    // The creature each spawner holds now.
    public readonly Dictionary<ZDOID, ZDOID> Links = [];
    // What protects, as the world says now.
    public readonly List<ZoneReset.Protector> Live = [];
    public Func<bool> IsAlive = () => true;
    public Action<Vector2s>? OnList;
    public Action<Vector2s>? OnUngenerate;
    public Action<ZoneReset.Obj>? OnDestroy;
    public Action? OnNewFrame;
    public int Frame;
    public readonly Dictionary<int, int> DestroysByFrame = [];
    public bool Alive => IsAlive();

    public FakeWorld Add(Vector2s zone, params ZoneReset.Obj[] objects)
    {
      if (!Objects.TryGetValue(zone, out var list))
        Objects[zone] = list = [];
      // The object knows the zone it is in.
      list.AddRange(objects.Select(o => new ZoneReset.Obj(o.Id, o.Prefab, o.Kind, zone)));
      return this;
    }

    public FakeWorld Spawns(uint spawner, uint creature)
    {
      Links[Id(spawner)] = Id(creature);
      return this;
    }

    public int Frames;

    public void NewFrame()
    {
      Frames++;
      Flush();
      OnNewFrame?.Invoke();
    }

    public int Flushes;
    // Called as the queue is sent, before it is: what a test measures a message by.
    public Action? OnFlush;

    public void Flush()
    {
      Flushes++;
      OnFlush?.Invoke();
      Flushed.UnionWith(Destroyed);
    }

    public IEnumerable<ZoneReset.Protector> LiveProtectors() => Live.ToList();

    public bool IsGenerated(Vector2s zone) => Generated.Contains(zone);

    public ZoneReset.Obj? Find(ZDOID id)
    {
      foreach (var list in Objects.Values)
        foreach (var o in list)
          if (o.Id.Equals(id) && !Destroyed.Contains(id))
            return o;
      return null;
    }

    public FakeWorld Fill(IEnumerable<Vector2s> zones, int count, ref uint next)
    {
      foreach (var zone in zones)
        for (int i = 0; i < count; i++)
          Add(zone, O(next++));
      return this;
    }

    public List<ZoneReset.Obj> ObjectsIn(Vector2s zone)
    {
      Calls.Add($"list {zone.x},{zone.y}");
      OnList?.Invoke(zone);
      return Objects.TryGetValue(zone, out var list) ? list.Where(o => !Destroyed.Contains(o.Id)).ToList() : [];
    }

    public bool Destroy(ZoneReset.Obj obj, out ZDOID spawned)
    {
      spawned = default;
      if (!Destroyed.Add(obj.Id))
        return false;
      Calls.Add($"destroy {obj.Id.ID}");
      DestroysByFrame[Frame] = DestroysByFrame.GetValueOrDefault(Frame) + 1;
      OnDestroy?.Invoke(obj);
      if (Links.TryGetValue(obj.Id, out var held))
        spawned = held;
      return true;
    }

    public void Unplace(Vector2s zone) => Calls.Add($"unplace {zone.x},{zone.y}");

    public void Ungenerate(Vector2s zone)
    {
      Calls.Add($"ungenerate {zone.x},{zone.y}");
      Generated.Remove(zone);
      OnUngenerate?.Invoke(zone);
    }

    public void DestroyRoot(Vector2s zone) => Calls.Add($"root {zone.x},{zone.y}");

    public int AdjustBorder(Vector2s zone, Edges edges)
    {
      Calls.Add($"border {zone.x},{zone.y} {edges}");
      BorderCalls.Add((zone, edges));
      return BorderVertices.GetValueOrDefault(zone);
    }
  }

  // Runs the work to its end, counting the frames: Frame is the frame a call is made in.
  private static (int Frames, Progress Progress) Drive(FakeWorld world, Plan plan, Budget budget)
  {
    var progress = new Progress();
    var work = ZoneReset.Execute(world, plan, budget, progress);
    int frames = 1;
    while (work.MoveNext())
    {
      frames++;
      world.Frame = frames - 1;
    }
    return (frames, progress);
  }

  private static Budget Roomy() => new(1_000_000, 1_000_000, () => 0);

  private static void ExecuteTests()
  {
    Section("execute: a made-up world, zone by zone");
    uint next = 1;

    // Two zones: three plain objects, a tamed animal, the player at this machine on the ground.
    var world = new FakeWorld();
    world.Add(Z(0, 0), O(next++), O(next++), O(next++), O(next++, Kind.Tamed), O(next++, Kind.Player | Kind.Local));
    world.Add(Z(1, 0), O(next++), O(next++));
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0)], [], [Z(0, 0)]);
    var (frames, progress) = Drive(world, plan, Roomy());
    C(frames == 1, "a small world is done in one frame");
    C(world.Destroyed.Count == 5 && !world.Destroyed.Contains(Id(4)) && !world.Destroyed.Contains(Id(5)), "the plain objects go; the tamed animal and the player stay");
    C(progress.Reset == 2 && progress.Done == 2 && progress.Skipped == 0 && progress.Destroyed == 5 && progress.Kept == 2 && progress.Total == 2,
      "counted: 2 zones reset, 5 objects destroyed, 2 kept");
    C(string.Join("|", world.Calls) == "list 0,0|destroy 1|destroy 2|destroy 3|list 1,0|destroy 6|destroy 7|unplace 0,0|ungenerate 0,0|root 0,0|unplace 1,0|ungenerate 1,0|root 1,0",
      "each zone: its objects, then its location, then its generated mark, then its loaded copy; the first waits for the objects of the zone beside it");
    C(progress.Percent == 100, "done is 100%");

    // The plan's order is the order of the work.
    world = new FakeWorld();
    next = 1;
    plan = ZoneReset.MakePlan(Square(1), [], [Z(1, 1)]);
    world.Fill(Square(1), 1, ref next);
    Drive(world, plan, Roomy());
    var listed = world.Calls.Where(c => c.StartsWith("list")).Select(c => c[5..]).ToList();
    C(string.Join(" ", listed) == Show(plan.Reset), "zones are reset in the plan's order");

    // Something that protects stands in a zone planned for reset (a piece placed after the scan): the zone is left alone.
    world = new FakeWorld();
    next = 1;
    world.Add(Z(0, 0), O(next++), O(next++));
    world.Add(Z(1, 0), O(next++), O(next++));
    world.Add(Z(5, 0), O(next++));
    world.OnList = zone =>
    {
      if (zone.Equals(Z(1, 0)) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(5, 0)], [], [Z(0, 0)]);
    (_, progress) = Drive(world, plan, Roomy());
    C(progress.Skipped == 1 && progress.Reset == 2 && progress.Done == 3, "a zone that holds a piece by its turn is skipped, the others reset");
    C(!world.Calls.Any(c => c.EndsWith("1,0") && !c.StartsWith("list")) && !world.Destroyed.Contains(Id(3)) && !world.Destroyed.Contains(Id(4)),
      "nothing of the skipped zone is touched: not an object, not its location, not its generated mark, not its loaded copy");

    // The same for a tombstone, a player from another machine, the player at this machine in a dungeon; not for them on the ground.
    foreach (var (kind, skipped, what) in new[]
             {
               (Kind.Tombstone, true, "a tombstone"),
               (Kind.Player, true, "a player from another machine"),
               (Kind.Player | Kind.Local | Kind.Interior, true, "the player at this machine in a dungeon"),
               (Kind.Player | Kind.Local, false, "the player at this machine on the ground"),
               (Kind.Tamed, false, "a tamed animal"),
             })
    {
      world = new FakeWorld();
      world.Add(Z(0, 0), O(1), O(2, kind));
      plan = ZoneReset.MakePlan([Z(0, 0)], [], []);
      (_, progress) = Drive(world, plan, Roomy());
      C(skipped ? progress.Skipped == 1 && world.Destroyed.Count == 0 : progress.Reset == 1 && world.Destroyed.Count == 1, $"{what} in a zone planned for reset: {(skipped ? "the zone is left alone" : "the zone is reset around it")}");
    }

    // Survivors of every kind in one zone, with one plain object among them.
    world = new FakeWorld();
    next = 1;
    var keep = new[] { Kind.Player, Kind.Player | Kind.Local, Kind.Tamed, Kind.Piece | Kind.Tamed, Kind.Tombstone | Kind.Tamed };
    world.Add(Z(0, 0), keep.Select(k => O(next++, k)).Concat([O(next++)]).ToArray());
    // The plan was made without knowing a piece and a tombstone are there; the work finds them and leaves the zone whole.
    plan = ZoneReset.MakePlan([Z(0, 0)], [], []);
    (_, progress) = Drive(world, plan, Roomy());
    C(progress.Skipped == 1 && world.Destroyed.Count == 0, "a zone with survivors that protect is left whole, the plain object too");
    world = new FakeWorld();
    next = 1;
    world.Add(Z(0, 0), O(next++, Kind.Tamed), O(next++, Kind.Player | Kind.Local), O(next++), O(next++, Kind.Tamed));
    (_, progress) = Drive(world, ZoneReset.MakePlan([Z(0, 0)], [], []), Roomy());
    C(progress.Kept == 3 && progress.Destroyed == 1 && world.Destroyed.Single().ID == 3, "in a zone that is reset, three survivors are counted and one object goes");

    // An object that was destroyed meanwhile (the world did it) is not counted.
    world = new FakeWorld();
    world.Add(Z(0, 0), O(1), O(2));
    world.Destroyed.Add(Id(2));
    (_, progress) = Drive(world, ZoneReset.MakePlan([Z(0, 0)], [], []), Roomy());
    C(progress.Destroyed == 1 && progress.Reset == 1, "an object already gone is not listed, not counted");

    // The world goes away between zones: the work stops there, nothing after is touched.
    world = new FakeWorld();
    next = 1;
    world.Fill([Z(0, 0), Z(1, 0), Z(2, 0)], 2, ref next);
    world.IsAlive = () => !world.Calls.Contains("destroy 2");
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(2, 0)], [], [Z(0, 0)]);
    (_, progress) = Drive(world, plan, Roomy());
    C(progress.Aborted && progress.Reset == 0 && !world.Calls.Any(c => c.EndsWith("1,0") || c.EndsWith("2,0") || c.StartsWith("root")) && world.Destroyed.Count == 2,
      "the world gone after the first zone's objects: nothing else is touched, the work says it was stopped");

    // Borders come after every reset, in a fixed order, and count only the ones that changed.
    world = new FakeWorld();
    next = 1;
    world.Fill(Square(2), 1, ref next);
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    world.BorderVertices[Z(1, 0)] = 7;
    world.BorderVertices[Z(-1, 1)] = 3;
    (_, progress) = Drive(world, plan, Roomy());
    var calls = world.Calls;
    int firstBorder = calls.FindIndex(c => c.StartsWith("border"));
    C(firstBorder > calls.FindLastIndex(c => c.StartsWith("root")), "the left-alone zones' edges are done after the last reset");
    var border = world.BorderCalls;
    C(border.Count == 8 && border.Select(b => b.Zone).SequenceEqual(border.Select(b => b.Zone).OrderBy(z => z.x).ThenBy(z => z.y)), "all eight left-alone zones that meet a reset zone are visited, in x then y order");
    C(border[0].Zone.Equals(Z(-1, -1)) && border[0].Edges == (Edges.West | Edges.SouthWest | Edges.South | Edges.NorthWest | Edges.SouthEast), "each with the sides that meet a reset zone: the south west corner of the block has five");
    C(progress.BorderZones == 2 && progress.BorderVertices == 10, "two zones had edges to mend: 10 vertices");

    // Mending a zone's edge costs a frame's worth when frames are small: the eight borders of the block take their own frames.
    world = new FakeWorld();
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    (frames, progress) = Drive(world, plan, new Budget(ZoneReset.BorderCost, 1_000_000, () => 0));
    C(frames == 8 && world.BorderCalls.Count == 8, $"at {ZoneReset.BorderCost} objects a frame the 16 empty zones and the first border take one frame, each of the other seven borders another ({frames})");

    // A spawner's creature goes with it, wherever it has walked to: into another zone that is reset, into the spawner's own; not
    // into a zone that is left alone (nor into one next to a protector that is not generated), not a tame one, not one that is gone.
    world = new FakeWorld();
    world.Add(Z(0, 0), O(1), O(2), O(3), O(4), O(5), O(13), O(6));
    world.Spawns(1, 10).Spawns(2, 11).Spawns(3, 12).Spawns(4, 99).Spawns(5, 13).Spawns(6, 14);
    world.Add(Z(3, 0), O(10), O(12, Kind.Tamed));
    world.Add(Z(-5, 0), O(11), O(20, Kind.Piece));
    world.Add(Z(-4, 1), O(14));
    plan = ZoneReset.MakePlan([Z(0, 0), Z(3, 0), Z(-5, 0)], [Z(-5, 0)], [Z(0, 0)]);
    (_, progress) = Drive(world, plan, Roomy());
    C(world.Destroyed.Select(d => (int)d.ID).OrderBy(i => i).SequenceEqual([1, 2, 3, 4, 5, 6, 10, 13]),
      "the spawners go, and the creatures that walked into a zone that is reset or stayed in the spawner's own: " + string.Join(",", world.Destroyed.Select(d => d.ID).OrderBy(i => i)));
    C(progress.Spawned == 2 && progress.SpawnedKept == 3 && progress.Destroyed == 8, $"counted: two creatures taken along, three that stay ({progress.Spawned}, {progress.SpawnedKept}, {progress.Destroyed})");
    C(!world.Destroyed.Contains(Id(11)) && !world.Destroyed.Contains(Id(14)) && !world.Destroyed.Contains(Id(12)), "the creature in a kept zone, the one in a zone next to a protector, and the tame one stay");
    C(world.Destroyed.Count == 8 && progress.Kept == 1, "each object once, even the creature that is also in the list of its own zone");

    // The world is told of every new frame.
    world = new FakeWorld();
    next = 1;
    world.Fill(Square(1), 4, ref next);
    (frames, progress) = Drive(world, ZoneReset.MakePlan(Square(1), [], [Z(0, 0)]), new Budget(10, 1_000_000, () => 0));
    C(world.Frames == frames, $"the work tells the world each frame is a new one ({world.Frames} for {frames})");

    // An empty plan.
    (frames, progress) = Drive(new FakeWorld(), ZoneReset.MakePlan([], [], []), Roomy());
    C(frames == 1 && progress.Total == 0 && progress.Percent == 100 && !progress.Aborted, "nothing to reset: done at once");
  }

  // ------------------------------------------------------------------------------------------------ spread over frames

  private static void BudgetTests()
  {
    Section("time-slicing: objects and milliseconds per frame");
    uint next = 1;
    // 25 zones of 4 objects, 10 objects a frame at most.
    var zones = Square(2, 0, 0).Take(25).ToList();
    var world = new FakeWorld();
    world.Fill(zones, 4, ref next);
    var plan = ZoneReset.MakePlan(zones, [], [Z(0, 0)]);
    var (frames, progress) = Drive(world, plan, new Budget(10, 1_000_000, () => 0));
    C(progress.Destroyed == 100 && progress.Reset == 25, "every object of every zone goes in the end");
    C(world.DestroysByFrame.Values.Max() <= 10, $"no frame destroys more than 10 objects (most: {world.DestroysByFrame.Values.Max()})");
    C(frames > 10 && world.DestroysByFrame.Count > 10, $"the work is spread over several frames ({frames})");
    // A zone is whole or its tail waits: no other zone's calls are in between.
    Vector2s ZoneOfObject(string call) => world.Objects.First(pair => pair.Value.Any(o => o.Id.ID == uint.Parse(call[8..]))).Key;
    string listed = "";
    bool alternate = true;
    foreach (var call in world.Calls)
      if (call.StartsWith("list "))
        listed = call[5..];
      else if (call.StartsWith("destroy "))
        alternate &= $"{ZoneOfObject(call).x},{ZoneOfObject(call).y}" == listed;
    C(alternate && world.Calls.Count(c => c.StartsWith("root")) == 25, "zones never interleave: each zone is listed and emptied before the next is listed, and every zone is finished in the end");

    // One zone bigger than a frame: spread over frames, finished after its last object.
    world = new FakeWorld();
    next = 1;
    world.Fill([Z(0, 0)], 35, ref next);
    world.Fill([Z(5, 0)], 2, ref next);
    plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 0)], [], [Z(0, 0)]);
    (frames, progress) = Drive(world, plan, new Budget(10, 1_000_000, () => 0));
    C(progress.Destroyed == 37 && world.DestroysByFrame.Values.All(n => n <= 10) && frames >= 4, "a zone of 35 objects takes four frames at 10 a frame");
    int lastDestroy = world.Calls.FindLastIndex(c => c == "destroy 35");
    int unplace = world.Calls.IndexOf("unplace 0,0");
    C(lastDestroy < unplace && unplace < world.Calls.IndexOf("list 5,0"), "its location, generated mark and loaded copy follow its last object, before a zone that is not beside it");
    // A budget that leaves no room still makes progress.
    world = new FakeWorld();
    next = 1;
    world.Fill([Z(0, 0)], 5, ref next);
    (frames, progress) = Drive(world, ZoneReset.MakePlan([Z(0, 0)], [], []), new Budget(1, 1_000_000, () => 0));
    C(progress.Destroyed == 5 && progress.Reset == 1, "a frame of one object still gets everything done");

    // Milliseconds: a clock the world advances by one on each listing, 3 ms a frame.
    double now = 0;
    world = new FakeWorld();
    next = 1;
    var twelve = Square(1, 0, 0).Take(9).Concat([Z(5, 5), Z(6, 6), Z(7, 7)]).ToList();
    world.Fill(twelve, 1, ref next);
    world.OnList = _ => now += 1;
    plan = ZoneReset.MakePlan(twelve, [], [Z(0, 0)]);
    (frames, progress) = Drive(world, plan, new Budget(1_000_000, 3, () => now));
    C(progress.Reset == 12 && progress.Destroyed == 12, "all twelve zones are done");
    C(frames == 4, $"at 3 ms a frame and 1 ms a zone, twelve zones take 4 frames, three zones each ({frames})");

    // The budget itself.
    now = 0;
    var b = new Budget(5, 10, () => now);
    b.NewFrame();
    C(!b.Spent && b.Room == 5, "a new frame has its whole budget");
    b.Use(3);
    C(!b.Spent && b.Room == 2, "part of it used");
    b.Use(2);
    C(b.Spent && b.Room == 0, "spent when the objects run out");
    b.NewFrame();
    C(!b.Spent, "a new frame starts afresh");
    now = 9.9;
    C(!b.Spent, "just under the time");
    now = 10;
    C(b.Spent, "spent when the time is up");
    b.Use(100);
    C(b.Room == 0, "never negative room");
    // With no clock given the system's is used, in milliseconds: it moves, and as fast as time does.
    var first = Budget.SystemClock();
    System.Threading.Thread.Sleep(30);
    var elapsed = Budget.SystemClock() - first;
    C(elapsed >= 29 && elapsed < 5000, $"the system clock counts milliseconds: a pause of 30 ms is {elapsed:0.0}");
    var real = new Budget(int.MaxValue, 600_000);
    real.NewFrame();
    C(!real.Spent, "a budget with no clock of its own uses the system's: a fresh frame of ten minutes is not spent at once");

    // The numbers the game side uses: a frame's destroy message stays well under the 512 KiB the sockets allow (12 bytes each).
    C(ZoneRegen.ObjectsPerFrame * 12 + 1024 < 512 * 1024 / 4, $"{ZoneRegen.ObjectsPerFrame} objects a frame is {ZoneRegen.ObjectsPerFrame * 12 / 1024} KiB of a 512 KiB message");
    C(ZoneRegen.ObjectsPerFrameWithPlayers * 12 * 60 <= 150 * 1024 / 2, $"with players connected, {ZoneRegen.ObjectsPerFrameWithPlayers} objects a frame at 60 a second is {ZoneRegen.ObjectsPerFrameWithPlayers * 12 * 60 / 1024} KB/s of the 150 KB/s a connection gets: at most half");
    // The budget follows a number asked at the start of each frame.
    int limit = 7;
    var follow = new Budget(() => limit, 1_000_000, () => 0);
    C(follow.Room == 7, "a budget asks for its limit when it is made");
    limit = 3;
    C(follow.Room == 7, "and not while a frame runs");
    follow.NewFrame();
    C(follow.Room == 3, "but at the start of the next");
    double frameMs = ZoneRegen.MillisecondsPerFrame;
    C(frameMs >= 1 && frameMs <= 12, "a frame's time is a part of a 60 Hz frame");
  }

  // ------------------------------------------------------------------------------------------------ what it says

  private static void MessageTests()
  {
    Section("messages");
    var plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    var start = ZoneReset.StartLine(plan);
    C(start.Contains("resetting 16 of 25 generated zones") && start.Contains("9 left alone"), "the start line says how many of how many zones, and how many are left alone: " + start);
    C(ZoneReset.StartLine(ZoneReset.MakePlan([], [], [])).Contains("no generated zone"), "no zones: it says so");
    var progress = new Progress { Total = 16, Done = 8, Reset = 8, Destroyed = 1234 };
    var line = ZoneReset.ProgressLine(progress);
    C(line.Contains("8 of 16") && line.Contains("50%") && line.Contains("1,234"), "a progress line: " + line);
    progress = new Progress { Total = 16, Done = 16, Reset = 15, Skipped = 1, Destroyed = 5000, Kept = 4, BorderZones = 3, BorderVertices = 90 };
    var finish = ZoneReset.FinishLine(progress, 2.5, 0);
    C(finish.Contains("2.5 s") && finish.Contains("15 zones reset") && finish.Contains("5,000 objects") && finish.Contains("4 objects stay") && finish.Contains("1 zone was left alone after all")
      && finish.Contains("3 left-alone zones") && finish.Contains("generates again"), "the finish line has every count: " + finish);
    C(!finish.Contains("errors"), "no errors, none mentioned");
    C(ZoneReset.FinishLine(progress, 1, 2).Contains("2 errors"), "errors are counted");
    var one = ZoneReset.FinishLine(new Progress { Total = 1, Done = 1, Reset = 1, Destroyed = 1, Kept = 1, BorderZones = 1, Skipped = 0 }, 1, 1);
    C(one.Contains("1 zone reset") && one.Contains("1 object removed") && one.Contains("1 object stays") && one.Contains("1 left-alone zone was adjusted") && one.Contains("1 error,"), "one of anything is said in the singular: " + one);
    C(ZoneReset.StartLine(ZoneReset.MakePlan([Z(0, 0)], [], [])).Contains("resetting 1 of 1 generated zone;") && ZoneReset.StartLine(ZoneReset.MakePlan([Z(0, 0)], [Z(0, 0)], [])).Contains("(1 generated zone is left alone)"),
      "the start line in the singular");
    progress.Aborted = true;
    C(ZoneReset.FinishLine(progress, 1, 0).Contains("stopped") && !ZoneReset.FinishLine(progress, 1, 0).Contains("generates again"), "a stopped run says so");
    // The words players read do not speak of other mods (the project's rule for what a player reads).
    var words = new[] { start, line, finish, ZoneReset.StartLine(ZoneReset.MakePlan([], [], [])) };
    string[] banned = ["Upgrade World", "Expand World", "replace", "obsolete", "no longer need"];
    C(words.All(w => banned.All(x => !w.Contains(x, StringComparison.OrdinalIgnoreCase))), "none of the lines names or replaces another mod");
  }
}

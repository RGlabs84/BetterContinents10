// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// What the review round of zone regeneration asked for: ground work protects (a compiler with an edited or painted vertex), a location
// is kept whole (groups of zones, and a zone is finished only when the zones beside it are emptied), no zone is half empty in a
// save or after a stop, who is near is looked at again every frame, the borders follow what was actually cleared, the peers'
// radius follows their simulation distance, a frame is limited by the clock inside a zone and by the wall time with peers
// connected, a spawner's creature is read when the spawner is destroyed, and the words say what is so.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Budget = BetterContinents.ZoneReset.Budget;
using Edges = BetterContinents.ZoneReset.Edges;
using Kind = BetterContinents.ZoneReset.Kind;
using Plan = BetterContinents.ZoneReset.Plan;
using Progress = BetterContinents.ZoneReset.Progress;
using Rules = BetterContinents.ZoneReset.Rules;

internal static partial class Program
{
  private static void FixesTests()
  {
    using var seams = new Seams();
    ZoneRegen.DeltaTime = () => 1f / 60f;
    ZoneRegen.FrameClock = () => 0.0;
    GroundWorkTests();
    LocationPlanTests();
    LocationWorkTests();
    SaveTests();
    SavePatchTests();
    StopTests();
    LiveTests();
    BorderTests();
    PeerTests();
    LimitTests();
    SpawnerTests();
    TextTests();
    PatchTests();
    FixFactTests();
  }

  // ------------------------------------------------------------------------------------------------ helpers

  private static string Sorted(IEnumerable<Vector2s> zones) => string.Join(" ", zones.OrderBy(z => z.x).ThenBy(z => z.y).Select(z => $"{z.x},{z.y}"));

  // A location in zone (zx, zy) whose centre is dx, dz metres from the zone's middle.
  private static ZoneReset.PlacedLocation LocIn(int zx, int zy, float dx, float dz, float radius) => new(Z(zx, zy), zx * 64f + dx, zy * 64f + dz, radius);

  // Runs the work to its end on a made-up world; the work is handed back for what a test does to it.
  private static (ZoneReset.Work Work, Progress Progress, int Frames) Go(FakeWorld world, Plan plan, Budget budget, ZoneReset.Memory? memory = null)
  {
    var progress = new Progress();
    var work = new ZoneReset.Work(world, plan, budget, progress, memory ?? new ZoneReset.Memory());
    var steps = work.Steps();
    int frames = 1;
    while (steps.MoveNext())
    {
      frames++;
      world.Frame = frames - 1;
    }
    return (work, progress, frames);
  }

  private static List<string> Strings(MethodBase method) =>
    PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode == OpCodes.Ldstr).Select(i => (string)i.operand).ToList();

  // ------------------------------------------------------------------------------------------------ A: ground work protects

  private static void GroundWorkTests()
  {
    Section("ground work: a field, a path or levelled ground protects like a piece");
    static bool Has(byte[]? data) => ZoneReset.TerrainBorder.HasEdits(data);
    C(Has(TcData((r, c) => r == 12 && c == 40)), "a compiler with one edited height vertex is ground work");
    C(Has(TcData((r, c) => r == 64 && c == 64)), "... at the last vertex of the grid too");
    C(Has(TcData((r, c) => false, (r, c) => r == 3 && c == 9)), "a compiler with one painted vertex and no edited height is ground work (the surface of a field)");
    C(Has(TcData((r, c) => true, (r, c) => true)), "a compiler with both is");
    C(!Has(TcData((r, c) => false)), "a compiler with neither is not");
    var northOnly = TcData((r, c) => r == 64);
    var mended = ZoneReset.TerrainBorder.Clear(northOnly, Edges.North, out _);
    C(Has(northOnly) && mended != null && !Has(mended), "a compiler whose only edits were cleared at a border is not ground work any more");
    var paintedNorth = TcData((r, c) => r == 64, (r, c) => r == 64);
    var paintLeft = ZoneReset.TerrainBorder.Clear(paintedNorth, Edges.North, out _);
    C(paintLeft != null && Has(paintLeft), "... unless it was painted: the paint is not touched at a border");
    C(Has(TcData((r, c) => r == 4 && c == 4, pitch: 10)), "a grid of another width is read the same way");
    C(!Has(null) && !Has([]) && !Has([1, 2, 3, 4, 5, 6, 7, 8]) && !Has(Utils.Compress([1, 2, 3])), "data that is not a compiler's is not ground work, and nothing throws");
    C(!Has(Utils.Compress(Utils.Decompress(northOnly).Take(100).ToArray())), "data cut short is not");
    var notSquare = new ZPackage();
    notSquare.Write(1);
    notSquare.Write(1);
    notSquare.Write(Vector3.zero);
    notSquare.Write(1f);
    notSquare.Write(50);
    for (int i = 0; i < 50; i++)
      notSquare.Write(true);
    C(!Has(Utils.Compress(notSquare.GetArray())), "a grid that is not square is not read as one");

    // What the rules are told about the real ZDOs: only compilers are unpacked.
    ZDOExtraData.Reset();
    var local = new ZDOID(600L, 1u);
    ZDO Compiler(uint id, byte[]? data)
    {
      var zdo = NewZdo("_TerrainCompiler", id);
      if (data != null)
        ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_TCData, data);
      return zdo;
    }
    C(ZoneRegen.KindOf(Compiler(1, TcData((r, c) => r == 2 && c == 2)), local) == Kind.Ground, "a compiler with an edited vertex is ground");
    C(ZoneRegen.KindOf(Compiler(2, TcData((r, c) => false, (r, c) => c == 7)), local) == Kind.Ground, "a compiler with a painted vertex is ground");
    C(ZoneRegen.KindOf(Compiler(3, TcData((r, c) => false)), local) == Kind.None, "a compiler nothing was done to is nothing special");
    C(ZoneRegen.KindOf(Compiler(4, null), local) == Kind.None, "a compiler with no data yet is nothing special");
    C(ZoneRegen.KindOf(Compiler(5, [1, 2, 3]), local) == Kind.None, "a compiler with data nobody can read is nothing special");
    var pine = NewZdo("Pine", 6);
    ZDOExtraData.Set(pine.m_uid, ZDOVars.s_TCData, TcData((r, c) => true));
    C(ZoneRegen.KindOf(pine, local) == Kind.None, "only compilers are unpacked: a tree with the data of a field is a tree");
    C(ZoneRegen.KindOf(Creator(Compiler(7, TcData((r, c) => true)), 9L), local) == (Kind.Ground | Kind.Piece), "ground and a creator combine");

    // The scan, the plan and the work on real ZDOs: a worked field and a painted one keep the 3 x 3 around them.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(3))
      {
        w.Add(zone, "Pine");
        var compiler = w.Add(zone, "_TerrainCompiler");
        if (zone.Equals(Z(1, -1)))
          ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => r == 30 && c == 30));
        else if (zone.Equals(Z(-3, 3)))
          ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => false, (r, c) => c == 5));
        else
          ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => false));
        w.Generated.Add(zone);
      }
      var kept = Near(w.Generated.ToList(), [Z(1, -1), Z(-3, 3)]);
      var (_, _, job) = Regenerate(w);
      C(kept.Count == 13 && w.Generated.SetEquals(kept), $"a worked field and a painted one keep the 3 x 3 around them (13 zones), and nothing else is kept (left: {w.Generated.Count})");
      var queued = w.All.ToHashSet();
      C(queued.Count == 36 * 2 && job.Progress.Reset == 36, "the other 36 zones go whole, their compilers too");
      var worked = w.ByZone[Z(1, -1)].Single(zdo => zdo.GetPrefab() == "_TerrainCompiler".GetStableHashCode());
      C(!queued.Contains(worked.m_uid) && w.ByZone.Where(z => kept.Contains(z.Key)).All(z => z.Value.All(zdo => !queued.Contains(zdo.m_uid))), "the worked compiler and everything else in the kept zones stays");

      // A compiler hoed after it was listed is not destroyed.
      var gameWorld = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));
      var lateZone = Z(8, 8);
      var late = w.Add(lateZone, "_TerrainCompiler");
      var listed = gameWorld.ObjectsIn(lateZone);
      C(listed.Count == 1 && listed[0].Kind == Kind.None, "(a compiler nothing was done to is listed as an ordinary object)");
      ZDOExtraData.Set(late.m_uid, ZDOVars.s_TCData, TcData((r, c) => r == 1 && c == 1));
      int queuedBefore = w.DestroyQueue.Count;
      C(!gameWorld.Destroy(listed[0], out _) && w.DestroyQueue.Count == queuedBefore, "a compiler that was hoed after it was listed is not destroyed");
    }
    finally
    {
      World.Close();
    }

    // The same for the work's own look at a zone: ground work that appears in a zone planned for reset leaves it alone.
    var world = new FakeWorld();
    world.Add(Z(0, 0), O(1), O(2, Kind.Ground));
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(7, 0)], [], [Z(0, 0)]);
    world.Add(Z(7, 0), O(3));
    var (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 1 && progress.Reset == 1 && world.Destroyed.Count == 1 && !world.Destroyed.Contains(Id(1)), "ground work found in a zone at its turn: the zone is left alone, its plain objects too");
  }

  // ------------------------------------------------------------------------------------------------ B1: locations stay whole (the plan)

  private static void LocationPlanTests()
  {
    Section("locations: the zones a location reaches into stay or go together");
    // Which zones a circle touches.
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 20))) == "0,0", "a location in the middle of its zone touches only that zone");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 20))) == "0,0 1,0", "20 m from the east edge with a radius of 20: it reaches the zone to the east");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 30, 30, 20))) == "0,0 0,1 1,0 1,1", "near the north east corner: four zones");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 30, 30, 2.5f))) == "0,0 0,1 1,0", "a circle that reaches both edges but not the corner touches the corner's zone only if it gets there: it does not");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 30, 30, 3f))) == "0,0 0,1 1,0 1,1", "... and does when it just does");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 0f))) == "0,0", "no radius: its zone");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, float.NaN))) == "0,0" && ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, -5f)).Count == 1, "a radius that is not a number or is negative is none");
    C(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 100f)).Count == 13, "a radius of 100 m from the middle: the 3 x 3 and the zones two away in line");
    C(ZoneReset.TouchedZones(LocIn(0, 0, 0, 0, 1e9f)).Count <= 33 * 33, "a silly radius is cut, not a list of millions");
    var odd = new ZoneReset.PlacedLocation(Z(7, 7), 0f, 0f, 10f);
    C(Sorted(ZoneReset.TouchedZones(odd)) == "0,0 7,7", "the home zone is always touched, wherever the position says");
    C(ZoneReset.TouchedZones(LocIn(-1, -1, -20, -20, 20)).Count == 4 && ZoneReset.TouchedZones(LocIn(-1, -1, -20, -20, 20)).Contains(Z(-2, -2)), "negative zones work the same");

    var reach = LocIn(0, 0, 20, 0, 20);

    // A kept home keeps a zone its location reaches into.
    var generated = new[] { Z(0, 0), Z(1, 0), Z(5, 5) };
    var plan = ZoneReset.MakePlan(generated, [Z(-1, 0)], [Z(5, 5)], [reach]);
    C(Sorted(plan.Kept) == "0,0 1,0" && Show(plan.Reset) == "5,5" && plan.Covered.Contains(Z(1, 0)) && plan.KeptForLocations == 1,
      "a location's home near something that protects: the zone it reaches into stays too, and counts as covered");
    plan = ZoneReset.MakePlan(generated, [Z(-1, 0)], [Z(5, 5)]);
    C(Sorted(plan.Kept) == "0,0" && plan.Reset.Count == 2 && plan.KeptForLocations == 0, "(without the location, the neighbour is reset)");

    // A kept neighbour keeps the home.
    plan = ZoneReset.MakePlan(generated, [Z(2, 0)], [Z(5, 5)], [reach]);
    C(Sorted(plan.Kept) == "0,0 1,0" && plan.KeptForLocations == 1, "a zone a location reaches into is near something that protects: the home stays");

    // Chains: A reaches B, B reaches C, and something protects C.
    var chain = new[] { Z(0, 0), Z(1, 0), Z(2, 0), Z(9, 9) };
    plan = ZoneReset.MakePlan(chain, [Z(3, 0)], [Z(9, 9)], [reach, LocIn(1, 0, 20, 0, 20)]);
    C(Sorted(plan.Kept) == "0,0 1,0 2,0" && Show(plan.Reset) == "9,9" && plan.KeptForLocations == 2, "locations linked in a chain stay together: protecting the last keeps the first");
    // Another group is not drawn in.
    var two = new[] { Z(0, 0), Z(1, 0), Z(0, 5), Z(1, 5) };
    plan = ZoneReset.MakePlan(two, [Z(2, 0)], [], [reach, LocIn(0, 5, 20, 0, 20)]);
    C(Sorted(plan.Kept) == "0,0 1,0" && Sorted(plan.Reset) == "0,5 1,5", "a group that nothing protects goes on being reset");
    // A location that reaches no other zone links nothing.
    plan = ZoneReset.MakePlan(two, [Z(2, 0)], [], [LocIn(0, 0, 0, 0, 20)]);
    C(Sorted(plan.Kept) == "1,0" && plan.KeptForLocations == 0, "a location inside its zone links nothing");
    // Two locations reaching the same zone are one group.
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(2, 0)], [Z(-1, 0)], [], [LocIn(0, 0, 20, 0, 20), LocIn(2, 0, -20, 0, 20)]);
    C(Sorted(plan.Kept) == "0,0 1,0 2,0", "the home of a location that reaches a zone, and the home of another one that reaches it, are one group");
    // The zone a location reaches may be a zone nobody generated: if something protects it, the group stays.
    plan = ZoneReset.MakePlan([Z(0, 0), Z(9, 9)], [Z(2, 0)], [], [reach]);
    C(Sorted(plan.Kept) == "0,0" && plan.KeptForLocations == 1, "a zone of the group that was never generated, near something that protects, keeps the generated one");
    plan = ZoneReset.MakePlan([Z(0, 0), Z(9, 9)], [], [], [reach]);
    C(plan.Kept.Count == 0 && plan.Reset.Count == 2, "... and nothing protecting, nothing is kept");

    // Zones an earlier run emptied and did not finish are reset whatever protects them now.
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)], null, [Z(1, 0), Z(9, 9)]);
    C(plan.Kept.Count == 8 && plan.Reset.Count == 17 && plan.Reset.Contains(Z(1, 0)) && !plan.Kept.Contains(Z(1, 0)) && plan.Covered.Contains(Z(1, 0)),
      "a zone an earlier run emptied is reset though it is near something that protects now");
    C(Sorted(plan.Carried) == "1,0", "(only a zone that is generated is carried)");
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0)], [Z(-1, 0)], [], [reach], [Z(1, 0)]);
    C(Sorted(plan.Kept) == "0,0" && plan.KeptForLocations == 0, "... also when a kept location would have kept it: it is not counted as kept for the location");

    // Protectors keep the widest radius of the zone, and each is dilated by its own.
    var protectors = new ZoneReset.Protectors();
    protectors.Add(Z(0, 0), 1);
    protectors.Add(Z(0, 0), 3);
    protectors.Add(Z(0, 0), 2);
    protectors.Add(Z(20, 0), 1);
    C(protectors.Count == 2 && protectors.Single(p => p.Zone.Equals(Z(0, 0))).Radius == 3, "of several things in one zone the widest radius counts");
    var dilated = ZoneReset.Dilate(protectors);
    C(dilated.Count == 49 + 9 && dilated.Contains(Z(-3, 3)) && !dilated.Contains(Z(4, 0)) && dilated.Contains(Z(19, -1)), "each protector keeps the square its own radius makes");
    C(ZoneReset.Dilate([new ZoneReset.Protector(Z(0, 0), 2), new ZoneReset.Protector(Z(10, 0), 0)]).Count == 25 + 1, "a radius of 0 keeps the zone alone");
    C(ZoneReset.Dilate([Z(0, 0), Z(1, 0)], 1).Count == 12 && ((ZoneReset.Protector)Z(2, 2)).Radius == Rules.ProtectRadius, "a zone alone is a protector with a piece's radius");

    // What the game has placed: a location that is not placed stands nowhere, and links nothing.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      w.PlaceLocation(Z(0, 0), true, 40f, dx: 25f);
      w.PlaceLocation(Z(10, 0), false, 40f, dx: 25f);
      w.Zones.m_locationInstances[Z(5, 0)] = new ZoneSystem.LocationInstance { m_position = new Vector3(5 * 64f, 30f, 0f), m_placed = true };
      w.Zones.m_locationInstances[Z(12, 0)] = new ZoneSystem.LocationInstance
      {
        m_location = new ZoneSystem.ZoneLocation { m_exteriorRadius = 10f, m_interiorRadius = 55f }, m_position = new Vector3(12 * 64f, 30f, 3f), m_placed = true
      };
      var placed = ZoneRegen.PlacedLocations(w.Zones).OrderBy(l => l.Zone.x).ToList();
      C(placed.Count == 3 && Show(placed.Select(l => l.Zone)) == "0,0 5,0 12,0", "only the locations the game has placed are handed over");
      C(placed[0].Radius == 40f && placed[0].X == 25f && placed[0].Z == 0f, "a location's centre is its position, its reach its exterior radius");
      C(placed[1].Radius == 0f && placed[2].Radius == 10f, "a location with no data reaches nowhere; the interior's radius does not count, even when it is the larger (a dungeon is built inside one zone)");
      var plan2 = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(10, 0), Z(11, 0)], [Z(2, 0), Z(12, 0)], [Z(0, 0)], placed);
      C(Sorted(plan2.Kept) == "0,0 1,0 11,0" && Show(plan2.Reset) == "10,0", "a placed location keeps its home with the zone it reaches into; a location not placed keeps nothing");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ B1, B2: locations stay whole (the work)

  private static void LocationWorkTests()
  {
    Section("locations: the work keeps a location whole, and finishes a zone only when the zones beside it are emptied");
    var a = Z(0, 0);
    var b = Z(1, 0);
    var reach = LocIn(0, 0, 20, 0, 20);

    // A zone left alone at its turn takes the zones of its location that have not had theirs.
    var world = new FakeWorld();
    world.Add(a, O(1), O(2)).Add(b, O(3), O(4)).Add(Z(5, 0), O(5), O(6));
    world.OnList = zone =>
    {
      if (zone.Equals(a) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    var plan = ZoneReset.MakePlan([a, b, Z(5, 0)], [], [a], [reach]);
    var (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 2 && progress.SkippedForLocations == 1 && progress.Reset == 1 && progress.Done == 3, "a piece in the home at its turn: the zone it reaches into is left alone with it, the rest is reset");
    C(!world.Calls.Contains("list 1,0") && !world.Destroyed.Contains(Id(3)) && !world.Destroyed.Contains(Id(4)) && world.Destroyed.Contains(Id(5)), "the neighbour's objects are not even listed; the other zone goes");

    // ... but a zone that was emptied already cannot be filled again.
    world = new FakeWorld();
    world.Add(a, O(1), O(2)).Add(b, O(3), O(4)).Add(Z(5, 0), O(5), O(6));
    world.OnList = zone =>
    {
      if (zone.Equals(a) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([a, b, Z(5, 0)], [], [b], [reach]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 1 && progress.SkippedForLocations == 0 && progress.Reset == 2 && world.Destroyed.Contains(Id(3)) && world.Destroyed.Contains(Id(4)),
      "the neighbour had its turn first: it is gone, and only the home is left alone");
    C(world.Calls.IndexOf("unplace 1,0") > world.Calls.IndexOf("list 0,0"), "(its finish waited for the home's turn)");

    // The straddling location: its home regenerates as soon as it is not generated, putting the whole location down again.
    world = new FakeWorld();
    world.Add(a, O(1), O(2), O(5));
    world.Add(b, O(3), O(4), O(6));
    world.Generated.UnionWith([a, b]);
    uint fresh = 100;
    world.OnUngenerate = zone =>
    {
      if (zone.Equals(a))
      {
        world.Add(a, O(fresh++), O(fresh++));
        world.Add(b, O(fresh++), O(fresh++));
      }
      world.Add(zone, O(fresh++));
      world.Generated.Add(zone);
    };
    plan = ZoneReset.MakePlan(world.Generated, [], [a], [reach]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(Enumerable.Range(1, 6).All(i => world.Destroyed.Contains(Id((uint)i))), "every old object of both zones is destroyed");
    C(!world.Destroyed.Any(id => id.ID >= 100) && fresh > 100, $"nothing the zones put down when they generated again is destroyed ({fresh - 100} new objects)");
    int firstUngenerate = world.Calls.FindIndex(c => c.StartsWith("ungenerate"));
    int lastOld = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && uint.Parse(c[8..]) < 100);
    C(lastOld < firstUngenerate, "no zone generated again before the last old object was destroyed");

    // The zones of a location are waited for even when they are not beside each other (a radius of 100 m reaches two zones out).
    var far = LocIn(0, 0, 30, 0, 100);
    var line = new[] { Z(0, 0), Z(1, 0), Z(2, 0) };
    C(ZoneReset.TouchedZones(far).Count > 3 && line.All(z => ZoneReset.TouchedZones(far).Contains(z)), "(that location reaches the zones two to the east of its home)");
    world = new FakeWorld();
    uint next = 1;
    world.Fill(line, 2, ref next);
    world.Fill([Z(20, 0)], 2, ref next);
    plan = ZoneReset.MakePlan(line.Append(Z(20, 0)), [], [a], [far]);
    Go(world, plan, Roomy());
    int ungeneratedHome = world.Calls.IndexOf("ungenerate 0,0");
    int lastOfThird = world.Calls.FindLastIndex(c => c.StartsWith("destroy ") && world.Objects[Z(2, 0)].Any(o => c == $"destroy {o.Id.ID}"));
    C(lastOfThird >= 0 && ungeneratedHome > lastOfThird, "the home is finished after the objects of a zone of its location two zones away are gone");
    C(world.Calls.Count(c => c.StartsWith("root")) == 4 && ungeneratedHome < world.Calls.IndexOf("list 20,0"), "and it is finished as soon as the last zone of its location is emptied, not at the end of the run");

    // The zones of a location that are left alone with the one that holds a piece no longer hold back a zone beside them.
    var p = Z(0, 0);
    var g2 = Z(1, 0);
    var g1 = Z(2, 0);
    world = new FakeWorld();
    world.Add(p, O(1), O(2)).Add(g2, O(3), O(4)).Add(g1, O(5), O(6)).Add(Z(20, 0), O(7));
    world.OnList = zone =>
    {
      if (zone.Equals(g1) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([p, g2, g1, Z(20, 0)], [], [p, g1], [LocIn(2, 0, -20, 0, 20)]);
    C(Show(plan.Reset) == "0,0 2,0 1,0 20,0", "(the zone that waits is emptied first, then the one with the piece, then the one it shares a location with)");
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 2 && progress.SkippedForLocations == 1 && world.Calls.IndexOf("unplace 0,0") is > 0 && world.Calls.IndexOf("unplace 0,0") < world.Calls.IndexOf("list 20,0"),
      "the zone that waited for the one left alone with a location is finished then, not at the end of the run");

    // A neighbour left alone does not hold a zone back.
    world = new FakeWorld();
    world.Add(a, O(1), O(2)).Add(b, O(3), O(4));
    world.OnList = zone =>
    {
      if (zone.Equals(b) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([a, b], [], [a]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Reset == 1 && progress.Skipped == 1 && world.Calls.Contains("root 0,0") && world.Calls.IndexOf("unplace 0,0") > world.Calls.IndexOf("list 1,0"),
      "a zone waits for a neighbour's turn, and is finished when that zone is left alone");

    // Three in a row: each is finished when the one after it is emptied, the last when it is.
    world = new FakeWorld();
    next = 1;
    world.Fill([Z(0, 0), Z(1, 0), Z(2, 0)], 1, ref next);
    plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(2, 0)], [], [Z(0, 0)]);
    var memory = new ZoneReset.Memory();
    (_, progress, _) = Go(world, plan, Roomy(), memory);
    var finishes = world.Calls.Where(c => c.StartsWith("unplace")).ToList();
    C(string.Join(" ", finishes) == "unplace 0,0 unplace 1,0 unplace 2,0" && world.Calls.IndexOf("unplace 0,0") > world.Calls.IndexOf("destroy 2")
      && world.Calls.IndexOf("unplace 1,0") > world.Calls.IndexOf("destroy 3") && memory.Pending.Count == 0 && progress.Reset == 3,
      "a row of zones is finished as the wave of emptying passes, and none is left waiting at the end");
  }

  // ------------------------------------------------------------------------------------------------ C1, C2: a save in the middle

  // What a save writes of the made-up world: the objects not told away, the zones generated less those emptied and not finished.
  private static (HashSet<ZDOID> Objects, HashSet<Vector2s> Generated) Saved(FakeWorld world, ZoneReset.Memory memory) =>
    (world.Objects.Values.SelectMany(l => l).Select(o => o.Id).Where(id => !world.Flushed.Contains(id)).ToHashSet(),
     world.Generated.Except(memory.Pending).ToHashSet());

  // Whether a zone of the saved state is half done: generated with some of its objects gone, or not generated with some left.
  private static List<Vector2s> HalfZones(FakeWorld world, (HashSet<ZDOID> Objects, HashSet<Vector2s> Generated) saved) =>
    world.Objects.Where(pair =>
    {
      int present = pair.Value.Count(o => saved.Objects.Contains(o.Id));
      return saved.Generated.Contains(pair.Key) ? present != pair.Value.Count : present != 0 && world.Destroyed.Overlaps(pair.Value.Select(o => o.Id));
    }).Select(pair => pair.Key).ToList();

  private static void SaveTests()
  {
    Section("saving in the middle: no zone is stored half emptied");
    var z0 = Z(0, 0);
    var z1 = Z(1, 0);
    var world = new FakeWorld();
    uint next = 1;
    world.Fill([z0], 100, ref next);
    world.Fill([z1], 10, ref next);
    world.Generated.UnionWith([z0, z1]);
    var plan = ZoneReset.MakePlan(world.Generated, [], [z0]);
    var progress = new Progress();
    var memory = new ZoneReset.Memory();
    var work = new ZoneReset.Work(world, plan, new Budget(30, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    steps.MoveNext();
    C(work.InFlight && world.Destroyed.Count == 29 && world.Destroyed.Count < 100, $"the first frame empties part of a zone of 100 objects ({world.Destroyed.Count} gone)");
    world.Flush();
    C(HalfZones(world, Saved(world, memory)).SequenceEqual([z0]), "(control: a save one frame later, with nothing done about it, would store the zone half emptied, and generated)");

    work.BeforeSave();
    var saved = Saved(world, memory);
    C(!work.InFlight && world.Destroyed.Count == 100 && world.Flushed.Count == 100, "a save finishes the zone being emptied, and sends the queue: all 100 objects are out of the world's lists");
    C(HalfZones(world, saved).Count == 0 && !saved.Generated.Contains(z0) && saved.Generated.Contains(z1) && saved.Objects.Count == 10,
      "the saved state has no half zone: the emptied zone is not generated, the other has all its objects");
    C(world.Generated.Contains(z0) && memory.Pending.SetEquals([z0]), "the live world keeps the emptied zone as it is until it is finished");
    C(memory.Cleared.Count == 0 && world.BorderCalls.Count == 1 && world.BorderCalls[0].Zone.Equals(z1) && world.BorderCalls[0].Edges == Edges.West,
      "the ground beside the cleared zone is mended at the save; the pass is complete");
    int flushes = world.Flushes;
    work.BeforeSave();
    C(world.Flushes == flushes + 1 && world.BorderCalls.Count == 1 && world.Destroyed.Count == 100, "a second save in the same state changes nothing");

    // The run goes on from where the save found it.
    int frames = 1;
    while (steps.MoveNext())
      frames++;
    C(progress.Reset == 2 && progress.Done == 2 && world.Destroyed.Count == 110 && memory.Pending.Count == 0 && world.Generated.Count == 0 && !progress.Aborted,
      "after the save the run goes on to its end: both zones are finished, each object destroyed once");
    C(world.BorderCalls.Count == 1 && memory.Cleared.Count == 0, "the ground mended at the save is not mended again at the end: the zone beside it was finished meanwhile, and has no edits left");
    work.BeforeSave();
    saved = Saved(world, memory);
    C(HalfZones(world, saved).Count == 0 && saved.Generated.Count == 0 && saved.Objects.Count == 0, "and a save after the run has nothing half done either: the last frame's objects are sent first");

    // A save between zones has only the queue to send.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, Z(5, 0)], 3, ref next);
    world.Generated.UnionWith([z0, Z(5, 0)]);
    plan = ZoneReset.MakePlan(world.Generated, [], [z0]);
    memory = new ZoneReset.Memory();
    progress = new Progress();
    work = new ZoneReset.Work(world, plan, new Budget(4, 1_000_000, () => 0), progress, memory);
    steps = work.Steps();
    steps.MoveNext();
    C(!work.InFlight && world.Destroyed.Count == 3 && world.Flushed.Count == 0, "(the first frame emptied a zone and is spent)");
    work.BeforeSave();
    C(world.Flushed.Count == 3 && HalfZones(world, Saved(world, memory)).Count == 0, "a save between zones sends what was destroyed so far");

    // A world that is gone is not touched.
    world.IsAlive = () => false;
    flushes = world.Flushes;
    work.BeforeSave();
    C(world.Flushes == flushes, "a save in a world that closed does nothing");
  }

  // ZoneSystem.PrepareSave is plain managed code, so the real patch can be applied to it here; ZNet.SaveWorld and Piece.SetCreator reach
  // Unity's own code, which this process does not have, so those two are checked by their attributes (PatchTests).
  private static void PatchPrepareSave(Harmony harmony) =>
    harmony.Patch(AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.PrepareSave)),
      postfix: new HarmonyMethod(typeof(ZoneRegenPatch).GetMethod("PrepareSavePostfix", BindingFlags.NonPublic | BindingFlags.Static)));

  // The save of the real classes: ZoneSystem.PrepareSave copies, the patch edits the copies.
  private static void SavePatchTests()
  {
    Section("saving in the middle: the game's own save copies, as patched");
    ZDOExtraData.Reset();
    var w = new World();
    var harmony = new Harmony("zone-tests.save");
    var sent = 0;
    var oldSend = ZoneRegen.SendDestroyQueue;
    try
    {
      ZoneRegen.SendDestroyQueue = () =>
      {
        sent++;
        w.Flush();
      };
      PatchPrepareSave(harmony);
      // A peer from another machine keeps the frames small, so a zone of many objects takes several frames. It is far from everything.
      var far = w.Add(Z(40, 40), "Player", user: 700L);
      far.SetOwner(700L);
      w.Peer(far, 700L);
      // The zones are done nearest that player first: (5, 0), (1, 0), the big one (0, 0), and (-1, 0) after it, which keeps (0, 0) waiting.
      foreach (var zone in new[] { Z(0, 0), Z(1, 0), Z(5, 0), Z(-1, 0) })
      {
        int count = zone.Equals(Z(0, 0)) ? 250 : 5;
        for (int i = 0; i < count; i++)
          w.Add(zone, "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
        w.Generated.Add(zone);
        w.PlaceLocation(zone, true, 0f);
      }
      var job = new ZoneRegen.Job(_ => { });
      SetStatic(typeof(ZoneRegen), "job", job);
      var steps = ZoneRegen.Steps(job);
      int guard = 0;
      while (guard++ < 1000 && steps.MoveNext())
      {
        w.Flush();
        if (job.Work is { InFlight: true })
          break;
      }
      C(job.Work is { InFlight: true } && w.LiveIn(Z(0, 0)).Count is > 0 and < 250, $"(the run is in the middle of a zone of 250 objects: {w.LiveIn(Z(0, 0)).Count} left)");

      C(GetStatic<bool>(typeof(ZoneRegen), "flushOwed"), "(objects were destroyed since the queue was last sent)");
      // ZNet.SaveWorld, first.
      ZoneRegen.BeforeSave();
      C(!GetStatic<bool>(typeof(ZoneRegen), "flushOwed"), "(and the queue has been sent now)");
      C(w.LiveIn(Z(0, 0)).Count == 0 && w.DestroyQueue.Count == 0 && sent >= 1, "the save's first step has the zone emptied and the destroy queue sent: no object of it is left in the world's lists");

      // ZoneSystem.PrepareSave, as patched.
      w.Zones.PrepareSave();
      var generated = GetField<HashSet<Vector2s>>(w.Zones, "m_tempGeneratedZonesSaveClone");
      var locations = GetField<List<ZoneSystem.LocationInstance>>(w.Zones, "m_tempLocationsSaveClone");
      var memory = GetStatic<ZoneReset.Memory>(typeof(ZoneRegen), "memory");
      C(memory.Pending.SetEquals([Z(0, 0)]) && generated.SetEquals([Z(-1, 0)]),
        "the copy that is written has the zone that is emptied and waiting as not generated, as it has the zones that are finished; the zone not reached yet is as it is");
      var home = locations.Single(l => ZoneSystem.GetZone(l.m_position) == Z(0, 0));
      C(!home.m_placed && locations.Where(l => ZoneSystem.GetZone(l.m_position) == Z(-1, 0)).All(l => l.m_placed) && locations.Count(l => l.m_placed) == 1,
        "... and its location as not placed (the finished zones' locations are not placed), the zone not reached yet as placed");
      C(w.Generated.Contains(Z(0, 0)) && w.Zones.m_locationInstances[Z(0, 0)].m_placed && !w.Generated.Contains(Z(1, 0)), "the live world keeps the waiting zone generated and its location placed until the run finishes it");

      // The run finishes, and the next save leaves nothing out.
      while (steps.MoveNext())
        w.Flush();
      w.Zones.PrepareSave();
      generated = GetField<HashSet<Vector2s>>(w.Zones, "m_tempGeneratedZonesSaveClone");
      C(memory.Pending.Count == 0 && generated.Count == 0 && w.Generated.Count == 0, "when the run is over all four zones are finished, and a save has nothing to hide");
      locations = GetField<List<ZoneSystem.LocationInstance>>(w.Zones, "m_tempLocationsSaveClone");
      C(locations.All(l => !l.m_placed), "(every location is unplaced by the reset, saved as it is)");
    }
    finally
    {
      harmony.UnpatchAll("zone-tests.save");
      ZoneRegen.SendDestroyQueue = oldSend;
      SetStatic(typeof(ZoneRegen), "job", null);
      World.Close();
    }

    // With no run and nothing owed, the patches do nothing.
    ZDOExtraData.Reset();
    w = new World();
    harmony = new Harmony("zone-tests.save2");
    try
    {
      sent = 0;
      ZoneRegen.SendDestroyQueue = () => sent++;
      SetStatic(typeof(ZoneRegen), "flushOwed", false);
      PatchPrepareSave(harmony);
      w.Generated.Add(Z(0, 0));
      w.PlaceLocation(Z(0, 0), true, 0f);
      ZoneRegen.BeforeSave();
      w.Zones.PrepareSave();
      var generated = GetField<HashSet<Vector2s>>(w.Zones, "m_tempGeneratedZonesSaveClone");
      var locations = GetField<List<ZoneSystem.LocationInstance>>(w.Zones, "m_tempLocationsSaveClone");
      C(sent == 0 && generated.Contains(Z(0, 0)) && locations.Single().m_placed, "with no run under way and nothing left over, a save is as the game makes it");
      // The patch finds nothing of another world.
      var other = new World();
      other.Generated.Add(Z(3, 3));
      other.Zones.PrepareSave();
      C(GetField<HashSet<Vector2s>>(other.Zones, "m_tempGeneratedZonesSaveClone").Contains(Z(3, 3)), "(nor in another world)");
    }
    finally
    {
      harmony.UnpatchAll("zone-tests.save2");
      ZoneRegen.SendDestroyQueue = oldSend;
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ C3: stop

  private static void StopTests()
  {
    Section("a second request: the zone being emptied is finished, and what was emptied is reset by the next run");
    var z0 = Z(0, 0);
    var z1 = Z(1, 0);
    var world = new FakeWorld();
    uint next = 1;
    world.Fill([z0], 100, ref next);
    world.Fill([z1, Z(5, 0)], 10, ref next);
    world.Generated.UnionWith([z0, z1, Z(5, 0)]);
    var memory = new ZoneReset.Memory();
    var plan = ZoneReset.MakePlan(world.Generated, [], [z0], null, memory.Pending);
    var progress = new Progress();
    var work = new ZoneReset.Work(world, plan, new Budget(30, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    steps.MoveNext();
    C(work.InFlight && world.Destroyed.Count < 100, "(a zone of 100 objects is half emptied)");
    work.FinishInFlight();
    C(!work.InFlight && world.Destroyed.Count == 100 && memory.Pending.SetEquals([z0]) && memory.Cleared.SetEquals([z0]), "stopping finishes the zone being emptied; it is remembered as emptied and not finished");
    C(world.Generated.Contains(z0) && world.Objects[z0].All(o => world.Destroyed.Contains(o.Id)), "(it is bare, and still generated: the state the next run has to put right)");
    C(world.BorderCalls.Count == 0, "(the ground beside it is not mended: the stopped run never reached its borders)");

    // The next run: the same world, what is generated now, the zone carried.
    plan = ZoneReset.MakePlan(world.Generated, [], [z0], null, memory.Pending);
    C(plan.Carried.SetEquals([z0]) && plan.Reset.Contains(z0), "the next plan resets the zone that was emptied");
    var (_, progress2, _) = Go(world, plan, Roomy(), memory);
    C(world.Generated.Count == 0 && memory.Pending.Count == 0 && progress2.Reset == 3, "when it is over no zone is generated: none is left bare");
    C(world.BorderCalls.Count == 0 && memory.Cleared.Count == 0, "(and the ground was mended for what the first run cleared: nothing left to mend here)");

    // Something protects the emptied zone by then: it is reset all the same, and what is beside it stays.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, z1, Z(2, 0), Z(3, 0)], 4, ref next);
    world.Generated.UnionWith([z0, z1, Z(2, 0), Z(3, 0)]);
    memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    memory.Cleared.Add(z0);
    foreach (var o in world.Objects[z0])
      world.Destroyed.Add(o.Id);
    plan = ZoneReset.MakePlan(world.Generated, [Z(2, 0)], [z0], null, memory.Pending);
    (_, progress2, _) = Go(world, plan, Roomy(), memory);
    C(Sorted(world.Generated) == "1,0 2,0 3,0" && progress2.Reset == 1 && progress2.Skipped == 0, "an emptied zone beside a base is reset again; the base's zones stay");
    C(world.BorderCalls.Count == 1 && world.BorderCalls[0].Zone.Equals(z1) && world.BorderCalls[0].Edges == Edges.West, "and the ground beside it is mended once");

    // A zone that is no longer generated is forgotten.
    memory = new ZoneReset.Memory();
    memory.Pending.UnionWith([z0, Z(9, 9)]);
    world = new FakeWorld();
    world.Fill([z0], 1, ref next);
    world.Generated.Add(z0);
    plan = ZoneReset.MakePlan(world.Generated, [], [], null, memory.Pending);
    Go(world, plan, Roomy(), memory);
    C(memory.Pending.Count == 0 && !world.Calls.Contains("unplace 9,9"), "a zone that is not generated any more is dropped from what is owed, not finished");

    // The zone a location shares with an emptied one is left alone at its turn: the emptied zone is finished all the same.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0], 2, ref next);
    world.Fill([z1], 2, ref next);
    world.Generated.UnionWith([z0, z1]);
    memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    memory.Cleared.Add(z0);
    world.Destroyed.UnionWith(world.Objects[z0].Select(o => o.Id));
    world.OnList = zone =>
    {
      if (zone.Equals(z1) && world.Objects[zone].Count == 2)
        world.Add(zone, O(88, Kind.Piece));
    };
    plan = ZoneReset.MakePlan(world.Generated, [], [z0], [LocIn(0, 0, 20, 0, 20)], memory.Pending);
    (_, progress2, _) = Go(world, plan, Roomy(), memory);
    C(progress2.Skipped == 1 && progress2.SkippedForLocations == 0 && progress2.Reset == 1 && world.Calls.Contains("unplace 0,0") && !world.Calls.Contains("unplace 1,0") && world.Generated.SetEquals([z1]),
      "a zone that was emptied is finished even when the zone it shares a location with is left alone: the bare one fills again");

    // A piece placed in an emptied zone by then does not stop it: nothing is left in it to protect.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, z1], 2, ref next);
    world.Generated.UnionWith([z0, z1]);
    memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    memory.Cleared.Add(z0);
    world.Destroyed.UnionWith(world.Objects[z0].Select(o => o.Id));
    world.Add(z0, O(77, Kind.Piece));
    plan = ZoneReset.MakePlan(world.Generated, [], [z0], null, memory.Pending);
    (_, progress2, _) = Go(world, plan, Roomy(), memory);
    C(progress2.Skipped == 0 && progress2.Reset == 2 && !world.Destroyed.Contains(Id(77)) && world.Generated.Count == 0, "a piece placed in the bare zone does not keep it bare: it is reset, and the piece is not touched");
  }

  // ------------------------------------------------------------------------------------------------ D: who is near, again every frame

  private static void LiveTests()
  {
    Section("who is near is looked at again every frame");
    var line = Enumerable.Range(0, 10).Select(x => Z(x, 0)).ToList();
    var world = new FakeWorld();
    uint next = 1;
    world.Fill(line, 1, ref next);
    // One zone a frame: a listing and an object are two, the frame holds two.
    world.OnNewFrame = () =>
    {
      if (world.Frames == 3)
        world.Live.Add(new ZoneReset.Protector(Z(7, 0), 2));
    };
    var plan = ZoneReset.MakePlan(line, [], [Z(0, 0)]);
    var (_, progress, frames) = Go(world, plan, new Budget(2, 1_000_000, () => 0));
    C(progress.Reset == 5 && progress.Skipped == 5 && progress.Done == 10, $"a player who walks in with a radius of 2 keeps the five zones around them that had not had their turn (frames: {frames})");
    C(Sorted(world.Generated) == "" && world.Calls.Count(c => c.StartsWith("list")) == 5, "the zones they keep are not even listed");
    C(Enumerable.Range(0, 5).All(x => world.Destroyed.Contains(Id((uint)(x + 1)))) && !world.Destroyed.Contains(Id(6)) && !world.Destroyed.Contains(Id(10)), "zones 0 to 4 are gone, 5 to 9 stay");

    // A player with a radius of 1 keeps only 6 to 8.
    world = new FakeWorld();
    next = 1;
    world.Fill(line, 1, ref next);
    world.OnNewFrame = () =>
    {
      if (world.Frames == 3)
        world.Live.Add(new ZoneReset.Protector(Z(7, 0), 1));
    };
    (_, progress, _) = Go(world, ZoneReset.MakePlan(line, [], [Z(0, 0)]), new Budget(2, 1_000_000, () => 0));
    C(progress.Skipped == 3 && progress.Reset == 7, "with a radius of 1 it is three");

    // A player the plan did not know about (it was made before they came) is found when the work begins.
    world = new FakeWorld();
    next = 1;
    world.Fill(line, 1, ref next);
    world.Live.Add(new ZoneReset.Protector(Z(2, 0), 1));
    (_, progress, _) = Go(world, ZoneReset.MakePlan(line, [], [Z(0, 0)]), Roomy());
    C(progress.Skipped == 3 && progress.Reset == 7, "a player the plan did not know about is found at the first frame");

    // A zone with a piece found at its turn: the zones around it stay too, those that have not had their turn.
    world = new FakeWorld();
    next = 1;
    world.Fill(line.Take(5), 2, ref next);
    world.OnList = zone =>
    {
      if (zone.Equals(Z(2, 0)) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    (_, progress, _) = Go(world, ZoneReset.MakePlan(line.Take(5), [], [Z(0, 0)]), Roomy());
    C(progress.Skipped == 2 && progress.Reset == 3 && !world.Calls.Contains("list 3,0"), "a piece found at a zone's turn keeps the zone after it too, unlisted; the zone before it, done already, is not undone");
    C(world.Destroyed.Contains(Id(3)) && world.Destroyed.Contains(Id(4)) && world.Destroyed.Contains(Id(9)) && !world.Destroyed.Contains(Id(5)) && !world.Destroyed.Contains(Id(7)),
      "zones 0, 1 and 4 are emptied; 2 and 3 stay");

    // What the local player placed during the run, on real classes: the postfix of Piece.SetCreator records it.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(3))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      ZoneRegen.NotePlaced(new Vector3(2 * 64f, 30f, 0f));
      var job = new ZoneRegen.Job(_ => { });
      SetStatic(typeof(ZoneRegen), "job", null);
      var placed = GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun");
      C(placed.Count == 0, "(no run under way: a piece placed is not recorded)");
      SetStatic(typeof(ZoneRegen), "job", job);
      var (_, _, run) = Regenerate(w, () => ZoneRegen.NotePlaced(new Vector3(2 * 64f + 10f, 30f, 0f)));
      C(Sorted(w.Generated) == Sorted(Square(1, 2, 0).Where(z => Math.Abs(z.x) <= 3 && Math.Abs(z.y) <= 3)), $"a piece the player placed after the plan keeps the 3 x 3 around it (left: {Sorted(w.Generated)})");
      C(run.Progress.Skipped == 9 && run.Progress.Reset == 40, "the nine zones count as left alone");
    }
    finally
    {
      SetStatic(typeof(ZoneRegen), "job", null);
      GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Clear();
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ E: borders follow what happened

  private static void BorderTests()
  {
    Section("borders follow the zones that were actually cleared");
    var k = Z(1, 0);
    var z0 = Z(0, 0);

    // A run that is stopped before its borders: the next one mends them.
    var world = new FakeWorld();
    uint next = 1;
    world.Fill([z0, Z(9, 9)], 3, ref next);
    world.Add(k, O(50, Kind.Piece));
    world.Generated.UnionWith([z0, k, Z(9, 9)]);
    var memory = new ZoneReset.Memory();
    var plan = ZoneReset.MakePlan(world.Generated, [Z(2, 0)], [z0], null, memory.Pending);
    var progress = new Progress();
    var work = new ZoneReset.Work(world, plan, new Budget(4, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    steps.MoveNext();
    work.FinishInFlight();
    C(world.Generated.SetEquals([k, Z(9, 9)]) && memory.Cleared.SetEquals([z0]) && memory.Pending.Count == 0 && world.BorderCalls.Count == 0,
      "(the first run emptied and finished one zone and stopped before the borders)");
    plan = ZoneReset.MakePlan(world.Generated, [Z(2, 0)], [Z(9, 9)], null, memory.Pending);
    var (_, progress2, _) = Go(world, plan, Roomy(), memory);
    C(world.BorderCalls.Count == 1 && world.BorderCalls[0].Zone.Equals(k) && world.BorderCalls[0].Edges == Edges.West, "the next run mends the ground beside the zone the first one cleared, though its plan resets nothing there");
    C(memory.Cleared.Count == 0 && progress2.Reset == 1, "(the pass is complete: nothing is owed after it)");
    Go(world, ZoneReset.MakePlan(world.Generated, [Z(2, 0)], [], null, memory.Pending), Roomy(), memory);
    C(world.BorderCalls.Count == 1, "and a run after that does not mend it again");

    // A zone left alone at its turn: its neighbours are not cleared for nothing, and it is mended against the zone cleared beside it.
    world = new FakeWorld();
    var s = Z(0, 0);
    var w = Z(1, 0);
    var kept = Z(-1, 0);
    world.Add(s, O(1), O(2)).Add(w, O(3), O(4)).Add(kept, O(5));
    world.Add(Z(-2, 0), O(6, Kind.Piece));
    world.Generated.UnionWith([s, w, kept]);
    world.OnList = zone =>
    {
      if (zone.Equals(s) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(world.Generated, [Z(-2, 0)], [w], null, memory.Pending);
    C(Show(plan.Reset) == "1,0 0,0" && Sorted(plan.Kept) == "-1,0", "(the plan resets the two zones east of the base; the base's neighbour stays)");
    (_, progress, _) = Go(world, plan, Roomy(), memory);
    C(progress.Skipped == 1 && progress.Reset == 1, "(the zone nearer the base is left alone at its turn: a piece appeared in it)");
    C(world.BorderCalls.Count == 1 && world.BorderCalls[0].Zone.Equals(s) && world.BorderCalls[0].Edges == Edges.East,
      "the zone left alone is mended against the zone cleared beside it; the base's neighbour, which meets no cleared zone, is not touched");

    // Zones cleared by an earlier run and generated again since are new: they are not mended against each other.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, k], 2, ref next);
    world.Generated.UnionWith([z0, k]);
    memory = new ZoneReset.Memory();
    memory.Cleared.UnionWith([z0, k]);
    plan = ZoneReset.MakePlan(world.Generated, [z0], [z0], null, memory.Pending);
    (_, progress, _) = Go(world, plan, Roomy(), memory);
    C(plan.Reset.Count == 0 && world.BorderCalls.Count == 0 && memory.Cleared.Count == 0, "two zones cleared earlier and generated again are not mended against each other, and the pass still completes");

    // Zones cleared in a run and finished are not mended: a finished zone has no ground edits left.
    world = new FakeWorld();
    next = 1;
    world.Fill(Square(1), 1, ref next);
    world.Generated.UnionWith(Square(1));
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(world.Generated, [], [Z(0, 0)], null, memory.Pending);
    Go(world, plan, Roomy(), memory);
    C(world.BorderCalls.Count == 0, "when every generated zone is reset there is nothing to mend");
  }

  // ------------------------------------------------------------------------------------------------ E-peers

  private static void PeerTests()
  {
    Section("players from other machines keep the zones their game has loaded");
    C(Rules.PeerRadius(0) == 1 && Rules.PeerRadius(1) == 1 && Rules.PeerRadius(2) == 2 && Rules.PeerRadius(5) == 5, "the radius is the near simulation distance, and never less than a piece's");
    C(Rules.FallbackRadius([]) == 1 && Rules.FallbackRadius([0]) == 1 && Rules.FallbackRadius([2, 3, 1]) == 3, "for a player no peer is found for it is the widest of those connected, at least a piece's");
    C(Rules.PeerRadius(SimulationDistance.OriginalNear) == 2, "the game's default near distance is 2: a 5 x 5");

    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(5))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      // Peer 700 sees 3 zones around it, from (0, 0).
      var one = w.Add(Z(0, 0), "Player", user: 700L);
      one.SetOwner(700L);
      w.Peer(one, 700L).m_simulationDistance = new SimulationDistance(3, 2);
      // Peer 701 has not entered the world: where its client says it is, with the near distance of 1.
      var loading = Make<ZNetPeer>();
      loading.m_uid = 701L;
      loading.m_refPos = new Vector3(-5 * 64f, 30f, 5 * 64f);
      loading.m_simulationDistance = new SimulationDistance(1, 2);
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(loading);
      // Peer 702 reports nowhere.
      var nowhere = Make<ZNetPeer>();
      nowhere.m_uid = 702L;
      nowhere.m_simulationDistance = new SimulationDistance(2, 2);
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(nowhere);
      // A player whose peer has left: nobody owns it; it gets the widest of those connected (3).
      var orphan = w.Add(Z(5, -5), "Player", user: 999L);
      orphan.SetOwner(999L);
      // The player at this machine, in a dungeon under (-5, -5), is nobody's peer either.
      var me = w.Add(Z(-5, -5), "Player", user: 600L, y: 5100f);
      SetField(w.Net, "m_characterID", me.m_uid);

      // The characters the game knows and where each client says its player is (the orphan is found only by the scan, in the sectors).
      var expectedLive = new[] { (Z(0, 0), 3), (Z(-5, 5), 1), (Z(-5, -5), 3) };
      var expectedKept = new[] { (Z(0, 0), 3), (Z(-5, 5), 1), (Z(5, -5), 3), (Z(-5, -5), 3) };
      var keptExpected = Square(5).Where(z => expectedKept.Any(e => Math.Max(Math.Abs(z.x - e.Item1.x), Math.Abs(z.y - e.Item1.y)) <= e.Item2)).ToHashSet();
      var live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => (p.Zone, p.Radius)).Distinct().OrderBy(p => p.Zone.x).ThenBy(p => p.Zone.y).ToList();
      C(live.SequenceEqual(expectedLive.OrderBy(p => p.Item1.x).ThenBy(p => p.Item1.y)), "the live look finds each player with the radius of their game: " + string.Join(" ", live.Select(p => $"{p.Zone.x},{p.Zone.y}/{p.Radius}")));
      var (_, _, job) = Regenerate(w);
      C(keptExpected.Count == 49 + 4 + 12 + 12, $"(the squares overlap in 8 zones: {keptExpected.Count} zones are kept)");
      C(w.Generated.SetEquals(keptExpected), $"the scan keeps the squares of the game's near distance: a 7 x 7 for peer 700, a 3 x 3 for peer 701 where its client says it is, nothing for peer 702, 7 x 7 for the player nobody owns and for the local one in a dungeon (left: {w.Generated.Count})");
      C(job.Progress.Reset == 121 - keptExpected.Count, "the others are reset");
    }
    finally
    {
      World.Close();
    }

    // Alone with a dungeon: a piece's radius.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(3))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var me = w.Add(Z(0, 0), "Player", user: 600L, y: 5100f);
      SetField(w.Net, "m_characterID", me.m_uid);
      var live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().ToList();
      C(live.Count == 1 && live[0].Radius == 1, "the player at this machine in a dungeon with nobody else connected keeps a piece's radius");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ F: frame and bandwidth limits

  private static void LimitTests()
  {
    Section("frame and bandwidth limits");
    // The clock is looked at inside a zone, every 32 objects.
    double now = 0;
    var world = new FakeWorld();
    uint next = 1;
    world.Fill([Z(0, 0)], 200, ref next);
    world.OnDestroy = _ => now += 0.1;
    var (_, progress, frames) = Go(world, ZoneReset.MakePlan([Z(0, 0)], [], []), new Budget(1_000_000, 3, () => now));
    C(progress.Destroyed == 200 && world.DestroysByFrame.Values.Max() == ZoneReset.Work.SliceSize, $"a zone of 200 objects at 0.1 ms each against 3 ms a frame: a frame destroys {world.DestroysByFrame.Values.Max()} objects, the clock is looked at every {ZoneReset.Work.SliceSize}");
    C(frames == 7, $"so the zone takes 7 frames ({frames})");

    // Within the object count the slices are as big as the room.
    now = 0;
    world = new FakeWorld();
    next = 1;
    world.Fill([Z(0, 0)], 100, ref next);
    (_, progress, frames) = Go(world, ZoneReset.MakePlan([Z(0, 0)], [], []), new Budget(50, 1_000_000, () => now));
    C(progress.Destroyed == 100 && world.DestroysByFrame[0] == 49 && world.DestroysByFrame.Values.Max() == 50 && frames == 3, $"a frame of 50 objects takes 49 of them in the frame that lists the zone, and 50 in the next ({frames} frames)");

    // The per-frame cap follows the wall time with players connected: about 6000 objects a second.
    double fraction = 0;
    C(ZoneRegen.PerFrameWithPlayers(1 / 60.0, ref fraction) == 100, "at 60 frames a second a frame may destroy 100 objects");
    C(ZoneRegen.PerFrameWithPlayers(0.5, ref fraction) == 100 && Math.Abs(fraction) <= 0.5, "after a hitch of half a second the cap is still 100, and no debt is carried");
    fraction = 0;
    C(ZoneRegen.PerFrameWithPlayers(1 / 144.0, ref fraction) == 42 && ZoneRegen.PerFrameWithPlayers(1 / 144.0, ref fraction) == 41, "at 144 frames a second it is 42 and 41 by turns (41.67 each)");
    long total = 0;
    fraction = 0;
    for (int i = 0; i < 1000; i++)
      total += ZoneRegen.PerFrameWithPlayers(0.0077, ref fraction);
    C(Math.Abs(total - 46200) <= 1, $"over 1000 frames of 7.7 ms the objects add up to the 6000 a second ({total} for 46200)");
    fraction = 0;
    C(ZoneRegen.PerFrameWithPlayers(0.0001, ref fraction) == 1 && ZoneRegen.PerFrameWithPlayers(0, ref fraction) == 1 && ZoneRegen.PerFrameWithPlayers(-1, ref fraction) == 1, "a frame destroys at least one object, whatever the clock says");
    fraction = 0;
    C(ZoneRegen.PerFrameWithPlayers(double.NaN, ref fraction) == 1 && ZoneRegen.PerFrameWithPlayers(double.PositiveInfinity, ref fraction) == 1 && double.IsFinite(fraction) && ZoneRegen.PerFrameWithPlayers(1 / 60.0, ref fraction) == 100,
      "a clock that gives no number does not poison the fraction carried: the next frame is as it should be");
    long slow = 0;
    fraction = 0;
    for (int i = 0; i < 300; i++)
      slow += ZoneRegen.PerFrameWithPlayers(1 / 30.0, ref fraction);
    C(slow == 300 * 100, "at 30 frames a second the cap of 100 a frame holds: 3000 a second, under the allowance");
    C(ZoneRegen.ObjectsPerSecondWithPlayers * 12 <= 150 * 1024 / 2, $"{ZoneRegen.ObjectsPerSecondWithPlayers} objects of 12 bytes a second are {ZoneRegen.ObjectsPerSecondWithPlayers * 12 / 1000} KB/s of the 153 KB/s a connection gets: at most half");

    // On the game's side: alone it is the fixed count, with a peer it follows the clock.
    ZDOExtraData.Reset();
    var w = new World();
    var oldDelta = ZoneRegen.DeltaTime;
    try
    {
      ZoneRegen.DeltaTime = () => throw new InvalidOperationException("alone, the clock is not asked");
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame, "alone in the world a frame is 1500 objects");
      var remote = w.Add(Z(0, 0), "Player", user: 700L);
      w.Peer(remote, 700L);
      float dt = 1f / 144f;
      ZoneRegen.DeltaTime = () => dt;
      var a = ZoneRegen.ObjectsPerFrameNow();
      var b = ZoneRegen.ObjectsPerFrameNow();
      C(a + b == 83 && a >= 41 && b >= 41, $"with a peer connected the cap follows the frame time ({a}, {b})");
      dt = 0.02f;
      C(ZoneRegen.ObjectsPerFrameNow() == 100, "and is at most 100");
    }
    finally
    {
      ZoneRegen.DeltaTime = oldDelta;
      World.Close();
    }

    // Sets and dictionaries of zones hash every zone differently.
    var big = Square(139);
    C(big.Count == 279 * 279, $"(a world of {big.Count:N0} zones)");
    int distinctGame = big.Select(z => z.GetHashCode()).Distinct().Count();
    int distinctOurs = big.Select(z => ZoneReset.Comparer.GetHashCode(z)).Distinct().Count();
    C(distinctGame < 1000 && distinctOurs == big.Count, $"the game's hash gives these zones {distinctGame} values; the comparer gives each its own ({distinctOurs})");
    C(ZoneReset.Comparer.Equals(Z(-3, 4), Z(-3, 4)) && !ZoneReset.Comparer.Equals(Z(-3, 4), Z(4, -3)) && ZoneReset.Comparer.GetHashCode(Z(-3, 4)) != ZoneReset.Comparer.GetHashCode(Z(4, -3)),
      "(zones that differ by swapping x and y are different keys, which x ^ y would not tell apart)");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var plan = ZoneReset.MakePlan(big, Enumerable.Range(0, 500).Select(i => (ZoneReset.Protector)Z(i % 270 - 135, i * 7 % 270 - 135)), [Z(0, 0)]);
    watch.Stop();
    C(plan.Generated == big.Count && plan.Reset.Count + plan.Kept.Count == big.Count, $"a plan of {big.Count:N0} zones ({watch.ElapsedMilliseconds} ms)");
    System.Console.WriteLine($"  (a plan of {big.Count:N0} zones with 500 protectors took {watch.ElapsedMilliseconds} ms here; not asserted: the hash of the keys is)");
    // The sets of a plan use the comparer.
    C(plan.Kept.Comparer is ZoneReset.ZoneComparer && plan.Covered.Comparer is ZoneReset.ZoneComparer && new ZoneReset.Memory().Pending.Comparer is ZoneReset.ZoneComparer && ZoneReset.Dilate([Z(0, 0)], 1).Comparer is ZoneReset.ZoneComparer,
      "the sets of a plan, of the memory, and of a dilation are built with the comparer");

    // The work on a big plan is not quadratic: 20,000 zones in a row of squares, all empty, in one go.
    var many = Square(70);
    world = new FakeWorld();
    var bigPlan = ZoneReset.MakePlan(many, [], [Z(0, 0)]);
    (_, progress, frames) = Go(world, bigPlan, Roomy());
    C(progress.Reset == many.Count && frames == 1, $"{many.Count:N0} empty zones are reset in one frame when the frame allows it");
  }

  // ------------------------------------------------------------------------------------------------ G: a spawner's creature, as it is when the spawner goes

  private static void SpawnerTests()
  {
    Section("a spawner's creature is read when the spawner is destroyed");
    var a = Z(0, 0);
    var world = new FakeWorld();
    // The spawner is listed with nothing; a tree before it in the zone goes first, and by then the spawner has made a creature.
    world.Add(a, O(1), O(2));
    world.Add(Z(4, 0), O(10));
    world.OnDestroy = o =>
    {
      if (o.Id.Equals(Id(1)))
        world.Links[Id(2)] = Id(10);
    };
    var plan = ZoneReset.MakePlan([a, Z(4, 0)], [], [a]);
    var (_, progress, _) = Go(world, plan, Roomy());
    C(world.Destroyed.Contains(Id(10)) && progress.Spawned == 1, "a creature a spawner made after its zone was listed goes with it");

    // A creature standing where somebody is near now stays, though the plan would have reset that zone.
    world = new FakeWorld();
    world.Add(a, O(1));
    world.Add(Z(6, 0), O(10));
    world.Spawns(1, 10);
    world.Live.Add(new ZoneReset.Protector(Z(6, 0), 1));
    (_, progress, _) = Go(world, ZoneReset.MakePlan([a, Z(6, 0)], [], [a]), Roomy());
    C(!world.Destroyed.Contains(Id(10)) && progress.SpawnedKept == 1 && progress.Spawned == 0 && progress.Skipped == 1, "a creature in a zone where a player is near stays, and so does the zone");

    // And the reverse: a creature it no longer holds stays.
    world = new FakeWorld();
    world.Add(a, O(1), O(2));
    world.Add(Z(4, 0), O(10));
    world.Links[Id(2)] = Id(10);
    world.OnDestroy = o =>
    {
      if (o.Id.Equals(Id(1)))
        world.Links.Remove(Id(2));
    };
    (_, progress, _) = Go(world, ZoneReset.MakePlan([a, Z(4, 0)], [], [a]), Roomy());
    C(progress.Spawned == 0, "a creature it let go of before it was destroyed is not taken along");
  }

  // ------------------------------------------------------------------------------------------------ H: what it says

  private static void TextTests()
  {
    Section("what it says");
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(1, 0), Z(5, 5)], [Z(-1, 0)], [], [LocIn(0, 0, 20, 0, 20)]);
    var start = ZoneReset.StartLine(plan);
    C(start.Contains("resetting 1 of 3 generated zones; 2 left alone (near something a player built or worked on, a tombstone, or a player; 1 of them to keep a location whole)."), "the start line names ground work and the zones kept for a location: " + start);
    var without = ZoneReset.StartLine(ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]));
    C(without.Contains("9 left alone (near something a player built or worked on, a tombstone, or a player).") && !without.Contains("location"), "with no location involved it says nothing of one: " + without);

    var progress = new Progress { Total = 10, Done = 10, Reset = 7, Skipped = 3, SkippedForLocations = 2, Destroyed = 5 };
    var finish = ZoneReset.FinishLine(progress, 1.5, 0);
    C(finish.Contains("1 zone was left alone after all") && finish.Contains("2 more zones were left alone to keep a location whole"), "the finish line tells the zones left alone for their location from the others: " + finish);
    progress = new Progress { Total = 10, Done = 10, Reset = 8, Skipped = 2, SkippedForLocations = 2, Destroyed = 5 };
    finish = ZoneReset.FinishLine(progress, 1.5, 0);
    C(!finish.Contains("left alone after all") && finish.Contains("2 zones were left alone to keep a location whole"), "... and has no first clause when there are none: " + finish);

    var words = new[] { start, without, finish };
    string[] banned = ["Upgrade World", "Expand World", "replace", "obsolete", "no longer need"];
    C(words.All(w => banned.All(x => !w.Contains(x, StringComparison.OrdinalIgnoreCase))), "none of the lines names or replaces another mod");

    // The strings in the code: read from the compiled methods.
    var request = Strings(typeof(ZoneRegen).GetMethod("Request", Any)!);
    C(request.Contains("starting over") && !request.Any(t => t.Contains("settings changed")), "a second request logs that it starts over, and does not claim the settings changed");
    var canRun = Strings(typeof(ZoneRegen).GetMethod("CanRun", Any)!);
    C(canRun.Contains("it runs on the machine that runs the world: in single player, or on the host.") && canRun.Contains("load a world first."), "the refusal on a client says it runs where the world runs: " + string.Join(" | ", canRun));
  }

  // ------------------------------------------------------------------------------------------------ the patches

  private static void PatchTests()
  {
    Section("the Harmony patches");
    var type = typeof(ZoneRegenPatch);
    C(type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 1, "ZoneRegenPatch is a [HarmonyPatch] class, so PatchAll applies it at load");
    var patches = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0).ToList();
    C(patches.Count == 3, "it holds three patches");
    var expected = new (string Name, Type Target, string Method, Type Kind)[]
    {
      ("SaveWorldPrefix", typeof(ZNet), "SaveWorld", typeof(HarmonyPrefix)),
      ("PrepareSavePostfix", typeof(ZoneSystem), "PrepareSave", typeof(HarmonyPostfix)),
      ("SetCreatorPostfix", typeof(Piece), "SetCreator", typeof(HarmonyPostfix)),
    };
    foreach (var (name, target, method, kind) in expected)
    {
      var patch = patches.SingleOrDefault(m => m.Name == name);
      var info = patch?.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().SingleOrDefault()?.info;
      MethodBase? original = null;
      try
      {
        original = AccessTools.Method(target, method);
      }
      catch (AmbiguousMatchException)
      {
      }
      C(patch != null && info?.declaringType == target && info.methodName == method && patch.GetCustomAttributes(kind, false).Length == 1 && original != null,
        $"{name}: a {kind.Name.Replace("Harmony", "")} of {target.Name}.{method}, which exists once in the installed game");
    }
    var save = AccessTools.Method(typeof(ZNet), "SaveWorld");
    C(save.GetParameters().Length == 1 && save.GetParameters()[0].ParameterType == typeof(bool) && !save.IsStatic, "(ZNet.SaveWorld(bool sync) is an instance method; the prefix takes none of its arguments)");
    C(type.GetMethod("SaveWorldPrefix", BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters().Length == 0, "(and has none)");
    var postfix = type.GetMethod("PrepareSavePostfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    C(postfix.GetParameters().Single().Name == "__instance" && postfix.GetParameters().Single().ParameterType == typeof(ZoneSystem), "(the postfix takes the ZoneSystem as __instance)");
    var creator = AccessTools.Method(typeof(Piece), "SetCreator");
    C(creator.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(["Int64", "PlatformUserID"]), "(Piece.SetCreator(long, PlatformUserID))");

    // The prefix does nothing when no run has left anything.
    var calls = 0;
    SetStatic(typeof(ZoneRegen), "flushOwed", false);
    var old = ZoneRegen.SendDestroyQueue;
    ZoneRegen.SendDestroyQueue = () => calls++;
    try
    {
      type.GetMethod("SaveWorldPrefix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
      C(calls == 0, "ZNet.SaveWorld's prefix with no run under way and nothing left in the queue sends nothing");
      // Something this machine destroyed and the game has not sent yet is sent by the first save after the run, once.
      SetStatic(typeof(ZoneRegen), "flushOwed", true);
      type.GetMethod("SaveWorldPrefix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
      type.GetMethod("SaveWorldPrefix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
      C(calls == 1, "... but what a finished run left in the queue is sent by the first save after it, once");
    }
    finally
    {
      ZoneRegen.SendDestroyQueue = old;
      SetStatic(typeof(ZoneRegen), "flushOwed", false);
    }
  }

  // ------------------------------------------------------------------------------------------------ the game's code the fixes rely on

  private static void FixFactTests()
  {
    Section("the game's own code the fixes rely on (the installed game)");
    // A save copies the objects, then the zone system, in that order, before it starts its thread.
    var saveWorld = AccessTools.Method(typeof(ZNet), "SaveWorld")!;
    var calls = Callees(saveWorld);
    int zdoSave = calls.FindIndex(m => m.Name == "PrepareSave" && m.DeclaringType == typeof(ZDOMan));
    int zoneSave = calls.FindIndex(m => m.Name == "PrepareSave" && m.DeclaringType == typeof(ZoneSystem));
    int thread = calls.FindIndex(m => m.Name == "Start" && m.DeclaringType == typeof(System.Threading.Thread));
    C(zdoSave >= 0 && zoneSave > zdoSave && thread > zoneSave, "ZNet.SaveWorld: ZDOMan.PrepareSave, then ZoneSystem.PrepareSave, then the save thread starts");
    C(Callees(typeof(ZDOMan).GetMethod("PrepareSave", Any)!).Any(m => m.Name == "GetSaveClonePerChunk"), "ZDOMan.PrepareSave copies the objects of the world's lists");

    // ZoneSystem.PrepareSave copies the generated zones and the location instances; Save writes the copies.
    var instructions = PatchProcessor.GetOriginalInstructions(typeof(ZoneSystem).GetMethod("PrepareSave", Any)!).ToList();
    FieldInfo? FieldOf(object? operand) => operand as FieldInfo;
    bool Stores(string name) => instructions.Any(i => i.opcode == OpCodes.Stfld && FieldOf(i.operand)?.Name == name);
    bool Loads(string name) => instructions.Any(i => i.opcode == OpCodes.Ldfld && FieldOf(i.operand)?.Name == name);
    C(Stores("m_tempGeneratedZonesSaveClone") && Loads("m_generatedZones") && Stores("m_tempLocationsSaveClone") && Loads("m_locationInstances"), "ZoneSystem.PrepareSave copies m_generatedZones and m_locationInstances into the save clones");
    var write = PatchProcessor.GetOriginalInstructions(typeof(ZoneSystem).GetMethod("Save", Any)!).ToList();
    C(write.Any(i => i.opcode == OpCodes.Ldfld && FieldOf(i.operand)?.Name == "m_tempGeneratedZonesSaveClone") && write.Any(i => i.opcode == OpCodes.Ldfld && FieldOf(i.operand)?.Name == "m_tempLocationsSaveClone")
      && write.Any(i => i.opcode == OpCodes.Ldfld && FieldOf(i.operand)?.Name == "m_placed") && !write.Any(i => i.opcode == OpCodes.Ldfld && FieldOf(i.operand)?.Name is "m_generatedZones" or "m_locationInstances"),
      "ZoneSystem.Save writes the clones, location by location with m_placed, and never the live collections");
    var zs = typeof(ZoneSystem);
    C(zs.GetField("m_tempGeneratedZonesSaveClone", Any)?.FieldType == typeof(HashSet<Vector2s>) && zs.GetField("m_tempLocationsSaveClone", Any)?.FieldType == typeof(List<ZoneSystem.LocationInstance>), "the clones are a HashSet<Vector2s> and a List<LocationInstance>");

    // A destroyed object leaves the lists when the queue is sent, here and now: SendDestroyed handles the message for everybody at once.
    var send = typeof(ZDOMan).GetMethod("SendDestroyed", Any)!;
    C(Strings(send).Contains("DestroyZDO") && Callees(send).Any(m => m.Name == "InvokeRoutedRPC"), "ZDOMan.SendDestroyed sends the 'DestroyZDO' message through ZRoutedRpc.InvokeRoutedRPC");
    var rpc = typeof(ZDOMan).GetMethod("RPC_DestroyZDO", Any)!;
    var handle = typeof(ZDOMan).GetMethod("HandleDestroyedZDO", Any)!;
    C(Callees(rpc).Any(m => m.Name == "HandleDestroyedZDO") && Callees(handle).Any(m => m.Name == "RemoveFromSector") && Callees(handle).Any(m => m.Name == "Release"),
      "the message takes each object off its sector's list (HandleDestroyedZDO: RemoveFromSector, then the pool)");
    C(Strings(typeof(ZDOMan).GetConstructors(Any).Single(c => !c.IsStatic)).Contains("DestroyZDO"), "ZDOMan registers the 'DestroyZDO' message in its constructor");

    // Ground work leaves no object: a TerrainOp applies its operation to the compiler and goes.
    var awake = typeof(TerrainOp).GetMethod("Awake", Any)!;
    C(Callees(awake).Any(m => m.Name == "ApplyOperation") && Callees(awake).Any(m => m.Name == "Destroy" && m.DeclaringType == typeof(UnityEngine.Object)), "TerrainOp.Awake applies the operation to the zone's compiler and destroys itself");
    C(CallersOf(typeof(Piece).GetMethod("SetCreator", Any)!).Callers.SequenceEqual(["Player.PlacePiece"]), "a creator is set only when a player places a piece (a grown crop or tree is made by Plant.Grow)");
    var grow = typeof(Plant).GetMethod("Grow", Any)!;
    C(Callees(grow).Any(m => m.Name == "Instantiate") && !Callees(grow).Any(m => m.Name == "SetCreator"), "Plant.Grow makes the grown crop or tree from a prefab and gives it no creator");

    // How many zones a peer's game keeps.
    var peer = typeof(ZNetPeer).GetField("m_simulationDistance");
    C(peer?.FieldType == typeof(SimulationDistance) && typeof(SimulationDistance).GetProperty("NearSimulationDistance")?.PropertyType == typeof(int), "ZNetPeer.m_simulationDistance and SimulationDistance.NearSimulationDistance");
    C(typeof(ZDO).GetMethod("GetOwner", Any)?.ReturnType == typeof(long), "ZDO.GetOwner() is the owner's peer id");
    var ownerOf = typeof(ZNetPeer).GetField("m_uid");
    C(ownerOf?.FieldType == typeof(long), "ZNetPeer.m_uid is the id a ZDO's owner is");

    // A location's reach.
    var location = typeof(ZoneSystem.ZoneLocation);
    C(location.GetField("m_exteriorRadius")?.FieldType == typeof(float) && location.GetField("m_interiorRadius")?.FieldType == typeof(float), "ZoneLocation.m_exteriorRadius and m_interiorRadius");
    var random = PatchProcessor.GetOriginalInstructions(typeof(ZoneSystem).GetMethod("GetRandomPointInZone", Any)!).ToList();
    C(random.Count(i => i.opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.operand) == -32f) >= 1 && random.Count(i => i.opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.operand) == 32f) >= 1,
      "the game keeps a location's radius inside its zone when it picks the spot (GetRandomPointInZone); a pin of a map is not kept that way");
    var place = PatchProcessor.GetOriginalInstructions(typeof(ZoneSystem).GetMethod("PlaceLocations", Any)!).ToList();
    C(place.Any(i => i.operand is MethodInfo m && m.Name == "GetGroundData") && place.Any(i => i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f.Name == "m_placed"),
      "PlaceLocations puts the location on the ground itself when it places it, and marks it placed");
    var thePin = typeof(BC).GetNestedType("ZoneSystemPatch", BindingFlags.NonPublic)!.GetMethod("TryRegister", Any)!;
    C(Callees(thePin).Any(m => m.Name == "RegisterLocation") && !Callees(thePin).Any(m => m.Name == "Clamp" || m.Name == "Max" || m.Name == "Min"), "Better Continents registers a map's pin as it is, without keeping it inside its zone");
  }
}

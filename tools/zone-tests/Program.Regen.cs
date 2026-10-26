// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).
//
// "bc regen" with words (ZoneRegenFilter.cs): the words and their errors, which zones a filter lets through, a plan made over a filter
// (a zone left out is a zone left alone, and a location that reaches into one is kept whole), the lines of a start and of a dry run,
// the command's refusals, the filter through GameUtils, a whole run of a made-up world with a filter and with "dry"; and "bc genloc"
// (Genloc.cs): its pins word.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static void RegenWordsTests()
  {
    Section("bc regen: the words, the filters, the dry run");
    Words();
    Eligibility();
    FilteredPlans();
    Lines();
    CommandRefusals();
    FilteredRuns();
    GenLocTests();
  }

  private static RegenFilter? Parse(string? text, out string error) => RegenFilter.Parse(text, out error);

  private static void Words()
  {
    C(Parse(null, out var e0) == null && e0 == "" && Parse("   ", out e0) == null && e0 == "", "no words: no filter, no error");
    var dry = Parse("dry", out var e1)!;
    C(dry != null && e1 == "" && dry.Dry && !dry.Narrows, "'dry' alone counts the whole world");
    C(Parse("DRY", out _)!.Dry, "any case");
    var f = Parse("biome=AshLands min=1000 max=3000 pos=10,-20 chance=0.25", out var e2)!;
    C(f != null && e2 == "" && f.Biomes == Heightmap.Biome.AshLands && f.Min == 1000f && f.Max == 3000f && f.PosX == 10f && f.PosZ == -20f && f.Chance == 0.25 && f.Narrows && !f.Dry,
      "biome, min, max, pos and chance are read");
    f = Parse("biome=meadows,Black_Forest", out _)!;
    C(f != null && f.Biomes == (Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest), "biome names: any case, comma between, spaces and underscores loosely");
    f = Parse("radius=500 zone=3,-4 chance=50", out _)!;
    C(f != null && f.Max == 500f && f.Zone.HasValue && f.Zone.Value.Equals(Z(3, -4)) && f.Chance == 0.5, "radius is max; zone; a chance past 1 is a percentage");
    C(Parse("chance=50%", out _)!.Chance == 0.5 && Parse("chance=1", out _)!.Chance == 1.0 && Parse("chance=0", out _)!.Chance == 0.0, "50% and 1 and 0");
    C(Parse("dry zone=1,1", out _)!.Dry && Parse("zone=1,1 dry", out _)!.Dry, "dry may come anywhere among the words");

    foreach (var (text, part) in new[]
    {
      ("hlp", "does not know 'hlp'"),
      ("now please", "does not know 'now'"),
      ("biome=Nowhere", "'Nowhere' is not a biome"),
      ("biome=", "does not know 'biome='"),
      ("biome=,", "biome= needs a name"),
      ("min=-5", "min= takes a distance"),
      ("max=far", "max= takes a distance"),
      ("max=NaN", "max= takes a distance"),
      ("min=5 min=6", "'min' is given twice"),
      ("min=500 max=100", "max (100 m) is less than min (500 m)"),
      ("zone=1.5,2", "zone= takes two whole numbers"),
      ("zone=1", "zone= takes two whole numbers"),
      ("pos=1", "pos= takes two numbers"),
      ("chance=150", "chance= takes a number"),
      ("chance=-1", "chance= takes a number"),
      ("colour=red", "does not know 'colour='"),
    })
    {
      var none = Parse(text, out var error);
      C(none == null && error.Contains(part), $"'bc regen {text}' starts nothing and says: {error}");
    }
    C(Parse("hlp", out var help) == null && help.Contains("bc regen help") && help.Contains("[dry] [biome=<names>]"), "and the error says where the help is, and the words");
  }

  private static RegenFilter Make(string text, Func<float, float, Heightmap.Biome>? biome = null, params double[] rolls)
  {
    var filter = Parse(text, out var error)!;
    C(filter != null, $"'{text}' parses ({error})");
    if (biome != null)
      filter!.BiomeAt = biome;
    if (rolls.Length > 0)
    {
      int i = 0;
      filter!.Roll = () => rolls[i++ % rolls.Length];
    }
    return filter!;
  }

  private static void Eligibility()
  {
    // Meadows west of the centre line, AshLands east of it.
    Heightmap.Biome Split(float x, float z) => x < 0f ? Heightmap.Biome.Meadows : Heightmap.Biome.AshLands;
    var ash = Make("biome=AshLands", Split);
    C(ash.Eligible(Z(1, 0)) && ash.Eligible(Z(0, 0)) && !ash.Eligible(Z(-1, 0)), "biome: a zone is in when any corner is (the zone on the line is half in, so in), a zone wholly in another biome is out");
    var both = Make("biome=AshLands,Meadows", Split);
    C(both.Eligible(Z(-5, 0)) && both.Eligible(Z(5, 0)), "two biomes: either");
    var ring = Make("min=100 max=200");
    C(ring.Eligible(Z(2, 0)) && ring.Eligible(Z(0, -3)) && !ring.Eligible(Z(1, 0)) && !ring.Eligible(Z(4, 0)) && !ring.Eligible(Z(0, 0)),
      "min and max: metres from the centre to the middle of the zone (64 m, 128 m and 192 m, 256 m)");
    var around = Make("pos=128,0 max=10");
    C(around.Eligible(Z(2, 0)) && !around.Eligible(Z(2, 1)) && !around.Eligible(Z(1, 0)), "pos: the distance is from there");
    var one = Make("zone=1,1");
    C(one.Eligible(Z(1, 1)) && !one.Eligible(Z(1, 0)) && !one.Eligible(Z(-1, -1)), "zone: that one");
    var combined = Make("biome=AshLands min=100", Split);
    C(combined.Eligible(Z(3, 0)) && !combined.Eligible(Z(-3, 0)) && !combined.Eligible(Z(1, 0)), "words together: all of them hold");
    var chance = Make("chance=0.5", null, 0.1, 0.9, 0.4);
    bool a = chance.Eligible(Z(0, 0)), b = chance.Eligible(Z(1, 0)), c = chance.Eligible(Z(2, 0));
    C(a && !b && c, "chance: each zone has its own draw");
    C(chance.Eligible(Z(0, 0)) == a && chance.Eligible(Z(1, 0)) == b && chance.Eligible(Z(2, 0)) == c, "and keeps it: the same zone, the same answer, however often it is asked (a plan made twice agrees with itself)");
    var never = Make("chance=0", null, 0.0);
    C(!never.Eligible(Z(0, 0)), "chance=0 resets nothing");
    var always = Make("chance=1", null, 0.999999);
    C(always.Eligible(Z(0, 0)) && !always.Narrows, "chance=1 is every zone");
    C(Make("biome=AshLands min=1000 max=3000 pos=10,-20 chance=25").Describe() == "biome AshLands, 1000 to 3000 m from 10,-20, 25% of them", "the filter in words: " + Make("biome=AshLands min=1000 max=3000 pos=10,-20 chance=25").Describe());
    C(Make("zone=2,3 max=500").Describe() == "zone 2,3, within 500 m of the centre", "... " + Make("zone=2,3 max=500").Describe());
  }

  private static void FilteredPlans()
  {
    // No filter: the plan is as it was.
    var plan = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)]);
    C(plan.Filtered == 0 && plan.Reset.Count == 49 && plan.Kept.Count == 0, "no filter: all 49 zones are reset");
    var line = ZoneReset.StartLine(plan);
    C(!line.Contains("outside what was asked"), "and the start line is the old one: " + line);

    Func<Vector2s, bool> east = z => z.x >= 2;
    plan = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)], null, null, east);
    C(plan.Reset.Count == 14 && plan.Reset.All(z => z.x >= 2) && plan.Kept.Count == 35 && plan.Filtered == 35 && plan.Generated == 49, "a filter: only the zones it lets through are reset, the rest are left alone and counted as such");
    line = ZoneReset.StartLine(plan);
    C(line.Contains("resetting 14 of 49 generated zones") && line.Contains("35 left alone (35 outside what was asked for)") && !line.Contains("more near"), "its start line says so: " + line);
    C(plan.Covered.Contains(Z(-3, -3)) && plan.Covered.Contains(Z(2, 0)) == false, "a zone left out covers itself (a creature standing there stays), and the zones around it are not covered");

    // What protects still protects: a protector at 3,0 keeps the 3 x 3 around it.
    plan = ZoneReset.MakePlan(Square(3), [Z(3, 0)], [Z(0, 0)], null, null, east);
    C(plan.Reset.Count == 8 && !plan.Reset.Contains(Z(3, 1)) && !plan.Reset.Contains(Z(2, -1)) && plan.Filtered == 35 && plan.Kept.Count == 41,
      "a protector still keeps its zones inside the filter's area (14 - 6 = 8 reset)");
    line = ZoneReset.StartLine(plan);
    C(line.Contains("41 left alone (35 outside what was asked for; 6 more near something a player built or worked on, a tombstone, or a player)"), "and the line tells the two apart: " + line);

    // A location that reaches over the filter's border is kept whole: 80 m around the middle of zone 2,0 touches 1,0 (out), 3,0 and 2,+-1.
    var location = new ZoneReset.PlacedLocation(Z(2, 0), 128f, 0f, 80f);
    var withLocation = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)], [location], null, east);
    var touched = ZoneReset.TouchedZones(location);
    C(touched.Contains(Z(1, 0)) && touched.Contains(Z(3, 0)) && touched.Contains(Z(2, 1)), "(the location touches zones on both sides of the filter's border: " + Show(touched) + ")");
    C(touched.All(z => withLocation.Kept.Contains(z)) && withLocation.KeptForLocations > 0, "the location is kept whole: every zone it touches is left alone, though most are inside the filter");
    C(withLocation.Reset.Count == 14 - touched.Count(z => z.x >= 2), "and no other zone is spared");
    var without = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)], [location], null, null);
    C(without.Reset.Count == 49, "(without the filter the location's zones are all reset, as before)");
    line = ZoneReset.StartLine(withLocation);
    C(line.Contains("to keep a location whole") || line.Contains("outside what was asked for"), "the line is still told: " + line);

    // A zone an earlier run emptied and left waiting is finished whatever the filter says.
    plan = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)], null, [Z(-3, -3)], east);
    C(plan.Reset.Contains(Z(-3, -3)) && plan.Carried.Contains(Z(-3, -3)), "a carried zone is reset even outside the filter");

    // Nothing the filter lets through.
    plan = ZoneReset.MakePlan(Square(1), [], [Z(0, 0)], null, null, _ => false);
    C(plan.Reset.Count == 0 && plan.Kept.Count == 9, "a filter that lets nothing through resets nothing");
    C(ZoneReset.StartLine(plan).Contains("no generated zone to reset of 9 generated zones; 9 left alone (9 outside what was asked for)"), "and says so: " + ZoneReset.StartLine(plan));
  }

  private static void Lines()
  {
    Heightmap.Biome Split(float x, float z) => x < 0f ? Heightmap.Biome.Meadows : Heightmap.Biome.AshLands;
    var filter = Make("dry min=100", Split);
    var plan = ZoneReset.MakePlan(Square(3), [], [Z(0, 0)], null, null, filter.Eligible);
    var lines = ZoneReset.DryRunLines(plan, filter, filter.BiomeAt);
    C(lines[0].StartsWith("Zone regeneration, dry run: would reset ") && lines[0].Contains(" of 49 generated zones"), "a dry run's first line: " + lines[0]);
    C(lines.Any(l => l == "Asked for: 100 m or more from the centre."), "it says what was asked for");
    C(lines.Any(l => l.StartsWith("Zones to reset, by the biome at their middle: ") && l.Contains("AshLands") && l.Contains("Meadows")), "the biomes of the zones to reset: " + lines.FirstOrDefault(l => l.StartsWith("Zones to reset")));
    C(lines.Any(l => l.StartsWith("Nearest first (zone, metres from the centre): ") && l.Contains("0,2 (128 m)") && l.Contains("The whole list is in the log.")), "the nearest, and where the whole list is: " + lines.FirstOrDefault(l => l.StartsWith("Nearest")));
    C(lines[^1] == "Nothing was changed. 'bc regen min=100' does it.", "and that nothing was changed: " + lines[^1]);
    C(ZoneReset.ZoneListLine(plan).StartsWith("Zone regeneration, dry run: zones to reset: ") && ZoneReset.ZoneListLine(plan, 3).Contains("(the first 3 of "), "the log gets the list, capped");
    var all = Make("dry", Split);
    var wholeWorld = ZoneReset.DryRunLines(ZoneReset.MakePlan(Square(1), [], [Z(0, 0)]), all, all.BiomeAt);
    C(wholeWorld[0].Contains("would reset 9 of 9 generated zones") && !wholeWorld.Any(l => l.StartsWith("Asked for")) && wholeWorld[^1] == "Nothing was changed. 'bc regen' does it.", "a plain 'bc regen dry' counts the world: " + string.Join(" | ", wholeWorld));
    var none = ZoneReset.DryRunLines(ZoneReset.MakePlan(Square(1), [], [Z(0, 0)], null, null, _ => false), Make("dry zone=9,9", Split), Split);
    C(none[0].Contains("dry run: no generated zone to reset") && none.Count == 3, "nothing to reset: " + string.Join(" | ", none));
    foreach (var text in lines.Concat(wholeWorld).Concat(none).Append(RegenFilter.Syntax))
      C(new[] { "replace", "obsolete", "no longer need", "Upgrade World", "Expand World" }.All(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase)), "no other mod is named: " + text);
  }

  private static void CommandRefusals()
  {
    using var seams = new Seams();
    var said = new List<string>();
    var started = new List<RegenFilter?>();
    BC.ConfigDebugResetCommand.Value = "";
    ZoneRegen.RunCommand("dry biome=Meadows", said.Add, started.Add);
    C(said.Count == 0 && started.Count == 1 && started[0]!.Dry && started[0]!.Biomes == Heightmap.Biome.Meadows, "with Debug Reset Command empty the words are handed on");
    BC.ConfigDebugResetCommand.Value = "zones_reset start";
    ZoneRegen.RunCommand("biome=Meadows", said.Add, started.Add);
    ZoneRegen.RunCommand("dry", said.Add, started.Add);
    C(started.Count == 1 && said.Count == 2 && said[0].Contains("Debug Reset Command") && said[0].Contains("zones_reset start") && said[0].Contains("takes no filters"),
      "with a console command in Debug Reset Command, words are refused and say why: " + said.FirstOrDefault());
    ZoneRegen.RunCommand("", said.Add, started.Add);
    C(started.Count == 2 && started[1] == null && said.Count == 2, "and plain 'bc regen' still goes to it");
    BC.ConfigDebugResetCommand.Value = "";

    // Through GameUtils: the filter reaches the filtered request, the plain one the old.
    var events = new List<string>();
    GameUtils.RunConsoleCommand = command => events.Add("run " + command);
    GameUtils.RequestRegeneration = () => events.Add("regenerate");
    GameUtils.RequestFilteredRegeneration = f => events.Add("regenerate " + f.Text);
    GameUtils.PokeHeightmap = _ => events.Add("poke");
    GameUtils.RegenerateZones();
    GameUtils.RegenerateZonesWith(Make("dry zone=1,1"));
    C(events.SequenceEqual(["regenerate", "regenerate dry zone=1,1"]), "GameUtils.RegenerateZones and RegenerateZonesWith: no filter, the old request; a filter, the filtered one; no terrain is poked: " + string.Join(", ", events));
  }

  private static (List<string> Lines, ZoneRegen.Job Job) RegenerateWith(World w, RegenFilter filter)
  {
    var lines = new List<string>();
    var job = new ZoneRegen.Job(lines.Add) { Filter = filter };
    var steps = ZoneRegen.Steps(job);
    while (steps.MoveNext())
      w.Flush();
    w.Flush();
    return (lines, job);
  }

  private static void FilteredRuns()
  {
    using var seams = new Seams();
    ZoneRegen.DeltaTime = () => 1f / 60f;
    ZoneRegen.FrameClock = () => 0.0;
    BC.ConfigDebugResetCommand.Value = "";
    Heightmap.Biome Split(float x, float z) => x < 0f ? Heightmap.Biome.Meadows : Heightmap.Biome.AshLands;
    World Build()
    {
      ZDOExtraData.Reset();
      var w = new World();
      foreach (var zone in Square(3))
      {
        for (int i = 0; i < 3; i++)
          w.Add(zone, "Pine", dx: i * 5f - 7f, dz: i * 3f - 4f);
        w.Add(zone, "_ZoneCtrl");
        w.Generated.Add(zone);
      }
      return w;
    }

    // One zone.
    var world = Build();
    try
    {
      var (lines, job) = RegenerateWith(world, Make("zone=2,2", Split));
      C(job.Progress.Reset == 1 && world.Generated.Count == 48 && !world.Generated.Contains(Z(2, 2)), "zone=2,2: that zone is reset and no other: " + string.Join(" | ", lines.Take(2)));
      C(world.LiveIn(Z(2, 2)).Count == 0 && Square(3).Where(z => !z.Equals(Z(2, 2))).All(z => world.LiveIn(z).Count == 4), "its objects are gone, those of every other zone are all there");
      C(lines[0].Contains("resetting 1 of 49 generated zones") && lines[0].Contains("48 left alone (48 outside what was asked for)"), "and the run says so: " + lines[0]);
    }
    finally
    {
      World.Close();
    }

    // A biome, and a distance.
    world = Build();
    try
    {
      var (_, job) = RegenerateWith(world, Make("biome=Meadows", Split));
      var reset = Square(3).Where(z => !world.Generated.Contains(z)).ToList();
      C(reset.Count == 28 && reset.All(z => z.x <= 0), "biome=Meadows: the west of the world and the column on the line (a zone half in the biome is in): 4 columns of 7: " + reset.Count);
    }
    finally
    {
      World.Close();
    }
    world = Build();
    try
    {
      var (_, job) = RegenerateWith(world, Make("min=190"));
      var reset = Square(3).Where(z => !world.Generated.Contains(z)).ToList();
      C(reset.Count == 24 && reset.All(z => Math.Max(Math.Abs(z.x), Math.Abs(z.y)) == 3 || Math.Sqrt(z.x * z.x + z.y * z.y) * 64.0 >= 190.0), "min=190: the zones 190 m or more from the centre: " + reset.Count);
    }
    finally
    {
      World.Close();
    }

    // A player's base inside the filter's area is still left alone.
    world = Build();
    try
    {
      Creator(world.Add(Z(2, 2), "piece_chest", dx: 3f, dz: 3f), 42L);
      var (lines, job) = RegenerateWith(world, Make("min=100"));
      C(world.Generated.Contains(Z(2, 2)) && world.Generated.Contains(Z(3, 3)) && world.Generated.Contains(Z(1, 1)) && !world.Generated.Contains(Z(-3, -3)),
        "a chest inside the area keeps its zone and the 8 around it, as it does in a plain 'bc regen'");
      C(world.LiveIn(Z(2, 2)).Any(zdo => zdo.GetPrefab() == "piece_chest".GetStableHashCode()), "and the chest is still there");
    }
    finally
    {
      World.Close();
    }

    // A dry run: nothing changes.
    world = Build();
    try
    {
      Creator(world.Add(Z(2, 2), "piece_chest", dx: 3f, dz: 3f), 42L);
      int objects = Square(3).Sum(z => world.LiveIn(z).Count);
      var (lines, job) = RegenerateWith(world, Make("dry min=100", Split));
      C(world.Generated.Count == 49 && Square(3).Sum(z => world.LiveIn(z).Count) == objects && world.DestroyQueue.Count == 0 && world.Sent.Count == 0, "dry: every zone is still generated, every object is still there, nothing is queued");
      C(job.Progress.Reset == 0 && job.Work == null && lines.Count >= 4 && lines[0].StartsWith("Zone regeneration, dry run: would reset ") && lines[^1].StartsWith("Nothing was changed."), "and it says what would be reset: " + string.Join(" | ", lines));
      C(lines[0].Contains("would reset 32 of 49 generated zones"), "the 9 zones within 100 m of the centre are outside the filter, the chest's 9 are protected (one is both): 49 - 17 = 32 would be reset: " + lines[0]);
    }
    finally
    {
      World.Close();
    }
  }

  private static void GenLocTests()
  {
    // "bc genloc" is Genloc.cs (the forced run); the pins word only puts the location map's pins and the start position into the registry.
    C(Genloc.Parse("pins") == Genloc.Mode.Pins && Genloc.Parse(" PINS ") == Genloc.Mode.Pins, "bc genloc pins: its word, any case");
    C(Genloc.Parse("") == Genloc.Mode.Force && Genloc.Parse("alt") == Genloc.Mode.Alt && Genloc.Parse("pins now") == Genloc.Mode.Bad, "and the other words are as before");
    C(Genloc.Usage.Contains("pins") && Genloc.Help.Any(l => l.Contains("bc genloc pins")), "the usage and the help name it");
  }

}

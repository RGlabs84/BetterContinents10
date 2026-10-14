// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// What of SeaDraw and its words can run without the engine (the shader swap itself needs a graphics device and the water bundle, and is
// checked in the game): which materials are the sea's, why the game's water is drawn instead (in what order the causes are told), that a
// refresh and the words of bc_water run engine-free, as DynamicPatch needs, and that AshlandsWater's fallback is left to the game's water.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static void DrawTests()
  {
    Section("draw: what can run without the engine");
    if (!running) return;
    // Only the sea gets Better Continents' shader: `water` (the zones) and `water_lod` (the far sea), their instances too (decision 10).
    foreach (var name in new[] { "water", "water (Instance)", "water_lod", "water_lod (Instance)" })
      Check(SeaDraw.IsSeaName(name), $"{name} is the sea");
    foreach (var name in new[] { "water_hottub", "water_hottub (Instance)", "BarberWater_mat", "skull_mudwater", "water_interior", "water_interior_cave (Instance)", "Water", "waterfall", "" })
      Check(!SeaDraw.IsSeaName(name), $"'{name}' keeps the game's shader");

    // Why the game's water is drawn: no graphics first, then no field, a world whose sea is the game's, a server's field, a player's switch, another mod's shader.
    var gameSea = BuildTestField();
    gameSea.CalmSameAsGame = gameSea.HotSameAsGame = true;
    var serverField = BuildTestField();
    serverField.Hot = null;
    var differs = BuildTestField();
    Check(SeaDraw.Cause(true, differs, false, false, null) == "this machine draws nothing", "no graphics device: nothing is drawn, whatever else holds");
    Check(SeaDraw.Cause(false, null, true, false, null) == "the sea field is not ready yet" && SeaDraw.Cause(false, null, false, false, null) == "this world's sea is the game's",
      "no field: it is on its way, or the world's sea is the game's (no field is built for it)");
    Check(SeaDraw.Cause(false, gameSea, false, true, "x") == "this world's sea is the game's own", "a field that is the game's sea: nothing to draw, before any setting counts");
    Check(SeaDraw.Cause(false, serverField, false, false, null) == "the sea field has no hot channel", "a field without the hot channel (a server's) cannot be drawn");
    Check(SeaDraw.Cause(false, differs, false, true, null) == "Own Water is off" && SeaDraw.Cause(false, differs, false, true, "another mod") == "Own Water is off",
      "Own Water off is told before another mod's shader");
    Check(SeaDraw.Cause(false, differs, false, false, "another mod has replaced the game's water shader") == "another mod has replaced the game's water shader",
      "another mod's shader on the sea is the cause when nothing before it is");
    Check(SeaDraw.Cause(false, differs, false, false, null) == null, "a field that differs from the game's, with Own Water on and no other mod in the way: nothing stands in the way");
    Check(!SeaDraw.Draws(null), "no renderer is drawn by BC's shader before anything is swapped");
    // 'bc water force on': BC's shader is drawn where the world's sea is the game's own, and nothing else is overridden.
    Check(SeaDraw.Cause(false, gameSea, false, false, null, forced: true) == null && SeaDraw.Cause(false, gameSea, false, false, null) == "this world's sea is the game's own",
      "forced on, a field that is the game's sea is drawn (not forced, it is not)");
    Check(SeaDraw.Cause(true, gameSea, false, false, null, forced: true) == "this machine draws nothing" && SeaDraw.Cause(false, null, false, false, null, forced: true) == "this world's sea is the game's"
          && SeaDraw.Cause(false, gameSea, false, true, null, forced: true) == "Own Water is off" && SeaDraw.Cause(false, serverField, false, false, null, forced: true) == "the sea field has no hot channel"
          && SeaDraw.Cause(false, gameSea, false, false, "another mod", forced: true) == "another mod",
      "forced on does not draw on a machine with no graphics, without a field or its hot channel, against Own Water off or another mod's shader");

    // A refresh and the words are engine-free.
    var settings = BC.Settings;
    var harmony = BC.HarmonyInstance;
    BC.HarmonyInstance = new HarmonyLib.Harmony("water-tests.draw");
    try
    {
      BC.Settings = World(12, 2000f, 100f);
      SeaDraw.Refresh();
      Check(SeaPatches.Snapshot.Pending && SeaField.Current == null, "a refresh (DynamicPatch's) runs without the engine, and asks for the field");
      var lines = new List<string>();
      WaterCommands.RunLine("", lines.Add, debug: true);
      Check(lines.Any(l => l.Contains("a candidate")) && lines.Any(l => l.Contains("the sea field")) && lines.Any(l => l.Contains("game's own water is drawn")),
        $"'bc water' says that BC's sea is a candidate, what the field is doing, and that the game's water is drawn ({lines.Count} lines)");
      SeaPatches.Start(BC.Settings, true);
      Check(WaitForField(60000), "the field is built");
      lines.Clear();
      WaterCommands.RunLine("", lines.Add, debug: true);
      Check(lines.Any(l => l.Contains("525 x 525 texels")) && lines.Any(l => l.Contains("Deep North")),
        "once built 'bc water' gives the field's size, spacing and shares");
      lines.Clear();
      WaterCommands.RunLine("sing", lines.Add, debug: true);
      WaterCommands.RunLine("force", lines.Add, debug: true);
      WaterCommands.RunLine("force maybe", lines.Add, debug: true);
      Check(lines.Count == 3 && lines.All(l => l.Contains(WaterCommands.Usage)) && !SeaPatches.ForcedOn, "other words, and 'force' without on or off, say the usage and change nothing");
    }
    finally
    {
      SeaField.Current = null;
      BC.Settings = settings;
      BC.HarmonyInstance = harmony;
      SeaPatches.Refresh();
    }

    // AshlandsWater: with nothing swapped, its renderers are the game's: the fallback colours apply as in 0.10.3.
    Check(!SeaDraw.Draws(null), "AshlandsWater asks SeaDraw.Draws before it colours a renderer, and nothing is drawn by BC's shader here");
  }

  // ------------------------------------------------------------------------------------------------ bc water force on|off
  private static void ForceTests()
  {
    Section("force: bc water force on|off");
    if (!running) return;
    var settings = BC.Settings;
    var harmony = BC.HarmonyInstance;
    var repatch = WaterCommands.Repatch;
    var probe = WaterCommands.Probe;
    var ews = EWS.Installed;
    int repatches = 0, probes = 0;
    bool probeLogs = false;
    var lines = new List<string>();
    BC.HarmonyInstance = new HarmonyLib.Harmony("water-tests.force");
    WaterCommands.Repatch = () => repatches++;
    WaterCommands.Probe = (output, alsoLog) =>
    {
      probes++;
      probeLogs = alsoLog;
      output("Water probe: n=0 median=NaN p99=NaN max=NaN (drawn minus felt, metres); camera 0.0 0.0");
    };
    try
    {
      EWS.Installed = false;
      BC.ExpandWorldSizeGeometry = null;
      SeaPatches.ForcedOn = false;
      // Debug Mode only: the setting (not bound here) for the console command, the debug tree for itself.
      WaterCommands.RunLine("force on", lines.Add, debug: false);
      WaterCommands.RunLine("probe", lines.Add, debug: false);
      Check(!SeaPatches.ForcedOn && repatches == 0 && probes == 0 && lines.Count == 2 && lines.All(l => l.Contains("needs Debug Mode")), "without Debug Mode, force and probe say so and do nothing");

      lines.Clear();
      WaterCommands.RunLine("force on", lines.Add, debug: true);
      Check(SeaPatches.ForcedOn && repatches == 1 && lines.Count == 1 && lines[0].Contains("force is ON"), "'force on' sets the force, says so, and patches again");
      WaterCommands.RunLine("force on", lines.Add, debug: true);
      Check(SeaPatches.ForcedOn && repatches == 1, "(again: nothing changes, nothing is patched again)");
      lines.Clear();
      foreach (var bad in new[] { "force", "force maybe", "force on now", "force auto", "forc on", "probe now", "probe on" })
        WaterCommands.RunLine(bad, lines.Add, debug: true);
      Check(lines.Count == 7 && lines.All(l => l.Contains(WaterCommands.Usage)) && SeaPatches.ForcedOn && repatches == 1 && probes == 0,
        "other words say the usage and change nothing");

      // A vanilla-size world without maps, forced on: a candidate, a field of the game's own sea, and BC's shader drawn all the same.
      BC.Settings = World();
      SeaPatches.Refresh();
      Check(SeaPatches.Snapshot.Pending && SeaPatches.Candidate(BC.Settings), "forced on, a vanilla-size world without maps asks for a field");
      var report = new List<string>();
      WaterCommands.RunLine("", report.Add, debug: true);
      Check(report.Any(l => l.Contains("FORCE IS ON")) && report.Any(l => l.Contains("forced on")), "'bc water' says that force is on");
      SeaPatches.Start(BC.Settings, true);
      Check(WaitForField(60000), "its field is built from the world's rules");
      var field = SeaField.Current!;
      Check(field.SameAsGame && field.Hot != null && SeaPatches.DepthPending && SeaPatches.Snapshot.State.Contains("forced"),
        $"it is the game's own sea, and still gets its depth grid and says it is forced ({SeaPatches.Snapshot.State})");
      Check(SeaDraw.Cause(false, field, false, false, null, SeaPatches.ForcedOn) == null, "so BC's shader is drawn on it");
      double fade = 0.25;
      Check(SeaPatches.DeepNorthWaveFadePrefix(0f, 9000f, ref fade) && fade == 0.25, "and the felt side stays the game's (the prefix runs the game's code)");
      Check(BC.WantedToggles(BC.Settings).Count(n => n.EndsWith("(BC's sea)")) == 2, "(the felt toggles are on, and pass through)");

      lines.Clear();
      WaterCommands.RunLine("force off", lines.Add, debug: true);
      Check(!SeaPatches.ForcedOn && repatches == 2 && lines.Single().Contains("force is off"), "'force off' gives the rules back, and patches again");
      SeaPatches.Refresh();
      Check(!SeaPatches.Candidate(BC.Settings) && SeaField.Current == null && !SeaPatches.Snapshot.Pending, "the vanilla-size world is no candidate again: no field");
      report.Clear();
      WaterCommands.RunLine("", report.Add, debug: true);
      Check(!report.Any(l => l.Contains("FORCE IS ON")), "and 'bc water' no longer says force is on");

      // It stays through a refresh in the same world and ends when the world is left.
      WaterCommands.RunLine("force on", lines.Add, debug: true);
      SeaPatches.Refresh();
      Check(SeaPatches.ForcedOn && SeaPatches.Snapshot.Pending, "a refresh in the same world keeps the force");
      BC.Settings = World(enabled: false);
      SeaPatches.Refresh();
      Check(!SeaPatches.ForcedOn && !SeaPatches.Snapshot.Pending, "leaving the world (the main menu) resets it");
      BC.Settings = World();
      var gameTerrain = World();
      gameTerrain.GameTerrain = true;
      WaterCommands.RunLine("force on", lines.Add, debug: true);
      BC.Settings = gameTerrain;
      SeaPatches.Refresh();
      Check(!SeaPatches.ForcedOn && !SeaPatches.Candidate(gameTerrain), "and a world that keeps the game's terrain is no candidate, forced or not");

      // The probe goes to its own code, with the debug tree's logging flag.
      lines.Clear();
      WaterCommands.RunLine("probe", lines.Add, debug: true, alsoLog: true);
      Check(probes == 1 && probeLogs && lines.Single().StartsWith("Water probe: "), "'probe' runs the probe (the debug tree asks it to log as well)");
      WaterCommands.RunLine("probe", lines.Add, debug: true, alsoLog: false);
      Check(probes == 2 && !probeLogs, "(the console command's output logs by itself)");
    }
    finally
    {
      SeaPatches.ForcedOn = false;
      SeaField.Current = null;
      WaterCommands.Repatch = repatch;
      WaterCommands.Probe = probe;
      BC.Settings = settings;
      BC.HarmonyInstance = harmony;
      EWS.Installed = ews;
      SeaPatches.Refresh();
    }
  }

  // ------------------------------------------------------------------------------------------------ bc water probe
  private static void ProbeTests()
  {
    Section("probe: the places, the vertices and the line");
    if (!running) return;
    // 81 places, 8 m apart, 8 to 72 m ahead and 32 m to each side.
    var north = SeaProbe.Targets(100f, 200f, 0f);
    Check(north.Length == 81 && Math.Abs(north[0].X - 68f) < 1e-3 && Math.Abs(north[0].Z - 208f) < 1e-3 && Math.Abs(north[8].X - 132f) < 1e-3
          && Math.Abs(north[80].X - 132f) < 1e-3 && Math.Abs(north[80].Z - 272f) < 1e-3 && Math.Abs(north[4].X - 100f) < 1e-3,
      "facing north from (100, 200): rows 8 to 72 m ahead, columns 32 m left to 32 m right");
    var east = SeaProbe.Targets(100f, 200f, 90f);
    Check(Math.Abs(east[4].X - 108f) < 1e-3 && Math.Abs(east[4].Z - 200f) < 1e-3 && Math.Abs(east[8].Z - 168f) < 1e-3 && Math.Abs(east[0].Z - 232f) < 1e-3,
      "facing east: ahead is +x, and the right-hand column is on the south side (-z)");
    bool eightApart = true;
    for (int row = 0; row < 9; row++)
      for (int column = 0; column < 8; column++)
      {
        var a = east[row * 9 + column];
        var b = east[row * 9 + column + 1];
        eightApart &= Math.Abs(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) - 8.0) < 1e-3;
      }
    Check(eightApart && SeaProbe.Targets(0f, 0f, 37f).Select(t => (t.X, t.Z)).Distinct().Count() == 81, "neighbours are 8 m apart at any heading, and all 81 places differ");

    // The zone water's vertices: 32 across the zone, 2 half-widths / 31 apart, the outer two left out.
    const float half = 32.1f;
    var random = new System.Random(3);
    bool onLattice = true, near = true;
    for (int i = 0; i < 2000; i++)
    {
      float center = (float)(random.Next(-300, 300) * 64.0), t = center + (float)((random.NextDouble() * 2 - 1) * (half - 1.5f));
      float v = SeaProbe.SnapToVertex(t, center, half);
      double k = ((v - center) / half + 1.0) * 31.0 / 2.0;
      onLattice &= Math.Abs(k - Math.Round(k)) < 1e-3 && Math.Round(k) >= 1 && Math.Round(k) <= 30;
      near &= Math.Abs(v - t) <= half / 31.0 + 1e-3;
    }
    Check(onLattice && near, "a place snaps to a vertex of the 32 x 32 mesh (half a step, 1.04 m, at most away)");
    Check(Math.Abs(SeaProbe.SnapToVertex(-32f, 0f, half) - (-half * 29f / 31f)) < 1e-3 && Math.Abs(SeaProbe.SnapToVertex(40f, 0f, half) - half * 29f / 31f) < 1e-3,
      "and the two outer columns, which overlap the next zone's water, are not used");

    // The numbers.
    var s = SeaProbe.Summarize([-0.01, 0.02, 0.03, -0.04, 0.0]);
    Check(s.N == 5 && Math.Abs(s.Median - 0.02) < 1e-12 && Math.Abs(s.P99 - 0.0396) < 1e-12 && Math.Abs(s.Max - 0.04) < 1e-12,
      $"of the absolute differences: median 0.02, p99 by interpolation 0.0396, max 0.04 ({s.Median}, {s.P99}, {s.Max})");
    var even = SeaProbe.Summarize([0.1, 0.2, 0.3, 0.4]);
    Check(Math.Abs(even.Median - 0.25) < 1e-12, "the median of an even count is the mean of the middle two");
    var one = SeaProbe.Summarize([-0.07]);
    Check(one.N == 1 && one.Median == 0.07 && one.P99 == 0.07 && one.Max == 0.07, "one difference is all three");
    var none = SeaProbe.Summarize(Array.Empty<double>());
    Check(none.N == 0 && double.IsNaN(none.Median) && double.IsNaN(none.P99) && double.IsNaN(none.Max), "none gives n=0 and NaN");

    // The line: parseable, whatever the culture.
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
      System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
      var line = SeaProbe.FormatLine(62, 0.0041, 0.03149, 0.0449, -774.23f, 9869.04f);
      Check(line == "Water probe: n=62 median=0.004 p99=0.031 max=0.045 (drawn minus felt, metres); camera -774.2 9869.0", $"the line: {line}");
      var match = System.Text.RegularExpressions.Regex.Match(line,
        @"^Water probe: n=(\d+) median=(-?[\d.]+|NaN) p99=(-?[\d.]+|NaN) max=(-?[\d.]+|NaN) \(drawn minus felt, metres\); camera (-?[\d.]+) (-?[\d.]+)$");
      Check(match.Success, "and a script can parse it");
      Check(SeaProbe.FormatLine(0, double.NaN, double.NaN, double.NaN, 1f, 2f) == "Water probe: n=0 median=NaN p99=NaN max=NaN (drawn minus felt, metres); camera 1.0 2.0", "(also with no place used)");
      Check(SeaProbe.Cannot("no world is loaded here") == "Water probe: cannot: no world is loaded here", "and what the probe cannot do is said in one line too");
    }
    finally
    {
      System.Globalization.CultureInfo.CurrentCulture = culture;
    }
  }
}

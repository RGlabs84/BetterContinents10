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
      Check(lines.Count == 3 && lines.All(l => l.Contains(WaterCommands.Usage)) && SeaPatches.Forced == null, "other words, and 'force' without on, off or auto, say the usage and change nothing");
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
}

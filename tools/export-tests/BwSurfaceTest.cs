// Added by Wubarrk on 2026-10-26 for the BetterWorlds-complete export (0.10.7).

using System;
using System.Linq;
using System.Reflection;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// What BetterWorlds (bw_bcupgrade, bw_import) relies on in this exporter, checked on the real types: a port of its CheckExportApi (LegacyContinents.cs,
// BetterWorlds working tree of 2026-10-26), the option texts it sends, and the Settings members it reads by name. A change to any of these is silent in
// BetterWorlds (a missing member reads as 0 or false, a second overload refuses the upgrade), so this test is the place that says so.
internal static class BwSurfaceTest
{
  const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

  public static void Run()
  {
    System.Console.WriteLine("== the surface BetterWorlds calls by reflection");
    var asm = typeof(WorldExport).Assembly;
    Type export = asm.GetType("BetterContinents.WorldExport"), cmds = asm.GetType("BetterContinents.WorldExportCommands");
    Program.C(export != null && cmds != null && asm.GetType("BetterContinents.BetterContinents") != null, "BetterContinents.WorldExport, WorldExportCommands and BetterContinents are found by name");
    if (export == null || cmds == null)
      return;

    var can = export.GetMethod("CanExport", Static);
    var cp = can?.GetParameters();
    Program.C(can != null && can.ReturnType == typeof(bool) && cp.Length == 1 && cp[0].ParameterType == typeof(string).MakeByRefType(), "CanExport(out string) -> bool");

    var starts = export.GetMethods(Static).Where(m => m.Name == "Start").ToArray();
    var optionsType = export.GetNestedType("Options", BindingFlags.Public | BindingFlags.NonPublic);
    var sp = starts.Length == 1 ? starts[0].GetParameters() : null;
    Program.C(starts.Length == 1 && starts[0].ReturnType == typeof(bool) && sp.Length == 2 && optionsType != null && sp[0].ParameterType == optionsType && sp[1].ParameterType == typeof(string),
      $"exactly one Start, and it is Start(Options, string) -> bool ({starts.Length} found)");

    var parses = cmds.GetMethods(Static).Where(m => m.Name == "TryParse").ToArray();
    var pp = parses.Length == 1 ? parses[0].GetParameters() : null;
    Program.C(pp != null && parses[0].ReturnType == typeof(bool) && pp.Length == 3 && pp[0].ParameterType == typeof(string) && pp[1].ParameterType == optionsType.MakeByRefType()
      && pp[2].ParameterType == typeof(string).MakeByRefType(), $"exactly one TryParse, and it is TryParse(string, out Options, out string) -> bool ({parses.Length} found)");

    Program.C(export.GetMethod("Cancel", Static, null, Type.EmptyTypes, null) != null, "Cancel() with no parameters");
    foreach (var (name, type) in new[] { ("IsRunning", typeof(bool)), ("Phase", typeof(string)), ("Overall", typeof(float)), ("LastError", typeof(string)), ("LastExportDir", typeof(string)), ("RunningDir", typeof(string)) })
    {
      var p = export.GetProperty(name, Static);
      Program.C(p != null && p.PropertyType == type && p.GetGetMethod(true) != null, $"WorldExport.{name} is a {type.Name} property with a getter");
    }
    Program.C(typeof(BC).GetField("Settings", Static) != null || typeof(BC).GetProperty("Settings", Static) != null, "BetterContinents.Settings is a static field or property");

    // The option texts BetterWorlds builds (LegacyContinents.ExportOptionsText): they parse, Lava, Moss, Paint and Sources stay on (it never passes
    // nolava, nomoss, nopaint or nosources), and the cold map is on unless it says nocold.
    var texts = new (string Text, int Size, float Amount, bool Forest, bool Heat, bool Heights, bool Biomes, bool AltBiomes, bool Edge)[]
    {
      ("4096 amount=5 nopreset nolocations noforest noheat edge=off", 4096, 5f, false, false, true, true, true, true),
      ("2048 amount=4 nopreset nolocations forest=exact noaltbiomes", 2048, 4f, true, true, true, true, false, false),
      ("16384 amount=81 nopreset nolocations noheight nobiomes noforest noheat", 16384, 81f, false, false, false, false, true, false),
    };
    foreach (var t in texts)
    {
      bool ok = WorldExportCommands.TryParse(t.Text, out var o, out var error);
      Program.C(ok && o.Size == t.Size && o.HeightmapAmount == t.Amount && !o.Preset && !o.Locations && o.Heightmap == t.Heights && o.Biomes == t.Biomes && o.AltBiomes == t.AltBiomes
        && o.Lava && o.Moss && o.Paint && o.Sources && o.Cold, $"BetterWorlds' '{t.Text}' parses ({error}) with lava, moss, paint, sources and cold on");
      Program.C(ok && o.Forest == !t.Text.Contains("noforest") && o.Heat == !t.Text.Contains("noheat") && o.ForestExact == t.Text.Contains("forest=exact"), "and its forest and heat words do what they did");
      var withNoCold = t.Text + " nocold";
      Program.C(WorldExportCommands.TryParse(withNoCold, out var nc, out _) && !nc.Cold && nc.Lava && nc.Moss && nc.Paint && nc.Sources && nc.Heat == o.Heat, "and with nocold appended the cold map is off, nothing else changes");
    }
    Program.C(WorldExportCommands.TryParse("cold", out var cold, out _) && cold.Cold && WorldExportCommands.TryParse("nocold cold", out var back, out _) && back.Cold, "'cold' switches it back on");
    Program.C(WorldExportCommands.OptionsUsage.Contains("[nocold]"), "bc_export's usage names nocold");

    // The members of BetterContinentsSettings that BetterWorlds reads by name (ReadMember: a field or a property, public or not), with the type it casts to.
    var settings = typeof(BC.BetterContinentsSettings);
    const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    Type TypeOf(string name) => settings.GetField(name, instance)?.FieldType ?? settings.GetProperty(name, instance)?.PropertyType;
    foreach (var name in new[] { "EnabledForThisWorld", "WideSectors", "GameTerrain", "HasLayer", "CompactMaps", "SkipDefaultLocations", "ForestFactorOverrideAllTrees", "DisableMapEdgeDropoff" })
      Program.C(TypeOf(name) == typeof(bool), $"Settings.{name} is a bool");
    foreach (var name in new[] { "Version", "BiomePrecision" })
      Program.C(TypeOf(name) == typeof(int), $"Settings.{name} is an int");
    foreach (var name in new[] { "WorldSize", "EdgeSize", "ForestScale", "ForestAmountOffset", "ColdMapScale" })
      Program.C(TypeOf(name) == typeof(float), $"Settings.{name} is a float");
    foreach (var name in new[] { "AltBiomes", "AltBiomeMapData", "ForestMap", "HeatMap", "ColdMap", "LavaMap", "BiomeMap", "PaintMap", "MossMap", "TerrainMap", "SpawnMap", "VegetationMap", "LocationExclusionMap" })
      Program.C(TypeOf(name) != null && !TypeOf(name).IsValueType, $"Settings.{name} is there, and null when there is no such map");
    var empty = new BC.BetterContinentsSettings();
    Program.C(new[] { "ColdMap", "LocationExclusionMap", "HeatMap" }.All(n => (settings.GetField(n, instance)?.GetValue(empty) ?? settings.GetProperty(n, instance)?.GetValue(empty)) == null), "and a world without them reads null");
  }
}

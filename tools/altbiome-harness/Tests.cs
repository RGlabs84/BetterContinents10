// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-25 for version-agnostic wording (0.9.1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

internal static partial class Tests
{
  public static int RunAll()
  {
    var started = DateTime.Now;
    Section("settings: per-world alt-biome options");
    SettingsTests();
    Section("save format");
    SaveFormatTests();
    Section("legend");
    LegendTests();
    Section("colour scheme");
    PaletteTests();

    var alts = LoadAltBiomes();
    Check(alts.Count == 32, $"32 alt biomes loaded from the 1.0.15 bundle extraction ({alts.Count})");
    Section("sectors: Better Continents' build against vanilla GenerateSectors");
    var a = ParityTest();
    InfoTest(a);
    Evidence(a, alts);
    CanAddModifierParity(a, alts);
    Section("placement control around vanilla GenerateAltBiomes");
    ControlRunTests(a, alts);
    Section("planting foundation (a test provider that claims every biome)");
    CirclePlantingTests(alts);
    Section("colour planting (altbiomemap.png + legend)");
    ColourPlantingTests();
    Section("planted and unplanted regions: split out, never merged");
    SplitSemanticsTests();
    Section("baked alt-biome map: serialization");
    MapSerializationTests();
    Section("server placement on a client");
    ServerAssignmentTest(a, alts);
    Section("agreement hashes");
    HashTests();
    Section("what the installed game does with the biome cache");
    VanillaFacts();
    Section("biome cache fingerprint trailer");
    CacheTests();
    Section("fix a: Expand World Size grid clamp");
    GridClampTests();
    Section("WorldEdge cut-off");
    CutoffTest();
    Section("fix c: Deep North weather");
    DeepNorthTests();
    Section("GenerateAltBiomes seed transpiler on the real IL");
    SeedTranspilerTest();
    Section("planting errors fail the world load");
    FailLoadTests();
    Section("the game's random placement, run for real through Harmony");
    RandomPassTests();
    Section("quota: planted alt biomes count toward the game's maximums");
    QuotaTests(alts);
    Section("modes: Random, PlantedOnly, Off");
    ModeTests(alts);
    Section("world export (0.9.0): heightmap encode/decode round trip");
    ExportMathTests();

    System.Console.WriteLine();
    System.Console.WriteLine($"{Checks - Failures}/{Checks} checks passed ({(DateTime.Now - started).TotalSeconds:0} s)");
    foreach (var f in FailedChecks)
      System.Console.WriteLine("  FAILED: " + f);
    return Failures == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------------------------------------ settings
  private static void SettingsTests()
  {
    var s = new BC.AltBiomeSettings
    {
      Mode = BC.AltBiomeMode.PlantedOnly,
      Grid = BC.AltBiomeGridMode.WorldEdge,
      UseFixedSeed = true,
      Seed = -12345,
      FixNeighbourCheck = true,
      MeanSectorHeight = true,
      MinSectorThickness = 2.5f,
      ChanceMultiplier = 1.5f,
      AmountMultiplier = 2f,
      EdgeScale = 4f,
      DistanceScale = 0.5f,
    };
    Check(BC.AltBiomeSettings.TryParseOverrides(
      "Dark Meadows: enabled=false, chance=0.5; *: maxedge=800, ignorebounds=true; Wolf Mountain: min=2, max=4, mindist=100, minedge=10, minheight=20, maxheight=500",
      s.Overrides, out var err), "parse overrides: " + err);
    var blob = s.Serialize();
    var r = BC.AltBiomeSettings.Deserialize(blob);
    Check(r.Mode == s.Mode && r.Grid == s.Grid && r.UseFixedSeed && r.Seed == -12345 && r.FixNeighbourCheck && r.MeanSectorHeight
          && r.MinSectorThickness == 2.5f && r.ChanceMultiplier == 1.5f && r.AmountMultiplier == 2f && r.EdgeScale == 4f && r.DistanceScale == 0.5f,
      "settings round trip: scalars");
    Check(r.FormatOverrides() == s.FormatOverrides(), $"settings round trip: overrides ('{r.FormatOverrides()}')");
    Check(r.Overrides.TryGetValue("dark meadows", out var dm) && dm.Enabled == false && dm.Chance == 0.5f, "override lookup is case-insensitive");
    Check(r.Overrides["Wolf Mountain"].MinAmount == 2 && r.Overrides["Wolf Mountain"].MaxAvgHeight == 500f, "override fields");
    Check(s.Serialize().SequenceEqual(s.Clone().Serialize()), "clone serialises identically");

    var s2 = new BC.AltBiomeSettings();
    BC.AltBiomeSettings.TryParseOverrides("Wolf Mountain: min=2; Dark Meadows: enabled=false", s2.Overrides, out _);
    var s3 = new BC.AltBiomeSettings();
    BC.AltBiomeSettings.TryParseOverrides("Dark Meadows: enabled=false; Wolf Mountain: min=2", s3.Overrides, out _);
    Check(s2.Serialize().SequenceEqual(s3.Serialize()), "serialisation independent of override order");

    var s4 = new BC.AltBiomeSettings();
    var ok4 = BC.AltBiomeSettings.TryParseOverrides("Bad; Mushroom: chance=x, max=3; Lantern: colour=red", s4.Overrides, out var err4);
    Check(!ok4 && err4.Contains("Bad") && err4.Contains("not a number") && err4.Contains("unknown field") && s4.Overrides["Mushroom"].MaxAmount == 3,
      "override parse errors reported, good parts kept: " + err4);

    // Forward compatibility: a newer blob with extra trailing fields, and an override entry with an unknown bit.
    var pkg = new ZPackage();
    pkg.Write(2);
    pkg.Write((byte)0); pkg.Write((byte)1); pkg.Write(false); pkg.Write(0); pkg.Write(false); pkg.Write(false);
    pkg.Write(0f); pkg.Write(1f); pkg.Write(1f); pkg.Write(3f); pkg.Write(1f);
    var entry = new ZPackage();
    entry.Write("Mushroom");
    entry.Write((1 << 1) | (1 << 20));
    entry.Write(0.75f);
    entry.Write(123.5f); // unknown future field
    pkg.Write(1);
    pkg.Write(entry.GetArray());
    pkg.Write(999); // unknown future blob field
    var f = BC.AltBiomeSettings.Deserialize(pkg.GetArray());
    Check(f.Grid == BC.AltBiomeGridMode.WorldEdge && f.EdgeScale == 3f && f.Overrides["Mushroom"].Chance == 0.75f, "forward-compatible read of a newer blob");

    var d = new BC.AltBiomeSettings();
    Check(d.IsLegacyEquivalent(10500f), "defaults are legacy-equivalent");
    d.Grid = BC.AltBiomeGridMode.WorldEdge;
    Check(d.IsLegacyEquivalent(10500f) && d.IsLegacyEquivalent(20000f) && !d.IsLegacyEquivalent(5500f), "WorldEdge is legacy-equivalent unless the edge is inside 10500 m");
    d.Mode = BC.AltBiomeMode.Off;
    Check(!d.IsLegacyEquivalent(10500f), "Off is not legacy-equivalent");

    Check(!BC.AltBiomeSettings.TryParseSeed("", out _) && !BC.AltBiomeSettings.TryParseSeed("world", out _), "empty or 'world' seed = the world seed");
    Check(BC.AltBiomeSettings.TryParseSeed("42", out var n42) && n42 == 42, "numeric seed");
    Check(BC.AltBiomeSettings.TryParseSeed("MyMap", out var nm) && nm == "MyMap".GetStableHashCode(), "text seed hashes like a world seed");

    // Wildcard overrides (authored's "Never Random" list is an override now): exact name, then patterns, then *.
    Check(BC.AltBiomeSettings.GlobMatches("*Mistlands", "Trees Mistlands") && BC.AltBiomeSettings.GlobMatches("Lant*", "lantern")
          && BC.AltBiomeSettings.GlobMatches("*", "") && BC.AltBiomeSettings.GlobMatches("a*b*c", "aXbYc")
          && !BC.AltBiomeSettings.GlobMatches("a*b*c", "aXbY") && !BC.AltBiomeSettings.GlobMatches("Dark*", "Birch Meadows"),
      "wildcard matching (* = any run, case-insensitive)");
    var p = new BC.AltBiomeSettings();
    BC.AltBiomeSettings.TryParseOverrides("Dark Meadows: chance=0.5; Dark*: chance=0.3, max=9; *: chance=0.1, min=4, enabled=false; *Mistlands: enabled=true; Trees*: enabled=false", p.Overrides, out _);
    var rdm = p.Resolve("Dark Meadows");
    Check(rdm != null && rdm.Chance == 0.5f && rdm.MaxAmount == 9 && rdm.MinAmount == 4 && rdm.Enabled == false, "precedence field by field: exact name, then pattern, then *");
    var rb = p.Resolve("Birch Meadows");
    Check(rb != null && rb.Chance == 0.1f && rb.MaxAmount == null && rb.Enabled == false, "'*' alone applies to every alt biome");
    Check(p.Resolve("Trees Mistlands")?.Enabled == true, "the more specific pattern wins (*Mistlands over Trees*)");
    Check(new BC.AltBiomeSettings().Resolve("Lox Plains") == null, "no overrides resolve to nothing");

    // New-world defaults from the config: vanilla behaviour, not written for a standard-size world.
    var fromConfig = BC.AltBiomeSettings.FromConfig();
    Check(fromConfig.Mode == BC.AltBiomeMode.Random && fromConfig.Grid == BC.AltBiomeGridMode.WorldEdge && !fromConfig.UseFixedSeed
          && fromConfig.ChanceMultiplier == 1f && fromConfig.Overrides.Count == 0 && fromConfig.IsLegacyEquivalent(10500f),
      "config defaults: Random, WorldEdge, world seed, x1, no overrides; legacy-equivalent at 10500 m");
    BC.ConfigAltBiomeOverrides.Value = "*Mistlands: enabled=false";
    var withPattern = BC.AltBiomeSettings.FromConfig();
    BC.ConfigAltBiomeOverrides.Value = "";
    Check(withPattern.Resolve("Hare Mistlands")?.Enabled == false && withPattern.Resolve("Lox Plains") == null, "config Overrides accept wildcard patterns");
  }

  // ------------------------------------------------------------------------------------------------ save format
  private static byte[] ReadSettingsPayload(string path)
  {
    using var br = new BinaryReader(File.OpenRead(path));
    int count = br.ReadInt32();
    return br.ReadBytes(count);
  }

  private static byte[] SerializeSettings(BC.BetterContinentsSettings settings, bool network, bool includeAltBiomes = true)
  {
    var pkg = new ZPackage();
    settings.Serialize(pkg, network, includeAltBiomes);
    return pkg.GetArray();
  }

  // The network settings of an otherwise default BC world, ready for a hand-written key to be appended. (A default
  // world always writes a few keys, GlobalScale among them, so a hand-built stream must start from a real one.)
  internal static ZPackage BaseSettingsPackage()
  {
    var pkg = new ZPackage();
    NewSettings().Serialize(pkg, true);
    return pkg;
  }


  private static void SaveFormatTests()
  {
    Check((int)BC.DataKey.SkipDefaultLocations == 63 && (int)BC.DataKey.AltBiomes == 64 && (int)BC.DataKey.AltBiomeMap == 65 && (int)BC.DataKey.AltBiomeMapPath == 66,
      "DataKeys: SkipDefaultLocations 63, AltBiomes 64, AltBiomeMap 65, AltBiomeMapPath 66");

    // Real worlds saved by 0.8.0 (copies of the user's saves): 0.8.1 reads them and writes back the same bytes.
    foreach (var world in new[] { "Era02", "ProximaMaxi" })
    {
      var source = $"/home/rohan/.config/unity3d/IronGate/Valheim/worlds_local/{world}/BetterContinents";
      if (!File.Exists(source))
      {
        Note($"skipped {world}: no settings file");
        continue;
      }
      var copy = Path.Combine(Work, world + ".BetterContinents");
      File.Copy(source, copy, true);
      var payload = ReadSettingsPayload(copy);
      var settings = BC.BetterContinentsSettings.Load(new ZPackage(payload));
      var again = SerializeSettings(settings, network: false);
      Check(settings.EnabledForThisWorld && again.SequenceEqual(payload),
        $"a real 0.8.0 world ({world}, {payload.Length} bytes, biome map {settings.HasBiomeMap}) saves byte for byte as it was written");
      Check(settings.AltBiomes == null && !settings.HasAltBiomeMap && SerializeSettings(settings, true).SequenceEqual(SerializeSettings(settings, true, includeAltBiomes: false)),
        $"{world}: no alt-biome key read or written");
    }

    // A new world with the default options writes no alt-biome key: same bytes as 0.8.0 would write.
    var plain = NewSettings();
    var defaults = NewSettings(BC.AltBiomeSettings.FromConfig());
    Check(SerializeSettings(defaults, false).SequenceEqual(SerializeSettings(plain, false)) && SerializeSettings(defaults, true).SequenceEqual(SerializeSettings(plain, true)),
      "new world with default alt-biome options: byte-identical to settings without the feature (disk and network)");
    var small = NewSettings(BC.AltBiomeSettings.FromConfig());
    small.WorldSize = 5000f;
    Check(BC.BetterContinentsSettings.Load(new ZPackage(SerializeSettings(small, false))).AltBiomes?.Grid == BC.AltBiomeGridMode.WorldEdge,
      "a world edge inside 10500 m with Grid WorldEdge writes the key (behaviour differs from 0.8.0)");
    var used = NewSettings(new BC.AltBiomeSettings { Mode = BC.AltBiomeMode.PlantedOnly, ChanceMultiplier = 2f });
    var usedBytes = SerializeSettings(used, false);
    var usedBack = BC.BetterContinentsSettings.Load(new ZPackage(usedBytes));
    Check(usedBack.AltBiomes != null && usedBack.AltBiomes.Mode == BC.AltBiomeMode.PlantedOnly && usedBack.AltBiomes.ChanceMultiplier == 2f,
      "options round trip through the world settings");
    Check(SerializeSettings(used, true, includeAltBiomes: false).SequenceEqual(SerializeSettings(plain, true)), "includeAltBiomes:false leaves every alt-biome key out");

    // A newer or unreadable blob is kept and written back unchanged, so an older build never drops data.
    var newer = new ZPackage();
    newer.Write(2);
    newer.Write((byte)1); newer.Write((byte)0); newer.Write(false); newer.Write(0); newer.Write(false); newer.Write(false);
    newer.Write(0f); newer.Write(1f); newer.Write(1f); newer.Write(1f); newer.Write(1f); newer.Write(0); newer.Write(12345);
    var newerBlob = newer.GetArray();
    var withNewer = BaseSettingsPackage();
    withNewer.Write((int)BC.DataKey.AltBiomes);
    withNewer.Write(newerBlob);
    var readNewer = BC.BetterContinentsSettings.Load(new ZPackage(withNewer.GetArray()));
    Check(readNewer.EnabledForThisWorld && readNewer.AltBiomes?.Mode == BC.AltBiomeMode.PlantedOnly && SerializeSettings(readNewer, true).SequenceEqual(withNewer.GetArray()),
      "a newer alt-biome blob is read (known fields) and saved back unchanged");
    var corrupt = BaseSettingsPackage();
    corrupt.Write((int)BC.DataKey.AltBiomes);
    corrupt.Write(new byte[] { 1, 2, 3 });
    int mark = CapturingLogHandler.Mark();
    var readCorrupt = BC.BetterContinentsSettings.Load(new ZPackage(corrupt.GetArray()));
    Check(readCorrupt.EnabledForThisWorld && readCorrupt.AltBiomes == null && LogContains(mark, "Failed to read the alt-biome settings")
          && SerializeSettings(readCorrupt, true).SequenceEqual(corrupt.GetArray()),
      "an unreadable alt-biome blob: error, vanilla behaviour, the rest loads, the blob is kept");
  }

  // ------------------------------------------------------------------------------------------------ legend
  private static void LegendTests()
  {
    var dir = Path.Combine(Work, "legend");
    Directory.CreateDirectory(dir);
    var png = Path.Combine(dir, "altbiomemap.png");
    using (var img = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 0)))
      img.SaveAsPng(png);
    File.WriteAllText(Path.Combine(dir, "altbiomemap.txt"), string.Join("\n",
    [
      "# comment line",
      "Dark Meadows: #2D4613   # inline comment",
      "Lantern: 242,163,0",
      "Bones: F8E1B6",
      "Menhir: F8E1B6",
      "!Wolf Mountain + Drake Mountain: 93C7BC",
      "none: C030F0",
      "Lantern: at 1200.5, -3400",
      "none: @ 10, 20",
      "Broken line without colon",
      "Black: 000000",
      "Name: not-a-colour",
      "none + Lantern: 0080C0",
      "*Mistlands: 8A7FDB",
    ]));
    var m = BetterContinents.ImageMapAltBiome.Create(png);
    Check(m != null, "legend + 8x8 transparent image load");
    var colours = m.Classes.Skip(1).Where(c => !c.IsPin).ToList();
    Check(colours.Count == 7, $"7 colour entries ({string.Join(" | ", colours.Select(c => c.ColorHex + " " + c.Names))})");
    Check(colours.Any(c => c.ColorHex == "2D4613" && c.Names == "Dark Meadows"), "#RRGGBB with an inline comment");
    Check(colours.Any(c => c.ColorHex == "F2A300" && c.Names == "Lantern"), "r,g,b colour");
    Check(colours.Any(c => c.ColorHex == "F8E1B6" && c.Names == "Bones + Menhir"), "a repeated colour stacks");
    Check(colours.Any(c => c.ColorHex == "93C7BC" && c.Names == "!Wolf Mountain + Drake Mountain"), "+ stacking and ! force");
    Check(colours.Any(c => c.ColorHex == "C030F0" && c.Names == "none"), "none");
    Check(colours.Any(c => c.ColorHex == "0080C0" && c.Names == "Lantern"), "'none + Lantern' drops none");
    Check(colours.Any(c => c.ColorHex == "8A7FDB" && c.Names == "*Mistlands"), "* wildcard entry kept for resolution at load");
    Check(m.Pins.Count == 2, "two point plants ('at' and '@')");
    Check(Math.Abs(m.Pins[0].X - 1200.5f) < 0.01f && Math.Abs(m.Pins[0].Z + 3400f) < 0.01f, "point coordinates parsed invariantly");
    Check(!colours.Any(c => c.ColorHex == "000000"), "black refused");

    // Default legend is written when missing and parses to the full palette.
    var dir2 = Path.Combine(Work, "legend2");
    Directory.CreateDirectory(dir2);
    var png2 = Path.Combine(dir2, "altbiomemap.png");
    using (var img = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 0, 0)))
      img.SaveAsPng(png2);
    var m2 = BetterContinents.ImageMapAltBiome.Create(png2);
    Check(File.Exists(Path.Combine(dir2, "altbiomemap.txt")), "default legend written when missing");
    var c2 = m2.Classes.Skip(1).ToList();
    Check(c2.Count == 32 && c2.Select(c => c.ColorHex).Distinct().Count() == 32 && m2.Pins.Count == 0, $"default legend = 32 distinct colours, no points (got {c2.Count})");
  }

  // ------------------------------------------------------------------------------------------------ colour scheme
  private static void PaletteTests()
  {
    var palette = BetterContinents.ImageMapAltBiome.DefaultPalette;
    var alts = LoadAltBiomes();
    var gameNames = alts.Select(a => a.m_name).ToList();
    Check(palette.Length == 32 && palette.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == 32, "32 palette entries, each alt biome once");
    Check(palette.Select(p => p.Name).SequenceEqual(gameNames, StringComparer.Ordinal), "the palette covers exactly the 32 vanilla alt biomes, in the game's order");
    var biomeMismatch = palette.Where(p => alts.First(a => a.m_name == p.Name).m_biome != p.Biomes).Select(p => p.Name).ToList();
    Check(biomeMismatch.Count == 0, "each entry's base biomes equal the game's m_biome " + string.Join(", ", biomeMismatch));

    // Base-biome colours, taken from ImageMapBiome.DefaultColors itself.
    var baseText = (string)typeof(BetterContinents.ImageMapAltBiome).Assembly.GetType("BetterContinents.ImageMapBiome")!
      .GetField("DefaultColors", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var bases = baseText.Split('|').Select(s => s.Split(':')).Select(s => (Name: s[0].Trim(), Hex: s[1].Trim())).ToList();
    Check(bases.Count == 10 && bases[0].Name == "None", $"10 base-biome colours in ImageMapBiome.DefaultColors ({bases.Count})");
    int tol = BetterContinents.ImageMapAltBiome.ColorTolerance;
    double Dist(string a, string b)
    {
      var x = Hex(a);
      var y = Hex(b);
      return Math.Sqrt((x.R - y.R) * (x.R - y.R) + (x.G - y.G) * (x.G - y.G) + (x.B - y.B) * (x.B - y.B));
    }
    double minAlt = double.MaxValue, minBase = double.MaxValue;
    string minAltPair = "", minBasePair = "";
    for (int i = 0; i < palette.Length; i++)
    {
      for (int j = i + 1; j < palette.Length; j++)
      {
        var d = Dist(palette[i].Hex, palette[j].Hex);
        if (d < minAlt) { minAlt = d; minAltPair = $"{palette[i].Name}/{palette[j].Name}"; }
      }
      foreach (var b in bases)
      {
        var d = Dist(palette[i].Hex, b.Hex);
        if (d < minBase) { minBase = d; minBasePair = $"{palette[i].Name}/{b.Name}"; }
      }
    }
    Check(minAlt >= 2 * tol, $"every alt-biome colour is at least 2 x {tol} from every other (closest {minAlt:0.0}, {minAltPair})");
    Check(minBase >= 2 * tol, $"every alt-biome colour is at least 2 x {tol} from every base-biome colour, black and white included (closest {minBase:0.0}, {minBasePair})");

    // The generated palette files. They are written from the built DLL, so they can never drift from the code.
    var outDir = Path.Combine(RepoRoot, "palettes");
    Directory.CreateDirectory(outDir);
    string BiomeGroup(Heightmap.Biome mask) =>
      BetterContinents.ImageMapAltBiome.BiomeList(mask) is var list && list.Contains(',') ? "Several base biomes" : list;
    var groupOrder = new[] { "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "AshLands", "DeepNorth", "Ocean", "Several base biomes" };
    var grouped = palette.Select((p, i) => (p, i, group: BiomeGroup(p.Biomes)))
      .OrderBy(t => Array.IndexOf(groupOrder, t.group)).ThenBy(t => t.i).ToList();

    var gpl = new StringBuilder();
    void Line(string line) => gpl.Append(line).Append('\n');
    Line("GIMP Palette");
    Line("Name: Better Continents");
    Line("Columns: 8");
    Line("#");
    Line("# Better Continents biome-map and alt-biome-map colours, generated from BetterContinents.dll");
    Line("# by tools/altbiome-harness. Do not edit: change the code and run the harness.");
    Line("# Base biomes: the default biomemap.txt legend. Alt biomes: the default altbiomemap.txt palette,");
    Line($"# grouped by the base biome they plant on. Pixels within {tol} RGB units of a colour count as it.");
    Line("#");
    Line("# Base biomes");
    foreach (var b in bases)
    {
      var c = Hex(b.Hex);
      Line($"{c.R,3} {c.G,3} {c.B,3}\tBiome: {b.Name}");
    }
    string lastGroup = "";
    foreach (var (p, _, group) in grouped)
    {
      if (group != lastGroup)
      {
        Line($"# Alt biomes: {group}");
        lastGroup = group;
      }
      var c = Hex(p.Hex);
      Line($"{c.R,3} {c.G,3} {c.B,3}\tAlt: {p.Name}");
    }
    File.WriteAllText(Path.Combine(outDir, "BetterContinents.gpl"), gpl.ToString());

    var entries = new List<Dictionary<string, object>>();
    foreach (var b in bases)
    {
      var c = Hex(b.Hex);
      entries.Add(new Dictionary<string, object>
      {
        ["name"] = b.Name, ["kind"] = "biome", ["base_biome"] = b.Name, ["hex"] = b.Hex, ["r"] = (int)c.R, ["g"] = (int)c.G, ["b"] = (int)c.B,
      });
    }
    foreach (var (p, _, group) in grouped)
    {
      var c = Hex(p.Hex);
      entries.Add(new Dictionary<string, object>
      {
        ["name"] = p.Name, ["kind"] = "alt", ["base_biome"] = BetterContinents.ImageMapAltBiome.BiomeList(p.Biomes), ["group"] = group,
        ["hex"] = p.Hex, ["r"] = (int)c.R, ["g"] = (int)c.G, ["b"] = (int)c.B,
      });
    }
    var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    File.WriteAllText(Path.Combine(outDir, "palette.json"), json);

    var legendPath = Path.Combine(outDir, "altbiomemap.txt");
    File.WriteAllText(legendPath, BetterContinents.ImageMapAltBiome.DefaultLegend);
    Note($"wrote {outDir}/BetterContinents.gpl, palette.json and altbiomemap.txt");

    // BC's own default legend, written by its own loader for a map without one, is that file byte for byte.
    var dir = Path.Combine(Work, "legend3");
    Directory.CreateDirectory(dir);
    var png = Path.Combine(dir, "altbiomemap.png");
    using (var img = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 0, 0)))
      img.SaveAsPng(png);
    var written = BetterContinents.ImageMapAltBiome.Create(png);
    var bcLegend = File.ReadAllBytes(Path.Combine(dir, "altbiomemap.txt"));
    Check(written != null && bcLegend.SequenceEqual(File.ReadAllBytes(legendPath)), $"BC writes the default legend when none exists, identical to palettes/altbiomemap.txt ({bcLegend.Length} bytes)");
    Check(!bcLegend.Contains((byte)'\r') && !(bcLegend.Length >= 3 && bcLegend[0] == 0xEF && bcLegend[1] == 0xBB), "the default legend has LF line endings and no BOM on every platform");
    var reparsed = written!.Classes.Skip(1).ToList();
    Check(reparsed.Select(c => (c.Names, c.ColorHex)).SequenceEqual(palette.Select(p => (p.Name, p.Hex))), "the default legend parses back to exactly the palette");
    var jsonBack = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "palette.json"))).RootElement;
    Check(jsonBack.GetArrayLength() == 42 && File.ReadAllLines(Path.Combine(outDir, "BetterContinents.gpl")).Count(l => l.Contains("\tAlt: ")) == 32
          && File.ReadAllLines(Path.Combine(outDir, "BetterContinents.gpl")).Count(l => l.Contains("\tBiome: ")) == 10,
      "palette.json has 42 entries; BetterContinents.gpl has 10 biomes and 32 alt biomes");
  }
}

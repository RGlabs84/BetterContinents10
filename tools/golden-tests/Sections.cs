// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for the Forest Scale default (0.10.2).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace GoldenTest;

// What is recorded. Each method reaches the plugin through the members it has today; when the refactor moves one, the
// probe here moves with it and the recorded values must not.
internal static class Sections
{
  static ConfigFile cfg;
  const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

  // The config the plugin binds, and the biome table, as the game has them before a map loads.
  public static void Prepare()
  {
    cfg = new ConfigFile(Path.Combine(Program.Work, "BetterContinents.cfg"), true);
    BC.DeclareConfig(cfg);
    BiomeRegistry.RefreshTable();
  }

  public static void All()
  {
    Prepare();
    Config();
    Legends();
    LegendFuzz.Run();
    Scenarios();
    Presets();
    Export();
    // Last: it binds other config files, which takes the plugin's config entries away from cfg.
    Migration();
  }

  static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

  // ---- the config surface -------------------------------------------------------------------------------------------

  static void Config()
  {
    const string S = "config";
    Golden.Add(S, "count", cfg.Count.ToString());
    foreach (var kv in cfg.OrderBy(kv => kv.Key.Section, StringComparer.Ordinal).ThenBy(kv => kv.Key.Key, StringComparer.Ordinal))
    {
      var e = kv.Value;
      var attrs = e.Description.Tags.OfType<ConfigurationManagerAttributes>().FirstOrDefault();
      Golden.Add(S, $"[{kv.Key.Section}] {kv.Key.Key}",
        $"{e.SettingType.Name} | default={TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType)} | "
        + $"acceptable={e.Description.AcceptableValues?.ToDescriptionString() ?? "-"} | browsable={attrs?.Browsable} | order={attrs?.Order} | "
        + $"desc={e.Description.Description}");
    }
  }

  // ---- legends ------------------------------------------------------------------------------------------------------

  static void Legends()
  {
    const string S = "legends";
    var dir = Path.Combine("fx", "legends");
    Directory.CreateDirectory(dir);
    // A small picture with one pixel of every colour a legend below names, plus colours it does not name.
    static SixLabors.ImageSharp.PixelFormats.Rgba32 Rgba(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);
    uint[] palette = [0x00FF00, 0x007F00, 0xFFFFFF, 0x8B4513, 0x0000FF, 0x10F010, 0x000000, 0xFF0000, 0x123456, 0x7F7F00, 0xA05A3C, 0x6A8CA0, 0x3C5A28, 0x101010, 0xFE0102, 0x00FF80];
    const int size = 4;
    SixLabors.ImageSharp.PixelFormats.Rgba32 Pixel(int x, int y) => Rgba(palette[(y * size + x) % palette.Length]);

    // Biome legends: comments, blanks, a loose name, a bad colour for a vanilla biome, an unknown name, a duplicate, a
    // number, a broken line.
    var biomeLegends = new (string Name, string? Text)[]
    {
      ("missing", null),
      ("default", "None: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF".Replace("|", "\n")),
      ("messy", "# comment\n\nMeadows: 00FF00\nBlack Forest: 7,127,0\nmountain: zzz\nDeadWastes: 8B4513\nOcean: 0000FF\nOcean: 0000FE\n4: 7F7F00\nthis line is broken\nAshLands: FF0000\n"),
    };
    foreach (var (name, text) in biomeLegends)
    {
      var png = Path.Combine(dir, $"biome-{name}.png");
      Fixtures.ColourMap(png, Pixel, size);
      var txt = Path.ChangeExtension(png, ".txt");
      if (text != null) File.WriteAllText(txt, text);
      ImageMapBiome? map = null;
      var log = LogHandler.During(() => map = ImageMapBiome.Create(png));
      var key = $"biome/{name}";
      Golden.Add(S, $"{key}/created", (map != null).ToString());
      Golden.Add(S, $"{key}/legend-file-after", File.Exists(txt) ? File.ReadAllText(txt).Replace("\r", "") : "(none)");
      if (map != null)
      {
        Golden.Add(S, $"{key}/errors", map.LegendErrors.ToString());
        Golden.Add(S, $"{key}/unresolved", string.Join(", ", map.UnresolvedLegend.Select(u => $"{u.Name}={u.Color}")));
        Golden.Add(S, $"{key}/pixels", string.Join(",", Enumerable.Range(0, size * size).Select(i => ((int)map.GetValue((i % size + 0.0f) / (size - 1), (i / size + 0.0f) / (size - 1))).ToString())));
        Golden.Add(S, $"{key}/serialized", Hash.Bytes(map.Serialize()));
      }
      Golden.Lines(S, $"{key}/log", log);
    }

    // Location legends: EWD "Name:Alias", a bad colour, a duplicate name, a comment, a colour two names share.
    var locationLegends = new (string Name, string? Text)[]
    {
      ("missing", null),
      ("messy", "# locs\nStartTemple: 0,255,0\nRunestone_Meadows:alias: 0,255,128\nEikthyrnir: nope\nStartTemple: 255,0,0\nGDKing: 0,0,255\nBonemass: 0,0,255\n"),
    };
    foreach (var (name, text) in locationLegends)
    {
      var png = Path.Combine(dir, $"location-{name}.png");
      // One pixel per colour, the rest black, so every area is one pixel and every pick is certain.
      Fixtures.ColourMap(png, (x, y) => (x + y) % 2 == 0 ? Pixel(x, y) : Rgba(0x000000), size);
      var txt = Path.ChangeExtension(png, ".txt");
      if (text != null) File.WriteAllText(txt, text);
      ImageMapLocation? map = null;
      var log = LogHandler.During(() => map = ImageMapLocation.Create(png));
      var key = $"location/{name}";
      Golden.Add(S, $"{key}/created", (map != null).ToString());
      Golden.Add(S, $"{key}/legend-file-after", File.Exists(txt) ? Hash.Strings(File.ReadAllLines(txt)) : "(none)");
      if (map != null)
        foreach (var kv in map.RemainingAreas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
          Golden.Add(S, $"{key}/areas/{kv.Key}", string.Join(" ", kv.Value.Select(p => $"({F(p.x)},{F(p.y)})")));
      Golden.Lines(S, $"{key}/log", log.Where(l => !l.Contains("Found #") && !l.Contains("Selected ")));
    }

    // Paint and terrain legends ("mask: image colour"), including a line that does not parse.
    var colourLegends = new (string Kind, string Name, string? Text)[]
    {
      ("paint", "missing", null),
      ("paint", "mapped", "80808080: FF0000\nFF000000: 123456\n"),
      ("paint", "broken", "80808080: FF0000\n1,2: 123456\nzz: 00FF00\n"),
      ("terrain", "missing", null),
      ("terrain", "mapped", "Meadows: 00FF00\nOcean: 0000FF\nMountain: FFFFFF\nFF8800FF: 123456\nDefault: 000000\n"),
      ("terrain", "commented", "# a comment\nMeadows: 00FF00\n"),
    };
    foreach (var (kind, name, text) in colourLegends)
    {
      var png = Path.Combine(dir, $"{kind}-{name}.png");
      Fixtures.ColourMap(png, Pixel, size);
      var txt = Path.ChangeExtension(png, ".txt");
      if (text != null) File.WriteAllText(txt, text);
      ImageMapColor? map = null;
      List<string> log;
      try
      {
        log = LogHandler.During(() => map = kind == "paint" ? ImageMapPaint.Create(png) : ImageMapTerrain.Create(png));
      }
      catch (Exception e)
      {
        log = [$"threw {e.GetType().Name}: {e.Message}"];
      }
      var key = $"{kind}/{name}";
      Golden.Add(S, $"{key}/created", (map != null).ToString());
      Golden.Add(S, $"{key}/legend-file-after", File.Exists(txt) ? File.ReadAllText(txt).Replace("\r", "") : "(none)");
      if (map != null)
      {
        Golden.Add(S, $"{key}/source-colors", map.SourceColors);
        var values = new List<string>();
        for (int i = 0; i < size * size; i++)
        {
          bool ok = map.TryGetValue((i % size + 0f) / (size - 1), (i / size + 0f) / (size - 1), out var c);
          values.Add(ok ? $"{F(c.r)},{F(c.g)},{F(c.b)},{F(c.a)}" : "-");
        }
        Golden.Add(S, $"{key}/pixels", string.Join(" ", values));
      }
      Golden.Lines(S, $"{key}/log", log);
    }

    // Vegetation / spawn legends ("colour: entries"), with a comment and a broken line.
    var spawnLegends = new (string Name, string? Text)[]
    {
      ("missing", null),
      ("entries", "# veg\n255,0,0,255: -FirTree, +Beech1\n0,255,0,255: none, +Pinetree_01\nbroken line\n0,0,255: +Birch*\n"),
      ("badcolour", "255,0,0,255: -FirTree\nzz: +Beech1\n0,255,0,255: none\n"),
    };
    foreach (var (name, text) in spawnLegends)
    {
      var png = Path.Combine(dir, $"spawn-{name}.png");
      Fixtures.ColourMap(png, Pixel, size);
      var txt = Path.ChangeExtension(png, ".txt");
      if (text != null) File.WriteAllText(txt, text);
      ImageMapSpawn? map = null;
      var log = LogHandler.During(() => map = ImageMapSpawn.Create(png));
      var key = $"spawn/{name}";
      Golden.Add(S, $"{key}/created", (map != null).ToString());
      Golden.Add(S, $"{key}/legend-file-after", File.Exists(txt) ? File.ReadAllText(txt).Replace("\r", "") : "(none)");
      if (map is { } spawn)
      {
        Golden.Add(S, $"{key}/legend", string.Join(" | ", spawn.LegendColors.Zip(spawn.LegendEntries, (c, e) => $"{c}={e.Data}")));
        Golden.Add(S, $"{key}/pixels", string.Join(" ", Enumerable.Range(0, size * size).Select(i => spawn.GetEntry((i % size + 0f) / (size - 1), (i / size + 0f) / (size - 1))?.Data ?? "-")));
      }
      Golden.Lines(S, $"{key}/log", log);
    }

    // Alt-biome legends: combinations, a forced name, none, a point plant, broken lines, a missing legend.
    var altLegends = new (string Name, string? Text)[]
    {
      ("missing", null),
      ("planted", Fixtures.AltBiomeLegend + "this is not a line\nLantern: zzz\nMushroom: 000000\n"),
    };
    foreach (var (name, text) in altLegends)
    {
      var png = Path.Combine(dir, $"alt-{name}.png");
      Fixtures.ColourMap(png, Pixel, size);
      var txt = Path.ChangeExtension(png, ".txt");
      if (text != null) File.WriteAllText(txt, text);
      ImageMapAltBiome? map = null;
      var log = LogHandler.During(() => map = ImageMapAltBiome.Create(png));
      var key = $"alt/{name}";
      Golden.Add(S, $"{key}/created", (map != null).ToString());
      Golden.Add(S, $"{key}/legend-file-after", File.Exists(txt) ? Hash.Strings(File.ReadAllLines(txt)) : "(none)");
      if (map != null)
      {
        Golden.Add(S, $"{key}/classes", string.Join(" | ", map.Classes.Select(c => c.Label + (c.IsNone ? " none" : ""))));
        Golden.Add(S, $"{key}/pins", string.Join(" | ", map.Pins.Select(p => $"{F(p.X)},{F(p.Z)}->{p.Class}")));
        Golden.Add(S, $"{key}/map", string.Join(",", map.Map));
        Golden.Add(S, $"{key}/block", Hash.Bytes(map.ToBlock()));
      }
      Golden.Lines(S, $"{key}/log", log);
    }
  }

  // ---- world settings: scenarios built the way "From Config" builds a new world ---------------------------------------

  sealed class Scenario(string name, Dictionary<ConfigEntryBase, object?> overrides, Action<BC.BetterContinentsSettings>? tweak = null)
  {
    public readonly string Name = name;
    public readonly Dictionary<ConfigEntryBase, object?> Overrides = overrides;
    public readonly Action<BC.BetterContinentsSettings>? Tweak = tweak;
  }

  static IEnumerable<Scenario> AllScenarios()
  {
    Fixtures.FullFolder(Path.Combine("fx", "all"));
    yield return new("all", new()
    {
      [BC.ConfigEnabled] = true,
      [BC.ConfigMapSourceDir] = "fx/all",
      [BC.ConfigContinentSize] = 0.3f,
      [BC.ConfigSeaLevelAdjustment] = 0.6f,
      [BC.ConfigAshlandsGapEnabled] = true,
      [BC.ConfigDeepNorthGapEnabled] = true,
      [BC.ConfigRiversEnabled] = false,
      [BC.ConfigMapEdgeDropoff] = false,
      [BC.ConfigMountainsAllowedAtCenter] = true,
      [BC.ConfigWorldSize] = 12000f,
      [BC.ConfigEdgeSize] = 600f,
      [BC.ConfigSkipDefaultLocations] = true,
      [BC.ConfigHeightmapAmount] = 1.7f,
      [BC.ConfigHeightmapBlend] = 0.8f,
      [BC.ConfigHeightmapAdd] = 0.1f,
      [BC.ConfigHeightmapMask] = 0.2f,
      [BC.ConfigHeightmapOverrideAll] = false,
      [BC.ConfigRoughmapBlend] = 0.7f,
      [BC.ConfigBiomePrecision] = 3,
      [BC.ConfigForestScale] = 0.7f,
      [BC.ConfigForestAmount] = 0.4f,
      [BC.ConfigForestFactorOverrideAllTrees] = true,
      [BC.ConfigForestmapMultiply] = 0.5f,
      [BC.ConfigForestmapAdd] = 0.25f,
      [BC.ConfigOverrideStartPosition] = true,
      [BC.ConfigStartPositionX] = 123.5f,
      [BC.ConfigStartPositionY] = -456.25f,
      [BC.ConfigHeatScale] = 7.5f,
      [BC.ConfigAltBiomeMode] = "PlantedOnly",
      [BC.ConfigAltBiomeGrid] = "Vanilla",
      [BC.ConfigAltBiomeSeed] = "abc",
      [BC.ConfigAltBiomeChanceMultiplier] = 1.5f,
      [BC.ConfigAltBiomeAmountMultiplier] = 2f,
      [BC.ConfigAltBiomeEdgeScale] = 3f,
      [BC.ConfigAltBiomeDistanceScale] = 0.5f,
      [BC.ConfigAltBiomeMinThickness] = 2f,
      [BC.ConfigAltBiomeMeanHeight] = true,
      [BC.ConfigAltBiomeFixNeighbourCheck] = true,
      [BC.ConfigAltBiomeOverrides] = "Wolf Mountain: enabled=false; *Mistlands: chance=0.3, min=2",
    }, s =>
    {
      // No longer in the config (they have no effect on a new world), still in the save format: an old world holds them.
      s.OceanChannelsEnabled = false;
      s.FixWaterColor = false;
    });

    // The same folder at every default.
    yield return new("all-defaults", new() { [BC.ConfigEnabled] = true, [BC.ConfigMapSourceDir] = "fx/all" });

    Directory.CreateDirectory(Path.Combine("fx", "heightonly"));
    Fixtures.Heightmap(Path.Combine("fx", "heightonly", "heightmap.png"));
    yield return new("heightonly", new() { [BC.ConfigEnabled] = true, [BC.ConfigMapSourceDir] = "fx/heightonly" });

    Directory.CreateDirectory(Path.Combine("fx", "alpha"));
    Fixtures.Heightmap(Path.Combine("fx", "alpha", "heightmap.png"), alpha: true);
    yield return new("alpha", new()
    {
      [BC.ConfigEnabled] = true, [BC.ConfigMapSourceDir] = "fx/alpha", [BC.ConfigHeightmapAlpha] = true, [BC.ConfigHeightmapOverrideAll] = false,
    });

    // Files named one by one (no Directory), one of them quoted as people paste it.
    Directory.CreateDirectory(Path.Combine("fx", "files"));
    Fixtures.Heightmap(Path.Combine("fx", "files", "h.png"));
    Fixtures.ColourMap(Path.Combine("fx", "files", "biomes.png"), Fixtures.Biome);
    File.WriteAllText(Path.Combine("fx", "files", "biomes.txt"), Fixtures.BiomeLegend);
    Fixtures.Grey16Map(Path.Combine("fx", "files", "forest.png"), (x, y) => (x % 7) / 6f);
    yield return new("files", new()
    {
      [BC.ConfigEnabled] = true,
      [BC.ConfigHeightFile] = "fx/files/h.png",
      [BC.ConfigBiomeFile] = "\"fx/files/biomes.png\"",
      [BC.ConfigForestFile] = "fx/files/forest.png",
      [BC.ConfigHeightmapAmount] = 2f,
    });

    // Every scalar off its default, no map: what a world without maps keeps of them.
    yield return new("scalars", new()
    {
      [BC.ConfigEnabled] = true,
      [BC.ConfigContinentSize] = 0.9f,
      [BC.ConfigSeaLevelAdjustment] = 0.1f,
      [BC.ConfigRiversEnabled] = false,
      [BC.ConfigMapEdgeDropoff] = false,
      [BC.ConfigHeightmapAmount] = 3f,
      [BC.ConfigHeatScale] = 2f,
      [BC.ConfigForestScale] = 2.5f,
      [BC.ConfigForestAmount] = 0.9f,
      [BC.ConfigWorldSize] = 8000f,
      [BC.ConfigEdgeSize] = 250f,
      [BC.ConfigBiomePrecision] = 5,
    }, s => s.OceanChannelsEnabled = false);

    // The legacy "Spawnmap File" key and a spawnmap.png beside a missing locationmap.png.
    Directory.CreateDirectory(Path.Combine("fx", "legacy"));
    Fixtures.ColourMap(Path.Combine("fx", "legacy", "spawnmap.png"), Fixtures.Location);
    File.WriteAllText(Path.Combine("fx", "legacy", "spawnmap.txt"), Fixtures.LocationLegend());
    yield return new("legacy-spawnmap", new()
    {
      [BC.ConfigEnabled] = true,
      [BC.ConfigLocationFile] = "fx/legacy/locationmap.png",
    });

    // The legacy flat map (settings 5 and 6): only an old world carries one.
    yield return new("legacy-flat", new() { [BC.ConfigEnabled] = true, [BC.ConfigMapSourceDir] = "fx/all", [BC.ConfigHeightmapOverrideAll] = false }, s =>
    {
      var flat = ImageMapFloat.Create(Path.Combine("fx", "all", "forestmap.png"), false);
      typeof(BC.BetterContinentsSettings).GetField("FlatMap", Any)!.SetValue(s, flat);
      s.FlatmapBlend = 0.6f;
    });
  }

  static byte[] Save(BC.BetterContinentsSettings s, bool network, bool alt = true, int? version = null)
  {
    var pkg = new ZPackage();
    s.Serialize(pkg, network, alt, version);
    return pkg.GetArray();
  }

  static List<string> DumpOf(BC.BetterContinentsSettings s)
  {
    var lines = new List<string>();
    s.Dump(lines.Add);
    return lines;
  }

  static void Scenarios()
  {
    const string S = "settings";
    const string P = "sampling";
    var disabled = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
    Golden.Add(S, "disabled/disk", Hash.Bytes(Save(disabled, false, true, 11)));
    Golden.Add(S, "disabled/network", Hash.Bytes(Save(disabled, true, true, 11)));
    foreach (var sc in AllScenarios())
    {
      BC.BetterContinentsSettings s = null!;
      var log = LogHandler.During(() =>
      {
        s = BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, sc.Overrides), lean: false);
        sc.Tweak?.Invoke(s);
      });
      var key = sc.Name;
      Golden.Lines(S, $"{key}/build-log", log.Where(l => !l.Contains("Found #") && !l.Contains("Selected ")));
      Golden.Lines(S, $"{key}/dump", DumpOf(s));
      Golden.Add(S, $"{key}/loaded-maps", string.Join(", ", s.LoadedImageMaps().Select(m => m.FileName)));
      var disk = Save(s, false, true, 11);
      var net = Save(s, true, true, 11);
      Golden.Add(S, $"{key}/v11/disk", Hash.Bytes(disk));
      Golden.Add(S, $"{key}/v11/network", Hash.Bytes(net));
      Golden.Add(S, $"{key}/v11/network-no-alt", Hash.Bytes(Save(s, true, false, 11)));
      Golden.Add(S, $"{key}/v11/config-version", Hash.Bytes(Save(s, false)));
      Golden.Add(S, $"{key}/cache-id", BC.ZNetPatch.WorldCache.PackageID(new ZPackage(net)));
      // Loaded back and saved again: the same bytes, and the same "bc info".
      var back = BC.BetterContinentsSettings.Load(new ZPackage(disk));
      Golden.Add(S, $"{key}/v11/disk-roundtrip", Save(back, false, true, 11).SequenceEqual(disk) ? "same" : "DIFFERENT " + Hash.Bytes(Save(back, false, true, 11)));
      var backNet = BC.BetterContinentsSettings.Load(new ZPackage(net));
      Golden.Add(S, $"{key}/v11/network-roundtrip", Save(backNet, true, true, 11).SequenceEqual(net) ? "same" : "DIFFERENT " + Hash.Bytes(Save(backNet, true, true, 11)));
      Golden.Add(S, $"{key}/v11/dump-after-load", Hash.Strings(DumpOf(back)));
      for (int v = 1; v <= 10; v++)
      {
        var legacy = Save(s, false, true, v);
        Golden.Add(S, $"{key}/v{v}/disk", Hash.Bytes(legacy));
        Golden.Add(S, $"{key}/v{v}/network", Hash.Bytes(Save(s, true, true, v)));
        try
        {
          var old = BC.BetterContinentsSettings.Load(new ZPackage(legacy));
          Golden.Add(S, $"{key}/v{v}/dump-after-load", Hash.Strings(DumpOf(old)));
        }
        catch (Exception e)
        {
          Golden.Add(S, $"{key}/v{v}/dump-after-load", $"threw {e.GetType().Name}");
        }
      }
      Sample(P, key, s);
      // The settings as a loaded world sees them (paths and all): the same samples, or the ones that differ.
      var before = SampleMap(s);
      var after = SampleMap(back);
      var differ = before.Keys.Where(k => !after.TryGetValue(k, out var v) || v != before[k]).ToList();
      Golden.Add(P, $"{key}/after-load", differ.Count == 0 ? "same" : "DIFFERENT: " + string.Join(", ", differ.Select(k => $"{k} ({before[k]} -> {(after.TryGetValue(k, out var v) ? v : "missing")})")));
      // Saved at the settings' own version, as the game saves a world (12 for one made since 0.10; the probe above
      // writes 11, the format 0.9.4 is compared in, which reads a new world's heightmap alpha the old way).
      var own = SampleMap(BC.BetterContinentsSettings.Load(new ZPackage(Save(s, false, true, s.SavedVersion))));
      var differOwn = before.Keys.Where(k => !own.TryGetValue(k, out var v) || v != before[k]).ToList();
      Golden.Add(P, $"{key}/after-load-own-version", differOwn.Count == 0 ? "same" : "DIFFERENT: " + string.Join(", ", differOwn.Select(k => $"{k} ({before[k]} -> {(own.TryGetValue(k, out var v) ? v : "missing")})")));
    }
  }

  // ---- sampling -----------------------------------------------------------------------------------------------------

  const int G = 60;
  static IEnumerable<(float U, float V)> Grid()
  {
    for (int j = 0; j <= G; j++)
      for (int i = 0; i <= G; i++)
        yield return (i / (float)G, j / (float)G);
    // Off the pixel grid, and the edges.
    for (int j = 0; j < 17; j++)
      for (int i = 0; i < 17; i++)
        yield return ((i + 0.37f) / 17f, (j + 0.61f) / 17f);
  }
  static float W(float u) => (u - 0.5f) * BC.TotalSize;

  static Dictionary<string, string> SampleMap(BC.BetterContinentsSettings s)
  {
    var values = new Dictionary<string, string>();
    SampleInto(s, (k, v) => values[k] = v);
    return values;
  }

  static void Sample(string section, string key, BC.BetterContinentsSettings s) => SampleInto(s, (k, v) => Golden.Add(section, $"{key}/{k}", v));

  static void SampleInto(BC.BetterContinentsSettings s, Action<string, string> add)
  {
    var saved = BC.Settings;
    BC.Settings = s;
    try
    {
      var grid = Grid().ToList();
      add("heightmap", Hash.Floats(grid.Select(p => s.ApplyHeightmap(p.U, p.V, 0.3f + 0.1f * MathF.Sin(p.U * 9f)))));
      add("roughmap", Hash.Floats(grid.Select(p => s.ApplyRoughmap(p.U, p.V, 0.2f, 0.5f + p.V * 0.1f))));
      add("flatmap", Hash.Floats(grid.Select(p => s.ApplyFlatmap(p.U, p.V, 0.25f, 0.4f + p.U * 0.2f))));
      add("forest", Hash.Floats(grid.Select(p => s.ApplyForest(p.U, p.V, 0.1f + 1.9f * p.V))));
      add("heat", Hash.Floats(grid.Select(p => s.ApplyHeatmap(p.U, p.V))));
      add("biome", Hash.Floats(grid.Select(p => (float)(int)s.GetBiomeOverride(p.U, p.V))));
      foreach (var biome in new[] { Heightmap.Biome.AshLands, Heightmap.Biome.Mistlands, Heightmap.Biome.Meadows })
        add($"paint-{biome}", Hash.Floats(grid.SelectMany(p =>
        {
          var mask = new Color(0.1f, 0.2f, 0.3f, 0.4f);
          s.ApplyPaintMap(W(p.U), W(p.V), biome, ref mask);
          return new[] { mask.r, mask.g, mask.b, mask.a };
        })));
      add("terrain", Hash.Floats(grid.SelectMany(p =>
      {
        var c = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        bool hit = s.ApplyTerrainMap(W(p.U), W(p.V), ref c);
        return new[] { hit ? 1f : 0f, c.r, c.g, c.b, c.a };
      })));
      foreach (var field in new[] { "VegetationMap", "SpawnMap" })
      {
        var map = typeof(BC.BetterContinentsSettings).GetField(field, Any)!.GetValue(s) as ImageMapSpawn;
        add(field, map == null ? "none" : Hash.Strings(grid.Select(p => map.GetEntry(p.U, p.V)?.Data ?? "-")));
      }
      foreach (var (name, _, _, _) in Fixtures.Locations)
        add($"locations/{name}", string.Join(" ", s.GetAllSpawns(name).Select(v => $"({F(v.x)},{F(v.y)})")));
      add("locations/Runestone_Meadows", string.Join(" ", s.GetAllSpawns("Runestone_Meadows").Select(v => $"({F(v.x)},{F(v.y)})")));

      // The world generator's patches, as the game calls them.
      BC.WorldGeneratorPatch.ApplyNoiseSettings();
      add("base-height-v3", Hash.Floats(grid.Select(p =>
      {
        float wx = W(p.U), wy = W(p.V), r = 0f;
        BC.WorldGeneratorPatch.GetBaseHeightPrefixV3(ref wx, ref wy, ref r, 1000f);
        return r;
      })));
      add("getbiome-prefix", Hash.Floats(grid.SelectMany(p =>
      {
        var r = Heightmap.Biome.Swamp;
        bool run = BC.WorldGeneratorPatch.GetBiomePrefix(W(p.U), W(p.V), ref r);
        return new[] { run ? 1f : 0f, (float)(int)r };
      })));
      add("isashlands", Hash.Floats(grid.SelectMany(p =>
      {
        bool r1 = false, r2 = false;
        bool run1 = BC.WorldGeneratorPatch.IsAshlandsPrefix(W(p.U), W(p.V), ref r1);
        bool run2 = BC.WorldGeneratorPatch.IsAshlandsFallbackPrefix(W(p.U), W(p.V), ref r2);
        return new[] { run1 ? 1f : 0f, r1 ? 1f : 0f, run2 ? 1f : 0f, r2 ? 1f : 0f };
      })));
      add("isdeepnorth", Hash.Floats(grid.SelectMany(p =>
      {
        bool r = false;
        double fade = -1;
        bool run = BC.WorldGeneratorPatch.IsDeepnorthPrefix(W(p.U), W(p.V), ref r);
        bool run2 = BC.WorldGeneratorPatch.DeepNorthWaveFadePrefix(W(p.U), W(p.V), ref fade);
        return new[] { run ? 1f : 0f, r ? 1f : 0f, run2 ? 1f : 0f, (float)fade };
      })));
      add("ashlands-gradient", Hash.Floats(grid.Select(p =>
      {
        float r = 0f;
        BC.WorldGeneratorPatch.GetAshlandsOceanGradientPrefix(W(p.U), W(p.V), ref r);
        return r;
      })));
      add("forest-factor", Hash.Floats(grid.SelectMany(p =>
      {
        var pos = new Vector3(W(p.U), 0f, W(p.V));
        BC.WorldGeneratorPatch.GetForestFactorPrefix(ref pos);
        float r = 0.2f + 1.5f * p.U;
        BC.WorldGeneratorPatch.GetForestFactorPostfix(pos, ref r);
        return new[] { pos.x, pos.z, r };
      })));
      add("precision", BC.EffectiveBiomePrecision(s).ToString());
      add("override-all", s.ShouldHeightMapOverrideAll.ToString());
      var alt = s.AltBiomeMapData;
      add("altbiome-map", alt == null ? "none" : $"classes={string.Join(" | ", alt.Classes.Select(c => c.Label))} map={Hash.Bytes(alt.Map)}");
      add("altbiome-settings", s.AltBiomes == null ? "null" : Hash.Bytes(s.AltBiomes.Serialize()));
    }
    finally
    {
      BC.Settings = saved;
    }
  }

  // ---- the shipped presets ------------------------------------------------------------------------------------------

  static void Presets()
  {
    const string S = "presets";
    foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "presets"), "*.BetterContinents").OrderBy(f => f, StringComparer.Ordinal))
    {
      var key = Path.GetFileNameWithoutExtension(file);
      var s = BC.BetterContinentsSettings.Load(file);
      Golden.Add(S, $"{key}/file", Hash.Bytes(File.ReadAllBytes(file)));
      Golden.Lines(S, $"{key}/dump", DumpOf(s));
      Golden.Add(S, $"{key}/v11/disk", Hash.Bytes(Save(s, false, true, 11)));
      Golden.Add(S, $"{key}/v11/network", Hash.Bytes(Save(s, true, true, 11)));
      Golden.Add(S, $"{key}/v7/disk-roundtrip", Save(s, false, true, 7).SequenceEqual(ReadPackage(file)) ? "same" : "DIFFERENT");
      Sample(S, key, s);
    }
  }

  static byte[] ReadPackage(string file)
  {
    var bytes = File.ReadAllBytes(file);
    int count = BitConverter.ToInt32(bytes, 0);
    return bytes.Skip(4).Take(count).ToArray();
  }

  // ---- one-time changes to an existing BetterContinents.cfg -----------------------------------------------------------

  static void Migration()
  {
    const string S = "migration";
    // Step 1 is the export's Default Heightmap Amount, step 2 the Debug Reset Command (zone regeneration), step 3 Forest Scale;
    // Config Version records the last made.
    const string Debug = "[00 BetterContinents.Debug]\nDebug Reset Command = ";
    const string Forest = "[04 BetterContinents.Forest]\nForest Scale = ";
    var cases = new (string Name, string? File)[]
    {
      ("new-file", null),
      ("0.9.x-file", "[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("0.9.x-file-amount-3", "[09 BetterContinents.Export]\nDefault Heightmap Amount = 3\n"),
      ("migrated-to-1", "[07 BetterContinents.Misc]\nConfig Version = 1\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("already-migrated", "[07 BetterContinents.Misc]\nConfig Version = 2\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("0.9.x-file-debug-default", Debug + "zones_reset start\n"),
      ("0.9.x-file-debug-custom", Debug + "zones_reset start safezones=3\n"),
      ("0.9.x-file-both-defaults", Debug + "zones_reset start\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("migrated-to-1-debug-default", "[07 BetterContinents.Misc]\nConfig Version = 1\n\n" + Debug + "zones_reset start\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("already-migrated-debug-default", "[07 BetterContinents.Misc]\nConfig Version = 2\n\n" + Debug + "zones_reset start\n"),
      ("newer-file", "[07 BetterContinents.Misc]\nConfig Version = 4\n\n" + Debug + "zones_reset start\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n\n" + Forest + "1\n"),
      ("0.10.x-file-forest-default", "[07 BetterContinents.Misc]\nConfig Version = 2\n\n" + Forest + "1\n"),
      ("0.10.x-file-forest-custom", "[07 BetterContinents.Misc]\nConfig Version = 2\n\n" + Forest + "0.7\n"),
      ("0.10.x-file-forest-above-1", "[07 BetterContinents.Misc]\nConfig Version = 2\n\n" + Forest + "2.5\n"),
      ("0.9.x-file-all-defaults", Debug + "zones_reset start\n\n" + Forest + "1\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n"),
      ("already-migrated-forest-default", "[07 BetterContinents.Misc]\nConfig Version = 3\n\n" + Forest + "1\n"),
    };
    foreach (var (name, text) in cases)
    {
      var path = Path.Combine(Program.Work, $"migration-{name}.cfg");
      if (text != null)
        File.WriteAllText(path, text);
      var file = new ConfigFile(path, true);
      List<string> log = [];
      log = LogHandler.During(() =>
      {
        BC.DeclareConfig(file);
        SettingsSchema.Migrate();
      });
      Golden.Add(S, $"{name}/export-amount", F(BC.ConfigExportHeightmapAmount.Value));
      Golden.Add(S, $"{name}/config-version", BC.ConfigFileVersion.Value.ToString());
      Golden.Add(S, $"{name}/debug-reset-command", BC.ConfigDebugResetCommand.Value);
      Golden.Add(S, $"{name}/forest-scale", F(BC.ConfigForestScale.Value));
      Golden.Lines(S, $"{name}/log", log);
    }
  }

  // ---- the export ---------------------------------------------------------------------------------------------------

  static void Export()
  {
    const string S = "export";
    Golden.Add(S, "defaults", WorldExport.Options.Default().ToString());
    Golden.Add(S, "usage", WorldExportCommands.OptionsUsage);
    Golden.Add(S, "format", WorldExport.Format);

    // export.cfg and README.txt of a Better Continents world, every map written, at fixed options.
    var s = BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, new Dictionary<ConfigEntryBase, object?> { [BC.ConfigEnabled] = true, [BC.ConfigMapSourceDir] = "fx/all", [BC.ConfigBiomePrecision] = 2 }), lean: false);
    foreach (var (name, amount, exact, edge) in new (string, float, bool, bool?)[] { ("amount2", 2f, false, null), ("amount1-exact-edgeoff", 1f, true, false) })
    {
      var o = WorldExport.Options.Default();
      o.HeightmapAmount = amount;
      o.SeaLevel = 0.5f;
      o.ForestExact = exact;
      o.EdgeDropoff = edge;
      o.Size = 2048;
      var job = MakeJob(o, Path.Combine(Program.Work, "BetterContinents", "Golden", "export-2026-10-04-12-00-00"), s);
      var jobType = job.GetType();
      var config = (List<string>)jobType.GetMethod("ConfigLines", Any)!.Invoke(job, null)!;
      Golden.Lines(S, $"{name}/export.cfg", config.Select(l => l.Replace(Program.Work, "<work>")));
      var readme = (List<string>)jobType.GetMethod("ReadmeLines", Any)!.Invoke(job, null)!;
      Golden.Lines(S, $"{name}/README.txt", readme.Select(l => l.Replace(Program.Work, "<work>").Replace(ModInfo.Version, "<version>").Replace(global::Version.GetVersionString(), "<game>")));
    }
  }

  static object MakeJob(WorldExport.Options o, string dir, BC.BetterContinentsSettings settings)
  {
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var job = RuntimeHelpers.GetUninitializedObject(jobType);
    void Set(string name, object? value) => jobType.GetField(name, Any)!.SetValue(job, value);
    Set("O", o);
    Set("Dir", dir);
    Set("World", new World { m_name = "Golden", m_seedName = "GoldenSeed", m_seed = 7 });
    Set("Settings", settings);
    Set("BcWorld", true);
    Set("Size", o.Size);
    Set("Total", BC.TotalSize);
    Set("WorldR", BC.WorldRadius);
    Set("TotalR", BC.TotalRadius);
    Set("Sla", WorldExportMath.SeaLevelAdjustment(o.SeaLevel));
    Set("EdgeDropoff", o.EdgeDropoff ?? true);
    Set("ZoneBlend", o.ZoneBlend);
    Set("OnServer", true);
    Set("Role", "host");
    Set("Workers", 1);
    Set("SourceForestScale", settings.ForestScale);
    Set("ImportForestScale", settings.ForestScale);
    Set("ForestScaleText", "1");
    Set("SourceForestAmount", settings.ForestAmount);
    Set("ForestOverridesAllTrees", settings.ForestFactorOverrideAllTrees);
    Set("WorldSizeSetting", settings.WorldSize);
    Set("EdgeSizeSetting", settings.EdgeSize);
    Set("AltGrid", settings.EffectiveAltBiomes.Grid);
    Set("Clock", System.Diagnostics.Stopwatch.StartNew());
    Set("Started", new DateTime(2026, 10, 4, 12, 0, 0));
    Set("Written", new List<string> { "heightmap.png", "biomemap.png", "biomemap.txt", "locationmap.png", "locationmap.txt", "forestmap.png", "heatmap.png", "altbiomemap.png", "altbiomemap.txt", "lavamap.png", "mossmap.png", "paintmap.png", "paintmap.txt", "sources/bc-settings.txt" });
    Set("Notes", new List<string> { "Only the generated world is exported: player terrain edits, buildings and placed objects are not." });
    Set("Tracked", new List<string>());
    Set("BiomePixels", new long[BiomeRegistry.IndexCount]);
    Set("MinMetres", 1f);
    Set("MaxMetres", 300f);
    Set("LocationsFull", true);
    Set("LocationsGenerated", true);
    foreach (var flag in new[] { "LocationsWritten", "AltBiomesWritten", "HeatWritten", "ForestWritten", "HeightsWritten" })
      Set(flag, true);
    return job;
  }
}

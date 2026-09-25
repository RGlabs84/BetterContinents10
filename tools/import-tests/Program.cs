// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

// Offline checks of Better Continents 0.9.0's world import (WorldImport). A synthetic export folder is written with
// ImageSharp and made into a New World preset by the real builder, on a worker thread as in the game, then read back with
// BetterContinentsSettings.Load: every setting and every map must come back. Also the preset's picture, the lean build,
// a folder without export.cfg, broken maps, the config way (and its backup), the folder list and bc_import's names, and
// the export's own preset step. Loads the real pre-ILRepack BetterContinents.dll, the game's assemblies and BepInEx, and
// binds the whole config with the plugin's own DeclareConfig, so the sections, keys, defaults and ranges are the game's.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace ImportTest;

internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines) Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }
  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines) Lines.Add("[Exception] " + exception);
  }
  public static bool Has(string part) { lock (Lines) return Lines.Any(l => l.Contains(part)); }
}

internal static class Program
{
  static int checks, failures;
  static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }
  static void Section(string s) => System.Console.WriteLine("== " + s);

  // The synthetic export: N x N pixels, file row 0 = north.
  const int N = 256;
  static string work, root, presetsDir, cfgPath;
  static ConfigFile cfg;

  static int Main()
  {
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      "/home/rohan/WubarrkCODING/libs-Tools/1.0/client",
      "/home/rohan/WubarrkCODING/libs-Tools",
      "/home/rohan/WubarrkCODING/libs-Tools/BepInEx/core",
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
    };
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
      foreach (var d in dirs)
      {
        var p = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(p))
          return ctx.LoadFromAssemblyPath(p);
      }
      return null;
    };
    return Run();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run()
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    work = Path.Combine(Path.GetTempPath(), "bc-import-test-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    try
    {
      Bind();
      Parsing();
      Names();
      var folder = Path.Combine(root, "Test World", "export-2026-09-24-12-00-00");
      MakeFolder(folder, withConfig: true, manifest: ("Test World", N, "2026-09-24T12:00:00"));
      var preset = Preset(folder);
      Thumbnail(preset);
      Reimport(folder);
      Lean(folder);
      NoConfig();
      BrokenMaps();
      ConfigWay(folder, preset);
      Listing(folder);
      ExportStep();
    }
    catch (Exception e)
    {
      System.Console.WriteLine("CRASH " + e);
      failures++;
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
    System.Console.WriteLine($"{checks} checks, {failures} failures");
    return failures == 0 ? 0 : 1;
  }

  // ---- the config -------------------------------------------------------------------------------------------------

  static void Bind()
  {
    Section("binding the whole config with the plugin's DeclareConfig");
    cfgPath = Path.Combine(work, "BetterContinents.cfg");
    cfg = new ConfigFile(cfgPath, true);
    BC.DeclareConfig(cfg);
    C(BC.ConfigEnabled != null && BC.ConfigMapSourceDir != null && BC.ConfigBiomePrecision != null && BC.ConfigAltBiomeMode != null && BC.ConfigExportHud != null,
      "every group is bound (Debug, Biomemap, AltBiomes, Export)");
    C(cfg.Count >= 60, $"{cfg.Count} settings bound");
    C(BC.ConfigBiomePrecision.Definition.Section == "03 BetterContinents.Biomemap" && BC.ConfigExportHud.Definition.Section == "09 BetterContinents.Export",
      "the sections carry the game's numbers");
    root = Path.Combine(work, "save", "BetterContinents");
    presetsDir = Path.Combine(root, "presets");
    WorldImport.RootDir = root;
    Presets.PresetsDir = presetsDir;
    Presets.WatchSelection();
    bool threw = false;
    try { BC.ConfigSelectedPreset.Value = "Disabled"; } catch { threw = true; }
    C(!threw, "a selection change without a New World screen (Presets.Active is null) is harmless");
  }

  static void Parsing()
  {
    Section("export.cfg is read the way BepInEx reads a config");
    var parsed = WorldImport.ParseConfig(["## a comment", "# another", "[00 A]", "Key = 1", "  Key2=  two words  ", "no value here", "[01 B]", "Key = x=y", "[00 A]", "Key = 3", "Empty ="]);
    C(parsed.Count == 5, $"five settings lines ({parsed.Count})");
    C(parsed[0] == ("00 A", "Key", "1") && parsed[1] == ("00 A", "Key2", "two words") && parsed[2] == ("01 B", "Key", "x=y")
      && parsed[3] == ("00 A", "Key", "3") && parsed[4] == ("00 A", "Empty", ""), "sections, trimming, only the first '=' splits, an empty value is kept");
  }

  static void Names()
  {
    Section("preset names");
    C(WorldImport.PresetNameFor("/x/BetterContinents/Midgard/export-2026-09-24-14-05-33") == "Midgard 2026-09-24 14-05-33", "export-<time> of a world: '<world> <yyyy-MM-dd HH-mm-ss>'");
    C(WorldImport.PresetNameFor("/x/BetterContinents/Midgard/export-2026-09-24-14-05-33-2/") == "Midgard 2026-09-24 14-05-33 (2)", "a second export in the same second, and a trailing slash");
    var odd = WorldImport.PresetNameFor("/x/y/export-2026-09-24-14-05-33", "My.World: v1*?<>|\"\\/");
    C(odd.IndexOfAny(".:*?<>|\"\\/".ToCharArray()) < 0 && odd.EndsWith("2026-09-24 14-05-33") && odd.StartsWith("My_World_ v1"), $"unsafe characters and dots become '_' ('{odd}')");
    C(BC.GetBCFile(odd) == odd + ".BetterContinents", "no dot is left for GetBCFile to take for an extension");
    C(WorldImport.PresetNameFor("/x/my  maps") == "my maps", "any other folder: its own name, blanks collapsed");
    C(WorldImport.SafePresetName("   ") == "Imported maps" && WorldImport.SafePresetName("...") == "Imported maps", "nothing left: 'Imported maps'");
    C(WorldImport.SafePresetName(new string('a', 200)).Length == 80, "at most 80 characters");
  }

  // ---- the synthetic export ------------------------------------------------------------------------------------------

  static readonly Heightmap.Biome[] ExpectedBiome = new Heightmap.Biome[N * N];
  static readonly ushort[] ExpectedHeight = new ushort[N * N];

  // The height value (0..1) of file pixel (x, y): an island with bumps. At amount 2.5 and sea level 0.45 the sea is below 0.08.
  static float HeightValue(int x, int y)
  {
    float u = x / (N - 1f) - 0.5f, w = y / (N - 1f) - 0.5f;
    return Mathf.Clamp01(0.45f - Mathf.Sqrt(u * u + w * w) * 0.9f + 0.05f * Mathf.Sin(x * 0.2f) * Mathf.Cos(y * 0.15f));
  }

  static Heightmap.Biome BiomeAt(int x, int y)
  {
    if (HeightValue(x, y) < 0.08f) return Heightmap.Biome.Ocean;
    bool west = x < N / 2, north = y < N / 2;
    return north ? (west ? Heightmap.Biome.Meadows : Heightmap.Biome.BlackForest) : (west ? Heightmap.Biome.Plains : Heightmap.Biome.Mountain);
  }

  static readonly Rgba32 Magenta = new(255, 0, 255, 255);
  static readonly (int X, int Y) Temple = (N / 2, N / 2), Eikthyr = (N / 4, N / 4);

  static void MakeFolder(string dir, bool withConfig, (string World, int Size, string At)? manifest)
  {
    Directory.CreateDirectory(dir);
    var png16 = new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 };
    var png8 = new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 };
    var colours = ImageMapBiome.DefaultColorTable();
    using (var h = new Image<L16>(N, N))
    using (var b = new Image<Rgb24>(N, N))
    using (var f = new Image<L16>(N, N))
    using (var heat = new Image<L16>(N, N))
    using (var lava = new Image<L8>(N, N))
    using (var moss = new Image<L8>(N, N))
    using (var paint = new Image<Rgb24>(N, N))
    using (var alt = new Image<Rgb24>(N, N))
    using (var loc = new Image<Rgb24>(N, N))
    using (var veg = new Image<Rgba32>(N, N))
    {
      for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
          var v = (ushort)Mathf.RoundToInt(HeightValue(x, y) * 65535f);
          ExpectedHeight[y * N + x] = v;
          h[x, y] = new L16(v);
          var biome = BiomeAt(x, y);
          ExpectedBiome[y * N + x] = biome;
          var c = colours[biome];
          b[x, y] = new Rgb24(c.r, c.g, c.b);
          f[x, y] = new L16((ushort)(x * 65535 / (N - 1)));
          heat[x, y] = new L16((ushort)(y > N * 3 / 4 ? (y - N * 3 / 4) * 1000 : 0));
          lava[x, y] = new L8((byte)(x > N * 3 / 4 && y > N * 3 / 4 ? 200 : 0));
          moss[x, y] = new L8((byte)(x < N / 4 && y < N / 4 ? 150 : 0));
          paint[x, y] = new Rgb24((byte)x, (byte)y, 128);
          alt[x, y] = x >= 40 && x < 100 && y >= 40 && y < 100 ? new Rgb24(0xAA, 0x33, 0x77)
            : x >= 160 && x < 220 && y >= 160 && y < 220 ? new Rgb24(0x33, 0xAA, 0x77) : new Rgb24(0, 0, 0);
          veg[x, y] = x < N / 2 ? Magenta : y < N / 2 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 255);
        }
      loc[Temple.X, Temple.Y] = new Rgb24(255, 0, 0);
      loc[Eikthyr.X, Eikthyr.Y] = new Rgb24(255, 153, 0);
      h.SaveAsPng(Path.Combine(dir, "heightmap.png"), png16);
      b.SaveAsPng(Path.Combine(dir, "biomemap.png"));
      f.SaveAsPng(Path.Combine(dir, "forestmap.png"), png16);
      heat.SaveAsPng(Path.Combine(dir, "heatmap.png"), png16);
      lava.SaveAsPng(Path.Combine(dir, "lavamap.png"), png8);
      moss.SaveAsPng(Path.Combine(dir, "mossmap.png"), png8);
      paint.SaveAsPng(Path.Combine(dir, "paintmap.png"));
      alt.SaveAsPng(Path.Combine(dir, "altbiomemap.png"));
      loc.SaveAsPng(Path.Combine(dir, "locationmap.png"));
      veg.SaveAsPng(Path.Combine(dir, "vegetationmap.png"));
    }
    File.WriteAllLines(Path.Combine(dir, "biomemap.txt"), ImageMapBiome.DefaultColors.Split('|'));
    File.WriteAllLines(Path.Combine(dir, "paintmap.txt"), ["# The paint map needs no colour list. Every pixel is used as it is"]);
    File.WriteAllLines(Path.Combine(dir, "altbiomemap.txt"), ["# test legend", "Dark Meadows: AA3377", "Wolf Mountain: 33AA77"]);
    File.WriteAllLines(Path.Combine(dir, "locationmap.txt"), ["StartTemple: 255,0,0", "Eikthyrnir: 255,153,0"]);
    File.WriteAllLines(Path.Combine(dir, "vegetationmap.txt"), ["255,0,255,255: Beech1, Birch1"]);
    if (withConfig)
      File.WriteAllLines(Path.Combine(dir, "export.cfg"),
      [
        "## A test export.cfg: unusual values, a later duplicate, keys that are not world settings, and one out of range.",
        "[00 BetterContinents.Debug]", "Enabled = true", "Directory = /somewhere/it/was/exported/first", "Override version = ", "Debug Mode = true",
        "[01 BetterContinents.Global]", "World Size = 9000", "Edge Size = 400", "Map Edge Drop-off = false", "Skip Default Locations = true",
        "Sea Level Adjustment = 0.45", "Rivers = false", "Mountains Allowed At Center = true", "Continent Size = 2",
        "[02 BetterContinents.Heightmap]", "Heightmap Amount = 2.5", "Heightmap Blend = 1", "Heightmap Add = 0", "Heightmap Mask = 0",
        "Heightmap Override All = true", "Heightmap Alpha = false",
        "[03 BetterContinents.Biomemap]", "Biome precision = 3",
        "[04 BetterContinents.Forest]", "Forest Scale = 0.7", "Forest Amount = 0.5", "Forestmap Multiply = 0", "Forestmap Add = 1",
        "Forest Factor Overrides All Trees = true",
        "[05 BetterContinents.StartPosition]", "Override Start Position = true", "Start Position X = 123", "Start Position Y = -456",
        "[06 BetterContinents.Maps]", "Heatmap Scale = 50",
        "[07 BetterContinents.Misc]", "SelectedPreset = From Config",
        "[08 BetterContinents.AltBiomes]", "Mode = PlantedOnly", "Grid = Vanilla",
        "[09 BetterContinents.Export]", "Hud = true",
        "[99 Nonsense]", "Bogus = 1",
        "[06 BetterContinents.Maps]", "Heatmap Scale = 12",
      ]);
    if (manifest is { } m)
      File.WriteAllText(Path.Combine(dir, "manifest.json"),
        $"{{\n  \"format\": \"bc-export/1\",\n  \"modVersion\": \"0.9.0\",\n  \"exportedAt\": \"{m.At}\",\n  \"worldName\": \"{m.World}\",\n  \"size\": {m.Size},\n  \"files\": []\n}}");
  }

  static Dictionary<ConfigEntryBase, object> ConfigState() => cfg.ToDictionary(kv => kv.Value, kv => kv.Value.BoxedValue);

  static T Map<T>(BC.BetterContinentsSettings s, string file) where T : class =>
    s.LoadedImageMaps().Where(m => m.FileName == file).Select(m => m.Map as T).FirstOrDefault();

  // The decoded pixels a map keeps (the private Map array, declared on the map's class or a base class).
  static float FieldLength(object map)
  {
    for (var t = map.GetType(); t != null; t = t.BaseType)
    {
      var f = t.GetField("Map", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
      if (f != null)
        return ((Array)f.GetValue(map)!).Length;
    }
    throw new InvalidOperationException("no Map field");
  }

  // ---- the preset --------------------------------------------------------------------------------------------------

  static string Preset(string folder)
  {
    Section("a synthetic export made into a preset, then read back with BetterContinentsSettings.Load");
    var before = ConfigState();
    var fileBefore = File.ReadAllText(cfgPath);
    var plan = WorldImport.MakePlan(folder);
    C(plan.PresetName == "Test World 2026-09-24 12-00-00", $"the preset is named after the world and the export time ('{plan.PresetName}')");
    C(plan.HadConfig && plan.Ignored.Any(l => l.Contains("Debug Mode")) && plan.Ignored.Any(l => l.Contains("[09 BetterContinents.Export] Hud"))
      && plan.Ignored.Any(l => l.Contains("Bogus")) && !plan.Ignored.Any(l => l.Contains("SelectedPreset")),
      "debug switches, the live Export group and unknown keys are ignored (and said so); SelectedPreset silently");
    // On a worker, as the game runs it.
    var outcome = Task.Run(() => WorldImport.BuildPreset(plan)).Result;
    C(outcome.Error == null && outcome.PresetPath == plan.PresetPath && File.Exists(plan.PresetPath), $"the preset is written ({outcome.Error ?? "no error"})");
    C(outcome.Warnings.Count == 0, "every map in the folder loaded" + (outcome.Warnings.Count > 0 ? ": " + string.Join("; ", outcome.Warnings) : ""));
    System.Console.WriteLine("    " + outcome.Summary);
    C(ConfigState().All(kv => Equals(before[kv.Key], kv.Value)) && File.ReadAllText(cfgPath) == fileBefore,
      "building it changed no config value and did not touch BetterContinents.cfg");

    var s = BC.BetterContinentsSettings.Load(plan.PresetPath);
    C(s.EnabledForThisWorld && s.Version == BC.BetterContinentsSettings.MaxVersion, $"it loads, enabled, in the newest settings format ({s.Version})");
    C(s.WorldSize == 9000f && s.EdgeSize == 400f, "World Size 9000, Edge Size 400");
    C(Mathf.Abs(s.SeaLevel - 0.45f) < 1e-5f && s.HeightmapAmount == 2.5f && s.HeightmapBlend == 1f && s.HeightmapAdd == 0f && s.HeightmapMask == 0f
      && s.HeightmapOverrideAll && !s.HeightMapAlpha, "sea level 0.45, Heightmap Amount 2.5, blend 1, add 0, mask 0, override all, no alpha");
    C(!s.MapEdgeDropoff && s.SkipDefaultLocations && !s.RiversEnabled && s.MountainsAllowedAtCenter, "no edge drop-off, skip default locations, no rivers, mountains at the centre");
    C(s.BiomePrecision == 3, $"Biome precision 3 ({s.BiomePrecision})");
    C(Mathf.Abs(s.ForestScaleFactor - 0.7f) < 1e-4f && Mathf.Abs(s.ForestAmount - 0.5f) < 1e-5f && s.ForestmapMultiply == 0f && s.ForestmapAdd == 1f && s.ForestFactorOverrideAllTrees,
      $"Forest Scale 0.7 ({s.ForestScaleFactor}), amount 0.5, multiply 0, add 1, overrides all trees");
    C(s.OverrideStartPosition && s.StartPositionX == 123f && s.StartPositionY == -456f, "start position 123, -456");
    C(s.HeatMapScale == 12f, $"Heatmap Scale: the later of two lines wins (12, got {s.HeatMapScale})");
    C(Mathf.Abs(s.ContinentSize - 1f) < 1e-4f, $"an out-of-range Continent Size (2) is clamped as BepInEx would (1, got {s.ContinentSize})");
    C(s.AltBiomes != null && s.AltBiomes.Mode == BC.AltBiomeMode.PlantedOnly && s.AltBiomes.Grid == BC.AltBiomeGridMode.Vanilla, "alt biomes PlantedOnly on the vanilla grid");
    C(s.FixWaterColor && s.OceanChannelsEnabled, "settings export.cfg does not name come from the config (Fix Water Color, Ocean Channels: on)");

    var names = s.LoadedImageMaps().Select(m => m.FileName).ToHashSet();
    string[] want = ["heightmap.png", "biomemap.png", "locationmap.png", "forestmap.png", "heatmap.png", "paintmap.png", "lavamap.png", "mossmap.png", "vegetationmap.png", "altbiomemap.png"];
    C(want.All(names.Contains), "every map is in the preset: " + string.Join(", ", want.Where(w => !names.Contains(w)).DefaultIfEmpty("none missing")));

    foreach (var file in new[] { "heightmap.png", "forestmap.png", "heatmap.png", "lavamap.png", "mossmap.png", "paintmap.png" })
    {
      var map = Map<ImageMapBase>(s, file);
      C(map != null && map.SourceData.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, file))), $"{file}: the preset holds the file's bytes exactly");
    }
    var hm = Map<ImageMapFloat>(s, "heightmap.png");
    int heightBad = 0, biomeBad = 0;
    var bm = Map<ImageMapBiome>(s, "biomemap.png");
    for (int y = 0; y < N; y += 3)
      for (int x = 0; x < N; x += 3)
      {
        float u = x / (N - 1f), v = (N - 1 - y) / (N - 1f);
        if (Mathf.Abs(hm.GetValue(u, v) - ExpectedHeight[y * N + x] / 65535f) > 1e-6f) heightBad++;
        if (bm.GetValue(u, v) != ExpectedBiome[y * N + x]) biomeBad++;
      }
    C(heightBad == 0, $"heights decode to the written pixels, north at the top ({heightBad} differ)");
    C(biomeBad == 0, $"biomes decode to the painted ones through the legend ({biomeBad} differ)");
    var paint = Map<ImageMapPaint>(s, "paintmap.png");
    C(paint.TryGetValue(10 / (N - 1f), (N - 1 - 20) / (N - 1f), out var pc) && Mathf.Abs(pc.r - 10 / 255f) < 1e-3f && Mathf.Abs(pc.g - 20 / 255f) < 1e-3f,
      "the paint map decodes to its pixels (the comment in paintmap.txt is not a colour mapping)");
    var ab = Map<ImageMapAltBiome>(s, "altbiomemap.png");
    string AltAt(int x, int y) { var k = ab.GetClass(x / (N - 1f), (N - 1 - y) / (N - 1f)); return k == 0 ? "" : ab.Classes[k].Names; }
    C(AltAt(70, 70) == "Dark Meadows" && AltAt(190, 190) == "Wolf Mountain" && AltAt(10, 200) == "", $"alt biomes are planted where painted ({AltAt(70, 70)}, {AltAt(190, 190)}, '{AltAt(10, 200)}')");
    var lm = Map<ImageMapLocation>(s, "locationmap.png");
    bool At(string name, (int X, int Y) p) => lm.RemainingAreas.TryGetValue(name, out var list) && list.Count == 1
                                            && Mathf.Abs(list[0].x - p.X / (float)N) < 1e-6f && Mathf.Abs(list[0].y - (N - 1 - p.Y) / (float)N) < 1e-6f;
    C(At("StartTemple", Temple) && At("Eikthyrnir", Eikthyr), "each location is at its pixel (decoded on the worker with System.Random, not Unity's)");
    var veg = Map<ImageMapSpawn>(s, "vegetationmap.png");
    C(veg.Indices[(N - 1 - 10) * N + 10] == 1 && veg.Indices[(N - 1 - 10) * N + 200] == 0 && veg.Indices[(N - 1 - 200) * N + 200] == 255
      && veg.LegendEntries.Count == 2 && veg.LegendEntries[1].Data == "Beech1, Birch1", "the vegetation map keeps its legend and indices (its prefabs are looked up at world load)");

    // Selecting it, as bc_import and the HUD do.
    var said = new List<string>();
    WorldImport.Complete(plan, outcome, true, (line, _) => said.Add(line));
    C(BC.ConfigSelectedPreset.Value == plan.PresetPath && File.ReadAllText(cfgPath).Contains("SelectedPreset = " + plan.PresetPath),
      "Complete selects the preset for the next New World, and the config file says so");
    C(ConfigState().Where(kv => kv.Key != BC.ConfigSelectedPreset).All(kv => Equals(before[kv.Key], kv.Value)), "nothing else in the config changed");
    C(said.Any(l => l.Contains("is made and selected")) && WorldImport.LastPresetPath == plan.PresetPath && WorldImport.LastError == null, "and says what it did");
    BC.ConfigSelectedPreset.Value = "Disabled";
    return plan.PresetPath;
  }

  static void Thumbnail(string preset)
  {
    Section("the preset's picture");
    var png = Path.ChangeExtension(preset, ".png");
    C(File.Exists(png), "<name>.png beside <name>.BetterContinents (Presets.UpdatePreview's path)");
    using var img = SixLabors.ImageSharp.Image.Load<Rgb24>(png);
    C(img.Width == 256 && img.Height == 256, $"256 x 256 ({img.Width} x {img.Height})");
    var corner = img[0, 0];
    var meadows = img[80, 80];
    C(corner.R == 16 && corner.G == 18 && corner.B == 22, "outside the world disc it is dark");
    C(meadows.G > meadows.B && meadows.G > meadows.R, $"the north-west is Meadows green ({meadows})");
    // Between the island and the disc's edge: sea (bluer than it is red).
    var sea = img[128, 8];
    C(sea.B > sea.R + 20, $"the sea is drawn as water ({sea})");
    var distinct = new HashSet<(byte, byte, byte)>();
    for (int y = 0; y < 256; y += 4) for (int x = 0; x < 256; x += 4) { var p = img[x, y]; distinct.Add((p.R, p.G, p.B)); }
    C(distinct.Count > 20, $"it is a shaded little map, not a flat fill ({distinct.Count} colours sampled)");
  }

  static void Reimport(string folder)
  {
    Section("making the same preset again, and a preset of that name made some other way");
    var plan = WorldImport.MakePlan(folder);
    var o = WorldImport.BuildPreset(plan);
    C(o.Error == null && o.Warnings.Count == 0 && !Directory.GetFiles(presetsDir).Any(f => f.Contains(".old-") || f.EndsWith(".new") || f.EndsWith(".tmp")),
      "importing the same folder again replaces its preset, with nothing left over");
    C(File.Exists(plan.SourcePath) && File.ReadAllText(plan.SourcePath).Trim() == folder, "the preset remembers the folder it was made from (<name>.import.txt)");
    // A preset of that name that this folder did not make ("bc savepreset", another folder of the same name) is kept.
    File.Delete(plan.SourcePath);
    var o2 = WorldImport.BuildPreset(WorldImport.MakePlan(folder));
    var old = Directory.GetFiles(presetsDir).Where(f => f.Contains(".old-")).ToList();
    C(o2.Error == null && o2.Warnings.Any(w => w.Contains("did not make")) && old.Count == 2 && old.Any(f => f.Contains(".png.old-")),
      "a preset of that name the folder did not make is moved aside with its picture, and the import says so");
    foreach (var f in old)
      File.Delete(f);
  }

  static void Lean(string folder)
  {
    Section("the lean build drops the pixels a preset does not need");
    var plan = WorldImport.MakePlan(folder);
    var lean = BC.BetterContinentsSettings.CreateForImport(plan.Values, lean: true);
    var full = BC.BetterContinentsSettings.CreateForImport(plan.Values, lean: false);
    C(FieldLength(Map<ImageMapFloat>(lean, "forestmap.png")) == 0 && FieldLength(Map<ImageMapFloat>(full, "forestmap.png")) == N * N, "lean: the forest map keeps no decoded pixels");
    C(FieldLength(Map<ImageMapPaint>(lean, "paintmap.png")) == 0, "lean: the paint map neither (8 bytes a pixel)");
    C(FieldLength(Map<ImageMapFloat>(lean, "heightmap.png")) == N * N, "lean: the heightmap keeps its pixels for the picture");
    C(Map<ImageMapBase>(lean, "forestmap.png").SourceData.Length > 0, "lean: the file bytes the preset stores are kept");
  }

  static void NoConfig()
  {
    Section("a folder without export.cfg");
    var dir = Path.Combine(work, "elsewhere", "My hand-made maps");
    MakeFolder(dir, withConfig: false, manifest: null);
    foreach (var extra in Directory.GetFiles(dir).Where(f => !f.EndsWith("heightmap.png") && !f.StartsWith(Path.Combine(dir, "biomemap"))))
      File.Delete(extra);
    var plan = WorldImport.MakePlan(dir);
    C(!plan.HadConfig && plan.Notes.Any(n => n.Contains("no export.cfg")) && plan.PresetName == "My hand-made maps", "it is allowed, named after the folder, and the note says every setting is yours");
    var outcome = WorldImport.BuildPreset(plan);
    var s = BC.BetterContinentsSettings.Load(outcome.PresetPath);
    C(outcome.Error == null && s.HasHeightMap && s.HasBiomeMap && !s.HasForestMap, "the preset has the folder's heightmap and biome map and nothing else");
    C(s.HeightmapAmount == BC.ConfigHeightmapAmount.Value && s.BiomePrecision == BC.ConfigBiomePrecision.Value && s.WorldSize == BC.ConfigWorldSize.Value,
      $"the other settings are the config's own (Heightmap Amount {s.HeightmapAmount})");
  }

  static void BrokenMaps()
  {
    Section("broken and missing maps");
    var dir = Path.Combine(work, "elsewhere", "broken");
    Directory.CreateDirectory(dir);
    using (var bad = new Image<L16>(N, N / 2)) bad.SaveAsPng(Path.Combine(dir, "heightmap.png"));
    using (var good = new Image<Rgb24>(N, N))
    {
      for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) good[x, y] = new Rgb24(0, 255, 0);
      good.SaveAsPng(Path.Combine(dir, "biomemap.png"));
    }
    var outcome = WorldImport.BuildPreset(WorldImport.MakePlan(dir));
    C(outcome.Error == null && outcome.Warnings.Any(w => w.Contains("heightmap.png")) && outcome.Maps.Contains("biomemap.png") && !outcome.Maps.Contains("heightmap.png"),
      "a non-square heightmap is left out with a warning; the rest makes the preset");
    C(File.Exists(Path.Combine(dir, "biomemap.txt")), "a biome map without a legend gets the default one written, as world creation does");
    var empty = Path.Combine(work, "elsewhere", "empty");
    Directory.CreateDirectory(empty);
    File.WriteAllText(Path.Combine(empty, "notes.txt"), "no maps");
    string error = null;
    try { WorldImport.MakePlan(empty); } catch (Exception e) { error = e.Message; }
    C(error != null && error.Contains("holds no map"), $"a folder with no map is refused with a clear reason ({error})");
    error = null;
    try { WorldImport.MakePlan(Path.Combine(work, "does not exist")); } catch (Exception e) { error = e.Message; }
    C(error != null && error.Contains("does not exist"), "a missing folder is refused");
  }

  // ---- the config way ----------------------------------------------------------------------------------------------

  static void ConfigWay(string folder, string preset)
  {
    Section("the config way: export.cfg into BetterContinents.cfg, 'From Config' selected, the old file kept");
    var oldText = File.ReadAllText(cfgPath);
    var hudBefore = BC.ConfigExportHud.Value;
    var o = WorldImport.ApplyToConfig(folder);
    C(o.Error == null && o.Changed.Count > 10, $"applied: {o.Changed.Count} setting(s) changed ({o.Error ?? "no error"})");
    C(BC.ConfigMapSourceDir.Value == folder.Replace('\\', '/'), "Directory is this folder, not the one export.cfg names");
    C(BC.ConfigHeightmapAmount.Value == 2.5f && BC.ConfigBiomePrecision.Value == 3 && BC.ConfigWorldSize.Value == 9000f && BC.ConfigHeatScale.Value == 12f
      && BC.ConfigAltBiomeMode.Value == "PlantedOnly" && BC.ConfigEnabled.Value, "the world settings are the export's (amount 2.5, precision 3, world 9000, heat scale 12, PlantedOnly)");
    C(BC.ConfigSelectedPreset.Value == "From Config", "the preset is 'From Config'");
    C(BC.ConfigExportHud.Value == hudBefore && !BC.ConfigDebugModeEnabled.Value, "the live Export group and Debug Mode are left alone");
    C(cfg.SaveOnConfigSet, "SaveOnConfigSet is back on");
    var newText = File.ReadAllText(cfgPath);
    C(newText.Contains("Heightmap Amount = 2.5") && newText.Contains("SelectedPreset = From Config"), "the file on disk has the new values (saved once)");
    C(o.Backup != null && File.Exists(o.Backup) && File.ReadAllText(o.Backup) == oldText && !o.Backup.EndsWith(".cfg"),
      "the old file is kept beside it, byte for byte, under a name no config manager lists as a config");
    // "From Config" now builds what the preset holds.
    var live = BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, []), lean: true);
    var fromPreset = BC.BetterContinentsSettings.Load(preset);
    C(live.HeightmapAmount == fromPreset.HeightmapAmount && live.BiomePrecision == fromPreset.BiomePrecision && live.WorldSize == fromPreset.WorldSize
      && live.LoadedImageMaps().Select(m => m.FileName).SequenceEqual(fromPreset.LoadedImageMaps().Select(m => m.FileName)),
      "'From Config' now builds the same settings and maps as the preset");
    // The backup puts the old config back.
    File.Copy(o.Backup!, cfgPath, true);
    cfg.Reload();
    C(BC.ConfigHeightmapAmount.Value == 1f && BC.ConfigBiomePrecision.Value == 0 && BC.ConfigMapSourceDir.Value == "", "copying the backup back restores the old settings");
  }

  // ---- the folder list and bc_import's names ------------------------------------------------------------------------

  static void Listing(string testFolder)
  {
    Section("the export folders and bc_import's names");
    void Stub(string world, string folder, bool finished, int size, string at)
    {
      var dir = Path.Combine(root, world, folder);
      Directory.CreateDirectory(dir);
      using (var img = new Image<L16>(8, 8)) img.SaveAsPng(Path.Combine(dir, "heightmap.png"));
      if (finished)
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{\n  \"exportedAt\": \"{at}\",\n  \"worldName\": \"{world}\",\n  \"size\": {size}\n}}");
    }
    Stub("World A", "export-2026-09-23-10-00-00", true, 1024, "2026-09-23T10:00:00");
    Stub("World B", "export-2026-09-24-09-00-00", true, 2048, "2026-09-24T09:00:00");
    Stub("World B", "export-2026-09-24-11-00-00", false, 0, "");
    var list = WorldImport.ListExports();
    C(list.Count == 4, $"four export folders; the presets folder's files are not exports ({list.Count})");
    C(list.Select(f => Path.GetFileName(f.Path)).SequenceEqual(["export-2026-09-24-12-00-00", "export-2026-09-24-11-00-00", "export-2026-09-24-09-00-00", "export-2026-09-23-10-00-00"]),
      "newest first: " + string.Join(", ", list.Select(f => f.Label)));
    var a = list[3];
    C(a.World == "World A" && a.Size == 1024 && a.Finished && a.Time == new DateTime(2026, 9, 23, 10, 0, 0) && a.Label.Contains("1024 px") && !a.PresetExists,
      $"a finished export: world, size and time from manifest.json ({a.Label})");
    C(!list[1].Finished && list[1].World == "World B" && list[1].Label.Contains("unfinished"), "no manifest.json: unfinished, world from the folder above it");
    C(list[0].PresetExists && list[0].Label.Contains("preset made"), "the export already imported shows its preset");

    ExportFolder R(string arg, out string how, out string error) => WorldImport.Resolve(arg, out how, out error);
    var r = R("", out var how, out _);
    C(r?.Path == testFolder && how.Contains("any world"), $"no argument, no world loaded, no export this session: the newest finished export ({how})");
    C(R("2", out _, out _)?.Path == list[1].Path, "a number: that line of 'bc_import list'");
    C(R("World A", out _, out _)?.Path == a.Path, "a world's name: its newest export (its folder under BetterContinents/ holds exports, not maps)");
    C(R(Path.Combine(root, "World A"), out _, out var e0) == null && e0.Contains("holds no map"), $"a full path to a folder without maps says so ({e0})");
    C(R("world b", out _, out _)?.Path == list[2].Path, "a world's name picks its newest FINISHED export, in any case");
    C(R("World B/export-2026-09-24-11-00-00", out _, out _)?.Path == list[1].Path, "a path under BetterContinents/");
    C(R(a.Path, out _, out _)?.Path == a.Path && R("\"" + a.Path + "\"", out _, out _)?.Path == a.Path, "a full path, with or without quotes");
    C(R("export-2026-09-23-10-00-00", out _, out _)?.Path == a.Path, "an export folder's name");
    C(R("nothing like it", out _, out var e1) == null && e1.Contains("bc_import list"), $"no match: a reason that points at 'bc_import list' ({e1})");
    C(R("99", out _, out var e2) == null && e2.Contains("1 to 4"), $"a number out of range ({e2})");

    var lines = new List<string>();
    WorldImportCommands.List(lines.Add);
    C(lines.Count == 6 && lines[1].Contains(" 1. ") && lines[1].Contains("Test World") && lines[1].Contains("Test World/export-2026-09-24-12-00-00"), "bc_import list numbers the folders");
    lines.Clear();
    WorldImportCommands.Import("nothing like it", lines.Add);
    C(lines.Count == 1 && lines[0].StartsWith("bc_import: no folder"), "bc_import with a bad name says so and does nothing");
    lines.Clear();
    WorldImportCommands.Help(lines.Add);
    C(lines.Any(l => l.Contains("config")) && lines.Any(l => l.Contains("list")), "bc_import help covers list, numbers, names, folders and config");
  }

  // ---- the export's own preset step ---------------------------------------------------------------------------------

  static void ExportStep()
  {
    Section("the export's preset step (WorldExport.Job.PresetPass, then its text files)");
    var dir = Path.Combine(root, "Export World", "export-2026-09-24-13-00-00");
    MakeFolder(dir, withConfig: false, manifest: null);
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    object Make()
    {
      var job = RuntimeHelpers.GetUninitializedObject(jobType);
      void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
      var o = WorldExport.Options.Default();
      Set("O", o);
      Set("Dir", dir);
      Set("World", new World { m_name = "Export World", m_seedName = "Seed", m_seed = 1 });
      Set("Settings", new BC.BetterContinentsSettings());
      Set("Size", N);
      Set("Total", 21000f);
      Set("WorldR", 10000f);
      Set("TotalR", 10500f);
      Set("Sla", 0f);
      Set("EdgeDropoff", true);
      Set("ZoneBlend", true);
      Set("Role", "host");
      Set("ForestScaleText", "0.5");
      Set("SourceForestAmount", 0.5f);
      Set("WorldSizeSetting", 10000f);
      Set("EdgeSizeSetting", 500f);
      Set("AltGrid", BC.AltBiomeGridMode.Vanilla);
      Set("Clock", System.Diagnostics.Stopwatch.StartNew());
      Set("Started", new DateTime(2026, 9, 24, 13, 0, 0));
      Set("Written", Directory.GetFiles(dir).Select(Path.GetFileName).ToList());
      Set("Notes", new List<string>());
      Set("Tracked", new List<string>());
      Set("BiomePixels", new long[10]);
      Set("HeightsWritten", true);
      Set("ForestWritten", true);
      Set("HeatWritten", true);
      Set("AltBiomesWritten", true);
      Set("LocationsWritten", true);
      Set("LocationsFull", true);
      Set("LocationsGenerated", true);
      Set("MinMetres", -14f);
      Set("MaxMetres", 310f);
      return job;
    }
    void Drain(object job, string method, params object[] args)
    {
      var e = ((IEnumerable)jobType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, args)!).GetEnumerator();
      while (e.MoveNext())
        Thread.Sleep(1);
    }
    var job = Make();
    var config = (List<string>)jobType.GetMethod("ConfigLines", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;
    Drain(job, "PresetPass", config);
    var presetPath = Path.Combine(presetsDir, "Export World 2026-09-24 13-00-00.BetterContinents");
    C(File.Exists(presetPath) && File.Exists(Path.ChangeExtension(presetPath, ".png")), "the export writes the preset \"<world> <time>\" and its picture");
    C(((List<string>)jobType.GetField("Tracked", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!).Contains(presetPath), "a cancel or a failure deletes the preset with the maps (it is tracked)");
    var s = BC.BetterContinentsSettings.Load(presetPath);
    C(s.HeightmapAmount == 2f && s.SeaLevel == 0.5f && s.HasHeightMap && s.HasBiomeMap && s.AltBiomes?.Mode == BC.AltBiomeMode.PlantedOnly,
      "built from the export's own settings lines (amount 2, sea level 0.5, PlantedOnly) before export.cfg exists");
    C(BC.ConfigSelectedPreset.Value == "Disabled", "the export does not select its preset: it only appears in the New World list");
    Drain(job, "TextPass", config);
    var readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
    var header = File.ReadLines(Path.Combine(dir, "export.cfg")).TakeWhile(l => l.StartsWith("##")).ToList();
    C(readme.Contains("A. The preset (easiest)") && readme.Contains("\"Export World 2026-09-24 13-00-00\"") && readme.Contains("bc_import Export World/export-2026-09-24-13-00-00"),
      "README.txt names the preset and gives the exact bc_import line");
    C(header.Any(l => l.Contains("pick the Better Continents preset \"Export World 2026-09-24 13-00-00\"")), "export.cfg's header names the preset");
    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))))
    {
      var p = doc.RootElement.GetProperty("preset");
      C(p.GetProperty("made").GetBoolean() && p.GetProperty("name").GetString() == "Export World 2026-09-24 13-00-00" && p.GetProperty("file").GetString() == presetPath,
        "manifest.json records the preset");
      C(doc.RootElement.GetProperty("import").GetString() == "bc_import Export World/export-2026-09-24-13-00-00", "and the bc_import line");
    }
    var summary = (List<string>)jobType.GetMethod("Summary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;
    C(summary.Any(l => l.Contains("New World preset") && l.Contains("is ready")), "the summary the HUD shows names the preset");

    // Cancelled after the preset was made (while the text files were written): it goes with the rest.
    var job2 = Make();
    Drain(job2, "PresetPass", config);
    C(File.Exists(presetPath), "made again by a second run");
    jobType.GetMethod("Finish")!.Invoke(job2, [null, true]);
    C(!File.Exists(presetPath) && !File.Exists(Path.ChangeExtension(presetPath, ".png")) && Directory.Exists(dir), "a cancel after it leaves no preset behind (and the export's own cleanup ran)");
    // Cancelled before the files are written: the builder writes nothing.
    var early = WorldImport.BuildPreset(WorldImport.MakePlan(dir, config, "Cancelled early"), () => true);
    C(early.Error == "cancelled" && !File.Exists(Path.Combine(presetsDir, "Cancelled early.BetterContinents")), "a cancel before writing: no file, error 'cancelled'");
  }
}

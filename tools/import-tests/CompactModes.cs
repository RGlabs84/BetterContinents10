// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using BC = BetterContinents.BetterContinents;

namespace ImportTest;

// Compact Maps as Auto, On or Off (and true and false, as every file written before had it): which maps make a new world compact,
// what a config file and an export.cfg make of each value, and what is written back.
internal static partial class Program
{
  static void Strip(string folder, string file, int width)
  {
    // A real picture one row high: past the size that counts (its width), and no map (it is not square), so nothing big is made.
    using var image = new Image<L16>(width, 1);
    image.Save(Path.Combine(folder, file), new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16, ChunkFilter = PngChunkFilter.ExcludeAll });
  }

  static int Count(string part) { lock (LogHandler.Lines) return LogHandler.Lines.Count(l => l.Contains(part)); }

  // ---- Compact Maps: Auto, On, Off ----------------------------------------------------------------------------------------

  static void CompactModes()
  {
    Section("Compact Maps Auto, On and Off: the maps that make a world compact, 8192 px across and 8193");
    var big = Path.Combine(root, "ModeMaps", "maps");
    Directory.CreateDirectory(big);
    foreach (var file in Directory.GetFiles(big))
      File.Delete(file);
    BC.ConfigMapSourceDir.Value = big;
    try
    {
      foreach (var (mode, atEdge, over) in new[] { (CompactMapsMode.Auto, false, true), (CompactMapsMode.On, true, true), (CompactMapsMode.Off, false, false) })
      {
        BC.ConfigCompactMaps.Value = mode;
        Strip(big, "forestmap.png", 8192);
        var edge = BC.BetterContinentsSettings.Create();
        Strip(big, "forestmap.png", 8193);
        lock (LogHandler.Lines) LogHandler.Lines.Clear();
        var large = BC.BetterContinentsSettings.Create();
        C(edge.CompactMaps == atEdge && large.CompactMaps == over && edge.Version == 12 && large.Version == 12 && large.CompactMapsChoice == mode,
          $"{mode}: 8192 px across is {(atEdge ? "compact" : "pictures")}, 8193 is {(over ? "compact" : "pictures")}");
        bool said = LogHandler.Has("[BetterContinents] Compact Maps is Auto and Forestmap") && LogHandler.Has("8193 x 1 pixels");
        bool warned = LogHandler.Has("[Warning] [BetterContinents] Compact Maps is Off, so this world keeps its maps as pictures although Forestmap")
          && LogHandler.Has("8193 x 1 pixels") && LogHandler.Has("gigabytes of memory") && LogHandler.Has("hundreds of megabytes");
        C(mode switch { CompactMapsMode.Auto => said && !warned && Count("Compact Maps is Auto") == 1, CompactMapsMode.On => !said && !warned, _ => warned && !said && Count("Compact Maps is Off") == 1 },
          mode switch
          {
            CompactMapsMode.Auto => "Auto: the log names the map that made the world compact",
            CompactMapsMode.On => "On: no word about the map, the world is compact anyway",
            _ => "Off: one warning, naming the map and what it costs as pictures (memory, the world file, every join)",
          });
        var info = new List<string>();
        large.Dump(info.Add);
        C(info.Any(l => l.StartsWith("Compact Maps: the maps are held and saved as compressed tiles")) == over, $"{mode}: bc info {(over ? "says" : "does not say")} the maps are compact");
      }
      // The biome map counts, the location and alt-biome maps do not; Override version 11 makes no world compact; the threshold is one number.
      BC.ConfigCompactMaps.Value = CompactMapsMode.Auto;
      File.Delete(Path.Combine(big, "forestmap.png"));
      Strip(big, "locationmap.png", 9000);
      Strip(big, "altbiomemap.png", 9000);
      C(!BC.BetterContinentsSettings.Create().CompactMaps, "Auto: a location map or an alt-biome map of 9000 px does not make a world compact (they hold no tiles)");
      Strip(big, "biomemap.png", 9000);
      C(BC.BetterContinentsSettings.Create().CompactMaps, "Auto: a biome map of 9000 px does");
      BC.ConfigCompactMaps.Value = CompactMapsMode.Off;
      C(!BC.BetterContinentsSettings.Create().CompactMaps, "Off: not even that");
      BC.ConfigCompactMaps.Value = CompactMapsMode.On;
      BC.ConfigOverrideVersion.Value = "11";
      var old = BC.BetterContinentsSettings.Create();
      C(!old.CompactMaps && old.Version == 11, "On, Override version 11: a world of settings version 11 is not compact (tiles are saved by no other)");
      BC.ConfigOverrideVersion.Value = "";
    }
    finally
    {
      BC.ConfigCompactMaps.Value = CompactMapsMode.Auto;
      BC.ConfigOverrideVersion.Value = "";
      BC.ConfigMapSourceDir.Value = "";
    }
  }

  // ---- true, false, the names: a config file, an export.cfg, and written back ------------------------------------------------------

  static void CompactValues()
  {
    Section("Compact Maps in a config file: true and false (every file written before) read as On and Auto, the names as they are, anything else is refused");
    var cases = new (string Text, CompactMapsMode Want)[]
    {
      ("true", CompactMapsMode.On), ("false", CompactMapsMode.Auto), ("TRUE", CompactMapsMode.On), ("False", CompactMapsMode.Auto), (" true ", CompactMapsMode.On),
      ("Auto", CompactMapsMode.Auto), ("On", CompactMapsMode.On), ("Off", CompactMapsMode.Off), ("auto", CompactMapsMode.Auto), ("ON", CompactMapsMode.On), ("off", CompactMapsMode.Off),
      ("garbage", CompactMapsMode.Auto), ("On, Off", CompactMapsMode.Auto), ("7", CompactMapsMode.Auto), ("yes", CompactMapsMode.Auto), ("", CompactMapsMode.Auto),
    };
    int n = 0;
    foreach (var (text, want) in cases)
    {
      var path = Path.Combine(work, $"compact-values-{n++}.cfg");
      File.WriteAllText(path, $"[07 BetterContinents.Misc]\nCompact Maps = {text}\n");
      var file = new ConfigFile(path, true);
      BC.DeclareConfig(file);
      var entry = BC.ConfigCompactMaps;
      bool ok = entry.Value == want && entry.GetSerializedValue() == want.ToString() && File.ReadAllText(path).Contains($"Compact Maps = {want}");
      C(ok, $"a config that says '{text}' reads as {entry.Value} ({want} wanted), and is written back as {want}");
    }
    // A live reload (ConfigFile.Reload, which LiveConfig calls when the file changes) reads the old values the same way.
    var livePath = Path.Combine(work, "compact-values-live.cfg");
    File.WriteAllText(livePath, "[07 BetterContinents.Misc]\nCompact Maps = Off\n");
    var liveFile = new ConfigFile(livePath, true);
    BC.DeclareConfig(liveFile);
    var liveSteps = new List<string> { BC.ConfigCompactMaps.Value.ToString() };
    foreach (var text in new[] { "true", "false", "On", "garbage", "Off" })
    {
      File.WriteAllText(livePath, $"[07 BetterContinents.Misc]\nCompact Maps = {text}\n");
      liveFile.Reload();
      liveSteps.Add(BC.ConfigCompactMaps.Value.ToString());
    }
    C(liveSteps.SequenceEqual(["Off", "On", "Auto", "On", "On", "Off"]), $"a live reload: Off, then true, false, On, garbage (refused: the value stays) and Off read as {string.Join(", ", liveSteps)}");
    // The setting's own entry in the file: the type, the default, and every value listed for whoever edits it.
    var text0 = File.ReadAllText(Path.Combine(work, "compact-values-0.cfg"));
    C(text0.Contains("# Setting type: CompactMapsMode") && text0.Contains("# Default value: Auto") && text0.Contains("# Acceptable values: Auto, On, Off"),
      "the file says what the setting is: CompactMapsMode, default Auto, acceptable values Auto, On, Off");
    // Every other enum reads as it did (the converter is BepInEx's, wrapped).
    C(TomlTypeConverter.ConvertToValue("KB384", typeof(TransferRatePreset)).Equals(TransferRatePreset.KB384) && TomlTypeConverter.ConvertToString(TransferRatePreset.MB3, typeof(TransferRatePreset)) == "MB3"
      && TomlTypeConverter.ConvertToValue("f9", typeof(UnityEngine.KeyCode)).Equals(UnityEngine.KeyCode.F9) && TomlTypeConverter.ConvertToString(UnityEngine.KeyCode.F9, typeof(UnityEngine.KeyCode)) == "F9",
      "every other enum reads and writes as before (Settings Transfer Rate, the export's hotkeys)");
    bool threw = false;
    try { TomlTypeConverter.ConvertToValue("true", typeof(TransferRatePreset)); } catch (ArgumentException) { threw = true; }
    C(threw, "and 'true' is still no Settings Transfer Rate");
    // The main test config again.
    BC.DeclareConfig(cfg);
    C(BC.ConfigCompactMaps == SettingsSchema.CompactMaps.Entry && BC.ConfigCompactMaps.ConfigFile == cfg, "the test's own config is bound again");

    Section("Compact Maps in an export.cfg: the same values, the same rule");
    var folder = Path.Combine(root, "CompactCfg", "export-2026-10-06-12-00-00");
    Directory.CreateDirectory(folder);
    using (var image = new Image<L16>(8, 8))
      image.SaveAsPng(Path.Combine(folder, "heightmap.png"));
    foreach (var (text, want) in cases)
    {
      File.WriteAllLines(Path.Combine(folder, "export.cfg"), ["[00 BetterContinents.Debug]", "Enabled = true", "[07 BetterContinents.Misc]", $"Compact Maps = {text}"]);
      var plan = WorldImport.MakePlan(folder);
      var value = plan.Values.Get(BC.ConfigCompactMaps);
      bool refused = want == CompactMapsMode.Auto && text is "garbage" or "On, Off" or "7" or "yes" or "";
      C(value == want && (refused ? plan.Ignored.Any(l => l.Contains("[07 BetterContinents.Misc] Compact Maps: cannot read")) : plan.Applied.Any(l => l == $"[07 BetterContinents.Misc] Compact Maps = {text.Trim()}")),
        $"an export.cfg that says '{text}': {value}" + (refused ? ", refused and said so (the config's own value stands)" : ", applied"));
    }

    Section("Compact Maps in an export.cfg: a map of 8193 px across, for each value");
    var export = Path.Combine(root, "CompactCfg2", "export-2026-10-06-12-30-00");
    Directory.CreateDirectory(export);
    using (var image = new Image<L16>(64, 64))
      image.SaveAsPng(Path.Combine(export, "heightmap.png"));
    Strip(export, "biomemap.png", 8193);
    using (var forest = new Image<L16>(64, 64))
      forest.SaveAsPng(Path.Combine(export, "forestmap.png"), new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 });
    foreach (var (text, compactWanted) in new[] { ("true", true), ("false", true), ("Auto", true), ("On", true), ("Off", false) })
    {
      File.WriteAllLines(Path.Combine(export, "export.cfg"), ["[00 BetterContinents.Debug]", "Enabled = true", "[07 BetterContinents.Misc]", $"Compact Maps = {text}"]);
      var made = BC.BetterContinentsSettings.CreateForImport(WorldImport.MakePlan(export).Values, lean: false);
      var forestMap = Map<ImageMapFloat>(made, "forestmap.png");
      C(made.CompactMaps == compactWanted && (forestMap?.Compact ?? false) == compactWanted,
        $"export.cfg Compact Maps = {text}: with a map of 8193 px across the world is {(made.CompactMaps ? "compact" : "pictures")}, its other maps too");
    }
  }
}

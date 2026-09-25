// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Configuration;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

/// <summary>The config values a new world's settings are built from (BetterContinentsSettings.ReadConfig): the live
/// BetterContinents.cfg, or a snapshot of it with an import's export.cfg laid over it. A snapshot is taken on the main
/// thread, can be read from any thread, and never changes the player's config.</summary>
public sealed class ConfigValues
{
  /// <summary>BetterContinents.cfg as it is when a value is read ("From Config").</summary>
  public static readonly ConfigValues Live = new(null);

  private readonly Dictionary<ConfigEntryBase, object?>? values;

  private ConfigValues(Dictionary<ConfigEntryBase, object?>? values) => this.values = values;

  public T Get<T>(ConfigEntry<T> entry)
  {
    if (values != null && values.TryGetValue(entry, out var value) && value is T typed)
      return typed;
    return entry.Value;
  }

  /// <summary>Every entry of <paramref name="config"/> as it is now, with <paramref name="overrides"/> over it. Main thread.</summary>
  internal static ConfigValues Snapshot(ConfigFile config, IEnumerable<KeyValuePair<ConfigEntryBase, object?>> overrides)
  {
    var values = new Dictionary<ConfigEntryBase, object?>();
    foreach (var kv in config)
      values[kv.Value] = kv.Value.BoxedValue;
    foreach (var kv in overrides)
      values[kv.Key] = kv.Value;
    return new ConfigValues(values);
  }
}

/// <summary>A folder of Better Continents maps the import can read: an export (BetterContinents/&lt;world&gt;/export-&lt;time&gt;/)
/// or any folder of maps with the standard names (heightmap.png, biomemap.png, ...).</summary>
public sealed class ExportFolder
{
  public string Path = "";
  /// <summary>The world it was exported from (manifest.json), else the name of the folder it is in.</summary>
  public string World = "";
  public DateTime Time;
  /// <summary>Pixels per side (manifest.json); 0 when unknown.</summary>
  public int Size;
  /// <summary>It has a manifest.json, which an export writes last: the export finished.</summary>
  public bool Finished;
  public bool HasConfig;
  /// <summary>The preset an import makes of it ("&lt;world&gt; &lt;time&gt;").</summary>
  public string PresetName = "";
  public bool PresetExists;

  public string Label
  {
    get
    {
      var sb = new StringBuilder();
      sb.Append(World.Length > 0 ? World : System.IO.Path.GetFileName(Path));
      sb.Append("  ").Append(Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
      if (Size > 0)
        sb.Append("  ").Append(Size.ToString(CultureInfo.InvariantCulture)).Append(" px");
      if (!Finished)
        sb.Append("  (unfinished)");
      if (PresetExists)
        sb.Append("  (preset made)");
      return sb.ToString();
    }
  }
}

// World import: turns an export folder (or any folder of Better Continents maps, with or without an export.cfg) into
// something the New World screen creates a world from. Two ways:
//  - PRESET (the default): "<name>.BetterContinents" in the presets folder, the same file "bc savepreset" writes, with
//    every map's bytes inside, plus a 256 px picture. It is selected in the New World dropdown. The folder can be moved
//    or deleted afterwards; editing its PNGs needs a new import.
//  - CONFIG: export.cfg's lines are applied to BetterContinents.cfg (a copy of the old file is kept beside it) and the
//    preset becomes "From Config". Every world created then reads the PNGs from the folder as they are at that moment.
//
// A preset is built the way "From Config" builds a new world's settings (BetterContinentsSettings.ReadConfig), from a
// snapshot of the config with export.cfg laid over it, on a worker: nothing in the player's config changes (but the
// selected preset), and a loaded world's patches and caches are left alone. The export writes one for every export
// (WorldExport.Options.Preset) through the same builder.
public static class WorldImport
{
  public const string ConfigFileName = "export.cfg";
  public const string ManifestFileName = "manifest.json";
  public const string ExportPrefix = "export-";
  public const int ThumbnailSize = 256;

  /// <summary>The names BetterContinentsSettings loads from a Directory, in its order.</summary>
  internal static readonly string[] MapFiles =
  [
    "heightmap.png", "biomemap.png", "locationmap.png", "roughmap.png", "forestmap.png", "heatmap.png", "terrainmap.png",
    "paintmap.png", "lavamap.png", "mossmap.png", "vegetationmap.png", "spawnmap.png", "altbiomemap.png",
  ];

  // Settings that are not a new world's: the live Export group, the debug switches and the mod's own bookkeeping.
  private const string ExportSection = "09 BetterContinents.Export";
  private static readonly HashSet<string> NotWorldKeys = ["Debug Mode", "Debug Reset Command", "NexusID", "SelectedPreset"];

  private static readonly Regex StampPattern = new(@"^export-(\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2})(?:-(\d+))?$", RegexOptions.CultureInvariant);

  private static string? rootDir;

  /// <summary>&lt;save data (local)&gt;/BetterContinents: exports are its &lt;world&gt;/export-&lt;time&gt;/ folders. Settable for
  /// the offline harness.</summary>
  internal static string RootDir
  {
    get => rootDir ??= Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents");
    set => rootDir = value;
  }

  public enum Mode
  {
    Preset,
    Config,
  }

  /// <summary>A preset is being built (on a worker; bc_import and the HUD start one at a time).</summary>
  public static bool IsRunning { get; private set; }
  /// <summary>What the running import does, for the HUD.</summary>
  public static string Phase { get; private set; } = "";
  /// <summary>0..100 of the running import.</summary>
  public static float Progress => running?.Progress ?? 0f;
  /// <summary>The last import's outcome, one line; null before the first.</summary>
  public static string? LastResult { get; private set; }
  /// <summary>Why the last import failed; null after a success.</summary>
  public static string? LastError { get; private set; }
  /// <summary>The preset file the last successful preset import wrote.</summary>
  public static string? LastPresetPath { get; private set; }

  private static Plan? running;

  // ---- the folders --------------------------------------------------------------------------------------------------

  /// <summary>Every BetterContinents/&lt;world&gt;/export-* folder, newest first.</summary>
  public static List<ExportFolder> ListExports()
  {
    var list = new List<ExportFolder>();
    var root = RootDir;
    if (!Directory.Exists(root))
      return list;
    // The presets folder holds files only, unless a world is called "presets": then its exports are there, and listed.
    foreach (var worldDir in Dirs(root, "*"))
      foreach (var dir in Dirs(worldDir, ExportPrefix + "*"))
        list.Add(Describe(dir));
    return [.. list.OrderByDescending(f => f.Time).ThenByDescending(f => f.Path, StringComparer.Ordinal)];
  }

  private static string[] Dirs(string dir, string pattern)
  {
    try
    {
      return Directory.GetDirectories(dir, pattern);
    }
    catch (Exception e)
    {
      LogWarning($"World import: cannot list {dir}: {e.Message}");
      return [];
    }
  }

  /// <summary>What the import knows about a folder: its world, time and size (manifest.json, else the folder names).</summary>
  public static ExportFolder Describe(string dir)
  {
    dir = Path.GetFullPath(dir).TrimEnd('/', '\\');
    var f = new ExportFolder { Path = dir };
    string? manifest = null;
    try
    {
      var path = Path.Combine(dir, ManifestFileName);
      if (File.Exists(path))
        manifest = File.ReadAllText(path);
    }
    catch (Exception e)
    {
      LogWarning($"World import: cannot read {dir}/{ManifestFileName}: {e.Message}");
    }
    f.Finished = manifest != null;
    f.HasConfig = File.Exists(Path.Combine(dir, ConfigFileName));
    var name = Path.GetFileName(dir);
    var stamp = StampPattern.Match(name);
    f.World = (manifest != null ? JsonString(manifest, "worldName") : null)
              ?? (stamp.Success ? Path.GetFileName(Path.GetDirectoryName(dir) ?? "") : "");
    if (manifest != null && JsonNumber(manifest, "size") is long size && size > 0 && size < int.MaxValue)
      f.Size = (int)size;
    if (manifest != null && DateTime.TryParseExact(JsonString(manifest, "exportedAt") ?? "", "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
      f.Time = at;
    else if (stamp.Success && DateTime.TryParseExact(stamp.Groups[1].Value, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
      f.Time = t;
    else
    {
      try
      {
        f.Time = Directory.GetLastWriteTime(dir);
      }
      catch
      {
        f.Time = DateTime.MinValue;
      }
    }
    f.PresetName = PresetNameFor(dir, f.World.Length > 0 ? f.World : null);
    try
    {
      f.PresetExists = File.Exists(Path.Combine(Presets.PresetsDir, f.PresetName + ConfigFileExtension));
    }
    catch
    {
      f.PresetExists = false;
    }
    return f;
  }

  /// <summary>The preset name for a folder: "&lt;world&gt; &lt;yyyy-MM-dd HH-mm-ss&gt;" for an export-&lt;time&gt; folder, else the
  /// folder's own name, made safe for a file name on every system.</summary>
  public static string PresetNameFor(string folder, string? world = null)
  {
    folder = folder.TrimEnd('/', '\\');
    var name = Path.GetFileName(folder);
    var stamp = StampPattern.Match(name);
    if (stamp.Success && DateTime.TryParseExact(stamp.Groups[1].Value, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
    {
      if (string.IsNullOrWhiteSpace(world))
        world = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
      name = $"{world} {t.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}" + (stamp.Groups[2].Success ? $" ({stamp.Groups[2].Value})" : "");
    }
    return SafePresetName(name);
  }

  // Characters no file name may hold on Windows, macOS or Linux, and the dot: GetBCFile (Path.ChangeExtension) would take
  // everything after one for an extension.
  private static readonly char[] Unsafe = [.. Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*.")];

  /// <summary>A name that is a valid file name everywhere: unsafe characters and dots become "_", runs of blanks one
  /// space, at most 80 characters; "Imported maps" when nothing is left.</summary>
  public static string SafePresetName(string name)
  {
    var chars = (name ?? "").Select(c => char.IsControl(c) || Unsafe.Contains(c) ? '_' : c).ToArray();
    var s = Regex.Replace(new string(chars), @"\s+", " ").Trim();
    if (s.Length > 80)
      s = s.Substring(0, 80).TrimEnd();
    s = s.Trim('_', ' ');
    return s.Length == 0 ? "Imported maps" : s;
  }

  /// <summary>The folder bc_import means. Empty: the newest finished export of the loaded world, else of the last export
  /// this session, else of the last world played, else of any world. A number: that line of 'bc_import list'. Else a
  /// folder (absolute, or under BetterContinents/), an export folder's name, or a world's name (its newest export).</summary>
  public static ExportFolder? Resolve(string? arg, out string how, out string error)
  {
    how = error = "";
    arg = (arg ?? "").Trim().Trim('"').Trim();
    List<ExportFolder> list;
    try
    {
      list = ListExports();
    }
    catch (Exception e)
    {
      error = $"cannot list the exports: {e.Message}";
      return null;
    }
    var finished = list.Where(f => f.Finished).ToList();
    if (arg.Length == 0)
    {
      ExportFolder? Newest(string? world)
      {
        if (string.IsNullOrEmpty(world))
          return null;
        var safe = SafeFolderName(world!);
        return finished.FirstOrDefault(f => string.Equals(f.World, world, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(Path.GetFileName(Path.GetDirectoryName(f.Path) ?? ""), safe, StringComparison.OrdinalIgnoreCase));
      }
      var loaded = LoadedWorldName();
      var found = Newest(loaded);
      if (found != null)
        how = $"the newest export of the loaded world \"{loaded}\"";
      if (found == null && WorldExport.LastExportDir != null && Directory.Exists(WorldExport.LastExportDir))
      {
        found = Describe(WorldExport.LastExportDir);
        how = "the last export of this session";
      }
      if (found == null)
      {
        var last = LastWorldName();
        found = Newest(last);
        if (found != null)
          how = $"the newest export of the last world played, \"{last}\"";
      }
      if (found == null && finished.Count > 0)
      {
        found = finished[0];
        how = "the newest export of any world";
      }
      if (found == null)
        error = $"there is no finished export under {RootDir} yet (export a world first: bc_export, or F7 in a world with Hud on)";
      return found;
    }
    if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
    {
      if (n >= 1 && n <= list.Count)
      {
        how = $"line {n} of 'bc_import list'";
        return list[n - 1];
      }
      error = list.Count == 0 ? "there are no exports to pick from" : $"pick a number from 1 to {list.Count} ('bc_import list' shows them)";
      return null;
    }
    // A full path, or one under BetterContinents/ ("Midgard/export-2026-09-24-14-05-33"), that holds maps: a world's own
    // folder there ("Midgard") holds exports, not maps, and is taken as the world's name below.
    try
    {
      var path = Path.IsPathRooted(arg) ? arg : Path.Combine(RootDir, arg);
      if (Directory.Exists(path) && (FolderMaps(path).Count > 0 || File.Exists(Path.Combine(path, ManifestFileName))))
      {
        how = "the folder you named";
        return Describe(path);
      }
    }
    catch
    {
      // Not a path; try the names below.
    }
    var byFolder = list.FirstOrDefault(f => string.Equals(Path.GetFileName(f.Path), arg, StringComparison.OrdinalIgnoreCase));
    if (byFolder != null)
    {
      how = "the export folder of that name";
      return byFolder;
    }
    var byWorld = finished.FirstOrDefault(f => string.Equals(f.World, arg, StringComparison.OrdinalIgnoreCase))
                  ?? list.FirstOrDefault(f => string.Equals(f.World, arg, StringComparison.OrdinalIgnoreCase));
    if (byWorld != null)
    {
      how = $"the newest export of \"{byWorld.World}\"";
      return byWorld;
    }
    error = Path.IsPathRooted(arg) && Directory.Exists(arg)
      ? $"{arg} holds no map Better Continents loads ({string.Join(", ", MapFiles)})"
      : $"no folder or export matches '{arg}' ('bc_import list' shows the exports; a full path works for any folder of maps)";
    return null;
  }

  private static string SafeFolderName(string name)
  {
    var bad = Path.GetInvalidFileNameChars();
    var s = new string([.. name.Select(c => bad.Contains(c) ? '_' : c)]).Trim();
    return s.Length == 0 ? "world" : s;
  }

  private static string? LoadedWorldName()
  {
    try
    {
      var world = WorldGenerator.instance?.m_world;
      if (world != null && !world.m_menu && ZNet.instance != null)
        return world.m_name;
    }
    catch
    {
      // No game here (the offline harness).
    }
    return null;
  }

  private static string? LastWorldName()
  {
    try
    {
      var name = PlatformPrefs.GetString("world", "");
      return string.IsNullOrEmpty(name) ? null : name;
    }
    catch
    {
      return null;
    }
  }

  // ---- export.cfg ---------------------------------------------------------------------------------------------------

  /// <summary>BetterContinents.cfg read the way BepInEx reads it (ConfigFile.Reload): "[section]" lines, "key = value"
  /// lines, "#" comments, a later line winning over an earlier one.</summary>
  internal static List<(string Section, string Key, string Value)> ParseConfig(IEnumerable<string> lines)
  {
    var result = new List<(string, string, string)>();
    var section = "";
    foreach (var raw in lines)
    {
      var line = (raw ?? "").Trim();
      if (line.StartsWith("#"))
        continue;
      if (line.StartsWith("[") && line.EndsWith("]"))
      {
        section = line.Substring(1, line.Length - 2);
        continue;
      }
      var split = line.Split(['='], 2);
      if (split.Length != 2)
        continue;
      result.Add((section, split[0].Trim(), split[1].Trim()));
    }
    return result;
  }

  /// <summary>A value as BepInEx would store it: converted with its TOML converter and clamped to its acceptable values.</summary>
  internal static bool TryConvert(ConfigEntryBase entry, string text, out object? value, out string why)
  {
    try
    {
      value = TomlTypeConverter.ConvertToValue(text, entry.SettingType);
      var acceptable = entry.Description?.AcceptableValues;
      if (acceptable != null)
        value = acceptable.Clamp(value);
      why = "";
      return true;
    }
    catch (Exception e)
    {
      value = null;
      why = e.Message;
      return false;
    }
  }

  internal static bool IsWorldSetting(ConfigEntryBase entry) =>
    entry.Definition.Section != ExportSection && !NotWorldKeys.Contains(entry.Definition.Key);

  // The parsed lines matched to Better Continents' settings; the last line of a key wins. applied gets "[section] key = value"
  // per setting used, ignored the lines that were not (unknown, not a world setting, unreadable).
  private static Dictionary<ConfigEntryBase, object?> Overrides(ConfigFile config, IEnumerable<(string Section, string Key, string Value)> parsed,
    List<string> applied, List<string> ignored)
  {
    var byName = new Dictionary<(string, string), ConfigEntryBase>();
    foreach (var kv in config)
      byName[(kv.Key.Section, kv.Key.Key)] = kv.Value;
    var result = new Dictionary<ConfigEntryBase, object?>();
    var text = new Dictionary<ConfigEntryBase, string>();
    foreach (var (section, key, value) in parsed)
    {
      var name = $"[{section}] {key}";
      if (!byName.TryGetValue((section, key), out var entry))
      {
        ignored.Add($"{name}: not a Better Continents {ModInfo.Version} setting");
        continue;
      }
      if (!IsWorldSetting(entry))
      {
        if (entry.Definition.Key != "SelectedPreset")
          ignored.Add($"{name}: not a world setting");
        continue;
      }
      if (!TryConvert(entry, value, out var converted, out var why))
      {
        ignored.Add($"{name}: cannot read '{value}' ({why})");
        continue;
      }
      result[entry] = converted;
      text[entry] = value;
    }
    foreach (var kv in text)
      applied.Add($"[{kv.Key.Definition.Section}] {kv.Key.Definition.Key} = {kv.Value}");
    return result;
  }

  private static ConfigFile PluginConfig() =>
    ConfigEnabled?.ConfigFile ?? throw new InvalidOperationException("Better Continents' config is not loaded");

  // Directory as export.cfg writes it: forward slashes, which every system's Path.Combine takes.
  private static string ConfigDirectory(string folder) => folder.Replace('\\', '/');

  private static List<string> FolderMaps(string folder) => [.. MapFiles.Where(f => File.Exists(Path.Combine(folder, f)))];

  // ---- the preset ---------------------------------------------------------------------------------------------------

  /// <summary>What a preset import reads and writes, fixed on the main thread before the worker builds it.</summary>
  internal sealed class Plan
  {
    public string Folder = "";
    public string PresetName = "";
    public string PresetsDir = "";
    public ConfigValues Values = ConfigValues.Live;
    public bool HadConfig;
    public readonly List<string> Applied = [];
    public readonly List<string> Ignored = [];
    public readonly List<string> Notes = [];
    public volatile float Progress;

    public string PresetPath => Path.Combine(PresetsDir, PresetName + ConfigFileExtension);
    public string ThumbnailPath => Path.Combine(PresetsDir, PresetName + ".png");
    // "<name>.import.txt": the folder an import made the preset from, so a later import of the same folder replaces it
    // and any other preset of that name is moved aside instead (the New World list reads only *.BetterContinents).
    public string SourcePath => Path.Combine(PresetsDir, PresetName + ".import.txt");
  }

  /// <summary>What a preset build did. Never thrown: Error says why it failed.</summary>
  internal sealed class Outcome
  {
    public string? PresetPath, ThumbnailPath, Error;
    public readonly List<string> Maps = [];
    public readonly List<string> Warnings = [];
    public string Summary = "";
  }

  /// <summary>Main thread: reads the folder's export.cfg (or <paramref name="configLines"/>, which the export passes before
  /// it writes the file) over a snapshot of the config. Directory is always the folder itself, wherever export.cfg says
  /// the export was made. Throws when the folder holds no map Better Continents loads.</summary>
  internal static Plan MakePlan(string folder, IEnumerable<string>? configLines = null, string? presetName = null)
  {
    folder = Path.GetFullPath(folder.Trim()).TrimEnd('/', '\\');
    if (!Directory.Exists(folder))
      throw new DirectoryNotFoundException($"{folder} does not exist");
    if (FolderMaps(folder).Count == 0)
      throw new InvalidOperationException($"{folder} holds no map Better Continents loads ({string.Join(", ", MapFiles)})");
    var config = PluginConfig();
    var plan = new Plan
    {
      Folder = folder,
      PresetName = SafePresetName(presetName ?? PresetNameFor(folder, JsonStringFromFile(Path.Combine(folder, ManifestFileName), "worldName"))),
      PresetsDir = Presets.PresetsDir,
    };
    var cfgPath = Path.Combine(folder, ConfigFileName);
    if (configLines == null && File.Exists(cfgPath))
      configLines = File.ReadAllLines(cfgPath);
    plan.HadConfig = configLines != null;
    var overrides = Overrides(config, ParseConfig(configLines ?? []), plan.Applied, plan.Ignored);
    overrides[ConfigMapSourceDir] = ConfigDirectory(folder);
    overrides[ConfigEnabled] = true;
    plan.Values = ConfigValues.Snapshot(config, overrides);
    if (!plan.HadConfig)
      plan.Notes.Add("There is no export.cfg in the folder, so every setting but Directory comes from your BetterContinents.cfg.");
    return plan;
  }

  /// <summary>Any thread: builds the settings a new world would get from the plan, and writes the preset and its picture.
  /// <paramref name="cancelled"/> is asked before the files are written. Never throws.</summary>
  internal static Outcome BuildPreset(Plan plan, Func<bool>? cancelled = null)
  {
    var o = new Outcome();
    var path = plan.PresetPath;
    var fresh = path + ".new";
    try
    {
      plan.Progress = 2f;
      Log($"World import: building the preset \"{plan.PresetName}\" from {plan.Folder}");
      var settings = BetterContinentsSettings.CreateForImport(plan.Values, lean: true);
      plan.Progress = 75f;
      var loaded = settings.LoadedImageMaps().Select(m => m.FileName).ToList();
      o.Maps.AddRange(loaded);
      foreach (var file in FolderMaps(plan.Folder))
        if (!loaded.Contains(file))
          o.Warnings.Add($"{file} is in the folder but could not be loaded, so the preset has none of it. The log says why (every map must be a square image Better Continents can read).");
      if (loaded.Count == 0)
        throw new InvalidOperationException("no map in the folder could be loaded (the log says why)");
      if (cancelled?.Invoke() == true)
        throw new OperationCanceledException();
      Directory.CreateDirectory(plan.PresetsDir);
      DeleteQuietly(fresh);
      DeleteQuietly(fresh + ".tmp");
      settings.Save(fresh, currentFormat: true);
      // A preset of this name that no import of this folder made ("bc savepreset", another folder of the same name) is
      // kept, moved aside the way Presets.Save does; one this folder made is simply replaced.
      if (File.Exists(path) && !MadeFrom(plan.SourcePath, plan.Folder))
      {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
        File.Move(path, path + ".old-" + stamp);
        if (File.Exists(plan.ThumbnailPath))
          File.Move(plan.ThumbnailPath, plan.ThumbnailPath + ".old-" + stamp);
        o.Warnings.Add($"There was already a preset \"{plan.PresetName}\" that this folder did not make; it is kept as {Path.GetFileName(path)}.old-{stamp}.");
      }
      Replace(fresh, path);
      File.WriteAllText(plan.SourcePath, plan.Folder + Environment.NewLine);
      o.PresetPath = path;
      plan.Progress = 90f;
      var picture = RenderThumbnail(settings, ThumbnailSize);
      if (picture != null)
      {
        WorldExportPng.SaveRgb24(plan.ThumbnailPath, picture, ThumbnailSize);
        o.ThumbnailPath = plan.ThumbnailPath;
      }
      else
        DeleteQuietly(plan.ThumbnailPath);
      o.Summary = $"{loaded.Count} map(s): {string.Join(", ", loaded.Select(f => Path.GetFileNameWithoutExtension(f)))}; "
                  + $"Heightmap Amount {Inv(settings.HeightmapAmount)}, sea level {Inv(settings.SeaLevel)}, Biome precision {settings.BiomePrecision}, "
                  + $"world {Inv(settings.WorldSize)} m + {Inv(settings.EdgeSize)} m edge";
      plan.Progress = 100f;
    }
    catch (Exception e)
    {
      o.Error = e is OperationCanceledException ? "cancelled" : e.Message;
      if (e is not OperationCanceledException)
        LogError($"World import: the preset \"{plan.PresetName}\" could not be made: {e}");
      DeleteQuietly(fresh);
      DeleteQuietly(fresh + ".tmp");
    }
    return o;
  }

  // ---- the config ---------------------------------------------------------------------------------------------------

  /// <summary>What applying a folder to BetterContinents.cfg did.</summary>
  internal sealed class ConfigOutcome
  {
    public string? Backup, Error;
    public bool HadConfig;
    public readonly List<string> Changed = [];
    public readonly List<string> Ignored = [];
  }

  /// <summary>Main thread: export.cfg's world settings go into BetterContinents.cfg, Directory becomes the folder, Better
  /// Continents is enabled and the preset becomes "From Config". The file as it was is copied beside it first
  /// ("BetterContinents.cfg.before-import-&lt;time&gt;"), and the new file is saved once.</summary>
  internal static ConfigOutcome ApplyToConfig(string folder, IEnumerable<string>? configLines = null)
  {
    var o = new ConfigOutcome();
    try
    {
      folder = Path.GetFullPath(folder.Trim()).TrimEnd('/', '\\');
      if (!Directory.Exists(folder))
        throw new DirectoryNotFoundException($"{folder} does not exist");
      if (FolderMaps(folder).Count == 0)
        throw new InvalidOperationException($"{folder} holds no map Better Continents loads ({string.Join(", ", MapFiles)})");
      var config = PluginConfig();
      var cfgPath = Path.Combine(folder, ConfigFileName);
      if (configLines == null && File.Exists(cfgPath))
        configLines = File.ReadAllLines(cfgPath);
      o.HadConfig = configLines != null;
      var overrides = Overrides(config, ParseConfig(configLines ?? []), [], o.Ignored);
      overrides[ConfigMapSourceDir] = ConfigDirectory(folder);
      overrides[ConfigEnabled] = true;
      overrides[ConfigSelectedPreset] = Presets.FromConfigName;
      var file = config.ConfigFilePath;
      if (!string.IsNullOrEmpty(file) && File.Exists(file))
      {
        o.Backup = file + ".before-import-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
        File.Copy(file, o.Backup, true);
      }
      bool saveOnSet = config.SaveOnConfigSet;
      config.SaveOnConfigSet = false;
      try
      {
        // SelectedPreset last, so the New World dropdown shows "From Config" with the rest already in place.
        foreach (var kv in overrides.OrderBy(kv => kv.Key == ConfigSelectedPreset ? 1 : 0))
        {
          if (Equals(kv.Key.BoxedValue, kv.Value))
            continue;
          kv.Key.BoxedValue = kv.Value;
          o.Changed.Add($"[{kv.Key.Definition.Section}] {kv.Key.Definition.Key} = {kv.Key.GetSerializedValue()}");
        }
      }
      finally
      {
        config.SaveOnConfigSet = saveOnSet;
      }
      config.Save();
    }
    catch (Exception e)
    {
      o.Error = e.Message;
      LogError($"World import: could not apply {folder} to the config: {e}");
    }
    return o;
  }

  // ---- running it (bc_import, the HUD) -------------------------------------------------------------------------------

  /// <summary>Starts an import of <paramref name="folder"/>. A preset is built on a worker and selected in the New World
  /// screen when <paramref name="select"/>; the config way runs at once. Lines go to <paramref name="output"/> (and the
  /// log). False when it cannot start.</summary>
  public static bool Start(string folder, Mode mode, Action<string> output, bool select = true)
  {
    void Say(string line, bool warning = false)
    {
      if (warning)
        LogWarning(line);
      else
        Log(line);
      try
      {
        output(line);
      }
      catch
      {
        // The console that asked is gone (a scene change); the log has the line.
      }
    }
    if (IsRunning)
    {
      Say("World import: an import is already running; 'bc_import status' shows it.");
      return false;
    }
    string full;
    try
    {
      full = Path.GetFullPath(folder.Trim()).TrimEnd('/', '\\');
    }
    catch (Exception e)
    {
      Say($"World import: '{folder}' is not a folder ({e.Message}).", true);
      return false;
    }
    if (WorldExport.IsRunning && WorldExport.RunningDir != null && string.Equals(Path.GetFullPath(WorldExport.RunningDir).TrimEnd('/', '\\'), full, StringComparison.OrdinalIgnoreCase))
    {
      Say("World import: that export is still being written; import it when it has finished.", true);
      return false;
    }
    if (!File.Exists(Path.Combine(full, ManifestFileName)) && StampPattern.IsMatch(Path.GetFileName(full)))
      Say("World import: this export has no manifest.json, so it did not finish (the game stopped during it?); what it holds is used.", true);

    if (mode == Mode.Config)
    {
      var c = ApplyToConfig(full);
      if (c.Error != null)
      {
        LastError = c.Error;
        LastResult = $"Could not use {full} as the config: {c.Error}";
        Say("World import: " + LastResult, true);
        return false;
      }
      LastError = null;
      LastResult = $"BetterContinents.cfg now loads {full} ({c.Changed.Count} setting(s) changed); the preset is \"From Config\".";
      Say("World import: " + LastResult);
      if (!c.HadConfig)
        Say("World import: the folder has no export.cfg, so only Directory changed; every other setting is yours.");
      foreach (var line in c.Ignored.Take(10))
        Say("World import: ignored " + line, true);
      if (c.Backup != null)
        Say($"World import: your config as it was is saved as {c.Backup}.");
      Say("World import: create a New World now; it reads the PNGs in the folder as they are when you create it. No restart is needed.");
      Presets.RefreshActive();
      return true;
    }

    Plan plan;
    try
    {
      plan = MakePlan(full);
    }
    catch (Exception e)
    {
      LastError = e.Message;
      LastResult = $"Could not import {full}: {e.Message}";
      Say("World import: " + LastResult, true);
      return false;
    }
    if (instance == null)
    {
      Say("World import: Better Continents is not initialised.", true);
      return false;
    }
    IsRunning = true;
    running = plan;
    Phase = $"Making the preset \"{plan.PresetName}\"";
    LastError = null;
    Say($"World import: making the preset \"{plan.PresetName}\" from {full}. This takes a few seconds; the game keeps running.");
    foreach (var line in plan.Notes)
      Say("World import: " + line);
    foreach (var line in plan.Ignored.Take(10))
      Say("World import: ignored " + line, true);
    try
    {
      var task = Task.Run(() => BuildPreset(plan));
      instance.StartCoroutine(WaitAndFinish(task, plan, select, Say));
    }
    catch (Exception e)
    {
      IsRunning = false;
      running = null;
      Phase = "";
      LastError = e.Message;
      Say($"World import: could not start: {e.Message}", true);
      return false;
    }
    return true;
  }

  private static IEnumerator WaitAndFinish(Task<Outcome> task, Plan plan, bool select, Action<string, bool> say)
  {
    while (!task.IsCompleted)
      yield return null;
    Outcome o;
    try
    {
      o = task.Result;
    }
    catch (Exception e)
    {
      o = new Outcome { Error = e.GetBaseException().Message };
    }
    IsRunning = false;
    running = null;
    Phase = "";
    try
    {
      Complete(plan, o, select, say);
    }
    catch (Exception e)
    {
      LogError($"World import: finishing failed: {e}");
    }
  }

  // Main thread, after a preset build: select it, refresh the New World screen, and say what happened.
  internal static void Complete(Plan plan, Outcome o, bool select, Action<string, bool> say)
  {
    if (o.Error != null)
    {
      LastError = o.Error;
      LastResult = $"The preset \"{plan.PresetName}\" could not be made: {o.Error}";
      say("World import: " + LastResult, true);
      return;
    }
    LastError = null;
    LastPresetPath = o.PresetPath;
    if (select && ConfigSelectedPreset != null)
      ConfigSelectedPreset.Value = o.PresetPath!;
    Presets.RefreshActive();
    LastResult = select
      ? $"Preset \"{plan.PresetName}\" is made and selected: create a New World to use it."
      : $"Preset \"{plan.PresetName}\" is made: pick it under Better Continents when you create a New World.";
    say("World import: " + LastResult, false);
    say($"World import: {o.Summary}.", false);
    foreach (var w in o.Warnings)
      say("World import: " + w, true);
    say($"World import: saved as {o.PresetPath}.", false);
  }

  /// <summary>One line per state, for 'bc_import status' and the HUD.</summary>
  public static List<string> StatusLines()
  {
    var lines = new List<string>();
    lines.Add(IsRunning ? $"World import: {Phase} {Progress:0}%" : "World import: idle");
    if (LastResult != null)
      lines.Add($"Last import: {LastResult}");
    return lines;
  }

  // ---- the picture ----------------------------------------------------------------------------------------------------

  // Muted map colours per biome (the biome map's own colours are pure primaries), and the sea.
  private static readonly Dictionary<Heightmap.Biome, Rgb24> Land = new()
  {
    { Heightmap.Biome.None, new Rgb24(110, 110, 110) },
    { Heightmap.Biome.Meadows, new Rgb24(126, 160, 82) },
    { Heightmap.Biome.BlackForest, new Rgb24(64, 98, 58) },
    { Heightmap.Biome.Swamp, new Rgb24(112, 100, 72) },
    { Heightmap.Biome.Mountain, new Rgb24(226, 228, 232) },
    { Heightmap.Biome.Plains, new Rgb24(200, 182, 104) },
    { Heightmap.Biome.Mistlands, new Rgb24(98, 94, 112) },
    { Heightmap.Biome.AshLands, new Rgb24(150, 60, 44) },
    { Heightmap.Biome.DeepNorth, new Rgb24(206, 224, 238) },
    { Heightmap.Biome.Ocean, new Rgb24(56, 102, 144) },
  };
  private static readonly Rgb24 Shallow = new(78, 130, 170), Deep = new(24, 54, 92), Outside = new(16, 18, 22);

  /// <summary>A small map of the settings for the New World screen, north up: the biome map's biomes in map colours,
  /// shaded by the heightmap, sea below 30 m, dark past the world's edge. Null without a biome map and a heightmap.</summary>
  internal static Rgb24[]? RenderThumbnail(BetterContinentsSettings s, int size)
  {
    ImageMapFloat? height = null;
    ImageMapBiome? biome = null;
    foreach (var (file, map) in s.LoadedImageMaps())
    {
      if (file == "heightmap.png")
        height = map as ImageMapFloat;
      else if (file == "biomemap.png")
        biome = map as ImageMapBiome;
    }
    if (height == null && biome == null)
      return null;
    var metres = new float[size * size];
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
      {
        float u = (x + 0.5f) / size, v = 1f - (y + 0.5f) / size;
        metres[y * size + x] = height != null ? WorldExportMath.ValueToMetres(height.GetValue(u, v), s.HeightmapAmount, s.SeaLevel) : 100f;
      }
    var pixels = new Rgb24[size * size];
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
      {
        float u = (x + 0.5f) / size - 0.5f, v = 0.5f - (y + 0.5f) / size;
        if (u * u + v * v > 0.25f)
        {
          pixels[y * size + x] = Outside;
          continue;
        }
        float m = metres[y * size + x];
        var b = biome?.GetValue(u + 0.5f, v + 0.5f) ?? Heightmap.Biome.None;
        Rgb24 colour;
        if ((height != null && m < WorldExportMath.WaterLevel) || (height == null && b == Heightmap.Biome.Ocean))
        {
          float depth = height != null ? Mathf.Clamp01((WorldExportMath.WaterLevel - m) / 120f) : 0.5f;
          colour = Mix(Shallow, Deep, depth);
        }
        else
        {
          colour = biome != null && Land.TryGetValue(b, out var c) ? c : Altitude(m);
          if (height != null)
          {
            // Lit from the north-west: slopes that face it are brighter.
            float east = metres[y * size + Math.Min(size - 1, x + 1)] - metres[y * size + Math.Max(0, x - 1)];
            float south = metres[Math.Min(size - 1, y + 1) * size + x] - metres[Math.Max(0, y - 1) * size + x];
            colour = Scale(colour, Mathf.Clamp(1f + (east + south) * 0.004f, 0.55f, 1.4f));
          }
        }
        pixels[y * size + x] = colour;
      }
    return pixels;
  }

  private static Rgb24 Altitude(float metres) =>
    metres < 90f ? Mix(new Rgb24(112, 150, 82), new Rgb24(140, 132, 96), Mathf.InverseLerp(30f, 90f, metres))
    : metres < 200f ? Mix(new Rgb24(140, 132, 96), new Rgb24(196, 196, 196), Mathf.InverseLerp(90f, 200f, metres))
    : new Rgb24(236, 236, 240);

  private static Rgb24 Mix(Rgb24 a, Rgb24 b, float t) =>
    new((byte)Mathf.RoundToInt(Mathf.Lerp(a.R, b.R, t)), (byte)Mathf.RoundToInt(Mathf.Lerp(a.G, b.G, t)), (byte)Mathf.RoundToInt(Mathf.Lerp(a.B, b.B, t)));

  private static Rgb24 Scale(Rgb24 c, float f) =>
    new((byte)Mathf.Clamp(Mathf.RoundToInt(c.R * f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.G * f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.B * f), 0, 255));

  // ---- small helpers ------------------------------------------------------------------------------------------------

  private static string Inv(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

  private static void DeleteQuietly(string path)
  {
    try
    {
      if (File.Exists(path))
        File.Delete(path);
    }
    catch (Exception e)
    {
      LogWarning($"World import: could not delete {path}: {e.Message}");
    }
  }

  private static bool MadeFrom(string sourcePath, string folder)
  {
    try
    {
      return File.Exists(sourcePath)
             && string.Equals(File.ReadAllLines(sourcePath).FirstOrDefault()?.Trim().TrimEnd('/', '\\'), folder, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
      return false;
    }
  }

  private static void Replace(string from, string to)
  {
    if (File.Exists(to))
      File.Delete(to);
    File.Move(from, to);
  }

  private static string? JsonStringFromFile(string path, string key)
  {
    try
    {
      return File.Exists(path) ? JsonString(File.ReadAllText(path), key) : null;
    }
    catch
    {
      return null;
    }
  }

  // manifest.json is written by WorldExportJson; the first "key": "string" / "key": number is the top-level one.
  internal static string? JsonString(string json, string key)
  {
    var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
    return m.Success ? Unescape(m.Groups[1].Value) : null;
  }

  internal static long? JsonNumber(string json, string key)
  {
    var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
    return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
  }

  private static string Unescape(string s)
  {
    if (s.IndexOf('\\') < 0)
      return s;
    var sb = new StringBuilder(s.Length);
    for (int i = 0; i < s.Length; i++)
    {
      var c = s[i];
      if (c != '\\' || i + 1 >= s.Length)
      {
        sb.Append(c);
        continue;
      }
      var e = s[++i];
      switch (e)
      {
        case 'n': sb.Append('\n'); break;
        case 'r': sb.Append('\r'); break;
        case 't': sb.Append('\t'); break;
        case 'b': sb.Append('\b'); break;
        case 'f': sb.Append('\f'); break;
        case 'u':
          if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
          {
            sb.Append((char)code);
            i += 4;
          }
          break;
        default: sb.Append(e); break;
      }
    }
    return sb.ToString();
  }
}

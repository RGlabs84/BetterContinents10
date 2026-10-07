// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for the Forest Scale default (0.10.2), and on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

/// <summary>What a setting is for, which decides who reads it and when.</summary>
internal enum SettingScope
{
  /// <summary>A default for NEW worlds: baked into a world's settings when the world is created (From Config, an import,
  /// a Directory's export.cfg) and never read again for that world.</summary>
  World,
  /// <summary>Applies while the game runs (LiveConfig): the export HUD and gate, the export defaults, the transfer rate. Also what a machine
  /// that runs a world decides for itself, when a world is made and when it loads (Wide Sectors); never part of a world, an export or an import.</summary>
  Live,
  /// <summary>This game's own bookkeeping and debug switches: never part of a world, an export or an import.</summary>
  Local,
}

/// <summary>What a live change of a world setting (the "bc" console in debug mode) has to redo.</summary>
internal enum LiveChange
{
  /// <summary>Everything that reads the settings: noise, patches, the minimap, the alt-biome grid and the zones.</summary>
  Regenerate,
  /// <summary>Biome precision: how zones sample the biomes, not the biomes - patches and the zones only.</summary>
  Precision,
  /// <summary>Heightmap Alpha: how the heightmap is read - decode it again, then everything Regenerate redoes.</summary>
  HeightmapDecode,
  /// <summary>High Terrain: which of the game's height rules are patched, not the world itself - DynamicPatch switches the patches, the alt-biome
  /// placement is made again for the height limit they lift, and the zones are generated again; the loaded terrain, the noise and the minimap are
  /// left as they are (HighTerrain.ModeChanged).</summary>
  Rules,
}

/// <summary>One setting, declared once: its config section, key, type, default, range and description; for a world
/// setting also where the world's settings hold it (in config units) and its "bc" console name. The config file, a new
/// world's settings, the console, the export's export.cfg and the import all read it from here, so none of them can
/// disagree with another about a default, a range or a name.</summary>
internal abstract class SettingDef(string key, string description, SettingScope scope)
{
  public readonly string Key = key;
  public readonly string Description = description;
  public readonly SettingScope Scope = scope;
  public bool Hidden;
  /// <summary>Its config section, "NN BetterContinents.&lt;Group&gt;" (set by SettingsSchema, known without a bound config).</summary>
  public string Section { get; internal set; } = "";

  /// <summary>The bound entry (after <see cref="SettingsSchema.Bind"/>).</summary>
  public abstract ConfigEntryBase EntryBase { get; }
  public abstract Type ValueType { get; }

  /// <summary>"bc &lt;group&gt; &lt;name&gt;" in the debug console, and the name its settings window shows; null when the
  /// console has no command for it.</summary>
  public string? ConsoleGroup, ConsoleName, ConsoleLabel;
  public LiveChange OnLiveChange = LiveChange.Regenerate;

  internal abstract void Bind(GroupBuilder group);

  /// <summary>A world setting a new world copies straight into its settings (the maps and the alt-biome options are read
  /// by their own code).</summary>
  public abstract bool ReadsIntoSettings { get; }

  /// <summary>World settings: copies the value from the config (or an import's snapshot of it) into the settings.</summary>
  internal abstract void Read(ConfigValues values, BetterContinentsSettings settings);
}

internal sealed class SettingDef<T>(string key, string description, SettingScope scope, T defaultValue, Action<ConfigEntry<T>> assign)
  : SettingDef(key, description, scope) where T : IComparable
{
  public readonly T Default = defaultValue;
  public AcceptableValueBase? Range;
  /// <summary>A range for the console's settings window where the config has none.</summary>
  public (T Min, T Max)? ConsoleRange;
  /// <summary>The value as a world's settings hold it, in config units (for example Sea Level 0..1, not the offset).</summary>
  public Func<BetterContinentsSettings, T>? Get;
  public Action<BetterContinentsSettings, T>? Set;

  public ConfigEntry<T> Entry = null!;
  public override ConfigEntryBase EntryBase => Entry;
  public override Type ValueType => typeof(T);

  internal override void Bind(GroupBuilder group)
  {
    var builder = group.AddValue(Key).Description(Description).Default(Default);
    if (Range != null)
      builder.Range(Range);
    if (Hidden)
      builder.Hidden();
    builder.Bind(out Entry);
    if (Entry.Definition.Section != Section)
      LogError($"Config: {Key} was bound in section '{Entry.Definition.Section}', expected '{Section}'");
    assign(Entry);
  }

  public override bool ReadsIntoSettings => Scope == SettingScope.World && Set != null;

  internal override void Read(ConfigValues values, BetterContinentsSettings settings)
  {
    if (Set != null)
      Set(settings, values.Get(Entry));
  }

  /// <summary>The console's range: the config's own, else <see cref="ConsoleRange"/>.</summary>
  public (T Min, T Max)? Limits => Range is AcceptableValueRange<T> r ? (r.MinValue, r.MaxValue) : ConsoleRange;
}

/// <summary>A config section: "NN BetterContinents.&lt;Name&gt;", where NN is its position. Sections are never reordered or
/// renamed, and a new one only ever goes last: the number is part of the name, so moving a section would rename every one
/// after it and reset the values players saved there.</summary>
internal sealed class SettingGroup(string name, params SettingDef[] settings)
{
  public readonly string Name = name;
  public readonly SettingDef[] Settings = settings;
}

/// <summary>Every Better Continents setting, in the order of BetterContinents.cfg.</summary>
internal static class SettingsSchema
{
  static SettingDef<T> S<T>(string key, string description, SettingScope scope, T defaultValue, Action<ConfigEntry<T>> assign) where T : IComparable =>
    new(key, description, scope, defaultValue, assign);

  static AcceptableValueRange<float> R(float min, float max) => new(min, max);
  static AcceptableValueRange<int> R(int min, int max) => new(min, max);

  // ---- 00 Debug -------------------------------------------------------------------------------------------------------
  public static readonly SettingDef<bool> Enabled = S("Enabled", "Whether this mod is enabled", SettingScope.World, true, e => ConfigEnabled = e);
  public static readonly SettingDef<bool> DebugMode = S("Debug Mode", "For editing a world's settings live with the bc commands. In a world that uses Better Continents, on the machine that runs it (single player or the host), it turns devcommands on when you spawn, shows \"Better Continents Debug Mode Enabled!\" at the top left, and adds a Better Continents button to the Esc menu and the large map that opens the settings window (Alt+F8 there does the same). Leave it off for a world you play.", SettingScope.Local, false, e => ConfigDebugModeEnabled = e);
  public static readonly SettingDef<string> DebugResetCommand = S("Debug Reset Command", "Empty (the default): Better Continents regenerates the zones itself after a change made with the bc commands and on 'bc regen'. Every generated zone is reset, and generates again with the current settings when somebody comes near, except zones within one zone of something a player built or worked on (a field, a path, levelled ground), a tombstone, a player connected from another machine (as far as their game keeps zones loaded around them), or you inside a dungeon. A location that reaches into one of those zones is kept whole. Players, what players built, tombstones and tamed animals are never removed. A console command written here (for example zones_reset start) runs instead.", SettingScope.Local, "", e => ConfigDebugResetCommand = e);
  public static readonly SettingDef<string> OverrideVersion = S("Override version", "Empty (the default): every world is saved in its own settings version (one older than 11 as 11), and a new world gets the newest (12, made by 0.10). A number here saves every world in that version instead, and a new world made From Config gets it when it is 11 or more: 11 makes worlds as Better Continents 0.9 did. For testing only: a world saved in an older version than its own loses what its version added.", SettingScope.World, "", e => ConfigOverrideVersion = e);
  public static readonly SettingDef<string> Directory = S("Directory", "A folder of maps, each loaded by its standard name: heightmap.png, biomemap.png, terrainmap.png, locationmap.png, roughmap.png, forestmap.png, heatmap.png, paintmap.png, lavamap.png, mossmap.png, vegetationmap.png, spawnmap.png, altbiomemap.png (and the legends beside them). When it is set, the map file settings are not used. A folder with an export.cfg in it (a world export) also brings the settings in that file, which win over this one.", SettingScope.World, "", e => ConfigMapSourceDir = e);

  // ---- 01 Global ------------------------------------------------------------------------------------------------------
  public static readonly SettingDef<bool> SkipDefaultLocations = new("Skip Default Locations", "Skips the default location placement. Spawn temple and location map are still placed.", SettingScope.World, false, e => ConfigSkipDefaultLocations = e)
  { Get = s => s.SkipDefaultLocations, Set = (s, v) => s.SkipDefaultLocations = v, ConsoleGroup = "g", ConsoleName = "skipdefaultlocations", ConsoleLabel = "Skip default locations" };
  public static readonly SettingDef<float> ContinentSize = new("Continent Size", "Continent size", SettingScope.World, 0.5f, e => ConfigContinentSize = e)
  { Range = R(0f, 1f), Get = s => s.ContinentSize, Set = (s, v) => s.ContinentSize = v, ConsoleGroup = "g", ConsoleName = "cs", ConsoleLabel = "Continent size adjustment" };
  public static readonly SettingDef<float> WorldSize = new("World Size", "The world's radius in metres (vanilla 10000). A new world is made to its size: its maps span World Size + Edge Size, and its alt-biome grid, locations, the game's own biome bands, Ashlands, Deep North, lakes and rivers, and the minimap follow it. A world made by an older Better Continents keeps vanilla's 21000 m, and World Size only moves its edge. Expand World Size, when installed, sets the world's size and lays it out itself.", SettingScope.World, 10000f, e => ConfigWorldSize = e)
  { ConsoleRange = (0f, 1000000f), Get = s => s.WorldSize, Set = (s, v) => s.WorldSize = v, ConsoleGroup = "g", ConsoleName = "worldsize", ConsoleLabel = "World size" };
  public static readonly SettingDef<float> EdgeSize = new("Edge Size", "How far the world's edge reaches past World Size, in metres (vanilla 500). On a new world the land drops away over it.", SettingScope.World, 500f, e => ConfigEdgeSize = e)
  { ConsoleRange = (0f, 1000000f), Get = s => s.EdgeSize, Set = (s, v) => s.EdgeSize = v, ConsoleGroup = "g", ConsoleName = "edgesize", ConsoleLabel = "Edge size" };
  public static readonly SettingDef<float> SeaLevel = new("Sea Level Adjustment", "Modify sea level, which changes the land:sea ratio", SettingScope.World, 0.5f, e => ConfigSeaLevelAdjustment = e)
  { Range = R(0f, 1f), Get = s => s.SeaLevel, Set = (s, v) => s.SeaLevel = v, ConsoleGroup = "g", ConsoleName = "sl", ConsoleLabel = "Sea level adjustment" };
  public static readonly SettingDef<bool> AshlandsGap = new("Ashlands Gap", "Whether to add the Ashlands ocean gap (usually custom maps don't need this)", SettingScope.World, false, e => ConfigAshlandsGapEnabled = e)
  { Get = s => s.AshlandsGapEnabled, Set = (s, v) => s.AshlandsGapEnabled = v, ConsoleGroup = "g", ConsoleName = "ag", ConsoleLabel = "Ashlands Gap" };
  public static readonly SettingDef<bool> DeepNorthGap = new("Deep North Gap", "Whether to add the Deep North ocean gap (usually custom maps don't need this)", SettingScope.World, false, e => ConfigDeepNorthGapEnabled = e)
  { Get = s => s.DeepNorthGapEnabled, Set = (s, v) => s.DeepNorthGapEnabled = v, ConsoleGroup = "g", ConsoleName = "ng", ConsoleLabel = "Deep North Gap" };
  public static readonly SettingDef<bool> Rivers = new("Rivers", "Whether rivers should be enabled or not", SettingScope.World, true, e => ConfigRiversEnabled = e)
  { Get = s => s.RiversEnabled, Set = (s, v) => s.RiversEnabled = v, ConsoleGroup = "g", ConsoleName = "r", ConsoleLabel = "Rivers" };
  public static readonly SettingDef<bool> MapEdgeDropoff = new("Map Edge Drop-off", "Whether the map should drop off at the edges or not (consequences unknown!)", SettingScope.World, true, e => ConfigMapEdgeDropoff = e)
  { Get = s => s.MapEdgeDropoff, Set = (s, v) => s.MapEdgeDropoff = v, ConsoleGroup = "g", ConsoleName = "me", ConsoleLabel = "Map edge drop off" };
  public static readonly SettingDef<bool> MountainsAllowedAtCenter = new("Mountains Allowed At Center", "Whether the map should allow mountains to occur at the map center (if you have default spawn then you should keep this unchecked)", SettingScope.World, false, e => ConfigMountainsAllowedAtCenter = e)
  { Get = s => s.MountainsAllowedAtCenter, Set = (s, v) => s.MountainsAllowedAtCenter = v, ConsoleGroup = "g", ConsoleName = "mc", ConsoleLabel = "Allow mountains in center" };

  // ---- 02 Heightmap ---------------------------------------------------------------------------------------------------
  public static readonly SettingDef<string> HeightmapFile = S("Heightmap File", "Path to a heightmap: a square image, 16-bit grey for smooth slopes, white highest (see Heightmap Amount). A heightmap-fine.png beside it (8-bit grey, the same size) adds finer steps. Not used when Directory is set: its heightmap.png is used.", SettingScope.World, "", e => ConfigHeightFile = e);
  // The most a heightmap can be multiplied by: 81 gives -30 m to 16,170 m with Sea Level Adjustment 0.5 (200 m per unit, the sea at 30 m).
  // A world whose heightmap is read at an amount above 5, the most before 0.10.3, also gets the fixes of HighTerrain (by default: see High Terrain).
  internal const float MaxHeightmapAmount = 81f;
  public static readonly SettingDef<float> HeightmapAmount = new("Heightmap Amount", "Multiplier of the heightmap's values. With Sea Level Adjustment 0.5, Heightmap Amount 1 (the default) gives heights of -30 m to 170 m (the sea is at 30 m) and clips vanilla's highest mountains; 2 gives -30 m to 370 m and keeps them all; 81, the most, gives -30 m to 16,170 m. A world whose heightmap is read at an amount above 5, the most older versions allowed, is also given Better Continents' fixes for the height rules the game assumes (what counts as inside a dungeon, where the ground and the grass are looked for, how high plants, creatures and locations may be, which heights the creatures can walk). A world export records the amount its heightmap was made for. At 81 one step of a 16-bit heightmap is 25 cm high: a heightmap-fine.png beside it makes the steps finer.", SettingScope.World, 1f, e => ConfigHeightmapAmount = e)
  { Range = R(0f, MaxHeightmapAmount), Get = s => s.HeightmapAmount, Set = (s, v) => s.HeightmapAmount = v, ConsoleGroup = "h", ConsoleName = "am", ConsoleLabel = "Heightmap Amount" };
  public static readonly SettingDef<float> HeightmapBlend = new("Heightmap Blend", "How strongly to blend the heightmap file into the final result", SettingScope.World, 1f, e => ConfigHeightmapBlend = e)
  { Range = R(0f, 1f), Get = s => s.HeightmapBlend, Set = (s, v) => s.HeightmapBlend = v, ConsoleGroup = "h", ConsoleName = "bl", ConsoleLabel = "Heightmap Blend" };
  public static readonly SettingDef<float> HeightmapAdd = new("Heightmap Add", "How strongly to add the heightmap file to the final result (usually you want to blend it instead)", SettingScope.World, 0f, e => ConfigHeightmapAdd = e)
  { Range = R(-1f, 1f), Get = s => s.HeightmapAdd, Set = (s, v) => s.HeightmapAdd = v, ConsoleGroup = "h", ConsoleName = "ad", ConsoleLabel = "Heightmap Add" };
  public static readonly SettingDef<float> HeightmapMask = new("Heightmap Mask", "How strongly to apply the heightmap as a mask on normal height generation (i.e. it limits maximum height to the height of the mask)", SettingScope.World, 0f, e => ConfigHeightmapMask = e)
  { Range = R(0f, 1f), Get = s => s.HeightmapMask, Set = (s, v) => s.HeightmapMask = v, ConsoleGroup = "h", ConsoleName = "ma", ConsoleLabel = "Heightmap Mask" };
  public static readonly SettingDef<bool> HeightmapOverrideAll = new("Heightmap Override All", "All other aspects of the height calculation will be disabled, so the world will perfectly conform to your heightmap. With it off, the game's own biome heights are made over the heightmap's, and the Mountain biome doubles what is over 80 m: a peak of 1,000 m in the heightmap is about 1,900 m high", SettingScope.World, true, e => ConfigHeightmapOverrideAll = e)
  { Get = s => s.HeightmapOverrideAll, Set = (s, v) => s.HeightmapOverrideAll = v, ConsoleGroup = "h", ConsoleName = "ov", ConsoleLabel = "Heightmap Override All" };
  public static readonly SettingDef<bool> HeightmapAlpha = new("Heightmap Alpha", "Enables alpha channel for the heightmap file to blend vanilla generation with the heightmap. A new world reads the heightmap at full precision and blends it by its alpha: where a pixel is transparent, the game's own terrain shows. A world made by an older Better Continents keeps reading the heightmap at 8 bits, without the blend.", SettingScope.World, false, e => ConfigHeightmapAlpha = e)
  { Get = s => s.HeightMapAlpha, Set = (s, v) => s.HeightMapAlpha = v, ConsoleGroup = "h", ConsoleName = "alpha", ConsoleLabel = "Heightmap Alpha", OnLiveChange = LiveChange.HeightmapDecode };
  public static readonly SettingDef<string> RoughmapFile = S("Roughmap File", "Path to a roughmap: a square grey image of where the biomes' own rough ground shows through the heightmap (white) and where the heightmap stays smooth (black). Only used with Heightmap Override All off. Not used when Directory is set: its roughmap.png is used.", SettingScope.World, "", e => ConfigRoughFile = e);
  public static readonly SettingDef<float> RoughmapBlend = new("Roughmap Blend", "How strongly to apply the roughmap file", SettingScope.World, 1f, e => ConfigRoughmapBlend = e)
  { Range = R(0f, 1f), Get = s => s.RoughmapBlend, Set = (s, v) => s.RoughmapBlend = v, ConsoleGroup = "r", ConsoleName = "bl", ConsoleLabel = "Roughmap Blend" };
  // Last in its section: a setting added in the middle would move the order of every one after it in the file.
  public static readonly SettingDef<HighTerrainMode> HighTerrain = new("High Terrain", "Whether Better Continents adjusts the game's own rules for high ground. Those rules assume land under about 400 m: what counts as inside a dungeon (anything above 3,000 m), where the ground is looked for from, how high grass, plants, creatures and locations may stand, which ground creatures in dungeons can walk, the Valkyrie's flight height, random events, and the Deep North weather test. Auto (the default): a world whose heightmap is read at a Heightmap Amount above 5 gets the adjustments, and every other world keeps the game's own rules. On: every Better Continents world gets them, whatever its heights. Off: none does, whatever Heightmap Amount is. Off costs a world with high ground: no grass above 500 m, no plants, creatures or locations above about 1,000 m, no terrain, weather or building above 3,000 m (the game takes it for the inside of a dungeon), and no ground found above 6,000 m. A new world takes this setting when it is made (From Config, bc_import, or a Directory's export.cfg) and keeps it; 'bc h ht' changes it in a world that is running (Debug Mode)", SettingScope.World, HighTerrainMode.Auto, e => ConfigHighTerrain = e)
  { Get = s => s.HighTerrainMode, Set = (s, v) => s.HighTerrainMode = HighTerrainModes.Known(v), ConsoleGroup = "h", ConsoleName = "ht", ConsoleLabel = "High Terrain", OnLiveChange = LiveChange.Rules };

  // ---- 03 Biomemap ----------------------------------------------------------------------------------------------------
  public static readonly SettingDef<string> BiomemapFile = S("Biomemap File", "Path to a biome map: a square image with a colour per biome, named by the legend beside it (biomemap.txt, 'Biome: colour' lines, written with the default colours when missing). Not used when Directory is set: its biomemap.png is used.", SettingScope.World, "", e => ConfigBiomeFile = e);
  public static readonly SettingDef<int> BiomePrecision = new("Biome precision", "How closely the ground follows the biome borders inside each 64 m terrain zone (ground textures, grass, vegetation and spawn points). 0 = vanilla: a zone takes its biomes from its 4 corners, so the borders follow the 64 m zone grid. 1 to 5 split every zone into (N + 1) x (N + 1) cells, with a corner every 32, 21, 16, 13 or 11 m; a new world goes on up to 31 (a corner every 2 m), and an older one stops at 5. Works with or without a biomemap, and terrain heights do not change", SettingScope.World, 0, e => ConfigBiomePrecision = e)
  { Range = R(0, 31), Get = s => s.BiomePrecision, Set = (s, v) => s.BiomePrecision = v, ConsoleGroup = "b", ConsoleName = "p", ConsoleLabel = "Biome precision", OnLiveChange = LiveChange.Precision };
  public static readonly SettingDef<string> TerrainmapFile = S("Terrainmap file", "Path to a terrain map: a square image of the ground colour, named by its legend (terrainmap.txt, 'Ground: colour' lines). Not used when Directory is set: its terrainmap.png is used.", SettingScope.World, "", e => ConfigTerrainFile = e);

  // ---- 04 Forest ------------------------------------------------------------------------------------------------------
  // The setter runs the value through FeatureScaleCurve (BetterContinentsSettings), on which 0.5 is the game's own scale.
  // The range stays 0 to 10, as since 0.7.20, so a value somebody chose keeps its meaning.
  public static readonly SettingDef<float> ForestScale = new("Forest Scale", "Size of the game's own forest and clearing patches (a forestmap is not scaled). 0.5 (the default) is the game's own size, 0 about a third of it, 1 about five times larger. Keep it within 0 to 1: near 1.15 the game's own forest is the same everywhere, and above that the patches shrink again.", SettingScope.World, 0.5f, e => ConfigForestScale = e)
  { Range = R(0f, 10f), Get = s => s.ForestScaleFactor, Set = (s, v) => s.ForestScaleFactor = v, ConsoleGroup = "fo", ConsoleName = "sc", ConsoleLabel = "Forest Scale" };
  public static readonly SettingDef<float> ForestAmount = new("Forest Amount", "Adjusts how much forest there is, relative to clearings", SettingScope.World, 0.5f, e => ConfigForestAmount = e)
  { Range = R(0f, 1f), Get = s => s.ForestAmount, Set = (s, v) => s.ForestAmount = v, ConsoleGroup = "fo", ConsoleName = "am", ConsoleLabel = "Forest Amount" };
  public static readonly SettingDef<bool> ForestFactorOverrideAllTrees = new("Forest Factor Overrides All Trees", "Trees in all biomes will be affected by forest factor (both procedural and from forestmap)", SettingScope.World, false, e => ConfigForestFactorOverrideAllTrees = e)
  { Get = s => s.ForestFactorOverrideAllTrees, Set = (s, v) => s.ForestFactorOverrideAllTrees = v, ConsoleGroup = "fo", ConsoleName = "ffo", ConsoleLabel = "Forest Factor Override All" };
  public static readonly SettingDef<string> ForestmapFile = S("Forestmap File", "Path to a forest map: a square grey image, white for more forest, combined with the game's own forest by Forestmap Multiply and Forestmap Add. Not used when Directory is set: its forestmap.png is used.", SettingScope.World, "", e => ConfigForestFile = e);
  public static readonly SettingDef<float> ForestmapMultiply = new("Forestmap Multiply", "How strongly to scale the vanilla forest factor by the forestmap", SettingScope.World, 1f, e => ConfigForestmapMultiply = e)
  { Range = R(0f, 1f), Get = s => s.ForestmapMultiply, Set = (s, v) => s.ForestmapMultiply = v, ConsoleGroup = "fo", ConsoleName = "mu", ConsoleLabel = "Forestmap Multiply" };
  public static readonly SettingDef<float> ForestmapAdd = new("Forestmap Add", "How strongly to add the forestmap directly to the vanilla forest factor", SettingScope.World, 1f, e => ConfigForestmapAdd = e)
  { Range = R(0f, 1f), Get = s => s.ForestmapAdd, Set = (s, v) => s.ForestmapAdd = v, ConsoleGroup = "fo", ConsoleName = "add", ConsoleLabel = "Forestmap Add" };

  // ---- 05 StartPosition -----------------------------------------------------------------------------------------------
  public static readonly SettingDef<bool> OverrideStartPosition = new("Override Start Position", "Whether to override the start position using the values provided (warning: will disable all validation of the position)", SettingScope.World, false, e => ConfigOverrideStartPosition = e)
  { Get = s => s.OverrideStartPosition, Set = (s, v) => s.OverrideStartPosition = v, ConsoleGroup = "st", ConsoleName = "os", ConsoleLabel = "Override Start Position" };
  public static readonly SettingDef<float> StartPositionX = new("Start Position X", "Start position override X value, in metres from the world's centre (vanilla's world reaches 10500 either way)", SettingScope.World, 0f, e => ConfigStartPositionX = e)
  { Range = R(-1000000f, 1000000f), Get = s => s.StartPositionX, Set = (s, v) => s.StartPositionX = v, ConsoleGroup = "st", ConsoleName = "x", ConsoleLabel = "Start Position X" };
  public static readonly SettingDef<float> StartPositionY = new("Start Position Y", "Start position override Y value, in metres from the world's centre (vanilla's world reaches 10500 either way)", SettingScope.World, 0f, e => ConfigStartPositionY = e)
  { Range = R(-1000000f, 1000000f), Get = s => s.StartPositionY, Set = (s, v) => s.StartPositionY = v, ConsoleGroup = "st", ConsoleName = "y", ConsoleLabel = "Start Position Y" };

  // ---- 06 Maps --------------------------------------------------------------------------------------------------------
  public static readonly SettingDef<string> LocationmapFile = S("Locationmap File", "Path to a location map: a square image, black with one dot of colour per location, named by its legend (locationmap.txt, 'Location: colour' lines). Not used when Directory is set: its locationmap.png is used.", SettingScope.World, "", e => ConfigLocationFile = e);
  public static readonly SettingDef<string> SpawnmapFile = S("Spawnmap File", "Path to a spawn map: a square image of which creatures may spawn where, named by its legend (spawnmap.txt, 'r,g,b,a: +Creature, -Creature' lines; white stops every spawner). Not used when Directory is set: its spawnmap.png is used.", SettingScope.World, "", e => ConfigSpawnFile = e);
  public static readonly SettingDef<string> VegetationmapFile = S("Vegetationmap File", "Path to a vegetation map: a square image of which plants may grow where, named by its legend (vegetationmap.txt, 'r,g,b,a: +Plant, -Plant' lines). Not used when Directory is set: its vegetationmap.png is used.", SettingScope.World, "", e => ConfigVegetationFile = e);
  public static readonly SettingDef<string> PaintmapFile = S("Paintmap File", "Path to a paint map: a square image of the ground paint (the terrain mask colour), each colour as it is or as paintmap.txt maps it ('mask colour: image colour' lines). Not used when Directory is set: its paintmap.png is used.", SettingScope.World, "", e => ConfigPaintFile = e);
  public static readonly SettingDef<string> LavamapFile = S("Lavamap File", "Path to a lava map: a square grey image of where the Ashlands ground is lava (white). Not used when Directory is set: its lavamap.png is used.", SettingScope.World, "", e => ConfigLavaFile = e);
  public static readonly SettingDef<string> MossmapFile = S("Mossmap File", "Path to a moss map: a square grey image of the Mistlands ground cover. Not used when Directory is set: its mossmap.png is used.", SettingScope.World, "", e => ConfigMossFile = e);
  public static readonly SettingDef<string> HeatmapFile = S("Heatmap File", "Path to a heat map: a square grey image of the Ashlands heat, which decides where the Ashlands' weather and lava and ship damage are (anything above black). Not used when Directory is set: its heatmap.png is used.", SettingScope.World, "", e => ConfigHeatFile = e);
  public static readonly SettingDef<float> HeatmapScale = new("Heatmap Scale", "Multiplies the heat map (white = this value). Most heat effects cap at 1; 10 covers the game's whole Ashlands ocean gradient", SettingScope.World, 10f, e => ConfigHeatScale = e)
  { Range = R(0f, 100f), Get = s => s.HeatMapScale, Set = (s, v) => s.HeatMapScale = v, ConsoleGroup = "heat", ConsoleName = "sc", ConsoleLabel = "Heatmap Scale" };

  // ---- 07 Misc --------------------------------------------------------------------------------------------------------
  public static readonly SettingDef<int> NexusId = new("NexusID", "", SettingScope.Local, 446, e => NexusID = e) { Hidden = true };
  public static readonly SettingDef<string> SelectedPreset = new("SelectedPreset", "", SettingScope.Local, "Disabled", e => ConfigSelectedPreset = e) { Hidden = true };
  /// <summary>Which of <see cref="SettingsSchema.Migrate"/>'s one-time changes this file has had; 0 = none (a file written
  /// before the unifying refactor, or a new one).</summary>
  public static readonly SettingDef<int> ConfigVersion = new("Config Version", "", SettingScope.Local, 0, e => ConfigFileVersion = e) { Hidden = true };
  public static readonly SettingDef<TransferRatePreset> SettingsTransferRate = S("Settings Transfer Rate", "How fast this machine (the host, or a dedicated server) may push a joining player's Better Continents settings and images over their Steam connection. Valheim pins every connection to about 150 KB/s, so a large world can take over a minute to join; Vanilla leaves that rate untouched; KB256, KB384, KB512, KB768, MB1, MB1_5 and MB3 set that one connection to that fixed rate for the transfer only, then restore Valheim's own; Unlimited sets 100 MB/s (the transfer itself moves about 4 MB/s at most). Steam sends at exactly the rate set, with no congestion control, so pick one the server's upload can carry. The server's value is used; a client's own value has no effect. No effect on PlayFab (crossplay) connections, whose send rate cannot be changed.", SettingScope.Live, TransferRatePreset.KB512, e => ConfigSettingsTransferRate = e);

  public static readonly SettingDef<CompactMapsMode> CompactMaps = new("Compact Maps", "Whether a new world keeps its maps compressed in memory, in tiles of 128 x 128 pixels that are decoded when the world first reads them (Map Memory), and saves and sends them compressed: a far smaller world file, and far less memory, for a world of large maps. Every map reads exactly as it would without. Auto (the default): a new world is compact when any of its maps is more than 8192 pixels across (as pictures such maps take gigabytes of memory, and hundreds of megabytes in the world file and in what every joining player is sent) or when its heightmap has fine heights (a heightmap-fine.png beside it), and keeps its maps as pictures otherwise. On: every new world is compact. Off: no new world is compact, not even one of 16384 pixel maps, and a heightmap-fine.png is not read. Read only when a world is made (From Config, bc_import, or a Directory's export.cfg) with the newest settings version; a world keeps it for good. Better Continents 0.9 cannot read a compact world, and would generate its terrain as vanilla. An older config's true and false read as On and Auto", SettingScope.World, CompactMapsMode.Auto, e => ConfigCompactMaps = e)
  { Set = (s, v) => s.SetCompactMaps(v) };
  public static readonly SettingDef<int> MapMemory = new("Map Memory", "For a world made with Compact Maps: how many megabytes this machine lets the decoded tiles of its maps take. Past this, the tiles read least recently are dropped, and decoded again when read again. 512 holds every tile of a world's maps up to about 8192 pixels across, so each is decoded once; the tiles of a world of 16384 pixel maps are more than that (a heightmap's are 537 MB decoded), and what players read across the whole world is decoded again when it was dropped; a server short of memory can use less (each player's surroundings take about a megabyte). No effect on any other world, which holds its maps decoded. Applies at once", SettingScope.Live, 512, e => ConfigMapMemory = e)
  { Range = R(64, 65536) };
  public static readonly SettingDef<int> MaxMapSize = new("Max Map Size", "The largest map, in pixels across, that this machine reads from a picture file: 4096, 8192 or 16384. A bigger picture is refused before it is read, with a line in the log, and the world is made without that map. A world's own maps, saved with it, are never refused. Making a world from five 16384 pixel maps takes about 1.8 GB of memory (such a world is saved compact), so a machine short of memory can stop at 8192 or 4096. Applies the next time a map is read", SettingScope.Live, ImageMapBase.LargestMapSize, e => ConfigMaxMapSize = e)
  { Range = new AcceptableValueList<int>(4096, 8192, 16384) };
  public static readonly SettingDef<WideSectorsMode> WideSectors = new("Wide Sectors", "Whether a world that reaches past the game's own sectors (its World Size + Edge Size is past 16,350 m) gets a sector and save chunks of its own for every zone out to 65.5 km, where the game gives everything beyond 16.35 km one shared sector (every player who comes near any of it is sent all of it, and it is saved as one chunk). Auto (the default): a new world past 16,350 m is made with wide sectors, and an existing world keeps the sectors it has. On: as Auto, and an existing world past 16,350 m that still has the game's sectors is converted when it loads. The conversion goes one way: the game saves a copy of the old save first, and a Better Continents without wide sectors (or the game alone) must not save the world afterwards, or it writes objects twice. Off: a new world gets the game's sectors whatever its size (objects past 16.35 km then share one sector, past 22.1 km some of them collide with the game's portal file, and past 32.7 km the game saves each zone's spawner at 20 km); a world already made or saved with wide sectors stays wide. Read by the machine that runs the world (single player, the host or a dedicated server), when a world is made and when it loads; a joining player's game follows the world's own settings. A world of 16,350 m or less is never touched", SettingScope.Live, WideSectorsMode.Auto, e => ConfigWideSectors = e);

  // ---- 08 AltBiomes (read into AltBiomeSettings by AltBiomeSettings.FromConfig) --------------------------------------
  public static readonly SettingDef<string> AltBiomemapFile = S("Altbiomemap File", "Path to an alt-biome map (altbiomemap.png). It plants Valheim 1.0 alt biomes with colours, the way the biome map plants biomes; the legend beside it (altbiomemap.txt, written with a default colour per alt biome when missing) says which colour plants what. Black and transparent pixels are left to the game.", SettingScope.World, "", e => ConfigAltBiomeFile = e);
  public static readonly SettingDef<string> AltBiomeMode = new("Mode", "Random = the game's random alt-biome placement on unplanted land (tuned by the values below) plus every planted region; PlantedOnly = only planted regions; Off = no alt biomes at all, planted ones included", SettingScope.World, "Random", e => ConfigAltBiomeMode = e)
  { Range = new AcceptableValueList<string>("Random", "PlantedOnly", "Off") };
  public static readonly SettingDef<string> AltBiomeGrid = new("Grid", "WorldEdge = alt biomes and biome-based location candidates stop at the edge of the world (World Size + Edge Size) when it is smaller than vanilla's 10500 m; Vanilla = always sample the vanilla 10500 m disc. No effect on a world laid out to its World Size (made since 0.10 without Expand World Size), whose grid covers the world.", SettingScope.World, "WorldEdge", e => ConfigAltBiomeGrid = e)
  { Range = new AcceptableValueList<string>("WorldEdge", "Vanilla") };
  public static readonly SettingDef<string> AltBiomeSeed = S("Fixed Seed", "Seed for the game's random alt-biome placement. Empty = the world seed (vanilla). A number, or any text, gives the same random alt-biome layout for this map whatever the world seed is", SettingScope.World, "", e => ConfigAltBiomeSeed = e);
  public static readonly SettingDef<float> AltBiomeChance = new("Chance Multiplier", "Multiplies every alt biome's random placement chance (vanilla 0.2, Dark Meadows 0.5)", SettingScope.World, 1f, e => ConfigAltBiomeChanceMultiplier = e) { Range = R(0f, 10f) };
  public static readonly SettingDef<float> AltBiomeAmount = new("Amount Multiplier", "Multiplies every alt biome's minimum and maximum number of regions", SettingScope.World, 1f, e => ConfigAltBiomeAmountMultiplier = e) { Range = R(0f, 10f) };
  public static readonly SettingDef<float> AltBiomeEdgeScale = new("Region Size Scale", "Multiplies every alt biome's region size window (edge length). Vanilla only gives alt biomes to regions roughly 0.1-2.4 km across; hand-drawn maps with big regions usually need 2-10", SettingScope.World, 1f, e => ConfigAltBiomeEdgeScale = e) { Range = R(0.1f, 50f) };
  public static readonly SettingDef<float> AltBiomeDistanceScale = new("Distance Scale", "Multiplies every alt biome's minimum distance from the world centre (vanilla 500-2000 m) and its world bounds", SettingScope.World, 1f, e => ConfigAltBiomeDistanceScale = e) { Range = R(0f, 10f) };
  public static readonly SettingDef<float> AltBiomeMinThickness = new("Min Sector Thickness", "Regions thinner than this (area / edge length, in 12 m cells) never get a random alt biome. 0 = vanilla. 2 filters the slivers an anti-aliased biome map leaves along its borders", SettingScope.World, 0f, e => ConfigAltBiomeMinThickness = e) { Range = R(0f, 20f) };
  public static readonly SettingDef<bool> AltBiomeMeanHeight = S("Mean Sector Height", "Measure a region's average height as the mean over the whole region instead of vanilla's (lowest + highest) / 2 of its border. Helps maps whose biome paint runs out into the sea: the border is then under water and vanilla's measure sinks below the 30 m every alt biome requires", SettingScope.World, false, e => ConfigAltBiomeMeanHeight = e);
  public static readonly SettingDef<bool> AltBiomeFixNeighbourCheck = S("Fix Neighbour Check", "Use a corrected version of vanilla's require/not-neighbour test (broken in vanilla; no vanilla alt biome uses it, modded ones may)", SettingScope.World, false, e => ConfigAltBiomeFixNeighbourCheck = e);
  public static readonly SettingDef<string> AltBiomeOverrides = S("Overrides", "Per alt biome overrides of the game's random placement: 'Name: key=value, key=value; Other Name: key=value'. A name may contain * wildcards ('*Mistlands'); '*' alone applies to all. An exact name wins over a wildcard pattern, and a pattern over '*', field by field. Keys: enabled, chance, min, max, mindist, minedge, maxedge, minheight, maxheight, ignorebounds. 'Fortress Mountain: enabled=false' keeps the game from placing Fortress Mountain at random; planted Fortress Mountain still works", SettingScope.World, "", e => ConfigAltBiomeOverrides = e);

  // ---- 09 Export (LiveConfig) -----------------------------------------------------------------------------------------
  public static readonly SettingDef<bool> ExportHud = S("Hud", "Shows the world export HUD in game: a status box (Hud Hotkey) and a window (Window Hotkey) with an Export tab (the maps, their options, Start and Cancel) and an Import tab (your exports, made into New World presets). Applies at once. On a client connected to a server that runs Better Continents 0.9.0 or later, the server's value is used instead", SettingScope.Live, false, e => ConfigExportHud = e);
  public static readonly SettingDef<bool> ExportAllowed = S("Allow Export", "Whether players connected to this server may export the world (bc_export, and Start in the export HUD). The machine that runs the world always may: single player, the host, and a dedicated server's own console (where an admin's 'bc_export server' runs). Applies at once, also to connected players. On a client, the server's value is used instead of this one", SettingScope.Live, true, e => ConfigExportAllowed = e);
  public static readonly SettingDef<KeyCode> ExportHudKey = S("Hud Hotkey", "Shows or hides the export HUD's status box (with no Shift, Ctrl or Alt held). None = no key", SettingScope.Live, KeyCode.F9, e => ConfigExportHudKey = e);
  public static readonly SettingDef<KeyCode> ExportWindowKey = S("Window Hotkey", "Opens or closes the export and import window, which frees the mouse while it is open (with no Shift, Ctrl or Alt held). None = no key", SettingScope.Live, KeyCode.F7, e => ConfigExportWindowKey = e);
  public static readonly SettingDef<int> ExportSize = new("Default Size", "Pixels per side of an export when bc_export is given no size, and the export window's first choice. 2048 and up keep the alt-biome map exact; 16384 is the largest, and every player joining a world built from the maps downloads them", SettingScope.Live, 4096, e => ConfigExportSize = e)
  { Range = R(WorldExport.MinSize, WorldExport.MaxSize) };
  public static readonly SettingDef<float> ExportHeightmapAmount = new("Default Heightmap Amount", "The Heightmap Amount an export encodes its heights for, unless bc_export or the window says otherwise; export.cfg records it, so the import always reads the heights at the amount they were written for. 1 is Better Continents' default everywhere: with Sea Level 0.5 it spans -30 m to 170 m and clips higher ground (the export says how many pixels); 2 spans -30 m to 370 m and keeps every vanilla mountain; the largest, 81, spans -30 m to 16,170 m in steps of a quarter of a metre", SettingScope.Live, 1f, e => ConfigExportHeightmapAmount = e)
  { Range = R(0.01f, MaxHeightmapAmount) };
  public static readonly SettingDef<float> ExportSeaLevel = new("Default Sea Level", "The Sea Level Adjustment an export encodes its heights for, unless bc_export or the window says otherwise", SettingScope.Live, 0.5f, e => ConfigExportSeaLevel = e)
  { Range = R(0f, 1f) };
  public static readonly SettingDef<int> ExportLargestSize = new("Largest Size", "The largest export, in pixels a side, that this machine makes: the export window offers sizes up to it, bc_export refuses bigger ones, and a larger Default Size is used as this. 4096, 8192 or 16384. A 16384 px export takes a dedicated server about three minutes, and every player joining a world built from it downloads its maps", SettingScope.Live, WorldExport.MaxSize, e => ConfigExportLargestSize = e)
  { Range = new AcceptableValueList<int>(4096, 8192, 16384) };

  /// <summary>BetterContinents.cfg, section by section, in order.</summary>
  public static readonly SettingGroup[] Groups =
  [
    new("BetterContinents.Debug", Enabled, DebugMode, DebugResetCommand, OverrideVersion, Directory),
    new("BetterContinents.Global", SkipDefaultLocations, ContinentSize, WorldSize, EdgeSize, SeaLevel, AshlandsGap, DeepNorthGap, Rivers, MapEdgeDropoff, MountainsAllowedAtCenter),
    new("BetterContinents.Heightmap", HeightmapFile, HeightmapAmount, HeightmapBlend, HeightmapAdd, HeightmapMask, HeightmapOverrideAll, HeightmapAlpha, RoughmapFile, RoughmapBlend, HighTerrain),
    new("BetterContinents.Biomemap", BiomemapFile, BiomePrecision, TerrainmapFile),
    new("BetterContinents.Forest", ForestScale, ForestAmount, ForestFactorOverrideAllTrees, ForestmapFile, ForestmapMultiply, ForestmapAdd),
    new("BetterContinents.StartPosition", OverrideStartPosition, StartPositionX, StartPositionY),
    new("BetterContinents.Maps", LocationmapFile, SpawnmapFile, VegetationmapFile, PaintmapFile, LavamapFile, MossmapFile, HeatmapFile, HeatmapScale),
    new("BetterContinents.Misc", NexusId, SelectedPreset, SettingsTransferRate, CompactMaps, MapMemory, ConfigVersion, MaxMapSize, WideSectors),
    // Every value of these two is still read at the same moments as before: AltBiomes when a world is created, Export live.
    new("BetterContinents.AltBiomes", AltBiomemapFile, AltBiomeMode, AltBiomeGrid, AltBiomeSeed, AltBiomeChance, AltBiomeAmount, AltBiomeEdgeScale, AltBiomeDistanceScale, AltBiomeMinThickness, AltBiomeMeanHeight, AltBiomeFixNeighbourCheck, AltBiomeOverrides),
    new("BetterContinents.Export", ExportHud, ExportAllowed, ExportHudKey, ExportWindowKey, ExportSize, ExportHeightmapAmount, ExportSeaLevel, ExportLargestSize),
  ];

  public static IEnumerable<SettingDef> All => Groups.SelectMany(g => g.Settings);

  static SettingsSchema()
  {
    for (int i = 0; i < Groups.Length; i++)
      foreach (var setting in Groups[i].Settings)
        setting.Section = GroupBuilder.SectionName(i, Groups[i].Name);
  }

  /// <summary>The world settings a new world copies from the config one by one, in config order.</summary>
  public static IEnumerable<SettingDef> Scalars => All.Where(d => d.ReadsIntoSettings);

  /// <summary>The "bc &lt;group&gt;" console values of a group, in config order.</summary>
  public static IEnumerable<SettingDef> Console(string group) => All.Where(d => d.ConsoleGroup == group && d.ConsoleName != null);

  /// <summary>Binds every setting into <paramref name="config"/>, section by section (Awake, and the offline suites).</summary>
  public static void Bind(ConfigFile config)
  {
    // Before the first value is read: Compact Maps' old values, true and false.
    CompactMapsConverter.Install();
    var builder = config.Declare();
    foreach (var group in Groups)
      builder.AddGroup(group.Name, g =>
      {
        foreach (var setting in group.Settings)
          setting.Bind(g);
      });
  }

  // ---- one-time changes to a player's file ----------------------------------------------------------------------------

  public const int CurrentConfigVersion = 3;

  /// <summary>Awake, after <see cref="Bind"/> and before anything reads the values: the changes a file written by an older
  /// Better Continents needs, each made once (Config Version records how far a file has come).</summary>
  internal static void Migrate()
  {
    var version = ConfigVersion.Entry.Value;
    if (version >= CurrentConfigVersion)
      return;
    // 1, the unifying refactor: the export's Default Heightmap Amount follows Better Continents' default of 1 everywhere.
    // 0.9.0 to 0.9.4 shipped it as 2, and every file they wrote holds that; a 2 chosen on purpose cannot be told apart.
    if (version < 1 && ExportHeightmapAmount.Entry.Value == 2f)
    {
      ExportHeightmapAmount.Entry.Value = 1f;
      Log("BetterContinents.cfg: [09 BetterContinents.Export] Default Heightmap Amount was 2 (the default of Better Continents 0.9.0 to 0.9.4) and is now 1, "
          + "Better Continents' default everywhere. Set it back to 2 to export at the old encoding (-30 m to 370 m).");
    }
    // 2, zone regeneration: Debug Reset Command is empty by default, which leaves the regeneration to Better Continents
    // (ZoneRegen). 0.9.x shipped "zones_reset start", a console command of another mod, and every file they wrote holds
    // it; only that exact text is the old default, anything else was chosen.
    if (version < 2 && DebugResetCommand.Entry.Value == "zones_reset start")
    {
      DebugResetCommand.Entry.Value = "";
      Log("BetterContinents.cfg: [00 BetterContinents.Debug] Debug Reset Command was \"zones_reset start\" (the default of Better Continents 0.9) and is now empty: "
          + "Better Continents regenerates the zones itself. Write \"zones_reset start\" there again to run that console command instead.");
    }
    // 3, the forest: Forest Scale is 0.5 by default, the game's own size of forest and clearing patches. 0.7.20 to 0.10.1
    // shipped 1, which the curve makes about five times that size, and every file they wrote holds it; a 1 chosen on
    // purpose cannot be told apart. Only a new world reads it: a world keeps the scale it was made with.
    if (version < 3 && ForestScale.Entry.Value == 1f)
    {
      ForestScale.Entry.Value = 0.5f;
      Log("BetterContinents.cfg: [04 BetterContinents.Forest] Forest Scale was 1 (the default of Better Continents 0.7.20 to 0.10.1: forest and clearing patches "
          + "about five times the game's size) and is now 0.5, the game's own size. Set it back to 1 for the larger patches. Worlds already made keep their forests.");
    }
    ConfigVersion.Entry.Value = CurrentConfigVersion;
  }

  /// <summary>The setting bound to <paramref name="entry"/>, if it is one of Better Continents'.</summary>
  public static SettingDef? Find(ConfigEntryBase entry) => All.FirstOrDefault(d => d.EntryBase == entry);
}

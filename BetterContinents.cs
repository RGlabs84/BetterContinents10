// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BetterContinents;

[BepInPlugin("BetterContinents", ModInfo.Name, ModInfo.Version)]
public partial class BetterContinents : BaseUnityPlugin
{
#nullable disable
    public static Harmony HarmonyInstance;
    // See the Awake function for the config descriptions
    public static ConfigEntry<int> NexusID;
    public static ConfigEntry<string> ConfigSelectedPreset;
    public static ConfigEntry<TransferRatePreset> ConfigSettingsTransferRate;

    public static ConfigEntry<bool> ConfigEnabled;

    public static ConfigEntry<float> ConfigContinentSize;
    public static ConfigEntry<float> ConfigSeaLevelAdjustment;
    public static ConfigEntry<bool> ConfigOceanChannelsEnabled;
    public static ConfigEntry<bool> ConfigAshlandsGapEnabled;
    public static ConfigEntry<float> ConfigWorldSize;
    public static ConfigEntry<float> ConfigEdgeSize;
    public static ConfigEntry<bool> ConfigFixWaterColor;

    public static ConfigEntry<bool> ConfigDeepNorthGapEnabled;

    public static ConfigEntry<bool> ConfigRiversEnabled;
    public static ConfigEntry<bool> ConfigMapEdgeDropoff;
    public static ConfigEntry<bool> ConfigMountainsAllowedAtCenter;

    public static ConfigEntry<string> ConfigMapSourceDir;

    public static ConfigEntry<string> ConfigHeightFile;
    public static ConfigEntry<float> ConfigHeightmapAmount;
    public static ConfigEntry<float> ConfigHeightmapBlend;
    public static ConfigEntry<float> ConfigHeightmapAdd;
    public static ConfigEntry<float> ConfigHeightmapMask;
    public static ConfigEntry<bool> ConfigHeightmapOverrideAll;
    public static ConfigEntry<bool> ConfigHeightmapAlpha;

    public static ConfigEntry<int> ConfigBiomePrecision;
    public static ConfigEntry<string> ConfigBiomeFile;

    public static ConfigEntry<string> ConfigLocationFile;

    public static ConfigEntry<string> ConfigSpawnFile;

    public static ConfigEntry<string> ConfigTerrainFile;
    public static ConfigEntry<string> ConfigPaintFile;
    public static ConfigEntry<string> ConfigLavaFile;
    public static ConfigEntry<string> ConfigMossFile;
    public static ConfigEntry<string> ConfigVegetationFile;


    public static ConfigEntry<string> ConfigRoughFile;
    public static ConfigEntry<float> ConfigRoughmapBlend;

    public static ConfigEntry<float> ConfigForestScale;
    public static ConfigEntry<float> ConfigForestAmount;
    public static ConfigEntry<bool> ConfigForestFactorOverrideAllTrees;
    public static ConfigEntry<string> ConfigForestFile;
    public static ConfigEntry<float> ConfigForestmapMultiply;
    public static ConfigEntry<float> ConfigForestmapAdd;

    public static ConfigEntry<bool> ConfigOverrideStartPosition;
    public static ConfigEntry<float> ConfigStartPositionX;
    public static ConfigEntry<float> ConfigStartPositionY;

    public static ConfigEntry<bool> ConfigDebugModeEnabled;
    public static ConfigEntry<bool> ConfigSkipDefaultLocations;
    public static ConfigEntry<string> ConfigDebugResetCommand;
    public static ConfigEntry<string> ConfigOverrideVersion;

    public static ConfigEntry<float> ConfigHeatScale;
    public static ConfigEntry<string> ConfigHeatFile;

    // Alt biomes (new-world defaults; baked into the world's settings when it is created, see
    // AltBiomeSettings.FromConfig and ALTBIOMES.md)
    public static ConfigEntry<string> ConfigAltBiomeFile;
    public static ConfigEntry<string> ConfigAltBiomeMode;
    public static ConfigEntry<string> ConfigAltBiomeGrid;
    public static ConfigEntry<string> ConfigAltBiomeSeed;
    public static ConfigEntry<float> ConfigAltBiomeChanceMultiplier;
    public static ConfigEntry<float> ConfigAltBiomeAmountMultiplier;
    public static ConfigEntry<float> ConfigAltBiomeEdgeScale;
    public static ConfigEntry<float> ConfigAltBiomeDistanceScale;
    public static ConfigEntry<float> ConfigAltBiomeMinThickness;
    public static ConfigEntry<bool> ConfigAltBiomeMeanHeight;
    public static ConfigEntry<bool> ConfigAltBiomeFixNeighbourCheck;
    public static ConfigEntry<string> ConfigAltBiomeOverrides;

    // World export (0.9.0). Unlike every group above, these apply while the game runs (LiveConfig), and Hud and Allow
    // Export follow the server on its clients.
    public static ConfigEntry<bool> ConfigExportHud;
    public static ConfigEntry<bool> ConfigExportAllowed;
    public static ConfigEntry<KeyCode> ConfigExportHudKey;
    public static ConfigEntry<KeyCode> ConfigExportWindowKey;
    public static ConfigEntry<int> ConfigExportSize;
    public static ConfigEntry<float> ConfigExportHeightmapAmount;
    public static ConfigEntry<float> ConfigExportSeaLevel;

    public static BetterContinents instance;
#nullable enable
    public static void SetSize(float size, float edge)
    {
        Log($"Received world size {size} and edge size {edge}");
        TotalRadius = size + edge;
        TotalSize = TotalRadius * 2f;
        WorldRadius = size;
        WorldGeneratorPatch.ApplyNoiseSettings();
    }
    public static float TotalRadius = 10500f;
    public static float TotalSize = TotalRadius * 2f;
    public static float WorldRadius = 10000f;
    public const string ConfigFileExtension = ".BetterContinents";
    public static string GetBCFile(string path) => Path.ChangeExtension(path, ConfigFileExtension);
    public static string GetLegacyBCFile(string path) => Path.ChangeExtension(path, ".fwl" + ConfigFileExtension);

    // Valheim 1.0 stores every world as a directory - worlds_local/<name>/ holding _main.N.fwl2, .db2,
    // .chunks, .ok plus the chunk files - and that directory is the unit the save system works on:
    // SaveSystem.Delete, Copy and RenameDirectory all act on SaveFile.ChunkedDirectory. Writing our
    // settings inside it means they travel with the world through every copy, move, rename, backup and
    // delete with no save-system patching at all.
    //
    // The name is deliberately extension-less, matching the game's own cacheMinimapBiome / cacheMinimapHeight
    // / cacheMinimapMask / cacheMinimapMeta sitting in the same folder. SaveCollection.Reload scans every
    // file under worlds_local recursively, and SaveSystem.GetSaveInfo returns false as soon as a file has no
    // extension, so an extension-less file is simply invisible to the scanner.
    //
    // Two tempting alternatives are actively destructive and must not be used:
    //   - a name starting with "_main." joins the rolling-save group, and SaveCollection.KeepOnlyNewest
    //     deletes any such group that is not exactly four files. A fifth file makes it delete the world.
    //   - "<worldname>.BetterContinents" is seen as a second save sharing the world's name, and
    //     SaveWithBackups.EnsureSortedAndPrimaryFileDetermined resolves that by moving the older of the two
    //     aside - which renames the real world folder to <name>_backup_<timestamp> and makes the world
    //     vanish from the world list.
    public const string ConfigFileName = "BetterContinents";
    public static string GetWorldBCFile(string worldName, FileHelpers.FileSource fileSource) =>
      SaveSystem.GetWorldsSaveRootPath(fileSource) + "/" + worldName + "/" + ConfigFileName;
    private static readonly Vector2 Half = Vector2.one * 0.5f;
    private static float Normalize(float x) => Mathf.Clamp(x / TotalSize + 0.5f, 0f, 1f);
    private static Vector2 NormalizedToWorld(Vector2 p) => (p - Half) * TotalSize;

    public static void Log(string msg) => Debug.Log($"[BetterContinents] {msg}");
    public static void LogError(string msg) => Debug.LogError($"[BetterContinents] {msg}");
    public static void LogWarning(string msg) => Debug.LogWarning($"[BetterContinents] {msg}");

    public static bool AllowDebugActions => ZNet.instance
                                            && ZNet.instance.IsServer()
                                            && Settings.EnabledForThisWorld
                                            && ConfigDebugModeEnabled.Value;

    public static BetterContinentsSettings Settings = new();

    // Unity's main thread, recorded in Awake. World import builds settings on a worker, where UnityEngine.Random and
    // prefab lookups are off limits (ImageMapLocation, SpawnEntry).
    private static int mainThreadId;

    /// <summary>True on Unity's main thread; false on a worker, and in the offline harness (no Awake).</summary>
    internal static bool IsMainThread => mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId;


    public void Awake()
    {
        instance = this;
        mainThreadId = Thread.CurrentThread.ManagedThreadId;

        // Cos why...
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        // Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

        Console.SetConsoleEnabled(true);

        DeclareConfig(Config);
        if (ConfigLocationFile.Value == "" && ConfigSpawnFile.Value != "")
        {
            ConfigLocationFile.Value = ConfigSpawnFile.Value.Replace("spawnmap", "locationmap");
            ConfigSpawnFile.Value = "";
            Config.Save();
        }
        LiveConfig.Init(Config);
        // A preset chosen outside the New World screen (bc_import, a config edit) shows there at once.
        Presets.WatchSelection();
        HarmonyInstance = new Harmony("BetterContinents.Harmony");
        HarmonyInstance.PatchAll();
        LogAltBiomePatches();
        // 0.9.0: a modpack (or a server admin) can ship .bcworld files so players already have a big world's
        // settings cached before they ever join (WorldCacheShare.cs; README "Sharing Maps With Players").
        try
        {
            WorldCacheShare.SeedFromDisk();
        }
        catch (Exception e)
        {
            LogWarning($"World cache seeding failed: {e.Message}");
        }
        Log("Awake");
        UI.Init();
    }

    // Binds every setting (Awake). Static so the offline harness binds exactly the sections, keys, defaults and ranges
    // the game does.
    internal static void DeclareConfig(ConfigFile config)
    {
        config.Declare()
            .AddGroup("BetterContinents.Debug", groupBuilder =>
            {
                groupBuilder.AddValue("Enabled")
                    .Description("Whether this mod is enabled")
                    .Default(true).Bind(out ConfigEnabled);
                groupBuilder.AddValue("Debug Mode")
                    .Description("Automatically reveals the full map on respawn, enables cheat mode, and debug mode, for debugging purposes").Bind(out ConfigDebugModeEnabled);
                groupBuilder.AddValue("Debug Reset Command")
                    .Description("Upgrade World command to execute when reloading images.").Default("zones_reset start").Bind(out ConfigDebugResetCommand);
                groupBuilder.AddValue("Override version")
                    .Description("Override the save version")
                    .Default("").Bind(out ConfigOverrideVersion);
                groupBuilder.AddValue("Directory")
                    .Description("This directory will load automatically any existing map files matching the correct names, overriding specific files specified below. Filenames must match: heightmap.png, biomemap.png, terrainmap.png, locationmap.png, roughmap.png, forestmap.png, heatmap.png, paintmap.png, lavamap.png, mossmap.png, vegetationmap.png, spawnmap.png, altbiomemap.png.")
                    .Default("").Bind(out ConfigMapSourceDir);
            })
            .AddGroup("BetterContinents.Global", groupBuilder =>
            {
                groupBuilder.AddValue("Skip Default Locations")
                  .Description("Skips the default location placement. Spawn temple and location map are still placed.").Bind(out ConfigSkipDefaultLocations);
                groupBuilder.AddValue("Continent Size")
                    .Description("Continent size")
                    .Default(0.5f).Range(0f, 1f).Bind(out ConfigContinentSize);
                groupBuilder.AddValue("World Size")
                    .Description("World radius in meter")
                    .Default(10000f).Bind(out ConfigWorldSize);
                groupBuilder.AddValue("Edge Size")
                    .Description("Edge size in meters")
                    .Default(500f).Bind(out ConfigEdgeSize);
                groupBuilder.AddValue("Fix Water Color")
                    .Description("Whether to fix the water color")
                    .Default(true).Bind(out ConfigFixWaterColor);
                groupBuilder.AddValue("Sea Level Adjustment")
                    .Description("Modify sea level, which changes the land:sea ratio")
                    .Default(0.5f).Range(0f, 1f).Bind(out ConfigSeaLevelAdjustment);
                groupBuilder.AddValue("Ocean Channels")
                    .Description("Whether ocean channels should be enabled or not (useful to disable when using height map for instance)")
                    .Default(true).Bind(out ConfigOceanChannelsEnabled);
                groupBuilder.AddValue("Ashlands Gap")
                    .Description("Whether to add the Ashlands ocean gap (usually custom maps don't need this)")
                    .Default(false).Bind(out ConfigAshlandsGapEnabled);
                groupBuilder.AddValue("Deep North Gap")
                    .Description("Whether to add the Deep North ocean gap (usually custom maps don't need this)")
                    .Default(false).Bind(out ConfigDeepNorthGapEnabled);
                groupBuilder.AddValue("Rivers")
                    .Description("Whether rivers should be enabled or not")
                    .Default(true).Bind(out ConfigRiversEnabled);
                groupBuilder.AddValue("Map Edge Drop-off")
                    .Description("Whether the map should drop off at the edges or not (consequences unknown!)")
                    .Default(true).Bind(out ConfigMapEdgeDropoff);
                groupBuilder.AddValue("Mountains Allowed At Center")
                    .Description("Whether the map should allow mountains to occur at the map center (if you have default spawn then you should keep this unchecked)")
                    .Default(false).Bind(out ConfigMountainsAllowedAtCenter);
            })
            .AddGroup("BetterContinents.Heightmap", groupBuilder =>
            {
                groupBuilder.AddValue("Heightmap File")
                    .Description("Path to a heightmap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigHeightFile);
                groupBuilder.AddValue("Heightmap Amount")
                    .Description("Multiplier of the height value from the heightmap file (more than 1 leads to higher max height than vanilla, good results are not guaranteed)")
                    .Default(1f).Range(0f, 5f).Bind(out ConfigHeightmapAmount);
                groupBuilder.AddValue("Heightmap Blend")
                    .Description("How strongly to blend the heightmap file into the final result")
                    .Default(1f).Range(0f, 1f).Bind(out ConfigHeightmapBlend);
                groupBuilder.AddValue("Heightmap Add")
                    .Description("How strongly to add the heightmap file to the final result (usually you want to blend it instead)")
                    .Default(0f).Range(-1f, 1f).Bind(out ConfigHeightmapAdd);
                groupBuilder.AddValue("Heightmap Mask")
                    .Description("How strongly to apply the heightmap as a mask on normal height generation (i.e. it limits maximum height to the height of the mask)")
                    .Default(0f).Range(0f, 1f).Bind(out ConfigHeightmapMask);
                groupBuilder.AddValue("Heightmap Override All")
                    .Description("All other aspects of the height calculation will be disabled, so the world will perfectly conform to your heightmap")
                    .Default(true).Bind(out ConfigHeightmapOverrideAll);
                groupBuilder.AddValue("Heightmap Alpha")
                    .Description("Enables alpha channel for the heightmap file to blend vanilla generation with the heightmap")
                    .Default(false).Bind(out ConfigHeightmapAlpha);
                groupBuilder.AddValue("Roughmap File")
                    .Description("Path to a roughmap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigRoughFile);
                groupBuilder.AddValue("Roughmap Blend")
                    .Description("How strongly to apply the roughmap file")
                    .Default(1f).Range(0f, 1f).Bind(out ConfigRoughmapBlend);
            })
            .AddGroup("BetterContinents.Biomemap", groupBuilder =>
            {
                groupBuilder.AddValue("Biomemap File")
                    .Description("Path to a biomemap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigBiomeFile);
                groupBuilder.AddValue("Biome precision")
                    .Description("How closely the ground follows the biome borders inside each 64 m terrain zone (ground textures, grass, vegetation and spawn points). 0 = vanilla: a zone takes its biomes from its 4 corners, so the borders follow the 64 m zone grid. 1 to 5 split every zone into (N + 1) x (N + 1) cells, with a corner every 32, 21, 16, 13 or 11 m. Works with or without a biomemap, and terrain heights do not change")
                    .Default(0).Range(0, 5).Bind(out ConfigBiomePrecision);
                groupBuilder.AddValue("Terrainmap file")
                    .Description("Path to a terrainmap file to use. See thea description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigTerrainFile);
            })
            .AddGroup("BetterContinents.Forest", groupBuilder =>
            {
                groupBuilder.AddValue("Forest Scale")
                    .Description("Scales forested/cleared area size")
                    .Default(1f).Range(0f, 10f).Bind(out ConfigForestScale);
                groupBuilder.AddValue("Forest Amount")
                    .Description("Adjusts how much forest there is, relative to clearings")
                    .Default(0.5f).Range(0f, 1f).Bind(out ConfigForestAmount);
                groupBuilder.AddValue("Forest Factor Overrides All Trees")
                    .Description("Trees in all biomes will be affected by forest factor (both procedural and from forestmap)")
                    .Default(false).Bind(out ConfigForestFactorOverrideAllTrees);
                groupBuilder.AddValue("Forestmap File")
                    .Description("Path to a forestmap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigForestFile);
                groupBuilder.AddValue("Forestmap Multiply")
                    .Description("How strongly to scale the vanilla forest factor by the forestmap")
                    .Default(1f).Range(0f, 1f).Bind(out ConfigForestmapMultiply);
                groupBuilder.AddValue("Forestmap Add")
                    .Description("How strongly to add the forestmap directly to the vanilla forest factor")
                    .Default(1f).Range(0f, 1f).Bind(out ConfigForestmapAdd);
            })
            .AddGroup("BetterContinents.StartPosition", groupBuilder =>
            {
                groupBuilder.AddValue("Override Start Position")
                    .Description("Whether to override the start position using the values provided (warning: will disable all validation of the position)")
                    .Default(false).Bind(out ConfigOverrideStartPosition);
                groupBuilder.AddValue("Start Position X")
                    .Description("Start position override X value, in ranges -10500 to 10500")
                    .Default(0f).Range(-10500f, 10500f).Bind(out ConfigStartPositionX);
                groupBuilder.AddValue("Start Position Y")
                    .Description("Start position override Y value, in ranges -10500 to 10500")
                    .Default(0f).Range(-10500f, 10500f).Bind(out ConfigStartPositionY);
            })
            .AddGroup("BetterContinents.Maps", groupBuilder =>
            {
                groupBuilder.AddValue("Locationmap File")
                    .Description("Path to a locationmap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                    .Default("").Bind(out ConfigLocationFile);
                groupBuilder.AddValue("Spawnmap File")
                .Description("Legay path to a locationmap file to use. See the description on Nexusmods.com for the specifications (it will fail if they are not met)")
                .Default("").Bind(out ConfigSpawnFile);
                groupBuilder.AddValue("Vegetationmap File")
                      .Description("Path to a vegetationmap file to use.")
                      .Default("").Bind(out ConfigVegetationFile);
                groupBuilder.AddValue("Paintmap File")
                .Description("Path to a paintmap file to use.")
                .Default("").Bind(out ConfigPaintFile);
                groupBuilder.AddValue("Lavamap File")
                   .Description("Path to a lavamap file to use.")
                   .Default("").Bind(out ConfigLavaFile);
                groupBuilder.AddValue("Mossmap File")
                 .Description("Path to a mossmap file to use.")
                 .Default("").Bind(out ConfigMossFile);
                groupBuilder.AddValue("Heatmap File")
                  .Description("Path to a heatmap file to use.")
                  .Default("").Bind(out ConfigHeatFile);
                groupBuilder.AddValue("Heatmap Scale")
                    .Description("Multiplies the heatmap color value. Most heat effects cap at 1 value.")
                    .Default(10f).Range(0f, 100f).Bind(out ConfigHeatScale);
            })
            .AddGroup("BetterContinents.Misc", groupBuilder =>
            {
                groupBuilder.AddValue("NexusID")
                    .Hidden().Default(446).Bind(out NexusID);
                groupBuilder.AddValue("SelectedPreset")
                    .Hidden().Default("Vanilla").Bind(out ConfigSelectedPreset);
                groupBuilder.AddValue("Settings Transfer Rate")
                    .Description("How fast this machine (the host, or a dedicated server) may push a joining player's Better Continents settings and images over their Steam connection. Valheim pins every connection to about 150 KB/s, so a large world can take over a minute to join; Vanilla leaves that rate untouched; KB256, KB384, KB512, KB768, MB1, MB1_5 and MB3 set that one connection to that fixed rate for the transfer only, then restore Valheim's own; Unlimited sets 100 MB/s (the transfer itself moves about 4 MB/s at most). Steam sends at exactly the rate set, with no congestion control, so pick one the server's upload can carry. The server's value is used; a client's own value has no effect. No effect on PlayFab (crossplay) connections, whose send rate cannot be changed.")
                    .Default(TransferRatePreset.KB512).Bind(out ConfigSettingsTransferRate);
            })
            // Must stay after Misc: section names carry the group's position ("07 BetterContinents.Misc"), so a group
            // inserted earlier would rename every later section and reset the values stored in them. Every value
            // here is a default for NEW worlds: it is baked into the world's settings when the world is created and
            // never read again for that world. Change a live world with the "bc ab" console commands (debug mode).
            .AddGroup("BetterContinents.AltBiomes", groupBuilder =>
            {
                groupBuilder.AddValue("Altbiomemap File")
                    .Description("Path to an alt-biome map (altbiomemap.png). It plants Valheim 1.0 alt biomes with colours, the way the biome map plants biomes; the legend beside it (altbiomemap.txt, written with a default colour per alt biome when missing) says which colour plants what. Black and transparent pixels are left to the game.")
                    .Default("").Bind(out ConfigAltBiomeFile);
                groupBuilder.AddValue("Mode")
                    .Description("Random = the game's random alt-biome placement on unplanted land (tuned by the values below) plus every planted region; PlantedOnly = only planted regions; Off = no alt biomes at all, planted ones included")
                    .Default("Random").Range(new AcceptableValueList<string>("Random", "PlantedOnly", "Off")).Bind(out ConfigAltBiomeMode);
                groupBuilder.AddValue("Grid")
                    .Description("WorldEdge = alt biomes and biome-based location candidates stop at the edge of the world (World Size + Edge Size) when it is smaller than vanilla's 10500 m; Vanilla = always sample the vanilla 10500 m disc")
                    .Default("WorldEdge").Range(new AcceptableValueList<string>("WorldEdge", "Vanilla")).Bind(out ConfigAltBiomeGrid);
                groupBuilder.AddValue("Fixed Seed")
                    .Description("Seed for the game's random alt-biome placement. Empty = the world seed (vanilla). A number, or any text, gives the same random alt-biome layout for this map whatever the world seed is")
                    .Default("").Bind(out ConfigAltBiomeSeed);
                groupBuilder.AddValue("Chance Multiplier")
                    .Description("Multiplies every alt biome's random placement chance (vanilla 0.2, Dark Meadows 0.5)")
                    .Default(1f).Range(0f, 10f).Bind(out ConfigAltBiomeChanceMultiplier);
                groupBuilder.AddValue("Amount Multiplier")
                    .Description("Multiplies every alt biome's minimum and maximum number of regions")
                    .Default(1f).Range(0f, 10f).Bind(out ConfigAltBiomeAmountMultiplier);
                groupBuilder.AddValue("Region Size Scale")
                    .Description("Multiplies every alt biome's region size window (edge length). Vanilla only gives alt biomes to regions roughly 0.1-2.4 km across; hand-drawn maps with big regions usually need 2-10")
                    .Default(1f).Range(0.1f, 50f).Bind(out ConfigAltBiomeEdgeScale);
                groupBuilder.AddValue("Distance Scale")
                    .Description("Multiplies every alt biome's minimum distance from the world centre (vanilla 500-2000 m) and its world bounds")
                    .Default(1f).Range(0f, 10f).Bind(out ConfigAltBiomeDistanceScale);
                groupBuilder.AddValue("Min Sector Thickness")
                    .Description("Regions thinner than this (area / edge length, in 12 m cells) never get a random alt biome. 0 = vanilla. 2 filters the slivers an anti-aliased biome map leaves along its borders")
                    .Default(0f).Range(0f, 20f).Bind(out ConfigAltBiomeMinThickness);
                groupBuilder.AddValue("Mean Sector Height")
                    .Description("Measure a region's average height as the mean over the whole region instead of vanilla's (lowest + highest) / 2 of its border. Helps maps whose biome paint runs out into the sea: the border is then under water and vanilla's measure sinks below the 30 m every alt biome requires")
                    .Default(false).Bind(out ConfigAltBiomeMeanHeight);
                groupBuilder.AddValue("Fix Neighbour Check")
                    .Description("Use a corrected version of vanilla's require/not-neighbour test (broken in 1.0.15; no vanilla alt biome uses it, modded ones may)")
                    .Default(false).Bind(out ConfigAltBiomeFixNeighbourCheck);
                groupBuilder.AddValue("Overrides")
                    .Description("Per alt biome overrides of the game's random placement: 'Name: key=value, key=value; Other Name: key=value'. A name may contain * wildcards ('*Mistlands'); '*' alone applies to all. An exact name wins over a wildcard pattern, and a pattern over '*', field by field. Keys: enabled, chance, min, max, mindist, minedge, maxedge, minheight, maxheight, ignorebounds. 'Fortress Mountain: enabled=false' keeps the game from placing Fortress Mountain at random; planted Fortress Mountain still works")
                    .Default("").Bind(out ConfigAltBiomeOverrides);
            })
            // Must stay last, like AltBiomes above ("09 BetterContinents.Export"). Unlike every group before it, these
            // values apply while the game runs: see LiveConfig, which also re-reads this file when it changes on disk.
            .AddGroup("BetterContinents.Export", groupBuilder =>
            {
                groupBuilder.AddValue("Hud")
                    .Description("Shows the world export HUD in game: a status box (Hud Hotkey) and a window (Window Hotkey) with an Export tab (the maps, their options, Start and Cancel) and an Import tab (your exports, made into New World presets). Applies at once. On a client connected to a server that runs Better Continents 0.9.0 or later, the server's value is used instead")
                    .Default(false).Bind(out ConfigExportHud);
                groupBuilder.AddValue("Allow Export")
                    .Description("Whether players connected to this server may export the world (bc_export, and Start in the export HUD). The machine that runs the world always may: single player, the host, and a dedicated server's own console (where an admin's 'bc_export server' runs). Applies at once, also to connected players. On a client, the server's value is used instead of this one")
                    .Default(true).Bind(out ConfigExportAllowed);
                groupBuilder.AddValue("Hud Hotkey")
                    .Description("Shows or hides the export HUD's status box (with no Shift, Ctrl or Alt held). None = no key")
                    .Default(KeyCode.F9).Bind(out ConfigExportHudKey);
                groupBuilder.AddValue("Window Hotkey")
                    .Description("Opens or closes the export and import window, which frees the mouse while it is open (with no Shift, Ctrl or Alt held). None = no key")
                    .Default(KeyCode.F7).Bind(out ConfigExportWindowKey);
                groupBuilder.AddValue("Default Size")
                    .Description("Pixels per side of an export when bc_export is given no size, and the export window's first choice. 2048 and up keep the alt-biome map exact; 8192 holds about half a gigabyte while it runs")
                    .Default(4096).Range(WorldExport.MinSize, WorldExport.MaxSize).Bind(out ConfigExportSize);
                groupBuilder.AddValue("Default Heightmap Amount")
                    .Description("The Heightmap Amount an export encodes its heights for, unless bc_export or the window says otherwise. 2 with Sea Level 0.5 spans -30 m to 370 m with the waterline at 0.15, the encoding hand-made and generated maps use")
                    .Default(2f).Range(0.01f, 5f).Bind(out ConfigExportHeightmapAmount);
                groupBuilder.AddValue("Default Sea Level")
                    .Description("The Sea Level Adjustment an export encodes its heights for, unless bc_export or the window says otherwise")
                    .Default(0.5f).Range(0f, 1f).Bind(out ConfigExportSeaLevel);
            });
    }

    public void Start()
    {
        EWD.Run();
    }

    public void Update()
    {
        // Reloads BetterContinents.cfg on the main thread once a change on disk has settled.
        LiveConfig.Update();
    }

    // One line at startup that says whether the 0.8.1 world-generation fixes are bound, so a server log shows it
    // without anyone having to load a world first.
    private static void LogAltBiomePatches()
    {
        try
        {
            static int Count(System.Reflection.MethodBase? method)
            {
                if (method == null)
                    return -1;
                var info = Harmony.GetPatchInfo(method);
                if (info == null)
                    return 0;
                return info.Prefixes.Count(p => p.owner == HarmonyInstance.Id) + info.Postfixes.Count(p => p.owner == HarmonyInstance.Id)
                       + info.Transpilers.Count(p => p.owner == HarmonyInstance.Id) + info.Finalizers.Count(p => p.owner == HarmonyInstance.Id);
            }
            var grid = Count(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), [typeof(int), typeof(int), typeof(bool)]));
            var weather = Count(AccessTools.Method(typeof(EnvMan), "UpdateEnvironment")) + Count(AccessTools.Method(typeof(EnvMan), "GetBiome"));
            var placement = Count(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateAltBiomes)));
            var cache = Count(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.TryLoadCache)))
                        + Count(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.SaveCache)));
            Log($"Alt biomes: patches bound - grid clamp for resized grids (GetBiomeSector) {grid}, Deep North weather (EnvMan) {weather} with {DeepNorthWeather.RewrittenCalls} IsDeepnorth call(s) rewritten, placement (GenerateAltBiomes) {placement}, biome cache fingerprint {cache}.");
        }
        catch (Exception e)
        {
            LogWarning($"Alt biomes: could not list the bound patches: {e.Message}");
        }
    }

    public void OnGUI()
    {
        CommandWrapper.Init();
        UI.OnGUI();
    }

    // Debug mode helpers
    [HarmonyPatch(typeof(Minimap))]
    private class MinimapPatch
    {
        // Minimap.GetMaskColor is deliberately NOT patched any more.
        //
        // The mask texture is not a simple "is there forest here" flag: 1.0 packs a different
        // meaning into each channel per biome. Red is the forest overlay (Meadows / Plains /
        // BlackForest), green carries the Mistlands mist density, blue carries the Ashlands
        // ocean gradient below the waterline and GetAshlandsHeight's mask.a above it, and
        // Swamp / Mountain / Deep North deliberately get no mask at all. Vanilla also returns
        // the ocean gradient for every pixel under 30 m before it ever looks at the biome.
        //
        // Better Continents already patches WorldGenerator.GetForestFactor, and vanilla's
        // InForest is just GetForestFactor(pos) < 1.15f, so vanilla's own GetMaskColor reads
        // Better Continents' forest values and paints the correct channel for free. The old
        // prefix collapsed six biomes onto the red forest channel, which drew Swamp, Mountain
        // and Mistlands as Black Forest and threw away the Ashlands and underwater gradients.

        // Some map mods may do stuff after generation which won't work with async.
        // So do one "fake" generate call to trigger those.
        static bool DoFakeGenerate = false;
        [HarmonyPrefix, HarmonyPatch(nameof(Minimap.GenerateWorldMap))]
        private static bool GenerateWorldMapPrefix(Minimap __instance)
        {
            if (DoFakeGenerate)
            {
                DoFakeGenerate = false;
                return false;
            }
            __instance.StartCoroutine(GenerateWorldMapMT(__instance));
            return false;
        }

        private static IEnumerator GenerateWorldMapMT(Minimap map)
        {
            Log($"Generating minimap textures multi-threaded ...");
            // The vanilla GenerateWorldMap is skipped, so the cache has to be invalidated here instead.
            // Otherwise an interrupted generation leaves a cache that still passes the seed and version checks.
            Minimap.DeleteMapTextureData(ZNet.World.m_name);
            int halfSize = map.m_textureSize / 2;
            float halfSizeF = map.m_pixelSize / 2f;
            var mapPixels = new Color32[map.m_textureSize * map.m_textureSize];
            var forestPixels = new Color32[map.m_textureSize * map.m_textureSize];
            var heightPixels = new Color[map.m_textureSize * map.m_textureSize];
            var cachedHeights = new float[map.m_textureSize * map.m_textureSize];
            int progress = 0;
            var task = Task.Run(() =>
            {
                GameUtils.SimpleParallelFor(4, 0, map.m_textureSize, i =>
                {
                    for (int j = 0; j < map.m_textureSize; j++)
                    {
                        float wx = (j - halfSize) * map.m_pixelSize + halfSizeF;
                        float wy = (i - halfSize) * map.m_pixelSize + halfSizeF;
                        var biome = WorldGenerator.instance.GetBiome(wx, wy);
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy, out _);
                        mapPixels[i * map.m_textureSize + j] = map.GetPixelColor(biome);
                        forestPixels[i * map.m_textureSize + j] = map.GetMaskColor(wx, wy, biomeHeight, biome);
                        // Alpha 0, not the 1 the three-argument Color constructor gives: vanilla
                        // fills this array by assigning .r onto a default Color, so its alpha is 0.
                        heightPixels[i * map.m_textureSize + j] = new Color(biomeHeight, 0f, 0f, 0f);
                        cachedHeights[i * map.m_textureSize + j] = biomeHeight;
                    }
                    // Updated every row, because every pixel is pointless for a percentage.
                    Interlocked.Increment(ref progress);
                });
            });

            try
            {
                UI.Add("GeneratingMinimap", () =>
                {
                    int percentProgress = (int)(100 * ((float)progress / map.m_textureSize));
                    UI.DisplayMessage($"Better Continents: generating minimap {percentProgress}% ...");
                });
                yield return new WaitUntil(() => task.IsCompleted);
            }
            finally
            {
                UI.Remove("GeneratingMinimap");
            }

            map.m_forestMaskTexture.SetPixels32(forestPixels);
            map.m_forestMaskTexture.Apply();
            map.m_mapTexture.SetPixels32(mapPixels);
            map.m_mapTexture.Apply();
            map.m_heightTexture.SetPixels(heightPixels);
            map.m_heightTexture.Apply();

            Log($"Finished generating minimap textures multi-threaded ...");
            if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported)
            {
                // This also writes the meta file with the world seed and the cache version.
                map.SaveMapTextureDataToDisk(forestPixels, mapPixels, cachedHeights);
            }
            // Some map mods may do stuff after generation which won't work with async.
            // So do one "fake" generate call to trigger those.
            DoFakeGenerate = true;
            map.GenerateWorldMap();
        }
    }

    // Cache might have wrong map size so has to be fully reimplemented.
    // This could be transpiled too but more complex.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.TryLoadMinimapTextureData))]
    public class PatchTryLoadMinimapTextureData
    {
        static bool Prefix(Minimap __instance, int worldSeed, ref bool __result)
        {
            __result = TryLoadMinimapTextureData(__instance, worldSeed);
            return false;
        }

        private static bool TryLoadMinimapTextureData(Minimap obj, int worldSeed)
        {
            if (string.IsNullOrEmpty(obj.m_cachedMinimapMaskTexturePath)
                || !File.Exists(obj.m_cachedMinimapMaskTexturePath)
                || !File.Exists(obj.m_cachedMinimapBiomeTexturePath)
                || !File.Exists(obj.m_cachedMinimapHeightTexturePath)
                || !File.Exists(obj.m_cachedMinimapMetaPath)
                || Version.World.DeepNorth != ZNet.World.m_worldVersion)
            {
                return false;
            }
            try
            {
                var meta = File.ReadAllBytes(obj.m_cachedMinimapMetaPath);
                if (BitConverter.ToInt32(meta, 0) != worldSeed)
                {
                    Log("Cached minimap is for another seed, regenerating it.");
                    return false;
                }
                var cacheVersion = (Version.CachedMinimap)BitConverter.ToInt32(meta, 4);
                if (cacheVersion != Version.CachedMinimap.Original)
                {
                    Log($"Cached minimap version changed from {cacheVersion} to {Version.CachedMinimap.Original}, regenerating it.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error loading the cached minimap meta data: {ex.Message}");
                return false;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            int pixels = obj.m_textureSize * obj.m_textureSize;
            try
            {
                var forestPixels = Utils.CompressedBufferToColors(File.ReadAllBytes(obj.m_cachedMinimapMaskTexturePath));
                var mapPixels = Utils.CompressedBufferToColors(File.ReadAllBytes(obj.m_cachedMinimapBiomeTexturePath));
                var heightPixels = Utils.CompressedHalfBufferToRedChannel(File.ReadAllBytes(obj.m_cachedMinimapHeightTexturePath));
                // The meta data doesn't store the map size, so it has to be checked here.
                if (forestPixels.Length != pixels || mapPixels.Length != pixels || heightPixels.Length != pixels)
                {
                    Log("Cached minimap has a different size, regenerating it.");
                    return false;
                }
                obj.m_forestMaskTexture.SetPixels32(forestPixels);
                obj.m_forestMaskTexture.Apply();
                obj.m_mapTexture.SetPixels32(mapPixels);
                obj.m_mapTexture.Apply();
                obj.m_heightTexture.SetPixels(heightPixels);
                obj.m_heightTexture.Apply();
            }
            catch (Exception ex)
            {
                LogWarning($"Error loading the cached minimap textures: {ex.Message}");
                return false;
            }
            ZLog.Log("Loading minimap textures done [" + stopwatch.ElapsedMilliseconds.ToString() + "ms]");
            return true;
        }
    }
}

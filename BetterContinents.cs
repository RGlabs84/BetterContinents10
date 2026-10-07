// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-25 for version-agnostic wording (0.9.1), and on 2026-09-27 for map mod compatibility (0.9.2), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

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
    public static ConfigEntry<CompactMapsMode> ConfigCompactMaps;
    public static ConfigEntry<int> ConfigMapMemory;
    public static ConfigEntry<int> ConfigMaxMapSize;
    public static ConfigEntry<WideSectorsMode> ConfigWideSectors;
    public static ConfigEntry<int> ConfigFileVersion;

    public static ConfigEntry<bool> ConfigEnabled;

    public static ConfigEntry<float> ConfigContinentSize;
    public static ConfigEntry<float> ConfigSeaLevelAdjustment;
    public static ConfigEntry<bool> ConfigAshlandsGapEnabled;
    public static ConfigEntry<float> ConfigWorldSize;
    public static ConfigEntry<float> ConfigEdgeSize;

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
    public static ConfigEntry<HighTerrainMode> ConfigHighTerrain;

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
    public static ConfigEntry<int> ConfigExportLargestSize;
    public static ConfigEntry<float> ConfigExportHeightmapAmount;
    public static ConfigEntry<float> ConfigExportSeaLevel;

    public static BetterContinents instance;
#nullable enable
    // Expand World Size calls this by reflection (its compatibility/BetterContinents.cs) with its own world size, when it
    // reads its config and as each world loads (a WorldGenerator.VersionSetup postfix, after the world's settings are
    // read). From then on its size is the one the maps span (MapGeometry).
    public static void SetSize(float size, float edge)
    {
        Log($"Received world size {size} and edge size {edge}");
        var geometry = new WorldGeometry(size, edge);
        ExpandWorldSizeGeometry = geometry;
        SetGeometry(geometry);
    }
    // The size Better Continents' maps span (WorldGeometry; MapGeometry chooses it). It can be swapped on another
    // thread, so a reader takes it once and uses that.
    internal static volatile WorldGeometry Geometry = WorldGeometry.Vanilla;
    // Expand World Size's size, once it has sent one.
    internal static volatile WorldGeometry? ExpandWorldSizeGeometry;

    // The size a world's maps span: Expand World Size's when it is installed; for a world made since 0.10 its own World
    // Size and Edge Size (BetterContinentsSettings.MapsSpanWorldSize); for any other world vanilla's, as always.
    internal static WorldGeometry MapGeometry(BetterContinentsSettings settings) =>
        ExpandWorldSizeGeometry
        ?? (settings.EnabledForThisWorld && settings.MapsSpanWorldSize && settings.OwnGeometry is { } own ? own : WorldGeometry.Vanilla);

    // Whether Expand World Size sizes the world: it is installed, or has sent its size.
    internal static bool ExpandWorldSizeSizes => EWS.Installed || ExpandWorldSizeGeometry != null;

    // The size the rest of a world is laid out to (WorldSizeHelper.Layout: the alt-biome grid, the locations, the game's
    // own biome bands, Ashlands, Deep North, lakes and rivers, and the minimap): a world made since 0.10 has its own World
    // Size and Edge Size, as its maps do (BetterContinentsSettings.LayoutFollowsWorldSize), unless Expand World Size is
    // installed, which lays out the world itself. Any other world keeps vanilla's layout, as always.
    internal static WorldGeometry LayoutGeometry(BetterContinentsSettings settings) =>
        !ExpandWorldSizeSizes && settings.EnabledForThisWorld && settings.LayoutFollowsWorldSize && settings.OwnGeometry is { } own
            ? own
            : WorldGeometry.Vanilla;

    // Expand World Size's World Stretch (WorldSizeHelper.SetStretch) on a Better Continents world: said once per world.
    private static string? ExpandWorldSizeStretchNote;
    internal static void NoteExpandWorldSizeStretch()
    {
        var stretch = WorldSizeHelper.ExpandWorldSizeStretch;
        if (!Settings.EnabledForThisWorld)
        {
            ExpandWorldSizeStretchNote = null;
            return;
        }
        if (stretch == 1f)
            return;
        var note = $"Expand World Size's World Stretch is {stretch}: it moves the positions Better Continents reads its maps at, so the maps are magnified and only their centre shows. Keep World Stretch at 1 on a Better Continents world: its maps already span the world's size.";
        if (note != ExpandWorldSizeStretchNote)
            LogWarning(note);
        ExpandWorldSizeStretchNote = note;
    }

    // DynamicPatch: the maps follow the world's settings.
    private static string? ExpandWorldSizeNote;
    internal static void UpdateGeometry()
    {
        NoteExpandWorldSizeStretch();
        var geometry = MapGeometry(Settings);
        if (ExpandWorldSizeGeometry is { } ews && Settings.EnabledForThisWorld && Settings.MapsSpanWorldSize
            && Settings.OwnGeometry is { } own && !own.SameAs(ews))
        {
            var note = $"World Size: Expand World Size sets the world's size ({ews}), so the maps span {ews.TotalSize} m, not this world's {own.TotalSize} m ({own}).";
            if (note != ExpandWorldSizeNote)
                Log(note);
            ExpandWorldSizeNote = note;
        }
        if (geometry.SameAs(Geometry))
            return;
        Log($"The maps span {geometry.TotalSize} m ({geometry}).");
        SetGeometry(geometry);
    }

    private static void SetGeometry(WorldGeometry geometry)
    {
        Geometry = geometry;
        TotalRadius = geometry.TotalRadius;
        TotalSize = geometry.TotalSize;
        WorldRadius = geometry.WorldRadius;
        WorldGeneratorPatch.ApplyNoiseSettings();
    }
    // The same size, for other mods that read these fields; Better Continents itself reads Geometry.
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
    private static float Normalize(float x) => Geometry.Normalize(x);
    private static Vector2 NormalizedToWorld(Vector2 p) => Geometry.NormalizedToWorld(p);

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
        SettingsSchema.Migrate();
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
    // the game does. Every setting is declared once, in SettingsSchema.
    internal static void DeclareConfig(ConfigFile config) => SettingsSchema.Bind(config);

    public void Start()
    {
        // Each on its own: an optional integration must never stop the biome table from being read.
        try
        {
            EWD.Run();
        }
        catch (Exception e)
        {
            LogError($"EWD compatibility failed: {e}");
        }
        try
        {
            EWS.Run();
        }
        catch (Exception e)
        {
            LogError($"Expand World Size compatibility failed: {e}");
        }
        try
        {
            // After every plugin's Awake, where Expand World Data patches the game's biome conversions.
            BiomeRegistry.RefreshTable();
        }
        catch (Exception e)
        {
            LogError($"Reading the game's biomes failed; biome maps use the vanilla biomes only: {e}");
        }
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

        // The map must be finished by the time GenerateWorldMap returns, as vanilla's is. Minimap.Update calls
        // LoadMapData straight after it, and map mods take the map at those two points: ZenMap's LoadMapData
        // postfix copies the three textures to build its biome-hidden map for cartography tables, and other
        // mods' GenerateWorldMap postfixes read them the moment the call returns. Up to 0.9.1 the work ran as a
        // background task and this returned at once, so all of those saw empty textures (on a Better Continents
        // world, ZenMap's tables showed meadows and plains as sea and the rest as noise), and a second "fake"
        // call was needed to run other mods' postfixes again once the map was ready. The work still runs on
        // several threads; the main thread now waits for it. The game shows its loading screen at this point,
        // and vanilla's own single-threaded generation blocks it for longer.
        private static bool ThreadedGenerationFailed;

        [HarmonyPrefix, HarmonyPatch(nameof(Minimap.GenerateWorldMap))]
        private static bool GenerateWorldMapPrefix(Minimap __instance)
        {
            // After one failure, vanilla draws the map for the rest of the session. Minimap.Update calls
            // GenerateWorldMap every frame until a call succeeds, and retrying the threaded version there would
            // redo the whole map and log the same error on every frame.
            if (ThreadedGenerationFailed)
                return true;
            try
            {
                GenerateWorldMapMT(__instance);
                return false;
            }
            catch (Exception e)
            {
                // Up to 0.9.1 a failed worker was ignored, and the rows it never reached stayed empty on the map.
                ThreadedGenerationFailed = true;
                LogError($"Multi-threaded minimap generation failed, so the game draws the map itself: {e}");
                return true;
            }
        }

        private static void GenerateWorldMapMT(Minimap map)
        {
            var stopwatch = Stopwatch.StartNew();
            int size = map.m_textureSize;
            // The loading screen waits for this, so use the whole CPU; vanilla uses one core.
            int threads = Math.Max(1, Math.Min(16, Environment.ProcessorCount));
            Log($"Generating minimap textures multi-threaded ({size} x {size}, {threads} threads) ...");
            // The vanilla GenerateWorldMap is skipped, so the cache has to be invalidated here instead.
            // Otherwise a failed generation leaves a cache that still passes the seed and version checks.
            Minimap.DeleteMapTextureData(ZNet.World.m_name);
            int halfSize = size / 2;
            float halfPixel = map.m_pixelSize / 2f;
            var mapPixels = new Color32[size * size];
            var forestPixels = new Color32[size * size];
            var heightPixels = new Color[size * size];
            var cachedHeights = new float[size * size];
            var worldGenerator = WorldGenerator.instance;
            // SimpleParallelFor waits for every worker and throws an AggregateException if any of them failed,
            // which the prefix turns into vanilla's generation.
            GameUtils.SimpleParallelFor(threads, 0, size, i =>
            {
                float wy = (i - halfSize) * map.m_pixelSize + halfPixel;
                for (int j = 0; j < size; j++)
                {
                    float wx = (j - halfSize) * map.m_pixelSize + halfPixel;
                    var biome = worldGenerator.GetBiome(wx, wy);
                    float biomeHeight = worldGenerator.GetBiomeHeight(biome, wx, wy, out _);
                    int index = i * size + j;
                    mapPixels[index] = map.GetPixelColor(biome);
                    forestPixels[index] = map.GetMaskColor(wx, wy, biomeHeight, biome);
                    // Expand World Data's minimap height for the biome, applied where its transpiler applies it in
                    // vanilla's loop: after the mask colour, which keeps the unscaled height.
                    biomeHeight = EWD.MinimapHeight(biomeHeight, biome);
                    // Alpha 0, not the 1 the three-argument Color constructor gives: vanilla
                    // fills this array by assigning .r onto a default Color, so its alpha is 0.
                    heightPixels[index] = new Color(biomeHeight, 0f, 0f, 0f);
                    cachedHeights[index] = biomeHeight;
                }
            });

            map.m_forestMaskTexture.SetPixels32(forestPixels);
            map.m_forestMaskTexture.Apply();
            map.m_mapTexture.SetPixels32(mapPixels);
            map.m_mapTexture.Apply();
            map.m_heightTexture.SetPixels(heightPixels);
            map.m_heightTexture.Apply();

            Log($"Finished generating minimap textures multi-threaded in {stopwatch.ElapsedMilliseconds} ms");
            if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported)
            {
                // This also writes the meta file with the world seed and the cache version.
                map.SaveMapTextureDataToDisk(forestPixels, mapPixels, cachedHeights);
            }
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

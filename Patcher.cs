// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-25 for version-agnostic wording (0.9.1), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the vegetation twin guard and the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // Switches Better Continents' Harmony patches, and the size its maps span (UpdateGeometry), to match the world's
  // settings (Settings): at world load, when the settings change, and back off in the main menu. Most patches are simply on or off (Toggle, declared below in the
  // order they are switched); biome precision, the base height version and the world size are more than that.
  public static void DynamicPatch()
  {
    // First: the game's rules that hold a height (HighTerrain.cs), on for a world that wants them (its High Terrain setting: by default a
    // heightmap read at an amount above 5, the most before 0.10.3) and off for any other. It reads only the settings, and comes before the
    // steps that can throw (another mod's transpiler can fail a Harmony patch), so that a world that is not high never runs on with the
    // patches of the one before it.
    PatchHighTerrain();
    UpdateGeometry();
    PatchHeightmap();
    PatchBiomeColor();
    PatchGetBaseHeight();
    foreach (var toggle in TogglesBeforeWorldSize)
      toggle.Update(Settings);
    PatchWorldSize();
    foreach (var toggle in TogglesAfterWorldSize)
      toggle.Update(Settings);
    UpdateLayerToggles();
    // The sectors a world past the game's 16 km gets: its own, out to 65 km (WorldSectors).
    WorldSectors.Update(HarmonyInstance, Settings, ExpandWorldSizeGeometry);
    // WorldGenerator caches GetBiome/GetBiomeArea results per grid cell for the lifetime of the
    // WorldGenerator instance (only cleared in its constructor). Any biome-affecting patch toggled
    // above (GetBiome, IsAshlands, IsAshlands without a heat map) can leave already-queried cells
    // returning their pre-patch answer for the rest of the session unless we clear the caches here.
    ClearWorldGeneratorBiomeCaches();
    // The water's colours follow the hot sea the patches above make (AshlandsWater).
    AshlandsWater.Refresh();
    // EnvMan's Deep North weather test reads the biome map at the camera's x and z exactly when IsDeepnorth
    // reads the biome map at all (the IsDeepnorth toggle); every other world keeps vanilla's (x, height) call. A
    // high world asks at x and z too: the game's (x, height) is inside the Deep North's circle once the camera is
    // higher than about 8000 m, wherever it is.
    var deepNorthUsesZ = DeepNorthWeatherUsesZ(Settings);
    if (deepNorthUsesZ != DeepNorthWeather.UseZ)
      Log(deepNorthUsesZ
        ? "Deep North weather: EnvMan asks IsDeepnorth at the camera's x and z (this world has a biome map or high terrain)"
        : "Deep North weather: EnvMan asks IsDeepnorth as vanilla does, at (x, camera height)");
    DeepNorthWeather.UseZ = deepNorthUsesZ;
    // Alt-biome grid, placement and planting state follows the settings (see AltBiomeControl.Configure).
    AltBiomeControl.Configure();
  }

  // AccessTools lookups return null instead of throwing when a member can't be found; Harmony then
  // throws when Patch()/Unpatch() is called with a null target, which would abort every remaining
  // Patch* call in DynamicPatch() for that cycle. Guard each lookup so a missing target logs a named
  // failure (and only disables that one feature) instead of silently killing everything after it.
  private static bool EnsurePatchTargetFound(object member, string description)
  {
    if (member == null)
    {
      LogError($"Could not find {description} - this BetterContinents feature will not work.");
      return false;
    }
    return true;
  }

  private static readonly FieldInfo CachedBiomeAreasField = AccessTools.Field(typeof(WorldGenerator), "s_cachedBiomeAreas");
  private static readonly FieldInfo CachedBiomesField = AccessTools.Field(typeof(WorldGenerator), "s_cachedBiomes");
  private static bool LoggedMissingBiomeCacheFields = false;

  private static void ClearWorldGeneratorBiomeCaches()
  {
    if (WorldGenerator.instance == null)
      return;
    if (CachedBiomeAreasField == null || CachedBiomesField == null)
    {
      if (!LoggedMissingBiomeCacheFields)
      {
        LogError("Could not find WorldGenerator.s_cachedBiomeAreas/s_cachedBiomes - biome caches may go stale after repatching.");
        LoggedMissingBiomeCacheFields = true;
      }
      return;
    }
    (CachedBiomeAreasField.GetValue(null) as IDictionary)?.Clear();
    (CachedBiomesField.GetValue(null) as IDictionary)?.Clear();
  }

  // ---- toggles: patches that are on exactly while a condition on the world's settings holds --------------------------

  private enum HookKind { Prefix, Postfix, Transpiler }

  // One patch method on one game method. priority, before and after are the HarmonyMethod's, when it needs them.
  private sealed class Hook(Func<MethodBase?> target, string targetDescription, Type patchType, string patchName, HookKind kind,
      int priority = -1, string[]? after = null, string[]? before = null)
  {
    public readonly Func<MethodBase?> Target = target;
    public readonly string TargetDescription = targetDescription;
    public readonly HookKind Kind = kind;
    public MethodInfo Patch() => AccessTools.Method(patchType, patchName);
    public HarmonyMethod Method(MethodInfo patch) =>
      priority == -1 && after == null && before == null ? new HarmonyMethod(patch) : new HarmonyMethod(patch, priority, before: before, after: after);
  }

  private sealed class Toggle(string name, Func<BetterContinentsSettings, bool> wanted, params Hook[] hooks)
  {
    // "Patching <Name>" / "Unpatching <Name>" in the log.
    public readonly string Name = name;
    // PatchProcessor rather than Harmony.Patch: the call that is the same in BepInEx's HarmonyX and in the Lib.Harmony
    // the offline tests (tools/export-tests) run it on.
    public bool ViaProcessor { get; init; }
    // When set, a failure is logged after this text and leaves the patches off, instead of stopping DynamicPatch.
    public string? FailureMessage { get; init; }
    public bool Applied { get; private set; }

    public bool Wanted(BetterContinentsSettings settings) => wanted(settings);

    public void Update(BetterContinentsSettings settings)
    {
      var toPatch = wanted(settings);
      if (toPatch == Applied)
        return;
      var targets = new MethodBase[hooks.Length];
      var patches = new MethodInfo[hooks.Length];
      for (int i = 0; i < hooks.Length; i++)
      {
        targets[i] = hooks[i].Target()!;
        patches[i] = hooks[i].Patch();
      }
      for (int i = 0; i < hooks.Length; i++)
        if (!EnsurePatchTargetFound(targets[i], hooks[i].TargetDescription))
          return;
      if (Applied)
      {
        Log("Unpatching " + Name);
        Unpatch(targets, patches);
        Applied = false;
      }
      if (!toPatch)
        return;
      Log("Patching " + Name);
      if (FailureMessage == null)
      {
        Apply(targets, patches);
        return;
      }
      try
      {
        Apply(targets, patches);
      }
      catch (Exception e)
      {
        LogError(FailureMessage + e.Message);
        try
        {
          Unpatch(targets, patches);
        }
        catch (Exception)
        {
          // Nothing was applied.
        }
      }
    }

    private static void Unpatch(MethodBase[] targets, MethodInfo[] patches)
    {
      for (int i = 0; i < targets.Length; i++)
        HarmonyInstance.Unpatch(targets[i], patches[i]);
    }

    // One Harmony call per game method, with all of its patch methods.
    private void Apply(MethodBase[] targets, MethodInfo[] patches)
    {
      foreach (var target in targets.Distinct())
      {
        HarmonyMethod? prefix = null, postfix = null, transpiler = null;
        for (int i = 0; i < hooks.Length; i++)
        {
          if (targets[i] != target)
            continue;
          var method = hooks[i].Method(patches[i]);
          switch (hooks[i].Kind)
          {
            case HookKind.Prefix: prefix = method; break;
            case HookKind.Postfix: postfix = method; break;
            default: transpiler = method; break;
          }
        }
        if (ViaProcessor)
          PatchViaProcessor(target, prefix, postfix, transpiler);
        else
          PatchDirectly(target, prefix, postfix, transpiler);
      }
      Applied = true;
    }

    // Each Harmony call in a method of its own, compiled only when it is called: the offline tests' Lib.Harmony has no
    // Harmony.Patch with HarmonyX's extra parameter, and must not meet that call while compiling the processor path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PatchViaProcessor(MethodBase target, HarmonyMethod? prefix, HarmonyMethod? postfix, HarmonyMethod? transpiler)
    {
      var processor = HarmonyInstance.CreateProcessor(target);
      if (prefix != null) processor.AddPrefix(prefix);
      if (postfix != null) processor.AddPostfix(postfix);
      if (transpiler != null) processor.AddTranspiler(transpiler);
      processor.Patch();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PatchDirectly(MethodBase target, HarmonyMethod? prefix, HarmonyMethod? postfix, HarmonyMethod? transpiler) =>
      HarmonyInstance.Patch(target, prefix: prefix, postfix: postfix, transpiler: transpiler);
  }

  private static Hook OnWorldGenerator(string method, Type[]? arguments, string patch, HookKind kind, int priority = -1, string[]? after = null,
      string[]? before = null) =>
    new(() => arguments == null ? AccessTools.Method(typeof(WorldGenerator), method) : AccessTools.Method(typeof(WorldGenerator), method, arguments),
      "WorldGenerator." + method + (arguments == null ? "" : $"({string.Join(",", arguments.Select(a => a == typeof(float) ? "float" : a == typeof(bool) ? "bool" : a.Name))})"),
      typeof(WorldGeneratorPatch), patch, kind, priority, after, before);

  // Heightmap.GetBiomeColor(float, float) has one prefix for both features that use it, the terrain map and biome
  // precision (GetBiomeColorPatch picks per vertex), so neither can unpatch the other. A new world's
  // (PrecisionKeepsTerritories) comes after Expand World Data's and keeps its territory colours.
  private static bool WantsBiomeColor(BetterContinentsSettings s) => s.ShapesWorld && (s.HasTerrainMap || EffectiveBiomePrecision(s) > 0);
  private static readonly Toggle BiomeColor = new("Heightmap.GetBiomeColor",
    s => WantsBiomeColor(s) && !s.PrecisionKeepsTerritories,
    new Hook(() => AccessTools.Method(typeof(Heightmap), nameof(Heightmap.GetBiomeColor), [typeof(float), typeof(float)]),
      "Heightmap.GetBiomeColor(float,float)", typeof(BetterContinents), nameof(GetBiomeColorPatch), HookKind.Prefix))
  { ViaProcessor = true };
  private static readonly Toggle BiomeColorAfterTerritories = new("Heightmap.GetBiomeColor, after Expand World Data's territories",
    s => WantsBiomeColor(s) && s.PrecisionKeepsTerritories,
    new Hook(() => AccessTools.Method(typeof(Heightmap), nameof(Heightmap.GetBiomeColor), [typeof(float), typeof(float)]),
      "Heightmap.GetBiomeColor(float,float)", typeof(BetterContinents), nameof(GetBiomeColorPatchAfterTerritories), HookKind.Prefix,
      after: [EWD.GUID]))
  { ViaProcessor = true };
  internal static void PatchBiomeColor()
  {
    BiomeColor.Update(Settings);
    BiomeColorAfterTerritories.Update(Settings);
  }

  // WorldGenerator.GetBiomeHeight: five postfixes on a world made before 0.10, chosen by whether the world paints the
  // ground (a paint, lava, moss or vegetation map), whether its heightmap overrides everything, and whether it has a rough
  // map. A newer one (HeightBeforeBiomeRules) has its heights from a postfix of their own, before Expand World Data's,
  // whose altitude rules then apply over them, and its paint from one after Expand World Data's ground colours (painting
  // once: an older world with a height or rough postfix paints twice, which paints the same).
  private static bool Paints(BetterContinentsSettings s) => s.HasPaintMap || s.HasLavaMap || s.HasMossMap || s.HasVegetationMap;

  // In the order DynamicPatch switches them, before and after the world size.
  private static readonly Toggle[] TogglesBeforeWorldSize =
  [
    new("WorldGenerator.GetBiomeHeight with rough",
      s => s.ShapesWorld && !s.HeightBeforeBiomeRules && !Paints(s) && !s.ShouldHeightMapOverrideAll && s.HasRoughMap,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightWithRough), HookKind.Postfix)),
    new("WorldGenerator.GetBiomeHeight with rough and paint",
      s => s.ShapesWorld && !s.HeightBeforeBiomeRules && Paints(s) && !s.ShouldHeightMapOverrideAll && s.HasRoughMap,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightWithRoughPaint), HookKind.Postfix)),
    new("WorldGenerator.GetBiomeHeight with height",
      s => s.ShapesWorld && !s.HeightBeforeBiomeRules && !Paints(s) && s.ShouldHeightMapOverrideAll,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightWithHeight), HookKind.Postfix)),
    new("WorldGenerator.GetBiomeHeight with height and paint",
      s => s.ShapesWorld && !s.HeightBeforeBiomeRules && Paints(s) && s.ShouldHeightMapOverrideAll,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightWithHeightPaint), HookKind.Postfix)),
    new("WorldGenerator.GetBiomeHeight with rough, before Expand World Data",
      s => s.ShapesWorld && s.HeightBeforeBiomeRules && !s.ShouldHeightMapOverrideAll && s.HasRoughMap,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightBeforeEwdWithRough), HookKind.Postfix,
        before: [EWD.GUID])),
    new("WorldGenerator.GetBiomeHeight with height, before Expand World Data",
      s => s.ShapesWorld && s.HeightBeforeBiomeRules && s.ShouldHeightMapOverrideAll,
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightBeforeEwdWithHeight), HookKind.Postfix,
        before: [EWD.GUID])),
    new("WorldGenerator.GetBiomeHeight with paint",
      s => s.ShapesWorld && !s.HeightBeforeBiomeRules && Paints(s),
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightWithPaint), HookKind.Postfix)),
    new("WorldGenerator.GetBiomeHeight with paint, after Expand World Data",
      s => s.ShapesWorld && s.HeightBeforeBiomeRules && Paints(s),
      OnWorldGenerator(nameof(WorldGenerator.GetBiomeHeight), null, nameof(WorldGeneratorPatch.GetBiomeHeightAfterEwdWithPaint), HookKind.Postfix,
        after: [EWD.GUID])),
    // After Expand World Data's prefix, which answers from its own world yaml and skips the original: every prefix runs
    // and the last to set the result wins, so the biome map overrides it wherever it has a biome, and a None pixel
    // leaves its answer.
    new("WorldGenerator.GetBiome",
      s => s.ShapesWorld && s.HasBiomeMap,
      OnWorldGenerator(nameof(WorldGenerator.GetBiome), [typeof(float), typeof(float), typeof(float), typeof(bool)],
        nameof(WorldGeneratorPatch.GetBiomePrefix), HookKind.Prefix, after: [EWD.GUID])),
    new("WorldGenerator.AddRivers",
      s => s.ShapesWorld && !s.RiversEnabled,
      OnWorldGenerator(nameof(WorldGenerator.AddRivers), null, nameof(WorldGeneratorPatch.AddRiversPrefix), HookKind.Prefix)),
    new("WorldGenerator.GetForestFactor prefix",
      s => s.ShapesWorld && s.ForestScale != 1f,
      OnWorldGenerator(nameof(WorldGenerator.GetForestFactor), null, nameof(WorldGeneratorPatch.GetForestFactorPrefix), HookKind.Prefix)),
    new("WorldGenerator.GetForestFactor postfix",
      s => s.ShapesWorld && (s.HasForestMap || s.ForestAmountOffset != 0f),
      OnWorldGenerator(nameof(WorldGenerator.GetForestFactor), null, nameof(WorldGeneratorPatch.GetForestFactorPostfix), HookKind.Postfix)),
    new("WorldGenerator.GetAshlandsOceanGradient prefix",
      s => s.ShapesWorld && s.HasHeatMap && s.HeatMapScale > 0f,
      OnWorldGenerator(nameof(WorldGenerator.GetAshlandsOceanGradient), [typeof(float), typeof(float)],
        nameof(WorldGeneratorPatch.GetAshlandsOceanGradientPrefix), HookKind.Prefix)),
  ];

  private static readonly Toggle[] TogglesAfterWorldSize =
  [
    // Hardcoded gaps don't work well when the whole world layout is changed (WorldGeneratorPatch.DisableGap).
    new("WorldGenerator.CreateAshlandsGap",
      s => s.ShapesWorld && !s.AshlandsGapEnabled,
      OnWorldGenerator(nameof(WorldGenerator.CreateAshlandsGap), null, nameof(WorldGeneratorPatch.DisableGap), HookKind.Prefix)),
    new("WorldGenerator.CreateDeepNorthGap",
      s => s.ShapesWorld && !s.DeepNorthGapEnabled,
      OnWorldGenerator(nameof(WorldGenerator.CreateDeepNorthGap), null, nameof(WorldGeneratorPatch.DisableGap), HookKind.Prefix)),
    // After Expand World Data's prefix (its world yaml), as GetBiome: a priority does not stop a later prefix from
    // setting the result again.
    new("WorldGenerator.IsAshlands",
      s => s.ShapesWorld && s.HasHeatMap && s.HeatMapScale > 0f,
      OnWorldGenerator(nameof(WorldGenerator.IsAshlands), null, nameof(WorldGeneratorPatch.IsAshlandsPrefix), HookKind.Prefix, Priority.VeryHigh, [EWD.GUID])),
    new("WorldGenerator.IsAshlands (no heat map)",
      s => s.ShapesWorld && s.HasBiomeMap && (!s.HasHeatMap || s.HeatMapScale == 0f),
      OnWorldGenerator(nameof(WorldGenerator.IsAshlands), null, nameof(WorldGeneratorPatch.IsAshlandsFallbackPrefix), HookKind.Prefix, Priority.VeryHigh, [EWD.GUID])),
    // Valheim 1.0 made Deep North a real biome with its own terrain, weather and snow behaviour, but vanilla still
    // decides where it IS from a hardcoded geographic test (WorldGenerator.IsDeepnorth). That test feeds EnvMan's
    // weather selection, TerrainComp's snow-vs-cultivate painting, stream placement and vanilla's own GetBiome
    // fallback - none of which consult the biome map. Without this, a biome map that moves Deep North produces Deep
    // North terrain that still has the wrong weather and paints the wrong ground when cultivated. This mirrors the
    // IsAshlands fallback; there is no heat-map condition because heat is Ashlands-only.
    new("WorldGenerator.IsDeepnorth",
      s => s.ShapesWorld && s.HasBiomeMap,
      OnWorldGenerator(nameof(WorldGenerator.IsDeepnorth), null, nameof(WorldGeneratorPatch.IsDeepnorthPrefix), HookKind.Prefix, Priority.VeryHigh)),
    // Not WorldGenerator.DeepNorthWaveFade (patched from 0.8.0 to 0.10.2): it floats boats and fish on the waves, and the water
    // shader draws them calm by the game's own circle whatever the biome map says, so the boats must follow that circle too
    // (AshlandsWater).
    // Mossmap is not needed as it doesn't apply to Ashlands.
    new("WorldGenerator.GetAshlandsHeight",
      s => s.ShapesWorld && (s.HasPaintMap || s.HasLavaMap),
      OnWorldGenerator(nameof(WorldGenerator.GetAshlandsHeight), null, nameof(WorldGeneratorPatch.GetAshlandsHeight), HookKind.Postfix)),
    // Every Better Continents world: the vegetation map and the twin guard (VegetationTwins) share these patches, so a
    // failure (another mod's rewrite of PlaceVegetation) must not stop the patches after this one.
    new("ZoneSystem.PlaceVegetation (vegetation map, twin guard)",
      s => s.ShapesWorld,
      new Hook(() => AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation)), "ZoneSystem.PlaceVegetation",
        typeof(ZoneSystemPatch), nameof(ZoneSystemPatch.PlaceVegetationPrefix), HookKind.Prefix),
      new Hook(() => AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation)), "ZoneSystem.PlaceVegetation",
        typeof(ZoneSystemPatch), nameof(ZoneSystemPatch.PlaceVegetationPostfix), HookKind.Postfix),
      new Hook(() => AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation)), "ZoneSystem.PlaceVegetation",
        typeof(ZoneSystemPatch), nameof(ZoneSystemPatch.PlaceVegetationSaveCurrent), HookKind.Transpiler),
      new Hook(() => AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.InsideClearArea)), "ZoneSystem.InsideClearArea",
        typeof(ZoneSystemPatch), nameof(ZoneSystemPatch.InsideClearAreaPostfix), HookKind.Postfix))
    { FailureMessage = "Could not patch ZoneSystem.PlaceVegetation, so the vegetation map and the twin guard are off: " },
    new("SpawnSystem.UpdateSpawnList",
      s => s.ShapesWorld && s.HasSpawnMap,
      new Hook(() => AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawnList)), "SpawnSystem.UpdateSpawnList",
        typeof(SpawnSystemPatch), nameof(SpawnSystemPatch.UpdateSpawnListEnable), HookKind.Prefix),
      new Hook(() => AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawnList)), "SpawnSystem.UpdateSpawnList",
        typeof(SpawnSystemPatch), nameof(SpawnSystemPatch.UpdateSpawnListDisable), HookKind.Postfix)),
  ];

  // The layer's two toggles (spec 4.2, 7.4, 8.1): they follow the world's baked layer, not its maps, so a GameTerrain world has them too.
  // The postfixes are slice B's (BakedVegetation, BakedGround).
  private static readonly Toggle[] LayerToggles =
  [
    // The vegetation mask: on for every world with a layer.
    new("ZoneSystem.InsideClearArea, baked vegetation mask",
      s => s.EnabledForThisWorld && s.HasLayer,
      new Hook(() => AccessTools.Method(typeof(ZoneSystem), "InsideClearArea"), "ZoneSystem.InsideClearArea",
        typeof(BakedVegetation), nameof(BakedVegetation.InsideClearAreaPostfix), HookKind.Postfix)),
    // The 1 m ground: only while the layer has ground; the lowest priority, after Expand World Data's postfix and Better Continents' own.
    new("WorldGenerator.GetBiomeHeight, baked ground",
      s => s.EnabledForThisWorld && s.Layer is { Ground.Any: true },
      new Hook(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)), "WorldGenerator.GetBiomeHeight",
        typeof(BakedGround), nameof(BakedGround.GetBiomeHeightPostfix), HookKind.Postfix, Priority.Last, after: [EWD.GUID])),
  ];

  // The toggles that follow the layer, switched to what the settings want now (DynamicPatch, and the layer being replaced).
  internal static void UpdateLayerToggles()
  {
    foreach (var toggle in LayerToggles)
      toggle.Update(Settings);
  }

  // The layer changed (BakedLayerStore.Changed): the rest of DynamicPatch has no reason to run. A patch that fails is logged, and does
  // not stop whoever replaced the layer.
  internal static void PatchLayerToggles()
  {
    try
    {
      UpdateLayerToggles();
    }
    catch (Exception e)
    {
      LogError("Could not switch the patches that follow the baked layer: " + e);
    }
  }

  // The toggles these settings want on, by name (the offline tests compare it with the rules as they were written
  // out one by one before the unifying refactor).
  internal static IEnumerable<string> WantedToggles(BetterContinentsSettings settings) =>
    new[] { BiomeColor, BiomeColorAfterTerritories }.Concat(TogglesBeforeWorldSize).Concat(TogglesAfterWorldSize).Concat(LayerToggles).Concat(HighTerrainToggles)
      .Where(t => t.Wanted(settings)).Select(t => t.Name);

  // ---- the three that are more than on or off -------------------------------------------------------------------------

  // Biome precision (BiomePrecisionGrid, in HeightmapPatch). HeightmapBuilder.Build, which runs on the game's builder
  // thread, is patched once at load and follows BiomePrecisionGrid.Active; the readers are patched here, on the main
  // thread, which is the only thread that calls them. Heightmap.GetBiomeColor belongs to PatchBiomeColor, shared with
  // the terrain map.
  private static int HeightmapGetBiomePatched = 0;
  internal static void PatchHeightmap()
  {
    var precision = EffectiveBiomePrecision(Settings);
    if (precision == HeightmapGetBiomePatched)
      return;
    var getBiome = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.GetBiome), [typeof(Vector3), typeof(float), typeof(bool)]);
    var getBiomePatch = AccessTools.Method(typeof(BetterContinents), nameof(GetBiomePatch));
    var haveBiome = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.HaveBiome), [typeof(Heightmap.Biome)]);
    var haveBiomePatch = AccessTools.Method(typeof(BetterContinents), nameof(HaveBiomePatch));
    if (!EnsurePatchTargetFound(getBiome, "Heightmap.GetBiome(Vector3,float,bool)") || !EnsurePatchTargetFound(haveBiome, "Heightmap.HaveBiome(Biome)"))
      return;
    // New builds sample at the new precision from here on. A heightmap keeps the grid it was built with (a grid
    // records its own size) until the rebuild below replaces it; with precision off no grid is read at all.
    BiomePrecisionGrid.Active = precision;
    if (precision == 0)
    {
      Log("Biome precision off: each 64 m terrain zone takes its biomes from its 4 corners (vanilla)");
      HarmonyInstance.Unpatch(getBiome, getBiomePatch);
      HarmonyInstance.Unpatch(haveBiome, haveBiomePatch);
      BiomePrecisionGrid.Clear();
    }
    else
    {
      if (HeightmapGetBiomePatched == 0)
      {
        // CreateProcessor rather than Patch(..., prefix:): the same call in BepInEx's HarmonyX and in the Lib.Harmony
        // that the offline tests (tools/export-tests) run this method on.
        HarmonyInstance.CreateProcessor(getBiome).AddPrefix(getBiomePatch).Patch();
        HarmonyInstance.CreateProcessor(haveBiome).AddPostfix(haveBiomePatch).Patch();
      }
      Log($"Biome precision {precision}: each 64 m terrain zone follows the biomes on {precision + 1} x {precision + 1} cells ({64f / (precision + 1):0.#} m)");
    }
    HeightmapGetBiomePatched = precision;
    RegenerateLoadedTerrain();
  }

  // Loaded terrain keeps the grid (or none) it was built with: rebuild the zone heightmaps (distant LOD never has a
  // grid) and let the grass read the biomes again, as the 0.7 code did before precision was switched off. In Valheim 1.0
  // that is Heightmap.s_heightmaps, a delayed Poke (Regenerate in LateUpdate, like GameUtils.ResetZones) and
  // ClutterSystem.ClearAll, a frame later (GameUtils.DropStaleTerrain: the grass is cut from the rebuilt ground, and the builds made
  // at the old precision that the terrain builder still holds are dropped). Only in a loaded world: at world load and in the main
  // menu there is nothing to redo.
  private static void RegenerateLoadedTerrain()
  {
    if (!ZoneSystem.instance)
      return;
    int rebuilt = 0;
    foreach (var hm in Heightmap.s_heightmaps)
    {
      hm.m_buildData = null;
      hm.Poke(1);
      rebuilt++;
    }
    GameUtils.DropStaleTerrain();
    Log($"Biome precision: rebuilding {rebuilt} loaded terrain zone(s) and the grass");
  }

  private static int GetBaseHeightPatched = 0;
  private static void PatchGetBaseHeight()
  {
    var patchVersion = 0;
    // A world that keeps the game's own terrain has the game's own base height (none of Better Continents' formulas).
    if (Settings.ShapesWorld)
    {
      switch (Settings.Version)
      {
        case 1:
        case 2:
          patchVersion = 1;
          break;
        case 3:
        case 4:
        case 5:
        case 6:
          patchVersion = 2;
          break;
        default:
          // GetBaseHeightV3 doesn't work at all without heightmap which makes testing more difficult.
          if (Settings.HasHeightMap || Settings.BaseHeightNoise.NoiseLayers.Count > 0)
            patchVersion = 3;
          // 4: V3 blended with the game's own height by the heightmap's alpha (a world made since 0.10 with Heightmap
          // Alpha; the map is read that way only then).
          if (Settings.BlendsHeightmapAlpha)
            patchVersion = 4;
          break;
      }
    }

    if (patchVersion == GetBaseHeightPatched)
      return;
    var method = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBaseHeight));
    var patch1 = AccessTools.Method(typeof(WorldGeneratorPatch), nameof(WorldGeneratorPatch.GetBaseHeightPrefixV1));
    var patch2 = AccessTools.Method(typeof(WorldGeneratorPatch), nameof(WorldGeneratorPatch.GetBaseHeightPrefixV2));
    var patch3 = AccessTools.Method(typeof(WorldGeneratorPatch), nameof(WorldGeneratorPatch.GetBaseHeightPrefixV3));
    var patch4 = AccessTools.Method(typeof(WorldGeneratorPatch), nameof(WorldGeneratorPatch.GetBaseHeightPrefixV3Alpha));
    var postfix4 = AccessTools.Method(typeof(WorldGeneratorPatch), nameof(WorldGeneratorPatch.GetBaseHeightPostfixV3Alpha));
    if (!EnsurePatchTargetFound(method, "WorldGenerator.GetBaseHeight"))
      return;
    if (GetBaseHeightPatched == 1)
    {
      Log("Unpatching WorldGenerator.GetBaseHeight V1");
      HarmonyInstance.Unpatch(method, patch1);
      GetBaseHeightPatched = 0;
    }
    if (GetBaseHeightPatched == 2)
    {
      Log("Unpatching WorldGenerator.GetBaseHeight V2");
      HarmonyInstance.Unpatch(method, patch2);
      GetBaseHeightPatched = 0;
    }
    if (GetBaseHeightPatched == 3)
    {
      Log("Unpatching WorldGenerator.GetBaseHeight");
      HarmonyInstance.Unpatch(method, patch3);
      GetBaseHeightPatched = 0;
    }
    if (GetBaseHeightPatched == 4)
    {
      Log("Unpatching WorldGenerator.GetBaseHeight with the heightmap's alpha");
      HarmonyInstance.Unpatch(method, patch4);
      HarmonyInstance.Unpatch(method, postfix4);
      GetBaseHeightPatched = 0;
    }
    if (patchVersion == 1)
    {
      Log($"Patching WorldGenerator.GetBaseHeight V1");
      HarmonyInstance.Patch(method, prefix: new(patch1));
      GetBaseHeightPatched = 1;
    }
    if (patchVersion == 2)
    {
      Log($"Patching WorldGenerator.GetBaseHeight V2");
      HarmonyInstance.Patch(method, prefix: new(patch2));
      GetBaseHeightPatched = 2;
    }
    if (patchVersion == 3)
    {
      Log($"Patching WorldGenerator.GetBaseHeight");
      HarmonyInstance.Patch(method, prefix: new(patch3));
      GetBaseHeightPatched = 3;
    }
    if (patchVersion == 4)
    {
      Log($"Patching WorldGenerator.GetBaseHeight with the heightmap's alpha");
      HarmonyInstance.Patch(method, prefix: new(patch4), postfix: new(postfix4));
      GetBaseHeightPatched = 4;
    }
  }

  private static void PatchWorldSize()
  {
    // A world that keeps the game's own terrain keeps the game's own sizes.
    if (!Settings.ShapesWorld)
      WorldSizeHelper.PatchEdgeChecks(HarmonyInstance, 10000f, 500f);
    else if (Settings.DisableMapEdgeDropoff)
      // Easiest to just apply very large values to disable the feature.
      WorldSizeHelper.PatchEdgeChecks(HarmonyInstance, 1E30f, 500f);
    else
      WorldSizeHelper.PatchEdgeChecks(HarmonyInstance, Settings.WorldSize, Settings.EdgeSize);

    if (!Settings.ShapesWorld)
      WorldSizeHelper.PatchWorldSize(HarmonyInstance, 10000f, 500f);
    else
      WorldSizeHelper.PatchWorldSize(HarmonyInstance, Settings.WorldSize, Settings.EdgeSize);

    WorldSizeHelper.PatchLayout(HarmonyInstance, LayoutGeometry(Settings));
  }
}

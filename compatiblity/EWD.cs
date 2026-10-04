// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace BetterContinents;

public class EWD
{
  public const string GUID = "expand_world_data";
  private static Assembly? Assembly;
  // Whether Expand World Data is installed (found by Run).
  internal static bool Installed => Assembly != null;
  private static MethodInfo? SetSize;
  // Expand World Data's biome names (BiomeManager.TryGetBiome / TryGetDisplayName): the biomes it adds from its
  // expand_biomes yaml, by name, in both directions. It also patches Enum.TryParse and Enum.GetName for biomes, but
  // asking it directly does not depend on those patches reaching Better Continents' own calls.
  private delegate bool TryGetBiomeHandler(string name, out Heightmap.Biome biome);
  private delegate bool TryGetDisplayNameHandler(Heightmap.Biome biome, out string name);
  private static TryGetBiomeHandler? TryGetBiomeByName;
  private static TryGetDisplayNameHandler? TryGetBiomeDisplayName;
  // Expand World Data's minimap height (Api.GetMinimapHeight: a biome's mapColorMultiplier above the water level). It
  // applies it by transpiling Minimap.GenerateWorldMap, which Better Continents replaces with its own drawing.
  private static volatile Func<float, Heightmap.Biome, float>? MinimapHeightFunc;
  // Expand World Data's lava biomes (BiomeManager.LavaBiomes: the biomes its yaml gives `lava: true`, rebuilt on every
  // yaml load, so read each time) and a biome's terrain (BiomeManager.GetTerrain: the biome whose ground it has, from
  // its yaml's `terrain`; the biome itself when it has none).
  private static volatile Func<Heightmap.Biome>? LavaBiomesFunc;
  private static volatile Func<Heightmap.Biome, Heightmap.Biome>? TerrainFunc;
  public static void Run()
  {
    if (!Chainloader.PluginInfos.TryGetValue(GUID, out var info)) return;
    Assembly = info.Instance.GetType().Assembly;
    BindBiomeNames();
    var type = Assembly.GetType("ExpandWorldData.WorldInfo");
    if (type == null)
    {
      BetterContinents.LogWarning("EWD compatibility: type \"ExpandWorldData.WorldInfo\" not found; skipping (Expand World Data may have changed its API).");
      return;
    }
    // AccessTools.Method can itself throw AmbiguousMatchException when the name resolves to more
    // than one overload upstream; catch it so an optional integration can never take BC down.
    try
    {
      SetSize = AccessTools.Method(type, "Set");
    }
    catch (Exception ex)
    {
      SetSize = null;
      BetterContinents.LogWarning($"EWD compatibility: failed to resolve WorldInfo.Set ({ex.Message}); skipping.");
      return;
    }
    if (SetSize == null)
    {
      BetterContinents.LogWarning("EWD compatibility: method \"WorldInfo.Set\" not found; skipping (Expand World Data may have changed its API).");
      return;
    }
    BetterContinents.Log("\"Expand World Data\" detected. Applying compatibility.");
  }

  // Internal for the offline tests, which load Expand World Data's assembly without BepInEx's chainloader.
  internal static void BindBiomeNames(Assembly assembly)
  {
    Assembly = assembly;
    BindBiomeNames();
  }

  private static void BindBiomeNames()
  {
    TryGetBiomeByName = null;
    TryGetBiomeDisplayName = null;
    MinimapHeightFunc = null;
    var manager = Assembly?.GetType("ExpandWorldData.BiomeManager");
    BindBiomeData(manager);
    try
    {
      var api = Assembly?.GetType("ExpandWorldData.Api");
      var minimapHeight = api == null ? null : AccessTools.Method(api, "GetMinimapHeight", [typeof(float), typeof(Heightmap.Biome)]);
      if (minimapHeight != null && minimapHeight.ReturnType == typeof(float))
        MinimapHeightFunc = (Func<float, Heightmap.Biome, float>)Delegate.CreateDelegate(typeof(Func<float, Heightmap.Biome, float>), minimapHeight);
      else
        BetterContinents.LogWarning("EWD compatibility: Api.GetMinimapHeight not found (Expand World Data may have changed its API); the minimap keeps unscaled heights.");
    }
    catch (Exception ex)
    {
      MinimapHeightFunc = null;
      BetterContinents.LogWarning($"EWD compatibility: failed to bind Api.GetMinimapHeight ({ex.Message}); the minimap keeps unscaled heights.");
    }
    try
    {
      var byName = manager == null ? null : AccessTools.Method(manager, "TryGetBiome", [typeof(string), typeof(Heightmap.Biome).MakeByRefType()]);
      var byBiome = manager == null ? null : AccessTools.Method(manager, "TryGetDisplayName", [typeof(Heightmap.Biome), typeof(string).MakeByRefType()]);
      if (byName != null)
        TryGetBiomeByName = (TryGetBiomeHandler)Delegate.CreateDelegate(typeof(TryGetBiomeHandler), byName);
      if (byBiome != null)
        TryGetBiomeDisplayName = (TryGetDisplayNameHandler)Delegate.CreateDelegate(typeof(TryGetDisplayNameHandler), byBiome);
    }
    catch (Exception ex)
    {
      TryGetBiomeByName = null;
      TryGetBiomeDisplayName = null;
      BetterContinents.LogWarning($"EWD compatibility: failed to bind BiomeManager's biome names ({ex.Message}); biome maps accept its biomes only where Enum.TryParse does.");
      return;
    }
    if (TryGetBiomeByName == null || TryGetBiomeDisplayName == null)
      BetterContinents.LogWarning("EWD compatibility: BiomeManager.TryGetBiome or TryGetDisplayName not found (Expand World Data may have changed its API); biome maps accept its biomes only where Enum.TryParse does.");
  }

  private static void BindBiomeData(Type? manager)
  {
    LavaBiomesFunc = null;
    TerrainFunc = null;
    if (manager == null) return;
    try
    {
      var lava = AccessTools.Field(manager, "LavaBiomes");
      if (lava != null && lava.IsStatic && lava.FieldType == typeof(Heightmap.Biome))
        LavaBiomesFunc = Expression.Lambda<Func<Heightmap.Biome>>(Expression.Field(null, lava)).Compile();
      var terrain = AccessTools.Method(manager, "GetTerrain", [typeof(Heightmap.Biome)]);
      if (terrain != null && terrain.IsStatic && terrain.ReturnType == typeof(Heightmap.Biome))
        TerrainFunc = (Func<Heightmap.Biome, Heightmap.Biome>)Delegate.CreateDelegate(typeof(Func<Heightmap.Biome, Heightmap.Biome>), terrain);
    }
    catch (Exception ex)
    {
      LavaBiomesFunc = null;
      TerrainFunc = null;
      BetterContinents.LogWarning($"EWD compatibility: failed to bind BiomeManager's lava biomes and terrains ({ex.Message}); its lava biomes are not hot on a biome map.");
      return;
    }
    if (LavaBiomesFunc == null || TerrainFunc == null)
      BetterContinents.LogWarning("EWD compatibility: BiomeManager.LavaBiomes or GetTerrain not found (Expand World Data may have changed its API); its lava biomes are not hot on a biome map.");
  }

  // Whether Expand World Data gives this biome lava (its yaml's `lava: true`); false when it is not installed.
  public static bool IsLavaBiome(Heightmap.Biome biome)
  {
    var func = LavaBiomesFunc;
    if (func == null) return false;
    try
    {
      return (func() & biome) != Heightmap.Biome.None;
    }
    catch (Exception)
    {
      LavaBiomesFunc = null;
      return false;
    }
  }

  // The biome whose ground this one has (Expand World Data's `terrain`); the biome itself when it is not installed.
  public static Heightmap.Biome Terrain(Heightmap.Biome biome)
  {
    var func = TerrainFunc;
    if (func == null) return biome;
    try
    {
      return func(biome);
    }
    catch (Exception)
    {
      TerrainFunc = null;
      return biome;
    }
  }

  // The biome Expand World Data knows by this name (its own and vanilla's, any case), if it is installed.
  public static bool TryGetBiome(string name, out Heightmap.Biome biome)
  {
    biome = Heightmap.Biome.None;
    if (TryGetBiomeByName == null) return false;
    try
    {
      return TryGetBiomeByName(name, out biome);
    }
    catch (Exception)
    {
      biome = Heightmap.Biome.None;
      return false;
    }
  }

  // Expand World Data's name for a biome, if it is installed and knows the biome.
  public static bool TryGetName(Heightmap.Biome biome, out string name)
  {
    name = "";
    if (TryGetBiomeDisplayName == null) return false;
    try
    {
      return TryGetBiomeDisplayName(biome, out name) && !string.IsNullOrEmpty(name);
    }
    catch (Exception)
    {
      name = "";
      return false;
    }
  }

  // The height the minimap shows for a biome: Expand World Data's scaled height if it is installed, else the height.
  // Called from the minimap's worker threads; if it ever throws, it is logged once and not called again.
  public static float MinimapHeight(float height, Heightmap.Biome biome)
  {
    var func = MinimapHeightFunc;
    if (func == null) return height;
    try
    {
      return func(height, biome);
    }
    catch (Exception ex)
    {
      if (MinimapHeightFunc != null)
      {
        MinimapHeightFunc = null;
        BetterContinents.LogWarning($"EWD compatibility: Api.GetMinimapHeight failed ({ex.Message}); the minimap keeps unscaled heights from here on.");
      }
      return height;
    }
  }

  public static void RefreshSize(float worldRadius, float worldTotalRadius, float worldStretch, float biomeStretch)
  {
    if (SetSize == null) return;
    try
    {
      SetSize.Invoke(null, [worldRadius, worldTotalRadius, worldStretch, biomeStretch]);
    }
    catch (Exception ex)
    {
      BetterContinents.LogWarning($"EWD compatibility: WorldInfo.Set call failed ({ex.Message}); disabling further calls.");
      SetSize = null;
    }
  }
}

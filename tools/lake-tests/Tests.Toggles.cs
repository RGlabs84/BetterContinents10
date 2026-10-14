// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// The lakes' patches on the installed game's own methods (LakeWater.Toggles.cs): which worlds want them, that they install and remove on the real IL with the
// parameters Harmony binds by name, and what the depth postfix writes into a volume (BUILD-SPEC-TARN.md 4.3 item 8).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterContinents;
using HarmonyLib;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  private static readonly string[] LakePatchNames =
  [
    "WaterVolume.DetectWaterDepth, lake depths",
    "WaterVolume.SetupMaterial, lake swim boxes",
    "WaterVolume.UpdateMaterials, lake swim boxes",
    "Minimap.GenerateWorldMap, lakes on the map",
    "Minimap.TryLoadMinimapTextureData, lakes on the map",
  ];

  private static BC.BetterContinentsSettings LakeWorld(bool lakes = true, bool enabled = true, bool gameTerrain = false)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = enabled, GameTerrain = gameTerrain };
    if (lakes)
      s.Lakes = new LakeSet([MakeLake(0, 0, 60f, 100f, "one")], 0.5f, 0);
    return s;
  }

  private static object[] LakeToggleObjects() =>
    ((Array)typeof(BC).GetField("LakeToggles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Cast<object>().ToArray();

  private static string NameOfToggle(object toggle) => (string)toggle.GetType().GetField("Name")!.GetValue(toggle)!;

  public static void TogglesWantedTest()
  {
    Section("patches: which worlds want the lakes' patches");
    var wanted = (BC.BetterContinentsSettings s) => BC.WantedToggles(s).Where(n => LakePatchNames.Contains(n)).OrderBy(n => n).ToList();
    var all = LakePatchNames.OrderBy(n => n).ToList();
    C(BC.LakeToggleNames.OrderBy(n => n).SequenceEqual(all), "LakeToggles holds the five: DetectWaterDepth, SetupMaterial, UpdateMaterials, GenerateWorldMap, TryLoadMinimapTextureData");
    C(wanted(LakeWorld()).SequenceEqual(all), "a world with lakes wants all five");
    C(wanted(LakeWorld(gameTerrain: true)).SequenceEqual(all), "a GameTerrain world with lakes too: they follow EnabledForThisWorld, not ShapesWorld");
    C(wanted(LakeWorld(lakes: false)).Count == 0, "a world with no lakes wants none");
    var empty = LakeWorld(lakes: false);
    empty.Lakes = LakeSet.Empty;
    C(wanted(empty).Count == 0, "nor does one whose set is empty");
    C(wanted(LakeWorld(enabled: false)).Count == 0, "Better Continents off for the world: none");
    C(wanted(new BC.BetterContinentsSettings()).Count == 0 && wanted(BC.BetterContinentsSettings.Disabled()).Count == 0, "the main menu's settings: none");
    var was = TarnCompat.Installed;
    try
    {
      TarnCompat.Installed = true;
      C(wanted(LakeWorld()).Count == 0, "with the Tarn mod installed, none (Better Continents' lakes are off, the data is kept)");
    }
    finally
    {
      TarnCompat.Installed = was;
    }
    C(BC.WantedToggles(LakeWorld(lakes: false)).All(n => !LakePatchNames.Contains(n)), "and a world without lakes keeps every other patch as it was");
  }

  public static void TogglesInstallOnTheGamesIlTest()
  {
    Section("patches: the five install and remove on the game's methods, by the names Harmony binds");
    var harmony = new Harmony("lake-tests.toggles");
    var saved = BC.HarmonyInstance;
    BC.HarmonyInstance = harmony;
    var toggles = LakeToggleObjects();
    var targets = new Dictionary<string, MethodBase>
    {
      [LakePatchNames[0]] = AccessTools.Method(typeof(WaterVolume), "DetectWaterDepth"),
      [LakePatchNames[1]] = AccessTools.Method(typeof(WaterVolume), "SetupMaterial"),
      [LakePatchNames[2]] = AccessTools.Method(typeof(WaterVolume), "UpdateMaterials"),
      [LakePatchNames[3]] = AccessTools.Method(typeof(Minimap), "GenerateWorldMap"),
      [LakePatchNames[4]] = AccessTools.Method(typeof(Minimap), "TryLoadMinimapTextureData"),
    };
    C(targets.Values.All(m => m != null), "the game has all five methods");
    var patches = new Dictionary<string, MethodInfo>
    {
      [LakePatchNames[0]] = AccessTools.Method(typeof(LakeWater), nameof(LakeWater.DetectWaterDepthPostfix)),
      [LakePatchNames[1]] = AccessTools.Method(typeof(LakeWater), nameof(LakeWater.SetupMaterialPrefix)),
      [LakePatchNames[2]] = AccessTools.Method(typeof(LakeWater), nameof(LakeWater.UpdateMaterialsPrefix)),
      [LakePatchNames[3]] = AccessTools.Method(typeof(LakeMap), nameof(LakeMap.GenerateWorldMapPostfix)),
      [LakePatchNames[4]] = AccessTools.Method(typeof(LakeMap), nameof(LakeMap.TryLoadMinimapTextureDataPostfix)),
    };
    foreach (var name in LakePatchNames)
    {
      // Harmony binds a patch's parameters by name: __instance, __result, or the target's own parameter names
      var target = targets[name];
      var patch = patches[name];
      var targetParameters = target.GetParameters().ToDictionary(p => p.Name, p => p.ParameterType);
      bool fits = patch != null && patch.GetParameters().All(p =>
        p.Name == "__instance" ? p.ParameterType == target.DeclaringType : p.Name == "__result" ? p.ParameterType == ((MethodInfo)target).ReturnType : targetParameters.TryGetValue(p.Name, out var t) && t == p.ParameterType);
      C(fits, $"{patch?.Name}: its parameters are the game's {target.DeclaringType!.Name}.{target.Name}'s, by name and type");
    }
    C(patches[LakePatchNames[1]].ReturnType == typeof(bool) && patches[LakePatchNames[2]].ReturnType == typeof(bool), "the SetupMaterial and UpdateMaterials patches are bool prefixes");
    try
    {
      var world = LakeWorld();
      foreach (var toggle in toggles)
        toggle.GetType().GetMethod("Update")!.Invoke(toggle, [world]);
      foreach (var toggle in toggles)
        C((bool)toggle.GetType().GetProperty("Applied")!.GetValue(toggle)!, $"{NameOfToggle(toggle)}: applied for a world with lakes");
      foreach (var name in LakePatchNames)
      {
        var info = Harmony.GetPatchInfo(targets[name]);
        var ours = info == null ? [] : info.Prefixes.Concat(info.Postfixes).Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod.Name).ToList();
        C(ours.Count == 1 && ours[0] == patches[name].Name, $"{name}: one patch of ours, {patches[name].Name} ({string.Join(",", ours)})");
      }
      var setup = Harmony.GetPatchInfo(targets[LakePatchNames[1]]);
      C(setup.Prefixes.Any(p => p.owner == harmony.Id && p.priority == HarmonyLib.Priority.Low), "the SetupMaterial prefix is at Priority.Low");
      foreach (var toggle in toggles)
        toggle.GetType().GetMethod("Update")!.Invoke(toggle, [LakeWorld(lakes: false)]);
      C(toggles.All(t => !(bool)t.GetType().GetProperty("Applied")!.GetValue(t)!), "for a world without lakes all five are removed again");
      C(LakePatchNames.All(n => (Harmony.GetPatchInfo(targets[n])?.Prefixes.Concat(Harmony.GetPatchInfo(targets[n])!.Postfixes).Any(p => p.owner == harmony.Id)) != true), "and Harmony holds none of ours");
    }
    finally
    {
      foreach (var toggle in toggles)
        toggle.GetType().GetMethod("Update")!.Invoke(toggle, [LakeWorld(lakes: false)]);
      harmony.UnpatchAll(harmony.Id);
      BC.HarmonyInstance = saved;
    }
  }

  public static void DepthApplyTest()
  {
    Section("patches: what the depth postfix writes into a volume (LakeWater.DepthsOf)");
    float[] metres = [8.2f, 14.6f, 1.8f, 0f];
    var tile = new LakeWater.Volume { Box = false, TileMetres = metres, TileX = 0, TileZ = 0 };
    C(LakeWater.DepthsOf(tile, 0.5f).SequenceEqual(LakeDepth.Normalize(metres, 0.5f)), "a tile gets its corners' depths over 10 m times the scale, clamped: " + string.Join(", ", LakeWater.DepthsOf(tile, 0.5f)));
    var box = new LakeWater.Volume { Box = true, TileMetres = metres, TileX = 0, TileZ = 0, MinX = -10, MinZ = -6, MaxX = 14, MaxZ = 20 };
    var expected = LakeDepth.BoxCorners(LakeDepth.Normalize(metres, 0.5f), 0, 0, -10, -6, 14, 20);
    C(LakeWater.DepthsOf(box, 0.5f).SequenceEqual(expected), "a box gets its tile's depth at its corners: " + string.Join(", ", LakeWater.DepthsOf(box, 0.5f)));
    C(!LakeWater.DepthsOf(box, 2f).SequenceEqual(expected), "a bigger Lake Depth Scale changes them");
    C(LakeWater.DepthsOf(box, 0f).All(d => d == 0f) && LakeWater.DepthsOf(tile, 0f).All(d => d == 0f), "and scale 0 makes the water as shallow as the shader knows");
    C(LakeSet.DefaultDepthScale == 0.5f, "the default Lake Depth Scale is 0.5, Tarn's");
  }

  // The game's members the lake code touches, read from the installed game's own assembly (not the publicized one the plugin was built against): they exist, with the
  // types the plan assumes.
  public static void GameMembersTest()
  {
    Section("patches: the game's members the lake code relies on, on the installed game");
    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    void Field(Type type, string name, Type fieldType)
    {
      var f = type.GetField(name, flags);
      C(f != null && f.FieldType == fieldType, $"{type.Name}.{name} is a {fieldType.Name}{(f == null ? " (missing)" : f.IsPublic ? " (public)" : " (private)")}");
    }
    Field(typeof(WaterVolume), "m_normalizedDepth", typeof(float[]));
    Field(typeof(WaterVolume), "m_oneDepth", typeof(float));
    Field(typeof(WaterVolume), "m_waterSurface", typeof(UnityEngine.MeshRenderer));
    Field(typeof(WaterVolume), "m_heightmap", typeof(Heightmap));
    Field(typeof(WaterVolume), "m_forceDepth", typeof(float));
    Field(typeof(WaterVolume), "m_surfaceOffset", typeof(float));
    Field(typeof(WaterVolume), "m_useGlobalWind", typeof(bool));
    Field(typeof(Minimap), "m_heightTexture", typeof(UnityEngine.Texture2D));
    Field(typeof(Minimap), "m_forestMaskTexture", typeof(UnityEngine.Texture2D));
    Field(typeof(Minimap), "m_hasGenerated", typeof(bool));
    Field(typeof(Minimap), "m_textureSize", typeof(int));
    Field(typeof(Minimap), "m_pixelSize", typeof(float));
    Field(typeof(ZoneSystem), "m_zonePrefab", typeof(UnityEngine.GameObject));
    Field(typeof(ZoneSystem), "m_waterLevel", typeof(float));
    C(typeof(AudioMan).GetField("m_oceanAudio", flags)?.FieldType.Name == "AudioClip" && typeof(AudioMan).GetField("m_ambientMixer", flags)?.FieldType.Name == "AudioMixerGroup", "AudioMan.m_oceanAudio is an AudioClip and m_ambientMixer an AudioMixerGroup");
    var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    C(typeof(WaterVolume).GetMethod("DetectWaterDepth", all) is { } d && d.GetParameters().Length == 0 && d.ReturnType == typeof(void), "WaterVolume.DetectWaterDepth() exists");
    C(typeof(WaterVolume).GetMethod("SetupMaterial", all) is { } m && m.GetParameters().Length == 0, "WaterVolume.SetupMaterial() exists");
    C(typeof(WaterVolume).GetMethod("UpdateMaterials", all) is { IsPublic: true }, "WaterVolume.UpdateMaterials() is public");
    C(typeof(Minimap).GetMethod("GenerateWorldMap", all) is { } g && g.GetParameters().Length == 0, "Minimap.GenerateWorldMap() exists");
    C(typeof(Minimap).GetMethod("TryLoadMinimapTextureData", all) is { } t && t.ReturnType == typeof(bool), "Minimap.TryLoadMinimapTextureData(int) returns a bool");
    C(typeof(Minimap).GetMethod("ForceRegen", all) is { IsPublic: true }, "Minimap.ForceRegen() is public");
    C(typeof(WorldGenerator).GetMethod("IsDeepnorth", BindingFlags.Public | BindingFlags.Static) != null && typeof(WorldGenerator).GetMethod("GetAshlandsOceanGradient", BindingFlags.Public | BindingFlags.Static, null, [typeof(float), typeof(float)], null) != null,
      "WorldGenerator.IsDeepnorth and GetAshlandsOceanGradient(float, float) are public statics");
  }
}

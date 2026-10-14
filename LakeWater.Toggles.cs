// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace BetterContinents;

public partial class BetterContinents
{
  // The lakes' patches (spec 2.12), each on while LakeWater.Wanted: the world is Better Continents', it has lakes, and the Tarn mod is not installed. They follow
  // EnabledForThisWorld, not ShapesWorld, as the baked layer's do: a GameTerrain world, and a vanilla world converted to carry lakes, have lakes too. A world
  // without lakes has none of them in the game's methods. Declared here (Toggle and Hook are private to this class); LakeStreamer.Tick switches them when the
  // world's lake set appears or goes, DynamicPatch when the world or its settings change.
  private static readonly Toggle[] LakeToggles =
  [
    // A lake volume has no zone terrain to read its depth from [Tarn: Water.cs:65-76].
    new("WaterVolume.DetectWaterDepth, lake depths",
      LakeWater.Wanted,
      new Hook(() => AccessTools.Method(typeof(WaterVolume), "DetectWaterDepth"), "WaterVolume.DetectWaterDepth",
        typeof(LakeWater), nameof(LakeWater.DetectWaterDepthPostfix), HookKind.Postfix))
    { ViaProcessor = true, FailureMessage = "Could not patch WaterVolume.DetectWaterDepth, so lakes keep a flat depth: " },
    // The swim boxes borrow a tile's surface and must not paint onto it [Tarn: Water.cs:79-84]: after Better Continents' own prefix on it (the world's edge), whose
    // effect on a box is harmless, and with no opinion on any other volume.
    new("WaterVolume.SetupMaterial, lake swim boxes",
      LakeWater.Wanted,
      new Hook(() => AccessTools.Method(typeof(WaterVolume), "SetupMaterial"), "WaterVolume.SetupMaterial",
        typeof(LakeWater), nameof(LakeWater.SetupMaterialPrefix), HookKind.Prefix, Priority.Low))
    { ViaProcessor = true, FailureMessage = "Could not patch WaterVolume.SetupMaterial, so lake swim boxes may paint their depths onto the water: " },
    new("WaterVolume.UpdateMaterials, lake swim boxes",
      LakeWater.Wanted,
      new Hook(() => AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials)), "WaterVolume.UpdateMaterials",
        typeof(LakeWater), nameof(LakeWater.UpdateMaterialsPrefix), HookKind.Prefix))
    { ViaProcessor = true, FailureMessage = "Could not patch WaterVolume.UpdateMaterials, so every lake swim box sets the water's clock once more a frame: " },
    // The map: marked to be painted again after the game made it, or loaded it from its files [Tarn: MapLakes.cs:90-100].
    new("Minimap.GenerateWorldMap, lakes on the map",
      LakeWater.Wanted,
      new Hook(() => AccessTools.Method(typeof(Minimap), "GenerateWorldMap"), "Minimap.GenerateWorldMap",
        typeof(LakeMap), nameof(LakeMap.GenerateWorldMapPostfix), HookKind.Postfix))
    { ViaProcessor = true, FailureMessage = "Could not patch Minimap.GenerateWorldMap, so lakes stay off the map: " },
    new("Minimap.TryLoadMinimapTextureData, lakes on the map",
      LakeWater.Wanted,
      new Hook(() => AccessTools.Method(typeof(Minimap), "TryLoadMinimapTextureData"), "Minimap.TryLoadMinimapTextureData",
        typeof(LakeMap), nameof(LakeMap.TryLoadMinimapTextureDataPostfix), HookKind.Postfix))
    { ViaProcessor = true, FailureMessage = "Could not patch Minimap.TryLoadMinimapTextureData, so lakes stay off the map: " },
  ];

  // The toggles that follow the world's lakes, switched to what the settings want now (DynamicPatch, and LakeStreamer.Tick when the lake set appears or goes). A patch
  // that fails is logged and does not stop whoever called.
  internal static void UpdateLakeToggles()
  {
    foreach (var toggle in LakeToggles)
    {
      try
      {
        toggle.Update(Settings);
      }
      catch (System.Exception e)
      {
        LogError($"Could not switch the lake patch '{toggle.Name}': {e}");
      }
    }
  }

  // Their names, for the offline tests.
  internal static IEnumerable<string> LakeToggleNames => LakeToggles.Select(t => t.Name);
}

// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0), and on 2026-10-04 for the vegetation twin guard (0.10.0).

using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  [HarmonyPatch(typeof(ZoneSystem))]
  private class ZoneSystemPatch
  {
    [HarmonyPostfix, HarmonyPatch(nameof(ZoneSystem.Load))]
    private static void LoadPostfix(ZoneSystem __instance)
    {
      if (Settings.EnabledForThisWorld)
      {
        if (!__instance.m_locationsGenerated && __instance.m_locationInstances.Count > 0)
        {
          LogWarning("Skipping automatic genloc, use the command manually if needed.");
          __instance.m_locationsGenerated = true;
        }
        Settings.LoadPrefabs(ZNetScene.instance);
      }
    }
    // Changes to location type spawn placement (this is the functional part of the mod)
    [HarmonyPostfix, HarmonyPatch(nameof(ZoneSystem.ClearNonPlacedLocations), []), HarmonyPriority(Priority.Low)]
    private static void ClearNonPlacedLocationsPostfix(ZoneSystem __instance)
    {
      if (!Settings.EnabledForThisWorld) return;
      if (!Settings.HasLocationMap && !Settings.OverrideStartPosition) return;
      List<ZoneSystem.ZoneLocation> locs = [.. __instance.m_locations.Where(loc => loc.m_enable && loc.m_quantity != 0).OrderByDescending(x => x.m_prioritized)];
      if (Settings.HasLocationMap)
      {
        foreach (var loc in locs)
          HandleLocation(loc);
      }
      if (Settings.OverrideStartPosition)
      {
        var startLoc = locs.FirstOrDefault(loc => loc.m_prefabName == "StartTemple");
        if (startLoc != null)
        {
          var y = WorldGenerator.instance.GetHeight(Settings.StartPositionX, Settings.StartPositionY);
          Vector3 position = new(Settings.StartPositionX, y, Settings.StartPositionY);
          __instance.RegisterLocation(startLoc, position, false);
          Log($"Start position overriden: set to {position}");
        }
      }
    }

    private static void HandleLocation(ZoneSystem.ZoneLocation loc)
    {
      var groupName = string.IsNullOrEmpty(loc.m_group) ? "<unnamed>" : loc.m_group;
      Log($"Generating location of group {groupName}, required {loc.m_quantity}, unique {loc.m_unique}, name {loc.m_prefabName}");
      // Place all locations specified by the spawn map, ignoring counts specified in the prefab
      int placed = 0;
      foreach (var normalizedPosition in Settings.GetAllSpawns(loc.m_prefabName))
      {
        var worldPos = NormalizedToWorld(normalizedPosition);
        var position = new Vector3(
            worldPos.x,
            WorldGenerator.instance.GetHeight(worldPos.x, worldPos.y),
            worldPos.y
        );
        ZoneSystem.instance.RegisterLocation(loc, position, false);
        Log($"Position of {loc.m_prefabName} ({++placed}/{loc.m_quantity}) overriden: set to {position}");
      }
    }


    [HarmonyPrefix, HarmonyPatch(nameof(ZoneSystem.CountNrOfLocation))]
    private static bool CountNrOfLocation(ZoneSystem.ZoneLocation location, ref int __result)
    {
      if (!Settings.EnabledForThisWorld) return true;
      if (!Settings.SkipDefaultLocations) return true;
      if (location.m_prefabName == "StartTemple") return true;
      __result = location.m_quantity;
      return false;
    }

    /* Vegetation placement, patched in every Better Continents world (Patcher.PatchVegetation): the vegetation map and
       the twin guard (VegetationTwins).
       The vegetation map's enabling is done for the whole zone. More precise solution would require entirely new implementation.
       Enabling is currently done by setting all biomes. This has to be reverted at end of the function.
       Disabling uses the clear area system so it's very precise. However transpiler is needed to keep track of the current vegetation.
       This technically should allow precise manipulation with enable + disable combo.
       The twin guard uses the same clear-area check, the last one before an object is placed, and the same tracking.
    */
    public static void PlaceVegetationPrefix(ZoneSystem __instance, Vector2s zoneID, Vector3 zoneCenterPos)
    {
      CurrentVegetation = null;
      VegetationTwins.Begin(zoneID);
      Settings.ApplyVegetationMap(zoneCenterPos, __instance.m_vegetation);
    }
    public static void PlaceVegetationPostfix()
    {
      Settings.RevertVegetationMap();
      VegetationTwins.End();
      CurrentVegetation = null;
    }
    private static ZoneSystem.ZoneVegetation? CurrentVegetation;
    private static ZoneSystem.ZoneVegetation SetCurrentVegetation(ZoneSystem.ZoneVegetation vegetation)
    {
      CurrentVegetation = vegetation;
      VegetationTwins.Entry(vegetation);
      return vegetation;
    }
    // The loop's first read of m_enable, once per entry. Expand World Data inserts its own tracking at the same place,
    // which leaves the read in place. Should another mod have rewritten the loop, nothing is inserted: the vegetation
    // map and the twin guard then do nothing, instead of the patch failing.
    public static IEnumerable<CodeInstruction> PlaceVegetationSaveCurrent(IEnumerable<CodeInstruction> instructions)
    {
      var enable = AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_enable));
      var codes = instructions.ToList();
      int at = codes.FindIndex(code => code.LoadsField(enable));
      if (at < 0)
      {
        LogWarning("ZoneSystem.PlaceVegetation has no m_enable read to track: the vegetation map and the twin guard are off.");
        return codes;
      }
      // Takes the entry the read is about and leaves it on the stack for the read.
      codes.Insert(at, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ZoneSystemPatch), nameof(SetCurrentVegetation))));
      return codes;
    }


    // Must be named __result (Harmony's reserved name for the original return value), not "result" -
    // ZoneSystem.InsideClearArea's own parameters are (areas, point), so a plain "result" parameter
    // does not bind to anything and Harmony throws "Parameter \"result\" not found" when this postfix
    // is applied (confirmed against 0Harmony.dll's HarmonyManipulator, which matches only "__result").
    // Expand World Data replaces the original with a prefix; postfixes still run after it.
    public static bool InsideClearAreaPostfix(bool __result, Vector3 point)
    {
      if (__result || CurrentVegetation == null) return __result;
      if (Settings.CheckVegetationMap(point, CurrentVegetation)) return true;
      return VegetationTwins.Skip(point);
    }
  }
}

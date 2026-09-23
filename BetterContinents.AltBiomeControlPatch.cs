// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // Harmony side of AltBiomeControl. Every target here is a static [HarmonyPatch] so refcheck verifies it
  // against the real 1.0.15 assemblies (name, overload and parameter names). Each patch is inert unless
  // Better Continents is enabled for the current world, apart from the GetBiomeSector grid clamp, which only
  // acts on a grid whose size is not vanilla's 2048 (Expand World Size resizes it).
  [HarmonyPatch(typeof(AltBiomeWorldData))]
  private class AltBiomeControlPatch
  {
    // AltBiomeWorldData.VerifyBiomeData(World world) - AltBiomeWorldData.cs:72. The single entry point:
    // ZNet.ServerLoadWorld (ZNet.cs:461) and the client's RPC_PeerInfo (ZNet.cs:1124).
    [HarmonyPrefix, HarmonyPatch(nameof(AltBiomeWorldData.VerifyBiomeData))]
    private static void VerifyBiomeDataPrefix(World world) => AltBiomeControl.BeforeVerifyBiomeData(world);

    [HarmonyPostfix, HarmonyPatch(nameof(AltBiomeWorldData.VerifyBiomeData))]
    private static void VerifyBiomeDataPostfix(World world) => AltBiomeControl.AfterVerifyBiomeData(world);

    // AltBiomeWorldData.GenerateBiomePoints(World world) - AltBiomeWorldData.cs:103. A postfix, not a
    // transpiler: Expand World Size transpiles this method's constants, and editing the same IL would race it.
    [HarmonyPostfix, HarmonyPatch(nameof(AltBiomeWorldData.GenerateBiomePoints))]
    private static void GenerateBiomePointsPostfix(World world)
    {
      if (AltBiomeControl.CutoffRadius > 0f && world.m_biomeData != null)
        AltBiomeControl.ApplyCutoff(world.m_biomeData);
    }

    // AltBiomeWorldData.GenerateSectors() - AltBiomeWorldData.cs:150. Replaced only while the planting layer
    // has painted points; the replacement ends by calling GenerateAltBiomes, like vanilla.
    [HarmonyPrefix, HarmonyPatch(nameof(AltBiomeWorldData.GenerateSectors))]
    private static bool GenerateSectorsPrefix(AltBiomeWorldData __instance) =>
      !AltBiomeControl.TryGeneratePlantedSectors(__instance);

    // AltBiomeWorldData.GenerateAltBiomes() - AltBiomeWorldData.cs:301. Called at the end of GenerateSectors
    // (:297) and by vanilla's "genloc alt" command (Terminal.cs:519).
    private static AltBiomeControl.AltBiomeRun? CurrentRun;

    [HarmonyPrefix, HarmonyPatch(nameof(AltBiomeWorldData.GenerateAltBiomes))]
    private static bool GenerateAltBiomesPrefix(AltBiomeWorldData __instance)
    {
      CurrentRun = null;
      if (!AltBiomeControl.WorldEnabled)
        return true;
      CurrentRun = AltBiomeControl.BeginRun(__instance);
      return CurrentRun.RunVanilla;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(AltBiomeWorldData.GenerateAltBiomes))]
    private static void GenerateAltBiomesPostfix(AltBiomeWorldData __instance)
    {
      var run = CurrentRun;
      CurrentRun = null;
      if (run != null)
        AltBiomeControl.EndRun(__instance, run);
    }

    // The overrides edit fields on the session's AltBiome objects, and split-out regions are hidden from the
    // game's lists during the run; both must be put back even if vanilla throws.
    [HarmonyFinalizer, HarmonyPatch(nameof(AltBiomeWorldData.GenerateAltBiomes))]
    private static Exception? GenerateAltBiomesFinalizer(AltBiomeWorldData __instance, Exception? __exception)
    {
      if (__exception != null)
      {
        var run = CurrentRun;
        CurrentRun = null;
        AltBiomeControl.RandomPhase = false;
        AltBiomeControl.RandomExcluded.Clear();
        if (run != null)
        {
          AltBiomeControl.EndRun(__instance, run);
          LogError($"Alt biomes: placement failed ({__exception.Message}); alt-biome settings were restored.");
        }
      }
      return __exception;
    }

    // Both `WorldGenerator.instance.GetSeed()` calls (IL_0005 and IL_00e6 in 1.0.15) become
    // AltBiomeControl.PlacementSeed(instance): same stack shape, and the world seed unless a fixed
    // placement seed is set for a Better Continents world.
    [HarmonyTranspiler, HarmonyPatch(nameof(AltBiomeWorldData.GenerateAltBiomes))]
    private static IEnumerable<CodeInstruction> GenerateAltBiomesTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      var getSeed = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetSeed));
      var placementSeed = AccessTools.Method(typeof(AltBiomeControl), nameof(AltBiomeControl.PlacementSeed));
      int replaced = 0;
      foreach (var instruction in instructions)
      {
        if (getSeed != null && placementSeed != null && instruction.Calls(getSeed))
        {
          instruction.opcode = OpCodes.Call;
          instruction.operand = placementSeed;
          replaced++;
        }
        yield return instruction;
      }
      if (replaced != 2)
        LogWarning($"Alt biomes: expected 2 GetSeed calls in GenerateAltBiomes, replaced {replaced}; a fixed placement seed may not fully apply.");
    }
  }

  // BiomeSector.CanAddModifier(AltBiome modifier) - BiomeSector.cs:96. Only called from GenerateAltBiomes (:325).
  // Refuses planted regions while the game's random placement runs, and replaces vanilla's test while a BC
  // eligibility option (neighbour fix, mean sector height, minimum thickness) is on. Inert on other worlds.
  [HarmonyPatch(typeof(BiomeSector), nameof(BiomeSector.CanAddModifier))]
  private class BiomeSectorCanAddModifierPatch
  {
    private static bool Prefix(BiomeSector __instance, AltBiome modifier, ref bool __result)
    {
      var decision = AltBiomeControl.CanAddModifierOverride(__instance, modifier);
      if (decision == null)
        return true;
      __result = decision.Value;
      return false;
    }
  }

  [HarmonyPatch(typeof(WorldGenerator))]
  private class WorldGeneratorBiomeSectorPatch
  {
    // WorldGenerator.GetBiomeSector(float wx, float wy, bool clamp) - WorldGenerator.cs:835.
    [HarmonyPostfix, HarmonyPatch(nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
    private static void GetBiomeSectorWorldPostfix(WorldGenerator __instance, float wx, float wy, ref BiomeSector __result)
    {
      if (!AltBiomeControl.FallbackActive || wx * wx + wy * wy <= AltBiomeControl.FallbackRadiusSq)
        return;
      __result = AltBiomeControl.PlainSector(__instance.GetBiome(wx, wy));
    }

    // WorldGenerator.GetBiomeSector(Vector3 worldPos, bool clamp) - WorldGenerator.cs:840.
    [HarmonyPostfix, HarmonyPatch(nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
    private static void GetBiomeSectorVectorPostfix(WorldGenerator __instance, Vector3 worldPos, ref BiomeSector __result)
    {
      if (!AltBiomeControl.FallbackActive || worldPos.x * worldPos.x + worldPos.z * worldPos.z <= AltBiomeControl.FallbackRadiusSq)
        return;
      __result = AltBiomeControl.PlainSector(__instance.GetBiome(worldPos.x, worldPos.z));
    }

    // WorldGenerator.GetBiomeSector(int gridx, int gridy, bool clamp) - WorldGenerator.cs:845 clamps to the
    // literal 2047 whatever the grid size. Expand World Size resizes the grid but skips its own fix of this
    // method whenever Better Continents is enabled for the world, leaving lookups clamped into the wrong
    // sector (bigger grid) or out of range (smaller grid, EWS issue #26). Clamp to the real size instead. A
    // vanilla-sized grid runs the original untouched.
    [HarmonyPrefix, HarmonyPatch(nameof(WorldGenerator.GetBiomeSector), typeof(int), typeof(int), typeof(bool))]
    private static bool GetBiomeSectorGridPrefix(WorldGenerator __instance, int gridx, int gridy, ref BiomeSector __result)
    {
      var data = __instance.m_world?.m_biomeData;
      if (data == null || data.Size == AltBiomeControl.VanillaGridSize)
        return true;
      __result = AltBiomeControl.SectorAtGrid(data, gridx, gridy);
      return false;
    }
  }
}

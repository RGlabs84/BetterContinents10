// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // The water of a zone follows its terrain.
  //
  // Each 64 m zone has a WaterVolume whose m_heightmap is the zone's terrain. It reads that terrain's four corner ocean
  // depths once, in Start (WaterVolume.cs:84-88): DetectWaterDepth (:100) turns them into m_normalizedDepth, each over 10 m
  // and clamped to 0..1, and into m_oneDepth when all four are equal, and SetupMaterial (:150) writes them to the water's
  // "_depth" shader array, which is what makes water look deep (dark, reflective, tinted by the sky) or shallow (clear,
  // the sea bed showing). Depth (:232) hands the same numbers to GetWaterSurface (:168), which sizes the waves by them, so
  // they are physics as well as looks. The terrain is never asked again, and DetectWaterDepth never resets m_oneDepth.
  //
  // Heightmap.Regenerate (Heightmap.cs:348) builds the terrain in place, and on a full rebuild refreshes the terrain's own
  // corner depths and its material's "_depth" (UpdateCornerDepths, :367), never the water's. In vanilla a zone's terrain
  // is rebuilt after its water started only by a Poke, such as a terrain edit whose TerrainComp loads after the zone (the
  // heightmap's first Regenerate is in OnEnable, :244, before the water's Start; TerrainComp.CheckLoad pokes it,
  // TerrainComp.cs:208). Better Continents rebuilds the loaded terrain whenever a live setting changes it
  // (GameUtils.ResetZones for the "bc" settings and "bc reset", RegenerateLoadedTerrain in Patcher.cs for the biome
  // precision), and a zone whose corner depths moved kept the depth its water started with: a 64 m square of sea, with
  // straight edges, looked deep among shallow-looking water or the other way round, and its waves were sized by the old
  // depth.
  //
  // So after a zone's terrain is rebuilt, its water is given what a freshly loaded zone's gets: when the corner depths no
  // longer match what the water holds, m_oneDepth is reset and DetectWaterDepth and SetupMaterial run again. Running
  // SetupMaterial again also runs the patches on it, exactly as they run when a zone loads: Better Continents' own (the
  // water's edge, WorldSizeHelper.SetupMaterialPrefix) and any other mod's.
  //
  // A new zone's first Regenerate runs in Heightmap.OnEnable, after its water's OnEnable has put the water in
  // WaterVolume.Instances (the zone prefab holds its Water before its Terrain) but before the water's Start. That water has
  // read nothing yet and is left to its Start, which reads the terrain as it is by then; a server's ghost zones, destroyed
  // before their Start, never make a water material either.
  //
  // Heightmap.Regenerate() is public, and every rebuild of a zone's terrain goes through it (OnEnable, LateUpdate,
  // CustomLateUpdate, ForceGenerateAll, and Poke when it is not delayed). Bound once by PatchAll at load and never
  // re-patched, and inert unless Better Continents is on for the world: a vanilla world keeps vanilla's water.
  [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
  internal static class WaterDepthPatch
  {
    // Regenerate runs for every stroke of a terrain edit, so while the depths match this allocates nothing, logs nothing
    // and changes nothing. The volumes are walked by index, as SetupMaterial belongs to the game and to every mod that
    // patches it, and none of them should be able to invalidate an enumerator.
    private static void Postfix(Heightmap __instance)
    {
      if (!Settings.EnabledForThisWorld)
        return;
      var volumes = WaterVolume.Instances;
      for (int i = 0; i < volumes.Count; i++)
      {
        // An exception would reach the caller of Regenerate (Heightmap.LateUpdate, a terrain edit's Poke) after the
        // rebuild itself had finished, and cut that short. Each volume has its own try, so one that fails does not stop
        // the zone's other water.
        try
        {
          var water = volumes[i];
          if (water != null && Stale(water.m_heightmap == __instance, water.m_oneDepth, __instance.GetOceanDepth(), water.m_normalizedDepth))
          {
            // DetectWaterDepth only ever sets m_oneDepth (WaterVolume.cs:111, :120), so a zone that was level and no
            // longer is would go on answering Depth with its old single depth (:234).
            water.m_oneDepth = -1f;
            water.DetectWaterDepth();
            water.SetupMaterial();
          }
        }
        catch (Exception e)
        {
          Failed(e);
        }
      }
    }

    // Whether a water volume has to read its terrain's corner depths again: it is this terrain's water (own), it has read
    // them before, and what it holds is not what DetectWaterDepth would make of them now.
    internal static bool Stale(bool own, float oneDepth, float[] oceanDepth, float[] normalizedDepth) =>
      own && !NotStarted(oneDepth, normalizedDepth) && !DepthsMatch(oceanDepth, normalizedDepth);

    // A volume whose Start has not run holds what it was made with: m_oneDepth -1 and four zeros (WaterVolume.cs:12, :8).
    // DetectWaterDepth never leaves that behind, since four equal depths set m_oneDepth (:109-112).
    internal static bool NotStarted(float oneDepth, float[] normalizedDepth) =>
      oneDepth == -1f && normalizedDepth[0] == 0f && normalizedDepth[1] == 0f && normalizedDepth[2] == 0f && normalizedDepth[3] == 0f;

    // Whether the depths a water volume holds (its m_normalizedDepth) are what DetectWaterDepth would make of these ocean
    // depths (a heightmap's GetOceanDepth): each over 10 m and clamped to 0..1, as WaterVolume.cs:105-108 does it, and
    // compared with Single.Equals, as the game's own test for a level zone does (:109). Equals counts a NaN as equal to
    // itself, so a depth that is not a number cannot make the water start over at every call.
    internal static bool DepthsMatch(float[] oceanDepth, float[] normalizedDepth)
    {
      for (int i = 0; i < 4; i++)
      {
        if (!Mathf.Clamp01(oceanDepth[i] / 10f).Equals(normalizedDepth[i]))
          return false;
      }
      return true;
    }

    private static bool LoggedFailure;

    internal static void Failed(Exception e)
    {
      if (LoggedFailure)
        return;
      LoggedFailure = true;
      LogError($"Water depth: giving a terrain zone's water the depth of its rebuilt terrain failed ({e.GetType().Name}: {e.Message}); that water may look and move as at its old depth until its terrain is rebuilt again.");
    }
  }
}

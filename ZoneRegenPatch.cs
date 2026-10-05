// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using HarmonyLib;

namespace BetterContinents;

// Harmony side of ZoneRegen: three small patches, each a no-op unless a regeneration is under way (or has left zones emptied and not
// finished, or objects in the destroy queue), which only a debug-mode command starts. Every target is a static [HarmonyPatch] so
// refcheck verifies it against the installed game assemblies.
[HarmonyPatch]
internal static class ZoneRegenPatch
{
  // ZNet.SaveWorld(bool sync) - ZNet.cs:1744. It copies the world's objects (ZDOMan.PrepareSave, :1755) and the zone system's state
  // (ZoneSystem.PrepareSave, :1756) before the save thread starts, so what must be whole has to be whole before it runs.
  [HarmonyPrefix, HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
  private static void SaveWorldPrefix() => ZoneRegen.BeforeSave();

  // ZoneSystem.PrepareSave() - ZoneSystem.cs:983. It copies m_generatedZones and the location instances for the save thread; the copies are
  // what is written, so they are edited here and the live world is not.
  [HarmonyPostfix, HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PrepareSave))]
  private static void PrepareSavePostfix(ZoneSystem __instance) => ZoneRegen.HidePendingFromSave(__instance);

  // Piece.SetCreator(long, PlatformUserID) - Piece.cs:429. Player.PlacePiece is its only caller (Player.cs:3102): a piece the player at
  // this machine has just placed.
  [HarmonyPostfix, HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
  private static void SetCreatorPostfix(Piece __instance) => ZoneRegen.NotePlaced(__instance);
}

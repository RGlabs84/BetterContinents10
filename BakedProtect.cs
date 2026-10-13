// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownUsables.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// A protected piece of the layer: a real piece (a door, a station, whatever an in-game bake adopted) whose ZDO says bc_protect. BakedServer
// writes it for every palette entry with the Protect flag and an in-game bake's `town` mode for the pieces it adopts. Nothing damages such a
// piece and the hammer cannot take it, on every machine (the damage a piece takes is applied by whoever owns it).
//
//  * Damage. Every kind of damage to a building reaches the piece through WearNTear.ApplyDamage: hits (RPC_Damage), rain, a lost support,
//    snow, an event. A prefix there answers "nothing happened". It reads the ZDO at each call, so a piece that is protected while players
//    have it loaded is protected at once.
//  * Removal. Piece.CanBeRemoved reads the ZDO at each call too (Player.RemovePiece asks it after the piece's own m_canBeRemoved).
//  * What WearNTear.Awake reads later, and the hammer reads first: no rain or support wear, no burning, m_canBeRemoved off. Set as a piece
//    that is already protected wakes (a piece loaded from the world, a client's copy of one), and by BakedServer as it seeds one (its
//    Awake has run by then, before the keys were on the ZDO: the same four fields, on the instance).
//
// Exactly four fields are set and no others: WearNTear.m_noRoofWear, m_noSupportWear and m_burnable, and Piece.m_canBeRemoved. The
// stations' no-roof rule (CraftingStation.CheckUsable, and Smelter.m_requiresRoof for a spinning wheel) is the compiler's own, keyed on its
// tags, and stays there (build spec 0.1, item 2).
//
// Protection holds while the machine has a layer: a world whose layer was dropped (bc_bake drop) leaves its pieces' keys where they are, and
// nothing reads them any more, so an admin can take their own chest away.
internal static class BakedProtect
{
  /// <summary>The ZDO key (a bool, stored as an int).</summary>
  internal static readonly int Key = "bc_protect".GetStableHashCode();

  /// <summary>Whether protection applies on this machine: it has a layer. (A field, for the offline tests to stand in for.)</summary>
  internal static Func<bool> LayerPresent = () => BakedLayerStore.Current != null;

  internal static bool IsProtected(ZDO? zdo) => zdo != null && LayerPresent() && zdo.GetBool(Key);

  // (`is not null`, not Unity's `!= null`: the piece being asked about is alive, or its ZDO says nothing.)
  internal static bool IsProtected(ZNetView? view) => view is not null && IsProtected(view.GetZDO());

  /// <summary>What a prefix does for a piece: the game's own method does not run (and the result says no) when the piece is protected.</summary>
  internal static bool Deny(ZNetView? view, ref bool result)
  {
    if (!IsProtected(view))
      return true;
    result = false;
    return false;
  }

  /// <summary>What a protected piece's components keep (the four fields; see above), one WearNTear.</summary>
  internal static void Apply(WearNTear wear)
  {
    wear.m_noRoofWear = false;
    wear.m_noSupportWear = false;
    wear.m_burnable = false;
  }

  /// <summary>... and one Piece.</summary>
  internal static void Apply(Piece piece) => piece.m_canBeRemoved = false;

  /// <summary>Every WearNTear and Piece under a piece's object (a piece may keep its Piece in a child, or have several).</summary>
  internal static void Apply(GameObject piece)
  {
    foreach (var wear in piece.GetComponentsInChildren<WearNTear>(true))
      Apply(wear);
    foreach (var part in piece.GetComponentsInChildren<Piece>(true))
      Apply(part);
  }

  // A protected piece loaded from the world is protected as it wakes. WearNTear.Awake has its ZNetView and its ZDO by then (it does
  // nothing without them), so the key can be read.
  [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Awake))]
  internal static class AwakePatch
  {
    private static void Postfix(WearNTear __instance)
    {
      if (IsProtected(__instance.m_nview))
        Apply(__instance.gameObject);
    }
  }

  // Damage of any kind (see the top of this file): none.
  [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.ApplyDamage))]
  internal static class DamagePatch
  {
    private static bool Prefix(WearNTear __instance, ref bool __result) => Deny(__instance.m_nview, ref __result);
  }

  // Removal: no. (A piece the layer owns would come back at the next reconciliation, and one an admin adopted is theirs to unbake.)
  [HarmonyPatch(typeof(Piece), nameof(Piece.CanBeRemoved))]
  internal static class RemovePatch
  {
    private static bool Prefix(Piece __instance, ref bool __result) => Deny(__instance.m_nview, ref __result);
  }
}

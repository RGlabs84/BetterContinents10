// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using UnityEngine;

namespace BetterContinents;

// The clearing mask in the game (BUILD-SPEC-TARN.md 2.11): where the world's mask is marked, the game's own trees, rocks and bushes grow none. Tarn did it
// with a prefix on ZoneSystem.PlaceVegetation that built one ClearArea per marked pixel near the zone (by reflection: the class is private) and took them
// out again in a finalizer [T: Clearing.cs, Patch_PlaceVegetation]. Here it is the mask's data, read by a postfix on ZoneSystem.InsideClearArea, which only
// PlaceVegetation calls (ZoneSystem.cs:1522, 1589), the last test before a plant is placed. The game's own placement runs on the machine that runs the
// world, so this does too; a client never asks. It names no ClearArea, builds no object and edits no list.
//
// It decides what Tarn's areas decided: the test comes after the spot's position, rotation and scale were rolled, so for most entries it removes only what
// would have stood there and moves nothing else. (An entry with a ground-tilt chance rolls one more number after the test for a spot it keeps, and an entry
// with forced placement makes up a cleared spot elsewhere: the same holds for Tarn's areas and for the game's own location clear areas.)
//
// The postfix is a toggle of its own (Patcher.ClearingToggles), on for every world with a mask, a GameTerrain world included, and off while the Tarn mod is
// installed (TarnCompat). It runs after Expand World Data's prefix (which replaces the original) and with the vegetation map's and the baked layer's postfixes:
// the answer is the OR of them.
internal static class ClearVegetation
{
  // The postfix on ZoneSystem.InsideClearArea(List<ClearArea>, Vector3), of the shape of BakedVegetation's: true when the game says so already or the point is
  // inside the clear area of a marked pixel. The parameter is named __result: Harmony matches only that.
  public static bool InsideClearAreaPostfix(bool __result, Vector3 point)
  {
    if (__result)
      return true;
    var mask = BetterContinents.Settings.Clearing;
    if (mask == null)
      return false;
    bool covered;
    try
    {
      covered = mask.Covers(point.x, point.z);
    }
    catch (LakeFormatException e)
    {
      // The bits do not open (BeforeVerifyBiomeData would have stopped the load; a mask replaced since is not checked until now): once in the log, no more.
      if (!failedOnce)
      {
        failedOnce = true;
        BetterContinents.LogError("Clearing: the mask cannot be read, so vegetation is placed as the game places it: " + e.Message);
      }
      return false;
    }
    if (covered && !ReferenceEquals(provedFor, mask))
      Prove(mask, point);
    return covered;
  }

  private static volatile bool failedOnce;
  private static volatile object? provedFor;

  // The one line that proves the clearing ran (a silent success and a silent no-op look the same) [T: Clearing.cs, "clearing: first zone with marked ground"]:
  // the first spot of each mask that was kept clear.
  private static void Prove(ClearMask mask, Vector3 point)
  {
    provedFor = mask;
    BetterContinents.Log($"Clearing: the first spot kept clear of vegetation is at ({point.x:0}, {point.z:0}), in the zone ({Math.Floor((point.x + 32f) / 64f):0}, {Math.Floor((point.z + 32f) / 64f):0})");
  }

  // A new mask (or a new world) is announced afresh.
  internal static void Forget()
  {
    provedFor = null;
    failedOnce = false;
  }

  // The world is loading (AltBiomeControl.BeforeVerifyBiomeData): the machine that runs the world opens the mask's bits now and says what the mask is, so
  // that a mask that does not open stops the load here. Null when all is well (or when this machine does not place vegetation, or the Tarn mod does the clearing);
  // else what is wrong, which is also kept in settings.ClearingError.
  internal static string? OpenAtLoad(BetterContinents.BetterContinentsSettings settings)
  {
    Forget();
    if (settings.Clearing is not { } mask || TarnCompat.Installed || !BakedTransfer.RunsWorld())
      return null;
    try
    {
      mask.Verify();
      BetterContinents.Log("Clearing: mask " + mask.Summary());
      return null;
    }
    catch (LakeFormatException e)
    {
      settings.ClearingError = $"the clearing mask in the world's settings cannot be read ({e.Message})";
      BetterContinents.LogError("Clearing: " + settings.ClearingError);
      return settings.ClearingError;
    }
  }
}

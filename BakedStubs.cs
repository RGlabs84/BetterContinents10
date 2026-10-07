// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using UnityEngine;

namespace BetterContinents;

// STUBS, slice A's day 0: the two classes of slice B that Patcher's layer toggles and BakedApi / WorldExport call. They answer as a world
// without a layer's ground and footprints would (the game's own result, no footprint). Slice B writes BakedVegetation.cs and BakedGround.cs
// with these members, and deletes this file when it merges. The members and their parameter names are the contract: Harmony binds the
// postfixes by name.

// The vegetation mask (spec 7.4).
internal static class BakedVegetation
{
  // A postfix on ZoneSystem.InsideClearArea (private), of the shape of ZoneSystemPatch.InsideClearAreaPostfix: true when the point's zone has
  // zone flag 16, or its 4 m cell is set in the zone's effective mask.
  public static bool InsideClearAreaPostfix(bool __result, Vector3 point) => __result;

  // Whether a point is inside a zone with flag 16 or a set cell of its effective mask (BakedPlacements.IsFootprint).
  public static bool IsFootprint(float x, float z) => false;
}

// The layer's 1 m ground (spec 8.1).
internal static class BakedGround
{
  // A passthrough postfix on WorldGenerator.GetBiomeHeight(Heightmap.Biome, float, float, out Color, bool, bool), the lowest priority, after
  // Expand World Data's: the result becomes lerp(result, ground, weight).
  public static float GetBiomeHeightPostfix(float __result, float wx, float wy) => __result;

  // The postfix steps aside until what this returns is disposed (a world export samples heights without the layer's ground, spec 3.3).
  public static IDisposable Pause() => Nothing.Instance;

  private sealed class Nothing : IDisposable
  {
    public static readonly Nothing Instance = new();

    public void Dispose()
    {
    }
  }
}

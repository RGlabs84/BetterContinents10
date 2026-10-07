// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;

namespace BetterContinents;

// A change of a layer, as the server sends it (spec 5.3): the new header, producer, palette, registry and zone index, and the blocks of the
// zones that changed, so a client that has the old layer can lay out the new one byte for byte. Making one is deterministic (the same two
// layers give the same bytes); applying one to a layer it was not made for, or a damaged one, throws.
internal static class BakedPatch
{
  // The patch that turns `from` into `to`. `changed` is what LayerEdit.Build said; every zone whose block differs between the two layers
  // is in the patch whether it is named or not.
  public static byte[] Make(BakedLayer from, BakedLayer to, ZoneKey[] changed) => throw new NotImplementedException();

  // The layer `patch` makes of `from`. Throws BakedFormatException when the patch is damaged or does not fit `from`; the caller then asks
  // for the whole layer. The id of the result is for the caller to compare with the one the server sent.
  public static BakedLayer Apply(BakedLayer from, byte[] patch) => throw new NotImplementedException();

  // Apply without the exception: false and the reason when the patch does not make the layer with that id.
  public static bool TryApply(BakedLayer from, byte[] patch, string expectedId, out BakedLayer? layer, out string problem) =>
    throw new NotImplementedException();
}

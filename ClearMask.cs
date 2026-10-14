// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;

namespace BetterContinents;

// THE CONTRACT of the clearing mask (BUILD-SPEC-TARN.md 2.1, 2.11), the data agent's (A) to fill in: a bitmask over the world's ground, square, Size cells a
// side, spanning -Extent..+Extent in x and z, where a set bit means "no tree, rock or bush here". Immutable; the bits are decoded lazily and only on the
// machine that runs the world.
internal sealed class ClearMask
{
  public int Size { get; }
  public float Extent { get; }
  public double Km2 { get; }

  // A: from and to the bytes of Tarn's ".clear" file ("TARNCLR1", a 16-byte header, the bit rows).
  public static ClearMask FromTarnFile(byte[] file) => throw new NotImplementedException("A");
  public byte[] ToTarnFile() => throw new NotImplementedException("A");

  // A: blob 73 (DataKey.Clearing), the first int32 the format version.
  public byte[] ToBlock() => throw new NotImplementedException("A");
  public static ClearMask FromBlock(byte[] block) => throw new NotImplementedException("A");

  // A: the test of the InsideClearArea postfix: is the point marked.
  public bool Covers(double x, double z) => throw new NotImplementedException("A");
}

// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;

namespace BetterContinents;

// THE CONTRACT of the lakes work (BUILD-SPEC-TARN.md 2.1), written by the lead before the data agent (A) and the engine agent (B) started: the types the two
// share. A fills in the bodies marked "A:"; B reads Lakes, DepthScale and the Lake records and needs nothing else of A to begin.

// One lake of the world's list: where Tarn's flood starts (X, Z, Level), the box it may not leave (MinX..MaxZ), and the depth of its water over the ground
// at the deepest. Level is the water's height in metres above the world's zero; the sea is at 30 m.
internal sealed record Lake(string Name, float X, float Z, float Level, float MinX, float MinZ, float MaxX, float MaxZ, float Depth);

// The world's lakes, immutable: a change is a new set (settings.Lakes is swapped whole). Revision counts the changes of this session and is not saved.
internal sealed class LakeSet
{
  public static readonly LakeSet Empty = new(Array.Empty<Lake>(), DefaultDepthScale, 0);

  public const float DefaultDepthScale = 0.5f;
  public const int MaxLakes = 5000;

  public IReadOnlyList<Lake> Lakes { get; }
  public float DepthScale { get; }
  public int Revision { get; }

  public LakeSet(IReadOnlyList<Lake> lakes, float depthScale, int revision)
  {
    Lakes = lakes;
    DepthScale = depthScale;
    Revision = revision;
  }

  public bool IsEmpty => Lakes.Count == 0;

  // A: Tarn's text format (T: LakeFile.cs), one lake a line, with a warning naming the line for one that does not read.
  public static LakeSet ParseLines(IEnumerable<string> lines, Action<string> warn) => throw new NotImplementedException("A");

  // A: the lines again, the numbers in the round-trip form.
  public IEnumerable<string> ToLines() => throw new NotImplementedException("A");

  // A: blob 72 (DataKey.Lakes), the first int32 the format version.
  public byte[] ToBlock() => throw new NotImplementedException("A");
  public static LakeSet FromBlock(byte[] block) => throw new NotImplementedException("A");
}

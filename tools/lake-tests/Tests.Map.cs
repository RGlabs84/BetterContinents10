// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// A lake on the map (LakeMap.cs, Tarn's MapLakes): the flood over the map's own pixels with the height of a sea as deep as the lake, and the half floats the map's
// height texture holds (RHalf), compared with System.Half over every bit pattern.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  public static void MapHalfFloatsTest()
  {
    Section("map: the height texture's half floats are IEEE binary16");
    int wrong = 0, wrongBack = 0;
    for (int bits = 0; bits < 65536; bits++)
    {
      float f = LakeMapPaint.HalfToFloat((ushort)bits);
      var half = BitConverter.UInt16BitsToHalf((ushort)bits);
      float want = (float)half;
      if (!(float.IsNaN(f) && float.IsNaN(want)) && f != want)
        wrong++;
      if (!float.IsNaN(f))
      {
        ushort back = LakeMapPaint.FloatToHalf(f);
        // -0 and +0 are different bits that compare equal; every other half comes back as itself
        if (back != (ushort)bits)
          wrongBack++;
      }
    }
    C(wrong == 0, $"HalfToFloat equals System.Half for all 65,536 patterns ({wrong} differ)");
    C(wrongBack == 0, $"FloatToHalf gives every half back ({wrongBack} differ)");
    var rng = new Rng(8080);
    int differ = 0;
    for (int n = 0; n < 200000; n++)
    {
      // heights as the map holds them: -50 .. 1000, and some stranger ones
      float f = n % 5 == 0 ? (float)(rng.Range(-1, 1) * Math.Pow(10, rng.Range(-9, 5))) : (float)rng.Range(-50, 1000);
      ushort got = LakeMapPaint.FloatToHalf(f), want = BitConverter.HalfToUInt16Bits((Half)f);
      if (got != want)
        differ++;
    }
    C(differ == 0, $"FloatToHalf rounds like System.Half on 200,000 random floats ({differ} differ)");
    C(LakeMapPaint.FloatToHalf(30f) == 0x4F80 && LakeMapPaint.HalfToFloat(0x4F80) == 30f && LakeMapPaint.FloatToHalf(1e6f) == 0x7C00 && LakeMapPaint.FloatToHalf(float.NaN) == 0x7E00, "30 m is 0x4F80, the huge is infinity");
  }

  // A map of size x size pixels of px metres: the height of a pixel from a function of its world position; the paint written to arrays.
  private sealed class MapRig
  {
    public readonly int Size;
    public readonly float Px;
    public readonly float[] Height;
    public readonly bool[] Cleared;
    public MapRig(int size, float px, Func<float, float, float> ground)
    {
      Size = size;
      Px = px;
      Height = new float[size * size];
      Cleared = new bool[size * size];
      for (int i = 0; i < size; i++)
        for (int j = 0; j < size; j++)
          Height[i * size + j] = ground((j - size / 2) * px + px / 2f, (i - size / 2) * px + px / 2f);
    }
    public int Paint(Lake lake) => LakeMapPaint.Paint(lake, Size, Px, 30f, k => Height[k], (k, v) => Height[k] = v, k => Cleared[k] = true);
    public int Painted => Cleared.Count(x => x);
  }

  public static void MapPaintTest()
  {
    Section("map: a lake becomes pixels of sea as deep as the lake, with no forest");
    // a 256 x 256 map of 10 m pixels (25.6 km): a basin at 40 m (a bowl of radius 800 m, rim 90 m) and a second one that is not under the lake's level
    Func<float, float, float> ground = (x, z) =>
    {
      float r = (float)Math.Sqrt((x - 300f) * (x - 300f) + (z + 200f) * (z + 200f));
      return 40f + 50f * Math.Min(1f, (r / 800f) * (r / 800f)) + (x < -3000f ? -20f : 0f);
    };
    var rig = new MapRig(256, 10f, ground);
    var lake = new Lake("m", 300f, -200f, 60f, -600f, -800f, 1200f, 500f, 20f);
    var before = (float[])rig.Height.Clone();
    int n = rig.Paint(lake);
    C(n > 1000 && rig.Painted == n, $"pixels painted and cleared agree: {n}");
    bool allUnder = true, depthOk = true, outsideSame = true;
    for (int k = 0; k < rig.Height.Length; k++)
    {
      if (rig.Cleared[k])
      {
        if (!(rig.Height[k] < 30f)) allUnder = false;
        if (Math.Abs(rig.Height[k] - (30f - Math.Max(1f, 60f - before[k]))) > 1e-4) depthOk = false;
        if (!(before[k] < 60f)) allUnder = false;
      }
      else if (rig.Height[k] != before[k])
        outsideSame = false;
    }
    C(allUnder && depthOk && outsideSame, "each painted pixel was under the lake's level, is now under the sea by the lake's depth there (at least 1 m); the others are as they were");
    // a pixel that was a metre under the lake's level gets 1 m of depth, one 20 m under 20 m
    int mid = (128 + (int)(-200f / 10f)) * 256 + 128 + 30;
    C(Math.Abs(rig.Height[mid] - (30f - Math.Max(1f, 60f - before[mid]))) < 1e-4, "the depth follows the ground");

    // the lake is not painted twice: a second paint finds only sea (< 30 m) and floods nothing beyond its start
    int again = rig.Paint(lake);
    C(again <= 1, $"a second paint of the same lake paints at most the pixel it starts from ({again})");

    // a lake smaller than a pixel, a lake whose point is high ground, a lake outside the map
    var rig2 = new MapRig(256, 10f, (x, z) => 70f);
    C(rig2.Paint(new Lake("s", 0, 0, 60f, -20, -20, 20, 20, 1f)) == 0 && rig2.Painted == 0, "ground above the level everywhere: nothing painted");
    var rig3 = new MapRig(256, 10f, ground);
    C(rig3.Paint(new Lake("o", 50000f, 50000f, 60f, 49000, 49000, 51000, 51000, 5f)) == 0, "a lake outside the map paints nothing");

    // the lake does not run into the sea: ground under 30 m next to it is left alone
    var rig4 = new MapRig(64, 10f, (x, z) => x < 0f ? 20f : 40f);
    int m4 = rig4.Paint(new Lake("c", 150f, 0f, 60f, 0f, -300f, 300f, 300f, 10f));
    bool sea = true;
    for (int i = 0; i < 64; i++)
      for (int j = 0; j < 64; j++)
        if (j < 32 && rig4.Cleared[i * 64 + j]) sea = false;
    C(m4 > 0 && sea && rig4.Height.Where((h, k) => k % 64 < 32).All(h => h == 20f), "a lake beside the sea paints none of the sea's pixels");

    // the point may sit a pixel off the lowest ground (the 5 x 5 search)
    var rig5 = new MapRig(64, 10f, (x, z) => (Math.Abs(x - 20f) < 10f && Math.Abs(z) < 10f) ? 70f : 40f);
    C(rig5.Paint(new Lake("p", 20f, 0f, 60f, -200f, -200f, 200f, 200f, 5f)) > 100, "a point on a pixel of high ground starts from the lowest pixel within two");
  }
}

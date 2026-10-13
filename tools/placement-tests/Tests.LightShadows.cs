// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// Light Shadows (BakedLightShadows): at most N lit copies cast shadows. The ranking is a pure function of the copies' places, which of them could cast
// a shadow, the eye and N; the time it is made at is another. The Unity side (LightLod, Light.shadows) is the game's and is proved in phase Q.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static void LightShadowsTest()
  {
    Section("light shadows: the nearest N copies");
    var eye = new Vector3(10f, 2f, -4f);
    var at = new List<Vector3> { new(11, 2, -4), new(50, 2, -4), new(10, 2, 10), new(-30, 2, -4), new(10, 40, -4), new(10.5f, 2, -4) };
    var all = Enumerable.Repeat(true, at.Count).ToList();
    string Picked(bool[] flags) => string.Join(",", flags.Select((f, i) => f ? i : -1).Where(i => i >= 0));
    C(Picked(BakedLightShadows.Nearest(at, all, eye, 0)) == "", "N = 0: none casts");
    C(Picked(BakedLightShadows.Nearest(at, all, eye, -3)) == "", "a negative N is none");
    C(Picked(BakedLightShadows.Nearest(at, all, eye, 1)) == "5", "N = 1: the nearest copy (0.5 m)");
    C(Picked(BakedLightShadows.Nearest(at, all, eye, 3)) == "0,2,5", "N = 3: 0.5 m, 1 m and 14 m");
    C(Picked(BakedLightShadows.Nearest(at, all, eye, 6)) == "0,1,2,3,4,5" && Picked(BakedLightShadows.Nearest(at, all, eye, 50)) == "0,1,2,3,4,5", "N as many as the copies, or more: all");
    // A copy none of whose lights could cast a shadow takes no place, and is never picked.
    var eligible = new List<bool> { false, true, true, true, true, false };
    C(Picked(BakedLightShadows.Nearest(at, eligible, eye, 2)) == "2,4", "copies that could not cast take no place: the nearest two of the others (14 m and 38 m)");
    C(Picked(BakedLightShadows.Nearest(at, eligible, eye, 9)) == "1,2,3,4", "and with room for all, only the eligible are picked");
    // Equal distances: by place in the list, the same every time.
    var tie = new List<Vector3> { new(5, 0, 0), new(-5, 0, 0), new(0, 5, 0), new(0, 0, 5) };
    var t = Enumerable.Repeat(true, 4).ToList();
    C(Picked(BakedLightShadows.Nearest(tie, t, Vector3.zero, 2)) == "0,1" && Picked(BakedLightShadows.Nearest(tie, t, Vector3.zero, 2)) == "0,1", "four copies 5 m away: the first two in the list, every time");
    // NaN is far, not a crash.
    var nan = new List<Vector3> { new(float.NaN, 0, 0), new(3, 0, 0), new(1, 0, 0) };
    C(Picked(BakedLightShadows.Nearest(nan, Enumerable.Repeat(true, 3).ToList(), Vector3.zero, 2)) == "1,2", "a place that is not a number is the farthest");
    // Against a brute-force ranking on a few hundred places, many N.
    var rng = new System.Random(7);
    var places = Enumerable.Range(0, 300).Select(_ => new Vector3((float)rng.NextDouble() * 200 - 100, (float)rng.NextDouble() * 20, (float)rng.NextDouble() * 200 - 100)).ToList();
    var able = Enumerable.Range(0, 300).Select(i => i % 7 != 0).ToList();
    bool agrees = true;
    foreach (int n in new[] { 1, 2, 4, 8, 32, 100, 256 })
    {
      var want = Enumerable.Range(0, 300).Where(i => able[i]).OrderBy(i => (places[i] - eye).sqrMagnitude).ThenBy(i => i).Take(n).ToHashSet();
      var got = BakedLightShadows.Nearest(places, able, eye, n);
      agrees &= got.Count(f => f) == want.Count && Enumerable.Range(0, 300).All(i => got[i] == want.Contains(i));
    }
    C(agrees, "300 copies, 7 values of N: the same as sorting by distance and taking the first N");

    Section("light shadows: when the ranking is made");
    C(!BakedLightShadows.Due(10.1f, 10f, newCopy: false, limitChanged: false) && !BakedLightShadows.Due(10.2f, 10f, false, false), "not every frame: not within 0.25 s");
    C(BakedLightShadows.Due(10.25f, 10f, false, false), "0.25 s after the last");
    C(!BakedLightShadows.Due(10.05f, 10f, newCopy: true, limitChanged: false) && BakedLightShadows.Due(10.1f, 10f, true, false), "a copy made since brings it forward, to 0.1 s after the last");
    C(BakedLightShadows.Due(10.001f, 10f, false, limitChanged: true), "a change of the setting is at once");
    C(BakedLightShadows.RankInterval == 0.25f && BakedLightShadows.RankGap == 0.1f, "the two intervals");

    Section("light shadows: the setting");
    var s = SettingsSchema.BakedLightShadows;
    C(s.Key == "Light Shadows" && s.Default == 4 && s.Limits == (0, 32) && s.Scope == SettingScope.Live && s.Section == "10 BetterContinents.BakedPlacements", "Light Shadows: 4, 0 to 32, read live, in section 10");
    C(s.Description.Contains("Light Distance") && s.Description.Contains("0 = no copy casts any"), "its description says what it counts and what 0 does");
  }
}

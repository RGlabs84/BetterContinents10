// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterContinents;

// ONLY THE NEAREST "LIGHT COUNT" BAKED LIGHTS BURN (section 9.6).
//
// A lit copy (BakedCopies) is a real lit prefab: its Light is on, its flames and sparks run, its sound plays. A real town's torches have no fuel and
// are unlit; a baked one's lit copy always burns, and a street of them costs what a street of lights does: in phase F (run f6, a user's twelve
// blueprint buildings, 15,527 pieces, the Graphics Overdrive mod's point light limit off) a baked house ran at 34.6 fps with its lights off and
// at 23.6 fps with the 92 lit copies near it standing, no better than the real town (22.9).
//
// So the setting Light Count (0 to 128, default 24) lets only the nearest N Copy records have a lit copy, across every zone, within Light Distance;
// every other Copy record is DRAWN UNLIT, by instancing, as its prefab's intact look (BakedKind.Draws, the same harvest as a Static of that prefab):
// beyond Light Distance as well, so a baked torch never vanishes in the distance. Where a lit copy stands, its record is not drawn instanced (the
// cull-and-LOD job skips it: BuiltZone.Lit). 0 = no baked light burns. Light Shadows (BakedLightShadows) applies within the lit ones.
//
// The ranking is made a few times a second, one for all the zones, and it is sticky: a copy that burns counts as MARGIN metres nearer than it is, so
// a copy at the edge of the Nth place does not change places every time the player moves. A copy that burns also stays while it is within Light
// Distance plus HYSTERESIS (BakedCopies.LightHysteresis, as before), and a copy made is made at most LightsPerFrame a frame, so the ranking only
// says which copies are WANTED; BakedCopies.Props makes and drops them.
internal static class BakedLightCount
{
  /// <summary>How many metres nearer than it is a copy that burns counts in the ranking: a copy not burning must be this much nearer than the one it
  /// would replace.</summary>
  internal const float Margin = 2f;

  /// <summary>The default and the highest Light Count.</summary>
  internal const int Default = 24, Highest = 128;

  // ---- the pure part (the offline suite) ------------------------------------------------------------------------------------------

  /// <summary>
  /// Which of the Copy records (their squared distances to the camera, and whether a lit copy stands at each) are to burn: the <paramref name="count"/>
  /// nearest among the eligible ones. A record is eligible within <paramref name="near"/> metres, or, when it burns now, within near plus
  /// <paramref name="hysteresis"/>. A record that burns is ranked <see cref="Margin"/> metres nearer than it is. Equal ranks go by the record's
  /// place in the list; a distance that is not a number is the farthest (and never eligible). count of 0 or less, or near of 0: none.
  /// </summary>
  internal static bool[] Choose(IReadOnlyList<float> distance2, IReadOnlyList<bool> burning, int count, float near, float hysteresis)
  {
    var result = new bool[distance2.Count];
    if (count <= 0 || near <= 0f)
      return result;
    float far = near + Math.Max(0f, hysteresis);
    var key = new float[distance2.Count];
    var order = new List<int>();
    for (int i = 0; i < distance2.Count; i++)
    {
      float d = (float)Math.Sqrt(distance2[i]);
      if (float.IsNaN(d) || d >= near && !(burning[i] && d <= far))
        continue;
      key[i] = burning[i] ? d - Margin : d;
      order.Add(i);
    }
    if (order.Count <= count)
    {
      foreach (int i in order)
        result[i] = true;
      return result;
    }
    order.Sort((a, b) =>
    {
      int c = key[a].CompareTo(key[b]);
      return c != 0 ? c : a.CompareTo(b);
    });
    for (int k = 0; k < count; k++)
      result[order[k]] = true;
    return result;
  }

  /// <summary>The line of bc_bake stats about the lights.</summary>
  internal static string StatsLine(int copyRecords, int lit, int count, float distance, bool hidden) =>
    $"Baked lights: {lit:N0} of {copyRecords:N0} Copy records burn as lit copies, {Math.Max(0, copyRecords - lit):N0} are drawn unlit "
    + $"(Light Count {count}, Light Distance {distance:0.#} m{(hidden ? ", drawing hidden" : "")}).";

  // ---- the ranking over the zones ---------------------------------------------------------------------------------------------------

  /// <summary>One Copy record that could burn: its zone's props, its place in that zone's copies, its squared distance, whether it burns now.</summary>
  internal readonly struct Candidate(BakedCopies.Props props, int index, float distance2, bool burning)
  {
    internal readonly BakedCopies.Props Props = props;
    internal readonly int Index = index;
    internal readonly float Distance2 = distance2;
    internal readonly bool Burning = burning;
  }

  private static readonly List<Candidate> Found = [];
  private static readonly List<float> Distances = [];
  private static readonly List<bool> Burning = [];
  private static float last = -1000f;
  private static int lastCount = -1, lastProps = -1;
  private static float lastNear = -1f;

  /// <summary>Records of the last ranking: how many were eligible, how many it wants lit.</summary>
  internal static int LastCandidates, LastWanted;

  /// <summary>Whether the ranking is made now: when the setting, Light Distance or the zones that have props changed, or RankInterval has gone by.</summary>
  internal static bool Due(float now, int count, float near, int props) =>
    count != lastCount || near != lastNear || props != lastProps || BakedLightShadows.Due(now, last, newCopy: false, limitChanged: false);

  /// <summary>
  /// Every frame (cheap unless it is time): ranks the Copy records of every zone that has props by distance to the camera, and tells each zone's
  /// props which of its records are wanted lit. The props make and drop the copies (a few a frame; a dropped copy waits for the drawing to take its
  /// record over). <paramref name="near"/> is Light Distance (0: none), <paramref name="count"/> Light Count.
  /// </summary>
  internal static void Update(IReadOnlyList<BakedCopies.Props> props, Vector3 eye, float near, int count, float now)
  {
    if (!Due(now, count, near, props.Count))
      return;
    last = now;
    lastCount = count;
    lastNear = near;
    lastProps = props.Count;
    Found.Clear();
    Distances.Clear();
    Burning.Clear();
    float far = near + BakedCopies.LightHysteresis;
    if (count > 0 && near > 0f)
      foreach (var p in props)
        p.AddCandidates(eye, near, far, Found);
    foreach (var c in Found)
    {
      Distances.Add(c.Distance2);
      Burning.Add(c.Burning);
    }
    var want = Choose(Distances, Burning, count, near, BakedCopies.LightHysteresis);
    foreach (var p in props)
      p.ClearWanted();
    int wanted = 0;
    for (int i = 0; i < Found.Count; i++)
      if (want[i])
      {
        Found[i].Props.Want(Found[i].Index);
        wanted++;
      }
    LastCandidates = Found.Count;
    LastWanted = wanted;
    // the wanted records that have no copy yet, nearest first: BakedCopies.LightsPerFrame of them are made a frame (MakePending)
    Pending.Clear();
    for (int i = 0; i < Found.Count; i++)
      if (want[i] && !Found[i].Burning)
        Pending.Add(Found[i]);
    Pending.Sort((a, b) =>
    {
      int c = a.Distance2.CompareTo(b.Distance2);
      return c != 0 ? c : a.Index.CompareTo(b.Index);
    });
    Found.Clear();
  }

  private static readonly List<Candidate> Pending = [];

  /// <summary>Every frame, after Update: makes the wanted lit copies that do not stand yet, the nearest first, as many as BakedCopies.LightsPerFrame
  /// leaves for this frame.</summary>
  internal static void MakePending()
  {
    int done = 0;
    while (done < Pending.Count && BakedCopies.CanMakeLight())
    {
      var c = Pending[done++];
      c.Props.TryLight(c.Index);
    }
    // what is left is made in the next frames (a record the ranking does not want any more is skipped by TryLight)
    if (done > 0)
      Pending.RemoveRange(0, done);
  }

  /// <summary>A world ends.</summary>
  internal static void Clear()
  {
    last = -1000f;
    lastCount = lastProps = -1;
    lastNear = -1f;
    LastCandidates = LastWanted = 0;
    Found.Clear();
    Pending.Clear();
  }
}

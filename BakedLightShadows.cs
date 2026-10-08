// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// AT MOST "LIGHT SHADOWS" LIT COPIES CAST SHADOWS (section 9.6).
//
// A lit copy (BakedCopies) is a real lit prefab: its Light is on and, left to the game, casts shadows. The game keeps a light's shadows
// itself: LightLod turns them on within m_shadowDistance of the camera, as many lights at once as the graphics setting's point light shadow
// limit allows, and a graphics mod may allow all of them (Graphics Overdrive's PointLightShadowLimit = -1). A point light's shadow is a cube
// map (12 to 48 MB), so a street of torches lit at once, which a real town never has (its torches have no fuel) and a baked one always does,
// could take gigabytes of memory in two seconds (phase F, run f2: +3 GB inside a house with 92 copies standing).
//
// So the setting Light Shadows (0 to 32, default 4) caps them across every zone: the lit copies whose lights cast shadows are ranked by their
// distance to the camera a few times a second, and only the nearest N keep their lights' shadows, as the prefab has them (LightLod still decides
// when, and the game's or a mod's own limits still apply); every other copy's lights cast none. 0 = none cast. It only ever turns shadows off,
// whatever the game's or a mod's own limits are, and it never turns on a shadow the prefab's light did not have.
//
// Taking the shadows off a LightLod light means setting its m_shadowLod false (its loop then leaves the shadows alone), the light's shadows
// None, and, when a ramp of the shadows was under way, starting the loop again (the ramp is part of the loop and would put them back).
internal static class BakedLightShadows
{
  /// <summary>The ranking is made this often (seconds); a copy made since then brings it forward to at least RankGap after the last.</summary>
  internal const float RankInterval = 0.25f, RankGap = 0.1f;

  // ---- the pure parts (the offline suite) ------------------------------------------------------------------------------------------

  /// <summary>Which of the copies (their positions) are the <paramref name="n"/> nearest to the eye among the eligible ones: a copy none of whose
  /// lights could cast a shadow does not take a place. Equal distances go by the copy's place in the list. n of 0 or less: none.</summary>
  internal static bool[] Nearest(IReadOnlyList<Vector3> positions, IReadOnlyList<bool> eligible, Vector3 eye, int n)
  {
    var result = new bool[positions.Count];
    if (n <= 0)
      return result;
    var candidates = new List<int>();
    for (int i = 0; i < positions.Count; i++)
      if (eligible[i])
        candidates.Add(i);
    if (candidates.Count <= n)
    {
      foreach (int i in candidates)
        result[i] = true;
      return result;
    }
    var distance = new float[positions.Count];
    foreach (int i in candidates)
    {
      float d = (positions[i] - eye).sqrMagnitude;
      distance[i] = float.IsNaN(d) ? float.PositiveInfinity : d;
    }
    candidates.Sort((a, b) =>
    {
      int c = distance[a].CompareTo(distance[b]);
      return c != 0 ? c : a.CompareTo(b);
    });
    for (int k = 0; k < n; k++)
      result[candidates[k]] = true;
    return result;
  }

  /// <summary>Whether the ranking is made now: when the setting changed, a copy was made and RankGap has gone by, or RankInterval has.</summary>
  internal static bool Due(float now, float last, bool newCopy, bool limitChanged) =>
    limitChanged || now - last >= (newCopy ? RankGap : RankInterval);

  // ---- the copies -------------------------------------------------------------------------------------------------------------------

  private struct Entry
  {
    internal Light Light;
    internal LightLod? Lod;
    /// <summary>What the prefab's light had: its shadows, and whether its LightLod managed them. Capable: the prefab's light cast any.</summary>
    internal LightShadows Shadows;
    internal bool ShadowLod, Capable;
  }

  private sealed class Copy
  {
    internal GameObject Go = null!;
    internal Vector3 At;
    internal Entry[] Lights = [];
    internal bool Eligible, On;
  }

  private static readonly List<Copy> Standing = [];
  private static readonly Dictionary<GameObject, (LightShadows Shadows, bool ShadowLod)[]> Prototypes = [];
  private static bool newCopy, reported;
  private static float last = -1000f;
  private static int lastLimit = -1;

  private static (LightShadows Shadows, bool ShadowLod)[] PrototypeOf(GameObject prefab)
  {
    if (Prototypes.TryGetValue(prefab, out var known))
      return known;
    var lights = prefab.GetComponentsInChildren<Light>(true);
    var list = new (LightShadows, bool)[lights.Length];
    for (int i = 0; i < lights.Length; i++)
    {
      var lod = lights[i].GetComponent<LightLod>();
      list[i] = (lights[i].shadows, lod != null && lod.m_shadowLod);
    }
    Prototypes[prefab] = list;
    return list;
  }

  /// <summary>A lit copy has been made (BakedCopies.MakeLit): its lights cast no shadows until the ranking says so.</summary>
  internal static void Register(GameObject copy, GameObject prefab, Vector3 at)
  {
    try
    {
      var lights = copy.GetComponentsInChildren<Light>(true);
      var proto = PrototypeOf(prefab);
      // The copy may have lost its lights (a palette flag takes them off) or some: then what each light is now is all that is known.
      bool same = proto.Length == lights.Length;
      var c = new Copy { Go = copy, At = at, Lights = new Entry[lights.Length] };
      for (int i = 0; i < lights.Length; i++)
      {
        var lod = lights[i].GetComponent<LightLod>();
        c.Lights[i] = new Entry
        {
          Light = lights[i], Lod = lod,
          Shadows = same ? proto[i].Shadows : lights[i].shadows,
          ShadowLod = same ? proto[i].ShadowLod : lod != null && lod.m_shadowLod,
          Capable = same ? proto[i].Shadows != LightShadows.None : lights[i].shadows != LightShadows.None || lod != null && lod.m_shadowLod,
        };
        c.Eligible |= c.Lights[i].Capable;
      }
      Standing.Add(c);
      newCopy = true;
      if (c.Eligible)
        Apply(c, on: false);
    }
    catch (Exception e)
    {
      Report(e);
    }
  }

  /// <summary>Every frame (cheap unless it is time): ranks the standing lit copies by distance to the eye and lets only the nearest
  /// <paramref name="limit"/> keep their lights' shadows.</summary>
  internal static void Update(Vector3 eye, int limit, float now)
  {
    if (Standing.Count == 0)
    {
      lastLimit = limit;
      return;
    }
    bool limitChanged = limit != lastLimit;
    if (!Due(now, last, newCopy, limitChanged))
      return;
    last = now;
    newCopy = false;
    lastLimit = limit;
    try
    {
      for (int i = Standing.Count - 1; i >= 0; i--)
        if (Standing[i].Go == null)
          Standing.RemoveAt(i);
      var at = new List<Vector3>(Standing.Count);
      var eligible = new List<bool>(Standing.Count);
      foreach (var c in Standing)
      {
        at.Add(c.At);
        eligible.Add(c.Eligible);
      }
      var near = Nearest(at, eligible, eye, limit);
      for (int i = 0; i < Standing.Count; i++)
      {
        var c = Standing[i];
        if (!c.Eligible)
          continue;
        if (c.On != near[i])
          Apply(c, near[i]);
        else if (!c.On)
          Enforce(c);
      }
    }
    catch (Exception e)
    {
      Report(e);
    }
  }

  // The nearest copies get their lights' shadows back as the prefab had them; LightLod, if it managed them, takes them up again.
  private static void Apply(Copy c, bool on)
  {
    c.On = on;
    foreach (var e in c.Lights)
    {
      if (e.Light == null || !e.Capable)
        continue;
      if (on)
      {
        if (e.Lod != null)
        {
          e.Lod.m_shadowLod = e.ShadowLod;
          Restart(e.Lod);
        }
        else
          e.Light.shadows = e.Shadows;
      }
      else
        Off(e);
    }
  }

  // A copy that must cast none and does: LightLod's loop was under way, or a mod set them. Cheap when nothing is wrong.
  private static void Enforce(Copy c)
  {
    foreach (var e in c.Lights)
      if (e.Light != null && e.Capable && (e.Light.shadows != LightShadows.None || e.Lod != null && e.Lod.m_shadowLod))
        Off(e);
  }

  private static void Off(Entry e)
  {
    bool casting = e.Light.shadows != LightShadows.None;
    if (e.Lod != null)
      e.Lod.m_shadowLod = false;
    if (casting)
    {
      e.Light.shadows = LightShadows.None;
      e.Light.shadowStrength = 0f;
      // A ramp of the shadows in LightLod's loop would put them back at once: the loop starts again, and with m_shadowLod off leaves them alone.
      if (e.Lod != null)
        Restart(e.Lod);
    }
  }

  private static void Restart(LightLod lod)
  {
    if (lod == null || !lod.isActiveAndEnabled)
      return;
    lod.StopCoroutine("UpdateLoop");
    lod.StartCoroutine("UpdateLoop");
  }

  private static void Report(Exception e)
  {
    if (reported)
      return;
    reported = true;
    LogWarning($"baked placements: the shadows of the lit copies could not be limited: {e.Message}");
  }

  /// <summary>A world ends: the copies went with their zones.</summary>
  internal static void Clear()
  {
    Standing.Clear();
    Prototypes.Clear();
    newCopy = false;
  }
}

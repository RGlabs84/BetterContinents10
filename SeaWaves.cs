// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// The waves that boats, swimmers and fish feel, written once for both sides: the water shader's wave block (unity/Sea/SeaWaves.hlsl) is
// this file's twin, line for line.
//
// It is WaterVolume.CalcWave's private overload (WaterVolume.cs:198, Valheim 1.0), one wind at a time, with the one thing BC's sea changes:
// the weights of the waves. The game's C# code and its shader differ here (the plan's 2.4):
//   the game's CalcWave   waves 1-6 x waveFactorBig (the Deep North's fade), waves 7-10 x waveFactor, nothing else;
//   the game's shader     waves 1-5 x f, waves 6-10 x max(f, 0.1), where f is the same fade.
// BC's sea takes what is drawn for the small waves, so that a boat in the calm sea feels what the eye sees (the calm sea's small ripple, a tenth
// of its height), and keeps waveFactor where the game's C# code has it (the Leviathan's 0.5):
//   waves 1-5   x W
//   wave 6      x max(W, 0.1)           (no waveFactor: as the game's CalcWave, which gives it waveFactorBig)
//   waves 7-10  x max(W, 0.1) x waveFactor
// W is 1 - the Deep North's fade (SeaField.Waves): 1 in the open sea, so there every digit is the game's, down to the order of the sums.
//
// The same arithmetic for the shader, in this order:
//   time = waterTime / 20
//   direction 0 = normalize(wind.x, wind.z) (zero when its length is 1e-5 or less), tangent 0 = (-direction.y, direction.x); waves 1-10 use
//     Directions[0..9] and Tangents[0..9] below, the other nine being constants.
//   wave(i) = (TrochSin(time * speed + v.y * length, sharp) * TrochSin(time * speed * 0.123 + v.x * 0.13123 * length, sharp) - 0.2) * height
//     with v = -(z * direction + x * tangent) (a float2), and TrochSin(a, k) = sin(a - cos(a) * k) * 0.5 + 0.5.
//   result = (wave1 + wave2 + ... + wave10) * lerp(0, wind.w, saturate(depth))
// The speeds, lengths, heights and sharpness are the game's (Speed, Length, Height, Sharp below).
internal static class SeaWaves
{
  // The nine constant directions of WaterVolume.s_createWaveDirections (the first is the wind's) and their tangents.
  private static readonly Vector2[] Directions =
  [
    new Vector2(1.0312f, 0.312f).normalized, new Vector2(1.0312f, 0.312f).normalized, new Vector2(-0.123f, 1.12f).normalized,
    new Vector2(0.423f, 0.124f).normalized, new Vector2(0.123f, -0.64f).normalized, new Vector2(-0.523f, -0.64f).normalized,
    new Vector2(0.223f, 0.74f).normalized, new Vector2(0.923f, -0.24f).normalized, new Vector2(-0.323f, 0.44f).normalized,
    new Vector2(0.5312f, -0.812f).normalized,
  ];
  private static readonly Vector2[] Tangents =
  [
    new Vector2(0f - Directions[0].y, Directions[0].x), new Vector2(0f - Directions[1].y, Directions[1].x), new Vector2(0f - Directions[2].y, Directions[2].x),
    new Vector2(0f - Directions[3].y, Directions[3].x), new Vector2(0f - Directions[4].y, Directions[4].x), new Vector2(0f - Directions[5].y, Directions[5].x),
    new Vector2(0f - Directions[6].y, Directions[6].x), new Vector2(0f - Directions[7].y, Directions[7].x), new Vector2(0f - Directions[8].y, Directions[8].x),
    new Vector2(0f - Directions[9].y, Directions[9].x),
  ];

  // The game's ten waves, as they appear in its CalcWave (what the offline tests read back from the installed game's IL).
  internal static readonly float[] Speed = [10f, 14.123f, 22.312f, 31.42f, 35.42f, 38.1223f, 41.1223f, 51.5123f, 54.2f, 56.123f];
  internal static readonly float[] Length = [0.04f, 0.08f, 0.1f, 0.2f, 0.4f, 1f, 1.2f, 1.3f, 1.3f, 1.5f];
  internal static readonly float[] Height = [8f, 6f, 4f, 2f, 1f, 0.8f, 0.6f, 0.4f, 0.3f, 0.2f];
  internal static readonly float[] Sharp = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.7f, 0.8f, 0.9f, 0.9f, 0.9f];

  private static float TrochSin(float x, float k) => Mathf.Sin(x - Mathf.Cos(x) * k) * 0.5f + 0.5f;

  // WaterVolume.CreateWave (WaterVolume.cs:~188).
  private static float Wave(float x, float z, float time, int i, float height, Vector2 dir, Vector2 tangent)
  {
    var v = -(z * dir + x * tangent);
    float t = time * Speed[i];
    return (TrochSin(t + v.y * Length[i], Sharp[i]) * TrochSin(t * 0.123f + v.x * 0.13123f * Length[i], Sharp[i]) - 0.2f) * height;
  }

  /// <summary>One wind's waves at a place: the game's ten waves, waves 1-5 x w, wave 6 x max(w, 0.1) and waves 7-10 x max(w, 0.1) x
  /// waveFactor, times lerp(0, wind.w, depth). <paramref name="time"/> is the water's clock in seconds (the shader's _WaterTime; it is
  /// divided by 20 here, as the game does). w is the world's wave strength there: 1 in the open sea, 0 where the sea is calm.</summary>
  public static float One(float x, float z, float depth, Vector4 wind, float time, float waveFactor, float w)
  {
    var dir = new Vector2(wind.x, wind.z);
    dir.Normalize();
    var tangent = new Vector2(0f - dir.y, dir.x);
    float amount = Mathf.Lerp(0f, wind.w, depth);
    float t = time / 20f;
    float small = Mathf.Max(w, 0.1f);
    float n1 = Wave(x, z, t, 0, 8f * w, dir, tangent);
    float n2 = Wave(x, z, t, 1, 6f * w, Directions[1], Tangents[1]);
    float n3 = Wave(x, z, t, 2, 4f * w, Directions[2], Tangents[2]);
    float n4 = Wave(x, z, t, 3, 2f * w, Directions[3], Tangents[3]);
    float n5 = Wave(x, z, t, 4, 1f * w, Directions[4], Tangents[4]);
    float n6 = Wave(x, z, t, 5, 0.8f * small, Directions[5], Tangents[5]);
    float n7 = Wave(x, z, t, 6, 0.6f * small * waveFactor, Directions[6], Tangents[6]);
    float n8 = Wave(x, z, t, 7, 0.4f * small * waveFactor, Directions[7], Tangents[7]);
    float n9 = Wave(x, z, t, 8, 0.3f * small * waveFactor, Directions[8], Tangents[8]);
    float n10 = Wave(x, z, t, 9, 0.2f * small * waveFactor, Directions[9], Tangents[9]);
    return (n1 + n2 + n3 + n4 + n5 + n6 + n7 + n8 + n9 + n10) * amount;
  }

  // The water's clock: WaterVolume.s_waterTime, what the shader's _WaterTime is set from every frame (WaterVolume.UpdateMaterials). The game's
  // own CalcWave callers pass the wrapped day time instead, which runs up to a fifth of a second apart (the plan's 2.4); felt waves follow the
  // drawn clock. A field the offline tests can replace, since WaterVolume's statics need the engine.
  internal static Func<float>? Clock;

  /// <summary>The drawn clock in seconds; NaN when the game's field cannot be read (the caller then uses the clock it was given).</summary>
  public static float DrawnClock => (Clock ??= MakeClock())();

  private static Func<float> MakeClock()
  {
    var field = AccessTools.Field(typeof(WaterVolume), "s_waterTime");
    if (field == null)
    {
      BetterContinents.LogError("Could not find WaterVolume.s_waterTime - the waves boats feel are on the game's clock, not the drawn one.");
      return () => float.NaN;
    }
    try
    {
      return Expression.Lambda<Func<float>>(Expression.Field(null, field)).Compile();
    }
    catch (Exception)
    {
      return () => (float)field.GetValue(null);
    }
  }
}

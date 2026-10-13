// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// SeaWaves (SeaWaves.cs) against the game: a transcription of WaterVolume.CalcWave (WaterVolume.cs:186-230, Valheim 1.0) at W = 1, a
// transcription of the water shader's weights (GLSL 184-319 of VALtima's dump of Custom/Water) below 1, and the constants of the installed game's
// IL, so that a game that changes a wave fails here instead of drawing and floating differently.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
  private static void WaveTests()
  {
    GameConstantTests();
    OpenSeaTests();
    ShaderWeightTests();
    WeightTests();
    ClockTests();
  }

  // ------------------------------------------------------------------------------------------------ the game's own waves
  // WaterVolume.CalcWave (the private overload, one wind) as the game writes it, with its own copies of the direction tables.
  private static readonly Vector2[] GameDirections =
  [
    new Vector2(1.0312f, 0.312f).normalized, new Vector2(1.0312f, 0.312f).normalized, new Vector2(-0.123f, 1.12f).normalized,
    new Vector2(0.423f, 0.124f).normalized, new Vector2(0.123f, -0.64f).normalized, new Vector2(-0.523f, -0.64f).normalized,
    new Vector2(0.223f, 0.74f).normalized, new Vector2(0.923f, -0.24f).normalized, new Vector2(-0.323f, 0.44f).normalized,
    new Vector2(0.5312f, -0.812f).normalized,
  ];
  private static Vector2[] GameTangents = [.. GameDirections.Select(d => new Vector2(0f - d.y, d.x))];

  private static float GameTrochSin(float x, float k) => Mathf.Sin(x - Mathf.Cos(x) * k) * 0.5f + 0.5f;

  private static float GameCreateWave(Vector3 worldPos, float time, float waveSpeed, float waveLength, float waveHeight, Vector2 dir, Vector2 tangent, float sharpness)
  {
    Vector2 vector = -(worldPos.z * dir + worldPos.x * tangent);
    float num = time * waveSpeed;
    return (GameTrochSin(num + vector.y * waveLength, sharpness) * GameTrochSin(num * 0.123f + vector.x * 0.13123f * waveLength, sharpness) - 0.2f) * waveHeight;
  }

  private static float GameCalcWave(Vector3 worldPos, float depth, Vector4 wind, float waterTime, float waveFactor, float waveFactorBig)
  {
    GameDirections[0].x = wind.x;
    GameDirections[0].y = wind.z;
    GameDirections[0].Normalize();
    GameTangents[0].x = 0f - GameDirections[0].y;
    GameTangents[0].y = GameDirections[0].x;
    float w = wind.w;
    float num = Mathf.Lerp(0f, w, depth);
    float time = waterTime / 20f;
    float num2 = GameCreateWave(worldPos, time, 10f, 0.04f, 8f * waveFactorBig, GameDirections[0], GameTangents[0], 0.5f);
    float num3 = GameCreateWave(worldPos, time, 14.123f, 0.08f, 6f * waveFactorBig, GameDirections[1], GameTangents[1], 0.5f);
    float num4 = GameCreateWave(worldPos, time, 22.312f, 0.1f, 4f * waveFactorBig, GameDirections[2], GameTangents[2], 0.5f);
    float num5 = GameCreateWave(worldPos, time, 31.42f, 0.2f, 2f * waveFactorBig, GameDirections[3], GameTangents[3], 0.5f);
    float num6 = GameCreateWave(worldPos, time, 35.42f, 0.4f, 1f * waveFactorBig, GameDirections[4], GameTangents[4], 0.5f);
    float num7 = GameCreateWave(worldPos, time, 38.1223f, 1f, 0.8f * waveFactorBig, GameDirections[5], GameTangents[5], 0.7f);
    float num8 = GameCreateWave(worldPos, time, 41.1223f, 1.2f, 0.6f * waveFactor, GameDirections[6], GameTangents[6], 0.8f);
    float num9 = GameCreateWave(worldPos, time, 51.5123f, 1.3f, 0.4f * waveFactor, GameDirections[7], GameTangents[7], 0.9f);
    float num10 = GameCreateWave(worldPos, time, 54.2f, 1.3f, 0.3f * waveFactor, GameDirections[8], GameTangents[8], 0.9f);
    float num11 = GameCreateWave(worldPos, time, 56.123f, 1.5f, 0.2f * waveFactor, GameDirections[9], GameTangents[9], 0.9f);
    return (num2 + num3 + num4 + num5 + num6 + num7 + num8 + num9 + num10 + num11) * num;
  }

  private static List<float> LoadedFloats(MethodBase method) =>
    PatchProcessor.GetOriginalInstructions(method).Where(c => c.opcode == OpCodes.Ldc_R4).Select(c => (float)c.operand).ToList();

  // Whether `expected` appears in `actual` as a run of values next to each other.
  private static bool ContainsRun(List<float> actual, float[] expected)
  {
    for (int i = 0; i + expected.Length <= actual.Count; i++)
    {
      int k = 0;
      while (k < expected.Length && Same(actual[i + k], expected[k])) k++;
      if (k == expected.Length) return true;
    }
    return false;
  }

  private static void GameConstantTests()
  {
    Section("waves: the game's constants, read from the installed game");
    if (!running) return;
    var calc = SeaPatches.CalcWaveTarget();
    Check(calc != null && !calc.IsPublic && !calc.IsStatic && ((MethodInfo)calc).ReturnType == typeof(float),
      "WaterVolume has a private instance CalcWave(Vector3, float, Vector4, float, float, float) returning float");
    if (calc == null) return;
    // The ten waves: speed, length, height (times the fade) and sharpness, in the order the game writes them.
    var expected = new List<float>();
    for (int i = 0; i < 10; i++)
      expected.AddRange([SeaWaves.Speed[i], SeaWaves.Length[i], SeaWaves.Height[i], SeaWaves.Sharp[i]]);
    var constants = LoadedFloats(calc);
    // Each wave's four constants are loaded with other instructions between them (the call's other arguments), so match them in order.
    int at = 0, found = 0;
    foreach (var value in expected)
    {
      int next = constants.FindIndex(at, c => Same(c, value));
      if (next < 0) break;
      at = next + 1;
      found++;
    }
    Check(found == expected.Count, $"CalcWave loads the ten waves' speeds, lengths, heights and sharpness in order, as SeaWaves holds them ({found}/{expected.Count} found)");
    Check(constants.Contains(20f), "CalcWave divides the clock by 20");
    var create = LoadedFloats(AccessTools.Method(typeof(WaterVolume), "CreateWave"));
    Check(ContainsRun(create, [0.123f, 0.13123f]) || (create.Contains(0.123f) && create.Contains(0.13123f) && create.Contains(0.2f)),
      "CreateWave's constants: 0.123 and 0.13123 for the second sine, and 0.2 taken off");
    var troch = LoadedFloats(AccessTools.Method(typeof(WaterVolume), "TrochSin"));
    Check(troch.Count(c => c == 0.5f) == 2, "TrochSin: sin(x - cos(x) * k) * 0.5 + 0.5");
    // The directions of waves 2-10 are the game's static data.
    var directions = new float[]
    {
      1.0312f, 0.312f, 1.0312f, 0.312f, -0.123f, 1.12f, 0.423f, 0.124f, 0.123f, -0.64f, -0.523f, -0.64f, 0.223f, 0.74f, 0.923f, -0.24f, -0.323f, 0.44f,
      0.5312f, -0.812f,
    };
    var cctor = typeof(WaterVolume).TypeInitializer;
    Check(cctor != null && ContainsRun(LoadedFloats(cctor), directions), "WaterVolume's static data holds the ten wave directions SeaWaves copies");
    // The drawn clock is s_waterTime: the field UpdateMaterials hands the shader every frame.
    var clock = AccessTools.Field(typeof(WaterVolume), "s_waterTime");
    var update = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(WaterVolume), "UpdateMaterials"));
    Check(clock != null && clock.IsStatic && clock.FieldType == typeof(float) && update.Any(c => c.opcode == OpCodes.Ldsfld && Equals(c.operand, clock)),
      "the drawn clock is WaterVolume.s_waterTime, a static float that UpdateMaterials sets the shader's _WaterTime from");
    // The public CalcWave (five arguments) calls the private one once per wind: the prefix sees every wave that is drawn or felt.
    var publicCalc = AccessTools.Method(typeof(WaterVolume), "CalcWave", [typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float)]);
    var callers = PatchProcessor.GetOriginalInstructions(publicCalc).Count(c => c.Calls((MethodInfo)calc));
    Check(publicCalc != null && callers == 3, $"the public CalcWave calls the one-wind overload for wind 1 alone, and for both winds ({callers} call sites)");
    var surface = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(WaterVolume), "GetWaterSurface"));
    Check(surface.Any(c => c.Calls(AccessTools.Method(typeof(WorldGenerator), "DeepNorthWaveFade"))) && surface.Any(c => c.Calls(publicCalc)),
      "GetWaterSurface asks DeepNorthWaveFade and calls the public CalcWave");
  }

  // ------------------------------------------------------------------------------------------------ W = 1: the game's waves
  private static void OpenSeaTests()
  {
    Section("waves: W = 1 is the game's CalcWave");
    if (!running) return;
    var random = new System.Random(2026);
    int n = 100000, exact = 0;
    float worst = 0f;
    for (int i = 0; i < n; i++)
    {
      var p = new Vector3((float)(random.NextDouble() * 130000 - 65000), 30f, (float)(random.NextDouble() * 130000 - 65000));
      float depth = (float)random.NextDouble() * 1.2f - 0.1f;
      var wind = new Vector4((float)(random.NextDouble() * 2 - 1), 0f, (float)(random.NextDouble() * 2 - 1), (float)(0.05 + random.NextDouble() * 1.15));
      float time = (float)(random.NextDouble() * 86400.0);
      float waveFactor = random.Next(4) == 0 ? 0.5f : random.Next(4) == 0 ? (float)random.NextDouble() : 1f;
      float game = GameCalcWave(p, depth, wind, time, waveFactor, 1f);
      float ours = SeaWaves.One(p.x, p.z, depth, wind, time, waveFactor, 1f);
      if (Same(game, ours)) exact++;
      worst = Math.Max(worst, Math.Abs(game - ours));
    }
    Check(worst <= 1e-5f, $"SeaWaves.One at W = 1 against the game's CalcWave, {n} random places, depths, winds, clocks and wave factors: the greatest difference is {worst:G3} m (at most 1e-5)");
    Check(exact == n, $"... and it is the game's to the last bit ({exact}/{n} the same)");

    var still = SeaWaves.One(100f, -200f, 0.7f, new Vector4(1f, 0f, 0f, 0f), 12.5f, 1f, 1f);
    Check(still == 0f, "no wind, no waves");
    Check(SeaWaves.One(100f, -200f, 0f, new Vector4(1f, 0f, 0f, 1f), 12.5f, 1f, 1f) == 0f, "no depth, no waves");
    var calm = SeaWaves.One(100f, -200f, 3f, new Vector4(1f, 0f, 0f, 1f), 12.5f, 1f, 1f);
    Check(calm == SeaWaves.One(100f, -200f, 1f, new Vector4(1f, 0f, 0f, 1f), 12.5f, 1f, 1f), "a depth above 1 counts as 1, as the game's Lerp does");
    var noDirection = SeaWaves.One(100f, -200f, 1f, new Vector4(0f, 5f, 0f, 1f), 12.5f, 1f, 1f);
    Check(!float.IsNaN(noDirection) && !float.IsInfinity(noDirection), "a wind with no direction gives a number, not NaN");
  }

  // ------------------------------------------------------------------------------------------------ W < 1: the shader's weights
  // The shader's wave block (GLSL 150-319), table-driven and written afresh, with the weight of each wave given: the shader's own are f for
  // waves 1-5 and max(f, 0.1) for waves 6-10, and it has no waveFactor.
  private static float WeightedWaves(float x, float z, float depth, Vector4 wind, float time, float[] weight)
  {
    float[] speed = [10f, 14.123f, 22.312f, 31.42f, 35.42f, 38.1223f, 41.1223f, 51.5123f, 54.2f, 56.123f];
    float[] length = [0.04f, 0.08f, 0.1f, 0.2f, 0.4f, 1f, 1.2f, 1.3f, 1.3f, 1.5f];
    float[] height = [8f, 6f, 4f, 2f, 1f, 0.8f, 0.6f, 0.4f, 0.3f, 0.2f];
    float[] k = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.7f, 0.8f, 0.9f, 0.9f, 0.9f];
    float[,] raw = { { 0, 0 }, { 1.0312f, 0.312f }, { -0.123f, 1.12f }, { 0.423f, 0.124f }, { 0.123f, -0.64f }, { -0.523f, -0.64f }, { 0.223f, 0.74f }, { 0.923f, -0.24f }, { -0.323f, 0.44f }, { 0.5312f, -0.812f } };
    float amplitude = depth * wind.w;
    float t = time / 20f;
    float sum = 0f;
    for (int i = 0; i < 10; i++)
    {
      Vector2 d = i == 0 ? new Vector2(wind.x, wind.z) : new Vector2(raw[i, 0], raw[i, 1]);
      d = d.normalized;
      var tangent = new Vector2(-d.y, d.x);
      float u = -(z * d.x + x * tangent.x), v = -(z * d.y + x * tangent.y);
      float a = t * speed[i];
      float wave = (GameTrochSin(a + v * length[i], k[i]) * GameTrochSin(a * 0.123f + u * 0.13123f * length[i], k[i]) - 0.2f) * height[i];
      sum += wave * weight[i];
    }
    return sum * amplitude;
  }

  private static float[] ShaderWeights(float f)
  {
    float small = Math.Max(f, 0.1f);
    return [f, f, f, f, f, small, small, small, small, small];
  }

  private static void ShaderWeightTests()
  {
    Section("waves: W < 1 against the shader's weights");
    if (!running) return;
    var random = new System.Random(77);
    int n = 100000;
    float worst = 0f, worstCalm = 0f;
    for (int i = 0; i < n; i++)
    {
      float x = (float)(random.NextDouble() * 65000 - 32500), z = (float)(random.NextDouble() * 65000 - 32500);
      float depth = (float)random.NextDouble();
      var wind = new Vector4((float)(random.NextDouble() * 2 - 1), 0f, (float)(random.NextDouble() * 2 - 1), (float)(0.05 + random.NextDouble() * 0.95));
      float time = (float)(random.NextDouble() * 86400.0);
      float f = random.Next(5) == 0 ? 0f : random.Next(5) == 0 ? 1f : (float)random.NextDouble();
      float shader = WeightedWaves(x, z, depth, wind, time, ShaderWeights(f));
      float ours = SeaWaves.One(x, z, depth, wind, time, 1f, f);
      worst = Math.Max(worst, Math.Abs(shader - ours));
      if (f == 0f) worstCalm = Math.Max(worstCalm, Math.Abs(shader - ours));
    }
    Check(worst <= 1e-5f, $"SeaWaves.One (waveFactor 1) against the shader's weights, {n} random places and W: the greatest difference is {worst:G3} m (at most 1e-5)");
    Check(worstCalm <= 1e-5f, $"... and in the calm sea (W = 0), where the small waves are a tenth: {worstCalm:G3} m");
  }

  // ------------------------------------------------------------------------------------------------ the weights themselves
  private static void WeightTests()
  {
    Section("waves: the weights");
    if (!running) return;
    var wind = new Vector4(0.6f, 0f, 0.8f, 1f);
    var p = new Vector3(-1234.5f, 30f, 8765.4f);
    float time = 4321.5f;
    // The plan's decision (6.3): waves 1-5 x W, wave 6 x max(W, 0.1) with no waveFactor (the game gives wave 6 its fade, not its waveFactor), and
    // waves 7-10 x max(W, 0.1) x waveFactor.
    int bad = 0, cases = 0;
    foreach (float w in new[] { 0f, 0.05f, 0.1f, 0.3f, 1f })
      foreach (float waveFactor in new[] { 0.5f, 1f, 0.25f })
      {
        float small = Math.Max(w, 0.1f);
        float[] weight = [w, w, w, w, w, small, small * waveFactor, small * waveFactor, small * waveFactor, small * waveFactor];
        float expected = WeightedWaves(p.x, p.z, 0.8f, wind, time, weight);
        float ours = SeaWaves.One(p.x, p.z, 0.8f, wind, time, waveFactor, w);
        cases++;
        if (Math.Abs(ours - expected) > 1e-5f)
        {
          bad++;
          System.Console.WriteLine($"  W {w}, waveFactor {waveFactor}: {ours:G6} against {expected:G6}");
        }
      }
    Check(bad == 0, $"waves 1-5 x W, wave 6 x max(W, 0.1), waves 7-10 x max(W, 0.1) x waveFactor ({cases} combinations, {bad} wrong)");
    // The Leviathan (waveFactor 0.5) in the open sea feels the game's waves: waves 7-10 halved.
    Check(Math.Abs(SeaWaves.One(p.x, p.z, 1f, wind, time, 0.5f, 1f) - GameCalcWave(p, 1f, wind, time, 0.5f, 1f)) <= 1e-5f,
      "in the open sea a wave factor of 0.5 (the Leviathan) is the game's");
    // In the calm sea the small waves are a tenth of their height, whatever the wave factor does to them.
    float calmOne = SeaWaves.One(p.x, p.z, 1f, wind, time, 1f, 0f);
    float tenth = WeightedWaves(p.x, p.z, 1f, wind, time, [0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
    Check(Math.Abs(calmOne - tenth) <= 1e-5f, $"W = 0: only the six smallest waves move, at a tenth ({calmOne:G4} m)");
  }

  private static void ClockTests()
  {
    Section("waves: the clock");
    if (!running) return;
    var saved = SeaWaves.Clock;
    try
    {
      SeaWaves.Clock = () => 4242.5f;
      Check(SeaWaves.DrawnClock == 4242.5f, "the drawn clock can be replaced for the offline tests");
      SeaWaves.Clock = () => float.NaN;
      Check(float.IsNaN(SeaWaves.DrawnClock), "and says NaN when the game's field cannot be read (the prefix then uses the clock it was given)");
    }
    finally
    {
      SeaWaves.Clock = saved;
    }
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// How far above Better Continents' heightmap the game's own terrain can reach: HighTerrain.MaxMetres, the top the ground rays start over and the
// altitude limits are lifted to. Three kinds of check:
//   - the numbers it gives for the settings that matter (the old and new Heightmap Amount limits, 81, VALtima's 10.8 with Heightmap Override All on and off);
//   - the game's own height formulas of Valheim 1.0.17 (WorldGenerator.GetMeadowsHeight, GetPlainsHeight, GetForestHeight, GetMistlandsHeight,
//     GetSnowMountainHeight, GetDeepNorthHeight, GetMarshHeight, GetOceanHeight), transcribed with the noise taken to the extremes of what the game's
//     Perlin function gives (-0.14 .. 1.14 measured), over every base height from -0.15 to 81 units: none passes the bound;
//   - the game's Mountain and Mistlands formulas as they are in both game builds (Cecil): a game update that changes their constants fails it,
//     which is the point: the bound stands on them.
// What the bound does not hold is the Mountain biome's own slope term (+ 2 * Perlin * the base height's change over 2 m in x and y, about 4 m per
// unit of slope): a heightmap's steepness, within the 1,000 m the rays start over it (RayMargin), slopes up to 250; measured on the rig (tools/high-tests/rig).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // Unity's Mathf.PerlinNoise is not confined to 0..1: -0.1361 .. 1.1384 over 4,000,000 points on the dedicated server (BCPROBE_PERLIN on the rig).
  private static readonly float[] NoiseValues = [-0.15f, 0f, 0.5f, 1f, 1.15f];

  private static IEnumerable<float[]> NoiseCombinations(int count)
  {
    var index = new int[count];
    while (true)
    {
      yield return index.Select(i => NoiseValues[i]).ToArray();
      int k = 0;
      while (k < count && ++index[k] == NoiseValues.Length)
        index[k++] = 0;
      if (k == count)
        yield break;
    }
  }

  // num3 of GetMeadowsHeight, GetPlainsHeight, GetForestHeight and GetDeepNorthHeight, and num5 of GetSnowMountainHeight: two products of noises, the second
  // times the first and one half, added.
  private static float FieldNoise(float p1, float p2, float p3, float p4)
  {
    float n = p1 * p2;
    return n + p3 * p4 * n * 0.5f;
  }

  // The heights, in units of 200 m, of the game's biome formulas over a base height b (rivers left out: AddRivers only lowers, the tilt of the Mountain biome
  // left out: see above). q is the noise values: four for the field noise, then the two small ones (the 0.1 and the 0.4 scale).
  private static float MeadowsOrPlains(float b, float[] q)
  {
    float n = FieldNoise(q[0], q[1], q[2], q[3]);
    float h = b + n * 0.1f;
    float over = h - 0.15f;
    float mask = Mathf.Clamp01(b / 0.4f);
    if (over > 0f)
      h -= over * ((1f - mask) * 0.75f);
    return h + q[4] * 0.01f + q[5] * 0.003f;
  }

  private static float BlackForest(float b, float[] q) => b + FieldNoise(q[0], q[1], q[2], q[3]) * 0.1f + q[4] * 0.01f + q[5] * 0.003f;

  private static float DeepNorth(float b, float[] q)
  {
    float basis = b + 0.1f;
    float h = basis + FieldNoise(q[0], q[1], q[2], q[3]) * 0.1f;
    float over = h - 0.15f;
    float mask = Mathf.Clamp01(basis / 0.4f);
    if (over > 0f)
      h -= over * ((1f - mask) * 0.75f);
    return h + q[4] * 0.01f + q[5] * 0.003f;
  }

  private static float Mountain(float b, float[] q) => b + (b - 0.4f) + FieldNoise(q[0], q[1], q[2], q[3]) * 0.2f + q[4] * 0.01f + q[5] * 0.003f;

  private static float Mistlands(float b, float[] q)
  {
    // num3 = P(.014) * P(.028); num3 += P(.021) * P(.035) * num3 * .5; num3 = num3 > 0 ? num3 ^ 1.5 : num3; the height + 0.4 * num3
    float n = FieldNoise(q[0], q[1], q[2], q[3]);
    n = n > 0f ? (float)Math.Pow(n, 1.5) : n;
    float h = b + n * 0.4f;
    float mask = Mathf.Clamp01(n * 7f);
    h += q[4] * 0.03f * mask;
    h += q[5] * 0.01f * mask;
    float a = h + q[5] * 0.002f;
    float terrace = Mathf.Ceil(h * 400f) / 400f;
    return Mathf.Lerp(a, terrace, mask);
  }

  // The world's settings with Heightmap Amount, Blend, Add, Sea Level shift and Override All as given, and the brightest pixel.
  private static BC.BetterContinentsSettings BoundWorld(float amount, bool overrideAll, float blend = 1f, float add = 0f, float sea = 0f)
  {
    var s = HighWorld(amount, overrideAll: overrideAll, blend: blend, add: add);
    s.SeaLevelAdjustment = sea;
    return s;
  }

  private static void BoundTests()
  {
    Section("how far above the heightmap the game's own terrain reaches");

    // The numbers, in metres over zero (the sea is at 30 m): b = Amount * Blend + max(0, Add) - 0.15 + max(0, sea) units of 200 m with Heightmap Override All
    // on; 2b - 0.4 + the noise of the Mountain biome with it off.
    float Metres(float amount, bool overrideAll, float blend = 1f, float add = 0f, float sea = 0f) => HighTerrain.MaxMetres(BoundWorld(amount, overrideAll, blend, add, sea));
    Check(Math.Abs(Metres(81f, true) - 16170f) < 0.5f, $"Heightmap Amount 81, Override All on: the land cannot pass {Metres(81f, true)} m (16170: the description's -30 m to 16,170 m)");
    Check(Math.Abs(Metres(1f, true) - 170f) < 0.5f && Math.Abs(Metres(5f, true) - 970f) < 0.5f && Math.Abs(Metres(10.8f, true) - 2130f) < 0.5f,
      $"Amount 1: {Metres(1f, true)} m (170), 5: {Metres(5f, true)} m (970), VALtima's 10.8: {Metres(10.8f, true)} m (2130, for the brightest pixel; its tallest has 1999 m)");
    float off81 = Metres(81f, false), off108 = Metres(10.8f, false), off5 = Metres(5f, false);
    float top81 = 200f * (2f * 80.85f - 0.4f);
    Check(off81 > top81 + 40f && off81 < top81 + 120f && off108 > 200f * (2f * 10.65f - 0.4f) + 40f && off108 < 200f * (2f * 10.65f - 0.4f) + 120f,
      $"Override All off: the Mountain biome doubles what is over 80 m: Amount 81 {off81:0} m (the game's own {top81:0} + its noise), 10.8 {off108:0} m, 5 {off5:0} m");
    Check(Math.Abs(Metres(81f, true, blend: 0.5f) - 200f * (40.5f - 0.15f)) < 0.01f && Math.Abs(Metres(81f, true, add: 1f) - 200f * (82f - 0.15f)) < 0.5f && Math.Abs(Metres(81f, true, sea: 1f) - 200f * (82f - 0.15f)) < 0.5f
          && Math.Abs(Metres(81f, true, add: -1f) - Metres(81f, true)) < 0.01f && Math.Abs(Metres(81f, true, sea: -1f) - Metres(81f, true)) < 0.01f,
      "Blend 0.5 halves what the amount adds (8070 m), Add 1 and a Sea Level Adjustment of +1 add a unit (16370 m), a negative Add or sea shift lowers the land and counts for nothing");
    Check(Metres(float.NaN, true) == 0f && Metres(0f, true) == 0f && HighTerrain.MaxMetres(HighWorld(81f, enabled: false)) == 0f
          && HighTerrain.MaxMetres(new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, HeightmapAmount = 81f }) == 0f,
      "an amount that is not a number or 0 (Override All on), Better Continents off, and no heightmap at all: no land to speak of, 0 m (not the sea's -30 m)");

    // The game's formulas under the bound, for every base height from just under the sea to the top of Amount 81, and every noise.
    var biomes = new (string name, int noises, Func<float, float[], float> height)[]
    {
      ("Meadows and Plains", 6, MeadowsOrPlains), ("BlackForest", 6, BlackForest), ("Mistlands", 6, Mistlands), ("Mountain", 6, Mountain), ("DeepNorth", 6, DeepNorth),
    };
    var bases = new[] { -0.15f, 0f, 0.15f, 0.3f, 0.4f, 0.6f, 1f, 1.3f, 2f, 5f, 10.65f, 20f, 40f, 80.85f };
    foreach (var b in bases)
    {
      float amount = b + 0.15f;
      float on = HighTerrain.MaxMetres(BoundWorld(amount, true)) / 200f, off = HighTerrain.MaxMetres(BoundWorld(amount, false)) / 200f;
      // Override All on: the world's height is the base height itself (WorldGeneratorPatch.GetBiomeHeightBeforeEwdWithHeight: GetBaseHeight * 200).
      Check(Math.Abs(on - Math.Max(0f, b)) < 1e-3f, $"Override All on, base height {b}: the bound is that height, {on:0.###} units");
      foreach (var (name, noises, height) in biomes)
      {
        float most = float.MinValue;
        foreach (var q in NoiseCombinations(noises))
          most = Math.Max(most, height(b, q));
        Check(most <= off + 1e-4f, $"base height {b}: the game's {name} formula reaches at most {most:0.###} units, the bound with Override All off is {off:0.###}");
      }
      // Swamp (0.137 + 0.03 * a product of two noises + the small ones) and Ocean (the base height) do not depend on more than that.
      Check(Math.Max(b, 0.137f + 0.03f * 1.15f * 1.15f + 0.01f * 1.15f + 0.003f * 1.15f) <= off + 1e-4f, $"base height {b}: Swamp and Ocean are under the bound too");
    }
    // Where Mountain wins (over 0.9 units, 180 m) the bound is 2b - 0.4 plus the largest the Mountain biome's own noise can add; the measured largest on the
    // dedicated server (4,000,000 points of the real Perlin function) was 0.223 units, the bound's allowance is the formula's maximum at a noise of 1.15.
    float allowance = Metres(81f, false) / 200f - (2f * 80.85f - 0.4f);
    Check(allowance > 0.223f && allowance < 0.5f, $"the Mountain biome's noise allowance is {allowance:0.###} units ({allowance * 200f:0} m): over the 0.223 units ({0.223f * 200f:0} m) measured");

    // Measured on the rig (tools/high-tests/rig, a world of Amount 81 and Override All off on the synthetic ziggurat map, 20,000 random points per box): the
    // terrain minus 200 * (2b - 0.4) on a flat plateau was -0.77 .. +40.1 m (mean +10.9 m); on the cliffs of the ziggurat (slopes 7 to 35) up to +164 m, which is the
    // slope term (4 m per unit of slope) and not in the bound. The summit plateau (base height 80.5): 32,119 .. 32,160 m.
    float summit = HighTerrain.MaxMetres(BoundWorld(80.65f, false));
    Check(summit > 32160f && summit - 32160f < 100f, $"the bound for the summit plateau of the rig's world (Amount 80.65): {summit:0} m, over its measured highest 32,160 m by {summit - 32160f:0} m");
  }

  private static bool IsStore(Instruction i) => i.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

  // ---- the game's own formulas, as they are in both builds ------------------------------------------------------------------
  private static void BoundGuardTests()
  {
    foreach (var dll in new[] { "client", "server" })
    {
      Section($"the Mountain and Mistlands height formulas in the {dll}'s game assembly");
      using var module = ModuleDefinition.ReadModule(Path.Combine(Libs, "1.0", dll, "assembly_valheim.dll"));
      var generator = module.GetTypes().First(t => t.FullName == "WorldGenerator");
      MethodDefinition FindMethod(string name) => generator.Methods.Single(m => m.Name == name);
      var mountain = FindMethod("GetSnowMountainHeight").Body.Instructions.ToArray();
      // `baseHeight - 0.4` (num4), `baseHeight + num4` (the doubling), `num5 * 0.2` (its noise), `* 2.0 * tilt`, and the height multiplier of 200 m.
      int Count(Instruction[] code, double value) => code.Count(i => i.OpCode.Code == Code.Ldc_R8 && i.Operand is double d && Math.Abs(d - value) < 1e-6);
      Check(Count(mountain, 0.4) == 3 && Count(mountain, 0.2) == 3 && Count(mountain, 2.0) == 1 && Count(mountain, 0.5) == 1 && Count(mountain, 0.003) == 1 && Count(mountain, 0.01) == 3,
        $"{dll}: GetSnowMountainHeight has the constants 0.4 (x3), 0.2 (x3), 2.0, 0.5, 0.003, 0.01 (x3): got 0.4 x{Count(mountain, 0.4)}, 0.2 x{Count(mountain, 0.2)}, 2.0 x{Count(mountain, 2.0)}, 0.5 x{Count(mountain, 0.5)}, 0.003 x{Count(mountain, 0.003)}, 0.01 x{Count(mountain, 0.01)}");
      // The first constant is the 0.4 of `num4 = baseHeight - 0.4`: ldc 0.4, sub, conv.r4, stloc; and an addition follows before the next constant (baseHeight + num4).
      int first = Array.FindIndex(mountain, i => i.OpCode.Code == Code.Ldc_R8 && i.Operand is double d && Math.Abs(d - 0.4) < 1e-6);
      int next = first < 0 ? -1 : Array.FindIndex(mountain, first + 1, i => i.OpCode.Code == Code.Ldc_R8);
      bool doubled = first > 0 && mountain[first + 1].OpCode.Code == Code.Sub && IsStore(mountain[first + 3])
        && mountain.Skip(first + 4).Take(next - first - 4).Any(i => i.OpCode.Code == Code.Add);
      Check(doubled, $"{dll}: GetSnowMountainHeight is `baseHeight - 0.4` stored, then `baseHeight + (baseHeight - 0.4)`: the Mountain biome doubles what is over 80 m");
      Check(mountain.Count(i => i.OpCode.Code is Code.Call or Code.Callvirt && i.Operand is MethodReference { Name: "AddRivers" }) == 1
            && mountain.Count(i => i.OpCode.Code is Code.Call or Code.Callvirt && i.Operand is MethodReference { Name: "GetBaseHeight" }) == 1
            && mountain.Count(i => i.OpCode.Code is Code.Call or Code.Callvirt && i.Operand is MethodReference { Name: "BaseHeightTilt" }) == 1,
        $"{dll}: GetSnowMountainHeight reads GetBaseHeight, BaseHeightTilt and AddRivers once each");
      var mistlands = FindMethod("GetMistlandsHeight").Body.Instructions.ToArray();
      Check(Count(mistlands, 0.4) >= 1 && Count(mistlands, 1.5) == 1 && Count(mistlands, 400.0) == 2 && Count(mistlands, 7.0) == 1,
        $"{dll}: GetMistlandsHeight has 0.4, 1.5 (the power), 400 (the terrace, twice) and 7: got 1.5 x{Count(mistlands, 1.5)}, 400 x{Count(mistlands, 400.0)}, 7 x{Count(mistlands, 7.0)}");
      Check(FindMethod("GetHeightMultiplier").Body.Instructions.Any(i => i.OpCode.Code == Code.Ldc_R4 && i.Operand is float f && f == 200f),
        $"{dll}: the game's height multiplier is 200 m a unit");
    }
  }
}

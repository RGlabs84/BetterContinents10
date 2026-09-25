// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-25 for version-agnostic wording (0.9.1).

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // Deep North weather follows the biome map.
  //
  // Vanilla EnvMan asks WorldGenerator.IsDeepnorth with the camera's HEIGHT as the second coordinate, in both
  // places it decides Deep North weather, EnvMan.UpdateEnvironment and EnvMan.GetBiome:
  //
  //     bool flag  = WorldGenerator.IsAshlands(position.x, position.z);
  //     bool flag2 = WorldGenerator.IsDeepnorth(position.x, position.y);     <- y is the camera height
  //
  // IsAshlands on the line above gets z. On a Better Continents world with a biome map, IsDeepnorth is patched to
  // read the biome map (WorldGeneratorPatch.IsDeepnorthPrefix), so it samples the map at (x, camera height) - the
  // map's middle rows - and a map with Deep North there gives Deep North weather at sea anywhere in that x band.
  //
  // Both calls are rewritten to DeepNorthWeather.IsDeepnorth(x, y, z). It uses z on a Better Continents world with a
  // biome map, so Deep North weather at sea follows the biome map as Ashlands weather already does, and y
  // everywhere else, so every other world behaves exactly as vanilla, bug included.
  public static class DeepNorthWeather
  {
    // Set by DynamicPatch: a Better Continents world with a biome map.
    public static bool UseZ;
    public static int RewrittenCalls;

    public static bool IsDeepnorth(float x, float y, float z) => WorldGenerator.IsDeepnorth(x, UseZ ? z : y);

    private static readonly MethodInfo? Vanilla = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth), [typeof(float), typeof(float)]);
    private static readonly MethodInfo? Helper = AccessTools.Method(typeof(DeepNorthWeather), nameof(IsDeepnorth), [typeof(float), typeof(float), typeof(float)]);
    private static readonly FieldInfo? FieldY = AccessTools.Field(typeof(Vector3), nameof(Vector3.y));
    private static readonly FieldInfo? FieldZ = AccessTools.Field(typeof(Vector3), nameof(Vector3.z));

    // Rewrites every `ldloc <v>; ldfld Vector3::y; call WorldGenerator::IsDeepnorth(float, float)` into
    // `ldloc <v>; ldfld Vector3::y; ldloc <v>; ldfld Vector3::z; call DeepNorthWeather::IsDeepnorth(float, float, float)`.
    // Anything that does not match that exact shape is left alone (vanilla behaviour) and reported.
    public static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, string where, out int rewritten)
    {
      var code = new List<CodeInstruction>(instructions);
      rewritten = 0;
      int calls = 0;
      if (Vanilla == null || Helper == null || FieldY == null || FieldZ == null)
      {
        LogError($"Deep North weather: a member was not found; {where} is left unchanged.");
        return code;
      }
      for (int i = 0; i < code.Count; i++)
      {
        if (!code[i].Calls(Vanilla))
          continue;
        calls++;
        if (i < 2 || !code[i - 1].LoadsField(FieldY) || !code[i - 2].IsLdloc())
          continue;
        var load = new CodeInstruction(code[i - 2].opcode, code[i - 2].operand);
        code.Insert(i, load);
        code.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, FieldZ));
        i += 2;
        code[i].opcode = OpCodes.Call;
        code[i].operand = Helper;
        rewritten++;
      }
      if (rewritten != 1 || calls != 1)
        LogWarning($"Deep North weather: expected one IsDeepnorth(position.x, position.y) call in {where}, found {calls} call(s) and rewrote {rewritten}.");
      return code;
    }
  }

  [HarmonyPatch(typeof(EnvMan))]
  private class EnvManDeepNorthPatch
  {
    // private void EnvMan.UpdateEnvironment(long sec, BiomeSector biome) - EnvMan.cs:587.
    [HarmonyTranspiler, HarmonyPatch("UpdateEnvironment")]
    private static IEnumerable<CodeInstruction> UpdateEnvironmentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      var code = DeepNorthWeather.Rewrite(instructions, "EnvMan.UpdateEnvironment", out var n);
      DeepNorthWeather.RewrittenCalls += n;
      if (n > 0)
        Log($"Deep North weather: EnvMan.UpdateEnvironment now asks IsDeepnorth with the camera's z on worlds with a biome map ({n} call).");
      return code;
    }

    // private BiomeSector EnvMan.GetBiome() - EnvMan.cs:679.
    [HarmonyTranspiler, HarmonyPatch("GetBiome")]
    private static IEnumerable<CodeInstruction> GetBiomeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      var code = DeepNorthWeather.Rewrite(instructions, "EnvMan.GetBiome", out var n);
      DeepNorthWeather.RewrittenCalls += n;
      if (n > 0)
        Log($"Deep North weather: EnvMan.GetBiome now asks IsDeepnorth with the camera's z on worlds with a biome map ({n} call).");
      return code;
    }
  }
}

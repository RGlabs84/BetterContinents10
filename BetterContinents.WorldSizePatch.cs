// Modified by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
namespace BetterContinents;

// The game's own edge of the world, moved to the world's "World Size" and "Edge Size" (or out of reach, with the edge
// drop-off disabled). Vanilla writes its 10000 m world and 500 m edge into the code as constants: the ship's push back,
// the kill zone, the water's edge, the wind and the terrain formulas. The transpilers here replace them. Patcher's
// PatchWorldSize switches two groups: the edge checks, and the Ashlands' limit (and with it the size Expand World Data
// is told).
//
// Each group remembers the size it was patched for (WorldGeometry), and its transpilers read that size. Harmony runs all
// of a method's transpilers again whenever any patch of that method changes, ours or another mod's (Expand World Size
// patches GetBaseHeight too), so every run must find the size of its own group. A group is patched again when its size
// changes, so the edge always follows the world being loaded, the way it does for the first world loaded.
//
// Expand World Size looks this class up by name for a SetStretch(worldStretch, biomeStretch) method. There is none (nor
// in Better Continents 0.7.30): its World Stretch does not reach these limits.
public class WorldSizeHelper
{
  // ---- the groups ---------------------------------------------------------------------------------------------------

  // A game method and this class's patches on it.
  internal sealed class Part(Func<MethodBase> target, string? prefix = null, string? postfix = null, string? transpiler = null)
  {
    private static HarmonyMethod? Method(string? name) =>
      name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(WorldSizeHelper), name));

    // What Harmony.Patch does, through the processor: the call that is the same in BepInEx's HarmonyX and in the
    // Lib.Harmony the offline tests (tools/size-tests) run it on.
    public void Patch(Harmony harmony)
    {
      var processor = harmony.CreateProcessor(target());
      if (Method(prefix) is { } before) processor.AddPrefix(before);
      if (Method(postfix) is { } after) processor.AddPostfix(after);
      if (Method(transpiler) is { } rewrite) processor.AddTranspiler(rewrite);
      processor.Patch();
    }

    public void Unpatch(Harmony harmony)
    {
      foreach (var name in new[] { prefix, postfix, transpiler })
        if (name != null)
          harmony.Unpatch(target(), AccessTools.Method(typeof(WorldSizeHelper), name));
    }
  }

  // Patches that are on for any size but vanilla's, and read the size they were patched for.
  internal sealed class Group
  {
    private readonly Part[] parts;
    // The size the group is patched for; vanilla's while it is off.
    public WorldGeometry Size { get; private set; } = WorldGeometry.Vanilla;
    public bool Patched { get; private set; }

    internal Group(params Part[] parts) => this.parts = parts;

    // Off for vanilla's size, on for any other, and patched again when the size changes. False when nothing changed.
    public bool Update(Harmony harmony, WorldGeometry size)
    {
      if (!Changes(size))
        return false;
      var unpatch = Patched;
      Size = size;
      Patched = !size.IsVanilla;
      if (unpatch)
        foreach (var part in parts)
          part.Unpatch(harmony);
      if (Patched)
        foreach (var part in parts)
          part.Patch(harmony);
      return true;
    }

    // Whether Update would switch the group: on or off, or the size it is patched for.
    internal bool Changes(WorldGeometry size) => size.IsVanilla ? Patched : !Patched || !size.SameAs(Size);

    // For the offline tests: the group as patched for this size, without Harmony.
    internal void Assume(WorldGeometry size)
    {
      Size = size;
      Patched = !size.IsVanilla;
    }
  }

  // The ship's push back, the kill zone, the water's edge and the wind, and the base and biome heights' edge.
  internal static readonly Group EdgeChecks = new(
    new Part(() => AccessTools.Method(typeof(Ship), nameof(Ship.ApplyEdgeForce)), transpiler: nameof(ApplyEdgeForceTranspiler)),
    new Part(() => AccessTools.Method(typeof(Player), nameof(Player.EdgeOfWorldKill)),
      prefix: nameof(EdgeOfWorldKillPrefix), transpiler: nameof(EdgeOfWorldKillTranspiler)),
    new Part(() => AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.SetupMaterial)), prefix: nameof(SetupMaterialPrefix)),
    new Part(() => AccessTools.Method(typeof(EnvMan), nameof(EnvMan.Awake)), postfix: nameof(ScaleGlobalWaterSurfacePostFix)),
    new Part(() => AccessTools.Method(typeof(EnvMan), nameof(EnvMan.UpdateWind)), transpiler: nameof(UpdateWindTranspiler)),
    new Part(() => AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface)), transpiler: nameof(ReplaceTotalSize)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)), transpiler: nameof(ReplaceTotalSize)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBaseHeight)), transpiler: nameof(GetBaseHeightTranspiler)));

  // The Ashlands' limit.
  internal static readonly Group WorldSize = new(
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsHeight)), transpiler: nameof(GetAshlandsHeightTranspiler)));

  public static void PatchEdgeChecks(Harmony harmony, float worldSize, float edgeSize)
  {
    if (!EdgeChecks.Update(harmony, new WorldGeometry(worldSize, edgeSize)))
      return;
    // Water already in the scene was set up with the edge as it was: move its edge too.
    RefreshSetupMaterial();
    if (EnvMan.instance)
      ScaleGlobalWaterSurface(EnvMan.instance);
  }

  public static void PatchWorldSize(Harmony harmony, float worldSize, float edgeSize)
  {
    if (WorldSize.Update(harmony, new WorldGeometry(worldSize, edgeSize)) && WorldSize.Patched)
      EWD.RefreshSize(WorldSize.Size.WorldRadius, WorldSize.Size.TotalRadius, 1f, 1f);
  }

  // ---- the edge checks ----------------------------------------------------------------------------------------------

  private static IEnumerable<CodeInstruction> ApplyEdgeForceTranspiler(IEnumerable<CodeInstruction> instructions) => ModifyEdgeCheck(instructions);
  private static IEnumerable<CodeInstruction> EdgeOfWorldKillTranspiler(IEnumerable<CodeInstruction> instructions) => ModifyEdgeCheck(instructions);
  // Safer to simply skip when in dungeons.
  private static bool EdgeOfWorldKillPrefix(Player __instance) => __instance.transform.position.y < 4000f;

  private static IEnumerable<CodeInstruction> ModifyEdgeCheck(IEnumerable<CodeInstruction> instructions)
  {
    var total = EdgeChecks.Size.TotalRadius;
    return new Constants(instructions)
      .Replace(10420f, total - 80)
      .Replace(10500f, total)
      .Codes;
  }

  private static void RefreshSetupMaterial()
  {
    var total = EdgeChecks.Size.TotalRadius;
    var objects = UnityEngine.Object.FindObjectsByType<WaterVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
    foreach (var water in objects)
    {
      water.m_waterSurface.material.SetFloat("_WaterEdge", total);
    }
  }
  private static void SetupMaterialPrefix(WaterVolume __instance)
  {
    __instance.m_waterSurface.material.SetFloat("_WaterEdge", EdgeChecks.Size.TotalRadius);
  }
  private static void ScaleGlobalWaterSurface(EnvMan obj)
  {
    var water = obj.transform.Find("WaterPlane").Find("watersurface");
    water.GetComponent<MeshRenderer>().material.SetFloat("_WaterEdge", EdgeChecks.Size.TotalRadius);
  }
  private static void ScaleGlobalWaterSurfacePostFix(EnvMan __instance) => ScaleGlobalWaterSurface(__instance);

  private static IEnumerable<CodeInstruction> UpdateWindTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var total = EdgeChecks.Size.TotalRadius;
    return new Constants(instructions)
      .Replace(10500f, total)
      // Removes the subtraction of m_edgeOfWorldWidth (already applied above).
      .Nop(3)
      .Replace(10500f, total)
      // Removes the subtraction of m_edgeOfWorldWidth (already applied above).
      .Nop(3)
      .Replace(10500f, total)
      .Codes;
  }

  // WaterVolume.GetWaterSurface and WorldGenerator.GetBiomeHeight.
  private static IEnumerable<CodeInstruction> ReplaceTotalSize(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions).Replace(10500f, EdgeChecks.Size.TotalRadius).Codes;

  private static IEnumerable<CodeInstruction> GetBaseHeightTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var size = EdgeChecks.Size;
    var offset1 = AccessTools.Field(typeof(WorldGenerator), nameof(WorldGenerator.m_offset1));
    return new Constants(instructions)
      // Skipping the menu part.
      .SkipTo(code => code.opcode == OpCodes.Ldfld && Equals(code.operand, offset1))
      .Replace(10000f, size.WorldRadius)
      .Replace(10000f, size.WorldRadius)
      .Replace(10500f, size.TotalRadius)
      .Replace(10490f, size.TotalRadius - 10f)
      .Replace(10500f, size.TotalRadius)
      .Codes;
  }

  // ---- the world size -----------------------------------------------------------------------------------------------

  private static IEnumerable<CodeInstruction> GetAshlandsHeightTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions).Replace(10150d, WorldSize.Size.TotalRadius).Codes;

  // ---- the game's constants -----------------------------------------------------------------------------------------

  // A method's instructions, its constants replaced one after another: each replacement takes the next load of that
  // constant after the previous replacement (as CodeMatcher's MatchForward and SetOperandAndAdvance did here), and a
  // constant that is not there fails the patch.
  internal sealed class Constants(IEnumerable<CodeInstruction> instructions)
  {
    public readonly List<CodeInstruction> Codes = instructions.ToList();
    private int position;

    // On to the first instruction from here that matches.
    public Constants SkipTo(Func<CodeInstruction, bool> match)
    {
      position = Find(match, "the instruction to start from");
      return this;
    }

    public Constants Replace(float value, float newValue)
    {
      int at = Find(code => code.opcode == OpCodes.Ldc_R4 && code.operand is float f && f.Equals(value), $"ldc.r4 {value}");
      Codes[at].operand = newValue;
      position = at + 1;
      return this;
    }

    public Constants Replace(double value, double newValue)
    {
      int at = Find(code => code.opcode == OpCodes.Ldc_R8 && code.operand is double d && d.Equals(value), $"ldc.r8 {value}");
      Codes[at].operand = newValue;
      position = at + 1;
      return this;
    }

    // The next instructions become nops (they keep their operands, and any labels).
    public Constants Nop(int count)
    {
      for (int i = 0; i < count; i++)
      {
        if (position >= Codes.Count)
          throw new InvalidOperationException("World size: the method ends before the instructions to remove.");
        Codes[position++].opcode = OpCodes.Nop;
      }
      return this;
    }

    private int Find(Func<CodeInstruction, bool> match, string what)
    {
      for (int i = position; i < Codes.Count; i++)
        if (match(Codes[i]))
          return i;
      throw new InvalidOperationException($"World size: {what} is not where it was expected.");
    }
  }
}

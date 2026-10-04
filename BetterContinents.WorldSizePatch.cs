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
// PatchWorldSize switches three groups: the edge checks, the Ashlands' limit (and with it the size Expand World Data is
// told), and on a world made since 0.10 the layout: everything else the game places by its distance from the centre
// (the alt-biome grid, the locations, its own biome bands, the Ashlands and the Deep North, its lakes and rivers), and
// the minimap.
//
// Each group remembers the size it was patched for (WorldGeometry), and its transpilers read that size. Harmony runs all
// of a method's transpilers again whenever any patch of that method changes, ours or another mod's (Expand World Size
// patches GetBaseHeight too), so every run must find the size of its own group. A group is patched again when its size
// changes, so the edge always follows the world being loaded, the way it does for the first world loaded.
//
// Expand World Size looks this class up by name for SetStretch(worldStretch, biomeStretch) (from 0.10; 0.7.30 to 0.9
// had none).
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

    // For the log: the game's method.
    public override string ToString()
    {
      try
      {
        return target() is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "(a method not in this game)";
      }
      catch (Exception e)
      {
        return $"(a method not found: {e.Message})";
      }
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
    // A part that cannot be patched (another mod changed the method first) is logged, and the others are still patched.
    public bool Update(Harmony harmony, WorldGeometry size)
    {
      if (!Changes(size))
        return false;
      var unpatch = Patched;
      Size = size;
      Patched = !size.IsVanilla;
      if (unpatch)
        foreach (var part in parts)
          Try(part, "unpatch", () => part.Unpatch(harmony));
      if (Patched)
        foreach (var part in parts)
          Try(part, "patch", () => part.Patch(harmony));
      return true;
    }

    private static void Try(Part part, string what, Action action)
    {
      try
      {
        action();
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"World size: could not {what} {part}: {e.Message}");
      }
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

  // The layout (BetterContinents.LayoutGeometry): on for a world made since 0.10 whose own size is not vanilla's, while
  // Expand World Size, which lays out the world itself, is not installed. The game places all of this by the distance
  // from the centre of its 10000 m world; here those distances follow the world's World Size (and its alt-biome grid
  // and minimap its whole size), so a world of any size has vanilla's layout at its own scale. The terrain's detail
  // (each biome's hills, rocks and noise) keeps its size.
  internal static readonly Group Layout = new(
    // The alt-biome grid (AltBiomeWorldData: 2048 points of 12 m to 10500 m) covers the world. The game also places
    // its locations at the grid's points, so they reach the whole world too.
    new Part(() => AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.MapSpaceToWorldSpace), [typeof(float)]),
      transpiler: nameof(GridToWorldTranspiler)),
    new Part(() => AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.WorldSpaceToMapSpace), [typeof(float)]),
      transpiler: nameof(WorldToGridTranspiler)),
    new Part(() => AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints)),
      transpiler: nameof(GenerateBiomePointsTranspiler)),
    // The locations: within World Size of the centre (10000 m), and each one's distance from the centre at the world's
    // scale.
    new Part(() => AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone)), transpiler: nameof(GetRandomZoneTranspiler)),
    new Part(() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GenerateLocationsTimeSliced),
      [typeof(ZoneSystem.ZoneLocation), typeof(System.Diagnostics.Stopwatch), typeof(ZPackage)])), transpiler: nameof(GenerateLocationsTranspiler)),
    // The game's own biomes, where no biome map decides: its bands (the Swamp's limit included), its Ashlands ring in the
    // south and Deep North ring in the north, with their gaps, and the calm sea of the Deep North.
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), [typeof(float), typeof(float), typeof(float), typeof(bool)]),
      transpiler: nameof(GetBiomeTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsAshlands), [typeof(float), typeof(float)]),
      transpiler: nameof(AshlandsTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsOceanGradient), [typeof(float), typeof(float)]),
      transpiler: nameof(AshlandsTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsHeight)), transpiler: nameof(AshlandsTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.CreateAshlandsGap), [typeof(float), typeof(float)]),
      transpiler: nameof(AshlandsTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth), [typeof(float), typeof(float)]),
      transpiler: nameof(IsDeepnorthTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.CreateDeepNorthGap), [typeof(float), typeof(float)]),
      transpiler: nameof(DeepNorthTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.DeepNorthWaveFade), [typeof(float), typeof(float)]),
      transpiler: nameof(DeepNorthTranspiler)),
    // The lakes the rivers run between, and the streams' sources: searched for within World Size.
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.FindLakes)), transpiler: nameof(FindLakesTranspiler)),
    new Part(() => AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.FindStreamStartPoint)),
      transpiler: nameof(FindStreamStartPointTranspiler)));

  // Expand World Data, when installed, gives the locations its own distances at the world's size (its location data is
  // in fractions of the world's radius, which Better Continents tells it: PatchWorldSize), so they are not scaled again.
  internal static bool ScalesLocationDistances = true;

  internal static void PatchLayout(Harmony harmony, WorldGeometry size)
  {
    ScalesLocationDistances = !EWD.Installed;
    var was = Layout.Patched;
    if (Layout.Update(harmony, size))
    {
      if (Layout.Patched)
        BetterContinents.Log($"The world is laid out to its size ({size}): the alt-biome grid is {LayoutGrid.Size} points of {LayoutGrid.Pixel} m, "
          + $"the locations are placed within {size.WorldRadius} m{(ScalesLocationDistances ? $" and their distances from the centre are x{LayoutRatio:0.###}" : "")}, "
          + $"and the game's own biome bands, Ashlands, Deep North, lakes and rivers are x{LayoutRatio:0.###}.");
      else if (was)
        BetterContinents.Log("The world is laid out as vanilla's.");
    }
    ScaleMinimap(Minimap.instance);
  }

  // ---- Expand World Size --------------------------------------------------------------------------------------------

  // Expand World Size calls this by reflection (its compatibility/BetterContinents.cs), straight after SetSize, with its
  // World Stretch and Biome Stretch. Its World Stretch divides the positions the game asks about before Better
  // Continents reads its maps there, so any stretch but 1 magnifies the maps (only their centre shows); its Biome
  // Stretch changes only the game's own biome noise. Nothing here changes how a world is made: the log says it once
  // (BetterContinents.NoteExpandWorldSizeStretch).
  public static void SetStretch(float worldStretch, float biomeStretch)
  {
    ExpandWorldSizeStretch = worldStretch;
    BetterContinents.NoteExpandWorldSizeStretch();
  }

  internal static float ExpandWorldSizeStretch = 1f;

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

  // ---- the layout ---------------------------------------------------------------------------------------------------

  // The layout's distances over vanilla's: World Size over 10000 m.
  internal static double LayoutRatio => Layout.Size.WorldRadius / 10000.0;
  private static float Scaled(float vanilla) => (float)(vanilla * LayoutRatio);
  private static double Scaled(double vanilla) => vanilla * LayoutRatio;

  // The alt-biome grid a size has: vanilla's 12 m points, as many as cover it with vanilla's margin (vanilla's 2048 reach
  // 12288 m on its 10500 m world), an even number, so the centre falls between two points as in vanilla's. Past 8192 a
  // side (a world over 42000 m in radius) the points grow instead, to keep the grid's memory in bounds.
  internal readonly struct Grid(int size, float pixel)
  {
    public const int MaxSize = 8192;
    public readonly int Size = size;
    public readonly float Pixel = pixel;
    public float Half => Size / 2;
    public float HalfPixel => Pixel / 2f;

    public static Grid For(WorldGeometry world)
    {
      // Points from the centre to the grid's edge: vanilla's 1024.
      double half = 1024.0 * world.TotalRadius / 10500.0;
      return half <= MaxSize / 2 ? new Grid(2 * (int)Math.Ceiling(half), 12f) : new Grid(MaxSize, (float)(12.0 * half / (MaxSize / 2)));
    }
  }

  internal static Grid LayoutGrid => Grid.For(Layout.Size);

  // AltBiomeWorldData.MapSpaceToWorldSpace(float): (x - 1024f) * 12f + 6f.
  private static IEnumerable<CodeInstruction> GridToWorldTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var grid = LayoutGrid;
    return new Constants(instructions).Replace(1024f, grid.Half).Replace(12f, grid.Pixel).Replace(6f, grid.HalfPixel).Codes;
  }

  // AltBiomeWorldData.WorldSpaceToMapSpace(float): (int)((x - 6f) / 12f + 1024f).
  private static IEnumerable<CodeInstruction> WorldToGridTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var grid = LayoutGrid;
    return new Constants(instructions).Replace(6f, grid.HalfPixel).Replace(12f, grid.Pixel).Replace(1024f, grid.Half).Codes;
  }

  // AltBiomeWorldData.GenerateBiomePoints: a grid of 2048 x 2048 points, sampled within 10500 m (its square, 110250000)
  // and ocean beyond.
  private static IEnumerable<CodeInstruction> GenerateBiomePointsTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var size = LayoutGrid.Size;
    var radius = Layout.Size.TotalRadius;
    return new Constants(instructions).Replace(2048, size).Replace(110250000f, radius * radius).Replace(2048, size).Replace(2048, size).Codes;
  }

  // ZoneSystem.GetRandomZone: a zone within 10000 m (for the locations placed from the centre out).
  private static IEnumerable<CodeInstruction> GetRandomZoneTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions).Replace(10000f, Layout.Size.WorldRadius).Codes;

  // ZoneSystem.GenerateLocationsTimeSliced(location, ...)'s state machine: the range it starts from (10000 m), and the
  // location's minimum and maximum distance from the centre wherever it reads them (the start of a search from the
  // centre, and the two checks of each candidate).
  private static IEnumerable<CodeInstruction> GenerateLocationsTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var codes = new Constants(instructions).Replace(10000f, Layout.Size.WorldRadius);
    if (!ScalesLocationDistances)
      return codes.Codes;
    var ratio = (float)LayoutRatio;
    var min = AccessTools.Field(typeof(ZoneSystem.ZoneLocation), nameof(ZoneSystem.ZoneLocation.m_minDistance));
    var max = AccessTools.Field(typeof(ZoneSystem.ZoneLocation), nameof(ZoneSystem.ZoneLocation.m_maxDistance));
    return codes.Scale(min, ratio).Scale(min, ratio).Scale(min, ratio).Scale(max, ratio).Scale(max, ratio).Codes;
  }

  // WorldGenerator.GetBiome's bands: the Swamp from 2000 m to its limit (maxMarshDistance, 6000 m on a new world), the
  // Mistlands from 6000 m to 10000 m, the Plains from 3000 m to 8000 m, the Black Forest's patches from 600 m to 6000 m
  // and all of it past 5000 m (the starts as doubles).
  private static IEnumerable<CodeInstruction> GetBiomeTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions)
      .Replace(2000f, Scaled(2000f))
      .Scale(AccessTools.Field(typeof(WorldGenerator), nameof(WorldGenerator.maxMarshDistance)), (float)LayoutRatio)
      .Replace(6000d, Scaled(6000d))
      .Replace(10000f, Scaled(10000f))
      .Replace(3000d, Scaled(3000d))
      .Replace(8000f, Scaled(8000f))
      .Replace(600d, Scaled(600d))
      .Replace(6000f, Scaled(6000f))
      .Replace(5000d, Scaled(5000d))
      .Codes;

  // The Ashlands' ring: past 12000 m (ashlandsMinDistance) from a centre 4000 m south (ashlandsYOffset). Read by
  // IsAshlands, GetAshlandsOceanGradient, GetAshlandsHeight and CreateAshlandsGap.
  private static IEnumerable<CodeInstruction> AshlandsTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions)
      .ReplaceLoads(AccessTools.Field(typeof(WorldGenerator), nameof(WorldGenerator.ashlandsYOffset)), Scaled(-4000f))
      .ReplaceLoads(AccessTools.Field(typeof(WorldGenerator), nameof(WorldGenerator.ashlandsMinDistance)), Scaled(12000f))
      .Codes;

  // The Deep North's ring: past 12000 m from a centre 4000 m north. IsDeepnorth writes both as doubles.
  private static IEnumerable<CodeInstruction> IsDeepnorthTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions).Replace(4000d, Scaled(4000d)).Replace(12000d, Scaled(12000d)).Codes;

  // CreateDeepNorthGap and DeepNorthWaveFade write the centre as a float.
  private static IEnumerable<CodeInstruction> DeepNorthTranspiler(IEnumerable<CodeInstruction> instructions) =>
    new Constants(instructions).Replace(4000f, Scaled(4000f)).Replace(12000d, Scaled(12000d)).Codes;

  // WorldGenerator.FindLakes: every 128 m from -10000 m to 10000 m, within 10000 m of the centre.
  private static IEnumerable<CodeInstruction> FindLakesTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var radius = Layout.Size.WorldRadius;
    return new Constants(instructions).Replace(-10000f, -radius).Replace(-10000f, -radius)
      .Replace(10000f, radius).Replace(10000f, radius).Replace(10000f, radius).Codes;
  }

  // WorldGenerator.FindStreamStartPoint: a stream's source anywhere from -10000 m to 10000 m.
  private static IEnumerable<CodeInstruction> FindStreamStartPointTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var radius = Layout.Size.WorldRadius;
    return new Constants(instructions).Replace(-10000f, -radius).Replace(10000f, radius).Replace(-10000f, -radius).Replace(10000f, radius).Codes;
  }

  // ---- the minimap --------------------------------------------------------------------------------------------------

  // The minimap's pixels grow with the world, its texture keeping its size: vanilla's 2048 pixels of 12 m cover its 10500
  // m with a margin, and cover a world laid out to another size with the same margin. MinimapScale is what Better
  // Continents multiplied the current minimap's pixels by: 1 while the layout is vanilla's, which leaves them as the
  // game (or another mod) made them.
  internal static float MinimapScale { get; private set; } = 1f;
  internal static float LayoutMinimapScale => Layout.Patched ? Layout.Size.TotalRadius / WorldGeometry.Vanilla.TotalRadius : 1f;

  // Each scene's minimap starts with the game's own pixels.
  [HarmonyPatch(typeof(Minimap), nameof(Minimap.Awake))]
  private static class MinimapAwakePatch
  {
    private static void Postfix(Minimap __instance)
    {
      MinimapScale = 1f;
      ScaleMinimap(__instance);
    }
  }

  // The minimap's pixels for the layout (DynamicPatch, and each new minimap). A map already drawn is drawn again.
  internal static void ScaleMinimap(Minimap? map)
  {
    var scale = LayoutMinimapScale;
    if (map is null || !map || scale == MinimapScale || !(scale > 0f) || float.IsInfinity(scale))
      return;
    map.m_pixelSize = map.m_pixelSize / MinimapScale * scale;
    MinimapScale = scale;
    BetterContinents.Log($"The minimap's {map.m_textureSize} pixels are {map.m_pixelSize} m: they cover {map.m_textureSize * map.m_pixelSize} m.");
    if (map.m_hasGenerated)
      map.ForceRegen();
  }

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

    public Constants Replace(int value, int newValue)
    {
      int at = Find(code => code.opcode == OpCodes.Ldc_I4 && code.operand is int i && i == value, $"ldc.i4 {value}");
      Codes[at].operand = newValue;
      position = at + 1;
      return this;
    }

    // Every load of a static float field, anywhere in the method, becomes this value (keeping any labels).
    public Constants ReplaceLoads(FieldInfo field, float value)
    {
      int count = 0;
      foreach (var code in Codes)
        if (code.opcode == OpCodes.Ldsfld && Equals(code.operand, field))
        {
          code.opcode = OpCodes.Ldc_R4;
          code.operand = value;
          count++;
        }
      if (count == 0)
        throw new InvalidOperationException($"World size: ldsfld {field.Name} is not where it was expected.");
      return this;
    }

    // The next load of a float field is multiplied by factor (two instructions after it).
    public Constants Scale(FieldInfo field, float factor)
    {
      int at = Find(code => code.opcode == OpCodes.Ldfld && Equals(code.operand, field), $"ldfld {field.Name}");
      Codes.InsertRange(at + 1, [new CodeInstruction(OpCodes.Ldc_R4, factor), new CodeInstruction(OpCodes.Mul)]);
      position = at + 3;
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

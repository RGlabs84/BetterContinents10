// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3).
//
// Offline checks of the world's size: WorldGeometry against 0.9.4's maths, BetterContinents.SetSize (Expand World
// Size's way in), the size a world's maps span (MapGeometry: Expand World Size's, a world's own since 0.10, or
// vanilla's), and WorldSizeHelper, which moves the game's edge of the world: when a group is patched (again), its
// transpilers on the installed game's IL, which group's size each one reads, and the world-size group patched for real;
// the layout a world made since 0.10 gets at its own size (Layout.cs); the water depth patch on the game's IL (Water.cs); and the
// lakes' merge (Lakes.cs).
// "dotnet run -c Release -- dump" lists the constants of every method the transpilers rewrite.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Group = BetterContinents.WorldSizeHelper.Group;

internal static partial class Program
{
  private static int checks, failures;

  private static void Check(bool ok, string what)
  {
    checks++;
    if (ok) return;
    failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  private static int Main(string[] args)
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the
    // same name and the API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    var libs = Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools");
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(libs, "1.0", "client"), libs };
    AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
      foreach (var dir in dirs)
      {
        var path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path))
          return context.LoadFromAssemblyPath(Path.GetFullPath(path));
      }
      return null;
    };
    return args.Length > 0 && args[0] == "dump" ? Dump() : RunAll();
  }

  // The methods WorldSizeHelper's transpilers rewrite (the test project sees the game's own, unpublicized assembly).
  private static MethodBase Target(Type type, string name, Type[] args = null) => AccessTools.Method(type, name, args);
  private static readonly Type[] XY = [typeof(float), typeof(float)];
  private static readonly (string name, Func<MethodBase> method)[] Targets =
  [
    ("Ship.ApplyEdgeForce", () => Target(typeof(Ship), "ApplyEdgeForce")),
    ("Player.EdgeOfWorldKill", () => Target(typeof(Player), "EdgeOfWorldKill")),
    ("EnvMan.UpdateWind", () => Target(typeof(EnvMan), "UpdateWind")),
    ("WaterVolume.GetWaterSurface", () => Target(typeof(WaterVolume), "GetWaterSurface")),
    ("WorldGenerator.GetBiomeHeight", () => Target(typeof(WorldGenerator), "GetBiomeHeight")),
    ("WorldGenerator.GetBaseHeight", () => Target(typeof(WorldGenerator), "GetBaseHeight")),
    ("WorldGenerator.GetAshlandsHeight", () => Target(typeof(WorldGenerator), "GetAshlandsHeight")),
    // The layout's.
    ("AltBiomeWorldData.MapSpaceToWorldSpace", () => Target(typeof(AltBiomeWorldData), "MapSpaceToWorldSpace", [typeof(float)])),
    ("AltBiomeWorldData.WorldSpaceToMapSpace", () => Target(typeof(AltBiomeWorldData), "WorldSpaceToMapSpace", [typeof(float)])),
    ("AltBiomeWorldData.GenerateBiomePoints", () => Target(typeof(AltBiomeWorldData), "GenerateBiomePoints")),
    ("ZoneSystem.GetRandomZone", () => Target(typeof(ZoneSystem), "GetRandomZone")),
    ("ZoneSystem.GenerateLocationsTimeSliced", () => AccessTools.EnumeratorMoveNext(Target(typeof(ZoneSystem), "GenerateLocationsTimeSliced",
      [typeof(ZoneSystem.ZoneLocation), typeof(System.Diagnostics.Stopwatch), typeof(ZPackage)]))),
    ("WorldGenerator.GetBiome", () => Target(typeof(WorldGenerator), "GetBiome", [typeof(float), typeof(float), typeof(float), typeof(bool)])),
    ("WorldGenerator.IsAshlands", () => Target(typeof(WorldGenerator), "IsAshlands", XY)),
    ("WorldGenerator.GetAshlandsOceanGradient", () => Target(typeof(WorldGenerator), "GetAshlandsOceanGradient", XY)),
    ("WorldGenerator.CreateAshlandsGap", () => Target(typeof(WorldGenerator), "CreateAshlandsGap", XY)),
    ("WorldGenerator.IsDeepnorth", () => Target(typeof(WorldGenerator), "IsDeepnorth", XY)),
    ("WorldGenerator.CreateDeepNorthGap", () => Target(typeof(WorldGenerator), "CreateDeepNorthGap", XY)),
    ("WorldGenerator.DeepNorthWaveFade", () => Target(typeof(WorldGenerator), "DeepNorthWaveFade", XY)),
    ("WorldGenerator.FindLakes", () => Target(typeof(WorldGenerator), "FindLakes")),
    ("WorldGenerator.FindStreamStartPoint", () => Target(typeof(WorldGenerator), "FindStreamStartPoint")),
  ];

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int Dump()
  {
    foreach (var (name, method) in Targets)
    {
      var codes = PatchProcessor.GetOriginalInstructions(method());
      System.Console.WriteLine($"{name}: {codes.Count} instructions");
      for (int i = 0; i < codes.Count; i++)
        if (codes[i].opcode == OpCodes.Ldc_R4 || codes[i].opcode == OpCodes.Ldc_R8 || codes[i].opcode == OpCodes.Ldc_I4 || codes[i].opcode == OpCodes.Ldsfld
            || codes[i].opcode == OpCodes.Ldfld && codes[i].operand is FieldInfo { Name: "m_offset1" or "m_edgeOfWorldWidth" or "maxMarshDistance" or "m_minDistance" or "m_maxDistance" })
          System.Console.WriteLine($"  {i,4} {codes[i]}");
    }
    return 0;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    GeometryTests();
    SetSizeTests();
    MapGeometryTests();
    GroupTests();
    TranspilerTests();
    OwnSizeTests();
    PatchTests();
    WaterTests();
    LayoutTests();
    LakeTests();
    WorldSizeHelper.EdgeChecks.Assume(WorldGeometry.Vanilla);
    WorldSizeHelper.WorldSize.Assume(WorldGeometry.Vanilla);
    WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    System.Console.WriteLine($"size-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

  // ------------------------------------------------------------------------------------------------ WorldGeometry
  // 0.9.4's maths, as it was written: BetterContinents.SetSize's three fields, Normalize, NormalizedToWorld, and the
  // edge drop-off GetBaseHeightV1, V2 and V3 each wrote out.
  private static class Old
  {
    public static float TotalRadius = 10500f, TotalSize = TotalRadius * 2f, WorldRadius = 10000f;
    private static readonly Vector2 Half = Vector2.one * 0.5f;

    public static void SetSize(float size, float edge)
    {
      TotalRadius = size + edge;
      TotalSize = TotalRadius * 2f;
      WorldRadius = size;
    }
    public static float Normalize(float x) => Mathf.Clamp(x / TotalSize + 0.5f, 0f, 1f);
    public static Vector2 NormalizedToWorld(Vector2 p) => (p - Half) * TotalSize;
    public static float Edge(float finalHeight, float distance)
    {
      if (distance > WorldRadius)
      {
        float t = Utils.LerpStep(WorldRadius, TotalRadius, distance);
        finalHeight = Mathf.Lerp(finalHeight, -0.2f, t);
        var edge = TotalRadius - 10;
        if (distance > edge)
        {
          float t2 = Utils.LerpStep(edge, TotalRadius, distance);
          finalHeight = Mathf.Lerp(finalHeight, -2f, t2);
        }
      }
      return finalHeight;
    }
  }

  private static void GeometryTests()
  {
    var vanilla = WorldGeometry.Vanilla;
    Check(vanilla.WorldRadius == 10000f && vanilla.EdgeSize == 500f && vanilla.TotalRadius == 10500f && vanilla.TotalSize == 21000f && vanilla.IsVanilla,
      "vanilla: 10000 m and 500 m, 10500 m in radius, 21000 m across");
    Check(BC.Geometry.SameAs(vanilla) && BC.TotalRadius == 10500f && BC.TotalSize == 21000f && BC.WorldRadius == 10000f,
      "Better Continents starts at vanilla's size, in Geometry and in the old fields");

    var sizes = new (float w, float e)[] { (10000f, 500f), (20000f, 500f), (5000f, 250f), (12345.6f, 777.7f), (30000f, 0f), (100f, 50f), (1E30f, 500f), (9999.999f, 500.001f) };
    var random = new System.Random(4321);
    int compared = 0, differ = 0;
    foreach (var (w, e) in sizes)
    {
      Old.SetSize(w, e);
      var g = new WorldGeometry(w, e);
      if (!Same(g.TotalRadius, Old.TotalRadius) || !Same(g.TotalSize, Old.TotalSize) || !Same(g.WorldRadius, Old.WorldRadius))
        differ++;
      var xs = new List<float> { 0f, w, -w, g.TotalRadius, -g.TotalRadius, g.TotalRadius - 10f, g.TotalRadius - 9.999f, g.TotalRadius + 1f,
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e-30f };
      for (int i = 0; i < 3000; i++)
        xs.Add((float)((random.NextDouble() * 4 - 2) * Math.Min(g.TotalRadius, 1e7)));
      foreach (var x in xs)
      {
        compared++;
        if (!Same(g.Normalize(x), Old.Normalize(x)))
          differ++;
        var p = new Vector2((float)random.NextDouble() * 1.2f - 0.1f, x / Math.Max(1f, g.TotalSize));
        var a = g.NormalizedToWorld(p);
        var b = Old.NormalizedToWorld(p);
        if (!Same(a.x, b.x) || !Same(a.y, b.y))
          differ++;
        foreach (var h in new[] { -1f, 0f, 0.3f, 1f, float.NaN })
          if (!Same(g.DropOff(h, Math.Abs(x)), Old.Edge(h, Math.Abs(x))))
            differ++;
      }
    }
    Check(differ == 0, $"WorldGeometry gives 0.9.4's floats, bit for bit: sizes, Normalize, NormalizedToWorld and the edge drop-off ({compared} positions, {differ} differ)");

    Check(new WorldGeometry(20000f, 500f).SameAs(new WorldGeometry(20000f, 500f)) && !new WorldGeometry(20000f, 500f).SameAs(new WorldGeometry(20000f, 600f)),
      "SameAs compares the radius and the edge");
    Check(new WorldGeometry(float.NaN, 500f).SameAs(new WorldGeometry(float.NaN, 500f)) && !new WorldGeometry(float.NaN, 500f).IsVanilla,
      "a NaN size is the same as itself, and not vanilla's");
    Check(!new WorldGeometry(10000f, 500.0001f).IsVanilla && !new WorldGeometry(1E30f, 500f).IsVanilla && new WorldGeometry(10000f, 500f).IsVanilla,
      "only exactly 10000 m and 500 m is vanilla");
  }

  // ------------------------------------------------------------------------------------------------ SetSize
  private static void SetSizeTests()
  {
    int mark = CapturingLogHandler.Lines.Count;
    var noise = BC.WorldGeneratorPatch.BaseHeightNoise;
    BC.SetSize(20000f, 600f);
    var g = BC.Geometry;
    Check(g.WorldRadius == 20000f && g.EdgeSize == 600f && g.TotalRadius == 20600f && g.TotalSize == 41200f,
      "SetSize(20000, 600): Geometry is 20000 m and 600 m, 20600 m in radius, 41200 m across");
    Check(BC.TotalRadius == 20600f && BC.TotalSize == 41200f && BC.WorldRadius == 20000f,
      "SetSize keeps the old public fields for other mods: 20600, 41200, 20000");
    Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("Received world size 20000 and edge size 600")),
      "SetSize logs what it received");
    Check(BC.WorldGeneratorPatch.BaseHeightNoise != null && !ReferenceEquals(BC.WorldGeneratorPatch.BaseHeightNoise, noise),
      "SetSize rebuilds the base height noise for the new size");
    Check(Same(BC.Geometry.Normalize(10300f), Mathf.Clamp(10300f / 41200f + 0.5f, 0f, 1f)),
      "the maps then span 41200 m");
    BC.SetSize(10000f, 500f);
    Check(BC.Geometry.SameAs(WorldGeometry.Vanilla) && BC.TotalRadius == 10500f && BC.TotalSize == 21000f && BC.WorldRadius == 10000f,
      "SetSize(10000, 500) is vanilla's size again");
  }

  // ------------------------------------------------------------------------------------------------ the maps' size
  private static BC.BetterContinentsSettings World(int version, float worldSize, float edgeSize, bool enabled = true) =>
    new() { EnabledForThisWorld = enabled, Version = version, WorldSize = worldSize, EdgeSize = edgeSize };

  private static void MapGeometryTests()
  {
    var settings = BC.Settings;
    var ews = BC.ExpandWorldSizeGeometry;
    try
    {
      BC.ExpandWorldSizeGeometry = null;
      Check(BC.MapGeometry(World(11, 20000f, 500f)).IsVanilla && !World(11, 20000f, 500f).MapsSpanWorldSize,
        "a world made before 0.10 (version 11) with World Size 20000: its maps span vanilla's 21000 m, as they always did");
      Check(BC.MapGeometry(World(6, 20000f, 500f)).IsVanilla, "a legacy world (version 6): vanilla's");
      var own = BC.MapGeometry(World(12, 20000f, 500f));
      Check(World(12, 20000f, 500f).MapsSpanWorldSize && own.WorldRadius == 20000f && own.EdgeSize == 500f && own.TotalSize == 41000f,
        "a world made since 0.10 (version 12): its maps span its World Size and Edge Size, 41000 m");
      Check(BC.MapGeometry(World(12, 5000f, 250f)).TotalSize == 10500f, "a smaller one: 10500 m");
      Check(BC.MapGeometry(World(12, 20000f, 500f, enabled: false)).IsVanilla, "Better Continents off for the world: vanilla's");
      Check(BC.MapGeometry(World(12, 0f, 0f)).IsVanilla && BC.MapGeometry(World(12, float.NaN, 500f)).IsVanilla
            && BC.MapGeometry(World(12, -600f, 500f)).IsVanilla && BC.MapGeometry(World(12, float.MaxValue, float.MaxValue)).IsVanilla,
        "a World Size and Edge Size that make no world (nothing, NaN, negative, infinite): vanilla's");
      BC.ExpandWorldSizeGeometry = new WorldGeometry(15000f, 500f);
      Check(BC.MapGeometry(World(12, 20000f, 500f)).TotalRadius == 15500f && BC.MapGeometry(World(11, 20000f, 500f)).TotalRadius == 15500f
            && BC.MapGeometry(World(12, 20000f, 500f, enabled: false)).TotalRadius == 15500f,
        "Expand World Size installed: its size wins, for every world");

      // DynamicPatch's step: the maps follow the loaded world, and only a change rebuilds the noise.
      BC.ExpandWorldSizeGeometry = null;
      BC.Settings = World(12, 20000f, 500f);
      int mark = CapturingLogHandler.Lines.Count;
      BC.UpdateGeometry();
      var noise = BC.WorldGeneratorPatch.BaseHeightNoise;
      Check(BC.Geometry.TotalSize == 41000f && BC.TotalSize == 41000f && BC.TotalRadius == 20500f && BC.WorldRadius == 20000f,
        "UpdateGeometry: a version 12 world with World Size 20000 has its maps span 41000 m (and the old fields say so)");
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("The maps span 41000 m")), "and says so in the log");
      BC.UpdateGeometry();
      Check(ReferenceEquals(noise, BC.WorldGeneratorPatch.BaseHeightNoise), "the same size again: nothing rebuilt");
      Check(Same(BC.Geometry.Normalize(20500f), 1f) && Same(BC.Geometry.Normalize(10250f), 0.75f),
        "the map's edge is at the world's edge (20500 m), a quarter of the way in at 10250 m");
      Check(Same(BC.Geometry.DropOff(1f, 19000f), 1f) && BC.Geometry.DropOff(1f, 20250f) < 1f,
        "the land drops off past World Size (20000 m), not at vanilla's 10000 m");
      BC.Settings = World(11, 20000f, 500f);
      BC.UpdateGeometry();
      Check(BC.Geometry.IsVanilla && BC.TotalSize == 21000f, "back to a version 11 world: vanilla's 21000 m");
      BC.Settings = new BC.BetterContinentsSettings();
      BC.UpdateGeometry();
      Check(BC.Geometry.IsVanilla, "the main menu (Better Continents off): vanilla's");

      // Expand World Size's size wins over the world's own, and the log says so once.
      BC.ExpandWorldSizeGeometry = WorldGeometry.Vanilla;
      BC.Settings = World(12, 20000f, 500f);
      mark = CapturingLogHandler.Lines.Count;
      BC.UpdateGeometry();
      BC.UpdateGeometry();
      Check(BC.Geometry.IsVanilla, "with Expand World Size at vanilla's size, a version 12 world with World Size 20000 spans 21000 m");
      Check(CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains("Expand World Size sets the world's size")) == 1,
        "the log says once that Expand World Size's size wins over the world's");
    }
    finally
    {
      BC.Settings = settings;
      BC.ExpandWorldSizeGeometry = ews;
      BC.UpdateGeometry();
    }
  }

  // ------------------------------------------------------------------------------------------------ groups
  private static void GroupTests()
  {
    var harmony = new Harmony("size-tests.groups");
    var g = new Group();
    Check(!g.Update(harmony, WorldGeometry.Vanilla) && !g.Patched, "vanilla's size: off, and nothing to do");
    Check(g.Update(harmony, new WorldGeometry(20000f, 500f)) && g.Patched && g.Size.WorldRadius == 20000f, "World Size 20000: patched for 20000");
    Check(!g.Update(harmony, new WorldGeometry(20000f, 500f)) && g.Size.WorldRadius == 20000f, "the same size again: nothing to do");
    Check(g.Update(harmony, new WorldGeometry(30000f, 500f)) && g.Patched && g.Size.WorldRadius == 30000f,
      "another world's World Size 30000 while patched: patched again for 30000 (0.9.4 kept 20000)");
    Check(g.Update(harmony, new WorldGeometry(30000f, 600f)) && g.Size.EdgeSize == 600f, "a new edge size alone: patched again");
    Check(g.Update(harmony, WorldGeometry.Vanilla) && !g.Patched && g.Size.IsVanilla, "back to vanilla's size: off");
    Check(!g.Update(harmony, WorldGeometry.Vanilla), "vanilla's size again: nothing to do");
    Check(g.Update(harmony, new WorldGeometry(float.NaN, 500f)) && g.Patched && !g.Update(harmony, new WorldGeometry(float.NaN, 500f)),
      "a NaN size is patched once, not at every turn");
    Check(g.Update(harmony, new WorldGeometry(1E30f, 500f)) && g.Patched, "the drop-off disabled (1E30): patched");
    Check(g.Changes(WorldGeometry.Vanilla) && !g.Changes(new WorldGeometry(1E30f, 500f)) && g.Changes(new WorldGeometry(20000f, 500f)),
      "Changes says what Update would do");
  }

  // ------------------------------------------------------------------------------------------------ transpilers
  // A transpiler run on copies of the instructions (with their labels and blocks, which Clone drops).
  private static List<CodeInstruction> Run(string transpiler, List<CodeInstruction> instructions) =>
    ((IEnumerable<CodeInstruction>)typeof(WorldSizeHelper).GetMethod(transpiler, BindingFlags.NonPublic | BindingFlags.Static)
      .Invoke(null, [instructions.Select(i => new CodeInstruction(i)).ToList()])).ToList();

  private static List<CodeInstruction> Original(string type) =>
    PatchProcessor.GetOriginalInstructions(Targets.Single(t => t.name == type).method());

  private static string Show(CodeInstruction code) => code.operand switch
  {
    float f => $"{code.opcode.Name} {f.ToString("R", CultureInfo.InvariantCulture)}",
    double d => $"{code.opcode.Name} {d.ToString("R", CultureInfo.InvariantCulture)}",
    MemberInfo m => $"{code.opcode.Name} {m.Name}",
    null => code.opcode.Name,
    var o => $"{code.opcode.Name} {o}",
  };

  // Every instruction a transpiler changed, as "before -> after", in order; null when it added or removed any.
  private static string[] Changes(List<CodeInstruction> before, List<CodeInstruction> after)
  {
    if (before.Count != after.Count)
      return null;
    var changes = new List<string>();
    for (int i = 0; i < before.Count; i++)
    {
      if (Show(before[i]) != Show(after[i]))
        changes.Add($"{Show(before[i])} -> {Show(after[i])}");
      else if (!before[i].labels.SequenceEqual(after[i].labels) || before[i].blocks.Count != after[i].blocks.Count)
        changes.Add($"{Show(before[i])}: labels or blocks");
    }
    return [.. changes];
  }

  private static void Expect(string what, string transpiler, string method, params string[] expected)
  {
    var original = Original(method);
    var changes = Changes(original, Run(transpiler, original));
    Check(changes != null && changes.SequenceEqual(expected),
      $"{what}\n    expected: {string.Join(" | ", expected)}\n    got:      {(changes == null ? "instructions added or removed" : string.Join(" | ", changes))}");
  }

  private static void TranspilerTests()
  {
    // A world of 20000 m and 500 m: total 20500.
    WorldSizeHelper.EdgeChecks.Assume(new WorldGeometry(20000f, 500f));
    WorldSizeHelper.WorldSize.Assume(new WorldGeometry(20000f, 500f));
    Expect("Ship.ApplyEdgeForce: the push back starts 80 m inside the edge and is full at it", "ApplyEdgeForceTranspiler", "Ship.ApplyEdgeForce",
      "ldc.r4 10420 -> ldc.r4 20420", "ldc.r4 10500 -> ldc.r4 20500");
    Expect("Player.EdgeOfWorldKill: the same two limits", "EdgeOfWorldKillTranspiler", "Player.EdgeOfWorldKill",
      "ldc.r4 10420 -> ldc.r4 20420", "ldc.r4 10500 -> ldc.r4 20500");
    var nop3 = new[] { "ldarg.0 -> nop", "ldfld m_edgeOfWorldWidth -> nop m_edgeOfWorldWidth", "sub -> nop" };
    Expect("EnvMan.UpdateWind: the three limits, and the edge width no longer taken off twice", "UpdateWindTranspiler", "EnvMan.UpdateWind",
      ["ldc.r4 10500 -> ldc.r4 20500", .. nop3, "ldc.r4 10500 -> ldc.r4 20500", .. nop3, "ldc.r4 10500 -> ldc.r4 20500"]);
    Expect("WaterVolume.GetWaterSurface: the water's edge", "ReplaceTotalSize", "WaterVolume.GetWaterSurface", "ldc.r4 10500 -> ldc.r4 20500");
    Expect("WorldGenerator.GetBiomeHeight: the edge", "ReplaceTotalSize", "WorldGenerator.GetBiomeHeight", "ldc.r4 10500 -> ldc.r4 20500");
    Expect("WorldGenerator.GetBaseHeight: the world radius twice, the edge, 10 m inside it, the edge (none of the menu's)", "GetBaseHeightTranspiler", "WorldGenerator.GetBaseHeight",
      "ldc.r4 10000 -> ldc.r4 20000", "ldc.r4 10000 -> ldc.r4 20000", "ldc.r4 10500 -> ldc.r4 20500", "ldc.r4 10490 -> ldc.r4 20490", "ldc.r4 10500 -> ldc.r4 20500");
    Expect("WorldGenerator.GetAshlandsHeight: the Ashlands' limit becomes the edge (as a double)", "GetAshlandsHeightTranspiler", "WorldGenerator.GetAshlandsHeight",
      "ldc.r8 10150 -> ldc.r8 20500");

    // An odd size reaches the IL as the same float (and the Ashlands' limit as that float widened).
    WorldSizeHelper.EdgeChecks.Assume(new WorldGeometry(12345.6f, 777.7f));
    WorldSizeHelper.WorldSize.Assume(new WorldGeometry(12345.6f, 777.7f));
    float t = 12345.6f + 777.7f;
    string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    Expect("an odd size: the edge checks get its float sums", "ApplyEdgeForceTranspiler", "Ship.ApplyEdgeForce",
      $"ldc.r4 10420 -> ldc.r4 {R(t - 80)}", $"ldc.r4 10500 -> ldc.r4 {R(t)}");
    Expect("an odd size: GetBaseHeight", "GetBaseHeightTranspiler", "WorldGenerator.GetBaseHeight",
      $"ldc.r4 10000 -> ldc.r4 {R(12345.6f)}", $"ldc.r4 10000 -> ldc.r4 {R(12345.6f)}", $"ldc.r4 10500 -> ldc.r4 {R(t)}", $"ldc.r4 10490 -> ldc.r4 {R(t - 10f)}", $"ldc.r4 10500 -> ldc.r4 {R(t)}");
    Expect("an odd size: GetAshlandsHeight", "GetAshlandsHeightTranspiler", "WorldGenerator.GetAshlandsHeight",
      $"ldc.r8 10150 -> ldc.r8 {((double)t).ToString("R", CultureInfo.InvariantCulture)}");

    // Vanilla's size puts every constant back as it was: only the wind's subtraction and the Ashlands' limit change.
    WorldSizeHelper.EdgeChecks.Assume(WorldGeometry.Vanilla);
    WorldSizeHelper.WorldSize.Assume(WorldGeometry.Vanilla);
    foreach (var (transpiler, method) in new[] { ("ApplyEdgeForceTranspiler", "Ship.ApplyEdgeForce"), ("EdgeOfWorldKillTranspiler", "Player.EdgeOfWorldKill"),
      ("ReplaceTotalSize", "WaterVolume.GetWaterSurface"), ("ReplaceTotalSize", "WorldGenerator.GetBiomeHeight"), ("GetBaseHeightTranspiler", "WorldGenerator.GetBaseHeight") })
      Expect($"vanilla's size: {method} unchanged", transpiler, method);

    // A constant that is not there fails the patch, as CodeMatcher did.
    try
    {
      Run("ApplyEdgeForceTranspiler", Original("WorldGenerator.GetAshlandsHeight"));
      Check(false, "a method without the constants fails the patch");
    }
    catch (TargetInvocationException e)
    {
      Check(e.InnerException is InvalidOperationException && e.InnerException.Message.Contains("not where it was expected"),
        $"a method without the constants fails the patch ({e.InnerException?.Message})");
    }
  }

  // ------------------------------------------------------------------------------------------------ each group's own size
  private static void OwnSizeTests()
  {
    // The drop-off disabled on a World Size 20000 world: the edge checks are out of reach (1E30), the Ashlands' limit at
    // 20500. In 0.9.4 both groups shared one size, the last one set (20000), so a transpiler run again later (any patch of
    // GetBaseHeight, ours or Expand World Size's) put the edge back at 20500.
    WorldSizeHelper.EdgeChecks.Assume(new WorldGeometry(1E30f, 500f));
    WorldSizeHelper.WorldSize.Assume(new WorldGeometry(20000f, 500f));
    Expect("drop-off disabled, World Size 20000: GetBaseHeight run again keeps the edge out of reach", "GetBaseHeightTranspiler", "WorldGenerator.GetBaseHeight",
      "ldc.r4 10000 -> ldc.r4 1E+30", "ldc.r4 10000 -> ldc.r4 1E+30", "ldc.r4 10500 -> ldc.r4 1E+30", "ldc.r4 10490 -> ldc.r4 1E+30", "ldc.r4 10500 -> ldc.r4 1E+30");
    Expect("drop-off disabled, World Size 20000: the ship's edge out of reach too", "ApplyEdgeForceTranspiler", "Ship.ApplyEdgeForce",
      "ldc.r4 10420 -> ldc.r4 1E+30", "ldc.r4 10500 -> ldc.r4 1E+30");
    Expect("drop-off disabled, World Size 20000: the Ashlands' limit at 20500", "GetAshlandsHeightTranspiler", "WorldGenerator.GetAshlandsHeight",
      "ldc.r8 10150 -> ldc.r8 20500");
  }

  // ------------------------------------------------------------------------------------------------ patched for real
  // GetAshlandsHeight calls the engine's native code, which .NET cannot compile outside the game, so a stand-in holding
  // the same constant is patched instead, with the same transpiler, by a group made like WorldSize: the group sets its
  // size, then patches, and the transpiler reads WorldSize's (Assume stands in for that).
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static double AshlandsLimit() => 10150d;

  private static void PatchTests()
  {
    var harmony = new Harmony("size-tests.patch");
    var target = AccessTools.Method(typeof(Program), nameof(AshlandsLimit));
    var group = new Group(new WorldSizeHelper.Part(() => target, transpiler: "GetAshlandsHeightTranspiler"));
    int Ours() => Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == harmony.Id) ?? 0;
    bool To(WorldGeometry size)
    {
      if (group.Changes(size))
        WorldSizeHelper.WorldSize.Assume(size);
      return group.Update(harmony, size);
    }
    try
    {
      Check(To(new WorldGeometry(20000f, 500f)) && Ours() == 1 && AshlandsLimit() == 20500d,
        $"World Size 20000: patched through Harmony, the limit 20500 ({Ours()} transpilers, {AshlandsLimit()})");
      Check(!To(new WorldGeometry(20000f, 500f)) && Ours() == 1 && AshlandsLimit() == 20500d, "the same size again: left as it is");
      Check(To(new WorldGeometry(30000f, 500f)) && Ours() == 1 && AshlandsLimit() == 30500d,
        $"another world at 30000: unpatched and patched again, once, the limit 30500 ({Ours()} transpilers, {AshlandsLimit()})");
      Check(To(WorldGeometry.Vanilla) && Ours() == 0 && AshlandsLimit() == 10150d, "vanilla's size: unpatched, the method's own constant again");
    }
    catch (Exception e)
    {
      Check(false, $"patching the stand-in: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
    }

    // The real targets: every part's method is there, with the parameters its patches bind.
    var kill = Target(typeof(Player), "EdgeOfWorldKill");
    var setup = Target(typeof(WaterVolume), "SetupMaterial");
    var awake = Target(typeof(EnvMan), "Awake");
    Check(Targets.All(t => t.method() != null) && setup != null && awake != null,
      "every method the two groups patch is in the installed game");
    Check(!kill.IsStatic && !setup.IsStatic && !awake.IsStatic, "the prefixes' and postfix's __instance: EdgeOfWorldKill, SetupMaterial and Awake are instance methods");
  }
}

// Unity's Debug.Log ends in a native call; capture it instead.
internal sealed class CapturingLogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];

  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines)
      Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }

  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines)
      Lines.Add("[Exception] " + exception.GetType().Name + ": " + exception.Message);
  }
}

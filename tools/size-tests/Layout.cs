// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// The layout (WorldSizeHelper.Layout): which worlds are laid out to their own size, the alt-biome grid a size has, every
// layout transpiler on the installed game's IL, the pure ones patched through Harmony and run, the minimap's scale,
// Expand World Size's stretch note, and the alt-biome grid's settings on a world laid out to its size.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using BC = BetterContinents.BetterContinents;
using Group = BetterContinents.WorldSizeHelper.Group;
using Grid = BetterContinents.WorldSizeHelper.Grid;

internal static partial class Program
{
  private static void LayoutTests()
  {
    LayoutGeometryTests();
    GridTests();
    LayoutTranspilerTests();
    LayoutPatchTests();
    MinimapScaleTests();
    StretchNoteTests();
    OwnGridTests();
    WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    WorldSizeHelper.ScalesLocationDistances = true;
  }

  private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
  private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

  // ------------------------------------------------------------------------------------------------ which worlds
  private static void LayoutGeometryTests()
  {
    var ews = BC.ExpandWorldSizeGeometry;
    var installed = EWS.Installed;
    try
    {
      BC.ExpandWorldSizeGeometry = null;
      EWS.Installed = false;
      var own = BC.LayoutGeometry(World(12, 20000f, 500f));
      Check(own.WorldRadius == 20000f && own.EdgeSize == 500f, "a world made since 0.10 with World Size 20000: laid out to 20000 m and 500 m");
      Check(BC.LayoutGeometry(World(12, 5000f, 250f)).TotalRadius == 5250f, "a smaller one: to 5250 m");
      Check(BC.LayoutGeometry(World(12, 10000f, 500f)).IsVanilla, "one of vanilla's size: vanilla's layout, so nothing is patched");
      Check(BC.LayoutGeometry(World(11, 20000f, 500f)).IsVanilla && BC.LayoutGeometry(World(6, 20000f, 500f)).IsVanilla,
        "a world made before 0.10 (version 11, or a legacy one) with World Size 20000: vanilla's layout, as always");
      Check(BC.LayoutGeometry(World(12, 20000f, 500f, enabled: false)).IsVanilla, "Better Continents off for the world: vanilla's");
      Check(BC.LayoutGeometry(World(12, float.NaN, 500f)).IsVanilla && BC.LayoutGeometry(World(12, 0f, 0f)).IsVanilla
            && BC.LayoutGeometry(World(12, -600f, 500f)).IsVanilla && BC.LayoutGeometry(World(12, float.MaxValue, float.MaxValue)).IsVanilla,
        "a World Size and Edge Size that make no world: vanilla's");
      var noDropOff = World(12, 20000f, 500f);
      noDropOff.DisableMapEdgeDropoff = true;
      Check(BC.LayoutGeometry(noDropOff).TotalRadius == 20500f, "the edge drop-off disabled: still laid out to World Size and Edge Size (20500 m)");
      EWS.Installed = true;
      Check(BC.LayoutGeometry(World(12, 20000f, 500f)).IsVanilla, "Expand World Size installed: it lays out the world, so Better Continents does not");
      EWS.Installed = false;
      BC.ExpandWorldSizeGeometry = new WorldGeometry(15000f, 500f);
      Check(BC.LayoutGeometry(World(12, 20000f, 500f)).IsVanilla, "Expand World Size's size received: the same");
    }
    finally
    {
      BC.ExpandWorldSizeGeometry = ews;
      EWS.Installed = installed;
    }
  }

  // ------------------------------------------------------------------------------------------------ the grid
  private static void GridTests()
  {
    var vanilla = Grid.For(WorldGeometry.Vanilla);
    Check(vanilla.Size == 2048 && vanilla.Pixel == 12f && vanilla.Half == 1024f && vanilla.HalfPixel == 6f,
      "vanilla's size: vanilla's grid, 2048 points of 12 m centred on 1024");
    var big = Grid.For(new WorldGeometry(20000f, 500f));
    Check(big.Size == 4000 && big.Pixel == 12f && big.Half == 2000f, $"20500 m: 4000 points of 12 m ({big.Size} of {big.Pixel})");
    Check(Grid.For(new WorldGeometry(5000f, 250f)).Size == 1024, "5250 m: 1024 points");
    Check(Grid.For(new WorldGeometry(100f, 0f)).Size == 20 && Grid.For(new WorldGeometry(0.001f, 0f)).Size == 2, "a tiny world: a few points, never none");
    var huge = Grid.For(new WorldGeometry(1000000f, 500f));
    Check(huge.Size == Grid.MaxSize && huge.Pixel > 12f, $"a huge world: at most {Grid.MaxSize} points, bigger ones ({huge.Pixel} m)");

    int bad = 0, tried = 0;
    var random = new System.Random(77);
    var radii = new List<float> { 10500f, 10499f, 10501f, 20500f, 42000f, 42999f, 43007.8f, 50000f, 1000500f };
    for (int i = 0; i < 2000; i++)
      radii.Add((float)(random.NextDouble() * 60000.0 + 1.0));
    foreach (var radius in radii)
    {
      tried++;
      var grid = Grid.For(new WorldGeometry(radius, 0f));
      // Vanilla's margin: its grid reaches 12288 m on its 10500 m world.
      bool covers = grid.Half * grid.Pixel >= radius * 12288.0 / 10500.0 * (1 - 1e-6);
      if (grid.Size % 2 != 0 || grid.Size < 2 || grid.Size > Grid.MaxSize || grid.Pixel < 12f || !covers || grid.Size > 2 && grid.Pixel == 12f && (grid.Half - 1) * 12f >= radius * 12288.0 / 10500.0)
        bad++;
    }
    Check(bad == 0, $"every size's grid: an even number of points, at most {Grid.MaxSize}, of 12 m or more, covering the world with vanilla's margin and no more ({tried} sizes, {bad} wrong)");
  }

  // ------------------------------------------------------------------------------------------------ transpilers
  // Every change a transpiler made, in order: a replaced instruction as "before -> after", and an inserted
  // multiplication as "ldfld field * factor".
  private static string[] Edits(List<CodeInstruction> before, List<CodeInstruction> after)
  {
    var edits = new List<string>();
    int i = 0, j = 0;
    while (i < before.Count || j < after.Count)
    {
      if (i < before.Count && j < after.Count && Show(before[i]) == Show(after[j]))
      {
        if (!before[i].labels.SequenceEqual(after[j].labels) || before[i].blocks.Count != after[j].blocks.Count)
          edits.Add($"{Show(before[i])}: labels or blocks");
        i++;
        j++;
      }
      else if (j > 0 && j + 1 < after.Count && after[j - 1].opcode == OpCodes.Ldfld && after[j].opcode == OpCodes.Ldc_R4 && after[j + 1].opcode == OpCodes.Mul)
      {
        edits.Add($"{Show(after[j - 1])} * {F((float)after[j].operand)}");
        j += 2;
      }
      else if (i < before.Count && j < after.Count)
      {
        edits.Add($"{Show(before[i])} -> {Show(after[j])}");
        i++;
        j++;
      }
      else
        edits.Add(i < before.Count ? $"removed {Show(before[i++])}" : $"added {Show(after[j++])}");
    }
    return [.. edits];
  }

  private static void ExpectEdits(string what, string transpiler, string method, params string[] expected)
  {
    var original = Original(method);
    var edits = Edits(original, Run(transpiler, original));
    Check(edits.SequenceEqual(expected),
      $"{what}\n    expected: {string.Join(" | ", expected)}\n    got:      {string.Join(" | ", edits)}");
  }

  private static void LayoutTranspilerTests()
  {
    // A world of 20000 m and 500 m: its layout x2, its grid 4000 points of 12 m.
    WorldSizeHelper.Layout.Assume(new WorldGeometry(20000f, 500f));
    WorldSizeHelper.ScalesLocationDistances = true;
    Expect("AltBiomeWorldData.MapSpaceToWorldSpace: the grid's centre at 2000 (its 12 m points as they were)", "GridToWorldTranspiler",
      "AltBiomeWorldData.MapSpaceToWorldSpace", "ldc.r4 1024 -> ldc.r4 2000");
    Expect("AltBiomeWorldData.WorldSpaceToMapSpace: the same", "WorldToGridTranspiler", "AltBiomeWorldData.WorldSpaceToMapSpace", "ldc.r4 1024 -> ldc.r4 2000");
    Expect("AltBiomeWorldData.GenerateBiomePoints: 4000 x 4000 points, sampled within 20500 m", "GenerateBiomePointsTranspiler",
      "AltBiomeWorldData.GenerateBiomePoints", "ldc.i4 2048 -> ldc.i4 4000", $"ldc.r4 110250000 -> ldc.r4 {F(20500f * 20500f)}",
      "ldc.i4 2048 -> ldc.i4 4000", "ldc.i4 2048 -> ldc.i4 4000");
    Expect("ZoneSystem.GetRandomZone: a zone within 20000 m", "GetRandomZoneTranspiler", "ZoneSystem.GetRandomZone", "ldc.r4 10000 -> ldc.r4 20000");
    ExpectEdits("ZoneSystem.GenerateLocationsTimeSliced: the search from the centre starts within 20000 m, and every read of a location's distances is x2",
      "GenerateLocationsTranspiler", "ZoneSystem.GenerateLocationsTimeSliced",
      "ldc.r4 10000 -> ldc.r4 20000", "ldfld m_minDistance * 2", "ldfld m_minDistance * 2", "ldfld m_minDistance * 2",
      "ldfld m_maxDistance * 2", "ldfld m_maxDistance * 2");
    ExpectEdits("WorldGenerator.GetBiome: every band x2, the Swamp's limit too", "GetBiomeTranspiler", "WorldGenerator.GetBiome",
      "ldc.r4 2000 -> ldc.r4 4000", "ldfld maxMarshDistance * 2", "ldc.r8 6000 -> ldc.r8 12000", "ldc.r4 10000 -> ldc.r4 20000",
      "ldc.r8 3000 -> ldc.r8 6000", "ldc.r4 8000 -> ldc.r4 16000", "ldc.r8 600 -> ldc.r8 1200", "ldc.r4 6000 -> ldc.r4 12000", "ldc.r8 5000 -> ldc.r8 10000");
    Expect("WorldGenerator.IsAshlands: the Ashlands' ring past 24000 m from 8000 m south", "AshlandsTranspiler", "WorldGenerator.IsAshlands",
      "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsMinDistance -> ldc.r4 24000");
    Expect("WorldGenerator.GetAshlandsOceanGradient: the same ring", "AshlandsTranspiler", "WorldGenerator.GetAshlandsOceanGradient",
      "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsMinDistance -> ldc.r4 24000");
    Expect("WorldGenerator.GetAshlandsHeight: the same ring (its 10150 m limit is the world-size group's)", "AshlandsTranspiler", "WorldGenerator.GetAshlandsHeight",
      "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsMinDistance -> ldc.r4 24000");
    Expect("WorldGenerator.CreateAshlandsGap: the same ring", "AshlandsTranspiler", "WorldGenerator.CreateAshlandsGap",
      "ldsfld ashlandsYOffset -> ldc.r4 -8000", "ldsfld ashlandsMinDistance -> ldc.r4 24000");
    Expect("WorldGenerator.IsDeepnorth: the Deep North's ring past 24000 m from 8000 m north", "IsDeepnorthTranspiler", "WorldGenerator.IsDeepnorth",
      "ldc.r8 4000 -> ldc.r8 8000", "ldc.r8 12000 -> ldc.r8 24000");
    Expect("WorldGenerator.CreateDeepNorthGap: the same ring", "DeepNorthTranspiler", "WorldGenerator.CreateDeepNorthGap",
      "ldc.r4 4000 -> ldc.r4 8000", "ldc.r8 12000 -> ldc.r8 24000");
    Expect("WorldGenerator.DeepNorthWaveFade: the same ring", "DeepNorthTranspiler", "WorldGenerator.DeepNorthWaveFade",
      "ldc.r4 4000 -> ldc.r4 8000", "ldc.r8 12000 -> ldc.r8 24000");
    Expect("WorldGenerator.FindLakes: lakes looked for within 20000 m", "FindLakesTranspiler", "WorldGenerator.FindLakes",
      "ldc.r4 -10000 -> ldc.r4 -20000", "ldc.r4 -10000 -> ldc.r4 -20000", "ldc.r4 10000 -> ldc.r4 20000", "ldc.r4 10000 -> ldc.r4 20000", "ldc.r4 10000 -> ldc.r4 20000");
    Expect("WorldGenerator.FindStreamStartPoint: streams' sources within 20000 m either way", "FindStreamStartPointTranspiler", "WorldGenerator.FindStreamStartPoint",
      "ldc.r4 -10000 -> ldc.r4 -20000", "ldc.r4 10000 -> ldc.r4 20000", "ldc.r4 -10000 -> ldc.r4 -20000", "ldc.r4 10000 -> ldc.r4 20000");

    // Expand World Data installed: it gives the locations their distances at the world's size itself.
    WorldSizeHelper.ScalesLocationDistances = false;
    ExpectEdits("with Expand World Data: only the search from the centre changes", "GenerateLocationsTranspiler", "ZoneSystem.GenerateLocationsTimeSliced",
      "ldc.r4 10000 -> ldc.r4 20000");
    WorldSizeHelper.ScalesLocationDistances = true;

    // An odd size: the same floats the layout's own sums give.
    var odd = new WorldGeometry(12345.6f, 777.7f);
    WorldSizeHelper.Layout.Assume(odd);
    double ratio = 12345.6f / 10000.0;
    var grid = Grid.For(odd);
    Check(grid.Size == 2 * (int)Math.Ceiling(1024.0 * odd.TotalRadius / 10500.0) && WorldSizeHelper.LayoutRatio == ratio,
      $"an odd size: its grid ({grid.Size}) and ratio ({WorldSizeHelper.LayoutRatio})");
    Expect("an odd size: MapSpaceToWorldSpace", "GridToWorldTranspiler", "AltBiomeWorldData.MapSpaceToWorldSpace", $"ldc.r4 1024 -> ldc.r4 {F(grid.Half)}");
    Expect("an odd size: GetRandomZone", "GetRandomZoneTranspiler", "ZoneSystem.GetRandomZone", $"ldc.r4 10000 -> ldc.r4 {F(12345.6f)}");
    ExpectEdits("an odd size: GetBiome", "GetBiomeTranspiler", "WorldGenerator.GetBiome",
      $"ldc.r4 2000 -> ldc.r4 {F((float)(2000f * ratio))}", $"ldfld maxMarshDistance * {F((float)ratio)}", $"ldc.r8 6000 -> ldc.r8 {F(6000d * ratio)}",
      $"ldc.r4 10000 -> ldc.r4 {F((float)(10000f * ratio))}", $"ldc.r8 3000 -> ldc.r8 {F(3000d * ratio)}", $"ldc.r4 8000 -> ldc.r4 {F((float)(8000f * ratio))}",
      $"ldc.r8 600 -> ldc.r8 {F(600d * ratio)}", $"ldc.r4 6000 -> ldc.r4 {F((float)(6000f * ratio))}", $"ldc.r8 5000 -> ldc.r8 {F(5000d * ratio)}");

    // A huge world: bigger points, so the conversions change their 12 m and 6 m too.
    WorldSizeHelper.Layout.Assume(new WorldGeometry(1000000f, 500f));
    var hugeGrid = WorldSizeHelper.LayoutGrid;
    Expect("a huge world: the grid's points grow", "GridToWorldTranspiler", "AltBiomeWorldData.MapSpaceToWorldSpace",
      $"ldc.r4 1024 -> ldc.r4 {F(hugeGrid.Half)}", $"ldc.r4 12 -> ldc.r4 {F(hugeGrid.Pixel)}", $"ldc.r4 6 -> ldc.r4 {F(hugeGrid.HalfPixel)}");
    WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
  }

  // ------------------------------------------------------------------------------------------------ patched for real
  // The pure ones (no engine calls, so .NET can compile them here), patched by a group of those parts with the layout's
  // transpilers, which read the layout's size (Assume stands in for the layout group's own patching).
  private static void LayoutPatchTests()
  {
    var harmony = new Harmony("size-tests.layout");
    var group = new Group(
      new WorldSizeHelper.Part(() => Target(typeof(AltBiomeWorldData), "MapSpaceToWorldSpace", [typeof(float)]), transpiler: "GridToWorldTranspiler"),
      new WorldSizeHelper.Part(() => Target(typeof(AltBiomeWorldData), "WorldSpaceToMapSpace", [typeof(float)]), transpiler: "WorldToGridTranspiler"),
      new WorldSizeHelper.Part(() => Target(typeof(WorldGenerator), "IsAshlands", XY), transpiler: "AshlandsTranspiler"),
      new WorldSizeHelper.Part(() => Target(typeof(WorldGenerator), "IsDeepnorth", XY), transpiler: "IsDeepnorthTranspiler"),
      new WorldSizeHelper.Part(() => Target(typeof(WorldGenerator), "DeepNorthWaveFade", XY), transpiler: "DeepNorthTranspiler"));
    int mark = CapturingLogHandler.Lines.Count;
    try
    {
      Check(AltBiomeWorldData.MapSpaceToWorldSpace(0f) == -12282f && WorldGenerator.IsDeepnorth(0f, 9000f) && WorldGenerator.IsAshlands(0f, -9000f),
        "unpatched: vanilla's grid starts at -12282 m, the Deep North at 8000 m north, the Ashlands at 8000 m south");

      var size = new WorldGeometry(20000f, 500f);
      WorldSizeHelper.Layout.Assume(size);
      Check(group.Update(harmony, size) && group.Patched, "World Size 20000: patched");
      Check(AltBiomeWorldData.MapSpaceToWorldSpace(0f) == -23994f && AltBiomeWorldData.MapSpaceToWorldSpace(3999f) == 23994f,
        $"the grid's 4000 points run from -23994 m to 23994 m ({AltBiomeWorldData.MapSpaceToWorldSpace(0f)} to {AltBiomeWorldData.MapSpaceToWorldSpace(3999f)})");
      int roundTrips = 0;
      for (int i = 0; i < 4000; i++)
        if (AltBiomeWorldData.WorldSpaceToMapSpace(AltBiomeWorldData.MapSpaceToWorldSpace(i)) == i
            && AltBiomeWorldData.WorldSpaceToMapSpace(AltBiomeWorldData.MapSpaceToWorldSpace(i) + 5.9f) == i)
          roundTrips++;
      Check(roundTrips == 4000, $"every grid point's position finds its point again ({roundTrips}/4000)");
      Check(!WorldGenerator.IsDeepnorth(0f, 15000f) && WorldGenerator.IsDeepnorth(0f, 17000f),
        "the Deep North starts 16000 m north (vanilla's 8000 m, x2)");
      Check(!WorldGenerator.IsAshlands(0f, -15000f) && WorldGenerator.IsAshlands(0f, -17000f),
        "the Ashlands start 16000 m south (vanilla's 8000 m, x2)");
      Check(Math.Abs(WorldGenerator.DeepNorthWaveFade(0f, 16100f) - 0.5) < 1e-3,
        $"the Deep North's calm sea fades in from its new ring ({WorldGenerator.DeepNorthWaveFade(0f, 16100f)} at 16100 m)");
      Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("could not")), "no part failed to patch");

      Check(group.Update(harmony, WorldGeometry.Vanilla) && !group.Patched && AltBiomeWorldData.MapSpaceToWorldSpace(0f) == -12282f
            && WorldGenerator.IsDeepnorth(0f, 9000f), "vanilla's size again: unpatched, vanilla's answers");

      // A part another mod has already changed (here: no 1024 to replace) is logged, and the rest are patched.
      var broken = new Group(
        new WorldSizeHelper.Part(() => Target(typeof(WorldGenerator), "IsDeepnorth", XY), transpiler: "GridToWorldTranspiler"),
        new WorldSizeHelper.Part(() => Target(typeof(AltBiomeWorldData), "MapSpaceToWorldSpace", [typeof(float)]), transpiler: "GridToWorldTranspiler"));
      mark = CapturingLogHandler.Lines.Count;
      broken.Update(harmony, size);
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("could not patch WorldGenerator.IsDeepnorth")) && AltBiomeWorldData.MapSpaceToWorldSpace(0f) == -23994f,
        "a part that cannot be patched is logged by name, and the group's other parts are patched");
      broken.Update(harmony, WorldGeometry.Vanilla);
    }
    catch (Exception e)
    {
      Check(false, $"patching the layout's pure parts: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    }
  }

  // ------------------------------------------------------------------------------------------------ the minimap
  private static void MinimapScaleTests()
  {
    WorldSizeHelper.Layout.Assume(new WorldGeometry(20000f, 500f));
    Check(WorldSizeHelper.LayoutMinimapScale == 20500f / 10500f, $"World Size 20000: the minimap's pixels x{WorldSizeHelper.LayoutMinimapScale} (20500 / 10500)");
    WorldSizeHelper.Layout.Assume(new WorldGeometry(5000f, 250f));
    Check(WorldSizeHelper.LayoutMinimapScale == 0.5f, "World Size 5000: x0.5");
    WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    Check(WorldSizeHelper.LayoutMinimapScale == 1f && WorldSizeHelper.MinimapScale == 1f, "vanilla's layout: the game's own pixels, untouched");
    try
    {
      WorldSizeHelper.ScaleMinimap(null);
      Check(WorldSizeHelper.MinimapScale == 1f, "no minimap (a dedicated server, the main menu): nothing to do");
    }
    catch (Exception e)
    {
      Check(false, $"no minimap: {e.GetType().Name}: {e.Message}");
    }
  }

  // ------------------------------------------------------------------------------------------------ Expand World Size's stretch
  private static void StretchNoteTests()
  {
    var method = typeof(WorldSizeHelper).GetMethod("SetStretch", BindingFlags.Public | BindingFlags.Static, [typeof(float), typeof(float)]);
    Check(method != null && typeof(BC).Assembly.GetType("BetterContinents.WorldSizeHelper") == typeof(WorldSizeHelper)
          && typeof(WorldSizeHelper).GetMethods().Count(m => m.Name == "SetStretch") == 1,
      "Expand World Size finds BetterContinents.WorldSizeHelper.SetStretch(float, float) by name, public and static, the only one");
    var settings = BC.Settings;
    int Notes(int from) => CapturingLogHandler.Lines.Skip(from).Count(l => l.StartsWith("[Warning]") && l.Contains("World Stretch is 2"));
    try
    {
      BC.Settings = new BC.BetterContinentsSettings();
      int mark = CapturingLogHandler.Lines.Count;
      WorldSizeHelper.SetStretch(2f, 1f);
      Check(WorldSizeHelper.ExpandWorldSizeStretch == 2f && Notes(mark) == 0, "at the main menu: the stretch is kept, nothing said");
      BC.Settings = World(12, 10000f, 500f);
      BC.UpdateGeometry();
      WorldSizeHelper.SetStretch(2f, 0.5f);
      BC.UpdateGeometry();
      Check(Notes(mark) == 1, "a Better Continents world with World Stretch 2: one warning, however often it is said");
      BC.Settings = new BC.BetterContinentsSettings();
      BC.UpdateGeometry();
      BC.Settings = World(11, 10000f, 500f);
      BC.UpdateGeometry();
      Check(Notes(mark) == 2, "the next world (any version) is warned again");
      mark = CapturingLogHandler.Lines.Count;
      WorldSizeHelper.SetStretch(1f, 2f);
      BC.UpdateGeometry();
      Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("World Stretch")), "World Stretch 1 (a Biome Stretch only): nothing to say");
    }
    finally
    {
      WorldSizeHelper.ExpandWorldSizeStretch = 1f;
      BC.Settings = settings;
      BC.UpdateGeometry();
    }
  }

  // ------------------------------------------------------------------------------------------------ the alt-biome grid
  private static void OwnGridTests()
  {
    var settings = BC.Settings;
    var ews = BC.ExpandWorldSizeGeometry;
    try
    {
      BC.ExpandWorldSizeGeometry = null;
      BC.Settings = World(12, 20000f, 500f);
      BC.UpdateGeometry();
      WorldSizeHelper.Layout.Assume(new WorldGeometry(20000f, 500f));
      BC.AltBiomeControl.Configure();
      Check(BC.AltBiomeControl.OwnGrid && !BC.AltBiomeControl.ExternalGrid && BC.AltBiomeControl.SampledRadius == 20500f
            && BC.AltBiomeControl.CutoffRadius == 0f && !BC.AltBiomeControl.FallbackActive,
        "a world laid out to 20000 m: its own grid, sampled to 20500 m, nothing cut off, no fallback past it");
      BC.Settings.DisableMapEdgeDropoff = true;
      BC.AltBiomeControl.Configure();
      Check(BC.AltBiomeControl.FallbackActive && BC.AltBiomeControl.FallbackRadiusSq == 20500f * 20500f,
        "without the edge drop-off: past 20500 m the land goes on, with its real biome and no alt biomes");

      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
      BC.Settings = World(11, 20000f, 500f);
      BC.UpdateGeometry();
      BC.AltBiomeControl.Configure();
      Check(!BC.AltBiomeControl.OwnGrid && !BC.AltBiomeControl.ExternalGrid && BC.AltBiomeControl.SampledRadius == 10500f && !BC.AltBiomeControl.FallbackActive,
        "a world made before 0.10 with World Size 20000: vanilla's grid to 10500 m, as in 0.9.4");
    }
    finally
    {
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
      BC.ExpandWorldSizeGeometry = ews;
      BC.Settings = settings;
      BC.UpdateGeometry();
      BC.AltBiomeControl.Configure();
    }
  }
}

// Added by Wubarrk on 2026-09-29 for Expand World Data biomes (0.9.3), and modified on 2026-10-04 for the unifying refactor (0.10.0).

// World generation with Expand World Data's biomes (called at the end of Program.EwdChecks, so its patches are in). The game's
// WorldGenerator.GetBiome and GetBiomeHeight run for real, under Expand World Data's own Harmony patches of them (its
// GetBiomeWG.Prefix and BiomeHeight, registered under its own Harmony id, "expand_world_data", before anything of Better
// Continents) and Better Continents' patches applied by Better Continents itself the way it applies them when a world loads
// (BetterContinents.DynamicPatch over a settings object holding the maps). What does not run offline is replaced: the game's
// terrain formulas (they call DUtils.PerlinNoise, which is Mathf.PerlinNoise) by smooth functions, one per terrain the checks
// use, and the two DLLs are loaded from memory with a few call sites changed (LoadBetterContinents, LoadExpandWorldData).
// Everything the checks call is in a NoInlining method of its own, first called after the patches are in (see Program's header).
//
// One check pins a known gap on purpose (its name starts with KNOWN GAP): see the comment above it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;
using Color = UnityEngine.Color;

namespace EwdTest;

internal static class Generation
{
  const Heightmap.Biome DeadWastes = (Heightmap.Biome)0x400;
  const Heightmap.Biome Meadows = Heightmap.Biome.Meadows;
  const Heightmap.Biome Mountain = Heightmap.Biome.Mountain;
  const Heightmap.Biome Mistlands = Heightmap.Biome.Mistlands;

  static void C(bool ok, string what) => Program.C(ok, what);
  static void Section(string s) => Program.Section(s);
  static void Info(string s) => System.Console.WriteLine("  INFO " + s);

  static Assembly ewd = null!;
  static string work = "";
  // Expand World Data's own Harmony id: Better Continents orders its patches after it by this name (EWD.GUID).
  static readonly Harmony EwdHarmony = new("expand_world_data");
  static readonly MethodInfo GetBiomeMethod = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), [typeof(float), typeof(float), typeof(float), typeof(bool)]);
  static readonly MethodInfo GetBiomeHeightMethod = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight));

  [MethodImpl(MethodImplOptions.NoInlining)]
  public static void Run(string workDir, Assembly ewdAssembly)
  {
    ewd = ewdAssembly;
    work = workDir;
    Info($"Better Continents loaded with {rewrittenCalls} Harmony.Patch calls pointed at Lib.Harmony's overload, Expand World Data with {rewrittenNoise} Mathf.PerlinNoise calls pointed at a plain noise function");
    Stubs();
    Section("what the game's own GetBiomeHeight gives a biome only Expand World Data knows");
    VanillaGround();
    ExpandWorldDataGround();
    Section("Better Continents' biome map decides WorldGenerator.GetBiome");
    Biomes();
    Section("biome precision over a DeadWastes / Meadows zone");
    Precision();
    Section("the world export over this world");
    Export();
    Section("terrain height of an added biome with Better Continents' height patches");
    Heights();
  }

  // ---- the two DLLs, loaded from memory with a few call sites changed --------------------------------------------------
  // Better Continents' DLL is built against BepInEx's HarmonyX, whose Harmony.Patch has a sixth parameter (an IL manipulator)
  // that the Lib.Harmony this suite runs on lacks: every "HarmonyInstance.Patch(method, prefix: ...)" of Patcher.cs would
  // throw MissingMethodException offline. Expand World Data's calls Mathf.PerlinNoise, native in Unity, in BiomeHeight's
  // postfix (lava) and in its biome rules (BiomeCalculator): a method holding that call cannot even be compiled here (ECall),
  // whether or not it runs it. So each DLL is read into memory first with those calls pointed at their offline equivalents
  // (the five-parameter Patch, the sixth argument being always the default null; a plain noise function), and loaded before
  // anything else uses it. Nothing else of either changes: the code that runs is the real one.
  static int rewrittenCalls, rewrittenNoise;

  // Cecil resolves the types of enum constants when it writes: the game's, Better Continents' and Expand World Data's from
  // the folders the suite loads them from, the framework's from this runtime's core library.
  sealed class Resolver(string[] dirs) : IAssemblyResolver
  {
    readonly Dictionary<string, AssemblyDefinition> cache = [];
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
      if (cache.TryGetValue(name.Name, out var found))
        return found;
      var file = dirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists) ?? typeof(object).Assembly.Location;
      return cache[name.Name] = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = this });
    }
    public void Dispose() { }
  }

  static Assembly LoadRewritten(string path, string[] dirs, Action<ModuleDefinition, MethodDefinition, Instruction> rewrite, Action<ModuleDefinition>? finish = null)
  {
    var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = new Resolver(dirs) });
    foreach (var type in module.GetTypes())
      foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var il in method.Body.Instructions.ToList())
          rewrite(module, method, il);
    finish?.Invoke(module);
    // From a file: an assembly loaded from a stream is not the one the runtime finds when code asks for it by name.
    var folder = Directory.CreateTempSubdirectory("bc-ewd-rewritten-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { folder.Delete(true); } catch { } };
    var file = Path.Combine(folder.FullName, Path.GetFileName(path));
    module.Write(file);
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
  }

  public static void LoadBetterContinents() =>
    LoadRewritten(typeof(Generation).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "BcDll").Value!, [AppContext.BaseDirectory], (module, method, il) =>
    {
      if (il.Operand is not MethodReference call || call.Name != "Patch" || call.DeclaringType.FullName != "HarmonyLib.Harmony" || call.Parameters.Count != 6)
        return;
      if (il.Previous.OpCode != OpCodes.Ldnull)
        throw new InvalidOperationException($"{method.FullName} passes a sixth argument to Harmony.Patch");
      il.Previous.OpCode = OpCodes.Nop;
      il.Previous.Operand = null;
      var five = new MethodReference("Patch", call.ReturnType, call.DeclaringType) { HasThis = true };
      foreach (var parameter in call.Parameters.Take(5))
        five.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
      il.Operand = five;
      rewrittenCalls++;
    });

  public static Assembly LoadExpandWorldData(string path)
  {
    var noise = typeof(PerlinStub).GetMethod(nameof(PerlinStub.Noise))!;
    string[] dirs = [AppContext.BaseDirectory, "/home/rohan/WubarrkCODING/libs-Tools/1.0/client", "/home/rohan/WubarrkCODING/libs-Tools", Path.GetDirectoryName(path)!];
    return LoadRewritten(path, dirs, (module, method, il) =>
    {
      if (il.Operand is not MethodReference call || call.Name != "PerlinNoise" || call.DeclaringType.FullName != "UnityEngine.Mathf")
        return;
      il.Operand = module.ImportReference(noise);
      rewrittenNoise++;
    }, module =>
    {
      // Expand World Data is built against the game's assemblies with their private members made public; Mono, the game's
      // runtime, does not check access, .NET does unless the assembly says so (Better Continents' DLL has the same attribute).
      var attribute = new TypeReference("System", "Attribute", module, module.TypeSystem.CoreLibrary);
      var type = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit, attribute);
      var ctor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
      ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
      var il = ctor.Body.GetILProcessor();
      il.Emit(OpCodes.Ldarg_0);
      il.Emit(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true });
      il.Emit(OpCodes.Ret);
      type.Methods.Add(ctor);
      module.Types.Add(type);
      foreach (var game in new[] { "assembly_valheim", "assembly_utils" })
        module.Assembly.CustomAttributes.Add(new CustomAttribute(ctor) { ConstructorArguments = { new CustomAttributeArgument(module.TypeSystem.String, game) } });
    });
  }

  // ---- the rig -------------------------------------------------------------------------------------------------------

  // The game's terrain formulas call DUtils.PerlinNoise, which is Mathf.PerlinNoise: native in Unity, and a method that
  // calls it cannot even be compiled here (ECall), so the formulas are replaced by smooth functions, one per terrain the
  // checks use, and GetBiomeHeight itself - its multiplier, gaps, world edge and biome switch with its default - runs for
  // real around them.
  static float Base(float wx, float wy) => 0.30f + 0.15f * (float)(Math.Sin(wx * 0.0011) * Math.Cos(wy * 0.0007));
  // Better Continents' GetBaseHeight prefix (its heightmap) is registered later and runs first: this stands in for the
  // game's own base height only where nothing else has answered.
  [HarmonyPriority(Priority.Last)]
  static bool BaseStub(float wx, float wy, ref float __result, bool __runOriginal) { if (__runOriginal) __result = Base(wx, wy); return false; }
  static bool MeadowsStub(float wx, float wy, ref float __result) { __result = Base(wx, wy) + 0.03f * (float)Math.Sin(wx * 0.05); return false; }
  static bool MountainStub(float wx, float wy, ref float __result) { __result = 2f * Base(wx, wy) - 0.2f + 0.06f * (float)Math.Cos(wy * 0.04); return false; }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Stubs()
  {
    // The JIT would inline DUtils.PerlinNoise into every formula it compiles from here on - and cannot compile the ECall it
    // holds. MonoMod (inside Harmony) marks the methods it patches as not inlinable the same way.
    var triple = typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple")!;
    var current = triple.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var disable = triple.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(MethodBase)])!;
    foreach (var noise in typeof(DUtils).GetMethods().Where(m => m.Name == nameof(DUtils.PerlinNoise)))
      disable.Invoke(current, [noise]);
    var stubs = new Harmony("ewd-generation-stubs");
    HarmonyMethod Stub(string name) => new(typeof(Generation), name);
    stubs.Patch(AccessTools.Method(typeof(WorldGenerator), "GetBaseHeight"), prefix: Stub(nameof(BaseStub)));
    stubs.Patch(AccessTools.Method(typeof(WorldGenerator), "GetMeadowsHeight"), prefix: Stub(nameof(MeadowsStub)));
    stubs.Patch(AccessTools.Method(typeof(WorldGenerator), "GetSnowMountainHeight"), prefix: Stub(nameof(MountainStub)));
    // The last GetBiome prefix, for Expand World Data's answer (see EwdBiomePrefix).
    stubs.Patch(GetBiomeMethod, prefix: new HarmonyMethod(typeof(Generation), nameof(EwdBiomeFinish)) { priority = Priority.Last });
  }

  static WorldGenerator NewGenerator()
  {
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    wg.m_world = new World { m_name = "EWD generation", m_seedName = "EwdGeneration", m_seed = 7 };
    AccessTools.Field(typeof(WorldGenerator), "m_minMountainDistance").SetValue(wg, 1000f);
    return wg;
  }

  // What a loaded world's settings hold: the maps (their fields are private to Better Continents).
  static BC.BetterContinentsSettings MakeSettings(bool enabled = true, object? biomeMap = null, object? heightMap = null, bool overrideAll = true, object? roughMap = null, object? paintMap = null)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = enabled, HeightmapOverrideAll = overrideAll };
    void Set(string field, object? value) => typeof(BC.BetterContinentsSettings).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, value);
    Set("BiomeMap", biomeMap);
    Set("HeightMap", heightMap);
    Set("RoughMap", roughMap);
    Set("PaintMap", paintMap);
    return s;
  }

  // The world loads: its settings are current and Better Continents patches the game to them.
  static void Load(BC.BetterContinentsSettings settings)
  {
    BC.HarmonyInstance ??= new Harmony("BetterContinents.Harmony");
    BC.Settings = settings;
    BC.DynamicPatch();
  }

  // The biome map: west to east Meadows, DeadWastes, None (the game decides), Mistlands; its band centres at x = -7875,
  // -2625, 2625 and 7875 (z = 0), and the Meadows / DeadWastes border at x = -5333.
  static ImageMapBiome MakeBiomeMap() => ImageMapBiome.Create(Program.WriteMap(work, "generation", [
    ("Meadows", new Rgba32(0x00, 0xFF, 0x00)), ("DeadWastes", new Rgba32(0x8B, 0x45, 0x13)),
    ("None", new Rgba32(0x00, 0x00, 0x00)), ("Mistlands", new Rgba32(0x7F, 0x7F, 0x7F))]))!;
  static float BandX(int band) => (-0.375f + 0.25f * band) * BC.TotalSize;

  static object Manager(string field) => AccessTools.Field(ewd.GetType("ExpandWorldData.BiomeManager"), field).GetValue(null)!;

  // ---- ground: the game's own switch ---------------------------------------------------------------------------------

  // Before any Expand World Data patch of GetBiomeHeight: the game's switch has no case for 0x400.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void VanillaGround()
  {
    var wg = NewGenerator();
    var flat = wg.GetBiomeHeight(DeadWastes, -3000f, 1000f, out var mask);
    var meadows = wg.GetBiomeHeight(Meadows, -3000f, 1000f, out _);
    Info($"the game's GetBiomeHeight at (-3000, 1000): 0x400 -> {flat} m, Meadows -> {meadows} m");
    C(flat == 0f && mask == Color.black, "vanilla GetBiomeHeight(0x400) is the switch default: 0 m and black paint (flat ground 30 m below the water level)");
    C(meadows > 20f, "and the same call for Meadows gives real ground");
  }

  static void EwdPatchHeight() => EwdHarmony.CreateClassProcessor(ewd.GetType("ExpandWorldData.BiomeHeight")!).Patch();

  // Expand World Data's BiomeHeight patch alone, the terrain of DeadWastes taken from its yaml's `terrain`.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void ExpandWorldDataGround()
  {
    EwdPatchHeight();
    var wg = NewGenerator();
    var none = wg.GetBiomeHeight(DeadWastes, -3000f, 1000f, out _);
    C(none == 0f, "Expand World Data's patch with no `terrain` for the biome (the yaml's default): still the switch default, 0 m");
    ((Dictionary<Heightmap.Biome, Heightmap.Biome>)Manager("BiomeToTerrain"))[DeadWastes] = Mountain;
    var mapped = wg.GetBiomeHeight(DeadWastes, -3000f, 1000f, out _);
    var mountain = wg.GetBiomeHeight(Mountain, -3000f, 1000f, out _);
    var meadows = wg.GetBiomeHeight(Meadows, -3000f, 1000f, out _);
    Info($"Expand World Data alone at (-3000, 1000): DeadWastes with terrain Mountain -> {mapped} m, Mountain -> {mountain} m, Meadows -> {meadows} m");
    C(mapped == mountain && mapped != meadows && mapped != 0f, "with `terrain: Mountain` DeadWastes has Mountain's ground (not Meadows', not 0 m)");
  }

  // ---- GetBiome ------------------------------------------------------------------------------------------------------

  // Expand World Data's own GetBiome prefix (GetBiomeWG.Prefix: its world yaml decides, it skips the original), called through
  // a wrapper. The game's Harmony (BepInEx's HarmonyX 2.9, libs-Tools/0Harmony.dll and the BepInEx pack's alike) runs every prefix of
  // a method: each one that returns false only clears "run the original" (HarmonyManipulator.WritePrefixes: runOriginal &= result),
  // and whatever a later prefix writes to __result replaces what an earlier one wrote. Lib.Harmony, which this suite runs on, stops at the first prefix that returns
  // false. So the wrapper never stops the prefixes after it and hands its verdict to a last prefix that, if nothing before
  // it answered, gives Expand World Data's answer and skips the original: the same net result as the game's.
  delegate bool EwdPrefix(WorldGenerator instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean, ref Heightmap.Biome result);
  static EwdPrefix? realEwdPrefix;
  [ThreadStatic] static bool ewdSkipsOriginal;
  [ThreadStatic] static Heightmap.Biome ewdAnswer;

  static bool EwdBiomePrefix(WorldGenerator __instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean, ref Heightmap.Biome __result)
  {
    var answer = __result;
    ewdSkipsOriginal = !realEwdPrefix!(__instance, wx, wy, oceanLevel, waterAlwaysOcean, ref answer);
    ewdAnswer = answer;
    __result = answer;
    return true;
  }

  static bool EwdBiomeFinish(ref Heightmap.Biome __result)
  {
    if (!ewdSkipsOriginal)
      return true;
    __result = ewdAnswer;
    return false;
  }

  static void EwdPatchBiome()
  {
    realEwdPrefix ??= (EwdPrefix)Delegate.CreateDelegate(typeof(EwdPrefix), AccessTools.Method(ewd.GetType("ExpandWorldData.GetBiomeWG"), "Prefix"));
    EwdHarmony.Patch(GetBiomeMethod, prefix: new HarmonyMethod(typeof(Generation), nameof(EwdBiomePrefix)));
  }

  static void EwdUnpatchBiome()
  {
    EwdHarmony.Unpatch(GetBiomeMethod, HarmonyPatchType.Prefix, "expand_world_data");
    ewdSkipsOriginal = false;
  }

  // What its world data says (expand_world.yaml): Mountain everywhere, whatever the map below says.
  static void EwdWorldData()
  {
    var file = new ConfigFile(Path.Combine(work, "ewd-generation.cfg"), false);
    var configuration = ewd.GetType("ExpandWorldData.Configuration")!;
    AccessTools.Field(configuration, "configDataWorld").SetValue(null, file.Bind("1. General", "World data", true));
    AccessTools.Field(configuration, "configLegacyGeneration").SetValue(null, file.Bind("1. General", "Legacy generation", false));
    var entryType = ewd.GetType("ExpandWorldData.WorldEntry")!;
    // One entry for the whole world (a yaml builds it through a constructor that needs the whole plugin): no altitude, distance
    // or sector limits, no wiggle.
    var entry = RuntimeHelpers.GetUninitializedObject(entryType);
    foreach (var (field, value) in new (string, object)[] { ("biome", Mountain), ("maxDistance", 20000f), ("maxAltitude", 10000f), ("minAltitude", -1000f), ("maxSector", 1f), ("amount", 1f), ("stretch", 1f) })
      entryType.GetField(field)!.SetValue(entry, value);
    var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
    list.Add(entry);
    AccessTools.Field(ewd.GetType("ExpandWorldData.BiomeCalculator"), "BiomeData").SetValue(null, list);
  }

  // The patches of a method in the order Harmony runs them (its PatchSorter, the same algorithm in HarmonyX).
  static List<MethodInfo> RunOrder(MethodBase method, IEnumerable<Patch> patches)
  {
    var sorter = AccessTools.Method(typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions"), "GetSortedPatchMethods");
    return (List<MethodInfo>)sorter.Invoke(null, [method, patches.ToArray(), false])!;
  }
  static string OrderText(IEnumerable<MethodInfo> methods) =>
    string.Join(" > ", methods.Select(m => m.DeclaringType!.FullName!.Replace("BetterContinents.BetterContinents+", "BC.").Replace("ExpandWorldData.", "EWD.") + "." + m.Name));
  static List<MethodInfo> PrefixOrder() => RunOrder(GetBiomeMethod, Harmony.GetPatchInfo(GetBiomeMethod).Prefixes);
  static string PrefixOrderText() => OrderText(PrefixOrder());
  // Both Harmonys write every postfix that returns void first and the pass-through ones (they return a value) after them, each
  // group in the sorted order: a pass-through postfix cannot be ordered before a void one.
  static string PostfixOrderText()
  {
    var sorted = RunOrder(GetBiomeHeightMethod, Harmony.GetPatchInfo(GetBiomeHeightMethod).Postfixes);
    return OrderText(sorted.Where(m => m.ReturnType == typeof(void)).Concat(sorted.Where(m => m.ReturnType != typeof(void))));
  }
  static bool BetterContinentsRunsAfterExpandWorldData()
  {
    var order = PrefixOrder();
    int ewdAt = order.FindIndex(m => m.Name == nameof(EwdBiomePrefix)), bcAt = order.FindIndex(m => m.Name == "GetBiomePrefix");
    return ewdAt >= 0 && bcAt > ewdAt;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Biomes()
  {
    var map = MakeBiomeMap();
    C(map.GetValue(0.375f, 0.5f) == DeadWastes, "the test map's second band is DeadWastes");
    var wg = NewGenerator();
    LogHandler.Clear();
    Load(MakeSettings(biomeMap: map));
    C(LogHandler.Lines.Any(l => l.EndsWith("] Patching WorldGenerator.GetBiome")), "Better Continents patches WorldGenerator.GetBiome for a world with a biome map");
    BiomeChecks(wg, "Better Continents' patch alone");

    // The game's order: Expand World Data patches in Awake, Better Continents when the world loads.
    Load(MakeSettings());
    EwdWorldData();
    EwdPatchBiome();
    C(wg.GetBiome(BandX(1), 0f) == Mountain, "Expand World Data's real GetBiome prefix, with no biome map, answers from its world data (Mountain everywhere)");
    Load(MakeSettings(biomeMap: map));
    Info("GetBiome prefixes in run order, Better Continents registered after Expand World Data: " + PrefixOrderText());
    C(BetterContinentsRunsAfterExpandWorldData(), "Better Continents' GetBiome prefix runs after Expand World Data's (the last to set the result wins in the game's Harmony)");
    BiomeChecks(wg, "beside Expand World Data's GetBiome prefix");
    C(wg.GetBiome(BandX(2), 0f) == Mountain, "a None pixel of the map leaves Expand World Data's answer (the game decides there)");

    // Better Continents registered first, Expand World Data second.
    Load(MakeSettings());
    EwdUnpatchBiome();
    Load(MakeSettings(biomeMap: map));
    EwdPatchBiome();
    Info("GetBiome prefixes in run order, Better Continents registered before Expand World Data: " + PrefixOrderText());
    C(BetterContinentsRunsAfterExpandWorldData(), "Better Continents' GetBiome prefix still runs after Expand World Data's when it registered first (`after: expand_world_data`)");

    // The same registration written by hand as Patcher.cs's edit has it: the ordering Harmony gives `after: expand_world_data`.
    Load(MakeSettings());
    EwdUnpatchBiome();
    var bc = BC.HarmonyInstance;
    var prefix = new HarmonyMethod(AccessTools.Method(typeof(BC.WorldGeneratorPatch), nameof(BC.WorldGeneratorPatch.GetBiomePrefix))) { after = ["expand_world_data"] };
    bc.Patch(GetBiomeMethod, prefix: prefix);
    EwdPatchBiome();
    C(BetterContinentsRunsAfterExpandWorldData(), "a prefix registered before Expand World Data's with `after: expand_world_data` runs after it (Harmony's ordering by owner id)");
    bc.Unpatch(GetBiomeMethod, prefix.method);

    // Back to the game's order for the checks that follow.
    EwdUnpatchBiome();
    EwdPatchBiome();
    Load(MakeSettings(biomeMap: map));
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void BiomeChecks(WorldGenerator wg, string when)
  {
    var dead = wg.GetBiome(BandX(1), 0f);
    var meadows = wg.GetBiome(BandX(0), 0f);
    var mist = wg.GetBiome(BandX(3), 0f);
    C(dead == DeadWastes, $"{when}: GetBiome inside the DeadWastes region is DeadWastes, 0x400 (got 0x{(uint)dead:X})");
    C(meadows == Meadows && mist == Mistlands, $"{when}: and the Meadows and Mistlands regions are Meadows and Mistlands (got {meadows}, {mist})");
  }

  // ---- biome precision ---------------------------------------------------------------------------------------------

  // A 64 m zone whose west 30 m is the Meadows region and the rest DeadWastes, sampled on 5 x 5 points 16 m apart.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Precision()
  {
    const int cells = 4, size = cells + 1;
    const float x0 = -5357f;
    var wg = NewGenerator();
    Load(MakeSettings(biomeMap: MakeBiomeMap()));
    var grid = BC.BiomePrecisionGrid.Sample(wg, x0, 0f, 64.0, cells);
    bool columns = true, plain = true;
    for (int row = 0; row < size; row++)
      for (int col = 0; col < size; col++)
      {
        var want = col < 2 ? Meadows : DeadWastes;
        var sector = grid.Sectors[row * size + col];
        columns &= sector.Biome == want;
        plain &= ReferenceEquals(sector, BC.AltBiomeControl.PlainSector(want));
      }
    C(columns, "BiomePrecisionGrid.Sample over a 64 m zone across the Meadows / DeadWastes border: Meadows sectors in the two west columns, DeadWastes in the other three");
    C(plain, "the DeadWastes samples are the plain DeadWastes sector where the world has no sector data (the same object as AltBiomeControl.PlainSector)");
    C((grid.Biomes & DeadWastes) != 0 && !grid.Uniform, "the grid knows DeadWastes is in the zone (HaveBiome) and that it is not one biome");

    // A world with sector data (alt biomes): its DeadWastes sector is kept where the map says DeadWastes, and not where it says Meadows.
    var data = new AltBiomeWorldData(2048) { PointsGenerated = true, SectorsCalculated = true };
    var world = new BiomeSector(null, DeadWastes);
    world.AltBiomes.Add(new AltBiome { m_name = "Bone Fields", m_biome = DeadWastes, m_enabled = true });
    for (int y = 0; y < 2048; y++)
      for (int x = 0; x < 2048; x++)
        data.PointSectors[x, y] = world;
    wg.m_world.m_biomeData = data;
    var kept = BC.BiomePrecisionGrid.Sample(wg, x0, 0f, 64.0, cells);
    bool keptWorld = true, plainWest = true;
    for (int row = 0; row < size; row++)
      for (int col = 0; col < size; col++)
      {
        var sector = kept.Sectors[row * size + col];
        if (col < 2)
          plainWest &= ReferenceEquals(sector, BC.AltBiomeControl.PlainSector(Meadows));
        else
          keptWorld &= ReferenceEquals(sector, world) && sector.AltBiomes.Count == 1;
      }
    C(keptWorld && plainWest, "with world sector data the DeadWastes samples are the world's own DeadWastes sector (its alt biome kept), and the Meadows samples are plain Meadows where the world's sector disagrees");
    wg.m_world.m_biomeData = null;
  }

  // ---- terrain height ----------------------------------------------------------------------------------------------

  static object MakeFloatMap(Func<int, int, double> value)
  {
    using var image = new Image<L16>(64, 64);
    for (int y = 0; y < 64; y++)
      for (int x = 0; x < 64; x++)
        image[x, y] = new L16((ushort)(Math.Clamp(value(x, y), 0.0, 1.0) * 65535.0));
    using var png = new MemoryStream();
    image.SaveAsPng(png);
    return ImageMapFloat.Create(png.ToArray(), false)!;
  }

  static object MakePaintMap()
  {
    using var image = new Image<Rgba32>(64, 64, new Rgba32(200, 100, 50, 255));
    using var png = new MemoryStream();
    image.SaveAsPng(png);
    return ImageMapPaint.Create(png.ToArray(), "")!;
  }

  static readonly (float X, float Z)[] Samples = [(-4700f, 1500f), (-3300f, -2000f), (-2100f, 3400f), (-900f, -700f), (-4200f, 4200f)];

  // The names of Better Continents' postfixes on GetBiomeHeight, as the log says it patched them.
  static string BcHeightPatches() =>
    string.Join(" + ", Harmony.GetPatchInfo(GetBiomeHeightMethod).Postfixes.Where(p => p.owner == BC.HarmonyInstance.Id)
      .Select(p => p.PatchMethod.Name.Replace("GetBiomeHeightWith", "")).OrderBy(n => n));

  // Expand World Data's BiomeHeight (real, patched first as in the game: it registers in Awake) against Better Continents' own
  // GetBiomeHeight patches (registered by BetterContinents.DynamicPatch, later), in every combination of maps that changes which of
  // them Patcher.PatchGetBiomeHeight applies. DeadWastes has `terrain: Mountain`.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Heights()
  {
    var biomes = MakeBiomeMap();
    var height = MakeFloatMap((x, y) => 0.40 + 0.20 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17));
    var rough = MakeFloatMap((x, y) => 0.5);
    var paint = MakePaintMap();
    (string Name, BC.BetterContinentsSettings Settings, string Patches)[] configs =
    [
      ("Better Continents off (Expand World Data alone)", MakeSettings(enabled: false), ""),
      ("a biome map only", MakeSettings(biomeMap: biomes), ""),
      ("a heightmap with Override All", MakeSettings(biomeMap: biomes, heightMap: height), "Height"),
      ("a heightmap with Override All and a paint map", MakeSettings(biomeMap: biomes, heightMap: height, paintMap: paint), "HeightPaint + Paint"),
      ("a heightmap without Override All and a rough map", MakeSettings(biomeMap: biomes, heightMap: height, overrideAll: false, roughMap: rough), "Rough"),
      ("a heightmap without Override All, a rough map and a paint map", MakeSettings(biomeMap: biomes, heightMap: height, overrideAll: false, roughMap: rough, paintMap: paint), "Paint + RoughPaint"),
      ("a paint map only", MakeSettings(biomeMap: biomes, paintMap: paint), "Paint"),
    ];
    var overrideAll = new List<(string Name, float Delta)>();
    foreach (var (name, settings, patches) in configs)
    {
      LogHandler.Clear();
      Load(settings);
      C(BcHeightPatches() == patches, $"{name}: Better Continents patches GetBiomeHeight with [{patches}] (got [{BcHeightPatches()}])");
      var delta = HeightChecks(name, NewGenerator(), patches, settings.HasBiomeMap);
      if (patches.StartsWith("Height"))
      {
        overrideAll.Add((name, delta));
        Info($"{name}: the postfixes on GetBiomeHeight run in this order: {PostfixOrderText()}");
      }
    }

    // PINS A KNOWN GAP, kept on purpose in 0.9.3. Expand World Data's postfix (BiomeHeight.cs:63-112: altitude modifiers, lava, paint) is a
    // void postfix and Better Continents' Height postfixes (WorldGeneratorPatch.cs:94-102, registered by Patcher.cs:339-340 and :354-355) are
    // pass-through postfixes (they return a float): Harmony writes all void postfixes first and pass-through ones after them, and these
    // ignore their `result` argument and return GetBaseHeight(...) * 200, so what Expand World Data wrote to __result is thrown away.
    // Letting it through (the remedy below) would move the ground of every existing Override All world with Expand World Data altitude
    // settings in the zones not generated yet, so it waits for a decision. This check fails the day that changes: update it then.
    C(overrideAll.Count == 2 && overrideAll.All(d => Math.Abs(d.Delta) < 0.01f),
      $"KNOWN GAP (kept for existing worlds): on a heightmap Override All world Expand World Data's altitudeDelta does not move the ground (got {string.Join(", ", overrideAll.Select(d => $"{d.Delta:0.###} m"))}: Better Continents' pass-through Height postfix runs after Expand World Data's void one and replaces its result)");

    // The remedy, registered by hand: the same ground from a void postfix (ref __result) ordered `before: expand_world_data`. Ordering
    // alone does not do it: the pass-through postfix above cannot be ordered before a void one.
    Load(MakeSettings(biomeMap: biomes, heightMap: height));
    var bc = BC.HarmonyInstance;
    var heightPostfix = AccessTools.Method(typeof(BC.WorldGeneratorPatch), nameof(BC.WorldGeneratorPatch.GetBiomeHeightWithHeight));
    bc.Unpatch(GetBiomeHeightMethod, heightPostfix);
    bc.Patch(GetBiomeHeightMethod, postfix: new HarmonyMethod(heightPostfix) { before = ["expand_world_data"] });
    Info("the postfixes on GetBiomeHeight in run order, Height (pass-through) registered with `before: expand_world_data`: " + PostfixOrderText());
    C(RemedyDelta(NewGenerator()) == 0f, "ordering Better Continents' pass-through Height postfix `before: expand_world_data` changes nothing: Harmony still runs the void postfix first");
    bc.Unpatch(GetBiomeHeightMethod, heightPostfix);
    var voidPostfix = new HarmonyMethod(typeof(Generation), nameof(HeightVoidPostfix)) { before = ["expand_world_data"] };
    bc.Patch(GetBiomeHeightMethod, postfix: voidPostfix);
    Info("the postfixes on GetBiomeHeight in run order, Height as a void postfix `before: expand_world_data`: " + PostfixOrderText());
    C(Math.Abs(RemedyDelta(NewGenerator()) + 10f) < 0.01f, "the same ground written by a void postfix ordered before Expand World Data's: its altitudeDelta -10 lowers the heightmap's ground by 10 m");
    bc.Unpatch(GetBiomeHeightMethod, voidPostfix.method);
  }

  // Better Continents' own Height postfix body, as a void postfix.
  static void HeightVoidPostfix(WorldGenerator __instance, float wx, float wy, ref float __result) =>
    __result = BC.WorldGeneratorPatch.GetBiomeHeightWithHeight(__result, __instance, wx, wy);

  // How much Expand World Data's altitudeDelta -10 for DeadWastes changes the ground now.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static float RemedyDelta(WorldGenerator wg)
  {
    var data = (System.Collections.IDictionary)Manager("BiomeData");
    var plain = DeadHeights(wg);
    data[DeadWastes] = AltitudeData(-10f);
    var lowered = DeadHeights(wg);
    data.Remove(DeadWastes);
    return lowered.Select((h, i) => h - plain[i]).Average();
  }

  // What Expand World Data's yaml can say about a biome's ground besides `terrain`: an altitude delta (BiomeData, applied by its
  // postfix). Built without the constructor, which reads a whole yaml entry.
  static object AltitudeData(float delta)
  {
    var type = ewd.GetType("ExpandWorldData.BiomeData")!;
    var data = RuntimeHelpers.GetUninitializedObject(type);
    foreach (var (field, value) in new (string, object)[] { ("altitudeMultiplier", 1f), ("waterDepthMultiplier", 1f), ("altitudeDelta", delta), ("excessFactor", 0.5f), ("excessSign", 1f),
      ("minimumAltitude", -1000f), ("maximumAltitude", 10000f), ("lavaAmount", 1f), ("mapColorMultiplier", 1f), ("forestMultiplier", 1f) })
      type.GetField(field)!.SetValue(data, value);
    return data;
  }

  static float[] DeadHeights(WorldGenerator wg)
  {
    var heights = new float[Samples.Length];
    for (int i = 0; i < heights.Length; i++)
      heights[i] = wg.GetBiomeHeight(DeadWastes, Samples[i].X, Samples[i].Z, out _);
    return heights;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static float HeightChecks(string name, WorldGenerator wg, string patches, bool biomeMap)
  {
    bool replaces = patches.StartsWith("Height"), blends = patches.Contains("Rough");
    int same = 0, sameMask = 0, notMeadows = 0, notFlat = 0, likeMeadows = 0, viaGetHeight = 0;
    foreach (var (x, z) in Samples)
    {
      var dead = wg.GetBiomeHeight(DeadWastes, x, z, out var deadMask);
      var mountain = wg.GetBiomeHeight(Mountain, x, z, out var mountainMask);
      var meadows = wg.GetBiomeHeight(Meadows, x, z, out _);
      if (dead == mountain) same++;
      if (deadMask == mountainMask) sameMask++;
      if (Math.Abs(dead - meadows) > 1f) notMeadows++;
      if (dead == meadows) likeMeadows++;
      if (Math.Abs(dead) > 1f) notFlat++;
      // The game's own way to a height: GetHeight asks GetBiome (the biome map's DeadWastes) and then GetBiomeHeight.
      if (wg.GetHeight(x, z) == mountain) viaGetHeight++;
    }
    C(same == Samples.Length && sameMask == Samples.Length, $"{name}: GetBiomeHeight(DeadWastes) is GetBiomeHeight(Mountain), its `terrain`, at all {Samples.Length} points (height and paint mask)");
    if (biomeMap)
      C(viaGetHeight == Samples.Length, $"{name}: and WorldGenerator.GetHeight, which asks GetBiome first (DeadWastes from the biome map), gives that same ground");
    if (replaces)
      C(likeMeadows == Samples.Length && notFlat == Samples.Length, $"{name}: Better Continents replaces the ground for every biome alike (DeadWastes = Meadows = Mountain = the heightmap), so `terrain` has nothing to decide");
    else
      C(notMeadows == Samples.Length && notFlat == Samples.Length, $"{name}: and that ground is Mountain's, not Meadows' and not the switch default 0 m");

    // The yaml's other ground settings, and a biome whose yaml has no `terrain` (the biome is its own terrain: the switch default).
    var terrain = (Dictionary<Heightmap.Biome, Heightmap.Biome>)Manager("BiomeToTerrain");
    var data = (System.Collections.IDictionary)Manager("BiomeData");
    var mapped = DeadHeights(wg);
    data[DeadWastes] = AltitudeData(-10f);
    var lowered = DeadHeights(wg);
    data.Remove(DeadWastes);
    terrain.Remove(DeadWastes);
    var unmapped = DeadHeights(wg);
    terrain[DeadWastes] = Mountain;
    float Delta(float[] a) => a.Select((h, i) => h - mapped[i]).Average();
    Info($"{name}: altitudeDelta -10 for DeadWastes changes the ground by {Delta(lowered):0.###} m; with no `terrain` it is {unmapped[0]:0.###} m at {Samples[0]} (mapped {mapped[0]:0.###} m)");
    if (patches is "" or "Paint")
      C(lowered.Select((h, i) => Math.Abs(h - (mapped[i] - 10f)) < 0.01f).All(ok => ok), $"{name}: Expand World Data's altitudeDelta -10 for DeadWastes still lowers the ground by 10 m (Better Continents does not touch the height here)");
    if (!blends && !replaces)
      C(unmapped.All(h => h == 0f), $"{name}: a biome with no `terrain` in the yaml is flat 0 m ground (Expand World Data's own default: the biome is its own terrain, and the game's switch has no case for it)");
    return Delta(lowered);
  }

  // ---- world export ------------------------------------------------------------------------------------------------

  static bool CheckWorldPrefix() => false;

  static object MakeJob(WorldExport.Options o, string dir, WorldGenerator wg)
  {
    // What Job's constructor sets, minus its ZNet / ZoneSystem reads (Unity objects cannot exist offline); as the export tests do.
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var job = RuntimeHelpers.GetUninitializedObject(jobType);
    void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
    var probe = new BC.BetterContinentsSettings { ForestScale = 1f };
    probe.ForestScaleFactor = 0.5f;
    Set("O", o);
    Set("Dir", dir);
    Set("Wg", wg);
    Set("World", wg.m_world);
    Set("Settings", BC.Settings);
    Set("BcWorld", true);
    Set("Size", o.Size);
    Set("Total", BC.TotalSize);
    Set("WorldR", BC.WorldRadius);
    Set("TotalR", BC.TotalRadius);
    Set("Sla", WorldExportMath.SeaLevelAdjustment(o.SeaLevel));
    Set("EdgeDropoff", true);
    Set("ZoneBlend", o.ZoneBlend);
    Set("OnServer", true);
    Set("Role", "host");
    Set("Workers", Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2)));
    Set("SourceForestScale", 1f);
    Set("ImportForestScale", probe.ForestScale);
    Set("ForestScaleText", "0.5");
    Set("SourceForestAmount", 0.5f);
    Set("ForestOverridesAllTrees", false);
    Set("WorldSizeSetting", 10000f);
    Set("EdgeSizeSetting", 500f);
    Set("AltGrid", BC.AltBiomeGridMode.Vanilla);
    Set("Clock", System.Diagnostics.Stopwatch.StartNew());
    Set("Started", DateTime.Now);
    Set("Written", new List<string>());
    Set("Notes", new List<string>());
    Set("Tracked", new List<string>());
    Set("BiomePixels", new long[BiomeRegistry.IndexCount]);
    Set("MinMetres", float.NaN);
    Set("MaxMetres", float.NaN);
    Set("LocationsGenerated", true);
    return job;
  }

  // The real export job (the biome pass and the text files) over this world: its biomes come from Better Continents' map through
  // WorldGenerator.GetBiome, beside Expand World Data's prefix.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Export()
  {
    var map = MakeBiomeMap();
    Load(MakeSettings(biomeMap: map));
    var wg = NewGenerator();
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    new Harmony("ewd-generation-export").Patch(AccessTools.Method(jobType, "CheckWorld"), prefix: new HarmonyMethod(typeof(Generation), nameof(CheckWorldPrefix)));
    const int n = 256;
    var o = WorldExport.Options.Default();
    (o.Size, o.Heightmap, o.Lava, o.Moss, o.Paint, o.Forest, o.Heat, o.AltBiomes, o.Locations, o.Sources, o.Preset, o.Biomes) = (n, false, false, false, false, false, false, false, false, false, false, true);
    var dir = Path.Combine(work, "generation-export");
    var job = MakeJob(o, dir, wg);
    typeof(WorldExport).GetProperty("IsRunning")!.GetSetMethod(true)!.Invoke(null, [true]);
    var drive = (System.Collections.IEnumerator)typeof(WorldExport).GetMethod("Drive", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [job])!;
    while (drive.MoveNext())
      System.Threading.Thread.Sleep(1);
    C(WorldExport.Phase == "Done" && WorldExport.LastError == null, $"the export finishes: phase {WorldExport.Phase}, error {WorldExport.LastError ?? "none"}");
    if (WorldExport.Phase != "Done")
      return;

    // Which pixels are DeadWastes, asked of the game independently of the export.
    long expected = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
        if (wg.GetBiome(WorldExportMath.PixelToWorld(col, n, BC.TotalSize), WorldExportMath.FileRowToWorldZ(row, n, BC.TotalSize)) == DeadWastes)
          expected++;
    long inPng = 0, meadowsInPng = 0;
    using (var png = Image.Load<Rgb24>(Path.Combine(dir, "biomemap.png")))
      for (int row = 0; row < n; row++)
        for (int col = 0; col < n; col++)
        {
          var p = png[col, row];
          if (p == new Rgb24(0x00, 0x44, 0x88)) inPng++;
          if (p == new Rgb24(0x00, 0xFF, 0x00)) meadowsInPng++;
        }
    C(expected > n * n / 5 && inPng == expected, $"biomemap.png has a DeadWastes pixel, colour 004488, wherever the game says DeadWastes ({inPng} of {expected}; {meadowsInPng} Meadows pixels in its own 00FF00)");
    var legend = File.ReadAllLines(Path.Combine(dir, "biomemap.txt"));
    C(legend.Length > 0 && legend[^1] == "DeadWastes: 004488" && legend.Take(10).SequenceEqual(ImageMapBiome.DefaultColors.Split('|')), $"biomemap.txt: the default legend, then \"DeadWastes: 004488\" last (got \"{legend.LastOrDefault()}\")");
    using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))))
    {
      var counts = manifest.RootElement.GetProperty("biomePixels");
      C(counts.TryGetProperty("DeadWastes", out var dead) && dead.GetInt64() == expected && !counts.TryGetProperty("1024", out _),
        $"manifest.json counts the biome under its name, \"DeadWastes\": {(counts.TryGetProperty("DeadWastes", out var d) ? d.GetInt64() : -1)} (no \"1024\")");
    }
    var back = ImageMapBiome.Create(Path.Combine(dir, "biomemap.png"));
    C(back != null && back.GetValue(0.375f, 0.5f) == DeadWastes && back.GetValue(0.125f, 0.5f) == Meadows, "the exported biomemap.png imports back through its legend: DeadWastes where it was, Meadows where it was");
  }
}

// Mathf.PerlinNoise's range and smoothness, without Unity (public: Expand World Data's rewritten code calls it).
public static class PerlinStub
{
  public static float Noise(float x, float y) => (float)(0.5 + 0.3 * Math.Sin(x * 1.7 + Math.Cos(y * 1.1)) + 0.2 * Math.Cos(y * 2.3 - x * 0.9));
}

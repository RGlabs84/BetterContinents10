// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// The water depth patch (BetterContinents.WaterDepthPatch, BetterContinents.WaterPatch.cs): that it binds to
// Heightmap.Regenerate in the installed game, patched through Harmony as PatchAll does; the facts of the game's own code
// that it relies on, read from the installed IL (a zone's water takes the terrain's corner depths once, at its Start;
// m_oneDepth is only ever set, never reset; Regenerate is the one way the corner depths change); and the comparison
// that decides whether a zone's water has to start over. What needs the engine (the water of a loaded zone) does not run
// offline: WaterVolume's static initializer asks the engine for shader property ids.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static void WaterTests()
  {
    WaterBindingTests();
    WaterGameFactTests();
    WaterDepthTests();
    WaterPostfixTests();
  }

  private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

  private static List<CodeInstruction> WaterIl(MethodBase method) => PatchProcessor.GetOriginalInstructions(method);

  // The methods and constructors a type declares whose IL satisfies `uses`, by name; and how many could not be read.
  private static (List<string> names, int unreadable) WaterUsers(Type type, Func<CodeInstruction, bool> uses)
  {
    var names = new List<string>();
    int unreadable = 0;
    foreach (var m in type.GetMethods(AllMembers | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(AllMembers | BindingFlags.DeclaredOnly)))
    {
      if (m.GetMethodBody() == null) continue;
      try
      {
        if (WaterIl(m).Any(uses)) names.Add(m.Name);
      }
      catch
      {
        unreadable++;
      }
    }
    return (names, unreadable);
  }

  // ------------------------------------------------------------------------------------------------ binding
  private static void WaterBindingTests()
  {
    var patch = typeof(BC.WaterDepthPatch);
    var regenerate = AccessTools.Method(typeof(Heightmap), "Regenerate");
    Check(regenerate != null && regenerate.IsPublic && !regenerate.IsStatic && regenerate.ReturnType == typeof(void) && regenerate.GetParameters().Length == 0,
      "Heightmap.Regenerate is in the installed game: a public instance method without parameters");

    // PatchAll finds the class by its attribute: the type and the method it names are the game's.
    var named = HarmonyMethodExtensions.GetMergedFromType(patch);
    Check(named != null && named.declaringType == typeof(Heightmap) && named.methodName == "Regenerate" && named.argumentTypes == null
          && AccessTools.DeclaredMethod(named.declaringType, named.methodName) == regenerate,
      "the patch class names Heightmap.Regenerate, and the name finds that method in the installed game");

    var postfix = patch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
    Check(postfix != null && postfix.ReturnType == typeof(void) && postfix.GetParameters().Select(p => (p.Name, p.ParameterType)).SequenceEqual([("__instance", typeof(Heightmap))]),
      "the postfix takes the heightmap as __instance and nothing else");

    var harmony = new Harmony("size-tests.water");
    int Ours() => Harmony.GetPatchInfo(regenerate)?.Postfixes.Count(p => p.owner == harmony.Id) ?? 0;
    try
    {
      harmony.CreateClassProcessor(patch).Patch();
      var info = Harmony.GetPatchInfo(regenerate);
      var ours = info?.Postfixes.Where(p => p.owner == harmony.Id).ToList();
      Check(ours is { Count: 1 } && ours[0].PatchMethod == postfix && !info.Prefixes.Any(p => p.owner == harmony.Id) && !info.Transpilers.Any(p => p.owner == harmony.Id),
        $"patched through Harmony as PatchAll does: one postfix on Heightmap.Regenerate and nothing else ({ours?.Count} postfixes)");
    }
    catch (Exception e)
    {
      Check(false, $"patching Heightmap.Regenerate: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
    }
    Check(Ours() == 0, "and unpatched again");
  }

  // ------------------------------------------------------------------------------------------------ the game's own code
  private static void WaterGameFactTests()
  {
    var detect = AccessTools.Method(typeof(WaterVolume), "DetectWaterDepth");
    var setup = AccessTools.Method(typeof(WaterVolume), "SetupMaterial");
    var update = AccessTools.Method(typeof(Heightmap), "UpdateCornerDepths");
    var oceanDepths = AccessTools.Method(typeof(Heightmap), "GetOceanDepth", Type.EmptyTypes);
    var clamp01 = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp01));
    var oneDepth = AccessTools.Field(typeof(WaterVolume), "m_oneDepth");
    var normalized = AccessTools.Field(typeof(WaterVolume), "m_normalizedDepth");
    var forced = AccessTools.Field(typeof(WaterVolume), "m_forceDepth");
    Check(detect != null && setup != null && update != null && oceanDepths != null && clamp01 != null && oneDepth != null && normalized != null && forced != null,
      "every WaterVolume and Heightmap member the patch uses is in the installed game");

    // The water reads the terrain's corner depths once, at its start, and sets its material up from them.
    var start = WaterIl(AccessTools.Method(typeof(WaterVolume), "Start"));
    int detectAt = start.FindIndex(c => c.Calls(detect));
    int setupAt = start.FindIndex(c => c.Calls(setup));
    Check(start.Count(c => c.Calls(detect)) == 1 && start.Count(c => c.Calls(setup)) == 1 && detectAt >= 0 && detectAt < setupAt,
      "WaterVolume.Start (installed IL) calls DetectWaterDepth, then SetupMaterial, once each");
    var (detectUsers, detectUnreadable) = WaterUsers(typeof(WaterVolume), c => c.Calls(detect));
    var (setupUsers, setupUnreadable) = WaterUsers(typeof(WaterVolume), c => c.Calls(setup));
    Check(detectUsers.SequenceEqual(["Start"]) && setupUsers.SequenceEqual(["Start"]) && detectUnreadable + setupUnreadable == 0,
      $"no other WaterVolume method runs DetectWaterDepth or SetupMaterial: the water is never set up again by the game ({detectUnreadable + setupUnreadable} methods unreadable offline)");

    // Heightmap.Regenerate is the one place the terrain's corner depths are refreshed.
    var regenerate = WaterIl(AccessTools.Method(typeof(Heightmap), "Regenerate"));
    var (updateUsers, updateUnreadable) = WaterUsers(typeof(Heightmap), c => c.Calls(update));
    Check(regenerate.Count(c => c.Calls(update)) == 1 && updateUsers.SequenceEqual(["Regenerate"]),
      $"Heightmap.Regenerate calls UpdateCornerDepths once, and no other Heightmap method does ({updateUnreadable} methods unreadable offline)");
    Check(updateUnreadable == 0, "every Heightmap method could be read, so none can hide a second caller");

    // DetectWaterDepth: the corner depths from GetOceanDepth, each over 10 m and clamped to 0..1 into m_normalizedDepth.
    var codes = WaterIl(detect);
    int arithmetic = 0;
    for (int i = 0; i + 4 < codes.Count; i++)
    {
      if (codes[i].opcode == OpCodes.Ldelem_R4 && codes[i + 1].opcode == OpCodes.Ldc_R4 && codes[i + 1].operand is 10f && codes[i + 2].opcode == OpCodes.Div
          && codes[i + 3].Calls(clamp01) && codes[i + 4].opcode == OpCodes.Stelem_R4)
        arithmetic++;
    }
    Check(codes.Count(c => c.Calls(oceanDepths)) == 1 && arithmetic == 4,
      $"DetectWaterDepth reads GetOceanDepth() once and makes each of the four corners Clamp01(depth / 10f) ({arithmetic} of 4 found): the arithmetic DepthsMatch repeats");

    // m_oneDepth: -1 means no single depth (the constructor, and Depth's test), and only DetectWaterDepth ever sets one:
    // for a level zone, from the first corner, and from m_forceDepth when the volume has no heightmap. It never resets it.
    var constructors = typeof(WaterVolume).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(WaterIl).ToList();
    Check(constructors.Count == 1 && constructors[0].Count(c => c.StoresField(oneDepth)) == 1
          && constructors[0].FindIndex(c => c.StoresField(oneDepth)) is var at && at > 0 && constructors[0][at - 1].operand is -1f,
      "WaterVolume's constructor starts m_oneDepth at -1");
    var depth = WaterIl(AccessTools.Method(typeof(WaterVolume), "Depth"));
    int read = depth.FindIndex(c => c.LoadsField(oneDepth));
    Check(read >= 0 && depth[read + 1].operand is -1f && depth[read + 2].opcode == OpCodes.Ble_Un_S,
      "WaterVolume.Depth answers with m_oneDepth when it is over -1, the value the patch resets it to");
    var (storeUsers, storeUnreadable) = WaterUsers(typeof(WaterVolume), c => c.StoresField(oneDepth));
    Check(storeUsers.OrderBy(n => n).SequenceEqual([".ctor", "DetectWaterDepth"]) && storeUnreadable == 0, "only the constructor and DetectWaterDepth store to m_oneDepth");
    var stores = codes.Select((c, i) => (c, i)).Where(x => x.c.StoresField(oneDepth)).Select(x => x.i).ToList();
    var before = stores.Select(i => codes[i - 1]).ToList();
    Check(stores.Count == 2 && before.Count(c => c.opcode == OpCodes.Ldelem_R4) == 1 && before.Count(c => c.LoadsField(forced)) == 1,
      $"DetectWaterDepth stores to m_oneDepth twice, the equal-depths case (a corner's depth) and the forced-depth case (m_forceDepth), and never resets it ({stores.Count} stores)");

    // SetupMaterial hands the shader the same numbers.
    var material = WaterIl(setup);
    Check(material.Count(c => c.LoadsField(normalized)) == 1, "SetupMaterial writes m_normalizedDepth to the material (when the volume has no forced depth)");
  }

  // ------------------------------------------------------------------------------------------------ the comparison
  private static void WaterDepthTests()
  {
    bool Match(float[] ocean, float[] held) => BC.WaterDepthPatch.DepthsMatch(ocean, held);
    // What DetectWaterDepth makes of a corner depth, written out.
    float Held(float depth) => Mathf.Clamp01(depth / 10f);

    Check(Match([0f, 5f, 10f, 25f], [0f, 0.5f, 1f, 1f]), "0 m is 0, 5 m is half way, 10 m and deeper are 1");
    Check(Match([-3f, 12f, 10f, 1000f], [0f, 1f, 1f, 1f]), "a depth under 0 clamps to 0 and one over 10 m to 1");
    Check(Match([7.77f, 0.1f, 3.3f, 9.99f], [Held(7.77f), Held(0.1f), Held(3.3f), Held(9.99f)]), "odd depths give exactly the floats the game makes of them");

    // A level zone, then terrain that is not level and the other way round: the water has to start over.
    Check(Match([30f, 30f, 30f, 30f], [1f, 1f, 1f, 1f]), "a level zone of deep sea matches");
    Check(!Match([30f, 30f, 30f, 30f], [0f, 0f, 0f, 0f]), "a zone that was land when its water started and is deep sea now does not match");
    Check(!Match([0f, 0f, 0f, 0f], [1f, 1f, 1f, 1f]), "a zone that was deep sea and is land now does not match");
    Check(!Match([0f, 0f, 0f, 0f], [0.5f, 0.5f, 0.5f, 0.5f]), "a level zone that was shallower does not match");

    // Each corner counts.
    for (int k = 0; k < 4; k++)
    {
      var ocean = new[] { 5f, 5f, 5f, 5f };
      var held = new[] { 0.5f, 0.5f, 0.5f, 0.5f };
      Check(Match(ocean, held), $"corner {k} unchanged: matches");
      ocean[k] = 6f;
      Check(!Match(ocean, held), $"corner {k} 1 m deeper: does not match");
      ocean[k] = 5f;
      held[k] = Held(5.0001f);
      Check(!Match(ocean, held), $"corner {k} held a hair off: does not match (exact, no tolerance)");
    }

    // The comparison is of what the water would hold, not of the raw depths: a change that reads the same is no change.
    Check(Match([12f, 13f, 14f, 15f], [1f, 1f, 1f, 1f]), "depths that all read as 1 match, whatever they are past 10 m");
    Check(Match([-5f, -1f, 0f, -0.5f], [0f, 0f, 0f, 0f]), "depths that all read as 0 match");

    // A depth that is not a number is held as one, as Single.Equals counts it: the water does not start over at every call.
    Check(Match([float.NaN, 5f, 5f, 5f], [float.NaN, 0.5f, 0.5f, 0.5f]), "a NaN corner matches the NaN it was held as");
    Check(!Match([float.NaN, 5f, 5f, 5f], [0f, 0.5f, 0.5f, 0.5f]), "a NaN corner does not match 0");

    // A volume that has not run its Start holds what it was made with (m_oneDepth -1, four zeros); DetectWaterDepth never
    // leaves that behind (four equal depths set m_oneDepth).
    bool NotStarted(float one, float[] held) => BC.WaterDepthPatch.NotStarted(one, held);
    Check(NotStarted(-1f, [0f, 0f, 0f, 0f]), "m_oneDepth -1 with four zeros: the volume has not read its depths yet");
    Check(!NotStarted(0f, [0f, 0f, 0f, 0f]), "a level land zone that has read its depths (m_oneDepth 0) has started");
    Check(!NotStarted(-1f, [0f, 0.5f, 1f, 1f]), "a sloping zone (m_oneDepth -1, depths not all equal) has started");
    Check(!NotStarted(1f, [1f, 1f, 1f, 1f]), "a level deep zone has started");
    for (int k = 0; k < 4; k++)
    {
      var held = new[] { 0f, 0f, 0f, 0f };
      held[k] = 0.25f;
      Check(!NotStarted(-1f, held), $"corner {k} away from zero: started");
    }

    // The decision, as a truth table: only this terrain's water, only once it has started, only when its depths moved.
    bool Stale(bool own, float one, float[] ocean, float[] held) => BC.WaterDepthPatch.Stale(own, one, ocean, held);
    float[] deep = [30f, 30f, 30f, 30f], land = [0f, 0f, 0f, 0f], zeros = [0f, 0f, 0f, 0f], ones = [1f, 1f, 1f, 1f];
    Check(Stale(true, 0f, deep, zeros), "own, started as land, deep sea now: stale");
    Check(Stale(true, 1f, land, ones), "own, started as deep sea, land now: stale");
    Check(Stale(true, -1f, [5f, 5f, 5f, 0f], [0.5f, 0.5f, 0.5f, 0.25f]), "own, started sloping, one corner moved: stale");
    Check(!Stale(false, 0f, deep, zeros), "another terrain's water is never stale for this terrain");
    Check(!Stale(true, -1f, deep, zeros), "own but not started: left to its Start");
    Check(!Stale(true, 1f, deep, ones), "own, started, depths unchanged: not stale");
    Check(!Stale(true, 0f, land, zeros), "own, started as land, still land: not stale");
  }

  // ------------------------------------------------------------------------------------------------ the postfix
  private static void WaterPostfixTests()
  {
    var patch = typeof(BC.WaterDepthPatch);
    var postfix = patch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
    var matches = patch.GetMethod("DepthsMatch", AllMembers);
    var stale = patch.GetMethod("Stale", AllMembers);
    var notStarted = patch.GetMethod("NotStarted", AllMembers);
    var equality = AccessTools.Method(typeof(UnityEngine.Object), "op_Equality");
    var inequality = AccessTools.Method(typeof(UnityEngine.Object), "op_Inequality");
    var failed = patch.GetMethod("Failed", AllMembers);
    var detect = AccessTools.Method(typeof(WaterVolume), "DetectWaterDepth");
    var setup = AccessTools.Method(typeof(WaterVolume), "SetupMaterial");
    var oneDepth = AccessTools.Field(typeof(WaterVolume), "m_oneDepth");
    var heightmap = AccessTools.Field(typeof(WaterVolume), nameof(WaterVolume.m_heightmap));

    // In a world without Better Continents the postfix returns at the first line: WaterVolume.Instances is never read
    // (its static initializer asks the engine for shader property ids, which cannot be answered here).
    var world = BC.Settings;
    try
    {
      BC.Settings = BC.BetterContinentsSettings.Disabled();
      string error = null;
      try
      {
        postfix.Invoke(null, [null]);
      }
      catch (TargetInvocationException e)
      {
        error = $"{e.InnerException?.GetType().Name}: {e.InnerException?.Message}";
      }
      Check(error == null, $"the postfix returns at once while Better Continents is off for the world ({error})");
    }
    finally
    {
      BC.Settings = world;
    }

    // What it does, for the volumes of this heightmap only, and only when their depths no longer match: reset the single
    // depth, read the corner depths again, set the material up again, in that order.
    var codes = WaterIl(postfix);
    int own = codes.FindIndex(c => c.LoadsField(heightmap));
    int compare = codes.FindIndex(c => c.Calls(stale));
    int reset = codes.FindIndex(c => c.StoresField(oneDepth));
    int detectAt = codes.FindIndex(c => c.Calls(detect));
    int setupAt = codes.FindIndex(c => c.Calls(setup));
    Check(codes.Count(c => c.StoresField(oneDepth)) == 1 && reset > 0 && codes[reset - 1].operand is -1f, "the postfix resets m_oneDepth to -1");
    Check(codes.Count(c => c.Calls(detect)) == 1 && codes.Count(c => c.Calls(setup)) == 1 && 0 <= own && own < compare && compare < reset && reset < detectAt && detectAt < setupAt,
      "after the volume's heightmap and depths are checked: DetectWaterDepth, then SetupMaterial, once each");
    // Which way it decides: a volume that is gone is skipped, "own" is the volume's heightmap being this one (Unity's
    // equality, against the patched instance), and the refresh runs only when Stale says so.
    int nullCheck = codes.FindIndex(c => c.Calls(inequality));
    Check(nullCheck > 0 && codes[nullCheck - 1].opcode == OpCodes.Ldnull && nullCheck < own, "a volume that is gone (Unity null) is skipped before anything is read from it");
    Check(own >= 0 && own + 2 < codes.Count && codes[own + 1].opcode == OpCodes.Ldarg_0 && codes[own + 2].Calls(equality) && own + 2 < compare,
      "own: the volume's m_heightmap == the heightmap being rebuilt, handed to Stale");
    Check(codes.Count(c => c.Calls(stale)) == 1 && !codes.Any(c => c.Calls(matches)) && compare + 1 < codes.Count
      && (codes[compare + 1].opcode == OpCodes.Brfalse || codes[compare + 1].opcode == OpCodes.Brfalse_S),
      "the refresh is skipped when Stale says false");
    Check(codes.Any(c => c.blocks.Any(b => b.blockType == ExceptionBlockType.BeginCatchBlock && b.catchType == typeof(Exception))) && codes.Any(c => c.Calls(failed)),
      "inside a try that reports a failure instead of letting it out");

    // Regenerate runs for every stroke of a terrain edit: nothing the postfix does while the depths match may allocate.
    bool Allocates(IEnumerable<CodeInstruction> il) => il.Any(c => c.opcode == OpCodes.Newobj || c.opcode == OpCodes.Newarr || c.opcode == OpCodes.Box
      || c.opcode == OpCodes.Ldftn || c.operand is MethodInfo { DeclaringType: { } t } && t == typeof(System.Linq.Enumerable));
    Check(!Allocates(codes) && !Allocates(WaterIl(matches)) && !Allocates(WaterIl(stale)) && !Allocates(WaterIl(notStarted)),
      "the postfix and the decision create no object, array, box or delegate and call no LINQ");

    // A failure is logged once, however often it happens.
    int mark = CapturingLogHandler.Lines.Count;
    failed.Invoke(null, [new InvalidOperationException("boom")]);
    failed.Invoke(null, [new InvalidOperationException("boom again")]);
    failed.Invoke(null, [new InvalidOperationException("and again")]);
    var logged = CapturingLogHandler.Lines.Skip(mark).Where(l => l.Contains("Water depth")).ToList();
    Check(logged.Count == 1 && logged[0].StartsWith("[Error]") && logged[0].Contains("boom") && !logged[0].Contains("boom again") && !logged[0].Contains("and again"),
      $"a failure is logged once as an error, with its message ({logged.Count} lines)");
  }
}

// Added by Wubarrk on 2026-09-27 for map mod compatibility (0.9.2).

// Offline checks of Better Continents 0.9.2's minimap generation (BetterContinents.MinimapPatch). The map must be
// finished when Minimap.GenerateWorldMap returns, because vanilla calls LoadMapData straight after it and map mods
// (ZenMap's biome-hidden cartography tables among them) copy the textures there. Unity textures cannot exist
// offline, so this checks what can be checked without the engine:
//  - GameUtils.SimpleParallelFor visits every row exactly once for any thread count, and a failing worker surfaces
//    as an exception after every worker has stopped (the prefix's vanilla fallback depends on both);
//  - the patch is synchronous: no coroutine, no background task, no second "fake" GenerateWorldMap call;
//  - a failure falls back to vanilla's GenerateWorldMap once, with one error, and not again on later calls,
//    both for a failure before the workers start and for a failure inside the workers.
// Loads the real pre-ILRepack BetterContinents.dll, the game's assemblies and BepInEx (see Program.cs, which this
// shares its process with).
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BetterContinents;
using HarmonyLib;
using BC = BetterContinents.BetterContinents;

namespace LiveCfgTest;

internal static class MinimapTests
{
  static int checks, failures;

  static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }

  static void Section(string s) => System.Console.WriteLine("== " + s);

  const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

  // Called once from Program.Run(); keeps its own tally and returns just the failure count.
  public static int Run()
  {
    checks = 0;
    failures = 0;
    ParallelFor();
    var patch = typeof(BC).GetNestedType("MinimapPatch", BindingFlags.NonPublic);
    C(patch != null, "BetterContinents.MinimapPatch exists");
    if (patch != null)
    {
      Shape(patch);
      Fallback(patch);
    }
    System.Console.WriteLine($"  minimap: {checks} checks, {failures} failures");
    return failures;
  }

  static void ParallelFor()
  {
    Section("GameUtils.SimpleParallelFor (the minimap's row loop)");
    foreach (int threads in new[] { 1, 2, 3, 4, 7, 16 })
      foreach (var (from, to) in new[] { (0, 1), (0, 3), (5, 21), (0, 2048) })
      {
        var visits = new int[to];
        GameUtils.SimpleParallelFor(threads, from, to, i => Interlocked.Increment(ref visits[i]));
        bool exact = Enumerable.Range(0, to).All(i => visits[i] == (i >= from ? 1 : 0));
        C(exact, $"{threads} thread(s), rows {from}..{to - 1}: every row once, none outside the range");
      }

    // One row throws. SimpleParallelFor must still wait for every worker (so none can write into the map arrays
    // after the fallback starts) and then throw, instead of returning as if the map were complete.
    int running = 0, finished = 0;
    Exception caught = null;
    try
    {
      GameUtils.SimpleParallelFor(4, 0, 64, i =>
      {
        Interlocked.Increment(ref running);
        try
        {
          if (i == 37)
            throw new InvalidOperationException("row 37 failed");
          Thread.Sleep(1);
        }
        finally
        {
          Interlocked.Increment(ref finished);
        }
      });
    }
    catch (Exception e)
    {
      caught = e;
    }
    C(caught is AggregateException, $"a failing row throws an AggregateException out of the loop (got {caught?.GetType().Name ?? "nothing"})");
    C(caught is AggregateException agg && agg.Flatten().InnerExceptions.Any(x => x.Message == "row 37 failed"), "the AggregateException carries the row's own exception");
    C(running == finished, $"every started row had finished when the exception arrived ({finished} of {running})");
  }

  static void Shape(Type patch)
  {
    Section("MinimapPatch is synchronous");
    var mt = patch.GetMethod("GenerateWorldMapMT", Any);
    C(mt != null && mt.ReturnType == typeof(void), $"GenerateWorldMapMT returns void, so the map is finished when it returns (got {mt?.ReturnType.Name ?? "no method"})");
    var coroutines = patch.GetMethods(Any).Where(m => typeof(IEnumerator).IsAssignableFrom(m.ReturnType)).Select(m => m.Name).ToList();
    C(coroutines.Count == 0, $"no coroutine left in MinimapPatch ({(coroutines.Count == 0 ? "none" : string.Join(", ", coroutines))})");
    C(patch.GetField("DoFakeGenerate", Any) == null, "the second 'fake' GenerateWorldMap call is gone, so other mods' postfixes run once, on the finished map");
    var nested = patch.GetNestedTypes(Any).Select(t => t.Name).ToList();
    // A coroutine version compiles to an iterator class named <GenerateWorldMapMT>d__N; the parallel loop's closure
    // (<>c__DisplayClassN_0) is expected and fine.
    C(!nested.Any(n => n.StartsWith("<GenerateWorldMapMT>d__")), $"no iterator state machine for GenerateWorldMapMT (nested types: {(nested.Count == 0 ? "none" : string.Join(", ", nested))})");

    var prefix = patch.GetMethod("GenerateWorldMapPrefix", Any);
    C(prefix != null && prefix.ReturnType == typeof(bool), "GenerateWorldMapPrefix is a bool prefix (false = the map is done, true = let vanilla draw it)");
    var target = prefix?.GetCustomAttributes<HarmonyPatch>().Select(a => a.info.methodName).FirstOrDefault(n => n != null);
    C(prefix?.GetCustomAttribute<HarmonyPrefix>() != null && target == "GenerateWorldMap", $"it is bound as a Harmony prefix of Minimap.GenerateWorldMap (target '{target}')");
  }

  static void Fallback(Type patch)
  {
    var prefix = patch.GetMethod("GenerateWorldMapPrefix", Any);
    var flag = patch.GetField("ThreadedGenerationFailed", Any);
    C(flag != null && flag.FieldType == typeof(bool), "MinimapPatch.ThreadedGenerationFailed exists");
    if (prefix == null || flag == null)
      return;
    var zWorld = typeof(ZNet).GetField("m_world", BindingFlags.NonPublic | BindingFlags.Static);
    var wgInstance = typeof(WorldGenerator).GetField("m_instance", BindingFlags.NonPublic | BindingFlags.Static);
    var oldWorld = zWorld?.GetValue(null);
    var oldWg = wgInstance?.GetValue(null);
    try
    {
      Section("a failure before the workers start falls back to vanilla, once");
      flag.SetValue(null, false);
      int before = ErrorLines();
      // A null Minimap fails on its first field read, before any thread starts.
      bool first = (bool)prefix.Invoke(null, [null]);
      C(first, "the prefix returns true, so vanilla's GenerateWorldMap draws the map");
      C((bool)flag.GetValue(null), "the failure is remembered for the session");
      C(ErrorLines() == before + 1 && LogHandler.Has("Multi-threaded minimap generation failed, so the game draws the map itself"), "exactly one error names the failure and the fallback");
      bool second = (bool)prefix.Invoke(null, [null]);
      C(second && ErrorLines() == before + 1, "the next call (Minimap.Update retries every frame) goes straight to vanilla without a second error");

      Section("a failure inside the workers falls back to vanilla, once");
      flag.SetValue(null, false);
      C(zWorld != null && wgInstance != null, "ZNet.m_world and WorldGenerator.m_instance can be set for the test");
      if (zWorld == null || wgInstance == null)
        return;
      // A world name no save folder can have, so vanilla's DeleteMapTextureData has nothing real to delete.
      zWorld.SetValue(null, new World { m_name = "bc-minimap-offline-test-" + Environment.ProcessId, m_seed = 1 });
      // No WorldGenerator: every worker fails on its first GetBiome call.
      wgInstance.SetValue(null, null);
      var map = (Minimap)RuntimeHelpers.GetUninitializedObject(typeof(Minimap));
      map.m_textureSize = 64;
      map.m_pixelSize = 12f;
      before = ErrorLines();
      bool threaded = (bool)prefix.Invoke(null, [map]);
      var line = LastError();
      C(threaded, "the prefix returns true, so vanilla's GenerateWorldMap draws the map");
      C(ErrorLines() == before + 1 && line.Contains("Multi-threaded minimap generation failed") && line.Contains("AggregateException"), "one error, and it comes from the workers (AggregateException), not from before the loop");
      C(LogHandler.Has("Generating minimap textures multi-threaded (64 x 64,"), "the start line names the texture size and the thread count");
      C(!LogHandler.Has("Finished generating minimap textures"), "no 'Finished' line after a failure");
      bool again = (bool)prefix.Invoke(null, [map]);
      C(again && ErrorLines() == before + 1, "the next call goes straight to vanilla without a second error");
    }
    catch (Exception e)
    {
      C(false, "the fallback checks ran without throwing: " + (e is TargetInvocationException t ? t.InnerException : e));
    }
    finally
    {
      flag.SetValue(null, false);
      zWorld?.SetValue(null, oldWorld);
      wgInstance?.SetValue(null, oldWg);
    }
  }

  // Only Better Continents' own minimap errors: vanilla's DeleteMapTextureData logs its own error offline, where
  // the save folder cannot be resolved, and that one must not count.
  static bool IsOurMinimapError(string l) => l.StartsWith("[Error]") && l.Contains("[BetterContinents]") && l.Contains("minimap");

  static int ErrorLines()
  {
    lock (LogHandler.Lines)
      return LogHandler.Lines.Count(IsOurMinimapError);
  }

  static string LastError()
  {
    lock (LogHandler.Lines)
      return LogHandler.Lines.LastOrDefault(IsOurMinimapError) ?? "";
  }
}

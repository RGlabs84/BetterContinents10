// Added by Wubarrk on 2026-10-04 for the vegetation twin guard (0.10.0).
//
// Offline checks of VegetationTwins: the guard's rules for one PlaceVegetation pass, bc_twins' scan and its choice of
// what to remove, and the PlaceVegetation tracking (ZoneSystemPatch.PlaceVegetationSaveCurrent) on the installed game's IL.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Debug = UnityEngine.Debug;
using Item = BetterContinents.VegetationTwins.Item;
using Pass = BetterContinents.VegetationTwins.Pass;

internal static class Program
{
  private static int checks, failures;

  private static void Check(bool ok, string what)
  {
    checks++;
    if (ok) return;
    failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  private static int Main()
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
    return RunAll();
  }

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    GuardTests();
    ScanTests();
    TrackingTests();
    System.Console.WriteLine($"twin-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------------------------------------ the guard
  private static void GuardTests()
  {
    const int Fir = 101, Rock = 202;
    // A zone filled a second time: every spot comes again, exactly.
    var p = new Pass();
    p.AddBefore(Fir, 10f, 20f);
    Check(p.Skip(Fir, 1, 10f, 20f) && p.SkippedBefore == 1, "a copy on an object already in the zone is kept out");
    Check(p.Skip(Fir, 1, 10.9f, 20f), "within 1 m of an object already there: kept out");
    Check(!p.Skip(Fir, 1, 11.2f, 20f), "1.2 m away: placed");
    Check(!p.Skip(Rock, 1, 10f, 20f), "another prefab on the same spot: placed");
    Check(p.SkippedBefore == 2 && p.SkippedOther == 0, "both counted as kept out over objects already there");

    // Two entries for one prefab meet (a vegetation map's +prefab, an alt-biome combo, a vanilla pair).
    p = new Pass();
    Check(!p.Skip(Fir, 1, 0f, 0f), "entry 1 places");
    Check(!p.Skip(Fir, 1, 0.3f, 0f), "entry 1 places its own next to it: groups stay as the game makes them");
    Check(p.Skip(Fir, 2, 0.5f, 0.5f) && p.SkippedOther == 1, "entry 2's copy within 1 m of entry 1's is kept out");
    Check(!p.Skip(Fir, 2, 3f, 0f), "entry 2 places away from entry 1's");
    Check(p.Skip(Fir, 3, 3f, 0.99f), "entry 3 is kept out by entry 2's placement too");

    // A copy that was kept out is not recorded, so it keeps nothing out itself.
    p = new Pass();
    p.Skip(Fir, 1, 0f, 0f);
    Check(p.Skip(Fir, 2, 0.9f, 0f), "entry 2 at 0.9 m: kept out");
    Check(!p.Skip(Fir, 3, 1.8f, 0f), "entry 3 at 1.8 m from entry 1's (0.9 m from the copy kept out): placed");

    // One entry listed twice takes two turns; the second turn's copies are kept out.
    p = new Pass();
    p.Skip(Fir, 4, 5f, 5f);
    Check(p.Skip(Fir, 5, 5f, 5f), "the same entry in the list twice: its second turn is kept out");

    // The edge, and the distance being horizontal.
    p = new Pass();
    p.AddBefore(Fir, 0f, 0f);
    Check(p.Skip(Fir, 1, 1f, 0f), "exactly 1 m: kept out");
    Check(VegetationTwins.Within(0, 0, 0.6f, 0.79f, 1f) && !VegetationTwins.Within(0, 0, 0.6f, 0.81f, 1f), "Within: horizontal distance up to the radius");
  }

  // ------------------------------------------------------------------------------------------------ bc_twins' scan
  private static Item I(int prefab, float x, float z, float y = 30f, uint id = 1, long user = 7, bool keep = false, bool planted = false) => new()
  {
    Prefab = prefab,
    X = x,
    Y = y,
    Z = z,
    ZoneX = (int)Math.Floor((x + 32f) / 64f),
    ZoneZ = (int)Math.Floor((z + 32f) / 64f),
    User = user,
    Id = id,
    Keep = keep,
    Planted = planted,
    Tag = id,
  };

  private static float Ground30(Item _) => 30f;
  private static float NoGround(Item _) => float.NaN;
  private static uint[] Removed(VegetationTwins.Scan scan) => [.. scan.Remove.Select(i => i.Id).OrderBy(i => i)];

  private static void ScanTests()
  {
    const int Fir = 101, Rock = 202, Puff = 303;

    // A zone filled twice: the copies share x and z. The one nearest the ground stays.
    var s = VegetationTwins.Find([I(Fir, 100f, 100f, 30f, 1), I(Fir, 100f, 100f, 30.6f, 2)], 1f, true, Ground30);
    Check(s.ExactPairs == 1 && s.NearPairs == 0 && Removed(s).SequenceEqual([2u]), "exact twins: the copy 0.6 m above the ground goes");
    s = VegetationTwins.Find([I(Fir, 100f, 100f, 31f, 1), I(Fir, 100f, 100f, 30f, 2)], 1f, true, Ground30);
    Check(Removed(s).SequenceEqual([1u]), "the older copy floats after a terrain change: it goes, though its id is lower");
    s = VegetationTwins.Find([I(Fir, 100f, 100f, 31f, 2), I(Fir, 100f, 100f, 30f, 1)], 1f, true, NoGround);
    Check(Removed(s).SequenceEqual([2u]), "no ground height: the lowest ZDOID stays");
    s = VegetationTwins.Find([I(Fir, 5f, 5f, 30f, 1, user: 9), I(Fir, 5f, 5f, 30f, 2, user: 3)], 1f, true, Ground30);
    Check(Removed(s).SequenceEqual([1u]), "a tie: ordered by the ZDOID's user first");
    s = VegetationTwins.Find([I(Fir, 0f, 0f, 30f, 1), I(Fir, 0f, 0f, 30f, 2), I(Fir, 0f, 0f, 30f, 3)], 1f, true, Ground30);
    Check(s.ExactPairs == 3 && Removed(s).SequenceEqual([2u, 3u]), "three copies on one spot: two go");

    // Near twins: two entries meeting. A plain remove leaves them; a radius takes them.
    var near = new[] { I(Fir, 0f, 0f, 30f, 1), I(Fir, 0.6f, 0f, 30.1f, 2) };
    s = VegetationTwins.Find(near, 1f, true, Ground30);
    Check(s.ExactPairs == 0 && s.NearPairs == 1 && s.Remove.Count == 0, "near twins: counted, not removed by a plain remove");
    s = VegetationTwins.Find(near, 1f, false, Ground30);
    Check(Removed(s).SequenceEqual([2u]), "near twins with a radius: the one further from the ground goes");
    s = VegetationTwins.Find(near, 0.5f, false, Ground30);
    Check(s.NearPairs == 0 && s.Remove.Count == 0, "0.6 m apart with a 0.5 m radius: not twins");

    // Crops players grow stand close on purpose: only copies on one spot go.
    s = VegetationTwins.Find([I(Puff, 0f, 0f, 30f, 1, planted: true), I(Puff, 0.5f, 0f, 30f, 2, planted: true)], 1f, false, Ground30);
    Check(s.NearPairs == 1 && s.Remove.Count == 0, "a planted prefab 0.5 m apart stays, even with a radius");
    s = VegetationTwins.Find([I(Puff, 0f, 0f, 30f, 1, planted: true), I(Puff, 0f, 0f, 30f, 2, planted: true)], 1f, false, Ground30);
    Check(Removed(s).SequenceEqual([2u]), "a planted prefab on one spot: the copy goes");

    // Player-made objects never go, and win over wild ones.
    s = VegetationTwins.Find([I(Fir, 0f, 0f, 35f, 1, keep: true), I(Fir, 0f, 0f, 30f, 2)], 1f, true, Ground30);
    Check(Removed(s).SequenceEqual([2u]), "player-made and wild on one spot: the wild one goes, though it sits on the ground");
    s = VegetationTwins.Find([I(Fir, 0f, 0f, 30f, 1, keep: true), I(Fir, 0f, 0f, 30f, 2, keep: true)], 1f, true, Ground30);
    Check(s.ExactPairs == 1 && s.Remove.Count == 0, "two player-made ones: both stay");

    // A chain A-B-C 0.6 m apart (A to C 1.2 m): what stays never has a twin left.
    var chain = new[] { I(Fir, 0f, 0f, 30.2f, 1), I(Fir, 0.6f, 0f, 30f, 2), I(Fir, 1.2f, 0f, 30.2f, 3) };
    s = VegetationTwins.Find(chain, 1f, false, Ground30);
    Check(s.NearPairs == 2 && Removed(s).SequenceEqual([1u, 3u]), "chain, middle nearest the ground: both ends go");
    chain[0].Y = 30f;
    chain[1].Y = 30.1f;
    s = VegetationTwins.Find(chain, 1f, false, Ground30);
    Check(Removed(s).SequenceEqual([2u]), "chain, an end nearest the ground: only the middle goes (the ends are 1.2 m apart)");

    // Grid cells: twins across a cell boundary, on both sides of zero.
    s = VegetationTwins.Find([I(Rock, 0.97f, 0f, 30f, 1), I(Rock, 1.04f, 0f, 30f, 2)], 1f, true, Ground30);
    Check(s.ExactPairs == 1, "twins across a cell boundary are found");
    s = VegetationTwins.Find([I(Rock, -0.02f, -0.02f, 30f, 1), I(Rock, 0.02f, 0.02f, 30f, 2)], 1f, true, Ground30);
    Check(s.ExactPairs == 1, "twins across zero are found");
    s = VegetationTwins.Find([I(Rock, 0f, 0f, 30f, 1), I(Fir, 0f, 0f, 30f, 2)], 1f, false, Ground30);
    Check(s.ExactPairs + s.NearPairs == 0, "two prefabs on one spot are not twins");
    s = VegetationTwins.Find([I(Rock, 0f, 0f, 30f, 1), I(Rock, 0.05f, 0f, 30f, 2)], 0.01f, true, Ground30);
    Check(s.ExactPairs == 1, "a radius under 10 cm still finds exact twins");

    // Tallies by prefab and zone.
    s = VegetationTwins.Find([I(Fir, 0f, 0f, 30f, 1), I(Fir, 0f, 0f, 30f, 2), I(Rock, 64f, 0f, 30f, 3), I(Rock, 64f, 0f, 30f, 4), I(Rock, 64.5f, 0f, 30f, 5)], 1f, true, Ground30);
    Check(s.ExactPairs == 2 && s.NearPairs == 2 && s.PairsByPrefab[Fir] == 1 && s.PairsByPrefab[Rock] == 3, "pairs by prefab");
    Check(s.ExactZones.SetEquals([(0, 0), (1, 0)]) && s.PairsByZone[(0, 0)] == 1 && s.PairsByZone[(1, 0)] == 3, "pairs by zone, and the zones with exact twins");

    // A world's worth: 200,000 objects with every tenth one doubled.
    var rng = new System.Random(5);
    var world = new List<Item>();
    for (uint i = 0; i < 200_000; i++)
    {
      var it = I(1000 + (int)(i % 40), (float)(rng.NextDouble() * 20000 - 10000), (float)(rng.NextDouble() * 20000 - 10000), 30f, i);
      world.Add(it);
      if (i % 10 == 0) world.Add(I(it.Prefab, it.X, it.Z, 30.5f, 1_000_000 + i));
    }
    var watch = Stopwatch.StartNew();
    s = VegetationTwins.Find(world, 1f, true, Ground30);
    watch.Stop();
    Check(s.ExactPairs == 20_000 && s.Remove.Count == 20_000 && s.Remove.All(r => r.Id >= 1_000_000), $"220,000 objects: the 20,000 copies found and chosen ({watch.ElapsedMilliseconds} ms)");
  }

  // ------------------------------------------------------------------------------------------------ PlaceVegetation tracking
  public static ZoneSystem.ZoneVegetation ExpandWorldDataLike(ZoneSystem.ZoneVegetation vegetation) => vegetation;

  private static List<CodeInstruction> Run(MethodInfo transpiler, IEnumerable<CodeInstruction> instructions) =>
    ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, [instructions.Select(i => i.Clone()).ToList()])).ToList();

  private static void TrackingTests()
  {
    var method = AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
    var original = PatchProcessor.GetOriginalInstructions(method);
    var enable = AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_enable));
    var patch = typeof(BC).GetNestedType("ZoneSystemPatch", BindingFlags.NonPublic);
    var transpiler = patch.GetMethod("PlaceVegetationSaveCurrent", BindingFlags.Public | BindingFlags.Static);
    var track = patch.GetMethod("SetCurrentVegetation", BindingFlags.NonPublic | BindingFlags.Static);
    int first = original.FindIndex(i => i.LoadsField(enable));
    Check(first > 0 && original.Count(i => i.LoadsField(enable)) == 1, "PlaceVegetation (installed IL) reads m_enable once: the top of the entry loop");

    var result = Run(transpiler, original);
    int k = result.FindIndex(i => i.Calls(track));
    Check(result.Count == original.Count + 1 && k == first && result[k + 1].LoadsField(enable), "the tracking call goes right before that read");
    Check(result.Take(k).Select(i => (i.opcode, i.operand)).SequenceEqual(original.Take(first).Select(i => (i.opcode, i.operand)))
          && result.Skip(k + 1).Select(i => (i.opcode, i.operand)).SequenceEqual(original.Skip(first).Select(i => (i.opcode, i.operand))),
      "nothing else changes");

    // Expand World Data's transpiler puts its own tracking call at the same read; ours still lands right before it.
    var ewd = original.Select(i => i.Clone()).ToList();
    ewd.Insert(first, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Program), nameof(ExpandWorldDataLike))));
    var both = Run(transpiler, ewd);
    Check(both.FindIndex(i => i.Calls(track)) == first + 1 && both[first + 2].LoadsField(enable), "after Expand World Data's tracking call, ours still goes right before the read");

    // Another mod rewrote the loop: nothing is inserted and a warning is logged, rather than the patch failing.
    int mark = CapturingLogHandler.Lines.Count;
    var without = original.Where(i => !i.LoadsField(enable)).ToList();
    var untouched = Run(transpiler, without);
    Check(untouched.Count == without.Count && !untouched.Any(i => i.Calls(track))
          && CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("twin guard are off")),
      "no m_enable read: nothing inserted, a warning instead of a failure");

    // The clear-area check is the last one before an object is placed, and only PlaceVegetation asks it.
    var clear = AccessTools.Method(typeof(ZoneSystem), "InsideClearArea");
    Check(original.Count(i => i.Calls(clear)) == 1, "PlaceVegetation asks InsideClearArea once");
    int unreadable = 0;
    var callers = new List<string>();
    foreach (var m in typeof(ZoneSystem).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    {
      if (m.GetMethodBody() == null) continue;
      try
      {
        if (PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(clear))) callers.Add(m.Name);
      }
      catch
      {
        unreadable++;
      }
    }
    Check(callers.SequenceEqual(["PlaceVegetation"]), $"no other ZoneSystem method asks InsideClearArea ({unreadable} methods unreadable offline)");

    // Harmony binds the patches' parameters by name and type.
    var parameters = method.GetParameters();
    Check(parameters.Any(x => x.Name == "zoneID" && x.ParameterType == typeof(Vector2s)) && parameters.Any(x => x.Name == "zoneCenterPos" && x.ParameterType == typeof(Vector3)),
      "PlaceVegetation still takes zoneID (Vector2s) and zoneCenterPos (Vector3) for the prefix");
    Check(clear.ReturnType == typeof(bool) && clear.GetParameters().Any(x => x.Name == "point" && x.ParameterType == typeof(Vector3)),
      "InsideClearArea still returns bool and takes point (Vector3) for the postfix");
    var prefix = patch.GetMethod("PlaceVegetationPrefix", BindingFlags.Public | BindingFlags.Static);
    Check(prefix.GetParameters().Where(x => !x.Name.StartsWith("__")).All(x => parameters.Any(y => y.Name == x.Name && y.ParameterType == x.ParameterType)),
      "every prefix parameter matches one of PlaceVegetation's");
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

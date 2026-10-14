// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// Offline checks of BC's own sea (the plan's 10.1):
//   Waves.cs    SeaWaves.One against a transcription of the game's WaterVolume.CalcWave (W = 1), against a transcription of the water shader's
//               weights (W < 1), and the game's constants read back from the installed game's IL.
//   Field.cs    SeaField: the grid, the exact distances against brute force, deterministic bytes (two builds, parallel against one worker), the
//               sampler against a transcription of the shader's arithmetic, the world's own rules (the layout patched for real) against the scaled
//               formula, the hot sea's mask, the same-as-game test, and what a field for a big world costs.
//   Draw.cs, Bubbles.cs  SeaDraw's causes, bc water force and probe, and the property blocks of the fallback (no bubble layer).
//   Patches.cs  SeaPatches: which worlds are candidates, the two felt toggles on the game's methods (installed, removed, the game's values before
//               the field is ready), the field's life, other mods' patches on the waves.
// "dotnet run -c Release -- <part of a section's name>" runs some of them; -v prints every check.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static int checks, failures;
  private static string filter = "";
  private static bool verbose;

  private static void Check(bool ok, string what)
  {
    checks++;
    if (ok)
    {
      if (verbose && running)
        System.Console.WriteLine("  ok " + what);
      return;
    }
    failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  private static bool running = true;

  private static void Section(string name)
  {
    running = filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    if (running)
      System.Console.WriteLine("== " + name);
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
    verbose = args.Contains("-v");
    filter = string.Join(" ", args.Where(a => a != "-v"));
    return RunAll();
  }

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    WaveTests();
    FieldTests();
    PatchTests();
    BubbleTests();
    System.Console.WriteLine($"water-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

  // A world as the settings hold it (version 12 is made since 0.10; its World Size and Edge Size lay it out).
  private static BC.BetterContinentsSettings World(int version = 12, float worldSize = 10000f, float edgeSize = 500f, bool enabled = true) =>
    new() { EnabledForThisWorld = enabled, Version = version, WorldSize = worldSize, EdgeSize = edgeSize };

  private static void SetMap(BC.BetterContinentsSettings s, string field, Type type) =>
    typeof(BC.BetterContinentsSettings).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, RuntimeHelpers.GetUninitializedObject(type));
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

  public static int Mark()
  {
    lock (Lines)
      return Lines.Count;
  }

  public static List<string> Since(int mark)
  {
    lock (Lines)
      return [.. Lines.Skip(mark)];
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Offline checks of the high-terrain patches (HighTerrain.cs, BetterContinents.HighTerrainPatch.cs):
//   Decision.cs   when a world is a high world; the land's height at Heightmap Amount 81 through ApplyHeightmap and GetBaseHeightV3;
//                 the rule for what is inside a dungeon, and the helpers the patched game code calls
//   Il.cs         every transpiler on the IL of the client's AND the dedicated server's game assemblies (they are two assemblies),
//                 the ones that can run here run on stand-ins, and an inventory of the methods of both assemblies that hold the
//                 numbers they replace: a game update that adds a caller or a ray fails it, which is the point
//   Toggles.cs    the toggles: which settings want them, that every target and patch method is in the installed game
//   Bound.cs      how far above the heightmap the game's own terrain reaches (HighTerrain.MaxMetres): the numbers, the game's biome height formulas
//                 transcribed and run over every noise extreme, and the Mountain and Mistlands formulas in both game builds (a game update fails it)
// "dotnet run -c Release -- dump" lists every instruction each transpiler changes.

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

  private static void Section(string name) => System.Console.WriteLine("-- " + name);

  internal static string Libs;
  private static readonly string[] Dirs = new string[2];

  private static int Main(string[] args)
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the
    // same name and the API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    Libs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools"));
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(Libs, "1.0", "client"), Libs };
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

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
      var x = e.ExceptionObject as Exception;
      string text;
      try { text = x?.StackTrace; } catch (Exception) { text = "(no stack trace)"; }
      System.Console.WriteLine($"UNHANDLED {x?.GetType().Name}: {x?.Message}\n{text}");
    };
    foreach (var (name, stage) in new (string, Action)[]
    {
      ("DecisionTests", DecisionTests), ("HeightTests", HeightTests), ("InteriorTests", InteriorTests), ("HelperTests", HelperTests), ("IlTests", IlTests),
      ("RunningTests", RunningTests), ("InventoryTests", InventoryTests), ("ToggleTests", ToggleTests), ("BoundTests", BoundTests), ("BoundGuardTests", BoundGuardTests),
    })
    {
      try
      {
        stage();
      }
      catch (Exception e)
      {
        string trace;
        try { trace = e.StackTrace; } catch (Exception) { trace = "(no stack trace)"; }
        Check(false, $"{name} stopped: {e.GetType().Name}: {e.Message}\n{trace}");
      }
    }
    System.Console.WriteLine($"high-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
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
      Lines.Add("[Exception] " + exception);
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Offline checks of the high-terrain patches (HighTerrain.cs, BetterContinents.HighTerrainPatch.cs):
//   Decision.cs   when a world is a high world; the land's height at Heightmap Amount 81 through ApplyHeightmap and GetBaseHeightV3;
//                 the rule for what is inside a dungeon, and the helpers the patched game code calls
//   Il.cs         every transpiler on the IL of the client's AND the dedicated server's game assemblies (they are two assemblies),
//                 the ones that can run here run on stand-ins, and an inventory of the methods of both assemblies that hold the
//                 numbers they replace: a game update that adds a caller or a ray fails it, which is the point
//   Toggles.cs    the toggles: which settings want them, the production hook list against the game by identity (type, method, signature, patch), that
//                 every transpiler changes the game method it is bound to, and the rules around them (Heightmap Amount's range, the Deep North weather)
//   Bound.cs      how far above the heightmap the game's own terrain reaches (HighTerrain.MaxMetres): the numbers, the game's biome height formulas
//                 transcribed and run over every noise extreme, the game's own base height (the top where there is no heightmap), and the Mountain,
//                 Mistlands and base height formulas in both game builds (a game update fails it)
//   Modes.cs      the High Terrain setting (Auto, On, Off): what each mode wants for every kind of world, what it saves and sends (disk, network,
//                 the older formats, a preset file, a world that never had the key), how a config and a new world read it, the console's parse
//   Session.cs    DynamicPatch run for real on the game's methods that .NET can compile here: the six groups on and off for a high world, a vanilla
//                 world after it, a live change of the amount and of the mode, a step that fails, the console's live change
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

  // What a run found that is not a check (which path ran, what a stand-in did).
  private static void Info(string text) => System.Console.WriteLine("    note: " + text);

  internal static string Libs;
  private static readonly string[] Dirs = new string[2];

  // The program's own entry (Entry.Main in Rewrite.cs runs first, before this class's statics, which refer to Better Continents' types, are touched).
  internal static int Start(string[] args)
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the
    // same name and the API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    Libs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools"));
    // The engine's AI module, which Pathfinding uses and the reference folder lacks, is read from the game's own install where there is one (read only).
    var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam", "steamapps", "common", "Valheim", "valheim_Data", "Managed");
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(Libs, "1.0", "client"), Libs, steam };
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
    Info($"BetterContinents.dll was read with {LibHarmonyRewrite.Calls} call(s) of Harmony.Patch pointed at Lib.Harmony's five-parameter overload (Rewrite.cs)");
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
      var x = e.ExceptionObject as Exception;
      string text;
      try { text = x?.StackTrace; } catch (Exception) { text = "(no stack trace)"; }
      System.Console.WriteLine($"UNHANDLED {x?.GetType().Name}: {x?.Message}\n{text}");
    };
    foreach (var (name, stage) in new (string, Action)[]
    {
      ("DecisionTests", DecisionTests), ("HeightTests", HeightTests), ("InteriorTests", InteriorTests), ("HelperTests", HelperTests), ("IlTests", IlTests), ("PartialTests", PartialTests),
      ("RunningTests", RunningTests), ("InventoryTests", InventoryTests), ("ToggleTests", ToggleTests), ("DeepNorthLogTests", DeepNorthLogTests), ("BoundTests", BoundTests), ("BoundGuardTests", BoundGuardTests),
      ("ModeTests", ModeTests), ("ModeSettingTests", ModeSettingTests), ("ModeSaveTests", ModeSaveTests), ("ConsoleTests", ConsoleTests), ("SessionTests", SessionTests),
    })
    {
      int before = checks;
      try
      {
        stage();
      }
      catch (Exception e)
      {
        string text;
        try { text = e.ToString(); } catch (Exception) { text = $"{e.GetType().Name}: {e.Message} (no stack trace)"; }
        Check(false, $"{name} stopped: {text}");
      }
      // A stage that ran nothing (a filter that matched no hook, a loop over an empty list) would pass for ever.
      Check(checks > before, $"{name} ran at least one check");
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

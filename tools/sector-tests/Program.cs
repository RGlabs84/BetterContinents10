// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Offline checks of the sectors a world past the game's 16 km limit gets (WorldSectors.cs):
//   Map.cs      the zone, sector and chunk arithmetic: every one of the 2048 x 2048 zones, against the game's own functions
//   Il.cs       the transpilers on the game's IL, the client's and the dedicated server's: only the expected constants change;
//               the patches bind to both assemblies
//   Plan.cs     the game's own save planning (ZDOMan.GetSaveClonePerChunk and what it calls), patched, run on made-up worlds:
//               the same chunks as the game's for a world inside its sectors; for a world past them every object in exactly one
//               chunk, nothing lost after a change, nothing written twice
//   Session.cs  when the map is on (the world's size, the save's chunks), switching it, and loading an older save
// "dotnet run -c Release -- dump" lists the constants of the methods the transpilers rewrite, on both assemblies.

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

  private static void Check(bool ok, string what)
  {
    checks++;
    if (ok) return;
    failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  private static void Section(string name) => System.Console.WriteLine("-- " + name);

  private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

  private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
  private static void SetField(object target, string name, object value) => target.GetType().GetField(name, Any)!.SetValue(target, value);
  private static void SetStatic(Type type, string name, object value) => type.GetField(name, Any)!.SetValue(null, value);
  private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, Any)!.GetValue(target);

  private static string LibsTools => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools"));

  // The dedicated server's own assemblies (a different build from the client's), in a context of their own.
  private static Assembly serverGame, serverUtils;

  private static int Main(string[] args)
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the
    // same name and the API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    var libs = LibsTools;
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
    var server = new AssemblyLoadContext("server");
    var serverDir = Path.Combine(libs, "1.0", "server");
    server.Resolving += (context, name) =>
    {
      var path = Path.Combine(serverDir, name.Name + ".dll");
      return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    };
    serverGame = server.LoadFromAssemblyPath(Path.Combine(serverDir, "assembly_valheim.dll"));
    serverUtils = server.LoadFromAssemblyPath(Path.Combine(serverDir, "assembly_utils.dll"));
    return args.Length > 0 && args[0] == "dump" ? Dump() : RunAll();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    foreach (var (name, test) in new (string, Action)[] { ("MapTests", MapTests), ("IlTests", IlTests), ("BindingTests", BindingTests), ("PlanTests", PlanTests), ("SessionTests", SessionTests) })
      try
      {
        test();
      }
      catch (Exception e)
      {
        // Exception.ToString() can fail here (a stack frame in a module the test does not have): say what is known.
        checks++;
        failures++;
        var chain = new System.Text.StringBuilder();
        for (var inner = e; inner != null; inner = inner.InnerException)
          chain.Append(inner == e ? "" : " / inner ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
        System.Console.WriteLine($"CRASH {name}: {chain}");
      }
    System.Console.WriteLine($"sector-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int Dump()
  {
    foreach (var (label, asm) in new[] { ("client", typeof(ZoneSystem).Assembly), ("server", serverGame) })
    {
      System.Console.WriteLine($"==== {label}");
      foreach (var (type, name) in new[] { ("ZDOMan", "GetSaveClonePerChunk"), ("ZDOMan", "AddObjectsPerChunk"), ("ZDOMan", "DecideChunkSize"), ("ZDOMan", "ResetSectorArray"),
                 ("ZoneSystem", "SectorToIndex"), ("ZoneSystem", "IndicesToIndex"), ("ZoneSystem", "IndexToSector"), ("ZoneSystem", "GetZonesChunk"),
                 ("ZoneSystem", "GetZoneFromChunk"), ("ZoneSystem", "ChunkIndexFromXY") })
        foreach (var m in asm.GetType(type).GetMethods(Any | BindingFlags.DeclaredOnly).Where(x => x.Name == name))
        {
          var codes = PatchProcessor.GetOriginalInstructions(m);
          System.Console.WriteLine($"-- {type}.{name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}): {m.GetMethodBody()!.GetILAsByteArray().Length} bytes of IL, {codes.Count} instructions");
          for (int i = 0; i < codes.Count; i++)
            if (WorldSectors.IntLoad(codes[i]) is { } value && value >= 8 || codes[i].opcode == System.Reflection.Emit.OpCodes.Call)
              System.Console.WriteLine($"  {i,4} {codes[i]}");
        }
    }
    return 0;
  }
}

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

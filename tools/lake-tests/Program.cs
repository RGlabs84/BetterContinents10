// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
//
// SCAFFOLD of the engine agent (B): the data agent (A) creates tools/lake-tests (LakeTests.csproj, Program.cs, Tests.Data.cs). This copy of the
// lake-tests driver lets Tests.Flood.cs, Tests.Spill.cs and Tests.Shape.cs run before A's arrives; when they meet, A's Program.cs and
// LakeTests.csproj win (the lead keeps theirs): the tests here use only Tests.C(ok, what) and Tests.Section(title).
//
// Offline checks of the lakes (the flood, the spill, the shape and its depths, and what the lake data holds). Every static method named *Test of the partial
// class Tests runs (in the order of their names), so each slice adds a Tests.<Slice>.cs of its own and nothing here changes. A test checks with C(ok, what), opens a
// section with Section(title), and may throw: the exception is a failure of that test, and the rest still run.
//
//   dotnet run -c Release                 every test
//   dotnet run -c Release -- Flood         the tests whose names contain "Flood" (not case sensitive)
//
// Like the other suites it loads the plugin's pre-ILRepack DLL (obj/Release/net4.8) and reads the installed game's assemblies from
// libs-Tools, so build the plugin first.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using UnityEngine;

namespace LakeTests;

// Unity's Debug.Log ends in a native call; keep the game's log here instead. Lines are kept as "[Log] text".
internal sealed class LogHandler : ILogHandler
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

  // The lines logged while an action ran.
  public static List<string> During(Action action)
  {
    int start;
    lock (Lines)
      start = Lines.Count;
    action();
    lock (Lines)
      return [.. Lines.Skip(start)];
  }
}

internal static class Program
{
  private static int Main(string[] args)
  {
    // BetterContinents.dll asks for BepInEx's HarmonyX 0Harmony (2.9), which cannot start on .NET 8; Lib.Harmony has the same name and the
    // API it touches. The game's and BepInEx's assemblies resolve from libs-Tools, read only.
    var libs = FindLibsTools();
    var dirs = new[] { AppContext.BaseDirectory, Path.Combine(libs, "1.0", "client"), libs, Path.Combine(libs, "BepInEx", "core"), Path.Combine(libs, "1.0", "server") };
    AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
      // The game's Steam wrapper is the file steamworks.net.dll and the assembly com.rlabrecque.steamworks.net.
      var file = name.Name == "com.rlabrecque.steamworks.net" ? "steamworks.net" : name.Name;
      foreach (var dir in dirs)
      {
        var path = Path.Combine(dir, file + ".dll");
        if (File.Exists(path))
          return context.LoadFromAssemblyPath(Path.GetFullPath(path));
      }
      return null;
    };
    return RunAll(args);
  }

  private static string FindLibsTools()
  {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
      var candidate = Path.Combine(dir.FullName, "libs-Tools");
      if (Directory.Exists(candidate))
        return candidate;
    }
    return "/home/rohan/WubarrkCODING/libs-Tools";
  }

  // Not inlined, so nothing of Better Continents is resolved before the handler above is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int RunAll(string[] args)
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    var filter = args.Length > 0 ? args[0] : null;
    var tests = typeof(Tests).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
      .Where(m => m.Name.EndsWith("Test", StringComparison.Ordinal) && m.GetParameters().Length == 0 && m.ReturnType == typeof(void))
      .Where(m => filter == null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
      .OrderBy(m => m.Name, StringComparer.Ordinal)
      .ToList();
    Tests.Work = Path.Combine(Path.GetTempPath(), "bc-lake-tests-" + Environment.ProcessId);
    Directory.CreateDirectory(Tests.Work);
    var started = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      foreach (var test in tests)
      {
        var before = Tests.Failures;
        var at = started.ElapsedMilliseconds;
        try
        {
          test.Invoke(null, null);
        }
        catch (TargetInvocationException e)
        {
          Tests.Crash(test.Name, e.InnerException ?? e);
        }
        System.Console.WriteLine($"-- {test.Name}: {(Tests.Failures == before ? "ok" : "FAILED")} ({started.ElapsedMilliseconds - at} ms)");
      }
    }
    finally
    {
      try
      {
        Directory.Delete(Tests.Work, true);
      }
      catch (IOException)
      {
      }
    }
    System.Console.WriteLine($"lake-tests: {Tests.Checks - Tests.Failures}/{Tests.Checks} checks passed in {tests.Count} tests");
    return Tests.Failures == 0 ? 0 : 1;
  }
}

// The shared part of the partial class Tests: the counters and the check helpers (the placement-tests' Tests.cs, cut down).
internal static partial class Tests
{
  public static int Checks, Failures;
  // A folder of this run's own (deleted at the end).
  public static string Work = "";

  // One check.
  public static void C(bool ok, string what)
  {
    Checks++;
    if (ok)
      return;
    Failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  public static void Section(string title) => System.Console.WriteLine("== " + title);

  // A test that threw.
  public static void Crash(string test, Exception e)
  {
    Checks++;
    Failures++;
    System.Console.WriteLine($"FAIL {test} threw {e.GetType().Name}: {e.Message}");
    System.Console.WriteLine(e.StackTrace);
  }
}

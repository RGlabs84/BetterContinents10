// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines) Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }
  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines) Lines.Add("[Exception] " + exception);
  }
}

internal static class Program
{
  static int checks, failures;
  static void Check(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }

  static int Main(string[] args)
  {
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      "/home/rohan/WubarrkCODING/libs-Tools/1.0/client",
      "/home/rohan/WubarrkCODING/libs-Tools",
      "/home/rohan/WubarrkCODING/libs-Tools/BepInEx/core",
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
    };
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
      foreach (var d in dirs)
      {
        var p = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(p))
          return ctx.LoadFromAssemblyPath(p);
      }
      return null;
    };
    return Run();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run()
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    var work = Path.Combine(Path.GetTempPath(), "bcexport-test-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    try
    {
      Tests.Math();
      Tests.HeightRoundTrip(work);
      Tests.EightBitRoundTrip(work);
      Tests.BiomeRoundTrip(work);
      Tests.AltBiomeRoundTrip(work);
      Tests.PaintRoundTrip(work);
      Tests.LocationMath();
      Tests.LocationColours();
      Tests.Commands();
      Tests.Json();
      Tests.ConfigAndReadme(work);
      TerrainTest.Run();
      EndToEnd.Run(work);
      LocationTest.Run();
      // After TerrainTest, whose generator stubs it builds zones with.
      PrecisionTest.Run();
    }
    catch (Exception e)
    {
      System.Console.WriteLine("CRASH " + e);
      failures++;
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
    System.Console.WriteLine($"{checks} checks, {failures} failures");
    return failures == 0 ? 0 : 1;
  }

  public static void C(bool ok, string what) => Check(ok, what);
}

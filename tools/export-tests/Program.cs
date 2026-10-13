// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0), and modified on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

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
      // BCEXPORT_ONLY=fine: the fine heights tests alone (the commands and the window's text, then the terrain test whose generator stubs
      // the exports use), for working on them. BCEXPORT_ONLY=big: just the big export (BCEXPORT_BIG=<size>, BCEXPORT_AMOUNT) and its cancel.
      if (Environment.GetEnvironmentVariable("BCEXPORT_ONLY") == "big" && int.TryParse(Environment.GetEnvironmentVariable("BCEXPORT_BIG"), out var bigOnly) && bigOnly > 0)
      {
        TerrainTest.Run();
        BigExport.Run(work, bigOnly, float.TryParse(Environment.GetEnvironmentVariable("BCEXPORT_AMOUNT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amountOnly) ? amountOnly : 81f);
        BigExport.CancelAt(work, bigOnly);
        System.Console.WriteLine($"{checks} checks, {failures} failures");
        return failures == 0 ? 0 : 1;
      }
      if (Environment.GetEnvironmentVariable("BCEXPORT_ONLY") == "fine")
      {
        Tests.Commands();
        Tests.HudText();
        PngRowWriterTest.Run(work);
        TerrainTest.Run();
        FineTest.Run(work);
        System.Console.WriteLine($"{checks} checks, {failures} failures");
        return failures == 0 ? 0 : 1;
      }
      Tests.Math();
      Tests.HeightRoundTrip(work);
      Tests.Amount81(work);
      AlphaTest.Run();
      RecordTest.Run(work);
      PngRowWriterTest.Run(work);
      Tests.EightBitRoundTrip(work);
      Tests.BiomeRoundTrip(work);
      Tests.AltBiomeRoundTrip(work);
      Tests.PaintRoundTrip(work);
      Tests.LocationMath();
      Tests.LocationColours();
      Tests.Commands();
      Tests.HudText();
      Tests.Json();
      Tests.ConfigAndReadme(work);
      TerrainTest.Run();
      EndToEnd.Run(work);
      LayerExport.Run(work);
      FineTest.Run(work);
      // Past the usual sizes: BCEXPORT_BIG=8192 (or 16384) runs the whole export there, with its memory measured.
      if (int.TryParse(Environment.GetEnvironmentVariable("BCEXPORT_BIG"), out var big) && big > 0)
      {
        BigExport.Run(work, big, float.TryParse(Environment.GetEnvironmentVariable("BCEXPORT_AMOUNT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount) ? amount : 81f);
        BigExport.CancelAt(work, big);
      }
      LocationTest.Run();
      PinTest.Run(work);
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

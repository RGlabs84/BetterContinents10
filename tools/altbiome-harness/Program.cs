// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace AltBiomeHarness;

// Offline checks of Better Continents 0.8.1's alt-biome code: control's foundation (cache, grid, placement control,
// server placement), authored's colour planting on top of it, and the three 0.8.1 fixes. It loads the real
// pre-ILRepack BetterContinents.dll and the real Valheim 1.0.15 assemblies and runs everything that does not need
// the Unity engine. Anything that would reach a Unity native call (UnityEngine.Random, Debug.Log's native sink) is
// avoided, redirected or caught.
internal static class Program
{
  private static readonly string LibsTools = FindLibsTools();

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

  private static int Main(string[] args)
  {
    // Read-only resolution of the game and BepInEx assemblies. The installed game is only read, and only for
    // Unity modules the tracked reference set lacks.
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      Path.Combine(LibsTools, "1.0", "client"),
      LibsTools,
      Path.Combine(LibsTools, "BepInEx", "core"),
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
    return Run(args);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int Run(string[] args)
  {
    H.Init();
    var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "test";
    try
    {
      switch (mode)
      {
        case "test":
          return Tests.RunAll();
        case "inspect":
        case "sealevel":
        case "plant":
        case "decode":
        case "breakmap":
          return ServerTools.Run(mode, args.Skip(1).ToArray());
        default:
          System.Console.WriteLine("AltBiomeHarness [test]                     every offline check; regenerates <repo>/palettes");
          System.Console.WriteLine("AltBiomeHarness inspect <settings>           dump a world's BetterContinents settings file");
          System.Console.WriteLine("AltBiomeHarness sealevel <settings> <delta>  change the sea level adjustment (for the cache test)");
          System.Console.WriteLine("AltBiomeHarness plant <settings> <dir>       paint an alt-biome map from the world's own biome map, bake it in");
          System.Console.WriteLine("AltBiomeHarness decode <settings>            print the baked alt-biome map and legend of a settings file");
          System.Console.WriteLine("AltBiomeHarness breakmap <settings>          append an unreadable alt-biome map block (the load must stop)");
          return mode == "help" ? 0 : 2;
      }
    }
    finally
    {
      H.Cleanup();
    }
  }
}

// Unity's Debug.Log ends in a native call; capture it instead.
internal sealed class CapturingLogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public static bool Echo = true;

  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    var s = $"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args));
    lock (Lines)
      Lines.Add(s);
    if (Echo)
      System.Console.WriteLine("    log " + s);
  }

  public void LogException(Exception exception, UnityEngine.Object context)
  {
    var s = "[Exception] " + exception.GetType().Name + ": " + exception.Message;
    lock (Lines)
      Lines.Add(s);
    if (Echo)
      System.Console.WriteLine("    log " + s);
  }

  public static int Mark()
  {
    lock (Lines)
      return Lines.Count;
  }

  public static List<string> Since(int mark)
  {
    lock (Lines)
      return Lines.Skip(mark).ToList();
  }
}

// Shared harness state and helpers.
internal static class H
{
  public static int Checks, Failures;
  public static readonly List<string> FailedChecks = [];
  public static string RepoRoot = "";
  public static string Work = "";
  public static string AltBiomesJson = "";
  public static BepInEx.Configuration.ConfigFile Config = null!;
  public static Func<BC.BetterContinentsSettings, BC.IAltBiomePlanting> DefaultProvider = null!;

  public static void Check(bool ok, string what)
  {
    Checks++;
    if (!ok)
    {
      Failures++;
      FailedChecks.Add(what);
    }
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }

  public static void Section(string name) => System.Console.WriteLine("== " + name);

  public static void Note(string text) => System.Console.WriteLine("    " + text);

  public static void Init()
  {
    UnityEngine.Debug.unityLogger.logHandler = new CapturingLogHandler();
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
      if (File.Exists(Path.Combine(dir.FullName, "BetterContinents.csproj")))
      {
        RepoRoot = dir.FullName;
        break;
      }
    }
    Work = Path.Combine(AppContext.BaseDirectory, "work-" + Environment.ProcessId);
    Directory.CreateDirectory(Work);
    AltBiomesJson = Environment.GetEnvironmentVariable("ALTBIOMES_JSON")
                    ?? "/home/rohan/WubarrkCODING/BetterContinents-TestMaps/altbiomes/altbiomes_1.0.15_full.json";

    // The config entries Better Continents reads outside Awake, bound with the defaults BetterContinents.cs declares.
    Config = new BepInEx.Configuration.ConfigFile(Path.Combine(Work, "harness.cfg"), true);
    BC.ConfigOverrideVersion = Config.Bind("Debug", "Override version", "");
    BC.ConfigMapSourceDir = Config.Bind("Debug", "Directory", "");
    BC.ConfigAltBiomeFile = Config.Bind("AltBiomes", "Altbiomemap File", "");
    BC.ConfigAltBiomeMode = Config.Bind("AltBiomes", "Mode", "Random");
    BC.ConfigAltBiomeGrid = Config.Bind("AltBiomes", "Grid", "WorldEdge");
    BC.ConfigAltBiomeSeed = Config.Bind("AltBiomes", "Fixed Seed", "");
    BC.ConfigAltBiomeChanceMultiplier = Config.Bind("AltBiomes", "Chance Multiplier", 1f);
    BC.ConfigAltBiomeAmountMultiplier = Config.Bind("AltBiomes", "Amount Multiplier", 1f);
    BC.ConfigAltBiomeEdgeScale = Config.Bind("AltBiomes", "Region Size Scale", 1f);
    BC.ConfigAltBiomeDistanceScale = Config.Bind("AltBiomes", "Distance Scale", 1f);
    BC.ConfigAltBiomeMinThickness = Config.Bind("AltBiomes", "Min Sector Thickness", 0f);
    BC.ConfigAltBiomeMeanHeight = Config.Bind("AltBiomes", "Mean Sector Height", false);
    BC.ConfigAltBiomeFixNeighbourCheck = Config.Bind("AltBiomes", "Fix Neighbour Check", false);
    BC.ConfigAltBiomeOverrides = Config.Bind("AltBiomes", "Overrides", "");
    DefaultProvider = BC.AltBiomeControl.PlantingProvider!;
  }

  public static void Cleanup()
  {
    try
    {
      Directory.Delete(Work, true);
    }
    catch
    {
      // Best effort.
    }
  }

  // ---------------------------------------------------------------------------------------------------- settings
  public static BC.BetterContinentsSettings NewSettings(BC.AltBiomeSettings altBiomes = null, bool enabled = true) =>
    new() { EnabledForThisWorld = enabled, AltBiomes = altBiomes };

  // Makes these settings the active world's and runs Better Continents' own Configure(), as a world load does.
  public static void Use(BC.BetterContinentsSettings settings, BC.IAltBiomePlanting planting = null)
  {
    BC.Settings = settings;
    BC.AltBiomeControl.PlantingProvider = planting == null ? DefaultProvider : _ => planting;
    BC.AltBiomeControl.Configure();
  }

  public static void Use(BC.AltBiomeSettings altBiomes, BC.IAltBiomePlanting planting = null) => Use(NewSettings(altBiomes), planting);

  // ---------------------------------------------------------------------------------------------------- alt biomes
  public static List<AltBiome> LoadAltBiomes()
  {
    using var doc = JsonDocument.Parse(File.ReadAllText(AltBiomesJson));
    var list = new List<AltBiome>();
    foreach (var e in doc.RootElement.GetProperty("m_alts").EnumerateArray())
    {
      var alt = new AltBiome
      {
        m_name = e.GetProperty("m_name").GetString()!,
        m_enabled = e.GetProperty("m_enabled").GetInt32() != 0,
        m_biome = (Heightmap.Biome)e.GetProperty("m_biome").GetInt32(),
        m_minDistanceFromCenter = e.GetProperty("m_minDistanceFromCenter").GetSingle(),
        m_minAmountSpawned = e.GetProperty("m_minAmountSpawned").GetInt32(),
        m_maxAmountSpawned = e.GetProperty("m_maxAmountSpawned").GetInt32(),
        m_chance = e.GetProperty("m_chance").GetSingle(),
        m_requireNeighbor = (Heightmap.Biome)e.GetProperty("m_requireNeighbor").GetInt32(),
        m_notNeighbor = (Heightmap.Biome)e.GetProperty("m_notNeighbor").GetInt32(),
        m_minEdgeSize = e.GetProperty("m_minEdgeSize").GetInt32(),
        m_maxEdgeSize = e.GetProperty("m_maxEdgeSize").GetInt32(),
        m_minAvgHeight = e.GetProperty("m_minAvgHeight").GetSingle(),
        m_maxAvgHeight = e.GetProperty("m_maxAvgHeight").GetSingle(),
        m_belowWorldX = e.GetProperty("m_belowWorldX").GetSingle(),
        m_aboveWorldX = e.GetProperty("m_aboveWorldX").GetSingle(),
        m_belowWorldY = e.GetProperty("m_belowWorldY").GetSingle(),
        m_aboveWorldY = e.GetProperty("m_aboveWorldY").GetSingle(),
      };
      foreach (var inc in e.GetProperty("m_incompatibleAltBiomes").EnumerateArray())
        alt.m_incompatibleAltBiomes.Add(inc.GetString()!);
      list.Add(alt);
    }
    return list;
  }

  public static void SetAltBiomes(IEnumerable<AltBiome> alts)
  {
    AltBiomeList.m_altBiomes.Clear();
    AltBiomeList.m_altBiomes.AddRange(alts);
    foreach (var a in AltBiomeList.m_altBiomes)
      a.Sectors.Clear();
  }

  // ---------------------------------------------------------------------------------------------------- worlds
  public const int Size = 2048;

  public static int G(float w) => AltBiomeWorldData.WorldSpaceToMapSpace(w);

  public static bool InCircle(float x, float z, float cx, float cz, float r) => (x - cx) * (x - cx) + (z - cz) * (z - cz) <= r * r;

  // Vanilla GenerateSectors ends by calling GenerateAltBiomes, which cannot run outside the game (UnityEngine.Random
  // is a native ECall and WorldGenerator.instance may be null), so the sector data is complete when it stops.
  public static void VanillaGenerateSectors(AltBiomeWorldData data)
  {
    try
    {
      data.GenerateSectors();
    }
    catch (Exception e) when (data.SectorsCalculated)
    {
      // SectorsCalculated is set right before the final GenerateAltBiomes call: the sectors are complete.
      _ = e;
    }
  }

  // What the GenerateSectors prefix does: Better Continents' planted build, or vanilla's own when nothing is planted.
  public static bool BuildSectorsAsPatched(AltBiomeWorldData data)
  {
    if (BC.AltBiomeControl.TryBuildPlantedSectors(data))
      return true;
    VanillaGenerateSectors(data);
    return false;
  }

  // Worlds of round islands in open sea, for the random-placement runs.
  public static AltBiomeWorldData IslandWorld(IReadOnlyList<(float x, float z, float r, Heightmap.Biome b, float h)> islands)
  {
    var data = new AltBiomeWorldData(Size);
    for (int i = 0; i < Size; i++)
    {
      for (int j = 0; j < Size; j++)
      {
        float x = AltBiomeWorldData.MapSpaceToWorldSpace(j), z = AltBiomeWorldData.MapSpaceToWorldSpace(i);
        var b = Heightmap.Biome.Ocean;
        float h = -40f;
        if (x * x + z * z > 110250000f)
          h = -1000f;
        else
        {
          foreach (var isl in islands)
          {
            if (InCircle(x, z, isl.x, isl.z, isl.r))
            {
              b = isl.b;
              h = isl.h;
              break;
            }
          }
        }
        data.PointBiomes[j, i] = b.ToBiomeIndex();
        data.PointHeights[j, i] = h;
      }
    }
    data.PointsGenerated = true;
    return data;
  }

  public static AltBiomeWorldData CopyPoints(AltBiomeWorldData source)
  {
    var data = new AltBiomeWorldData(source.Size);
    Array.Copy(source.PointBiomes, data.PointBiomes, source.PointBiomes.Length);
    Array.Copy(source.PointHeights, data.PointHeights, source.PointHeights.Length);
    data.PointsGenerated = true;
    return data;
  }

  // ---------------------------------------------------------------------------------------------------- images
  public const float TotalSize = 21000f;

  // Writes an alt-biome map image: pixel (px, py) is painted with paint(x, z) for its world position (top row north).
  public static string WritePng(string dir, int size, Func<float, float, Rgba32?> paint, string legend)
  {
    Directory.CreateDirectory(dir);
    var png = Path.Combine(dir, "altbiomemap.png");
    using (var img = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 0)))
    {
      for (int py = 0; py < size; py++)
      {
        for (int px = 0; px < size; px++)
        {
          // BC flips images vertically on load: the top row is north (+z).
          float u = px / (float)(size - 1), v = (size - 1 - py) / (float)(size - 1);
          var c = paint((u - 0.5f) * TotalSize, (v - 0.5f) * TotalSize);
          if (c != null)
            img[px, py] = c.Value;
        }
      }
      img.SaveAsPng(png);
    }
    if (legend != null)
      File.WriteAllText(Path.Combine(dir, "altbiomemap.txt"), legend);
    return png;
  }

  public static Rgba32 Hex(string hex)
  {
    hex = hex.TrimStart('#');
    return new Rgba32(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
  }

  public static string Mods(BiomeSector s) => string.Join(",", s.AltBiomes.Select(a => a.m_name));

  public static string SortedMods(BiomeSector s) => string.Join(",", s.AltBiomes.Select(a => a.m_name).OrderBy(n => n, StringComparer.Ordinal));

  public static bool LogContains(int mark, string a, string b = null) =>
    CapturingLogHandler.Since(mark).Any(l => l.Contains(a) && (b == null || l.Contains(b)));
}

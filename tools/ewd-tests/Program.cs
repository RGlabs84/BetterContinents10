// Added by Wubarrk on 2026-09-29 for Expand World Data biomes (0.9.3), and modified on 2026-10-04 for the unifying refactor (0.10.0).

// Offline checks of Better Continents' support for the biomes Expand World Data adds, against the real
// ExpandWorldData.dll 1.73.0 (the Hexium package, kept in libs-Tools/1.0/thirdparty/expand-world-data-2026-09-29):
// its Harmony patches of the game's biome conversions and of Enum parsing are applied as it applies them, and its
// biome names are set the way a joining client receives them (BiomeManager.SetNames). Loads the real pre-ILRepack
// BetterContinents.dll, the game's assemblies and BepInEx (Generation.cs loads both DLLs from memory with a few call sites changed).
//
// Each mode runs in its own process ("vanilla", then "ewd"), patched before any Better Continents code runs: the JIT
// may inline the game's small biome conversions into a caller compiled before the patch, which in the game cannot
// happen (Expand World Data patches in Awake, Better Continents reads the conversions from Start on).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Logging;
using BetterContinents;
using HarmonyLib;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace EwdTest;

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
  public static bool Has(string part) { lock (Lines) return Lines.Any(l => l.Contains(part)); }
  public static int Count(string part) { lock (Lines) return Lines.Count(l => l.Contains(part)); }
  public static void Clear() { lock (Lines) Lines.Clear(); }
}

internal static class Program
{
  const string EwdDll = "/home/rohan/WubarrkCODING/libs-Tools/1.0/thirdparty/expand-world-data-2026-09-29/ExpandWorldData-1.73.0/ExpandWorldData.dll";

  const Heightmap.Biome DeadWastes = (Heightmap.Biome)0x400;
  const Heightmap.Biome AshenMarsh = (Heightmap.Biome)0x800;
  const Heightmap.Biome Unnamed = (Heightmap.Biome)0x1000;
  const Heightmap.Biome LastBit = (Heightmap.Biome)int.MinValue;
  const Heightmap.Biome Bit7 = (Heightmap.Biome)0x80;

  static int checks, failures;
  internal static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }
  internal static void Section(string s) => System.Console.WriteLine("== " + s);

  static int Main(string[] args)
  {
    if (args.Length == 0)
    {
      int failed = 0;
      foreach (var mode in new[] { "vanilla", "ewd" })
      {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, mode) { UseShellExecute = false })!;
        child.WaitForExit();
        if (child.ExitCode != 0)
          failed++;
      }
      System.Console.WriteLine(failed == 0 ? "ALL MODES PASSED" : $"{failed} MODE(S) FAILED");
      return failed == 0 ? 0 : 1;
    }
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      "/home/rohan/WubarrkCODING/libs-Tools/1.0/client",
      "/home/rohan/WubarrkCODING/libs-Tools",
      "/home/rohan/WubarrkCODING/libs-Tools/BepInEx/core",
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
      Path.GetDirectoryName(EwdDll)!,
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
    Generation.LoadBetterContinents();
    return Run(args[0]);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run(string mode)
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    var work = Path.Combine(Path.GetTempPath(), $"bc-ewd-test-{mode}-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    System.Console.WriteLine($"#### mode {mode}");
    try
    {
      if (mode == "vanilla")
        Vanilla(work);
      else if (mode == "ewd")
        WithEwd(work);
      else
        throw new ArgumentException("unknown mode " + mode);
    }
    catch (Exception ex)
    {
      C(false, $"unexpected exception: {ex}");
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
    System.Console.WriteLine($"RESULT ({mode}): {checks - failures}/{checks} passed" + (failures > 0 ? $", {failures} FAILED" : ""));
    return failures == 0 ? 0 : 1;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Vanilla: no Expand World Data. The game's conversions throw for anything but the ten vanilla biomes.
  // ---------------------------------------------------------------------------------------------------------------
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Vanilla(string work)
  {
    Section("biome table without Expand World Data");
    BiomeRegistry.RefreshTable();
    C(BiomeRegistry.IndexCount == (int)Heightmap.BiomeIndex.Count, $"ten biome indices (got {BiomeRegistry.IndexCount})");
    C(BiomeRegistry.Extra == Heightmap.Biome.None, $"no biomes beyond vanilla (got 0x{(uint)BiomeRegistry.Extra:X})");
    C(BC.PlainSectors.Length == 10 && Enumerable.Range(0, 10).All(i => BC.PlainSectors[i].Biome == ((Heightmap.BiomeIndex)i).ToBiome()), "ten plain sectors, one per vanilla index");
    C(!BiomeRegistry.IsValid(DeadWastes) && !BiomeRegistry.IsValid(Bit7) && !BiomeRegistry.IsValid(Heightmap.Biome.All), "0x400, 0x80 and All are not biomes the game can use");
    C(BiomeRegistry.ToSafeIndex(DeadWastes) == 0 && BiomeRegistry.ToSafeIndex(Heightmap.Biome.Mistlands) == 9, "ToSafeIndex: 0x400 -> 0, Mistlands -> 9");
    C(BiomeRegistry.ToSafeBiome(11) == Heightmap.Biome.None && BiomeRegistry.ToSafeBiome(-1) == Heightmap.Biome.None && BiomeRegistry.ToSafeBiome(9) == Heightmap.Biome.Mistlands, "ToSafeBiome: out of range -> None, 9 -> Mistlands");

    Section("legend names without Expand World Data");
    ExpectParse("Meadows", Heightmap.Biome.Meadows);
    ExpectParse(" blackforest ", Heightmap.Biome.BlackForest);
    ExpectParse("None", Heightmap.Biome.None);
    ExpectParse("512", Heightmap.Biome.Mistlands);
    ExpectNoParse("DeadWastes");
    ExpectNoParse("1024");
    ExpectNoParse("All");
    ExpectNoParse("Land");
    ExpectNoParse("Meadows, Swamp");
    ExpectNoParse("3");
    ExpectParse("Black Forest", Heightmap.Biome.BlackForest);
    ExpectParse("deep_north", Heightmap.Biome.DeepNorth);
    ExpectParse("Ash-Lands", Heightmap.Biome.AshLands);
    ExpectNoParse("Meadow");
    C(BiomeRegistry.Name(Heightmap.Biome.AshLands) == "AshLands" && BiomeRegistry.Name(DeadWastes) == "1024", $"BiomeName: AshLands, 0x400 -> 1024 (got {BiomeRegistry.Name(DeadWastes)})");

    Section("one bad legend entry no longer discards the legend");
    // The tester's legend, on a game without Expand World Data: DeadWastes is not a biome here. Meadows is in the
    // map maker's own colour, which the whole-legend fallback to the default colours used to throw away.
    var path = WriteMap(work, "biomemap", [
      ("Meadows", new Rgba32(0x10, 0xA0, 0x10)), ("DeadWastes", new Rgba32(0x8B, 0x45, 0x13)),
      ("Ocean", new Rgba32(0x00, 0x00, 0xFF)), ("Mistlands", new Rgba32(0x7F, 0x7F, 0x7F))]);
    LogHandler.Clear();
    var map = ImageMapBiome.Create(path);
    C(map != null, "the map loads");
    C(LogHandler.Has("invalid biome name DeadWastes, skipped"), "the bad entry is reported by name");
    C(!LogHandler.Has("Cannot load file"), "the legend is not discarded");
    C(map != null && map.LegendColors.Count == 3 && map.LegendColors.TryGetValue(Heightmap.Biome.Meadows, out var meadows) && meadows.g == 0xA0,
      "the other three entries keep the map maker's colours (Meadows 10A010)");
    C(map != null && map.GetValue(Q(0), 0.5f) == Heightmap.Biome.Meadows && map.GetValue(Q(2), 0.5f) == Heightmap.Biome.Ocean,
      "Meadows and Ocean pixels decode by the map maker's colours");
    C(map != null && map.GetValue(Q(1), 0.5f) == Heightmap.Biome.None && map.LegendErrors == 1,
      "the skipped DeadWastes colour reads as None (the default generation decides), not as the nearest other biome");
    C(map != null && map.UnresolvedLegend.Count == 1 && map.UnresolvedLegend[0].Name == "DeadWastes" && map.UnresolvedLegend[0].Color.r == 0x8B,
      "the skipped entry keeps its name and colour, so a world export writes it back for a game that has the biome");
    var legacyPkg = new ZPackage();
    map!.SerializeLegacy(legacyPkg, 11, network: true);
    legacyPkg.SetPos(0);
    var legacy = ImageMapBiome.LoadLegacy(legacyPkg, 11);
    C(legacy != null && legacy.GetValue(Q(1), 0.5f) == Heightmap.Biome.None && legacy.Biomes.SequenceEqual(map.Biomes),
      "the legacy settings format sends the skipped entry too: the other side reads its colour as None, not as the nearest biome");
    C(LogHandler.Has("Biome map (64 x 64): Meadows 25.00%, Ocean 25.00%, Mistlands 25.00%, None 25.00%"), "one summary line gives each biome's share");

    Section("bc b fn and bc reload bm keep the world's map when the new one has errors");
    var dirG = Path.Combine(work, "reload");
    Directory.CreateDirectory(dirG);
    var st = new BC.BetterContinentsSettings();
    var good = WriteMap(dirG, "biomemap", [("Meadows", new Rgba32(0, 255, 0)), ("Swamp", new Rgba32(0x7F, 0x7F, 0)), ("Ocean", new Rgba32(0, 0, 255)), ("Plains", new Rgba32(255, 255, 0))]);
    C(st.SetBiomePath(good) && st.HasBiomeMap && st.GetBiomeOverride(Q(1), 0.5f) == Heightmap.Biome.Swamp, "a clean map is applied");
    File.WriteAllLines(Path.ChangeExtension(good, ".txt"), ["Meadows: 00FF00", "Swampy: 7F7F00", "Ocean: 0000FF", "Plains: FFFF00"]);
    LogHandler.Clear();
    st.ReloadBiomeMap();
    C(st.GetBiomeOverride(Q(1), 0.5f) == Heightmap.Biome.Swamp && LogHandler.Has("keeps its current biome map"),
      "a reload whose legend has an error keeps the world's map (it used to reload with the default colours, and the save kept that)");
    C(!st.SetBiomePath(Path.Combine(dirG, "missing.png")) && st.HasBiomeMap && st.GetBiomeOverride(Q(1), 0.5f) == Heightmap.Biome.Swamp,
      "a wrong path keeps the map too (it switched the biome map off before)");
    File.WriteAllLines(Path.ChangeExtension(good, ".txt"), ["Meadows: 00FF00", "Mountain: 7F7F00", "Ocean: 0000FF", "Plains: FFFF00"]);
    st.ReloadBiomeMap();
    C(st.GetBiomeOverride(Q(1), 0.5f) == Heightmap.Biome.Mountain, "a clean reload applies");
    C(!st.SetBiomePath("") && !st.HasBiomeMap, "an empty path still switches the biome map off");

    Section("legend comments and bad colours");
    var dir2 = Path.Combine(work, "legend2");
    Directory.CreateDirectory(dir2);
    var p2 = WriteMap(dir2, "biomemap", [("Meadows", new Rgba32(0x10, 0xA0, 0x10)), ("Swamp", new Rgba32(0x60, 0x60, 0x00)), ("Ocean", new Rgba32(0, 0, 0xFF)), ("Plains", new Rgba32(0xFF, 0xFF, 0))]);
    File.WriteAllLines(Path.ChangeExtension(p2, ".txt"), ["# my colours: keep these", "Meadows: 10A010", "Swamp: 96,96,300", "Ocean: 0000FF", "Plains: FFFF00"]);
    LogHandler.Clear();
    var m2 = ImageMapBiome.Create(p2);
    C(m2 != null && !LogHandler.Has("# my colours") && m2.LegendColors.Count == 4 && m2.LegendColors[Heightmap.Biome.Swamp].r == 0x7F && m2.LegendErrors == 1,
      "a # line is a comment; Swamp's bad colour falls back to its default (7F7F00), counted as an error; the rest is untouched");
    C(LogHandler.Has("invalid colour 96,96,300 for Swamp"), "the bad colour is reported with its biome");
    File.WriteAllLines(Path.ChangeExtension(p2, ".txt"), ["Meadows: 10A010", "Swamp: 8B451", "Ocean 0000FF", "Plains: FFFF00"]);
    LogHandler.Clear();
    m2 = ImageMapBiome.Create(p2);
    C(m2 != null && m2.LegendColors.Count == 3 && m2.LegendErrors == 2 && LogHandler.Has("invalid colour 8B451 for Swamp") && LogHandler.Has("\"Ocean 0000FF\" is not \"name: colour\""),
      "a hex colour that does not parse and a line without its colon are reported and counted (they read as transparent black, or vanished, before)");
    // A vanilla biome whose colour does not parse keeps its default colour; a comment after an invisible character is
    // still a comment (StartsWith("#") is culture-sensitive and says no on the game's Mono).
    File.WriteAllLines(Path.ChangeExtension(p2, ".txt"), ["\u200B# a note pasted from a web page", "Meadows: 10A010", "Swamp: 7F7F00", "Ocean: 0000FF", "Plains: 255,255,x"]);
    File.Delete(p2);
    var p2b = WriteMap(dir2, "biomemap", [("Meadows", new Rgba32(0x10, 0xA0, 0x10)), ("Swamp", new Rgba32(0x7F, 0x7F, 0)), ("Ocean", new Rgba32(0, 0, 0xFF)), ("Plains", new Rgba32(0xFF, 0xFF, 0))]);
    File.WriteAllLines(Path.ChangeExtension(p2b, ".txt"), ["\u200B# a note pasted from a web page", "Meadows: 10A010", "Swamp: 7F7F00", "Ocean: 0000FF", "Plains: 255,255,x"]);
    LogHandler.Clear();
    m2 = ImageMapBiome.Create(p2b);
    C(m2 != null && m2.GetValue(Q(3), 0.5f) == Heightmap.Biome.Plains && m2.LegendErrors == 1 && LogHandler.Has("invalid colour 255,255,x for Plains") && LogHandler.Has("its default colour is used"),
      "Plains with a colour that does not parse keeps its default colour (FFFF00): its pixels still read as Plains, and it is reported");
    C(!LogHandler.Has("a note pasted"), "a # line after an invisible character is a comment");
    File.WriteAllLines(Path.ChangeExtension(p2, ".txt"), ["# nothing but a comment"]);
    WriteMap(dir2, "biomemap", [("Meadows", new Rgba32(0x10, 0xA0, 0x10)), ("Swamp", new Rgba32(0x60, 0x60, 0x00)), ("Ocean", new Rgba32(0, 0, 0xFF)), ("Plains", new Rgba32(0xFF, 0xFF, 0))]);
    File.WriteAllLines(Path.ChangeExtension(p2, ".txt"), ["# nothing but a comment"]);
    LogHandler.Clear();
    m2 = ImageMapBiome.Create(p2);
    C(m2 != null && m2.LegendErrors == 1 && LogHandler.Has("No biome colors defined"), "a legend with no entries counts as an error (so a reload keeps the world's map)");

    Section("location legend: Expand World Data's Name:Alias locations");
    var dir3 = Path.Combine(work, "locations");
    Directory.CreateDirectory(dir3);
    var p3 = WriteMap(dir3, "locationmap", [("x", new Rgba32(255, 0, 0)), ("y", new Rgba32(0, 255, 0)), ("z", new Rgba32(0, 0, 255)), ("w", new Rgba32(0, 0, 0))]);
    File.WriteAllLines(Path.ChangeExtension(p3, ".txt"), ["# EWD locations", "Runestone_Boars:Copy: 255,0,0", "Eikthyrnir: 00FF00", "Eikthyrnir: 0000FF", "Vendor_BlackForest: 1,2,300", "Crypt2 FF00FF"]);
    LogHandler.Clear();
    var loc = ImageMapLocation.Create(p3);
    C(loc != null && loc.LegendColors.ContainsKey("Runestone_Boars:Copy") && loc.LegendColors["Runestone_Boars:Copy"].r == 255,
      "\"Runestone_Boars:Copy: 255,0,0\" keeps its full name (it was dropped before)");
    C(loc != null && loc.LegendColors.TryGetValue("Eikthyrnir", out var eik) && eik.b == 255 && LogHandler.Has("Eikthyrnir is listed more than once"),
      "a name listed twice keeps its last colour, with a warning (the whole legend fell back to the defaults before)");
    C(loc != null && !loc.LegendColors.ContainsKey("Vendor_BlackForest") && LogHandler.Has("invalid colour 1,2,300 for Vendor_BlackForest") && loc.LegendColors.Count == 2,
      "a bad colour skips its entry only; the # line is a comment");
    C(LogHandler.Has("\"Crypt2 FF00FF\" is not \"name: colour\""), "a line without its colon is reported, not dropped without a word");

    Section("a world's biome bytes without Expand World Data");
    LogHandler.Clear();
    // 3 x 3: None, Meadows, Mistlands, 0x400 (11), 0x80000000 (32), not a flag bit (33), 255, Ocean (9), 0x80 (8)
    var decoded = ImageMapBiome.Create(new byte[] { 0, 1, 10, 11, 32, 33, 255, 9, 8 })!;
    var biomes = decoded.Biomes;
    C(biomes[0] == Heightmap.Biome.None && biomes[1] == Heightmap.Biome.Meadows && biomes[2] == Heightmap.Biome.Mistlands && biomes[7] == Heightmap.Biome.Ocean,
      "vanilla bytes decode as before");
    C(biomes[3] == DeadWastes && biomes[4] == LastBit && biomes[8] == Bit7,
      "bytes of biomes only Expand World Data knows keep their biome in the map");
    C(biomes[5] == Heightmap.Biome.None && biomes[6] == Heightmap.Biome.None, "bytes that are no flag bit decode to None");
    C(!LogHandler.Has("[Warning]"), "decoding alone warns about nothing (the world load does)");
    C(decoded.Serialize().SequenceEqual(new byte[] { 0, 1, 10, 11, 32, 0, 0, 9, 8 }), "and a save writes them back: nothing is lost on a game without Expand World Data");
    // What the game reads: the map sampled at each pixel centre (Size 3, so pixel k sits at k / 2).
    Heightmap.Biome At(ImageMapBiome m, int px, int py) => m.GetValue(px / 2f, py / 2f);
    C(At(decoded, 0, 1) == Heightmap.Biome.None && At(decoded, 1, 1) == Heightmap.Biome.None && At(decoded, 2, 2) == Heightmap.Biome.None,
      "but the game reads them as None (the default generation decides there)");
    C(At(decoded, 1, 0) == Heightmap.Biome.Meadows && At(decoded, 2, 0) == Heightmap.Biome.Mistlands && At(decoded, 1, 2) == Heightmap.Biome.Ocean, "vanilla pixels read as before");
    LogHandler.Clear();
    BiomeRegistry.RefreshUsable();
    decoded.WarnUnusable();
    C(LogHandler.Has("does not have now") && LogHandler.Has("1024 (0x400)") && LogHandler.Has("-2147483648 (0x80000000)") && LogHandler.Has("128 (0x80)") && LogHandler.Has("Expand World Data"),
      "one warning at world load names them and points at Expand World Data");
    LogHandler.Clear();
    decoded.WarnUnusable();
    C(!LogHandler.Has("[Warning]"), "and it is not repeated for the same biomes");
    // Worlds saved by Better Continents 0.7.x with Expand World Data biomes (bits 7 and 10..30).
    var old = new byte[] { 8, 11, 12, 31, 1, 10, 9, 0, 0 };
    C(ImageMapBiome.Create(old)!.Serialize().SequenceEqual(old), "a 0.7.x world's bytes 8, 11, 12 and 31 survive a load and a save");
    var bits = Enumerable.Range(0, 32).Select(bit => (Heightmap.Biome)(int)(1u << bit)).ToArray();
    var every = ImageMapBiome.Create(Enumerable.Range(0, 36).Select(k => k < 33 ? (byte)k : (byte)0).ToArray())!;
    C(every.Biomes.Skip(1).Take(32).SequenceEqual(bits) && every.Serialize().Take(33).SequenceEqual(Enumerable.Range(0, 33).Select(k => (byte)k)),
      "every flag bit encodes as bit index + 1 and back (0x80000000 = 32)");
    var all = new[] { Heightmap.Biome.None, Heightmap.Biome.Meadows, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest,
      Heightmap.Biome.Plains, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean, Heightmap.Biome.Mistlands };
    var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 0, 0, 0, 0, 0, 0 };
    var round = ImageMapBiome.Create(bytes)!;
    C(round.Serialize().SequenceEqual(bytes), "the vanilla byte encoding is unchanged (bit index + 1)");
    C(round.Biomes.Take(10).SequenceEqual(all), "and decodes to the ten vanilla biomes");

    Section("world export without Expand World Data");
    var exportTable = ImageMapBiome.ExportColorTable();
    C(exportTable.Count == 10 && exportTable.All(kv => ImageMapBiome.DefaultColorTable()[kv.Key].Equals(kv.Value)), "the export colours are the default legend's");
    C(ImageMapBiome.LegendLines(exportTable).SequenceEqual(ImageMapBiome.DefaultColors.Split('|')), "and biomemap.txt is written exactly as before");
    var rawLegend = ImageMapBiome.ExportLegend(decoded.LegendColors, decoded.Biomes);
    C(rawLegend.Count == 13 && rawLegend[DeadWastes].r == 0x00 && rawLegend[DeadWastes].g == 0x44 && rawLegend[DeadWastes].b == 0x88,
      "exporting a map that holds biomes this game lacks still gives each a colour (0x400 in 004488), so a re-import with Expand World Data restores them");
    C(ImageMapBiome.LegendLines(rawLegend).Skip(10).SequenceEqual(["128: FF00FF", "1024: 004488", "-2147483648: 00FFAA"]),
      $"and names them by number ({string.Join(" | ", ImageMapBiome.LegendLines(rawLegend).Skip(10))})");

    Section("biome precision without Expand World Data");
    var grid = new BC.BiomePrecisionGrid(1, [Sector(Heightmap.Biome.Meadows), Sector(Heightmap.Biome.Swamp), Sector(Heightmap.Biome.Swamp), Sector(Heightmap.Biome.Swamp)]);
    C(grid.GetBiome(0.1f, 0.1f) == Heightmap.Biome.Meadows && grid.GetBiome(0.9f, 0.9f) == Heightmap.Biome.Swamp, "vanilla's corner rule is unchanged");
  }

  // ---------------------------------------------------------------------------------------------------------------
  // With Expand World Data 1.73.0's own patches and names.
  // ---------------------------------------------------------------------------------------------------------------
  // Patches first, checks after, in a method compiled only once the patches are in (see the header).
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void WithEwd(string work)
  {
    var ewd = PatchLikeExpandWorldData(work);
    EwdChecks(work, ewd);
  }

  static Type manager;
  static void SetNames(Dictionary<Heightmap.Biome, string> extra)
  {
    var names = new Dictionary<Heightmap.Biome, string>
    {
      [Heightmap.Biome.None] = "None", [Heightmap.Biome.Meadows] = "Meadows", [Heightmap.Biome.Swamp] = "Swamp",
      [Heightmap.Biome.Mountain] = "Mountain", [Heightmap.Biome.BlackForest] = "BlackForest", [Heightmap.Biome.Plains] = "Plains",
      [Heightmap.Biome.AshLands] = "AshLands", [Heightmap.Biome.DeepNorth] = "DeepNorth", [Heightmap.Biome.Ocean] = "Ocean",
      [Heightmap.Biome.Mistlands] = "Mistlands",
    };
    foreach (var kv in extra)
      names[kv.Key] = kv.Value;
    AccessTools.Method(manager, "SetNames").Invoke(null, [names]);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static Assembly PatchLikeExpandWorldData(string work)
  {
    Section("Expand World Data 1.73.0 loaded and patched");
    var ewd = Generation.LoadExpandWorldData(EwdDll);
    // The plugin's version is in its BepInPlugin attribute (the assembly itself says 1.0.0.0).
    var plugin = CustomAttributeData.GetCustomAttributes(ewd.GetType("ExpandWorldData.EWD")).First(a => a.AttributeType.Name == "BepInPlugin");
    var version = plugin.ConstructorArguments[2].Value as string;
    C(version == "1.73", $"ExpandWorldData.dll is Expand World Data 1.73 (got {version})");
    // BiomeManager's static fields read BepInEx's config path (Yaml.BaseDirectory); its log is BepInEx's.
    typeof(BepInEx.Paths).GetProperty(nameof(BepInEx.Paths.ConfigPath))!.GetSetMethod(true)!.Invoke(null, [work]);
    AccessTools.Method(ewd.GetType("Service.Log"), "Init").Invoke(null, [new ManualLogSource("Expand World Data")]);
    var harmony = new Harmony("ewd-test");
    // Its own patch classes where their targets resolve the same on .NET 8 as in the game's Mono ...
    foreach (var name in new[] { "ExpandWorldData.ToBiomeIndex", "ExpandWorldData.ToBiome", "ExpandWorldData.EnumParse", "ExpandWorldData.ParseIgnoreCase" })
      harmony.CreateClassProcessor(ewd.GetType(name)).Patch();
    // ... and its prefixes on the .NET 8 overloads where the target is found by position or by name alone (in Mono,
    // Enum has no generic GetName and its first two TryParse methods are the generic ones).
    var tryParse = typeof(Enum).GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Where(m => m.Name == nameof(Enum.TryParse) && m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType == typeof(string))
      .Where(m => m.GetParameters().Length == 2 || m.GetParameters()[1].ParameterType == typeof(bool))
      .Select(m => m.MakeGenericMethod(typeof(Heightmap.Biome))).ToList();
    foreach (var target in tryParse)
      harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(ewd.GetType("ExpandWorldData.TryParseBiome"), "Prefix")));
    harmony.Patch(typeof(Enum).GetMethod(nameof(Enum.GetName), [typeof(Type), typeof(object)]),
      prefix: new HarmonyMethod(AccessTools.Method(ewd.GetType("ExpandWorldData.GetName"), "Prefix")));
    // GetValues: the biomes the game's alt-biome grid (AltBiomeWorldData) makes room for, so the ones a map may use.
    harmony.Patch(typeof(Enum).GetMethod(nameof(Enum.GetValues), [typeof(Type)]),
      prefix: new HarmonyMethod(AccessTools.Method(ewd.GetType("ExpandWorldData.GetValues"), "Prefix")));
    C(tryParse.Count == 2, $"both generic Enum.TryParse(string) overloads patched (got {tryParse.Count})");

    manager = ewd.GetType("ExpandWorldData.BiomeManager");
    // What its expand_biomes yaml gives: the first added biome is 0x400, the next 0x800; the last one it can
    // allocate before 0x80 is 0x80000000.
    SetNames(new() { [DeadWastes] = "DeadWastes", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit" });
    return ewd;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static void EwdChecks(string work, Assembly ewd)
  {
    C(((Heightmap.BiomeIndex)11).ToBiome() == DeadWastes && DeadWastes.ToIndex() == 11 && Bit7.ToIndex() == 10 && LastBit.ToIndex() == 32,
      "the game's conversions now index 0x400 at 11, 0x80 at 10, 0x80000000 at 32 (Expand World Data's table)");
    C(Enum.GetName(typeof(Heightmap.Biome), DeadWastes) == "DeadWastes", "Enum.GetName names 0x400 DeadWastes");
    var viaEnum = Enum.TryParse<Heightmap.Biome>("DeadWastes", true, out var parsed) && parsed == DeadWastes;
    System.Console.WriteLine($"  INFO Enum.TryParse(\"DeadWastes\") from this call site: {(viaEnum ? "patched (0x400)" : "not patched")}");

    Section("Better Continents binds Expand World Data's names");
    EWD.BindBiomeNames(ewd);
    C(EWD.TryGetBiome("deadwastes", out var b1) && b1 == DeadWastes, "EWD.TryGetBiome(\"deadwastes\") = 0x400 (any case)");
    C(EWD.TryGetName(AshenMarsh, out var n1) && n1 == "AshenMarsh", "EWD.TryGetName(0x800) = AshenMarsh");
    C(!EWD.TryGetBiome("Nowhere", out _) && !EWD.TryGetName(Unnamed, out _), "unknown names and biomes are not found");

    Section("minimap heights: Expand World Data's per-biome map height (its transpiler skips Better Continents' drawing)");
    var dataType = ewd.GetType("ExpandWorldData.BiomeData");
    var data = RuntimeHelpers.GetUninitializedObject(dataType);
    dataType.GetField("mapColorMultiplier").SetValue(data, 2f);
    var biomeData = (System.Collections.IDictionary)manager.GetField("BiomeData", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    biomeData[DeadWastes] = data;
    C(EWD.MinimapHeight(50f, DeadWastes) == 70f && EWD.MinimapHeight(20f, DeadWastes) == 20f && EWD.MinimapHeight(50f, Heightmap.Biome.Meadows) == 50f,
      $"a biome with mapColorMultiplier 2 draws 50 m as 70 m (30 m water level), under water and other biomes unchanged (got {EWD.MinimapHeight(50f, DeadWastes)})");
    biomeData.Remove(DeadWastes);

    Section("biome table with Expand World Data");
    var vanillaSectors = BC.PlainSectors.ToArray();
    BiomeRegistry.RefreshTable();
    C(BiomeRegistry.IndexCount == 33, $"33 biome indices (got {BiomeRegistry.IndexCount})");
    C((uint)BiomeRegistry.Extra == 0xFFFFFC80u, $"every other flag bit is a biome the game can use (got 0x{(uint)BiomeRegistry.Extra:X})");
    C(LogHandler.Has("Biome maps accept 23 biomes beyond vanilla"), "the startup line reports them");
    C(BiomeRegistry.IsValid(DeadWastes) && BiomeRegistry.IsValid(Unnamed) && BiomeRegistry.IsValid(LastBit) && BiomeRegistry.IsValid(Bit7),
      "0x400, an unnamed 0x1000, 0x80000000 and 0x80 are all usable (the game indexes every bit)");
    C(!BiomeRegistry.IsValid(Heightmap.Biome.All) && !BiomeRegistry.IsValid(DeadWastes | Heightmap.Biome.Meadows), "combinations still are not");
    C(BiomeRegistry.ToSafeIndex(DeadWastes) == 11 && BiomeRegistry.ToSafeIndex(LastBit) == 32 && BiomeRegistry.ToSafeIndex(Heightmap.Biome.All) == 0,
      "ToSafeIndex: 0x400 -> 11, 0x80000000 -> 32, All -> 0");
    C(Enumerable.Range(0, 33).All(i => i == 0 || BiomeRegistry.ToSafeBiome(i) != Heightmap.Biome.None) && BiomeRegistry.ToSafeBiome(33) == Heightmap.Biome.None,
      "ToSafeBiome covers indices 1..32");
    C(BC.PlainSectors.Length == 33 && BC.PlainSectors[11].Biome == DeadWastes && BC.PlainSectors[10].Biome == Bit7 && BC.PlainSectors[32].Biome == LastBit,
      "33 plain sectors, Expand World Data's biomes included");
    C(Enumerable.Range(0, 10).All(i => ReferenceEquals(BC.PlainSectors[i], vanillaSectors[i])), "the vanilla plain sectors are kept, not replaced");
    C(BC.AltBiomeControl.PlainSector(DeadWastes).Biome == DeadWastes && BC.AltBiomeControl.PlainSector(Heightmap.Biome.All).Biome == Heightmap.Biome.None,
      "PlainSector: 0x400 -> its own sector, a combination -> None's");

    Section("legend names with Expand World Data");
    ExpectParse("DeadWastes", DeadWastes);
    ExpectParse("DEADWASTES", DeadWastes);
    ExpectParse("AshenMarsh", AshenMarsh);
    ExpectParse("Dead Wastes", DeadWastes);
    ExpectParse("1024", DeadWastes);
    ExpectParse("4096", Unnamed);
    ExpectParse("-2147483648", LastBit);
    ExpectParse("Meadows", Heightmap.Biome.Meadows);
    ExpectParse("None", Heightmap.Biome.None);
    ExpectNoParse("All");
    ExpectNoParse("Nowhere");
    ExpectNoParse("1025");
    C(BiomeRegistry.Name(DeadWastes) == "DeadWastes" && BiomeRegistry.Name(Unnamed) == "4096" && BiomeRegistry.Name(Heightmap.Biome.Ocean) == "Ocean",
      $"BiomeName: 0x400 -> DeadWastes, unnamed 0x1000 -> 4096, Ocean -> Ocean");

    Section("biomes the world can use now (the alt-biome grid's)");
    C(Enum.GetValues(typeof(Heightmap.Biome)).Cast<Heightmap.Biome>().Contains(DeadWastes) && !Enum.GetValues(typeof(Heightmap.Biome)).Cast<Heightmap.Biome>().Contains(Unnamed),
      "Expand World Data's Enum.GetValues lists DeadWastes, not the unnamed 0x1000");
    C(BiomeRegistry.IsUsable(DeadWastes) && BiomeRegistry.IsUsable(LastBit) && !BiomeRegistry.IsUsable(Unnamed) && BiomeRegistry.IsUsable(Heightmap.Biome.Plains),
      "usable now: the named biomes and vanilla's, not 0x1000");
    var unnamed = ImageMapBiome.Create(new byte[] { 13, 11, 1, 13 })!;
    C(unnamed.Biomes[0] == Unnamed && unnamed.GetValue(0f, 0f) == Heightmap.Biome.None && unnamed.GetValue(1f, 0f) == DeadWastes,
      "a map pixel of the unnamed 0x1000 reads as None (the grid has no room for it); DeadWastes reads as DeadWastes");
    LogHandler.Clear();
    unnamed.WarnUnusable();
    C(LogHandler.Has("4096 (0x1000)") && !LogHandler.Has("DeadWastes (0x400)"), "the world-load warning names 0x1000 only");
    SetNames(new() { [DeadWastes] = "DeadWastes", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit", [Unnamed] = "Frost" });
    BiomeRegistry.RefreshUsable();
    C(unnamed.GetValue(0f, 0f) == Unnamed, "once Expand World Data names 0x1000 (its yaml changed back), the map's pixels are that biome again: nothing was lost");
    SetNames(new() { [DeadWastes] = "DeadWastes", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit" });
    BiomeRegistry.RefreshUsable();

    Section("the tester's map: a DeadWastes region in biomemap.png, DeadWastes in biomemap.txt");
    var path = WriteMap(work, "biomemap", [
      ("Meadows", new Rgba32(0x00, 0xFF, 0x00)), ("DeadWastes", new Rgba32(0x8B, 0x45, 0x13)),
      ("Mistlands", new Rgba32(0x7F, 0x7F, 0x7F)), ("Ocean", new Rgba32(0x00, 0x00, 0xFF))]);
    LogHandler.Clear();
    var map = ImageMapBiome.Create(path);
    C(map != null && !LogHandler.Has("[Error]"), "the map and its legend load without an error");
    C(map != null && map.LegendColors.ContainsKey(DeadWastes) && map.LegendColors.Count == 4, "the legend holds DeadWastes and the other three");
    C(map != null && map.GetValue(Q(1), 0.5f) == DeadWastes, $"the DeadWastes region is DeadWastes (0x400), not Mistlands (got {(map == null ? "-" : map.GetValue(Q(1), 0.5f).ToString())})");
    C(map != null && map.GetValue(Q(2), 0.5f) == Heightmap.Biome.Mistlands && map.GetValue(Q(0), 0.5f) == Heightmap.Biome.Meadows, "Mistlands and Meadows regions are unchanged");

    Section("a world's settings: bytes, save and reload");
    var saved = map!.Serialize();
    C(saved.Contains((byte)11) && saved.All(v => v is 1 or 10 or 11 or 9), "DeadWastes is stored as byte 11 (bit 10 + 1)");
    LogHandler.Clear();
    var reloaded = ImageMapBiome.Create(saved)!;
    C(reloaded.Biomes.SequenceEqual(map.Biomes) && !LogHandler.Has("[Warning]"), "the stored map reads back identically, without a warning");
    var edge = ImageMapBiome.Create(new byte[] { 32, 11, 12, 13, 8, 0, 0, 0, 0 })!;
    C(edge.Biomes[0] == LastBit && edge.Biomes[1] == DeadWastes && edge.Biomes[2] == AshenMarsh && edge.Biomes[3] == Unnamed && edge.Biomes[4] == Bit7,
      "bytes 32, 11, 12, 13 and 8 read as 0x80000000, 0x400, 0x800, 0x1000 and 0x80");
    C(edge.Serialize().SequenceEqual(new byte[] { 32, 11, 12, 13, 8, 0, 0, 0, 0 }), "and write back the same bytes");

    Section("the server sends the map with its legend (SerializeLegacy / LoadLegacy)");
    var pkg = new ZPackage();
    map.SerializeLegacy(pkg, 11, network: true);
    pkg.SetPos(0);
    // A client that has not received Expand World Data's names yet: the legend travels with numbers for its biomes.
    SetNames(new());
    LogHandler.Clear();
    var received = ImageMapBiome.LoadLegacy(pkg, 11);
    C(received != null && !LogHandler.Has("[Error]"), "the client reads the legend without an error, before Expand World Data's names arrive");
    C(received != null && received.Biomes.SequenceEqual(map.Biomes), "and decodes exactly the server's biomes, DeadWastes included");
    SetNames(new() { [DeadWastes] = "DeadWastes", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit" });

    Section("world export with Expand World Data");
    var table = ImageMapBiome.ExportColorTable();
    C(table.Count == 33, $"a colour for all 33 biomes (got {table.Count})");
    C(table.Values.Select(c => (c.r, c.g, c.b)).Distinct().Count() == 33, "every biome's colour differs from every other");
    C(Enumerable.Range(0, 10).All(i => table[((Heightmap.BiomeIndex)i).ToBiome()].Equals(ImageMapBiome.DefaultColorTable()[((Heightmap.BiomeIndex)i).ToBiome()])),
      "vanilla's biomes keep the default colours");
    var worldLegend = table.Where(kv => BiomeRegistry.IsVanilla(kv.Key) || kv.Key == DeadWastes).ToDictionary(kv => kv.Key, kv => kv.Value);
    var lines = ImageMapBiome.LegendLines(worldLegend).ToList();
    C(lines.Take(10).SequenceEqual(ImageMapBiome.DefaultColors.Split('|')) && lines.Count == 11 && lines[10] == "DeadWastes: 004488",
      $"biomemap.txt: the default lines, then \"DeadWastes: 004488\" (got \"{lines.LastOrDefault()}\")");
    // Export -> import: a map drawn in the export colours and read back with the exported legend.
    var mixed = new[] { Heightmap.Biome.Meadows, DeadWastes, AshenMarsh, Heightmap.Biome.Ocean };
    var exported = WriteMap(work, "exported", mixed.Select(b => (BiomeRegistry.Name(b), ToRgba(table[b]))).ToArray());
    File.WriteAllLines(Path.ChangeExtension(exported, ".txt"), ImageMapBiome.LegendLines(table.Where(kv => BiomeRegistry.IsVanilla(kv.Key) || mixed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)));
    LogHandler.Clear();
    var imported = ImageMapBiome.Create(exported);
    C(imported != null && Enumerable.Range(0, 4).All(i => imported.GetValue(Q(i), 0.5f) == mixed[i]) && !LogHandler.Has("[Error]"),
      "an exported map with added biomes imports back to the same biomes");
    // A world's map read back from its settings has no legend: the default one plus the added biomes it holds.
    var fromSettings = ImageMapBiome.Create(map.Serialize())!;
    var legend1 = ImageMapBiome.ExportLegend(fromSettings.LegendColors, fromSettings.Biomes);
    C(legend1.Count == 11 && legend1[DeadWastes].Equals(table[DeadWastes]), "a settings-only map exports DeadWastes in its export colour, not None's black");
    // The map maker's own legend: their DeadWastes colour is kept.
    var legend2 = ImageMapBiome.ExportLegend(map.LegendColors, map.Biomes);
    C(legend2[DeadWastes].r == 0x8B && legend2[DeadWastes].g == 0x45 && legend2[DeadWastes].b == 0x13, "the map maker's legend keeps their DeadWastes colour (8B4513)");
    // A legend already using DeadWastes' export colour for another biome: DeadWastes gets a free one.
    var taken = new Dictionary<Heightmap.Biome, Color32> { [Heightmap.Biome.Meadows] = table[DeadWastes], [Heightmap.Biome.Ocean] = new Color32(0, 0, 255, 255) };
    var legend3 = ImageMapBiome.ExportLegend(taken, [Heightmap.Biome.Meadows, DeadWastes, Heightmap.Biome.Ocean]);
    C(legend3.Values.Select(c => (c.r, c.g, c.b)).Distinct().Count() == legend3.Count, "a colour another entry already has is never reused");

    Section("biome precision with Expand World Data");
    var grid = new BC.BiomePrecisionGrid(1, [Sector(DeadWastes), Sector(DeadWastes), Sector(Heightmap.Biome.Meadows), Sector(DeadWastes)]);
    C(grid.GetBiome(0.5f, 0.5f) == DeadWastes, "three DeadWastes corners of four: DeadWastes (it was dropped from the vote before)");
    C(grid.GetBiome(0.05f, 0.95f) == Heightmap.Biome.Meadows, "next to the Meadows corner: Meadows");
    C((grid.Biomes & DeadWastes) != 0, "HaveBiome sees DeadWastes in the grid");
    var two = new BC.BiomePrecisionGrid(1, [Sector(AshenMarsh), Sector(DeadWastes), Sector(DeadWastes), Sector(AshenMarsh)]);
    C(two.GetBiome(0.1f, 0.1f) == AshenMarsh && two.GetBiome(0.9f, 0.1f) == DeadWastes, "two added biomes against each other: the nearer corner wins");
    var tie = new BC.BiomePrecisionGrid(1, [Sector(AshenMarsh), Sector(DeadWastes), Sector(DeadWastes), Sector(AshenMarsh)]);
    C(tie.GetBiome(0.5f, 0.5f) == DeadWastes, "an exact tie goes to the lower index, as in vanilla (0x400 = 11 before 0x800 = 12)");

    // World generation (Generation.cs): patched games only, after everything above.
    NameFollowing(work);
    Generation.Run(work, ewd);
  }

  // A world made since 0.10 saves its biome map's added biomes with their names (in the alt-biome blob, format 2) and reads them
  // back by name: Expand World Data numbers its biomes in its yaml's order, so after the yaml changes the same bit can be another
  // biome. The bytes the world saved never change; an older world reads the bits as it always has.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void NameFollowing(string work)
  {
    Section("a new world's biome map follows Expand World Data's biome names");
    var field = typeof(BC.BetterContinentsSettings).GetField("BiomeMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var bands = new[] { Heightmap.Biome.Meadows, DeadWastes, AshenMarsh, Heightmap.Biome.Ocean };
    var path = WriteMap(work, "named", [("Meadows", new Rgba32(0, 255, 0)), ("DeadWastes", new Rgba32(0x8B, 0x45, 0x13)), ("AshenMarsh", new Rgba32(0x55, 0x22, 0x77)), ("Ocean", new Rgba32(0, 0, 255))]);
    var map = ImageMapBiome.Create(path)!;
    BC.BetterContinentsSettings World(int version)
    {
      var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = version };
      field.SetValue(s, map);
      return s;
    }
    byte[] Save(BC.BetterContinentsSettings s, bool network = false)
    {
      var pkg = new ZPackage();
      s.Serialize(pkg, network, true, s.SavedVersion);
      return pkg.GetArray();
    }
    BC.BetterContinentsSettings Read(byte[] bytes) => BC.BetterContinentsSettings.Load(new ZPackage(bytes));
    string Band(BC.BetterContinentsSettings s, int i) => BiomeRegistry.Name(((ImageMapBiome)field.GetValue(s)!).GetValue(Q(i), 0.5f));
    void Names(Dictionary<Heightmap.Biome, string> extra)
    {
      SetNames(extra);
      BiomeRegistry.RefreshUsable();
    }

    var saved12 = Save(World(12));
    var saved11 = Save(World(11));
    var back = Read(saved12);
    C(back.AltBiomes == null && Band(back, 1) == "DeadWastes" && Band(back, 2) == "AshenMarsh" && Save(back).SequenceEqual(saved12),
      "a new world saves the names with its map and reads it back the same: no alt-biome options of its own (the blob only carries the names), the same bytes saved again");
    C(saved11.Length < saved12.Length && Read(saved11).AltBiomes == null, "an older world saves no names");

    // The yaml changes: DeadWastes and AshenMarsh swap their numbers.
    Names(new() { [AshenMarsh] = "DeadWastes", [DeadWastes] = "AshenMarsh", [LastBit] = "LastBit" });
    var moved = Read(saved12);
    C(Band(moved, 1) == "DeadWastes" && Band(moved, 2) == "AshenMarsh",
      $"after the yaml swaps their numbers, the new world's map still reads DeadWastes and AshenMarsh where they were (got {Band(moved, 1)}, {Band(moved, 2)})");
    C(Save(moved).SequenceEqual(saved12), "and saves the bytes and names it read, unchanged");
    var client = Read(Save(moved, network: true));
    C(Band(client, 1) == "DeadWastes" && Band(client, 2) == "AshenMarsh", "a client gets the map as the server reads it");
    var older = Read(saved11);
    C(Band(older, 1) == "AshenMarsh" && Band(older, 2) == "DeadWastes", "an older world reads the bits as it always has: the swap shows (as before 0.10)");

    // DeadWastes leaves the yaml and another biome takes its number.
    Names(new() { [DeadWastes] = "Frost", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit" });
    var gone = Read(saved12);
    C(Band(gone, 1) == "None" && Band(gone, 2) == "AshenMarsh", $"with no DeadWastes in the yaml, its ground reads as None, not as Frost, which has its number now (got {Band(gone, 1)})");
    C(Save(gone).SequenceEqual(saved12), "and the world keeps DeadWastes' bytes and name, for when the yaml has it again");
    Names(new() { [DeadWastes] = "DeadWastes", [AshenMarsh] = "AshenMarsh", [LastBit] = "LastBit" });
    C(Band(Read(saved12), 1) == "DeadWastes", "the yaml as it was: DeadWastes is back");

    // What an older build reads: the blob's format 1 part, unchanged.
    var legacy = BC.AltBiomeSettings.Legacy;
    var one = legacy.Serialize();
    var two = legacy.Serialize(new Dictionary<Heightmap.Biome, string> { [DeadWastes] = "DeadWastes" }, true);
    C(BitConverter.ToInt32(one, 0) == 1 && BitConverter.ToInt32(two, 0) == 2 && two.Skip(4).Take(one.Length - 4).SequenceEqual(one.Skip(4))
      && BC.AltBiomeSettings.Deserialize(two).Serialize().SequenceEqual(one),
      "the names come after format 1's fields, so a build that knows format 1 reads the same options (Better Continents 0.9 warns and keeps the blob)");
  }

  static BiomeSector Sector(Heightmap.Biome biome) => BC.AltBiomeControl.PlainSector(biome);
  static Rgba32 ToRgba(Color32 c) => new(c.r, c.g, c.b, c.a);

  static void ExpectParse(string name, Heightmap.Biome want) =>
    C(BiomeRegistry.TryParse(name, out var got) && got == want, $"\"{name}\" -> {(uint)want:X} (got {(BiomeRegistry.TryParse(name, out var g) ? $"{(uint)g:X}" : "no biome")})");
  static void ExpectNoParse(string name) =>
    C(!BiomeRegistry.TryParse(name, out _), $"\"{name}\" is not a biome here");

  // The x at the middle of column band i of the test map's four vertical bands.
  static float Q(int i) => (i + 0.5f) / 4f;

  // A 64 x 64 biome map of four vertical bands in the given colours (west to east), and its legend (biomemap.txt)
  // with one "Name: RRGGBB" line per band.
  internal static string WriteMap(string dir, string name, (string Name, Rgba32 Color)[] bands)
  {
    const int size = 64;
    using var img = new Image<Rgba32>(size, size);
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
        img[x, y] = bands[Math.Min(x * bands.Length / size, bands.Length - 1)].Color;
    var png = Path.Combine(dir, name + ".png");
    img.SaveAsPng(png);
    File.WriteAllLines(Path.Combine(dir, name + ".txt"), bands.Select(b => $"{b.Name}: {b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}"));
    return png;
  }
}

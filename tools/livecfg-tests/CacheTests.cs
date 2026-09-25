// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

// Offline checks of Better Continents 0.9.0's shareable world cache (WorldCacheShare, and the accessors it uses on
// BetterContinents.ZNetPatch.WorldCache): a .bcworld imports into the real on-disk cache under its recomputed id
// with byte-identical content, a corrupt/short file is refused without throwing, the plugin-tree seed scan imports
// once and reports "already present" the second time, and export writes both files. Loads the real pre-ILRepack
// BetterContinents.dll, the game's assemblies and BepInEx (see Program.cs, which this shares its process with).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SixLabors.ImageSharp.PixelFormats;
using BetterContinents;
using BC = BetterContinents.BetterContinents;
using WorldCache = BetterContinents.BetterContinents.ZNetPatch.WorldCache;

namespace LiveCfgTest;

internal static class CacheTests
{
  static int checks, failures;

  static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }

  static void Section(string s) => System.Console.WriteLine("== " + s);

  // Called once from Program.Run(); keeps its own tally and returns just the failure count, so the runner's own
  // totals need only "failures += CacheTests.Run();" - no shared state with Program itself.
  public static int Run()
  {
    checks = 0;
    failures = 0;
    var work = Path.Combine(Path.GetTempPath(), "bc-cache-test-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    // Every sub-test sets its own WorldCachePath override before reading it, so nothing here needs to save and
    // restore the real default: computing that default calls Utils.GetSaveDataPath, which needs a running game and
    // throws in this offline harness - fine, since nothing later in this process (nor in Program.cs's own tests,
    // which run before this and never touch WorldCache) depends on it going back to a real value.
    try
    {
      ImportRoundTrip(work);
      CorruptFileRefused(work);
      SeedScan(work);
      ExportWritesBothFiles(work);
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
    System.Console.WriteLine($"CacheTests: {checks} checks, {failures} failures");
    return failures;
  }

  // A small real settings object with a tiny heightmap, serialized in network form - the same bytes a .bcworld
  // holds, and what WorldCache.Add stores under cache/<id>.bc.
  static byte[] NewNetworkPackage()
  {
    var path = Path.GetTempFileName();
    byte[] pngBytes;
    try
    {
      WorldExportPng.SaveL16(path, Enumerable.Repeat(new L16(12345), 4).ToArray(), 2);
      pngBytes = File.ReadAllBytes(path);
    }
    finally { File.Delete(path); }

    var map = ImageMapFloat.Create(pngBytes, false);
    if (map == null)
      throw new InvalidOperationException("test setup: the tiny heightmap PNG did not decode");
    var settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
    typeof(BC.BetterContinentsSettings)
      .GetField("HeightMap", BindingFlags.NonPublic | BindingFlags.Instance)!
      .SetValue(settings, map);

    // formatVersion is explicit: Serialize's own default reads ConfigOverrideVersion, a config entry this minimal
    // harness never binds (Program.cs's Bind() only binds the Export group), and it would throw on a null
    // ConfigEntry. Passing MaxVersion reproduces the real game's own default (ConfigOverrideVersion is "" there
    // too, so int.TryParse fails and it falls back to MaxVersion) without needing that entry bound at all.
    var pkg = new ZPackage();
    settings.Serialize(pkg, network: true, formatVersion: BC.BetterContinentsSettings.MaxVersion);
    return pkg.GetArray();
  }

  static void ImportRoundTrip(string work)
  {
    Section("import: id recomputed from content, bytes stored identically, then a second import is a no-op");
    WorldCache.WorldCachePath = Path.Combine(work, "cache1");
    var bytes = NewNetworkPackage();
    var expectedId = WorldCache.PackageID(new ZPackage(bytes));

    // The source file's own name is deliberately wrong: the id must come from the content, never the name.
    var srcDir = Path.Combine(work, "share1");
    Directory.CreateDirectory(srcDir);
    var bcworldPath = Path.Combine(srcDir, "Renamed-does-not-matter" + WorldCacheShare.Extension);
    File.WriteAllBytes(bcworldPath, bytes);

    var outcomes = WorldCacheShare.ImportPath(bcworldPath, out var error);
    C(error == null, $"import: no top-level error ({error})");
    C(outcomes.Count == 1 && outcomes[0].Imported && !outcomes[0].AlreadyPresent, "import: one file, imported, not already present");
    C(outcomes[0].Id == expectedId, $"import: reported id is the recomputed one ({outcomes[0].Id} vs {expectedId})");

    var storedPath = Path.Combine(WorldCache.WorldCachePath, expectedId + ".bc");
    C(File.Exists(storedPath), $"import: stored under the recomputed id regardless of the source file's name ({storedPath})");
    C(File.ReadAllBytes(storedPath).SequenceEqual(bytes), "import: stored bytes are identical to the .bcworld's bytes");

    // Same round trip a real handshake gets for free: SerializeCacheList's ZPackage is left positioned at the end
    // (just written), so re-reading it needs the same bytes -> new ZPackage that ZRpc's own wire transfer does.
    var cacheList = new ZPackage(WorldCache.SerializeCacheList().GetArray());
    C(WorldCache.CacheItemExists(expectedId, cacheList), "CacheItemExists(id, cacheList) is true for a cache list containing the id");

    var again = WorldCacheShare.ImportPath(bcworldPath, out var error2);
    C(error2 == null && again.Count == 1 && !again[0].Imported && again[0].AlreadyPresent, "importing the same id again reports 'already present', not a second write");
  }

  static void CorruptFileRefused(string work)
  {
    Section("import: a corrupt/short file is refused, never thrown");
    WorldCache.WorldCachePath = Path.Combine(work, "cache2");

    var tooShort = Path.Combine(work, "tooshort" + WorldCacheShare.Extension);
    File.WriteAllBytes(tooShort, new byte[] { 1, 2 });
    var r1 = WorldCacheShare.ImportPath(tooShort, out var e1);
    C(e1 == null && r1.Count == 1 && !r1[0].Imported && r1[0].Error != null, $"a 2-byte file is refused, not thrown ({r1.FirstOrDefault()?.Error})");

    var badHeader = Path.Combine(work, "badheader" + WorldCacheShare.Extension);
    File.WriteAllBytes(badHeader, BitConverter.GetBytes(999999).Concat(new byte[] { 9, 9, 9, 9 }).ToArray());
    var r2 = WorldCacheShare.ImportPath(badHeader, out var e2);
    C(e2 == null && r2.Count == 1 && !r2[0].Imported && r2[0].Error != null, $"an out-of-range version header is refused, not thrown ({r2.FirstOrDefault()?.Error})");

    C(!Directory.Exists(WorldCache.WorldCachePath) || Directory.GetFiles(WorldCache.WorldCachePath).Length == 0, "neither refused file reached the cache directory");
  }

  static void SeedScan(string work)
  {
    Section("seed scan over a temp plugins tree: imports once, then reports already present");
    WorldCache.WorldCachePath = Path.Combine(work, "cache3");

    var pluginsRoot = Path.Combine(work, "plugins");
    var nested = Path.Combine(pluginsRoot, "SomePack", "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllBytes(Path.Combine(nested, "ProximaMaxi-seed" + WorldCacheShare.Extension), NewNetworkPackage());

    var first = WorldCacheShare.SeedRoot(pluginsRoot);
    C(first.Imported == 1 && first.AlreadyPresent == 0, $"first scan: imported once ({first.Imported} imported, {first.AlreadyPresent} present)");
    var second = WorldCacheShare.SeedRoot(pluginsRoot);
    C(second.Imported == 0 && second.AlreadyPresent == 1, $"second scan: already present, not re-imported ({second.Imported} imported, {second.AlreadyPresent} present)");

    var missing = WorldCacheShare.SeedRoot(Path.Combine(work, "does-not-exist"));
    C(missing.Imported == 0 && missing.AlreadyPresent == 0, "a missing folder scans as zero, not an exception");
  }

  static void ExportWritesBothFiles(string work)
  {
    Section("export writes the .bcworld and its .txt sidecar");
    var bytes = NewNetworkPackage();
    var outDir = Path.Combine(work, "export-out");
    var result = WorldCacheShare.WriteExportFiles(bytes, "Test World", "myseed123", outDir);

    C(File.Exists(result.FilePath) && result.FilePath.EndsWith(WorldCacheShare.Extension, StringComparison.Ordinal), $"export writes the .bcworld ({result.FilePath})");
    C(File.Exists(result.SidecarPath) && result.SidecarPath.EndsWith(".txt", StringComparison.Ordinal), $"export writes the .txt sidecar ({result.SidecarPath})");
    C(File.ReadAllBytes(result.FilePath).SequenceEqual(bytes), "the .bcworld's bytes are exactly the package bytes given to it");
    C(result.Id == WorldCache.PackageID(new ZPackage(bytes)), "the reported id is the package's recomputed id");

    var text = File.ReadAllText(result.SidecarPath);
    C(text.Contains("Test World") && text.Contains("myseed123") && text.Contains(result.Id) && text.Contains(ModInfo.Version),
      "the sidecar names the world, seed, id and Better Continents version");
  }
}

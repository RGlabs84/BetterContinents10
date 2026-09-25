// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

// 0.9.0: a shareable form of the world cache (BetterContinents.ZNetPatch.WorldCache). Joining a Better Continents
// world streams its settings package to every new client once per world revision (11.8 MB for a 2048 px world);
// a ".bcworld" file lets a modpack, or a server admin, pre-seed that client-side cache so joining skips the
// download. The player- and admin-facing walkthrough is chapter 9 of the Export & Import guide and the README's
// "Sharing Maps With Players"; "bc_cache ..." (registered in DebugUtils.Export.cs) is the console front end for
// Export/ImportPath/ListEntries below.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static BetterContinents.BetterContinents;
using BC = BetterContinents.BetterContinents;
using WorldCache = BetterContinents.BetterContinents.ZNetPatch.WorldCache;

namespace BetterContinents;

public static class WorldCacheShare
{
  public const string Extension = ".bcworld";

  // A file this large could not be a settings package (even an 8192 world's is nowhere near this), so it is not
  // worth reading fully just to refuse it - protects the startup scan against a huge, wrongly-named file.
  private const long MaxReasonableSize = 512L * 1024 * 1024;

  public sealed class ExportResult
  {
    public readonly string FilePath;
    public readonly string SidecarPath;
    public readonly string Id;
    public readonly long Size;
    internal ExportResult(string filePath, string sidecarPath, string id, long size)
    {
      FilePath = filePath;
      SidecarPath = sidecarPath;
      Id = id;
      Size = size;
    }
  }

  public sealed class ImportOutcome
  {
    public readonly string FilePath;
    public readonly bool Imported;
    public readonly bool AlreadyPresent;
    public readonly string? Id;
    public readonly string? Error;
    internal ImportOutcome(string filePath, bool imported, bool alreadyPresent, string? id, string? error)
    {
      FilePath = filePath;
      Imported = imported;
      AlreadyPresent = alreadyPresent;
      Id = id;
      Error = error;
    }

    // One line for "bc_cache import"'s per-file result, and for the seed scan's log.
    public string Describe()
    {
      var name = Path.GetFileName(FilePath);
      if (Error != null)
        return $"{name}: refused ({Error})";
      return AlreadyPresent ? $"{name}: already present (id {Id})" : $"{name}: imported (id {Id})";
    }
  }

  public sealed class CacheEntry
  {
    public readonly string Id;
    public readonly long Size;
    public readonly DateTime Modified;
    internal CacheEntry(string id, long size, DateTime modified)
    {
      Id = id;
      Size = size;
      Modified = modified;
    }
  }

  // ---- export --------------------------------------------------------------------------------------------------

  // On the host or dedicated server: freshly serializes the loaded world's settings, the same way SendSettings
  // does, so the id this writes is exactly the id a joining client's cache check looks for. On a client: exports
  // its own cached entry for the world it is currently in, if the handshake or a download has told it which one
  // that is (ZNetPatch.WorldCache.CurrentId). Returns null and sets error on failure.
  public static ExportResult? Export(out string? error)
  {
    error = null;
    var net = ZNet.instance;
    if (net == null)
    {
      error = "not connected to a world";
      return null;
    }

    byte[] bytes;
    if (net.IsServer())
    {
      if (!BC.Settings.EnabledForThisWorld)
      {
        error = "this world does not use Better Continents";
        return null;
      }
      var pkg = new ZPackage();
      BC.Settings.Serialize(pkg, true);
      bytes = pkg.GetArray();
    }
    else
    {
      var id = WorldCache.CurrentId;
      if (id == null)
      {
        error = "no cached settings for the current world yet (it may still be downloading, or this world may not use Better Continents)";
        return null;
      }
      if (!WorldCache.CacheItemExists(id))
      {
        error = $"cache entry {id} for the current world is missing on disk";
        return null;
      }
      bytes = WorldCache.LoadCacheItem(id).GetArray();
    }

    var worldName = WorldGenerator.instance?.m_world?.m_name ?? ZNet.World?.m_name ?? "world";
    var seedName = WorldGenerator.instance?.m_world?.m_seedName ?? ZNet.World?.m_seedName ?? "";
    try
    {
      var dir = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", SafeFileName(worldName));
      return WriteExportFiles(bytes, worldName, seedName, dir);
    }
    catch (Exception e)
    {
      error = $"could not write the cache file: {e.Message}";
      return null;
    }
  }

  // The file-writing half of an export, independent of ZNet/WorldGenerator so the offline tests can call it
  // directly. Writes "<world name sanitised>-<id>.bcworld" (the raw bytes, byte-identical to cache/<id>.bc) plus
  // a "<same>.txt" sidecar, into outputDir (created if missing).
  internal static ExportResult WriteExportFiles(byte[] bytes, string worldName, string seedName, string outputDir)
  {
    var id = WorldCache.PackageID(new ZPackage(bytes));
    var baseName = $"{SafeFileName(worldName)}-{id}";
    Directory.CreateDirectory(outputDir);
    var filePath = Path.Combine(outputDir, baseName + Extension);
    var sidecarPath = Path.Combine(outputDir, baseName + ".txt");
    File.WriteAllBytes(filePath, bytes);
    File.WriteAllText(sidecarPath, Sidecar(worldName, seedName, id, bytes.LongLength));
    return new ExportResult(filePath, sidecarPath, id, bytes.LongLength);
  }

  private static string Sidecar(string worldName, string seedName, string id, long size) => string.Join("\n", new[]
  {
    $"Better Continents world cache: {worldName}",
    $"Seed: {seedName}",
    $"Better Continents version: {ModInfo.Version}",
    $"Id: {id}",
    $"Size: {size} bytes",
    $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    "",
    $"Player: before you join \"{worldName}\", drop this file into BepInEx/config/BetterContinents/seed/ (it imports automatically) or run 'bc_cache import <path to this file>' once - either way, you skip the settings download.",
  }) + "\n";

  // Mirrors WorldExport.cs's own SafeFileName (private there): replace characters a file name cannot hold.
  // Kept as a small local copy rather than exposing that one, to keep this file's footprint self-contained.
  private static string SafeFileName(string name)
  {
    var bad = Path.GetInvalidFileNameChars();
    var chars = name.Select(c => bad.Contains(c) ? '_' : c).ToArray();
    var s = new string(chars).Trim();
    return s.Length == 0 ? "world" : s;
  }

  // ---- import --------------------------------------------------------------------------------------------------

  // The cheapest check that these bytes could be a settings package: a settings package's very first write is its
  // format version (Serialize.cs's Serialize/Deserialize), a plain 4-byte int, either -1 (disabled) or
  // 1..MaxVersion. This never decodes any image or other content.
  internal static bool LooksLikeSettingsPackageHeader(byte[] header)
  {
    int version = BitConverter.ToInt32(header, 0);
    return version == -1 || (version >= 1 && version <= BetterContinentsSettings.MaxVersion);
  }

  // Cheap: a size check, then only the first 4 bytes, before ever reading a whole (possibly large) file.
  private static bool TryLoad(string file, out byte[] bytes, out string? error)
  {
    bytes = Array.Empty<byte>();
    error = null;
    FileInfo info;
    try
    {
      info = new FileInfo(file);
    }
    catch (Exception e)
    {
      error = $"cannot read: {e.Message}";
      return false;
    }
    if (!info.Exists)
    {
      error = "file not found";
      return false;
    }
    if (info.Length < 4)
    {
      error = "too small to be a settings package";
      return false;
    }
    if (info.Length > MaxReasonableSize)
    {
      error = $"too large to be a settings package ({info.Length} bytes)";
      return false;
    }
    try
    {
      using var stream = File.OpenRead(file);
      var header = new byte[4];
      if (stream.Read(header, 0, 4) != 4 || !LooksLikeSettingsPackageHeader(header))
      {
        error = "not a Better Continents settings package (bad header)";
        return false;
      }
      stream.Position = 0;
      var buffer = new byte[info.Length];
      int offset = 0, read;
      while (offset < buffer.Length && (read = stream.Read(buffer, offset, buffer.Length - offset)) > 0)
        offset += read;
      if (offset != buffer.Length)
      {
        error = "could not read the whole file";
        return false;
      }
      bytes = buffer;
      return true;
    }
    catch (Exception e)
    {
      error = $"cannot read: {e.Message}";
      return false;
    }
  }

  private static ImportOutcome ImportOne(string file)
  {
    if (!TryLoad(file, out var bytes, out var loadError))
      return new ImportOutcome(file, false, false, null, loadError);

    string id;
    try
    {
      id = WorldCache.PackageID(new ZPackage(bytes));
    }
    catch (Exception e)
    {
      return new ImportOutcome(file, false, false, null, $"cannot read as a settings package: {e.Message}");
    }

    if (WorldCache.CacheItemExists(id))
      return new ImportOutcome(file, false, true, id, null);

    try
    {
      WorldCache.Add(new ZPackage(bytes));
    }
    catch (Exception e)
    {
      return new ImportOutcome(file, false, false, id, $"could not write to the cache: {e.Message}");
    }
    return new ImportOutcome(file, true, false, id, null);
  }

  private static IEnumerable<string> FindBcworldFiles(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .Where(f => f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase));

  // "bc_cache import <file or folder>": a folder is searched recursively for *.bcworld (case-insensitively, since
  // a search pattern's case sensitivity differs between Windows and Linux); a file is tried as given, whatever its
  // name. Every id already on disk is skipped, never overwritten; a bad file is one refused result line, not an
  // exception. Never throws.
  public static List<ImportOutcome> ImportPath(string fileOrFolder, out string? error)
  {
    error = null;
    var results = new List<ImportOutcome>();
    var text = (fileOrFolder ?? "").Trim().Trim('"');
    if (text.Length == 0)
    {
      error = "no file or folder given";
      return results;
    }
    string full;
    try
    {
      full = Path.GetFullPath(text);
    }
    catch (Exception e)
    {
      error = $"bad path: {e.Message}";
      return results;
    }

    if (Directory.Exists(full))
    {
      List<string> files;
      try
      {
        files = FindBcworldFiles(full).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
      }
      catch (Exception e)
      {
        error = $"cannot list {full}: {e.Message}";
        return results;
      }
      foreach (var file in files)
        results.Add(ImportOne(file));
      return results;
    }
    if (File.Exists(full))
    {
      results.Add(ImportOne(full));
      return results;
    }
    error = $"{full} does not exist";
    return results;
  }

  // ---- list ----------------------------------------------------------------------------------------------------

  public static List<CacheEntry> ListEntries()
  {
    var dir = WorldCache.WorldCachePath;
    if (!Directory.Exists(dir))
      return [];
    return Directory.GetFiles(dir, "*.bc")
      .Select(f => new FileInfo(f))
      .Select(fi => new CacheEntry(Path.GetFileNameWithoutExtension(fi.Name).ToLowerInvariant(), fi.Length, fi.LastWriteTime))
      .OrderByDescending(e => e.Modified)
      .ToList();
  }

  // ---- automatic pre-seeding ------------------------------------------------------------------------------------

  // <BepInEx config>/BetterContinents/seed - a folder just for this, so a "WorldCache-Something" Thunderstore
  // package can ship only .bcworld files with nothing else to conflict with.
  public static string SeedFolder => Path.Combine(BepInEx.Paths.ConfigPath, "BetterContinents", "seed");

  private static void Seed(string root, ref int imported, ref int present)
  {
    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
      return;
    List<string> files;
    try
    {
      files = FindBcworldFiles(root).ToList();
    }
    catch (Exception e)
    {
      LogWarning($"World cache seed: could not scan {root}: {e.Message}");
      return;
    }
    foreach (var file in files)
    {
      ImportOutcome outcome;
      try
      {
        outcome = ImportOne(file);
      }
      catch (Exception e)
      {
        LogWarning($"World cache seed: {Path.GetFileName(file)}: {e.Message}");
        continue;
      }
      if (outcome.Error != null)
        LogWarning($"World cache seed: {outcome.Describe()}");
      else if (outcome.AlreadyPresent)
        present++;
      else
        imported++;
    }
  }

  // Exposed for the offline tests, which have no BepInEx.Paths to point at: scans exactly one root and reports
  // what it did there, without touching the real plugin or config folders.
  internal static (int Imported, int AlreadyPresent) SeedRoot(string root)
  {
    int imported = 0, present = 0;
    Seed(root, ref imported, ref present);
    return (imported, present);
  }

  // Called once from BetterContinents.Awake (wrapped in try/catch there too): scans BepInEx's whole plugin tree
  // and the dedicated seed folder above for .bcworld files and imports any id not already cached. This is what
  // lets a modpack or server admin's Thunderstore/Hexium package - containing nothing but .bcworld files - pre-seed
  // every player's cache with zero action from them.
  public static void SeedFromDisk()
  {
    int imported = 0, present = 0;
    string? pluginPath = null;
    try { pluginPath = BepInEx.Paths.PluginPath; }
    catch (Exception e) { LogWarning($"World cache seed: could not read BepInEx.Paths.PluginPath: {e.Message}"); }
    if (!string.IsNullOrEmpty(pluginPath))
      Seed(pluginPath!, ref imported, ref present);

    string? seedFolder = null;
    try { seedFolder = SeedFolder; }
    catch (Exception e) { LogWarning($"World cache seed: could not read the seed folder path: {e.Message}"); }
    if (!string.IsNullOrEmpty(seedFolder))
      Seed(seedFolder!, ref imported, ref present);

    Log($"World cache seeded: {imported} file(s), {present} already present");
  }
}

// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BetterContinents;

// Where a world's lakes and clearing mask come from and go to as files (BUILD-SPEC-TARN.md 2.2, 2.6). Tarn's formats are kept byte for byte, so the files a
// map maker's pipeline wrote for Tarn (Ravenrest's) work unchanged:
//
//   lakes.txt        one lake a line, name;x;z;level;minX;minZ;maxX;maxZ;depth [T: Plugin.cs Lake.Parse]
//   clearmask.bin    Tarn's ".clear" file: TARNCLR1, int32 size, float32 half-extent, size x size bits [T: Clearing.cs]
//
// A new world takes them from its Directory like every other map file (BetterContinentsSettings.ReadLakeFiles, which also serves a world made by bc_import
// and one made from an export folder: all go through ReadConfig). The names the Ravenrest pipeline writes, tarn-lakes.txt and tarn-clear.bin, are taken when
// the standard name is missing. Tarn's own folder (BepInEx/config/Tarn/<world>.txt and .clear) is an import source only, never written to.
internal static class LakeFiles
{
  public const string LakesFileName = "lakes.txt";
  public const string ClearFileName = "clearmask.bin";
  public const string TarnLakesFileName = "tarn-lakes.txt";
  public const string TarnClearFileName = "tarn-clear.bin";

  private static readonly CultureInfo C = CultureInfo.InvariantCulture;

  // BepInEx's config folder (the stand-in of the offline tests).
  internal static Func<string> ConfigPath = () => BepInEx.Paths.ConfigPath;

  // What a Directory held.
  internal sealed class Loaded
  {
    public LakeSet? Lakes;
    public ClearMask? Clearing;
    public string? LakesPath, ClearingPath;
  }

  // ---- the Directory ----------------------------------------------------------------------------------------------------------------------------

  // The file of a Directory that holds the lakes: the standard name, else the Ravenrest pipeline's; null when there is none. Both: the standard one wins.
  internal static string? FindLakes(string dir, out bool both) => Find(dir, LakesFileName, TarnLakesFileName, out both);

  internal static string? FindClearing(string dir, out bool both) => Find(dir, ClearFileName, TarnClearFileName, out both);

  private static string? Find(string dir, string standard, string alias, out bool both)
  {
    var main = Path.Combine(dir, standard);
    var other = Path.Combine(dir, alias);
    bool hasMain = File.Exists(main), hasOther = File.Exists(other);
    both = hasMain && hasOther;
    return hasMain ? main : hasOther ? other : null;
  }

  // The lakes and the mask a new world takes from this Directory. A file that cannot be read is an error line naming the file and what is wrong, and the world
  // is made without it (the layer's rule); `bc_lake load` adds it later.
  internal static Loaded FromDirectory(string dir)
  {
    var loaded = new Loaded();
    if (FindLakes(dir, out bool bothLakes) is { } lakesPath)
    {
      if (bothLakes)
        BetterContinents.Log($"Lakes: {dir} holds {LakesFileName} and {TarnLakesFileName}: {LakesFileName} is used.");
      try
      {
        var set = ReadLakes(lakesPath);
        if (set.IsEmpty)
          BetterContinents.LogWarning($"Lakes: {lakesPath} holds no lake: the world is made without lakes.");
        else
        {
          loaded.Lakes = set;
          loaded.LakesPath = lakesPath;
          BetterContinents.Log($"Lakes: {lakesPath}: {set.Lakes.Count:N0} lakes");
        }
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
        BetterContinents.LogError($"The lakes {lakesPath} cannot be read ({e.Message}): the world is made without them. 'bc_lake load' adds lakes to a world later.");
      }
    }
    if (FindClearing(dir, out bool bothMasks) is { } clearPath)
    {
      if (bothMasks)
        BetterContinents.Log($"Clearing: {dir} holds {ClearFileName} and {TarnClearFileName}: {ClearFileName} is used.");
      try
      {
        var mask = ReadClearing(clearPath);
        loaded.Clearing = mask;
        loaded.ClearingPath = clearPath;
        BetterContinents.Log($"Clearing: {clearPath}: {mask.Summary()}");
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
      {
        BetterContinents.LogError($"The clearing mask {clearPath} cannot be read ({e.Message}): the world is made without it. 'bc_lake mask load' adds one to a world later.");
      }
    }
    return loaded;
  }

  // ---- reading and writing ------------------------------------------------------------------------------------------------------------------------

  // A lakes file read: a line that does not parse is a warning that names the file and the line; the numbers of a lake that is odd are warned about and kept. The
  // warnings go to the log and, when a list is given, into it as well (the dry run of a command shows them: what the parser left out is what an admin wants to see).
  internal static LakeSet ReadLakes(string path, List<string>? notes = null)
  {
    var lines = File.ReadAllLines(path);
    return LakeSet.ParseLines(lines, w =>
    {
      BetterContinents.LogWarning($"Lakes: {path}: {w}");
      notes?.Add($"{Path.GetFileName(path)}: {w}");
    });
  }

  // A mask file read. The 33 MB of a 16384 px mask are read once and the bits copied out of them.
  internal static ClearMask ReadClearing(string path) => ClearMask.FromTarnFile(File.ReadAllBytes(path));

  // The lakes as a text file (Tarn's header comment, then the lakes with their numbers in the round-trip form: Tarn reads that too), UTF-8 and "\n".
  internal static void WriteLakes(string path, LakeSet set)
  {
    var text = string.Join("\n", set.ToFileLines()) + "\n";
    WriteFile(path, new UTF8Encoding(false).GetBytes(text));
  }

  internal static void WriteClearing(string path, ClearMask mask) => WriteFile(path, mask.ToTarnFile());

  // Whole, or not at all: written beside it, then put in its place in one step (File.Replace over an existing file, so a crash leaves the old file or the new one, and
  // never neither).
  private static void WriteFile(string path, byte[] bytes)
  {
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir))
      Directory.CreateDirectory(dir);
    var tmp = path + ".tmp";
    try
    {
      File.WriteAllBytes(tmp, bytes);
      if (File.Exists(path))
        File.Replace(tmp, path, null);
      else
        File.Move(tmp, path);
    }
    catch
    {
      try
      {
        File.Delete(tmp);
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
        // Left where it is.
      }
      throw;
    }
  }

  // A name nobody has used yet: prefix + stamp + ext, and -2, -3 ... after the stamp when it is taken (two backups in one clock tick never share a file).
  internal static string UniquePath(string folder, string prefix, string stamp, string ext)
  {
    var path = Path.Combine(folder, prefix + stamp + ext);
    for (int k = 2; File.Exists(path) || Directory.Exists(path); k++)
      path = Path.Combine(folder, $"{prefix}{stamp}-{k}{ext}");
    return path;
  }

  // ---- Tarn's own folder: an import source ----------------------------------------------------------------------------------------------------------

  internal static string TarnFolder() => Path.Combine(ConfigPath(), "Tarn");

  internal static string TarnLakesFile(string world, string? folder = null) => Path.Combine(folder ?? TarnFolder(), world + ".txt");

  internal static string TarnClearFile(string world, string? folder = null) => Path.Combine(folder ?? TarnFolder(), world + ".clear");

  // Tarn's own look setting, DepthScale in the [Look] section of its config (BepInEx/config/com.raveniron.tarn.cfg), if the player ever set one: an imported
  // world keeps the look the lakes had. Tarn's default is Better Continents' (0.5), which is what a missing file or line gives.
  internal static float TarnDepthScale()
  {
    try
    {
      var cfg = Path.Combine(ConfigPath(), TarnCompat.GUID + ".cfg");
      if (!File.Exists(cfg))
        return LakeSet.DefaultDepthScale;
      bool look = false;
      foreach (var line in File.ReadAllLines(cfg))
      {
        var text = line.Trim();
        if (text.StartsWith("["))
          look = text.Equals("[Look]", StringComparison.OrdinalIgnoreCase);
        else if (look && Regex.Match(text, @"^DepthScale\s*=\s*(\S+)$") is { Success: true } m
                 && float.TryParse(m.Groups[1].Value, NumberStyles.Float, C, out var v) && !float.IsNaN(v) && !float.IsInfinity(v))
          return Mathf.Clamp(v, 0f, LakeSet.MaxDepthScale);
      }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      // Not readable: Tarn's default.
    }
    return LakeSet.DefaultDepthScale;
  }

  // What reading Tarn's files for a world gives (nothing when a file is not there). Errors are in Problems; a file that does not read gives nothing.
  internal sealed class TarnRead
  {
    public LakeSet? Lakes;
    public ClearMask? Clearing;
    public string? LakesPath, ClearingPath;
    public readonly List<string> Problems = [];
    // What the parser said of the lakes file (lines left out, odd lakes), as "lakes.txt: line 6: ...".
    public readonly List<string> Notes = [];
  }

  // Tarn's <world>.txt and <world>.clear from a folder (BepInEx/config/Tarn by default).
  internal static TarnRead ReadTarn(string world, string? folder = null)
  {
    var read = new TarnRead();
    var lakes = TarnLakesFile(world, folder);
    if (File.Exists(lakes))
      try
      {
        var set = ReadLakes(lakes, read.Notes);
        if (set.IsEmpty)
          read.Problems.Add($"{lakes} holds no lake");
        else
        {
          read.Lakes = set.WithDepthScale(TarnDepthScale());
          read.LakesPath = lakes;
        }
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
        read.Problems.Add($"{lakes} cannot be read ({e.Message})");
      }
    var clear = TarnClearFile(world, folder);
    if (File.Exists(clear))
      try
      {
        read.Clearing = ReadClearing(clear);
        read.ClearingPath = clear;
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
      {
        read.Problems.Add($"{clear} cannot be read ({e.Message})");
      }
    return read;
  }

  // Once, right after a world's settings are loaded on the machine that runs it (ZNetPatch.SetServerPrefix): a Better Continents world that holds no lakes takes
  // Tarn's <world>.txt, one that holds no mask takes <world>.clear, from BepInEx/config/Tarn. This is how a live Ravenrest world keeps its lakes when
  // Better Continents takes Tarn's place. It never converts a vanilla world (that is `bc_lake import ... convert`), never repeats (the settings hold the data
  // from then on, and a world whose lakes were all removed holds an empty set, not none), never reads a file while the Tarn mod is installed (Tarn is using
  // it), and never writes to Tarn's folder. The data is saved with the world at its next save. Returns whether it took anything.
  internal static bool ImportTarnOnce(BetterContinents.BetterContinentsSettings settings, string? world)
  {
    if (!settings.EnabledForThisWorld || TarnCompat.Installed || string.IsNullOrEmpty(world))
      return false;
    bool needLakes = settings.Lakes == null && settings.LakesBlobAsRead == null && settings.LakesError == null;
    bool needMask = settings.Clearing == null && settings.ClearingBlobAsRead == null && settings.ClearingError == null;
    if (!needLakes && !needMask)
      return false;
    if (!File.Exists(TarnLakesFile(world!)) && !File.Exists(TarnClearFile(world!)))
      return false;
    var read = ReadTarn(world!);
    foreach (var problem in read.Problems)
      BetterContinents.LogError("Tarn's files: " + problem + ": not imported.");
    bool took = false;
    if (needLakes && read.Lakes != null)
    {
      settings.Lakes = read.Lakes;
      took = true;
      BetterContinents.Log($"Lakes: imported {read.Lakes.Lakes.Count:N0} lakes from {read.LakesPath}; they are saved with the world at the next save");
    }
    if (needMask && read.Clearing != null)
    {
      settings.Clearing = read.Clearing;
      took = true;
      BetterContinents.Log($"Clearing: imported the mask from {read.ClearingPath} ({read.Clearing.Summary()}); it is saved with the world at the next save");
    }
    return took;
  }

  // ---- bc info ---------------------------------------------------------------------------------------------------------------------------------

  // The lakes and the mask in words, for Settings.Dump: nothing for a world with neither.
  internal static void DumpLines(BetterContinents.BetterContinentsSettings s, Action<string> output)
  {
    bool any = false;
    if (s.Lakes is { } lakes)
    {
      any = true;
      var names = string.Join(", ", lakes.Lakes.Take(3).Select(l => l.Name));
      output($"Lakes: {lakes.Lakes.Count:N0} lake{(lakes.Lakes.Count == 1 ? "" : "s")}, depth scale {lakes.DepthScale.ToString("0.##", C)}"
        + (lakes.IsEmpty ? "" : $" ({names}{(lakes.Lakes.Count > 3 ? ", ..." : "")})"));
    }
    else if (s.LakesError != null)
    {
      any = true;
      output($"Lakes unreadable by this build ({s.LakesBlobAsRead?.Length:N0} bytes, kept unchanged, none made): {s.LakesError}");
    }
    if (s.Clearing is { } mask)
    {
      any = true;
      output($"Clearing mask: {mask.Size} x {mask.Size} over +-{mask.Extent.ToString("0", C)} m ({mask.MetresPerPixel.ToString("0.##", C)} m a pixel)"
        + (mask.Decoded ? $", {mask.Km2.ToString("0.00", C)} km2 kept free of vegetation" : ", its bits are opened where the world runs"));
    }
    else if (s.ClearingError != null)
    {
      any = true;
      output($"Clearing mask unreadable by this build ({s.ClearingBlobAsRead?.Length:N0} bytes, kept unchanged, not used): {s.ClearingError}");
    }
    if (any && TarnCompat.Installed)
      output("Lakes and clearing: off, the Tarn mod is installed (the data is kept and saved with the world)");
  }
}

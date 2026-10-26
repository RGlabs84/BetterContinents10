// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
//
// Where a world's lakes and mask come from (BUILD-SPEC-TARN.md 2.2): the Directory's lakes.txt and clearmask.bin (and the names the Ravenrest pipeline writes), a
// new world made From Config, by bc_import and from an export folder's export.cfg, a preset, and Tarn's own folder read once.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  private static ConfigFile boundConfig;

  // BetterContinents.cfg bound the way the plugin binds it, in the run's folder; once.
  public static ConfigFile Config()
  {
    if (boundConfig == null)
    {
      boundConfig = new ConfigFile(Path.Combine(Work, "BetterContinents.cfg"), true);
      BC.DeclareConfig(boundConfig);
      WorldImport.RootDir = Path.Combine(Work, "save", "BetterContinents");
      Presets.PresetsDir = Path.Combine(WorldImport.RootDir, "presets");
    }
    return boundConfig;
  }

  // The settings a new world made From Config would get if the config held these values (and Directory this folder).
  private static BC.BetterContinentsSettings NewWorldFrom(string dir, float? depthScale = null, bool enabled = true)
  {
    Config();
    var overrides = new Dictionary<ConfigEntryBase, object>
    {
      [BC.ConfigMapSourceDir] = dir,
      [BC.ConfigEnabled] = enabled,
    };
    if (depthScale != null)
      overrides[SettingsSchema.LakeDepthScale.Entry] = depthScale.Value;
    var values = ConfigValues.Snapshot(Config(), overrides.Select(kv => new KeyValuePair<ConfigEntryBase, object>(kv.Key, kv.Value)));
    return BC.BetterContinentsSettings.CreateForImport(values, lean: false);
  }

  private static void DirectoryFilesTest()
  {
    Section("files: the Directory's lakes.txt and clearmask.bin");
    Config();
    var dir = NewDir("dir-both");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(dir, "lakes.txt"));
    File.Copy(Fixture("clear37.bin"), Path.Combine(dir, "clearmask.bin"));
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var log = LogHandler.During(() => s.ReadLakeFiles(dir));
    C(s.Lakes != null && s.Lakes.Lakes.Count == 5 && s.Clearing is { Size: 37 } && s.LakesError == null && s.ClearingError == null, "a Directory's lakes.txt and clearmask.bin are the new world's lakes and mask");
    C(log.Any(l => l.Contains("Lakes: " + Path.Combine(dir, "lakes.txt") + ": 5 lakes")) && log.Any(l => l.Contains("Clearing: " + Path.Combine(dir, "clearmask.bin") + ": 37 x 37 over +-124 m")), "the log names each file and what it held: " + string.Join(" | ", log));
    C(log.Any(l => l.Contains(Path.Combine(dir, "lakes.txt") + ": line 6:") && l.Contains("4 fields")) && log.Count(l => l.StartsWith("[Warning]")) == 4, "a line that does not read is a warning naming the file and the line (Tarn dropped it silently): " + string.Join(" | ", log.Where(l => l.StartsWith("[Warning]"))));
    C(s.Lakes.DepthScale == s.LakeDepthScale, "the lakes take the world's depth scale");

    // The Ravenrest pipeline's names.
    var alias = NewDir("dir-alias");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(alias, "tarn-lakes.txt"));
    File.Copy(Fixture("clear64.bin"), Path.Combine(alias, "tarn-clear.bin"));
    var a = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    a.ReadLakeFiles(alias);
    C(a.Lakes?.Lakes.Count == 5 && a.Clearing is { Size: 64 }, "tarn-lakes.txt and tarn-clear.bin are taken when the standard names are missing");
    // Both: the standard one wins and the log says so.
    File.Copy(Fixture("clear37.bin"), Path.Combine(alias, "clearmask.bin"));
    File.WriteAllText(Path.Combine(alias, "lakes.txt"), "only;1;2;60;0;0;9;9;3\n");
    var b = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var bothLog = LogHandler.During(() => b.ReadLakeFiles(alias));
    C(b.Lakes?.Lakes.Single().Name == "only" && b.Clearing is { Size: 37 } && bothLog.Any(l => l.Contains("holds lakes.txt and tarn-lakes.txt")) && bothLog.Any(l => l.Contains("holds clearmask.bin and tarn-clear.bin")),
      "with both, the standard name wins and the log says so");

    // Nothing to read.
    var none = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var noneLog = LogHandler.During(() => none.ReadLakeFiles(NewDir("dir-none")));
    C(none.Lakes == null && none.Clearing == null && noneLog.Count == 0, "a Directory without them gives a world without them, and says nothing");
    none.ReadLakeFiles("");
    C(none.Lakes == null && none.Clearing == null, "no Directory, none");

    // Files that do not read: an error naming the file, the world is made without that one.
    var bad = NewDir("dir-bad");
    File.WriteAllBytes(Path.Combine(bad, "clearmask.bin"), File.ReadAllBytes(Fixture("clear64.bin")).Take(100).ToArray());
    File.WriteAllText(Path.Combine(bad, "lakes.txt"), "# nothing but a comment\n\n");
    var d = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var badLog = LogHandler.During(() => d.ReadLakeFiles(bad));
    C(d.Clearing == null && d.ClearingError == null && badLog.Any(l => l.StartsWith("[Error]") && l.Contains(Path.Combine(bad, "clearmask.bin")) && l.Contains("512") && l.Contains("made without it")), "a mask that does not read is an error naming the file and what is wrong: " + string.Join(" | ", badLog));
    C(d.Lakes == null && badLog.Any(l => l.StartsWith("[Warning]") && l.Contains("holds no lake")), "a lakes file with no lake in it gives no lakes, with a word");
    var worse = NewDir("dir-worse");
    Directory.CreateDirectory(Path.Combine(worse, "lakes.txt"));
    var e = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    LogHandler.During(() => e.ReadLakeFiles(worse));
    C(e.Lakes == null, "a folder where lakes.txt should be does not stop the world being made (it has no lakes file)");

    // ReadLakeFiles starts from nothing: lakes from an earlier read do not stay.
    var again = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, Lakes = SampleLakes(), Clearing = SampleMask() };
    again.ReadLakeFiles(NewDir("dir-empty"));
    C(again.Lakes == null && again.Clearing == null, "a new read replaces what the settings held");
  }

  private static void NewWorldTest()
  {
    Section("files: a new world takes the files like every other map (From Config, bc_import, export.cfg), and a preset keeps them");
    var dir = NewDir("world-dir");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(dir, "lakes.txt"));
    File.Copy(Fixture("clear37.bin"), Path.Combine(dir, "clearmask.bin"));
    var world = NewWorldFrom(dir);
    C(world.Lakes?.Lakes.Count == 5 && world.Clearing is { Size: 37 } && world.Lakes.DepthScale == 0.5f, "From Config with the Directory set: the lakes (depth scale 0.5, the config's default) and the mask");
    var scaled = NewWorldFrom(dir, 1.25f);
    C(scaled.Lakes.DepthScale == 1.25f && scaled.LakeDepthScale == 1.25f && scaled.EffectiveLakeDepthScale == 1.25f, "the config's Lake Depth Scale is the new world's");
    C(NewWorldFrom(NewDir("world-empty")).Lakes == null, "a Directory without the files gives none");
    var disabled = BC.BetterContinentsSettings.Create(false, ConfigValues.Snapshot(Config(), new Dictionary<ConfigEntryBase, object> { [BC.ConfigMapSourceDir] = dir }));
    C(disabled.Lakes == null && disabled.Clearing == null && !disabled.EnabledForThisWorld, "Better Continents off for the world (the Disabled preset) takes none, though the Directory has them");
    var bytes = SettingsBytes(world, false);
    var back = BC.BetterContinentsSettings.Load(new ZPackage(bytes));
    C(back.Lakes.Lakes.SequenceEqual(world.Lakes.Lakes) && back.Clearing.ToTarnFile().SequenceEqual(File.ReadAllBytes(Fixture("clear37.bin"))), "the world saves and reads them back: the mask to the byte of the file it came from");

    // A preset is the settings serialization: it keeps both, whole.
    var preset = Path.Combine(Work, "preset.BetterContinents");
    world.Save(preset);
    var made = BC.BetterContinentsSettings.Load(preset);
    C(made.Lakes.Lakes.SequenceEqual(world.Lakes.Lakes) && made.Lakes.DepthScale == 0.5f && made.Clearing.ToBlock().SequenceEqual(world.Clearing.ToBlock()), "a preset saved from a world with lakes makes new worlds with the same lakes and mask");
    var presetBytes = new ZPackage();
    world.Serialize(presetBytes, false, true, world.SavedVersion);
    C(presetBytes.GetArray().SequenceEqual(bytes), "saved as a preset (the settings' own version) it is the same bytes");

    // An export folder, the way bc_import reads it: export.cfg over the config, the folder as the Directory.
    var folder = NewDir("export-folder");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(folder, "lakes.txt"));
    File.Copy(Fixture("clear64.bin"), Path.Combine(folder, "clearmask.bin"));
    using (var image = new Image<L16>(32, 32))
      image.SaveAsPng(Path.Combine(folder, "heightmap.png"));
    File.WriteAllLines(Path.Combine(folder, "export.cfg"),
    [
      "## Better Continents world export", "", "[00 BetterContinents.Debug]", "Enabled = true", "", "[12 BetterContinents.Lakes]", "Lake Depth Scale = 1.5",
    ]);
    var plan = WorldImport.MakePlan(folder);
    C(plan.Applied.Any(x => x.Contains("Lake Depth Scale = 1.5")) && !plan.Ignored.Any(x => x.Contains("Lake")), "export.cfg's Lake Depth Scale is applied by bc_import (not ignored): " + string.Join(" | ", plan.Applied.Concat(plan.Ignored)));
    var imported = BC.BetterContinentsSettings.CreateForImport(plan.Values, lean: false);
    C(imported.Lakes?.Lakes.Count == 5 && imported.Lakes.DepthScale == 1.5f && imported.Clearing is { Size: 64 }, "bc_import: the world made from the folder has its lakes (depth scale 1.5, export.cfg's) and its mask");
    var oldDir = BC.ConfigMapSourceDir.Value;
    try
    {
      BC.ConfigMapSourceDir.Value = folder;
      var values = WorldImport.DirectoryValues();
      C(values != null, "a Directory with an export.cfg: its values are used");
      var viaDirectory = BC.BetterContinentsSettings.CreateForImport(values, lean: false);
      C(viaDirectory.Lakes?.Lakes.Count == 5 && viaDirectory.Lakes.DepthScale == 1.5f && viaDirectory.Clearing is { Size: 64 }, "From Config with that Directory: the same lakes and mask");
    }
    finally
    {
      BC.ConfigMapSourceDir.Value = oldDir;
    }
  }

  // A shareable .bcworld file is the settings package as a client holds it: it carries the lakes and the mask with no line of its own.
  private static void ShareFileTest()
  {
    Section("files: a .bcworld share file carries the lakes and the mask");
    var world = World(SampleLakes(), SampleMask());
    var package = SettingsBytes(world, true);
    // (The cache folder is the run's own from here on: its getter needs the game's save path, so the old value is not read back.)
    {
      BC.ZNetPatch.WorldCache.WorldCachePath = NewDir("share-cache");
      var export = WorldCacheShare.WriteExportFiles(package, "Test World", "seed", NewDir("share-out"));
      C(File.ReadAllBytes(export.FilePath).SequenceEqual(package) && !File.ReadAllText(export.SidecarPath).Contains("lake", StringComparison.OrdinalIgnoreCase), "the .bcworld file is the package byte for byte, and its sidecar needs no line about lakes");
      var imported = WorldCacheShare.ImportPath(export.FilePath, out var error);
      C(error == null && imported.Count == 1 && imported[0].Imported, "a player imports it: " + string.Join(" | ", imported.Select(i => i.Describe())));
      var read = BC.ZNetPatch.WorldCache.LoadCacheSettings(export.Id);
      C(read.HasLakes && read.Lakes.Lakes.SequenceEqual(world.Lakes.Lakes) && read.Clearing != null && read.Clearing.ToBlock().SequenceEqual(world.Clearing.ToBlock()), "and the cached settings have the same lakes and the same mask");
    }
  }

  // ---- Tarn's own folder: read once, never written -------------------------------------------------------------------------------------------------

  private static void TarnImportTest()
  {
    Section("files: Tarn's own folder is read once, by a Better Continents world that has no lakes and no mask");
    var config = NewDir("tarn-config");
    var tarnDir = Path.Combine(config, "Tarn");
    Directory.CreateDirectory(tarnDir);
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(tarnDir, "Raventest.txt"));
    File.Copy(Fixture("clear37.bin"), Path.Combine(tarnDir, "Raventest.clear"));
    File.WriteAllText(Path.Combine(config, "com.raveniron.tarn.cfg"), "## Tarn\n[Look]\n\n## how deep\n# Setting type: Single\nDepthScale = 0.8\n\n[Sound]\nDepthScale = 3\nLakeVolume = 0.35\n");
    var before = Directory.GetFiles(tarnDir).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).OrderBy(x => x.f).ToList();
    var old = LakeFiles.ConfigPath;
    LakeFiles.ConfigPath = () => config;
    TarnCompat.Installed = false;
    try
    {
      var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
      bool took = false;
      var log = LogHandler.During(() => took = LakeFiles.ImportTarnOnce(s, "Raventest"));
      C(took && s.Lakes?.Lakes.Count == 5 && s.Clearing is { Size: 37 }, "a world with no lakes and no mask takes Tarn's <world>.txt and <world>.clear");
      C(s.Lakes.DepthScale == 0.8f, "and Tarn's own DepthScale from its config ([Look] only): " + s.Lakes.DepthScale);
      C(log.Any(l => l.Contains("Lakes: imported 5 lakes from ") && l.Contains("saved with the world at the next save")) && log.Any(l => l.Contains("Clearing: imported the mask from ")), "the log says what was imported: " + string.Join(" | ", log));
      C(!LakeFiles.ImportTarnOnce(s, "Raventest"), "it never repeats: once the settings hold them the files are not looked at again");
      var after = Directory.GetFiles(tarnDir).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).OrderBy(x => x.f).ToList();
      C(before.SequenceEqual(after), "Tarn's folder is never written to");

      // A world whose lakes were all removed holds an empty set, which is not 'no lakes': the lakes do not come back.
      var emptied = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, Lakes = LakeSet.Empty };
      LakeFiles.ImportTarnOnce(emptied, "Raventest");
      C(emptied.Lakes.IsEmpty && emptied.Clearing is { Size: 37 }, "a world whose lakes were removed does not get them back (its empty set is kept), and still takes a mask it lacks");
      // Only what is missing.
      var maskOnly = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, Clearing = ClearMask.FromTarnFile(File.ReadAllBytes(Fixture("clear8.bin"))) };
      LakeFiles.ImportTarnOnce(maskOnly, "Raventest");
      C(maskOnly.Lakes?.Lakes.Count == 5 && maskOnly.Clearing.Size == 8, "a world that has a mask keeps it, and takes the lakes");
      // A vanilla world is never converted by this.
      var vanilla = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      C(!LakeFiles.ImportTarnOnce(vanilla, "Raventest") && vanilla.Lakes == null && vanilla.Clearing == null, "a world that is not a Better Continents world takes nothing (bc_lake import ... convert does that)");
      // The Tarn mod being there: it uses the files.
      TarnCompat.Installed = true;
      var tarnWorld = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
      C(!LakeFiles.ImportTarnOnce(tarnWorld, "Raventest") && tarnWorld.Lakes == null, "while the Tarn mod is installed nothing is imported");
      TarnCompat.Installed = false;
      // Another world's name, or no files, or no name.
      C(!LakeFiles.ImportTarnOnce(new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 }, "Other") && !LakeFiles.ImportTarnOnce(new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 }, null), "a world with no files of its name takes nothing");
      // A world whose own lakes cannot be read is not touched.
      var unreadable = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, LakesError = "unreadable here" };
      LakeFiles.ImportTarnOnce(unreadable, "Raventest");
      C(unreadable.Lakes == null && unreadable.Clearing is { Size: 37 }, "a world whose lakes this build cannot read keeps them (they are not replaced), and takes the mask");
      // A mask that does not read: an error, the lakes still come.
      File.WriteAllBytes(Path.Combine(tarnDir, "Broken.clear"), new byte[] { 1, 2, 3 });
      File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(tarnDir, "Broken.txt"));
      var broken = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
      var brokenLog = LogHandler.During(() => LakeFiles.ImportTarnOnce(broken, "Broken"));
      C(broken.Lakes?.Lakes.Count == 5 && broken.Clearing == null && brokenLog.Any(l => l.StartsWith("[Error]") && l.Contains("Broken.clear") && l.Contains("not imported")), "a Tarn mask that does not read is an error naming the file; the lakes come all the same");
      // The read alone (bc_lake import reads it the same way) and Tarn's DepthScale when its config is missing.
      var read = LakeFiles.ReadTarn("Raventest");
      C(read.Lakes.Lakes.Count == 5 && read.Clearing != null && read.Problems.Count == 0, "ReadTarn reads both files of a world");
      File.Delete(Path.Combine(config, "com.raveniron.tarn.cfg"));
      C(LakeFiles.TarnDepthScale() == 0.5f && LakeFiles.ReadTarn("Raventest").Lakes.DepthScale == 0.5f, "without Tarn's config the depth scale is Tarn's default, 0.5");
      File.WriteAllText(Path.Combine(config, "com.raveniron.tarn.cfg"), "[Look]\nDepthScale = 9\n");
      C(LakeFiles.TarnDepthScale() == 3f, "and one out of Tarn's range is held to it (3)");
    }
    finally
    {
      LakeFiles.ConfigPath = old;
      TarnCompat.Installed = false;
    }
  }
}

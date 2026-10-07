// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using BC = BetterContinents.BetterContinents;

namespace ImportTest;

// Fine heights (a heightmap-fine.png beside the heightmap) through the ways a new world is made: a Directory with an export.cfg
// (with each value of Compact Maps), bc_import's preset, the config's own heightmap file.
internal static partial class Program
{
  static void FineWithEachValue()
  {
    Section("fine heights and Compact Maps in an export.cfg: a map of 8193 px across and a fine heightmap, for each value");
    var export = Path.Combine(root, "FineCompactCfg", "export-2026-10-06-12-30-00");
    Directory.CreateDirectory(export);
    var (heightmap, fine) = FinePairBytes(64, record: true);
    File.WriteAllBytes(Path.Combine(export, "heightmap.png"), heightmap);
    File.WriteAllBytes(Path.Combine(export, "heightmap-fine.png"), fine);
    Strip(export, "biomemap.png", 8193);
    using (var forest = new Image<L16>(64, 64))
      forest.SaveAsPng(Path.Combine(export, "forestmap.png"), new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 });
    foreach (var (text, compactWanted, fineWanted) in new[] { ("true", true, true), ("false", true, true), ("Auto", true, true), ("On", true, true), ("Off", false, false) })
    {
      File.WriteAllLines(Path.Combine(export, "export.cfg"),
        ["[00 BetterContinents.Debug]", "Enabled = true", "[02 BetterContinents.Heightmap]", "Heightmap Amount = 81", "[07 BetterContinents.Misc]", $"Compact Maps = {text}"]);
      lock (LogHandler.Lines) LogHandler.Lines.Clear();
      var made = BC.BetterContinentsSettings.CreateForImport(WorldImport.MakePlan(export).Values, lean: false);
      var forestMap = Map<ImageMapFloat>(made, "forestmap.png");
      C(made.CompactMaps == compactWanted && made.FineBits > 0 == fineWanted && (forestMap?.Compact ?? false) == compactWanted,
        $"export.cfg Compact Maps = {text}: with a map of 8193 px and a fine heightmap the world is {(made.CompactMaps ? "compact" : "pictures")}, fine heights {(made.FineBits > 0 ? $"{made.FineBits} bits" : "not read")}"
        + (made.FineNote != null ? $" ({made.FineNote})" : ""));
    }
  }

  // ---- fine heights through the ways a world is made --------------------------------------------------------------------------------

  // A heightmap.png and its heightmap-fine.png (n pixels; the fine file's record names the heightmap's CRC-32, wrongly when asked).
  static (byte[] Heightmap, byte[] Fine) FinePairBytes(int n, bool record, bool wrongCrc = false)
  {
    var heightmap = PngWriter.Write(n, n, 0, 16, (y, raw) =>
    {
      for (int x = 0; x < n; x++)
      {
        ushort v = (ushort)(20000 + 31 * x + 17 * y);
        raw[2 * x] = (byte)(v >> 8);
        raw[2 * x + 1] = (byte)v;
      }
    });
    uint crc = Crc32.Compute(heightmap, 0, heightmap.Length);
    var fine = PngWriter.Write(n, n, 0, 8, (y, raw) =>
    {
      for (int x = 0; x < n; x++)
        raw[x] = (byte)((x * 5 + y * 3) % 61 - 30);
    }, record ? HeightmapRecord.Keyword : null, record ? FineHeights.RecordText(8, wrongCrc ? crc ^ 1 : crc) : null);
    return (heightmap, fine);
  }

  static void FineImport()
  {
    Section("fine heights: a Directory with an export.cfg, the config's own file, bc_import's preset");
    const int F = 64;
    var folder = Path.Combine(root, "FineFolder", "export-2026-10-06-13-00-00");
    Directory.CreateDirectory(folder);
    var (heightmap, fine) = FinePairBytes(F, record: true);
    File.WriteAllBytes(Path.Combine(folder, "heightmap.png"), heightmap);
    File.WriteAllBytes(Path.Combine(folder, "heightmap-fine.png"), fine);
    using (var forest = new Image<L16>(F, F))
      forest.SaveAsPng(Path.Combine(folder, "forestmap.png"), new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 });
    File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\n  \"format\": \"bc-export/1\",\n  \"worldName\": \"Fine World\",\n  \"exportedAt\": \"2026-10-06T13:00:00\",\n  \"size\": 64,\n  \"totalSize\": 21000\n}");
    File.WriteAllLines(Path.Combine(folder, "export.cfg"),
      ["[00 BetterContinents.Debug]", "Enabled = true", "[01 BetterContinents.Global]", "World Size = 10000", "Edge Size = 500", "[02 BetterContinents.Heightmap]", "Heightmap Amount = 81", "Heightmap Override All = true"]);

    // The Directory way: a new world made From Config.
    BC.ConfigMapSourceDir.Value = folder;
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var s = BC.BetterContinentsSettings.Create();
    var heights = Map<ImageMapFloat>(s, "heightmap.png");
    var forestMap = Map<ImageMapFloat>(s, "forestmap.png");
    C(s.HasHeightMap && heights.HasFine && heights.FineBits == 8 && s.FineBits == 8 && s.FineNote == null && s.CompactMaps && forestMap.Compact && heights.Compact,
      "a Directory with an export.cfg: the new world has the fine heights (8 bits), and is compact, the forest map too");
    C(LogHandler.Has("[BetterContinents] Fine heights: heightmap-fine.png refines heightmap.png with 8 bits a pixel: ground in steps of") && LogHandler.Has("Compact Maps is on for this world: its heightmap has fine heights (heightmap-fine.png)"),
      "the log says the file refines the heightmap, with what it costs, and that the world is compact because of it");
    var info = new List<string>();
    s.Dump(info.Add);
    C(info.Any(l => l.StartsWith("Fine heights: 8 bits a pixel on top of the heightmap's 16: ground in steps of 0.97 mm at Heightmap Amount 81")), "bc info says so (" + info.FirstOrDefault(l => l.StartsWith("Fine heights")) + ")");
    // The world as it is saved and read again.
    var pkg = new ZPackage();
    s.Serialize(pkg, false, true, 12);
    var again = BC.BetterContinentsSettings.Load(new ZPackage(pkg.GetArray()));
    C(Map<ImageMapFloat>(again, "heightmap.png").HasFine && again.CompactMaps && Map<ImageMapFloat>(again, "heightmap.png").GetValue(0.4f, 0.6f) == heights.GetValue(0.4f, 0.6f), "saved and loaded again it has them, and reads the same");

    // The same folder, the fine file gone: the world as before (pictures), nothing said.
    var plainFolder = Path.Combine(root, "FinePlain", "export-2026-10-06-13-10-00");
    Directory.CreateDirectory(plainFolder);
    foreach (var file in new[] { "heightmap.png", "forestmap.png", "export.cfg", "manifest.json" })
      File.Copy(Path.Combine(folder, file), Path.Combine(plainFolder, file), true);
    BC.ConfigMapSourceDir.Value = plainFolder;
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var plain = BC.BetterContinentsSettings.Create();
    C(!plain.CompactMaps && !Map<ImageMapFloat>(plain, "heightmap.png").HasFine && plain.FineNote == null && !LogHandler.Has("Fine heights"), "without the fine file: pictures, no fine heights, not a word about them");

    // Unusable files: the preset is made without them, and says why.
    foreach (var (name, make, words) in new (string, Func<(byte[] H, byte[] F)>, string)[]
    {
      ("another size", () => (heightmap, FinePairBytes(F + 1, true).Fine), "is 65 x 65 pixels and heightmap.png is 64 x 64"),
      ("another heightmap", () => FinePairBytes(F, true, wrongCrc: true), "was made for another heightmap.png"),
    })
    {
      var bad = Path.Combine(root, "FineBad", name.Replace(' ', '-'), "export-2026-10-06-13-20-00");
      Directory.CreateDirectory(bad);
      var (h, f) = make();
      File.WriteAllBytes(Path.Combine(bad, "heightmap.png"), h);
      File.WriteAllBytes(Path.Combine(bad, "heightmap-fine.png"), f);
      File.Copy(Path.Combine(folder, "export.cfg"), Path.Combine(bad, "export.cfg"), true);
      var plan = WorldImport.MakePlan(bad);
      var outcome = WorldImport.BuildPreset(plan);
      var made = BC.BetterContinentsSettings.Load(outcome.PresetPath!);
      C(outcome.Error == null && !Map<ImageMapFloat>(made, "heightmap.png").HasFine && !made.CompactMaps && outcome.Warnings.Any(w => w.StartsWith("Fine heights: heightmap-fine.png") && w.Contains(words)) && !outcome.Summary.Contains("fine heights"),
        $"bc_import, a fine file that is {name}: a preset without fine heights, and the warning says why ({outcome.Warnings.FirstOrDefault(w => w.StartsWith("Fine heights"))})");
    }

    // The heightmap edited after the pair was made: its CRC is no longer the one in the record.
    var edited = Path.Combine(root, "FineEdited", "export-2026-10-06-13-30-00");
    Directory.CreateDirectory(edited);
    var (eh, ef) = FinePairBytes(F, true);
    File.WriteAllBytes(Path.Combine(edited, "heightmap-fine.png"), ef);
    File.WriteAllBytes(Path.Combine(edited, "heightmap.png"), PngWriter.Write(F, F, 0, 16, (y, raw) => { for (int x = 0; x < F; x++) { raw[2 * x] = 0x40; raw[2 * x + 1] = (byte)(x + y); } }));
    File.Copy(Path.Combine(folder, "export.cfg"), Path.Combine(edited, "export.cfg"), true);
    var editedOutcome = WorldImport.BuildPreset(WorldImport.MakePlan(edited));
    C(editedOutcome.Error == null && editedOutcome.Warnings.Any(w => w.Contains("was made for another heightmap.png") && w.Contains("make heightmap-fine.png again or delete it")), "bc_import, a heightmap edited after the pair was made: the CRC guard refuses the fine file, and the warning says what to do");

    // bc_import's preset of the good folder.
    var plan2 = WorldImport.MakePlan(folder);
    var good = Task_Run(() => WorldImport.BuildPreset(plan2));
    var preset = BC.BetterContinentsSettings.Load(good.PresetPath!);
    C(good.Error == null && good.Warnings.Count == 0 && good.Summary.EndsWith(", fine heights 8 bits") && Map<ImageMapFloat>(preset, "heightmap.png").HasFine && preset.CompactMaps && Map<ImageMapFloat>(preset, "forestmap.png").Compact,
      $"bc_import: the preset holds the fine heights, its summary says so ('{good.Summary}'), and no warning");
    C(Map<ImageMapFloat>(preset, "heightmap.png").GetValue(0.4f, 0.6f) == heights.GetValue(0.4f, 0.6f), "and the preset's heightmap reads as the Directory world's does");

    // The config's own file: Directory empty, Heightmap File set.
    BC.ConfigMapSourceDir.Value = "";
    BC.ConfigHeightFile.Value = Path.Combine(folder, "heightmap.png");
    var configured = BC.BetterContinentsSettings.Create();
    C(Map<ImageMapFloat>(configured, "heightmap.png").HasFine && configured.CompactMaps, "the config's Heightmap File: the fine file beside it is read as well");
    BC.ConfigCompactMaps.Value = CompactMapsMode.Off;
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var off = BC.BetterContinentsSettings.Create();
    C(!Map<ImageMapFloat>(off, "heightmap.png").HasFine && !off.CompactMaps && LogHandler.Has("[Warning] [BetterContinents] Fine heights: heightmap-fine.png is not read: Compact Maps is Off")
      && off.FineNote!.Contains("Compact Maps is Off"), "with Compact Maps Off the same file is not read, and the log says why");
    BC.ConfigCompactMaps.Value = CompactMapsMode.Auto;
    BC.ConfigOverrideVersion.Value = "11";
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var old = BC.BetterContinentsSettings.Create();
    C(old.Version == 11 && !Map<ImageMapFloat>(old, "heightmap.png").HasFine && LogHandler.Has("Fine heights: heightmap-fine.png is not read: the world is saved in settings version 11"), "Override version 11: a world of settings version 11 does not read it");
    BC.ConfigOverrideVersion.Value = "";
    BC.ConfigHeightFile.Value = "";
    BC.ConfigMapSourceDir.Value = "";
  }

  static T Task_Run<T>(Func<T> work) => System.Threading.Tasks.Task.Run(work).Result;
}

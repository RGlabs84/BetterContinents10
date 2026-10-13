// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// A new world's layer from the Directory (spec 3.3): placements.bcp read with the maps, kept byte for byte, refused by name when it cannot be
// read; the preset a running world saves, which keeps the compiler's records; the share file's note.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static ConfigFile bcConfig;

  // The plugin's own config, bound to a file of this run's (once): the Directory and the other settings a new world reads.
  private static ConfigFile Config()
  {
    if (bcConfig == null)
    {
      bcConfig = new ConfigFile(Path.Combine(Work, "BetterContinents.cfg"), true);
      BC.DeclareConfig(bcConfig);
    }
    return bcConfig;
  }

  private static BC.BetterContinentsSettings NewWorldFrom(string directory, bool enabled = true)
  {
    var cfg = Config();
    BC.ConfigMapSourceDir.Value = directory;
    try
    {
      return BC.BetterContinentsSettings.Create(enabled, ConfigValues.Snapshot(cfg, []));
    }
    finally
    {
      BC.ConfigMapSourceDir.Value = "";
    }
  }

  private static BC.BetterContinentsSettings ImportedFrom(string directory)
  {
    var cfg = Config();
    BC.ConfigMapSourceDir.Value = directory;
    try
    {
      return BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, []), lean: false);
    }
    finally
    {
      BC.ConfigMapSourceDir.Value = "";
    }
  }

  private static void DirectoryLayerTest()
  {
    Section("directory: a new world takes the layer in its Directory, byte for byte");
    var layer = MakeSample(41).Layer;
    // A layer that is revision 2 or more: a new world keeps the file's revision.
    var bumped = layer.Edit();
    bumped.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    var file = bumped.Build().Layer;
    C(file.Revision == 2u, "the file is revision 2");
    var dir = Path.Combine(Work, "directory-with");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "placements.bcp"), file.Bytes.Take(file.Length).ToArray());

    var made = NewWorldFrom(dir);
    C(made.EnabledForThisWorld && made.HasLayer && made.Layer.Id == file.Id && made.Layer.Revision == 2u && !made.GameTerrain, "From Config with the folder as Directory: the world has the file's layer, and its revision");
    C(made.Layer.Bytes.Take(made.Layer.Length).SequenceEqual(file.Bytes.Take(file.Length)), "the bytes are the file's, as they are");
    var imported = ImportedFrom(dir);
    C(imported.HasLayer && imported.Layer.Id == file.Id, "an import (the preset bc_import makes) has it too");
    // It saves and reads back with the world.
    var saved = made.Bytes(out int length);
    var reread = BC.BetterContinentsSettings.Load(new ZPackage(saved.Take(length).ToArray()));
    C(reread.HasLayer && reread.Layer.Id == file.Id && reread.Version == 12, "the world's settings file holds it");
    C(!NewWorldFrom(dir, enabled: false).HasLayer, "a world made with Better Continents off for it takes no layer");
    C(!BC.BetterContinentsSettings.Disabled().HasLayer, "nor does a Disabled one");
    var empty = Path.Combine(Work, "directory-without");
    Directory.CreateDirectory(empty);
    C(!NewWorldFrom(empty).HasLayer && !NewWorldFrom("").HasLayer && !NewWorldFrom(Path.Combine(Work, "no such folder")).HasLayer, "no file, no folder, no layer");

    // A file that cannot be read: the world is made without it, and a line names the file and the problem.
    var bad = Path.Combine(Work, "directory-damaged");
    Directory.CreateDirectory(bad);
    var damaged = file.Bytes.Take(file.Length).ToArray();
    damaged[damaged.Length / 3] ^= 0x10;
    File.WriteAllBytes(Path.Combine(bad, "placements.bcp"), damaged);
    BC.BetterContinentsSettings world = null;
    var lines = LogHandler.During(() => world = NewWorldFrom(bad));
    C(world.EnabledForThisWorld && !world.HasLayer, "a damaged file: the world is made without a layer");
    C(lines.Any(l => l.Contains("[Error]") && l.Contains("placements.bcp") && l.Contains("CRC") && l.Contains("bc_bake load")), "and an error line names the file, the problem and the way to add a layer later");
    File.WriteAllBytes(Path.Combine(bad, "placements.bcp"), [1, 2, 3]);
    lines = LogHandler.During(() => world = NewWorldFrom(bad));
    C(!world.HasLayer && lines.Any(l => l.Contains("under the 36")), "a file that is too short is named too");
    var newer = file.Bytes.Take(file.Length).ToArray();
    newer[6] |= 16;
    Recrc(newer, newer.Length);
    File.WriteAllBytes(Path.Combine(bad, "placements.bcp"), newer);
    lines = LogHandler.During(() => world = NewWorldFrom(bad));
    C(!world.HasLayer && lines.Any(l => l.Contains("newer Better Continents or tool: header bit 4")), "a file from a newer tool says so");

    // VALtima's real file.
    if (NeedValtima("DirectoryLayerTest"))
    {
      var big = Path.Combine(Work, "directory-valtima");
      Directory.CreateDirectory(big);
      File.WriteAllBytes(Path.Combine(big, "placements.bcp"), Valtima());
      var started = System.Diagnostics.Stopwatch.StartNew();
      var town = NewWorldFrom(big);
      C(town.HasLayer && town.Layer.Placements == 702_700 && town.Layer.Length == 2_782_837 && Md5(town.Layer.Bytes, town.Layer.Length) == "e9d8f303b2e582d5ddf668a5be1dae3a",
        $"a new world from VALtima's folder has their layer, 702,700 records (read in {started.ElapsedMilliseconds} ms)");
      var townBytes = town.Bytes(out int townLength);
      C(townLength > 2_782_837 && townLength < 2_782_837 + 200, $"its settings file is the layer and a few bytes ({townLength:N0})");
      var townNet = SettingsBytes(town, true);
      C(townNet.Length < 200, $"and the package sent to players is {townNet.Length} bytes, the layer not in it");
    }
  }

  private static void PresetLayerTest()
  {
    Section("presets: a preset saved from a running world keeps the compiler's records only");
    // A preset is saved in the configured format (Override version), so the config is bound first.
    Config();
    var sample = MakeSample(42);
    var layer = sample.Layer;
    var kept = layer.WithoutInGame(out int dropped);
    long inGame = layer.Placements - layer.RecordsOfSource(0);
    C(inGame > 0 && dropped == inGame, $"the sample holds {inGame} records baked in game, and says so");
    C(kept.Placements == layer.RecordsOfSource(0) && kept.RecordsBySource().Keys.SequenceEqual(new[] { 0 }) && !kept.HasRegistry && kept.Registry.Operations.Count == 0,
      "the preset's layer has the compiler's records and no registry");
    C(kept.Palette.Count == layer.Palette.Count && kept.Zones.Any(z => z.HasGround) && kept.Ground.Count == layer.Ground.Count, "the palette, ground and sections stay");
    var plain = BakedLayer.Read(kept.Bytes, kept.Length);
    C(Differ(kept, plain) == null, "and it is a layer that reads");
    var none = LayerEdit.New("none");
    none.PaletteIndexFor(Beam());
    none.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    var compilerOnly = none.Build().Layer;
    C(ReferenceEquals(compilerOnly.WithoutInGame(out int zero), compilerOnly) && zero == 0, "a layer with none of either is its own preset layer");
    // The settings of the world are left as they are, and the preset file holds the other.
    var world = World(layer);
    var forPreset = world.WithLayer(kept);
    C(ReferenceEquals(world.Layer, layer) && ReferenceEquals(forPreset.Layer, kept) && forPreset.WorldSize == world.WorldSize && forPreset.GlobalScale == world.GlobalScale, "the world keeps its layer; the copy has the preset's");
    var path = Path.Combine(Work, "preset-test.BetterContinents");
    forPreset.Save(path);
    var loaded = BC.BetterContinentsSettings.Load(path);
    C(loaded.HasLayer && loaded.Layer.RecordsOfSource(1) == 0 && !loaded.Layer.HasRegistry && loaded.Layer.Id == kept.Id && loaded.GlobalScale == 1.5f, "a world made from the preset has the compiler's layer");
    // A GameTerrain world's preset is a GameTerrain preset.
    var gt = World(layer, gameTerrain: true).WithLayer(kept);
    path = Path.Combine(Work, "preset-test-game-terrain.BetterContinents");
    gt.Save(path);
    var gtLoaded = BC.BetterContinentsSettings.Load(path);
    C(gtLoaded.GameTerrain && gtLoaded.HasLayer && gtLoaded.Layer.Id == kept.Id, "and a GameTerrain world's preset makes GameTerrain worlds");
    // Presets.Load (a new world from a preset file): the layer comes with it.
    var choice = new NewWorldBuild.Choice { PresetPath = path, Values = ConfigValues.Snapshot(Config(), []) };
    var fromPreset = Presets.Load(choice);
    C(fromPreset.GameTerrain && fromPreset.HasLayer && fromPreset.AltBiomes == null, "Presets.Load gives a new world the preset's layer (and a GameTerrain preset takes no alt-biome options)");
  }

  private static void ShareFileTest()
  {
    Section("share file: the settings package only, and a note for the player when the world has a layer");
    var net = SettingsBytes(World(MakeSample(43).Layer), true);
    var dir = Path.Combine(Work, "share");
    var without = WorldCacheShare.WriteExportFiles(net, "My World", "seed", dir);
    var with = WorldCacheShare.WriteExportFiles(net, "My World With", "seed", dir, hasLayer: true);
    var noteWithout = File.ReadAllText(without.SidecarPath);
    var noteWith = File.ReadAllText(with.SidecarPath);
    C(!noteWithout.Contains("baked placements") && noteWith.Contains("baked placements") && noteWith.Contains("when you join"), "the sidecar says the layer is downloaded when joining, when there is one");
    C(File.ReadAllBytes(with.FilePath).SequenceEqual(net) && with.Id == PackageId(net), "the file is the settings package as it is sent: its id is the one a joining player's cache check looks for");
    C(WorldCacheShare.LooksLikeSettingsPackageHeader(net.Take(4).ToArray()), "and it looks like a settings package");
  }
}

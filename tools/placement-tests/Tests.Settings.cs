// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The layer in the world's settings (spec 3, 4): DataKeys 70 and 71 on disk and in the network package, the package id that a change of the
// layer leaves alone, what Better Continents 0.10.3 does with them (the released DLL is loaded and asked), a world that keeps the game's own
// terrain (GameTerrain) and what is patched for it, a layer that cannot be read, the Directory, presets, the export folder and the share file.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // A world made the new way (settings version 12), with a few settings that make its package more than a version.
  public static BC.BetterContinentsSettings World(BakedLayer layer = null, bool gameTerrain = false, bool wide = false)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, WorldSize = 12000f, EdgeSize = 600f };
    if (!gameTerrain)
    {
      s.GlobalScale = 1.5f;
      s.OceanChannelsEnabled = false;
      s.ForestScale = 2f;
    }
    s.GameTerrain = gameTerrain;
    s.WideSectors = wide;
    s.Layer = layer;
    return s;
  }

  public static byte[] SettingsBytes(BC.BetterContinentsSettings s, bool network, bool alt = true)
  {
    var pkg = new ZPackage();
    s.Serialize(pkg, network, alt, 12);
    return pkg.GetArray();
  }

  public static string PackageId(byte[] bytes) => BC.ZNetPatch.WorldCache.PackageID(bytes, bytes.Length);

  // The package's bytes from the start of a key's two ints on: the keys 70 and 71 as they sit in a package.
  private static int IndexOfKey(byte[] package, int key, int from = 4)
  {
    var wanted = BitConverter.GetBytes(key);
    for (int i = from; i + 4 <= package.Length; i++)
      if (package[i] == wanted[0] && package[i + 1] == wanted[1] && package[i + 2] == wanted[2] && package[i + 3] == wanted[3])
        return i;
    return -1;
  }

  private static void SettingsLayerTest()
  {
    Section("settings: the layer on disk and in the package sent to players (keys 70 and 71)");
    var sample = MakeSample(31);
    var layer = sample.Layer;
    var other = MakeSample(32).Layer;

    var plain = World();
    var plainDisk = SettingsBytes(plain, false);
    var plainNet = SettingsBytes(plain, true);
    C(IndexOfKey(plainDisk, 70, 4) < 0 && plainDisk.Length < 100, "a world without a layer writes no key 70 or 71: " + plainDisk.Length + " bytes");

    // On disk: the key, then the layer's bytes as one byte array, last.
    var withLayer = World(layer);
    var disk = SettingsBytes(withLayer, false);
    C(disk.Length == plainDisk.Length + 4 + 4 + layer.Length, $"a layer adds a key, a length and its {layer.Length:N0} bytes");
    C(disk.Take(plainDisk.Length).SequenceEqual(plainDisk), "after every key a world without one writes, so those bytes are as they were");
    int at = plainDisk.Length;
    C(BitConverter.ToInt32(disk, at) == 70 && BitConverter.ToInt32(disk, at + 4) == layer.Length && disk.Skip(at + 8).SequenceEqual(layer.Bytes.Take(layer.Length)), "key 70, a length, the layer's bytes");
    var back = BC.BetterContinentsSettings.Load(new ZPackage(disk));
    C(back.EnabledForThisWorld && back.HasLayer && !back.LayerSentApart && back.Layer.Id == layer.Id && back.LayerError == null && back.GlobalScale == 1.5f && back.Version == 12,
      "read back: the layer is the same layer, and the rest of the settings are as they were");
    C(SettingsBytes(back, false).SequenceEqual(disk), "and writes the same bytes again");

    // In the package sent to players: the key and an empty byte array.
    var net = SettingsBytes(withLayer, true);
    C(net.Length == plainNet.Length + 8 && net.Take(plainNet.Length).SequenceEqual(plainNet), "the network package carries the key and an empty block (8 bytes more than without a layer)");
    C(BitConverter.ToInt32(net, net.Length - 8) == 70 && BitConverter.ToInt32(net, net.Length - 4) == 0, "an empty block: this world has a layer, sent apart");
    var client = BC.BetterContinentsSettings.Load(new ZPackage(net));
    C(client.EnabledForThisWorld && !client.HasLayer && client.LayerSentApart && client.LayerError == null, "a client reads it as a world whose layer comes apart");
    C(SettingsBytes(client, true).SequenceEqual(net), "and sends the same package on");
    // The package id: a change of the layer leaves it alone.
    var idWith = PackageId(net);
    C(idWith == PackageId(SettingsBytes(World(other), true)) && idWith == PackageId(SettingsBytes(client, true)), "the package id does not change when only the layer changes");
    C(idWith != PackageId(plainNet), "the id of a world with a layer is not that of one without");
    // The edit of a running world does not touch it either.
    var edit = layer.Edit();
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    withLayer.Layer = edit.Build().Layer;
    C(PackageId(SettingsBytes(withLayer, true)) == idWith, "nor does a bake");
    C(SettingsBytes(withLayer, false).Length != disk.Length, "but the file the world saves has the new layer");

    // The biome cache's fingerprint leaves the layer out.
    C(SettingsBytes(withLayer, true, false).SequenceEqual(SettingsBytes(plain, true, false)), "the biome cache's fingerprint is the same with or without a layer (no key 70)");

    // Whatever Override version says, a layer is written in the keyed format.
    var pkg = new ZPackage();
    withLayer.Serialize(pkg, false, true, 10);
    C(BitConverter.ToInt32(pkg.GetArray(), 0) == 11 && BC.BetterContinentsSettings.Load(new ZPackage(pkg.GetArray())).HasLayer, "a layer is never written in a legacy layout (version 10 becomes 11)");

    // Dump says what is there.
    var lines = new List<string>();
    back.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Baked layer: revision 1,")) && lines.Any(l => l.StartsWith("Baked records:")), "bc info lists the layer");
    lines.Clear();
    client.Dump(lines.Add);
    C(lines.Any(l => l.Contains("sent apart")), "and a layer on its way");
    lines.Clear();
    plain.Dump(lines.Add);
    C(!lines.Any(l => l.Contains("Baked")), "and nothing for a world without one");
  }

  private static void SettingsGameTerrainTest()
  {
    Section("settings: a world that keeps the game's own terrain (key 71)");
    var layer = MakeSample(33).Layer;
    var world = World(layer, gameTerrain: true);
    var disk = SettingsBytes(world, false);
    // The version, key 71, key 70 and the layer: nothing else.
    C(BitConverter.ToInt32(disk, 0) == 12 && BitConverter.ToInt32(disk, 4) == 71 && BitConverter.ToInt32(disk, 8) == 70 && disk.Length == 4 + 4 + 4 + 4 + layer.Length,
      "a GameTerrain world writes its version, key 71, key 70 and the layer, and nothing else");
    var back = BC.BetterContinentsSettings.Load(new ZPackage(disk));
    C(back.EnabledForThisWorld && back.GameTerrain && back.ShapesWorld == false && back.HasLayer && back.Layer.Id == layer.Id, "read back: a Better Continents world that does not shape its terrain, with its layer");
    C(SettingsBytes(back, false).SequenceEqual(disk), "and written again as it was");
    var net = SettingsBytes(world, true);
    C(BitConverter.ToInt32(net, 4) == 71 && BitConverter.ToInt32(net, 8) == 70 && BitConverter.ToInt32(net, 12) == 0 && net.Length == 16, "the network package: version, key 71, key 70, an empty block");
    var wide = World(layer, gameTerrain: true, wide: true);
    var wideDisk = SettingsBytes(wide, false);
    C(BitConverter.ToInt32(wideDisk, 4) == 68 && BitConverter.ToInt32(wideDisk, 8) == 71 && BitConverter.ToInt32(wideDisk, 12) == 70, "with wide sectors: key 68, then 71, then 70");
    C(BC.BetterContinentsSettings.Load(new ZPackage(wideDisk)).WideSectors, "wide sectors are read back");
    // Its other fields mean nothing: they are not written, whatever they hold.
    var noisy = World(layer, gameTerrain: true);
    noisy.GlobalScale = 3f;
    noisy.RiversEnabled = false;
    noisy.ForestScale = 4f;
    noisy.AshlandsGapEnabled = true;
    noisy.BiomePrecision = 5;
    C(SettingsBytes(noisy, false).SequenceEqual(disk), "so a GameTerrain world's other settings cannot change what it saves");
    var legacy = new ZPackage();
    world.Serialize(legacy, false, true, 10);
    C(BitConverter.ToInt32(legacy.GetArray(), 0) == 12, "it is always the newest version, whatever Override version says");
    var lines = new List<string>();
    back.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Game terrain:")) && lines.Any(l => l.StartsWith("Baked layer:")) && !lines.Any(l => l.StartsWith("Continent size")), "bc info says so, and lists no map");
  }

  // The toggles a world wants (Patcher.WantedToggles): a GameTerrain world only the layer's.
  private static void SettingsToggleTest()
  {
    Section("settings: what a world wants patched (Patcher.WantedToggles)");
    var groundLayer = MakeSample(34).Layer;
    var noGround = LayerEdit.New("none");
    noGround.PaletteIndexFor(Beam());
    noGround.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    var plainLayer = noGround.Build().Layer;
    const string Mask = "ZoneSystem.InsideClearArea, baked vegetation mask";
    const string Ground = "WorldGenerator.GetBiomeHeight, baked ground";

    // A world that is no longer a Better Continents world (the main menu) wants nothing.
    var menu = World(groundLayer);
    menu.EnabledForThisWorld = false;
    C(!BC.WantedToggles(menu).Any(), "the main menu's settings want nothing, a layer or not");

    // A GameTerrain world wants the layer's toggles only, whatever its other fields hold.
    var game = World(groundLayer, gameTerrain: true);
    game.RiversEnabled = false;
    game.ForestScale = 3f;
    game.AshlandsGapEnabled = false;
    game.DeepNorthGapEnabled = false;
    game.HighTerrainMode = HighTerrainMode.On;
    game.BiomePrecision = 8;
    C(BC.WantedToggles(game).OrderBy(x => x).SequenceEqual(new[] { Ground, Mask }.OrderBy(x => x)), "a GameTerrain world with ground wants the vegetation mask and the ground, and nothing else: " + string.Join(" | ", BC.WantedToggles(game)));
    C(BC.WantedToggles(World(plainLayer, gameTerrain: true)).SequenceEqual(new[] { Mask }), "without ground in its layer, the vegetation mask only");
    C(!BC.WantedToggles(World(null, gameTerrain: true)).Any(), "a GameTerrain world without a layer wants nothing");

    // The same fields in a world that shapes its terrain: every toggle that follows from them, plus the layer's.
    var shaped = World(groundLayer);
    shaped.RiversEnabled = false;
    shaped.ForestScale = 3f;
    var wanted = BC.WantedToggles(shaped).ToList();
    C(wanted.Contains(Mask) && wanted.Contains(Ground) && wanted.Contains("WorldGenerator.AddRivers") && wanted.Contains("WorldGenerator.GetForestFactor prefix")
      && wanted.Contains("ZoneSystem.PlaceVegetation (vegetation map, twin guard)"), "a world that shapes its terrain wants those toggles and the layer's: " + string.Join(" | ", wanted));
    C(BC.WantedToggles(World(plainLayer)).Contains(Mask) && !BC.WantedToggles(World(plainLayer)).Contains(Ground), "the ground's toggle follows the layer's ground");
    C(!BC.WantedToggles(World()).Contains(Mask), "no layer, no mask");

    // What else follows ShapesWorld.
    C(BC.MapGeometry(game).SameAs(WorldGeometry.Vanilla) && BC.LayoutGeometry(game).SameAs(WorldGeometry.Vanilla), "a GameTerrain world's maps and layout have the game's size");
    C(BC.MapGeometry(shaped).SameAs(new WorldGeometry(12000f, 600f)) && BC.LayoutGeometry(shaped).SameAs(new WorldGeometry(12000f, 600f)), "a shaped world's have its own (control)");
    C(BC.EffectiveBiomePrecision(game) == 0 && BC.EffectiveBiomePrecision(shaped) == 0 == (shaped.BiomePrecision == 0), "biome precision is not applied to a GameTerrain world");
    var precise = World(groundLayer);
    precise.BiomePrecision = 3;
    C(BC.EffectiveBiomePrecision(precise) == 3, "though it is to a shaped one (control)");
    var highShaped = World(groundLayer);
    highShaped.HighTerrainMode = HighTerrainMode.On;
    C(!HighTerrain.Wanted(game) && HighTerrain.MaxMetres(game) == 0f && HighTerrain.Wanted(highShaped), "High Terrain is not wanted by a GameTerrain world, even On (a shaped one with On is: control)");
    C(!BC.DeepNorthWeatherUsesZ(game), "the Deep North weather test stays the game's");
    var big = World(groundLayer, gameTerrain: true);
    big.WorldSize = 40000f;
    C(WorldSectors.Reach(big, null) <= WorldSectors.VanillaReach && WorldSectors.Reach(shapedBig(), null) > WorldSectors.VanillaReach, "a GameTerrain world's reach is the game's own, whatever its settings' size holds");
    BC.BetterContinentsSettings shapedBig()
    {
      var s = World(groundLayer);
      s.WorldSize = 40000f;
      return s;
    }
    C(WorldSectors.Reach(big, new WorldGeometry(40000f, 500f)) > WorldSectors.VanillaReach, "unless Expand World Size's is larger");
    BC.Settings = game;
    C(!BC.AllowDebugActions, "no debug actions in a GameTerrain world (they need ZNet, so false either way here)");
    BC.Settings = new BC.BetterContinentsSettings();
  }

  // A layer the settings cannot read is kept as it is, and the world load stops.
  private static void SettingsUnreadableLayerTest()
  {
    Section("settings: a layer that cannot be read is kept as it is, and the load stops");
    var layer = MakeSample(35).Layer;
    var damaged = (byte[])layer.Bytes.Take(layer.Length).ToArray().Clone();
    damaged[damaged.Length / 2] ^= 0x55;
    var good = World(layer);
    var disk = SettingsBytes(good, false);
    int at = disk.Length - layer.Length;
    var corrupt = (byte[])disk.Clone();
    corrupt[at + layer.Length / 2] ^= 0x55;
    List<string> log = null;
    BC.BetterContinentsSettings read = null;
    log = LogHandler.During(() => read = BC.BetterContinentsSettings.Load(new ZPackage(corrupt)));
    C(read.EnabledForThisWorld && !read.HasLayer && read.LayerError != null && read.LayerError.Contains("CRC"), "a damaged layer: the world is still read, with an error named: " + read.LayerError);
    C(log.Any(l => l.Contains("Failed to read the baked layer")), "and logged");
    C(SettingsBytes(read, false).SequenceEqual(corrupt), "its bytes are saved back as they were");
    // A layer a newer Better Continents made: its header bit.
    var newer = (byte[])layer.Bytes.Take(layer.Length).ToArray().Clone();
    newer[6] |= 16;
    Recrc(newer, newer.Length);
    var newerPkg = SettingsBytes(World(), false).Concat(BitConverter.GetBytes(70)).Concat(BitConverter.GetBytes(newer.Length)).Concat(newer).ToArray();
    var readNewer = BC.BetterContinentsSettings.Load(new ZPackage(newerPkg));
    C(readNewer.LayerError != null && readNewer.LayerError.Contains("newer Better Continents") && SettingsBytes(readNewer, false).SequenceEqual(newerPkg), "a layer from a newer Better Continents is refused by name, and kept");
    // The world load stops, as it does for an alt-biome map that cannot be read.
    BC.Settings = read;
    string message = "";
    try
    {
      BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "damaged layer" });
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    C(message.Contains("reading the baked layer failed") && message.Contains("cannot be read") && message.Contains("newer Better Continents"), "loading the world stops, and says what to do: " + message);
    ZNet.m_loadError = false;
    BC.Settings = World(layer);
    bool threw = false;
    try
    {
      BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "fine layer" });
    }
    catch (BC.AltBiomeLoadException)
    {
      threw = true;
    }
    C(!threw, "a world whose layer reads loads");
    // A GameTerrain world's layer stops the load too.
    var gt = World(layer, gameTerrain: true);
    var gtDisk = SettingsBytes(gt, false);
    gtDisk[gtDisk.Length - layer.Length / 2] ^= 0x55;
    BC.Settings = BC.BetterContinentsSettings.Load(new ZPackage(gtDisk));
    message = "";
    try
    {
      BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "damaged game terrain" });
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    C(message.Contains("reading the baked layer failed"), "and so does a GameTerrain world's");
    ZNet.m_loadError = false;
    BC.Settings = new BC.BetterContinentsSettings();
  }

  // Better Continents 0.10.3 (the released DLL) asked to read the packages: it stops at key 70 and at key 71, as U7 says, and reads a world without a layer
  // exactly as it always did.
  private static void SettingsOldReaderTest()
  {
    Section("settings: Better Continents 0.10.3 reads a package with key 70 or 71 as unknown");
    var dll = Released("0.10.3");
    if (dll == null)
      return;
    var context = new ReleasedContext(dll);
    try
    {
      var assembly = context.LoadFromAssemblyPath(dll);
      var settingsType = assembly.GetType("BetterContinents.BetterContinents+BetterContinentsSettings", true);
      var load = settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ZPackage) }, null);
      var enabled = settingsType.GetField("EnabledForThisWorld");
      var layer = MakeSample(36).Layer;

      (bool Enabled, List<string> Log, object Settings) Ask(byte[] package)
      {
        object settings = null;
        var log = LogHandler.During(() => settings = load.Invoke(null, new object[] { new ZPackage(package) }));
        return ((bool)enabled.GetValue(settings), log, settings);
      }

      var plain = Ask(SettingsBytes(World(), false));
      C(plain.Enabled && !plain.Log.Any(l => l.Contains("Unknown feature")), "0.10.3 reads a world without a layer");
      var withLayer = Ask(SettingsBytes(World(layer), false));
      C(!withLayer.Enabled && withLayer.Log.Any(l => l.Contains("Unknown feature: 70")), "a world with a layer: 0.10.3 stops at key 70 and treats the world as vanilla: " + string.Join(" | ", withLayer.Log));
      var network = Ask(SettingsBytes(World(layer), true));
      C(!network.Enabled && network.Log.Any(l => l.Contains("Unknown feature: 70")), "so it does with the package sent to players");
      var game = Ask(SettingsBytes(World(layer, gameTerrain: true), false));
      C(!game.Enabled && game.Log.Any(l => l.Contains("Unknown feature: 71")), "a GameTerrain world: 0.10.3 stops at key 71");
      var wide = Ask(SettingsBytes(World(layer, gameTerrain: true, wide: true), false));
      C(!wide.Enabled && wide.Log.Any(l => l.Contains("Unknown feature: 71")), "with wide sectors, it reads key 68 and stops at 71");

      // A world without a layer or GameTerrain: what this build writes, 0.10.3 reads and writes again byte for byte.
      var serialize = settingsType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
      byte[] Again(object settings, bool net)
      {
        var pkg = new ZPackage();
        serialize.Invoke(settings, new object[] { pkg, net, true, (int?)12 });
        return pkg.GetArray();
      }
      var disk = SettingsBytes(World(), false);
      var net2 = SettingsBytes(World(), true);
      C(Again(Ask(disk).Settings, false).SequenceEqual(disk) && Again(Ask(net2).Settings, true).SequenceEqual(net2), "a world without a layer is read and written by 0.10.3 as the same bytes (nothing of this build is in them)");
    }
    finally
    {
      context.Unload();
    }
  }

  private sealed class ReleasedContext(string path) : AssemblyLoadContext("BetterContinents " + Path.GetFileName(path), isCollectible: true)
  {
    protected override Assembly Load(AssemblyName name) => name.Name == "BetterContinents" ? LoadFromAssemblyPath(path) : null;
  }

  // The plugin DLL of a released version, taken out of its package into the run's folder: from BC_RELEASES, the repository's HexiumDist, or the main
  // checkout's beside it. Null (and a note) when none is on this machine.
  public static string Released(string version)
  {
    var name = $"BetterContinents-v{version}-hexium.zip";
    var places = new List<string>();
    var env = Environment.GetEnvironmentVariable("BC_RELEASES");
    if (env != null)
      places.Add(Path.Combine(env, name));
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      if (File.Exists(Path.Combine(dir.FullName, "BetterContinents.csproj")))
      {
        places.Add(Path.Combine(dir.FullName, "HexiumDist", name));
        places.Add(Path.Combine(dir.Parent.FullName, "BetterContinents10", "HexiumDist", name));
        break;
      }
    var zip = places.FirstOrDefault(File.Exists);
    if (zip == null)
    {
      if (Environment.GetEnvironmentVariable("BC_REQUIRE_RELEASES") == "1")
        C(false, $"the released {version} package is not on this machine (looked in {string.Join(", ", places)})");
      else
        System.Console.WriteLine($"SKIPPED: the released {version} package is not on this machine (BC_RELEASES names a folder with {name})");
      return null;
    }
    var target = Path.Combine(Work, "released-" + version);
    Directory.CreateDirectory(target);
    var dll = Path.Combine(target, "BetterContinents.dll");
    if (!File.Exists(dll))
    {
      using var archive = ZipFile.OpenRead(zip);
      var entry = archive.Entries.First(e => e.FullName == "plugins/BetterContinents.dll");
      entry.ExtractToFile(dll);
    }
    return dll;
  }
}

// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
//
// The lakes and the clearing mask in the world's settings (BUILD-SPEC-TARN.md 2.1): DataKeys 72 and 73 on disk and in the package sent to players, the biome cache's
// fingerprint that leaves them out, a world that keeps the game's own terrain, a legacy layout, the blobs this build cannot read (kept as they were, the world still
// loads, a mask that does not read stops the load), what the released Better Continents does with a package that has them, the toggle of the clearing mask on the
// game's own method, and Tarn being installed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  public static LakeSet SampleLakes(int count = 3, float scale = 0.7f)
  {
    var rng = new System.Random(77);
    return new LakeSet(Enumerable.Range(0, count).Select(i => RandomLake(rng, i)).ToList(), scale, 0);
  }

  public static ClearMask SampleMask() => ClearMask.FromTarnFile(File.ReadAllBytes(Fixture("clear37.bin")));

  // A world made the new way (settings version 12), with a few settings that make its package more than a version.
  public static BC.BetterContinentsSettings World(LakeSet lakes = null, ClearMask mask = null, bool gameTerrain = false, bool wide = false, BakedLayer layer = null)
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
    s.Lakes = lakes;
    s.Clearing = mask;
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

  public static BakedLayer SampleLayer()
  {
    var edit = LayerEdit.New("lake test");
    edit.PaletteIndexFor(new PaletteEntry([new Candidate("stone_wall_2x1", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab));
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    return edit.Build().Layer;
  }

  private static int KeyAt(byte[] package, int at) => BitConverter.ToInt32(package, at);

  // The package bytes from this offset: key, length, blob, as WriteLakeKeys writes them.
  private static void ExpectKeys(byte[] package, int at, string what, params (int Key, byte[] Blob)[] keys)
  {
    foreach (var (key, blob) in keys)
    {
      C(at + 8 + blob.Length <= package.Length && KeyAt(package, at) == key && KeyAt(package, at + 4) == blob.Length && package.Skip(at + 8).Take(blob.Length).SequenceEqual(blob), $"{what}: key {key}, a length of {blob.Length:N0}, the blob");
      at += 8 + blob.Length;
    }
    C(at == package.Length, $"{what}: nothing follows the last key ({at} of {package.Length})");
  }

  private static void SettingsLakeKeysTest()
  {
    Section("settings: the lakes and the clearing mask on disk and in the package sent to players (keys 72 and 73)");
    var lakes = SampleLakes();
    var mask = SampleMask();
    var lakeBlob = lakes.ToBlock();
    var maskBlob = mask.ToBlock();

    var plain = World();
    var plainDisk = SettingsBytes(plain, false);
    var plainNet = SettingsBytes(plain, true);
    C(!plain.HasLakes && !plain.HasClearing && plainDisk.Length < 100 && plainDisk.Length == SettingsBytes(World(), false).Length, "a world without lakes or a mask writes neither key: " + plainDisk.Length + " bytes");
    for (int at = 4; at + 4 <= plainDisk.Length; at++)
      if (KeyAt(plainDisk, at) is 72 or 73)
        C(false, "a plain world holds key 72 or 73 at " + at + " (a float can look like one: check the bytes)");

    var both = World(lakes, mask);
    var disk = SettingsBytes(both, false);
    C(both.HasLakes && both.HasClearing && disk.Take(plainDisk.Length).SequenceEqual(plainDisk), "after every key a world without them writes, so those bytes are as they were");
    ExpectKeys(disk, plainDisk.Length, "on disk", (72, lakeBlob), (73, maskBlob));
    var net = SettingsBytes(both, true);
    C(net.Take(plainNet.Length).SequenceEqual(plainNet), "the network package starts as a plain world's");
    ExpectKeys(net, plainNet.Length, "in the network package", (72, lakeBlob), (73, maskBlob));
    var lakesOnly = SettingsBytes(World(lakes), false);
    ExpectKeys(lakesOnly, plainDisk.Length, "lakes alone", (72, lakeBlob));
    var maskOnly = SettingsBytes(World(null, mask), false);
    ExpectKeys(maskOnly, plainDisk.Length, "a mask alone", (73, maskBlob));

    var back = BC.BetterContinentsSettings.Load(new ZPackage(disk));
    C(back.EnabledForThisWorld && back.Lakes != null && back.Lakes.Lakes.SequenceEqual(lakes.Lakes) && back.Lakes.DepthScale == 0.7f && back.Clearing is { Size: 37, Extent: 123.5f } && back.LakesError == null
      && back.ClearingError == null && back.GlobalScale == 1.5f && back.Version == 12, "read back: the same lakes and depth scale, the mask's size and extent, and the rest of the settings");
    C(SettingsBytes(back, false).SequenceEqual(disk), "and written again as the same bytes (the mask's blob is kept, not deflated again)");
    var client = BC.BetterContinentsSettings.Load(new ZPackage(net));
    C(client.HasLakes && client.HasClearing && !client.Clearing.Decoded && SettingsBytes(client, true).SequenceEqual(net), "a client reads the package, does not open the mask's bits, and sends the same package on");

    // The package id: stable, and a change of the lakes changes it.
    var id = PackageId(net);
    C(id == PackageId(SettingsBytes(World(SampleLakes(), SampleMask()), true)) && id == PackageId(SettingsBytes(client, true)), "the package id is the same for the same lakes and mask, made again or read back");
    var moved = new LakeSet(lakes.Lakes.Select((l, i) => i == 0 ? l with { Level = l.Level + 1f } : l).ToList(), 0.7f, 1);
    C(id != PackageId(SettingsBytes(World(moved, mask), true)) && id != PackageId(plainNet), "and it is not that of a world with other lakes, or none");
    // The biome cache's fingerprint leaves them out.
    C(SettingsBytes(both, true, false).SequenceEqual(SettingsBytes(plain, true, false)), "the biome cache's fingerprint is the same with or without them (no key 72 or 73)");

    // After the layer's keys.
    var layer = SampleLayer();
    var withLayer = SettingsBytes(World(lakes, mask, layer: layer), false);
    var layerOnly = SettingsBytes(World(layer: layer), false);
    C(withLayer.Take(layerOnly.Length).SequenceEqual(layerOnly), "with a baked layer: the layer's key 70 and its bytes come first");
    ExpectKeys(withLayer, layerOnly.Length, "after the layer", (72, lakeBlob), (73, maskBlob));
    var layerNet = SettingsBytes(World(lakes, mask, layer: layer), true);
    C(layerNet.Length == SettingsBytes(World(layer: layer), true).Length + 16 + lakeBlob.Length + maskBlob.Length, "in the network package the layer is sent apart (an empty block), and the lakes and the mask are in it");

    // A world that keeps the game's own terrain carries them.
    var game = SettingsBytes(World(lakes, mask, gameTerrain: true), false);
    C(KeyAt(game, 0) == 12 && KeyAt(game, 4) == 71, "a GameTerrain world: its version, then key 71");
    ExpectKeys(game, 8, "a GameTerrain world", (72, lakeBlob), (73, maskBlob));
    var gameWide = SettingsBytes(World(lakes, mask, gameTerrain: true, wide: true), false);
    C(KeyAt(gameWide, 4) == 68 && KeyAt(gameWide, 8) == 71, "with wide sectors: 68, then 71");
    ExpectKeys(gameWide, 12, "a wide GameTerrain world", (72, lakeBlob), (73, maskBlob));
    var gameBack = BC.BetterContinentsSettings.Load(new ZPackage(game));
    C(gameBack.GameTerrain && gameBack.HasLakes && gameBack.HasClearing && SettingsBytes(gameBack, false).SequenceEqual(game), "and reads them back");
    var noisy = World(lakes, mask, gameTerrain: true);
    noisy.GlobalScale = 3f;
    C(SettingsBytes(noisy, false).SequenceEqual(game), "its other fields still mean nothing: they are not written");

    // A legacy layout cannot hold keys, so a world with lakes is saved as 11.
    var legacy = new ZPackage();
    both.Serialize(legacy, false, true, 10);
    C(BitConverter.ToInt32(legacy.GetArray(), 0) == 11 && BC.BetterContinentsSettings.Load(new ZPackage(legacy.GetArray())).HasLakes, "a world with lakes is never written in a legacy layout (version 10 becomes 11)");
    var legacyPlain = new ZPackage();
    plain.Serialize(legacyPlain, false, true, 10);
    C(BitConverter.ToInt32(legacyPlain.GetArray(), 0) == 10, "a world without them is (control)");

    // An empty set (every lake taken out) is kept: the depth scale and the fact that the lakes were removed.
    var emptied = World(LakeSet.Empty.WithDepthScale(0.3f));
    var emptyDisk = SettingsBytes(emptied, false);
    ExpectKeys(emptyDisk, plainDisk.Length, "an empty set", (72, LakeSet.Empty.WithDepthScale(0.3f).ToBlock()));
    var emptyBack = BC.BetterContinentsSettings.Load(new ZPackage(emptyDisk));
    C(emptyBack.Lakes != null && emptyBack.Lakes.IsEmpty && emptyBack.Lakes.DepthScale == 0.3f && !emptyBack.HasLakes, "read back it has no lakes, remembers its depth scale, and is not 'a world with lakes'");

    // A set over the limit is refused when it is written.
    var tooMany = World(new LakeSet(Enumerable.Range(0, LakeSet.MaxLakes + 1).Select(i => lakes.Lakes[0]).ToList(), 0.5f, 0));
    C(RefusesLake(() => SettingsBytes(tooMany, false), "at most 5,000"), "a lake count over the limit is refused when the settings are written");

    // Dump says what is there.
    var lines = new List<string>();
    back.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Lakes: 3 lakes, depth scale 0.7")) && lines.Any(l => l.StartsWith("Clearing mask: 37 x 37 over +-124 m")), "bc info lists the lakes and the mask: " + string.Join(" | ", lines.Where(l => l.StartsWith("Lakes") || l.StartsWith("Clearing"))));
    lines.Clear();
    plain.Dump(lines.Add);
    C(!lines.Any(l => l.StartsWith("Lakes") || l.StartsWith("Clearing")), "and nothing for a world without them");
    lines.Clear();
    gameBack.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Lakes: 3")) && lines.Any(l => l.StartsWith("Clearing mask")), "a GameTerrain world says so too");
    // The mask's summary line, once its bits are open.
    back.Clearing.Verify();
    lines.Clear();
    back.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Clearing mask:") && l.Contains("km2 kept free of vegetation")), "with the bits open it says how many square kilometres are kept free: " + lines.FirstOrDefault(l => l.StartsWith("Clearing mask")));

    // WithLayer's copy shares them (the objects are immutable).
    var copy = both.WithLayer(layer);
    C(ReferenceEquals(copy.Lakes, both.Lakes) && ReferenceEquals(copy.Clearing, both.Clearing), "a copy with another layer shares the lakes and the mask");
    // Replace helpers.
    var edited = World(lakes, mask);
    edited.ReplaceLakes(null);
    edited.ReplaceClearing(null);
    C(!edited.HasLakes && !edited.HasClearing && SettingsBytes(edited, false).SequenceEqual(plainDisk), "taking both out gives the bytes of a plain world");
    var scaled = World();
    var set = scaled.SetLakeDepthScale(1.25f);
    C(scaled.Lakes == set && set.IsEmpty && set.DepthScale == 1.25f && scaled.EffectiveLakeDepthScale == 1.25f && SettingsBytes(scaled, false).Length > plainDisk.Length, "SetLakeDepthScale keeps the scale in the world as an empty set");
  }

  private static void SettingsUnreadableTest()
  {
    Section("settings: lakes and a mask this build cannot read are kept as they are");
    var lakes = SampleLakes();
    var mask = SampleMask();
    var plainDisk = SettingsBytes(World(), false);
    byte[] Package(byte[] lakeBlob, byte[] maskBlob)
    {
      var all = plainDisk.ToList();
      if (lakeBlob != null)
        all.AddRange(BitConverter.GetBytes(72).Concat(BitConverter.GetBytes(lakeBlob.Length)).Concat(lakeBlob));
      if (maskBlob != null)
        all.AddRange(BitConverter.GetBytes(73).Concat(BitConverter.GetBytes(maskBlob.Length)).Concat(maskBlob));
      return all.ToArray();
    }

    // Lakes of a newer format: the world loads without them, the key goes back unchanged, and the mask after it still reads.
    var newer = lakes.ToBlock();
    BitConverter.GetBytes(5).CopyTo(newer, 0);
    var pkg = Package(newer, mask.ToBlock());
    BC.BetterContinentsSettings read = null;
    var log = LogHandler.During(() => read = BC.BetterContinentsSettings.Load(new ZPackage(pkg)));
    C(read.EnabledForThisWorld && read.Lakes == null && !read.HasLakes && read.LakesError != null && read.LakesError.Contains("newer Better Continents") && read.HasClearing, "lakes of a newer format: the world still loads, with an error named, and the mask after them is read");
    C(log.Any(l => l.Contains("Failed to read the lakes")), "and it is logged");
    C(SettingsBytes(read, false).SequenceEqual(pkg), "its bytes are saved back as they were");
    C(SettingsBytes(read, true).SequenceEqual(pkg), "and sent on as they were");
    var lines = new List<string>();
    read.Dump(lines.Add);
    C(lines.Any(l => l.StartsWith("Lakes unreadable by this build")), "bc info says so");
    // A replaced set clears the kept bytes and the error.
    read.ReplaceLakes(lakes);
    C(read.LakesError == null && SettingsBytes(read, false).SequenceEqual(Package(lakes.ToBlock(), mask.ToBlock())), "an edit that replaces the lakes takes the old bytes out");

    // Damaged lakes (cut short).
    var cut = lakes.ToBlock().Take(40).ToArray();
    var cutRead = BC.BetterContinentsSettings.Load(new ZPackage(Package(cut, null)));
    C(cutRead.LakesError != null && cutRead.EnabledForThisWorld && SettingsBytes(cutRead, false).SequenceEqual(Package(cut, null)), "lakes cut short: an error, and the bytes kept");

    // A mask of a newer format.
    var newerMask = mask.ToBlock().ToArray();
    BitConverter.GetBytes(4).CopyTo(newerMask, 0);
    var maskPkg = Package(null, newerMask);
    var maskRead = BC.BetterContinentsSettings.Load(new ZPackage(maskPkg));
    C(maskRead.EnabledForThisWorld && maskRead.Clearing == null && !maskRead.HasClearing && maskRead.ClearingError != null && maskRead.ClearingError.Contains("newer Better Continents") && SettingsBytes(maskRead, false).SequenceEqual(maskPkg),
      "a mask of a newer format is refused by name and kept");
    var wanted = BC.WantedToggles(maskRead);
    C(!wanted.Contains("ZoneSystem.InsideClearArea, clearing mask"), "and nothing is patched for it");
    // The world load stops, as it does for a baked layer that cannot be read.
    BC.Settings = maskRead;
    string message = "";
    try
    {
      BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "newer mask" });
    }
    catch (BC.AltBiomeLoadException e)
    {
      message = e.Message;
    }
    C(message.Contains("reading the clearing mask failed") && message.Contains("newer Better Continents") && message.Contains("clearing mask stored"), "loading the world stops, and says what to do: " + message);
    ZNet.m_loadError = false;

    // A mask whose header reads and whose bits do not.
    var junk = mask.ToBlock().ToArray();
    for (int i = 20; i < junk.Length; i++)
      junk[i] = 0xFF;
    var junkRead = BC.BetterContinentsSettings.Load(new ZPackage(Package(null, junk)));
    C(junkRead.Clearing != null && junkRead.ClearingError == null && SettingsBytes(junkRead, false).SequenceEqual(Package(null, junk)), "a mask whose bits are damaged reads its header, and keeps its bytes");
    var runs = BakedTransfer.RunsWorld;
    try
    {
      BakedTransfer.RunsWorld = () => false;
      BC.Settings = junkRead;
      bool threw = false;
      try
      {
        BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "client of a damaged mask" });
      }
      catch (BC.AltBiomeLoadException)
      {
        threw = true;
      }
      C(!threw, "a client does not open the bits, so it is not stopped");
      BakedTransfer.RunsWorld = () => true;
      message = "";
      try
      {
        BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "damaged mask" });
      }
      catch (BC.AltBiomeLoadException e)
      {
        message = e.Message;
      }
      ZNet.m_loadError = false;
      C(message.Contains("reading the clearing mask failed") && junkRead.ClearingError != null, "the machine that runs the world opens them at the world load and is stopped: " + message);
      C(SettingsBytes(junkRead, false).SequenceEqual(Package(null, junk)), "and the damaged mask is still saved back as it was");
      // A good mask loads and says what it is.
      var good = BC.BetterContinentsSettings.Load(new ZPackage(Package(null, mask.ToBlock())));
      BC.Settings = good;
      var goodLog = LogHandler.During(() => BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "good mask" }));
      C(goodLog.Any(l => l.Contains("Clearing: mask 37 x 37 over +-124 m") && l.Contains("km2 kept free of vegetation")), "a good mask loads, and the log says: " + goodLog.FirstOrDefault(l => l.Contains("Clearing:")));
      // With Tarn installed the machine does not open it.
      TarnCompat.Installed = true;
      BC.Settings = junkRead;
      junkRead.ClearingError = null;
      bool tarnThrew = false;
      try
      {
        BC.AltBiomeControl.BeforeVerifyBiomeData(new World { m_name = "tarn" });
      }
      catch (BC.AltBiomeLoadException)
      {
        tarnThrew = true;
      }
      C(!tarnThrew, "while the Tarn mod is installed the mask is not opened: Tarn does the clearing");
    }
    finally
    {
      TarnCompat.Installed = false;
      BakedTransfer.RunsWorld = runs;
      ZNet.m_loadError = false;
      BC.Settings = new BC.BetterContinentsSettings();
    }
  }

  // The released Better Continents asked to read the packages: it stops at key 72 (and at 73 when it is the first), treats the world as vanilla, and reads a plain world
  // exactly as it always did.
  private static void SettingsOldReaderTest()
  {
    Section("settings: Better Continents 0.10.4 reads a package with key 72 or 73 as unknown");
    var dll = Released("0.10.4");
    if (dll == null)
      return;
    var context = new ReleasedContext(dll);
    try
    {
      var assembly = context.LoadFromAssemblyPath(dll);
      var settingsType = assembly.GetType("BetterContinents.BetterContinents+BetterContinentsSettings", true);
      var load = settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ZPackage) }, null);
      var enabled = settingsType.GetField("EnabledForThisWorld");
      (bool Enabled, List<string> Log, object Settings) Ask(byte[] package)
      {
        object settings = null;
        var log = LogHandler.During(() => settings = load.Invoke(null, new object[] { new ZPackage(package) }));
        return ((bool)enabled.GetValue(settings), log, settings);
      }

      var lakes = SampleLakes();
      var mask = SampleMask();
      var plain = Ask(SettingsBytes(World(), false));
      C(plain.Enabled && !plain.Log.Any(l => l.Contains("Unknown feature")), "0.10.4 reads a world without lakes");
      var withLakes = Ask(SettingsBytes(World(lakes), false));
      C(!withLakes.Enabled && withLakes.Log.Any(l => l.Contains("Unknown feature: 72")), "a world with lakes: 0.10.4 stops at key 72 and treats the world as vanilla: " + string.Join(" | ", withLakes.Log));
      var both = Ask(SettingsBytes(World(lakes, mask), false));
      C(!both.Enabled && both.Log.Any(l => l.Contains("Unknown feature: 72")), "with lakes and a mask it stops at 72, the first");
      var network = Ask(SettingsBytes(World(lakes, mask), true));
      C(!network.Enabled && network.Log.Any(l => l.Contains("Unknown feature: 72")), "so it does with the package sent to players");
      var maskOnly = Ask(SettingsBytes(World(null, mask), false));
      C(!maskOnly.Enabled && maskOnly.Log.Any(l => l.Contains("Unknown feature: 73")), "a world with only a mask: it stops at key 73");
      var game = Ask(SettingsBytes(World(lakes, mask, gameTerrain: true), false));
      C(!game.Enabled && game.Log.Any(l => l.Contains("Unknown feature: 72")), "a GameTerrain world: 0.10.4 reads key 71 and stops at 72");
      var layered = Ask(SettingsBytes(World(lakes, mask, layer: SampleLayer()), false));
      C(!layered.Enabled && layered.Log.Any(l => l.Contains("Unknown feature: 72")), "with a layer, it reads key 70 and stops at 72");
      // A world without them: what this build writes, 0.10.4 reads and writes again byte for byte.
      var serialize = settingsType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
      byte[] Again(object settings, bool net)
      {
        var pkg = new ZPackage();
        serialize.Invoke(settings, new object[] { pkg, net, true, (int?)12 });
        return pkg.GetArray();
      }
      var disk = SettingsBytes(World(), false);
      var net2 = SettingsBytes(World(), true);
      C(Again(Ask(disk).Settings, false).SequenceEqual(disk) && Again(Ask(net2).Settings, true).SequenceEqual(net2), "a world without them is read and written by 0.10.4 as the same bytes (nothing of this build is in them)");
    }
    finally
    {
      context.Unload();
    }
  }

  // ---- the clearing mask on the game's own method ------------------------------------------------------------------------------------------------

  private static MethodInfo InsideClearAreaMethod() => AccessTools.Method(typeof(ZoneSystem), "InsideClearArea");

  private static object[] ClearingToggles() => ((Array)typeof(BC).GetField("ClearingToggles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Cast<object>().ToArray();

  private static void UpdateToggle(object toggle, BC.BetterContinentsSettings s) => toggle.GetType().GetMethod("Update")!.Invoke(toggle, [s]);

  private static bool IsApplied(object toggle) => (bool)toggle.GetType().GetProperty("Applied")!.GetValue(toggle)!;

  private static IEnumerable<string> OurPostfixes(MethodBase target, Harmony harmony) =>
    Harmony.GetPatchInfo(target) is { } info ? info.Postfixes.Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod.Name) : [];

  private static void ClearingToggleTest()
  {
    Section("clearing: the toggle on ZoneSystem.InsideClearArea of the game's own IL");
    const string Name = "ZoneSystem.InsideClearArea, clearing mask";
    var mask = SampleMask();
    var layer = SampleLayer();
    var target = InsideClearAreaMethod();
    C(target != null && target.GetParameters().Length == 2 && target.GetParameters()[1].Name == "point" && target.ReturnType == typeof(bool), "the game's InsideClearArea(areas, point) is there, private, and answers a bool");
    var postfix = AccessTools.Method(typeof(ClearVegetation), nameof(ClearVegetation.InsideClearAreaPostfix));
    C(postfix != null && postfix.ReturnType == typeof(bool) && postfix.GetParameters().Select(p => p.Name).SequenceEqual(["__result", "point"]), "the postfix takes __result and the game's own point, and answers a bool (Harmony matches by name)");
    var toggles = ClearingToggles();
    C(toggles.Length == 1 && (string)toggles[0].GetType().GetField("Name")!.GetValue(toggles[0])! == Name, "one clearing toggle, named as above");
    C((bool)toggles[0].GetType().GetProperty("ViaProcessor")!.GetValue(toggles[0])! && toggles[0].GetType().GetProperty("FailureMessage")!.GetValue(toggles[0]) is string { Length: > 0 }, "patched through the processor, and a failure is logged and leaves the others");

    // Which worlds want it.
    TarnCompat.Installed = false;
    C(BC.WantedToggles(World(null, mask)).Contains(Name), "a world with a mask wants it");
    C(!BC.WantedToggles(World()).Contains(Name) && !BC.WantedToggles(World(SampleLakes())).Contains(Name), "a world without one, lakes or not, does not");
    C(BC.WantedToggles(World(null, mask, gameTerrain: true)).SequenceEqual([Name]), "a GameTerrain world with a mask wants it, and nothing else");
    var menu = World(null, mask);
    menu.EnabledForThisWorld = false;
    C(!BC.WantedToggles(menu).Any(), "the main menu's settings want nothing");
    C(BC.WantedToggles(World(null, mask, layer: layer)).Contains("ZoneSystem.InsideClearArea, baked vegetation mask") && BC.WantedToggles(World(null, mask, layer: layer)).Contains(Name), "a layer's vegetation mask and the clearing mask are both wanted, side by side");
    TarnCompat.Installed = true;
    C(!BC.WantedToggles(World(null, mask)).Contains(Name), "while the Tarn mod is installed it is not wanted (the data is kept)");
    TarnCompat.Installed = false;

    // Patched for real, and asked through the game's method.
    var saved = (BC.Settings, BC.HarmonyInstance);
    var harmony = new Harmony("lake-tests.clearing");
    BC.HarmonyInstance = harmony;
    var runs = BakedTransfer.RunsWorld;
    try
    {
      var world = World(null, mask);
      UpdateToggle(toggles[0], world);
      C(IsApplied(toggles[0]) && OurPostfixes(target, harmony).SequenceEqual(["InsideClearAreaPostfix"]), "switched on for a world with a mask: one postfix of ours on the game's method");
      BC.Settings = world;
      var zoneSystem = (ZoneSystem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZoneSystem));
      var areaType = typeof(ZoneSystem).GetNestedType("ClearArea", BindingFlags.NonPublic)!;
      var none = Activator.CreateInstance(typeof(List<>).MakeGenericType(areaType))!;
      bool Ask(object areas, float x, float z) => (bool)target.Invoke(zoneSystem, [areas, new Vector3(x, 30f, z)])!;
      var covered = mask.CentreOf(0, 0);
      float mpx = mask.MetresPerPixel;
      C(Ask(none, covered.X, covered.Z) && Ask(none, covered.X + mpx * 0.4f, covered.Z - mpx * 0.4f), "a spot inside a marked pixel is inside a clear area: the game's own method answers yes with no area in its list");
      C(Ask(none, covered.X + mpx * 3f, covered.Z) == mask.Covers(covered.X + mpx * 3f, covered.Z), "and a spot two pixels over is as the mask says (the game's method and Covers agree)");
      var clear = mask.CentreOf(1, 36);
      C(!mask.Covers(clear.X, clear.Z) == !Ask(none, clear.X, clear.Z), "the method and Covers agree on a spot that is not marked");
      // The game's own areas (a location's) still count: the answer is the OR.
      var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(areaType))!;
      var area = Activator.CreateInstance(areaType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [new Vector3(1000f, 0f, 1000f), 5f], null)!;
      list.GetType().GetMethod("Add")!.Invoke(list, [area]);
      C(Ask(list, 1001f, 1001f) && !Ask(list, 1100f, 1100f), "a clear area of the game's own still clears (the answers are ORed), and the mask clears nothing elsewhere");
      // A world without the mask: removed again.
      UpdateToggle(toggles[0], World());
      C(!IsApplied(toggles[0]) && !OurPostfixes(target, harmony).Any(), "for a world without a mask the postfix is removed again");
      C(!Ask(none, covered.X, covered.Z), "and the game's method answers as the game does");
      // The first spot kept clear is logged once per mask.
      ClearVegetation.Forget();
      UpdateToggle(toggles[0], world);
      BC.Settings = world;
      var lines = LogHandler.During(() =>
      {
        for (int i = 0; i < 5; i++)
          Ask(none, covered.X, covered.Z);
      });
      C(lines.Count(l => l.Contains("Clearing: the first spot kept clear")) == 1, "the first spot kept clear is logged, once: " + string.Join(" | ", lines.Where(l => l.Contains("Clearing:"))));
      // A mask whose bits do not open: once in the log, no more, and vegetation is placed as the game places it.
      var junk = mask.ToBlock().ToArray();
      for (int i = 20; i < junk.Length; i++)
        junk[i] = 0xFF;
      var damaged = World(null, ClearMask.FromBlock(junk));
      BC.Settings = damaged;
      ClearVegetation.Forget();
      var failLines = LogHandler.During(() =>
      {
        for (int i = 0; i < 4; i++)
          Ask(none, covered.X, covered.Z);
      });
      C(failLines.Count(l => l.Contains("the mask cannot be read")) == 1 && !Ask(none, covered.X, covered.Z), "a mask that does not open is said once and clears nothing: " + string.Join(" | ", failLines.Where(l => l.Contains("Clearing:"))));
    }
    finally
    {
      foreach (var toggle in toggles)
        UpdateToggle(toggle, World());
      harmony.UnpatchAll(harmony.Id);
      BC.Settings = saved.Settings;
      BC.HarmonyInstance = saved.HarmonyInstance;
      BakedTransfer.RunsWorld = runs;
      ClearVegetation.Forget();
    }
  }
}

internal static partial class Tests
{
  // ---- the Tarn mod installed ----------------------------------------------------------------------------------------------------------------------

  private static void TarnPresentTest()
  {
    Section("Tarn: the standalone mod installed beside Better Continents (BUILD-SPEC-TARN.md 5.3)");
    // BepInEx's plugin list cannot exist outside the game: Detect is the seam. A stub plugin under Tarn's GUID is "in the list" for the second half.
    var plugins = new HashSet<string>();
    var detectOld = TarnCompat.Detect;
    TarnCompat.Detect = () => plugins.Contains(TarnCompat.GUID);
    TarnCompat.Run();
    C(!TarnCompat.Installed && TarnCompat.Refusal == null, "without it nothing is stepped aside");
    // A stub registered in the plugin list under Tarn's GUID.
    plugins.Add(TarnCompat.GUID);
    var shown = new List<string>();
    var showOld = TarnCompat.Show;
    try
    {
      var log = LogHandler.During(TarnCompat.Run);
      C(TarnCompat.Installed && TarnCompat.Refusal == TarnCompat.Message && log.Any(l => l.Contains("The Tarn mod is installed") && l.Contains("the data is kept")), "with Tarn's GUID in the plugin list it is found, and the log says so: " + string.Join(" | ", log));
      C(TarnCompat.Message == "Better Continents' lakes are off while the Tarn mod is installed. Remove Tarn to use Better Continents' own.", "the message is the spec's");
      // The data is kept, the toggles are off.
      var world = World(SampleLakes(), SampleMask());
      var bytes = SettingsBytes(world, false);
      C(!BC.WantedToggles(world).Contains("ZoneSystem.InsideClearArea, clearing mask") && BC.BetterContinentsSettings.Load(new ZPackage(bytes)).HasLakes && SettingsBytes(BC.BetterContinentsSettings.Load(new ZPackage(bytes)), false).SequenceEqual(bytes),
        "the clearing toggle is off, and the lakes and the mask are still read, kept and saved as they were");
      var lines = new List<string>();
      world.Dump(lines.Add);
      C(lines.Any(l => l.Contains("Lakes and clearing: off, the Tarn mod is installed")), "bc info says so");
      // One message a session, when a player is in a world that carries something.
      TarnCompat.ForgetTold();
      TarnCompat.Show = text =>
      {
        shown.Add(text);
        return true;
      };
      C(!TarnCompat.TellOnce(World()) && shown.Count == 0, "a world with no lakes and no mask hears nothing");
      var off = World(SampleLakes(), null);
      off.EnabledForThisWorld = false;
      C(!TarnCompat.TellOnce(off) && shown.Count == 0, "nor does the main menu's");
      C(TarnCompat.TellOnce(world) && shown.SequenceEqual([TarnCompat.Message]), "a world with lakes or a mask is told on the screen");
      C(!TarnCompat.TellOnce(world) && shown.Count == 1, "once per session");
      TarnCompat.ForgetTold();
      TarnCompat.Show = _ => false;
      C(!TarnCompat.TellOnce(world), "when there is no screen to show it on, nothing is said, and it is tried again at the next spawn");
      TarnCompat.Show = text =>
      {
        shown.Add(text);
        return true;
      };
      C(TarnCompat.TellOnce(World(null, SampleMask())) && shown.Count == 2, "a world with only a mask is told too");
    }
    finally
    {
      plugins.Remove(TarnCompat.GUID);
      TarnCompat.Run();
      TarnCompat.Detect = detectOld;
      TarnCompat.Show = showOld;
      TarnCompat.ForgetTold();
    }
    C(!TarnCompat.Installed && !TarnCompat.TellOnce(World(SampleLakes())), "without Tarn it is never told");
  }
}

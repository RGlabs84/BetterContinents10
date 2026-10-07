// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The Wide Sectors setting (WideSectorsMode: Auto, On, Off) and the marker a wide world carries in its settings (WideSectors, saved under
// DataKey.WideSectors): which worlds the wide sectors are on for, for a new world and an existing one, with and without wide chunks in its
// save, below, at and above the game's reach, on the machine that runs the world and on a client that follows it; how a new world is made
// (From Config, a snapshot as an import takes, a Directory's export.cfg); and how the marker is written, sent and read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static readonly WideSectorsMode[] Modes = [WideSectorsMode.Auto, WideSectorsMode.On, WideSectorsMode.Off];

  // The world's size + edge: at the game's reach (16,350 m), below it, one metre past it, and well past it.
  private static readonly (string Name, float Size, bool Past)[] Sizes =
    [("16,350 m", 15850f, false), ("10,500 m", 10000f, false), ("16,351 m", 15851f, true), ("24,500 m", 24000f, true)];

  // The chunks of a save that holds one only the wide sectors write: chunk (159, 0), where the zones of the game's chunk (1, 0) are kept.
  private static readonly ZoneSystem.ChunkIndex[] WideChunks = [new ZoneSystem.ChunkIndex(0, 0), new ZoneSystem.ChunkIndex(159, 0)];

  private static byte[] Written(BC.BetterContinentsSettings settings, bool network)
  {
    var pkg = new ZPackage();
    settings.Serialize(pkg, network, true, 12);
    return pkg.GetArray();
  }

  private static BC.BetterContinentsSettings Read(byte[] bytes) => BC.BetterContinentsSettings.Load(new ZPackage(bytes));

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ModeTests(Harmony harmony)
  {
    Section("Wide Sectors: Auto, On and Off, a new world and an existing one, the machine that runs it and a client");
    var owner = "sector-tests.session";
    var none = (WorldGeometry)null;

    // ---- which worlds the wide sectors are on for: the settings, the machine's mode, whether it runs the world, the save's chunks
    int cases = 0, wrong = 0;
    string firstWrong = null;
    void Case(WideSectorsMode mode, bool marker, bool hosted, string size, float metres, bool past, bool forced, bool bcOn)
    {
      WorldSectors.ModeOverride = mode;
      WorldSectors.SessionStarts(hosted);
      var settings = World(bcOn, metres, 500f, wide: marker);
      if (forced)
        WorldSectors.MappingLoaded(harmony, WideChunks);
      WorldSectors.Update(harmony, settings, none);
      // The rule, from the setting's description: a wide world (its marker) that reaches past 16,350 m; On, on the machine that runs the world,
      // also any world that reaches past it; and a save with wide chunks, whatever else.
      bool expected = forced || (bcOn && past && (marker || (mode == WideSectorsMode.On && hosted)));
      bool expectedMarker = marker || (expected && hosted && bcOn && past && mode == WideSectorsMode.On);
      cases++;
      if (WorldSectors.Active != expected || AllHooksPatched(owner) != expected || NoHookPatched(owner) != !expected || settings.WideSectors != expectedMarker)
      {
        wrong++;
        firstWrong ??= $"{mode}, marker {marker}, {(hosted ? "runs the world" : "a client")}, {size}, wide chunks {forced}, BC {(bcOn ? "on" : "off")}: "
          + $"wide {WorldSectors.Active} (expected {expected}), marker {settings.WideSectors} (expected {expectedMarker})";
      }
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
    foreach (var mode in Modes)
      foreach (var marker in new[] { false, true })
        foreach (var hosted in new[] { true, false })
          foreach (var (size, metres, past) in Sizes)
            foreach (var forced in new[] { false, true })
              Case(mode, marker, hosted, size, metres, past, forced, bcOn: true);
    foreach (var mode in Modes)
      foreach (var hosted in new[] { true, false })
      {
        Case(mode, marker: true, hosted, "24,500 m", 24000f, past: true, forced: false, bcOn: false);
        Case(mode, marker: false, hosted, "24,500 m", 24000f, past: true, forced: true, bcOn: false);
      }
    Check(wrong == 0, $"{cases} cases of mode x marker x who runs the world x size x wide chunks in the save x Better Continents on or off: the wide sectors are on exactly when the rule says "
      + $"({wrong} wrong{(firstWrong != null ? ", the first: " + firstWrong : "")})");
    Check(!WorldSectors.Active && NoHookPatched(owner), "and none is left on at the end");

    // The three modes, for an existing world past the game's reach that has the game's sectors (no marker, no wide chunks), on the machine that runs it.
    foreach (var (mode, becomesWide) in new[] { (WideSectorsMode.Auto, false), (WideSectorsMode.On, true), (WideSectorsMode.Off, false) })
    {
      WorldSectors.ModeOverride = mode;
      WorldSectors.SessionStarts(hosted: true);
      var old = World(true, 24000f, 500f);
      int at = Lines;
      WorldSectors.Update(harmony, old, none);
      Check(WorldSectors.Active == becomesWide && old.WideSectors == becomesWide,
        $"{mode}: an existing 24,500 m world with the game's sectors {(becomesWide ? "is converted, and its settings say so now (they are saved with the world)" : "keeps them")}");
      if (mode == WideSectorsMode.Auto)
        Check(Logged(at, "Wide Sectors On converts it") && !Logged(at, "converted"), "Auto: the log says it keeps them and what On does, once");
      if (mode == WideSectorsMode.On)
        Check(Logged(at, "Wide Sectors is On, so this world is a wide world from now on"), "On: the log says the world is a wide one from now on");
      at = Lines;
      WorldSectors.Update(harmony, old, none);
      Check(Lines == at, $"{mode}: asked again, nothing more is said or done");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
    // Off, for a world that is wide: it stays wide, and the log says why, once a session.
    {
      WorldSectors.ModeOverride = WideSectorsMode.Off;
      WorldSectors.SessionStarts(hosted: true);
      int at = Lines;
      WorldSectors.Update(harmony, Wide(), none);
      WorldSectors.Update(harmony, Wide(), none);
      Check(WorldSectors.Active && CapturingLogHandler.Lines.Skip(at).Count(l => l.Contains("Wide Sectors is Off, but this world stays wide: its settings say it is a wide world")) == 1,
        "Off: a world whose settings say it is wide stays wide, and the log says why once");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      at = Lines;
      WorldSectors.MappingLoaded(harmony, WideChunks);
      Check(WorldSectors.Active && Logged(at, "Wide Sectors is Off, but this world stays wide: its save has chunks only the wide sectors write"), "Off: a save with wide chunks stays wide, and says why");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    // ---- a new world: Auto and On make a world past the game's reach wide, Off does not, a preset that is wide stays wide, a world that needs none gets none
    int newWrong = 0;
    string firstNew = null;
    foreach (var mode in Modes)
      foreach (var (size, metres, past) in Sizes)
        foreach (var bcOn in new[] { true, false })
        {
          var settings = World(bcOn, metres, 500f);
          bool made = WorldSectors.NewWorld(settings, mode, none);
          bool expected = bcOn && past && mode != WideSectorsMode.Off;
          if (made != expected || settings.WideSectors != expected)
          {
            newWrong++;
            firstNew ??= $"{mode}, {size}, BC {(bcOn ? "on" : "off")}: {made}";
          }
        }
    Check(newWrong == 0, $"a new world: wide for Auto and On when it reaches past 16,350 m (and Better Continents is on for it), never for Off ({newWrong} wrong{(firstNew != null ? ", the first: " + firstNew : "")})");
    Check(Modes.All(mode => WorldSectors.NewWorld(Wide(), mode, none)), "a preset that is wide stays wide in every mode (Off does not take it back)");
    Check(WorldSectors.NewWorld(World(true, 10000f, 500f), WideSectorsMode.Auto, new WorldGeometry(20000f, 500f)) && !WorldSectors.NewWorld(World(true, 10000f, 500f), WideSectorsMode.Auto, new WorldGeometry(15000f, 500f)),
      "Expand World Size's size counts for a new world as it does for a loaded one");
    {
      int at = Lines;
      WorldSectors.NewWorld(World(true, 24000f, 500f), WideSectorsMode.Off, none);
      Check(Logged(at, "Wide Sectors is Off, so this new world, which reaches 24500 m, has the game's own sectors"), "Off: the log says a new world past the game's reach has the game's sectors");
    }

    // ---- how a new world is made: From Config, the snapshot an import takes, a Directory's export.cfg
    NewWorldFromConfigTests();

    // ---- the marker: saved and sent only by a wide world, read by every other
    {
      var plain = World(true, 24000f, 500f);
      var marked = Wide();
      foreach (var network in new[] { false, true })
      {
        var form = network ? "network" : "disk";
        var a = Written(plain, network);
        var b = Written(marked, network);
        // The same bytes with one key more, 68 (a 4-byte number), somewhere among the keys.
        bool onlyTheKey = b.Length == a.Length + 4 && Enumerable.Range(0, a.Length + 1).Any(i => b.Take(i).SequenceEqual(a.Take(i)) && b.Skip(i + 4).SequenceEqual(a.Skip(i)) && BitConverter.ToInt32(b, i) == 68);
        Check(onlyTheKey, $"{form}: a wide world's settings are a world's without it plus the key 68 ({a.Length} and {b.Length} bytes)");
        var back = Read(b);
        Check(back.EnabledForThisWorld && back.WideSectors && back.WorldSize == 24000f && back.EdgeSize == 500f && back.Version == 12, $"{form}: the marker is read back with the rest of the settings");
        var old = Read(a);
        Check(old.EnabledForThisWorld && !old.WideSectors && old.WorldSize == 24000f, $"{form}: a world saved without it (any made before) is read as one that is not wide, and is not refused");
        Check(Written(Read(b), network).SequenceEqual(b) && Written(Read(a), network).SequenceEqual(a), $"{form}: read and written again, both are the same bytes");
        var small = World(true, 10000f, 500f);
        Check(Written(small, network).SequenceEqual(Written(Read(Written(small, network)), network)) && !Read(Written(small, network)).WideSectors, $"{form}: a world of 10,500 m has no key");
      }
      Check((int)BC.DataKey.AltBiomeMapPath == 66 && (int)BC.DataKey.TiledMap == 67 && (int)BC.DataKey.WideSectors == 68 && Enumerable.Range(0, 69).All(key => Enum.IsDefined(typeof(BC.DataKey), key)),
        "the key is 68, right after the tiles' 67, and no key before it is missing (the numbers are the file format)");
      Check(DumpOf(Read(Written(marked, false))).Any(l => l.StartsWith("Wide sectors:")) && !DumpOf(Read(Written(plain, false))).Any(l => l.Contains("ide sectors")),
        "bc info says so for a wide world, and says nothing for any other");
    }

    // ---- a client follows what the server sent, whatever its own setting says
    {
      var fake = new Fake(512);
      try
      {
        foreach (var mode in Modes)
        {
          WorldSectors.ModeOverride = mode;
          // The session starts as a client's does (ZNet.SetServer(false)): no settings yet, so the game's sectors.
          WorldSectors.SessionStarts(hosted: false);
          WorldSectors.Update(harmony, BC.BetterContinentsSettings.Disabled(), none);
          Check(!WorldSectors.Active && fake.Sectors.Length == 512 * 512, $"a client, Wide Sectors {mode}: before the server's settings arrive it has the game's sectors");
          // The server's settings arrive (ReceivedSettings, LoadFromCache), then DynamicPatch, then the client says it is ready for PeerInfo.
          var received = Read(Written(Wide(), network: true));
          WorldSectors.Update(harmony, received, none);
          Check(WorldSectors.Active && AllHooksPatched(owner) && fake.Sectors.Length == 2048 * 2048,
            $"a client, Wide Sectors {mode}: the server's world is wide (its marker came with the settings), so its sector array is wide before any object arrives");
          WorldSectors.SessionStarts(hosted: false);
          WorldSectors.Update(harmony, BC.BetterContinentsSettings.Disabled(), none);
          Check(!WorldSectors.Active && fake.Sectors.Length == 512 * 512, $"a client, Wide Sectors {mode}: back at the menu, the game's again");
          // A world of the server's that is not wide: never made wide by the client's own setting, On included.
          var plainReceived = Read(Written(World(true, 24000f, 500f), network: true));
          WorldSectors.Update(harmony, plainReceived, none);
          Check(!WorldSectors.Active && NoHookPatched(owner) && !plainReceived.WideSectors, $"a client, Wide Sectors {mode}: a server world that is not wide stays on the game's sectors, and its settings are not changed");
          WorldSectors.Update(harmony, BC.BetterContinentsSettings.Disabled(), none);
        }
      }
      finally
      {
        fake.Close();
      }
    }
    WorldSectors.ModeOverride = WideSectorsMode.Auto;
    WorldSectors.SessionStarts(hosted: true);
  }

  private static List<string> DumpOf(BC.BetterContinentsSettings settings)
  {
    var lines = new List<string>();
    settings.Dump(lines.Add);
    return lines;
  }

  // The config the plugin binds (the offline suites bind it as the game does), a new world made from it in each mode and at each size, the
  // snapshot an import takes (its own Wide Sectors, not the live config's), and a Directory with an export.cfg (which cannot carry the setting).
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void NewWorldFromConfigTests()
  {
    var dir = Path.Combine(Path.GetTempPath(), $"bc-sector-tests-{Environment.ProcessId}");
    Directory.CreateDirectory(dir);
    try
    {
      if (BC.ConfigEnabled == null)
      {
        var cfg = new ConfigFile(Path.Combine(dir, "BetterContinents.cfg"), true);
        BC.DeclareConfig(cfg);
        BiomeRegistry.RefreshTable();
      }
      var config = BC.ConfigEnabled.ConfigFile;
      var entry = BC.ConfigWideSectors;
      Check(entry != null && entry.Value == WideSectorsMode.Auto && SettingsSchema.WideSectors.Section == "07 BetterContinents.Misc" && SettingsSchema.WideSectors.Scope == SettingScope.Live
            && SettingsSchema.Groups.Single(g => g.Name == "BetterContinents.Misc").Settings.Last() == SettingsSchema.WideSectors && !SettingsSchema.WideSectors.ReadsIntoSettings,
        "Wide Sectors is in [07 BetterContinents.Misc], last in its list, Auto by default, a machine's own setting (no world, export or import carries it)");
      Check(WorldImport.IsWorldSetting(entry) == false, "export.cfg never carries it (it is not a world setting)");

      BC.ConfigEnabled.Value = true;
      BC.ConfigEdgeSize.Value = 500f;
      int wrong = 0;
      string first = null;
      foreach (var mode in Modes)
        foreach (var (size, metres, past) in Sizes)
        {
          BC.ConfigWorldSize.Value = metres;
          entry.Value = mode;
          var made = BC.BetterContinentsSettings.Create();
          bool expected = past && mode != WideSectorsMode.Off;
          if (made.WideSectors != expected)
          {
            wrong++;
            first ??= $"{mode}, {size}: {made.WideSectors}";
          }
        }
      Check(wrong == 0, $"a new world made From Config: wide for Auto and On past 16,350 m, never for Off, never at or below it ({wrong} wrong{(first != null ? ", the first: " + first : "")})");

      // The snapshot an import takes: its own value of the setting, whatever the live config says.
      BC.ConfigWorldSize.Value = 24000f;
      foreach (var (live, snapshot) in new[] { (WideSectorsMode.On, WideSectorsMode.Off), (WideSectorsMode.Off, WideSectorsMode.On), (WideSectorsMode.Auto, WideSectorsMode.Off) })
      {
        entry.Value = live;
        var values = ConfigValues.Snapshot(config, new Dictionary<ConfigEntryBase, object> { [entry] = snapshot });
        var made = BC.BetterContinentsSettings.CreateForImport(values, lean: true);
        Check(made.WideSectors == (snapshot != WideSectorsMode.Off), $"an import's snapshot says {snapshot} while this machine's config says {live}: the preset it makes is {(snapshot != WideSectorsMode.Off ? "" : "not ")}wide");
      }

      // A Directory with an export.cfg: its lines win over the config for the world settings; its Wide Sectors line (which a hand could write) does not count.
      var export = Path.Combine(dir, "export");
      Directory.CreateDirectory(export);
      File.WriteAllLines(Path.Combine(export, WorldImport.ConfigFileName),
        ["[01 BetterContinents.Global]", "World Size = 24000", "Edge Size = 500", "[07 BetterContinents.Misc]", "Wide Sectors = Off"]);
      BC.ConfigMapSourceDir.Value = export;
      BC.ConfigWorldSize.Value = 10000f;
      entry.Value = WideSectorsMode.Auto;
      int at = Lines;
      var fromExport = BC.BetterContinentsSettings.Create();
      Check(fromExport.WideSectors && fromExport.WorldSize == 24000f && Logged(at, "ignored [07 BetterContinents.Misc] Wide Sectors: not a world setting", "Warning"),
        "a world made from a Directory with an export.cfg (24,500 m there, 10,500 m in the config) is wide by this machine's Auto; its own Wide Sectors line is ignored, as any other that is not a world setting");
      entry.Value = WideSectorsMode.Off;
      Check(!BC.BetterContinentsSettings.Create().WideSectors, "and not by this machine's Off");
      BC.ConfigMapSourceDir.Value = "";
    }
    finally
    {
      if (BC.ConfigWideSectors != null)
        BC.ConfigWideSectors.Value = WideSectorsMode.Auto;
      try { Directory.Delete(dir, true); } catch { }
    }
  }
}

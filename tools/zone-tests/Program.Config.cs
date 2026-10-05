// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// The Debug Reset Command setting and its migration, the "bc regen" command and what it is wired to, and the game's own code
// that the regeneration depends on, read from the installed game's IL: who sets a creator, how a destroyed object reaches
// the clients, what a generated zone is, the terrain compiler's data format.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Kind = BetterContinents.ZoneReset.Kind;

internal static partial class Program
{
  private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
  private const BindingFlags AnyDeclared = Any | BindingFlags.DeclaredOnly;

  // ------------------------------------------------------------------------------------------------ the setting and its migration

  private static int cfgCount;

  // A BetterContinents.cfg as the file the player has, bound and migrated as the game does at start.
  private static (string Command, int Version, float ExportAmount, List<string> Log, string FileText) Start(string? text, string? name = null)
  {
    var path = Path.Combine(Work, name ?? $"cfg{++cfgCount}.cfg");
    if (text != null)
      File.WriteAllText(path, text);
    var file = new ConfigFile(path, true);
    var log = LogHandler.During(() =>
    {
      BC.DeclareConfig(file);
      SettingsSchema.Migrate();
    });
    file.Save();
    return (BC.ConfigDebugResetCommand.Value, BC.ConfigFileVersion.Value, BC.ConfigExportHeightmapAmount.Value, log, File.ReadAllText(path));
  }

  private static List<string> Mine(List<string> log) => log.Where(l => l.Contains("Debug Reset Command")).ToList();

  private static void MigrationTests()
  {
    Section("Debug Reset Command: its default and the one-time migration");
    var def = SettingsSchema.DebugResetCommand;
    C(def.Section == "00 BetterContinents.Debug" && def.Key == "Debug Reset Command", "the setting keeps its section and key");
    C(def.Default == "" && def.Scope == SettingScope.Local, "its default is empty, and it is this machine's own");
    C(def.Description.Contains("bc regen") && def.Description.Contains("zones_reset start") && def.Description.StartsWith("Empty (the default)"),
      "its description says what empty does and that a command runs instead");
    string[] banned = ["replace", "obsolete", "no longer need", "needs no other", "Upgrade World", "Expand World"];
    C(banned.All(w => !def.Description.Contains(w, StringComparison.OrdinalIgnoreCase)), "its description names no other mod and replaces none");
    C(def.Description.Contains("worked on") && def.Description.Contains("kept whole"), "its description names ground work, and says a location is kept whole");
    C(SettingsSchema.CurrentConfigVersion == 2, "the config version is 2");

    // A file written by Better Continents 0.9: the old default is emptied once, with a line that says how to go back.
    var old = Start("[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n");
    C(old.Command == "" && old.Version == 2, "0.9's default 'zones_reset start' becomes empty, and the file is at version 2");
    var line = Mine(old.Log);
    C(line.Count == 1 && line[0].Contains("was \"zones_reset start\"") && line[0].Contains("now empty") && line[0].Contains("Write \"zones_reset start\" there again"),
      "one log line, in the style of the export amount's, with the way back: " + (line.Count > 0 ? line[0] : "(none)"));
    C(old.FileText.Contains("Debug Reset Command =") && !old.FileText.Contains("Debug Reset Command = zones_reset start") && old.FileText.Contains("Config Version = 2"),
      "the file on disk holds the new value and version");

    // A new file: nothing to migrate, nothing logged, version 2.
    var fresh = Start(null);
    C(fresh.Command == "" && fresh.Version == 2 && Mine(fresh.Log).Count == 0, "a new file starts empty at version 2, silently");

    // A command somebody wrote stays, whatever it is.
    foreach (var command in new[] { "zones_reset start safezones=3", "zones_reset", "zones_reset  start", "Zones_Reset Start", "zones_reset start force", "zones_reset start " + "x" })
    {
      var custom = Start($"[00 BetterContinents.Debug]\nDebug Reset Command = {command}\n");
      C(custom.Command == command && custom.Version == 2 && Mine(custom.Log).Count == 0, $"'{command}' is somebody's own and stays");
    }

    // The old default written again after the migration is a choice.
    var again = Start("[07 BetterContinents.Misc]\nConfig Version = 2\n\n[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n");
    C(again.Command == "zones_reset start" && again.Version == 2 && Mine(again.Log).Count == 0, "at version 2 'zones_reset start' is a choice: left alone");

    // Version 1 (after the export amount's step): only the new step runs.
    var one = Start("[07 BetterContinents.Misc]\nConfig Version = 1\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n\n[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n");
    C(one.Command == "" && one.Version == 2 && one.ExportAmount == 2f && one.Log.Count == 1 && Mine(one.Log).Count == 1, "at version 1 only the new step runs: the export amount of 2 is kept");

    // Version 0 with both: both steps, in order.
    var both = Start("[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n\n[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n");
    C(both.Command == "" && both.Version == 2 && both.ExportAmount == 1f && both.Log.Count == 2 && both.Log[0].Contains("Default Heightmap Amount") && both.Log[1].Contains("Debug Reset Command"),
      "a 0.9 file with both old defaults gets both steps, the amount's first");

    // A file written by a newer Better Continents keeps its version: none of it is done again, and the version is not lowered.
    var newer = Start("[07 BetterContinents.Misc]\nConfig Version = 3\n\n[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n\n[09 BetterContinents.Export]\nDefault Heightmap Amount = 2\n");
    C(newer.Version == 3 && newer.Command == "zones_reset start" && newer.ExportAmount == 2f && newer.Log.Count == 0 && newer.FileText.Contains("Config Version = 3"), "a file at version 3 is left as it is, and stays at version 3");

    // A second start of a file that has been migrated changes nothing.
    var path = Path.Combine(Work, "twice.cfg");
    File.WriteAllText(path, "[00 BetterContinents.Debug]\nDebug Reset Command = zones_reset start\n");
    var first = Start(null, "twice.cfg");
    var second = Start(null, "twice.cfg");
    C(first.Command == "" && Mine(first.Log).Count == 1 && second.Command == "" && second.Version == 2 && second.Log.Count == 0, "a second start of a migrated file logs nothing and changes nothing");

    // The state the wiring tests below need: the default.
    Start(null);
  }

  // ------------------------------------------------------------------------------------------------ the command and its wiring

  private static List<MethodBase> Callees(MethodBase method) =>
    PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt).Select(i => i.operand).OfType<MethodBase>().ToList();

  private static void WiringTests()
  {
    Section("wiring: bc regen, the Debug Reset Command choice, GameUtils.ResetZones");
    C(ZoneRegen.RunsOwn("") && ZoneRegen.RunsOwn("   ") && ZoneRegen.RunsOwn(null), "an empty, blank or missing Debug Reset Command leaves the regeneration to Better Continents");
    C(!ZoneRegen.RunsOwn("zones_reset start") && !ZoneRegen.RunsOwn("x"), "any command written there runs instead");

    // GameUtils.ResetZones itself, with its game calls in fields, is run by HardenTests (ResetZonesTests), and the game calls those
    // fields hold by default are read there too (DefaultSeamTests).

    // Nothing but GameUtils.ResetZones reads the Debug Reset Command setting to run it: every other place goes through that method.
    var readers = new List<string>();
    var field = typeof(BC).GetField("ConfigDebugResetCommand")!;
    foreach (var type in typeof(BC).Assembly.GetTypes())
      foreach (var method in type.GetMethods(AnyDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AnyDeclared)))
      {
        try
        {
          if (method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(method).Any(i => i.opcode == OpCodes.Ldsfld && i.operand is FieldInfo f && f == field))
            readers.Add(type.Name + "." + method.Name);
        }
        catch
        {
          // Not every method decodes offline.
        }
      }
    C(readers.SequenceEqual(["GameUtils.ResetZones"]), "only GameUtils.ResetZones reads Debug Reset Command: " + string.Join(", ", readers));

    // The alt-biome rebuild comes first, and the zones are reset when it is done: GameUtils.Reset hands ResetZones to it.
    var resetCalls = PatchProcessor.GetOriginalInstructions(typeof(GameUtils).GetMethod(nameof(GameUtils.Reset))!).ToList();
    int ldftn = resetCalls.FindIndex(i => i.opcode == OpCodes.Ldftn && i.operand is MethodInfo m && m.Name == "ResetZones");
    int rebuild = resetCalls.FindIndex(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m && m.Name == "RequestRebuild");
    C(ldftn >= 0 && rebuild > ldftn, "GameUtils.Reset passes ResetZones to the alt-biome rebuild, which calls it when the new grid is ready");

    // The tree.
    var root = typeof(DebugUtils).GetField("rootCommand", Any)!.GetValue(null)!;
    var rootType = root.GetType();
    var subs = ((System.Collections.IEnumerable)rootType.GetMethod("GetSubcommands")!.Invoke(root, null)!).Cast<object>().ToList();
    string Name(object c) => (string)c.GetType().GetField("cmd")!.GetValue(c)!;
    string Desc(object c) => (string)c.GetType().GetField("desc")!.GetValue(c)!;
    var names = subs.Select(Name).ToList();
    int iReset = names.IndexOf("reset");
    int iRegen = names.IndexOf("regen");
    C(iRegen > 0 && iReset >= 0 && iRegen == iReset + 1, "bc regen is in the tree, next to bc reset");
    var regen = subs[iRegen];
    C(Desc(regen).Contains("Debug Reset Command") && Desc(regen).Contains("never removed") && Desc(regen).Contains("one zone of something a player built"), "its help says what it does and what it leaves");
    C(banned(Desc(regen)), "its help names no other mod and replaces none");
    C(Desc(regen).Contains("worked on") && Desc(regen).Contains("kept whole"), "its help names ground work, and says a location is kept whole");
    var resetHelp = Desc(subs[iReset]);
    C(resetHelp.Contains("Redraws the minimap") && resetHelp.Contains("rebuilds the alt-biome grid") && resetHelp.Contains("regenerates the zones") && !resetHelp.Contains("Resets whole map") && banned(resetHelp),
      "bc reset says what it does: " + resetHelp);
    C(names.Count(n => n == "regen") == 1 && names.Contains("twins") && names.Contains("cache") && names.Contains("export"), "the other commands are all still there");

    // bc regen with no world: it answers and logs, and does not throw.
    BC.ConfigDebugResetCommand.Value = "";
    var lines = LogHandler.During(() => root.GetType().GetMethod("Run")!.Invoke(root, ["bc regen"]));
    C(lines.Any(l => l.Contains("Zone regeneration: load a world first.")), "bc regen without a world says to load one: " + string.Join(" | ", lines));
    // Anything after the word is a mistake, and nothing is started.
    int started = 0;
    var said = new List<string>();
    ZoneRegen.RunCommand("hlp", said.Add, () => started++);
    ZoneRegen.RunCommand("now please", said.Add, () => started++);
    C(started == 0 && said.Count == 2 && said[0].Contains("takes no arguments ('hlp')") && said[0].Contains("bc regen help"), "bc regen with a word after it does not start the regeneration; it says so: " + said.FirstOrDefault());
    ZoneRegen.RunCommand("", said.Add, () => started++);
    ZoneRegen.RunCommand(null, said.Add, () => started++);
    ZoneRegen.RunCommand("  ", said.Add, () => started++);
    C(started == 3 && said.Count == 2, "bc regen alone starts it");
    var wired = PatchProcessor.GetOriginalInstructions(typeof(DebugUtils).GetNestedType("<>c", Any)!.GetMethods(Any).First(m => m.Name.Contains("b__") && Callees(m).Any(c => c.Name == "RunCommand")));
    C(wired.Any(i => i.opcode == OpCodes.Ldftn && i.operand is MethodInfo m && m.Name == "ResetZones"), "and the command hands GameUtils.ResetZones to it");

    static bool banned(string text) => new[] { "replace", "obsolete", "no longer need", "Upgrade World", "Expand World" }.All(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase));
  }

  // ------------------------------------------------------------------------------------------------ the game's own code

  private static IEnumerable<Type> GameTypes()
  {
    try
    {
      return typeof(Piece).Assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException e)
    {
      return e.Types.Where(t => t != null)!;
    }
  }

  // Every method of the game's assembly that calls the target, and how many methods could be read at all (some do not decode offline).
  private static (List<string> Callers, int Scanned, int Unreadable) CallersOf(MethodBase target)
  {
    var callers = new List<string>();
    int scanned = 0, unreadable = 0;
    foreach (var type in GameTypes())
    {
      IEnumerable<MethodBase> methods;
      try
      {
        methods = type.GetMethods(AnyDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AnyDeclared)).ToList();
      }
      catch
      {
        unreadable++;
        continue;
      }
      foreach (var method in methods)
      {
        try
        {
          if (method.GetMethodBody() == null)
            continue;
          scanned++;
          if (PatchProcessor.GetOriginalInstructions(method).Any(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase m && m == target))
            callers.Add(type.FullName + "." + method.Name);
        }
        catch
        {
          unreadable++;
        }
      }
    }
    return (callers, scanned, unreadable);
  }

  // The parameter types of the calls to a class's methods, in the order the method makes them.
  private static List<string> CallSequence(MethodBase method, Type declaring, string prefix) =>
    PatchProcessor.GetOriginalInstructions(method)
      .Where(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m.DeclaringType == declaring && m.Name.StartsWith(prefix))
      .Select(i => (MethodInfo)i.operand)
      .Select(m => prefix == "Write" ? m.GetParameters()[0].ParameterType.Name : m.Name)
      .ToList();

  private static void GameFactTests()
  {
    Section("the game's own code the regeneration relies on (the installed game)");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var setCreator = typeof(Piece).GetMethod("SetCreator", Any)!;
    var (creators, scanned, unreadable) = CallersOf(setCreator);
    C(creators.SequenceEqual(["Player.PlacePiece"]), $"a creator is set only when a player places a piece: {string.Join(", ", creators)}");
    // The methods that do not decode use assemblies the reference folder lacks (audio, AI, cloth, PlayFab) or are not plain methods.
    C(scanned > 5000 && unreadable * 20 < scanned, $"... read off {scanned:N0} of the game's methods, {unreadable} of which did not decode offline ({watch.ElapsedMilliseconds / 1000.0:0.0} s)");
    var tombSetup = typeof(TombStone).GetMethod("Setup", Any)!;
    var (tombs, _, _) = CallersOf(tombSetup);
    // (And by a cheat command of the console, which an admin runs on purpose.)
    C(tombs.Contains("Player.CreateTombStone") && tombs.All(c => c == "Player.CreateTombStone" || c.StartsWith("Terminal+")), $"a tombstone is made for a dead player, or by a console cheat: {string.Join(", ", tombs)}");

    // Character.InInterior: higher than 3000 m.
    var interior = typeof(Character).GetMethod("InInterior", Any, null, [typeof(Vector3)], null)!;
    C(PatchProcessor.GetOriginalInstructions(interior).Any(i => i.opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.operand) == ZoneReset.Rules.InteriorHeight),
      $"Character.InInterior compares with {ZoneReset.Rules.InteriorHeight} m");

    // Only the owner can destroy an object, and ZNetScene.Destroy hands an owned one to ZDOMan.
    var destroyZdo = typeof(ZDOMan).GetMethod("DestroyZDO", Any)!;
    var dz = PatchProcessor.GetOriginalInstructions(destroyZdo);
    C(dz.Any(i => i.operand is MethodInfo m && m.Name == "IsOwner") && dz.Any(i => i.operand is MethodInfo m && m.Name == "Add" && m.DeclaringType == typeof(List<ZDOID>)),
      "ZDOMan.DestroyZDO queues the object for the clients only if this machine owns it");
    var sceneDestroy = typeof(ZNetScene).GetMethod("Destroy", Any, null, [typeof(GameObject)], null)!;
    var sd = Callees(sceneDestroy);
    C(sd.Any(m => m.Name == "IsOwner") && sd.Any(m => m.Name == "DestroyZDO") && sd.Any(m => m.Name == "Destroy" && m.DeclaringType == typeof(UnityEngine.Object)),
      "ZNetScene.Destroy destroys the object and, if owned, queues its ZDO");
    var send = Callees(typeof(ZDOMan).GetMethod("SendDestroyed", Any)!);
    C(send.Any(m => m.Name == "InvokeRoutedRPC"), "ZDOMan.SendDestroyed sends the queue as one routed message to everybody");
    var update = Callees(typeof(ZDOMan).GetMethod("Update", Any)!);
    C(update.Any(m => m.Name == "SendDestroyed"), "ZDOMan.Update sends it every frame");
    C(Callees(typeof(ZNet).GetMethod("Update", Any, null, Type.EmptyTypes, null)!).Any(m => m.Name == "Update" && m.DeclaringType == typeof(ZDOMan)), "and ZNet.Update, which runs every frame, runs that");
    var invoke = Callees(typeof(ZRoutedRpc).GetMethod("InvokeRoutedRPC", Any, null, [typeof(long), typeof(ZDOID), typeof(string), typeof(object[])], null)!);
    C(invoke.Any(m => m.Name == "HandleRoutedRPC") && invoke.Any(m => m.Name == "RouteRPC"), "a routed message to everybody is handled on this machine at once, and sent on to the others");

    // A generated zone is never filled again: SpawnZone fills a zone only if it is not generated, and marks it.
    var spawn = Callees(typeof(ZoneSystem).GetMethod("SpawnZone", Any)!);
    C(spawn.Any(m => m.Name == "IsZoneGenerated") && spawn.Any(m => m.Name == "SetZoneGenerated") && spawn.Any(m => m.Name == "PlaceLocations") && spawn.Any(m => m.Name == "PlaceVegetation"),
      "SpawnZone fills a zone that is not generated, and marks it generated");
    var place = PatchProcessor.GetOriginalInstructions(typeof(ZoneSystem).GetMethod("PlaceLocations", Any)!);
    var placed = typeof(ZoneSystem.LocationInstance).GetField("m_placed")!;
    C(place.Any(i => i.operand is FieldInfo f && f == placed) && place.Any(i => i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f == placed),
      "PlaceLocations reads a location's m_placed and sets it: an unplaced one is placed again");
    var poke = Callees(typeof(ZoneSystem).GetMethod("PokeLocalZone", Any)!);
    C(poke.Any(m => m.Name == "IsZoneGenerated") && poke.Any(m => m.Name == "SpawnZone"), "PokeLocalZone spawns a zone that is not loaded, as a fresh one if it is not generated");
    var ttl = Callees(typeof(ZoneSystem).GetMethod("UpdateTTL", Any)!);
    C(ttl.Any(m => m.Name == "Destroy" && m.DeclaringType == typeof(UnityEngine.Object)), "a loaded zone's copy goes by Object.Destroy of its m_root, as here");

    // The terrain compiler's data: TerrainComp.Save writes it in the order ZoneReset.TerrainBorder reads it, and Load reads that order.
    var save = typeof(TerrainComp).GetMethod("Save", Any)!;
    var written = CallSequence(save, typeof(ZPackage), "Write");
    var expectedWrite = new[] { "Int32", "Int32", "Vector3", "Single", "Int32", "Boolean", "Single", "Single", "Int32", "Boolean", "Single", "Single", "Single", "Single" };
    C(written.SequenceEqual(expectedWrite), "TerrainComp.Save writes: version, operations, point, radius, count, [edited, level, smooth], count, [painted, r, g, b, a]: " + string.Join(" ", written));
    var load = typeof(TerrainComp).GetMethod("Load", Any)!;
    var read = CallSequence(load, typeof(ZPackage), "Read");
    var expectedRead = new[] { "ReadInt", "ReadInt", "ReadVector3", "ReadSingle", "ReadInt", "ReadBool", "ReadSingle", "ReadSingle", "ReadInt", "ReadBool", "ReadSingle", "ReadSingle", "ReadSingle", "ReadSingle" };
    C(read.Take(expectedRead.Length).SequenceEqual(expectedRead), "TerrainComp.Load reads them in that order: " + string.Join(" ", read.Take(expectedRead.Length)));
    var pokeAfter = Callees(typeof(TerrainComp).GetMethod("CheckLoad", Any)!);
    C(pokeAfter.Any(m => m.Name == "Load") && pokeAfter.Any(m => m.Name == "Poke"), "a loaded compiler that sees new data loads it and rebuilds its ground");
    C(typeof(TerrainComp).GetMethod("CheckLoad", Any) != null && PatchProcessor.GetOriginalInstructions(typeof(TerrainComp).GetMethod("CheckLoad", Any)!).Any(i => i.operand is MethodInfo m && m.Name == "get_DataRevision"),
      "and it notices new data by the ZDO's data revision");

    // The ZDO variables and prefabs the rules use.
    C(ZDOVars.s_creator == "creator".GetStableHashCode() && ZDOVars.s_tamed == "tamed".GetStableHashCode() && ZDOVars.s_TCData == "TCData".GetStableHashCode(),
      "ZDOVars: creator, tamed and TCData are the names the regeneration reads");
    var prefabs = FindPrefabList();
    C(prefabs != null && prefabs.Count > 1000, $"the game's list of prefab names is found next to libs-Tools and holds the game's prefabs ({prefabs?.Count})");
    foreach (var name in new[] { "Player", "Player_tombstone", "_TerrainCompiler", "_ZoneCtrl" })
      C(prefabs != null && prefabs.Contains(name), $"the game has a prefab named {name}");

    // The private members it reaches into, by name and type.
    var zs = typeof(ZoneSystem);
    var generated = zs.GetField("m_generatedZones", Any);
    C(generated != null && generated.FieldType == typeof(HashSet<Vector2s>), "ZoneSystem.m_generatedZones is a HashSet<Vector2s>");
    var zones = zs.GetField("m_zones", Any);
    var zoneData = zs.GetNestedType("ZoneData", BindingFlags.NonPublic);
    C(zones != null && zoneData != null && zones.FieldType == typeof(Dictionary<,>).MakeGenericType(typeof(Vector2s), zoneData) && zoneData.GetField("m_root")?.FieldType == typeof(GameObject),
      "ZoneSystem.m_zones maps a zone to a ZoneData with a GameObject m_root");
    var locations = zs.GetField("m_locationInstances", Any);
    var instance = typeof(ZoneSystem.LocationInstance);
    C(locations != null && locations.FieldType == typeof(Dictionary<Vector2s, ZoneSystem.LocationInstance>) && instance.IsValueType
      && instance.GetField("m_placed")?.FieldType == typeof(bool) && instance.GetField("m_position")?.FieldType == typeof(Vector3),
      "ZoneSystem.m_locationInstances maps a zone to a LocationInstance struct with m_placed and m_position");
    var bySector = typeof(ZDOMan).GetField("m_objectsBySector", Any);
    var portals = typeof(ZDOMan).GetField("m_portalObjects", Any);
    C(bySector?.FieldType == typeof(List<ZDO>[]) && portals?.FieldType == typeof(Dictionary<ZoneSystem.SectorIndex, List<ZDO>>), "ZDOMan.m_objectsBySector and m_portalObjects");
    C(typeof(ZDOMan).GetMethod("GetPortalList", Any)?.ReturnType == typeof(List<ZDO>) && typeof(ZDOMan).GetMethod("GetZDO", Any, null, [typeof(ZDOID)], null)?.ReturnType == typeof(ZDO)
      && typeof(ZDOMan).GetMethod("GetSessionID", Any)?.ReturnType == typeof(long), "ZDOMan.GetPortalList, GetZDO and GetSessionID");
    C(typeof(ZoneSystem.SectorIndex).GetField("Sector")?.FieldType == typeof(uint) && typeof(ZoneSystem).GetMethod("SectorToIndex", Any, null, [typeof(Vector2s)], null)?.ReturnType == typeof(ZoneSystem.SectorIndex)
      && typeof(ZoneSystem).GetMethod("GetZone", Any, null, [typeof(Vector3)], null)?.ReturnType == typeof(Vector2s), "ZoneSystem.SectorToIndex, SectorIndex.Sector and GetZone");
    C(typeof(ZNetScene).GetMethod("FindInstance", Any, null, [typeof(ZDO)], null)?.ReturnType == typeof(ZNetView), "ZNetScene.FindInstance(ZDO) gives the ZNetView");
    var net = typeof(ZNet);
    C(net.GetMethod("GetAllCharacterZDOS", Any)?.ReturnType == typeof(List<ZDO>) && net.GetProperty("LocalPlayerCharacterID", Any)?.PropertyType == typeof(ZDOID)
      && net.GetMethod("GetPeers", Any)?.ReturnType == typeof(List<ZNetPeer>), "ZNet.GetAllCharacterZDOS, LocalPlayerCharacterID and GetPeers");
    var peer = typeof(ZNetPeer);
    C(peer.GetField("m_characterID")?.FieldType == typeof(ZDOID) && peer.GetMethod("GetRefPos")?.ReturnType == typeof(Vector3) && peer.GetMethod("IsReady")?.ReturnType == typeof(bool), "ZNetPeer: m_characterID, GetRefPos, IsReady");
    C(typeof(Minimap).GetMethod("UpdateLocationPins", Any, null, [typeof(float)], null) != null && typeof(ClutterSystem).GetMethod("ClearAll", Any) != null, "Minimap.UpdateLocationPins(float) and ClutterSystem.ClearAll()");
    C(typeof(Heightmap).GetField("s_heightmaps", Any)?.FieldType == typeof(List<Heightmap>), "Heightmap.s_heightmaps");
    C(typeof(WorldGenerator).GetMethod("GetHeight", Any, null, [typeof(float), typeof(float)], null)?.ReturnType == typeof(float), "WorldGenerator.GetHeight(x, z)");
    C(typeof(ZDO).GetMethod("SetOwner", Any)?.GetParameters().Single().ParameterType == typeof(long) && typeof(ZDO).GetMethod("Set", Any, null, [typeof(int), typeof(byte[])], null) != null
      && typeof(ZDO).GetMethod("GetByteArray", Any, null, [typeof(int), typeof(byte[])], null) != null, "ZDO.SetOwner(long), Set(int, byte[]) and GetByteArray(int, byte[])");
  }

  // The prefab names the game's ZNetScene holds, from the newest list in the reference data next to libs-Tools (read only); null if there is none.
  private static HashSet<string>? FindPrefabList()
  {
    var libs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools"));
    var dir = Path.Combine(libs, "1.0", "ASSET-DATA", "materials-and-prefabs");
    var path = Directory.Exists(dir) ? Directory.GetFiles(dir, "znetscene_prefabs_*.txt").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() : null;
    return path != null ? File.ReadAllLines(path).Select(l => l.Trim()).ToHashSet() : null;
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// bc_bake (10.1, 11.1): the console tool of the in-game bake. Built like bc_twins: not part of the `bc` cheat tree, an answer on the console that
// asked and in the log, and a `server` prefix that reaches a dedicated server for admins (ConsoleTools). The host's `bc` tree has it as `bc bake`.
//
//   single player        the player
//   a hosted game        the host at their own console; an admin of the host's list through `bc_bake server ...`
//   a dedicated server   an admin through `bc_bake server ...` (the game checks adminlist.txt)
//   any client           the read-only words: info, stats, hide, show
// No Debug Mode and no devcommands are needed. Every word that changes the world is a dry run unless `confirm` is given, and one operation runs at
// a time.
internal static class BakeCommands
{
  public static void Register()
  {
    // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
    new Terminal.ConsoleCommand("bc_bake",
      "[help | info | list | area ... | box ... | unbake ... | undo | load | drop ... | server ...] - Better Continents: bake pieces into the world's layer, take them out again, undo (bc_bake help)",
      args => Run(args),
      isCheat: false, isNetwork: false, onlyServer: false,
      optionsFetcher: () => ["help", "info", "stats", "hide", "show", "list", "check", "area", "box", "unbake", "undo", "load", "export", "orphans", "drop", "server"]);
  }

  // The answer goes to the console that asked, the log, and the admin a server runs it for (ConsoleTools).
  private static void Run(Terminal.ConsoleEventArgs args) => RunLine(ConsoleTools.Rest(args, "bc_bake"), ConsoleTools.Output(args.Context));

  // bc_bake, and "bc bake" in the host's debug tree.
  internal static void RunLine(string text, Action<string> output)
  {
    var command = BakeCommandLine.Parse(text);
    if (command.Error != null)
    {
      output(command.Error);
      output(BakeCommandLine.Usage);
      return;
    }
    var net = ZNet.instance;
    bool inWorld = net != null && ZDOMan.instance != null && ZNetScene.instance != null && ZNet.World != null;
    var refusal = BakeCommandLine.Refusal(command.Verb, net != null && net.IsServer(), inWorld);
    if (refusal != null)
    {
      output("bc_bake: " + refusal);
      return;
    }
    switch (command.Verb)
    {
      case BakeVerb.Help:
        Help(output);
        return;
      case BakeVerb.Server:
        SendToServer(command.Text ?? "", output);
        return;
      case BakeVerb.Info:
        Info(output);
        return;
      case BakeVerb.Stats:
      case BakeVerb.Hide:
      case BakeVerb.Show:
        ClientTool(command.Verb, output);
        return;
      case BakeVerb.DropWorld:
        BakeDropWorld.Run(command.Text ?? "", command.Words.Confirm, output);
        return;
    }
    var who = WhoIsAsking();
    var ctx = BakeRuntime.Context(output, who);
    if (ctx == null)
    {
      output("bc_bake: load a world first.");
      return;
    }
    switch (command.Verb)
    {
      case BakeVerb.List:
        foreach (var line in BakeReport.ListLines(ctx.Layer.Operations, ctx.Journal.Numbers(), n => ctx.Journal.GetState(n), n => TryRead(ctx.Journal, n)))
          output(line);
        return;
      case BakeVerb.Check:
        Check(ctx, command, output);
        return;
      case BakeVerb.Export:
        Export(ctx, command.Text, output);
        return;
      case BakeVerb.Orphans:
        Orphans(command, output);
        return;
      case BakeVerb.Area:
      case BakeVerb.Box:
      case BakeVerb.Unbake:
        {
          var caller = CallerPlace();
          var area = command.AreaFor(caller?.x, caller?.z);
          if (area == null && !command.IsWorld)
          {
            output("bc_bake: there is no one to take the place from: give it with 'at <x> <z>'.");
            return;
          }
          // The words that make the same command again, with the place it was given or took, so that 'confirm' bakes what the dry run described.
          var words = area?.Words(true) ?? "world";
          if (command.Verb == BakeVerb.Unbake)
            Start(BakeRunner.Unbake(ctx, new UnbakeScope { Area = area }, command.Words, "unbake " + (area is { Circle: true } ? words.Substring("area ".Length) : words)), "an unbake", output);
          else
            Start(BakeRunner.Bake(ctx, area!, command.Words, words), "a bake", output);
          return;
        }
      case BakeVerb.Undo:
        Start(BakeRunner.Undo(ctx, command.Number, command.Words), "an undo", output);
        return;
      case BakeVerb.Drop:
        Start(BakeRunner.Drop(ctx, new DropScope { Compiler = command.Compiler, Bake = command.Number ?? 0 }, command.Words), "a drop", output);
        return;
      case BakeVerb.Load:
        Load(ctx, command, output);
        return;
    }
  }

  // An operation is a coroutine of the plugin; Guard says what goes wrong when it throws, and keeps nesting honest.
  private static void Start(IEnumerator routine, string what, Action<string> output)
  {
    BetterContinents.instance.StartCoroutine(BakeRunner.Guard(routine, output, what));
  }

  private static BakeJournalData? TryRead(BakeJournal journal, int number)
  {
    try
    {
      return journal.Read(number);
    }
    catch (BakeJournalException)
    {
      return null;
    }
  }

  // ------------------------------------------------------------------------------------------------ who asks, and where

  // Who runs the command: the player at this console, or the admin the server runs it for.
  private static BakeWho WhoIsAsking()
  {
    var net = ZNet.instance;
    var caller = ConsoleTools.RemoteCaller;
    if (caller != null && net != null)
    {
      var peer = net.GetPeer(caller);
      if (peer != null)
      {
        long player = ZDOMan.instance?.GetZDO(peer.m_characterID)?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
        return new BakeWho
        {
          Name = string.IsNullOrEmpty(peer.m_playerName) ? "an admin" : peer.m_playerName,
          PlatformId = peer.m_socket?.GetHostName() ?? "",
          Creator = player != 0L ? player : BakeRunner.ConsoleCreator,
          Notify = text => Tell(peer, text),
        };
      }
    }
    var local = Player.m_localPlayer;
    if (local != null)
      return new BakeWho
      {
        Name = local.GetPlayerName(),
        Creator = local.GetPlayerID() is var id && id != 0L ? id : BakeRunner.ConsoleCreator,
        Notify = text => MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text),
      };
    return new BakeWho { Name = "server console" };
  }

  // One top-left message on an admin's screen.
  private static void Tell(ZNetPeer peer, string text)
  {
    try
    {
      ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)MessageHud.MessageType.TopLeft, text);
    }
    catch (Exception)
    {
      // The admin left.
    }
  }

  // Where the one who asks stands: the host's player, or the admin's peer; null at a server's own console.
  private static (float x, float z)? CallerPlace()
  {
    var net = ZNet.instance;
    var caller = ConsoleTools.RemoteCaller;
    if (caller != null && net != null && net.GetPeer(caller) is { } peer)
    {
      var at = peer.GetRefPos();
      return (at.x, at.z);
    }
    var local = Player.m_localPlayer;
    if (local != null)
    {
      var at = local.transform.position;
      return (at.x, at.z);
    }
    return null;
  }

  // ------------------------------------------------------------------------------------------------ the words that are not operations

  private static void SendToServer(string rest, Action<string> output)
  {
    var net = ZNet.instance;
    if (net == null)
    {
      output("bc_bake server: not connected to a server.");
      return;
    }
    // A line the server would refuse is said here, before it travels.
    var command = BakeCommandLine.Parse(rest);
    if (command.Error != null)
    {
      output(command.Error);
      return;
    }
    if (net.IsServer())
    {
      // This machine runs the world; ZNet.RemoteCommand would log a null peer here.
      Console.instance.TryRunCommand(("bc_bake " + rest).Trim());
      return;
    }
    net.RemoteCommand(("bc_bake " + rest).Trim());
    output("Asked the server (admins only); its answer comes back here and goes to its log.");
  }

  private static void Help(Action<string> output)
  {
    output(BakeCommandLine.Usage);
    output("bc_bake bakes the pieces players built into the world's layer: drawn and solid on every client, never an object, so they cost no instance, no save size and no network traffic.");
    output("bc_bake area 40 [town] [any]      a dry run: what a bake of the 40 m around you would do; add 'confirm' to bake. Doors, chests, fires, beds, stations, lights and comfort stay real pieces;");
    output("                                  'town' makes them protected parts of the layer; 'any' takes pieces nobody built. 'convert' lets a world that is not a Better Continents world become one.");
    output("bc_bake box x1 z1 x2 z2           the same for a box (up to 512 x 512 m).");
    output("bc_bake unbake 40 | box ... | world [all]   turns records back into real pieces (the compiler's too with 'all'). 'unbake world' is the way out: do it before removing Better Continents.");
    output("bc_bake undo [n]                  puts the newest operation (or n) back exactly as it was, from its undo file.");
    output("bc_bake list | info | check       the operations, what this machine holds, and checks of the layer.");
    output("bc_bake load [file] | export [file]   a compiler's placements.bcp into the running world (the layer as it was is kept); the layer written out as it is.");
    output("bc_bake drop compiler | bake n    takes records out without making pieces; 'bc_bake undo' brings a drop back. 'bc_bake drop <world>' is for the main menu.");
    output("bc_bake orphans [destroy]         live pieces whose record is gone and that hold items (a chest) stay where they are; this lists them.");
    output("bc_bake stats | hide | show       on a client: what the drawing costs, and the drawing off and on (for timing).");
    output("bc_bake server ...                the same on a dedicated server, for admins.");
    output("Every operation writes an undo file first and survives a crash or a cut save: the next time the world loads it is settled, and no piece is ever in neither place.");
  }

  private static void ClientTool(BakeVerb verb, Action<string> output)
  {
    var tools = BakeServices.Client;
    if (tools.Unavailable != null)
    {
      output($"bc_bake {verb.ToString().ToLowerInvariant()}: {tools.Unavailable}.");
      return;
    }
    switch (verb)
    {
      case BakeVerb.Stats:
        tools.Stats(output);
        break;
      case BakeVerb.Hide:
        tools.Hide();
        output("bc_bake hide: this client's drawing of baked pieces is off; the colliders stay. 'bc_bake show' draws them again.");
        break;
      default:
        tools.Show();
        output("bc_bake show: this client draws baked pieces again.");
        break;
    }
  }

  private static void Info(Action<string> output)
  {
    var settings = BetterContinents.Settings;
    int live = -1;
    if (ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null)
      live = ZDOMan.instance.m_objectsByID.Values.Count(z => z.IsValid() && ZDOExtraData.GetInt(z.m_uid, BakedKeys.Id, out _));
    foreach (var line in BakeReport.InfoLines(BakedLayerStore.Current, settings.GameTerrain, live, BakeServices.Client.MissingKinds()))
      output(line);
    // What is a consumable is the game's prefabs' to say: only where a world is up.
    if (BakedLayerStore.Current is { } layer && ZNetScene.instance != null)
      foreach (var line in BakedConsumables.InfoLines(layer))
        output(line);
  }

  private static void Orphans(BakeCommand command, Action<string> output)
  {
    var orphans = BakeServices.Orphans;
    if (orphans.Unavailable != null)
    {
      output($"bc_bake orphans: {orphans.Unavailable}.");
      return;
    }
    var list = orphans.List();
    if (list.Count == 0)
    {
      output("bc_bake orphans: none. Every live piece of the layer has its record.");
      return;
    }
    foreach (var orphan in list.Take(40))
      output($"{orphan.Prefab} at {orphan.Position.x:0}, {orphan.Position.y:0}, {orphan.Position.z:0}: {orphan.Reason}{(orphan.HoldsItems ? " (holds items)" : "")}");
    if (list.Count > 40)
      output($"... and {list.Count - 40} more.");
    var chosen = list.Where(o => command.Items || !o.HoldsItems).ToList();
    if (!command.Destroy)
    {
      output($"{list.Count} orphans, {list.Count(o => o.HoldsItems)} holding items. 'bc_bake orphans destroy' shows what would go; 'items' takes the ones that hold items too.");
      return;
    }
    if (!command.Words.Confirm)
    {
      output($"bc_bake orphans destroy: a dry run, nothing changes. {chosen.Count} of {list.Count} would be destroyed" + (command.Items ? "" : " (the ones that hold items stay; add 'items')") + ". Add 'confirm'.");
      return;
    }
    orphans.Destroy(chosen.Select(o => o.Id), output);
  }

  // ------------------------------------------------------------------------------------------------ export, load, check

  private static void Export(BakeContext ctx, string? file, Action<string> output)
  {
    var layer = ctx.Layer.Current;
    if (layer == null)
    {
      output("bc_bake export: this world has no layer.");
      return;
    }
    var path = file ?? Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", BakeJournal.SafeName(ZNet.World?.m_worldName ?? "world"), BetterContinents.BetterContinentsSettings.LayerFileName);
    try
    {
      BakeJournal.WriteAtomic(path, layer.Bytes, layer.Length);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
      output($"bc_bake export: {path} could not be written ({e.Message}).");
      return;
    }
    output($"bc_bake export: the layer (revision {layer.Revision}, {BakeTally.Num(layer.Placements)} records, {BakeRunner.Bytes(layer.Length)}) is written to {path}.");
  }

  private static void Load(BakeContext ctx, BakeCommand command, Action<string> output)
  {
    var path = command.Text ?? Path.Combine(BetterContinents.ConfigMapSourceDir?.Value ?? "", BetterContinents.BetterContinentsSettings.LayerFileName);
    if (command.Text == null && string.IsNullOrEmpty(BetterContinents.ConfigMapSourceDir?.Value))
    {
      output("bc_bake load: give the file ('bc_bake load <file>'), or set Directory in the configuration to the folder that holds placements.bcp.");
      return;
    }
    byte[] bytes;
    try
    {
      bytes = File.ReadAllBytes(path);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
      output($"bc_bake load: {path} cannot be read ({e.Message}).");
      return;
    }
    Start(BakeRunner.Load(ctx, Path.GetFileName(path), bytes, command.Words), "a load", output);
  }

  private static void Check(BakeContext ctx, BakeCommand command, Action<string> output)
  {
    var layer = ctx.Layer.Current;
    if (layer == null)
    {
      output("bc_bake check: this world has no layer.");
      return;
    }
    List<ZoneKey> zones;
    string what;
    if (command.CheckAll || (command.ZoneX == null && CallerPlace() == null))
    {
      zones = layer.Zones.Where(z => z.Placements > 0).Select(z => z.Key).ToList();
      what = "the whole layer";
    }
    else if (command.ZoneX is { } zx && command.ZoneZ is { } zz)
    {
      zones = [new ZoneKey(zx, zz)];
      what = $"zone {zx},{zz}";
    }
    else
    {
      var at = CallerPlace()!.Value;
      zones = [ZoneKey.OfPoint(at.x, at.z)];
      what = $"your zone {zones[0]}";
    }
    foreach (var line in BakeCheck.Lines(ctx, layer, zones, what, WorldSectors.Active))
      output(line);
  }
}

// bc_bake check: the layer's data against itself (duplicates, two sources putting one piece in one place, Live records out of the game's sectors),
// and against the world (a real piece standing where a record is: a bake that was not finished, or a piece built into a baked spot).
internal static class BakeCheck
{
  // One finding: what is wrong, and where.
  internal sealed class Finding
  {
    public string Kind = "";
    public string Where = "";
  }

  internal static List<string> Lines(BakeContext ctx, BakedLayer layer, IReadOnlyList<ZoneKey> zones, string what, bool wideSectors)
  {
    var found = Run(ctx, layer, zones, wideSectors, out long records);
    var lines = new List<string> { $"bc_bake check: {BakeTally.Num(records)} records in {what} ({BakeTally.Num(zones.Count)} zones)." };
    if (found.Count == 0)
    {
      lines.Add("No problem found.");
      return lines;
    }
    foreach (var group in found.GroupBy(f => f.Kind))
    {
      lines.Add($"{group.Key}: {BakeTally.Num(group.Count())}" + (group.Count() > 0 ? " (e.g. " + string.Join("; ", group.Take(3).Select(f => f.Where)) + ")" : ""));
    }
    return lines;
  }

  internal static List<Finding> Run(BakeContext ctx, BakedLayer layer, IReadOnlyList<ZoneKey> zones, bool wideSectors, out long records)
  {
    var found = new List<Finding>();
    records = 0;
    foreach (var zone in zones)
    {
      if (!layer.TryGetZoneRow(zone, out var row) || row.Placements == 0)
        continue;
      var data = layer.Decode(row);
      records += data.Count;
      var cells = new Dictionary<(string Name, long X, long Z), List<int>>();
      var items = new List<(string Name, double X, double Y, double Z, double Yaw, int Source, BakedRole Role)>(data.Count);
      for (int k = 0; k < data.Count; k++)
      {
        var record = data.Record(k);
        var entry = layer.Palette[record.Palette];
        items.Add((entry.Name, record.WorldX, record.WorldY, record.WorldZ, BakedFormat.YawDegrees(record.Yaw), record.SourceNumber, entry.Role));
        var key = (entry.Name, (long)Math.Floor(record.WorldX / 0.5), (long)Math.Floor(record.WorldZ / 0.5));
        if (!cells.TryGetValue(key, out var list))
          cells[key] = list = [];
        list.Add(k);
      }
      for (int k = 0; k < items.Count; k++)
      {
        var a = items[k];
        string at = $"{a.Name} at {a.X:0.##}, {a.Y:0.##}, {a.Z:0.##}";
        if (a.Role == BakedRole.Live && !wideSectors && (Math.Abs(a.X) > 16350.0 || Math.Abs(a.Z) > 16350.0))
          found.Add(new Finding { Kind = "Live records past 16.35 km in a world without wide sectors", Where = at });
        for (long dx = -1; dx <= 1; dx++)
          for (long dz = -1; dz <= 1; dz++)
          {
            if (!cells.TryGetValue((a.Name, (long)Math.Floor(a.X / 0.5) + dx, (long)Math.Floor(a.Z / 0.5) + dz), out var near))
              continue;
            foreach (int j in near)
            {
              if (j <= k)
                continue;
              var b = items[j];
              double dxm = a.X - b.X, dym = a.Y - b.Y, dzm = a.Z - b.Z;
              double dist = Math.Sqrt(dxm * dxm + dym * dym + dzm * dzm);
              double dyaw = Math.Abs(a.Yaw - b.Yaw);
              dyaw = Math.Min(dyaw, 360.0 - dyaw);
              if (dist <= 0.01 && dyaw <= 1.0)
                found.Add(new Finding { Kind = "Duplicates (the same kind within a centimetre and a degree)", Where = at });
              else if (a.Source != b.Source && dist <= 0.5 && dyaw <= 5.0)
                found.Add(new Finding { Kind = "Records of two sources in one place (within half a metre and 5 degrees)", Where = $"{at} (sources {a.Source} and {b.Source})" });
            }
          }
      }
      // Real pieces standing where a record is.
      if (ctx.World.Ready)
      {
        var objects = new List<WorldObject>();
        ctx.World.Gather([zone], objects);
        var byPrefab = objects.GroupBy(o => o.Prefab).ToDictionary(g => g.Key, g => g.ToList());
        for (int k = 0; k < items.Count; k++)
        {
          var a = items[k];
          if (a.Role == BakedRole.Live)
            continue;
          int hash = ctx.World.PrefabOf(a.Name);
          if (hash == 0 || !byPrefab.TryGetValue(hash, out var same))
            continue;
          if (same.Any(o => Math.Abs(o.X - a.X) <= 0.05 && Math.Abs(o.Y - a.Y) <= 0.05 && Math.Abs(o.Z - a.Z) <= 0.05))
            found.Add(new Finding { Kind = "Records with a real piece of the same kind standing there (an unfinished bake, or a piece built into a baked spot)", Where = $"{a.Name} at {a.X:0.##}, {a.Y:0.##}, {a.Z:0.##}" });
        }
      }
    }
    return found;
  }
}

// `bc_bake drop <world>` (4.5), in the main menu: the layer leaves a world's settings file. The old file is kept (outside the world's folder: a file
// with an extension in a world's folder is one the game's save list would look at). A GameTerrain world, which holds nothing else, loses its file
// and is the game's own world again. Live pieces stay as real pieces with their keys, which nothing reads any more.
internal static class BakeDropWorld
{
  internal static void Run(string name, bool confirm, Action<string> say)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      say("bc_bake drop: the name of a world, as the world list shows it. (In a world, 'bc_bake drop compiler' and 'bc_bake drop bake <n>' take records out.)");
      return;
    }
    var path = BetterContinents.GetWorldBCFile(name, FileHelpers.FileSource.Local);
    var backup = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", "pre-drop", BakeJournal.SafeName(name), "BetterContinents.pre-drop");
    RunOnFile(path, backup, name, confirm, say);
  }

  internal static void RunOnFile(string path, string backup, string name, bool confirm, Action<string> say)
  {
    if (!File.Exists(path))
    {
      say($"bc_bake drop: the world '{name}' has no Better Continents settings here ({path}); nothing to drop.");
      return;
    }
    BetterContinents.BetterContinentsSettings settings;
    try
    {
      settings = BetterContinents.BetterContinentsSettings.Load(path);
    }
    catch (Exception e)
    {
      say($"bc_bake drop: the settings of '{name}' cannot be read ({e.Message}); nothing is changed.");
      return;
    }
    if (!settings.EnabledForThisWorld)
    {
      say($"bc_bake drop: the settings of '{name}' are not a Better Continents world's; nothing is changed.");
      return;
    }
    if (settings.Layer == null)
    {
      say(settings.LayerError != null
        ? $"bc_bake drop: the layer in the settings of '{name}' cannot be read ({settings.LayerError}); nothing is changed."
        : $"bc_bake drop: the world '{name}' has no layer.");
      return;
    }
    var layer = settings.Layer;
    long inGame = layer.Placements - layer.RecordsOfSource(0);
    if (!confirm)
    {
      say($"bc_bake drop {name}: a dry run, nothing changes. 'bc_bake drop {name} confirm' drops the layer.");
      if (inGame > 0)
        say($"This world's layer holds {BakeTally.Num(inGame)} pieces baked in the game. Dropping it removes them for good (a copy of the file is kept at {backup}). "
          + "To keep them as real pieces, load the world and run 'bc_bake unbake world confirm' first.");
      else
        say($"This world's layer holds {BakeTally.Num(layer.Placements)} records from a compiler's file and none baked in the game. A copy of the file is kept at {backup}.");
      say(settings.GameTerrain
        ? "The world keeps the game's own terrain, and the layer is all its settings hold: the file goes, and the world is the game's own world again."
        : "The world keeps its Better Continents settings and maps; only the layer goes.");
      return;
    }
    try
    {
      BakeJournal.WriteAtomic(backup, File.ReadAllBytes(path));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      say($"bc_bake drop: the old file could not be kept at {backup} ({e.Message}); nothing is changed.");
      return;
    }
    try
    {
      if (settings.GameTerrain)
        File.Delete(path);
      else
      {
        settings.Layer = null;
        // In the format the world's own settings were in (not the configured Override version): the file is the same, less the layer.
        var package = new ZPackage();
        settings.Serialize(package, false, true, settings.SavedVersion);
        var bytes = PackageBytes.Buffer(package, out int length);
        var file = new byte[length + 4];
        BitConverter.GetBytes(length).CopyTo(file, 0);
        Buffer.BlockCopy(bytes, 0, file, 4, length);
        BakeJournal.WriteAtomic(path, file);
      }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      say($"bc_bake drop: the settings of '{name}' could not be changed ({e.Message}); the old file is at {backup}.");
      return;
    }
    say($"bc_bake drop: the layer of '{name}' is gone ({BakeTally.Num(layer.Placements)} records)" + (settings.GameTerrain ? "; the world is the game's own world again" : "") + $". The old file is kept at {backup}.");
  }
}

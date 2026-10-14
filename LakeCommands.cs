// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace BetterContinents;

// bc_lake (BUILD-SPEC-TARN.md 2.7): the console tool of the world's lakes and clearing mask. Built like bc_bake and bc_twins: not part of the `bc` cheat tree, an answer on
// the console that asked and in the log, and a `server` prefix that reaches a dedicated server for admins (ConsoleTools). The host's `bc` tree has it as `bc lake`.
//
//   single player        the player
//   a hosted game        the host at their own console; an admin of the host's list through `bc_lake server ...`
//   a dedicated server   an admin through `bc_lake server ...` (the game checks adminlist.txt)
//   any client           the read-only words: help, info, list, mask at, export, mask export
// No Debug Mode and no devcommands are needed. Every word that replaces or removes lakes or the mask in bulk is a dry run unless `confirm` is given; a single `here`, `at`
// or `depth` is made at once. A change is saved with the world at its next save, and the lakes are sent to every player in it (LakeTransfer).
//
// While the Tarn mod is installed Better Continents' lakes and clearing are off (TarnCompat): the words that change the world say so and do nothing.
internal static class LakeCommands
{
  public static void Register()
  {
    // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
    new Terminal.ConsoleCommand("bc_lake",
      "[help | info | list | here | at | remove | depth | load | import | export | mask ... | regen | server ...] - Better Continents: the world's lakes above the sea and its mask of ground kept clear of plants (bc_lake help)",
      args => Run(args),
      isCheat: false, isNetwork: false, onlyServer: false,
      optionsFetcher: () => ["help", "info", "list", "here", "at", "remove", "depth", "load", "import", "export", "mask", "regen", "server"]);
  }

  // The answer goes to the console that asked, the log, and the admin a server runs it for (ConsoleTools).
  private static void Run(Terminal.ConsoleEventArgs args) => RunLine(ConsoleTools.Rest(args, "bc_lake"), ConsoleTools.Output(args.Context));

  // ---- the seams: the game's side, which the offline tests stand in for -------------------------------------------------------------------------------

  internal static class Seams
  {
    public static Func<bool> RunsWorld = () => BakedTransfer.RunsWorld();
    public static Func<bool> InWorld = () => ZNet.instance != null && ZDOMan.instance != null && ZNetScene.instance != null && ZNet.World != null;
    public static Func<bool> IsServerMachine = () => ZNet.instance != null && ZNet.instance.IsServer();
    public static Func<string> WorldName = () => ZNet.World?.m_name ?? "unknown";
    // <save data>/BetterContinents/<world>/, where the backups and the exports go.
    public static Func<string> SaveFolder = () => Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", WorldName());
    // The Directory setting.
    public static Func<string> MapDirectory = () => BC.ConfigMapSourceDir?.Value ?? "";
    public static Func<DateTime> Now = () => DateTime.Now;
    public static Func<(float X, float Z)?> CallerPlace = DefaultCallerPlace;
    public static Func<BakeWorldKind> Kind = BakeRuntime.KindOfWorld;
    // Everyone connected, with the Better Continents version their handshake told (null: none): a conversion needs them all on this version.
    public static Func<IReadOnlyList<(string Name, string? Version)>> Peers = () => new BakedTransport().Peers;
    // Makes a vanilla world a Better Continents world that keeps the game's terrain, carrying these lakes and this mask, and tells every player.
    public static Action<LakeSet?, ClearMask?> Convert = (lakes, mask) => new BakedConvert().Convert(lakes, mask);
    // Sends the new lakes to the players in the world; how many it went to.
    public static Func<LakeSet, int> Push = LakeTransfer.Broadcast;
    // The mask was replaced: the patch that follows it is switched.
    public static Action PatchClearing = BC.PatchClearingToggles;
    // The zones the world has generated (zone x, zone z).
    public static Func<IReadOnlyCollection<(int X, int Z)>> GeneratedZones = () =>
      ZoneSystem.instance == null ? [] : ZoneSystem.instance.m_generatedZones.Select(z => ((int)z.x, (int)z.y)).ToList();
    // What `bc regen` runs.
    public static Action Regenerate = GameUtils.RegenerateZones;
    // Whether this machine may export (the server's say, WorldExport.Allowed) and why not.
    public static Func<string?> ExportRefusal = () =>
    {
      try
      {
        return WorldExport.Allowed == null || WorldExport.Allowed() ? null : WorldExport.NotAllowedReason?.Invoke() ?? "world export is not allowed here";
      }
      catch (Exception e)
      {
        return "world export is not allowed here (" + e.Message + ")";
      }
    };
    // The world's own Deep North and hot sea at a point (the game's functions, which Better Continents' patches make the world's own): a lake in either is warned about.
    public static Func<float, float, bool> Deep = (x, z) => WorldGenerator.instance != null && WorldGenerator.IsDeepnorth(x, z);
    public static Func<float, float, bool> Hot = (x, z) => WorldGenerator.instance != null && WorldGenerator.GetAshlandsOceanGradient(x, z) > 0f;
    // A command sent to the dedicated server (admins): the machine's console and the remote command.
    public static Action<string> RunOnThisMachine = line => Console.instance.TryRunCommand(line);
    public static Action<string> RemoteCommand = line => ZNet.instance.RemoteCommand(line);

    // ---- the lake engine (agent B) -----------------------------------------------------------------------------------------------------------------
    // PLACEHOLDERS, until the engine is merged: `here` and `at` call LakeSpill's entry point (a small `internal static` in LakeSpill.cs that B publishes), which finds the
    // hollow, closes the lake under the builder's own sampling and answers with the lake; `list` and `info` ask which lakes are built and how the water stands.
    public static Func<float, float, float, float, HereOutcome> Spill = (x, z, radius, margin) => HereOutcome.NotAvailable;
    // "built", "building", "not built" or "failed: ...": null when the engine says nothing (it is not in this build).
    public static Func<Lake, string?> BuiltState = _ => null;
    public static Func<IEnumerable<string>>? EngineInfo = null;
  }

  // What the spill answers: the lake to add (null when there is none) and the words to say, a refusal included.
  internal sealed record HereOutcome(Lake? Lake, string Message)
  {
    public static readonly HereOutcome NotAvailable = new(null, "not available yet: the lake engine's spill is not in this build.");
  }

  private static (float X, float Z)? DefaultCallerPlace()
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

  // ---- the line ---------------------------------------------------------------------------------------------------------------------------------------

  // bc_lake, and "bc lake" in the host's debug tree.
  internal static void RunLine(string text, Action<string> output)
  {
    var command = LakeCommandLine.Parse(text);
    if (command.Error != null)
    {
      output(command.Error);
      output(LakeCommandLine.Usage);
      return;
    }
    bool inWorld = Seams.InWorld();
    var refusal = LakeCommandLine.Refusal(command.Verb, Seams.RunsWorld(), inWorld);
    if (refusal != null)
    {
      output("bc_lake: " + refusal);
      return;
    }
    if (LakeCommandLine.Changes(command.Verb) && TarnCompat.Refusal is { } tarn)
    {
      output("bc_lake: " + tarn);
      return;
    }
    try
    {
      switch (command.Verb)
      {
        case LakeVerb.Help: Help(output); break;
        case LakeVerb.Server: SendToServer(command.Text ?? "", output); break;
        case LakeVerb.Info: Info(output); break;
        case LakeVerb.List: List(command.Count, output); break;
        case LakeVerb.MaskAt: MaskAt(command.X!.Value, command.Z!.Value, output); break;
        case LakeVerb.Export: Export(command.Text, output); break;
        case LakeVerb.MaskExport: MaskExport(command.Text!, output); break;
        case LakeVerb.Depth: Depth(command.Scale, output); break;
        case LakeVerb.Remove: Remove(command.Text!, command.Confirm, output); break;
        case LakeVerb.Here:
        case LakeVerb.At:
          Here(command, output);
          break;
        case LakeVerb.Load: Load(command, output); break;
        case LakeVerb.Import: Import(command, output); break;
        case LakeVerb.MaskLoad: MaskLoad(command, output); break;
        case LakeVerb.Regen: Regen(command.Confirm, output); break;
      }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
    {
      output($"bc_lake {command.Verb.ToString().ToLowerInvariant()}: {e.Message}");
    }
  }

  private static void SendToServer(string rest, Action<string> output)
  {
    if (ZNet.instance == null && !Seams.InWorld())
    {
      output("bc_lake server: not connected to a server.");
      return;
    }
    // A line the server would refuse is said here, before it travels (the parser ran in RunLine for the whole line, rest included).
    if (Seams.IsServerMachine())
    {
      // This machine runs the world; ZNet.RemoteCommand would log a null peer here.
      Seams.RunOnThisMachine(("bc_lake " + rest).Trim());
      return;
    }
    Seams.RemoteCommand(("bc_lake " + rest).Trim());
    output("Asked the server (admins only); its answer comes back here and goes to its log.");
  }

  private static void Help(Action<string> output)
  {
    output(LakeCommandLine.Usage);
    output("bc_lake: a world's lakes sit at their own height above the sea, drawn with the game's water, swimmable and sailable, and on the map; its clearing mask keeps trees, rocks and bushes off the ground it marks (roads, lake beds). Lakes and clearing come from Raven Iron's Tarn.");
    output("bc_lake info | list [n]            the lakes and the mask in words; the n nearest lakes (default 10).");
    output("bc_lake here [radius] [margin]      fills the hollow you stand in, up to just under its spill point (Tarn's `here`); `at <x> <z> ...` does it at a place.");
    output("bc_lake remove <name|all>           takes a lake out; `all` is a dry run until you add 'confirm'. The old list is first written to lakes-before-<time>.txt in the world's save folder.");
    output("bc_lake depth [scale]               how deep the water shader takes lakes to be, 0 to 3 (0.5): colour, foam and the waves boats feel follow it. One value for the world.");
    output("bc_lake load [folder]               lakes.txt and clearmask.bin of the Directory (or the folder) into the running world; a dry run until 'confirm'. 'convert' lets a world that is not a Better Continents world become one.");
    output("bc_lake import [path]               Tarn's own <world>.txt and <world>.clear from BepInEx/config/Tarn (or the folder), the same way.");
    output("bc_lake export [folder]             writes lakes.txt and clearmask.bin (Tarn's formats) to a folder.");
    output("bc_lake mask at <x> <z> | load <file> | export <file>   whether a point is cleared; the mask alone, from and to a file. The mask is not sent to players: only the machine that runs the world places vegetation.");
    output("bc_lake regen                       zones already generated keep their plants: this says how many the lakes' boxes and the mask touch, and with 'confirm' regenerates every unprotected generated zone (as `bc regen` does).");
    output("bc_lake server ...                  the same on a dedicated server, for admins.");
    output("Nothing is saved until the world is: a change is saved with it at its next save. Lakes you change are sent to every player in the world at once.");
  }

  // ---- the lakes and the mask in words ----------------------------------------------------------------------------------------------------------------

  // The distance from a point to a lake's box, 0 inside it [T: Plugin.LakeRt.DistanceTo].
  internal static float Distance(Lake l, float x, float z)
  {
    float dx = Mathf.Max(0f, Mathf.Max(l.MinX - x, x - l.MaxX));
    float dz = Mathf.Max(0f, Mathf.Max(l.MinZ - z, z - l.MaxZ));
    return Mathf.Sqrt(dx * dx + dz * dz);
  }

  private static string Scale(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

  private static string Km2(ClearMask mask) => mask.Km2.ToString("0.00", CultureInfo.InvariantCulture);

  private static void Info(Action<string> output)
  {
    var s = BC.Settings;
    if (TarnCompat.Installed)
      output("bc_lake info: " + TarnCompat.Message + " The world's lakes and mask are kept and saved with it.");
    if (!s.EnabledForThisWorld)
    {
      output("bc_lake info: this world is not a Better Continents world, so it has no lakes or clearing mask. 'bc_lake load <folder> convert' or 'bc_lake import convert' makes it one that has.");
      return;
    }
    var place = Seams.CallerPlace();
    if (s.Lakes is { IsEmpty: false } lakes)
    {
      var near = place is { } p ? lakes.Lakes.OrderBy(l => Distance(l, p.X, p.Z)).Take(3).ToList() : lakes.Lakes.Take(3).ToList();
      output($"bc_lake info: {lakes.Lakes.Count:N0} lake{(lakes.Lakes.Count == 1 ? "" : "s")}, depth scale {Scale(lakes.DepthScale)}; "
        + (place != null ? "nearest " : "first ") + string.Join(", ", near.Select(l => place is { } q ? $"{l.Name} ({Distance(l, q.X, q.Z):0} m)" : l.Name)) + ".");
      int hot = lakes.Lakes.Count(l => Seams.Hot(l.X, l.Z)), deep = lakes.Lakes.Count(l => Seams.Deep(l.X, l.Z));
      if (hot + deep > 0)
        output($"bc_lake info: {hot + deep} of them lie in the world's {(hot > 0 ? "hot sea (swimmers and boats take heat damage there, and the water is red)" : "")}{(hot > 0 && deep > 0 ? " or " : "")}{(deep > 0 ? "Deep North (calm)" : "")}.");
    }
    else if (s.Lakes != null)
      output($"bc_lake info: this world has no lakes (depth scale {Scale(s.Lakes.DepthScale)}).");
    else if (s.LakesError != null)
      output($"bc_lake info: the lakes in this world's settings cannot be read by this build ({s.LakesError}); they are kept as they are.");
    else
      output("bc_lake info: this world has no lakes.");
    if (s.Clearing is { } mask)
    {
      if (!mask.Decoded && Seams.RunsWorld())
        mask.Verify();
      output($"bc_lake info: clearing mask {mask.Size} x {mask.Size} over +-{mask.Extent.ToString("0", CultureInfo.InvariantCulture)} m ({Scale(mask.MetresPerPixel)} m a pixel)"
        + (mask.Decoded ? $", {Km2(mask)} km2 kept free of vegetation." : ", opened where the world runs."));
    }
    else if (s.ClearingError != null)
      output($"bc_lake info: the clearing mask in this world's settings cannot be read by this build ({s.ClearingError}); it is kept as it is.");
    else
      output("bc_lake info: this world has no clearing mask.");
    if (Seams.EngineInfo != null)
      foreach (var line in Seams.EngineInfo())
        output(line);
  }

  private static void List(int count, Action<string> output)
  {
    var lakes = BC.Settings.Lakes;
    if (lakes == null || lakes.IsEmpty)
    {
      output("bc_lake list: this world has no lakes.");
      return;
    }
    var place = Seams.CallerPlace();
    var ordered = place is { } p ? lakes.Lakes.OrderBy(l => Distance(l, p.X, p.Z)).ToList() : lakes.Lakes.ToList();
    output($"bc_lake list: {lakes.Lakes.Count:N0} lake{(lakes.Lakes.Count == 1 ? "" : "s")}, the {Math.Min(count, ordered.Count)} {(place != null ? "nearest" : "first")}:");
    foreach (var l in ordered.Take(count))
    {
      var built = Seams.BuiltState(l);
      output($"{l.Name}: ({l.X:0}, {l.Z:0}) level {l.Level:0.0}" + (place is { } q ? $", {Distance(l, q.X, q.Z):0} m away" : "") + (built != null ? ", " + built : ""));
    }
  }

  private static void MaskAt(float x, float z, Action<string> output)
  {
    var mask = BC.Settings.Clearing;
    if (mask == null)
    {
      output("bc_lake mask at: this world has no clearing mask.");
      return;
    }
    var covered = mask.Covers(x, z);
    var pixel = mask.PixelAt(x, z);
    output($"bc_lake mask at: ({x:0.##}, {z:0.##}) is {(covered ? "cleared: no tree, rock or bush is placed there" : "not cleared")}"
      + (pixel is { } px ? $" (pixel row {px.Row}, column {px.Column}, {(mask.At(px.Row, px.Column) ? "marked" : "not marked")})" : ", outside the mask") + ". Only zones generated after the mask is in the world are affected.");
  }

  // ---- depth, remove ---------------------------------------------------------------------------------------------------------------------------------

  // Whether this world's lakes may be changed here: it is a Better Continents world whose lakes this build reads. The reason is said when not.
  private static bool MayChange(string word, Action<string> output, bool replacing = false)
  {
    var kind = Seams.Kind();
    if (kind == BakeWorldKind.Unreadable)
    {
      output($"bc_lake {word}: this world has a Better Continents settings file that cannot be read (it was written by a newer Better Continents, or it is damaged), so nothing is changed. Open the world with the Better Continents that made it, or restore the file.");
      return false;
    }
    if (kind == BakeWorldKind.Vanilla || !BC.Settings.EnabledForThisWorld)
    {
      output($"bc_lake {word}: this world is not a Better Continents world. 'bc_lake load <folder> convert' (or 'bc_lake import convert') makes it one that keeps the game's own terrain and has lakes.");
      return false;
    }
    if (!replacing && BC.Settings.LakesError != null)
    {
      output($"bc_lake {word}: the lakes in this world's settings cannot be read by this build ({BC.Settings.LakesError}), so they are not changed. 'bc_lake load confirm' or 'bc_lake import confirm' replaces them.");
      return false;
    }
    return true;
  }

  private static void Depth(float? scale, Action<string> output)
  {
    var s = BC.Settings;
    if (scale == null)
    {
      output($"bc_lake depth: the water shader takes lakes to be {Scale(s.EffectiveLakeDepthScale)} of their real depth (colour, foam and waves reach full at 10 m, and the waves boats and swimmers feel follow it). Try 0.2 (calm, green) to 1 (sea-like): bc_lake depth <scale>");
      return;
    }
    if (!MayChange("depth", output))
      return;
    var set = s.SetLakeDepthScale(scale.Value);
    int sent = Seams.Push(set);
    LakeTransfer.LakesChanged?.Invoke(set);
    output($"bc_lake depth: depth scale {Scale(scale.Value)}, saved with the world at its next save; sent to {sent} player{(sent == 1 ? "" : "s")}.");
  }

  private static void Remove(string name, bool confirm, Action<string> output)
  {
    if (!MayChange("remove", output))
      return;
    var s = BC.Settings;
    var lakes = s.Lakes;
    if (lakes == null || lakes.IsEmpty)
    {
      output("bc_lake remove: this world has no lakes.");
      return;
    }
    var after = lakes.Removed(name, out int removed);
    if (removed == 0)
    {
      output($"bc_lake remove: no lake is named '{name}'. 'bc_lake list' names them.");
      return;
    }
    if (name == "all" && !confirm)
    {
      output($"bc_lake remove all: a dry run, nothing changes. All {removed:N0} lakes would be removed (the list is first written to lakes-before-<time>.txt in the world's save folder). Add 'confirm'.");
      return;
    }
    if (!Backup(output, "remove", lakes, null))
      return;
    s.ReplaceLakes(after);
    int sent = Seams.Push(after);
    LakeTransfer.LakesChanged?.Invoke(after);
    output($"bc_lake remove: removed {removed:N0} lake{(removed == 1 ? "" : "s")}; {after.Lakes.Count:N0} left. Saved with the world at its next save; sent to {sent} player{(sent == 1 ? "" : "s")}. "
      + "The map keeps a removed lake's picture until it is drawn again.");
  }

  // ---- here, at ---------------------------------------------------------------------------------------------------------------------------------------

  private static void Here(LakeCommand command, Action<string> output)
  {
    string word = command.Verb == LakeVerb.Here ? "here" : "at";
    float x, z;
    if (command.Verb == LakeVerb.At)
    {
      x = command.X!.Value;
      z = command.Z!.Value;
    }
    else if (Seams.CallerPlace() is { } place)
      (x, z) = place;
    else
    {
      output("bc_lake here: there is no one to take the place from: give it with 'at <x> <z>'.");
      return;
    }
    if (!MayChange(word, output))
      return;
    var s = BC.Settings;
    if (s.Lakes != null && s.Lakes.Lakes.Count >= LakeSet.MaxLakes)
    {
      output($"bc_lake {word}: a world holds at most {LakeSet.MaxLakes:N0} lakes.");
      return;
    }
    // PLACEHOLDER (agent B): the spill. Until LakeSpill's entry point is published this says 'not available yet'.
    var outcome = Seams.Spill(x, z, command.Radius, command.Margin);
    if (outcome.Lake == null)
    {
      output($"bc_lake {word}: {outcome.Message}");
      return;
    }
    var set = s.Lakes ?? LakeSet.Empty.WithDepthScale(s.EffectiveLakeDepthScale);
    var lake = outcome.Lake with { Name = set.NextName() };
    var next = set.Added(lake);
    s.ReplaceLakes(next);
    int sent = Seams.Push(next);
    LakeTransfer.LakesChanged?.Invoke(next);
    output($"{lake.Name}: {outcome.Message}. Saved with the world at its next save; sent to {sent} player{(sent == 1 ? "" : "s")}.");
    foreach (var warning in Guards([lake]))
      output("bc_lake " + word + ": " + warning);
  }

  // The words about lakes in the world's hot sea or Deep North [BUILD-SPEC-TARN.md 3.5].
  internal static List<string> Guards(IReadOnlyCollection<Lake> lakes)
  {
    var words = new List<string>();
    int hot = 0, deep = 0;
    string? firstHot = null, firstDeep = null;
    try
    {
      foreach (var l in lakes)
      {
        if (Seams.Hot(l.X, l.Z)) { hot++; firstHot ??= l.Name; }
        if (Seams.Deep(l.X, l.Z)) { deep++; firstDeep ??= l.Name; }
      }
    }
    catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
    {
      // No world generator to ask (the main menu): nothing to say.
      return words;
    }
    bool one = lakes.Count == 1;
    if (hot > 0)
      words.Add($"{(one ? "this lake is" : $"{hot:N0} of these lakes, {firstHot} first, are")} inside the world's hot sea: swimmers and boats in {(one ? "it" : "them")} take heat damage, and the water is drawn red.");
    if (deep > 0)
      words.Add($"{(one ? "this lake is" : $"{deep:N0} of these lakes, {firstDeep} first, are")} inside the world's Deep North: the water is calm there.");
    return words;
  }

  // ---- load, import, mask load: replacing -------------------------------------------------------------------------------------------------------------

  // The files a load reads: a folder's lakes.txt and clearmask.bin, or (no folder) the Directory's.
  private sealed class Loaded
  {
    public string Folder = "";
    public LakeSet? Lakes;
    public ClearMask? Clearing;
    public string? LakesPath, ClearingPath;
    public List<string> Problems = [];
  }

  private static Loaded? ReadFolder(string word, string? folder, Action<string> output)
  {
    var dir = string.IsNullOrWhiteSpace(folder) ? Seams.MapDirectory() : folder!.Trim();
    if (string.IsNullOrWhiteSpace(dir))
    {
      output($"bc_lake {word}: name a folder (bc_lake {word} <folder>): the Directory setting is empty.");
      return null;
    }
    if (!Directory.Exists(dir))
    {
      output($"bc_lake {word}: {dir} is not a folder.");
      return null;
    }
    var loaded = new Loaded { Folder = dir };
    if (LakeFiles.FindLakes(dir, out _) is { } lakesPath)
      try
      {
        var set = LakeFiles.ReadLakes(lakesPath);
        if (set.IsEmpty)
          loaded.Problems.Add($"{lakesPath} holds no lake");
        else
        {
          loaded.Lakes = set;
          loaded.LakesPath = lakesPath;
        }
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
        loaded.Problems.Add($"{lakesPath} cannot be read ({e.Message})");
      }
    if (LakeFiles.FindClearing(dir, out _) is { } clearPath)
      try
      {
        loaded.Clearing = LakeFiles.ReadClearing(clearPath);
        loaded.ClearingPath = clearPath;
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
      {
        loaded.Problems.Add($"{clearPath} cannot be read ({e.Message})");
      }
    return loaded;
  }

  private static void Load(LakeCommand command, Action<string> output)
  {
    var loaded = ReadFolder("load", command.Text, output);
    if (loaded == null)
      return;
    Replace("load", loaded.Folder, loaded, command, output);
  }

  private static void Import(LakeCommand command, Action<string> output)
  {
    var folder = string.IsNullOrWhiteSpace(command.Text) ? LakeFiles.TarnFolder() : command.Text!.Trim();
    var world = Seams.WorldName();
    if (!Directory.Exists(folder))
    {
      output($"bc_lake import: {folder} is not a folder (Tarn keeps its files in BepInEx/config/Tarn).");
      return;
    }
    var read = LakeFiles.ReadTarn(world, folder);
    var loaded = new Loaded { Folder = folder, Lakes = read.Lakes, Clearing = read.Clearing, LakesPath = read.LakesPath, ClearingPath = read.ClearingPath, Problems = read.Problems };
    if (loaded.Lakes == null && loaded.Clearing == null && loaded.Problems.Count == 0)
    {
      output($"bc_lake import: {folder} holds no {world}.txt or {world}.clear (Tarn's files are named for the world).");
      return;
    }
    Replace("import", folder, loaded, command, output);
  }

  private static void MaskLoad(LakeCommand command, Action<string> output)
  {
    var path = command.Text!.Trim();
    if (Directory.Exists(path))
      path = LakeFiles.FindClearing(path, out _) ?? Path.Combine(path, LakeFiles.ClearFileName);
    var loaded = new Loaded { Folder = Path.GetDirectoryName(path) ?? "" };
    if (!File.Exists(path))
    {
      output($"bc_lake mask load: {path} does not exist.");
      return;
    }
    try
    {
      loaded.Clearing = LakeFiles.ReadClearing(path);
      loaded.ClearingPath = path;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
    {
      output($"bc_lake mask load: {path} cannot be read ({e.Message}).");
      return;
    }
    Replace("mask load", path, loaded, command, output);
  }

  // The dry run or the change for load, import and mask load: what the files hold against what the world has, and with 'confirm' the swap (a vanilla world only with 'convert').
  private static void Replace(string word, string from, Loaded loaded, LakeCommand command, Action<string> output)
  {
    foreach (var problem in loaded.Problems)
      output($"bc_lake {word}: {problem}: left out.");
    if (loaded.Lakes == null && loaded.Clearing == null)
    {
      output($"bc_lake {word}: nothing to load from {from}: " + (word == "mask load" ? "no clearing mask." : "no lakes and no clearing mask."));
      return;
    }
    var s = BC.Settings;
    var kind = Seams.Kind();
    if (kind == BakeWorldKind.Unreadable)
    {
      MayChange(word, output, replacing: true);
      return;
    }
    bool vanilla = kind == BakeWorldKind.Vanilla || !s.EnabledForThisWorld;
    // What the files hold, and what the world has now.
    var holds = new List<string>();
    var has = new List<string>();
    if (loaded.Lakes != null)
    {
      holds.Add($"{loaded.Lakes.Lakes.Count:N0} lake{(loaded.Lakes.Lakes.Count == 1 ? "" : "s")} ({Path.GetFileName(loaded.LakesPath)})");
      has.Add(s.Lakes is { IsEmpty: false } l0 ? $"{l0.Lakes.Count:N0} lakes" : s.LakesError != null ? "lakes this build cannot read, which are replaced" : "no lakes");
    }
    if (loaded.Clearing != null)
    {
      holds.Add($"a clearing mask of {loaded.Clearing.Size} x {loaded.Clearing.Size} over +-{loaded.Clearing.Extent.ToString("0", CultureInfo.InvariantCulture)} m, {Km2(loaded.Clearing)} km2 ({Path.GetFileName(loaded.ClearingPath)})");
      has.Add(s.Clearing != null ? $"a mask of {s.Clearing.Size} x {s.Clearing.Size}" : s.ClearingError != null ? "a mask this build cannot read, which is replaced" : "no mask");
    }
    var warnings = loaded.Lakes != null ? Guards(loaded.Lakes.Lakes.ToList()) : [];
    string convertWarning = "this world is not a Better Continents world. Loading makes it one: its ground, biomes, vegetation and locations stay the game's own, and Better Continents keeps the lakes with the world. "
      + "From then on every player needs the same Better Continents version as this world to join it, and an older Better Continents, or the game without it, opens it without them.";
    if (!command.Confirm)
    {
      output($"bc_lake {word}: a dry run, nothing changes. {from} holds {string.Join(" and ", holds)}; the world has {string.Join(" and ", has)}.");
      foreach (var w in warnings)
        output($"bc_lake {word}: {w}");
      if (vanilla)
        output($"bc_lake {word}: {convertWarning} Add 'convert' and 'confirm' to go ahead.");
      else
        output($"bc_lake {word}: add 'confirm' to load {(holds.Count > 1 ? "them" : "it")}." + (loaded.Lakes != null && s.Lakes is { IsEmpty: false } ? " The lakes you have are first written to lakes-before-<time>.txt in the world's save folder." : ""));
      return;
    }
    // The depth scale the new lakes have: the world's own; Tarn's own, for Tarn's files; the config's for a world that has none yet.
    float scale = word == "import" && loaded.Lakes != null ? loaded.Lakes.DepthScale : s.Lakes?.DepthScale ?? LakeSettings.DepthScale?.Value ?? LakeSet.DefaultDepthScale;
    var newLakes = loaded.Lakes?.WithDepthScale(scale);
    if (vanilla)
    {
      if (!command.Convert)
      {
        output($"bc_lake {word}: {convertWarning} Add 'convert' to go ahead.");
        return;
      }
      var others = Seams.Peers().Where(p => p.Version != ModInfo.Version).ToList();
      if (others.Count > 0)
      {
        output($"bc_lake {word}: " + string.Join(", ", others.Select(p => $"{p.Name} ({(p.Version == null ? "no Better Continents" : "Better Continents " + p.Version)})"))
          + $" would not be able to join the converted world: every player needs Better Continents {ModInfo.Version}. Nothing is changed.");
        return;
      }
      Seams.Convert(newLakes, loaded.Clearing);
      LakeTransfer.LakesChanged?.Invoke(BC.Settings.Lakes);
      output($"bc_lake {word}: this world keeps the game's own terrain and is a Better Continents world now, with {string.Join(" and ", holds)}. Saved with the world at its next save; every player in it was sent the settings.");
    }
    else
    {
      if (!Backup(output, word, newLakes != null ? s.Lakes : null, loaded.Clearing != null ? s.Clearing : null))
        return;
      if (newLakes != null)
        s.ReplaceLakes(newLakes);
      if (loaded.Clearing != null)
      {
        s.ReplaceClearing(loaded.Clearing);
        Seams.PatchClearing();
      }
      int sent = newLakes != null ? Seams.Push(newLakes) : 0;
      if (newLakes != null)
        LakeTransfer.LakesChanged?.Invoke(newLakes);
      output($"bc_lake {word}: loaded {string.Join(" and ", holds)}. Saved with the world at its next save" + (newLakes != null ? $"; the lakes were sent to {sent} player{(sent == 1 ? "" : "s")}" : "") + ".");
    }
    foreach (var w in warnings)
      output($"bc_lake {word}: {w}");
    if (loaded.Clearing != null)
      output($"bc_lake {word}: the mask is not sent to players (only the machine that runs the world places vegetation). Zones already generated keep their plants: 'bc_lake regen' says how many it touches.");
  }

  // The old lakes and mask, written before they are replaced or removed. False (and the words) when they could not be: nothing is then changed.
  private static bool Backup(Action<string> output, string word, LakeSet? lakes, ClearMask? mask)
  {
    bool someLakes = lakes is { IsEmpty: false };
    if (!someLakes && mask == null)
      return true;
    try
    {
      var folder = Seams.SaveFolder();
      Directory.CreateDirectory(folder);
      var stamp = Seams.Now().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
      if (someLakes)
      {
        var path = Path.Combine(folder, $"lakes-before-{stamp}.txt");
        LakeFiles.WriteLakes(path, lakes!);
        output($"bc_lake {word}: the lakes as they were are in {path}.");
      }
      if (mask != null)
      {
        var path = Path.Combine(folder, $"clearmask-before-{stamp}.bin");
        LakeFiles.WriteClearing(path, mask);
        output($"bc_lake {word}: the clearing mask as it was is in {path}.");
      }
      return true;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or LakeFormatException)
    {
      output($"bc_lake {word}: the old lakes could not be written to the save folder ({e.Message}), so nothing was changed.");
      return false;
    }
  }

  // ---- export -----------------------------------------------------------------------------------------------------------------------------------------

  private static void Export(string? folder, Action<string> output)
  {
    if (Seams.ExportRefusal() is { } why)
    {
      output("bc_lake export: " + why + ".");
      return;
    }
    var s = BC.Settings;
    var lakes = s.Lakes is { IsEmpty: false } l ? l : null;
    var mask = s.Clearing;
    if (lakes == null && mask == null)
    {
      output("bc_lake export: this world has no lakes and no clearing mask.");
      return;
    }
    var dir = string.IsNullOrWhiteSpace(folder) ? Path.Combine(Seams.SaveFolder(), "lakes-" + Seams.Now().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)) : folder!.Trim();
    var written = new List<string>();
    if (lakes != null)
    {
      LakeFiles.WriteLakes(Path.Combine(dir, LakeFiles.LakesFileName), lakes);
      written.Add($"{LakeFiles.LakesFileName} ({lakes.Lakes.Count:N0} lakes)");
    }
    if (mask != null)
    {
      LakeFiles.WriteClearing(Path.Combine(dir, LakeFiles.ClearFileName), mask);
      written.Add($"{LakeFiles.ClearFileName} ({mask.Size} x {mask.Size})");
    }
    output($"bc_lake export: wrote {string.Join(" and ", written)} to {dir}, in Tarn's formats. Setting Directory to the folder (or 'bc_lake load {dir}') gives a world the same.");
  }

  private static void MaskExport(string file, Action<string> output)
  {
    if (Seams.ExportRefusal() is { } why)
    {
      output("bc_lake mask export: " + why + ".");
      return;
    }
    var mask = BC.Settings.Clearing;
    if (mask == null)
    {
      output("bc_lake mask export: this world has no clearing mask.");
      return;
    }
    var path = file.Trim();
    if (Directory.Exists(path))
      path = Path.Combine(path, LakeFiles.ClearFileName);
    LakeFiles.WriteClearing(path, mask);
    output($"bc_lake mask export: wrote the clearing mask ({mask.Size} x {mask.Size}) to {path}, in Tarn's .clear format.");
  }

  // ---- regen ------------------------------------------------------------------------------------------------------------------------------------------

  private static void Regen(bool confirm, Action<string> output)
  {
    if (!MayChange("regen", output))
      return;
    var s = BC.Settings;
    var touched = LakeRegen.Touched(Seams.GeneratedZones(), s.Lakes, s.Clearing);
    output($"bc_lake regen: {touched.Generated:N0} zones are generated; the lakes' boxes touch {touched.ByLakes:N0} and the clearing mask {touched.ByMask:N0} ({touched.Either:N0} in all). "
      + "Zones already generated keep their plants, and a lake's bed is not cleared by the lake itself.");
    if (!confirm)
    {
      output("bc_lake regen: a dry run, nothing changes. Better Continents has no per-region regeneration: with 'confirm' this regenerates EVERY unprotected generated zone, as `bc regen` does (zones within one zone of something a player built or worked on, tombstones, "
        + "players from other machines and you inside a dungeon are kept; players, what players built, tombstones and tamed animals are never removed). Add 'confirm'.");
      return;
    }
    Seams.Regenerate();
    output("bc_lake regen: regenerating the zones (the same run as `bc regen`).");
  }
}

// The generated zones that lakes and the clearing mask touch, for `bc_lake regen`. Pure: the zones are given.
internal static class LakeRegen
{
  // How far a lake's flood may reach past its box [T: LakeBuilder, the builder adds 12 m].
  internal const float Edge = 12f;

  internal readonly record struct Count(int Generated, int ByLakes, int ByMask, int Either);

  // A zone (x, z) is the 64 m square centred on (64 x, 64 z) [G: ZoneSystem.GetZone].
  internal static Count Touched(IReadOnlyCollection<(int X, int Z)> generated, LakeSet? lakes, ClearMask? mask)
  {
    var gen = generated as HashSet<(int X, int Z)> ?? new HashSet<(int X, int Z)>(generated);
    var byLakes = new HashSet<(int X, int Z)>();
    if (lakes != null)
      foreach (var l in lakes.Lakes)
      {
        int x0 = Zone(l.MinX - Edge), x1 = Zone(l.MaxX + Edge), z0 = Zone(l.MinZ - Edge), z1 = Zone(l.MaxZ + Edge);
        if (x1 < x0 || z1 < z0)
          continue;
        long cells = (long)(x1 - x0 + 1) * (z1 - z0 + 1);
        if (cells > gen.Count)
        {
          foreach (var z in gen)
            if (z.X >= x0 && z.X <= x1 && z.Z >= z0 && z.Z <= z1)
              byLakes.Add(z);
        }
        else
          for (int x = x0; x <= x1; x++)
            for (int zz = z0; zz <= z1; zz++)
              if (gen.Contains((x, zz)))
                byLakes.Add((x, zz));
      }
    var byMask = new HashSet<(int X, int Z)>();
    if (mask != null)
      foreach (var z in gen)
        if (mask.Touches(z.X * 64.0 - 32.0, z.Z * 64.0 - 32.0, z.X * 64.0 + 32.0, z.Z * 64.0 + 32.0))
          byMask.Add(z);
    var either = new HashSet<(int X, int Z)>(byLakes);
    either.UnionWith(byMask);
    return new Count(gen.Count, byLakes.Count, byMask.Count, either.Count);
  }

  internal static int Zone(float v) => (int)Math.Floor((v + 32f) / 64f);
}

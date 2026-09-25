// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace BetterContinents;

// "bc export ..." and "bc import ..." - the world export and import in the host's debug tree, next to "bc scr". The same
// options as bc_export and bc_import.
public partial class DebugUtils
{
    private static void AddExportCommands(Command.SubcommandBuilder bc)
    {
        bc.AddGroup("export", "World export", "Dumps the loaded world into Better Continents maps that rebuild it, get more info with 'bc export help'", export =>
        {
            export.AddCommand("run", "Run", $"Starts an export into <save data>/BetterContinents/<world>/export-<time>. Options: {WorldExportCommands.OptionsUsage}",
                args => WorldExportCommands.StartExport(args ?? "", line => Console.instance.Print(line)));
            export.AddCommand("status", "Status", "What the export is doing, and where the last one went",
                _ => WorldExportCommands.PrintStatus(line => Console.instance.Print(line)));
            export.AddCommand("cancel", "Cancel", "Stops the running export and deletes its partial files",
                _ => WorldExportCommands.CancelExport(line => Console.instance.Print(line)));
        });
        bc.AddGroup("import", "World import", "Makes a New World preset (or the config) from an export folder, get more info with 'bc import help'", import =>
        {
            import.AddCommand("run", "Run", $"Imports an export folder: {WorldImportCommands.ArgsUsage}",
                args => WorldImportCommands.Import(args ?? "", line => Console.instance.Print(line)));
            import.AddCommand("list", "List", "The export folders, newest first, numbered for 'bc import run <number>'",
                _ => WorldImportCommands.List(line => Console.instance.Print(line)));
            import.AddCommand("status", "Status", "What the import is doing, and how the last one went",
                _ => WorldImportCommands.PrintStatus(line => Console.instance.Print(line)));
        });
    }
}

// bc_export: the world export for any player, not a cheat. It is gated by WorldExport.Allowed (the server's say), and
// a client's export holds only the locations the server shows on the map. "bc_export server ..." asks a dedicated
// server to export its own world: the server checks its admin list and writes under its own save folder.
public static class WorldExportCommands
{
    public const string OptionsUsage =
        "[size] [amount=2] [sealevel=0.5] [heatscale=10] [edge=auto|on|off] [forest=additive|exact] "
        + "[noheight] [nobiomes] [nolocations] [noforest] [noheat] [noaltbiomes] [nolava] [nomoss] [nopaint] [nosources] [nopreset] [perpoint]";

    public const string Usage = "bc_export [status | cancel | server <options> | <options>] - options: " + OptionsUsage;

    public static void Register()
    {
        // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
        new Terminal.ConsoleCommand("bc_export",
            "[status | cancel | server ... | size and options] - Better Continents: export the loaded world as a map set that rebuilds it (bc_export help)",
            args => Run(args),
            isCheat: false, isNetwork: false, onlyServer: false,
            optionsFetcher: () => ["status", "cancel", "server", "help", "1024", "2048", "4096", "8192"]);
    }

    private static void Run(Terminal.ConsoleEventArgs args)
    {
        void Output(string line)
        {
            args.Context?.AddString(line);
            BetterContinents.Log(line);
        }
        var sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        var rest = args.Length >= 2 ? string.Join(" ", args.Args.Skip(1)).Trim() : "";
        switch (sub)
        {
            case "help":
                Output(Usage);
                Output("Writes heightmap, biome, location, forest, heat, alt-biome, lava, moss and paint maps, export.cfg, README.txt and manifest.json,");
                Output("and a New World preset \"<world> <time>\": pick it in the New World screen to make a world from the export.");
                Output("After editing the PNGs, bc_import makes the preset again (bc_import help).");
                Output($"Defaults, from [09 BetterContinents.Export] in BetterContinents.cfg: {WorldExport.Options.Default()}");
                Output("The same section's Hud turns on the export HUD: a status box (Hud Hotkey, F9) and an export window (Window Hotkey, F7).");
                break;
            case "status":
                PrintStatus(Output);
                break;
            case "cancel":
                CancelExport(Output);
                break;
            case "server":
                var serverArgs = args.Length >= 3 ? string.Join(" ", args.Args.Skip(2)).Trim() : "";
                SendToServer(serverArgs, Output);
                break;
            default:
                StartExport(rest, Output);
                break;
        }
    }

    // The dedicated server exports its own world (every location, its own files). ZNet.RemoteCommand has the server
    // check adminlist.txt and run "bc_export <options>" on its console, so the answer lands in the server's log.
    private static void SendToServer(string options, Action<string> output)
    {
        var net = ZNet.instance;
        if (net == null)
        {
            output("bc_export server: not connected to a server.");
            return;
        }
        var word = options.Trim().ToLowerInvariant();
        bool control = word == "status" || word == "cancel";
        if (net.IsServer())
        {
            // This machine runs the world; ZNet.RemoteCommand would log a null peer here.
            if (word == "status")
                PrintStatus(output);
            else if (word == "cancel")
                CancelExport(output);
            else
                StartExport(options, output);
            return;
        }
        if (!control && !TryParse(options, out _, out var error))
        {
            output($"bc_export: {error}");
            return;
        }
        net.RemoteCommand(("bc_export " + options).Trim());
        output(control
            ? $"Sent '{word}' to the server (admins only); the answer is in the server's log."
            : "Asked the server to export its world (admins only). It writes under its own save folder and reports in its log; 'bc_export server status' and 'bc_export server cancel' go the same way.");
    }

    public static void StartExport(string text, Action<string> output)
    {
        if (!TryParse(text, out var options, out var error))
        {
            output($"bc_export: {error}");
            output(Usage);
            return;
        }
        if (!WorldExport.CanExport(out var reason))
        {
            output($"World export cannot start: {reason}.");
            return;
        }
        if (WorldExport.Start(options))
            output("World export running; 'bc_export status' shows progress, 'bc_export cancel' stops it.");
        else
            output($"World export cannot start: {WorldExport.LastError}");
    }

    public static void PrintStatus(Action<string> output)
    {
        foreach (var line in WorldExport.StatusLines())
            output(line);
    }

    public static void CancelExport(Action<string> output)
    {
        if (!WorldExport.IsRunning)
        {
            output("No world export is running.");
            return;
        }
        WorldExport.Cancel();
        output("World export: cancelling; its partial files will be deleted.");
    }

    // Options: a number is the size; key=value pairs and switches as in OptionsUsage, case-insensitive, invariant culture.
    public static bool TryParse(string text, out WorldExport.Options options, out string error)
    {
        options = WorldExport.Options.Default();
        error = "";
        foreach (var raw in text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                options.Size = size;
                continue;
            }
            var eq = token.IndexOf('=');
            if (eq > 0)
            {
                var key = token.Substring(0, eq);
                var value = token.Substring(eq + 1);
                bool ok = true;
                switch (key)
                {
                    case "size":
                        ok = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out options.Size);
                        break;
                    case "amount":
                        ok = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out options.HeightmapAmount);
                        break;
                    case "sealevel":
                        ok = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out options.SeaLevel);
                        break;
                    case "heatscale":
                        ok = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out options.HeatScale);
                        break;
                    case "edge":
                        if (value == "auto") options.EdgeDropoff = null;
                        else if (value == "on") options.EdgeDropoff = true;
                        else if (value == "off") options.EdgeDropoff = false;
                        else ok = false;
                        break;
                    case "forest":
                        if (value == "additive") options.ForestExact = false;
                        else if (value == "exact") options.ForestExact = true;
                        else ok = false;
                        break;
                    default:
                        error = $"unknown option '{raw}'";
                        return false;
                }
                if (!ok)
                {
                    error = $"cannot read '{raw}'";
                    return false;
                }
                continue;
            }
            switch (token)
            {
                case "noheight": case "noheights": case "noheightmap": options.Heightmap = false; break;
                case "nobiomes": case "nobiome": options.Biomes = false; break;
                case "nolocations": case "nolocation": options.Locations = false; break;
                case "noforest": options.Forest = false; break;
                case "noheat": options.Heat = false; break;
                case "noaltbiomes": case "noaltbiome": options.AltBiomes = false; break;
                case "nolava": options.Lava = false; break;
                case "nomoss": options.Moss = false; break;
                case "nosources": options.Sources = false; break;
                case "paint": options.Paint = true; break;
                case "nopaint": options.Paint = false; break;
                case "preset": options.Preset = true; break;
                case "nopreset": options.Preset = false; break;
                case "perpoint": options.ZoneBlend = false; break;
                default:
                    error = $"unknown option '{raw}'";
                    return false;
            }
        }
        return true;
    }
}

// Registers "bc_cache ..." once the terminal exists. bc_export/bc_import are registered from DebugUtils's own
// InitTerminal postfix (DebugUtils.cs); this is a second, independent postfix on the same vanilla method so this
// file does not need to touch that one - Harmony runs every postfix registered for a target, in any file.
[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
internal static class WorldCacheCommandsInit
{
    private static void Postfix() => WorldCacheCommands.Register();
}

// bc_cache: a shareable form of the world cache, for pre-seeding clients of extra-large worlds (WorldCacheShare.cs;
// documented in the README and chapter 9 of the Export & Import guide). Any player, not a cheat: export and list are read-only or write to the player's own
// folders, import only ever adds to the local cache and never overwrites an existing entry.
public static class WorldCacheCommands
{
    public const string Usage = "bc_cache [export | import <file or folder> | list | help]";

    public static void Register()
    {
        // Named args: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
        new Terminal.ConsoleCommand("bc_cache",
            "[export | import <file or folder> | list | help] - Better Continents: a shareable world cache, for pre-seeding clients of big worlds (bc_cache help)",
            args => Run(args),
            isCheat: false, isNetwork: false, onlyServer: false,
            optionsFetcher: () => ["export", "import", "list", "help"]);
    }

    private static void Run(Terminal.ConsoleEventArgs args)
    {
        void Output(string line)
        {
            try
            {
                args.Context?.AddString(line);
            }
            catch
            {
                // The console that asked is gone.
            }
            BetterContinents.Log(line);
        }
        // Everything after the command, so an import path with spaces needs no quotes.
        var line = args.FullLine ?? "";
        var rest = line.Length > "bc_cache".Length && line.StartsWith("bc_cache", StringComparison.OrdinalIgnoreCase)
            ? line.Substring("bc_cache".Length).Trim()
            : string.Join(" ", args.Args.Skip(1)).Trim();
        var split = rest.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        var word = split.Length > 0 ? split[0].ToLowerInvariant() : "";
        var tail = split.Length > 1 ? split[1].Trim() : "";
        switch (word)
        {
            case "":
            case "help":
                Help(Output);
                break;
            case "export":
                Export(Output);
                break;
            case "import":
                Import(tail, Output);
                break;
            case "list":
                List(Output);
                break;
            default:
                Output($"bc_cache: unknown option '{word}'");
                Output(Usage);
                break;
        }
    }

    public static void Help(Action<string> output)
    {
        output(Usage);
        output("  bc_cache export           on the host/dedicated server: writes this world's settings as a shareable");
        output("                            .bcworld (+ .txt) file. On a client: exports its own cached copy of the");
        output("                            world it is currently in, if it has one.");
        output("  bc_cache import <path>    imports one .bcworld file, or every .bcworld found under a folder, into");
        output("                            your local cache. Ids already present are skipped, never overwritten.");
        output("  bc_cache list             the cache entries on this machine: id, size, date.");
        output($"A .bcworld dropped into {SafeSeedFolder()} is imported automatically next time Better Continents starts -");
        output("what a modpack such as \"<World>-WorldCache\" uses to pre-seed every player with zero action from them.");
    }

    public static void Export(Action<string> output)
    {
        var result = WorldCacheShare.Export(out var error);
        if (result == null)
        {
            output($"bc_cache export: {error}.");
            return;
        }
        output($"bc_cache export: wrote {result.FilePath} ({result.Size} bytes, id {result.Id}).");
        output($"A description for whoever you give it to is in {result.SidecarPath}.");
    }

    public static void Import(string text, Action<string> output)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            output("bc_cache import: needs a file or folder, e.g. 'bc_cache import MyWorld-<id>.bcworld'.");
            return;
        }
        var outcomes = WorldCacheShare.ImportPath(text, out var error);
        if (error != null)
        {
            output($"bc_cache import: {error}.");
            return;
        }
        if (outcomes.Count == 0)
        {
            output($"bc_cache import: no {WorldCacheShare.Extension} files found at {text}.");
            return;
        }
        foreach (var outcome in outcomes)
            output("  " + outcome.Describe());
        int imported = outcomes.Count(o => o.Imported);
        int present = outcomes.Count(o => o.AlreadyPresent);
        int refused = outcomes.Count(o => o.Error != null);
        output($"bc_cache import: {imported} imported, {present} already present, {refused} refused.");
    }

    public static void List(Action<string> output)
    {
        var entries = WorldCacheShare.ListEntries();
        if (entries.Count == 0)
        {
            output("bc_cache list: the cache is empty.");
            return;
        }
        output($"World cache ({entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}):");
        foreach (var entry in entries)
            output($"  {entry.Id}  {FormatSize(entry.Size)}  {entry.Modified:yyyy-MM-dd HH:mm}");
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024f * 1024f):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private static string SafeSeedFolder()
    {
        try
        {
            return WorldCacheShare.SeedFolder;
        }
        catch
        {
            return "<BepInEx>/config/BetterContinents/seed";
        }
    }
}

// bc_import: the world import for any player, in the main menu or in a world. It only reads local folders and writes the
// player's own presets and config, so no server has a say. "bc_import" alone takes the newest export of the loaded (or
// last) world; "list" numbers the export folders; a trailing "config" applies the folder to BetterContinents.cfg instead
// of making a preset.
public static class WorldImportCommands
{
    public const string ArgsUsage = "[<number> | <folder> | <world>] [config]";

    public const string Usage = "bc_import [list | status | help | <number> | <folder> | <world>] [config]";

    public static void Register()
    {
        // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
        new Terminal.ConsoleCommand("bc_import",
            "[list | <number> | <folder> | <world>] [config] - Better Continents: make a New World preset (or the config) from an export folder (bc_import help)",
            args => Run(args),
            isCheat: false, isNetwork: false, onlyServer: false,
            optionsFetcher: () => ["list", "status", "help", "config"]);
    }

    private static void Run(Terminal.ConsoleEventArgs args)
    {
        void Output(string line)
        {
            try
            {
                args.Context?.AddString(line);
            }
            catch
            {
                // The console that asked is gone.
            }
            BetterContinents.Log(line);
        }
        // Everything after the command, so a folder with spaces in its name needs no quotes.
        var line = args.FullLine ?? "";
        var rest = line.Length > "bc_import".Length && line.StartsWith("bc_import", StringComparison.OrdinalIgnoreCase)
            ? line.Substring("bc_import".Length).Trim()
            : string.Join(" ", args.Args.Skip(1)).Trim();
        switch (rest.ToLowerInvariant())
        {
            case "help":
                Help(Output);
                break;
            case "list":
                List(Output);
                break;
            case "status":
                PrintStatus(Output);
                break;
            default:
                Import(rest, Output);
                break;
        }
    }

    public static void Help(Action<string> output)
    {
        output(Usage);
        output("  bc_import               makes a New World preset from your newest export (of the loaded world, else the last one)");
        output("                          and selects it: choose Start, New World, Create.");
        output("  bc_import list          the export folders, newest first, numbered.");
        output("  bc_import 2             the second folder of the list.");
        output("  bc_import <world>       the newest export of that world.");
        output("  bc_import <folder>      any folder of Better Continents maps: a full path, or one under BetterContinents/.");
        output("  ... config              instead of a preset: puts export.cfg into BetterContinents.cfg (the old file is kept");
        output("                          beside it) and selects \"From Config\", so every new world reads the PNGs as they are then.");
        output("  bc_import status        what the import is doing, and how the last one went.");
        output($"Exports are in {SafeRoot()}/<world>/export-<time>/; presets in {SafePresets()}.");
    }

    public static void List(Action<string> output)
    {
        List<ExportFolder> list;
        try
        {
            list = WorldImport.ListExports();
        }
        catch (Exception e)
        {
            output($"bc_import: cannot list the exports: {e.Message}");
            return;
        }
        if (list.Count == 0)
        {
            output($"bc_import: no exports in {SafeRoot()} yet. Export a world first (bc_export, or F7 in a world with Hud on).");
            return;
        }
        output($"Export folders in {SafeRoot()}, newest first:");
        for (int i = 0; i < list.Count; i++)
            output($"  {i + 1,2}. {list[i].Label}   {Path.GetFileName(Path.GetDirectoryName(list[i].Path))}/{Path.GetFileName(list[i].Path)}");
        output("'bc_import <number>' makes that one a New World preset; add 'config' to load it from the config instead.");
    }

    public static void PrintStatus(Action<string> output)
    {
        foreach (var line in WorldImport.StatusLines())
            output(line);
    }

    // "<target> [config]": the folder by bc_import's rules, then a preset or the config.
    public static void Import(string text, Action<string> output)
    {
        text = (text ?? "").Trim();
        var mode = WorldImport.Mode.Preset;
        if (text.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            mode = WorldImport.Mode.Config;
            text = "";
        }
        else if (text.EndsWith(" config", StringComparison.OrdinalIgnoreCase))
        {
            mode = WorldImport.Mode.Config;
            text = text.Substring(0, text.Length - " config".Length).Trim();
        }
        var folder = WorldImport.Resolve(text, out var how, out var error);
        if (folder == null)
        {
            output($"bc_import: {error}.");
            return;
        }
        output($"bc_import: {folder.Path} ({how}).");
        WorldImport.Start(folder.Path, mode, output);
    }

    private static string SafeRoot()
    {
        try
        {
            return WorldImport.RootDir;
        }
        catch
        {
            return "<save data>/BetterContinents";
        }
    }

    private static string SafePresets()
    {
        try
        {
            return Presets.PresetsDir;
        }
        catch
        {
            return "<save data>/BetterContinents/presets";
        }
    }
}

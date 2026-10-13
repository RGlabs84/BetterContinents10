// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;

namespace BetterContinents;

// "bc water": bc_water for any player, and in the host's debug tree next to "bc bake" (the same words).
public partial class DebugUtils
{
    private static void AddWaterCommands(Command.SubcommandBuilder bc)
    {
        bc.AddCommand("water", "Water", $"Whether Better Continents' own sea is on and why, its field, and what draws the water: as {WaterCommands.Usage}",
            args => WaterCommands.RunLine(args ?? "", line => Console.instance.Print(line), debug: true));
    }
}

// bc_water: what BC's own sea is doing in the world that is loaded. Read-only for every player; 'force' (Debug Mode, on the machine that runs
// the world) switches BC's sea on in a world where it would be off, or off in one where it would be on, for looking at the two seas side by side.
internal static class WaterCommands
{
    public const string Usage = "bc_water [force on|off|auto]";

    public static void Register()
    {
        // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
        new Terminal.ConsoleCommand("bc_water",
            "[force on|off|auto] - Better Continents: whether its own sea is on in this world and why, the sea field, and what draws the water",
            args => RunLine(ConsoleTools.Rest(args, "bc_water"), ConsoleTools.Output(args.Context), debug: false),
            isCheat: false, isNetwork: false, onlyServer: false,
            optionsFetcher: () => ["force"]);
    }

    // bc_water, and "bc water" in the host's debug tree (debug: the tree itself is the Debug Mode gate).
    internal static void RunLine(string text, Action<string> output, bool debug)
    {
        var words = text.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            SeaPatches.Report(output);
            return;
        }
        if (!string.Equals(words[0], "force", StringComparison.OrdinalIgnoreCase))
        {
            output("bc_water: " + Usage);
            return;
        }
        if (!debug && !BetterContinents.AllowDebugActions)
        {
            output("bc_water: force needs Debug Mode, on the machine that runs the world.");
            return;
        }
        var how = words.Length > 1 ? words[1].ToLowerInvariant() : "";
        bool? forced;
        switch (how)
        {
            case "on": forced = true; break;
            case "off": forced = false; break;
            case "auto": forced = null; break;
            default:
                output("bc_water: " + Usage);
                return;
        }
        SeaPatches.Forced = forced;
        output($"Water: BC's sea is now {(forced == null ? "decided by the world as usual" : forced.Value ? "forced on" : "forced off")} (the patches and the field follow).");
        BetterContinents.DynamicPatch();
    }
}

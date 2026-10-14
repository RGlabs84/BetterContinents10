// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;

namespace BetterContinents;

// "bc water": bc_water for any player, and in the host's debug tree next to "bc bake" (the same words).
public partial class DebugUtils
{
    private static void AddWaterCommands(Command.SubcommandBuilder bc)
    {
        bc.AddCommand("water", "Water", $"Whether Better Continents' own sea is on and why, its field, and what draws the water; force it on, probe drawn against felt: as {WaterCommands.Usage}",
            args => WaterCommands.RunLine(args ?? "", line => Console.instance.Print(line), debug: true, alsoLog: true));
    }
}

// bc_water: what BC's own sea is doing in the world that is loaded. Read-only for every player; two words need Debug Mode:
//   force on|off   BC's sea on in a world where it would be off (a vanilla-size world without maps, or one whose sea is the game's own): the world counts
//                  as a candidate, its field is built from its rules and BC's shader is drawn all the same, so that it can be compared with the game's own at
//                  the same places (the parity check of the plan's 10.3). The felt waves stay as they are. Not saved, not a setting; off gives back the
//                  rules, and leaving the world resets it.
//   probe          the drawn height of the zone water against the felt one at 81 vertices in front of the camera (SeaProbe): one line, on the console and
//                  in the log.
internal static class WaterCommands
{
    public const string Usage = "bc_water [force on|off | probe]";

    public static void Register()
    {
        // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
        new Terminal.ConsoleCommand("bc_water",
            "[force on|off | probe] - Better Continents: whether its own sea is on in this world and why, the sea field, and what draws the water; with Debug Mode: force it on, probe the drawn waves against the felt",
            args => RunLine(ConsoleTools.Rest(args, "bc_water"), ConsoleTools.Output(args.Context), debug: false, alsoLog: false),
            isCheat: false, isNetwork: false, onlyServer: false,
            optionsFetcher: () => ["force", "probe"]);
    }

    // What the offline tests stand in for: the repatch a force makes, and the probe.
    internal static Action Repatch = () => BetterContinents.DynamicPatch();
    internal static Action<Action<string>, bool> Probe = SeaProbe.Run;

    // Debug Mode: the host's debug tree itself, or the setting for the console command.
    internal static bool DebugModeOn(bool debug) => debug || BetterContinents.ConfigDebugModeEnabled?.Value == true;

    // bc_water, and "bc water" in the host's debug tree (debug: the tree is the Debug Mode gate). alsoLog: the answer of the probe goes to the BepInEx
    // log as well (bc_water's output goes there already).
    internal static void RunLine(string text, Action<string> output, bool debug, bool alsoLog = false)
    {
        var words = text.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            SeaDraw.Report(output);
            return;
        }
        var word = words[0].ToLowerInvariant();
        if (word != "force" && word != "probe")
        {
            output("bc_water: " + Usage);
            return;
        }
        if (!DebugModeOn(debug))
        {
            output($"bc_water: {word} needs Debug Mode (Debug Mode = true in BetterContinents.cfg).");
            return;
        }
        if (word == "probe")
        {
            if (words.Length > 1)
            {
                output("bc_water: " + Usage);
                return;
            }
            Probe(output, alsoLog);
            return;
        }
        var how = words.Length == 2 ? words[1].ToLowerInvariant() : "";
        if (how != "on" && how != "off")
        {
            output("bc_water: " + Usage);
            return;
        }
        bool on = how == "on";
        bool was = SeaPatches.ForcedOn;
        SeaPatches.ForcedOn = on;
        output(on
            ? "Water: force is ON: BC's sea is a candidate here whatever the world's rules say, its field is built from the world's rules and BC's shader is drawn even where the sea is the game's own; "
              + "the felt waves stay as they are. 'bc water' says so; 'bc water force off' gives the rules back."
            : "Water: force is off: the world's own rules decide again.");
        if (on != was)
            Repatch();
    }
}

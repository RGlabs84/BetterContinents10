// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

namespace BetterContinents;

// "bc bake ...": bc_bake in the host's debug tree, next to "bc twins" (the same words as bc_bake, BakeCommands).
public partial class DebugUtils
{
    private static void AddBakeCommands(Command.SubcommandBuilder bc)
    {
        bc.AddCommand("bake", "Baked placements", $"Bake pieces into the world's layer, take them out again, undo: as {BakeCommandLine.Usage}",
            args => BakeCommands.RunLine(args ?? "", line => Console.instance.Print(line)));
    }
}

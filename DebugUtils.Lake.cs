// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).

namespace BetterContinents;

// "bc lake ...": bc_lake in the host's debug tree, next to "bc bake" and "bc water" (the same words as bc_lake, LakeCommands).
public partial class DebugUtils
{
    private static void AddLakeCommands(Command.SubcommandBuilder bc)
    {
        bc.AddCommand("lake", "Lakes", $"The world's lakes above the sea and its mask of ground kept clear of plants, from Raven Iron's Tarn: as {LakeCommandLine.Usage}",
            args => LakeCommands.RunLine(args ?? "", line => Console.instance.Print(line)));
    }
}

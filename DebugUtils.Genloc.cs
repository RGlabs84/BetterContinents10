// Added by Wubarrk on 2026-10-13 for the forced genloc (0.10.5), and modified on 2026-10-26 for the pins word (0.10.7).

namespace BetterContinents;

// "bc genloc": makes the world's locations again where the game's own genloc does nothing (Genloc.cs). In the host's "bc" tree, which runs where the world
// is (the root is a cheat for the machine that runs it).
public partial class DebugUtils
{
    private static void AddGenlocCommands(Command.SubcommandBuilder bc)
    {
        bc.AddCommand("genloc", "Genloc",
            "Makes the world's locations again: every location that is not placed yet is cleared and rolled again in the zones that are not generated yet; placed ones and generated zones stay. "
            + "Does what genloc does also when the game's own run is stuck or the world was loaded with its locations marked as made, says what it found and what changed, and sends the new icons to the clients. "
            + "'bc genloc alt' passes to the game's alt-biome branch exactly as 'genloc alt' does. 'bc genloc pins' only puts the location map's pins and the start position into the registry and generates nothing. 'bc genloc help' says more",
            args => Genloc.RunLine(args ?? "", Genloc.Say));
    }
}

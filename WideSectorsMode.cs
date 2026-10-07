// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

namespace BetterContinents;

/// <summary>"Wide Sectors" (07 BetterContinents.Misc): whether a world that reaches past the game's own sectors (its World Size + Edge
/// Size is past 16,350 m) is given sectors and save chunks of its own for every zone out to 65.5 km (WorldSectors), or keeps the game's,
/// where everything beyond 16.4 km shares one. It is read by the machine that runs the world (single player, the host, a dedicated
/// server), when a world is made and when it loads; a joining player's game follows what the server's world says. A world records only
/// whether it is wide (BetterContinentsSettings.WideSectors), and a wide world stays wide whatever this says.</summary>
public enum WideSectorsMode
{
  /// <summary>The default: a new world that reaches past the game's sectors is made wide; an existing world keeps the sectors it has.</summary>
  Auto,
  /// <summary>As Auto, and an existing world that reaches past the game's sectors and still has them is converted when it loads (one way:
  /// the game saves a copy of the old save first).</summary>
  On,
  /// <summary>A new world gets the game's sectors whatever its size; a world that is wide already stays wide.</summary>
  Off,
}

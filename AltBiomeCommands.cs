// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.Linq;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// bc_altbiomes: check planted and random alt biomes in game. A separate command rather than a "bc" subcommand,
// because "bc" is a server-side cheat and comparing what a CLIENT computed with the server is half the point.
public static class AltBiomeCommands
{
  public const string Usage = "bc_altbiomes [here | names [filter] | hash | list [filter]]";

  public static void Register()
  {
    new Terminal.ConsoleCommand("bc_altbiomes",
      "[here | names [filter] | hash | list [filter]] - Better Continents alt biomes: summary, the region you stand in, alt-biome names and colours, agreement hashes, every alt-biome region (list needs devcommands on a client)",
      args => Run(args),
      isCheat: false, isNetwork: false, onlyServer: false,
      optionsFetcher: () => ["here", "names", "hash", "list"]);
  }

  private static void Run(Terminal.ConsoleEventArgs args)
  {
    void Output(string line) => args.Context?.AddString(line);
    void OutputAll(IEnumerable<string> lines)
    {
      foreach (var line in lines)
        Output(line);
    }
    var sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
    var rest = args.Length >= 3 ? string.Join(" ", args.Args.Skip(2)).Trim() : "";

    if (sub == "names")
    {
      OutputAll(AltBiomeReport.Names(rest));
      return;
    }

    var data = ZNet.World?.m_biomeData;
    if (data == null || !data.IsReady)
    {
      Output("No alt biome data yet: load a world first.");
      return;
    }
    if (!AltBiomeControl.WorldEnabled)
      Output("This world does not use Better Continents; showing the game's own alt biomes.");

    switch (sub)
    {
      case "":
        OutputAll(AltBiomeReport.ClientSummary());
        if (Player.m_localPlayer != null)
          OutputAll(AltBiomeReport.Here(Player.m_localPlayer.transform.position).Take(1));
        Output("More: " + Usage);
        break;
      case "here":
        if (Player.m_localPlayer != null)
          OutputAll(AltBiomeReport.Here(Player.m_localPlayer.transform.position));
        else
          Output("No local player.");
        break;
      case "hash":
        OutputAll(AltBiomeReport.ClientSummary());
        break;
      case "list":
        // The full list says where every alt biome is. Vanilla's own "altbioms" is a cheat for the same reason.
        if (ZNet.instance != null && !ZNet.instance.IsServer() && !Terminal.m_cheat)
        {
          Output("bc_altbiomes list reveals where every alt biome is; it needs devcommands on a client.");
          return;
        }
        OutputAll(AltBiomeReport.ListSectors(rest, 200));
        break;
      default:
        Output($"Unknown option '{sub}'. Use: {Usage}");
        break;
    }
  }
}

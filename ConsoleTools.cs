// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Linq;
using HarmonyLib;

namespace BetterContinents;

// What Better Continents' console tools share: bc_export, bc_cache, bc_import, bc_altbiomes and bc_twins. They are
// commands of their own, not "bc" subcommands, because "bc" is a cheat for whoever runs the world and these serve every
// player, each with its own rule on who may do what. The host's "bc" tree has them too (bc export, bc import, bc ab,
// bc cache, bc twins).
//
// A tool answers on the console that asked and in the log. When a dedicated server runs it for an admin ("bc_export
// server ...", "bc_twins server ..."), the admin's console gets the answer too.
internal static class ConsoleTools
{
  // The admin a server runs a remote console command for. ZNet.InternalCommand runs the command for them, so they are
  // known only while it runs (RemoteCallerPatch); a tool takes them when it starts (Output, RemoteEcho).
  internal static ZRpc? RemoteCaller;

  // The lines of one run of a tool: to the console that asked, while it is there; to the log, unless log is false; and
  // to the admin it runs for.
  internal static Action<string> Output(Terminal? console, bool log = true)
  {
    var caller = RemoteCaller;
    return line =>
    {
      try
      {
        console?.AddString(line);
      }
      catch
      {
        // The console that asked is gone.
      }
      if (log)
        BetterContinents.Log(line);
      Send(caller, line);
    };
  }

  // The admin's console alone, for what a tool says later (a server export when it finishes); null when no admin asked.
  internal static Action<string>? RemoteEcho()
  {
    var caller = RemoteCaller;
    return caller == null ? null : line => Send(caller, line);
  }

  private static void Send(ZRpc? caller, string line)
  {
    if (caller == null)
      return;
    try
    {
      ZNet.instance?.RemotePrint(caller, line);
    }
    catch
    {
      // The admin left.
    }
  }

  // Everything after the command's name, so a path with spaces needs no quotes.
  internal static string Rest(Terminal.ConsoleEventArgs args, string name)
  {
    var line = args.FullLine ?? "";
    return line.Length > name.Length && line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
      ? line.Substring(name.Length).Trim()
      : string.Join(" ", args.Args.Skip(1)).Trim();
  }
}

// Remembers who sent a remote console command while the server runs it (ConsoleTools.RemoteCaller).
[HarmonyPatch(typeof(ZNet), "InternalCommand")]
internal static class RemoteCallerPatch
{
  private static void Prefix(ZRpc rpc) => ConsoleTools.RemoteCaller = rpc;
  private static void Finalizer() => ConsoleTools.RemoteCaller = null;
}

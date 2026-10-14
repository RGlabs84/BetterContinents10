// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// The standalone Tarn mod (Raven Iron's, com.raveniron.tarn) does what Better Continents' lakes and clearing now do. When it is installed Better Continents
// steps aside (BUILD-SPEC-TARN.md 5.3): its lake and clearing toggles stay off, no tile, box, map paint, sound or clearing is made by it, its data is still
// read, kept in the settings and saved (so removing Tarn later turns Better Continents' own on with nothing lost), and it never refuses to load.
// The contract: Installed is set once, in Awake.
public static class TarnCompat
{
  public const string GUID = "com.raveniron.tarn";
  internal static bool Installed;

  // What a player is told, on the screen and by the commands that would change the world's lakes or its mask.
  internal const string Message = "Better Continents' lakes are off while the Tarn mod is installed. Remove Tarn to use Better Continents' own.";

  public static void Run()
  {
    Installed = Chainloader.PluginInfos.ContainsKey(GUID);
    if (Installed)
      BetterContinents.Log("The Tarn mod is installed: Better Continents' lakes and clearing are off while it is (the data is kept).");
  }

  // Why a command that changes the world's lakes or mask is refused, or null.
  internal static string? Refusal => Installed ? Message : null;

  // The screen: the standard top-left message (the stand-in of the offline tests).
  internal static Func<string, bool> Show = text =>
  {
    var hud = MessageHud.instance;
    if (hud == null)
      return false;
    hud.ShowMessage(MessageHud.MessageType.TopLeft, text);
    return true;
  };

  private static bool told;

  // Once per session, when a player is in a world that carries lakes or a mask (Better Continents' own, which Tarn's presence switched off): the message
  // on the screen. A world with neither is none of Tarn's business or ours, so it hears nothing. True when the message was shown just now.
  internal static bool TellOnce(BetterContinents.BetterContinentsSettings settings)
  {
    if (!Installed || told || !settings.EnabledForThisWorld || !(settings.HasLakes || settings.HasClearing))
      return false;
    if (!Show(Message))
      return false;
    told = true;
    BetterContinents.Log(Message);
    return true;
  }

  // For the offline tests: a new session.
  internal static void ForgetTold() => told = false;
}

// A player has spawned in a world: the message, if it is due.
[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
internal static class TarnCompatSpawn
{
  private static void Postfix()
  {
    try
    {
      TarnCompat.TellOnce(BetterContinents.Settings);
    }
    catch (Exception e)
    {
      BetterContinents.LogError("Tarn compatibility message failed: " + e.Message);
    }
  }
}

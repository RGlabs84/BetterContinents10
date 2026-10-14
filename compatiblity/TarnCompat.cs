// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using BepInEx.Bootstrap;

namespace BetterContinents;

// The standalone Tarn mod (Raven Iron's, com.raveniron.tarn) does what Better Continents' lakes and clearing now do. When it is installed Better Continents
// steps aside (BUILD-SPEC-TARN.md 5.3): its lake and clearing toggles stay off, its data is kept and saved. The contract: Installed is set once, in Awake.
public static class TarnCompat
{
  public const string GUID = "com.raveniron.tarn";
  internal static bool Installed;

  public static void Run()
  {
    Installed = Chainloader.PluginInfos.ContainsKey(GUID);
    if (Installed)
      BetterContinents.Log("The Tarn mod is installed: Better Continents' lakes and clearing are off while it is (the data is kept).");
  }
}

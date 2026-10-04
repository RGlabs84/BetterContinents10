// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using BepInEx.Bootstrap;

namespace BetterContinents;

// Expand World Size: when it is installed it sizes the world and lays it out itself (its radius, its alt-biome grid,
// minimap and locations), and tells Better Continents its size (BetterContinents.SetSize) and stretch
// (WorldSizeHelper.SetStretch). Better Continents then leaves the layout to it (BetterContinents.LayoutGeometry).
public static class EWS
{
  public const string GUID = "expand_world_size";
  internal static bool Installed;

  public static void Run()
  {
    Installed = Chainloader.PluginInfos.ContainsKey(GUID);
    if (Installed)
      BetterContinents.Log("\"Expand World Size\" detected: it sizes and lays out the world.");
  }
}

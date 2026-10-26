// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-26 to see Expand World Size's save grid (0.10.7).

using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace BetterContinents;

// Expand World Size: when it is installed it sizes the world and lays it out itself (its radius, its alt-biome grid,
// minimap and locations), and tells Better Continents its size (BetterContinents.SetSize) and stretch
// (WorldSizeHelper.SetStretch). Better Continents then leaves the layout to it (BetterContinents.LayoutGeometry).
//
// Since 1.44 it also widens the game's save grid (ExpandWorldSize.ZoneGrid: 512 zones a side, up to 2048) when its world
// is bigger than the game's 16,384 m. That is the job of Better Continents' Wide Sectors, with a different chunk layout in the
// save, so the two never run together: GridActive says whether Expand World Size's grid is the one in use.
public static class EWS
{
  public const string GUID = "expand_world_size";
  internal static bool Installed;
  // The game's own grid: 512 zones a side.
  internal const int VanillaGridWidth = 512;
  private static Func<int>? gridWidth;
  private static Func<int>? configuredWidth;
  // The offline suites stand in for the installed mod with a width.
  internal static int? GridWidthOverride;

  public static void Run()
  {
    Installed = Chainloader.PluginInfos.ContainsKey(GUID);
    if (!Installed)
      return;
    BetterContinents.Log("\"Expand World Size\" detected: it sizes and lays out the world.");
    try
    {
      BindGrid(Chainloader.PluginInfos[GUID].Instance.GetType().Assembly);
    }
    catch (Exception ex)
    {
      BetterContinents.LogWarning($"EWS compatibility: could not look at the save grid ({ex.Message}).");
    }
  }

  // Internal for the offline tests, which load Expand World Size's assembly without BepInEx's chainloader.
  internal static void BindGrid(Assembly assembly)
  {
    gridWidth = configuredWidth = null;
    var grid = assembly.GetType("ExpandWorldSize.ZoneGrid");
    var width = grid?.GetField("Width", BindingFlags.Public | BindingFlags.Static);
    var configured = grid?.GetMethod("GetWidth", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
    if (width == null || width.FieldType != typeof(int) || configured == null || configured.ReturnType != typeof(int))
    {
      BetterContinents.Log("EWS compatibility: this Expand World Size has no save grid of its own (before 1.44), so Wide Sectors stays Better Continents'.");
      return;
    }
    gridWidth = () => (int)width.GetValue(null)!;
    configuredWidth = () => (int)configured.Invoke(null, null)!;
    BetterContinents.Log("EWS compatibility: its save grid (ZoneGrid) found: when it is wider than the game's, it files the world's objects and Wide Sectors stays out of the way.");
  }

  // The zones a side of the grid Expand World Size files this world's objects in, or 0 when it is not installed (or has no grid of its own).
  // Its width now, or the width its settings give (it patches the game when a world starts, not before).
  internal static int GridWidth
  {
    get
    {
      if (GridWidthOverride is { } fixedWidth)
        return fixedWidth;
      if (!Installed || gridWidth == null || configuredWidth == null)
        return 0;
      try
      {
        return Math.Max(gridWidth(), configuredWidth());
      }
      catch (Exception)
      {
        gridWidth = configuredWidth = null;
        return 0;
      }
    }
  }

  // Whether Expand World Size's save grid is wider than the game's: it, not Better Continents' Wide Sectors, files this world's objects.
  internal static bool GridActive => GridWidth > VanillaGridWidth;
}

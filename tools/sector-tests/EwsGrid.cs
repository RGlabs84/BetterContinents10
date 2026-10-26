// Added by Wubarrk on 2026-10-26 for Expand World Size's save grid (0.10.7).
//
// Expand World Size 1.44 and later widen the game's save grid to 65 km themselves (ExpandWorldSize.ZoneGrid), with a chunk layout of their own.
// Better Continents' wide sectors patch the same ten methods with another one, so while Expand World Size's grid is wider than the game's the
// wide sectors stay out: a new world is not made wide, Wanted is false, and a save that holds wide chunks is refused (it cannot be read with both).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private const string EwsDll = "/home/rohan/WubarrkCODING/libs-Tools/1.0/thirdparty/expand-world-size-2026-10-26/ExpandWorldSize-1.45.0/ExpandWorldSize.dll";

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void EwsGridTests(Harmony harmony)
  {
    Section("Expand World Size's save grid: the wide sectors stay out of its way");
    var owner = "sector-tests.session";
    var none = (WorldGeometry)null;
    var hadHarmony = GetStatic<Harmony>(typeof(BC), "HarmonyInstance");
    try
    {
      WorldSectors.ModeOverride = WideSectorsMode.Auto;
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);

      // Not installed (or before 1.44): nothing changes.
      EWS.GridWidthOverride = null;
      Check(EWS.GridWidth == 0 && !EWS.GridActive, "Expand World Size not installed: it has no grid");
      EWS.GridWidthOverride = 512;
      Check(!EWS.GridActive && WorldSectors.Wanted(Wide(24000f, 500f), none, WideSectorsMode.Auto, hosted: true), "its grid at the game's 512 zones: the wide sectors are wanted as before");

      // Its grid wider than the game's: the wide sectors are not wanted, in any mode, from any size, and a new world is not made wide.
      EWS.GridWidthOverride = 2048;
      Check(EWS.GridActive, "its grid at 2048 zones is active");
      int wanted = 0, made = 0;
      foreach (var mode in new[] { WideSectorsMode.Auto, WideSectorsMode.On, WideSectorsMode.Off })
        foreach (var metres in new[] { 16000f, 24000f, 64500f })
        {
          if (WorldSectors.Wanted(Wide(metres, 500f), new WorldGeometry(metres, 500f), mode, hosted: true)) wanted++;
          var settings = World(true, metres, 500f);
          if (WorldSectors.NewWorld(settings, mode, new WorldGeometry(metres, 500f)) || settings.WideSectors) made++;
        }
      Check(wanted == 0, $"its grid in use: no wide world wants the wide sectors, in any mode or size ({wanted} did)");
      Check(made == 0, $"its grid in use: no new world is made wide ({made} were)");
      int at = Lines;
      WorldSectors.NewWorld(World(true, 24000f, 500f), WideSectorsMode.Auto, new WorldGeometry(24000f, 500f));
      Check(Logged(at, "Expand World Size's save grid (2048 zones a side) files its objects"), "and the log says why");

      // The session: the sectors do not come on, and a save with wide chunks is refused with a message naming both.
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      FakeWorld("TestWorld");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, Wide(24000f, 500f), none);
      Check(!WorldSectors.Active && NoHookPatched(owner), "a wide world under its grid: the sectors stay off, no method patched");
      at = Lines;
      WorldSectors.MappingLoaded(harmony, WideChunks);
      Check(!WorldSectors.Active && NoHookPatched(owner) && !WorldSectors.ForcedBySave && ZNet.m_loadError, "a save with wide chunks under its grid: not loaded (the game's load error is set), no method patched");
      Check(BC.LastConnectionError is { } shown && shown.Contains("was saved with wide sectors") && shown.Contains("Expand World Size's save grid (2048 zones a side)") && shown.Contains("'TestWorld'") && shown.Contains("not loaded"),
        "the error the player is shown names the wide sectors and Expand World Size's grid");
      Check(Logged(at, "cannot be turned on", "Error"), "the log says the same as an error");
      // A save with only the game's chunks loads.
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.MappingLoaded(harmony, [new ZoneSystem.ChunkIndex(0, 0), new ZoneSystem.ChunkIndex(1, 0), new ZoneSystem.ChunkIndex(63 | 63 << 8, 0)]);
      Check(!ZNet.m_loadError && !WorldSectors.Active, "a save with only the game's chunks loads under its grid");

      // The grid gone: the same save is read with the wide sectors again.
      EWS.GridWidthOverride = null;
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.MappingLoaded(harmony, WideChunks);
      Check(WorldSectors.Active && AllHooksPatched(owner) && !ZNet.m_loadError, "without Expand World Size the same save loads on the wide sectors");

      // The real Expand World Size: the members Better Continents reads are where it looks for them.
      if (File.Exists(EwsDll))
      {
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(EwsDll);
        var grid = assembly.GetType("ExpandWorldSize.ZoneGrid");
        var width = grid?.GetField("Width", BindingFlags.Public | BindingFlags.Static);
        var configured = grid?.GetMethod("GetWidth", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Check(width is { FieldType: var t } && t == typeof(int) && configured is { ReturnType: var r } && r == typeof(int), "Expand World Size 1.45: ZoneGrid.Width (int field) and ZoneGrid.GetWidth() (int) are where the bridge looks");
        Check(grid?.GetField("VanillaWidth", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetRawConstantValue() is EWS.VanillaGridWidth,
          "and its VanillaWidth is the game's 512 the bridge compares with");
      }
      else
        Check(false, "Expand World Size 1.45 is in libs-Tools");
    }
    finally
    {
      EWS.GridWidthOverride = null;
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      SetStatic(typeof(ZNet), "m_world", null);
      SetStatic(typeof(BC), "HarmonyInstance", hadHarmony);
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
  }
}

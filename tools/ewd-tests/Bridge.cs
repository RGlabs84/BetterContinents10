// Added by Wubarrk on 2026-10-26 for the Expand World Data 1.79 bridge fix (0.10.7).
//
// The bridge only: Better Continents' reflection into Expand World Data (compatiblity/EWD.cs) against the real DLL of an Expand
// World Data release, with none of the generation checks (those run against 1.73, see Program.cs). 1.79 moved WorldInfo and
// BiomeManager into namespaces, and the first sign was two log warnings; these modes fail instead. One process per DLL: two
// releases are the same assembly identity.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using BetterContinents;

namespace EwdTest;

internal static class Bridge
{
  public static readonly Dictionary<string, (string Dll, string Version)> Releases = new()
  {
    ["bridge173"] = ("/home/rohan/WubarrkCODING/libs-Tools/1.0/thirdparty/expand-world-data-2026-09-29/ExpandWorldData-1.73.0/ExpandWorldData.dll", "1.73"),
    ["bridge179"] = ("/home/rohan/WubarrkCODING/libs-Tools/1.0/thirdparty/expand-world-data-2026-10-26/ExpandWorldData-1.79.0/ExpandWorldData.dll", "1.79"),
  };

  [MethodImpl(MethodImplOptions.NoInlining)]
  public static void Run(string mode, string work)
  {
    var (dll, expected) = Releases[mode];
    Program.Section($"Expand World Data {expected}: Better Continents' bridge binds");
    var ewd = Generation.LoadExpandWorldData(dll);
    var plugin = CustomAttributeData.GetCustomAttributes(ewd.GetType("ExpandWorldData.EWD")).First(a => a.AttributeType.Name == "BepInPlugin");
    var version = plugin.ConstructorArguments[2].Value as string;
    Program.C(version == expected, $"ExpandWorldData.dll is Expand World Data {expected} (got {version})");
    typeof(BepInEx.Paths).GetProperty(nameof(BepInEx.Paths.ConfigPath))!.GetSetMethod(true)!.Invoke(null, [work]);
    AccessTools.Method(ewd.GetType("Service.Log"), "Init").Invoke(null, [new ManualLogSource("Expand World Data")]);

    var info = EWD.FindType(ewd, "WorldInfo", "Set", "ExpandWorldData.Compatibility", "ExpandWorldData");
    var manager = EWD.FindType(ewd, "BiomeManager", "TryGetBiome", "ExpandWorldData.Biome", "ExpandWorldData");
    Program.C(info != null && info.GetMethod("Set", BindingFlags.Public | BindingFlags.Static) != null, $"WorldInfo.Set found ({info?.FullName})");
    Program.C(manager != null && manager.GetMethod("TryGetBiome", BindingFlags.Public | BindingFlags.Static) != null, $"BiomeManager.TryGetBiome found ({manager?.FullName})");
    Program.C(ewd.GetType("ExpandWorldData.Api") != null, "Api found");

    // A later release that moves the type again is still found by name and member, when the type's name is unique.
    Program.C(EWD.FindType(ewd, "WorldInfo", "Set", "No.Such.Namespace") == info, "WorldInfo is found without its namespace");
    Program.C(EWD.FindType(ewd, "BiomeManager", "TryGetBiome", "No.Such.Namespace") == manager, "BiomeManager is found without its namespace");
    Program.C(EWD.FindType(ewd, "NoSuchType", "Set") == null, "a type that does not exist is not found");

    // The binding itself: names both ways, lava, terrain, the minimap height.
    Program.Section("names, lava, terrain and minimap height bind");
    manager!.GetMethod("SetNames", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [new Dictionary<Heightmap.Biome, string>
    {
      [Heightmap.Biome.None] = "None", [Heightmap.Biome.Meadows] = "Meadows", [Heightmap.Biome.Swamp] = "Swamp",
      [Heightmap.Biome.Mountain] = "Mountain", [Heightmap.Biome.BlackForest] = "BlackForest", [Heightmap.Biome.Plains] = "Plains",
      [Heightmap.Biome.AshLands] = "AshLands", [Heightmap.Biome.DeepNorth] = "DeepNorth", [Heightmap.Biome.Ocean] = "Ocean",
      [Heightmap.Biome.Mistlands] = "Mistlands", [(Heightmap.Biome)0x400] = "DeadWastes",
    }]);
    EWD.BindBiomeNames(ewd);
    Program.C(EWD.BindSize(), "WorldInfo.Set binds");
    Program.C(EWD.TryGetBiome("deadwastes", out var added) && added == (Heightmap.Biome)0x400, "EWD.TryGetBiome(\"deadwastes\") = 0x400");
    Program.C(EWD.TryGetName((Heightmap.Biome)0x400, out var name) && name == "DeadWastes", "EWD.TryGetName(0x400) = DeadWastes");
    Program.C(EWD.TryGetBiome("meadows", out var meadows) && meadows == Heightmap.Biome.Meadows, "vanilla names resolve too");
    Program.C(EWD.Terrain(Heightmap.Biome.Meadows) == Heightmap.Biome.Meadows, "a biome with no terrain of its own is its own terrain");
    Program.C(!EWD.IsLavaBiome(Heightmap.Biome.Meadows), "Meadows are not a lava biome");
    Program.C(Math.Abs(EWD.MinimapHeight(35f, Heightmap.Biome.Meadows) - 35f) < 0.01f, "the minimap height of a plain biome is the height");

    // The size: Set reaches WorldInfo's fields (it then reloads the world's data, which this process cannot do; BC only needs the numbers).
    Program.Section("the world's size reaches Expand World Data");
    EWD.RefreshSize(12345f, 12845f, 1f, 1f);
    var radius = (float)info!.GetField("Radius", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    var total = (float)info.GetField("TotalRadius", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    Program.C(radius == 12345f && total == 12845f, $"WorldInfo.Radius and TotalRadius follow RefreshSize (got {radius} and {total})");
  }
}

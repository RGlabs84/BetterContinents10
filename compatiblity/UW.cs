// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace BetterContinents;

#nullable enable

// Upgrade World (upgrade_world): a mod of commands that work on a world's zones, locations and objects (zones_reset,
// locations_add, locations_restore, upgrade ..., biomes_count). It asks the game for biomes, heights and locations, so what
// Better Continents patches is what it sees. Three places where a Better Continents world meets it, each reached by name and
// failing soft (an Upgrade World that renames one is logged and left alone):
//
//  1. Its "World radius" setting (config section "1. General", default 10500 m; Settings.cs:66 of Upgrade World 1.87, and
//     Zones.cs:41-55, which lists the zones inside it): zones beyond it are not in its commands. The setting "Upgrade World
//     Radius" (Misc) raises it to the world's size; Off (the default) leaves it as it is and the log says how to turn it on.
//  2. locations_restore (RestoreLocations.cs:35-71): it empties the location registry, runs a copy of the game's search loop for
//     every location, and merges the result with the old registry zone by zone. It never calls ClearNonPlacedLocations, where
//     Better Continents puts the location map's pins, so the pins are not in the fresh registry and the merge removes the unplaced
//     ones of the ids it is given (RestoreLocations.cs:109-115). Better Continents puts them in once the registry is emptied
//     (RebuildPostfix), which is what the game's own genloc does first, so the restore finds them where they are.
//  3. "upgrade ashlands" and "upgrade deepnorth" (Upgrade.cs:39-69) choose their zones by distance from a point of the vanilla
//     layout, not by biome: on a world with a biome map of its own they reach whatever lies there. One line in the log, once.
public static class UW
{
  public const string GUID = "upgrade_world";
  // The names Better Continents looks Upgrade World's members up by. tools/uw-tests and tools/check-jere-names.py check each of
  // them against the real DLL.
  internal const string ConfigSection = "1. General";
  internal const string RadiusKey = "World radius";
  internal const string RestoreType = "UpgradeWorld.RestoreLocations";
  internal const string RestoreRebuild = "Rebuild";
  internal const string UpgradeType = "UpgradeWorld.Upgrade";
  /// <summary>Upgrade World's own default for its World radius, metres.</summary>
  internal const int DefaultRadius = 10500;
  /// <summary>One zone, metres (Upgrade World counts its radius in zones of this size).</summary>
  private const double ZoneSize = 64.0;

  internal static bool Installed;
  private static BepInEx.BaseUnityPlugin? Plugin;
  private static bool WarnedUpgrade;
  private static string? RadiusNote;

  public static void Run()
  {
    Installed = false;
    if (!Chainloader.PluginInfos.TryGetValue(GUID, out var info) || info.Instance == null)
      return;
    Installed = true;
    Plugin = info.Instance;
    BetterContinents.Log($"\"Upgrade World\" detected (version {info.Metadata.Version}).");
    var assembly = Plugin.GetType().Assembly;
    var harmony = BetterContinents.HarmonyInstance;
    if (harmony == null)
      return;
    // Each on its own: an optional integration must never stop Better Continents.
    try
    {
      var rebuild = FindRestoreRebuild(assembly);
      if (rebuild == null)
        BetterContinents.LogWarning("Upgrade World compatibility: its locations_restore (RestoreLocations.Rebuild) was not found; "
          + "after locations_restore, 'bc genloc pins' puts the location map's pins back.");
      else
      {
        harmony.Patch(rebuild, postfix: new HarmonyMethod(typeof(UW).GetMethod(nameof(RebuildPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
        BetterContinents.Log("Upgrade World: locations_restore keeps this world's location map pins.");
      }
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Upgrade World compatibility: locations_restore could not be patched ({e.Message}); after it, 'bc genloc pins' puts the location map's pins back.");
    }
    try
    {
      var upgrade = FindUpgradeConstructor(assembly);
      if (upgrade != null)
        harmony.Patch(upgrade, prefix: new HarmonyMethod(typeof(UW).GetMethod(nameof(UpgradePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Upgrade World compatibility: its upgrade command could not be watched ({e.Message}); skipping.");
    }
  }

  // ------------------------------------------------------------------------------------------------ the lookups (internal for the tests)

  /// <summary>RestoreLocations.Rebuild(Dictionary&lt;Vector2s, ZoneSystem.LocationInstance&gt;): private static, one parameter.</summary>
  internal static MethodInfo? FindRestoreRebuild(Assembly assembly)
  {
    var type = assembly.GetType(RestoreType);
    if (type == null)
      return null;
    foreach (var method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
      if (method.Name != RestoreRebuild)
        continue;
      var parameters = method.GetParameters();
      if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Dictionary<Vector2s, ZoneSystem.LocationInstance>))
        return method;
    }
    return null;
  }

  /// <summary>Upgrade(Terminal context, string type, List&lt;string&gt; extra, FiltererParameters args).</summary>
  internal static ConstructorInfo? FindUpgradeConstructor(Assembly assembly)
  {
    var type = assembly.GetType(UpgradeType);
    if (type == null)
      return null;
    foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
    {
      var parameters = constructor.GetParameters();
      if (parameters.Length == 4 && parameters[0].ParameterType == typeof(Terminal) && parameters[1].ParameterType == typeof(string))
        return constructor;
    }
    return null;
  }

  /// <summary>Upgrade World's World radius entry in its own config file: an int in section "1. General".</summary>
  internal static ConfigEntry<int>? FindRadiusEntry(ConfigFile? config) =>
    config != null && config.TryGetEntry<int>(ConfigSection, RadiusKey, out var entry) ? entry : null;

  // ------------------------------------------------------------------------------------------------ 1. the world radius

  /// <summary>The radius Upgrade World needs to list every zone of a world whose land and edge reach <paramref name="totalRadius"/>
  /// metres from the centre: whole zones, so that the outermost one is inside (Zones.cs:41-55 lists the zones whose index is within
  /// ceil(radius / 64) of the centre in a disc).</summary>
  internal static int RadiusFor(float totalRadius) =>
    totalRadius > 0f && !float.IsInfinity(totalRadius) ? (int)(Math.Ceiling(totalRadius / ZoneSize) * ZoneSize) : DefaultRadius;

  private static int Zones(int radius) => (int)Math.Ceiling(radius / ZoneSize);

  internal enum RadiusAction { None, Raised, Suggested }

  /// <summary>What to do about a world that reaches <paramref name="totalRadius"/> metres when Upgrade World's World radius is
  /// <paramref name="current"/>: nothing when it covers the world (it is never lowered), raise it when the setting is on, say how
  /// when it is off. <paramref name="newValue"/> is the value to write for Raised.</summary>
  internal static RadiusAction DecideRadius(int current, float totalRadius, bool on, out int newValue)
  {
    newValue = current;
    int wanted = RadiusFor(totalRadius);
    // Upgrade World counts zones: ceil(radius / 64) of them in each direction. The same number of zones needs no change.
    if (Zones(wanted) <= Zones(current))
      return RadiusAction.None;
    newValue = wanted;
    return on ? RadiusAction.Raised : RadiusAction.Suggested;
  }

  /// <summary>A world has loaded (or its size changed): applies the setting. Says each thing once.</summary>
  internal static void OnWorldLoaded()
  {
    if (!Installed || Plugin == null || !BetterContinents.Settings.ShapesWorld)
      return;
    try
    {
      // The size the maps span, or the one the world is laid out to: whichever reaches further.
      float reach = Math.Max(BetterContinents.Geometry.TotalRadius, BetterContinents.LayoutGeometry(BetterContinents.Settings).TotalRadius);
      ApplyRadius(Plugin.Config, reach, BetterContinents.ConfigUpgradeWorldRadius?.Value ?? false, BetterContinents.Log);
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Upgrade World compatibility: its World radius could not be read or set ({e.Message}).");
    }
  }

  /// <summary>The decision of <see cref="DecideRadius"/> carried out on Upgrade World's config. Returns the action taken.</summary>
  internal static RadiusAction ApplyRadius(ConfigFile? config, float totalRadius, bool on, Action<string> say)
  {
    var entry = FindRadiusEntry(config);
    if (entry == null)
    {
      Once(ref RadiusNote, $"Upgrade World compatibility: its \"{RadiusKey}\" setting (section \"{ConfigSection}\") was not found; its commands keep the radius it has.", say);
      return RadiusAction.None;
    }
    var action = DecideRadius(entry.Value, totalRadius, on, out var wanted);
    switch (action)
    {
      case RadiusAction.Raised:
        var before = entry.Value;
        entry.Value = wanted;
        Once(ref RadiusNote, $"Upgrade World's \"{RadiusKey}\" was {before} m; it is {wanted} m now, so that its commands reach this whole world ({Math.Round(totalRadius)} m from the centre).", say);
        break;
      case RadiusAction.Suggested:
        Once(ref RadiusNote, $"This world reaches {Math.Round(totalRadius)} m from its centre, and Upgrade World's \"{RadiusKey}\" is {entry.Value} m, so its commands leave the outer ring out. "
          + $"The Better Continents setting \"{SettingsSchema.UpgradeWorldRadius.Key}\" (section \"{SettingsSchema.UpgradeWorldRadius.Section}\") sets it to {wanted} m for every world that needs it; or set it in upgrade_world.cfg.", say);
        break;
    }
    return action;
  }

  private static void Once(ref string? last, string note, Action<string> say)
  {
    if (last == note)
      return;
    last = note;
    say(note);
  }

  // ------------------------------------------------------------------------------------------------ 2. locations_restore

  /// <summary>RestoreLocations.Rebuild(instances), after: it has emptied the registry (the first call of a restore), and is about to
  /// run the search loop into it. The pins and the start override go in first, as they do in the game's genloc, so the loop counts
  /// them and the merge finds each pin where it is (an old pin and a fresh one at the same place are the same location).</summary>
  private static void RebuildPostfix(Dictionary<Vector2s, ZoneSystem.LocationInstance> __0)
  {
    try
    {
      if (__0 == null || __0.Count != 0)
        return;
      BetterContinents.ReassertLocationPins(ZoneSystem.instance);
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Upgrade World compatibility: the location map's pins could not be put in for locations_restore ({e.Message}); 'bc genloc pins' puts them back.");
    }
  }

  // ------------------------------------------------------------------------------------------------ 3. upgrade ashlands / deepnorth

  private static void UpgradePrefix(Terminal __0, string __1)
  {
    try
    {
      Note(__1, line => BetterContinents.LogWarning(line), __0 == null ? null : line => __0.AddString(line));
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Upgrade World compatibility: {e.Message}");
    }
  }

  /// <summary>Whether "upgrade &lt;type&gt;" chooses its zones by the vanilla layout, which this world does not have.</summary>
  internal static bool UpgradeSelectsByDistance(string? type, BetterContinents.BetterContinentsSettings settings, WorldGeometry layout) =>
    type != null && (type.Equals("ashlands", StringComparison.OrdinalIgnoreCase) || type.Equals("deepnorth", StringComparison.OrdinalIgnoreCase))
    && settings.ShapesWorld && (settings.HasBiomeMap || !layout.IsVanilla);

  internal static void Note(string? type, Action<string> log, Action<string>? console)
  {
    if (!UpgradeSelectsByDistance(type, BetterContinents.Settings, BetterContinents.Geometry))
      return;
    var biome = type!.Equals("ashlands", StringComparison.OrdinalIgnoreCase) ? "AshLands" : "DeepNorth";
    var line = $"Upgrade World's \"upgrade {type.ToLowerInvariant()}\" picks its zones by distance from a point of the game's own layout (more than 11,600 m from 4,000 m {(biome == "AshLands" ? "north" : "south")} of the centre), "
      + $"not by biome. On this world that can be land of another biome. \"bc regen dry biome={biome}\" lists the zones of that biome, and \"bc regen biome={biome}\" regenerates them.";
    if (WarnedUpgrade)
      return;
    WarnedUpgrade = true;
    log(line);
    console?.Invoke(line);
  }

  /// <summary>For the tests: forget what has been said.</summary>
  internal static void ForgetNotes()
  {
    WarnedUpgrade = false;
    RadiusNote = null;
  }
}

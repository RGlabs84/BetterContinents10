// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace BetterContinents;

/// <summary>"Compact Maps" (07 BetterContinents.Misc): whether a new world keeps its maps as compressed tiles (MapTiles.cs) or
/// as pictures. Before 0.10.3 it was a switch, off by default, and every file written then holds true or false: they read as
/// On and Auto (CompactMapsConverter), which is what each meant for every world that version could make.</summary>
public enum CompactMapsMode
{
  /// <summary>The default: a new world is compact when a map is more than 8192 pixels across or the heightmap has fine heights
  /// (a heightmap-fine.png beside it), and keeps its maps as pictures otherwise.</summary>
  Auto,
  /// <summary>Every new world of the newest settings version is compact.</summary>
  On,
  /// <summary>No new world is compact, whatever its maps, and no heightmap-fine.png is read (a world with fine heights is compact).</summary>
  Off,
}

/// <summary>Reads Compact Maps' old values. BepInEx converts every enum with its one converter for the type Enum (its
/// GetConverter answers that before it looks up the type itself, and AddConverter refuses a converter for an enum), which
/// reads names only, so true and false in a config file or an export.cfg would be refused. The converter is wrapped: this enum
/// reads true as On and false as Auto, anything else as before (a name, whatever its case), and every other enum is handed to
/// the converter that was there, so it reads exactly as it did. If BepInEx keeps its converters elsewhere, true and false are
/// refused as any unknown value is (BepInEx says so and keeps the default), and a line is logged.</summary>
internal static class CompactMapsConverter
{
  private static bool installed;

  /// <summary>Before the settings are bound (SettingsSchema.Bind), once.</summary>
  internal static void Install()
  {
    if (installed)
      return;
    try
    {
      var converters = typeof(TomlTypeConverter).GetProperty("TypeConverters", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        as Dictionary<Type, BepInEx.Configuration.TypeConverter>;
      if (converters == null || !converters.TryGetValue(typeof(Enum), out var enums))
      {
        BetterContinents.LogWarning("Compact Maps: this BepInEx does not let Better Continents read the old values true and false, which it will refuse (use Auto, On or Off).");
        return;
      }
      converters[typeof(Enum)] = new BepInEx.Configuration.TypeConverter
      {
        ConvertToString = enums.ConvertToString,
        ConvertToObject = (text, type) => type == typeof(CompactMapsMode) ? Read(text) : enums.ConvertToObject(text, type),
      };
      installed = true;
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Compact Maps: the old values true and false cannot be read ({e.Message}); use Auto, On or Off.");
    }
  }

  internal static CompactMapsMode Read(string text)
  {
    var trimmed = text.Trim();
    if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
      return CompactMapsMode.On;
    if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
      return CompactMapsMode.Auto;
    // A name; Enum.Parse would also take a number it does not know.
    var mode = (CompactMapsMode)Enum.Parse(typeof(CompactMapsMode), trimmed, ignoreCase: true);
    if (!Enum.IsDefined(typeof(CompactMapsMode), mode))
      throw new ArgumentException($"'{text}' is not Auto, On or Off");
    return mode;
  }
}

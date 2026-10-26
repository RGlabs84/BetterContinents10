// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7): filters for "bc regen".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// What "bc regen" is asked to reset, when it is asked for less than the whole world:
//
//   bc regen [dry] [biome=<names>] [min=<m>] [max=<m>] [pos=<x>,<z>] [zone=<x>,<z>] [chance=<0..1>]
//
//   dry     counts what would be reset and says so, and changes nothing.
//   biome=  zones where any corner of the zone is one of these biomes (a zone half in the biome is reset; the names are the biome
//           map's: Meadows, BlackForest, AshLands, DeepNorth ..., any case, comma between).
//   min=    max=    metres from pos (the world's centre when there is no pos) to the middle of the zone. radius= is max=.
//   zone=   the one zone with these zone coordinates (a zone is 64 m; the zone of a place is its x / 64, z / 64, rounded).
//   chance= each zone that is left is reset with this chance (a number past 1 is a percentage), the same zone the same way
//           for as long as the request stands.
//
// A filter only ever takes zones out of the reset: everything that protects a zone in a plain "bc regen" still does (ZoneReset), a
// location that reaches into a zone left out is kept whole (the plan treats a zone left out as it treats one near a player's base),
// and a word that is not understood starts nothing.
internal sealed class RegenFilter
{
  internal const double ZoneSize = 64.0;
  internal const string Syntax = "bc regen [dry] [biome=<names>] [min=<m>] [max=<m>] [pos=<x>,<z>] [zone=<x>,<z>] [chance=<0..1>]";

  public bool Dry;
  /// <summary>The biomes asked for (a mask of the flags); None: any.</summary>
  public Heightmap.Biome Biomes = Heightmap.Biome.None;
  public float Min;
  public float Max = float.PositiveInfinity;
  public float PosX, PosZ;
  public Vector2s? Zone;
  public double Chance = 1.0;
  /// <summary>The words that made it, in the order given (for what the run says).</summary>
  public string Text = "";

  /// <summary>Whether anything but "dry" was asked: the zones are narrowed.</summary>
  public bool Narrows => Biomes != Heightmap.Biome.None || Min > 0f || !float.IsPositiveInfinity(Max) || Zone.HasValue || Chance < 1.0;

  /// <summary>The biome at a place: the game's, from the world generator (the tests stand in for it).</summary>
  internal Func<float, float, Heightmap.Biome> BiomeAt = (x, z) => WorldGenerator.instance.GetBiome(new Vector3(x, 0f, z));
  /// <summary>A number from 0 up to but not including 1 (the tests stand in for it).</summary>
  internal Func<double> Roll = new System.Random().NextDouble;

  private readonly Dictionary<Vector2s, bool> decided = new(ZoneReset.Comparer);

  /// <summary>Whether the filter lets this zone be reset (the rules that protect it are asked separately). A zone is decided once.</summary>
  public bool Eligible(Vector2s zone)
  {
    if (decided.TryGetValue(zone, out var known))
      return known;
    return decided[zone] = Decide(zone);
  }

  private bool Decide(Vector2s zone)
  {
    if (Zone.HasValue && !Zone.Value.Equals(zone))
      return false;
    double x = zone.x * ZoneSize, z = zone.y * ZoneSize;
    if (Min > 0f || !float.IsPositiveInfinity(Max))
    {
      double dx = x - PosX, dz = z - PosZ;
      double distance = Math.Sqrt(dx * dx + dz * dz);
      if (distance < Min || distance > Max)
        return false;
    }
    if (Biomes != Heightmap.Biome.None && !InBiomes(x, z))
      return false;
    return Chance >= 1.0 || Roll() < Chance;
  }

  // The four corners of the zone, as a zone's biome is judged for a filter anywhere (a zone half in a biome is in it).
  private bool InBiomes(double x, double z)
  {
    const float half = (float)(ZoneSize / 2.0);
    float fx = (float)x, fz = (float)z;
    return (BiomeAt(fx + half, fz + half) & Biomes) != 0 || (BiomeAt(fx - half, fz + half) & Biomes) != 0
        || (BiomeAt(fx + half, fz - half) & Biomes) != 0 || (BiomeAt(fx - half, fz - half) & Biomes) != 0;
  }

  /// <summary>The filter in words, for the line that starts a run: "biome AshLands, 3000 m or more from the centre, 50% of them".</summary>
  public string Describe()
  {
    var parts = new List<string>();
    if (Biomes != Heightmap.Biome.None)
      parts.Add("biome " + string.Join(" or ", Names(Biomes)));
    if (Zone.HasValue)
      parts.Add($"zone {Zone.Value.x},{Zone.Value.y}");
    string from = PosX != 0f || PosZ != 0f ? $"{F(PosX)},{F(PosZ)}" : "the centre";
    if (Min > 0f && !float.IsPositiveInfinity(Max))
      parts.Add($"{F(Min)} to {F(Max)} m from {from}");
    else if (Min > 0f)
      parts.Add($"{F(Min)} m or more from {from}");
    else if (!float.IsPositiveInfinity(Max))
      parts.Add($"within {F(Max)} m of {from}");
    if (Chance < 1.0)
      parts.Add($"{(Chance * 100.0).ToString("0.##", CultureInfo.InvariantCulture)}% of them");
    return string.Join(", ", parts);
  }

  private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

  private static IEnumerable<string> Names(Heightmap.Biome mask)
  {
    for (int bit = 0; bit < 32; bit++)
    {
      var biome = (Heightmap.Biome)(1 << bit);
      if ((mask & biome) != 0)
        yield return BiomeRegistry.Name(biome);
    }
  }

  // ------------------------------------------------------------------------------------------------ the words

  /// <summary>Reads the words after "bc regen". Null for no words (the whole world); the error when a word is not understood.</summary>
  internal static RegenFilter? Parse(string? args, out string error)
  {
    error = "";
    var words = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0)
      return null;
    var filter = new RegenFilter { Text = string.Join(" ", words) };
    var seen = new HashSet<string>();
    bool dry = false;
    foreach (var word in words)
    {
      if (word.Equals("dry", StringComparison.OrdinalIgnoreCase))
      {
        dry = true;
        continue;
      }
      int at = word.IndexOf('=');
      if (at <= 0 || at == word.Length - 1)
      {
        error = $"bc regen does not know '{word}'. {Syntax}. 'bc regen help' says what each does.";
        return null;
      }
      string key = word.Substring(0, at).ToLowerInvariant(), value = word.Substring(at + 1);
      if (key == "radius") key = "max";
      if (key == "biomes") key = "biome";
      if (!seen.Add(key))
      {
        error = $"bc regen: '{key}' is given twice.";
        return null;
      }
      if (!filter.Set(key, value, out error))
        return null;
    }
    if (filter.Max < filter.Min)
    {
      error = $"bc regen: max ({F(filter.Max)} m) is less than min ({F(filter.Min)} m).";
      return null;
    }
    filter.Dry = dry;
    return filter;
  }

  private bool Set(string key, string value, out string error)
  {
    error = "";
    switch (key)
    {
      case "biome":
        foreach (var name in value.Split([','], StringSplitOptions.RemoveEmptyEntries))
        {
          if (!BiomeRegistry.TryParse(name, out var biome) || biome == Heightmap.Biome.None)
          {
            error = $"bc regen: '{name}' is not a biome. {BiomeRegistry.NameHelp()}";
            return false;
          }
          Biomes |= biome;
        }
        if (Biomes == Heightmap.Biome.None)
        {
          error = "bc regen: biome= needs a name.";
          return false;
        }
        return true;
      case "min":
        return Metres(key, value, out Min, out error);
      case "max":
        return Metres(key, value, out Max, out error);
      case "pos":
        {
          if (!Pair(value, out var x, out var z))
          {
            error = $"bc regen: pos= takes two numbers, metres: pos=<x>,<z> ('{value}').";
            return false;
          }
          PosX = (float)x;
          PosZ = (float)z;
          return true;
        }
      case "zone":
        {
          if (!Pair(value, out var x, out var z) || x != Math.Floor(x) || z != Math.Floor(z) || Math.Abs(x) > short.MaxValue || Math.Abs(z) > short.MaxValue)
          {
            error = $"bc regen: zone= takes two whole numbers, zone coordinates: zone=<x>,<z> ('{value}').";
            return false;
          }
          Zone = new Vector2s((int)x, (int)z);
          return true;
        }
      case "chance":
        {
          if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var chance) || double.IsNaN(chance) || chance < 0.0 || chance > 100.0)
          {
            error = $"bc regen: chance= takes a number from 0 to 1, or a percentage up to 100 ('{value}').";
            return false;
          }
          Chance = chance > 1.0 ? chance / 100.0 : chance;
          return true;
        }
      default:
        error = $"bc regen does not know '{key}='. {Syntax}. 'bc regen help' says what each does.";
        return false;
    }
  }

  private static bool Metres(string key, string value, out float metres, out string error)
  {
    error = "";
    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out metres) || float.IsNaN(metres) || metres < 0f)
    {
      metres = key == "max" ? float.PositiveInfinity : 0f;
      error = $"bc regen: {key}= takes a distance in metres, 0 or more ('{value}').";
      return false;
    }
    return true;
  }

  private static bool Pair(string value, out double x, out double z)
  {
    x = z = 0.0;
    var parts = value.Split(',');
    return parts.Length == 2
      && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && !double.IsNaN(x) && !double.IsInfinity(x)
      && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z) && !double.IsNaN(z) && !double.IsInfinity(z);
  }
}

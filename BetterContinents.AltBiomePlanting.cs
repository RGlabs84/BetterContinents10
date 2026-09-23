// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace BetterContinents;

public partial class BetterContinents
{
  // One alt biome a planting key asks for, already resolved against the game's alt-biome list, and whether the
  // author forced it ("!Name" in the legend) past the content rules: disabled, wrong base biome, incompatible.
  public readonly struct PlantedAltBiome(AltBiome altBiome, bool force)
  {
    public readonly AltBiome AltBiome = altBiome;
    public readonly bool Force = force;
  }

  // A point plant ("Name: at x, z" in the legend): the key applies to the whole region under the position.
  public readonly struct PlantPoint(float x, float z, int key)
  {
    public readonly float X = x;
    public readonly float Z = z;
    public readonly int Key = key;
  }

  // The contract a planting layer implements. AltBiomeControl owns everything else: it splits planted patches
  // out into their own regions, applies their alt biomes before the game's random placement (so they count
  // toward its per-alt-biome maximums) and keeps random placement off them. The default provider is the
  // colour-painted alt-biome map (ColourPlanting); AltBiomeControl.PlantingProvider can replace it.
  public interface IAltBiomePlanting
  {
    // Any painted pixel or point plant at all.
    bool HasPlanting { get; }
    // Any painted pixel. Only then are regions split; point plants alone use the game's own regions.
    bool HasPaintedRegions { get; }
    // Main thread, after the game's alt-biome list exists, before any of the calls below. Resolves names and
    // warns about them. May throw: a planting error fails the world load.
    void Prepare();
    // Plant key at a Better Continents map position: x and y are world / TotalSize + 0.5, NOT clamped (outside
    // 0..1 is off the map). 0 = not planted. Called for every grid point from worker threads: cheap and
    // thread-safe.
    int GetPlantKey(float mapX, float mapY);
    // Whether a key takes over a grid point of this base biome. A point it does not take over stays unplanted
    // and open to random placement. Thread-safe after Prepare.
    bool Claims(int key, Heightmap.Biome biome);
    // What to plant for a key. An empty list is a protected region: no alt biome, random or planted.
    IReadOnlyList<PlantedAltBiome> GetAltBiomes(int key);
    IReadOnlyList<PlantPoint> Points { get; }
    // Short human-readable description of a key, for reports.
    string DescribeKey(int key);
  }

  // Authored planting: altbiomemap.png plus its legend, baked into the world settings as ImageMapAltBiome.
  internal sealed class ColourPlanting : IAltBiomePlanting
  {
    private sealed class Resolved
    {
      public readonly List<PlantedAltBiome> Mods = [];
      // Indexed by Heightmap.BiomeIndex: does this key take over land of that biome at all?
      public readonly bool[] Claims = new bool[(int)Heightmap.BiomeIndex.Count];
    }

    // One provider per baked map object, so a warning is given once per map per session and a reloaded map
    // (bc reload ab, bc ab fn) starts afresh.
    private static readonly ConditionalWeakTable<ImageMapAltBiome, ColourPlanting> Cache = new();

    public static ColourPlanting For(ImageMapAltBiome map) => Cache.GetValue(map, m => new ColourPlanting(m));

    private readonly ImageMapAltBiome map;
    private readonly bool hasPainted;
    private readonly List<PlantPoint> points;
    private readonly HashSet<string> warned = [];
    private Resolved?[] resolved = [];

    private ColourPlanting(ImageMapAltBiome map)
    {
      this.map = map;
      hasPainted = map.HasPlantedPixels;
      points = [.. map.Pins.Select(p => new PlantPoint(p.X, p.Z, p.Class))];
    }

    internal ImageMapAltBiome Map => map;
    public bool HasPlanting => hasPainted || points.Count > 0;
    public bool HasPaintedRegions => hasPainted;
    public IReadOnlyList<PlantPoint> Points => points;

    private void WarnOnce(string key, string message)
    {
      if (warned.Add(key))
        LogWarning(message);
    }

    // The game's alt biomes whose AltBiome.m_name matches a legend name: exact (case-insensitive), or a pattern
    // with * wildcards.
    internal static List<AltBiome> Match(string pattern)
    {
      var all = AltBiomeList.m_altBiomes;
      pattern = (pattern ?? "").Trim();
      if (AltBiomeSettings.IsPattern(pattern))
        return [.. all.Where(a => !string.IsNullOrEmpty(a.m_name) && AltBiomeSettings.GlobMatches(pattern, a.m_name))];
      return [.. all.Where(a => string.Equals(a.m_name, pattern, StringComparison.OrdinalIgnoreCase))];
    }

    public void Prepare()
    {
      var result = new Resolved?[map.Classes.Count];
      for (int i = 1; i < map.Classes.Count; i++)
      {
        var def = map.Classes[i];
        var rc = new Resolved();
        foreach (var entry in def.Entries)
        {
          var matches = Match(entry.Pattern);
          if (matches.Count == 0)
          {
            var known = string.Join(", ", AltBiomeList.m_altBiomes.Select(a => a.m_name));
            WarnOnce("unknown:" + entry.Pattern,
              $"Alt biomes: '{entry.Pattern}' in {def.Label} does not exist in this game, so it is not planted. Known alt biomes: {known}");
            continue;
          }
          foreach (var m in matches)
            if (!rc.Mods.Any(t => t.AltBiome == m))
              rc.Mods.Add(new PlantedAltBiome(m, entry.Force));
        }
        for (int b = 1; b < rc.Claims.Length; b++)
        {
          var biome = ((Heightmap.BiomeIndex)b).ToBiome();
          // A colour only takes over the base biomes its alt biomes can change. Disabled alt biomes and alt
          // biomes of another base biome count only when forced.
          rc.Claims[b] = def.IsNone || rc.Mods.Any(t => t.Force || (t.AltBiome.m_enabled && (t.AltBiome.m_biome & biome) != 0));
        }
        if (!def.IsNone && !rc.Claims.Any(c => c))
          WarnOnce("ineffective:" + i, $"Alt biomes: {def.Label} plants nothing: none of its alt biomes exist and are enabled. Its pixels are treated as unplanted.");
        result[i] = rc;
      }
      resolved = result;
    }

    public int GetPlantKey(float mapX, float mapY)
    {
      int c = map.GetClass(mapX, mapY);
      return c;
    }

    public bool Claims(int key, Heightmap.Biome biome)
    {
      var r = resolved;
      if (key <= 0 || key >= r.Length || r[key] is not { } rc)
        return false;
      int index = ImageMapBiome.ToSafeIndex(biome);
      return index > 0 && rc.Claims[index];
    }

    public IReadOnlyList<PlantedAltBiome> GetAltBiomes(int key)
    {
      var r = resolved;
      if (key <= 0 || key >= r.Length || r[key] is not { } rc || map.Classes[key].IsNone)
        return [];
      return rc.Mods;
    }

    public string DescribeKey(int key)
    {
      if (key <= 0 || key >= map.Classes.Count)
        return $"key {key}";
      var def = map.Classes[key];
      if (!def.IsPin)
        return def.Label;
      var pin = map.Pins.FirstOrDefault(p => p.Class == key);
      return pin == null ? def.Label : $"point at {Inv(pin.X)}, {Inv(pin.Z)} [{def.Names}]";
    }

    // What each key resolved to, for the report.
    public string DescribeResolution(int key)
    {
      var r = resolved;
      if (key <= 0 || key >= map.Classes.Count)
        return "";
      if (map.Classes[key].IsNone)
        return "none";
      if (key >= r.Length || r[key] is not { } rc || rc.Mods.Count == 0)
        return "(nothing resolves)";
      return string.Join(", ", rc.Mods.Select(t => (t.Force ? "!" : "") + t.AltBiome.m_name));
    }

    private static string Inv(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
  }
}

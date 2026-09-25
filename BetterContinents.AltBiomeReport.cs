// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-25 for version-agnostic wording (0.9.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // What the alt-biome pipeline produced for this world, for map authors and server owners: log lines at world
  // load, the "bc ab" and "bc_altbiomes" console commands, map pins and a PNG/TXT export.
  public static class AltBiomeReport
  {
    private static readonly Heightmap.Biome[] RealBiomes =
    [
      Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
      Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
      Heightmap.Biome.Ocean,
    ];

    // Heightmap.Biome is not a [Flags] enum, so ToString() prints a number for a combined mask.
    public static string BiomeMask(Heightmap.Biome mask)
    {
      if (mask == Heightmap.Biome.None)
        return "None";
      var names = RealBiomes.Where(b => (mask & b) != 0).Select(b => b.ToString()).ToList();
      return names.Count == 0 ? ((int)mask).ToString(CultureInfo.InvariantCulture) : string.Join("|", names);
    }

    private static AltBiomeWorldData? Data =>
      WorldGenerator.instance?.m_world?.m_biomeData is { IsReady: true } data ? data : null;

    private static AltBiomeControl.AltBiomeParams Params(AltBiome alt) =>
      AltBiomeControl.LastEffective.TryGetValue(alt, out var eff) ? eff
      : AltBiomeControl.LastVanilla.TryGetValue(alt, out var van) ? van
      : AltBiomeControl.AltBiomeParams.From(alt);

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static bool IsPlanted(BiomeSector sector) => AltBiomeControl.PlantedSectors.Contains(sector);

    public static List<string> Summary(bool detailed, string reason = "report")
    {
      var lines = new List<string>();
      var data = Data;
      if (!AltBiomeControl.WorldEnabled)
      {
        lines.Add("Alt biomes: Better Continents is not enabled for this world (vanilla alt biomes).");
        return lines;
      }
      if (data == null)
      {
        lines.Add("Alt biomes: no biome data yet (it is built when the world loads).");
        return lines;
      }
      AltBiomeControl.EnsureSectorInfo(data);
      var s = AltBiomeControl.Active;
      var grid = AltBiomeControl.ExternalGrid
        ? $"external grid {data.Size} x {F(AltBiomeControl.GridPixelSize())} m (Expand World Size)"
        : $"grid {data.Size} x {F(AltBiomeControl.GridPixelSize())} m to {F(AltBiomeControl.SampledRadius)} m ({s.Grid})";
      int plantedRegions = data.Sectors.Count(IsPlanted);
      int withAlt = data.Sectors.Count(x => x.AltBiomes.Count > 0);
      int plantedWithAlt = data.Sectors.Count(x => x.AltBiomes.Count > 0 && IsPlanted(x));
      lines.Add($"Alt biomes ({reason}): mode {s.Mode}, placement seed {s.SeedText}, {grid}, {data.Sectors.Count} sectors, "
                + $"{withAlt} with alt biomes ({plantedWithAlt} planted, {withAlt - plantedWithAlt} random), {plantedRegions} planted region(s)");
      lines.Add($"Alt biomes: hashes grid {AltBiomeControl.LastGridHash:x8}, assignment {AltBiomeControl.LastAssignmentHash:x8}"
                + (AltBiomeControl.LastAgreement != "" ? $"; {AltBiomeControl.LastAgreement}" : ""));
      var planting = AltBiomeControl.Planting;
      if (planting != null && planting.HasPlanting)
        lines.Add("Alt biomes: planting " + (s.Mode == AltBiomeMode.Off
          ? "is switched OFF with the rest of the alt biomes (mode Off)"
          : AltBiomeControl.LastPartitionSummary != "" ? AltBiomeControl.LastPartitionSummary : "uses the game's own regions (point plants only)"));

      var absent = new List<string>();
      foreach (var biome in RealBiomes)
      {
        var sectors = data.Sectors.Where(x => x.Biome == biome).ToList();
        int area = sectors.Sum(x => AltBiomeControl.GetInfo(x).Area);
        if (area == 0)
        {
          absent.Add(biome.ToString());
          continue;
        }
        if (AltBiomeControl.IsGlobal(biome) && sectors.Count == 1)
        {
          lines.Add($"Alt biomes: {biome}: one world-wide sector, edge {sectors[0].EdgeCount}, centre ({F(sectors[0].Center.x)}, {F(sectors[0].Center.y)})");
          continue;
        }
        int tiny = sectors.Count(x => x.EdgeCount < 20);
        int thin = sectors.Count(x => x.EdgeCount >= 20 && AltBiomeControl.GetInfo(x).Thickness(x) < 1.5f);
        int maxEdge = sectors.Count > 0 ? sectors.Max(x => x.EdgeCount) : 0;
        int biomeWithAlt = sectors.Count(x => x.AltBiomes.Count > 0);
        int planted = sectors.Count(IsPlanted);
        lines.Add($"Alt biomes: {biome}: {sectors.Count} regions ({tiny} tiny < 20 edge, {thin} sliver-thin), largest edge {maxEdge}, {biomeWithAlt} with alt biomes"
                  + (planted > 0 ? $", {planted} planted" : ""));
      }
      if (absent.Count > 0)
        lines.Add($"Alt biomes: absent from this map: {string.Join(", ", absent)} (their alt biomes cannot place)");

      int placements = AltBiomeList.m_altBiomes.Sum(a => a.Sectors.Count);
      lines.Add($"Alt biomes: {placements} placement(s) in {withAlt} sector(s)" + (s.Mode == AltBiomeMode.Off ? " - alt biomes are OFF for this world" : ""));
      foreach (var alt in AltBiomeList.m_altBiomes)
        lines.Add("  " + AnalyzeAlt(data, alt));
      lines.AddRange(AltBiomeControl.LastPlantingNotes.Select(n => "Alt biomes: " + n));
      var unknown = s.Overrides.Keys.Where(k => k != AltBiomeSettings.Wildcard
                                                && (AltBiomeSettings.IsPattern(k)
                                                  ? !AltBiomeList.m_altBiomes.Any(a => AltBiomeSettings.GlobMatches(k, a.m_name ?? ""))
                                                  : AltBiomeControl.FindAltBiome(k) == null)).ToList();
      if (unknown.Count > 0)
        lines.Add($"Alt biomes: overrides name no loaded alt biome: {string.Join(", ", unknown)}");
      if (detailed)
      {
        lines.Add("Alt biomes: sectors with alt biomes:");
        lines.AddRange(ListSectors("", 200).Select(l => "  " + l));
      }
      return lines;
    }

    // One line per planting key (what it resolved to and how much land it took over), then one line per planted
    // region with the alt biomes it carries.
    public static List<string> PlantedLines(int limit)
    {
      var lines = new List<string>();
      var data = Data;
      var planting = AltBiomeControl.Planting;
      if (!AltBiomeControl.WorldEnabled || data == null || planting == null || !planting.HasPlanting || AltBiomeControl.Active.Mode == AltBiomeMode.Off)
        return lines;
      AltBiomeControl.EnsureSectorInfo(data);
      var keys = new SortedSet<int>(AltBiomeControl.PlantedPointsByKey.Keys.Concat(AltBiomeControl.IgnoredPointsByKey.Keys));
      foreach (var p in planting.Points)
        keys.Add(p.Key);
      foreach (var key in keys)
      {
        var resolved = string.Join(", ", planting.GetAltBiomes(key).Select(e => (e.Force ? "!" : "") + e.AltBiome.m_name));
        if (resolved == "")
          resolved = "nothing (a protected region)";
        AltBiomeControl.PlantedPointsByKey.TryGetValue(key, out var points);
        AltBiomeControl.IgnoredPointsByKey.TryGetValue(key, out var ignored);
        int regions = data.Sectors.Count(x => AltBiomeControl.GetInfo(x) is var i && (i.PlantKey == key || (i.PointKeys?.Contains(key) ?? false)));
        var land = planting.Points.Any(pp => pp.Key == key) ? $"{regions} region(s)" : $"{points} grid points in {regions} region(s)";
        lines.Add($"Alt biomes:   {planting.DescribeKey(key)} -> {resolved}: {land}"
                  + (ignored > 0 ? $", {ignored} painted points on other base biomes left alone" : ""));
      }
      int shown = 0;
      foreach (var sector in data.Sectors)
      {
        if (!IsPlanted(sector))
          continue;
        if (shown++ >= limit)
        {
          lines.Add($"Alt biomes:   ... {data.Sectors.Count(IsPlanted) - limit} more planted regions (bc ab list planted)");
          break;
        }
        lines.Add("Alt biomes:   " + DescribeSector(sector));
      }
      return lines;
    }

    // Why an alt biome did or did not place at random, counting each candidate region's first failing test in
    // vanilla's order (CanAddModifier), with the values the last placement run actually used.
    public static string AnalyzeAlt(AltBiomeWorldData data, AltBiome alt)
    {
      var p = Params(alt);
      var s = AltBiomeControl.Active;
      int placedPlanted = alt.Sectors.Count(IsPlanted);
      int placedRandom = alt.Sectors.Count - placedPlanted;
      var head = $"{alt.m_name} [{BiomeMask(alt.m_biome)}] {placedRandom}/{p.Min}-{p.Max} chance {F(p.Chance)}"
                 + (placedPlanted > 0 ? $" +{placedPlanted} planted (counted toward the maximum)" : "");
      if (s.Mode == AltBiomeMode.Off)
        return head + ": alt biomes off";
      if (s.Mode == AltBiomeMode.PlantedOnly)
        return head + ": random placement off (PlantedOnly)";
      if (!p.Enabled)
        return head + ": disabled for random placement";
      var reasons = new Dictionary<string, int>();
      void Reject(string reason) => reasons[reason] = reasons.TryGetValue(reason, out var n) ? n + 1 : 1;
      int total = 0, eligible = 0, minSeen = int.MaxValue, maxSeen = 0;
      foreach (var sector in data.Sectors)
      {
        if ((alt.m_biome & sector.Biome) == 0)
          continue;
        total++;
        var info = AltBiomeControl.GetInfo(sector);
        minSeen = Math.Min(minSeen, sector.EdgeCount);
        maxSeen = Math.Max(maxSeen, sector.EdgeCount);
        var why = Rejection(sector, info, p, alt);
        if (why == null) eligible++;
        else Reject(why);
      }
      if (total == 0)
        return head + $": no {BiomeMask(alt.m_biome)} on this map";
      var detail = string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
      return head + $": {eligible} of {total} regions qualify (edge {p.MinEdge}-{p.MaxEdge - 1}, seen {minSeen}-{maxSeen})"
             + (detail != "" ? $"; rejected: {detail}" : "");
    }

    // The static part of CanAddModifier (stacking incompatibility depends on placement order and is left out).
    private static string? Rejection(BiomeSector sector, AltBiomeControl.SectorInfo info, AltBiomeControl.AltBiomeParams p, AltBiome alt)
    {
      var s = AltBiomeControl.Active;
      if (IsPlanted(sector)) return "planted";
      if (s.MinSectorThickness > 0f && info.Thickness(sector) < s.MinSectorThickness) return "too thin";
      if (sector.DistanceFromCenter < p.MinDist) return $"within {F(p.MinDist)} m of centre";
      if (sector.EdgeCount < p.MinEdge) return "too small";
      if (sector.EdgeCount >= p.MaxEdge) return "too big";
      float h = s.MeanSectorHeight ? info.MeanHeight : sector.HeightAvg;
      if (h < p.MinH || h >= p.MaxH) return "avg height out of range";
      if ((p.AboveX != 0f && sector.Center.x < p.AboveX) || (p.BelowX != 0f && sector.Center.x > p.BelowX)
          || (p.AboveY != 0f && sector.Center.y < p.AboveY) || (p.BelowY != 0f && sector.Center.y > p.BelowY))
        return "outside world bounds";
      if ((alt.m_requireNeighbor != 0 || alt.m_notNeighbor != 0) && !(s.FixNeighbourCheck ? AltBiomeControl.NeighboursOk(sector, alt) : AltBiomeControl.VanillaNeighboursOk(sector, alt)))
        return "neighbour rule";
      return null;
    }

    public static string PlantingOf(BiomeSector sector)
    {
      var info = AltBiomeControl.GetInfo(sector);
      var planting = AltBiomeControl.Planting;
      var parts = new List<string>();
      if (info.PlantKey != 0)
        parts.Add("planted " + (planting?.DescribeKey(info.PlantKey) ?? info.PlantKey.ToString(CultureInfo.InvariantCulture)));
      if (info.PointKeys != null)
        foreach (var key in info.PointKeys)
          parts.Add(planting?.DescribeKey(key) ?? key.ToString(CultureInfo.InvariantCulture));
      if (parts.Count > 0)
        return string.Join(", ", parts);
      return sector.AltBiomes.Count > 0 ? "random" : "unplanted";
    }

    public static string DescribeSector(BiomeSector sector)
    {
      var info = AltBiomeControl.GetInfo(sector);
      var alts = sector.AltBiomes.Count > 0 ? string.Join(", ", sector.AltBiomes.Select(a => a.m_name)) : "no alt biome";
      // Both measures, so an author can see when the vanilla one is dragged down by an underwater border.
      var height = $"h {F(sector.HeightAvg)} (mean {F(info.MeanHeight)})";
      return $"#{info.Id} {sector.Biome} [{PlantingOf(sector)}] centre ({F(sector.Center.x)}, {F(sector.Center.y)}) edge {sector.EdgeCount} area {info.Area} thick {F(info.Thickness(sector))} {height} dist {F(sector.DistanceFromCenter)} -> {alts}";
    }

    // No filter: sectors with alt biomes. "all": every sector. "planted": planted regions. Otherwise a biome name
    // or part of an alt-biome name.
    public static List<string> ListSectors(string filter, int limit)
    {
      var data = Data;
      if (data == null)
        return ["no biome data"];
      AltBiomeControl.EnsureSectorInfo(data);
      filter = (filter ?? "").Trim();
      IEnumerable<BiomeSector> sectors;
      if (filter == "")
        sectors = data.Sectors.Where(x => x.AltBiomes.Count > 0);
      else if (filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        sectors = data.Sectors;
      else if (filter.Equals("planted", StringComparison.OrdinalIgnoreCase))
        sectors = data.Sectors.Where(IsPlanted);
      else if (filter.Equals("random", StringComparison.OrdinalIgnoreCase))
        sectors = data.Sectors.Where(x => x.AltBiomes.Count > 0 && !IsPlanted(x));
      else if (Enum.TryParse<Heightmap.Biome>(filter, true, out var biome) && ImageMapBiome.IsValidBiome(biome) && biome != Heightmap.Biome.None)
        sectors = data.Sectors.Where(x => x.Biome == biome);
      else
        sectors = data.Sectors.Where(x => x.AltBiomes.Any(a => (a.m_name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
      var list = sectors.OrderByDescending(x => x.EdgeCount).ToList();
      var lines = list.Take(limit).Select(DescribeSector).ToList();
      if (list.Count > limit)
        lines.Add($"... {list.Count - limit} more");
      if (list.Count == 0)
        lines.Add("no matching sectors");
      return lines;
    }

    public static List<string> Here(Vector3 position)
    {
      var lines = new List<string>();
      var data = Data;
      if (data == null || WorldGenerator.instance == null)
      {
        lines.Add("no biome data");
        return lines;
      }
      AltBiomeControl.EnsureSectorInfo(data);
      var sector = WorldGenerator.instance.GetBiomeSector(position);
      var biome = WorldGenerator.instance.GetBiome(position);
      if (!AltBiomeControl.Info.ContainsKey(sector))
      {
        lines.Add($"No grid sector here: {sector.Biome} without alt biomes (outside the sampled area, or biome data not ready).");
        return lines;
      }
      lines.Add($"Here ({F(position.x)}, {F(position.z)}): " + DescribeSector(sector));
      if (sector.Biome != biome)
        lines.Add($"Note: the grid says {sector.Biome} but the biome map says {biome} here (a 12 m grid cell on a border).");
      if (!AltBiomeControl.WorldEnabled)
        return lines;
      foreach (var alt in AltBiomeList.m_altBiomes.Where(a => (a.m_biome & sector.Biome) != 0))
      {
        var placed = sector.AltBiomes.Contains(alt);
        var why = placed ? null : Rejection(sector, AltBiomeControl.GetInfo(sector), Params(alt), alt);
        lines.Add($"  {alt.m_name}: {(placed ? "PLACED here" : why == null ? "qualifies (not chosen: chance, quota or incompatibility)" : why)}");
      }
      return lines;
    }

    // Agreement check without positions: safe for any player to run on a client.
    public static List<string> ClientSummary()
    {
      var lines = new List<string>();
      var data = Data;
      if (!AltBiomeControl.WorldEnabled || data == null)
      {
        lines.Add("Alt biomes: no Better Continents biome data on this machine.");
        return lines;
      }
      int withAlt = data.Sectors.Count(x => x.AltBiomes.Count > 0);
      int planted = data.Sectors.Count(x => x.AltBiomes.Count > 0 && IsPlanted(x));
      lines.Add($"Alt biomes: mode {AltBiomeControl.Active.Mode}, {data.Sectors.Count} sectors, {withAlt} with alt biomes ({planted} planted, {withAlt - planted} random)");
      lines.Add($"Alt biomes: this machine: grid hash {AltBiomeControl.LastGridHash:x8}, assignment hash {AltBiomeControl.LastAssignmentHash:x8}");
      if (AltBiomeControl.HaveServerHashes)
        lines.Add($"Alt biomes: server: grid hash {AltBiomeControl.ServerGridHash:x8}, assignment hash {AltBiomeControl.ServerAssignmentHash:x8}: {AltBiomeControl.LastAgreement}");
      else if (ZNet.instance != null && !ZNet.instance.IsServer())
        lines.Add("Alt biomes: no placement received from the server.");
      lines.Add("Alt biomes placed: " + string.Join(", ", AltBiomeList.m_altBiomes.Where(a => a.Sectors.Count > 0).Select(a => $"{a.m_name} x{a.Sectors.Count}")));
      return lines;
    }

    // The game's live alt-biome list with the default colours and the game's own rules; before a world loads,
    // the vanilla palette.
    public static List<string> Names(string filter)
    {
      var lines = new List<string>();
      filter = (filter ?? "").Trim();
      var all = AltBiomeList.m_altBiomes;
      if (all.Count == 0)
      {
        lines.Add("The game's alt biome list is empty until a world is loaded. The vanilla alt biome names and default colours:");
        foreach (var (name, hex, biomes) in ImageMapAltBiome.DefaultPalette)
          if (filter == "" || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            lines.Add($"  {name}: {hex}  -  plants on {ImageMapAltBiome.BiomeList(biomes)}");
        return lines;
      }
      lines.Add($"{all.Count} alt biomes in this game (legend line, then the game's own random-placement rules):");
      foreach (var a in all)
      {
        if (filter != "" && (a.m_name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
          continue;
        var hex = ImageMapAltBiome.DefaultColorFor(a.m_name ?? "") ?? "(no default colour)";
        var incompatible = a.m_incompatibleAltBiomes == null || a.m_incompatibleAltBiomes.Count == 0 ? "" : $", incompatible with {string.Join(", ", a.m_incompatibleAltBiomes)}";
        lines.Add($"  {a.m_name}: {hex}  -  {(a.m_enabled ? "" : "DISABLED, ")}biomes {BiomeMask(a.m_biome)}, random {a.m_minAmountSpawned}-{a.m_maxAmountSpawned} at chance {F(a.m_chance)}, edge [{a.m_minEdgeSize},{a.m_maxEdgeSize}), min distance {F(a.m_minDistanceFromCenter)}{incompatible}");
      }
      return lines;
    }

    private static readonly List<Minimap.PinData> Pins = [];

    public static int ShowPins(string filter)
    {
      var data = Data;
      if (data == null || Minimap.instance == null)
        return 0;
      HidePins();
      AltBiomeControl.EnsureSectorInfo(data);
      filter = (filter ?? "").Trim();
      foreach (var sector in data.Sectors)
      {
        if (sector.AltBiomes.Count == 0)
          continue;
        var names = string.Join(", ", sector.AltBiomes.Select(a => a.m_name));
        if (filter != "" && names.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
          continue;
        var label = $"{names} (#{AltBiomeControl.GetInfo(sector).Id}{(IsPlanted(sector) ? ", planted" : "")})";
        Pins.Add(Minimap.instance.AddPin(new Vector3(sector.Center.x, 0f, sector.Center.y), Minimap.PinType.Icon3, label, false, false));
      }
      return Pins.Count;
    }

    public static void HidePins()
    {
      if (Minimap.instance != null)
        foreach (var pin in Pins)
          Minimap.instance.RemovePin(pin);
      Pins.Clear();
    }

    // The pins belong to a minimap that no longer exists.
    public static void ForgetPins() => Pins.Clear();

    private static readonly Dictionary<Heightmap.Biome, Color32> BiomeColors = new()
    {
      // Better Continents' default biome map colours (ImageMapBiome.DefaultColors), so an export lines up
      // with the author's own biome map.
      { Heightmap.Biome.Meadows, new Color32(0, 255, 0, 255) },
      { Heightmap.Biome.BlackForest, new Color32(0, 127, 0, 255) },
      { Heightmap.Biome.Swamp, new Color32(127, 127, 0, 255) },
      { Heightmap.Biome.Mountain, new Color32(255, 255, 255, 255) },
      { Heightmap.Biome.Plains, new Color32(255, 255, 0, 255) },
      { Heightmap.Biome.Mistlands, new Color32(127, 127, 127, 255) },
      { Heightmap.Biome.AshLands, new Color32(255, 0, 0, 255) },
      { Heightmap.Biome.DeepNorth, new Color32(0, 255, 255, 255) },
      { Heightmap.Biome.Ocean, new Color32(0, 0, 255, 255) },
    };

    // An alt biome's tint in the export: its default legend colour, or a stable hash colour for modded ones.
    private static Color32 AltColor(string name)
    {
      var hex = ImageMapAltBiome.DefaultColorFor(name ?? "");
      if (hex != null && ImageMapAltBiome.TryParseColor(hex, out var c))
        return c;
      var hue = (uint)(name ?? "").GetStableHashCode() % 360u / 360f;
      return Color.HSVToRGB(hue, 0.9f, 1f);
    }

    // PNG of the grid, north up like a BC map image: biome colours, sector borders darkened, alt-biome sectors
    // tinted with their alt biome's legend colour (legend in the .txt), planted sectors striped.
    public static string Export()
    {
      var data = Data ?? throw new InvalidOperationException("no biome data");
      AltBiomeControl.EnsureSectorInfo(data);
      var dir = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", ZNet.World?.m_name ?? "world");
      Directory.CreateDirectory(dir);
      var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
      var png = Path.Combine(dir, $"altbiomes-{stamp}.png");
      var txt = Path.Combine(dir, $"altbiomes-{stamp}.txt");
      int size = data.Size;
      var pixels = new Color32[size * size];
      for (int y = 0; y < size; y++)
      {
        for (int x = 0; x < size; x++)
        {
          var sector = data.PointSectors[x, y];
          Color32 c = sector != null && BiomeColors.TryGetValue(sector.Biome, out var bc) ? bc : new Color32(0, 0, 0, 255);
          if (sector != null && sector.AltBiomes.Count > 0)
            c = Color32.Lerp(c, AltColor(sector.AltBiomes[0].m_name), 0.7f);
          if (sector != null && IsPlanted(sector) && ((x + y) & 7) == 0)
            c = new Color32(255, 0, 255, 255);
          bool border = sector != null && x > 0 && y > 0 && x < size - 1 && y < size - 1
                        && (data.PointSectors[x - 1, y] != sector || data.PointSectors[x + 1, y] != sector || data.PointSectors[x, y - 1] != sector || data.PointSectors[x, y + 1] != sector);
          if (border)
            c = Color32.Lerp(c, new Color32(0, 0, 0, 255), 0.5f);
          // Texture rows run bottom-up, so grid row y (world z increasing) needs no flip for a north-up PNG.
          pixels[y * size + x] = c;
        }
      }
      var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
      try
      {
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(png, ImageConversion.EncodeToPNG(tex));
      }
      finally
      {
        UnityEngine.Object.Destroy(tex);
      }
      var lines = Summary(detailed: false, "export");
      lines.AddRange(PlantedLines(int.MaxValue));
      lines.Add("");
      lines.Add("Legend (tint colour of sectors whose first alt biome is):");
      foreach (var alt in AltBiomeList.m_altBiomes.Where(a => a.Sectors.Count > 0))
      {
        Color32 c = AltColor(alt.m_name);
        lines.Add($"  #{c.r:X2}{c.g:X2}{c.b:X2} {alt.m_name}");
      }
      lines.Add("  magenta stripes: planted region; darkened pixels: sector borders");
      lines.Add("");
      lines.Add("Sectors with alt biomes:");
      lines.AddRange(ListSectors("", int.MaxValue));
      lines.Add("");
      lines.Add("All sectors:");
      lines.AddRange(ListSectors("all", int.MaxValue));
      File.WriteAllLines(txt, lines);
      return png;
    }
  }
}

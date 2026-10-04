// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.IO;

namespace BetterContinents;

public partial class BetterContinents
{
  public partial class BetterContinentsSettings
  {
    // Every kind of image map, declared once: what it is called, the file a Directory holds it as, the setting that
    // names its file, its console names, and how it loads, is set and reloads. All is the order the maps are listed in
    // (the Directory setting's text, a world export's sources/, bc reload); LoadOrder is the order a new world reads them
    // in. The save format (Serialize.cs) stays written out map by map: it is frozen.
    internal abstract class MapKind(string name, string fileName, SettingDef<string>? fileSetting, string? group, string reloadCommand)
    {
      // As the console shows it: "Heightmap".
      public readonly string Name = name;
      // Its standard name in a Directory, and in a world export's sources/.
      public readonly string FileName = fileName;
      // The setting that names its file; the flat map (settings version 6 and older) has none.
      public readonly SettingDef<string>? FileSetting = fileSetting;
      // The console group whose "fn" sets its file (bc <Group> fn). None for the flat map; the alt-biome map's is
      // "bc ab fn", which also replants.
      public readonly string? Group = group;
      // bc reload <ReloadCommand>.
      public readonly string ReloadCommand = reloadCommand;

      public abstract ImageMapBase? Map(BetterContinentsSettings settings);
      public bool Has(BetterContinentsSettings settings) => Map(settings) != null;
      // What a world export writes to its sources/ folder.
      public virtual ImageMapBase? Listed(BetterContinentsSettings settings) => Map(settings);

      // A new world's map, from the config (or c, an export.cfg laid over it) and the Directory (ReadConfig).
      internal abstract void Load(BetterContinentsSettings settings, ConfigValues c, string dir, bool lean);
      // bc <group> fn: the map from a resolved path; "" switches it off. True when the map from that path is in use.
      public abstract bool Set(BetterContinentsSettings settings, string path);
      // What bc <group> fn says when the path did not give a map.
      public virtual string NotLoaded(string path) => $"ERROR: Path {path} not found!";
      // bc reload: reads the picture (and its legend) again.
      public abstract void Reload(BetterContinentsSettings settings);

      // The file the map came from, under the Directory when it is there (bc <group> fn's value).
      public string Get(BetterContinentsSettings settings) => SimplePath(Map(settings)?.FilePath ?? string.Empty);
      // bc <group> fn's argument: a full path, a directory (its standard file name) or a file name in the Directory.
      public string Resolve(string path) => ResolvePath(path, FileName);
      // Where the config points now: bc reload's fallback when the map's own file is gone.
      internal virtual string ConfigPath => GetPath(ConfigMapSourceDir.Value, FileName, FileSetting!.Entry.Value);

      public static readonly MapKind Height = new MapKind<ImageMapFloat>("Heightmap", "heightmap.png", SettingsSchema.HeightmapFile, "h", "hm",
        s => s.HeightMap, (s, m) => s.HeightMap = m, (s, path) => ImageMapFloat.Create(path, s.HeightmapAlphaMode), false, (s, m) => m.CreateMap(s.HeightmapAlphaMode));
      public static readonly MapKind Biome = new BiomeMapKind();
      public static readonly MapKind Terrain = new MapKind<ImageMapTerrain>("Terrainmap", "terrainmap.png", SettingsSchema.TerrainmapFile, "terrain", "terrain",
        s => s.TerrainMap, (s, m) => s.TerrainMap = m, (_, path) => ImageMapTerrain.Create(path), true, (_, m) => m.CreateMap());
      public static readonly MapKind Location = new MapKind<ImageMapLocation>("Locationmap", "locationmap.png", SettingsSchema.LocationmapFile, "l", "lm",
        s => s.LocationMap, (s, m) => s.LocationMap = m, (_, path) => ImageMapLocation.Create(path), false, (_, m) => m.CreateMap());
      public static readonly MapKind Rough = new MapKind<ImageMapFloat>("Roughmap", "roughmap.png", SettingsSchema.RoughmapFile, "r", "rm",
        s => s.RoughMap, (s, m) => s.RoughMap = m, (_, path) => ImageMapFloat.Create(path, false), true, (_, m) => m.CreateMap(false));
      public static readonly MapKind Flat = new FlatMapKind();
      public static readonly MapKind Forest = new MapKind<ImageMapFloat>("Forestmap", "forestmap.png", SettingsSchema.ForestmapFile, "fo", "fom",
        s => s.ForestMap, (s, m) => s.ForestMap = m, (_, path) => ImageMapFloat.Create(path, false), true, (_, m) => m.CreateMap(false));
      public static readonly MapKind Heat = new MapKind<ImageMapFloat>("Heatmap", "heatmap.png", SettingsSchema.HeatmapFile, "heat", "heat",
        s => s.HeatMap, (s, m) => s.HeatMap = m, (_, path) => ImageMapFloat.Create(path, false), true, (_, m) => m.CreateMap(false));
      public static readonly MapKind Paint = new MapKind<ImageMapPaint>("Paintmap", "paintmap.png", SettingsSchema.PaintmapFile, "paint", "paint",
        s => s.PaintMap, (s, m) => s.PaintMap = m, (_, path) => ImageMapPaint.Create(path), true, (_, m) => m.CreateMap());
      public static readonly MapKind Lava = new MapKind<ImageMapFloat>("Lavamap", "lavamap.png", SettingsSchema.LavamapFile, "lava", "lava",
        s => s.LavaMap, (s, m) => s.LavaMap = m, (_, path) => ImageMapFloat.Create(path, false), true, (_, m) => m.CreateMap(false));
      public static readonly MapKind Moss = new MapKind<ImageMapFloat>("Mossmap", "mossmap.png", SettingsSchema.MossmapFile, "moss", "moss",
        s => s.MossMap, (s, m) => s.MossMap = m, (_, path) => ImageMapFloat.Create(path, false), true, (_, m) => m.CreateMap(false));
      public static readonly MapKind Vegetation = new MapKind<ImageMapSpawn>("Vegetationmap", "vegetationmap.png", SettingsSchema.VegetationmapFile, "vegetation", "vegetation",
        s => s.VegetationMap, (s, m) => s.VegetationMap = m, (_, path) => ImageMapSpawn.Create(path), false, (_, m) => m.CreateMap());
      public static readonly MapKind Spawn = new MapKind<ImageMapSpawn>("Spawnmap", "spawnmap.png", SettingsSchema.SpawnmapFile, "spawn", "spawn",
        s => s.SpawnMap, (s, m) => s.SpawnMap = m, (_, path) => ImageMapSpawn.Create(path), false, (_, m) => m.CreateMap());
      public static readonly MapKind AltBiome = new AltBiomeMapKind();

      public static readonly MapKind[] All = [Height, Biome, Terrain, Location, Rough, Flat, Forest, Heat, Paint, Lava, Moss, Vegetation, Spawn, AltBiome];
      // The order a new world has always read its maps in: their log lines, and the legends they write, come in this order.
      internal static readonly MapKind[] LoadOrder = [Height, Biome, Location, Rough, Forest, Terrain, Paint, Lava, Moss, Vegetation, Heat, Spawn, AltBiome];
    }

    // A map kind whose map is a T in one of the settings' fields. releasable: a world import (CreateForImport, lean)
    // drops its decoded pixels, as a preset stores the file's bytes; the heightmap keeps its pixels (a preset's thumbnail
    // is drawn from it), and the biome, location, vegetation, spawn and alt-biome maps are stored decoded. rebuild decodes
    // the picture again after a reload reads it.
    internal class MapKind<T>(string name, string fileName, SettingDef<string>? fileSetting, string? group, string reloadCommand,
        Func<BetterContinentsSettings, T?> get, Action<BetterContinentsSettings, T?> set, Func<BetterContinentsSettings, string, T?> create,
        bool releasable, Action<BetterContinentsSettings, T> rebuild)
      : MapKind(name, fileName, fileSetting, group, reloadCommand) where T : ImageMapBase
    {
      public override ImageMapBase? Map(BetterContinentsSettings settings) => get(settings);

      internal override void Load(BetterContinentsSettings settings, ConfigValues c, string dir, bool lean)
      {
        var map = create(settings, GetPath(dir, FileName, c.Get(FileSetting!.Entry)));
        if (lean && releasable)
          map?.ReleasePixels();
        set(settings, map);
      }

      public override bool Set(BetterContinentsSettings settings, string path)
      {
        set(settings, create(settings, path));
        return Has(settings);
      }

      // Into the same map. When its file is gone, the file the config names now, if that one is there.
      public override void Reload(BetterContinentsSettings settings)
      {
        var map = get(settings);
        if (map == null) return;
        if (!map.LoadSourceImage())
        {
          var configPath = ConfigPath;
          if (!File.Exists(configPath) || File.Exists(map.FilePath)) return;
          LogWarning($"Cannot find image {map.FilePath}: Using default path from config.");
          map.FilePath = configPath;
          if (!map.LoadSourceImage()) return;
        }
        rebuild(settings, map);
      }
    }

    // The biome map is only ever replaced by one whose picture and legend read cleanly (SetBiomePath, ReloadBiomeMap).
    private sealed class BiomeMapKind() : MapKind<ImageMapBiome>("Biomemap", "biomemap.png", SettingsSchema.BiomemapFile, "b", "bm",
        s => s.BiomeMap, (s, m) => s.BiomeMap = m, (_, path) => ImageMapBiome.Create(path), false, (_, m) => m.CreateMap())
    {
      public override bool Set(BetterContinentsSettings settings, string path) => settings.SetBiomePath(path);
      public override string NotLoaded(string path) =>
        $"ERROR: {path} was not loaded (not found, or its legend has errors: see the log). The world keeps its current biome map.";
      public override void Reload(BetterContinentsSettings settings) => settings.ReloadBiomeMap();
    }

    // The alt-biome map: a new map clears what a newer Better Continents wrote (SetAltBiomePath, ReloadAltBiomeMap).
    private sealed class AltBiomeMapKind() : MapKind<ImageMapAltBiome>("Altbiomemap", "altbiomemap.png", SettingsSchema.AltBiomemapFile, null, "ab",
        s => s.AltBiomeMap, (s, m) => s.AltBiomeMap = m, (_, path) => ImageMapAltBiome.Create(path), false, (_, m) => m.CreateMap())
    {
      public override bool Set(BetterContinentsSettings settings, string path)
      {
        settings.SetAltBiomePath(path);
        return Has(settings);
      }
      public override void Reload(BetterContinentsSettings settings) => settings.ReloadAltBiomeMap();
    }

    // The flat map: settings version 6 and older only, never from the config, and listed only when it is its own picture.
    // A world that inverts its rough map instead reloads that; otherwise it falls back to the rough map's file.
    private sealed class FlatMapKind() : MapKind<ImageMapFloat>("Flatmap", "flatmap.png", null, null, "fm",
        s => s.FlatMap, (s, m) => s.FlatMap = m, (_, path) => ImageMapFloat.Create(path, false), false, (_, m) => m.CreateMap(false))
    {
      public override ImageMapBase? Listed(BetterContinentsSettings settings) => settings.UseRoughInvertedAsFlat ? null : settings.FlatMap;
      internal override void Load(BetterContinentsSettings settings, ConfigValues c, string dir, bool lean) { }
      internal override string ConfigPath => Rough.ConfigPath;
      public override void Reload(BetterContinentsSettings settings)
      {
        if (settings.UseRoughInvertedAsFlat)
          Rough.Reload(settings);
        else
          base.Reload(settings);
      }
    }
  }
}

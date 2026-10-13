// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-02 for export folders used as the Directory (0.9.4), and on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Splatform;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // These are what are baked into the world when it is created
  public partial class BetterContinentsSettings
  {

    public bool EnabledForThisWorld;
    public int Version;
    public float GlobalScale = 1f;
    public float MountainsAmount;
    public float SeaLevelAdjustment;
    public float MaxRidgeHeight;
    public float RidgeScale;
    public float RidgeBlendSigmoidB;
    public float RidgeBlendSigmoidXOffset;
    public float HeightmapAmount = 1f;
    public float HeightmapBlend = 1f;
    public float HeightmapAdd;
    public bool OceanChannelsEnabled = true;
    public bool AshlandsGapEnabled = false;
    public bool DeepNorthGapEnabled = false;
    public bool RiversEnabled = true;
    public float ForestScale = 1f;
    public float ForestAmountOffset;
    public int BiomePrecision;
    public bool OverrideStartPosition;
    public float StartPositionX;
    public float StartPositionY;
    public bool SkipDefaultLocations;
    public float RoughmapBlend = 1f;
    public bool UseRoughInvertedAsFlat;
    public float FlatmapBlend;
    public float ForestmapMultiply = 1f;
    public float ForestmapAdd = 1f;
    public bool DisableMapEdgeDropoff;
    public bool MountainsAllowedAtCenter;
    public bool ForestFactorOverrideAllTrees;
    public bool HeightmapOverrideAll = true;
    // High Terrain (SettingsSchema.HighTerrain): which worlds get the patches of the game's height rules (HighTerrain.Wanted). Auto unless the
    // world says otherwise; saved only when it is not Auto (DataKey.HighTerrain), and a world without the key is Auto.
    public HighTerrainMode HighTerrainMode = HighTerrainMode.Auto;
    public float HeightmapMask;
    public float HeatMapScale = 10f;
    public float WorldSize = 10000f;
    public float EdgeSize = 500f;
    public bool FixWaterColor = true;
    public NoiseStackSettings BaseHeightNoise = new();
    // Null = the world predates 0.8.1 (or uses no alt-biome option): vanilla alt-biome behaviour.
    public AltBiomeSettings? AltBiomes;
    // The DataKey.AltBiomes blob as read, kept only when a newer Better Continents wrote it (or it could not be
    // read): it is saved back unchanged until an edit replaces it, so an older build never drops data.
    private byte[]? AltBiomesBlobAsRead;

    public AltBiomeSettings EffectiveAltBiomes => AltBiomes ?? AltBiomeSettings.Legacy;

    // ---- baked placements (0.10.4) --------------------------------------------------------------------------------------------
    // The world's layer (BakedLayer, placements.bcp): saved under DataKey.Placements, and never sent in the settings package (it goes in a
    // transfer of its own: BakedTransfer). The active settings' layer is the one that is current (BakedLayerStore.Current), so a change of
    // the layer is a swap of this reference and the world saves the layer it has. Null in a world without one.
    internal volatile BakedLayer? Layer;
    public bool HasLayer => Layer != null;
    // A copy of these settings with another layer, which is not a layer of the active world (a preset saved from a running world keeps the
    // compiler's records only). The maps and everything else are the same objects.
    internal BetterContinentsSettings WithLayer(BakedLayer? layer)
    {
      var copy = (BetterContinentsSettings)MemberwiseClone();
      copy.Layer = layer;
      copy.LayerSentApart = false;
      copy.LayerBytesAsRead = null;
      copy.LayerError = null;
      return copy;
    }
    // Set when the settings package of a world with a layer was read: the layer is sent apart, and has not come yet when Layer is null.
    internal bool LayerSentApart;
    // Set when the layer in the world's settings could not be read: the world load then stops (AltBiomeControl.BeforeVerifyBiomeData), as it
    // does for an alt-biome map that cannot be read. Its bytes are kept as they were, and saved back unchanged (Serialize).
    internal string? LayerError;
    private byte[]? LayerBytesAsRead;
    // The world's terrain is the game's own (spec 4): a world made without Better Continents' maps, or with Better Continents off for it,
    // that carries a layer. Its settings hold nothing but this, a wide-sectors flag and the layer.
    public bool GameTerrain;
    // Whether Better Continents shapes this world's terrain, biomes, vegetation, locations and water (every toggle, every map, every
    // size): it is on for the world and the world's terrain is not the game's own. EnabledForThisWorld stays the test of whether the world
    // is a Better Continents world at all (saved, sent, versions matched).
    public bool ShapesWorld => EnabledForThisWorld && !GameTerrain;

    // For live edits (console): never mutate the shared Legacy instance.
    public AltBiomeSettings EditAltBiomes()
    {
      AltBiomesBlobAsRead = null;
      return AltBiomes ??= AltBiomeSettings.Legacy.Clone();
    }

    // Non-serialized
    private ImageMapFloat? HeightMap;
    public bool HeightMapAlpha = false;
    private ImageMapBiome? BiomeMap;
    private ImageMapTerrain? TerrainMap;
    private ImageMapPaint? PaintMap;
    private ImageMapFloat? LavaMap;
    private ImageMapFloat? MossMap;
    private ImageMapSpawn? VegetationMap;

    private ImageMapLocation? LocationMap;
    private ImageMapFloat? RoughMap;
    private ImageMapFloat? FlatMap;
    private ImageMapFloat? ForestMap;
    private ImageMapFloat? HeatMap;
    private ImageMapSpawn? SpawnMap;
    // Baked alt-biome map (class per pixel, legend classes, point plants); serialized under DataKey.AltBiomeMap.
    private ImageMapAltBiome? AltBiomeMap;
    // The DataKey.AltBiomeMap block as read, kept only when a newer Better Continents wrote it (or it could not be
    // read): it is saved back unchanged until the map is replaced or reloaded.
    private byte[]? AltBiomeMapBlockAsRead;

    public bool HasHeightMap => HeightMap != null;
    // The heightmap's fine heights (heightmap-fine.png): how many bits its bytes use, 0 for none; and why a file beside the heightmap
    // was not used, null when it was or there is none (bc info, a world import's warnings).
    internal int FineBits => HeightMap?.FineBits ?? 0;
    internal string? FineNote => HeightMap?.FineNote;
    public bool HasAltBiomeMap => AltBiomeMap != null;
    internal ImageMapAltBiome? AltBiomeMapData => AltBiomeMap;
    public bool HasBiomeMap => BiomeMap != null;
    // Before the alt-biome grid is built: says which of the map's biomes the world cannot use now (BiomeRegistry.Usable).
    internal void WarnUnusableBiomes() => BiomeMap?.WarnUnusable();
    public bool HasLocationMap => LocationMap != null;
    public bool HasRoughMap => RoughMap != null;
    public bool HasFlatMap => FlatMap != null;
    public bool HasForestMap => ForestMap != null;
    public bool HasTerrainMap => TerrainMap != null;
    public bool HasPaintMap => PaintMap != null;
    public bool HasLavaMap => LavaMap != null;
    public bool HasMossMap => MossMap != null;
    public bool HasVegetationMap => VegetationMap != null;
    public bool HasHeatMap => HeatMap != null;
    public bool HasSpawnMap => SpawnMap != null;


    public bool AnyImageMap => MapKind.All.Any(kind => kind.Has(this));
    public bool ShouldHeightMapOverrideAll => HasHeightMap && HeightmapOverrideAll;

    // ---- a new world's settings ---------------------------------------------------------------------------------------
    // A new world takes its settings once, when it is first saved (WorldPatch), from the preset chosen in the New World
    // screen (Presets.LoadActivePreset):
    //   Disabled        Better Continents is off for the world.
    //   a preset file   the settings saved in it, maps and all, at the settings version it was saved with, so a preset
    //                   makes the world it always made. One saved before alt biomes (0.8.1) takes the config's alt-biome
    //                   options, as a new world does.
    //   From Config     BetterContinents.cfg as it is now (Create). When Directory is a world export (it holds an
    //                   export.cfg), the settings in export.cfg win over the config's (WorldImport.DirectoryValues), and
    //                   the maps span the world the way they spanned the exported one (WorldImport.ExportVersion).
    // bc_import makes a preset by the From Config rule, from a snapshot of the config with the folder's export.cfg over it
    // (CreateForImport). A world made either way gets the newest settings version (12), unless an export says otherwise
    // or, From Config, Override version names an older one (NewWorldVersion).

    public static BetterContinentsSettings Create()
    {
      bool enabled = ConfigEnabled.Value;
      return Create(enabled, enabled ? WorldImport.DirectoryValues() ?? ConfigValues.Live : ConfigValues.Live);
    }

    // From Config with the config read beforehand (NewWorldBuild.Choose, on the main thread), so that this runs on any thread.
    internal static BetterContinentsSettings Create(bool enabled, ConfigValues values)
    {
      Log($"Init settings for new world");
      var settings = new BetterContinentsSettings { EnabledForThisWorld = enabled };
      if (settings.EnabledForThisWorld)
        settings.FromConfig(values, overridable: true, lean: false);
      return settings;
    }

    // World import (WorldImport): the settings "From Config" would give a new world if the config held these values
    // (an export folder's export.cfg over a snapshot of the config). Unlike Create it runs on any thread: it reads no
    // live config value. lean drops the decoded pixels of every map a preset stores as its file bytes, which halves the
    // memory an import holds; such settings are only good for Save.
    internal static BetterContinentsSettings CreateForImport(ConfigValues values, bool lean)
    {
      var settings = new BetterContinentsSettings { EnabledForThisWorld = true };
      settings.FromConfig(values, overridable: false, lean);
      return settings;
    }

    public static BetterContinentsSettings Disabled() => new() { EnabledForThisWorld = false };

    private void FromConfig(ConfigValues values, bool overridable, bool lean)
    {
      Version = NewWorldVersion(values, overridable);
      MapsFromExport = values.FromExport;
      ReadConfig(values, lean);
      // Wide Sectors: a world that reaches past the game's sectors is made with the wide ones (Auto and On), as its size is now known.
      WorldSectors.NewWorld(this, values.Get(SettingsSchema.WideSectors.Entry), ExpandWorldSizeGeometry);
      WarnHeightmapRecord();
    }

    // A world export's heightmap.png says which Heightmap Amount and Sea Level Adjustment its heights are encoded for
    // (HeightmapRecord). Read with others, its land sits higher or lower than it did in the exported world.
    private void WarnHeightmapRecord()
    {
      if (HeightMap?.Record is not { } record || record.Matches(HeightmapAmount, SeaLevel))
        return;
      LogWarning(string.Format(CultureInfo.InvariantCulture,
        "{0} was made by a world export for Heightmap Amount {1} and Sea Level Adjustment {2}, but this new world reads it at "
        + "Heightmap Amount {3} and Sea Level Adjustment {4}, so its land is not at the heights it was made for. The export's "
        + "export.cfg has the settings it needs.",
        string.IsNullOrEmpty(HeightMap.FilePath) ? "The heightmap" : HeightMap.FilePath, record.Amount, record.SeaLevel, HeightmapAmount, SeaLevel));
    }

    private static string GetPath(string projectDir, string projectDirFileName, string defaultFileName)
    {
      if (string.IsNullOrEmpty(projectDir))
        return CleanPath(defaultFileName);
      var path = Path.Combine(projectDir, CleanPath(projectDirFileName));
      if (File.Exists(path))
        return path;
      else
        return "";
    }

    // World export (WorldExport's sources/ folder): every image map this world carries, under the file name the
    // Directory setting loads it from. The flat map (settings version 6 and older) has no such setting.
    internal List<(string FileName, ImageMapBase Map)> LoadedImageMaps()
    {
      var maps = new List<(string, ImageMapBase)>();
      foreach (var kind in MapKind.All)
        if (kind.Listed(this) is { } map)
          maps.Add((kind.FileName, map));
      return maps;
    }

    // Everything a new world takes from the config. ConfigValues.Live reads BetterContinents.cfg as it is now. Every
    // plain value comes from its definition in SettingsSchema, so a new world can only ever read the config's own
    // defaults and ranges; then the maps, from their kinds (MapKind), in the order they have always loaded in. lean:
    // a world import drops the decoded pixels a preset does not need (MapKind<T>).
    private void ReadConfig(ConfigValues c, bool lean)
    {
      foreach (var setting in SettingsSchema.Scalars)
        setting.Read(c, this);
      BaseHeightNoise = new();

      var dir = c.Get(ConfigMapSourceDir);
      CompactForLargeMaps(c, dir);
      foreach (var kind in MapKind.LoadOrder)
        kind.Load(this, c, dir, lean);
      AltBiomes = AltBiomeSettings.FromConfig(c);
      ReadLayerFile(dir);
    }

    // The file a Directory holds the world's baked layer in (a compiler's, or a world export's: WorldExport writes it beside the maps).
    internal const string LayerFileName = "placements.bcp";

    // 0.10.4: placements.bcp in the Directory is the new world's baked layer. It is checked (BakedLayer.Read) and kept byte for byte. A file
    // that cannot be read makes a world without its layer, and an error line says which file and what is wrong; `bc_bake load` adds one later.
    private void ReadLayerFile(string dir)
    {
      Layer = null;
      if (string.IsNullOrEmpty(dir))
        return;
      var path = Path.Combine(dir, LayerFileName);
      if (!File.Exists(path))
        return;
      try
      {
        var bytes = File.ReadAllBytes(path);
        Layer = BakedLayer.Read(bytes, bytes.Length);
        Log($"Baked layer: {path}: revision {Layer.Revision}, {Layer.Placements:N0} records in {Layer.Zones.Count:N0} zones");
      }
      catch (Exception e) when (e is BakedFormatException || e is IOException || e is UnauthorizedAccessException)
      {
        LogError($"The baked layer {path} cannot be read ({e.Message}): the world is made without it. 'bc_bake load' adds a layer to a world later.");
      }
    }

    // Compact Maps as a new world reads it (SettingsSchema.CompactMaps): On makes the world compact now, in the newest settings version only
    // (tiles are saved by no other); Auto and Off leave it to the maps (CompactForLargeMaps, NoteFine), and Off to none.
    internal void SetCompactMaps(CompactMapsMode mode)
    {
      CompactMapsChoice = mode;
      CompactMaps = mode == CompactMapsMode.On && Version >= UnifiedVersion;
    }

    // A picture of more pixels than this across (8192: 67 million pixels) makes a world with Compact Maps on Auto compact. Saved as
    // pictures, a map of 16384 pixels across takes 268 MB for each biome, spawn and vegetation map (a byte a pixel, uncompressed),
    // which the world file holds and every joining player is sent, and every decoded map takes 0.5 to 1 GB of memory for as long as
    // the world runs; as tiles they take a few megabytes (the biome map) to a hundred (a noisy heightmap), and a few tiles at a time
    // are decoded as the world reads them. int.MaxValue switches this off.
    internal static int CompactAbove = 8192;

    private void CompactForLargeMaps(ConfigValues c, string dir)
    {
      // Tiles are saved only by a world of the newest settings version (DataKey.TiledMap), and a map of the alt-biome or
      // location kind holds none (a class per pixel, run-length encoded; the positions of the pins).
      if (CompactMaps || Version < UnifiedVersion)
        return;
      foreach (var kind in MapKind.LoadOrder)
      {
        if (kind.FileSetting == null || kind == MapKind.Location || kind == MapKind.AltBiome)
          continue;
        var path = GetPath(dir, kind.FileName, c.Get(kind.FileSetting.Entry));
        if (path == "" || !File.Exists(path) || ImageMapBase.PictureSize(path) is not { } size || Math.Max(size.Width, size.Height) <= CompactAbove)
          continue;
        if (CompactMapsChoice == CompactMapsMode.Off)
        {
          LogWarning($"Compact Maps is Off, so this world keeps its maps as pictures although {kind.Name} {path} is {size.Width} x {size.Height} pixels (more than {CompactAbove} across). " +
            "As pictures such maps take gigabytes of memory, and the world file and what every joining player is sent come to hundreds of megabytes.");
          return;
        }
        CompactMaps = true;
        Log($"Compact Maps is Auto and {kind.Name} {path} is {size.Width} x {size.Height} pixels (more than {CompactAbove} across), so this world is compact. " +
          "A map that large is kept, saved and sent to joining players as compressed tiles: as pictures it would take gigabytes of memory and a world file of hundreds of megabytes.");
        return;
      }
    }

    // The heightmap just made (a new world's, bc h fn, bc reload): a heightmap with fine heights is held as tiles, and so are the other
    // maps of its world (read after it, or made from now on). Returns the map.
    private ImageMapFloat? NoteFine(ImageMapFloat? map)
    {
      if (map is { HasFine: true } && !CompactMaps)
      {
        CompactMaps = true;
        Log($"Compact Maps is on for this world: its heightmap has fine heights ({map.FineName}), which a world keeps as compressed tiles, and so it does its maps.");
      }
      return map;
    }

    #region Setters
    public float ContinentSize
    {
      set => GlobalScale = FeatureScaleCurve(value);
      get => InvFeatureScaleCurve(GlobalScale);
    }

    public float SeaLevel
    {
      set => SeaLevelAdjustment = Mathf.Lerp(1f, -1f, value);
      get => Mathf.InverseLerp(1f, -1f, SeaLevelAdjustment);
    }

    public bool MapEdgeDropoff
    {
      set => DisableMapEdgeDropoff = !value;
      get => !DisableMapEdgeDropoff;
    }

    public float ForestScaleFactor
    {
      set => ForestScale = FeatureScaleCurve(value);
      get => InvFeatureScaleCurve(ForestScale);
    }

    public float ForestAmount
    {
      set => ForestAmountOffset = Mathf.Lerp(1, -1, value);
      get => Mathf.InverseLerp(1, -1, ForestAmountOffset);
    }

    // bc b fn: an empty path switches the biome map off. Any other path replaces the world's map only if its picture
    // and legend read cleanly; otherwise the world keeps its map (before 0.9.3 a wrong path or a legend error replaced
    // it, and the next save kept the damage). True when the new map is in use.
    public bool SetBiomePath(string path)
    {
      if (string.IsNullOrEmpty(path))
      {
        BiomeMap = null;
        return false;
      }
      var map = ImageMapBiome.Create(path, CompactMaps);
      if (!UsableReload(map, path))
        return false;
      BiomeMap = map;
      return true;
    }
    private bool UsableReload(ImageMapBiome? map, string path)
    {
      if (map != null && map.LegendErrors == 0)
      {
        BiomeRegistry.RefreshUsable();
        map.WarnUnusable();
        return true;
      }
      LogError(map == null
        ? $"The biome map {path} could not be read: the world keeps its current biome map."
        : $"The legend of {path} has errors (see above): the world keeps its current biome map. Fix the legend and reload.");
      return false;
    }

    public void SetAltBiomePath(string path)
    {
      AltBiomeMap = ImageMapAltBiome.Create(path);
      AltBiomeMapBlockAsRead = null;
      AltBiomeMapError = null;
    }
    // Attaches an already decoded map (tests and tools; the game uses SetAltBiomePath).
    internal void SetAltBiomeMap(ImageMapAltBiome? map)
    {
      AltBiomeMap = map;
      AltBiomeMapBlockAsRead = null;
      AltBiomeMapError = null;
    }

    private static string SimplePath(string path)
    {
      path = CleanPath(path);
      if (path.StartsWith(ConfigMapSourceDir.Value))
        return path.Substring(ConfigMapSourceDir.Value.Length).TrimStart(Path.DirectorySeparatorChar);
      return path;
    }
    private static string ResolvePath(string path, string defaultName)
    {
      path = CleanPath(path);
      if (File.Exists(path))
        return path;
      if (string.IsNullOrEmpty(path))
      {
        if (File.Exists(Path.Combine(ConfigMapSourceDir.Value, defaultName)))
          return Path.Combine(ConfigMapSourceDir.Value, defaultName);
        return "";
      }
      var name = Path.GetFileName(path);
      if (name != "" && !Path.HasExtension(name))
        name += Path.GetExtension(defaultName);
      var directory = Path.GetDirectoryName(path);
      if (name != "" && File.Exists(Path.Combine(ConfigMapSourceDir.Value, name)))
        return Path.Combine(ConfigMapSourceDir.Value, name);
      if (directory != "" && File.Exists(Path.Combine(directory, defaultName)))
        return Path.Combine(directory, defaultName);
      return "";
    }
    #endregion

    private static float FeatureScaleCurve(float x) => ScaleRange(Gamma(x, 0.726965071031f), 0.2f, 3f);
    private static float InvFeatureScaleCurve(float y) => InvGamma(InvScaleRange(y, 0.2f, 3f), 0.726965071031f);

    private static float Gamma(float x, float h) => Mathf.Pow(x, Mathf.Pow(1 - h * 0.5f + 0.25f, 6f));
    private static float InvGamma(float g, float h) => Mathf.Pow(g, 1 / Mathf.Pow(1 - h * 0.5f + 0.25f, 6f));

    private static float ScaleRange(float x, float a, float b) => a + (b - a) * (1 - x);
    private static float InvScaleRange(float y, float a, float b) => 1f - (y - a) / (b - a);


    public void Dump(Action<string>? output = null)
    {
      output ??= Log;

      if (EnabledForThisWorld && GameTerrain)
      {
        // Nothing else in its settings means anything: its terrain, biomes, vegetation and locations are the game's own.
        output($"Version {Version}");
        output("Game terrain: this world keeps the game's own terrain, biomes, vegetation and locations; Better Continents carries only its baked placements here");
        if (WideSectors)
          output("Wide sectors: every zone out to 65504 m has a sector and save chunks of its own, where the game gives everything past 16.4 km one shared sector");
        DumpLayer(output);
      }
      else if (EnabledForThisWorld)
      {
        output($"Version {Version}");
        output($"Continent size {ContinentSize}");
        output($"Mountains amount {MountainsAmount}");
        output($"Sea level adjustment {SeaLevel}");
        output($"Ocean channels enabled {OceanChannelsEnabled}");
        output($"Ashlands gap enabled {AshlandsGapEnabled}");
        output($"Deep North gap enabled {DeepNorthGapEnabled}");
        output($"Rivers enabled {RiversEnabled}");
        if (WorldSize != 10000f)
          output($"World size {WorldSize}");
        if (EdgeSize != 500f)
          output($"Edge size {EdgeSize}");
        if (MapsSpanWorldSize && OwnGeometry is { IsVanilla: false } own)
          output($"The maps span World Size and Edge Size: {own.TotalSize} m across"
                 + (LayoutFollowsWorldSize && !ExpandWorldSizeSizes ? ", and the world is laid out to that size" : ""));
        output($"Fix water color {FixWaterColor}");

        output($"Map edge dropoff {MapEdgeDropoff}");
        output($"Mountains allowed at center {MountainsAllowedAtCenter}");
        if (CompactMaps)
          output("Compact Maps: the maps are held and saved as compressed tiles, each decoded when the world reads it");
        if (WideSectors)
          output("Wide sectors: every zone out to 65504 m has a sector and save chunks of its own, where the game gives everything past 16.4 km one shared sector");

        if (HeightMap != null)
        {
          output($"Heightmap file ({HeightMap.Size}) {HeightMap.FilePath}");
          if (HeightMap.HasFine)
            output($"Fine heights: {HeightMap.FineBits} bits a pixel on top of the heightmap's 16: ground in steps of {FineHeights.Length(FineHeights.Resolution(HeightmapAmount, HeightMap.FineBits))} "
              + $"at Heightmap Amount {HeightmapAmount} (the heightmap alone: {FineHeights.Length(FineHeights.Step(HeightmapAmount))})");
          else if (HeightMap.FineNote != null)
            output(HeightMap.FineNote);
          output($"Heightmap amount {HeightmapAmount}, blend {HeightmapBlend}, add {HeightmapAdd}, mask {HeightmapMask}");
          if (HighTerrainMode == HighTerrainMode.Auto && HighTerrain.Wanted(this))
            output($"High terrain: the land can reach {HighTerrain.MaxMetres(this):0} m, so the game's rules that hold a height are patched (what is inside a dungeon, the ground and the grass, altitude limits, the AI's tiles)");
          if (HeightMap.Record is { } record)
            output($"Heightmap made by a world export for Heightmap Amount {record.Amount}, Sea Level Adjustment {record.SeaLevel}");
          if (HeightMapAlpha)
            output(HeightmapAlphaMode == ImageMapFloat.HeightAlpha.Blend
              ? "Heightmap alpha: full precision, blended with the game's own terrain by the alpha"
              : "Heightmap alpha: 8-bit heights, the alpha unused (a world made by an older Better Continents)");
          if (HeightmapOverrideAll)
          {
            output($"Heightmap overrides ALL");
          }
        }
        else output($"Heightmap disabled");
        // Auto is said above, where the heightmap is; On and Off are the world's own choice, with or without one.
        if (HighTerrainMode == HighTerrainMode.On)
          output($"High Terrain On: the game's rules that hold a height are patched for this world, whatever its heights (what is inside a dungeon, the ground and the grass, altitude limits, the AI's tiles); its land cannot pass {HighTerrain.MaxMetres(this):0} m");
        else if (HighTerrainMode == HighTerrainMode.Off)
          output("High Terrain Off: the game's own height rules stand, whatever Heightmap Amount is (no grass above 500 m, no plants, creatures or locations above about 1,000 m, inside a dungeon above 3,000 m, no ground found above 6,000 m)");

        if (Version < 7)
        {
          if (UseRoughInvertedAsFlat)
          {
            output($"Using inverted Roughmap as Flatmap");
          }
          else
          {
            if (FlatMap != null)
            {
              output($"Flatmap file {FlatMap.FilePath}");
              output($"Flatmap size {FlatMap.Size}x{FlatMap.Size}, blend {FlatmapBlend}");
            }
            else
            {
              output($"Flatmap disabled");
            }
          }
        }
        else
        {
          output($"Base height noise stack:");
          BaseHeightNoise.Dump(str => output($"    {str}"));
        }

        if (RoughMap != null)
        {
          output($"Roughmap file ({RoughMap.Size}) {RoughMap.FilePath}");
          output($"Roughmap blend {RoughmapBlend}");
        }
        else output($"Roughmap disabled");

        if (BiomeMap != null)
          output($"Biomemap file ({BiomeMap.Size}) {BiomeMap.FilePath}");
        else output($"Biomemap disabled");
        // Applies with or without a biome map (EffectiveBiomePrecision, BiomePrecisionGrid).
        var precision = Mathf.Clamp(BiomePrecision, 0, BiomePrecisionGrid.MaxFor(this));
        output(precision > 0
          ? $"Biome precision {precision}: the ground follows the biomes on {precision + 1} x {precision + 1} cells per 64 m terrain zone ({64f / (precision + 1):0.#} m)"
          : "Biome precision 0: the ground takes its biomes from the 4 corners of each 64 m terrain zone (vanilla)");

        if (TerrainMap != null)
        {
          output($"Terrainmap file ({TerrainMap.Size}) {TerrainMap.FilePath}");
          output($"Terrain map colors {TerrainMap.SourceColors}");
        }
        else output($"Terrainmap disabled");

        output($"Forest scale {ForestScaleFactor}");
        output($"Forest amount {ForestAmount}");
        if (ForestMap != null)
        {
          output($"Forestmap file ({ForestMap.Size}) {ForestMap.FilePath}");
          output($"Forestmap multiply {ForestmapMultiply}, add {ForestmapAdd}");
          if (ForestFactorOverrideAllTrees)
            output($"Forest Factor overrides all trees");
          else
            output($"Forest Factor applies only to the same trees as vanilla");
        }
        else output($"Forestmap disabled");

        if (LocationMap != null)
        {
          output($"Location file ({LocationMap.Size}) {LocationMap.FilePath}");
          output($"Locationmap includes spawns for {LocationMap.RemainingAreas.Count} types");
        }
        else output($"Locationmap disabled");

        if (OverrideStartPosition) output($"StartPosition {StartPositionX}, {StartPositionY}");

        if (PaintMap != null)
        {
          output($"Paintmap file ({PaintMap.Size}) {PaintMap.FilePath}");
          output($"Paintmap colors {PaintMap.SourceColors}");
        }
        else output($"Paintmap disabled");

        if (LavaMap != null)
          output($"Lavamap file ({LavaMap.Size}) {LavaMap.FilePath}");
        else output($"Lavamap disabled");

        if (MossMap != null)
          output($"Mossmap file ({MossMap.Size}) {MossMap.FilePath}");
        else output($"Mossmap disabled");

        if (VegetationMap != null)
          output($"Vegetationmap file ({VegetationMap.Size}) {VegetationMap.FilePath}");
        else output($"Vegetationmap disabled");

        if (HeatMap != null)
        {
          output($"Heatmap file {HeatMap.FilePath}");
          output($"Heatmap size {HeatMap.Size}x{HeatMap.Size}");
          output($"Heatmap scale {HeatMapScale}");
        }
        else output($"Heatmap disabled");

        if (SpawnMap != null)
          output($"Spawnmap file ({SpawnMap.Size}) {SpawnMap.FilePath}");
        else output($"Spawnmap disabled");

        if (AltBiomeMap != null)
          AltBiomeMap.Dump(output);
        else if (AltBiomeMapBlockAsRead != null)
          output($"Altbiomemap unreadable by this build ({AltBiomeMapBlockAsRead.Length} bytes, kept unchanged, not planted)");
        else output($"Altbiomemap disabled");
        EffectiveAltBiomes.Dump(output, AltBiomes == null);
        DumpLayer(output);
      }
      else
      {
        output($"DISABLED");
      }
    }

    // The baked layer, when there is one (or was meant to be): what it holds and where it came from.
    private void DumpLayer(Action<string> output)
    {
      if (Layer is { } layer)
      {
        output($"Baked layer: revision {layer.Revision}, {layer.Placements:N0} records in {layer.Zones.Count:N0} zones, {layer.Palette.Count:N0} palette entries, {layer.Length:N0} bytes, made by {layer.Producer}");
        if (layer.Ground.Any)
          output($"Baked ground in {layer.Ground.Count:N0} zones");
        var sources = layer.RecordsBySource();
        output("Baked records: " + string.Join(", ", sources.Select(kv => kv.Key == 0 ? $"{kv.Value:N0} from the compiler's file" : $"{kv.Value:N0} from bake {kv.Key}")));
      }
      else if (LayerError != null)
        output($"Baked layer unreadable by this build ({LayerBytesAsRead?.Length:N0} bytes, kept unchanged, not drawn): {LayerError}");
      else if (LayerSentApart)
        output("Baked layer: sent apart, not here yet");
    }



    public static BetterContinentsSettings Load(ZPackage pkg)
    {
      var settings = new BetterContinentsSettings();
      settings.Deserialize(pkg);
      return settings;
    }

    public static BetterContinentsSettings Load(string path)
    {
      using BinaryReader binaryReader = new(File.OpenRead(path));
      int count = binaryReader.ReadInt32();
      if (count < 0 || count > binaryReader.BaseStream.Length) throw new Exception("Invalid data length");
      return Load(PackageBytes.Read(binaryReader, count));
    }

    public void Save(string path) => Save(path, false);

    // currentFormat: the settings' own version (SavedVersion) whatever Override version says (a preset is only a
    // container; the world made from it is saved in the configured format anyway).
    internal void Save(string path, bool currentFormat)
    {
      var zpackage = new ZPackage();
      Serialize(zpackage, false, true, currentFormat ? SavedVersion : null);

      // The package's own buffer, not a copy of it.
      var binaryData = PackageBytes.Buffer(zpackage, out int length);
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      using BinaryWriter binaryWriter = new(File.Create(path + ".tmp"));
      binaryWriter.Write(length);
      binaryWriter.Write(binaryData, 0, length);
      binaryWriter.Flush();
      File.Move(path + ".tmp", path);
    }

    public static BetterContinentsSettings LoadFromSource(string path, FileHelpers.FileSource fileSource)
    {
      FileReader fileReader;
      try
      {
        fileReader = new FileReader(path, fileSource);
      }
      catch
      {
        try
        {
          fileReader = new FileReader(GetLegacyBCFile(path), fileSource);
        }
        catch
        {
          Log($"Couldn't find loaded settings for this world at {path}, mod is disabled.");
          return Disabled();
        }
      }

      try
      {
        var binaryReader = (BinaryReader)fileReader;
        int count = binaryReader.ReadInt32();
        return Load(PackageBytes.Read(binaryReader, count));
      }
      catch (Exception e)
      {
        LogError($"Failed to load settings from {path}: {e.Message}");
        return Disabled();
      }
      finally
      {
        fileReader.Dispose();
      }
    }

    public void SaveToSource(string path, FileHelpers.FileSource fileSource)
    {
      var binaryData = Bytes(out int length);
      WriteToSource(path, fileSource, binaryData, length);
    }

    /// <summary>The bytes the settings are saved as: the first <paramref name="length"/> of the array. Any thread.</summary>
    internal byte[] Bytes(out int length)
    {
      var zpackage = new ZPackage();
      Serialize(zpackage, false);
      return PackageBytes.Buffer(zpackage, out length);
    }

    /// <summary>Writes the bytes of settings (Bytes) to their file.</summary>
    internal static void WriteToSource(string path, FileHelpers.FileSource fileSource, byte[] binaryData, int length)
    {
      // 1.0.15: FileWriter gained a CloudStorageFileGrouping parameter, inserted before the existing
      // FileHelperType/FileSource ones (assembly_utils.decompiled.cs:4705). Our .BetterContinents
      // sidecar isn't one of the extensions SaveSystem.IsWorldSaveExtension recognises out of the box
      // (SaveSystem.cs:149-162: .fwl/.db/.fwl2/.db2/.ok/.chunks/.chunk), so vanilla's own
      // SaveFileHelper.CreateFileForWriting auto-detection (SaveFileHelper.cs:73) would bucket it
      // under SameFileEnding instead of with its world. We pass SameFolder explicitly instead, which
      // is what World.SaveWorldFWLData uses to write the world's own .fwl (World.cs:241) and what
      // ZNet's world-save path uses for the .db2 (ZNet.cs:1846) - so this sidecar lands in the same
      // Steam Cloud bucket as the world files it configures, instead of syncing independently and
      // potentially desyncing from them.
      var fileWriter = new FileWriter(path, CloudStorageFileGrouping.SameFolder, FileHelpers.FileHelperType.Binary, fileSource);
      fileWriter.m_binary.Write(length);
      fileWriter.m_binary.Write(binaryData, 0, length);
      fileWriter.Finish();
    }

    public bool ApplyTerrainMap(float x, float z, ref Color color)
    {
      if (TerrainMap == null) return false;
      return TerrainMap.TryGetValue(Normalize(x), Normalize(z), out color);
    }

    public void ApplyPaintMap(float x, float z, Heightmap.Biome biome, ref Color mask)
    {
      var wx = Normalize(x);
      var wz = Normalize(z);
      if (PaintMap != null && PaintMap.TryGetValue(wx, wz, out var paint))
      {
        mask.r = paint.r;
        mask.g = paint.g;
        mask.b = paint.b;
        if (paint.a != 1f)
          mask.a = paint.a;
      }
        ;
      if (LavaMap != null && biome == Heightmap.Biome.AshLands)
        mask.a = LavaMap.GetValue(wx, wz);
      else if (MossMap != null && biome == Heightmap.Biome.Mistlands)
        mask.a = MossMap.GetValue(wx, wz);

    }

    private Dictionary<ZoneSystem.ZoneVegetation, Heightmap.Biome> EnabledVegetation = [];
    public void ApplyVegetationMap(Vector3 position, List<ZoneSystem.ZoneVegetation> vegetation)
    {
      if (VegetationMap == null) return;
      var entry = VegetationMap.GetEntry(Normalize(position.x), Normalize(position.z));
      if (entry == null) return;

      foreach (var v in vegetation)
      {
        if (entry.HasEnabled(v.m_prefab.name))
        {
          EnabledVegetation[v] = v.m_biome;
          v.m_biome = (Heightmap.Biome)(-1);
        }
      }
    }
    public bool CheckVegetationMap(Vector3 position, ZoneSystem.ZoneVegetation vegetation)
    {
      if (VegetationMap == null) return false;
      var entry = VegetationMap.GetEntry(Normalize(position.x), Normalize(position.z));
      if (entry == null) return false;
      return entry.HasDisabled(vegetation.m_prefab.name);
    }
    public void RevertVegetationMap()
    {
      foreach (var kvp in EnabledVegetation)
      {
        kvp.Key.m_biome = kvp.Value;
      }
      EnabledVegetation.Clear();
    }
    private Dictionary<SpawnSystem.SpawnData, Heightmap.Biome> EnabledSpawns = [];
    public void ApplySpawnMap(Vector3 position, List<SpawnSystem.SpawnData> spawners)
    {
      if (SpawnMap == null) return;
      var entry = SpawnMap.GetEntry(Normalize(position.x), Normalize(position.z));
      if (entry == null) return;

      foreach (var v in spawners)
      {
        if (entry.HasEnabled(v.m_prefab.name))
        {
          EnabledSpawns[v] = v.m_biome;
          v.m_biome = (Heightmap.Biome)(-1);
        }
        if (entry.HasDisabled(v.m_prefab.name))
        {
          EnabledSpawns[v] = v.m_biome;
          v.m_biome = 0;
        }
      }
    }
    public void RevertSpawnMap()
    {
      foreach (var kvp in EnabledSpawns)
      {
        kvp.Key.m_biome = kvp.Value;
      }
      EnabledSpawns.Clear();
    }
    // Whether the heightmap blends with the game's own terrain by its alpha (a world made since 0.10 with Heightmap Alpha:
    // HeightmapAlphaMode), and its alpha at a map position (1, opaque, where it has none).
    public bool BlendsHeightmapAlpha => HeightMap?.HasAlpha == true;
    public float HeightmapAlphaAt(float x, float y) => HeightMap?.GetAlpha(x, y) ?? 1f;

    public float ApplyHeightmap(float x, float y, float height)
    {
      if (HeightMap == null || (HeightmapBlend == 0 && HeightmapAdd == 0 && HeightmapMask == 0))
      {
        return height;
      }

      float h = HeightMap.GetValue(x, y);
      float blendedHeight = Mathf.Lerp(height, h * HeightmapAmount, HeightmapBlend);
      return Mathf.Lerp(blendedHeight, blendedHeight * h, HeightmapMask) + h * HeightmapAdd;
    }

    public float ApplyRoughmap(float x, float y, float smoothHeight, float roughHeight)
    {
      if (RoughMap == null)
        return roughHeight;

      float r = RoughMap.GetValue(x, y);
      return Mathf.Lerp(smoothHeight, roughHeight, r * RoughmapBlend);
    }

    public float ApplyFlatmap(float x, float y, float flatHeight, float height)
    {
      if (Settings.ShouldHeightMapOverrideAll)
      {
        return flatHeight;
      }
      var image = UseRoughInvertedAsFlat ? RoughMap : FlatMap;
      if (image == null)
        return height;

      float f = UseRoughInvertedAsFlat ? 1 - image.GetValue(x, y) : image.GetValue(x, y);
      return Mathf.Lerp(height, flatHeight, f * FlatmapBlend);
    }
    public float ApplyHeatmap(float x, float y) => HeatMapScale * (HeatMap?.GetValue(x, y) ?? 0);

    public float ApplyForest(float x, float y, float forest)
    {
      float finalValue = forest;
      if (ForestMap != null)
      {
        // Map forest from weird vanilla range to 0 - 1
        float normalizedForestValue = Mathf.InverseLerp(1.850145f, 0.145071f, forest);
        float fmap = ForestMap.GetValue(x, y);
        float calculatedValue = Mathf.Lerp(normalizedForestValue, normalizedForestValue * fmap, ForestmapMultiply) + fmap * ForestmapAdd;
        // Map back to weird values
        finalValue = Mathf.Lerp(1.850145f, 0.145071f, calculatedValue);
      }

      // Clamp between the known good values (that vanilla generates)
      finalValue = Mathf.Clamp(finalValue + ForestAmountOffset, 0.145071f, 1.850145f);
      return finalValue;
    }

    public Heightmap.Biome GetBiomeOverride(float mapX, float mapY) => BiomeMap?.GetValue(mapX, mapY) ?? 0;

    public IEnumerable<Vector2> GetAllSpawns(string spawn) => LocationMap?.GetAllSpawns(spawn) ?? [];

    // bc reload bm: reads the picture and legend again into a new map, which replaces the world's only if both read
    // cleanly (before 0.9.3 a legend error reloaded the picture with the default colours, and the next save kept it).
    public void ReloadBiomeMap()
    {
      if (BiomeMap == null) return;
      var path = BiomeMap.FilePath;
      // The config's path only when the map's own file is gone.
      if (!File.Exists(path) && File.Exists(MapKind.Biome.ConfigPath))
      {
        LogWarning($"Cannot find image {path}: Using default path from config.");
        path = MapKind.Biome.ConfigPath;
      }
      var map = ImageMapBiome.Create(path, CompactMaps);
      if (UsableReload(map, path))
        BiomeMap = map;
    }

    // Re-reads the image and its legend into a new map object. Planted sectors follow when the sectors are rebuilt,
    // which "bc reload ab" does straight away.
    public void ReloadAltBiomeMap()
    {
      if (AltBiomeMap == null) return;
      var path = AltBiomeMap.FilePath;
      if (!File.Exists(path))
      {
        var configPath = MapKind.AltBiome.ConfigPath;
        if (!File.Exists(configPath)) return;
        LogWarning($"Cannot find image {path}: Using default path from config.");
        path = configPath;
      }
      var reloaded = ImageMapAltBiome.Create(path);
      if (reloaded != null)
      {
        AltBiomeMap = reloaded;
        AltBiomeMapBlockAsRead = null;
        AltBiomeMapError = null;
      }
    }

    public void LoadPrefabs(ZNetScene scene)
    {
      if (SpawnMap != null)
        SpawnMap.LoadPrefabs(scene);
      if (VegetationMap != null)
        VegetationMap.LoadPrefabs(scene);
    }
  }
}

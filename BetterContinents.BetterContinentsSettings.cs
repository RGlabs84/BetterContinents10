// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-02 for export folders used as the Directory (0.9.4), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
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

    public static BetterContinentsSettings Create()
    {
      var settings = new BetterContinentsSettings();
      settings.InitSettings(ConfigEnabled.Value);
      return settings;
    }

    // World import (WorldImport): the settings "From Config" would give a new world if the config held these values
    // (an export folder's export.cfg over a snapshot of the config). Unlike Create it runs on any thread: it reads no
    // live config value and changes no Harmony patch (Create's DynamicPatch follows the loaded world's Settings, which
    // must not move). lean drops the decoded pixels of every map a preset stores as its file bytes, which halves the
    // memory an import holds; such settings are only good for Save.
    internal static BetterContinentsSettings CreateForImport(ConfigValues values, bool lean)
    {
      var settings = new BetterContinentsSettings { EnabledForThisWorld = true };
      settings.ReadConfig(values, lean);
      return settings;
    }

    public static BetterContinentsSettings Disabled()
    {
      var settings = new BetterContinentsSettings();
      settings.InitSettings(false);
      return settings;
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

    private void InitSettings(bool enabled)
    {
      Log($"Init settings for new world");

      EnabledForThisWorld = enabled;

      // A Directory that holds an export.cfg (a world export) brings the settings its maps were encoded for.
      if (EnabledForThisWorld)
        ReadConfig(WorldImport.DirectoryValues() ?? ConfigValues.Live, false);
      DynamicPatch();
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
      foreach (var kind in MapKind.LoadOrder)
        kind.Load(this, c, dir, lean);
      AltBiomes = AltBiomeSettings.FromConfig(c);
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
      var map = ImageMapBiome.Create(path);
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

      if (EnabledForThisWorld)
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
        output($"Fix water color {FixWaterColor}");

        output($"Map edge dropoff {MapEdgeDropoff}");
        output($"Mountains allowed at center {MountainsAllowedAtCenter}");

        if (HeightMap != null)
        {
          output($"Heightmap file ({HeightMap.Size}) {HeightMap.FilePath}");
          output($"Heightmap amount {HeightmapAmount}, blend {HeightmapBlend}, add {HeightmapAdd}, mask {HeightmapMask}");
          if (HeightmapOverrideAll)
          {
            output($"Heightmap overrides ALL");
          }
        }
        else output($"Heightmap disabled");

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
        var precision = Mathf.Clamp(BiomePrecision, 0, BiomePrecisionGrid.MaxPrecision);
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
      }
      else
      {
        output($"DISABLED");
      }
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
      return Load(new ZPackage(binaryReader.ReadBytes(count)));
    }

    public void Save(string path) => Save(path, false);

    // currentFormat: the newest settings format whatever Override version says (a preset is only a container; the
    // world made from it is saved in the configured format anyway).
    internal void Save(string path, bool currentFormat)
    {
      var zpackage = new ZPackage();
      Serialize(zpackage, false, true, currentFormat ? MaxVersion : null);

      byte[] binaryData = zpackage.GetArray();
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      using BinaryWriter binaryWriter = new(File.Create(path + ".tmp"));
      binaryWriter.Write(binaryData.Length);
      binaryWriter.Write(binaryData);
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
        return Load(new ZPackage(binaryReader.ReadBytes(count)));
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
      var zpackage = new ZPackage();
      Serialize(zpackage, false);

      byte[] binaryData = zpackage.GetArray();
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
      fileWriter.m_binary.Write(binaryData.Length);
      fileWriter.m_binary.Write(binaryData);
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
      var map = ImageMapBiome.Create(path);
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

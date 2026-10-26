// Added by Wubarrk on 2026-10-26 for the BetterWorlds-complete export (0.10.7).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// What a folder needs so that BetterWorlds (bw_import, its Studio's Import project, tools/betterworlds.py --migrate) builds the same world from it as
// Better Continents does, without guessing: the Deep North's cold map, the location exclusion mask, world.json and the settings version in the manifest,
// the noise pins in export.cfg, and a few informational manifest fields. All of it is additive: no file, key or name that an export had is gone or
// different, and Format stays "bc-export/1". The passes are here, with a hook each in WorldExport.cs.
public static partial class WorldExport
{
  /// <summary>manifest.json "exportRevision": 1 was every export before the BetterWorlds-complete one (the key was absent), 2 has coldmap.png, the
  /// exclusion mask, world.json and settingsVersion. A reader that wants one of them tests this, not the format.</summary>
  internal const int ExportRevision = 2;

  /// <summary>The largest map file BetterWorlds takes (ProjectImporter.MaxMapFileBytes).</summary>
  internal const long BetterWorldsMapLimit = 256L * 1024 * 1024;

  private sealed partial class Job
  {
    // ---- world.json and settingsVersion -----------------------------------------------------------------------

    // The settings version of a world made from this folder: WorldImport.ExportVersion's rule, the newest when the maps span what World Size
    // and Edge Size give, else 11 (a resized 0.9.x world's export, spanning 21000 m). BetterWorlds reads the same number from the manifest.
    private int ExportSettingsVersion =>
      Total == new WorldGeometry(WorldSizeSetting, EdgeSizeSetting).TotalSize ? BetterContinentsSettings.UnifiedVersion : BetterContinentsSettings.KeyedVersion;

    private string ExportLayout => ExportSettingsVersion >= BetterContinentsSettings.UnifiedVersion ? "scaled" : "vanilla";

    // world.json as BetterWorlds writes it (WorldGeometry.ToJson): the radius, the edge and the layout, one line. No Sectors member: the new
    // world picks its own sectors from its size.
    private List<string> WorldJsonLines() =>
    [
      "{ \"WorldRadius\": " + F(WorldSizeSetting) + ", \"EdgeSize\": " + F(EdgeSizeSetting) + ", \"Layout\": \"" + ExportLayout + "\" }",
    ];

    // ---- the cold map ----------------------------------------------------------------------------------------

    // A world with a cold map of its own that is in use: Coldmap Scale above 0 (at 0 the Deep North follows the biome map).
    private bool OwnColdActive => BcWorld && Settings.HasColdMap && Settings.ColdMapScale > 0f;

    // The world's own cold map goes into the folder as it is (byte for byte, with its own Coldmap Scale), rather than a sampled one.
    private bool ColdCarried => O.Cold && O.Sources && OwnColdActive;

    private bool ColdWritten, ColdCopied;
    private long ColdPixels;
    private float ColdScaleWritten;

    // The maps CarryForward also puts at the top of the folder (WorldExport.cs): the three nothing else holds, the location exclusion mask, and
    // the world's own cold map (instead of the sampled one).
    private bool CarriesForward(string file) =>
      CarriedForward.Contains(file) || file == "no_spawns_mask.png" || (file == "coldmap.png" && ColdCarried);

    // Worker thread, at the end of CarryForward.
    private void AfterCarryForward(List<string> copied)
    {
      if (!copied.Contains("coldmap.png"))
        return;
      ColdScaleWritten = Settings.ColdMapScale;
      ColdCopied = true;
    }

    // Where the Deep North is, as BetterWorlds' cold map and Better Continents' Coldmap Scale 10: value = fade / 10, with the game's own wave fade
    // (a ramp over the 200 m at the border, which the calm sea of the Deep North follows) inside and black outside. Sampled through the game's
    // IsDeepnorth and DeepNorthWaveFade, which Better Continents' patches have already pointed at the world's own cold map or biome map. Not
    // sampled when the world's own cold map was copied (SourcesPass ran before).
    private IEnumerable ColdPass()
    {
      if (ColdCopied)
        yield break;
      int n = Size;
      long north = 0;
      float scale = WorldExportMath.ColdExportScale;
      var output = Open("coldmap.png", 1, 16);
      foreach (var step in Stream("Sampling cold", W(0.2) + W(0.3), n, [output], (row, buffers, r) =>
               {
                 var buffer = buffers[0];
                 long rowNorth = 0;
                 float wz = WorldExportMath.FileRowToWorldZ(row, n, Total);
                 for (int col = 0; col < n; col++)
                 {
                   float wx = WorldExportMath.PixelToWorld(col, n, Total);
                   bool deepNorth = WorldGenerator.IsDeepnorth(wx, wz);
                   var value = WorldExportMath.ColdToValue(deepNorth, deepNorth ? WorldGenerator.DeepNorthWaveFade(wx, wz) : 0.0, scale);
                   var p = WorldExportMath.ValueToUShort(value, out _);
                   Put16(buffer, 2 * (r * n + col), p);
                   if (p > 0) rowNorth++;
                 }
                 Interlocked.Add(ref north, rowNorth);
               }))
        yield return step;
      ColdPixels = north;
      ColdScaleWritten = scale;
      Complete(output);
      ColdWritten = true;
    }

    // export.cfg, after the heat: the scale the cold map is read back with.
    private void ColdConfigKey(Action<SettingDef, string, string> key)
    {
      if (ColdWritten || ColdCopied)
        key(SettingsSchema.ColdmapScale, F(ColdScaleWritten), "cold = pixel / 65535 * scale; Deep North wherever it is above 0.");
    }

    private void ColdSummary(List<string> lines)
    {
      if (ColdCopied)
        lines.Add("cold: the world's own cold map was copied (Coldmap Scale " + Inv(ColdScaleWritten) + ").");
      else if (ColdWritten)
        lines.Add($"cold: the Deep North is {ColdPixels:N0} pixel(s).");
    }

    // ---- export.cfg --------------------------------------------------------------------------------------------

    // The heights hold the mountain and Mistlands noise already (GetBiomeHeight adds it at the corners): a world made from them must not add it
    // a second time, whatever the player's own BetterContinents.cfg says.
    private void NoisePinKeys(Action<SettingDef, string, string> key)
    {
      key(SettingsSchema.MountainNoise, "0", "The loaded world's mountain jaggedness is already in heightmap.png; no second layer of it.");
      key(SettingsSchema.MistlandsNoise, "0", "The loaded world's Mistlands crags are already in heightmap.png; no second layer of them.");
    }

    // The loaded world's two water switches, so a rebuilt world floats and colours its seas the same way.
    private void WaterKeys(Action<SettingDef, string, string> key)
    {
      if (!BcWorld)
        return;
      key(SettingsSchema.AshlandsWater, B(Settings.AshlandsWater), "As the loaded world.");
      key(SettingsSchema.DeepNorthWater, B(Settings.DeepNorthWater), "As the loaded world.");
    }

    // High Terrain, when the new world would not get the loaded world's state from Auto: the mode the world chose, or the state it has because
    // of its own Heightmap Amount while the export is encoded for another (Auto goes by the amount, above 5). null: write nothing.
    private string? HighTerrainToWrite()
    {
      if (!Settings.ShapesWorld)
        return null;
      if (Settings.HighTerrainMode != HighTerrainMode.Auto)
        return Settings.HighTerrainMode.ToString();
      bool wanted = HighTerrain.Wanted(Settings);
      bool auto = HeightsWritten && O.HeightmapAmount > HighTerrain.OldMaxAmount;
      return wanted == auto ? null : wanted ? nameof(HighTerrainMode.On) : nameof(HighTerrainMode.Off);
    }

    // ---- manifest.json -----------------------------------------------------------------------------------------

    // Before TextPass writes the manifest: BetterWorlds' settingsVersion, the revision, the size and the layout, the cold map, and the sizes of
    // the files. Every key is new; none is changed.
    private void AddInteropManifest(WorldExportJson.Obj m)
    {
      void After(string key, string name, object? value)
      {
        int at = m.FindIndex(kv => kv.Key == key) + 1;
        m.Insert(at, new KeyValuePair<string, object?>(name, value));
      }
      After("exportedAt", "settingsVersion", ExportSettingsVersion);
      After("settingsVersion", "exportRevision", ExportRevision);
      After("totalRadius", "worldSize", WorldSizeSetting);
      After("worldSize", "edgeSize", EdgeSizeSetting);
      After("edgeSize", "layout", ExportLayout);
      After("layout", "wideSectors", BcWorld && Settings.WideSectors);
      After("wideSectors", "highTerrain", BcWorld ? (int)Settings.HighTerrainMode : 0);
      if (ColdWritten || ColdCopied)
        After("heat", "cold", new WorldExportJson.Obj
        {
          { "scale", ColdScaleWritten },
          { "deepNorthPixels", ColdCopied ? null : ColdPixels },
          { "source", ColdCopied ? "the world's own map" : "sampled" },
        });
      else
        After("heat", "cold", null);
      // The location map's pins sit on the i / size grid, which BetterWorlds calls Corner.
      var locations = m.FirstOrDefault(kv => kv.Key == "locations").Value as WorldExportJson.Obj;
      locations?.Add("pinGrid", "Corner");
      var bytes = new WorldExportJson.Obj();
      foreach (var name in Written)
        if (!name.Contains('/') && TryLength(name) is { } length)
          bytes.Add(name, length);
      After("files", "fileBytes", bytes);
    }

    private long? TryLength(string name)
    {
      try
      {
        var info = new FileInfo(Path.Combine(Dir, name));
        return info.Exists ? info.Length : null;
      }
      catch (IOException)
      {
        return null;
      }
    }

    // A map file over what BetterWorlds takes is skipped there; say so while the export can still be redone smaller.
    private void NoteLargeFiles()
    {
      foreach (var name in Written.Where(x => x.EndsWith(".png", StringComparison.Ordinal) && !x.Contains('/')).ToList())
        if (TryLength(name) is { } length && length > BetterWorldsMapLimit)
          Notes.Add($"{name} is {length / (1024 * 1024)} MiB. BetterWorlds takes at most {BetterWorldsMapLimit / (1024 * 1024)} MiB per map file and skips a larger one: export at a smaller size for it.");
    }
  }
}

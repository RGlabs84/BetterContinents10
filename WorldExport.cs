// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using static BetterContinents.BetterContinents;
using ISConfiguration = SixLabors.ImageSharp.Configuration;
using ISImage = SixLabors.ImageSharp.Image;

namespace BetterContinents;

// The arithmetic of a world export, free of the game (UnityEngine.Mathf only) so the offline harness can check it.
// Every formula is Better Continents' own import path run backwards: what a pixel must hold so that loading the
// export rebuilds the value that was sampled.
public static class WorldExportMath
{
  /// <summary>Metres per unit of base height: WorldGenerator.GetHeightMultiplier() = 200.</summary>
  public const float HeightScale = 200f;

  /// <summary>What GetBaseHeightV3 subtracts: finalHeight = v * amount - 0.15 + seaLevelAdjustment.</summary>
  public const float BaseOffset = 0.15f;

  /// <summary>Valheim's water plane (ZoneSystem.m_waterLevel): 30 m, i.e. finalHeight 0.15.</summary>
  public const float WaterLevel = 30f;

  /// <summary>ApplyForest's sparsest forest factor (normalised 0).</summary>
  public const float ForestSparse = 1.850145f;

  /// <summary>ApplyForest's densest forest factor (normalised 1).</summary>
  public const float ForestDense = 0.145071f;

  /// <summary>What GetAshlandsOceanGradientPrefix subtracts, so zero heat reads as (barely) cold water.</summary>
  public const float HeatBias = 0.00001f;

  /// <summary>World coordinate of pixel <paramref name="index"/> on one axis: w = (index / (size - 1) - 0.5) * totalSize.
  /// Column 0 is the west edge; on the z axis the index is the map row (0 = south, see <see cref="FileRowToWorldZ"/>).
  /// This is ImageMapFloat.GetValue's grid, so a re-import is exact at these points and bilinear between them.</summary>
  public static float PixelToWorld(int index, int size, float totalSize) => ((float)index / (size - 1) - 0.5f) * totalSize;

  /// <summary>Nearest pixel to a world coordinate: round((w / totalSize + 0.5) * (size - 1)), clamped to 0..size - 1.</summary>
  public static int WorldToPixel(float world, int size, float totalSize) =>
    Mathf.Clamp(Mathf.RoundToInt((world / totalSize + 0.5f) * (size - 1)), 0, size - 1);

  /// <summary>File row of a map row and back: size - 1 - row. Better Continents flips every image vertically when it
  /// loads it, so file row 0 (the top of the PNG) is the north edge (+z) and map row 0 the south edge.</summary>
  public static int FlipRow(int row, int size) => size - 1 - row;

  /// <summary>World z of file row <paramref name="fileRow"/> (row 0 = north): ((size - 1 - fileRow) / (size - 1) - 0.5) * totalSize.</summary>
  public static float FileRowToWorldZ(int fileRow, int size, float totalSize) => PixelToWorld(FlipRow(fileRow, size), size, totalSize);

  /// <summary>File row nearest to world z (row 0 = north): size - 1 - WorldToPixel(z).</summary>
  public static int WorldZToFileRow(float z, int size, float totalSize) => FlipRow(WorldToPixel(z, size, totalSize), size);

  /// <summary>Where the location map puts a one-pixel location at index i: w = (i / size - 0.5) * totalSize.
  /// ImageMapLocation divides by size, not size - 1 like every other map, so its grid is up to a pixel off theirs.</summary>
  public static float LocationPixelToWorld(int index, int size, float totalSize) => ((float)index / size - 0.5f) * totalSize;

  /// <summary>Location-map pixel whose import position is nearest w: round((w / totalSize + 0.5) * size), clamped.</summary>
  public static int WorldToLocationPixel(float world, int size, float totalSize) =>
    Mathf.Clamp(Mathf.RoundToInt((world / totalSize + 0.5f) * size), 0, size - 1);

  /// <summary>Whether a world coordinate lies on the map square at all: |w| &lt;= totalSize / 2.</summary>
  public static bool OnMap(float world, float totalSize) => Mathf.Abs(world) <= totalSize * 0.5f;

  /// <summary>The terrain zone a coordinate lies in: floor((w + 32) / 64). Zones are 64 m, centred on multiples of 64.</summary>
  public static int ZoneIndex(float world) => Mathf.FloorToInt((world + 32f) / 64f);

  /// <summary>BetterContinentsSettings.SeaLevel's setter: seaLevelAdjustment = Lerp(1, -1, seaLevel); 0.5 gives 0.</summary>
  public static float SeaLevelAdjustment(float seaLevel) => Mathf.Lerp(1f, -1f, seaLevel);

  /// <summary>Heightmap value for a final height in metres, unclamped: v = (metres / 200 + 0.15 - Lerp(1, -1, seaLevel)) / amount.
  /// The inverse of GetBaseHeightV3 with Heightmap Blend 1, Add 0, Mask 0 and no noise layers.</summary>
  public static float MetresToValue(float metres, float amount, float seaLevel) =>
    (metres / HeightScale + BaseOffset - SeaLevelAdjustment(seaLevel)) / amount;

  /// <summary>Final height in metres Better Continents builds from value v: (v * amount - 0.15 + Lerp(1, -1, seaLevel)) * 200.</summary>
  public static float ValueToMetres(float value, float amount, float seaLevel) =>
    (value * amount - BaseOffset + SeaLevelAdjustment(seaLevel)) * HeightScale;

  /// <summary>The value at the water plane: MetresToValue(30) = (0.30 - Lerp(1, -1, seaLevel)) / amount; 0.15 at amount 2, sea level 0.5.</summary>
  public static float WaterlineValue(float amount, float seaLevel) => MetresToValue(WaterLevel, amount, seaLevel);

  /// <summary>16-bit pixel for value v: round(clamp01(v) * 65535). clip is -1 below 0 (and for NaN), +1 above 1, else 0.</summary>
  public static ushort ValueToUShort(float value, out int clip)
  {
    if (!(value >= 0f))
    {
      clip = -1;
      return 0;
    }
    if (value > 1f)
    {
      clip = 1;
      return ushort.MaxValue;
    }
    clip = 0;
    return (ushort)(value * 65535f + 0.5f);
  }

  /// <summary>The value a 16-bit pixel decodes to (ImageMapFloat reads L16 as p / 65535).</summary>
  public static float UShortToValue(ushort pixel) => pixel / 65535f;

  /// <summary>8-bit channel for value v: round(clamp01(v) * 255). The game keeps its terrain mask in 8 bits
  /// (Heightmap.m_paintMask is an RGBA32 texture), so lava, moss and paint lose nothing at this depth.</summary>
  public static byte ValueToByte(float value) => !(value > 0f) ? (byte)0 : value >= 1f ? (byte)255 : (byte)(value * 255f + 0.5f);

  /// <summary>Utils.LerpStep: clamp01((v - l) / (h - l)).</summary>
  public static float LerpStep(float l, float h, float v) => Mathf.Clamp01((v - l) / (h - l));

  /// <summary>GetBaseHeightV3's edge drop-off: past worldRadius, h = Lerp(h, -0.2, LerpStep(worldRadius, totalRadius, d)),
  /// then within 10 m of totalRadius h = Lerp(h, -2, LerpStep(totalRadius - 10, totalRadius, d)).</summary>
  public static float ApplyEdgeDropoff(float finalHeight, float distance, float worldRadius, float totalRadius)
  {
    if (distance <= worldRadius)
      return finalHeight;
    finalHeight = Mathf.Lerp(finalHeight, -0.2f, LerpStep(worldRadius, totalRadius, distance));
    var edge = totalRadius - 10f;
    if (distance > edge)
      finalHeight = Mathf.Lerp(finalHeight, -2f, LerpStep(edge, totalRadius, distance));
    return finalHeight;
  }

  /// <summary>Inverse of <see cref="ApplyEdgeDropoff"/>: the height that comes out as <paramref name="finalHeight"/> after
  /// the drop-off. With t = LerpStep(worldRadius, totalRadius, d): before = (h + 0.2 t) / (1 - t), after undoing the last
  /// 10 m first: h = (h + 2 t2) / (1 - t2). False at or past totalRadius, where the drop-off reaches -2 whatever the input.</summary>
  public static bool TryUndoEdgeDropoff(float finalHeight, float distance, float worldRadius, float totalRadius, out float before)
  {
    before = finalHeight;
    if (distance <= worldRadius)
      return true;
    var h = finalHeight;
    var edge = totalRadius - 10f;
    if (distance > edge)
    {
      var t2 = LerpStep(edge, totalRadius, distance);
      if (!(t2 < 1f))
        return false;
      h = (h + 2f * t2) / (1f - t2);
    }
    var t = LerpStep(worldRadius, totalRadius, distance);
    if (!(t < 1f))
      return false;
    before = (h + 0.2f * t) / (1f - t);
    return true;
  }

  /// <summary>ApplyForest's normalisation: n = InverseLerp(1.850145, 0.145071, factor), 0 = bare, 1 = densest (clamped).</summary>
  public static float ForestNormalized(float forestFactor) => Mathf.InverseLerp(ForestSparse, ForestDense, forestFactor);

  /// <summary>Forestmap value for Forestmap Multiply 0 / Add 1, where ApplyForest adds the map to the game's own forest:
  /// fmap = n(target) - n(vanilla), unclamped. Below 0 the world has less forest than the game makes there, which this
  /// encoding cannot store.</summary>
  public static float ForestMapAdditive(float targetForest, float vanillaForest) =>
    ForestNormalized(targetForest) - ForestNormalized(vanillaForest);

  /// <summary>Forestmap value for Forestmap Multiply 1 / Add 1, where ApplyForest computes c = fmap * (n + 1):
  /// fmap = n(target) / (n(vanilla) + 1), always within 0..1, so every target is reachable.</summary>
  public static float ForestMapExact(float targetForest, float vanillaForest) =>
    ForestNormalized(targetForest) / (ForestNormalized(vanillaForest) + 1f);

  /// <summary>BetterContinentsSettings.ApplyForest for a stored forestmap value: n = normalised vanilla factor,
  /// c = Lerp(n, n * fmap, multiply) + fmap * add, result = clamp(Lerp(1.850145, 0.145071, c) + amountOffset).</summary>
  public static float ApplyForest(float vanillaForest, float fmap, float multiply, float add, float amountOffset)
  {
    var n = ForestNormalized(vanillaForest);
    var c = Mathf.Lerp(n, n * fmap, multiply) + fmap * add;
    return Mathf.Clamp(Mathf.Lerp(ForestSparse, ForestDense, c) + amountOffset, ForestDense, ForestSparse);
  }

  /// <summary>Heatmap value for an Ashlands ocean gradient g read back with Heatmap Scale s: v = g / s, unclamped (stored
  /// clamped to 0..1). Better Continents reads s * v - 0.00001 back as the gradient and s * v &gt; 0 as IsAshlands.</summary>
  public static float HeatToValue(float gradient, float heatScale) => heatScale > 0f ? gradient / heatScale : 0f;

  /// <summary>The Ashlands ocean gradient Better Continents reads back from heat value v: s * v - 0.00001.</summary>
  public static float ValueToHeat(float value, float heatScale) => heatScale * value - HeatBias;
}

// Dumps the LOADED world into a Better Continents map set that rebuilds it, plus the config lines that load it
// (export.cfg), a README.txt, a manifest.json and a New World preset (WorldImport). A coroutine on
// BetterContinents.instance drives it; the sampling runs on worker threads and the PNG encoding on a task, so the main
// thread only ever polls. The static surface is what a HUD needs (IsRunning, Phase, Progress, Overall, LastExportDir,
// LastSummary, LastError, Start, Cancel).
//
// Everything is written under <save data>/BetterContinents/<world>/, never inside worlds_local/<world>/: a file in
// a world's save folder can be taken for a save (see BetterContinents.SaveSystemPatch.cs).
public static class WorldExport
{
  public const int MinSize = 128;
  public const int MaxSize = 8192;
  public const string Format = "bc-export/1";

  public sealed class Options
  {
    /// <summary>Pixels per side of every image (1024, 2048, 4096 or 8192 in the HUD). 2048 and up keep the alt-biome map
    /// exact on the game's 12 m grid; 8192 holds about half a gigabyte while it runs, and every client that joins a
    /// world built from it downloads the maps.</summary>
    public int Size = 4096;
    /// <summary>The Heightmap Amount the heights are encoded for (0-5). 2 with Sea Level 0.5 spans -30 m to 370 m, the
    /// encoding Wubarrk's own generated maps use, so the two can be cut and pasted into each other.</summary>
    public float HeightmapAmount = 2f;
    /// <summary>The Sea Level Adjustment (0-1) the heights are encoded for.</summary>
    public float SeaLevel = 0.5f;
    /// <summary>heightmap.png: the terrain height, 16-bit.</summary>
    public bool Heightmap = true;
    /// <summary>biomemap.png + biomemap.txt: the biome, in the default biome colours.</summary>
    public bool Biomes = true;
    /// <summary>locationmap.png + locationmap.txt: one pixel per location. Complete only where the world runs (host or
    /// dedicated server); a client knows only the locations the server shows on the map.</summary>
    public bool Locations = true;
    /// <summary>forestmap.png: the forest factor (see ForestExact).</summary>
    public bool Forest = true;
    /// <summary>heatmap.png: the Ashlands heat (ocean gradient), read back with Heatmap Scale = HeatScale.</summary>
    public bool Heat = true;
    /// <summary>altbiomemap.png + altbiomemap.txt: the alt biomes exactly as placed, for Mode = PlantedOnly.</summary>
    public bool AltBiomes = true;
    /// <summary>lavamap.png: the Ashlands lava mask, so lava stays with the terrain whatever the new world's seed.</summary>
    public bool Lava = true;
    /// <summary>mossmap.png: the Mistlands ground mask.</summary>
    public bool Moss = true;
    /// <summary>paintmap.png: the colour channels of the terrain mask (Deep North snow depth and the like). On by default
    /// since 0.9.0, so nothing is left to the seed: it costs 3 bytes a pixel on disk and in every world built from it (which
    /// every joining player downloads), and it pins the paint of every biome, also where you later edit the heights. A world
    /// made with the same seed and unedited heights paints the ground identically without it.</summary>
    public bool Paint = true;
    /// <summary>sources/: a Better Continents world's own maps, as its settings store them. Its terrain, vegetation and
    /// spawn maps, which no other map holds, are also copied into the folder itself, so the rebuilt world keeps them.</summary>
    public bool Sources = true;
    /// <summary>After the maps: the New World preset "&lt;world&gt; &lt;time&gt;" is built from the folder (WorldImport), so the
    /// export is a choice in the New World screen at once. It is not selected there; bc_import selects one. Off by
    /// default on a dedicated server, which has no New World screen ("preset" asks for it anyway).</summary>
    public bool Preset = true;
    /// <summary>Heights as the game builds terrain: every 64 m zone blends the height formulas of its four corner biomes
    /// (HeightmapBuilder). Off samples one biome per point, which differs from the real ground near biome borders.</summary>
    public bool ZoneBlend = true;
    /// <summary>Whether the rebuilt world keeps Better Continents' map-edge drop-off. null (default): as the loaded world
    /// (a vanilla world has it). true: past World Size the pixels hold the height before the drop-off, which Better
    /// Continents then rebuilds, and the world edge (ship push, edge kill, edge water) stays. false: the edge is written as
    /// it is and Map Edge Drop-off = false, which also removes the world edge altogether.</summary>
    public bool? EdgeDropoff;
    /// <summary>false (default): Forestmap Multiply 0 / Add 1, the additive convention hand-made maps use; exact wherever
    /// the world has at least the game's own forest, clipped where it has less. true: Multiply 1 / Add 1, exact everywhere
    /// but awkward to paint by hand.</summary>
    public bool ForestExact;
    /// <summary>The Heatmap Scale the heat is encoded for (above 0, at most 100): value = gradient / scale. 10 (Better
    /// Continents' default) covers the whole vanilla gradient, which reaches about 8.3 at the southern rim.</summary>
    public float HeatScale = 10f;

    /// <summary>The built-in defaults, with [09 BetterContinents.Export] Default Size, Default Heightmap Amount and
    /// Default Sea Level applied (LiveConfig; the built-in values where the config is not bound), and no preset on a
    /// dedicated server.</summary>
    public static Options Default()
    {
      var o = LiveConfig.ApplyDefaults(new Options());
      if (OnDedicatedServer())
        o.Preset = false;
      return o;
    }

    public Options Clone() => (Options)MemberwiseClone();

    internal bool NeedsTerrain => Heightmap || Lava || Moss || Paint;

    internal string? Validate()
    {
      if (Size < MinSize || Size > MaxSize)
        return $"the size must be {MinSize} to {MaxSize} pixels (got {Size})";
      if (!(HeightmapAmount > 0f && HeightmapAmount <= 5f))
        return $"the heightmap amount must be above 0 and at most 5, the config's range (got {Inv(HeightmapAmount)})";
      if (!(SeaLevel >= 0f && SeaLevel <= 1f))
        return $"the sea level must be 0 to 1 (got {Inv(SeaLevel)})";
      if (!(HeatScale > 0f && HeatScale <= 100f))
        return $"the heat scale must be above 0 and at most 100 (got {Inv(HeatScale)})";
      if (!(NeedsTerrain || Biomes || Locations || Forest || Heat || AltBiomes || Sources))
        return "every map is switched off";
      return null;
    }

    public override string ToString()
    {
      var maps = new List<string>();
      if (Heightmap) maps.Add("heights");
      if (Biomes) maps.Add("biomes");
      if (Locations) maps.Add("locations");
      if (Forest) maps.Add("forest");
      if (Heat) maps.Add("heat");
      if (AltBiomes) maps.Add("alt biomes");
      if (Lava) maps.Add("lava");
      if (Moss) maps.Add("moss");
      if (Paint) maps.Add("paint");
      if (Sources) maps.Add("sources");
      var edge = EdgeDropoff == null ? "as the world" : EdgeDropoff.Value ? "on" : "off";
      return $"{Size} px, amount {Inv(HeightmapAmount)}, sea level {Inv(SeaLevel)}, {string.Join(", ", maps)}; "
             + $"{(ZoneBlend ? "zone blend" : "per-point heights")}, edge drop-off {edge}, forest {(ForestExact ? "exact" : "additive")}, heat scale {Inv(HeatScale)}"
             + (Preset ? "; New World preset" : "; no preset");
    }
  }

  /// <summary>An export is running (sampling, writing or cleaning up after a cancel).</summary>
  public static bool IsRunning { get; private set; }
  /// <summary>What the export is doing: "Sampling heights", "Writing heightmap.png", ... then "Done", "Cancelled" or
  /// "Failed"; "Idle" before the first export.</summary>
  public static string Phase { get; private set; } = "Idle";
  /// <summary>0..100 of the current phase. Writing phases are estimated from the encoder's speed so far.</summary>
  public static float Progress { get; private set; }
  /// <summary>0..100 of the whole export.</summary>
  public static float Overall { get; private set; }
  /// <summary>The folder of the last export that finished.</summary>
  public static string? LastExportDir { get; private set; }
  /// <summary>What the last export that finished came to, a line each (heights, locations, alt biomes, the preset), for the
  /// HUD.</summary>
  public static IReadOnlyList<string> LastSummary { get; private set; } = [];
  /// <summary>The folder the running export writes; null when none runs.</summary>
  public static string? RunningDir { get; private set; }
  /// <summary>Why the last export failed or could not start; null after a success.</summary>
  public static string? LastError { get; private set; }
  /// <summary>Gate for exports (the HUD and bc_export check it through CanExport). LiveConfig replaces it with Allow
  /// Export, where the server's value wins on a client; it must be cheap and main-thread safe.</summary>
  public static Func<bool> Allowed = () => true;
  /// <summary>Why Allowed() said no, for CanExport's reason; null or a null result gives a generic one.</summary>
  public static Func<string?>? NotAllowedReason;

  private static volatile bool cancelRequested;
  // The work item the running export waits on, so a cancel or a failure can let it finish before deleting files.
  private static Task? pending;

  /// <summary>Whether an export can start now: a world is loaded, nothing is running, and Allowed() says yes.</summary>
  public static bool CanExport(out string reason)
  {
    reason = "";
    if (IsRunning)
    {
      reason = "an export is already running";
      return false;
    }
    bool allowed;
    try
    {
      allowed = Allowed == null || Allowed();
    }
    catch (Exception e)
    {
      LogWarning($"World export: the Allowed check threw ({e.Message}); treating it as not allowed.");
      allowed = false;
    }
    if (!allowed)
    {
      string? why = null;
      try
      {
        why = NotAllowedReason?.Invoke();
      }
      catch (Exception e)
      {
        LogWarning($"World export: the NotAllowedReason check threw ({e.Message}).");
      }
      reason = why ?? "world export is not allowed here";
      return false;
    }
    var wg = WorldGenerator.instance;
    if (ZNet.instance == null || wg?.m_world == null || wg.m_world.m_menu)
    {
      reason = "no world is loaded";
      return false;
    }
    if (instance == null)
    {
      reason = "Better Continents is not initialised";
      return false;
    }
    return true;
  }

  /// <summary>&lt;save data (local)&gt;/BetterContinents/&lt;world name&gt;/export-&lt;yyyy-MM-dd-HH-mm-ss&gt;.</summary>
  public static string DefaultExportDir()
  {
    var name = WorldGenerator.instance?.m_world?.m_name ?? ZNet.World?.m_name ?? "world";
    return Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", SafeFileName(name),
      "export-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
  }

  /// <summary>Starts an export of the loaded world into <paramref name="dir"/> (default: DefaultExportDir()). Returns false,
  /// with LastError set, when it cannot start. Progress is in Phase, Progress and Overall; the result in LastExportDir or
  /// LastError, and in the console and the log.</summary>
  public static bool Start(Options options, string? dir = null)
  {
    string? error = null;
    if (options == null)
      error = "no options";
    else if (!CanExport(out var reason))
      error = reason;
    else
      error = options.Validate();
    string full = "";
    if (error == null)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(dir))
        {
          // Two exports in the same second would share the default name.
          var baseDir = Path.GetFullPath(DefaultExportDir());
          full = baseDir;
          for (int k = 2; Directory.Exists(full); k++)
            full = $"{baseDir}-{k}";
        }
        else
          full = Path.GetFullPath(dir!.Trim());
        if (InsideWorldSaves(full))
          error = $"{full} is inside the game's world saves; a file there can be taken for a world save";
        else if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
          error = $"{full} is not empty";
      }
      catch (Exception e)
      {
        error = $"cannot use the export folder: {e.Message}";
      }
    }
    if (error != null)
    {
      // The caller shows LastError (the commands print it, the HUD displays it).
      LastError = error;
      LogWarning($"World export cannot start: {error}");
      return false;
    }

    Job job;
    try
    {
      job = new Job(options!.Clone(), full);
    }
    catch (Exception e)
    {
      LastError = $"cannot prepare the export: {e.Message}";
      LogError($"World export: {e}");
      return false;
    }
    IsRunning = true;
    RunningDir = full;
    cancelRequested = false;
    pending = null;
    LastError = null;
    Phase = "Starting";
    Progress = 0f;
    Overall = 0f;
    instance.StartCoroutine(Drive(job));
    return true;
  }

  /// <summary>Stops the running export at its next step and deletes the files it has written so far.</summary>
  public static void Cancel()
  {
    if (!IsRunning || cancelRequested)
      return;
    cancelRequested = true;
    Phase = "Cancelling";
    Log("World export: cancel requested.");
  }

  /// <summary>The file pixel (x from the west edge, y from the north edge) of a world position in an export of the given size.</summary>
  public static Vector2Int PixelOf(Vector3 worldPos, int size)
  {
    size = Mathf.Max(2, size);
    return new Vector2Int(WorldExportMath.WorldToPixel(worldPos.x, size, TotalSize), WorldExportMath.WorldZToFileRow(worldPos.z, size, TotalSize));
  }

  /// <summary>Human-readable state, one line each, for the console commands.</summary>
  public static List<string> StatusLines()
  {
    var lines = new List<string>();
    if (IsRunning)
      lines.Add($"World export: {Phase} {Progress:0}% (overall {Overall:0}%)");
    else
      lines.Add($"World export: {Phase}");
    if (LastExportDir != null)
      lines.Add($"Last export: {LastExportDir}");
    if (LastError != null)
      lines.Add($"Last error: {LastError}");
    if (!IsRunning && !CanExport(out var reason))
      lines.Add($"Cannot export now: {reason}");
    return lines;
  }

  // Logs, and prints to the in-game console when there is one (a dedicated server's goes to its log only).
  internal static void Say(string message, bool warning = false)
  {
    if (warning)
      LogWarning(message);
    else
      Log(message);
    try
    {
      Console.instance?.Print(message);
    }
    catch
    {
      // No console on this peer; the log line above is the record.
    }
  }

  private static string Inv(float v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);

  // A headless dedicated server (no game screens). False where there is no game (the offline harness).
  private static bool OnDedicatedServer()
  {
    try
    {
      return ZNet.instance != null && ZNet.instance.IsDedicated();
    }
    catch
    {
      return false;
    }
  }

  private static string SafeFileName(string name)
  {
    var bad = Path.GetInvalidFileNameChars();
    var chars = name.Select(c => bad.Contains(c) ? '_' : c).ToArray();
    var s = new string(chars).Trim();
    return s.Length == 0 ? "world" : s;
  }

  private static string NormalizeDir(string path) =>
    Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";

  // The save-path trap: worlds_local/<name>/ (and the legacy worlds/) belong to the game's save system.
  private static bool InsideWorldSaves(string dir)
  {
    var local = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);
    var target = NormalizeDir(dir);
    foreach (var root in new[] { Path.Combine(local, "worlds_local"), Path.Combine(local, "worlds") })
    {
      if (target.StartsWith(NormalizeDir(root), StringComparison.OrdinalIgnoreCase))
        return true;
    }
    return false;
  }

  // Steps the export and never lets an exception escape into Unity: a failure or a cancel waits for the work in
  // flight, deletes the partial files and resets the state.
  private static IEnumerator Drive(Job job)
  {
    IEnumerator? steps = null;
    Exception? failure = null;
    bool cancelled = false;
    try
    {
      steps = job.Run().GetEnumerator();
    }
    catch (Exception e)
    {
      failure = e;
    }
    while (steps != null)
    {
      try
      {
        if (!steps.MoveNext())
          break;
      }
      catch (OperationCanceledException)
      {
        cancelled = true;
        break;
      }
      catch (Exception e)
      {
        failure = e;
        break;
      }
      yield return null;
    }
    if (failure != null || cancelled)
    {
      cancelRequested = true;
      while (pending != null && !pending.IsCompleted)
        yield return null;
    }
    try
    {
      job.Finish(failure, cancelled);
    }
    catch (Exception e)
    {
      LogError($"World export: finishing failed: {e}");
    }
    finally
    {
      pending = null;
      cancelRequested = false;
      IsRunning = false;
      RunningDir = null;
    }
  }

  private struct TerrainRowStats
  {
    public long Low, High, Ring;
    public float Min, Max;
    public int LavaCells, MossCells;
  }

  private struct LocationRecord(string name, float x, float z, int zoneX, int zoneY)
  {
    public readonly string Name = name;
    public readonly float X = x;
    public readonly float Z = z;
    public readonly int ZoneX = zoneX;
    public readonly int ZoneY = zoneY;
  }

  private sealed class AltClass(string key, List<string> names)
  {
    public readonly string Key = key;
    public readonly List<string> Names = names;
    public Color32 Color;
    public int Regions;
    public long Pixels;
  }

  // One export run: everything it samples, writes and reports. Constructed on the main thread (it snapshots the
  // world and settings); Run() is the coroutine body.
  private sealed class Job
  {
    private readonly Options O;
    private readonly string Dir;
    private readonly WorldGenerator Wg;
    private readonly World World;
    private readonly BetterContinentsSettings Settings;
    private readonly bool BcWorld;
    private readonly int Size;
    private readonly float Total, WorldR, TotalR, Sla;
    private readonly bool EdgeDropoff;
    private readonly bool ZoneBlend;
    private readonly bool OnServer;
    private readonly string Role;
    private readonly int Workers;
    private readonly float SourceForestScale, ImportForestScale;
    private readonly string ForestScaleText;
    private readonly float SourceForestAmount;
    private readonly bool ForestOverridesAllTrees;
    private readonly float WorldSizeSetting, EdgeSizeSetting;
    private readonly AltBiomeGridMode AltGrid;
    private readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DateTime Started = DateTime.Now;

    // Results
    private readonly List<string> Written = [];
    private readonly List<string> Notes = [];
    private readonly List<string> Tracked = [];
    private bool CreatedDir, CreatedSources;
    private long ClippedLow, ClippedHigh, RingClipped;
    private float MinMetres = float.NaN, MaxMetres = float.NaN;
    private long LavaCells, MossCells;
    private readonly long[] BiomePixels = new long[(int)Heightmap.BiomeIndex.Count];
    private long UnknownBiomePixels;
    private long ForestClippedLow, ForestClippedHigh;
    private long HeatPixels, HeatClipped;
    private int AltClassCount, AltRegions, AltForced, AltDropped;
    private bool LocationsFull, LocationsGenerated = true;
    private int LocationCount, LocationWritten, LocationNudged, LocationDropped, LocationOutside, LocationTypes, LocationUnnamed;
    private bool LocationsWritten, AltBiomesWritten, HeatWritten, ForestWritten, HeightsWritten;
    // The New World preset (PresetPass): what the builder did, or why it did not run.
    private WorldImport.Outcome? PresetOutcome;
    private string? PresetSkipped;
    private string? presetName;
    private string PresetName => presetName ??= WorldImport.PresetNameFor(Dir, World?.m_name);
    private bool PresetMade => PresetOutcome != null && PresetOutcome.Error == null;

    // Progress
    private double totalWeight, doneWeight, curWeight;
    private int rowsDone;
    private int cornerMin, cornerCount;
    private static double encodeBytesPerSecond = 40e6;

    private double Mpx => (double)Size * Size / 1e6;

    public Job(Options options, string dir)
    {
      O = options;
      Dir = dir;
      Wg = WorldGenerator.instance;
      World = Wg.m_world;
      Settings = BetterContinents.Settings;
      BcWorld = Settings.EnabledForThisWorld;
      Size = O.Size;
      Total = TotalSize;
      WorldR = WorldRadius;
      TotalR = TotalRadius;
      Sla = WorldExportMath.SeaLevelAdjustment(O.SeaLevel);
      EdgeDropoff = O.EdgeDropoff ?? (!BcWorld || Settings.MapEdgeDropoff);
      OnServer = ZNet.instance.IsServer();
      Role = ZNet.instance.IsDedicated() ? "dedicated server" : OnServer ? "host" : "client";
      Workers = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));
      WorldSizeSetting = BcWorld ? Settings.WorldSize : 10000f;
      EdgeSizeSetting = BcWorld ? Settings.EdgeSize : 500f;
      AltGrid = BcWorld ? Settings.EffectiveAltBiomes.Grid : AltBiomeGridMode.Vanilla;
      ForestOverridesAllTrees = BcWorld && Settings.ForestFactorOverrideAllTrees;

      // HeightmapBuilder blends over the zone prefab's heightmap, 64 x 1 m in vanilla; the corner cache assumes it.
      ZoneBlend = O.ZoneBlend;
      var hm = ZoneSystem.instance?.m_zonePrefab?.GetComponentInChildren<Heightmap>();
      if (ZoneBlend && hm != null && hm.m_width * hm.m_scale != 64f)
      {
        ZoneBlend = false;
        Notes.Add($"The terrain zones are {hm.m_width * hm.m_scale} m, not 64 m (another mod?), so heights were sampled per point instead of with the zone blend.");
      }

      // Forest: the new world scales the game's forest noise by the same Forest Scale this one uses, so the only
      // unknown per pixel is the forestmap. The encoding uses the scale the new world will derive from the config
      // value as written (the curve is not exactly invertible in floats: a vanilla world's 1 comes back 0.99999994,
      // well under a millimetre at the world edge). A short value is written when it derives the same scale.
      SourceForestScale = BcWorld ? Settings.ForestScale : 1f;
      SourceForestAmount = BcWorld ? Settings.ForestAmount : 0.5f;
      var probe = new BetterContinentsSettings { ForestScale = SourceForestScale };
      var configValue = probe.ForestScaleFactor;
      float ScaleFrom(string text)
      {
        probe.ForestScaleFactor = float.Parse(text, CultureInfo.InvariantCulture);
        return probe.ForestScale;
      }
      var exactText = configValue.ToString("R", CultureInfo.InvariantCulture);
      var shortText = configValue.ToString("0.######", CultureInfo.InvariantCulture);
      ImportForestScale = ScaleFrom(exactText);
      ForestScaleText = ScaleFrom(shortText) == ImportForestScale ? shortText : exactText;
    }

    public IEnumerable Run()
    {
      Notes.Add("Only the generated world is exported: player terrain edits, buildings and placed objects are not.");
      PlanWeights();
      Say($"World export started: \"{World.m_name}\" on the {Role} -> {Dir} ({O})");
      if (!Directory.Exists(Dir))
      {
        Directory.CreateDirectory(Dir);
        CreatedDir = true;
      }
      if (O.NeedsTerrain)
        foreach (var step in TerrainPass())
          yield return step;
      if (O.Biomes)
        foreach (var step in BiomePass())
          yield return step;
      if (O.Forest)
        foreach (var step in ForestPass())
          yield return step;
      if (O.Heat)
        foreach (var step in HeatPass())
          yield return step;
      if (O.AltBiomes)
        foreach (var step in AltBiomePass())
          yield return step;
      if (O.Locations)
        foreach (var step in LocationPass())
          yield return step;
      if (O.Sources)
        foreach (var step in SourcesPass())
          yield return step;
      // The settings lines first: the preset is built from them, and export.cfg, written after it, says how it went.
      var config = ConfigLines();
      foreach (var step in PresetPass(config))
        yield return step;
      foreach (var step in TextPass(config))
        yield return step;
    }

    // ---- progress ----------------------------------------------------------------------------------------------

    private double W(double perMegapixel, double fixedPart = 0) => fixedPart + perMegapixel * Mpx;

    private void PlanWeights()
    {
      // 0.05 is the text files at the end.
      double t = 0.05;
      if (O.NeedsTerrain)
        t += (ZoneBlend ? 0.1 : 0) + W(3.0) + (O.Heightmap ? W(0.5) : 0) + (O.Lava ? W(0.15) : 0) + (O.Moss ? W(0.15) : 0) + (O.Paint ? W(0.3) : 0);
      if (O.Biomes)
        t += W(1.0) + W(0.3);
      if (O.Forest)
        t += W(0.6) + W(0.5);
      if (O.Heat)
        t += W(0.2) + W(0.3);
      if (O.AltBiomes)
        t += W(0.2, 0.05) + W(0.3);
      if (O.Locations)
        t += W(0.05, 0.1) + W(0.2);
      if (O.Sources)
        t += 0.5;
      if (O.Preset)
        t += W(0.8, 0.2);
      totalWeight = t;
    }

    private void Begin(string phase, double weight)
    {
      // A cancel between phases stops here, before the next phase starts any work.
      if (cancelRequested)
        throw new OperationCanceledException();
      Phase = phase;
      Progress = 0f;
      curWeight = weight;
      UpdateOverall();
    }

    private void Done()
    {
      doneWeight += curWeight;
      curWeight = 0;
      Progress = 100f;
      UpdateOverall();
    }

    private void Skip(double weight)
    {
      doneWeight += weight;
      UpdateOverall();
    }

    private void UpdateOverall() =>
      Overall = (float)Math.Min(100.0, 100.0 * (doneWeight + curWeight * Progress / 100.0) / Math.Max(1e-9, totalWeight));

    // The world must stay loaded: its generator and Better Continents' patches are what the workers sample.
    private void CheckWorld()
    {
      if (WorldGenerator.instance != Wg || ZNet.instance == null)
        throw new InvalidOperationException("the world was unloaded during the export");
    }

    // Waits for background work, one frame at a time, publishing its progress. Throws when it failed, when the
    // world went away (unless the work needs only the files written so far), or (OperationCanceledException) when the
    // export was cancelled.
    private IEnumerable Await(Task task, Func<float> progress, bool needsWorld = true)
    {
      pending = task;
      while (!task.IsCompleted)
      {
        Progress = Mathf.Clamp(progress(), 0f, 100f);
        UpdateOverall();
        if (needsWorld)
          CheckWorld();
        yield return null;
      }
      pending = null;
      if (task.IsFaulted)
      {
        var inner = task.Exception?.GetBaseException();
        throw new InvalidOperationException(inner?.Message ?? "a worker failed", inner);
      }
      if (cancelRequested)
        throw new OperationCanceledException();
      Done();
    }

    private Func<float> RowProgress(int rows) => () => 100f * Volatile.Read(ref rowsDone) / Math.Max(1, rows);

    // Rows 0..rows-1 on the workers, interleaved (worker w takes rows w, w + n, ...) so the costly rows - Ashlands,
    // mountains - spread evenly. Every row checks the cancel flag, so a cancel stops within a row per worker.
    private Task RunRows(int rows, Action<int> body)
    {
      rowsDone = 0;
      int workers = Math.Max(1, Math.Min(Workers, rows));
      return Task.Run(() => GameUtils.SimpleParallelFor(workers, 0, workers, w =>
      {
        for (int r = w; r < rows; r += workers)
        {
          if (cancelRequested)
            return;
          body(r);
          Interlocked.Increment(ref rowsDone);
        }
      }));
    }

    // Writes one output on a task. The encoder reports no progress, so the phase advances on an estimate from the
    // speed of the files written so far.
    private IEnumerable Write(string relative, double weight, long rawBytes, Action<string> write, string? companion = null)
    {
      var path = Path.Combine(Dir, relative);
      Tracked.Add(path);
      if (companion != null)
        Tracked.Add(Path.Combine(Dir, companion));
      Begin($"Writing {relative}", weight);
      var sw = Stopwatch.StartNew();
      var expected = Math.Max(0.25, rawBytes / encodeBytesPerSecond);
      var task = Task.Run(() => write(path));
      foreach (var step in Await(task, () => (float)Math.Min(95.0, 100.0 * sw.Elapsed.TotalSeconds / expected)))
        yield return step;
      if (rawBytes > 1_000_000 && sw.Elapsed.TotalSeconds > 0.05)
        encodeBytesPerSecond = 0.5 * encodeBytesPerSecond + 0.5 * rawBytes / sw.Elapsed.TotalSeconds;
      Written.Add(relative.Replace('\\', '/'));
      if (companion != null)
        Written.Add(companion);
    }

    // ---- terrain: heights, lava, moss, paint ---------------------------------------------------------------------

    private IEnumerable TerrainPass()
    {
      int n = Size;
      L16[]? heights = O.Heightmap ? new L16[n * n] : null;
      L8[]? lava = O.Lava ? new L8[n * n] : null;
      L8[]? moss = O.Moss ? new L8[n * n] : null;
      Rgb24[]? paint = O.Paint ? new Rgb24[n * n] : null;
      var stats = new TerrainRowStats[n];

      Heightmap.Biome[]? corners = null;
      if (ZoneBlend)
      {
        cornerMin = WorldExportMath.ZoneIndex(WorldExportMath.PixelToWorld(0, n, Total));
        cornerCount = WorldExportMath.ZoneIndex(WorldExportMath.PixelToWorld(n - 1, n, Total)) + 2 - cornerMin;
        corners = new Heightmap.Biome[cornerCount * cornerCount];
        var grid = corners;
        Begin("Sampling zone corners", 0.1);
        foreach (var step in Await(RunRows(cornerCount, r => CornerRow(r, grid)), RowProgress(cornerCount)))
          yield return step;
      }

      Begin(O.Heightmap ? "Sampling heights" : "Sampling terrain masks", W(3.0));
      foreach (var step in Await(RunRows(n, row => TerrainRow(row, corners, heights, lava, moss, paint, stats)), RowProgress(n)))
        yield return step;
      corners = null;

      foreach (var s in stats)
      {
        ClippedLow += s.Low;
        ClippedHigh += s.High;
        RingClipped += s.Ring;
        LavaCells += s.LavaCells;
        MossCells += s.MossCells;
        if (s.Min <= s.Max)
        {
          MinMetres = float.IsNaN(MinMetres) ? s.Min : Math.Min(MinMetres, s.Min);
          MaxMetres = float.IsNaN(MaxMetres) ? s.Max : Math.Max(MaxMetres, s.Max);
        }
      }

      if (heights != null)
      {
        foreach (var step in Write("heightmap.png", W(0.5), 2L * n * n, p => WorldExportPng.SaveL16(p, heights!, n)))
          yield return step;
        heights = null;
        HeightsWritten = true;
        var floor = WorldExportMath.ValueToMetres(0f, O.HeightmapAmount, O.SeaLevel);
        var ceiling = WorldExportMath.ValueToMetres(1f, O.HeightmapAmount, O.SeaLevel);
        if (ClippedLow > 0 || ClippedHigh > 0)
          Notes.Add($"Heights: {ClippedLow} pixel(s) below {Inv(floor)} m and {ClippedHigh} above {Inv(ceiling)} m inside the world were clipped; "
                    + "export again with a larger heightmap amount (and a matching sea level) to keep them.");
        if (RingClipped > 0)
          Notes.Add($"Heights: {RingClipped} pixel(s) in the edge ring between {Inv(WorldR)} m and {Inv(TotalR)} m were clipped (the world edge falls away there).");
      }
      if (lava != null)
      {
        if (LavaCells > 0)
        {
          foreach (var step in Write("lavamap.png", W(0.15), (long)n * n, p => WorldExportPng.SaveL8(p, lava!, n)))
            yield return step;
        }
        else
        {
          Skip(W(0.15));
          Notes.Add("No Ashlands terrain on this map, so there is no lava map.");
        }
        lava = null;
      }
      if (moss != null)
      {
        if (MossCells > 0)
        {
          foreach (var step in Write("mossmap.png", W(0.15), (long)n * n, p => WorldExportPng.SaveL8(p, moss!, n)))
            yield return step;
        }
        else
        {
          Skip(W(0.15));
          Notes.Add("No Mistlands terrain on this map, so there is no moss map.");
        }
        moss = null;
      }
      if (paint != null)
      {
        foreach (var step in Write("paintmap.png", W(0.3), 3L * n * n, p =>
                 {
                   WorldExportPng.SaveRgb24(p, paint!, n);
                   // No colour mappings: every pixel is its own mask colour, alpha untouched (paint.a = 1). The comment has
                   // no colon, because ImageMapPaint reads any line with one as a "from: to" mapping.
                   WorldExportPng.WriteText(Path.Combine(Dir, "paintmap.txt"),
                   [
                     "# The paint map needs no colour list. Every pixel of paintmap.png is used as the ground paint as it is",
                     "# (red, green and blue of the terrain mask), and the game keeps its own alpha (lava and moss).",
                   ]);
                 }, "paintmap.txt"))
          yield return step;
        paint = null;
      }
    }

    private void CornerRow(int r, Heightmap.Biome[] corners)
    {
      float z = (cornerMin + r) * 64f - 32f;
      for (int c = 0; c < cornerCount; c++)
        corners[r * cornerCount + c] = Wg.GetBiome((cornerMin + c) * 64f - 32f, z);
    }

    // One file row of the terrain. With the zone blend this is HeightmapBuilder.Build's non-LOD path evaluated at the
    // pixel: the zone's four corner biomes, each corner's height formula at the point, blended with a smoothstep across
    // the zone (the same DUtils calls, so equal inputs give bit-equal heights), and the terrain masks blended the same way.
    private void TerrainRow(int fileRow, Heightmap.Biome[]? corners, L16[]? heights, L8[]? lava, L8[]? moss, Rgb24[]? paint, TerrainRowStats[] stats)
    {
      int n = Size;
      float wz = WorldExportMath.FileRowToWorldZ(fileRow, n, Total);
      int zz = WorldExportMath.ZoneIndex(wz);
      float tz = DUtils.SmoothStep(0f, 1f, (float)(((double)wz - (zz * 64f - 32f)) / 64.0));
      int cz = zz - cornerMin;
      var st = new TerrainRowStats { Min = float.MaxValue, Max = float.MinValue };
      for (int col = 0; col < n; col++)
      {
        float wx = WorldExportMath.PixelToWorld(col, n, Total);
        float h;
        Color mask;
        bool hasAsh = false, hasMist = false;
        float ashA = 0f, mistA = 0f;
        if (corners != null)
        {
          int zx = WorldExportMath.ZoneIndex(wx);
          int i0 = cz * cornerCount + (zx - cornerMin);
          var b00 = corners[i0];
          var b10 = corners[i0 + 1];
          var b01 = corners[i0 + cornerCount];
          var b11 = corners[i0 + cornerCount + 1];
          if (b01 == b00 && b10 == b00 && b11 == b00)
          {
            h = Wg.GetBiomeHeight(b00, wx, wz, out mask);
            hasAsh = b00 == Heightmap.Biome.AshLands;
            hasMist = b00 == Heightmap.Biome.Mistlands;
            ashA = mistA = mask.a;
          }
          else
          {
            float tx = DUtils.SmoothStep(0f, 1f, (float)(((double)wx - (zx * 64f - 32f)) / 64.0));
            // Each distinct corner biome once: GetBiomeHeight is a pure function of (biome, x, z).
            float h00 = Wg.GetBiomeHeight(b00, wx, wz, out var m00);
            float h10, h01, h11;
            Color m10, m01, m11;
            if (b10 == b00) { h10 = h00; m10 = m00; }
            else h10 = Wg.GetBiomeHeight(b10, wx, wz, out m10);
            if (b01 == b00) { h01 = h00; m01 = m00; }
            else if (b01 == b10) { h01 = h10; m01 = m10; }
            else h01 = Wg.GetBiomeHeight(b01, wx, wz, out m01);
            if (b11 == b00) { h11 = h00; m11 = m00; }
            else if (b11 == b10) { h11 = h10; m11 = m10; }
            else if (b11 == b01) { h11 = h01; m11 = m01; }
            else h11 = Wg.GetBiomeHeight(b11, wx, wz, out m11);
            h = DUtils.Lerp(DUtils.Lerp(h00, h10, tx), DUtils.Lerp(h01, h11, tx), tz);
            mask = Color.Lerp(Color.Lerp(m00, m10, tx), Color.Lerp(m01, m11, tx), tz);
            // Lava and moss maps replace the alpha of the Ashlands / Mistlands corners before the blend, so they hold
            // those corners' own alpha here.
            if (b00 == Heightmap.Biome.AshLands) { hasAsh = true; ashA = m00.a; }
            else if (b10 == Heightmap.Biome.AshLands) { hasAsh = true; ashA = m10.a; }
            else if (b01 == Heightmap.Biome.AshLands) { hasAsh = true; ashA = m01.a; }
            else if (b11 == Heightmap.Biome.AshLands) { hasAsh = true; ashA = m11.a; }
            if (b00 == Heightmap.Biome.Mistlands) { hasMist = true; mistA = m00.a; }
            else if (b10 == Heightmap.Biome.Mistlands) { hasMist = true; mistA = m10.a; }
            else if (b01 == Heightmap.Biome.Mistlands) { hasMist = true; mistA = m01.a; }
            else if (b11 == Heightmap.Biome.Mistlands) { hasMist = true; mistA = m11.a; }
          }
        }
        else
        {
          var biome = Wg.GetBiome(wx, wz);
          h = Wg.GetBiomeHeight(biome, wx, wz, out mask);
          hasAsh = biome == Heightmap.Biome.AshLands;
          hasMist = biome == Heightmap.Biome.Mistlands;
          ashA = mistA = mask.a;
        }

        int i = fileRow * n + col;
        if (heights != null)
        {
          float fh = h / WorldExportMath.HeightScale;
          float d = Mathf.Sqrt(wx * wx + wz * wz);
          bool inside = d <= WorldR;
          // With the drop-off kept, Better Continents applies it again on import: store the height before it, so the
          // import lands on the sampled height. Past the total radius the drop-off wins whatever is stored.
          if (EdgeDropoff && !inside)
            WorldExportMath.TryUndoEdgeDropoff(fh, d, WorldR, TotalR, out fh);
          float v = (fh + WorldExportMath.BaseOffset - Sla) / O.HeightmapAmount;
          heights[i] = new L16(WorldExportMath.ValueToUShort(v, out int clip));
          if (inside)
          {
            if (clip < 0) st.Low++;
            else if (clip > 0) st.High++;
            if (h < st.Min) st.Min = h;
            if (h > st.Max) st.Max = h;
          }
          else if (d <= TotalR && clip != 0)
            st.Ring++;
        }
        if (lava != null && hasAsh)
        {
          lava[i] = new L8(WorldExportMath.ValueToByte(ashA));
          st.LavaCells++;
        }
        if (moss != null && hasMist)
        {
          moss[i] = new L8(WorldExportMath.ValueToByte(mistA));
          st.MossCells++;
        }
        if (paint != null)
          paint[i] = new Rgb24(WorldExportMath.ValueToByte(mask.r), WorldExportMath.ValueToByte(mask.g), WorldExportMath.ValueToByte(mask.b));
      }
      stats[fileRow] = st;
    }

    // ---- biomes ------------------------------------------------------------------------------------------------

    private IEnumerable BiomePass()
    {
      int n = Size;
      var table = ImageMapBiome.DefaultColorTable();
      var colours = new Rgb24[(int)Heightmap.BiomeIndex.Count];
      foreach (var kv in table)
        colours[ImageMapBiome.ToSafeIndex(kv.Key)] = new Rgb24(kv.Value.r, kv.Value.g, kv.Value.b);
      Rgb24[]? pixels = new Rgb24[n * n];
      Begin("Sampling biomes", W(1.0));
      foreach (var step in Await(RunRows(n, row =>
               {
                 var local = new long[colours.Length];
                 long unknown = 0;
                 float wz = WorldExportMath.FileRowToWorldZ(row, n, Total);
                 for (int col = 0; col < n; col++)
                 {
                   var biome = Wg.GetBiome(WorldExportMath.PixelToWorld(col, n, Total), wz);
                   int k = ImageMapBiome.ToSafeIndex(biome);
                   if (k == 0 && biome != Heightmap.Biome.None)
                     unknown++;
                   pixels![row * n + col] = colours[k];
                   local[k]++;
                 }
                 for (int k = 0; k < local.Length; k++)
                   Interlocked.Add(ref BiomePixels[k], local[k]);
                 Interlocked.Add(ref UnknownBiomePixels, unknown);
               }), RowProgress(n)))
        yield return step;
      if (UnknownBiomePixels > 0)
        Notes.Add($"Biomes: {UnknownBiomePixels} pixel(s) had a biome Better Continents' biome map cannot hold (another mod?) and were left black (None: the game decides there).");
      foreach (var step in Write("biomemap.png", W(0.3), 3L * n * n, p =>
               {
                 WorldExportPng.SaveRgb24(p, pixels!, n);
                 WorldExportPng.WriteText(Path.Combine(Dir, "biomemap.txt"), ImageMapBiome.DefaultColors.Split('|'));
               }, "biomemap.txt"))
        yield return step;
      pixels = null;
    }

    // ---- forest ------------------------------------------------------------------------------------------------

    private IEnumerable ForestPass()
    {
      int n = Size;
      L16[]? pixels = new L16[n * n];
      long low = 0, high = 0;
      Begin("Sampling forest", W(0.6));
      foreach (var step in Await(RunRows(n, row =>
               {
                 long rowLow = 0, rowHigh = 0;
                 float wz = WorldExportMath.FileRowToWorldZ(row, n, Total);
                 for (int col = 0; col < n; col++)
                 {
                   var pos = new Vector3(WorldExportMath.PixelToWorld(col, n, Total), 0f, wz);
                   // What vegetation reads in this world (Better Continents' patches included)...
                   float target = WorldGenerator.GetForestFactor(pos);
                   // ...and what the game's own noise will give the new world at this point: vanilla's GetForestFactor
                   // body after Better Continents' Forest Scale prefix.
                   var scaled = ImportForestScale != 1f ? pos * ImportForestScale : pos;
                   float vanilla = DUtils.Fbm(scaled * 0.01f * 0.4f, 3, 1.6f, 0.7f);
                   float f = O.ForestExact ? WorldExportMath.ForestMapExact(target, vanilla) : WorldExportMath.ForestMapAdditive(target, vanilla);
                   pixels![row * n + col] = new L16(WorldExportMath.ValueToUShort(f, out _));
                   // Counted only past a real difference: the new world's scale is a float step off this one's, so a
                   // vanilla map lands a hair either side of 0 everywhere.
                   if (f < -ForestTolerance) rowLow++;
                   else if (f > 1f + ForestTolerance) rowHigh++;
                 }
                 Interlocked.Add(ref low, rowLow);
                 Interlocked.Add(ref high, rowHigh);
               }), RowProgress(n)))
        yield return step;
      ForestClippedLow = low;
      ForestClippedHigh = high;
      if (low > 0)
        Notes.Add($"Forest: {low} pixel(s) have less forest than the game's own noise gives there, which the additive encoding cannot store; "
                  + "they come back at the game's density. Export with the exact forest encoding to keep them.");
      foreach (var step in Write("forestmap.png", W(0.5), 2L * n * n, p => WorldExportPng.SaveL16(p, pixels!, n)))
        yield return step;
      pixels = null;
      ForestWritten = true;
    }

    // A normalised forest difference that matters (a forest factor of about 0.0002).
    private const float ForestTolerance = 1e-4f;

    // ---- heat --------------------------------------------------------------------------------------------------

    private IEnumerable HeatPass()
    {
      int n = Size;
      L16[]? pixels = new L16[n * n];
      long hot = 0, clipped = 0;
      Begin("Sampling heat", W(0.2));
      foreach (var step in Await(RunRows(n, row =>
               {
                 long rowHot = 0, rowClipped = 0;
                 float wz = WorldExportMath.FileRowToWorldZ(row, n, Total);
                 for (int col = 0; col < n; col++)
                 {
                   float wx = WorldExportMath.PixelToWorld(col, n, Total);
                   float g = WorldGenerator.GetAshlandsOceanGradient(wx, wz);
                   var p = WorldExportMath.ValueToUShort(WorldExportMath.HeatToValue(g, O.HeatScale), out int clip);
                   pixels![row * n + col] = new L16(p);
                   if (p > 0) rowHot++;
                   // The map square's corners lie past the world, where the gradient keeps climbing (about 20).
                   if (clip > 0 && wx * wx + wz * wz <= TotalR * TotalR) rowClipped++;
                 }
                 Interlocked.Add(ref hot, rowHot);
                 Interlocked.Add(ref clipped, rowClipped);
               }), RowProgress(n)))
        yield return step;
      HeatPixels = hot;
      HeatClipped = clipped;
      if (!(BcWorld && Settings.HasHeatMap))
        Notes.Add("Heat: the rebuilt world is Ashlands wherever the heat is above 0, i.e. along the ocean-gradient line. This world's own "
                  + "Ashlands test (IsAshlands) uses a slightly different line, up to about 100 m away, so weather and lava damage can "
                  + "move in that strip; the biome map keeps the Ashlands ground itself where it was.");
      if (clipped > 0)
        Notes.Add($"Heat: {clipped} pixel(s) inside the world were hotter than the heat scale ({Inv(O.HeatScale)}) and were clipped to it; export with a larger heat scale to keep them.");
      foreach (var step in Write("heatmap.png", W(0.3), 2L * n * n, p => WorldExportPng.SaveL16(p, pixels!, n)))
        yield return step;
      pixels = null;
      HeatWritten = true;
    }

    // ---- alt biomes --------------------------------------------------------------------------------------------

    private IEnumerable AltBiomePass()
    {
      int n = Size;
      var data = World.m_biomeData;
      if (data == null || !data.IsReady || data.PointSectors == null || data.PointHeights == null)
      {
        Skip(W(0.2, 0.05) + W(0.3));
        Notes.Add("Alt biomes: the world has no alt-biome data yet, so there is no alt-biome map.");
        yield break;
      }
      // Grid geometry on the main thread (Expand World Size patches these functions).
      float g0 = AltBiomeWorldData.MapSpaceToWorldSpace(0f);
      float cell = AltBiomeWorldData.MapSpaceToWorldSpace(1f) - g0;
      var sectors = data.PointSectors;
      var heights = data.PointHeights;
      int gw = sectors.GetLength(0), gh = sectors.GetLength(1);
      var classOf = new Dictionary<BiomeSector, int>();
      var classes = new List<AltClass>();
      Rgb24[]? pixels = new Rgb24[n * n];
      Rgb24[] palette = [];

      Begin("Sampling alt biomes", W(0.2, 0.05));

      // Main thread, NOT the background task below: BiomeSector.AltBiomes is a plain List<AltBiome> that a live
      // rebuild (the "bc ab mode"/"bc ab fn"/"bc reload ab" commands, AltBiomeControl.RequestRebuild's Assignment
      // level) clears and refills in place on the main thread (ClearAssignments then GenerateAltBiomes) whenever a
      // host runs one while an export is in flight - reading it from a worker at the same time is a collection-
      // modified-during-enumeration race, not just a slow one. This walk is bounded by the game's own alt-biome grid
      // (at most 2048 x 2048 points, regardless of the export's own pixel size) and runs in a frame or two even at
      // that size, so doing it here costs nothing worth moving to a worker.
      var byKey = new Dictionary<string, int>();
      for (int y = 0; y < gh && !cancelRequested; y++)
      {
        for (int x = 0; x < gw; x++)
        {
          var s = sectors[x, y];
          if (s == null || s.AltBiomes.Count == 0 || classOf.ContainsKey(s))
            continue;
          var names = LegendNames(s);
          var key = string.Join(" + ", names);
          if (!byKey.TryGetValue(key, out var index))
          {
            if (classes.Count >= ImageMapAltBiome.MaxEntries)
            {
              classOf[s] = -1;
              AltDropped++;
              continue;
            }
            index = classes.Count;
            classes.Add(new AltClass(key, names));
            byKey[key] = index;
          }
          classOf[s] = index;
          classes[index].Regions++;
        }
      }
      AssignAltColours(classes);
      palette = classes.Select(c => new Rgb24(c.Color.r, c.Color.g, c.Color.b)).ToArray();

      // From here on, only sectors[,] (read for object identity), heights[,] and the now-finished, read-only
      // classOf/palette are touched, on worker threads: none of that is BiomeSector.AltBiomes, so a live rebuild
      // running at the same time no longer races with it (a Sectors/Points-level rebuild also replaces
      // World.m_biomeData wholesale rather than mutating this data/sectors/heights snapshot in place).
      rowsDone = 0;
      var task = Task.Run(() =>
      {
        var counts = new long[classes.Count];
        GameUtils.SimpleParallelFor(Math.Max(1, Math.Min(Workers, n)), 0, Math.Max(1, Math.Min(Workers, n)), w =>
        {
          int step = Math.Max(1, Math.Min(Workers, n));
          var local = new long[counts.Length];
          for (int row = w; row < n; row += step)
          {
            if (cancelRequested)
              return;
            // The class of the grid point nearest each pixel: the import samples the nearest pixel at every grid
            // point, and with pixels closer than 12 m that pixel's nearest grid point is that same point.
            float wz = WorldExportMath.FileRowToWorldZ(row, n, Total);
            int gy = Mathf.Clamp(Mathf.RoundToInt((wz - g0) / cell), 0, gh - 1);
            for (int col = 0; col < n; col++)
            {
              int gx = Mathf.Clamp(Mathf.RoundToInt((WorldExportMath.PixelToWorld(col, n, Total) - g0) / cell), 0, gw - 1);
              var s = sectors[gx, gy];
              // Points past the sampled disc (height -1000) are never planted.
              if (s == null || heights[gx, gy] == -1000f || !classOf.TryGetValue(s, out var k) || k < 0)
                continue;
              pixels![row * n + col] = palette[k];
              local[k]++;
            }
            Interlocked.Increment(ref rowsDone);
          }
          lock (counts)
          {
            for (int k = 0; k < local.Length; k++)
              counts[k] += local[k];
          }
        });
        for (int k = 0; k < classes.Count; k++)
          classes[k].Pixels = counts[k];
      });
      foreach (var step in Await(task, RowProgress(n)))
        yield return step;

      AltClassCount = classes.Count;
      AltRegions = classes.Sum(c => c.Regions);
      AltForced = classes.Count(c => c.Names.Any(x => x.StartsWith("!")));
      if (AltDropped > 0)
        Notes.Add($"Alt biomes: more than {ImageMapAltBiome.MaxEntries} different alt-biome combinations; {AltDropped} region(s) past that were left unplanted.");
      if (Size < 2048)
        Notes.Add("Alt biomes: below 2048 pixels a pixel is wider than the game's 12 m alt-biome grid, so region borders move by up to a pixel.");
      var legend = AltLegend(classes);
      foreach (var step in Write("altbiomemap.png", W(0.3), 3L * n * n, p =>
               {
                 WorldExportPng.SaveRgb24(p, pixels!, n);
                 WorldExportPng.WriteText(Path.Combine(Dir, "altbiomemap.txt"), legend);
               }, "altbiomemap.txt"))
        yield return step;
      pixels = null;
      AltBiomesWritten = true;
    }

    // The legend names of a region's alt biomes, in the order they were added. A name gets "!" (forced) only when the
    // import's content rules would refuse it where it is now: disabled, not an alt biome of the region's biome, or
    // incompatible with one before it. Forcing everything would also plant it past biome borders that shift a pixel.
    private static List<string> LegendNames(BiomeSector s)
    {
      var names = new List<string>();
      var earlier = new List<AltBiome>();
      foreach (var alt in s.AltBiomes)
      {
        if (alt == null)
          continue;
        bool force = !alt.m_enabled || (alt.m_biome & s.Biome) == 0
                     || earlier.Any(e => e.m_incompatibleAltBiomes.Contains(alt.m_name) || alt.m_incompatibleAltBiomes.Contains(e.m_name));
        names.Add((force ? "!" : "") + alt.m_name);
        earlier.Add(alt);
      }
      return names;
    }

    // Single alt biomes keep their palette colour; combinations and modded names get stable hash colours at least
    // 2 x 20 RGB units from every other colour and from black, so the legend decodes without warnings.
    private static void AssignAltColours(List<AltClass> classes)
    {
      var used = new List<Color32> { new(0, 0, 0, 255) };
      bool Far(Color32 c) => used.All(u => ImageMapAltBiome.ColorDistanceSq(u, c) > 4 * ImageMapAltBiome.ColorTolerance * ImageMapAltBiome.ColorTolerance);
      foreach (var c in classes)
      {
        if (c.Names.Count != 1 || c.Names[0].StartsWith("!"))
          continue;
        var hex = ImageMapAltBiome.DefaultColorFor(c.Names[0]);
        if (hex != null && ImageMapAltBiome.TryParseColor(hex, out var colour) && !used.Any(u => u.r == colour.r && u.g == colour.g && u.b == colour.b))
        {
          c.Color = colour;
          used.Add(colour);
        }
      }
      foreach (var c in classes)
      {
        if (c.Color.a == 255)
          continue;
        Color32 pick = HashColour(c.Key, 0);
        for (int salt = 0; salt < 256; salt++)
        {
          pick = HashColour(c.Key, salt);
          if (Far(pick))
            break;
        }
        c.Color = pick;
        used.Add(pick);
      }
    }

    private List<string> AltLegend(List<AltClass> classes)
    {
      var lines = new List<string>
      {
        $"# Better Continents alt-biome map exported from \"{World.m_name}\" on {Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.",
        "# One colour per set of alt biomes, exactly as the game placed them; black is unplanted. Load it with",
        "# Mode = PlantedOnly so the game adds none at random. A leading ! forces a name the content rules would refuse.",
      };
      foreach (var c in classes)
        lines.Add($"{c.Key}: {c.Color.r:X2}{c.Color.g:X2}{c.Color.b:X2}   # {c.Regions} region(s)");
      return lines;
    }

    // ---- locations ---------------------------------------------------------------------------------------------

    private IEnumerable LocationPass()
    {
      int n = Size;
      var zs = ZoneSystem.instance;
      if (zs == null)
      {
        Skip(W(0.05, 0.1) + W(0.2));
        Notes.Add("Locations: no zone system, so there is no location map.");
        yield break;
      }
      // Main thread: the zone system changes these as zones generate.
      var records = new List<LocationRecord>();
      LocationsFull = OnServer;
      if (OnServer)
      {
        LocationsGenerated = zs.LocationsGenerated;
        foreach (var kv in zs.m_locationInstances)
        {
          var name = kv.Value.m_location?.m_prefabName;
          if (string.IsNullOrEmpty(name))
          {
            LocationUnnamed++;
            continue;
          }
          records.Add(new LocationRecord(name!, kv.Value.m_position.x, kv.Value.m_position.z, kv.Key.x, kv.Key.y));
        }
      }
      else
      {
        var icons = new Dictionary<Vector3, string>();
        zs.GetLocationIcons(icons);
        foreach (var kv in icons)
        {
          if (string.IsNullOrEmpty(kv.Value))
            continue;
          var zone = ZoneSystem.GetZone(kv.Key);
          records.Add(new LocationRecord(kv.Value, kv.Key.x, kv.Key.z, zone.x, zone.y));
        }
      }
      LocationCount = records.Count;
      if (!OnServer)
      {
        Notes.Add($"Locations: sampled on a client, which only knows the {records.Count} location(s) the server shows on the map "
                  + "(start temple, bosses, traders). Export on the host or the dedicated server for every location; this map "
                  + "leaves Skip Default Locations off so the game places the rest.");
        LogWarning("World export: a client does not know every location; the location map holds only the map-icon locations.");
      }
      else if (!LocationsGenerated)
        Notes.Add("Locations: the world was still placing its locations when it was exported, so some may be missing.");
      if (LocationUnnamed > 0)
        Notes.Add($"Locations: {LocationUnnamed} instance(s) whose location type is not loaded were skipped.");
      if (records.Count == 0)
      {
        // An empty map would still switch the game's own placement off (Skip Default Locations).
        Skip(W(0.05, 0.1) + W(0.2));
        Notes.Add("Locations: this peer knows no locations, so there is no location map; the game places them itself.");
        yield break;
      }

      Rgb24[]? pixels = new Rgb24[n * n];
      var legend = new List<string>();
      Begin("Placing locations", W(0.05, 0.1));
      var task = Task.Run(() => PlaceLocations(records, pixels!, legend));
      foreach (var step in Await(task, () => 50f))
        yield return step;
      if (LocationOutside > 0)
        Notes.Add($"Locations: {LocationOutside} instance(s) lie outside the map square and were skipped.");
      if (LocationNudged > 0 || LocationDropped > 0)
        Notes.Add($"Locations: {LocationNudged} moved by a pixel or two to stay apart from a neighbour of the same kind, {LocationDropped} dropped (no free pixel nearby).");
      foreach (var step in Write("locationmap.png", W(0.2), 3L * n * n, p =>
               {
                 WorldExportPng.SaveRgb24(p, pixels!, n);
                 WorldExportPng.WriteText(Path.Combine(Dir, "locationmap.txt"), legend);
               }, "locationmap.txt"))
        yield return step;
      pixels = null;
      LocationsWritten = true;
    }

    // One pixel per location. The location loader flood-fills 4-connected pixels of one colour into ONE location
    // at a random pixel of the blob, and the game keeps one location per 64 m zone, so a pixel must not touch another
    // of its colour and should import into the zone its location is in. The nearest pixel almost always qualifies;
    // otherwise the nearest one within two pixels that does, else it is dropped.
    private void PlaceLocations(List<LocationRecord> records, Rgb24[] pixels, List<string> legend)
    {
      int n = Size;
      records.Sort((a, b) =>
      {
        int c = string.CompareOrdinal(a.Name, b.Name);
        if (c != 0) return c;
        c = a.X.CompareTo(b.X);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
      });
      var names = records.Select(r => r.Name).Distinct().ToList();
      var colours = LocationColours(names);
      LocationTypes = names.Count;
      foreach (var name in names)
      {
        var c = colours[name];
        legend.Add($"{name}: {c.R},{c.G},{c.B}");
      }
      var occupiedZones = new HashSet<long>(records.Select(r => ZoneKey(r.ZoneX, r.ZoneY)));
      int loggedDrops = 0;
      foreach (var r in records)
      {
        if (!WorldExportMath.OnMap(r.X, Total) || !WorldExportMath.OnMap(r.Z, Total))
        {
          LocationOutside++;
          continue;
        }
        var colour = colours[r.Name];
        int px = WorldExportMath.WorldToLocationPixel(r.X, n, Total);
        int my = WorldExportMath.WorldToLocationPixel(r.Z, n, Total);
        if (TryPlace(pixels, px, my, colour, r, occupiedZones, out bool moved))
        {
          LocationWritten++;
          if (moved)
            LocationNudged++;
        }
        else
        {
          LocationDropped++;
          if (loggedDrops++ < 20)
            LogWarning($"World export: no free pixel for {r.Name} at ({r.X:0.#}, {r.Z:0.#}); it is not in the location map.");
        }
      }
    }

    private static long ZoneKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static readonly (int dx, int dy)[] Nudges = BuildNudges();

    private static (int, int)[] BuildNudges()
    {
      var list = new List<(int, int)>();
      for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
          list.Add((dx, dy));
      return [.. list.OrderBy(t => t.Item1 * t.Item1 + t.Item2 * t.Item2).ThenBy(t => Math.Abs(t.Item2)).ThenBy(t => t.Item2).ThenBy(t => t.Item1)];
    }

    private bool TryPlace(Rgb24[] pixels, int px, int my, Rgb24 colour, LocationRecord r, HashSet<long> occupiedZones, out bool moved)
    {
      int n = Size;
      bool Free(int x, int y)
      {
        if (x < 0 || y < 0 || x >= n || y >= n)
          return false;
        if (!IsBlack(pixels[WorldExportMath.FlipRow(y, n) * n + x]))
          return false;
        return !Same(x + 1, y) && !Same(x - 1, y) && !Same(x, y + 1) && !Same(x, y - 1);
      }
      bool Same(int x, int y) => x >= 0 && y >= 0 && x < n && y < n && pixels[WorldExportMath.FlipRow(y, n) * n + x].Equals(colour);
      Vector2s ImportZone(int x, int y) => ZoneSystem.GetZone(new Vector3(WorldExportMath.LocationPixelToWorld(x, n, Total), 0f, WorldExportMath.LocationPixelToWorld(y, n, Total)));
      // First choice: a free pixel that imports into the location's own zone; second: any free pixel whose zone no
      // other location uses.
      for (int pass = 0; pass < 2; pass++)
      {
        foreach (var (dx, dy) in Nudges)
        {
          int x = px + dx, y = my + dy;
          if (!Free(x, y))
            continue;
          var zone = ImportZone(x, y);
          bool own = zone.x == r.ZoneX && zone.y == r.ZoneY;
          if (pass == 0 ? !own : occupiedZones.Contains(ZoneKey(zone.x, zone.y)))
            continue;
          pixels[WorldExportMath.FlipRow(y, n) * n + x] = colour;
          occupiedZones.Add(ZoneKey(zone.x, zone.y));
          moved = dx != 0 || dy != 0 || !own;
          return true;
        }
      }
      moved = false;
      return false;
    }

    private static bool IsBlack(Rgb24 c) => c.R == 0 && c.G == 0 && c.B == 0;

    // Each location type its own colour, so the import never has to pick at random between types sharing one: the
    // default legend's colour where this type is its first owner there, a stable hash colour otherwise.
    private static Dictionary<string, Rgb24> LocationColours(List<string> names)
    {
      var defaults = new Dictionary<string, Color32>();
      var owner = new Dictionary<int, string>();
      foreach (var entry in ImageMapLocation.DefaultColors.Split('|'))
      {
        var parts = entry.Split(':');
        if (parts.Length != 2)
          continue;
        var name = parts[0].Trim();
        var rgb = parts[1].Split(',');
        if (rgb.Length < 3 || !byte.TryParse(rgb[0].Trim(), out var r) || !byte.TryParse(rgb[1].Trim(), out var g) || !byte.TryParse(rgb[2].Trim(), out var b))
          continue;
        var c = new Color32(r, g, b, 255);
        if (!defaults.ContainsKey(name))
          defaults[name] = c;
        var key = (r << 16) | (g << 8) | b;
        if (!owner.ContainsKey(key))
          owner[key] = name;
      }
      var used = new HashSet<int> { 0 };
      var result = new Dictionary<string, Rgb24>();
      foreach (var name in names)
      {
        if (!defaults.TryGetValue(name, out var c))
          continue;
        var key = (c.r << 16) | (c.g << 8) | c.b;
        if (owner[key] == name && used.Add(key))
          result[name] = new Rgb24(c.r, c.g, c.b);
      }
      foreach (var name in names)
      {
        if (result.ContainsKey(name))
          continue;
        for (int salt = 0; ; salt++)
        {
          var c = HashColour(name, salt);
          if (used.Add((c.r << 16) | (c.g << 8) | c.b))
          {
            result[name] = new Rgb24(c.r, c.g, c.b);
            break;
          }
        }
      }
      return result;
    }

    // FNV-1a over the name (and a salt), each channel kept in 48..255 so nothing comes out near black.
    private static Color32 HashColour(string key, int salt)
    {
      uint h = 2166136261;
      foreach (char ch in salt == 0 ? key : key + "#" + salt.ToString(CultureInfo.InvariantCulture))
      {
        h ^= ch;
        h *= 16777619;
      }
      return new Color32((byte)(48 + h % 208), (byte)(48 + (h >> 8) % 208), (byte)(48 + (h >> 16) % 208), 255);
    }

    // ---- sources -----------------------------------------------------------------------------------------------

    private IEnumerable SourcesPass()
    {
      if (!BcWorld)
      {
        Skip(0.5);
        yield break;
      }
      // Main thread: the settings object is only read here, but it belongs to the game loop.
      var maps = Settings.LoadedImageMaps();
      var dump = new List<string>();
      Settings.Dump(dump.Add);
      if (maps.Count == 0)
      {
        Skip(0.5);
        Notes.Add("Sources: this Better Continents world uses no image maps.");
        yield break;
      }
      var sourcesDir = Path.Combine(Dir, "sources");
      Begin("Copying sources", 0.5);
      var done = 0;
      var task = Task.Run(() =>
      {
        if (!Directory.Exists(sourcesDir))
        {
          Directory.CreateDirectory(sourcesDir);
          CreatedSources = true;
        }
        void Out(string name, Action<string> write)
        {
          var path = Path.Combine(sourcesDir, name);
          lock (Tracked)
            Tracked.Add(path);
          write(path);
          lock (Written)
            Written.Add("sources/" + name);
        }
        Out("bc-settings.txt", p => WorldExportPng.WriteText(p, dump));
        foreach (var (file, map) in maps)
        {
          try
          {
            WriteSource(file, map, Out);
          }
          catch (Exception e)
          {
            lock (Notes)
              Notes.Add($"Sources: {file} could not be written ({e.Message}).");
          }
          Interlocked.Increment(ref done);
        }
        CarryForward(maps);
      });
      foreach (var step in Await(task, () => 100f * Volatile.Read(ref done) / maps.Count))
        yield return step;
    }

    // Maps that nothing sampled holds: the ground colours (terrain map) and which vegetation and creatures may appear
    // where (vegetation and spawn maps). They go into the folder itself as well, under the names Directory loads, so the
    // rebuilt world keeps them. Every other map is rebuilt from what was sampled; the heightmap already holds what the
    // rough map did, and a new world ignores a rough map under Heightmap Override All anyway.
    private static readonly string[] CarriedForward = ["terrainmap.png", "vegetationmap.png", "spawnmap.png"];

    // Worker thread (SourcesPass): only a PNG image counts, and a legend without its image is taken back out.
    private void CarryForward(List<(string FileName, ImageMapBase Map)> maps)
    {
      var copied = new List<string>();
      foreach (var (file, map) in maps)
      {
        if (!CarriedForward.Contains(file))
          continue;
        var stem = Path.GetFileNameWithoutExtension(file);
        var wrote = new List<string>();
        void Top(string name, Action<string> write)
        {
          if (name != stem + ".png" && name != stem + ".txt")
            return;
          var path = Path.Combine(Dir, name);
          lock (Tracked)
            Tracked.Add(path);
          write(path);
          wrote.Add(name);
        }
        try
        {
          WriteSource(file, map, Top);
          if (wrote.Contains(stem + ".png"))
          {
            lock (Written)
              Written.AddRange(wrote);
            copied.Add(file);
          }
          else
          {
            foreach (var name in wrote)
              File.Delete(Path.Combine(Dir, name));
            lock (Notes)
              Notes.Add($"Sources: {file} is not a PNG in this world's settings, so it is only in sources/ and the rebuilt world does not load it.");
          }
        }
        catch (Exception e)
        {
          lock (Notes)
            Notes.Add($"Sources: {file} could not be copied into the folder ({e.Message}), so the rebuilt world does not load it.");
        }
      }
      if (copied.Count > 0)
        lock (Notes)
          Notes.Add($"Sources: {string.Join(", ", copied)} {(copied.Count == 1 ? "was" : "were")} copied into the folder itself, so the rebuilt world keeps {(copied.Count == 1 ? "it" : "them")}.");
    }

    // A map's source as the world keeps it: the original image where the settings hold it (with its legend), else the
    // decoded map written back out as an image. Only file names Better Continents itself would use.
    private void WriteSource(string file, ImageMapBase map, Action<string, Action<string>> output)
    {
      var stem = Path.GetFileNameWithoutExtension(file);
      string? ImageExtension()
      {
        if (map.SourceData == null || map.SourceData.Length < 8)
          return null;
        var format = ISImage.DetectFormat(map.SourceData);
        return format == null ? null : "." + (format.FileExtensions.FirstOrDefault() ?? "img");
      }
      void Original(string ext) => output(stem + ext, p => File.WriteAllBytes(p, map.SourceData));
      void Legend(IEnumerable<string> lines) => output(stem + ".txt", p => WorldExportPng.WriteText(p, lines));
      var ext = ImageExtension();
      switch (map)
      {
        case ImageMapAltBiome alt:
          {
            // The settings keep the decoded classes, never the image (ImageMapAltBiome.Create drops it).
            int size = alt.Size;
            if (size <= 0 || alt.Map.Length != size * size)
              break;
            var classColours = alt.Classes.Select(c => new Rgb24(c.Color.r, c.Color.g, c.Color.b)).ToArray();
            var pixels = new Rgb24[size * size];
            for (int y = 0; y < size; y++)
              for (int x = 0; x < size; x++)
              {
                var cls = alt.Map[y * size + x];
                if (cls != 0 && cls < classColours.Length)
                  pixels[WorldExportMath.FlipRow(y, size) * size + x] = classColours[cls];
              }
            output(stem + ".png", p => WorldExportPng.SaveRgb24(p, pixels, size));
            Legend(alt.Legend.Replace("\r\n", "\n").Split('\n'));
            break;
          }
        case ImageMapBiome biome:
          {
            var colours = biome.LegendColors.Count > 0 ? biome.LegendColors : ImageMapBiome.DefaultColorTable();
            var legend = colours.Select(kv => $"{kv.Key}: {kv.Value.r},{kv.Value.g},{kv.Value.b},{kv.Value.a}").ToList();
            if (ext != null)
              Original(ext);
            else
            {
              int size = biome.Size;
              var grid = biome.Biomes;
              if (size <= 0 || grid.Length != size * size)
                break;
              var pixels = new Rgba32[size * size];
              for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                  var b = grid[y * size + x];
                  var c = colours.TryGetValue(b, out var found) ? found : new Color32(0, 0, 0, 255);
                  pixels[WorldExportMath.FlipRow(y, size) * size + x] = new Rgba32(c.r, c.g, c.b, c.a);
                }
              output(stem + ".png", p => WorldExportPng.SaveRgba32(p, pixels, size));
            }
            Legend(legend);
            break;
          }
        case ImageMapLocation location:
          {
            if (ext != null)
            {
              Original(ext);
              if (location.LegendColors.Count > 0)
                Legend(location.LegendColors.Select(kv => $"{kv.Key}: {kv.Value.r},{kv.Value.g},{kv.Value.b}"));
            }
            else
            {
              // The settings keep only the positions the map chose, as fractions of the map.
              var lines = new List<string> { "# Location positions from the world's location map (world x, z in metres)." };
              foreach (var kv in location.RemainingAreas)
                foreach (var p in kv.Value)
                  lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.##}, {2:0.##}", kv.Key, (p.x - 0.5f) * Total, (p.y - 0.5f) * Total));
              output(stem + "-positions.txt", p => WorldExportPng.WriteText(p, lines));
            }
            break;
          }
        case ImageMapSpawn spawn:
          {
            var legend = new List<string>();
            for (int i = 1; i < spawn.LegendColors.Count && i < spawn.LegendEntries.Count; i++)
            {
              var c = spawn.LegendColors[i];
              legend.Add($"{c.r},{c.g},{c.b},{c.a}: {spawn.LegendEntries[i].Data}");
            }
            if (ext != null)
              Original(ext);
            else
            {
              var indices = spawn.Indices;
              int size = spawn.Size;
              if (size <= 0 || indices.Length != size * size)
                break;
              var pixels = new Rgba32[size * size];
              for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                  int k = indices[y * size + x];
                  var c = k < spawn.LegendColors.Count ? spawn.LegendColors[k] : new Color32(0, 0, 0, 255);
                  pixels[WorldExportMath.FlipRow(y, size) * size + x] = new Rgba32(c.r, c.g, c.b, c.a);
                }
              output(stem + ".png", p => WorldExportPng.SaveRgba32(p, pixels, size));
            }
            Legend(legend);
            break;
          }
        case ImageMapColor colour:
          if (ext != null)
            Original(ext);
          Legend(colour.SourceColors.Split('|'));
          break;
        default:
          if (ext != null)
            Original(ext);
          break;
      }
    }

    // ---- export.cfg, README.txt, manifest.json -----------------------------------------------------------------

    private IEnumerable TextPass(List<string> config)
    {
      Begin("Writing export.cfg, README.txt and manifest.json", 0.05);
      var cfg = ConfigHeader();
      cfg.AddRange(config);
      var readme = ReadmeLines();
      // Tracked and listed before the manifest is built, so it lists itself and the other two.
      foreach (var name in new[] { "export.cfg", "README.txt", "manifest.json" })
      {
        Tracked.Add(Path.Combine(Dir, name));
        Written.Add(name);
      }
      var manifest = ManifestJson();
      var task = Task.Run(() =>
      {
        WorldExportPng.WriteText(Path.Combine(Dir, "export.cfg"), cfg);
        WorldExportPng.WriteText(Path.Combine(Dir, "README.txt"), readme);
        // Last: a folder with a manifest.json is a finished export.
        WorldExportPng.WriteText(Path.Combine(Dir, "manifest.json"), [manifest]);
      });
      // Text from memory only: a world unloaded by now does not fail the export.
      foreach (var step in Await(task, () => 50f, needsWorld: false))
        yield return step;
    }

    // ---- the New World preset ----------------------------------------------------------------------------------

    // bc_import's builder, on this folder and these settings lines (export.cfg is written after it, and says how it went).
    // A preset that cannot be made never fails the export: every map is written by now.
    private IEnumerable PresetPass(List<string> config)
    {
      if (!O.Preset)
      {
        PresetSkipped = OnDedicatedServer()
          ? "a dedicated server has no New World screen: copy the folder to a game and run bc_import there"
          : "switched off for this export";
        yield break;
      }
      Begin("Making the New World preset", W(0.8, 0.2));
      WorldImport.Plan? plan = null;
      try
      {
        plan = WorldImport.MakePlan(Dir, config, PresetName);
      }
      catch (Exception e)
      {
        PresetSkipped = e.Message;
      }
      if (plan == null)
      {
        Notes.Add($"Preset: not made ({PresetSkipped}); 'bc_import {ImportArg}' makes it from this folder.");
        LogWarning($"World export: the New World preset could not be prepared: {PresetSkipped}");
        Done();
        yield break;
      }
      // A cancel from here on deletes the preset with the maps.
      Tracked.Add(plan.PresetPath);
      Tracked.Add(plan.ThumbnailPath);
      Tracked.Add(plan.SourcePath);
      var built = plan;
      var task = Task.Run(() => WorldImport.BuildPreset(built, () => cancelRequested));
      // The preset needs only the files, so it finishes even when the world is left meanwhile.
      foreach (var step in Await(task, () => built.Progress, needsWorld: false))
        yield return step;
      PresetOutcome = task.Result;
      if (PresetOutcome.Error != null)
      {
        Notes.Add($"Preset: not made ({PresetOutcome.Error}); 'bc_import {ImportArg}' makes it from this folder.");
        LogWarning($"World export: the New World preset could not be made: {PresetOutcome.Error}");
      }
      else
      {
        foreach (var w in PresetOutcome.Warnings)
          Notes.Add("Preset: " + w);
        Presets.RefreshActive();
      }
    }

    // What bc_import is given for this folder: BetterContinents/<world>/export-<time> by its short name.
    private string ImportArg
    {
      get
      {
        var parent = Path.GetDirectoryName(Dir) ?? "";
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(parent) ?? ""), "BetterContinents", StringComparison.OrdinalIgnoreCase)
          ? $"{Path.GetFileName(parent)}/{Path.GetFileName(Dir)}"
          : ConfigDir;
      }
    }

    private string ConfigDir => Dir.Replace('\\', '/');

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string B(bool v) => v ? "true" : "false";

    // The top of export.cfg: what the file is and the three ways to use it, for someone who opens it first.
    private List<string> ConfigHeader()
    {
      var l = new List<string>
      {
        $"## Better Continents world export of \"{World.m_name}\" (seed \"{World.m_seedName}\"), {Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.",
        "##",
        "## WHAT THIS FILE IS: the Better Continents settings that rebuild this world from the maps in this folder.",
        "## You do not have to edit it. Three ways to make a new world from the export (README.txt has the steps):",
      };
      if (PresetMade)
        l.Add($"##  1. Easiest: in the New World screen, pick the Better Continents preset \"{PresetName}\" and create the world.");
      else
        l.Add($"##  1. The preset: the export did not make it ({PresetSkipped ?? PresetOutcome?.Error ?? "switched off"}); way 2 makes it.");
      l.Add($"##  2. After editing the PNGs: open the console (F5) and type   bc_import {ImportArg}");
      l.Add("##     It makes the preset again from the PNGs as they are now, and selects it.");
      l.Add($"##  3. To keep editing the PNGs: type   bc_import {ImportArg} config");
      l.Add("##     or paste every line of this file at the END of BepInEx/config/BetterContinents.cfg (a later line wins) and");
      l.Add("##     save it. Better Continents reads the change at once; create the world with the preset \"From Config\".");
      l.Add("## Only NEW worlds use these settings. A world that exists keeps the maps it was created with.");
      return l;
    }

    private List<string> ConfigLines()
    {
      var l = new List<string>();
      void Section(string name)
      {
        l.Add("");
        l.Add($"[{name}]");
      }
      void Key(string key, string value, string why)
      {
        l.Add("");
        l.Add("## " + why);
        l.Add($"{key} = {value}");
      }
      bool fullLocations = LocationsWritten && LocationsFull && LocationsGenerated;

      Section("00 BetterContinents.Debug");
      Key("Enabled", "true", "Better Continents on for the new world.");
      Key("Directory", ConfigDir, "Loads every map in this export folder by its standard name, and no map from anywhere else.");
      Key("Override version", "", "Empty: the new world uses the current settings format (the V3 height formula this export is encoded for).");

      Section("01 BetterContinents.Global");
      Key("World Size", F(WorldSizeSetting), "The loaded world's playable radius.");
      Key("Edge Size", F(EdgeSizeSetting), "The loaded world's edge width.");
      Key("Map Edge Drop-off", B(EdgeDropoff), EdgeDropoff
        ? "On: the heightmap holds the edge ring as it is before the drop-off, and Better Continents applies it once. Keeps the world edge (ship push, edge kill)."
        : "Off: the edge is already in the heightmap. Also removes the world edge (no ship push or edge kill); the outermost pixels continue past it.");
      Key("Skip Default Locations", B(fullLocations), fullLocations
        ? "The location map holds every location, so the game places no others."
        : "The location map (if any) is incomplete, so the game places the rest.");
      if (HeightsWritten)
      {
        Key("Sea Level Adjustment", F(O.SeaLevel), "The sea level the heights are encoded for.");
        Key("Rivers", "false", "The heightmap overrides the biome height formulas, rivers included; the loaded world's rivers are in it.");
        Key("Mountains Allowed At Center", "true", "The loaded world's centre is already in the heightmap; no second clamp.");
      }

      if (HeightsWritten)
      {
        Section("02 BetterContinents.Heightmap");
        Key("Heightmap Amount", F(O.HeightmapAmount), "metres = (pixel / 65535 * amount - 0.15 + Lerp(1, -1, sea level)) * 200.");
        Key("Heightmap Blend", "1", "The heightmap is the height, not blended with generated terrain.");
        Key("Heightmap Add", "0", "Nothing added on top.");
        Key("Heightmap Mask", "0", "No masking.");
        Key("Heightmap Override All", "true", "Every biome takes its height from the heightmap.");
        Key("Heightmap Alpha", "false", "heightmap.png is plain 16-bit grey.");
      }

      Section("03 BetterContinents.Biomemap");
      var precision = EffectiveBiomePrecision(Settings);
      Key("Biome precision", precision.ToString(CultureInfo.InvariantCulture), precision > 0
        ? $"As the loaded world: the ground follows the biomes on {precision + 1} x {precision + 1} cells per 64 m terrain zone."
        : "As the loaded world: each 64 m terrain zone takes its biomes from its 4 corners (vanilla).");

      Section("04 BetterContinents.Forest");
      Key("Forest Scale", ForestScaleText, "The loaded world's forest scale, so the game's own forest noise lines up with forestmap.png.");
      if (ForestWritten)
      {
        Key("Forest Amount", "0.5", "Neutral: the loaded world's forest amount is already in forestmap.png.");
        Key("Forestmap Multiply", O.ForestExact ? "1" : "0", O.ForestExact
          ? "Exact encoding: forest = forestmap * (game forest + 1)."
          : "Additive encoding: forest = game forest + forestmap (black = the game's own forest).");
        Key("Forestmap Add", "1", "See Forestmap Multiply.");
      }
      else
        Key("Forest Amount", F(SourceForestAmount), "The loaded world's forest amount (no forestmap was exported).");
      Key("Forest Factor Overrides All Trees", B(ForestOverridesAllTrees), "As the loaded world.");

      Section("05 BetterContinents.StartPosition");
      if (LocationsWritten)
        Key("Override Start Position", "false", "The start temple is in the location map.");
      else if (BcWorld && Settings.OverrideStartPosition)
      {
        Key("Override Start Position", "true", "As the loaded world.");
        Key("Start Position X", F(Settings.StartPositionX), "As the loaded world.");
        Key("Start Position Y", F(Settings.StartPositionY), "As the loaded world.");
      }
      else
        Key("Override Start Position", "false", "As the loaded world.");

      if (HeatWritten)
      {
        Section("06 BetterContinents.Maps");
        Key("Heatmap Scale", F(O.HeatScale), "heat = pixel / 65535 * scale; Ashlands where it is above 0.");
      }

      Section("07 BetterContinents.Misc");
      Key("SelectedPreset", "From Config", "Makes the new-world preset \"From Config\", i.e. these settings. Anything else creates the world without them.");

      if (AltBiomesWritten)
      {
        Section("08 BetterContinents.AltBiomes");
        Key("Mode", "PlantedOnly", "The alt-biome map holds every alt biome as placed, so the game places none at random.");
        Key("Grid", AltGrid.ToString(), "As the loaded world.");
      }
      return l;
    }

    // README.txt: how to make a world from the export, for someone who has never done it, then what every file is,
    // how to edit and combine exports, the export's notes, and the numbers.
    private List<string> ReadmeLines()
    {
      int n = Size;
      float mpp = Total / (n - 1);
      var wl = WorldExportMath.WaterlineValue(O.HeightmapAmount, O.SeaLevel);
      var floor = WorldExportMath.ValueToMetres(0f, O.HeightmapAmount, O.SeaLevel);
      var ceiling = WorldExportMath.ValueToMetres(1f, O.HeightmapAmount, O.SeaLevel);
      var seed = World.m_seedName;
      var l = new List<string>
      {
        $"Better Continents world export of \"{World.m_name}\" (seed \"{seed}\"), {Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)},",
        $"Better Continents {ModInfo.Version}, Valheim {Version.GetVersionString()}, sampled on the {Role}.",
        $"Every image is {n} x {n} pixels over {Inv(Total)} m ({Inv(mpp)} m per pixel), north at the top.",
        "",
        "MAKE A NEW WORLD FROM THIS EXPORT - pick one of the three ways",
        "",
      };
      if (PresetMade)
      {
        l.Add($"  A. The preset (easiest). The export made the New World preset \"{PresetName}\".");
        l.Add("     1. In the main menu choose Start, then New World.");
        l.Add($"     2. In the Better Continents box under the seed, pick \"{PresetName}\".");
        l.Add($"     3. Name the world, type the seed \"{seed}\" for the closest match, and press Create.");
        l.Add("     The preset is a copy of this folder as it was when the export finished, so it keeps working if you");
        l.Add("     move or delete the folder, and it does not see edits made to the PNGs afterwards: for those use B.");
      }
      else
        l.Add($"  A. The preset. The export did not make it ({PresetSkipped ?? PresetOutcome?.Error ?? "switched off"}); way B makes it.");
      l.Add("");
      l.Add("  B. bc_import, for example after you have edited the PNGs.");
      l.Add("     1. Open the console with F5, in the main menu or in a world.");
      l.Add($"     2. Type   bc_import {ImportArg}");
      l.Add("        (or just  bc_import  for your newest export, or  bc_import list  and then  bc_import <number>).");
      l.Add($"     3. That makes the preset \"{PresetName}\" again from the PNGs as they are now and selects it.");
      l.Add("        Then choose Start, New World, and Create.");
      l.Add("");
      l.Add("  C. The config, if you keep editing the PNGs and making worlds from them.");
      l.Add($"     1. Type   bc_import {ImportArg} config   in the console. It copies export.cfg into");
      l.Add("        BepInEx/config/BetterContinents.cfg (your old file is kept beside it, ending in .before-import-<time>)");
      l.Add("        and selects the preset \"From Config\". Or paste export.cfg at the END of that file yourself and save");
      l.Add("        it: Better Continents reads the file again within a second, with no restart.");
      l.Add("     2. Every New World you then create with \"From Config\" reads the PNGs in this folder as they are then.");
      l.Add("");
      l.Add("  A world that already exists keeps the maps it was created with. With the same seed, vegetation, fine lava");
      l.Add("  detail and the random layout of things come out closest to the exported world.");
      l.Add("");
      l.Add("WHAT EACH FILE IS");
      void Entry(string name, string what)
      {
        if (Written.Contains(name))
          l.Add($"  {name,-17} {what}");
      }
      Entry("heightmap.png", $"How high the ground is (16-bit grey): black {Inv(floor, "0.#")} m, white {Inv(ceiling, "0.#")} m, the sea (30 m) {Inv(wl, "0.####")}.");
      Entry("biomemap.png", "Which biome is where, in Better Continents' biome colours (biomemap.txt lists them).");
      Entry("locationmap.png", "One dot per location: start temple, bosses, traders, dungeons (locationmap.txt names the colours).");
      Entry("forestmap.png", ForestWritten && O.ForestExact
        ? "How dense the forest is (16-bit grey): density = value x (the game's own density + 1)."
        : "Forest added to the game's own (16-bit grey): black keeps the game's forest, lighter adds trees.");
      Entry("heatmap.png", $"The Ashlands heat (16-bit grey): heat = value x {Inv(O.HeatScale)}; Ashlands wherever it is above black.");
      Entry("altbiomemap.png", "The alt biomes (Dark Meadows, Wolf Mountain...) exactly as placed (altbiomemap.txt names them).");
      Entry("lavamap.png", "Where Ashlands lava is (8-bit grey).");
      Entry("mossmap.png", "The Mistlands ground cover (8-bit grey).");
      Entry("paintmap.png", "The ground paint of every biome (snow depth and the like), a colour per pixel.");
      Entry("terrainmap.png", "This world's own ground colour map, copied so the rebuilt world keeps it (terrainmap.txt).");
      Entry("vegetationmap.png", "This world's own vegetation map: which plants may grow where (vegetationmap.txt).");
      Entry("spawnmap.png", "This world's own spawn map: which creatures may appear where (spawnmap.txt).");
      if (Written.Any(w => w.StartsWith("sources/")))
        l.Add($"  {"sources/",-17} The loaded world's own Better Continents maps as its settings keep them (not loaded).");
      l.Add($"  {"export.cfg",-17} The settings that load this folder (way C). It keeps the world's Biome precision ({EffectiveBiomePrecision(Settings)}):");
      l.Add($"  {"",-17} how closely the ground follows the biome map inside each 64 m terrain zone.");
      l.Add($"  {"manifest.json",-17} Every number about this export, for tools.");
      l.Add("  Delete a map from the folder, then remake the preset (B), to let the game make that part itself.");
      l.Add("");
      l.Add("EDITING, AND CUTTING AND PASTING BETWEEN EXPORTS");
      l.Add("  - Edit the PNGs in any image editor. Keep them square PNGs of the same size, and keep heightmap.png,");
      l.Add("    forestmap.png and heatmap.png 16-bit grey (an 8-bit save turns smooth slopes into steps).");
      l.Add("  - Two exports line up pixel for pixel when they have the same size, heightmap amount and sea level");
      l.Add("    (size, heightmapAmount, seaLevel and totalSize in manifest.json). Then an area cut from one pastes into the");
      l.Add("    same place of the other, and the heights there come out the same.");
      l.Add("  - Paste the same area from every map (heights, biomes, forest, lava, moss, heat, alt biomes) so they stay");
      l.Add("    matched, and copy the legend lines (.txt) of any colour the other export's legend lacks.");
      l.Add("  - Heights are read smoothly, so a seam blends over one pixel; a biome is the one most of the 4 nearest");
      l.Add("    pixels have.");
      l.Add("  - A location dot must stay one pixel that touches no other dot of its colour.");
      l.Add("  - Forest is added to the game's own forest at the new place, so pasted woods keep their extra trees, but the");
      l.Add("    game's pattern under them changes.");
      l.Add("  - Only the generated world is exported: buildings, terrain edits and placed objects are not.");
      var notes = Notes.Where(x => !x.StartsWith("Only the generated world")).ToList();
      if (notes.Count > 0)
      {
        l.Add("");
        l.Add("NOTES ON THIS EXPORT");
        foreach (var note in notes)
          l.Add("  - " + note);
      }
      l.Add("");
      l.Add("THE NUMBERS");
      if (HeightsWritten)
      {
        l.Add($"  Heights: metres = (v x {Inv(O.HeightmapAmount)} - 0.15 + {Inv(Sla)}) x 200, v = pixel / 65535.");
        if (EdgeDropoff)
          l.Add($"  Past {Inv(WorldR)} m the heightmap holds the ground before Better Continents' edge drop-off, which rebuilds the edge.");
      }
      l.Add($"  Pixel (column c, row r) is world x = (c / {n - 1} - 0.5) x {Inv(Total)}, z = (0.5 - r / {n - 1}) x {Inv(Total)}.");
      if (LocationsWritten)
        l.Add($"  The location map alone uses c / {n} and r / {n} (Better Continents' location loader): up to a pixel off the rest.");
      return l;
    }

    private string ManifestJson()
    {
      int n = Size;
      var files = Written.ToList();
      var biomes = new WorldExportJson.Obj();
      for (int k = 1; k < BiomePixels.Length; k++)
        if (BiomePixels[k] > 0)
          biomes.Add(((Heightmap.BiomeIndex)k).ToString(), BiomePixels[k]);
      var m = new WorldExportJson.Obj
      {
        { "format", Format },
        { "modVersion", ModInfo.Version },
        { "gameVersion", Version.GetVersionString() },
        { "exportedAt", Started.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
        { "seconds", Math.Round(Clock.Elapsed.TotalSeconds, 1) },
        { "worldName", World.m_name },
        { "seedName", World.m_seedName },
        { "seed", World.m_seed },
        { "betterContinentsWorld", BcWorld },
        { "sampledOn", Role },
        { "size", n },
        { "totalSize", Total },
        { "worldRadius", WorldR },
        { "totalRadius", TotalR },
        { "metresPerPixel", Total / (n - 1) },
        { "pixelToWorld", "x = (column / (size - 1) - 0.5) * totalSize; z = (0.5 - row / (size - 1)) * totalSize; row 0 is north" },
        { "heightmapAmount", O.HeightmapAmount },
        { "seaLevel", O.SeaLevel },
        { "seaLevelAdjustment", Sla },
        { "heightEncoding", "metres = (pixel / 65535 * heightmapAmount - 0.15 + seaLevelAdjustment) * 200" },
        { "waterlineValue", Math.Round(WorldExportMath.WaterlineValue(O.HeightmapAmount, O.SeaLevel), 6) },
        { "floorMetres", Math.Round(WorldExportMath.ValueToMetres(0f, O.HeightmapAmount, O.SeaLevel), 3) },
        { "ceilingMetres", Math.Round(WorldExportMath.ValueToMetres(1f, O.HeightmapAmount, O.SeaLevel), 3) },
        { "clippedLow", HeightsWritten ? ClippedLow : null },
        { "clippedHigh", HeightsWritten ? ClippedHigh : null },
        { "edgeRingClipped", HeightsWritten ? RingClipped : null },
        { "heightRangeMetres", HeightsWritten && !float.IsNaN(MinMetres) ? new object[] { MinMetres, MaxMetres } : null },
        { "zoneBlend", ZoneBlend },
        { "edgeDropoff", EdgeDropoff },
        { "forest", !ForestWritten ? null : new WorldExportJson.Obj
          {
            { "encoding", O.ForestExact ? "exact" : "additive" },
            { "multiply", O.ForestExact ? 1 : 0 },
            { "add", 1 },
            { "forestScale", ForestScaleText },
            { "clippedLow", ForestClippedLow },
            { "clippedHigh", ForestClippedHigh },
          } },
        { "heat", !HeatWritten ? null : new WorldExportJson.Obj
          {
            { "scale", O.HeatScale },
            { "ashlandsPixels", HeatPixels },
            { "clipped", HeatClipped },
          } },
        { "altBiomes", !AltBiomesWritten ? null : new WorldExportJson.Obj
          {
            { "classes", AltClassCount },
            { "regions", AltRegions },
            { "forcedClasses", AltForced },
            { "droppedRegions", AltDropped },
          } },
        { "locations", !LocationsWritten ? null : new WorldExportJson.Obj
          {
            { "complete", LocationsFull && LocationsGenerated },
            { "instances", LocationCount },
            { "types", LocationTypes },
            { "written", LocationWritten },
            { "nudged", LocationNudged },
            { "dropped", LocationDropped },
            { "outsideMap", LocationOutside },
            { "pixelToWorld", "x = (column / size - 0.5) * totalSize; z = (0.5 - (row + 1) / size) * totalSize" },
          } },
        { "biomePixels", Written.Contains("biomemap.png") ? biomes : null },
        { "preset", !O.Preset ? null : new WorldExportJson.Obj
          {
            { "name", PresetName },
            { "file", PresetOutcome?.PresetPath },
            { "made", PresetMade },
            { "error", PresetMade ? null : PresetOutcome?.Error ?? PresetSkipped },
            { "howToUse", PresetMade
              ? "New World screen: pick this preset in the Better Continents box. It is a copy of the folder as exported."
              : "bc_import makes it: see README.txt." },
          } },
        { "import", $"bc_import {ImportArg}" },
        { "files", files },
        { "notes", Notes.ToList() },
      };
      return WorldExportJson.Write(m);
    }

    // ---- finishing ---------------------------------------------------------------------------------------------

    public void Finish(Exception? failure, bool cancelled)
    {
      if (failure == null && !cancelled)
      {
        LastExportDir = Dir;
        LastError = null;
        LastSummary = Summary();
        Phase = "Done";
        Progress = 100f;
        Overall = 100f;
        Say($"World export finished in {Clock.Elapsed.TotalSeconds:0} s: {Dir}");
        foreach (var line in LastSummary)
          Say("World export: " + line);
        Say("World export: README.txt in the folder says how to make a world from it, in three ways.");
        return;
      }
      DeletePartial();
      if (failure == null)
      {
        Phase = "Cancelled";
        Say("World export cancelled; its partial files were deleted.");
      }
      else
      {
        Phase = "Failed";
        LastError = failure.Message;
        LogError($"World export failed: {failure}");
        Say($"World export failed: {failure.Message}. Its partial files were deleted.", warning: true);
      }
    }

    // What the export came to, a line each, for the console and the HUD's "Last export" panel.
    private List<string> Summary()
    {
      var lines = new List<string>();
      if (HeightsWritten && !float.IsNaN(MinMetres))
      {
        var clipped = ClippedLow + ClippedHigh;
        lines.Add($"heights {MinMetres:0.#} m to {MaxMetres:0.#} m inside the world; "
                  + (clipped == 0 ? "no pixel clipped." : $"{ClippedLow} pixel(s) clipped low and {ClippedHigh} high (a larger heightmap amount keeps them)."));
      }
      if (LocationsWritten)
      {
        var line = $"{LocationWritten} of {LocationCount} locations in the location map";
        if (LocationDropped > 0)
          line += $", {LocationDropped} dropped (no free pixel)";
        if (!LocationsFull)
          line += "; WARNING: sampled on a client, which knows only the map-icon locations, so the game places the rest";
        else if (!LocationsGenerated)
          line += "; WARNING: the world was still placing locations, so some may be missing";
        lines.Add(line + ".");
      }
      else if (O.Locations)
        lines.Add("no location map (no locations known here): the game places them in the new world.");
      if (AltBiomesWritten)
        lines.Add($"alt biomes: {AltRegions} region(s) in {AltClassCount} colour(s){(AltDropped > 0 ? $", {AltDropped} left out" : "")}.");
      if (ForestWritten && ForestClippedLow > 0)
        lines.Add($"forest: {ForestClippedLow} pixel(s) had less forest than the game makes there and come back at its density (the exact forest encoding keeps them).");
      if (PresetMade)
        lines.Add($"New World preset \"{PresetName}\" is ready: pick it under Better Continents when you create a world.");
      else if (O.Preset)
        lines.Add($"WARNING: the New World preset was not made ({PresetOutcome?.Error ?? PresetSkipped}); 'bc_import {ImportArg}' makes it.");
      int notes = Notes.Count(x => !x.StartsWith("Only the generated world"));
      if (notes > 0)
        lines.Add($"{notes} note(s) in README.txt.");
      return lines;
    }

    private void DeletePartial()
    {
      for (int i = Tracked.Count - 1; i >= 0; i--)
      {
        foreach (var path in new[] { Tracked[i], Tracked[i] + ".tmp" })
        {
          try
          {
            if (File.Exists(path))
              File.Delete(path);
          }
          catch (Exception e)
          {
            LogWarning($"World export: could not delete {path}: {e.Message}");
          }
        }
      }
      TryRemoveEmpty(Path.Combine(Dir, "sources"), CreatedSources);
      TryRemoveEmpty(Dir, CreatedDir);
    }

    private static void TryRemoveEmpty(string dir, bool created)
    {
      try
      {
        if (created && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
          Directory.Delete(dir);
      }
      catch (Exception e)
      {
        LogWarning($"World export: could not remove {dir}: {e.Message}");
      }
    }
  }
}

// PNG and text output. ImageSharp (merged into the plugin) writes the 16-bit maps Unity's encoder cannot. The pixel
// arrays are wrapped, not copied, and every file goes to <name>.tmp first and is renamed when complete, so a crash
// never leaves a half-written map under a name Better Continents would load.
internal static class WorldExportPng
{
  private static PngEncoder Encoder(PngColorType type, PngBitDepth depth) => new()
  {
    ColorType = type,
    BitDepth = depth,
    CompressionLevel = PngCompressionLevel.DefaultCompression,
    // No gAMA, pHYs or text chunks: an editor that honours gAMA shifts the exact colours the legends match on.
    ChunkFilter = PngChunkFilter.ExcludeAll,
  };

  public static void SaveL16(string path, L16[] pixels, int size) => Save(path, pixels, size, Encoder(PngColorType.Grayscale, PngBitDepth.Bit16));

  public static void SaveL8(string path, L8[] pixels, int size) => Save(path, pixels, size, Encoder(PngColorType.Grayscale, PngBitDepth.Bit8));

  public static void SaveRgb24(string path, Rgb24[] pixels, int size) => Save(path, pixels, size, Encoder(PngColorType.Rgb, PngBitDepth.Bit8));

  public static void SaveRgba32(string path, Rgba32[] pixels, int size) => Save(path, pixels, size, Encoder(PngColorType.RgbWithAlpha, PngBitDepth.Bit8));

  private static void Save<T>(string path, T[] pixels, int size, PngEncoder encoder) where T : unmanaged, IPixel<T>
  {
    var tmp = path + ".tmp";
    using (var image = ISImage.WrapMemory(ISConfiguration.Default, new Memory<T>(pixels), size, size))
    using (var stream = File.Create(tmp))
      image.Save(stream, encoder);
    Replace(tmp, path);
  }

  public static void WriteText(string path, IEnumerable<string> lines)
  {
    var tmp = path + ".tmp";
    File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
    Replace(tmp, path);
  }

  private static void Replace(string tmp, string path)
  {
    if (File.Exists(path))
      File.Delete(path);
    File.Move(tmp, path);
  }
}

// A minimal JSON writer for manifest.json (no serializer ships with the game that the plugin could rely on).
internal static class WorldExportJson
{
  // An object that keeps its keys in insertion order.
  public sealed class Obj : List<KeyValuePair<string, object?>>
  {
    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));
  }

  public static string Write(object? value)
  {
    var sb = new StringBuilder();
    Write(sb, value, 0);
    return sb.ToString();
  }

  private static void Write(StringBuilder sb, object? value, int indent)
  {
    switch (value)
    {
      case null:
        sb.Append("null");
        break;
      case string s:
        Quote(sb, s);
        break;
      case bool b:
        sb.Append(b ? "true" : "false");
        break;
      case float f:
        sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture));
        break;
      case double d:
        sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
        break;
      case int or long or short or byte or uint or ulong or ushort:
        sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        break;
      case Obj obj:
        if (obj.Count == 0)
        {
          sb.Append("{}");
          break;
        }
        sb.Append("{\n");
        for (int i = 0; i < obj.Count; i++)
        {
          sb.Append(' ', 2 * (indent + 1));
          Quote(sb, obj[i].Key);
          sb.Append(": ");
          Write(sb, obj[i].Value, indent + 1);
          sb.Append(i + 1 < obj.Count ? ",\n" : "\n");
        }
        sb.Append(' ', 2 * indent).Append('}');
        break;
      case IEnumerable list:
        var items = list.Cast<object?>().ToList();
        if (items.Count == 0)
        {
          sb.Append("[]");
          break;
        }
        bool simple = items.All(x => x is not Obj && (x is string || x is not IEnumerable));
        if (simple && items.All(x => x is not string))
        {
          sb.Append('[');
          for (int i = 0; i < items.Count; i++)
          {
            if (i > 0)
              sb.Append(", ");
            Write(sb, items[i], indent + 1);
          }
          sb.Append(']');
          break;
        }
        sb.Append("[\n");
        for (int i = 0; i < items.Count; i++)
        {
          sb.Append(' ', 2 * (indent + 1));
          Write(sb, items[i], indent + 1);
          sb.Append(i + 1 < items.Count ? ",\n" : "\n");
        }
        sb.Append(' ', 2 * indent).Append(']');
        break;
      default:
        Quote(sb, value.ToString());
        break;
    }
  }

  private static void Quote(StringBuilder sb, string s)
  {
    sb.Append('"');
    foreach (var c in s)
    {
      switch (c)
      {
        case '"': sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (c < 0x20)
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          else
            sb.Append(c);
          break;
      }
    }
    sb.Append('"');
  }
}

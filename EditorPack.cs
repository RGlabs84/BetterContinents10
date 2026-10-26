// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// The editor pack: a folder editor/ inside a world export with the ground as 32-bit floats (TIFF, raw .r32) and masks, for
// Gaea and other heavy terrain editors, and editor.json, which says how the pixels map to the world. Everything else an
// export writes is untouched, and a default export (no `editor`) is byte for byte what it was. Nothing in editor/ is read by
// the game; EditorIngest brings an edited height back as a heightmap.png.
//
// This file is the export side. It is part of WorldExport and of its Options and Job, so the pass has the job's folder,
// workers, cancel flag and notes; WorldExport.cs only calls the few hooks at the end of this file.
public static partial class WorldExport
{
  /// <summary>The float containers the editor pack's heights are written in (EXR is planned).</summary>
  [Flags]
  public enum EditorFormat
  {
    None = 0,
    /// <summary>height.tif: classic TIFF, 32-bit float, uncompressed.</summary>
    Tiff = 1,
    /// <summary>height.r32: headerless 32-bit floats, clamped to 0..1.</summary>
    Raw32 = 2,
  }

  /// <summary>The masks of the editor pack (editor/masks/*.png).</summary>
  [Flags]
  public enum EditorMask
  {
    None = 0,
    /// <summary>water.png: 255 where the ground is under the game's 30 m water plane.</summary>
    Water = 1,
    /// <summary>water-inland.png: water that is not the Ocean biome (lakes, rivers, marsh).</summary>
    Inland = 2,
    /// <summary>slope.png: the slope in degrees / 90, 16-bit.</summary>
    Slope = 4,
    /// <summary>forest-density.png: how dense the forest is, 0 bare to 1 densest, 16-bit.</summary>
    Forest = 8,
    /// <summary>biome-&lt;name&gt;.png: 255 where the biome is, one file for each biome that has pixels.</summary>
    Biomes = 16,
    /// <summary>biome-index.png: the biome's index (BiomeRegistry), with biome-index.txt.</summary>
    Index = 32,
    /// <summary>biome-soft-&lt;name&gt;.png: how much of the ground's height blend each biome has, 16-bit.</summary>
    Soft = 64,
    /// <summary>protect-locations.png: discs around every location.</summary>
    Protect = 128,
    Default = Water | Inland | Slope | Forest | Biomes | Index | Protect,
    All = Default | Soft,
  }

  /// <summary>What the editor pack's tests and a client without locations replace: the discs around locations as (x, z, radius).</summary>
  internal static Func<List<(float X, float Z, float Radius)>>? EditorDiscSource;

  /// <summary>Rows in a band of the editor pass; 0 (the usual) picks about a million pixels. A test sets it to prove that the bytes do not depend on it.</summary>
  internal static int EditorBandRowsOverride;

  public sealed partial class Options
  {
    /// <summary>editor/: the editor pack (see EditorPack.cs). Off by default.</summary>
    public bool Editor;
    /// <summary>The float containers of the heights.</summary>
    public EditorFormat EditorFormats = EditorFormat.Tiff;
    /// <summary>Also write height-metres.tif: the same grid in metres.</summary>
    public bool EditorMetres;
    /// <summary>The Heightmap Amount of the editor heights; 0: the larger of 2 and the world's own.</summary>
    public float EditorAmount;
    /// <summary>Pixels per side of the editor pack; 0: <see cref="Size"/>.</summary>
    public int EditorSize;
    public EditorMask EditorMasks = EditorMask.Default;
    /// <summary>Metres a protected disc grows beyond the location's radius, and the width of its soft edge.</summary>
    public float EditorProtectPad = 8f, EditorProtectFeather = 32f;

    internal int EffectiveEditorSize => EditorSize > 0 ? EditorSize : Size;

    internal string? ValidateEditor()
    {
      if (!Editor)
        return null;
      int n = EffectiveEditorSize;
      if (n < MinSize || n > SizeLimit)
        return $"the editor size must be {MinSize} to {SizeLimit} pixels{(SizeLimit < MaxSize ? " (Largest Size in the config)" : "")} (got {n}); a bigger, tiled pack comes in a later version";
      if (EditorAmount != 0f && !(EditorAmount >= 0.01f && EditorAmount <= MaxHeightmapAmount))
        return $"the editor amount must be 0 (automatic) or 0.01 to {Inv(MaxHeightmapAmount)} (got {Inv(EditorAmount)})";
      if (EditorFormats == EditorFormat.None)
        return "the editor pack needs at least one height format (eformat=tif,r32)";
      if (!(EditorProtectPad >= 0f && EditorProtectPad <= 1000f) || !(EditorProtectFeather >= 0f && EditorProtectFeather <= 1000f))
        return "the protect pad and feather must be 0 to 1000 metres";
      return null;
    }

    // " + editor pack ..." in the options line; nothing for an export without one.
    internal string EditorText()
    {
      if (!Editor)
        return "";
      var formats = new List<string>();
      if ((EditorFormats & EditorFormat.Tiff) != 0) formats.Add("tif");
      if ((EditorFormats & EditorFormat.Raw32) != 0) formats.Add("r32");
      return $"; editor pack {EffectiveEditorSize} px {string.Join("+", formats)}, amount {(EditorAmount > 0f ? Inv(EditorAmount) : "auto")}"
             + (EditorMetres ? ", metres" : "") + $", masks {EditorMasksText(EditorMasks)}";
    }

    internal static string EditorMasksText(EditorMask m) =>
      m == EditorMask.None ? "none" : m == EditorMask.Default ? "default" : m == EditorMask.All ? "all"
        : string.Join(",", EditorMaskNames.Where(kv => (m & kv.Value) != 0).Select(kv => kv.Key));

    private static readonly KeyValuePair<string, EditorMask>[] EditorMaskNames =
    [
      new("water", EditorMask.Water), new("inland", EditorMask.Inland), new("slope", EditorMask.Slope), new("forest", EditorMask.Forest),
      new("biomes", EditorMask.Biomes), new("index", EditorMask.Index), new("soft", EditorMask.Soft), new("protect", EditorMask.Protect),
    ];

    /// <summary>One editor option of the form key=value (already lower case). False when the key is not an editor key; true with
    /// <paramref name="problem"/> set when the value cannot be used.</summary>
    internal static bool TryEditorKey(Options o, string key, string value, out string? problem)
    {
      problem = null;
      switch (key)
      {
        case "eformat":
        {
          var formats = EditorFormat.None;
          foreach (var part in value.Split(','))
          {
            if (part == "tif" || part == "tiff") formats |= EditorFormat.Tiff;
            else if (part == "r32" || part == "raw") formats |= EditorFormat.Raw32;
            else if (part == "exr") { problem = "EXR is not written yet; use eformat=tif or eformat=r32"; return true; }
            else { problem = $"cannot read 'eformat={value}' (tif, r32)"; return true; }
          }
          o.EditorFormats = formats;
          return true;
        }
        case "eunit":
          if (value == "v") o.EditorMetres = false;
          else if (value == "metres" || value == "meters") o.EditorMetres = true;
          else problem = $"cannot read 'eunit={value}' (v, metres)";
          return true;
        case "eamount":
          if (value == "auto")
            o.EditorAmount = 0f;
          else if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out o.EditorAmount))
            problem = $"cannot read 'eamount={value}'";
          return true;
        case "esize":
          if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.EditorSize))
            problem = $"cannot read 'esize={value}'";
          return true;
        case "emasks":
        {
          var masks = EditorMask.None;
          foreach (var part in value.Split(','))
          {
            if (part == "default") masks |= EditorMask.Default;
            else if (part == "all") masks |= EditorMask.All;
            else if (part == "none") { }
            else if (part == "rivers") { problem = "the river masks are not written yet"; return true; }
            else if (EditorMaskNames.Any(kv => kv.Key == part)) masks |= EditorMaskNames.First(kv => kv.Key == part).Value;
            else { problem = $"cannot read 'emasks={value}' (default, all, none, or a list of water, inland, slope, forest, biomes, index, soft, protect)"; return true; }
          }
          o.EditorMasks = masks;
          return true;
        }
        case "eprotectpad":
          if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out o.EditorProtectPad))
            problem = $"cannot read 'eprotectpad={value}'";
          return true;
        case "eprotectfeather":
          if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out o.EditorProtectFeather))
            problem = $"cannot read 'eprotectfeather={value}'";
          return true;
        case "etile": case "eregion": case "epitch": case "eexrchannel":
          problem = $"'{key}' (tiles, windows and EXR) comes in a later version";
          return true;
      }
      return false;
    }

    /// <summary>One editor switch: editor, noeditor, editoronly. False when the token is not one.</summary>
    internal static bool TryEditorSwitch(Options o, string token)
    {
      switch (token)
      {
        case "editor": o.Editor = true; return true;
        case "noeditor": o.Editor = false; return true;
        case "editoronly":
          // The editor pack beside heightmap.png, export.cfg, README.txt, manifest.json and world.json, and nothing else.
          o.Editor = true;
          o.Biomes = o.Locations = o.Forest = o.Heat = o.Cold = o.AltBiomes = o.Lava = o.Moss = o.Paint = o.Sources = o.Preset = false;
          return true;
      }
      return false;
    }
  }

  // What the pass found, for editor.json, the manifest, the README and the summary.
  private sealed class EditorResult
  {
    public int Size;
    public float Amount;
    public long ClippedLow, ClippedHigh;
    public float VMin = float.NaN, VMax = float.NaN, HMin = float.NaN, HMax = float.NaN;
    public readonly List<string> Formats = [];
    public readonly List<string> MaskFiles = [];
    public long MissedBiomePixels;
    public string? HeightCrc;
    public int Discs = -1;
  }

  private sealed partial class Job
  {
    private List<IRowSink>? editorSinks;
    private List<IRowSink> EditorSinks => editorSinks ??= [];
    private EditorResult? editorResult;
    private List<string>? editorDirs;
    private List<string> EditorDirs => editorDirs ??= [];

    // ---- hooks WorldExport.cs calls ----------------------------------------------------------------------------------

    private double EditorWeight() => !O.Editor ? 0 : 0.25 + EditorStreamWeight();

    // The sampling and writing phase: the terrain pass's 3.0 a megapixel, a little for every container and mask.
    private double EditorStreamWeight()
    {
      double mpx = (double)O.EffectiveEditorSize * O.EffectiveEditorSize / 1e6;
      var m = O.EditorMasks;
      double perMpx = 3.0 + 0.1 * (((O.EditorFormats & EditorFormat.Tiff) != 0 ? 1 : 0) + ((O.EditorFormats & EditorFormat.Raw32) != 0 ? 1 : 0) + (O.EditorMetres ? 1 : 0))
        + ((m & (EditorMask.Water | EditorMask.Inland)) != 0 ? 0.15 : 0) + ((m & EditorMask.Slope) != 0 ? 0.3 : 0) + ((m & EditorMask.Forest) != 0 ? 1.1 : 0)
        + ((m & (EditorMask.Biomes | EditorMask.Index | EditorMask.Inland | EditorMask.Soft)) != 0 ? 1.0 : 0) + ((m & EditorMask.Biomes) != 0 ? 0.3 : 0)
        + ((m & EditorMask.Index) != 0 ? 0.15 : 0) + ((m & EditorMask.Soft) != 0 ? 0.5 : 0) + ((m & EditorMask.Protect) != 0 ? 0.15 : 0);
      return perMpx * mpx;
    }

    // The folder editor/ is empty when it is time to remove it: after a cancel, only the empty folders go.
    private void DeleteEditorDirs()
    {
      for (int i = EditorDirs.Count - 1; i >= 0; i--)
        TryRemoveEmpty(EditorDirs[i], true);
    }

    private void DisposeEditorSinks()
    {
      foreach (var sink in EditorSinks)
        sink.Dispose();
    }

    private void AddEditorManifest(WorldExportJson.Obj m)
    {
      var r = editorResult;
      if (r == null)
        return;
      m.Add("editorPack", new WorldExportJson.Obj
      {
        { "dir", "editor" },
        { "json", "editor/editor.json" },
        { "format", "bc-editor/1" },
        { "formats", r.Formats.ToList() },
        { "size", r.Size },
        { "tiled", false },
      });
    }

    private void EditorReadmeEntry(List<string> l, int w)
    {
      if (editorResult != null)
        l.Add($"  {"editor/".PadRight(w)} The ground as 32-bit floats and masks, for Gaea and other terrain editors; editor/README-editor.txt says how to use it and bring the result back.");
    }

    private void AddEditorSummary(List<string> lines)
    {
      var r = editorResult;
      if (r == null)
        return;
      lines.Add($"editor pack: {r.Size} px {string.Join(" + ", r.Formats)} at Heightmap Amount {Inv(r.Amount)}, {r.MaskFiles.Count} mask(s) in editor/"
                + (r.ClippedHigh > 0 ? $"; WARNING: {r.ClippedHigh} pixel(s) are above 1.0 (eamount={Inv(NextAmount(r))} keeps them)" : "") + ".");
    }

    // The smallest of the usual amounts that holds the highest ground, for the advice in the summary.
    private float NextAmount(EditorResult r)
    {
      var ceiling = float.IsNaN(r.HMax) ? 0f : r.HMax;
      foreach (var a in new[] { 2f, 3f, 4f, 5f, 6f, 8f, 10f, 16f, 32f, 64f, 81f })
        if (a > r.Amount && WorldExportMath.ValueToMetres(1f, a, O.SeaLevel) >= ceiling)
          return a;
      return MaxHeightmapAmount;
    }

    // ---- the pass ----------------------------------------------------------------------------------------------------

    private float ResolveEditorAmount() =>
      Mathf.Min(MaxHeightmapAmount, O.EditorAmount > 0f ? O.EditorAmount : Mathf.Max(2f, BcWorld && Settings != null ? Settings.HeightmapAmount : 0f));

    private sealed class EditorOut(string relative, IRowSink sink)
    {
      public readonly string Relative = relative;
      public readonly IRowSink Sink = sink;
      public int RowBytes => Sink.RowBytes;
    }

    // One set of band buffers: a byte buffer for every output, and the floats of the band (and the row below it) for the slope.
    private sealed class EditorSet
    {
      public byte[][] Out = [];
      public float[] V = [];
    }

    private struct EditorRowStats
    {
      public long Low, High;
      public float VMin, VMax, HMin, HMax;
    }

    private EditorOut OpenEditorFile(string relative, Func<string, IRowSink> make)
    {
      var path = Path.Combine(Dir, relative);
      Tracked.Add(path);
      var sink = make(path);
      EditorSinks.Add(sink);
      return new EditorOut(relative, sink);
    }

    private void MakeEditorDir(string relative)
    {
      var path = Path.Combine(Dir, relative);
      if (!Directory.Exists(path))
      {
        Directory.CreateDirectory(path);
        EditorDirs.Add(path);
      }
    }

    private string EditorDescription(string encoding, float amount) =>
      $"BetterContinents: Height Encoding = {encoding}; Heightmap Amount = {amount.ToString("R", CultureInfo.InvariantCulture)}; "
      + $"Sea Level Adjustment = {O.SeaLevel.ToString("R", CultureInfo.InvariantCulture)}; Orientation = north-up";

    private IEnumerable EditorPass()
    {
      var o = O;
      int n = o.EffectiveEditorSize;
      float amount = ResolveEditorAmount();
      var result = new EditorResult { Size = n, Amount = amount };
      var masks = o.EditorMasks;
      bool blend = ZoneBlend;
      float mpp = Total / (n - 1);
      var software = "Better Continents " + ModInfo.Version;

      MakeEditorDir("editor");
      MakeEditorDir("editor/masks");
      MakeEditorDir("editor/return");

      // Which biomes get a file of their own. The ground's four corner biomes per zone and a coarse grid of points tell: a biome that
      // only a few pixels have is missed, and said so.
      bool wantHard = (masks & EditorMask.Biomes) != 0, wantIndex = (masks & EditorMask.Index) != 0, wantInland = (masks & EditorMask.Inland) != 0;
      bool wantSoft = (masks & EditorMask.Soft) != 0 && blend;
      if ((masks & EditorMask.Soft) != 0 && !blend)
        Notes.Add("Editor pack: the soft biome masks need the zone-corner blend (perpoint is on), so they were left out.");
      bool needPointBiome = wantHard || wantIndex || wantInland;
      int biomeCount = BiomeRegistry.IndexCount;
      var present = new bool[biomeCount];

      Heightmap.Biome[]? corners = null;
      int cornerMin = 0, cornerCount = 0;
      if (blend)
      {
        cornerMin = WorldExportMath.ZoneIndex(WorldExportMath.PixelToWorld(0, n, Total));
        cornerCount = WorldExportMath.ZoneIndex(WorldExportMath.PixelToWorld(n - 1, n, Total)) + 2 - cornerMin;
        var grid = corners = new Heightmap.Biome[cornerCount * cornerCount];
        int cm = cornerMin, cc = cornerCount;
        Begin("Sampling editor zone corners", 0.1);
        foreach (var step in Await(RunRows(cc, r =>
                 {
                   float z = (cm + r) * 64f - 32f;
                   for (int c = 0; c < cc; c++)
                   {
                     var b = Wg.GetBiome((cm + c) * 64f - 32f, z);
                     grid[r * cc + c] = b;
                     present[BiomeRegistry.ToSafeIndex(b)] = true;
                   }
                 }), RowProgress(cc)))
          yield return step;
      }
      else
        Skip(0.1);
      if (needPointBiome || wantSoft)
      {
        int s = Math.Min(256, n);
        Begin("Looking for the biomes", 0.05);
        foreach (var step in Await(RunRows(s, r =>
                 {
                   float z = WorldExportMath.FileRowToWorldZ((int)((long)r * (n - 1) / Math.Max(1, s - 1)), n, Total);
                   for (int c = 0; c < s; c++)
                     present[BiomeRegistry.ToSafeIndex(Wg.GetBiome(WorldExportMath.PixelToWorld((int)((long)c * (n - 1) / Math.Max(1, s - 1)), n, Total), z))] = true;
                 }), RowProgress(s)))
          yield return step;
      }
      else
        Skip(0.05);
      present[0] = false;

      // The discs of the protect mask: the main thread, like the location map.
      List<(float X, float Z, float Radius)>? discs = null;
      if ((masks & EditorMask.Protect) != 0)
      {
        discs = EditorDiscSource != null ? EditorDiscSource() : ReadLocationDiscs();
        result.Discs = discs.Count;
        if (discs.Count == 0)
        {
          Notes.Add("Editor pack: this peer knows no locations, so there is no protect-locations mask.");
          masks &= ~EditorMask.Protect;
        }
        else if (!OnServer && EditorDiscSource == null)
          Notes.Add("Editor pack: sampled on a client, which knows only the locations the server shows on the map, so protect-locations.png is incomplete; export on the host or the dedicated server for every location.");
      }

      // ---- the outputs, in a fixed order
      var outs = new List<EditorOut>();
      int iTif = -1, iR32 = -1, iMetres = -1, iWater = -1, iInland = -1, iSlope = -1, iForest = -1, iIndex = -1, iProtect = -1;
      var hard = new int[biomeCount];
      var soft = new int[biomeCount];
      for (int k = 0; k < biomeCount; k++)
        hard[k] = soft[k] = -1;
      int Add(EditorOut e) { outs.Add(e); return outs.Count - 1; }
      if ((o.EditorFormats & EditorFormat.Tiff) != 0)
        iTif = Add(OpenEditorFile("editor/height.tif", p => new TiffRowWriter(p, n, n, 32, EditorDescription("bc-v", amount), software)));
      if ((o.EditorFormats & EditorFormat.Raw32) != 0)
        iR32 = Add(OpenEditorFile("editor/height.r32", p => new RawFloatRowWriter(p, n, n)));
      if (o.EditorMetres)
        iMetres = Add(OpenEditorFile("editor/height-metres.tif", p => new TiffRowWriter(p, n, n, 32,
          "BetterContinents: Height Encoding = metres; Orientation = north-up", software)));
      PngRowSink Png(string path, int channels, int bits) => new(new PngRowWriter(path, n, n, channels, bits));
      if ((masks & EditorMask.Water) != 0) iWater = Add(OpenEditorFile("editor/masks/water.png", p => Png(p, 1, 8)));
      if (wantInland) iInland = Add(OpenEditorFile("editor/masks/water-inland.png", p => Png(p, 1, 8)));
      if ((masks & EditorMask.Slope) != 0) iSlope = Add(OpenEditorFile("editor/masks/slope.png", p => Png(p, 1, 16)));
      if ((masks & EditorMask.Forest) != 0) iForest = Add(OpenEditorFile("editor/masks/forest-density.png", p => Png(p, 1, 16)));
      if (wantIndex) iIndex = Add(OpenEditorFile("editor/masks/biome-index.png", p => Png(p, 1, 8)));
      if ((masks & EditorMask.Protect) != 0) iProtect = Add(OpenEditorFile("editor/masks/protect-locations.png", p => Png(p, 1, 8)));
      string BiomeFile(int k) => SafeFileName(BiomeRegistry.Name(BiomeRegistry.ToSafeBiome(k)));
      for (int k = 1; k < biomeCount; k++)
      {
        if (!present[k])
          continue;
        int kk = k;
        if (wantHard) hard[k] = Add(OpenEditorFile($"editor/masks/biome-{BiomeFile(k)}.png", p => Png(p, 1, 8)));
        if (wantSoft) soft[k] = Add(OpenEditorFile($"editor/masks/biome-soft-{BiomeFile(kk)}.png", p => Png(p, 1, 16)));
      }

      // ---- the sampling
      var stats = new EditorRowStats[n];
      var biomePixels = new long[biomeCount];
      var softPixels = new long[biomeCount];
      var missed = new long[biomeCount];
      int bandRows = EditorBandRowsOverride > 0 ? Math.Min(n, EditorBandRowsOverride) : Math.Max(1, Math.Min(n, (1 << 20) / n));
      bool slope = iSlope >= 0;
      float total = Total, worldR = WorldR, totalR = TotalR, sla = Sla;
      bool hardNeedsCall = needPointBiome && !blend;
      float padIn = o.EditorProtectPad, feather = o.EditorProtectFeather;
      var discList = discs;
      var prevRow = slope ? new float[n] : null;
      bool havePrev = false;
      float metresScale = mpp;

      void Sample(int fileRow, EditorSet set, int rb, bool extraRow)
      {
        float wz = WorldExportMath.FileRowToWorldZ(fileRow, n, total);
        int zz = WorldExportMath.ZoneIndex(wz);
        float tz = DUtils.SmoothStep(0f, 1f, (float)(((double)wz - (zz * 64f - 32f)) / 64.0));
        int cz = zz - cornerMin;
        var rowV = new float[n];
        var rowH = iMetres >= 0 ? new float[n] : null;
        var rowR32 = iR32 >= 0 ? new float[n] : null;
        var st = new EditorRowStats { VMin = float.MaxValue, VMax = float.MinValue, HMin = float.MaxValue, HMax = float.MinValue };
        // The sparse masks start black: the buffers are reused.
        for (int k = 1; k < biomeCount; k++)
        {
          if (hard[k] >= 0) Array.Clear(set.Out[hard[k]], rb * n, n);
          if (soft[k] >= 0) Array.Clear(set.Out[soft[k]], rb * n * 2, n * 2);
        }
        byte[]? bWater = iWater >= 0 ? set.Out[iWater] : null, bInland = iInland >= 0 ? set.Out[iInland] : null, bForest = iForest >= 0 ? set.Out[iForest] : null,
          bIndex = iIndex >= 0 ? set.Out[iIndex] : null, bProtect = iProtect >= 0 ? set.Out[iProtect] : null;
        var rowBiome = needPointBiome ? new long[biomeCount] : null;
        var rowSoft = wantSoft ? new long[biomeCount] : null;
        var rowMissed = needPointBiome ? new long[biomeCount] : null;
        for (int col = 0; col < n; col++)
        {
          float wx = WorldExportMath.PixelToWorld(col, n, total);
          float h;
          Heightmap.Biome point = Heightmap.Biome.None;
          bool havePoint = false;
          // Corner biomes and their blend weights, for the soft masks.
          Heightmap.Biome c00 = default, c10 = default, c01 = default, c11 = default;
          float tx = 0f;
          bool uniform = true;
          if (corners != null)
          {
            int zx = WorldExportMath.ZoneIndex(wx);
            int i0 = cz * cornerCount + (zx - cornerMin);
            var b00 = c00 = corners[i0];
            var b10 = c10 = corners[i0 + 1];
            var b01 = c01 = corners[i0 + cornerCount];
            var b11 = c11 = corners[i0 + cornerCount + 1];
            if (b01 == b00 && b10 == b00 && b11 == b00)
              h = Wg.GetBiomeHeight(b00, wx, wz, out _);
            else
            {
              uniform = false;
              tx = DUtils.SmoothStep(0f, 1f, (float)(((double)wx - (zx * 64f - 32f)) / 64.0));
              // TerrainRow's blend, call for call.
              float h00 = Wg.GetBiomeHeight(b00, wx, wz, out _);
              float h10, h01, h11;
              if (b10 == b00) h10 = h00;
              else h10 = Wg.GetBiomeHeight(b10, wx, wz, out _);
              if (b01 == b00) h01 = h00;
              else if (b01 == b10) h01 = h10;
              else h01 = Wg.GetBiomeHeight(b01, wx, wz, out _);
              if (b11 == b00) h11 = h00;
              else if (b11 == b10) h11 = h10;
              else if (b11 == b01) h11 = h01;
              else h11 = Wg.GetBiomeHeight(b11, wx, wz, out _);
              h = DUtils.Lerp(DUtils.Lerp(h00, h10, tx), DUtils.Lerp(h01, h11, tx), tz);
            }
          }
          else
          {
            point = Wg.GetBiome(wx, wz);
            havePoint = true;
            h = Wg.GetBiomeHeight(point, wx, wz, out _);
          }

          // The height as the world has it (the edge drop-off applied, which is not undone here), as editor values.
          float v = (h / WorldExportMath.HeightScale + WorldExportMath.BaseOffset - sla) / amount;
          bool low = !(v >= 0f);
          float file = low ? 0f : v;
          if (low) st.Low++;
          else if (v > 1f) st.High++;
          if (file < st.VMin) st.VMin = file;
          if (file > st.VMax) st.VMax = file;
          float d2 = wx * wx + wz * wz;
          if (d2 <= worldR * worldR)
          {
            if (h < st.HMin) st.HMin = h;
            if (h > st.HMax) st.HMax = h;
          }
          rowV[col] = file;
          if (rowH != null) rowH[col] = h;
          if (rowR32 != null) rowR32[col] = file > 1f ? 1f : file;

          bool water = h < WorldExportMath.WaterLevel;
          if (bWater != null) bWater[rb * n + col] = water ? (byte)255 : (byte)0;
          if (needPointBiome)
          {
            if (!havePoint)
              point = Wg.GetBiome(wx, wz);
            int k = BiomeRegistry.ToSafeIndex(point);
            if (bInland != null) bInland[rb * n + col] = water && point != Heightmap.Biome.Ocean ? (byte)255 : (byte)0;
            if (bIndex != null) bIndex[rb * n + col] = (byte)k;
            if (k != 0)
            {
              rowBiome![k]++;
              if (hard[k] >= 0) set.Out[hard[k]][rb * n + col] = 255;
              else if (wantHard) rowMissed![k]++;
            }
          }
          if (wantSoft)
          {
            if (uniform)
            {
              int k = BiomeRegistry.ToSafeIndex(c00);
              if (soft[k] >= 0 && k != 0) { PutSoft(set.Out[soft[k]], rb * n + col, 1f); rowSoft![k]++; }
            }
            else
            {
              // The smoothstep weights of the corners; a biome that holds several corners gets their sum.
              float w00 = (1f - tx) * (1f - tz), w10 = tx * (1f - tz), w01 = (1f - tx) * tz, w11 = tx * tz;
              SoftAdd(set, soft, rowSoft!, rb * n + col, c00, c10, c01, c11, w00, w10, w01, w11);
            }
          }
          if (bForest != null)
          {
            float f = WorldExportMath.ForestNormalized(WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz)));
            ushort u = (ushort)(Mathf.Clamp01(f) * 65535f + 0.5f);
            bForest[2 * (rb * n + col)] = (byte)(u >> 8);
            bForest[2 * (rb * n + col) + 1] = (byte)u;
          }
          if (bProtect != null)
          {
            float best = 0f;
            foreach (var disc in discList!)
            {
              float dx = wx - disc.X, dz = wz - disc.Z;
              float rIn = disc.Radius + padIn;
              float lim = rIn + feather;
              if (dx > lim || dx < -lim || dz > lim || dz < -lim)
                continue;
              float dist = Mathf.Sqrt(dx * dx + dz * dz);
              float a = dist <= rIn ? 1f : feather > 0f && dist < lim ? 1f - (dist - rIn) / feather : 0f;
              if (a > best) best = a;
            }
            bProtect[rb * n + col] = (byte)(best * 255f + 0.5f);
          }
        }
        if (iTif >= 0) Buffer.BlockCopy(rowV, 0, set.Out[iTif], rb * n * 4, n * 4);
        if (rowR32 != null) Buffer.BlockCopy(rowR32, 0, set.Out[iR32], rb * n * 4, n * 4);
        if (rowH != null) Buffer.BlockCopy(rowH, 0, set.Out[iMetres], rb * n * 4, n * 4);
        if (slope) Array.Copy(rowV, 0, set.V, rb * n, n);
        stats[fileRow] = st;
        // A row sampled as the band's look-ahead is sampled again as the first of the next band: counted once.
        if (extraRow)
          return;
        if (rowBiome != null)
          for (int k = 1; k < biomeCount; k++)
          {
            if (rowBiome[k] != 0) Interlocked.Add(ref biomePixels[k], rowBiome[k]);
            if (rowMissed![k] != 0) Interlocked.Add(ref missed[k], rowMissed[k]);
          }
        if (rowSoft != null)
          for (int k = 1; k < biomeCount; k++)
            if (rowSoft[k] != 0) Interlocked.Add(ref softPixels[k], rowSoft[k]);
      }

      // The slope of the band's rows, once its floats (and the row below) are sampled; the row above is the band before's last.
      void Finish(int first, int count, int extra, EditorSet set)
      {
        if (!slope)
          return;
        var bytes = set.Out[iSlope];
        var v = set.V;
        int w = Math.Min(Workers, Math.Max(1, count));
        double ampl = amount, sea = sla;
        GameUtils.SimpleParallelFor(w, 0, w, k =>
        {
          for (int r = k; r < count; r += w)
          {
            if (cancelRequested)
              return;
            bool hasUp = r > 0 || (first > 0 && havePrev), hasDown = r + 1 < count || extra > 0;
            for (int c = 0; c < n; c++)
            {
              double here = Metres(v[r * n + c], ampl, sea);
              double gx = c == 0 ? (Metres(v[r * n + 1], ampl, sea) - here) / metresScale
                : c == n - 1 ? (here - Metres(v[r * n + c - 1], ampl, sea)) / metresScale
                : (Metres(v[r * n + c + 1], ampl, sea) - Metres(v[r * n + c - 1], ampl, sea)) / (2.0 * metresScale);
              double up = hasUp ? Metres(r > 0 ? v[(r - 1) * n + c] : prevRow![c], ampl, sea) : here;
              double down = hasDown ? Metres(v[(r + 1) * n + c], ampl, sea) : here;
              double gz = (down - up) / ((hasUp && hasDown ? 2.0 : 1.0) * metresScale);
              double deg = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * (180.0 / Math.PI);
              ushort u = (ushort)(deg / 90.0 * 65535.0 + 0.5);
              int at = 2 * (r * n + c);
              bytes[at] = (byte)(u >> 8);
              bytes[at + 1] = (byte)u;
            }
          }
        });
        // The last row of this band is the row above the next band.
        if (count > 0)
          Array.Copy(v, (count - 1) * n, prevRow!, 0, n);
        havePrev = count > 0;
      }

      Begin("Sampling the editor pack", EditorStreamWeight());
      foreach (var step in Await(EditorBands(n, bandRows, outs, slope ? 1 : 0, Sample, Finish), RowProgress(n)))
        yield return step;
      // Await ends the phase; the next one starts with Begin.

      // ---- the files are whole
      foreach (var s in stats)
      {
        result.ClippedLow += s.Low;
        result.ClippedHigh += s.High;
        if (s.VMin <= s.VMax)
        {
          result.VMin = float.IsNaN(result.VMin) ? s.VMin : Math.Min(result.VMin, s.VMin);
          result.VMax = float.IsNaN(result.VMax) ? s.VMax : Math.Max(result.VMax, s.VMax);
        }
        if (s.HMin <= s.HMax)
        {
          result.HMin = float.IsNaN(result.HMin) ? s.HMin : Math.Min(result.HMin, s.HMin);
          result.HMax = float.IsNaN(result.HMax) ? s.HMax : Math.Max(result.HMax, s.HMax);
        }
      }
      var indexLegend = new List<string>();
      for (int i = 0; i < outs.Count; i++)
      {
        outs[i].Sink.Complete();
      }
      // Complete does the files' names; an empty biome mask is not worth keeping.
      for (int i = 0; i < outs.Count; i++)
      {
        var e = outs[i];
        bool keep = true;
        for (int k = 1; k < biomeCount; k++)
        {
          if (hard[k] == i && biomePixels[k] == 0) keep = false;
          if (soft[k] == i && softPixels[k] == 0) keep = false;
        }
        if (!keep)
        {
          try { File.Delete(Path.Combine(Dir, e.Relative)); } catch (Exception ex) { LogWarning($"World export: could not delete {e.Relative}: {ex.Message}"); }
          continue;
        }
        Written.Add(e.Relative);
        if (e.Relative.StartsWith("editor/masks/"))
          result.MaskFiles.Add(e.Relative.Substring("editor/".Length));
      }
      result.MissedBiomePixels = missed.Sum();
      if (result.MissedBiomePixels > 0)
        Notes.Add($"Editor pack: {result.MissedBiomePixels} pixel(s) belong to a biome too small to be found beforehand (under a coarse grid and the zone corners), so that biome has no mask of its own; biome-index.png has them.");
      if (iTif >= 0) result.Formats.Add("tif");
      if (iR32 >= 0) result.Formats.Add("r32");
      if (iMetres >= 0) result.Formats.Add("metres.tif");
      if (result.ClippedHigh > 0)
        Notes.Add($"Editor pack: {result.ClippedHigh} pixel(s) are above 1.0 at Heightmap Amount {Inv(amount)} (the highest ground is {Inv(result.HMax)} m); "
                  + $"export again with eamount={Inv(NextAmount(result))} to keep a value range of 0..1 (.r32 is clamped to 1.0 either way).");
      if ((o.EditorFormats & EditorFormat.Raw32) != 0 && result.VMax > 1f)
        Notes.Add("Editor pack: height.r32 is clamped to 0..1 as Gaea's .r32 is; height.tif keeps the values above 1.0.");
      editorResult = result;

      // ---- editor.json, the legend, the README, the probe
      Begin("Writing the editor pack's description", 0.1);
      var crcPath = Path.Combine(Dir, iTif >= 0 ? "editor/height.tif" : "editor/height.r32");
      var task = Task.Run(() =>
      {
        result.HeightCrc = PngRowWriter.FileCrc32(crcPath).ToString("X8", CultureInfo.InvariantCulture);
      });
      foreach (var step in Await(task, () => 50f, needsWorld: false))
        yield return step;
      if (wantIndex)
      {
        var table = ImageMapBiome.ExportColorTable();
        indexLegend.Add("# biome-index.png: the pixel value is the biome's index (BiomeRegistry); 0 is none or a biome this map cannot hold.");
        indexLegend.Add("# index: name: R,G,B (the colour biomemap.png gives the biome)");
        foreach (var kv in table.OrderBy(kv => BiomeRegistry.ToSafeIndex(kv.Key)))
          indexLegend.Add($"{BiomeRegistry.ToSafeIndex(kv.Key)}: {BiomeRegistry.Name(kv.Key)}: {kv.Value.r},{kv.Value.g},{kv.Value.b}");
        WriteTextFile("editor/masks/biome-index.txt", indexLegend);
      }
      WriteTextFile("editor/editor.json", [EditorJson(result, n, amount, mpp, masks)]);
      WriteTextFile("editor/README-editor.txt", EditorReadme(result, n, amount, mpp));
      WriteEditorProbe();
    }

    private static double Metres(float v, double amount, double sla) => (v * amount - WorldExportMath.BaseOffset + sla) * WorldExportMath.HeightScale;

    // A soft mask's pixel: 16-bit big endian.
    private static void PutSoft(byte[] buffer, int index, float weight)
    {
      ushort u = (ushort)(Mathf.Clamp01(weight) * 65535f + 0.5f);
      buffer[2 * index] = (byte)(u >> 8);
      buffer[2 * index + 1] = (byte)u;
    }

    // The weights of the four corners go to their biomes' masks; a biome that holds several corners gets the sum, written once.
    private static void SoftAdd(EditorSet set, int[] soft, long[] counts, int index, Heightmap.Biome c00, Heightmap.Biome c10, Heightmap.Biome c01, Heightmap.Biome c11,
      float w00, float w10, float w01, float w11)
    {
      var biomes = new[] { c00, c10, c01, c11 };
      var weights = new[] { w00, w10, w01, w11 };
      for (int i = 0; i < 4; i++)
      {
        bool first = true;
        for (int j = 0; j < i; j++)
          if (biomes[j] == biomes[i]) { first = false; break; }
        if (!first)
          continue;
        float sum = weights[i];
        for (int j = i + 1; j < 4; j++)
          if (biomes[j] == biomes[i]) sum += weights[j];
        int k = BiomeRegistry.ToSafeIndex(biomes[i]);
        if (k == 0 || soft[k] < 0)
          continue;
        PutSoft(set.Out[soft[k]], index, sum);
        if (sum > 0f)
          counts[k]++;
      }
    }

    // The discs around the locations: (x, z, radius) with the larger of the two radii the game clears and keeps free.
    private List<(float X, float Z, float Radius)> ReadLocationDiscs()
    {
      var list = new List<(float, float, float)>();
      var zs = ZoneSystem.instance;
      if (zs == null)
        return list;
      if (OnServer)
      {
        foreach (var kv in zs.m_locationInstances)
        {
          var loc = kv.Value.m_location;
          if (loc != null)
            list.Add((kv.Value.m_position.x, kv.Value.m_position.z, Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius)));
        }
        return list;
      }
      var icons = new Dictionary<Vector3, string>();
      zs.GetLocationIcons(icons);
      foreach (var kv in icons)
      {
        if (string.IsNullOrEmpty(kv.Value))
          continue;
        var loc = zs.GetLocation(kv.Value);
        list.Add((kv.Key.x, kv.Key.z, loc != null ? Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius) : 32f));
      }
      return list;
    }

    // The band runner of the editor pass: RunBands for IRowSink outputs and for the floats of the slope. Samples file rows 0..rows-1
    // band by band on the workers (every n-th row each, so the costly rows spread) while the band before is written, one task per
    // output; lookAhead rows after a band are sampled too (the row below the band the slope needs).
    private Task EditorBands(int rows, int bandRows, List<EditorOut> outputs, int lookAhead, Action<int, EditorSet, int, bool> sample, Action<int, int, int, EditorSet> finish)
    {
      rowsDone = 0;
      int n = rows;
      return OwnThread(() =>
      {
        int workers = Math.Max(1, Math.Min(Workers, bandRows));
        var sets = new EditorSet[2];
        for (int s = 0; s < 2; s++)
          sets[s] = new EditorSet
          {
            Out = [.. outputs.Select(o => new byte[(bandRows + lookAhead) * o.RowBytes])],
            V = lookAhead > 0 ? new float[(bandRows + lookAhead) * n] : [],
          };
        Task? writing = null;
        bool finished = false;
        try
        {
          for (int start = 0, set = 0; start < rows && !cancelRequested; start += bandRows, set ^= 1)
          {
            int first = start, count = Math.Min(bandRows, rows - start);
            int extra = Math.Min(lookAhead, rows - (first + count));
            var buffers = sets[set];
            GameUtils.SimpleParallelFor(workers, 0, workers, w =>
            {
              using var withoutGround = SamplingPause();
              for (int r = w; r < count + extra; r += workers)
              {
                if (cancelRequested)
                  return;
                sample(first + r, buffers, r, r >= count);
                if (r < count)
                  Interlocked.Increment(ref rowsDone);
              }
            });
            if (!cancelRequested)
              finish(first, count, extra, buffers);
            writing?.Wait();
            if (cancelRequested)
              break;
            writing = OwnThread(() =>
            {
              if (outputs.Count == 1)
                outputs[0].Sink.WriteRows(buffers.Out[0], 0, count);
              else
                Task.WaitAll(outputs.Select((o, k) => OwnThread(() => o.Sink.WriteRows(buffers.Out[k], 0, count))).ToArray());
            });
          }
          writing?.Wait();
          finished = !cancelRequested;
        }
        finally
        {
          if (!finished)
          {
            try { writing?.Wait(); } catch { /* the failure that matters is the one being thrown */ }
            foreach (var o in outputs)
              o.Sink.Dispose();
          }
        }
      });
    }

    // ---- editor.json -------------------------------------------------------------------------------------------------

    private string EditorJson(EditorResult r, int n, float amount, float mpp, EditorMask masks)
    {
      var sla = Sla;
      float floor = WorldExportMath.ValueToMetres(0f, amount, O.SeaLevel), ceiling = WorldExportMath.ValueToMetres(1f, amount, O.SeaLevel);
      var files = new List<string>();
      if (r.Formats.Contains("tif")) files.Add("height.tif");
      if (r.Formats.Contains("r32")) files.Add("height.r32");
      var maskList = new List<object>();
      foreach (var f in r.MaskFiles)
        maskList.Add(new WorldExportJson.Obj { { "file", f }, { "bits", MaskBits(f) }, { "definition", MaskDefinition(f) } });
      var referenced = new WorldExportJson.Obj();
      void Ref(string key, string file, string? note = null)
      {
        if (Written.Contains(file))
          referenced.Add(key, note == null ? "../" + file : $"../{file} ({note})");
      }
      Ref("biome", "biomemap.png");
      Ref("altBiomes", "altbiomemap.png");
      Ref("heat", "heatmap.png");
      Ref("lava", "lavamap.png");
      Ref("moss", "mossmap.png");
      Ref("paint", "paintmap.png");
      Ref("locations", "locationmap.png", "its pixels are on the corner grid / size, see ../manifest.json");
      var doc = new WorldExportJson.Obj
      {
        { "format", "bc-editor/1" },
        { "exportFormat", Format },
        { "modVersion", ModInfo.Version },
        { "exportedAt", Started.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
        { "worldName", World.m_name },
        { "seed", World.m_seed },
        { "source", new WorldExportJson.Obj
          {
            { "manifest", "../manifest.json" },
            { "heightmap", Written.Contains("heightmap.png") ? "../heightmap.png" : null },
            { "heightmapAmount", O.HeightmapAmount },
            { "heightmapSeaLevel", O.SeaLevel },
          } },
        { "grid", new WorldExportJson.Obj
          {
            { "size", n },
            { "totalSize", Total },
            { "metresPerPixel", mpp },
            { "registration", "node: pixel i is at (i / (size - 1) - 0.5) * totalSize on both axes, the same grid as every Better Continents map" },
            { "origin", "pixel (column 0, row 0) = world (x = -totalSize / 2, z = +totalSize / 2): top left, west, north" },
            { "axes", "columns run east (+x); rows run south (-z); file row 0 is north" },
            { "worldRadius", WorldR },
            { "totalRadius", TotalR },
            { "edgeDropoff", EdgeDropoff },
            { "worldX", "x = (column / (size - 1) - 0.5) * totalSize" },
            { "worldZ", "z = (0.5 - row / (size - 1)) * totalSize" },
          } },
        { "height", new WorldExportJson.Obj
          {
            { "files", files },
            { "dtype", "float32" },
            { "unit", "bc-v" },
            { "amount", amount },
            { "seaLevel", O.SeaLevel },
            { "seaLevelAdjustment", sla },
            { "formula", "metres = (v * amount - 0.15 + seaLevelAdjustment) * 200" },
            { "floorMetres", Math.Round(floor, 3) },
            { "ceilingMetres", Math.Round(ceiling, 3) },
            { "waterlineValue", Math.Round(WorldExportMath.WaterlineValue(amount, O.SeaLevel), 6) },
            { "valueRange", float.IsNaN(r.VMin) ? null : new object[] { r.VMin, r.VMax } },
            { "heightRangeMetres", float.IsNaN(r.HMin) ? null : new object[] { r.HMin, r.HMax } },
            { "clippedLow", r.ClippedLow },
            { "clippedHigh", r.ClippedHigh },
            { "clamped01", (O.EditorFormats & EditorFormat.Raw32) != 0 ? "height.r32" : null },
            { "edgeDropoffApplied", "the heights are the ground as the world has it; past worldRadius they include the map-edge drop-off, which is not undone" },
            { "metresFile", r.Formats.Contains("metres.tif") ? "height-metres.tif" : null },
            { "crc32", r.HeightCrc },
          } },
        { "gaea", new WorldExportJson.Obj
          {
            { "note", "Values are editor-neutral. README-editor.txt has the steps; none of the Gaea settings below has been checked against Gaea itself." },
            { "terrainHeightMetres", amount * 200f },
            { "scaleMetresKeepPixelPitch", mpp * n },
            { "scaleMetresAsTotal", Total },
            { "seaNodeLevel", Math.Round(WorldExportMath.WaterlineValue(amount, O.SeaLevel), 6) },
            { "buildOutputRange", "Raw" },
            { "buildFormat", "TIFF 32-bit (or R32)" },
            { "buildResolution", n },
          } },
        { "masks", maskList },
        { "referencedMaps", referenced },
        { "protect", r.Discs < 0 ? null : new WorldExportJson.Obj
          {
            { "discs", r.Discs },
            { "padMetres", O.EditorProtectPad },
            { "featherMetres", O.EditorProtectFeather },
          } },
        { "probe", "probe.tif" },
        { "return", "return/" },
        { "tiles", null },
        { "window", null },
      };
      return WorldExportJson.Write(doc);
    }

    private static int MaskBits(string file) => file.Contains("slope") || file.Contains("forest-density") || file.Contains("biome-soft-") ? 16 : 8;

    private static string MaskDefinition(string file)
    {
      var name = Path.GetFileNameWithoutExtension(file);
      return name switch
      {
        "water" => "255 where the ground is under the game's water plane (height < 30 m), else 0",
        "water-inland" => "255 where it is under water and the biome is not Ocean (lakes, rivers, marsh), else 0",
        "slope" => "round(slope in degrees / 90 * 65535); slope = atan(hypot(gx, gz)) from the float heights, central differences over one pixel pitch, one-sided at the border",
        "forest-density" => "round(density * 65535); density 0 (bare) to 1 (densest) is the game's forest factor, normalised",
        "biome-index" => "the biome's index (BiomeRegistry); biome-index.txt lists the values",
        "protect-locations" => "255 within (location radius + pad) metres of a location, falling to 0 over the feather width",
        _ => name.StartsWith("biome-soft-") ? "round(weight * 65535): how much of the ground's height blend this biome has (the four zone corners, smoothstep weights)"
          : name.StartsWith("biome-") ? "255 where the biome of the point is this one, else 0" : "",
      };
    }

    // ---- README-editor.txt and the orientation probe ----------------------------------------------------------------------

    private List<string> EditorReadme(EditorResult r, int n, float amount, float mpp)
    {
      float floor = WorldExportMath.ValueToMetres(0f, amount, O.SeaLevel), ceiling = WorldExportMath.ValueToMetres(1f, amount, O.SeaLevel);
      var wl = WorldExportMath.WaterlineValue(amount, O.SeaLevel);
      var main = r.Formats.Contains("tif") ? "height.tif" : "height.r32";
      var l = new List<string>
      {
        $"EDITOR PACK for \"{World.m_name}\" - the ground as 32-bit floats, for Gaea and other terrain editors.",
        "",
        "WHAT IS HERE",
        $"  {main}".PadRight(22) + $" {n} x {n} pixels, 32-bit float, north at the top, west at the left, one pixel every {Inv(mpp)} m.",
        "  editor.json           how the pixels map to the world, the height formula and the numbers below, for tools.",
        "  masks/                grey pictures of what a heightfield does not hold (water, slope, forest, biomes, locations).",
        "  probe.tif             a small test picture to see how your editor orients a file (step 3).",
        "  return/               put the editor's result here (step 5).",
        "",
        "THE HEIGHTS",
        $"  value v = (metres / 200 + 0.15 - {Inv(Sla)}) / {Inv(amount)}; metres = (v x {Inv(amount)} - 0.15 + {Inv(Sla)}) x 200.",
        $"  v = 0 is {Inv(floor)} m, v = 1 is {Inv(ceiling)} m, the sea (30 m) is v = {Inv(wl)}. Deeper ground is flattened to v = 0.",
        $"  Heights in this file: v from {Inv(r.VMin)} to {Inv(r.VMax)} ({r.ClippedLow} pixel(s) were below 0, {r.ClippedHigh} above 1).",
        "  The ground is as the world has it: past the world's edge it includes the map-edge drop-off, which stays as it is.",
        "",
        "USING IT IN GAEA (the settings below follow Gaea's documentation; they have not been checked against Gaea itself)",
        $"  1. Terrain Definition: Height = {Inv(amount * 200f)} m, Scale = {Inv(mpp * n)} m (or {Inv(Total)} m for the whole map).",
        $"  2. Add a File node, Scale = Raw, load {main}. Its minimum and maximum should read about {Inv(r.VMin)} and {Inv(r.VMax)}.",
        "     If they read 0 and 1 while v does not span that, the editor normalised the file: use Mapped or Custom with the values above.",
        "  3. Orientation: load probe.tif once. Its high side must be at the top (north) and the bright dot at the top left.",
        "     If it is not, no matter: the way back finds the orientation itself, or you can say orient=flipy and the like.",
        "  4. Edit. Keep towns and dungeons where they are: masks/protect-locations.png is white around every location.",
        "  5. Build with Output Range = Raw, format TIFF 32-bit (or R32), at the same resolution, and save the height as",
        "     return/height.tif (or height.r32).",
        $"  6. In the game's console: bc_import edited {Path.GetFileName(Dir)}    (the folder's name, or its full path)",
        "     It makes a new folder, export-<time>-edited, beside this one: its heightmap.png and heightmap-fine.png hold the edited ground,",
        "     the other maps are copied. 'bc_import <that folder>' then makes the New World preset.",
        "",
        "GOOD TO KNOW",
        "  - Biomes, locations and the other maps do not move with the ground you edit; paint them in the PNGs (biome-index.png is a start).",
        "  - Pixels are the same grid as every Better Continents map (pixel i is at i / (size - 1)); an editor that centres pixels instead",
        $"    puts features up to half a pixel ({Inv(mpp / 2)} m) off.",
        "  - A heightmap.png of the result is 16-bit with a fine file of 8 more bits: about a millimetre at this Heightmap Amount.",
        "  - Better Continents reads maps up to 16384 pixels across; a bigger editor result is not accepted yet.",
      };
      return l;
    }

    // 64 x 64 floats: ground that rises to the north and a bright 5 x 5 patch in the north-west quarter, so one look at the file in an
    // editor shows how it turns the picture (and north is the top row of the file).
    private void WriteEditorProbe()
    {
      const int s = 64;
      var path = Path.Combine(Dir, "editor/probe.tif");
      Tracked.Add(path);
      var row = new float[s];
      var bytes = new byte[s * 4];
      using var tif = new TiffRowWriter(path, s, s, 32, "BetterContinents: orientation probe; the high side is north, the bright patch is north-west; Orientation = north-up", $"{ModInfo.Name} {ModInfo.Version}");
      for (int y = 0; y < s; y++)
      {
        for (int x = 0; x < s; x++)
        {
          float v = 0.1f + 0.5f * (1f - y / (float)(s - 1));
          if (x >= 14 && x <= 18 && y >= 14 && y <= 18)
            v = 1f;
          row[x] = v;
        }
        Buffer.BlockCopy(row, 0, bytes, 0, bytes.Length);
        tif.WriteRows(bytes, 0, 1);
      }
      tif.Complete();
      Written.Add("editor/probe.tif");
    }
  }
}

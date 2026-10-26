// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

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
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// The way back of the editor pack: "bc_import edited <export>" reads the ground an editor built from editor/ (a float TIFF, .r32 or
// 16-bit PNG in editor/return/), turns it into the heightmap.png and heightmap-fine.png every export has (24 effective bits), copies
// every other map byte for byte and writes export.cfg and manifest.json for the heights, all into a new folder
// <export>-edited beside the first. The export itself is never changed. 'bc_import <that folder>' then makes the preset as for any export.
internal enum EditorUnit
{
  /// <summary>Better Continents' value v of the pack: metres = (v x amount - 0.15 + sea level adjustment) x 200.</summary>
  V,
  Metres,
  /// <summary>metres = lo + f x (hi - lo): a build that normalised or scaled the heights to a range it names.</summary>
  Range,
}

/// <summary>The eight ways a square picture can be turned or mirrored. Each is what is done to the picture an editor returned to get the
/// pack's orientation back: out(row, column) = in(...) as <see cref="EditorOrientation.Map"/> says.</summary>
internal enum EditorOrient
{
  None,
  /// <summary>Upside down: the rows in reverse.</summary>
  FlipY,
  /// <summary>Mirrored left to right.</summary>
  FlipX,
  Rot180,
  /// <summary>Rows and columns swapped (a mirror along the main diagonal).</summary>
  Transpose,
  AntiTranspose,
  /// <summary>Turned a quarter clockwise.</summary>
  Cw90,
  /// <summary>Turned a quarter counter-clockwise.</summary>
  Ccw90,
}

internal static class EditorOrientation
{
  public static readonly string[] Names = ["none", "flipy", "flipx", "rot180", "transpose", "antitranspose", "cw90", "ccw90"];

  public static string Name(EditorOrient o) => Names[(int)o];

  public static bool TryParse(string text, out EditorOrient o)
  {
    int i = Array.IndexOf(Names, text);
    o = i < 0 ? EditorOrient.None : (EditorOrient)i;
    return i >= 0;
  }

  /// <summary>Where output pixel (r, c) of an n x n picture comes from in the input.</summary>
  public static (int Row, int Column) Map(EditorOrient o, int r, int c, int n) => o switch
  {
    EditorOrient.FlipY => (n - 1 - r, c),
    EditorOrient.FlipX => (r, n - 1 - c),
    EditorOrient.Rot180 => (n - 1 - r, n - 1 - c),
    EditorOrient.Transpose => (c, r),
    EditorOrient.AntiTranspose => (n - 1 - c, n - 1 - r),
    EditorOrient.Cw90 => (n - 1 - c, r),
    EditorOrient.Ccw90 => (c, n - 1 - r),
    _ => (r, c),
  };

  /// <summary>Output rows r0 .. r0 + rows - 1 of the turned picture into <paramref name="to"/> (rows x n values), read from <paramref name="src"/> a row at a time.
  /// The rotations and mirrors that swap rows with columns read the whole picture once for the rows asked for.</summary>
  public static void FillRows(HeightSource src, EditorOrient o, int r0, int rows, float[] to, float[] scratch, Func<bool>? cancelled = null)
  {
    int n = src.Width;
    switch (o)
    {
      case EditorOrient.None:
      case EditorOrient.FlipY:
      case EditorOrient.FlipX:
      case EditorOrient.Rot180:
        for (int i = 0; i < rows; i++)
        {
          bool flipRows = o == EditorOrient.FlipY || o == EditorOrient.Rot180, flipCols = o == EditorOrient.FlipX || o == EditorOrient.Rot180;
          src.ReadRow(flipRows ? n - 1 - (r0 + i) : r0 + i, scratch);
          if (flipCols)
            for (int x = 0; x < n; x++)
              to[i * n + x] = scratch[n - 1 - x];
          else
            Buffer.BlockCopy(scratch, 0, to, i * n * 4, n * 4);
        }
        return;
    }
    for (int s = 0; s < n; s++)
    {
      if (cancelled != null && cancelled())
        throw new OperationCanceledException();
      src.ReadRow(s, scratch);
      for (int i = 0; i < rows; i++)
      {
        int r = r0 + i;
        switch (o)
        {
          case EditorOrient.Transpose: to[i * n + s] = scratch[r]; break;
          case EditorOrient.AntiTranspose: to[i * n + (n - 1 - s)] = scratch[n - 1 - r]; break;
          case EditorOrient.Cw90: to[i * n + (n - 1 - s)] = scratch[r]; break;
          default: to[i * n + s] = scratch[n - 1 - r]; break;
        }
      }
    }
  }
}

internal sealed class IngestOptions
{
  /// <summary>The folder or file with the editor's result: default editor/return/ of the export.</summary>
  public string? Files;
  public EditorUnit Unit = EditorUnit.V;
  public bool UnitGiven;
  public double RangeLo, RangeHi;
  public float? Amount;
  public bool AmountAuto;
  public float? SeaLevel;
  public bool SeaAuto;
  /// <summary>null: found by comparing with the pack's own heights.</summary>
  public EditorOrient? Orient;
  public bool RingKeep = true;
  /// <summary>Fine bits of the heightmap-fine.png of the result: 8 (the default: about a millimetre at Heightmap Amount 2), 4, or 0 for none.</summary>
  public int FineBits = 8;
  /// <summary>The side of a headerless file, when it is not the square root of its length.</summary>
  public int RawSize;
  /// <summary>Make the New World preset from the result at once.</summary>
  public bool Preset;
  /// <summary>Where the result goes: default a folder beside the export, named after it with -edited.</summary>
  public string? OutputDir;

  public const string Usage = "[unit=v|metres] [range=lo,hi] [amount=auto|<n>] [sealevel=auto|<s>] [orient=auto|none|flipy|flipx|rot180|transpose|antitranspose|cw90|ccw90] "
                              + "[ering=keep|gaea] [fine=8|4|off] [files=<path>] [size=<n>] [preset]";

  /// <summary>Splits the text after "bc_import edited" into options and the export (what is left, so a folder with spaces needs no quotes).</summary>
  public static bool TryParse(string text, out IngestOptions options, out string exportText, out string error)
  {
    options = new IngestOptions();
    exportText = "";
    error = "";
    var rest = new List<string>();
    foreach (var raw in Tokens(text))
    {
      var token = raw;
      var lower = token.ToLowerInvariant();
      int eq = token.IndexOf('=');
      if (eq <= 0)
      {
        if (lower == "preset") { options.Preset = true; continue; }
        rest.Add(token);
        continue;
      }
      var key = lower.Substring(0, eq);
      var value = token.Substring(eq + 1).Trim('"');
      var lowerValue = value.ToLowerInvariant();
      bool ok = true;
      switch (key)
      {
        case "unit":
          if (lowerValue == "v") options.Unit = EditorUnit.V;
          else if (lowerValue == "metres" || lowerValue == "meters") options.Unit = EditorUnit.Metres;
          else ok = false;
          options.UnitGiven = ok;
          break;
        case "range":
        {
          var parts = lowerValue.Split(',');
          ok = parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out options.RangeLo)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out options.RangeHi) && options.RangeHi != options.RangeLo;
          options.Unit = EditorUnit.Range;
          options.UnitGiven = true;
          break;
        }
        case "amount":
          if (lowerValue == "auto") options.AmountAuto = true;
          else
          {
            ok = float.TryParse(lowerValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0f && a <= WorldExport.MaxHeightmapAmount;
            options.Amount = a;
          }
          break;
        case "sealevel":
          if (lowerValue == "auto") options.SeaAuto = true;
          else
          {
            ok = float.TryParse(lowerValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0f && s <= 1f;
            options.SeaLevel = s;
          }
          break;
        case "orient":
          if (lowerValue == "auto") options.Orient = null;
          else
          {
            ok = EditorOrientation.TryParse(lowerValue, out var o);
            options.Orient = o;
          }
          break;
        case "ering":
          if (lowerValue == "keep") options.RingKeep = true;
          else if (lowerValue == "gaea") options.RingKeep = false;
          else ok = false;
          break;
        case "fine":
          if (lowerValue == "8" || lowerValue == "auto") options.FineBits = 8;
          else if (lowerValue == "4") options.FineBits = 4;
          else if (lowerValue == "off") options.FineBits = 0;
          else ok = false;
          break;
        case "files":
          options.Files = value;
          break;
        case "size":
          ok = int.TryParse(lowerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out options.RawSize) && options.RawSize > 0;
          break;
        case "out":
          options.OutputDir = value;
          break;
        default:
          rest.Add(token);
          continue;
      }
      if (!ok)
      {
        error = $"cannot read '{token}'";
        return false;
      }
    }
    exportText = string.Join(" ", rest);
    return true;
  }

  // Spaces separate tokens, except inside double quotes (files="C:\My Maps\height.tif" or "key=a b").
  private static IEnumerable<string> Tokens(string text)
  {
    var sb = new StringBuilder();
    bool quoted = false;
    foreach (var c in text)
    {
      if (c == '"')
      {
        quoted = !quoted;
        sb.Append(c);
      }
      else if ((c == ' ' || c == '\t') && !quoted)
      {
        if (sb.Length > 0)
          yield return sb.ToString();
        sb.Clear();
      }
      else
        sb.Append(c);
    }
    if (sb.Length > 0)
      yield return sb.ToString();
  }
}

internal sealed class IngestResult
{
  public string? Error;
  public string? OutDir;
  public readonly List<string> Lines = [];
  public EditorOrient Orientation;
  public bool OrientationFound;
  public int Size, SourceSize;
  public float Amount, SeaLevel;
  public long ClippedLow, ClippedHigh, Restored;
  public double MinMetres = double.NaN, MaxMetres = double.NaN;
  public int FineBitsWritten;
  public string ReturnFile = "";
  public double[] OrientationScores = [];
}

internal static class EditorIngest
{
  public const string FolderSuffix = "-edited";

  public static bool IsRunning { get; private set; }
  public static string Phase { get; private set; } = "";
  public static float Progress { get; private set; }
  public static string? LastResult { get; private set; }
  public static string? LastError { get; private set; }
  private static volatile bool cancelRequested;

  /// <summary>Rows in a band; 0 (usual) picks about two million pixels. The tests set it to see that the bytes do not depend on it.</summary>
  internal static int BandRowsOverride;

  public static void Cancel() => cancelRequested = true;

  public static List<string> StatusLines()
  {
    var lines = new List<string>();
    if (IsRunning)
      lines.Add($"Editor return: {Phase} {Progress:0}%");
    if (LastResult != null)
      lines.Add($"Last editor return: {LastResult}");
    return lines;
  }

  // ---- bc_import edited ----------------------------------------------------------------------------------------------------

  /// <summary>The console command: "bc_import edited ..." (the text after the word). Returns false when it was not for this command at all.</summary>
  public static bool TryRunLine(string line, Action<string> output)
  {
    var trimmed = (line ?? "").Trim();
    var lower = trimmed.ToLowerInvariant();
    if (lower != "edited" && !lower.StartsWith("edited ") && !lower.StartsWith("edited\t"))
      return false;
    var tail = trimmed.Substring("edited".Length).Trim();
    if (tail.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
      Help(output);
      return true;
    }
    if (tail.Equals("cancel", StringComparison.OrdinalIgnoreCase))
    {
      if (IsRunning) { Cancel(); output("bc_import edited: cancelling; the new folder will be deleted."); }
      else output("bc_import edited: nothing is running.");
      return true;
    }
    if (!IngestOptions.TryParse(tail, out var options, out var exportText, out var error))
    {
      output($"bc_import edited: {error}.");
      return true;
    }
    var folder = WorldImport.Resolve(exportText, out var how, out error);
    if (folder == null)
    {
      output($"bc_import edited: {error}.");
      return true;
    }
    output($"bc_import edited: {folder.Path} ({how}).");
    Start(folder.Path, options, output);
    return true;
  }

  public static void Help(Action<string> output)
  {
    output("bc_import edited [<number> | <folder> | <world>] " + IngestOptions.Usage);
    output("  Reads the ground your editor saved in <export>/editor/return/ (height.tif, height.r32 or a 16-bit PNG), turns it into heightmap.png and");
    output("  heightmap-fine.png in a new folder <export>-edited beside the export, copies the other maps unchanged, and leaves the export as it was.");
    output("  Then 'bc_import <that folder>' makes the New World preset (or add 'preset' to do both).");
    output("  orient=auto finds out whether the editor turned or mirrored the picture; unit=v (the default) reads editor.json's encoding, unit=metres or");
    output("  range=lo,hi a build that wrote metres or a normalised range; amount=auto / sealevel=auto pick an encoding that holds the edited heights.");
    output("  bc_import edited cancel stops a running one; bc_import status shows it.");
  }

  /// <summary>Runs the ingest on a worker and finishes on the main thread (the optional preset, the log). In a game only.</summary>
  public static bool Start(string exportDir, IngestOptions options, Action<string> output)
  {
    void Say(string line, bool warning = false)
    {
      if (warning)
        LogWarning(line);
      else
        Log(line);
      try
      {
        output(line);
      }
      catch
      {
        // The console that asked is gone; the log has the line.
      }
    }
    if (IsRunning)
    {
      Say("bc_import edited: one is already running; 'bc_import status' shows it.", true);
      return false;
    }
    if (instance == null)
    {
      Say("bc_import edited: Better Continents is not initialised.", true);
      return false;
    }
    if (WorldExport.IsRunning && WorldExport.RunningDir != null
        && string.Equals(Path.GetFullPath(WorldExport.RunningDir).TrimEnd('/', '\\'), Path.GetFullPath(exportDir).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
    {
      Say("bc_import edited: that export is still being written; bring the heights back when it has finished.", true);
      return false;
    }
    IsRunning = true;
    cancelRequested = false;
    Phase = "Starting";
    Progress = 0f;
    LastError = null;
    IngestResult? result = null;
    // The worker's lines wait here for the main thread: the game's console is not for other threads.
    var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var task = Task.Factory.StartNew(() => result = Run(exportDir, options, line => lines.Enqueue(line), () => cancelRequested,
      (phase, percent) => { Phase = phase; Progress = percent; }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    instance.StartCoroutine(Finish(task, () => result, lines, options, Say));
    return true;
  }

  private static IEnumerator Finish(Task task, Func<IngestResult?> result, System.Collections.Concurrent.ConcurrentQueue<string> lines, IngestOptions options, Action<string, bool> say)
  {
    while (!task.IsCompleted)
    {
      while (lines.TryDequeue(out var line))
        say("bc_import edited: " + line, false);
      yield return null;
    }
    while (lines.TryDequeue(out var last))
      say("bc_import edited: " + last, false);
    IsRunning = false;
    Phase = "";
    var r = result();
    if (task.IsFaulted || r == null)
    {
      LastError = task.Exception?.GetBaseException().Message ?? "failed";
      LastResult = "failed: " + LastError;
      say("bc_import edited: failed: " + LastError, true);
      yield break;
    }
    if (r.Error != null)
    {
      LastError = r.Error;
      LastResult = "failed: " + r.Error;
      if (r.Error != "cancelled")
        say("bc_import edited: " + r.Error + ".", true);
      yield break;
    }
    LastResult = $"{r.OutDir} ({r.Size} px)";
    if (options.Preset && r.OutDir != null)
      WorldImport.Start(r.OutDir, WorldImport.Mode.Preset, line => say(line, false));
  }

  // ---- the ingest -------------------------------------------------------------------------------------------------------------

  /// <summary>Makes the edited folder from the export at <paramref name="exportDir"/>. Never throws for a bad input: the result's Error says what was wrong. A
  /// cancel (<paramref name="cancelled"/>) deletes what was made and gives Error "cancelled".</summary>
  public static IngestResult Run(string exportDir, IngestOptions o, Action<string> say, Func<bool>? cancelled = null, Action<string, float>? progress = null)
  {
    var res = new IngestResult();
    string? madeDir = null;
    var sw = Stopwatch.StartNew();
    void Report(string phase, float percent) => progress?.Invoke(phase, percent);
    bool Cancelled() => cancelled != null && cancelled();
    try
    {
      Report("Reading the export", 0f);
      exportDir = Path.GetFullPath(exportDir).TrimEnd('/', '\\');
      var manifestPath = Path.Combine(exportDir, "manifest.json");
      if (!File.Exists(manifestPath))
        return Fail(res, $"{exportDir} has no manifest.json: it is not a finished export");
      var manifest = MiniJson.ParseObject(File.ReadAllText(manifestPath));
      if (manifest == null)
        return Fail(res, "manifest.json cannot be read");
      var editorPath = Path.Combine(exportDir, "editor", "editor.json");
      var editor = File.Exists(editorPath) ? MiniJson.ParseObject(File.ReadAllText(editorPath)) : null;
      if (File.Exists(editorPath) && editor == null)
        return Fail(res, "editor/editor.json cannot be read");
      int sourceSize = (int)(manifest.Num("size") ?? 0);
      float total = (float)(manifest.Num("totalSize") ?? 0), worldR = (float)(manifest.Num("worldRadius") ?? 0), totalR = (float)(manifest.Num("totalRadius") ?? 0);
      if (sourceSize < 2 || total <= 0f)
        return Fail(res, "manifest.json has no size or totalSize");
      bool edgeDropoff = manifest.Bool("edgeDropoff") ?? false;
      var grid = editor?.Child("grid");
      var packHeight = editor?.Child("height");
      int packSize = (int)(grid?.Num("size") ?? sourceSize);
      res.SourceSize = sourceSize;

      // ---- the encoding of the pack (unit v) and of the heightmap.png of the export (ring pixels)
      double packAmount = packHeight?.Num("amount") ?? manifest.Num("heightmapAmount") ?? 1.0;
      double packSea = packHeight?.Num("seaLevel") ?? manifest.Num("seaLevel") ?? 0.5;
      double packSla = packHeight?.Num("seaLevelAdjustment") ?? WorldExportMath.SeaLevelAdjustment((float)packSea);
      double srcAmount = manifest.Num("heightmapAmount") ?? 1.0, srcSea = manifest.Num("seaLevel") ?? 0.5;
      double srcSla = manifest.Num("seaLevelAdjustment") ?? WorldExportMath.SeaLevelAdjustment((float)srcSea);

      // ---- the editor's file
      var returnPath = FindReturn(exportDir, o, out var findError);
      if (returnPath == null)
        return Fail(res, findError!);
      res.ReturnFile = returnPath;
      using var source = HeightSource.Open(returnPath, o.RawSize);
      int m = source.Width;
      res.Size = m;
      say($"{Path.GetFileName(returnPath)}: {m} x {m} pixels, {source.Kind}.");
      foreach (var w in source.Warnings)
        say("Note: " + w);
      if (m < 16 || m > HeightSource.MaxSide)
        return Fail(res, $"{m} px: Better Continents reads maps from 16 to {HeightSource.MaxSide} pixels across");
      if (m != sourceSize)
        say($"The returned height is {m} px and the export's other maps are {sourceSize} px: every map is read by its position on the world, so the sizes may differ.");
      if (o.Unit == EditorUnit.V && editor == null)
        return Fail(res, "there is no editor/editor.json to say what the values mean: give unit=metres, or range=lo,hi");
      if (Cancelled()) throw new OperationCanceledException();

      // ---- the scan: a coarse picture for the orientation, and the range
      var packFile = FindPackHeight(exportDir);
      int small = Math.Min(256, Math.Min(m, packFile != null ? packSize : m));
      Report("Looking at the heights", 5f);
      var returned = Reduce(source, small, Cancelled, out var minF, out var maxF, out var nonFinite);
      if (nonFinite > 0)
        say($"{nonFinite} pixel(s) of the returned height are not numbers; they are restored from the export.");
      double[]? reference = null;
      if (packFile != null && o.Orient == null)
      {
        try
        {
          using var pack = HeightSource.Open(packFile);
          reference = Reduce(pack, small, Cancelled, out _, out _, out _);
        }
        catch (EditorReadException e)
        {
          say($"Note: the pack's own height ({Path.GetFileName(packFile)}) cannot be read to compare with: {e.Message}");
        }
      }
      EditorOrient orient;
      if (o.Orient != null)
      {
        orient = o.Orient.Value;
        say($"Orientation: {EditorOrientation.Name(orient)} (as asked).");
      }
      else if (reference == null)
      {
        orient = EditorOrient.None;
        say("Orientation: not checked (there is no editor/height.* to compare with), taken as it is; orient= says otherwise.");
      }
      else
      {
        orient = ChooseOrientation(returned, reference, small, out var scores, out var why);
        res.OrientationScores = scores;
        if (why != null)
          return Fail(res, why);
        res.OrientationFound = true;
        say(orient == EditorOrient.None ? "Orientation: as the pack's (nothing to turn)." : $"Orientation: the returned picture was turned; undone with '{EditorOrientation.Name(orient)}'.");
      }
      res.Orientation = orient;
      if (Cancelled()) throw new OperationCanceledException();

      // ---- units
      Func<double, double> toMetres;
      switch (o.Unit)
      {
        case EditorUnit.Metres:
          toMetres = f => f;
          break;
        case EditorUnit.Range:
          double lo = o.RangeLo, hi = o.RangeHi;
          toMetres = f => lo + f * (hi - lo);
          break;
        default:
          double a = packAmount, sla = packSla;
          toMetres = f => (f * a - WorldExportMath.BaseOffset + sla) * WorldExportMath.HeightScale;
          var vr = packHeight?["valueRange"] as List<object?>;
          double packMax = vr is { Count: 2 } && vr[1] is double pm ? pm : 1.0;
          if (!o.UnitGiven && Math.Abs(minF) < 1e-6 && Math.Abs(maxF - 1.0) < 1e-6 && packMax < 0.98)
            return Fail(res, "the returned heights span exactly 0 to 1 while the pack's span 0 to " + packMax.ToString("0.###", CultureInfo.InvariantCulture)
                             + ": this looks like a Normalized build. Build with Output Range = Raw, or say what the range is (range=lo,hi, in metres), or unit=v to read it as it is");
          break;
      }
      double mMin = toMetres(minF), mMax = toMetres(maxF);
      if (mMin > mMax) { var t = mMin; mMin = mMax; mMax = t; }
      res.MinMetres = mMin;
      res.MaxMetres = mMax;

      // ---- the encoding of the result
      double defaultAmount = packHeight?.Num("amount") ?? srcAmount, defaultSea = packHeight?.Num("seaLevel") ?? srcSea;
      double outSea = o.SeaLevel ?? defaultSea;
      if (o.SeaAuto)
      {
        outSea = defaultSea;
        while (outSea < 1.0 && (-WorldExportMath.BaseOffset + WorldExportMath.SeaLevelAdjustment((float)outSea)) * WorldExportMath.HeightScale > mMin)
          outSea = Math.Min(1.0, Math.Round(outSea + 0.05, 2));
      }
      double outSla = WorldExportMath.SeaLevelAdjustment((float)outSea);
      double outAmount = o.Amount ?? defaultAmount;
      if (o.AmountAuto)
      {
        outAmount = WorldExport.MaxHeightmapAmount;
        foreach (var candidate in new[] { 2.0, 3, 4, 5, 6, 8, 10, 16, 32, 64, 81 })
          if (WorldExportMath.ValueToMetres(1f, (float)candidate, (float)outSea) >= mMax && candidate >= Math.Min(defaultAmount, 2.0))
          {
            outAmount = candidate;
            break;
          }
      }
      res.Amount = (float)outAmount;
      res.SeaLevel = (float)outSea;
      float floorM = WorldExportMath.ValueToMetres(0f, res.Amount, res.SeaLevel), ceilM = WorldExportMath.ValueToMetres(1f, res.Amount, res.SeaLevel);
      say($"Heights {mMin:0.#} m to {mMax:0.#} m; encoded for Heightmap Amount {Inv(res.Amount)} and Sea Level {Inv(res.SeaLevel)} ({floorM:0.#} m to {ceilM:0.#} m).");

      // ---- the folder
      string name = Path.GetFileName(exportDir) + FolderSuffix;
      string parent = Path.GetDirectoryName(exportDir) ?? exportDir;
      string outDir = o.OutputDir != null ? Path.GetFullPath(o.OutputDir) : Path.Combine(parent, name);
      if (o.OutputDir == null)
        for (int k = 2; Directory.Exists(outDir); k++)
          outDir = Path.Combine(parent, $"{name}-{k}");
      else if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        return Fail(res, $"{outDir} is not empty");
      Directory.CreateDirectory(outDir);
      madeDir = outDir;
      res.OutDir = outDir;

      // ---- the ring: the export's own pixels past the world's edge
      RingSource? ring = null;
      var srcHeightmap = Path.Combine(exportDir, "heightmap.png");
      bool ringWanted = edgeDropoff && o.RingKeep;
      if (ringWanted)
      {
        if (m != sourceSize)
          say("The edge ring is taken from the returned height (its size is not the export's).");
        else if (!File.Exists(srcHeightmap))
          say("The edge ring is taken from the returned height (the export has no heightmap.png).");
        else
          try
          {
            ring = new RingSource(srcHeightmap);
            if (ring.Size != m) { ring.Dispose(); ring = null; }
            else if (ring.Record != null) { srcAmount = ring.Record.Amount; srcSla = WorldExportMath.SeaLevelAdjustment(ring.Record.SeaLevel); }
          }
          catch (Exception e)
          {
            say($"The edge ring is taken from the returned height (heightmap.png cannot be read: {e.Message}).");
          }
      }

      // ---- the heights
      using (ring)
        WriteHeights(res, source, orient, toMetres, ring, srcAmount, srcSla, outAmount, outSea, total, worldR, totalR, edgeDropoff, o.FineBits, outDir, say, Cancelled, Report);

      // ---- the rest of the folder
      Report("Copying the other maps", 92f);
      var copied = CopyMaps(exportDir, outDir);
      var cfg = EditConfig(exportDir, outDir, res.Amount, res.SeaLevel);
      WorldExportPng.WriteText(Path.Combine(outDir, "export.cfg"), cfg);
      var written = new List<string>();
      foreach (var f in Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal))
        if (f != "manifest.json" && !f.EndsWith(".tmp"))
          written.Add(f!);
      written.Add("manifest.json");
      WorldExportPng.WriteText(Path.Combine(outDir, "README.txt"), Readme(exportDir, res, copied));
      WorldExportPng.WriteText(Path.Combine(outDir, "manifest.json"), [ManifestFor(manifest, editor, exportDir, outDir, res, orient, o, written)]);
      Report("Done", 100f);
      say($"Done in {sw.Elapsed.TotalSeconds:0} s: {outDir}");
      say($"Next: bc_import {ImportArg(outDir)}   (makes the New World preset from the edited maps)");
      return res;
    }
    catch (OperationCanceledException)
    {
      res.Error = "cancelled";
      if (madeDir != null)
        DeleteMade(madeDir);
      res.OutDir = null;
      say("Cancelled; the new folder was deleted.");
      return res;
    }
    catch (Exception e) when (e is EditorReadException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
    {
      res.Error = e.Message;
      if (madeDir != null)
        DeleteMade(madeDir);
      res.OutDir = null;
      return res;
    }
  }

  private static IngestResult Fail(IngestResult r, string error)
  {
    r.Error = error;
    return r;
  }

  private static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

  // The folder this run made, and nothing else: it was created empty by this run.
  private static void DeleteMade(string dir)
  {
    try
    {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
    catch (Exception e)
    {
      LogWarning($"bc_import edited: could not delete {dir}: {e.Message}");
    }
  }

  // ---- finding the files --------------------------------------------------------------------------------------------------------

  private static readonly string[] ReturnExtensions = [".tif", ".tiff", ".r32", ".raw", ".r16", ".png", ".exr"];

  internal static string? FindReturn(string exportDir, IngestOptions o, out string? error)
  {
    error = null;
    var where = o.Files != null ? Path.IsPathRooted(o.Files) ? o.Files : Path.Combine(exportDir, o.Files) : Path.Combine(exportDir, "editor", "return");
    if (File.Exists(where))
      return where;
    if (!Directory.Exists(where))
    {
      error = o.Files != null ? $"{where} does not exist" : $"there is no folder {where}: build the height in your editor and save it there";
      return null;
    }
    foreach (var preferred in new[] { "height.tif", "height.tiff", "height.r32", "height.raw", "height.png" })
    {
      var p = Path.Combine(where, preferred);
      if (File.Exists(p))
        return p;
    }
    var candidates = Directory.GetFiles(where).Where(f => ReturnExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.Ordinal).ToList();
    if (candidates.Count == 1)
      return candidates[0];
    error = candidates.Count == 0
      ? $"{where} holds no height file (.tif, .r32, .raw or 16-bit .png): save the editor's height there as height.tif"
      : $"{where} holds several files ({string.Join(", ", candidates.Select(Path.GetFileName))}): name the one with files=<path>, or save it as height.tif";
    return null;
  }

  private static string? FindPackHeight(string exportDir)
  {
    foreach (var n in new[] { "height.tif", "height.r32" })
    {
      var p = Path.Combine(exportDir, "editor", n);
      if (File.Exists(p))
        return p;
    }
    return null;
  }

  // ---- the coarse picture and the orientation ------------------------------------------------------------------------------------

  // The picture as small x small means (every pixel in the cell it falls in), one pass over the file; also its range.
  internal static double[] Reduce(HeightSource s, int small, Func<bool> cancelled, out double min, out double max, out long nonFinite)
  {
    int n = s.Width;
    var sums = new double[small * small];
    var counts = new long[small * small];
    var row = new float[n];
    var cell = new int[n];
    for (int x = 0; x < n; x++)
      cell[x] = (int)((long)x * small / n);
    min = double.MaxValue;
    max = double.MinValue;
    nonFinite = 0;
    for (int y = 0; y < n; y++)
    {
      if ((y & 63) == 0 && cancelled())
        throw new OperationCanceledException();
      s.ReadRow(y, row);
      int baseCell = (int)((long)y * small / n) * small;
      for (int x = 0; x < n; x++)
      {
        float f = row[x];
        if (float.IsNaN(f) || float.IsInfinity(f))
        {
          nonFinite++;
          continue;
        }
        if (f < min) min = f;
        if (f > max) max = f;
        sums[baseCell + cell[x]] += f;
        counts[baseCell + cell[x]]++;
      }
    }
    if (min > max)
    {
      min = max = 0;
    }
    for (int i = 0; i < sums.Length; i++)
      sums[i] = counts[i] > 0 ? sums[i] / counts[i] : 0.0;
    return sums;
  }

  // The turn that makes the returned coarse picture most like the pack's own (both with their mean and spread taken out, so a rescaled
  // build still compares), if one is clearly the best: the runner-up's error must be at least 4 times as big.
  internal static EditorOrient ChooseOrientation(double[] returned, double[] reference, int n, out double[] scores, out string? problem)
  {
    problem = null;
    var norm = Normalize(reference);
    scores = new double[8];
    if (norm == null)
    {
      problem = "the pack's own height is flat, so the orientation cannot be found: say orient=none (or the turn) yourself";
      return EditorOrient.None;
    }
    for (int k = 0; k < 8; k++)
    {
      var turned = new double[n * n];
      var o = (EditorOrient)k;
      for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
        {
          var (sr, sc) = EditorOrientation.Map(o, r, c, n);
          turned[r * n + c] = returned[sr * n + sc];
        }
      var t = Normalize(turned);
      if (t == null)
      {
        scores[k] = double.PositiveInfinity;
        continue;
      }
      double e = 0;
      for (int i = 0; i < t.Length; i++)
      {
        double d = t[i] - norm[i];
        e += d * d;
      }
      scores[k] = e / t.Length;
    }
    int best = 0;
    for (int k = 1; k < 8; k++)
      if (scores[k] < scores[best]) best = k;
    if (double.IsInfinity(scores[best]))
    {
      problem = "the returned height is flat, so the orientation cannot be found: check the build, or say orient= yourself";
      return EditorOrient.None;
    }
    double second = double.MaxValue;
    for (int k = 0; k < 8; k++)
      if (k != best && scores[k] < second) second = scores[k];
    if (second < scores[best] * 4.0 + 1e-9)
    {
      var shown = scores;
      problem = "cannot tell how the returned picture is turned (" + string.Join(", ", Enumerable.Range(0, 8).Select(k => $"{EditorOrientation.Names[k]} {shown[k]:0.####}"))
                + "): say orient=none, flipy, flipx, rot180, transpose, antitranspose, cw90 or ccw90";
      return EditorOrient.None;
    }
    return (EditorOrient)best;
  }

  private static double[]? Normalize(double[] a)
  {
    double mean = a.Average();
    double variance = 0;
    foreach (var x in a)
      variance += (x - mean) * (x - mean);
    variance /= a.Length;
    if (!(variance > 1e-18))
      return null;
    double sd = Math.Sqrt(variance);
    return a.Select(x => (x - mean) / sd).ToArray();
  }

  // ---- the edge ring of the export's heightmap.png ----------------------------------------------------------------------------------

  private sealed class RingSource : IDisposable
  {
    private readonly MapPicture<L16> picture;
    private ushort[]? cache;
    private int cachedRow = -1;

    public int Size => picture.Width;
    public HeightmapRecord? Record { get; }

    public RingSource(string path)
    {
      var data = File.ReadAllBytes(path);
      MapPicture<L16>? p = PngRows<L16>.TryOpen(data);
      p ??= new ImagePicture<L16>(SixLabors.ImageSharp.Image.Load<L16>(SixLabors.ImageSharp.Configuration.Default, data));
      picture = p;
      Record = HeightmapRecord.From(picture.Text);
    }

    // File rows first .. first + count - 1 into dest (count x Size values).
    public void Fetch(int first, int count, ushort[] dest)
    {
      int n = picture.Width;
      int start = first;
      if (cache != null && cachedRow == first)
      {
        Array.Copy(cache, 0, dest, 0, n);
        start++;
      }
      if (start < first + count)
      {
        int cnt = first + count - start;
        int s0 = start;
        picture.ReadRows(picture.Height - start - cnt, cnt, (k, pixels) =>
        {
          int fileRow = s0 + (cnt - 1 - k);
          int at = (fileRow - first) * n;
          for (int x = 0; x < pixels.Length; x++)
            dest[at + x] = pixels[x].PackedValue;
        });
      }
      cache ??= new ushort[n];
      Array.Copy(dest, (count - 1) * n, cache, 0, n);
      cachedRow = first + count - 1;
    }

    public void Dispose() => picture.Dispose();
  }

  // ---- heightmap.png and heightmap-fine.png ------------------------------------------------------------------------------------------

  private struct RowStats
  {
    public long Low, High, Restored;
    public double Min, Max;
  }

  private static void WriteHeights(IngestResult res, HeightSource source, EditorOrient orient, Func<double, double> toMetres, RingSource? ring,
    double srcAmount, double srcSla, double amount, double sea, float total, float worldR, float totalR, bool edgeDropoff, int fineBits, string outDir,
    Action<string> say, Func<bool> cancelled, Action<string, float> report)
  {
    double sla = WorldExportMath.SeaLevelAdjustment((float)sea);
    int m = source.Width;
    bool transposed = orient is EditorOrient.Transpose or EditorOrient.AntiTranspose or EditorOrient.Cw90 or EditorOrient.Ccw90;
    int bandRows = EditorIngest.BandRowsOverride > 0 ? Math.Min(m, EditorIngest.BandRowsOverride) : Math.Max(1, Math.Min(m, (transposed ? 1 << 23 : 1 << 21) / m));
    int workers = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));
    int lookAhead = fineBits > 0 ? 1 : 0;
    var heightPath = Path.Combine(outDir, "heightmap.png");
    var finePath = Path.Combine(outDir, "heightmap-fine.png");
    var record = new HeightmapRecord((float)amount, (float)sea);
    using var heightWriter = new PngRowWriter(heightPath, m, m, 1, 16, HeightmapRecord.Keyword, record.Text);
    using var fineWriter = fineBits > 0 ? new PngRowWriter(finePath, m, m, 1, 8) : null;
    var fine = fineBits > 0 ? new WorldExport.FineBands(m, fineBits, bandRows, workers) : null;
    var sets = new (byte[] Grey, byte[]? Fine)[2];
    for (int s = 0; s < 2; s++)
      sets[s] = (new byte[(bandRows + lookAhead) * m * 2], fineBits > 0 ? new byte[(bandRows + lookAhead) * m] : null);
    var vals = new float[(bandRows + lookAhead) * m];
    var scratch = new float[m];
    var ringBuf = ring != null ? new ushort[(bandRows + lookAhead) * m] : null;
    var stats = new RowStats[m];
    Task? writing = null;
    bool fineAtAll = fineBits > 0;
    double off = WorldExportMath.BaseOffset, scale = WorldExportMath.HeightScale;
    try
    {
      for (int first = 0, set = 0; first < m; first += bandRows, set ^= 1)
      {
        if (cancelled())
          throw new OperationCanceledException();
        report("Writing heightmap.png", 10f + 80f * first / m);
        int count = Math.Min(bandRows, m - first);
        int extra = Math.Min(lookAhead, m - (first + count));
        int rows = count + extra;
        EditorOrientation.FillRows(source, orient, first, rows, vals, scratch, cancelled);
        ring?.Fetch(first, rows, ringBuf!);
        var (grey, fineBytes) = sets[set];
        int w = Math.Min(workers, Math.Max(1, rows));
        int f0 = first;
        GameUtils.SimpleParallelFor(w, 0, w, k =>
        {
          for (int rb = k; rb < rows; rb += w)
          {
            int fileRow = f0 + rb;
            float wz = WorldExportMath.FileRowToWorldZ(fileRow, m, total);
            var st = new RowStats { Min = double.MaxValue, Max = double.MinValue };
            for (int col = 0; col < m; col++)
            {
              float wx = WorldExportMath.PixelToWorld(col, m, total);
              int i = rb * m + col;
              float f = vals[i];
              float d = Mathf.Sqrt(wx * wx + wz * wz);
              bool inside = d <= worldR;
              bool kept = false;
              double metres;
              if (edgeDropoff && !inside && ringBuf != null)
              {
                // The export's own pixel: the height before the drop-off, as it was written.
                metres = ((ringBuf[i] / 65535.0) * srcAmount - off + srcSla) * scale;
                kept = true;
              }
              else if (float.IsNaN(f) || float.IsInfinity(f))
              {
                st.Restored++;
                metres = ringBuf != null ? ((ringBuf[i] / 65535.0) * srcAmount - off + srcSla) * scale : (0.0 * amount - off + sla) * scale;
                kept = ringBuf != null;
              }
              else
                metres = toMetres(f);
              double fh = metres / scale;
              if (edgeDropoff && !inside && !kept)
              {
                float fhf = (float)fh;
                WorldExportMath.TryUndoEdgeDropoff(fhf, d, worldR, totalR, out fhf);
                fh = fhf;
              }
              double v = (fh + off - sla) / amount;
              double x = !(v > 0.0) ? 0.0 : v >= 1.0 ? 65535.0 : v * 65535.0;
              ushort c = (ushort)(x + 0.5);
              grey[2 * i] = (byte)(c >> 8);
              grey[2 * i + 1] = (byte)c;
              if (fine != null)
                fine.Pixel(fineBytes!, rb, col, x, c, kept || metres < WorldExportMath.WaterLevel);
              if (inside && rb < count)
              {
                if (!(v >= 0.0)) st.Low++;
                else if (v > 1.0) st.High++;
                if (metres < st.Min) st.Min = metres;
                if (metres > st.Max) st.Max = metres;
              }
            }
            if (rb < count)
              stats[fileRow] = st;
          }
        });
        fine?.Finish(fineBytes!, first, count, extra, cancelled);
        if (cancelled())
          throw new OperationCanceledException();
        writing?.Wait();
        int cnt = count;
        var buffers = (grey, fineBytes);
        writing = Task.Factory.StartNew(() =>
        {
          heightWriter.WriteRows(buffers.grey, 0, cnt);
          fineWriter?.WriteRows(buffers.fineBytes!, 0, cnt);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
      }
      writing?.Wait();
    }
    catch
    {
      try { writing?.Wait(); } catch { /* the failure that matters is the one being thrown */ }
      throw;
    }
    foreach (var s in stats)
    {
      res.ClippedLow += s.Low;
      res.ClippedHigh += s.High;
      res.Restored += s.Restored;
    }
    heightWriter.Complete();
    int bits = fine == null ? 0 : WorldExportMath.FineBitsUsed(fine.Or);
    if (fineWriter != null)
    {
      if (bits == 0)
      {
        fineWriter.Dispose();
        say("No pixel needs fine heights (under water, steep, or on whole steps), so there is no heightmap-fine.png.");
      }
      else
      {
        uint crc = PngRowWriter.FileCrc32(heightPath);
        fineWriter.Complete(HeightmapRecord.Keyword, WorldExportMath.FineRecordText(bits, crc));
        res.FineBitsWritten = bits;
      }
    }
    if (res.ClippedLow > 0 || res.ClippedHigh > 0)
      say($"{res.ClippedLow} pixel(s) inside the world were below {Inv(WorldExportMath.ValueToMetres(0f, res.Amount, res.SeaLevel))} m and {res.ClippedHigh} above "
          + $"{Inv(WorldExportMath.ValueToMetres(1f, res.Amount, res.SeaLevel))} m and were clipped: amount=auto keeps them.");
    if (res.Restored > 0)
      say($"{res.Restored} pixel(s) that were not numbers were restored from the export.");
    // The stats of the pixels inside the world replace the file-wide range.
    double lo = double.MaxValue, hi = double.MinValue;
    foreach (var s in stats)
      if (s.Min <= s.Max) { lo = Math.Min(lo, s.Min); hi = Math.Max(hi, s.Max); }
    if (lo <= hi) { res.MinMetres = lo; res.MaxMetres = hi; }
  }

  // ---- the rest of the folder ----------------------------------------------------------------------------------------------------

  private static readonly HashSet<string> NotCopied = new(StringComparer.OrdinalIgnoreCase)
    { "heightmap.png", "heightmap-fine.png", "export.cfg", "manifest.json", "README.txt" };

  // Every file of the export's top level but the heights and the text files that describe them, byte for byte (the location map too:
  // it is never turned, whatever the heights were). The editor pack and sources/ stay with the export.
  private static List<string> CopyMaps(string exportDir, string outDir)
  {
    var copied = new List<string>();
    foreach (var file in Directory.GetFiles(exportDir).OrderBy(f => f, StringComparer.Ordinal))
    {
      var name = Path.GetFileName(file);
      if (NotCopied.Contains(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        continue;
      File.Copy(file, Path.Combine(outDir, name));
      copied.Add(name);
    }
    return copied;
  }

  private static List<string> EditConfig(string exportDir, string outDir, float amount, float sea)
  {
    var lines = new List<string>();
    var path = Path.Combine(exportDir, "export.cfg");
    var source = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
    int firstSection = source.FindIndex(l => l.TrimStart().StartsWith("["));
    lines.Add($"## Better Continents export edited in an outside editor: the heights are from {Path.GetFileName(exportDir)}/editor/return/, the other maps are that export's.");
    lines.Add($"## Make the preset with   bc_import {ImportArg(outDir)}");
    lines.Add("");
    string section = "";
    bool amountDone = false, seaDone = false, dirDone = false;
    for (int i = Math.Max(0, firstSection); i < source.Count; i++)
    {
      var line = source[i];
      var trimmed = line.Trim();
      if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
        section = trimmed.Substring(1, trimmed.Length - 2);
      else if (!trimmed.StartsWith("#") && trimmed.Contains("="))
      {
        int eq = trimmed.IndexOf('=');
        var key = trimmed.Substring(0, eq).Trim();
        if (section == SettingsSchema.HeightmapAmount.Section && key == SettingsSchema.HeightmapAmount.Key)
        {
          line = $"{key} = {amount.ToString("R", CultureInfo.InvariantCulture)}";
          amountDone = true;
        }
        else if (section == SettingsSchema.SeaLevel.Section && key == SettingsSchema.SeaLevel.Key)
        {
          line = $"{key} = {sea.ToString("R", CultureInfo.InvariantCulture)}";
          seaDone = true;
        }
        else if (section == SettingsSchema.Directory.Section && key == SettingsSchema.Directory.Key)
        {
          line = $"{key} = {outDir.Replace('\\', '/')}";
          dirDone = true;
        }
      }
      lines.Add(line);
    }
    if (!dirDone || !amountDone || !seaDone)
    {
      // An export without heights in its settings: the lines that make heightmap.png the whole ground.
      void Add(SettingDef def, string value, string why)
      {
        lines.Add("");
        lines.Add($"[{def.Section}]");
        lines.Add("## " + why);
        lines.Add($"{def.Key} = {value}");
      }
      if (!dirDone) Add(SettingsSchema.Directory, outDir.Replace('\\', '/'), "Loads every map in this folder by its standard name.");
      if (!seaDone) Add(SettingsSchema.SeaLevel, sea.ToString("R", CultureInfo.InvariantCulture), "The sea level the heights are encoded for.");
      if (!amountDone)
      {
        Add(SettingsSchema.Rivers, "false", "The heightmap holds the rivers.");
        Add(SettingsSchema.MountainsAllowedAtCenter, "true", "The centre is in the heightmap.");
        Add(SettingsSchema.HeightmapAmount, amount.ToString("R", CultureInfo.InvariantCulture), "metres = (pixel / 65535 * amount - 0.15 + Lerp(1, -1, sea level)) * 200.");
        Add(SettingsSchema.HeightmapBlend, "1", "The heightmap is the height.");
        Add(SettingsSchema.HeightmapAdd, "0", "Nothing added.");
        Add(SettingsSchema.HeightmapMask, "0", "No masking.");
        Add(SettingsSchema.HeightmapOverrideAll, "true", "Every biome takes its height from the heightmap.");
        Add(SettingsSchema.HeightmapAlpha, "false", "heightmap.png is plain 16-bit grey.");
      }
    }
    return lines;
  }

  private static string ImportArg(string dir)
  {
    var parent = Path.GetDirectoryName(dir) ?? "";
    return string.Equals(Path.GetFileName(Path.GetDirectoryName(parent) ?? ""), "BetterContinents", StringComparison.OrdinalIgnoreCase)
      ? $"{Path.GetFileName(parent)}/{Path.GetFileName(dir)}"
      : dir.Replace('\\', '/');
  }

  private static List<string> Readme(string exportDir, IngestResult r, List<string> copied)
  {
    return
    [
      $"EDITED EXPORT - made by bc_import edited from {Path.GetFileName(exportDir)}",
      "",
      $"  heightmap.png (and heightmap-fine.png): the ground your editor returned ({Path.GetFileName(r.ReturnFile)}, {r.Size} px), encoded for",
      $"  Heightmap Amount {Inv(r.Amount)} and Sea Level {Inv(r.SeaLevel)} (export.cfg sets both).",
      "  Every other map is a byte for byte copy from the export, the location map included.",
      "  The editor pack (editor/) and sources/ stay with the export.",
      "",
      "  To use it: bc_import <this folder> makes the New World preset, as for any export.",
      "  The biomes, locations and the rest do not move with the ground you changed.",
    ];
  }

  private static string ManifestFor(MiniJson.Obj source, MiniJson.Obj? editor, string exportDir, string outDir, IngestResult r, EditorOrient orient, IngestOptions o, List<string> written)
  {
    var m = source;
    float floor = WorldExportMath.ValueToMetres(0f, r.Amount, r.SeaLevel), ceil = WorldExportMath.ValueToMetres(1f, r.Amount, r.SeaLevel);
    m.Set("modVersion", ModInfo.Version);
    m.Set("exportedAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    m.Set("heightmapAmount", (double)r.Amount);
    m.Set("seaLevel", (double)r.SeaLevel);
    m.Set("seaLevelAdjustment", (double)WorldExportMath.SeaLevelAdjustment(r.SeaLevel));
    m.Set("heightEncoding", "metres = (pixel / 65535 * heightmapAmount - 0.15 + seaLevelAdjustment) * 200");
    m.Set("waterlineValue", Math.Round(WorldExportMath.WaterlineValue(r.Amount, r.SeaLevel), 6));
    m.Set("floorMetres", Math.Round(floor, 3));
    m.Set("ceilingMetres", Math.Round(ceil, 3));
    m.Set("clippedLow", (double)r.ClippedLow);
    m.Set("clippedHigh", (double)r.ClippedHigh);
    m.Remove("edgeRingClipped");
    m.Set("heightRangeMetres", double.IsNaN(r.MinMetres) ? null : new List<object?> { r.MinMetres, r.MaxMetres });
    if (r.Size != r.SourceSize)
      m.SetAfter("size", "heightmapSize", (double)r.Size);
    m.Remove("fineBits");
    m.Remove("fineEncoding");
    if (r.FineBitsWritten > 0)
    {
      m.SetAfter("heightEncoding", "fineBits", (double)r.FineBitsWritten);
      m.SetAfter("fineBits", "fineEncoding", "signed offset, 1/256 step, format 1");
    }
    m.Set("preset", null);
    m.Set("import", $"bc_import {ImportArg(outDir)}");
    m.Remove("editorPack");
    m.Remove("seconds");
    var from = new MiniJson.Obj();
    from.Add(new("export", Path.GetFileName(exportDir)));
    from.Add(new("returnFile", Path.GetFileName(r.ReturnFile)));
    from.Add(new("returnSize", (double)r.Size));
    from.Add(new("orientation", EditorOrientation.Name(orient)));
    from.Add(new("orientationFound", r.OrientationFound));
    from.Add(new("unit", o.Unit == EditorUnit.V ? "v" : o.Unit == EditorUnit.Metres ? "metres" : "range"));
    from.Add(new("range", o.Unit == EditorUnit.Range ? new List<object?> { o.RangeLo, o.RangeHi } : null));
    from.Add(new("edgeRing", o.RingKeep ? "keep" : "gaea"));
    from.Add(new("editorJson", editor != null ? "editor/editor.json" : null));
    from.Add(new("clippedLow", (double)r.ClippedLow));
    from.Add(new("clippedHigh", (double)r.ClippedHigh));
    from.Add(new("restoredPixels", (double)r.Restored));
    from.Add(new("heightRangeMetres", double.IsNaN(r.MinMetres) ? null : new List<object?> { r.MinMetres, r.MaxMetres }));
    m.Set("editedFrom", from);
    m.Set("files", written.Cast<object?>().ToList());
    var notes = m["notes"] as List<object?> ?? [];
    notes.Add($"Heights are from an outside editor (bc_import edited): {Path.GetFileName(r.ReturnFile)}, turned by '{EditorOrientation.Name(orient)}'. The other maps are the export's, as they were.");
    m.Set("notes", notes);
    return WorldExportJson.Write(MiniJson.ToWritable(m));
  }
}

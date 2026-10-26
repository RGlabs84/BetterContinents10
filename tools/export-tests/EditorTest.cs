// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The editor pack (editor/ in an export): the TIFF and raw float writers against an independent reader (tools/editor_ref.py, the
// standard library only), the options, and whole exports of the made-up world: every height pixel against HeightmapBuilder's blend and
// the value rule, every mask against its definition (the slope against the reference), the same bytes whatever size the bands are
// cut in, a default export that is what it was, a cancel, and the manifest.
internal static class EditorTest
{
  static void C(bool ok, string what) => Program.C(ok, what);
  static void Section(string s) => System.Console.WriteLine("== " + s);

  const float T = 21000f;

  // ---- the reference ---------------------------------------------------------------------------------------------------

  static string Script()
  {
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
    {
      var p = Path.Combine(dir, "editor_ref.py");
      if (File.Exists(p))
        return p;
      p = Path.Combine(dir, "tools", "editor_ref.py");
      if (File.Exists(p))
        return p;
    }
    throw new FileNotFoundException("tools/editor_ref.py");
  }

  internal static string Py(params string[] args)
  {
    var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add("-I");
    psi.ArgumentList.Add(Script());
    foreach (var a in args)
      psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
      throw new InvalidOperationException("editor_ref.py " + string.Join(" ", args) + ": " + err.Result);
    return output;
  }

  static float[] FloatsFromHex(string hex)
  {
    var bytes = Convert.FromHexString(hex);
    var f = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
    return f;
  }

  static byte[] Bytes(float[] f)
  {
    var b = new byte[f.Length * 4];
    Buffer.BlockCopy(f, 0, b, 0, b.Length);
    return b;
  }

  public static void Run(string work)
  {
    Writers(work);
    Parsing();
    Pack(work);
    Heights(work);
    BandsAndOthers(work);
    Cancel(work);
  }

  // ---- the writers ------------------------------------------------------------------------------------------------------

  static void Writers(string work)
  {
    Section("editor pack: the TIFF and raw float writers against the reference reader");
    var dir = Path.Combine(work, "editor-writers");
    Directory.CreateDirectory(dir);
    var rng = new System.Random(11);
    foreach (var (w, h) in new[] { (7, 5), (1, 1), (70, 3), (3, 130), (16384, 2), (64, 64) })
    {
      var values = new float[w * h];
      for (int i = 0; i < values.Length; i++)
        values[i] = i % 17 == 0 ? (float)(rng.NextDouble() * 4 - 2) : (float)rng.NextDouble();
      var path = Path.Combine(dir, $"t{w}x{h}.tif");
      var desc = "BetterContinents: Height Encoding = bc-v; Heightmap Amount = 2; Sea Level Adjustment = 0.5; Orientation = north-up";
      using (var tif = new TiffRowWriter(path, w, h, 32, desc, "Better Continents 0.10.7"))
      {
        var bytes = Bytes(values);
        int rowBytes = w * 4;
        for (int r = 0, step = 1; r < h; step = step % 3 + 1)
        {
          int rows = Math.Min(step, h - r);
          tif.WriteRows(bytes, r * rowBytes, rows);
          r += rows;
        }
        C(File.Exists(path + ".tmp") && !File.Exists(path), $"{w} x {h}: the file is <name>.tmp until it is complete");
        tif.Complete();
      }
      C(File.Exists(path) && !File.Exists(path + ".tmp"), $"{w} x {h}: complete, it has its name and no .tmp is left");
      using var doc = JsonDocument.Parse(Py("tiff", path));
      var root = doc.RootElement;
      var got = FloatsFromHex(root.GetProperty("pixels").GetString()!);
      bool same = got.Length == values.Length && got.Zip(values, (a, b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)).All(x => x);
      int header = TiffRowWriter.HeaderLength(h, desc, "Better Continents 0.10.7");
      int strips = (h + 63) / 64;
      C(same, $"{w} x {h}: the reference reads back the same {values.Length} floats, bit for bit");
      C(root.GetProperty("width").GetInt32() == w && root.GetProperty("height").GetInt32() == h && root.GetProperty("bits").GetInt32() == 32
        && root.GetProperty("sampleformat").GetInt32() == 3 && root.GetProperty("compression").GetInt32() == 1 && root.GetProperty("photometric").GetInt32() == 1
        && root.GetProperty("samples").GetInt32() == 1 && root.GetProperty("planar").GetInt32() == 1 && root.GetProperty("orientation").GetInt32() == 1
        && root.GetProperty("order").GetString() == "<" && !root.GetProperty("big").GetBoolean() && root.GetProperty("ifd").GetInt32() == 8,
        $"{w} x {h}: classic little-endian TIFF, 32-bit float, one sample, uncompressed, row 0 on top");
      C(root.GetProperty("entries").GetInt32() == 14 && root.GetProperty("tagsAscending").GetBoolean()
        && string.Join(",", root.GetProperty("tags").EnumerateArray().Select(e => e.GetInt32())) == "256,257,258,259,262,270,273,274,277,278,279,284,305,339",
        $"{w} x {h}: 14 entries in ascending tag order");
      C(root.GetProperty("description").GetString() == desc && root.GetProperty("software").GetString() == "Better Continents 0.10.7", $"{w} x {h}: the description and the software text");
      C(root.GetProperty("rowsPerStrip").GetInt32() == Math.Min(h, 64) && root.GetProperty("stripOffsets").GetArrayLength() == strips && root.GetProperty("stripOffsets")[0].GetInt64() == header
        && root.GetProperty("stripsContiguous").GetBoolean() && root.GetProperty("stripsEnd").GetInt64() == header + (long)w * h * 4 && root.GetProperty("fileLength").GetInt64() == header + (long)w * h * 4
        && header % 4 == 0 && new FileInfo(path).Length == TiffRowWriter.FileLength(w, h, 32, desc, "Better Continents 0.10.7"),
        $"{w} x {h}: {strips} strip(s) of {Math.Min(h, 64)} rows from offset {header}, the file {header + (long)w * h * 4} bytes");
    }
    // A size under 4 GiB is worked out without writing it; one over is refused.
    C(TiffRowWriter.FileLength(32768, 32768, 32, "x", "y") > uint.MaxValue && TiffRowWriter.FileLength(16384, 16384, 32, "x", "y") < uint.MaxValue, "16384 x 16384 floats fit a classic TIFF (4 GiB offsets), 32768 x 32768 would not");
    bool refused = false;
    try { new TiffRowWriter(Path.Combine(dir, "huge.tif"), 32768, 32768, 32, "x", "y").Dispose(); } catch (ArgumentException) { refused = true; }
    C(refused && !File.Exists(Path.Combine(dir, "huge.tif.tmp")), "a picture past 4 GiB is refused before any file is made");
    // 16-bit unsigned, for the day masks are TIFFs too.
    var tifPath = Path.Combine(dir, "u16.tif");
    using (var tif16 = new TiffRowWriter(tifPath, 4, 2, 16, "x", "y"))
    {
      tif16.WriteRows([1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8, 0], 0, 2);
      tif16.Complete();
    }
    using (var d16 = JsonDocument.Parse(Py("tiff", tifPath)))
      C(d16.RootElement.GetProperty("bits").GetInt32() == 16 && d16.RootElement.GetProperty("sampleformat").GetInt32() == 1 && d16.RootElement.GetProperty("pixels").GetString() == "0100020003000400050006000700" + "0800",
        "16-bit samples are unsigned (SampleFormat 1)");
    // Dispose before Complete deletes the .tmp.
    var abandoned = Path.Combine(dir, "abandoned.tif");
    var sink = new TiffRowWriter(abandoned, 8, 8, 32, "x", "y");
    sink.WriteRows(new byte[32], 0, 1);
    sink.Dispose();
    C(!File.Exists(abandoned) && !File.Exists(abandoned + ".tmp"), "a writer that is disposed before it is complete leaves nothing");
    bool early = false;
    using (var half = new TiffRowWriter(Path.Combine(dir, "half.tif"), 8, 8, 32, "x", "y"))
    {
      half.WriteRows(new byte[32], 0, 1);
      try { half.Complete(); } catch (InvalidOperationException) { early = true; }
    }
    C(early, "Complete before the last row is an error");

    // R32: bytes are the little-endian floats, the side is the square root of the length.
    foreach (var side in new[] { 9, 1, 128 })
    {
      var path = Path.Combine(dir, $"r{side}.r32");
      var values = Enumerable.Range(0, side * side).Select(i => (float)(i % 101) / 100f).ToArray();
      using (var r32 = new RawFloatRowWriter(path, side, side))
      {
        var bytes = Bytes(values);
        for (int r = 0; r < side; r++)
          r32.WriteRows(bytes, r * side * 4, 1);
        r32.Complete();
      }
      using var doc = JsonDocument.Parse(Py("r32", path));
      C(doc.RootElement.GetProperty("side").GetInt32() == side && doc.RootElement.GetProperty("square").GetBoolean()
        && FloatsFromHex(doc.RootElement.GetProperty("pixels").GetString()!).SequenceEqual(values), $"R32 {side} x {side}: {side * side * 4} bytes, headerless, the floats in order");
    }
  }

  // ---- the options -----------------------------------------------------------------------------------------------------------

  static void Parsing()
  {
    Section("editor pack: the options");
    C(WorldExportCommands.TryParse("4096 editor", out var o, out var e) && o.Editor && o.EditorFormats == WorldExport.EditorFormat.Tiff && !o.EditorMetres && o.EditorAmount == 0f
      && o.EditorSize == 0 && o.EditorMasks == WorldExport.EditorMask.Default && o.Size == 4096 && o.Validate() == null, $"'editor' alone: tif, automatic amount, the default masks, the export's size ({e})");
    C(WorldExportCommands.TryParse("4096", out var plain, out _) && !plain.Editor && !plain.ToString().Contains("editor") && plain.ToString() == WorldExport.Options.Default().ToString().Replace(WorldExport.Options.Default().Size + " px", "4096 px"),
      "without 'editor' the options line is the one it was");
    C(WorldExportCommands.TryParse("2048 editor eformat=tif,r32 eunit=metres eamount=3 esize=1024 emasks=water,slope eprotectpad=5 eprotectfeather=0", out o, out e)
      && o.EditorFormats == (WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32) && o.EditorMetres && o.EditorAmount == 3f && o.EditorSize == 1024
      && o.EditorMasks == (WorldExport.EditorMask.Water | WorldExport.EditorMask.Slope) && o.EditorProtectPad == 5f && o.EditorProtectFeather == 0f && o.Validate() == null && o.EffectiveEditorSize == 1024,
      $"every option: {o}");
    C(o.ToString().Contains("editor pack 1024 px tif+r32, amount 3, metres, masks water,slope"), "the options line names the pack (" + o + ")");
    C(WorldExportCommands.TryParse("editor emasks=all", out o, out _) && o.EditorMasks == WorldExport.EditorMask.All && WorldExportCommands.TryParse("editor emasks=none", out o, out _) && o.EditorMasks == WorldExport.EditorMask.None
      && WorldExportCommands.TryParse("editor emasks=default,soft", out o, out _) && o.EditorMasks == (WorldExport.EditorMask.Default | WorldExport.EditorMask.Soft), "emasks=all, none, default,soft");
    C(WorldExportCommands.TryParse("editor eamount=auto", out o, out _) && o.EditorAmount == 0f, "eamount=auto is 0");
    C(WorldExportCommands.TryParse("editoronly", out o, out _) && o.Editor && o.Heightmap && !o.Biomes && !o.Forest && !o.Heat && !o.AltBiomes && !o.Lava && !o.Moss && !o.Paint && !o.Sources && !o.Preset && !o.Locations && o.Validate() == null,
      "editoronly: the pack beside heightmap.png and no other map, no preset");
    C(WorldExportCommands.TryParse("editor noeditor", out o, out _) && !o.Editor, "noeditor turns it off");
    foreach (var (text, part) in new[]
    {
      ("editor eformat=exr", "EXR is not written yet"), ("editor eformat=png", "cannot read 'eformat=png'"), ("editor emasks=rivers", "river masks are not written yet"),
      ("editor emasks=lakes", "cannot read 'emasks=lakes'"), ("editor eunit=feet", "cannot read 'eunit=feet'"), ("editor eamount=x", "cannot read 'eamount=x'"),
      ("editor etile=8192", "later version"), ("editor eregion=1,2,3,4", "later version"), ("editor wibble=1", "unknown option 'wibble=1'"), ("editor wibble", "unknown option 'wibble'"),
    })
    {
      bool ok = WorldExportCommands.TryParse(text, out _, out e);
      C(!ok && e.Contains(part), $"'{text}' is refused: {e}");
    }
    foreach (var (text, part) in new[]
    {
      ("editor esize=64", "editor size must be 128 to"), ("editor esize=20000", "editor size must be 128 to"), ("editor eamount=500", "editor amount must be 0 (automatic) or 0.01 to"),
      ("editor eamount=-1", "editor amount must be"), ("editor eformat=r32 eprotectpad=-1", "protect pad"),
    })
    {
      bool parsed = WorldExportCommands.TryParse(text, out o, out e);
      var problem = o.Validate();
      C(parsed && problem != null && problem.Contains(part), $"'{text}' parses but does not validate: {problem}");
    }
    C(WorldExportCommands.TryParse("editor noheight nobiomes nolocations noforest noheat nocold noaltbiomes nolava nomoss nopaint nosources", out o, out _) && o.Validate() == null,
      "the pack counts as a map: with every other map off an editor export is still valid");
    C(WorldExportCommands.TryParse("noheight nobiomes nolocations noforest noheat nocold noaltbiomes nolava nomoss nopaint nosources", out o, out _) && o.Validate() == "every map is switched off", "and without the pack nothing is still 'every map is switched off'");
  }

  // ---- whole exports ---------------------------------------------------------------------------------------------------------

  sealed class Run_
  {
    public string Dir = "", Phase = "", Error = "";
    public IReadOnlyList<string> Summary = [];
    public WorldExport.Options Options = null!;
  }

  static WorldExport.Options Options(int n, Action<WorldExport.Options> tweak = null)
  {
    var o = WorldExport.Options.Default();
    o.Size = n;
    o.Biomes = o.Locations = o.Forest = o.Heat = o.AltBiomes = o.Lava = o.Moss = o.Paint = o.Sources = o.Preset = false;
    o.Editor = true;
    o.HeightmapAmount = 2f;
    tweak?.Invoke(o);
    return o;
  }

  static Run_ Export(string work, string name, WorldExport.Options o, Func<float, float, float> height = null, Func<string, bool> cancelAt = null, int bands = 0, Func<List<(float, float, float)>> discs = null)
  {
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Editor World", m_seedName = "EditorSeed", m_seed = 9 };
    var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    BC.Settings = new BC.BetterContinentsSettings();
    var dir = Path.Combine(work, name);
    TerrainTest.HeightOverride = height;
    WorldExport.EditorBandRowsOverride = bands;
    WorldExport.EditorDiscSource = discs == null ? null : () => discs();
    try
    {
      var job = EndToEnd.MakeJob(o, dir, wg);
      EndToEnd.SetRunning(true);
      var drive = EndToEnd.Drive(job);
      while (drive.MoveNext())
      {
        if (cancelAt != null && cancelAt(WorldExport.Phase))
          WorldExport.Cancel();
        Thread.Sleep(1);
      }
    }
    finally
    {
      TerrainTest.HeightOverride = null;
      WorldExport.EditorBandRowsOverride = 0;
      WorldExport.EditorDiscSource = null;
    }
    return new Run_ { Dir = dir, Phase = WorldExport.Phase, Error = WorldExport.LastError ?? "", Summary = WorldExport.LastSummary, Options = o };
  }

  // HeightmapBuilder's blend of the zone's four corner biomes at a point (the same expression the export's sampler is checked against).
  static float Blend(float wx, float wz)
  {
    int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
    float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
    var b = new[] { TerrainTest.Biome(x0, z0), TerrainTest.Biome(x0 + 64f, z0), TerrainTest.Biome(x0, z0 + 64f), TerrainTest.Biome(x0 + 64f, z0 + 64f) };
    float tx = DUtils.SmoothStep(0f, 1f, (float)((wx - (double)x0) / 64.0)), tz = DUtils.SmoothStep(0f, 1f, (float)((wz - (double)z0) / 64.0));
    return b.All(x => x == b[0]) ? TerrainTest.Height(b[0], wx, wz, out _)
      : DUtils.Lerp(DUtils.Lerp(TerrainTest.Height(b[0], wx, wz, out _), TerrainTest.Height(b[1], wx, wz, out _), tx),
                    DUtils.Lerp(TerrainTest.Height(b[2], wx, wz, out _), TerrainTest.Height(b[3], wx, wz, out _), tx), tz);
  }

  static float[] ReadTiff(string path, out JsonElement info, out JsonDocument keep)
  {
    keep = JsonDocument.Parse(Py("tiff", path));
    info = keep.RootElement;
    return FloatsFromHex(info.GetProperty("pixels").GetString()!);
  }

  static byte[] ReadMask8(string path, int n)
  {
    using var image = Image.Load<L8>(path);
    if (image.Width != n || image.Height != n) throw new InvalidOperationException(path + " has the wrong size");
    var pixels = new L8[n * n];
    image.CopyPixelDataTo(pixels);
    return pixels.Select(p => p.PackedValue).ToArray();
  }

  static ushort[] ReadMask16(string path, int n)
  {
    using var image = Image.Load<L16>(path);
    if (image.Width != n || image.Height != n) throw new InvalidOperationException(path + " has the wrong size");
    var pixels = new L16[n * n];
    image.CopyPixelDataTo(pixels);
    return pixels.Select(p => p.PackedValue).ToArray();
  }

  static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

  static void Pack(string work)
  {
    Section("editor pack: a whole export of the made-up world, pixel by pixel");
    const int n = 256;
    var discs = new List<(float, float, float)> { (0f, 0f, 60f), (-9000f, 4000f, 120f), (3000f, -7000f, 30f), (10400f, 10400f, 400f), (-3000f, 3000f, 0f) };
    var o = Options(n, x =>
    {
      x.EditorMasks = WorldExport.EditorMask.All;
      x.EditorFormats = WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32;
      x.EditorMetres = true;
    });
    var r = Export(work, "editor-pack", o, discs: () => discs);
    C(r.Phase == "Done" && r.Error == "", $"the export with the editor pack finishes ({r.Phase} {r.Error})");
    var ed = Path.Combine(r.Dir, "editor");
    C(Directory.Exists(Path.Combine(ed, "return")) && !Directory.EnumerateFileSystemEntries(Path.Combine(ed, "return")).Any(), "editor/return/ exists and is empty");
    var all = Directory.GetFiles(r.Dir, "*", SearchOption.AllDirectories);
    C(!all.Any(f => f.EndsWith(".tmp")), "no .tmp file is left");
    var expectedFiles = new[] { "height.tif", "height.r32", "height-metres.tif", "editor.json", "README-editor.txt", "probe.tif", "masks/water.png", "masks/water-inland.png", "masks/slope.png", "masks/forest-density.png",
      "masks/biome-index.png", "masks/biome-index.txt", "masks/protect-locations.png" };
    var missing = expectedFiles.Where(f => !File.Exists(Path.Combine(ed, f))).ToList();
    C(missing.Count == 0, "the pack's files are all there" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));

    // Heights: every pixel is the blend's height as a value, floored at 0, bit for bit.
    var tif = ReadTiff(Path.Combine(ed, "height.tif"), out var info, out var keep);
    float sla = M.SeaLevelAdjustment(0.5f);
    float amount = 2f;
    var wantV = new float[n * n];
    var wantH = new float[n * n];
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        float wx = M.PixelToWorld(col, n, T), wz = M.FileRowToWorldZ(row, n, T);
        float h = Blend(wx, wz);
        wantH[row * n + col] = h;
        float v = (h / 200f + 0.15f - sla) / amount;
        wantV[row * n + col] = !(v >= 0f) ? 0f : v;
      }
    int differ = 0;
    for (int i = 0; i < tif.Length; i++)
      if (BitConverter.SingleToInt32Bits(tif[i]) != BitConverter.SingleToInt32Bits(wantV[i])) differ++;
    C(info.GetProperty("width").GetInt32() == n && tif.Length == n * n && differ == 0, $"height.tif holds HeightmapBuilder's blend as the value rule says, every pixel ({differ} differ)");
    var description = info.GetProperty("description").GetString()!;
    C(description == "BetterContinents: Height Encoding = bc-v; Heightmap Amount = 2; Sea Level Adjustment = 0.5; Orientation = north-up", "the TIFF's description records the encoding: " + description);
    double worstMetres = 0;
    for (int i = 0; i < tif.Length; i++)
      if (wantH[i] >= M.ValueToMetres(0f, amount, 0.5f))
        worstMetres = Math.Max(worstMetres, Math.Abs(M.ValueToMetres(tif[i], amount, 0.5f) - wantH[i]));
    C(worstMetres < 1e-3, $"ValueToMetres of the file gives the world's height back (worst {worstMetres * 1000:0.####} mm)");
    keep.Dispose();
    var r32 = FloatsFromHex(JsonDocument.Parse(Py("r32", Path.Combine(ed, "height.r32"))).RootElement.GetProperty("pixels").GetString()!);
    C(r32.Length == tif.Length && r32.Zip(tif, (a, b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(Math.Min(b, 1f))).All(x => x), "height.r32 is the same floats clamped to 1");
    var metresFile = ReadTiff(Path.Combine(ed, "height-metres.tif"), out var minfo, out var mkeep);
    C(metresFile.Zip(wantH, (a, b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)).All(x => x), "height-metres.tif is the world's height in metres, every pixel");
    mkeep.Dispose();

    // Masks.
    int Idx(int row, int col) => row * n + col;
    var water = ReadMask8(Path.Combine(ed, "masks/water.png"), n);
    int waterBad = 0, wet = 0;
    for (int i = 0; i < water.Length; i++)
    {
      if ((water[i] == 255) != (wantH[i] < 30f) || (water[i] != 0 && water[i] != 255)) waterBad++;
      if (water[i] == 255) wet++;
    }
    C(waterBad == 0 && wet > 1000 && wet < n * n - 1000, $"water.png is 255 exactly where the height is under 30 m ({wet} of {n * n} pixels, {waterBad} wrong)");
    var inland = ReadMask8(Path.Combine(ed, "masks/water-inland.png"), n);
    C(inland.SequenceEqual(water), "water-inland.png is the water that is not Ocean (the made-up world has no ocean, so all of it)");

    var slopeOut = Py("slope", Path.Combine(ed, "height.tif"), Path.Combine(ed, "masks/slope.png"), "--amount", "2", "--sla", "0", "--pitch", (T / (n - 1)).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    using (var sd = JsonDocument.Parse(slopeOut))
      C(sd.RootElement.GetProperty("bad").GetInt32() == 0 && sd.RootElement.GetProperty("max").GetInt32() > 100, $"slope.png against the reference: no pixel is more than 1 step off (worst {sd.RootElement.GetProperty("worst").GetInt32()}, steepest {sd.RootElement.GetProperty("max").GetInt32()})");

    var forest = ReadMask16(Path.Combine(ed, "masks/forest-density.png"), n);
    int forestBad = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        var pos = new Vector3(M.PixelToWorld(col, n, T), 0f, M.FileRowToWorldZ(row, n, T));
        ushort want = (ushort)(Mathf.Clamp01(M.ForestNormalized(EndToEnd.Target(pos))) * 65535f + 0.5f);
        if (forest[Idx(row, col)] != want) forestBad++;
      }
    C(forestBad == 0 && forest.Distinct().Count() > 100, $"forest-density.png is the normalised forest factor, 16-bit ({forestBad} differ)");

    // Biomes: the files, a partition, the index, the soft weights.
    var biomeFiles = Directory.GetFiles(Path.Combine(ed, "masks"), "biome-*.png").Select(Path.GetFileName).Where(f => f != "biome-index.png" && !f.StartsWith("biome-soft-")).OrderBy(f => f).ToList();
    var softFiles = Directory.GetFiles(Path.Combine(ed, "masks"), "biome-soft-*.png").Select(Path.GetFileName).OrderBy(f => f).ToList();
    var names = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.AshLands, Heightmap.Biome.Mistlands }.Select(b => BiomeRegistry.Name(b)).OrderBy(x => x).ToList();
    C(biomeFiles.SequenceEqual(names.Select(x => $"biome-{x}.png")) && softFiles.SequenceEqual(names.Select(x => $"biome-soft-{x}.png")),
      $"one hard and one soft mask for each biome that has pixels, and none for the others ({string.Join(", ", biomeFiles)})");
    var hard = biomeFiles.ToDictionary(f => f, f => ReadMask8(Path.Combine(ed, "masks", f), n));
    var index = ReadMask8(Path.Combine(ed, "masks/biome-index.png"), n);
    int partBad = 0, indexBad = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        var biome = TerrainTest.Biome(M.PixelToWorld(col, n, T), M.FileRowToWorldZ(row, n, T));
        int k = BiomeRegistry.ToSafeIndex(biome);
        int sum = 0;
        foreach (var kv in hard)
          sum += kv.Value[Idx(row, col)];
        if (sum != 255 || hard[$"biome-{BiomeRegistry.Name(biome)}.png"][Idx(row, col)] != 255) partBad++;
        if (index[Idx(row, col)] != k) indexBad++;
      }
    C(partBad == 0, $"the hard biome masks partition the map: every pixel is 255 in exactly its own biome's file ({partBad} wrong)");
    C(indexBad == 0, $"biome-index.png is BiomeRegistry.ToSafeIndex of the point biome ({indexBad} wrong)");
    var legend = File.ReadAllLines(Path.Combine(ed, "masks/biome-index.txt"));
    C(legend.Any(l => l.StartsWith($"{BiomeRegistry.ToSafeIndex(Heightmap.Biome.Meadows)}: {BiomeRegistry.Name(Heightmap.Biome.Meadows)}: ")), "biome-index.txt lists 'index: name: R,G,B'");

    var soft = softFiles.ToDictionary(f => f, f => ReadMask16(Path.Combine(ed, "masks", f), n));
    int softSumBad = 0, softBad = 0, mixed = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        float wx = M.PixelToWorld(col, n, T), wz = M.FileRowToWorldZ(row, n, T);
        int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
        float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
        var cb = new[] { TerrainTest.Biome(x0, z0), TerrainTest.Biome(x0 + 64f, z0), TerrainTest.Biome(x0, z0 + 64f), TerrainTest.Biome(x0 + 64f, z0 + 64f) };
        double tx = DUtils.SmoothStep(0f, 1f, (float)((wx - (double)x0) / 64.0)), tz = DUtils.SmoothStep(0f, 1f, (float)((wz - (double)z0) / 64.0));
        var weights = new[] { (1 - tx) * (1 - tz), tx * (1 - tz), (1 - tx) * tz, tx * tz };
        int sum = 0;
        bool anyMixed = !cb.All(b => b == cb[0]);
        if (anyMixed) mixed++;
        foreach (var biome in new[] { Heightmap.Biome.Meadows, Heightmap.Biome.AshLands, Heightmap.Biome.Mistlands })
        {
          double want = 0;
          for (int c = 0; c < 4; c++)
            if (cb[c] == biome) want += weights[c];
          int got = soft[$"biome-soft-{BiomeRegistry.Name(biome)}.png"][Idx(row, col)];
          sum += got;
          if (Math.Abs(got - want * 65535.0) > 2.0) softBad++;
        }
        if (Math.Abs(sum - 65535) > 3) softSumBad++;
      }
    C(softSumBad == 0 && softBad == 0 && mixed > 100, $"the soft masks are the corners' smoothstep weights (sum 1 per pixel) in 16 bits ({softBad} off, {softSumBad} sums off, {mixed} pixels in mixed zones)");

    // Protect: discs with the pad and the feather, against a brute force written separately.
    var protect = ReadMask8(Path.Combine(ed, "masks/protect-locations.png"), n);
    int protBad = 0, protOn = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        double wx = M.PixelToWorld(col, n, T), wz = M.FileRowToWorldZ(row, n, T);
        double best = 0;
        foreach (var (dx, dz, rad) in discs)
        {
          double dist = Math.Sqrt((wx - dx) * (wx - dx) + (wz - dz) * (wz - dz));
          double inner = rad + 8.0;
          double a = dist <= inner ? 1.0 : dist >= inner + 32.0 ? 0.0 : 1.0 - (dist - inner) / 32.0;
          best = Math.Max(best, a);
        }
        if (Math.Abs(protect[Idx(row, col)] - best * 255.0) > 1.01) protBad++;
        if (protect[Idx(row, col)] > 0) protOn++;
      }
    C(protBad == 0 && protOn > 0, $"protect-locations.png is white inside each location's radius plus 8 m and fades over 32 m ({protBad} off, {protOn} pixels lit)");

    // editor.json, the manifest, the README.
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(ed, "editor.json")));
    var jr = json.RootElement;
    var grid = jr.GetProperty("grid");
    var hjson = jr.GetProperty("height");
    C(jr.GetProperty("format").GetString() == "bc-editor/1" && jr.GetProperty("exportFormat").GetString() == "bc-export/1" && grid.GetProperty("size").GetInt32() == n && grid.GetProperty("totalSize").GetDouble() == T
      && Math.Abs(grid.GetProperty("metresPerPixel").GetDouble() - T / (n - 1)) < 1e-4 && grid.GetProperty("axes").GetString()!.Contains("row 0 is north") && jr.GetProperty("worldName").GetString() == "Editor World",
      "editor.json: format, grid and georeference");
    C(hjson.GetProperty("amount").GetDouble() == 2.0 && hjson.GetProperty("seaLevelAdjustment").GetDouble() == 0.0 && Math.Abs(hjson.GetProperty("waterlineValue").GetDouble() - 0.15) < 1e-9
      && hjson.GetProperty("floorMetres").GetDouble() == -30.0 && hjson.GetProperty("ceilingMetres").GetDouble() == 370.0 && hjson.GetProperty("dtype").GetString() == "float32"
      && hjson.GetProperty("clippedLow").GetInt64() == 0 && hjson.GetProperty("clippedHigh").GetInt64() == 0
      && Math.Abs(hjson.GetProperty("valueRange")[0].GetDouble() - wantV.Min()) < 1e-6 && Math.Abs(hjson.GetProperty("valueRange")[1].GetDouble() - wantV.Max()) < 1e-6,
      $"editor.json: amount 2 spans -30 to 370 m, the waterline is 0.15, the value range is the file's ({hjson.GetProperty("valueRange")})");
    C(hjson.GetProperty("crc32").GetString() == PngRowWriter.FileCrc32(Path.Combine(ed, "height.tif")).ToString("X8") && jr.GetProperty("gaea").GetProperty("terrainHeightMetres").GetDouble() == 400.0
      && Math.Abs(jr.GetProperty("gaea").GetProperty("scaleMetresKeepPixelPitch").GetDouble() - T / (n - 1) * n) < 1e-3 && jr.GetProperty("masks").GetArrayLength() == 4 + hard.Count + soft.Count + 2,
      "editor.json: the file's CRC-32, the Gaea numbers, one entry per mask");
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir, "manifest.json")));
    var mroot = manifest.RootElement;
    var listed = mroot.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToList();
    C(mroot.GetProperty("format").GetString() == "bc-export/1" && mroot.GetProperty("editorPack").GetProperty("dir").GetString() == "editor" && mroot.GetProperty("editorPack").GetProperty("size").GetInt32() == n
      && expectedFiles.All(f => listed.Contains("editor/" + f)) && listed.Contains("manifest.json"), "manifest.json stays bc-export/1, has editorPack, and lists every editor file");
    C(File.ReadAllText(Path.Combine(r.Dir, "README.txt")).Contains("editor/") && File.ReadAllText(Path.Combine(ed, "README-editor.txt")).Contains("bc_import edited " + Path.GetFileName(r.Dir)), "README.txt has an entry for editor/, and README-editor.txt names the way back");
    C(r.Summary.Any(l => l.StartsWith("editor pack:")), "the summary has a line for the pack: " + r.Summary.FirstOrDefault(l => l.StartsWith("editor pack:")));

    // The orientation probe.
    var probe = ReadTiff(Path.Combine(ed, "probe.tif"), out var pinfo, out var pkeep);
    C(pinfo.GetProperty("width").GetInt32() == 64 && pinfo.GetProperty("height").GetInt32() == 64 && probe[0] > probe[63 * 64] && probe[16 * 64 + 16] == 1f && probe[16 * 64 + 48] < 1f && probe[48 * 64 + 16] < 1f,
      "probe.tif: 64 x 64, high at the top (north), the bright patch in the north-west quarter");
    pkeep.Dispose();

    // The same export without the pack: byte for byte the same maps, a manifest without the key.
    var plainOptions = Options(n, x => x.Editor = false);
    var plain = Export(work, "editor-plain", plainOptions);
    C(plain.Phase == "Done" && !Directory.Exists(Path.Combine(plain.Dir, "editor")), "an export without 'editor' has no editor folder");
    using var pm = JsonDocument.Parse(File.ReadAllText(Path.Combine(plain.Dir, "manifest.json")));
    C(!pm.RootElement.TryGetProperty("editorPack", out _) && pm.RootElement.GetProperty("format").GetString() == "bc-export/1" && !File.ReadAllText(Path.Combine(plain.Dir, "README.txt")).Contains("editor/"), "and its manifest and README say nothing about one");
    C(Sha(Path.Combine(plain.Dir, "heightmap.png")) == Sha(Path.Combine(r.Dir, "heightmap.png")), "heightmap.png is byte for byte the same with and without the pack");
    var withKeys = mroot.EnumerateObject().Select(p => p.Name).Where(k => k != "editorPack" && k != "seconds").ToList();
    var plainKeys = pm.RootElement.EnumerateObject().Select(p => p.Name).Where(k => k != "seconds").ToList();
    C(withKeys.SequenceEqual(plainKeys), "the manifest has the same keys in the same order, plus editorPack");
  }

  // ---- heights: floor, clip, edge ------------------------------------------------------------------------------------------------

  static void Heights(string work)
  {
    Section("editor pack: the value rule on ground below the floor and above the top, and the edge");
    // Value rule on a grid of metres.
    bool ruleOk = true;
    foreach (var amount in new[] { 0.3f, 2f, 5f, 81f })
      foreach (var m in new[] { -400f, -30f, 0f, 29.999f, 30f, 123.456f, 370f, 8000f })
      {
        float v = M.MetresToValue(m, amount, 0.5f);
        float file = !(v >= 0f) ? 0f : v;
        float back = M.ValueToMetres(file, amount, 0.5f);
        float floor = M.ValueToMetres(0f, amount, 0.5f);
        bool fine = file >= 0f && (m < floor ? back == floor : Math.Abs(back - m) < 1e-3f * Math.Max(1f, Math.Abs(m) / 100f));
        if (!fine) { ruleOk = false; System.Console.WriteLine($"    amount {amount} {m} m -> {file} -> {back}"); }
      }
    C(ruleOk, "metres -> value -> metres holds within a millimetre (and floors at the amount's floor) over a grid incl. -400, 29.999, 370 and 8000 m");

    const int n = 128;
    float Land(float x, float z) => 60f + 90f * Mathf.Sin(x * 0.0004f) * Mathf.Cos(z * 0.0003f) - (x * x + z * z > 8.1e7f ? 400f : 0f);
    foreach (var amount in new[] { 2f, 0.3f })
    {
      var o = Options(n, x =>
      {
        x.EditorAmount = amount;
        x.EditorMasks = WorldExport.EditorMask.None;
        x.EditorFormats = WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32;
      });
      var r = Export(work, "editor-land-" + amount, o, Land);
      var tif = ReadTiff(Path.Combine(r.Dir, "editor", "height.tif"), out var info, out var keep);
      long low = 0, high = 0;
      int bad = 0;
      float vmin = float.MaxValue, vmax = float.MinValue;
      for (int row = 0; row < n; row++)
        for (int col = 0; col < n; col++)
        {
          float h = Land(M.PixelToWorld(col, n, T), M.FileRowToWorldZ(row, n, T));
          float v = (h / 200f + 0.15f - 0f) / amount;
          if (!(v >= 0f)) low++; else if (v > 1f) high++;
          float want = !(v >= 0f) ? 0f : v;
          if (BitConverter.SingleToInt32Bits(tif[row * n + col]) != BitConverter.SingleToInt32Bits(want)) bad++;
          vmin = Math.Min(vmin, want);
          vmax = Math.Max(vmax, want);
        }
      using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir, "editor", "editor.json")));
      var hj = json.RootElement.GetProperty("height");
      C(bad == 0 && low > 100 && hj.GetProperty("clippedLow").GetInt64() == low && hj.GetProperty("clippedHigh").GetInt64() == high && (amount > 1f ? high == 0 : high > 100),
        $"amount {amount}: the file is max(v, 0), unclamped above ({low} pixels floored, {high} above 1.0, {bad} pixels differ)");
      C(tif.Min() == 0f && tif.Max() == vmax && Math.Abs(hj.GetProperty("valueRange")[1].GetDouble() - vmax) < 1e-6 && (amount > 1f || tif.Max() > 1f), $"amount {amount}: the range in editor.json is the file's ({tif.Min()}..{tif.Max()})");
      var r32 = FloatsFromHex(JsonDocument.Parse(Py("r32", Path.Combine(r.Dir, "editor", "height.r32"))).RootElement.GetProperty("pixels").GetString()!);
      C(r32.Max() <= 1f && r32.Min() >= 0f && r32.Zip(tif, (a, b) => a == Math.Min(b, 1f)).All(x => x), $"amount {amount}: height.r32 is clamped to 0..1");
      if (amount < 1f)
        C(r.Summary.Any(l => l.Contains("WARNING") && l.Contains("above 1.0")) && File.ReadAllText(Path.Combine(r.Dir, "README.txt")).Contains("above 1.0"), "pixels above 1.0 are warned about in the summary and the README");
      keep.Dispose();
    }

    // The editor's heights are the ground as it is: the edge drop-off option changes heightmap.png's ring, not the pack.
    float Shore(float x, float z) => 80f + 20f * Mathf.Sin(x * 0.0003f);
    var oOn = Options(n, x => { x.EdgeDropoff = true; x.EditorMasks = WorldExport.EditorMask.None; });
    var oOff = Options(n, x => { x.EdgeDropoff = false; x.EditorMasks = WorldExport.EditorMask.None; });
    var on = Export(work, "editor-edge-on", oOn, Shore);
    var off = Export(work, "editor-edge-off", oOff, Shore);
    C(Sha(Path.Combine(on.Dir, "editor", "height.tif")) == Sha(Path.Combine(off.Dir, "editor", "height.tif")), "height.tif does not depend on the edge drop-off setting");
    C(Sha(Path.Combine(on.Dir, "heightmap.png")) != Sha(Path.Combine(off.Dir, "heightmap.png")), "while heightmap.png does (its ring holds the pre-drop-off value)");
  }

  // ---- bands, a folder without the pack, the memory of a pass ------------------------------------------------------------------

  static void BandsAndOthers(string work)
  {
    Section("editor pack: the same bytes whatever the bands, the folders it makes and removes");
    const int n = 128;
    var discs = new List<(float, float, float)> { (0f, 0f, 60f), (-9000f, 4000f, 120f) };
    string Digest(Run_ r)
    {
      var ed = Path.Combine(r.Dir, "editor");
      var files = Directory.GetFiles(ed, "*", SearchOption.AllDirectories).Select(f => f.Substring(ed.Length + 1)).Where(f => f != "editor.json" && f != "README-editor.txt").OrderBy(f => f, StringComparer.Ordinal).ToList();
      return string.Join("\n", files.Select(f => f + " " + Sha(Path.Combine(ed, f))));
    }
    string crc(Run_ r) => JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Dir, "editor", "editor.json"))).RootElement.GetProperty("height").GetProperty("crc32").GetString()!;
    var reference = Export(work, "editor-bands-ref", Options(n, x => { x.EditorMasks = WorldExport.EditorMask.All; x.EditorFormats = WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32; x.EditorMetres = true; }), discs: () => discs);
    var want = Digest(reference);
    C(want.Split('\n').Length >= 14, "the reference run wrote " + want.Split('\n').Length + " files under editor/");
    foreach (var rows in new[] { 1, 7, 64, n })
    {
      var r = Export(work, "editor-bands-" + rows, Options(n, x => { x.EditorMasks = WorldExport.EditorMask.All; x.EditorFormats = WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32; x.EditorMetres = true; }), bands: rows, discs: () => discs);
      C(r.Phase == "Done" && Digest(r) == want && crc(r) == crc(reference), $"bands of {rows} row(s): every file under editor/ is byte for byte the same (slope included, which needs the rows around a band)");
    }

    // editoronly: heightmap.png and the pack.
    WorldExportCommands.TryParse($"{n} editoronly", out var parsed, out _);
    parsed.HeightmapAmount = 2f;
    var only = Export(work, "editor-only", parsed, discs: () => discs);
    var top = Directory.GetFiles(only.Dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
    C(only.Phase == "Done" && top.SequenceEqual(new[] { "README.txt", "export.cfg", "heightmap.png", "manifest.json", "world.json" }), "editoronly: heightmap.png, export.cfg, README.txt, manifest.json, world.json and editor/ (" + string.Join(", ", top) + ")");

    // A pack only: no heightmap.
    var packOnly = Options(n, x => { x.Heightmap = false; x.EditorMasks = WorldExport.EditorMask.Water; });
    var po = Export(work, "editor-pack-only", packOnly);
    C(po.Phase == "Done" && !File.Exists(Path.Combine(po.Dir, "heightmap.png")) && File.Exists(Path.Combine(po.Dir, "editor", "height.tif")), "with no heightmap the pack is still written (and the export is valid)");

    // perpoint: no soft masks, per-point heights.
    var perPoint = Options(n, x => { x.ZoneBlend = false; x.EditorMasks = WorldExport.EditorMask.All; });
    var pp = Export(work, "editor-perpoint", perPoint);
    var tifPP = ReadTiff(Path.Combine(pp.Dir, "editor", "height.tif"), out _, out var kPP);
    int ppBad = 0;
    for (int row = 0; row < n; row++)
      for (int col = 0; col < n; col++)
      {
        float wx = M.PixelToWorld(col, n, T), wz = M.FileRowToWorldZ(row, n, T);
        float h = TerrainTest.Height(TerrainTest.Biome(wx, wz), wx, wz, out _);
        float v = (h / 200f + 0.15f) / 2f;
        if (BitConverter.SingleToInt32Bits(tifPP[row * n + col]) != BitConverter.SingleToInt32Bits(!(v >= 0f) ? 0f : v)) ppBad++;
      }
    kPP.Dispose();
    C(pp.Phase == "Done" && ppBad == 0 && !Directory.GetFiles(Path.Combine(pp.Dir, "editor", "masks"), "biome-soft-*").Any() && Directory.GetFiles(Path.Combine(pp.Dir, "editor", "masks"), "biome-*.png").Length >= 4,
      $"perpoint: heights per point ({ppBad} differ), hard biome masks, and no soft masks (they need the zone blend)");
  }

  // ---- a big pack, with its memory and time measured --------------------------------------------------------------------------

  static long Rss()
  {
    try
    {
      foreach (var line in File.ReadAllLines("/proc/self/status"))
        if (line.StartsWith("VmRSS:")) return long.Parse(line.Substring(6).Trim().Split(' ')[0]);
    }
    catch
    {
      // No /proc: no numbers.
    }
    return 0;
  }

  static long Hwm()
  {
    try
    {
      foreach (var line in File.ReadAllLines("/proc/self/status"))
        if (line.StartsWith("VmHWM:")) return long.Parse(line.Substring(6).Trim().Split(' ')[0]);
    }
    catch
    {
      // No /proc: no numbers.
    }
    return 0;
  }

  /// <summary>BCEXPORT_ONLY=editorbig BCEXPORT_BIG=&lt;size&gt;: the pack alone (no heightmap.png, default masks, one TIFF) at that size on the fake world.</summary>
  public static void Big(string work, int n)
  {
    Section($"editor pack at {n} x {n} px: time and memory");
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Big Editor World", m_seedName = "BigSeed", m_seed = 7 };
    var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    BC.Settings = new BC.BetterContinentsSettings();
    var o = Options(n, x => { x.Heightmap = false; x.EditorMasks = WorldExport.EditorMask.Default; });
    var dir = Path.Combine(work, "big-editor");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);
    var peaks = new List<(string Phase, double Seconds, long PeakKb)>();
    var clock = Stopwatch.StartNew();
    long before = Rss(), peak = 0;
    string phase = "";
    double start = 0;
    bool stop = false;
    var gate = new object();
    var sampler = new Thread(() =>
    {
      while (!Volatile.Read(ref stop))
      {
        var r = Rss();
        lock (gate) if (r > peak) peak = r;
        Thread.Sleep(100);
      }
    }) { IsBackground = true };
    sampler.Start();
    var drive = EndToEnd.Drive(job);
    while (drive.MoveNext())
    {
      var now = WorldExport.Phase;
      if (now != phase)
      {
        lock (gate)
        {
          if (phase.Length > 0) peaks.Add((phase, clock.Elapsed.TotalSeconds - start, peak));
          phase = now;
          peak = 0;
          start = clock.Elapsed.TotalSeconds;
        }
      }
      Thread.Sleep(2);
    }
    lock (gate) peaks.Add((phase, clock.Elapsed.TotalSeconds - start, peak));
    Volatile.Write(ref stop, true);
    sampler.Join();
    foreach (var p in peaks)
      System.Console.WriteLine($"    {p.Phase,-44} {p.Seconds,7:0.0} s, peak {p.PeakKb / 1024,6} MB");
    System.Console.WriteLine($"    MEASURED editor pack {n}: {clock.Elapsed.TotalSeconds:0.0} s in all; process RSS {before / 1024} MB before, peak (VmHWM) {Hwm() / 1024} MB; managed {GC.GetTotalMemory(false) / 1048576} MB");
    C(WorldExport.Phase == "Done" && WorldExport.LastError == null, $"the {n} px pack finishes: {WorldExport.Phase} {WorldExport.LastError}");
    var ed = Path.Combine(dir, "editor");
    foreach (var f in Directory.GetFiles(ed, "*", SearchOption.AllDirectories).OrderBy(f => f))
      System.Console.WriteLine($"    {f.Substring(ed.Length + 1),-34} {new FileInfo(f).Length / 1048576.0,9:0.0} MB");
    var tifPath = Path.Combine(ed, "height.tif");
    var desc = "BetterContinents: Height Encoding = bc-v; Heightmap Amount = 2; Sea Level Adjustment = 0.5; Orientation = north-up";
    C(new FileInfo(tifPath).Length == TiffRowWriter.FileLength(n, n, 32, desc, "Better Continents " + ModInfo.Version), "height.tif is header + n x n x 4 bytes");
    // Rows read straight from the file against the fake world.
    int header = TiffRowWriter.HeaderLength(n, desc, "Better Continents " + ModInfo.Version), bad = 0, checkedPixels = 0;
    using (var fs = new FileStream(tifPath, FileMode.Open, FileAccess.Read))
    {
      var row = new byte[n * 4];
      var f = new float[n];
      foreach (var r in new[] { 0, 1, n / 3, n / 2, n - 2, n - 1 })
      {
        fs.Seek(header + (long)r * n * 4, SeekOrigin.Begin);
        fs.Read(row, 0, row.Length);
        Buffer.BlockCopy(row, 0, f, 0, row.Length);
        for (int c = 0; c < n; c += 5)
        {
          float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
          float v = (Blend(wx, wz) / 200f + 0.15f - 0f) / 2f;
          checkedPixels++;
          if (BitConverter.SingleToInt32Bits(f[c]) != BitConverter.SingleToInt32Bits(!(v >= 0f) ? 0f : v)) bad++;
        }
      }
    }
    C(bad == 0, $"{checkedPixels} pixels read straight from height.tif are the fake world's ({bad} differ)");
    try { Directory.Delete(dir, true); } catch { }
  }

  static void Cancel(string work)
  {
    Section("editor pack: a cancel");
    var o = Options(512, x => { x.EditorMasks = WorldExport.EditorMask.All; x.EditorFormats = WorldExport.EditorFormat.Tiff | WorldExport.EditorFormat.Raw32; });
    int seen = 0;
    bool sawTmp = false;
    string dir = Path.Combine(work, "editor-cancel");
    var r = Export(work, "editor-cancel", o, cancelAt: phase =>
    {
      if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories).Length > 0)
      {
        sawTmp = true;
        return seen++ == 2;
      }
      return false;
    });
    C(sawTmp, "the pack's files were being written (as .tmp) when the cancel came");
    C(r.Phase == "Cancelled" && !WorldExport.IsRunning, $"cancel ends in 'Cancelled' ({r.Phase})");
    C(!Directory.Exists(dir), "a cancelled export leaves no folder, no editor/, no .tmp, no empty folder");

  }
}

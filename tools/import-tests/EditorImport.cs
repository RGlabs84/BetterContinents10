// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace ImportTest;

// The way back of the editor pack (bc_import edited, EditorIngest): a made-up export with a pack, "what an editor returned" in each
// layout and orientation, the folder it makes and what is in it (heights within a millimetre where the ground is gentle, the edge ring
// and every other map untouched, the location map byte for byte), the units, the readers against files an independent writer made
// (tools/editor_ref.py), hostile files, a cancel.
internal static partial class Program
{
  const float TotalM = 21000f;

  static string Py(params string[] args) => EditorPyRunner.Run(args);

  // A made-up pack: the ground v(row, col) as float TIFF, the export's own heightmap.png (the ring in it a pattern of its own), a
  // location map and a forest map to copy, export.cfg and manifest.json.
  sealed class PackExport
  {
    public string Dir = "", Name = "";
    public int N;
    public float[] V = [];
    public double Amount = 2, Sea = 0.5;
    public bool EdgeDropoff = true;
    public ushort[] SourceGrey = [];
  }

  static float[] Ground(int n, bool gentle = true)
  {
    var v = new float[n * n];
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        double x = c / (double)(n - 1), y = r / (double)(n - 1);
        // A gentle slope (a millimetre or so a pixel), a hill that is not symmetrical in any way, a cliff and a sea.
        double g = 0.2 + 0.0015 * x + 0.001 * y;
        g += 0.08 * Math.Exp(-((y - 0.25) * (y - 0.25) + (x - 0.75) * (x - 0.75)) / 0.01);
        g += 0.03 * Math.Exp(-((y - 0.7) * (y - 0.7) / 0.02 + (x - 0.3) * (x - 0.3) / 0.002));
        if (x > 0.55 && x < 0.6 && y > 0.5) g += 0.05;
        v[r * n + c] = (float)g;
      }
    return v;
  }

  static PackExport MakePack(string world, string name, int n, float[] v, bool edgeDropoff = true, double amount = 2, double sea = 0.5, bool withEditorJson = true, bool withPackTiff = true)
  {
    var p = new PackExport { Name = name, N = n, V = v, Amount = amount, Sea = sea, EdgeDropoff = edgeDropoff, Dir = Path.Combine(root, world, name) };
    Directory.CreateDirectory(Path.Combine(p.Dir, "editor", "return"));
    double sla = WorldExportMath.SeaLevelAdjustment((float)sea);
    // heightmap.png: v as grey, and a pattern of its own past the world's edge.
    p.SourceGrey = new ushort[n * n];
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(r, n, TotalM);
        bool ring = edgeDropoff && Mathf.Sqrt(wx * wx + wz * wz) > 10000f;
        p.SourceGrey[r * n + c] = ring ? (ushort)(3000 + (c * 7 + r * 3) % 500) : (ushort)Math.Round(Math.Min(1.0, Math.Max(0.0, v[r * n + c])) * 65535.0);
      }
    var record = new HeightmapRecord((float)amount, (float)sea);
    File.WriteAllBytes(Path.Combine(p.Dir, "heightmap.png"), PngWriter.Write(n, n, 0, 16, (y, raw) =>
    {
      for (int x = 0; x < n; x++)
      {
        raw[2 * x] = (byte)(p.SourceGrey[y * n + x] >> 8);
        raw[2 * x + 1] = (byte)p.SourceGrey[y * n + x];
      }
    }, HeightmapRecord.Keyword, record.Text));
    var rng = new System.Random(5);
    File.WriteAllBytes(Path.Combine(p.Dir, "locationmap.png"), PngWriter.Write(n, n, 2, 8, (y, raw) =>
    {
      for (int x = 0; x < n; x++)
        if ((x * 31 + y * 17) % 997 == 0) { raw[3 * x] = 255; raw[3 * x + 1] = (byte)(y % 255); raw[3 * x + 2] = (byte)(x % 255); }
    }));
    File.WriteAllText(Path.Combine(p.Dir, "locationmap.txt"), "Start: FF0000\n");
    File.WriteAllBytes(Path.Combine(p.Dir, "forestmap.png"), PngWriter.Write(n, n, 0, 16, (y, raw) => { for (int x = 0; x < n; x++) { raw[2 * x] = (byte)(x * 255 / (n - 1)); raw[2 * x + 1] = 0; } }));
    var heightSection = SettingsSchema.HeightmapAmount.Section;
    File.WriteAllLines(Path.Combine(p.Dir, "export.cfg"),
    [
      "## Better Continents world export of a made-up world",
      $"[{SettingsSchema.Enabled.Section}]", $"{SettingsSchema.Enabled.Key} = true",
      $"[{SettingsSchema.WorldSize.Section}]", $"{SettingsSchema.WorldSize.Key} = 10000", $"{SettingsSchema.EdgeSize.Key} = 500",
      $"[{SettingsSchema.Directory.Section}]", $"{SettingsSchema.Directory.Key} = {p.Dir.Replace('\\', '/')}",
      $"[{SettingsSchema.SeaLevel.Section}]", $"{SettingsSchema.SeaLevel.Key} = {sea.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
      $"[{heightSection}]", $"{SettingsSchema.HeightmapAmount.Key} = {amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", $"{SettingsSchema.HeightmapBlend.Key} = 1",
      $"{SettingsSchema.HeightmapOverrideAll.Key} = true",
    ]);
    string C_(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    File.WriteAllText(Path.Combine(p.Dir, "manifest.json"),
      "{\n  \"format\": \"bc-export/1\",\n  \"modVersion\": \"0.10.7\",\n  \"exportedAt\": \"2026-10-26T12:00:00\",\n  \"seconds\": 1.5,\n  \"worldName\": \"" + world + "\",\n  \"seed\": 42,\n"
      + $"  \"size\": {n},\n  \"totalSize\": 21000,\n  \"worldRadius\": 10000,\n  \"totalRadius\": 10500,\n  \"heightmapAmount\": {C_(amount)},\n  \"seaLevel\": {C_(sea)},\n"
      + $"  \"seaLevelAdjustment\": {C_(sla)},\n  \"heightEncoding\": \"metres = ...\",\n  \"edgeDropoff\": {(edgeDropoff ? "true" : "false")},\n"
      + "  \"preset\": {\n    \"name\": \"x\",\n    \"made\": true\n  },\n  \"editorPack\": {\n    \"dir\": \"editor\"\n  },\n  \"import\": \"bc_import x\",\n"
      + "  \"files\": [\"heightmap.png\", \"locationmap.png\", \"locationmap.txt\", \"forestmap.png\", \"export.cfg\", \"README.txt\", \"manifest.json\"],\n  \"notes\": [\"Only the generated world is exported.\"]\n}\n");
    File.WriteAllText(Path.Combine(p.Dir, "README.txt"), "an export\n");
    if (withEditorJson)
      File.WriteAllText(Path.Combine(p.Dir, "editor", "editor.json"),
        "{\n  \"format\": \"bc-editor/1\",\n  \"grid\": {\n    \"size\": " + n + ",\n    \"totalSize\": 21000\n  },\n  \"height\": {\n    \"amount\": " + C_(amount) + ",\n    \"seaLevel\": " + C_(sea)
        + ",\n    \"seaLevelAdjustment\": " + C_(sla) + ",\n    \"valueRange\": [" + C_(v.Min()) + ", " + C_(v.Max()) + "]\n  }\n}\n");
    if (withPackTiff)
      WriteTiff(Path.Combine(p.Dir, "editor", "height.tif"), n, v);
    return p;
  }

  static void WriteTiff(string path, int n, float[] values)
  {
    using var tif = new TiffRowWriter(path, n, n, 32, "BetterContinents: test", "test");
    var bytes = new byte[n * n * 4];
    Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
    tif.WriteRows(bytes, 0, n);
    tif.Complete();
  }

  static float[] Turned(float[] f, int n, EditorOrient o)
  {
    // The picture an editor returned so that Map(o) turns it back into f.
    var returned = new float[n * n];
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        var (sr, sc) = EditorOrientation.Map(o, r, c, n);
        returned[sr * n + sc] = f[r * n + c];
      }
    return returned;
  }

  static (ushort[] Grey, sbyte[]? Fine, HeightmapRecord? Record, int Size) ReadPair(string folder)
  {
    using var image = Image.Load<L16>(Path.Combine(folder, "heightmap.png"));
    int n = image.Width;
    var px = new L16[n * n];
    image.CopyPixelDataTo(px);
    var record = HeightmapRecord.From(image.Metadata.GetPngMetadata().TextData);
    sbyte[]? fine = null;
    if (File.Exists(Path.Combine(folder, "heightmap-fine.png")))
    {
      using var fi = Image.Load<L8>(Path.Combine(folder, "heightmap-fine.png"));
      var fp = new L8[n * n];
      fi.CopyPixelDataTo(fp);
      fine = fp.Select(p => (sbyte)p.PackedValue).ToArray();
    }
    return (px.Select(p => p.PackedValue).ToArray(), fine, record, n);
  }

  // Metres of the 24-bit pair at a pixel: v = (256 c + f) / 16776960.
  static double PairMetres(ushort[] grey, sbyte[]? fine, int i, double amount, double sla) =>
    (((256.0 * grey[i] + (fine?[i] ?? 0)) / 16776960.0) * amount - WorldExportMath.BaseOffset + sla) * 200.0;

  static IngestResult Ingest(PackExport p, string args = "", Func<bool>? cancelled = null)
  {
    Assert(IngestOptions.TryParse(args, out var options, out var rest, out var error), "ingest options parse: " + error);
    return EditorIngest.Run(p.Dir, options, line => { lock (Said) Said.Add(line); }, cancelled);
  }

  static readonly List<string> Said = [];

  static void Assert(bool ok, string what)
  {
    if (!ok) throw new InvalidOperationException(what);
  }

  // How well a folder's heights are those of `v` (the pack's encoding): the worst error over the pixels inside the world, over the gentle
  // ones (neighbours within 5 mm), and how many are gentle.
  static (double Worst, double WorstGentle, int Gentle, int Inside) Accuracy(string folder, float[] v, int n, double packAmount, double packSla, double outAmount, double outSla)
  {
    var (grey, fine, record, size) = ReadPair(folder);
    double worst = 0, worstGentle = 0;
    int gentle = 0, inside = 0;
    double M(int i) => (v[i] * packAmount - WorldExportMath.BaseOffset + packSla) * 200.0;
    // The edge ring holds the export's own pattern, so a pixel beside it has a neighbour that is not the ground.
    bool Ring(int r, int c) => r >= 0 && c >= 0 && r < n && c < n
      && Mathf.Sqrt(Mathf.Pow(WorldExportMath.PixelToWorld(c, n, TotalM), 2) + Mathf.Pow(WorldExportMath.FileRowToWorldZ(r, n, TotalM), 2)) > 10000f;
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(r, n, TotalM);
        if (Mathf.Sqrt(wx * wx + wz * wz) > 9950f)
          continue;
        inside++;
        int i = r * n + c;
        double want = M(i);
        double got = PairMetres(grey, fine, i, outAmount, outSla);
        double err = Math.Abs(got - want);
        worst = Math.Max(worst, err);
        bool isGentle = (c == 0 || Math.Abs(M(i - 1) - want) < 0.005) && (c == n - 1 || Math.Abs(M(i + 1) - want) < 0.005) && (r == 0 || Math.Abs(M(i - n) - want) < 0.005) && (r == n - 1 || Math.Abs(M(i + n) - want) < 0.005);
        if (isGentle && want >= 30.0 && !Ring(r - 1, c) && !Ring(r + 1, c) && !Ring(r, c - 1) && !Ring(r, c + 1))
        {
          gentle++;
          worstGentle = Math.Max(worstGentle, err);
        }
      }
    return (worst, worstGentle, gentle, inside);
  }

  static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

  static void EditorImport()
  {
    EditorRoundTrip();
    EditorOrientations();
    EditorUnits();
    EditorReaders();
    EditorErrors();
    EditorCommandLine();
    EditorCancel();
    EditorSizes();
  }

  static void EditorRoundTrip()
  {
    Section("editor return: a made-up pack, edited and brought back");
    const int n = 256;
    var v0 = Ground(n);
    var p = MakePack("EditWorld", "export-2026-10-26-12-00-00", n, v0);
    // What the editor did: a hill, and the picture upside down.
    var edited = (float[])v0.Clone();
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
        edited[r * n + c] += (float)(0.1 * Math.Exp(-((r - 100) * (r - 100) + (c - 60) * (c - 60)) / 150.0));
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, Turned(edited, n, EditorOrient.FlipY));
    lock (Said) Said.Clear();
    var res = Ingest(p);
    Assert(res.Error == null, "ingest failed: " + res.Error);
    var outDir = res.OutDir!;
    C(res.Error == null && Path.GetFileName(outDir) == p.Name + "-edited" && Path.GetDirectoryName(outDir) == Path.GetDirectoryName(p.Dir), $"a new folder beside the export: {Path.GetFileName(outDir)}");
    C(res.OrientationFound && res.Orientation == EditorOrient.FlipY, $"the upside-down picture is found out ({EditorOrientation.Name(res.Orientation)}, scores {string.Join(" ", res.OrientationScores.Select(s => s.ToString("0.###")))})");
    var names = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
    C(names.SequenceEqual(new[] { "README.txt", "export.cfg", "forestmap.png", "heightmap-fine.png", "heightmap.png", "locationmap.png", "locationmap.txt", "manifest.json" }) && !Directory.Exists(Path.Combine(outDir, "editor")),
      "it holds heightmap.png and heightmap-fine.png, the other maps and the text files, and no editor/ (" + string.Join(", ", names) + ")");
    C(Sha(Path.Combine(outDir, "locationmap.png")) == Sha(Path.Combine(p.Dir, "locationmap.png")) && Sha(Path.Combine(outDir, "forestmap.png")) == Sha(Path.Combine(p.Dir, "forestmap.png"))
      && Sha(Path.Combine(outDir, "locationmap.txt")) == Sha(Path.Combine(p.Dir, "locationmap.txt")), "locationmap.png, forestmap.png and the legend are byte for byte the export's, though the heights were turned");
    var (worst, worstGentle, gentle, inside) = Accuracy(outDir, edited, n, 2, 0, 2, 0);
    C(worst < 3.06e-3 && worstGentle < 1e-3 && gentle > inside / 3, $"the heights are the edited ones: worst {worst * 1000:0.###} mm (a half grey step is 3.05 mm), on {gentle} of {inside} gentle pixels {worstGentle * 1000:0.####} mm");
    var (grey, fine, record, size) = ReadPair(outDir);
    C(record != null && record.Amount == 2f && record.SeaLevel == 0.5f && fine != null && size == n, "heightmap.png has the settings record (amount 2, sea level 0.5)");
    var fineText = new System.Text.StringBuilder();
    using (var fi = Image.Load<L8>(Path.Combine(outDir, "heightmap-fine.png")))
      foreach (var t in fi.Metadata.GetPngMetadata().TextData)
        fineText.Append(t.Value);
    C(fineText.ToString() == FineHeights.RecordText(8, PngRowWriter.FileCrc32(Path.Combine(outDir, "heightmap.png"))), "heightmap-fine.png names the CRC-32 of the new heightmap.png: " + fineText);

    // The edge ring is the export's own pixels.
    int ringBad = 0, ringN = 0;
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(r, n, TotalM);
        if (Mathf.Sqrt(wx * wx + wz * wz) > 10000f)
        {
          ringN++;
          if (grey[r * n + c] != p.SourceGrey[r * n + c] || fine![r * n + c] != 0) ringBad++;
        }
      }
    C(ringN > 1000 && ringBad == 0, $"past the world's edge every pixel is the export's own (the pre-drop-off value): {ringN} pixels, {ringBad} different");
    double hill = PairMetres(grey, fine, 100 * n + 60, 2, 0), before = (v0[100 * n + 60] * 2.0 - WorldExportMath.BaseOffset) * 200.0;
    C(hill - before > 30.0, $"the edit is in the result: the hill adds {hill - before:0.#} m at its centre");

    // export.cfg and manifest.json.
    var cfg = File.ReadAllLines(Path.Combine(outDir, "export.cfg"));
    C(cfg.Any(l => l.StartsWith(SettingsSchema.Directory.Key + " = ") && l.EndsWith(outDir.Replace('\\', '/'))) && cfg.Any(l => l == SettingsSchema.HeightmapAmount.Key + " = 2")
      && !cfg.Any(l => l.Contains(p.Dir.Replace('\\', '/') + "\n")) && cfg.Count(l => l.StartsWith(SettingsSchema.Directory.Key)) == 1, "export.cfg points Directory at the new folder and keeps Heightmap Amount 2");
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "manifest.json")));
    var m = manifest.RootElement;
    var from = m.GetProperty("editedFrom");
    var listed = m.GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToList();
    C(m.GetProperty("format").GetString() == "bc-export/1" && m.GetProperty("size").GetInt32() == n && m.GetProperty("heightmapAmount").GetDouble() == 2.0 && from.GetProperty("export").GetString() == p.Name
      && from.GetProperty("orientation").GetString() == "flipy" && from.GetProperty("edgeRing").GetString() == "keep" && from.GetProperty("returnFile").GetString() == "height.tif"
      && listed.Contains("heightmap-fine.png") && listed.Contains("manifest.json") && !m.TryGetProperty("editorPack", out _) && m.GetProperty("preset").ValueKind == JsonValueKind.Null
      && m.GetProperty("fineBits").GetInt32() == 8 && m.GetProperty("import").GetString()!.Contains(Path.GetFileName(outDir)),
      "manifest.json stays bc-export/1, gets editedFrom, the fine keys and the new import line, and drops the preset and the pack");
    C(m.GetProperty("notes").EnumerateArray().Any(x => x.GetString()!.Contains("bc_import edited")), "and a note says the heights are from an editor");
    // The source export is as it was.
    C(!Directory.Exists(Path.Combine(outDir, "editor")) && File.Exists(Path.Combine(p.Dir, "editor", "return", "height.tif")) && JsonDocument.Parse(File.ReadAllText(Path.Combine(p.Dir, "manifest.json"))).RootElement.TryGetProperty("editorPack", out _),
      "the export itself is not touched");
    // A second ingest makes a second folder.
    var again = Ingest(p, "orient=flipy");
    C(again.Error == null && Path.GetFileName(again.OutDir!) == p.Name + "-edited-2", "a second one is -edited-2");

    // The folder is an export like any other: the preset builder takes it.
    var load = Path.Combine(root, "EditWorld", "loadable");
    Directory.CreateDirectory(load);
    foreach (var f in new[] { "heightmap.png", "heightmap-fine.png", "export.cfg", "manifest.json", "forestmap.png" })
      File.Copy(Path.Combine(outDir, f), Path.Combine(load, f));
    File.WriteAllLines(Path.Combine(load, "export.cfg"), File.ReadAllLines(Path.Combine(load, "export.cfg")).Select(l => l.StartsWith(SettingsSchema.Directory.Key + " = ") ? SettingsSchema.Directory.Key + " = " + load.Replace('\\', '/') : l));
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var made = BC.BetterContinentsSettings.CreateForImport(WorldImport.MakePlan(load).Values, lean: false);
    var heights = Map<ImageMapFloat>(made, "heightmap.png");
    double mapWorst = 0;
    for (int k = 0; k < 400; k++)
    {
      int r = 20 + k * 37 % (n - 40), c = 20 + k * 91 % (n - 40);
      float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(r, n, TotalM);
      if (Mathf.Sqrt(wx * wx + wz * wz) > 9900f) continue;
      double got = WorldExportMath.ValueToMetres(heights.GetValue(wx / TotalM + 0.5f, wz / TotalM + 0.5f), made.HeightmapAmount, made.SeaLevel);
      double want = (edited[r * n + c] * 2.0 - WorldExportMath.BaseOffset) * 200.0;
      mapWorst = Math.Max(mapWorst, Math.Abs(got - want));
    }
    C(made.HasHeightMap && heights.HasFine && made.HeightmapAmount == 2f && mapWorst < 0.05, $"the preset builder reads the new folder: a world made from it has fine heights and rebuilds the edited ground (400 points, worst {mapWorst * 1000:0.#} mm, bilinear)");

    // edgeDropoff off: no ring at all, the returned heights everywhere.
    var q = MakePack("EditWorld", "export-2026-10-26-12-05-00", n, v0, edgeDropoff: false);
    WriteTiff(Path.Combine(q.Dir, "editor", "return", "height.tif"), n, edited);
    var noRing = Ingest(q);
    var (g2, f2, _, _) = ReadPair(noRing.OutDir!);
    C(noRing.Error == null && Accuracy(noRing.OutDir!, edited, n, 2, 0, 2, 0).Worst < 3.06e-3 && noRing.OrientationFound, "an export without the edge drop-off: the heights everywhere are the returned ones");

    // ering=gaea: the pre-drop-off value is worked out from the returned height.
    var r3 = Ingest(p, "ering=gaea orient=flipy");
    var (g3, f3, _, _) = ReadPair(r3.OutDir!);
    int ring3 = 0;
    for (int i = 0; i < n * n; i++) if (g3[i] != p.SourceGrey[i]) ring3++;
    C(r3.Error == null && ring3 > 1000, $"ering=gaea does not keep the export's ring ({ring3} pixels differ from it)");
  }

  static void EditorOrientations()
  {
    Section("editor return: every way the picture can come back turned");
    const int n = 128;
    var v0 = Ground(n);
    var edited = (float[])v0.Clone();
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
        edited[r * n + c] += (float)(0.05 * Math.Exp(-((r - 50) * (r - 50) + (c - 30) * (c - 30)) / 60.0));
    // Map and FillRows agree on every turn.
    var pack0 = MakePack("OrientWorld", "export-2026-10-26-13-00-00", n, v0);
    foreach (EditorOrient o in Enum.GetValues(typeof(EditorOrient)))
    {
      var returned = Turned(edited, n, o);
      WriteTiff(Path.Combine(pack0.Dir, "editor", "return", "height.tif"), n, returned);
      using (var src = HeightSource.Open(Path.Combine(pack0.Dir, "editor", "return", "height.tif")))
      {
        var rows = new float[n * n];
        EditorOrientation.FillRows(src, o, 0, n, rows, new float[n]);
        var part = new float[40 * n];
        EditorOrientation.FillRows(src, o, 33, 40, part, new float[n]);
        C(rows.SequenceEqual(edited) && part.SequenceEqual(edited.Skip(33 * n).Take(40 * n)), $"{EditorOrientation.Name(o)}: reading the turned picture back gives the original, whole and a band of rows");
      }
      var auto = Ingest(pack0);
      var explicitly = Ingest(pack0, "orient=" + EditorOrientation.Name(o));
      Assert(auto.Error == null && explicitly.Error == null, $"{EditorOrientation.Name(o)}: " + auto.Error + explicitly.Error);
      var acc = Accuracy(auto.OutDir!, edited, n, 2, 0, 2, 0);
      var acc2 = Accuracy(explicitly.OutDir!, edited, n, 2, 0, 2, 0);
      C(auto.OrientationFound && auto.Orientation == o && acc.Worst < 3.06e-3 && acc2.Worst < 3.06e-3 && explicitly.Orientation == o,
        $"{EditorOrientation.Name(o)}: orient=auto finds it and the heights are the edited ones (worst {acc.Worst * 1000:0.###} mm, scores {string.Join(" ", auto.OrientationScores.Select(s => s.ToString("0.##")))})");
      C(Sha(Path.Combine(auto.OutDir!, "locationmap.png")) == Sha(Path.Combine(pack0.Dir, "locationmap.png")) && Sha(Path.Combine(explicitly.OutDir!, "locationmap.png")) == Sha(Path.Combine(pack0.Dir, "locationmap.png")),
        $"{EditorOrientation.Name(o)}: locationmap.png is byte for byte the export's");
      // Bands of one, seven and all rows make the same files (the fine heights look a row below a band).
      foreach (var rowsPerBand in new[] { 1, 7 })
      {
        EditorIngest.BandRowsOverride = rowsPerBand;
        try
        {
          var banded = Ingest(pack0, "orient=" + EditorOrientation.Name(o));
          C(banded.Error == null && Sha(Path.Combine(banded.OutDir!, "heightmap.png")) == Sha(Path.Combine(explicitly.OutDir!, "heightmap.png")) && Sha(Path.Combine(banded.OutDir!, "heightmap-fine.png")) == Sha(Path.Combine(explicitly.OutDir!, "heightmap-fine.png")),
            $"{EditorOrientation.Name(o)}: bands of {rowsPerBand} row(s) give the same heightmap.png and heightmap-fine.png");
        }
        finally
        {
          EditorIngest.BandRowsOverride = 0;
        }
      }
    }
    // A wrong orient= is obeyed (and gives other heights).
    WriteTiff(Path.Combine(pack0.Dir, "editor", "return", "height.tif"), n, edited);
    var wrong = Ingest(pack0, "orient=flipx");
    C(wrong.Error == null && wrong.Orientation == EditorOrient.FlipX && Accuracy(wrong.OutDir!, edited, n, 2, 0, 2, 0).Worst > 1.0, "an explicit orient= is obeyed, right or wrong");

    // A picture that is the same turned (symmetric) cannot be told; a flat one cannot either; both say so and orient= settles it.
    var sym = new float[n * n];
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
        sym[r * n + c] = (float)(0.2 + 0.05 * Math.Sin(Math.PI * c / (n - 1)) * Math.Sin(Math.PI * r / (n - 1)));
    var symPack = MakePack("OrientWorld", "export-2026-10-26-13-10-00", n, sym);
    WriteTiff(Path.Combine(symPack.Dir, "editor", "return", "height.tif"), n, sym);
    var amb = Ingest(symPack);
    C(amb.Error != null && amb.Error.Contains("cannot tell how the returned picture is turned") && amb.Error.Contains("orient=") && amb.OutDir == null && !Directory.Exists(Path.Combine(symPack.Dir + "-edited")),
      "a symmetric picture: 'cannot tell how it is turned', nothing is made (" + (amb.Error ?? "no error")?.Substring(0, Math.Min(90, (amb.Error ?? "").Length)) + ")");
    C(Ingest(symPack, "orient=none").Error == null, "and orient=none settles it");
    var flat = MakePack("OrientWorld", "export-2026-10-26-13-20-00", n, new float[n * n].Select(_ => 0.3f).ToArray());
    WriteTiff(Path.Combine(flat.Dir, "editor", "return", "height.tif"), n, new float[n * n].Select(_ => 0.3f).ToArray());
    var flatRes = Ingest(flat);
    C(flatRes.Error != null && flatRes.Error.Contains("flat"), "a flat world: the orientation cannot be found (" + flatRes.Error + ")");
    // No pack height to compare with: taken as it is, said so.
    var nopack = MakePack("OrientWorld", "export-2026-10-26-13-30-00", n, v0, withPackTiff: false);
    WriteTiff(Path.Combine(nopack.Dir, "editor", "return", "height.tif"), n, v0);
    lock (Said) Said.Clear();
    var np = Ingest(nopack);
    C(np.Error == null && np.Orientation == EditorOrient.None && !np.OrientationFound && Said.Any(l => l.Contains("not checked")), "without editor/height.* the orientation is not checked, and it says so");
  }

  static void EditorUnits()
  {
    Section("editor return: units, the amount, the sea level, clipping");
    const int n = 128;
    var v0 = Ground(n);
    var p = MakePack("UnitWorld", "export-2026-10-26-14-00-00", n, v0);
    double M(float v) => (v * 2.0 - WorldExportMath.BaseOffset) * 200.0;
    // metres
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, v0.Select(v => (float)M(v)).ToArray());
    var metres = Ingest(p, "unit=metres");
    C(metres.Error == null && Accuracy(metres.OutDir!, v0, n, 2, 0, 2, 0).Worst < 3.06e-3, "unit=metres reads the file as metres");
    // a range
    double lo = -50, hi = 700;
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, v0.Select(v => (float)((M(v) - lo) / (hi - lo))).ToArray());
    var range = Ingest(p, "range=-50,700");
    C(range.Error == null && Accuracy(range.OutDir!, v0, n, 2, 0, 2, 0).Worst < 5e-3, "range=lo,hi reads a normalised build");
    var scaled = v0.Select(v => (float)((M(v) - M(v0.Min())) / (M(v0.Max()) - M(v0.Min())))).ToArray();
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, scaled);
    var guard = Ingest(p);
    C(guard.Error != null && guard.Error.Contains("Normalized") && guard.Error.Contains("Output Range = Raw") && guard.OutDir == null, "a return that spans exactly 0 to 1 when the pack's does not: 'this looks like a Normalized build', nothing is made");
    C(Ingest(p, "unit=v").Error == null, "unit=v says it is meant");
    // A pack of the editor's own v (the usual).
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, v0);
    var plain = Ingest(p);
    C(plain.Error == null && plain.Amount == 2f && plain.SeaLevel == 0.5f && plain.ClippedLow == 0 && plain.ClippedHigh == 0, "the usual return: the pack's amount and sea level, nothing clipped");

    // Heights far above and below: amount=auto and sealevel=auto.
    var tall = v0.Select((v, i) => (float)(v + (i % n > 100 ? 1.2 : 0) - (i / n < 10 ? 0.42 : 0))).ToArray();
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, tall);
    var tallMin = tall.Min(v => M(v)); var tallMax = tall.Max(v => M(v));
    lock (Said) Said.Clear();
    var plainTall = Ingest(p, "orient=none");
    C(plainTall.Error == null && plainTall.ClippedHigh > 0 && plainTall.ClippedLow > 0 && Said.Any(l => l.Contains("clipped")), $"heights from {tallMin:0} to {tallMax:0} m with amount 2 are clipped, counted ({plainTall.ClippedLow} low, {plainTall.ClippedHigh} high), and said");
    var auto = Ingest(p, "amount=auto sealevel=auto orient=none");
    float floor = WorldExportMath.ValueToMetres(0f, auto.Amount, auto.SeaLevel), ceil = WorldExportMath.ValueToMetres(1f, auto.Amount, auto.SeaLevel);
    C(auto.Error == null && auto.ClippedLow == 0 && auto.ClippedHigh == 0 && floor <= tallMin + 1e-3 && ceil >= tallMax - 1e-3 && auto.SeaLevel > 0.5f && auto.Amount > 2f,
      $"amount=auto sealevel=auto: Heightmap Amount {auto.Amount}, Sea Level {auto.SeaLevel} hold {tallMin:0} to {tallMax:0} m ({floor:0} to {ceil:0} m), nothing clipped");
    var tallAcc = Accuracy(auto.OutDir!, tall, n, 2, 0, auto.Amount, WorldExportMath.SeaLevelAdjustment(auto.SeaLevel));
    double halfStep = 200.0 * auto.Amount / 65535.0 / 2;
    C(tallAcc.Worst < halfStep * 1.01, $"and the heights are within half a grey step ({halfStep * 1000:0.##} mm): worst {tallAcc.Worst * 1000:0.##} mm");
    var cfg = File.ReadAllLines(Path.Combine(auto.OutDir!, "export.cfg"));
    C(cfg.Any(l => l == SettingsSchema.HeightmapAmount.Key + " = " + auto.Amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) && cfg.Any(l => l == SettingsSchema.SeaLevel.Key + " = " + auto.SeaLevel.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
      "export.cfg carries that amount and sea level");
    var (_, _, rec, _) = ReadPair(auto.OutDir!);
    C(rec != null && rec.Amount == auto.Amount && rec.SeaLevel == auto.SeaLevel, "so does the record in heightmap.png");
    // amount and sea level given.
    var given = Ingest(p, "amount=40 sealevel=0.6 orient=none");
    C(given.Error == null && given.Amount == 40f && Math.Abs(given.SeaLevel - 0.6f) < 1e-6f, "amount=40 sealevel=0.6 are used as given");
    // fine=off, fine=4
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, v0);
    var off = Ingest(p, "fine=off");
    var four = Ingest(p, "fine=4");
    C(off.Error == null && !File.Exists(Path.Combine(off.OutDir!, "heightmap-fine.png")) && four.Error == null && four.FineBitsWritten == 4 && Accuracy(four.OutDir!, v0, n, 2, 0, 2, 0).WorstGentle < 0.4e-3,
      "fine=off writes no fine file; fine=4 writes one with 4 bits (0.38 mm at Amount 2)");
    // PNG16 as a return.
    var png = PngWriter.Write(n, n, 0, 16, (y, raw) => { for (int x = 0; x < n; x++) { ushort g = (ushort)Math.Round(v0[y * n + x] * 65535.0); raw[2 * x] = (byte)(g >> 8); raw[2 * x + 1] = (byte)g; } });
    File.Delete(Path.Combine(p.Dir, "editor", "return", "height.tif"));
    File.WriteAllBytes(Path.Combine(p.Dir, "editor", "return", "height.png"), png);
    var fromPng = Ingest(p);
    C(fromPng.Error == null && Accuracy(fromPng.OutDir!, v0, n, 2, 0, 2, 0).Worst < 3.06e-3 * 1.01, "a 16-bit PNG is read too (to within a grey step)");
  }

  static void EditorReaders()
  {
    Section("editor return: the readers against files an independent writer made");
    const int n = 70;
    var dir = Path.Combine(work, "editor-readers");
    Directory.CreateDirectory(dir);
    var rng = new System.Random(3);
    var values = new float[n * n];
    for (int i = 0; i < values.Length; i++)
      values[i] = (float)(0.1 + 0.8 * rng.NextDouble());
    var raw = Path.Combine(dir, "values.f32");
    var rawBytes = new byte[n * n * 4];
    Buffer.BlockCopy(values, 0, rawBytes, 0, rawBytes.Length);
    File.WriteAllBytes(raw, rawBytes);
    var layouts = new (string Name, string[] Args, int Bits)[]
    {
      ("strips", [], 32), ("strips of 7 rows", ["--rows-per-strip", "7"], 32), ("strips of 8 rows", ["--rows-per-strip", "8"], 32),
      ("Deflate", ["--compression", "deflate", "--rows-per-strip", "9"], 32), ("Deflate, floating point predictor", ["--compression", "deflate", "--predictor", "3", "--rows-per-strip", "16"], 32),
      ("Deflate, floating point predictor, one strip", ["--compression", "deflate", "--predictor", "3"], 32),
      ("big-endian", ["--be"], 32), ("big-endian, Deflate, predictor", ["--be", "--compression", "deflate", "--predictor", "3", "--rows-per-strip", "10"], 32),
      ("BigTIFF", ["--big"], 32), ("BigTIFF, big-endian, Deflate", ["--big", "--be", "--compression", "deflate", "--rows-per-strip", "13"], 32),
      ("tiles of 32", ["--tiles", "32"], 32), ("tiles of 32, Deflate, predictor", ["--tiles", "32", "--compression", "deflate", "--predictor", "3"], 32),
      ("tiles of 16, big-endian", ["--tiles", "16", "--be"], 32),
      ("16 bit", [], 16), ("16 bit, Deflate, horizontal predictor", ["--compression", "deflate", "--predictor", "2", "--rows-per-strip", "11"], 16), ("16 bit, big-endian", ["--be"], 16),
      ("8 bit", [], 8), ("8 bit, Deflate, horizontal predictor", ["--compression", "deflate", "--predictor", "2"], 8),
    };
    foreach (var (name, args, bits) in layouts)
    {
      var path = Path.Combine(dir, name.Replace(' ', '_').Replace(',', '_') + ".tif");
      Py(["mktiff", path, raw, n.ToString(), "--bits", bits.ToString(), .. args]);
      try
      {
        using var src = HeightSource.Open(path);
        var row = new float[n];
        double worst = 0;
        // Any order: the rows backwards, then forwards.
        for (int pass = 0; pass < 2; pass++)
          for (int k = 0; k < n; k++)
          {
            int r = pass == 0 ? n - 1 - k : k;
            src.ReadRow(r, row);
            for (int x = 0; x < n; x++)
              worst = Math.Max(worst, Math.Abs(row[x] - values[r * n + x]));
          }
        double tolerance = bits == 32 ? 0 : bits == 16 ? 0.5 / 65535 + 1e-7 : 0.5 / 255 + 1e-7;
        C(src.Width == n && src.Height == n && worst <= tolerance, $"{name}: {n} x {n}, {src.Kind}, {(bits == 32 ? "every float exact" : $"within a step ({worst:g3})")}");
      }
      catch (EditorReadException e)
      {
        C(false, $"{name}: {e.Message}");
      }
    }
    // R32 and raw 16-bit.
    var r32 = Path.Combine(dir, "values.r32");
    File.Copy(raw, r32, true);
    using (var src = HeightSource.Open(r32))
    {
      var row = new float[n];
      src.ReadRow(5, row);
      C(src.Width == n && row.SequenceEqual(values.Skip(5 * n).Take(n)), "R32: the side is the square root of the length, the floats exact");
    }
    var r16 = Path.Combine(dir, "values.raw");
    var u16 = new byte[n * n * 2];
    for (int i = 0; i < values.Length; i++) { ushort u = (ushort)Math.Round(values[i] * 65535.0); u16[2 * i] = (byte)u; u16[2 * i + 1] = (byte)(u >> 8); }
    File.WriteAllBytes(r16, u16);
    using (var src = HeightSource.Open(r16))
    {
      var row = new float[n];
      src.ReadRow(7, row);
      C(src.Width == n && Enumerable.Range(0, n).All(x => Math.Abs(row[x] - values[7 * n + x]) < 1e-5), "raw 16-bit unsigned: little endian, the side from the length");
    }
    var png = Path.Combine(dir, "values.png");
    File.WriteAllBytes(png, PngWriter.Write(n, n, 0, 16, (y, rw) => { for (int x = 0; x < n; x++) { ushort u = (ushort)Math.Round(values[y * n + x] * 65535.0); rw[2 * x] = (byte)(u >> 8); rw[2 * x + 1] = (byte)u; } }));
    using (var src = HeightSource.Open(png))
    {
      var row = new float[n];
      src.ReadRow(9, row);
      C(src.Width == n && Enumerable.Range(0, n).All(x => Math.Abs(row[x] - values[9 * n + x]) < 1e-5), "16-bit PNG: file row 0 on top");
    }
    var odd = Path.Combine(dir, "odd.r32");
    File.WriteAllBytes(odd, new byte[4 * 70 * 70 + 8]);
    C(Throws(() => HeightSource.Open(odd), "square"), "an .r32 that is not a square number of floats is refused");
    File.WriteAllBytes(odd, new byte[4 * 70 * 70 + 1]);
    C(Throws(() => HeightSource.Open(odd), "whole number"), "an .r32 with a stray byte is refused");
    File.WriteAllBytes(odd, new byte[4 * 20 * 20]);
    using (var sized = HeightSource.Open(odd, 20))
      C(sized.Width == 20, "size= is checked against the length, and agrees");
    C(Throws(() => HeightSource.Open(odd, 21), "not 21 x 21"), "size= that disagrees with the length is refused");
  }

  static bool Throws(Func<IDisposable> a, string part)
  {
    try
    {
      a().Dispose();
      return false;
    }
    catch (EditorReadException e)
    {
      return e.Message.Contains(part);
    }
  }

  static void EditorErrors()
  {
    Section("editor return: files that are not what they should be");
    const int n = 64;
    var dir = Path.Combine(work, "editor-hostile");
    Directory.CreateDirectory(dir);
    var values = new float[n * n];
    for (int i = 0; i < values.Length; i++) values[i] = i / (float)values.Length;
    var good = Path.Combine(dir, "good.tif");
    WriteTiff(good, n, values);
    var goodBytes = File.ReadAllBytes(good);
    string Variant(string name, Action<byte[]> edit, int? truncate = null)
    {
      var b = (byte[])goodBytes.Clone();
      edit(b);
      var path = Path.Combine(dir, name + ".tif");
      File.WriteAllBytes(path, truncate is { } t ? b.Take(t).ToArray() : b);
      return path;
    }
    // Entry positions: the classic header puts the IFD at 8; entry k starts at 10 + 12 k (tag, type, count, value).
    int Entry(int tag) => 10 + 12 * new[] { 256, 257, 258, 259, 262, 270, 273, 274, 277, 278, 279, 284, 305, 339 }.ToList().IndexOf(tag);
    void Put32(byte[] b, int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
    foreach (var (name, path, part) in new (string, string, string)[]
    {
      ("not a TIFF", Variant("nottiff", b => { b[0] = 1; b[1] = 2; }), "not a TIFF"),
      ("short", Variant("short", b => { }, 12), "too short"),
      ("directory past the end", Variant("ifdpast", b => Put32(b, 4, 100000)), "outside the file"),
      ("strip offset past the end", Variant("stripoff", b => Put32(b, Entry(273) + 8, 100000000)), "past the end"),
      ("strip count past the end", Variant("stripcount", b => Put32(b, Entry(279) + 8, 100000000)), "past the end"),
      ("zero width", Variant("zerow", b => Put32(b, Entry(256) + 8, 0)), "pixels"),
      ("huge width", Variant("hugew", b => { Put32(b, Entry(256) + 8, 4000000000u); Put32(b, Entry(257) + 8, 4000000000u); }), "up to 16384"),
      ("not square", Variant("nonsq", b => Put32(b, Entry(257) + 8, 60)), "square"),
      ("three samples", Variant("rgb", b => b[Entry(277) + 8] = 3), "3 samples"),
      ("LZW", Variant("lzw", b => b[Entry(259) + 8] = 5), "LZW"),
      ("JPEG", Variant("jpeg", b => b[Entry(259) + 8] = 7), "JPEG"),
      ("64-bit samples", Variant("bits64", b => b[Entry(258) + 8] = 64), "64-bit"),
      ("planar", Variant("planar", b => b[Entry(284) + 8] = 2), "planes"),
      ("truncated pixels", Variant("trunc", b => { }, goodBytes.Length - 400), "past the end"),
      ("absurd tag count", Variant("tagcount", b => Put32(b, Entry(270) + 4, 0x7FFFFFFF)), ""),
    })
    {
      string? message = null;
      try
      {
        using var src = HeightSource.Open(path);
        var row = new float[src.Width];
        for (int r = 0; r < src.Height; r++)
          src.ReadRow(r, row);
        message = "opened and read";
      }
      catch (EditorReadException e)
      {
        message = e.Message;
      }
      catch (Exception e)
      {
        message = "CRASH " + e.GetType().Name + ": " + e.Message;
      }
      bool ok = name == "absurd tag count" ? !message!.StartsWith("CRASH") : !message!.StartsWith("CRASH") && message != "opened and read" && message.Contains(part);
      C(ok, $"{name}: {(message!.Length > 100 ? message.Substring(0, 100) : message)}");
    }
    // EXR and unknown things.
    var exr = Path.Combine(dir, "x.exr");
    File.WriteAllBytes(exr, [0x76, 0x2f, 0x31, 0x01, 2, 0, 0, 0]);
    C(Throws(() => HeightSource.Open(exr), "EXR"), "EXR: a clear message (not read yet)");
    var txt = Path.Combine(dir, "x.txt");
    File.WriteAllText(txt, "hello");
    C(Throws(() => HeightSource.Open(txt), "cannot tell what"), "an unknown extension is refused");
    // Through the ingest: errors come back as messages, and nothing is left behind.
    var p = MakePack("HostileWorld", "export-2026-10-26-15-00-00", n, values);
    var empty = Ingest(p);
    C(empty.Error != null && empty.Error.Contains("holds no height file") && empty.OutDir == null, "an empty editor/return/ says to save the height there");
    File.Copy(good, Path.Combine(p.Dir, "editor", "return", "a.tif"));
    File.Copy(good, Path.Combine(p.Dir, "editor", "return", "b.tif"));
    var two = Ingest(p);
    C(two.Error != null && two.Error.Contains("several files") && two.Error.Contains("files="), "two files and neither is height.tif: 'name the one with files='");
    var named = Ingest(p, "files=" + Path.Combine(p.Dir, "editor", "return", "b.tif"));
    C(named.Error == null, "files=<path> names one");
    var broken = Ingest(p, "files=" + Path.Combine(dir, "lzw.tif"));
    C(broken.Error != null && broken.Error.Contains("LZW") && broken.OutDir == null, "a file that cannot be read: the message, and no folder made");
    var noManifest = Path.Combine(root, "HostileWorld", "export-2026-10-26-15-10-00");
    Directory.CreateDirectory(Path.Combine(noManifest, "editor", "return"));
    var nm = EditorIngest.Run(noManifest, new IngestOptions(), line => { });
    C(nm.Error != null && nm.Error.Contains("no manifest.json"), "an export without a manifest.json: not finished (" + nm.Error + ")");
    var ne = MakePack("HostileWorld", "export-2026-10-26-15-20-00", n, values, withEditorJson: false);
    WriteTiff(Path.Combine(ne.Dir, "editor", "return", "height.tif"), n, values);
    var noJson = Ingest(ne);
    C(noJson.Error != null && noJson.Error.Contains("editor.json") && noJson.Error.Contains("unit=metres"), "without editor.json unit=v has no meaning: it says what to give");
    var metresNoJson = Ingest(ne, "unit=metres");
    C(metresNoJson.Error == null, "unit=metres needs no editor.json");
    var tooBig = MakePack("HostileWorld", "export-2026-10-26-15-30-00", n, values);
    var bigTiff = (byte[])goodBytes.Clone();
    Put32(bigTiff, Entry(256) + 8, 20000); Put32(bigTiff, Entry(257) + 8, 20000);
    File.WriteAllBytes(Path.Combine(tooBig.Dir, "editor", "return", "height.tif"), bigTiff);
    var tb = Ingest(tooBig);
    C(tb.Error != null && tb.Error.Contains("16384"), "a picture past 16384 px is refused with the limit: " + tb.Error);
    // NaN pixels are restored from the export.
    var nan = (float[])values.Clone();
    nan[32 * n + 32] = float.NaN; nan[30 * n + 40] = float.PositiveInfinity;
    var np = MakePack("HostileWorld", "export-2026-10-26-15-40-00", n, values);
    WriteTiff(Path.Combine(np.Dir, "editor", "return", "height.tif"), n, nan);
    var restored = Ingest(np, "orient=none");
    C(restored.Error == null && restored.Restored == 2, $"pixels that are not numbers are restored and counted ({restored.Restored})");
  }

  static void EditorCommandLine()
  {
    Section("editor return: bc_import edited");
    C(IngestOptions.TryParse("export-2026-10-26-12-00-00 unit=metres orient=cw90 amount=auto sealevel=0.4 fine=4 ering=gaea size=64 preset files=\"C:\\a b\\h.tif\"", out var o, out var rest, out var error)
      && rest == "export-2026-10-26-12-00-00" && o.Unit == EditorUnit.Metres && o.UnitGiven && o.Orient == EditorOrient.Cw90 && o.AmountAuto && o.SeaLevel == 0.4f && o.FineBits == 4 && !o.RingKeep && o.RawSize == 64 && o.Preset
      && o.Files == "C:\\a b\\h.tif", "every option parses; the rest is the export (" + error + ")");
    C(IngestOptions.TryParse("My World/export 1 range=-10,300 amount=3 sealevel=auto orient=auto", out o, out rest, out error) && rest == "My World/export 1" && o.Unit == EditorUnit.Range && o.RangeLo == -10 && o.RangeHi == 300 && o.Amount == 3f && o.SeaAuto && o.Orient == null,
      "a folder with spaces needs no quotes");
    foreach (var bad in new[] { "unit=feet", "orient=sideways", "amount=0", "amount=500", "sealevel=2", "range=1", "range=5,5", "fine=3", "ering=maybe", "size=0" })
      C(!IngestOptions.TryParse(bad, out _, out _, out error) && error.StartsWith("cannot read"), $"'{bad}' is refused ({error})");
    var said = new List<string>();
    C(EditorIngest.TryRunLine("edited help", said.Add) && said.Any(l => l.Contains("heightmap-fine.png")) && said.Any(l => l.Contains("orient=auto")), "bc_import edited help");
    said.Clear();
    C(EditorIngest.TryRunLine("edited cancel", said.Add) && said.Any(l => l.Contains("nothing is running")), "bc_import edited cancel with nothing running");
    said.Clear();
    C(EditorIngest.TryRunLine("edited no-such-export", said.Add) && said.Any(l => l.Contains("no folder or export matches")), "an export that does not exist: " + said.FirstOrDefault());
    C(!EditorIngest.TryRunLine("2", said.Add) && !EditorIngest.TryRunLine("edit", said.Add) && !EditorIngest.TryRunLine("editedx", said.Add) && !EditorIngest.TryRunLine("My World", said.Add), "other bc_import words are not taken");
    said.Clear();
    var p = MakePack("CmdWorld", "export-2026-10-26-16-00-00", 64, Ground(64));
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), 64, Ground(64));
    // Resolves the export by its name; Start itself needs the game (instance), which the offline run has not.
    C(EditorIngest.TryRunLine("edited CmdWorld/export-2026-10-26-16-00-00 unit=v", said.Add) && said.Any(l => l.Contains("the folder you named") || l.Contains("export folder of that name")) && said.Any(l => l.Contains("not initialised")),
      "bc_import edited <export> resolves the export like bc_import does (" + string.Join(" | ", said) + ")");
    var help = new List<string>();
    WorldImportCommands.Help(help.Add);
    C(help.Any(l => l.Contains("bc_import edited")), "bc_import help lists it");
    C(EditorIngest.StatusLines().Count == 0 || EditorIngest.StatusLines().All(l => l.StartsWith("Last editor return") || l.StartsWith("Editor return")), "bc_import status has lines for it");
  }

  static void EditorCancel()
  {
    Section("editor return: a cancel");
    const int n = 256;
    var p = MakePack("CancelWorld", "export-2026-10-26-17-00-00", n, Ground(n));
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), n, Ground(n));
    int polls = 0;
    EditorIngest.BandRowsOverride = 8;
    try
    {
      var r = Ingest(p, "orient=none", () => ++polls > 20);
      C(r.Error == "cancelled" && r.OutDir == null && !Directory.Exists(Path.Combine(Path.GetDirectoryName(p.Dir)!, p.Name + "-edited")), $"a cancel in the middle of the heights leaves no folder ({polls} polls)");
      polls = 0;
      var early = Ingest(p, "", () => ++polls > 2);
      C(early.Error == "cancelled" && early.OutDir == null && !Directory.Exists(Path.Combine(Path.GetDirectoryName(p.Dir)!, p.Name + "-edited")), "and one while it looks at the picture leaves none either");
    }
    finally
    {
      EditorIngest.BandRowsOverride = 0;
    }
  }

  // BCIMPORT_ONLY=editor BCIMPORT_BIG=<size>: an editor return at that size, streamed, with the process's memory and the time measured.
  static long ProcStatus(string key)
  {
    try
    {
      foreach (var line in File.ReadAllLines("/proc/self/status"))
        if (line.StartsWith(key + ":")) return long.Parse(line.Substring(key.Length + 1).Trim().Split(' ')[0]);
    }
    catch
    {
      // No /proc: no numbers.
    }
    return 0;
  }

  static void BigEditorIngest(int n)
  {
    Section($"editor return at {n} x {n} px: time and memory");
    var dir = Path.Combine(root, "BigEdit", "export-2026-10-26-19-00-00");
    Directory.CreateDirectory(Path.Combine(dir, "editor", "return"));
    double Plane(int r, int c) => 0.2 + 0.0015 * c / (n - 1) + 0.001 * r / (n - 1);
    double Bump(int r, int c)
    {
      double y = r / (double)(n - 1), x = c / (double)(n - 1);
      double dy = y - 0.2, dx = x - 0.7;
      return dx * dx + dy * dy > 0.04 ? 0.0 : 0.05 * Math.Exp(-(dx * dx + dy * dy) / 0.004);
    }
    var record = new HeightmapRecord(2f, 0.5f);
    var clock = Stopwatch.StartNew();
    ushort Grey(int r, int c)
    {
      float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(r, n, TotalM);
      return Mathf.Sqrt(wx * wx + wz * wz) > 10000f ? (ushort)(3000 + (c * 7 + r * 3) % 500) : (ushort)Math.Round((Plane(r, c) + Bump(r, c)) * 65535.0);
    }
    using (var png = new PngRowWriter(Path.Combine(dir, "heightmap.png"), n, n, 1, 16, HeightmapRecord.Keyword, record.Text))
    {
      var band = new byte[2 * n * 32];
      for (int r0 = 0; r0 < n; r0 += 32)
      {
        int rows = Math.Min(32, n - r0);
        for (int i = 0; i < rows; i++)
          for (int c = 0; c < n; c++)
          {
            var g = Grey(r0 + i, c);
            band[2 * (i * n + c)] = (byte)(g >> 8);
            band[2 * (i * n + c) + 1] = (byte)g;
          }
        png.WriteRows(band, 0, rows);
      }
      png.Complete();
    }
    void WriteField(string path, Func<int, int, double> field)
    {
      using var tif = new TiffRowWriter(path, n, n, 32, "BetterContinents: test", "test");
      var floats = new float[n * 32];
      var bytes = new byte[n * 32 * 4];
      for (int r0 = 0; r0 < n; r0 += 32)
      {
        int rows = Math.Min(32, n - r0);
        for (int i = 0; i < rows; i++)
          for (int c = 0; c < n; c++)
            floats[i * n + c] = (float)field(r0 + i, c);
        Buffer.BlockCopy(floats, 0, bytes, 0, rows * n * 4);
        tif.WriteRows(bytes, 0, rows);
      }
      tif.Complete();
    }
    WriteField(Path.Combine(dir, "editor", "height.tif"), (r, c) => Plane(r, c) + Bump(r, c));
    // What the editor did: a second hill.
    WriteField(Path.Combine(dir, "editor", "return", "height.tif"), (r, c) =>
    {
      double y = r / (double)(n - 1), x = c / (double)(n - 1);
      return Plane(r, c) + Bump(r, c) + (Math.Abs(x - 0.3) < 0.1 && Math.Abs(y - 0.6) < 0.1 ? 0.04 * Math.Exp(-((x - 0.3) * (x - 0.3) + (y - 0.6) * (y - 0.6)) / 0.003) : 0.0);
    });
    File.WriteAllText(Path.Combine(dir, "manifest.json"),
      "{\n  \"format\": \"bc-export/1\",\n  \"worldName\": \"BigEdit\",\n  \"exportedAt\": \"2026-10-26T19:00:00\",\n  \"size\": " + n + ",\n  \"totalSize\": 21000,\n  \"worldRadius\": 10000,\n  \"totalRadius\": 10500,\n"
      + "  \"heightmapAmount\": 2,\n  \"seaLevel\": 0.5,\n  \"seaLevelAdjustment\": 0,\n  \"edgeDropoff\": true,\n  \"files\": [\"heightmap.png\", \"manifest.json\"],\n  \"notes\": []\n}\n");
    File.WriteAllText(Path.Combine(dir, "editor", "editor.json"),
      "{\n  \"format\": \"bc-editor/1\",\n  \"grid\": {\"size\": " + n + "},\n  \"height\": {\"amount\": 2, \"seaLevel\": 0.5, \"seaLevelAdjustment\": 0, \"valueRange\": [0.2, 0.26]}\n}\n");
    System.Console.WriteLine($"    made the export and the return in {clock.Elapsed.TotalSeconds:0.0} s; RSS {ProcStatus("VmRSS") / 1024} MB, peak so far {ProcStatus("VmHWM") / 1024} MB");
    long before = ProcStatus("VmRSS"), peak = 0;
    bool stop = false;
    var gate = new object();
    var sampler = new Thread(() =>
    {
      while (!Volatile.Read(ref stop))
      {
        var r = ProcStatus("VmRSS");
        lock (gate) if (r > peak) peak = r;
        Thread.Sleep(100);
      }
    }) { IsBackground = true };
    sampler.Start();
    var phases = new List<string>();
    clock.Restart();
    IngestOptions.TryParse(Environment.GetEnvironmentVariable("BCIMPORT_EDITOR_ARGS") ?? "", out var options, out _, out _);
    var res = EditorIngest.Run(dir, options, line => { phases.Add(line); System.Console.WriteLine("    " + line); }, null, null);
    var seconds = clock.Elapsed.TotalSeconds;
    Volatile.Write(ref stop, true);
    sampler.Join();
    System.Console.WriteLine($"    MEASURED editor return {n}: {seconds:0.0} s; RSS {before / 1024} MB before, peak (sampled) {peak / 1024} MB, VmHWM {ProcStatus("VmHWM") / 1024} MB; managed {GC.GetTotalMemory(false) / 1048576} MB");
    C(res.Error == null && res.OutDir != null, $"the {n} px return is brought back ({res.Error})");
    if (res.OutDir == null)
      return;
    foreach (var f in Directory.GetFiles(res.OutDir).OrderBy(f => f))
      System.Console.WriteLine($"    {Path.GetFileName(f),-20} {new FileInfo(f).Length / 1048576.0,9:0.0} MB");
    // Rows of the result, read a few at a time, against the editor's field.
    var heightBytes = File.ReadAllBytes(Path.Combine(res.OutDir, "heightmap.png"));
    var fineBytes = File.Exists(Path.Combine(res.OutDir, "heightmap-fine.png")) ? File.ReadAllBytes(Path.Combine(res.OutDir, "heightmap-fine.png")) : null;
    using var hp = PngRows<L16>.TryOpen(heightBytes);
    using var fp = fineBytes != null ? PngRows<L8>.TryOpen(fineBytes) : null;
    C(hp != null && hp.Width == n, "the new heightmap.png is read row by row");
    double worst = 0;
    int checkedPixels = 0;
    foreach (var fileRow in new[] { n / 5, n / 3, (int)(n * 0.6) - 2, (int)(n * 0.6), n / 2 })
    {
      var grey = new ushort[n];
      var fine = new sbyte[n];
      hp!.ReadRows(n - 1 - fileRow, 1, (_, px) => { for (int x = 0; x < n; x++) grey[x] = px[x].PackedValue; });
      fp?.ReadRows(n - 1 - fileRow, 1, (_, px) => { for (int x = 0; x < n; x++) fine[x] = (sbyte)px[x].PackedValue; });
      for (int c = 0; c < n; c += 3)
      {
        float wx = WorldExportMath.PixelToWorld(c, n, TotalM), wz = WorldExportMath.FileRowToWorldZ(fileRow, n, TotalM);
        if (Mathf.Sqrt(wx * wx + wz * wz) > 9900f) continue;
        double y = fileRow / (double)(n - 1), x = c / (double)(n - 1);
        double v = Plane(fileRow, c) + Bump(fileRow, c) + (Math.Abs(x - 0.3) < 0.1 && Math.Abs(y - 0.6) < 0.1 ? 0.04 * Math.Exp(-((x - 0.3) * (x - 0.3) + (y - 0.6) * (y - 0.6)) / 0.003) : 0.0);
        v = (float)v;
        double want = (v * 2.0 - WorldExportMath.BaseOffset) * 200.0;
        double got = (((256.0 * grey[c] + fine[c]) / 16776960.0) * 2.0 - WorldExportMath.BaseOffset) * 200.0;
        worst = Math.Max(worst, Math.Abs(got - want));
        checkedPixels++;
      }
    }
    C(worst < 3.06e-3, $"{checkedPixels} pixels of 5 rows are the editor's heights (worst {worst * 1000:0.###} mm)");
    try { Directory.Delete(Path.Combine(root, "BigEdit"), true); } catch { }
  }

  static void EditorSizes()
  {
    Section("editor return: another size than the export, and mixed sizes");
    const int n = 128, m = 96;
    var v0 = Ground(n);
    var p = MakePack("SizeWorld", "export-2026-10-26-18-00-00", n, v0);
    var small = Ground(m);
    WriteTiff(Path.Combine(p.Dir, "editor", "return", "height.tif"), m, small);
    var r = Ingest(p);
    C(r.Error == null && r.Size == m && r.SourceSize == n, $"a {m} px return for a {n} px export is taken ({r.Error})");
    var (grey, fine, rec, size) = ReadPair(r.OutDir!);
    C(size == m && rec != null && File.Exists(Path.Combine(r.OutDir!, "locationmap.png")), "the heightmap is the returned size and the other maps are copied");
    var acc = Accuracy(r.OutDir!, small, m, 2, 0, 2, 0);
    C(acc.Worst < 3.06e-3, $"and its heights are the returned ones (worst {acc.Worst * 1000:0.###} mm)");
    // The real loaders on a folder of mixed sizes.
    var mixed = Path.Combine(root, "SizeWorld", "mixed");
    Directory.CreateDirectory(mixed);
    foreach (var f in new[] { "heightmap.png", "heightmap-fine.png", "forestmap.png", "manifest.json" })
      File.Copy(Path.Combine(r.OutDir!, f), Path.Combine(mixed, f));
    File.WriteAllLines(Path.Combine(mixed, "export.cfg"), File.ReadAllLines(Path.Combine(r.OutDir!, "export.cfg")).Select(l => l.StartsWith(SettingsSchema.Directory.Key + " = ") ? SettingsSchema.Directory.Key + " = " + mixed.Replace('\\', '/') : l));
    lock (LogHandler.Lines) LogHandler.Lines.Clear();
    var made = BC.BetterContinentsSettings.CreateForImport(WorldImport.MakePlan(mixed).Values, lean: false);
    var heights = Map<ImageMapFloat>(made, "heightmap.png");
    var forest = Map<ImageMapFloat>(made, "forestmap.png");
    double worst = 0;
    for (int k = 0; k < 300; k++)
    {
      int row = 10 + k * 13 % (m - 20), col = 10 + k * 29 % (m - 20);
      float wx = WorldExportMath.PixelToWorld(col, m, TotalM), wz = WorldExportMath.FileRowToWorldZ(row, m, TotalM);
      if (Mathf.Sqrt(wx * wx + wz * wz) > 9800f) continue;
      worst = Math.Max(worst, Math.Abs(WorldExportMath.ValueToMetres(heights.GetValue(wx / TotalM + 0.5f, wz / TotalM + 0.5f), made.HeightmapAmount, made.SeaLevel) - (small[row * m + col] * 2.0 - WorldExportMath.BaseOffset) * 200.0));
    }
    C(made.HasHeightMap && heights != null && forest != null && worst < 0.05, $"a world made from a {m} px heightmap beside a {n} px forest map loads both and reads the heights (worst {worst * 1000:0.#} mm)");
  }
}

internal static class EditorPyRunner
{
  public static string Run(string[] args)
  {
    var dir = AppContext.BaseDirectory;
    string? script = null;
    for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
      if (File.Exists(Path.Combine(dir, "editor_ref.py"))) { script = Path.Combine(dir, "editor_ref.py"); break; }
    if (script == null)
      throw new FileNotFoundException("tools/editor_ref.py");
    var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add("-I");
    psi.ArgumentList.Add(script);
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
}

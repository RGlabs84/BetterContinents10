// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

// The fine heights of the export (heightmap-fine.png, format 1 of tools/fine_ref.py): the format's arithmetic; the policy that keeps
// the extra bits off water and steep ground, whatever size the bands are cut in; and whole exports of a made-up world, read back
// pixel for pixel against an independent copy of the writer rule: the files, the record at the end of the fine file and its CRC,
// the 4-bit bytes, heightmap.png the same with and without a fine file, the manifest and the README, and a cancel.
internal static class FineTest
{
  static void C(bool ok, string what) => Program.C(ok, what);
  static void Section(string s) => System.Console.WriteLine("== " + s);

  const float T = 21000f;

  public static void Run(string work)
  {
    Arithmetic();
    Policy();
    Exports(work);
  }

  // ---- the arithmetic ----------------------------------------------------------------------------------------------

  static void Arithmetic()
  {
    Section("fine heights: the writer rule");
    // The worked example of fine_ref.py: 123.456 m at Heightmap Amount 81, Sea Level 0.5 (adjustment 0) is c 621 and f -55
    // (byte 201), which reads 123.4557 m; 4 bits give f -48 (byte 208), 123.4625 m.
    float fh = 123.456f / 200f;
    double x = M.FineX(fh, 0f, 81f);
    int c = M.ValueToUShort((fh + 0.15f) / 81f, out _);
    double Metres(int grey, int f) => ((256.0 * grey + f) / 16776960.0 * 81.0 - 0.15) * 200.0;
    C(c == 621 && M.FineOffset(x, c, 8) == -55 && (byte)M.FineOffset(x, c, 8) == 201 && Math.Abs(Metres(c, -55) - 123.4557) < 2e-4,
      $"123.456 m at Heightmap Amount 81: grey 621, f -55 (byte 201), {Metres(c, -55):0.0000} m (x = {x:0.0000})");
    C(M.FineOffset(x, c, 4) == -48 && (byte)M.FineOffset(x, c, 4) == 208 && Math.Abs(Metres(c, -48) - 123.4625) < 2e-4, $"with 4 bits f -48 (byte 208), {Metres(c, -48):0.0000} m");
    C(M.FineOffset(x, c, 0) == 0 && M.FineOffset(x, c, 9) == 0, "no bits, no byte");

    // Where the height is a hair from the half step: ties go to even, and the ends of the byte are kept.
    C(M.FineOffset(100.5, 100, 8) == 127 && M.FineOffset(99.5, 100, 8) == -128 && M.FineOffset(100.5, 100, 4) == 112 && M.FineOffset(99.5, 100, 4) == -128,
      "half a step either way: 127 and -128 with 8 bits, 112 and -128 with 4");
    C(M.FineOffset(100 + 1.5 / 256, 100, 8) == 2 && M.FineOffset(100 + 2.5 / 256, 100, 8) == 2 && M.FineOffset(100 + 0.5 / 256, 100, 8) == 0 && M.FineOffset(100 - 1.5 / 256, 100, 8) == -2,
      "half a unit rounds to the even number, as numpy's rint does in fine_ref.py");
    C(M.FineOffset(100 + 8.0 / 256, 100, 4) == 0 && M.FineOffset(100 + 8.01 / 256, 100, 4) == 16 && M.FineOffset(100 + 24.0 / 256, 100, 4) == 32 && M.FineOffset(100 - 8.01 / 256, 100, 4) == -16,
      "with 4 bits the bytes go in steps of 16, ties to even");

    // Heights at the ends of the scale and past them.
    float sla = 0f;
    C(M.FineX(-5f, sla, 81f) == 0.0 && M.FineX(500f, sla, 81f) == 65535.0 && M.FineX(float.NaN, sla, 81f) == 0.0 && M.FineX(0.5f, sla, 81f) > 0.0 && M.FineX(0.5f, sla, 81f) < 65535.0,
      "FineX is clamped to 0..65535 steps (and NaN is 0)");
    C(M.FineUnits(0.0) == 0 && M.FineUnits(65535.0) == 16776960 && M.FineUnits(1.0 / 512) == 0 && M.FineUnits(3.0 / 512) == 2 && M.FineUnits(5.0 / 512) == 2, "FineUnits is 256 to a step, rounded half to even");

    // A hundred thousand heights: the grey value of the plain writer, the byte within half a unit of the height (a unit and
    // a half at the top of the range, where the byte runs out), and 4 bits within 8 units (16 at the top), multiples of 16.
    var rng = new System.Random(7);
    int bad8 = 0, bad4 = 0, nibble = 0, over = 0;
    double worst8 = 0, worst4 = 0;
    for (int k = 0; k < 100000 + 4; k++)
    {
      double xx = k < 4 ? new[] { 0.0, 65535.0, 0.5, 65534.5 }[k] : rng.NextDouble() * 65535.0;
      int grey = (int)Math.Floor(xx + 0.5);
      foreach (var bits in new[] { 8, 4 })
      {
        int f = M.FineOffset(xx, grey, bits);
        double err = Math.Abs(256.0 * grey + f - xx * 256.0);
        double near = bits == 8 ? 0.5 : 8.0, far = bits == 8 ? 1.0 : 16.0;
        bool top = (xx - grey) * 256.0 > (bits == 8 ? 127.5 : 120.0);
        if (bits == 4 && f % 16 != 0) nibble++;
        if (f < -128 || f > (bits == 8 ? 127 : 112)) over++;
        if (err > (top ? far : near) + 1e-6)
        {
          if (bits == 8) bad8++; else bad4++;
        }
        if (bits == 8) worst8 = Math.Max(worst8, err); else worst4 = Math.Max(worst4, err);
      }
    }
    C(bad8 == 0 && bad4 == 0 && nibble == 0 && over == 0, $"100,000 heights: 8 bits within 1 unit (worst {worst8:0.000}), 4 bits within 16 (worst {worst4:0.00}), every 4-bit byte a multiple of 16, none out of range");
    C(M.FineBitsUsed(0) == 0 && M.FineBitsUsed(16) == 4 && M.FineBitsUsed(48) == 4 && M.FineBitsUsed(128) == 1 && M.FineBitsUsed(255) == 8 && M.FineBitsUsed(8 | 64) == 5 && M.FineBitsUsed(256) == 0,
      "the bits the bytes use: 8 less the trailing zeros, 0 for none");
    C(M.LengthText(0.24719922) == "24.7 cm" && M.LengthText(0.015449951) == "1.5 cm" && M.LengthText(0.00096562) == "0.97 mm" && M.LengthText(0.0061) == "6.1 mm" && M.LengthText(1.25) == "1.25 m" && M.LengthText(0.01) == "1 cm" && M.LengthText(0.0099) == "9.9 mm",
      "lengths read as in the reader's log: 24.7 cm, 1.5 cm, 0.97 mm, 6.1 mm, 1.25 m");
    C(M.FineRecordText(4, 0x89ABCDEF) == "Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 89ABCDEF" && M.FineRecordText(8, 0x1A) == "Fine Format = 1; Fine Bits = 8; Heightmap CRC-32 = 0000001A",
      "the record's text: format 1, the bits, the CRC-32 as 8 hex digits in upper case");
  }

  // ---- the policy, over bands ----------------------------------------------------------------------------------------

  // The writer rule and the policy of fine_ref.py on a whole picture at once, written the plain way (no bands): x in grey steps, the
  // grey value written c, wet pixels. f = the rule; 0 under water; 0 where the true heights (x * 256, rounded) differ by 256 or more
  // from any of the four neighbours.
  static byte[] Reference(double[] x, ushort[] c, bool[] wet, int n, int bits)
  {
    var f = new byte[n * n];
    var units = x.Select(v => (long)Math.Round(v * 256.0, MidpointRounding.ToEven)).ToArray();
    int q = 1 << (8 - bits);
    for (int y = 0; y < n; y++)
      for (int col = 0; col < n; col++)
      {
        int i = y * n + col;
        if (wet[i])
          continue;
        long steepest = 0;
        if (col > 0) steepest = Math.Max(steepest, Math.Abs(units[i] - units[i - 1]));
        if (col < n - 1) steepest = Math.Max(steepest, Math.Abs(units[i] - units[i + 1]));
        if (y > 0) steepest = Math.Max(steepest, Math.Abs(units[i] - units[i - n]));
        if (y < n - 1) steepest = Math.Max(steepest, Math.Abs(units[i] - units[i + n]));
        if (steepest >= 256)
          continue;
        double v = Math.Round((x[i] - c[i]) * (256.0 / q)) * q;
        f[i] = (byte)(sbyte)Math.Clamp(v, -128.0, 128.0 - q);
      }
    return f;
  }

  // What the terrain pass does with bands: every row of a band and one more below it go through Pixel, then Finish, and the
  // band's rows are what is written.
  static byte[] ThroughBands(double[] x, ushort[] c, bool[] wet, int n, int bits, int bandRows, int workers)
  {
    var bands = new WorldExport.FineBands(n, bits, bandRows, workers);
    var result = new byte[n * n];
    var buffer = new byte[(bandRows + 1) * n];
    for (int first = 0; first < n; first += bandRows)
    {
      int count = Math.Min(bandRows, n - first), extra = Math.Min(1, n - first - count);
      Array.Fill(buffer, (byte)0xAA);
      for (int r = 0; r < count + extra; r++)
        for (int col = 0; col < n; col++)
          bands.Pixel(buffer, r, col, x[(first + r) * n + col], c[(first + r) * n + col], wet[(first + r) * n + col]);
      bands.Finish(buffer, first, count, extra);
      Buffer.BlockCopy(buffer, 0, result, first * n, count * n);
    }
    Last = bands;
    return result;
  }

  static WorldExport.FineBands Last;

  static void Policy()
  {
    Section("fine heights: the policy (under water, steep ground), whatever the bands");
    // A made-up landscape in grey steps: gentle slopes with a ripple of a few units, a cliff between two rows and a column, a
    // steep ramp, plateaus cut off at both ends of the scale, a flooded corner, and noise on one block (steep by itself).
    const int n = 97;
    var x = new double[n * n];
    var wet = new bool[n * n];
    var rng = new System.Random(3);
    for (int y = 0; y < n; y++)
      for (int col = 0; col < n; col++)
      {
        double v = 20000 + 0.3 * col + 0.2 * y + 0.4 * Math.Sin(col * 0.5) * Math.Cos(y * 0.4) + rng.NextDouble() * 0.02;
        if (y >= 50) v += 90;                                  // a cliff between rows 49 and 50 (a band boundary for several sizes)
        if (col >= 70) v += 4 * (col - 69) + 6;                // a ramp from column 70: 4 steps a pixel
        if (y < 6) v = 65535;                                  // a plateau cut off at the top
        if (y >= 90 && col < 20) v = 0;                        // and at the bottom
        if (y >= 20 && y < 30 && col >= 10 && col < 20) v += rng.NextDouble() * 6;   // noise: most of it a step or more from its neighbours
        x[y * n + col] = v;
        wet[y * n + col] = y >= 60 && y < 80 && col < 25;
      }
    var c = x.Select(v => (ushort)Math.Floor(v + 0.5)).ToArray();
    foreach (var bits in new[] { 8, 4 })
    {
      var want = Reference(x, c, wet, n, bits);
      int nonZero = want.Count(b => b != 0);
      C(nonZero > n * n / 3 && nonZero < n * n * 9 / 10, $"{bits} bits: the reference keeps {nonZero} of {n * n} pixels (gentle and dry), drops the rest");
      bool same = true;
      var sizes = new[] { 1, 2, 3, 7, 10, 16, 49, 50, 51, 96, 97 };
      foreach (var bandRows in sizes)
      {
        var got = ThroughBands(x, c, wet, n, bits, bandRows, workers: 1 + bandRows % 4);
        int differ = got.Where((b, i) => b != want[i]).Count();
        if (differ > 0)
        {
          same = false;
          System.Console.WriteLine($"    bands of {bandRows} rows: {differ} bytes differ from the whole-picture rule");
        }
      }
      C(same, $"{bits} bits: bands of {string.Join(", ", sizes)} rows (up to four workers) give the whole-picture rule's bytes, one by one");
    }

    // The pieces of the rule, one by one.
    var plain = Reference(x, c, wet, n, 4);
    C(Enumerable.Range(0, n * n).Where(i => wet[i]).All(i => plain[i] == 0), "under water f is 0");
    C(Enumerable.Range(0, 69).All(col => plain[49 * n + col] == 0 && plain[50 * n + col] == 0), "on both sides of the cliff f is 0");
    C(Enumerable.Range(0, 69).Count(col => plain[48 * n + col] != 0) > 40 && Enumerable.Range(0, 69).Count(col => plain[51 * n + col] != 0) > 40, "and one row further it is not");
    C(Enumerable.Range(0, 7 * n).All(i => plain[i] == 0) && Enumerable.Range(0, n).Count(col => plain[8 * n + col] != 0) > n / 2, "the plateau at the top (65535) and the ground right beside it get f 0, the ground one row further gets bytes");
    C(Enumerable.Range(10, 80).All(y => plain[y * n + 75] == 0), "the ramp (4 steps a pixel) has none");
    var all8 = ThroughBands(x, c, wet, n, 8, 13, 3);
    var bandsAfter = Last;
    C(bandsAfter.Or == all8.Aggregate(0, (a, b) => a | b) && bandsAfter.Pixels == all8.Count(b => b != 0), $"FineBands counts what it kept: OR {bandsAfter.Or}, {bandsAfter.Pixels} pixels");
    C(WorldExportMath.FineBitsUsed(ThroughBands(x, c, wet, n, 4, 13, 3).Aggregate(0, (a, b) => a | b)) == 4 && WorldExportMath.FineBitsUsed(all8.Aggregate(0, (a, b) => a | b)) == 8, "with 4 bits the bytes use 4 bits, with 8 they use 8");
    // A flat picture of a whole grey step: every pixel dry and level, so f is the same everywhere (0 for exact steps).
    var flat = Enumerable.Repeat(1000.0, 16 * 16).ToArray();
    var flatBytes = ThroughBands(flat, flat.Select(v => (ushort)v).ToArray(), new bool[16 * 16], 16, 8, 5, 2);
    C(flatBytes.All(b => b == 0) && Last.Or == 0 && Last.Pixels == 0, "level ground on whole grey steps needs no fine bytes (the file would change nothing)");
    // Level ground between steps: f the same everywhere (not 0), and nothing is steep.
    var level = Enumerable.Repeat(1000.25, 16 * 16).ToArray();
    var levelBytes = ThroughBands(level, level.Select(v => (ushort)Math.Floor(v + 0.5)).ToArray(), new bool[16 * 16], 16, 8, 5, 2);
    C(levelBytes.All(b => b == 64) && Last.Pixels == 256, "level ground a quarter of a step above a grey value has f 64 on every pixel, whatever the bands");
  }

  // ---- whole exports --------------------------------------------------------------------------------------------------

  // The fake world's heights in metres: gentle ground with a ripple finer than a grey step (and a bit of tilt), cliffs between
  // file rows on both sides of the band boundaries of a 2100 px export (998 rows a band: boundaries at rows 998 and 1996) and
  // one in the middle of a band, a ramp, and a flooded west.
  static float Terraces(float x, float z)
  {
    float h = 80f + 0.0015f * x + 0.001f * z + 0.04f * Mathf.Sin(x * 0.011f) * Mathf.Cos(z * 0.013f);
    foreach (var cliff in CliffZ)
      if (z > cliff)
        h += 7f;                    // seven metres at each cliff: 28 grey steps at Heightmap Amount 81
    if (x > 3000f && x < 3600f)
      h += (x - 3000f) * 0.05f;     // a ramp of 2 grey steps a pixel at 2100 px
    if (x < -6000f)
      h = 10f + 0.001f * x;         // flooded (under 30 m)
    return h;
  }

  static readonly List<float> CliffZ = [];

  static float Flooded(float x, float z) => 5f + 0.001f * x + 0.0003f * z + 0.03f * Mathf.Sin(x * 0.02f);   // 5 m to 19 m everywhere: under the 30 m of the sea

  // Gentle ground a lower Heightmap Amount can keep fine bytes on: it rises 'slope' metres a metre to the east, half that to the north,
  // 60 m up (or as high as asked), so a pixel (35 m at 600 px) is a fraction of a grey step away from the next at the amount it is made for.
  static Func<float, float, float> Plain(float slope, float metres = 60f) => (x, z) => metres + slope * x + 0.5f * slope * z + 100f * slope * Mathf.Sin(x * 0.003f);

  // What the export of the fake world must write for a pixel, by an independent copy of the pieces: the generator's height, the
  // edge handling, the grey value of ValueToUShort, the writer rule on double heights.
  sealed class Expect
  {
    public int N;
    public ushort[] Grey;
    public byte[] Fine;
    public double[] X;
    public bool[] Wet, Inside;
    public float[] Height;      // metres as the generator gives them
    public int Dry, Kept;
  }

  static Expect Expected(int n, float amount, float seaLevel, int bits, Func<float, float, float> height, bool edge = true)
  {
    float sla = M.SeaLevelAdjustment(seaLevel);
    float worldR = BC.WorldRadius, totalR = BC.TotalRadius, total = BC.TotalSize;
    var e = new Expect { N = n, Grey = new ushort[n * n], X = new double[n * n], Wet = new bool[n * n], Inside = new bool[n * n], Height = new float[n * n] };
    for (int r = 0; r < n; r++)
      for (int col = 0; col < n; col++)
      {
        float wx = M.PixelToWorld(col, n, total), wz = M.FileRowToWorldZ(r, n, total);
        float h = height(wx, wz);
        float fh = h / 200f;
        float d = Mathf.Sqrt(wx * wx + wz * wz);
        if (edge && d > worldR)
          M.TryUndoEdgeDropoff(fh, d, worldR, totalR, out fh);
        float v = (fh + 0.15f - sla) / amount;
        int i = r * n + col;
        e.Grey[i] = M.ValueToUShort(v, out _);
        double dv = ((double)fh + (double)0.15f - (double)sla) / (double)amount;
        dv = !(dv > 0.0) ? 0.0 : dv >= 1.0 ? 1.0 : dv;
        e.X[i] = dv * 65535.0;
        e.Wet[i] = h < 30f;
        e.Height[i] = h;
        e.Inside[i] = d <= worldR;
      }
    e.Fine = bits == 0 ? new byte[n * n] : Reference(e.X, e.Grey, e.Wet, n, bits);
    for (int i = 0; i < n * n; i++)
      if (!e.Wet[i])
      {
        e.Dry++;
        if (e.Fine[i] != 0)
          e.Kept++;
      }
    return e;
  }

  sealed class Folder
  {
    public string Dir;
    public WorldExport.Options Options;
    public string Phase, Error;
    public IReadOnlyList<string> Summary;
    public HashSet<string> Files;
  }

  // The export coroutine on the fake world, to the end (or until cancel(phase) says to cancel).
  static Folder Export(string work, string name, WorldExport.Options o, Func<float, float, float> height, Func<string, bool> cancelAt = null, Action<WorldExport.Options> tweak = null)
  {
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Fine World", m_seedName = "FineSeed", m_seed = 5 };
    var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    BC.Settings = new BC.BetterContinentsSettings();
    o.Biomes = o.Locations = o.Forest = o.Heat = o.AltBiomes = o.Lava = o.Moss = o.Paint = o.Sources = o.Preset = false;
    tweak?.Invoke(o);
    var dir = Path.Combine(work, name);
    TerrainTest.HeightOverride = height;
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
    }
    return new Folder
    {
      Dir = dir, Options = o, Phase = WorldExport.Phase, Error = WorldExport.LastError, Summary = WorldExport.LastSummary,
      Files = Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet() : [],
    };
  }

  static WorldExport.Options Options(int n, float amount, WorldExport.FineHeightsMode mode)
  {
    var o = WorldExport.Options.Default();
    o.Size = n;
    o.HeightmapAmount = amount;
    o.FineHeights = mode;
    return o;
  }

  static ushort[] ReadGrey(string path, int n)
  {
    using var image = Image.Load<L16>(path);
    var pixels = new L16[n * n];
    image.CopyPixelDataTo(pixels);
    return pixels.Select(p => p.PackedValue).ToArray();
  }

  static byte[] ReadFine(string path, int n)
  {
    using var image = Image.Load<L8>(path);
    var pixels = new L8[n * n];
    image.CopyPixelDataTo(pixels);
    return pixels.Select(p => p.PackedValue).ToArray();
  }

  static uint Crc(byte[] bytes)
  {
    uint crc = 0xFFFFFFFF;
    foreach (var b in bytes)
    {
      crc ^= b;
      for (int k = 0; k < 8; k++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return ~crc;
  }

  static string Latin1(byte[] bytes, int at, int count) => Encoding.Latin1.GetString(bytes, at, count);

  static void Exports(string work)
  {
    Section("fine heights: whole exports of a made-up world");
    const int n = 2100;
    // Cliffs between file rows 997 / 998 and 1995 / 1996 (the band boundaries at 2100 px) and 500 / 501 (inside a band).
    float ZBetween(int rowAbove, int rowBelow) => 0.5f * (M.FileRowToWorldZ(rowAbove, n, BC.TotalSize) + M.FileRowToWorldZ(rowBelow, n, BC.TotalSize));
    CliffZ.Clear();
    CliffZ.AddRange([ZBetween(997, 998), ZBetween(1995, 1996), ZBetween(500, 501)]);
    var bandRows = Math.Max(1, Math.Min(n, (1 << 21) / n));
    C(bandRows == 998 && (n + bandRows - 1) / bandRows == 3, $"a {n} px export is cut in three bands of {bandRows} rows, so the cliffs lie at both band boundaries and inside a band");

    var a = Export(work, "fine-auto-81", Options(n, 81f, WorldExport.FineHeightsMode.Auto), Terraces);
    C(a.Phase == "Done" && a.Error == null, $"the export at Heightmap Amount 81 finishes ({a.Phase} {a.Error})");
    var expected = new[] { "heightmap.png", "heightmap-fine.png", "export.cfg", "README.txt", "manifest.json" };
    C(expected.All(a.Files.Contains) && a.Files.Count == expected.Length, $"it writes heightmap.png and heightmap-fine.png beside it ({string.Join(", ", a.Files.OrderBy(f => f))})");
    var heightmapBytes = File.ReadAllBytes(Path.Combine(a.Dir, "heightmap.png"));
    var fineBytes = File.ReadAllBytes(Path.Combine(a.Dir, "heightmap-fine.png"));

    // The file: 8-bit grey, the record after the last IDAT, the CRC-32 of heightmap.png's bytes.
    var chunks = PngRowWriterTest.Chunks(fineBytes, out var chunksOk);
    var types = chunks.Select(c => c.Type).ToList();
    int lastIdat = types.LastIndexOf("IDAT");
    var header = chunks[0].Data;
    C(chunksOk && header[8] == 8 && header[9] == 0 && header[12] == 0 && ((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]) == n
      && ((header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7]) == n, "heightmap-fine.png is a well formed 8-bit grey PNG of the same size, no alpha, no interlacing");
    C(types[0] == "IHDR" && types.Count(t => t == "tEXt") == 1 && types[lastIdat + 1] == "tEXt" && types[lastIdat + 2] == "IEND" && types.Count == lastIdat + 3,
      $"its one text chunk comes after the last image data, before the end ({string.Join(" ", types.Distinct())})");
    var textData = chunks[lastIdat + 1].Data;
    int zero = Array.IndexOf(textData, (byte)0);
    var keyword = Latin1(textData, 0, zero);
    var text = Latin1(textData, zero + 1, textData.Length - zero - 1);
    uint crc = Crc(heightmapBytes);
    C(keyword == "BetterContinents" && text == $"Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = {crc:X8}", $"the record is '{text}', and {crc:X8} is the CRC-32 of the heightmap.png file");
    C(PngRowWriter.FileCrc32(Path.Combine(a.Dir, "heightmap.png")) == crc, "the writer's own file CRC says the same");
    using (var image = Image.Load<L8>(Path.Combine(a.Dir, "heightmap-fine.png")))
    {
      var meta = image.Metadata.GetPngMetadata().TextData.ToList();
      C(meta.Count == 1 && meta[0].Value == text, "ImageSharp reads that record");
    }
    var recordChunks = PngRowWriterTest.Chunks(heightmapBytes, out _);
    C(recordChunks.Select(c => c.Type).First(t => t != "IHDR") == "tEXt" && recordChunks.Count(c => c.Type == "tEXt") == 1, "heightmap.png keeps its one record, before the pixels");

    // Every pixel against the writer rule on the same heights.
    var e = Expected(n, 81f, 0.5f, 4, Terraces);
    var grey = ReadGrey(Path.Combine(a.Dir, "heightmap.png"), n);
    var fine = ReadFine(Path.Combine(a.Dir, "heightmap-fine.png"), n);
    int greyBad = 0, fineBad = 0, nibbleBad = 0;
    for (int i = 0; i < n * n; i++)
    {
      if (grey[i] != e.Grey[i]) greyBad++;
      if (fine[i] != e.Fine[i]) fineBad++;
      if ((fine[i] & 15) != 0) nibbleBad++;
    }
    C(greyBad == 0, $"heightmap.png holds ValueToUShort's grey value at every pixel ({greyBad} differ)");
    C(fineBad == 0, $"heightmap-fine.png holds the writer rule's byte at every pixel, 0 under water and on steep ground ({fineBad} of {n * n} differ)");
    C(nibbleBad == 0, "with 4 bits the low nibble of every byte is 0");
    int wetN = e.Wet.Count(w => w), zeroed = Enumerable.Range(0, n * n).Count(i => !e.Wet[i] && e.Fine[i] == 0);
    C(e.Kept > e.Dry / 2 && wetN > n * n / 20 && zeroed > 3 * n && fine.Count(b => b != 0) == e.Kept, $"the world has all three kinds of ground: {e.Kept} dry gentle pixels keep a byte, {wetN} are under water, {zeroed} dry ones are steep or exactly on a step");
    // Read back as a world reads it, N = 256 x grey + f, v = N / 16776960, metres = (v x amount - 0.15 + adjustment) x 200: where there is a
    // byte the height is the generator's to within the 4 bits (a sixteenth of a grey step is 1.55 cm, and 16 units at most where the byte runs out),
    // and where there is none, to within half a grey step (12.4 cm).
    {
      float sla = M.SeaLevelAdjustment(0.5f);
      double withByte = 0, without = 0;
      long counted = 0;
      for (int i = 0; i < n * n; i++)
      {
        if (!e.Inside[i] || e.Wet[i])
          continue;
        double back = (((256.0 * grey[i] + (sbyte)fine[i]) / 16776960.0) * 81.0 - 0.15f + sla) * 200.0;
        double err = Math.Abs(back - e.Height[i]);
        if (fine[i] != 0) withByte = Math.Max(withByte, err); else without = Math.Max(without, err);
        counted++;
      }
      C(counted > n * n / 2 && withByte < 0.0156 && without < 0.1237, $"reading it back, the ground with a byte is within {withByte * 100:0.00} cm of the generator's height (4 bits: 1.56 cm at most), the rest within {without * 100:0.0} cm (half a step: 12.4 cm)");
    }
    // The cliffs: the rows either side of each are 0 on dry ground (the neighbour across the cliff is 28 steps away), the rows next to those are not.
    foreach (var (above, below) in new[] { (997, 998), (1995, 1996), (500, 501) })
    {
      int dry = 0, zeros = 0, beside = 0, besideKept = 0;
      for (int col = 0; col < n; col++)
      {
        float wx = M.PixelToWorld(col, n, BC.TotalSize);
        if (wx < -5000f || wx > 2900f)
          continue;       // dry, inside the world and short of the ramp
        foreach (var r in new[] { above, below })
        {
          dry++;
          if (fine[r * n + col] == 0) zeros++;
        }
        foreach (var r in new[] { above - 1, below + 1 })
        {
          beside++;
          if (fine[r * n + col] != 0) besideKept++;
        }
      }
      C(dry > 1000 && zeros == dry && besideKept > beside / 2, $"the cliff between rows {above} and {below}: all {dry} dry pixels beside it have f 0, and {besideKept} of {beside} one row further keep a byte");
    }

    // heightmap.png is the same with and without a fine file, and that file is the same with 4 given as with auto.
    var off = Export(work, "fine-off-81", Options(n, 81f, WorldExport.FineHeightsMode.Off), Terraces);
    C(off.Phase == "Done" && !off.Files.Contains("heightmap-fine.png") && off.Files.Contains("heightmap.png"), "fine=off writes no fine file");
    C(File.ReadAllBytes(Path.Combine(off.Dir, "heightmap.png")).SequenceEqual(heightmapBytes), "heightmap.png is byte for byte the same with the fine file and without it");
    var four = Export(work, "fine-4-81", Options(n, 81f, WorldExport.FineHeightsMode.Bits4), Terraces);
    C(File.ReadAllBytes(Path.Combine(four.Dir, "heightmap-fine.png")).SequenceEqual(fineBytes) && File.ReadAllBytes(Path.Combine(four.Dir, "heightmap.png")).SequenceEqual(heightmapBytes),
      "fine=4 writes the very files fine=auto does at Heightmap Amount 81");

    // The manifest, the README, the notes and the summary.
    using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(a.Dir, "manifest.json"))))
    {
      var root = doc.RootElement;
      var keys = root.EnumerateObject().Select(p => p.Name).ToList();
      C(root.GetProperty("fineBits").GetInt32() == 4 && root.GetProperty("fineEncoding").GetString() == "signed offset, 1/256 step, format 1"
        && keys.IndexOf("fineBits") == keys.IndexOf("heightEncoding") + 1 && keys.IndexOf("fineEncoding") == keys.IndexOf("heightEncoding") + 2, "manifest.json has fineBits 4 and fineEncoding 'signed offset, 1/256 step, format 1', after heightEncoding");
      var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
      C(files.Contains("heightmap-fine.png") && files.IndexOf("heightmap-fine.png") == files.IndexOf("heightmap.png") + 1, "and lists heightmap-fine.png after heightmap.png");
      var notes = root.GetProperty("notes").EnumerateArray().Select(f => f.GetString()).ToList();
      C(notes.Any(t => t.StartsWith("Heights: heightmap-fine.png refines heightmap.png from steps of 24.7 cm to 1.5 cm on ")), $"the notes say what it refines to ({notes.FirstOrDefault(t => t.Contains("fine"))})");
      C(!notes.Any(t => t.Contains("adds nothing")), "and not that it adds nothing");
    }
    var readme = File.ReadAllLines(Path.Combine(a.Dir, "README.txt"));
    int fineLine = Array.FindIndex(readme, l => l.StartsWith("  heightmap-fine.png "));
    int heightLine = Array.FindIndex(readme, l => l.StartsWith("  heightmap.png "));
    C(fineLine > 0 && heightLine > 0 && fineLine == heightLine + 2 && readme[fineLine].Contains("Finer heights for heightmap.png (8-bit grey): 4 more bits for each pixel, a step of 24.7 cm refined to 1.5 cm")
      && readme[fineLine + 1].EndsWith("If you edit heightmap.png, delete") && readme[fineLine + 2].Contains("heightmap-fine.png or export again: Better Continents ignores a fine file whose CRC does not match"),
      "README.txt lists heightmap-fine.png with its precision and the warning about editing heightmap.png");
    int col18 = readme[fineLine].IndexOf("Finer");
    C(readme[heightLine].IndexOf("How high") == col18 && readme[heightLine + 1].IndexOf("Only right at") == col18 && readme[fineLine + 1].IndexOf("where the ground") == col18
      && readme.First(l => l.StartsWith("  export.cfg")).IndexOf("The settings") == col18 && readme.First(l => l.StartsWith("  manifest.json")).IndexOf("Every") == col18,
      $"the descriptions line up in one column (column {col18}) with the longer name");
    C(readme.Any(l => l.Contains("v = (256 x pixel + f) / 16776960")) && readme.Any(l => l.Contains("heightmap-fine.png goes with the heightmap.png it was exported with")), "the numbers and the editing advice name the fine file");
    C(a.Summary.Any(l => l.StartsWith("fine heights: heightmap-fine.png refines 65.9% of the pixels from steps of 24.7 cm to 1.5 cm")), $"the summary says so ({a.Summary.FirstOrDefault(l => l.StartsWith("fine"))})");

    // Without one: the old export, in every file.
    using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(off.Dir, "manifest.json"))))
    {
      var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
      C(!keys.Contains("fineBits") && !keys.Contains("fineEncoding") && !doc.RootElement.GetProperty("notes").EnumerateArray().Any(t => t.GetString().Contains("fine")), "without a fine file the manifest has no fine keys and no fine notes");
    }
    var readmeOff = File.ReadAllLines(Path.Combine(off.Dir, "README.txt"));
    C(!readmeOff.Any(l => l.Contains("heightmap-fine") || l.Contains("Finer heights") || l.Contains("fine heights")) && readmeOff.Any(l => l.StartsWith("  heightmap.png     How high")) && readmeOff.Any(l => l.Contains("so no height is stored finer than that")),
      "and its README.txt has no fine line, in the old column (17 wide)");
    C(!off.Summary.Any(l => l.Contains("fine")), "nor the summary");

    // Heightmap Amount 4.9 (a step of 1.495 cm) and 5 (1.526 cm), on a smaller world: auto draws the line between them.
    const int m = 600;
    var plain5 = Plain(0.0002f);
    var low = Export(work, "fine-auto-4.9", Options(m, 4.9f, WorldExport.FineHeightsMode.Auto), plain5);
    var five = Export(work, "fine-auto-5", Options(m, 5f, WorldExport.FineHeightsMode.Auto), plain5);
    C(low.Phase == "Done" && !low.Files.Contains("heightmap-fine.png") && low.Files.Contains("heightmap.png") && !low.Files.Any(f => f.EndsWith(".tmp")), "fine=auto at Heightmap Amount 4.9 writes no fine file");
    C(five.Phase == "Done" && five.Files.Contains("heightmap-fine.png"), "and at 5 it does");
    var expected5 = Expected(m, 5f, 0.5f, 4, plain5);
    var five4 = five.Files.Contains("heightmap-fine.png") ? ReadFine(Path.Combine(five.Dir, "heightmap-fine.png"), m) : [];
    C(five4.SequenceEqual(expected5.Fine) && five4.Any(b => b != 0), $"the fine file at 5 is the writer rule's, byte for byte ({expected5.Kept} of {m * m} pixels keep a byte)");
    var plain2 = Plain(0.00004f);
    var bits8 = Export(work, "fine-8-81", Options(m, 81f, WorldExport.FineHeightsMode.Bits8), Plain(0.00002f));
    var eight = Expected(m, 81f, 0.5f, 8, Plain(0.00002f));
    var eightGrey = ReadGrey(Path.Combine(bits8.Dir, "heightmap.png"), m);
    var eightFine = bits8.Files.Contains("heightmap-fine.png") ? ReadFine(Path.Combine(bits8.Dir, "heightmap-fine.png"), m) : [];
    // (The made-up generator does not apply the edge drop-off the export undoes, so the ring just outside the world radius is a cliff of its own.)
    double worst8 = 0;
    long withByte8 = 0;
    for (int i = 0; i < m * m && eightFine.Length > 0; i++)
      if (eight.Inside[i] && !eight.Wet[i] && eightFine[i] != 0)
      {
        withByte8++;
        worst8 = Math.Max(worst8, Math.Abs((((256.0 * eightGrey[i] + (sbyte)eightFine[i]) / 16776960.0) * 81.0 - 0.15f + M.SeaLevelAdjustment(0.5f)) * 200.0 - eight.Height[i]));
      }
    C(eightFine.SequenceEqual(eight.Fine) && withByte8 > m * m / 2 && worst8 < 0.00105, $"8 bits at Heightmap Amount 81: the writer rule's bytes, and read back each of {withByte8} heights with a byte is within {worst8 * 1000:0.000} mm of the generator's (one unit is 0.97 mm)");
    var explicit8 = Export(work, "fine-8-2", Options(m, 2f, WorldExport.FineHeightsMode.Bits8), plain2);
    var expected8 = Expected(m, 2f, 0.5f, 8, plain2);
    var fine8 = explicit8.Files.Contains("heightmap-fine.png") ? ReadFine(Path.Combine(explicit8.Dir, "heightmap-fine.png"), m) : [];
    C(explicit8.Phase == "Done" && fine8.SequenceEqual(expected8.Fine) && fine8.Any(b => (b & 1) != 0), $"fine=8 at Heightmap Amount 2 writes a fine file of 8 bits, the writer rule's byte for byte ({expected8.Kept} of {m * m} pixels keep a byte)");
    var chunks8 = PngRowWriterTest.Chunks(File.ReadAllBytes(Path.Combine(explicit8.Dir, "heightmap-fine.png")), out _);
    C(Latin1(chunks8[^2].Data, 17, chunks8[^2].Data.Length - 17).StartsWith("Fine Format = 1; Fine Bits = 8; "), "whose record says 8 bits");
    using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(explicit8.Dir, "manifest.json"))))
    {
      var notes = doc.RootElement.GetProperty("notes").EnumerateArray().Select(t => t.GetString()).ToList();
      C(doc.RootElement.GetProperty("fineBits").GetInt32() == 8 && notes.Any(t => t.Contains("at Heightmap Amount 2 one step of heightmap.png is already 6.1 mm, so heightmap-fine.png adds nothing worth its size")),
        $"and a note says it adds nothing worth its size there ({notes.FirstOrDefault(t => t.Contains("adds nothing"))})");
    }

    // The other terrain maps (lava, moss, paint) share the bands with the fine file and its row of look-ahead: they are the same with it and without,
    // on the three-biome world (whose Ashlands and Mistlands corners make the lava and moss maps), over three bands.
    {
      void Terrain(WorldExport.Options o) { o.Lava = o.Moss = o.Paint = true; }
      var withFine = Export(work, "fine-terrain-auto", Options(n, 81f, WorldExport.FineHeightsMode.Auto), null, tweak: Terrain);
      var without = Export(work, "fine-terrain-off", Options(n, 81f, WorldExport.FineHeightsMode.Off), null, tweak: Terrain);
      var maps = new[] { "heightmap.png", "lavamap.png", "mossmap.png", "paintmap.png" };
      C(withFine.Phase == "Done" && without.Phase == "Done" && maps.All(withFine.Files.Contains) && withFine.Files.Contains("heightmap-fine.png") && !without.Files.Contains("heightmap-fine.png"),
        $"with the lava, moss and paint maps too, the export writes them all and the fine file ({string.Join(", ", withFine.Files.Where(f => f.EndsWith(".png")).OrderBy(f => f))})");
      C(maps.All(f => File.ReadAllBytes(Path.Combine(withFine.Dir, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(without.Dir, f)))), "and the heightmap, lava, moss and paint maps are byte for byte the ones of the export without a fine file");
      var fineMap = ReadFine(Path.Combine(withFine.Dir, "heightmap-fine.png"), n);
      C(fineMap.Any(b => b != 0) && fineMap.All(b => (b & 15) == 0), "the fine file of the three-biome world (steep where the biomes meet) has 4-bit bytes");
      // The same ground without the zone blend (per-point heights): one biome, so the same heights, so the same file.
      var perPoint = Export(work, "fine-perpoint", Options(600, 81f, WorldExport.FineHeightsMode.Auto), Plain(0.0002f), tweak: o => o.ZoneBlend = false);
      var blended = Export(work, "fine-blend", Options(600, 81f, WorldExport.FineHeightsMode.Auto), Plain(0.0002f));
      C(perPoint.Files.Contains("heightmap-fine.png") && File.ReadAllBytes(Path.Combine(perPoint.Dir, "heightmap-fine.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(blended.Dir, "heightmap-fine.png"))),
        "per-point heights (perpoint) give the same fine file on ground of one biome");
      // No heightmap, no fine file, whatever was asked.
      var none = Export(work, "fine-no-heightmap", Options(300, 81f, WorldExport.FineHeightsMode.Bits8), Plain(0.0002f), tweak: o => { o.Heightmap = false; o.Lava = true; });
      C(none.Phase == "Done" && !none.Files.Contains("heightmap.png") && !none.Files.Contains("heightmap-fine.png") && !none.Files.Any(f => f.EndsWith(".tmp")), "without the heightmap there is no fine file, and the export is not troubled by the request");
    }

    // High ground, 15 km up at the top of the grey scale (grey 60,000 and more at Heightmap Amount 81): the float heights are 1 mm apart there, and
    // the bytes are still the writer rule's and read back within the 4 bits.
    {
      var high = Plain(0.0002f, 15000f);
      var up = Export(work, "fine-high", Options(m, 81f, WorldExport.FineHeightsMode.Auto), high);
      var expectedUp = Expected(m, 81f, 0.5f, 4, high);
      var upFine = up.Files.Contains("heightmap-fine.png") ? ReadFine(Path.Combine(up.Dir, "heightmap-fine.png"), m) : [];
      var upGrey = ReadGrey(Path.Combine(up.Dir, "heightmap.png"), m);
      double worstUp = 0;
      long withByteUp = 0;
      for (int i = 0; i < m * m && upFine.Length > 0; i++)
        if (expectedUp.Inside[i] && upFine[i] != 0)
        {
          withByteUp++;
          worstUp = Math.Max(worstUp, Math.Abs((((256.0 * upGrey[i] + (sbyte)upFine[i]) / 16776960.0) * 81.0 - 0.15f + M.SeaLevelAdjustment(0.5f)) * 200.0 - expectedUp.Height[i]));
        }
      C(upGrey.Min() > 58000 && upFine.SequenceEqual(expectedUp.Fine) && withByteUp > m * m / 3 && worstUp < 0.0156,
        $"15 km up (grey {upGrey.Min()} to {upGrey.Max()}): the writer rule's bytes, and each of {withByteUp} heights with a byte reads back within {worstUp * 100:0.00} cm");
    }

    // The wording of how much is refined: a share, or the number of pixels when it is under a tenth of a percent.
    {
      var world = new World { m_name = "Words", m_seedName = "W", m_seed = 1 };
      var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
      typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
      var o = Options(2048, 81f, WorldExport.FineHeightsMode.Auto);
      var job = EndToEnd.MakeJob(o, Path.Combine(work, "words"), wg);
      var jobType = job.GetType();
      string Coverage(long pixels)
      {
        jobType.GetField("FinePixels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, pixels);
        return (string)jobType.GetMethod("FineCoverage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;
      }
      C(Coverage(157) == "157 pixel(s)" && Coverage(2_000_000) == "47.7% of the pixels" && Coverage(4194) == "4194 pixel(s)" && Coverage(4195) == "0.1% of the pixels", "the coverage is a share of the pixels, or their number when under a tenth of a percent");
    }

    // A world with no ground above water: every byte would be 0, so there is no fine file, and a note says why.
    var flooded = Export(work, "fine-flooded", Options(m, 81f, WorldExport.FineHeightsMode.Auto), Flooded);
    C(flooded.Phase == "Done" && !flooded.Files.Contains("heightmap-fine.png") && !flooded.Files.Any(f => f.EndsWith(".tmp")) && flooded.Files.Contains("heightmap.png"),
      "a flooded world has no fine file, and leaves no .tmp");
    using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(flooded.Dir, "manifest.json"))))
    {
      var notes = doc.RootElement.GetProperty("notes").EnumerateArray().Select(t => t.GetString()).ToList();
      C(!doc.RootElement.TryGetProperty("fineBits", out _) && notes.Any(t => t.StartsWith("Heights: no pixel needs fine heights")), $"the notes say no pixel needed it ({notes.FirstOrDefault(t => t.Contains("fine"))})");
    }

    // A cancel with both files open, at every point a test can see: no file, no folder.
    var cancelled = Export(work, "fine-cancel", Options(n, 81f, WorldExport.FineHeightsMode.Auto), Terraces, cancelAt: phase => phase == "Sampling heights" && SawBothTmp(Path.Combine(work, "fine-cancel")));
    C(cancelled.Phase == "Cancelled" && !Directory.Exists(cancelled.Dir) && sawBoth, $"a cancel while both files are being written leaves neither (their .tmp files were both there: {sawBoth}; {cancelled.Phase})");
    sawBoth = false;
    // And in the one moment heightmap.png is whole and the fine file is not (the CRC is read from the finished heightmap): slowed down so
    // that the test sees that phase.
    SlowCrcOnce();
    slowCrc = true;
    bool heightmapWhole = false;
    var cancelledLate = Export(work, "fine-cancel-late", Options(m, 81f, WorldExport.FineHeightsMode.Auto), Terraces, cancelAt: phase =>
    {
      if (phase != "Writing heightmap-fine.png")
        return false;
      heightmapWhole |= File.Exists(Path.Combine(work, "fine-cancel-late", "heightmap.png")) && File.Exists(Path.Combine(work, "fine-cancel-late", "heightmap-fine.png.tmp"));
      return true;
    });
    slowCrc = false;
    C(heightmapWhole && cancelledLate.Phase == "Cancelled" && !Directory.Exists(cancelledLate.Dir), $"a cancel with heightmap.png whole and the fine file still open leaves no file and no folder ({cancelledLate.Phase})");
    C(!Directory.GetFiles(work, "*.tmp", SearchOption.AllDirectories).Any(), "no .tmp file is left anywhere under the test folder");

    System.Console.WriteLine($"    fine file {fineBytes.Length / 1024} KB beside heightmap.png {heightmapBytes.Length / 1024} KB; {e.Kept} of {n * n} pixels have a byte ({100.0 * e.Kept / (n * n):0.#}%)");
    CliffZ.Clear();

    // BCEXPORT_FINE_KEEP=<folder>: the exports above are copied there, for tools/fine_ref.py's checker to read, and the heights of the first one
    // (x in grey steps as doubles, and 1 where a pixel is under water, as raw files) for a comparison with fine_ref.py's own writer rule.
    var keep = Environment.GetEnvironmentVariable("BCEXPORT_FINE_KEEP");
    if (!string.IsNullOrEmpty(keep))
    {
      foreach (var name in new[] { "fine-auto-81", "fine-off-81", "fine-auto-5", "fine-8-2", "fine-flooded" })
      {
        var to = Path.Combine(keep, name);
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(Path.Combine(work, name)))
          File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
      }
      var rawX = new byte[8 * n * n];
      Buffer.BlockCopy(e.X, 0, rawX, 0, rawX.Length);
      File.WriteAllBytes(Path.Combine(keep, "fine-auto-81", "x.f64"), rawX);
      File.WriteAllBytes(Path.Combine(keep, "fine-auto-81", "wet.u8"), e.Wet.Select(w => w ? (byte)1 : (byte)0).ToArray());
    }
  }

  static bool sawBoth, slowCrc, slowPatched;

  // The CRC step of the fine file made slow (while slowCrc is set), for the test that cancels in the middle of it.
  static void SlowCrcPrefix()
  {
    if (slowCrc)
      Thread.Sleep(400);
  }

  static void SlowCrcOnce()
  {
    if (slowPatched)
      return;
    slowPatched = true;
    new HarmonyLib.Harmony("export-fine-slow").Patch(HarmonyLib.AccessTools.Method(typeof(PngRowWriter), "FileCrc32"), prefix: new HarmonyLib.HarmonyMethod(typeof(FineTest), nameof(SlowCrcPrefix)));
  }

  static bool SawBothTmp(string dir)
  {
    try
    {
      if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "heightmap.png.tmp")) && File.Exists(Path.Combine(dir, "heightmap-fine.png.tmp")))
        sawBoth = true;
    }
    catch (IOException)
    {
    }
    return sawBoth;
  }
}

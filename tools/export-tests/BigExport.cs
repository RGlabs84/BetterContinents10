// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The whole export at a size past the usual test sizes (8192, 16384): every map but the locations (a Unity object), on the
// fake world of EndToEnd, with the memory of the process sampled each 100 ms and the peak printed per phase, then spot
// checks of what was written (decoded one map at a time, so the checks stay within a few hundred MB).
// BCEXPORT_BIG=<size> turns it on; BCEXPORT_AMOUNT sets the Heightmap Amount (default 81, the largest).
internal static class BigExport
{
  static readonly object Gate = new();

  static (long Rss, long Hwm) Memory()
  {
    long rss = 0, hwm = 0;
    try
    {
      foreach (var line in File.ReadAllLines("/proc/self/status"))
      {
        if (line.StartsWith("VmRSS:")) rss = long.Parse(line.Substring(6).Trim().Split(' ')[0]);
        else if (line.StartsWith("VmHWM:")) hwm = long.Parse(line.Substring(6).Trim().Split(' ')[0]);
      }
    }
    catch
    {
      // Not Linux, or no /proc: no numbers.
    }
    return (rss, hwm);
  }

  public static void Run(string work, int n, float amount, bool check = true)
  {
    System.Console.WriteLine($"== big export: {n} x {n} px, Heightmap Amount {amount}, every map but the locations");
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Big World", m_seedName = "BigSeed", m_seed = 7 };
    var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    EndToEnd.BuildAltBiomes(world);
    BC.Settings = new BC.BetterContinentsSettings();
    const float T = 21000f;
    var o = WorldExport.Options.Default();
    o.Size = n;
    o.HeightmapAmount = amount;
    o.Paint = true;
    o.Locations = false;
    o.Preset = false;
    var dir = Path.Combine(work, "big-export");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);

    // Memory per phase, from a sampler thread (the export itself runs on workers and a polling coroutine).
    var peaks = new List<(string Phase, double Start, double Seconds, long PeakKb)>();
    var clock = Stopwatch.StartNew();
    var before = Memory();
    string phase = "";
    long phasePeak = 0;
    double phaseStart = 0;
    bool stop = false;
    void CloseFrame(string next)
    {
      lock (Gate)
      {
        if (phase.Length > 0)
          peaks.Add((phase, phaseStart, clock.Elapsed.TotalSeconds - phaseStart, phasePeak));
        phase = next;
        phasePeak = 0;
        phaseStart = clock.Elapsed.TotalSeconds;
      }
    }
    var sampler = new Thread(() =>
    {
      while (!Volatile.Read(ref stop))
      {
        var rss = Memory().Rss;
        lock (Gate)
          if (rss > phasePeak) phasePeak = rss;
        Thread.Sleep(100);
      }
    }) { IsBackground = true };
    sampler.Start();

    var drive = EndToEnd.Drive(job);
    while (drive.MoveNext())
    {
      var now = WorldExport.Phase;
      if (now != phase)
        CloseFrame(now);
      Thread.Sleep(2);
    }
    CloseFrame("");
    Volatile.Write(ref stop, true);
    sampler.Join();
    var total = clock.Elapsed.TotalSeconds;
    var after = Memory();
    foreach (var p in peaks)
      System.Console.WriteLine($"    {p.Phase,-44} {p.Seconds,7:0.0} s, peak {p.PeakKb / 1024,6} MB");
    System.Console.WriteLine($"    MEASURED {n}: {total:0.0} s in all; process RSS {before.Rss / 1024} MB before, peak (VmHWM) {after.Hwm / 1024} MB, {after.Rss / 1024} MB after; "
                             + $"managed {GC.GetTotalMemory(false) / 1048576} MB; GC heap peak {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576} MB");
    Program.C(!WorldExport.IsRunning && WorldExport.Phase == "Done" && WorldExport.LastError == null,
      $"the {n} px export finishes: phase {WorldExport.Phase}, error {WorldExport.LastError ?? "none"}");
    var present = Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet() : new HashSet<string>();
    bool fineWritten = M.AutoFineBits(amount) > 0;
    var expected = new[] { "heightmap.png", "biomemap.png", "forestmap.png", "heatmap.png", "altbiomemap.png", "lavamap.png", "mossmap.png", "paintmap.png", "export.cfg", "README.txt", "manifest.json" }
      .Concat(fineWritten ? ["heightmap-fine.png"] : []).ToArray();
    var missing = expected.Where(f => !present.Contains(f)).ToList();
    Program.C(missing.Count == 0 && !present.Any(f => f.EndsWith(".tmp")), "every map and text file is written, no .tmp is left" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
    foreach (var f in expected.Where(f => f.EndsWith(".png") && present.Contains(f)))
      System.Console.WriteLine($"    {f,-16} {new FileInfo(Path.Combine(dir, f)).Length / 1048576.0,8:0.0} MB on disk");
    if (!check || missing.Count > 0)
    {
      Cleanup(dir);
      return;
    }

    // Spot checks: every 61st row and 67th column of each map, against the fake world at that point.
    long wrong = 0, seen = 0;
    float worstMetres = 0f;
    var sla = M.SeaLevelAdjustment(o.SeaLevel);
    using (var image = Image.Load<L16>(Path.Combine(dir, "heightmap.png")))
    {
      Program.C(image.Width == n && image.Height == n, $"heightmap.png is {image.Width} x {image.Height}");
      var meta = image.Metadata.GetPngMetadata().TextData.SingleOrDefault(t => t.Keyword == HeightmapRecord.Keyword);
      var record = HeightmapRecord.From(image.Metadata.GetPngMetadata().TextData);
      Program.C(record != null && record.Amount == amount && record.SeaLevel == o.SeaLevel, $"heightmap.png records its amount ({meta.Value})");
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
            if (Mathf.Sqrt(wx * wx + wz * wz) > 10000f) continue;
            float h = Expected(wx, wz, out _, out _, out _, out _, out _);
            float got = M.ValueToMetres(M.UShortToValue(row[c].PackedValue), amount, o.SeaLevel);
            float err = Mathf.Abs(got - h);
            seen++;
            if (err > worstMetres) worstMetres = err;
          }
        }
      });
    }
    if (fineWritten)
      FineChecks(dir, n, amount, o.SeaLevel);
    // One grey step is (ceiling - floor) / 65535 metres; the pixel is the nearest step.
    float step = (M.ValueToMetres(1f, amount, o.SeaLevel) - M.ValueToMetres(0f, amount, o.SeaLevel)) / 65535f;
    Program.C(seen > 1000 && worstMetres <= step * 0.51f + 0.01f, $"{seen} sampled heights are within half a grey step ({step:0.###} m) of the fake terrain (worst {worstMetres:0.###} m)");

    wrong = 0; seen = 0;
    using (var image = Image.Load<L8>(Path.Combine(dir, "lavamap.png")))
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
            Expected(wx, wz, out _, out var ash, out var ashA, out _, out _);
            seen++;
            if (row[c].PackedValue != (ash ? M.ValueToByte(ashA) : (byte)0)) wrong++;
          }
        }
      });
    Program.C(wrong == 0 && seen > 1000, $"{seen} sampled lava pixels match ({wrong} differ)");

    wrong = 0; seen = 0;
    using (var image = Image.Load<L8>(Path.Combine(dir, "mossmap.png")))
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
            Expected(wx, wz, out _, out _, out _, out var mist, out var mistA);
            seen++;
            if (row[c].PackedValue != (mist ? M.ValueToByte(mistA) : (byte)0)) wrong++;
          }
        }
      });
    Program.C(wrong == 0 && seen > 1000, $"{seen} sampled moss pixels match ({wrong} differ)");

    wrong = 0; seen = 0;
    using (var image = Image.Load<Rgb24>(Path.Combine(dir, "paintmap.png")))
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
            Expected(wx, wz, out var mask, out _, out _, out _, out _);
            seen++;
            if (row[c].G != M.ValueToByte(mask.g) || row[c].R != M.ValueToByte(mask.r) || row[c].B != M.ValueToByte(mask.b)) wrong++;
          }
        }
      });
    Program.C(wrong == 0 && seen > 1000, $"{seen} sampled paint pixels match ({wrong} differ)");

    wrong = 0; seen = 0;
    var table = ImageMapBiome.ExportColorTable();
    using (var image = Image.Load<Rgb24>(Path.Combine(dir, "biomemap.png")))
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
            var want = table[TerrainTest.Biome(wx, wz)];
            seen++;
            if (row[c].R != want.r || row[c].G != want.g || row[c].B != want.b) wrong++;
          }
        }
      });
    Program.C(wrong == 0 && seen > 1000, $"{seen} sampled biome pixels match ({wrong} differ)");

    // The 16-bit forest and heat maps and the alt-biome map: they decode at this size, and the heat is zero in the north.
    using (var image = Image.Load<L16>(Path.Combine(dir, "heatmap.png")))
      Program.C(image.Width == n && image.Height == n && image[n / 2, 0].PackedValue == 0, "heatmap.png decodes at this size, cold in the north");
    using (var image = Image.Load<L16>(Path.Combine(dir, "forestmap.png")))
      Program.C(image.Width == n && image.Height == n, "forestmap.png decodes at this size");
    using (var image = Image.Load<Rgb24>(Path.Combine(dir, "altbiomemap.png")))
      Program.C(image.Width == n && image.Height == n, "altbiomemap.png decodes at this size");

    Cleanup(dir);
  }

  // heightmap-fine.png at this size: 8-bit grey of the same size, 4 bits, its record (after the pixels) naming the CRC-32 of the heightmap.png beside it,
  // and its bytes at every 61st row and 67th column against the writer rule on the fake world (with the policy's neighbours worked out the same way).
  static void FineChecks(string dir, int n, float amount, float seaLevel)
  {
    var path = Path.Combine(dir, "heightmap-fine.png");
    float sla = M.SeaLevelAdjustment(seaLevel), T = BC.TotalSize, worldR = BC.WorldRadius, totalR = BC.TotalRadius;
    // The height of the fake world at a pixel as the export stores it: the grey value, x in double, and whether it is under water.
    (ushort Grey, double X, bool Wet) At(int r, int col)
    {
      float wx = M.PixelToWorld(col, n, T), wz = M.FileRowToWorldZ(r, n, T);
      float h = Expected(wx, wz, out _, out _, out _, out _, out _);
      float fh = h / 200f;
      float d = Mathf.Sqrt(wx * wx + wz * wz);
      if (d > worldR)
        M.TryUndoEdgeDropoff(fh, d, worldR, totalR, out fh);
      var grey = M.ValueToUShort((fh + 0.15f - sla) / amount, out _);
      double v = ((double)fh + (double)0.15f - (double)sla) / (double)amount;
      v = !(v > 0.0) ? 0.0 : v >= 1.0 ? 1.0 : v;
      return (grey, v * 65535.0, h < 30f);
    }
    long wrong = 0, seen = 0, nonZero = 0, nibble = 0;
    using (var image = Image.Load<L8>(path))
    {
      Program.C(image.Width == n && image.Height == n, $"heightmap-fine.png is {image.Width} x {image.Height} and 8-bit grey");
      image.ProcessPixelRows(acc =>
      {
        for (int r = 0; r < n; r += 61)
        {
          var row = acc.GetRowSpan(r);
          for (int c = 0; c < n; c += 67)
          {
            var px = At(r, c);
            byte want = 0;
            if (!px.Wet)
            {
              var units = (long)Math.Round(px.X * 256.0);
              long steepest = 0;
              foreach (var (dr, dc) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
              {
                int rr = r + dr, cc = c + dc;
                if (rr < 0 || rr >= n || cc < 0 || cc >= n)
                  continue;
                steepest = Math.Max(steepest, Math.Abs(units - (long)Math.Round(At(rr, cc).X * 256.0)));
              }
              if (steepest < 256)
                want = (byte)M.FineOffset(px.X, px.Grey, 4);
            }
            seen++;
            if (row[c].PackedValue != want) wrong++;
            if (row[c].PackedValue != 0) nonZero++;
            if ((row[c].PackedValue & 15) != 0) nibble++;
          }
        }
      });
    }
    Program.C(seen > 1000 && wrong == 0 && nibble == 0, $"{seen} sampled fine bytes are the writer rule's ({wrong} differ; {nonZero} are not 0; {nibble} with bits in the low nibble)");
    var fileBytes = File.ReadAllBytes(path);
    var chunks = PngRowWriterTest.Chunks(fileBytes, out var ok);
    var types = chunks.Select(c => c.Type).ToList();
    int lastIdat = types.LastIndexOf("IDAT");
    var text = chunks[lastIdat + 1].Data;
    string record = System.Text.Encoding.Latin1.GetString(text, 17, text.Length - 17);
    uint crc = PngRowWriter.FileCrc32(Path.Combine(dir, "heightmap.png"));
    Program.C(ok && types[lastIdat + 1] == "tEXt" && types[lastIdat + 2] == "IEND" && record == $"Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = {crc:X8}",
      $"its record, after the pixels, names the CRC-32 of the heightmap.png beside it ({record})");
  }

  // A cancel at this size, a little way into the terrain pass: it stops within a band, and the files and the folder go.
  public static void CancelAt(string work, int n)
  {
    System.Console.WriteLine($"== big export: cancelled part way at {n} x {n} px");
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Big World", m_seedName = "BigSeed", m_seed = 7 };
    var wg = (WorldGenerator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    EndToEnd.BuildAltBiomes(world);
    BC.Settings = new BC.BetterContinentsSettings();
    var o = WorldExport.Options.Default();
    o.Size = n;
    o.HeightmapAmount = 81f;
    o.Locations = false;
    o.Preset = false;
    var dir = Path.Combine(work, "big-cancel");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);
    var drive = EndToEnd.Drive(job);
    var clock = Stopwatch.StartNew();
    double cancelledAt = -1;
    bool sawTmp = false;
    while (drive.MoveNext())
    {
      if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.tmp").Length > 0)
        sawTmp = true;
      if (cancelledAt < 0 && WorldExport.Phase == "Sampling heights" && WorldExport.Progress > 20f)
      {
        cancelledAt = clock.Elapsed.TotalSeconds;
        WorldExport.Cancel();
      }
      Thread.Sleep(2);
    }
    double stopped = clock.Elapsed.TotalSeconds - cancelledAt;
    System.Console.WriteLine($"    cancelled at {cancelledAt:0.0} s, stopped {stopped:0.00} s later");
    Program.C(cancelledAt > 0 && sawTmp, "the cancel came while the maps were open (their .tmp files were there)");
    Program.C(WorldExport.Phase == "Cancelled" && !WorldExport.IsRunning && !Directory.Exists(dir), $"the export ends 'Cancelled' and leaves no folder ({WorldExport.Phase})");
    Program.C(stopped < 5.0, $"it stops within seconds ({stopped:0.00} s)");
  }

  static void Cleanup(string dir)
  {
    try { Directory.Delete(dir, true); } catch { }
  }

  // The fake world at a point, as TerrainTest checks TerrainRow against: HeightmapBuilder.Build's non-LOD blend of the four
  // zone corners (the edge drop-off applied to the height as the export stores it, so inside the world only).
  static float Expected(float wx, float wz, out UnityEngine.Color mask, out bool ash, out float ashA, out bool mist, out float mistA)
  {
    int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
    float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
    var b = new[] { TerrainTest.Biome(x0, z0), TerrainTest.Biome(x0 + 64f, z0), TerrainTest.Biome(x0, z0 + 64f), TerrainTest.Biome(x0 + 64f, z0 + 64f) };
    float tx = DUtils.SmoothStep(0f, 1f, (float)((wx - (double)x0) / 64.0)), tz = DUtils.SmoothStep(0f, 1f, (float)((wz - (double)z0) / 64.0));
    var m = new UnityEngine.Color[4];
    float h;
    if (b.All(x => x == b[0]))
      h = TerrainTest.Height(b[0], wx, wz, out mask);
    else
    {
      float h0 = TerrainTest.Height(b[0], wx, wz, out m[0]), h1 = TerrainTest.Height(b[1], wx, wz, out m[1]);
      float h2 = TerrainTest.Height(b[2], wx, wz, out m[2]), h3 = TerrainTest.Height(b[3], wx, wz, out m[3]);
      h = DUtils.Lerp(DUtils.Lerp(h0, h1, tx), DUtils.Lerp(h2, h3, tx), tz);
      mask = UnityEngine.Color.Lerp(UnityEngine.Color.Lerp(m[0], m[1], tx), UnityEngine.Color.Lerp(m[2], m[3], tx), tz);
    }
    // The corners' own alpha, the first Ashlands / Mistlands corner in the job's order (00, 10, 01, 11).
    var order = new[] { 0, 1, 2, 3 };
    ash = false; ashA = 0f; mist = false; mistA = 0f;
    foreach (var k in order)
    {
      TerrainTest.Height(b[k], wx, wz, out var cm);
      if (!ash && b[k] == Heightmap.Biome.AshLands) { ash = true; ashA = cm.a; }
      if (!mist && b[k] == Heightmap.Biome.Mistlands) { mist = true; mistA = cm.a; }
    }
    return h;
  }
}

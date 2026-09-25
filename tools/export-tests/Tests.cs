// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

internal static class Tests
{
  const float T = 21000f;
  static void C(bool ok, string what) => Program.C(ok, what);
  static void Section(string s) => System.Console.WriteLine("== " + s);

  public static void Math()
  {
    Section("pixel grid");
    foreach (int n in new[] { 128, 1024, 2048, 4096, 8192 })
    {
      bool round = true, rows = true, loc = true;
      for (int i = 0; i < n; i++)
      {
        if (M.WorldToPixel(M.PixelToWorld(i, n, T), n, T) != i) round = false;
        if (M.WorldZToFileRow(M.FileRowToWorldZ(i, n, T), n, T) != i) rows = false;
        if (M.WorldToLocationPixel(M.LocationPixelToWorld(i, n, T), n, T) != i) loc = false;
      }
      C(round, $"{n}: WorldToPixel(PixelToWorld(i)) == i for every i");
      C(rows, $"{n}: WorldZToFileRow(FileRowToWorldZ(r)) == r for every r");
      C(loc, $"{n}: location pixel round trip");
      C(M.PixelToWorld(0, n, T) == -10500f && M.PixelToWorld(n - 1, n, T) == 10500f, $"{n}: pixel 0 is the west edge, pixel n-1 the east edge");
      C(M.FileRowToWorldZ(0, n, T) == 10500f && M.FileRowToWorldZ(n - 1, n, T) == -10500f, $"{n}: file row 0 is north (+z), the last row south");
    }
    var p = WorldExport.PixelOf(new Vector3(-10500f, 0f, 10500f), 4096);
    C(p.x == 0 && p.y == 0, $"PixelOf(north-west corner) = (0, 0), got {p}");
    p = WorldExport.PixelOf(new Vector3(10500f, 0f, -10500f), 4096);
    C(p.x == 4095 && p.y == 4095, $"PixelOf(south-east corner) = (4095, 4095), got {p}");
    p = WorldExport.PixelOf(Vector3.zero, 4097);
    C(p.x == 2048 && p.y == 2048, $"PixelOf(origin) at size 4097 = (2048, 2048), got {p}");

    Section("height encoding");
    C(Mathf.Abs(M.WaterlineValue(2f, 0.5f) - 0.15f) < 1e-6f, $"waterline at amount 2 / sea level 0.5 is 0.15 (got {M.WaterlineValue(2f, 0.5f)})");
    C(Mathf.Abs(M.WaterlineValue(1f, 0.5f) - 0.30f) < 1e-6f, "waterline at amount 1 is 0.30");
    C(Mathf.Abs(M.ValueToMetres(0f, 2f, 0.5f) + 30f) < 1e-4f && Mathf.Abs(M.ValueToMetres(1f, 2f, 0.5f) - 370f) < 1e-3f, "amount 2 spans -30 m .. 370 m");
    bool rt = true;
    foreach (var a in new[] { 0.5f, 1f, 2f, 3.7f, 5f })
      foreach (var sl in new[] { 0f, 0.3f, 0.5f, 1f })
        foreach (var m in new[] { -100f, -30f, 0f, 30f, 123.456f, 370f, 800f })
          if (Mathf.Abs(M.ValueToMetres(M.MetresToValue(m, a, sl), a, sl) - m) > 1e-3f) rt = false;
    C(rt, "ValueToMetres(MetresToValue(m)) == m for every amount / sea level");
    // Against GetBaseHeightV3's own arithmetic: finalHeight = v * A - 0.15 + Lerp(1, -1, SL); metres = finalHeight * 200.
    bool v3 = true;
    foreach (var m in new[] { -20f, 30f, 250f })
    {
      float v = M.MetresToValue(m, 2f, 0.5f);
      float final = v * 2f - 0.15f + Mathf.Lerp(1f, -1f, 0.5f);
      if (Mathf.Abs(final * 200f - m) > 1e-3f) v3 = false;
    }
    C(v3, "MetresToValue inverts GetBaseHeightV3");
    C(M.ValueToUShort(-0.1f, out var c1) == 0 && c1 == -1, "below 0 clips low");
    C(M.ValueToUShort(1.1f, out var c2) == 65535 && c2 == 1, "above 1 clips high");
    C(M.ValueToUShort(float.NaN, out var c3) == 0 && c3 == -1, "NaN clips low");
    C(M.ValueToUShort(0f, out var c4) == 0 && c4 == 0 && M.ValueToUShort(1f, out var c5) == 65535 && c5 == 0, "0 and 1 are not clipped");
    var rnd = new System.Random(1);
    float worst = 0f;
    for (int i = 0; i < 100000; i++)
    {
      float v = (float)rnd.NextDouble();
      worst = Mathf.Max(worst, Mathf.Abs(M.UShortToValue(M.ValueToUShort(v, out _)) - v));
    }
    C(worst <= 0.5f / 65535f + 1e-7f, $"16-bit quantisation error <= half a step (worst {worst * 65535f:0.###} steps)");
    C(M.ValueToByte(-1f) == 0 && M.ValueToByte(2f) == 255 && M.ValueToByte(0.5f) == 128 && M.ValueToByte(float.NaN) == 0, "8-bit conversion clamps and rounds");

    Section("edge drop-off inverse");
    bool edge = true;
    float edgeWorst = 0f;
    foreach (var d in new[] { 10000.5f, 10100f, 10250f, 10400f, 10480f, 10489.9f, 10491f, 10495f, 10499f })
      foreach (var s in new[] { -0.3f, -0.15f, 0f, 0.15f, 0.5f })
      {
        if (!M.TryUndoEdgeDropoff(s, d, 10000f, 10500f, out var before)) { edge = false; continue; }
        float back = M.ApplyEdgeDropoff(before, d, 10000f, 10500f);
        edgeWorst = Mathf.Max(edgeWorst, Mathf.Abs(back - s));
      }
    C(edge && edgeWorst < 2e-4f, $"ApplyEdgeDropoff(TryUndoEdgeDropoff(h)) == h across the ring (worst {edgeWorst:g3})");
    C(!M.TryUndoEdgeDropoff(0f, 10500f, 10000f, 10500f, out _) && !M.TryUndoEdgeDropoff(0f, 12000f, 10000f, 10500f, out _), "no inverse at or past the total radius");
    C(M.TryUndoEdgeDropoff(0.33f, 9999f, 10000f, 10500f, out var inside) && inside == 0.33f, "inside the world radius the height is kept");
    // A vanilla ocean pixel: the game fades base b to Lerp(b, -0.2, t) exactly like BC, so the stored value is b itself.
    {
      float b = -0.05f, d = 10250f;
      float t = M.LerpStep(10000f, 10500f, d);
      float vanilla = Mathf.Lerp(b, -0.2f, t);
      M.TryUndoEdgeDropoff(vanilla, d, 10000f, 10500f, out var stored);
      C(Mathf.Abs(stored - b) < 1e-5f, $"a vanilla edge-faded height is stored as its pre-fade value ({stored} vs {b})");
    }

    Section("forest encodings");
    var factors = new[] { 0.145071f, 0.3f, 0.7f, 1.0f, 1.15f, 1.4f, 1.850145f };
    bool additive = true, exact = true, identity = true;
    float addWorst = 0f, exWorst = 0f;
    foreach (var vanilla in factors)
      foreach (var target in factors)
      {
        float fa = M.ForestMapAdditive(target, vanilla);
        float stored = M.UShortToValue(M.ValueToUShort(fa, out int clip));
        float back = M.ApplyForest(vanilla, stored, 0f, 1f, 0f);
        if (clip == 0) addWorst = Mathf.Max(addWorst, Mathf.Abs(back - target));
        else if (clip < 0 && Mathf.Abs(back - vanilla) > 1e-4f) additive = false;
        if (vanilla == target && fa != 0f) identity = false;
        float fe = M.ForestMapExact(target, vanilla);
        if (fe < 0f || fe > 1f) exact = false;
        float backE = M.ApplyForest(vanilla, M.UShortToValue(M.ValueToUShort(fe, out _)), 1f, 1f, 0f);
        exWorst = Mathf.Max(exWorst, Mathf.Abs(backE - target));
      }
    C(additive && addWorst < 1e-4f, $"additive encoding: exact where storable (worst {addWorst:g3}), the game's own forest where the target is sparser");
    C(identity, "additive encoding: the game's own forest stores as exactly 0");
    C(exact && exWorst < 1e-4f, $"exact encoding: every target reachable (worst {exWorst:g3})");
    // WorldExportMath.ApplyForest against the real BetterContinentsSettings.ApplyForest, through a real forest map.
    {
      bool same = true;
      foreach (var u in new ushort[] { 0, 1000, 32768, 65535 })
      {
        var map = ImageMapFloat.Create(PngL16(Enumerable.Repeat(new L16(u), 4).ToArray(), 2), false);
        var s = new BC.BetterContinentsSettings();
        typeof(BC.BetterContinentsSettings).GetField("ForestMap", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, map);
        foreach (var (mul, add, off) in new[] { (0f, 1f, 0f), (1f, 1f, 0f), (1f, 0f, 0.1f), (0.5f, 0.5f, -0.2f) })
        {
          s.ForestmapMultiply = mul;
          s.ForestmapAdd = add;
          s.ForestAmountOffset = off;
          foreach (var vf in factors)
            if (s.ApplyForest(0.5f, 0.5f, vf) != M.ApplyForest(vf, u / 65535f, mul, add, off))
              same = false;
        }
      }
      C(same, "WorldExportMath.ApplyForest is bit-equal to BetterContinentsSettings.ApplyForest");
    }

    Section("forest scale config value");
    {
      var probe = new BC.BetterContinentsSettings { ForestScale = 1f };
      float inv = probe.ForestScaleFactor;
      probe.ForestScaleFactor = float.Parse(inv.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
      float viaR = probe.ForestScale;
      probe.ForestScaleFactor = 0.5f;
      float viaHalf = probe.ForestScale;
      System.Console.WriteLine($"    InvFeatureScaleCurve(1) = {inv:R}; back through R: {viaR:R}; FeatureScaleCurve(0.5) = {viaHalf:R}");
      // Search the floats around 0.5 for one that maps to exactly 1.
      int bits = BitConverter.SingleToInt32Bits(0.5f);
      var ones = new List<float>();
      for (int d = -2000; d <= 2000; d++)
      {
        float x = BitConverter.Int32BitsToSingle(bits + d);
        probe.ForestScaleFactor = x;
        if (probe.ForestScale == 1f) ones.Add(x);
      }
      System.Console.WriteLine($"    floats within 2000 ULPs of 0.5 that give exactly 1: {ones.Count} {(ones.Count > 0 ? ones.Min().ToString("R") + " .. " + ones.Max().ToString("R") : "")}");
      if (ones.Count > 0)
      {
        var text = ones[ones.Count / 2].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        probe.ForestScaleFactor = float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        System.Console.WriteLine($"    e.g. {text} -> {probe.ForestScale:R}; as BepInEx rewrites it (G): {float.Parse(text).ToString(System.Globalization.NumberFormatInfo.InvariantInfo)}");
      }
      probe.ForestScaleFactor = 1f;
      System.Console.WriteLine($"    BC's config default Forest Scale 1.0 gives ForestScale {probe.ForestScale:R}");
    }

    Section("heat");
    bool heat = true;
    foreach (var g in new[] { 0f, 0.001f, 0.5f, 5f, 9.99f })
    {
      float v = M.UShortToValue(M.ValueToUShort(M.HeatToValue(g, 10f), out _));
      if (Mathf.Abs(M.ValueToHeat(v, 10f) - (g - 0.00001f)) > 2e-4f) heat = false;
    }
    C(heat, "heat round trip at scale 10");
    C(M.ValueToUShort(M.HeatToValue(-3f, 10f), out _) == 0, "cold water (negative gradient) stores 0, which BC reads as not Ashlands");
    C(M.ValueToUShort(M.HeatToValue(0.0002f, 10f), out _) > 0, "a small positive gradient stays above 0 (still Ashlands)");
  }

  static byte[] PngL16(L16[] px, int n)
  {
    var path = Path.GetTempFileName();
    try
    {
      WorldExportPng.SaveL16(path, px, n);
      return File.ReadAllBytes(path);
    }
    finally { File.Delete(path); }
  }

  public static void HeightRoundTrip(string work)
  {
    Section("heightmap through ImageMapFloat");
    foreach (int n in new[] { 257, 1024 })
    {
      // Inside the encodable -30 m .. 370 m at amount 2, and different at every edge so orientation shows.
      float Metres(int col, int row) => 150f + 80f * col / n - 120f * row / n + 60f * Mathf.Sin(col * 0.05f) * Mathf.Cos(row * 0.03f);
      var px = new L16[n * n];
      for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
          px[r * n + c] = new L16(M.ValueToUShort(M.MetresToValue(Metres(c, r), 2f, 0.5f), out _));
      var path = Path.Combine(work, $"heightmap-{n}.png");
      WorldExportPng.SaveL16(path, px, n);
      C(!File.Exists(path + ".tmp"), $"{n}: no .tmp left behind");
      var bytes = File.ReadAllBytes(path);
      using (var img = SixLabors.ImageSharp.Image.Load(bytes, out var format))
        C(format.Name == "PNG" && img.PixelType.BitsPerPixel == 16, $"{n}: a 16-bit PNG ({img.PixelType.BitsPerPixel} bits per pixel)");
      var map = ImageMapFloat.Create(bytes, false);
      var mapA = ImageMapFloat.Create(bytes, true);
      C(map != null && mapA != null, $"{n}: ImageMapFloat decodes it (L16 and La16)");
      float worst = 0f, worstA = 0f;
      for (int r = 0; r < n; r += 7)
        for (int c = 0; c < n; c += 5)
        {
          float x = M.PixelToWorld(c, n, T) / T + 0.5f;
          float z = M.FileRowToWorldZ(r, n, T) / T + 0.5f;
          worst = Mathf.Max(worst, Mathf.Abs(M.ValueToMetres(map!.GetValue(x, z), 2f, 0.5f) - Metres(c, r)));
          worstA = Mathf.Max(worstA, Mathf.Abs(M.ValueToMetres(mapA!.GetValue(x, z), 2f, 0.5f) - Metres(c, r)));
        }
      C(worst < 0.02f, $"{n}: BC reads back every sampled height at its pixel (worst {worst * 100:0.##} cm)");
      System.Console.WriteLine($"    note: with Heightmap Alpha on (La16 decode) the worst is {worstA * 100:0.##} cm");
      // Orientation: the north-west pixel (file row 0, column 0) is at world (-10500, +10500).
      float nw = M.ValueToMetres(map!.GetValue(0f, 1f), 2f, 0.5f);
      float sw = M.ValueToMetres(map.GetValue(0f, 0f), 2f, 0.5f);
      C(Mathf.Abs(nw - Metres(0, 0)) < 0.02f && Mathf.Abs(sw - Metres(0, n - 1)) < 0.02f, $"{n}: file row 0 is the north edge on import");
      // Between two pixels the import interpolates.
      float xm = (M.PixelToWorld(10, n, T) + M.PixelToWorld(11, n, T)) * 0.5f / T + 0.5f;
      float zm = M.FileRowToWorldZ(20, n, T) / T + 0.5f;
      float mid = M.ValueToMetres(map.GetValue(xm, zm), 2f, 0.5f);
      float lo = Mathf.Min(Metres(10, 20), Metres(11, 20)), hi = Mathf.Max(Metres(10, 20), Metres(11, 20));
      C(mid >= lo - 0.02f && mid <= hi + 0.02f, $"{n}: halfway between two pixels it reads between them");
    }
  }

  public static void EightBitRoundTrip(string work)
  {
    Section("8-bit masks through ImageMapFloat");
    int n = 64;
    var px = new L8[n * n];
    for (int i = 0; i < px.Length; i++)
      px[i] = new L8((byte)(i % 256));
    var path = Path.Combine(work, "lavamap.png");
    WorldExportPng.SaveL8(path, px, n);
    var map = ImageMapFloat.Create(File.ReadAllBytes(path), false)!;
    bool ok = true;
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        float v = map.GetValue(M.PixelToWorld(c, n, T) / T + 0.5f, M.FileRowToWorldZ(r, n, T) / T + 0.5f);
        if (Mathf.Abs(v - px[r * n + c].PackedValue / 255f) > 1e-3f) ok = false;
      }
    C(ok, "an 8-bit grey map reads back as byte / 255 at every pixel");
  }

  public static void BiomeRoundTrip(string work)
  {
    Section("biome map through ImageMapBiome");
    int n = 300;
    var table = ImageMapBiome.DefaultColorTable();
    Heightmap.Biome Expected(int col, int row) =>
      col % 50 < 5 ? Heightmap.Biome.Swamp
      : row < n / 3 ? Heightmap.Biome.Mountain
      : row < 2 * n / 3 ? Heightmap.Biome.Meadows
      : col < n / 2 ? Heightmap.Biome.Ocean : Heightmap.Biome.AshLands;
    var px = new Rgb24[n * n];
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        var col = table[Expected(c, r)];
        px[r * n + c] = new Rgb24(col.r, col.g, col.b);
      }
    var path = Path.Combine(work, "biomemap.png");
    WorldExportPng.SaveRgb24(path, px, n);
    var map = ImageMapBiome.Create(File.ReadAllBytes(path), ImageMapBiome.DefaultColors, path)!;
    bool ok = true;
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
        if (map.GetValue(M.PixelToWorld(c, n, T) / T + 0.5f, M.FileRowToWorldZ(r, n, T) / T + 0.5f) != Expected(c, r))
          ok = false;
    C(ok, "every pixel decodes to its biome, north third Mountain on top");
    C(map.GetValue(0.5f, 0.99f) == Heightmap.Biome.Mountain && map.GetValue(0.9f, 0.01f) == Heightmap.Biome.AshLands, "north is Mountain, south-east AshLands");
  }

  public static void AltBiomeRoundTrip(string work)
  {
    Section("alt-biome map through ImageMapAltBiome");
    // The real colour assignment, through reflection on the private nested types.
    var altClass = typeof(WorldExport).GetNestedType("AltClass", BindingFlags.NonPublic)!;
    var job = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(altClass))!;
    var keys = new[] { "Dark Meadows", "Lantern + Mushroom", "!Wolf Mountain", "Wolf Mountain", "Some Modded Alt", "Troll Black Forest" };
    foreach (var key in keys)
      list.Add(Activator.CreateInstance(altClass, key, key.Split(new[] { " + " }, StringSplitOptions.None).ToList()));
    job.GetMethod("AssignAltColours", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [list]);
    var colours = list.Cast<object>().Select(o => (Color32)altClass.GetField("Color")!.GetValue(o)!).ToList();
    C(colours[0].r == 0x2D && colours[0].g == 0x46 && colours[0].b == 0x13, "a single alt biome keeps its palette colour (Dark Meadows #2D4613)");
    C(colours[3].r == 0x93 && colours[3].g == 0xC7 && colours[3].b == 0xBC, "Wolf Mountain keeps #93C7BC");
    bool far = true;
    for (int i = 0; i < colours.Count; i++)
    {
      if (ImageMapAltBiome.ColorDistanceSq(colours[i], new Color32(0, 0, 0, 255)) <= 1600) far = false;
      for (int j = i + 1; j < colours.Count; j++)
        if (ImageMapAltBiome.ColorDistanceSq(colours[i], colours[j]) <= 1600) far = false;
    }
    C(far, "every class colour is more than 40 RGB units from the others and from black");

    int n = 2048;
    var px = new Rgb24[n * n];
    // Four quadrants: NW Dark Meadows, NE Lantern + Mushroom, SW !Wolf Mountain, SE unplanted.
    int Cls(int c, int r) => r < n / 2 ? (c < n / 2 ? 0 : 1) : (c < n / 2 ? 2 : -1);
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        int k = Cls(c, r);
        if (k >= 0) px[r * n + c] = new Rgb24(colours[k].r, colours[k].g, colours[k].b);
      }
    var path = Path.Combine(work, "altbiomemap.png");
    WorldExportPng.SaveRgb24(path, px, n);
    var legend = new List<string> { "# header", "# more header" };
    for (int k = 0; k < 3; k++)
      legend.Add($"{keys[k]}: {colours[k].r:X2}{colours[k].g:X2}{colours[k].b:X2}   # 1 region(s)");
    WorldExportPng.WriteText(Path.Combine(work, "altbiomemap.txt"), legend);
    var map = ImageMapAltBiome.Create(path)!;
    C(map.Classes.Count == 4, $"three classes decoded (got {map.Classes.Count - 1})");
    C(map.Classes[1].Names == "Dark Meadows" && map.Classes[2].Names == "Lantern + Mushroom" && map.Classes[3].Names == "!Wolf Mountain",
      $"legend names and force flags survive ({string.Join(" | ", map.Classes.Skip(1).Select(c => c.Names))})");
    // Sample on the game's 12 m grid, as AltBiomeControl.SampleKeys does.
    bool ok = true;
    for (int gy = 0; gy < 2048; gy += 3)
      for (int gx = 0; gx < 2048; gx += 3)
      {
        float wx = (gx - 1024) * 12f + 6f, wz = (gy - 1024) * 12f + 6f;
        if (Mathf.Abs(wx) > 10500f || Mathf.Abs(wz) > 10500f) continue;
        int c = M.WorldToPixel(wx, n, T), r = M.WorldZToFileRow(wz, n, T);
        int want = Cls(c, r) + 1;
        if (map.GetClass(wx / T + 0.5f, wz / T + 0.5f) != want) ok = false;
      }
    C(ok, "every grid point samples the class painted at its nearest pixel");
    C(!LogHandler.Lines.Any(l => l.Contains("match no legend colour")), "no pixel failed to match a legend colour");
  }

  public static void PaintRoundTrip(string work)
  {
    Section("paint map through ImageMapPaint");
    int n = 16;
    var px = new Rgb24[n * n];
    for (int i = 0; i < px.Length; i++)
      px[i] = new Rgb24(0, (byte)(77 + i % 76), 0);
    var path = Path.Combine(work, "paintmap.png");
    WorldExportPng.SaveRgb24(path, px, n);
    var map = ImageMapPaint.Create(File.ReadAllBytes(path), "")!;
    bool ok = true;
    float worst = 0f;
    int shown = 0;
    for (int r = 0; r < n; r++)
      for (int c = 0; c < n; c++)
      {
        if (!map.TryGetValue(M.PixelToWorld(c, n, T) / T + 0.5f, M.FileRowToWorldZ(r, n, T) / T + 0.5f, out var col)) { ok = false; continue; }
        float err = Mathf.Abs(col.g - px[r * n + c].G / 255f);
        worst = Mathf.Max(worst, err);
        if (err > 1f / 255f + 1e-4f || col.r != 0f || col.a != 1f) { ok = false; if (shown++ < 5) System.Console.WriteLine($"    paint ({c},{r}) got {col} want g {px[r * n + c].G / 255f}"); }
      }
    C(ok, $"paint reads back its colour within one 8-bit step (worst {worst * 255f:0.##} steps: BC's Color32.Lerp truncates), alpha 1 (so the lava/moss alpha is left alone)");
  }

  public static void LocationMath()
  {
    Section("location pixels");
    foreach (int n in new[] { 1024, 4096 })
    {
      var rnd = new System.Random(n);
      float worst = 0f;
      for (int i = 0; i < 20000; i++)
      {
        float x = (float)(rnd.NextDouble() * 21000 - 10500), z = (float)(rnd.NextDouble() * 21000 - 10500);
        int col = M.WorldToLocationPixel(x, n, T), mapRow = M.WorldToLocationPixel(z, n, T);
        int fileRow = M.FlipRow(mapRow, n);
        // ImageMapLocation: the image is flipped on load, a one-pixel blob at (fx, fy) is placed at (fx / Size, fy / Size).
        int fy = n - 1 - fileRow;
        float ix = (col / (float)n - 0.5f) * T, iz = (fy / (float)n - 0.5f) * T;
        worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(ix - x), Mathf.Abs(iz - z)));
      }
      C(worst <= T / n + 1e-2f, $"{n}: a location imports within {worst:0.##} m of where it was (limit {T / n:0.##} m, half that away from the east/north edge)");
    }
  }

  public static void LocationColours()
  {
    Section("location colours");
    var job = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var method = job.GetMethod("LocationColours", BindingFlags.NonPublic | BindingFlags.Static)!;
    var names = new List<string> { "Crypt2", "Crypt3", "Eikthyrnir", "ModdedThing", "StartTemple", "WoodHouse1", "WoodHouse10", "WoodHouse2" };
    var colours = (Dictionary<string, Rgb24>)method.Invoke(null, [names])!;
    C(colours["StartTemple"].Equals(new Rgb24(255, 0, 0)) && colours["Eikthyrnir"].Equals(new Rgb24(255, 153, 0)), "unique default colours are kept");
    C(colours["WoodHouse1"].Equals(new Rgb24(109, 158, 235)) && colours["Crypt2"].Equals(new Rgb24(255, 242, 204)), "a shared default colour goes to its first owner in the default legend");
    C(colours.Values.Distinct().Count() == colours.Count, "every location type has its own colour");
    C(colours.Values.All(c => c.R + c.G + c.B > 0), "no location is black (the background)");
    var alone = (Dictionary<string, Rgb24>)method.Invoke(null, [new List<string> { "WoodHouse10", "ModdedThing" }])!;
    C(alone["WoodHouse10"].Equals(colours["WoodHouse10"]) && alone["ModdedThing"].Equals(colours["ModdedThing"]), "hash colours do not depend on which other types are present");
  }

  public static void Commands()
  {
    Section("bc_export options");
    C(WorldExportCommands.TryParse("2048 amount=3 sealevel=0.4 heatscale=20 edge=off forest=exact noforest nolava paint perpoint", out var o, out var e), "options parse: " + e);
    C(o.Size == 2048 && o.HeightmapAmount == 3f && o.SeaLevel == 0.4f && o.HeatScale == 20f && o.EdgeDropoff == false && o.ForestExact
      && !o.Forest && !o.Lava && o.Paint && !o.ZoneBlend && o.Heightmap && o.Biomes, "every option lands in its field");
    C(WorldExportCommands.TryParse("", out var d, out _) && d.Size == 4096 && d.HeightmapAmount == 2f && d.SeaLevel == 0.5f && d.EdgeDropoff == null && d.Paint && d.Preset,
      "defaults: 4096, amount 2, sea level 0.5, edge as the world, paint on, New World preset on (0.9.0)");
    C(WorldExportCommands.TryParse("nopaint nopreset", out var np, out _) && !np.Paint && !np.Preset && np.Heightmap, "nopaint and nopreset switch those two off");
    C(WorldExportCommands.TryParse("paint preset", out var pp, out _) && pp.Paint && pp.Preset, "paint and preset are still accepted");
    C(d.ToString().Contains("New World preset") && np.ToString().Contains("no preset"), "the options' text says whether a preset is made");
    C(!WorldExportCommands.TryParse("bogus", out _, out var e2) && e2.Contains("bogus"), "unknown words are refused");
    C(!WorldExportCommands.TryParse("amount=abc", out _, out _), "unreadable values are refused");
    var bad = WorldExport.Options.Default();
    bad.Size = 100000;
    C(typeof(WorldExport.Options).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bad, null) is string, "an out-of-range size is refused");
  }

  public static void Json()
  {
    Section("manifest writer");
    var obj = new WorldExportJson.Obj
    {
      { "s", "quote \" backslash \\ newline \n tab \t ctrl \u0001" },
      { "f", 1.5f },
      { "nan", float.NaN },
      { "l", 123456789012L },
      { "b", true },
      { "n", null },
      { "arr", new object[] { 1f, 2f } },
      { "files", new List<string> { "a.png", "sources/b.png" } },
      { "nested", new WorldExportJson.Obj { { "x", 1 } } },
      { "empty", new List<string>() },
    };
    var text = WorldExportJson.Write(obj);
    try
    {
      using var doc = System.Text.Json.JsonDocument.Parse(text);
      var root = doc.RootElement;
      C(root.GetProperty("s").GetString() == "quote \" backslash \\ newline \n tab \t ctrl \u0001", "strings escape and parse back");
      C(root.GetProperty("nan").ValueKind == System.Text.Json.JsonValueKind.Null && root.GetProperty("f").GetDouble() == 1.5, "NaN writes null, floats write as numbers");
      C(root.GetProperty("files").GetArrayLength() == 2 && root.GetProperty("nested").GetProperty("x").GetInt32() == 1, "arrays and nested objects");
    }
    catch (Exception ex)
    {
      C(false, "manifest JSON parses: " + ex.Message + "\n" + text);
    }
  }

  // export.cfg, README.txt and manifest.json from a Job built without its constructor (which needs a live world).
  public static void ConfigAndReadme(string work)
  {
    Section("export.cfg, README.txt, manifest.json");
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    object Make(bool heights, bool locations, bool full, bool edge, bool forestExact)
    {
      var job = RuntimeHelpers.GetUninitializedObject(jobType);
      void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
      var o = WorldExport.Options.Default();
      o.ForestExact = forestExact;
      Set("O", o);
      Set("Dir", Path.Combine(work, "export-test"));
      // World(name, seed) draws a UID from UnityEngine.Random (a native call); the empty constructor does not.
      var world = new World { m_name = "Test World", m_seedName = "SeedName", m_seed = 12345 };
      Set("World", world);
      Set("Settings", new BC.BetterContinentsSettings());
      Set("Size", 4096);
      Set("Total", 21000f);
      Set("WorldR", 10000f);
      Set("TotalR", 10500f);
      Set("Sla", 0f);
      Set("EdgeDropoff", edge);
      Set("ZoneBlend", true);
      Set("Role", "host");
      Set("ForestScaleText", "0.5");
      Set("SourceForestAmount", 0.5f);
      Set("WorldSizeSetting", 10000f);
      Set("EdgeSizeSetting", 500f);
      Set("AltGrid", BC.AltBiomeGridMode.Vanilla);
      Set("Clock", System.Diagnostics.Stopwatch.StartNew());
      Set("Started", new DateTime(2026, 9, 24, 12, 0, 0));
      Set("Written", new List<string> { "heightmap.png", "biomemap.png", "biomemap.txt", "forestmap.png", "heatmap.png", "altbiomemap.png", "altbiomemap.txt", "locationmap.png", "locationmap.txt", "lavamap.png", "mossmap.png" });
      Set("Notes", new List<string> { "a note" });
      Set("Tracked", new List<string>());
      Set("BiomePixels", new long[10]);
      Set("HeightsWritten", heights);
      Set("ForestWritten", true);
      Set("HeatWritten", true);
      Set("AltBiomesWritten", true);
      Set("LocationsWritten", locations);
      Set("LocationsFull", full);
      Set("LocationsGenerated", true);
      Set("MinMetres", -14f);
      Set("MaxMetres", 310f);
      return job;
    }
    List<string> Call(object job, string name) => (List<string>)jobType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;

    var job1 = Make(heights: true, locations: true, full: true, edge: true, forestExact: false);
    var cfg = Call(job1, "ConfigLines");
    // Paste after an existing config that says otherwise, then read it with BepInEx itself.
    var file = Path.Combine(work, "BetterContinents.cfg");
    var before = new List<string>
    {
      "[00 BetterContinents.Debug]", "Directory = /somewhere/else", "Override version = 6",
      "[01 BetterContinents.Global]", "Map Edge Drop-off = false", "Mountains Allowed At Center = false", "Rivers = true",
      "[02 BetterContinents.Heightmap]", "Heightmap Amount = 1", "Heightmap Blend = 0.3",
      "[04 BetterContinents.Forest]", "Forestmap Multiply = 1", "Forestmap Add = 1",
      "[07 BetterContinents.Misc]", "SelectedPreset = Vanilla",
      "[08 BetterContinents.AltBiomes]", "Mode = Random",
    };
    File.WriteAllLines(file, before.Concat(cfg));
    var cf = new BepInEx.Configuration.ConfigFile(file, false);
    T0 Get<T0>(string section, string key, T0 dflt) => cf.Bind(section, key, dflt).Value;
    C(Get("00 BetterContinents.Debug", "Directory", "") == Path.Combine(work, "export-test").Replace('\\', '/'), "Directory = the export folder (the later line wins)");
    C(Get("00 BetterContinents.Debug", "Override version", "x") == "", "Override version is empty");
    C(Get("00 BetterContinents.Debug", "Enabled", false), "Enabled = true");
    C(Get("01 BetterContinents.Global", "Map Edge Drop-off", false) && Get("01 BetterContinents.Global", "Mountains Allowed At Center", false)
      && !Get("01 BetterContinents.Global", "Rivers", true) && Get("01 BetterContinents.Global", "Skip Default Locations", false), "Global: edge drop-off on, mountains at centre allowed, rivers off, skip default locations");
    C(Get("01 BetterContinents.Global", "Sea Level Adjustment", 0f) == 0.5f && Get("01 BetterContinents.Global", "World Size", 0f) == 10000f && Get("01 BetterContinents.Global", "Edge Size", 0f) == 500f, "sea level 0.5, world size 10000, edge 500");
    C(Get("02 BetterContinents.Heightmap", "Heightmap Amount", 0f) == 2f && Get("02 BetterContinents.Heightmap", "Heightmap Blend", 0f) == 1f
      && Get("02 BetterContinents.Heightmap", "Heightmap Add", 1f) == 0f && Get("02 BetterContinents.Heightmap", "Heightmap Mask", 1f) == 0f
      && Get("02 BetterContinents.Heightmap", "Heightmap Override All", false) && !Get("02 BetterContinents.Heightmap", "Heightmap Alpha", true), "Heightmap: amount 2, blend 1, add 0, mask 0, override all, no alpha");
    C(Get("04 BetterContinents.Forest", "Forestmap Multiply", 1f) == 0f && Get("04 BetterContinents.Forest", "Forestmap Add", 0f) == 1f
      && Get("04 BetterContinents.Forest", "Forest Amount", 0f) == 0.5f && Get("04 BetterContinents.Forest", "Forest Scale", 0f) == 0.5f, "Forest: multiply 0, add 1, amount 0.5, scale 0.5");
    C(!Get("05 BetterContinents.StartPosition", "Override Start Position", true), "no start position override (the temple is in the location map)");
    C(Get("06 BetterContinents.Maps", "Heatmap Scale", 0f) == 10f, "Heatmap Scale 10");
    C(Get("07 BetterContinents.Misc", "SelectedPreset", "") == "From Config", "SelectedPreset = From Config");
    C(Get("08 BetterContinents.AltBiomes", "Mode", "") == "PlantedOnly" && Get("08 BetterContinents.AltBiomes", "Grid", "") == "Vanilla", "alt biomes PlantedOnly, grid as the world");
    C(Get("03 BetterContinents.Biomemap", "Biome precision", -1) == 0, "a world without Better Continents exports Biome precision 0");

    // Biome precision is copied from the world it was exported from (and clamped as the game uses it).
    var precise = Make(heights: true, locations: true, full: true, edge: true, forestExact: false);
    jobType.GetField("Settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(precise, new BC.BetterContinentsSettings { EnabledForThisWorld = true, BiomePrecision = 3 });
    var cfgP = Call(precise, "ConfigLines");
    var fileP = Path.Combine(work, "BetterContinents-precision.cfg");
    File.WriteAllLines(fileP, new[] { "[03 BetterContinents.Biomemap]", "Biome precision = 5" }.Concat(cfgP));
    var cfP = new BepInEx.Configuration.ConfigFile(fileP, false);
    C(cfP.Bind("03 BetterContinents.Biomemap", "Biome precision", -1).Value == 3 && cfgP.Count(l => l.StartsWith("Biome precision")) == 1, "a world with Biome precision 3 exports Biome precision = 3 (the later line wins)");
    C(Call(precise, "ReadmeLines").Count(l => l.Contains("Biome precision (3)")) == 1, "README.txt names the exported Biome precision");

    var client = Make(heights: true, locations: true, full: false, edge: false, forestExact: true);
    var cfg2 = Call(client, "ConfigLines");
    C(cfg2.Contains("Skip Default Locations = false") && cfg2.Contains("Map Edge Drop-off = false") && cfg2.Contains("Forestmap Multiply = 1"), "a client export keeps default locations, and the edge / forest options follow");
    var noHeights = Call(Make(heights: false, locations: false, full: false, edge: true, forestExact: false), "ConfigLines");
    C(!noHeights.Any(l => l.StartsWith("Heightmap Amount")) && !noHeights.Any(l => l.StartsWith("Rivers")), "without a heightmap no heightmap keys are written");

    var readme = Call(job1, "ReadmeLines");
    C(readme.Count >= 50 && readme.Count <= 110, $"README.txt is {readme.Count} lines (50-110)");
    C(readme.Any(l => l.Contains("MAKE A NEW WORLD FROM THIS EXPORT")) && readme.Any(l => l.Contains("  A. ")) && readme.Any(l => l.Contains("  B. bc_import"))
      && readme.Any(l => l.Contains("  C. The config")) && readme.Any(l => l.Contains("WHAT EACH FILE IS")), "README.txt gives the three ways (preset, bc_import, config) and explains every file");
    C(!readme.Any(l => l.Contains("Quit the game")), "README.txt no longer says to quit the game: the config is read again while it runs (LiveConfig)");
    var header = Call(job1, "ConfigHeader");
    C(header.All(l => l.StartsWith("##")) && header.Any(l => l.Contains("WHAT THIS FILE IS")) && header.Any(l => l.Contains("bc_import")), "export.cfg starts with a comment header that says what to do with it");
    System.Console.WriteLine("    ---- README.txt ----");
    foreach (var line in readme) System.Console.WriteLine("    | " + line);
    System.Console.WriteLine("    ---- export.cfg ----");
    foreach (var line in cfg) System.Console.WriteLine("    | " + line);
    var manifest = (string)jobType.GetMethod("ManifestJson", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job1, null)!;
    try
    {
      using var doc = System.Text.Json.JsonDocument.Parse(manifest);
      var r = doc.RootElement;
      string[] required = ["format", "modVersion", "gameVersion", "worldName", "seed", "sampledOn", "size", "totalSize", "metresPerPixel", "heightmapAmount", "seaLevel", "waterlineValue", "floorMetres", "ceilingMetres", "clippedLow", "clippedHigh", "files", "notes"];
      var missing = required.Where(k => !r.TryGetProperty(k, out _)).ToList();
      C(missing.Count == 0, "manifest has every required key" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
      C(r.GetProperty("format").GetString() == "bc-export/1" && Mathf.Abs((float)r.GetProperty("waterlineValue").GetDouble() - 0.15f) < 1e-6f
        && System.Math.Abs(r.GetProperty("floorMetres").GetDouble() + 30) < 1e-3 && System.Math.Abs(r.GetProperty("ceilingMetres").GetDouble() - 370) < 1e-3, "format bc-export/1, waterline 0.15, floor -30 m, ceiling 370 m");
      System.Console.WriteLine("    ---- manifest.json ----");
      foreach (var line in manifest.Split('\n')) System.Console.WriteLine("    | " + line);
    }
    catch (Exception ex)
    {
      C(false, "manifest parses: " + ex.Message);
    }
  }
}

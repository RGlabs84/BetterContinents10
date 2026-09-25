// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;

namespace ExportTest;

// TerrainRow against an independent copy of HeightmapBuilder.Build's non-LOD blend, with the generator replaced by
// synthetic biome and height functions (the real ones need Unity's native Perlin noise).
internal static class TerrainTest
{
  public static Heightmap.Biome Biome(float x, float z)
  {
    // Borders that do not line up with the 64 m zones, three biomes.
    int k = ((int)Math.Floor((x + 37f) / 211f) + (int)Math.Floor((z - 11f) / 157f)) % 3;
    if (k < 0) k += 3;
    return k == 0 ? Heightmap.Biome.Meadows : k == 1 ? Heightmap.Biome.AshLands : Heightmap.Biome.Mistlands;
  }

  public static float Height(Heightmap.Biome b, float x, float z, out Color mask)
  {
    switch (b)
    {
      case Heightmap.Biome.AshLands:
        mask = new Color(0f, 0f, 0f, 0.5f + 0.4f * Mathf.Sin(x * 0.01f));
        return 100f + 0.002f * z;
      case Heightmap.Biome.Mistlands:
        mask = new Color(0f, 0f, 0f, 0.25f + 0.2f * Mathf.Cos(z * 0.02f));
        return 20f + 0.003f * x;
      default:
        mask = new Color(0f, 0.3f + 0.2f * Mathf.Sin(z * 0.005f), 0f, 0f);
        return 50f + 0.001f * x;
    }
  }

  static bool GetBiomePrefix(float wx, float wy, ref Heightmap.Biome __result)
  {
    __result = Biome(wx, wy);
    return false;
  }

  static bool GetBiomeHeightPrefix(Heightmap.Biome biome, float wx, float wy, out Color mask, ref float __result)
  {
    __result = Height(biome, wx, wy, out mask);
    return false;
  }

  public static void Run()
  {
    System.Console.WriteLine("== terrain row (zone-corner blend) against HeightmapBuilder");
    var harmony = new Harmony("export-test");
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), [typeof(float), typeof(float), typeof(float), typeof(bool)]),
      prefix: new HarmonyMethod(typeof(TerrainTest), nameof(GetBiomePrefix)));
    harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight)),
      prefix: new HarmonyMethod(typeof(TerrainTest), nameof(GetBiomeHeightPrefix)));
    try
    {
      var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
      var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
      var job = RuntimeHelpers.GetUninitializedObject(jobType);
      void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
      object Get(string name) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!;
      const int n = 700;
      const float T = 21000f;
      var o = WorldExport.Options.Default();
      Set("O", o);
      Set("Size", n);
      Set("Total", T);
      Set("WorldR", 10000f);
      Set("TotalR", 10500f);
      Set("Sla", 0f);
      Set("EdgeDropoff", true);
      Set("ZoneBlend", true);
      Set("Wg", wg);
      int cornerMin = M.ZoneIndex(M.PixelToWorld(0, n, T));
      int cornerCount = M.ZoneIndex(M.PixelToWorld(n - 1, n, T)) + 2 - cornerMin;
      Set("cornerMin", cornerMin);
      Set("cornerCount", cornerCount);
      var corners = new Heightmap.Biome[cornerCount * cornerCount];
      var cornerRow = jobType.GetMethod("CornerRow", BindingFlags.NonPublic | BindingFlags.Instance)!;
      for (int r = 0; r < cornerCount; r++)
        cornerRow.Invoke(job, [r, corners]);
      var heights = new L16[n * n];
      var lava = new L8[n * n];
      var moss = new L8[n * n];
      var paint = new Rgb24[n * n];
      var statsType = typeof(WorldExport).GetNestedType("TerrainRowStats", BindingFlags.NonPublic)!;
      var stats = Array.CreateInstance(statsType, n);
      var terrainRow = jobType.GetMethod("TerrainRow", BindingFlags.NonPublic | BindingFlags.Instance)!;
      for (int r = 0; r < n; r++)
        terrainRow.Invoke(job, [r, corners, heights, lava, moss, paint, stats]);

      int bad = 0, blended = 0, lavaBad = 0, mossBad = 0, paintBad = 0;
      for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
        {
          float wx = M.PixelToWorld(c, n, T), wz = M.FileRowToWorldZ(r, n, T);
          // HeightmapBuilder.Build for the zone this point lies in, evaluated at the point.
          var zonePos = ZoneSystem.GetZonePos(new Vector2s((short)Mathf.FloorToInt((wx + 32f) / 64f), (short)Mathf.FloorToInt((wz + 32f) / 64f)));
          float x0 = zonePos.x - 32f, z0 = zonePos.z - 32f;
          var b0 = Biome(x0, z0);
          var b1 = Biome((float)((double)x0 + 64.0), z0);
          var b2 = Biome(x0, (float)((double)z0 + 64.0));
          var b3 = Biome((float)((double)x0 + 64.0), (float)((double)z0 + 64.0));
          float t = DUtils.SmoothStep(0f, 1f, (float)((wz - (double)z0) / 64.0));
          float t2 = DUtils.SmoothStep(0f, 1f, (float)((wx - (double)x0) / 64.0));
          float h;
          Color mask;
          Color[] m = new Color[4];
          if (b2 == b0 && b1 == b0 && b3 == b0)
            h = Height(b0, wx, wz, out mask);
          else
          {
            blended++;
            float h0 = Height(b0, wx, wz, out m[0]), h1 = Height(b1, wx, wz, out m[1]), h2 = Height(b2, wx, wz, out m[2]), h3 = Height(b3, wx, wz, out m[3]);
            h = DUtils.Lerp(DUtils.Lerp(h0, h1, t2), DUtils.Lerp(h2, h3, t2), t);
            mask = Color.Lerp(Color.Lerp(m[0], m[1], t2), Color.Lerp(m[2], m[3], t2), t);
          }
          float fh = h / 200f;
          float d = Mathf.Sqrt(wx * wx + wz * wz);
          if (d > 10000f)
            M.TryUndoEdgeDropoff(fh, d, 10000f, 10500f, out fh);
          var want = M.ValueToUShort((fh + 0.15f) / 2f, out _);
          int i = r * n + c;
          if (heights[i].PackedValue != want) bad++;
          var corners4 = new[] { b0, b1, b2, b3 };
          int ai = Array.IndexOf(corners4, Heightmap.Biome.AshLands);
          byte wantLava = 0;
          if (ai >= 0) { Height(Heightmap.Biome.AshLands, wx, wz, out var am); wantLava = M.ValueToByte(am.a); }
          if (lava[i].PackedValue != wantLava) lavaBad++;
          int mi = Array.IndexOf(corners4, Heightmap.Biome.Mistlands);
          byte wantMoss = 0;
          if (mi >= 0) { Height(Heightmap.Biome.Mistlands, wx, wz, out var mm); wantMoss = M.ValueToByte(mm.a); }
          if (moss[i].PackedValue != wantMoss) mossBad++;
          if (paint[i].G != M.ValueToByte(mask.g)) paintBad++;
        }
      Program.C(bad == 0, $"every height pixel equals HeightmapBuilder's blend at that point ({bad} differ, {blended} of {n * n} pixels in mixed zones)");
      Program.C(blended > n * n / 20, "the synthetic map exercises the mixed-corner blend");
      Program.C(lavaBad == 0 && mossBad == 0, $"lava / moss hold the Ashlands / Mistlands corner's own alpha ({lavaBad} / {mossBad} differ)");
      Program.C(paintBad == 0, $"paint holds the blended mask ({paintBad} differ)");
      var lows = stats.Cast<object>().Sum(s => (long)statsType.GetField("Low")!.GetValue(s)!);
      var ring = stats.Cast<object>().Sum(s => (long)statsType.GetField("Ring")!.GetValue(s)!);
      System.Console.WriteLine($"    clipped low inside {lows}, edge ring clipped {ring}");
    }
    finally
    {
      // No UnpatchAll: restoring the originals touches Unity internals offline, and the process ends here anyway.
    }
  }
}

// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using M = BetterContinents.WorldExportMath;

namespace ExportTest;

// Job.PlaceLocations against a copy of ImageMapLocation's decode (4-connected flood fill of one colour, one location
// per blob at a pixel of it, position = pixel / size).
internal static class LocationTest
{
  public static void Run()
  {
    System.Console.WriteLine("== location placement");
    const int n = 1024;
    const float T = 21000f;
    var jobType = typeof(WorldExport).GetNestedType("Job", BindingFlags.NonPublic)!;
    var recType = typeof(WorldExport).GetNestedType("LocationRecord", BindingFlags.NonPublic)!;
    var job = RuntimeHelpers.GetUninitializedObject(jobType);
    void Set(string name, object value) => jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
    int Get(string name) => (int)jobType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!;
    Set("Size", n);
    Set("Total", T);

    var rnd = new System.Random(7);
    var records = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(recType))!;
    var truth = new List<(string Name, float X, float Z, int ZX, int ZZ)>();
    var usedZones = new HashSet<(int, int)>();
    void Add(string name, float x, float z)
    {
      var zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
      if (!usedZones.Add((zone.x, zone.y))) return;   // the game keeps one location per zone
      records.Add(Activator.CreateInstance(recType, name, x, z, (int)zone.x, (int)zone.y));
      truth.Add((name, x, z, zone.x, zone.y));
    }
    string[] names = ["StartTemple", "Eikthyrnir", "WoodHouse1", "WoodHouse2", "Crypt2", "Crypt3", "ModdedCamp"];
    // Scattered locations, placed well inside their zones like the game does.
    for (int i = 0; i < 3000; i++)
    {
      int zx = rnd.Next(-150, 150), zz = rnd.Next(-150, 150);
      Add(names[rnd.Next(names.Length)], zx * 64f + (float)(rnd.NextDouble() * 40 - 20), zz * 64f + (float)(rnd.NextDouble() * 40 - 20));
    }
    // Crowded: the same type in neighbouring zones, a few metres apart across the zone border.
    for (int i = 0; i < 40; i++)
    {
      float bx = -4000f + i * 200f, bz = -2000f;
      Add("WoodHouse1", bx + 31f, bz);
      Add("WoodHouse1", bx + 34f, bz);
    }
    // Off the map square.
    records.Add(Activator.CreateInstance(recType, "Crypt2", 12000f, 0f, 188, 0));

    var pixels = new Rgb24[n * n];
    var legend = new List<string>();
    jobType.GetMethod("PlaceLocations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, [records, pixels, legend]);
    int written = Get("LocationWritten"), nudged = Get("LocationNudged"), dropped = Get("LocationDropped"), outside = Get("LocationOutside");
    System.Console.WriteLine($"    {truth.Count} locations: {written} written, {nudged} nudged, {dropped} dropped, {outside} outside");
    Program.C(outside == 1, "a location off the map square is skipped");
    Program.C(written + dropped == truth.Count && dropped == 0, "every location on the map gets a pixel");

    // Decode like ImageMapLocation: flip, flood-fill 4-connected same colour, position = pixel / size.
    var colourOf = legend.Select(l => l.Split(':')).ToDictionary(p => p[1].Trim(), p => p[0].Trim());
    Program.C(colourOf.Count == legend.Count, "every legend colour is unique");
    var seen = new bool[n * n];
    var found = new List<(string Name, float X, float Z, int Size)>();
    for (int y = 0; y < n; y++)
      for (int x = 0; x < n; x++)
      {
        var p = pixels[(n - 1 - y) * n + x];
        if (p.R == 0 && p.G == 0 && p.B == 0 || seen[y * n + x]) continue;
        var queue = new Queue<(int, int)>();
        queue.Enqueue((x, y));
        seen[y * n + x] = true;
        int size = 0;
        while (queue.Count > 0)
        {
          var (qx, qy) = queue.Dequeue();
          size++;
          foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
          {
            int ax = qx + dx, ay = qy + dy;
            if (ax < 0 || ay < 0 || ax >= n || ay >= n || seen[ay * n + ax]) continue;
            var q = pixels[(n - 1 - ay) * n + ax];
            if (q.R == p.R && q.G == p.G && q.B == p.B) { seen[ay * n + ax] = true; queue.Enqueue((ax, ay)); }
          }
        }
        found.Add((colourOf[$"{p.R},{p.G},{p.B}"], (x / (float)n - 0.5f) * T, (y / (float)n - 0.5f) * T, size));
      }
    Program.C(found.All(f => f.Size == 1), "every location is a blob of exactly one pixel");
    Program.C(found.Count == written, $"the decode finds one location per written pixel ({found.Count})");
    // Match each truth location to the decoded one of its name nearest to it.
    int far = 0, wrongZone = 0;
    float worst = 0f;
    var pool = found.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());
    foreach (var t in truth)
    {
      var list = pool[t.Name];
      var best = list.OrderBy(f => (f.X - t.X) * (f.X - t.X) + (f.Z - t.Z) * (f.Z - t.Z)).First();
      list.Remove(best);
      float d = Mathf.Sqrt((best.X - t.X) * (best.X - t.X) + (best.Z - t.Z) * (best.Z - t.Z));
      worst = Mathf.Max(worst, d);
      if (d > 3f * T / n) far++;
      var z = ZoneSystem.GetZone(new Vector3(best.X, 0f, best.Z));
      if (z.x != t.ZX || z.y != t.ZZ) wrongZone++;
    }
    Program.C(far == 0, $"every location imports within 3 pixels of where it was (worst {worst:0.#} m, pixel {T / n:0.#} m)");
    Program.C(wrongZone == 0, $"every location imports into its own zone ({wrongZone} do not)");
    Program.C(nudged > 0, "the crowded pairs were nudged apart rather than merged");
  }
}

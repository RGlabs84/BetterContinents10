// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownPaint.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// THE LAYER'S GROUND PAINT (section 8.2, U2): a compiler's streets paved, its dirt roads dirt, gardens cultivated, and every floor and interior
// paved, so no grass grows indoors or in the streets. A zone brings its 64 x 64 cells of 1 m; they go into the terrain's own mask, the one
// Valheim's hoe and cultivator paint: red is dirt, green cultivated, blue paved (Heightmap.m_paintMask*), and grass stops where any of them
// is over half (Heightmap.IsCleared). Not Better Continents' paint map: at 8,175 px it would hold about 535 MB for the whole world.
//
// A zone's terrain keeps its base mask (HeightmapBuilder's m_baseMask) and copies it into the mask texture each time it regenerates, under the
// players' own paint. So the cells are painted into the base mask: when the terrain asks for its build data (a zone built after its paint was
// read), or at once with a paint-only regeneration (a zone built before it). Clients only: the machine that runs the world has no use for
// the colours of the ground.
internal static class BakedPaint
{
  /// <summary>Paint is read for the zones within this many of the reference zone: a terrain in the game's near ring, and its neighbours,
  /// whose last row and column are the next zone's first cells.</summary>
  internal const int Reach = 3;
  private const int Side = 64, Cells = Side * Side;
  private const int MostReads = 4;

  private static readonly Dictionary<ZoneKey, byte[]> Painted = new();
  // zones read (or being read) for the layer in hand, painted or not
  private static readonly HashSet<ZoneKey> Asked = [];
  private static readonly List<Heightmap> Near = [];
  private static readonly List<(ZoneKey Zone, byte[]? Cells, uint Revision)> Finished = [];
  private static int reading;
  private static uint layerRevision;
  private static BakedLayer? layerInHand;
  private static ZoneKey lastRef = new(int.MinValue, int.MinValue);

  /// <summary>How many zones have paint registered now.</summary>
  internal static int ZonesPainted => Painted.Count;

  /// <summary>A new world, or the layer gone: the paint goes.</summary>
  internal static void Clear()
  {
    Painted.Clear();
    Asked.Clear();
    lock (Finished)
      Finished.Clear();
    layerInHand = null;
    lastRef = new ZoneKey(int.MinValue, int.MinValue);
  }

  /// <summary>
  /// Main thread, every frame the layer or the reference zone may have changed: reads the paint of the zones around the reference zone
  /// that have some and are not read yet (on workers), and registers what the workers have finished. Costs nothing for a layer with no
  /// paint at all.
  /// </summary>
  internal static void Tick(BakedLayer? layer, ZoneKey reference)
  {
    if (layer == null || (layer.Flags & HeaderFlags.Paint) == 0)
    {
      if (Painted.Count > 0 || layerInHand != null)
        Clear();
      return;
    }
    if (!ReferenceEquals(layer, layerInHand))
    {
      // a new layer: what the old one painted stays until its zones are read again (BakedPaint.Changed names the ones that differ)
      layerInHand = layer;
      layerRevision = layer.Revision;
      lastRef = new ZoneKey(int.MinValue, int.MinValue);
    }
    Take();
    if (reference != lastRef)
    {
      lastRef = reference;
      for (int dz = -Reach; dz <= Reach; dz++)
        for (int dx = -Reach; dx <= Reach; dx++)
        {
          var key = new ZoneKey(reference.X + dx, reference.Z + dz);
          if (Asked.Contains(key) || !layer.TryGetZoneRow(key, out var row) || !row.HasPaint)
            continue;
          Ask(layer, row);
        }
      Forget(reference);
    }
  }

  /// <summary>A zone's paint, from a decode the caller made (a zone built for its pieces): registered if it is new.</summary>
  internal static void Offer(ZoneKey zone, byte[]? cells, uint revision)
  {
    if (revision != layerRevision && layerInHand != null)
      return;
    Asked.Add(zone);
    Register(zone, cells);
  }

  /// <summary>The layer changed: the zones that differ are read again, and a zone that lost its paint loses it from the ground built under it.</summary>
  internal static void Changed(BakedLayer? next, ZoneKey[]? changed)
  {
    if (next == null)
    {
      Clear();
      return;
    }
    if (changed == null)
    {
      foreach (var zone in new List<ZoneKey>(Painted.Keys))
        Asked.Remove(zone);
      Asked.Clear();
    }
    else
      foreach (var zone in changed)
        Asked.Remove(zone);
    layerInHand = next;
    layerRevision = next.Revision;
    lastRef = new ZoneKey(int.MinValue, int.MinValue);
    // a zone painted before and not any more has nothing to read: it is dropped now
    foreach (var zone in new List<ZoneKey>(Painted.Keys))
      if (!Asked.Contains(zone) && (!next.TryGetZoneRow(zone, out var row) || !row.HasPaint))
        Register(zone, null);
  }

  // zones far from the reference have their paint dropped, and are read again when the player comes back
  private static void Forget(ZoneKey reference)
  {
    if (Painted.Count <= 64)
      return;
    foreach (var zone in new List<ZoneKey>(Painted.Keys))
      if (Math.Abs(zone.X - reference.X) > Reach * 2 || Math.Abs(zone.Z - reference.Z) > Reach * 2)
      {
        Painted.Remove(zone);
        Asked.Remove(zone);
      }
  }

  private static void Ask(BakedLayer layer, ZoneRow row)
  {
    if (Volatile.Read(ref reading) >= MostReads)
      return;
    Asked.Add(row.Key);
    Interlocked.Increment(ref reading);
    uint revision = layer.Revision;
    ThreadPool.QueueUserWorkItem(_ =>
    {
      byte[]? cells = null;
      try
      {
        cells = layer.Decode(row).Paint;
      }
      catch (Exception e)
      {
        Log($"baked placements: could not read the paint of zone {row.Key}: {e.Message}");
      }
      lock (Finished)
        Finished.Add((row.Key, cells, revision));
      Interlocked.Decrement(ref reading);
    });
  }

  private static void Take()
  {
    List<(ZoneKey Zone, byte[]? Cells, uint Revision)>? done = null;
    lock (Finished)
    {
      if (Finished.Count > 0)
      {
        done = new List<(ZoneKey, byte[]?, uint)>(Finished);
        Finished.Clear();
      }
    }
    if (done == null)
      return;
    foreach (var (zone, cells, revision) in done)
      if (revision == layerRevision)
        Register(zone, cells);
  }

  /// <summary>Takes a zone's cells (row 0 south; null or all 0: none) and paints the terrain already built around it.</summary>
  internal static void Register(ZoneKey zone, byte[]? cells)
  {
    bool any = false;
    if (cells != null && cells.Length == Cells)
      for (int i = 0; i < cells.Length && !any; i++)
        any = cells[i] != 0;
    bool had = Painted.TryGetValue(zone, out var old);
    if (!any)
    {
      if (!had)
        return;
      Painted.Remove(zone);
      Repaint(zone, rebuild: true);
      return;
    }
    if (had && old!.AsSpan().SequenceEqual(cells))
      return;
    Painted[zone] = cells!;
    Repaint(zone, rebuild: had);
  }

  // The zone's own terrain and the ones around it (the three that share its south and west edges end in this zone's first cells).
  // rebuild: paint was taken away or changed, which painting over the base mask cannot undo: the terrain makes its build data again.
  private static void Repaint(ZoneKey zone, bool rebuild)
  {
    Near.Clear();
    Heightmap.FindHeightmap(zone.Centre, 33f, Near);   // square overlap: the zone and its eight neighbours
    int repainted = 0;
    foreach (var hmap in Near)
    {
      if (hmap == null || hmap.IsDistantLod || hmap.m_buildData == null)
        continue;
      if (rebuild)
      {
        hmap.m_buildData = null;
        hmap.Poke(0);
        repainted++;
        continue;
      }
      if (!Paint(hmap.m_buildData))
        continue;
      hmap.Poke(0, paintOnly: true);
      repainted++;
    }
    if (repainted > 0 && ClutterSystem.instance != null)
      ClutterSystem.instance.ResetGrass(zone.Centre, 40f);
  }

  // 0 none, 1 dirt, 2 paved, 3 cultivated, for the 1 m cell at world (x, z)
  internal static byte Cell(int wx, int wz)
  {
    int zx = Mathf.FloorToInt((wx + 32) / 64f), zz = Mathf.FloorToInt((wz + 32) / 64f);
    if (!Painted.TryGetValue(new ZoneKey(zx, zz), out var cells))
      return 0;
    int ix = wx - (zx * 64 - 32), iz = wz - (zz * 64 - 32);
    if (ix < 0 || iz < 0 || ix >= Side || iz >= Side)
      return 0;
    return cells[iz * Side + ix];
  }

  /// <summary>
  /// Paints a zone terrain's base mask: its pixel (l, k) holds the cell whose south-west corner is at the terrain's corner plus (l, k) metres
  /// (Heightmap.IsCleared reads it so). Keeps the mask's alpha (Mistlands and Ashlands use it). True when anything was painted.
  /// </summary>
  internal static bool Paint(HeightmapBuilder.HMBuildData? data)
  {
    if (data == null || data.m_distantLod || data.m_baseMask == null || Painted.Count == 0)
      return false;
    if (data.m_scale != 1f)
      return false;
    int n = data.m_width + 1;
    if (data.m_baseMask.Length != n * n)
      return false;
    int x0 = Mathf.RoundToInt(data.m_center.x - data.m_width * 0.5f), z0 = Mathf.RoundToInt(data.m_center.z - data.m_width * 0.5f);
    // quick out: no painted zone touches this terrain
    int za = Mathf.FloorToInt((x0 + 32) / 64f), zb = Mathf.FloorToInt((x0 + data.m_width + 32) / 64f);
    int zc = Mathf.FloorToInt((z0 + 32) / 64f), zd = Mathf.FloorToInt((z0 + data.m_width + 32) / 64f);
    bool near = false;
    for (int zx = za; zx <= zb && !near; zx++)
      for (int zz = zc; zz <= zd && !near; zz++)
        near = Painted.ContainsKey(new ZoneKey(zx, zz));
    if (!near)
      return false;
    bool painted = false;
    for (int k = 0; k < n; k++)
      for (int l = 0; l < n; l++)
      {
        byte c = Cell(x0 + l, z0 + k);
        if (c == 0)
          continue;
        ref Color m = ref data.m_baseMask[k * n + l];
        float a = m.a;
        m = c == 1 ? new Color(1f, 0f, 0f, a) : c == 3 ? new Color(0f, 1f, 0f, a) : new Color(0f, 0f, 1f, a);
        painted = true;
      }
    return painted;
  }

  // every zone terrain gets its build data here (Heightmap.Generate, main thread), so a terrain built after its paint was read is painted
  // before its first mask is made
  [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrainSync))]
  internal static class BuildDataPatch
  {
    private static void Postfix(HeightmapBuilder.HMBuildData __result)
    {
      if (Painted.Count == 0)
        return;
      try
      {
        Paint(__result);
      }
      catch (Exception e)
      {
        LogError($"baked placements: terrain paint: {e.Message}");
      }
    }
  }
}

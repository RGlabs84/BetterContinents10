// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BakedVegetation (spec 7.4, 14.1 "Mask"): VALtima's clear masks and zone flag 16 as the compiler wrote them, in-game footprints plus 4 m
// from a record's point and its prefab's reach, the cache per layer, and what the planner calls "grown".
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  public static void ServerMaskValtimaTest()
  {
    Section("mask: VALtima's zone flag 16 and clear masks, as the compiler wrote them");
    var layer = ServerValtima(nameof(ServerMaskValtimaTest));
    if (layer == null)
      return;
    int flag16 = layer.Zones.Count(r => r.NoVegetation), withMask = layer.Zones.Count(r => r.HasClearMask);
    C(flag16 == 1142, $"1,142 zones set zone flag 16 ({flag16}): the zones with records");
    C(withMask == 223 && layer.Zones.Where(r => r.HasClearMask).All(r => r.ClearMask != null && r.ClearMask.Length == 32), $"223 zones carry a clear mask of 32 bytes ({withMask}): the zones with no records");

    var random = new System.Random(77);
    int wrong = 0, covered = 0, free = 0;
    using (ServerStore(layer))
    {
      foreach (var row in layer.Zones)
      {
        if (row.NoVegetation)
        {
          // Everywhere in it, corners included.
          foreach (var (dx, dz) in new[] { (0.0, 0.0), (63.999, 0.0), (0.0, 63.999), (63.999, 63.999), (32.0, 32.0), (random.NextDouble() * 64, random.NextDouble() * 64) })
            if (!BakedVegetation.Covers(row.Key.OriginX + dx, row.Key.OriginZ + dz))
              wrong++;
          if (!BakedVegetation.IsFootprint((float)(row.Key.OriginX + 10), (float)(row.Key.OriginZ + 10)))
            wrong++;
          covered++;
          continue;
        }
        if (!row.HasClearMask)
          continue;
        // Every cell of the mask against its bit, worked out here (row 0 south, bit = row * 16 + col, byte bit >> 3, value 1 << (bit & 7)).
        for (int cell = 0; cell < 256; cell++)
        {
          bool set = (row.ClearMask[cell >> 3] >> (cell & 7) & 1) != 0;
          int col = cell % 16, cellRow = cell / 16;
          foreach (var (fx, fz) in new[] { (0.01, 0.01), (3.99, 3.99), (2.0, 2.0) })
          {
            double x = row.Key.OriginX + col * 4 + fx, z = row.Key.OriginZ + cellRow * 4 + fz;
            bool got = BakedVegetation.Covers(x, z);
            if (got != set)
              wrong++;
          }
          if (set) covered++; else free++;
        }
      }
      C(wrong == 0, $"every cell of every mask, and every point of every flag-16 zone, is covered when and only when the file says ({wrong} wrong)");
      C(covered > 1142 && free > 0, $"(both kinds were looked at: {covered:N0} covered, {free:N0} free)");
      // Where the file has no zone, nothing is covered.
      C(!BakedVegetation.Covers(0, 0) && !BakedVegetation.Covers(9000, -9000), "outside the file's zones nothing is covered");
      // The effective mask is the file's when no in-game bake has records: the same bits.
      var maskZone = layer.Zones.First(r => r.HasClearMask);
      var effective = BakedVegetation.EffectiveMask(layer, maskZone.Key);
      C(effective.Bits != null && effective.Bits.SequenceEqual(maskZone.ClearMask), "with no in-game records the zone's effective mask is the file's");
      C(ReferenceEquals(BakedVegetation.EffectiveMask(layer, maskZone.Key), effective), "and it is made once for the layer");
      var next = ServerWithRevision(layer, 9);
      C(!ReferenceEquals(BakedVegetation.EffectiveMask(next, maskZone.Key), effective), "a new layer starts over");
      C(BakedVegetation.EffectiveMask(layer, layer.Zones.First(r => r.NoVegetation).Key).All, "a zone with flag 16 is all of it");
    }
    using (ServerStore(null))
      C(!BakedVegetation.Covers(layer.Zones[0].Key.OriginX + 5, layer.Zones[0].Key.OriginZ + 5) && !BakedVegetation.InsideClearAreaPostfix(false, Vector3.zero), "with no layer nothing is covered");
    using (ServerStore(layer))
    {
      var flagged = layer.Zones.First(r => r.NoVegetation).Key;
      var inside = new Vector3((float)flagged.OriginX + 7f, 40f, (float)flagged.OriginZ + 7f);
      C(BakedVegetation.InsideClearAreaPostfix(true, Vector3.zero) && BakedVegetation.InsideClearAreaPostfix(false, inside), "the postfix keeps a true answer and adds the layer's");
      C(!BakedVegetation.InsideClearAreaPostfix(false, new Vector3(0f, 40f, 0f)), "and adds nothing elsewhere");
    }
  }

  public static void ServerMaskFootprintTest()
  {
    Section("mask: the cells within 4 m of the footprint of an in-game record");
    // One footprint at the middle of a zone, then at its corner, with the radius of a piece.
    var zone = new ZoneKey(3, -2);
    double cx = zone.OriginX + 32.0, cz = zone.OriginZ + 32.0;
    var bits = BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(cx, cz, 0.0));
    C(bits != null, "a footprint marks cells");
    // Brute force: a cell is marked when its rectangle is within 4 m of the point.
    bool Near(double px, double pz, double reach, int col, int row)
    {
      double dx = Math.Max(Math.Max(zone.OriginX + col * 4 - px, 0), px - (zone.OriginX + col * 4 + 4));
      double dz = Math.Max(Math.Max(zone.OriginZ + row * 4 - pz, 0), pz - (zone.OriginZ + row * 4 + 4));
      return dx * dx + dz * dz <= reach * reach;
    }
    bool Same(byte[] made, double px, double pz, double reach)
    {
      for (int row = 0; row < 16; row++)
        for (int col = 0; col < 16; col++)
          if (BakedVegetation.ZoneMask.Has(made ?? new byte[32], row * 16 + col) != Near(px, pz, reach, col, row))
            return false;
      return true;
    }
    C(Same(bits, cx, cz, 4.0), "a point at the middle of a zone marks exactly the cells within 4 m of it (the four around its corner, and the ring of cells a 4 m reach touches)");
    C(Count(bits) == 12 && BakedVegetation.ZoneMask.Has(bits, 7 * 16 + 7) && BakedVegetation.ZoneMask.Has(bits, 8 * 16 + 8) && !BakedVegetation.ZoneMask.Has(bits, 6 * 16 + 6) && !BakedVegetation.ZoneMask.Has(bits, 5 * 16 + 5),
      $"(twelve cells: the 4 x 4 around the zone's middle without its corners, which are 5.7 m off: {Count(bits)})");
    var wide = BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(cx + 1.3, cz - 2.2, 6.5));
    C(Same(wide, cx + 1.3, cz - 2.2, 10.5), "a prefab's reach is added to the 4 m");
    // A footprint at the edge of the next zone reaches into this one; one 20 m beyond does not.
    var edge = BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(zone.OriginX + 64.0 + 2.0, cz, 1.0));
    C(edge != null && Same(edge, zone.OriginX + 66.0, cz, 5.0) && BakedVegetation.ZoneMask.Has(edge, 8 * 16 + 15) && !BakedVegetation.ZoneMask.Has(edge, 8 * 16 + 12),
      "a record just past the zone's east edge marks the cells of this zone along it");
    C(BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(zone.OriginX + 64.0 + 20.0, cz, 1.0)) == null, "one 20 m past it marks none");
    C(BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(zone.OriginX - 2.0, zone.OriginZ - 2.0, 0.0)) is { } corner && Count(corner) == 1 && BakedVegetation.ZoneMask.Has(corner, 0),
      "one 2 m off the south west corner marks the corner cell alone");
    C(BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(zone.OriginX - 3.0, zone.OriginZ - 3.0, 0.0)) == null, "and one 3 m off it (4.2 m from the cell) marks none");
    // Marks add to what is there.
    var both = BakedVegetation.Mark((byte[])bits.Clone(), zone, new BakedVegetation.Footprint(zone.OriginX + 2.0, zone.OriginZ + 2.0, 0.0));
    C(Count(both) > Count(bits) && Count(both) == Count(bits) + Count(BakedVegetation.Mark(null, zone, new BakedVegetation.Footprint(zone.OriginX + 2.0, zone.OriginZ + 2.0, 0.0))), "a second footprint adds its cells to the first's");

    // The footprints of a decoded zone: only the records an in-game bake owns, with their prefab's reach grown by their scale.
    var layer = ServerValtima(nameof(ServerMaskFootprintTest));
    if (layer == null)
      return;
    var data = new ZoneData(zone, 4);
    data.Palette[0] = 0; data.Palette[1] = 1; data.Palette[2] = 1; data.Palette[3] = 1;
    data.Flags[0] = RecordFlags.Source; data.Flags[1] = RecordFlags.None; data.Flags[2] = RecordFlags.Source | RecordFlags.Scale; data.Flags[3] = RecordFlags.Source;
    for (int k = 0; k < 4; k++)
    {
      data.X[k] = (ushort)(1024 * (10 + k)); data.Z[k] = (ushort)(1024 * 20); data.Y[k] = 30000;
    }
    data.SourceRun = [1, 1, 2];
    data.ValueSetRun = [0, 0, 0];
    data.ScaleRun = [2000, 2000, 2000];
    var footprints = BakedVegetation.FootprintsIn(layer, data, entry => ReferenceEquals(entry, layer.Palette[0]) ? 1.5f : 3f);
    C(footprints.Length == 3, $"three of the four records are in-game ({footprints.Length}): the one without a source is the compiler's");
    C(Math.Abs(footprints[0].Radius - 1.5) < 1e-6 && Math.Abs(footprints[1].Radius - 6.0) < 1e-6 && Math.Abs(footprints[2].Radius - 3.0) < 1e-6,
      $"a record's reach is its prefab's, times its scale ({string.Join(", ", footprints.Select(f => f.Radius))})");
    C(Math.Abs(footprints[0].X - (zone.OriginX + 10)) < 1e-6 && Math.Abs(footprints[0].Z - (zone.OriginZ + 20)) < 1e-6, "and stands at the record's point");

    // The cells a few records ask a zone for, from the neighbours too (the zone is composed from the nine around it).
    var grown = BakedVegetation.ZoneMask.None;
    C(!grown.Any && !grown.Covers(1, 1) && !BakedVegetation.ZoneMask.Everywhere.Grown(BakedVegetation.ZoneMask.Everywhere).Any, "none covers nothing, and nothing grows over everything");
  }

  private static int Count(byte[] bits)
  {
    int n = 0;
    if (bits != null)
      for (int bit = 0; bit < 256; bit++)
        if (BakedVegetation.ZoneMask.Has(bits, bit))
          n++;
    return n;
  }

  public static void ServerMaskGrownTest()
  {
    Section("mask: the cells a new layer's file clears that the old one's did not");
    var some = new byte[32];
    some[0] = 0b0000_0011;
    some[5] = 0b1000_0000;
    var more = (byte[])some.Clone();
    more[0] |= 0b0001_0100;
    more[31] = 1;
    var oldMask = new BakedVegetation.ZoneMask(false, some);
    var newMask = new BakedVegetation.ZoneMask(false, more);
    var grown = newMask.Grown(oldMask);
    C(grown.Bits != null && Count(grown.Bits) == 3 && BakedVegetation.ZoneMask.Has(grown.Bits, 2) && BakedVegetation.ZoneMask.Has(grown.Bits, 4) && BakedVegetation.ZoneMask.Has(grown.Bits, 248),
      "a mask that grew gives exactly the cells it gained");
    C(!newMask.Grown(newMask).Any && !oldMask.Grown(newMask).Any, "the same mask, and a mask that shrank, gain none");
    C(newMask.Grown(BakedVegetation.ZoneMask.None).Bits!.SequenceEqual(more), "from none, every cell is new");
    C(BakedVegetation.ZoneMask.Everywhere.Grown(BakedVegetation.ZoneMask.None).All, "everywhere from none is everywhere");
    var rest = BakedVegetation.ZoneMask.Everywhere.Grown(oldMask);
    C(rest.Bits != null && Count(rest.Bits) == 256 - 3 && !BakedVegetation.ZoneMask.Has(rest.Bits, 0), "everywhere over a mask is every cell the mask lacked");
    C(!BakedVegetation.ZoneMask.Everywhere.Grown(BakedVegetation.ZoneMask.Everywhere).Any && !oldMask.Grown(BakedVegetation.ZoneMask.Everywhere).Any, "nothing is new over everywhere");
    C(!BakedVegetation.ZoneMask.None.Grown(oldMask).Any, "none gains nothing");

    var layer = ServerValtima(nameof(ServerMaskGrownTest));
    if (layer == null)
      return;
    var same = ServerWithRevision(layer, 3);
    var noGrowth = layer.Zones.Count(r => (r.NoVegetation || r.HasClearMask) && BakedVegetation.FileMask(layer, r.Key).Grown(BakedVegetation.FileMask(same, r.Key)).Any);
    var everything = layer.Zones.Count(r => (r.NoVegetation || r.HasClearMask) && BakedVegetation.FileMask(layer, r.Key).Grown(BakedVegetation.FileMask(null, r.Key)).Any);
    C(noGrowth == 0, "VALtima's file against the same file: no zone's mask grew");
    C(everything == 1142 + 223 - 0, $"against no layer: every zone with a flag 16 or a mask grew ({everything})");
  }
}

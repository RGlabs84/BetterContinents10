// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// VALtima's real placements.bcp (spec 2.7): every value the independent reader (~/valheim-testbed/bc-0104/measure/bcpread.py) found, and every
// record's place, turn, scale and id against the digest an independent Python decoder wrote (valtima_digest.py, fixtures/valtima-records.tsv).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // The file as spec 2.7 lists it: size, md5, header, palette, index, records, blocks, kinds.
  private static void ValtimaTest()
  {
    Section("VALtima's file: the values of spec 2.7");
    if (!NeedValtima("ValtimaTest"))
      return;
    var bytes = Valtima();
    C(bytes.Length == 2_782_837, "the file is 2,782,837 bytes");
    C(Md5(bytes, bytes.Length) == "e9d8f303b2e582d5ddf668a5be1dae3a", "its md5 is e9d8f303b2e582d5ddf668a5be1dae3a");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var layer = BakedLayer.Read(bytes, bytes.Length);
    long readMs = watch.ElapsedMilliseconds;
    System.Console.WriteLine($"   read and checked in {readMs} ms");
    C(layer.Length == bytes.Length && layer.Bytes == bytes, "the layer keeps the bytes it was given");

    // The header.
    C(layer.Flags == (HeaderFlags)7, "header flags 7 (ground, paint, clear masks; no registry)");
    C(layer.Revision == 1u, "revision 1");
    C(layer.Producer == "Uo2Bc (VALtimaOnline towns)", "producer 'Uo2Bc (VALtimaOnline towns)'");
    C(!layer.HasRegistry && layer.Registry.Operations.Count == 0, "no registry");

    // The palette.
    var palette = layer.Palette;
    C(palette.Count == 128, "128 palette entries");
    C(layer.IndexStart == 7_033, "the palette ends at byte 7,033");
    var roles = palette.GroupBy(e => e.Role).ToDictionary(g => g.Key, g => g.Count());
    C(roles.GetValueOrDefault(BakedRole.Static) == 99 && roles.GetValueOrDefault(BakedRole.Live) == 19 && roles.GetValueOrDefault(BakedRole.Copy) == 5
      && roles.GetValueOrDefault(BakedRole.Seat) == 5, "roles Static 99, Live 19, Copy 5, Seat 5");
    var collisions = palette.GroupBy(e => e.Collision).ToDictionary(g => g.Key, g => g.Count());
    C(collisions.GetValueOrDefault(BakedCollision.Boxes) == 80 && collisions.GetValueOrDefault(BakedCollision.None) == 21
      && collisions.GetValueOrDefault(BakedCollision.Trunk) == 8 && collisions.GetValueOrDefault(BakedCollision.Prefab) == 19, "collision Boxes 80, None 21, Trunk 8, Prefab 19");
    C(palette.All(e => e.Layer == 0), "every entry on layer 0");
    var flags = palette.GroupBy(e => e.Flags).ToDictionary(g => g.Key, g => g.Count());
    C(flags.GetValueOrDefault(PaletteFlags.None) == 82 && flags.GetValueOrDefault(PaletteFlags.UniformScale) == 27 && flags.GetValueOrDefault(PaletteFlags.Protect) == 19
      && flags.Count == 3, "palette flags none 82, UniformScale 27, Protect 19");
    C(palette.Count(e => e.HasTint) == 14, "14 tinted");
    C(palette.Count(e => e.Candidates.Length > 1) == 54, "54 with more than one candidate");
    C(palette.Count(e => e.Tags.Any(t => t.Key == "VALtima_TownPiece")) == 19 && palette.Count(e => e.Tags.Any(t => t.Key == "VALtima_Kind")) == 19
      && palette.Sum(e => e.Tags.Length) == 38, "tags VALtima_TownPiece 19, VALtima_Kind 19");
    var live = palette.Where(e => e.Role == BakedRole.Live).ToList();
    C(live.All(e => e.Collision == BakedCollision.Prefab && e.Protected), "every Live entry has collision Prefab and Protect");
    C(live.All(e => e.TryGetTag("VALtima_TownPiece", out var t) && t.Type == TagType.Bool && t.Bool
      && e.TryGetTag("VALtima_Kind", out var k) && k.Type == TagType.String && (k.Text == "door" || k.Text == "station" || k.Text == "extension")),
      "every Live entry has VALtima_TownPiece = true and VALtima_Kind = door, station or extension");

    // The zone index.
    var zones = layer.Zones;
    C(layer.IndexEnd == 45_253, "the zone index ends at byte 45,253");
    C(zones.Count == 1_365, "1,365 zones");
    C(zones.Count(z => z.Placements > 0) == 1_142 && zones.Where(z => z.Placements > 0).All(z => z.NoVegetation), "1,142 zones with records, all with zone flag 16");
    C(zones.Count(z => z.Placements == 0) == 223 && zones.Where(z => z.Placements == 0).All(z => z.HasClearMask), "223 zones with a clear mask and no records");
    var zoneFlags = zones.GroupBy(z => (int)z.Flags).ToDictionary(g => g.Key, g => g.Count());
    C(zoneFlags.Count == 5 && zoneFlags.GetValueOrDefault(4) == 11 && zoneFlags.GetValueOrDefault(5) == 184 && zoneFlags.GetValueOrDefault(7) == 28
      && zoneFlags.GetValueOrDefault(17) == 143 && zoneFlags.GetValueOrDefault(19) == 999, "zone flags {4: 11, 5: 184, 7: 28, 17: 143, 19: 999}");
    C(zones.Count(z => z.HasGround) == 1_354 && layer.Ground.Count == 1_354, "ground in 1,354 zones");
    C(zones.Count(z => z.HasPaint) == 1_027, "paint in 1,027 zones");
    C(zones.Count(z => z.HasClearMask && z.ClearMask != null && z.ClearMask.Length == 32) == zones.Count(z => z.HasClearMask), "every clear mask is 32 bytes");
    C(zones.Zip(zones.Skip(1), (a, b) => a.Key.CompareTo(b.Key) < 0).All(x => x), "the rows are strictly sorted by (z, x)");

    // The records, read zone by zone.
    long records = 0, rawBytes = 0, compressed = zones.Sum(z => (long)z.Length), outOfBounds = 0, unsorted = 0, extras = 0, rotations = 0;
    var byRole = new Dictionary<BakedRole, long>();
    var byFlags = new Dictionary<int, long>();
    var kinds = new Dictionary<string, long>();
    var ids = new HashSet<uint>();
    var scratch = new byte[1 << 16];
    foreach (var row in zones)
    {
      if (row.Length > 0)
        rawBytes += BakedFormat.Inflate(bytes, row.Offset, row.Length, ref scratch, BakedFormat.MaxInflated, "zone " + row.Key);
      if (row.Placements == 0)
      {
        var empty = layer.Decode(row);
        C(empty.Count == 0 && (empty.ClearMask != null || empty.Ground != null || empty.Paint != null), "a zone without records has a mask, ground or paint: " + row.Key);
        extras += empty.Extras.Length;
        continue;
      }
      var data = layer.Decode(row);
      extras += data.Extras.Length;
      int liveHere = 0;
      for (int k = 0; k < data.Count; k++)
      {
        var r = data.Record(k);
        var entry = palette[r.Palette];
        byRole[entry.Role] = byRole.GetValueOrDefault(entry.Role) + 1;
        byFlags[(int)r.Flags] = byFlags.GetValueOrDefault((int)r.Flags) + 1;
        kinds[entry.Name] = kinds.GetValueOrDefault(entry.Name) + 1;
        if (r.HasFullRotation)
          rotations++;
        if (entry.Role == BakedRole.Live)
        {
          liveHere++;
          ids.Add(r.Id);
        }
        double y = r.WorldY;
        if (y < row.YMin - 0.001 || y > row.YMax + 0.001)
          outOfBounds++;
        if (k > 0 && (data.Palette[k], data.Z[k], data.X[k]).CompareTo((data.Palette[k - 1], data.Z[k - 1], data.X[k - 1])) < 0)
        {
          unsorted++;
          break;
        }
      }
      C(liveHere == row.Live, "the index's live count is the block's: zone " + row.Key);
      records += data.Count;
    }
    C(records == 702_700 && layer.Placements == 702_700, "702,700 records");
    C(byRole.GetValueOrDefault(BakedRole.Static) == 697_696 && byRole.GetValueOrDefault(BakedRole.Seat) == 3_562 && byRole.GetValueOrDefault(BakedRole.Copy) == 834
      && byRole.GetValueOrDefault(BakedRole.Live) == 608, "Static 697,696, Seat 3,562, Copy 834, Live 608");
    C(ids.Count == 608, "608 unique Live ids");
    C(byFlags.Count == 3 && byFlags.GetValueOrDefault(0) == 604_670 && byFlags.GetValueOrDefault(2) == 97_422 && byFlags.GetValueOrDefault(4) == 608,
      "record flags {0: 604,670, 2: 97,422, 4: 608}");
    C(rotations == 0, "no full rotation");
    C(compressed == 2_737_580, "the blocks are 2,737,580 bytes compressed");
    C(rawBytes == 31_109_983, "and 31,109,983 bytes raw");
    C(outOfBounds == 0, "no record is outside its zone's y bounds");
    C(unsorted == 0, "no zone is out of (palette, z, x) order");
    C(extras == 0, "no zone has extras");
    var top = kinds.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(8).ToList();
    var expected = new (string, long)[]
    {
      ("stone_wall_2x1", 121_218), ("BCP_ClayWall2x2", 67_189), ("BFP_FineWoodFloor2x2", 52_845), ("stone_wall_1x1", 41_680),
      ("Piece_grausten_floor_2x2", 40_159), ("BCP_ClayHalfWall_1x2", 35_568), ("darkwood_roof", 34_870), ("darkwood_pole", 27_659),
    };
    C(top.Select(kv => (kv.Key, kv.Value)).SequenceEqual(expected), "the top kinds: " + string.Join(", ", expected.Select(e => $"{e.Item1} {e.Item2:N0}")));
    C(layer.RecordsOfSource(0) == 702_700 && layer.RecordsBySource().Count == 1, "every record is the compiler's (source 0)");
    C(readMs < 30_000, $"reading and checking the whole layer takes {readMs} ms");
  }

  // Every record's place, turn, scale and id against the independent decoder's digest.
  private static void ValtimaRecordsTest()
  {
    Section("VALtima's file: every record against the independent decoder");
    if (!NeedValtima("ValtimaRecordsTest"))
      return;
    var bytes = Valtima();
    var layer = BakedLayer.Read(bytes, bytes.Length);
    var expected = new Dictionary<ZoneKey, (int Count, string Digest)>();
    foreach (var line in File.ReadAllLines(Fixture("valtima-records.tsv")))
    {
      if (line.StartsWith('#') || line.Length == 0)
        continue;
      var part = line.Split(' ');
      expected[new ZoneKey(int.Parse(part[0]), int.Parse(part[1]))] = (int.Parse(part[2]), part[3]);
    }
    C(expected.Count == 1_142, "the digest has 1,142 zones");
    int checkedZones = 0, wrong = 0;
    var inv = CultureInfo.InvariantCulture;
    foreach (var row in layer.Zones)
    {
      if (row.Placements == 0)
      {
        C(!expected.ContainsKey(row.Key), "a zone without records is not in the digest: " + row.Key);
        continue;
      }
      var data = layer.Decode(row);
      var text = new StringBuilder(data.Count * 64);
      for (int k = 0; k < data.Count; k++)
      {
        var r = data.Record(k);
        double sx = r.HasScale ? r.ScaleX / 1000.0 : 1.0, sy = r.HasScale ? r.ScaleY / 1000.0 : 1.0, sz = r.HasScale ? r.ScaleZ / 1000.0 : 1.0;
        text.Append(r.Palette.ToString(inv)).Append(' ')
          .Append(r.WorldX.ToString("F6", inv)).Append(' ').Append(r.WorldZ.ToString("F6", inv)).Append(' ').Append(r.WorldY.ToString("F6", inv)).Append(' ')
          .Append(BakedFormat.YawDegrees(r.Yaw).ToString("F6", inv)).Append(' ')
          .Append(sx.ToString("F3", inv)).Append(' ').Append(sy.ToString("F3", inv)).Append(' ').Append(sz.ToString("F3", inv)).Append(' ')
          .Append((r.HasId ? r.Id : 0u).ToString(inv)).Append('\n');
      }
      checkedZones++;
      if (!expected.TryGetValue(row.Key, out var want) || want.Count != data.Count || want.Digest != Sha256(text.ToString()))
      {
        wrong++;
        C(false, "zone " + row.Key + " decodes differently from the independent reader");
      }
    }
    C(checkedZones == 1_142 && wrong == 0, $"every record of all {checkedZones} zones decodes as the independent reader's (place, yaw, scale, id)");

    // The ground and the clear mask against the file's own sections, by a second reading of the raw bytes.
    var scratch = new byte[1 << 16];
    int groundZones = 0;
    foreach (var row in layer.Zones.Where(z => z.HasGround).Take(40))
    {
      int n = BakedFormat.Inflate(bytes, row.Offset, row.Length, ref scratch, BakedFormat.MaxInflated, "zone " + row.Key);
      // The ground is the last big section before the paint (4096 bytes) and the extras count: find it from the end.
      int end = n - 1 - (row.HasPaint ? BakedFormat.PaintCells : 0);
      int start = end - (4 + BakedFormat.GroundVertices * 3);
      float baseMetres = BitConverter.ToSingle(scratch, start);
      layer.Ground.TryGet(row.Key, out var ground);
      C(ground != null && ground.Base == baseMetres, "the ground's base is the file's: zone " + row.Key);
      bool same = ground != null;
      for (int i = 0; same && i < BakedFormat.GroundVertices; i++)
        same = ground.Heights[i] == BitConverter.ToInt16(scratch, start + 4 + 2 * i) && ground.Weights[i] == scratch[start + 4 + 2 * BakedFormat.GroundVertices + i];
      C(same, "every vertex of the ground is the file's: zone " + row.Key);
      groundZones++;
    }
    C(groundZones == 40, "forty ground zones compared with the raw bytes");
  }
}

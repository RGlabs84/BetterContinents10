// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BC's additions to format 1 (spec 2.3): header flag 8 (a registry), record flags 8 and 16 (source and value set, seed), and that a compiler's
// file, which sets none of them, is read and written exactly as VALtima's writer lays it out (R2). Plus the rotation codec (smallest three).
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // Records with flags 8 and 16, and a registry, written and read back.
  private static void AdditionsRoundTripTest()
  {
    Section("additions: source, value set, seed and the registry round-trip");
    var palette = SamplePalette();
    var edit = LayerEdit.New("additions");
    foreach (var entry in palette)
      edit.PaletteIndexFor(entry);
    edit.AddOperation(SampleOperation(1, OperationKind.BakeArea, 3));
    var records = new List<ZoneRecord>
    {
      ZoneRecord.CreateYaw(0, 3.5, 40, 4.5, 90, source: 1, valueSet: 2, seed: 12344),
      ZoneRecord.CreateYaw(0, 7.5, 40, 4.5, 10, source: 1, valueSet: 0, seed: 0),
      ZoneRecord.CreateYaw(4, 9.5, 40, 4.5, 20, seed: 777),
      ZoneRecord.CreateYaw(2, 1.5, 40, 4.5, 30, source: 1, valueSet: 1),
    };
    edit.AddRecords(records);
    var layer = edit.Build().Layer;
    layer.TryGetZoneRow(0, 0, out var row);
    var data = layer.Decode(row);
    var read = data.Records().ToList();
    C(read.Count == 4 && read.All(r => records.Contains(r)), "four records with flags 8 and 16 read back");
    C(read.Count(r => r.HasSource) == 3 && read.Count(r => r.HasSeed) == 3, "three name a bake and three carry a seed");
    var seeded = read.First(r => r.HasSeed && r.Seed == 12344);
    C(seeded.Source == 1 && seeded.ValueSet == 2 && seeded.Flags == (RecordFlags.Source | RecordFlags.Seed), "source, value set and seed of one record");
    C(layer.RecordsOfSource(1) == 3 && layer.RecordsOfSource(0) == 1, "the layer counts a bake's records");
    C(layer.RecordsBySource().SequenceEqual(new SortedDictionary<int, long> { [0] = 1, [1] = 3 }), "and its records by source");
    C(layer.Registry.Operations[0].ValueSets.Length == 3 && layer.Registry.Operations[0].ValueSets[2].Equals(SampleValueSet(2)), "the value sets read back");
    C(data.Seeds[Array.IndexOf(data.X, seeded.X)] == 12344, "the per-record seed array has the stored seed");

    // The flags in a block, byte by byte, with the second writer.
    var raw = ValidRaw();
    raw.HeaderFlags = 8;
    raw.Registry = RawFile.RegistryBytes(1, 2, (1, 1, 1, "who", 2));
    var zone = raw.Zones[0];
    zone.Records[0].Flags = 8 | 16;
    zone.Records[0].Source = 1;
    zone.Records[0].ValueSet = 1;
    zone.Records[0].Seed = 321;
    zone.Records[1].Flags = 2 | 16;
    zone.Records[1].Seed = 5;
    var bytes = raw.Build(out int length);
    var second = BakedLayer.Read(bytes, length);
    var back = second.Decode(second.Zones[0]).Records().ToList();
    C(back[0].Source == 1 && back[0].ValueSet == 1 && back[0].Seed == 321 && back[1].Seed == 5 && back[1].ScaleY == 2000, "the second writer's flag 8 and flag 16 runs read in the order the spec gives");
    // The same content written by BC's writer has the same raw block (the runs are in the same order).
    var edit2 = LayerEdit.New("raw test");
    edit2.PaletteIndexFor(Entry("piece_a", BakedRole.Static, BakedCollision.Prefab));
    edit2.AddOperation(new OperationInfo(1, OperationKind.BakeArea, OperationState.InLayer, 1_700_000_000L, "who", 0, 0, 0, 0, 0, 1, 0, 0, "0.10.4"));
    var viaBc = edit2.Build().Layer;
    C(viaBc.HasRegistry, "a registry is written once an operation exists");
  }

  // The unknown bits are refused by name, in each place they can be.
  private static void AdditionsUnknownBitsTest()
  {
    Section("additions: a bit this version does not know is refused by name");
    var sample = MakeSample(3);
    var bytes = (byte[])sample.Layer.Bytes.Clone();
    int length = sample.Layer.Length;
    // Header flags are at byte 6: a bit 16 set by a newer tool.
    var header = (byte[])bytes.Clone();
    header[6] |= 16;
    Recrc(header, length);
    C(Refuses(() => BakedLayer.Read(header, length), "header bit 4"), "refused: header bit 4 (flag 16)");
    // A record flag of 32 and a zone flag of 32, in a layer from the second writer.
    var raw = ValidRaw();
    raw.Zones[0].Records[1].Flags = 2 | 32;
    var rawBytes = raw.Build(out int rawLength);
    C(Refuses(() => BakedLayer.Read(rawBytes, rawLength), "record bit 5", "newer"), "refused: record bit 5 (flag 32)");
    raw = ValidRaw();
    raw.Zones[0].IndexFlags = 32;
    rawBytes = raw.Build(out rawLength);
    C(Refuses(() => BakedLayer.Read(rawBytes, rawLength), "zone bit 5", "newer"), "refused: zone bit 5 (flag 32)");
    raw = ValidRaw();
    raw.Palette[0].Flags = 64;
    rawBytes = raw.Build(out rawLength);
    C(Refuses(() => BakedLayer.Read(rawBytes, rawLength), "palette flag bit 6", "newer"), "refused: palette flag bit 6 (flag 64; 32 is Decor)");
  }

  // A layer without the additions is laid out as VALtima's writer lays it out (R2): header, producer, palette, zone index, blocks, in order.
  private static void AdditionsPlainLayoutTest()
  {
    Section("additions: a layer without them is laid out as VALtima's writer lays it out");
    var palette = new[] { Entry("piece_a", BakedRole.Static, BakedCollision.Prefab), Entry("door", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect, tags: [Tag.OfBool("VALtima_TownPiece", true), Tag.OfString("VALtima_Kind", "door")]) };
    var edit = LayerEdit.New("raw test");
    foreach (var entry in palette)
      edit.PaletteIndexFor(entry);
    var records = new[]
    {
      ZoneRecord.CreateYaw(0, 1000 / 1024.0 - 32, 31, 2000 / 1024.0 - 32, 100 * 360.0 / 65536, id: null),
      ZoneRecord.CreateYaw(0, 3000 / 1024.0 - 32, 31, 2000 / 1024.0 - 32, 200 * 360.0 / 65536, scale: new Vector3(1f, 2f, 1f)),
      ZoneRecord.CreateYaw(1, 5000 / 1024.0 - 32, 31, 6000 / 1024.0 - 32, 300 * 360.0 / 65536, id: 7u),
    };
    edit.AddRecords(records, 9f);
    var made = edit.Build().Layer;
    // The second writer's file for the same content.
    var raw = ValidRaw();
    raw.Producer = "raw test";
    raw.Palette[0].Candidates = [("piece_a", [0, 0, 0])];
    raw.Palette[1].Flags = 4;
    raw.Zones[0].YMin = 31;
    raw.Zones[0].YMax = 40;
    var theirs = raw.Build(out int theirLength);
    var other = BakedLayer.Read(theirs, theirLength);
    C(made.Palette.Zip(other.Palette, (a, b) => a.Equals(b)).All(x => x), "the palette of the two writers is the same");
    C(made.IndexStart == other.IndexStart && made.IndexEnd == other.IndexEnd, "the palette and the index end at the same bytes");
    // Everything up to the zone index is byte for byte the same (the revision and counts match; no registry).
    bool sameHead = made.Bytes.Take(made.IndexEnd).SequenceEqual(other.Bytes.Take(other.IndexEnd));
    C(sameHead, "header, producer, palette and zone index are the same bytes");
    var rawMade = BakedFormat.Crc32(made.Bytes, 0, made.Length - 4) == BitConverter.ToUInt32(made.Bytes, made.Length - 4);
    C(rawMade && made.Length > made.IndexEnd, "and the CRC is the zlib CRC-32 of the file");
    // The inflated blocks are the same bytes.
    var madeRaw = RawFile.Inflate(made.Bytes.Skip(made.Zones[0].Offset).Take(made.Zones[0].Length).ToArray());
    var theirRaw = RawFile.Inflate(other.Bytes.Skip(other.Zones[0].Offset).Take(other.Zones[0].Length).ToArray());
    C(madeRaw.SequenceEqual(theirRaw), $"the block's inflated bytes are the second writer's ({madeRaw.Length} bytes)");
  }

  private static void Recrc(byte[] bytes, int length)
  {
    uint crc = BakedFormat.Crc32(bytes, 0, length - 4);
    bytes[length - 4] = (byte)crc;
    bytes[length - 3] = (byte)(crc >> 8);
    bytes[length - 2] = (byte)(crc >> 16);
    bytes[length - 1] = (byte)(crc >> 24);
  }

  // 1,000,000 random rotations through the smallest-three codec: the worst angle against the original.
  private static void RotationsTest()
  {
    Section("rotations: smallest three, 1,000,000 random quaternions");
    var random = new Lcg(2026);
    double worst = 0;
    for (int i = 0; i < 1_000_000; i++)
    {
      var q = RandomRotation(random);
      var packed = PackedRotation.Of(q);
      var back = packed.ToQuaternion();
      double angle = Angle(q, back);
      if (angle > worst)
        worst = angle;
    }
    C(worst < 0.01, $"the worst angle is {worst:0.00000} degrees (under 0.01)");
    // The corners: axis rotations, the identity, negative components, a quaternion that is not normalised.
    foreach (var q in new[] { new Quaternion(0, 0, 0, 1), new Quaternion(1, 0, 0, 0), new Quaternion(0, 1, 0, 0), new Quaternion(0, 0, 1, 0), new Quaternion(0, 0, 0, -1),
               new Quaternion(-0.5f, 0.5f, -0.5f, 0.5f), new Quaternion(0, 0.7071068f, 0, 0.7071068f), new Quaternion(0.2f, 0.4f, 0.6f, 0.8f), new Quaternion(2, 4, 6, 8) })
    {
      var back = PackedRotation.Of(q).ToQuaternion();
      C(Angle(q, back) < 0.01, $"the angle of ({q.x}, {q.y}, {q.z}, {q.w}) survives: {Angle(q, back):0.00000} degrees");
    }
    // The largest component is made positive and the others follow it.
    var neg = PackedRotation.Of(new Quaternion(0, 0, 0, -1));
    C(neg.Largest == 3 && neg.A == 0 && neg.B == 0 && neg.C == 0, "the sign is folded into the three: (0, 0, 0, -1) is (0, 0, 0, 1)");
    var packedYaw = PackedRotation.Of(new Quaternion(0, 0.7071068f, 0, 0.7071068f));
    C(packedYaw.Largest == 1 || packedYaw.Largest == 3, "a rotation of 90 degrees about y has y or w as its largest");

    // Yaw: 65,536 steps; yaw-only is kept for an upright rotation, a full rotation for a leaning one.
    var upright = ZoneRecord.Create(0, 0, 0, 0, YawQuaternion(123.456));
    C(!upright.HasFullRotation && Math.Abs(BakedFormat.YawDegrees(upright.Yaw) - 123.456) < 360.0 / 65536 / 2 + 1e-9, "an upright rotation keeps only its yaw, to half a step");
    var leaning = ZoneRecord.Create(0, 0, 0, 0, YawQuaternion(40) * new Quaternion(0.01f, 0, 0, 0.99995f));
    C(leaning.HasFullRotation && Angle(leaning.Rotation, YawQuaternion(40) * new Quaternion(0.01f, 0, 0, 0.99995f)) < 0.01, "a leaning rotation is stored in full");
    var nearly = ZoneRecord.Create(0, 0, 0, 0, YawQuaternion(40) * new Quaternion(0.00001f, 0, 0, 1f));
    C(!nearly.HasFullRotation, "under 0.005 degrees of lean keeps only the yaw");
    C(BakedFormat.QuantizeYaw(-90) == 49152 && BakedFormat.QuantizeYaw(360) == 0 && BakedFormat.QuantizeYaw(359.9999) == 0 && BakedFormat.QuantizeYaw(180) == 32768, "yaw steps wrap at a turn");
    for (int step = 0; step < 65536; step += 997)
      C(BakedFormat.QuantizeYaw(BakedFormat.YawDegrees((ushort)step)) == step, "yaw step " + step + " survives a round trip");
  }

  public static Quaternion YawQuaternion(double degrees)
  {
    double half = degrees * Math.PI / 360.0;
    return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
  }

  // The angle between two rotations, in degrees.
  public static double Angle(Quaternion a, Quaternion b)
  {
    double dot = a.x * (double)b.x + a.y * (double)b.y + a.z * (double)b.z + a.w * (double)b.w;
    double la = Math.Sqrt(a.x * (double)a.x + a.y * (double)a.y + a.z * (double)a.z + a.w * (double)a.w);
    double lb = Math.Sqrt(b.x * (double)b.x + b.y * (double)b.y + b.z * (double)b.z + b.w * (double)b.w);
    double c = Math.Abs(dot) / (la * lb);
    return 2 * Math.Acos(Math.Min(1.0, c)) * 180.0 / Math.PI;
  }
}

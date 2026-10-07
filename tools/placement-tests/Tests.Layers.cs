// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Layers the tests make with BC's writer (LayerEdit), and the comparison of two layers' content.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  public static PaletteEntry Entry(string name, BakedRole role = BakedRole.Static, BakedCollision collision = BakedCollision.Prefab, PaletteFlags flags = PaletteFlags.None,
    Tag[] tags = null, Box[] boxes = null, byte layer = 0, string tint = "", string[] others = null)
  {
    var candidates = new List<Candidate> { new(name, 0, 0, 0) };
    if (others != null)
      foreach (var other in others)
        candidates.Add(new Candidate(other, 100, -200, 300));
    return new PaletteEntry(candidates.ToArray(), role, collision, layer, flags, boxes, tint, 900, 800, 700, tint.Length > 0 ? "all" : "", tags);
  }

  // A deterministic generator (the tests must not depend on a seed of the runtime).
  public sealed class Lcg
  {
    private ulong state;

    public Lcg(ulong seed) => state = seed * 6364136223846793005UL + 1442695040888963407UL;

    public uint Next()
    {
      state = state * 6364136223846793005UL + 1442695040888963407UL;
      return (uint)(state >> 33);
    }

    public double Unit() => Next() / (double)(1u << 31);
    public int Int(int n) => (int)(Next() % (uint)n);
    public double Range(double a, double b) => a + (b - a) * Unit();
  }

  public static Quaternion RandomRotation(Lcg random)
  {
    // A uniformly random unit quaternion (Marsaglia).
    double u1 = random.Unit(), u2 = random.Unit() * 2 * Math.PI, u3 = random.Unit() * 2 * Math.PI;
    double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
    return new Quaternion((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
  }

  public static byte[] MaskOf(params int[] cells)
  {
    var mask = new byte[32];
    foreach (var bit in cells)
      mask[bit >> 3] |= (byte)(1 << (bit & 7));
    return mask;
  }

  public static ZoneGround GroundOf(Lcg random, float baseMetres)
  {
    var heights = new short[BakedFormat.GroundVertices];
    var weights = new byte[BakedFormat.GroundVertices];
    for (int i = 0; i < heights.Length; i++)
    {
      heights[i] = (short)random.Int(900);
      weights[i] = (byte)(random.Int(4) == 0 ? 0 : 255 - random.Int(60));
    }
    return new ZoneGround(baseMetres, heights, weights);
  }

  // The palette of the sample layer: every role, collision and kind of tag, and a tint.
  public static PaletteEntry[] SamplePalette() =>
  [
    Entry("stone_wall_2x1", BakedRole.Static, BakedCollision.Boxes, PaletteFlags.UniformScale | PaletteFlags.NoShadows,
      boxes: [new Box(0, 500, 0, 2000, 1000, 400), new Box(-300, 1200, 100, 400, 400, 400)], tint: "stone", others: ["wood_wall"]),
    Entry("wood_door", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect,
      tags: [Tag.OfBool("VALtima_TownPiece", true), Tag.OfString("VALtima_Kind", "door"), Tag.OfInt("hp", 5), Tag.OfFloat("weight", 1.5f)]),
    Entry("fire_pit", BakedRole.Copy, BakedCollision.None, PaletteFlags.NoCopyLight),
    Entry("chair", BakedRole.Seat, BakedCollision.Trunk, layer: 5),
    Entry("Piece_grausten_floor_2x2", BakedRole.Static, BakedCollision.Boxes, PaletteFlags.LOD0Only, tags: [Tag.OfInt("MatVar0", 2)], boxes: [new Box(0, -50, 0, 2000, 100, 2000)]),
  ];

  // The values of every type, for a bake's value sets.
  public static ValueSet SampleValueSet(int n) => new(
  [
    ZdoValue.OfFloat(1001, 12.5f + n), ZdoValue.OfVec3(1002, 1, 2, 3 + n), ZdoValue.OfQuat(1003, 0, 0, 0, 1), ZdoValue.OfInt(1004, 77 + n),
    ZdoValue.OfLong(1005, 1L << 40 | (uint)n), ZdoValue.OfString(1006, "creator " + n), ZdoValue.OfBytes(1007, [1, 2, 3, (byte)n]),
  ]);

  public static OperationInfo SampleOperation(int number, OperationKind kind, int valueSets = 0, OperationState state = OperationState.InLayer) =>
    new((ushort)number, kind, state, 1_700_000_000L + number, "Tester " + number, 1f, 2f, 3f, 4f, kind == OperationKind.BakeArea ? 40f : 0f, 10, 2, 1, "0.10.4",
      Enumerable.Range(0, valueSets).Select(SampleValueSet).ToArray());

  // The sample layer and the records it was made from, by zone. All features: every flag of every record, a registry with value sets,
  // sections of every kind in some zones, a zone with only a clear mask, the corners of the zone range.
  public sealed class Sample
  {
    public BakedLayer Layer;
    public Dictionary<ZoneKey, List<ZoneRecord>> Records = new();
    public PaletteEntry[] Palette;
    public ZoneKey[] Built;
  }

  public static Sample MakeSample(ulong seed = 1)
  {
    var random = new Lcg(seed);
    var sample = new Sample { Palette = SamplePalette() };
    var edit = LayerEdit.New("sample producer");
    foreach (var entry in sample.Palette)
      edit.PaletteIndexFor(entry);
    edit.AddOperation(SampleOperation(1, OperationKind.BakeArea, 2));
    edit.AddOperation(SampleOperation(2, OperationKind.BakeBox, 1));
    edit.AddOperation(SampleOperation(3, OperationKind.Unbake));
    edit.AddOperation(SampleOperation(4, OperationKind.Load));
    edit.AddOperation(SampleOperation(5, OperationKind.Drop));
    var zones = new[] { new ZoneKey(0, 0), new ZoneKey(1, 0), new ZoneKey(-1, 0), new ZoneKey(5, -3), new ZoneKey(1023, -1024), new ZoneKey(-1024, 1023) };
    uint id = 100;
    foreach (var zone in zones)
    {
      var list = new List<ZoneRecord>();
      int count = 40 + random.Int(30);
      for (int i = 0; i < count; i++)
      {
        int palette = random.Int(sample.Palette.Length);
        double x = zone.OriginX + random.Range(0, 63.99), z = zone.OriginZ + random.Range(0, 63.99), y = random.Range(28, 140);
        Quaternion? rotation = null;
        double yaw = random.Range(0, 360);
        Vector3? scale = random.Int(4) == 0 ? new Vector3((float)random.Range(0.5, 3), (float)random.Range(0.5, 3), (float)random.Range(0.5, 3)) : null;
        uint? lasting = sample.Palette[palette].Role == BakedRole.Live ? id++ : null;
        int source = 0, valueSet = 0, seed2 = -1;
        if (random.Int(3) == 0)
        {
          source = 1 + random.Int(2);
          valueSet = random.Int(source == 1 ? 2 : 1);
        }
        if (random.Int(3) == 0)
          seed2 = random.Int(12345);
        ZoneRecord record;
        if (random.Int(5) == 0)
        {
          rotation = RandomRotation(random);
          record = ZoneRecord.Create(palette, x, y, z, rotation, scale, lasting, source, valueSet, seed2);
        }
        else
          record = ZoneRecord.CreateYaw(palette, x, y, z, yaw, scale, lasting, source, valueSet, seed2);
        // A Live record under a bake needs its (source, id) unique, which a lasting id of its own guarantees.
        list.Add(record);
      }
      sample.Records[zone] = list;
      edit.AddRecords(list, 3f);
    }
    // Sections: a compiler's, in some zones; one zone holds nothing else.
    edit.SetSections(zones[0], new ZoneSections
    {
      Flags = ZoneFlags.ProtectFootprints | ZoneFlags.NoVegetation, Mask = MaskOf(0, 5, 255), Ground = GroundOf(random, 31.25f), Paint = Enumerable.Range(0, 4096).Select(i => (byte)(i % 4)).ToArray(),
      Extras = [new ZoneExtra("note", [9, 8, 7]), new ZoneExtra("empty", [])],
    });
    edit.SetSections(zones[3], new ZoneSections { Ground = GroundOf(random, 40f) });
    edit.SetSections(new ZoneKey(2, 2), new ZoneSections { Mask = MaskOf(1, 2, 3) });
    edit.SetSections(new ZoneKey(-7, 9), new ZoneSections { Flags = ZoneFlags.NoVegetation });
    var (layer, changed) = edit.Build();
    sample.Layer = layer;
    sample.Built = changed;
    return sample;
  }

  // ------------------------------------------------------------------------------------------------ comparing

  public static string Differ(BakedLayer a, BakedLayer b)
  {
    if (a.Flags != b.Flags) return $"header flags {a.Flags} against {b.Flags}";
    if (a.Producer != b.Producer) return $"producer '{a.Producer}' against '{b.Producer}'";
    if (a.Placements != b.Placements) return $"placements {a.Placements} against {b.Placements}";
    if (a.Palette.Count != b.Palette.Count) return $"palette {a.Palette.Count} entries against {b.Palette.Count}";
    for (int i = 0; i < a.Palette.Count; i++)
      if (!a.Palette[i].Equals(b.Palette[i]))
        return $"palette entry {i}: {a.Palette[i]} against {b.Palette[i]}";
    if (a.HasRegistry != b.HasRegistry) return "one has a registry";
    if (a.Registry.NextOperation != b.Registry.NextOperation) return "next operation";
    if (a.Registry.Operations.Count != b.Registry.Operations.Count) return "operations";
    for (int i = 0; i < a.Registry.Operations.Count; i++)
    {
      var x = a.Registry.Operations[i];
      var y = b.Registry.Operations[i];
      if (x.Number != y.Number || x.Kind != y.Kind || x.State != y.State || x.Time != y.Time || x.Who != y.Who || x.X1 != y.X1 || x.Z1 != y.Z1 || x.X2 != y.X2
          || x.Z2 != y.Z2 || x.Radius != y.Radius || x.Added != y.Added || x.Removed != y.Removed || x.Adopted != y.Adopted || x.Version != y.Version
          || x.ValueSets.Length != y.ValueSets.Length)
        return $"operation {x.Number}";
      for (int s = 0; s < x.ValueSets.Length; s++)
        if (!x.ValueSets[s].Equals(y.ValueSets[s]))
          return $"operation {x.Number} value set {s}";
    }
    if (a.Zones.Count != b.Zones.Count) return $"{a.Zones.Count} zones against {b.Zones.Count}";
    for (int i = 0; i < a.Zones.Count; i++)
    {
      var x = a.Zones[i];
      var y = b.Zones[i];
      if (x.Key != y.Key) return $"zone {x.Key} against {y.Key}";
      if (x.Flags != y.Flags || x.Placements != y.Placements || x.Live != y.Live) return $"zone {x.Key}: flags, count or live count";
      if (x.YMin != y.YMin || x.YMax != y.YMax) return $"zone {x.Key}: y bounds {x.YMin}..{x.YMax} against {y.YMin}..{y.YMax}";
      var da = a.Decode(x);
      var db = b.Decode(y);
      var ra = da.Records().ToList();
      var rb = db.Records().ToList();
      ra.Sort();
      rb.Sort();
      for (int k = 0; k < ra.Count; k++)
        if (!ra[k].Equals(rb[k]))
          return $"zone {x.Key}: record {k} {ra[k]} against {rb[k]}";
      if (!Same(da.ClearMask, db.ClearMask)) return $"zone {x.Key}: clear mask";
      if (!Same(da.Paint, db.Paint)) return $"zone {x.Key}: paint";
      if ((da.Ground == null) != (db.Ground == null)) return $"zone {x.Key}: ground";
      if (da.Ground != null && (da.Ground.Base != db.Ground.Base || !da.Ground.Heights.SequenceEqual(db.Ground.Heights) || !da.Ground.Weights.SequenceEqual(db.Ground.Weights)))
        return $"zone {x.Key}: ground values";
      if (da.Extras.Length != db.Extras.Length || da.Extras.Zip(db.Extras, (p, q) => p.Tag != q.Tag || !p.Data.SequenceEqual(q.Data)).Any(d => d))
        return $"zone {x.Key}: extras";
    }
    return null;
  }

  private static bool Same(byte[] a, byte[] b) => a == null ? b == null : b != null && a.SequenceEqual(b);
}

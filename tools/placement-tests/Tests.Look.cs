// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The look of a record under instancing (spec 2.5, 14.1): the hash of its place, the seed and the variant words derived from it, and the buckets
// of VALtima's grausten floors. The expected values are fixtures/look-vectors.tsv and fixtures/look-buckets.tsv, which look_digest.py wrote from
// the words of the spec (a transcription in Python that shares no code with BakedFormat.cs); rerun it after a change of the rules, never to make
// a failing test pass.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // The lines of a fixture that are not comments, split at spaces.
  private static List<string[]> LookLines(string name) =>
    File.ReadAllLines(Fixture(name)).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(' ')).ToList();

  private static double LookDouble(string text) => double.Parse(text, CultureInfo.InvariantCulture);

  // What the code derives for one vector, as text; null when it is what the fixture says.
  private static string LookCheckRecord(string[] t)
  {
    int zx = int.Parse(t[1]), zz = int.Parse(t[2]);
    var record = new ZoneRecord { Zone = new ZoneKey(zx, zz), X = ushort.Parse(t[3]), Z = ushort.Parse(t[4]), Y = int.Parse(t[5]) };
    uint hash = Convert.ToUInt32(t[6], 16);
    int seed = int.Parse(t[7]), bucket = int.Parse(t[8]);
    var problems = new List<string>();
    if (record.LookHash != hash)
      problems.Add($"record hash {record.LookHash:x8}");
    if (BakedFormat.LookHash(record.WorldX, record.WorldY, record.WorldZ) != hash)
      problems.Add($"LookHash of its point {BakedFormat.LookHash(record.WorldX, record.WorldY, record.WorldZ):x8}");
    if (BakedFormat.DerivedSeed(hash) != seed || record.EffectiveSeed != seed)
      problems.Add($"seed {BakedFormat.DerivedSeed(hash)} / {record.EffectiveSeed}");
    if (seed % 8 != bucket)
      problems.Add("bucket");
    for (int slot = 0; slot < 4; slot++)
    {
      double u = BakedFormat.VariantUnit(hash, slot);
      if (u < 0.0 || u >= 1.0 || u * 65536.0 != int.Parse(t[9 + slot]))
        problems.Add($"slot {slot} word {u * 65536.0}");
    }
    return problems.Count == 0 ? null : $"({t[1]},{t[2]}) X {t[3]} Z {t[4]} Y {t[5]}: {string.Join(", ", problems)}, expected {t[6]} seed {t[7]}";
  }

  private static void LookVectorsTest()
  {
    Section("look: the hash, seed, bucket and variant words of the spec's rules, against a Python transcription");
    var lines = LookLines("look-vectors.tsv");
    var records = lines.Where(t => t[0] == "rec").ToList();
    var points = lines.Where(t => t[0] == "pt").ToList();
    C(records.Count >= 300 && points.Count >= 9, $"the fixture has {records.Count} record vectors and {points.Count} points");
    var bad = records.Select(LookCheckRecord).Where(m => m != null).ToList();
    C(bad.Count == 0, bad.Count == 0 ? $"all {records.Count} record vectors: look hash, derived seed, bucket (seed mod 8) and the variant words of slots 0 to 3" : bad.Count + " vectors differ, the first: " + bad[0]);
    var badPoints = points.Where(t => BakedFormat.LookHash(LookDouble(t[1]), LookDouble(t[2]), LookDouble(t[3])) != Convert.ToUInt32(t[4], 16)).ToList();
    C(badPoints.Count == 0, badPoints.Count == 0 ? $"all {points.Count} points given as doubles hash as the transcription does" : $"{badPoints.Count} points differ, the first: {string.Join(" ", badPoints[0])}");
    // The same from four threads at once: the functions keep no state.
    var wrong = 0;
    Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
    {
      foreach (var t in records)
        if (LookCheckRecord(t) != null)
          System.Threading.Interlocked.Increment(ref wrong);
    });
    C(wrong == 0, "and the same from four threads at once");
    // The vectors reach the corners of the world and halves.
    C(records.Any(t => t[1] == "-1024" && t[2] == "-1024") && records.Any(t => t[1] == "1023" && t[2] == "1023") && records.Any(t => int.Parse(t[5]) < 0)
      && records.Count(t => int.Parse(t[3]) % 128 == 64 && int.Parse(t[4]) % 128 == 64) >= 8, "they include the corner zones, a point below the sea and points whose x * 1000 is exactly a half");
  }

  private static void LookRulesTest()
  {
    Section("look: millimetres, ties to the even number, the stored seed and the shape of the variant word");
    // The hash is over the point in whole millimetres.
    C(BakedFormat.LookHash(1.0004, 2, 3) == BakedFormat.LookHash(1.0, 2, 3) && BakedFormat.LookHash(1.0006, 2, 3) == BakedFormat.LookHash(1.001, 2, 3)
      && BakedFormat.LookHash(1.0, 2, 3) != BakedFormat.LookHash(1.001, 2, 3), "the point is rounded to the millimetre: 0.4 mm is the same, 1 mm differs");
    C(BakedFormat.LookHash(1, 2, 3) != BakedFormat.LookHash(2, 1, 3) && BakedFormat.LookHash(1, 2, 3) != BakedFormat.LookHash(1, 3, 2) && BakedFormat.LookHash(1, 2, 3) != BakedFormat.LookHash(3, 2, 1),
      "x, y and z are in order: swapping two changes the hash");
    // Halves go to the even millimetre (62.5 -> 62, 187.5 -> 188, 312.5 -> 312, 437.5 -> 438), as Python's round and the transcription do.
    C(BakedFormat.LookHash(0.0625, 0, 0) == BakedFormat.LookHash(0.062, 0, 0) && BakedFormat.LookHash(0.0625, 0, 0) != BakedFormat.LookHash(0.063, 0, 0), "62.5 mm is 62");
    C(BakedFormat.LookHash(0.1875, 0, 0) == BakedFormat.LookHash(0.188, 0, 0) && BakedFormat.LookHash(0.1875, 0, 0) != BakedFormat.LookHash(0.187, 0, 0), "187.5 mm is 188");
    C(BakedFormat.LookHash(0.3125, 0, 0) == BakedFormat.LookHash(0.312, 0, 0) && BakedFormat.LookHash(0.4375, 0, 0) == BakedFormat.LookHash(0.438, 0, 0), "312.5 mm is 312, 437.5 mm is 438");
    C(BakedFormat.LookHash(-0.0625, 0, 0) == BakedFormat.LookHash(-0.062, 0, 0) && BakedFormat.LookHash(-0.1875, 0, 0) == BakedFormat.LookHash(-0.188, 0, 0), "and below zero the same way: -62.5 is -62, -187.5 is -188");
    C(BakedFormat.LookHash(-0.0, 0, 0) == BakedFormat.LookHash(0, 0, 0), "negative zero is zero");
    // Neighbours one step of the 1/1024 m grid apart often share a millimetre; those a millimetre or more apart do not.
    var a = new ZoneRecord { Zone = new ZoneKey(0, 0), X = 500, Z = 500, Y = 1000 };
    var b = a;
    b.X = 502;
    C(a.LookHash != b.LookHash, "two steps of the grid (1.95 mm) give two looks");

    // The seed: the stored one wins, wherever it is in range; else the hash mod 12,345.
    var plain = ZoneRecord.Create(0, 12.5, 30.0, 7.25);
    var stored = ZoneRecord.Create(0, 12.5, 30.0, 7.25, seed: 777);
    C(!plain.HasSeed && plain.EffectiveSeed == (int)(plain.LookHash % 12345u) && stored.HasSeed && stored.EffectiveSeed == 777 && stored.LookHash == plain.LookHash,
      "a record without a seed gets its hash mod 12,345; one with a seed keeps it, and the same place has the same hash");
    foreach (int seed in new[] { 0, 12344 })
      C(ZoneRecord.Create(0, 12.5, 30.0, 7.25, seed: seed).EffectiveSeed == seed, $"the stored seed {seed} (the ends of the range) is kept");
    C(BakedFormat.DerivedSeed(0) == 0 && BakedFormat.DerivedSeed(12344) == 12344 && BakedFormat.DerivedSeed(12345) == 0 && BakedFormat.DerivedSeed(12346) == 1 && BakedFormat.DerivedSeed(uint.MaxValue) == 6000,
      "DerivedSeed is the hash mod 12,345: 0, 12344, 12345 -> 0, 12346 -> 1, 4,294,967,295 -> 6000");
    var random = new Lcg(5);
    int lowest = int.MaxValue, highest = int.MinValue;
    var seen = new bool[8];
    for (int i = 0; i < 200_000; i++)
    {
      int seed = BakedFormat.DerivedSeed(random.Next() * 2u + (uint)random.Int(2));
      lowest = Math.Min(lowest, seed);
      highest = Math.Max(highest, seed);
      seen[seed % 8] = true;
    }
    C(lowest >= 0 && highest <= BakedFormat.MaxSeed && seen.All(x => x), $"200,000 hashes give seeds from {lowest} to {highest} (0 to 12,344) in all 8 buckets");
    // The block's own array of seeds follows the same rule.
    var edit = LayerEdit.New("seeds");
    edit.PaletteIndexFor(Entry("piece_a"));
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3.5, 40, 4.5, 0), ZoneRecord.CreateYaw(0, 7.5, 40, 4.5, 0, seed: 12000), ZoneRecord.CreateYaw(0, 9.5, 41, 8.5, 0)]);
    var layer = edit.Build().Layer;
    layer.TryGetZoneRow(0, 0, out var row);
    var data = layer.Decode(row);
    bool arrays = true;
    for (int k = 0; k < data.Count; k++)
    {
      var r = data.Record(k);
      arrays &= data.Seeds[k] == (r.HasSeed ? r.Seed : BakedFormat.DerivedSeed(BakedFormat.LookHash(data.WorldX(k), data.WorldY(k), data.WorldZ(k)))) && data.Seeds[k] == r.EffectiveSeed;
    }
    C(arrays && data.Seeds.Contains(12000), "ZoneData.Seeds has the stored seed where there is one and the derived seed elsewhere");

    // The variant word: the low 16 bits of the hash xor the low 16 bits of slot * 0x9E3779B9 (0x79B9, 0xF372, 0x6D2B for slots 1 to 3), over 65,536.
    C(BakedFormat.VariantUnit(0, 0) == 0.0 && BakedFormat.VariantUnit(0, 1) * 65536 == 31161 && BakedFormat.VariantUnit(0, 2) * 65536 == 62322 && BakedFormat.VariantUnit(0, 3) * 65536 == 27947,
      "slots 1, 2 and 3 of hash 0 are 0x79B9, 0xF372 and 0x6D2B over 65,536 (the multiple of 0x9E3779B9 wraps in 32 bits)");
    C(BakedFormat.VariantUnit(0xFFFFFFFFu, 0) * 65536 == 65535 && BakedFormat.VariantUnit(0xABCD1234u, 0) * 65536 == 0x1234 && BakedFormat.VariantUnit(0x00001234u, 0) == BakedFormat.VariantUnit(0xFFFF1234u, 0),
      "only the low 16 bits of the hash count");
    C(BakedFormat.VariantUnit(0x00001234u, 1) * 65536 == (0x1234 ^ 0x79B9) && BakedFormat.VariantUnit(0x12345678u, 2) * 65536 == (0x5678 ^ 0xF372), "and the slot's word is xor-ed into them");
    var units = new List<double>();
    for (uint h = 0; h < 100_000; h++)
      units.Add(BakedFormat.VariantUnit(h * 2654435761u, h % 5 == 0 ? 3 : 0));
    C(units.All(u => u >= 0.0 && u < 1.0) && Math.Abs(units.Average() - 0.5) < 0.01, $"over 100,000 hashes the words are in [0, 1) and average {units.Average():0.####}");
    // One position, different slots: the variants of a piece's slots are not all the same word.
    C(Enumerable.Range(0, 8).Select(s => BakedFormat.VariantUnit(0x5BD1E995u, s)).Distinct().Count() == 8, "the eight slots of one hash give eight different words");
  }

  // The buckets of VALtima's file, recomputed here and compared with what the transcription counted.
  private static void LookBucketsValtimaTest()
  {
    Section("look: the seed buckets of VALtima's 40,159 grausten floors (and of every record), against the transcription");
    if (!NeedValtima("LookBucketsValtimaTest"))
      return;
    var bytes = Valtima();
    var layer = BakedLayer.Read(bytes, bytes.Length);
    var expected = new Dictionary<string, string[]>();
    foreach (var t in LookLines("look-buckets.tsv"))
      expected[t[0]] = t;
    C(expected.ContainsKey("ALL") && expected.ContainsKey("Piece_grausten_floor_2x2"), "the fixture has the whole file and the grausten floors");

    var count = new Dictionary<string, int[]>();
    var seeds = new Dictionary<string, StringBuilder>();
    var words = new Dictionary<string, StringBuilder>();
    void Add(string key, int seed, string line)
    {
      if (!count.TryGetValue(key, out var buckets))
      {
        count[key] = buckets = new int[9];
        seeds[key] = new StringBuilder();
        words[key] = new StringBuilder();
      }
      buckets[seed % 8]++;
      buckets[8]++;
      seeds[key].Append(seed).Append('\n');
      words[key].Append(line);
    }
    var watch = System.Diagnostics.Stopwatch.StartNew();
    foreach (var row in layer.Zones)
    {
      if (row.Placements == 0)
        continue;
      var data = layer.Decode(row);
      for (int k = 0; k < data.Count; k++)
      {
        uint h = BakedFormat.LookHash(data.WorldX(k), data.WorldY(k), data.WorldZ(k));
        int seed = BakedFormat.DerivedSeed(h);
        var line = (int)(BakedFormat.VariantUnit(h, 0) * 65536.0) + " " + (int)(BakedFormat.VariantUnit(h, 1) * 65536.0) + " " + (int)(BakedFormat.VariantUnit(h, 2) * 65536.0) + "\n";
        Add("ALL", seed, line);
        var name = layer.Palette[data.Palette[k]].Name;
        if (name.StartsWith("Piece_grausten", StringComparison.Ordinal) || name.StartsWith("Ice_floor", StringComparison.Ordinal))
          Add(name, seed, line);
      }
    }
    System.Console.WriteLine($"   hashed {count["ALL"][8]:N0} records in {watch.ElapsedMilliseconds} ms");
    C(count.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(expected.Keys.OrderBy(k => k, StringComparer.Ordinal)), "the same kinds of the grausten and ice family as the transcription found: " + string.Join(", ", count.Keys.Where(k => k != "ALL")));
    static string Digest(StringBuilder text) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text.ToString()))).ToLowerInvariant();
    foreach (var key in expected.Keys)
    {
      if (!count.TryGetValue(key, out var buckets))
      {
        C(false, $"{key}: no records here");
        continue;
      }
      var t = expected[key];
      var want = Enumerable.Range(0, 8).Select(i => int.Parse(t[2 + i])).ToArray();
      C(buckets[8] == int.Parse(t[1]) && buckets.Take(8).SequenceEqual(want), $"{key}: {buckets[8]:N0} records in buckets {string.Join(" ", buckets.Take(8))} (the transcription: {string.Join(" ", want)})");
      C(Digest(seeds[key]) == t[10], $"{key}: the derived seeds, one a record in the file's order, have the transcription's digest");
      C(Digest(words[key]) == t[11], $"{key}: the variant words of slots 0 to 2 have the transcription's digest");
    }
    // The spec's figure, and that the hash spreads a regular grid of floors over the buckets.
    var floors = count["Piece_grausten_floor_2x2"];
    C(floors[8] == 40_159, "VALtima's file holds 40,159 Piece_grausten_floor_2x2 records (spec 2.5)");
    double mean = floors[8] / 8.0, sigma = Math.Sqrt(floors[8] / 8.0 * 7.0 / 8.0);
    C(floors.Take(8).All(n => Math.Abs(n - mean) <= 5 * sigma), $"each of the 8 buckets is within 5 sigma of an eighth ({mean:0} +- {5 * sigma:0}): {string.Join(" ", floors.Take(8))}");
  }
}

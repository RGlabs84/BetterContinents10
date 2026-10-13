// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The client renderer's drawing job (slice C): what BakedDraw.Fill does with a ring of built zones, with no engine: each instance's LOD at the
// player's LOD bias, the culling of cells against the frustum and of instances by distance, the batches by part, LOD, variant and seed bucket,
// the two buffers, the bounds of a draw list, and what bc_bake stats says of LOD0 and the last LOD apart.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BetterContinents;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlacementTests;

internal static partial class Tests
{
  // A wall of size 4 m: LOD0 within size * K / 0.2 = 40 m (K = 2) and the last LOD out to size * K / 0.05 = 160 m, times Draw Scale. A camera 40 m
  // west of the zone's centre sees the instances at distances 10, 38.5, 41.5, 63 and 65 m.
  private static void ClientLodTest()
  {
    Section("client: each instance's LOD");
    var wall = ClientKit.Kind("wall", ClientKit.TwoLods("wall", 4f, 0.2f, 0.05f, radius: 2f));
    var kinds = new BakedKind[] { wall };
    var near = ClientKit.BatchOf(wall, 0, 0);
    var far = ClientKit.BatchOf(wall, 1, 0);
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, -30, 0, 0, 0, null), (0, -1.5, 0, 0, 0, null), (0, 1.5, 0, 0, 0, null), (0, 23, 0, 0, 0, null), (0, 25, 0, 0, 0, null)), kinds, 1);
    C(zone.Drawn == 5 && zone.Kinds.Length == 1 && zone.Kinds[0].Count == 5, "five instances of one kind");
    var job = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 0.4f);
    BakedDraw.Fill(job);
    C(job.Done && job.Error == null, "the job ends without an error");
    var xs = (Batch b) => ClientKit.Filled(b, false).Select(m => (float)Math.Round(m.m03, 1)).OrderBy(x => x).ToList();
    C(xs(near).SequenceEqual([-30f, -1.5f]), $"within 40 m (10 and 38.5 m): LOD0 ({string.Join(", ", xs(near))})");
    C(xs(far).SequenceEqual([1.5f, 23f]), $"past 40 m and within the cull distance 64 m (41.5 and 63 m): the last LOD ({string.Join(", ", xs(far))})");
    C(job.KindLod0[0] == 2 && job.KindLod1[0] == 2 && job.InstancesDrawn == 4, "the job counts 2 at LOD0, 2 at the last LOD, and the one at 65 m is not drawn");
    C(ClientKit.Filled(near, true).Count == 0 && ClientKit.Filled(far, true).Count == 0, "the buffers in front are untouched until the job is adopted");

    // Draw Scale and Detail Scale move the distances
    var wide = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 1f, detailScale: 1.2f);
    BakedDraw.Fill(wide);
    C(ClientKit.Filled(near, false).Count == 3 && ClientKit.Filled(far, false).Count == 2,
      "Detail Scale 1.2 moves LOD0 out to 48 m and Draw Scale 1 the cull to 160 m: 3 at LOD0, and the 63 and 65 m pieces at the last LOD");
    var wider = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 1f, detailScale: 2f);
    BakedDraw.Fill(wider);
    C(ClientKit.Filled(near, false).Count == 5 && ClientKit.Filled(far, false).Count == 0, "Detail Scale 2 (LOD0 to 80 m) keeps all five at full detail");
    var tight = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 0.2f, detailScale: 0.5f);
    BakedDraw.Fill(tight);
    C(ClientKit.Filled(near, false).Count == 1 && ClientKit.Filled(far, false).Count == 0, "Detail Scale 0.5 (20 m) and Draw Scale 0.2 (32 m): only the instance at 10 m is drawn");

    // a scaled piece is as big as its largest scale: its distances grow with it
    var scaled = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 25, 0, 0, 0, new Vector3(2, 1, 1.5f)), (0, 25, 0, 5, 0, null)), kinds, 1);
    var scaledJob = ClientKit.Job([scaled], kinds, -40f, 0f, 0f, 2f, drawScale: 0.4f);
    BakedDraw.Fill(scaledJob);
    var scaledNear = ClientKit.Filled(near, false);
    C(scaledNear.Count == 1 && ClientKit.Filled(far, false).Count == 0 && Math.Abs(scaledNear[0].m23) < 0.01f,
      "of two pieces 65 m away, the one scaled by 2 (LOD0 to 80 m, cull at 128 m) is drawn at LOD0 and the plain one (cull at 64 m) is not");

    // a piece flagged LOD0Only never switches, and the last LOD's batches do not exist
    var only = ClientKit.Kind("only", ClientKit.TwoLods("only", 4f, 0.2f, 0.05f, 2f), flags: PaletteFlags.LOD0Only);
    C(only.FarFirst.Length == 0 && only.Batches.Length == 1, "LOD0Only makes no batch for the last LOD");
    var onlyZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, -30, 0, 0, 0, null), (0, 23, 0, 0, 0, null), (0, 25, 0, 0, 0, null)), [only], 1);
    var onlyJob = ClientKit.Job([onlyZone], [only], -40f, 0f, 0f, 2f, drawScale: 0.4f);
    BakedDraw.Fill(onlyJob);
    C(ClientKit.Filled(only.Batches[0], false).Count == 2, "...and its pieces stay at LOD0 to the cull distance (two of three here)");

    // a piece with no LOD group: culled where it would fill 1% of the screen (size = its diameter), never switched
    var bush = ClientKit.Kind("bush", ClientKit.NoLods("bush", 1.5f));
    var bushZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0, 0, 0, 0, null)), [bush], 1);
    var bushNear = ClientKit.Job([bushZone], [bush], -300f, 0f, 0f, 2f, drawScale: 1f);   // 300 m: cull at 3 m * 2 / 0.01 = 600 m
    BakedDraw.Fill(bushNear);
    var bushFar = ClientKit.Job([bushZone], [bush], -300f, 0f, 0f, 2f, drawScale: 0.4f);   // cull at 240 m
    BakedDraw.Fill(bushFar);
    C(ClientKit.Filled(bush.Batches[0], false).Count == 0, "with Draw Scale 0.4 the 3 m bush 300 m away is past its 240 m");
    BakedDraw.Fill(bushNear);
    C(ClientKit.Filled(bush.Batches[0], false).Count == 1, "...and drawn at the full 600 m");

    // who casts a shadow
    var part = ClientKit.Part(true, false);
    var lodPartFar = ClientKit.Part(false, true);
    var plain = ClientKit.Kind("c", null);
    C(BakedDraw.CastsFor(plain, part, 0, BakedShadows.Near) && !BakedDraw.CastsFor(plain, lodPartFar, 1, BakedShadows.Near), "Near: LOD0 casts, the last LOD does not");
    C(BakedDraw.CastsFor(plain, part, 0, BakedShadows.All) && BakedDraw.CastsFor(plain, lodPartFar, 1, BakedShadows.All), "All: both cast");
    C(!BakedDraw.CastsFor(plain, part, 0, BakedShadows.Off) && !BakedDraw.CastsFor(plain, lodPartFar, 1, BakedShadows.Off), "Off: none");
    var noShadow = ClientKit.Kind("n", null, flags: PaletteFlags.NoShadows);
    C(!BakedDraw.CastsFor(noShadow, part, 0, BakedShadows.All), "palette flag NoShadows never casts");
    C(!BakedDraw.CastsFor(plain, ClientKit.Part(true, false, ShadowCastingMode.Off), 0, BakedShadows.All), "a renderer that casts no shadow in the game casts none here");
  }

  // Cells outside the view are not drawn; a cell just outside it is, for the shadows its pieces cast into it.
  private static void ClientCullTest()
  {
    Section("client: the frustum culls cells");
    var wall = ClientKit.Kind("wall", ClientKit.TwoLods("wall", 4f, 0.2f, 0.05f, radius: 1f));
    var kinds = new BakedKind[] { wall };
    var near = ClientKit.BatchOf(wall, 0, 0);
    // a camera 40 m west looking east, a view 10 degrees high and wide: 3.5 m either side at 40 m
    var (fw, rt, up) = ClientKit.Basis(90, 0);
    Vector3 pos = new(-40, 0, 0);
    CullJob Make(BuiltZone zone, BakedShadows shadows)
    {
      var job = ClientKit.Job([zone], kinds, pos.x, pos.y, pos.z, 2f, drawScale: 4f, detailScale: 10f, shadows: shadows);
      BakedFrustum.Make(job.Planes, pos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
      return job;
    }
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0, 0, 0, 0, null), (0, 0, 0, -14, 0, null), (0, 0, 0, 25, 0, null), (0, 0, 0, -30, 0, null)), kinds, 1);
    var job = Make(zone, BakedShadows.Off);
    BakedDraw.Fill(job);
    var zs = ClientKit.Filled(near, false).Select(m => (float)Math.Round(m.m23, 1)).OrderBy(z => z).ToList();
    C(zs.SequenceEqual([0f]), $"with no shadows only the piece in view is drawn ({string.Join(", ", zs)}); the ones 14, 25 and 30 m to the side are not");
    job = Make(zone, BakedShadows.All);
    BakedDraw.Fill(job);
    zs = ClientKit.Filled(near, false).Select(m => (float)Math.Round(m.m23, 1)).OrderBy(z => z).ToList();
    C(zs.SequenceEqual([-14f, 0f]), $"with shadows the piece just outside the view (14 m to the side, about 9 m out) is drawn too ({string.Join(", ", zs)}); 25 and -30 m are too far to matter");
    // the shadow-only reach is for batches that cast: a NoShadows piece is drawn only in view
    var noShadow = ClientKit.Kind("flat", ClientKit.TwoLods("flat", 4f, 0.2f, 0.05f, 1f), flags: PaletteFlags.NoShadows);
    var zone2 = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0, 0, 0, 0, null), (0, 0, 0, -14, 0, null)), [noShadow], 1);
    var job2 = ClientKit.Job([zone2], [noShadow], pos.x, pos.y, pos.z, 2f, drawScale: 4f, detailScale: 10f, shadows: BakedShadows.All);
    BakedFrustum.Make(job2.Planes, pos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(job2);
    C(ClientKit.Filled(noShadow.Batches[0], false).Count == 1, "a piece flagged NoShadows is drawn only where it is seen");
    // Near: LOD0 casts, so it has the reach; the last LOD does not
    var lodZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0, 0, 10, 0, null)), kinds, 1);
    var nearJob = ClientKit.Job([lodZone], kinds, -40f, 0f, 0f, 2f, drawScale: 4f, detailScale: 10f, shadows: BakedShadows.Near);
    BakedFrustum.Make(nearJob.Planes, pos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(nearJob);
    C(ClientKit.Filled(near, false).Count == 1, "under Near the LOD0 piece beside the view casts into it and is drawn");
    var farZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 0, 0, 14, 0, null)), kinds, 1);
    var farPos = new Vector3(-100, 0, 0);   // 100 m away: the last LOD
    var farJob = ClientKit.Job([farZone], kinds, farPos.x, 0f, 0f, 2f, drawScale: 4f, shadows: BakedShadows.Near);
    BakedFrustum.Make(farJob.Planes, farPos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(farJob);
    C(ClientKit.Filled(ClientKit.BatchOf(wall, 1, 0), false).Count == 0, "...and the last LOD beside the view does not (it casts nothing under Near)");
    var farAll = ClientKit.Job([farZone], kinds, farPos.x, 0f, 0f, 2f, drawScale: 4f, shadows: BakedShadows.All);
    BakedFrustum.Make(farAll.Planes, farPos, fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(farAll);
    C(ClientKit.Filled(ClientKit.BatchOf(wall, 1, 0), false).Count == 1, "...but under All it does, and is drawn");
    // the zone's own box rejects a whole zone behind the camera
    var behind = BakedFrustum.Depth(job.Planes, zone.CellBox, 0);
    C(zone.CellUsed.Count(u => u) >= 1, "the zone says which cells hold pieces");
    C(!float.IsNaN(behind), "a cell's depth is a number");
    var far = ClientKit.Job([zone], kinds, 500f, 0f, 0f, 2f, drawScale: 4f);
    BakedFrustum.Make(far.Planes, new Vector3(500, 0, 0), fw, rt, up, 10f, 1f, 1000f, 0f, 0f);
    BakedDraw.Fill(far);
    C(ClientKit.Filled(near, false).Count == 0 && far.ZonesSeen == 0, "a zone behind the camera is skipped whole");
  }

  // Look variants and seed buckets: one batch for each variant of a part's slot and each bucket of a part with random values; every record in
  // the batch its own hash chooses.
  private static void ClientBatchTest()
  {
    Section("client: batches by variant and bucket");
    var piece = ClientKit.NoLods("grausten", 2f);
    var plainPart = ClientKit.Part(true, true);
    piece.Parts = [plainPart, ClientKit.Part(true, true)];
    piece.NearParts = [0, 1];
    piece.FarParts = [0, 1];
    var weights = new float[] { 1f, 1f, 2f };
    piece.Slots = [new VariantSlot { Key = 0, Weights = weights, Stride = 1 }];
    piece.Combos = 3;
    piece.Parts[0].Slot = 0; piece.Parts[0].SlotStride = 1; piece.Parts[0].SlotCount = 3;
    piece.Parts[1].Buckets = 8;
    piece.HasBuckets = true;
    var kind = ClientKit.Kind("grausten", piece);
    C(kind.Batches.Length == 3 + 8 && kind.NearFirst[0] == 0 && kind.NearFirst[1] == 3, "part 0 has a batch for each of its 3 variants, part 1 one for each of its 8 buckets");
    // 400 records at made-up places
    var rng = new System.Random(77);
    var records = new List<(int, double, double, double, double, Vector3?)>();
    for (int i = 0; i < 400; i++)
      records.Add((0, 128 + rng.NextDouble() * 60 - 30, rng.NextDouble() * 5, -64 + rng.NextDouble() * 60 - 30, rng.NextDouble() * 360, null));
    var data = ClientKit.Zone(2, -1, records.ToArray());
    var zone = BakedZoneBuild.Build(data, [kind], 1);
    var job = ClientKit.Job([zone], [kind], 128f, 0f, -64f, 2f, drawScale: 4f);
    BakedDraw.Fill(job);
    var variantCount = new int[3];
    var bucketCount = new int[8];
    for (int k = 0; k < data.Count; k++)
    {
      uint h = BakedFormat.LookHash(data.WorldX(k), data.WorldY(k), data.WorldZ(k));
      variantCount[BakedLook.Variant(h, 0, weights)]++;
      bucketCount[BakedLook.Bucket(BakedFormat.DerivedSeed(h))]++;
    }
    bool ok = true;
    for (int v = 0; v < 3; v++)
      ok &= ClientKit.Filled(kind.Batches[v], false).Count == variantCount[v];
    for (int b = 0; b < 8; b++)
      ok &= ClientKit.Filled(kind.Batches[3 + b], false).Count == bucketCount[b];
    C(ok, $"every record is in the batch its hash chooses: variants {string.Join("/", variantCount)}, buckets {string.Join("/", bucketCount)}");
    C(variantCount.All(n => n > 0) && bucketCount.All(n => n > 0), "all three variants and all eight buckets are used");
    // the same zone again gives the same lists
    var again = ClientKit.Job([zone], [kind], 128f, 0f, -64f, 2f, drawScale: 4f);
    BakedDraw.Fill(again);
    C(Enumerable.Range(0, 11).All(i => ClientKit.Filled(kind.Batches[i], false).Count == (i < 3 ? variantCount[i] : bucketCount[i - 3])), "a second job fills the same lists");
    // an entry's MatVar tag fixes a slot's variant for every record
    var fixedKind = ClientKit.Kind("fixed", piece);
    fixedKind.FixedVariant = [2];
    var fixedZone = BakedZoneBuild.Build(data, [fixedKind], 1);
    var fixedJob = ClientKit.Job([fixedZone], [fixedKind], 128f, 0f, -64f, 2f, drawScale: 4f);
    BakedDraw.Fill(fixedJob);
    C(ClientKit.Filled(fixedKind.Batches[2], false).Count == 400 && ClientKit.Filled(fixedKind.Batches[0], false).Count == 0 && ClientKit.Filled(fixedKind.Batches[1], false).Count == 0,
      "an entry with the tag MatVar0 = 2 draws every record in variant 2");

    // a record's own seed (flag 16) is the bucket's, whatever its place
    var seeded = ClientKit.Zone(0, 0, (0, 1, 0, 1, 0, null), (0, 2, 0, 2, 0, null));
    seeded.Flags[0] |= RecordFlags.Seed;
    seeded.Flags[1] |= RecordFlags.Seed;
    seeded.SeedRun = [5, 12_343];
    var seededZone = BakedZoneBuild.Build(seeded, [kind], 1);
    var seededJob = ClientKit.Job([seededZone], [kind], 0f, 0f, 0f, 2f, drawScale: 4f);
    BakedDraw.Fill(seededJob);
    C(ClientKit.Filled(kind.Batches[3 + 5], false).Count == 1 && ClientKit.Filled(kind.Batches[3 + 12_343 % 8], false).Count == 1, "stored seeds 5 and 12,343 are drawn in buckets 5 and 7");
  }

  // The two buffers: the job fills the one that is not in front; a batch's bounds hold its instances; calls of up to 1,023; the statistics.
  private static void ClientBufferTest()
  {
    Section("client: buffers, bounds and the statistics");
    var wall = ClientKit.Kind("stone_wall_2x1", ClientKit.TwoLods("stone_wall_2x1", 4f, 0.2f, 0.05f, radius: 2f));
    var kinds = new BakedKind[] { wall };
    // 2,500 pieces: 1,200 near the camera and 1,300 past 40 m
    var records = new List<(int, double, double, double, double, Vector3?)>();
    for (int i = 0; i < 1200; i++)
      records.Add((0, -31 + (i % 60) * 0.05, 0, -31 + (i / 60) * 3.0, 0, null));   // x -31..-28: 9 to 12 m from a camera at x -40
    for (int i = 0; i < 1300; i++)
      records.Add((0, 25 + (i % 60) * 0.05, 0, -31 + (i / 60) * 2.9, 0, null));   // x 25..28: 65 to 68 m
    var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records.ToArray()), kinds, 1);
    var job = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 1f);
    BakedDraw.Fill(job);
    var near = ClientKit.BatchOf(wall, 0, 0);
    var far = ClientKit.BatchOf(wall, 1, 0);
    C(ClientKit.Filled(near, false).Count == 1200 && ClientKit.Filled(far, false).Count == 1300, "1,200 at LOD0 and 1,300 at the last LOD");
    C(near.Buf[1 - near.Front].MinX <= -31 - 2f + 0.01f && near.Buf[1 - near.Front].MaxX >= -28.05f + 2f - 0.01f, "a draw list's bounds hold its instances and their radius");
    C(near.Buf[1 - near.Front].MaxX < 0f && far.Buf[1 - far.Front].MinX > 0f, "and no more than the cells they stand in");
    BakedDraw.Adopt(job);
    C(ClientKit.Filled(near, true).Count == 1200 && near.Front == 1, "adopting the job puts its buffer in front");
    // the next job fills the other buffer while the first is still in front
    var nothing = ClientKit.Job([zone], kinds, 5000f, 0f, 0f, 2f, drawScale: 1f);
    BakedDraw.Fill(nothing);
    C(ClientKit.Filled(near, true).Count == 1200, "a running job leaves the buffer in front alone");
    BakedDraw.Adopt(nothing);
    C(ClientKit.Filled(near, true).Count == 0 && ClientKit.Filled(near, false).Count == 1200, "and the swap shows the new job's lists (none: out of range)");
    BakedDraw.Fill(job);
    BakedDraw.Adopt(job);

    var lines = new List<string>();
    BakedDraw.Report(lines.Add, 1);
    var row = lines.FirstOrDefault(l => l.StartsWith("stone_wall_2x1"));
    C(row != null, "bc_bake stats has a line for the kind: " + string.Join(" | ", lines.Take(4)));
    if (row != null)
    {
      var t = Regex.Split(row.Trim(), @"\s+");
      // name, then LOD0: instances, calls, triangles, shadow-casting calls, ms; then the last LOD's
      C(t.Length == 11 && t[1] == "1,200" && t[2] == "2" && t[3] == "120,000" && t[4] == "2", $"LOD0: 1,200 instances in 2 calls (1,023 + 177) of 100 triangles, both casting ({row.Trim()})");
      C(t[6] == "1,300" && t[7] == "2" && t[8] == "15,600" && t[9] == "2", "the last LOD: 1,300 instances in 2 calls, 12 triangles each, both casting");
    }
    var total = lines.FirstOrDefault(l => l.StartsWith("total"));
    C(total != null && Regex.Split(total.Trim(), @"\s+")[1] == "1,200" && Regex.Split(total.Trim(), @"\s+")[6] == "1,300", "the totals are kept apart for LOD0 and the last LOD");
    C(lines.Any(l => l.Contains("LOD0")) && lines.Any(l => l.Contains("last LOD")) && lines.Any(l => l.StartsWith("Main thread:")) && lines.Any(l => l.StartsWith("Worker:")),
      "the report names both LODs and the main thread's and the worker's milliseconds");
    // Near: the last LOD does not cast
    var nearShadows = ClientKit.Job([zone], kinds, -40f, 0f, 0f, 2f, drawScale: 1f, shadows: BakedShadows.Near);
    BakedDraw.Fill(nearShadows);
    BakedDraw.Adopt(nearShadows);
    lines.Clear();
    BakedDraw.Report(lines.Add, 1);
    var nearRow = lines.First(l => l.StartsWith("stone_wall_2x1"));
    var tn = Regex.Split(nearRow.Trim(), @"\s+");
    C(tn[4] == "2" && tn[9] == "0", $"under Near the last LOD's calls cast nothing ({nearRow.Trim()})");
  }
  // A casting batch is drawn in segments with their own bounds (a zone each near the camera, four slabs beyond), so that Unity's shadow passes
  // cull it; a batch that casts nothing stays one segment.
  private static void ClientSegmentTest()
  {
    Section("client: casting batches drawn by zone near the camera and by slab beyond");
    C(BakedDraw.SegmentKey(0, 0) != BakedDraw.SegmentKey(1, 0) && BakedDraw.SegmentKey(2, -2) != BakedDraw.SegmentKey(-2, 2), "each near zone is its own segment");
    C(BakedDraw.SegmentKey(3, 0) == BakedDraw.SegmentKey(9, -9) && BakedDraw.SegmentKey(-3, 5) == BakedDraw.SegmentKey(-9, 0)
      && BakedDraw.SegmentKey(0, 3) == BakedDraw.SegmentKey(2, 9) && BakedDraw.SegmentKey(-2, -3) == BakedDraw.SegmentKey(2, -9),
      "the zones beyond fall in four slabs: east, west, north, south");
    C(new[] { BakedDraw.SegmentKey(3, 0), BakedDraw.SegmentKey(-3, 0), BakedDraw.SegmentKey(0, 3), BakedDraw.SegmentKey(0, -3) }.Distinct().Count() == 4
      && BakedDraw.SegmentKey(3, 0) > BakedDraw.SegmentKey(2, 2), "the four slabs are apart from each other and from every near zone");

    var wall = ClientKit.Kind("wall", ClientKit.NoLods("wall", 1f));
    var kinds = new BakedKind[] { wall };
    (int Zx, int Zz)[] at = [(0, 0), (1, 0), (0, -2), (3, 0), (4, 1), (-5, 0), (1, 3), (2, -3)];
    var zones = at.Select(z => BakedZoneBuild.Build(ClientKit.Zone(z.Zx, z.Zz,
      Enumerable.Range(0, 10).Select(i => (0, z.Zx * 64.0 + i, 0.0, z.Zz * 64.0 + i * 0.5, 0.0, (Vector3?)null)).ToArray()), kinds, 1)).ToArray();
    // the ring's order is not the segments': the zones come mixed
    var mixed = new[] { zones[3], zones[0], zones[5], zones[4], zones[1], zones[7], zones[2], zones[6] };
    var job = ClientKit.Job(mixed, kinds, 0f, 0f, 0f, 2f, drawScale: 1f);
    BakedDraw.Fill(job);
    var batch = ClientKit.BatchOf(wall, 0, 0);
    var buf = batch.Buf[1 - batch.Front];
    C(buf.Count == 80, $"all 80 instances are drawn ({buf.Count})");
    C(buf.Segs == 7, $"3 near zones and 4 slabs make 7 segments ({buf.Segs})");
    bool together = true, inside = true;
    for (int s = 0; s < buf.Segs; s++)
    {
      int g = s * 6, n = buf.SegEnd(s) - buf.SegStart[s];
      for (int i = buf.SegStart[s]; i < buf.SegEnd(s); i++)
      {
        var m = buf.M[i];
        inside &= m.m03 >= buf.SegBox[g] && m.m13 >= buf.SegBox[g + 1] && m.m23 >= buf.SegBox[g + 2]
          && m.m03 <= buf.SegBox[g + 3] && m.m13 <= buf.SegBox[g + 4] && m.m23 <= buf.SegBox[g + 5];
      }
      // a near segment holds one zone's 10; the east slab holds two zones' 20
      together &= n == 10 || (n == 20 && buf.SegBox[g] > 2.5f * 64f - 20f);
    }
    C(inside, "every instance is inside its segment's bounds");
    C(together, "each segment holds one zone, or one slab's zones, together");
    bool clear = true;
    for (int s = 0; s < buf.Segs; s++)
    {
      int g = s * 6;
      float cx = (buf.SegBox[g] + buf.SegBox[g + 3]) * 0.5f, cz = (buf.SegBox[g + 2] + buf.SegBox[g + 5]) * 0.5f;
      bool slab = Math.Abs(cx) > 2.5f * 64f || Math.Abs(cz) > 2.5f * 64f;
      // outside the square of 128 m around the camera on x or on z
      if (slab)
        clear &= buf.SegBox[g] > 128f || buf.SegBox[g + 3] < -128f || buf.SegBox[g + 2] > 128f || buf.SegBox[g + 5] < -128f;
    }
    C(clear, "no slab's bounds come within 128 m of the camera");
    BakedDraw.Adopt(job);
    var lines = new List<string>();
    BakedDraw.Report(lines.Add, 8);
    var row = lines.First(l => l.StartsWith("wall"));
    var t = Regex.Split(row.Trim(), @"\s+");
    C(t[2] == "7" && t[4] == "7", $"bc_bake stats counts a call for each segment ({row.Trim()})");

    // casting nothing: one segment, one call
    var off = ClientKit.Job(mixed, kinds, 0f, 0f, 0f, 2f, drawScale: 1f, shadows: BakedShadows.Off);
    BakedDraw.Fill(off);
    var offBuf = batch.Buf[1 - batch.Front];
    C(offBuf.Count == 80 && offBuf.Segs == 1 && offBuf.Calls(BakedDraw.MaxPerCall) == 1, $"with Shadows Off the batch is one segment in one call ({offBuf.Segs})");
  }
}

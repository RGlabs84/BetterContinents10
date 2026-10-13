// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The client renderer's pure parts (slice C): the look variants of a record (spec 2.5), the arithmetic that places a piece and welds its
// collision, and the frustum the cull-and-LOD job is given.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // The variant of a slot is the first whose running weight passes u times the total; the seed and the bucket follow the hash. Every call, and
  // every record of VALtima's file, gets the same answer, and the 40,159 grausten floors fall about evenly into the eight buckets.
  private static void ClientLookTest()
  {
    Section("client: look variants");
    // the weighted choice
    var w = new float[] { 1f, 1f, 2f };
    C(BakedLook.Variant(0.0, w) == 0 && BakedLook.Variant(0.2499, w) == 0 && BakedLook.Variant(0.25, w) == 1 && BakedLook.Variant(0.4999, w) == 1
      && BakedLook.Variant(0.5, w) == 2 && BakedLook.Variant(0.9999, w) == 2, "weights 1, 1, 2 split u at 0.25 and 0.5");
    C(BakedLook.Variant(0.0, new float[] { 0f, 1f }) == 1 && BakedLook.Variant(0.9, new float[] { 1f, 0f }) == 0, "a variant of weight 0 is never chosen");
    C(BakedLook.Variant(0.7, new float[] { 0f, 0f }) == 0 && BakedLook.Variant(0.7, Array.Empty<float>()) == 0, "no weight at all is variant 0");
    var hist = new int[3];
    for (uint h = 0; h < 65_536; h++)
      hist[BakedLook.Variant(h, 0, w)]++;
    C(hist[0] == 16_384 && hist[1] == 16_384 && hist[2] == 32_768, $"over every low 16 bits the variants come in the weights' ratio ({string.Join(", ", hist)})");
    C(BakedLook.Variant(12345u, 3, w) == BakedLook.Variant(12345u, 3, w), "the same hash and slot choose the same variant");
    bool slotsDiffer = false;
    for (uint h = 0; h < 200 && !slotsDiffer; h++)
      slotsDiffer = BakedLook.Variant(h, 0, w) != BakedLook.Variant(h, 1, w);
    C(slotsDiffer, "another slot of the same piece draws from the same hash differently");
    C(BakedLook.Bucket(0) == 0 && BakedLook.Bucket(7) == 7 && BakedLook.Bucket(8) == 0 && BakedLook.Bucket(12_344) == 12_344 % 8, "a seed's bucket is the seed mod 8");

    // a record's hash and seed come from the format (A's functions): the same point gives the same hash, however it is asked
    var record = ZoneRecord.CreateYaw(0, 10.5, 31.25, -20.125, 90);
    C(record.LookHash == BakedFormat.LookHash(record.WorldX, record.WorldY, record.WorldZ), "ZoneRecord.LookHash is BakedFormat.LookHash of the record's point");
    C(record.EffectiveSeed == BakedFormat.DerivedSeed(record.LookHash) && record.EffectiveSeed >= 0 && record.EffectiveSeed <= 12_344, "a record with no seed of its own gets h mod 12,345");
    var seeded = ZoneRecord.CreateYaw(0, 10.5, 31.25, -20.125, 90, seed: 4321);
    C(seeded.EffectiveSeed == 4321 && BakedLook.Bucket(seeded.EffectiveSeed) == 4321 % 8, "a stored seed is the record's, and its bucket follows it");

    // VALtima's grausten floors: a stable bucket for each, and the buckets used evenly
    if (!NeedValtima("ClientLookTest"))
      return;
    var layer = BakedLayer.Read(Valtima(), Valtima().Length);
    int grausten = -1;
    for (int i = 0; i < layer.Palette.Count; i++)
      if (layer.Palette[i].Candidates[0].Name == "Piece_grausten_floor_2x2")
      {
        grausten = i;
        break;
      }
    C(grausten >= 0, "VALtima's palette has Piece_grausten_floor_2x2");
    var buckets = new int[8];
    var again = new int[8];
    long floors = 0;
    foreach (var row in layer.Zones)
    {
      if (row.Placements == 0)
        continue;
      var data = layer.Decode(row);
      for (int k = 0; k < data.Count; k++)
      {
        if (data.Palette[k] != grausten)
          continue;
        floors++;
        buckets[BakedLook.Bucket(BakedFormat.DerivedSeed(BakedFormat.LookHash(data.WorldX(k), data.WorldY(k), data.WorldZ(k))))]++;
        again[BakedLook.Bucket(data.Record(k).EffectiveSeed)]++;
      }
    }
    C(floors == 40_159, $"VALtima's file holds 40,159 grausten floors (found {floors})");
    C(buckets.SequenceEqual(again), "the bucket of every floor is the same asked two ways");
    double mean = floors / 8.0, sd = Math.Sqrt(floors * (1 / 8.0) * (7 / 8.0));
    C(buckets.All(n => Math.Abs(n - mean) < 5 * sd), $"the eight buckets hold {string.Join(", ", buckets)} floors, each within 5 sd of {mean:0}");
  }

  // The matrix of a record: the piece turned, scaled and put so that its anchor lands on the record's point. Checked against VALtima's own
  // TownBuild.Place for a turn about y, and against a quaternion's rotation worked out apart for a full rotation.
  private static void ClientPlaceTest()
  {
    Section("client: where a record puts its piece");
    var rng = new System.Random(20261007);
    double worst = 0;
    for (int n = 0; n < 2000; n++)
    {
      double x = rng.NextDouble() * 4000 - 2000, y = rng.NextDouble() * 200, z = rng.NextDouble() * 4000 - 2000, yaw = rng.NextDouble() * 360;
      float sx = 0.5f + (float)rng.NextDouble() * 2f, sy = 0.5f + (float)rng.NextDouble() * 2f, sz = 0.5f + (float)rng.NextDouble() * 2f;
      var anchor = new Vector3((float)(rng.NextDouble() - 0.5) * 4, (float)(rng.NextDouble() - 0.5) * 4, (float)(rng.NextDouble() - 0.5) * 4);
      var a = BakedMath.FromYaw(yaw, sx, sy, sz);
      var m = BakedMath.Place(a, x, y, z, anchor);
      // VALtima's Towns/TownBuild.Place, as it is
      float r = (float)yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
      float ax = anchor.x * sx, ay = anchor.y * sy, az = anchor.z * sz;
      float v00 = c * sx, v02 = s * sz, v03 = (float)x - (c * ax + s * az), v11 = sy, v13 = (float)y - ay, v20 = -s * sx, v22 = c * sz, v23 = (float)z - (-s * ax + c * az);
      worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(m.m00 - v00), Math.Abs(m.m02 - v02)), Math.Max(Math.Abs(m.m11 - v11), Math.Max(Math.Abs(m.m20 - v20), Math.Abs(m.m22 - v22)))));
      worst = Math.Max(worst, Math.Max(Math.Abs(m.m03 - v03) / 4000.0, Math.Max(Math.Abs(m.m13 - v13) / 4000.0, Math.Abs(m.m23 - v23) / 4000.0)));
      // the point of the record is where the anchor lands
      var landed = BakedMath.Point(m, anchor);
      worst = Math.Max(worst, Math.Max(Math.Abs(landed.x - x), Math.Max(Math.Abs(landed.y - y), Math.Abs(landed.z - z))) / 4000.0);
      C(m.m33 == 1f && m.m30 == 0f && m.m31 == 0f && m.m32 == 0f && m.m01 == 0f && m.m10 == 0f && m.m12 == 0f && m.m21 == 0f, "a yaw placement has no tilt and an affine last row");
    }
    C(worst < 1e-5, $"a turn about y places the piece as VALtima's Place does, and the anchor lands on the point (worst relative difference {worst:0.0e+0})");

    // a quaternion: the rotation it makes, worked out independently
    double worstQ = 0;
    for (int n = 0; n < 2000; n++)
    {
      // a random rotation: four numbers, made a unit quaternion (no call into the engine)
      double qx = rng.NextDouble() - 0.5, qy = rng.NextDouble() - 0.5, qz = rng.NextDouble() - 0.5, qw = rng.NextDouble() - 0.5;
      double qn = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
      var q = new Quaternion((float)(qx / qn), (float)(qy / qn), (float)(qz / qn), (float)(qw / qn));
      var v = new Vector3((float)(rng.NextDouble() - 0.5) * 8, (float)(rng.NextDouble() - 0.5) * 8, (float)(rng.NextDouble() - 0.5) * 8);
      var anchor = new Vector3((float)(rng.NextDouble() - 0.5) * 2, (float)(rng.NextDouble() - 0.5) * 2, (float)(rng.NextDouble() - 0.5) * 2);
      var scale = new Vector3(0.7f + (float)rng.NextDouble(), 0.7f + (float)rng.NextDouble(), 0.7f + (float)rng.NextDouble());
      var a = BakedMath.FromQuaternion(q.x, q.y, q.z, q.w, scale.x, scale.y, scale.z);
      var m = BakedMath.Place(a, 100, 50, -80, anchor);
      // x_world = R(S x) + t, with R from the quaternion by the textbook formula (rotating a vector: v + 2w(u x v) + 2u x (u x v))
      Vector3 Rotate(Vector3 p)
      {
        var u = new Vector3(q.x, q.y, q.z);
        var uv = Vector3.Cross(u, p);
        var uuv = Vector3.Cross(u, uv);
        return p + 2f * (q.w * uv + uuv);
      }
      var pivot = new Vector3(100, 50, -80) - Rotate(Vector3.Scale(anchor, scale));
      var expected = Rotate(Vector3.Scale(v, scale)) + pivot;
      var got = BakedMath.Point(m, v);
      worstQ = Math.Max(worstQ, (expected - got).magnitude);
    }
    C(worstQ < 1e-3, $"a full rotation turns and scales the piece as the quaternion says (worst error {worstQ:0.0e+0} m)");
    var yawQ = new Quaternion(0f, (float)Math.Sin(37 * Math.PI / 360), 0f, (float)Math.Cos(37 * Math.PI / 360));
    var viaQ = BakedMath.FromQuaternion(yawQ.x, yawQ.y, yawQ.z, yawQ.w, 1, 1, 1);
    var viaYaw = BakedMath.FromYaw(37, 1, 1, 1);
    C(Math.Abs(viaQ.M00 - viaYaw.M00) < 1e-6 && Math.Abs(viaQ.M02 - viaYaw.M02) < 1e-6 && Math.Abs(viaQ.M20 - viaYaw.M20) < 1e-6 && Math.Abs(viaQ.M22 - viaYaw.M22) < 1e-6
      && Math.Abs(viaQ.M11 - 1) < 1e-6, "the quaternion of a yaw places a piece as the yaw does");
    // Place with an origin keeps the collider's numbers small: x and z relative to the zone object
    var rel = BakedMath.Place(BakedMath.FromYaw(0, 1, 1, 1), 64_001.5, 12, -64_000.25, Vector3.zero, 64_000.0, -64_000.0);
    C(Math.Abs(rel.m03 - 1.5f) < 1e-6 && Math.Abs(rel.m13 - 12f) < 1e-6 && Math.Abs(rel.m23 - -0.25f) < 1e-6, "relative to a zone object 64 km out, a piece's place is still exact to the float");

    // the matrix product, the point and the determinant
    var m1 = BakedMath.Place(BakedMath.FromYaw(30, 1.5f, 2, 1), 1, 2, 3, Vector3.zero);
    var m2 = BakedMath.Place(BakedMath.FromYaw(-70, 1, 1, 3), -4, 5, 6, Vector3.zero);
    var p = new Vector3(0.3f, -1.2f, 2.5f);
    C((BakedMath.Point(BakedMath.Mul(m1, m2), p) - BakedMath.Point(m1, BakedMath.Point(m2, p))).magnitude < 1e-4, "Mul(a, b) applied to a point is a applied after b");
    C(BakedMath.Det3(m1) > 0 && BakedMath.Det3(BakedMath.Place(BakedMath.FromYaw(0, -1, 1, 1), 0, 0, 0, Vector3.zero)) < 0, "a mirrored piece has a negative determinant");
  }

  // The frustum the job culls against: widened by the margins so that a job made for one pose still holds after the camera has moved 2 m and
  // turned 5 degrees (the job runs again at that point), and cells are measured by their worst plane.
  private static void ClientFrustumTest()
  {
    Section("client: the frustum of a job");
    var planes = new float[24];
    var (fw, rt, up) = ClientKit.Basis(0, 0);
    BakedFrustum.Make(planes, new Vector3(0, 10, 0), fw, rt, up, 60f, 16f / 9f, 500f, 0f, 0f);
    float[] Box(float x0, float y0, float z0, float x1, float y1, float z1) => [x0, y0, z0, x1, y1, z1];
    float D(float[] b) => BakedFrustum.Depth(planes, b, 0);
    C(D(Box(-1, 9, 50, 1, 11, 52)) >= 0, "a box straight ahead is in view");
    C(D(Box(-1, 9, -52, 1, 11, -50)) < 0, "a box behind the camera is out");
    C(D(Box(200, 9, 10, 202, 11, 12)) < 0, "a box far to the side is out");
    C(D(Box(-1, 9, 600, 1, 11, 602)) < 0, "a box past the far plane is out");
    C(D(Box(-1, 200, 50, 1, 202, 52)) < 0, "a box far above the view is out");
    // a box straddling the edge touches the view; the half-angle of 60 degrees vertical at 16:9 is about 43.9 degrees across the width
    C(D(Box(40, 9, 49, 60, 11, 51)) >= 0, "a box across the edge of the view touches it");
    // margins: 3 m out beyond the plane and the angle
    BakedFrustum.Make(planes, new Vector3(0, 10, 0), fw, rt, up, 60f, 16f / 9f, 500f, 0f, 3f);
    float wide = D(Box(-1, 9, -2, 1, 11, -1));
    C(wide >= 0, "with a 3 m margin a box 1 m behind the camera is still in");
    C(D(Box(-1, 9, -6, 1, 11, -5)) < 0, "and one 5 m behind is out");

    // the promise: whatever the camera sees after it has moved up to 2 m and turned up to 5 degrees is inside the frustum made for where it was
    var rng = new System.Random(4242);
    int checkedPoints = 0, outside = 0;
    for (int trial = 0; trial < 200; trial++)
    {
      double yaw = rng.NextDouble() * 360, pitch = rng.NextDouble() * 120 - 60;
      var pos = new Vector3((float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 100), (float)(rng.NextDouble() * 1000));
      var (f0, r0, u0) = ClientKit.Basis(yaw, pitch);
      var wideStart = new float[24];
      BakedFrustum.Make(wideStart, pos, f0, r0, u0, 60f, 16f / 9f, 800f, BakedDraw.AngleMargin, BakedDraw.MoveMargin);
      // the camera after it moved and turned by no more than the refresh thresholds (a turn about a random axis)
      var move = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized * (float)(rng.NextDouble() * BakedDraw.RefreshDistance);
      var axis = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized;
      float angle = (float)(rng.NextDouble() * BakedDraw.RefreshAngle) * Mathf.Deg2Rad;
      Vector3 Turn(Vector3 v) => v * Mathf.Cos(angle) + Vector3.Cross(axis, v) * Mathf.Sin(angle) + axis * Vector3.Dot(axis, v) * (1 - Mathf.Cos(angle));
      var f1 = Turn(f0).normalized;
      var r1 = Turn(r0).normalized;
      var u1 = Turn(u0).normalized;
      var pos1 = pos + move;
      var exact = new float[24];
      BakedFrustum.Make(exact, pos1, f1, r1, u1, 60f, 16f / 9f, 800f, 0f, 0f);
      for (int i = 0; i < 100; i++)
      {
        // a point the new camera sees: in its own frame, inside its frustum
        float z = 1f + (float)rng.NextDouble() * 700f;
        float halfH = z * Mathf.Tan(30f * Mathf.Deg2Rad), halfW = halfH * 16f / 9f;
        float x = (float)(rng.NextDouble() * 2 - 1) * halfW * 0.999f, y = (float)(rng.NextDouble() * 2 - 1) * halfH * 0.999f;
        var point = pos1 + r1 * x + u1 * y + f1 * z;
        var box = new[] { point.x, point.y, point.z, point.x, point.y, point.z };
        if (BakedFrustum.Depth(exact, box, 0) < -1e-2f)
          continue;   // rounding put it just outside the exact frustum
        checkedPoints++;
        if (BakedFrustum.Depth(wideStart, box, 0) < 0)
          outside++;
      }
    }
    C(checkedPoints > 15_000 && outside == 0, $"every one of {checkedPoints} points the camera sees after moving 2 m and turning 5 degrees is inside the frustum made before ({outside} were not)");
  }
}

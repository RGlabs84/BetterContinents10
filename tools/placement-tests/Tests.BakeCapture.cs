// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of what the in-game bake keeps of a piece (BakeCapture.cs): Unity's Euler convention and the game's save of a
// rotation, where a piece is "the same place", the record a copied piece makes (its palette entry, look, scale and value set), what
// the layer cannot hold, and a whole ZDO copied out of the game's own value store.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static readonly BindingFlags BakeAny = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

  // Rotates a vector by a quaternion: v + 2w (q x v) + 2 q x (q x v).
  private static (double X, double Y, double Z) BakeRotate((double X, double Y, double Z, double W) q, (double X, double Y, double Z) v)
  {
    (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
      (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    var qv = (q.X, q.Y, q.Z);
    var t = Cross(qv, v);
    var u = Cross(qv, t);
    return (v.X + 2 * q.W * t.X + 2 * u.X, v.Y + 2 * q.W * t.Y + 2 * u.Y, v.Z + 2 * q.W * t.Z + 2 * u.Z);
  }

  private static bool BakeNear((double X, double Y, double Z) a, (double X, double Y, double Z) b, double eps = 1e-9) =>
    Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps && Math.Abs(a.Z - b.Z) < eps;

  // Hamilton product a * b.
  private static (double X, double Y, double Z, double W) BakeMul((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b) =>
    (a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
      a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
      a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
      a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

  private static (double X, double Y, double Z, double W) BakeQ(Quaternion q) => (q.x, q.y, q.z, q.w);

  private static void BakeRotationTest()
  {
    Section("capture: Euler angles to rotations, as Unity does them");
    // Unity: a positive y turns +z toward +x; a positive x turns +z down; a positive z turns +y toward -x (z first, then x, then y).
    var yaw90 = BakeMath.EulerToQuaternion(0, 90, 0);
    C(BakeNear(BakeRotate(yaw90, (0, 0, 1)), (1, 0, 0)), "yaw 90 turns forward to the east (+x)");
    var pitch90 = BakeMath.EulerToQuaternion(90, 0, 0);
    C(BakeNear(BakeRotate(pitch90, (0, 0, 1)), (0, -1, 0)), "pitch 90 turns forward down");
    var roll90 = BakeMath.EulerToQuaternion(0, 0, 90);
    C(BakeNear(BakeRotate(roll90, (0, 1, 0)), (-1, 0, 0)), "roll 90 turns up toward -x");
    // z first, then x, then y: q = qy * qx * qz.
    var rng = new System.Random(20261007);
    bool composed = true, unit = true;
    for (int i = 0; i < 2000; i++)
    {
      double x = rng.NextDouble() * 360, y = rng.NextDouble() * 360, z = rng.NextDouble() * 360;
      var q = BakeMath.EulerToQuaternion(x, y, z);
      var byParts = BakeMul(BakeMul(BakeMath.EulerToQuaternion(0, y, 0), BakeMath.EulerToQuaternion(x, 0, 0)), BakeMath.EulerToQuaternion(0, 0, z));
      composed &= BakeMath.AngleBetween(q, byParts) < 1e-6;
      unit &= Math.Abs(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W - 1) < 1e-12;
    }
    C(composed, "2000 random angles: Euler(x, y, z) is qy * qx * qz");
    C(unit, "and always a unit quaternion");
    C(BakeMath.AngleBetween(BakeMath.EulerToQuaternion(0, 0, 0), BakeMath.EulerToQuaternion(0, 360, 0)) < 1e-9, "yaw 360 is no turn");
    C(Math.Abs(BakeMath.AngleBetween(BakeMath.EulerToQuaternion(0, 0, 0), BakeMath.EulerToQuaternion(0, 90, 0)) - 90) < 1e-9, "the angle between none and 90 degrees is 90");
    C(Math.Abs(BakeMath.AngleBetween(BakeMath.EulerToQuaternion(0, 10, 0), BakeMath.EulerToQuaternion(0, 10.05, 0)) - 0.05) < 1e-6, "and 0.05 degrees apart is 0.05, where acos would lose it");
    C(BakeMath.AngleBetween((0, 0, 0, 1), (0, 0, 0, -1)) < 1e-9, "q and -q are one rotation");

    Section("capture: a record keeps a yaw when the piece is level, else the full rotation");
    C(BakeCapture.IsLevel(0f, 0f) && BakeCapture.IsLevel(0.003f, 359.998f), "pitch and roll under 0.005 degrees, either side of zero, are level");
    C(!BakeCapture.IsLevel(0.006f, 0f) && !BakeCapture.IsLevel(0f, 359.99f), "0.006 degrees of pitch, or 0.01 of roll the other way round, are not");
    C(BakeCapture.Wrap360(360.0) == 0.0 && Math.Abs(BakeCapture.Wrap360(-10.0) - 350.0) < 1e-9, "a yaw of 360 is 0 and of -10 is 350");
    var facts = BakeFacts("wall");
    BakeRecord Rec(float rx, float ry, float rz)
    {
      var copy = BakeCopyAt(1, 10f, 30f, 20f, ry, rx, rz);
      return BakeCapture.MakeRecord(copy, facts, BakedRole.Static, 7, 0, new BakeValueSetPool());
    }
    var level = Rec(0f, 137.5f, 0f);
    C(!level.Record.HasFullRotation && Math.Abs(BakedFormat.YawDegrees(level.Record.Yaw) - 137.5) < 0.004, "a level piece keeps its yaw (65,536 steps)");
    var nearly = Rec(0.003f, 10f, 359.998f);
    C(!nearly.Record.HasFullRotation && Math.Abs(BakedFormat.YawDegrees(nearly.Record.Yaw) - 10) < 0.004, "within 0.005 degrees of level: a yaw too");
    C(Rec(0.006f, 10f, 0f).Record.HasFullRotation, "0.006 degrees of pitch: a full rotation");
    var tilted = Rec(30f, 45f, 10f);
    var expect = BakeMath.EulerToQuaternion(30, 45, 10);
    C(tilted.Record.HasFullRotation, "a tilted piece keeps a full rotation");
    C(BakeMath.AngleBetween(BakeQ(tilted.Record.Rotation), expect) < 0.01, "and it is the same rotation, to 0.01 degrees");
    var flipped = Rec(180f, 0f, 0f);
    C(flipped.Record.HasFullRotation && BakeMath.AngleBetween(BakeQ(flipped.Record.Rotation), BakeMath.EulerToQuaternion(180, 0, 0)) < 0.01, "upside down (180, 0, 0) is kept whole, not mistaken for a yaw");
    var twoFlips = Rec(180f, 37f, 180f);
    C(!twoFlips.Record.HasFullRotation && Math.Abs(BakedFormat.YawDegrees(twoFlips.Record.Yaw) - 217.0) < 0.004, "(180, 37, 180) is two flips that cancel, a yaw of 217: the layer keeps the yaw alone");
    var steep = Rec(89f, 200f, 359f);
    C(steep.Record.HasFullRotation && BakeMath.AngleBetween(BakeQ(steep.Record.Rotation), BakeMath.EulerToQuaternion(89, 200, 359)) < 0.01, "a steep one too");
    double worst = 0;
    for (int i = 0; i < 20000; i++)
    {
      float x = (float)(rng.NextDouble() * 360), y = (float)(rng.NextDouble() * 360), z = (float)(rng.NextDouble() * 360);
      var r = Rec(x, y, z);
      worst = Math.Max(worst, BakeMath.AngleBetween(BakeQ(r.Record.Rotation), BakeMath.EulerToQuaternion(x, y, z)));
    }
    C(worst < 0.01, $"20,000 random rotations come back within 0.01 degrees (worst {worst:0.#####})");
    double worstYaw = 0;
    for (int i = 0; i < 20000; i++)
    {
      float y = (float)(rng.NextDouble() * 360);
      var r = Rec(0f, y, 0f);
      double d = Math.Abs(BakedFormat.YawDegrees(r.Record.Yaw) - y);
      worstYaw = Math.Max(worstYaw, Math.Min(d, 360 - d));
    }
    C(worstYaw <= 360.0 / 65536.0 / 2 + 1e-4, $"20,000 yaws come back within half a step (worst {worstYaw:0.#####})");

    Section("capture: what the game's save does to a rotation");
    // Against the game's own ZPackage round trip, which ZDO.Save and ZDO.Load use (when ZDO.Save writes a rotation at all).
    int compared = 0, differing = 0;
    for (int i = 0; i < 20000; i++)
    {
      // Mostly level pieces, as the game has them, and some tilted ones.
      float x = i % 3 == 0 ? (float)(rng.NextDouble() * 360) : (rng.Next(3) == 0 ? (float)(rng.NextDouble() * 1.5) : 0f);
      float y = (float)(rng.NextDouble() * 360);
      float z = i % 5 == 0 ? (float)(rng.NextDouble() * 360) : (rng.Next(3) == 0 ? (float)(rng.NextDouble() * 1.5) : 0f);
      if (rng.Next(7) == 0) { x = 360f - x * 0.001f; }
      var v = new Vector3(x, y, z);
      var mine = BakeMath.RotationAfterSave(x, y, z);
      Vector3 theirs;
      if (v.CloseToZero())
        theirs = Vector3.zero;
      else
      {
        var pkg = new ZPackage();
        pkg.WriteSmallRotation(v);
        pkg.SetPos(0);
        theirs = pkg.ReadSmallRotation();
      }
      compared++;
      if (mine.X != theirs.x || mine.Y != theirs.y || mine.Z != theirs.z)
      {
        differing++;
        if (differing < 4)
          System.Console.WriteLine($"   differs: ({x}, {y}, {z}) mine {mine} theirs {theirs}");
      }
    }
    C(compared == 20000 && differing == 0, $"RotationAfterSave equals the game's save and load for 20,000 angles ({differing} differ)");
    var cut = BakeMath.RotationAfterSave(0f, 37.3217f, 0f);
    C(cut.X == 0f && cut.Y == 37f && cut.Z == 0f, "a yaw of 37.3217 is saved as 37.0 (half degrees, cut)");
    C(BakeMath.RotationAfterSave(0.3f, 0.2f, 0.4f) == (0f, 0f, 0f), "a rotation within half a degree of none is not saved");
  }

  private static PieceCopy BakeCopyAt(int prefab, float x, float y, float z, float ry = 0f, float rx = 0f, float rz = 0f) =>
    new() { Prefab = prefab, PrefabName = "wall", X = x, Y = y, Z = z, RotX = rx, RotY = ry, RotZ = rz, Persistent = true };

  private static void BakePlaceTest()
  {
    Section("capture: the same place, within a centimetre and a tenth of a degree");
    var copy = BakeCopyAt(77, 100.5f, 31.25f, -200.75f, 37.3217f);
    C(BakeMath.SamePlace(copy, 77, 100.5f, 31.25f, -200.75f, 0f, 37.3217f, 0f), "exactly there: the same");
    C(BakeMath.SamePlace(copy, 77, 100.509f, 31.25f, -200.75f, 0f, 37.3217f, 0f), "9 mm away: the same");
    C(!BakeMath.SamePlace(copy, 77, 100.512f, 31.25f, -200.75f, 0f, 37.3217f, 0f), "12 mm away: not");
    C(!BakeMath.SamePlace(copy, 77, 100.5f, 31.262f, -200.75f, 0f, 37.3217f, 0f), "12 mm up: not (a piece above another is another)");
    C(!BakeMath.SamePlace(copy, 78, 100.5f, 31.25f, -200.75f, 0f, 37.3217f, 0f), "another prefab: not");
    C(BakeMath.SamePlace(copy, 77, 100.5f, 31.25f, -200.75f, 0f, 37.4f, 0f), "0.08 degrees off: the same");
    C(!BakeMath.SamePlace(copy, 77, 100.5f, 31.25f, -200.75f, 0f, 37.5f, 0f), "0.18 degrees off: not, though it is nearer the half degree the save keeps");
    var saved = BakeMath.RotationAfterSave(0f, 37.3217f, 0f);
    C(BakeMath.SamePlace(copy, 77, 100.5f, 31.25f, -200.75f, saved.X, saved.Y, saved.Z), "after a save and a load (37.0): the same");
    C(!BakeMath.SamePlace(copy, 77, 100.5f, 31.25f, -200.75f, 0f, 90f, 0f), "another way round: not (two beams on one pivot are two pieces)");
    var wrap = BakeCopyAt(5, 0f, 0f, 0f, 359.97f);
    C(BakeMath.SamePlace(wrap, 5, 0f, 0f, 0f, 0f, 0.02f, 0f), "359.97 and 0.02 are 0.05 degrees apart");
    var tiltedCopy = BakeCopyAt(6, 1f, 2f, 3f, 20f, 10.3f, 0.4f);
    var tiltedSaved = BakeMath.RotationAfterSave(10.3f, 20f, 0.4f);
    C(BakeMath.SamePlace(tiltedCopy, 6, 1f, 2f, 3f, tiltedSaved.X, tiltedSaved.Y, tiltedSaved.Z), "a tilted piece after a save: the same");
    var slight = BakeCopyAt(7, 1f, 2f, 3f, 20f, 0.7f, 0.3f);
    var slightSaved = BakeMath.RotationAfterSave(0.7f, 20f, 0.3f);
    C(slightSaved.X == 0f && slightSaved.Z == 0f && BakeMath.SamePlace(slight, 7, 1f, 2f, 3f, slightSaved.X, slightSaved.Y, slightSaved.Z),
      "a slight tilt the save drops (under one degree) is still the same piece after the load");
  }

  private static int BakeKey(string name) => name.GetStableHashCode();

  private static PrefabFacts BakeFacts(string name, bool syncsScale = false) => new(name, BakeWallParts, false, 0, syncsScale);

  private static ZdoValue[] BakeVals(params ZdoValue[] values) => values;

  private static void BakeRecordTest()
  {
    Section("capture: the record of a piece, its palette entry, look, scale and value set");
    var pool = new BakeValueSetPool();
    var mat0 = BakeKey("MatVar0");
    var seed = BakeKey("RandMatSeed");

    var plain = BakeCopyAt(1, 10f, 30f, 20f, 90f);
    plain.Values = BakeVals(ZdoValue.OfLong(BakeKey("creator"), 4242), ZdoValue.OfInt(BakeKey("creatorIndex"), 3));
    var rec = BakeCapture.MakeRecord(plain, BakeFacts("wall"), BakedRole.Static, 7, 0, pool);
    C(rec.Prefab == "wall" && rec.Role == BakedRole.Static && !rec.Entry.Protected && rec.Record.SourceNumber == 7 && !rec.Record.HasId, "the prefab, role, source and no id");
    var entry = rec.Entry;
    C(entry.Candidates.Length == 1 && entry.Candidates[0].Name == "wall" && entry.Candidates[0].AnchorX == 0 && entry.Candidates[0].AnchorY == 0 && entry.Candidates[0].AnchorZ == 0, "the palette entry has one candidate, the piece's own prefab, anchor 0");
    C(entry.Collision == BakedCollision.Prefab && entry.Layer == 0 && entry.Flags == PaletteFlags.None && !entry.HasTint && entry.Boxes.Length == 0 && entry.Tags.Length == 0, "collision Prefab, layer 0, no flags, tint, boxes or tags");
    C(Math.Abs(rec.Record.WorldX - 10) < 0.001 && Math.Abs(rec.Record.WorldY - 30) < 0.001 && Math.Abs(rec.Record.WorldZ - 20) < 0.001 && !rec.Record.HasFullRotation, "the pivot, to a millimetre, and a yaw alone");
    C(rec.Record.Zone == new ZoneKey(0, 0) && rec.Zone == new ZoneKey(0, 0), "its zone");
    C(!rec.Record.HasSeed && !rec.Record.HasScale, "no seed and no scale");
    C(rec.Values.Values.Length == 2 && rec.Values.Values.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == 4242, "the creator and its index are in the value set");
    C(rec.Record.ValueSet == 0 && pool.Sets.Count == 1, "which is the pool's first");

    var looks = BakeCopyAt(2, 0f, 0f, 0f);
    looks.Values = BakeVals(
      ZdoValue.OfInt(BakeKey("MatVar1"), 2), ZdoValue.OfInt(mat0, 1), ZdoValue.OfInt(seed, 777),
      ZdoValue.OfLong(BakeKey("creator"), 4242), ZdoValue.OfFloat(BakeKey("health"), 150f), ZdoValue.OfFloat(BakeKey("snow"), 0.5f),
      ZdoValue.OfInt(BakeKey("preSnow"), 1), ZdoValue.OfInt(BakeKey("cheated"), 1));
    var look = BakeCapture.MakeRecord(looks, BakeFacts("floor"), BakedRole.Static, 7, 0, pool);
    C(look.Entry.Tags.Length == 2 && look.Entry.Tags[0].Key == "MatVar0" && look.Entry.Tags[0].Number == 1 && look.Entry.Tags[1].Key == "MatVar1" && look.Entry.Tags[1].Number == 2
      && look.Entry.Tags[0].Type == TagType.Int, "MatVar0 and MatVar1 are the palette entry's tags, by slot");
    C(look.Record.HasSeed && look.Record.Seed == 777, "the seed is the record's seed");
    C(look.Values.Values.Length == 3 && look.Values.Values.Find(BakeKey("health"), ZdoValueType.Float) != null && look.Values.Values.Find(BakeKey("cheated"), ZdoValueType.Int) != null,
      "the value set keeps creator, health and cheated, and not MatVar, the seed, snow or preSnow");
    var again = BakeCapture.MakeRecord(looks, BakeFacts("floor"), BakedRole.Static, 7, 0, pool);
    C(again.Entry.Equals(look.Entry) && again.Record.ValueSet == look.Record.ValueSet, "the same piece makes the same entry and the same set");

    var wild = BakeCopyAt(2, 0f, 0f, 0f);
    wild.Values = BakeVals(ZdoValue.OfInt(seed, 20000), ZdoValue.OfInt(BakeKey("HasFields"), 1));
    var wildRec = BakeCapture.MakeRecord(wild, BakeFacts("floor"), BakedRole.Static, 7, 0, pool);
    C(!wildRec.Record.HasSeed && wildRec.Values.Values.Find(seed, ZdoValueType.Int)?.Number == 20000, "a seed outside 0 to 12,344 stays a value, so unbake gives it back exactly");
    foreach (var s in new[] { 0, 12344 })
    {
      var edge = BakeCopyAt(2, 0f, 0f, 0f);
      edge.Values = BakeVals(ZdoValue.OfInt(seed, s));
      var r = BakeCapture.MakeRecord(edge, BakeFacts("floor"), BakedRole.Static, 7, 0, pool);
      C(r.Record.HasSeed && r.Record.Seed == s && r.Values.Values.Length == 0, $"a seed of {s} is a seed");
    }

    // Scale.
    var scaled = BakeCopyAt(3, 0f, 0f, 0f);
    scaled.Values = BakeVals(ZdoValue.OfVec3(BakeKey("scale"), 2f, 3f, 4f));
    var s1 = BakeCapture.MakeRecord(scaled, BakeFacts("big", true), BakedRole.Static, 7, 0, pool);
    C(s1.Record.HasScale && s1.Record.ScaleX == 2000 && s1.Record.ScaleY == 3000 && s1.Record.ScaleZ == 4000 && s1.Values.Values.Length == 0, "a prefab that syncs scale: the Vec3 scale is the record's, not a value");
    var s2 = BakeCapture.MakeRecord(scaled, BakeFacts("big", false), BakedRole.Static, 7, 0, pool);
    C(!s2.Record.HasScale && s2.Values.Values.Length == 0, "a prefab that does not: no scale run, and the key is still not a value");
    var scalar = BakeCopyAt(3, 0f, 0f, 0f);
    scalar.Values = BakeVals(ZdoValue.OfFloat(BakeKey("scaleScalar"), 1.5f));
    var s3 = BakeCapture.MakeRecord(scalar, BakeFacts("big", true), BakedRole.Static, 7, 0, pool);
    C(s3.Record.HasScale && s3.Record.ScaleX == 1500 && s3.Record.ScaleY == 1500 && s3.Record.ScaleZ == 1500, "scaleScalar is the same scale on every axis");
    var both = BakeCopyAt(3, 0f, 0f, 0f);
    both.Values = BakeVals(ZdoValue.OfFloat(BakeKey("scaleScalar"), 1.5f), ZdoValue.OfVec3(BakeKey("scale"), 2f, 2f, 2f));
    C(BakeCapture.MakeRecord(both, BakeFacts("big", true), BakedRole.Static, 7, 0, pool).Record.ScaleX == 2000, "the Vec3 comes first, as ZNetView.Awake reads it");
    var zero = BakeCopyAt(3, 0f, 0f, 0f);
    zero.Values = BakeVals(ZdoValue.OfVec3(BakeKey("scale"), 0f, 0f, 0f), ZdoValue.OfFloat(BakeKey("scaleScalar"), 3f));
    C(BakeCapture.MakeRecord(zero, BakeFacts("big", true), BakedRole.Static, 7, 0, pool).Record.ScaleY == 3000, "a Vec3 of zero is no scale: the scalar counts, as in the game");
    var unit = BakeCopyAt(3, 0f, 0f, 0f);
    unit.Values = BakeVals(ZdoValue.OfVec3(BakeKey("scale"), 1f, 1f, 1f));
    C(!BakeCapture.MakeRecord(unit, BakeFacts("big", true), BakedRole.Static, 7, 0, pool).Record.HasScale, "a scale of 1 needs no run");

    // Town (Live) and Seat records.
    var door = BakeCopyAt(4, 5f, 6f, 7f, 180f);
    door.Values = BakeVals(ZdoValue.OfInt(BakeKey("state"), 1), ZdoValue.OfLong(BakeKey("creator"), 9));
    var live = BakeCapture.MakeRecord(door, BakeFacts("door"), BakedRole.Live, 7, 12, pool);
    C(live.Role == BakedRole.Live && live.Entry.Protected && live.Record.HasId && live.Record.Id == 12 && live.Values.Values.Length == 2, "a town record: Live, protected, with its id, and every value");
    var seat = BakeCapture.MakeRecord(door, BakeFacts("stool"), BakedRole.Seat, 7, 12, pool);
    C(seat.Role == BakedRole.Seat && !seat.Entry.Protected && !seat.Record.HasId, "a Seat has no id and is not protected");
    var compiler = BakeCapture.MakeRecord(door, BakeFacts("x"), BakedRole.Static, 0, 0, pool);
    C(!compiler.Record.HasSource, "source 0 makes a record without source and value set (the compiler's form)");

    // Value sets are shared.
    var a = BakeCopyAt(1, 0f, 0f, 0f);
    var b = BakeCopyAt(1, 50f, 0f, 0f);
    var c = BakeCopyAt(1, 0f, 50f, 0f);
    a.Values = BakeVals(ZdoValue.OfLong(BakeKey("creator"), 1), ZdoValue.OfFloat(BakeKey("health"), 10f));
    b.Values = BakeVals(ZdoValue.OfFloat(BakeKey("health"), 10f), ZdoValue.OfLong(BakeKey("creator"), 1));
    c.Values = BakeVals(ZdoValue.OfLong(BakeKey("creator"), 1), ZdoValue.OfFloat(BakeKey("health"), 11f));
    var shared = new BakeValueSetPool();
    int ia = BakeCapture.MakeRecord(a, BakeFacts("w"), BakedRole.Static, 7, 0, shared).Record.ValueSet;
    int ib = BakeCapture.MakeRecord(b, BakeFacts("w"), BakedRole.Static, 7, 0, shared).Record.ValueSet;
    int ic = BakeCapture.MakeRecord(c, BakeFacts("w"), BakedRole.Static, 7, 0, shared).Record.ValueSet;
    int ie = BakeCapture.MakeRecord(BakeCopyAt(1, 9f, 9f, 9f), BakeFacts("w"), BakedRole.Static, 7, 0, shared).Record.ValueSet;
    int ie2 = BakeCapture.MakeRecord(BakeCopyAt(1, 8f, 9f, 9f), BakeFacts("w"), BakedRole.Static, 7, 0, shared).Record.ValueSet;
    C(ia == ib && ia != ic && shared.Sets.Count == 3, "two pieces with the same values (in any order) share one set; another health is another set");
    C(ie == ie2 && shared.Sets[ie].Values.Length == 0, "pieces with no values at all share the empty set, which has an index like any other");
    C(BakeValues.SetOf(a.Values).Equals(BakeValues.SetOf(b.Values)), "sets are equal whatever the order the values were listed in");

    Section("capture: what a layer cannot hold stays a piece");
    var facts = BakeFacts("w");
    C(BakeCapture.Problem(BakeCopyAt(1, 10f, 0f, 10f), facts) == null, "an ordinary piece has no problem");
    C(BakeCapture.Problem(BakeCopyAt(1, 80000f, 0f, 0f), facts) != null, "beyond zone 1023: refused");
    C(BakeCapture.Problem(BakeCopyAt(1, 65500f, 0f, -65500f), facts) == null, "the last zones are fine");
    C(BakeCapture.Problem(BakeCopyAt(1, 0f, float.NaN, 0f), facts) != null, "a position that is not a number");
    foreach (var bad in new[] { 70f, 0.0005f, -1f })
    {
      var big = BakeCopyAt(1, 0f, 0f, 0f);
      big.Values = BakeVals(ZdoValue.OfVec3(BakeKey("scale"), bad, 1f, 1f));
      C(BakeCapture.Problem(big, BakeFacts("w", true)) != null, $"a scale of {bad} cannot be held");
      C(BakeCapture.Problem(big, BakeFacts("w", false)) == null, $"... unless the prefab does not sync scale, when it is no scale at all");
    }
    var edgeScale = BakeCopyAt(1, 0f, 0f, 0f);
    edgeScale.Values = BakeVals(ZdoValue.OfVec3(BakeKey("scale"), 65.535f, 0.001f, 1f));
    C(BakeCapture.Problem(edgeScale, BakeFacts("w", true)) == null, "and its limits are fine");
    var named = BakeCopyAt(1, 0f, 0f, 0f);
    named.PrefabName = "café_wall";
    C(BakeCapture.Problem(named, facts) != null, "a prefab name outside ASCII cannot be written");
    named.PrefabName = new string('x', 300);
    C(BakeCapture.Problem(named, facts) != null, "nor one over 255 characters");
    var text = BakeCopyAt(1, 0f, 0f, 0f);
    text.Values = BakeVals(ZdoValue.OfString(BakeKey("long"), new string('y', 65536)));
    C(BakeCapture.Problem(text, facts) != null, "a text value over 65,535 bytes cannot go in a registry");
    text.Values = BakeVals(ZdoValue.OfString(BakeKey("long"), new string('y', 65535)));
    C(BakeCapture.Problem(text, facts) == null, "one of 65,535 can");
  }

  // ------------------------------------------------------------------------------------------------ the game's own ZDO

  private static ZDO BakeZdo(string prefab, uint id, float x, float y, float z, float rx = 0f, float ry = 0f, float rz = 0f, long user = 1000L)
  {
    var zdo = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
    zdo.m_uid = new ZDOID(user, id);
    typeof(ZDO).GetField("m_prefab", BakeAny)!.SetValue(zdo, prefab.GetStableHashCode());
    typeof(ZDO).GetField("m_position", BakeAny)!.SetValue(zdo, new Vector3(x, y, z));
    typeof(ZDO).GetField("m_rotation", BakeAny)!.SetValue(zdo, new Vector3(rx, ry, rz));
    return zdo;
  }

  private static void BakeZdoCopyTest()
  {
    Section("capture: a whole ZDO out of the game's own value store");
    ZDOExtraData.Init();
    var zdo = BakeZdo("stone_wall_2x1", 5, 10.5f, 30.25f, -40.125f, 0f, 22.5f, 0f, 77L);
    zdo.Persistent = true;
    zdo.Distant = false;
    zdo.Type = ZDO.ObjectType.Solid;
    var id = zdo.m_uid;
    ZDOExtraData.Set(id, ZDOVars.s_creator, 123456789L);
    ZDOExtraData.Set(id, ZDOVars.s_creatorIndex, 4);
    ZDOExtraData.Set(id, ZDOVars.s_health, 87.5f);
    ZDOExtraData.Set(id, ZDOVars.s_support, 1200f);
    ZDOExtraData.Set(id, "MatVar0".GetStableHashCode(), 2);
    ZDOExtraData.Set(id, "pos".GetStableHashCode(), new Vector3(1f, 2f, 3f));
    ZDOExtraData.Set(id, "rot".GetStableHashCode(), new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
    ZDOExtraData.Set(id, "text".GetStableHashCode(), "hello é世");
    ZDOExtraData.Set(id, "blob".GetStableHashCode(), new byte[] { 9, 8, 7 });
    var copy = BakeCapture.CopyOf(zdo, "stone_wall_2x1");
    C(copy.Prefab == "stone_wall_2x1".GetStableHashCode() && copy.PrefabName == "stone_wall_2x1", "the prefab");
    C(copy.X == 10.5f && copy.Y == 30.25f && copy.Z == -40.125f && copy.RotX == 0f && copy.RotY == 22.5f && copy.RotZ == 0f, "the position and the rotation as stored");
    C(copy.Persistent && !copy.Distant && copy.Type == (byte)ZDO.ObjectType.Solid, "the flags");
    C(copy.UserId == 77L && copy.Id == 5, "the ZDOID it had");
    C(copy.Find(ZDOVars.s_support, ZdoValueType.Float) == null, "the game's session-only 'support' is not copied");
    C(copy.Find(ZDOVars.s_creator, ZdoValueType.Long)?.Number == 123456789L && copy.Find(ZDOVars.s_creatorIndex, ZdoValueType.Int)?.Number == 4 && copy.Find(ZDOVars.s_health, ZdoValueType.Float)?.A == 87.5f,
      "the creator, its index and the health");
    C(copy.Find("MatVar0".GetStableHashCode(), ZdoValueType.Int)?.Number == 2, "the MatVar stays in a copy, which is the whole ZDO");
    var vec = copy.Find("pos".GetStableHashCode(), ZdoValueType.Vec3);
    var quat = copy.Find("rot".GetStableHashCode(), ZdoValueType.Quat);
    C(vec is { A: 1f, B: 2f, C: 3f } && quat is { A: 0.1f, B: 0.2f, C: 0.3f, D: 0.9f }, "vectors and quaternions");
    C(copy.Find("text".GetStableHashCode(), ZdoValueType.String)?.Text == "hello é世" && copy.Find("blob".GetStableHashCode(), ZdoValueType.Bytes)?.Data.SequenceEqual(new byte[] { 9, 8, 7 }) == true, "text and bytes");
    C(copy.Values.Zip(copy.Values.Skip(1)).All(p => BakeValues.Compare(p.First, p.Second) <= 0), "the values are in key order");
    C(copy.Values.Length == 8, $"eight values (got {copy.Values.Length})");

    // Changing the ZDO afterwards touches the copy not at all.
    ZDOExtraData.Set(id, ZDOVars.s_health, 1f);
    C(copy.Find(ZDOVars.s_health, ZdoValueType.Float)?.A == 87.5f, "the copy does not follow the ZDO");

    var empty = BakeZdo("wood_pole", 6, 0f, 0f, 0f);
    C(BakeCapture.CopyOf(empty, "wood_pole").Values.Length == 0, "a ZDO with no values copies to none");

    // The record of that copy.
    var record = BakeCapture.MakeRecord(copy, BakeFacts("stone_wall_2x1"), BakedRole.Static, 3, 0, new BakeValueSetPool());
    C(record.Entry.Tags.Length == 1 && record.Entry.Tags[0].Key == "MatVar0" && record.Entry.Tags[0].Number == 2, "its record: MatVar0 as a tag");
    C(record.Values.Values.Length == 7, $"and the other seven values in the set ({record.Values.Values.Length})");
    C(record.Values.Values.Find(ZDOVars.s_creator, ZdoValueType.Long)?.Number == 123456789L && Math.Abs(BakedFormat.YawDegrees(record.Record.Yaw) - 22.5) < 0.004, "the creator comes back from the record's set, and the yaw is 22.5");
    ZDOExtraData.Reset();
  }
}

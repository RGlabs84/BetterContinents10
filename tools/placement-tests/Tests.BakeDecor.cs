// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4 decor).
//
// Offline checks of what 'town' bakes beyond the town pieces (BakeClassifier.cs, BakeRunner.cs) and of how the drawing turns a part that
// RandomPieceRotation turns (BakedKinds.cs, BakedZoneBuild.cs):
//   - the step each axis takes: deterministic, thread safe, in range, per axis, the steps of the component respected, Euler's order;
//   - a zone's instance matrices for a turned part (and the plain parts beside it);
//   - a whole bake of comfort decor, lights and rotating blocks on the stand-ins of Tests.BakeRunner.cs: the roles, the palette entries, the
//     value sets, what is left in the world, the dry run's lines, and unbake and undo giving the real pieces back;
//   - a Copy record that a bake made reaches the client's zone build as a lit copy, undrawn, with its colliders.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static bool DecorNear(Vector3 a, Vector3 b, float eps = 1e-4f) => (a - b).magnitude < eps;

  private static bool DecorSame(Matrix4x4 a, Matrix4x4 b, float eps = 1e-5f)
  {
    for (int r = 0; r < 4; r++)
      for (int c = 0; c < 4; c++)
        if (Math.Abs(a[r, c] - b[r, c]) > eps)
          return false;
    return true;
  }

  // ------------------------------------------------------------------------------------------------ the step

  private static void BakeDecorStepTest()
  {
    Section("decor: RandomPieceRotation's step comes from the record's place");
    var counts = new int[4];
    int sameAxes = 0, sameSalts = 0;
    var seen2 = new HashSet<int>();
    var seen8 = new HashSet<int>();
    for (int i = 0; i < 4000; i++)
    {
      uint hash = BakedFormat.LookHash(i * 0.37, 10 + (i % 7), i * 0.11);
      int y = BakedLook.RotationStep(hash, 7, 1, 4);
      counts[y]++;
      if (y == BakedLook.RotationStep(hash, 7, 0, 4))
        sameAxes++;
      if (y == BakedLook.RotationStep(hash, 8, 1, 4))
        sameSalts++;
      seen2.Add(BakedLook.RotationStep(hash, 7, 2, 2));
      seen8.Add(BakedLook.RotationStep(hash, 7, 2, 8));
    }
    C(counts.All(n => n > 800 && n < 1200), $"4 steps are about equally likely over 4000 places ({string.Join(", ", counts)})");
    C(seen2.SetEquals([0, 1]) && seen8.SetEquals([0, 1, 2, 3, 4, 5, 6, 7]), "2 steps give 0 and 1, 8 steps give all of 0 to 7");
    C(sameAxes > 600 && sameAxes < 1400, $"each axis has a step of its own: the x and y steps agree {sameAxes} times in 4000, about a quarter");
    C(sameSalts > 600 && sameSalts < 1400, $"two parts of one piece turn alone: salts 7 and 8 agree {sameSalts} times in 4000, about a quarter");
    uint h0 = BakedFormat.LookHash(12.5, 3.25, -40.125);
    C(BakedLook.RotationStep(h0, 5, 2, 4) == BakedLook.RotationStep(h0, 5, 2, 4) && BakedLook.RotationStep(h0, 5, 2, 4) == BakedLook.RotationStep(BakedFormat.LookHash(12.5, 3.25, -40.125), 5, 2, 4),
      "the same place, salt and axis give the same step on every call");
    C(BakedLook.RotationStep(h0, 5, 0, 1) == 0 && BakedLook.RotationStep(h0, 5, 0, 0) == 0 && BakedLook.RotationStep(h0, 5, 0, -3) == 0, "one step, none or a negative number is no turn");
    C(PartRotation.Angle(0, 4) == 0f && PartRotation.Angle(1, 4) == 90f && PartRotation.Angle(3, 4) == 270f && PartRotation.Angle(3, 8) == 135f && PartRotation.Angle(1, 2) == 180f && PartRotation.Angle(1, 1) == 0f,
      "a step is 360 / steps degrees, as the component works it out");

    Section("decor: Unity's Euler order (z, then x, then y)");
    var yaw90 = BakedMath.Euler(0, 90, 0);
    var yaw = BakedMath.FromYaw(90, 1, 1, 1);
    C(Math.Abs(yaw90.M00 - yaw.M00) < 1e-6f && Math.Abs(yaw90.M02 - yaw.M02) < 1e-6f && Math.Abs(yaw90.M20 - yaw.M20) < 1e-6f && Math.Abs(yaw90.M22 - yaw.M22) < 1e-6f && yaw90.M11 == 1f,
      "Euler(0, 90, 0) is the yaw of 90 degrees");
    var m = BakedMath.Frame(BakedMath.Euler(90, 0, 90), Vector3.zero);
    C(DecorNear(BakedMath.Point(m, Vector3.right), new Vector3(0, 0, 1)), "Euler(90, 0, 90) turns +x about z first (to +y), then about x (to +z)");
    C(DecorNear(BakedMath.Point(BakedMath.Frame(BakedMath.Euler(90, 0, 0), Vector3.zero), new Vector3(0, 0, 1)), new Vector3(0, -1, 0)), "Euler(90, 0, 0) turns +z down to -y");
    var scaled = BakedMath.Euler(0, 90, 0, 2f, 3f, 4f);
    C(DecorNear(BakedMath.Point(BakedMath.Frame(scaled, Vector3.zero), new Vector3(0, 0, 1)), new Vector3(4, 0, 0)), "scales go along the object's own axes (z scale 4, then the turn)");
    // x then z: Euler(30, 40, 50) agrees with R = Ry(40) * Rx(30) * Rz(50) on a point
    var composed = BakedMath.Frame(BakedMath.Euler(30, 40, 50), Vector3.zero);
    Vector3 q = new(1, 2, 3);
    Vector3 byHand = q;
    byHand = new Vector3((float)(byHand.x * Math.Cos(50 * Math.PI / 180) - byHand.y * Math.Sin(50 * Math.PI / 180)), (float)(byHand.x * Math.Sin(50 * Math.PI / 180) + byHand.y * Math.Cos(50 * Math.PI / 180)), byHand.z);
    byHand = new Vector3(byHand.x, (float)(byHand.y * Math.Cos(30 * Math.PI / 180) - byHand.z * Math.Sin(30 * Math.PI / 180)), (float)(byHand.y * Math.Sin(30 * Math.PI / 180) + byHand.z * Math.Cos(30 * Math.PI / 180)));
    byHand = new Vector3((float)(byHand.x * Math.Cos(40 * Math.PI / 180) + byHand.z * Math.Sin(40 * Math.PI / 180)), byHand.y, (float)(-byHand.x * Math.Sin(40 * Math.PI / 180) + byHand.z * Math.Cos(40 * Math.PI / 180)));
    C(DecorNear(BakedMath.Point(composed, q), byHand, 1e-3f), $"a general Euler is z, then x, then y ({V(BakedMath.Point(composed, q))} vs {V(byHand)})");

    Section("decor: a turned part's matrix and reach");
    var rot = new PartRotation { RotateY = true, StepsY = 4, Position = new Vector3(0, 1, 0), Salt = 3 };
    C(DecorNear(BakedMath.Point(rot.AtSteps(0, 1, 0), new Vector3(0, 0, 1)), new Vector3(1, 1, 0)), "one step of four about y turns +z to +x about the object's place");
    C(DecorNear(BakedMath.Point(rot.AtSteps(0, 2, 0), new Vector3(0, 0, 1)), new Vector3(0, 1, -1)), "two steps turn it to -z");
    var nested = new PartRotation
    {
      RotateY = true, StepsY = 4, Position = new Vector3(0, 1, 0),
      Parent = BakedMath.Frame(BakedMath.FromYaw(0, 1, 1, 1), new Vector3(5, 0, 0)), Below = BakedMath.Frame(BakedMath.FromYaw(0, 1, 1, 1), new Vector3(0, 0, 2)),
    };
    C(DecorNear(BakedMath.Point(nested.AtSteps(0, 1, 0), Vector3.zero), new Vector3(7, 1, 0)), "the parent's place and the renderer's place below the object bracket the turn");
    C(DecorSame(rot.At(h0), rot.AtSteps(rot.StepsFor(h0).X, rot.StepsFor(h0).Y, rot.StepsFor(h0).Z)), "At(hash) is AtSteps of StepsFor(hash)");
    var (sx, sy, sz) = rot.StepsFor(h0);
    C(sx == 0 && sz == 0 && sy >= 0 && sy < 4, "an axis that does not rotate has no step");
    var all = new PartRotation { RotateX = true, RotateY = true, RotateZ = true, StepsX = 2, StepsY = 8, StepsZ = 4 };
    int xs = 0, ys = 0, zs = 0;
    for (int i = 0; i < 500; i++)
    {
      var (a, b, c) = all.StepsFor(BakedFormat.LookHash(i, 1, i * 3));
      xs = Math.Max(xs, a);
      ys = Math.Max(ys, b);
      zs = Math.Max(zs, c);
    }
    C(xs == 1 && ys == 7 && zs == 3, $"steps are respected on every axis: the most seen are x {xs} of 2, y {ys} of 8, z {zs} of 4");
    var aside = new PartRotation { RotateY = true, StepsY = 4, Position = new Vector3(-1, 0, 0) };
    C(Math.Abs(aside.Reach(new Bounds(new Vector3(1, 0, 0), new Vector3(2, 2, 2))) - (float)Math.Sqrt(11)) < 1e-4f,
      $"a turn about an axis off the root swings a lopsided box out: the reach takes the farthest of the turns ({aside.Reach(new Bounds(new Vector3(1, 0, 0), new Vector3(2, 2, 2)))}, not the step 0's {Math.Sqrt(3):0.###})");
    var bounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));
    C(Math.Abs(rot.Reach(bounds) - (float)Math.Sqrt(6)) < 1e-4f, $"a turn about y keeps a box's reach from the root: {rot.Reach(bounds)}");
  }

  // ------------------------------------------------------------------------------------------------ the zone

  private static void BakeDecorZoneTest()
  {
    Section("decor: a zone draws a turned part with a matrix of its own for every instance");
    var piece = ClientKit.NoLods("blackmarble_1x1", 2.5f);
    var rot = new PartRotation { RotateX = true, RotateY = true, RotateZ = false, StepsX = 2, StepsY = 4, Salt = 11, Position = new Vector3(0, 0.5f, 0) };
    piece.Parts = [piece.Parts[0], ClientKit.Part(true, true)];
    piece.NearParts = [0, 1];
    piece.FarParts = [0, 1];
    piece.Locals = [rot.AtSteps(0, 0, 0), BakedMath.Place(BakedMath.FromYaw(0, 1, 1, 1), 0, 2, 0, Vector3.zero)];
    piece.LocalRotations = [rot];   // shorter than Locals: the second is a plain offset part
    piece.Parts[0].LocalIsIdentity = false;
    piece.Parts[0].LocalIndex = 0;
    piece.Parts[1].LocalIsIdentity = false;
    piece.Parts[1].LocalIndex = 1;
    C(piece.HasRotation, "a piece with a turned part knows it");
    C(!ClientKit.NoLods("plain", 1f).HasRotation, "a plain piece does not");
    var kind = ClientKit.Kind("blackmarble_1x1", piece);

    var records = new List<(int, double, double, double, double, Vector3?)>();
    for (int i = 0; i < 160; i++)
      records.Add((0, 1 + i * 0.375, 2 + i % 5, 5 + i % 7 * 3, i % 4 * 90, null));
    var data = ClientKit.Zone(0, 0, records.ToArray());
    var zone = BakedZoneBuild.Build(data, [kind], 1);
    var ki = zone.Kinds.Single();
    C(ki.Count == 160 && ki.Locals.Length == 2 && ki.Locals[0].Length == 160 && ki.Locals[1].Length == 160, "160 instances, two matrices each");

    double x0 = data.Zone.OriginX, z0 = data.Zone.OriginZ;
    bool allRight = true, plainRight = true;
    var seenX = new HashSet<int>();
    var seenY = new HashSet<int>();
    int ninety = 0;
    for (int k = 0; k < ki.Count; k++)
    {
      // the record this instance is: the one at its place
      int at = -1;
      for (int i = 0; i < data.Count && at < 0; i++)
        if (Math.Abs(x0 + data.X[i] / BakedFormat.UnitsPerMetre - ki.Root[k].m03) < 1e-3 && Math.Abs(data.Y[i] / 1000.0 - ki.Root[k].m13) < 1e-3
            && Math.Abs(z0 + data.Z[i] / BakedFormat.UnitsPerMetre - ki.Root[k].m23) < 1e-3)
          at = i;
      if (at < 0)
      {
        allRight = false;
        continue;
      }
      uint hash = BakedFormat.LookHash(x0 + data.X[at] / BakedFormat.UnitsPerMetre, data.Y[at] / 1000.0, z0 + data.Z[at] / BakedFormat.UnitsPerMetre);
      var (a, b, c) = rot.StepsFor(hash);
      seenX.Add(a);
      seenY.Add(b);
      if (c != 0)
        allRight = false;
      allRight &= DecorSame(ki.Locals[0][k], BakedMath.Mul(ki.Root[k], rot.AtSteps(a, b, 0)), 1e-4f);
      plainRight &= DecorSame(ki.Locals[1][k], BakedMath.Mul(ki.Root[k], piece.Locals[1]), 1e-4f);
      if (b == 1)
        ninety++;
    }
    C(allRight, "every instance's turned matrix is its root times the part turned by the steps of its place");
    C(plainRight, "the plain offset part beside it is its root times its offset, as before");
    C(seenX.SetEquals([0, 1]) && seenY.SetEquals([0, 1, 2, 3]) && ninety > 15, $"across the 160 instances every step is taken (x {string.Join("", seenX.OrderBy(v => v))}, y {string.Join("", seenY.OrderBy(v => v))})");

    var again = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records.ToArray()), [kind], 1).Kinds.Single();
    bool same = true;
    for (int k = 0; k < ki.Count; k++)
      same &= DecorSame(ki.Locals[0][k], again.Locals[0][k], 0f) && DecorSame(ki.Locals[1][k], again.Locals[1][k], 0f);
    C(same, "a second build of the same records gives the same matrices (deterministic)");

    // the same place in another zone block gives the same step: the step comes from the place, not the order or the zone's contents
    var one = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records[37]), [kind], 1).Kinds.Single();
    int whichOfAll = -1;
    for (int k = 0; k < ki.Count && whichOfAll < 0; k++)
      if (DecorNear(new Vector3(ki.Root[k].m03, ki.Root[k].m13, ki.Root[k].m23), new Vector3(one.Root[0].m03, one.Root[0].m13, one.Root[0].m23)))
        whichOfAll = k;
    C(whichOfAll >= 0 && DecorSame(one.Locals[0][0], ki.Locals[0][whichOfAll], 0f), "a record alone in its zone is turned as it is among 159 others");

    Section("decor: a piece with no turned part builds exactly as before");
    var plain = ClientKit.NoLods("post", 1.5f);
    plain.Locals = [BakedMath.Place(BakedMath.FromYaw(0, 1, 1, 1), 0, 2, 0, Vector3.zero)];
    plain.Parts[0].LocalIsIdentity = false;
    plain.Parts[0].LocalIndex = 0;
    var plainZone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, (0, 5, 1, 5, 0, null)), [ClientKit.Kind("post", plain)], 1).Kinds.Single();
    C(Math.Abs(plainZone.Locals[0][0].m13 - 3f) < 1e-4f, "root times local, no hash needed");
  }

  // ------------------------------------------------------------------------------------------------ the bake

  private static readonly string[] DecorFire = ["Fireplace", "EffectArea"];

  private static PrefabFacts[] BakeDecorPrefabs() =>
  [
    new("stone_wall_2x1", BakeSafeParts, height: 3f),
    new("rug_wolf", BakeSafeParts, comfort: 1, height: 1f),
    new("piece_chair02", [.. BakeSafeParts, "Chair"], comfort: 2, height: 1f),
    new("piece_walltorch", [.. BakeSafeParts, .. DecorFire], height: 1f),
    new("piece_groundtorch", [.. BakeSafeParts, .. DecorFire, "LightFlicker", "LightLod", "TimedDestruction", "UnityEngine.AudioSource", "UnityEngine.Light", "UnityEngine.ParticleSystem",
      "UnityEngine.ParticleSystemRenderer", "ZSFX"], height: 2f),
    new("hooded_lantern", [.. BakeSafeParts, "EffectArea", "LightFlicker", "LightLod", "UnityEngine.Light"], height: 1f),
    new("fire_pit", [.. BakeSafeParts, .. DecorFire], height: 1f, burningArea: true),
    new("portal_wood", [.. BakeSafeParts, "TeleportWorld", "EffectArea", "UnityEngine.Light"], height: 4f),
    new("ArmorStand", [.. BakeSafeParts, "ArmorStand"], comfort: 1, height: 2f),
    new("blackmarble_1x1", [.. BakeSafeParts, "RandomPieceRotation"], height: 2f),
    new("Pine_tree", ["ZNetView"]),
  ];

  // Decor: 12 pieces a bake with 'town' takes, 3 it adopts, a tree it does not touch. Returns the 12.
  private static List<PieceCopy> BakeDecorWorld(BakeFakeWorld world)
  {
    foreach (var facts in BakeDecorPrefabs())
      world.Facts[facts.Name.GetStableHashCode()] = facts;
    var fuel = ZdoValue.OfFloat(BakeKey("fuel"), 7.5f);
    var lit = ZdoValue.OfLong(BakeKey("lastTime"), 637000000000000000L);
    var taken = new List<PieceCopy>
    {
      BakeObject("stone_wall_2x1", 5f, 10f, 5f, 0f),
      BakeObject("stone_wall_2x1", 7f, 10f, 5f, 90f),
      BakeObject("rug_wolf", 10f, 10f, 8f, 45f, 1001, 0f, 0f, ZdoValue.OfFloat(BakeKey("health"), 40f)),
      BakeObject("rug_wolf", 12f, 10f, 8f, 135f),
      BakeObject("piece_chair02", 14f, 10f, 8f, 270f),
      BakeObject("piece_walltorch", 20f, 12f, 5f, 0f, 1001, 0f, 0f, fuel, lit),
      BakeObject("piece_walltorch", 22f, 12f, 5f, 90f, 1002, 0f, 0f, ZdoValue.OfFloat(BakeKey("fuel"), 3f)),
      BakeObject("piece_walltorch", 24f, 12f, 5f, 180f),
      BakeObject("piece_groundtorch", 26f, 10f, 9f, 0f),
      BakeObject("hooded_lantern", 28f, 11f, 9f, 30f),
      BakeObject("blackmarble_1x1", 40f, 10f, 5f, 0f),
      BakeObject("blackmarble_1x1", 42f, 10f, 5f, 90f),
    };
    foreach (var piece in taken)
      world.Add(piece);
    // What stays: a fire to cook on, a portal, an armor stand; and a tree.
    world.Add(BakeObject("fire_pit", 30f, 10f, 3f, 0f, 1001, 0f, 0f, ZdoValue.OfFloat(BakeKey("fuel"), 20f)));
    world.Add(BakeObject("portal_wood", 32f, 10f, 3f, 0f, 1001, 0f, 0f, ZdoValue.OfString(BakeKey("tag"), "home")));
    world.Add(BakeObject("ArmorStand", 34f, 10f, 3f, 0f, 1001, 0f, 0f, ZdoValue.OfString(BakeKey("0_item"), "ArmorBronzeChest")));
    world.Add(BakeObject("Pine_tree", 50f, 10f, 20f, 0f, 0));
    return taken;
  }

  private static void BakeDecorRunTest()
  {
    Section("decor: a dry run with 'town' says what becomes decor and what becomes a lit copy");
    BakeRunner.Reset();
    var fx = BakeNewFx("decor", BakeDecorWorld, out var taken);
    int objects0 = fx.World.Objects.Count;
    BakeRun(fx, BakeStandardArea(), "town");
    var dry = string.Join("\n", fx.Said);
    C(fx.Clock.Events.Count == 0 && fx.World.Objects.Count == objects0, "a dry run changes nothing");
    C(dry.Contains("Found 15 pieces. To bake: 12 (piece_walltorch 3, blackmarble_1x1 2, rug_wolf 2 and 4 more kinds)."), "the found line counts the lit copies and the decor as records: " + dry);
    C(dry.Contains("Comfort: 3 become decor (rug_wolf 2, piece_chair02 1). A baked piece gives no comfort."), "the comfort line: " + dry);
    C(dry.Contains("Lights: 5 become lit copies (piece_walltorch 3, hooded_lantern 1, piece_groundtorch 1). They never need fuel and give no warmth; fires you cook on stay real pieces."), "the lights line: " + dry);
    C(dry.Contains("Town pieces: 3 (fires and torches 1, comfort 1, lights 1). They stay real pieces and become protected parts of the layer."), "and the town line holds the fire to cook on, the armor stand and the portal: " + dry);

    Section("decor: without 'town' the same area bakes neither");
    BakeRunner.Reset();
    var plain = BakeNewFx("decor-plain", BakeDecorWorld, out _);
    BakeRun(plain, BakeStandardArea(), "");
    var plainDry = string.Join("\n", plain.Said);
    C(plainDry.Contains("Stay pieces: 11 (") && plainDry.Contains("Add 'town' to make them protected parts of the layer.") && !plainDry.Contains("Comfort: ") && !plainDry.Contains("Lights: "),
      "the dry run without 'town' reports eleven stay pieces and says add 'town': " + plainDry);
    BakeRunner.Reset();
    BakeRun(plain, BakeStandardArea(), "confirm");
    var plainRecords = BakeRecordsOf(plain);
    C(plainRecords.Count == 4 && plainRecords.All(r => r.Prefab is "stone_wall_2x1" or "blackmarble_1x1"), "only the walls and the rotating blocks are baked: " + string.Join(", ", plainRecords.Select(r => r.Prefab)));
    C(plain.World.Objects.Values.Count(o => o.PrefabName is "rug_wolf" or "piece_chair02" or "piece_walltorch" or "piece_groundtorch" or "hooded_lantern") == 8, "the rugs, the chair, the torches and the lantern are still objects");

    Section("decor: a bake with 'town' makes the records");
    BakeRunner.Reset();
    fx.Said.Clear();
    BakeRun(fx, BakeStandardArea(), "town confirm");
    var records = BakeRecordsOf(fx);
    C(records.Count == 15, $"fifteen records: twelve baked and three live (got {records.Count})");
    var roles = records.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.Count());
    C(roles.GetValueOrDefault(BakedRole.Static) == 6 && roles.GetValueOrDefault(BakedRole.Seat) == 1 && roles.GetValueOrDefault(BakedRole.Copy) == 5 && roles.GetValueOrDefault(BakedRole.Live) == 3,
      "6 Static (walls, rugs, blocks), 1 Seat (the chair), 5 Copy (torches, lantern), 3 Live: " + string.Join(", ", roles.Select(p => p.Key + " " + p.Value)));
    C(records.Where(r => r.Role == BakedRole.Copy).Select(r => r.Prefab).OrderBy(n => n).SequenceEqual(["hooded_lantern", "piece_groundtorch", "piece_walltorch", "piece_walltorch", "piece_walltorch"]),
      "the Copy records are the three wall torches, the ground torch and the lantern");
    C(records.Where(r => r.Role == BakedRole.Live).Select(r => r.Prefab).OrderBy(n => n).SequenceEqual(["ArmorStand", "fire_pit", "portal_wood"]), "the Live records are the fire to cook on, the portal and the armor stand");
    var torch = records.First(r => r.Prefab == "piece_walltorch" && r.Record.WorldX > 19.9 && r.Record.WorldX < 20.1);
    C(torch.Entry.Role == BakedRole.Copy && torch.Entry.Collision == BakedCollision.Prefab && torch.Entry.Flags == PaletteFlags.None && torch.Entry.Candidates.Length == 1 && torch.Entry.Candidates[0].Name == "piece_walltorch",
      "a Copy's palette entry: its own prefab, collision 3 (the prefab's colliders), no flags (the Light stays)");
    C(torch.Values.Values.Find(BakeKey("fuel"), ZdoValueType.Float)?.A == 7.5f && torch.Values.Values.Find(BakeKey("lastTime"), ZdoValueType.Long) != null
      && torch.Values.Values.Find(BakeKey("creator"), ZdoValueType.Long)?.Number == 1001, "its value set keeps the fuel and the time it was lit, for an unbake");
    C(torch.Record.HasSource && torch.Record.Source == 1 && !torch.Record.HasId && !torch.Entry.Protected, "an ordinary record of bake 1: no id, not protected");
    var chair = records.First(r => r.Prefab == "piece_chair02");
    C(chair.Role == BakedRole.Seat && chair.Entry.Collision == BakedCollision.Prefab, "the chair with comfort is a Seat");
    var statics = records.Where(r => r.Role == BakedRole.Static).Select(r => r.Prefab).OrderBy(n => n).ToList();
    C(statics.SequenceEqual(["blackmarble_1x1", "blackmarble_1x1", "rug_wolf", "rug_wolf", "stone_wall_2x1", "stone_wall_2x1"]), "Statics: the rugs (decor), the walls and the rotating blocks: " + string.Join(", ", statics));

    Section("decor: what a bake with 'town' leaves in the world");
    foreach (var piece in taken)
      C(!BakeStands(fx, piece), $"the {piece.PrefabName} at {piece.X}, {piece.Z} is gone as an object");
    var fire = fx.World.Objects.Values.First(o => o.PrefabName == "fire_pit");
    var portal = fx.World.Objects.Values.First(o => o.PrefabName == "portal_wood");
    var stand = fx.World.Objects.Values.First(o => o.PrefabName == "ArmorStand");
    C(BakeHasKeys(fire) && BakeHasKeys(portal) && BakeHasKeys(stand), "the fire to cook on, the portal and the armor stand are the same objects, adopted with their keys");
    C(fire.Find(BakeKey("fuel"), ZdoValueType.Float)?.A == 20f && portal.Find(BakeKey("tag"), ZdoValueType.String)?.Text == "home" && stand.Find(BakeKey("0_item"), ZdoValueType.String)?.Text == "ArmorBronzeChest",
      "and keep their fuel, their tag and the armor on the stand");
    C(fx.World.Objects.Count == objects0 - 12, "twelve objects fewer: the tree, the fire, the portal and the stand remain");
    var text = string.Join("\n", fx.Said);
    C(text.Contains("12 pieces baked, 3 left as pieces (town pieces, protected parts of the layer)"), "the end says so: " + text);
    var data = new BakeJournal(fx.Folder).Read(1);
    C(data.Statics.Count == 12 && data.Records.Count == 15, "the undo file holds every one of the twelve, whole");
    var snapshot = fx.World.Objects.Values.Select(o => (o.PrefabName, o.X, o.Z)).ToList();

    Section("decor: unbake gives the real pieces back, a torch with the fuel it had");
    BakeRunner.Reset();
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "confirm", "unbake world");
    foreach (var piece in taken)
      C(BakeFindAgain(fx, piece) != null, $"the {piece.PrefabName} at {piece.X}, {piece.Z} is an object again with every value ({BakeDescribeValues(piece)})");
    C(fx.World.Objects.Values.Count(o => o.PrefabName == "piece_walltorch") == 3 && fx.World.Objects.Values.All(o => !BakeHasAnyKey(o)), "three wall torches stand again, and nothing is keyed");
    C(fx.Layer.Placements == 0, "the layer holds no record");

    Section("decor: undo of the unbake puts the records back, undo of the bake gives every piece back");
    BakeRunner.Reset();
    BakeUndoRun(fx, null);
    C(fx.Layer.Placements == 15 && taken.All(p => !BakeStands(fx, p)), "the records are back and the pieces made go again");
    BakeRunner.Reset();
    BakeUndoRun(fx, 1);
    foreach (var piece in taken)
      C(BakeFindAgain(fx, piece) != null, $"after undoing the bake the {piece.PrefabName} at {piece.X}, {piece.Z} is back with every value");
    C(fx.Layer.Placements == 0 && fx.World.Objects.Count == objects0 && fx.World.Objects.Values.All(o => !BakeHasAnyKey(o)), "the world is the one the test began with, nothing keyed");
    C(snapshot.Count == 4, "(the four that stayed through the bake)");
  }

  // ------------------------------------------------------------------------------------------------ the client

  private static void BakeDecorClientTest()
  {
    Section("decor: a Copy record that a bake made reaches the client as a lit copy, and is drawn unlit until one stands");
    BakeRunner.Reset();
    BakedKinds.ResetAll();
    var fx = BakeNewFx("decor-client", BakeDecorWorld, out _);
    BakeRun(fx, BakeStandardArea(), "town confirm");
    var layer = fx.Layer;
    typeof(BakedKinds).GetField("pieceLayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, 10);
    var zones = layer.AllRecords().Select(r => ZoneKey.OfPoint((float)r.WorldX, (float)r.WorldZ)).Distinct().ToList();
    C(zones.Count == 2, "the records are in two zones (the blocks and the portal are past x = 32)");
    var kinds = new BakedKind[layer.Palette.Count];
    for (int e = 0; e < kinds.Length; e++)
    {
      var entry = layer.Palette[e];
      var kind = BakedKinds.Resolve(EntryDef.From(entry));
      C(kind.Role == entry.Role && kind.Collision == entry.Collision, $"entry {entry.Candidates[0].Name}: the client reads the role {entry.Role} and the collision {entry.Collision} from the layer");
      if (entry.Role is BakedRole.Copy or BakedRole.Seat or BakedRole.Static)
      {
        // what the game would have given the kind: the prefab, and the piece harvested from it (boxes for the colliders)
        var piece = ClientKit.NoLods(entry.Candidates[0].Name, 1.5f);
        piece.Boxes = [BakedMath.BoxMatrix(new Vector3(0, 0.5f, 0), new Vector3(1, 1, 1))];
        piece.HasChair = entry.Role == BakedRole.Seat;
        if (entry.Candidates[0].Name == "blackmarble_1x1")
        {
          var rot = new PartRotation { RotateY = true, StepsY = 4, Salt = 5 };
          piece.Locals = [rot.AtSteps(0, 0, 0)];
          piece.LocalRotations = [rot];
          piece.Parts[0].LocalIsIdentity = false;
          piece.Parts[0].LocalIndex = 0;
        }
        kind.Piece = piece;
        kind.Prefab = ApiAlive<GameObject>();
        kind.UnityLayer = 10;
      }
      kinds[e] = kind;
    }
    var builds = zones.Select(z =>
    {
      layer.TryGetZoneRow(z, out var row);
      return BakedZoneBuild.Build(layer.Decode(row), kinds, 1);
    }).ToList();
    C(builds.Sum(b => b.Records) == 15, "all fifteen records are read");
    var copies = builds.SelectMany(b => b.Copies).ToList();
    var seats = builds.SelectMany(b => b.Seats).ToList();
    C(copies.Count == 5 && copies.All(c => c.Kind.Role == BakedRole.Copy), "the five Copy records are listed as lit copies to make near the camera");
    C(seats.Count == 1 && seats[0].Kind.Role == BakedRole.Seat, "and the one Seat as a seat");
    C(builds.Any(b => b.HasProps), "a zone has props");
    int drawn = builds.Sum(b => b.Kinds.Sum(k => k.Count));
    C(builds.Sum(b => b.Drawn) == 12 && drawn == 12, $"the Statics, the Seat and the Copy records (drawn unlit until a lit copy stands) are drawn (12 of 15), no Live (drawn {builds.Sum(b => b.Drawn)})");
    C(builds.All(b => b.Kinds.All(k => k.Kind.Role != BakedRole.Live)) && builds.SelectMany(b => b.Kinds).Where(k => k.Kind.Role == BakedRole.Copy).Sum(k => k.Count) == 5
      && builds.SelectMany(b => b.Kinds).Where(k => k.Kind.Role == BakedRole.Copy).All(k => k.CopyIndex != null && k.CopyIndex.All(i => i >= 0)), "the five Copy kinds' instances each know their lit copy's place");
    C(builds.All(b => b.HasColliders) && builds.Sum(b => b.ColliderTriangles) == 12 * 12, $"the colliders of the twelve baked pieces are welded, a Copy's included (one box each: {builds.Sum(b => b.ColliderTriangles)} triangles)");
    var rotated = builds.SelectMany(b => b.Kinds).Single(k => k.Kind.Name == "blackmarble_1x1");
    C(rotated.Count == 2 && rotated.Locals.Length == 1, "the rotating blocks are drawn, each with a matrix of its own for the turned part");
    BakedKinds.ResetAll();
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BakedServer's arithmetic and what it writes (spec 7.1): where a record's piece stands against VALtimaOnline's TownBuild.Place, the keys of
// a seeded ZDO with the game's own setters, the variant a record derives (spec 2.5). The pieces themselves are made by the game's Instantiate,
// which only the autotest runs; zone-tests drives the zone's loop with a stand-in for it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  // VALtimaOnline's TownBuild.Place (Towns/TownBuild.cs:90-100): the matrix that turns the piece by yaw about y, scales it along its own
  // axes and puts its anchor on the record's point; its translation is the pivot.
  private static Vector3 TownBuildPivot(Vector3 anchor, float x, float y, float z, float yawDeg, float sx, float sy, float sz)
  {
    float r = yawDeg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
    float ax = anchor.x * sx, ay = anchor.y * sy, az = anchor.z * sz;
    return new Vector3(x - (c * ax + s * az), y - ay, z - (-s * ax + c * az));
  }

  public static void ServerSeedPlacementTest()
  {
    Section("seeding: where a record's piece stands, against VALtimaOnline's TownBuild.Place");
    var layer = ServerValtima(nameof(ServerSeedPlacementTest));
    if (layer == null)
      return;
    var live = Enumerable.Range(0, layer.Palette.Count).Where(i => layer.Palette[i].Role == BakedRole.Live).ToList();
    C(live.Count == 19 && live.All(i => layer.Palette[i].Protected && layer.Palette[i].TryGetTag("VALtima_TownPiece", out _) && layer.Palette[i].TryGetTag("VALtima_Kind", out _)),
      "VALtima's file has 19 Live entries, each Protect and tagged VALtima_TownPiece and VALtima_Kind");
    int records = 0, wrong = 0;
    double worst = 0;
    foreach (var row in layer.Zones.Where(r => r.Live > 0))
    {
      var data = layer.Decode(row);
      for (int k = 0; k < data.Count; k++)
      {
        var palette = layer.Palette[data.Palette[k]];
        if (palette.Role != BakedRole.Live)
          continue;
        var record = data.Record(k);
        var candidate = palette.Candidates[0];
        var resolved = new BakedServer.Resolved(null, candidate, syncsScale: false);
        var place = BakedServer.PlacementOf(resolved, palette, record);
        float scaleX = record.HasScale ? record.Scale.x : 1f, scaleY = record.HasScale ? record.Scale.y : 1f;
        // A prefab that does not take a scale is placed at scale 1 whatever the record says (spec 7.1).
        var expected = TownBuildPivot(candidate.Anchor, (float)record.WorldX, (float)record.WorldY, (float)record.WorldZ, (float)BakedFormat.YawDegrees(record.Yaw), 1f, 1f, 1f);
        double off = (place.Pivot - expected).magnitude;
        worst = Math.Max(worst, off);
        if (off > 1e-3 || place.Scaled || place.Scale != Vector3.one)
          wrong++;
        records++;
      }
    }
    C(records == 608, $"608 Live records ({records})");
    C(wrong == 0, $"each pivot is the record's point less its anchor turned by its yaw, within 1 mm of TownBuild.Place's (worst {worst * 1000:F3} mm; {wrong} wrong)");

    // Scale and full rotation, on a record made here: the prefab that syncs a scale takes it; one that does not is placed at 1.
    var entry = layer.Palette[live[0]];
    var anchor = entry.Candidates[0].Anchor;
    var rotation = new Quaternion(0.1f, 0.6f, -0.2f, 0.7f);
    float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
    rotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
    var made = ZoneRecord.Create(live[0], 100.25, 33.5, -200.75, rotation, new Vector3(2f, 3f, 2f), id: 41u);
    var takes = BakedServer.PlacementOf(new BakedServer.Resolved(null, entry.Candidates[0], syncsScale: true), entry, made);
    var ignores = BakedServer.PlacementOf(new BakedServer.Resolved(null, entry.Candidates[0], syncsScale: false), entry, made);
    var point = made.Position;
    C(takes.Scaled && takes.Scale == new Vector3(2f, 3f, 2f) && (takes.Pivot - (point - takes.Rotation * Vector3.Scale(new Vector3(2f, 3f, 2f), anchor))).magnitude < 1e-4f,
      "a prefab that syncs a scale is placed at the record's scale, turned by the record's full rotation");
    C(!ignores.Scaled && ignores.Scale == Vector3.one && (ignores.Pivot - (point - ignores.Rotation * anchor)).magnitude < 1e-4f,
      "one that does not is placed at scale 1: the anchor's offset is not scaled either");
    C(Quaternion.Angle(takes.Rotation, rotation) < 0.01f, "the full rotation comes back within 0.01 degrees");
  }

  public static void ServerSeedVariantTest()
  {
    Section("seeding: the variant a record derives for a MaterialVariation slot (spec 2.5)");
    C(BakedServer.DerivedVariant(0.0, [1f, 1f]) == 0 && BakedServer.DerivedVariant(0.49, [1f, 1f]) == 0, "two equal variants: u under a half is the first");
    C(BakedServer.DerivedVariant(0.5, [1f, 1f]) == 1 && BakedServer.DerivedVariant(0.999, [1f, 1f]) == 1, "u of a half and over is the second (the running weight must pass u x the total)");
    C(BakedServer.DerivedVariant(0.74, [3f, 1f]) == 0 && BakedServer.DerivedVariant(0.75, [3f, 1f]) == 1, "weights 3 and 1: the first up to u = 0.75");
    C(BakedServer.DerivedVariant(0.0, [0f, 5f]) == 1 && BakedServer.DerivedVariant(0.9, [0f, 5f]) == 1, "a variant of weight 0 is never chosen while another has weight");
    C(BakedServer.DerivedVariant(0.3, [0f, 0f]) == 0 && BakedServer.DerivedVariant(0.3, []) == 0, "no weight at all: the first");
    // The same record gives the same variant every time, and the variants spread by their weights.
    var counts = new int[3];
    var weights = new[] { 2f, 1f, 1f };
    for (int n = 0; n < 40000; n++)
    {
      var record = ZoneRecord.Create(0, 100.0 + n * 0.37, 30.0, -50.0 + (n % 97) * 1.13);
      int a = BakedServer.DerivedVariant(BakedFormat.VariantUnit(record.LookHash, 0), weights);
      int b = BakedServer.DerivedVariant(BakedFormat.VariantUnit(record.LookHash, 0), weights);
      if (a != b)
        throw new InvalidOperationException("a record's variant changed between two asks");
      counts[a]++;
    }
    C(Math.Abs(counts[0] / 40000.0 - 0.5) < 0.02 && Math.Abs(counts[1] / 40000.0 - 0.25) < 0.02 && Math.Abs(counts[2] / 40000.0 - 0.25) < 0.02,
      $"over 40,000 records the variants follow the weights 2:1:1 ({counts[0]}, {counts[1]}, {counts[2]})");
    // The seed: the stored one, else the hash mod 12,345.
    var plain = ZoneRecord.Create(0, 12.5, 30.0, 7.25);
    var stored = ZoneRecord.Create(0, 12.5, 30.0, 7.25, seed: 777);
    C(plain.EffectiveSeed == (int)(plain.LookHash % 12345u) && plain.EffectiveSeed is >= 0 and <= 12344 && stored.EffectiveSeed == 777, "RandMatSeed: the record's own, else the place's hash mod 12,345");
  }

  // ---- what a ZDO gets -----------------------------------------------------------------------------------------------------------------

  // ZDO.Set raises the data revision, which asks the game's singletons whether this is the server and which sector is dirty: stand-ins,
  // as tools/zone-tests makes them, and a ZDO made without Unity.
  private sealed class FakeZdos : IDisposable
  {
    private readonly ZNet net = (ZNet)RuntimeHelpers.GetUninitializedObject(typeof(ZNet));
    private readonly ZDOMan man = (ZDOMan)RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan));
    private readonly object netBefore, manBefore;
    private uint next = 1;

    public FakeZdos()
    {
      ServerPrivate(typeof(ZNet), "m_isServer").SetValue(net, true);
      var netField = ServerPrivate(typeof(ZNet), "m_instance");
      var manField = ServerPrivate(typeof(ZDOMan), "s_instance");
      netBefore = netField.GetValue(null);
      manBefore = manField.GetValue(null);
      netField.SetValue(null, net);
      manField.SetValue(null, man);
      ZDOExtraData.Reset();
    }

    public ZDO New(string prefab)
    {
      var zdo = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
      zdo.m_uid = new ZDOID(1000L, next++);
      ServerPrivate(typeof(ZDO), "m_prefab").SetValue(zdo, prefab.GetStableHashCode());
      return zdo;
    }

    public void Dispose()
    {
      ServerPrivate(typeof(ZNet), "m_instance").SetValue(null, netBefore);
      ServerPrivate(typeof(ZDOMan), "s_instance").SetValue(null, manBefore);
      ZDOExtraData.Reset();
    }
  }

  public static void ServerSeedKeysTest()
  {
    Section("seeding: the keys of a seeded piece, with the game's own setters");
    var layer = ServerValtima(nameof(ServerSeedKeysTest));
    if (layer == null)
      return;
    using var zdos = new FakeZdos();
    var row = layer.Zones.First(r => r.Live > 0);
    var data = layer.Decode(row);
    int k = Enumerable.Range(0, data.Count).First(i => layer.Palette[data.Palette[i]].Role == BakedRole.Live);
    var palette = layer.Palette[data.Palette[k]];
    var record = data.Record(k);
    var zdo = zdos.New(palette.Name);
    BakedServer.WriteKeys(layer, palette, record, zdo);
    C(zdo.GetBool("VALtima_TownPiece") && zdo.GetInt("VALtima_TownPiece") == 1, "a bool tag is written as a bool (an int 1), which VALtimaOnline's IsTownPiece reads");
    C(palette.TryGetTag("VALtima_Kind", out var kind) && zdo.GetString("VALtima_Kind") == kind.Text && kind.Text.Length > 0, $"a string tag as a string ({kind.Text})");
    C(zdo.GetInt(BakedKeys.Src, -1) == 0 && zdo.GetInt(BakedKeys.Id, 0) == unchecked((int)record.Id) && zdo.GetInt(BakedKeys.Rev, 0) == (int)layer.Revision,
      "bc_bake_src is 0 (the compiler's), bc_bake_id the id cast unchecked, bc_bake_rev the layer's revision");
    C(zdo.GetBool(BakedKeys.Protect) && zdo.GetInt(BakedKeys.Protect) == 1, "bc_protect is a bool for an entry with the Protect flag");
    // Every tag the entry has is on it, whatever its type.
    var typed = new PaletteEntry([new Candidate("forge", 0, 0, 0)], BakedRole.Live, BakedCollision.Prefab, 0, PaletteFlags.None, null, "", 1000, 1000, 1000, "",
      [Tag.OfBool("flag", false), Tag.OfInt("number", -7), Tag.OfFloat("amount", 2.5f), Tag.OfString("text", "hello"), Tag.OfBool("MatVar0", true)]);
    var second = zdos.New("forge");
    BakedServer.WriteKeys(layer, typed, ZoneRecord.Create(0, 1, 2, 3, id: uint.MaxValue), second);
    C(second.GetInt("flag", 9) == 0 && second.GetInt("number") == -7 && second.GetFloat("amount") == 2.5f && second.GetString("text") == "hello",
      "bool false, int, float and string tags, each with its own setter");
    C(second.GetInt(BakedKeys.Id) == -1 && !second.GetBool(BakedKeys.Protect) && !second.GetInt(BakedKeys.Protect, out _), "an id of 4,294,967,295 is -1, and an entry without Protect writes no bc_protect");

    // Values of a value set, each of its seven types.
    var values = zdos.New("piece_chest");
    BakedServer.WriteValue(values, ZdoValue.OfFloat("health".GetStableHashCode(), 37.5f));
    BakedServer.WriteValue(values, ZdoValue.OfVec3("where".GetStableHashCode(), 1f, 2f, 3f));
    BakedServer.WriteValue(values, ZdoValue.OfQuat("turn".GetStableHashCode(), 0f, 1f, 0f, 0f));
    BakedServer.WriteValue(values, ZdoValue.OfInt("number".GetStableHashCode(), -42));
    BakedServer.WriteValue(values, ZdoValue.OfLong("creator".GetStableHashCode(), 7_000_000_000_001L));
    BakedServer.WriteValue(values, ZdoValue.OfString("creatorName".GetStableHashCode(), "Wubarrk"));
    BakedServer.WriteValue(values, ZdoValue.OfBytes("items".GetStableHashCode(), [1, 2, 3, 4]));
    C(values.GetFloat("health") == 37.5f && values.GetVec3("where", Vector3.zero) == new Vector3(1f, 2f, 3f) && values.GetQuaternion("turn", Quaternion.identity) == new Quaternion(0f, 1f, 0f, 0f)
      && values.GetInt("number") == -42 && values.GetLong("creator") == 7_000_000_000_001L && values.GetString("creatorName") == "Wubarrk" && values.GetByteArray("items")!.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
      "a value set's float, vec3, quat, int, long, string and bytes come back as they were");
  }
}

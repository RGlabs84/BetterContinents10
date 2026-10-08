// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Seeding and reconciliation (BakedServer, BakedReconcile) on the game's own ZDO, ZDOMan, ZNet, ZoneSystem and ZNetScene, made without their Unity
// parts as Program.World.cs does it: what stands in a zone (StandingKeys), the zone's Live records made with the piece-making seam stood in for
// (the ghost initialisation that always ends, a piece that fails, one that throws, the pieces that stand skipped), what the world gives the planner
// (Gather), and the executor on real ZDOs: Seed, Keep, Replace, Remove and Orphan, a thousand changes a frame, a world that closes, a seeder that
// throws, the vegetation of the cells that grew, the orphans listed and destroyed, and that an orphan is baked for a zone reset.
// VALtima's real file is the layer where one is needed (it skips, as the placement-tests do, when it is not on this machine).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Kind = BetterContinents.ZoneReset.Kind;

internal static partial class Program
{
  private static readonly int BakeSrcHash = "bc_bake_src".GetStableHashCode(), BakeRevHash = "bc_bake_rev".GetStableHashCode(), OrphanHash = "bc_bake_orphan".GetStableHashCode();

  private static BakedLayer? valtimaSeedLayer;

  // VALtima's real file as a layer (spec 2.7), read once; null when it is not here (the test says so, and fails if BC_REQUIRE_VALTIMA is 1).
  private static BakedLayer? ValtimaForSeeding(string test)
  {
    if (valtimaSeedLayer != null)
      return valtimaSeedLayer;
    var path = Environment.GetEnvironmentVariable("BC_VALTIMA_BCP")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "valheim-testbed", "valtima-work", "bcp", "bake", "placements.bcp");
    if (!File.Exists(path))
    {
      if (Environment.GetEnvironmentVariable("BC_REQUIRE_VALTIMA") == "1")
        C(false, test + ": VALtima's placements.bcp is not at " + path);
      else
        System.Console.WriteLine($"SKIPPED {test}: VALtima's placements.bcp is not at {path}");
      return null;
    }
    var bytes = File.ReadAllBytes(path);
    return valtimaSeedLayer = BakedLayer.Read(bytes, bytes.Length);
  }

  // A piece of a baked layer in the made-up world: the bake keys BakedServer writes, and the revision of the layer that made it.
  private static ZDO BakedPieceAt(World w, Vector2s zone, string prefab, int source, uint id, float dx = 0f, float dz = 0f, int revision = 1)
  {
    var zdo = w.Add(zone, prefab, dx, dz);
    ZDOExtraData.Set(zdo.m_uid, BakeSrcHash, source);
    ZDOExtraData.Set(zdo.m_uid, BakeIdHash, unchecked((int)id));
    ZDOExtraData.Set(zdo.m_uid, BakeRevHash, revision);
    return zdo;
  }

  // What a container with items has saved (Inventory.Save: the version, the number of items as a ushort, then the items).
  private static byte[] ChestWithItems(int count)
  {
    var pkg = new ZPackage();
    pkg.Write(109);
    pkg.Write((ushort)count);
    for (int i = 0; i < count; i++)
      pkg.Write("Stone");
    return pkg.GetArray();
  }

  // The seams of seeding and reconciliation, put back as they were found.
  private sealed class SeedSeams : IDisposable
  {
    private readonly Func<string, GameObject?> findPrefab = BakedServer.FindPrefab;
    private readonly Func<ZoneKey, IEnumerable<ZDO>> zdosIn = BakedServer.ZdosIn;
    private readonly Func<BakedServer.Context, int, PaletteEntry, ZoneRecord, bool, List<GameObject>?, bool> makePiece = BakedServer.MakePiece;
    private readonly Func<BakedServer.Context?, LiveRecord, bool> seeder = BakedReconcile.Seeder;
    private readonly Func<ZDO, Quaternion> rotationOf = BakedReconcile.RotationOf;
    private readonly Func<HashSet<int>> vegetation = BakedReconcile.VegetationPrefabs;
    private readonly Func<ZDOMan?> objects = BakedReconcile.Objects;
    private readonly Func<ZoneKey, bool> isGenerated = BakedReconcile.IsGenerated;
    private readonly Action sendQueue = ZoneRegen.SendDestroyQueue;
    private readonly BC.BetterContinentsSettings settings = BC.Settings;
    private readonly bool server = GetStatic<bool>(typeof(ZNet), "m_isServer");

    public SeedSeams()
    {
      // zdo.Set and zdo.RemoveInt ask the game whether this is the server.
      SetStatic(typeof(ZNet), "m_isServer", true);
    }

    public void Dispose()
    {
      BakedServer.FindPrefab = findPrefab;
      BakedServer.ZdosIn = zdosIn;
      BakedServer.MakePiece = makePiece;
      BakedReconcile.Seeder = seeder;
      BakedReconcile.RotationOf = rotationOf;
      BakedReconcile.VegetationPrefabs = vegetation;
      BakedReconcile.Objects = objects;
      BakedReconcile.IsGenerated = isGenerated;
      ZoneRegen.SendDestroyQueue = sendQueue;
      BC.Settings = settings;
      SetStatic(typeof(ZNet), "m_isServer", server);
      World.Close();
    }
  }

  private static void BakedSeedTests()
  {
    BakedStandingTests();
    BakedSeedZoneTests();
    BakedGatherTests();
    BakedExecuteTests();
    BakedExecuteLimitsTests();
    BakedExecuteRecheckTests();
    BakedVegetationTests();
    BakedOrphanTests();
  }

  // ------------------------------------------------------------------------------------------------ what stands

  private static void BakedStandingTests()
  {
    Section("baked seeding: the (source, id) of what stands in a zone, found the way a reset finds a zone's objects");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(2, -3);
    BakedPieceAt(w, zone, "wood_door", 0, 7);
    BakedPieceAt(w, zone, "forge", 3, 0, dx: 4f);
    BakedPieceAt(w, zone, "piece_chest", 65535, uint.MaxValue, dz: -4f);
    w.Add(zone, "Pine", dx: -3f);
    var noId = w.Add(zone, "wood_door", dx: 1f);
    ZDOExtraData.Set(noId.m_uid, BakeSrcHash, 4);
    BakedPieceAt(w, Z(3, -3), "wood_door", 0, 8);
    var portal = w.AddPortal(zone, 42L);
    ZDOExtraData.Set(portal.m_uid, BakeIdHash, 77);
    var keys = BakedServer.StandingKeys(ZoneKey.Of(zone));
    var wanted = new[] { BakedFormat.LiveKey(0, 7), BakedFormat.LiveKey(3, 0), BakedFormat.LiveKey(65535, uint.MaxValue), BakedFormat.LiveKey(0, 77) };
    C(keys.SetEquals(wanted), $"the keys of the zone's pieces: (0, 7), (3, 0), (65535, 4294967295) and a portal's (0, 77); not a tree, a piece with a source and no id, or the zone beside's ({keys.Count} found)");
    C(BakedServer.StandingKeys(new ZoneKey(9, 9)).Count == 0, "a zone with nothing in it has none");
    // Past the 256 zones the game's sector grid reaches every object shares one sector list: each is found by its own position.
    var far = Z(312, 0);
    var other = Z(-400, 5);
    BakedPieceAt(w, far, "forge", 1, 9);
    BakedPieceAt(w, other, "forge", 1, 10, dx: 3f);
    C(BakedServer.StandingKeys(ZoneKey.Of(far)).SetEquals(new[] { BakedFormat.LiveKey(1, 9) }) && BakedServer.StandingKeys(ZoneKey.Of(other)).SetEquals(new[] { BakedFormat.LiveKey(1, 10) }),
      "far out, where zones share a sector list, each zone has its own pieces' keys only");
    // A source and an id make the key: the same id from another bake is another piece.
    C(BakedFormat.LiveKey(0, 7) != BakedFormat.LiveKey(1, 7) && BakedFormat.LiveKey(2, uint.MaxValue) == (((ulong)2 << 32) | uint.MaxValue), "(source, id) is the key");
  }

  // ------------------------------------------------------------------------------------------------ a zone's seeding

  private static void BakedSeedZoneTests()
  {
    var layer = ValtimaForSeeding(nameof(BakedSeedZoneTests));
    if (layer == null)
      return;
    Section("baked seeding: a zone's Live records are made, unless they stand, and the ghost initialisation always ends");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
    var row = layer.Zones.Where(r => r.Live >= 5).OrderBy(r => r.Key.Z).ThenBy(r => r.Key.X).First();
    var zone = row.Key;
    var live = layer.Decode(row).Records().Where(r => layer.Palette[r.Palette].Role == BakedRole.Live).ToList();
    var calls = new List<(uint Id, bool Ghost, bool GhostInit)>();
    Func<ZoneRecord, bool?> verdict = _ => true;
    bool GhostInit() => GetStatic<bool>(typeof(ZNetView), "m_ghostInit");
    BakedServer.MakePiece = (context, index, palette, record, ghost, spawned) =>
    {
      calls.Add((record.Id, ghost, GhostInit()));
      return verdict(record) ?? throw new InvalidOperationException("the prefab went wrong");
    };
    var ids = live.Select(r => r.Id).OrderBy(x => x).ToList();
    C(live.Count == row.Live && live.Count >= 5 && ids.Distinct().Count() == ids.Count, $"(zone {zone} has {live.Count} Live records with their own ids)");

    // Everything is made.
    int made = BakedServer.SeedZone(zone, ghost: false, spawned: null);
    C(made == live.Count && calls.Count == live.Count && calls.Select(c => c.Id).OrderBy(x => x).SequenceEqual(ids), "a zone's Live records are all made, once each");
    C(calls.All(c => !c.Ghost && !c.GhostInit) && !GhostInit(), "in Full mode, with no ghost initialisation");

    // Ghost mode (a dedicated server): the ghost initialisation is on while a piece is made, and off afterwards.
    calls.Clear();
    made = BakedServer.SeedZone(zone, ghost: true, spawned: new List<GameObject>());
    C(made == live.Count && calls.All(c => c.Ghost && c.GhostInit) && !GhostInit(), "in Ghost mode it is on while every piece is made, and off when the zone is done");

    // Pieces that stand are not made again; the key is (source, id).
    calls.Clear();
    BakedPieceAt(w, Z(zone.X, zone.Z), "wood_door", 0, live[0].Id);
    BakedPieceAt(w, Z(zone.X, zone.Z), "forge", 0, live[2].Id, dx: 5f);
    BakedPieceAt(w, Z(zone.X, zone.Z), "forge", 1, live[1].Id, dx: -5f);
    made = BakedServer.SeedZone(zone, ghost: false, spawned: null);
    C(made == live.Count - 2 && calls.All(c => c.Id != live[0].Id && c.Id != live[2].Id) && calls.Any(c => c.Id == live[1].Id), "two that stand are skipped; the same id of another bake (source 1) is another piece, and is made");

    // A piece that cannot be made costs that piece.
    ZDOExtraData.Reset();
    _ = new World();
    calls.Clear();
    verdict = r => r.Id == live[1].Id ? false : true;
    var lines = LogHandler.During(() => made = BakedServer.SeedZone(zone, ghost: false, spawned: null));
    C(made == live.Count - 1 && calls.Count == live.Count, "a piece that fails is not counted, and the others are made all the same");
    C(lines.Any(l => l.Contains($"{live.Count - 1} seeded, 1 could not be")), "and the zone's line says how many could not be: " + string.Join(" | ", lines.Where(l => l.Contains("seeded"))));

    // Seeding that throws: the exception stays inside (it would make the zone generate again), and the ghost initialisation ends.
    calls.Clear();
    verdict = r => r.Id == live[3].Id ? null : true;
    lines = LogHandler.During(() => made = BakedServer.SeedZone(zone, ghost: true, spawned: new List<GameObject>()));
    C(!GhostInit(), "when a piece throws in Ghost mode, the ghost initialisation is off afterwards all the same");
    C(lines.Any(l => l.Contains("could not be seeded") && l.Contains("the prefab went wrong")) && made == calls.Count - 1 && made < live.Count, "the zone's error is logged with the reason, nothing escapes, and the pieces made before it count: " + made);

    // Nothing to do.
    verdict = _ => true;
    calls.Clear();
    var empty = layer.Zones.First(r => r.Live == 0 && r.Placements > 0).Key;
    C(BakedServer.SeedZone(empty, false, null) == 0 && BakedServer.SeedZone(new ZoneKey(999, 999), false, null) == 0 && calls.Count == 0, "a zone with no Live records, and one the layer does not list, make nothing");
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
    C(BakedServer.SeedZone(zone, false, null) == 0 && calls.Count == 0, "with no layer, nothing");
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };

    // The patch on PlaceZoneCtrl: Full and Ghost seed, a client's mode does not.
    var postfix = typeof(BakedServer).GetNestedType("SeedPatch", Any)!.GetMethod("Postfix", Any)!;
    var spawn = new List<GameObject>();
    calls.Clear();
    postfix.Invoke(null, [zone.ToVector2s(), ZoneSystem.SpawnMode.Client, spawn]);
    C(calls.Count == 0, "the client's spawn mode seeds nothing");
    postfix.Invoke(null, [zone.ToVector2s(), ZoneSystem.SpawnMode.Full, spawn]);
    C(calls.Count == live.Count && calls.All(c => !c.Ghost), "Full mode seeds the zone");
    calls.Clear();
    postfix.Invoke(null, [zone.ToVector2s(), ZoneSystem.SpawnMode.Ghost, spawn]);
    C(calls.Count == live.Count && calls.All(c => c.Ghost && c.GhostInit) && !GhostInit(), "Ghost mode seeds it in ghost mode");
    verdict = _ => null;
    var escaped = false;
    try
    {
      postfix.Invoke(null, [zone.ToVector2s(), ZoneSystem.SpawnMode.Full, spawn]);
    }
    catch (Exception)
    {
      escaped = true;
    }
    C(!escaped, "and nothing escapes from the patch whatever seeding does (the game would generate the zone again)");
  }

  // ------------------------------------------------------------------------------------------------ what the planner is given

  private static void BakedGatherTests()
  {
    var layer = ValtimaForSeeding(nameof(BakedGatherTests));
    if (layer == null)
      return;
    Section("baked reconciliation: what the world and the layer give the planner");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    BakedServer.FindPrefab = _ => null;
    BakedReconcile.RotationOf = zdo => Quaternion.identity;
    // (The game's own answer asks ZoneSystem.instance != null, which a made-up object does not pass.)
    BakedReconcile.IsGenerated = zone => w.Generated.Contains(zone.ToVector2s());
    var withLive = layer.Zones.Where(r => r.Live > 0).OrderBy(r => r.Key.Z).ThenBy(r => r.Key.X).Take(6).ToList();
    var generated = withLive.Take(4).Select(r => r.Key).ToList();
    foreach (var key in generated)
      w.Generated.Add(key.ToVector2s());
    // The pieces: one with a revision, one with items, one in a zone nobody generated, a portal; and objects with no bake id.
    var zone = generated[0].ToVector2s();
    var a = BakedPieceAt(w, zone, "wood_door", 0, 5, dx: 3f, dz: -2f, revision: 4);
    var b = BakedPieceAt(w, zone, "piece_chest", 2, uint.MaxValue, dx: -3f);
    ZDOExtraData.Set(b.m_uid, ZDOVars.s_items, ChestWithItems(2));
    var c = w.Add(zone, "wood_door", dx: 8f);
    ZDOExtraData.Set(c.m_uid, BakeSrcHash, 1);
    w.Add(zone, "Pine");
    var d = BakedPieceAt(w, withLive[5].Key.ToVector2s(), "forge", 0, 6);
    var portal = w.AddPortal(zone, 42L);
    ZDOExtraData.Set(portal.m_uid, BakeIdHash, 9);
    ZDOExtraData.Set(portal.m_uid, BakeSrcHash, 3);
    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, layer, made => input = made);
    int steps = 0;
    while (gathering.MoveNext())
      steps++;
    C(input != null && ReferenceEquals(input.Layer, layer) && input.Revision == layer.Revision, "the input is for the layer, and its revision");
    int live = generated.Sum(k => layer.TryGetZoneRow(k, out var row) ? row.Live : 0);
    C(input.Records.Count == live && input.Records.All(r => r.Unresolved && generated.Contains(r.Zone)), $"its records are the Live records of the generated zones only ({input.Records.Count} of {live}); none is resolved here: the game has no prefabs");
    C(input.Records.Select(r => r.Key).Distinct().Count() == input.Records.Count && input.Records.All(r => r.Source == 0), "each with its own (source, id)");
    var pieces = input.Pieces.ToDictionary(p => p.Uid);
    C(pieces.Count == 4 && pieces.ContainsKey(a.m_uid) && pieces.ContainsKey(b.m_uid) && pieces.ContainsKey(d.m_uid) && pieces.ContainsKey(portal.m_uid) && !pieces.ContainsKey(c.m_uid),
      $"its pieces are the objects that carry bake keys with an id, in any zone and the portals too ({pieces.Count})");
    var pa = pieces[a.m_uid];
    C(pa.Source == 0 && pa.BakeId == 5 && pa.Revision == 4 && pa.Prefab == "wood_door".GetStableHashCode() && pa.Zone == generated[0] && !pa.HoldsItems && (pa.Position - a.GetPosition()).magnitude < 1e-4f,
      "a piece has its source, id, revision, prefab, zone and position");
    C(pieces[b.m_uid].BakeId == uint.MaxValue && pieces[b.m_uid].Source == 2 && pieces[b.m_uid].HoldsItems && pieces[portal.m_uid].Source == 3 && pieces[portal.m_uid].BakeId == 9,
      "an id over 2 billion comes back unchecked, a chest with items says so, and a portal is read from its own list");
    // A piece with no revision key.
    var bare = w.Add(zone, "forge", dx: 11f);
    ZDOExtraData.Set(bare.m_uid, BakeIdHash, 12);
    ReconcileInput again = null;
    var second = BakedReconcile.Gather(layer, layer, made => again = made);
    while (second.MoveNext())
    {
    }
    C(again.Pieces.Single(p => p.Uid == bare.m_uid).Revision == -1 && again.Pieces.Single(p => p.Uid == bare.m_uid).Source == 0, "a piece with no revision says -1, and no source is the compiler's");
    // The vegetation where the compiler's clear cells grew: every generated zone of flag 16 against none; none against the same layer.
    C(input.Growth.Count == generated.Count(k => layer.TryGetZoneRow(k, out var row) && (row.NoVegetation || row.HasClearMask)) && input.Growth.All(g => g.Cells.Any), $"a first layer clears the cells of every generated zone it has a flag or mask in ({input.Growth.Count})");
    C(again.Growth.Count == 0, "the same layer as before grows none");
    // The plan over it: unresolved records leave what stands under their key alone, the rest of the pieces go (or stay as orphans).
    var plan = BakedReconcile.Plan(input);
    var unheld = input.Pieces.Where(p => !input.Records.Any(r => r.Key == p.Key)).ToList();
    C(plan.Seeds == 0 && plan.Replaces == 0 && plan.Removes + plan.Orphans == unheld.Count && plan.Orphans == unheld.Count(p => p.HoldsItems) && plan.Keeps == input.Pieces.Count - unheld.Count,
      $"unresolved records seed and replace nothing; the pieces whose key the layer lacks go, or stay as orphans when they hold items: {plan.Summary()}");
    // A world that is gone: only the layer's side is gathered.
    BakedReconcile.Objects = () => null;
    ReconcileInput empty = null;
    var none = BakedReconcile.Gather(null, layer, made => empty = made);
    while (none.MoveNext())
    {
    }
    C(empty != null && empty.Pieces.Count == 0 && empty.Records.Count == live, "with no world the pieces are none and the records are the layer's");
  }

  // ------------------------------------------------------------------------------------------------ the executor

  private static LiveRecord SeedRecord(int source, uint id, string prefab, Vector3 pivot) =>
    new(source, id, ZoneKey.OfPoint(pivot), prefab.GetStableHashCode(), pivot, Quaternion.identity, unresolved: false);

  private static void BakedExecuteTests()
  {
    Section("baked reconciliation: Seed, Keep, Replace, Remove and Orphan carried out on real ZDOs");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(1, 1);
    w.Generated.Add(zone);
    BakedReconcile.IsGenerated = z => w.Generated.Contains(z.ToVector2s());
    var turns = new Dictionary<ZDOID, Quaternion>();
    BakedReconcile.RotationOf = zdo => turns.TryGetValue(zdo.m_uid, out var q) ? q : Quaternion.identity;
    BakedReconcile.VegetationPrefabs = () => [];
    var sends = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends.Add(w.DestroyQueue.Count);
      w.Flush();
    };
    // What seeding makes here: a ZDO at the record's pivot, with the keys.
    var names = new[] { "forge", "wood_door", "piece_chest" }.ToDictionary(n => n.GetStableHashCode());
    var seeded = new List<LiveRecord>();
    BakedReconcile.Seeder = (context, record) =>
    {
      seeded.Add(record);
      var made = w.Add(Z(record.Zone.X, record.Zone.Z), names[record.Prefab], record.Pivot.x - record.Zone.X * 64f, record.Pivot.z - record.Zone.Z * 64f, record.Pivot.y);
      ZDOExtraData.Set(made.m_uid, BakeSrcHash, record.Source);
      ZDOExtraData.Set(made.m_uid, BakeIdHash, unchecked((int)record.Id));
      ZDOExtraData.Set(made.m_uid, BakeRevHash, 5);
      turns[made.m_uid] = record.Rotation;
      return true;
    };

    var kept = BakedPieceAt(w, zone, "forge", 0, 1, revision: 1);
    var same = BakedPieceAt(w, zone, "forge", 0, 7, dx: 5f, revision: 5);
    var moved = BakedPieceAt(w, zone, "wood_door", 0, 2, dx: 10f);
    var gone = BakedPieceAt(w, zone, "forge", 0, 3, dx: -10f, dz: 5f);
    var full = BakedPieceAt(w, zone, "piece_chest", 0, 4, dx: -5f, dz: -5f);
    ZDOExtraData.Set(full.m_uid, ZDOVars.s_items, ChestWithItems(3));
    var carried = BakedPieceAt(w, zone, "piece_chest", 2, 5, dz: 10f);
    ZDOExtraData.Set(carried.m_uid, ZDOVars.s_items, ChestWithItems(1));
    var records = new[]
    {
      SeedRecord(0, 1, "forge", kept.GetPosition()), SeedRecord(0, 7, "forge", same.GetPosition()), SeedRecord(0, 2, "wood_door", moved.GetPosition() + new Vector3(3f, 0f, 0f)),
      SeedRecord(2, 5, "piece_chest", carried.GetPosition() + new Vector3(4f, 0f, 0f)), SeedRecord(0, 6, "forge", new Vector3(zone.x * 64f - 12f, 30f, zone.y * 64f + 12f)),
    };
    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, null, made => input = made);
    while (gathering.MoveNext())
    {
    }
    input.Revision = 5;
    input.Records.AddRange(records);
    var plan = BakedReconcile.Plan(input);
    C(plan.Seeds == 1 && plan.Replaces == 1 && plan.Removes == 1 && plan.Orphans == 2 && plan.Keeps == 2 && plan.Refreshes == 1, $"(the plan: {plan.Summary()})");
    var lines = new List<string>();
    var steps = BakedReconcile.Execute(plan, lines.Add);
    int frames = 0;
    while (steps.MoveNext())
      frames++;
    var destroyed = w.All.ToHashSet();
    C(frames == 0, $"a few changes are one frame's work ({frames} yields)");
    C(kept.GetInt(BakedKeys.Rev, -1) == 5 && !destroyed.Contains(kept.m_uid) && kept.GetInt(BakedKeys.Id, 0) == 1, "Keep with an older revision: the piece takes the new revision and nothing else");
    C(same.GetInt(BakedKeys.Rev, -1) == 5 && !destroyed.Contains(same.m_uid), "Keep at the layer's revision: untouched");
    C(destroyed.Contains(moved.m_uid) && seeded.Any(r => r.Id == 2), "Replace: the stale piece is destroyed and the record seeded");
    C(destroyed.Contains(gone.m_uid) && seeded.All(r => r.Id != 3), "Remove: the piece goes, and nothing is seeded");
    C(!destroyed.Contains(full.m_uid) && !full.GetInt(BakedKeys.Id, out _) && !full.GetInt(BakedKeys.Src, out _) && !full.GetInt(BakedKeys.Rev, out _) && full.GetString(BakedKeys.Orphan) == "0/4" && !seeded.Any(r => r.Id == 4),
      "Orphan (no record): the chest stays, loses its bake keys, and says what it was");
    C(!destroyed.Contains(carried.m_uid) && !carried.GetInt(BakedKeys.Id, out _) && carried.GetString(BakedKeys.Orphan) == "2/5" && seeded.Any(r => r.Source == 2 && r.Id == 5),
      "Orphan (its record moved): the chest stays where it is as an orphan, and the record is seeded where it belongs");
    C(seeded.Count == 3 && seeded.Select(r => r.Id).OrderBy(x => x).SequenceEqual(new uint[] { 2, 5, 6 }), "three records were seeded: the replaced, the carried and the new");
    C(sends.Count == 1 && sends[0] == 2, $"what was destroyed was sent once, two objects ({sends.Count} sends, {string.Join(",", sends)})");
    C(lines.Count == 1 && lines[0] == "Baked pieces reconciled: 3 seeded, 1 replaced, 1 removed, 2 left standing as orphans (`bc_bake orphans` lists them), 2 kept.", "and it said what it did: " + string.Join(" | ", lines));

    // The world it left is what the layer says: the next plan keeps every piece and changes nothing.
    ReconcileInput after = null;
    var scan = BakedReconcile.Gather(null, null, made => after = made);
    while (scan.MoveNext())
    {
    }
    after.Revision = 5;
    after.Records.AddRange(records);
    var next = BakedReconcile.Plan(after);
    C(next.Keeps == records.Length && next.Changes == 0 && next.Seeds == 0 && next.Removes == 0 && next.Orphans == 0, $"a second plan over the world it left changes nothing: {next.Summary()}");
    // And the orphans are not pieces of the layer any more: nothing is planned for them.
    C(after.Pieces.All(p => p.Uid != full.m_uid && p.Uid != carried.m_uid), "the orphans are no pieces for the planner: they have no id");
    // Carrying out the plan that changes nothing changes nothing.
    seeded.Clear();
    sends.Clear();
    lines.Clear();
    var quiet = BakedReconcile.Execute(next, lines.Add);
    while (quiet.MoveNext())
    {
    }
    C(seeded.Count == 0 && sends.Count == 0 && lines.Count == 1 && lines[0].Contains("5 kept"), "and running it changes nothing: " + string.Join(" | ", lines));
  }

  private static void BakedExecuteLimitsTests()
  {
    Section("baked reconciliation: a thousand changes a frame at most, a world that closes, a seeder that throws");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(1, 1);
    w.Generated.Add(zone);
    BakedReconcile.IsGenerated = z => true;
    var sends = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends.Add(w.DestroyQueue.Count);
      w.Flush();
    };
    BakedReconcile.RotationOf = zdo => Quaternion.identity;
    BakedReconcile.VegetationPrefabs = () => [];
    var pieces = new List<ZDO>();
    for (uint id = 1; id <= 2500; id++)
      pieces.Add(BakedPieceAt(w, zone, "forge", 0, id, dx: (id % 50) * 0.5f - 12f, dz: (id / 50) * 0.4f - 10f));
    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, null, made => input = made);
    while (gathering.MoveNext())
    {
    }
    input.Revision = 2;
    var plan = BakedReconcile.Plan(input);
    C(plan.Removes == 2500 && plan.Items.Count == 2500, "(a plan of 2,500 removals)");
    var lines = new List<string>();
    var steps = BakedReconcile.Execute(plan, lines.Add);
    int frames = 0;
    while (steps.MoveNext())
      frames++;
    C(sends.Sum() == 2500 && sends.Max() <= BakedReconcile.ChangesPerFrame && frames >= 2 && sends.Count >= 3, $"all 2,500 are destroyed, at most {BakedReconcile.ChangesPerFrame} in a frame, and sent as they come ({frames} yields, sends of {string.Join(", ", sends)})");
    C(pieces.All(p => w.All.Contains(p.m_uid)) && lines.Count == 1 && lines[0].Contains("2500 removed"), "each one went to the clients: " + string.Join(" | ", lines));

    // The world closes while it works: the executor says so and stops, with the rest undone.
    ZDOExtraData.Reset();
    var w2 = new World();
    var sends2 = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends2.Add(w2.DestroyQueue.Count);
      w2.Flush();
    };
    w2.Generated.Add(zone);
    for (uint id = 1; id <= 2500; id++)
      BakedPieceAt(w2, zone, "forge", 0, id, dx: (id % 50) * 0.5f - 12f, dz: (id / 50) * 0.4f - 10f);
    ReconcileInput input2 = null;
    var gather2 = BakedReconcile.Gather(null, null, made => input2 = made);
    while (gather2.MoveNext())
    {
    }
    var plan2 = BakedReconcile.Plan(input2);
    var lines2 = new List<string>();
    var work = BakedReconcile.Execute(plan2, lines2.Add);
    C(work.MoveNext(), "(a first frame of changes)");
    World.Close();
    while (work.MoveNext())
    {
    }
    C(lines2.Count == 1 && lines2[0].Contains("the world closed") && sends2.Sum() <= BakedReconcile.ChangesPerFrame * 2, $"a world that closes ends it, with a line: {string.Join(" | ", lines2)}");

    // A seeder that throws costs that record: the others go on, three errors are logged, and the summary counts them.
    ZDOExtraData.Reset();
    var w3 = new World();
    w3.Generated.Add(zone);
    ZoneRegen.SendDestroyQueue = () => w3.Flush();
    BakedReconcile.Seeder = (context, record) => throw new InvalidOperationException("the prefab went wrong");
    var input3 = new ReconcileInput { Revision = 3, IsGenerated = _ => true };
    for (uint id = 1; id <= 5; id++)
      input3.Records.Add(SeedRecord(0, id, "forge", new Vector3(zone.x * 64f + id, 30f, zone.y * 64f)));
    var plan3 = BakedReconcile.Plan(input3);
    var lines3 = new List<string>();
    var logged = LogHandler.During(() =>
    {
      var run = BakedReconcile.Execute(plan3, lines3.Add);
      while (run.MoveNext())
      {
      }
    });
    C(plan3.Seeds == 5 && lines3.Count == 1 && lines3[0].Contains("5 could not be done (see the log)") && !lines3[0].Contains("seeded"), "five seeders that throw are five errors, counted: " + string.Join(" | ", lines3));
    C(logged.Count(l => l.Contains("could not be reconciled")) == 3, $"and only three are logged, with their reason ({logged.Count(l => l.Contains("could not be reconciled"))})");
  }

  // The plan is frames old when it runs: a chest it saw empty may have been opened and filled meanwhile.
  private static void BakedExecuteRecheckTests()
  {
    Section("baked reconciliation: a piece that holds items when the change is made is not destroyed, whatever the plan saw");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(1, 1);
    w.Generated.Add(zone);
    BakedReconcile.IsGenerated = z => w.Generated.Contains(z.ToVector2s());
    BakedReconcile.RotationOf = _ => Quaternion.identity;
    var seeded = new List<LiveRecord>();
    BakedReconcile.Seeder = (context, record) =>
    {
      seeded.Add(record);
      return true;
    };
    var sends = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends.Add(w.DestroyQueue.Count);
      w.Flush();
    };
    var opened = BakedPieceAt(w, zone, "piece_chest", 0, 1, dx: -10f);
    var movedAway = BakedPieceAt(w, zone, "piece_chest", 0, 2, dx: 10f);
    var untouched = BakedPieceAt(w, zone, "forge", 0, 3, dz: 10f);
    var smelter = BakedPieceAt(w, zone, "smelter", 0, 4, dz: -10f);
    ReconcileInput gathered = null;
    var scan = BakedReconcile.Gather(null, null, made => gathered = made);
    while (scan.MoveNext())
    {
    }
    // The record of `movedAway` is 3 m off (a Replace); the other three have no record (Remove).
    gathered.Revision = 2;
    gathered.Records.Add(SeedRecord(0, 2, "piece_chest", movedAway.GetPosition() + new Vector3(3f, 0f, 0f)));
    var plan = BakedReconcile.Plan(gathered);
    C(plan.Removes == 3 && plan.Replaces == 1 && plan.Orphans == 0, $"(the plan saw them all empty: {plan.Summary()})");
    // Meanwhile players put things in: a chest, the moved chest, a smelter's queue.
    ZDOExtraData.Set(opened.m_uid, ZDOVars.s_items, ChestWithItems(2));
    ZDOExtraData.Set(movedAway.m_uid, ZDOVars.s_items, ChestWithItems(1));
    ZDOExtraData.Set(smelter.m_uid, ZDOVars.s_queued, 4);
    var lines = new List<string>();
    var run = BakedReconcile.Execute(plan, lines.Add);
    while (run.MoveNext())
    {
    }
    var gone = w.All.ToHashSet();
    C(!gone.Contains(opened.m_uid) && !gone.Contains(movedAway.m_uid) && !gone.Contains(smelter.m_uid), "the chest, the chest that was to be replaced and the smelter with ore queued are not destroyed");
    C(opened.GetString(BakedKeys.Orphan) == "0/1" && movedAway.GetString(BakedKeys.Orphan) == "0/2" && smelter.GetString(BakedKeys.Orphan) == "0/4"
      && !opened.GetInt(BakedKeys.Id, out _) && !movedAway.GetInt(BakedKeys.Id, out _) && !smelter.GetInt(BakedKeys.Id, out _), "each is an orphan now: its bake keys gone, the mark saying what it was");
    C(gone.Contains(untouched.m_uid) && w.All.Count() == 1, "the one that is still empty goes, and nothing else");
    C(seeded.Count == 1 && seeded[0].Id == 2, "the replaced chest's record is seeded where it belongs all the same (the orphan stays where it stood)");
    C(sends.Count == 1 && sends[0] == 1, $"only the one that was destroyed is sent ({string.Join(",", sends)})");
    C(lines.SequenceEqual(["Baked pieces reconciled: 1 seeded, 1 removed, 3 left standing as orphans (`bc_bake orphans` lists them), 0 kept."]), "and the output counts them as orphans: " + string.Join(" | ", lines));
    C(BakedReconcile.Orphans().Count == 3 && BakedReconcile.Orphans().All(o => o.HoldsItems), "`bc_bake orphans` lists the three, each holding items");
  }

  private static void BakedVegetationTests()
  {
    Section("baked reconciliation: the world's vegetation in the cells the compiler's file cleared");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(4, 4);
    w.Generated.Add(zone);
    var key = ZoneKey.Of(zone);
    var sends = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends.Add(w.DestroyQueue.Count);
      w.Flush();
    };
    BakedReconcile.RotationOf = _ => Quaternion.identity;
    BakedReconcile.VegetationPrefabs = () => ["Pine".GetStableHashCode(), "Rock_3".GetStableHashCode()];
    // Cell (row 0, column 0) and cell (row 3, column 5) are cleared: x, z from the zone's south-west corner (zone centre - 32).
    var bits = new byte[BakedFormat.MaskBytes];
    foreach (var cell in new[] { 0, 3 * 16 + 5 })
      bits[cell >> 3] |= (byte)(1 << (cell & 7));
    ZDO Tree(string prefab, float x, float z)
    {
      // (dx, dz) from the zone's centre
      return w.Add(zone, prefab, x - 32f, z - 32f);
    }
    var inFirst = Tree("Pine", 1f, 2f);
    var inSecond = Tree("Rock_3", 5 * 4 + 1f, 3 * 4 + 3.9f);
    var outside = Tree("Pine", 40f, 40f);
    var justOut = Tree("Pine", 4.01f, 1f);
    var planted = Creator(Tree("Pine", 2f, 2f), 42L);
    var kept = Tree("Pine", 3f, 1f);
    ZDOExtraData.Set(kept.m_uid, BakeIdHash, 5);
    var door = Tree("wood_door", 1f, 1f);
    var plan = new ReconcilePlan(new ReconcileInput { Revision = 1, IsGenerated = _ => true });
    plan.Vegetation.Add(new VegetationGrowth(key, new BakedVegetation.ZoneMask(false, bits)));
    var lines = new List<string>();
    var run = BakedReconcile.Execute(plan, lines.Add);
    while (run.MoveNext())
    {
    }
    var gone = w.All.ToHashSet();
    C(gone.Contains(inFirst.m_uid) && gone.Contains(inSecond.m_uid), "the trees and rocks the world made in a cleared cell go");
    C(!gone.Contains(outside.m_uid) && !gone.Contains(justOut.m_uid), "one outside the cleared cells stays, also 1 cm past the first cell's edge");
    C(!gone.Contains(planted.m_uid) && !gone.Contains(kept.m_uid) && !gone.Contains(door.m_uid), "a tree with a creator (a player planted it), one with a bake id, and a prefab that is no vegetation stay");
    C(lines.Count == 1 && lines[0].Contains("2 trees, rocks and bushes cleared") && sends.Sum() == 2, "and the output counts them: " + string.Join(" | ", lines));
    // The whole zone: flag 16.
    var every = new ReconcilePlan(new ReconcileInput { Revision = 1, IsGenerated = _ => true });
    every.Vegetation.Add(new VegetationGrowth(key, BakedVegetation.ZoneMask.Everywhere));
    var all = BakedReconcile.Execute(every, lines.Add);
    while (all.MoveNext())
    {
    }
    var goneNow = w.All.ToHashSet();
    C(goneNow.Contains(outside.m_uid) && goneNow.Contains(justOut.m_uid) && !goneNow.Contains(planted.m_uid) && !goneNow.Contains(kept.m_uid), "a whole zone cleared takes every tree the world made in it, still not a planted or a baked one");
    // Without vegetation prefabs (a game that has none loaded) nothing is touched.
    BakedReconcile.VegetationPrefabs = () => [];
    var before = w.All.Count();
    var empty = BakedReconcile.Execute(every, lines.Add);
    while (empty.MoveNext())
    {
    }
    C(w.All.Count() == before, "with no vegetation prefabs known, nothing is cleared");
  }

  private static void BakedOrphanTests()
  {
    Section("baked reconciliation: orphans are listed, destroyed on request, and baked for a zone reset");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var zone = Z(2, 2);
    ZDO Orphan(string prefab, string value, float dx, bool items)
    {
      var zdo = w.Add(zone, prefab, dx);
      ZDOExtraData.Set(zdo.m_uid, OrphanHash, value);
      if (items)
        ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_items, ChestWithItems(2));
      return zdo;
    }
    var chest = Orphan("piece_chest", "0/4", -5f, items: true);
    var emptied = Orphan("piece_chest", "2/9", 5f, items: false);
    var odd = Orphan("forge", "not a mark", 9f, items: false);
    var piece = BakedPieceAt(w, zone, "forge", 0, 1, dx: 12f);
    var bystander = w.Add(zone, "Pine", dx: 14f);
    var portal = w.AddPortal(zone, 42L);
    ZDOExtraData.Set(portal.m_uid, OrphanHash, "1/1");
    var listed = BakedReconcile.Orphans();
    C(listed.Count == 4 && listed.All(o => o.Id != piece.m_uid && o.Id != bystander.m_uid), $"the orphans are the objects that carry the mark, a portal's too ({listed.Count})");
    var one = listed.Single(o => o.Id == chest.m_uid);
    C(one.HoldsItems && one.Reason.Contains("source 0, id 4") && one.Reason.Contains("holds items") && (one.Position - chest.GetPosition()).magnitude < 1e-4f && one.Prefab == "prefab " + "piece_chest".GetStableHashCode(),
      "a chest with items says what it was and that it holds items: " + one.Reason);
    var two = listed.Single(o => o.Id == emptied.m_uid);
    C(!two.HoldsItems && two.Reason.Contains("source 2, id 9") && two.Reason.Contains("what it held is gone"), "one without says what it held is gone: " + two.Reason);
    C(listed.Single(o => o.Id == odd.m_uid).Reason.Contains("a piece the layer had"), "a mark that cannot be read is still an orphan");

    // For a zone reset an orphan is baked: it stays, and does not keep its zone from being reset by itself.
    var local = new ZDOID(600L, 1);
    C(ZoneRegen.KindOf(chest, local) == Kind.Baked && (ZoneRegen.KindOf(portal, local) & Kind.Baked) != 0, "an orphan is Kind.Baked (a portal's creator makes it a piece too), so a reset never empties its chest");
    C(ZoneReset.Rules.Survives(ZoneRegen.KindOf(chest, local)) && !ZoneReset.Rules.Protects(ZoneRegen.KindOf(chest, local)), "which survives and does not protect");

    // The admin destroys some of them by id: only objects that still say they are orphans.
    var sends = new List<int>();
    ZoneRegen.SendDestroyQueue = () =>
    {
      sends.Add(w.DestroyQueue.Count);
      w.Flush();
    };
    var lines = new List<string>();
    BakedReconcile.DestroyOrphans([chest.m_uid, piece.m_uid, new ZDOID(1000L, 99999), emptied.m_uid], lines.Add);
    var destroyed = w.All.ToHashSet();
    C(destroyed.Contains(chest.m_uid) && destroyed.Contains(emptied.m_uid) && !destroyed.Contains(piece.m_uid) && !destroyed.Contains(odd.m_uid) && !destroyed.Contains(portal.m_uid), "the named orphans go; a piece of the layer that was named by mistake and an id nobody has do not; the orphans not named stay");
    C(sends.Count == 1 && sends[0] == 2 && lines.SequenceEqual(["Destroyed 2 orphans; 2 named were not an orphan any more."]), "and the output says it: " + string.Join(" | ", lines));
    lines.Clear();
    BakedReconcile.DestroyOrphans([odd.m_uid], lines.Add);
    C(lines.SequenceEqual(["Destroyed 1 orphan."]), "one orphan, singular: " + string.Join(" | ", lines));
    lines.Clear();
    BakedReconcile.DestroyOrphans([], lines.Add);
    C(lines.SequenceEqual(["Destroyed 0 orphans."]), "none named: " + string.Join(" | ", lines));
    BakedReconcile.Objects = () => null;
    C(BakedReconcile.Orphans().Count == 0, "with no world there are none");
  }
}

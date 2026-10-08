// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Consumables are never baked (build spec 0.2, BakedConsumables): in a compiler's file every record of a consumable kind, whatever its role, is placed
// ONCE as an ordinary object when its zone generates (BakedServer.SeedZone), with no bake keys, no bc_protect and no creator, and reconciliation
// never seeds or removes one. Checked here on a made-up layer (the kinds of Valheim's own vegetation, a door, a wall, a tree) with the prefabs
// of the game stood in for, and on VALtima's real file. The piece-making seams stand in for Instantiate, which only the autotest can run; what
// the real PlaceConsumable may call is read from its IL.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Kind = BetterContinents.ZoneReset.Kind;

internal static partial class Program
{
  private static readonly string[] ConsumableNames = ["Pickable_Flax_Wild", "Pickable_Dandelion", "Pickable_Mushroom", "RaspberryBush"];

  // The game's prefabs as a test sees them: made-up objects by name, which of them are consumables, and which have no ZNetView.
  private sealed class ConsumableGame
  {
    public readonly Dictionary<string, GameObject> Prefabs = [];
    public readonly HashSet<GameObject> Consumables = new(ReferenceEqualityComparer.Instance);
    public readonly HashSet<GameObject> NoView = new(ReferenceEqualityComparer.Instance);

    public ConsumableGame Has(string name, bool consumable = false, bool view = true)
    {
      var prefab = FinalAlive<GameObject>();
      Prefabs[name] = prefab;
      if (consumable)
        Consumables.Add(prefab);
      if (!view)
        NoView.Add(prefab);
      return this;
    }

    // The seams of seeding, stood in: the game has these prefabs, and reads them this way.
    public void Install()
    {
      BakedServer.FindPrefab = name => Prefabs.TryGetValue(name, out var prefab) ? prefab : null;
      BakedConsumables.Probe = prefab => Consumables.Contains(prefab);
      BakedServer.ViewOf = prefab => NoView.Contains(prefab) ? null : true;
      BakedReconcile.ConsumablePrefab = hash => Prefabs.Any(p => p.Key.GetStableHashCode() == hash && Consumables.Contains(p.Value));
    }
  }

  // A layer with one zone holding every case: consumables of every role, and the pieces that are not.
  private static (BakedLayer Layer, ZoneKey Zone, int[] Entries) ConsumableLayer()
  {
    var edit = LayerEdit.New("consumables");
    PaletteEntry Entry(BakedRole role, params string[] names) =>
      new(names.Select(n => new Candidate(n, 0, 0, 0)).ToArray(), role, role == BakedRole.Live ? BakedCollision.Prefab : BakedCollision.None,
        0, role == BakedRole.Live ? PaletteFlags.Protect : PaletteFlags.None);
    var entries = new[]
    {
      edit.PaletteIndexFor(Entry(BakedRole.Static, "Pickable_Flax_Wild")),                  // 0 a consumable, drawn as Static by the file
      edit.PaletteIndexFor(Entry(BakedRole.Static, "RaspberryBush", "Bush01")),             // 1 a consumable whose stand-in is a plain bush
      edit.PaletteIndexFor(Entry(BakedRole.Live, "Pickable_Mushroom")),                     // 2 a consumable the file marks Live, with ids
      edit.PaletteIndexFor(Entry(BakedRole.Live, "wood_door")),                             // 3 an ordinary Live piece
      edit.PaletteIndexFor(Entry(BakedRole.Static, "stone_wall_2x1")),                      // 4 an ordinary Static piece
      edit.PaletteIndexFor(Entry(BakedRole.Copy, "Pickable_Dandelion")),                    // 5 a consumable with another role
      edit.PaletteIndexFor(Entry(BakedRole.Static, "Pine")),                                // 6 a tree: decoration
      edit.PaletteIndexFor(Entry(BakedRole.Static, "Missing_Mod_Pickable")),                // 7 a prefab the game does not have
      edit.PaletteIndexFor(Entry(BakedRole.Static, "Pickable_NoView")),                     // 8 a consumable that cannot be placed
    };
    var zone = new ZoneKey(3, -2);
    double x0 = zone.OriginX + 4, z0 = zone.OriginZ + 4;
    var records = new List<ZoneRecord>();
    void Add(int entry, int count, double step, uint? firstId = null)
    {
      for (int n = 0; n < count; n++)
        records.Add(ZoneRecord.CreateYaw(entries[entry], x0 + step * n, 40, z0 + entry * 3.0, 15 * n, null, firstId == null ? null : firstId + (uint)n));
    }
    Add(0, 3, 2.0);
    Add(1, 2, 2.0);
    Add(2, 2, 2.0, firstId: 10);
    Add(3, 2, 5.0, firstId: 20);
    Add(4, 3, 4.0);
    Add(5, 1, 2.0);
    Add(6, 2, 6.0);
    Add(7, 1, 2.0);
    Add(8, 1, 2.0);
    edit.AddRecords(records);
    return (edit.Build().Layer, zone, entries);
  }

  private static void BakedConsumableTests()
  {
    BakedConsumableKindTests();
    BakedConsumableSeedTests();
    BakedConsumableReconcileTests();
    BakedConsumableCodeTests();
    BakedConsumableValtimaTests();
  }

  // ------------------------------------------------------------------------------------------------ which entries are consumables

  private static void BakedConsumableKindTests()
  {
    Section("baked consumables: an entry is a consumable by the prefab it resolves to, whatever its name or its role");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var (layer, _, entries) = ConsumableLayer();
    var game = new ConsumableGame().Has("Pickable_Flax_Wild", consumable: true).Has("Bush01").Has("Pickable_Mushroom", consumable: true).Has("wood_door").Has("stone_wall_2x1")
      .Has("Pickable_Dandelion", consumable: true).Has("Pine").Has("Pickable_NoView", consumable: true, view: false);
    game.Install();
    var context = new BakedServer.Context(layer);
    bool Is(int entry, out BakedServer.Resolved? kind) => BakedServer.IsConsumable(context, entries[entry], layer.Palette[entries[entry]], out kind);
    C(Is(0, out var flax) && flax != null && flax.Candidate.Name == "Pickable_Flax_Wild" && flax.SyncsScale, "a Static entry of a pickable is a consumable, and is placed as that prefab");
    C(!Is(1, out var bush) && bush == null, "RaspberryBush missing, its stand-in Bush01 is a plain bush: the entry is not a consumable (the resolved prefab decides)");
    C(Is(2, out var mushroom) && mushroom != null && mushroom.Candidate.Name == "Pickable_Mushroom", "a pickable the file marks Live is a consumable all the same");
    C(!Is(3, out _) && !Is(4, out _), "a door and a wall are not");
    C(Is(5, out var dandelion) && dandelion != null, "a pickable with the role Copy is one");
    C(!Is(6, out _), "a tree is not");
    C(!Is(7, out _), "an entry of a prefab the game does not have is not one");
    var lines = LogHandler.During(() => C(Is(8, out var none) && none == null, "a consumable with no ZNetView is one, and cannot be placed"));
    C(lines.Any(l => l.Contains("Pickable_NoView has no ZNetView")), "which the log says: " + string.Join(" | ", lines));
    C(Is(0, out var again) && ReferenceEquals(again, flax) && context.Consumables.Count == 4 && context.Plain.Count == 5, "each entry is decided once for a zone's pass");

    // The same layer where the game has RaspberryBush: now the first candidate the game has is a consumable.
    var withBush = new ConsumableGame().Has("RaspberryBush", consumable: true).Has("Bush01");
    withBush.Install();
    var second = new BakedServer.Context(layer);
    C(BakedServer.IsConsumable(second, entries[1], layer.Palette[entries[1]], out var raspberry) && raspberry!.Candidate.Name == "RaspberryBush", "with RaspberryBush in the game the same entry is a consumable");
    // A stand-in that is the consumable while the first candidate is a plain prefab: the first candidate the game has decides, and that is not it.
    var plainFirst = new ConsumableGame().Has("RaspberryBush").Has("Bush01", consumable: true);
    plainFirst.Install();
    C(!BakedServer.IsConsumable(new BakedServer.Context(layer), entries[1], layer.Palette[entries[1]], out _), "and the stand-in is never looked at while the first candidate is there");

    // Each consumable kind is named in the log once a session, not for every record.
    BakedConsumables.SessionStarts();
    lines = LogHandler.During(() =>
    {
      for (int n = 0; n < 5; n++)
        BakedConsumables.Note("Pickable_Flax_Wild");
      BakedConsumables.Note("Pickable_Mushroom");
    });
    C(lines.Count(l => l.Contains("Pickable_Flax_Wild is a consumable")) == 1 && lines.Count(l => l.Contains("Pickable_Mushroom is a consumable")) == 1 && BakedConsumables.Kinds.Count == 2,
      "each kind is named once in the log: " + string.Join(" | ", lines));
  }

  // ------------------------------------------------------------------------------------------------ a zone's seeding

  private static void BakedConsumableSeedTests()
  {
    Section("baked consumables: a zone places each record of a consumable kind once, with nothing baked on it, and the Live pieces as before");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var (layer, zone, entries) = ConsumableLayer();
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
    var game = new ConsumableGame().Has("Pickable_Flax_Wild", consumable: true).Has("Bush01").Has("Pickable_Mushroom", consumable: true).Has("wood_door").Has("stone_wall_2x1")
      .Has("Pickable_Dandelion", consumable: true).Has("Pine").Has("Pickable_NoView", consumable: true, view: false);
    game.Install();
    BakedConsumables.SessionStarts();
    var row = layer.Zones.Single(r => r.Key.Equals(zone));
    var data = layer.Decode(row);
    var all = data.Records().ToList();
    int expected = all.Count(r => entries[0] == r.Palette || entries[2] == r.Palette || entries[5] == r.Palette);
    C(all.Count == 17 && expected == 6 && row.Live == 4, $"(the zone holds {all.Count} records: 6 of consumable kinds with a usable prefab, 4 Live, 1 consumable that cannot be placed)");

    // The seams stand in for Instantiate, and add the object the game would have made: a keyed piece for a Live record, a plain object for a consumable.
    var pieces = new List<ZoneRecord>();
    var placed = new List<(string Prefab, ZoneRecord Record, bool Ghost, bool GhostInit)>();
    bool GhostInit() => GetStatic<bool>(typeof(ZNetView), "m_ghostInit");
    BakedServer.MakePiece = (context, index, palette, record, ghost, spawned) =>
    {
      pieces.Add(record);
      BakedPieceAt(w, zone.ToVector2s(), palette.Candidates[0].Name, record.SourceNumber, record.Id, dx: (float)(record.WorldX - zone.X * 64.0), dz: (float)(record.WorldZ - zone.Z * 64.0));
      return true;
    };
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      placed.Add((palette.Candidates[0].Name, record, ghost, GhostInit()));
      // Nothing is written on it: no bake keys, no creator.
      w.Add(zone.ToVector2s(), context.Consumables[index]!.Candidate.Name, dx: (float)(record.WorldX - zone.X * 64.0), dz: (float)(record.WorldZ - zone.Z * 64.0), y: (float)record.WorldY);
      return true;
    };

    var lines = LogHandler.During(() =>
    {
      int made = BakedServer.SeedZone(zone, ghost: false, spawned: null, out int consumables);
      C(made == 2 && pieces.Count == 2 && pieces.All(r => layer.Palette[r.Palette].Candidates[0].Name == "wood_door"), "the Live door records are seeded as before (2); a Live consumable is not");
      C(consumables == 6 && placed.Count == 6, $"six records of consumable kinds are placed ({placed.Count})");
    });
    C(placed.Count(p => p.Prefab == "Pickable_Flax_Wild") == 3 && placed.Count(p => p.Prefab == "Pickable_Mushroom") == 2 && placed.Count(p => p.Prefab == "Pickable_Dandelion") == 1,
      "three flax, two mushrooms (Live with ids) and a dandelion (role Copy): every role");
    C(!placed.Any(p => p.Prefab is "stone_wall_2x1" or "Pine" or "wood_door" or "Bush01" or "Missing_Mod_Pickable"), "nothing else is: not a wall, a tree, a door, the bush that stands in for a missing one, or a prefab the game lacks");
    C(placed.Select(p => p.Record.Position).Distinct().Count() == placed.Count, "each at its own record's place, once");
    C(lines.Any(l => l.Contains("1 could not be") && l.Contains("consumables")) && BakedConsumables.Placed == 6 && BakedConsumables.Failed == 1,
      $"the one with no ZNetView is counted as not placed and the zone's line says so ({BakedConsumables.Placed} placed, {BakedConsumables.Failed} failed): " + string.Join(" | ", lines.Where(l => l.Contains("consumables"))));
    C(BakedConsumables.Kinds.Count == 4 && lines.Count(l => l.Contains("is a consumable")) == 4, "the log names each of the four kinds once: " + string.Join(" | ", lines.Where(l => l.Contains("is a consumable"))));

    // What stands now carries no bake key and no creator; the doors do.
    var standing = w.ByZone[zone.ToVector2s()];
    var objects = standing.Where(z => game.Prefabs.Keys.Any(n => n.GetStableHashCode() == z.GetPrefab()) && z.GetPrefab() != "wood_door".GetStableHashCode()).ToList();
    C(objects.Count == 6 && objects.All(z => !z.GetInt(BakedKeys.Id, out _) && !z.GetInt(BakedKeys.Src, out _) && !z.GetInt(BakedKeys.Rev, out _) && !z.GetInt(BakedKeys.Protect, out _) && z.GetLong(ZDOVars.s_creator, 0L) == 0L),
      "none of the six has a bake key, bc_protect or a creator");
    C((ZoneRegen.KindOf(objects[0], new ZDOID(600L, 1)) & Kind.Baked) == 0, "so a zone reset treats it as the world's own object");

    // A second pass over the zone finds them standing, and places nothing more; the doors stand as well.
    placed.Clear();
    pieces.Clear();
    int again = BakedServer.SeedZone(zone, ghost: false, spawned: null, out int more);
    C(again == 0 && more == 0 && placed.Count == 0 && pieces.Count == 0, "a second generation pass makes nothing: the objects stand, the doors stand");
    // 4 cm from where it would be is the same place (the reconciliation's 5 cm); 6 cm is not.
    var flaxRecord = all.First(r => r.Palette == entries[0]);
    var someFlax = objects.First(z => z.GetPrefab() == "Pickable_Flax_Wild".GetStableHashCode() && (z.GetPosition() - flaxRecord.Position).magnitude < 0.01f);
    var there = BakedServer.PrefabsStanding(zone);
    var flaxKind = new BakedServer.Resolved(game.Prefabs["Pickable_Flax_Wild"], layer.Palette[entries[0]].Candidates[0], syncsScale: true);
    C(BakedServer.Stands(there, flaxKind, layer.Palette[entries[0]], flaxRecord), "(the flax record's object stands)");
    FPosition.SetValue(someFlax, someFlax.GetPosition() + new Vector3(0.04f, 0f, 0f));
    C(BakedServer.Stands(BakedServer.PrefabsStanding(zone), flaxKind, layer.Palette[entries[0]], flaxRecord), "4 cm away is the same place");
    FPosition.SetValue(someFlax, someFlax.GetPosition() + new Vector3(0.04f, 0f, 0f));
    C(!BakedServer.Stands(BakedServer.PrefabsStanding(zone), flaxKind, layer.Palette[entries[0]], flaxRecord), "8 cm away is not");

    // A zone reset destroys what the world made, the consumables with it, and the zone's generation places them again; the kept doors stand.
    var doors = standing.Where(z => z.GetPrefab() == "wood_door".GetStableHashCode())
      .Select(z => (Id: (uint)z.GetInt(BakedKeys.Id, 0), Position: z.GetPosition())).ToList();
    ZDOExtraData.Reset();
    var fresh = new World();
    foreach (var door in doors)
      BakedPieceAt(fresh, zone.ToVector2s(), "wood_door", 0, door.Id, dx: door.Position.x - zone.X * 64f, dz: door.Position.z - zone.Z * 64f);
    BakedConsumables.SessionStarts();
    placed.Clear();
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      placed.Add((palette.Candidates[0].Name, record, ghost, GhostInit()));
      fresh.Add(zone.ToVector2s(), palette.Candidates[0].Name, dx: (float)(record.WorldX - zone.X * 64.0), dz: (float)(record.WorldZ - zone.Z * 64.0), y: (float)record.WorldY);
      return true;
    };
    pieces.Clear();
    int afterReset = BakedServer.SeedZone(zone, ghost: false, spawned: null, out int regrown);
    C(afterReset == 0 && regrown == 6 && placed.Count == 6 && pieces.Count == 0, "after a reset the six are placed again and the kept doors are not seeded twice");

    // Ghost mode: the ghost initialisation is on while a consumable is made and off when the zone is done, and the list goes to the seam.
    ZDOExtraData.Reset();
    _ = new World();
    placed.Clear();
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      placed.Add((palette.Candidates[0].Name, record, ghost, GhostInit()));
      return true;
    };
    var list = new List<GameObject>();
    BakedServer.SeedZone(zone, ghost: true, spawned: list, out int ghosted);
    C(ghosted == 6 && placed.All(p => p.Ghost && p.GhostInit) && !GhostInit(), "in Ghost mode the ghost initialisation is on while each is made, and off afterwards");

    // A consumable that fails costs that one: the rest of the zone is placed, and the zone's Live pieces are seeded.
    ZDOExtraData.Reset();
    _ = new World();
    placed.Clear();
    pieces.Clear();
    var flaxes = all.Where(r => r.Palette == entries[0]).ToList();
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      placed.Add((palette.Candidates[0].Name, record, ghost, GhostInit()));
      return record.Position != flaxes[1].Position;
    };
    BakedServer.MakePiece = (context, index, palette, record, ghost, spawned) =>
    {
      pieces.Add(record);
      return true;
    };
    BakedConsumables.SessionStarts();
    int madeWhile = BakedServer.SeedZone(zone, ghost: false, spawned: null, out int placedWhile);
    C(madeWhile == 2 && placedWhile == 5 && placed.Count == 6 && BakedConsumables.Failed == 2, $"one flax that cannot be placed costs only itself ({placedWhile} placed, {BakedConsumables.Failed} failed with the one that has no view)");
    // A seam that throws is caught by the zone's seeding, whatever it was making.
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) => throw new InvalidOperationException("the prefab went wrong");
    var thrown = LogHandler.During(() => BakedServer.SeedZone(zone, ghost: true, spawned: new List<GameObject>(), out _));
    C(thrown.Any(l => l.Contains("could not be seeded") && l.Contains("the prefab went wrong")) && !GhostInit(), "and one that throws is logged, nothing escapes, and the ghost initialisation ends");
  }

  // ------------------------------------------------------------------------------------------------ reconciliation

  private static void BakedConsumableReconcileTests()
  {
    Section("baked consumables: the reconciliation never seeds or removes one, also after a reload");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var (layer, zone, entries) = ConsumableLayer();
    var game = new ConsumableGame().Has("Pickable_Flax_Wild", consumable: true).Has("Bush01").Has("Pickable_Mushroom", consumable: true).Has("wood_door").Has("stone_wall_2x1")
      .Has("Pickable_Dandelion", consumable: true).Has("Pine").Has("Pickable_NoView", consumable: true, view: false);
    game.Install();
    w.Generated.Add(zone.ToVector2s());
    BakedReconcile.IsGenerated = z => w.Generated.Contains(z.ToVector2s());
    var turns = new Dictionary<ZDOID, Quaternion>();
    BakedReconcile.RotationOf = zdo => turns.TryGetValue(zdo.m_uid, out var q) ? q : Quaternion.identity;
    BakedReconcile.VegetationPrefabs = () => [];
    ZoneRegen.SendDestroyQueue = w.Flush;
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };

    // The world after generation: nothing stands of the Live records yet (a Live mushroom picked since would be gone as well).
    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, layer, made => input = made);
    while (gathering.MoveNext())
    {
    }
    C(input.Records.Count == 2 && input.Records.All(r => r.Key == BakedFormat.LiveKey(0, r.Id) && r.Id is 20 or 21), $"the planner is given the two Live doors and not the two Live mushrooms ({input.Records.Count})");
    var plan = BakedReconcile.Plan(input);
    C(plan.Seeds == 2 && plan.Items.All(i => i.Record == null || i.Record.Id is 20 or 21), "so it seeds the doors only");

    // Pieces of a consumable prefab that carry keys (from a build that baked them) are left alone: neither kept, replaced, removed nor orphaned.
    var oldMushroom = BakedPieceAt(w, zone.ToVector2s(), "Pickable_Mushroom", 0, 10, dx: 10f);
    var strayFlax = BakedPieceAt(w, zone.ToVector2s(), "Pickable_Flax_Wild", 0, 999, dx: -10f);
    var strayDoor = BakedPieceAt(w, zone.ToVector2s(), "wood_door", 0, 777, dx: 12f);
    input = null;
    gathering = BakedReconcile.Gather(null, layer, made => input = made);
    while (gathering.MoveNext())
    {
    }
    plan = BakedReconcile.Plan(input);
    C(input.Pieces.Count == 3 && plan.Items.All(i => i.Piece == null || i.Piece.Uid == strayDoor.m_uid), $"of the three keyed pieces only the door the layer lacks is planned ({plan.Summary()})");
    C(plan.Removes == 1 && plan.Seeds == 2 && plan.Keeps == 0 && plan.Replaces == 0 && plan.Orphans == 0, "removed, and the two doors seeded: nothing about a mushroom or a flax");

    // Executed, the world keeps them, and a second plan over the result is all Keep.
    var seeded = new List<(int Source, uint Id)>();
    BakedReconcile.Seeder = (context, record) =>
    {
      seeded.Add((record.Source, record.Id));
      var made = BakedPieceAt(w, zone.ToVector2s(), "wood_door", record.Source, record.Id, dx: record.Pivot.x - zone.X * 64f, dz: record.Pivot.z - zone.Z * 64f);
      FPosition.SetValue(made, record.Pivot);
      turns[made.m_uid] = record.Rotation;
      return true;
    };
    var lines = new List<string>();
    var run = BakedReconcile.Execute(plan, lines.Add);
    while (run.MoveNext())
    {
    }
    var alive = w.ByZone[zone.ToVector2s()].Where(z => !w.All.Contains(z.m_uid)).Select(z => z.m_uid).ToHashSet();
    C(alive.Contains(oldMushroom.m_uid) && alive.Contains(strayFlax.m_uid) && !alive.Contains(strayDoor.m_uid), "the stray door is destroyed and the two consumables stand");
    C(seeded.Select(s => s.Id).OrderBy(x => x).SequenceEqual([20u, 21u]), "only the doors were seeded");
    // A reload: gathered again, nothing is planned for a consumable, and the doors are kept.
    seeded.Clear();
    input = null;
    gathering = BakedReconcile.Gather(layer, layer, made => input = made);
    while (gathering.MoveNext())
    {
    }
    plan = BakedReconcile.Plan(input);
    C(plan.Seeds == 0 && plan.Removes == 0 && plan.Replaces == 0 && plan.Orphans == 0 && plan.Keeps == 2, $"after a reload the plan is the doors kept and nothing else ({plan.Summary()})");
    // And the same layer once its mushrooms have been picked: they are not seeded again.
    ZDOExtraData.Reset();
    _ = new World();
    input = null;
    gathering = BakedReconcile.Gather(layer, layer, made => input = made);
    while (gathering.MoveNext())
    {
    }
    plan = BakedReconcile.Plan(input);
    C(plan.Items.All(i => i.Action != ReconcileAction.Seed || i.Record!.Id is 20 or 21), "a Live consumable that was picked (nothing stands) is never seeded again");

    // The planner itself, with no gather: whatever it is given, a record or a piece of a consumable prefab is not its business.
    int flax = "Pickable_Flax_Wild".GetStableHashCode(), door = "wood_door".GetStableHashCode();
    var raw = new ReconcileInput { Layer = layer, Revision = 5, IsConsumable = h => h == flax };
    raw.Records.Add(new LiveRecord(0, 1, zone, flax, new Vector3(1f, 30f, 1f), Quaternion.identity, unresolved: false));
    raw.Records.Add(new LiveRecord(0, 2, zone, door, new Vector3(5f, 30f, 5f), Quaternion.identity, unresolved: false));
    // A door record whose key a piece of a consumable prefab carries (an entry changed since): the door is seeded, the old piece is left alone.
    raw.Records.Add(new LiveRecord(0, 5, zone, door, new Vector3(7f, 30f, 7f), Quaternion.identity, unresolved: false));
    raw.Pieces.Add(new WorldPiece(new ZDOID(5L, 4), 0, 5, zone, flax, new Vector3(7f, 30f, 7f), Quaternion.identity, false, 1));
    raw.Pieces.Add(new WorldPiece(new ZDOID(5L, 1), 0, 1, zone, flax, new Vector3(9f, 30f, 9f), Quaternion.identity, false, 1));
    raw.Pieces.Add(new WorldPiece(new ZDOID(5L, 2), 0, 3, zone, flax, new Vector3(9f, 30f, 9f), Quaternion.identity, true, 1));
    raw.Pieces.Add(new WorldPiece(new ZDOID(5L, 3), 0, 4, zone, door, new Vector3(9f, 30f, 9f), Quaternion.identity, false, 1));
    var rawPlan = BakedReconcile.Plan(raw);
    C(rawPlan.Items.Count == 3 && rawPlan.Seeds == 2 && rawPlan.Removes == 1 && rawPlan.Items.Where(i => i.Action == ReconcileAction.Seed).Select(i => i.Record!.Id).OrderBy(x => x).SequenceEqual([2u, 5u])
      && rawPlan.Items.Single(i => i.Action == ReconcileAction.Remove).Piece!.BakeId == 4,
      $"given a record and three pieces of a consumable prefab (one holding items) and the doors, it plans the doors only: {rawPlan.Summary()}");
    raw.IsConsumable = _ => false;
    var control = BakedReconcile.Plan(raw);
    C(control.Seeds == 1 && control.Replaces == 2 && control.Removes == 1 && control.Orphans == 1, $"(without the test it would have planned the flax too: {control.Summary()})");
  }

  // ------------------------------------------------------------------------------------------------ what the real placement may do

  private static void BakedConsumableCodeTests()
  {
    Section("baked consumables: the real placement writes nothing on the object (read from its IL)");
    var server = typeof(BakedServer);
    var place = server.GetMethod("PlaceConsumable", Any)!;
    var make = server.GetMethod("Make", Any)!;
    var seed = server.GetMethod("SeedPiece", Any)!;
    var names = new[] { "WriteKeys", "WriteLook", "WriteTag", "WriteValue", "Apply", "RaiseLiveSeeded" };
    bool Writes(MethodBase method) => Callees(method).Any(m => names.Contains(m.Name) || m.DeclaringType == typeof(ZDO) && m.Name.StartsWith("Set", StringComparison.Ordinal) || m.DeclaringType == typeof(ZDOExtraData));
    C(Writes(seed), "(the reading works: SeedPiece writes keys, the look, the protection and raises LiveSeeded)");
    C(!Writes(place) && !Writes(make), "PlaceConsumable and the Make it shares call none of them and set no ZDO value");
    var called = Callees(place).Select(m => m.Name).ToList();
    C(called.Contains("Make") && called.Contains("PlacementOf"), "it makes the object at PlacementOf's pivot, rotation and scale, as a Live piece is");
    // The postfix of PlaceZoneCtrl seeds the zone in one call.
    var postfix = server.GetNestedType("SeedPatch", Any)!.GetMethod("Postfix", Any)!;
    C(Callees(postfix).Any(m => m.Name == "SeedZone"), "and the one postfix of PlaceZoneCtrl that seeds Live records places them (SeedZone)");
  }

  // ------------------------------------------------------------------------------------------------ VALtima's file

  private static void BakedConsumableValtimaTests()
  {
    var layer = ValtimaForSeeding(nameof(BakedConsumableValtimaTests));
    if (layer == null)
      return;
    Section("baked consumables: VALtima's file, with its four pickable kinds: 12,052 records placed once each, 608 Live pieces as before");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    _ = new World();
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
    var game = new ConsumableGame();
    foreach (var name in ConsumableNames)
      game.Has(name, consumable: true);
    game.Install();
    BakedConsumables.SessionStarts();
    var byEntry = Enumerable.Range(0, layer.Palette.Count).ToDictionary(i => i, i => layer.Palette[i].Candidates[0].Name);
    long expected = 0;
    var perName = new Dictionary<string, int>();
    foreach (var row in layer.Zones)
      foreach (var record in layer.Decode(row).Records())
      {
        string first = byEntry[record.Palette];
        if (!ConsumableNames.Contains(first))
          continue;
        expected++;
        perName[first] = perName.GetValueOrDefault(first) + 1;
      }
    C(expected == 12052 && perName["Pickable_Flax_Wild"] == 4080 && perName["Pickable_Dandelion"] == 3992 && perName["Pickable_Mushroom"] == 2884 && perName["RaspberryBush"] == 1096,
      $"the file draws 12,052 of them: {string.Join(", ", perName.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value.ToString("N0")))}");

    int pieces = 0, objects = 0;
    BakedServer.MakePiece = (context, index, palette, record, ghost, spawned) =>
    {
      pieces++;
      return true;
    };
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      objects++;
      return true;
    };
    int made = 0, total = 0;
    foreach (var row in layer.Zones)
    {
      made += BakedServer.SeedZone(row.Key, ghost: false, spawned: null, out int placed);
      total += placed;
    }
    C(made == 608 && pieces == 608, $"the 608 Live pieces are seeded as before ({made})");
    C(total == 12052 && objects == 12052, $"and 12,052 records of consumable kinds are placed ({total})");
    C(BakedConsumables.Kinds.Count == 4 && BakedConsumables.Placed == 12052 && BakedConsumables.Failed == 0, $"four kinds named, none failed ({BakedConsumables.Kinds.Count} kinds, {BakedConsumables.Placed} placed)");

    // The planner is given the Live records only, and never one of a consumable kind.
    var input = new ReconcileInput { Layer = layer, Revision = layer.Revision, IsGenerated = _ => true, IsConsumable = BakedReconcile.ConsumablePrefab };
    int liveRecords = 0;
    var context = new BakedServer.Context(layer);
    foreach (var row in layer.Zones.Where(r => r.Live > 0))
    {
      var data = layer.Decode(row);
      for (int k = 0; k < data.Count; k++)
        if (layer.Palette[data.Palette[k]].Role == BakedRole.Live && !BakedServer.IsConsumable(context, data.Palette[k], layer.Palette[data.Palette[k]], out _))
          liveRecords++;
    }
    C(liveRecords == 608, $"none of VALtima's 608 Live records is a consumable ({liveRecords})");
  }
}

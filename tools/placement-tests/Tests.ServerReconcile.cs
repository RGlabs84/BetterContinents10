// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The reconciliation planner (spec 7.3, 14.1 "Reconciliation planner"): Seed, Keep, Replace, Remove and Orphan, a container that holds items
// never destroyed, a second run that changes nothing, ungenerated zones untouched, and the vegetation that grew. The planner is pure: the
// world's pieces and the layer's records are made here, and the plan is carried out on a model of the world to see what a second plan finds.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static readonly int ForgePrefab = "forge".GetStableHashCode(), ChestPrefab = "piece_chest".GetStableHashCode(), DoorPrefab = "wood_door".GetStableHashCode();

  private static LiveRecord SrvRecord(int source, uint id, int prefab, Vector3 pivot, float yaw = 0f, bool unresolved = false, ZoneKey? zone = null) =>
    new(source, id, zone ?? ZoneKey.OfPoint(pivot), prefab, pivot, BakedFormat.YawRotation(BakedFormat.QuantizeYaw(yaw)), unresolved);

  private static uint srvPieceCounter;

  private static WorldPiece SrvPiece(int source, uint id, int prefab, Vector3 position, float yaw = 0f, bool items = false, long revision = 1) =>
    new(new ZDOID(1000L, ++srvPieceCounter), source, id, ZoneKey.OfPoint(position), prefab, position, BakedFormat.YawRotation(BakedFormat.QuantizeYaw(yaw)), items, revision);

  private static ReconcileInput SrvInput(IEnumerable<LiveRecord> records, IEnumerable<WorldPiece> pieces, Func<ZoneKey, bool> generated = null, uint revision = 2) =>
    new() { Revision = revision, Records = records.ToList(), Pieces = pieces.ToList(), IsGenerated = generated ?? (_ => true) };

  private static IEnumerable<PlanItem> SrvOf(ReconcilePlan plan, ReconcileAction action) => plan.Items.Where(i => i.Action == action);

  public static void ServerReconcileActionsTest()
  {
    Section("reconciliation: seed, keep, replace, remove, orphan");
    var at = new Vector3(100f, 30f, 100f);

    // Seed: the record has no piece, and its zone is generated.
    var plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at)], []));
    C(plan.Seeds == 1 && plan.Items.Count == 1 && plan.Changes == 1, "a record with no piece in a generated zone is seeded");

    // Keep: the same prefab, within 5 cm and 1 degree.
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at, 90f)], [SrvPiece(0, 1, ForgePrefab, at + new Vector3(0.049f, 0f, 0f), 90.9f, revision: 2)]));
    C(plan.Keeps == 1 && plan.Changes == 0, "a piece 4.9 cm and 0.9 degrees off, at the layer's revision, is kept as it is");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at, 90f)], [SrvPiece(0, 1, ForgePrefab, at, 90f, revision: 1)], revision: 2));
    C(plan.Keeps == 1 && plan.Refreshes == 1 && plan.Changes == 1, "a kept piece at an older revision takes the new one (its key is the only change)");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at, 90f)], [SrvPiece(0, 1, ForgePrefab, at, 90f, revision: -1)], revision: 2));
    C(plan.Refreshes == 1, "and one with no revision at all");

    // Replace: it moved, turned, or is another prefab.
    foreach (var (what, piece) in new (string, WorldPiece)[]
    {
      ("5.5 cm off", SrvPiece(0, 1, ForgePrefab, at + new Vector3(0.055f, 0f, 0f), 90f)),
      ("2 degrees round", SrvPiece(0, 1, ForgePrefab, at, 92f)),
      ("another prefab", SrvPiece(0, 1, ChestPrefab, at, 90f)),
      ("a metre up", SrvPiece(0, 1, ForgePrefab, at + Vector3.up, 90f)),
    })
    {
      plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at, 90f)], [piece]));
      C(plan.Replaces == 1 && plan.Items.Count == 1 && plan.Items[0].Piece == piece && plan.Items[0].Record != null, $"a piece {what} is replaced: removed, and the record seeded");
    }

    // Remove: the layer no longer has the record.
    plan = BakedReconcile.Plan(SrvInput([], [SrvPiece(0, 5, ForgePrefab, at)]));
    C(plan.Removes == 1 && plan.Items.Count == 1, "a piece whose record is gone is removed");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, at)], [SrvPiece(0, 1, ForgePrefab, at, revision: 2), SrvPiece(3, 1, ForgePrefab, at, revision: 2)]));
    C(plan.Keeps == 1 && plan.Removes == 1, "(source, id) is the identity: the same id of another bake is another piece, and goes");

    // Orphan: a container that holds items is never destroyed.
    plan = BakedReconcile.Plan(SrvInput([], [SrvPiece(0, 5, ChestPrefab, at, items: true)]));
    C(plan.Orphans == 1 && plan.Removes == 0 && plan.Items[0].Record == null, "a chest with items whose record is gone is an orphan, not destroyed");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 5, ChestPrefab, at + new Vector3(3f, 0f, 0f))], [SrvPiece(0, 5, ChestPrefab, at, items: true)]));
    C(plan.Orphans == 1 && plan.Items[0].Record != null && plan.Replaces == 0 && plan.Removes == 0, "one that moved is an orphan where it stands, and the record is seeded where it should be");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 5, ChestPrefab, at + new Vector3(3f, 0f, 0f))], [SrvPiece(0, 5, ChestPrefab, at, items: false)]));
    C(plan.Replaces == 1 && plan.Orphans == 0, "an empty one moves like any other piece");

    // Duplicates under one key: one is kept, the others go (or stay as orphans with items).
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, DoorPrefab, at)], [SrvPiece(0, 1, DoorPrefab, at + Vector3.right * 4f, revision: 2), SrvPiece(0, 1, DoorPrefab, at, revision: 2), SrvPiece(0, 1, DoorPrefab, at, revision: 2, items: true)]));
    C(plan.Keeps == 1 && plan.Removes == 1 && plan.Orphans == 1 && plan.Items.Count == 3, "pieces that share a key: the one that matches stays, the others go, and one that holds items stays as an orphan");

    // A record this game cannot make leaves what stands under its key alone.
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, 0, at, unresolved: true)], [SrvPiece(0, 1, ForgePrefab, at + Vector3.up * 9f)]));
    C(plan.Items.Count == 0, "a record whose prefab this game lacks is neither seeded nor judged against what stands");
  }

  public static void ServerReconcileZonesTest()
  {
    Section("reconciliation: zones the world has not generated are left to seeding");
    var generated = new ZoneKey(1, 1);
    var bare = new ZoneKey(5, 5);
    Vector3 Where(ZoneKey zone) => new(zone.X * 64f + 5f, 30f, zone.Z * 64f + 5f);
    bool Generated(ZoneKey zone) => zone == generated;
    var plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, Where(bare)), SrvRecord(0, 2, ForgePrefab, Where(generated))], [], Generated));
    C(plan.Seeds == 1 && plan.Items[0].Record!.Id == 2, "a record in a zone that is not generated is not seeded; one in a generated zone is");
    // A piece of a record that moved into an ungenerated zone goes, and nothing is seeded there: seeding makes it when the zone generates.
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, Where(bare))], [SrvPiece(0, 1, ForgePrefab, Where(generated))], Generated));
    C(plan.Removes == 1 && plan.Seeds == 0 && plan.Replaces == 0, "a piece whose record moved into a zone nobody has generated goes, and the new place is left to seeding");
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ChestPrefab, Where(bare))], [SrvPiece(0, 1, ChestPrefab, Where(generated), items: true)], Generated));
    C(plan.Orphans == 1 && plan.Items[0].Record == null, "and a chest with items in it stays where it is as an orphan, not made again until its new zone generates");
    // A piece that stands where its record says, though the record's zone is not generated (a piece made by hand): kept.
    plan = BakedReconcile.Plan(SrvInput([SrvRecord(0, 1, ForgePrefab, Where(bare))], [SrvPiece(0, 1, ForgePrefab, Where(bare), revision: 2)], Generated));
    C(plan.Keeps == 1 && plan.Changes == 0, "a piece that stands as its record says is kept whatever the zone");
  }

  // A model of the world to carry a plan out on: the pieces that stand, as the executor would leave them.
  private static List<WorldPiece> SrvCarry(ReconcilePlan plan, List<WorldPiece> pieces, ReconcileInput input)
  {
    var world = new List<WorldPiece>(pieces);
    foreach (var item in plan.Items)
    {
      void Seed() => world.Add(new WorldPiece(new ZDOID(1000L, ++srvPieceCounter), item.Record!.Source, item.Record.Id, item.Record.Zone, item.Record.Prefab, item.Record.Pivot, item.Record.Rotation, false, plan.Revision));
      switch (item.Action)
      {
        case ReconcileAction.Seed:
          Seed();
          break;
        case ReconcileAction.Keep:
          if (item.Refresh)
          {
            world.Remove(item.Piece!);
            world.Add(new WorldPiece(item.Piece!.Uid, item.Piece.Source, item.Piece.BakeId, item.Piece.Zone, item.Piece.Prefab, item.Piece.Position, item.Piece.Rotation, item.Piece.HoldsItems, plan.Revision));
          }
          break;
        case ReconcileAction.Replace:
          world.Remove(item.Piece!);
          Seed();
          break;
        case ReconcileAction.Remove:
          world.Remove(item.Piece!);
          break;
        default:
          // An orphan loses its bake keys: it is no longer a piece of the layer, and the model forgets it.
          world.Remove(item.Piece!);
          if (item.Record != null)
            Seed();
          break;
      }
    }
    return world;
  }

  public static void ServerReconcileIdempotentTest()
  {
    Section("reconciliation: a second plan over the same layer changes nothing");
    var random = new System.Random(5);
    var records = new List<LiveRecord>();
    var pieces = new List<WorldPiece>();
    for (uint id = 1; id <= 400; id++)
    {
      var at = new Vector3(random.Next(-500, 500), 30f, random.Next(-500, 500));
      int prefab = id % 3 == 0 ? ChestPrefab : id % 3 == 1 ? ForgePrefab : DoorPrefab;
      int source = id % 5 == 0 ? 2 : 0;
      float yaw = random.Next(0, 360);
      records.Add(SrvRecord(source, id, prefab, at, yaw));
      // What stands: nothing, the same, moved, turned, another prefab, or it holds items, by the id.
      switch (id % 7)
      {
        case 0: break;
        case 1:
        case 2: pieces.Add(SrvPiece(source, id, prefab, at, yaw, revision: 2)); break;
        case 3: pieces.Add(SrvPiece(source, id, prefab, at + new Vector3(0.5f, 0f, 0f), yaw)); break;
        case 4: pieces.Add(SrvPiece(source, id, prefab, at, yaw + 33f)); break;
        case 5: pieces.Add(SrvPiece(source, id, ChestPrefab, at, 0f, items: true)); break;
        default: pieces.Add(SrvPiece(source, id, prefab, at, 0f, items: id % 2 == 0)); break;
      }
    }
    // Pieces the layer never had.
    for (uint id = 1000; id < 1040; id++)
      pieces.Add(SrvPiece(0, id, ForgePrefab, new Vector3(id, 30f, id), items: id % 4 == 0));
    var input = SrvInput(records, pieces);
    var first = BakedReconcile.Plan(input);
    C(first.Seeds > 0 && first.Replaces > 0 && first.Removes > 0 && first.Orphans > 0 && first.Keeps > 0 && first.Changes > 100,
      $"the first plan does a bit of everything: {first.Seeds} seeded, {first.Replaces} replaced, {first.Removes} removed, {first.Orphans} orphaned, {first.Keeps} kept");
    var after = SrvCarry(first, pieces, input);
    var again = BakedReconcile.Plan(SrvInput(records, after));
    C(again.Changes == 0 && again.Seeds == 0 && again.Replaces == 0 && again.Removes == 0 && again.Orphans == 0, $"the plan made over the world it left changes nothing ({again.Changes})");
    C(again.Keeps == records.Count, $"every record has its piece, kept ({again.Keeps} of {records.Count})");
    // And the same input gives the same plan.
    var twin = BakedReconcile.Plan(input);
    C(twin.Items.Select(i => i.ToString()).SequenceEqual(first.Items.Select(i => i.ToString())), "the same input gives the same plan");
    // A container that holds items is in no plan as a Remove or a Replace.
    C(first.Items.Where(i => i.Piece != null && i.Piece.HoldsItems).All(i => i.Action is ReconcileAction.Orphan or ReconcileAction.Keep),
      "no piece that holds items is ever planned for destruction");
  }

  public static void ServerReconcileVegetationTest()
  {
    Section("reconciliation: the vegetation where the clear cells grew");
    var zone = new ZoneKey(2, 2);
    var bits = new byte[32];
    bits[0] = 1;
    var input = SrvInput([], []);
    input.Growth.Add(new VegetationGrowth(zone, new BakedVegetation.ZoneMask(false, bits)));
    input.Growth.Add(new VegetationGrowth(new ZoneKey(9, 9), BakedVegetation.ZoneMask.Everywhere));
    input.Growth.Add(new VegetationGrowth(new ZoneKey(8, 8), BakedVegetation.ZoneMask.None));
    input.IsGenerated = z => z != new ZoneKey(9, 9);
    var plan = BakedReconcile.Plan(input);
    C(plan.Vegetation.Count == 1 && plan.Vegetation[0].Zone == zone && plan.Changes == 1, "growth in a generated zone is planned; growth of none, and growth in a zone nobody generated, are not");
  }

  public static void ServerReconcileItemsTest()
  {
    Section("reconciliation: what a container holds");
    ZPackage Inventory(int version, int count, bool wide)
    {
      var pkg = new ZPackage();
      pkg.Write(version);
      if (wide)
        pkg.Write((ushort)count);
      else
        pkg.Write(count);
      for (int i = 0; i < count; i++)
        pkg.Write("Stone");
      return pkg;
    }
    var fake = new FakeZdos();
    using (fake)
    {
      bool Holds(byte[] items, int itemStand = 0)
      {
        var zdo = fake.New("piece_chest");
        if (items != null)
          zdo.Set("items".GetStableHashCode(), items);
        if (itemStand != 0)
          zdo.Set("item".GetStableHashCode(), itemStand);
        return BakedReconcile.HoldsItems(zdo);
      }
      C(!Holds(null), "a container with nothing saved holds nothing");
      C(!Holds(Inventory(109, 0, wide: true).GetArray()), "an inventory saved with no items holds nothing");
      C(Holds(Inventory(109, 3, wide: true).GetArray()), "one with items holds items");
      C(!Holds(Inventory(107, 0, wide: false).GetArray()) && Holds(Inventory(107, 2, wide: false).GetArray()), "an older inventory counts its items in an int");
      C(Holds([1, 2]), "bytes nobody can read count as holding something: nothing is destroyed on a guess");
      C(!Holds([]) && Holds(null, "sword".GetStableHashCode()), "an item stand holds its item, and an empty array holds nothing");

      // The stations that hold what a player put in them, without an inventory of the game's.
      bool Has(Action<ZDO> set)
      {
        var zdo = fake.New("piece_station");
        set(zdo);
        return BakedReconcile.HoldsItems(zdo);
      }
      C(!Has(_ => { }), "a piece with nothing saved holds nothing");
      C(Has(z => z.Set("2_item".GetStableHashCode(), "ArmorBronzeChest".GetStableHashCode())) && Has(z => z.Set("0_item".GetStableHashCode(), 5)) && Has(z => z.Set("15_item".GetStableHashCode(), 5)),
        "an armor stand holds what is on any of its slots (0_item ... 15_item is the hash of the item's prefab)");
      C(!Has(z => z.Set("0_item".GetStableHashCode(), 0)) && !Has(z => z.Set("16_item".GetStableHashCode(), 5)) && !Has(z => z.Set("0_variant".GetStableHashCode(), 3)) && !Has(z => z.Set("pose".GetStableHashCode(), 2)),
        "an empty slot, a slot past the sixteenth, a variant and a pose are not items");
      C(Has(z => z.Set(ZDOVars.s_queued, 3)) && !Has(z => z.Set(ZDOVars.s_queued, 0)), "a smelter, kiln or furnace holds the ore in its queue");
      C(Has(z => z.Set(ZDOVars.s_spawnOre, "CopperOre")) && !Has(z => z.Set(ZDOVars.s_spawnOre, "")), "and what it made and has not given out");
      C(!Has(z => z.Set(ZDOVars.s_fuel, 12.5f)) && !Has(z => z.Set(ZDOVars.s_fuel, 0f)), "fuel is not an item: every fire and torch would otherwise be an orphan at every change");
      C(Has(z => z.Set(ZDOVars.s_content, "Mead".GetStableHashCode())) && !Has(z => z.Set(ZDOVars.s_content, 0)), "a fermenter holds its content");
      C(Has(z => z.Set("slot0", "RawMeat")) && Has(z => z.Set("slot15", "Fish")) && !Has(z => z.Set("slot3", "")) && !Has(z => z.Set("slot16", "Fish")) && !Has(z => z.Set("slotstatus0", 2)),
        "a cooking station holds the food on any of its slots; an empty slot, a seventeenth and a status are not food");
      C(Has(z => z.Set(ZDOVars.s_ammo, 7)) && !Has(z => z.Set(ZDOVars.s_ammo, 0)) && !Has(z => z.Set(ZDOVars.s_ammoType, "arrow_wood")), "a turret holds its ammunition, and its ammunition type alone is not ammunition");
      var several = fake.New("piece_chest");
      several.Set(ZDOVars.s_items, Inventory(109, 0, wide: true).GetArray());
      several.Set(ZDOVars.s_queued, 0);
      C(!BakedReconcile.HoldsItems(several), "an empty inventory and an empty queue together hold nothing");
    }
  }
}

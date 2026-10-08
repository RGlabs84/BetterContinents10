// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Consumables are never baked (build spec 0.2): a prefab with a Pickable, PickableItem, ItemDrop or Plant anywhere in its hierarchy is not touched
// by a bake, with or without 'town' and 'any', and an unbake leaves the records of such kinds alone. Stations that give items and stay are not
// consumables.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static void BakeConsumablesTest()
  {
    Section("consumables: never baked, never adopted (spec 0.2)");
    C(BakeClassifier.ConsumableComponents.SequenceEqual(["Pickable", "PickableItem", "ItemDrop", "Plant"]), "the four names of the spec");
    var everything = new BakeWords { Town = true, Any = true };
    foreach (var component in BakeClassifier.ConsumableComponents)
    {
      var prefab = BakePrefab("piece_" + component, component);
      foreach (var (words, what) in new[] { (default(BakeWords), "plain"), (new BakeWords { Town = true }, "town"), (new BakeWords { Any = true }, "any"), (everything, "town any") })
        foreach (long creator in new long[] { 7, 0 })
        {
          var cls = BakeClassifier.Classify(prefab, creator, words);
          C(cls.Kind == BakeKind.Consumable && cls.Group == BakeClassifier.Consumables && cls.Component == component && cls.Untouched && !cls.Baked && !cls.StaysReal,
            $"a Piece with {component} ({what}, creator {creator}) is not touched: gives items when used (got {cls})");
        }
    }
    C(BakeClassifier.Consumables == "gives items when used", "the group is named as the user's words");
    var plant = BakeClassifier.Classify(BakePrefab("sapling", "Plant", "Door", "Container"), 7, everything);
    C(plant.Kind == BakeKind.Consumable, "a consumable comes before the rules for what stays: a Plant that is also a container is left alone, not adopted");
    var hearth = BakeClassifier.Classify(BakePrefab("hearth_bush", "Fireplace", "Pickable"), 7, everything);
    C(hearth.Kind == BakeKind.Consumable, "a modded piece that is also Pickable is not adopted as a Live record");
    var mod = new PrefabFacts("mod_berry", [new ComponentFact("Piece"), new ComponentFact("WearNTear"), new ComponentFact("ZNetView"), new ComponentFact("MyMod.BerryBush", "Pickable")]);
    C(BakeClassifier.Classify(mod, 7, everything).Kind == BakeKind.Consumable, "a mod's subclass of Pickable counts");
    var lookalike = new PrefabFacts("mod_plant", [new ComponentFact("Piece"), new ComponentFact("WearNTear"), new ComponentFact("ZNetView"), new ComponentFact("MyMod.Plant")]);
    C(BakeClassifier.Classify(lookalike, 7, default).Kind == BakeKind.Stays && BakeClassifier.Classify(lookalike, 7, everything).Kind == BakeKind.Adopt,
      "a mod's own class that is merely called Plant is not the game's: it stays real (adopted with 'town') as any unknown component does");
    C(BakeClassifier.Classify(BakePrefab("wall", "TreeBase"), 7, default).Kind == BakeKind.Stays, "a tree's component is not a consumable's (it is unknown to StaticSafe, so it stays real)");

    Section("consumables: a Beehive is a station, and stays or is adopted as before");
    var hive = BakePrefab("beehive", "Beehive");
    var plain = BakeClassifier.Classify(hive, 7, default);
    var town = BakeClassifier.Classify(hive, 7, new BakeWords { Town = true });
    C(plain.Kind == BakeKind.Stays && plain.Group == BakeClassifier.Stations && town.Kind == BakeKind.Adopt && town.Group == BakeClassifier.Stations, "a Beehive stays real, and 'town' adopts it, in the stations group");
    foreach (var station in new[] { "SapCollector", "Fermenter", "CookingStation", "Smelter", "CraftingStation", "Container" })
      C(BakeClassifier.Classify(BakePrefab("s_" + station, station), 7, everything).Kind == BakeKind.Adopt, $"{station} is not a consumable: 'town' adopts it");

    Section("consumables: the dry run names them, and the bake takes nothing of them");
    BakeRunner.Reset();
    var fx = BakeNewFx("consumables", world =>
    {
      var taken = BakeStandardWorld(world);
      foreach (var facts in new[]
      {
        new PrefabFacts("piece_berry", [.. BakeSafeParts, "Pickable"], height: 1f),
        new PrefabFacts("piece_sapling", [.. BakeSafeParts, "Plant"], height: 1f),
        new PrefabFacts("piece_lamp_item", [.. BakeSafeParts, "ItemDrop"], height: 1f),
        new PrefabFacts("piece_beehive", [.. BakeSafeParts, "Beehive"], height: 1f),
      })
        world.Facts[facts.Name.GetStableHashCode()] = facts;
      world.Add(BakeObject("piece_berry", 6f, 10f, 9f));
      world.Add(BakeObject("piece_berry", 8f, 10f, 9f, 0f, 0));
      world.Add(BakeObject("piece_sapling", 10f, 10f, 9f));
      world.Add(BakeObject("piece_lamp_item", 12f, 10f, 9f));
      world.Add(BakeObject("piece_beehive", 14f, 10f, 9f));
      return taken;
    }, out var standing);
    int objects = fx.World.Objects.Count;
    BakeRun(fx, BakeStandardArea(), "town any");
    var text = string.Join("\n", fx.Said);
    C(text.Contains("gives items when used 4"), "the dry run counts the four it leaves alone: " + text);
    C(text.Contains("Town pieces: 3 (") && text.Contains("stations 1"), "and 'town' still adopts the door, the chest and the beehive: " + text);
    fx.Said.Clear();
    BakeRun(fx, BakeStandardArea(), "town any confirm");
    C(fx.World.Objects.Values.Count(o => o.PrefabName is "piece_berry" or "piece_sapling" or "piece_lamp_item") == 4, "all four are objects still");
    C(fx.World.Objects.Values.Where(o => o.PrefabName is "piece_berry" or "piece_sapling" or "piece_lamp_item").All(o => !BakeHasAnyKey(o)), "with no key: none adopted");
    C(BakeHasKeys(fx.World.Objects.Values.First(o => o.PrefabName == "piece_beehive")), "the beehive is adopted");
    C(BakeRecordsOf(fx).All(r => r.Prefab is not ("piece_berry" or "piece_sapling" or "piece_lamp_item")), "no record of a consumable");
  }

  private static void BakeUnbakeConsumablesTest()
  {
    Section("consumables: an unbake leaves the records of a consumable kind in the layer");
    BakeRunner.Reset();
    // A compiler's layer with flax records (a Static and a Live one) beside walls.
    var flax = Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None);
    var flaxLive = Entry("Pickable_Flax_Wild", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect);
    var wall = Entry("stone_wall", BakedRole.Static, BakedCollision.Prefab);
    var edit = LayerEdit.New("compiler");
    int iFlax = edit.PaletteIndexFor(flax), iLive = edit.PaletteIndexFor(flaxLive), iWall = edit.PaletteIndexFor(wall);
    var zone = new ZoneKey(3, 3);
    for (int i = 0; i < 5; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(iFlax, zone.OriginX + 3 + i, 30, zone.OriginZ + 5, 0)], 1f);
    edit.AddRecords([ZoneRecord.CreateYaw(iLive, zone.OriginX + 20, 30, zone.OriginZ + 5, 0, null, 900u)], 1f);
    for (int i = 0; i < 3; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(iWall, zone.OriginX + 3 + i, 30, zone.OriginZ + 9, 0)], 3f);
    var layer = edit.Build().Layer;
    var fx = BakeNewFx("unbake-consumables", world =>
    {
      world.Facts["stone_wall".GetStableHashCode()] = new PrefabFacts("stone_wall", BakeSafeParts, height: 3f);
      world.Facts["Pickable_Flax_Wild".GetStableHashCode()] = new PrefabFacts("Pickable_Flax_Wild", ["Pickable", "ZNetView"], height: 1f);
      return [];
    }, out _, layer);
    BakeUnbakeRun(fx, BakeWholeWorld, "all", "unbake world all");
    var text = string.Join("\n", fx.Said);
    C(text.Contains("Left alone: 6 records of kinds that give items when used.") && text.Contains("To unbake: 3 pieces (stone_wall 3)."), "the dry run names them, and takes the walls only: " + text);
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "all confirm", "unbake world all");
    C(fx.World.Objects.Values.Count(o => o.PrefabName == "stone_wall") == 3 && fx.World.Objects.Values.All(o => o.PrefabName != "Pickable_Flax_Wild"), "three walls are made and no flax: the game placed that when the zone generated");
    C(fx.Layer.Placements == 6 && BakeRecordsOf(fx).All(r => r.Prefab == "Pickable_Flax_Wild"), "the six records of the flax stay in the layer, the Live one too");
  }
}

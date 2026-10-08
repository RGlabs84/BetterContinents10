// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Consumables are never baked (build spec 0.2): the one test every slice shares (BakedConsumables), on made-up prefabs. A prefab is the list of
// component types its hierarchy has; the offline suite has no components to read, so the rule is run over the game's own types.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

// A mod's own types: a class derived from a consumable's component, and a class that is only called Plant.
internal sealed class ModdedPickable : Pickable { }

internal static partial class Tests
{
  // A made-up prefab: the components of its whole hierarchy (the root's and its children's alike).
  private static IEnumerable<Type> ConsumablePrefab(params Type[] components) => new[] { typeof(ZNetView), typeof(Piece) }.Concat(components);

  public static void ConsumablesRuleTest()
  {
    Section("consumables: a prefab with a Pickable, a PickableItem, an ItemDrop or a Plant is one; a station, a tree, a rock is not");
    C(BakedConsumables.Components.SequenceEqual(["Pickable", "PickableItem", "ItemDrop", "Plant"]), "the shared list names Pickable, PickableItem, ItemDrop and Plant (slice D's PrefabFacts reads it)");
    // Each name is a type of the game, in the global namespace: a typo in the list would make a kind a consumable no prefab ever is.
    var types = new[] { typeof(Pickable), typeof(PickableItem), typeof(ItemDrop), typeof(Plant) };
    C(types.Select(t => t.FullName).SequenceEqual(BakedConsumables.Components), "each name is the full name of a type of the game");

    foreach (var type in types)
    {
      C(BakedConsumables.IsAny(ConsumablePrefab(type)), $"a prefab with a {type.Name} alone is a consumable");
      C(BakedConsumables.IsComponent(type), $"{type.Name} is a consumable's component");
    }
    C(BakedConsumables.IsAny(ConsumablePrefab(typeof(Pickable), typeof(ItemDrop), typeof(Plant))), "and with several of them");
    // The component may be on a child (the whole hierarchy is read): the list is flat, whatever object each came from.
    C(BakedConsumables.IsAny([typeof(Transform), typeof(MeshRenderer), typeof(ZNetView), typeof(Pickable), typeof(Collider)]), "a Pickable among a prefab's other components is found");

    // Stations that give items and stay are not consumables.
    foreach (var station in new[] { typeof(Beehive), typeof(SapCollector), typeof(Fermenter), typeof(CookingStation), typeof(Container), typeof(Smelter) })
    {
      C(!BakedConsumables.IsAny(ConsumablePrefab(station)), $"a {station.Name} station is not a consumable");
      C(!BakedConsumables.IsComponent(station), $"{station.Name} is not a consumable's component");
    }
    C(!BakedConsumables.IsAny(ConsumablePrefab(typeof(Beehive), typeof(WearNTear), typeof(Destructible))), "a Beehive with its WearNTear and Destructible is not one");

    // Trees and rocks stay decoration.
    C(!BakedConsumables.IsAny([typeof(ZNetView), typeof(TreeBase), typeof(Destructible), typeof(LODGroup), typeof(MeshRenderer)]), "a tree (TreeBase) is not a consumable");
    C(!BakedConsumables.IsAny([typeof(ZNetView), typeof(MineRock), typeof(Destructible)]) && !BakedConsumables.IsAny([typeof(ZNetView), typeof(MineRock5)]), "a rock (MineRock, MineRock5) is not one");
    C(!BakedConsumables.IsAny([typeof(Transform), typeof(MeshRenderer), typeof(MeshFilter), typeof(BoxCollider)]), "and a bare mesh is not");
    C(!BakedConsumables.IsAny([]) && !BakedConsumables.IsComponent(null), "no components at all: not a consumable");

    // A mod's pickable is a pickable all the same; a class that is only called Plant, in a namespace of its own, is not the game's.
    C(BakedConsumables.IsAny(ConsumablePrefab(typeof(ModdedPickable))), "a class derived from Pickable is a consumable's component");
    C(!BakedConsumables.IsAny(ConsumablePrefab(typeof(SomeMod.Plant))), "a class called Plant in a mod's namespace is not the game's Plant");
  }

  public static void ConsumablesPrefabTest()
  {
    Section("consumables: Is(GameObject) asks the prefab once, and a prefab that is not there is not one");
    var probe = BakedConsumables.Probe;
    try
    {
      var flax = ApiAlive<GameObject>();
      var hive = ApiAlive<GameObject>();
      var asked = new List<GameObject>();
      BakedConsumables.Probe = prefab =>
      {
        asked.Add(prefab);
        return ReferenceEquals(prefab, flax);
      };
      C(BakedConsumables.Is(flax) && !BakedConsumables.Is(hive), "Is answers for a prefab as the probe reads it");
      C(asked.Count == 2 && ReferenceEquals(asked[0], flax) && ReferenceEquals(asked[1], hive), "and asks it about the prefab itself");
      C(!BakedConsumables.Is(null), "no prefab is not a consumable");
      var gone = (GameObject)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
      asked.Clear();
      C(!BakedConsumables.Is(gone) && asked.Count == 0, "a prefab the game has destroyed is not asked about, and is not one");
    }
    finally
    {
      BakedConsumables.Probe = probe;
    }
  }

  // ------------------------------------------------------------------------------------------------ the client

  public static void ConsumablesClientTest()
  {
    Section("consumables, client: an entry is a consumable by the prefab it resolves to, and a zone builds no instance and no collider for one");
    var probe = BakedConsumables.Probe;
    using var scene = new ApiScene();
    try
    {
      foreach (var name in new[] { "Pickable_Flax_Wild", "Pickable_Mushroom", "Bush01", "wood_door", "Pine" })
        scene.Add(name);
      GameObject Prefab(string name) => scene.Prefabs[name.GetStableHashCode()];
      var consumables = new[] { Prefab("Pickable_Flax_Wild"), Prefab("Pickable_Mushroom") };
      BakedConsumables.Probe = prefab => consumables.Any(c => ReferenceEquals(c, prefab));
      EntryDef Def(BakedRole role, params string[] names) => new() { Names = names, Anchors = names.Select(_ => Vector3.zero).ToArray(), Role = role };

      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "Pickable_Flax_Wild")) == "Pickable_Flax_Wild", "a Static entry of a pickable is a consumable");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Live, "Pickable_Mushroom")) == "Pickable_Mushroom" && BakedKinds.ConsumableOf(Def(BakedRole.Copy, "Pickable_Mushroom")) != null, "whatever its role");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "Pine")) == null && BakedKinds.ConsumableOf(Def(BakedRole.Live, "wood_door")) == null, "a tree and a door are not");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "RaspberryBush", "Bush01")) == null, "RaspberryBush missing, its stand-in Bush01 is not a consumable: the entry is not one");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "Missing_Mod_Berry", "Pickable_Flax_Wild")) == "Pickable_Flax_Wild", "a missing first candidate whose stand-in is a pickable is one, by the stand-in");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "Pine", "Pickable_Flax_Wild")) == null, "and a pickable that is only the stand-in of a prefab the game has is not looked at: the first candidate the game has decides");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, "Missing_Mod_Berry")) == null && BakedKinds.ConsumableOf(Def(BakedRole.Static)) == null, "an entry the game has no prefab of is not one");

      // A kind that would be drawn, collided with, copied and sat on is left out of the zone whole.
      BakedConsumables.SessionStarts();
      var piece = ClientKit.NoLods("flax", 1f);
      var flax = ClientKit.Kind("Pickable_Flax_Wild", piece, collision: BakedCollision.Boxes, boxes: [new KindBox(Vector3.zero, Vector3.one)]);
      var copy = ClientKit.Kind("Pickable_Mushroom", piece, role: BakedRole.Copy, collision: BakedCollision.Boxes, boxes: [new KindBox(Vector3.zero, Vector3.one)]);
      var live = ClientKit.Kind("Pickable_Mushroom", null, role: BakedRole.Live);
      var wall = ClientKit.Kind("stone_wall", ClientKit.NoLods("wall", 2f), collision: BakedCollision.Boxes, boxes: [new KindBox(Vector3.zero, Vector3.one)]);
      var records = new[] { (0, 4.0, 5.0, 6.0, 0.0, (Vector3?)null), (0, 8.0, 5.0, 6.0, 20.0, null), (1, 10.0, 5.0, 6.0, 0.0, null), (2, 12.0, 5.0, 6.0, 0.0, null), (3, 14.0, 5.0, 6.0, 0.0, null) };
      var kinds = new[] { flax, copy, live, wall };
      var before = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), kinds, 1);
      C(before.Drawn == 3 && before.Consumables == 0 && before.Colliders.Length == 1 && before.Colliders[0].Triangles.Length == 4 * 36, "(without the flag the flax is drawn, and all four boxes would be collided with)");
      foreach (var kind in new[] { flax, copy, live })
        kind.Consumable = true;
      var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), kinds, 1);
      C(zone.Consumables == 4 && zone.Drawn == 1 && zone.Kinds.Length == 1 && zone.Kinds[0].Kind == wall && zone.Kinds[0].Count == 1,
        $"no instance is built for a consumable: only the wall is drawn ({zone.Drawn} drawn, {zone.Consumables} consumables)");
      C(zone.Colliders.Length == 1 && zone.Colliders[0].Triangles.Length == 36 && zone.Copies.Length == 0 && zone.Seats.Length == 0 && zone.Convex.Length == 0 && zone.Skipped == 0,
        "no collider (the only box is the wall's), no copy, no seat, nothing skipped");
    }
    finally
    {
      BakedConsumables.Probe = probe;
    }
  }

  public static void ConsumablesStatsTest()
  {
    Section("consumables, client: bc_bake stats says how many kinds and records are placed as real objects");
    var layer = MakeSample(91).Layer;
    var rig = new ClientLayerRig();
    BakedLayerStore.Changed += BakedClient.OnLayerChanged;
    try
    {
      using var scene = new ApiScene();
      BakedConsumables.SessionStarts();
      BakedLayerStore.Set(layer, null);
      BakedClient.Prepare();
      var lines = new List<string>();
      BakedClient.Stats(lines.Add);
      C(!lines.Any(l => l.Contains("real objects")), "with none in view the stats do not mention them: " + string.Join(" | ", lines));
      BakedConsumables.Note("Pickable_Flax_Wild");
      BakedConsumables.Note("Pickable_Mushroom");
      var zone = BakedClient.SlotOf(new ZoneKey(0, 0));
      zone.Current = BuiltZone.Empty(0, 0, layer.Revision);
      zone.Current.Consumables = 41;
      var other = BakedClient.SlotOf(new ZoneKey(1, 0));
      other.Current = BuiltZone.Empty(1, 0, layer.Revision);
      other.Current.Consumables = 1;
      lines.Clear();
      BakedClient.Stats(lines.Add);
      C(lines.Any(l => l.Contains("2 consumable kinds") && l.Contains("Pickable_Flax_Wild") && l.Contains("42") && l.Contains("placed as real objects, never drawn")), "the stats name the kinds and count the records of the zones built: " + string.Join(" | ", lines));
    }
    finally
    {
      BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
      rig.Dispose();
      BakedConsumables.SessionStarts();
    }
  }

  public static void ConsumablesInfoTest()
  {
    Section("consumables, report: the layer's consumable kinds and records, and what the session placed (bc_bake info)");
    var layer = ServerValtima(nameof(ConsumablesInfoTest));
    if (layer == null)
      return;
    var (find, probe, view, placed, failed) = (BakedServer.FindPrefab, BakedConsumables.Probe, BakedServer.ViewOf, BakedConsumables.Placed, BakedConsumables.Failed);
    try
    {
      var prefabs = new Dictionary<string, GameObject>();
      foreach (var name in new[] { "Pickable_Flax_Wild", "Pickable_Dandelion", "Pickable_Mushroom", "RaspberryBush" })
        prefabs[name] = ApiAlive<GameObject>();
      BakedServer.FindPrefab = name => prefabs.TryGetValue(name, out var prefab) ? prefab : null;
      BakedConsumables.Probe = prefab => prefabs.Values.Any(p => ReferenceEquals(p, prefab));
      BakedServer.ViewOf = _ => true;
      BakedConsumables.SessionStarts();
      var lines = BakedConsumables.InfoLines(layer);
      C(lines.Count == 2 && lines[0].Contains("4 kinds, 12,052 records (0 of them marked Live"), "VALtima's file has four consumable kinds and 12,052 records, none of them Live: " + lines[0]);
      C(lines[0].Contains("Pickable_Flax_Wild 4,080") && lines[0].Contains("Pickable_Dandelion 3,992") && lines[0].Contains("Pickable_Mushroom 2,884") && lines[0].Contains("RaspberryBush 1,096"), "with the count of each kind");
      C(lines[1] == "Consumables this session: 0 placed, 0 could not be placed.", "and what the session placed: " + lines[1]);
      BakedConsumables.Placed = 11;
      BakedConsumables.Failed = 2;
      C(BakedConsumables.InfoLines(layer)[1] == "Consumables this session: 11 placed, 2 could not be placed.", "the session's counts follow");
      BakedServer.FindPrefab = _ => null;
      var none = BakedConsumables.InfoLines(layer);
      C(none.Count == 1 && none[0].StartsWith("Consumables: none"), "in a game that has none of the prefabs it says there are none: " + none[0]);
    }
    finally
    {
      (BakedServer.FindPrefab, BakedConsumables.Probe, BakedServer.ViewOf) = (find, probe, view);
      BakedConsumables.Placed = placed;
      BakedConsumables.Failed = failed;
    }
  }
}

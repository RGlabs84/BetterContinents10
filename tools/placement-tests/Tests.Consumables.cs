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
}

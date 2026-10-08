// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterContinents;

// CONSUMABLES ARE NEVER BAKED (build spec 0.2, the user's decision of 2026-10-07 evening).
//
// A consumable kind is a prefab that a player picks, picks up or harvests for items: it has a Pickable, a PickableItem, an ItemDrop or a Plant
// anywhere in its hierarchy. The game owns such an object from the moment it is placed (picked, regrown, rotting, destroyed as the world's own
// vegetation is), so a layer never draws one and never keeps one alive:
//   - the compiler's file: every record of a consumable kind, whatever its role, is placed ONCE as an ordinary object when its zone generates
//     (BakedServer.SeedZone), with no bake keys, no bc_protect and no creator; reconciliation neither seeds nor removes one (BakedReconcile);
//   - the client never draws one and gives it no baked collider (BakedKinds, BakedZoneBuild);
//   - the in-game bake (slice D) never bakes and never adopts one; it reads Components on its PrefabFacts.
// Trees and rocks (TreeBase, MineRock, Destructible) are not consumables: they stay decoration where a layer draws them. Stations that give items
// and stay (Beehive, SapCollector, Fermenter, CookingStation, ...) are not either: they are Live or stay real, and HoldsItems keeps what is in them.
internal static class BakedConsumables
{
  /// <summary>The components that make a prefab a consumable kind. The in-game bake's PrefabFacts tests the same names.</summary>
  internal static readonly string[] Components = ["Pickable", "PickableItem", "ItemDrop", "Plant"];

  /// <summary>Whether one of these component types is a consumable's (the type, or a type it derives from, is named in <see cref="Components"/>;
  /// the game's types are in the global namespace, so a mod's own class that happens to be called Plant is not).</summary>
  internal static bool IsAny(IEnumerable<Type> componentTypes)
  {
    foreach (var type in componentTypes)
      if (IsComponent(type))
        return true;
    return false;
  }

  /// <summary>Whether a component type is one of <see cref="Components"/>, or derives from one.</summary>
  internal static bool IsComponent(Type? type)
  {
    // A mod's subclass of Pickable is a pickable all the same. The walk stops at Unity's own bases: none of them is a consumable.
    for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(object); t = t.BaseType)
      if (Array.IndexOf(Components, t.FullName) >= 0)
        return true;
    return false;
  }

  /// <summary>Whether a prefab is a consumable kind: any component in its whole hierarchy (inactive children too) is one. False for no prefab.</summary>
  internal static bool Is(GameObject? prefab) => prefab != null && Probe(prefab);

  /// <summary>Reads a prefab (the offline suite has no components to read: it stands in for this).</summary>
  internal static Func<GameObject, bool> Probe = Read;

  // A prefab is read once a session: the zone loop asks for every record's entry.
  private static readonly Dictionary<GameObject, bool> Known = new();

  private static bool Read(GameObject prefab)
  {
    if (Known.TryGetValue(prefab, out bool known))
      return known;
    bool consumable = false;
    foreach (var component in prefab.GetComponentsInChildren<Component>(true))
    {
      // A script the game cannot load (a mod that is gone) leaves a null: it is not a consumable's.
      if (component != null && IsComponent(component.GetType()))
      {
        consumable = true;
        break;
      }
    }
    Known[prefab] = consumable;
    return consumable;
  }

  /// <summary>A world ends: the prefabs of the next may not be the same ones.</summary>
  internal static void Forget() => Known.Clear();

  // ---- what a session did with them (the log, and the counts bc_bake info and stats give) -----------------------------------------------

  private static readonly HashSet<string> Named = [];
  private static readonly List<string> NamedInOrder = [];

  /// <summary>Records of consumable kinds the server placed as ordinary objects this session, and those it could not place.</summary>
  internal static int Placed, Failed;

  /// <summary>The consumable kinds this session met (the server placing them, the client leaving them out), in the order they were met.</summary>
  internal static IReadOnlyList<string> Kinds => NamedInOrder;

  /// <summary>Names a consumable kind in the log, the first time it is met in a session (not for every record).</summary>
  internal static void Note(string kind)
  {
    if (!Named.Add(kind))
      return;
    NamedInOrder.Add(kind);
    BetterContinents.Log($"Baked placements: {kind} is a consumable (a player picks it up or harvests it): its records are placed once as ordinary objects of the game when a zone generates, never baked, drawn or protected.");
  }

  /// <summary>A new session: nothing of the last one's counts or names stays.</summary>
  internal static void SessionStarts()
  {
    Named.Clear();
    NamedInOrder.Clear();
    Placed = Failed = 0;
    Forget();
  }

  /// <summary>The layer's own count of the records of a palette entry set that are consumable kinds, said for bc_bake info and stats.</summary>
  internal static string Summary(int kinds, long records) =>
    $"{kinds} consumable kind{(kinds == 1 ? "" : "s")} ({records:N0} records) placed as real objects, never drawn";
}

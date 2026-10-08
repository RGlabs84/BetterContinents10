// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// What bc_bake does with each piece of an area. A baked piece stops being an object: it lives on as a record of the world's
// baked layer, drawn and solid on every client, and costs no instance. That is safe only for a piece whose whole life is its
// look and its collider; anything with state in its ZDO, a light, an area it owns, comfort, or a component this code has never
// heard of keeps being a real piece. Keeping a piece real is always safe, so every doubt goes that way.
//
// The classifier is a pure function of the prefab's component types (the whole hierarchy, read once per prefab), the object's
// creator and the command's words, so the offline tests drive it with made-up prefabs and with every piece of the game's own
// prefab dump. The rules, in order:
//   1 no Piece                                            not touched: not a piece
//   2 a TerrainModifier                                   not touched: its change to the ground lasts only while it is an object
//   3 the ZDO has bc_bake_id                              not touched: already baked
//   3b a Pickable, PickableItem, ItemDrop or Plant        not touched: gives items when used (spec 0.2: consumables are never baked, never
//                                                         adopted, with or without 'town' and 'any')
//   4 no creator, and no 'any'                            not touched: not built by a player
//   5 a loose body (a Rigidbody that moves, ZSyncTransform, ItemDrop, Ship, Vagon, Character, Tameable)   not touched: it moves
//   6 any component outside StaticSafe, any Light or EffectArea, comfort, connection data in the ZDO       stays a real piece
//     (with 'town': adopted, a protected Live record of the bake)
//   7 a Chair                                             a Seat record
//   8 otherwise                                           a Static record
internal static class BakeClassifier
{
  // What a piece may carry and still be baked: components that draw or collide, and the game's own that keep nothing in the
  // ZDO of a piece (WearNTear keeps health, which the record keeps in its value set).
  internal static readonly HashSet<string> StaticSafe =
  [
    "Piece", "WearNTear", "ZNetView", "MaterialVariation", "RandomMaterialValues", "StaticPhysics", "LodFadeInOut", "HoverText",
    "DisableInPlacementGhost", "ImpactEffect",
    // Unity's own, the ones that only draw or collide.
    "UnityEngine.Transform", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.SkinnedMeshRenderer",
    "UnityEngine.BoxCollider", "UnityEngine.SphereCollider", "UnityEngine.CapsuleCollider", "UnityEngine.MeshCollider",
    "UnityEngine.LODGroup",
  ];

  // What makes a prefab a consumable kind (spec 0.2): anything a player picks, picks up or harvests for items, anywhere in its hierarchy (a mod's
  // subclass of one counts). The one list the server and the client renderer test too.
  internal static readonly string[] ConsumableComponents = BakedConsumables.Components;

  // The component that makes a prefab a consumable kind, or null.
  internal static string? ConsumableComponent(PrefabFacts prefab)
  {
    foreach (var component in ConsumableComponents)
      if (prefab.Has(component))
        return component;
    return null;
  }

  // A Chair is what makes a Seat; with nothing else against it, it is not a reason to keep a piece real.
  private const string Chair = "Chair";

  // Rule 5's loose bodies, by the game's component names (Character covers Humanoid and Player through their base types).
  private static readonly string[] Movers = ["ZSyncTransform", "ItemDrop", "Ship", "Vagon", "Character", "Tameable"];

  // The groups of rule 6, in the order the report lists them; a piece is counted in the first that applies.
  internal const string Doors = "doors", Chests = "chests", Fires = "fires and torches", Beds = "beds", Stations = "stations",
    Comfort = "comfort", Lights = "lights", Signs = "signs", Other = "other";
  internal static readonly string[] StayGroups = [Doors, Chests, Fires, Beds, Stations, Comfort, Lights, Signs, Other];

  // The groups of rules 1 to 5, as the report words them.
  internal const string NotAPiece = "not a piece", GroundWork = "ground work", AlreadyBaked = "already baked",
    NotByPlayer = "not built by a player", Moves = "carts and ships", UnknownPrefab = "not in this game", Consumables = "gives items when used";

  // The class of one piece. The object's own facts (rules 3 and 6) come in the overload; without them neither applies.
  internal static BakeClass Classify(PrefabFacts? prefab, long creator, BakeWords words) => Classify(prefab, creator, words, default);

  internal static BakeClass Classify(PrefabFacts? prefab, long creator, BakeWords words, ObjectFacts obj)
  {
    if (prefab == null)
      return new BakeClass(BakeKind.UnknownPrefab, UnknownPrefab, "is not a prefab of this game");
    // 1
    if (!prefab.Has("Piece"))
      return new BakeClass(BakeKind.NotAPiece, NotAPiece, "is not a piece");
    // 2: a TerrainModifier's change to the ground is made again each time it wakes up and lasts only while the object lives.
    if (prefab.Has("TerrainModifier"))
      return new BakeClass(BakeKind.GroundWork, GroundWork, "shapes the ground only while it is an object", "TerrainModifier");
    // 3
    if (obj.AlreadyBaked)
      return new BakeClass(BakeKind.AlreadyBaked, AlreadyBaked, "is owned by a bake or a compiler's town already");
    // 3b: never baked and never adopted (an adopted one would be a Live record, seeded again at every load once picked).
    if (ConsumableComponent(prefab) is { } consumable)
      return new BakeClass(BakeKind.Consumable, Consumables, $"has {consumable}, which a player picks, picks up or harvests", consumable);
    // 4
    if (creator == 0 && !words.Any)
      return new BakeClass(BakeKind.NotByPlayer, NotByPlayer, "was not built by a player");
    // 5
    if (prefab.NonKinematicBody)
      return new BakeClass(BakeKind.Moves, Moves, "has a body that moves", "Rigidbody");
    foreach (var mover in Movers)
      if (prefab.Has(mover))
        return new BakeClass(BakeKind.Moves, Moves, $"moves ({mover})", mover);
    // 6
    var stays = StayReason(prefab, obj);
    if (stays.Group != null)
      return new BakeClass(words.Town ? BakeKind.Adopt : BakeKind.Stays, stays.Group, stays.Reason, stays.Component);
    // 7
    if (prefab.Has(Chair))
      return new BakeClass(BakeKind.Seat, "", "is a chair");
    // 8
    return new BakeClass(BakeKind.Static, "", "only draws and collides");
  }

  // Why a piece stays real, if it does: the group the report counts it in, in words, and the component that decided.
  private static (string? Group, string Reason, string Component) StayReason(PrefabFacts prefab, ObjectFacts obj)
  {
    if (prefab.Has("Door")) return (Doors, "opens and closes (Door)", "Door");
    if (prefab.Has("Container")) return (Chests, "holds items (Container)", "Container");
    // A fire's base object is active only on a piece with a creator, and the base value gates raids, spawns in bases and
    // "safe in home": such a piece stays real, with its creator.
    if (prefab.Has("Fireplace")) return (Fires, "burns fuel (Fireplace)", "Fireplace");
    if (prefab.Has("Bed")) return (Beds, "keeps a spawn point (Bed)", "Bed");
    foreach (var station in StationComponents)
      if (prefab.Has(station))
        return (Stations, $"works or keeps contents ({station})", station);
    // Comfort is counted from real Piece instances near the player; a local copy would put a Piece without a ZDO into the
    // game's piece lists.
    if (prefab.Comfort > 0) return (Comfort, $"gives comfort ({prefab.Comfort})", "Piece");
    if (prefab.Has("UnityEngine.Light")) return (Lights, "lights its surroundings (Light)", "UnityEngine.Light");
    if (prefab.Has("EffectArea")) return (Lights, "makes an area (EffectArea)", "EffectArea");
    if (prefab.Has("Sign")) return (Signs, "holds a text (Sign)", "Sign");
    foreach (var component in prefab.Components)
      if (!StaticSafe.Contains(component.Name) && component.Name != Chair)
        return (Other, $"has {component.Name}", component.Name);
    // The ZDO says what the prefab cannot: links to other objects (a portal's pair, a spawner's spawn).
    if (obj.HasConnection) return (Other, "is linked to other objects", "");
    return (null, "", "");
  }

  private static readonly string[] StationComponents =
    ["CraftingStation", "StationExtension", "Smelter", "CookingStation", "Fermenter", "Incinerator", "Beehive", "SapCollector", "Windmill"];

  // Prefab facts are read once per prefab and kept for the session (the game's prefabs do not change while it runs).
  private static readonly Dictionary<int, PrefabFacts?> Cache = [];
  private static ZNetScene? cacheScene;

  // The facts of a prefab by hash, from the game's own ZNetScene; null when this game has no such prefab.
  internal static PrefabFacts? Facts(int prefabHash)
  {
    var scene = ZNetScene.instance;
    if (scene == null)
      return null;
    if (!ReferenceEquals(cacheScene, scene))
    {
      Cache.Clear();
      cacheScene = scene;
    }
    if (Cache.TryGetValue(prefabHash, out var facts))
      return facts;
    var prefab = scene.GetPrefab(prefabHash);
    facts = prefab == null ? null : PrefabFacts.Read(prefab);
    Cache[prefabHash] = facts;
    return facts;
  }
}

internal enum BakeKind
{
  // Not touched: the piece stays exactly as it is.
  NotAPiece,
  GroundWork,
  AlreadyBaked,
  NotByPlayer,
  Moves,
  UnknownPrefab,
  // A consumable kind: not touched, whatever the words.
  Consumable,
  // A real piece that stays real (rule 6); with 'town' it is adopted: a protected Live record of the bake, the same object.
  Stays,
  Adopt,
  // Becomes a record, and the piece goes.
  Seat,
  Static,
}

// A piece's class: what is done with it, the report's group for it, and why in a few words.
internal readonly struct BakeClass
{
  public BakeKind Kind { get; }
  // "doors", "not built by a player", ...; empty for a piece that becomes a record.
  public string Group { get; }
  public string Reason { get; }
  // The component that decided (rule 2, 5 or 6), else empty.
  public string Component { get; }

  public BakeClass(BakeKind kind, string group, string reason, string component = "")
  {
    Kind = kind;
    Group = group;
    Reason = reason;
    Component = component;
  }

  // Becomes a record and is removed as an object.
  public bool Baked => Kind is BakeKind.Static or BakeKind.Seat;
  // A real piece that stays one (rule 6), adopted or not.
  public bool StaysReal => Kind is BakeKind.Stays or BakeKind.Adopt;
  // Not changed at all.
  public bool Untouched => !Baked && Kind != BakeKind.Adopt;
  public override string ToString() => Group.Length == 0 ? Kind.ToString() : $"{Kind}: {Group} ({Reason})";
}

// The command's words that change what the classifier decides; the rest of the words belong to the command.
internal readonly struct BakeWords
{
  // Rule 6's pieces become protected Live records of the bake.
  public bool Town { get; init; }
  // Rule 4's creator-less pieces are taken too.
  public bool Any { get; init; }
  // The change is made; without it every world-changing command is a dry run.
  public bool Confirm { get; init; }
  // A world that is not a Better Continents world may become one (4.3).
  public bool Convert { get; init; }
  // Unbake: the compiler's records too (6.3).
  public bool All { get; init; }
}

// What the ZDO of one object adds to what its prefab says.
internal readonly struct ObjectFacts
{
  // The ZDO has bc_bake_id: a bake or a compiler's town owns it (rule 3).
  public bool AlreadyBaked { get; init; }
  // The ZDO has connection data (ZDOExtraData.GetConnection): a portal's pair, a spawner's spawn or a synced transform (rule 6).
  public bool HasConnection { get; init; }
}

// A component of a prefab: its type's full name, and the full names of the types it derives from up to MonoBehaviour.
internal readonly struct ComponentFact
{
  public string Name { get; }
  public string[] Bases { get; }

  public ComponentFact(string name, params string[] bases)
  {
    Name = name;
    Bases = bases;
  }

  public bool Is(string type) => Name == type || Array.IndexOf(Bases, type) >= 0;
}

// What the classifier reads of a prefab: every component type in its whole hierarchy, whether one of its bodies moves, its
// comfort, and whether its ZNetView syncs scale. No Unity objects, so the tests make them by hand.
internal sealed class PrefabFacts
{
  public string Name { get; }
  public IReadOnlyList<ComponentFact> Components { get; }
  // A Rigidbody that is not kinematic: it falls, rolls and is pushed.
  public bool NonKinematicBody { get; }
  // The largest Piece.m_comfort in the hierarchy.
  public int Comfort { get; }
  // ZNetView.m_syncInitialScale: the ZDO's scale is part of the piece.
  public bool SyncsScale { get; }
  // How far above its pivot the prefab reaches, in whole metres, for the y bounds of the zone its records are in; 0 when not known.
  public float Height { get; }

  public PrefabFacts(string name, IEnumerable<ComponentFact> components, bool nonKinematicBody = false, int comfort = 0, bool syncsScale = false, float height = 0f)
  {
    Name = name;
    Components = [.. components];
    NonKinematicBody = nonKinematicBody;
    Comfort = comfort;
    SyncsScale = syncsScale;
    Height = height;
  }

  public PrefabFacts(string name, IEnumerable<string> components, bool nonKinematicBody = false, int comfort = 0, bool syncsScale = false, float height = 0f)
    : this(name, components.Select(c => new ComponentFact(c, BasesOf(c))), nonKinematicBody, comfort, syncsScale, height)
  {
  }

  // The game's own base types that rule 5 needs: Humanoid and Player are Characters.
  private static string[] BasesOf(string component) => component switch
  {
    "Humanoid" => ["Character"],
    "Player" => ["Humanoid", "Character"],
    _ => [],
  };

  public bool Has(string component)
  {
    foreach (var c in Components)
      if (c.Is(component))
        return true;
    return false;
  }

  // Reads a prefab of the game: its whole hierarchy, inactive children too.
  internal static PrefabFacts Read(GameObject prefab)
  {
    var components = new List<ComponentFact>();
    var seen = new HashSet<string>();
    bool body = false;
    int comfort = 0;
    foreach (var component in prefab.GetComponentsInChildren<Component>(true))
    {
      // A script the game cannot load (a mod that is gone) leaves a null; whatever it was, the piece stays real.
      if (component == null)
      {
        if (seen.Add("(missing script)"))
          components.Add(new ComponentFact("(missing script)"));
        continue;
      }
      var type = component.GetType();
      var name = type.FullName ?? type.Name;
      if (seen.Add(name))
      {
        var bases = new List<string>();
        for (var b = type.BaseType; b != null && b != typeof(MonoBehaviour) && b != typeof(Behaviour) && b != typeof(Component) && b != typeof(object); b = b.BaseType)
          bases.Add(b.FullName ?? b.Name);
        components.Add(new ComponentFact(name, [.. bases]));
      }
      if (component is Rigidbody rigidbody && !rigidbody.isKinematic)
        body = true;
      if (component is Piece piece)
        comfort = Math.Max(comfort, piece.m_comfort);
    }
    var view = prefab.GetComponent<ZNetView>();
    return new PrefabFacts(prefab.name, components, body, comfort, view != null && view.m_syncInitialScale, HeightOf(prefab));
  }

  // The highest corner of any mesh's bounds, in the prefab's own frame, rounded up to a whole metre with a margin (0 when it has no mesh).
  // Mesh.bounds is stored with the mesh, so it can be read from a mesh the CPU cannot read.
  private static float HeightOf(GameObject prefab)
  {
    float top = 0f;
    bool any = false;
    var toRoot = prefab.transform.worldToLocalMatrix;
    foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
    {
      var mesh = filter.sharedMesh;
      if (mesh == null)
        continue;
      var matrix = toRoot * filter.transform.localToWorldMatrix;
      var bounds = mesh.bounds;
      for (int corner = 0; corner < 8; corner++)
      {
        var offset = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
        var point = matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, offset));
        any = true;
        if (point.y > top)
          top = point.y;
      }
    }
    return any ? Mathf.Max(1f, Mathf.Ceil(top + 0.5f)) : 0f;
  }
}

// What a dry run counts: how many pieces fall in each class, which prefabs become records, and what to say about it.
internal sealed class BakeTally
{
  public int Found { get; private set; }
  // To bake, by prefab name.
  public Dictionary<string, int> Baked { get; } = [];
  public int Records { get; private set; }
  public int Seats { get; private set; }
  // Pieces that stay real (rule 6) by group, and one example of each "other".
  public Dictionary<string, int> Stays { get; } = [];
  public List<string> OtherExamples { get; } = [];
  // Not touched, by group.
  public Dictionary<string, int> Untouched { get; } = [];
  // Pieces taken only because of 'any': no creator.
  public int WithoutCreator { get; private set; }
  public int Adopted { get; private set; }

  // Rule 1 is not counted as a piece; everything else is a piece.
  public void Add(BakeClass cls, string prefabName, long creator)
  {
    if (cls.Kind == BakeKind.NotAPiece)
      return;
    // An unknown prefab is not known to be a piece either.
    if (cls.Kind == BakeKind.UnknownPrefab)
    {
      Bump(Untouched, cls.Group);
      return;
    }
    Found++;
    if (creator == 0 && cls.Kind != BakeKind.NotByPlayer)
      WithoutCreator++;
    switch (cls.Kind)
    {
      case BakeKind.Static:
      case BakeKind.Seat:
        Records++;
        if (cls.Kind == BakeKind.Seat)
          Seats++;
        Baked[prefabName] = Baked.TryGetValue(prefabName, out var n) ? n + 1 : 1;
        break;
      case BakeKind.Stays:
      case BakeKind.Adopt:
        if (cls.Kind == BakeKind.Adopt)
          Adopted++;
        Bump(Stays, cls.Group);
        if (cls.Group == BakeClassifier.Other && OtherExamples.Count < 3)
        {
          var example = $"{prefabName} {cls.Reason}";
          if (!OtherExamples.Contains(example))
            OtherExamples.Add(example);
        }
        break;
      default:
        Bump(Untouched, cls.Group);
        break;
    }
  }

  private static void Bump(Dictionary<string, int> counts, string group) => counts[group] = counts.TryGetValue(group, out var n) ? n + 1 : 1;

  public int StayCount => Stays.Values.Sum();
  public int UntouchedCount => Untouched.Values.Sum();

  internal static string Num(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

  // "stone_wall_2x1 1,204, wood_floor 980, darkwood_roof 611 and 18 more kinds"
  internal string BakedKinds(int show = 3)
  {
    var top = Baked.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
    var text = string.Join(", ", top.Take(show).Select(p => $"{p.Key} {Num(p.Value)}"));
    if (top.Count > show)
      text += $" and {Num(top.Count - show)} more kinds";
    return text;
  }

  // The three lines of the dry run that depend on the classes alone (10.11), as they are said.
  internal IEnumerable<string> Lines(BakeWords words)
  {
    yield return $"Found {Num(Found)} pieces. " + (Records == 0 ? "Nothing to bake." : $"To bake: {Num(Records)} ({BakedKinds()}).");
    if (StayCount > 0)
    {
      var groups = new List<string>();
      foreach (var group in BakeClassifier.StayGroups)
        if (Stays.TryGetValue(group, out var count))
          groups.Add($"{group} {Num(count)}" + (group == BakeClassifier.Other && OtherExamples.Count > 0 ? ": " + string.Join("; ", OtherExamples) : ""));
      yield return words.Town
        ? $"Town pieces: {Num(StayCount)} ({string.Join(", ", groups)}). They stay real pieces and become protected parts of the layer."
        : $"Stay pieces: {Num(StayCount)} ({string.Join(", ", groups)}). Add 'town' to make them protected parts of the layer.";
    }
    if (UntouchedCount > 0)
    {
      var parts = new List<string>();
      foreach (var group in new[] { BakeClassifier.NotByPlayer, BakeClassifier.Moves, BakeClassifier.Consumables, BakeClassifier.AlreadyBaked, BakeClassifier.GroundWork, BakeClassifier.UnknownPrefab })
        if (Untouched.TryGetValue(group, out var count))
          parts.Add($"{group} {Num(count)}" + (group == BakeClassifier.NotByPlayer ? ", add 'any' to take them" : ""));
      yield return $"Not touched: {Num(UntouchedCount)} ({string.Join("; ", parts)}).";
    }
    if (words.Any && WithoutCreator > 0)
      yield return $"{Num(WithoutCreator)} of these pieces have no creator. Some may belong to the game's own places, which a later zone reset puts back over the baked ones.";
  }
}

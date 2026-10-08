// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of the in-game bake's classifier (BakeClassifier.cs): the eight rules each on a made-up prefab and their order,
// StaticSafe, the groups the dry run reports, its report lines against the spec's example, and every piece prefab of the game's own
// prefab dump (with the dump's comfort values) given a class and a reason.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static readonly string[] BakeWallParts =
    ["Piece", "WearNTear", "ZNetView", "UnityEngine.Transform", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.BoxCollider"];

  private static PrefabFacts BakePrefab(string name, params string[] extra) => new(name, BakeWallParts.Concat(extra), false, 0, false);

  private static BakeClass BakeCls(PrefabFacts prefab, long creator = 7, BakeWords words = default, ObjectFacts obj = default) =>
    BakeClassifier.Classify(prefab, creator, words, obj);

  private static void BakeClassifierRulesTest()
  {
    Section("classifier: rule 1 to 4, the not touched");
    var wall = BakePrefab("wood_wall");
    C(BakeCls(wall).Kind == BakeKind.Static, "a plain wall built by a player is a Static");
    C(BakeCls(BakePrefab("x"), 7, default, default).Baked, "Baked is true for a Static");

    var tree = new PrefabFacts("Pine", ["ZNetView", "UnityEngine.Transform"]);
    C(BakeCls(tree).Kind == BakeKind.NotAPiece && BakeCls(tree).Group == BakeClassifier.NotAPiece, "rule 1: no Piece, not touched");
    C(BakeCls(tree, 0, new BakeWords { Any = true, Town = true }).Kind == BakeKind.NotAPiece, "rule 1 holds with 'any' and 'town' too");
    C(BakeCls(null).Kind == BakeKind.UnknownPrefab && BakeCls(null).Untouched, "a prefab this game does not have is not touched");

    var path = BakePrefab("path", "TerrainModifier");
    C(BakeCls(path).Kind == BakeKind.GroundWork && BakeCls(path).Component == "TerrainModifier", "rule 2: a TerrainModifier, not touched");
    C(BakeCls(path, 0).Kind == BakeKind.GroundWork, "rule 2 comes before rule 4");
    C(BakeCls(path, 7, default, new ObjectFacts { AlreadyBaked = true }).Kind == BakeKind.GroundWork, "rule 2 comes before rule 3");
    C(BakeCls(path, 7, new BakeWords { Any = true, Town = true }).Kind == BakeKind.GroundWork, "'any' and 'town' do not take ground work");

    C(BakeCls(wall, 7, default, new ObjectFacts { AlreadyBaked = true }).Kind == BakeKind.AlreadyBaked, "rule 3: bc_bake_id, already baked");
    C(BakeCls(wall, 0, default, new ObjectFacts { AlreadyBaked = true }).Kind == BakeKind.AlreadyBaked, "rule 3 comes before rule 4");
    C(BakeCls(wall, 7, new BakeWords { Any = true, Town = true }, new ObjectFacts { AlreadyBaked = true }).Kind == BakeKind.AlreadyBaked,
      "'any' and 'town' do not take what is already baked");

    C(BakeCls(wall, 0).Kind == BakeKind.NotByPlayer && BakeCls(wall, 0).Group == BakeClassifier.NotByPlayer, "rule 4: no creator, not touched");
    C(BakeCls(wall, 0, new BakeWords { Any = true }).Kind == BakeKind.Static, "rule 4 with 'any': taken");
    C(BakeCls(wall, -5).Kind == BakeKind.Static, "a creator is any nonzero id");
    C(BakeCls(BakePrefab("door", "Door"), 0).Kind == BakeKind.NotByPlayer, "rule 4 comes before rule 6: a creator-less door is not touched");
    C(BakeCls(BakePrefab("door", "Door"), 0, new BakeWords { Any = true }).Kind == BakeKind.Stays, "... and with 'any' it is a door that stays");

    Section("classifier: rule 5, what moves");
    foreach (var mover in new[] { "ZSyncTransform", "Ship", "Vagon", "Character", "Tameable" })
    {
      var c = BakeCls(BakePrefab("piece_" + mover, mover));
      C(c.Kind == BakeKind.Moves && c.Component == mover && c.Group == BakeClassifier.Moves, $"rule 5: {mover} moves, not touched");
    }
    C(BakeCls(BakePrefab("giant", "Humanoid")).Kind == BakeKind.Moves, "Humanoid is a Character");
    C(BakeCls(BakePrefab("me", "Player")).Kind == BakeKind.Moves, "Player is a Character");
    C(BakeCls(new PrefabFacts("falls", [.. BakeWallParts, "UnityEngine.Rigidbody"], nonKinematicBody: true)).Kind == BakeKind.Moves, "a Rigidbody that is not kinematic moves");
    var pinned = BakeCls(new PrefabFacts("pinned", [.. BakeWallParts, "UnityEngine.Rigidbody"], nonKinematicBody: false));
    C(pinned.Kind == BakeKind.Stays && pinned.Component == "UnityEngine.Rigidbody", "a kinematic Rigidbody does not move it, but it is not StaticSafe: it stays");
    C(BakeCls(BakePrefab("torch", "Fireplace", "ItemDrop")).Kind == BakeKind.Consumable, "an ItemDrop is a consumable (spec 0.2), before rules 5 and 6: a torch item is not touched");
    C(BakeCls(BakePrefab("cartdoor", "Door", "Vagon"), 0, new BakeWords { Any = true }).Kind == BakeKind.Moves, "'any' does not take what moves");
    C(BakeCls(BakePrefab("cartdoor", "Door", "Vagon"), 7, new BakeWords { Town = true }).Kind == BakeKind.Moves, "'town' does not adopt what moves");

    Section("classifier: rule 6, what stays real");
    var expect = new (string[] Components, int Comfort, string Group)[]
    {
      (["Door"], 0, BakeClassifier.Doors),
      (["Container"], 0, BakeClassifier.Chests),
      (["Fireplace"], 0, BakeClassifier.Fires),
      (["Fireplace", "UnityEngine.Light", "EffectArea"], 0, BakeClassifier.Fires),
      (["Bed"], 2, BakeClassifier.Beds),
      (["CraftingStation"], 0, BakeClassifier.Stations),
      (["StationExtension"], 0, BakeClassifier.Stations),
      (["Smelter"], 0, BakeClassifier.Stations),
      (["CookingStation"], 0, BakeClassifier.Stations),
      (["Fermenter"], 0, BakeClassifier.Stations),
      (["Incinerator"], 0, BakeClassifier.Stations),
      (["Beehive"], 0, BakeClassifier.Stations),
      (["SapCollector"], 0, BakeClassifier.Stations),
      (["Windmill"], 0, BakeClassifier.Stations),
      ([], 1, BakeClassifier.Comfort),
      (["Chair"], 2, BakeClassifier.Comfort),
      (["UnityEngine.Light"], 0, BakeClassifier.Lights),
      (["EffectArea"], 0, BakeClassifier.Lights),
      (["Chair", "UnityEngine.Light"], 0, BakeClassifier.Lights),
      (["Sign"], 0, BakeClassifier.Signs),
      (["MyMod.Lamp"], 0, BakeClassifier.Other),
      (["Destructible"], 0, BakeClassifier.Other),
      (["ItemStand"], 0, BakeClassifier.Other),
      (["PrivateArea"], 0, BakeClassifier.Other),
      (["TeleportWorld"], 0, BakeClassifier.Other),
      (["Door", "UnityEngine.Light"], 0, BakeClassifier.Doors),
      (["Container", "Door"], 0, BakeClassifier.Doors),
      (["CraftingStation"], 1, BakeClassifier.Stations),
      (["Sign", "UnityEngine.Light"], 0, BakeClassifier.Lights),
      (["UnityEngine.ParticleSystem"], 0, BakeClassifier.Other),
      (["UnityEngine.Animator"], 0, BakeClassifier.Other),
      (["UnityEngine.AudioSource"], 0, BakeClassifier.Other),
      (["UnityEngine.LineRenderer"], 0, BakeClassifier.Other),
      (["UnityEngine.WheelCollider"], 0, BakeClassifier.Other),
      (["RandomPieceRotation"], 0, BakeClassifier.Other),
    };
    foreach (var (components, comfort, group) in expect)
    {
      var prefab = new PrefabFacts("p_" + string.Join("_", components), [.. BakeWallParts, .. components], false, comfort, false);
      var c = BakeCls(prefab);
      var what = $"[{string.Join(", ", components)}] comfort {comfort}";
      C(c.Kind == BakeKind.Stays && c.Group == group && c.Reason.Length > 0, $"rule 6: {what} stays real in group '{group}' (got {c.Kind}, '{c.Group}')");
      var town = BakeCls(prefab, 7, new BakeWords { Town = true });
      C(town.Kind == BakeKind.Adopt && town.Group == group && town.StaysReal && !town.Baked, $"rule 6 with 'town': {what} is adopted, same group");
    }
    var lamp = BakeCls(BakePrefab("piece_example", "MyMod.Lamp"));
    C(lamp.Component == "MyMod.Lamp" && lamp.Reason == "has MyMod.Lamp", "an unknown component is named, as the dry run says it: 'has MyMod.Lamp'");
    C(BakeCls(wall, 7, default, new ObjectFacts { HasConnection = true }) is { Kind: BakeKind.Stays, Group: "other" }, "rule 6: connection data in the ZDO keeps a piece real");
    C(BakeCls(wall, 7, new BakeWords { Town = true }, new ObjectFacts { HasConnection = true }).Kind == BakeKind.Adopt, "... and 'town' adopts it");
    C(BakeCls(new PrefabFacts("broken", [.. BakeWallParts, "(missing script)"])).Kind == BakeKind.Stays, "a script the game cannot load keeps a piece real");
    C(BakeCls(new PrefabFacts("mod", [.. BakeWallParts, "MyMod.WearNTear"])).Kind == BakeKind.Stays, "a mod's own type with a familiar name is not the game's: it stays");
    C(BakeCls(BakePrefab("a", "Chair", "Door")).Group == BakeClassifier.Doors, "a chair with a door is a door that stays");

    Section("classifier: rule 7 and 8, the records");
    var seat = BakeCls(BakePrefab("mod_stool", "Chair"));
    C(seat.Kind == BakeKind.Seat && seat.Baked && seat.Group.Length == 0, "rule 7: a Chair with nothing against it is a Seat");
    C(BakeCls(BakePrefab("mod_stool", "Chair"), 7, new BakeWords { Town = true }).Kind == BakeKind.Seat, "'town' does not change a Seat");
    C(BakeCls(new PrefabFacts("bench", [.. BakeWallParts, "Chair"], false, 1, false)).Kind == BakeKind.Stays, "a chair that gives comfort stays real (rule 6 first)");
    foreach (var safe in BakeClassifier.StaticSafe)
      C(BakeCls(new PrefabFacts("safe", [.. BakeWallParts, safe])).Kind == BakeKind.Static, $"StaticSafe: {safe} alone keeps a piece a Static");
    C(BakeCls(BakePrefab("rich", "MaterialVariation", "RandomMaterialValues", "StaticPhysics", "LodFadeInOut", "HoverText", "DisableInPlacementGhost", "ImpactEffect", "UnityEngine.LODGroup", "UnityEngine.SkinnedMeshRenderer")).Kind == BakeKind.Static,
      "every StaticSafe component together is still a Static");
    C(BakeClassifier.StaticSafe.Count == 19, $"StaticSafe is the 10 game components and 9 of Unity's (it has {BakeClassifier.StaticSafe.Count})");

    Section("classifier: the new overload and the 3-argument form agree");
    C(BakeClassifier.Classify(wall, 7, default).Kind == BakeKind.Static && BakeClassifier.Classify(wall, 0, default).Kind == BakeKind.NotByPlayer,
      "the 3-argument form of 13.2 is the 4-argument form with an object that says nothing");
  }

  private static void BakeClassifierReportTest()
  {
    Section("classifier: the dry run's lines (10.11's example)");
    var tally = new BakeTally();
    void Add(PrefabFacts prefab, int times, long creator = 7, BakeWords words = default, ObjectFacts obj = default)
    {
      for (int i = 0; i < times; i++)
        tally.Add(BakeCls(prefab, creator, words, obj), prefab.Name, creator);
    }
    Add(BakePrefab("stone_wall_2x1"), 1204);
    Add(BakePrefab("wood_floor"), 980);
    Add(BakePrefab("darkwood_roof"), 611);
    int rest = 5630 - 1204 - 980 - 611;
    for (int k = 0; k < 18; k++)
      Add(BakePrefab("kind" + k.ToString("00")), k < 17 ? rest / 18 : rest - 17 * (rest / 18));
    Add(BakePrefab("door", "Door"), 18);
    Add(BakePrefab("chest", "Container"), 41);
    Add(BakePrefab("torch", "Fireplace"), 37);
    Add(BakePrefab("bed", "Bed"), 4);
    Add(BakePrefab("bench", "CraftingStation"), 9);
    tally.Add(BakeCls(new PrefabFacts("rug", BakeWallParts, false, 1, false)), "rug", 7);
    for (int i = 0; i < 60; i++)
      tally.Add(BakeCls(new PrefabFacts("rug", BakeWallParts, false, 1, false)), "rug", 7);
    Add(BakePrefab("lantern", "UnityEngine.Light"), 22);
    Add(BakePrefab("sign", "Sign"), 3);
    Add(BakePrefab("piece_example", "MyMod.Lamp"), 17);
    Add(BakePrefab("wild_wall"), 310, creator: 0);
    Add(BakePrefab("cart", "Vagon"), 2);
    Add(BakePrefab("old_wall"), 60, obj: new ObjectFacts { AlreadyBaked = true });
    // Objects that are not pieces are not counted at all.
    Add(new PrefabFacts("Pine", ["ZNetView"]), 500);
    var lines = tally.Lines(default).ToList();
    C(lines.Count == 3, $"three lines (got {lines.Count})");
    C(lines.ElementAtOrDefault(0) == "Found 6,214 pieces. To bake: 5,630 (stone_wall_2x1 1,204, wood_floor 980, darkwood_roof 611 and 18 more kinds).",
      "the found line: " + lines.ElementAtOrDefault(0));
    C(lines.ElementAtOrDefault(1) == "Stay pieces: 212 (doors 18, chests 41, fires and torches 37, beds 4, stations 9, comfort 61, lights 22, signs 3, other 17: piece_example has MyMod.Lamp). Add 'town' to make them protected parts of the layer.",
      "the stay line: " + lines.ElementAtOrDefault(1));
    C(lines.ElementAtOrDefault(2) == "Not touched: 372 (not built by a player 310, add 'any' to take them; carts and ships 2; already baked 60).",
      "the not touched line: " + lines.ElementAtOrDefault(2));
    C(tally.Records == 5630 && tally.Found == 6214 && tally.StayCount == 212 && tally.UntouchedCount == 372, "the counts add up to 5,630 + 212 + 372 = 6,214");

    var town = tally.Lines(new BakeWords { Town = true }).ToList();
    C(town[1].StartsWith("Town pieces: 212 (") && town[1].EndsWith("They stay real pieces and become protected parts of the layer."), "with 'town' the stay line says they become protected parts: " + town[1]);

    var any = new BakeTally();
    any.Add(BakeCls(BakePrefab("a"), 0, new BakeWords { Any = true }), "a", 0);
    any.Add(BakeCls(BakePrefab("b"), 7, new BakeWords { Any = true }), "b", 7);
    var anyLines = any.Lines(new BakeWords { Any = true }).ToList();
    C(anyLines.Count == 2 && anyLines[1].StartsWith("1 of these pieces have no creator."), "with 'any' the dry run warns about creator-less pieces: " + string.Join(" | ", anyLines));
    var none = new BakeTally();
    none.Add(BakeCls(BakePrefab("a"), 0), "a", 0);
    var noneLines = none.Lines(default).ToList();
    C(noneLines[0] == "Found 1 pieces. Nothing to bake." && noneLines[1].StartsWith("Not touched: 1 (not built by a player 1, add 'any' to take them)"), "nothing to bake: " + string.Join(" | ", noneLines));
    C(new BakeTally().Lines(default).Single() == "Found 0 pieces. Nothing to bake.", "an empty area says so");
    var three = new BakeTally();
    foreach (var name in new[] { "b", "a", "c" })
      three.Add(BakeCls(BakePrefab(name)), name, 7);
    C(three.BakedKinds() == "a 1, b 1, c 1", "kinds with equal counts go by name: " + three.BakedKinds());
  }

  // ------------------------------------------------------------------------------------------------ the game's own pieces

  private const string BakeDumpDir = "/home/rohan/WubarrkCODING/libs-Tools/WubarrksEye_Dumps/2026-09-08_10-13-48-playtest-server-build23105022-clean-VANILLA/";

  // Piece.m_comfort and ZNetView.m_syncInitialScale of every prefab of the values dump, read as a stream (the file is 52 MB).
  private static Dictionary<string, (int Comfort, bool SyncsScale)> BakeValuesDump(string path)
  {
    var result = new Dictionary<string, (int, bool)>();
    var bytes = File.ReadAllBytes(path);
    var reader = new Utf8JsonReader(bytes);
    reader.Read();
    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
    {
      string? key = null, source = null, comfort = null, sync = null;
      while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
      {
        var name = reader.GetString();
        reader.Read();
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
          reader.Skip();
          continue;
        }
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (name == "_Key") key = value;
        else if (name == "_Source") source = value;
        else if (name == "Piece.m_comfort") comfort = value;
        else if (name == "ZNetView.m_syncInitialScale") sync = value;
      }
      if (source == "Prefabs" && key != null)
        result[key] = (int.TryParse(comfort, out var c) ? c : 0, sync == "true");
    }
    return result;
  }

  private static void BakeClassifierDumpTest()
  {
    Section("classifier: every piece prefab of the game's dump gets a class and a reason");
    var prefabs = BakeDumpDir + "Prefabs_Dump.json";
    var values = BakeDumpDir + "Values_Dump.json";
    if (!File.Exists(prefabs) || !File.Exists(values))
    {
      C(false, "the vanilla dump is not at " + BakeDumpDir);
      return;
    }
    var extra = BakeValuesDump(values);
    using var doc = JsonDocument.Parse(File.ReadAllBytes(prefabs));
    var classes = new Dictionary<string, BakeClass>();
    int pieces = 0;
    foreach (var prefab in doc.RootElement.EnumerateArray())
    {
      var name = prefab.GetProperty("Name").GetString()!;
      var components = prefab.GetProperty("Components").EnumerateArray().Select(c => c.GetProperty("ComponentType").GetString()!).Distinct().ToList();
      if (!components.Contains("Piece"))
        continue;
      pieces++;
      extra.TryGetValue(name, out var more);
      // The dump lists root components and not whether a Rigidbody is kinematic; on the game's own pieces every Rigidbody rides with ZSyncTransform.
      var facts = new PrefabFacts(name, components, components.Contains("UnityEngine.Rigidbody"), more.Comfort, more.SyncsScale);
      var cls = BakeClassifier.Classify(facts, 1, default);
      classes[name] = cls;
      C(cls.Reason.Length > 0 && Enum.IsDefined(typeof(BakeKind), cls.Kind), $"{name} has a class and a reason ({cls})");
      C(cls.Baked == (cls.Group.Length == 0 && cls.Kind is BakeKind.Static or BakeKind.Seat), $"{name}: a record has no group, the others have one");
    }
    C(pieces == 535, $"the dump has 535 piece prefabs (it has {pieces})");

    var byKind = classes.Values.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
    var summary = string.Join(", ", byKind.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));
    System.Console.WriteLine("   the 535 vanilla pieces: " + summary);
    var stayGroups = classes.Values.Where(c => c.Kind == BakeKind.Stays).GroupBy(c => c.Group).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
    System.Console.WriteLine("   the ones that stay real: " + string.Join(", ", stayGroups));

    // What the game's designers would expect, by name.
    void Is(string name, BakeKind kind, string? group = null, string? why = null)
    {
      if (!classes.TryGetValue(name, out var cls))
      {
        C(false, $"{name} is in the dump");
        return;
      }
      C(cls.Kind == kind && (group == null || cls.Group == group), $"{name}: {why ?? kind.ToString()} (got {cls})");
    }
    Is("wood_wall_roof", BakeKind.Static, null, "a roof piece is a Static");
    Is("stone_wall_2x1", BakeKind.Static);
    Is("wood_floor", BakeKind.Static);
    Is("woodwall", BakeKind.Static);
    Is("wood_door", BakeKind.Stays, BakeClassifier.Doors);
    Is("piece_chest_wood", BakeKind.Stays, BakeClassifier.Chests);
    Is("piece_chest", BakeKind.Stays, BakeClassifier.Chests);
    Is("fire_pit", BakeKind.Stays, BakeClassifier.Fires);
    Is("piece_groundtorch_wood", BakeKind.Stays, BakeClassifier.Fires);
    Is("bed", BakeKind.Stays, BakeClassifier.Beds);
    Is("piece_workbench", BakeKind.Stays, BakeClassifier.Stations);
    Is("forge", BakeKind.Stays, BakeClassifier.Stations);
    Is("piece_chair02", BakeKind.Stays, BakeClassifier.Comfort, "a chair that gives comfort stays real");
    Is("piece_bench01", BakeKind.Stays, BakeClassifier.Comfort);
    Is("rug_wolf", BakeKind.Stays, BakeClassifier.Comfort, "a rug gives comfort");
    Is("sign", BakeKind.Stays, BakeClassifier.Signs);
    Is("path", BakeKind.GroundWork);
    Is("raise", BakeKind.GroundWork);
    Is("Cart", BakeKind.Moves);
    Is("Karve", BakeKind.Moves);
    Is("MeadHasty", BakeKind.Consumable, BakeClassifier.Consumables, "an item that is placed is a consumable");
    // The hierarchy decides what the game's own hearth is; the dump has only root components.
    C(classes.Values.Count(c => c.Kind == BakeKind.Static) > 100, $"the game has many Static pieces (found {classes.Values.Count(c => c.Kind == BakeKind.Static)})");
  }
}

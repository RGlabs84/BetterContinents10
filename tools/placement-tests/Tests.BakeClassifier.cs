// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of the in-game bake's classifier (BakeClassifier.cs): the eight rules each on a made-up prefab and their order,
// StaticSafe, the groups the dry run reports, its report lines against the spec's example, and every piece prefab of the game's own
// prefab dump (with the dump's comfort values) given a class and a reason. 0.10.4 decor: with 'town' a piece that stays only for its comfort
// is baked as decor (a Seat for a chair), a piece that stays only for its light and fire is baked as a lit copy (role Copy) unless it is a
// fire to cook on (a Burning EffectArea), and RandomPieceRotation is StaticSafe.

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

  // A prefab with some components in a part that is inactive in the prefab (WearNTear's worn, broken and wet looks).
  private static PrefabFacts BakePrefabInactive(string name, string[] active, string[] inactive) =>
    new(name, BakeWallParts.Concat(active).Select(c => new ComponentFact(c)).Concat(inactive.Select(c => new ComponentFact(c, false))), false, 0, false);

  // The census of phase Q (2026-10-07: the game's wood floors, walls and roofs stayed real): what a part that is inactive in the prefab
  // carries does not keep a piece real, and SimpleMeshCombine is safe.
  private static void BakeClassifierInactivePartsTest()
  {
    Section("classifier: inactive parts and SimpleMeshCombine");
    var floor = BakePrefabInactive("wood_floor", [], ["UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer"]);
    C(BakeCls(floor).Kind == BakeKind.Static, "a wood floor whose rain drip (a ParticleSystem) is in an inactive part is a Static");
    var lit = BakePrefab("sparkly_floor", "UnityEngine.ParticleSystem");
    C(BakeCls(lit).Kind == BakeKind.Stays && BakeCls(lit).Component == "UnityEngine.ParticleSystem", "the same ParticleSystem in an active part keeps it real");
    var roof = BakePrefabInactive("wood_roof", [], ["SimpleMeshCombine", "RandomPieceRotation"]);
    C(BakeCls(roof).Kind == BakeKind.Static, "a roof whose worn look carries SimpleMeshCombine and RandomPieceRotation is a Static");
    C(BakeCls(BakePrefab("woodwall", "SimpleMeshCombine")).Kind == BakeKind.Static, "SimpleMeshCombine in an active part is safe too (no code runs in the game)");
    C(BakeCls(BakePrefab("rotated", "RandomPieceRotation")).Kind == BakeKind.Static, "RandomPieceRotation in an active part is safe too (0.10.4 decor: the drawing turns the mesh as the game does)");
    var hiddenDoor = BakePrefabInactive("door_hidden", [], ["Door"]);
    C(BakeCls(hiddenDoor).Kind == BakeKind.Stays && BakeCls(hiddenDoor).Group == BakeClassifier.Doors, "a Door counts wherever it is, an inactive part too");
    var hiddenLight = BakePrefabInactive("lamp_hidden", [], ["UnityEngine.Light"]);
    C(BakeCls(hiddenLight).Kind == BakeKind.Stays, "a Light counts wherever it is");
    var hiddenPick = BakePrefabInactive("berry_hidden", [], ["Pickable"]);
    C(BakeCls(hiddenPick).Kind == BakeKind.Consumable, "a consumable counts wherever it is (rule 3b)");
    var hiddenCart = BakePrefabInactive("cart_hidden", [], ["Vagon"]);
    C(BakeCls(hiddenCart).Kind == BakeKind.Moves, "a mover counts wherever it is (rule 5)");
    var both = new PrefabFacts("both", BakeWallParts.Select(c => new ComponentFact(c)).Append(new ComponentFact("MyMod.Thing", true)), false, 0, false);
    C(BakeCls(both).Kind == BakeKind.Stays && BakeCls(both).Component == "MyMod.Thing", "an unknown component in an active part keeps it real");
    C(new ComponentFact("X").Active && !new ComponentFact("X", false).Active, "a component is active unless it is said not to be");
  }

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
    // [components, comfort, the group it stays in, what 'town' makes of it]: most are adopted; comfort alone is decor, light and fire alone a lit copy
    const BakeKind A = BakeKind.Adopt;
    var expect = new (string[] Components, int Comfort, string Group, BakeKind Town)[]
    {
      (["Door"], 0, BakeClassifier.Doors, A),
      (["Container"], 0, BakeClassifier.Chests, A),
      (["Fireplace"], 0, BakeClassifier.Fires, BakeKind.Copy),
      (["Fireplace", "UnityEngine.Light", "EffectArea"], 0, BakeClassifier.Fires, BakeKind.Copy),
      (["Bed"], 2, BakeClassifier.Beds, A),
      (["CraftingStation"], 0, BakeClassifier.Stations, A),
      (["StationExtension"], 0, BakeClassifier.Stations, A),
      (["Smelter"], 0, BakeClassifier.Stations, A),
      (["CookingStation"], 0, BakeClassifier.Stations, A),
      (["Fermenter"], 0, BakeClassifier.Stations, A),
      (["Incinerator"], 0, BakeClassifier.Stations, A),
      (["Beehive"], 0, BakeClassifier.Stations, A),
      (["SapCollector"], 0, BakeClassifier.Stations, A),
      (["Windmill"], 0, BakeClassifier.Stations, A),
      ([], 1, BakeClassifier.Comfort, BakeKind.Static),
      (["Chair"], 2, BakeClassifier.Comfort, BakeKind.Seat),
      (["UnityEngine.Light"], 0, BakeClassifier.Lights, BakeKind.Copy),
      (["EffectArea"], 0, BakeClassifier.Lights, A),
      (["Chair", "UnityEngine.Light"], 0, BakeClassifier.Lights, A),
      (["Sign"], 0, BakeClassifier.Signs, A),
      (["MyMod.Lamp"], 0, BakeClassifier.Other, A),
      (["Destructible"], 0, BakeClassifier.Other, A),
      (["ItemStand"], 0, BakeClassifier.Other, A),
      (["PrivateArea"], 0, BakeClassifier.Other, A),
      (["TeleportWorld"], 0, BakeClassifier.Other, A),
      (["Door", "UnityEngine.Light"], 0, BakeClassifier.Doors, A),
      (["Container", "Door"], 0, BakeClassifier.Doors, A),
      (["CraftingStation"], 1, BakeClassifier.Stations, A),
      (["Sign", "UnityEngine.Light"], 0, BakeClassifier.Lights, A),
      (["UnityEngine.ParticleSystem"], 0, BakeClassifier.Other, A),
      (["UnityEngine.Animator"], 0, BakeClassifier.Other, A),
      (["UnityEngine.AudioSource"], 0, BakeClassifier.Other, A),
      (["UnityEngine.LineRenderer"], 0, BakeClassifier.Other, A),
      (["UnityEngine.WheelCollider"], 0, BakeClassifier.Other, A),
    };
    foreach (var (components, comfort, group, townKind) in expect)
    {
      var prefab = new PrefabFacts("p_" + string.Join("_", components), [.. BakeWallParts, .. components], false, comfort, false);
      var c = BakeCls(prefab);
      var what = $"[{string.Join(", ", components)}] comfort {comfort}";
      C(c.Kind == BakeKind.Stays && c.Group == group && c.Reason.Length > 0, $"rule 6: {what} stays real in group '{group}' (got {c.Kind}, '{c.Group}')");
      var town = BakeCls(prefab, 7, new BakeWords { Town = true });
      if (townKind == BakeKind.Adopt)
        C(town.Kind == BakeKind.Adopt && town.Group == group && town.StaysReal && !town.Baked, $"rule 6 with 'town': {what} is adopted, same group (got {town.Kind})");
      else
        C(town.Kind == townKind && town.Baked && town.Group.Length == 0 && town.Note.Length > 0, $"rule 6 with 'town': {what} is baked as {townKind} (got {town.Kind})");
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
    C(BakeCls(new PrefabFacts("bench", [.. BakeWallParts, "Chair"], false, 1, false), 7, new BakeWords { Town = true }).Kind == BakeKind.Seat, "... and with 'town' it is a Seat that gives no comfort (0.10.4 decor)");
    foreach (var safe in BakeClassifier.StaticSafe)
      C(BakeCls(new PrefabFacts("safe", [.. BakeWallParts, safe])).Kind == BakeKind.Static, $"StaticSafe: {safe} alone keeps a piece a Static");
    C(BakeCls(BakePrefab("rich", "MaterialVariation", "RandomMaterialValues", "StaticPhysics", "LodFadeInOut", "HoverText", "DisableInPlacementGhost", "ImpactEffect", "UnityEngine.LODGroup", "UnityEngine.SkinnedMeshRenderer", "RandomPieceRotation")).Kind == BakeKind.Static,
      "every StaticSafe component together is still a Static");
    C(BakeClassifier.StaticSafe.Count == 21, $"StaticSafe is the 10 game components, SimpleMeshCombine, RandomPieceRotation and 9 of Unity's (it has {BakeClassifier.StaticSafe.Count})");

    Section("classifier: the new overload and the 3-argument form agree");
    C(BakeClassifier.Classify(wall, 7, default).Kind == BakeKind.Static && BakeClassifier.Classify(wall, 0, default).Kind == BakeKind.NotByPlayer,
      "the 3-argument form of 13.2 is the 4-argument form with an object that says nothing");
  }

  private static PrefabFacts BakeLight(string name, bool burning = false, int comfort = 0, params string[] extra) =>
    new(name, BakeWallParts.Concat(extra), false, comfort, false, 0f, burning);

  private static readonly BakeWords BakeTown = new() { Town = true };

  // 0.10.4 decor, the user's decisions of 2026-10-08: with 'town', comfort alone is decor, light and fire alone is a lit copy, a fire you
  // cook on stays; RandomPieceRotation is StaticSafe. Without 'town' nothing of it applies.
  private static void BakeClassifierDecorTest()
  {
    Section("classifier: comfort decor with 'town'");
    var rug = BakeLight("rug", false, 1);
    C(BakeCls(rug).Kind == BakeKind.Stays && BakeCls(rug).Group == BakeClassifier.Comfort, "without 'town' a rug stays real (comfort)");
    var baked = BakeCls(rug, 7, BakeTown);
    C(baked.Kind == BakeKind.Static && baked.Baked && baked.Group.Length == 0 && baked.Note == BakeClassifier.NoteComfort && baked.Reason.Contains("gives none"),
      $"with 'town' a rug is a Static decor that says it gives no comfort (got {baked.Kind}, '{baked.Reason}')");
    var chair = BakeLight("mod_chair", false, 2, "Chair");
    var seat = BakeCls(chair, 7, BakeTown);
    C(BakeCls(chair).Kind == BakeKind.Stays && seat.Kind == BakeKind.Seat && seat.Note == BakeClassifier.NoteComfort, "a mod chair with comfort: real without 'town', a Seat decor with it");
    var vanillaChair = BakeLight("piece_chair02", false, 2, "Chair");
    C(BakeCls(vanillaChair, 7, BakeTown).Kind == BakeKind.Seat, "the game's chair is the same");
    C(BakeCls(BakeLight("armorstand", false, 1, "ArmorStand", "Switch", "UnityEngine.Animator", "VisEquipment"), 7, BakeTown).Kind == BakeKind.Adopt, "an armor stand has its own components: it stays real, adopted (a holder)");
    C(BakeCls(BakeLight("itemstand", false, 1, "ItemStand"), 7, BakeTown) is { Kind: BakeKind.Adopt, Group: BakeClassifier.Comfort }, "an item stand stays real, adopted, in the comfort group");
    C(BakeCls(BakeLight("barber", false, 1, "Barber"), 7, BakeTown).Kind == BakeKind.Adopt, "a barber chair (its own component) stays real");
    C(BakeCls(BakeLight("cushion_chest", false, 1, "Container"), 7, BakeTown) is { Kind: BakeKind.Adopt, Group: BakeClassifier.Chests }, "comfort with a Container is a chest that stays");
    C(BakeCls(BakeLight("cosy_bed", false, 2, "Bed"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with a Bed stays");
    C(BakeCls(BakeLight("cosy_bench", false, 2, "CraftingStation"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with a station stays");
    C(BakeCls(BakeLight("cosy_sign", false, 1, "Sign"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with a Sign stays");
    C(BakeCls(BakeLight("cosy_lamp", false, 1, "UnityEngine.Light", "LightLod"), 7, BakeTown).Kind == BakeKind.Copy,
      "comfort with a Light is a lit copy with 'town' (the user bakes both decor and lights: a dvergr lantern, a fairy-light garland)");
    C(BakeCls(BakeLight("cosy_lamp", false, 1, "UnityEngine.Light", "LightLod"), 7, default).Kind == BakeKind.Stays, "... and without 'town' it stays real");
    C(BakeCls(BakeLight("cosy_fire", false, 1, "Fireplace"), 7, BakeTown).Kind == BakeKind.Copy, "comfort with a Fireplace that has no Burning area is a lit copy with 'town'");
    C(BakeCls(BakeLight("cosy_chair_lamp", false, 1, "UnityEngine.Light", "Chair"), 7, BakeTown).Kind == BakeKind.Adopt, "a chair with a Light stays real (a chair is sat on)");
    C(BakeCls(BakeLight("cosy_area", false, 1, "EffectArea"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with an EffectArea stays real");
    C(BakeCls(BakeLight("cosy_mod", false, 1, "MyMod.Thing"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with an active component this code has never heard of stays real");
    C(BakeCls(BakeLight("cosy_ps", false, 1, "UnityEngine.ParticleSystem"), 7, BakeTown).Kind == BakeKind.Adopt, "comfort with a ParticleSystem alone stays real (no light, no fire)");
    C(BakeCls(rug, 7, BakeTown, new ObjectFacts { HasConnection = true }).Kind == BakeKind.Adopt, "comfort with connection data in the ZDO stays real");
    C(BakeCls(rug, 7, BakeTown, new ObjectFacts { AlreadyBaked = true }).Kind == BakeKind.AlreadyBaked, "rule 3 still comes first");
    C(BakeCls(rug, 0, BakeTown).Kind == BakeKind.NotByPlayer, "rule 4 still comes first: a rug nobody built is not touched");
    C(BakeCls(rug, 0, new BakeWords { Town = true, Any = true }).Kind == BakeKind.Static, "... and with 'any' it is baked");
    C(BakeCls(BakeLight("rugger", false, 1, "ItemDrop"), 7, BakeTown).Kind == BakeKind.Consumable, "a consumable is never baked, comfort or not");
    var inactiveOnly = BakePrefabInactive("cosy_hidden", [], ["MyMod.Thing"]);
    C(BakeCls(new PrefabFacts("cosy_hidden", inactiveOnly.Components, false, 1, false), 7, BakeTown).Kind == BakeKind.Static, "a component only in a part that is inactive in the prefab does not keep decor real");

    Section("classifier: lights and fires with 'town' are lit copies");
    var lantern = BakeLight("hoodedlantern", false, 0, "EffectArea", "LightFlicker", "LightLod", "UnityEngine.Light", "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer");
    C(BakeCls(lantern).Kind == BakeKind.Stays && BakeCls(lantern).Group == BakeClassifier.Lights, "without 'town' a lantern stays real");
    var copy = BakeCls(lantern, 7, BakeTown);
    C(copy.Kind == BakeKind.Copy && copy.Baked && copy.Group.Length == 0 && copy.Note == BakeClassifier.NoteLit && copy.Reason.Contains("never needs fuel") && !copy.StaysReal && !copy.Untouched,
      $"with 'town' it is a Copy that says it never needs fuel (got {copy.Kind}, '{copy.Reason}')");
    var wallTorch = BakeLight("piece_walltorch", false, 0, "EffectArea", "Fireplace");
    C(BakeCls(wallTorch).Kind == BakeKind.Stays && BakeCls(wallTorch).Group == BakeClassifier.Fires, "without 'town' a wall torch stays real, in 'fires and torches'");
    C(BakeCls(wallTorch, 7, BakeTown).Kind == BakeKind.Copy, "a wall torch (a Fireplace with no Burning area) is a Copy with 'town'");
    var groundTorch = BakeLight("piece_groundtorch", false, 0, "EffectArea", "Fireplace", "LightFlicker", "LightLod", "TimedDestruction", "UnityEngine.AudioSource",
      "UnityEngine.Light", "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer", "ZSFX");
    C(BakeCls(groundTorch, 7, BakeTown).Kind == BakeKind.Copy, "an iron ground torch (all of the light-and-fire set, with the sound's TimedDestruction) is a Copy");
    var fancy = BakeLight("BFP_Lantern1", false, 0, "LightFlicker", "LightLod", "UnityEngine.Animator", "UnityEngine.AudioSource", "UnityEngine.Light", "UnityEngine.ParticleSystem",
      "UnityEngine.ParticleSystemRenderer", "UnityEngine.SpriteRenderer", "ZSFX");
    C(BakeCls(fancy, 7, BakeTown).Kind == BakeKind.Copy, "a Fine Wood lantern (an Animator and a SpriteRenderer too) is a Copy");
    C(BakeCls(BakeLight("candle", false, 0, "UnityEngine.Light"), 7, BakeTown).Kind == BakeKind.Copy, "a candle with a Light alone is a Copy");

    Section("classifier: a fire you cook on stays real");
    var firePit = BakeLight("fire_pit", true, 0, "EffectArea", "CinderSpawner", "Fireplace");
    C(BakeCls(firePit, 7, BakeTown) is { Kind: BakeKind.Adopt, Group: BakeClassifier.Fires }, "a Fireplace with a Burning EffectArea stays real, adopted, in 'fires and torches'");
    C(BakeCls(BakeLight("brazier", true, 0, "EffectArea", "Fireplace"), 7, BakeTown).Kind == BakeKind.Adopt, "a brazier (Burning area) stays");
    C(BakeCls(BakeLight("pure_light_cooks", true, 0, "UnityEngine.Light"), 7, BakeTown) is { Kind: BakeKind.Adopt, Group: BakeClassifier.Lights }, "a light that carries a Burning area stays real too (any piece a cooking station could find)");
    C(BakeCls(BakeLight("fire_pit_unlit", false, 0, "EffectArea", "Fireplace"), 7, BakeTown).Kind == BakeKind.Copy, "the same fire without a Burning area is a Copy: the flag decides, not the name");
    C(PrefabFacts.IsBurning((EffectArea.Type)8) && PrefabFacts.IsBurning((EffectArea.Type)(8 | 1)) && !PrefabFacts.IsBurning((EffectArea.Type)(1 | 2)) && !PrefabFacts.IsBurning(EffectArea.Type.PlayerBase) && !PrefabFacts.IsBurning(EffectArea.Type.None),
      "the Burning flag is bit 8 (a fire pit's FireBurn is 8; Heat|Fire, Fire and PlayerBase are not)");
    var burningFacts = new PrefabFacts("x", [new ComponentFact("Fireplace")], false, 0, false, 0f, true);
    C(burningFacts.BurningArea && !new PrefabFacts("y", [new ComponentFact("Fireplace")]).BurningArea, "PrefabFacts carries the Burning flag, false by default");

    Section("classifier: what else keeps a light real");
    foreach (var (name, parts) in new (string, string[])[]
    {
      ("portal", ["EffectArea", "TeleportWorld", "UnityEngine.Light", "LightFlicker", "UnityEngine.AudioSource", "UnityEngine.ParticleSystem"]),
      ("guard_stone", ["PrivateArea", "CircleProjector", "EffectArea", "UnityEngine.Light", "LightLod"]),
      ("shieldgenerator", ["ShieldGenerator", "Switch", "EffectArea", "UnityEngine.Light", "LightLod"]),
      ("turret", ["Turret", "EffectArea", "UnityEngine.Light"]),
      ("wisplure", ["WispSpawner", "GuidePoint", "UnityEngine.Light", "LightFlicker"]),
      ("groundtorch_mist", ["Demister", "EffectArea", "UnityEngine.Light", "LightFlicker"]),
      ("eternal_pyre", ["WispSpawner", "EffectArea", "UnityEngine.Light", "TimedDestruction"]),
      ("clay_collector", ["FineWoodPieces.Functions.ClayCollector", "UnityEngine.Light", "LightLod"]),
      ("torch_door", ["Door", "UnityEngine.Light"]),
      ("torch_chest", ["Container", "UnityEngine.Light"]),
      ("torch_bed", ["Bed", "UnityEngine.Light"]),
      ("torch_forge", ["CraftingStation", "Fireplace", "UnityEngine.Light"]),
      ("torch_sign", ["Sign", "UnityEngine.Light"]),
      ("torch_chair", ["Chair", "UnityEngine.Light"]),
      ("torch_destructible", ["Destructible", "Fireplace"]),
      ("torch_spawner", ["CinderSpawner", "Fireplace"]),
      ("torch_aoe", ["Aoe", "Fireplace"]),
      ("mod_light", ["MyMod.Lamp", "UnityEngine.Light"]),
      ("missing_script", ["(missing script)", "UnityEngine.Light"]),
    })
    {
      var c = BakeCls(BakeLight(name, false, 0, parts), 7, BakeTown);
      C(c.Kind == BakeKind.Adopt, $"{name} keeps another reason to stay real: adopted with 'town' (got {c.Kind})");
    }
    C(BakeCls(BakeLight("area_only", false, 0, "EffectArea"), 7, BakeTown).Kind == BakeKind.Adopt, "an EffectArea alone is no light and no fire: it stays");
    C(BakeCls(BakeLight("smoke_only", false, 0, "UnityEngine.ParticleSystem", "ZSFX"), 7, BakeTown).Kind == BakeKind.Adopt, "smoke and a sound alone are no light and no fire: it stays");
    C(BakeCls(BakeLight("lamp_linked", false, 0, "UnityEngine.Light"), 7, BakeTown, new ObjectFacts { HasConnection = true }).Kind == BakeKind.Adopt, "a light with connection data stays");
    C(BakeCls(BakeLight("lamp_cart", false, 0, "UnityEngine.Light", "Vagon"), 7, BakeTown).Kind == BakeKind.Moves, "a lamp on a cart moves: not touched (rule 5 first)");
    C(BakeCls(BakeLight("lamp_ghost", false, 0, "UnityEngine.Light", "ItemDrop"), 7, BakeTown).Kind == BakeKind.Consumable, "a placed lamp item is a consumable, never baked (rule 3b first)");
    C(BakeCls(BakeLight("lamp_nocreator", false, 0, "UnityEngine.Light"), 0, BakeTown).Kind == BakeKind.NotByPlayer, "a lamp nobody built is not touched (rule 4 first)");
    C(BakeCls(BakeLight("lamp_nocreator", false, 0, "UnityEngine.Light"), 0, new BakeWords { Town = true, Any = true }).Kind == BakeKind.Copy, "... and with 'any' it is a Copy");
    // the walltorch of the game: only a Fireplace and an EffectArea in its active parts; its Light and flame are in the part that is switched on when lit
    var litPart = new PrefabFacts("piece_walltorch", BakeWallParts.Select(c => new ComponentFact(c)).Concat(
      [new ComponentFact("Fireplace"), new ComponentFact("EffectArea"), new ComponentFact("UnityEngine.Light", false), new ComponentFact("MyMod.Flame", false)]));
    C(BakeCls(litPart, 7, BakeTown).Kind == BakeKind.Copy, "a Light and any component in the part that is inactive until lit do not keep a torch real");
    C(BakeCls(litPart).Kind == BakeKind.Stays, "... and without 'town' it stays");
    // a door, a container, a bed, a sign, a station or a chair counts wherever it is, an inactive part too
    foreach (var hidden in new[] { "Door", "Container", "Bed", "Sign", "CraftingStation", "Smelter", "Chair" })
    {
      var p = new PrefabFacts("hidden_" + hidden, BakeWallParts.Select(c => new ComponentFact(c)).Concat([new ComponentFact("Fireplace"), new ComponentFact(hidden, false)]));
      C(BakeCls(p, 7, BakeTown).Kind == BakeKind.Adopt, $"a torch with a {hidden} in an inactive part still stays (it counts wherever it is)");
    }

    Section("classifier: RandomPieceRotation");
    var block = BakePrefab("blackmarble_1x1", "RandomPieceRotation");
    C(BakeCls(block).Kind == BakeKind.Static && BakeCls(block, 7, BakeTown).Kind == BakeKind.Static, "RandomPieceRotation is a Static with and without 'town'");
    C(BakeCls(BakePrefab("rotating_door", "RandomPieceRotation", "Door")).Kind == BakeKind.Stays, "... but a door that carries one stays");
    C(BakeCls(BakePrefab("rotating_stool", "RandomPieceRotation", "Chair")).Kind == BakeKind.Seat, "a chair that carries one is a Seat");
    C(BakeCls(BakeLight("rotating_lamp", false, 0, "RandomPieceRotation", "UnityEngine.Light"), 7, BakeTown).Kind == BakeKind.Copy, "a lamp that carries one is a Copy with 'town'");
    C(BakeCls(BakeLight("rotating_rug", false, 1, "RandomPieceRotation"), 7, BakeTown).Kind == BakeKind.Static, "rotating decor is Static decor with 'town'");

    Section("classifier: the dry run's lines for decor and lit copies");
    var tally = new BakeTally();
    void Add(PrefabFacts prefab, int times, BakeWords words = default)
    {
      for (int i = 0; i < times; i++)
        tally.Add(BakeCls(prefab, 7, words), prefab.Name, 7);
    }
    Add(BakePrefab("stone_wall_2x1"), 100, BakeTown);
    Add(rug, 6, BakeTown);
    Add(vanillaChair, 4, BakeTown);
    Add(wallTorch, 9, BakeTown);
    Add(lantern, 3, BakeTown);
    Add(firePit, 2, BakeTown);
    Add(BakePrefab("door", "Door"), 5, BakeTown);
    var lines = tally.Lines(BakeTown).ToList();
    C(lines.Count == 4, $"the found, comfort, lights and town lines, and no untouched line (got {lines.Count}: {string.Join(" | ", lines)})");
    C(lines.ElementAtOrDefault(0) == "Found 129 pieces. To bake: 122 (stone_wall_2x1 100, piece_walltorch 9, rug 6 and 2 more kinds).", "the found line counts every record: " + lines.ElementAtOrDefault(0));
    C(lines.ElementAtOrDefault(1) == "Comfort: 10 become decor (rug 6, piece_chair02 4). A baked piece gives no comfort.", "the comfort line: " + lines.ElementAtOrDefault(1));
    C(lines.ElementAtOrDefault(2) == "Lights: 12 become lit copies (piece_walltorch 9, hoodedlantern 3). They never need fuel and give no warmth; fires you cook on stay real pieces.", "the lights line: " + lines.ElementAtOrDefault(2));
    C(lines.ElementAtOrDefault(3) == "Town pieces: 7 (doors 5, fires and torches 2). They stay real pieces and become protected parts of the layer.", "the town line holds only what stays: " + lines.ElementAtOrDefault(3));
    C(tally.Records == 122 && tally.Seats == 4 && tally.ComfortDecorCount == 10 && tally.LitCopyCount == 12 && tally.StayCount == 7, "records 122 (4 seats), decor 10, lit copies 12, stay 7");
    var plain = new BakeTally();
    plain.Add(BakeCls(rug), "rug", 7);
    plain.Add(BakeCls(lantern), "lantern", 7);
    C(plain.Lines(default).Count() == 2 && plain.ComfortDecorCount == 0 && plain.LitCopyCount == 0 && plain.Records == 0, "without 'town' neither line appears and nothing is baked");
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
    var townClasses = new Dictionary<string, BakeClass>();
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
      C(cls.Baked == (cls.Group.Length == 0 && cls.Kind is BakeKind.Static or BakeKind.Seat or BakeKind.Copy), $"{name}: a record has no group, the others have one");
      var town = BakeClassifier.Classify(facts, 1, new BakeWords { Town = true }, default);
      townClasses[name] = town;
      C(town.Reason.Length > 0 && Enum.IsDefined(typeof(BakeKind), town.Kind), $"{name} has a class and a reason with 'town' ({town})");
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

    // ---- with 'town' (0.10.4 decor). The dump lists root components only, and it cannot say which fires carry a Burning EffectArea (the area
    // is in the part that is inactive until the fire is lit, and the dump has no children). That fact was read from the game's own asset
    // bundles (2026-10-08, UnityPy over valheim_Data/StreamingAssets/SoftRef/Bundles, every EffectArea's m_type & 8): these eleven vanilla
    // pieces carry a Burning area, the ones CookingStation.IsFireLit and CraftingStation.m_haveFire look for. The classifier reads the same
    // flag from the live prefab (PrefabFacts.Read), so the phase Q census shows it for the mods' fires too.
    string[] cooking = ["BogWitch_Fire_Pit", "Morkhalla_firepit", "bonfire", "fire_pit", "fire_pit_haldor", "fire_pit_hildir", "fire_pit_iron", "hearth",
      "piece_brazierceiling01", "piece_brazierfloor01", "piece_brazierfloor02"];
    var withFlag = new Dictionary<string, BakeClass>();
    foreach (var prefab in doc.RootElement.EnumerateArray())
    {
      var name = prefab.GetProperty("Name").GetString()!;
      var components = prefab.GetProperty("Components").EnumerateArray().Select(c => c.GetProperty("ComponentType").GetString()!).Distinct().ToList();
      if (!components.Contains("Piece"))
        continue;
      extra.TryGetValue(name, out var more);
      var facts = new PrefabFacts(name, components, components.Contains("UnityEngine.Rigidbody"), more.Comfort, more.SyncsScale, 0f, cooking.Contains(name));
      withFlag[name] = BakeClassifier.Classify(facts, 1, new BakeWords { Town = true }, default);
    }
    var townSummary = string.Join(", ", withFlag.Values.GroupBy(c => c.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
    System.Console.WriteLine("   the 535 vanilla pieces with 'town' (root components, the eleven cooking fires flagged): " + townSummary);
    void Town(string name, BakeKind kind, string? group = null, string? why = null)
    {
      if (!withFlag.TryGetValue(name, out var cls))
      {
        C(false, $"{name} is in the dump");
        return;
      }
      C(cls.Kind == kind && (group == null || cls.Group == group), $"with 'town', {name}: {why ?? kind.ToString()} (got {cls})");
    }
    Town("piece_chair02", BakeKind.Seat, null, "a chair that gives comfort is a Seat decor");
    Town("piece_bench01", BakeKind.Static, null, "a bench is Static decor here: its Chair is in a child, which the dump (root components) does not list");
    Town("rug_wolf", BakeKind.Static, null, "a rug is Static decor");
    foreach (var cookingFire in cooking.Where(withFlag.ContainsKey))
      Town(cookingFire, BakeKind.Adopt, BakeClassifier.Fires, "a fire to cook on stays real, adopted");
    Town("piece_walltorch", BakeKind.Copy, null, "a wall torch is a lit copy");
    Town("piece_groundtorch_wood", BakeKind.Copy);
    Town("wood_door", BakeKind.Adopt, BakeClassifier.Doors);
    Town("piece_chest", BakeKind.Adopt, BakeClassifier.Chests);
    Town("bed", BakeKind.Adopt, BakeClassifier.Beds);
    Town("piece_workbench", BakeKind.Adopt, BakeClassifier.Stations);
    Town("sign", BakeKind.Adopt, BakeClassifier.Signs);
    Town("path", BakeKind.GroundWork);
    Town("Cart", BakeKind.Moves);
    // the dump's root components cannot see the Fireplace-less lights' children, so no more is asserted than: every Copy has a Fireplace or a Light
    // at its root, and none of the cooking fires, doors, chests, beds or stations is one
    var copies = withFlag.Where(p => p.Value.Kind == BakeKind.Copy).Select(p => p.Key).ToList();
    C(copies.Count > 0 && copies.All(n => !cooking.Contains(n)), $"{copies.Count} vanilla pieces become lit copies (root components), none a cooking fire: {string.Join(", ", copies)}");
  }
}

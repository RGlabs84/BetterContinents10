// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The pieces of a baked layer and zone regeneration (ZoneReset.Kind.Baked, ZoneRegen.KindOf): an object with bc_bake_id survives a
// reset and does not keep its zone from being reset, and a creator it has (a piece an in-game bake adopted keeps its own) still
// protects as on any piece. The whole thing on a made-up world built from the game's own classes, as Program.World.cs does it.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using Kind = BetterContinents.ZoneReset.Kind;
using Rules = BetterContinents.ZoneReset.Rules;

internal static partial class Program
{
  private static readonly int BakeIdHash = "bc_bake_id".GetStableHashCode();

  // The key BakedServer writes: the u32 id of the piece's record, as an int (unchecked), so that any value, 0 included, says "baked".
  private static ZDO Baked(ZDO zdo, int id = 7)
  {
    ZDOExtraData.Set(zdo.m_uid, BakeIdHash, id);
    return zdo;
  }

  private static void BakedTests()
  {
    BakedRulesTests();
    BakedKindTests();
    BakedRegenerationTests();
    BakedProtectTests();
    BakedSeedTests();
  }

  private static void BakedRulesTests()
  {
    Section("baked pieces: the rules");
    C(Rules.Survives(Kind.Baked), "a baked piece is never destroyed by a reset");
    C(!Rules.Protects(Kind.Baked), "a baked piece alone does not keep its zone from being reset");
    C(Rules.Protects(Kind.Baked | Kind.Piece), "a baked piece that has a creator protects, as any piece does");
    C(Rules.Survives(Kind.Baked | Kind.Piece) && Rules.Survives(Kind.Baked | Kind.Tamed), "kinds combine");
    C(!Rules.Survives(Kind.None) && !Rules.Protects(Kind.None), "nothing else changed: an object of no kind goes with its zone and protects nothing");
    C(((int)Kind.Baked & (int)(Kind.Player | Kind.Local | Kind.Interior | Kind.Tombstone | Kind.Piece | Kind.Tamed | Kind.Ground)) == 0, "Baked is a bit of its own");
    C(Kind.Baked == (Kind)128, "and is 128, as the build spec has it");
  }

  private static void BakedKindTests()
  {
    Section("baked pieces: what kind of object ZoneRegen.KindOf says it is");
    ZDOExtraData.Reset();
    var local = new ZDOID(600L, 1);
    C(ZoneRegen.KindOf(Baked(NewZdo("piece_workbench", 1)), local) == Kind.Baked, "an object with bc_bake_id is baked");
    C(ZoneRegen.KindOf(Baked(NewZdo("piece_workbench", 2), 0), local) == Kind.Baked, "whatever its id: 0 is an id too");
    C(ZoneRegen.KindOf(Baked(NewZdo("door", 3), -1), local) == Kind.Baked, "and a u32 id that is negative as an int");
    C(ZoneRegen.KindOf(NewZdo("piece_workbench", 4), local) == Kind.None, "the same prefab without the key is nothing special");
    ZDOExtraData.Set(new ZDOID(1000L, 5), "bc_bake_src".GetStableHashCode(), 1);
    C(ZoneRegen.KindOf(NewZdo("piece_workbench", 5), local) == Kind.None, "bc_bake_src alone is not it: the id is");
    C(ZoneRegen.KindOf(Creator(Baked(NewZdo("piece_chest", 6)), 42L), local) == (Kind.Baked | Kind.Piece), "a baked piece an in-game bake adopted keeps its creator: both");
    C(ZoneRegen.KindOf(Tame(Baked(NewZdo("Boar", 7))), local) == (Kind.Baked | Kind.Tamed), "kinds combine");
    C(ZoneRegen.KindOf(Baked(NewZdo("Pine", 8)), local) == Kind.Baked, "the key decides, not the prefab");
  }

  // A made-up world: three zones that are generated (a town zone with trees and baked pieces; a zone with a player's chest beside
  // it and a baked piece; a zone with a baked piece that has a creator), and one run of the regeneration.
  private static void BakedRegenerationTests()
  {
    Section("baked pieces: a regeneration keeps them and still resets their zone");
    using var seams = new Seams();
    ZoneRegen.DeltaTime = () => 1f / 60f;
    ZoneRegen.FrameClock = () => 0.0;
    ZDOExtraData.Reset();
    var finishDefault = ZoneRegen.Finish;
    ZoneRegen.Finish = () => { };
    var w = new World();
    try
    {
      var town = Z(0, 0);
      var townTrees = new[] { w.Add(town, "Pine", dx: -9f, dz: 4f), w.Add(town, "Pine", dx: 8f, dz: -3f), w.Add(town, "Rock_3", dx: 2f) };
      var ctrl = w.Add(town, "_ZoneCtrl");
      var door = Baked(w.Add(town, "wood_door", dx: 3f, dz: 3f), 11);
      var forge = Baked(w.Add(town, "forge", dx: -3f, dz: 5f), 0);
      var chest = Baked(w.Add(town, "piece_chest", dx: 5f, dz: -5f), 12);
      w.Generated.Add(town);

      // Far from it: a zone with a player's chest, and one with a piece an in-game bake adopted (it keeps its creator).
      var mine = Z(6, 0);
      var mineTree = w.Add(mine, "Pine", dx: 4f);
      Creator(w.Add(mine, "piece_chest", dx: -2f), 42L);
      Baked(w.Add(mine, "forge", dx: 6f), 21);
      w.Generated.Add(mine);
      var beside = Z(7, 0);
      var besideTree = w.Add(beside, "Pine");
      w.Generated.Add(beside);
      var adopted = Z(0, 6);
      var adoptedTree = w.Add(adopted, "Pine", dx: 4f);
      Creator(Baked(w.Add(adopted, "piece_chest", dx: -2f), 31), 42L);
      w.Generated.Add(adopted);
      // A zone with nothing in it but a baked piece: it is reset all the same.
      var lone = Z(-6, 0);
      var loneDoor = Baked(w.Add(lone, "wood_door"), 41);
      w.Generated.Add(lone);

      var (lines, _, job) = Regenerate(w);
      var queued = w.All.ToHashSet();
      C(!w.Generated.Contains(town) && !w.Generated.Contains(lone), "a town zone is reset, and so is a zone with nothing in it but a baked piece");
      C(townTrees.All(t => queued.Contains(t.m_uid)) && queued.Contains(ctrl.m_uid), "its trees and rocks and its zone control go, as in any zone");
      C(!queued.Contains(door.m_uid) && !queued.Contains(forge.m_uid) && !queued.Contains(chest.m_uid) && !queued.Contains(loneDoor.m_uid),
        "the baked pieces stay: a door, a forge with id 0, a chest, a lone door");
      C(w.Generated.Contains(mine) && w.Generated.Contains(beside) && w.Generated.Contains(adopted), "the zone with the player's chest, the zone beside it and the zone with the adopted piece are left alone");
      C(!queued.Contains(mineTree.m_uid) && !queued.Contains(besideTree.m_uid) && !queued.Contains(adoptedTree.m_uid), "nothing in them is destroyed");
      C(job.Progress.Reset == 2 && job.Progress.Kept == 4, $"two zones reset, four objects stay in them ({job.Progress.Reset}, {job.Progress.Kept})");
      C(job.Progress.Destroyed == townTrees.Length + 1, $"only the town's own four objects are destroyed ({job.Progress.Destroyed})");
      C(job.Errors == 0 && lines.Count > 0, "no errors");

      // The same run, a second time, with the pieces already standing and the zone generated again: nothing more goes.
      var before = w.All.Count();
      w.Flush();
      w.Add(town, "Pine", dx: 1f);
      w.Generated.Add(town);
      var (_, _, job2) = Regenerate(w);
      C(job2.Progress.Reset >= 1 && w.All.Count() - before == 1 && w.LiveIn(town).Contains(door) && w.LiveIn(town).Contains(forge), "a second run resets the refilled zone, destroying the new tree and not the baked pieces");
    }
    finally
    {
      ZoneRegen.Finish = finishDefault;
      World.Close();
    }
  }
}

// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// The Decor palette flag (32): a compiler marks a consumable kind (a prefab with a Pickable, PickableItem, ItemDrop or Plant) as scenery. Its records
// are drawn by the client like any Static or Copy and are never placed as real objects, never pickable. An entry without the flag is a consumable
// exactly as before; the flag on a prefab that is no consumable does nothing; on a Live or Seat entry the reader refuses it. The in-game bake never
// writes it. The server's side (IsConsumable, SeedZone, the vegetation clean-up) is in zone-tests (Program.BakedDecor.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // ------------------------------------------------------------------------------------------------ the format

  private static void DecorFlagFormatTest()
  {
    Section("decor flag: palette flag 32 is read and written, and every other unknown bit is still refused");
    C((int)PaletteFlags.Decor == 32 && (int)PaletteFlags.All == 63 && (PaletteFlags.All & PaletteFlags.Decor) != 0, "Decor is flag 32 and All is 63");

    var edit = LayerEdit.New("decor flag");
    var flaxDecor = Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor);
    var mushroomCopy = Entry("Pickable_Mushroom", BakedRole.Copy, BakedCollision.Prefab, PaletteFlags.Decor | PaletteFlags.NoCopyLight | PaletteFlags.NoShadows);
    var flaxReal = Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None);
    int iDecor = edit.PaletteIndexFor(flaxDecor), iCopy = edit.PaletteIndexFor(mushroomCopy), iReal = edit.PaletteIndexFor(flaxReal);
    C(iDecor != iReal && edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor)) == iDecor,
      "the same prefab with and without the flag are two entries, and the flagged one is found again");
    edit.AddRecords([ZoneRecord.CreateYaw(iDecor, 3.5, 40, 4.5, 0), ZoneRecord.CreateYaw(iCopy, 7.5, 40, 4.5, 0), ZoneRecord.CreateYaw(iReal, 9.5, 40, 4.5, 0)]);
    var made = edit.Build().Layer;
    var read = BakedLayer.Read(made.Bytes, made.Length);
    C(read.Palette.Count == 3 && read.Palette[iDecor].Flags == PaletteFlags.Decor && read.Palette[iCopy].Flags == (PaletteFlags.Decor | PaletteFlags.NoCopyLight | PaletteFlags.NoShadows)
      && read.Palette[iReal].Flags == PaletteFlags.None, "the flags round-trip through the writer and the reader");
    C(read.Palette[iDecor].Equals(flaxDecor) && read.Palette[iCopy].Equals(mushroomCopy) && read.Palette[iReal].Equals(flaxReal) && !read.Palette[iDecor].Equals(read.Palette[iReal]),
      "the entries read back are the entries written, and the flag tells two entries apart");
    C(read.Palette[iDecor].IsDecor && read.Palette[iCopy].IsDecor && !read.Palette[iReal].IsDecor && read.Placements == 3, "IsDecor says it for the flagged Static and Copy entries only");

    // The second writer's bytes: a palette flag of 32 reads, alone and with the others.
    foreach (var (flags, role, what) in new (byte, byte, string)[] { (32, 0, "alone on a Static entry"), (32 | 1 | 2 | 8 | 16, 0, "with four others on a Static entry"), (32, 2, "on a Copy entry") })
    {
      var raw = ValidRaw();
      raw.Palette[0].Flags = flags;
      raw.Palette[0].Role = role;
      var bytes = raw.Build(out int length);
      var layer = BakedLayer.Read(bytes, length);
      C(layer.Palette[0].Flags == (PaletteFlags)flags && layer.Palette[0].IsDecor, $"a reader accepts flag 32 {what}");
    }
    // Still refused: a bit this version does not know, also next to Decor.
    var unknown = ValidRaw();
    unknown.Palette[0].Flags = 64;
    var unknownBytes = unknown.Build(out int unknownLength);
    C(Refuses(() => BakedLayer.Read(unknownBytes, unknownLength), "palette flag bit 6", "newer"), "refused: palette flag 64 (bit 6)");
    unknown = ValidRaw();
    unknown.Palette[0].Flags = 32 | 64;
    unknownBytes = unknown.Build(out unknownLength);
    C(Refuses(() => BakedLayer.Read(unknownBytes, unknownLength), "palette flag bit 6", "newer"), "refused: 32 with 64 (bit 6)");
    unknown = ValidRaw();
    unknown.Palette[0].Flags = 128;
    unknownBytes = unknown.Build(out unknownLength);
    C(Refuses(() => BakedLayer.Read(unknownBytes, unknownLength), "palette flag bit 7", "newer"), "refused: palette flag 128 (bit 7)");
  }

  private static void DecorFlagRolesTest()
  {
    Section("decor flag: only a Static or Copy entry can be decor; a Live or Seat entry with the flag is refused, by the reader and by the writer");
    foreach (var (role, name) in new (byte, string)[] { (1, "Live"), (3, "Seat") })
    {
      var raw = ValidRaw();
      raw.Palette[0].Role = role;
      raw.Palette[0].Flags = 32;
      var bytes = raw.Build(out int length);
      C(Refuses(() => BakedLayer.Read(bytes, length), "palette entry 0", "decor", "Static or Copy", name), $"the reader refuses flag 32 on a {name} entry, naming the entry and the role");
    }
    var live = ValidRaw();
    live.Palette[1].Flags = 4 | 32;
    var liveBytes = live.Build(out int liveLength);
    C(Refuses(() => BakedLayer.Read(liveBytes, liveLength), "palette entry 1", "decor"), "also with Protect, on the second entry");

    foreach (var role in new[] { BakedRole.Live, BakedRole.Seat })
    {
      var edit = LayerEdit.New("decor roles");
      C(Refuses(() => edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", role, BakedCollision.Prefab, PaletteFlags.Decor)), "decor", "Static or Copy"),
        $"the writer will not write a {role} entry marked decor (it would not read back)");
    }
    var ok = LayerEdit.New("decor roles");
    C(ok.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor)) == 0
      && ok.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Copy, BakedCollision.None, PaletteFlags.Decor)) == 1, "and writes a Static and a Copy one");

    // An entry made in memory with the mix is not decor: it stays what it was before the flag.
    C(Entry("x", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor).IsDecor && Entry("x", BakedRole.Copy, BakedCollision.None, PaletteFlags.Decor).IsDecor
      && !Entry("x", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Decor).IsDecor && !Entry("x", BakedRole.Seat, BakedCollision.Prefab, PaletteFlags.Decor).IsDecor
      && !Entry("x", BakedRole.Static, BakedCollision.None).IsDecor, "IsDecor is true for Static and Copy with the flag and false for every other mix");
  }

  // ------------------------------------------------------------------------------------------------ the client

  private static void DecorFlagClientTest()
  {
    Section("decor flag, client: a decor entry is not a consumable (drawn, not skipped), an entry without the flag is, as before");
    var probe = BakedConsumables.Probe;
    using var scene = new ApiScene();
    try
    {
      foreach (var name in new[] { "Pickable_Flax_Wild", "Pickable_Mushroom", "Pine", "RaspberryBush", "Bush01" })
        scene.Add(name);
      GameObject Prefab(string name) => scene.Prefabs[name.GetStableHashCode()];
      var consumables = new[] { Prefab("Pickable_Flax_Wild"), Prefab("Pickable_Mushroom"), Prefab("RaspberryBush") };
      BakedConsumables.Probe = prefab => consumables.Any(c => ReferenceEquals(c, prefab));
      EntryDef Def(BakedRole role, PaletteFlags flags, params string[] names) => new() { Names = names, Anchors = names.Select(_ => Vector3.zero).ToArray(), Role = role, Flags = flags };
      BakedConsumables.SessionStarts();

      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.Decor, "Pickable_Flax_Wild")) == null, "a Static pickable marked decor is not a consumable here");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Copy, PaletteFlags.Decor | PaletteFlags.NoCopyLight, "Pickable_Mushroom")) == null, "nor a Copy one, whatever its other flags");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.None, "Pickable_Flax_Wild")) == "Pickable_Flax_Wild", "the same prefab without the flag is one, exactly as before");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Copy, PaletteFlags.NoShadows, "Pickable_Mushroom")) == "Pickable_Mushroom", "also with other flags");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.Decor, "Pine")) == null && BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.None, "Pine")) == null,
        "the flag on a prefab that is no consumable changes nothing: it is a Static either way");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Live, PaletteFlags.Decor, "Pickable_Mushroom")) == "Pickable_Mushroom" && BakedKinds.ConsumableOf(Def(BakedRole.Seat, PaletteFlags.Decor, "Pickable_Mushroom")) == "Pickable_Mushroom",
        "the flag does not count on a Live or Seat entry (a reader never lets one through; an entry made in memory stays a consumable)");
      C(BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.Decor, "RaspberryBush", "Bush01")) == null && BakedKinds.ConsumableOf(Def(BakedRole.Static, PaletteFlags.Decor, "Missing_Mod_Berry")) == null,
        "and a decor entry whose prefab is missing is not one either");

      // The kind the client resolves: Decor and not Consumable for the flagged entry; Consumable and not Decor without the flag.
      BakedKinds.ResetAll();
      BakedConsumables.SessionStarts();
      var decorKind = BakedKinds.Resolve(Def(BakedRole.Static, PaletteFlags.Decor, "Pickable_Flax_Wild"));
      C(decorKind.Decor && !decorKind.Consumable && decorKind.Role == BakedRole.Static, "the flagged entry resolves to a kind that is Decor and not Consumable");
      C(BakedConsumables.DecorKinds.SequenceEqual(["Pickable_Flax_Wild"]) && BakedConsumables.Kinds.Count == 0, "the session names it as a decor kind, not as a consumable");
      var realKind = BakedKinds.Resolve(Def(BakedRole.Static, PaletteFlags.None, "Pickable_Flax_Wild"));
      C(realKind.Consumable && !realKind.Decor && realKind != decorKind, "without the flag the same prefab resolves to a consumable kind (a different kind: the flags are part of the entry)");
      var treeKind = BakedKinds.Resolve(Def(BakedRole.Static, PaletteFlags.Decor, "Pine"));
      C(!treeKind.Decor && !treeKind.Consumable, "the flag on a tree makes neither");
      BakedConsumables.SessionStarts();
      C(BakedConsumables.DecorKinds.Count == 0, "a new session forgets the decor kinds");

      // The zone build: a decor record is built like any other (drawn, collided with as the file says); a consumable one is left out whole.
      var piece = ClientKit.NoLods("flax", 1f);
      KindBox[] box = [new KindBox(Vector3.zero, Vector3.one)];
      var decorFlax = ClientKit.Kind("Pickable_Flax_Wild", piece, collision: BakedCollision.Boxes, boxes: box);
      decorFlax.Decor = true;
      var decorCopy = ClientKit.Kind("Pickable_Mushroom", piece, role: BakedRole.Copy, collision: BakedCollision.Boxes, boxes: box);
      decorCopy.Decor = true;
      decorCopy.Prefab = ApiAlive<GameObject>();
      var realFlax = ClientKit.Kind("Pickable_Flax_Wild", piece, collision: BakedCollision.Boxes, boxes: box);
      realFlax.Consumable = true;
      var wall = ClientKit.Kind("stone_wall", ClientKit.NoLods("wall", 2f), collision: BakedCollision.Boxes, boxes: box);
      var records = new[] { (0, 4.0, 5.0, 6.0, 0.0, (Vector3?)null), (0, 8.0, 5.0, 6.0, 20.0, null), (1, 10.0, 5.0, 6.0, 0.0, null), (2, 12.0, 5.0, 6.0, 0.0, null), (3, 14.0, 5.0, 6.0, 0.0, null) };
      var zone = BakedZoneBuild.Build(ClientKit.Zone(0, 0, records), [decorFlax, decorCopy, realFlax, wall], 1);
      C(zone.Records == 5 && zone.Consumables == 1 && zone.DecorRecords == 3 && zone.Skipped == 0, $"the zone counts one consumable left out and three decor records built ({zone.Consumables} and {zone.DecorRecords})");
      C(zone.Drawn == 4 && zone.Kinds.Any(g => g.Kind == decorFlax && g.Count == 2) && zone.Kinds.Any(g => g.Kind == wall && g.Count == 1) && zone.Kinds.All(g => g.Kind != realFlax),
        $"the two decor flax and the wall are drawn, with the copy (unlit), and the consumable is not ({zone.Drawn} drawn)");
      C(zone.Colliders.Length == 1 && zone.Colliders[0].Triangles.Length == 4 * 36, "and the file's collision is built for the three decor records and the wall, not for the consumable");
      C(zone.Copies.Length == 1, "the decor Copy is a copy like any other");

      // bc_bake stats names them.
      var layer = MakeSample(92).Layer;
      var rig = new ClientLayerRig();
      BakedLayerStore.Changed += BakedClient.OnLayerChanged;
      try
      {
        BakedConsumables.SessionStarts();
        BakedLayerStore.Set(layer, null);
        BakedClient.Prepare();
        var lines = new List<string>();
        BakedClient.Stats(lines.Add);
        C(!lines.Any(l => l.Contains("decor")), "with none in view the stats do not mention decor kinds: " + string.Join(" | ", lines));
        BakedConsumables.NoteDecor("Pickable_Flax_Wild");
        BakedConsumables.NoteDecor("Pickable_Mushroom");
        BakedConsumables.NoteDecor("Pickable_Flax_Wild");
        var slot = BakedClient.SlotOf(new ZoneKey(0, 0));
        slot.Current = BuiltZone.Empty(0, 0, layer.Revision);
        slot.Current.DecorRecords = 7;
        lines.Clear();
        BakedClient.Stats(lines.Add);
        C(lines.Any(l => l.Contains("2 decor kinds") && l.Contains("Pickable_Flax_Wild") && l.Contains("7 of their records") && l.Contains("drawn as scenery") && l.Contains("not pickable")),
          "the stats name the decor kinds and count their records: " + string.Join(" | ", lines));
        C(!lines.Any(l => l.Contains("placed as real objects")), "and say nothing of consumables placed as real objects");
      }
      finally
      {
        BakedLayerStore.Changed -= BakedClient.OnLayerChanged;
        rig.Dispose();
      }
    }
    finally
    {
      BakedConsumables.Probe = probe;
      BakedConsumables.SessionStarts();
      BakedKinds.ResetAll();
    }
  }

  // ------------------------------------------------------------------------------------------------ the report

  private static void DecorFlagInfoTest()
  {
    Section("decor flag, report: bc_bake info keeps the decor kinds out of the consumable lines and gives them a line of their own");
    var (find, probe, view, placed, failed) = (BakedServer.FindPrefab, BakedConsumables.Probe, BakedServer.ViewOf, BakedConsumables.Placed, BakedConsumables.Failed);
    try
    {
      var prefabs = new Dictionary<string, GameObject>();
      foreach (var name in new[] { "Pickable_Flax_Wild", "Pickable_Dandelion", "Pickable_Mushroom", "stone_wall_2x1" })
        prefabs[name] = ApiAlive<GameObject>();
      BakedServer.FindPrefab = name => prefabs.TryGetValue(name, out var prefab) ? prefab : null;
      BakedConsumables.Probe = prefab => prefabs.Where(p => p.Key != "stone_wall_2x1").Any(p => ReferenceEquals(p.Value, prefab));
      BakedServer.ViewOf = _ => true;

      var edit = LayerEdit.New("decor info");
      int flaxReal = edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None));
      int flaxDecor = edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor));
      int dandelionDecor = edit.PaletteIndexFor(Entry("Pickable_Dandelion", BakedRole.Copy, BakedCollision.Prefab, PaletteFlags.Decor));
      int wallDecor = edit.PaletteIndexFor(Entry("stone_wall_2x1", BakedRole.Static, BakedCollision.Prefab, PaletteFlags.Decor));
      int mushroomLive = edit.PaletteIndexFor(Entry("Pickable_Mushroom", BakedRole.Live, BakedCollision.Prefab, PaletteFlags.Protect));
      var records = new List<ZoneRecord>();
      for (int i = 0; i < 4; i++)
        records.Add(ZoneRecord.CreateYaw(flaxReal, 3 + i, 40, 5, 0));
      for (int i = 0; i < 6; i++)
        records.Add(ZoneRecord.CreateYaw(flaxDecor, 3 + i, 40, 8, 0));
      for (int i = 0; i < 3; i++)
        records.Add(ZoneRecord.CreateYaw(dandelionDecor, 3 + i, 40, 11, 0));
      for (int i = 0; i < 5; i++)
        records.Add(ZoneRecord.CreateYaw(wallDecor, 3 + i, 40, 14, 0));
      records.Add(ZoneRecord.CreateYaw(mushroomLive, 20, 40, 5, 0, null, 77u));
      edit.AddRecords(records);
      var layer = edit.Build().Layer;

      BakedConsumables.SessionStarts();
      var lines = BakedConsumables.InfoLines(layer);
      C(lines.Count == 3 && lines[0].Contains("2 kinds, 5 records (1 of them marked Live") && lines[0].Contains("Pickable_Flax_Wild 4") && lines[0].Contains("Pickable_Mushroom 1") && !lines[0].Contains("Dandelion"),
        "the consumable line counts the real flax (4) and the Live mushroom, and not one decor record: " + lines[0]);
      C(lines[1] == "Consumables this session: 0 placed, 0 could not be placed.", "the session line is as before: " + lines[1]);
      C(lines[2].StartsWith("Decor: 2 kinds") && lines[2].Contains("9 records") && lines[2].Contains("drawn as scenery") && lines[2].Contains("not pickable")
        && lines[2].Contains("Pickable_Flax_Wild 6") && lines[2].Contains("Pickable_Dandelion 3") && !lines[2].Contains("stone_wall"),
        "the decor line counts the six flax and three dandelions, and not the wall (its prefab is no consumable): " + lines[2]);

      // Only decor: the consumables line says none, the decor line follows.
      var onlyDecor = LayerEdit.New("decor only");
      int d = onlyDecor.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor));
      onlyDecor.AddRecords([ZoneRecord.CreateYaw(d, 3, 40, 5, 0)]);
      var few = BakedConsumables.InfoLines(onlyDecor.Build().Layer);
      C(few.Count == 2 && few[0].StartsWith("Consumables: none") && few[1].StartsWith("Decor: 1 kind ") && few[1].Contains("1 records"), "a layer with decor and no consumable: " + string.Join(" | ", few));

      // No decor: the lines are exactly what they were.
      var plain = LayerEdit.New("no decor");
      int p = plain.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None));
      plain.AddRecords([ZoneRecord.CreateYaw(p, 3, 40, 5, 0), ZoneRecord.CreateYaw(p, 4, 40, 5, 0)]);
      var same = BakedConsumables.InfoLines(plain.Build().Layer);
      C(same.Count == 2 && !same.Any(l => l.Contains("Decor")) && same[0].Contains("1 kind, 2 records"), "a layer with no decor has no decor line: " + string.Join(" | ", same));
      BakedServer.FindPrefab = _ => null;
      var none = BakedConsumables.InfoLines(layer);
      C(none.Count == 1 && none[0].StartsWith("Consumables: none"), "in a game with none of the prefabs: no decor line either: " + string.Join(" | ", none));
    }
    finally
    {
      (BakedServer.FindPrefab, BakedConsumables.Probe, BakedServer.ViewOf) = (find, probe, view);
      BakedConsumables.Placed = placed;
      BakedConsumables.Failed = failed;
      BakedConsumables.SessionStarts();
    }
  }

  // ------------------------------------------------------------------------------------------------ the in-game bake

  private static void DecorFlagBakeNeverTest()
  {
    Section("decor flag: the in-game bake never bakes a consumable and never writes the flag");
    BakeRunner.Reset();
    // Every combination of a consumable component with the words of a bake is left alone (also those that adopt everything).
    foreach (var component in BakeClassifier.ConsumableComponents)
    {
      var prefab = BakePrefab("piece_" + component, component);
      foreach (var words in new[] { default(BakeWords), new BakeWords { Town = true }, new BakeWords { Any = true }, new BakeWords { Town = true, Any = true, Convert = true } })
        foreach (long creator in new long[] { 7, 0 })
        {
          var cls = BakeClassifier.Classify(prefab, creator, words);
          C(cls.Kind == BakeKind.Consumable && !cls.Baked && cls.Untouched && !cls.StaysReal, $"a Piece with {component} (creator {creator}) is still refused: {cls}");
        }
    }
    // A bake of a world full of them and of everything else writes records with no Decor flag, and none of a consumable prefab.
    var fx = BakeNewFx("decor-flag-never", world =>
    {
      var taken = BakeDecorWorld(world);
      foreach (var facts in new[]
      {
        new PrefabFacts("piece_berry", [.. BakeSafeParts, "Pickable"], height: 1f),
        new PrefabFacts("piece_sapling", [.. BakeSafeParts, "Plant"], height: 1f),
        new PrefabFacts("piece_item", [.. BakeSafeParts, "ItemDrop"], height: 1f),
        new PrefabFacts("piece_pickable_item", [.. BakeSafeParts, "PickableItem"], height: 1f),
      })
        world.Facts[facts.Name.GetStableHashCode()] = facts;
      world.Add(BakeObject("piece_berry", 6f, 10f, 9f));
      world.Add(BakeObject("piece_sapling", 8f, 10f, 9f));
      world.Add(BakeObject("piece_item", 10f, 10f, 9f));
      world.Add(BakeObject("piece_pickable_item", 12f, 10f, 9f));
      return taken;
    }, out _);
    BakeRun(fx, BakeStandardArea(), "town any confirm");
    var records = BakeRecordsOf(fx);
    C(records.Count > 10, $"(the bake took {records.Count} pieces)");
    C(records.All(r => r.Prefab is not ("piece_berry" or "piece_sapling" or "piece_item" or "piece_pickable_item")), "no record of a consumable prefab");
    C(fx.Layer.Palette.Count > 3 && fx.Layer.Palette.All(e => (e.Flags & PaletteFlags.Decor) == 0 && !e.IsDecor), $"and none of the {fx.Layer.Palette.Count} palette entries it wrote carries the Decor flag");
    C(fx.World.Objects.Values.Count(o => o.PrefabName is "piece_berry" or "piece_sapling" or "piece_item" or "piece_pickable_item") == 4, "all four consumables are objects still");
  }

  private static void DecorFlagUnbakeTest()
  {
    Section("decor flag: an unbake leaves a decor record in the layer, says so in its own line, and makes no object of it");
    BakeRunner.Reset();
    var edit = LayerEdit.New("compiler");
    int iReal = edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None));
    int iDecor = edit.PaletteIndexFor(Entry("Pickable_Flax_Wild", BakedRole.Static, BakedCollision.None, PaletteFlags.Decor));
    int iCopy = edit.PaletteIndexFor(Entry("Pickable_Mushroom", BakedRole.Copy, BakedCollision.None, PaletteFlags.Decor));
    int iWall = edit.PaletteIndexFor(Entry("stone_wall", BakedRole.Static, BakedCollision.Prefab));
    var zone = new ZoneKey(3, 3);
    for (int i = 0; i < 2; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(iReal, zone.OriginX + 3 + i, 30, zone.OriginZ + 5, 0)], 1f);
    for (int i = 0; i < 4; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(iDecor, zone.OriginX + 3 + i, 30, zone.OriginZ + 7, 0)], 1f);
    edit.AddRecords([ZoneRecord.CreateYaw(iCopy, zone.OriginX + 20, 30, zone.OriginZ + 5, 0)], 1f);
    for (int i = 0; i < 3; i++)
      edit.AddRecords([ZoneRecord.CreateYaw(iWall, zone.OriginX + 3 + i, 30, zone.OriginZ + 9, 0)], 3f);
    var layer = edit.Build().Layer;
    var fx = BakeNewFx("unbake-decor-flag", world =>
    {
      world.Facts["stone_wall".GetStableHashCode()] = new PrefabFacts("stone_wall", BakeSafeParts, height: 3f);
      world.Facts["Pickable_Flax_Wild".GetStableHashCode()] = new PrefabFacts("Pickable_Flax_Wild", ["Pickable", "ZNetView"], height: 1f);
      world.Facts["Pickable_Mushroom".GetStableHashCode()] = new PrefabFacts("Pickable_Mushroom", ["Pickable", "ZNetView"], height: 1f);
      return [];
    }, out _, layer);
    BakeUnbakeRun(fx, BakeWholeWorld, "all", "unbake world all");
    var text = string.Join("\n", fx.Said);
    C(text.Contains("Left alone: 2 records of kinds that give items when used. The game places them") && text.Contains("Left alone: 5 records of kinds that give items when used, which the compiler's file marks as decor")
      && text.Contains("To unbake: 3 pieces (stone_wall 3)."), "the dry run counts the two real flax and the five decor records apart, and takes the walls only: " + text);
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "all confirm", "unbake world all");
    C(fx.World.Objects.Values.Count(o => o.PrefabName == "stone_wall") == 3 && fx.World.Objects.Values.All(o => o.PrefabName is not ("Pickable_Flax_Wild" or "Pickable_Mushroom")),
      "three walls are made and no flax or mushroom: a decor record is never an object");
    C(fx.Layer.Placements == 7 && BakeRecordsOf(fx).All(r => r.Prefab is "Pickable_Flax_Wild" or "Pickable_Mushroom"), "the seven records stay in the layer, the decor ones drawn as before");
  }
}

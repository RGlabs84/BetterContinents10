// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).
//
// The Decor palette flag (32), the server's side: a compiler's palette entry marked Decor (role Static or Copy) says its consumable prefab is scenery.
// BakedServer.IsConsumable is false for it, so SeedZone places nothing for its records (the client draws them), the vegetation clean-up does not
// keep a real object of its prefab at a decor record's pivot, and bc_bake info counts it apart. An entry without the flag is a consumable exactly
// as before (Program.BakedConsumables.cs). The format, the client and the in-game bake are in placement-tests (Tests.DecorFlag.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // A layer with one zone: flax as decor and as a real consumable, a Copy mushroom as decor, a tree marked decor, a pickable marked decor with no
  // ZNetView, a Live dandelion (a consumable, placed once) and Live doors.
  private static (BakedLayer Layer, ZoneKey Zone, int[] Entries) DecorLayer()
  {
    var edit = LayerEdit.New("decor flag");
    PaletteEntry Entry(BakedRole role, PaletteFlags flags, string name) =>
      new([new Candidate(name, 0, 0, 0)], role, role == BakedRole.Live ? BakedCollision.Prefab : BakedCollision.None, 0, flags);
    var entries = new[]
    {
      edit.PaletteIndexFor(Entry(BakedRole.Static, PaletteFlags.Decor, "Pickable_Flax_Wild")),                       // 0 a consumable marked decor
      edit.PaletteIndexFor(Entry(BakedRole.Static, PaletteFlags.None, "Pickable_Flax_Wild")),                        // 1 the same prefab, a real consumable
      edit.PaletteIndexFor(Entry(BakedRole.Copy, PaletteFlags.Decor | PaletteFlags.NoCopyLight, "Pickable_Mushroom")), // 2 a Copy marked decor
      edit.PaletteIndexFor(Entry(BakedRole.Static, PaletteFlags.Decor, "Pine")),                                     // 3 the flag on a tree: nothing
      edit.PaletteIndexFor(Entry(BakedRole.Static, PaletteFlags.Decor, "Pickable_NoView")),                          // 4 decor with no ZNetView: nothing to say
      edit.PaletteIndexFor(Entry(BakedRole.Live, PaletteFlags.Protect, "Pickable_Dandelion")),                       // 5 a Live consumable, placed once
      edit.PaletteIndexFor(Entry(BakedRole.Live, PaletteFlags.Protect, "wood_door")),                                // 6 an ordinary Live piece
    };
    var zone = new ZoneKey(3, -2);
    double x0 = zone.OriginX + 4, z0 = zone.OriginZ + 4;
    var records = new List<ZoneRecord>();
    void Add(int entry, int count, double step, uint? firstId = null)
    {
      for (int n = 0; n < count; n++)
        records.Add(ZoneRecord.CreateYaw(entries[entry], x0 + step * n, 40, z0 + entry * 3.0, 15 * n, null, firstId == null ? null : firstId + (uint)n));
    }
    Add(0, 3, 2.0);
    Add(1, 2, 2.0);
    Add(2, 2, 2.0);
    Add(3, 2, 6.0);
    Add(4, 1, 2.0);
    Add(5, 1, 2.0, firstId: 30);
    Add(6, 2, 5.0, firstId: 20);
    edit.AddRecords(records);
    return (edit.Build().Layer, zone, entries);
  }

  private static ConsumableGame DecorGame() =>
    new ConsumableGame().Has("Pickable_Flax_Wild", consumable: true).Has("Pickable_Mushroom", consumable: true).Has("Pickable_Dandelion", consumable: true).Has("Pine").Has("wood_door")
      .Has("Pickable_NoView", consumable: true, view: false);

  private static void BakedDecorFlagTests()
  {
    BakedDecorKindTests();
    BakedDecorSeedTests();
    BakedDecorReconcileTests();
    BakedDecorVegetationTests();
  }

  // ------------------------------------------------------------------------------------------------ which entries are consumables

  private static void BakedDecorKindTests()
  {
    Section("baked decor flag: an entry marked decor is not a consumable to place, an entry without the flag is, as before");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var (layer, _, entries) = DecorLayer();
    DecorGame().Install();
    BakedConsumables.SessionStarts();
    var context = new BakedServer.Context(layer);
    bool Is(int entry, out BakedServer.Resolved? kind) => BakedServer.IsConsumable(context, entries[entry], layer.Palette[entries[entry]], out kind);
    var lines = LogHandler.During(() =>
    {
      C(!Is(0, out var decor) && decor == null, "a Static pickable marked decor is not a consumable");
      C(Is(1, out var real) && real != null && real.Candidate.Name == "Pickable_Flax_Wild", "the same prefab without the flag is one, placed as that prefab");
      C(!Is(2, out _), "a Copy mushroom marked decor is not one");
      C(!Is(3, out _), "the flag on a tree makes nothing of it");
      C(!Is(4, out var noView) && noView == null, "a decor pickable with no ZNetView is not one either (nothing is placed, so nothing cannot be placed)");
      C(Is(5, out var dandelion) && dandelion != null && !Is(6, out _), "a Live consumable is one as before, and a door is not");
    });
    C(context.Decor.Count == 3 && context.Decor[entries[0]] == "Pickable_Flax_Wild" && context.Decor[entries[2]] == "Pickable_Mushroom" && context.Decor[entries[4]] == "Pickable_NoView" && !context.Decor.ContainsKey(entries[3]),
      "the context names the three decor kinds (a consumable prefab each), and not the tree: " + string.Join(", ", context.Decor));
    C(context.Consumables.Count == 2 && context.Plain.Contains(entries[0]) && context.Plain.Contains(entries[2]), "the consumables are the real flax and the dandelion; a decor entry is a plain one for the placing");
    C(!lines.Any(l => l.Contains("no ZNetView")), "and the log does not say a decor pickable cannot be placed: " + string.Join(" | ", lines));
    C(lines.Count(l => l.Contains("Pickable_Flax_Wild is a consumable that the file marks as decor")) == 1 && lines.Count(l => l.Contains("Pickable_Mushroom is a consumable that the file marks as decor")) == 1
      && lines.Count(l => l.Contains("Pickable_Flax_Wild is a consumable (a player")) == 1, "the log names each decor kind once, and the real flax as a consumable: " + string.Join(" | ", lines));
    C(BakedConsumables.DecorKinds.Count == 3 && BakedConsumables.Kinds.SequenceEqual(["Pickable_Flax_Wild", "Pickable_Dandelion"]), "the session keeps the two lists apart");
    Is(0, out _);
    Is(0, out _);
    C(context.Decor.Count == 3, "each entry is decided once for a zone's pass");
  }

  // ------------------------------------------------------------------------------------------------ a zone's seeding

  private static void BakedDecorSeedTests()
  {
    Section("baked decor flag: a zone places no decor record as an object, and the real consumables and the Live pieces as before");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    _ = new World();
    var (layer, zone, entries) = DecorLayer();
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
    DecorGame().Install();
    BakedConsumables.SessionStarts();
    var all = layer.Decode(layer.Zones.Single(r => r.Key.Equals(zone))).Records().ToList();
    C(all.Count == 13 && all.Count(r => r.Palette == entries[0]) == 3, $"(the zone holds {all.Count} records: 8 of kinds marked decor, 2 real flax, a Live dandelion, 2 doors)");

    var pieces = new List<ZoneRecord>();
    var placed = new List<(string Prefab, ZoneRecord Record)>();
    BakedServer.MakePiece = (context, index, palette, record, ghost, spawned) =>
    {
      pieces.Add(record);
      return true;
    };
    BakedServer.MakeConsumable = (context, index, palette, record, ghost, spawned) =>
    {
      placed.Add((palette.Candidates[0].Name, record));
      return true;
    };
    var lines = LogHandler.During(() =>
    {
      int made = BakedServer.SeedZone(zone, ghost: false, spawned: null, out int consumables);
      C(made == 2 && pieces.Count == 2 && pieces.All(r => layer.Palette[r.Palette].Candidates[0].Name == "wood_door"), "the two doors are seeded as before");
      C(consumables == 3 && placed.Count == 3, $"three records of consumable kinds are placed ({placed.Count}): the two real flax and the Live dandelion");
    });
    C(placed.Count(p => p.Prefab == "Pickable_Flax_Wild") == 2 && placed.Count(p => p.Prefab == "Pickable_Dandelion") == 1 && placed.All(p => p.Record.Palette == entries[1] || p.Record.Palette == entries[5]),
      "and they are the records of the entries without the flag");
    var decorPlaces = all.Where(r => r.Palette == entries[0] || r.Palette == entries[2] || r.Palette == entries[3] || r.Palette == entries[4]).Select(r => r.Position).ToHashSet();
    C(decorPlaces.Count == 8 && !placed.Any(p => decorPlaces.Contains(p.Record.Position)), "not one record of a decor entry (the flax, the Copy mushrooms, the trees, the pickable with no view) is placed");
    C(BakedConsumables.Placed == 3 && BakedConsumables.Failed == 0 && !lines.Any(l => l.Contains("could not be")), $"nothing counts as failed, not even the decor pickable with no ZNetView ({BakedConsumables.Placed} placed, {BakedConsumables.Failed} failed)");

    // A zone of decor only: nothing is placed, nothing is seeded, nothing is said about failures.
    ZDOExtraData.Reset();
    _ = new World();
    var edit = LayerEdit.New("decor only");
    int flax = edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Flax_Wild", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab, 0, PaletteFlags.Decor));
    int mushroom = edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Mushroom", 0, 0, 0)], BakedRole.Copy, BakedCollision.Prefab, 0, PaletteFlags.Decor));
    var only = new ZoneKey(1, 1);
    edit.AddRecords([ZoneRecord.CreateYaw(flax, only.OriginX + 3, 40, only.OriginZ + 3, 0), ZoneRecord.CreateYaw(mushroom, only.OriginX + 5, 40, only.OriginZ + 3, 0)]);
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = edit.Build().Layer };
    placed.Clear();
    pieces.Clear();
    BakedConsumables.SessionStarts();
    int again = BakedServer.SeedZone(only, ghost: false, spawned: null, out int none);
    C(again == 0 && none == 0 && placed.Count == 0 && pieces.Count == 0, "a zone of decor records places nothing and seeds nothing");
    // Ghost mode: the same, and nothing goes to the list of what was spawned.
    var spawned = new List<GameObject>();
    int ghosted = BakedServer.SeedZone(only, ghost: true, spawned: spawned, out int ghostedConsumables);
    C(ghosted == 0 && ghostedConsumables == 0 && spawned.Count == 0 && placed.Count == 0, "also in Ghost mode");
  }

  // ------------------------------------------------------------------------------------------------ reconciliation

  private static void BakedDecorReconcileTests()
  {
    Section("baked decor flag: the reconciliation gathers the Live records as before, and a decor record is not its business");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var (layer, zone, entries) = DecorLayer();
    DecorGame().Install();
    w.Generated.Add(zone.ToVector2s());
    BakedReconcile.IsGenerated = z => w.Generated.Contains(z.ToVector2s());
    BakedReconcile.RotationOf = _ => Quaternion.identity;
    BakedReconcile.VegetationPrefabs = () => [];
    ZoneRegen.SendDestroyQueue = w.Flush;
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, layer, made => input = made);
    while (gathering.MoveNext())
    {
    }
    C(input.Records.Count == 2 && input.Records.All(r => r.PaletteIndex == entries[6]), "Gather takes the two doors: not the Live dandelion (a consumable), not one record marked decor");
    var plan = BakedReconcile.Plan(input);
    C(plan.Seeds == 2 && plan.Removes == 0 && plan.Replaces == 0 && plan.Orphans == 0, $"the plan seeds the two doors and does nothing else: {plan.Summary()}");
  }

  // ------------------------------------------------------------------------------------------------ the vegetation clean-up

  // One zone: flax as decor and as a real consumable, and a Live mushroom, at (entry, x, z) metres from the zone's south-west corner, with a clear mask
  // over the first 8 m both ways.
  private static BakedLayer DecorClearingLayer(ZoneKey zone, params (int Entry, double X, double Z)[] records)
  {
    var edit = LayerEdit.New("decor clearing");
    var entries = new[]
    {
      edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Flax_Wild", 0, 0, 0)], BakedRole.Static, BakedCollision.None, 0, PaletteFlags.Decor)),
      edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Flax_Wild", 0, 0, 0)], BakedRole.Static, BakedCollision.None)),
      edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Mushroom", 0, 0, 0)], BakedRole.Live, BakedCollision.Prefab, 0, PaletteFlags.Protect)),
      edit.PaletteIndexFor(new PaletteEntry([new Candidate("Pickable_Mushroom", 0, 0, 0)], BakedRole.Copy, BakedCollision.None, 0, PaletteFlags.Decor)),
    };
    edit.AddRecords(records.Select(r => ZoneRecord.CreateYaw(entries[r.Entry], zone.OriginX + r.X, 40, zone.OriginZ + r.Z, 0, null, r.Entry == 2 ? 7u : null)));
    var bits = new byte[BakedFormat.MaskBytes];
    foreach (int cell in new[] { 0, 1, 16, 17 })
      bits[cell >> 3] |= (byte)(1 << (cell & 7));
    edit.SetSections(zone, new ZoneSections { Mask = bits });
    return edit.Build().Layer;
  }

  private static void BakedDecorVegetationTests()
  {
    Section("baked decor flag: the clean-up keeps the next layer's real consumables only; a real object at a decor record's pivot is cleared, a player's planted one never");
    ZDOExtraData.Reset();
    using var seams = new SeedSeams();
    var w = new World();
    var key = new ZoneKey(4, 4);
    var zone = key.ToVector2s();
    w.Generated.Add(zone);
    BakedReconcile.IsGenerated = z => w.Generated.Contains(z.ToVector2s());
    BakedReconcile.RotationOf = _ => Quaternion.identity;
    ZoneRegen.SendDestroyQueue = w.Flush;
    new ConsumableGame().Has("Pickable_Flax_Wild", consumable: true).Has("Pickable_Mushroom", consumable: true).Has("Pine").Install();
    BakedReconcile.VegetationPrefabs = () => ["Pickable_Flax_Wild".GetStableHashCode(), "Pickable_Mushroom".GetStableHashCode(), "Pine".GetStableHashCode()];

    // The next layer has flax marked decor at A, a real flax at B, a Copy mushroom marked decor at C and a Live mushroom at D, and clears the cells over all.
    var next = DecorClearingLayer(key, (0, 1.5, 1.5), (1, 5.5, 1.5), (3, 1.5, 5.5), (2, 5.5, 5.5));
    ZDO At(string prefab, double x, double z, float dx = 0f) => w.Add(zone, prefab, (float)(x + dx - 32.0), (float)(z - 32.0), y: 40f);
    var atDecorFlax = At("Pickable_Flax_Wild", 1.5, 1.5);
    var nearDecorFlax = At("Pickable_Flax_Wild", 1.5, 1.5, dx: 0.04f);
    var atRealFlax = At("Pickable_Flax_Wild", 5.5, 1.5);
    var atDecorMushroom = At("Pickable_Mushroom", 1.5, 5.5);
    var atLiveMushroom = At("Pickable_Mushroom", 5.5, 5.5);
    var planted = Creator(At("Pickable_Flax_Wild", 1.5, 1.5, dx: 0.01f), 42L);
    var plantedMushroom = Creator(At("Pickable_Mushroom", 1.5, 5.5, dx: 0.01f), 42L);
    var elsewhere = At("Pickable_Flax_Wild", 30.0, 30.0);
    var pine = At("Pine", 3.0, 3.0);

    ReconcileInput input = null;
    var gathering = BakedReconcile.Gather(null, next, made => input = made);
    while (gathering.MoveNext())
    {
    }
    var growth = input.Growth.Single();
    C(growth.Keep != null && growth.Keep.Count == 2 && growth.Keep["Pickable_Flax_Wild".GetStableHashCode()].Count == 1 && growth.Keep["Pickable_Mushroom".GetStableHashCode()].Count == 1,
      "Gather keeps the real flax and the Live mushroom of the zone that grew, and not the flax or the Copy mushroom marked decor");
    var lines = new List<string>();
    var run = BakedReconcile.Execute(BakedReconcile.Plan(input), lines.Add);
    while (run.MoveNext())
    {
    }
    var gone = w.All.ToHashSet();
    C(gone.Contains(atDecorFlax.m_uid) && gone.Contains(nearDecorFlax.m_uid) && gone.Contains(atDecorMushroom.m_uid),
      "a real object of the prefab of a decor record, at its pivot or 4 cm from it, is cleared like any other vegetation: no duplicate of the drawn one stays");
    C(!gone.Contains(atRealFlax.m_uid) && !gone.Contains(atLiveMushroom.m_uid), "the real flax and the Live mushroom stay, as before");
    C(!gone.Contains(planted.m_uid) && !gone.Contains(plantedMushroom.m_uid) && !gone.Contains(elsewhere.m_uid), "a planted one (it has a creator) and an object outside the cleared cells are never touched");
    C(gone.Contains(pine.m_uid), "a tree in the cell goes, as before");
    C(lines.Count == 1 && lines[0].Contains("4 trees, rocks and bushes cleared"), "four are cleared: " + string.Join(" | ", lines));
  }
}

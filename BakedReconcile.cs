// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// ---- what the planner is given, and what it makes (the names are build spec 13.2's) ---------------------------------------------

/// <summary>A Live record of the new layer, as the planner needs it: the prefab seeding would make and where it would stand.</summary>
internal sealed class LiveRecord(int source, uint id, ZoneKey zone, int prefab, Vector3 pivot, Quaternion rotation, bool unresolved)
{
  public readonly int Source = source;
  public readonly uint Id = id;
  public readonly ZoneKey Zone = zone;
  /// <summary>The stable hash of the prefab's name; 0 when no candidate of the entry is in this game (Unresolved).</summary>
  public readonly int Prefab = prefab;
  public readonly Vector3 Pivot = pivot;
  public readonly Quaternion Rotation = rotation;
  /// <summary>No prefab of the entry is in this game: the record cannot be seeded, and what stands under its key is left alone.</summary>
  public readonly bool Unresolved = unresolved;
  /// <summary>What seeding needs, for the executor: the record and its palette entry's index.</summary>
  public ZoneRecord Record;
  public int PaletteIndex;

  public ulong Key => BakedFormat.LiveKey(Source, Id);
}

/// <summary>An object of the world that carries bake keys.</summary>
internal sealed class WorldPiece(ZDOID uid, int source, uint bakeId, ZoneKey zone, int prefab, Vector3 position, Quaternion rotation, bool holdsItems, long revision)
{
  public readonly ZDOID Uid = uid;
  public readonly int Source = source;
  public readonly uint BakeId = bakeId;
  public readonly ZoneKey Zone = zone;
  public readonly int Prefab = prefab;
  public readonly Vector3 Position = position;
  public readonly Quaternion Rotation = rotation;
  /// <summary>It is a container, or a stand, with something in it (HoldsItems): never destroyed here.</summary>
  public readonly bool HoldsItems = holdsItems;
  /// <summary>The revision of its bc_bake_rev; -1 when it has none.</summary>
  public readonly long Revision = revision;

  public ulong Key => BakedFormat.LiveKey(Source, BakeId);
}

/// <summary>Cells of a generated zone that the new layer's file clears and the old one's did not.</summary>
internal sealed class VegetationGrowth(ZoneKey zone, BakedVegetation.ZoneMask cells)
{
  public readonly ZoneKey Zone = zone;
  public readonly BakedVegetation.ZoneMask Cells = cells;
}

internal sealed class ReconcileInput
{
  /// <summary>The new layer (null when it was dropped: nothing is seeded, every piece goes).</summary>
  public BakedLayer? Layer;
  /// <summary>Its revision: what a kept piece's bc_bake_rev becomes.</summary>
  public uint Revision;
  /// <summary>Every Live record of the new layer in a zone the world has generated.</summary>
  public List<LiveRecord> Records = [];
  public List<WorldPiece> Pieces = [];
  public Func<ZoneKey, bool> IsGenerated = _ => true;
  public List<VegetationGrowth> Growth = [];
}


internal enum ReconcileAction { Seed, Keep, Replace, Remove, Orphan }

internal sealed class PlanItem(ReconcileAction action, LiveRecord? record, WorldPiece? piece)
{
  public readonly ReconcileAction Action = action;
  /// <summary>What to seed (Seed, Replace, and an Orphan whose record is still in the layer, elsewhere).</summary>
  public readonly LiveRecord? Record = record;
  /// <summary>The piece it is about (everything but Seed).</summary>
  public readonly WorldPiece? Piece = piece;
  /// <summary>A Keep whose piece has an older revision: only its key changes.</summary>
  public bool Refresh;

  public override string ToString() => $"{Action} {(Record != null ? "record " + Record.Source + "/" + Record.Id : "")} {(Piece != null ? "piece " + Piece.Source + "/" + Piece.BakeId : "")}".TrimEnd();
}

internal sealed class ReconcilePlan(ReconcileInput input)
{
  public readonly BakedLayer? Layer = input.Layer;
  public readonly uint Revision = input.Revision;
  public readonly List<PlanItem> Items = [];
  public readonly List<VegetationGrowth> Vegetation = [];

  public int Count(ReconcileAction action) => Items.Count(i => i.Action == action);
  public int Seeds => Count(ReconcileAction.Seed);
  public int Keeps => Count(ReconcileAction.Keep);
  public int Replaces => Count(ReconcileAction.Replace);
  public int Removes => Count(ReconcileAction.Remove);
  public int Orphans => Count(ReconcileAction.Orphan);
  /// <summary>Kept pieces whose revision is older.</summary>
  public int Refreshes => Items.Count(i => i.Action == ReconcileAction.Keep && i.Refresh);
  /// <summary>What a run changes in the world: nothing, for a plan that is all Keep at the right revision.</summary>
  public int Changes => Items.Count(i => i.Action != ReconcileAction.Keep || i.Refresh) + Vegetation.Count;

  public string Summary()
  {
    var parts = new List<string>();
    if (Seeds > 0) parts.Add($"{Seeds} seeded");
    if (Replaces > 0) parts.Add($"{Replaces} replaced");
    if (Removes > 0) parts.Add($"{Removes} removed");
    if (Orphans > 0) parts.Add($"{Orphans} left standing as orphans (`bc_bake orphans` lists them)");
    if (Vegetation.Count > 0) parts.Add($"vegetation cleared in {Vegetation.Count} zone{(Vegetation.Count == 1 ? "" : "s")}");
    parts.Add($"{Keeps} kept");
    return string.Join(", ", parts);
  }
}


/// <summary>A piece that was the layer's and is not any more, left standing because it holds items.</summary>
internal readonly struct OrphanInfo(ZDOID id, string prefab, Vector3 position, string reason, bool holdsItems)
{
  public readonly ZDOID Id = id;
  public readonly string Prefab = prefab;
  public readonly Vector3 Position = position;
  public readonly string Reason = reason;
  public readonly bool HoldsItems = holdsItems;
}


// Reconciliation (build spec 7.3, U5: rebake in place, keeping players' progress): after a change of the layer the world's live pieces are
// brought to what the layer says, without touching what players put in them. A pure planner decides, per (source, id), what to do; an
// executor does it a frame's worth at a time.
//
//  * Seed      the layer has the record, its zone is generated, and no piece of that (source, id) stands.
//  * Keep      a piece stands with the same prefab, within 5 cm and 1 degree of the record: it stays, and takes the new revision.
//  * Replace   a piece stands, but moved or is another prefab: it is removed and the record is seeded (a static piece's GameObject does
//              not follow a change of its ZDO's position on clients).
//  * Remove    a piece stands whose record the layer no longer has.
//  * Orphan    Remove or Replace of a piece that holds items (HoldsItems: a container, a stand, a smelter's queue, a fermenter, a cooking
//              station, a turret's ammunition): it stays where it is, loses its bake keys, and carries bc_bake_orphan instead (the old
//              (source, id)), so that `bc_bake orphans` can list it and an admin decide. Never destroyed here. The planner decides from what
//              a piece held when it looked; the executor looks again as it makes each change (a chest opened meanwhile is not destroyed).
//  Records in zones the world has not generated are left to seeding (BakedServer), and a piece is never moved. A second run over the
//  same layer changes nothing: it is all Keep, at the revision they have.
//
// Vegetation: in generated zones where the compiler's clear cells grew (its mask or zone flag 16), the world-made vegetation in the new
// cells is destroyed, as a zone reset destroys what the world made (the prefabs of ZoneSystem.m_vegetation, with no creator and no bake
// id). The cells around in-game records never destroy anything: a player who kept a tree beside the house keeps it; those cells only keep
// vegetation out of a zone that generates again (BakedVegetation).
//
// When: at world load on the machine that runs the world (after the zones and every object are loaded: a ZNet.LoadWorld postfix, after
// BakeRunner's check of what a crash or a cut save left, which settles every bake, unbake and undo first), and after `bc_bake load`,
// `undo`, `unbake` and `drop`. At most 1,000 changes a frame.
internal static class BakedReconcile
{
  /// <summary>Changes a frame, at most (build spec 7.3).</summary>
  internal const int ChangesPerFrame = 1000;

  /// <summary>Milliseconds a frame's changes may take: seeding a piece is an Instantiate, which a thousand of would not fit in a frame.</summary>
  internal const double MillisecondsPerFrame = 8.0;

  /// <summary>A piece within this of its record's pivot (m) and this many degrees of its rotation is where the layer puts it.</summary>
  internal const float PositionTolerance = 0.05f, AngleTolerance = 1f;

  // ---- what the planner is given ----------------------------------------------------------------------------------------------------

  // ---- the plan ------------------------------------------------------------------------------------------------------------------------

  /// <summary>What to do to bring the world's live pieces to the layer. Pure: the same input gives the same plan, and the plan made
  /// after it was carried out is all Keep.</summary>
  internal static ReconcilePlan Plan(ReconcileInput input)
  {
    var plan = new ReconcilePlan(input);
    var byKey = new Dictionary<ulong, List<WorldPiece>>();
    foreach (var piece in input.Pieces)
    {
      if (!byKey.TryGetValue(piece.Key, out var list))
        byKey[piece.Key] = list = [];
      list.Add(piece);
    }
    var held = new HashSet<ulong>();

    foreach (var record in input.Records)
    {
      held.Add(record.Key);
      byKey.TryGetValue(record.Key, out var pieces);
      // A record whose prefab this game lacks cannot be seeded, and what stands under its key is not for this game to judge.
      if (record.Unresolved)
        continue;
      bool generated = input.IsGenerated(record.Zone);
      if (pieces == null)
      {
        if (generated)
          plan.Items.Add(new PlanItem(ReconcileAction.Seed, record, null));
        continue;
      }
      var match = pieces.FirstOrDefault(p => Matches(p, record));
      if (match != null)
      {
        plan.Items.Add(new PlanItem(ReconcileAction.Keep, record, match) { Refresh = match.Revision != input.Revision });
        foreach (var other in pieces)
          if (other != match)
            plan.Items.Add(Removal(other, null));
        continue;
      }
      // Stale: it moved, or it is another prefab. The first piece is replaced by the record's new one, when the record's zone is generated.
      bool replaced = false;
      foreach (var stale in pieces)
      {
        if (generated && !replaced)
        {
          replaced = true;
          plan.Items.Add(stale.HoldsItems ? new PlanItem(ReconcileAction.Orphan, record, stale) : new PlanItem(ReconcileAction.Replace, record, stale));
        }
        else
          plan.Items.Add(Removal(stale, null));
      }
    }

    foreach (var piece in input.Pieces)
      if (!held.Contains(piece.Key))
        plan.Items.Add(Removal(piece, null));
    plan.Vegetation.AddRange(input.Growth.Where(g => g.Cells.Any && input.IsGenerated(g.Zone)));
    return plan;
  }

  private static PlanItem Removal(WorldPiece piece, LiveRecord? record) =>
    new(piece.HoldsItems ? ReconcileAction.Orphan : ReconcileAction.Remove, record, piece);

  internal static bool Matches(WorldPiece piece, LiveRecord record) =>
    piece.Prefab == record.Prefab && Vector3.Distance(piece.Position, record.Pivot) <= PositionTolerance
    && Quaternion.Angle(piece.Rotation, record.Rotation) <= AngleTolerance;

  // ---- what a piece holds ----------------------------------------------------------------------------------------------------------------

  // Version.Item.Smaller: an inventory saved from this version on counts its items in a ushort.
  private const int SmallerVersion = 108;

  // The keys of the stations that hold what a player put in them without an inventory of the game's: an armor stand's slots ("0_item" ...),
  // a cooking station's ("slot0" ...). The game saves a count of slots in the prefab, not in the ZDO, so the first 16 are looked at.
  private const int StationSlots = 16;
  private static readonly int[] ArmorStandItems = Enumerable.Range(0, StationSlots).Select(i => (i + "_item").GetStableHashCode()).ToArray();
  private static readonly int[] CookingSlots = Enumerable.Range(0, StationSlots).Select(i => ("slot" + i).GetStableHashCode()).ToArray();

  /// <summary>Whether a piece holds something a player put in it, which nothing here may destroy: a container with an item (the inventory's
  /// bytes, whose count is read; an inventory that cannot be read counts as holding), an item stand with an item on it, an armor stand with
  /// anything on it, a smelter, kiln or windmill with ore queued or product waiting, a fermenter with something in it, a cooking station with
  /// food on it, a turret with ammunition. (Not fuel: every fire and torch would otherwise stand as an orphan at every change.)</summary>
  internal static bool HoldsItems(ZDO zdo)
  {
    var items = zdo.GetByteArray(ZDOVars.s_items);
    if (items != null && items.Length > 0)
    {
      try
      {
        var inventory = new ZPackage(items);
        int version = inventory.ReadInt();
        // Inventory.Save: the version (109), then the number of items, a ushort since 108 and an int before.
        int count = version >= SmallerVersion ? inventory.ReadUShort() : inventory.ReadInt();
        if (count > 0)
          return true;
      }
      catch (Exception)
      {
        return true;
      }
    }
    if (zdo.GetInt(ZDOVars.s_item, 0) != 0)
      return true;
    // Smelter: ore in the queue ("queued", with "item<i>" for each), or what it made and has not given out (SpawnOre).
    if (zdo.GetInt(ZDOVars.s_queued, 0) > 0 || !string.IsNullOrEmpty(zdo.GetString(ZDOVars.s_spawnOre, "")))
      return true;
    // Fermenter: what is in it (the hash of the item's name). Turret: its ammunition.
    if (zdo.GetInt(ZDOVars.s_content, 0) != 0 || zdo.GetInt(ZDOVars.s_ammo, 0) > 0)
      return true;
    // Beehive: its honey; a sap collector: its sap (both "level"). VALtima's towns seed Live beehives.
    if (zdo.GetInt(ZDOVars.s_level, 0) > 0)
      return true;
    foreach (var key in ArmorStandItems)
      if (zdo.GetInt(key, 0) != 0)
        return true;
    foreach (var key in CookingSlots)
      if (!string.IsNullOrEmpty(zdo.GetString(key, "")))
        return true;
    return false;
  }

  // ---- the world as input ------------------------------------------------------------------------------------------------------------------

  // The calls this makes into the game, as fields for the offline tests to stand in for.
  internal static Func<ZoneKey, bool> IsGenerated = zone => ZoneSystem.instance != null && ZoneSystem.instance.m_generatedZones.Contains(zone.ToVector2s());
  internal static Func<ZDOMan?> Objects = () => ZDOMan.instance;
  internal static Func<ZDO, Quaternion> RotationOf = zdo => zdo.GetRotation();
  internal static Func<HashSet<int>> VegetationPrefabs = () =>
    ZoneSystem.instance == null ? [] : ZoneSystem.instance.m_vegetation.Where(v => v.m_prefab != null).Select(v => v.m_prefab.name.GetStableHashCode()).ToHashSet();

  /// <summary>The world as the planner wants it, for the layer that was just made current: the records of the generated zones, the
  /// world's pieces that carry bake keys, and where the clear cells grew since <paramref name="old"/> (null: every cell is new). A scan of
  /// the world and a decode of the zones, a frame's worth at a time.</summary>
  internal static IEnumerator Gather(BakedLayer? old, BakedLayer? next, Action<ReconcileInput> done)
  {
    var input = new ReconcileInput { Layer = next, Revision = next?.Revision ?? 0, IsGenerated = IsGenerated };
    var clock = Stopwatch.StartNew();
    if (next != null)
    {
      var context = new BakedServer.Context(next);
      foreach (var row in next.Zones)
      {
        bool generated = IsGenerated(row.Key);
        if (generated && row.Live > 0)
          AddRecords(input, next, row, context);
        if (generated && (row.NoVegetation || row.HasClearMask))
        {
          var grown = BakedVegetation.FileMask(next, row.Key).Grown(BakedVegetation.FileMask(old, row.Key));
          if (grown.Any)
            input.Growth.Add(new VegetationGrowth(row.Key, grown));
        }
        if (clock.Elapsed.TotalMilliseconds > MillisecondsPerFrame)
        {
          yield return null;
          clock.Restart();
        }
      }
    }
    var man = Objects();
    if (man != null)
    {
      var sectors = man.m_objectsBySector;
      for (int s = 0; s < sectors.Length; s++)
      {
        var list = sectors[s];
        if (list != null)
          for (int i = 0; i < list.Count; i++)
            Note(input, list[i]);
        if (clock.Elapsed.TotalMilliseconds > MillisecondsPerFrame)
        {
          yield return null;
          clock.Restart();
          // The world may have closed while this waited.
          if (Objects() != man)
            yield break;
        }
      }
      // Portals are kept apart from the sectors (ZDOMan.AddIfPortal).
      foreach (var portal in man.GetPortalList())
        Note(input, portal);
    }
    done(input);
  }

  private static void AddRecords(ReconcileInput input, BakedLayer layer, ZoneRow row, BakedServer.Context context)
  {
    var data = layer.Decode(row);
    for (int k = 0; k < data.Count; k++)
    {
      var palette = layer.Palette[data.Palette[k]];
      if (palette.Role != BakedRole.Live)
        continue;
      var record = data.Record(k);
      if (!record.HasId)
        continue;
      var resolved = BakedServer.Resolve(context, data.Palette[k], palette);
      LiveRecord made;
      if (resolved == null)
        made = new LiveRecord(record.SourceNumber, record.Id, row.Key, 0, record.Position, record.Rotation, unresolved: true);
      else
      {
        var place = BakedServer.PlacementOf(resolved, palette, record);
        made = new LiveRecord(record.SourceNumber, record.Id, row.Key, resolved.Candidate.Name.GetStableHashCode(), place.Pivot, place.Rotation, unresolved: false);
      }
      made.Record = record;
      made.PaletteIndex = data.Palette[k];
      input.Records.Add(made);
    }
  }

  private static void Note(ReconcileInput input, ZDO? zdo)
  {
    if (zdo == null || !zdo.IsValid() || !zdo.GetInt(BakedKeys.Id, out int id))
      return;
    var position = zdo.GetPosition();
    input.Pieces.Add(new WorldPiece(zdo.m_uid, zdo.GetInt(BakedKeys.Src, 0), unchecked((uint)id), ZoneKey.OfPoint(position), zdo.GetPrefab(), position,
      RotationOf(zdo), HoldsItems(zdo), zdo.GetInt(BakedKeys.Rev, -1)));
  }

  // ---- the execution ---------------------------------------------------------------------------------------------------------------------

  /// <summary>Carries a plan out, up to 1,000 changes (and a few milliseconds) a frame; says what it did.</summary>
  internal static IEnumerator Execute(ReconcilePlan plan, Action<string> say)
  {
    var counts = new Counts();
    var man = Objects();
    var context = plan.Layer == null ? null : new BakedServer.Context(plan.Layer);
    var clock = Stopwatch.StartNew();
    int inFrame = 0;
    bool removed = false;
    foreach (var item in plan.Items)
    {
      if (item.Action == ReconcileAction.Keep && !item.Refresh)
        continue;
      if (Objects() != man)
      {
        say("Baked pieces: the world closed before they were reconciled.");
        yield break;
      }
      try
      {
        removed |= Apply(item, plan, context, counts);
      }
      catch (Exception e)
      {
        counts.Errors++;
        if (counts.Errors <= 3)
          BetterContinents.LogError($"Baked pieces: {item} could not be reconciled: {e}");
      }
      if (++inFrame >= ChangesPerFrame || clock.Elapsed.TotalMilliseconds > MillisecondsPerFrame)
      {
        // What was destroyed goes to the clients within the frame, one message of 12 bytes each (a Steam message holds 512 KiB).
        if (removed)
          ZoneRegen.SendQueued();
        removed = false;
        inFrame = 0;
        yield return null;
        clock.Restart();
      }
    }
    foreach (var growth in plan.Vegetation)
    {
      if (Objects() != man)
        yield break;
      try
      {
        removed |= ClearVegetation(growth, counts);
      }
      catch (Exception e)
      {
        counts.Errors++;
        BetterContinents.LogError($"Baked pieces: the vegetation of zone {growth.Zone} could not be cleared: {e}");
      }
      if (clock.Elapsed.TotalMilliseconds > MillisecondsPerFrame)
      {
        if (removed)
          ZoneRegen.SendQueued();
        removed = false;
        yield return null;
        clock.Restart();
      }
    }
    if (removed)
      ZoneRegen.SendQueued();
    say(counts.Summary(plan));
  }

  internal sealed class Counts
  {
    public int Seeded, Kept, Refreshed, Replaced, Removed, Orphaned, Vegetation, Errors, Missing;

    public string Summary(ReconcilePlan plan)
    {
      var parts = new List<string>();
      if (Seeded > 0) parts.Add($"{Seeded} seeded");
      if (Replaced > 0) parts.Add($"{Replaced} replaced");
      if (Removed > 0) parts.Add($"{Removed} removed");
      if (Orphaned > 0) parts.Add($"{Orphaned} left standing as orphans (`bc_bake orphans` lists them)");
      if (Vegetation > 0) parts.Add($"{Vegetation} trees, rocks and bushes cleared");
      parts.Add($"{plan.Keeps} kept");
      if (Errors > 0) parts.Add($"{Errors} could not be done (see the log)");
      return "Baked pieces reconciled: " + string.Join(", ", parts) + ".";
    }
  }

  // One change. True when something was destroyed (the queue is to be sent).
  private static bool Apply(PlanItem item, ReconcilePlan plan, BakedServer.Context? context, Counts counts)
  {
    switch (item.Action)
    {
      case ReconcileAction.Keep:
        var kept = Find(item.Piece!);
        if (kept != null)
        {
          kept.Set(BakedKeys.Rev, unchecked((int)plan.Revision));
          counts.Refreshed++;
        }
        return false;
      case ReconcileAction.Seed:
        if (Seed(context, item.Record!))
          counts.Seeded++;
        return false;
      case ReconcileAction.Replace:
        bool replaced = Retire(item.Piece!, counts, replacing: true);
        if (Seed(context, item.Record!))
          counts.Seeded++;
        return replaced;
      case ReconcileAction.Remove:
        return Retire(item.Piece!, counts, replacing: false);
      default:
        if (MakeOrphan(item.Piece!))
          counts.Orphaned++;
        if (item.Record != null && Seed(context, item.Record))
          counts.Seeded++;
        return false;
    }
  }

  // A piece the plan removes or replaces goes, unless it holds items now. The plan is frames old when it runs (a plan of thousands of changes
  // takes seconds, and a chest the plan saw empty can be opened meanwhile): the piece is looked at again as it is destroyed, and one with
  // something in it stays as an orphan, as the planner would have made it. True when the piece was destroyed.
  private static bool Retire(WorldPiece piece, Counts counts, bool replacing)
  {
    var zdo = Find(piece);
    if (zdo == null)
      return false;
    if (HoldsItems(zdo))
    {
      MakeOrphan(zdo, piece.Source, piece.BakeId);
      counts.Orphaned++;
      return false;
    }
    Destroy(zdo);
    if (replacing)
      counts.Replaced++;
    else
      counts.Removed++;
    return true;
  }

  /// <summary>Makes a record's piece. (A field, for the offline tests.)</summary>
  internal static Func<BakedServer.Context?, LiveRecord, bool> Seeder = (context, record) =>
    context != null && BakedServer.SeedStanding(context, record.PaletteIndex, context.Layer.Palette[record.PaletteIndex], record.Record);

  private static bool Seed(BakedServer.Context? context, LiveRecord record) => Seeder(context, record);

  // The ZDO of a piece the plan names, if it is still what the plan saw: the objects have new ids at every load and the plan is frames old.
  private static ZDO? Find(WorldPiece piece)
  {
    var zdo = Objects()?.GetZDO(piece.Uid);
    if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != piece.Prefab || !zdo.GetInt(BakedKeys.Id, out int id) || unchecked((uint)id) != piece.BakeId)
      return null;
    return zdo;
  }

  /// <summary>Destroys a piece as the game does: this machine takes it over first (without a revision bump: it is about to go), and an
  /// instance goes with its object.</summary>
  internal static bool Destroy(ZDO zdo)
  {
    zdo.SetOwnerInternal(ZDOMan.GetSessionID());
    var view = ZoneRegen.FindView(zdo);
    if (view)
      ZoneRegen.DestroyView(view!);
    else
      Objects()!.DestroyZDO(zdo);
    return true;
  }

  // The piece stays where it is, as a plain piece: the bake keys go, and bc_bake_orphan says what it was (and bumps the ZDO's revision, so
  // the clients' copies lose bc_protect too and its container can be opened and emptied).
  private static bool MakeOrphan(WorldPiece piece)
  {
    var zdo = Find(piece);
    if (zdo == null)
      return false;
    MakeOrphan(zdo, piece.Source, piece.BakeId);
    return true;
  }

  internal static void MakeOrphan(ZDO zdo, int source, uint id)
  {
    foreach (var key in BakedKeys.BakeKeys)
      zdo.RemoveInt(key);
    zdo.Set(BakedKeys.Orphan, BakedKeys.OrphanValue(source, id));
  }

  // ---- vegetation ------------------------------------------------------------------------------------------------------------------------

  // What the world made in the cells the file has cleared since: the prefabs of ZoneSystem.m_vegetation, with no creator and no bake id.
  private static bool ClearVegetation(VegetationGrowth growth, Counts counts)
  {
    var man = Objects();
    if (man == null)
      return false;
    var prefabs = VegetationPrefabs();
    if (prefabs.Count == 0)
      return false;
    bool any = false;
    double ox = growth.Zone.OriginX, oz = growth.Zone.OriginZ;
    foreach (var zdo in ZoneRegen.ZdosIn(man, growth.Zone.ToVector2s()))
    {
      if (!prefabs.Contains(zdo.GetPrefab()) || zdo.GetLong(ZDOVars.s_creator, 0L) != 0L || zdo.GetInt(BakedKeys.Id, out _))
        continue;
      var position = zdo.GetPosition();
      if (!growth.Cells.Covers(position.x - ox, position.z - oz))
        continue;
      if (Destroy(zdo))
      {
        counts.Vegetation++;
        any = true;
      }
    }
    return any;
  }

  // ---- orphans (build spec 7.3: bc_bake orphans) -------------------------------------------------------------------------------------------

  /// <summary>Every orphan in the world, by a scan of its objects (their ids are new at every load, so nothing is kept between runs).</summary>
  internal static IReadOnlyList<OrphanInfo> Orphans()
  {
    var list = new List<OrphanInfo>();
    var man = Objects();
    if (man == null)
      return list;
    foreach (var zdo in AllZdos(man))
      if (zdo.GetString(BakedKeys.Orphan, out var value) && !string.IsNullOrEmpty(value))
      {
        bool items = HoldsItems(zdo);
        string was = BakedKeys.TryParseOrphan(value, out int source, out uint id) ? $"the layer's piece (source {source}, id {id})" : "a piece the layer had";
        list.Add(new OrphanInfo(zdo.m_uid, PrefabName(zdo.GetPrefab()), zdo.GetPosition(),
          items ? $"was {was}; the layer no longer has it there, and it holds items, so it was left standing"
                : $"was {was}; the layer no longer has it there (what it held is gone)", items));
      }
    return list;
  }

  /// <summary>Destroys the named orphans (what the admin chose, after `Orphans()` listed them): only objects that still say they are one.</summary>
  internal static void DestroyOrphans(IEnumerable<ZDOID> ids, Action<string> say)
  {
    var man = Objects();
    int destroyed = 0, skipped = 0;
    foreach (var id in ids)
    {
      var zdo = man?.GetZDO(id);
      if (zdo == null || !zdo.IsValid() || !zdo.GetString(BakedKeys.Orphan, out var value) || string.IsNullOrEmpty(value))
      {
        skipped++;
        continue;
      }
      if (Destroy(zdo))
        destroyed++;
    }
    if (destroyed > 0)
      ZoneRegen.SendQueued();
    say($"Destroyed {destroyed} orphan{(destroyed == 1 ? "" : "s")}" + (skipped > 0 ? $"; {skipped} named {(skipped == 1 ? "was" : "were")} not an orphan any more." : "."));
  }

  private static IEnumerable<ZDO> AllZdos(ZDOMan man)
  {
    foreach (var list in man.m_objectsBySector)
      if (list != null)
        foreach (var zdo in list)
          if (zdo != null && zdo.IsValid())
            yield return zdo;
    foreach (var portal in man.GetPortalList())
      if (portal != null && portal.IsValid())
        yield return portal;
  }

  private static string PrefabName(int hash) => ZNetScene.instance == null ? "prefab " + hash : ZNetScene.instance.GetPrefab(hash)?.name ?? "prefab " + hash;

  // ---- when ------------------------------------------------------------------------------------------------------------------------------

  /// <summary>The check of what a crash or a cut save left (build spec 10.8), which runs before the reconciliation, and ends before it starts.
  /// The settings are the world's by then (ZNet.SetServer's prefix loads them, before Start loads the world).</summary>
  internal static Action AfterWorldLoadCheck = BakeRunner.AfterWorldLoad;

  // After the world's zones and every object are loaded, on the machine that runs the world. (LoadWorld is the chunked save's; a world
  // saved before Valheim 1.0 comes through LoadOldWorld.)
  [HarmonyPatch]
  internal static class WorldLoadPatch
  {
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
      yield return AccessTools.Method(typeof(ZNet), "LoadWorld");
      yield return AccessTools.Method(typeof(ZNet), "LoadOldWorld");
    }

    private static void Postfix(ZNet __instance)
    {
      try
      {
        AtWorldLoad(__instance);
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"Baked pieces: the check at world load failed: {e}");
      }
    }
  }

  private static void AtWorldLoad(ZNet net)
  {
    if (net == null || !net.IsServer())
      return;
    AfterWorldLoadCheck();
    var layer = BakedLayerStore.Current;
    if (layer == null)
      return;
    BetterContinents.instance.StartCoroutine(Reconcile(null, layer, line => BetterContinents.Log(line), quiet: true));
  }

  /// <summary>The whole thing for a layer that was just made current: gather, plan, execute. After `bc_bake load`, `undo`, `unbake`,
  /// `drop`, and at world load (<paramref name="old"/> null: every cell of the file's clear mask counts as new, and the world-made
  /// vegetation in it is cleared if it stands).</summary>
  internal static IEnumerator Reconcile(BakedLayer? old, BakedLayer? next, Action<string> say, bool quiet = false)
  {
    ReconcileInput? input = null;
    var gathering = Gather(old, next, made => input = made);
    while (gathering.MoveNext())
      yield return gathering.Current;
    if (input == null)
      yield break;
    var plan = Plan(input);
    if (quiet && plan.Changes == 0)
    {
      BetterContinents.Log($"Baked pieces: all {plan.Keeps} live piece(s) stand as the layer says.");
      yield break;
    }
    var executing = Execute(plan, say);
    while (executing.MoveNext())
      yield return executing.Current;
  }
}

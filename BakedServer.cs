// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownSeeding.cs, Towns/TownUsables.cs and Towns/TownBuild.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// Seeding the layer's Live records (build spec 7.1): the doors and stations a compiler's file puts in a town, and the pieces an in-game
// bake adopted, become real pieces on the machine that runs the world, once per zone and world, saved with it, as the game's own zone
// controller is: a ZoneSystem.PlaceZoneCtrl postfix (Full mode on a host or in single player, Ghost mode on a dedicated server), which VALtimaOnline's
// TownSeeding did for its town chunks.
//
//  * Consumables (build spec 0.2, BakedConsumables): every record of a consumable kind, whatever its role, is placed ONCE here as an ordinary
//    object of the game, with no bake keys, no bc_protect and no creator, so the game owns it from then on. Reconciliation never seeds or removes
//    one. The kind is decided by the prefab the entry resolves to (its first candidate the game has), not by its name. Not the entries the
//    compiler marked decor (palette flag 32, Static or Copy): their records are scenery the client draws, so nothing is placed for them here.
//
//  * Every Live record of the zone, of every source, is seeded unless its (source, id) already stands in the zone: after a zone reset the
//    kept piece stays and is not seeded twice. The zone's objects are found the way ZoneRegen finds them, each by its own position.
//  * The pivot is the record's point less the anchor turned and scaled (VALtima's TownBuild.Place), with the record's rotation. Scale is
//    only the record's when the prefab's ZNetView syncs it (m_syncInitialScale); otherwise it is 1, said once per kind.
//  * On the ZDO: the entry's tags (a bool as ZDO.Set(key, bool), an int, a float or a string, the keys through the game's own hash), the
//    bake keys (bc_bake_src, bc_bake_id as the u32 cast to an int, bc_bake_rev, and bc_protect for a palette entry with the Protect
//    flag), the look of the piece (MatVar<i> and RandMatSeed, captured in the entry's tags and the record's seed or derived from its
//    place, spec 2.5), and for an in-game record its value set (its creator, its health, whatever else it had). Then BakedPlacements.
//    LiveSeeded: after every key is written, while the instance that was made exists (spec 0.1, item 4).
//  * A piece's Awake has run by then, before the keys were on the ZDO (Door.Awake, WearNTear.Awake), so what the keys would have changed
//    there is done on the instance here: BakedProtect.Apply for a protected piece. VALtimaOnline attaches its door closer in LiveSeeded.
//  * A failure costs the piece, never the zone: an exception escaping PlaceZoneCtrl would make the zone generate twice, its vegetation and
//    locations placed again (VALtimaOnline's lesson, TownSeeding.cs:172-173). A piece that was made and could not be finished is taken
//    out again, so no half-made piece stays in the world.
//  * Outside zone generation (the reconciliation) pieces are made in ghost mode on a host as well: the ZDO is what stays, and the game
//    makes the instance when someone is near (ZoneSystem.cs:2468-2494).
internal static class BakedServer
{
  // ---- the patch --------------------------------------------------------------------------------------------------------------------

  // An exception here would escape PlaceZoneCtrl and abort SpawnZone before SetZoneGenerated: the zone would be generated again.
  [HarmonyPatch(typeof(ZoneSystem), "PlaceZoneCtrl")]
  internal static class SeedPatch
  {
    private static void Postfix(Vector2s zoneID, ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
    {
      if (mode != ZoneSystem.SpawnMode.Full && mode != ZoneSystem.SpawnMode.Ghost)
        return;
      try
      {
        SeedZone(ZoneKey.Of(zoneID), mode == ZoneSystem.SpawnMode.Ghost, spawnedObjects);
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"Baked pieces: zone {zoneID.x},{zoneID.y} could not be seeded: {e}");
      }
    }
  }

  // ---- the seams (the offline tests stand in for them) ---------------------------------------------------------------------------------

  /// <summary>The game's own objects: the prefab of a name, the ZDOs of a zone, how a piece that cannot be finished is taken out.</summary>
  internal static Func<string, GameObject?> FindPrefab = name => ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(name.GetStableHashCode());
  internal static Func<ZoneKey, IEnumerable<ZDO>> ZdosIn = zone => ZoneRegen.ZdosIn(ZDOMan.instance, zone.ToVector2s());
  /// <summary>Whether a prefab has a ZNetView, and if so whether it syncs a scale (m_syncInitialScale); null: it has no ZNetView.</summary>
  internal static Func<GameObject, bool?> ViewOf = prefab =>
  {
    var view = prefab.GetComponent<ZNetView>();
    return view == null ? null : view.m_syncInitialScale;
  };
  internal static Action<ZDO, GameObject?, bool> TakeOut = (zdo, go, ghost) =>
  {
    if (ghost || go == null)
      ZDOMan.instance.DestroyZDO(zdo);
    else
      ZNetScene.instance.Destroy(go);
  };

  // What is said once per kind of piece a session (the log would repeat it for every record).
  private static readonly HashSet<string> said = [];

  internal static void SessionStarts()
  {
    said.Clear();
    looks.Clear();
    BakedConsumables.SessionStarts();
  }

  private static void SayOnce(string kind, string what, string line)
  {
    if (said.Add(kind + "|" + what))
      BetterContinents.Log(line);
  }

  // ---- a zone's seeding -------------------------------------------------------------------------------------------------------------

  /// <summary>The Live records of a zone, as real pieces; those whose (source, id) stands in the zone already are skipped. The records of
  /// consumable kinds, whatever their role, are placed as ordinary objects (<see cref="PlaceConsumable"/>), unless one of that prefab stands
  /// at the same place already. Ghost mode puts the instances into <paramref name="spawned"/>, which the game destroys, and keeps ghost
  /// initialisation on while they are made. How many Live pieces were made.</summary>
  internal static int SeedZone(ZoneKey zone, bool ghost, List<GameObject>? spawned) => SeedZone(zone, ghost, spawned, out _);

  /// <summary>As above, and how many records of consumable kinds were placed.</summary>
  internal static int SeedZone(ZoneKey zone, bool ghost, List<GameObject>? spawned, out int consumables)
  {
    consumables = 0;
    var layer = BakedLayerStore.Current;
    if (layer == null || !layer.TryGetZoneRow(zone, out var row) || row.Placements == 0)
      return 0;
    int made = 0, failed = 0, placed = 0, notPlaced = 0;
    bool ghostStarted = false;
    try
    {
      var data = layer.Decode(row);
      HashSet<ulong>? standing = null;
      Dictionary<int, List<Vector3>>? there = null;
      var context = new Context(layer);
      for (int k = 0; k < data.Count; k++)
      {
        int index = data.Palette[k];
        var palette = layer.Palette[index];
        bool consumable = IsConsumable(context, index, palette, out var kind);
        if (!consumable && palette.Role != BakedRole.Live)
          continue;
        var record = data.Record(k);
        if (!ghostStarted && ghost)
        {
          ZNetView.StartGhostInit();
          ghostStarted = true;
        }
        if (consumable)
        {
          // Placed once, when the zone generates. What stands at its place already (a second pass over the zone) is not placed again.
          if (kind == null)
          {
            notPlaced++;
            continue;
          }
          there ??= PrefabsStanding(zone);
          if (Stands(there, kind, palette, record))
            continue;
          if (MakeConsumable(context, index, palette, record, ghost, spawned))
            placed++;
          else
            notPlaced++;
          continue;
        }
        standing ??= StandingKeys(zone);
        if (!record.HasId || standing.Contains(BakedFormat.LiveKey(record.SourceNumber, record.Id)))
          continue;
        if (MakePiece(context, index, palette, record, ghost, spawned))
          made++;
        else
          failed++;
      }
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Baked pieces: zone {zone} could not be seeded: {e}");
    }
    finally
    {
      if (ghostStarted)
        ZNetView.FinishGhostInit();
    }
    if (failed > 0)
      BetterContinents.Log($"Baked pieces: zone {zone}: {made} seeded, {failed} could not be (the log says why, once for each kind).");
    if (notPlaced > 0)
      BetterContinents.Log($"Baked pieces: zone {zone}: {placed} consumables placed, {notPlaced} could not be (the log says why, once for each kind).");
    BakedConsumables.Placed += placed;
    BakedConsumables.Failed += notPlaced;
    consumables = placed;
    return made;
  }

  /// <summary>The (source, id) of every piece in a zone that carries bake keys: what stands, which seeding does not make again. (The
  /// zone's objects are found by their own position: a zone past the sector map shares a list.)</summary>
  internal static HashSet<ulong> StandingKeys(ZoneKey zone)
  {
    var keys = new HashSet<ulong>();
    foreach (var zdo in ZdosIn(zone))
      if (zdo.GetInt(BakedKeys.Id, out int id))
        keys.Add(BakedFormat.LiveKey(zdo.GetInt(BakedKeys.Src, 0), unchecked((uint)id)));
    return keys;
  }

  // ---- one piece ----------------------------------------------------------------------------------------------------------------------

  /// <summary>What seeding learns once about a palette entry in a zone's pass: the prefab it makes, where its anchor is, whether the prefab
  /// takes a scale.</summary>
  internal sealed class Resolved(GameObject prefab, Candidate candidate, bool syncsScale)
  {
    public readonly GameObject Prefab = prefab;
    public readonly Candidate Candidate = candidate;
    /// <summary>The prefab's ZNetView syncs a scale (m_syncInitialScale): the record's scale is made, not 1.</summary>
    public readonly bool SyncsScale = syncsScale;
  }

  internal sealed class Context(BakedLayer layer)
  {
    public readonly BakedLayer Layer = layer;
    // By palette index; null where the entry cannot be seeded (said once).
    public readonly Dictionary<int, Resolved?> Entries = [];
    // The entries whose prefab is a consumable kind, by palette index: what to place (null: the prefab has no ZNetView, said once), and the
    // entries that are not consumables.
    public readonly Dictionary<int, Resolved?> Consumables = [];
    public readonly HashSet<int> Plain = [];
    // The entries whose prefab is a consumable kind that the compiler marked decor (palette flag 32), by palette index: the prefab's name. These
    // are in Plain as well: they are not consumables to place, they are scenery the client draws.
    public readonly Dictionary<int, string> Decor = [];
  }

  /// <summary>Whether a palette entry is a consumable kind, decided by the prefab it resolves to: the first candidate the game has (the one the
  /// client would draw), so an entry whose first candidate is missing is what its stand-in is. <paramref name="kind"/> is what to place, null
  /// when the prefab cannot be placed (it has no ZNetView; said once). An entry the game has no prefab of is not one. An entry the compiler marked
  /// decor (<see cref="PaletteEntry.IsDecor"/>) is not one either, whatever its prefab: its records are scenery the client draws and nothing here
  /// places (<paramref name="context"/>.Decor names it).</summary>
  internal static bool IsConsumable(Context context, int index, PaletteEntry palette, out Resolved? kind)
  {
    if (context.Consumables.TryGetValue(index, out kind))
      return true;
    kind = null;
    if (context.Plain.Contains(index))
      return false;
    foreach (var candidate in palette.Candidates)
    {
      var prefab = FindPrefab(candidate.Name);
      if (prefab == null)
        continue;
      if (!BakedConsumables.Is(prefab))
        break;
      if (palette.IsDecor)
      {
        BakedConsumables.NoteDecor(candidate.Name);
        context.Decor[index] = candidate.Name;
        break;
      }
      BakedConsumables.Note(candidate.Name);
      var scale = ViewOf(prefab);
      if (scale == null)
        SayOnce(palette.Name, "nview", $"Baked pieces: {candidate.Name} has no ZNetView, so its records cannot be placed.");
      else
        kind = new Resolved(prefab, candidate, scale.Value);
      context.Consumables[index] = kind;
      return true;
    }
    context.Plain.Add(index);
    return false;
  }

  /// <summary>The first prefab of an entry that the game has, and that has a ZNetView to seed. Null (and said once per kind) when none does.</summary>
  internal static Resolved? Resolve(Context context, int index, PaletteEntry palette)
  {
    if (context.Entries.TryGetValue(index, out var known))
      return known;
    Resolved? found = null;
    foreach (var candidate in palette.Candidates)
    {
      var prefab = FindPrefab(candidate.Name);
      if (prefab == null)
        continue;
      var scale = ViewOf(prefab);
      if (scale == null)
      {
        SayOnce(palette.Name, "nview", $"Baked pieces: {candidate.Name} has no ZNetView, so a Live record of it cannot be seeded.");
        continue;
      }
      found = new Resolved(prefab, candidate, scale.Value);
      break;
    }
    if (found == null)
      SayOnce(palette.Name, "missing", $"Baked pieces: no prefab of {string.Join(", ", palette.Candidates.Select(c => c.Name))} is in this game, so its Live records are not seeded.");
    context.Entries[index] = found;
    return found;
  }

  /// <summary>Where a piece's pivot goes: the record's point less the anchor, turned and scaled with the piece.</summary>
  internal static Vector3 Pivot(Vector3 point, Quaternion rotation, Vector3 scale, Vector3 anchor) => point - rotation * Vector3.Scale(scale, anchor);

  /// <summary>Where a record's piece stands: its pivot, its rotation, and the scale it takes (the record's only when the prefab's ZNetView
  /// syncs one, else 1, said once per kind).</summary>
  internal readonly struct Placement(Vector3 pivot, Quaternion rotation, Vector3 scale, bool scaled)
  {
    public readonly Vector3 Pivot = pivot;
    public readonly Quaternion Rotation = rotation;
    public readonly Vector3 Scale = scale;
    /// <summary>The piece is made at the record's scale (the prefab syncs it, and it is not 1).</summary>
    public readonly bool Scaled = scaled;
  }

  internal static Placement PlacementOf(Resolved resolved, PaletteEntry palette, ZoneRecord record)
  {
    var rotation = record.Rotation;
    var scale = record.HasScale ? record.Scale : Vector3.one;
    bool scaled = false;
    if (scale != Vector3.one)
    {
      if (resolved.SyncsScale)
        scaled = true;
      else
      {
        SayOnce(palette.Name, "scale", $"Baked pieces: {resolved.Candidate.Name} does not take a scale (its ZNetView does not sync one), so its records are made at scale 1.");
        scale = Vector3.one;
      }
    }
    return new Placement(Pivot(record.Position, rotation, scale, resolved.Candidate.Anchor), rotation, scale, scaled);
  }

  /// <summary>Makes one piece (SeedPiece; a field, for the offline tests, which have no prefabs to make).</summary>
  internal static Func<Context, int, PaletteEntry, ZoneRecord, bool, List<GameObject>?, bool> MakePiece = SeedPiece;

  /// <summary>Makes one piece. True when it stands finished. A failure is logged, the piece (if any was made) taken out, and false returned.</summary>
  internal static bool SeedPiece(Context context, int index, PaletteEntry palette, ZoneRecord record, bool ghost, List<GameObject>? spawned)
  {
    var resolved = Resolve(context, index, palette);
    if (resolved == null)
      return false;
    var made = new Made();
    try
    {
      var place = PlacementOf(resolved, palette, record);
      Make(resolved, place, ghost, spawned, made);
      var go = made.Go!;
      var zdo = made.Zdo!;
      WriteKeys(context.Layer, palette, record, zdo);
      WriteLook(palette, record, zdo, resolved.Prefab);
      if (palette.Protected)
        BakedProtect.Apply(go);
      BakedApi.RaiseLiveSeeded(zdo, go, record.SourceNumber, record.Id);
      return true;
    }
    catch (Exception e)
    {
      SayOnce(palette.Name, "failed", $"Baked pieces: a piece of {palette.Name} (source {record.SourceNumber}, id {record.Id}) could not be seeded: {e}");
      TakeOutHalfMade(made, ghost);
      return false;
    }
  }

  // ---- a consumable ----------------------------------------------------------------------------------------------------------------------

  /// <summary>Places the record of a consumable kind (a field, for the offline tests, which have no prefabs to make).</summary>
  internal static Func<Context, int, PaletteEntry, ZoneRecord, bool, List<GameObject>?, bool> MakeConsumable = PlaceConsumable;

  /// <summary>Places one record of a consumable kind as an ordinary object of the game: the prefab at the record's pivot, rotation and scale
  /// (<see cref="PlacementOf"/>, as for a Live record), and NOTHING else on it: no bake keys, no bc_protect, no look, no creator, no event.
  /// The game owns it from here on. True when it stands; a failure costs this object only (said once per kind; a half-made object is taken out).</summary>
  internal static bool PlaceConsumable(Context context, int index, PaletteEntry palette, ZoneRecord record, bool ghost, List<GameObject>? spawned)
  {
    if (!context.Consumables.TryGetValue(index, out var resolved) || resolved == null)
      return false;
    var made = new Made();
    try
    {
      Make(resolved, PlacementOf(resolved, palette, record), ghost, spawned, made);
      return true;
    }
    catch (Exception e)
    {
      SayOnce(palette.Name, "failed", $"Baked pieces: a record of {palette.Name} (a consumable, source {record.SourceNumber}) could not be placed: {e}");
      TakeOutHalfMade(made, ghost);
      return false;
    }
  }

  /// <summary>The objects of a zone by prefab, with their places: what a second pass over the zone finds standing (consumables carry no key to
  /// find them by).</summary>
  internal static Dictionary<int, List<Vector3>> PrefabsStanding(ZoneKey zone)
  {
    var there = new Dictionary<int, List<Vector3>>();
    foreach (var zdo in ZdosIn(zone))
    {
      if (zdo == null || !zdo.IsValid())
        continue;
      int prefab = zdo.GetPrefab();
      if (!there.TryGetValue(prefab, out var list))
        there[prefab] = list = [];
      list.Add(zdo.GetPosition());
    }
    return there;
  }

  /// <summary>An object of the record's prefab stands where the record would put it (within 5 cm of its pivot, the reconciliation's tolerance).</summary>
  internal static bool Stands(Dictionary<int, List<Vector3>> there, Resolved resolved, PaletteEntry palette, ZoneRecord record)
  {
    if (!there.TryGetValue(resolved.Candidate.Name.GetStableHashCode(), out var places))
      return false;
    var pivot = PlacementOf(resolved, palette, record).Pivot;
    foreach (var place in places)
      if (Vector3.Distance(place, pivot) <= BakedReconcile.PositionTolerance)
        return true;
    return false;
  }

  // What Make has made so far, so that a failure after the instance exists can take it out again.
  private sealed class Made
  {
    public GameObject? Go;
    public ZDO? Zdo;
  }

  private static void TakeOutHalfMade(Made made, bool ghost)
  {
    try
    {
      if (made.Zdo != null && made.Zdo.IsValid())
        TakeOut(made.Zdo, made.Go, ghost);
    }
    catch (Exception again)
    {
      BetterContinents.LogError($"Baked pieces: the half-made piece could not be taken out: {again.Message}");
    }
  }

  // The game object itself, as the game's own PlaceVegetation makes one: the prefab at the pivot, turned, and at the record's scale when its
  // ZNetView takes one. Fills `made` as each part exists.
  private static void Make(Resolved resolved, Placement place, bool ghost, List<GameObject>? spawned, Made made)
  {
    var go = made.Go = UnityEngine.Object.Instantiate(resolved.Prefab, place.Pivot, place.Rotation);
    // Whatever happens next, the game destroys a ghost instance with the rest of the zone's.
    if (ghost)
      spawned?.Add(go);
    var view = go.GetComponent<ZNetView>();
    var zdo = view == null ? null : view.GetZDO();
    if (zdo == null)
      throw new InvalidOperationException("the instance has no ZDO");
    made.Zdo = zdo;
    if (place.Scaled)
    {
      view!.SetLocalScale(place.Scale);
      foreach (var collider in go.GetComponentsInChildren<Collider>())
      {
        collider.enabled = false;
        collider.enabled = true;
      }
    }
  }

  // Every key a seeded piece has, in the order the build spec lists them.
  internal static void WriteKeys(BakedLayer layer, PaletteEntry palette, ZoneRecord record, ZDO zdo)
  {
    foreach (var tag in palette.Tags)
      WriteTag(zdo, tag);
    zdo.Set(BakedKeys.Src, record.SourceNumber);
    zdo.Set(BakedKeys.Id, unchecked((int)record.Id));
    zdo.Set(BakedKeys.Rev, unchecked((int)layer.Revision));
    if (palette.Protected)
      zdo.Set(BakedKeys.Protect, true);
    if (record.HasSource && layer.Registry.TryGet(record.SourceNumber, out var operation) && operation.IsBake && record.ValueSet < operation.ValueSets.Length)
      foreach (var value in operation.ValueSets[record.ValueSet].Values)
        WriteValue(zdo, value);
    // A new portal marks the portal file itself; this keeps a save that started in between from missing the keys.
    BakedKeys.Changed(zdo);
  }

  // A tag's value, with the game's own setters: a bool is stored as an int and read back by GetBool (VALtimaOnline's IsTownPiece reads it so).
  internal static void WriteTag(ZDO zdo, Tag tag)
  {
    int key = tag.Key.GetStableHashCode();
    switch (tag.Type)
    {
      case TagType.Bool:
        zdo.Set(key, tag.Bool);
        break;
      case TagType.Int:
        zdo.Set(key, tag.Number);
        break;
      case TagType.Float:
        zdo.Set(key, tag.Float);
        break;
      default:
        zdo.Set(key, tag.Text);
        break;
    }
  }

  /// <summary>A ZDO value of a registry's value set, given back.</summary>
  internal static void WriteValue(ZDO zdo, ZdoValue value)
  {
    switch (value.Type)
    {
      case ZdoValueType.Float:
        zdo.Set(value.Key, value.A);
        break;
      case ZdoValueType.Vec3:
        zdo.Set(value.Key, new Vector3(value.A, value.B, value.C));
        break;
      case ZdoValueType.Quat:
        zdo.Set(value.Key, new Quaternion(value.A, value.B, value.C, value.D));
        break;
      case ZdoValueType.Int:
        zdo.Set(value.Key, (int)value.Number);
        break;
      case ZdoValueType.Long:
        zdo.Set(value.Key, value.Number);
        break;
      case ZdoValueType.String:
        zdo.Set(value.Key, value.Text);
        break;
      default:
        zdo.Set(value.Key, value.Data);
        break;
    }
  }

  // ---- the look (build spec 2.5) ----------------------------------------------------------------------------------------------------

  /// <summary>What a prefab has that gives a piece a look of its own: the slots of its MaterialVariation components with their weights, and
  /// whether it has RandomMaterialValues.</summary>
  internal sealed class Look(KeyValuePair<int, float[]>[] variations, bool randomValues)
  {
    public readonly KeyValuePair<int, float[]>[] Variations = variations;
    public readonly bool RandomValues = randomValues;
  }

  private static readonly Dictionary<GameObject, Look> looks = [];

  private static Look LookOf(GameObject prefab)
  {
    if (looks.TryGetValue(prefab, out var known))
      return known;
    var variations = new List<KeyValuePair<int, float[]>>();
    foreach (var variation in prefab.GetComponentsInChildren<MaterialVariation>(true))
      if (variation.m_materials.Count > 0)
        variations.Add(new KeyValuePair<int, float[]>(variation.m_materialIndex, variation.m_materials.Select(m => m.m_weight).ToArray()));
    return looks[prefab] = new Look(variations.ToArray(), prefab.GetComponentInChildren<RandomMaterialValues>(true) != null);
  }

  // The variant of each MaterialVariation slot the entry does not name in its tags (MatVar<i>), and RandMatSeed, from the record's place:
  // every machine, and every reconciliation, gives the piece the look the layer's drawing gives it, so it keeps one look from then on.
  private static void WriteLook(PaletteEntry palette, ZoneRecord record, ZDO zdo, GameObject prefab)
  {
    var look = LookOf(prefab);
    if (look.Variations.Length == 0 && !look.RandomValues)
      return;
    uint hash = record.LookHash;
    foreach (var variation in look.Variations)
      if (!palette.TryGetTag(BakedKeys.MatVarKey(variation.Key), out _))
        zdo.Set(BakedKeys.MatVar(variation.Key), DerivedVariant(BakedFormat.VariantUnit(hash, variation.Key), variation.Value));
    if (look.RandomValues && !palette.TryGetTag(BakedFormat.KeySeed, out _))
      zdo.Set(BakedKeys.Seed, record.EffectiveSeed);
  }

  /// <summary>The first variant whose running weight passes u times the total weight (build spec 2.5).</summary>
  internal static int DerivedVariant(double u, IReadOnlyList<float> weights)
  {
    double total = 0;
    foreach (var weight in weights)
      total += weight;
    double target = u * total, running = 0;
    for (int i = 0; i < weights.Count; i++)
    {
      running += weights[i];
      if (running > target)
        return i;
    }
    return 0;
  }

  // ---- outside zone generation ----------------------------------------------------------------------------------------------------------

  /// <summary>Makes a record's piece in a zone that is generated already (the reconciliation: a record the world lacks), in ghost mode on a
  /// host as on a dedicated server: the ZDO is what stays. True when it was made.</summary>
  internal static bool SeedStanding(Context context, int index, PaletteEntry palette, ZoneRecord record)
  {
    var spawned = new List<GameObject>();
    bool made;
    ZNetView.StartGhostInit();
    try
    {
      made = SeedPiece(context, index, palette, record, ghost: true, spawned);
    }
    finally
    {
      ZNetView.FinishGhostInit();
    }
    foreach (var go in spawned)
      UnityEngine.Object.Destroy(go);
    return made;
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// The game files every object under a sector, one per 64 m zone, in an array of 512 x 512 sectors (zones -256 to 255, out to
// about +-16.4 km from the centre), and saves them in chunks of 8 x 8 zones named after that array. ZoneSystem.SectorToIndex
// answers sector 0 for every zone outside it, so everything beyond about 16.35 km shares one list, which a server then sends
// to every client that comes near, and one save chunk. WorldSectors gives a world that reaches past that every zone from -1024
// to 1023 (-65.6 km to 65.5 km) a sector and a save chunk of its own, on the server and on the clients. See
// tools/sector-tests/DESIGN.md for the game code this follows and why each patch is needed.
//
// What is patched, while a world needs it (Wanted: Better Continents is on for the world and its World Size + Edge Size is
// past 16,350 m; or the save already holds chunks of the wider area, ForcedBySave):
//   - the five functions that turn a zone into a sector, a sector into a chunk, and a chunk into its zones (SectorMap),
//   - the array's allocation (ZDOMan.ResetSectorArray: 2048 x 2048 sectors, 32 MB),
//   - the three save-planning methods of ZDOMan (GetSaveClonePerChunk, AddObjectsPerChunk, DecideChunkSize), whose 64 x 64
//     chunks and 512 x 512 sectors are constants of their code, which a transpiler widens to 256 x 256 and 2048 x 2048,
//   - Utils.SmallPosition, which writes an object at y = 0 (every zone's _ZoneCtrl) in two shorts and, beyond 20,000 m,
//     clamps it to 20,000 m.
// A world that does not need it keeps the game's own code, byte for byte.
//
// The inner area (zones -256 to 255) keeps the game's sector order and chunk numbers, so a vanilla-size world's save does not
// change, and a world saved before 0.10.3 whose far objects were all filed in chunk (0, 0) is migrated when it loads.
internal static class WorldSectors
{
  // ---- the sector map -----------------------------------------------------------------------------------------------

  // Zone, sector and chunk arithmetic, free of the game (tools/sector-tests runs it on its own).
  //
  // The sector array is the chunk grid: the chunk of a sector is the sector's 8 x 8 block in the array, so the game's own
  // loops over the chunk grid (GetSaveClonePerChunk) and the zones of a chunk (AddObjectsPerChunk) need nothing but wider
  // constants. A zone's place along an axis is (zone + 256) modulo 2048: zones -256 to 255 are 0 to 511 as in the game, zones 256
  // to 1023 follow them (512 to 1279), and zones -1024 to -257 come last (1280 to 2047), so a chunk of 8 zones is 8 whole
  // places on every axis and no chunk of the game's merged sizes (2, 4 or 8 chunks across) reaches over a border.
  internal static class SectorMap
  {
    // Sectors, and zones, along an axis; the game's own 512.
    internal const int Width = 2048;
    internal const int VanillaWidth = 512;
    // The first and last zone of an axis the map reaches.
    internal const int FirstZone = -1024;
    internal const int LastZone = 1023;
    // The game's: an 8 x 8 block of zones is a chunk, and zone -256 is its sector array's first place.
    internal const int ZonesPerChunk = 8;
    internal const int VanillaFirstZone = -256;
    internal const int VanillaLastZone = 255;
    // The game's ChunkPortal, chunk (1, 0), is the key its portal file is saved under: a chunk of zones with the same key
    // would be written over it, or read as portals. The block of zones that is chunk (1, 0) in the game's order (zones -248 to
    // -241 by -256 to -249, 22.7 km out) is kept at chunk (159, 0) instead, where the zones 1016 to 1023 by -256 to -249 would
    // have been (67 km out, past the world's largest disc), which have no sector then.
    internal const int PortalChunkX = 1;
    internal const int MovedChunkX = 159;
    private const int MovedShift = (MovedChunkX - PortalChunkX) * ZonesPerChunk;

    // A zone's place along an axis of the array. Only for FirstZone to LastZone.
    internal static int Axis(int zone) => (zone + 256) & (Width - 1);
    // The zone at a place along an axis.
    internal static int Zone(int axis) => axis < 1280 ? axis - 256 : axis - 2304;

    internal static bool InRange(int zone) => zone >= FirstZone && zone <= LastZone;

    // ZoneSystem.SectorToIndex: the sector of a zone; sector 0 (where the game files what it cannot place) for a zone the map
    // does not reach.
    internal static uint SectorToIndex(int zoneX, int zoneY)
    {
      if (!InRange(zoneX) || !InRange(zoneY))
        return 0;
      int x = Axis(zoneX), y = Axis(zoneY);
      if (y < ZonesPerChunk)
      {
        int chunk = x / ZonesPerChunk;
        if (chunk == PortalChunkX)
          x += MovedShift;
        else if (chunk == MovedChunkX)
          return 0;
      }
      return (uint)(y * Width + x);
    }

    // ZoneSystem.IndicesToIndex: the sector at a place (x, y) of the array; sector 0 outside it.
    internal static uint IndicesToIndex(uint x, uint y) => x >= Width || y >= Width ? 0 : y * Width + x;

    // ZoneSystem.IndexToSector: the zone of a sector. A sector no zone has (the 8 x 8 places of chunk (1, 0), which the zones
    // it would hold have moved out of) answers the zone its place would be in the game's own order.
    internal static (int x, int y) IndexToSector(uint index)
    {
      int x = (int)(index % Width), y = (int)(index / Width);
      if (y < ZonesPerChunk && x / ZonesPerChunk == MovedChunkX)
        x -= MovedShift;
      return (Zone(x), Zone(y));
    }

    // ZoneSystem.GetZonesChunk: the chunk, 8 bits for each axis (the game's), of a sector.
    internal static ushort Chunk(uint index) => (ushort)((index % Width / ZonesPerChunk) | (index / Width / ZonesPerChunk << 8));

    // ZoneSystem.GetZoneFromChunk: the first zone (the lowest on each axis) of a chunk's 8 x 8.
    internal static (int x, int y) ZoneFromChunk(ushort chunk)
    {
      int x = (chunk & 0xFF) * ZonesPerChunk, y = (chunk >> 8) * ZonesPerChunk;
      if (y < ZonesPerChunk && x / ZonesPerChunk == MovedChunkX)
        x -= MovedShift;
      return (Zone(x), Zone(y));
    }

    // Whether a chunk of the game's save is outside the area the game's own order has: only a world saved with this map has one.
    internal static bool IsWideChunk(ushort chunk) => (chunk & 0xFF) >= 64 || (chunk >> 8) >= 64;
  }

  // ---- when the map is used -----------------------------------------------------------------------------------------

  // A world whose World Size + Edge Size is within this needs no more than the game's own sectors (zones -256 to 255 reach
  // -16,416 m to 16,352 m).
  internal const float VanillaReach = 16350f;
  // The most a world reaches: the map's zones out to 65,504 m, with the room a ship pushed back from the edge needs.
  internal const float MaxReach = 65000f;

  // How far out a size has objects: its radius, or nothing when it makes no world.
  private static float Reach(WorldGeometry? size) => size != null && size.TotalRadius > 0f && !float.IsNaN(size.TotalRadius) ? size.TotalRadius : 0f;

  // Whether a world needs the map: Better Continents is on for it and its size (its own, or Expand World Size's when that
  // has sent one) reaches past the game's sectors. A world made by an older Better Continents is the same: it has the
  // same objects far out, in the one list.
  internal static bool Wanted(BetterContinents.BetterContinentsSettings settings, WorldGeometry? expandWorldSize) =>
    settings.EnabledForThisWorld && Math.Max(Reach(settings.OwnGeometry), Reach(expandWorldSize)) > VanillaReach;

  // Whether the patches are on.
  internal static bool Active { get; private set; }

  // The loaded save holds chunks only this map writes, so the world keeps the map whatever its settings now say; until the
  // next session starts (SessionStarts).
  internal static bool ForcedBySave { get; private set; }

  // What DynamicPatch asks for: the map for a world that needs it, off for any other.
  internal static void Update(Harmony harmony, BetterContinents.BetterContinentsSettings settings, WorldGeometry? expandWorldSize) =>
    Switch(harmony, ForcedBySave || Wanted(settings, expandWorldSize), settings, expandWorldSize);

  // A new session (ZNet.SetServer): nothing is known of its save yet.
  internal static void SessionStarts() => ForcedBySave = false;

  private static bool warnedBeyond;
  // What the last refused switch asked for (said once).
  private static bool? warnedStay;

  // On or off. The sectors an existing game holds are not moved: a change comes only while no object is in them (the main
  // menu, a client before the server's objects arrive, a server before its world loads), or it is left for the next session.
  private static void Switch(Harmony harmony, bool on, BetterContinents.BetterContinentsSettings? settings, WorldGeometry? expandWorldSize)
  {
    if (on && !warnedBeyond && settings != null && Math.Max(Reach(settings.OwnGeometry), Reach(expandWorldSize)) > MaxReach)
    {
      warnedBeyond = true;
      BetterContinents.LogWarning($"Sectors: this world reaches past {MaxReach} m. Zones out to 65,504 m have their own sectors; objects beyond share one, as the game's do beyond 16.4 km.");
    }
    if (on == Active)
      return;
    if (!CanSwitch())
    {
      if (warnedStay != on)
        BetterContinents.LogWarning($"Sectors: the world's objects are loaded, so the sectors stay as they are ({(Active ? "wide" : "the game's")}) until the next session.");
      warnedStay = on;
      return;
    }
    warnedStay = null;
    if (on)
    {
      if (!TryPatch(harmony))
        return;
      Active = true;
      ResizeLive();
      BetterContinents.Log($"Sectors: every zone from {SectorMap.FirstZone} to {SectorMap.LastZone} (to {SectorMap.LastZone * 64 + 32} m) has a sector and save chunks of its own "
        + $"({SectorMap.Width} x {SectorMap.Width} sectors, {SectorMap.Width * SectorMap.Width * IntPtr.Size / 1048576} MB), where the game has {SectorMap.VanillaWidth} x {SectorMap.VanillaWidth}.");
    }
    else
    {
      Unpatch(harmony);
      Active = false;
      ResizeLive();
      BetterContinents.Log("Sectors: the game's own sectors (zones -256 to 255).");
    }
  }

  // The loaded save's chunk list (ChunkSaveMapping.Load's postfix): a chunk past the game's 64 x 64 means the world was saved
  // with the map, and it must be loaded with it, or the objects of one chunk would be filed under another.
  internal static void MappingLoaded(Harmony harmony, IEnumerable<ZoneSystem.ChunkIndex> chunks)
  {
    if (Active)
      return;
    foreach (var chunk in chunks)
      if (SectorMap.IsWideChunk(chunk.Chunk))
      {
        ForcedBySave = true;
        BetterContinents.Log($"Sectors: the save has a chunk ({chunk.Chunk & 0xFF}, {chunk.Chunk >> 8}) beyond the game's 64 x 64, so it was saved with the wide sectors.");
        Switch(harmony, true, null, null);
        return;
      }
  }

  private static bool CanSwitch()
  {
    var man = ZDOMan.instance;
    return man == null || (man.m_objectsByID.Count == 0 && man.m_portalObjects.Count == 0);
  }

  // The live ZDOMan's array for the sectors now in use (it is empty: CanSwitch).
  private static void ResizeLive()
  {
    var man = ZDOMan.instance;
    if (man == null)
      return;
    int width = Active ? SectorMap.Width : SectorMap.VanillaWidth;
    man.m_objectsBySector = new List<ZDO>[width * width];
  }

  // ---- loading a save made before ------------------------------------------------------------------------------------

  // ZDOMan.LoadChunks's postfix, for a save made with the game's own sectors (0.10.2 and before, or a world that has no map):
  //
  // 1. Everything beyond the game's sectors was filed in chunk (0, 0). That chunk stays in the save's chunk list as it was, and is not
  //    written again unless it changes, so the objects, now in chunks of their own, would be in the save twice. The chunk is taken out
  //    of the list: it is written again with what is left in its own zones if there is any, and the file it was is removed with the
  //    next load's orphans.
  // 2. The 8 x 8 zones at -248 to -241 by -256 to -249 were chunk (1, 0), the key the game's portals are saved under (ZoneSystem.ChunkPortal),
  //    which loads every object in it as a portal. An object there that is not a portal is put in its sector, and the portals are saved
  //    again without it (the objects themselves go to chunk (159, 0)).
  internal static void LoadedChunks(ZDOMan man)
  {
    if (!Active || man.m_chunkSaveMapping is not { } mapping)
      return;
    foreach (var chunk in mapping.Chunks.Keys)
      if (SectorMap.IsWideChunk(chunk.Chunk))
      {
        // A save made with the wide sectors has no object in the portals' chunk but portals. One that has (every object of its zones
        // is also in chunk (159, 0)) was saved again by a Better Continents without the wide sectors, or by the game alone.
        if (Game.instance?.PortalPrefabHash is { } portals && man.m_portalObjects.Values.Sum(l => l.Count(z => !portals.Contains(z.GetPrefab()))) is var strays and > 0)
          BetterContinents.LogWarning($"Sectors: the portals' chunk (1, 0) of this save holds {strays} objects that are not portals. This world was saved after the wide sectors by "
            + "a version of Better Continents without them (or without Better Continents), which files those zones' objects there: they are in the world twice.");
        return;
      }
    int far = 0;
    foreach (var list in man.m_objectsBySector)
      if (list != null)
        foreach (var zdo in list)
        {
          var zone = ZoneSystem.GetZone(zdo.GetPosition());
          if (zone.x < SectorMap.VanillaFirstZone || zone.x > SectorMap.VanillaLastZone || zone.y < SectorMap.VanillaFirstZone || zone.y > SectorMap.VanillaLastZone)
            far++;
        }
    if (far > 0)
    {
      var first = new ZoneSystem.ChunkIndex(0, 0);
      BetterContinents.Log($"Sectors: {far} objects lie beyond the game's sectors, where an earlier save filed them in chunk (0, 0): "
        + (mapping.Chunks.Remove(first) ? "that chunk is written again, with what lies in its own zones, and the rest in chunks of their own, at the next save." : "they are written in chunks of their own at the next save."));
    }
    int misfiled = 0;
    var portalPrefabs = Game.instance?.PortalPrefabHash;
    if (portalPrefabs != null)
      foreach (var sector in man.m_portalObjects.Keys.ToList())
      {
        var list = man.m_portalObjects[sector];
        for (int i = list.Count - 1; i >= 0; i--)
          if (!portalPrefabs.Contains(list[i].GetPrefab()))
          {
            var zdo = list[i];
            list.RemoveAt(i);
            man.InitialAddToSector(zdo, zdo.GetSectorIndex());
            misfiled++;
          }
        if (list.Count == 0)
          man.m_portalObjects.Remove(sector);
      }
    if (misfiled > 0)
    {
      man.SetDirtyPortals();
      BetterContinents.Log($"Sectors: {misfiled} objects of the zones the game's save files as its portals' chunk (1, 0) are put in their sectors; they are written in chunk (159, 0) at the next save.");
    }
  }

  // ---- the patches --------------------------------------------------------------------------------------------------

  // Bound once, at load, like Better Continents' other plain patches: they do nothing while the map is off (a save that has
  // no wide chunk, and a world that needs no map).
  [HarmonyPatch(typeof(ChunkSaveMapping), nameof(ChunkSaveMapping.Load))]
  private static class MappingLoadPatch
  {
    private static void Postfix(ChunkSaveMapping __instance) => MappingLoaded(BetterContinents.HarmonyInstance, __instance.Chunks.Keys);
  }

  [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
  private static class LoadChunksPatch
  {
    private static void Postfix(ZDOMan __instance) => LoadedChunks(__instance);
  }

  // A game method and this class's patches on it.
  private sealed class Hook(string name, Func<MethodBase?> target, string? prefix = null, string? transpiler = null)
  {
    public readonly string Name = name;
    public MethodBase? Target() => target();
    public MethodInfo? Prefix => Method(prefix);
    public MethodInfo? Transpiler => Method(transpiler);
    private static MethodInfo? Method(string? patch) => patch == null ? null : AccessTools.Method(typeof(WorldSectors), patch);

    public void Patch(Harmony harmony)
    {
      var method = target() ?? throw new InvalidOperationException($"{Name} is not in this game");
      var processor = harmony.CreateProcessor(method);
      if (Method(prefix) is { } before) processor.AddPrefix(new HarmonyMethod(before));
      if (Method(transpiler) is { } rewrite) processor.AddTranspiler(new HarmonyMethod(rewrite));
      processor.Patch();
    }

    public void Unpatch(Harmony harmony)
    {
      if (target() is not { } method)
        return;
      foreach (var patch in new[] { prefix, transpiler })
        if (Method(patch) is { } m)
          harmony.Unpatch(method, m);
    }
  }

  private static MethodBase? Zone(string name, params Type[] arguments) => AccessTools.Method(typeof(ZoneSystem), name, arguments.Length == 0 ? null : arguments);
  private static MethodBase? Man(string name) => AccessTools.Method(typeof(ZDOMan), name);

  private static readonly Hook[] Hooks =
  [
    new("ZoneSystem.SectorToIndex", () => Zone(nameof(ZoneSystem.SectorToIndex), typeof(int), typeof(int)), prefix: nameof(SectorToIndexPrefix)),
    new("ZoneSystem.IndicesToIndex", () => Zone(nameof(ZoneSystem.IndicesToIndex), typeof(uint), typeof(uint)), prefix: nameof(IndicesToIndexPrefix)),
    new("ZoneSystem.IndexToSector", () => Zone(nameof(ZoneSystem.IndexToSector)), prefix: nameof(IndexToSectorPrefix)),
    new("ZoneSystem.GetZonesChunk", () => Zone(nameof(ZoneSystem.GetZonesChunk)), prefix: nameof(GetZonesChunkPrefix)),
    new("ZoneSystem.GetZoneFromChunk", () => Zone(nameof(ZoneSystem.GetZoneFromChunk)), prefix: nameof(GetZoneFromChunkPrefix)),
    new("ZDOMan.ResetSectorArray", () => Man(nameof(ZDOMan.ResetSectorArray)), prefix: nameof(ResetSectorArrayPrefix)),
    new("ZDOMan.GetSaveClonePerChunk", () => Man(nameof(ZDOMan.GetSaveClonePerChunk)), transpiler: nameof(GetSaveClonePerChunkTranspiler)),
    new("ZDOMan.AddObjectsPerChunk", () => Man(nameof(ZDOMan.AddObjectsPerChunk)), transpiler: nameof(AddObjectsPerChunkTranspiler)),
    new("ZDOMan.DecideChunkSize", () => Man(nameof(ZDOMan.DecideChunkSize)), transpiler: nameof(DecideChunkSizeTranspiler)),
    new("Utils.SmallPosition", () => AccessTools.Method(typeof(global::Utils), nameof(global::Utils.SmallPosition)), prefix: nameof(SmallPositionPrefix)),
  ];

  // For the offline tests: each hook's name, the game method it patches, and its patch methods.
  internal static IEnumerable<(string Name, MethodBase? Target, MethodInfo? Prefix, MethodInfo? Transpiler)> HookList() =>
    Hooks.Select(h => (h.Name, h.Target(), h.Prefix, h.Transpiler));

  // All or none: half of these patches would file an object under one sector and look for it in another.
  private static bool TryPatch(Harmony harmony)
  {
    var done = new List<Hook>();
    try
    {
      foreach (var hook in Hooks)
      {
        hook.Patch(harmony);
        done.Add(hook);
      }
      return true;
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Sectors: could not patch {Hooks[done.Count].Name} ({e.Message}), so the game's own sectors stay on: objects beyond 16.4 km share one sector, as before.");
      done.Add(Hooks[done.Count]);
      foreach (var hook in done)
        try
        {
          hook.Unpatch(harmony);
        }
        catch (Exception undo)
        {
          BetterContinents.LogError($"Sectors: could not take the patch off {hook.Name}: {undo.Message}");
        }
      return false;
    }
  }

  private static void Unpatch(Harmony harmony)
  {
    foreach (var hook in Hooks)
      try
      {
        hook.Unpatch(harmony);
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"Sectors: could not take the patch off {hook.Name}: {e.Message}");
      }
  }

  // The names of the game's parameters (ZoneSystem.SectorToIndex(int sectorX, int sectorY), IndicesToIndex(uint x, uint y),
  // IndexToSector(uint index), GetZonesChunk(SectorIndex sectorIndex), GetZoneFromChunk(ChunkIndex chunkIndex)).
  private static bool SectorToIndexPrefix(int sectorX, int sectorY, ref ZoneSystem.SectorIndex __result)
  {
    __result = new ZoneSystem.SectorIndex(SectorMap.SectorToIndex(sectorX, sectorY));
    return false;
  }

  private static bool IndicesToIndexPrefix(uint x, uint y, ref ZoneSystem.SectorIndex __result)
  {
    __result = new ZoneSystem.SectorIndex(SectorMap.IndicesToIndex(x, y));
    return false;
  }

  private static bool IndexToSectorPrefix(uint index, ref Vector2s __result)
  {
    var (x, y) = SectorMap.IndexToSector(index);
    __result = new Vector2s(x, y);
    return false;
  }

  private static bool GetZonesChunkPrefix(ZoneSystem.SectorIndex sectorIndex, ref ZoneSystem.ChunkIndex __result)
  {
    __result = new ZoneSystem.ChunkIndex(SectorMap.Chunk(sectorIndex.Sector), 0);
    return false;
  }

  private static bool GetZoneFromChunkPrefix(ZoneSystem.ChunkIndex chunkIndex, ref (int x, int y) __result)
  {
    __result = SectorMap.ZoneFromChunk(chunkIndex.Chunk);
    return false;
  }

  // The sector array at the width in use.
  private static bool ResetSectorArrayPrefix(ref List<ZDO>[] ___m_objectsBySector)
  {
    ___m_objectsBySector = new List<ZDO>[SectorMap.Width * SectorMap.Width];
    return false;
  }

  // Utils.SmallPosition(Vector3 v): (true, the position as two shorts) for an object at y = 0 whose x and z are whole numbers
  // in a short's range, else (false, _). The game also answers (true, +-20000) for an x or z past +-20000 when the other is
  // whole, which saves a _ZoneCtrl (the zone's marker, at the zone's centre, y = 0) 25 km out as one at 20 km. This is the
  // game's function without those two clamps; for every position within 20,000 m it answers as the game does.
  private static bool SmallPositionPrefix(Vector3 v, ref (bool, Vector2s) __result)
  {
    __result = SmallPosition(v.x, v.y, v.z);
    return false;
  }

  // The game's Utils.FloatIsZero: the bits of +0 (not -0).
  private static unsafe bool IsPositiveZero(float f) => *(int*)&f == 0;

  internal static (bool, Vector2s) SmallPosition(float x, float y, float z)
  {
    if (!IsPositiveZero(y))
      return (false, Vector2s.zero);
    var item = new Vector2s((short)x, (short)z);
    return ((float)item.x).Equals(x) && ((float)item.y).Equals(z) ? (true, item) : (false, Vector2s.zero);
  }

  // ---- the game's save planning, widened -----------------------------------------------------------------------------

  // The value an int constant instruction loads, whichever of the game's forms it is in; null for any other instruction.
  internal static int? IntLoad(CodeInstruction code)
  {
    var op = code.opcode;
    if (op == OpCodes.Ldc_I4) return (int)code.operand;
    if (op == OpCodes.Ldc_I4_S) return Convert.ToInt32(code.operand);
    if (op == OpCodes.Ldc_I4_M1) return -1;
    if (op == OpCodes.Ldc_I4_0) return 0;
    if (op == OpCodes.Ldc_I4_1) return 1;
    if (op == OpCodes.Ldc_I4_2) return 2;
    if (op == OpCodes.Ldc_I4_3) return 3;
    if (op == OpCodes.Ldc_I4_4) return 4;
    if (op == OpCodes.Ldc_I4_5) return 5;
    if (op == OpCodes.Ldc_I4_6) return 6;
    if (op == OpCodes.Ldc_I4_7) return 7;
    if (op == OpCodes.Ldc_I4_8) return 8;
    return null;
  }

  // The instruction now loads this value (it keeps its labels and blocks).
  private static void SetIntLoad(CodeInstruction code, int value)
  {
    code.opcode = OpCodes.Ldc_I4;
    code.operand = value;
  }

  private static bool Calls(CodeInstruction code, string name) =>
    (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodBase { Name: var called } && called == name;

  private static InvalidOperationException Unexpected(string method, string what) =>
    new($"Sectors: {what} is not where it was expected in {method}.");

  // ZDOMan.GetSaveClonePerChunk: it counts the objects of each 8 x 8 chunk of the sector array in an array of 64 x 64
  // (4096) numbers, merges 2 x 2 of them into one of 32 x 32 (1024) where it is worth it, then 16 x 16 and 8 x 8 (DecideChunkSize),
  // and collects each chunk's objects (AddObjectsPerChunk, with the size of the number array it reads: 64, 32, 16 and 8 across).
  // Here: the 512 sectors it loops over a side are 2048, the arrays have 16 times the numbers and the calls give the array's
  // new side, 256, 128, 64 and 32.
  internal static IEnumerable<CodeInstruction> GetSaveClonePerChunkTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    const string method = "ZDOMan.GetSaveClonePerChunk";
    var codes = instructions.ToList();
    // The four arrays of numbers.
    int[] arrays = [4096, 1024, 256, 64];
    int found = 0;
    for (int i = 1; i < codes.Count; i++)
      if (codes[i].opcode == OpCodes.Newarr && codes[i].operand is Type { Name: "Int32" } && IntLoad(codes[i - 1]) is { } length)
      {
        if (found >= arrays.Length || length != arrays[found])
          throw Unexpected(method, $"an array of {length} numbers");
        SetIntLoad(codes[i - 1], length * 16);
        found++;
      }
    if (found != arrays.Length)
      throw Unexpected(method, "the four arrays of numbers");
    // The loops over the sector array's 512 places on each axis.
    int loops = 0;
    foreach (var code in codes)
      if (IntLoad(code) == SectorMap.VanillaWidth)
      {
        SetIntLoad(code, SectorMap.Width);
        loops++;
      }
    if (loops != 2)
      throw Unexpected(method, "the two loops over 512 sectors");
    // The sizes of the number arrays in the calls: this, size, chunk size, two arrays.
    int[] decide = [64, 32, 16], add = [64, 32, 16, 8], decideChunk = [1, 2, 3], addChunk = [0, 1, 2, 3];
    int decided = 0, added = 0;
    for (int i = 0; i < codes.Count; i++)
    {
      bool isDecide = Calls(codes[i], nameof(ZDOMan.DecideChunkSize)), isAdd = Calls(codes[i], nameof(ZDOMan.AddObjectsPerChunk));
      if (!isDecide && !isAdd)
        continue;
      if (i < 5 || IntLoad(codes[i - 4]) is not { } size)
        throw Unexpected(method, $"the size an array is called with ({codes[i].operand})");
      var expected = isDecide ? decide : add;
      var expectedChunk = isDecide ? decideChunk : addChunk;
      int index = isDecide ? decided++ : added++;
      if (index >= expected.Length || size != expected[index] || IntLoad(codes[i - 3]) != expectedChunk[index])
        throw Unexpected(method, $"the size {size} of a call to {(isDecide ? "DecideChunkSize" : "AddObjectsPerChunk")}");
      SetIntLoad(codes[i - 4], size * 4);
    }
    if (decided != decide.Length || added != add.Length)
      throw Unexpected(method, "the calls that give the arrays' sizes");
    return codes;
  }

  // ZDOMan.AddObjectsPerChunk(size, ...) and DecideChunkSize(size, ...) turn the number array's side into the chunks it
  // stands for with 64 / size: how many of the 64 chunks of a side one number is.
  internal static IEnumerable<CodeInstruction> AddObjectsPerChunkTranspiler(IEnumerable<CodeInstruction> instructions) =>
    ChunksAcross("ZDOMan.AddObjectsPerChunk", instructions);

  internal static IEnumerable<CodeInstruction> DecideChunkSizeTranspiler(IEnumerable<CodeInstruction> instructions) =>
    ChunksAcross("ZDOMan.DecideChunkSize", instructions);

  // 64 becomes 256 where it is divided by the size (the next instruction loads argument 1, the size, then divides).
  private static List<CodeInstruction> ChunksAcross(string method, IEnumerable<CodeInstruction> instructions)
  {
    var codes = instructions.ToList();
    int found = 0;
    for (int i = 0; i + 2 < codes.Count; i++)
      if (IntLoad(codes[i]) == 64 && codes[i + 1].opcode == OpCodes.Ldarg_1 && (codes[i + 2].opcode == OpCodes.Div || codes[i + 2].opcode == OpCodes.Div_Un))
      {
        SetIntLoad(codes[i], 256);
        found++;
      }
    if (found != 1)
      throw Unexpected(method, "the 64 chunks a side, divided by the size");
    return codes;
  }
}

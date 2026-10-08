// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
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
// What is patched, while a world needs it (Wanted: Better Continents is on for the world, its World Size + Edge Size is past
// 16,350 m, and it is a wide world: its settings say so (WideSectors), or this machine is told to convert the worlds it runs (the
// Wide Sectors setting, On); or the save already holds chunks of the wider area, ForcedBySave):
//   - the five functions that turn a zone into a sector, a sector into a chunk, and a chunk into its zones (SectorMap),
//   - the array's allocation (ZDOMan.ResetSectorArray: 2048 x 2048 sectors, 32 MB),
//   - the three save-planning methods of ZDOMan (GetSaveClonePerChunk, AddObjectsPerChunk, DecideChunkSize), whose 64 x 64
//     chunks and 512 x 512 sectors are constants of their code, which a transpiler widens to 256 x 256 and 2048 x 2048,
//   - Utils.SmallPosition, which writes an object at y = 0 (every zone's _ZoneCtrl) in two shorts and, when one of its
//     coordinates is not a short (a zone 512 or more out, past 32,767 m) and the other is whole, clamps it to 20,000 m.
// A world that does not need it keeps the game's own code, byte for byte.
//
// The inner area (zones -256 to 255) keeps the game's sector order and chunk numbers, so a vanilla-size world's save does not
// change. A world saved with the game's sectors, whose far objects are all filed in chunk (0, 0), is converted when it loads on a
// machine that is told to (Wide Sectors On): the objects move to chunks of their own, and the game saves a copy of the old save
// before the first save of the new one (LoadedChunks).
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
    // -241 by -256 to -249, from 22.1 km out) is kept at chunk (159, 0) instead, where the zones 1016 to 1023 by -256 to -249 would
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

  // How far out a world has objects: its own size, or Expand World Size's when that has sent one and is larger.
  // A world that keeps the game's own terrain (GameTerrain) has the game's own size, whatever its settings' sizes hold.
  internal static float Reach(BetterContinents.BetterContinentsSettings settings, WorldGeometry? expandWorldSize) =>
    Math.Max(Reach(settings.GameTerrain ? WorldGeometry.Vanilla : settings.OwnGeometry), Reach(expandWorldSize));

  // This machine's Wide Sectors setting; Auto where the config is not bound (the offline suites, which can also set it).
  internal static WideSectorsMode Mode => ModeOverride ?? BetterContinents.ConfigWideSectors?.Value ?? WideSectorsMode.Auto;
  internal static WideSectorsMode? ModeOverride;

  // Whether a world needs the map: Better Continents is on for it, its size (its own, or Expand World Size's when that has sent one) reaches
  // past the game's sectors, and it is a wide world: its settings say so (WideSectors: it was made wide, or converted), or this machine is told
  // to convert the worlds it runs (On) and runs this one (hosted: single player, the host or a dedicated server). A client never decides: it
  // follows the settings it was sent, whatever its own Wide Sectors says. A world whose save holds chunks of the wider area needs it too
  // (ForcedBySave), which Update adds.
  internal static bool Wanted(BetterContinents.BetterContinentsSettings settings, WorldGeometry? expandWorldSize, WideSectorsMode mode, bool hosted) =>
    settings.EnabledForThisWorld && Reach(settings, expandWorldSize) > VanillaReach && (settings.WideSectors || (mode == WideSectorsMode.On && hosted));

  // A new world takes its settings (BetterContinentsSettings.FromConfig, and a preset's world when it is made): Auto and On make a world that
  // reaches past the game's sectors a wide one, which its settings say for good (WideSectors); Off leaves it on the game's, and a world that is
  // wide already (a preset made so) stays wide. Whether the world is wide.
  internal static bool NewWorld(BetterContinents.BetterContinentsSettings settings, WideSectorsMode mode, WorldGeometry? expandWorldSize)
  {
    if (settings.WideSectors || !settings.EnabledForThisWorld)
      return settings.WideSectors;
    var reach = Reach(settings, expandWorldSize);
    if (reach <= VanillaReach)
      return false;
    if (mode == WideSectorsMode.Off)
    {
      BetterContinents.Log($"Sectors: Wide Sectors is Off, so this new world, which reaches {reach:0} m, has the game's own sectors: what lies beyond 16.4 km shares one sector, "
        + "and beyond 22.1 km some of it shares the game's portal file.");
      return false;
    }
    settings.WideSectors = true;
    BetterContinents.Log($"Sectors: this new world reaches {reach:0} m, so it is made with wide sectors (Wide Sectors is {mode}): every zone out to {SectorMap.LastZone * 64 + 32} m has a sector and save chunks of its own.");
    return true;
  }

  // Whether the patches are on.
  internal static bool Active { get; private set; }

  // The loaded save holds chunks only this map writes, so the world keeps the map whatever its settings now say; until the
  // next session starts (SessionStarts).
  internal static bool ForcedBySave { get; private set; }

  // This machine runs the world of this session (single player, the host, a dedicated server): it loads the save, and may convert it.
  // A client does not.
  internal static bool Hosted { get; private set; }

  // What DynamicPatch asks for: the map for a world that needs it, off for any other. With Wide Sectors On, a world this machine runs that
  // reaches past the game's sectors is a wide world from then on: its settings say so, and are saved so.
  internal static void Update(Harmony harmony, BetterContinents.BetterContinentsSettings settings, WorldGeometry? expandWorldSize)
  {
    var mode = Mode;
    Switch(harmony, ForcedBySave || Wanted(settings, expandWorldSize, mode, Hosted), settings, expandWorldSize);
    bool reaches = settings.EnabledForThisWorld && Reach(settings, expandWorldSize) > VanillaReach;
    if (Active && Hosted && reaches && mode == WideSectorsMode.On && !settings.WideSectors)
    {
      settings.WideSectors = true;
      BetterContinents.Log("Sectors: Wide Sectors is On, so this world is a wide world from now on: its settings say so, and are saved with the world.");
    }
    if (Hosted && reaches && !Active && !settings.WideSectors && mode == WideSectorsMode.Auto && !warnedKeeps)
    {
      warnedKeeps = true;
      BetterContinents.Log("Sectors: this world reaches past 16,350 m. Unless its save was made with wide sectors, it keeps the game's own sectors while Wide Sectors is Auto: what lies beyond "
        + "16.4 km shares one sector. Wide Sectors On converts it when it loads (the game saves a copy of the world first).");
    }
    if (Hosted && Active && mode == WideSectorsMode.Off && (settings.WideSectors || ForcedBySave))
      NoteOffStays(settings.WideSectors ? "its settings say it is a wide world" : "its save has chunks only the wide sectors write");
  }

  // A new session (ZNet.SetServer, or the main menu): nothing is known of its save yet. hosted: this machine runs the world.
  internal static void SessionStarts(bool hosted)
  {
    ForcedBySave = false;
    Hosted = hosted;
    warnedKeeps = warnedOffStays = false;
  }

  private static bool warnedBeyond, warnedKeeps, warnedOffStays;
  // What the last refused switch asked for (said once).
  private static bool? warnedStay;

  // Wide Sectors is Off, and the world is wide: once a session, why it stays.
  private static void NoteOffStays(string why)
  {
    if (warnedOffStays)
      return;
    warnedOffStays = true;
    BetterContinents.Log($"Sectors: Wide Sectors is Off, but this world stays wide: {why}.");
  }

  // On or off. The sectors an existing game holds are not moved: a change comes only while no object is in them (the main
  // menu, a client before the server's objects arrive, a server before its world loads), or it is left for the next session.
  private static void Switch(Harmony harmony, bool on, BetterContinents.BetterContinentsSettings? settings, WorldGeometry? expandWorldSize)
  {
    if (on && !warnedBeyond && settings != null && Reach(settings, expandWorldSize) > MaxReach)
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
      {
        // A world whose save is wide has its own explanation (MappingLoaded).
        if (!ForcedBySave)
          BetterContinents.LogError("Sectors: the world runs on the game's own sectors: objects beyond 16.4 km share one sector, as before.");
        return;
      }
      Active = true;
      ResizeLive();
      BetterContinents.Log($"Sectors: every zone from {SectorMap.FirstZone} to {SectorMap.LastZone} (to {SectorMap.LastZone * 64 + 32} m) has a sector and save chunks of its own "
        + $"({SectorMap.Width} x {SectorMap.Width} sectors, {SectorMap.Width * SectorMap.Width * IntPtr.Size / 1048576} MB), where the game has {SectorMap.VanillaWidth} x {SectorMap.VanillaWidth}.");
    }
    else
    {
      // A patch left on while the array is the game's would file an object in a sector that is not there: the sectors stay wide, and the
      // next session tries again.
      if (!Unpatch(harmony))
      {
        BetterContinents.LogError("Sectors: the wide sectors could not be taken off, so they stay on until the next session.");
        return;
      }
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
        if (!Active)
          RefuseToLoad(chunk);
        else if (Hosted && Mode == WideSectorsMode.Off)
          NoteOffStays("its save has chunks only the wide sectors write");
        return;
      }
  }

  // The save has chunks only the wide sectors write, and they could not be turned on. Loaded on the game's sectors, every object beyond 16.4 km
  // would go into one list and be saved again in chunk (0, 0) on top of the chunks it is in: twice. The game's own way to stop is its load
  // error (ZNet.m_loadError): the game then saves nothing ("Skipping world save"), and a game that runs the world goes back to the menu
  // (Game.FixedUpdate: "World load failed, exiting without save"), where the error is shown. A dedicated server does not stop by itself:
  // it would run a world nobody could save, so it stops here, as it does for a failed alt-biome planting (AltBiomeControl.FailLoad).
  private static void RefuseToLoad(ZoneSystem.ChunkIndex chunk)
  {
    var world = ZNet.World?.m_name ?? "?";
    var message = $"Better Continents: the world '{world}' was saved with wide sectors (its save has a chunk ({chunk.Chunk & 0xFF}, {chunk.Chunk >> 8}) beyond the game's 64 x 64), "
      + "which could not be turned on, so it is not loaded, and nothing is saved over it: on the game's own sectors every object beyond 16.4 km would be saved twice. The log names the patch that failed.";
    BetterContinents.LogError(message);
    BetterContinents.LastConnectionError = message;
    ZNet.m_loadError = true;
    try
    {
      BetterContinents.instance?.StartCoroutine(StopAfterRefusal());
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Sectors: could not stop the session cleanly: {e.Message}");
    }
  }

  private static IEnumerator StopAfterRefusal()
  {
    yield return null;
    var net = ZNet.instance;
    if (net != null && net.IsDedicated())
    {
      BetterContinents.LogError("Sectors: stopping the dedicated server because the world was not loaded (see the error above).");
      Application.Quit(1);
      yield break;
    }
    ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
    Game.instance?.Logout(save: false);
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

  // ---- loading a save made with the game's sectors --------------------------------------------------------------------

  // The game's own zones, which its 512 x 512 sectors file one by one.
  private static bool InGame(Vector2s zone) =>
    zone.x >= SectorMap.VanillaFirstZone && zone.x <= SectorMap.VanillaLastZone && zone.y >= SectorMap.VanillaFirstZone && zone.y <= SectorMap.VanillaLastZone;

  // ZDOMan.LoadChunks's postfix, for a save made with the game's own sectors (a world the wide sectors are now turned on for, by Wide Sectors
  // On or by its settings). What it holds in the wrong chunk for them:
  //
  // 1. Everything beyond the game's sectors was filed in chunk (0, 0). The objects are in their sectors by now (the load files them by
  //    position), but the save does not know it: chunk (0, 0) stays in its chunk list with the file it has, which is written again only when it
  //    changes, so the objects, saved in chunks of their own, would be in the save twice. Every such object is marked changed, so that its chunk is
  //    written by any version of the game (the one this is written for also writes a chunk that is missing from the list; an older one only a
  //    changed one). Chunk (0, 0) is marked changed too when anything is left in its zones (and the server's ghost zones): the game writes it
  //    again under its next version, which is a new file name, and removes the old file once the new chunk list is written. Taken out of the list
  //    it would be written under version 1, which can be the name of the file the saved list still has, over it, before the new list exists.
  //    With nothing left in it, it leaves the list and nothing is written in its place; the old file goes with the next load's orphans.
  // 2. The 8 x 8 zones at -248 to -241 by -256 to -249 were chunk (1, 0), the key the game's portals are saved under (ZoneSystem.ChunkPortal),
  //    which loads every object in it as a portal. An object there that is not a portal is put in its sector and marked changed, and the portals
  //    are saved again without it (the objects themselves go to chunk (159, 0)).
  //
  // Moving any object asks the game to copy the old save before its first save (World.m_createBackupBeforeSaving), and says so: an older Better
  // Continents, or the game alone, that saves the converted world writes those objects twice.
  //
  // A save that has chunks of the wide sectors needs none of this; it is only looked at, for what an older version wrote over it (WarnOfTwins).
  internal static void LoadedChunks(ZDOMan man)
  {
    if (!Active || man.m_chunkSaveMapping is not { } mapping)
      return;
    var portals = Game.instance?.PortalPrefabHash;
    if (mapping.Chunks.Keys.Any(chunk => SectorMap.IsWideChunk(chunk.Chunk)))
    {
      WarnOfTwins(man, mapping, portals);
      return;
    }
    var beyond = ObjectsBeyond(man);
    var block = portals == null ? new List<ZDO>() : TakeFromPortalChunk(man, portals);
    if (beyond.Count == 0 && block.Count == 0)
      return;
    foreach (var zdo in beyond.Concat(block))
      man.SetDirtySector(zdo);
    var first = new ZoneSystem.ChunkIndex(0, 0);
    string pile = "";
    if (beyond.Count > 0 && mapping.Chunks.ContainsKey(first))
    {
      if (HoldsObjects(man, first.Chunk))
      {
        man.SetDirtyChunks(new ZoneSystem.SectorIndex(0));
        pile = "chunk (0, 0) is written again, under a new file name, with what lies in its own zones";
      }
      else
      {
        mapping.Chunks.Remove(first);
        pile = "chunk (0, 0) leaves the chunk list: nothing lies in its own zones";
      }
    }
    if (block.Count > 0)
      man.SetDirtyPortals();
    if (ZNet.World is { } world)
      world.m_createBackupBeforeSaving = true;
    BetterContinents.Log($"Sectors: {beyond.Count} objects lie beyond the game's sectors, where the save filed them in chunk (0, 0), and {block.Count} are of the zones the game's save files as its portals' chunk (1, 0) "
      + $"(chunk (159, 0) here): they are written in chunks of their own at the next save{(pile.Length > 0 ? "; " + pile : "")}.");
    BetterContinents.LogWarning($"Sectors: this world was saved with the game's sectors and is converted to the wide ones. Before its first save the game saves a copy of it as "
      + $"'{ZNet.World?.m_worldName ?? "<world>"}_backup_<date and time>', beside it. Do not open or save the converted world with a Better Continents that has no wide sectors (or without Better Continents): "
      + "it would write those objects twice.");
  }

  // The objects that lie in the wide map's zones past the game's: the game's sectors filed them in sector 0. An object past the map is in sector 0
  // in both (the server's ghost zones are, wherever the world's size reaches), so it is not one.
  private static List<ZDO> ObjectsBeyond(ZDOMan man)
  {
    var beyond = new List<ZDO>();
    foreach (var list in man.m_objectsBySector)
      if (list != null)
        foreach (var zdo in list)
        {
          var zone = ZoneSystem.GetZone(zdo.GetPosition());
          if (!InGame(zone) && SectorMap.SectorToIndex(zone.x, zone.y) != 0)
            beyond.Add(zdo);
        }
    return beyond;
  }

  // The objects of the portals' chunk that are not portals: put in their sectors.
  private static List<ZDO> TakeFromPortalChunk(ZDOMan man, List<int> portals)
  {
    var taken = new List<ZDO>();
    foreach (var sector in man.m_portalObjects.Keys.ToList())
    {
      var list = man.m_portalObjects[sector];
      for (int i = list.Count - 1; i >= 0; i--)
        if (!portals.Contains(list[i].GetPrefab()))
        {
          var zdo = list[i];
          list.RemoveAt(i);
          man.InitialAddToSector(zdo, zdo.GetSectorIndex());
          taken.Add(zdo);
        }
      if (list.Count == 0)
        man.m_portalObjects.Remove(sector);
    }
    return taken;
  }

  // Whether any object is in the sectors of a chunk: the game plans a chunk when its sectors hold any (GetSaveClonePerChunk counts them all).
  private static bool HoldsObjects(ZDOMan man, ushort chunk)
  {
    uint x0 = (uint)(chunk & 0xFF) * SectorMap.ZonesPerChunk, y0 = (uint)(chunk >> 8) * SectorMap.ZonesPerChunk;
    for (uint y = y0; y < y0 + SectorMap.ZonesPerChunk; y++)
      for (uint x = x0; x < x0 + SectorMap.ZonesPerChunk; x++)
        if (man.m_objectsBySector[SectorMap.IndicesToIndex(x, y)] is { Count: > 0 })
          return true;
    return false;
  }

  // A save made with the wide sectors that a version without them saved again: its chunk (1, 0), the portals', holds the objects of the zones
  // that are chunk (159, 0) here, and its chunk (0, 0) holds every object beyond 16.4 km. The objects load twice. Nothing repairs them; this says so.
  private static void WarnOfTwins(ZDOMan man, ChunkSaveMapping mapping, List<int>? portals)
  {
    // A save made with the wide sectors has no object in the portals' chunk but portals.
    if (portals != null && man.m_portalObjects.Values.Sum(list => list.Count(zdo => !portals.Contains(zdo.GetPrefab()))) is var strays and > 0)
      BetterContinents.LogWarning($"Sectors: the portals' chunk (1, 0) of this save holds {strays} objects that are not portals. This world was saved after the wide sectors by "
        + "a version of Better Continents without them (or without Better Continents), which files those zones' objects there: they are in the world twice.");
    // Nor has chunk (0, 0) an object that lies outside its own zones.
    if (mapping.Chunks.TryGetValue(new ZoneSystem.ChunkIndex(0, 0), out var pile))
    {
      int inZones = 0;
      for (uint y = 0; y < SectorMap.ZonesPerChunk; y++)
        for (uint x = 0; x < SectorMap.ZonesPerChunk; x++)
          if (man.m_objectsBySector[SectorMap.IndicesToIndex(x, y)] is { } list)
            foreach (var zdo in list)
              if (zdo.Persistent && portals?.Contains(zdo.GetPrefab()) != true)
                inZones++;
      if (pile.m_numZDOs > inZones)
        BetterContinents.LogWarning($"Sectors: chunk (0, 0) of this save holds {pile.m_numZDOs} objects, but {inZones} of them lie in its own zones. This world was saved after the wide sectors by "
          + $"a version of Better Continents without them (or without Better Continents), which files everything beyond 16.4 km in chunk (0, 0): the other {pile.m_numZDOs - inZones} are in the world twice.");
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
      BetterContinents.LogError($"Sectors: could not patch {Hooks[done.Count].Name} ({e.Message}); the patches of the others are taken off again.");
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

  // For the offline tests: called with a hook's name before its patch is taken off (one that throws is a hook that cannot be).
  internal static Action<string>? BeforeUnpatch;

  // Takes every patch off. False when one could not be: the others are patched again, so that what stays is all of it, the wide sectors as they
  // were, never half of them.
  private static bool Unpatch(Harmony harmony)
  {
    var removed = new List<Hook>();
    bool failed = false;
    foreach (var hook in Hooks)
      try
      {
        BeforeUnpatch?.Invoke(hook.Name);
        hook.Unpatch(harmony);
        removed.Add(hook);
      }
      catch (Exception e)
      {
        failed = true;
        BetterContinents.LogError($"Sectors: could not take the patch off {hook.Name}: {e.Message}");
      }
    if (failed)
      foreach (var hook in removed)
        try
        {
          hook.Patch(harmony);
        }
        catch (Exception e)
        {
          BetterContinents.LogError($"Sectors: could not patch {hook.Name} again: {e.Message}");
        }
    return !failed;
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
  // in a short's range (within 32,767 m), else (false, _). The game also answers (true, +-20000) when one of them is such a whole
  // number and the other, past +-20000, is not (it is beyond a short, or has a fraction): that saves a _ZoneCtrl (the zone's marker,
  // at the zone's centre, y = 0) of a zone 512 or more out (32,768 m and beyond: no short holds it) as one at 20 km, and it is all
  // a _ZoneCtrl ever meets of it, as the zone centres are whole. This is the game's function without those two clamps; for every
  // position within 20,000 m, and for every position whose coordinates are both whole shorts, it answers as the game does.
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

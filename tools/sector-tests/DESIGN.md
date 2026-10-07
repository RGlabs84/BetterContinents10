<!-- Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3). -->
# Sectors for worlds past the game's 16 km (Better Continents 0.10.3)

What the game does with an object that is far from the centre of its world, what `WorldSectors.cs` does about it, why each
piece is there, and what was measured. Game code is Valheim 1.0.17 (`libs-Tools/Decompiled_1.0.17/assembly_valheim/<Type>.cs`;
the dedicated server is a different build, `libs-Tools/1.0/DECOMPILED/assembly_valheim_SERVER.decompiled.cs`: the IL of every
method named here is the same in both, which `tools/sector-tests` checks).

## 1. The game

### Sectors
* `ZoneSystem.SectorToIndex(int sectorX, int sectorY)` (ZoneSystem.cs:2992) maps zones -256..255 on each axis into `y * 512 + x`
  (`x`, `y` = zone + 256) and answers **sector 0** for every other zone. `IndicesToIndex(uint x, uint y)` (:3008) does the same for
  places of the array, `IndexToSector` (:3022) is its inverse (nothing calls it), `GetSectorIndex(Vector3)` (:2980) floors
  `(position + 32) / 64` and calls `SectorToIndex`. Zone -256 reaches -16,416 m, zone 255 reaches 16,352 m.
* `ZNet.Awake` (ZNet.cs:336) makes `new ZDOMan(512)`; `ZDOMan.ResetSectorArray` (ZDOMan.cs:212) allocates `m_width * m_width` lists
  there, on every `ResetBeforeLoad` (:217) and on `ShutDown`. `m_objectsBySector` is read by `AddToSector`/`RemoveFromSector`/
  `InitialAddToSector` (:789-817), `FindObjects` and `FindDistantObjects` (:1433, :1450: what a server sends a peer, what ZNetScene
  instantiates), `GetAllZDOsWithPrefabIterative` (:1507, by `Length`), and the save planning. `m_portalObjects` is keyed by the
  same `SectorIndex`.
* Every object whose zone is outside the 512 x 512 shares **sector 0 with zone (-256, -256)**, and every machine that comes near
  any of them is sent the whole list (`FindObjects` for a far zone returns sector 0's list). A ZDO moved out of its sector by the
  server is `InvalidateSector`d on a client into sector 0 too (`ZDO.SetSector`, ZDO.cs:498, with the `OutsideZones` flag).
* The dedicated server's reference position with nobody on it is (1,000,000, 0, 1,000,000) (server build, line 100369): it keeps
  generating ghost zones there, so **every dedicated server's sector 0 holds about 80 `_ZoneCtrl` objects** whatever the world's size.

### Save chunks
* `ZoneSystem.GetZonesChunk(SectorIndex)` (:3072) is the sector's 8 x 8 block of zones: `(index % 512 / 8) | (index / 512 / 8) << 8`,
  8 bits on each axis (a `ushort`), so the chunk grid is 64 x 64 and the format has room for 256 x 256. `GetZoneFromChunk` (:3079)
  is its inverse (`(chunk & 0xFF) * 8 - 256`). A file is `<y:x2>_<x:x2>__<size>_<version>.chunk`; `_main.N.chunks` lists
  `(ushort chunk, byte size, uint version, int numZDOs)`; **a chunk file does not say where its objects are: `ZDOMan.LoadChunks`
  (:474) reads every file the list names and files each object by its position**. A ZDO's id is not in the file either (it is given
  at load, `ZDO.Load`), so an object that is in two files is two objects.
* `GetSaveClonePerChunk` (:1649) counts the objects of each of the 4096 chunks in `int[4096]`, `DecideChunkSize` (:1563) merges 2 x 2
  groups into chunks of 16 x 16, 32 x 32 and 64 x 64 zones (sizes 1, 2 and 3; groups that touch chunk row 0 or column 0 are never
  merged, and a group is merged only if the sub-chunks have nothing in the save and the sum is 1..99,999), and `AddObjectsPerChunk`
  (:1611) collects the objects of every chunk that is dirty or missing from the list (1.0.17's fix). The constants 4096, 1024, 256, 64
  (arrays), 512 (sector loops), 64/32/16/8 (array sides, `64 / size`) are in the code.
  Dirty chunks come from `SetDirtyChunks` (:773): `GetZonesChunk` and its three merged parents (`ChunkIndexFromIndexAndSize`, masks
  `0xFEFE`, `0xFCFC`, `0xF8F8`, which work on 8 bits per axis).
* `ZoneSystem.ChunkPortal = new ChunkIndex(1, 0)` is the key the portals are saved under, and `LoadChunks` loads every object in that
  file as a portal. Chunk (1, 0) is also the sector chunk of zones -248..-241 by -256..-249 (22.7 km from the centre). In the
  game's own sectors nothing is ever there; in a world past 22.7 km it is where the two collide: the later write of the file wins and the
  other's objects are lost, or are read as portals.
* A chunk with no objects left is never written again, so its file keeps what it held. (The game's own, not changed here.)

### Other limits met on the way
* `Vector2s` (zones) is two `short`s: +-2 million metres. `ZoneSystem`'s zone dictionaries, `ZNetScene`'s active area and
  `ZoneSystem.GetZone` are zone-based and have no 16 km limit. `Utils.FloorToInt` is only right for arguments above -64,000, and is
  only called with zone numbers and minimap pixels.
* **`Utils.SmallPosition`** (assembly_utils, :12676), used by `ZDO.Save`, writes an object with `y == 0` whose `x` and `z` are whole
  numbers as two `short`s, and **clamps** the position to +-20,000 m when the other coordinate is whole and this one is beyond
  +-20,000 m. Every zone's `_ZoneCtrl` is at `y == 0` and at a multiple of 64 m: in a world past 20 km they are saved at 20 km. The
  network (`ZDOMan.SendZDOs`) writes the full `Vector3`.
* A position in a `float` has 2^-8 m = 4 mm of resolution at 65 km; Unity's own advice is to stay within 100 km.

## 2. The design

### The map
The sector array is the chunk grid: sector index = `place_y * 2048 + place_x`, and a zone's place on an axis is
`(zone + 256) mod 2048`:

| zones | places | chunks (8 zones each) | metres |
|---|---|---|---|
| -256 .. 255 (the game's) | 0 .. 511 | 0 .. 63 | -16,416 .. 16,352 |
| 256 .. 1023 | 512 .. 1279 | 64 .. 159 | 16,352 .. 65,504 |
| -1024 .. -257 | 1280 .. 2047 | 160 .. 255 | -65,568 .. -16,416 |

* **The game's area keeps the game's chunk numbers** (a zone's place is `zone + 256` there), so a world inside it saves exactly the
  same chunks with the same objects. Only the sector *indices* differ, and they are never saved.
* 2048 x 2048 sectors = 4.2 million lists, **32 MB** of references on a 64-bit machine, on each client and the server; the game's
  is 2 MB. The save planning loops over all of them (measured: 23-27 ms, against 3-4 ms for the game's).
* `GetSaveClonePerChunk`, `AddObjectsPerChunk` and `DecideChunkSize` then need nothing but wider constants: the loops over the
  chunk grid are the loops over the array, a merged group of 2, 4 or 8 chunks across is a square of zones everywhere (64 and 160,
  where the regions meet, are multiples of 8), and the game's rule that groups at chunk row/column 0 are never merged still holds.
* **Sector 0** is still zone (-256, -256) (place 0, 0) and everything the map does not reach, as in the game; `OutsideZones` and
  `InvalidateSector` work as before.
* **Chunk (1, 0)** is the portals' key. The 64 zones that would be there are kept at chunk (159, 0) (places 1272..1279 of row 0),
  where the zones 1016..1023 by -256..-249 would have been (67 km out: nothing of a world within 65 km reaches them; those 64
  zones have no sector). Row 0 is never merged, so nothing else needs to know.

### What is patched, and only while a world needs it (`WorldSectors.Active`)
| hook | what |
|---|---|
| `ZoneSystem.SectorToIndex(int, int)`, `IndicesToIndex`, `IndexToSector`, `GetZonesChunk`, `GetZoneFromChunk` | prefixes that answer from `SectorMap` |
| `ZDOMan.ResetSectorArray` | prefix: 2048 x 2048 lists |
| `ZDOMan.GetSaveClonePerChunk`, `AddObjectsPerChunk`, `DecideChunkSize` | transpilers: 13 + 1 + 1 constants widened; any other shape of the IL is refused |
| `Utils.SmallPosition` | prefix: the game's function without the two clamps |
| `ChunkSaveMapping.Load` (postfix), `ZDOMan.LoadChunks` (postfix) | bound once, inert unless the map is on |

All ten are patched or none: a hook that cannot be patched (another mod changed the method) is logged with the hook's name, the others
are taken off, and the world runs on the game's sectors as it did before. Every replaced method has more than 20 bytes of IL (Mono
inlines smaller ones past a Harmony patch); `ChunkIndexFromXY` (13 bytes) is the one that would be inlined and nothing depends on it.

### When it is on
* `Wanted`: Better Continents is on for the world and `max(World Size + Edge Size, Expand World Size's size)` is **past 16,350 m**
  (the game reaches 16,352 m east and 16,416 m west). Any settings version, any world made by an older Better Continents.
* `ForcedBySave`: the loaded save's chunk list has a chunk past the game's 64 x 64 (`ChunkSaveMapping.Load`'s postfix). Only this
  map writes one, so the save is read with it whatever the settings now say (until the next session).
* `DynamicPatch` asks (`WorldSectors.Update`) at the world's load: on a server and in single player `ZNet.SetServer`'s prefix runs
  before `ZNet.Awake` makes the `ZDOMan`; on a client the settings arrive before PeerInfo, so before any object, and the live
  `ZDOMan`'s empty array is replaced. A change is only made while the `ZDOMan` has no object (the main menu, those two moments);
  otherwise the sectors stay as they are until the next session and the log says so once.
* A vanilla-size world is not touched: nothing is patched, nothing is logged, and a world made and saved by 0.10.2 and loaded and
  changed by this build writes the same files byte for byte (measured, section 4).

### A save made before (chunk (0, 0))
Past 16.4 km 0.10.2 filed every far object in chunk (0, 0). Left in the list, that file is not written again unless it changes
(the game writes a chunk that is dirty or missing from the list), so the objects, now in chunks of their own, would be in the save
**twice** and load as twice the objects (a duplicated chest is a duplicated chest). `ZDOMan.LoadChunks`'s postfix takes chunk (0, 0)
out of the list when the save has no wide chunk and some object is beyond the game's zones: it is written again with what is in its own
zones (and the pile of the server's ghost zones), the rest go to chunks of their own, and the old file goes with the next load's
orphans. The same postfix puts an object that is not a portal back in its sector when it came from chunk (1, 0) (a save of that block
is read as portals) and writes the portals again. Measured on a 24.5 km world made by 0.10.2: 5735 objects out of sector 0, 121
out of the portals, 0 duplicates, and the second load identical.

### Utils.SmallPosition
With the map on, positions beyond 20 km are written whole instead of clamped. A `_ZoneCtrl` of a zone 25 km out is then saved and
loaded where it is (it was at 20 km before: the 4 `_ZoneCtrl`s of the 0.10.2 test world beyond 20 km came back at 20,000 m).

### The maximum
**World Size + Edge Size 65,000 m** (zones -1024 to 1023: 65,504 m east, -65,568 m west). Why that, and not more or less:
* the chunk number has 8 bits an axis (the game's format), 256 chunks = 2048 zones, and the map needs all of them: a bigger area would
  need a new chunk format, which no older Better Continents or the game could read at all;
* the world's disc is 65,000 m in radius, which leaves 500 m for what is pushed past the edge; objects past 65.5 km share sector 0 as
  today's do past 16.4 km (the log says so once when the world's size is past 65,000 m);
* the sector array is 32 MB (about 8 MB resident until it fills), and a float has 4 mm of resolution there;
* **what limits a world's size in practice is not this**: a world of 24,500 m takes 68-85 s to start on the test machine (28 s for
  vanilla's size), and one of 65,000 m had not finished starting after 10 minutes of one core (its alt-biome grid and locations,
  `WorldSizeHelper.Layout`, are not part of this work).

### Old versions, and no Better Continents
* The settings carry no new key and no new version (`SettingsVersion` 12, `DataKey`s unchanged), on purpose: 0.8.1 to 0.10.2 read
  settings of any version above 10 the same way and have no upper limit, and **stop at a key they do not know and treat the world as
  vanilla**. A new key would therefore not make an older version refuse the world; it would make it open the world as a vanilla one
  (Better Continents off, the terrain not its own) and, for a client, skip the version check that today tells it
  "world has Better Continents enabled, but server X and client Y mod versions don't match" (`ZNetPatch`). That check stays the
  multiplayer guard: an older client is refused with a clear message. A new key would also take a number in the `DataKey` list that
  the other 0.10.3 streams may take.
* An older Better Continents (or the game alone) **does open** a world saved with the map, and loads it fine: its sectors are the
  game's, so every far object is in sector 0 as before. **Its first save writes duplicates**: measured with 0.10.2 on a 0.10.3 save,
  chunk (1, 0) was written with the 121 objects of chunk (159, 0) (the game writes a chunk missing from the list), and chunk (0, 0)
  is written with every far object, 5,742 of them in the test world, the next time anything in sector 0 changes (an object of zone
  (-256, -256), a new ghost zone of the server's). The objects are then loaded twice by anything. 0.10.3 says so in the log when it finds
  objects in the portals' chunk of a wide save, and does not try to repair them. **Do not save a world with the map in an older version.** No setting can stop it.
* The one hard stop there is would be the game's own: the world version (`Version.c_WorldVersion`, a constant 41 that `ZNet`, `World` and the
  chunk files write, and `Version.IsWorldVersionCompatible` checks). A number above it makes the game and every older Better
  Continents refuse the save ("incompatible data version": an empty world with saving off, so nothing is overwritten). It would mean
  rewriting each place that writes the 41 and accepting the new number only while the map is on; that is a patch on the game's save format
  well beyond sectors, and was not tried. The cost of not doing it is the rollback case above.

## 3. Compatibility
* **Expand World Size**, **Expand World Data**: neither patches any of the ten methods (checked in their sources/decompile: none
  names `SectorToIndex`, `IndicesToIndex`, `ResetSectorArray`, `SmallPosition`, the chunk functions or `m_objectsBySector`). EWS sets
  the world's size, which `Wanted` reads (`ExpandWorldSizeGeometry`); with EWS and the world's own size both, the larger counts.
* Mods that read `m_objectsBySector` through `ZoneSystem.SectorToIndex` (ServerDevcommands' reset-dungeon) keep working; one that
  does the game's arithmetic itself (`(zone + 256) * 512 + ...`) would not, and none is known.
* Better Continents' own `ZoneRegen`, `ZoneReset` and `VegetationTwins` look up a zone's objects with `SectorToIndex` and check each
  object's position, and `ZoneRegen.Scan` walks the lists of the whole array (4.2 million with the map, 16 times the game's: 0.1 s of
  clock reads across the frames of a `bc regen`, which `zone-tests` pins to one read a sector): with the map a zone's list holds that
  zone's objects (and, for the zone at sector 0, the server's ghost zones). Only comments changed in these three files.

## 4. What was run
* `tools/sector-tests` (offline, `tools/run-tests.sh sector-tests`): the map for all 2048 x 2048 zones (a sector each but the 64 unmapped,
  distinct, back to the zone, inside its chunk's zones, the game's own chunk number for the game's 512 x 512 zones, moved block, no
  merged group over a border); the transpilers on the client's and the server's IL (13 + 1 + 1 constants and nothing else, refused
  when a constant moves, the same IL in both builds); the ten hooks bound to both assemblies by name, parameter names and types; the
  game's own `GetSaveClonePerChunk` run patched on made-up worlds (4 saves each, changes between): identical chunks and objects to the
  game's for worlds inside its sectors, and for worlds from -65 km to 65 km every object in exactly one chunk that reaches it, every change
  written, files that give the world back; on/off, a failed hook, a live `ZDOMan`, the save's chunks, the migration of a save from
  before, and `Utils.SmallPosition` against the game's text (and `ZDO.Save`/`ZDO.Load` out to 65 km).
* The rig (`~/valheim-testbed/bc-16k/sectors`, the Steam dedicated server, BepInEx, the probe `BCServerProbe` with a ZDO/sector/chunk dump,
  a zone generator, a toucher, a portal maker and a `FindSectorObjects` check):
  * a vanilla-size Better Continents world made by 0.10.2, loaded and changed (every 5th object touched) by 0.10.2 and by this build:
    **all 15 files byte for byte the same** (9 chunk files, `.chunks`, `.fwl2`, `.db2`, `.ok`, the Better Continents file). The same
    with the wide sectors switched on and off again by the probe before the world loads (all ten hooks patched and unpatched under
    HarmonyX on the server's Mono): the same 15 files, byte for byte. The log of a vanilla-size world has no "Sectors:" line.
  * a 24.5 km world made by this build, 446 zones generated around the centre, in each direction past 16.4 km, the block that
    would have been chunk (1, 0), and out to the map's corners: 21,976 objects, 447 sector lists, the block's 121 in chunk (159, 0),
    chunks out to (254..255) and (159..160) (made the same way by 0.10.2: 231 lists, 5,735 objects in sector 0); saved; loaded: the same
    21,976 objects, the same sectors, none twice; 4,396 touched, saved again (27 chunk files written), loaded: all 4,396 back; two saves
    in one session (every 5th touched, `ZNet.Save`, every 7th touched, shutdown) and a load: both sets back (4,396 + 3,140, 628 in both).
  * the same 24.5 km world with its Better Continents settings file removed (Better Continents off for the world): the save's chunk (159, 0)
    switches the wide sectors on in the middle of `ZDOMan.LoadChunks` (after the array was made) and the same 21,976 objects load in the
    same sectors (log: "the save has a chunk (159, 0) beyond the game's 64 x 64").
  * four portals, one in the block that is chunk (1, 0) in the game's numbering, one at 16.6 km, one at -64 km: saved in the portals' chunk
    (1, 0) (4 objects) beside the block's 121 objects in chunk (159, 0), loaded as portals (and connected by the game), the 121 in sectors.
  * `ZDOMan.FindSectorObjects` (what the server sends a peer) at 13 zone centres, against the objects found by position with the game's
    own `ZonesWithinRadius`: exact at all 10 centres inside the map, at the far ones too (zone 260: 948 found, 948 expected, 1 portal; zone
    -1000 and 1000: 1 and 1); the 3 centres outside the map (its corners, and the server's reference position at zone 15625) also get
    the 82 objects of sector 0, as the game's would. In the game's own sectors (a 0.10.2 world made the same way) a peer at zone 260
    is sent 5,742 objects, 965 of them its zone's, and one at zone 1000 gets 5,735 for none.
  * the same world made by 0.10.2 (5,735 objects in chunk (0, 0), 121 in chunk (1, 0)) loaded by this build: migrated as above (log: "5735 objects
    lie beyond the game's sectors", "121 objects ... put in their sectors"), 0 duplicates; loaded again: identical. The 4 `_ZoneCtrl`s
    of that world beyond 20 km had been saved at 20,000 m by 0.10.2 and come back there.
  * a 0.10.3 save opened by 0.10.2 and then by 0.10.3 (section 2, old versions): 22,097 objects (121 twice).
  * server RSS 2,093 MB at the start of the 24.5 km world with the map and 2,085 MB without it (the same world made by 0.10.2): the
    32 MB array is not resident until it is filled; the alt-biome grid of that size is most of the 540 MB over a vanilla-size world's
    1,550 MB. `ZDOMan.LoadChunks` 57 ms for 21,976 objects, `GetSaveClonePerChunk` 23-27 ms (3-4 ms in the game's sectors), a mid-session
    save of that world 0.23 s.

## 5. Not done, and what needs a game
* No client was started (and with Expand World Size installed nothing was run: no build of it was at hand; it and Expand World Data
  patch none of the ten methods). In a game: a client joining at 20 km, 40 km and in the far corner (it must see the objects of its
  zone and of nothing else, and its first frames must not stall: its sector array is made wide when the settings arrive, before PeerInfo);
  a ship sailing across 16.35 km and 65 km (the zone change is an ordinary one now); a portal teleport between far zones; a client of 0.10.2
  joining a 0.10.3 server (the version message); a world saved by a vanilla server after the map.
* The memory and boot time of the alt-biome grid and the locations at 40-65 km, which are what limits a world's size (not this).
* A save a vanilla game or an older Better Continents wrote after a save with the map is not repaired.

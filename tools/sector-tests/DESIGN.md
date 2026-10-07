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
  file as a portal. Chunk (1, 0) is also the sector chunk of zones -248..-241 by -256..-249 (22.1 km from the centre at their nearest corner). In the
  game's own sectors nothing is ever there; in a world past 22.1 km it is where the two collide: the later write of the file wins and the
  other's objects are lost, or are read as portals.
* A chunk with no objects left is never written again, so its file keeps what it held. (The game's own, not changed here.)

### Other limits met on the way
* `Vector2s` (zones) is two `short`s: +-2 million metres. `ZoneSystem`'s zone dictionaries, `ZNetScene`'s active area and
  `ZoneSystem.GetZone` are zone-based and have no 16 km limit. `Utils.FloorToInt` is only right for arguments above -64,000, and is
  only called with zone numbers and minimap pixels.
* **`Utils.SmallPosition`** (assembly_utils, :12676), used by `ZDO.Save`, writes an object with `y == 0` whose `x` and `z` are both
  exactly a `short` (whole numbers within +-32,767 m) as two `short`s, exactly. When only one of them is, and the other is beyond +-20,000 m
  (it is beyond a `short`, or it has a fraction), it **clamps** that one to +-20,000 m. Every zone's `_ZoneCtrl` is at `y == 0` and at a
  multiple of 64 m, so it is clamped only when a coordinate is beyond a `short`: a zone 512 or more out (32,768 m and beyond) is saved at
  20 km; one at 25,600 m by 25,600 m is saved exactly (the tests assert both). The network (`ZDOMan.SendZDOs`) writes the full `Vector3`.
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

All ten are patched or none: a hook that cannot be patched (another mod changed the method) is logged with the hook's name and the others
are taken off again; the world then runs on the game's sectors as it did before, unless its save was made with the map (it is not loaded:
below). Taking them off is all or none too: a patch that cannot be taken off keeps the map on (the ones that came off are patched again, the
array stays wide, the log says so), and the next session tries again. Every replaced method has more than 20 bytes of IL (Mono inlines smaller
ones past a Harmony patch); `ChunkIndexFromXY` (13 bytes) is the one that would be inlined and nothing depends on it.

### When it is on
`DynamicPatch` asks (`WorldSectors.Update`) at the world's load and whenever the settings change: the map is on when `ForcedBySave || Wanted`.
* `Wanted(settings, Expand World Size's size, mode, hosted)`: Better Continents is on for the world, `max(World Size + Edge Size, Expand World
  Size's size)` is **past 16,350 m** (the game reaches 16,352 m east and 16,416 m west), and the world is a **wide world**: its settings carry
  the marker (`BetterContinentsSettings.WideSectors`, below), or this machine has **Wide Sectors On** and runs the world (`Hosted`: single
  player, the host or a dedicated server, from `ZNet.SetServer`'s `server` flag, never a client).
* `ForcedBySave`: the loaded save's chunk list has a chunk past the game's 64 x 64 (`ChunkSaveMapping.Load`'s postfix). Only this map writes
  one, so the save is read with it whatever the settings or the machine's Wide Sectors say (until the next session; with Off the log says why,
  once), and a world that needs the map and cannot have it is not loaded (below).
* On a server and in single player `ZNet.SetServer`'s prefix runs before `ZNet.Awake` makes the `ZDOMan`; on a client the settings arrive
  before PeerInfo, so before any object, and the live `ZDOMan`'s empty array is replaced. A change is only made while the `ZDOMan` has no
  object (the main menu, those two moments); otherwise the sectors stay as they are until the next session and the log says so once.
* A vanilla-size world is not touched: nothing is patched, nothing is logged, and a world made and saved by 0.10.2 and loaded and changed by
  this build writes the same files byte for byte (measured, section 4).

### The Wide Sectors setting and the world's marker
`Wide Sectors` (`[07 BetterContinents.Misc]`, the last of its list; `WideSectorsMode`: Auto, On, Off; Auto by default) is a machine's own choice, a
`Live` setting like Settings Transfer Rate: the machine that runs a world reads it, when a world is made and when it loads. It is no part of a
world, an `export.cfg` or an import (a line for it in an `export.cfg` is ignored, as any that is not a world setting), and a client's own value
has no effect. What a world says is its **marker**: `BetterContinentsSettings.WideSectors`, saved as the key `DataKey.WideSectors` = 68 (a key
with no value) in the disk and the network forms, by a world that uses wide sectors and by no other. A world of 16,350 m or less never has one:
its settings and its saves are as they were, byte for byte (the golden settings recordings have no difference).

| | a new world past 16,350 m | an existing world past 16,350 m, the game's sectors | a wide world (marker, or wide chunks in its save) |
|---|---|---|---|
| Auto | made wide, marked | keeps them (the log says what On does, once a session) | stays wide |
| On | as Auto | converted when it loads: the map is on before the world loads, the marker is set, the save is converted (below) | stays wide |
| Off | the game's sectors, never marked (the log says so) | keeps them | stays wide (the log says why, once a session) |

* A new world takes its settings in `BetterContinentsSettings.FromConfig`: From Config, the snapshot of the config an import takes (that
  snapshot's Wide Sectors counts, not the live config's), a Directory's `export.cfg` (over the config; its own Wide Sectors line does not
  count). A world made from a preset file has the marker the preset has (an import's preset is made the same way), and
  `WorldPatch.SaveWorldFWLDataPostfix` gives a preset that has none this machine's choice when the world is made. `WorldSectors.NewWorld` is the
  rule, and Expand World Size's size counts for it as it does for a world that loads.
* `Update` converts: with On, a world this machine runs that reaches past 16,350 m gets the map and, at once, the marker; Better Continents
  saves its settings with every world save, after the game copied the old save. A client, Auto and Off never set one.
* A client takes what the server sent: `ReceivedSettings` and `LoadFromCache` set `Settings`, call `DynamicPatch`, and only then tell the
  server the client is ready for PeerInfo (the server holds PeerInfo until then), so `Wanted(..., hosted: false)` sees the server's marker, whatever
  the client's own setting says, and the live `ZDOMan`'s empty array is replaced before any object arrives. A wide world without the marker (made
  wide by a build before the setting: the map is on by its save) is wide on its host and not on its clients; the host with On marks it once.
* Older versions: 0.8.1 to 0.10.2 read settings of any version above 10 the same way, with no upper limit, and **stop at a key they do not
  know** ("Unknown feature"): a world that has the marker opens in them with Better Continents off for it, as a vanilla world (its terrain is not
  its own, which shows at once), and they do not save over its settings. A client of an older version is refused by the server's version check
  (`SendSettings`) before any settings are sent.

### A save made with the game's sectors
A world saved with the game's sectors has every object beyond them in chunk (0, 0), and the objects of the zones that are chunk (1, 0) in the
game's order in the portals' file. Loaded with the map on, `ZDOMan.LoadChunks` files every object by position, so each lands in its own
sector; the save does not know it: chunk (0, 0) stays in the chunk list with the file it has, which is written again only when it changes, so
the objects, written in chunks of their own, would be in the save **twice** and load as twice the objects (a duplicated chest is a duplicated
chest). `ZDOMan.LoadChunks`'s postfix (`LoadedChunks`) converts such a save when the map is on and the save has no chunk of the wide area (one
that has was made by the map: it is only looked at, below):
1. **The objects that move are marked changed** (`ZDOMan.SetDirtySector`, what the game does for an object that changes): the ones in the
   map's zones past the game's (not the ones past the map, nor the server's ghost zones at zone 15625, which are in sector 0 either way) and
   the objects of the portals' file that are not portals. Their chunks are then written by every game version: 1.0.17's `AddObjectsPerChunk`
   also writes a chunk that is missing from the list, 1.0.16's (`libs-Tools/OLD`) only a changed one. The tests run both.
2. **Chunk (0, 0)**: when anything lies in its 8 x 8 zones or in sector 0, it stays in the list and is marked changed: the game writes it again
   under its next version, which is a new file name, and removes the old file once the new chunk list is written. When nothing does, it leaves
   the list and nothing is written in its place; the old file is an orphan the next load removes. (Taken out of the list whatever lies in it,
   it is written as version 1, which is the name of the file the saved list names when the world was saved once: the game truncates that file
   before the new chunk list exists, and a crash in between leaves the old list pointing at a chunk without its far objects.)
3. The objects of the portals' file that are not portals are put in their sectors (chunk (159, 0) here), and the portals' chunk is written
   again without them.
4. When anything moved, **the game is asked to copy the old save** before its first save (`World.m_createBackupBeforeSaving`:
   `<world>_backup_<date and time>`, which holds the settings too: Better Continents' patch of the game's copy), and the log warns, naming the
   copy, that an older Better Continents (or the game alone) must not save the converted world.

A save that has chunks of the wide area is looked at for what an older version wrote over it, and nothing is repaired: the portals' chunk
holding objects that are not portals, and chunk (0, 0) holding more objects than lie in its zones, each with the numbers in the log.

### A save made with the map, whose patches cannot be installed
If a save has chunks of the wide area and the ten patches cannot be installed (another mod changed one of the three methods, or a game update
did), loading it on the game's sectors would put every far object into sector 0 and save it again in chunk (0, 0), on top of the chunks it is
in. `MappingLoaded` stops it the game's own way: `ZNet.m_loadError`, which `ZNet.Save` honours (it skips the save: "Skipping world save") and which
logs a game that runs the world out (`Game.FixedUpdate`: "World load failed, exiting without save"); the menu's error is Better Continents' text
(`LastConnectionError`), and the log has an error that says the world was saved with wide sectors, after the one that names the patch that failed.
The game itself leaves a dedicated server running with its saving disabled, which would let players play a world that is lost at the next start,
so the server stops (`Application.Quit(1)`, as it does for a failed alt-biome planting). A world that is wide only by its settings (no chunk of the
wide area saved yet) runs on the game's sectors instead, with the error in the log: nothing in its save would be put twice.

### Utils.SmallPosition
With the map on, a position is written whole instead of clamped. A `_ZoneCtrl` of a zone 512 or more out (32,768 m and beyond) is then saved
and loaded where it is (the game saved it at 20 km: the 4 `_ZoneCtrl`s of the 0.10.2 test world past 20 km, which the test zones put near
65 km, came back at 20,000 m). Up to 32,767 m the game's function saves a `_ZoneCtrl` exactly, and so does the patch: it is not needed there,
and harmless.

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
* The marker is a key of the settings (above): an older Better Continents opens a world that has it with Better Continents off for it, as a
  vanilla world, which shows at once, and does not save over its settings; a client of an older version is refused by the server. The game's own
  world version and the settings version (12) are not touched.
* An older Better Continents (or the game alone) **does open** a save made with the map when it can read the world's settings as its own (a
  world made wide by a build before the marker, or a world whose settings were taken away) or has none, and it loads it fine: its sectors are
  the game's, so every far object is in sector 0 as before. **Its first save writes duplicates**: measured with 0.10.2 on a save made with the
  map, chunk (1, 0) was written with the 121 objects of chunk (159, 0) (the game writes a chunk that is missing from the list), and the world then
  loaded 22,097 objects for 21,976 saved. Chunk (0, 0) is written with every far object (5,742 in the test world) the next time anything in
  sector 0 changes: that is inferred from the game's planning, not measured (in that run chunk (0, 0) stayed 1,484 bytes). The objects are then
  loaded twice by anything. The log says so when it finds objects in the portals' chunk of a save made with the map, or more objects in chunk
  (0, 0) than lie in its zones, and nothing is repaired. **Do not save a world with the map in an older version.**
* The one hard stop there is would be the game's own: the world version (`Version.c_WorldVersion`, a constant 41 that `ZNet`, `World` and the
  chunk files write, and `Version.IsWorldVersionCompatible` checks). A number above it makes the game and every older Better Continents
  refuse the save ("incompatible data version": an empty world with saving off, so nothing is overwritten). It would mean rewriting each place
  that writes the 41 (five of them), accepting the new number only while the map is on, and would make the game copy the save before the first
  save of every session (`World.m_createBackupBeforeSaving` when the version differs); that is a patch on the game's save format well beyond
  sectors, and was not tried. The cost of not doing it is the rollback case above, which the marker, the log and the game's copy before a
  conversion make hard to meet by accident.

## 3. Compatibility
* **Expand World Size**, **Expand World Data**: neither patches any of the ten methods (checked in their sources/decompile: none
  names `SectorToIndex`, `IndicesToIndex`, `ResetSectorArray`, `SmallPosition`, the chunk functions or `m_objectsBySector`). EWS sets
  the world's size, which `Wanted` and `NewWorld` read (`ExpandWorldSizeGeometry`); with EWS and the world's own size both, the larger counts.
  A world made wide carries its marker, and a client takes the size from the settings it was sent (plus its own EWS, when installed): a client
  whose own EWS and the host's differ can disagree on whether a world reaches past 16,350 m, and then runs on the game's sectors (the shared
  pile, as before this work, no corruption).
* Mods that read `m_objectsBySector` through `ZoneSystem.SectorToIndex` (ServerDevcommands' reset-dungeon) keep working; one that
  does the game's arithmetic itself (`(zone + 256) * 512 + ...`) would not, and none is known.
* Better Continents' own `ZoneRegen`, `ZoneReset` and `VegetationTwins` look up a zone's objects with `SectorToIndex` and check each
  object's position, and `ZoneRegen.Scan` walks the lists of the whole array (4.2 million with the map, 16 times the game's: 0.1 s of
  clock reads across the frames of a `bc regen`, which `zone-tests` pins to one read a sector): with the map a zone's list holds that
  zone's objects (and, for the zone at sector 0, the server's ghost zones). Only comments changed in these three files.

## 4. What was run

### Offline: `tools/sector-tests` (`tools/run-tests.sh sector-tests`)
* `Map.cs`, `Il.cs`, `Plan.cs`: the map for all 2048 x 2048 zones (a sector each but the 64 unmapped, distinct, back to the zone, inside its
  chunk's zones, the game's own chunk number for the game's 512 x 512 zones, moved block, no merged group over a border); the transpilers on
  the client's and the server's IL (13 + 1 + 1 constants and nothing else, refused when a constant, or the chunk size of one of the 7 calls, moves
  by one, the same IL in both builds); the ten hooks bound to both assemblies by name, parameter names and types; the game's own
  `GetSaveClonePerChunk` run patched on made-up worlds (4 saves each, changes between): identical chunks and objects to the game's for worlds
  inside its sectors, and for worlds from -65 km to 65 km every object in exactly one chunk that reaches it, every change written, files that give
  the world back, on a fake world folder that keeps the chunk files by version, as the game names them (nothing is written over a file the saved
  chunk list names).
* `Session.cs`: switching on and off with Harmony, a live `ZDOMan`, the array's width, the game's `IndexToSector` and `SectorToIndex` answering the
  wide map, the 65 km warning, the save's chunks that force the map, one failed hook, zone regeneration on the wide sectors, and `Utils.SmallPosition`
  against the game's text (and `ZDO.Save`/`ZDO.Load` out to 65 km).
* `Modes.cs`: the Wide Sectors setting. 108 cases of mode x marker x who runs the world x size (at, below and above 16,350 m) x wide chunks in the
  save x Better Continents on or off, each against the rule; an existing world with the game's sectors in each mode; a new world in each mode and
  size (From Config, the snapshot an import takes, a Directory with an `export.cfg`); the marker saved and read in the disk and network forms (the
  same bytes plus the key 68; a world without it read as one that is not wide); a client following the server's marker in every mode.
* `Migration.cs`: a save made with the game's sectors converted, in 7 shapes (far objects, chunk (0, 0) alone or with objects in its zones or the
  server's ghost zones, saved once and three times, the zones of the portals' chunk, all of it) x the game's planning and an older game's (1.0.16's
  `AddObjectsPerChunk`, which writes a chunk only when it is marked changed): every object that moves is marked changed, chunk (0, 0) is kept (marked)
  or dropped as the rule says and written as version V0 + 1 or not at all, every object in the save once, no file the saved list names is written
  over (the control of the first conversion, which took chunk (0, 0) out of the list, writes version 1 over it), the game is asked for its copy and
  the log warns, a load of the converted save (its own chunks turn the map on, the orphans are removed, nothing is converted again, nothing is
  left to write); what a load leaves alone (no map, a save that has wide chunks and real portals, nothing to move, a world of the game's size),
  and which objects count as beyond; a wide save an older version wrote over (the portals' chunk, chunk (0, 0)): warned about, with the numbers;
  a patch that cannot be taken off; a wide save whose patches cannot be installed (not loaded: the game's load error, the menu's error, the log);
  the five calls that reach `WorldSectors` (by IL) and the two patches on the game's load (bound, and run).
* Checked by mutation (scratch copy, `~/valheim-testbed/bc-16k/sectorsfix-rig/mut/`): 54 mutants of what this stream added or changed (the conversion: `LoadedChunks`
  and its helpers, 18; the setting and the marker, 16; fail closed and switching off, 6; the calls that reach `WorldSectors` and its two patches, 8; and the
  six that the review's tests lens found alive: its B5, B6, C4, D12, D13, D15, while its E3, E4, E6 and W1-W6 are L2, L3, L1 and G1-G7 here) were made one at a
  time on a scratch copy of the committed tree, built and run through the suite: **all 54 fail it** (`mut/results.txt`, with each mutant's edit in `mut/mutants.py`;
  one of them first did not compile and was rewritten). What the suite cannot see: that a dedicated server stops after a refused load
  (`StopAfterRefusal` needs Unity's coroutines: the rig saw it), the New World screen's own path to a preset file (`WorldPatch`: its call is checked), and
  the refusal to take the patches off on the game's own Mono.

### The first stream's rig (`~/valheim-testbed/bc-16k/sectors`, the Steam dedicated server, BepInEx, the probe `BCServerProbe`), as reviewed
* a vanilla-size Better Continents world made by 0.10.2, loaded and changed (every 5th object touched) by 0.10.2 and by this build:
  **all 15 files byte for byte the same** (9 chunk files, `.chunks`, `.fwl2`, `.db2`, `.ok`, `BetterContinents` and `BetterContinents.old`). The
  same with the wide sectors switched on and off again by the probe before the world loads (all ten hooks patched and unpatched under HarmonyX on
  the server's Mono): the same 15 files. The log of a vanilla-size world has no "Sectors:" line.
* a 24.5 km world made by that build, 446 zones generated around the centre, in each direction past 16.4 km, the block that would have been chunk
  (1, 0), and out to the map's corners: 21,976 objects, 447 sector lists, the block's 121 in chunk (159, 0) (made the same way by 0.10.2: 231 lists,
  5,735 objects in sector 0); saved; loaded: the same objects, the same sectors, none twice; 4,396 touched, saved again (27 chunk files written),
  loaded: all 4,396 back; two saves in one session (every 5th touched, `ZNet.Save`, every 7th touched, shutdown) and a load: both sets back (4,396 +
  3,140, 628 in both). (The vanilla-size world and the 24.5 km world's two loads were run with the build that was committed; the two saves in one
  session and the settings-deleted run with the build before the last one, which differs from it in no method of `WorldSectors` but a GUID.)
* the same world with its Better Continents settings file removed: the save's chunk (159, 0) switches the wide sectors on in the middle of
  `ZDOMan.LoadChunks` (after the array was made) and the same objects load in the same sectors.
* four portals, one in the block that is chunk (1, 0) in the game's numbering, one at 16.6 km, one at -64 km: saved in the portals' chunk (4
  objects) beside the block's 121 objects in chunk (159, 0), loaded as portals (and connected by the game), the 121 in sectors.
* `ZDOMan.FindSectorObjects` (what the server sends a peer) at 13 zone centres, against the objects found by position with the game's own
  `ZonesWithinRadius`: exact at all 10 centres that lie in the map, the far ones too (zone 260: 948 found, 948 expected, 1 portal); the other 3
  centres are the map's corners (which lie in it, but their search area reaches past it) and the server's reference position at zone 15625, and
  they also get the 82 objects of sector 0, as the game's would. In the game's own sectors (a 0.10.2 world made the same way) a peer at zone 260 is
  sent 5,742 objects, 965 of them its zone's, and one at zone 1000 gets 5,735 for none.
* the same world made by 0.10.2 (5,735 objects in chunk (0, 0), 121 in chunk (1, 0)) loaded by the first conversion (it took chunk (0, 0) out of
  the list): 0 duplicates, loaded again: identical. The 4 `_ZoneCtrl`s of that world beyond 20 km had been saved at 20,000 m by 0.10.2 (they lie near
  65 km) and come back there.
* a save made with the map opened and saved by 0.10.2, then loaded by 0.10.3: 22,097 objects for 21,976 (121 twice). That run's build had no
  warning for it; the one seen in a log came from a debug build whose probe made objects of prefab 0 in the portals' chunk, and it is tested offline.
* a world of 24.5 km takes 68 s to load and 85 s to create and generate on the test machine (a vanilla-size world: 10 s to load, 28 s to create), a
  server's RSS is 2,093 MB at its start with the map and 2,085 MB without it (the same world made by 0.10.2): the 32 MB array is not resident until
  it is filled; the alt-biome grid of that size is most of the 540 MB over a vanilla-size world's 1,550 MB. `ZDOMan.LoadChunks` 57 ms for 21,976
  objects, `GetSaveClonePerChunk` 23-27 ms (3-4 ms in the game's sectors), a mid-session save of that world 0.23 s.

### This stream's rig (`~/valheim-testbed/bc-16k/sectorsfix-rig`, 2026-10-07, the fix build)
The Steam dedicated server (read only), BepInEx and the probe `BCServerProbe` (this stream added `BCPROBE_BREAK`, and the world's marker in the probe's
settings line), port 2504, each run through `heavy.sh` (8G), with the DLL the committed tree's last full test run built (`dist/plugins/BetterContinents.dll`, md5
`39c2c49e5a96...`). The scripts are `s1.sh` to `s4.sh`, `sfinal.sh` and `analyze3.sh` there; every folder a run left is in `after/<label>`.
1. A vanilla-size world made by 0.10.2 (V10): loaded, every 5th object touched, saved, by 0.10.2 and by this build: **the 15 files are byte for byte the
   same** (`diff -rq`), and the log has no "Sectors:" line.
2. A new 24.5 km world with Wide Sectors **Auto**, 446 zones generated: "this new world reaches 24500 m, so it is made with wide sectors (Wide Sectors is
   Auto)", then the map goes on before the world is made; 21,961 objects in 447 sector lists, 34 chunk files (19 past the game's 64 x 64, chunk (159, 0)
   among them). Its settings file is the 32 bytes of a world without the marker plus the key 68 (36 bytes: version 12, GlobalScale, ForestScale, World
   Size, 68). Loaded with Wide Sectors **Off**, which only the saved marker can overrule ("Wide Sectors is Off, but this world stays wide: its settings say
   it is a wide world"), 4,393 objects touched, saved; loaded with Auto: every object back, once (same multiset of prefab, position, zone, sector, sector
   list and touched value, 0 uids twice), the settings file unchanged.
3. A 24.5 km world made by 0.10.2 (L24gen: 21,947 objects, 5,735 of them in chunk (0, 0), 121 in the portals' chunk (1, 0)):
   * **Auto**: the log says it keeps the game's sectors (and what On does, once); loaded, every 5th object touched, saved, the folder is **byte for byte
     what 0.10.2 leaves** after the same run (22 files); no sector is changed.
   * **On**: converted when it loads: "5653 objects lie beyond the game's sectors ... and 121 are of the zones the game's save files as its portals' chunk
     (1, 0) (chunk (159, 0) here)" (the 82 objects of the server's ghost zones stay in sector 0), then the warning that names the copy. 21,947 objects
     before and after, the same multiset of (prefab, position, persistent, zone) as 0.10.2's load of the world. The game made its copy,
     `L24_backup_<date and time>` (21 files, the old save byte for byte, settings file included, beside its own auto backup). Chunk (0, 0) was written again
     as version 2 (1,637 bytes, from 188,519; the version 1 file is gone), the portals' chunk as version 2 (6 bytes: no portal), chunk (159, 0) as version 1
     (3,642 bytes); the settings file has the key 68. Loaded again with **Off** (the marker keeps it wide) and with Auto: every object back, touched
     values included, 35 chunk files.
   * **Off**: a new 24.5 km world (446 zones, 21,929 objects): "has the game's own sectors", 16 chunk files, none past the game's 64 x 64, 5,709 objects in
     sector 0; its settings file is the 32 bytes, no key 68.
4. **Fail closed.** With `BCPROBE_BREAK=1` (a transpiler that makes DecideChunkSize's 64 chunks a side 65, as another mod's might), a wide save made by an
   earlier build (W24gen: no marker) and the world of run 2 (marker): Better Continents logs the patch that failed, then an error that the world was saved
   with wide sectors and is not loaded; the game logs "World db couldn't load correctly, saving has been disabled"; the server stops a second later
   ("Sectors: stopping the dedicated server because the world was not loaded", "Game - OnApplicationQuit", "Skipping world save"); **the world folder is
   byte for byte the pristine one**.
5. 0.10.2 opens the marked world of run 2: "Failed to load the save file. Unknown feature: 68", Better Continents off for the world (the probe says
   enabled False): it opens as a vanilla world.
6. A save made with the map that 0.10.2 opened and saved (W24oldopened), loaded by this build: wide by its chunks, 22,097 objects (121 twice), and the log
   warns: "the portals' chunk (1, 0) of this save holds 121 objects that are not portals ... they are in the world twice."
7. A wide save whose Better Continents settings are gone, Wide Sectors Off: wide by its chunks, "Wide Sectors is Off, but this world stays wide: its save has
   chunks only the wide sectors write".

## 5. Not done, and what needs a game
* No client was started (and with Expand World Size installed nothing was run: no build of it was at hand; it and Expand World Data
  patch none of the ten methods). In a game: a client joining at 20 km, 40 km and in the far corner (it must see the objects of its
  zone and of nothing else, and its first frames must not stall: its sector array is made wide when the settings arrive, before PeerInfo);
  a ship sailing across 16.35 km and 65 km (the zone change is an ordinary one now); a portal teleport between far zones; a client of an
  older version joining a server whose world is wide (the version message); a world saved by a vanilla server after the map; a game that loads
  a wide world, goes back to the menu and loads a vanilla-size one in the same process (the session's state is reset by `SessionStarts`, tested
  offline only); the New World screen's own preset file path (`WorldPatch`, tested by its call and by `NewWorld`).
* The conversion's copy of the old save (`<world>_backup_<date and time>`) was seen on the dedicated server (below); on Steam Cloud, and the
  game's Manage Saves restore of it, nothing was run.
* The memory and boot time of the alt-biome grid and the locations at 40-65 km, which are what limits a world's size (not this).
* A save a vanilla game or an older Better Continents wrote after a save with the map is not repaired.

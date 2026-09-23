# Alt biomes in Better Continents 0.8.1

Valheim 1.0 gives parts of each biome an *alt biome*: Dark Meadows, Troll Black Forest, Fortress
Mountain and 29 more, each changing creatures, vegetation, locations or weather. The game picks
them at random. Better Continents 0.8.1 lets a map author **plant** them with colours, the way the
biome map plants biomes, gives world owners control over the game's random placement, and fixes
three bugs in the pipeline that decides them.

Part 1 describes the design. Part 2 is the reference: every file, legend rule, colour, config key,
command, save key and log line, with exact names and defaults. Part 3 covers the three fixes (and
two smaller ones in the same pipeline), and Part 4 the offline test harness.

Contents

1. [Design](#1-design)
2. [Reference](#2-reference)
3. [The three fixes](#3-the-three-fixes)
4. [Testing](#4-testing)

---

## 1. Design

### 1.1 How Valheim 1.0.15 decides alt biomes

Everything happens once per world load, in `AltBiomeWorldData.VerifyBiomeData`, on the server and
on every client separately (nothing about it is sent over the network):

1. **Point grid.** `GenerateBiomePoints` samples the biome and height on a 2048 x 2048 grid of
   12 m cells centred on the world origin. Points further than 10500 m from the centre are
   marked as ocean with height -1000.
2. **Regions.** `GenerateSectors` flood-fills 4-connected points of one biome into regions
   (`BiomeSector`). Ashlands, Deep North and Ocean are special: each is **one** world-wide region,
   however many separate patches it has. Per region the game records its border length
   (`EdgeCount`, in grid cells), centre, distance from the world centre and average height, which
   is `(lowest + highest) / 2` over the border points only.
3. **Random placement.** `GenerateAltBiomes` goes through each base biome and each alt biome that
   belongs to it, shuffles that biome's regions with a seed made from the world seed, and gives
   the alt biome to regions in that order while its count is below its maximum. Below the
   minimum it ignores the chance; above it, a region needs to pass the chance roll. Every region
   must also pass `BiomeSector.CanAddModifier`: distance from the centre, border length window,
   average height window, world position bounds, incompatibilities with alt biomes already on
   the region, and neighbour rules.

Everything downstream reads the result through `WorldGenerator.GetBiomeSector`: the biome of each
terrain chunk's corners, vegetation, locations, spawns and their level-up chance, weather and
music. In Valheim 1.0 even the base biome a terrain chunk gets comes from this region grid.

### 1.2 Planting with colours

A second, optional image, the **alt-biome map** (`altbiomemap.png`), is laid over the world exactly
like the biome map. Its **legend** (`altbiomemap.txt`, beside the image) says which alt biomes each
colour plants. Black and transparent pixels are unplanted: the game's own placement decides there.
A line `Name: at x, z` plants the whole region under a world position instead of a painted area.

The map is baked into the world when the world is created, like every other Better Continents
map. What is baked is the *resolved* form, not the PNG: one class index per pixel (run-length
encoded), the class table with its alt-biome names, and the point plants. Colours are therefore
matched once, on the author's machine, and every server and client decodes the same bytes. Names
are resolved when the world loads, because the game's alt-biome list does not exist on the main
menu where worlds are created.

At world load, each grid point samples the map. A colour only **claims** the base biomes its alt
biomes can change: Dark Meadows painted across a meadow and its beach takes over the meadow and
leaves the beach alone. `none` claims every biome, and a forced name (`!Name`) claims every biome
too.

### 1.3 Planted areas become regions of their own, never merged

When at least one painted point claims land, Better Continents replaces `GenerateSectors` with:

1. a line-for-line copy of the game's flood fill, so every region starts exactly as the game makes
   it;
2. **splitting**: each 4-connected patch of one colour inside one of those regions becomes a new
   region of its own. Nothing is ever merged: two patches of one colour in the same region are two
   regions, and a patch never joins a neighbouring region. On Ashlands, Deep North and Ocean all
   patches of one colour form **one** region per colour, because the game treats those biomes as a
   single region. A game region that ends up completely planted is reused for its first patch, so
   no empty region is left behind;
3. a line-for-line copy of the game's statistics pass over the final regions.

What is left of a game region after its patches are taken out stays that one region, with its
statistics recomputed over what is left. With no painted point claiming land, the game's own
`GenerateSectors` runs untouched.

Split-out regions are hidden from the game's region lists while it places alt biomes at random,
and the lists are restored afterwards, so the game shuffles exactly the lists it would have
shuffled without the planting. Alt biomes you did not plant therefore land where they would have
without the planting, except in regions the planting touched and where the quota rule below
applies. (The harness checks this on a real placement run: four unplanted alt biomes land on
exactly the same regions with and without a map.)

Point plants (`at x, z`) split nothing. They are resolved after splitting and apply to the whole
region under the position: a planted region, the remainder of a game region, or, on Ashlands,
Deep North and Ocean, the whole unplanted world-wide region.

### 1.4 Planting first: the quota rule

Planted alt biomes are applied **before** the game's random placement, with the game's own
`BiomeSector.AddModifier`. The game counts an alt biome's regions (`AltBiome.Sectors.Count`) when
it decides whether to place more, so planted regions count toward its per-alt-biome **minimum**
and **maximum**:

* Fortress Mountain allows 1 to 2 per world. Plant one, and the game can still place one more at
  random. Plant two, and it places none.
* Lox Plains allows 0 to 1. Plant one, and the game places none.
* Dark Meadows allows 2 to 5. Plant one, and the game still places one more without rolling the
  chance (while the count is below 2) if a region qualifies, and up to four more in all.

Planting itself is not capped: plant three Fortress Mountain regions and all three get it; the game
then places none. The table in 2.4 gives the game's numbers for every alt biome, and 2.9 works
through them.

### 1.5 What planting ignores and what it keeps

The author chooses the place, so planting ignores every *occurrence* rule of the game: region size,
distance from the centre, average height, world position bounds, neighbour rules and chance. It
keeps the three *content* rules by default, because breaking them is almost always a mistake:

* an alt biome the game has disabled is not planted (warning);
* an alt biome is only planted on the base biomes it belongs to (a colour does not even claim
  other base biomes, see 1.2);
* an alt biome incompatible with one already on the region is not planted (warning), first come
  first served in legend order.

`!Name` in the legend overrides all three for that name.

### 1.6 Modes

| Mode | Planted regions | Game's random placement |
|:--|:--|:--|
| `Random` (default) | planted | on unplanted land, under the world's options |
| `PlantedOnly` | planted | skipped |
| `Off` | nothing, the map is not even sampled | skipped: no alt biome anywhere |

A `none` region is protected in every mode: nothing is planted on it and random placement stays off
it.

### 1.7 Per-world options

The options in the config section `[08 BetterContinents.AltBiomes]` (section 2.5) are defaults for
**new** worlds. When a world is created they are baked into its settings, like the maps, and never
read from the config again for that world. The world owner changes them in game with the
`bc ab` commands (section 2.6), which save with the world.

A world whose settings hold no alt-biome options (every world made before 0.8.1) behaves as 0.8.0
did: mode `Random`, grid `Vanilla`, the world seed, no multipliers, no overrides.

Options that reproduce exactly that behaviour are not written to the world at all (section 2.8).

### 1.8 Multiplayer

The alt-biome options and the baked alt-biome map travel to clients inside Better Continents'
world settings, like every other map. Each client builds its own grid and regions from them. The
server then sends its **placement** (which region has which alt biomes, each region identified by
its first grid point, biome and area), and the client applies it over its own, so every region
both machines agree on carries the server's alt biomes, random ones included.

Both sides compute two FNV-1a hashes, one of the region grid and one of the placement. The client
logs whether they match the server's and reports its hashes back; the server logs the result per
client. A difference is a warning, never a kick: on a client whose grid differs, regions that still
match get the server's alt biomes and the rest get none.

### 1.9 A planting error stops the world load

Zones generated with the wrong alt biomes are permanent, so a planting error stops the world load
with a clear message instead of carrying on without the planting:

* an alt-biome map in the world's settings that cannot be read;
* any failure while sampling the map, splitting the regions or applying the planted alt biomes.

A dedicated server logs the error and quits (exit code 1) without saving the world. A host logs out
without saving. A client disconnects. In mode `Off` nothing is planted, so such a world still
loads. Content problems are not errors: an unknown name, a disabled or incompatible alt biome, or a
colour that matches no legend line is logged as a warning and simply not planted.

The world carries its own baked copy of the map, so editing the image or the legend cannot repair a
world whose stored map is damaged: restore the world's Better Continents settings from a backup (the
previous save's copy ends in `.old`). Any other failure is a bug to report with the log.

During a live edit in debug mode (section 2.6) a planting error keeps the previous regions and
placement instead.

### 1.10 Compatibility

* **Vanilla worlds** (no Better Continents settings): exactly as before. The only patch that acts
  on them is the `GetBiomeSector` clamp, and only on a grid Expand World Size has resized.
* **Existing Better Continents worlds**: the same regions and the same random alt biomes as in
  0.8.0, and their settings are saved byte for byte as 0.8.0 saves them (checked on two real 0.8.0
  worlds, 4.8 MB and 10 MB of settings). What does change for them are the fixes: Deep North
  weather on worlds with a biome map (3.3), live edits in debug mode (3.2), land beyond 10500 m on
  worlds with the edge drop-off disabled and locations that accept several biomes (3.4).
* **Worlds that use an alt-biome feature** (a planted map, or any option that differs from 0.8.0
  behaviour, see 2.8) need 0.8.1 or later. Better Continents 0.8.0 stops reading the world's
  settings at the first key it does not know and loads the world *without* Better Continents: it
  leaves the settings file alone, but zones generated in that session get vanilla terrain.

---

## 2. Reference

### 2.1 Files

| File | Where | Notes |
|:--|:--|:--|
| `altbiomemap.png` | the path in `Altbiomemap File` (section `[08 BetterContinents.AltBiomes]`); when `Directory` (section `[00 BetterContinents.Debug]`) is set, only a file of this name in that folder is used, as for every map | Square, any size. Covers the same square as every Better Continents map: 21000 m x 21000 m centred on the origin on a standard world, top of the image north (+z). Use PNG: a lossy format shifts colours. |
| `altbiomemap.txt` | beside the image, named after it (`<image name>.txt`) | Written with the default palette when missing (UTF-8, `\n` line endings, identical to `palettes/altbiomemap.txt`). |
| `palettes/BetterContinents.gpl` | repository | GIMP palette "Better Continents": the 10 base-biome colours, then the 32 alt-biome colours grouped by base biome (`Biome: Meadows`, `Alt: Troll Black Forest`). |
| `palettes/altbiomemap.txt` | repository | The default legend, byte for byte as Better Continents writes it. |
| `palettes/palette.json` | repository | The same 42 colours: `name`, `kind` (`biome` or `alt`), `base_biome`, `group` (alt biomes), `hex`, `r`, `g`, `b`. |
| `altbiomes-<yyyy-MM-dd-HH-mm-ss>.png` and `.txt` | `<save data>/BetterContinents/<world name>/` | Written by `bc ab export`. |

The three `palettes/` files are generated from the built DLL by the harness (Part 4); do not edit
them by hand.

A pixel at column `px`, row `py` of an `N x N` image covers world position
`x = (px / (N - 1) - 0.5) * 21000`, `z = (0.5 - py / (N - 1)) * 21000` (standard world; Expand
World Size changes the 21000). The grid is sampled at the nearest pixel.

### 2.2 Legend syntax

One instruction per line:

```
<alt biome>[ + <alt biome> ...]: <colour>       plant these alt biomes where the image has this colour
<alt biome>[ + <alt biome> ...]: at <x>, <z>    plant them on the whole region at world position x, z
none: <colour>                                  a protected region: no alt biome, planted or random
```

* **Names** are the game's internal alt-biome names (`AltBiome.m_name`, the names in the palette
  table), matched case-insensitively. A name that is not one of the 32 vanilla names is noted when
  the map is baked ("fine if another mod adds it"); a name the game does not have when the world
  loads is warned about and plants nothing.
* `*` is a wildcard: `*Mistlands` plants **every** alt biome whose name matches and that fits the
  land, subject to the content rules. `*Plains` plants Lox Plains only: Goblin Plains and Death
  Plains are incompatible with it and are refused.
* `+` stacks alt biomes on one colour. Listing the same colour on several lines does the same.
* `!Name` forces that name past the content rules (disabled, wrong base biome, incompatible), and
  makes the colour claim every base biome.
* `none` cannot be combined with names (the `none` is ignored, with a warning).
* **Colours**: `RRGGBB`, `#RRGGBB`, `RRGGBBAA`, `#RRGGBBAA`, `r,g,b` or `r,g,b,a` (decimal). An
  alpha component is accepted and ignored. Black (`000000`) is reserved for unplanted land and is
  refused.
* **Points**: `at x, z` or `@ x, z`, world coordinates in metres (x east, z north), invariant
  culture (`.` decimal point).
* **Comments**: `#` starts a comment at the start of a line, or where it has whitespace on both
  sides (or ends the line), so `Dark Meadows: #2D4613` is still a colour.
* A colour within 40 RGB units of another legend colour is warned about: pixels between them could
  go either way.
* At most 254 entries: distinct colours plus point lines.

Examples:

```
Dark Meadows: 2D4613                   plant Dark Meadows where the image is #2D4613
Dark Meadows + Lantern: 0080C0         two alt biomes on one colour
Lantern: 0080C0                        ...the same colour on another line stacks the same way
*Mistlands: 7F3FBF                     every Mistlands alt biome that fits
!Dark Meadows: 7A8B9C                  force it, even on a Black Forest region
none: C030F0                           keep this area free of alt biomes
Troll Black Forest: at 2750, -1210     the whole region under (2750, -1210)
Bat Swamp: @ 3728, 7088                short form
```

### 2.3 How pixels are decoded

Once, when the map is baked (world creation, `bc ab fn`, `bc reload ab`):

* alpha below 128: unplanted;
* otherwise the nearest legend colour within 20 RGB units (Euclidean distance over R, G, B) wins,
  the first in legend order on a tie, unless black is at least as near: then the pixel is
  unplanted;
* a pixel within 20 of neither a legend colour nor black is unplanted too, and a warning lists the
  five most common such colours with the nearest legend colour and its distance.

Then, at every world load, per 12 m grid point:

* the point takes the class of the nearest pixel; outside the image nothing is planted;
* a point beyond the sampled disc (height -1000) is never planted;
* the colour claims the point only if one of its alt biomes that exists and is enabled belongs to
  the point's base biome, or one is forced, or it is `none`. Painted points it does not claim stay
  unplanted and open to random placement; the report counts them as "painted points on other base
  biomes left alone".

### 2.4 Colour scheme

One default colour per Valheim 1.0.15 alt biome, in the game's own list order. Every pair of
alt-biome colours, and every alt-biome colour and every base-biome colour of the biome legend
(black and white included), is more than 2 x 20 RGB units apart, so tolerant matching can never
confuse two of them. The closest pairs are Rock Black Forest / Trees Mistlands (52.4) and
Abomination Swamp / Mistlands (54.6).

"Plants on" is the alt biome's `m_biome` in 1.0.15. "Game count", "Chance" and "Min. distance" are
the game's own random-placement rules (`m_minAmountSpawned`-`m_maxAmountSpawned`, `m_chance`,
`m_minDistanceFromCenter`), which the quota rule counts against.

| # | Alt biome | Plants on | Hex | R, G, B | Game count (min-max) | Chance | Min. distance (m) |
|--:|:--|:--|:--|:--|:-:|:-:|:-:|
| 0 | Mushroom | Meadows, Black Forest | `FF969C` | 255, 150, 156 | 1-3 | 0.2 | 500 |
| 1 | Lantern | Meadows, Black Forest, Swamp, Mountain, Plains, Mistlands, Deep North | `F2A300` | 242, 163, 0 | 3-5 | 0.2 | 500 |
| 2 | Bones | Swamp, Plains, Mistlands | `F8E1B6` | 248, 225, 182 | 1-3 | 0.2 | 2000 |
| 3 | Menhir | Meadows, Plains | `DDA776` | 221, 167, 118 | 1-3 | 0.2 | 750 |
| 4 | Dark Meadows | Meadows | `2D4613` | 45, 70, 19 | 2-5 | 0.5 | 1900 |
| 5 | Peaceful Meadows | Meadows | `ADFFDA` | 173, 255, 218 | 1-2 | 0.2 | 1000 |
| 6 | Dandelion Meadows | Meadows | `E5C54B` | 229, 197, 75 | 1-3 | 0.2 | 500 |
| 7 | Raspberry Meadows | Meadows | `C2185B` | 194, 24, 91 | 1-3 | 0.2 | 500 |
| 8 | Smalltree Meadows | Meadows | `6EC03D` | 110, 192, 61 | 1-3 | 0.2 | 500 |
| 9 | Birch Meadows | Meadows | `CCFFAC` | 204, 255, 172 | 1-3 | 0.2 | 500 |
| 10 | Troll Black Forest | Black Forest | `2C7B76` | 44, 123, 118 | 1-3 | 0.2 | 750 |
| 11 | Root Black Forest | Black Forest | `6F5E32` | 111, 94, 50 | 1-4 | 0.2 | 2000 |
| 12 | Ruin Black Forest | Black Forest | `9D6C4B` | 157, 108, 75 | 1-5 | 0.2 | 500 |
| 13 | Rock Black Forest | Black Forest | `5A5264` | 90, 82, 100 | 1-5 | 0.2 | 750 |
| 14 | Pinetree Black Forest | Black Forest | `004754` | 0, 71, 84 | 1-5 | 0.2 | 750 |
| 15 | Blueberry Black Forest | Black Forest | `4A5CB6` | 74, 92, 182 | 1-5 | 0.2 | 500 |
| 16 | Hut Swamp | Swamp | `B1332D` | 177, 51, 45 | 1-3 | 0.2 | 1500 |
| 17 | Bog Swamp | Swamp | `6E2143` | 110, 33, 67 | 1-5 | 0.2 | 1500 |
| 18 | Bat Swamp | Swamp | `382B41` | 56, 43, 65 | 1-3 | 0.2 | 2000 |
| 19 | Abomination Swamp | Swamp | `95B17E` | 149, 177, 126 | 1-3 | 0.2 | 2000 |
| 20 | Wolf Mountain | Mountain | `93C7BC` | 147, 199, 188 | 1-5 | 0.2 | 2000 |
| 21 | Drake Mountain | Mountain | `5AAAC2` | 90, 170, 194 | 1-3 | 0.2 | 2000 |
| 22 | Fortress Mountain | Mountain | `B697AF` | 182, 151, 175 | 1-2 | 0.2 | 2000 |
| 23 | Lox Plains | Plains | `CC605F` | 204, 96, 95 | 0-1 | 0.2 | 2000 |
| 24 | Goblin Plains | Plains | `FF7622` | 255, 118, 34 | 0-2 | 0.2 | 2000 |
| 25 | Death Plains | Plains | `671E07` | 103, 30, 7 | 0-1 | 0.2 | 1000 |
| 26 | Kalhygge Black Forest | Black Forest | `A9AE34` | 169, 174, 52 | 1-2 | 0.2 | 500 |
| 27 | Rockless Mistlands | Mistlands | `D99FE8` | 217, 159, 232 | 2-5 | 0.2 | 2000 |
| 28 | Trees Mistlands | Mistlands | `472577` | 71, 37, 119 | 1-5 | 0.2 | 2000 |
| 29 | Swords Mistlands | Mistlands | `8A7FDB` | 138, 127, 219 | 1-3 | 0.2 | 2000 |
| 30 | Hare Mistlands | Mistlands | `D56EBB` | 213, 110, 187 | 1-3 | 0.2 | 2000 |
| 31 | BroodSwarm Mistlands | Mistlands | `8E2F8E` | 142, 47, 142 | 1-3 | 0.2 | 2000 |

The base-biome colours of the default biome legend (`ImageMapBiome.DefaultColors`), which the
alt-biome colours keep clear of:

| Biome | Hex | R, G, B |
|:--|:--|:--|
| None | `000000` | 0, 0, 0 |
| Meadows | `00FF00` | 0, 255, 0 |
| BlackForest | `007F00` | 0, 127, 0 |
| Swamp | `7F7F00` | 127, 127, 0 |
| Mountain | `FFFFFF` | 255, 255, 255 |
| Plains | `FFFF00` | 255, 255, 0 |
| Mistlands | `7F7F7F` | 127, 127, 127 |
| AshLands | `FF0000` | 255, 0, 0 |
| DeepNorth | `00FFFF` | 0, 255, 255 |
| Ocean | `0000FF` | 0, 0, 255 |

Incompatible pairs in 1.0.15 (the game checks both directions): Birch Meadows / Menhir; Bones /
Peaceful Meadows; Dark Meadows / Peaceful Meadows; Rock / Root Black Forest; Rock / Pinetree Black
Forest; Kalhygge / Root Black Forest; Hut Swamp / Bog Swamp; Wolf / Drake / Fortress Mountain (each
pair); Lox / Goblin / Death Plains (each pair). All 32 are enabled.

### 2.5 Config keys

Section `[08 BetterContinents.AltBiomes]` of `BetterContinents.cfg`. Every value is a default for
new worlds, baked into the world when it is created (1.7).

| Key | Type | Default | Range | Meaning |
|:--|:--|:--|:--|:--|
| `Altbiomemap File` | string | *(empty)* | | Path of the alt-biome map. Its legend is the `.txt` beside it. |
| `Mode` | string | `Random` | `Random`, `PlantedOnly`, `Off` | See 1.6. |
| `Grid` | string | `WorldEdge` | `WorldEdge`, `Vanilla` | `WorldEdge`: when the edge of the world (`World Size` + `Edge Size`) is inside 10500 m and the edge drop-off is on, grid points beyond it are marked as outside-the-world ocean, so alt biomes and the game's biome-point location candidates cannot land beyond the edge. `Vanilla`: always the 10500 m disc. No effect when Expand World Size owns the grid. |
| `Fixed Seed` | string | *(empty)* | | Seed for the game's random placement. Empty or `world`: the world seed (vanilla). A whole number is used as is; any other text is hashed like a world seed name. The same map then gets the same random layout whatever the world seed. |
| `Chance Multiplier` | float | `1` | 0 to 10 | Multiplies every alt biome's chance (result capped at 1). |
| `Amount Multiplier` | float | `1` | 0 to 10 | Multiplies every alt biome's minimum and maximum count, rounded to whole numbers. |
| `Region Size Scale` | float | `1` | 0.1 to 50 | Multiplies every alt biome's border-length window (`m_minEdgeSize`, `m_maxEdgeSize`). The game only gives alt biomes to regions roughly 0.1 to 2.4 km across; big hand-drawn regions usually need 2 to 10. |
| `Distance Scale` | float | `1` | 0 to 10 | Multiplies every alt biome's minimum distance from the centre and its world position bounds. |
| `Min Sector Thickness` | float | `0` | 0 to 20 | Regions thinner than this (area / border length, in 12 m cells) never get a random alt biome. 0 = vanilla. 2 filters the slivers an anti-aliased biome map leaves along its borders. |
| `Mean Sector Height` | bool | `false` | | Measure a region's average height as the mean over the whole region instead of the game's `(lowest + highest) / 2` of its border. Helps maps whose biome paint runs into the sea, where the border is under water and the game's measure sinks below the 30 m every alt biome requires. |
| `Fix Neighbour Check` | bool | `false` | | Use a corrected require/not-neighbour test. The game's is broken in 1.0.15: any required neighbour makes every region fail, and the not-neighbour test looks for the wrong biome except for Meadows and Swamp. No vanilla alt biome uses it; modded ones may. |
| `Overrides` | string | *(empty)* | | Per-alt-biome overrides of the random placement, see 2.5.1. |

All of these except `Altbiomemap File` affect only the game's **random** placement; planted regions
ignore them. `Min Sector Thickness`, `Mean Sector Height` and `Fix Neighbour Check` switch the
eligibility test to Better Continents' copy of `BiomeSector.CanAddModifier` with the change; with
all three off, the game's own test runs.

#### 2.5.1 Overrides

Syntax: `Name: key=value, key=value; Other Name: key=value`. Also used by `bc ab set`.

| Key | Value | Replaces |
|:--|:--|:--|
| `enabled` | `true`, `false`, `1`, `0` | `m_enabled` (random placement only) |
| `chance` | 0 to 1 | `m_chance` |
| `min` | 0 to 10000 | `m_minAmountSpawned` |
| `max` | 0 to 10000 | `m_maxAmountSpawned` |
| `mindist` | 0 to 1000000 (m) | `m_minDistanceFromCenter` |
| `minedge` | 0 to 100000000 (cells) | `m_minEdgeSize` |
| `maxedge` | 0 to 100000000 (cells) | `m_maxEdgeSize` |
| `minheight` | -100000 to 100000 (m) | `m_minAvgHeight` |
| `maxheight` | -100000 to 100000 (m) | `m_maxAvgHeight` |
| `ignorebounds` | `true`, `false` | `true` switches off `m_aboveWorldX/Y` and `m_belowWorldX/Y` |

* Values out of range are clamped. An empty value or `default` clears the field.
* A name may contain `*` wildcards; `*` alone applies to every alt biome. Names are compared
  case-insensitively.
* Precedence, field by field: the exact name, then the matching patterns (more literal characters
  first, then ordinal order), then `*`.
* An override replaces the multiplied value for its field: `chance=0.5` is 0.5 whatever
  `Chance Multiplier` says.
* If the resulting maximum is below the minimum, the maximum is raised to the minimum. A disabled
  alt biome gets minimum 0, so the game does not warn that it placed too few.
* Overrides never touch planted regions: `Fortress Mountain: enabled=false` stops the game placing
  Fortress Mountain at random, and planted Fortress Mountain still works.

Examples: `Fortress Mountain: enabled=false`, `*Mistlands: chance=0.5; Hare Mistlands: max=1`,
`*: minedge=0, maxedge=100000`.

### 2.6 Commands

`bc ...` is Better Continents' existing debug command: a cheat (devcommands, or Better Continents'
`Debug Mode`) that runs on the machine that hosts the world. Edits change the loaded world's
settings, are saved with the world, and redo only as much of the pipeline as they affect
(**Assignment**: placement only; **Sectors**: regions and placement; **Points**: the whole grid),
then reset the zones. Connected clients keep their placement until they reconnect. A value
command without an argument prints the current value.

| Command | Does | Redoes |
|:--|:--|:--|
| `bc ab info` | Summary of this world's alt biomes and why each did or did not place, what each legend line planted, and up to 40 planted regions. | |
| `bc ab list [filter]` | Up to 60 regions with alt biomes; filter `planted`, `random`, `all`, a biome name, or part of an alt-biome name. | |
| `bc ab here` | The region you stand in, what was planted on it, and how each alt biome of its biome fared. | |
| `bc ab show [filter]` | Map pins on every region with an alt biome (optional alt-biome name filter). | |
| `bc ab hide` | Removes those pins. | |
| `bc ab export` | Writes `altbiomes-<time>.png` (the region grid, north up, alt-biome regions tinted with their alt biome's colour, planted ones striped magenta, borders darkened) and a `.txt` report. | |
| `bc ab hash` | This machine's grid and placement hashes. | |
| `bc ab names [filter]` | The game's alt biomes with their default colours and random-placement rules. | |
| `bc ab rebuild` | Regenerates grid, regions and placement from the current settings. | Points |
| `bc ab fn [path]` | Sets the alt-biome map (full path, directory or file name) and bakes it with its legend. Clearing the field in the Better Continents debug menu (Alt+F8) removes it. | Sectors |
| `bc ab mode [Random\|PlantedOnly\|Off]` | Mode. | Sectors |
| `bc ab grid [WorldEdge\|Vanilla]` | Grid. | Points |
| `bc ab seed [value]` | Placement seed: a number or any text; `world` = the world seed. | Assignment |
| `bc ab chance [0-10]` | Chance multiplier. | Assignment |
| `bc ab amount [0-10]` | Amount multiplier. | Assignment |
| `bc ab regionscale [0.1-50]` | Region size scale. | Assignment |
| `bc ab distancescale [0-10]` | Distance scale. | Assignment |
| `bc ab thickness [0-20]` | Min sector thickness. | Assignment |
| `bc ab meanheight [true\|false]` | Mean sector height. | Assignment |
| `bc ab neighbourfix [true\|false]` | Fix neighbour check. | Assignment |
| `bc ab set <name\|pattern\|*> <field> <value>` | Sets one override field; `default` clears it. | Assignment |
| `bc ab clear [name\|pattern\|*]` | Clears one entry's overrides, or all without a name. | Assignment |
| `bc ab overrides` | Prints the world's overrides. | |
| `bc ab reroll [seed]` | Sets a new fixed placement seed (random, or the one given). | Assignment |
| `bc reload ab` | Re-reads the alt-biome map and its legend from their files and bakes them again. `bc reload all` includes it. | Sectors |

`bc ab help` lists the group. For every player, on a client or a host:

| Command | Does |
|:--|:--|
| `bc_altbiomes` | Hashes and agreement with the server, and the region you stand in. |
| `bc_altbiomes here` | The region you stand in, in full. |
| `bc_altbiomes names [filter]` | Alt-biome names and default colours (works on the main menu too). |
| `bc_altbiomes hash` | Hashes and agreement with the server. |
| `bc_altbiomes list [filter]` | Up to 200 regions with alt biomes, same filters as `bc ab list`. It reveals where they are, so on a client it needs devcommands, like the game's own `altbioms`. |

### 2.7 Modes, precisely

* `Random`: planted regions are split out and planted first; the game's random placement then runs
  with the world's options on every region that carries no planting. Split-out regions are hidden
  from its lists; reused and point-planted regions (and `none` regions) are refused by a
  `CanAddModifier` gate while it runs.
* `PlantedOnly`: the same regions and planting; the random placement does not run.
* `Off`: no region is split, nothing is planted, the random placement does not run, and alt biomes
  already assigned are cleared. An unreadable alt-biome map does not stop the load in this mode.

### 2.8 Save format

Three new keys in Better Continents' tagged settings stream, after `SkipDefaultLocations` (63). The
settings format version stays 11. Each key is written only when used, so a world that uses no
alt-biome feature is saved byte for byte as 0.8.0 saves it.

| Key | Value | Written when |
|:--|:--|:--|
| `AltBiomes` = 64 | byte array: the options blob | the world has alt-biome options that differ from 0.8.0 behaviour for its edge of the world, or a blob this build could not fully read was kept |
| `AltBiomeMap` = 65 | byte array: the baked map block | the world has an alt-biome map (or an unreadable block was kept) |
| `AltBiomeMapPath` = 66 | string: the image path | after key 65, on disk only; never sent to clients |

Options that match 0.8.0 behaviour: mode `Random`, no fixed seed, all three eligibility options off,
all four multipliers 1, no overrides, and grid `Vanilla` or an edge of the world at 10500 m or
beyond. A new world created with the default config on the standard World Size (10000) and Edge
Size (500) therefore writes no key 64; a new world whose edge is inside 10500 m writes it, because
`Grid` defaults to `WorldEdge`.

**Options blob** (a `ZPackage`, format 1): `int` format version, `byte` mode (0 Random,
1 PlantedOnly, 2 Off), `byte` grid (0 Vanilla, 1 WorldEdge), `bool` fixed seed, `int` seed, `bool`
neighbour fix, `bool` mean sector height, `float` min sector thickness, `float` chance multiplier,
`float` amount multiplier, `float` region size scale, `float` distance scale, `int` override count,
then one byte array per override, sorted by name (ordinal): `string` name, `int` field mask, then
the fields whose bit is set, in bit order: bit 0 `enabled` (bool), 1 `chance` (float), 2 `min`
(int), 3 `max` (int), 4 `mindist` (float), 5 `minedge` (int), 6 `maxedge` (int), 7 `minheight`
(float), 8 `maxheight` (float), 9 `ignorebounds` (bool).

**Map block** (a `ZPackage`, version 1): `int` block version, `int` class count (class 0,
unplanted, is implicit), per class `byte` r, g, b, a, `byte` flags (1 point plant, 2 none), `int`
entry count, per entry `string` name or pattern and `bool` forced; `int` point count, per point
`float` x, `float` z, `int` class; `int` map size; byte array: the class map, row 0 = south, as runs
of `byte` class + LEB128 run length; `string` the legend text as read.

Reading: a blob or block with a higher version is read as far as this build knows and saved back
unchanged until it is edited. An options blob that cannot be read leaves the world on 0.8.0
behaviour and is saved back unchanged. A map block that cannot be read is saved back unchanged and
stops the world load (1.9).

### 2.9 The quota rule with the game's numbers

For each alt biome, the game's random placement runs while `planted + placed < max`, ignores the
chance while `planted + placed < min`, and needs every region to pass `CanAddModifier`. With the
1.0.15 data (table in 2.4):

| Planted | Fortress Mountain (1-2) | Lox Plains (0-1) | Dark Meadows (2-5) |
|:-:|:--|:--|:--|
| 0 | at least 1, at most 2 random | at most 1 random | at least 2, at most 5 random |
| 1 | at most 1 more random | none | at least 1, at most 4 more random |
| 2 | none | none | at most 3 more random |
| 3 | none (all 3 planted) | none | at most 2 more random |

"At least" holds only where enough regions pass the game's tests. `Amount Multiplier` and the `min`
and `max` overrides change these numbers; planted regions count against the changed ones. An alt
biome of several base biomes (Lantern, 3-5) counts all of them together. On Ashlands, Deep North and
Ocean, one colour is one region and counts once.

### 2.10 Network

| RPC | Direction | Payload |
|:--|:--|:--|
| `BetterContinentsAltBiomes` | server to client, before `PeerInfo` | `ZPackage`: `int` 1, `int` grid hash, `int` placement hash, `int` count, per region with alt biomes `int` first grid point x, y, `int` biome, `int` area, `int` name count, `string` names |
| `BetterContinentsAltBiomesResult` | client to server, after the client applied it | `int` grid hash, `int` placement hash |

A client applies an entry only where its own region under that grid point has the same biome and
an area within 5 % of the server's (or within 16 cells, whichever is more).

### 2.11 Log lines

At startup:

```
[BetterContinents] Deep North weather: EnvMan.UpdateEnvironment now asks IsDeepnorth with the camera's z on worlds with a biome map (1 call).
[BetterContinents] Deep North weather: EnvMan.GetBiome now asks IsDeepnorth with the camera's z on worlds with a biome map (1 call).
[BetterContinents] Alt biomes: patches bound - grid clamp for resized grids (GetBiomeSector) 1, Deep North weather (EnvMan) 2 with 2 IsDeepnorth call(s) rewritten, placement (GenerateAltBiomes) 4, biome cache fingerprint 2.
```

At world load (from the dedicated-server test with a planted copy of a real world):

```
[BetterContinents] Deep North weather: EnvMan asks IsDeepnorth at the camera's x and z (this world has a biome map)
[BetterContinents] Alt biomes: split 7 planted region(s) out of 6 region(s) (0 fully planted and reused), 29175 of 4194304 grid points planted.
[BetterContinents] Alt biomes (world load): mode Random, placement seed world seed, grid 2048 x 12 m to 10500 m (Vanilla), 1687 sectors, 14 with alt biomes (7 planted, 7 random), 8 planted region(s)
[BetterContinents] Alt biomes: hashes grid 13887407, assignment 42c1050b
[BetterContinents] Alt biomes: planted: 8 region(s), 7 alt biome(s) applied, 1 protected (none), 0 skipped for the base biome, 0 refused (disabled or incompatible)
[BetterContinents] Alt biomes:   #2D4613 [Dark Meadows] -> Dark Meadows: 4401 grid points in 1 region(s)
[BetterContinents] Alt biomes:   #B1332D [Hut Swamp] -> Hut Swamp: 4417 grid points in 1 region(s), 4 painted points on other base biomes left alone
[BetterContinents] Alt biomes:   #C030F0 [none] -> nothing (a protected region): 2664 grid points in 1 region(s)
[BetterContinents] Alt biomes:   point at 3728, 7088 [Bat Swamp] -> Bat Swamp: 1 region(s)
[BetterContinents] Alt biomes:   #1681 Meadows [planted #2D4613 [Dark Meadows]] centre (3100.3, -4566.7) edge 244 area 4401 thick 18 h 40.9 (mean 66.4) dist 5519.7 -> Dark Meadows
```

The summary is followed by one line per base biome, the biomes absent from the map, and per alt
biome why it did or did not place ("+N planted (counted toward the maximum)" where planting
counts). In multiplayer the server logs `Sending alt-biome placement to client ...` and `Alt biomes:
client <name> matches the server (grid ..., assignment ...).` or a warning; the client logs
`Alt biomes: received the server's placement (N sectors with alt biomes).` and `Alt biomes: matches
the server (...)` or a warning. A failed load logs `Better Continents: <step> failed for world
'<name>': <cause>. The world load was stopped so that no zone is generated with the wrong alt
biomes. ...`, and a dedicated server then `Better Continents: stopping the dedicated server because
the world failed to load (see the error above).`

### 2.12 Limits

* 254 legend entries (distinct colours plus point lines). Images up to 16384 x 16384.
* The grid is 12 m: a patch narrower than a cell or two can vanish or break into several regions.
  Paint patches at least a few cells wide.
* Only the sampled disc has regions: 10500 m, the world edge with `Grid` `WorldEdge`, or Expand
  World Size's radius. Planting beyond it does nothing. Beyond the WorldEdge cut-off, and beyond
  10500 m on a world with the edge drop-off disabled, `GetBiomeSector` returns a plain region of
  the real biome, without alt biomes.
* Ashlands, Deep North and Ocean are one region per colour: two Deep North areas painted with the
  same colour are one region (and count once toward the maximum); use two colours to make two.
* Planting chooses alt biomes, not terrain or base biomes.
* Colours are matched when the map is baked. Editing the PNG or the legend later changes nothing
  until `bc reload ab` (or `bc ab fn`) bakes it again.
* Zones the game has already generated keep the vegetation and locations they got. Plant before a
  world is explored.

---

## 3. The three fixes

### 3.1 `GetBiomeSector` on resized grids (Expand World Size issue #26)

`WorldGenerator.GetBiomeSector(int gridx, int gridy, bool clamp)` clamps the grid index to the
literal 2047, whatever the grid size. Expand World Size resizes the grid with its radius but skips
its own fix of this method whenever Better Continents is enabled for the world. On a smaller grid
the lookup then throws `IndexOutOfRangeException` (radius 6000: grid 1171, index 2000); on a
bigger one every lookup past index 2047 lands on the wrong column or row (radius 15000: grid 2926,
x = 14000 m is index 2629, read from column 2047 at x = 7014 m).

0.8.1 prefixes that overload: on a grid whose size is not 2048 it clamps to the real size; a
2048 grid runs the original untouched. The harness checks radius 6000, 10500 and 15000.

### 3.2 Stale biome grid

Valheim 1.0.7 and 1.0.12 cached the point grid in
`<save data>/cache/<world name>_biomedatacache.bin`, keyed on the world name and checked against the
world version only, so a grid built for other
Better Continents settings (a replaced settings file, an edited map, a copied world, a mod update)
was reused. 0.8.1 appends a fingerprint trailer to the game's cache file on Better Continents
worlds: SHA-256 over the Better Continents and game versions, the world seed and world generation
version, the grid geometry, and the settings that shape the grid (the alt-biome keys excluded; they
change regions, not the grid), then the trailer version (1) and the magic `BCFP`. A cache whose
trailer does not match is not loaded, so the game rebuilds and rewrites it. The trailer sits after
the game's payload, where the game's reader never looks, so the file stays readable without the
mod. A vanilla world whose cache carries a Better Continents trailer is rebuilt too.

**Valheim 1.0.15 does not use the cache at all.** Its `VerifyBiomeData` is `RemoveCache`, then
`GenerateBiomePoints`, then `GenerateSectors`, and nothing calls `TryLoadCache` or `SaveCache`
(read from the IL of the 1.0.15 client, `assembly_valheim.dll` md5 2fb85d90..., and dedicated
server, md5 47df8869...; also `libs-Tools/1.0/DECOMPILED/assembly_valheim.decompiled.cs:92753`).
On 1.0.15 a grid can therefore never be stale across loads, and the two fingerprint patches bind
but never run; they stay as a guard for any game version that reads the cache again. Note that
`libs-Tools/Decompiled_1.0.15/assembly_valheim/` is a 1.0.12 decompile (`Version.CurrentVersion` is
1.0.12 there), which still shows the cache.

What does matter on 1.0.15 is staleness *within* a session. The game builds the grid only at world
load, but Better Continents' debug commands change maps and settings live. 0.8.1 rebuilds the grid,
regions and placement whenever they change (on a worker thread for the grid), and only then resets
the zones; the zone reset also clears each terrain chunk's cached corner regions, which the game
only ever appends to.

### 3.3 Deep North weather

`EnvMan.UpdateEnvironment` and `EnvMan.GetBiome` decide Deep North weather with the camera's
**height** as the second coordinate:

```csharp
bool flag  = WorldGenerator.IsAshlands(position.x, position.z);
bool flag2 = WorldGenerator.IsDeepnorth(position.x, position.y);    // y is the camera height
```

(1.0.15: `UpdateEnvironment` IL_006f and `GetBiome` IL_0059,
`libs-Tools/1.0/DECOMPILED/assembly_valheim.decompiled.cs:96367-96368` and `:96455-96456`.) On a
Better Continents world with a biome map, `IsDeepnorth` reads the biome map, so it samples the map
at (x, camera height), in the map's middle rows: a map with Deep North there gives Deep North
weather at sea anywhere in that band of x.

0.8.1 rewrites both calls into `DeepNorthWeather.IsDeepnorth(x, y, z)`, which passes z on a Better
Continents world with a biome map, so Deep North weather follows the map as Ashlands weather
already does, and y everywhere else, so every other world keeps the game's behaviour, bug
included. The transpiler only rewrites the exact shape `ldloc v; ldfld Vector3::y; call
IsDeepnorth` and warns if it finds anything else; the harness checks the shape against the real
1.0.15 IL.

### 3.4 Also fixed in the same pipeline

* **Land beyond the sampled disc.** Past 10500 m every grid point is ocean, which is right while
  the terrain there is ocean. On a Better Continents world with the edge drop-off disabled, land
  continues past 10500 m and took the ocean's region for its terrain biome, vegetation and spawns.
  There, and beyond a `WorldEdge` cut-off, `GetBiomeSector` now returns a plain region of the real
  biome, without alt biomes.
* **Locations that accept several biomes.** `AltBiomeWorldData.RandomBiomeFromBiomes` picks which
  biome such a location searches, and in 1.0.15 it returns Black Forest for Plains, tests the
  Meadows bit for Ocean, and never picks the last matching biome (`Random.Range(0, num - 1)`). On a
  Better Continents world, when its pick is outside the requested biomes or absent from the map,
  one of the requested biomes the map has is picked instead. A usable pick is kept, so complete
  maps place as before.

---

## 4. Testing

`tools/altbiome-harness` is an offline test program. It loads the real pre-ILRepack
`BetterContinents.dll` and the real Valheim 1.0.15 assemblies and runs everything that does not
need the Unity engine: settings and save format (including real 0.8.0 worlds round-tripped byte
for byte), the legend and colour scheme, the region build against the game's own
`GenerateSectors`, the partition, the quota rule and the three modes through a real Harmony-patched
placement run, server placement, the cache trailer, the three fixes and the failure path.

```
dotnet build BetterContinents.csproj -c Release
cd tools/altbiome-harness
dotnet run -c Release                  # every check; regenerates ../../palettes
dotnet run -c Release -- help          # the tools used for the dedicated-server test
```

It needs `libs-Tools` next to the repository and the 1.0.15 alt-biome data extraction
(`altbiomes_1.0.15_full.json`, path overridable with the `ALTBIOMES_JSON` environment variable).
Its project file is ignored by git, like every `.csproj` except the mod's.

- v0.10.7
  - Per-Biome Procedural Height Noise (Selective Mountain Jaggedness):
    - When `Heightmap Override All = true`, lowlands, plains, meadows, towns, docks, and canals remain 100% faithful to the PNG heightmap (zero noise).
    - Mountain and Deep North biomes dynamically blend Valheim's natural alpine fractal ridges, couloirs/micro-gullies (10m and 2.5m frequencies), and slope tilt at runtime, controlled by `Mountain Noise` (`bc g mn <scale>`, default `1`).
    - Mistlands biomes blend stepped terrace crags and fractal noise, controlled by `Mistlands Noise` (`bc g msn <scale>`, default `1`).
    - Smooth altitude attenuation prevents procedural noise from affecting sea level or coastal lowlands.
  - Location Exclusion Mask (`no_spawns_mask.png` / `nospawns.png`):
    - Added image map support for designating protected zones (towns, historical centers, canal corridors) where random location candidate placement (crypts, dungeons, camps, bosses) is suppressed.
    - Unlike binary `Skip Default Locations`, the exclusion mask rejects placement candidates without consuming the world location placement counter, allowing the game to naturally distribute locations across eligible territory elsewhere.
    - Explicit location pins placed via `locationmap.png` remain unblocked.
    - Configured via `Location Exclusion File` in `[06 BetterContinents.Maps]` or by dropping `no_spawns_mask.png` into the world folder.
  - Detailed Bake Meshes (High-Precision 32-bit Float Positions & Normals):
    - Switched default proxy merge geometry format to full 32-bit float positions and normals (`VertexLayout.Plain`) in `BakedProxyMerge`.
    - Maximizes edge sharpness, vertex precision, and lighting fidelity across dense settlements and baked town structures while maintaining background GPU batching. Optional compact half-precision format remains available via `BC_SHADOW_PROXY_FORMAT=compact`.
  - Deep North Cold Map (`coldmap.png` / `Coldmap File`, `Coldmap Scale`):
    - Added image map support for Deep North cold regions driving cold weather, freezing effects, snow-vs-cultivate terrain painting, and calm waters wherever pixels are above black.
    - Decoupled from vanilla's hardcoded North circle (`WorldGenerator.IsDeepnorth`), falling back to custom Deep North biome areas when no cold map is present.
    - Configured via `Coldmap File` in `[06 BetterContinents.Maps]` or `coldmap.png` in the world directory; scale multiplier set via `Coldmap Scale` (`bc cold sc <scale>`, default `10`).
  - Deep North Calm Sea & Wave Fade Decoupling (`Deep North Water`):
    - Decoupled `WorldGenerator.DeepNorthWaveFade` from vanilla's hardcoded north coordinates `(0, 4000), dist >= 12000m`, dynamically aligning calm seas with custom Deep North territory and the cold map.
    - Controlled via `Deep North Water` (`[01 BetterContinents.Global]`, console `bc g dnw <on|off>`, default `true`).
  - Ashlands Water Decoupling Toggle (`Ashlands Water`):
    - Added configurable toggle `Ashlands Water` (`[01 BetterContinents.Global]`, console `bc g aw <on|off>`, default `true`) to control whether Ashlands hot sea water coloring follows Better Continents' custom heat maps and Ashlands biomes rather than vanilla's hardcoded southern boundary.
- v0.10.6
  - HarmonyX and assembly reflection fix: resolves `System.Reflection.ReflectionTypeLoadException`
    (`Could not load type of field 'System.Text.EncodingTable:s_codePageToItemCache' due to missing
    'System.ValueTuple, Version=4.0.5.0'`) on player spawn. Under Unity Mono on Linux, `System.ValueTuple`
    is built into mscorlib rather than provided as a separate assembly; the dependency from
    `System.Text.Encoding.CodePages 10.0.3` referenced the external assembly, causing type loading to fail
    and breaking assembly scans by mods such as Infinity Hammer. Downgraded `System.Text.Encoding.CodePages`
    to 8.0.0 (LTS), removing the tuple-based encoding cache and eliminating the external assembly reference.
- v0.10.5
  - Waves out to the edge of a big world. The game draws its calm Deep North sea and its red Ashlands sea
    from two circles of a 10 km world, written into its water shader, so on a bigger world the sea
    past those circles was calm and the far sea looked flat. In a world that Better Continents shapes and
    whose sea is not the game's (a big world, or one with its own Deep North or Ashlands), the sea is now
    drawn by Better Continents' own water: waves and white crests out to the world's edge, calm only in
    the world's own Deep North, and the Ashlands' red only where the world's own hot sea is. It looks like
    the game's water (the same colours, glare and foam), and it needs no new world data: an existing world
    gets it the moment it loads, and nothing is made again.
  - Boats, swimmers and anything that floats feel the waves that are drawn, on every machine and on a dedicated server
    (drawn and felt waves agree to within 4 mm in the tests). Waves are real geometry in the zones that are
    loaded; the far plane beyond them, about 130 m out, is flat with the game's ripple texture, as in the
    game. A storm gives a real swell with whitecaps where the game's far sea stays flat.
  - Two new settings in [11 BetterContinents.Water], each player's own and applied at once: Own Water
    (off: the game's water, with the colours of 0.10.3) and Soft Far Sea (the far sea takes the depth
    of the sea under it, so the near and far water meet without light and dark strips seen from high up).
    If a graphics device cannot run the water, or another mod has replaced the game's water shader, that
    player sees the game's water and the world is unchanged for everyone else. `bc_water` says whether
    Better Continents' own sea is on and why; with Debug Mode, `bc water force on|off` and `bc water probe`
    compare it with the game's water.
  - `bc genloc` runs the game's location generation again with a report: what it found, what changed,
    and the new location icons sent to the players. When a run that threw left the game's own `genloc`
    stuck (it does nothing while its slot is taken), `bc genloc` frees it first. Locations that are
    placed stay. It runs on the machine that runs the world and needs `devcommands`, as the other `bc`
    commands do (Debug Mode turns it on). `bc genloc alt` passes to the game's alt-biome branch.
  - Lakes above the sea (experimental). Valheim has one water level, so every lake in the mountains is a dry hollow.
    A world can now carry a list of lakes, each with its own water height: Better Continents puts the
    game's own water in them, so you swim, sail and see them on the minimap and the large map,
    and boats feel the lake's waves. Each lake fills its basin up to its level; lakes are built as you
    come near and dropped behind you, a few milliseconds a frame. Lakes are written to follow the
    world's rules (no waves in the calm Deep North, red and hot where the world's hot sea is; this is
    how the code works and has not been seen in a game yet), and Better Continents' own water is
    drawn on them when it is on. This is Tarn, the mod by Raven Iron (Wubarrk and
    NomadicWar), brought into Better Continents by Wubarrk.
  - The lake list is `lakes.txt` in the maps folder (one lake a line: name; x; z; level; the box the lake
    may fill; depth; Tarn's own format, so a map maker's file works as it is; `tarn-lakes.txt` is read
    too). It is saved with the world, sent to every player, carried by export and import and `.bcworld`
    files, and a dedicated server builds no water of its own. In game, `bc_lake here` fills the hollow you
    stand in up to just under its spill point, `bc_lake at <x> <z>` does it anywhere, `bc_lake list`,
    `info`, `remove`, `depth` (how deep lakes look; one value for the world, sized so that boats and
    swimmers feel the waves they see), `load`, `import`, `export` and `regen`. The words that replace or
    remove data (`load`, `import`, `remove all`, `regen`, and writing over a file) need `confirm` and
    keep a backup of what they replace; on a server the words that change lakes need an admin.
  - Clearing (experimental). A map maker's `clearmask.bin` (a bitmask over the ground, Tarn's `.clear` file) keeps trees,
    rocks, bushes and pickable debris off marked ground, such as roads and lake beds; only what would
    have stood on a marked spot is left out, everything else is placed as before. It acts on zones that
    are generated after it exists, so make a new world or regenerate those zones. `bc_lake mask at`,
    `load` and `export` look at it and change it.
  - Everything that comes from Tarn (lakes, clearing, the `bc_lake` commands and the import of Tarn's
    files) is experimental for now: it works in the tests and on test worlds, but details may still
    change, so try it on a copy of a world first.
  - A world from Tarn: Better Continents reads Tarn's own files (`BepInEx/config/Tarn/<world>.txt` and
    `.clear`) once, when the world has no lakes or mask yet, and keeps them in the world; Tarn's folder is
    never written to. If the Tarn mod itself is also installed, Better Continents' lakes and clearing step
    aside and say so, and nothing is lost.
  - New settings in [12 BetterContinents.Lakes]: Lake Depth Scale (a world setting), Lake Build Range,
    Lake Build Budget, Lake Sound and Lakes On Map (each player's own, applied at once).
  - A world with lakes or a clearing mask needs 0.10.5 or later: an older Better Continents opens it as
    a world without Better Continents (vanilla terrain). The settings stay in the world's files.
  - New guide: Water and lakes, the easy guide (BetterContinents-Water-Guide.pdf, GPL-3.0), in this
    package.
  - Baked buildings: a painted zone you teleport to now gets its paint at once (a road in a zone without
    pieces stayed bare until you walked to another zone); `bc_bake load` with no file now prints a
    `confirm` line that can be typed back as it stands; and `bc_bake undo` finds its undo files in a
    world that was renamed.
  - Updated AI doc to v0.10.5.
- v0.10.4
  - Baked buildings. A bake takes the pieces players built within a circle or a box and makes them part
    of the world's layer: every player's game draws them, solid to walk on, and none of them is a game
    object any more, so the world neither saves, sends nor updates them one by one. In a test village
    the objects within 128 m went from 7,984 to 2,472 (5,512 pieces baked); in the densest block of a
    700,000-piece town the frame rate went from 45 fps as real pieces to 81 fps baked (measured on one
    machine). The export window (F7) has a third tab, Bake buildings, and the console has `bc_bake`.
    Every change shows a dry run first and needs Confirm (or `confirm`).
  - What stays real: doors, chests and other containers, stations, beds, cooking fires, portals, wards,
    signs, anything that moves, and any part of a mod that Better Continents does not know. With the
    Town option a bake also takes comfort decor (tables, banners, rugs, plants; chairs and benches
    become seats you can still sit on) and torches and lanterns, which become lit copies that never need
    fuel; the pieces that stay become protected parts of the layer that nobody can damage or remove. A
    baked piece gives no comfort and a baked light no warmth.
  - Nothing that gives items is baked: bushes, mushrooms, flax, saplings and items on the ground stay
    the game's own. Black marble and the other pieces the game turns at random bake, each drawn with its
    own turn.
  - Undo and unbake. `bc_bake undo` puts back exactly what the last change took, from its undo file;
    `bc_bake unbake` turns baked pieces in an area back into real pieces; `bc_bake unbake world` does it
    for every baked piece, the way out before removing Better Continents from a world. Every change
    writes its undo file before it changes anything, and a crash or a cut save is finished or taken back
    at the next load: no piece is ever lost, and none is both a real piece and a baked one. An undo also
    finds a piece that the game's physics dropped or lifted, and every change reports what it could not
    find.
  - Town files. A `placements.bcp` in the maps folder (format 1, written by a town compiler) builds its
    towns into a new world: drawn pieces, live pieces made real (doors, stations, chests) and protected,
    the ground under the towns, paint and cleared vegetation. `bc_bake load` brings a newer file into a
    running world; the live pieces follow, and a container that holds items is never taken away.
    Consumables in the file are placed once as the game's own objects, unless the file marks a kind as
    decor: then it is drawn like the rest of the town and cannot be picked. `bc_bake export` writes the
    layer out.
  - Players download the layer when they join (a few MB for a whole town file, kept in the cache for the
    next time), and a change made during play reaches them as a small patch before any piece is taken
    away.
  - New settings in [10 BetterContinents.BakedPlacements], each player's own and applied at once: Draw
    Distance, Draw Scale, Detail Scale, Shadows, Light Distance, Light Count (how many baked lights
    burn: the nearest 24 by default; the rest are drawn unlit), Light Shadows (how many of them cast
    shadows: 4), Seat Distance, Tints and Diagnostics.
  - Shadows of baked pieces. Near you, a zone's shadow-casting pieces are merged into a few shadow-only shapes, built in
    the background on the other processor cores; the pieces you see are drawn as before, and the shadows look the same.
    Farther out, each zone's or slab's shadows are drawn on their own, so a light or shadow band redraws only what it
    reaches. In the busiest view of a test town of 33 blueprint buildings the frame rate went from 38 to 63 fps, with
    about 350 MB of graphics memory for those shadows (measured on one machine).
  - A world with baked pieces needs 0.10.4 or later: an older Better Continents opens it as a world
    without Better Continents (vanilla terrain), and the game alone without its layer; either way its
    baked pieces are neither drawn nor solid there. The settings and the layer stay in the world's files,
    and 0.10.4 reads them again.
  - New guide: Baked buildings, the easy guide (BetterContinents-Baking-Guide.pdf, GPL-3.0), in this
    package.
  - Updated AI doc to v0.10.4.
- v0.10.3
  - Maps up to 16,384 x 16,384 pixels. Pictures are read a few rows at a time and kept as tiles: a
    server making a world from five 16,384 px maps peaked at 1.80 GB of memory, where 0.10.2 needed
    5.83 GB (measured on one machine). A bigger picture is refused before it is read. `Max Map Size` in
    [07 BetterContinents.Misc] (4096, 8192 or 16384; 16384 by default) can set a lower limit for map
    files; it is each machine's own and applies the next time a map is read.
  - Compact Maps is Auto / On / Off, Auto by default. Auto makes a new world compact when any of its maps
    but the location and alt-biome maps is more than 8,192 px across, or when its heightmap has fine
    heights: five 16,384 px maps take 52.6 MB in the world file instead of 352.5 MB. On makes every new
    world compact and Off none (heightmap-fine.png is then not read). An old `true` reads as On and
    `false` as Auto.
  - Worlds past 16 km. Valheim files objects by zone only out to about 16.4 km from the centre. Past that,
    far objects shared one list that was sent whole to any player out there, a patch about 22.1 km
    south-west shared its save file with every portal in the world, and the creature spawners of zones
    past 32.7 km east, west, north or south were moved to the 20 km line at every save. `Wide Sectors` in
    [07 BetterContinents.Misc] gives every zone out to 65.5 km its own place in memory and in the save,
    and saves the spawners where they are. Auto, the default, makes every new world past 16,350 m wide and
    leaves existing worlds as they are. On also converts an existing big world when it loads, one way: the
    game first saves a copy of the old save, and damage already done is not undone. Off makes no new wide
    worlds; a wide world stays wide. The setting is read by the machine that runs the world, and a joining
    player's game follows the world. A wide world needs 0.10.3 or later: an older Better Continents opens
    it as a world without Better Continents, and an older version, or the game alone, that saves it writes
    its objects twice. World Size + Edge Size can go up to 65,000 m.
  - Terrain up to 16 km high. `Heightmap Amount` takes 0 to 81 (it was 0 to 5); at 81, white is 16,170 m
    on the game's height scale. A world whose heightmap is read at an Amount above 5 gets Better
    Continents' versions of the game's height rules, which were made for land under about 400 m: inside a
    dungeon means more than 3,000 m above the ground below, the ground is found at any height, and the
    altitude limits of 1,000 m or more for plants, creatures, locations and grass are lifted. Worlds read
    at 5 or less keep the game's own rules. `High Terrain` in [02 BetterContinents.Heightmap] (Auto, On or
    Off; Auto by default) gives them to every world or to none; its description says what Off costs. A
    new world keeps the mode it was made with, and `bc h ht` changes it in a running world. Older versions
    open a world whose High Terrain is On or Off as a world without Better Continents.
  - Fine heights. An optional `heightmap-fine.png` beside the heightmap adds 1/256 of a 16-bit step to each
    height, 24 bits in all, so the gentle slopes of a tall world stay smooth. A record inside it ties it to
    its heightmap by CRC-32. A world made with it is compact, and 0.10.0 to 0.10.2 cannot read it. The
    world export writes it: `bc_export fine=auto|off|4|8`, or the Fine heights choice in the export window.
    Auto, the default, writes 4 bits where one step of heightmap.png is more than 1.5 cm, from a Heightmap
    Amount of about 4.92, so an export at a lower Amount is the same as before.
  - The world export goes up to 16,384 px, and `Default Heightmap Amount` and `bc_export amount=` take 0.01
    to 81. An export holds about 100 MB of memory at any size (measured on one machine), because each map is
    written as it is sampled. `Largest Size` in [09 BetterContinents.Export] (4096, 8192 or 16384; 16384 by
    default) sets the largest export offered. Above 8,192 px the export leaves the New World preset to
    `bc_import`.
  - A world with a very large World Size loads in seconds: 16 s for World Size 32,264 on a 32-core server
    (measured on one machine). The game's lake search took minutes there on every machine at every load; it
    now takes seconds and finds the same lakes.
  - Making a new world no longer stops the game. The New World screen read a new world's maps and wrote its
    settings inside Done, on the game's main thread: with five 16,384 px maps the game stood still for about
    30 s, drawing nothing and answering nothing (measured on one machine). They are now made in the background
    behind the game's "Please wait", and the world is made when they are ready; its settings are the same, byte
    for byte. A dedicated server makes a new world's settings as before. Lines Better Continents logs from work
    in the background (a new world's settings, a world import) now reach the log; Unity used to drop them.
  - Loading a big world, or joining one, no longer stops the game for half a minute. Whenever a world loads,
    and as each player joins, the game builds its alt-biome grid on its main thread: 6,392 x 6,392 points for a
    world of World Size 32,264, which took 29 s (measured on one machine). Better Continents now samples the grid
    on every core and builds its regions on flat arrays: 5 s on the same machine (32 cores; fewer cores take
    longer). The grid and its regions are the game's own, point for point. Where another mod changes those
    steps, the game's own way runs.
  - Joining a big world: the server's log says how long the download will take. A world's settings
    package and the `.bcworld` file can be up to 1.5 GB (the file was limited to 512 MB).
  - The sea's colour and waves in a big world. The game's water shader colours the Ashlands sea red by its
    own ring of a vanilla-size world, so a bigger world, or one with a heat map, had red water where the
    sea is cold (on a 32 km world, canals 20 km from the centre). Each 64 m patch of water now takes its
    colours from the world's own hot sea, the sea that heats swimmers and damages ships; only the sea
    counts, so lava on land leaves the water beside it plain, and with a heat map the sea is red only
    where the heat map has heat over it. The shader also draws the waves dying down past its own circle,
    12,000 m from (0, -4,000) in every direction, which in a big world is most of the sea. Better
    Continents had moved the waves that boats and fish float on, by the biome map since 0.8.0 and by the
    world's size since 0.10.0, so they floated on waves that were not drawn. They now float on the waves
    as drawn, so a big world's far sea is calm to sail too.
  - Console fixes. `bc clouds` no longer removes the pins on the map, and `bc hide` with no prefix
    removes only the pins `bc show` and `bc bosses` placed; both used to remove every pin, the player's
    own too. `bc savepreset` with no name saves under the world's name (it saved a preset with no name).
    A height layer's `op` sets its opacity (it set its threshold). Adding the first height layer to a
    world with no heightmap, or deleting the last one, shows at once (it waited for the next change).

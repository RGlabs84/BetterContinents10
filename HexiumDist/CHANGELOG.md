- v0.9.0
  - Adds world export: it dumps the loaded world, vanilla or Better Continents, into Better
    Continents maps that rebuild it. That is a 16-bit heightmap sampled the way the game builds
    its terrain, plus biome, location, forest, heat, alt-biome, lava and moss maps with their
    legends, and an export.cfg with the settings that load them. Heights are encoded for
    Heightmap Amount 2 and Sea Level 0.5 by default (-30 m to 370 m, waterline 0.15).
  - The export samples on worker threads and writes its PNGs on a background task, so the game
    keeps running. It writes to BetterContinents/<world>/export-<time>/ beside worlds_local,
    never into the world's save folder, and a cancelled or failed export deletes its partial
    files.
  - Adds the "bc export run/status/cancel" console commands for the host, and "bc_export" for
    every player the server allows. "bc_export server" asks a dedicated server to export its own
    world (admins only).
  - Adds an export HUD modelled on Wubarrk's Eye: a status box (F9) with the progress, the last
    export folder and your map pixel, and a window (F7) with the export options, Start and
    Cancel. The window frees the mouse while it is open. Turn it on with Hud in the new
    "BetterContinents.Export" config section.
  - The Export section applies at once: Better Continents now reads BetterContinents.cfg again
    when the file changes on disk, and Configuration Manager changes apply straight away. Other
    settings are still read only when a world is created.
  - A server sends its Hud and Allow Export values to every Better Continents client, and a
    change reaches players who are already connected. A client connected to an older server, or
    to one without the mod, uses its own values.
  - Fixes the server forgetting a joining client's Better Continents version at the game's own
    handshake, which made a world using Better Continents turn every client away (since 0.7.31).
  - Adds a "Settings Transfer Rate" setting ("07 BetterContinents.Misc": Vanilla, KB256, KB384, KB512 (the
    default), KB768, MB1, MB1_5, MB3 or Unlimited, 100 MB/s). Valheim pins every Steam connection to about
    150 KB/s, so a detailed Better Continents world could take over a minute to reach a joining player; this
    sets that one connection to the chosen rate for the transfer, then restores it. Steam sends at exactly the
    rate set (it has no congestion control), and the transfer itself moves about 4 MB/s at most: with an 11.8 MB
    world a join took 78 s at Vanilla, 23 s at KB512 and 3 s at Unlimited on a local dedicated server. Only the
    value on the machine that sends the settings (the host, or the dedicated server) matters, a client's own
    value has no effect, and the rate cannot be raised at all on PlayFab (crossplay) connections. The log shows
    the rate used and the transfer's size, time and speed.
  - Adds a shareable form of the world cache: "bc_cache export" writes a world's settings as a ".bcworld"
    file (the same data a joining client would otherwise download: 11.8 MB for a 2048 px world, more for
    larger ones) with a ".txt" description beside it; "bc_cache import <file or folder>" puts one into a
    player's local cache before they ever connect, so joining skips that download entirely; "bc_cache
    list" shows what is cached. Better Continents also imports any ".bcworld" it finds under BepInEx's
    plugins folder or in BepInEx/config/BetterContinents/seed/ automatically at startup, so a mod pack
    containing nothing but a ".bcworld" file pre-seeds every player who installs it. An import never
    overwrites an entry, and a file's id is computed from its content, never taken from its name.
  - Adds world import: "bc_import" (any player, in the main menu or in a world) makes a New World
    preset from an export folder, or from any folder of Better Continents maps with or without an
    export.cfg, and selects it, so Start, New World and Create make the world. With no argument it
    takes your newest export; "bc_import list" numbers them, and a number, a world's name or a
    folder picks one. "bc_import ... config" instead copies export.cfg into BetterContinents.cfg,
    keeping the old file beside it, and selects "From Config", for when you keep editing the PNGs.
    "bc import run/list/status" does the same in the host's debug tree.
  - Every export now makes a New World preset "<world> <date> <time>" when it finishes, so it is a
    choice in the New World screen straight away (not selected: pick it). The preset carries every
    map and a 256 px picture of the map, so it keeps working if the folder is moved or deleted.
    "bc_export ... nopreset" (or the HUD) switches it off.
  - An import builds the settings exactly as "From Config" would, but from a copy of the config with
    export.cfg laid over it and on a worker thread, so your config is left alone (except the
    selected preset) and a loaded world is not touched. Making the same export's preset again
    replaces it; a preset of that name made some other way is moved aside, not overwritten.
  - The export HUD window has an Import tab (your exports, newest first, with Make preset, Use as
    config, Open the folder and Copy the folder path) next to the Export tab, which now explains
    every map in a line (and more when you point at it), estimates the size, memory and time, and
    shows what the last export came to: heights and clipped pixels, missing locations, the preset.
    The window scrolls when it is taller than the screen.
  - The paint map is now exported by default, like every other map ("bc_export ... nopaint" leaves
    it out). A Better Continents world's own terrain, vegetation and spawn maps, which no sampled map
    holds, are now copied into the export folder too, so the new world keeps them.
  - README.txt in every export is rewritten for first-timers: the three ways to make a world from
    the export, step by step, what every file is, and how to edit and cut and paste between exports.
    export.cfg starts with a short explanation. The old advice to quit the game before pasting
    export.cfg is gone: the config is read again while the game runs.
  - Fixes preset pictures never showing in the New World screen on Linux (and where the user name
    has a dot), and preset names with a dot being cut short there.
  - The Directory setting's description now lists all 13 map file names it loads.
  - Biome precision works again, now on Valheim 1.0 (it was switched off in 0.7.20). "Biome
    precision" 1 to 5 makes the ground follow the biome borders inside each 64 m terrain zone, on
    (N + 1) x (N + 1) cells down to 11 m, instead of only at the zone's 4 corners: ground textures,
    grass, vegetation and spawn points. 0 stays vanilla. Heights do not change, and "bc b p" changes
    it in a loaded world. An export's export.cfg carries the world's value.
  - Adds BetterContinents-Export-Guide.pdf to the package: "Export & Import, the easy guide", an
    illustrated, step-by-step guide for first-timers to exporting a world, editing the pictures,
    cutting and pasting between worlds, Biome precision and making new worlds from an export, with
    a table of what can go wrong and how to fix it (GPL-3.0, its source attached inside the PDF).

  - The package now carries three PDFs: the Better Continents Guide, "Export & Import, the easy guide", and
    "Map-making: a skill for AI agents" (BetterContinents-AI-Skill.pdf), a stand-alone document that teaches an
    AI agent how to author, check, export and import Better Continents maps; its toolkit is attached inside.  - Known issue: the location map's grid is corner-based, unlike every other map's, so rotating or flipping
    an export's PNGs in an editor shifts every location by one pixel (10 m at 2048 px). Two locations can
    then land in the same 64 m zone, and the game keeps only the first: 487 of 11,817 were lost in a test
    world rotated by 180 degrees. Cut-and-paste and hand edits are not affected. To be fixed in a later version.

- v0.8.1
  - Adds alt-biome planting: an alt-biome map (altbiomemap.png) plants Valheim's alt biomes with
    colours, the way the biome map plants biomes. Its legend (altbiomemap.txt, written with a
    default colour per alt biome when missing) says which colour plants what, and "at x, z" lines
    plant the region under a point. Every planted area becomes a region of its own.
  - Planted alt biomes count toward the game's per-alt-biome maximums when it places the rest at
    random.
  - Adds per-world alt-biome options in the new "BetterContinents.AltBiomes" config section: mode
    (Random, PlantedOnly, Off), grid, placement seed, chance, amount, region size and distance
    scales, region filters and per-alt-biome overrides. They are baked into each new world.
  - New worlds stop the alt-biome grid at the edge of the world when it is inside 10500 m.
  - Adds the "bc ab" and "bc reload ab" console commands, and "bc_altbiomes" for every player, to
    inspect, export and change alt-biome placement.
  - Clients use the server's alt-biome placement, and both sides log whether they agree.
  - Fixes an index out of range error on Expand World Size worlds smaller than 10500 m, and wrong
    alt-biome regions on larger ones.
  - Fixes Deep North weather on worlds with a biome map following the camera height instead of
    the map.
  - Fixes land beyond 10500 m on worlds with the map edge drop-off disabled taking the ocean's
    biome for terrain, vegetation and spawns.
  - Fixes locations that accept several biomes searching the wrong biome, or one the map lacks.
  - Fixes debug-mode map edits not reaching alt-biome regions and chunk biomes.
  - Guards the game's biome data cache with a fingerprint, so a cache built for other settings is
    never reused. Valheim 1.0.15 itself no longer reads that cache.
  - Worlds that use none of the new alt-biome features are saved exactly as 0.8.0 saves them.
    Worlds that do need 0.8.1 or later: 0.8.0 loads them without Better Continents.

- v0.8.0
  - Updates the mod for Valheim 1.0.15.
  - Saves world settings in the new 1.0 chunked save format, inside the world's own folder.
  - Adds an upgrade path: a pre-1.0 world's settings are imported and moved into the world folder
    on its first save, and the old file is kept as "<world>.BetterContinents.pre10".
  - Carries the settings through Valheim's save operations (rename, copy, move), which filter by
    file extension and would otherwise drop or delete them.
  - Fixes the minimap drawing Swamp, Mountain, Plains and Mistlands as Black Forest, and restores
    the Ashlands and Mistlands map shading.
  - Fixes world generation hanging forever when the biome map omits a biome.

- v0.7.31
  - Fixes wrong default name for moss map image file (was mossmmap.png, should be mossmap.png).
  - Fixes manually reloading heat map using forest map file path.
  - Fixes flat map being disabled when rough map blend was zero (should be only disabled when flat map blend is zero).
  - Fixes None biome on biome maps not using the default biome generation.
  - Fixes location pin removing possibly removing a random pin from the map.
  - Minor optimizations.

- v0.7.30
  - Adds spawn map.
  - Adds vegetation map.
  - Changes the "Skip default location placement" config setting to be saved on the map settings.
  - Improves config settings layout.
  - Minor optimizations.
  - Fixes index out of range error when going out of map bounds.
  - Fixes "Disable map drop off" not working.

- v0.7.29
  - Fixes some worlds not working.

- v0.7.28
  - Adds fallback handling for lava when heat map is missing.

- v0.7.27
  - Fixed for the new game version.

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

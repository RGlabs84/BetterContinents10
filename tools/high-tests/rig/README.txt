Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

tools/high-tests/rig: what was read and measured outside the offline suite, so that every number of the high-terrain work can be made again.

Scripts here (run each under ~/valheim-testbed/heavy.sh: UnityPy and numpy over the game's 758 MB bundle or a 16k picture take 1 to 4 GB):
  read_terrainlod.py, read_envman.py, read_followplayer.py, read_sky_objects.py, read_weather_followers.py, read_clutter.py
                      the game's own values out of its asset bundle d59cfac (client and dedicated server): TerrainLod's 2400 m / 10 m / 3 x 3 / 256 m,
                      the fog densities, which sky and weather objects keep their own height, the grass entries' altitude limits
  make_heightmap.py   the synthetic heightmap of the rig world: a ziggurat with plateaus at 1200, 4000, 8000, 15000 and 16100 m, a dome and flat land
  valtima_crop.py     a 4096 px square of VALtima's 16,383 px heightmap around its tallest pixel, at the same 4 m per pixel

The headless rig is ~/valheim-testbed/bc-16k/<name>/ (run.sh, profile/ with BepInEx, Better Continents and the probe, saves/, out/<label>/). The probe
(probe/BCServerProbe/Plugin.cs) is test-only and not in the repository; its switches, set as environment variables of run.sh:
  BCPROBE_HIGH=1 BCPROBE_HIGHPTS=<file of "name x z">   at each point: the generator's height, ZoneSystem.GetGroundHeight, GetGroundData, IsBlocked, the grass's
                      ray (the probe gives the server the client's terrain ray mask), Character.InInterior on the ground, 5000 m up and at 3500 m, the AI's tile height
  BCPROBE_HIGH_CONTROL=1   the same world with the high-terrain patches never switched on (the game as it is without them)
  BCPROBE_TOWNS=<file of zone x, z>   generate those zones as a dedicated server does, and list every object placed (towns-objects.tsv)
  BCPROBE_BOXES=<file of "name x0 z0 x1 z1 n">   n random points in the box: the base height b (GetBaseHeight, which Better Continents replaces), the biome, the
                      terrain height, and per box and biome the terrain minus 200 * (2b - 0.4) (the Mountain biome's formula) and minus 200 * b (boxes.tsv)
  BCPROBE_PERLIN=1   4,000,000 random points of the real Mathf.PerlinNoise and of the Mountain and Mistlands noise terms
The world is made from the rig's profile/BepInEx/config/BetterContinents.cfg (SelectedPreset = From Config) when it does not exist yet.

The runs behind the numbers (2026-10-06; Heightmap Override All as named; rig high-c for the continuation agent's, high for the first agent's):
  hoff81    Amount 81, Override All OFF, the synthetic ziggurat, boxes on each plateau and cliff, Perlin statistics:
            terrain = 200 * (2b - 0.4) + (-0.8 .. 40) m on a plateau, +164 m on the steepest cliff (slope 35); Mathf.PerlinNoise -0.14 .. 1.14
  hoff108   Amount 10.8, Override All OFF, the same map: terrain = 200 * (2b - 0.4) + (-0.7 .. 37) m
  von/vonc/voff   VALtima's real heightmap cropped to 4096 px around its tallest pixel (world of World Size 7692 + Edge Size 500, Amount 10.8), Override All on with
            the patches / on without / off with the patches. Override All on: the terrain is 200 * b exactly (0 m difference at 240,000 random points in boxes of 200 m, 1 km, 4 km and the whole 16 km), the summit
            1,999.4 m, the bound 2,130 m; 25 zones around the summit hold 706 objects with the patches (624 within 6 m of the ground, none higher) and 25 (the zone
            controllers) without, and the grass ray finds the ground at 8 of 8 points with them, at 1 of 8 without (the one at 327 m). Override All off: terrain = 200 * (2b - 0.4) - 0.1 .. 140 m
            (mean 15 m: the tilt term on the map's steep pixels; the most, 140 m, in the 4 km box), the summit 3,929.8 m, the bound 4,270.9 m; 748 objects.
  vfinal    the same world with Override All off once more, with the DLL of the last commit (the rays start 1,000 m over the bound): the same answers, six patch groups
            applied, no "did not find", 748 objects (680 within 6 m of the ground).
  high06, high06late, the final-DLL pairs, the vanilla-height A/B (van06*), dungeons: the first agent's, in ~/valheim-testbed/bc-16k/high and high-r

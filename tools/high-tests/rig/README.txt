Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

tools/high-tests/rig: what was read and measured outside the offline suite (tools/high-tests), and the means of making it again.
Nothing here is built or run by tools/run-tests.sh. Everything heavy runs as a ~/valheim-testbed/heavy.sh job: a dedicated server takes about 3 GB (cap 6G),
UnityPy over the game's 758 MB asset bundle or numpy over a 16k picture 1 to 4 GB (cap 4G). results/ holds what the last runs printed.

READING THE GAME AND THE MAPS (Python 3; UnityPy for the bundle, numpy and Pillow for pictures: the author's environments are ~/upy and ~/.venvs/numpy)
  read_terrainlod.py, read_envman.py, read_followplayer.py, read_sky_objects.py, read_sky_ranges.py, read_weather_followers.py, read_clutter.py
                      the game's own values out of its asset bundle d59cfac (client and dedicated server): TerrainLod's 2400 m / 10 m / 3 x 3 / 256 m, the fog
                      densities and the distances they hide the land at (plain exponential fog, as the game's shaders have it), which sky and weather objects keep
                      their own height, the grass entries' altitude limits
  make_heightmap.py   the synthetic heightmap of the rig world, maps/high.png: a ziggurat with plateaus at 1200, 4000, 8000, 15000 and 16100 m, a dome and flat land
  valtima_crop.py     a 4096 px square of VALtima's 16,383 px heightmap around its tallest pixel, at the same 4 m per pixel, and the slopes of that crop
  results/            read_envman.txt and valtima_crop.txt hold what those two printed in their last run

THE HEADLESS SERVER RIG
  The rig is ~/valheim-testbed/bc-16k/<name>/, made from ~/valheim-testbed/bc-16k/template (its profile/, fake-home.sh and port file) with these files added:
    run.sh          one server run: boots the world $WORLD from profile/ (BepInEx, Better Continents, the probe) with saves/ of its own; output in out/<label>/
    hf_run.sh       one run with the config it needs: hf_run.sh <label> <world> <seed> <dll> <amount> <mode> <heightmap file | none> [VAR=value ...]. Builds the
                    probe, puts <dll> in the profile, writes World Size 8000, Edge Size 500, Heightmap Override All on and the amount, High Terrain mode and map it
                    is given, and runs run.sh with the VAR=value pairs in the probe's environment. A world that does not exist is made from that config; one that
                    does is loaded as it is
    probe/          BCServerProbe, a test-only BepInEx plugin: one merged copy of what the three rig copies of the first work had
    maps/           the lists of points, zones and boxes of the runs; high.png is made by make_heightmap.py
    final-runs.sh   the runs behind the report, in steps (below); compare_runs.py compares what they wrote, and can be run again over the output alone
  The probe's switches, set as environment variables of run.sh (hf_run.sh passes VAR=value pairs on):
    BCPROBE_HIGH=1 BCPROBE_HIGHPTS=<file of "name x z">   at each point: the generator's height, ZoneSystem.GetGroundHeight, GetGroundData, IsBlocked, the grass's ray
                    (the probe gives the server the client's terrain ray mask), Character.InInterior on the ground, 5000 m up and at 3500 m, the AI's tile height
    BCPROBE_HIGH_CONTROL=1   the same world with the high-terrain patches never switched on (the game as it is without them)
    BCPROBE_HIGH_TOGGLE=1    after everything else: the same table again after `bc h ht off`, `bc h ht on` and `bc h ht <the world's own mode>` typed through the game's
                    own console table (DebugUtils.RunConsoleCommand), with the state of the six groups logged each time (high-rays-off.tsv, high-rays-on.tsv, ...)
    BCPROBE_DUNGEONS=N  N locations that have an interior, the highest first, plus a low and a middle one: each zone is generated as a dedicated server generates it,
                    and every object in it is asked Character.InInterior, as is a point at the entrance (dungeons.tsv)
    BCPROBE_TOWNS=<file of zone x, z>   generate those zones as a dedicated server does, and list every object placed (towns-objects.tsv)
    BCPROBE_POINTS=<file of "pt name x z">   the height and biome the generator gives at each point (points-out.tsv)
    BCPROBE_BOXES=<file of "name x0 z0 x1 z1 n">   n random points in the box: the base height b (GetBaseHeight, which Better Continents replaces), the biome, the
                    terrain height, and per box and biome the terrain minus 200 * (2b - 0.4) (the Mountain biome's formula) and minus 200 * b (boxes.tsv)
    BCPROBE_PERLIN=1   4,000,000 random points of the real Mathf.PerlinNoise and of the Mountain and Mistlands noise terms
    BCPROBE_HIGH_LATE, BCPROBE_INLINE, BCPROBE_DUMPDATA, BCPROBE_PREFABS, BCPROBE_TOWNS_TIMEOUT: see the comments in Plugin.cs
  What the rig cannot tell: on a dedicated server the terrain of the zones around the players is not loaded, so the ground below a point that Character.InInterior
  asks for (HighTerrain.GroundBelow) comes from the generator, its fallback; the branch that reads the loaded terrain is a client's, and nothing here runs it. The
  dungeon run classifies the objects with that same generator, so that "inside = more than 3,000 m above the ground" holds there by the way it is made; what it shows
  on its own is that the patched rule says "inside" for every object 4.9 km up, and "not inside" for the ground-level objects and the entrances, which the game's own
  rule would call inside on ground above 3,000 m.

THE RUNS BEHIND THE REPORT (2026-10-07; one DLL for all of them; final-runs.sh, out/<prefix>-final-runs.txt in the rig, results/final-runs.txt here; port 2503)
  step high      Heightmap Amount 81, High Terrain Auto, the ziggurat (World Size 8000 + Edge Size 500, Heightmap Override All on, seed high06): the 14 points; then
                 `bc h ht off`, `on` and `Auto` typed through the console table; 7 dungeons; and a world made with High Terrain Off
  step vanilla   Heightmap Amount 2 (-30 m to 370 m), the same map, seed van06: the 0.10.2 build and this build in Auto, and this build with High Terrain On
  step none      no heightmap, Heightmap Amount 1: High Terrain On and Auto
  step noise     the world of step none made again, twice in Auto and once in On, to see how much two runs of one mode differ
  What they showed (results/final-runs.txt, the tables in results/, md5 sums of every file compared in results/hfz-md5sums.txt; DLL BetterContinents-hfx2.dll,
  md5 0e2c3ed3363b755b55411832057d75eb, the 0.10.2 build b0618199007facd6510989e0aeb459a8):
    Amount 81, Auto: the ground is found at all 14 points, the three above 6,000 m among them (7,999.9 m, 15,000.0 m, 16,100.0 m): GetGroundHeight, GetGroundData,
      the grass's ray; nothing is blocked; Character.InInterior is false on the ground at all 14 and true in a dungeon 5,000 m up at all 14; the AI's tile is
      centred about 2,500 m over the ground where the ground is over 400 m (the ground at the tile's own centre). Six groups applied, and none logged "did not find".
    `bc h ht off`: the six groups are removed, and the table is the game's own: GetGroundHeight finds nothing at the three points over 6,000 m, InInterior is true
      on the ground at the five points over 3,000 m (4,000 m twice, 8,000, 15,000, 16,100), the grass is found at 3 of 14 points (the low ones), the tile is centred on
      2,500 m everywhere. `on` and `Auto` after it: the first table again, byte for byte. A world made with High Terrain Off has the same table as the Auto world
      after `bc h ht off`.
    7 dungeons (entrances at 272 m, 8,000 m three times, 15,000 m twice, 16,100 m): 2,408 objects, 2,085 of them 4.9 to 5.06 km over the ground and all 2,085
      InInterior true; the other 323 (the ground and the entrance) false; every entrance false.
    Amount 2: the 0.10.2 build and this build in Auto: the terrain and chunks of the towns' zones, every location, the point table and all 1,093 objects identical,
      in the same order. High Terrain On in that world (patches applied; the land cannot pass 370 m, the ground is looked for from 1,370 m): the same files and
      the same objects (one zone controller listed in another place), the same point table.
    No heightmap, Amount 1, High Terrain On (the land cannot pass 1,908 m, the ground is looked for from 2,908 m): the terrain and chunks, the locations and the
      point table are those of the Auto world. Four of the 27 zones that hold objects (a Mistlands town, a wooden ruin, two more) lay their objects out differently from run to run in
      both modes: five runs, three Auto and two On, show the same four zones changing and the other 23 identical in all of them.
  Zone objects are compared as the probe lists them (towns-objects.tsv) and the rest byte for byte. The procedural locations of a world (a Mistlands town, a wooden
  ruin, a dungeon) lay themselves out from the game's random state, which the rest of the running server also draws on: two runs of the very same build, mode and
  world can differ in those zones, and the number of objects in a dungeon varies by one or two. The comparison therefore says which zones differ, and the noise step
  and the earlier run (hfx) show how far apart two runs of one mode are.

THE MUTATION CHECK
  mutants.py <scratch folder> [--keep] [--no-baseline] [mutant ...]   one line of the production code changed at a time (the wiring of the patches, the guards of the
  transpilers, the rules, the saved key, the console), in a scratch copy of the repository: the suites that should notice it are run, and a mutant they let through is
  a line no test pins. results/mutants.txt is the report; `mutants.py --list` names the mutants. 26 of the 27 were killed on the first run; the one that survived
  (the Deep North transpiler saying what it rewrote at every re-patch) got a test, DeepNorthLogTests, and is killed.

THE FIRST WORK (2026-10-06; rigs high, high-c and high-r, not in the repository)
  hoff81    Amount 81, Override All OFF, the synthetic ziggurat, boxes on each plateau and cliff, Perlin statistics:
            terrain = 200 * (2b - 0.4) + (-0.8 .. 40) m on a plateau, +164 m on the steepest cliff (slope 35); Mathf.PerlinNoise -0.14 .. 1.14
  hoff108   Amount 10.8, Override All OFF, the same map: terrain = 200 * (2b - 0.4) + (-0.7 .. 37) m
  von/vonc/voff   VALtima's real heightmap cropped to 4096 px around its tallest pixel (world of World Size 7692 + Edge Size 500, Amount 10.8), Override All on with
            the patches / on without / off with the patches. Override All on: the terrain is 200 * b exactly (0 m difference at 240,000 random points in boxes of
            200 m, 1 km, 4 km and the whole 16 km), the summit 1,999.4 m, the bound 2,130 m; 25 zones around the summit hold 706 objects with the patches (624 within
            6 m of the ground, none higher) and 25 (the zone controllers) without, and the grass ray finds the ground at 8 of 8 points with them, at 1 of 8 without
            (the one at 327 m). Override All off: terrain = 200 * (2b - 0.4) - 0.1 .. 140 m (mean 15 m: the tilt term on the map's steep pixels; the most, 140 m, in
            the 4 km box), the summit 3,929.8 m, the bound 4,270.9 m; 748 objects.
  vfinal    the same world with Override All off once more, with the DLL of the last commit of that work (the rays start 1,000 m over the bound): the same answers,
            six patch groups applied, no "did not find", 748 objects (680 within 6 m of the ground).
  Its Amount 81 runs and its vanilla-height A/B used older DLLs than its last commit (the review of it found that): all of them are made again, with one DLL, in the
  runs above.

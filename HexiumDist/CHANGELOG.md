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
    Consumables in the file are placed once as the game's own objects. `bc_bake export` writes the layer
    out.
  - Players download the layer when they join (a few MB for a whole town file, kept in the cache for the
    next time), and a change made during play reaches them as a small patch before any piece is taken
    away.
  - New settings in [10 BetterContinents.BakedPlacements], each player's own and applied at once: Draw
    Distance, Draw Scale, Detail Scale, Shadows, Light Distance, Light Count (how many baked lights
    burn: the nearest 24 by default; the rest are drawn unlit), Light Shadows (how many of them cast
    shadows: 4), Seat Distance, Tints and Diagnostics.
  - A world with baked pieces needs 0.10.4 or later: an older Better Continents, or the game alone,
    opens it without its layer, and its baked pieces are neither drawn nor solid there until 0.10.4
    opens it again.
  - New guide: Baked buildings, the easy guide (BetterContinents-Baking-Guide.pdf, GPL-3.0), in this
    package.
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

- v0.10.2
  - Forest Scale is 0.5 by default, the game's own size of forest and clearing patches. Since 0.7.20 the
    default was 1, which Better Continents turns into patches about five times the game's size, so a new
    world made from an untouched config had far larger forests and clearings than vanilla. The values
    mean what they always did: 0 gives patches about a third of the game's size, 0.5 the game's own, 1
    about five times larger. Keep it within 0 to 1: near 1.15 the game's own forest is the same
    everywhere, and above that the patches shrink again. Its description in BetterContinents.cfg says so.
  - A BetterContinents.cfg that holds Forest Scale = 1, as every config written by 0.7.20 to 0.10.1 does
    unless somebody changed it, is set to 0.5 once, the first time the game starts with this version, and
    the log says so ([07 BetterContinents.Misc] Config Version becomes 3). A 1 written there again
    afterwards is kept, and any other value stays as it is.
  - Only new worlds read Forest Scale. Every existing world and preset keeps the forest it was made with,
    and a world made from an export folder takes the Forest Scale in the export's export.cfg, as before.
  - The Better Continents Guide's forest chapter and "Map-making: a skill for AI agents" describe the
    default, and say what black means in a forestmap: with the default Forestmap Multiply 1 and Add 1,
    black is a clearing; with Forestmap Multiply 0 and Add 1, black leaves the game's own forest and the
    map only adds trees.
- v0.10.1
  - Rebuilt against Valheim 1.0.17 and re-verified: the reference check passes against the 1.0.17
    client and dedicated server, every offline test suite passes, and a world made from image maps on a
    1.0.17 dedicated server comes out the same, height for height and pin for pin, as under 1.0.16.
    Nothing that Better Continents patches changed in 1.0.17.
  - The New World screen's Better Continents panel shows the mod's own emblem, the package icon, for
    "From Config" and for a preset without a picture of its own. It still showed the original 2021
    pictures, a gear beside "BC" and "BC" over a map.
- v0.10.0
  - Every existing world and preset keeps its terrain, biomes and locations exactly as in 0.9.4, and a
    world is saved byte for byte as before. What else changes for them is listed under "For every
    world". Everything under "For new worlds only" belongs to new worlds, those of settings version
    12, which is what 0.10 gives a world it creates, and never reaches a world marked with an older
    version. A preset keeps the version it was saved with, so it makes the world it always made: the
    four presets that ship with the mod are version 7 and make version 11 worlds. A new world made
    from map files of your own is a version 12 world, which reads them the 0.10 way; setting
    [00 BetterContinents.Debug] Override version to 11 before you make it gives a version 11 world,
    which gets none of the new-world changes, as 0.9.4 would have made it. While Override version is
    set it is also the version every world is saved in and sent to joining players, so leave it empty.
    The README's "Upgrading from 0.9" section sets out which worlds get what.
  - A world made from an export folder (the export's own preset, "bc_import", or a Directory with an
    export.cfg) is version 12 when the export's maps span what its own World Size and Edge Size give,
    which an export of a version 12 world and a 0.9.x export of a world of vanilla's size do. Otherwise
    (an export of a resized 0.9.x world, or one made under Expand World Size at another size) it is
    version 11: its maps span 21,000 m, as a version 11 world's do (an export made under Expand World
    Size is rebuilt as it was only with Expand World Size installed at that size), and it gets none of
    the new-world changes.
  - Play a version 12 world with 0.10. Better Continents 0.9 still opens it, but as an older world (its
    maps span 21,000 m whatever its World Size says, for one), and its next save writes the world as
    version 11, so 0.10 then treats it as made by 0.9. It cannot read a world made with the experimental
    Compact Maps below: it generates that world's terrain as vanilla and leaves the world's settings file
    alone. It refuses a .bcworld file of a version 12 world as a bad header. On a world that uses Better
    Continents the server and every player must run exactly the same version, so update them together.
  - The README, ALTBIOMES.md and the three guides in the package (the Better Continents Guide, "Export &
    Import, the easy guide" and "Map-making: a skill for AI agents") describe 0.10: the guides are rebuilt
    as the Valheim 1.0 / Better Continents 0.10.x editions.
  - For every world
    - Better Continents regenerates the zones itself, after each change made with the "bc" commands or
      the settings window and on "bc regen". It resets the world's generated zones on the machine that
      runs the world (single player or the host), in a world that uses Better Continents: in a world
      without it, which is the game's own, the zones are left alone and the console says so. This comes
      with the "bc" commands, not with Debug Mode: they need cheats, which Debug Mode turns on at spawn
      (or type devcommands). The "bc" command is registered for the host only; the Server
      Devcommands mod lets any player with devcommands type it on their own machine, but on a joined
      player's machine a change alters only that player's copy of the settings and their own view of the
      ground, never the world or the other players, and the regeneration refuses there ("it runs on the
      machine that runs the world"). Make "bc" changes on the host or in single player. The work is
      spread over frames, with a start line, progress lines and a finish line in the console, and a new
      change or a new "bc regen" while it runs starts it over, after the zone it was in is finished.
      Each reset zone generates again with the new settings, locations included, when someone comes near.
      Players, what players built, tombstones and tamed animals are never removed, and a creature that a
      creature spawner made goes with its spawner unless it is tamed or stands in a zone that is left
      alone. If an error stops the work, the zones it had emptied and left waiting for their neighbours
      are finished, so they generate again when someone comes near, and the zones it had not reached are
      as they were: "bc regen" resets them.
    - What zone regeneration leaves alone. Every generated zone is reset except zones within one zone
      (3 x 3) of something a player built, a tombstone, ground a player has worked (fields, paths, roads,
      levelled or dug ground), or you while you are inside a dungeon; the ground around your own character
      on the surface is regenerated too. A player connected from another machine keeps the zones their
      game has loaded around them (their simulation distance: the 5 x 5 block by default, never less than
      the 3 x 3), checked as the work goes, so a player who moves keeps the zones around where they are
      now; something you build while it runs keeps its zone from then on. A location that reaches into
      more than one zone is kept or regenerated whole: if one of its zones stays, the others stay with it,
      and the parts of a location that stand in a zone nobody has generated yet are cleared with it, so
      regenerating again does not leave a second copy. Crops are kept with their field. A tree a player
      grew from a sapling counts as the world's, so it goes unless something nearby keeps its zone, and a
      zone that was already reset is not brought back when something is built beside it later. A location
      whose home zone the game generates while the work is running (someone came near it) can end up half
      placed, or placed twice; "bc regen" again sets it right.
    - The ground under what it leaves alone. The ground of every zone, the zones it keeps included, is
      built from the world's settings, so after a change it follows the new settings; the edits players
      made with the hoe, the cultivator or the pickaxe are stored as changes to the ground and ride on the
      new ground. A change that lowers the ground under a base leaves its pieces standing in the air, and
      the game breaks a piece that has lost its support a few seconds later (in a test, a workbench and a
      floor left about 3 m up were gone 8 seconds later); a change that raises the ground buries them,
      and trees and rocks in kept zones float or sink the same way. Make big height changes before
      building, or away from what you want to keep. "bc regen" on its own changes no ground.
    - Zone regeneration and saves. A zone is never saved half emptied, and a location is never saved half
      cleared: if the world is saved while the regeneration is going, the zones it has emptied are saved
      as not generated and generate again when someone comes near. A reset zone also waits to generate
      again until the zones around it that are being reset have been cleared, so a location that reaches
      over a zone edge is built once, whole. Where a zone that stays meets a zone that was reset, the
      height edits along the shared edge are cleared on the side that stays (the paint stays), so that
      the two grounds meet; this follows the zones actually reset, also when the regeneration is cut short.
    - "bc regen" does what a change does to the zones, on demand, and leaves the terrain that is loaded
      as it is: nothing changed, so the ground already matches the settings, and the game does not stall
      when it starts. It does not redraw the minimap or rebuild the alt-biome grid either, which "bc
      reset" still does before it. In a world without Better Continents it answers "Zone regeneration:
      this world does not use Better Continents." at once and rebuilds nothing.
    - A change made with the "bc" commands or the settings window (any setting, "bc reset", an alt-biome
      change, a map reload, Biome precision) rebuilds the loaded terrain once, the distant terrain and the
      grass too, and then regenerates the zones. That stalls the game for a second or two (1.6 s
      measured), as the rebuild after a change did in 0.9.4; the regeneration work itself is spread over
      frames.
    - The whole ground follows a change at once. After a "bc" change or "bc reset", the distant terrain,
      the grass and a zone that is loading at that moment take the new settings straight away; before,
      they could keep the old shape until the game redrew them or the zone loaded again.
    - "bc h alpha", "bc reload lm" and "bc reload terrain" read a map by the loaded world's own rule. "bc
      h alpha" decodes the loaded heightmap again at once, in every world: a version 11 world re-reads it
      the old way (8-bit grey, the alpha unused) with Heightmap Alpha on, and as 16-bit grey with it off;
      before, a change did nothing until the world was loaded again. "bc reload lm" reads the location
      map's pins exactly, and "bc reload terrain" reads the terrain legend's Expand World Data ground
      names, on a version 12 world; a version 11 world reads both as it always did.
    - [00 BetterContinents.Debug] Debug Reset Command, which held the console command that regenerated
      the zones (zones_reset start), is empty by default now: Better Continents regenerates the zones
      itself. A console command written there runs instead, after each change and on "bc regen" (for
      example zones_reset start), and a BetterContinents.cfg that still holds the old default is set to
      empty once, with a line in the log that says how to go back. The hidden [07 BetterContinents.Misc]
      Config Version records it, so a zones_reset start you write afterwards stays.
    - [00 BetterContinents.Debug] Debug Mode's description says what it does. The old one, "Automatically
      reveals the full map on respawn, enables cheat mode, and debug mode", named a map reveal that Debug
      Mode does not do, and did not in 0.9.x either. In a world that uses Better Continents, on the
      machine that runs the world (single player or the host), it turns devcommands on when you spawn,
      shows "Better Continents Debug Mode Enabled!" at the top left, and adds a Better Continents button
      to the Esc menu and the large map that opens the settings window (Alt+F8 does the same while the Esc
      menu or the large map is open, and does nothing elsewhere). What reveals the whole map is a "bc"
      change: most of them redraw the minimap and explore all of it.
    - Water follows its ground. Each 64 m zone's water works out how deep the sea is under it once, when
      the zone loads, so ground rebuilt in place afterwards (a "bc" change, "bc reset", or a terrain edit
      that loads after its zone) left a square of sea with straight edges looking deep among
      shallow-looking water, or the other way round. Now each zone's water takes the depth of its rebuilt
      ground. Worlds without Better Continents are untouched.
    - Vegetation is no longer placed twice. The game fills a zone with trees, bushes, rocks and the like
      from the world seed, the zone and each prefab's name, so a zone filled a second time over its own
      objects (a command that fills zones again, or a save put back together from mismatched files) draws
      every spot again and doubles everything in it: "a clone of everything within inches or 1 m", as a
      player put it. Now a plant is not placed within 1 m of the same kind of object already standing in
      its zone, or placed there by another vegetation entry in the same pass, in every world made with
      Better Continents, old ones included. It changes vegetation only, never terrain, and only in zones
      generated from now on: zones that are already generated keep what they have. One entry never blocks
      itself, so vanilla's densities and groups stay for every prefab with one entry, except beside an
      object of that prefab that already stands in its zone (one of a location placed just before, or of
      an earlier fill); where two entries place the same prefab (for example a vegetation map's, or an alt
      biome's added vegetation) a second plant within 1 m of the first is dropped, so a zone generated
      from now on can hold fewer plants than in 0.9.4.
    - Adds "bc_twins" (any player; "bc twins" in the host's bc tree) to find and remove the doubled
      vegetation a world already has. "bc_twins [radius]" counts twins by prefab and zone: exact twins
      (two copies on one spot) and near ones (within the radius, 1 m unless given, 0.1 to 5 m). "bc_twins
      remove [radius]" shows what a cleanup would delete and "bc_twins remove confirm [radius]" deletes
      it, 1000 objects a frame: the copy nearest the ground stays, nothing a player built is ever removed,
      crops players grow go only when they stand on one spot, and without a radius only exact twins go.
      Removal runs where the world is: single player, the host, or a dedicated server ("bc_twins server
      ...", admins only). Back the world up first. If another mod has changed the game's vegetation code
      so that the guard cannot be installed, the log says so (the vegetation map is off then too) and the
      rest of Better Continents still works.
    - Maps take far less memory. Better Continents now holds the float maps (height, rough, forest, heat,
      lava and moss), the biome map, the spawn and vegetation maps and the paint and terrain maps in tiles
      of 128 x 128 pixels, and a tile of one value (open sea, the empty canvas around a painted area) is
      held as that one value. Every value reads exactly as before, and as fast. Measured on a real set of
      10,501 px maps (a heightmap, a forest map and a biome map), a world holds about 99 MB where 0.9.4
      held about 1.32 GB. The world file and what is sent to joining players are unchanged.
    - Heightmap Amount is 1 everywhere. [02 BetterContinents.Heightmap] Heightmap Amount and the bc
      console already used 1; [09 BetterContinents.Export] Default Heightmap Amount, which was 2, is 1 now
      too. With Sea Level 0.5 an export's heights then span -30 m to 170 m on the game's height scale,
      where the sea is at 30 m (60 m below the water to 140 m above it), and clip higher ground (the export
      says how many pixels); "amount=2" (or Default Heightmap Amount 2) keeps the old encoding of -30 m to
      370 m (up to 340 m above the water), which keeps every vanilla mountain. A BetterContinents.cfg that
      still holds the old default of 2 is set to 1 once, and the log says so; the hidden
      [07 BetterContinents.Misc] Config Version records it, so a 2 you choose afterwards stays.
    - A world export's heightmap.png now records the Heightmap Amount and Sea Level Adjustment its heights
      are encoded for, in a PNG text chunk that image editors show and often keep. A new world that reads
      a heightmap with such a record at other settings says so in the log, and "bc info" shows the record.
      Nothing changes by itself, and a heightmap without a record (every existing one) reads exactly as
      before.
    - A world export, with Expand World Data installed, treats an added biome whose terrain is the
      Ashlands or the Mistlands as that ground for the lava and moss maps, so rebuilding the world paints
      them again. Worlds without such biomes export as before.
    - [05 BetterContinents.StartPosition] Start Position X and Y accept -1,000,000 to 1,000,000 m (the
      config stopped at +-10,500 and the console's x and y at 0 to 1), for worlds bigger than vanilla's.
    - When a location cannot be placed because its zone already holds one (the game keeps one location per
      64 m zone and drops a second with only "Location already exist in zone"), the log now says which: an
      error when the start position override was refused, a warning for a pin of the location map. Before,
      it claimed the position was "overriden".
    - The edge of the world keeps to each world's own size within one game session. Before, a world loaded
      after another one of a different World Size kept the first world's edge (the ship's push back, the
      kill zone and the water's edge), and with [01 BetterContinents.Global] Map Edge Drop-off off another
      mod's patching of the same game code could put the edge back at World Size.
    - When Expand World Size's World Stretch is not 1, the log warns once, in any Better Continents world,
      that it magnifies the maps so that only their centre shows: keep it at 1.
    - Console tools answer where they were asked. "bc_export", "bc_cache", "bc_import", "bc_altbiomes" and
      "bc_twins" share one set of plumbing: what an admin sends to a dedicated server ("bc_export server
      ...", "bc_twins server ...") now answers in the admin's own console as well as in the server's log,
      and a server export tells the admin when it ends. "bc cache" and "bc twins" join "bc export", "bc
      import" and "bc ab" in the host's bc tree. The group descriptions that said "get more info with 'bc
      param X help'" now name their own group ("bc h help"), and "help" on a group lists it instead of
      first printing "argument help is not recognized".
    - The bc console's defaults and ranges now come from the same place as the config's, so they agree:
      Heightmap Override All, Forest Scale, Forestmap Add and Start Position X and Y had drifted.
    - Fix Water Color and Ocean Channels are no longer in BetterContinents.cfg, and the "bc g
      fixwatercolor" command is gone. The first was never implemented and the second is read only by the
      oldest height formulas (settings formats 1 to 6); worlds that saved them still load them.
    - [06 BetterContinents.Maps] Spawnmap File is the creature spawn map, and Better Continents no longer
      treats it as the old name of the location map. Before, a BetterContinents.cfg with a Spawnmap File
      and no Locationmap File had the Spawnmap File moved into Locationmap File when the game started, and
      a missing locationmap.png with a spawnmap.png beside it had the spawn map renamed to
      locationmap.png.
    - A new BetterContinents.cfg selects the preset "Disabled". Its old default, "Vanilla", named no
      preset: a new world was made without Better Continents and logged an error saying so. An existing
      BetterContinents.cfg that holds "Vanilla" keeps it and still logs that error. The map-file settings'
      descriptions say what each file is and that Directory wins, and none refers to Nexusmods any more.
  - For new worlds only
    - World Size lays out the whole world. Until now a world's maps always spanned vanilla's 21,000 m and
      World Size only moved the game's edge (the kill zone, the ship's push back and the water's edge), so
      a World Size 20000 world had its land end at 10,500 m, ringed by ocean out to its edge. On a new
      world the maps span World Size + Edge Size in each direction, the land drops away over the edge, and
      the rest of the world is laid out to the size too: the alt-biome grid (12 m points out to the edge,
      so the locations reach the whole world too) and the minimap's pixels (its texture keeps its size)
      follow World Size + Edge Size; where locations are searched for and how far from the centre each may
      be, the game's own biome bands, its Ashlands and Deep North rings with their gaps and the Deep
      North's calm sea, and the area lakes and stream sources are searched for in follow World Size.
      Terrain detail (hills, rocks, noise) keeps its size. [08 BetterContinents.AltBiomes] Grid has no
      effect on such a world, and "bc info" says when a world is laid out to its size.
    - A bigger world builds a bigger alt-biome grid each time it loads, on the server and on every client
      as it joins: slower, and about 0.2 GB at a radius of 16,350 m. Valheim builds the grid for every
      world, so [08 BetterContinents.AltBiomes] Mode = Off means no alt biomes, not no grid. The practical
      maximum is a World Size + Edge Size of 16,350 m: the game files every object beyond about 16.35 km
      from the centre under one shared sector.
    - With Expand World Size installed, it lays out the world as before (its size, grid, minimap and
      locations) and Better Continents leaves the layout to it. Install it on every machine that plays the
      world or on none, and do not add it to a world Better Continents laid out. Its World Stretch
      magnifies Better Continents' maps, so that only their centre shows, and it should be 1.
    - [02 BetterContinents.Heightmap] Heightmap Alpha now blends. Before, a heightmap with it on was read
      as 8-bit grey (heights in 256 steps) and its 8-bit alpha was never used. A new world reads 16-bit
      grey with a 16-bit alpha, and the alpha blends the heightmap with the game's own terrain: where a
      pixel is opaque the heightmap alone decides, where it is transparent the game's terrain shows. A
      version 11 world keeps the old reading with it on: 8 bits, the alpha unused (with it off every world
      reads 16-bit grey).
    - Expand World Data's rules reach the maps. Its altitude rules (a biome's or territory's altitude
      multiplier and delta, water depth, height limits and lava dip) apply over the heightmap's heights as
      they do over the game's own; before, Better Continents' heights replaced them everywhere but where
      the rough map is white. On a world with a biome map and no heat map (or Heatmap Scale 0), its lava
      biomes ("lava: true" in its yaml) are hot where the biome map puts them, so lava burns there; before,
      only the Ashlands were hot on a biome map. With a heat map the heat decides. The terrain map's
      legend (terrainmap.txt) may name one of its biomes for a ground colour ("DeadWastes: 8B4513"), and
      Biome precision keeps its territories' ground colours instead of replacing them. A new world also
      saves the names of the biomes its biome map adds and follows them when Expand World Data numbers its
      biomes differently (it numbers them in its yaml's order, and a world stored the number): a biome it
      no longer has reads as None, where before it read as whichever biome held its number.
    - [03 BetterContinents.Biomemap] Biome precision goes up to 31: cells of 2 m in each 64 m terrain
      zone. A version 11 world stops at 5.
    - The location map's pins land exactly on their pixel, where the height and biome maps put it (before,
      at the pixel's corner, up to a pixel to the south-west). A pin of several pixels is placed on the
      one nearest its middle, and locations that share a colour are dealt out the same way every time, so
      one set of maps gives one world. Maps from a world export are still read where the export wrote
      them. The start position override is placed before the pins, so a pin in its zone can no longer take
      it.
    - [07 BetterContinents.Misc] Compact Maps (EXPERIMENTAL, off by default). On, a world made while it is
      on keeps its maps compressed in memory, decodes each tile when it is first read, and saves and sends
      its maps compressed. On the same 10,501 px maps as above the world file shrinks from 126 MB to 10.5
      MB and a loaded world holds about 21 MB. It is read only when a world is made (From Config,
      "bc_import" or a Directory's export.cfg), and a world keeps it for good: Better Continents 0.9
      cannot read such a world. [07 BetterContinents.Misc] Map Memory (default 512 MB, 64 to 65536) is how
      much memory this machine lets the decoded tiles of those worlds take. Past it the tiles read least
      recently are dropped and decoded again when read again. It applies at once and does nothing for any
      other world.
- v0.9.4
  - A world export loads correctly when the Directory setting points at it. Setting only Directory to an
    export folder and creating the world with "From Config" loaded the maps but not the settings in the
    folder's export.cfg, so the world kept BetterContinents.cfg's own: at the default Heightmap Amount of 1
    the heightmap, stored for Heightmap Amount 2, put the waterline twice as high and a vanilla world came
    back almost all ocean (reported from the Valheim Worlds discord). The forest came out wrong too, and the
    game placed its own locations and random alt biomes beside the exported ones.
  - Now a new world made with "From Config" takes the export.cfg of the Directory along with its maps, the
    way a preset import does. Its settings win over BetterContinents.cfg for the keys it lists: edit
    export.cfg, or delete it, to use your own. Directory stays where BetterContinents.cfg points, so a moved
    export still loads, and BetterContinents.cfg itself is not changed. The log names every setting the
    export changed. A folder without export.cfg (hand-made maps) loads exactly as before, and presets and
    existing worlds are not affected.
  - An export's README.txt and export.cfg describe this as a fourth way to make a world from it.
- v0.9.3
  - Works with Expand World Data's biomes again. A biome map's legend (biomemap.txt) names them the way
    Expand World Data's expand_biomes yaml does, for example "DeadWastes: 8B4513", in any case. Since
    0.8.0 Better Continents accepted only the ten vanilla biome names: such a line was reported as
    "Invalid biome name", the whole legend fell back to the default colours, and the added biome's area
    became whichever biome's default colour was nearest (often Mistlands).
  - Legend names may be written as the game shows them, with spaces, underscores or hyphens ("Black
    Forest", "deep_north"). A legend line Better Continents cannot use (a name that is no biome here, a
    colour that does not parse, or a line that is not "name: colour") is reported and skipped, and the rest
    of the legend still applies: the pixels of a skipped name's colour are left to the default generation
    (None) instead of becoming the nearest other biome, and a vanilla biome whose colour does not parse
    keeps its default colour. Lines starting with # are comments. Before, one such line threw the whole
    legend away for the default colours.
  - bc reload bm and bc b fn keep the world's biome map when the new picture or legend has an error, and
    say so. Before, a legend error reloaded the picture with the default colours (a wrong path switched the
    biome map off), and the next save kept the damage.
  - Expand World Data's biomes survive world saves and the settings a server sends to its players. A
    world made with Better Continents 0.7.x and Expand World Data biomes loads with them again, unless a
    0.8 or 0.9 version has saved it since: those versions erased them. There, reload the biome map from
    its picture (bc reload bm) and the next save keeps it.
  - The rest of Better Continents handles them too: Biome precision, alt-biome planting, location
    placement, the minimap (including Expand World Data's per-biome map heights, which Better Continents'
    own map drawing skipped), the "bc ab" report, and world export, which gives each added biome a colour
    of its own and names it in biomemap.txt.
  - Expand World Data numbers its biomes in the order its expand_biomes yaml lists them, and a Better
    Continents world stores that number. Install it wherever the world is played, with the same yaml,
    and add new biomes at the end. Where a world's biome map holds a biome Expand World Data does not
    define now (not installed, or its yaml changed), the default generation decides and the log says so;
    the map keeps the biome for when it is defined again. Before, such a biome would have stopped the
    world from loading.
  - Give each added biome a terrain in the expand_biomes yaml (for example "terrain: Plains"): its ground
    is then that biome's. Without one Expand World Data gives it vanilla's default ground, flat and 30 m
    under water, with or without Better Continents; only a heightmap with Override All hides that.
  - Where a biome map or heat map says what a place is, it now wins over Expand World Data's world yaml
    in every case: Expand World Data's Ashlands test could override the heat map, so Ashlands there had no
    lava heat or Ashlands weather.
  - locationmap.txt keeps Expand World Data's "Name:Alias" locations, which were dropped without a word;
    a location listed twice keeps its last colour, and a colour that does not parse skips only its line
    (before, both threw the whole legend away). Other mods' locations already worked by name.
  - On a world that uses Better Continents the server and every player must run the same version, so update
    them together, and install Expand World Data on all of them when its biomes are in the biome map.
- v0.9.2
  - Works with ZenMap and no-map worlds. Better Continents drew the minimap on background threads and
    let the game carry on before the drawing was done, and the game hands the map to other mods at that
    moment. ZenMap copies it there to build the biome-hidden map it shows at cartography tables, so on a
    Better Continents world the tables showed meadows, plains and low ground as sea and the rest as
    noise. The map is now finished before the game carries on, so cartography tables and admin maps on
    no-map worlds show the real land and sea, as crafted parchment maps already did.
  - The minimap is still drawn on several threads, now on every CPU core (up to 16) instead of four.
    The loading screen waits for it, as it does for vanilla's single-threaded drawing, which takes longer.
  - Mods that hook the game's map drawing now run once, on the finished map. Before, they ran twice, the
    first time on an empty map.
  - If drawing the map on several threads fails, the error is logged and the game draws the map itself.
    Before, a failed thread was ignored and part of the map stayed empty.
  - No world needs changing: every player's game redraws the map each time it joins. On a world that uses
    Better Continents the server and every player must run the same version, so update them together.
- v0.9.1
  - Rebuilt against Valheim 1.0.16 and re-verified: the reference check passes against the
    1.0.16 client and dedicated server, the mod boots on a 1.0.16 dedicated server, and the
    offline alt-biome harness passes every check. Nothing that Better Continents patches changed
    in 1.0.16, and the game's alt-biome list is the same 32 entries.
  - The README, ALTBIOMES.md, the config descriptions, the console output, the generated legend
    headers and the printable colour chart no longer name a Valheim patch: they say "Valheim 1.0"
    or "vanilla". The three guides are rebuilt as the Valheim 1.0 / Better Continents 0.9.x
    editions, so neither a game patch nor a mod patch release needs new guides.
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

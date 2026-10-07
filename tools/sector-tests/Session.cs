// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// When the wide sectors are on (a world's size, the chunks of a save), switching them on and off with Harmony, one failed hook,
// the sector array's width, a save made before 0.10.3 whose far objects are all in chunk (0, 0), and Utils.SmallPosition.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static BC.BetterContinentsSettings World(bool enabled, float size, float edge, int version = 12) =>
    new() { EnabledForThisWorld = enabled, Version = version, WorldSize = size, EdgeSize = edge };

  private static bool Patched(MethodBase target, string owner) =>
    Harmony.GetPatchInfo(target) is { } info && info.Prefixes.Concat(info.Transpilers).Any(p => p.owner == owner);

  private static bool AllHooksPatched(string owner) => WorldSectors.HookList().All(h => Patched(h.Target!, owner));
  private static bool NoHookPatched(string owner) => WorldSectors.HookList().All(h => !Patched(h.Target!, owner));

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void SessionTests()
  {
    Section("when the wide sectors are on, switching them, and the save's chunks");
    var owner = "sector-tests.session";
    var harmony = new Harmony(owner);
    FakeGame();
    WorldSectors.SessionStarts();

    // The world's size: the game's sectors reach 16,352 m east and 16,416 m west.
    var none = (WorldGeometry)null;
    Check(!WorldSectors.Wanted(World(true, 15850f, 500f), none) && WorldSectors.Wanted(World(true, 15851f, 500f), none), "World Size + Edge Size 16350 m is the game's own reach, 16351 m is past it");
    Check(!WorldSectors.Wanted(World(true, 10000f, 500f), none) && !WorldSectors.Wanted(new BC.BetterContinentsSettings(), none), "vanilla's size, and Better Continents off, are not");
    Check(WorldSectors.Wanted(World(true, 24000f, 500f), none) && WorldSectors.Wanted(World(true, 64500f, 500f), none) && WorldSectors.Wanted(World(true, 200000f, 500f), none),
      "24500 m, 65000 m and 200000 m are");
    Check(!WorldSectors.Wanted(World(false, 24000f, 500f), none), "Better Continents off for the world: the game's own, whatever its size");
    Check(WorldSectors.Wanted(World(true, 24000f, 500f, version: 11), none) && WorldSectors.Wanted(World(true, 24000f, 500f, version: 6), none),
      "a world made by an older Better Continents is the same (its far objects are in the one list too)");
    Check(!WorldSectors.Wanted(World(true, float.NaN, 500f), none) && !WorldSectors.Wanted(World(true, 0f, 0f), none) && !WorldSectors.Wanted(World(true, -20000f, 500f), none),
      "a size that makes no world (nothing, NaN, negative) is not");
    Check(WorldSectors.Wanted(World(true, 10000f, 500f), new WorldGeometry(20000f, 500f)) && !WorldSectors.Wanted(World(true, 10000f, 500f), new WorldGeometry(15000f, 500f)),
      "Expand World Size's size counts, when it has sent one: 20500 m is past the game's, 15500 m is not");
    Check(WorldSectors.Wanted(World(true, 20000f, 500f), new WorldGeometry(10000f, 500f)), "and the world's own, when Expand World Size's is smaller");

    // Switching on and off.
    int mark = CapturingLogHandler.Lines.Count;
    WorldSectors.Update(harmony, World(true, 10000f, 500f), none);
    Check(!WorldSectors.Active && NoHookPatched(owner), "a vanilla-size world: nothing is patched");
    WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
    Check(WorldSectors.Active && AllHooksPatched(owner), "a world of 24500 m: all ten hooks are patched");
    Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("Sectors: every zone from -1024 to 1023") && l.Contains("32 MB")), "and the log says so");
    mark = CapturingLogHandler.Lines.Count;
    WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
    Check(CapturingLogHandler.Lines.Count == mark, "asked again: nothing happens, nothing is logged");
    WorldSectors.Update(harmony, World(true, 30000f, 500f), none);
    Check(WorldSectors.Active && AllHooksPatched(owner), "another size that needs them: still on");
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    Check(!WorldSectors.Active && NoHookPatched(owner), "back to the main menu: off, and no hook is left");

    // What ZDOMan's array is: ResetSectorArray's width.
    {
      var man = Make<ZDOMan>();
      SetField(man, "m_width", 512);
      var reset = typeof(ZDOMan).GetMethod("ResetSectorArray", Any)!;
      reset.Invoke(man, null);
      Check(GetField<List<ZDO>[]>(man, "m_objectsBySector").Length == 512 * 512, "the game's own ResetSectorArray: 512 x 512 sectors");
      WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
      reset.Invoke(man, null);
      Check(GetField<List<ZDO>[]>(man, "m_objectsBySector").Length == 2048 * 2048, "patched: 2048 x 2048 sectors, whatever width the ZDOMan was made with");
      // The game's own functions now answer for the wide sectors.
      var sectorToIndex = typeof(ZoneSystem).GetMethod("SectorToIndex", [typeof(int), typeof(int)])!;
      var asked = (ZoneSystem.SectorIndex)sectorToIndex.Invoke(null, [300, -300])!;
      Check(asked.Sector == WorldSectors.SectorMap.SectorToIndex(300, -300) && asked.Sector != 0, "ZoneSystem.SectorToIndex answers the wide map for zone (300, -300)");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      asked = (ZoneSystem.SectorIndex)sectorToIndex.Invoke(null, [300, -300])!;
      Check(asked.Sector == 0, "and the game's own again when off: zone (300, -300) is in sector 0");
    }

    // A live ZDOMan: its objects stay where they are.
    {
      var fake = new Fake(512);
      try
      {
        mark = CapturingLogHandler.Lines.Count;
        fake.Load(NewZdo(1, 100f, 100f));
        WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
        Check(!WorldSectors.Active && NoHookPatched(owner) && fake.Sectors.Length == 512 * 512, "a ZDOMan that holds objects keeps the sectors it has: not switched on");
        Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("the sectors stay as they are")), "and the log says why");
        fake.Destroy(fake.World.Values.First());
        WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
        Check(WorldSectors.Active && AllHooksPatched(owner) && fake.Sectors.Length == 2048 * 2048, "an empty one (a client before the server's objects, a server before its world loads): switched on, its array is made wide");
        fake.Load(NewZdo(2, 100f, 100f));
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
        Check(WorldSectors.Active && fake.Sectors.Length == 2048 * 2048, "with objects in it again, off is left for the next session");
        fake.Destroy(fake.World.Values.First());
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
        Check(!WorldSectors.Active && NoHookPatched(owner) && fake.Sectors.Length == 512 * 512, "emptied: off, its array is the game's again");
      }
      finally
      {
        fake.Close();
      }
    }

    // The save's chunks.
    {
      WorldSectors.SessionStarts();
      var game = new[] { new ZoneSystem.ChunkIndex(0, 0), new ZoneSystem.ChunkIndex(1, 0), new ZoneSystem.ChunkIndex(63 | 63 << 8, 0), new ZoneSystem.ChunkIndex(32 | 5 << 8, 3) };
      WorldSectors.MappingLoaded(harmony, game);
      Check(!WorldSectors.Active && !WorldSectors.ForcedBySave, "a save whose chunks are all inside the game's 64 x 64: nothing changes");
      mark = CapturingLogHandler.Lines.Count;
      WorldSectors.MappingLoaded(harmony, game.Append(new ZoneSystem.ChunkIndex(64 | 3 << 8, 0)));
      Check(WorldSectors.Active && WorldSectors.ForcedBySave && AllHooksPatched(owner), "a save with a chunk (64, 3): the wide sectors are on, whatever the world's settings say");
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("a chunk (64, 3) beyond the game's 64 x 64")), "and the log names the chunk");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      Check(WorldSectors.Active, "asked for the game's by settings that say nothing is needed: still on, the save says otherwise");
      WorldSectors.SessionStarts();
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      Check(!WorldSectors.Active && !WorldSectors.ForcedBySave && NoHookPatched(owner), "the next session starts from its own settings");
      WorldSectors.MappingLoaded(harmony, [new ZoneSystem.ChunkIndex(0 | 200 << 8, 0)]);
      Check(WorldSectors.Active, "a chunk (0, 200) is beyond too");
      WorldSectors.SessionStarts();
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    // One hook that cannot be patched: all of them are left off.
    {
      var other = new Harmony("sector-tests.other");
      var decide = typeof(ZDOMan).GetMethod("DecideChunkSize", Any)!;
      other.CreateProcessor(decide).AddTranspiler(typeof(Program).GetMethod(nameof(Break64), Any)!).Patch();
      mark = CapturingLogHandler.Lines.Count;
      WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
      Check(!WorldSectors.Active && NoHookPatched(owner), "another mod's transpiler has changed DecideChunkSize: no hook is left on any game method, the sectors stay the game's");
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("[Error]") && l.Contains("could not patch ZDOMan.DecideChunkSize") && l.Contains("objects beyond 16.4 km share one sector")), "and the log says which, and what stays");
      other.UnpatchAll("sector-tests.other");
      WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
      Check(WorldSectors.Active && AllHooksPatched(owner), "with that gone, the next ask patches all of them");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    MigrationTests(harmony);
    ZoneRegenTests(harmony);
    SmallPositionTests(harmony);
  }

  // ------------------------------------------------------------------------------------------------ Better Continents' zone regeneration

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ZoneRegenTests(Harmony harmony)
  {
    Section("zone regeneration finds a far zone's objects in the wide sectors");
    var none = (WorldGeometry)null;
    WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
    ZDOExtraData.Reset();
    var fake = new Fake(2048);
    var net = Make<ZNet>();
    SetField(net, "m_peers", new List<ZNetPeer>());
    SetStatic(typeof(ZNet), "m_instance", net);
    try
    {
      uint id = 1;
      var counts = new Dictionary<(int, int), int> { [(300, 40)] = 5, [(-300, 17)] = 3, [(-256, -256)] = 2, [(1100, 0)] = 4, [(0, 0)] = 1, [(-1024, 1023)] = 2, [(-244, -252)] = 6 };
      foreach (var ((zx, zy), count) in counts)
        for (int i = 0; i < count; i++)
          fake.Load(NewZdo(id++, zx * 64f + i * 3f, zy * 64f - i));
      var world = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));
      var found = counts.ToDictionary(kv => kv.Key, kv => world.ObjectsIn(new Vector2s(kv.Key.Item1, kv.Key.Item2)).Count);
      Check(found.All(kv => kv.Value == counts[kv.Key]),
        $"ObjectsIn(zone) is the zone's own objects, for zones in and past the game's sectors, in the corner zone (-256, -256) that shares sector 0 with what the map does not reach, and past the map "
        + $"({string.Join(", ", found.Select(kv => $"{kv.Key}: {kv.Value}"))})");
      Check(world.ObjectsIn(new Vector2s(5, 5)).Count == 0 && world.ObjectsIn(new Vector2s(-1030, -1030)).Count == 0, "and nothing for a zone with none");
      var sectors = fake.Sectors;
      Check(sectors[ZoneSystem.GetSectorIndex(new Vector3(300 * 64f, 30f, 40 * 64f)).Sector].Count == 5 && sectors[0].Count == 2 + 4, "the lists are the zone's own: 5 in zone (300, 40), and sector 0 holds what the game files there (2 + 4)");
    }
    finally
    {
      fake.Close();
      SetStatic(typeof(ZNet), "m_instance", null);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
  }

  // Another mod's change to DecideChunkSize: its 64 chunks a side are 65.
  private static IEnumerable<CodeInstruction> Break64(IEnumerable<CodeInstruction> instructions)
  {
    foreach (var code in instructions)
    {
      if (WorldSectors.IntLoad(code) == 64)
      {
        code.opcode = System.Reflection.Emit.OpCodes.Ldc_I4;
        code.operand = 65;
      }
      yield return code;
    }
  }

  // ------------------------------------------------------------------------------------------------ a save made before

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void MigrationTests(Harmony harmony)
  {
    Section("a save made before the wide sectors: the far objects in chunk (0, 0)");
    var none = (WorldGeometry)null;
    var problems = new List<string>();
    WorldSectors.SessionStarts();
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);

    // The game's own save, as 0.10.2 made it: objects in and beyond its sectors, the far ones filed in chunk (0, 0).
    var rng = new System.Random(5);
    var zdos = MakeWorld(rng, 1, 400, -256, 255, (x, y) => InsideGame(x, y) && !InMovedBlock(x, y));
    var farZdos = MakeWorld(rng, 5000, 300, -600, 600, (x, y) => !InsideGame(x, y) && Mapped(x, y));
    var legacy = new Fake(512, portalChunk: true);
    var legacyMapping = (ChunkSaveMapping)null;
    Dictionary<ChunkKey, List<ZDOID>> legacyDisk;
    Dictionary<ZDOID, ZDO> legacyWorld;
    try
    {
      foreach (var zdo in zdos.Concat(farZdos))
        legacy.Load(zdo);
      var plan = legacy.Plan();
      legacy.Save(plan);
      legacyMapping = legacy.Mapping;
      legacyDisk = new Dictionary<ChunkKey, List<ZDOID>>(legacy.Disk);
      legacyWorld = new Dictionary<ZDOID, ZDO>(legacy.World);
      var pile = plan.Single(c => c.Number == 0 && c.Size == 0);
      Check(pile.Ids.Count >= farZdos.Count && farZdos.All(z => pile.Ids.Contains(z.m_uid)), $"0.10.2's save: all {farZdos.Count} far objects are in chunk (0, 0), with {pile.Ids.Count - farZdos.Count} of the zones there");
    }
    finally
    {
      legacy.Close();
    }

    // Loaded with the wide sectors but without the migration: the far objects are in the file and in chunks of their own.
    WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
    List<Chunk> Reload(bool migrate, out Fake fake)
    {
      fake = new Fake(2048);
      foreach (var zdo in legacyWorld.Values)
        fake.Load(zdo);
      SetField(fake.Man, "m_chunkSaveMapping", legacyMapping.Clone());
      foreach (var (key, ids) in legacyDisk)
        fake.Disk[key] = ids;
      if (migrate)
        WorldSectors.LoadedChunks(fake.Man);
      return fake.Plan();
    }
    var without = Reload(false, out var f0);
    f0.Save(without);
    Check(without.Any(c => (c.Number & 0xFF) >= 64 || c.Number >> 8 >= 64) && f0.DiskIds.Count != f0.DiskIds.Distinct().Count(),
      $"without it the far objects are in the save twice ({f0.DiskIds.Count - f0.DiskIds.Distinct().Count()} of {f0.World.Count} objects are in two files)");
    f0.Close();

    mark(out var mark0);
    var migrated = Reload(true, out var f1);
    try
    {
      Check(!f1.Mapping.Chunks.ContainsKey(new ZoneSystem.ChunkIndex(0, 0)) && f1.Mapping.Chunks.ContainsKey(ZoneSystem.ChunkPortal), "with it: chunk (0, 0) is out of the chunk list, the others (and the portals') stay");
      Check(CapturingLogHandler.Lines.Skip(mark0).Any(l => l.Contains($"{farZdos.Count} objects lie beyond the game's sectors")), $"the log counts the {farZdos.Count} objects");
      CheckPlan(f1, migrated, f1.World.Values.Where(z => !InsideGame(ZoneSystem.GetZone(z.GetPosition()).x, ZoneSystem.GetZone(z.GetPosition()).y)).ToList(), "migration save", problems, requireAll: false);
      f1.Save(migrated);
      CheckDisk(f1, "migration save", problems);
      Check(problems.Count == 0 && f1.DiskIds.Count == f1.World.Count, $"the save after it holds every object once ({f1.DiskIds.Count} of {f1.World.Count}; {problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
      Check(farZdos.All(z => migrated.Any(c => c.Ids.Contains(z.m_uid) && !(c.Number == 0 && c.Size == 0))), "every far object is in a chunk of its own, none in chunk (0, 0)");
      var next = f1.Plan();
      Check(next.Count == 0, "and nothing is left to write");
    }
    finally
    {
      f1.Close();
    }

    // The zones the game files in chunk (1, 0), its portals' key: an old save of them is loaded as portals.
    {
      var portalPrefab = "portal_wood".GetStableHashCode();
      var inBlock = new List<ZDO>();
      uint id = 70000;
      for (int zy = -256; zy <= -249; zy += 2)
        for (int zx = -248; zx <= -241; zx += 2)
          inBlock.Add(NewZdo(id++, zx * 64f + 3f, zy * 64f + 4f));
      var elsewhere = MakeWorld(rng, 71000, 120, -256, 255, (x, y) => InsideGame(x, y) && !InMovedBlock(x, y));
      var portal = NewZdo(id++, 700f, 900f);
      FPrefabField.SetValue(portal, portalPrefab);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      var old = new Fake(512);
      Dictionary<ChunkKey, List<ZDOID>> disk;
      ChunkSaveMapping mapping;
      Dictionary<ZDOID, ZDO> world;
      try
      {
        GetStatic<Game>(typeof(Game), "<instance>k__BackingField").PortalPrefabHash.Add(portalPrefab);
        foreach (var zdo in inBlock.Concat(elsewhere))
          old.Load(zdo);
        var plan = old.Plan();
        // The game keeps a portal out of the sector lists, and saves it in chunk (1, 0) beside the objects of those zones: the
        // second write of that file is the portal's.
        Check(plan.Any(c => c.Number == 1 && c.Size == 0 && inBlock.All(z => c.Ids.Contains(z.m_uid))), "the game's save of those zones is chunk (1, 0), the portals' key");
        old.Save(plan);
        mapping = old.Mapping;
        disk = new Dictionary<ChunkKey, List<ZDOID>>(old.Disk);
        world = new Dictionary<ZDOID, ZDO>(old.World);
      }
      finally
      {
        old.Close();
      }
      WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
      var f = new Fake(2048);
      try
      {
        // ZDOMan.LoadChunks: the file of chunk (1, 0) is the portals' (m_portalObjects), every other file's objects go into their sectors.
        var portals = GetField<Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>(f.Man, "m_portalObjects");
        foreach (var (key, ids) in disk)
          foreach (var zdoId in ids)
          {
            var zdo = world[zdoId];
            if (key.Number == 1 && key.Size == 0)
            {
              f.World[zdo.m_uid] = zdo;
              f.ById[zdo.m_uid] = zdo;
              var sector = ZoneSystem.GetSectorIndex(zdo.GetPosition());
              if (!portals.TryGetValue(sector, out var list))
                portals[sector] = list = [];
              list.Add(zdo);
            }
            else
              f.Load(zdo);
          }
        SetField(f.Man, "m_chunkSaveMapping", mapping.Clone());
        foreach (var (key, ids) in disk)
          f.Disk[key] = ids;
        mark(out var mark1);
        WorldSectors.LoadedChunks(f.Man);
        Check(portals.Count == 0 && inBlock.All(z => f.Sectors[ZoneSystem.GetSectorIndex(z.GetPosition()).Sector]?.Contains(z) == true),
          "loaded with the wide sectors, the objects of those zones that are not portals leave the portals' list and go into their sectors");
        Check(CapturingLogHandler.Lines.Skip(mark1).Any(l => l.Contains($"{inBlock.Count} objects of the zones the game's save files as its portals' chunk (1, 0)")), "and the log counts them");
        var plan = f.Plan();
        Check(plan.Any(c => c.Number == 159 && c.Size == 0 && inBlock.All(z => c.Ids.Contains(z.m_uid))), "the next save writes them in chunk (159, 0)");
        Check(plan.Any(c => c.Number == 1 && c.Size == 0 && c.Ids.Count == 0), "and the portals' chunk again, without them");
        f.Save(plan);
        var problems2 = new List<string>();
        CheckDisk(f, "portal chunk migration", problems2);
        Check(problems2.Count == 0 && f.DiskIds.Count == f.World.Count, $"the save holds every object once ({f.DiskIds.Count} of {f.World.Count}; {problems2.Count} problems)");
        Check(f.Plan().Count == 0, "and nothing is left to write");
      }
      finally
      {
        f.Close();
      }
      // A real portal stays where it is.
      var g = new Fake(2048);
      try
      {
        var portals = GetField<Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>(g.Man, "m_portalObjects");
        portals[ZoneSystem.GetSectorIndex(portal.GetPosition())] = [portal];
        g.World[portal.m_uid] = portal;
        g.ById[portal.m_uid] = portal;
        SetField(g.Man, "m_chunkSaveMapping", new ChunkSaveMapping());
        WorldSectors.LoadedChunks(g.Man);
        Check(portals.Values.SelectMany(l => l).Contains(portal) && !GetField<bool[]>(g.Man, "m_dirtyPortalObjects")[0], "a portal stays in the portals' list, and the portals' chunk is not written for nothing");
      }
      finally
      {
        g.Close();
      }
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    // A save that has chunks beyond the game's, or no object beyond its sectors: the chunk list stays as it is.
    {
      var f = new Fake(2048);
      try
      {
        foreach (var zdo in zdos)
          f.Load(zdo);
        SetField(f.Man, "m_chunkSaveMapping", legacyMapping.Clone());
        WorldSectors.LoadedChunks(f.Man);
        Check(f.Mapping.Chunks.ContainsKey(new ZoneSystem.ChunkIndex(0, 0)), "a save with no object beyond the game's sectors: chunk (0, 0) stays");
        foreach (var zdo in farZdos)
          f.Load(zdo);
        f.Mapping.CreateOrUpdate(new ZoneSystem.ChunkIndex(70 | 2 << 8, 0));
        WorldSectors.LoadedChunks(f.Man);
        Check(f.Mapping.Chunks.ContainsKey(new ZoneSystem.ChunkIndex(0, 0)), "a save that has a chunk beyond the game's (made by the wide sectors): chunk (0, 0) stays too");
      }
      finally
      {
        f.Close();
      }
    }
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
  }

  private static void mark(out int index) => index = CapturingLogHandler.Lines.Count;

  // ------------------------------------------------------------------------------------------------ Utils.SmallPosition

  // The game's Utils.SmallPosition as it is written in the 1.0.17 client and server (assembly_utils): the test cannot run it (Utils'
  // static constructor calls into Unity), so this is its text, and its IL is checked below to be the one it was copied from.
  private static (bool, Vector2s) GameSmallPosition(float x, float y, float z)
  {
    if (BitConverter.SingleToInt32Bits(y) != 0)
      return (false, Vector2s.zero);
    Vector2s item = new Vector2s((short)x, (short)z);
    bool num = ((float)item.x).Equals(x);
    bool flag = ((float)item.y).Equals(z);
    if (num)
    {
      if (flag)
        return (true, item);
      if (z < -20000f)
      {
        item.y = -20000;
        return (true, item);
      }
      if (z > 20000f)
      {
        item.y = 20000;
        return (true, item);
      }
    }
    if (flag)
    {
      if (x < -20000f)
      {
        item.x = -20000;
        return (true, item);
      }
      if (x > 20000f)
      {
        item.x = 20000;
        return (true, item);
      }
    }
    return (false, Vector2s.zero);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void SmallPositionTests(Harmony harmony)
  {
    Section("Utils.SmallPosition: a _ZoneCtrl is saved where it is");
    var none = (WorldGeometry)null;
    var game = typeof(Utils).GetMethod("SmallPosition", Any)!;
    // The text above is the game's: the IL it was written from (the same in the client's and the server's).
    string Md5(MethodBase m) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(Text(m))));
    var serverSmall = serverUtils.GetType("Utils")!.GetMethod("SmallPosition", Any)!;
    Check(Md5(game) == Md5(serverSmall) && PatchProcessor.GetOriginalInstructions(game).Count == 90 && Md5(game) == "42B85036E4F15C205BA30B6E723BE7CA",
      $"the game's Utils.SmallPosition is the code this test transcribes (the instructions' md5 {Md5(game)}, {PatchProcessor.GetOriginalInstructions(game).Count} of them; the same in the server's)");
    var rng = new System.Random(9);
    var values = new List<float> { 0f, 1f, -1f, 64f, -64f, 19999f, 20000f, -20000f, 20001f, -20001f, 32767f, -32768f, 32768f, 40000f, -40000f, 65504f, 1.5f, -2.25f, 19999.5f, 25600.5f };
    for (int i = 0; i < 200; i++)
      values.Add(rng.Next(-20000, 20001) * (rng.Next(0, 3) == 0 ? 1.5f : 1f));
    int within = 0, differ = 0, clamped = 0, exact = 0, bad = 0;
    foreach (var x in values)
      foreach (var z in values)
        foreach (var y in new[] { 0f, 30f, -0f })
        {
          var was = GameSmallPosition(x, y, z);
          var now = WorldSectors.SmallPosition(x, y, z);
          if (Math.Abs(x) <= 20000f && Math.Abs(z) <= 20000f)
          {
            within++;
            if (was != now) differ++;
          }
          else if (was.Item1 && (was.Item2.x != x || was.Item2.y != z))
            clamped++;
          // Whatever it says, the position must come back exactly: a short pair that is the position, or no small form.
          if (now.Item1 ? now.Item2.x != x || now.Item2.y != z || BitConverter.SingleToInt32Bits(y) != 0 : now.Item2 != Vector2s.zero)
            bad++;
          else if (now.Item1)
            exact++;
        }
    Check(within > 40000 && differ == 0, $"for every position within 20000 m the game's answer and the patch's are the same ({within} positions, {differ} differ)");
    Check(clamped > 100, $"the game clamps a position beyond 20000 m (y = 0, the other coordinate whole) to 20000 m: {clamped} of the positions here");
    Check(bad == 0 && exact > 10000, $"the patch never does: a short pair is the position, or there is none ({bad} wrong, {exact} short pairs)");
    Check(GameSmallPosition(40000f, 0f, 0f) == (true, new Vector2s(20000, 0)), "the game: a _ZoneCtrl at x = 40000 m is saved at 20000 m");

    // Patched in: the game's own function.
    WorldSectors.Update(harmony, World(true, 24000f, 500f), none);
    (bool, Vector2s) Asked(float x, float y, float z) => ((bool, Vector2s))game.Invoke(null, [new Vector3(x, y, z)])!;
    Check(Asked(40000f, 0f, 0f) == (false, Vector2s.zero) && Asked(25600f, 0f, 25600f) == (true, new Vector2s(25600, 25600)) && Asked(40000f, 30f, 0f) == (false, Vector2s.zero)
          && Asked(0f, 0f, -40000f) == (false, Vector2s.zero) && Asked(1024f, 0f, 2048f) == (true, new Vector2s(1024, 2048)),
      "patched: at 40000 m it has no short form (the full position is saved), at 25600 m by 25600 m it is two shorts, exactly");

    // The save and the load of an object, in the game's own ZDO.Save and ZDO.Load.
    try
    {
      ZDOExtraData.Reset();
      ZDOExtraData.PrepareSave();
      var back = new List<(Vector3 Was, Vector3 Is)>();
      foreach (var position in new[] { new Vector3(25600f, 0f, -25600f), new Vector3(-40000f, 0f, 0f), new Vector3(0f, 0f, 60032f), new Vector3(-64000f, 0f, 65472f), new Vector3(1024f, 0f, 2048f), new Vector3(30000.5f, 30f, 1f) })
      {
        var zdo = NewZdo(7, position.x, position.z);
        FPosition.SetValue(zdo, position);
        var written = new ZPackage();
        zdo.Save(written);
        var pkg = new ZPackage(written.GetArray());
        var loaded = Make<ZDO>();
        loaded.Load(pkg, Enum.GetValues(typeof(global::Version.World)).Cast<global::Version.World>().Max());
        back.Add((position, loaded.GetPosition()));
      }
      Check(back.All(b => b.Was == b.Is), $"ZDO.Save then ZDO.Load gives the position back exactly, out to 65 km ({string.Join("; ", back.Where(b => b.Was != b.Is).Select(b => $"{b.Was} came back as {b.Is}"))})");
    }
    catch (Exception e)
    {
      Check(false, $"ZDO.Save and ZDO.Load on made-up objects failed: {e.GetType().Name}: {e.Message}{(e.InnerException != null ? " / " + e.InnerException.Message : "")}");
    }
    finally
    {
      ZDOExtraData.ClearSave();
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
    Check(!WorldSectors.Active && NoHookPatched("sector-tests.session"), "and the wide sectors are off again at the end");
  }
}

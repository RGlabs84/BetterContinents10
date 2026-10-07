// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The game's own save planning, run on made-up worlds: ZDOMan.GetSaveClonePerChunk (with AddObjectsPerChunk, DecideChunkSize and
// the sector and chunk functions they call), on a ZDOMan built without a game around it (as the zone tests do). First the game's
// own code, unpatched, on worlds inside its 512 x 512 sectors; then the same worlds with WorldSectors patched in, which must
// plan the same chunks; then worlds that reach 65 km, where every object must be in one chunk, a change must be written, and
// the chunks that the save leaves behind must give the world back exactly.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using Map = BetterContinents.WorldSectors.SectorMap;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static readonly FieldInfo FPosition = typeof(ZDO).GetField("m_position", Any)!;
  private static readonly FieldInfo FPrefabField = typeof(ZDO).GetField("m_prefab", Any)!;
  private static T GetStatic<T>(Type type, string name) => (T)type.GetField(name, Any)!.GetValue(null);

  private static ZDO NewZdo(uint id, float x, float z, bool persistent = true)
  {
    // Not ZDOPool.Create: the constructor of ZDO calls into Unity (Quaternion.eulerAngles).
    var zdo = Make<ZDO>();
    zdo.m_uid = new ZDOID(1000L, id);
    FPosition.SetValue(zdo, new Vector3(x, 30f, z));
    zdo.Persistent = persistent;
    return zdo;
  }

  // Game.instance.PortalPrefabHash, which the planning reads.
  private static void FakeGame()
  {
    var game = Make<Game>();
    SetField(game, "<PortalPrefabHash>k__BackingField", new List<int>());
    SetStatic(typeof(Game), "<instance>k__BackingField", game);
  }

  // A chunk of a plan or of the "disk": its key and its objects.
  private readonly record struct Chunk(ushort Number, byte Size, List<ZDOID> Ids)
  {
    public string Key => $"{Number & 0xFF},{Number >> 8}/{Size}";
  }

  private sealed class Fake
  {
    public readonly ZDOMan Man = Make<ZDOMan>();
    public readonly int Width;
    public readonly Dictionary<ZDOID, ZDO> World = [];
    public ChunkSaveMapping Mapping => GetField<ChunkSaveMapping>(Man, "m_chunkSaveMapping");
    public HashSet<ZoneSystem.ChunkIndex> Dirty => GetField<HashSet<ZoneSystem.ChunkIndex>[]>(Man, "m_dirtyChunks")[0];
    public List<ZDO>[] Sectors => GetField<List<ZDO>[]>(Man, "m_objectsBySector");
    public readonly Dictionary<ChunkKey, List<ZDOID>> Disk = [];
    public Dictionary<ZDOID, ZDO> ById => GetField<Dictionary<ZDOID, ZDO>>(Man, "m_objectsByID");

    public Fake(int width, bool portalChunk = false)
    {
      Width = width;
      SetField(Man, "m_objectsBySector", new List<ZDO>[width * width]);
      SetField(Man, "m_dirtyChunks", new[] { new HashSet<ZoneSystem.ChunkIndex>(), new HashSet<ZoneSystem.ChunkIndex>() });
      SetField(Man, "m_dirtyPortalObjects", new bool[2]);
      SetField(Man, "m_portalObjects", new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
      SetField(Man, "m_objectsByID", new Dictionary<ZDOID, ZDO>());
      SetField(Man, "m_chunkSaveMapping", new ChunkSaveMapping());
      SetField(Man, "m_saveData", Activator.CreateInstance(typeof(ZDOMan).GetNestedType("SaveData", Any)!, true)!);
      SetStatic(typeof(ZDOMan), "s_instance", Man);
      // The portal file a world has: ChunkPortal is a chunk of the save like any other.
      if (portalChunk)
        Mapping.CreateOrUpdate(ZoneSystem.ChunkPortal);
    }

    public void Close() => SetStatic(typeof(ZDOMan), "s_instance", null);

    // As loading does (ZDOMan.InitialAddToSector): the object goes into its sector's list, and nothing is dirty.
    public void Load(ZDO zdo)
    {
      World[zdo.m_uid] = zdo;
      ById[zdo.m_uid] = zdo;
      var index = ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector;
      (Sectors[index] ??= []).Add(zdo);
    }

    // A new object, a change (the object is saved again) or a removal, as the game does them.
    public void Create(ZDO zdo)
    {
      World[zdo.m_uid] = zdo;
      ById[zdo.m_uid] = zdo;
      Man.AddToSector(zdo, ZoneSystem.GetSectorIndex(zdo.GetPosition()));
    }

    public void Touch(ZDO zdo) => Man.SetDirtySector(zdo);

    public void Destroy(ZDO zdo)
    {
      World.Remove(zdo.m_uid);
      ById.Remove(zdo.m_uid);
      Man.RemoveFromSector(zdo, ZoneSystem.GetSectorIndex(zdo.GetPosition()));
    }

    // ZDOMan.PrepareSave's plan.
    public List<Chunk> Plan()
    {
      var method = typeof(ZDOMan).GetMethod("GetSaveClonePerChunk", Any)!;
      var plan = (List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>)method.Invoke(Man, null)!;
      return plan.Select(t => new Chunk(t.Item1.Chunk, t.Item1.m_chunkSize, t.Item2.Select(z => z.m_uid).ToList())).ToList();
    }

    // What ZDOMan.SaveChunks, DeleteOldChunks and the end of a save do with a plan: the chunk list keeps the chunks (a chunk that
    // is merged into a bigger one, or split from one, replaces it), and each file now holds what the plan gave it.
    public void Save(List<Chunk> plan)
    {
      var current = Mapping.Clone();
      foreach (var chunk in plan)
        current.CreateOrUpdate(new ZoneSystem.ChunkIndex(chunk.Number, chunk.Size));
      foreach (var chunk in plan)
      {
        current.Get(new ZoneSystem.ChunkIndex(chunk.Number, chunk.Size)).m_numZDOs = chunk.Ids.Count;
        Disk[new ChunkKey(chunk.Number, chunk.Size)] = chunk.Ids;
      }
      SetField(Man, "m_chunkSaveMapping", current);
      // A file whose chunk is not in the list any more was deleted with the old ones.
      var keep = current.Chunks.Where(kv => kv.Value.SaveChunk).Select(kv => new ChunkKey(kv.Key.Chunk, kv.Key.m_chunkSize)).ToHashSet();
      foreach (var key in Disk.Keys.ToList())
        if (!keep.Contains(key))
          Disk.Remove(key);
      Dirty.Clear();
      GetField<bool[]>(Man, "m_dirtyPortalObjects")[0] = false;
    }

    // What a load would read back: the objects of every file in the chunk list, which the game files by position.
    public List<ZDOID> DiskIds => Disk.Values.SelectMany(l => l).ToList();
  }

  private readonly record struct ChunkKey(ushort Number, byte Size);

  // The zone and chunk of a position as the planning is meant to put them: an object lies within its chunk's own zones.
  private static bool InChunk(Chunk chunk, Vector3 position)
  {
    var origin = Map.ZoneFromChunk(chunk.Number);
    var zone = ZoneSystem.GetZone(position);
    int side = 8 << chunk.Size;
    return zone.x >= origin.x && zone.x < origin.x + side && zone.y >= origin.y && zone.y < origin.y + side;
  }

  // A made-up world: clusters of objects (a base, a forest, a harbour) and scattered ones, as many as the zones allow.
  private static List<ZDO> MakeWorld(System.Random rng, uint first, int count, int firstZone, int lastZone, Func<int, int, bool> allowed)
  {
    var zdos = new List<ZDO>();
    uint id = first;
    int clusters = 1 + count / 40;
    for (int c = 0; c < clusters; c++)
    {
      int cx = rng.Next(firstZone, lastZone + 1), cy = rng.Next(firstZone, lastZone + 1);
      int spread = rng.Next(0, 3) == 0 ? 20 : 2;
      for (int i = 0; i < count / clusters; i++)
      {
        int zx = Math.Clamp(cx + rng.Next(-spread, spread + 1), firstZone, lastZone), zy = Math.Clamp(cy + rng.Next(-spread, spread + 1), firstZone, lastZone);
        if (!allowed(zx, zy)) continue;
        zdos.Add(NewZdo(id++, zx * 64f + (float)(rng.NextDouble() * 62 - 31), zy * 64f + (float)(rng.NextDouble() * 62 - 31)));
      }
    }
    return zdos;
  }

  private static bool InsideGame(int zx, int zy) => zx >= -256 && zx <= 255 && zy >= -256 && zy <= 255;
  private static bool InMovedBlock(int zx, int zy) => zx >= -248 && zx <= -241 && zy >= -256 && zy <= -249;
  private static bool Mapped(int zx, int zy) => !(zx >= 1016 && zy >= -256 && zy <= -249);

  // One scenario, run on a fake ZDOMan of the given width, whatever is patched: four saves with changes between them. The plan of
  // each save is returned (the first plans everything; the second and third what changed; the fourth nothing).
  private static List<List<Chunk>> Scenario(int seed, int width, int firstZone, int lastZone, Func<int, int, bool> allowed, List<string> problems, bool portalChunk = false)
  {
    var rng = new System.Random(seed);
    var fake = new Fake(width, portalChunk);
    var plans = new List<List<Chunk>>();
    try
    {
      var zdos = MakeWorld(rng, 1, 600, firstZone, lastZone, allowed);
      foreach (var zdo in zdos)
        fake.Load(zdo);
      var round1 = fake.Plan();
      CheckPlan(fake, round1, zdos, $"seed {seed} save 1", problems, requireAll: true);
      fake.Save(round1);
      plans.Add(round1);
      CheckDisk(fake, $"seed {seed} save 1", problems);

      // Changes: some objects change, some are destroyed, new ones appear next to old ones and in new places.
      var changed = new List<ZDO>();
      foreach (var zdo in zdos.OrderBy(_ => rng.Next()).Take(60))
      {
        fake.Touch(zdo);
        changed.Add(zdo);
      }
      // (The game never writes a chunk that has no objects left, so the file of a chunk that is emptied keeps what it held: the
      // objects destroyed here are never the last of their chunk. That is the game's own, not this patch's.)
      var inChunk = zdos.GroupBy(z => ZoneSystem.GetZonesChunk(ZoneSystem.GetSectorIndex(z.GetPosition())).Chunk).ToDictionary(g => g.Key, g => g.Count());
      foreach (var zdo in zdos.OrderBy(_ => rng.Next()).Take(20).ToList())
      {
        var chunk = ZoneSystem.GetZonesChunk(ZoneSystem.GetSectorIndex(zdo.GetPosition())).Chunk;
        if (changed.Contains(zdo) || inChunk[chunk] < 2)
          continue;
        inChunk[chunk]--;
        fake.Destroy(zdo);
        zdos.Remove(zdo);
      }
      uint next = 100000;
      foreach (var zdo in MakeWorld(rng, next, 80, firstZone, lastZone, allowed))
      {
        fake.Create(zdo);
        zdos.Add(zdo);
        changed.Add(zdo);
      }
      var round2 = fake.Plan();
      CheckPlan(fake, round2, changed, $"seed {seed} save 2", problems, requireAll: false);
      fake.Save(round2);
      plans.Add(round2);
      CheckDisk(fake, $"seed {seed} save 2", problems);

      // More of the same, in places the world has not been to.
      changed.Clear();
      foreach (var zdo in zdos.OrderBy(_ => rng.Next()).Take(30))
      {
        fake.Touch(zdo);
        changed.Add(zdo);
      }
      foreach (var zdo in MakeWorld(rng, 200000, 150, firstZone, lastZone, allowed))
      {
        fake.Create(zdo);
        zdos.Add(zdo);
        changed.Add(zdo);
      }
      var round3 = fake.Plan();
      CheckPlan(fake, round3, changed, $"seed {seed} save 3", problems, requireAll: false);
      fake.Save(round3);
      plans.Add(round3);
      CheckDisk(fake, $"seed {seed} save 3", problems);

      // Nothing has changed: nothing is written.
      var round4 = fake.Plan();
      if (round4.Count != 0)
        problems.Add($"seed {seed} save 4: {round4.Count} chunks planned though nothing changed");
      plans.Add(round4);
    }
    finally
    {
      fake.Close();
    }
    return plans;
  }

  // What a plan must be: no object twice, no chunk twice, every object in a chunk that reaches its zone, no chunk that is the
  // portals' key, and every object that is new or changed in a chunk of the plan (every one at the first save).
  private static void CheckPlan(Fake fake, List<Chunk> plan, List<ZDO> mustBeIn, string what, List<string> problems, bool requireAll)
  {
    var seen = new HashSet<ZDOID>();
    var keys = new HashSet<ChunkKey>();
    foreach (var chunk in plan)
    {
      if (!keys.Add(new ChunkKey(chunk.Number, chunk.Size))) problems.Add($"{what}: chunk {chunk.Key} twice");
      if (chunk.Number == ZoneSystem.ChunkPortal.Chunk && chunk.Size == ZoneSystem.ChunkPortal.m_chunkSize) problems.Add($"{what}: chunk {chunk.Key} is the portals' key");
      foreach (var id in chunk.Ids)
      {
        if (!seen.Add(id)) problems.Add($"{what}: object {id} twice");
        if (!fake.World.TryGetValue(id, out var zdo)) problems.Add($"{what}: object {id} is not in the world");
        else if (!InChunk(chunk, zdo.GetPosition())) problems.Add($"{what}: object {id} at {zdo.GetPosition()} is in chunk {chunk.Key}, which does not reach its zone");
      }
    }
    foreach (var zdo in mustBeIn)
      if (fake.World.ContainsKey(zdo.m_uid) && !seen.Contains(zdo.m_uid))
        problems.Add($"{what}: object {zdo.m_uid} at {zdo.GetPosition()} changed, or is new, and is in no chunk of the plan");
    if (requireAll && seen.Count != fake.World.Count)
      problems.Add($"{what}: {seen.Count} of the {fake.World.Count} objects are in the plan");
  }

  // The files a save leaves hold the world's objects, each once.
  private static void CheckDisk(Fake fake, string what, List<string> problems)
  {
    var ids = fake.DiskIds;
    var distinct = ids.ToHashSet();
    if (distinct.Count != ids.Count) problems.Add($"{what}: {ids.Count - distinct.Count} objects are in two files");
    if (!distinct.SetEquals(fake.World.Keys.Where(id => fake.World[id].Persistent))) problems.Add($"{what}: the files hold {distinct.Count} objects, the world {fake.World.Count}");
  }

  private static string Describe(List<Chunk> plan) =>
    string.Join(" ", plan.Select(c => $"{c.Key}:{c.Ids.Count}").OrderBy(s => s, StringComparer.Ordinal));

  private static bool SamePlan(List<Chunk> a, List<Chunk> b) =>
    a.Count == b.Count && a.Select(c => (c.Number, c.Size, string.Join(",", c.Ids.Select(i => i.ToString()).OrderBy(s => s, StringComparer.Ordinal))))
      .OrderBy(t => (t.Size, t.Number)).SequenceEqual(b.Select(c => (c.Number, c.Size, string.Join(",", c.Ids.Select(i => i.ToString()).OrderBy(s => s, StringComparer.Ordinal))))
        .OrderBy(t => (t.Size, t.Number)));

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void PlanTests()
  {
    Section("the save's chunk plan, on the game's own code: the game's sectors, then the wide ones");
    Check(!WorldSectors.Active, "the sectors start as the game's");
    FakeGame();
    var harmony = new Harmony("sector-tests.plan");
    var inner = (int zx, int zy) => InsideGame(zx, zy) && !InMovedBlock(zx, zy);
    const int Seeds = 6;

    // 1. The game's own code, unpatched, on worlds inside its sectors.
    var problems = new List<string>();
    var game = new List<List<List<Chunk>>>();
    for (int seed = 1; seed <= Seeds; seed++)
      game.Add(Scenario(seed, 512, -256, 255, inner, problems));
    Check(problems.Count == 0, $"the game's own planning keeps the rules on {Seeds} made-up worlds ({problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
    int merged = game.SelectMany(s => s).SelectMany(p => p).Count(c => c.Size > 0), total = game.SelectMany(s => s).SelectMany(p => p).Count();
    Check(merged > 0 && total > 50, $"the worlds exercise the game's merging of chunks ({merged} of {total} planned chunks are merged)");

    // 2. The wide sectors, on the same worlds: the same chunks, in the same files.
    var wide = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, WorldSize = 24000f, EdgeSize = 500f };
    WorldSectors.Update(harmony, wide, null);
    Check(WorldSectors.Active, "a world of 24500 m turns the wide sectors on");
    problems.Clear();
    int differ = 0;
    for (int seed = 1; seed <= Seeds; seed++)
    {
      var plans = Scenario(seed, 2048, -256, 255, inner, problems);
      for (int round = 0; round < plans.Count; round++)
        if (!SamePlan(plans[round], game[seed - 1][round]))
        {
          differ++;
          if (differ == 1) System.Console.WriteLine($"   first difference: seed {seed} save {round + 1}: game {Describe(game[seed - 1][round])}\n   wide {Describe(plans[round])}");
        }
    }
    Check(problems.Count == 0, $"the wide sectors keep the same rules on the same worlds ({problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
    Check(differ == 0, $"a world inside the game's sectors saves exactly the game's chunks and objects with the wide sectors: {Seeds} worlds x 4 saves, {differ} differ");

    // 3. Worlds that reach 65 km.
    problems.Clear();
    var far = new List<List<List<Chunk>>>();
    for (int seed = 11; seed < 11 + Seeds; seed++)
      far.Add(Scenario(seed, 2048, -1024, 1023, Mapped, problems, portalChunk: true));
    Check(problems.Count == 0, $"worlds from -65.5 km to 65.5 km: every object in one chunk that reaches it, every change written, files that give the world back ({problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
    var chunks = far.SelectMany(s => s).SelectMany(p => p).ToList();
    Check(chunks.Any(c => (c.Number & 0xFF) >= 64 || c.Number >> 8 >= 64) && chunks.Any(c => c.Size > 0 && ((c.Number & 0xFF) >= 64 || c.Number >> 8 >= 64)),
      $"and they use chunks beyond the game's 64 x 64, merged ones too ({chunks.Count(c => (c.Number & 0xFF) >= 64 || c.Number >> 8 >= 64)} of {chunks.Count})");
    Check(chunks.All(c => c.Number != 1 || c.Size != 0), "no chunk of theirs is the portals' key (chunk (1, 0) of size 0)");
    System.Console.WriteLine($"   the game's sectors: {total} chunks planned in {Seeds} worlds x 4 saves ({merged} merged); wide, past the game's: {chunks.Count} chunks planned in {far.Count} worlds x 4 saves "
      + $"({chunks.Count(c => c.Size > 0)} merged, {chunks.Count(c => (c.Number & 0xFF) >= 64 || c.Number >> 8 >= 64)} past the game's 64 x 64, {chunks.Sum(c => c.Ids.Count)} objects written)");

    // The zones that the game files in chunk (1, 0), the portals' key: here in chunk (159, 0).
    problems.Clear();
    {
      var fake = new Fake(2048, portalChunk: true);
      try
      {
        var inBlock = new List<ZDO>();
        uint id = 1;
        for (int zy = -256; zy <= -249; zy += 3)
          for (int zx = -248; zx <= -241; zx += 3)
            inBlock.Add(NewZdo(id++, zx * 64f + 5f, zy * 64f - 7f));
        var others = new List<ZDO> { NewZdo(id++, 100f, 100f), NewZdo(id++, -248f * 64f - 100f, -256f * 64f), NewZdo(id++, -241f * 64f + 100f, -256f * 64f), NewZdo(id++, -248f * 64f, -248f * 64f + 40f) };
        foreach (var zdo in inBlock.Concat(others))
          fake.Load(zdo);
        var plan = fake.Plan();
        CheckPlan(fake, plan, inBlock, "chunk (159, 0)", problems, requireAll: true);
        var moved = plan.Where(c => c.Number == 159 && c.Size == 0).ToList();
        Check(problems.Count == 0 && moved.Count == 1 && inBlock.All(z => moved[0].Ids.Contains(z.m_uid)) && moved[0].Ids.Count == inBlock.Count,
          $"objects in the zones -248 to -241 by -256 to -249 are in chunk (159, 0) and no other, and none is in the portals' chunk (1, 0) ({problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
        Check(plan.All(c => c.Number != 1), "no chunk of that plan is (1, 0)");
      }
      finally
      {
        fake.Close();
      }
    }

    // 4. The world's own sectors again: nothing of the wide sectors is left behind.
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), null);
    Check(!WorldSectors.Active && WorldSectors.HookList().All(h => h.Target == null || Harmony.GetPatchInfo(h.Target) is null or { Prefixes.Count: 0, Transpilers.Count: 0 }),
      "turned off, no hook is left on any game method");
    problems.Clear();
    var again = Scenario(1, 512, -256, 255, inner, problems);
    Check(problems.Count == 0 && Enumerable.Range(0, 4).All(r => SamePlan(again[r], game[0][r])), "and the game plans the same as it did before");
  }
}

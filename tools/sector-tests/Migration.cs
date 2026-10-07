// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// A world saved with the game's own sectors, loaded with the wide ones turned on (a machine with Wide Sectors On, or a world whose settings say it
// is wide): what the save filed in chunk (0, 0) beyond the game's sectors, and in chunk (1, 0), the portals', moves to chunks of its own, by
// WorldSectors.LoadedChunks. On a fake world folder that keeps the chunk files by version, as the game names them (yy_xx__size_version), and
// the game's own save planning, run as the game of this writing plans it and as an older one does (a chunk that is not in the list is
// written only if it is marked changed): every object comes back once, nothing is written over a file the saved chunk list still names, and
// the game is asked for a copy of the old save. Then what LoadedChunks leaves alone, and what it does when the wide sectors cannot be turned on.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // ------------------------------------------------------------------------------------------------ the save the game's sectors make

  // A world as the game's own sectors saved it (0.10.2): objects in its sectors, objects beyond them (all filed in chunk (0, 0)), the objects of
  // the zones the game files as its portals' chunk (1, 0), and what else lies in sector 0.
  private sealed class Legacy
  {
    public Folder Folder;
    public readonly List<ZDO> Inner = [], Far = [], Block = [], First = [], Beyond = [];
    // What moves out: the objects the wide sectors file in chunks of their own, and the ones read as portals.
    public IEnumerable<ZDO> Moved => Far.Concat(Block);
    public IEnumerable<ZDO> All => Inner.Concat(Far).Concat(First).Concat(Beyond).Concat(Block);
    public uint PileVersion => Folder.List.Get(new ZoneSystem.ChunkIndex(0, 0))?.m_version ?? 0u;
  }

  private static Legacy MakeLegacy(Harmony harmony, int seed, int saves, bool far, bool first, bool beyond, bool block)
  {
    WorldSectors.ModeOverride = WideSectorsMode.Auto;
    WorldSectors.SessionStarts(hosted: true);
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), null);
    FakeGame();
    var legacy = new Legacy();
    var rng = new System.Random(seed);
    // The 8 x 8 zones of chunk (0, 0) are kept free: First says whether anything is in them.
    legacy.Inner.AddRange(MakeWorld(rng, 1, 400, -256, 255, (x, y) => InsideGame(x, y) && !InMovedBlock(x, y) && !(x <= -249 && y <= -249)));
    if (far)
      legacy.Far.AddRange(MakeWorld(rng, 5000, 300, -600, 600, (x, y) => !InsideGame(x, y) && Mapped(x, y)));
    if (first)
    {
      legacy.First.Add(NewZdo(8000, -255f * 64f + 3f, -255f * 64f - 4f));
      legacy.First.Add(NewZdo(8001, -252f * 64f, -250f * 64f));
    }
    if (beyond)
    {
      // Past the wide map: sector 0 in the wide sectors too (zone 1500 is 96 km out; zone 15625 is where a dedicated server keeps its ghost zones).
      legacy.Beyond.Add(NewZdo(9000, 1500f * 64f, 20f));
      legacy.Beyond.Add(NewZdo(9001, 15625f * 64f, 15625f * 64f));
    }
    if (block)
    {
      uint id = 70000;
      for (int zy = -256; zy <= -249; zy += 2)
        for (int zx = -248; zx <= -241; zx += 2)
          legacy.Block.Add(NewZdo(id++, zx * 64f + 3f, zy * 64f + 4f));
    }
    // The game's sectors file the zones of the portals' chunk in chunk (1, 0) (a list that already named the portals' file would skip it unless it was marked changed).
    var game = new Fake(512);
    try
    {
      foreach (var zdo in legacy.All)
        game.Load(zdo);
      game.Save(game.Plan());
      // More saves of the same world: every one that changes something in sector 0 writes chunk (0, 0) again, under its next version.
      for (int i = 1; i < saves; i++)
      {
        game.Touch(legacy.Far.Count > 0 ? legacy.Far[0] : legacy.First.Count > 0 ? legacy.First[0] : legacy.Beyond[0]);
        game.Save(game.Plan());
      }
      legacy.Folder = game.Snapshot();
    }
    finally
    {
      game.Close();
    }
    return legacy;
  }

  // The game as it was before it wrote a chunk that is missing from the list: AddObjectsPerChunk skips every chunk that is not marked changed.
  private static bool AlwaysListed(Dictionary<ZoneSystem.ChunkIndex, ChunkSaveMapping.ChunkInfo> chunks, ZoneSystem.ChunkIndex index) => true;

  private static IEnumerable<CodeInstruction> OlderPlanner(IEnumerable<CodeInstruction> instructions)
  {
    int replaced = 0;
    foreach (var code in instructions)
    {
      if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && code.operand is MethodBase { Name: "ContainsKey" })
      {
        replaced++;
        var replacement = new CodeInstruction(OpCodes.Call, typeof(Program).GetMethod(nameof(AlwaysListed), BindingFlags.NonPublic | BindingFlags.Static));
        replacement.labels.AddRange(code.labels);
        replacement.blocks.AddRange(code.blocks);
        yield return replacement;
      }
      else
        yield return code;
    }
    if (replaced != 1)
      throw new InvalidOperationException($"AddObjectsPerChunk has {replaced} calls of ContainsKey, not 1");
  }

  // The plan, as the game of this writing makes it, or as an older one does.
  private static List<Chunk> PlanOf(Fake fake, bool older)
  {
    if (!older)
      return fake.Plan();
    var h = new Harmony("sector-tests.older");
    h.CreateProcessor(typeof(ZDOMan).GetMethod("AddObjectsPerChunk", Any)!).AddTranspiler(typeof(Program).GetMethod(nameof(OlderPlanner), BindingFlags.NonPublic | BindingFlags.Static)!).Patch();
    try
    {
      return fake.Plan();
    }
    finally
    {
      h.UnpatchAll("sector-tests.older");
    }
  }

  private static World FakeWorld(string name)
  {
    var world = Make<World>();
    world.m_worldName = name;
    world.m_name = name;
    SetStatic(typeof(ZNet), "m_world", world);
    return world;
  }

  // ------------------------------------------------------------------------------------------------ the conversion

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void MigrationTests(Harmony harmony)
  {
    Section("a save made with the game's sectors, converted: nothing lost, nothing twice, nothing written over what the saved list names");
    var none = (WorldGeometry)null;
    var first = new ZoneSystem.ChunkIndex(0, 0);
    var runs = new[]
    {
      // far objects, and chunk (0, 0) alone or with what stays in it; saved once (version 1: the name a removed entry would be rewritten under) and three times
      (Name: "far objects only", Far: true, First: false, Beyond: false, Block: false, Saves: 1),
      (Name: "far objects, saved three times", Far: true, First: false, Beyond: false, Block: false, Saves: 3),
      (Name: "far objects and an object in chunk (0, 0)'s zones", Far: true, First: true, Beyond: false, Block: false, Saves: 1),
      (Name: "far objects and an object in chunk (0, 0)'s zones, saved three times", Far: true, First: true, Beyond: false, Block: false, Saves: 3),
      (Name: "far objects and the server's ghost zones", Far: true, First: false, Beyond: true, Block: false, Saves: 1),
      (Name: "the zones of the portals' chunk", Far: false, First: false, Beyond: false, Block: true, Saves: 1),
      (Name: "all of it", Far: true, First: true, Beyond: true, Block: true, Saves: 2),
    };
    foreach (var older in new[] { false, true })
      foreach (var run in runs)
      {
        var label = $"{run.Name}, {(older ? "an older game's planning" : "the game's planning")}";
        try
        {
          Conversion(harmony, label, older, run.Far, run.First, run.Beyond, run.Block, run.Saves);
        }
        catch (Exception e)
        {
          Check(false, $"{label}: crashed: {CrashText(e)}");
          SetStatic(typeof(ZNet), "m_world", null);
          WorldSectors.SessionStarts(hosted: true);
          WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), null);
        }
      }
    MigrationControlTests(harmony);
  }

  // One conversion of a save made with the game's sectors, planned by the game of this writing or an older one.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void Conversion(Harmony harmony, string label, bool older, bool far, bool firstZones, bool beyond, bool block, int saves)
  {
    var none = (WorldGeometry)null;
    var first = new ZoneSystem.ChunkIndex(0, 0);
    var run = (Far: far, First: firstZones, Beyond: beyond, Block: block, Saves: saves);
    {
      {
        var legacy = MakeLegacy(harmony, 5 + run.Saves, run.Saves, run.Far, run.First, run.Beyond, run.Block);
        var problems = new List<string>();
        var moved = legacy.Moved.ToList();
        var oldPile = new FileKey(0, 0, legacy.PileVersion);

        // Without the conversion (the control): the far objects are in the save twice, and an older game's planning does not write them at all.
        WorldSectors.Update(harmony, Wide(), none);
        if (run.Far)
        {
          var control = Fake.LoadFrom(legacy.Folder, 2048);
          try
          {
            var plan = PlanOf(control, older);
            if (older)
            {
              var missed = new List<string>();
              CheckPlan(control, plan.Where(c => !(c.Number == 1 && c.Size == 0)).ToList(), legacy.Far, "control", missed, requireAll: false);
              Check(missed.Count > 0, $"{label}: without the conversion an older game's planning writes none of the {legacy.Far.Count} far objects (nothing marks them changed): {missed.Count} missing");
            }
            else
            {
              control.Save(plan);
              Check(control.DiskIds.Count != control.DiskIds.Distinct().Count(), $"{label}: without the conversion the far objects are in the save twice ({control.DiskIds.Count - control.DiskIds.Distinct().Count()} of {control.World.Count} objects are in two files)");
            }
          }
          finally
          {
            control.Close();
          }
        }

        // The conversion.
        var world = FakeWorld("TestWorld");
        var fake = Fake.LoadFrom(legacy.Folder, 2048);
        Fake reloaded = null;
        try
        {
          int at = Lines;
          WorldSectors.LoadedChunks(fake.Man);
          Check(Logged(at, $"{legacy.Far.Count} objects lie beyond the game's sectors, where the save filed them in chunk (0, 0), and {legacy.Block.Count} are of the zones", "Log"),
            $"{label}: the log counts {legacy.Far.Count} far objects and {legacy.Block.Count} of the portals' zones");
          Check(world.m_createBackupBeforeSaving && Logged(at, "converted to the wide ones", "Warning") && Logged(at, "'TestWorld_backup_<date and time>'", "Warning") && Logged(at, "Do not open or save the converted world", "Warning"),
            $"{label}: the game is asked to save a copy of the old save first, and the log warns, naming the copy and saying an older version must not save the world");
          Check(fake.Portals.Count == 0 && legacy.Block.All(z => fake.Sectors[ZoneSystem.GetSectorIndex(z.GetPosition()).Sector]?.Contains(z) == true),
            $"{label}: the objects read as portals are in their sectors, and no portal is left");

          // Every object that moves is marked changed, so that any game version writes its chunk (as 1.0.16's planning does: only a changed chunk).
          var unmarked = moved.Where(z => !fake.Dirty.Contains(ZoneSystem.GetZonesChunk(ZoneSystem.GetSectorIndex(z.GetPosition())))).ToList();
          Check(unmarked.Count == 0, $"{label}: the chunk of each of the {moved.Count} objects that move is marked changed ({unmarked.Count} are not)");
          bool listed = fake.Mapping.Chunks.ContainsKey(first);
          if (run.Far)
          {
            Check(run.First || run.Beyond ? listed && fake.Dirty.Contains(first) : !listed,
              $"{label}: chunk (0, 0) {(run.First || run.Beyond ? "stays in the chunk list, marked changed (something stays in it)" : "leaves the chunk list (nothing stays in it)")}");
          }
          else
            Check(listed == (legacy.Folder.List.Chunks.ContainsKey(first)) && !fake.Dirty.Contains(first), $"{label}: with no far object, chunk (0, 0) is left as it is");

          // The next save.
          var plan = PlanOf(fake, older);
          var portalPart = plan.Where(c => c.Number == 1 && c.Size == 0).ToList();
          CheckPlan(fake, plan.Where(c => !(c.Number == 1 && c.Size == 0)).ToList(), moved, label, problems, requireAll: false);
          Check(problems.Count == 0, $"{label}: every object that moves is in a chunk that reaches it, once, and no chunk is the portals' key ({problems.Count} problems{(problems.Count > 0 ? ": " + problems[0] : "")})");
          Check(run.Block ? portalPart.Count == 1 && portalPart[0].Ids.Count == 0 : portalPart.Count == 0, $"{label}: the portals' chunk is {(run.Block ? "written again, with no portal in it" : "not written")}");
          if (run.Block)
            Check(plan.Any(c => c.Number == 159 && c.Size == 0 && legacy.Block.All(z => c.Ids.Contains(z.m_uid))), $"{label}: the zones of the portals' chunk are chunk (159, 0)");
          fake.Save(plan);
          var disk = new List<string>();
          CheckDisk(fake, label, disk);
          Check(disk.Count == 0 && fake.DiskIds.Count == fake.World.Count, $"{label}: the save holds every object once ({fake.DiskIds.Count} of {fake.World.Count}; {disk.Count} problems{(disk.Count > 0 ? ": " + disk[0] : "")})");
          Check(fake.Overwrites == 0, $"{label}: no file the saved chunk list names is written over ({fake.Overwrites} are)");
          if (run.Far)
          {
            var after = fake.Committed.Get(first);
            Check(run.First || run.Beyond
                ? after != null && after.m_version == legacy.PileVersion + 1 && fake.Disk.ContainsKey(new FileKey(0, 0, legacy.PileVersion + 1)) && !fake.Disk.ContainsKey(oldPile)
                : after == null && fake.Disk.ContainsKey(oldPile) && fake.Listed.All(f => f.Number != 0 || f.Size != 0),
              run.First || run.Beyond ? $"{label}: chunk (0, 0) is written again as version {legacy.PileVersion + 1}, and the file of version {legacy.PileVersion} is gone"
                : $"{label}: chunk (0, 0) is not written: the list has none, and the old file is an orphan the next load removes");
            Check(legacy.Far.All(z => fake.Holds(z, file => !(file.Number == 0 && file.Size == 0))), $"{label}: every far object is in a chunk of its own, none in chunk (0, 0)");
          }
          Check(PlanOf(fake, older).Count == 0, $"{label}: and nothing is left to write");

          // A load of that save, the settings gone (Better Continents off for the world): its own chunks turn the wide sectors on while the chunk
          // list is read, before any object is placed, and nothing is converted again.
          var saved = fake.Snapshot();
          fake.Close();
          WorldSectors.SessionStarts(hosted: true);
          WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
          Check(!WorldSectors.Active, $"({label}: the next session, off until the save says otherwise)");
          List<ZoneSystem.ChunkIndex> chunksLoaded = null;
          reloaded = Fake.LoadFrom(saved, 512, f =>
          {
            chunksLoaded = f.Mapping.Chunks.Keys.ToList();
            WorldSectors.MappingLoaded(harmony, chunksLoaded);
          });
          Check(WorldSectors.Active && WorldSectors.ForcedBySave && reloaded.Sectors.Length == 2048 * 2048, $"{label}: its own chunks turn the wide sectors on, whatever the settings say, and its array is made wide");
          Check(reloaded.Disk.Keys.All(f => reloaded.Listed.Contains(f)), $"{label}: the load removes the orphans (files no chunk list names)");
          int at2 = Lines;
          WorldSectors.LoadedChunks(reloaded.Man);
          Check(!Logged(at2, "lie beyond") && !Logged(at2, "Sectors:", "Warning") && reloaded.ObjectsInSectors == reloaded.World.Count && reloaded.Portals.Count == 0 && reloaded.Dirty.Count == 0
                && reloaded.Mapping.Chunks.Count == chunksLoaded.Count && PlanOf(reloaded, older).Count == 0,
            $"{label}: loaded again, every object is in the world once ({reloaded.ObjectsInSectors} in sectors, {reloaded.World.Count} in the save), the chunk list and the changed chunks are untouched, nothing is said, nothing is left to write");
        }
        finally
        {
          fake.Close();
          reloaded?.Close();
          SetStatic(typeof(ZNet), "m_world", null);
          WorldSectors.SessionStarts(hosted: true);
          WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
        }
      }
    }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void MigrationControlTests(Harmony harmony)
  {
    var none = (WorldGeometry)null;
    var first = new ZoneSystem.ChunkIndex(0, 0);

    // The way chunk (0, 0) was first converted (taken out of the chunk list whatever else was in it) writes version 1 over the file the saved list names,
    // when the world was saved once: the fake's disk sees it, so the check above can fail.
    {
      var legacy = MakeLegacy(harmony, 6, 1, far: true, first: true, beyond: false, block: false);
      WorldSectors.Update(harmony, Wide(), none);
      var fake = Fake.LoadFrom(legacy.Folder, 2048);
      try
      {
        foreach (var zdo in legacy.Far)
          fake.Touch(zdo);
        fake.Mapping.Chunks.Remove(first);
        fake.Save(fake.Plan());
        Check(fake.Overwrites == 1 && legacy.PileVersion == 1, $"control: a chunk (0, 0) taken out of the list is written as version 1, over the file of the saved list ({fake.Overwrites} overwrites; the old file was version {legacy.PileVersion})");
      }
      finally
      {
        fake.Close();
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      }
    }

    // A real portal beside the objects of the portals' chunk stays a portal.
    {
      WorldSectors.Update(harmony, Wide(), none);
      var portalHash = "portal_wood".GetStableHashCode();
      FakeGame();
      GetStatic<Game>(typeof(Game), "<instance>k__BackingField").PortalPrefabHash.Add(portalHash);
      var portal = NewZdo(70500, 700f, 900f);
      FPrefabField.SetValue(portal, portalHash);
      var stray = NewZdo(70501, -244f * 64f, -252f * 64f);
      var fake = new Fake(2048);
      try
      {
        FakeWorld("TestWorld");
        fake.LoadAsPortal(portal);
        fake.LoadAsPortal(stray);
        WorldSectors.LoadedChunks(fake.Man);
        Check(fake.Portals.Values.SelectMany(l => l).SequenceEqual([portal]) && fake.Sectors[ZoneSystem.GetSectorIndex(stray.GetPosition()).Sector]?.Contains(stray) == true,
          "of an object that is a portal and one that is not, in the portals' chunk: the portal stays in the portal list, the other goes into its sector");
        Check(GetField<bool[]>(fake.Man, "m_dirtyPortalObjects")[0] && fake.Dirty.Contains(ZoneSystem.GetZonesChunk(ZoneSystem.GetSectorIndex(stray.GetPosition()))), "and the portals' chunk is written again, with the other's chunk marked changed");
      }
      finally
      {
        fake.Close();
        SetStatic(typeof(ZNet), "m_world", null);
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ what LoadedChunks leaves alone

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void GuardTests(Harmony harmony)
  {
    Section("what the load of a save leaves alone: no wide sectors, a save that has wide chunks, nothing to move, a world of the game's size");
    var none = (WorldGeometry)null;
    var first = new ZoneSystem.ChunkIndex(0, 0);

    // Which objects count as beyond the game's sectors: the zones of the wide map outside -256 to 255 on either axis, and nothing past the map (sector 0 in
    // both), nor the zones the map does not give a sector (the 64 at its south-east end).
    {
      WorldSectors.Update(harmony, Wide(), none);
      var fake = new Fake(2048);
      try
      {
        var zones = new[]
        {
          (255, 0, false), (256, 0, true), (-256, 0, false), (-257, 0, true), (0, 255, false), (0, 256, true), (0, -256, false), (0, -257, true),
          (-256, -256, false), (255, 255, false), (256, 256, true), (-257, -257, true), (1023, 1023, true), (-1024, -1024, true), (-1024, 1023, true),
          (1024, 0, false), (0, 1024, false), (-1025, 0, false), (0, -1025, false), (15625, 15625, false), (1020, -252, false), (1016, -256, false), (1015, -256, true),
        };
        uint id = 1;
        var made = zones.Select(z => (zdo: NewZdo(id++, z.Item1 * 64f + 5f, z.Item2 * 64f - 7f), far: z.Item3, z)).ToList();
        foreach (var (zdo, _, _) in made)
          fake.Load(zdo);
        var found = (List<ZDO>)typeof(WorldSectors).GetMethod("ObjectsBeyond", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fake.Man])!;
        var wrong = made.Where(m => found.Contains(m.zdo) != m.far).Select(m => $"({m.z.Item1}, {m.z.Item2}) is {(m.far ? "" : "not ")}beyond").ToList();
        Check(wrong.Count == 0 && found.Count == made.Count(m => m.far), $"which objects are beyond the game's sectors: zones outside -256 to 255 that the wide map reaches, not the ones past it or without a sector ({found.Count} found; wrong: {string.Join("; ", wrong)})");
      }
      finally
      {
        fake.Close();
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      }
    }

    // The wide sectors off: the save is the game's, it stays the game's, whatever lies beyond its sectors (the game files it all in sector 0).
    {
      var legacy = MakeLegacy(harmony, 11, 1, far: true, first: true, beyond: true, block: true);
      var world = FakeWorld("TestWorld");
      var fake = Fake.LoadFrom(legacy.Folder, 512);
      try
      {
        var listed = fake.Mapping.Chunks.Keys.ToList();
        int at = Lines;
        WorldSectors.LoadedChunks(fake.Man);
        Check(!WorldSectors.Active && fake.Mapping.Chunks.Keys.SequenceEqual(listed) && fake.Dirty.Count == 0 && fake.Portals.Values.Sum(l => l.Count) == legacy.Block.Count && !world.m_createBackupBeforeSaving
              && !Logged(at, "Sectors:"),
          "the wide sectors off (a world that needs none): the chunk list, the changed chunks and the portals are as they were, no copy is asked for, nothing is said");
      }
      finally
      {
        fake.Close();
        SetStatic(typeof(ZNet), "m_world", null);
      }
    }

    // A world of the game's size: sector 0 holds what a dedicated server keeps there, and chunk (0, 0) is in the list with it.
    {
      var legacy = MakeLegacy(harmony, 12, 1, far: false, first: false, beyond: true, block: false);
      var world = FakeWorld("TestWorld");
      foreach (var active in new[] { false, true })
      {
        WorldSectors.Update(harmony, active ? Wide() : new BC.BetterContinentsSettings(), none);
        var fake = Fake.LoadFrom(legacy.Folder, active ? 2048 : 512);
        try
        {
          var listed = fake.Mapping.Chunks.Keys.ToList();
          int at = Lines;
          WorldSectors.LoadedChunks(fake.Man);
          Check(listed.Contains(first) && fake.Mapping.Chunks.Keys.SequenceEqual(listed) && fake.Dirty.Count == 0 && !world.m_createBackupBeforeSaving && !Logged(at, "Sectors:") && PlanOf(fake, false).Count == 0,
            $"{(active ? "wide sectors on" : "the game's sectors")}, nothing beyond the game's sectors but the server's ghost zones: chunk (0, 0) stays in the list as it is, nothing is marked changed, no copy is asked for, nothing is said, nothing is written");
        }
        finally
        {
          fake.Close();
        }
      }
      SetStatic(typeof(ZNet), "m_world", null);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    // A save that has wide chunks, made by the wide sectors (a wide world, loaded again): nothing to convert, and nothing said, with its portals too.
    {
      WorldSectors.Update(harmony, Wide(), none);
      FakeGame();
      var portalHash = "portal_wood".GetStableHashCode();
      GetStatic<Game>(typeof(Game), "<instance>k__BackingField").PortalPrefabHash.Add(portalHash);
      var rng = new System.Random(13);
      var zdos = MakeWorld(rng, 1, 300, -600, 600, (x, y) => Mapped(x, y) && !(x >= -248 && x <= -241 && y >= -256 && y <= -249) && !(x >= -256 && x <= -249 && y >= -256 && y <= -249));
      // What the wide sectors write: the server's ghost zones in chunk (0, 0), the zones of the game's chunk (1, 0) in chunk (159, 0), the rest in chunks of their own.
      zdos.Add(NewZdo(9001, 15625f * 64f, 15625f * 64f));
      zdos.Add(NewZdo(9002, -255f * 64f, -255f * 64f));
      var blockObjects = new List<ZDO> { NewZdo(70000, -244f * 64f, -252f * 64f), NewZdo(70001, -243f * 64f + 5f, -250f * 64f) };
      var portal = NewZdo(70100, 3000f, 3000f);
      FPrefabField.SetValue(portal, portalHash);
      var made = new Fake(2048, portalChunk: true);
      Folder wide;
      try
      {
        foreach (var zdo in zdos.Concat(blockObjects))
          made.Load(zdo);
        made.LoadAsPortal(portal);
        made.Man.SetDirtyPortals();
        var plan = made.Plan();
        made.Save(plan);
        wide = made.Snapshot();
        Check(plan.Any(c => c.Number == 159 && c.Size == 0) && plan.Any(c => c.Number == 0 && c.Size == 0 && c.Ids.Count == 2) && plan.Any(c => c.Number == 1 && c.Size == 0 && c.Ids.Count == 1),
          "a wide world's save: chunk (159, 0) for the portals' zones, chunk (0, 0) for the ghost zones and its own zones, chunk (1, 0) for the portal");
      }
      finally
      {
        made.Close();
      }
      var world = FakeWorld("TestWorld");
      var fake = Fake.LoadFrom(wide, 2048);
      try
      {
        var listed = fake.Mapping.Chunks.Keys.ToList();
        int at = Lines;
        WorldSectors.LoadedChunks(fake.Man);
        Check(fake.Mapping.Chunks.Keys.SequenceEqual(listed) && fake.Mapping.Chunks.ContainsKey(first) && fake.Dirty.Count == 0 && !world.m_createBackupBeforeSaving && !Logged(at, "Sectors:")
              && fake.Portals.Values.SelectMany(l => l).SequenceEqual([portal]) && PlanOf(fake, false).Count == 0 && fake.ObjectsInSectors == zdos.Count + blockObjects.Count + 1,
          "a save that has wide chunks: its chunk list, (0, 0) too, the changed chunks and its portal are as they were, no copy is asked for, nothing is said, nothing is left to write");

        // What a version without the wide sectors writes over such a save: the objects of the portals' zones in chunk (1, 0) beside the portal ...
        var twins = Fake.LoadFrom(wide, 2048);
        try
        {
          twins.LoadAsPortal(blockObjects[0]);
          at = Lines;
          WorldSectors.LoadedChunks(twins.Man);
          Check(Logged(at, "the portals' chunk (1, 0) of this save holds 1 objects that are not portals", "Warning") && Logged(at, "they are in the world twice", "Warning") && twins.Dirty.Count == 0 && !world.m_createBackupBeforeSaving,
            "a wide save whose portals' chunk holds an object that is not a portal (an older version wrote it): the log warns, and nothing is changed");
        }
        finally
        {
          twins.Close();
        }

        // ... and, in chunk (0, 0), every object beyond 16.4 km: more in the file than lie in its zones.
        var pile = wide.List.Clone();
        var pileInfo = pile.Get(first);
        var ids = new List<ZDOID>(wide.Files[new FileKey(0, 0, pileInfo.m_version)]);
        var farIds = zdos.Where(z => !InsideGame(ZoneSystem.GetZone(z.GetPosition()).x, ZoneSystem.GetZone(z.GetPosition()).y) && Mapped(ZoneSystem.GetZone(z.GetPosition()).x, ZoneSystem.GetZone(z.GetPosition()).y)).Take(40).Select(z => z.m_uid).ToList();
        var piled = new Dictionary<FileKey, List<ZDOID>>(wide.Files) { [new FileKey(0, 0, pileInfo.m_version)] = [.. ids, .. farIds] };
        pileInfo.m_numZDOs = ids.Count + farIds.Count;
        var doubled = Fake.LoadFrom(new Folder(pile, piled, wide.World), 2048);
        try
        {
          at = Lines;
          WorldSectors.LoadedChunks(doubled.Man);
          Check(Logged(at, $"chunk (0, 0) of this save holds {ids.Count + farIds.Count} objects, but {ids.Count} of them lie in its own zones", "Warning") && Logged(at, $"the other {farIds.Count} are in the world twice", "Warning")
                && doubled.Dirty.Count == 0 && !world.m_createBackupBeforeSaving,
            $"a wide save whose chunk (0, 0) holds {farIds.Count} objects of other zones (an older version wrote them): the log warns, with the numbers, and nothing is changed");
        }
        finally
        {
          doubled.Close();
        }
      }
      finally
      {
        fake.Close();
        SetStatic(typeof(ZNet), "m_world", null);
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      }
    }

    // Switching off while a patch cannot be taken off: nothing is claimed, the wide sectors stay as they were, and the next ask tries again.
    {
      var owner = "sector-tests.session";
      WorldSectors.ModeOverride = WideSectorsMode.Auto;
      WorldSectors.SessionStarts(hosted: true);
      var fake = new Fake(512);
      try
      {
        WorldSectors.Update(harmony, Wide(), none);
        Check(WorldSectors.Active && fake.Sectors.Length == 2048 * 2048, "(wide sectors on, for a live ZDOMan)");
        WorldSectors.BeforeUnpatch = name =>
        {
          if (name == "ZDOMan.DecideChunkSize")
            throw new InvalidOperationException("this patch cannot be taken off");
        };
        int at = Lines;
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
        Check(WorldSectors.Active && AllHooksPatched(owner) && fake.Sectors.Length == 2048 * 2048
              && Logged(at, "could not take the patch off ZDOMan.DecideChunkSize: this patch cannot be taken off", "Error") && Logged(at, "the wide sectors could not be taken off, so they stay on until the next session", "Error")
              && !Logged(at, "the game's own sectors (zones -256 to 255)"),
          "a patch that cannot be taken off: the wide sectors stay on, all ten patched (the others put back), the array stays wide, the log says so and does not say they are off");
        WorldSectors.BeforeUnpatch = null;
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
        Check(!WorldSectors.Active && NoHookPatched(owner) && fake.Sectors.Length == 512 * 512, "and the next ask, with the patch free, takes them off and gives the game's array back");
      }
      finally
      {
        WorldSectors.BeforeUnpatch = null;
        fake.Close();
        WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ a save that needs the wide sectors, which cannot be turned on

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void FailClosedTests(Harmony harmony)
  {
    Section("a save made with the wide sectors whose patches cannot be installed: the world is not loaded, and nothing is saved over it");
    var owner = "sector-tests.session";
    var none = (WorldGeometry)null;
    var other = new Harmony("sector-tests.other");
    var decide = typeof(ZDOMan).GetMethod("DecideChunkSize", Any)!;
    var hadHarmony = GetStatic<Harmony>(typeof(BC), "HarmonyInstance");
    try
    {
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      FakeWorld("TestWorld");
      WorldSectors.ModeOverride = WideSectorsMode.Auto;

      // Another mod has changed one of the game's methods the wide sectors patch: all ten stay off, and a save with wide chunks is refused.
      other.CreateProcessor(decide).AddTranspiler(typeof(Program).GetMethod(nameof(Break64), Any)!).Patch();
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      int at = Lines;
      WorldSectors.MappingLoaded(harmony, WideChunks);
      Check(!WorldSectors.Active && NoHookPatched(owner) && ZNet.m_loadError, "the patches cannot be installed (another mod changed DecideChunkSize): none is left on, and the game's load error is set (it logs the player out without saving)");
      Check(BC.LastConnectionError is { } shown && shown.Contains("was saved with wide sectors") && shown.Contains("(159, 0)") && shown.Contains("'TestWorld'") && shown.Contains("not loaded"),
        "the error the player is shown says the world was saved with wide sectors, and which chunk says so");
      Check(Logged(at, "was saved with wide sectors", "Error") && Logged(at, "could not patch ZDOMan.DecideChunkSize", "Error") && !Logged(at, "the world runs on the game's own sectors"),
        "the log says the same as an error, after naming the patch that failed, and does not say the world runs on the game's sectors");
      // The same save with the patches free: loaded as before.
      other.UnpatchAll("sector-tests.other");
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.MappingLoaded(harmony, WideChunks);
      Check(WorldSectors.Active && AllHooksPatched(owner) && !ZNet.m_loadError && BC.LastConnectionError == null, "the same save with the patches free: the wide sectors are on, and nothing is refused");

      // A world that is only wide by its settings, with no wide chunk saved yet, loads on the game's sectors when the patches fail (nothing is in the save to put twice).
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      other.CreateProcessor(decide).AddTranspiler(typeof(Program).GetMethod(nameof(Break64), Any)!).Patch();
      at = Lines;
      WorldSectors.Update(harmony, Wide(), none);
      Check(!WorldSectors.Active && !ZNet.m_loadError && BC.LastConnectionError == null && Logged(at, "the world runs on the game's own sectors", "Error"),
        "a wide world whose save has no wide chunk, the patches failing: it runs on the game's sectors, with an error in the log, and is not refused");
      WorldSectors.MappingLoaded(harmony, [new ZoneSystem.ChunkIndex(0, 0), new ZoneSystem.ChunkIndex(1, 0), new ZoneSystem.ChunkIndex(63 | 63 << 8, 0)]);
      Check(!ZNet.m_loadError, "and a save with only the game's chunks is not refused either");
      other.UnpatchAll("sector-tests.other");

      // The save of a load that is refused: the game does not save a world with a load error (ZNet.Save: m_loadError), whichever way it is asked.
      var save = typeof(ZNet).GetMethod("Save", [typeof(bool), typeof(bool), typeof(bool)])!;
      Check(PatchProcessor.GetOriginalInstructions(save).Any(i => i.operand is System.Reflection.FieldInfo { Name: "m_loadError" }), "the game's ZNet.Save is the one that checks the load error (it skips the save)");
    }
    finally
    {
      other.UnpatchAll("sector-tests.other");
      ZNet.m_loadError = false;
      BC.LastConnectionError = null;
      SetStatic(typeof(ZNet), "m_world", null);
      SetStatic(typeof(BC), "HarmonyInstance", hadHarmony);
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
  }

  // ------------------------------------------------------------------------------------------------ the wiring

  private static bool CallsInIl(MethodBase method, string type, string name, Func<List<CodeInstruction>, int, bool> argument = null)
  {
    var codes = PatchProcessor.GetOriginalInstructions(method);
    for (int i = 0; i < codes.Count; i++)
      if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) && codes[i].operand is MethodBase { Name: var called } m && called == name && m.DeclaringType?.Name == type
          && (argument == null || argument(codes, i)))
        return true;
    return false;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void CallSiteTests(Harmony harmony)
  {
    Section("the wiring: the calls that reach WorldSectors, and the two patches on the game's load");
    var none = (WorldGeometry)null;
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    var znetPatch = typeof(BC).GetNestedType("ZNetPatch", all)!;
    var fejdPatch = typeof(BC).GetNestedType("FejdStartupPatch", all)!;
    var worldPatch = typeof(BC).GetNestedType("WorldPatch", all)!;
    var settingsType = typeof(BC.BetterContinentsSettings);

    // DynamicPatch asks for the sectors; a session starts when ZNet.SetServer does (with its server flag: who runs the world) and at the main menu (nobody does).
    Check(CallsInIl(typeof(BC).GetMethod("DynamicPatch", all)!, "WorldSectors", "Update"), "BetterContinents.DynamicPatch calls WorldSectors.Update");
    Check(CallsInIl(znetPatch.GetMethod("SetServerPrefix", all)!, "WorldSectors", "SessionStarts", (codes, i) => codes[i - 1].opcode == OpCodes.Ldarg_0),
      "ZNet.SetServer's prefix starts the session with its server flag (WorldSectors.SessionStarts(server))");
    Check(CallsInIl(fejdPatch.GetMethod("AwakePrefix", all)!, "WorldSectors", "SessionStarts", (codes, i) => codes[i - 1].opcode == OpCodes.Ldc_I4_0),
      "FejdStartup.Awake's prefix starts a session that nobody runs (WorldSectors.SessionStarts(false))");
    // A new world takes the choice: From Config (and so an import's preset and a Directory's export.cfg), and a preset file's world when it is made.
    Check(CallsInIl(settingsType.GetMethod("FromConfig", all)!, "WorldSectors", "NewWorld"), "BetterContinentsSettings.FromConfig calls WorldSectors.NewWorld");
    Check(CallsInIl(worldPatch.GetMethod("SaveWorldFWLDataPostfix", all)!, "WorldSectors", "NewWorld"), "the postfix that saves a new world's settings calls WorldSectors.NewWorld for the preset it takes");

    // The two patches on the game's load: bound to the right methods, and doing what they are for.
    var mappingPatch = typeof(WorldSectors).GetNestedType("MappingLoadPatch", all)!;
    var chunksPatch = typeof(WorldSectors).GetNestedType("LoadChunksPatch", all)!;
    var attributes = new Harmony("sector-tests.attributes");
    var hadHarmony = GetStatic<Harmony>(typeof(BC), "HarmonyInstance");
    try
    {
      attributes.CreateClassProcessor(mappingPatch).Patch();
      attributes.CreateClassProcessor(chunksPatch).Patch();
      bool Postfixed(MethodBase target, Type patch) => Harmony.GetPatchInfo(target) is { } info && info.Postfixes.Any(p => p.owner == "sector-tests.attributes" && p.PatchMethod.DeclaringType == patch);
      Check(Postfixed(typeof(ChunkSaveMapping).GetMethod("Load")!, mappingPatch) && Postfixed(typeof(ZDOMan).GetMethod("LoadChunks")!, chunksPatch),
        "the patch classes bind: a postfix on ChunkSaveMapping.Load and one on ZDOMan.LoadChunks");

      // ChunkSaveMapping.Load's postfix: a chunk list with a wide chunk turns the wide sectors on.
      SetStatic(typeof(BC), "HarmonyInstance", harmony);
      WorldSectors.ModeOverride = WideSectorsMode.Auto;
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      var list = new ChunkSaveMapping();
      list.CreateOrUpdate(new ZoneSystem.ChunkIndex(5 | 7 << 8, 0));
      mappingPatch.GetMethod("Postfix", all)!.Invoke(null, [list]);
      Check(!WorldSectors.Active, "the postfix on ChunkSaveMapping.Load leaves a chunk list inside the game's 64 x 64 alone");
      list.CreateOrUpdate(new ZoneSystem.ChunkIndex(159, 0));
      mappingPatch.GetMethod("Postfix", all)!.Invoke(null, [list]);
      Check(WorldSectors.Active && WorldSectors.ForcedBySave, "and turns the wide sectors on for one that has a chunk (159, 0)");

      // ZDOMan.LoadChunks's postfix: converts a save made with the game's sectors.
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      var legacy = MakeLegacy(harmony, 21, 1, far: true, first: true, beyond: false, block: false);
      WorldSectors.Update(harmony, Wide(), none);
      var world = FakeWorld("TestWorld");
      var fake = Fake.LoadFrom(legacy.Folder, 2048);
      try
      {
        chunksPatch.GetMethod("Postfix", all)!.Invoke(null, [fake.Man]);
        Check(world.m_createBackupBeforeSaving && fake.Dirty.Contains(new ZoneSystem.ChunkIndex(0, 0)) && legacy.Far.All(z => fake.Dirty.Contains(ZoneSystem.GetZonesChunk(ZoneSystem.GetSectorIndex(z.GetPosition())))),
          "the postfix on ZDOMan.LoadChunks converts it: the far objects' chunks are marked changed and the game is asked for a copy");
      }
      finally
      {
        fake.Close();
        SetStatic(typeof(ZNet), "m_world", null);
      }
    }
    finally
    {
      attributes.UnpatchAll("sector-tests.attributes");
      SetStatic(typeof(BC), "HarmonyInstance", hadHarmony);
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }
  }
}

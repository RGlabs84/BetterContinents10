// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// When the wide sectors are on (a world's settings, a machine's Wide Sectors, the chunks of a save), switching them on and off with
// Harmony, one failed hook, the sector array's width, the save's chunks that force them, zone regeneration on them, and
// Utils.SmallPosition. Modes.cs: the Wide Sectors setting. Migration.cs: a save made with the game's sectors.

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
  private static BC.BetterContinentsSettings World(bool enabled, float size, float edge, int version = 12, bool wide = false) =>
    new() { EnabledForThisWorld = enabled, Version = version, WorldSize = size, EdgeSize = edge, WideSectors = wide };

  // A world made wide (or converted): its settings say so.
  private static BC.BetterContinentsSettings Wide(float size = 24000f, float edge = 500f, int version = 12) => World(true, size, edge, version, wide: true);

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
    WorldSectors.ModeOverride = WideSectorsMode.Auto;
    WorldSectors.SessionStarts(hosted: true);

    // The world's size: the game's sectors reach 16,352 m east and 16,416 m west. (A wide world here: its settings say so; Modes.cs has
    // the rest of what decides.)
    var none = (WorldGeometry)null;
    bool Wanted(BC.BetterContinentsSettings settings, WorldGeometry ews = null) => WorldSectors.Wanted(settings, ews, WideSectorsMode.Auto, hosted: true);
    Check(!Wanted(Wide(15850f, 500f)) && Wanted(Wide(15851f, 500f)), "World Size + Edge Size 16350 m is the game's own reach, 16351 m is past it");
    Check(!Wanted(Wide(10000f, 500f)) && !Wanted(new BC.BetterContinentsSettings()), "vanilla's size, and Better Continents off, are not");
    Check(Wanted(Wide(24000f, 500f)) && Wanted(Wide(64500f, 500f)) && Wanted(Wide(200000f, 500f)), "24500 m, 65000 m and 200000 m are");
    Check(!Wanted(World(false, 24000f, 500f, wide: true)), "Better Continents off for the world: the game's own, whatever its size");
    Check(Wanted(Wide(24000f, 500f, version: 11)) && Wanted(Wide(24000f, 500f, version: 6)),
      "a world made by an older Better Continents is the same, once its settings say it is wide (converted: its far objects were in the one list)");
    Check(!Wanted(Wide(float.NaN, 500f)) && !Wanted(Wide(0f, 0f)) && !Wanted(Wide(-20000f, 500f)), "a size that makes no world (nothing, NaN, negative) is not");
    Check(Wanted(Wide(10000f, 500f), new WorldGeometry(20000f, 500f)) && !Wanted(Wide(10000f, 500f), new WorldGeometry(15000f, 500f)),
      "Expand World Size's size counts, when it has sent one: 20500 m is past the game's, 15500 m is not");
    Check(Wanted(Wide(20000f, 500f), new WorldGeometry(10000f, 500f)), "and the world's own, when Expand World Size's is smaller");
    Check(!Wanted(World(true, 24000f, 500f)) && !Wanted(World(true, 24000f, 500f, version: 11)), "a world whose settings do not say it is wide is not, whatever its size (Auto)");

    // Switching on and off.
    int mark = CapturingLogHandler.Lines.Count;
    WorldSectors.Update(harmony, Wide(10000f), none);
    Check(!WorldSectors.Active && NoHookPatched(owner), "a vanilla-size world: nothing is patched");
    WorldSectors.Update(harmony, Wide(), none);
    Check(WorldSectors.Active && AllHooksPatched(owner), "a world of 24500 m: all ten hooks are patched");
    Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("Sectors: every zone from -1024 to 1023") && l.Contains("32 MB")), "and the log says so");
    mark = CapturingLogHandler.Lines.Count;
    WorldSectors.Update(harmony, Wide(), none);
    Check(CapturingLogHandler.Lines.Count == mark, "asked again: nothing happens, nothing is logged");
    WorldSectors.Update(harmony, Wide(30000f), none);
    Check(WorldSectors.Active && AllHooksPatched(owner), "another size that needs them: still on");
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    Check(!WorldSectors.Active && NoHookPatched(owner), "back to the main menu: off, and no hook is left");
    // A world that reaches past the map's own 65,504 m is warned about, once; one that does not, is not.
    mark = CapturingLogHandler.Lines.Count;
    WorldSectors.Update(harmony, Wide(64000f), none);
    Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("reaches past 65000 m")), "a world of 64500 m is not warned about");
    WorldSectors.Update(harmony, Wide(70000f), none);
    WorldSectors.Update(harmony, Wide(70000f), none);
    Check(CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains("this world reaches past 65000 m. Zones out to 65,504 m have their own sectors")) == 1, "a world of 70500 m is, once: its zones out to 65,504 m have sectors, the rest share one");
    WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);

    // What ZDOMan's array is: ResetSectorArray's width.
    {
      var man = Make<ZDOMan>();
      SetField(man, "m_width", 512);
      var reset = typeof(ZDOMan).GetMethod("ResetSectorArray", Any)!;
      reset.Invoke(man, null);
      Check(GetField<List<ZDO>[]>(man, "m_objectsBySector").Length == 512 * 512, "the game's own ResetSectorArray: 512 x 512 sectors");
      WorldSectors.Update(harmony, Wide(), none);
      reset.Invoke(man, null);
      Check(GetField<List<ZDO>[]>(man, "m_objectsBySector").Length == 2048 * 2048, "patched: 2048 x 2048 sectors, whatever width the ZDOMan was made with");
      // The game's own functions now answer for the wide sectors.
      var sectorToIndex = typeof(ZoneSystem).GetMethod("SectorToIndex", [typeof(int), typeof(int)])!;
      var asked = (ZoneSystem.SectorIndex)sectorToIndex.Invoke(null, [300, -300])!;
      Check(asked.Sector == WorldSectors.SectorMap.SectorToIndex(300, -300) && asked.Sector != 0, "ZoneSystem.SectorToIndex answers the wide map for zone (300, -300)");
      // ... and the way back, which nothing in the game calls but other mods may: a sector is a zone again, the zones of the moved block included.
      var indexToSector = typeof(ZoneSystem).GetMethod("IndexToSector")!;
      var back = new[] { (300, -300), (-1024, 1023), (1023, -1024), (0, 0), (-256, -256), (-248, -256), (-241, -249), (255, 255), (256, 256) }
        .Select(z => (Zone: z, Back: (Vector2s)indexToSector.Invoke(null, [WorldSectors.SectorMap.SectorToIndex(z.Item1, z.Item2)])!)).ToList();
      Check(back.All(b => b.Back.x == b.Zone.Item1 && b.Back.y == b.Zone.Item2), $"ZoneSystem.IndexToSector gives the zone back, with the wide sectors on ({string.Join(", ", back.Where(b => b.Back.x != b.Zone.Item1 || b.Back.y != b.Zone.Item2).Select(b => b.Zone))})");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      asked = (ZoneSystem.SectorIndex)sectorToIndex.Invoke(null, [300, -300])!;
      Check(asked.Sector == 0, "and the game's own again when off: zone (300, -300) is in sector 0");
      var own = (Vector2s)indexToSector.Invoke(null, [(uint)(5 * 512 + 7)])!;
      Check(own.x == 7 - 256 && own.y == 5 - 256, "ZoneSystem.IndexToSector is the game's own when off (sector 5 * 512 + 7 is zone (-249, -251))");
    }

    // A live ZDOMan: its objects stay where they are.
    {
      var fake = new Fake(512);
      try
      {
        mark = CapturingLogHandler.Lines.Count;
        fake.Load(NewZdo(1, 100f, 100f));
        WorldSectors.Update(harmony, Wide(), none);
        Check(!WorldSectors.Active && NoHookPatched(owner) && fake.Sectors.Length == 512 * 512, "a ZDOMan that holds objects keeps the sectors it has: not switched on");
        WorldSectors.Update(harmony, Wide(), none);
        Check(CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains("the sectors stay as they are")) == 1, "and the log says why, once");
        fake.Destroy(fake.World.Values.First());
        WorldSectors.Update(harmony, Wide(), none);
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
      WorldSectors.SessionStarts(hosted: true);
      var game = new[] { new ZoneSystem.ChunkIndex(0, 0), new ZoneSystem.ChunkIndex(1, 0), new ZoneSystem.ChunkIndex(63 | 63 << 8, 0), new ZoneSystem.ChunkIndex(32 | 5 << 8, 3) };
      WorldSectors.MappingLoaded(harmony, game);
      Check(!WorldSectors.Active && !WorldSectors.ForcedBySave, "a save whose chunks are all inside the game's 64 x 64: nothing changes");
      mark = CapturingLogHandler.Lines.Count;
      WorldSectors.MappingLoaded(harmony, game.Append(new ZoneSystem.ChunkIndex(64 | 3 << 8, 0)));
      Check(WorldSectors.Active && WorldSectors.ForcedBySave && AllHooksPatched(owner), "a save with a chunk (64, 3): the wide sectors are on, whatever the world's settings say");
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("a chunk (64, 3) beyond the game's 64 x 64")), "and the log names the chunk");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      Check(WorldSectors.Active, "asked for the game's by settings that say nothing is needed: still on, the save says otherwise");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
      Check(!WorldSectors.Active && !WorldSectors.ForcedBySave && NoHookPatched(owner), "the next session starts from its own settings");
      WorldSectors.MappingLoaded(harmony, [new ZoneSystem.ChunkIndex(0 | 200 << 8, 0)]);
      Check(WorldSectors.Active, "a chunk (0, 200) is beyond too");
      WorldSectors.SessionStarts(hosted: true);
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    // One hook that cannot be patched: all of them are left off.
    {
      var other = new Harmony("sector-tests.other");
      var decide = typeof(ZDOMan).GetMethod("DecideChunkSize", Any)!;
      other.CreateProcessor(decide).AddTranspiler(typeof(Program).GetMethod(nameof(Break64), Any)!).Patch();
      mark = CapturingLogHandler.Lines.Count;
      WorldSectors.Update(harmony, Wide(), none);
      Check(!WorldSectors.Active && NoHookPatched(owner), "another mod's transpiler has changed DecideChunkSize: no hook is left on any game method, the sectors stay the game's");
      Check(CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("[Error]") && l.Contains("could not patch ZDOMan.DecideChunkSize"))
            && CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("[Error]") && l.Contains("the world runs on the game's own sectors") && l.Contains("objects beyond 16.4 km share one sector")),
        "and the log says which, and what stays");
      other.UnpatchAll("sector-tests.other");
      WorldSectors.Update(harmony, Wide(), none);
      Check(WorldSectors.Active && AllHooksPatched(owner), "with that gone, the next ask patches all of them");
      WorldSectors.Update(harmony, new BC.BetterContinentsSettings(), none);
    }

    Guarded("ModeTests", () => ModeTests(harmony));
    Guarded("MigrationTests", () => MigrationTests(harmony));
    Guarded("GuardTests", () => GuardTests(harmony));
    Guarded("FailClosedTests", () => FailClosedTests(harmony));
    Guarded("CallSiteTests", () => CallSiteTests(harmony));
    Guarded("ZoneRegenTests", () => ZoneRegenTests(harmony));
    Guarded("SmallPositionTests", () => SmallPositionTests(harmony));
  }

  // ------------------------------------------------------------------------------------------------ Better Continents' zone regeneration

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ZoneRegenTests(Harmony harmony)
  {
    Section("zone regeneration finds a far zone's objects in the wide sectors");
    var none = (WorldGeometry)null;
    WorldSectors.Update(harmony, Wide(), none);
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

  private static void mark(out int index) => index = CapturingLogHandler.Lines.Count;
  private static int Lines => CapturingLogHandler.Lines.Count;
  private static bool Logged(int since, string contains, string level = null) =>
    CapturingLogHandler.Lines.Skip(since).Any(l => l.Contains(contains) && (level == null || l.StartsWith("[" + level + "]")));

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
    WorldSectors.Update(harmony, Wide(), none);
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
      Check(back.All(b => b.Was == b.Is), $"ZDO.Save then ZDO.Load gives the position back exactly, out to 65 km ({string.Join("; ", back.Where(b => b.Was != b.Is).Select(b => $"{At(b.Was)} came back as {At(b.Is)}"))})");
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

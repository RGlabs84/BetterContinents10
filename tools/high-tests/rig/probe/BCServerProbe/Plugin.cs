// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// BCServerProbe: a test-only BepInEx plugin for a headless Valheim dedicated server (2026-10-05, VALtima era-1 4 m dry run).
// Inert unless BCPROBE_OUT is set. Writes only under BCPROBE_OUT and takes no commands from anywhere.
//
// A dedicated server cannot make a Better Continents world (README: create it in the game client). For a headless dry run the
// probe does on the server what the client's New World button does: the new world's seed is BCPROBE_SEED (World.GenerateSeed),
// and Better Continents' own "world being created" flag is raised around World.GetCreateWorld's create branch, as
// FejdStartup.OnNewWorldDone raises it in the client, so Better Continents bakes the world's settings from its config through
// its own code (WorldPatch.SaveWorldFWLDataPostfix -> Presets.LoadActivePreset). An existing world is only loaded.
//
// Once the locations are generated it records: every location instance (locations.tsv), the enabled location entries
// (location-entries.tsv), the height and biome WorldGenerator gives at the points in BCPROBE_POINTS (points-out.tsv), a grid
// of heights and biomes over the land (grid.bin; BCPROBE_GRID=0 skips it), Better Continents' active biome precision, and
// memory (VmRSS, VmHWM) and times at each step (probe.log). BCPROBE_QUIT=1 quits the server when done (a normal shutdown,
// which saves the world).
//
// Towns (2026-10-06, VALtima towns dry run): BCPROBE_TOWNS names a file of "x<TAB>z<TAB>payload" zone lines. Each zone is
// generated the way a dedicated server generates the zones around a player (ZoneSystem.CreateGhostZones: SpawnZone in Ghost
// mode), a few at a time so that the terrain builder's ready queue (16 entries) never drops one before it is used. Then it
// records every VALtima_TownChunk ZDO in the world (towns-chunks.tsv, towns-chunks.bin with each payload), the zones' terrain
// as the game builds it (HeightmapBuilder.RequestTerrainSync: the heights a zone's Heightmap and collision mesh are made from;
// towns-terrain.bin), each zone's object and terrain-modifier counts (towns-zones.tsv) and the world folder's files.
// Pack v3 rerun (2026-10-06 evening): also every object the server placed in the listed zones, with its prefab name and
// position (towns-objects.tsv), to compare with the ground VALtimaOnline 0.0.12 gives the clients there.
// VALtimaOnline 0.0.14 (2026-10-06 night): every seeded town station in the world (ZDO bool VALtima_TownPiece; towns-stations.tsv), every
// town NPC (ZDO string VALtima_NpcId, or an NPC prefab; towns-npcs.tsv), and each chunk's count of seeded stations (VALtima_TownUsables).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BCServerProbe
{
  [BepInPlugin("wubarrk.bcserverprobe", "BC Server Probe", "1.0.0")]
  [BepInDependency("BetterContinents")]
  public class Plugin : BaseUnityPlugin
  {
    static string Out, Seed, Points, Towns;
    static bool Quit, DoGrid;
    static float TownsTimeout = 1800f;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static FieldInfo beingCreated;

    void Awake()
    {
      Out = Environment.GetEnvironmentVariable("BCPROBE_OUT");
      if (string.IsNullOrEmpty(Out))
      {
        Logger.LogInfo("BCServerProbe is inert: BCPROBE_OUT is not set.");
        return;
      }
      Seed = Environment.GetEnvironmentVariable("BCPROBE_SEED");
      Points = Environment.GetEnvironmentVariable("BCPROBE_POINTS");
      Quit = Environment.GetEnvironmentVariable("BCPROBE_QUIT") == "1";
      DoGrid = Environment.GetEnvironmentVariable("BCPROBE_GRID") != "0";
      Towns = Environment.GetEnvironmentVariable("BCPROBE_TOWNS");
      if (float.TryParse(Environment.GetEnvironmentVariable("BCPROBE_TOWNS_TIMEOUT"), NumberStyles.Float, IC, out var tt))
        TownsTimeout = tt;
      Directory.CreateDirectory(Out);
      var worldPatch = AccessTools.TypeByName("BetterContinents.BetterContinents+WorldPatch");
      beingCreated = worldPatch == null ? null : AccessTools.Field(worldPatch, "bWorldBeingCreated");
      Log($"awake: seed '{Seed}', points '{Points}', grid {DoGrid}, towns '{Towns}', quit {Quit}; Better Continents' creation flag "
          + (beingCreated != null ? "found" : "MISSING") + $"; {Mem()}");
      var harmony = new Harmony("wubarrk.bcserverprobe");
      harmony.PatchAll(typeof(Plugin).Assembly);
      if (Environment.GetEnvironmentVariable("BCPROBE_HIGH_CONTROL") == "1")
      {
        // Better Continents' high-terrain patches never switched on (its DynamicPatch step skipped): the game as it is without them.
        var bcType = AccessTools.TypeByName("BetterContinents.BetterContinents");
        var step = AccessTools.Method(bcType, "PatchHighTerrain");
        harmony.CreateProcessor(step).AddPrefix(new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SkipPatchHighTerrain)))).Patch();
        Log("control: BetterContinents.PatchHighTerrain is skipped, so no high-terrain patch is ever applied");
      }
      StartCoroutine(Main());
    }

    // ------------------------------------------------------------------------------------------ logging

    static string F(float v) => v.ToString("0.###", IC);

    internal static void Log(string s)
    {
      var line = $"[{Time.realtimeSinceStartup.ToString("0.0", IC),7} s | {DateTime.Now:HH:mm:ss.fff}] {s}";
      Debug.Log("[BCServerProbe] " + s);
      try { File.AppendAllText(Path.Combine(Out, "probe.log"), line + "\n"); } catch { }
    }

    internal static string Mem()
    {
      string rss = "?", hwm = "?";
      try
      {
        foreach (var l in File.ReadAllLines("/proc/self/status"))
        {
          if (l.StartsWith("VmRSS:")) rss = l.Substring(6).Trim();
          else if (l.StartsWith("VmHWM:")) hwm = l.Substring(6).Trim();
        }
      }
      catch { }
      return $"rss {rss}, peak {hwm}, managed {GC.GetTotalMemory(false) / 1048576} MB";
    }

    // ------------------------------------------------------------------------------------------ creating the world

    [HarmonyPatch(typeof(World), nameof(World.GenerateSeed))]
    static class SeedPatch
    {
      static bool Prefix(ref string __result)
      {
        if (string.IsNullOrEmpty(Seed))
          return true;
        __result = Seed;
        Log($"World.GenerateSeed -> '{Seed}'");
        return false;
      }
    }

    [HarmonyPatch(typeof(World), nameof(World.GetCreateWorld))]
    static class CreatePatch
    {
      static void Prefix(string name)
      {
        if (SaveSystem.TryGetSaveByName(name, SaveDataType.World, out var save) && !save.IsDeleted)
        {
          Log($"World.GetCreateWorld('{name}'): the world exists, so it is loaded; {Mem()}");
          return;
        }
        if (beingCreated == null)
        {
          Log($"World.GetCreateWorld('{name}'): Better Continents' creation flag is missing: the world is made WITHOUT Better Continents");
          return;
        }
        beingCreated.SetValue(null, true);
        Log($"World.GetCreateWorld('{name}'): a new world: raised Better Continents' creation flag, as the client's New World does; {Mem()}");
      }

      static void Postfix(World __result)
      {
        if (beingCreated != null && (bool)beingCreated.GetValue(null))
        {
          beingCreated.SetValue(null, false);
          Log("WARNING: Better Continents did not take the creation flag: no settings were baked");
        }
        Log($"World.GetCreateWorld -> '{__result?.m_name}', seed '{__result?.m_seedName}' ({__result?.m_seed}); {Mem()}");
      }
    }

    // ------------------------------------------------------------------------------------------ the checks

    IEnumerator Main()
    {
      bool znet = false, worldGen = false, zoneSystem = false;
      float lastNote = 0f;
      while (true)
      {
        if (!znet && ZNet.instance) { znet = true; Log($"ZNet up; {Mem()}"); }
        if (!worldGen && WorldGenerator.instance != null) { worldGen = true; Log($"WorldGenerator up; {Mem()}"); }
        if (!zoneSystem && ZoneSystem.instance) { zoneSystem = true; Log($"ZoneSystem up; {Mem()}"); }
        if (ZoneSystem.instance && ZoneSystem.instance.LocationsGenerated)
          break;
        if (Time.realtimeSinceStartup - lastNote > 10f)
        {
          lastNote = Time.realtimeSinceStartup;
          Log($"waiting: locations {(ZoneSystem.instance ? ZoneSystem.instance.GenerateLocationsProgress : 0f).ToString("0.00", IC)}; {Mem()}");
        }
        yield return new WaitForSecondsRealtime(0.25f);
      }
      Log($"locations generated; {Mem()}");
      yield return null;
      if (Environment.GetEnvironmentVariable("BCPROBE_INLINE") == "1") Try("inline", InlineTest.Run);
      if (Environment.GetEnvironmentVariable("BCPROBE_DUMPDATA") == "1") Try("dumpdata", DumpData);
      Try("settings", Settings);
      Try("locations", Locations);
      Try("points", SamplePoints);
      if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BCPROBE_BOXES"))) Try("boxes", Boxes);
      if (Environment.GetEnvironmentVariable("BCPROBE_PERLIN") == "1") Try("perlin", PerlinStats);
      if (DoGrid)
        yield return StartCoroutine(Grid());
      if (Environment.GetEnvironmentVariable("BCPROBE_HIGH") == "1")
        yield return StartCoroutine(HighRun());
      if (Environment.GetEnvironmentVariable("BCPROBE_DUNGEONS") != null)
        yield return StartCoroutine(DungeonRun());
      if (!string.IsNullOrEmpty(Towns))
        yield return StartCoroutine(TownsRun());
      if (Environment.GetEnvironmentVariable("BCPROBE_HIGH_TOGGLE") == "1")
        yield return StartCoroutine(HighToggleRun());
      Log($"done; {Mem()}");
      try { File.WriteAllText(Path.Combine(Out, "done"), "done\n"); } catch { }
      if (Quit)
      {
        Log("quitting (a normal shutdown, which saves the world)");
        Application.Quit();
      }
    }

    // Whether a Harmony prefix on a tiny static game method (Character.InInterior(Vector3), 14 bytes of IL) is seen by callers that
    // were compiled before the patch, and by callers compiled after it (Mono inlines small methods).
    static class InlineTest
    {
      public static int Count;
      static bool Prefix(ref bool __result) { Count++; __result = true; return false; }
      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
      static bool Early(Vector3 v) => Character.InInterior(v);
      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
      static bool Late(Vector3 v) => Character.InInterior(v);
      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
      static bool LateT(Transform t) => Character.InInterior(t);
      public static void Run()
      {
        var p = new Vector3(0, 50, 0);
        bool a0 = Early(p);                                   // JIT-compiled before the patch
        var loc0 = Location.GetLocation(p);                   // a game method that calls InInterior(Vector3), compiled before the patch
        var t = new GameObject("inlinetest").transform; t.position = p;
        bool t0 = Character.InInterior(t);
        Log($"inline test before patch: Early {a0}, Location.GetLocation {(loc0 == null ? "null" : "loc")}, InInterior(Transform) {t0}, count {Count}");
        var h = new Harmony("wubarrk.bcserverprobe.inline");
        var target = AccessTools.Method(typeof(Character), "InInterior", new[] { typeof(Vector3) });
        h.CreateProcessor(target).AddPrefix(new HarmonyMethod(AccessTools.Method(typeof(InlineTest), nameof(Prefix)))).Patch();
        int c0 = Count;
        bool a1 = Early(p); int c1 = Count;
        bool t1 = Character.InInterior(t); int c2 = Count;
        bool l1 = Late(p); int c3 = Count;
        bool lt = LateT(t); int c4 = Count;
        Location loc1 = null; string err = "";
        try { loc1 = Location.GetLocation(p); } catch (Exception e) { err = e.GetType().Name; }
        int c5 = Count;
        Log($"inline test after patch: Early (compiled before) hit {c1 - c0} -> {a1}; InInterior(Transform) from here hit {c2 - c1} -> {t1}; Late (compiled after) hit {c3 - c2} -> {l1}; "
            + $"LateT hit {c4 - c3} -> {lt}; Location.GetLocation (compiled before) hit {c5 - c4} {err}");
        h.UnpatchSelf();
        Log($"inline test: unpatched, InInterior(p) = {Character.InInterior(p)}");
      }
    }

    // The altitude limits of the game's vegetation and spawn entries (high-terrain stream, 2026-10-06).
    static void DumpData()
    {
      var zs = ZoneSystem.instance;
      var sb = new StringBuilder("name\tbiome\tmin_alt\tmax_alt\tenable\n");
      foreach (var v in zs.m_vegetation)
        sb.Append($"{v.m_name}\t{v.m_biome}\t{F(v.m_minAltitude)}\t{F(v.m_maxAltitude)}\t{v.m_enable}\n");
      File.WriteAllText(Path.Combine(Out, "vegetation-entries.tsv"), sb.ToString());
      Log($"vegetation entries: {zs.m_vegetation.Count}");
      var sp = new StringBuilder("list\tname\tbiome\tmin_alt\tmax_alt\tenable\n");
      int n = 0;
      var lists = new HashSet<SpawnSystemList>();
      var sys = zs.m_zonePrefab.GetComponentInChildren<SpawnSystem>(true);
      if (sys != null) foreach (var l in sys.m_spawnLists) lists.Add(l);
      foreach (var l in Resources.FindObjectsOfTypeAll<SpawnSystemList>()) lists.Add(l);
      foreach (var l in lists)
        foreach (var d in l.m_spawners)
        {
          sp.Append($"{l.name}\t{d.m_name}\t{d.m_biome}\t{F(d.m_minAltitude)}\t{F(d.m_maxAltitude)}\t{d.m_enabled}\n");
          n++;
        }
      File.WriteAllText(Path.Combine(Out, "spawn-entries.tsv"), sp.ToString());
      Log($"spawn lists {lists.Count}, entries {n}");
    }

    // ------------------------------------------------------------------------------------------ high terrain (2026-10-06)

    // BCPROBE_HIGH=1: the game's ground rays and height rules on real zones (terrain and colliders, as a client has them), at the
    // points of BCPROBE_HIGHPTS ("name<TAB>x<TAB>z"): what ZoneSystem.GetGroundHeight, GetGroundData, Character.InInterior and the
    // AI's tile say, next to the height the generator gives. BCPROBE_HIGH_CONTROL=1 switches Better Continents' high-terrain patches
    // off first (through its own Toggle.Update, with settings that want none), which is what the game does without them.
    static bool SkipPatchHighTerrain() => false;

    // BCPROBE_HIGH_LATE=1 (with BCPROBE_HIGH_CONTROL=1, so that no high-terrain patch exists yet): methods of the game that call
    // Character.InInterior are compiled first (called once), THEN Better Continents' high-terrain patches are switched on, as DynamicPatch
    // does for a world loaded after others in a session; a call counter on HighTerrain.Interior(Vector3) says which callers go through it.
    static int interiorCalls;
    static void CountInterior() { interiorCalls++; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool EarlyCaller(Vector3 v) => Character.InInterior(v);

    static void LateRun()
    {
      var bc = AccessTools.TypeByName("BetterContinents.BetterContinents");
      var hi = AccessTools.TypeByName("BetterContinents.HighTerrain");
      var harmony = new Harmony("wubarrk.bcserverprobe.late");
      harmony.CreateProcessor(AccessTools.Method(hi, "Interior", new[] { typeof(Vector3) })).AddPrefix(new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(CountInterior)))).Patch();
      var p = new Vector3(0f, 100f, 0f);
      bool a0 = EarlyCaller(p);
      var l0 = Location.GetLocation(p);
      Log($"late: callers compiled before the patches (probe's EarlyCaller {a0}, Location.GetLocation {(l0 == null ? "null" : "a location")}); HighTerrain.Interior calls so far {interiorCalls}");
      var settings = AccessTools.Field(bc, "Settings").GetValue(null);
      AccessTools.Method(hi, "Update").Invoke(null, new[] { settings });
      foreach (var t in (Array)AccessTools.Field(bc, "HighTerrainToggles").GetValue(null))
        AccessTools.Method(t.GetType(), "Update").Invoke(t, new[] { settings });
      Log($"late: the high-terrain toggles are on now (HighTerrain active {Get(hi, null, "Active")})");
      int c0 = interiorCalls;
      EarlyCaller(p);
      int c1 = interiorCalls;
      Location.GetLocation(p);
      int c2 = interiorCalls;
      Character.InInterior(p);
      int c3 = interiorCalls;
      Log($"late: after: EarlyCaller (compiled before) went through HighTerrain.Interior {c1 - c0} times, Location.GetLocation (compiled before, a game method that calls Character.InInterior) {c2 - c1}, "
          + $"a direct call of Character.InInterior from the probe's own LateRun {c3 - c2}");
    }

    static IEnumerator HighRun()
    {
      var bc = AccessTools.TypeByName("BetterContinents.BetterContinents");
      var hi = AccessTools.TypeByName("BetterContinents.HighTerrain");
      bool control = Environment.GetEnvironmentVariable("BCPROBE_HIGH_CONTROL") == "1";
      var settings = AccessTools.Field(bc, "Settings").GetValue(null);
      Log($"high: HighTerrain active {Get(hi, null, "Active")}, top {Get(hi, null, "Top")} m; Heightmap Amount {Get(settings.GetType(), settings, "HeightmapAmount")}; control {control}");
      if (Environment.GetEnvironmentVariable("BCPROBE_HIGH_LATE") == "1")
        Try("late", LateRun);
      var table = HighTable("high-rays.tsv");
      while (table.MoveNext())
        yield return table.Current;
    }

    // BCPROBE_HIGH_TOGGLE=1 (after everything else): the same table again after each change of the High Terrain mode of the running world (bc h ht off, on, back to
    // what the world had), which is what a player does in the console: the patches go off and on again on the real server's methods (the groups' Applied states
    // are logged each time).
    static IEnumerator HighToggleRun()
    {
      var bc = AccessTools.TypeByName("BetterContinents.BetterContinents");
      var settings = AccessTools.Field(bc, "Settings").GetValue(null);
      var start = AccessTools.Field(settings.GetType(), "HighTerrainMode").GetValue(settings).ToString();
      Log($"toggle: the world starts in High Terrain {start}");
      foreach (var mode in new[] { "off", "on", start })
      {
        SetMode(mode);
        var table = HighTable($"high-rays-{mode}.tsv");
        while (table.MoveNext())
          yield return table.Current;
      }
    }

    // bc h ht <mode>, through the game's own console table when it runs (DebugUtils.RunConsoleCommand), the setting and DynamicPatch directly when it does not.
    static void SetMode(string mode)
    {
      var bc = AccessTools.TypeByName("BetterContinents.BetterContinents");
      var hi = AccessTools.TypeByName("BetterContinents.HighTerrain");
      var settings = AccessTools.Field(bc, "Settings").GetValue(null);
      var wanted = Enum.Parse(AccessTools.TypeByName("BetterContinents.HighTerrainMode"), mode, true);
      string how = "bc h ht " + mode;
      try
      {
        AccessTools.Method(AccessTools.TypeByName("BetterContinents.DebugUtils"), "RunConsoleCommand").Invoke(null, new object[] { "bc h ht " + mode });
      }
      catch (Exception e)
      {
        Log($"mode: 'bc h ht {mode}' failed: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
      }
      if (!Equals(AccessTools.Field(settings.GetType(), "HighTerrainMode").GetValue(settings), wanted))
      {
        how = $"the setting and DynamicPatch directly ({how} did not set it)";
        AccessTools.Field(settings.GetType(), "HighTerrainMode").SetValue(settings, wanted);
        AccessTools.Method(bc, "DynamicPatch").Invoke(null, null);
      }
      var states = ((Array)AccessTools.Field(bc, "HighTerrainToggles").GetValue(null)).Cast<object>()
        .Select(t => $"{AccessTools.Field(t.GetType(), "Name").GetValue(t).ToString().Replace("High terrain: ", "")}={AccessTools.Property(t.GetType(), "Applied").GetValue(t, null)}");
      Log($"mode: {how}: HighTerrainMode {AccessTools.Field(settings.GetType(), "HighTerrainMode").GetValue(settings)}, HighTerrain active {Get(hi, null, "Active")}, top {Get(hi, null, "Top")} m; groups {string.Join(", ", states)}");
    }

    static IEnumerator HighTable(string fileName)
    {
      var file = Environment.GetEnvironmentVariable("BCPROBE_HIGHPTS");
      if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Log("high: no BCPROBE_HIGHPTS"); yield break; }
      var zs = ZoneSystem.instance;
      var wg = WorldGenerator.instance;
      var spawn = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
      var sb = new StringBuilder("name\tx\tz\tground\tbiome\tray_ground\tray_ok\tray_h\tdata_y\tdata_biome\tdata_normal_y\tdata_hmap\tblocked\tinterior_ground\tinterior_dungeon\tinterior_3500\ttile_y\tgrass_ok\tgrass_y\n");
      var tileMethod = AccessTools.TypeByName("Pathfinding") != null ? AccessTools.Method(AccessTools.TypeByName("Pathfinding"), "GetTilePos") : null;
      var pathfinding = tileMethod != null ? UnityEngine.Object.FindObjectOfType(AccessTools.TypeByName("Pathfinding")) : null;
      Log($"high: Pathfinding instance {(pathfinding != null ? "found" : "none")}");
      foreach (var line in File.ReadAllLines(file))
      {
        var c = line.Split('\t');
        if (c.Length < 3) continue;
        float x = float.Parse(c[1], IC), z = float.Parse(c[2], IC);
        var zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
        object[] args = null;
        bool made = false;
        string failed = null;
        for (int tries = 0; tries < 600 && !made && failed == null; tries++)   // false until the terrain builder has the zone's heights
        {
          args = new object[] { zone, ZoneSystem.SpawnMode.Client, null };
          try { made = (bool)spawn.Invoke(zs, args); }
          catch (Exception e) { failed = (e.InnerException ?? e).Message; }
          if (!made && failed == null) yield return null;
        }
        if (!made) { Log($"high: {c[0]}: SpawnZone {(failed ?? "never finished")}"); continue; }
        var root = (GameObject)args[2];
        for (int i = 0; i < 3; i++) yield return null;
        Physics.SyncTransforms();
        float ground = wg.GetHeight(x, z);
        var biome = wg.GetBiome(x, z);
        float rayGround = zs.GetGroundHeight(new Vector3(x, 0f, z));
        bool rayOk = zs.GetGroundHeight(new Vector3(x, 0f, z), out var rayH);
        var p = new Vector3(x, 0f, z);
        zs.GetGroundData(ref p, out var normal, out var dataBiome, out var dataArea, out var hmap);
        bool blocked = zs.IsBlocked(new Vector3(x, 0f, z));
        bool inGround = Character.InInterior(new Vector3(x, ground + 2f, z));
        bool inDungeon = Character.InInterior(new Vector3(x, ground + 5002f, z));
        bool in3500 = Character.InInterior(new Vector3(x, 3500f, z));
        string grass = "-\t-";
        if (ClutterSystem.instance != null)
        {
          // A dedicated server (graphics device Null) leaves the grass's ray mask empty (ClutterSystem.Awake): give it the client's, the terrain.
          AccessTools.Field(typeof(ClutterSystem), "m_placeRayMask").SetValue(ClutterSystem.instance, LayerMask.GetMask("terrain"));
          bool gok = ClutterSystem.instance.GetGroundInfo(new Vector3(x, 0f, z), out var gp, out var gn, out var gh, out var gb);
          grass = $"{gok}\t{F(gp.y)}";
        }
        string tile = "-";
        if (pathfinding != null)
        {
          float size = (float)AccessTools.Field(pathfinding.GetType(), "m_tileSize").GetValue(pathfinding);
          var id = new Vector3Int(Mathf.RoundToInt(x / size), Mathf.RoundToInt(z / size), 0);
          tile = F(((Vector3)tileMethod.Invoke(pathfinding, new object[] { id })).y);
        }
        sb.Append($"{c[0]}\t{F(x)}\t{F(z)}\t{F(ground)}\t{biome}\t{F(rayGround)}\t{rayOk}\t{F(rayH)}\t{F(p.y)}\t{dataBiome}\t{F(normal.y)}\t{(hmap != null)}\t{blocked}\t{inGround}\t{inDungeon}\t{in3500}\t{tile}\t{grass}\n");
        Log($"high: {c[0]} ({F(x)}, {F(z)}): generator {F(ground)} m {biome}; GetGroundHeight {F(rayGround)} / {rayOk} {F(rayH)}; GetGroundData y {F(p.y)} {dataBiome} normal.y {F(normal.y)}; "
            + $"InInterior on the ground {inGround}, 5000 m up {inDungeon}, at 3500 m {in3500}; tile y {tile}; grass ray {grass.Replace('\t', ' ')}");
        if (root != null) UnityEngine.Object.Destroy(root);
        yield return null;
      }
      File.WriteAllText(Path.Combine(Out, fileName), sb.ToString());
    }

    // Real dungeons on high ground: for locations that have an interior (Location.m_hasInterior; the game builds it 5000 m over the entrance), generate the
    // zone the way a dedicated server does (SpawnZone in Ghost mode: every room becomes a ZDO), then list the ZDOs of that zone by height over the ground and
    // ask Character.InInterior about each, and about the entrance on the ground (the game's own rule would say "inside" over 3000 m, whatever is under it).
    // BCPROBE_DUNGEONS=N: N locations, the highest ones first, plus two lower ones (a low entrance and a middle one).
    static IEnumerator DungeonRun()
    {
      int want = int.Parse(Environment.GetEnvironmentVariable("BCPROBE_DUNGEONS"), IC);
      var zs = ZoneSystem.instance;
      var wg = WorldGenerator.instance;
      var spawn = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
      var isGenerated = AccessTools.Method(typeof(ZoneSystem), "IsZoneGenerated");
      var all = (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance);
      var interiors = zs.m_locationInstances.Where(kv => kv.Value.m_location != null && kv.Value.m_location.m_interiorRadius > 0f).OrderByDescending(kv => kv.Value.m_position.y).ToList();
      Log($"dungeons: {interiors.Count()} locations with an interior, highest entrance {(interiors.Count() > 0 ? F(interiors[0].Value.m_position.y) : "-")} m");
      var chosen = interiors.Take(want).ToList();
      var low = interiors.Where(kv => kv.Value.m_position.y < 300f).Take(1).ToList();
      var mid = interiors.Where(kv => kv.Value.m_position.y > 3000f && kv.Value.m_position.y < 9000f).Take(1).ToList();
      foreach (var extra in low.Concat(mid)) if (!chosen.Contains(extra)) chosen.Add(extra);
      var sb = new StringBuilder("location\tzone_x\tzone_z\tentrance_y\tground_at_zone_centre\tzdos\tabove_ground_3000\tmin_over_ground\tmax_over_ground\tinterior_true\tinterior_false\tentrance_interior\n");
      foreach (var kv in chosen)
      {
        var li = kv.Value;
        var zone = kv.Key;
        bool made = false;
        for (int tries = 0; tries < 600 && !made; tries++)
        {
          try { made = (bool)spawn.Invoke(zs, new object[] { zone, ZoneSystem.SpawnMode.Ghost, null }); }
          catch (Exception e) { Log($"dungeons: {li.m_location.m_prefabName}: SpawnZone FAILED {(e.InnerException ?? e).Message}"); break; }
          if (!made) yield return null;
        }
        if (!made) continue;
        yield return null;
        var centre = ZoneSystem.GetZonePos(zone);
        float groundCentre = wg.GetHeight(centre.x, centre.z);
        int zdos = 0, deep = 0, itrue = 0, ifalse = 0;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var zdo in all.Values)
        {
          var p = zdo.GetPosition();
          if (ZoneSystem.GetZone(p) != zone) continue;
          zdos++;
          float over = p.y - wg.GetHeight(p.x, p.z);
          if (over > 3000f)
          {
            deep++;
            if (over < min) min = over;
            if (over > max) max = over;
          }
          if (Character.InInterior(p)) itrue++; else ifalse++;
        }
        bool entrance = Character.InInterior(new Vector3(li.m_position.x, li.m_position.y + 2f, li.m_position.z));
        sb.Append($"{li.m_location.m_prefabName}\t{zone.x}\t{zone.y}\t{F(li.m_position.y)}\t{F(groundCentre)}\t{zdos}\t{deep}\t{(deep > 0 ? F(min) : "-")}\t{(deep > 0 ? F(max) : "-")}\t{itrue}\t{ifalse}\t{entrance}\n");
        Log($"dungeons: {li.m_location.m_prefabName} at ({F(li.m_position.x)}, {F(li.m_position.z)}), entrance {F(li.m_position.y)} m: {zdos} objects in its zone, {deep} of them more than 3000 m over the ground "
            + $"({(deep > 0 ? F(min) + " .. " + F(max) + " m over it" : "none")}); InInterior true for {itrue}, false for {ifalse}; standing at the entrance: {entrance}");
        yield return null;
      }
      File.WriteAllText(Path.Combine(Out, "dungeons.tsv"), sb.ToString());
    }

    static void Try(string what, Action a)
    {
      try { a(); }
      catch (Exception e) { Log($"{what} FAILED: {e}"); }
    }

    static object Get(Type t, object o, string name)
    {
      if (t == null) return "?";
      var f = AccessTools.Field(t, name);
      if (f != null) return f.GetValue(o);
      var p = AccessTools.Property(t, name);
      return p != null ? p.GetValue(o, null) : "?";
    }

    static void Settings()
    {
      var grid = AccessTools.TypeByName("BetterContinents.BetterContinents+BiomePrecisionGrid");
      var bc = AccessTools.TypeByName("BetterContinents.BetterContinents");
      var settings = bc == null ? null : Get(bc, null, "Settings");
      var st = settings?.GetType();
      Log("Better Continents: biome precision active " + Get(grid, null, "Active")
          + "; settings version " + Get(st, settings, "Version")
          + ", enabled " + Get(st, settings, "EnabledForThisWorld")
          + ", exact pins " + Get(st, settings, "ExactLocationPins")
          + ", maps from an export " + Get(st, settings, "MapsFromExport")
          + ", finer precision allowed " + Get(st, settings, "FinerBiomePrecision")
          + ", Heightmap Amount " + Get(st, settings, "HeightmapAmount")
          + ", Biome precision " + Get(st, settings, "BiomePrecision"));
    }

    static void Locations()
    {
      var zs = ZoneSystem.instance;
      var sb = new StringBuilder("zone_x\tzone_z\tprefab\tx\ty\tz\tplaced\n");
      var counts = new Dictionary<string, int>();
      foreach (var kv in zs.m_locationInstances)
      {
        var li = kv.Value;
        string n = li.m_location?.m_prefabName ?? "?";
        counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
        sb.Append($"{kv.Key.x}\t{kv.Key.y}\t{n}\t{F(li.m_position.x)}\t{F(li.m_position.y)}\t{F(li.m_position.z)}\t{li.m_placed}\n");
      }
      File.WriteAllText(Path.Combine(Out, "locations.tsv"), sb.ToString());
      Log($"location instances {zs.m_locationInstances.Count}: "
          + string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));

      var eb = new StringBuilder("prefab\tenable\tquantity\tprioritized\tbiome\tmin_alt\tmax_alt\n");
      int enabled = 0;
      foreach (var l in zs.m_locations)
      {
        if (l == null || !l.m_enable || l.m_quantity == 0) continue;
        enabled++;
        eb.Append($"{l.m_prefabName}\t{l.m_enable}\t{l.m_quantity}\t{l.m_prioritized}\t{l.m_biome}\t{F(l.m_minAltitude)}\t{F(l.m_maxAltitude)}\n");
      }
      File.WriteAllText(Path.Combine(Out, "location-entries.tsv"), eb.ToString());
      Log($"location entries: {zs.m_locations.Count}, enabled with a quantity {enabled}");
    }

    static IEnumerable<string[]> ReadPoints() =>
      string.IsNullOrEmpty(Points) || !File.Exists(Points)
        ? Enumerable.Empty<string[]>()
        : File.ReadAllLines(Points).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t'));

    // ------------------------------------------------------------------------------------------ the game's own terrain over BC's base height (high-terrain stream, continuation, 2026-10-06)

    // BCPROBE_BOXES: lines "name<TAB>x0<TAB>z0<TAB>x1<TAB>z1<TAB>n": n random points in the box (fixed seed). For each point: the base height
    // WorldGenerator.GetBaseHeight gives (which Better Continents' own height replaces: b, in units of 200 m), the biome and the terrain height
    // WorldGenerator.GetHeight gives. Per box and biome the statistics of the terrain minus 200 * (2b - 0.4) (the Mountain biome's formula,
    // GetSnowMountainHeight) for Mountain, minus 200 * b for the others.
    class Acc { public int n; public double bMin = 1e30, bMax = -1e30, hMin = 1e30, hMax = -1e30, hSum, dMin = 1e30, dMax = -1e30, dSum, eMin = 1e30, eMax = -1e30, eSum, hMaxB, hMaxX, hMaxZ; }

    static void Boxes()
    {
      var wg = WorldGenerator.instance;
      var baseHeight = AccessTools.Method(typeof(WorldGenerator), "GetBaseHeight");
      var sb = new StringBuilder("box\tbiome\tn\tb_min\tb_max\th_min\th_max\th_mean\tdev_min\tdev_max\tdev_mean\tmodel\tover_b_min\tover_b_max\tover_b_mean\ttop_b\ttop_x\ttop_z\n");
      foreach (var line in File.ReadAllLines(Environment.GetEnvironmentVariable("BCPROBE_BOXES")))
      {
        var c = line.Split('\t');
        if (c.Length < 6 || c[0].StartsWith("#")) continue;
        float x0 = float.Parse(c[1], IC), z0 = float.Parse(c[2], IC), x1 = float.Parse(c[3], IC), z1 = float.Parse(c[4], IC);
        int n = int.Parse(c[5], IC);
        var rng = new System.Random(12345);
        var acc = new Dictionary<string, Acc>();
        for (int i = 0; i < n; i++)
        {
          float x = x0 + (float)rng.NextDouble() * (x1 - x0), z = z0 + (float)rng.NextDouble() * (z1 - z0);
          float b = (float)baseHeight.Invoke(wg, new object[] { x, z, false });
          var biome = wg.GetBiome(x, z);
          float h = wg.GetHeight(x, z);
          bool mountain = biome == Heightmap.Biome.Mountain;
          double dev = h - (mountain ? 200.0 * (2.0 * b - 0.4) : 200.0 * b);
          string key = biome.ToString();
          if (!acc.TryGetValue(key, out var a)) acc[key] = a = new Acc();
          a.n++;
          a.bMin = Math.Min(a.bMin, b); a.bMax = Math.Max(a.bMax, b);
          a.hMin = Math.Min(a.hMin, h); a.hMax = Math.Max(a.hMax, h); a.hSum += h;
          a.dMin = Math.Min(a.dMin, dev); a.dMax = Math.Max(a.dMax, dev); a.dSum += dev;
          double over = h - 200.0 * b;
          a.eMin = Math.Min(a.eMin, over); a.eMax = Math.Max(a.eMax, over); a.eSum += over;
          if (h >= a.hMax) { a.hMaxB = b; a.hMaxX = x; a.hMaxZ = z; }
        }
        foreach (var kv in acc.OrderBy(k => k.Key))
        {
          var a = kv.Value;
          sb.Append($"{c[0]}\t{kv.Key}\t{a.n}\t{F((float)a.bMin)}\t{F((float)a.bMax)}\t{F((float)a.hMin)}\t{F((float)a.hMax)}\t{F((float)(a.hSum / a.n))}\t{F((float)a.dMin)}\t{F((float)a.dMax)}\t{F((float)(a.dSum / a.n))}\t{(kv.Key == "Mountain" ? "200*(2b-0.4)" : "200*b")}\t{F((float)a.eMin)}\t{F((float)a.eMax)}\t{F((float)(a.eSum / a.n))}\t{F((float)a.hMaxB)}\t{F((float)a.hMaxX)}\t{F((float)a.hMaxZ)}\n");
          Log($"boxes: {c[0]} {kv.Key}: {a.n} points, b {F((float)a.bMin)}..{F((float)a.bMax)}, terrain {F((float)a.hMin)}..{F((float)a.hMax)} m (mean {F((float)(a.hSum / a.n))}), terrain minus the model {F((float)a.dMin)}..{F((float)a.dMax)} m (mean {F((float)(a.dSum / a.n))}), terrain minus 200*b {F((float)a.eMin)}..{F((float)a.eMax)} m (mean {F((float)(a.eSum / a.n))}); highest at ({F((float)a.hMaxX)}, {F((float)a.hMaxZ)}), b {F((float)a.hMaxB)}");
        }
      }
      File.WriteAllText(Path.Combine(Out, "boxes.tsv"), sb.ToString());
    }

    // BCPROBE_PERLIN=1: the real Mathf.PerlinNoise on this server: its range, and the range of the Mountain biome's own noise
    // (GetSnowMountainHeight: num5 = P(.01)*P(.02); num5 += P(.05)*P(.1)*num5*.5; the terms 0.2*num5, 0.01*P(.1), 0.003*P(.4)).
    static void PerlinStats()
    {
      var rng = new System.Random(777);
      const int N = 4000000;
      double pMin = 9, pMax = -9, n5Max = 0, termMax = 0, n3Max = 0;
      for (int i = 0; i < N; i++)
      {
        double x = 100000.0 + rng.NextDouble() * 40000.0 - 20000.0, z = 100000.0 + rng.NextDouble() * 40000.0 - 20000.0;
        float p1 = Mathf.PerlinNoise((float)(x * 0.01), (float)(z * 0.01)), p2 = Mathf.PerlinNoise((float)(x * 0.02), (float)(z * 0.02));
        float p3 = Mathf.PerlinNoise((float)(x * 0.05), (float)(z * 0.05)), p4 = Mathf.PerlinNoise((float)(x * 0.1), (float)(z * 0.1));
        float n5 = p1 * p2; n5 += p3 * p4 * n5 * 0.5f;
        float term = 0.2f * n5 + Mathf.PerlinNoise((float)(x * 0.1), (float)(z * 0.1)) * 0.01f + Mathf.PerlinNoise((float)(x * 0.4), (float)(z * 0.4)) * 0.003f;
        pMin = Math.Min(pMin, Math.Min(Math.Min(p1, p2), Math.Min(p3, p4))); pMax = Math.Max(pMax, Math.Max(Math.Max(p1, p2), Math.Max(p3, p4)));
        n5Max = Math.Max(n5Max, n5); termMax = Math.Max(termMax, term);
        // Mistlands: num3 = P(.014)*P(.028); num3 += P(.021)*P(.035)*num3*.5; then ^1.5, times 0.4
        float m1 = Mathf.PerlinNoise((float)(x * 0.02 * 0.7), (float)(z * 0.02 * 0.7)) * Mathf.PerlinNoise((float)(x * 0.04 * 0.7), (float)(z * 0.04 * 0.7));
        m1 += Mathf.PerlinNoise((float)(x * 0.03 * 0.7), (float)(z * 0.03 * 0.7)) * Mathf.PerlinNoise((float)(x * 0.05 * 0.7), (float)(z * 0.05 * 0.7)) * m1 * 0.5f;
        n3Max = Math.Max(n3Max, m1);
      }
      Log($"perlin: {N} points: Mathf.PerlinNoise {pMin:0.0000}..{pMax:0.0000}; Mountain num5 max {n5Max:0.0000} (theory 1.5), its noise term 0.2*num5+0.01*P+0.003*P max {termMax:0.0000} units = {termMax * 200:0.0} m; Mistlands num3 max {n3Max:0.0000} (theory 1.5), 0.4*num3^1.5 = {0.4 * Math.Pow(n3Max, 1.5):0.0000} units = {0.4 * Math.Pow(n3Max, 1.5) * 200:0.0} m");
    }

    static void SamplePoints()
    {
      var wg = WorldGenerator.instance;
      var sb = new StringBuilder("kind\tname\tx\tz\ty\tbiome\n");
      int n = 0;
      foreach (var p in ReadPoints())
      {
        if (p[0] == "grid" || p.Length < 4) continue;
        float x = float.Parse(p[2], IC), z = float.Parse(p[3], IC);
        sb.Append($"{p[0]}\t{p[1]}\t{F(x)}\t{F(z)}\t{F(wg.GetHeight(x, z))}\t{wg.GetBiome(x, z)}\n");
        n++;
      }
      File.WriteAllText(Path.Combine(Out, "points-out.tsv"), sb.ToString());
      Log($"sampled {n} points");
    }

    // Heights (float) and biomes (ushort) on the grid line of BCPROBE_POINTS, row by row from the south, west to east.
    static IEnumerator Grid()
    {
      var g = ReadPoints().FirstOrDefault(p => p[0] == "grid");
      if (g == null) { Log("no grid line"); yield break; }
      float x0 = float.Parse(g[2], IC), z0 = float.Parse(g[3], IC), x1 = float.Parse(g[4], IC), z1 = float.Parse(g[5], IC), step = float.Parse(g[6], IC);
      int nx = (int)Math.Round((x1 - x0) / step) + 1, nz = (int)Math.Round((z1 - z0) / step) + 1;
      var wg = WorldGenerator.instance;
      Log($"grid {nx} x {nz} from ({F(x0)}, {F(z0)}) every {F(step)} m; {Mem()}");
      long mountainLow = 0, highNotMountain = 0, samples = 0;
      float maxY = float.MinValue, maxX = 0, maxZ = 0;
      var started = Time.realtimeSinceStartup;
      using (var w = new BinaryWriter(File.Create(Path.Combine(Out, "grid.bin"))))
      {
        w.Write(Encoding.ASCII.GetBytes("BCGRID1\n"));
        w.Write(nx); w.Write(nz); w.Write(x0); w.Write(z0); w.Write(step);
        var frame = System.Diagnostics.Stopwatch.StartNew();
        for (int j = 0; j < nz; j++)
        {
          float z = z0 + j * step;
          for (int i = 0; i < nx; i++)
          {
            float x = x0 + i * step;
            float y = wg.GetHeight(x, z);
            var b = wg.GetBiome(x, z);
            w.Write(y);
            w.Write((ushort)b);
            samples++;
            if (y > maxY) { maxY = y; maxX = x; maxZ = z; }
            bool mountain = b == Heightmap.Biome.Mountain;
            if (mountain && y < 210f) mountainLow++;
            else if (!mountain && y > 210f) highNotMountain++;
          }
          if (frame.ElapsedMilliseconds > 200)
          {
            frame.Reset();
            yield return null;
            frame.Start();
          }
          if (j % (nz / 10) == 0)
            Log($"grid row {j}/{nz}");
        }
      }
      Log($"grid done in {(Time.realtimeSinceStartup - started).ToString("0.0", IC)} s: {samples} samples; highest y {F(maxY)} at ({F(maxX)}, {F(maxZ)}); "
          + $"Mountain below 180 m above water {mountainLow}; above 180 m and not Mountain {highNotMountain}; {Mem()}");
    }

    // ------------------------------------------------------------------------------------------ the towns

    static string Md5(byte[] b)
    {
      if (b == null) return "-";
      using (var m = System.Security.Cryptography.MD5.Create())
        return BitConverter.ToString(m.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
    }

    IEnumerator TownsRun()
    {
      var zs = ZoneSystem.instance;
      var spawn = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
      var isGenerated = AccessTools.Method(typeof(ZoneSystem), "IsZoneGenerated");
      var hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
      int width = hm.m_width;
      float scale = hm.m_scale;
      bool lod = hm.IsDistantLod;
      var zones = new List<Vector2s>();
      var withPayload = new HashSet<Vector2s>();
      foreach (var line in File.ReadAllLines(Towns))
      {
        var p = line.Split('\t');
        if (p.Length < 2) continue;
        var z = new Vector2s(int.Parse(p[0], IC), int.Parse(p[1], IC));
        zones.Add(z);
        if (p.Length > 2 && p[2].Trim() == "1") withPayload.Add(z);
      }
      Func<Vector2s, bool> generated = z => (bool)isGenerated.Invoke(zs, new object[] { z });
      int already = zones.Count(generated);
      // BCPROBE_PREFABS: piece names (first column) to look up in ZNetScene, as VALtimaOnline's TownPieceLibrary does
      var prefabList = Environment.GetEnvironmentVariable("BCPROBE_PREFABS");
      if (!string.IsNullOrEmpty(prefabList) && File.Exists(prefabList))
        Try("prefabs", () =>
        {
          var sb = new StringBuilder("name\tfirst_candidate\texists\tpiece_name\n");
          int found = 0, total = 0;
          foreach (var l in File.ReadAllLines(prefabList))
          {
            var c = l.Split('\t');
            if (c[0].Length == 0) continue;
            var go = ZNetScene.instance.GetPrefab(c[0]);
            var piece = go ? go.GetComponent<Piece>() : null;
            total++;
            if (go) found++;
            sb.Append($"{c[0]}\t{(c.Length > 1 && c[1] == "0" ? 1 : 0)}\t{(go ? 1 : 0)}\t{(piece ? piece.m_name : "-")}\n");
          }
          File.WriteAllText(Path.Combine(Out, "towns-prefabs.tsv"), sb.ToString());
          Log($"towns: {found} of {total} piece names are prefabs in ZNetScene");
        });
      Log($"towns: {zones.Count} zones ({withPayload.Count} with a payload), {already} generated already; zone terrain {width} vertices x {F(scale)} m, "
          + $"distant {lod}, water {F(zs.m_waterLevel)}; {Mem()}");

      // 1. generate them as a dedicated server does (Ghost mode), a window of 8 at a time
      var pending = zones.Where(z => !generated(z)).ToList();
      int made = 0, failed = 0, total = pending.Count;
      float started = Time.realtimeSinceStartup, lastNote = started;
      const int Window = 8;
      while (pending.Count > 0)
      {
        var frame = System.Diagnostics.Stopwatch.StartNew();
        int i = 0;
        while (i < Math.Min(Window, pending.Count) && frame.ElapsedMilliseconds < 200)
        {
          bool done;
          try
          {
            done = (bool)spawn.Invoke(zs, new object[] { pending[i], ZoneSystem.SpawnMode.Ghost, null });
          }
          catch (Exception e)
          {
            Log($"towns: zone {pending[i].x},{pending[i].y} FAILED to generate: {(e.InnerException ?? e)}");
            failed++;
            done = true;
          }
          if (done) { pending.RemoveAt(i); made++; }
          else i++;
        }
        if (Time.realtimeSinceStartup - lastNote > 10f)
        {
          lastNote = Time.realtimeSinceStartup;
          Log($"towns: generated {made}/{total}, {pending.Count} left; {Mem()}");
        }
        if (Time.realtimeSinceStartup - started > TownsTimeout)
        {
          Log($"towns: TIMEOUT after {F(TownsTimeout)} s with {pending.Count} zones left");
          break;
        }
        yield return null;
      }
      int notGenerated = zones.Count(z => !generated(z));
      Log($"towns: generation done in {(Time.realtimeSinceStartup - started).ToString("0.0", IC)} s: {made} spawned ({failed} threw), "
          + $"{notGenerated} of the {zones.Count} zones not marked generated; {Mem()}");

      // 2. every town chunk in the world, and each listed zone's objects
      int chunkHash = "VALtima_TownChunk".GetStableHashCode(), terrainHash = "_TerrainCompiler".GetStableHashCode();
      var all = (Dictionary<ZDOID, ZDO>)AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance);
      var listed = new HashSet<Vector2s>(zones);
      var objects = new Dictionary<Vector2s, int>();
      var modifiers = new Dictionary<Vector2s, int>();
      var chunks = new List<ZDO>();
      var names = new Dictionary<int, string>();
      foreach (var n in new[] { "VALtima_Npc_Stander", "VALtima_Npc_StanderFemale", "VALtima_Npc_Guard", "VALtima_Npc_GuardCalled" })
        names[n.GetStableHashCode()] = n;
      var npcHashes = new HashSet<int>(names.Keys);
      var stations = new StringBuilder("zone_x\tzone_z\tprefab\tx\ty\tz\tyaw\n");
      var npcs = new StringBuilder("zone_x\tzone_z\tprefab\tid\ttown\trole\tground\tx\ty\tz\tpersistent\n");
      int stationCount = 0, npcCount = 0;
      var placed = new StringBuilder("zone_x\tzone_z\tprefab\tx\ty\tz\tground\n");
      foreach (var zdo in all.Values)
      {
        var zone = ZoneSystem.GetZone(zdo.GetPosition());
        int prefab = zdo.GetPrefab();
        if (prefab == chunkHash) chunks.Add(zdo);
        if (zdo.GetBool("VALtima_TownPiece"))
        {
          var sp = zdo.GetPosition();
          if (!names.TryGetValue(prefab, out var sn)) { var sg = ZNetScene.instance.GetPrefab(prefab); names[prefab] = sn = sg ? sg.name : "#" + prefab.ToString(IC); }
          stations.Append($"{zone.x}\t{zone.y}\t{sn}\t{F(sp.x)}\t{F(sp.y)}\t{F(sp.z)}\t{F(zdo.GetRotation().eulerAngles.y)}\n");
          stationCount++;
        }
        var npcId = zdo.GetString("VALtima_NpcId", "");
        if (npcId.Length > 0 || npcHashes.Contains(prefab))
        {
          var np = zdo.GetPosition();
          if (!names.TryGetValue(prefab, out var nn)) { var ng = ZNetScene.instance.GetPrefab(prefab); names[prefab] = nn = ng ? ng.name : "#" + prefab.ToString(IC); }
          npcs.Append($"{zone.x}\t{zone.y}\t{nn}\t{npcId}\t{zdo.GetString("VALtima_Town", "")}\t{zdo.GetString("VALtima_Role", "")}\t{zdo.GetString("VALtima_Ground", "")}\t"
                      + $"{F(np.x)}\t{F(np.y)}\t{F(np.z)}\t{zdo.Persistent}\n");
          npcCount++;
        }
        if (!listed.Contains(zone)) continue;
        objects[zone] = objects.TryGetValue(zone, out var c) ? c + 1 : 1;
        if (prefab == terrainHash) modifiers[zone] = modifiers.TryGetValue(zone, out var m) ? m + 1 : 1;
        var at = zdo.GetPosition();
        if (!names.TryGetValue(prefab, out var name))
        {
          var go = ZNetScene.instance.GetPrefab(prefab);
          names[prefab] = name = go ? go.name : "#" + prefab.ToString(IC);
        }
        placed.Append($"{zone.x}\t{zone.y}\t{name}\t{F(at.x)}\t{F(at.y)}\t{F(at.z)}\t{F(WorldGenerator.instance.GetHeight(at.x, at.z))}\n");
      }
      File.WriteAllText(Path.Combine(Out, "towns-objects.tsv"), placed.ToString());
      File.WriteAllText(Path.Combine(Out, "towns-stations.tsv"), stations.ToString());
      File.WriteAllText(Path.Combine(Out, "towns-npcs.tsv"), npcs.ToString());
      Log($"towns: {stationCount} seeded town stations and {npcCount} town NPCs in the world");
      var tsv = new StringBuilder("zone_x\tzone_z\tx\ty\tz\ttype\tpersistent\tdistant\tsector_x\tsector_z\tparts_bytes\tparts_md5\tusables\n");
      using (var bin = new BinaryWriter(File.Create(Path.Combine(Out, "towns-chunks.bin"))))
      {
        bin.Write(Encoding.ASCII.GetBytes("VTCHUNK1"));
        bin.Write(chunks.Count);
        foreach (var zdo in chunks.OrderBy(d => ZoneSystem.GetZone(d.GetPosition()).y).ThenBy(d => ZoneSystem.GetZone(d.GetPosition()).x))
        {
          var pos = zdo.GetPosition();
          var zone = ZoneSystem.GetZone(pos);
          var sector = zdo.GetSector();
          var parts = zdo.GetByteArray("VALtima_TownParts");
          bin.Write((short)zone.x);
          bin.Write((short)zone.y);
          bin.Write(parts == null ? -1 : parts.Length);
          if (parts != null) bin.Write(parts);
          tsv.Append($"{zone.x}\t{zone.y}\t{F(pos.x)}\t{F(pos.y)}\t{F(pos.z)}\t{zdo.Type}\t{zdo.Persistent}\t{zdo.Distant}\t{sector.x}\t{sector.y}\t{parts?.Length ?? -1}\t{Md5(parts)}\t{zdo.GetInt("VALtima_TownUsables", 0)}\n");
        }
      }
      File.WriteAllText(Path.Combine(Out, "towns-chunks.tsv"), tsv.ToString());
      Log($"towns: {chunks.Count} VALtima_TownChunk ZDOs in the world ({all.Count} ZDOs in all)");
      var zt = new StringBuilder("zone_x\tzone_z\tpayload\tgenerated\tobjects\tterrain_modifiers\n");
      foreach (var z in zones)
        zt.Append($"{z.x}\t{z.y}\t{(withPayload.Contains(z) ? 1 : 0)}\t{(generated(z) ? 1 : 0)}\t{(objects.TryGetValue(z, out var o) ? o : 0)}\t{(modifiers.TryGetValue(z, out var t) ? t : 0)}\n");
      File.WriteAllText(Path.Combine(Out, "towns-zones.tsv"), zt.ToString());
      Log($"towns: terrain modifiers in {modifiers.Count} of the listed zones ({modifiers.Values.Sum()} in all)");

      // 3. the terrain the game builds for each zone: (width + 1)^2 heights, row by row from the south, west to east
      started = Time.realtimeSinceStartup;
      using (var t = new BinaryWriter(File.Create(Path.Combine(Out, "towns-terrain.bin"))))
      {
        t.Write(Encoding.ASCII.GetBytes("VTTERR1\n"));
        t.Write(zones.Count);
        t.Write(width);
        t.Write(scale);
        var frame = System.Diagnostics.Stopwatch.StartNew();
        foreach (var z in zones)
        {
          var data = HeightmapBuilder.instance.RequestTerrainSync(ZoneSystem.GetZonePos(z), width, scale, lod, WorldGenerator.instance);
          t.Write((short)z.x);
          t.Write((short)z.y);
          t.Write(data.m_baseHeights.Count);
          foreach (var h in data.m_baseHeights) t.Write(h);
          if (frame.ElapsedMilliseconds > 200)
          {
            frame.Reset();
            yield return null;
            frame.Start();
          }
        }
      }
      Log($"towns: terrain of {zones.Count} zones written in {(Time.realtimeSinceStartup - started).ToString("0.0", IC)} s; {Mem()}");

      // 4. the world folder (the town pack is copied there while the world has never been saved)
      Try("world folder", () =>
      {
        var dir = ZNet.World.GetSaveDirectory(ZNet.World.m_fileSource);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : new string[0];
        Log($"towns: world folder {dir}: " + string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} B"
            + (Path.GetFileName(f) == "valtima-towns.bin" ? " md5 " + Md5(File.ReadAllBytes(f)) : ""))));
      });
    }
  }
}

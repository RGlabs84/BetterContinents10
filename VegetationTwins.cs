// Added by Wubarrk on 2026-10-04 for the vegetation twin guard (0.10.0), and modified on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

// Vegetation twins: two copies of one prefab standing in (nearly) one spot. Reported against 0.9.x as "everything had
// doubles ... a clone of everything within inches or 1 m".
//
// ZoneSystem.PlaceVegetation seeds each entry's random numbers with the world seed, the zone and the prefab name
// (seed + zone.x * 4271 + zone.y * 9187 + m_prefab.name.GetStableHashCode()) and draws x and z as
// centre +- u * (32 - m_groupRadius). So:
// - Two entries for one prefab that both apply in a zone draw the same spots, apart by their groupRadius difference (none
//   at the zone's centre, all of it at the edge): a vegetation map's "+prefab" (every entry of the prefab, in every
//   biome), a planted alt-biome combo, and a few vanilla pairs.
// - A zone filled a second time over its own objects draws every spot again, exactly: "everything doubled". Upgrade
//   World's vegetation and zone commands can do that, and so can a world put back together from mismatched save files.
// The game's only guard is IsBlocked: a ray straight down that drops a copy landing on another object's collider, only for
// entries with m_blockCheck, and blind to a zone's earlier objects while they are only data (a ghost zone).
//
// The guard, in every Better Continents world: a vegetation entry's placement is skipped within Radius (1 m) of an object of the same kind (the same
// prefab) that already stands in its zone, or that another entry placed in the same pass. An entry never blocks its own placements,
// so a prefab with a single entry keeps its vanilla density and groups except beside an object of it that stands there already, and
// a prefab with several entries also loses the copies of its later entries that would land within a metre of an earlier one's.
// bc_twins finds and removes the twins a world already has.
internal static class VegetationTwins
{
  // Horizontal metres.
  internal const float Radius = 1f;
  // A zone filled twice puts each copy on the very same x and z (the same random numbers); 10 cm allows for rounding.
  internal const float ExactRadius = 0.1f;

  internal static bool Within(float ax, float az, float bx, float bz, float radius)
  {
    float dx = ax - bx, dz = az - bz;
    return dx * dx + dz * dz <= radius * radius;
  }

  // One PlaceVegetation pass over one zone. No Unity objects, so the offline tests drive it directly.
  internal sealed class Pass
  {
    private readonly Dictionary<int, List<(float X, float Z)>> before = [];
    private readonly Dictionary<int, List<(float X, float Z, int Entry)>> placed = [];
    public int SkippedBefore { get; private set; }
    public int SkippedOther { get; private set; }

    // An object that stood in the zone before this pass.
    public void AddBefore(int prefab, float x, float z)
    {
      if (!before.TryGetValue(prefab, out var list)) before[prefab] = list = [];
      list.Add((x, z));
    }

    // True when the placement would make a twin, so the caller skips it; otherwise it is recorded as placed. entry is the
    // entry's turn in the pass, not the entry: a list holding one entry twice must not double either.
    public bool Skip(int prefab, int entry, float x, float z)
    {
      if (before.TryGetValue(prefab, out var old))
        foreach (var p in old)
          if (Within(p.X, p.Z, x, z, Radius))
          {
            SkippedBefore++;
            return true;
          }
      if (!placed.TryGetValue(prefab, out var now)) placed[prefab] = now = [];
      foreach (var p in now)
        if (p.Entry != entry && Within(p.X, p.Z, x, z, Radius))
        {
          SkippedOther++;
          return true;
        }
      now.Add((x, z, entry));
      return false;
    }
  }

  // The pass running now. PlaceVegetation runs on the main thread only, one zone at a time.
  private static Pass? pass;
  private static Vector2s passZone;
  private static int entryTurn;
  private static int entryPrefab;
  // Since the game started, for bc_twins.
  internal static int SkippedBefore, SkippedOther, ZonesFilledAgain;

  // PlaceVegetation prefix. The zone's objects so far: the ones of an earlier fill, and the locations placed just before.
  internal static void Begin(Vector2s zone)
  {
    pass = new Pass();
    passZone = zone;
    entryTurn = 0;
    entryPrefab = 0;
    var man = ZDOMan.instance;
    if (man == null) return;
    var objects = man.m_objectsBySector[ZoneSystem.SectorToIndex(zone).Sector];
    if (objects == null || objects.Count == 0) return;
    // ZDOMan.DestroyZDO only queues: a reset that empties and refills a zone in one go still lists the old objects.
    HashSet<ZDOID>? leaving = man.m_destroySendList.Count > 0 ? [.. man.m_destroySendList] : null;
    foreach (var zdo in objects)
    {
      if (leaving != null && leaving.Contains(zdo.m_uid)) continue;
      var p = zdo.GetPosition();
      // Sector 0 also holds everything the game has no sector for: beyond 256 zones out, or 1024 with WorldSectors' wide sectors.
      if (ZoneSystem.GetZone(p) != zone) continue;
      pass.AddBefore(zdo.GetPrefab(), p.x, p.z);
    }
  }

  // Each entry's turn in PlaceVegetation's loop (ZoneSystemPatch.SetCurrentVegetation).
  internal static void Entry(ZoneSystem.ZoneVegetation vegetation)
  {
    entryTurn++;
    entryPrefab = vegetation.m_prefab ? vegetation.m_prefab.name.GetStableHashCode() : 0;
  }

  // InsideClearArea postfix, the last check before PlaceVegetation places the object.
  internal static bool Skip(Vector3 point) => pass != null && entryPrefab != 0 && pass.Skip(entryPrefab, entryTurn, point.x, point.z);

  internal static void End()
  {
    if (pass == null) return;
    SkippedBefore += pass.SkippedBefore;
    SkippedOther += pass.SkippedOther;
    if (pass.SkippedBefore > 0)
    {
      ZonesFilledAgain++;
      if (ZonesFilledAgain <= 5 || ZonesFilledAgain % 1000 == 0)
        BetterContinents.Log($"Zone {passZone.x}, {passZone.y} was filled with vegetation again over its own objects: the twin guard "
          + $"kept {pass.SkippedBefore} copies out ({ZonesFilledAgain} such zones since the game started).");
    }
    pass = null;
  }

  // One object in a twin scan: read from the world by bc_twins, or made up by the offline tests.
  internal sealed class Item
  {
    public int Prefab;
    public float X, Y, Z;
    public int ZoneX, ZoneZ;
    // ZDOID parts, for a stable choice between equals.
    public long User;
    public uint Id;
    // Made by a player: never removed.
    public bool Keep;
    // A prefab players can grow (Plant.m_grownPrefabs): crops stand close on purpose, so only copies on one spot go.
    public bool Planted;
    public object? Tag;
  }

  internal sealed class Scan
  {
    public int Objects;
    // Same-prefab pairs: exact = on one spot (a zone filled twice), near = within the radius but apart.
    public int ExactPairs, NearPairs;
    public readonly Dictionary<int, int> PairsByPrefab = [];
    public readonly Dictionary<(int X, int Z), int> PairsByZone = [];
    public readonly HashSet<(int X, int Z)> ExactZones = [];
    // The extra copies, so that no two of what is left are twins by the removal rule.
    public readonly List<Item> Remove = [];
  }

  private static int Cell(float v, float size) => (int)Math.Floor(v / size);

  // Every same-prefab pair within radius, and the extra copies: exact twins only when exactOnly (near ones too
  // otherwise, but for planted prefabs only exact ones), never an object a player made. Of each set of twins the one
  // nearest the ground stays (after a terrain change an older copy floats or is buried), then the lowest ZDOID.
  // ground is asked only for objects that have a twin.
  internal static Scan Find(IReadOnlyList<Item> items, float radius, bool exactOnly, Func<Item, float> ground)
  {
    radius = Math.Max(radius, ExactRadius);
    var scan = new Scan { Objects = items.Count };
    var grid = new Dictionary<(int Prefab, int X, int Z), List<int>>();
    for (int i = 0; i < items.Count; i++)
    {
      var key = (items[i].Prefab, Cell(items[i].X, radius), Cell(items[i].Z, radius));
      if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
      list.Add(i);
    }
    var twins = new Dictionary<int, List<(int Other, bool Exact)>>();
    void Link(int a, int b, bool exact)
    {
      if (!twins.TryGetValue(a, out var list)) twins[a] = list = [];
      list.Add((b, exact));
    }
    for (int i = 0; i < items.Count; i++)
    {
      var a = items[i];
      int cx = Cell(a.X, radius), cz = Cell(a.Z, radius);
      for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
          if (!grid.TryGetValue((a.Prefab, cx + dx, cz + dz), out var cell)) continue;
          foreach (int j in cell)
          {
            if (j <= i) continue;
            var b = items[j];
            if (!Within(a.X, a.Z, b.X, b.Z, radius)) continue;
            bool exact = Within(a.X, a.Z, b.X, b.Z, ExactRadius);
            if (exact)
            {
              scan.ExactPairs++;
              scan.ExactZones.Add((a.ZoneX, a.ZoneZ));
            }
            else
              scan.NearPairs++;
            scan.PairsByPrefab[a.Prefab] = scan.PairsByPrefab.TryGetValue(a.Prefab, out var n) ? n + 1 : 1;
            var zone = (a.ZoneX, a.ZoneZ);
            scan.PairsByZone[zone] = scan.PairsByZone.TryGetValue(zone, out var z) ? z + 1 : 1;
            Link(i, j, exact);
            Link(j, i, exact);
          }
        }
    }
    var order = twins.Keys
      .Select(i => (Index: i, Off: Math.Abs(items[i].Y - ground(items[i]))))
      .Select(t => (t.Index, Off: float.IsNaN(t.Off) ? 0f : t.Off))
      .OrderBy(t => items[t.Index].Keep ? 0 : 1)
      .ThenBy(t => t.Off)
      .ThenBy(t => items[t.Index].User)
      .ThenBy(t => items[t.Index].Id)
      .Select(t => t.Index);
    var kept = new HashSet<int>();
    var removed = new HashSet<int>();
    foreach (int i in order)
    {
      if (removed.Contains(i)) continue;
      kept.Add(i);
      foreach (var (j, exact) in twins[i])
      {
        if (kept.Contains(j) || removed.Contains(j) || items[j].Keep) continue;
        if (!exact && (exactOnly || items[j].Planted)) continue;
        removed.Add(j);
        scan.Remove.Add(items[j]);
      }
    }
    return scan;
  }

  // What this machine holds of the vegetation: every prefab of ZoneSystem.m_vegetation, Expand World Data's included.
  internal static List<Item> Collect(out Dictionary<int, string> names)
  {
    names = [];
    foreach (var vegetation in ZoneSystem.instance.m_vegetation)
      if (vegetation.m_prefab)
        names[vegetation.m_prefab.name.GetStableHashCode()] = vegetation.m_prefab.name;
    var planted = new HashSet<int>();
    foreach (var prefab in ZNetScene.instance.m_prefabs)
      if (prefab && prefab.TryGetComponent<Plant>(out var plant))
        foreach (var grown in plant.m_grownPrefabs)
          if (grown)
            planted.Add(grown.name.GetStableHashCode());
    var items = new List<Item>();
    foreach (var zdo in ZDOMan.instance.m_objectsByID.Values)
    {
      int prefab = zdo.GetPrefab();
      if (!names.ContainsKey(prefab)) continue;
      var p = zdo.GetPosition();
      var zone = ZoneSystem.GetZone(p);
      items.Add(new Item
      {
        Prefab = prefab,
        X = p.x,
        Y = p.y,
        Z = p.z,
        ZoneX = zone.x,
        ZoneZ = zone.y,
        User = zdo.m_uid.UserID,
        Id = zdo.m_uid.ID,
        Keep = zdo.GetLong(ZDOVars.s_creator) != 0,
        Planted = planted.Contains(prefab),
        Tag = zdo.m_uid,
      });
    }
    return items;
  }
}

// bc_twins: counts and removes vegetation twins (VegetationTwins). Any player can count what their machine holds; removal
// runs where the world is: single player, a host, or a dedicated server through "bc_twins server ..." (admins only).
public static class VegetationTwinCommands
{
  public const string Usage = "bc_twins [radius] | remove [radius] | remove confirm [radius] | server <the same> | help";
  // Destroys per frame: ZDOMan sends each frame's as one message, 12 bytes an object, and one message tops out at 512 KiB.
  private const int BatchSize = 1000;
  private const float MaxRadius = 5f;
  private static bool Removing;

  public static void Register()
  {
    // Named arguments: 1.0.15 inserted hideBehindDevCommands into the constructor (Terminal.cs:152).
    new Terminal.ConsoleCommand("bc_twins",
      "[radius | remove [confirm] [radius] | server ... | help] - Better Continents: find and remove vegetation twins, two copies of one prefab on one spot (bc_twins help)",
      args => Run(args),
      isCheat: false, isNetwork: false, onlyServer: false,
      optionsFetcher: () => ["remove", "server", "help"]);
  }

  // The answer goes to the console that asked, the log, and the admin a server runs it for (ConsoleTools).
  private static void Run(Terminal.ConsoleEventArgs args) => RunLine(ConsoleTools.Rest(args, "bc_twins"), ConsoleTools.Output(args.Context));

  // bc_twins, and "bc twins" in the host's debug tree.
  internal static void RunLine(string text, Action<string> output)
  {
    var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim().ToLowerInvariant()).ToList();
    if (words.Count > 0 && words[0] == "server")
    {
      SendToServer(string.Join(" ", words.Skip(1)), output);
      return;
    }
    if (words.Count > 0 && (words[0] == "help" || words[0] == "?"))
    {
      Help(output);
      return;
    }
    bool remove = false, confirm = false;
    float? radius = null;
    foreach (var word in words)
    {
      if (word == "remove") remove = true;
      else if (word == "confirm") confirm = true;
      else if (float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r >= VegetationTwins.ExactRadius && r <= MaxRadius) radius = r;
      else
      {
        output($"bc_twins: '{word}' is not understood (a radius is {VegetationTwins.ExactRadius:0.0#} to {MaxRadius:0} m).");
        output(Usage);
        return;
      }
    }
    if (confirm && !remove)
    {
      output("bc_twins: 'confirm' goes with 'remove': bc_twins remove confirm");
      return;
    }
    var net = ZNet.instance;
    if (net == null || ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
    {
      output("bc_twins: load a world first.");
      return;
    }
    if (Removing)
    {
      output("bc_twins: a removal is still running.");
      return;
    }
    bool server = net.IsServer();
    if (remove && !server)
    {
      output("bc_twins: removal runs where the world is. Ask the server: bc_twins server " + string.Join(" ", words) + " (admins only).");
      return;
    }
    // A plain remove takes exact twins only, the copies of a zone filled twice; a radius thins near twins too.
    bool exactOnly = remove && radius == null;
    float scanRadius = radius ?? VegetationTwins.Radius;
    var items = VegetationTwins.Collect(out var names);
    var generator = WorldGenerator.instance;
    var scan = VegetationTwins.Find(items, scanRadius, exactOnly, item => generator != null ? generator.GetHeight(item.X, item.Z) : float.NaN);
    if (!remove)
    {
      Report(scan, names, scanRadius, server, output);
      return;
    }
    var rule = exactOnly
      ? "exact twins only, two copies on one spot"
      : $"twins within {scanRadius:0.0#} m, crops players grow only on one spot";
    if (scan.Remove.Count == 0)
    {
      output($"bc_twins remove: nothing to remove ({rule}).");
      return;
    }
    var zones = scan.Remove.Select(i => (i.ZoneX, i.ZoneZ)).Distinct().Count();
    if (!confirm)
    {
      output($"bc_twins remove: would delete {scan.Remove.Count:N0} extra copies in {zones:N0} zones ({rule}; the copy nearest the ground stays, player-made objects never go).");
      output("This cannot be undone: back up the world first, then run 'bc_twins remove confirm" + (radius != null ? $" {scanRadius.ToString("0.0#", CultureInfo.InvariantCulture)}" : "") + "'.");
      return;
    }
    Removing = true;
    output($"bc_twins: removing {scan.Remove.Count:N0} extra copies in {zones:N0} zones ({rule}), {BatchSize} a frame.");
    BetterContinents.instance.StartCoroutine(RemoveAll(scan.Remove.Select(i => ((ZDOID)i.Tag!, i.Prefab)).ToList(), output));
  }

  private static IEnumerator RemoveAll(List<(ZDOID Id, int Prefab)> extra, Action<string> output)
  {
    int removed = 0, done = 0;
    try
    {
      foreach (var (id, prefab) in extra)
      {
        var man = ZDOMan.instance;
        if (man == null)
        {
          output($"bc_twins: the world closed after {removed:N0} removals.");
          yield break;
        }
        // ZDOs are pooled: look each up again, and check it is still the same kind of object.
        var zdo = man.GetZDO(id);
        if (zdo != null && zdo.GetPrefab() == prefab)
        {
          // Only the owner can destroy an object; the server takes it over first, as vanilla does.
          zdo.SetOwner(ZDOMan.GetSessionID());
          var view = ZNetScene.instance ? ZNetScene.instance.FindInstance(zdo) : null;
          if (view)
            ZNetScene.instance!.Destroy(view.gameObject);
          else
            man.DestroyZDO(zdo);
          removed++;
        }
        if (++done % BatchSize == 0) yield return null;
      }
      output($"bc_twins: removed {removed:N0} extra copies. The world saves as usual ('save' saves now).");
    }
    finally
    {
      Removing = false;
    }
  }

  private static void Report(VegetationTwins.Scan scan, Dictionary<int, string> names, float radius, bool server, Action<string> output)
  {
    var r = radius.ToString("0.0#", CultureInfo.InvariantCulture);
    output($"bc_twins: {scan.Objects:N0} vegetation objects, " + (server ? "the whole world." : "what this client holds: the area around you (the server holds the whole world: bc_twins server)."));
    output($"Exact twins, two copies on one spot (a zone filled twice): {scan.ExactPairs:N0} in {scan.ExactZones.Count:N0} zones.");
    output($"Near twins, the same prefab within {r} m but apart: {scan.NearPairs:N0}.");
    if (scan.PairsByPrefab.Count > 0)
      output("Most: " + string.Join(", ", scan.PairsByPrefab.OrderByDescending(p => p.Value).Take(8)
        .Select(p => $"{(names.TryGetValue(p.Key, out var name) ? name : p.Key.ToString(CultureInfo.InvariantCulture))} {p.Value:N0}")));
    // Positions only where they are the player's own business, as with the alt-biome report.
    if (scan.PairsByZone.Count > 0 && (server || Terminal.m_cheat))
      output("Worst zones: " + string.Join("; ", scan.PairsByZone.OrderByDescending(p => p.Value).Take(5)
        .Select(p => $"{p.Key.X}, {p.Key.Z} (x {p.Key.X * 64}, z {p.Key.Z * 64}): {p.Value:N0}")));
    output($"Twin guard since the game started: {VegetationTwins.SkippedBefore:N0} copies kept out of {VegetationTwins.ZonesFilledAgain:N0} zones filled again, "
      + $"{VegetationTwins.SkippedOther:N0} where two entries met.");
    if (scan.ExactPairs + scan.NearPairs > 0)
      output("'bc_twins remove' shows what a cleanup of the exact twins would delete; 'bc_twins remove 1' takes near ones too.");
  }

  private static void SendToServer(string rest, Action<string> output)
  {
    var net = ZNet.instance;
    if (net == null)
    {
      output("bc_twins server: not connected to a server.");
      return;
    }
    if (net.IsServer())
    {
      // This machine runs the world; ZNet.RemoteCommand would log a null peer here.
      Console.instance.TryRunCommand(("bc_twins " + rest).Trim());
      return;
    }
    net.RemoteCommand(("bc_twins " + rest).Trim());
    output("Asked the server (admins only); its answer comes back here and goes to its log.");
  }

  private static void Help(Action<string> output)
  {
    output(Usage);
    output("A twin is two copies of one vegetation prefab on one spot or within the radius (1 m by default): a zone filled");
    output("with vegetation a second time over its own objects, or two entries for one prefab meeting. Better Continents");
    output("keeps new ones out as zones generate; this command finds and removes the ones a world already has.");
    output("bc_twins [radius]               count them (a client counts the area around it)");
    output("bc_twins remove [radius]        show what a cleanup would delete: exact twins, or near ones too with a radius");
    output("bc_twins remove confirm [radius] delete them: the copy nearest the ground stays; player-made objects never go,");
    output("                                crops players grow only when on one spot. Back up the world first.");
    output("bc_twins server ...             the same on a dedicated server (admins only)");
  }
}

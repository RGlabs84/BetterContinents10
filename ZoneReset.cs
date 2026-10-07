// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and modified on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// Zone regeneration for debug mode: the rules and the work, with the game kept out of them so that tools/zone-tests can drive
// them directly. ZoneRegen is the game's side (it reads the world, hands this its facts, and does what this decides).
//
// What a zone is, to the game. ZoneSystem.SpawnZone fills a zone with objects once (PlaceLocations, PlaceVegetation and
// PlaceZoneCtrl, SpawnMode.Full) and writes it into m_generatedZones; from then on its objects are ZDOs of the world and the
// game never fills the zone again. So a zone generates again, with other settings, only when
//   - its objects are destroyed the way the game destroys them (ZNetScene.Destroy for one that is loaded, otherwise
//     ZDOMan.DestroyZDO, which tells every client),
//   - its location is placed again (m_locationInstances[zone].m_placed = false; PlaceLocations skips a placed one),
//   - it stops being generated (m_generatedZones), and
//   - a loaded copy of it is thrown away (m_zones[zone].m_root), or the game goes on using that.
// It then generates when somebody comes near (ZoneSystem.Update: ten times a second, one zone each, the one a player stands in first).
//
// What stays. A world being worked on can have a base in it, and a player in it:
//   - Players are never destroyed, nor is anything a player made. Piece.SetCreator is called only by Player.PlacePiece
//     (Player.cs:3102, Piece.cs:429), so a creator on an object means a player placed it (buildings, chests, ships, portals,
//     seeds and saplings) and nothing the world made has one. Tombstones (Player_tombstone, TombStone.Setup) and creatures a
//     player tamed (Character.cs:4329) stay too: players own them without having placed them.
//   - Ground work stays. The hoe, the cultivator and the pickaxe leave no object behind: a TerrainOp applies its operation to the
//     zone's terrain compiler and destroys itself (TerrainOp.cs:118-136), so fields, paths, roads and levelled or dug ground live
//     only in the compiler's TCData (TerrainComp.Save), and nothing there has a creator. A compiler with one edited or painted
//     vertex is ground work (Kind.Ground, TerrainBorder.HasEdits), and protects like a piece. A crop is protected through its
//     field: the grown crop has no creator (Plant.Grow makes it from its own prefab, Plant.cs:181-213) but stands on cultivated ground.
//   - A zone within one zone (3 x 3) of any of those is not reset at all, so a base keeps its surroundings.
//   - The player at this keyboard is the exception: debug mode is about the ground under their feet, so their own zones reset
//     around them (they are never destroyed). A player connected from another machine keeps the zones their client has loaded:
//     their client still has the settings it joined with (a bc change reaches only this machine), so new objects would stand on
//     its old ground. Their game loads a square of zones around them as wide as their near simulation distance (ZNetPeer.cs:25,
//     SimulationDistance.cs:18; 2 by default, a 5 x 5), so that is their radius, and never less than a piece's one.
//     And nobody's dungeon is reset under them: a dungeon is built at 5000 m above its entrance's zone (Location.cs:60) and
//     Character.InInterior is any height over 3000 m (Character.cs:4372).
//   - A location is kept whole. The game keeps a location's radius inside its zone when it picks the spot (ZoneSystem.cs:2171-2172),
//     but a pin of a Better Continents map is used as it is (BetterContinents.ZoneSystemPatch.cs, TryRegister), so the objects of a
//     placed location can stand in the zones beside its own. Every zone that the circle of a placed location touches is linked to
//     its home zone, and zones linked (through one location or a chain of them) form a group: if a zone of the group (generated
//     or not) is near something that protects, every generated zone of the group stays. Otherwise a kept home would lose the parts
//     that stand in a reset neighbour for good, and a reset home beside a kept neighbour would put them down a second time.
//     The circle is the location's exterior radius. A dungeon's interior is built inside one zone, the zone of its generator: the
//     generator's bounds are that zone's 64 m cube (DungeonGenerator.cs:211-217) and every room must lie inside them
//     (IsInsideDungeon, :967-1010), and the interior's environment volume is 64 m wide at that zone's centre (Location.cs:57-63);
//     a location with a custom interior transform has its generator moved to the centre of the zone it is placed in (ZoneSystem.cs:
//     2422-2438). So the interior radius links no zone, and a group made with it would chain across a map with many pins for nothing.
//   - What a location left in a zone nobody generated. A placed location whose circle reaches a zone that was never generated leaves
//     its parts there, and its home puts another copy down when it generates again, so every reset would add one: the copies would
//     stand on one another until somebody generated that zone. A group that is reset whole (nothing near it protects) has its zones
//     that are not generated planned as well, as strays: clear-only work, taken in its turn with the rest. A stray has its turn right
//     after the nearest zone of its group that is reset, never before it (MakePlan), so that a zone of the group has emptied and waits
//     whenever a stray is gone (a save takes the turns of the groups of the zones that wait, below). A stray has no generated mark,
//     location or loaded copy to reset and adds to none of the counts, but its home is not finished before its turn, as for any zone
//     of the group. A stray with something that protects in or near it is left alone. A stray that somebody has come near since the
//     plan, and that is generated by its turn, holds a fill of its own besides the old parts of the location, and left as it is it
//     would hold two copies of them once its home generated again: it becomes a zone to reset like the rest (Work.Promote), counted,
//     looked at for what protects, finished with its generated mark and loaded copy.
//
// The work, and when a zone is done. A reset zone has its objects destroyed, then its location unplaced, then it is no longer
// generated, then its loaded copy is thrown away. The last three are the zone's finish, and a zone is finished only when the
// objects of every zone beside it that is reset too (its 3 x 3, and the zones of its location group) are destroyed or the zone
// was left alone: a zone that generated again while a neighbour still held the old objects would put a location down in
// the neighbour's area, and then the neighbour's turn would destroy it.
//   - A save must not store half a zone. A zone's objects are destroyed over several frames and its finish can wait for a
//     neighbour, and a zone that is saved with some of its objects gone and the rest still there, or empty but generated, never
//     fills again. So a save (ZNet.SaveWorld) while the work is under way destroys the rest of the zone being emptied, sends the
//     destroy queue (the objects leave the lists before the save copies them) and mends the borders owed so far (ZoneRegen has
//     the patch), and the save leaves the zones emptied but not finished out of m_generatedZones with their locations not placed
//     (ZoneSystem.PrepareSave copies both before it writes them). The live game keeps them as they are until their finish. Such a
//     zone puts its whole location down again when the world loads, so the zones of its location group that have not had their
//     turn (the strays too) take it first, without a budget: a file holds a group wholly emptied or untouched, never one whose
//     home would place the location over the old parts that stand beside it. A group that has lost a zone always has one that
//     waits, since a stray is never cleared before the first zone of its group that is reset is, and that zone waits until every
//     turn of the group is over (Memory carries it into the next run if a second request stops this one). Each zone's objects are
//     sent on their own, a message tops out at 512 KiB. A zone that something protects at that moment is left alone, which is the
//     case that cannot be undone (below).
//   - A second request stops the first: the zone being emptied is finished off first. The zones emptied but not finished are
//     remembered for this world (Memory) and the next run resets them again whatever protects them by then, since they are empty
//     and would stay bare. A save that comes before the next run has made its work (its scan and its plan take frames) has the first
//     run's work take the turns of their groups (ZoneRegen.BeforeSave), as any save does. A run that an error stops finishes them
//     itself (Work.FinishPending): the zones beside them will not be reached by anything, and generated and bare they would stay so
//     for the session.
//   - Who is near is looked at again at the start of every frame (IZoneWorld.LiveProtectors): the plan's picture of the world is
//     minutes old when a zone's turn comes. That is the players connected, each with the square their game keeps, and the zones where
//     the player at this machine has placed something since the run began (Piece.SetCreator). A zone that has not begun, and is in
//     that area (or in the 3 x 3 around a zone left alone at its turn), is left alone, and so are the zones of its location group that
//     have not had their turn; one that has begun is finished.
//   - The ground at a border follows what happened. A cleared zone's terrain compiler is destroyed, so a zone that stays and
//     meets it still holds its edits along the shared edge, and the two grounds no longer meet there: a crack, or a step of up to
//     8 m. After the work, and at a save, every zone beside a zone that was actually cleared (and is not cleared itself) has the
//     edits on the sides that meet it cleared. A run that was stopped leaves that owed to the next one.
//
// What goes with a spawner. A CreatureSpawner remembers the creature it made (CreatureSpawner.Spawn: a Spawned connection) and
// makes another only when that one is gone. A reset zone gets new spawners with no memory, so the creature the old one made would
// stay and a second would be made beside it, one more for every reset. So the creature goes with its spawner, wherever it has
// walked to, unless it is one that stays or it stands in a zone that is left alone. The connection is read when the spawner is
// destroyed, not when its zone was listed: it may have made one since.
//
// What is not done:
//   - A tree grown from a sapling has no creator (Plant.Grow): it is the world's, and goes with its zone unless something protects it.
//   - A protector that appears beside a zone that is already cleared cannot save it; nor can a zone of a location group that was
//     cleared before another zone of the group was left alone be filled again.
//   - A location whose home the game generates while a run goes on (one an earlier run reset, or one nobody had generated): its parts
//     in zones whose turns are over stay and those in zones still to come are cleared, so it ends half placed or, when the home is a
//     stray, twice; the next 'bc regen' sets it right.
//   - Zones beyond 65.5 km out share one sector bucket, as those beyond 16.4 km do in a world that has no wide sectors
//     (WorldSectors; ZoneSystem.SectorToIndex), so looking at them is slower.
//   - ZDOMan.m_deadZDOs grows with each destroyed object, as with any destroy.
internal static class ZoneReset
{
  /// <summary>What the rules need to know about one object.</summary>
  [Flags]
  internal enum Kind
  {
    None = 0,
    /// <summary>A player's character.</summary>
    Player = 1,
    /// <summary>The character of the player at this machine, not one a peer plays (with Player).</summary>
    Local = 2,
    /// <summary>Above 3000 m: in a dungeon.</summary>
    Interior = 4,
    /// <summary>The items a player died with.</summary>
    Tombstone = 8,
    /// <summary>Placed by a player: it has a creator.</summary>
    Piece = 16,
    /// <summary>A creature a player tamed.</summary>
    Tamed = 32,
    /// <summary>A zone's terrain compiler with an edited or painted vertex: a field, a path, a road, levelled or dug ground.</summary>
    Ground = 64,
  }

  internal static class Rules
  {
    /// <summary>A zone within this many zones of a protector is left alone: 1 is the 3 x 3 around it.</summary>
    internal const int ProtectRadius = 1;
    /// <summary>Character.InInterior: higher than this is inside a dungeon.</summary>
    internal const float InteriorHeight = 3000f;

    /// <summary>Whether what stands in a zone keeps that zone, and the zones around it, from being reset.</summary>
    internal static bool Protects(Kind kind) =>
      (kind & (Kind.Piece | Kind.Tombstone | Kind.Ground)) != 0
      || ((kind & Kind.Player) != 0 && ((kind & Kind.Local) == 0 || (kind & Kind.Interior) != 0));

    /// <summary>Whether an object is never destroyed, in a zone that is reset.</summary>
    internal static bool Survives(Kind kind) => (kind & (Kind.Player | Kind.Piece | Kind.Tombstone | Kind.Tamed | Kind.Ground)) != 0;

    /// <summary>How far around a player connected from another machine zones are kept: the near simulation distance of their
    /// game (the square it keeps loaded), and never less than a piece's.</summary>
    internal static int PeerRadius(int nearSimulationDistance) => Math.Max(ProtectRadius, nearSimulationDistance);

    /// <summary>The radius for a player whose peer cannot be found (the host's own player in a dungeon, one who has just left): the
    /// widest of those connected, and at least a piece's.</summary>
    internal static int FallbackRadius(IEnumerable<int> nearSimulationDistances) => nearSimulationDistances.Aggregate(ProtectRadius, Math.Max);
  }

  /// <summary>One object: the handle to find it again, what it is, what the rules know about it, and the zone it stands in.</summary>
  internal readonly struct Obj(ZDOID id, int prefab, Kind kind, Vector2s zone = default)
  {
    public readonly ZDOID Id = id;
    public readonly int Prefab = prefab;
    public readonly Kind Kind = kind;
    public readonly Vector2s Zone = zone;
  }

  /// <summary>A zone that holds something the rules protect, and how many zones around it are kept with it.</summary>
  internal readonly struct Protector(Vector2s zone, int radius = Rules.ProtectRadius)
  {
    public readonly Vector2s Zone = zone;
    public readonly int Radius = radius;
    public static implicit operator Protector(Vector2s zone) => new(zone);
  }

  /// <summary>The zones that hold something the rules protect, each with its radius (the largest, when several stand in one zone).</summary>
  internal sealed class Protectors : IEnumerable<Protector>
  {
    private readonly Dictionary<Vector2s, int> radii = new(Comparer);

    public int Count => radii.Count;

    public void Add(Vector2s zone, int radius)
    {
      if (!radii.TryGetValue(zone, out var known) || known < radius)
        radii[zone] = radius;
    }

    public IEnumerator<Protector> GetEnumerator()
    {
      foreach (var pair in radii)
        yield return new Protector(pair.Key, pair.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
  }

  /// <summary>A location the game has placed (ZoneSystem.m_locationInstances with m_placed): its home zone, where its centre is,
  /// and how far its objects reach (its exterior radius: a dungeon's interior stays inside one zone, see the top of this file).</summary>
  internal readonly struct PlacedLocation(Vector2s zone, float x, float z, float radius)
  {
    public readonly Vector2s Zone = zone;
    public readonly float X = x;
    public readonly float Z = z;
    public readonly float Radius = radius;
  }

  /// <summary>Zones as keys. Vector2s hashes as x ^ y, which gives the 78,000 zones of a big world some 440 distinct values and makes
  /// every set and dictionary of them crawl (a plan of such a world took hundreds of milliseconds); this has a hash for every zone.</summary>
  internal sealed class ZoneComparer : IEqualityComparer<Vector2s>
  {
    public bool Equals(Vector2s a, Vector2s b) => a.x == b.x && a.y == b.y;
    public int GetHashCode(Vector2s zone) => (zone.x << 16) | (ushort)zone.y;
  }

  internal static readonly IEqualityComparer<Vector2s> Comparer = new ZoneComparer();

  /// <summary>The world, as far as the work needs it. ZoneRegen's GameWorld is the real one; the tests have another.</summary>
  internal interface IZoneWorld
  {
    /// <summary>False once the world is gone (the player went to the menu): the work stops.</summary>
    bool Alive { get; }
    /// <summary>A new frame begins: what was destroyed in the last has been told to the clients by now.</summary>
    void NewFrame();
    /// <summary>Everything destroyed so far is told to every machine now, this one included: it is out of the world's lists when
    /// this returns, and so out of a save.</summary>
    void Flush();
    /// <summary>What protects, as the world is now: the players connected, each with how many zones around them their game keeps,
    /// and the zones where the player at this machine has placed something since the work began.</summary>
    IEnumerable<Protector> LiveProtectors();
    /// <summary>Whether the zone is generated now (ZoneSystem.m_generatedZones): a zone that was not when the plan was made may be by
    /// its turn, if somebody has come near it since.</summary>
    bool IsGenerated(Vector2s zone);
    /// <summary>Every object that stands in the zone, by position, in the order the game lists them.</summary>
    List<Obj> ObjectsIn(Vector2s zone);
    /// <summary>The object with this handle, as it is now; null when there is none.</summary>
    Obj? Find(ZDOID id);
    /// <summary>Destroys the object; false when it was no longer there, or was destroyed already. A spawner hands back the creature
    /// it holds at this moment (none for anything else).</summary>
    bool Destroy(Obj obj, out ZDOID spawned);
    /// <summary>The zone's location, if it has one, is to be placed again, at the ground's new height.</summary>
    void Unplace(Vector2s zone);
    /// <summary>The zone is no longer generated.</summary>
    void Ungenerate(Vector2s zone);
    /// <summary>A loaded copy of the zone is thrown away.</summary>
    void DestroyRoot(Vector2s zone);
    /// <summary>A left-alone zone's ground edits lose their hold on the edges that meet cleared zones; the number of vertices.</summary>
    int AdjustBorder(Vector2s zone, Edges edges);
  }

  // ------------------------------------------------------------------------------------------------ the plan

  /// <summary>The sides of a zone that meet a zone being reset. North is +z and East is +x, the ways a zone's id grows.</summary>
  [Flags]
  internal enum Edges
  {
    None = 0,
    North = 1,
    East = 2,
    South = 4,
    West = 8,
    NorthEast = 16,
    NorthWest = 32,
    SouthEast = 64,
    SouthWest = 128,
  }

  internal sealed class Plan
  {
    /// <summary>The zones to reset, in the order they are done: the nearest to a player first.</summary>
    public readonly List<Vector2s> Reset = [];
    /// <summary>The generated zones that are left alone.</summary>
    public readonly HashSet<Vector2s> Kept = new(Comparer);
    /// <summary>Every zone, generated or not, within reach of what protects, and the generated zones kept for a location.</summary>
    public HashSet<Vector2s> Covered = new(Comparer);
    /// <summary>Of the kept zones, those kept only because a location they share reaches a zone that something protects.</summary>
    public int KeptForLocations;
    /// <summary>The zones an earlier run emptied and did not finish: they are in Reset whatever protects them now.</summary>
    public readonly HashSet<Vector2s> Carried = new(Comparer);
    /// <summary>Every generated zone at the time of the plan.</summary>
    internal HashSet<Vector2s> GeneratedZones = new(Comparer);
    internal readonly Dictionary<Vector2s, int> GroupOf = new(Comparer);
    internal readonly List<Vector2s[]> Groups = [];
    /// <summary>Zones nobody has generated that a location of a group being reset reaches into: what it left there goes with it. They are
    /// not among the zones to reset, and no count of the run includes them (unless the game generates one by its turn: Work.Promote).</summary>
    public readonly List<Vector2s> Strays = [];
    /// <summary>Every zone that has a turn, in the order they have it: the zones to reset nearest the players first, each stray right
    /// after the nearest zone to reset of its group.</summary>
    internal readonly List<Vector2s> Turns = [];
    public int Generated => Reset.Count + Kept.Count;

    /// <summary>The zones that share a location with this one (this one included), generated or not; none when it shares none.</summary>
    public IReadOnlyList<Vector2s> GroupMembers(Vector2s zone) => GroupOf.TryGetValue(zone, out var at) ? Groups[at] : Array.Empty<Vector2s>();
  }

  /// <summary>What a run leaves for the next one, for the world it is in (ZoneRegen forgets it when the world changes).</summary>
  internal sealed class Memory
  {
    /// <summary>Zones emptied and not finished: still marked generated, with their location placed. A save leaves them out.</summary>
    public readonly HashSet<Vector2s> Pending = new(Comparer);
    /// <summary>Zones emptied since the last border pass that was completed: their neighbours owe the ground a mend.</summary>
    public readonly HashSet<Vector2s> Cleared = new(Comparer);
  }

  private static void AddSquare(HashSet<Vector2s> into, Vector2s zone, int radius)
  {
    for (int dx = -radius; dx <= radius; dx++)
      for (int dy = -radius; dy <= radius; dy++)
        into.Add(new Vector2s(zone.x + dx, zone.y + dy));
  }

  /// <summary>The zones within <paramref name="radius"/> zones of any of <paramref name="zones"/>, those included.</summary>
  internal static HashSet<Vector2s> Dilate(IEnumerable<Vector2s> zones, int radius)
  {
    var result = new HashSet<Vector2s>(Comparer);
    foreach (var zone in zones)
      AddSquare(result, zone, radius);
    return result;
  }

  /// <summary>The zones within each protector's radius of its zone, those included.</summary>
  internal static HashSet<Vector2s> Dilate(IEnumerable<Protector> protectors)
  {
    var result = new HashSet<Vector2s>(Comparer);
    foreach (var protector in protectors)
      AddSquare(result, protector.Zone, protector.Radius);
    return result;
  }

  /// <summary>The zones a location's circle touches, its home zone first: a zone is touched when the circle reaches its 64 m square.</summary>
  internal static List<Vector2s> TouchedZones(PlacedLocation location)
  {
    var touched = new List<Vector2s> { location.Zone };
    // (A circle that reaches over a kilometre is no location the game places; the cap keeps a bad value from listing thousands of zones.)
    double radius = location.Radius > 0f ? Math.Min(location.Radius, 1000f) : 0.0;
    double x = location.X, z = location.Z;
    // ZoneSystem.GetZone: the zone with id n spans 64 m around n * 64.
    int fromX = (int)Math.Floor((x - radius + 32.0) / 64.0), toX = (int)Math.Floor((x + radius + 32.0) / 64.0);
    int fromZ = (int)Math.Floor((z - radius + 32.0) / 64.0), toZ = (int)Math.Floor((z + radius + 32.0) / 64.0);
    for (int zx = fromX; zx <= toX; zx++)
      for (int zz = fromZ; zz <= toZ; zz++)
      {
        var zone = new Vector2s(zx, zz);
        if (zone.Equals(location.Zone))
          continue;
        // The nearest point of the zone's square to the centre.
        double dx = Math.Max(Math.Abs(x - zx * 64.0) - 32.0, 0.0), dz = Math.Max(Math.Abs(z - zz * 64.0) - 32.0, 0.0);
        if (dx * dx + dz * dz <= radius * radius)
          touched.Add(zone);
      }
    return touched;
  }

  // Zones linked through the locations that reach beyond their own: the groups, each with every zone it touches.
  private static void LinkLocations(Plan plan, IEnumerable<PlacedLocation> locations)
  {
    var parent = new Dictionary<Vector2s, Vector2s>(Comparer);
    Vector2s Root(Vector2s zone)
    {
      if (!parent.TryGetValue(zone, out var up))
      {
        parent[zone] = zone;
        return zone;
      }
      var root = zone;
      while (!parent[root].Equals(root))
        root = parent[root];
      // Everything on the way now points at the root.
      while (!zone.Equals(root))
      {
        up = parent[zone];
        parent[zone] = root;
        zone = up;
      }
      return root;
    }
    foreach (var location in locations)
    {
      var touched = TouchedZones(location);
      if (touched.Count < 2)
        continue;
      var root = Root(touched[0]);
      for (int i = 1; i < touched.Count; i++)
        parent[Root(touched[i])] = root;
    }
    var members = new Dictionary<Vector2s, List<Vector2s>>(Comparer);
    foreach (var zone in parent.Keys.ToList())
    {
      var root = Root(zone);
      if (!members.TryGetValue(root, out var list))
        members[root] = list = [];
      list.Add(zone);
    }
    foreach (var group in members.Values.Where(g => g.Count > 1))
    {
      group.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
      foreach (var zone in group)
        plan.GroupOf[zone] = plan.Groups.Count;
      plan.Groups.Add(group.ToArray());
    }
  }

  /// <summary>Which of the generated zones are reset, in what order, and which are left alone. <paramref name="protectors"/>
  /// are the zones that hold something the rules protect, each with how far around it zones are kept (a zone that is not
  /// generated can hold one too); <paramref name="focus"/> are the zones people are in, which are done first (none: the world's
  /// centre); <paramref name="locations"/> are the locations that are placed: a group of zones that share one is kept whole when
  /// any zone of it is covered, and otherwise reset whole, its zones that are not generated being planned as strays;
  /// <paramref name="carried"/> are zones an earlier run emptied and did not finish, which are reset
  /// whatever protects them now (nothing is left in them, and kept they would stay bare).</summary>
  internal static Plan MakePlan(IEnumerable<Vector2s> generated, IEnumerable<Protector> protectors, IReadOnlyList<Vector2s> focus,
    IEnumerable<PlacedLocation>? locations = null, IEnumerable<Vector2s>? carried = null)
  {
    var plan = new Plan();
    var all = new HashSet<Vector2s>(generated, Comparer);
    plan.GeneratedZones = all;
    var covered = Dilate(protectors);
    plan.Covered = covered;
    if (locations != null)
      LinkLocations(plan, locations);

    // A location is kept whole: one covered zone of a group keeps every generated zone of it. Covered is read as it was before
    // the group's own zones are added, since they protect nothing around them. A group that nothing covers is reset whole, and what
    // its locations left in the zones nobody generated goes with it: those are the strays (a group with no generated zone has no
    // home to put the location down again, so nothing of it is cleared).
    var viaLocation = new List<Vector2s>();
    var strays = new HashSet<Vector2s>(Comparer);
    foreach (var group in plan.Groups)
    {
      if (group.Any(covered.Contains))
        viaLocation.AddRange(group.Where(zone => all.Contains(zone) && !covered.Contains(zone)));
      else if (group.Any(all.Contains))
        strays.UnionWith(group.Where(zone => !all.Contains(zone)));
    }
    covered.UnionWith(viaLocation);

    if (carried != null)
      foreach (var zone in carried)
        if (all.Contains(zone))
          plan.Carried.Add(zone);
    var reset = new List<Vector2s>();
    foreach (var zone in all)
    {
      if (covered.Contains(zone) && !plan.Carried.Contains(zone))
        plan.Kept.Add(zone);
      else
        reset.Add(zone);
    }
    // A carried zone that sits in a kept location's group is not counted as kept for it.
    plan.KeptForLocations = viaLocation.Count(plan.Kept.Contains);

    // The zones to reset have their turns nearest the players first, ties by x then y. A stray has its turn right after the nearest
    // zone of its group that is reset (it sorts as that zone, one rank behind it), never before it: by then that zone has been emptied
    // and waits for the group, or the group was left alone whole. So a save never finds a stray cleared and the rest of its group
    // untouched (Work.BeforeSave takes the turns of the groups of the zones that wait), and a second request carries the zone that
    // waits into the next run (Memory).
    var near = focus.Count > 0 ? focus : new[] { Vector2s.zero };
    long FocusDistance(Vector2s z)
    {
      long best = long.MaxValue;
      foreach (var f in near)
      {
        long dx = z.x - f.x, dy = z.y - f.y;
        best = Math.Min(best, dx * dx + dy * dy);
      }
      return best;
    }
    var resetSet = new HashSet<Vector2s>(reset, Comparer);
    // The key of each group's nearest zone to reset, found once for the group and not once for each of its strays.
    var anchors = new (long Distance, int X, int Y)?[plan.Groups.Count];
    (long Distance, int X, int Y) AnchorOf(Vector2s stray)
    {
      int group = plan.GroupOf[stray];
      if (anchors[group] is { } known)
        return known;
      // (A stray's group has a generated zone, and nothing covers the group, so that zone is reset: there is always one.)
      var best = (Distance: long.MaxValue, X: int.MaxValue, Y: int.MaxValue);
      foreach (var member in plan.Groups[group])
      {
        if (!resetSet.Contains(member))
          continue;
        long distance = FocusDistance(member);
        if (distance < best.Distance || (distance == best.Distance && (member.x < best.X || (member.x == best.X && member.y < best.Y))))
          best = (distance, member.x, member.y);
      }
      anchors[group] = best;
      return best;
    }
    // (Distance, X, Y, Rank, ZoneX, ZoneY): a zone to reset sorts as itself at rank 0, a stray as its anchor at rank 1.
    var turns = reset.Concat(strays).ToList();
    var order = new (long Distance, int X, int Y, int Rank, int ZoneX, int ZoneY)[turns.Count];
    for (int i = 0; i < order.Length; i++)
    {
      var zone = turns[i];
      if (strays.Contains(zone))
      {
        var anchor = AnchorOf(zone);
        order[i] = (anchor.Distance, anchor.X, anchor.Y, 1, zone.x, zone.y);
      }
      else
      {
        order[i] = (FocusDistance(zone), zone.x, zone.y, 0, zone.x, zone.y);
      }
    }
    Array.Sort(order, (a, b) =>
      a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance)
      : a.X != b.X ? a.X.CompareTo(b.X)
      : a.Y != b.Y ? a.Y.CompareTo(b.Y)
      : a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank)
      : a.ZoneX != b.ZoneX ? a.ZoneX.CompareTo(b.ZoneX)
      : a.ZoneY.CompareTo(b.ZoneY));
    foreach (var item in order)
    {
      var zone = new Vector2s(item.ZoneX, item.ZoneY);
      plan.Turns.Add(zone);
      if (strays.Contains(zone))
        plan.Strays.Add(zone);
      else
        plan.Reset.Add(zone);
    }
    return plan;
  }

  private static Edges Facing(int dx, int dy) => (dx, dy) switch
  {
    (0, 1) => Edges.North,
    (1, 0) => Edges.East,
    (0, -1) => Edges.South,
    (-1, 0) => Edges.West,
    (1, 1) => Edges.NorthEast,
    (-1, 1) => Edges.NorthWest,
    (1, -1) => Edges.SouthEast,
    (-1, -1) => Edges.SouthWest,
    _ => Edges.None,
  };

  /// <summary>The zones whose ground owes a mend, and on which sides: every zone of <paramref name="candidates"/> (the generated
  /// zones) that is next to a zone in <paramref name="cleared"/> and is not cleared itself, nor in <paramref name="except"/>.</summary>
  internal static Dictionary<Vector2s, Edges> BordersOf(IEnumerable<Vector2s> cleared, ICollection<Vector2s> candidates, ICollection<Vector2s>? except = null)
  {
    var gone = new HashSet<Vector2s>(cleared, Comparer);
    var borders = new Dictionary<Vector2s, Edges>(Comparer);
    foreach (var zone in gone)
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
          if (dx == 0 && dy == 0)
            continue;
          // The zone that stays, and the side of it that meets the cleared one.
          var stays = new Vector2s(zone.x - dx, zone.y - dy);
          if (gone.Contains(stays) || !candidates.Contains(stays) || (except != null && except.Contains(stays)))
            continue;
          borders[stays] = (borders.TryGetValue(stays, out var edges) ? edges : Edges.None) | Facing(dx, dy);
        }
    return borders;
  }

  // ------------------------------------------------------------------------------------------------ the work

  /// <summary>What a frame may spend: this many objects, or this many milliseconds, whichever is reached first. Every object
  /// destroyed in a frame goes to every client as one message of 12 bytes each (ZDOMan.SendDestroyed), and one message tops
  /// out at 512 KiB, so the count is a limit on the message as well as on the frame.</summary>
  internal sealed class Budget
  {
    private readonly Func<double> clock;
    private readonly Func<int> objectsPerFrame;
    private readonly double maxMilliseconds;
    private double frameStart;
    private int maxObjects;
    private int used;

    /// <param name="objectsPerFrame">Asked at the start of each frame, so that it can follow who is connected.</param>
    public Budget(Func<int> objectsPerFrame, double maxMilliseconds, Func<double>? clock = null)
    {
      this.objectsPerFrame = objectsPerFrame;
      this.maxMilliseconds = maxMilliseconds;
      this.clock = clock ?? SystemClock;
      maxObjects = objectsPerFrame();
    }

    /// <summary>The system's clock, in milliseconds.</summary>
    internal static double SystemClock() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public Budget(int maxObjects, double maxMilliseconds, Func<double>? clock = null) : this(() => maxObjects, maxMilliseconds, clock)
    {
    }

    public void NewFrame()
    {
      frameStart = clock();
      maxObjects = objectsPerFrame();
      used = 0;
    }

    public void Use(int objects) => used += objects;
    public int Room => Math.Max(0, maxObjects - used);
    public bool Spent => used >= maxObjects || clock() - frameStart >= maxMilliseconds;
  }

  internal sealed class Progress
  {
    /// <summary>Zones to reset (a stray the game generates by its turn joins them: Work.Promote).</summary>
    public int Total;
    /// <summary>Zones dealt with: reset, or left alone after all.</summary>
    public int Done;
    public int Reset;
    /// <summary>Zones that were to be reset but were left alone by the time their turn came: something that protects was in them
    /// or near them, or they share a location with a zone that was.</summary>
    public int Skipped;
    /// <summary>Of those, the ones left alone only because a zone they share a location with was.</summary>
    public int SkippedForLocations;
    public long Destroyed;
    /// <summary>Of those, creatures that a spawner made and that had walked out of its zone.</summary>
    public long Spawned;
    /// <summary>Creatures a spawner made that stay: ones to keep, or in a zone that is left alone.</summary>
    public long SpawnedKept;
    /// <summary>Objects of reset zones that stay: players, what players made, tombstones, tamed creatures.</summary>
    public long Kept;
    public int BorderZones;
    public int BorderVertices;
    /// <summary>The world went away before the work was done.</summary>
    public bool Aborted;
    public int Percent => Total == 0 ? 100 : (int)(Done * 100L / Total);
  }

  /// <summary>What editing one left-alone zone's ground counts for in a frame's budget, in objects.</summary>
  internal const int BorderCost = 25;

  /// <summary>Resets the planned zones, a frame's worth at a time: it yields when the frame's budget is spent, between zones
  /// and inside a big one (the clock is looked at every <see cref="Work.SliceSize"/> objects). A zone's objects go first and its
  /// finish (location, generated mark, loaded copy) follows when its neighbours' objects are gone too. The ground at the edges
  /// of the zones that stay comes after all the resets.</summary>
  internal static IEnumerator Execute(IZoneWorld world, Plan plan, Budget budget, Progress progress, Memory? memory = null) =>
    new Work(world, plan, budget, progress, memory ?? new Memory()).Steps();

  /// <summary>One run of the work. It keeps its place in fields rather than in the iterator, so that a save or a second request
  /// can finish the zone being emptied from outside (<see cref="FinishInFlight"/>) while the iterator waits for its next frame.</summary>
  internal sealed class Work(IZoneWorld world, Plan plan, Budget budget, Progress progress, Memory memory)
  {
    /// <summary>Objects destroyed between two looks at the budget.</summary>
    internal const int SliceSize = 32;

    // The zones of the plan (a stray that the game has generated by its turn joins them: Promote); the strays (zones nobody generated
    // that hold what a location left there); the ones whose turn is over (emptied, left alone, or nothing to do); the 3 x 3 around
    // those left alone because something protects in them; the ones finished.
    private readonly HashSet<Vector2s> planned = new(plan.Reset, Comparer);
    private readonly HashSet<Vector2s> strays = new(plan.Strays, Comparer);
    private readonly HashSet<Vector2s> done = new(Comparer);
    private readonly HashSet<Vector2s> late = new(Comparer);
    private readonly HashSet<Vector2s> finished = new(Comparer);
    // What a zone waits for, kept as counts so that asking is not a walk: per location group, its zones that have not had their
    // turn (the strays among them); per zone emptied and waiting, the zones of the plan in its 3 x 3 that have not had theirs.
    private readonly int[] groupOpen = OpenPerGroup(plan);
    private readonly Dictionary<Vector2s, int> besideOpen = new(Comparer);
    // Where the players are, as it was at the start of this frame.
    private HashSet<Vector2s> live = new(Comparer);

    /// <summary>How many times a zone was looked at for its finish (the offline tests check that this does not grow with the square
    /// of a location group).</summary>
    internal long Looks;

    // The zone being emptied, and what is left of it.
    private bool inFlight;
    private Vector2s flightZone;
    private List<Obj> doomed = [];
    private int at;

    /// <summary>Whether a zone is half emptied: its objects are going over several frames.</summary>
    public bool InFlight => inFlight;

    private void StartFrame()
    {
      budget.NewFrame();
      world.NewFrame();
      live = Dilate(world.LiveProtectors());
    }

    public IEnumerator Steps()
    {
      progress.Total = plan.Reset.Count;
      // What an earlier run emptied and did not finish is finished by this one, if it is still generated.
      memory.Pending.IntersectWith(plan.Carried);
      StartFrame();
      foreach (var zone in plan.Turns)
      {
        // Left alone with a zone of its location, before its turn.
        if (done.Contains(zone))
          continue;
        if (budget.Spent)
        {
          yield return null;
          StartFrame();
        }
        if (!world.Alive)
        {
          progress.Aborted = true;
          yield break;
        }
        // A save may have taken this zone's turn while the work waited for the next frame (Work.BeforeSave takes the turns of the
        // groups of the zones that wait): a zone has its turn once.
        if (done.Contains(zone))
          continue;
        if (!Begin(zone))
          continue;
        while (inFlight)
        {
          Slice(Math.Min(SliceSize, Math.Max(1, budget.Room)));
          // A zone that has begun is finished, whoever comes near: half of it is gone.
          if (inFlight && budget.Spent)
          {
            yield return null;
            StartFrame();
            if (!world.Alive)
            {
              progress.Aborted = true;
              yield break;
            }
          }
        }
      }
      // Every turn is over, so nothing a zone waits for is left; this is for anything the rule above missed.
      foreach (var zone in memory.Pending.OrderBy(z => z.x).ThenBy(z => z.y).ToList())
        if (done.Contains(zone))
          Finish(zone);
      foreach (var border in Owed())
      {
        if (budget.Spent)
        {
          yield return null;
          StartFrame();
        }
        if (!world.Alive)
        {
          progress.Aborted = true;
          yield break;
        }
        Mend(border);
        // Unpacking, editing and packing a zone's ground costs about as much as a few dozen objects.
        budget.Use(BorderCost);
      }
      memory.Cleared.Clear();
    }

    // A zone's turn: left alone, or listed and started. False when it is not to be emptied.
    private bool Begin(Vector2s zone)
    {
      // A stray that somebody has come near since the plan is generated now: it holds a fill of its own besides the old parts of the
      // location, and left as it is it would hold two copies of them once the home generates again. So it is a zone of the plan from
      // here on, looked at and reset like the rest.
      if (strays.Contains(zone) && world.IsGenerated(zone))
        Promote(zone);
      // A zone an earlier run emptied has nothing left to protect, and kept it would stay bare.
      bool carried = plan.Carried.Contains(zone);
      // Somebody who protects is near by now (the plan was made minutes ago), or beside a zone left alone at its turn.
      if (!carried && (live.Contains(zone) || late.Contains(zone)))
      {
        LeaveAlone(zone, holdsProtector: false);
        return false;
      }
      var objects = world.ObjectsIn(zone);
      budget.Use(1);
      // Something that protects stands here since the plan was made (a piece placed meanwhile, a player come in): leave the zone.
      if (!carried && objects.Any(o => Rules.Protects(o.Kind)))
      {
        LeaveAlone(zone, holdsProtector: true);
        return false;
      }
      doomed = new List<Obj>(objects.Count);
      foreach (var o in objects)
      {
        if (Rules.Survives(o.Kind))
          progress.Kept++;
        else
          doomed.Add(o);
      }
      inFlight = true;
      flightZone = zone;
      at = 0;
      if (doomed.Count == 0)
        Complete();
      return true;
    }

    // A stray the game has generated since the plan becomes a zone to reset: it is counted (Total), and the zones waiting beside it, which
    // were not waiting for a stray, wait for it now (TurnOver counts it down again when its turn is over).
    private void Promote(Vector2s zone)
    {
      strays.Remove(zone);
      planned.Add(zone);
      progress.Total++;
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
          var beside = new Vector2s(zone.x + dx, zone.y + dy);
          if (besideOpen.TryGetValue(beside, out var open))
            besideOpen[beside] = open + 1;
        }
    }

    // Destroys up to <limit> objects of the zone being emptied; the zone is cleared with its last.
    private void Slice(int limit)
    {
      int n = Math.Min(limit, doomed.Count - at);
      for (int i = 0; i < n; i++)
        if (world.Destroy(doomed[at + i], out var spawned))
        {
          progress.Destroyed++;
          FollowSpawn(spawned);
        }
      at += Math.Max(0, n);
      budget.Use(Math.Max(0, n));
      if (at >= doomed.Count)
        Complete();
    }

    // The creature a destroyed spawner made, wherever it is now.
    private void FollowSpawn(ZDOID spawned)
    {
      if (spawned.IsNone() || world.Find(spawned) is not { } creature)
        return;
      if (Rules.Survives(creature.Kind) || Rules.Protects(creature.Kind) || plan.Covered.Contains(creature.Zone) || live.Contains(creature.Zone) || late.Contains(creature.Zone))
      {
        progress.SpawnedKept++;
        return;
      }
      if (world.Destroy(creature, out _))
      {
        progress.Destroyed++;
        progress.Spawned++;
      }
    }

    // The zone's objects are all gone. It is finished when the zones beside it are (a stray has nothing to finish: its turn is over).
    private void Complete()
    {
      inFlight = false;
      doomed = [];
      TurnOver(flightZone);
      if (!strays.Contains(flightZone))
      {
        memory.Cleared.Add(flightZone);
        memory.Pending.Add(flightZone);
        // Counted once its own turn is over, so that it is not among the zones it waits for.
        besideOpen[flightZone] = CountBeside(flightZone);
      }
      Settle(new List<Vector2s> { flightZone });
    }

    // The zone is left alone. When something that protects stands in it, the zones around it are kept with it; and since a
    // location is kept whole or reset whole, so are the zones of its group that have not had their turn (the strays too, which
    // are no zone of the run: they add to none of its counts). The ones already emptied cannot be filled again.
    private void LeaveAlone(Vector2s zone, bool holdsProtector)
    {
      TurnOver(zone);
      if (!strays.Contains(zone))
      {
        progress.Skipped++;
        progress.Done++;
      }
      if (holdsProtector)
        AddSquare(late, zone, Rules.ProtectRadius);
      var settled = new List<Vector2s> { zone };
      foreach (var member in plan.GroupMembers(zone))
      {
        bool stray = strays.Contains(member);
        if ((stray || (planned.Contains(member) && !plan.Carried.Contains(member))) && TurnOver(member))
        {
          if (!stray)
          {
            progress.Skipped++;
            progress.SkippedForLocations++;
            progress.Done++;
          }
          settled.Add(member);
        }
      }
      Settle(settled);
    }

    // A zone's turn is over (emptied, left alone, or nothing to do for it): every count that waits for it goes down. False when it
    // was over already.
    private bool TurnOver(Vector2s zone)
    {
      if (!done.Add(zone))
        return false;
      if (plan.GroupOf.TryGetValue(zone, out var group))
        groupOpen[group]--;
      // A stray is nobody's neighbour: nothing is put down in it by a zone that generates again beside it.
      if (planned.Contains(zone))
        for (int dx = -1; dx <= 1; dx++)
          for (int dy = -1; dy <= 1; dy++)
          {
            var beside = new Vector2s(zone.x + dx, zone.y + dy);
            if (besideOpen.TryGetValue(beside, out var open))
              besideOpen[beside] = open - 1;
          }
      return true;
    }

    // The zones of the plan in the zone's 3 x 3 whose turn is not over (the zone itself too, if it is one of them).
    private int CountBeside(Vector2s zone)
    {
      int open = 0;
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
          var beside = new Vector2s(zone.x + dx, zone.y + dy);
          if (planned.Contains(beside) && !done.Contains(beside))
            open++;
        }
      return open;
    }

    private static int[] OpenPerGroup(Plan plan)
    {
      var open = new int[plan.Groups.Count];
      foreach (var zone in plan.Turns)
        if (plan.GroupOf.TryGetValue(zone, out var group))
          open[group]++;
      return open;
    }

    // Zones' turns are over (all of them of one location group, or one zone): the zones that were waiting for them may be finished now.
    // Those beside each are looked at; the zones of the group only once the last zone of it has had its turn, when every one that waits can
    // go, and once however many zones the group lost (a group on a map with many pins can be thousands of zones, and walking it for each
    // zone would be the square of that). The first zone's neighbours come before the group, the others' after it, as they always have.
    private void Settle(IReadOnlyList<Vector2s> turned)
    {
      int walked = -1;
      foreach (var zone in turned)
      {
        for (int dx = -1; dx <= 1; dx++)
          for (int dy = -1; dy <= 1; dy++)
            FinishIfReady(new Vector2s(zone.x + dx, zone.y + dy));
        if (plan.GroupOf.TryGetValue(zone, out var group) && group != walked && groupOpen[group] == 0)
        {
          walked = group;
          foreach (var member in plan.Groups[group])
            FinishIfReady(member);
        }
      }
    }

    private void FinishIfReady(Vector2s zone)
    {
      Looks++;
      if (memory.Pending.Contains(zone) && done.Contains(zone) && Ready(zone))
        Finish(zone);
    }

    // A zone is finished when its own objects are gone and so are those of every zone beside it that is reset too (its 3 x 3, and
    // the zones of its location group, strays included), or those zones were left alone: nothing is put down over objects that are
    // about to go.
    private bool Ready(Vector2s zone) =>
      (besideOpen.TryGetValue(zone, out var open) ? open : CountBeside(zone)) == 0
      && (!plan.GroupOf.TryGetValue(zone, out var group) || groupOpen[group] == 0);

    private void Finish(Vector2s zone)
    {
      world.Unplace(zone);
      world.Ungenerate(zone);
      world.DestroyRoot(zone);
      memory.Pending.Remove(zone);
      besideOpen.Remove(zone);
      finished.Add(zone);
      progress.Reset++;
      progress.Done++;
    }

    // The zones whose ground owes a mend, in a fixed order: beside a zone cleared since the last pass, and neither cleared, nor
    // finished (a finished zone has no compiler left).
    private List<KeyValuePair<Vector2s, Edges>> Owed()
    {
      var gone = new HashSet<Vector2s>(memory.Pending, Comparer);
      gone.UnionWith(finished);
      return BordersOf(memory.Cleared, plan.GeneratedZones, gone).OrderBy(b => b.Key.x).ThenBy(b => b.Key.y).ToList();
    }

    private void Mend(KeyValuePair<Vector2s, Edges> border)
    {
      int vertices = world.AdjustBorder(border.Key, border.Value);
      if (vertices > 0)
      {
        progress.BorderZones++;
        progress.BorderVertices += vertices;
      }
    }

    /// <summary>The rest of the zone being emptied goes now, with no budget: for a save, and for a second request, since a zone
    /// must not be left half empty and generated.</summary>
    public void FinishInFlight()
    {
      while (inFlight)
        Slice(int.MaxValue);
    }

    /// <summary>What a save needs first: no zone half emptied, no location half cleared, every destroyed object out of the world's
    /// lists, and the ground mended where zones were cleared. The borders are done once they are mended; the next ones are new.</summary>
    public void BeforeSave()
    {
      if (!world.Alive)
        return;
      FinishInFlight();
      TakeTurnsOfGroups();
      world.Flush();
      foreach (var border in Owed())
        Mend(border);
      memory.Cleared.Clear();
    }

    // A zone emptied and waiting is saved as not generated, with its location not placed: when the world loads it puts the whole
    // location down again. The parts of that location that stand in the zones of its group must be gone by then, so the zones of
    // the group that have not had their turn (the strays too) take it now, without a budget, with the same look for what protects
    // that every turn has: one left alone then is the case that cannot be undone. So a file holds a group wholly emptied or untouched.
    private void TakeTurnsOfGroups()
    {
      live = Dilate(world.LiveProtectors());
      foreach (var waiting in memory.Pending.OrderBy(z => z.x).ThenBy(z => z.y).ToList())
      {
        if (!plan.GroupOf.TryGetValue(waiting, out var group) || groupOpen[group] == 0)
          continue;
        foreach (var member in plan.Groups[group])
          if (!done.Contains(member) && (planned.Contains(member) || strays.Contains(member)) && Begin(member))
          {
            FinishInFlight();
            // Each zone's objects are sent on their own: a message tops out at 512 KiB (ZDOMan.SendDestroyed, 12 bytes an object), and
            // a group chained over a big map can hold more objects than that.
            world.Flush();
          }
      }
    }

    /// <summary>For a run that an error has stopped: the zones emptied and still waiting for their neighbours are finished now. Nothing
    /// will reach those neighbours, and a zone left bare and generated never fills again. How many were finished; a zone that could not
    /// be finished is handed to <paramref name="failed"/> and left as it is.</summary>
    public int FinishPending(Action<Vector2s, Exception>? failed = null)
    {
      if (!world.Alive)
        return 0;
      int count = 0;
      foreach (var zone in memory.Pending.OrderBy(z => z.x).ThenBy(z => z.y).ToList())
      {
        try
        {
          Finish(zone);
          count++;
        }
        catch (Exception e)
        {
          failed?.Invoke(zone, e);
        }
      }
      return count;
    }
  }

  // ------------------------------------------------------------------------------------------------ the ground at a border

  // Terrain a player edited (the hoe, the pickaxe, a levelled building site) is kept in the zone's terrain compiler, a ZDO of
  // its own ("TCData", TerrainComp.Save): per vertex of the zone's 65 x 65 grid whether it was edited and by how much. The
  // grid includes the vertices on the zone's edges, which the neighbour has too, and each zone builds its ground from its own
  // copy. Resetting a zone destroys its compiler with the rest, so a neighbour that stays still holds its edits along the
  // shared edge, and the two grounds no longer meet there: a crack, or a step of up to 8 m. Clearing the edits of the
  // vertices on the edges that meet a reset zone, in the neighbour that stays, makes the ground meet again. The neighbour's
  // other edits stay; the loaded compiler notices the new data revision and rebuilds its ground (TerrainComp.CheckLoad).
  internal static class TerrainBorder
  {
    /// <summary>Whether the compiler's data holds an edited height or a painted vertex: a player worked this ground. False for
    /// data without either, and for data that is not what TerrainComp.Save writes.</summary>
    internal static bool HasEdits(byte[]? data)
    {
      if (data == null || data.Length == 0)
        return false;
      try
      {
        // The same order as TerrainComp.Save writes and TerrainComp.Load reads.
        var read = new ZPackage(Utils.Decompress(data));
        read.ReadInt();
        read.ReadInt();
        read.ReadVector3();
        read.ReadSingle();
        int vertices = read.ReadInt();
        int pitch = (int)Math.Round(Math.Sqrt(vertices));
        if (pitch < 2 || pitch * pitch != vertices)
          return false;
        // An edited vertex is followed by its deltas, and the first one decides, so the rest need not be read.
        for (int i = 0; i < vertices; i++)
          if (read.ReadBool())
            return true;
        int paint = read.ReadInt();
        for (int i = 0; i < paint; i++)
          if (read.ReadBool())
            return true;
        return false;
      }
      catch (Exception)
      {
        return false;
      }
    }

    /// <summary>The compiler's data with the edits on <paramref name="edges"/> cleared. Null when there is nothing to clear,
    /// or the data is not what TerrainComp.Save writes.</summary>
    internal static byte[]? Clear(byte[] data, Edges edges, out int cleared)
    {
      cleared = 0;
      if (data == null || data.Length == 0 || edges == Edges.None)
        return null;
      try
      {
        var raw = Utils.Decompress(data);
        var read = new ZPackage(raw);
        var write = new ZPackage();
        // The same order as TerrainComp.Save writes and TerrainComp.Load reads.
        write.Write(read.ReadInt());
        write.Write(read.ReadInt());
        write.Write(read.ReadVector3());
        write.Write(read.ReadSingle());
        int vertices = read.ReadInt();
        int pitch = (int)Math.Round(Math.Sqrt(vertices));
        if (pitch < 2 || pitch * pitch != vertices)
          return null;
        write.Write(vertices);
        int count = 0;
        for (int i = 0; i < vertices; i++)
        {
          bool edited = read.ReadBool();
          float level = 0f, smooth = 0f;
          if (edited)
          {
            level = read.ReadSingle();
            smooth = read.ReadSingle();
            if (OnEdge(i / pitch, i % pitch, pitch, edges))
            {
              edited = false;
              count++;
            }
          }
          write.Write(edited);
          if (edited)
          {
            write.Write(level);
            write.Write(smooth);
          }
        }
        if (count == 0)
          return null;
        // The paint, unchanged.
        int paint = read.ReadInt();
        write.Write(paint);
        for (int i = 0; i < paint; i++)
        {
          bool painted = read.ReadBool();
          write.Write(painted);
          if (painted)
            for (int c = 0; c < 4; c++)
              write.Write(read.ReadSingle());
        }
        var body = write.GetArray();
        var rest = raw.Length - read.GetPos();
        if (rest > 0)
        {
          // Whatever a later game version appends stays.
          var all = new byte[body.Length + rest];
          Buffer.BlockCopy(body, 0, all, 0, body.Length);
          Buffer.BlockCopy(raw, read.GetPos(), all, body.Length, rest);
          body = all;
        }
        cleared = count;
        return Utils.Compress(body);
      }
      catch (Exception)
      {
        // Not data this code knows how to read: leave it as it is.
        cleared = 0;
        return null;
      }
    }

    // Row y is the zone's z (north), column x is its x (east): TerrainComp indexes y * pitch + x.
    internal static bool OnEdge(int row, int column, int pitch, Edges edges)
    {
      int last = pitch - 1;
      return ((edges & Edges.North) != 0 && row == last)
             || ((edges & Edges.South) != 0 && row == 0)
             || ((edges & Edges.East) != 0 && column == last)
             || ((edges & Edges.West) != 0 && column == 0)
             || ((edges & Edges.NorthEast) != 0 && row == last && column == last)
             || ((edges & Edges.NorthWest) != 0 && row == last && column == 0)
             || ((edges & Edges.SouthEast) != 0 && row == 0 && column == last)
             || ((edges & Edges.SouthWest) != 0 && row == 0 && column == 0);
    }
  }

  // ------------------------------------------------------------------------------------------------ what it says

  private static string Count(long n, string one, string many) => (n == 1 ? "1 " + one : n.ToString("N0", CultureInfo.InvariantCulture) + " " + many);

  internal static string StartLine(Plan plan) =>
    plan.Reset.Count == 0
      ? $"Zone regeneration: no generated zone to reset ({Count(plan.Kept.Count, "generated zone is", "generated zones are")} left alone)."
      : $"Zone regeneration: resetting {plan.Reset.Count.ToString("N0", CultureInfo.InvariantCulture)} of {Count(plan.Generated, "generated zone", "generated zones")}; "
        + $"{plan.Kept.Count.ToString("N0", CultureInfo.InvariantCulture)} left alone (near something a player built or worked on, a tombstone, or a player"
        + (plan.KeptForLocations > 0 ? $"; {plan.KeptForLocations.ToString("N0", CultureInfo.InvariantCulture)} of them to keep a location whole" : "")
        + ").";

  internal static string ProgressLine(Progress progress) =>
    $"Zone regeneration: {progress.Done.ToString("N0", CultureInfo.InvariantCulture)} of {Count(progress.Total, "zone", "zones")} ({progress.Percent}%), {Count(progress.Destroyed, "object", "objects")} removed.";

  internal static string FinishLine(Progress progress, double seconds, int errors)
  {
    var text = progress.Aborted
      ? $"Zone regeneration stopped after {progress.Done.ToString("N0", CultureInfo.InvariantCulture)} of {Count(progress.Total, "zone", "zones")}: the world closed."
      : $"Zone regeneration finished in {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s: {Count(progress.Reset, "zone", "zones")} reset, {Count(progress.Destroyed, "object", "objects")} removed.";
    if (progress.Kept > 0)
      text += $" {Count(progress.Kept, "object stays", "objects stay")} (players, what players made, tombstones, tamed animals).";
    int alone = progress.Skipped - progress.SkippedForLocations;
    if (alone > 0)
      text += $" {Count(alone, "zone was", "zones were")} left alone after all: a player, or something a player made, was in or beside {(alone == 1 ? "it" : "them")} before {(alone == 1 ? "its" : "their")} turn.";
    if (progress.SkippedForLocations > 0)
      text += $" {Count(progress.SkippedForLocations, alone > 0 ? "more zone was" : "zone was", alone > 0 ? "more zones were" : "zones were")} left alone to keep a location whole.";
    if (progress.BorderZones > 0)
      text += $" The ground at the edge of {Count(progress.BorderZones, "left-alone zone", "left-alone zones")} was adjusted to meet the reset zones.";
    if (errors > 0)
      text += $" {Count(errors, "error", "errors")}, see the log.";
    if (!progress.Aborted && progress.Reset > 0)
      text += " A reset zone generates again with the new settings when somebody comes near.";
    return text;
  }

  /// <summary>What the log says after an error stopped the run and the zones left waiting (its own, or those of an earlier run it carried)
  /// were finished (see Work.FinishPending).</summary>
  internal static string PendingLine(int finished) =>
    $"Zone regeneration: {Count(finished, "zone", "zones")} left waiting {(finished == 1 ? "was" : "were")} finished, so {(finished == 1 ? "it generates" : "they generate")} again with the new settings when somebody comes near. "
    + "The zones it had not reached are as they were: 'bc regen' resets them.";

  /// <summary>What the log says of the zones that could not be finished beyond the few it told one by one (see ZoneRegen.FinishPending).</summary>
  internal static string UnfinishedLine(int more) =>
    $"Zone regeneration: {Count(more, "more zone", "more zones")} could not be finished either. 'bc regen' tries {(more == 1 ? "it" : "them")} again.";
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// The in-game operations of the baked layer: bake an area (pieces become records and go), unbake (records become pieces), undo,
// drop, and the check at world load that settles an operation a crash or a cut save left half done (10.7 to 10.10).
//
// Everything the runner does to the game's objects, to the layer and to the clients goes through small interfaces, so that a whole
// operation, with a crash at every mutation, runs offline against stand-ins (Tests.BakeRunner.cs); the game's own versions are
// ZdoBakeWorld (BakeCapture.cs), LayerPort (BakeRunner.Layer.cs) and the transport of slice B.
//
// The order that makes a save at any step safe (10.7): the journal is on disk first, and its state is written before each step's
// changes (so it is never behind the world); then the layer; then the clients have it; then the statics go, 1,000 a frame. A piece
// is in the layer or in the world, and in both for a moment, never in neither. An operation ends in a state that the next world
// load settles (Removed, Unbaking, Undoing, Applied), not in Settled: a save cut between the objects and the layer file after the
// last step would otherwise lose pieces, and only a load can tell that both reached the disk.

// One object as the world lists it: what the classifier and the gather need.
internal readonly struct WorldObject
{
  // Valid in this session only.
  public ZDOID Id { get; init; }
  public int Prefab { get; init; }
  public float X { get; init; }
  public float Y { get; init; }
  public float Z { get; init; }
  public long Creator { get; init; }
  // The ZDO has bc_bake_id.
  public bool AlreadyBaked { get; init; }
  public bool HasConnection { get; init; }
}

internal enum RemoveOutcome
{
  Removed,
  // Gone, moved, or another prefab: not removed, and its record stays.
  Changed,
}

// What gives a piece a look of its own (spec 2.5): the slots of its MaterialVariation components with their weights, and whether it has
// RandomMaterialValues.
internal sealed class LookInfo
{
  public static readonly LookInfo None = new();

  public KeyValuePair<int, float[]>[] Variations { get; init; } = [];
  public bool RandomValues { get; init; }
}

// The game's objects, as the runner needs them. Main thread only.
internal interface IBakeWorld
{
  // The game's world is loaded (ZDOMan, ZoneSystem and ZNetScene exist).
  bool Ready { get; }
  string WorldName { get; }
  long WorldUid { get; }
  // What the classifier reads of a prefab; null when this game has no such prefab.
  PrefabFacts? FactsOf(int prefab);
  // The game's prefab of that name: its hash, or 0 when it has none.
  int PrefabOf(string name);
  // The ZDO flags the prefab's own ZNetView gives its objects.
  (bool Persistent, bool Distant, byte Type) FlagsOf(int prefab);
  LookInfo LookOf(int prefab);
  // Every object whose own zone is one of these (10.3: the zone's sector list and its portals, each object kept only if its own
  // zone is that one, since the zones past the sector map share a bucket).
  void Gather(IReadOnlyList<ZoneKey> zones, List<WorldObject> into);
  PieceCopy Copy(in WorldObject piece);
  // The seeded Live pieces of a zone by (source, id), for the records an unbake releases.
  Dictionary<ulong, ZDOID> LiveIn(ZoneKey zone);
  // Takes an object out of the world if it still is what the copy says (prefab, position): this machine owns it at once, with no
  // revision bump, so that no peer is sent the object just before it goes (10.7 step 7).
  RemoveOutcome Remove(ZDOID id, PieceCopy expected);
  // Makes an object, whole, as undo and unbake do.
  ZDOID Create(PieceCopy copy);
  // For each piece, the object that is it now (prefab, position within 1 cm, rotation within 0.1 degrees; each object once), or null.
  ZDOID?[] FindStanding(IReadOnlyList<PieceCopy> pieces);
  // Every one of these objects has been sent to every client whose area holds it (ZDOMan keeps what it sent each peer).
  bool AllSent(IReadOnlyList<ZDOID> ids);
  // Town mode: this machine owns the piece, and it gets the four bake keys.
  void Adopt(ZDOID id, int source, uint bakeId, uint revision);
  // The bake keys off a piece; and the creator, when it has none.
  void Release(ZDOID id, long creatorIfNone);
  // Unity's Quaternion.eulerAngles, for the rotation a record becomes.
  (float X, float Y, float Z) EulerOf(Quaternion rotation);
}

// What changed in the layer: the layer now current and the zones whose look changed.
internal sealed class LayerChange
{
  public BakedLayer? Layer { get; init; }
  public ZoneKey[]? Changed { get; init; }
  public uint Revision { get; init; }
  public long Bytes { get; init; }
}

// What the dry run says of the layer after the change: its size and revision, how much it grows, and a patch's size (-1: unknown).
internal sealed class BakeEstimate
{
  public uint Revision { get; init; }
  public long Bytes { get; init; }
  public long Growth { get; init; }
  public long PatchBytes { get; init; } = -1;
}

// The layer, as the runner needs it (slice A's BakedLayerStore and LayerEdit behind it).
internal interface IBakeLayerPort
{
  // The layer this machine holds now (the reconciliation of Live pieces compares it with the one an edit makes).
  BakedLayer? Current { get; }
  bool HasLayer { get; }
  uint Revision { get; }
  long Bytes { get; }
  // The number the registry gives the next operation (1 without a layer).
  int NextOperation { get; }
  IReadOnlyList<OperationInfo> Operations { get; }
  bool TryGetOperation(int number, out OperationInfo operation);
  // How many records the compiler's file (0) or an in-game bake owns, and how many there are in all.
  long RecordsOfSource(int source);
  long Records { get; }
  // The records whose point is in these zones and that the test accepts, with their palette entries and value sets.
  List<BakeRecord> RecordsIn(IEnumerable<ZoneKey> zones, Func<ZoneRecord, bool> test);
  // The zones that hold records.
  List<ZoneKey> ZonesWithRecords();
  // The layer after a bake, without making it current. Throws BakedFormatException when the layer cannot hold it.
  BakeEstimate EstimateAdd(BakeJournalData data);
  // The edits. Each makes its result the current layer and says what changed; each is one revision.
  // A bake: its records in, its operation entered.
  LayerChange Add(BakeJournalData data);
  // An unbake or a drop: its records out, its operation entered.
  LayerChange Remove(BakeJournalData data);
  // An undo of a bake: its records out, its operation marked undone.
  LayerChange UndoBake(int number);
  // An undo of an unbake or a drop: its records back, its operation marked undone.
  LayerChange PutBack(BakeJournalData data);
  // A compiler's new file (6.2): its records without a source and its sections replace the layer's; every in-game record stays; the operation
  // is entered. `leftOut`: the records of the file that an in-game bake owns, which are not taken.
  LayerChange Load(BakedLayer file, OperationInfo operation, out int leftOut);
  // The layer a load would make, without making it current (the dry run of a load: the reconciliation plans against it). Throws
  // BakedFormatException when the layer cannot hold the merge.
  BakedLayer PreviewLoad(BakedLayer file, OperationInfo operation);
}

// What the clients are told, and who they are.
internal sealed class BakePush
{
  // "layer revision 13 sent to 3 players; 3 of 3 ready in 2.4 s", with what did not go as hoped.
  public string Summary { get; init; } = "";
  public double Seconds { get; init; }
}

internal interface IBakeTransport
{
  // Why this build cannot send a changed layer to the players (and so cannot change the world), or null. A dry run works without it.
  string? Unavailable { get; }
  // Remote clients that take part in the world.
  int Players { get; }
  // Sends the new layer and waits until every client has it, has left, or has been silent 30 s after its last packet (5.3).
  IEnumerator Push(LayerChange change, Action<BakePush> done);
  // Who is connected and which Better Continents version they run (null: none), for a conversion (4.3).
  IReadOnlyList<(string Name, string? Version)> Peers { get; }
}

// What brings the world's Live pieces to the layer after a change (slice B).
internal interface IBakeReconcile
{
  IEnumerator Run(BakedLayer? old, LayerChange change, Action<string> say);
  // What a run would do for `next`, said and not done (the dry run of a load).
  IEnumerator Preview(BakedLayer? old, BakedLayer next, Action<string> say);
}

// What the world's settings say about its being a Better Continents world (4.1, 4.3).
internal enum BakeWorldKind
{
  // A world BC made, or a GameTerrain one: it has a layer or may have one.
  BetterContinents,
  // The game's own world: BC does nothing to it until it is converted.
  Vanilla,
  // A settings file exists and cannot be read (a newer Better Continents, or damage): never converted.
  Unreadable,
}

// The world's conversion to a GameTerrain world.
internal interface IBakeConvert
{
  // Why this build cannot convert a world, or null.
  string? Unavailable { get; }
  BakeWorldKind Kind { get; }
  // Makes the world a Better Continents world that keeps the game's terrain, and tells every client (4.3).
  void Convert();
}

// Who runs a command: for the registry and the journal, and the creator of the pieces an unbake makes of a compiler's records.
internal sealed class BakeWho
{
  public string Name { get; init; } = "server console";
  public string PlatformId { get; init; } = "";
  // The player id the pieces an admin unbakes get as creator, so that zone resets keep them (design 3.6).
  public long Creator { get; init; } = BakeRunner.ConsoleCreator;
  // A message at the top left of the admin's screen when an operation ends, if the admin has a screen.
  public Action<string>? Notify { get; init; }
}

// Everything one run needs.
internal sealed class BakeContext
{
  public IBakeWorld World { get; init; } = null!;
  public IBakeLayerPort Layer { get; init; } = null!;
  public IBakeTransport Transport { get; init; } = null!;
  public IBakeReconcile? Reconcile { get; init; }
  public IBakeConvert? Convert { get; init; }
  public BakeJournal Journal { get; init; } = null!;
  public Action<string> Say { get; init; } = _ => { };
  // Seconds, always going forward.
  public Func<double> Clock { get; init; } = () => Time.realtimeSinceStartup;
  public BakeWho Who { get; init; } = new();
  public string Version { get; init; } = ModInfo.Version;
  public Func<long> UnixTime { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  // An operation ended as it should in this session, in the state that the next world load settles: after a complete world save that began
  // later, that state may be written as the final one it is given here (BakeSettle). Null in the tests and at world load.
  public Action<int, BakeState>? Ended { get; init; }
  // An operation takes up an undo file that an earlier one ended in a state to settle (an undo of it): that wait is over.
  public Action<int>? Began { get; init; }
}

// The area of a bake or an unbake: a circle of 4 to 256 m, or a world-aligned box up to 512 x 512 m; all heights; a piece is in it when
// its pivot is (10.2).
internal sealed class BakeArea
{
  public const float MinRadius = 4f, MaxRadius = 256f, MaxBox = 512f;

  public bool Circle { get; }
  public float CenterX { get; }
  public float CenterZ { get; }
  public float Radius { get; }
  public float MinX { get; }
  public float MinZ { get; }
  public float MaxX { get; }
  public float MaxZ { get; }

  private BakeArea(bool circle, float cx, float cz, float radius, float minX, float minZ, float maxX, float maxZ)
  {
    Circle = circle;
    CenterX = cx;
    CenterZ = cz;
    Radius = radius;
    MinX = minX;
    MinZ = minZ;
    MaxX = maxX;
    MaxZ = maxZ;
  }

  public static BakeArea OfCircle(float x, float z, float radius) => new(true, x, z, radius, x - radius, z - radius, x + radius, z + radius);

  public static BakeArea OfBox(float x1, float z1, float x2, float z2) =>
    new(false, (x1 + x2) / 2f, (z1 + z2) / 2f, 0f, Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2));

  // Why the area cannot be used, or null.
  public string? Problem()
  {
    if (float.IsNaN(CenterX) || float.IsNaN(CenterZ) || float.IsInfinity(CenterX) || float.IsInfinity(CenterZ))
      return "the place is not a number";
    if (Circle)
      return Radius < MinRadius || Radius > MaxRadius ? $"a radius is {MinRadius:0} to {MaxRadius:0} m" : null;
    return MaxX - MinX > MaxBox || MaxZ - MinZ > MaxBox ? $"a box is up to {MaxBox:0} x {MaxBox:0} m"
      : MaxX - MinX < 0.5f || MaxZ - MinZ < 0.5f ? "a box needs two different corners" : null;
  }

  public bool Contains(float x, float z)
  {
    if (!Circle)
      return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    float dx = x - CenterX, dz = z - CenterZ;
    return dx * dx + dz * dz <= Radius * Radius;
  }

  // The zones the area touches, by z then x (the order the work goes in).
  public List<ZoneKey> Zones()
  {
    var zones = new List<ZoneKey>();
    // A hair of margin: the game works out an object's zone in single precision, and an object a hair inside the area is looked for in its zone.
    const double Margin = 0.01;
    int x0 = BakedFormat.ZoneOf(MinX - Margin), x1 = BakedFormat.ZoneOf(MaxX + Margin), z0 = BakedFormat.ZoneOf(MinZ - Margin), z1 = BakedFormat.ZoneOf(MaxZ + Margin);
    for (int zz = z0; zz <= z1; zz++)
      for (int zx = x0; zx <= x1; zx++)
      {
        var zone = new ZoneKey(zx, zz);
        if (!zone.InRange)
          continue;
        if (Circle)
        {
          // The nearest point of the zone's square to the centre.
          double nx = Math.Max(zone.OriginX, Math.Min(CenterX, zone.OriginX + 64.0)), nz = Math.Max(zone.OriginZ, Math.Min(CenterZ, zone.OriginZ + 64.0));
          double dx = nx - CenterX, dz = nz - CenterZ;
          if (dx * dx + dz * dz > (double)Radius * Radius)
            continue;
        }
        zones.Add(zone);
      }
    return zones;
  }

  // "40 m around 812, -1206 (9 zones)", "box 812, -1206 to 900, -1100 (4 zones)".
  public string Describe(int zones) => Circle
    ? $"{Radius.ToString("0.#", CultureInfo.InvariantCulture)} m around {Round(CenterX)}, {Round(CenterZ)} ({Plural(zones)})"
    : $"box {Round(MinX)}, {Round(MinZ)} to {Round(MaxX)}, {Round(MaxZ)} ({Plural(zones)})";

  // The words that make this area again: "area 40 at 812 -1206", "box 1 2 3 4".
  public string Words(bool at) => Circle
    ? "area " + Radius.ToString("0.#", CultureInfo.InvariantCulture) + (at ? $" at {Exact(CenterX)} {Exact(CenterZ)}" : "")
    : $"box {Exact(MinX)} {Exact(MinZ)} {Exact(MaxX)} {Exact(MaxZ)}";

  // A coordinate as the words that make the area again need it: not rounded to the metre.
  private static string Exact(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

  private static string Round(float v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
  private static string Plural(int zones) => zones == 1 ? "1 zone" : zones + " zones";
}

// A piece the gather took: its object, its whole ZDO, and the record it becomes.
internal sealed class Taken
{
  public ZDOID Id { get; init; }
  public PieceCopy Copy { get; init; } = null!;
  public BakeRecord Record { get; init; } = null!;
}

// What a bake's gather found.
internal sealed class BakeGather
{
  public BakeTally Tally { get; } = new();
  public BakeValueSetPool Pool { get; } = new();
  // Every record, in the order the pieces were met: statics, seats and town pieces.
  public List<BakeRecord> Records { get; } = [];
  // The pieces that go.
  public List<Taken> Statics { get; } = [];
  // The pieces that stay and are adopted, with the object of each.
  public List<AdoptedPiece> Adopted { get; } = [];
  public List<ZDOID> AdoptedIds { get; } = [];
  // Objects in the zones the area touches, all of them.
  public long Objects { get; set; }
  public bool Closed { get; set; }
}

internal static partial class BakeRunner
{
  // 4 zones a frame while reading, 1,000 objects a frame while removing (the rate bc_twins uses: one frame's removals go out as
  // one "DestroyZDO" message, 12 bytes an id, and a Steam message holds 512 KiB).
  internal const int ZonesPerFrame = 4;
  internal const int PiecesPerFrame = 1000;
  // Town pieces are tens; a few hundred a frame is plenty.
  internal const int AdoptionsPerFrame = 200;
  // How long undo and unbake wait for the restored objects to reach the clients.
  internal const double SentTimeout = 60.0;
  // How often the wait asks whether they have.
  internal const double SentPoll = 0.5;
  // How far above its y a record of an in-game bake reaches, for its zone's y bounds (culling): taller than any piece.
  internal const float RecordHeight = 12f;
  // The creator the pieces get when the admin is the server's console and so has no player id: any nonzero id keeps a zone reset from
  // taking them.
  internal const long ConsoleCreator = 0x4243424B;

  // The operation that is under way on this machine (one at a time per world), or null.
  internal static string? Running { get; private set; }
  // The operation that stopped on an error or because the world closed, and left itself half done: the next world load settles it, and until
  // then nothing else changes the world, or it would build on a state nobody has checked.
  internal static string? Stuck { get; private set; }
  // The running operation has written its undo file: from then on a stop leaves something half done.
  private static bool touched;

  // Starts an operation; false, and the line to say, when one is under way already. A dry run changes nothing and may always go.
  internal static bool Enter(string what, bool confirm, out string refusal)
  {
    refusal = "";
    if (!confirm)
      return true;
    if (Running != null)
    {
      refusal = $"bc_bake: {Running} is still running. One operation runs at a time: wait for its last line.";
      return false;
    }
    if (Stuck != null)
    {
      refusal = $"bc_bake: {Stuck} stopped half done. Load the world again to settle it (the undo file says what was left) before changing it with anything else.";
      return false;
    }
    Running = what;
    touched = false;
    return true;
  }

  internal static void Leave(bool entered)
  {
    if (entered)
      Running = null;
  }

  // Forgets an operation that was under way and one that was left half done (a world that is loaded again, the tests).
  internal static void Reset()
  {
    Running = null;
    Stuck = null;
    touched = false;
  }

  // Runs an operation to its end, flattening the routines it yields (so that what they throw is seen here), and saying what went wrong
  // when something throws (the game's coroutine would only log it, and the admin would wait for lines that never come). The journal
  // tells what to do about a stop, at the next world load.
  internal static IEnumerator Guard(IEnumerator inner, Action<string> say, string what)
  {
    var stack = new Stack<IEnumerator>();
    stack.Push(inner);
    while (stack.Count > 0)
    {
      object? current = null;
      bool more;
      var top = stack.Peek();
      try
      {
        more = top.MoveNext();
        if (more)
          current = top.Current;
      }
      catch (BakeStopException e)
      {
        say($"bc_bake: {what} stopped: {e.Message}");
        Stopped(what);
        yield break;
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"bc_bake: {what} threw: {e}");
        say($"bc_bake: {what} stopped on an error: {e.Message}. The log has it. The next world load settles what was left half done (the undo file says what).");
        Stopped(what);
        yield break;
      }
      if (!more)
      {
        stack.Pop();
        continue;
      }
      if (current is IEnumerator nested)
      {
        stack.Push(nested);
        continue;
      }
      yield return current;
    }
  }

  // An operation stopped: it is no longer running, and if it had begun to change things the world is not changed by anything else until a load.
  private static void Stopped(string what)
  {
    if (touched)
      Stuck = what;
    Running = null;
  }

  // The pieces' number for display: "1,204".
  internal static string Num(long n) => BakeTally.Num(n);

  internal static string Bytes(long bytes) =>
    bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture) + " MB"
    : bytes >= 1024 ? (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB" : bytes + " B";

  // "about 0.9 MB".
  internal static string About(long bytes) => "about " + (bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : Bytes(bytes));

  internal static string Seconds(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

  // "+18 KB" or "-3 KB".
  internal static string Signed(long bytes) => (bytes >= 0 ? "+" : "-") + Bytes(Math.Abs(bytes));

  // The words of a command as a person typed them that change what a bake does, for the lines that say how to go on.
  internal static string WordsOf(BakeWords words) =>
    (words.Town ? " town" : "") + (words.Any ? " any" : "") + (words.Convert ? " convert" : "") + (words.All ? " all" : "");

  // ------------------------------------------------------------------------------------------------ the lines every operation says

  // The conversion of a world that is not a Better Continents world (4.3): what the dry run says, and what refuses a run.
  // True when the operation may go on; the lines are said either way.
  internal static bool WorldAllows(BakeContext ctx, BakeWords words, bool confirm, bool saysInDryRun)
  {
    var kind = ctx.Convert?.Kind ?? BakeWorldKind.BetterContinents;
    if (kind == BakeWorldKind.BetterContinents)
      return true;
    if (kind == BakeWorldKind.Unreadable)
    {
      ctx.Say("bc_bake: this world has a Better Continents settings file that cannot be read (it was written by a newer Better Continents, or it is damaged). "
        + "Baking would write over it, so nothing is changed. Open the world with the Better Continents that made it, or restore the file.");
      return false;
    }
    if (!words.Convert)
    {
      if (confirm || saysInDryRun)
        ctx.Say("This world is not a Better Continents world. Baking makes it one: its ground, biomes, vegetation and locations stay the game's own, and Better Continents "
          + "keeps the baked pieces with the world. From then on every player needs the same Better Continents version as this world to join it, and an older Better "
          + "Continents, or the game without it, opens it without the baked pieces. Add 'convert' to go ahead.");
      return false;
    }
    if (ctx.Convert?.Unavailable is { } cannot)
    {
      if (confirm || saysInDryRun)
        ctx.Say($"bc_bake: {cannot}. Nothing is changed.");
      return false;
    }
    var others = ctx.Transport.Peers.Where(p => p.Version != ModInfo.Version).ToList();
    if (others.Count > 0)
    {
      ctx.Say("bc_bake: " + string.Join(", ", others.Select(p => $"{p.Name} ({(p.Version == null ? "no Better Continents" : "Better Continents " + p.Version)})"))
        + $" would not be able to join the converted world: every player needs Better Continents {ModInfo.Version}. Nothing is changed.");
      return false;
    }
    return true;
  }

  // The journal, written before anything changes: false, and the line, when it cannot be.
  internal static bool WriteJournal(BakeContext ctx, BakeJournalData data, out string problem)
  {
    problem = "";
    try
    {
      ctx.Journal.Prune();
      ctx.Journal.Write(data);
      touched = true;
      ctx.Journal.SetState(data.Number, BakeState.Prepared);
      return true;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or BakeJournalException)
    {
      problem = $"bc_bake: the undo file could not be written ({e.Message}), so nothing was changed.";
      return false;
    }
  }

  // Waits for the clients to have the change and says how it went.
  internal static IEnumerator PushAndSay(BakeContext ctx, LayerChange change)
  {
    BakePush? push = null;
    yield return ctx.Transport.Push(change, p => push = p);
    if (push != null && push.Summary.Length > 0)
      ctx.Say("bc_bake: " + push.Summary + ".");
  }

  // Waits until every one of these objects has been sent to the clients that hold their zones, or SentTimeout.
  internal static IEnumerator WaitSent(BakeContext ctx, List<ZDOID> ids, string what)
  {
    if (ids.Count == 0)
      yield break;
    double start = ctx.Clock(), asked = start - SentPoll;
    while (true)
    {
      double now = ctx.Clock();
      // What ZDOMan has sent each peer is walked for every piece: twice a second is often enough.
      if (now - asked >= SentPoll)
      {
        asked = now;
        if (ctx.World.AllSent(ids))
          yield break;
      }
      if (!ctx.World.Ready)
        throw new BakeStopException("the world closed while the pieces were being sent");
      if (now - start > SentTimeout)
      {
        ctx.Say($"bc_bake: {what} had not reached every player after {SentTimeout:0} s; going on.");
        yield break;
      }
      yield return null;
    }
  }

  // The reconciliation of Live pieces with the layer that was just made current, if slice B's is there.
  internal static IEnumerator ReconcileAfter(BakeContext ctx, BakedLayer? old, LayerChange change)
  {
    if (ctx.Reconcile == null)
      yield break;
    yield return ctx.Reconcile.Run(old, change, ctx.Say);
  }

  // ------------------------------------------------------------------------------------------------ bake

  // `bc_bake area|box ...` (10.1 to 10.8): without confirm a dry run that says what would happen; with it, the operation.
  internal static IEnumerator Bake(BakeContext ctx, BakeArea area, BakeWords words, string echo)
  {
    var say = ctx.Say;
    var world = ctx.World;
    var layer = ctx.Layer;
    var problem = area.Problem();
    if (problem != null)
    {
      say($"bc_bake: {problem}.");
      yield break;
    }
    if (!world.Ready)
    {
      say("bc_bake: load a world first.");
      yield break;
    }
    bool entered = false;
    try
    {
      if (words.Confirm)
      {
        if (!Enter("a bake", true, out var refusal))
        {
          say(refusal);
          yield break;
        }
        entered = true;
      }
      var kind = area.Circle ? OperationKind.BakeArea : OperationKind.BakeBox;
      int number = ctx.Journal.NextNumber(layer.NextOperation);
      var zones = area.Zones();
      var gather = new BakeGather();
      yield return Gather(ctx, area, words, number, gather);
      if (gather.Closed)
      {
        say("bc_bake: the world closed while it was read; nothing changed.");
        yield break;
      }
      var tally = gather.Tally;
      int baked = gather.Statics.Count;

      var data = new BakeJournalData
      {
        Number = number,
        Kind = kind,
        Time = ctx.UnixTime(),
        Who = ctx.Who.Name,
        PlatformId = ctx.Who.PlatformId,
        X1 = area.MinX,
        Z1 = area.MinZ,
        X2 = area.MaxX,
        Z2 = area.MaxZ,
        Radius = area.Circle ? area.Radius : 0f,
        WorldName = world.WorldName,
        WorldUid = world.WorldUid,
        RevisionBefore = layer.Revision,
        RevisionAfter = layer.Revision + 1,
        Version = ctx.Version,
        Words = WordsOf(words).Trim(),
        ValueSets = gather.Pool.Sets,
        Records = gather.Records,
        Statics = gather.Statics.Select(s => s.Copy).ToList(),
        Adopted = gather.Adopted,
      };

      // What a dry run says of the area and of the pieces in it.
      var lines = new List<string>
      {
        $"bc_bake {echo}{WordsOf(words)}: a dry run, nothing changes. 'bc_bake {echo}{WordsOf(words)} confirm' bakes.",
        $"Area: {area.Describe(zones.Count)}.",
      };
      lines.AddRange(tally.Lines(words));
      bool nothing = baked == 0 && gather.Adopted.Count == 0;
      if (nothing)
      {
        if (!words.Confirm)
          foreach (var line in lines)
            say(line);
        else
          say($"bc_bake: nothing to bake in {area.Describe(zones.Count)}.");
        yield break;
      }
      if (gather.Pool.Sets.Count > BakeValueSetPool.Limit)
      {
        say($"bc_bake: the pieces in this area have {Num(gather.Pool.Sets.Count)} different sets of values (health, creator, ...) and a layer holds {Num(BakeValueSetPool.Limit)} for one bake. Bake a smaller area.");
        yield break;
      }
      BakeEstimate? estimate = null;
      string? cannot = null;
      try
      {
        estimate = layer.EstimateAdd(data);
      }
      catch (BakedFormatException e)
      {
        cannot = e.Message;
      }
      if (cannot != null)
      {
        say($"bc_bake: the layer cannot hold this bake: {cannot}.");
        yield break;
      }
      long undoBytes = BakeJournalFile.Encode(data).Length;
      if (!words.Confirm)
      {
        foreach (var line in lines)
          say(line);
        say($"Objects in these zones: {Num(gather.Objects)} now, {Num(gather.Objects - baked)} after."
          + (estimate != null ? $" Layer: {Signed(estimate.Growth)} ({Bytes(estimate.Bytes)}, revision {estimate.Revision})." : "") + $" Undo file: {About(undoBytes)}.");
        int players = ctx.Transport.Players;
        if (players > 0)
          say($"{players} player{(players == 1 ? "" : "s")} connected; each gets the change{(estimate is { PatchBytes: >= 0 } ? $" ({About(estimate.PatchBytes)})" : "")} before the pieces go.");
        WorldAllows(ctx, words, confirm: false, saysInDryRun: true);
        if (ctx.Transport.Unavailable is { } soon)
          say($"This build cannot bake yet: {soon}.");
        yield break;
      }
      if (ctx.Transport.Unavailable is { } nope)
      {
        say($"bc_bake: {nope}, and a bake takes its pieces out of the world once the players have the layer. Nothing is changed.");
        yield break;
      }
      if (!WorldAllows(ctx, words, confirm: true, saysInDryRun: true))
        yield break;

      // ---- the run: 10.7's steps 2 to 8.
      double began = ctx.Clock();
      if (!WriteJournal(ctx, data, out var written))
      {
        say(written);
        yield break;
      }
      say($"bc_bake: bake {number} started, {Num(baked)} pieces. Undo file written.");
      if (ctx.Convert?.Kind == BakeWorldKind.Vanilla)
      {
        ctx.Convert.Convert();
        say("bc_bake: this world is now a Better Continents world that keeps the game's own ground and biomes.");
      }

      // Step 3: the layer, in memory. The state first: it is never behind the world.
      ctx.Journal.SetState(number, BakeState.Applied);
      var change = layer.Add(data);
      // Step 4 and 5: the clients have it, or 30 s after the last packet.
      yield return PushAndSay(ctx, change);

      // Step 6: town pieces are owned and tagged, and the layer's Live records have their pieces.
      for (int i = 0; i < gather.Adopted.Count; i++)
      {
        world.Adopt(gather.AdoptedIds[i], number, gather.Adopted[i].Id, change.Revision);
        if ((i + 1) % AdoptionsPerFrame == 0)
          yield return null;
      }

      // Step 7: the statics go, 1,000 a frame. A piece that changed meanwhile (gone, moved, another prefab) stays, and its record too.
      ctx.Journal.SetState(number, BakeState.Removing);
      say($"bc_bake: removing {Num(baked)} pieces, {Num(PiecesPerFrame)} a frame.");
      int removed = 0, changedMeanwhile = 0;
      for (int i = 0; i < gather.Statics.Count; i++)
      {
        if (!world.Ready)
          throw new BakeStopException($"the world closed after {Num(removed)} removals. The next world load settles the rest.");
        var piece = gather.Statics[i];
        if (world.Remove(piece.Id, piece.Copy) == RemoveOutcome.Removed)
          removed++;
        else
          changedMeanwhile++;
        if ((i + 1) % PiecesPerFrame == 0)
          yield return null;
      }
      // Step 8.
      ctx.Journal.SetState(number, BakeState.Removed);
      ctx.Ended?.Invoke(number, BakeState.Settled);
      BakedApi.RaiseBaked(data.ToOperation());
      double took = ctx.Clock() - began;
      var stay = tally.StayCount;
      say($"bc_bake: bake {number} done in {Seconds(took)}: {Num(removed)} pieces baked, {Num(stay)} left as pieces" + (words.Town && stay > 0 ? " (town pieces, protected parts of the layer)" : "")
        + $". Objects here: {Num(gather.Objects)} -> {Num(gather.Objects - removed)}.");
      if (changedMeanwhile > 0)
        say($"bc_bake: {Num(changedMeanwhile)} piece{(changedMeanwhile == 1 ? "" : "s")} changed while the bake ran: their records stay; 'bc_bake undo' puts the area back as it was.");
      say("'bc_bake undo' puts them back. The world saves as usual ('save' saves now).");
      ctx.Who.Notify?.Invoke($"Bake {number} done: {Num(removed)} pieces baked.");
    }
    finally
    {
      Leave(entered);
    }
  }

  // Reads the area, a few zones a frame, and sorts what stands in it into the classes of 10.4.
  private static IEnumerator Gather(BakeContext ctx, BakeArea area, BakeWords words, int number, BakeGather into)
  {
    var world = ctx.World;
    var zones = area.Zones();
    var batch = new List<ZoneKey>(ZonesPerFrame);
    var found = new List<WorldObject>();
    for (int i = 0; i < zones.Count; i += ZonesPerFrame)
    {
      if (!world.Ready)
      {
        into.Closed = true;
        yield break;
      }
      batch.Clear();
      for (int j = i; j < Math.Min(i + ZonesPerFrame, zones.Count); j++)
        batch.Add(zones[j]);
      found.Clear();
      world.Gather(batch, found);
      into.Objects += found.Count;
      foreach (var piece in found)
        if (area.Contains(piece.X, piece.Z))
          Take(ctx, words, number, piece, into);
      yield return null;
    }
  }

  // One object of the area: classified, and taken when it becomes a record or is adopted.
  private static void Take(BakeContext ctx, BakeWords words, int number, in WorldObject piece, BakeGather into)
  {
    var world = ctx.World;
    var facts = world.FactsOf(piece.Prefab);
    var cls = BakeClassifier.Classify(facts, piece.Creator, words, new ObjectFacts { AlreadyBaked = piece.AlreadyBaked, HasConnection = piece.HasConnection });
    PieceCopy? copy = null;
    string name = facts?.Name ?? "";
    if (cls.Baked || cls.Kind == BakeKind.Adopt)
    {
      copy = world.Copy(piece);
      copy.PrefabName = name;
      var why = BakeCapture.Problem(copy, facts!);
      if (why != null)
      {
        // A layer cannot hold it: it stays a piece, said in the dry run as such.
        cls = new BakeClass(BakeKind.Stays, BakeClassifier.Other, why, "");
        copy = null;
      }
    }
    into.Tally.Add(cls, name, piece.Creator);
    if (copy == null)
      return;
    if (cls.Baked)
    {
      var role = cls.Kind switch { BakeKind.Seat => BakedRole.Seat, BakeKind.Copy => BakedRole.Copy, _ => BakedRole.Static };
      var record = BakeCapture.MakeRecord(copy, facts!, role, number, 0, into.Pool);
      into.Records.Add(record);
      into.Statics.Add(new Taken { Id = piece.Id, Copy = copy, Record = record });
      return;
    }
    // Adopted: the same object, a protected Live record of the bake with an id of its own.
    uint id = (uint)(into.Adopted.Count + 1);
    var live = BakeCapture.MakeRecord(copy, facts!, BakedRole.Live, number, id, into.Pool);
    into.Records.Add(live);
    into.Adopted.Add(new AdoptedPiece { Place = Place(copy), Id = id, Source = number });
    into.AdoptedIds.Add(piece.Id);
  }

  // Where a piece stands, without its values: what the journal keeps of a piece that stays.
  internal static PieceCopy Place(PieceCopy copy) => new()
  {
    Prefab = copy.Prefab,
    PrefabName = copy.PrefabName,
    X = copy.X,
    Y = copy.Y,
    Z = copy.Z,
    RotX = copy.RotX,
    RotY = copy.RotY,
    RotZ = copy.RotZ,
    Persistent = copy.Persistent,
    Distant = copy.Distant,
    Type = copy.Type,
    UserId = copy.UserId,
    Id = copy.Id,
  };
}

// A stop that is the operation's own: its message is for the admin, and nothing is logged as an error.
internal sealed class BakeStopException : Exception
{
  public BakeStopException(string message) : base(message)
  {
  }
}

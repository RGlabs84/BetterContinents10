// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of the in-game bake's runner (BakeRunner): whole operations run against stand-ins for the game's objects, the players' transport
// and the reconciliation, with the real LayerPort over a real layer and the undo files on disk. The stand-in world counts every change it is
// asked to make (an object made, removed or tagged) with every write of an undo file and every change of the layer, as the mutations of an
// operation; Tests.BakeRunnerCrash.cs stops operations at each of them and settles what is left.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  // ------------------------------------------------------------------------------------------------ the stand-ins

  private sealed class BakeSimulatedCrash : Exception
  {
    public BakeSimulatedCrash(string what) : base("the game stopped before: " + what)
    {
    }
  }

  // Every mutation of an operation goes through Do: the first one past Limit stops the operation (before it changes anything), and each one
  // that is done is announced, so that a recording run can keep the world, the layer and the undo files as they were after it.
  private sealed class BakeClock
  {
    public int Count;
    public int Limit = int.MaxValue;
    public Action<string> Applied;
    public readonly List<string> Events = [];

    public void Do(string what, Action apply)
    {
      if (Count >= Limit)
        throw new BakeSimulatedCrash(what);
      apply();
      Count++;
      Events.Add(what);
      Applied?.Invoke(what);
    }
  }

  private sealed class BakeCrashJournal : BakeJournal
  {
    private readonly BakeClock clock;

    public BakeCrashJournal(string folder, BakeClock clock) : base(folder)
    {
      this.clock = clock;
    }

    internal override void Write(BakeJournalData data) => clock.Do("write the undo file of " + data.Number, () => base.Write(data));

    internal override void SetState(int number, BakeState state) => clock.Do($"state {state}", () => base.SetState(number, state));
  }

  private sealed class BakeFakeWorld : IBakeWorld
  {
    public readonly BakeClock Clock;
    public readonly Dictionary<ZDOID, PieceCopy> Objects = [];
    public readonly Dictionary<int, PrefabFacts> Facts = [];
    public readonly Dictionary<int, LookInfo> Looks = [];
    public long Session;
    public bool Closed;
    // The world closes after this many removals (-1: it does not).
    public int CloseAfterRemovals = -1;
    private int removals;
    public int PollsToSend = 2;
    private int polls;
    private uint nextId = 1;

    public BakeFakeWorld(BakeClock clock, long session)
    {
      Clock = clock;
      Session = session;
    }

    public bool Ready => !Closed;
    public string WorldName => "Fake World";
    public long WorldUid => 777;

    public PrefabFacts FactsOf(int prefab) => Facts.TryGetValue(prefab, out var facts) ? facts : null;

    public int PrefabOf(string name) => Facts.ContainsKey(name.GetStableHashCode()) ? name.GetStableHashCode() : 0;

    public (bool Persistent, bool Distant, byte Type) FlagsOf(int prefab) => (true, false, 2);

    public LookInfo LookOf(int prefab) => Looks.TryGetValue(prefab, out var look) ? look : LookInfo.None;

    public ZDOID Add(PieceCopy piece)
    {
      var id = new ZDOID(Session, nextId++);
      var copy = BakeClone(piece);
      copy.UserId = id.UserID;
      copy.Id = id.ID;
      Objects[id] = copy;
      return id;
    }

    private IEnumerable<KeyValuePair<ZDOID, PieceCopy>> InOrder() => Objects.OrderBy(o => o.Key.ID);

    public void Gather(IReadOnlyList<ZoneKey> zones, List<WorldObject> into)
    {
      // Zone by zone, as the game's world is read, and in a zone in the order the world holds its objects.
      foreach (var zone in zones)
        foreach (var (id, piece) in InOrder().Where(o => ZoneKey.OfPoint(o.Value.X, o.Value.Z) == zone))
        into.Add(new WorldObject
        {
          Id = id,
          Prefab = piece.Prefab,
          X = piece.X,
          Y = piece.Y,
          Z = piece.Z,
          Creator = piece.Find(BakeKey("creator"), ZdoValueType.Long)?.Number ?? 0L,
          AlreadyBaked = piece.Find(BakedKeys.Id, ZdoValueType.Int) != null,
          HasConnection = false,
        });
    }

    public PieceCopy Copy(in WorldObject piece)
    {
      var copy = BakeClone(Objects[piece.Id]);
      copy.PrefabName = "";
      return copy;
    }

    public Dictionary<ulong, ZDOID> LiveIn(ZoneKey zone)
    {
      var live = new Dictionary<ulong, ZDOID>();
      foreach (var (id, piece) in InOrder())
        if (ZoneKey.OfPoint(piece.X, piece.Z) == zone && piece.Find(BakedKeys.Id, ZdoValueType.Int) is { } key)
          live[BakedFormat.LiveKey((int)(piece.Find(BakedKeys.Src, ZdoValueType.Int)?.Number ?? 0), unchecked((uint)(int)key.Number))] = id;
      return live;
    }

    public RemoveOutcome Remove(ZDOID id, PieceCopy expected)
    {
      if (!Objects.TryGetValue(id, out var piece) || piece.Prefab != expected.Prefab)
        return RemoveOutcome.Changed;
      double dx = piece.X - expected.X, dy = piece.Y - expected.Y, dz = piece.Z - expected.Z;
      if (dx * dx + dy * dy + dz * dz > BakeMath.PlaceMetres * BakeMath.PlaceMetres)
        return RemoveOutcome.Changed;
      Clock.Do("remove " + piece.PrefabName, () => Objects.Remove(id));
      if (++removals == CloseAfterRemovals)
        Closed = true;
      return RemoveOutcome.Removed;
    }

    public ZDOID Create(PieceCopy copy)
    {
      ZDOID made = default;
      Clock.Do("make " + copy.PrefabName, () => made = Add(copy));
      return made;
    }

    public ZDOID?[] FindStanding(IReadOnlyList<PieceCopy> pieces)
    {
      var ordered = InOrder().ToList();
      var poses = ordered.Select(o => new ObjectPose
      {
        Id = o.Key, Prefab = o.Value.Prefab, X = o.Value.X, Y = o.Value.Y, Z = o.Value.Z, RotX = o.Value.RotX, RotY = o.Value.RotY, RotZ = o.Value.RotZ,
      }).ToList();
      var found = BakeMatch.Find(poses, pieces);
      return found.Select(i => i < 0 ? (ZDOID?)null : poses[i].Id).ToArray();
    }

    public bool AllSent(IReadOnlyList<ZDOID> ids) => ++polls >= PollsToSend;

    // The four keys one at a time, as the game sets them: a stop between two leaves a piece with some of them.
    public void Adopt(ZDOID id, int source, uint bakeId, uint revision)
    {
      if (!Objects.ContainsKey(id))
        return;
      Clock.Do("key src", () => Set(id, ZdoValue.OfInt(BakedKeys.Src, source)));
      Clock.Do("key id", () => Set(id, ZdoValue.OfInt(BakedKeys.Id, unchecked((int)bakeId))));
      Clock.Do("key rev", () => Set(id, ZdoValue.OfInt(BakedKeys.Rev, unchecked((int)revision))));
      Clock.Do("key protect", () => Set(id, ZdoValue.OfInt(BakedKeys.Protect, 1)));
    }

    public void Release(ZDOID id, long creatorIfNone)
    {
      if (!Objects.ContainsKey(id))
        return;
      foreach (int key in BakedKeys.BakeKeys)
        if (Objects[id].Find(key, ZdoValueType.Int) != null)
          Clock.Do("key off", () => Objects[id].Values = Objects[id].Values.Where(v => v.Key != key).ToArray());
      if (creatorIfNone != 0 && Objects[id].Find(BakeKey("creator"), ZdoValueType.Long) == null)
        Clock.Do("creator", () => Set(id, ZdoValue.OfLong(BakeKey("creator"), creatorIfNone)));
    }

    private void Set(ZDOID id, ZdoValue value)
    {
      var piece = Objects[id];
      var values = piece.Values.Where(v => !(v.Key == value.Key && v.Type == value.Type)).Append(value).ToList();
      values.Sort(BakeValues.Compare);
      piece.Values = [.. values];
    }

    public (float X, float Y, float Z) EulerOf(Quaternion rotation) => BakeMath.EulerOf(rotation);
  }

  private sealed class BakeFakeTransport : IBakeTransport
  {
    public readonly BakeClock Clock;
    public readonly List<LayerChange> Pushed = [];
    public string Unavailable { get; set; }
    public int Players { get; set; } = 2;
    public List<(string Name, string Version)> PeerList { get; } = [];
    public IReadOnlyList<(string Name, string Version)> Peers => PeerList;

    public BakeFakeTransport(BakeClock clock)
    {
      Clock = clock;
    }

    public IEnumerator Push(LayerChange change, Action<BakePush> done)
    {
      Pushed.Add(change);
      Clock.Events.Add("push r" + change.Revision);
      yield return null;
      yield return null;
      done(new BakePush { Summary = $"layer revision {change.Revision} sent to {Players} players; {Players} of {Players} ready in 0.1 s", Seconds = 0.1 });
    }
  }

  private sealed class BakeFakeConvert : IBakeConvert
  {
    public BakeWorldKind Kind { get; set; } = BakeWorldKind.BetterContinents;
    public string Unavailable { get; set; }
    public int Converted;

    public void Convert()
    {
      Converted++;
      Kind = BakeWorldKind.BetterContinents;
    }
  }

  private sealed class BakeFakeReconcile : IBakeReconcile
  {
    public readonly BakeClock Clock;
    public int Runs;

    public BakeFakeReconcile(BakeClock clock)
    {
      Clock = clock;
    }

    public IEnumerator Run(BakedLayer old, LayerChange change, Action<string> say)
    {
      Runs++;
      Clock.Events.Add("reconcile r" + change.Revision);
      yield return null;
    }

    public int Previews;

    public IEnumerator Preview(BakedLayer old, BakedLayer next, Action<string> say)
    {
      Previews++;
      say($"The live pieces would follow: {next.Registry.Operations.Count} operations in the layer (a stand-in).");
      yield return null;
    }
  }

  // The moment after a mutation: the layer, the objects (copied) and the undo folder's files.
  private sealed class BakeMoment
  {
    public string What = "";
    public BakedLayer Layer;
    public List<PieceCopy> Objects = [];
    public Dictionary<string, byte[]> Files = [];
  }

  private static PieceCopy BakeClone(PieceCopy p) => new()
  {
    Prefab = p.Prefab, PrefabName = p.PrefabName, X = p.X, Y = p.Y, Z = p.Z, RotX = p.RotX, RotY = p.RotY, RotZ = p.RotZ, Persistent = p.Persistent,
    Distant = p.Distant, Type = p.Type, Values = (ZdoValue[])p.Values.Clone(), UserId = p.UserId, Id = p.Id,
  };

  private sealed class BakeFakeSaves : IBakeSaves
  {
    public uint Completed { get; set; }
    public bool Saving { get; set; }
    public bool WorldReady { get; set; } = true;
  }

  // One run of the runner on stand-ins.
  private sealed class BakeFx
  {
    public string Folder = "";
    public BakeClock Clock = new();
    public BakeFakeWorld World;
    public BakedLayer Layer;
    public LayerPort Port;
    public BakeFakeTransport Transport;
    public BakeFakeConvert Convert = new();
    public BakeFakeReconcile Reconcile;
    public BakeCrashJournal Journal;
    public BakeContext Ctx;
    public readonly List<string> Said = [];
    public readonly List<BakeMoment> Moments = [];
    public double Time;
    public bool Recording;
    public long Creator = 5005;
    // The world's saves, as BakeSettle asks about them.
    public readonly BakeFakeSaves Saves = new();

    public BakeMoment Snap(string what)
    {
      var moment = new BakeMoment { What = what, Layer = Layer };
      foreach (var piece in World.Objects.OrderBy(o => o.Key.ID))
        moment.Objects.Add(BakeClone(piece.Value));
      if (Directory.Exists(Folder))
        foreach (var file in Directory.GetFiles(Folder))
          moment.Files[Path.GetFileName(file)] = File.ReadAllBytes(file);
      return moment;
    }
  }

  private static readonly string[] BakeSafeParts = ["Piece", "WearNTear", "ZNetView", "UnityEngine.MeshFilter"];

  private static PrefabFacts[] BakeFakePrefabs() =>
  [
    new("stone_wall_2x1", BakeSafeParts, height: 3f),
    new("wood_floor", BakeSafeParts, height: 1f),
    new("wood_beam", BakeSafeParts, syncsScale: true, height: 6f),
    new("wood_chair", [.. BakeSafeParts, "Chair"], height: 1f),
    new("wood_door", [.. BakeSafeParts, "Door"], height: 3f),
    new("piece_chest", [.. BakeSafeParts, "Container"], height: 1f),
    new("Pine_tree", ["ZNetView"]),
  ];

  private static PieceCopy BakeObject(string prefab, float x, float y, float z, float yaw = 0f, long creator = 1001, float rx = 0f, float rz = 0f, params ZdoValue[] extra)
  {
    var values = new List<ZdoValue>();
    if (creator != 0)
      values.Add(ZdoValue.OfLong(BakeKey("creator"), creator));
    values.AddRange(extra);
    values.Sort(BakeValues.Compare);
    return new PieceCopy
    {
      Prefab = prefab.GetStableHashCode(), PrefabName = prefab, X = x, Y = y, Z = z, RotX = rx, RotY = yaw, RotZ = rz, Persistent = true, Distant = false, Type = 2,
      Values = [.. values],
    };
  }

  // The world of the bake tests: pieces in two zones that a bake takes, and pieces that it leaves (a door, a chest, a tree, a piece nobody built, a
  // wall far away). Returns the pieces a bake takes, as they stand.
  private static List<PieceCopy> BakeStandardWorld(BakeFakeWorld world)
  {
    foreach (var facts in BakeFakePrefabs())
      world.Facts[facts.Name.GetStableHashCode()] = facts;
    world.Looks["wood_floor".GetStableHashCode()] = new LookInfo { Variations = [new KeyValuePair<int, float[]>(0, [1f, 2f, 1f])], RandomValues = false };
    var taken = new List<PieceCopy>
    {
      BakeObject("stone_wall_2x1", 5f, 10f, 5f, 0f),
      BakeObject("stone_wall_2x1", 7f, 10f, 5f, 90f),
      BakeObject("stone_wall_2x1", 9f, 10f, 5f, 22.5f),
      BakeObject("stone_wall_2x1", 11f, 10.25f, 5f, 37.3217f),
      BakeObject("wood_floor", 40f, 10f, 5f, 0f, 1001, 0f, 0f, ZdoValue.OfInt(BakeKey("MatVar0"), 2), ZdoValue.OfFloat(BakeKey("health"), 80f)),
      BakeObject("wood_floor", 42f, 10f, 5f, 180f, 1002, 0f, 0f, ZdoValue.OfInt(BakeKey("MatVar0"), 0)),
      BakeObject("wood_chair", 13f, 10f, 7f, 270f),
      BakeObject("wood_beam", 15f, 12f, 9f, 45f, 1001, 30.3f, 10f, ZdoValue.OfVec3(BakeKey("scale"), 2f, 2f, 2.5f)),
    };
    foreach (var piece in taken)
      world.Add(piece);
    // What stays.
    world.Add(BakeObject("wood_door", 3f, 10f, 3f, 90f, 1001, 0f, 0f, ZdoValue.OfInt(BakeKey("state"), 1)));
    world.Add(BakeObject("piece_chest", 4f, 10f, 8f, 0f, 1001, 0f, 0f, ZdoValue.OfString(BakeKey("items"), "sword x1")));
    world.Add(BakeObject("Pine_tree", 20f, 10f, 20f, 0f, 0));
    world.Add(BakeObject("stone_wall_2x1", 21f, 10f, 3f, 0f, 0));
    world.Add(BakeObject("stone_wall_2x1", 300f, 10f, 5f, 0f));
    return taken;
  }

  private static BakeFx BakeNewFx(string name, Func<BakeFakeWorld, List<PieceCopy>> populate, out List<PieceCopy> taken, BakedLayer layer = null, bool recording = false)
  {
    var fx = new BakeFx { Folder = Path.Combine(Work, "bake-" + name), Recording = recording };
    if (Directory.Exists(fx.Folder))
      Directory.Delete(fx.Folder, true);
    else if (File.Exists(fx.Folder))
      File.Delete(fx.Folder);
    fx.World = new BakeFakeWorld(fx.Clock, 4242);
    taken = populate(fx.World);
    fx.Layer = layer;
    BakeWire(fx);
    if (recording)
    {
      fx.Clock.Applied = what => fx.Moments.Add(fx.Snap(what));
      fx.Moments.Add(fx.Snap("start"));
    }
    return fx;
  }

  private static void BakeWire(BakeFx fx)
  {
    fx.Port = new LayerPort(() => fx.Layer, (layer, _) => fx.Clock.Do("layer", () => fx.Layer = layer));
    fx.Transport = new BakeFakeTransport(fx.Clock);
    fx.Reconcile = new BakeFakeReconcile(fx.Clock);
    fx.Journal = new BakeCrashJournal(fx.Folder, fx.Clock);
    fx.Ctx = new BakeContext
    {
      World = fx.World,
      Layer = fx.Port,
      Transport = fx.Transport,
      Reconcile = fx.Reconcile,
      Convert = fx.Convert,
      Journal = fx.Journal,
      Say = line => fx.Said.Add(line),
      Clock = () => fx.Time += 0.05,
      Who = new BakeWho { Name = "Tester", PlatformId = "Steam_1", Creator = fx.Creator },
      Version = "0.10.4",
      UnixTime = () => 1791400000,
      Ended = (number, final) => BakeSettle.Register(fx.Saves, fx.Journal, number, final),
      Began = number => BakeSettle.Cancel(fx.Journal, number),
    };
  }

  // Runs a routine the way the game does, through Guard, to its end; the number of frames it took.
  private static int BakeDrive(IEnumerator routine, BakeFx fx, string what)
  {
    var guarded = BakeRunner.Guard(routine, fx.Ctx.Say, what);
    int frames = 0;
    while (guarded.MoveNext())
      if (++frames > 1_000_000)
        throw new InvalidOperationException("an operation that does not end");
    return frames;
  }

  private static BakeWords BakeWordsOf(string text) => new()
  {
    Town = text.Contains("town"), Any = text.Contains("any"), Convert = text.Contains("convert"), Confirm = text.Contains("confirm"), All = text.Contains("all"),
  };

  private static int BakeRun(BakeFx fx, BakeArea area, string words = "confirm") =>
    BakeDrive(BakeRunner.Bake(fx.Ctx, area, BakeWordsOf(words), area.Words(false)), fx, "a bake");

  private static BakeArea BakeStandardArea() => BakeArea.OfCircle(30f, 0f, 30f);

  // Every record of the layer this machine holds.
  private static List<BakeRecord> BakeRecordsOf(BakeFx fx) => fx.Port.RecordsIn(fx.Port.ZonesWithRecords(), _ => true);

  private static bool BakeIsRecord(BakeRecord record, PieceCopy piece) =>
    record.Prefab == piece.PrefabName && Math.Abs(record.Record.WorldX - piece.X) < 0.001 && Math.Abs(record.Record.WorldY - piece.Y) < 0.001 && Math.Abs(record.Record.WorldZ - piece.Z) < 0.001;

  private static bool BakeStands(BakeFx fx, PieceCopy piece) =>
    fx.World.Objects.Values.Any(o => BakeMath.SamePlace(piece, o.Prefab, o.X, o.Y, o.Z, o.RotX, o.RotY, o.RotZ));

  private static int BakeCountObjects(BakeFx fx, string prefab) => fx.World.Objects.Values.Count(o => o.PrefabName == prefab);

  private static bool BakeHasKeys(PieceCopy piece) => BakedKeys.BakeKeys.All(k => piece.Find(k, ZdoValueType.Int) != null);

  private static bool BakeHasAnyKey(PieceCopy piece) => BakedKeys.BakeKeys.Any(k => piece.Find(k, ZdoValueType.Int) != null);

  // ------------------------------------------------------------------------------------------------ the area

  private static void BakeAreaTest()
  {
    Section("runner: the area of a bake, a circle or a box");
    var circle = BakeArea.OfCircle(30f, 0f, 30f);
    C(circle.Problem() == null && circle.Contains(30f, 0f) && circle.Contains(60f, 0f) && !circle.Contains(60.1f, 0f) && !circle.Contains(55f, 30f), "a circle holds the points within its radius");
    C(BakeArea.OfCircle(0f, 0f, 3.9f).Problem() != null && BakeArea.OfCircle(0f, 0f, 256.1f).Problem() != null && BakeArea.OfCircle(0f, 0f, 4f).Problem() == null && BakeArea.OfCircle(0f, 0f, 256f).Problem() == null,
      "a radius is 4 to 256 m");
    var zones = circle.Zones();
    C(zones.SequenceEqual([new ZoneKey(0, 0), new ZoneKey(1, 0)]), "30 m around 30, 0 touches zone 0,0 and 1,0 in the order the work goes in (z, then x): " + string.Join(" ", zones));
    var corner = BakeArea.OfCircle(30f, 5f, 30f).Zones();
    C(corner.SequenceEqual([new ZoneKey(0, 0), new ZoneKey(1, 0), new ZoneKey(0, 1), new ZoneKey(1, 1)]), "30 m around 30, 5 reaches over the edge of the zones to the south: four zones, z first: " + string.Join(" ", corner));
    var wide = BakeArea.OfCircle(0f, 0f, 100f).Zones();
    C(wide.Count == 13 && !wide.Contains(new ZoneKey(2, 2)) && !wide.Contains(new ZoneKey(2, 1)) && wide.Contains(new ZoneKey(-2, 0)) && wide.Contains(new ZoneKey(0, 2)),
      $"a circle takes only the zones it reaches (100 m around the origin: {wide.Count} of 25)");
    var onEdge = BakeArea.OfCircle(32f, 0f, 4f).Zones();
    C(onEdge.Contains(new ZoneKey(0, 0)) && onEdge.Contains(new ZoneKey(1, 0)), "an area that ends on a zone's edge looks in both zones");
    var box = BakeArea.OfBox(500f, -100f, 400f, 100f);
    C(box.Problem() == null && box.MinX == 400f && box.MaxX == 500f && box.Contains(450f, 0f) && !box.Contains(399f, 0f), "a box takes its corners in either order");
    C(BakeArea.OfBox(0f, 0f, 513f, 10f).Problem() != null && BakeArea.OfBox(0f, 0f, 512f, 512f).Problem() == null && BakeArea.OfBox(0f, 0f, 0.2f, 10f).Problem() != null, "a box is up to 512 x 512 m and has two different corners");
    C(BakeArea.OfCircle(float.NaN, 0f, 10f).Problem() != null, "a place that is not a number is refused");
    C(circle.Describe(2) == "30 m around 30, 0 (2 zones)" && box.Describe(1).StartsWith("box 400, -100 to 500, 100"), "the words for the dry run: " + circle.Describe(2));
    C(circle.Words(true) == "area 30 at 30 0" && circle.Words(false) == "area 30" && box.Words(false) == "box 400 -100 500 100", "and the words that make the area again");

    Section("runner: Euler angles back from a rotation");
    var rng = new System.Random(77);
    double worst = 0, nearPole = 0;
    for (int i = 0; i < 20000; i++)
    {
      double x = rng.NextDouble() * 360, y = rng.NextDouble() * 360, z = rng.NextDouble() * 360;
      if (i % 50 == 0)
        x = 90 + (rng.NextDouble() - 0.5) * 0.2;
      var q = BakeMath.EulerToQuaternion(x, y, z);
      var e = BakeMath.EulerOf(new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W));
      double miss = BakeMath.AngleBetween(BakeMath.EulerToQuaternion(e.X, e.Y, e.Z), q);
      // Within a tenth of a degree of straight up or down the roll cannot be told from the yaw, and is dropped, as the game's does.
      bool pole = Math.Abs(BakeCapture.Wrap360(x) - 90.0) < 0.1 || Math.Abs(BakeCapture.Wrap360(x) - 270.0) < 0.1;
      if (pole)
        nearPole = Math.Max(nearPole, miss);
      else
        worst = Math.Max(worst, miss);
      if (e.X < 0 || e.X >= 360 || e.Y < 0 || e.Y >= 360 || e.Z < 0 || e.Z >= 360)
        worst = 999;
    }
    C(worst < 0.001, $"20,000 rotations: the angles EulerOf gives make the same rotation again (worst {worst:0.######} degrees)");
    C(nearPole < 0.2, $"and within a tenth of a degree of straight up the miss is under 0.2 degrees (worst {nearPole:0.####})");
    var straight = BakeMath.EulerOf(BakedFormat.YawRotation(BakedFormat.QuantizeYaw(90.0)));
    C(Math.Abs(straight.X) < 1e-3 && Math.Abs(straight.Y - 90f) < 1e-3 && Math.Abs(straight.Z) < 1e-3, "a yaw of 90 is (0, 90, 0)");
    var up = BakeMath.EulerToQuaternion(90, 30, 0);
    var gimbal = BakeMath.EulerOf(new Quaternion((float)up.X, (float)up.Y, (float)up.Z, (float)up.W));
    C(Math.Abs(gimbal.X - 90f) < 0.01 && BakeMath.AngleBetween(BakeMath.EulerToQuaternion(gimbal.X, gimbal.Y, gimbal.Z), up) < 0.001, "a pitch of 90 degrees has the roll folded into the yaw, and is the same rotation");
  }
}

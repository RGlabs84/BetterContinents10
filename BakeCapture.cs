// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BetterContinents;

// What a baked piece keeps (10.5), and what undo and unbake need to give it back.
//
// A baked piece stops being an object, so everything that made it that object must live on somewhere:
//   - in the layer's record (slice A's ZoneRecord and PaletteEntry): where it stands (pivot, rotation, scale), its prefab and look
//     (MatVar values in the palette entry's tags, RandMatSeed in the record), and the "value set", every other ZDO value (creator,
//     health, cheated, mod fields) shared by all pieces that have the same ones;
//   - in the journal's PieceCopy: the whole ZDO, so that undo puts back exactly the object that was removed.
// The arithmetic is plain, so the offline tests drive it; the game's ZDO is read and written in the few members under "the game's ZDO".

// Helpers on the layer's value type (ZdoValue is slice A's: one ZDO value by its key's hash).
internal static class BakeValues
{
  // The order values are kept in: by key, then type.
  internal static int Compare(ZdoValue a, ZdoValue b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Type.CompareTo(b.Type);

  internal static ZdoValue? Find(this IEnumerable<ZdoValue> values, int key, ZdoValueType type)
  {
    foreach (var v in values)
      if (v.Key == key && v.Type == type)
        return v;
    return null;
  }

  // A text for failure messages and the log.
  internal static string Describe(this ZdoValue v) => v.Type switch
  {
    ZdoValueType.Float => $"{v.Key}={v.A}",
    ZdoValueType.Vec3 => $"{v.Key}=({v.A}, {v.B}, {v.C})",
    ZdoValueType.Quat => $"{v.Key}=({v.A}, {v.B}, {v.C}, {v.D})",
    ZdoValueType.Int => $"{v.Key}={v.Number}",
    ZdoValueType.Long => $"{v.Key}={v.Number}L",
    ZdoValueType.String => $"{v.Key}=\"{v.Text}\"",
    _ => $"{v.Key}=bytes[{v.Data.Length}]",
  };

  // A set of values, sorted.
  internal static ValueSet SetOf(IEnumerable<ZdoValue> values)
  {
    var list = values.ToList();
    list.Sort(Compare);
    return new ValueSet(list.ToArray());
  }
}

// The value sets of one operation, each once, in the order they were first used: a record points to its set by its index here.
internal sealed class BakeValueSetPool
{
  public List<ValueSet> Sets { get; } = [];
  private readonly Dictionary<ValueSet, int> index = [];

  // The most value sets one operation can hold (the registry's and the records' index is a u16).
  public const int Limit = 65535;

  public int Add(ValueSet set)
  {
    if (index.TryGetValue(set, out var i))
      return i;
    i = Sets.Count;
    Sets.Add(set);
    index[set] = i;
    return i;
  }
}

// One record an in-game bake adds to the layer, takes out, or puts back: the layer's record with the palette entry it points to
// beside it, because a palette index means something only in one layer (a load rebuilds the palette).
internal sealed class BakeRecord
{
  public PaletteEntry Entry { get; }
  // Palette is the index in whatever layer or table the record is in at the moment; the edit sets it when it adds the record.
  public ZoneRecord Record;
  // The value set the record points to (its content), so that a record read back carries it.
  public ValueSet Values { get; }

  // How far above its y the piece reaches, in m, for the y bounds of its zone (culling): the prefab's own height when it is known, else a
  // height taller than any piece. Not kept in the journal: a record put back from it gets the taller one.
  public float Height { get; init; } = BakeRunner.RecordHeight;

  public BakeRecord(PaletteEntry entry, ZoneRecord record, ValueSet values)
  {
    Entry = entry;
    Record = record;
    Values = values;
  }

  public BakedRole Role => Entry.Role;
  public string Prefab => Entry.Name;
  public ZoneKey Zone => Record.Zone;
  public override string ToString() => $"{Prefab} at {Record.WorldX:0.###}, {Record.WorldY:0.###}, {Record.WorldZ:0.###} src {Record.SourceNumber}" + (Record.HasId ? $" id {Record.Id}" : "");
}

// A piece as the world has it, whole: what undo puts back. Every typed value of its ZDO except the game's session-only keys.
internal sealed class PieceCopy
{
  public int Prefab { get; set; }
  public string PrefabName { get; set; } = "";
  public float X { get; set; }
  public float Y { get; set; }
  public float Z { get; set; }
  // The ZDO's rotation as it stores it: Euler angles in degrees.
  public float RotX { get; set; }
  public float RotY { get; set; }
  public float RotZ { get; set; }
  public bool Persistent { get; set; }
  public bool Distant { get; set; }
  public byte Type { get; set; }
  public ZdoValue[] Values { get; set; } = [];
  // The ZDOID it had in the session it was copied in; ZDOIDs change at every load, so this is only for the log and for lookups
  // within that session.
  public long UserId { get; set; }
  public uint Id { get; set; }

  public ZoneKey Zone => ZoneKey.OfPoint(X, Z);

  public ZdoValue? Find(int key, ZdoValueType type) => Values.Find(key, type);

  public override string ToString() => $"{(PrefabName.Length > 0 ? PrefabName : Prefab.ToString())} at {X:0.###}, {Y:0.###}, {Z:0.###}";
}

// The pure half of capture: which keys the record does not carry as values, and the look, rotation and scale rules.
internal static class BakeCapture
{
  // "MatVar0" and its kin: MaterialVariation keeps its choice of material slot i under MatVar<i>. A prefab has a few slots at most.
  internal const int MatVarSlots = 32;
  internal static readonly Dictionary<int, int> MatVarKeys = Enumerable.Range(0, MatVarSlots).ToDictionary(i => BakedKeys.MatVar(i), i => i);
  internal static readonly int ScaleKey = "scale".GetStableHashCode();
  internal static readonly int ScaleScalarKey = "scaleScalar".GetStableHashCode();
  internal static readonly int SnowKey = "snow".GetStableHashCode();
  internal static readonly int PreSnowKey = "preSnow".GetStableHashCode();
  internal static readonly int CreatorKey = "creator".GetStableHashCode();
  internal static readonly int CreatorIndexKey = "creatorIndex".GetStableHashCode();
  internal static readonly int HealthKey = "health".GetStableHashCode();

  // The most a text value can hold in the registry (a Str16).
  internal const int MaxText = 65535;

  // The record of a copied piece: the look out of the ZDO into the palette entry's tags and the seed, the scale if the prefab syncs
  // it, and every other value into the value set (10.5's table). role is Static or Seat, or Live for a town piece (protected, with
  // an id). The palette index of the record is not set: the edit that adds it sets it.
  internal static BakeRecord MakeRecord(PieceCopy copy, PrefabFacts facts, BakedRole role, int source, uint id, BakeValueSetPool pool)
  {
    var matVars = new List<(int Slot, int Variant)>();
    int seed = -1;
    foreach (var v in copy.Values)
    {
      if (v.Type == ZdoValueType.Int && MatVarKeys.TryGetValue(v.Key, out var slot))
        matVars.Add((slot, (int)v.Number));
      else if (v.Type == ZdoValueType.Int && v.Key == BakedKeys.Seed && v.Number >= 0 && v.Number <= BakedFormat.MaxSeed)
        seed = (int)v.Number;
    }
    matVars.Sort((a, b) => a.Slot.CompareTo(b.Slot));
    var set = BakeValues.SetOf(copy.Values.Where(v => InValueSet(v, seed >= 0)));
    int setIndex = pool.Add(set);
    var tags = matVars.Select(m => Tag.OfInt(BakedKeys.MatVarKey(m.Slot), m.Variant)).ToArray();
    var name = copy.PrefabName.Length > 0 ? copy.PrefabName : facts.Name;
    // One candidate, the piece's own prefab, with the record at its pivot (anchor 0); the colliders are the prefab's own; layer 0.
    var entry = new PaletteEntry([new Candidate(name, 0, 0, 0)], role, BakedCollision.Prefab, 0, role == BakedRole.Live ? PaletteFlags.Protect : PaletteFlags.None, tags: tags);
    Vector3? scale = ScaleOf(copy, facts, out var s) ? s : null;
    uint? lasting = role == BakedRole.Live ? id : null;
    ZoneRecord record;
    if (IsLevel(copy.RotX, copy.RotZ))
      record = ZoneRecord.CreateYaw(0, copy.X, copy.Y, copy.Z, Wrap360(copy.RotY), scale, lasting, source, setIndex, seed);
    else
    {
      var q = BakeMath.EulerToQuaternion(copy.RotX, copy.RotY, copy.RotZ);
      // q and -q are one rotation: keep w non-negative.
      if (q.W < 0)
        q = (-q.X, -q.Y, -q.Z, -q.W);
      record = ZoneRecord.Create(0, copy.X, copy.Y, copy.Z, new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W), scale, lasting, source, setIndex, seed);
    }
    return new BakeRecord(entry, record, set) { Height = facts.Height > 0f ? facts.Height : BakeRunner.RecordHeight };
  }

  // The scale a ZNetView with m_syncInitialScale gives its piece: the Vec3 "scale" when it is not zero, else the float "scaleScalar" on
  // every axis (ZNetView.Awake). False for a piece at its prefab's own scale, or a prefab that does not sync it.
  internal static bool ScaleOf(PieceCopy copy, PrefabFacts facts, out Vector3 scale)
  {
    scale = Vector3.one;
    if (!facts.SyncsScale)
      return false;
    var vec = copy.Find(ScaleKey, ZdoValueType.Vec3);
    if (vec is { } v && (v.A != 0f || v.B != 0f || v.C != 0f))
      scale = new Vector3(v.A, v.B, v.C);
    else if (copy.Find(ScaleScalarKey, ZdoValueType.Float) is { } scalar)
      scale = new Vector3(scalar.A, scalar.A, scalar.A);
    else
      return false;
    // A piece at its prefab's own scale needs no scale run.
    return !(scale.x == 1f && scale.y == 1f && scale.z == 1f);
  }

  // Whether a copied value goes into the value set (10.5): everything except what the record carries another way (MatVar, the seed,
  // scale) and the weather's snow. The game's session-only keys are not in a copy at all.
  internal static bool InValueSet(ZdoValue v, bool seedKept)
  {
    if (v.Type == ZdoValueType.Int)
    {
      if (MatVarKeys.ContainsKey(v.Key))
        return false;
      if (v.Key == BakedKeys.Seed)
        return !(seedKept && v.Number >= 0 && v.Number <= BakedFormat.MaxSeed);
      if (v.Key == PreSnowKey)
        return false;
    }
    if (v.Type == ZdoValueType.Vec3 && v.Key == ScaleKey)
      return false;
    if (v.Type == ZdoValueType.Float && (v.Key == ScaleScalarKey || v.Key == SnowKey))
      return false;
    return true;
  }

  // Why a piece cannot become a record, if it cannot (the layer's limits, 2.2): a piece like that stays real, and the dry run says so.
  internal static string? Problem(PieceCopy copy, PrefabFacts facts)
  {
    var name = copy.PrefabName.Length > 0 ? copy.PrefabName : facts.Name;
    if (!BakedFormat.IsWritable(name))
      return "has a prefab name a layer cannot hold";
    if (float.IsNaN(copy.X) || float.IsNaN(copy.Y) || float.IsNaN(copy.Z) || float.IsInfinity(copy.X) || float.IsInfinity(copy.Z) || Math.Abs(copy.Y) > 2e6f)
      return "is somewhere that cannot be written down";
    if (!ZoneKey.OfPoint(copy.X, copy.Z).InRange)
      return "is outside the zones a layer can hold";
    if (ScaleOf(copy, facts, out var scale) && !(InScale(scale.x) && InScale(scale.y) && InScale(scale.z)))
      return "has a scale a layer cannot hold";
    if (copy.Values.Length > ushort.MaxValue)
      return "has more values than a layer can hold";
    foreach (var v in copy.Values)
      if (v.Type == ZdoValueType.String && Encoding.UTF8.GetByteCount(v.Text) > MaxText)
        return "has a text value too long for a layer";
    return null;
  }

  // The scale a record can hold: 0.001 to 65.535 on each axis.
  private static bool InScale(float s) => s >= 0.001f && s <= 65.535f;

  // ------------------------------------------------------------------------------------------------ rotation

  // The nearest a pitch or roll may be to level for the record to keep only a yaw (10.5): 0.005 degrees.
  internal const double LevelDegrees = 0.005;

  // Whether a ZDO's pitch and roll are level. The ZDO stores Quaternion.eulerAngles, in which a level piece has no pitch and no roll
  // (within float error: 0.00001 or 359.99999).
  internal static bool IsLevel(float pitch, float roll) => NearZero(pitch) && NearZero(roll);

  private static bool NearZero(double degrees)
  {
    var a = Wrap360(degrees);
    return Math.Min(a, 360.0 - a) < LevelDegrees;
  }

  internal static double Wrap360(double degrees)
  {
    var a = degrees % 360.0;
    return a < 0 ? a + 360.0 : a;
  }

  // ------------------------------------------------------------------------------------------------ the game's ZDO

  // A ZDO, whole: every typed value but the game's session-only ones (a save would not keep those either), its flags and its
  // rotation as it stores it. ZDOExtraData.GetData hands out copies, so nothing here aliases the live object.
  internal static PieceCopy CopyOf(ZDO zdo, string prefabName)
  {
    ZDOExtraData.GetData(zdo.m_uid, out var floats, out var vec3s, out var quats, out var ints, out var longs, out var strings, out var bytes, out _);
    var session = ZDOExtraData.s_sessionOnly;
    var values = new List<ZdoValue>(floats.Count + vec3s.Count + quats.Count + ints.Count + longs.Count + strings.Count + bytes.Count);
    foreach (var p in floats)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfFloat(p.Key, p.Value));
    foreach (var p in vec3s)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfVec3(p.Key, p.Value.x, p.Value.y, p.Value.z));
    foreach (var p in quats)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfQuat(p.Key, p.Value.x, p.Value.y, p.Value.z, p.Value.w));
    foreach (var p in ints)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfInt(p.Key, p.Value));
    foreach (var p in longs)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfLong(p.Key, p.Value));
    foreach (var p in strings)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfString(p.Key, p.Value ?? ""));
    foreach (var p in bytes)
      if (!session.Contains(p.Key)) values.Add(ZdoValue.OfBytes(p.Key, p.Value ?? []));
    values.Sort(BakeValues.Compare);
    var position = zdo.GetPosition();
    var rotation = zdo.m_rotation;
    return new PieceCopy
    {
      Prefab = zdo.GetPrefab(),
      PrefabName = prefabName,
      X = position.x,
      Y = position.y,
      Z = position.z,
      RotX = rotation.x,
      RotY = rotation.y,
      RotZ = rotation.z,
      Persistent = zdo.Persistent,
      Distant = zdo.Distant,
      Type = (byte)zdo.Type,
      Values = [.. values],
      UserId = zdo.m_uid.UserID,
      Id = zdo.m_uid.ID,
    };
  }

  // The object again, on the machine that runs the world (it owns the new ZDO until the game hands it to a client, as for any
  // object it makes). The order is ZNetView.Awake's for a new object: flags, then the prefab, which also bumps the revision
  // and so marks the sector dirty for the save, then the values. The rotation is written before that bump so that the first
  // send already carries it.
  internal static ZDO Create(PieceCopy copy)
  {
    var zdo = ZDOMan.instance.CreateNewZDO(new Vector3(copy.X, copy.Y, copy.Z), copy.Prefab);
    zdo.Persistent = copy.Persistent;
    zdo.Distant = copy.Distant;
    zdo.Type = (ZDO.ObjectType)copy.Type;
    zdo.m_rotation = new Vector3(copy.RotX, copy.RotY, copy.RotZ);
    zdo.SetPrefab(copy.Prefab);
    Apply(zdo, copy.Values);
    return zdo;
  }

  // Values onto a ZDO, each by its own type.
  internal static void Apply(ZDO zdo, IEnumerable<ZdoValue> values)
  {
    foreach (var v in values)
    {
      switch (v.Type)
      {
        case ZdoValueType.Float: zdo.Set(v.Key, v.A); break;
        case ZdoValueType.Vec3: zdo.Set(v.Key, new Vector3(v.A, v.B, v.C)); break;
        case ZdoValueType.Quat: zdo.Set(v.Key, new Quaternion(v.A, v.B, v.C, v.D)); break;
        case ZdoValueType.Int: zdo.Set(v.Key, (int)v.Number); break;
        case ZdoValueType.Long: zdo.Set(v.Key, v.Number); break;
        case ZdoValueType.String: zdo.Set(v.Key, v.Text); break;
        default: zdo.Set(v.Key, v.Data); break;
      }
    }
  }
}

// The arithmetic of places and turns.
internal static class BakeMath
{
  // Unity's Quaternion.Euler(x, y, z): z first, then x, then y, so q = qy * qx * qz. In double precision.
  internal static (double X, double Y, double Z, double W) EulerToQuaternion(double xDeg, double yDeg, double zDeg)
  {
    const double Half = Math.PI / 360.0;
    double sx = Math.Sin(xDeg * Half), cx = Math.Cos(xDeg * Half);
    double sy = Math.Sin(yDeg * Half), cy = Math.Cos(yDeg * Half);
    double sz = Math.Sin(zDeg * Half), cz = Math.Cos(zDeg * Half);
    return (cy * sx * cz + sy * cx * sz,
      sy * cx * cz - cy * sx * sz,
      cy * cx * sz - sy * sx * cz,
      cy * cx * cz + sy * sx * sz);
  }

  // Unity's Quaternion.eulerAngles, in double precision and without the call into the engine: each angle in [0, 360), the inverse of
  // EulerToQuaternion. At a pitch of +-90 degrees the roll is taken as 0 and the yaw absorbs it, as Unity does.
  internal static (float X, float Y, float Z) EulerOf(Quaternion q)
  {
    double x = q.x, y = q.y, z = q.z, w = q.w;
    double length = Math.Sqrt(x * x + y * y + z * z + w * w);
    if (length < 1e-12)
      return (0f, 0f, 0f);
    x /= length;
    y /= length;
    z /= length;
    w /= length;
    // The rotation matrix of q = qy * qx * qz.
    double m12 = 2 * (y * z - w * x);
    double pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -m12)));
    double yaw, roll;
    if (Math.Abs(m12) < 0.999999)
    {
      yaw = Math.Atan2(2 * (x * z + w * y), 1 - 2 * (x * x + y * y));
      roll = Math.Atan2(2 * (x * y + w * z), 1 - 2 * (x * x + z * z));
    }
    else
    {
      yaw = Math.Atan2(-2 * (x * z - w * y), 1 - 2 * (y * y + z * z));
      roll = 0;
    }
    const double Deg = 180.0 / Math.PI;
    return ((float)BakeCapture.Wrap360(pitch * Deg), (float)BakeCapture.Wrap360(yaw * Deg), (float)BakeCapture.Wrap360(roll * Deg));
  }

  // The angle in degrees between two rotations.
  internal static double AngleBetween((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b)
  {
    var dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
    // Unit quaternions: the angle is 2 acos(|dot|); for tiny angles acos loses precision, so use the chord there.
    if (dot > 0.9999999)
    {
      var chord = Math.Sqrt(Math.Max(0.0, (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z) + (a.W - b.W) * (a.W - b.W)));
      var chordNeg = Math.Sqrt(Math.Max(0.0, (a.X + b.X) * (a.X + b.X) + (a.Y + b.Y) * (a.Y + b.Y) + (a.Z + b.Z) * (a.Z + b.Z) + (a.W + b.W) * (a.W + b.W)));
      // |a - b| = 2 sin(angle / 4) for unit quaternions.
      return 4.0 * Math.Asin(Math.Min(1.0, Math.Min(chord, chordNeg) / 2.0)) * 180.0 / Math.PI;
    }
    return 2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
  }

  // What the game's save does to a ZDO's rotation (ZDO.Save, ZPackage.WriteSmallRotation and ReadSmallRotation): angles are cut
  // to whole half degrees, and a rotation within half a degree of none is not written at all. A world that was saved and loaded
  // holds this, not what the piece had in memory.
  internal static (float X, float Y, float Z) RotationAfterSave(float x, float y, float z)
  {
    if ((int)Math.Abs(x * 2f) < 1 && (int)Math.Abs(y * 2f) < 1 && (int)Math.Abs(z * 2f) < 1)
      return (0f, 0f, 0f);
    uint nx = (uint)(x * 2f), ny = (uint)(y * 2f), nz = (uint)(z * 2f);
    if ((nx <= 1 || nx >= 719) && (nz <= 1 || nz >= 719))
      return (0f, (float)(ny & 0x7FFFu) * 0.5f, 0f);
    return ((nx & 0x3FFu) * 0.5f, (ny & 0x3FFu) * 0.5f, (nz & 0x3FFu) * 0.5f);
  }

  // The distance within which a ZDO is "the same place" as a journal's piece (10.8): 1 cm.
  internal const double PlaceMetres = 0.01;
  // And the angle: 0.1 degrees.
  internal const double PlaceDegrees = 0.1;

  // Whether a ZDO of this prefab, here, turned so, is the piece the copy describes. The game's save cuts rotations to half
  // degrees, so a piece that has been through a save and a load is compared as the game would have saved it too.
  internal static bool SamePlace(PieceCopy copy, int prefab, float x, float y, float z, float rx, float ry, float rz)
  {
    if (prefab != copy.Prefab)
      return false;
    double dx = x - copy.X, dy = y - copy.Y, dz = z - copy.Z;
    if (dx * dx + dy * dy + dz * dz > PlaceMetres * PlaceMetres)
      return false;
    var there = EulerToQuaternion(rx, ry, rz);
    if (AngleBetween(EulerToQuaternion(copy.RotX, copy.RotY, copy.RotZ), there) <= PlaceDegrees)
      return true;
    var saved = RotationAfterSave(copy.RotX, copy.RotY, copy.RotZ);
    return AngleBetween(EulerToQuaternion(saved.X, saved.Y, saved.Z), there) <= PlaceDegrees;
  }
}

// One object of the world as the matching sees it: where it stands, turned as its ZDO stores it.
internal readonly struct ObjectPose
{
  public ZDOID Id { get; init; }
  public int Prefab { get; init; }
  public float X { get; init; }
  public float Y { get; init; }
  public float Z { get; init; }
  public float RotX { get; init; }
  public float RotY { get; init; }
  public float RotZ { get; init; }
}

// Which object is which piece of an undo file (10.8): ZDOIDs change at every load, so an object is found by its prefab, its place within a
// centimetre and its turn within a tenth of a degree (BakeMath.SamePlace, which also takes the half degrees the game's save cuts a turn to).
internal static class BakeMatch
{
  // The cells objects are filed in: a cell is 2 cm, and a piece looks in the cell it is in and the eight around it, so that anything within
  // 1 cm is found whichever side of a cell's edge it is on.
  private const double Cell = 0.02;

  // For each piece, the index in `objects` of the object that is it now, or -1; each object answers for one piece at most.
  internal static int[] Find(IReadOnlyList<ObjectPose> objects, IReadOnlyList<PieceCopy> pieces)
  {
    var found = new int[pieces.Count];
    for (int i = 0; i < found.Length; i++)
      found[i] = -1;
    if (objects.Count == 0 || pieces.Count == 0)
      return found;
    var cells = new Dictionary<(int Prefab, long X, long Z), List<int>>();
    for (int i = 0; i < objects.Count; i++)
    {
      var o = objects[i];
      var key = (o.Prefab, CellOf(o.X), CellOf(o.Z));
      if (!cells.TryGetValue(key, out var list))
        cells[key] = list = [];
      list.Add(i);
    }
    var taken = new bool[objects.Count];
    for (int p = 0; p < pieces.Count; p++)
    {
      var piece = pieces[p];
      long cx = CellOf(piece.X), cz = CellOf(piece.Z);
      for (long dx = -1; dx <= 1 && found[p] < 0; dx++)
        for (long dz = -1; dz <= 1 && found[p] < 0; dz++)
        {
          if (!cells.TryGetValue((piece.Prefab, cx + dx, cz + dz), out var list))
            continue;
          foreach (int i in list)
          {
            var o = objects[i];
            if (taken[i] || !BakeMath.SamePlace(piece, o.Prefab, o.X, o.Y, o.Z, o.RotX, o.RotY, o.RotZ))
              continue;
            taken[i] = true;
            found[p] = i;
            break;
          }
        }
    }
    return found;
  }

  private static long CellOf(double metres) => (long)Math.Floor(metres / Cell);
}

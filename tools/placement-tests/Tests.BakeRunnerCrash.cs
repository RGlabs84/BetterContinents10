// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The crash proof of the in-game operations: an operation is run once with every mutation recorded (the objects, the layer and the undo files
// as they were after each), and then, for every mutation it could have been stopped at, for every pair of moments the disk can hold the world's
// objects and the world's settings from (a save writes the objects first and the settings last, a cut save mixes a new one with an old one, a
// whole save is one moment), the game is "restarted": new ZDOIDs, rotations cut to half degrees as the game's save cuts them, the layer read
// again from its bytes, the undo files as they were at the stop. The check at world load then runs, and every piece must be in exactly one place
// after it, as an object or as a record, whichever the operation's end, and a second check must change nothing.
//
// A real stop (an exception at mutation k, through the runner's own Guard) is run for each k as well, and must leave exactly what the recording
// says: the proof settles the recorded states, and this shows that the runner can leave no others.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private sealed class BakeCase
  {
    public string Name = "";
    // A fixture before the operation (a recording one, in a folder of its own).
    public Func<string, BakeFx> Start = null!;
    // Runs the operation to its end, or to the stop the fixture's clock makes.
    public Action<BakeFx> Operate = null!;
    // After the check: null, or what is wrong. `reference` is the layer the whole operation made when nothing stopped it.
    public Func<BakeFx, BakedLayer, string> Verify = null!;
  }

  private static BakeFx BakeReload(BakeFx proto, BakeMoment journal, BakeMoment layer, BakeMoment objects, string folderName, long session)
  {
    var fx = new BakeFx { Folder = Path.Combine(Work, "reload-" + folderName) };
    if (Directory.Exists(fx.Folder))
      Directory.Delete(fx.Folder, true);
    Directory.CreateDirectory(fx.Folder);
    foreach (var file in journal.Files)
      File.WriteAllBytes(Path.Combine(fx.Folder, file.Key), file.Value);
    fx.World = new BakeFakeWorld(fx.Clock, session);
    foreach (var facts in proto.World.Facts)
      fx.World.Facts[facts.Key] = facts.Value;
    foreach (var look in proto.World.Looks)
      fx.World.Looks[look.Key] = look.Value;
    // What a save and a load make of an object: a new ZDOID, and the rotation cut to half degrees.
    foreach (var saved in objects.Objects)
    {
      var piece = BakeClone(saved);
      var cut = BakeMath.RotationAfterSave(saved.RotX, saved.RotY, saved.RotZ);
      piece.RotX = cut.X;
      piece.RotY = cut.Y;
      piece.RotZ = cut.Z;
      fx.World.Add(piece);
    }
    // The settings are read again from their bytes.
    fx.Layer = layer.Layer == null ? null : BakedLayer.Read(layer.Layer.Bytes, layer.Layer.Length);
    fx.Creator = proto.Creator;
    BakeWire(fx);
    return fx;
  }

  private static bool BakeSameWorld(BakeMoment a, BakeMoment b)
  {
    if (!ReferenceEquals(a.Layer, b.Layer) || a.Objects.Count != b.Objects.Count || a.Files.Count != b.Files.Count)
      return false;
    for (int i = 0; i < a.Objects.Count; i++)
    {
      var x = a.Objects[i];
      var y = b.Objects[i];
      if (x.Prefab != y.Prefab || x.X != y.X || x.Y != y.Y || x.Z != y.Z || x.RotX != y.RotX || x.RotY != y.RotY || x.RotZ != y.RotZ || !x.Values.SequenceEqual(y.Values))
        return false;
    }
    return a.Files.All(f => b.Files.TryGetValue(f.Key, out var other) && f.Value.SequenceEqual(other));
  }

  // The same layer (by its bytes), the same objects and the same undo files, as two separate runs would leave them.
  private static bool BakeSameState(BakeMoment a, BakeMoment b)
  {
    if ((a.Layer == null) != (b.Layer == null) || a.Objects.Count != b.Objects.Count || a.Files.Count != b.Files.Count)
      return false;
    if (a.Layer != null && !a.Layer.Bytes.Take(a.Layer.Length).SequenceEqual(b.Layer.Bytes.Take(b.Layer.Length)))
      return false;
    for (int i = 0; i < a.Objects.Count; i++)
    {
      var x = a.Objects[i];
      var y = b.Objects[i];
      if (x.Prefab != y.Prefab || x.X != y.X || x.Y != y.Y || x.Z != y.Z || x.RotX != y.RotX || x.RotY != y.RotY || x.RotZ != y.RotZ || !x.Values.SequenceEqual(y.Values))
        return false;
    }
    return a.Files.All(f => b.Files.TryGetValue(f.Key, out var other) && f.Value.SequenceEqual(other));
  }

  private static int BakeTrialsDone;

  // The proof for one operation. Returns the number of states settled.
  private static int BakeProve(BakeCase test, out List<string> problems)
  {
    problems = [];
    var recording = test.Start("rec-" + test.Name);
    test.Operate(recording);
    int mutations = recording.Clock.Count;
    var moments = recording.Moments;
    if (moments.Count != mutations + 1)
    {
      problems.Add($"{test.Name}: the recording holds {moments.Count} moments for {mutations} mutations");
      return 0;
    }
    int trials = 0;
    for (int k = 0; k <= mutations; k++)
      for (int a = 0; a <= k; a++)
        for (int b = 0; b <= k; b++)
        {
          var fx = BakeReload(recording, moments[k], moments[a], moments[b], test.Name, 9000 + trials);
          trials++;
          var at = $"{test.Name}: stopped after {k} of {mutations} mutations ({moments[k].What}), layer of moment {a} ({moments[a].What}), objects of moment {b} ({moments[b].What})";
          try
          {
            var result = BakeRunner.CheckPending(fx.Ctx);
            var after = fx.Snap("after the check");
            var wrong = test.Verify(fx, moments[mutations].Layer);
            if (wrong != null)
            {
              problems.Add(at + " | " + wrong + " | " + string.Join(" | ", fx.Said));
              continue;
            }
            if (result.Failed > 0)
            {
              problems.Add(at + " | the check left an operation unsettled | " + string.Join(" | ", fx.Said));
              continue;
            }
            // Whatever the first check settled, a second changes nothing.
            int said = fx.Said.Count;
            BakeRunner.CheckPending(fx.Ctx);
            var again = fx.Snap("after the second check");
            if (!BakeSameWorld(after, again) || fx.Said.Count != said)
              problems.Add(at + " | a second check changed something: " + string.Join(" | ", fx.Said.Skip(said)));
          }
          catch (Exception e)
          {
            problems.Add(at + " | threw " + e.GetType().Name + ": " + e.Message);
          }
        }
    BakeTrialsDone += trials;

    // A real stop at each mutation leaves what was recorded.
    for (int k = 0; k < mutations; k++)
    {
      BakeRunner.Reset();
      var stopped = test.Start("stop-" + test.Name);
      stopped.Recording = false;
      stopped.Clock.Limit = k;
      stopped.Clock.Applied = null;
      test.Operate(stopped);
      var end = stopped.Snap("stopped");
      bool same = BakeSameState(end, moments[k]);
      if (stopped.Clock.Count != k || !same)
        problems.Add($"{test.Name}: stopped at mutation {k} (the next: {moments[k + 1].What}) the runner left {stopped.Clock.Count} mutations done, and "
          + (same ? "the recorded state" : "a state that is not the recorded one") + " | " + string.Join(" | ", stopped.Said));
      else if (!stopped.Said.Any(l => l.Contains("stopped on an error")))
        problems.Add($"{test.Name}: stopped at mutation {k}: the runner did not say that it stopped | " + string.Join(" | ", stopped.Said));
      BakeRunner.Reset();
    }
    return trials;
  }

  // ------------------------------------------------------------------------------------------------ the bake cases

  // One bake of a case: its number, the pieces it takes (they become records) and the town pieces it adopts.
  private sealed class BakeOpSpec
  {
    public int Number;
    public List<PieceCopy> Pieces = [];
    public List<PieceCopy> Adopted = [];
  }

  // A record, apart from the number of its palette entry: what it is.
  private static string BakeRecordKey(BakeRecord r) =>
    $"{r.Prefab}|{r.Role}|{(int)r.Entry.Flags}|{r.Entry.Collision}|{string.Join(",", r.Entry.Tags.Select(t => t.ToString()))}|{r.Record.WorldX:0.0000}|{r.Record.WorldY:0.0000}|{r.Record.WorldZ:0.0000}"
    + $"|{(int)r.Record.Flags}|{r.Record.Yaw}|{r.Record.Packed.Largest},{r.Record.Packed.A},{r.Record.Packed.B},{r.Record.Packed.C}|{r.Record.ScaleX},{r.Record.ScaleY},{r.Record.ScaleZ}"
    + $"|{r.Record.Id}|{r.Record.Source}|{r.Record.ValueSet}|{r.Record.Seed}|{string.Join(",", r.Values.Values.Select(v => v.Describe()))}";

  private static List<string> BakeKeysOfSource(IBakeLayerPort port, int source) =>
    port.RecordsIn(port.ZonesWithRecords(), r => r.HasSource && r.Source == source).Select(BakeRecordKey).OrderBy(k => k, StringComparer.Ordinal).ToList();

  private static bool BakeSameOperation(OperationInfo a, OperationInfo b) =>
    a.Number == b.Number && a.Kind == b.Kind && a.State == b.State && a.Time == b.Time && a.Who == b.Who && a.X1 == b.X1 && a.Z1 == b.Z1 && a.X2 == b.X2 && a.Z2 == b.Z2 && a.Radius == b.Radius
    && a.Added == b.Added && a.Removed == b.Removed && a.Adopted == b.Adopted && a.Version == b.Version && a.ValueSets.Length == b.ValueSets.Length
    && a.ValueSets.Zip(b.ValueSets).All(p => p.First.Equals(p.Second));

  // The places of the pieces after the check has settled bakes: for each bake, all of its pieces as records (settled) or all as objects
  // (abandoned), its town pieces tagged or free, and the records and registry entry it made the same as an uncrashed run made them.
  private static string BakeVerifyBakes(BakeFx fx, BakedLayer reference, int priorRecords, params BakeOpSpec[] ops)
  {
    var journal = new BakeJournal(fx.Folder);
    var records = BakeRecordsOf(fx);
    var refPort = new LayerPort(() => reference, (_, _) => { });
    int settledPieces = 0;
    foreach (var spec in ops)
    {
      int n = spec.Number;
      bool inLayer = fx.Port.TryGetOperation(n, out var entry) && entry.State == OperationState.InLayer;
      var state = journal.GetState(n);
      bool exists = journal.Numbers().Contains(n);
      if (!exists)
      {
        // The stop came before the undo file: nothing was changed.
        if (inLayer || spec.Pieces.Any(p => !BakeStands(fx, p)))
          return $"bake {n}: no undo file, and the world is not as it was";
        continue;
      }
      if (state == null || !state.Value.Final())
        return $"bake {n} is {(state?.ToString() ?? "stateless")}, not settled";
      int baked = records.Count(r => r.Record.HasSource && r.Record.Source == n);
      if (state == BakeState.Settled)
      {
        if (!inLayer)
          return $"bake {n} is settled, and the layer does not list it";
        foreach (var p in spec.Pieces)
        {
          bool object_ = BakeStands(fx, p), record = records.Any(r => BakeIsRecord(r, p) && r.Record.Source == n);
          if (object_ || !record)
            return $"bake {n} is settled, and the {p.PrefabName} at {p.X}, {p.Z} is {(object_ ? "still an object" : "")}{(!record ? " no record" : "")}";
        }
        foreach (var p in spec.Adopted)
        {
          var piece = fx.World.Objects.Values.FirstOrDefault(o => o.Prefab == p.Prefab && Math.Abs(o.X - p.X) < 0.01 && Math.Abs(o.Z - p.Z) < 0.01);
          if (piece == null || !BakeHasKeys(piece))
            return $"bake {n} is settled, and the town piece {p.PrefabName} is {(piece == null ? "gone" : "without all four keys")}";
          if (!records.Any(r => r.Role == BakedRole.Live && r.Record.Source == n && BakeIsRecord(r, p)))
            return $"bake {n} is settled, and the town piece {p.PrefabName} has no Live record";
        }
        if (baked != spec.Pieces.Count + spec.Adopted.Count)
          return $"bake {n} is settled, and the layer holds {baked} records of it for {spec.Pieces.Count + spec.Adopted.Count} pieces";
        settledPieces += baked;
        // The records are what an uncrashed bake made, and so is the registry's entry.
        if (!BakeKeysOfSource(fx.Port, n).SequenceEqual(BakeKeysOfSource(refPort, n)))
          return $"bake {n} is settled, and its records are not the ones an uncrashed bake makes";
        if (!refPort.TryGetOperation(n, out var want) || !BakeSameOperation(entry, want))
          return $"bake {n} is settled, and its registry entry is not the one an uncrashed bake makes";
      }
      else if (state == BakeState.Abandoned)
      {
        if (inLayer || baked != 0)
          return $"bake {n} is abandoned, and the layer still lists it or holds its records";
        foreach (var p in spec.Pieces)
          if (!BakeStands(fx, p))
            return $"bake {n} is abandoned, and the {p.PrefabName} at {p.X}, {p.Z} is gone";
        foreach (var p in spec.Adopted)
        {
          var piece = fx.World.Objects.Values.FirstOrDefault(o => o.Prefab == p.Prefab && Math.Abs(o.X - p.X) < 0.01 && Math.Abs(o.Z - p.Z) < 0.01);
          if (piece == null || BakeHasAnyKey(piece))
            return $"bake {n} is abandoned, and the town piece {p.PrefabName} is {(piece == null ? "gone" : "still keyed")}";
        }
      }
      else
        return $"bake {n} ends {state}";
    }
    if (records.Count != priorRecords + settledPieces)
      return $"the layer holds {records.Count} records, and {priorRecords} were there before and {settledPieces} were settled";
    // What no bake takes stays.
    if (BakeCountObjects(fx, "wood_door") != 1 || BakeCountObjects(fx, "piece_chest") != 1 || BakeCountObjects(fx, "Pine_tree") != 1)
      return "a piece that no bake takes is gone";
    return null;
  }

  private static BakeCase BakeCaseBake(string name, string words, bool town)
  {
    List<PieceCopy> taken = null, adopted = null;
    var test = new BakeCase { Name = name };
    test.Start = folder =>
    {
      BakeRunner.Reset();
      var fx = BakeNewFx(folder, BakeStandardWorld, out taken, recording: true);
      adopted = town ? fx.World.Objects.Values.Where(o => o.PrefabName is "wood_door" or "piece_chest").Select(BakeClone).ToList() : [];
      return fx;
    };
    test.Operate = fx => BakeRun(fx, BakeStandardArea(), words);
    test.Verify = (fx, reference) => BakeVerifyBakes(fx, reference, 0, new BakeOpSpec { Number = 1, Pieces = taken, Adopted = adopted });
    return test;
  }

  // A second house, three zones east of the first (zone 3), for a second bake.
  private static List<PieceCopy> BakeSecondHouse(BakeFakeWorld world)
  {
    var house = new List<PieceCopy>
    {
      BakeObject("stone_wall_2x1", 130f, 10f, 5f, 0f),
      BakeObject("stone_wall_2x1", 132f, 10f, 5f, 90f, 1003),
      BakeObject("wood_floor", 134f, 10f, 5f, 0f, 1003, 0f, 0f, ZdoValue.OfInt(BakeKey("MatVar0"), 1), ZdoValue.OfFloat(BakeKey("health"), 60f)),
      BakeObject("wood_beam", 136f, 11f, 6f, 10f, 1001, 0f, 0f, ZdoValue.OfVec3(BakeKey("scale"), 1f, 3f, 1f)),
    };
    foreach (var piece in house)
      world.Add(piece);
    return house;
  }

  private static BakeArea BakeSecondArea() => BakeArea.OfCircle(133f, 5f, 12f);

  // ------------------------------------------------------------------------------------------------ the other operations

  private static bool BakeSameValues(PieceCopy a, PieceCopy b) => a.Values.SequenceEqual(b.Values);

  // The world after a check, whatever operation was stopped: no operation left unsettled, every piece in exactly one place (an object, or a
  // record of the layer; or, after a drop, in neither only if the whole drop happened), all the pieces in the same place (an operation is all or
  // nothing for what it covers), every piece that is an object with the values it had, every town piece with its keys exactly when the layer has
  // its Live record, and nothing else in the layer or the world.
  private static string BakeVerifyWorld(BakeFx fx, List<string> bakeKeys, List<PieceCopy> pieces, List<PieceCopy> town, int baseObjects, bool dropped = false)
  {
    var journal = new BakeJournal(fx.Folder);
    foreach (int n in journal.Pending())
      return $"operation {n} is {(journal.GetState(n)?.ToString() ?? "stateless")}, not settled";
    var records = BakeRecordsOf(fx);
    var layerKeys = records.Select(BakeRecordKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
    int asRecords = 0, asObjects = 0;
    foreach (var p in pieces)
    {
      // The game's save cuts an angle to the half degree below it, and a piece an unbake made has the float noise of its angles, so a piece
      // that went through a save and a load is within a half degree of the one it was (BakeMath.SamePlace is for the pieces of an undo file,
      // which carry their own angles).
      var stand = fx.World.Objects.Values.Where(o => o.Prefab == p.Prefab && Math.Abs(o.X - p.X) < 0.01 && Math.Abs(o.Y - p.Y) < 0.01 && Math.Abs(o.Z - p.Z) < 0.01
        && BakeMath.AngleBetween(BakeMath.EulerToQuaternion(o.RotX, o.RotY, o.RotZ), BakeMath.EulerToQuaternion(p.RotX, p.RotY, p.RotZ)) <= 0.6).ToList();
      int record = records.Count(r => BakeIsRecord(r, p) && r.Role != BakedRole.Live);
      if (stand.Count + record > 1)
        return $"the {p.PrefabName} at {p.X}, {p.Z} is in {stand.Count} places as an object and {record} as a record";
      if (stand.Count + record == 0)
      {
        if (!dropped)
          return $"the {p.PrefabName} at {p.X}, {p.Z} is nowhere";
        continue;
      }
      if (stand.Count == 1 && !BakeSameValues(stand[0], p))
        return $"the {p.PrefabName} at {p.X}, {p.Z} stands with other values ({BakeDescribeValues(stand[0])} for {BakeDescribeValues(p)})";
      asObjects += stand.Count;
      asRecords += record;
    }
    if (asObjects > 0 && asRecords > 0)
      return $"{asObjects} pieces are objects and {asRecords} are records: an operation is all or nothing";
    if (dropped && asObjects > 0)
      return "a drop made pieces";
    int liveRecords = 0;
    foreach (var p in town)
    {
      var stand = fx.World.Objects.Values.Where(o => o.Prefab == p.Prefab && Math.Abs(o.X - p.X) < 0.01 && Math.Abs(o.Z - p.Z) < 0.01).ToList();
      if (stand.Count != 1)
        return $"the town piece {p.PrefabName} stands {stand.Count} times";
      bool keyed = BakeHasKeys(stand[0]);
      if (BakeHasAnyKey(stand[0]) && !keyed)
        return $"the town piece {p.PrefabName} has some of its keys only";
      bool live = records.Any(r => r.Role == BakedRole.Live && BakeIsRecord(r, p));
      if (!dropped && keyed != live)
        return $"the town piece {p.PrefabName} is {(keyed ? "keyed" : "free")} and the layer {(live ? "holds" : "lacks")} its Live record";
      if (live)
        liveRecords++;
    }
    if (records.Count != asRecords + liveRecords)
      return $"the layer holds {records.Count} records for {asRecords} pieces and {liveRecords} town pieces";
    if (records.Count > 0 && bakeKeys != null && !layerKeys.SequenceEqual(bakeKeys))
      return "the records in the layer are not the ones the bake made";
    if (fx.World.Objects.Count != baseObjects + asObjects)
      return $"the world holds {fx.World.Objects.Count} objects, not {baseObjects + asObjects}";
    return null;
  }

  // Finishes what an earlier operation of a fixture left to the next load, as that load would, and starts recording.
  private static void BakeSettleAndRecord(BakeFx fx)
  {
    var plain = new BakeJournal(fx.Folder);
    foreach (int n in plain.Pending())
      plain.SetState(n, fx.Port.TryGetOperation(n, out var op) && op.State == OperationState.Undone ? BakeState.Undone : BakeState.Settled);
    fx.Clock.Count = 0;
    fx.Clock.Events.Clear();
    fx.Said.Clear();
    BakeRunner.Reset();
    fx.Recording = true;
    fx.Clock.Applied = what => fx.Moments.Add(fx.Snap(what));
    fx.Moments.Add(fx.Snap("start"));
  }

  // A case that begins after earlier operations, each settled: `prior` runs them, `operate` is the one that is stopped.
  private static BakeCase BakeCaseAfter(string name, bool town, Action<BakeFx> prior, Action<BakeFx> operate, bool dropped = false, bool sameRecords = true)
  {
    List<PieceCopy> taken = null, adopted = null;
    List<string> bakeKeys = null;
    int baseObjects = 0;
    var test = new BakeCase { Name = name };
    test.Start = folder =>
    {
      BakeRunner.Reset();
      var fx = BakeNewFx(folder, BakeStandardWorld, out taken);
      adopted = fx.World.Objects.Values.Where(o => o.PrefabName is "wood_door" or "piece_chest").Select(BakeClone).ToList();
      baseObjects = fx.World.Objects.Count - taken.Count;
      prior(fx);
      bakeKeys = fx.Layer == null ? null : BakeKeysOfSource(fx.Port, 1);
      if (bakeKeys is { Count: 0 })
        bakeKeys = null;
      BakeSettleAndRecord(fx);
      return fx;
    };
    test.Operate = operate;
    test.Verify = (fx, reference) => BakeVerifyWorld(fx, sameRecords ? bakeKeys : null, taken, town ? adopted : [], baseObjects, dropped);
    return test;
  }

  private static void BakeCrashOperationsTest()
  {
    Section("crash proof: unbake, undo and drop stopped at every mutation, settled by every pair of saved moments");
    BakeRunner.Reset();
    int total = 0;
    foreach (bool town in new[] { false, true })
    {
      var words = town ? "town confirm" : "confirm";
      string suffix = town ? "-town" : "";
      var cases = new List<BakeCase>
      {
        BakeCaseAfter("unbake" + suffix, town, fx => BakeRun(fx, BakeStandardArea(), words), fx => BakeUnbakeRun(fx, BakeWholeWorld)),
        BakeCaseAfter("undo-bake" + suffix, town, fx => BakeRun(fx, BakeStandardArea(), words), fx => BakeUndoRun(fx, 1)),
        BakeCaseAfter("undo-unbake" + suffix, town, fx =>
        {
          BakeRun(fx, BakeStandardArea(), words);
          BakeUnbakeRun(fx, BakeWholeWorld);
        }, fx => BakeUndoRun(fx, 2)),
        BakeCaseAfter("drop" + suffix, town, fx => BakeRun(fx, BakeStandardArea(), words), fx => BakeDropRun(fx, new DropScope { Bake = 1 }), dropped: true),
        BakeCaseAfter("undo-drop" + suffix, town, fx =>
        {
          BakeRun(fx, BakeStandardArea(), words);
          BakeDropRun(fx, new DropScope { Bake = 1 });
        }, fx => BakeUndoRun(fx, 2), dropped: true),
      };
      foreach (var test in cases)
      {
        int trials = BakeProve(test, out var problems);
        total += trials;
        C(problems.Count == 0, $"{test.Name}: {trials} stops and pairs of saved moments, each settled with every piece in one place" + (problems.Count > 0 ? $" ({problems.Count} wrong; first: " + string.Join(" || ", problems.Take(3)) + ")" : ""));
      }
    }
    // An unbake and a bake of the same pieces again in one run (the workflow of editing a town): the unbake still unsettled when the bake starts.
    foreach (bool town in new[] { false, true })
    {
      var words = town ? "town confirm" : "confirm";
      var test = BakeCaseAfter("unbake-then-bake" + (town ? "-town" : ""), town, fx => BakeRun(fx, BakeStandardArea(), words), fx =>
      {
        BakeUnbakeRun(fx, BakeWholeWorld);
        BakeRun(fx, BakeStandardArea(), words);
      }, sameRecords: false);
      int trials = BakeProve(test, out var problems);
      total += trials;
      C(problems.Count == 0, $"{test.Name}: {trials} stops and pairs of saved moments, each settled with every piece in one place" + (problems.Count > 0 ? $" ({problems.Count} wrong; first: " + string.Join(" || ", problems.Take(3)) + ")" : ""));
    }
    // A bake and an unbake of it in one run, the bake still unsettled when the unbake starts.
    foreach (bool town in new[] { false, true })
    {
      var words = town ? "town confirm" : "confirm";
      var test = BakeCaseAfter("bake-then-unbake" + (town ? "-town" : ""), town, fx => { }, fx =>
      {
        BakeRun(fx, BakeStandardArea(), words);
        BakeUnbakeRun(fx, BakeWholeWorld);
      });
      int trials = BakeProve(test, out var problems);
      total += trials;
      C(problems.Count == 0, $"{test.Name}: {trials} stops and pairs of saved moments, each settled with every piece in one place" + (problems.Count > 0 ? $" ({problems.Count} wrong; first: " + string.Join(" || ", problems.Take(3)) + ")" : ""));
    }
    System.Console.WriteLine($"   {total} states settled in all");
  }

  private static void BakeCrashBakeTest()
  {
    Section("crash proof: a bake stopped at every mutation, settled by every pair of saved moments");
    BakeRunner.Reset();
    int total = 0;
    var cases = new List<BakeCase> { BakeCaseBake("bake", "confirm", false), BakeCaseBake("bake-town", "town confirm", true) };

    // Two bakes in a row, the first still unsettled when the second starts: each stop and each pair of saved moments is settled for both.
    List<PieceCopy> first = null, second = null;
    var two = new BakeCase { Name = "two-bakes" };
    two.Start = folder =>
    {
      BakeRunner.Reset();
      var fx = BakeNewFx(folder, world =>
      {
        var one = BakeStandardWorld(world);
        second = BakeSecondHouse(world);
        return one;
      }, out first, recording: true);
      return fx;
    };
    two.Operate = fx =>
    {
      BakeRun(fx, BakeStandardArea());
      BakeRun(fx, BakeSecondArea());
    };
    two.Verify = (fx, reference) => BakeVerifyBakes(fx, reference, 0, new BakeOpSpec { Number = 1, Pieces = first }, new BakeOpSpec { Number = 2, Pieces = second });
    cases.Add(two);

    // A bake on a compiler's layer: the registry appears with it.
    List<PieceCopy> onFile = null;
    BakedLayer compiler = null;
    var onLayer = new BakeCase { Name = "bake-on-a-compiler-layer" };
    onLayer.Start = folder =>
    {
      BakeRunner.Reset();
      compiler = CompilerFile([Wall(), Door(), Roof()], 0);
      return BakeNewFx(folder, BakeStandardWorld, out onFile, compiler, recording: true);
    };
    onLayer.Operate = fx => BakeRun(fx, BakeStandardArea(), "confirm");
    onLayer.Verify = (fx, reference) => BakeVerifyBakes(fx, reference, (int)compiler.Placements, new BakeOpSpec { Number = 1, Pieces = onFile });
    cases.Add(onLayer);

    foreach (var test in cases)
    {
      int trials = BakeProve(test, out var problems);
      total += trials;
      C(problems.Count == 0, $"{test.Name}: {trials} stops and pairs of saved moments, each settled with every piece in one place" + (problems.Count > 0 ? $" ({problems.Count} wrong; first: " + string.Join(" || ", problems.Take(4)) + ")" : ""));
    }
    System.Console.WriteLine($"   {total} states settled in all");
  }
}

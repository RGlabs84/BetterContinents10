// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// Where an unbake looks: an area, or the whole world (every zone that has records: the way out of 4.5).
internal sealed class UnbakeScope
{
  public BakeArea? Area { get; init; }
  public bool World => Area == null;
}

// What an unbake's read of the layer found.
internal sealed class UnbakeGather
{
  // The records that come out of the layer.
  public List<BakeRecord> Records { get; } = [];
  // The pieces that are made, whole.
  public List<PieceCopy> Created { get; } = [];
  // The Live pieces that lose their bake keys.
  public List<AdoptedPiece> Freed { get; } = [];
  public Dictionary<string, int> Kinds { get; } = [];
  public int InGame { get; set; }
  public int Compiler { get; set; }
  public int LiveWithoutPiece { get; set; }
  // Records that stay: their prefab is not in this game, or it cannot be made as a piece.
  public int Stay { get; set; }
  public Dictionary<string, int> StayKinds { get; } = [];
  // Records of a consumable kind (spec 0.2): the game placed them as its own vegetation when the zone generated, and they stay in the layer.
  public int Consumables { get; set; }
  // Records of a consumable kind that the compiler marked decor (palette flag 32): drawn as scenery, never an object, so there is no piece to make of one;
  // they stay in the layer.
  public int DecorConsumables { get; set; }
  // Objects in the zones of an area (not asked for a whole world).
  public long Objects { get; set; }
  public List<WorldObject> ObjectsScratch { get; } = [];
  public bool Closed { get; set; }
}

internal static partial class BakeRunner
{
  // An unbake that makes more real objects than this says so in its dry run.
  internal const int UnbakeWarnObjects = 50_000;

  // The piece a record becomes (10.10): its prefab, its place (the record's point less the anchor, turned and scaled with the piece), its
  // turn and scale, and every value it had. A record of an in-game bake gets back what it held: the tags of its entry (MatVar), its value set,
  // its seed. A compiler's record has no value set: its creator is the admin who unbakes, so zone resets keep it, and its health is full. A look the
  // record does not name comes from its place, as the layer's drawing and the server's seeding derive it (BakedFormat.VariantUnit), so that the
  // piece keeps the look it was drawn with. Null, and why, when this game cannot make the piece.
  internal static PieceCopy? PieceOf(BakeContext ctx, BakeRecord rec, long creator, out string? problem)
  {
    problem = null;
    var world = ctx.World;
    var entry = rec.Entry;
    Candidate? chosen = null;
    int hash = 0;
    foreach (var candidate in entry.Candidates)
    {
      hash = world.PrefabOf(candidate.Name);
      if (hash != 0)
      {
        chosen = candidate;
        break;
      }
    }
    if (chosen is not { } candidateUsed)
    {
      problem = $"no prefab of {string.Join(", ", entry.Candidates.Select(c => c.Name))} is in this game";
      return null;
    }
    var facts = world.FactsOf(hash);
    if (facts != null && BakeClassifier.ConsumableComponent(facts) != null)
    {
      problem = BakeClassifier.Consumables;
      return null;
    }
    if (facts == null || !facts.Has("ZNetView"))
    {
      problem = $"{candidateUsed.Name} has no ZNetView, so it cannot be an object";
      return null;
    }
    var record = rec.Record;
    var rotation = record.Rotation;
    var scale = record.HasScale ? record.Scale : Vector3.one;
    bool scaled = scale != Vector3.one && facts.SyncsScale;
    if (!scaled)
      scale = Vector3.one;
    var pivot = Pivot(record.Position, rotation, scale, candidateUsed.Anchor);
    var euler = world.EulerOf(rotation);
    var flags = world.FlagsOf(hash);

    var values = new Dictionary<(int Key, ZdoValueType Type), ZdoValue>();
    void Put(ZdoValue value) => values[(value.Key, value.Type)] = value;
    foreach (var tag in entry.Tags)
    {
      int key = tag.Key.GetStableHashCode();
      Put(tag.Type switch
      {
        TagType.Bool or TagType.Int => ZdoValue.OfInt(key, tag.Number),
        TagType.Float => ZdoValue.OfFloat(key, tag.Float),
        _ => ZdoValue.OfString(key, tag.Text),
      });
    }
    foreach (var value in rec.Values.Values)
      Put(value);
    var look = world.LookOf(hash);
    if (look.Variations.Length > 0 || look.RandomValues)
    {
      uint lookHash = record.LookHash;
      foreach (var variation in look.Variations)
        if (!entry.TryGetTag(BakedKeys.MatVarKey(variation.Key), out _) && !values.ContainsKey((BakedKeys.MatVar(variation.Key), ZdoValueType.Int)))
          Put(ZdoValue.OfInt(BakedKeys.MatVar(variation.Key), DerivedVariant(BakedFormat.VariantUnit(lookHash, variation.Key), variation.Value)));
      if (look.RandomValues && !entry.TryGetTag(BakedFormat.KeySeed, out _) && !values.ContainsKey((BakedKeys.Seed, ZdoValueType.Int)))
        Put(ZdoValue.OfInt(BakedKeys.Seed, record.EffectiveSeed));
    }
    if (scaled)
      Put(ZdoValue.OfVec3(BakeCapture.ScaleKey, scale.x, scale.y, scale.z));
    if (creator != 0 && !values.ContainsKey((BakeCapture.CreatorKey, ZdoValueType.Long)))
      Put(ZdoValue.OfLong(BakeCapture.CreatorKey, creator));
    var list = values.Values.ToList();
    list.Sort(BakeValues.Compare);
    return new PieceCopy
    {
      Prefab = hash,
      PrefabName = candidateUsed.Name,
      X = pivot.x,
      Y = pivot.y,
      Z = pivot.z,
      RotX = euler.X,
      RotY = euler.Y,
      RotZ = euler.Z,
      Persistent = flags.Persistent,
      Distant = flags.Distant,
      Type = flags.Type,
      Values = [.. list],
    };
  }

  // Where a piece's pivot goes: the record's point less the anchor, turned and scaled with the piece.
  internal static Vector3 Pivot(Vector3 point, Quaternion rotation, Vector3 scale, Vector3 anchor) => point - rotation * Vector3.Scale(scale, anchor);

  // The first variant whose running weight passes u times the total weight (spec 2.5): the same answer the server's seeding gives.
  internal static int DerivedVariant(double u, IReadOnlyList<float> weights)
  {
    double total = 0;
    foreach (var weight in weights)
      total += weight;
    double target = u * total, running = 0;
    for (int i = 0; i < weights.Count; i++)
    {
      running += weights[i];
      if (running > target)
        return i;
    }
    return 0;
  }

  // ------------------------------------------------------------------------------------------------ unbake

  // `bc_bake unbake <radius> [at <x> <z>] | box ... | world [all]` (10.10, 4.5): without confirm a dry run; with it, the records in the scope
  // become real pieces. The pieces are made first, the players have them, then the records come out of the layer and the players have that.
  internal static IEnumerator Unbake(BakeContext ctx, UnbakeScope scope, BakeWords words, string echo)
  {
    var say = ctx.Say;
    var world = ctx.World;
    var layer = ctx.Layer;
    if (scope.Area?.Problem() is { } problem)
    {
      say($"bc_bake: {problem}.");
      yield break;
    }
    if (!world.Ready)
    {
      say("bc_bake: load a world first.");
      yield break;
    }
    if (!layer.HasLayer || layer.Records == 0)
    {
      say("bc_bake: this world's layer holds no records, so there is nothing to unbake.");
      yield break;
    }
    bool entered = false;
    try
    {
      if (words.Confirm)
      {
        if (!Enter("an unbake", true, out var refusal))
        {
          say(refusal);
          yield break;
        }
        entered = true;
      }
      int number = ctx.Journal.NextNumber(layer.NextOperation);
      var gather = new UnbakeGather();
      yield return UnbakeRead(ctx, scope, words, gather);
      if (gather.Closed)
      {
        say("bc_bake: the world closed while it was read; nothing changed.");
        yield break;
      }
      int pieces = gather.Created.Count;
      var lines = new List<string>
      {
        $"bc_bake {echo}{WordsOf(words)}: a dry run, nothing changes. 'bc_bake {echo}{WordsOf(words)} confirm' unbakes.",
        scope.World ? "World: every zone that has records." : $"Area: {scope.Area!.Describe(scope.Area.Zones().Count)}.",
      };
      var found = $"Found {Num(gather.InGame + gather.Compiler)} records here: {Num(gather.InGame)} baked in game"
        + (gather.Compiler > 0 ? $", {Num(gather.Compiler)} from the compiler's file" + (words.All ? "" : " (add 'all' to take those too)") : "") + ".";
      lines.Add(found);
      if (gather.Records.Count == 0)
      {
        if (!words.Confirm)
        {
          lines.Add("Nothing to unbake.");
          AddStay(gather, lines);
          foreach (var line in lines)
            say(line);
        }
        else
          say($"bc_bake: nothing to unbake here.{(gather.Compiler > 0 && !words.All ? " The records here are the compiler's: add 'all' to take them." : "")}");
        yield break;
      }
      lines.Add($"To unbake: {Num(pieces)} pieces ({KindsText(gather.Kinds)})." + (gather.Freed.Count > 0 ? $" {Num(gather.Freed.Count)} live pieces lose their bake keys and stay where they are." : "")
        + (gather.LiveWithoutPiece > 0 ? $" {Num(gather.LiveWithoutPiece)} Live records have no piece standing; they leave the layer." : ""));
      AddStay(gather, lines);
      if (words.All && gather.Records.Any(r => !r.Record.HasSource))
        lines.Add("With 'all' the compiler's records come out too: the next 'bc_bake load' of the compiler's file brings them back, so the change belongs in the compiler's source.");
      var data = new BakeJournalData
      {
        Number = number,
        Kind = OperationKind.Unbake,
        Time = ctx.UnixTime(),
        Who = ctx.Who.Name,
        PlatformId = ctx.Who.PlatformId,
        X1 = scope.Area?.MinX ?? 0f,
        Z1 = scope.Area?.MinZ ?? 0f,
        X2 = scope.Area?.MaxX ?? 0f,
        Z2 = scope.Area?.MaxZ ?? 0f,
        Radius = scope.Area is { Circle: true } circle ? circle.Radius : 0f,
        WorldName = world.WorldName,
        WorldUid = world.WorldUid,
        RevisionBefore = layer.Revision,
        RevisionAfter = layer.Revision + 1,
        Version = ctx.Version,
        Words = WordsOf(words).Trim(),
        Records = gather.Records,
        Created = gather.Created,
        Adopted = gather.Freed,
      };
      long undoBytes = BakeJournalFile.Encode(data).Length;
      lines.Add($"This makes {Num(pieces)} real objects: each is a piece of the world, saved with it and sent to the players near it. " + (scope.World ? "" : $"Objects in these zones: {Num(gather.Objects)} now, {Num(gather.Objects + pieces)} after. ")
        + $"Undo file: {About(undoBytes)}.");
      if (pieces > UnbakeWarnObjects)
        lines.Add($"WARNING: {Num(pieces)} objects is more than {Num(UnbakeWarnObjects)}. The world's save, its loading and what the players are sent all grow with them, and the game's own limits on a zone's objects are not the layer's. "
          + "Unbake a smaller area at a time if this world should stay light.");
      if (!words.Confirm)
      {
        foreach (var line in lines)
          say(line);
        int players = ctx.Transport.Players;
        if (players > 0)
          say($"{players} player{(players == 1 ? "" : "s")} connected; each gets the change before the records go.");
        if (ctx.Transport.Unavailable is { } soon)
          say($"This build cannot unbake yet: {soon}.");
        yield break;
      }
      if (ctx.Transport.Unavailable is { } nope)
      {
        say($"bc_bake: {nope}, and an unbake takes its records out once the players have the pieces. Nothing is changed.");
        yield break;
      }

      // ---- the run
      double began = ctx.Clock();
      if (!WriteJournal(ctx, data, out var written))
      {
        say(written);
        yield break;
      }
      say($"bc_bake: unbake {number} started, {Num(pieces)} pieces. Undo file written.");
      ctx.Journal.SetState(number, BakeState.Unbaking);
      // The pieces are new: none stands yet, whatever the world holds at their places.
      var work = Prepare(ctx, data, BakeState.Unbaking, live: true, freshPieces: true);
      say($"bc_bake: making {Num(pieces)} pieces, {Num(PiecesPerFrame)} a frame.");
      yield return Apply(ctx, work);
      // The end state is Unbaking, which the next world load settles.
      ctx.Ended?.Invoke(number, BakeState.Settled);
      var operation = data.ToOperation();
      BakedApi.RaiseUnbaked(operation);
      say($"bc_bake: unbake {number} done in {Seconds(ctx.Clock() - began)}: {Num(work.Made)} pieces made"
        + (work.Released > 0 ? $", {Num(work.Released)} live pieces freed" : "") + $", {Num(data.Records.Count)} records out of the layer."
        + (scope.World ? "" : $" Objects here: {Num(gather.Objects)} -> {Num(gather.Objects + work.Made)}."));
      say("'bc_bake undo' puts the records back and the pieces go. The world saves as usual ('save' saves now).");
      ctx.Who.Notify?.Invoke($"Unbake {number} done: {Num(work.Made)} pieces.");
    }
    finally
    {
      Leave(entered);
    }
  }

  // "stone_wall_2x1 1,204, wood_floor 980, darkwood_roof 611 and 18 more kinds"
  internal static string KindsText(Dictionary<string, int> kinds, int show = 3)
  {
    var top = kinds.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
    var text = string.Join(", ", top.Take(show).Select(p => $"{p.Key} {Num(p.Value)}"));
    if (top.Count > show)
      text += $" and {Num(top.Count - show)} more kinds";
    return text;
  }

  private static void AddStay(UnbakeGather gather, List<string> lines)
  {
    if (gather.Consumables > 0)
      lines.Add($"Left alone: {Num(gather.Consumables)} records of kinds that give items when used. The game places them as its own vegetation when a zone generates, and they stay in the layer.");
    if (gather.DecorConsumables > 0)
      lines.Add($"Left alone: {Num(gather.DecorConsumables)} records of kinds that give items when used, which the compiler's file marks as decor. They are scenery, not pickable objects, and they stay in the layer.");
    if (gather.Stay > 0)
      lines.Add($"Not unbaked: {Num(gather.Stay)} records stay in the layer ({KindsText(gather.StayKinds)}): this game cannot make them as pieces.");
  }

  // Whether the prefab a record would be made of is a consumable kind (the first of its candidates this game has).
  private static bool IsConsumableRecord(BakeContext ctx, BakeRecord record)
  {
    foreach (var candidate in record.Entry.Candidates)
    {
      int hash = ctx.World.PrefabOf(candidate.Name);
      if (hash == 0)
        continue;
      var facts = ctx.World.FactsOf(hash);
      return facts != null && BakeClassifier.ConsumableComponent(facts) != null;
    }
    return false;
  }

  // Reads the layer for an unbake, a few zones a frame: each record in scope is turned into the piece it becomes, or counted.
  private static IEnumerator UnbakeRead(BakeContext ctx, UnbakeScope scope, BakeWords words, UnbakeGather into)
  {
    var world = ctx.World;
    var layer = ctx.Layer;
    var withRecords = new HashSet<ZoneKey>(layer.ZonesWithRecords());
    var zones = scope.World ? layer.ZonesWithRecords() : scope.Area!.Zones().Where(withRecords.Contains).ToList();
    var batch = new List<ZoneKey>(ZonesPerFrame);
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
      var found = layer.RecordsIn(batch, r => scope.World || scope.Area!.Contains((float)r.WorldX, (float)r.WorldZ));
      // The seeded pieces of the batch's zones, to find a Live record's piece.
      Dictionary<ZoneKey, Dictionary<ulong, ZDOID>>? live = null;
      foreach (var record in found)
      {
        if (!record.Record.HasSource)
        {
          into.Compiler++;
          // The compiler's records come out only with 'all' (6.3).
          if (!words.All)
            continue;
        }
        else
          into.InGame++;
        if (IsConsumableRecord(ctx, record))
        {
          if (record.Entry.IsDecor)
            into.DecorConsumables++;
          else
            into.Consumables++;
          continue;
        }
        if (record.Role == BakedRole.Live)
        {
          live ??= [];
          if (!live.TryGetValue(record.Zone, out var inZone))
            live[record.Zone] = inZone = world.LiveIn(record.Zone);
          if (inZone.TryGetValue(BakedFormat.LiveKey(record.Record.SourceNumber, record.Record.Id), out var id))
          {
            var copy = world.Copy(new WorldObject { Id = id });
            var facts = world.FactsOf(copy.Prefab);
            copy.PrefabName = facts?.Name ?? record.Prefab;
            into.Freed.Add(new AdoptedPiece { Place = Place(copy), Id = record.Record.Id, Source = record.Record.SourceNumber });
          }
          else
            into.LiveWithoutPiece++;
          into.Records.Add(record);
          continue;
        }
        var piece = PieceOf(ctx, record, ctx.Who.Creator, out var problem);
        if (piece == null)
        {
          into.Stay++;
          into.StayKinds[record.Prefab] = into.StayKinds.TryGetValue(record.Prefab, out var n) ? n + 1 : 1;
          continue;
        }
        into.Created.Add(piece);
        into.Records.Add(record);
        into.Kinds[piece.PrefabName] = into.Kinds.TryGetValue(piece.PrefabName, out var count) ? count + 1 : 1;
      }
      if (!scope.World)
        world.Gather(batch, into.ObjectsScratch);
      yield return null;
    }
    into.Objects = into.ObjectsScratch.Count;
    into.ObjectsScratch.Clear();
  }
}

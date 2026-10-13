// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Patches (spec 5.3): a client assembles the new layer from the old one and the changed zones' blocks, byte for byte; a damaged patch is
// refused (the client then asks for the whole layer).
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static bool SameLayerBytes(BakedLayer a, BakedLayer b) => a.Length == b.Length && a.Bytes.Take(a.Length).SequenceEqual(b.Bytes.Take(b.Length));

  // Applies a patch made for (from, to) and holds the result to the server's bytes and id.
  private static void CheckPatch(string name, BakedLayer from, BakedLayer to, ZoneKey[] changed)
  {
    var patch = BakedPatch.Make(from, to, changed);
    BakedLayer made = null;
    var problem = RefusalOf(() => made = BakedPatch.Apply(from, patch));
    C(problem == null && made != null && SameLayerBytes(made, to) && made.Id == to.Id, $"patch: {name}: assemble equals the server's bytes ({patch.Length:N0} byte patch for a {to.Length:N0} byte layer) {problem}");
    C(BakedPatch.TryApply(from, patch, to.Id, out var viaTry, out var why) && viaTry != null && why == "", $"patch: {name}: TryApply says yes");
    C(BakedPatch.Make(from, to, changed).SequenceEqual(patch), $"patch: {name}: the same two layers make the same patch");
    // Naming no zone changes nothing: the patch carries every block that differs whatever `changed` says.
    var blind = BakedPatch.Make(from, to, Array.Empty<ZoneKey>());
    C(BakedPatch.TryApply(from, blind, to.Id, out _, out _), $"patch: {name}: a patch made without the changed zones is still right");
  }

  private static void PatchTest()
  {
    Section("patch: assemble equals the server's bytes");
    var sample = MakeSample(21);
    var from = sample.Layer;
    var zones = sample.Records.Keys.ToList();

    BakedLayer Build(Action<LayerEdit> change, out ZoneKey[] changed)
    {
      var edit = from.Edit();
      change(edit);
      var result = edit.Build();
      changed = result.Changed;
      return result.Layer;
    }

    var to = Build(e => e.AddRecords([ZoneRecord.CreateYaw(0, zones[1].OriginX + 9, 41, zones[1].OriginZ + 9, 12), ZoneRecord.CreateYaw(2, zones[1].OriginX + 10, 41, zones[1].OriginZ + 9, 12)], 3f), out var changed);
    CheckPatch("adds", from, to, changed);
    to = Build(e => e.RemoveRecords(sample.Records[zones[2]].Take(7)), out changed);
    CheckPatch("removes", from, to, changed);
    to = Build(e =>
    {
      e.AddRecords([ZoneRecord.CreateYaw(0, zones[3].OriginX + 9, 41, zones[3].OriginZ + 9, 12)], 3f);
      e.RemoveRecords(sample.Records[zones[4]].Take(3));
    }, out changed);
    CheckPatch("adds and removes", from, to, changed);
    to = Build(e =>
    {
      int at = e.PaletteIndexFor(Beam());
      e.AddOperation(SampleOperation(e.NextOperationNumber, OperationKind.BakeArea, 2));
      e.AddRecords([ZoneRecord.CreateYaw(at, zones[1].OriginX + 9, 41, zones[1].OriginZ + 9, 12, source: 6, valueSet: 1)], 3f);
    }, out changed);
    CheckPatch("palette growth (and an operation)", from, to, changed);
    to = Build(e => e.AddRecords([ZoneRecord.CreateYaw(0, new ZoneKey(-1024, -1024).OriginX + 3, 41, new ZoneKey(-1024, -1024).OriginZ + 3, 0)], 3f), out changed);
    CheckPatch("the first zone of the index", from, to, changed);
    to = Build(e => e.AddRecords([ZoneRecord.CreateYaw(0, new ZoneKey(1023, 1023).OriginX + 3, 41, new ZoneKey(1023, 1023).OriginZ + 3, 0)], 3f), out changed);
    CheckPatch("the last zone of the index", from, to, changed);
    to = Build(e => e.RemoveWhere(new[] { zones[5] }, _ => true), out changed);
    CheckPatch("a zone that goes", from, to, changed);
    to = Build(e => e.RemoveWhere(null, _ => true), out changed);
    CheckPatch("every record removed", from, to, changed);
    // An empty layer to a layer with records, and back.
    var empty = LayerEdit.New("empty").Build().Layer;
    C(empty.Zones.Count == 0 && empty.Placements == 0 && empty.Palette.Count == 0, "an empty layer has no zones");
    var edit = empty.Edit();
    edit.PaletteIndexFor(Beam());
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    var (filled, filledChanged) = edit.Build();
    CheckPatch("an empty layer to one with a record", empty, filled, filledChanged);
    var emptyAgain = filled.Edit();
    emptyAgain.RemoveWhere(null, _ => true);
    var (emptied, emptiedChanged) = emptyAgain.Build();
    CheckPatch("a layer to an empty one", filled, emptied, emptiedChanged);
    // A load: every block may differ.
    var other = CompilerFile([Wall(), Door(), Roof()], 0);
    var loaded = other.Edit();
    loaded.ReplaceSource0(CompilerFile([Wall(), Gate(), Door(), Roof()], 1));
    var (reloaded, reloadedChanged) = loaded.Build();
    CheckPatch("a load that replaces the compiler's records", other, reloaded, reloadedChanged);
    // Patches in a row.
    var one = Build(e => e.AddRecords([ZoneRecord.CreateYaw(0, zones[1].OriginX + 20, 41, zones[1].OriginZ + 9, 12)], 3f), out var changed1);
    var twoEdit = one.Edit();
    twoEdit.RemoveRecords(sample.Records[zones[2]].Take(2));
    var (two, changed2) = twoEdit.Build();
    var viaOne = BakedPatch.Apply(from, BakedPatch.Make(from, one, changed1));
    var viaTwo = BakedPatch.Apply(viaOne, BakedPatch.Make(one, two, changed2));
    C(SameLayerBytes(viaTwo, two), "two patches one after the other reach the server's bytes");
    C(viaTwo.Revision == from.Revision + 2, "and its revision");
  }

  private static void PatchDamageTest()
  {
    Section("patch: a damaged patch is refused, and the client asks for the whole layer");
    var sample = MakeSample(22);
    var from = sample.Layer;
    var zone = sample.Records.Keys.First();
    var edit = from.Edit();
    edit.AddRecords([ZoneRecord.CreateYaw(0, zone.OriginX + 9, 41, zone.OriginZ + 9, 12)], 3f);
    var (to, changed) = edit.Build();
    var patch = BakedPatch.Make(from, to, changed);
    C(BakedPatch.TryApply(from, patch, to.Id, out _, out _), "the undamaged patch applies");

    // A flipped byte anywhere with the patch's CRC left alone: refused by the CRC.
    var random = new Lcg(77);
    int refused = 0, tried = 0;
    for (int i = 0; i < 300; i++)
    {
      var damaged = (byte[])patch.Clone();
      damaged[random.Int(damaged.Length - 4)] ^= (byte)(1 << random.Int(8));
      tried++;
      if (!BakedPatch.TryApply(from, damaged, to.Id, out var layer, out var problem) && layer == null && problem.Length > 0)
        refused++;
    }
    C(refused == tried, $"{refused} of {tried} patches with one bit flipped (CRC left as it was) are refused");

    // A flipped byte with the CRC made right again: the layer it makes is checked, and its id is not the server's. Whatever happens, no exception
    // other than a refusal, and a patch that is accepted makes exactly the server's layer.
    int accepted = 0, wrong = 0, escaped = 0;
    for (int i = 0; i < 400; i++)
    {
      var damaged = (byte[])patch.Clone();
      damaged[random.Int(damaged.Length - 4)] ^= (byte)(1 << random.Int(8));
      uint crc = BakedFormat.Crc32(damaged, 0, damaged.Length - 4);
      Array.Copy(BitConverter.GetBytes(crc), 0, damaged, damaged.Length - 4, 4);
      try
      {
        if (BakedPatch.TryApply(from, damaged, to.Id, out var layer, out _))
        {
          accepted++;
          if (layer == null || layer.Id != to.Id)
            wrong++;
        }
      }
      catch (Exception)
      {
        escaped++;
      }
    }
    C(escaped == 0 && wrong == 0, $"patches damaged and given a good CRC: {accepted} still make the server's layer (a byte that means nothing), none makes another, none throws");
    // Truncated at every length near the start, and at random lengths.
    int truncatedRefused = 0, truncatedTried = 0;
    for (int length = 0; length < 120; length++)
    {
      var shorter = patch.Take(length).ToArray();
      truncatedTried++;
      if (!BakedPatch.TryApply(from, shorter, to.Id, out _, out _))
        truncatedRefused++;
    }
    for (int i = 0; i < 100; i++)
    {
      var shorter = patch.Take(random.Int(patch.Length)).ToArray();
      truncatedTried++;
      if (!BakedPatch.TryApply(from, shorter, to.Id, out _, out _))
        truncatedRefused++;
    }
    C(truncatedRefused == truncatedTried, $"{truncatedRefused} of {truncatedTried} truncated patches are refused");
    C(!BakedPatch.TryApply(from, null, to.Id, out _, out _), "no patch at all is refused");
    // A patch for another layer.
    var other = MakeSample(23).Layer;
    C(!BakedPatch.TryApply(other, patch, to.Id, out _, out var why) && why.Length > 0, "a patch applied to a layer it was not made for is refused: " + why);
    C(!BakedPatch.TryApply(to, patch, to.Id, out _, out _), "so is one applied to the layer it made (the revision is wrong)");
    // The id the server announced is checked.
    C(!BakedPatch.TryApply(from, patch, "00000000000000000000000000000000", out var none, out var idProblem) && none == null && idProblem.Contains("server"), "a patch that does not make the layer with the announced id is refused");
    C(Refuses(() => BakedPatch.Apply(from, new byte[] { 1, 2, 3 }), "patch"), "Apply throws a refusal naming the patch");
  }
}

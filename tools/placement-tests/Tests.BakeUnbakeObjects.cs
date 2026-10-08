// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The unbake dry run states how many real objects it makes and how big the undo file is, and warns over 50,000 objects (VALtima's layer would make
// 702,700).

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static void BakeUnbakeObjectsTest()
  {
    Section("unbake: the dry run says how many real objects it makes");
    BakeRunner.Reset();
    var fx = BakeNewFx("unbake-objects", BakeStandardWorld, out _);
    BakeRun(fx, BakeStandardArea(), "town confirm");
    BakeRunner.Reset();
    fx.Said.Clear();
    BakeUnbakeRun(fx, BakeWholeWorld, "all", "unbake world all");
    var text = string.Join("\n", fx.Said);
    C(text.Contains("This makes 8 real objects") && text.Contains("Undo file: about") && !text.Contains("WARNING"), "a small world: the count and the undo file's size, no warning: " + text);

    foreach (var (records, warns) in new[] { (BakeRunner.UnbakeWarnObjects, false), (BakeRunner.UnbakeWarnObjects + 1, true) })
    {
      BakeRunner.Reset();
      var wall = Entry("stone_wall", BakedRole.Static, BakedCollision.Prefab);
      var edit = LayerEdit.New("compiler");
      int index = edit.PaletteIndexFor(wall);
      var batch = new List<ZoneRecord>[100];
      for (int z = 0; z < 100; z++)
        batch[z] = [];
      for (int i = 0; i < records; i++)
      {
        var zone = new ZoneKey(i % 10, (i / 10) % 10);
        batch[zone.X * 10 + zone.Z].Add(ZoneRecord.CreateYaw(index, zone.OriginX + 2 + (i / 100) % 60, 30, zone.OriginZ + 2 + (i / 6000) % 60, 0));
      }
      foreach (var group in batch.Where(b => b.Count > 0))
        edit.AddRecords(group.ToArray(), 3f);
      var big = BakeNewFx("unbake-big", world =>
      {
        world.Facts["stone_wall".GetStableHashCode()] = new PrefabFacts("stone_wall", BakeSafeParts, height: 3f);
        return [];
      }, out _, edit.Build().Layer);
      BakeUnbakeRun(big, BakeWholeWorld, "all", "unbake world all");
      var dry = string.Join("\n", big.Said);
      string number = records.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
      C(dry.Contains($"This makes {number} real objects"), $"{number} records: the dry run states that many real objects: " + dry.Substring(0, Math.Min(dry.Length, 600)));
      C(dry.Contains("Undo file: about"), "and the journal's size");
      C(dry.Contains("WARNING: ") == warns, warns ? $"over {BakeRunner.UnbakeWarnObjects:N0} objects it warns" : $"{number} objects, exactly the limit, is not over it: no warning");
      C(big.Clock.Events.Count == 0 && big.World.Objects.Count == 0, "and a dry run changes nothing");
    }
  }
}

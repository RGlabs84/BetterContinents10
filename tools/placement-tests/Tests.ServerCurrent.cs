// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// ZNetPatch swaps BetterContinents.Settings in four places (a world is loaded, the server's settings arrive, a cached copy is used, the server
// sends them again) and BakedLayerStore.Changed is not raised by a swap, only by Set. Everything of slice B that reads the layer must therefore
// read BakedLayerStore.Current at the moment it is asked, and keep nothing from an earlier ask except what it checks against Current: these
// tests swap the settings the way ZNetPatch does, with no event, and ask each reader.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  public static void ServerCurrentTest()
  {
    Section("current: the readers of the layer follow the settings when they are swapped, without an event");
    var layer = ServerValtima(nameof(ServerCurrentTest));
    if (layer == null)
      return;
    var (x, z, town) = ServerGroundSpot(layer);
    var flagged = layer.Zones.First(r => r.NoVegetation).Key;
    float fx = (float)flagged.OriginX + 10f, fz = (float)flagged.OriginZ + 10f;
    var events = new List<string>();
    void OnChanged(BakedLayer old, BakedLayer next, ZoneKey[] zones) => events.Add("changed");
    BakedLayerStore.Changed += OnChanged;
    var before = BC.Settings;
    try
    {
      string Reading()
      {
        float ground = BakedGround.GetBiomeHeightPostfix(10f, x, z);
        bool covers = BakedVegetation.IsFootprint(fx, fz), postfix = BakedVegetation.InsideClearAreaPostfix(false, new Vector3(fx, 40f, fz));
        return $"{(Math.Abs(ground - town) < 0.001f ? "ground" : ground == 10f ? "no ground" : "wrong ground")}, {(covers ? "covered" : "free")}, {(postfix ? "covered" : "free")}, "
               + (BakedProtect.LayerPresent() ? "protecting" : "not protecting") + $", revision {BakedPlacements.Revision}";
      }

      // Each swap is a plain assignment to the settings, as ZNetPatch does it.
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
      var first = Reading();
      var second = ServerWithRevision(layer, 41);
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = second };
      var other = Reading();
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
      var none = Reading();
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Layer = layer };
      var again = Reading();
      C(first == $"ground, covered, covered, protecting, revision {layer.Revision}", "the first layer: its ground, its flag-16 zone's footprint, the postfix, the protection, its revision: " + first);
      C(other == $"ground, covered, covered, protecting, revision {second.Revision}", "swapped for another layer (same ground, another revision): the new revision, all else read afresh: " + other);
      C(none == "no ground, free, free, not protecting, revision 0", "swapped for none: no ground, nothing covered, nothing protected: " + none);
      C(again == first, "swapped back: as the first time again: " + again);
      C(events.Count == 0, $"and no swap raised an event ({events.Count})");

      // A change through the store does raise one, and the readers follow it just the same.
      BakedLayerStore.Set(second, null);
      C(events.Count == 1 && Reading() == other, "a Set raises the event once, and the readers have the layer it set");
    }
    finally
    {
      BakedLayerStore.Changed -= OnChanged;
      BC.Settings = before;
    }
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The rest of the client renderer's offline checks (slice C): the ground paint a layer's zones put into the terrain's base mask, the placeholder of
// the zone hold, the settings of section 10, and the small distance helper of the lit copies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BetterContinents;
using UnityEngine;

namespace PlacementTests;

internal static partial class Tests
{
  private static Color[] FlatMask(int n) => Enumerable.Repeat(new Color(0f, 0f, 0f, 0.5f), n).ToArray();

  // A zone's 64 x 64 cells of 1 m go into the base mask of the terrain built for it and for the zones around it, by world position: red dirt, green
  // cultivated, blue paved, the mask's alpha kept.
  private static void ClientPaintTest()
  {
    Section("client: ground paint");
    BakedPaint.Clear();
    var cells = new byte[4096];
    cells[0] = 1;                // x -32..-31, z -32..-31: dirt
    cells[63] = 2;               // x 31..32, z -32..-31: paved
    cells[10 * 64 + 5] = 3;      // x -27..-26, z -22..-21: cultivated
    BakedPaint.Register(new ZoneKey(0, 0), cells);
    C(BakedPaint.ZonesPainted == 1 && BakedPaint.Cell(-32, -32) == 1 && BakedPaint.Cell(31, -32) == 2 && BakedPaint.Cell(-27, -22) == 3 && BakedPaint.Cell(0, 0) == 0, "a zone's cells are looked up by world position");
    C(BakedPaint.Cell(32, -32) == 0 && BakedPaint.Cell(-33, -32) == 0 && BakedPaint.Cell(0, 40) == 0, "outside the zone, with no zone registered there, there is nothing");

    // the terrain of zone (0, 0): 65 x 65 pixels from its south-west corner, row 0 south
    var data = new HeightmapBuilder.HMBuildData(new Vector3(0, 0, 0), 64, 1f, false, null) { m_baseMask = FlatMask(65 * 65) };
    C(BakedPaint.Paint(data), "the terrain's base mask is painted");
    Color At(int l, int k) => data.m_baseMask[k * 65 + l];
    C(At(0, 0) == new Color(1f, 0f, 0f, 0.5f) && At(63, 0) == new Color(0f, 0f, 1f, 0.5f) && At(5, 10) == new Color(0f, 1f, 0f, 0.5f), "red dirt, blue paved, green cultivated, each keeping the mask's alpha");
    C(At(1, 0) == new Color(0f, 0f, 0f, 0.5f) && At(32, 32) == new Color(0f, 0f, 0f, 0.5f), "an unpainted pixel is left as it was");
    C(At(64, 0) == new Color(0f, 0f, 0f, 0.5f), "the pixel of the next zone's first cell is the next zone's (not painted yet)");
    // the last row and column of a terrain are the next zone's first cells: found by world position
    BakedPaint.Register(new ZoneKey(1, 0), new byte[4096].Select((_, i) => i == 0 ? (byte)2 : (byte)0).ToArray());
    var again = new HeightmapBuilder.HMBuildData(new Vector3(0, 0, 0), 64, 1f, false, null) { m_baseMask = FlatMask(65 * 65) };
    BakedPaint.Paint(again);
    C(again.m_baseMask[0 * 65 + 64] == new Color(0f, 0f, 1f, 0.5f), "pixel 64 of row 0 is the first cell of the zone to the east");
    // a terrain the paint does not reach, a distant one, another scale, the wrong size: left alone
    var elsewhere = new HeightmapBuilder.HMBuildData(new Vector3(640, 0, 640), 64, 1f, false, null) { m_baseMask = FlatMask(65 * 65) };
    var distant = new HeightmapBuilder.HMBuildData(new Vector3(0, 0, 0), 64, 1f, true, null) { m_baseMask = FlatMask(65 * 65) };
    var scaled = new HeightmapBuilder.HMBuildData(new Vector3(0, 0, 0), 64, 2f, false, null) { m_baseMask = FlatMask(65 * 65) };
    var wrong = new HeightmapBuilder.HMBuildData(new Vector3(0, 0, 0), 64, 1f, false, null) { m_baseMask = FlatMask(10) };
    C(!BakedPaint.Paint(elsewhere) && !BakedPaint.Paint(distant) && !BakedPaint.Paint(scaled) && !BakedPaint.Paint(wrong) && !BakedPaint.Paint(null), "a terrain far from the paint, a distant one, one at another scale or size, and nothing, are left alone");
    // a zone whose paint is taken away is forgotten; one with all cells 0 has none
    BakedPaint.Register(new ZoneKey(1, 0), new byte[4096]);
    C(BakedPaint.ZonesPainted == 1 && BakedPaint.Cell(32, -32) == 0, "an empty paint section takes the zone's paint away");
    BakedPaint.Register(new ZoneKey(0, 0), null);
    C(BakedPaint.ZonesPainted == 0 && BakedPaint.Cell(-32, -32) == 0, "so does none");
    BakedPaint.Register(new ZoneKey(2, 2), new byte[10]);
    C(BakedPaint.ZonesPainted == 0, "a section of the wrong size is not a paint");
    BakedPaint.Clear();
  }

  // The zone hold's placeholder looks like what ZoneSystem reads of a ZDO (a type and a sector) and is taken out of the game's list by reference.
  private static void ClientHoldTest()
  {
    Section("client: the zone hold");
    var zone = new ZoneKey(-3, 7);
    // (a ZDO's constructor calls into the engine for its rotation: the harness makes one without it)
    ZDO Make(ZoneKey where, ZDOID? uid)
    {
      var zdo = (ZDO)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
      zdo.Init();
      typeof(ZDO).GetField("m_position", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(zdo, where.Centre);
      zdo.Type = ZDO.ObjectType.Solid;
      if (uid is { } id)
        zdo.m_uid = id;
      return zdo;
    }
    var hold = Make(zone, null);
    C(hold.GetSector() == zone.ToVector2s() && hold.Type == ZDO.ObjectType.Solid && !hold.Created && hold.IsValid() && hold.m_uid == ZDOID.None, "a placeholder is a Solid ZDO in the zone's sector, with no id");
    // the game's loading list: LocationProxy's real ZDOs and ours, in one zone
    var other = Make(zone, new ZDOID(1234L, 5u));
    var loading = new Dictionary<Vector2s, List<ZDO>> { [zone.ToVector2s()] = [other, hold] };
    // ZDO equality is by id: the placeholder has none, so List.Remove would be a gamble; by reference it is not
    BakedZoneHold.Remove(loading, zone.ToVector2s(), hold);
    C(loading.Count == 1 && loading[zone.ToVector2s()].Count == 1 && ReferenceEquals(loading[zone.ToVector2s()][0], other), "the placeholder goes and whatever else holds the zone stays");
    BakedZoneHold.Remove(loading, zone.ToVector2s(), hold);
    C(loading.Count == 1 && loading[zone.ToVector2s()].Count == 1, "taking it out again changes nothing");
    var last = new Dictionary<Vector2s, List<ZDO>> { [zone.ToVector2s()] = [hold] };
    BakedZoneHold.Remove(last, zone.ToVector2s(), hold);
    C(last.Count == 0, "the zone's entry goes with the last holder (the game indexes the list without looking)");
    BakedZoneHold.Remove(last, zone.ToVector2s(), hold);
    C(last.Count == 0, "a zone that is not held is not an error");
    // what ZoneSystem.IsZoneReadyForType reads: a lower type waits while the placeholder stands
    var types = new[] { ZDO.ObjectType.Default, ZDO.ObjectType.Prioritized, ZDO.ObjectType.Solid, ZDO.ObjectType.Terrain };
    C(types.Select(t => (int)t < (int)hold.Type).SequenceEqual([true, true, false, false]), "creatures and items (Default, Prioritized) wait for a Solid hold; floors and terrain do not");
  }

  // The settings of the new section (spec 11.2): its place last, its keys, defaults and ranges; and that they bind into the config.
  private static void ClientSettingsTest()
  {
    Section("client: the settings of section 10");
    var groups = SettingsSchema.Groups;
    var group = groups[^1];
    C(group.Name == "BetterContinents.BakedPlacements" && groups.Length >= 11, "the baked placements group is the last one declared");
    C(groups.Take(10).Select(g => g.Name).Last() == "BetterContinents.Export", "the groups before it keep their places (Export is still the tenth, 09)");
    var keys = group.Settings.Select(s => s.Key).ToArray();
    C(keys.SequenceEqual(["Draw Distance", "Draw Scale", "Detail Scale", "Shadows", "Light Distance", "Seat Distance", "Light Shadows", "Tints", "Diagnostics"]), "Draw Distance, Draw Scale, Detail Scale, Shadows, Light Distance, Seat Distance, Light Shadows, Tints and Diagnostics: " + string.Join(", ", keys));
    C(group.Settings.All(s => s.Scope == SettingScope.Live && s.Section == "10 BetterContinents.BakedPlacements"), "every one is read live, in section '10 BetterContinents.BakedPlacements'");
    C(SettingsSchema.BakedDrawDistance.Default == 4 && SettingsSchema.BakedDrawScale.Default == 1.5f && SettingsSchema.BakedDetailScale.Default == 1f
      && SettingsSchema.BakedShadowMode.Default == BakedShadows.All && SettingsSchema.BakedLightDistance.Default == 90f && SettingsSchema.BakedSeatDistance.Default == 12f
      && SettingsSchema.BakedLightShadows.Default == 4 && SettingsSchema.BakedTints.Default == "" && SettingsSchema.BakedDiagnostics.Default == false, "the defaults: 4 zones, 1.5, 1, All (until the probe says), 90 m, 12 m, 4 lit copies with shadows, no tints, no diagnostics");
    C(SettingsSchema.BakedDrawDistance.Limits == (1, 8) && SettingsSchema.BakedDrawScale.Limits == (0.5f, 4f) && SettingsSchema.BakedDetailScale.Limits == (0.5f, 4f)
      && SettingsSchema.BakedLightDistance.Limits == (0f, 200f) && SettingsSchema.BakedSeatDistance.Limits == (0f, 40f) && SettingsSchema.BakedLightShadows.Limits == (0, 32),
      "the ranges: 1 to 8 zones, 0.5 to 4, 0.5 to 4, 0 to 200 m, 0 to 40 m, 0 to 32 copies");
    C(group.Settings.All(s => !s.ReadsIntoSettings && s.ConsoleGroup == null), "none is a world setting or a bc command: having a layer is the world's");
    // bound into a config file of its own, in its place
    var path = Path.Combine(Work, "baked.cfg");
    BakedSettings.DrawDistance = null;
    try
    {
      var cfg = new ConfigFile(path, true);
      var b = cfg.Declare();
      for (int i = 0; i < 10; i++)
        b.AddGroup("G" + i, g => { });
      b.AddGroup(group.Name, g =>
      {
        foreach (var s in group.Settings)
          s.Bind(g);
      });
      C(BakedSettings.DrawDistance is { Value: 4 } && BakedSettings.DrawScale!.Value == 1.5f && BakedSettings.Shadows!.Value == BakedShadows.All && BakedSettings.LightDistance!.Value == 90f
        && BakedSettings.SeatDistance!.Value == 12f && BakedSettings.LightShadows!.Value == 4 && BakedSettings.Tints!.Value == "" && !BakedSettings.Diagnostics!.Value && BakedSettings.DetailScale!.Value == 1f, "bound, the settings read their defaults");
      C(BakedSettings.DrawDistance!.Definition.Section == "10 BetterContinents.BakedPlacements" && BakedSettings.Shadows!.Description.Description.Contains("Near"), "in section 10, with their descriptions");
      BakedSettings.Shadows!.Value = BakedShadows.Near;
      C(BakedSettings.Shadows.Value == BakedShadows.Near, "a value can be changed");
    }
    finally
    {
      BakedSettings.DrawDistance = null;
      BakedSettings.LightShadows = null;
      BakedSettings.DrawScale = BakedSettings.DetailScale = BakedSettings.LightDistance = BakedSettings.SeatDistance = null;
      BakedSettings.Shadows = null;
      BakedSettings.Tints = null;
      BakedSettings.Diagnostics = null;
    }
  }

  private static void ClientCopiesTest()
  {
    Section("client: lit copies and seats");
    var box = new float[] { 0, 0, 0, 10, 10, 10 };
    C(BakedCopies.SqrDistance(box, new Vector3(5, 5, 5)) == 0f && BakedCopies.SqrDistance(box, new Vector3(13, 5, 5)) == 9f && BakedCopies.SqrDistance(box, new Vector3(-3, 14, 5)) == 25f,
      "the distance to the box of a zone's copies is 0 inside it and the distance to its nearest face or corner outside");
    C(BakedCopies.LightHysteresis == 20f && BakedCopies.SeatHysteresis == 3f && BakedCopies.LightsPerFrame == 4 && BakedCopies.SeatsPerFrame == 6, "a lit copy goes 20 m past where it came and 4 are made a frame; a seat 3 m and 6 a frame");
  }
}

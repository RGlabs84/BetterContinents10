// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// A location map's pins (ImageMapLocation): where a world made before 0.10 puts them (the corner of a random pixel of
// the pin, i / size across the map) and where one made since does (exactly on the pixel, i / (size - 1) like every
// other map; the pixel nearest a bigger pin's middle; the locations sharing a colour dealt out the same way every
// time); and the start position override, placed before the pins on a world made since 0.10, and a location that
// finds its zone taken saying so (ZoneSystemPatch.PlaceLocations).
internal static class PinTest
{
  static void C(bool ok, string what) => Program.C(ok, what);

  public static void Run(string work)
  {
    System.Console.WriteLine("== location pins");
    var png = Path.Combine(work, "pins", "locationmap.png");
    Directory.CreateDirectory(Path.GetDirectoryName(png)!);
    const int n = 1025;
    using (var image = new Image<Rgba32>(n, n, new Rgba32(0, 0, 0, 255)))
    {
      image[0, 0] = new Rgba32(0, 255, 0, 255);                 // PinA: the top left pixel
      image[n - 1, n - 1] = new Rgba32(0, 0, 255, 255);         // PinB: the bottom right one
      for (int dy = -1; dy <= 1; dy++)                          // PinC: 3 x 3 around (500, 600)
        for (int dx = -1; dx <= 1; dx++)
          image[500 + dx, 600 + dy] = new Rgba32(255, 255, 0, 255);
      image[700, 100] = new Rgba32(255, 0, 255, 255);           // PinD: two pixels side by side
      image[701, 100] = new Rgba32(255, 0, 255, 255);
      for (int i = 0; i < 8; i++)                               // Shared1 and Shared2 share a colour: 8 pins
        image[100 + i * 50, 900] = new Rgba32(128, 128, 128, 255);
      image.SaveAsPng(png);
    }
    File.WriteAllLines(Path.ChangeExtension(png, ".txt"),
      ["PinA: 0,255,0", "PinB: 0,0,255", "PinC: 255,255,0", "PinD: 255,0,255", "Shared1: 128,128,128", "Shared2: 128,128,128"]);

    // The map flips the picture, so row r of the picture is row n - 1 - r of the map.
    Vector2 Exact(int col, int row) => new(col / (float)(n - 1), (n - 1 - row) / (float)(n - 1));
    Vector2 Corner(int col, int row) => new(col / (float)n, (n - 1 - row) / (float)n);
    Vector2 Only(ImageMapLocation map, string name) => map.GetAllSpawns(name).Single();

    var exact = ImageMapLocation.Create(png, exactPins: true)!;
    C(exact != null && Only(exact, "PinA") == Exact(0, 0) && Only(exact, "PinB") == Exact(n - 1, n - 1),
      "a world made since 0.10: a one-pixel pin lands exactly on its pixel, the corner pixels on the map's corners (0 and 1)");
    var worldA = BC.Geometry.NormalizedToWorld(Only(exact, "PinA"));
    var worldB = BC.Geometry.NormalizedToWorld(Only(exact, "PinB"));
    C(worldA == new Vector2(-10500f, 10500f) && worldB == new Vector2(10500f, -10500f),
      $"so on a 21000 m map they are the world's corners, where the height and biome maps put those pixels ({worldA}, {worldB})");
    C(Only(exact, "PinC") == Exact(500, 600), "a 3 x 3 pin lands on its middle pixel");
    C(Only(exact, "PinD") == Exact(700, 100), "a pin of two pixels: the first of the two (both are as near its middle)");
    var exactAgain = ImageMapLocation.Create(png, exactPins: true)!;
    bool same = new[] { "PinA", "PinB", "PinC", "PinD", "Shared1", "Shared2" }
      .All(name => exact.GetAllSpawns(name).SequenceEqual(exactAgain.GetAllSpawns(name)));
    C(same && exact.GetAllSpawns("Shared1").Count() + exact.GetAllSpawns("Shared2").Count() == 8
          && exact.GetAllSpawns("Shared1").Any() && exact.GetAllSpawns("Shared2").Any(),
      "the same map gives the same pins every time, the 8 pins of a shared colour dealt out to both of its locations the same way");

    var old = ImageMapLocation.Create(png)!;
    C(Only(old, "PinA") == Corner(0, 0) && Only(old, "PinB") == Corner(n - 1, n - 1),
      "a world made before 0.10: at the pixel's corner (i / size), as always");
    var c = Only(old, "PinC");
    bool inPin = Enumerable.Range(-1, 3).Any(dx => Enumerable.Range(-1, 3).Any(dy => c == Corner(500 + dx, 600 + dy)));
    C(inPin, "and a bigger pin at one of its own pixels");

    // "bc reload lm" in a loaded world: the map the world holds was read back from its settings (made without the world's
    // rule), and a reload reads the picture again by that world's own rule.
    var locationField = typeof(BC.BetterContinentsSettings).GetField("LocationMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
    Vector2 ReloadedPinA(int version)
    {
      var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = version };
      locationField.SetValue(s, ImageMapLocation.Create(png, exactPins: false));
      BC.BetterContinentsSettings.MapKind.Location.Reload(s);
      return Only((ImageMapLocation)locationField.GetValue(s)!, "PinA");
    }
    C(ReloadedPinA(12) == Exact(0, 0), "bc reload lm in a world made since 0.10: the pins land exactly on their pixels again");
    C(ReloadedPinA(11) == Corner(0, 0), "bc reload lm in a world made before 0.10: at the pixel's corner, as always");

    // VALtimaOnline's canvas: 10501 pixels over 21000 m, a pixel every 2 m.
    var canvas = new ImageMapLocation { Size = 10501 };
    int off = 0;
    foreach (var x in new[] { 0, 1, 2, 1234, 5249, 5250, 5251, 9999, 10499, 10500 })
    {
      var w = BC.Geometry.NormalizedToWorld(canvas.PinPosition(new Vector2Int(x, 10500 - x)));
      if (Mathf.Abs(w.x - (2f * x - 10500f)) > 0.01f || Mathf.Abs(w.y - (2f * (10500 - x) - 10500f)) > 0.01f)
        off++;
    }
    C(off == 0, "on a 10501 pixel map over 21000 m, pixel i's pin is at 2 i - 10500 m: the 2 m grid its heights are on");

    Placement(exact);
  }

  // ZoneSystemPatch.PlaceLocations on a ZoneSystem of the game's own, holding no locations yet.
  static void Placement(ImageMapLocation map)
  {
    var patch = typeof(BC).GetNestedType("ZoneSystemPatch", BindingFlags.NonPublic)!;
    var place = patch.GetMethod("PlaceLocations", BindingFlags.NonPublic | BindingFlags.Static)!;
    ZoneSystem NewZones()
    {
      var zones = (ZoneSystem)RuntimeHelpers.GetUninitializedObject(typeof(ZoneSystem));
      foreach (var name in new[] { "m_locationInstances", "m_locationIDCache", "m_locationGroupCache", "m_locationMaxGroupCache" })
      {
        var field = typeof(ZoneSystem).GetField(name)!;
        field.SetValue(zones, Activator.CreateInstance(field.FieldType));
      }
      return zones;
    }
    ZoneSystem.ZoneLocation Location(string name) => new() { m_prefabName = name, m_group = "", m_groupMax = "", m_enable = true, m_quantity = 1 };
    var start = Location("StartTemple");
    var pinA = Location("PinA");
    // PinA is at the world's north-west corner (-10500, 10500): the start position is put in the same zone.
    var settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, OverrideStartPosition = true, StartPositionX = -10490f, StartPositionY = 10490f };
    typeof(BC.BetterContinentsSettings).GetField("LocationMap", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, map);
    string Holder(ZoneSystem zones) => zones.m_locationInstances.Values.Single().m_location.m_prefabName;

    foreach (var version in new[] { 12, 11 })
    {
      settings.Version = version;
      var zones = NewZones();
      int mark = LogHandler.Lines.Count;
      place.Invoke(null, [zones, new List<ZoneSystem.ZoneLocation> { start, pinA }, settings, new Func<float, float, float>((x, z) => 30f)]);
      var log = LogHandler.Lines.Skip(mark).ToList();
      if (version == 12)
      {
        C(zones.m_locationInstances.Count == 1 && Holder(zones) == "StartTemple",
          "a world made since 0.10: the start position goes first, so a pin in its zone cannot take it");
        C(log.Any(l => l.StartsWith("[Warning]") && l.Contains("PinA") && l.Contains("NOT placed") && l.Contains("StartTemple")),
          "and the pin says it was not placed, and which location holds its zone");
      }
      else
      {
        C(zones.m_locationInstances.Count == 1 && Holder(zones) == "PinA",
          "a world made before 0.10: the pins first and the start last, as always, so the pin keeps the zone");
        C(log.Any(l => l.StartsWith("[Error]") && l.Contains("start position override was NOT applied") && l.Contains("PinA"))
              && !log.Any(l => l.Contains("Start position overriden")),
          "and the start says loudly it was not applied, and why, instead of claiming it was");
      }
    }

    // Zones apart: both are placed, on any world.
    settings.Version = 12;
    settings.StartPositionX = 0f;
    settings.StartPositionY = 0f;
    var apart = NewZones();
    place.Invoke(null, [apart, new List<ZoneSystem.ZoneLocation> { start, pinA }, settings, new Func<float, float, float>((x, z) => 30f)]);
    C(apart.m_locationInstances.Count == 2, "in zones of their own the start and the pin are both placed");
  }
}

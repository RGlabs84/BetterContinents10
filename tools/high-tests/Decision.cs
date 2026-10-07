// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// When a world is a high world, the land's height at Heightmap Amount 81, the rule for what is inside a dungeon, and the
// helpers the patched game code calls.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // A heightmap of one grey: every pixel the same, so the map reads that value anywhere.
  private static ImageMapFloat GreyMap(ushort value)
  {
    using var image = new Image<L16>(4, 4);
    for (int y = 0; y < 4; y++)
      for (int x = 0; x < 4; x++)
        image[x, y] = new L16(value);
    using var stream = new MemoryStream();
    image.SaveAsPng(stream, new PngEncoder { BitDepth = PngBitDepth.Bit16, ColorType = PngColorType.Grayscale });
    return ImageMapFloat.Create(stream.ToArray(), ImageMapFloat.HeightAlpha.None)!;
  }

  private static readonly FieldInfo HeightMapField = typeof(BC.BetterContinentsSettings).GetField("HeightMap", BindingFlags.NonPublic | BindingFlags.Instance)!;

  // A Better Continents world with a heightmap (white unless said), at an amount.
  private static BC.BetterContinentsSettings HighWorld(float amount, ushort pixel = 65535, bool enabled = true, bool overrideAll = true, float blend = 1f, float add = 0f)
  {
    var s = new BC.BetterContinentsSettings
    {
      EnabledForThisWorld = enabled, Version = 12, HeightmapAmount = amount, HeightmapBlend = blend, HeightmapAdd = add, HeightmapOverrideAll = overrideAll,
    };
    HeightMapField.SetValue(s, GreyMap(pixel));
    return s;
  }

  // ---- when a world is a high world ------------------------------------------------------------------------------------
  private static void DecisionTests()
  {
    Section("when a world is a high world");
    var none = new BC.BetterContinentsSettings();
    Check(HighTerrain.MaxMetres(none) == 0f && !HighTerrain.Wanted(none), "the main menu's settings (Better Continents off, no heightmap): not a high world");
    var noMap = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, HeightmapAmount = 81f };
    Check(HighTerrain.MaxMetres(noMap) == 0f && !HighTerrain.Wanted(noMap), "Heightmap Amount 81 with no heightmap: the amount multiplies nothing, not a high world");
    Check(!HighTerrain.Wanted(HighWorld(81f, enabled: false)), "Better Continents off for the world: not a high world, whatever the amount");

    // A world is high above Heightmap Amount 5, the most before 0.10.3. The top it cannot pass is 200 m * (amount - 0.15) with Heightmap Override All
    // on (the base height is the terrain; Bound.cs has the whole derivation): 1 (the default), 2, 5 (the old most), 6 (the first that is high), 11, 12, 81.
    foreach (var (amount, metres, wanted) in new[] { (1f, 170f, false), (2f, 370f, false), (5f, 970f, false), (5.01f, 972f, true), (6f, 1170f, true), (11f, 2170f, true), (12f, 2370f, true), (81f, 16170f, true), (0f, 0f, false) })
    {
      var s = HighWorld(amount);
      Check(Math.Abs(HighTerrain.MaxMetres(s) - metres) < 0.5f && HighTerrain.Wanted(s) == wanted, $"Heightmap Amount {amount}: the land cannot pass {HighTerrain.MaxMetres(s)} m (expected {metres}), high world {HighTerrain.Wanted(s)} (expected {wanted})");
    }
    // With it off the game's Mountain biome doubles what is over 80 m: 2b - 0.4 and its noise (0.22 units at most measured, 0.45 allowed).
    float off5 = HighTerrain.MaxMetres(HighWorld(5f, overrideAll: false));
    Check(!HighTerrain.Wanted(HighWorld(5f, overrideAll: false)) && off5 > 200f * (2f * 4.85f - 0.4f + 0.223f) && off5 < 200f * (2f * 4.85f - 0.4f + 0.5f),
      $"an amount of 5, the most a world could have before 0.10.3, over the game's own biome heights (which double what is over 80 m): a bound of {off5:0} m, and still not a high world");
    var extreme = HighWorld(5f, overrideAll: false, add: 1f);
    extreme.SeaLevelAdjustment = 1f;
    Check(!HighTerrain.Wanted(extreme) && HighTerrain.MaxMetres(extreme) > 2700f && HighTerrain.MaxMetres(extreme) < 2800f,
      $"nor is an amount of 5 with every other setting at the most an old world could have (a bound of {HighTerrain.MaxMetres(extreme):0} m): an older version's world is left as it was");
    Check(HighTerrain.Wanted(HighWorld(6f, overrideAll: false)) && HighTerrain.Wanted(HighWorld(6f, blend: 0f)) && HighTerrain.Wanted(HighWorld(81f, blend: 0.01f)),
      "above 5 it is a high world whatever the blend and the other settings: the patches are right at any height");
    Check(Math.Abs(HighTerrain.MaxMetres(HighWorld(81f, blend: 0.5f)) - 8070f) < 0.5f && Math.Abs(HighTerrain.MaxMetres(HighWorld(81f, add: 1f)) - 16370f) < 0.5f,
      "Heightmap Blend 0.5 halves what the amount adds (8070 m), Heightmap Add 1 adds a unit (16370 m)");
    Check(HighTerrain.MaxMetres(HighWorld(float.NaN)) == 0f && HighTerrain.MaxMetres(HighWorld(float.PositiveInfinity)) == 0f && !HighTerrain.Wanted(HighWorld(float.NaN)),
      "an amount that is not a number, or infinite: counted as nothing");
    var sea = HighWorld(12f);
    sea.SeaLevelAdjustment = 1f;
    Check(Math.Abs(HighTerrain.MaxMetres(sea) - 2570f) < 0.5f, "a Sea Level Adjustment that lifts the land counts: 2570 m");

    // Update keeps the state the patched code reads, and says what it did once.
    HighTerrain.GroundSource = null;
    int mark = CapturingLogHandler.Lines.Count;
    HighTerrain.Update(HighWorld(81f));
    Check(HighTerrain.Active && Math.Abs(HighTerrain.Top - 16170f) < 0.5f, "Update with Heightmap Amount 81: active, the top 16170 m");
    Check(CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains("High terrain: this world's heightmap is read at Heightmap Amount 81, above the 5 that older versions allowed, so its land can reach 16170 m")) == 1,
      "and says so once in the log");
    HighTerrain.Update(HighWorld(81f));
    Check(CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains("High terrain")) == 1, "the same world again: nothing said again");
    HighTerrain.Update(new BC.BetterContinentsSettings());
    Check(!HighTerrain.Active && HighTerrain.Top == 0f && CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("is not read above Heightmap Amount 5")), "the menu again: inactive, and says the rules are left alone");
    int count = CapturingLogHandler.Lines.Count;
    HighTerrain.Update(new BC.BetterContinentsSettings());
    Check(CapturingLogHandler.Lines.Count == count, "inactive again: nothing logged (every world load passes here)");
  }

  // ---- the land's height at Heightmap Amount 81 --------------------------------------------------------------------------
  private static void HeightTests()
  {
    Section("the land's height at Heightmap Amount 81");
    Check(Math.Abs(WorldExportMath.ValueToMetres(1f, 81f, 0.5f) - 16170f) < 0.01f && Math.Abs(WorldExportMath.ValueToMetres(0f, 81f, 0.5f) + 30f) < 0.01f && Math.Abs(WorldExportMath.ValueToMetres(1f, 1f, 0.5f) - 170f) < 0.01f,
      $"the export's maths: a white pixel at amount 81 is 16170 m ({WorldExportMath.ValueToMetres(1f, 81f, 0.5f)}), a black one -30 m, at amount 1 170 m");
    Check(Math.Abs(WorldExportMath.ValueToMetres(1f, 5f, 0.5f) - 970f) < 0.05f && Math.Abs(WorldExportMath.ValueToMetres(1f, 12f, 0.5f) - 2370f) < 0.05f,
      "amount 5, the old most: 970 m; amount 12: 2370 m");

    // The value a heightmap pixel gives ApplyHeightmap, and the base height of a Better Continents world made since 0.10 (GetBaseHeightV3).
    var white = HighWorld(81f);
    Check(Same(white.ApplyHeightmap(0.3f, 0.7f, 0f), 81f), "ApplyHeightmap: a white pixel at amount 81 is 81 units (the base height, in 200 m)");
    var half = HighWorld(81f, 32768);
    Check(Math.Abs(half.ApplyHeightmap(0.5f, 0.5f, 0f) - 81f * 32768f / 65535f) < 1e-4f, "a half-grey pixel: 40.5 units, nothing clamped on the way");
    var blended = HighWorld(81f, blend: 0.25f);
    Check(Math.Abs(blended.ApplyHeightmap(0.5f, 0.5f, 10f) - (10f * 0.75f + 81f * 0.25f)) < 1e-3f, "Heightmap Blend 0.25 over the game's own value 10: Lerp(10, 81, 0.25)");
    var masked = HighWorld(81f, 32768);
    masked.HeightmapMask = 1f;
    float grey = 32768f / 65535f;
    Check(Math.Abs(masked.ApplyHeightmap(0.5f, 0.5f, 0f) - 81f * grey * grey) < 1e-3f, "Heightmap Mask 1 multiplies the blended height by the pixel again: 81 * 0.5 * 0.5");

    var previous = BC.Settings;
    try
    {
      BC.Settings = white;
      var v3 = typeof(BC.WorldGeneratorPatch).GetMethod("GetBaseHeightV3", BindingFlags.NonPublic | BindingFlags.Static)!;
      float baseHeight = (float)v3.Invoke(null, [100f, 100f, 0f])!;
      Check(Math.Abs(baseHeight * 200f - 16170f) < 0.01f, $"GetBaseHeightV3 at amount 81, a white pixel, sea level 0.5: {baseHeight * 200f} m (16170: the base height minus 0.15, times 200)");
      BC.Settings = HighWorld(81f, 0);
      baseHeight = (float)v3.Invoke(null, [100f, 100f, 0f])!;
      Check(Math.Abs(baseHeight * 200f + 30f) < 0.01f, $"a black pixel: {baseHeight * 200f} m (-30)");
    }
    finally
    {
      BC.Settings = previous;
    }

    // An export's heightmap.png records the amount it was made for (a text chunk): 81 and 0.5 come back as they went in.
    var record = new HeightmapRecord(81f, 0.5f);
    var read = HeightmapRecord.From([new PngTextData(HeightmapRecord.Keyword, record.Text, "", "")]);
    Check(record.Text == "Heightmap Amount = 81; Sea Level Adjustment = 0.5" && read is { Amount: 81f, SeaLevel: 0.5f } && read.Matches(81f, 0.5f) && !read.Matches(80f, 0.5f),
      $"the heightmap record at amount 81: \"{record.Text}\", read back exactly, and a world reading it at 80 is told");

    // 16 bits: one step of the picture is 200 * 81 / 65535 m, and the export writes the nearest.
    float step = WorldExportMath.ValueToMetres(1f, 81f, 0.5f) - WorldExportMath.ValueToMetres(65534f / 65535f, 81f, 0.5f);
    Check(Math.Abs(step - 0.2472f) < 0.001f, $"one 16-bit step at amount 81 is {step:0.0000} m (0.2472), at amount 2 {200f * 2f / 65535f:0.0000} m");
    var random = new System.Random(81);
    float worst = 0f;
    for (int i = 0; i < 20000; i++)
    {
      float metres = (float)(random.NextDouble() * 16200.0 - 30.0);
      float value = (metres / 200f + 0.15f) / 81f;
      var pixel = WorldExportMath.ValueToUShort(value, out var clip);
      worst = Math.Max(worst, Math.Abs(WorldExportMath.ValueToMetres(WorldExportMath.UShortToValue(pixel), 81f, 0.5f) - metres));
    }
    Check(worst <= 0.1237f + 0.01f, $"metres -> 16-bit pixel -> metres at amount 81: at worst {worst:0.0000} m off (half a step, 0.1236)");

    // The picture's own sampling: bilinear between pixel centres, so a step of one is a one-pixel ramp and a plateau stays flat.
    using var image = new Image<L16>(8, 8);
    for (int y = 0; y < 8; y++)
      for (int x = 0; x < 8; x++)
        image[x, y] = new L16((ushort)(x < 4 ? 1000 : 1001));
    using var stream = new MemoryStream();
    image.SaveAsPng(stream, new PngEncoder { BitDepth = PngBitDepth.Bit16, ColorType = PngColorType.Grayscale });
    var stair = ImageMapFloat.Create(stream.ToArray(), ImageMapFloat.HeightAlpha.None)!;
    float at(int px) => stair.GetValue(px / 7f, 0.5f) * 81f * 200f - 30f;
    Check(Math.Abs(at(1) - at(2)) < 1e-3f && Math.Abs(at(5) - at(6)) < 1e-3f && Math.Abs((at(4) - at(3)) - 0.2472f) < 0.001f,
      $"sampling a one-step stair at amount 81: flat on each side, one 0.2472 m ramp between two pixels (pixels 3 and 4 are {at(3):0.000} and {at(4):0.000} m): a gentle slope is stairs, not smoothed");
  }

  // ---- what is inside a dungeon -----------------------------------------------------------------------------------------
  private static void InteriorTests()
  {
    Section("what is inside a dungeon");
    try
    {
      HighTerrain.Update(new BC.BetterContinentsSettings());
      // The game's own rule while the patches are off: anything higher than 3000 m.
      HighTerrain.GroundSource = _ => 30f;
      Check(!HighTerrain.Interior(new Vector3(0, 2999.9f, 0)) && !HighTerrain.Interior(new Vector3(0, 3000f, 0)) && HighTerrain.Interior(new Vector3(0, 3000.1f, 0)),
        "not a high world: higher than 3000 m, as the game says (2999.9 and 3000 not, 3000.1 yes)");
      HighTerrain.GroundSource = _ => throw new InvalidOperationException("the ground is not asked for in a world that is not high");
      Check(HighTerrain.Interior(new Vector3(0, 8000f, 0)), "and the ground is not asked for there");

      HighTerrain.Update(HighWorld(81f));
      foreach (var (y, ground, interior, what) in new (float, float, bool, string)[]
      {
        (2999f, 30f, false, "under 3000 m is never inside"),
        (3001f, 30f, false, "3001 m over the sea is a balloon, not a dungeon (2971 m over the ground)"),
        (5030f, 30f, true, "a dungeon at an entrance of 30 m: 5030 m, 5000 m over the ground"),
        (5000f, 0f, true, "5000 m over a ground of 0 m"),
        (8100f, 8000f, false, "standing on a mountain of 8000 m"),
        (16200f, 16100f, false, "the summit of a 16100 m mountain, 100 m up"),
        (21030f, 16030f, true, "a dungeon on the highest ground: 21030 m"),
        (7100f, 2100f, true, "a dungeon on ground of 2100 m"),
        (5029f, 2100f, false, "2929 m over ground of 2100 m: under 3000 m above it"),
        (6000f, 1450f, true, "a dungeon whose zone's ground is 450 m higher than its entrance's (entrance 1000 m)"),
        (6000f, 550f, true, "a dungeon whose zone's ground is 450 m lower than its entrance's"),
        (4000f, 1000f, false, "exactly 3000 m over the ground is not over it"),
      })
      {
        HighTerrain.GroundSource = _ => ground;
        bool got = HighTerrain.Interior(new Vector3(100f, y, -50f));
        Check(got == interior, $"high world, y {y} over ground {ground}: inside {got} (expected {interior}): {what}");
      }
      HighTerrain.GroundSource = p => p.x > 0f ? 8000f : 30f;
      Check(!HighTerrain.Interior(new Vector3(10f, 8100f, 0f)) && HighTerrain.Interior(new Vector3(-10f, 8100f, 0f)),
        "the ground under the point's own x and z is the one asked: 8100 m over a 8000 m mountain is outside, over the sea beside it inside");
      HighTerrain.GroundSource = _ => float.MinValue / 4f;
      Check(HighTerrain.Interior(new Vector3(0, 3001f, 0)), "no ground known at all: the game's own answer (inside above 3000 m)");
    }
    finally
    {
      HighTerrain.GroundSource = null;
      HighTerrain.Update(new BC.BetterContinentsSettings());
    }
  }

  // ---- the helpers the patched game code calls --------------------------------------------------------------------------
  private static void HelperTests()
  {
    Section("the helpers the patched game code calls");
    try
    {
      HighTerrain.Update(new BC.BetterContinentsSettings());
      Check(HighTerrain.RayStart(6000f) == 6000f && HighTerrain.RayStart(0f) == 0f && HighTerrain.RayLength(10000f, 6000f) == 10000f && HighTerrain.RaiseOrigin(new Vector3(1, 5, 2)) == new Vector3(1, 5, 2)
            && HighTerrain.BlockerLift(new Vector3(1, 0, 2)) == 2000f,
        "not a high world: rays start and run as the game's");
      Check(HighTerrain.MaxAltitude(1000f) == 1000f && HighTerrain.MaxAltitude(280f) == 280f && HighTerrain.MaxElevation(10000) == 10000 && HighTerrain.MaxAverageHeight(10000f) == 10000f
            && HighTerrain.TileCentre(5f, 5f, 2500f) == 2500f && HighTerrain.ValkyrieLift(5000f) == 0f,
        "and altitude limits, elevation, the average height, the AI's tiles and the Valkyrie are the game's");

      HighTerrain.Update(HighWorld(81f));
      // The top is 16170 m: rays start at 17170 m (RayMargin 1000 m more) and run as much longer as their start was lower, which reaches as deep as the game's.
      bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
      Check(Near(HighTerrain.RayStart(6000f), 17170f) && HighTerrain.RayStart(20000f) == 20000f && Near(HighTerrain.RayStart(5000f + 30f), 17170f), "a ray that starts at 6000 m (or 5030 m) starts at 17170 m; one that starts higher stays");
      Check(Near(HighTerrain.RayLength(10000f, 6000f), 21170f) && Near(HighTerrain.RayLength(10000f, 5000f), 22170f) && Near(HighTerrain.RayLength(1000f, 500f), 17670f) && HighTerrain.RayLength(10000f, 20000f) == 10000f,
        "a 10000 m ray from 6000 m becomes 21170 m, from 5000 m 22170 m, the grass's 1000 m ray from 500 m 17670 m; one that started higher than 17170 m stays");
      Check(Near(6000f - 10000f, HighTerrain.RayStart(6000f) - HighTerrain.RayLength(10000f, 6000f)) && Near(5000f - 10000f, HighTerrain.RayStart(5000f) - HighTerrain.RayLength(10000f, 5000f))
            && Near(500f - 1000f, HighTerrain.RayStart(500f) - HighTerrain.RayLength(1000f, 500f)),
        "each ray reaches the same depth as before: -4000 m for GetGroundHeight, -5000 m for GetGroundData, -500 m for the grass");
      var lifted = HighTerrain.RaiseOrigin(new Vector3(1, 5, 2));
      Check(Near(lifted.y, 17170f) && lifted.x == 1f && lifted.z == 2f && HighTerrain.RaiseOrigin(new Vector3(1, 25000, 2)) == new Vector3(1, 25000, 2),
        "GetGroundData's origin (5000 m over the point, whose y is 0 for a zone's vegetation and the grass) is lifted to 17170 m, a higher one stays");
      Check(Near(HighTerrain.MaxAltitude(1000f), 16170f) && Near(HighTerrain.MaxAltitude(2000f), 16170f) && Near(HighTerrain.MaxAltitude(10000f), 16170f) && HighTerrain.MaxAltitude(280f) == 280f
            && HighTerrain.MaxAltitude(999.9f) == 999.9f && HighTerrain.MaxAltitude(-5f) == -5f && HighTerrain.MaxAltitude(20000f) == 20000f,
        "an upper altitude of 1000 m or more becomes the top (the game's \"no limit\" and the few that raise it); a lower one (the tree line at 280 m, the water's) stays");
      Check(HighTerrain.MaxElevation(10000) == int.MaxValue && HighTerrain.MaxElevation(500) == 500 && HighTerrain.MaxElevation(-10000) == -10000,
        "RandomSpawn's upper elevation of 10000 m is no limit; one a prefab chose stays");
      Check(Near(HighTerrain.MaxAverageHeight(10000f), 17170f) && HighTerrain.MaxAverageHeight(5000f) == 5000f && HighTerrain.MaxAverageHeight(20000f) == 20000f,
        "an alt biome's limit of 10000 m on a sector's mean height passes the top");

      foreach (var (ground, centre) in new[] { (30f, 2500f), (400f, 2500f), (401f, 2901f), (3000f, 5500f), (8000f, 10500f), (16100f, 18600f) })
      {
        HighTerrain.GroundSource = _ => ground;
        Check(HighTerrain.TileCentre(10f, 20f, 2500f) == centre, $"the AI's tile over ground of {ground} m is centred on {centre} m");
      }
      HighTerrain.GroundSource = null;
      Check(HighTerrain.ValkyrieLift(30f) == 0f && HighTerrain.ValkyrieLift(5f) == 0f && HighTerrain.ValkyrieLift(1230f) == 1200f, "the Valkyrie starts as much higher as the player stands over the water");
    }
    finally
    {
      HighTerrain.GroundSource = null;
      HighTerrain.Update(new BC.BetterContinentsSettings());
    }
  }
}

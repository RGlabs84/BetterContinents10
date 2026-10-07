// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using UnityEngine;

namespace BetterContinents;

/// <summary>High Terrain ([02 BetterContinents.Heightmap]): which worlds get the patches of the game's height rules (HighTerrain.Wanted). A
/// world saves it only when it is not Auto (DataKey.HighTerrain): a world without the key is Auto.</summary>
public enum HighTerrainMode
{
  /// <summary>The default: a world whose heightmap is read at a Heightmap Amount above 5 (the most any earlier version allowed) gets them, every
  /// other world keeps the game's own rules.</summary>
  Auto = 0,
  /// <summary>Every Better Continents world gets them, whatever its heights.</summary>
  On = 1,
  /// <summary>No world gets them: the game's own height rules, whatever the amount.</summary>
  Off = 2,
}

/// <summary>The modes as a world's settings or a config hold them: a number that is none of the modes (one a newer version wrote, or a 7 typed in
/// a config) is Auto.</summary>
internal static class HighTerrainModes
{
  internal static HighTerrainMode Of(int value) => Enum.IsDefined(typeof(HighTerrainMode), value) ? (HighTerrainMode)value : HighTerrainMode.Auto;

  internal static HighTerrainMode Known(HighTerrainMode mode) => Of((int)mode);
}

// Terrain up to 16 km above the sea.
//
// Heightmap Amount reaches 81 (-30 m to 16,170 m with Sea Level Adjustment 0.5), but Valheim 1.0 was written for terrain that
// stops at about 400 m, and a few of its rules hold an absolute height in a number: what is "inside a dungeon", where the
// ground is looked for, how high a plant or a creature may be, which heights the AI can walk. Each of them is a code path of its
// own, patched in BetterContinents.HighTerrainPatch.cs (the toggles) and HighTerrainPatches (the IL and prefixes); the numbers
// and rules they stand on are here.
//
// All of it is switched on by one decision (Wanted), which the world's High Terrain setting (HighTerrainMode) makes: Auto, the default,
// wants it for a Better Continents world whose heightmap is read at an amount above 5, the most any version before 0.10.3 allowed; On for
// every Better Continents world; Off for none. The patches are applied while that holds and removed when it stops, so a world that does not
// want them runs the game's own code, unchanged, and so does every world an older version could make, whatever its other settings, under
// Auto: the game's rules were as they are in those, and stay. The line is drawn at the amount, not at the 3000 m of the best known rule:
// a world of Amount 6, the first that is high, reaches 1,400 m at most, already past the game's grass (500 m), AI tiles (400 m of ground)
// and altitude limits of plants (1000 m).
public static class HighTerrain
{
  // What Character.InInterior calls inside a dungeon (Character.cs:4372): higher than this. A dungeon is built 5000 m above its
  // entrance (Location.cs:60), so in vanilla nothing else is ever that high.
  public const float InteriorHeight = 3000f;

  // The rule in a high world: inside a dungeon is higher than InteriorHeight AND more than this above the ground below. A
  // dungeon is 5000 m above the ground of its own zone (the interior stands at the zone's centre, the entrance anywhere in
  // the zone, so the ground under the interior can differ from the entrance's by the slope over 45 m: some hundreds of metres
  // on the steepest ground a location is placed on) and so is more than 4,000 m above the ground below it; a person on a
  // mountain is a few metres above theirs. 3000 m is between the two with room on both sides, and the same number as the game's.
  public const float InteriorAboveGround = 3000f;

  // The most Heightmap Amount any version before 0.10.3 allowed (its range was 0 to 5): a world read at more is new, and under Auto is a
  // high world. Above 5 its land can reach 1,200 m and more, past what the game's grass (500 m), AI tiles (400 m of ground) and altitude
  // limits (1000 m) were made for; from about 13 it passes 2500 m, under the game's own 3000 m rule, and at 81 it reaches 16,170 m (32,300 m
  // with Heightmap Override All off: see MaxMetres).
  public const float OldMaxAmount = 5f;

  // ---- how high the land can be (MaxMetres) -----------------------------------------------------------------------------------
  //
  // The game counts heights in units of 200 m (WorldGenerator.GetHeightMultiplier) with the sea at 0.15 (30 m). Better Continents' base height b of a
  // point (WorldGeneratorPatch.GetBaseHeightV3, which replaces the game's GetBaseHeight) is the heightmap's value there times Amount * Blend, plus its
  // value times Add, minus 0.15, plus the Sea Level shift; the Mask, the drop-off at the edge and the mountain-free centre only lower it, and the
  // brightest pixel a picture can have is 1. What the land is over b depends on Heightmap Override All (on unless a world says otherwise):
  //   on:   the terrain IS b: the postfix of WorldGenerator.GetBiomeHeight sets GetBaseHeight * 200 m, whatever the biome.
  //   off:  the game's own height of the biome is made over b (GetBiomeHeight -> Get<Biome>Height). The highest of them, from a base height of 0.9 up, is
  //         the Mountain biome's, which is the game's own biome wherever b > 0.4 and Better Continents' biome map's wherever it says Mountain
  //         (GetSnowMountainHeight):   h = b + (b - 0.4) + 0.2 * n + 0.01 * p + 0.003 * q  [ + 2 * r * tilt ]
  //         with n = u * v + w * x * u * v * 0.5 and u, v, w, x, p, q, r the game's Perlin noises. It DOUBLES what is over 0.4 (80 m): a peak of 2,000 m
  //         in the heightmap is 3,980 m high in a Mountain biome. Measured on the dedicated server (tools/high-tests/rig: a world of Amount 81 made
  //         from the synthetic ziggurat map, 20,000 random points on each flat plateau): terrain = 200 * (2b - 0.4) + 0 .. 40 m (mean 11 m), the summit
  //         plateau (b 80.5) 32,119 .. 32,160 m; and Mathf.PerlinNoise itself is -0.14 .. 1.14 (4,000,000 points), the noise term 0.223 units (45 m) at
  //         most. MountainNoise is the formula's maximum with every noise at PerlinMax. Next is Mistlands (GetMistlandsHeight: b + 0.4 * n^1.5 and
  //         small terms, MistlandsRise); Meadows, Plains, BlackForest, DeepNorth, Swamp and Ocean are under that (tools/high-tests/Bound.cs runs all of
  //         them, transcribed, over every noise extreme).
  // Not in the bound: the Mountain biome's tilt term (2 * r * tilt, r a noise and tilt the change of b over 2 m in x plus in z): a heightmap's own
  // steepness, 4 m per unit of slope for r = 1 and 4.55 m at the largest r measured (1.1384): +164 m on the 35-slope cliffs of the rig's ziggurat
  // (4.7 m per unit), 0 on a plateau. RayMargin covers it for slopes (|dx| + |dy|, metres per metre) up to about 220; Expand World Data's own
  // height rules; a rough map (it only mixes the two heights); and the 8 m at most a player's terrain tools add.
  private const float MetresPerUnit = 200f;
  private const float BaseOffset = 0.15f;
  private const float MountainStart = 0.4f;
  private const float PerlinMax = 1.15f;
  private static readonly float MountainNoise = 0.2f * FieldNoiseMax + 0.013f * PerlinMax;
  private static readonly float MistlandsRise = 0.4f * (float)Math.Pow(FieldNoiseMax, 1.5) + 0.04f * PerlinMax + 1f / 400f + 0.002f * PerlinMax;
  // u * v + w * x * u * v * 0.5 with every noise at PerlinMax.
  private static float FieldNoiseMax => PerlinMax * PerlinMax * (1f + 0.5f * PerlinMax * PerlinMax);

  // The game's own base height (WorldGenerator.GetBaseHeight, which a heightmap replaces) at its largest, in units of 200 m: three products of Perlin
  // noises added in turn, n = P1 * P2; n += P3 * P4 * n * 0.9; n += P5 * P6 * n * 0.5; minus 0.07. With every noise at PerlinMax that is 4.74 (948 m,
  // which the Mountain biome doubles); the ocean channels, the mountain-free centre and the world's edge only lower it, and the noises never are all at
  // their largest together: the highest mountain of the game's own terrain is under 370 m (Heightmap Amount 2 keeps them all). This is the base height
  // of a world without a heightmap, and of the transparent part of one with Heightmap Alpha. tools/high-tests/Bound.cs runs the game's formula over every
  // noise extreme, and reads it in both game builds.
  internal static readonly float GameBaseMax = GameBase(PerlinMax);

  private static float GameBase(float noise)
  {
    float pp = noise * noise;
    float n = pp;
    n += pp * n * 0.9f;
    n += pp * 0.5f * n;
    return n - 0.07f;
  }

  // How far above MaxMetres the game's absolute ground rays start (a ray that starts under the ground finds none): what MaxMetres leaves out, which is
  // the Mountain biome's tilt term of a world with Heightmap Override All off (up to 4.55 m per unit of slope of the heightmap: 164 m measured on the
  // rig's cliffs of slope 35, 350 to 430 m at the steepest pixel of VALtima's map, by central and by forward differences: rig/results/valtima_crop.txt) and the
  // 8 m a player's terrain tools add. 1,000 m is that for slopes (|dx| + |dy|) up to about 220, and the ray is longer by as much as it starts higher, so a higher
  // start costs nothing.
  public const float RayMargin = 1000f;

  // The upper limit of an altitude rule that means "no limit": the default of the game's altitude fields, in metres over the
  // water (ZoneSystem.cs:68, :237, SpawnSystem.cs:65), which a few entries raise to 2000, 5000 or 10000. A cap lower than this
  // is a rule (the water's, the tree line at 280 m) and stays.
  public const float NoLimitAltitude = 1000f;

  // RandomSpawn and RandomObject: the elevation (over the sea's 0) their parts are switched off above (RandomSpawn.cs:20,
  // RandomObject.cs:26), 10000 unless a prefab says otherwise.
  public const int NoLimitElevation = 10000;

  // The AI's navigation tiles (Pathfinding.cs:107-109) cover -500 m to 5500 m. A dungeon's rooms stand 5000 m above the
  // entrance, so a tile whose ground is higher than this has its dungeon outside it.
  public const float TileGroundLimit = 400f;
  public const float TileCentreAboveGround = 2500f;

  // What the world loaded now reaches; zero while it is not a high world. Written on the main thread by Update, read by the
  // patched game code wherever that runs.
  private static volatile bool active;
  private static volatile float top;
  private static volatile float rayTop;
  private static volatile float altitudeCap;

  // Whether the loaded world is a high world (Update).
  public static bool Active => active;
  // The most its terrain can reach, in metres (MaxMetres), or zero.
  public static float Top => top;

  // For tests: what stands in for the game's ground height under a point.
  internal static Func<Vector3, float>? GroundSource;

  // ---- the decision ---------------------------------------------------------------------------------------------------

  // The height in metres over zero the terrain of these settings cannot pass (the sea is at 30 m): the top the ground rays start over, RayMargin more, and
  // the altitude limits are lifted to. It is not what the land reaches (the heightmap's brightest pixel is not looked for: a world is the same high world
  // whatever picture it holds, and nothing is read from the image), and not what makes a world a high one (Wanted): see above for what it stands on.
  //   Override All on:   200 m * b
  //   Override All off:  200 m * max(2b - 0.4 + MountainNoise, b + MistlandsRise)
  // b = Amount * Blend + max(0, Add) - 0.15 + max(0, Sea Level shift). Where the game's own base height shows instead of the heightmap's (a world
  // without a heightmap, which High Terrain On also patches, and the transparent part of a heightmap with Heightmap Alpha) b is at most GameBaseMax
  // (plus the sea shift), over the same formulas. 0 when Better Continents is off.
  internal static float MaxMetres(BetterContinents.BetterContinentsSettings s)
  {
    if (!s.EnabledForThisWorld)
      return 0f;
    float sea = Mathf.Max(0f, Sane(s.SeaLevelAdjustment));
    bool overrides = s.HasHeightMap && s.HeightmapOverrideAll;
    float units = 0f;
    if (s.HasHeightMap)
    {
      float amount = Sane(s.HeightmapAmount), blend = Mathf.Clamp01(Sane(s.HeightmapBlend)), add = Sane(s.HeightmapAdd);
      units = Units(amount * blend + Mathf.Max(0f, add) - BaseOffset + sea, overrides);
    }
    if (!s.HasHeightMap || s.BlendsHeightmapAlpha)
      units = Mathf.Max(units, Units(GameBaseMax + sea, overrides));
    return Mathf.Max(0f, units) * MetresPerUnit;
  }

  // The most the terrain is over a base height b, in units of 200 m.
  private static float Units(float b, bool overrides) => overrides ? b : Mathf.Max(2f * b - MountainStart + MountainNoise, b + MistlandsRise);

  private static float Sane(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : Mathf.Clamp(v, -1000f, 1000f);

  // Whether the patches are wanted for these settings: Better Continents is on for the world and its High Terrain setting says so. Auto says so
  // for a heightmap read at an amount above the old most (HighByAmount), On for every world, Off for none.
  internal static bool Wanted(BetterContinents.BetterContinentsSettings s) =>
    s.EnabledForThisWorld && (s.HighTerrainMode switch
    {
      HighTerrainMode.On => true,
      HighTerrainMode.Off => false,
      _ => HighByAmount(s),
    });

  // What Auto goes by: a heightmap read at an amount above the old most.
  internal static bool HighByAmount(BetterContinents.BetterContinentsSettings s) => s.HasHeightMap && s.HeightmapAmount > OldMaxAmount;

  // What Update said last (null: nothing worth saying), so that a world load that changes nothing says nothing.
  private static string? said;

  // Called by DynamicPatch with the settings of the world being loaded (or the menu's), before the toggles are switched.
  internal static void Update(BetterContinents.BetterContinentsSettings s)
  {
    bool on = Wanted(s);
    float reach = on ? MaxMetres(s) : 0f;
    bool was = active;
    if (on != active || reach != top)
    {
      top = reach;
      lastTerrain = null;
      rayTop = on ? reach + RayMargin : 0f;
      altitudeCap = on ? reach : 0f;
      active = on;
    }
    // On says so; Off says what it costs a world whose heightmap Auto would have patched for; every other world that is not patched says nothing.
    var note = on ? OnNote(s, reach) : s.EnabledForThisWorld && s.HighTerrainMode == HighTerrainMode.Off && HighByAmount(s) ? OffNote(s) : null;
    if (note != null)
    {
      if (note != said)
        BetterContinents.Log(note);
    }
    else if (was)
      BetterContinents.Log("High terrain: off (no high world is loaded): the game's own height rules apply");
    said = note;
  }

  // A change of the mode in a world that is running (bc h ht, LiveChange.Rules): the patches follow it at once (DynamicPatch), and the zones are generated
  // again, so that what they place follows the rules. The noise and the minimap are as they were, and so is the loaded terrain, unless the alt biomes are
  // made again: an alt biome's limit of 10000 m on a sector's mean height is lifted with the patches (MaxAverageHeight), which only matters to a world whose
  // land passes 9,000 m, and then the placement and the terrain's alt-biome corners are redone as every alt-biome change does. Only the world on this machine
  // changes (single player, or the host): a player who joins takes the mode from the settings the server sends.
  internal static void ModeChanged()
  {
    float limit = MaxAverageHeight(NoLimitAverageHeight);
    BetterContinents.DynamicPatch();
    if (MaxAverageHeight(NoLimitAverageHeight) != limit)
      BetterContinents.AltBiomeControl.RequestRebuild(BetterContinents.AltBiomeControl.RebuildLevel.Assignment, "High Terrain changed", GameUtils.ResetZones);
    else
      GameUtils.RegenerateZones();
  }

  private static string OnNote(BetterContinents.BetterContinentsSettings s, float reach)
  {
    var what = $"Better Continents patches the game's rules that hold a height (what is inside a dungeon, where the ground is looked for from {reach + RayMargin:0} m, "
      + $"altitude limits of {NoLimitAltitude:0} m and more, the grass, the AI's tiles)";
    return s.HighTerrainMode == HighTerrainMode.On
      ? $"High terrain: High Terrain is On for this world, so its land can reach {reach:0} m at most: {what}"
      : $"High terrain: this world's heightmap is read at Heightmap Amount {s.HeightmapAmount:0.##}, above the {OldMaxAmount:0} that older versions allowed, so its land can reach {reach:0} m: {what}";
  }

  private static string OffNote(BetterContinents.BetterContinentsSettings s) =>
    $"High terrain: High Terrain is Off for this world, whose heightmap is read at Heightmap Amount {s.HeightmapAmount:0.##} (its land can reach {MaxMetres(s):0} m): the game's own height rules stand, "
    + "so there is no grass above 500 m, no plants, creatures or locations above about 1,000 m, anything above 3,000 m counts as inside a dungeon, and no ground is found above 6,000 m";

  // ---- what the patched game code calls ---------------------------------------------------------------------------------

  // Character.InInterior(Vector3, Transform, Character): the three ways the game asks it. Every call site of the game is
  // rewritten to these (Mono compiles a short method into the methods that call it, where a patch of it is not seen), and the
  // three methods themselves are patched for the mods that call them.
  public static bool Interior(Vector3 position)
  {
    if (!(position.y > InteriorHeight))
      return false;
    if (!active)
      return true;
    // Higher over the sea than the land can be, and 3000 m more: over every ground there is.
    if (position.y - top > InteriorAboveGround)
      return true;
    return position.y - GroundBelow(position) > InteriorAboveGround;
  }

  public static bool Interior(Transform me) => Interior(me.position);

  public static bool Interior(Character character) => Interior(character.transform.position);

  // The height of the ground at the x and z of a point: the loaded terrain's when it is loaded (what a player's zone always
  // is), else the generator's, which is what the terrain is made from (a dedicated server loads no terrain for the zones around
  // its players). With neither, as far below as can be: the game's own answer.
  private static float GroundBelow(Vector3 position)
  {
    if (GroundSource != null)
      return GroundSource(position);
    // InInterior is asked about every frame by the weather and the render groups, and must not throw: a zone's terrain that has no
    // heights yet (one that has just been made, or whose generation failed) is answered from the generator instead.
    try
    {
      // The terrain zone asked about last, which is the one asked about next (a monster is asked about at every physics step).
      var map = lastTerrain;
      if (map != null && map.IsPointInside(position))
      {
        if (map.GetWorldHeight(position, out var cached))
          return cached;
      }
      else
      {
        map = Heightmap.FindHeightmap(position);
        lastTerrain = map;
        if (map != null && map.GetWorldHeight(position, out var found))
          return found;
      }
    }
    catch (Exception)
    {
      lastTerrain = null;
    }
    return GroundAt(position.x, position.z);
  }

  private static Heightmap? lastTerrain;

  private static float GroundAt(float x, float z)
  {
    if (GroundSource != null)
      return GroundSource(new Vector3(x, 0f, z));
    try
    {
      var generator = WorldGenerator.instance;
      if (generator != null)
        return generator.GetHeight(x, z);
    }
    catch (Exception)
    {
      // As far below as can be, the game's own answer.
    }
    return float.MinValue / 4f;
  }

  // The rays that start at a height of their own (ZoneSystem.GetGroundHeight's 6000 m, GetGroundData's 5000 m over its point, the
  // grass's 500 m over its point, the AI's FindGround): at least above the highest terrain, and as much longer (RayLength), so that
  // they reach as deep as they did. The points of a zone's plants and of the grass have y 0, so a start over the point is a start
  // at 5000 m or 500 m, and the ground over that was never found.
  public static float RayStart(float vanilla) => vanilla > rayTop ? vanilla : rayTop;

  public static Vector3 RaiseOrigin(Vector3 origin)
  {
    if (origin.y < rayTop)
      origin.y = rayTop;
    return origin;
  }

  // A ray of the game's that starts at a height of its own, so many metres long, as long as it was and as much longer as its start is
  // higher now: it reaches as deep as it did.
  public static float RayLength(float length, float vanillaStart) => length + (rayTop > vanillaStart ? rayTop - vanillaStart : 0f);

  // ZoneSystem.IsBlocked(p) looks for a "blocker" from 2000 m over the point (down 10000 m): over the ground, on the low ground the game
  // was made for, and under it for a zone's vegetation, whose points have y 0. So the start is 2000 m over the higher of the point and
  // the ground below it, and not over the top of the world, which would take in the dungeons standing 5000 m over their entrances.
  public static float BlockerLift(Vector3 point)
  {
    if (!active)
      return 2000f;
    float lift = GroundAt(point.x, point.z) + 2000f - point.y;
    return lift > 2000f ? lift : 2000f;
  }

  // The upper altitude of a vegetation, location, spawn or grass entry: a limit of the game's "no limit" 1000 m or more is the top
  // of the world, a lower one is a rule and stays.
  public static float MaxAltitude(float cap) => cap >= NoLimitAltitude && cap < altitudeCap ? altitudeCap : cap;

  // RandomSpawn and RandomObject's upper elevation.
  public static int MaxElevation(int elevation) => active && elevation == NoLimitElevation ? int.MaxValue : elevation;

  // An alt biome's upper limit of the mean height of a sector (AltBiome.m_maxAvgHeight, 10000 m unless it says otherwise).
  public const float NoLimitAverageHeight = 10000f;

  public static float MaxAverageHeight(float cap) => active && cap >= NoLimitAverageHeight && cap < top + RayMargin ? top + RayMargin : cap;

  // Where the AI's navigation tile of a tile id is centred in height. Valheim's tiles are 6000 m high and centred on 2500 m
  // (-500 m to 5500 m): the ground, and the dungeon 5000 m above it (and a dungeon's rooms reach a little higher), are inside
  // only while the ground is under 400 m. Above that the tile is centred 2500 m over its ground, which keeps both inside.
  public static float TileCentre(float x, float z, float vanilla)
  {
    if (!active)
      return vanilla;
    float ground = GroundAt(x, z);
    return ground > TileGroundLimit ? ground + TileCentreAboveGround : vanilla;
  }

  // The Valkyrie that carries a new player in starts 500 m up and comes down to 100 m (Valkyrie.cs:15-17): over a sea level
  // ground. On high ground that is under the ground, so both heights are lifted by how far over the water the player stands.
  public static float ValkyrieLift(float playerY) => active ? Mathf.Max(0f, playerY - 30f) : 0f;
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownGround.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace BetterContinents;

// The layer's 1 m ground (build spec 8.1, U2), on every machine. VALtima's compiler works out, per zone, the height of the town's ground at
// the terrain's own 1 m (65 x 65 vertices) and a weight: 255 is the town's ground, 0 the world's, and a band of 8 m around a town's core
// blends. VALtimaOnline put that into each zone terrain's build data on clients only; here it is a final postfix on
// WorldGenerator.GetBiomeHeight, so the terrain on the clients, WorldGenerator.GetHeight (where the server places trees, locations and
// seeded pieces), the minimap and the distant terrain all take the same ground:
//
//   the terrain builder calls GetBiomeHeight for each of its four corner biomes and blends the results linearly (HeightmapBuilder.Build);
//   a blend of lerps with one ground and one weight is the lerp of the blend, so the blend of the postfix's answers is the postfix's
//   answer for the blend.
//
// It runs on the terrain builder's thread and on the minimap's workers (BetterContinents.HeightmapPatch, the multi-threaded minimap), so it
// reads nothing but the layer's immutable ground (BakedLayerStore.Current, one reference, taken once) and takes no lock. At a vertex of
// the grid it gives exactly VALtima's per-vertex rule: weight 255 the town's height, anything between a blend; between vertices the height
// and the weight are bilinear. A position in a zone with no ground, and a weight of 0, leave the world's ground alone.
internal static class BakedGround
{
  // A passthrough postfix on WorldGenerator.GetBiomeHeight(Heightmap.Biome, float, float, out Color, bool, bool), the lowest priority, after
  // Expand World Data's and Better Continents' own (Patcher.LayerToggles): the result becomes lerp(result, ground, weight). Harmony runs a
  // postfix that returns a value after every void one, whatever their priorities say, which is what puts this one last.
  public static float GetBiomeHeightPostfix(float __result, float wx, float wy)
  {
    var pause = pauseOfThread;
    if (pause != null && Volatile.Read(ref pause.Depth) != 0)
      return __result;
    var ground = BakedLayerStore.Current?.Ground;
    return ground == null || !ground.Any ? __result : Apply(ground, wx, wy, __result);
  }

  /// <summary>The world's height at (x, z), with the layer's ground over it.</summary>
  internal static float Apply(GroundSet ground, double x, double z, float height)
  {
    if (!ground.TrySample(x, z, out float groundHeight, out float weight) || weight <= 0f)
      return height;
    return weight >= 1f ? groundHeight : height + (groundHeight - height) * weight;
  }

  // ---- the postfix steps aside --------------------------------------------------------------------------------------------------------

  // A pause belongs to the thread that made it. A world export pauses on each of the threads that sample its heights; the terrain builder, the
  // minimap and every other thread of the running game keep the layer's ground meanwhile (a pause for all threads would take the towns' ground
  // out of the game for as long as an export runs).
  private sealed class PauseOfThread
  {
    public int Depth;
  }

  [ThreadStatic] private static PauseOfThread? pauseOfThread;

  /// <summary>The postfix gives the world's own height, on the calling thread, until what this returns is disposed. A world export samples
  /// heights without the layer's ground (build spec 3.3): a world made from the export would otherwise blend the ground twice in its 8 m
  /// band. They nest. Dispose from any thread ends the pause of the thread that made it, once.</summary>
  public static IDisposable Pause()
  {
    var pause = pauseOfThread ??= new PauseOfThread();
    Interlocked.Increment(ref pause.Depth);
    return new PauseScope(pause);
  }

  private sealed class PauseScope(PauseOfThread pause) : IDisposable
  {
    private int disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref disposed, 1) == 0)
        Interlocked.Decrement(ref pause.Depth);
    }
  }

  /// <summary>Whether the postfix is stepping aside on the calling thread (for the offline tests).</summary>
  internal static bool IsPaused => pauseOfThread is { } pause && Volatile.Read(ref pause.Depth) != 0;

  // ---- the ground changes in a running world (build spec 8.3) ------------------------------------------------------------------------

  // The calls this makes into the game, as fields for the offline tests to stand in for.
  // The zone terrain that is loaded, each with the zone it covers (the distant terrain is not in the list: PokeDistant has it).
  internal static Func<IEnumerable<(ZoneKey Zone, Heightmap Terrain)>> LoadedTerrain = () =>
    Heightmap.s_heightmaps.Where(hm => hm != null && !hm.IsDistantLod).Select(hm => (ZoneKey.OfPoint(hm.transform.position), hm)).ToList();
  internal static Action<Heightmap> PokeTerrain = hm =>
  {
    hm.m_buildData = null;
    hm.Poke(1);
  };
  internal static Action PokeDistant = () => GameUtils.PokeDistantTerrain(Heightmap.Instances, hm => PokeTerrain(hm));
  internal static Action ClearBuilds = GameUtils.ClearReadyBuilds;
  internal static Action<Vector3, float> ResetGrass = (centre, radius) => ClutterSystem.instance?.ResetGrass(centre, radius);
  internal static Action DeleteMinimapCache = () =>
  {
    if (ZNet.World != null)
      Minimap.DeleteMapTextureData(ZNet.World.m_name);
  };
  internal static Func<bool> RunsWorld = () => ZNet.instance != null && ZNet.instance.IsServer();

  /// <summary>Starts listening for a new layer (BakedTransfer.SessionStarts does it at every session, before the game's own scene):
  /// idempotent.</summary>
  internal static void Subscribe()
  {
    BakedLayerStore.Changed -= OnLayerChanged;
    BakedLayerStore.Changed += OnLayerChanged;
  }

  /// <summary>After a new layer is current (BakedLayerStore.Changed): the terrain that is loaded in a zone whose ground or paint changed was
  /// built from the old layer, so it is built again, and the grass there is cut again; the machine that runs the world deletes its minimap
  /// cache, which is checked against the seed only. (Player terrain edits are stored relative to the base heights, so edited ground moves
  /// with a changed base: build spec 15.1, risk 13.)</summary>
  internal static void OnLayerChanged(BakedLayer? from, BakedLayer? to, ZoneKey[]? changed)
  {
    try
    {
      var zones = TerrainChanges(from, to, changed);
      if (zones.Count == 0)
        return;
      BetterContinents.Log($"The baked layer's ground or paint changed in {zones.Count} zone(s): the terrain there is built again.");
      PokeLoaded(zones);
      if (RunsWorld())
        DeleteMinimapCache();
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"The terrain could not be rebuilt for the baked layer's new ground: {e}");
    }
  }

  /// <summary>The zones whose ground or paint differs between two layers (either may be none): among the zones a change names, or in every
  /// zone either layer has a ground or paint in when it names none (a first layer, a world change).</summary>
  internal static HashSet<ZoneKey> TerrainChanges(BakedLayer? from, BakedLayer? to, ZoneKey[]? changed)
  {
    var candidates = new HashSet<ZoneKey>();
    if (changed != null)
      foreach (var zone in changed)
        candidates.Add(zone);
    else
      foreach (var layer in new[] { from, to })
        if (layer != null)
          foreach (var row in layer.Zones)
            if (row.HasGround || row.HasPaint)
              candidates.Add(row.Key);
    var result = new HashSet<ZoneKey>();
    foreach (var zone in candidates)
      if (GroundDiffers(from, to, zone) || PaintDiffers(from, to, zone))
        result.Add(zone);
    return result;
  }

  private static bool GroundDiffers(BakedLayer? a, BakedLayer? b, ZoneKey zone)
  {
    ZoneGround? ga = null, gb = null;
    if (a != null)
      a.Ground.TryGet(zone, out ga);
    if (b != null)
      b.Ground.TryGet(zone, out gb);
    if (ReferenceEquals(ga, gb))
      return false;
    if (ga == null || gb == null)
      return true;
    return ga.Base != gb.Base || !ga.Heights.AsSpan().SequenceEqual(gb.Heights) || !ga.Weights.AsSpan().SequenceEqual(gb.Weights);
  }

  private static bool PaintDiffers(BakedLayer? a, BakedLayer? b, ZoneKey zone)
  {
    byte[]? pa = PaintOf(a, zone), pb = PaintOf(b, zone);
    if (pa == null && pb == null)
      return false;
    if (pa == null || pb == null)
      return true;
    return !pa.AsSpan().SequenceEqual(pb);
  }

  private static byte[]? PaintOf(BakedLayer? layer, ZoneKey zone) =>
    layer != null && layer.TryGetZoneRow(zone, out var row) && row.HasPaint ? layer.Decode(row).Paint : null;

  // The loaded terrain of those zones is poked, the distant terrain too (it samples the same ground, coarsely), and a frame later the
  // builds that were under way and the grass are dealt with (GameUtils.DropStaleTerrain's reasoning: a delayed Poke runs in this frame's
  // LateUpdate, and the grass is cut from the ground as it is after that).
  private static void PokeLoaded(HashSet<ZoneKey> zones)
  {
    int poked = 0;
    foreach (var (zone, terrain) in LoadedTerrain())
    {
      if (!zones.Contains(zone))
        continue;
      PokeTerrain(terrain);
      poked++;
    }
    if (poked > 0)
      PokeDistant();
    ClearBuilds();
    var host = BetterContinents.instance;
    if (host)
      host.StartCoroutine(AfterTheFrame(zones));
  }

  private static IEnumerator AfterTheFrame(HashSet<ZoneKey> zones)
  {
    yield return null;
    ClearBuilds();
    foreach (var zone in zones)
      ResetGrass(zone.Centre, 46f);
  }
}

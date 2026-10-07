// Added by Wubarrk on 2026-10-07 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterContinents;

// The colour of the Ashlands sea follows the world's own hot sea.
//
// The game's water shader (Custom/Water) draws the Ashlands sea by blending five colours of the water material towards
// five _Ashlands ones, by a mask it works out from the world position alone: the game's Ashlands ring, 12000 m (plus
// 100 m x WorldAngle) from (0, 4000), with an edge 25 m wide broken up by the foam texture. Nothing in the game's code
// feeds it (1.0.17: WaterVolume sets only _WaterTime, _depth and _UseGlobalWind), so a world whose hot sea is somewhere
// else (a heat map, or the ring laid out to a bigger world) was drawn red where the game's ring is and plain where its
// own Ashlands is: on a world of 32 km, red canals 20 km from the centre with a cold heat map.
//
// Two kinds of renderer draw the sea, and the colours can be set for each one (a MaterialPropertyBlock):
// - each zone's water surface (WaterVolume, 64 m), set when it starts and again when the settings change;
// - the far sea past the loaded zones: the game's Water, under _GameMain's WaterPlane, which follows the camera and keeps
//   its own block (_VisibleMaxDistance, which hides it near the player). It is set again whenever it has moved FarStep.
// The world's hot sea is where WorldGenerator.GetAshlandsOceanGradient is above 0, the same test that heats a swimmer and
// burns a ship. It is compared with the game's ring at points of the sea a renderer draws. Where the two agree, the
// renderer keeps the game's colours and the shader's own edge. Where they do not, both sets of colours are set to one
// blend, by the share of that sea that is hot, so the shader's mask no longer matters there. So the edge of a hot sea that
// is not the game's ring is drawn in steps of a zone (64 m), softened by that share, and the far sea takes one blend for
// all the sea around it.
//
// The shader's other ring, the calm sea of the Deep North, sets the height of the waves it draws and cannot be moved:
// WorldGenerator.DeepNorthWaveFade, which floats boats and fish on those waves, is left as the game's to match it
// (Patcher, WorldSizePatch).
internal static class AshlandsWater
{
  // Points per side of a zone's water surface (a narrow canal is still met: 7 m apart on 64 m).
  private const int Samples = 9;
  // The far sea is worked out again when it has moved this far.
  private const float FarStep = 128f;
  // Where the far sea is looked at: rings around the camera, out to about where the fog hides it.
  private static readonly float[] FarRadii = [300f, 600f, 1000f, 1500f, 2100f];
  private const int FarDirections = 16;

  private static bool? headless;
  private static bool Headless => headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

  // The water surfaces that have started, so that Refresh needs nothing of the game's: DynamicPatch also runs without the engine
  // (the offline tests), where WaterVolume's own statics (Shader.PropertyToID) cannot run.
  private static readonly List<WaterVolume> Volumes = [];
  // Settings changes, so the far sea is worked out again after one; and where it was worked out last.
  private static int generation;
  private static readonly Dictionary<Water, (Vector3 At, int Generation)> FarDone = [];

  [HarmonyPatch(typeof(WaterVolume), "Start")]
  private static class WaterVolumeStart
  {
    [HarmonyPostfix]
    private static void Postfix(WaterVolume __instance)
    {
      if (Headless)
        return;
      // Surfaces go with their zones: the ones destroyed since are dropped now and then.
      if (Volumes.Count % 256 == 255)
        Prune();
      Volumes.Add(__instance);
      Apply(__instance);
    }
  }

  /// <summary>DynamicPatch: a world was loaded or left, or its settings changed. After the patches are switched, as the
  /// hot sea is what they make it.</summary>
  internal static void Refresh()
  {
    generation++;
    if (Volumes.Count == 0)
      return;
    Prune();
    foreach (var volume in Volumes)
      Apply(volume);
  }

  /// <summary>Every frame (BetterContinents.Update): the far sea, where it is now.</summary>
  internal static void Tick()
  {
    var far = Water.Instances;
    if (far.Count == 0 || Headless)
      return;
    foreach (var water in far)
    {
      if (water == null)
        continue;
      var at = water.transform.position;
      if (FarDone.TryGetValue(water, out var done) && done.Generation == generation && (at - done.At).sqrMagnitude < FarStep * FarStep)
        continue;
      FarDone[water] = (at, generation);
      ApplyFar(water, at);
    }
  }

  private static void Prune()
  {
    Volumes.RemoveAll(v => v == null);
    Gpu.Changed.RemoveWhere(r => r == null);
    if (FarDone.Count > 0)
      foreach (var water in new List<Water>(FarDone.Keys))
        if (water == null)
          FarDone.Remove(water!);
  }

  private static bool Active => BetterContinents.Settings is { EnabledForThisWorld: true } && WorldGenerator.instance != null;

  private static void Apply(WaterVolume volume)
  {
    if (Headless || volume == null || volume.m_waterSurface is not { } surface)
      return;
    var material = surface.sharedMaterial;
    if (material == null || !material.HasProperty(Gpu.HotColours[0]))
      return;
    float share = -1f;
    if (Active)
    {
      var bounds = surface.bounds;
      var points = new List<Vector2>(Samples * Samples);
      for (int i = 0; i < Samples; i++)
      for (int j = 0; j < Samples; j++)
        points.Add(new Vector2(bounds.min.x + bounds.size.x * (i + 0.5f) / Samples, bounds.min.z + bounds.size.z * (j + 0.5f) / Samples));
      share = HotShare(points, volume.transform.position.y);
    }
    if (share < 0f)
    {
      // The game's colours and mask. Nothing else sets a property block on a zone's water (the game sets its values on the
      // material itself).
      if (Gpu.Changed.Remove(surface))
        surface.SetPropertyBlock(null);
      return;
    }
    SetColours(surface, material, share);
    Gpu.Changed.Add(surface);
  }

  private static void ApplyFar(Water water, Vector3 at)
  {
    if (water.GetComponent<MeshRenderer>() is not { } surface)
      return;
    var material = surface.sharedMaterial;
    if (material == null || !material.HasProperty(Gpu.HotColours[0]))
      return;
    float share = -1f;
    if (Active)
    {
      var points = new List<Vector2>(FarRadii.Length * FarDirections);
      foreach (var r in FarRadii)
        for (int k = 0; k < FarDirections; k++)
        {
          double a = 2.0 * Math.PI * (k + 0.5) / FarDirections;
          points.Add(new Vector2(at.x + r * (float)Math.Cos(a), at.z + r * (float)Math.Sin(a)));
        }
      share = HotShare(points, at.y);
    }
    // The block also holds the game's _VisibleMaxDistance (Water.ApplySettings), so it is never cleared: the game's own
    // colours are set back instead, and the shader's mask decides again.
    SetColours(surface, material, share);
  }

  private static void SetColours(MeshRenderer surface, Material material, float share)
  {
    var block = Gpu.Block;
    surface.GetPropertyBlock(block);
    for (int i = 0; i < Gpu.PlainColours.Length; i++)
    {
      Color plain = material.GetColor(Gpu.PlainColours[i]), hot = material.GetColor(Gpu.HotColours[i]);
      if (share < 0f)
      {
        block.SetColor(Gpu.PlainColours[i], plain);
        block.SetColor(Gpu.HotColours[i], hot);
      }
      else
      {
        var colour = Color.Lerp(plain, hot, share);
        block.SetColor(Gpu.PlainColours[i], colour);
        block.SetColor(Gpu.HotColours[i], colour);
      }
    }
    surface.SetPropertyBlock(block);
  }

  /// <summary>The share of the sea among the points that is hot by the world's rule, or -1 where the game's ring already says
  /// the same at every point. Only points where the ground is below the water count as sea; when none is (water narrower
  /// than the points' spacing), every point counts.</summary>
  private static float HotShare(List<Vector2> points, float surfaceY)
  {
    var hot = new bool[points.Count];
    bool differs = false;
    for (int i = 0; i < points.Count; i++)
      differs |= Differs(points[i].x, points[i].y, out hot[i]);
    if (!differs)
      return -1f;
    var generator = WorldGenerator.instance;
    int sea = 0, hotSea = 0, hotAll = 0;
    for (int i = 0; i < points.Count; i++)
    {
      if (hot[i])
        hotAll++;
      // Ground above the water is not sea: the lava of a heat map ends at its shore.
      if (generator.GetHeight(points[i].x, points[i].y) >= surfaceY)
        continue;
      sea++;
      if (hot[i])
        hotSea++;
    }
    return sea > 0 ? (float)hotSea / sea : (float)hotAll / points.Count;
  }

  /// <summary>Whether the world's hot sea (GetAshlandsOceanGradient, as Better Continents makes it) and the game's ring,
  /// which the shader draws, disagree at a point.</summary>
  internal static bool Differs(float x, float z, out bool hot)
  {
    hot = WorldGenerator.GetAshlandsOceanGradient(x, z) > 0f;
    return hot != GameRing(x, z) > 0.0;
  }

  /// <summary>The game's own WorldGenerator.GetAshlandsOceanGradient (1.0.17), whose ring the shader draws, as it is
  /// before Better Continents moves it.</summary>
  internal static double GameRing(float x, float z)
  {
    float zc = z + WorldGenerator.ashlandsYOffset;
    double angle = WorldGenerator.WorldAngle(x, zc) * 100.0;
    return (Math.Sqrt((double)x * x + (double)zc * zc) - (WorldGenerator.ashlandsMinDistance + angle)) / 300.0;
  }

  // Unity's side, made when it is first used: the offline tests run the rest without the engine.
  private static class Gpu
  {
    internal static readonly int[] PlainColours =
    [
      Shader.PropertyToID("_ColorTop"), Shader.PropertyToID("_ColorBottom"), Shader.PropertyToID("_ColorBottomShallow"),
      Shader.PropertyToID("_SurfaceColor"), Shader.PropertyToID("_FoamColor"),
    ];
    internal static readonly int[] HotColours =
    [
      Shader.PropertyToID("_AshlandsColorTop"), Shader.PropertyToID("_AshlandsColorBottom"), Shader.PropertyToID("_AshlandsColorBottomShallow"),
      Shader.PropertyToID("_AshlandsSurfaceColor"), Shader.PropertyToID("_AshlandsFoamColor"),
    ];
    internal static readonly MaterialPropertyBlock Block = new();
    // The zone surfaces whose colours are set, so they can be given back.
    internal static readonly HashSet<Renderer> Changed = [];
  }
}

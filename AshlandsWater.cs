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
// Each zone's water surface has a material of its own (WaterVolume.SetupMaterial asks its renderer for .material), and
// the colours can be set for one renderer. The world's hot sea is where WorldGenerator.GetAshlandsOceanGradient is
// above 0, the same test that heats a swimmer and burns a ship. It is compared with the game's ring at a grid of points
// of the surface where there is sea. Where the two agree, the surface keeps the game's colours and the shader's own
// edge. Where they do not, both sets of colours are set to one blend, by the share of the sea there that is hot, so
// the shader's mask no longer matters. So the edge of a hot sea that is not the game's ring is drawn in steps of a
// surface (64 m), softened by that share.
//
// The shader's other ring, the calm sea of the Deep North, sets the height of the waves it draws and cannot be moved:
// WorldGenerator.DeepNorthWaveFade, which floats boats and fish on those waves, is left as the game's to match it
// (Patcher, WorldSizePatch).
internal static class AshlandsWater
{
  // Points per side of a water surface that are compared.
  private const int Samples = 5;

  private static bool? headless;
  private static bool Headless => headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

  // The water surfaces that have started, so that Refresh needs nothing of the game's: DynamicPatch also runs without the engine
  // (the offline tests), where WaterVolume's own statics (Shader.PropertyToID) cannot run.
  private static readonly List<WaterVolume> Volumes = [];

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
    if (Volumes.Count == 0)
      return;
    Prune();
    foreach (var volume in Volumes)
      Apply(volume);
  }

  private static void Prune()
  {
    Volumes.RemoveAll(v => v == null);
    Gpu.Changed.RemoveWhere(r => r == null);
  }

  private static void Apply(WaterVolume volume)
  {
    if (Headless || volume == null || volume.m_waterSurface is not { } surface)
      return;
    var material = surface.sharedMaterial;
    if (material == null || !material.HasProperty(Gpu.HotColours[0]))
      return;
    var settings = BetterContinents.Settings;
    float share = settings != null && settings.EnabledForThisWorld && WorldGenerator.instance != null
      ? HotShare(surface.bounds, volume.transform.position.y)
      : -1f;
    if (share < 0f)
    {
      // The game's colours and mask. No other renderer uses a property block on the water: the game sets its values on the material.
      if (Gpu.Changed.Remove(surface))
        surface.SetPropertyBlock(null);
      return;
    }
    var block = Gpu.Block;
    surface.GetPropertyBlock(block);
    for (int i = 0; i < Gpu.PlainColours.Length; i++)
    {
      var colour = Color.Lerp(material.GetColor(Gpu.PlainColours[i]), material.GetColor(Gpu.HotColours[i]), share);
      block.SetColor(Gpu.PlainColours[i], colour);
      block.SetColor(Gpu.HotColours[i], colour);
    }
    surface.SetPropertyBlock(block);
    Gpu.Changed.Add(surface);
  }

  /// <summary>The share of the sea under a water surface that is hot by the world's rule, or -1 where the game's ring
  /// already says the same everywhere (or there is no sea).</summary>
  private static float HotShare(Bounds bounds, float surfaceY)
  {
    int sea = 0, hot = 0;
    bool differs = false;
    var generator = WorldGenerator.instance;
    for (int i = 0; i < Samples; i++)
    for (int j = 0; j < Samples; j++)
    {
      float x = bounds.min.x + bounds.size.x * (i + 0.5f) / Samples;
      float z = bounds.min.z + bounds.size.z * (j + 0.5f) / Samples;
      // Ground above the water is not sea: the lava of a heat map ends at its shore.
      if (generator.GetHeight(x, z) >= surfaceY)
        continue;
      sea++;
      if (Differs(x, z, out bool isHot))
        differs = true;
      if (isHot)
        hot++;
    }
    return sea == 0 || !differs ? -1f : (float)hot / sea;
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
    // The surfaces whose colours are set, so they can be given back.
    internal static readonly HashSet<Renderer> Changed = [];
  }
}

// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// 'bc water probe' (Debug Mode; the plan's 6.4 and 10.3): is the water that is DRAWN where the water that is FELT says it is?
//
// The drawn height of the zone water is whatever BC's vertex stage makes of the mesh (BCWaterDisplace, unity/Sea/BCWaterVertex.hlsl: the sea field's W, the
// same waves, the same clock); the felt height is WaterVolume.GetWaterSurface, which boats, swimmers and fish stand on. The probe asks the GPU for the
// first and the game's code for the second, at the same places, in the same frame, and reports how far they are apart.
//
// 81 places: a grid of 9 x 9, 8 m apart, in front of the main camera (8 to 72 m ahead, 32 m to each side), each moved to the nearest VERTEX of the zone
// water mesh there. (The mesh is a 32 x 32 grid over the zone, 2.07 m between vertices; between two vertices a triangle is flat, so a place between
// them would measure the mesh's own interpolation of the waves, not the shader. At a vertex the drawn height IS the shader's.) Each place gets a camera of
// its own for one render: an orthographic camera 2 cm wide looking straight down at the vertex from 500 m, drawing into a 1 x 1 RGBAFloat texture with
// BetterContinents/WaterProbe through Camera.RenderWithShader. The probe writes the world position of the displaced vertex it sees (rgb) and a = 1 for
// zone water; the far sea is hidden for the render (its flat plane would hide the troughs). The place is the pixel's: the x and z the probe writes are
// the ones the felt height is asked at.
//
// Places the probe does not use: where no zone water of the sea is drawn, where the water fades with the distance from the player (the last 20 m before
// _VisibleMaxDistance: the game's felt height has no fade) and near the world's edge (the sink).
//
// The answer is ONE line, on the console and in the BepInEx log, for scripts to read:
//   Water probe: n=<places used> median=<m> p99=<m> max=<m> (drawn minus felt, metres); camera <x> <z>
// median, p99 and max are of the absolute difference (p99 by linear interpolation between the two largest ranks); the targets are median <= 0.03 m
// and p99 <= 0.08 m. Anything the probe cannot do is "Water probe: cannot: <why>". Nothing is left behind: the camera, its texture and the far sea's
// rendering are put back (the water's own _WaterTime is set to the clock it would be set to this frame anyway).
internal static class SeaProbe
{
  internal const int Side = 9;
  internal const float Step = 8f;
  // A zone water mesh: 32 x 32 vertices over [-1, 1] x [-1, 1] of the object (libs-Tools WATER-SHADER-FACTS.md, section 9).
  internal const int Vertices = 32;

  // ---- the parts that need no engine -------------------------------------------------------------------------------------

  /// <summary>The 81 places in front of a camera at (x, z) looking along a heading (degrees from north, +z, towards +x): rows 1 to 9 of Step metres
  /// ahead, columns -4 to 4 of Step metres to the right. Row by row, nearest first.</summary>
  internal static (float X, float Z)[] Targets(float x, float z, float headingDegrees)
  {
    double a = headingDegrees * Math.PI / 180.0;
    double fx = Math.Sin(a), fz = Math.Cos(a);
    // the right of the heading: (+z, -x) of it
    double rx = fz, rz = -fx;
    var places = new (float, float)[Side * Side];
    for (int row = 0; row < Side; row++)
      for (int column = 0; column < Side; column++)
      {
        double ahead = Step * (row + 1), right = Step * (column - Side / 2);
        places[row * Side + column] = ((float)(x + fx * ahead + rx * right), (float)(z + fz * ahead + rz * right));
      }
    return places;
  }

  /// <summary>The coordinate of the vertex of a zone water mesh nearest to t along one axis: the mesh runs from center - half to center + half in
  /// Vertices steps, and the two outer columns are left out (they overlap the next zone's water by 0.2 m).</summary>
  internal static float SnapToVertex(float t, float center, float half)
  {
    int last = Vertices - 1;
    int k = (int)Math.Round(((t - center) / half + 1.0) * last / 2.0);
    k = Math.Min(Math.Max(k, 1), last - 1);
    return (float)(center + half * (-1.0 + 2.0 * k / last));
  }

  /// <summary>Median, 99th percentile and maximum of absolute differences, by linear interpolation between ranks (the median of an even count is
  /// the mean of the middle two). NaN for no differences.</summary>
  internal static (int N, double Median, double P99, double Max) Summarize(IEnumerable<double> differences)
  {
    var sorted = differences.Select(Math.Abs).OrderBy(d => d).ToArray();
    if (sorted.Length == 0)
      return (0, double.NaN, double.NaN, double.NaN);
    double At(double q)
    {
      double position = q * (sorted.Length - 1);
      int below = (int)Math.Floor(position);
      int above = Math.Min(below + 1, sorted.Length - 1);
      return sorted[below] + (sorted[above] - sorted[below]) * (position - below);
    }
    return (sorted.Length, At(0.5), At(0.99), sorted[sorted.Length - 1]);
  }

  /// <summary>The probe's answer line: "Water probe: n=62 median=0.004 p99=0.031 max=0.044 (drawn minus felt, metres); camera -774.2 9869.0".</summary>
  internal static string FormatLine(int n, double median, double p99, double max, float cameraX, float cameraZ)
  {
    string F(double v, string format) => double.IsNaN(v) ? "NaN" : v.ToString(format, CultureInfo.InvariantCulture);
    return $"Water probe: n={n} median={F(median, "0.000")} p99={F(p99, "0.000")} max={F(max, "0.000")} (drawn minus felt, metres); camera {F(cameraX, "0.0")} {F(cameraZ, "0.0")}";
  }

  internal static string Cannot(string why) => "Water probe: cannot: " + why;

  // ---- the engine's part --------------------------------------------------------------------------------------------------

  /// <summary>Runs the probe and says the one line (to output, and to the BepInEx log when alsoLog: a console command's output goes there already).</summary>
  internal static void Run(Action<string> output, bool alsoLog)
  {
    string line;
    try
    {
      line = Measure();
    }
    catch (Exception e)
    {
      line = Cannot($"{e.GetType().Name}: {e.Message}");
    }
    output(line);
    if (alsoLog)
      BetterContinents.Log(line);
  }

  private sealed class Surface(WaterVolume volume, Vector3 center, float half)
  {
    public readonly WaterVolume Volume = volume;
    public readonly Vector3 Center = center;
    public readonly float Half = half;
  }

  private static string Measure()
  {
    var main = Utils.GetMainCamera();
    if (main == null || WaterVolume.Instances.Count == 0)
      return Cannot("no world is loaded here");
    var shader = SeaDraw.ProbeShader(out var why);
    if (shader == null)
      return Cannot(why ?? "the probe shader is not there");
    if (!SeaDraw.On)
      return Cannot("Better Continents' own sea is not drawn here (bc water says why; bc water force on draws it in a world whose sea is the game's)");
    if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
      return Cannot("this graphics device has no float render texture");

    // The zone water of the sea that BC's shader draws, with its mesh's centre and half width.
    var surfaces = new List<Surface>();
    int mask = 0;
    var volumes = WaterVolume.Instances;
    for (int i = 0; i < volumes.Count; i++)
    {
      var v = volumes[i];
      if (v == null || v.m_heightmap == null || v.m_waterSurface == null || !SeaDraw.Draws(v.m_waterSurface))
        continue;
      var bounds = v.m_waterSurface.bounds;
      surfaces.Add(new Surface(v, bounds.center, bounds.extents.x));
      mask |= 1 << v.m_waterSurface.gameObject.layer;
    }
    var position = main.transform.position;
    if (surfaces.Count == 0)
      return FormatLine(0, double.NaN, double.NaN, double.NaN, position.x, position.z);

    // The places: each of the 81 on the nearest vertex of the zone water that covers it, and only where the water is drawn at full height.
    var reference = Shader.GetGlobalVector("_NetRefPos");
    var places = new List<(Surface Surface, float X, float Z)>();
    foreach (var (tx, tz) in Targets(position.x, position.z, main.transform.eulerAngles.y))
    {
      Surface? best = null;
      float nearest = float.MaxValue;
      foreach (var s in surfaces)
      {
        float d = Math.Max(Math.Abs(tx - s.Center.x), Math.Abs(tz - s.Center.z));
        if (d <= s.Half && d < nearest)
        {
          best = s;
          nearest = d;
        }
      }
      if (best == null)
        continue;
      float x = SnapToVertex(tx, best.Center.x, best.Half), z = SnapToVertex(tz, best.Center.z, best.Half);
      var material = best.Volume.m_waterSurface.sharedMaterial;
      float visible = material.HasProperty("_VisibleMaxDistance") ? material.GetFloat("_VisibleMaxDistance") : 0f;
      float edge = material.HasProperty("_WaterEdge") ? material.GetFloat("_WaterEdge") : float.MaxValue;
      // the waves fade out over the last 20 m before _VisibleMaxDistance from the reference position; the sink starts at _WaterEdge
      if (Math.Sqrt((x - reference.x) * (x - reference.x) + (z - reference.z) * (z - reference.z)) > visible - 20f - 2f || Math.Sqrt((double)x * x + (double)z * z) > edge - 1f)
        continue;
      places.Add((best, x, z));
    }

    var differences = new List<double>(places.Count);
    var hidden = new List<(Renderer Renderer, bool Was)>();
    GameObject? holder = null;
    RenderTexture? target = null;
    Texture2D? readback = null;
    var previousActive = RenderTexture.active;
    try
    {
      // The far sea's flat plane would hide the troughs of the zone water (it is not discarded under a replacement shader): off for the renders.
      foreach (var water in Water.Instances)
        if (water != null && water.GetComponent<MeshRenderer>() is { } far && far.sharedMaterial is { } m && m.HasProperty("_IsLod") && m.GetFloat("_IsLod") > 0.5f)
        {
          hidden.Add((far, far.forceRenderingOff));
          far.forceRenderingOff = true;
        }
      target = new RenderTexture(1, 1, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { antiAliasing = 1, name = "BCWaterProbe" };
      target.Create();
      readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true) { name = "BCWaterProbe" };
      holder = new GameObject("BCWaterProbe") { hideFlags = HideFlags.HideAndDontSave };
      var camera = holder.AddComponent<Camera>();
      camera.enabled = false;
      camera.orthographic = true;
      camera.orthographicSize = 0.01f;
      camera.aspect = 1f;
      camera.nearClipPlane = 1f;
      camera.farClipPlane = 1000f;
      camera.clearFlags = CameraClearFlags.SolidColor;
      camera.backgroundColor = Color.clear;
      camera.cullingMask = mask;
      camera.allowHDR = false;
      camera.allowMSAA = false;
      camera.useOcclusionCulling = false;
      camera.depthTextureMode = DepthTextureMode.None;
      camera.targetTexture = target;
      var clocked = new HashSet<WaterVolume>();
      foreach (var (surface, x, z) in places)
      {
        // The material holds the clock it was last given; the felt height is asked on the clock of this moment.
        if (clocked.Add(surface.Volume))
          surface.Volume.UpdateMaterials();
        holder.transform.SetPositionAndRotation(new Vector3(x, 500f, z), Quaternion.Euler(90f, 0f, 0f));
        camera.RenderWithShader(shader, "RenderType");
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
        var pixel = readback.GetPixel(0, 0);
        // not zone water, or not the vertex that was aimed at: not counted
        if (pixel.a < 0.5f || Math.Abs(pixel.r - x) > 0.1f || Math.Abs(pixel.b - z) > 0.1f)
          continue;
        float felt = surface.Volume.GetWaterSurface(new Vector3(pixel.r, pixel.g, pixel.b));
        differences.Add(pixel.g - felt);
      }
    }
    finally
    {
      RenderTexture.active = previousActive;
      foreach (var (renderer, was) in hidden)
        if (renderer != null)
          renderer.forceRenderingOff = was;
      if (target != null)
      {
        target.Release();
        UnityEngine.Object.Destroy(target);
      }
      if (readback != null)
        UnityEngine.Object.Destroy(readback);
      if (holder != null)
        UnityEngine.Object.Destroy(holder);
    }
    var summary = Summarize(differences);
    return FormatLine(summary.N, summary.Median, summary.P99, summary.Max, position.x, position.z);
  }
}

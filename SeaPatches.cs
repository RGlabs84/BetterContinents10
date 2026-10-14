// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// WHEN BC'S SEA IS ON, AND WHAT IT MAKES BOATS, SWIMMERS AND FISH FEEL (the plan's 6.2 and 6.3).
//
// A world's sea is the game's unless BC shapes the world and the world's Deep North or hot sea is not the game's circles: a biome map, a heat
// map, a world laid out to a size other than vanilla's, or Expand World Size. For those worlds (Candidate) the SeaField is built from the world's
// own rules (IsDeepnorth, GetAshlandsOceanGradient) and compared with the game's circles; where they are the same everywhere nothing changes.
//
// Felt side, the same rule on every machine (a boat's physics runs on whichever machine owns it, and ownership moves):
// - WorldGenerator.DeepNorthWaveFade answers 1 - W from the field. It feeds GetWaterSurface (everything that floats), Fish, GO's ray-traced
//   water and Player's debug line.
// - WaterVolume.CalcWave (the private overload, one wind) answers SeaWaves.One with W from the field, on the drawn clock.
// Both run the game's own code until the field is ready, and for good where the world's Deep North is the game's (SeaField.CalmSameAsGame): the
// felt rule depends on the Deep North alone, which a dedicated server (calm half only) knows as well as a client does. Whether a client also
// draws BC's shader (SeaDraw) additionally depends on the hot sea.
//
// Neither follows Own Water: that is a client's choice of what to draw (SeaDraw), and boats still float on the world's waves when it is off.
internal static class SeaPatches
{
  // 'bc water force on' (Debug Mode): BC's sea in a world where it would be off, for the parity check (a vanilla-size world without maps is no
  // candidate; a world whose field equals the game's is swapped nothing), so that BC's shader can be compared with the game's at the same places. The
  // world counts as a candidate, the field is built from its rules and SeaDraw draws BC's shader even when the field is the game's own; the felt side
  // stays as it is (the game's, in that case). Not saved and not a setting: 'force off' gives back the rules, and leaving the world resets it (Refresh).
  internal static bool ForcedOn;

  /// <summary>Whether BC's sea is a candidate for a world: BC shapes the world, together with any of a laid-out world whose World Size is not
  /// vanilla's (made since 0.10), a heat map with a scale above 0, a biome map, or Expand World Size installed (or 'bc water force on'). A
  /// vanilla-size world without maps, and a world that keeps the game's terrain, are none, so no field is built for them.</summary>
  internal static bool Candidate(BetterContinentsSettings s) =>
    s.ShapesWorld && (ForcedOn || s.HasBiomeMap || (s.HasHeatMap && s.HeatMapScale > 0f) || !LayoutGeometry(s).IsVanilla || ExpandWorldSizeSizes);

  /// <summary>Why a world's sea is, or is not, a candidate, in words.</summary>
  internal static string Why(BetterContinentsSettings s)
  {
    if (!s.ShapesWorld)
      return s.EnabledForThisWorld ? "this world keeps the game's own terrain" : "Better Continents is off for this world, or no world is loaded";
    if (ForcedOn)
      return "forced on ('bc water force on')";
    var reasons = new List<string>();
    if (s.HasBiomeMap) reasons.Add("a biome map");
    if (s.HasHeatMap && s.HeatMapScale > 0f) reasons.Add("a heat map");
    if (!LayoutGeometry(s).IsVanilla) reasons.Add("a world laid out to its own size");
    if (ExpandWorldSizeSizes) reasons.Add("Expand World Size");
    return reasons.Count > 0 ? "the world has " + string.Join(", ", reasons) : "a vanilla-size world without maps: the game's own sea";
  }

  // ---- the two felt patches ---------------------------------------------------------------------------------------

  // WorldGenerator.DeepNorthWaveFade(float, float): the fade is 1 - W.
  internal static bool DeepNorthWaveFadePrefix(float wx, float wy, ref double __result)
  {
    var field = SeaField.Current;
    if (field == null || field.CalmSameAsGame)
      return true;
    __result = 1.0 - field.Waves(wx, wy);
    return false;
  }

  // WaterVolume.CalcWave(Vector3, float, Vector4, float, float, float): one wind's waves, on the drawn clock and with the field's W. The fade the
  // caller passed (waveFactorBig) is the game's own and is not read: the field is the one rule.
  internal static bool CalcWavePrefix(Vector3 worldPos, float depth, Vector4 wind, float waterTime, float waveFactor, ref float __result)
  {
    var field = SeaField.Current;
    if (field == null || field.CalmSameAsGame)
      return true;
    float clock = SeaWaves.DrawnClock;
    __result = SeaWaves.One(worldPos.x, worldPos.z, depth, wind, float.IsNaN(clock) ? waterTime : clock, waveFactor, field.Waves(worldPos.x, worldPos.z));
    return false;
  }

  // ---- the game's circles ------------------------------------------------------------------------------------------

  /// <summary>The game's own WorldGenerator.IsDeepnorth (1.0), whose circle the water shader draws as the calm sea, as it is before the
  /// layout moves it: a copy of its formula, 12000 m from (0, -4000) plus 100 m x WorldAngle.</summary>
  internal static bool GameDeep(float x, float z)
  {
    float num = (float)((double)WorldGenerator.WorldAngle(x, z) * 100.0);
    return new Vector2(x, (float)((double)z + 4000.0)).magnitude > (float)(12000.0 + (double)num);
  }

  /// <summary>The game's own WorldGenerator.GetAshlandsOceanGradient (1.0) above 0, whose ring the shader draws as the Ashlands' red, as it is
  /// before the layout or a heat map moves it: a copy of its formula, 12000 m from (0, 4000).</summary>
  internal static bool GameHot(float x, float z)
  {
    double num = (double)WorldGenerator.WorldAngle(x, z + WorldGenerator.ashlandsYOffset) * 100.0;
    return (float)(((double)DUtils.Length(x, z + WorldGenerator.ashlandsYOffset) - ((double)WorldGenerator.ashlandsMinDistance + num)) / 300.0) > 0f;
  }

  // ---- the field's life -------------------------------------------------------------------------------------------

  private static CancellationTokenSource? build;
  private static int generation;
  private static bool pending;
  // A dedicated server builds when its first WaterVolume starts.
  private static bool waterStarted;
  private static string state = "no world";
  private static string? failure;
  // A client's field was built before the ground's heights could be asked (the world generator is made while the world loads, after DynamicPatch):
  // its depth grid is made when they can be.
  private static bool depthPending;
  private static int builtWorkers;

  /// <summary>DynamicPatch, after the toggles: the world, or its settings, changed. A build in flight stops, the field is dropped (the game's
  /// values apply until the new one is ready), and a new one is started on the next frame (Tick) if the world's sea is a candidate. Engine-free.</summary>
  internal static void Refresh()
  {
    Interlocked.Increment(ref generation);
    build?.Cancel();
    build = null;
    SeaField.Current = null;
    failure = null;
    depthPending = false;
    var settings = BetterContinents.Settings;
    // Leaving the world (the main menu, a world BC does not shape) ends the force.
    if (!settings.ShapesWorld)
      ForcedOn = false;
    pending = Candidate(settings);
    state = pending ? "the field is waiting to be built" : "off: " + Why(settings);
    if (pending)
      NoteForeignPatches();
  }

  /// <summary>Every frame (BetterContinents.Update): starts the build that Refresh asked for.</summary>
  internal static void Tick()
  {
    if (depthPending)
      StartDepth(null);
    // The world's edge is looked at twice a second or so, not every frame.
    if (!pending && (++ticks % 30 != 0 || !RadiusMoved(BetterContinents.Settings)))
      return;
    var net = ZNet.instance;
    bool dedicated = net != null && net.IsDedicated();
    if (dedicated && !waterStarted)
      return;
    Start(BetterContinents.Settings, !dedicated && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null);
  }

  // The radius the field in flight, or ready, was started for.
  private static float builtRadius;
  private static int ticks;

  /// <summary>Expand World Size sends its size after a world's settings are loaded (BetterContinents.SetSize), which can be after DynamicPatch: a
  /// field started for the radius the settings alone gave covers the wrong edge. When the world's edge is not what the field was started for, it is
  /// started again (as Refresh does). True when it was.</summary>
  internal static bool RadiusMoved(BetterContinentsSettings settings)
  {
    if (pending || (build == null && SeaField.Current == null) || !Candidate(settings))
      return false;
    if (SeaField.ClampRadius(FieldRadius(settings)) == builtRadius)
      return false;
    Log($"Water: the world's edge is now +-{FieldRadius(settings):0} m (it was +-{builtRadius:0} m when the sea field was started): the field is built again.");
    Refresh();
    return true;
  }

  /// <summary>Whether the field is waiting for the ground's heights to make its depth grid.</summary>
  internal static bool DepthPending => depthPending;

  /// <summary>Makes the depth grid of a ready client field once the ground's heights can be asked (the world generator exists), on a worker.
  /// height: the heights to ask (the offline tests'); null is the world generator's.</summary>
  internal static void StartDepth(Func<float, float, float>? height)
  {
    var field = SeaField.Current;
    var source = build;
    if (field == null || source == null || field.Hot == null || field.Depth != null)
      return;
    height ??= WorldGenerator.instance is { } generator ? generator.GetHeight : null;
    if (height == null)
      return;
    depthPending = false;
    int workers = builtWorkers;
    Task.Run(() =>
    {
      try
      {
        field.BuildDepth(height, workers, source.Token);
        Log($"Water: the far sea's depth grid is ready: {field.DepthSize} x {field.DepthSize} texels of {SeaField.DepthSpacing:0} m.");
      }
      catch (OperationCanceledException)
      {
      }
      catch (Exception e)
      {
        LogError($"Water: making the far sea's depth grid failed ({e.GetType().Name}: {e.Message}); the far sea is drawn as the game draws it.");
      }
    });
  }

  /// <summary>Builds the field now, on a worker (Tick's work, for the offline tests too).</summary>
  internal static void Start(BetterContinentsSettings settings, bool hot)
  {
    pending = false;
    SeaFieldInput input;
    try
    {
      input = Input(settings, hot);
    }
    catch (Exception e)
    {
      Fail(e);
      return;
    }
    builtRadius = SeaField.ClampRadius(input.Radius);
    builtWorkers = input.Workers;
    var source = new CancellationTokenSource();
    build = source;
    int mine = Volatile.Read(ref generation);
    state = "building the field";
    Task.Run(() =>
    {
      try
      {
        var field = SeaField.Build(input, hot, source.Token);
        if (source.IsCancellationRequested || Volatile.Read(ref generation) != mine)
          return;
        SeaField.Current = field;
        depthPending = hot && field.Depth == null && (!field.SameAsGame || ForcedOn);
        state = field.SameAsGame ? (ForcedOn ? "on (forced: this world's sea is the game's own, and BC's shader is drawn all the same)" : "on, but this world's sea is the game's own")
          : field.CalmSameAsGame ? "on (the hot sea only differs from the game's)" : "on";
        Log($"Water: the sea field is ready: {field}. {field.CalmShare:P1} of the world is Deep North, {(field.Hot != null ? $"{field.HotShare:P1} is hot sea, " : "")}"
          + $"its Deep North {(field.CalmSameAsGame ? "is" : "is not")} the game's circle{(field.Hot != null ? $" and its hot sea {(field.HotSameAsGame ? "is" : "is not")} the game's ring" : "")}"
          + (field.SameAsGame ? ", so the game's own sea stays." : "."));
      }
      catch (OperationCanceledException)
      {
      }
      catch (Exception e)
      {
        if (!source.IsCancellationRequested)
          Fail(e);
      }
    });
  }

  private static void Fail(Exception e)
  {
    failure = e.GetType().Name + ": " + e.Message;
    state = "failed: " + failure;
    LogError($"Water: building the sea field failed ({failure}); the game's own sea stays, drawn and felt.");
  }

  // What the field is built from. Threads: all but one core, unless another mod patches the functions it asks (it may not be thread safe: one).
  internal static SeaFieldInput Input(BetterContinentsSettings settings, bool hot)
  {
    var input = new SeaFieldInput
    {
      Radius = FieldRadius(settings),
      GameDeep = GameDeep,
      GameHot = GameHot,
    };
    if (OthersPatch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.IsDeepnorth), [typeof(float), typeof(float)]))
        || hot && OthersPatch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetAshlandsOceanGradient), [typeof(float), typeof(float)])))
      input.Workers = 1;
    if (hot && WorldGenerator.instance is { } generator)
      input.Height = generator.GetHeight;
    return input;
  }

  /// <summary>The world's edge, which the field spans: World Size + Edge Size (Expand World Size's when it sets one), or the maps' span with the
  /// edge drop-off off.</summary>
  internal static float FieldRadius(BetterContinentsSettings settings)
  {
    float radius = settings.DisableMapEdgeDropoff ? Geometry.TotalRadius : WorldSectors.Reach(settings, ExpandWorldSizeGeometry);
    if (!(radius > 0f))
      radius = Geometry.TotalRadius;
    return SeaField.ClampRadius(radius);
  }

  private static bool OthersPatch(MethodBase? method)
  {
    var ours = HarmonyInstance?.Id;
    if (method == null || ours == null)
      return false;
    var info = Harmony.GetPatchInfo(method);
    return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Any(p => p.owner != ours);
  }

  // ---- other mods' patches on the waves ---------------------------------------------------------------------------

  private static readonly HashSet<string> Noted = [];

  // A prefix or transpiler of another mod on the methods the waves go through can make what is drawn differ from what boats feel. Said once each.
  private static void NoteForeignPatches()
  {
    var ours = HarmonyInstance?.Id;
    if (ours == null)
      return;
    foreach (var (name, method) in Watched())
    {
      var info = method == null ? null : Harmony.GetPatchInfo(method);
      if (info == null)
        continue;
      foreach (var (kind, patch) in info.Prefixes.Select(p => ("prefix", p)).Concat(info.Transpilers.Select(p => ("transpiler", p))))
        if (patch.owner != ours && Noted.Add(name + kind + patch.owner))
          LogWarning($"Water: {patch.owner} has a {kind} on {name}: the drawn waves may not match what boats feel.");
    }
  }

  private static IEnumerable<(string, MethodBase?)> Watched()
  {
    yield return ("WaterVolume.GetWaterSurface", AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface)));
    yield return ("WaterVolume.CalcWave", AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.CalcWave), [typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float)]));
    yield return ("WaterVolume.CalcWave", CalcWaveTarget());
    yield return ("WorldGenerator.DeepNorthWaveFade", AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.DeepNorthWaveFade), [typeof(float), typeof(float)]));
  }

  /// <summary>WaterVolume's private CalcWave, the one for one wind.</summary>
  internal static MethodBase? CalcWaveTarget() =>
    AccessTools.Method(typeof(WaterVolume), "CalcWave", [typeof(Vector3), typeof(float), typeof(Vector4), typeof(float), typeof(float), typeof(float)]);

  // A dedicated server builds its field (the calm half) when its first zone water starts: until then it has no use for it.
  [HarmonyPatch(typeof(WaterVolume), "Start")]
  private static class WaterVolumeStart
  {
    [HarmonyPostfix]
    private static void Postfix() => waterStarted = true;
  }

  // ---- what 'bc water' says ---------------------------------------------------------------------------------------

  internal static void Report(Action<string> output)
  {
    var settings = BetterContinents.Settings;
    if (ForcedOn)
      output("Water: FORCE IS ON ('bc water force on'): BC's sea is a candidate here whatever the world's rules say, and BC's shader is drawn even where the field is the game's own; "
        + "the felt waves stay as they are. 'bc water force off' (or leaving the world) gives back the rules.");
    output($"Water: BC's sea is {(Candidate(settings) ? "a candidate" : "not a candidate")} for this world ({Why(settings)}).");
    output($"Water: the sea field: {state}.");
    var field = SeaField.Current;
    if (field != null)
    {
      output($"Water: {field}.");
      output($"Water: {field.CalmShare:P1} of the world is Deep North (calm), {(field.Hot != null ? $"{field.HotShare:P1} is hot sea (red), " : "no hot channel here, ")}"
        + $"its Deep North {(field.CalmSameAsGame ? "is" : "is not")} the game's circle{(field.Hot != null ? $", its hot sea {(field.HotSameAsGame ? "is" : "is not")} the game's ring" : "")}.");
      output(field.CalmSameAsGame
        ? "Water: boats, swimmers and fish feel the game's own waves."
        : "Water: boats, swimmers and fish feel the world's waves (the field's W, the drawn clock).");
      if (field.Depth != null)
        output($"Water: the far sea's depth grid: {field.DepthSize} x {field.DepthSize} texels of {SeaField.DepthSpacing:0} m.");
    }
    foreach (var (name, method) in Watched())
    {
      var info = method == null ? null : Harmony.GetPatchInfo(method);
      var ours = HarmonyInstance?.Id;
      if (info == null || ours == null)
        continue;
      foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Where(p => p.owner != ours))
        output($"Water: another mod, {patch.owner}, patches {name}: the drawn waves may not match what boats feel.");
    }
  }

  /// <summary>For the offline tests: the state words and whether a build is wanted or running.</summary>
  internal static (string State, bool Pending, bool Building) Snapshot => (state, pending, build != null);
}

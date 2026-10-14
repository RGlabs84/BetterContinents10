// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
//
// SeaPatches (SeaPatches.cs): which worlds are candidates for BC's own sea, the two felt toggles on the installed game's methods (installed and
// removed, what Harmony binds, the game's values before the field is ready and for good where the Deep North is the game's), and the field's life
// (a world that is not a candidate builds none, a refresh stops a build, a server builds the calm half), and other mods' patches on the waves.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private const string FadeToggle = "WorldGenerator.DeepNorthWaveFade (BC's sea)";
  private const string WaveToggle = "WaterVolume.CalcWave (BC's sea)";

  private static void PatchTests()
  {
    var settings = BC.Settings;
    var harmony = BC.HarmonyInstance;
    try
    {
      CandidateTests();
      ToggleTests();
      PrefixTests();
      LifeTests();
      ForeignTests();
      DrawTests();
      ForceTests();
      ProbeTests();
    }
    finally
    {
      SeaField.Current = null;
      SeaPatches.ForcedOn = false;
      SeaWaves.Clock = null;
      BC.Settings = settings;
      BC.HarmonyInstance = harmony;
      SeaPatches.Refresh();
    }
  }

  // ------------------------------------------------------------------------------------------------ which worlds
  private static void CandidateTests()
  {
    Section("patches: which worlds are candidates for BC's sea");
    if (!running) return;
    var ews = EWS.Installed;
    var ewsSize = BC.ExpandWorldSizeGeometry;
    try
    {
      EWS.Installed = false;
      BC.ExpandWorldSizeGeometry = null;
      Check(!SeaPatches.Candidate(World()), "a vanilla-size world without maps is no candidate: no field is built for it");
      Check(!SeaPatches.Candidate(World(11, 20000f, 500f)), "nor is an older world (version 11) of another size without maps: its circles are not scaled, so its sea is the game's");
      Check(SeaPatches.Candidate(World(12, 20000f, 500f)) && SeaPatches.Candidate(World(12, 5000f, 250f)),
        "a world laid out to another size (version 12, World Size 20000 or 5000) is");
      Check(!SeaPatches.Candidate(World(12, 20000f, 500f, enabled: false)), "Better Continents off for the world: no");
      var gameTerrain = World(12, 20000f, 500f);
      gameTerrain.GameTerrain = true;
      Check(!SeaPatches.Candidate(gameTerrain), "a world that keeps the game's own terrain: no, whatever its size");
      var biome = World();
      SetMap(biome, "BiomeMap", typeof(ImageMapBiome));
      Check(SeaPatches.Candidate(biome), "a vanilla-size world with a biome map is");
      var heat = World();
      SetMap(heat, "HeatMap", typeof(ImageMapFloat));
      heat.HeatMapScale = 10f;
      Check(SeaPatches.Candidate(heat), "and one with a heat map");
      heat.HeatMapScale = 0f;
      Check(!SeaPatches.Candidate(heat), "unless the heat map's scale is 0");
      EWS.Installed = true;
      Check(SeaPatches.Candidate(World()) && !SeaPatches.Candidate(World(enabled: false)) && !SeaPatches.Candidate(gameTerrain),
        "with Expand World Size installed every world BC shapes is");
      EWS.Installed = false;
      SeaPatches.ForcedOn = true;
      Check(SeaPatches.Candidate(World()) && !SeaPatches.Candidate(gameTerrain) && !SeaPatches.Candidate(World(enabled: false)),
        "'bc water force on' makes a world BC shapes a candidate, and no other");
      Check(BC.WantedToggles(World()).Count(n => n.EndsWith("(BC's sea)")) == 2, "and the felt toggles are wanted for it (they run the game's code where its Deep North is the game's)");
      SeaPatches.ForcedOn = false;
      Check(!SeaPatches.Candidate(World()) && SeaPatches.Candidate(World(12, 20000f, 500f)), "'bc water force off' gives the rules back: a vanilla-size world is no candidate, a laid-out one is");
      SeaPatches.ForcedOn = true;
      Check(SeaPatches.Why(World()).Contains("forced on"), "a forced world says so");
      SeaPatches.ForcedOn = false;
      Check(SeaPatches.Why(World(12, 20000f, 500f)).Contains("laid out") && SeaPatches.Why(biome).Contains("biome map")
            && SeaPatches.Why(World()).Contains("game's own sea") && SeaPatches.Why(gameTerrain).Contains("game's own terrain"),
        "and each says why in words");
    }
    finally
    {
      EWS.Installed = ews;
      BC.ExpandWorldSizeGeometry = ewsSize;
      SeaPatches.ForcedOn = false;
    }
  }

  // ------------------------------------------------------------------------------------------------ the toggles
  private static object[] SeaToggles()
  {
    var all = (Array)typeof(BC).GetField("TogglesAfterWorldSize", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    return all.Cast<object>().Where(t => ((string)t.GetType().GetField("Name")!.GetValue(t)!).EndsWith("(BC's sea)")).ToArray();
  }

  private static string ToggleNameOf(object toggle) => (string)toggle.GetType().GetField("Name")!.GetValue(toggle)!;
  private static void UpdateToggle(object toggle, BC.BetterContinentsSettings s) => toggle.GetType().GetMethod("Update")!.Invoke(toggle, [s]);
  private static bool IsApplied(object toggle) => (bool)toggle.GetType().GetProperty("Applied")!.GetValue(toggle)!;

  private static IEnumerable<string> OurPrefixes(MethodBase target, Harmony harmony) =>
    Harmony.GetPatchInfo(target) is { } info ? info.Prefixes.Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod.Name) : [];

  private static void ToggleTests()
  {
    Section("patches: the two felt toggles");
    if (!running) return;
    var harmony = new Harmony("water-tests.toggles");
    BC.HarmonyInstance = harmony;
    var fade = AccessTools.Method(typeof(WorldGenerator), "DeepNorthWaveFade", XY);
    var calc = SeaPatches.CalcWaveTarget();
    var toggles = SeaToggles();
    Check(toggles.Length == 2 && toggles.Select(ToggleNameOf).OrderBy(n => n).SequenceEqual(new[] { FadeToggle, WaveToggle }.OrderBy(n => n)),
      "TogglesAfterWorldSize holds the two: DeepNorthWaveFade and CalcWave (BC's sea)");
    if (toggles.Length != 2) return;
    foreach (var toggle in toggles)
      Check((bool)toggle.GetType().GetProperty("ViaProcessor")!.GetValue(toggle)! && toggle.GetType().GetProperty("FailureMessage")!.GetValue(toggle) is string { Length: > 0 },
        $"{ToggleNameOf(toggle)}: patched through the processor, and a failure is logged and leaves the others");

    // The patch methods fit the game's methods: Harmony binds a prefix's parameters by name.
    var fadePrefix = AccessTools.Method(typeof(SeaPatches), nameof(SeaPatches.DeepNorthWaveFadePrefix));
    var calcPrefix = AccessTools.Method(typeof(SeaPatches), nameof(SeaPatches.CalcWavePrefix));
    foreach (var (prefix, target) in new[] { (fadePrefix, (MethodBase)fade), (calcPrefix, calc) })
    {
      var targetParameters = target.GetParameters().ToDictionary(p => p.Name, p => p.ParameterType);
      Check(prefix != null && prefix.ReturnType == typeof(bool) && prefix.GetParameters().All(p => p.Name == "__result" || (targetParameters.TryGetValue(p.Name, out var type) && type == p.ParameterType)),
        $"{prefix?.Name}: a bool prefix whose parameters are the game's {target.DeclaringType!.Name}.{target.Name}'s, by name and type");
    }

    var plain = World();
    var big = World(12, 20000f, 500f);
    var gameTerrain = World(12, 20000f, 500f);
    gameTerrain.GameTerrain = true;
    var biome = World();
    SetMap(biome, "BiomeMap", typeof(ImageMapBiome));
    foreach (var (world, wants, what) in new[]
             {
               (plain, false, "a vanilla-size world without maps"), (big, true, "a world of World Size 20000"), (biome, true, "a world with a biome map"),
               (gameTerrain, false, "a world that keeps the game's terrain"), (World(12, 20000f, 500f, enabled: false), false, "the main menu's"),
             })
    {
      var wanted = BC.WantedToggles(world).Where(n => n.EndsWith("(BC's sea)")).OrderBy(n => n).ToList();
      Check(wants ? wanted.SequenceEqual(new[] { FadeToggle, WaveToggle }.OrderBy(n => n)) : wanted.Count == 0, $"WantedToggles for {what}: {(wants ? "both" : "none")} ({string.Join(" | ", wanted)})");
    }

    int mark = CapturingLogHandler.Mark();
    try
    {
      foreach (var toggle in toggles)
        UpdateToggle(toggle, big);
      Check(toggles.All(IsApplied), "switched on for a candidate world, both are applied");
      Check(OurPrefixes(fade, harmony).SequenceEqual(["DeepNorthWaveFadePrefix"]) && OurPrefixes(calc, harmony).SequenceEqual(["CalcWavePrefix"]),
        "and Harmony holds one prefix of ours on DeepNorthWaveFade and one on the one-wind CalcWave");
      Check(!(Harmony.GetPatchInfo(fade)?.Postfixes.Any(p => p.owner == harmony.Id) ?? false) && !(Harmony.GetPatchInfo(calc)?.Transpilers.Any(p => p.owner == harmony.Id) ?? false),
        "and nothing else of ours on them");
      Check(!CapturingLogHandler.Since(mark).Any(l => l.Contains("Could not patch")), "no patch failed");

      // Through the real, patched game method. (0, 2500) is inside the game's own circle (calm only beyond 12000 m from (0, -4000)): fade 0.
      SeaField.Current = null;
      double game = WorldGenerator.DeepNorthWaveFade(0f, 2500f);
      Check(game == 0.0, "before the field is ready DeepNorthWaveFade answers the game's own (0 at (0, 2500))");
      var field = BuildTestField();
      SeaField.Current = field;
      double fadeNow = WorldGenerator.DeepNorthWaveFade(0f, 2500f);
      Check(Math.Abs(fadeNow - (1.0 - field.Waves(0f, 2500f))) < 1e-12 && fadeNow > 0.99,
        $"with the field ready it answers 1 - W from the field ({fadeNow:0.###} at (0, 2500), 1500 m into the test field's Deep North)");
      field.CalmSameAsGame = true;
      Check(WorldGenerator.DeepNorthWaveFade(0f, 2500f) == game, "where the world's Deep North is the game's, the game's answer stands");
      SeaField.Current = null;

      foreach (var toggle in toggles)
        UpdateToggle(toggle, plain);
      Check(toggles.All(t => !IsApplied(t)) && !OurPrefixes(fade, harmony).Any() && !OurPrefixes(calc, harmony).Any(), "for a world that is no candidate both are removed again");
    }
    finally
    {
      foreach (var toggle in toggles)
        UpdateToggle(toggle, plain);
      harmony.UnpatchAll(harmony.Id);
    }
  }

  // A small field of a made-up world: calm from 1000 m north.
  private static SeaField BuildTestField()
  {
    var input = new SeaFieldInput
    {
      Radius = 4000f,
      Deep = (x, z) => z > 1000f,
      Hot = (x, z) => z < -2000f,
      GameDeep = SeaPatches.GameDeep,
      GameHot = SeaPatches.GameHot,
      Workers = 2,
    };
    return SeaField.Build(input, true, CancellationToken.None);
  }

  // ------------------------------------------------------------------------------------------------ the prefixes
  private static void PrefixTests()
  {
    Section("patches: what the prefixes answer");
    if (!running) return;
    var wind = new Vector4(0.7f, 0f, 0.7f, 0.8f);
    var p = new Vector3(120f, 30f, 2500f);
    try
    {
      SeaField.Current = null;
      double fade = 0.25;
      Check(SeaPatches.DeepNorthWaveFadePrefix(0f, 2500f, ref fade) && fade == 0.25, "no field yet: the fade prefix runs the game's code and leaves its result alone");
      float wave = 0.5f;
      Check(SeaPatches.CalcWavePrefix(p, 1f, wind, 100f, 1f, ref wave) && wave == 0.5f, "no field yet: the wave prefix runs the game's code too");

      var field = BuildTestField();
      SeaField.Current = field;
      Check(!SeaPatches.DeepNorthWaveFadePrefix(0f, 2500f, ref fade) && Math.Abs(fade - (1.0 - field.Waves(0f, 2500f))) < 1e-12,
        $"with the field: the fade is 1 - W and the game's code is skipped ({fade:0.###} at (0, 2500), 1500 m into the test Deep North)");
      Check(fade > 0.99, "(calm there)");
      SeaWaves.Clock = () => 777.25f;
      Check(!SeaPatches.CalcWavePrefix(p, 1f, wind, 100f, 1f, ref wave), "and CalcWave is skipped");
      float expected = SeaWaves.One(p.x, p.z, 1f, wind, 777.25f, 1f, field.Waves(p.x, p.z));
      Check(wave == expected, $"its answer is SeaWaves.One with the field's W on the drawn clock ({wave:G6})");
      float other = 0f;
      SeaPatches.CalcWavePrefix(p, 1f, wind, 5555f, 1f, ref other);
      Check(other == wave, "whatever clock the caller passes (the wrapped day time)");
      SeaWaves.Clock = () => float.NaN;
      SeaPatches.CalcWavePrefix(p, 1f, wind, 100f, 1f, ref other);
      Check(other == SeaWaves.One(p.x, p.z, 1f, wind, 100f, 1f, field.Waves(p.x, p.z)), "if the drawn clock cannot be read, the clock the caller passed is used");
      SeaWaves.Clock = () => 777.25f;
      var open = new Vector3(0f, 30f, -3000f);
      SeaPatches.CalcWavePrefix(open, 1f, wind, 100f, 0.5f, ref other);
      Check(other == SeaWaves.One(open.x, open.z, 1f, wind, 777.25f, 0.5f, 1f), "in the open sea W is 1 and the wave factor still applies to the small waves");

      field.CalmSameAsGame = true;
      fade = 0.25;
      wave = 0.5f;
      Check(SeaPatches.DeepNorthWaveFadePrefix(0f, 2500f, ref fade) && fade == 0.25 && SeaPatches.CalcWavePrefix(p, 1f, wind, 100f, 1f, ref wave) && wave == 0.5f,
        "where the world's Deep North is the game's circle both run the game's code (the same rule on a server that knows the Deep North alone)");
      field.CalmSameAsGame = false;
      field.Hot = null;
      Check(!SeaPatches.DeepNorthWaveFadePrefix(0f, 2500f, ref fade), "a server's field (no hot channel) answers the same");
    }
    finally
    {
      SeaField.Current = null;
      SeaWaves.Clock = null;
    }
  }

  // ------------------------------------------------------------------------------------------------ the field's life
  private static bool WaitForField(int milliseconds)
  {
    var watch = System.Diagnostics.Stopwatch.StartNew();
    while (SeaField.Current == null && watch.ElapsedMilliseconds < milliseconds)
      Thread.Sleep(20);
    return SeaField.Current != null;
  }

  private static void LifeTests()
  {
    Section("patches: the field's life");
    if (!running) return;
    var harmony = new Harmony("water-tests.life");
    BC.HarmonyInstance = harmony;
    var ews = EWS.Installed;
    // The small world's sea is not the game's only if its layout is patched: the field asks the game's own functions.
    var layout = LayoutGroup();
    var layoutSize = new WorldGeometry(2000f, 100f);
    WorldSizeHelper.Layout.Assume(layoutSize);
    layout.Update(harmony, layoutSize);
    try
    {
      EWS.Installed = false;
      BC.ExpandWorldSizeGeometry = null;
      SeaField.Current = BuildTestField();
      BC.Settings = World();
      SeaPatches.Refresh();
      var plain = SeaPatches.Snapshot;
      Check(SeaField.Current == null && !plain.Pending && !plain.Building && plain.State.StartsWith("off"),
        $"a refresh for a world that is no candidate drops the field and builds none ({plain.State})");

      // A small candidate: Refresh asks, Start builds on a worker.
      var small = World(12, 2000f, 100f);
      BC.Settings = small;
      SeaPatches.Refresh();
      Check(SeaPatches.Snapshot.Pending && SeaField.Current == null, "a refresh for a candidate world waits to build, and the game's values apply");
      int mark = CapturingLogHandler.Mark();
      SeaPatches.Start(small, true);
      Check(WaitForField(60000), "the field is built on a worker");
      var field = SeaField.Current;
      Check(field != null && field.Hot != null && field.Radius == 2100f && field.Size == 525,
        $"for a client: both channels, over the world's edge (2100 m): {field}");
      Check(CapturingLogHandler.Since(mark).Any(l => l.Contains("the sea field is ready")), "and says so in the log");
      Check(SeaPatches.Snapshot.State == "on" && !field!.SameAsGame, $"its Deep North and hot sea are not the game's, so BC's sea is on ({SeaPatches.Snapshot.State})");

      // A client field built before the ground's heights can be asked makes its depth grid when they can (the world generator is made while the
      // world loads, after DynamicPatch).
      Check(SeaPatches.DepthPending && field!.Depth == null, "a client's field built without the generator waits for the ground's heights to make its depth grid");
      SeaPatches.StartDepth(null);
      Check(field.Depth == null && SeaPatches.DepthPending, "with no world generator it goes on waiting");
      SeaPatches.StartDepth((x, z) => 30f - 0.001f * x);
      var depthWatch = System.Diagnostics.Stopwatch.StartNew();
      while (field.Depth == null && depthWatch.ElapsedMilliseconds < 30000)
        Thread.Sleep(20);
      Check(field.Depth != null && !SeaPatches.DepthPending && field.DepthSize > 60, $"and once they can, it is made on a worker ({field.DepthSize} x {field.DepthSize})");

      SeaPatches.Refresh();
      Check(SeaField.Current == null, "the next refresh drops it again (the game's values apply until the new one is ready)");

      // Expand World Size's size can arrive after the field was started: the field is started again for the new edge.
      Check(!SeaPatches.RadiusMoved(small), "a field whose edge is the world's edge is left alone");
      var ewsSize = BC.ExpandWorldSizeGeometry;
      try
      {
        SeaPatches.Refresh();
        SeaPatches.Start(small, true);
        Check(WaitForField(60000) && SeaField.Current!.Radius == 2100f, "the field is built for the settings' edge (2100 m)");
        BC.ExpandWorldSizeGeometry = new WorldGeometry(3000f, 200f);
        mark = CapturingLogHandler.Mark();
        Check(SeaPatches.RadiusMoved(small) && SeaField.Current == null && SeaPatches.Snapshot.Pending
              && CapturingLogHandler.Since(mark).Any(l => l.Contains("the world's edge is now +-3200 m")),
          "Expand World Size sends 3200 m: the field is dropped, asked for again, and the log says why");
        SeaPatches.Start(small, true);
        Check(WaitForField(60000) && SeaField.Current!.Radius == 3200f && !SeaPatches.RadiusMoved(small), "and the new one covers 3200 m, and is then left alone");
      }
      finally
      {
        BC.ExpandWorldSizeGeometry = ewsSize;
      }
      SeaPatches.Refresh();

      // A server asks for the calm half only.
      SeaPatches.Start(small, false);
      Check(WaitForField(60000) && SeaField.Current!.Hot == null && SeaField.Current.Calm.Length == 525 * 525 && !SeaPatches.DepthPending,
        "a server's field has no hot channel, and no depth grid to wait for");
      SeaField.Current = null;

      // A refresh stops a build in flight: its field is never published.
      var big = World(12, 30000f, 500f);
      BC.Settings = big;
      SeaPatches.Refresh();
      SeaPatches.Start(big, true);
      Check(SeaPatches.Snapshot.Building, "a big world's build is in flight");
      BC.Settings = World();
      SeaPatches.Refresh();
      Thread.Sleep(6000);
      Check(SeaField.Current == null && !SeaPatches.Snapshot.Building, "and a refresh stops it: nothing is published after it");

      // A build that fails is logged, and the game's sea stays.
      var broken = World(12, 2000f, 100f);
      BC.Settings = broken;
      SeaPatches.Refresh();
      var deepRule = AccessTools.Method(typeof(WorldGenerator), "IsDeepnorth", XY);
      var thrower = new Harmony("water-tests.thrower");
      thrower.Patch(deepRule, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ThrowingPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
      mark = CapturingLogHandler.Mark();
      try
      {
        SeaPatches.Start(broken, true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!SeaPatches.Snapshot.State.StartsWith("failed") && watch.ElapsedMilliseconds < 30000)
          Thread.Sleep(20);
      }
      finally
      {
        thrower.UnpatchAll(thrower.Id);
      }
      Check(SeaField.Current == null && SeaPatches.Snapshot.State.StartsWith("failed") && CapturingLogHandler.Since(mark).Any(l => l.Contains("building the sea field failed")),
        $"a rule that throws is logged and no field is published ({SeaPatches.Snapshot.State})");
    }
    finally
    {
      EWS.Installed = ews;
      SeaField.Current = null;
      layout.Update(harmony, WorldGeometry.Vanilla);
      harmony.UnpatchAll(harmony.Id);
      WorldSizeHelper.Layout.Assume(WorldGeometry.Vanilla);
    }
  }

  private static bool ThrowingPrefix() => throw new InvalidOperationException("a broken rule");

  // ------------------------------------------------------------------------------------------------ other mods
  private static void ForeignTests()
  {
    Section("patches: other mods' patches on the waves");
    if (!running) return;
    var ours = new Harmony("water-tests.ours");
    BC.HarmonyInstance = ours;
    var foreign = new Harmony("water-tests.someone.else");
    var fade = AccessTools.Method(typeof(WorldGenerator), "DeepNorthWaveFade", XY);
    var deep = AccessTools.Method(typeof(WorldGenerator), "IsDeepnorth", XY);
    var big = World(12, 20000f, 500f);
    try
    {
      BC.Settings = big;
      int mark = CapturingLogHandler.Mark();
      SeaPatches.Refresh();
      Check(!CapturingLogHandler.Since(mark).Any(l => l.Contains("the drawn waves may not match")), "nobody else patches the waves: nothing said");
      Check(SeaPatches.Input(big, true).Workers == 0, "and the field's functions are asked on every core");

      foreign.Patch(fade, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(QuietPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
      foreign.Patch(deep, postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(QuietPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
      mark = CapturingLogHandler.Mark();
      SeaPatches.Refresh();
      var said = CapturingLogHandler.Since(mark);
      Check(said.Count(l => l.Contains("water-tests.someone.else") && l.Contains("DeepNorthWaveFade") && l.Contains("the drawn waves may not match what boats feel")) == 1,
        "a prefix of another mod on DeepNorthWaveFade is said once, by the mod's name");
      SeaPatches.Refresh();
      Check(CapturingLogHandler.Since(mark).Count(l => l.Contains("the drawn waves may not match")) == 1, "and not again at the next refresh");
      var report = new List<string>();
      SeaPatches.Report(report.Add);
      Check(report.Any(l => l.Contains("water-tests.someone.else") && l.Contains("DeepNorthWaveFade")), "'bc water' lists it");
      Check(SeaPatches.Input(big, true).Workers == 1, "another mod's patch on a function the field asks makes the build use one worker (it may not be thread safe)");
    }
    finally
    {
      foreign.UnpatchAll(foreign.Id);
    }
    Check(SeaPatches.Input(big, true).Workers == 0, "and the cores come back when it is gone");
  }

  private static bool QuietPrefix() => true;
  private static void QuietPostfix() { }
}

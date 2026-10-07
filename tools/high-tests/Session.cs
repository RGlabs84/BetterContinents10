// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// BetterContinents.DynamicPatch run for real, as the game runs it when a world loads, when a setting changes and in the main menu, on the game's methods that
// .NET can compile here: the high-terrain patches (six groups, 31 hooks) stand on the game's methods exactly while the world wants them, and a world that
// does not want them, loaded after one that did, finds the game's own methods again (the IL's patches gone, the behaviour the game's). Also: a live change of
// the amount and of the mode, DynamicPatch's first step, a later step that fails, and the console's live change (bc h ht) through DebugUtils' own table.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static readonly FieldInfo HighTogglesField = typeof(BC).GetField("HighTerrainToggles", BindingFlags.NonPublic | BindingFlags.Static)!;

  private static List<object> HighToggleObjects() => ((Array)HighTogglesField.GetValue(null)!).Cast<object>().ToList();

  private static bool IsApplied(object toggle) => (bool)toggle.GetType().GetProperty("Applied")!.GetValue(toggle)!;

  private static string ToggleName(object toggle) => (string)toggle.GetType().GetField("Name")!.GetValue(toggle)!;

  private static List<object> HooksOf(object toggle) =>
    ((Array)toggle.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).First(f => f.FieldType.Name == "Hook[]").GetValue(toggle)!).Cast<object>().ToList();

  private static MethodBase TargetOf(object hook) => ((Func<MethodBase>)hook.GetType().GetField("Target")!.GetValue(hook)!)()!;

  private static string PatchNameOf(object hook) => ((MethodInfo)hook.GetType().GetMethod("Patch")!.Invoke(hook, null)!).Name;

  private static string KindOf(object hook) => hook.GetType().GetField("Kind")!.GetValue(hook)!.ToString()!.ToLowerInvariant();

  // A hook as a patch standing on a game method: "Type.Method|Patch|kind".
  private static string StandingOf(object hook)
  {
    var target = TargetOf(hook);
    return $"{target.DeclaringType?.Name}.{target.Name}|{PatchNameOf(hook)}|{KindOf(hook)}";
  }

  // Every patch of HighTerrainPatches that Better Continents' Harmony instance has on a method of the game now, as the same text.
  private static List<string> HighPatchesStanding(Harmony harmony)
  {
    var found = new List<string>();
    foreach (var method in Harmony.GetAllPatchedMethods().ToList())
    {
      var info = Harmony.GetPatchInfo(method);
      if (info == null)
        continue;
      foreach (var (kind, patches) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers) })
        foreach (var patch in patches)
          if (patch.owner == harmony.Id && patch.PatchMethod.DeclaringType == typeof(HighTerrainPatches))
            found.Add($"{method.DeclaringType?.Name}.{method.Name}|{patch.PatchMethod.Name}|{kind}");
    }
    found.Sort(StringComparer.Ordinal);
    return found;
  }

  // The world loads: its settings are current and Better Continents patches the game to them (as ewd-tests' Load does).
  private static void LoadWorld(BC.BetterContinentsSettings settings)
  {
    BC.HarmonyInstance ??= new Harmony("BetterContinents.Harmony");
    BC.Settings = settings;
    BC.DynamicPatch();
  }

  private static bool HasAiModule()
  {
    try
    {
      System.Reflection.Assembly.Load("UnityEngine.AIModule");
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }

  // A step of DynamicPatch that fails, in place of UpdateGeometry (another mod's transpiler can make a Harmony patch of Better Continents' fail).
  private static bool FailingStep() => throw new InvalidOperationException("stand-in: a step of DynamicPatch fails");

  // A bc console line, run as the game's own table runs it (DebugUtils.RunConsoleCommand) where that class starts here, else through the same group
  // of the table (AddSetting) built by hand.
  private static Action<string> ConsoleLine(out string how)
  {
    try
    {
      RuntimeHelpers.RunClassConstructor(typeof(DebugUtils).TypeHandle);
      how = "the game's own console table (DebugUtils.RunConsoleCommand)";
      return DebugUtils.RunConsoleCommand;
    }
    catch (Exception e)
    {
      how = $"DebugUtils cannot start here ({(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}): a group of the table built with its AddSetting";
      var addSetting = typeof(DebugUtils).GetMethod("AddSetting", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(HighTerrainMode));
      var root = new DebugUtils.Command("bc", "Better Continents", "test").Subcommands(bc =>
        bc.AddGroup("h", "Heightmap", "test", group => addSetting.Invoke(null, [group, SettingsSchema.HighTerrain])));
      return line => root.Run(line);
    }
  }

  // Methods the JIT must not inline: one that holds a call into the engine (an ECall, which .NET refuses to compile into a method Harmony rebuilds) would carry it into the
  // method that calls it. MonoMod, inside Harmony, does this for the methods it patches.
  private static void DisableInlining(IEnumerable<MethodBase> methods)
  {
    var triple = typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple")!;
    var current = triple.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var disable = triple.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(MethodBase)])!;
    foreach (var method in methods)
      disable.Invoke(current, [method]);
  }

  // One part of the session: when it throws, the whole exception is said (a SecurityException from the JIT has no stack of its own) and the next part goes on.
  private static void Phase(string what, Action part)
  {
    try
    {
      part();
    }
    catch (Exception e)
    {
      Check(false, $"{what}: {e}");
    }
  }

  private static void SessionTests()
  {
    Section("DynamicPatch: the patches stand on the game's methods exactly while a world wants them");
    BoundConfig();
    BC.HarmonyInstance ??= new Harmony("BetterContinents.Harmony");
    var harmony = BC.HarmonyInstance;
    var toggles = HighToggleObjects();
    bool ai = HasAiModule();
    Info($"the engine's AI module {(ai ? "is" : "is not")} here, so the groups with a Pathfinding method {(ai ? "can" : "cannot")} be compiled");
    var stub = new Harmony("high-tests.step");
    var savedRegeneration = GameUtils.RequestRegeneration;
    // WorldGenerator.GetBaseHeight, which DynamicPatch patches for a heightmap and unpatches, holds the noise function, which holds the engine's own.
    DisableInlining(typeof(DUtils).GetMethods().Where(m => m.Name == nameof(DUtils.PerlinNoise)));
    try
    {
      // DynamicPatch's first step is the high-terrain one: a step that throws later cannot leave a world's patches on for the next.
      var steps = PatchProcessor.GetOriginalInstructions(typeof(BC).GetMethod("DynamicPatch", BindingFlags.Public | BindingFlags.Static)!);
      var firstCall = steps.FirstOrDefault(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt);
      Check(firstCall?.operand is MethodInfo { Name: "PatchHighTerrain" }, $"DynamicPatch's first call is PatchHighTerrain (it is {(firstCall?.operand as MethodInfo)?.Name})");

      // The behaviour the patches change: Character.InInterior (the game's rule is anything over 3000 m), and where RandEventSystem tells an event.
      var inInterior = typeof(Character).GetMethod("InInterior", Any, null, [typeof(Vector3)], null)!;
      bool InsideAt(float y) => (bool)inInterior.Invoke(null, [new Vector3(10f, y, 0f)])!;
      var area = typeof(RandEventSystem).GetMethod("IsInsideRandomEventArea", Any)!;
      var system = RuntimeHelpers.GetUninitializedObject(typeof(RandEventSystem));
      GC.SuppressFinalize(system);
      var @event = (RandomEvent)RuntimeHelpers.GetUninitializedObject(typeof(RandomEvent));
      @event.m_pos = Vector3.zero;
      @event.m_eventRange = 100f;
      bool Told(float y) => (bool)area.Invoke(system, [@event, new Vector3(10f, y, 10f)])!;

      // What the state of the patches must be: high = the world wants them. UseZ is the Deep North weather's, set late in DynamicPatch (not after a failed step).
      void Expect(string what, bool high, bool deepNorth = true)
      {
        var applied = toggles.Where(IsApplied).ToList();
        var standing = HighPatchesStanding(harmony);
        var expected = applied.SelectMany(HooksOf).Select(StandingOf).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(standing.SequenceEqual(expected), $"{what}: the patches that stand are the hooks of the groups that are applied ({standing.Count} stand, {expected.Count} expected; {string.Join(", ", standing.Except(expected).Concat(expected.Except(standing)).Take(4))})");
        if (!high)
        {
          Check(!HighTerrain.Active && HighTerrain.Top == 0f && applied.Count == 0 && standing.Count == 0, $"{what}: no group is applied, no patch stands, HighTerrain is inactive");
          Check(InsideAt(8100f) && InsideAt(3001f) && !InsideAt(3000f) && !Told(8100f) && Told(2999f),
            $"{what}: the game's own rules: anything over 3000 m is inside a dungeon, and no event is told to a player over 3000 m");
        }
        else
        {
          Check(HighTerrain.Active && HighTerrain.Top > 0f, $"{what}: HighTerrain is active (the top {HighTerrain.Top:0.#} m)");
          // The groups whose every method .NET can compile here are applied: the random events', and with the engine's AI module the ground rays' and the AI's tiles' (the
          // others hold a call into the engine that .NET refuses to compile, and are rolled back whole: no patch of theirs stands, which the check above holds).
          Check(IsApplied(toggles[1]) && (!ai || (IsApplied(toggles[2]) && IsApplied(toggles[3]))),
            $"{what}: the groups that can be switched on here are ({string.Join(", ", toggles.Select(t => ToggleName(t).Replace("High terrain: ", "") + (IsApplied(t) ? " yes" : " no")))}; {standing.Count} patches stand)");
          // On the ground at the top of the world, 2900 m over it is not inside a dungeon (the game says so over 3000 m), and 5000 m over it is.
          float top = HighTerrain.Top;
          HighTerrain.GroundSource = _ => top;
          if (IsApplied(toggles[0]))
            Check(!InsideAt(top + 2900f) && InsideAt(top + 5000f) && !InsideAt(2999f), $"{what}: {top + 2900f:0} m over a ground of {top:0} m is not inside a dungeon, {top + 5000f:0} m is");
          if (IsApplied(toggles[1]))
            Check(Told(top + 2900f) && Told(2999f), $"{what}: an event is told to a player {top + 2900f:0} m up on ground of {top:0} m");
        }
        if (deepNorth)
          Check(BC.DeepNorthWeather.UseZ == high, $"{what}: the Deep North weather asks at x and z {(high ? "" : "no longer ")}(UseZ {BC.DeepNorthWeather.UseZ})");
      }

      Phase("the menu and a high world", () =>
      {
        LoadWorld(new BC.BetterContinentsSettings());
        Expect("the main menu", false);
        LoadWorld(HighWorld(81f));
        var failed = CapturingLogHandler.Lines.Where(l => l.StartsWith("[Error]") && l.Contains("High terrain")).Select(l => l.Substring(l.IndexOf("High terrain"))).Distinct().ToList();
        Info($"groups that could not be switched on here (.NET cannot compile a game method that holds a call into the engine, an ECall): {(failed.Count == 0 ? "none" : string.Join(" | ", failed))}");
        Expect("a heightmap at Amount 81 (Auto)", true);
        LoadWorld(new BC.BetterContinentsSettings());
        Expect("the main menu again, after the high world", false);
        LoadWorld(HighWorld(2f));
        Expect("a heightmap at Amount 2 (a vanilla-height world)", false);
        LoadWorld(HighWorld(81f));
        LoadWorld(HighWorld(5f));
        Expect("a heightmap at Amount 5 loaded after one of Amount 81", false);
      });

      // A live change of the amount (bc h am) and of the mode (bc h ht), which run DynamicPatch with the settings the world holds.
      Phase("a live change of the amount", () =>
      {
        LoadWorld(HighWorld(81f));
        BC.Settings.HeightmapAmount = 2f;
        BC.DynamicPatch();
        Expect("Amount 81 changed to 2 in the running world", false);
        BC.Settings.HeightmapAmount = 81f;
        BC.DynamicPatch();
        Expect("and back to 81", true);
      });
      Phase("a live change of the mode", () =>
      {
        BC.Settings.HighTerrainMode = HighTerrainMode.Off;
        BC.DynamicPatch();
        Expect("High Terrain Off at Amount 81", false);
        BC.Settings.HighTerrainMode = HighTerrainMode.On;
        BC.DynamicPatch();
        Expect("High Terrain On at Amount 81", true);
        BC.Settings.HighTerrainMode = HighTerrainMode.Auto;
        BC.DynamicPatch();
        Expect("High Terrain Auto at Amount 81", true);
      });
      Phase("worlds of each mode", () =>
      {
        LoadWorld(ModeWorld(HighTerrainMode.On, 1f, false));
        Expect("High Terrain On, no heightmap", true);
        Check(Math.Abs(HighTerrain.Top - HighTerrain.MaxMetres(BC.Settings)) < 0.01f && HighTerrain.Top > 1900f && HighTerrain.Top < 1920f, $"its top is the game's own terrain at its largest ({HighTerrain.Top:0.#} m)");
        LoadWorld(ModeWorld(HighTerrainMode.Off, 81f, true));
        Expect("High Terrain Off at Amount 81, loaded after On", false);
        LoadWorld(ModeWorld(HighTerrainMode.On, 2f, true));
        Expect("High Terrain On at Amount 2: the patches on a vanilla-height world", true);
        Check(Math.Abs(HighTerrain.Top - 370f) < 0.5f, $"its top is the heightmap's {HighTerrain.Top:0.#} m (370)");
      });

      // A step of DynamicPatch that fails after the first: the state of the high-terrain patches is the world's all the same, going in and coming out.
      Phase("a step that fails", () =>
      {
        var updateGeometry = AccessTools.Method(typeof(BC), "UpdateGeometry")!;
        stub.Patch(updateGeometry, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(FailingStep), BindingFlags.NonPublic | BindingFlags.Static)!));
        bool threw = false;
        try { LoadWorld(HighWorld(81f)); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "the stand-in step fails, and DynamicPatch lets it out");
        Expect("a high world whose later step threw", true, deepNorth: false);
        threw = false;
        try { LoadWorld(new BC.BetterContinentsSettings()); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "and fails for the next world too");
        Expect("the main menu after a high world, with a step that throws", false, deepNorth: false);
        stub.UnpatchAll(stub.Id);
      });

      // The console: bc h ht changes the mode of the running world, and the patches follow at once.
      Phase("the console's live change", () =>
      {
        LoadWorld(HighWorld(81f));
        int regenerations = 0;
        GameUtils.RequestRegeneration = () => regenerations++;
        var run = ConsoleLine(out var how);
        Info($"the console line runs through {how}");
        void Line(string text)
        {
          try { run(text); }
          catch (Exception e) { Check(false, $"{text}: {e}"); }
        }
        Line("bc h ht off");
        Check(BC.Settings.HighTerrainMode == HighTerrainMode.Off && regenerations == 1, $"bc h ht off: the running world is Off, and its zones are regenerated once ({regenerations})");
        Expect("after bc h ht off", false);
        Line("bc h ht on");
        Check(BC.Settings.HighTerrainMode == HighTerrainMode.On && regenerations == 2, $"bc h ht on: the world is On again, the zones regenerated once more ({regenerations})");
        Expect("after bc h ht on", true);
        LoadWorld(HighWorld(2f));
        Line("bc h ht on");
        Check(BC.Settings.HighTerrainMode == HighTerrainMode.On && regenerations == 3, $"bc h ht on in a world of Amount 2: nothing it lifts reaches an alt biome, and the zones are regenerated ({regenerations})");
        Expect("after bc h ht on at Amount 2", true);
        Line("bc h ht auto");
        Expect("after bc h ht auto at Amount 2", false);
        // A word that is no mode: the console says so (there is no console here to say it, which is the exception) and changes nothing.
        try { run("bc h ht banana"); } catch (Exception) { }
        Check(BC.Settings.HighTerrainMode == HighTerrainMode.Auto && regenerations == 4, $"a word that is no mode changes nothing, and regenerates nothing ({regenerations})");
      });
    }
    finally
    {
      stub.UnpatchAll(stub.Id);
      GameUtils.RequestRegeneration = savedRegeneration;
      HighTerrain.GroundSource = null;
      try { LoadWorld(new BC.BetterContinentsSettings()); } catch (Exception) { }
      HighTerrain.Update(new BC.BetterContinentsSettings());
    }
  }
}

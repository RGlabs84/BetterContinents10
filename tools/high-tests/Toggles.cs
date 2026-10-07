// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The toggles: which settings want them, that every target and every patch method is in the installed game (a target that was found
// ambiguous took a world's load down once: BaseAI.CanHearTarget has two overloads), the production hook list against the game by identity
// (type, method, signature, patch, kind: two hooks swapped with the count unchanged fails it), that each transpiler changes the very method
// its hook is bound to, and the rules around them (Heightmap Amount's range, the Deep North weather).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static readonly string[] HighToggleNames =
  [
    "High terrain: Character.InInterior and the methods that call it", "High terrain: random events", "High terrain: the ground rays",
    "High terrain: the AI's navigation tiles", "High terrain: altitude limits", "High terrain: the Valkyrie",
  ];

  // Every hook of the six groups as the game method it is bound to, the patch method and how: type.method(parameter types)|patch|kind.
  private static readonly string[] ExpectedHooks =
  [
    "Character.InInterior(Vector3)|InteriorOfPoint|prefix", "Character.InInterior(Transform)|InteriorOfTransform|prefix", "Character.InInterior()|InteriorOfCharacter|prefix",
    "Character.Awake()|RedirectInterior|transpiler", "Character.UpdateWalking(Single)|RedirectInterior|transpiler",
    "Player.PlacePiece(Piece,Vector3,Quaternion,Boolean,Boolean)|RedirectInterior|transpiler", "Player.UpdatePlacementGhost(Boolean)|RedirectInterior|transpiler",
    "Attack.Start(Humanoid,Rigidbody,ZSyncAnimation,CharacterAnimEvent,VisEquipment,ItemData,Attack,Single,Single)|RedirectInterior|transpiler",
    "BaseAI.CanHearTarget(Transform,Single,Character)|RedirectInterior|transpiler", "BaseAI.CanUseAttack(ItemData)|RedirectInterior|transpiler",
    "EnvMan.UpdateEnvironment(Int64,BiomeSector)|RedirectInterior|transpiler", "EnvMan.UpdateWind(Int64,Single)|RedirectInterior|transpiler",
    "RenderGroupSystem.LateUpdate()|RedirectInterior|transpiler", "Location.GetLocation(Vector3,Boolean)|RedirectInterior|transpiler",
    "Teleport.Interact(Humanoid,Boolean,Boolean)|RedirectInterior|transpiler",
    "RandEventSystem.IsInsideRandomEventArea(RandomEvent,Vector3)|RedirectEventHeight|transpiler", "RandEventSystem.GetValidEventPoints(RandomEvent,List`1)|RedirectEventHeight|transpiler",
    "ZoneSystem.GetGroundHeight(Vector3)|RaiseGroundRay|transpiler", "ZoneSystem.GetGroundHeight(Vector3,Single&)|RaiseGroundRay|transpiler",
    "ZoneSystem.GetGroundData(Vector3&,Vector3&,Biome&,BiomeArea&,Heightmap&)|RaiseGroundDataRay|transpiler", "ZoneSystem.IsBlocked(Vector3)|RaiseBlockedRay|transpiler",
    "Pathfinding.FindGround(Vector3,Boolean,List`1,AgentSettings)|RaiseGroundRay|transpiler",
    "ClutterSystem.GetGroundInfo(Vector3,Vector3&,Vector3&,Heightmap&,Biome&)|RaiseClutterRay|transpiler",
    "Pathfinding.GetTilePos(Vector3Int)|TilePosition|postfix",
    "ZoneSystem.PlaceVegetation(Vector2s,Vector3,Transform,Heightmap,List`1,SpawnMode,List`1)|LiftMaxAltitude|transpiler", "ZoneSystem.GenerateLocationsTimeSliced(MoveNext)|LiftMaxAltitude|transpiler",
    "SpawnSystem.IsSpawnPointGood(SpawnData,Vector3&)|LiftMaxAltitude|transpiler", "ClutterSystem.GenerateVegPatch(Vector2Int,Single)|LiftMaxAltitude|transpiler",
    "RandomSpawn.Randomize(Vector3,Location,DungeonGenerator)|LiftMaxElevation|transpiler", "RandomObject.Randomize(Vector3,Location,DungeonGenerator)|LiftMaxElevation|transpiler",
    "Valkyrie.Awake()|ValkyrieAwake|prefix",
  ];

  // A game method as "Type.Method(parameter types)", the parameters read by reflection or, where .NET cannot load a type of the signature (the engine's AI module),
  // by Cecil from the client's assembly; an iterator's MoveNext as "Type.Method(MoveNext)" of the method it is made from.
  private static string Describe(MethodBase method)
  {
    var type = method.DeclaringType!;
    var iterator = System.Text.RegularExpressions.Regex.Match(type.Name, @"^<(\w+)>d__\d+$");
    if (iterator.Success)
      return $"{type.DeclaringType!.Name}.{iterator.Groups[1].Value}(MoveNext)";
    try
    {
      return $"{type.Name}.{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})";
    }
    catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
    {
      using var module = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(Libs, "1.0", "client", "assembly_valheim.dll"));
      var definition = module.GetTypes().First(t => t.FullName == type.FullName).Methods.Single(m => m.Name == method.Name);
      return $"{type.Name}.{method.Name}({string.Join(",", definition.Parameters.Select(p => p.ParameterType.Name))})";
    }
  }

  private static void ToggleTests()
  {
    Section("the toggles");
    Check(!BC.WantedToggles(HighWorld(1f)).Any(HighToggleNames.Contains) && !BC.WantedToggles(HighWorld(5f)).Any(HighToggleNames.Contains)
          && !BC.WantedToggles(new BC.BetterContinentsSettings()).Any(HighToggleNames.Contains) && !BC.WantedToggles(HighWorld(81f, enabled: false)).Any(HighToggleNames.Contains),
      "none is wanted for Heightmap Amount 1 or 5 (the most before 0.10.3), for the menu, or with Better Continents off");
    Check(BC.WantedToggles(HighWorld(81f)).Where(HighToggleNames.Contains).OrderBy(s => s).SequenceEqual(HighToggleNames.OrderBy(s => s)) && BC.WantedToggles(HighWorld(6f)).Count(HighToggleNames.Contains) == 6,
      "all six are wanted for Heightmap Amount 81, and for 6, the first above 5");

    var field = typeof(BC).GetField("HighTerrainToggles", BindingFlags.NonPublic | BindingFlags.Static)!;
    var toggles = ((Array)field.GetValue(null)!).Cast<object>().ToList();
    Check(toggles.Count == 6 && toggles.Select(t => (string)t.GetType().GetField("Name")!.GetValue(t)!).SequenceEqual(HighToggleNames), "six toggles, named as above");
    int hooks = 0;
    foreach (var toggle in toggles)
    {
      var name = ToggleName(toggle);
      var list = HooksOf(toggle);
      Check((bool)toggle.GetType().GetProperty("ViaProcessor")!.GetValue(toggle)! && toggle.GetType().GetProperty("FailureMessage")!.GetValue(toggle) is string { Length: > 0 },
        $"{name}: patched through the processor, and a failure is logged and leaves the others");
      foreach (var hook in list)
      {
        hooks++;
        var type = hook.GetType();
        var description = (string)type.GetField("TargetDescription")!.GetValue(hook)!;
        var target = ((Func<MethodBase>)type.GetField("Target")!.GetValue(hook)!)();
        var patch = (MethodInfo)type.GetMethod("Patch")!.Invoke(hook, null)!;
        var kind = type.GetField("Kind")!.GetValue(hook)!.ToString()!;
        Check(target != null, $"{description} is in the installed game, and not ambiguous");
        Check(patch != null, $"{description}: its patch method {kind} is there");
        if (target == null || patch == null)
          continue;
        var parameters = patch.GetParameters();
        // (A method of the AI's classes cannot give its parameters here: the engine's AI module is not in the reference folder.)
        // (Where .NET cannot load a type of the target's signature, the patch may take only what Harmony itself provides.)
        HashSet<string> targetNames;
        try { targetNames = target.GetParameters().Select(p => p.Name).ToHashSet(); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException) { targetNames = []; }
        if (kind == "Transpiler")
          Check(parameters[0].ParameterType == typeof(IEnumerable<CodeInstruction>) && patch.ReturnType == typeof(IEnumerable<CodeInstruction>) && parameters.Skip(1).All(p => p.ParameterType == typeof(MethodBase)),
            $"{description}: the transpiler takes the instructions (and the method)");
        else
          Check(parameters.All(p => p.Name is "__instance" or "__result" || targetNames.Contains(p.Name)) && (kind == "Postfix" || patch.ReturnType == typeof(bool) || patch.ReturnType == typeof(void)),
            $"{description}: the {kind.ToLowerInvariant()}'s parameters ({string.Join(", ", parameters.Select(p => p.Name))}) are the method's or Harmony's");
      }
    }
    Check(hooks == 31, $"31 hooks in all: 15 for InInterior (its 3 overloads and 12 methods that call it), 2 events, 6 rays, 1 tile, 6 altitudes, 1 Valkyrie ({hooks})");

    // The production hook list against the game by identity, not by count: every hook as the game method it is bound to (type, name, parameter types), the
    // patch method and how, against what the six groups are meant to patch, written out here.
    var all = toggles.SelectMany(HooksOf).ToList();
    var described = all.Select(h => $"{Describe(TargetOf(h))}|{PatchNameOf(h)}|{KindOf(h)}").OrderBy(x => x, StringComparer.Ordinal).ToList();
    var meant = ExpectedHooks.OrderBy(x => x, StringComparer.Ordinal).ToList();
    Check(described.SequenceEqual(meant), $"the 31 hooks are the ones meant, by type, method, signature and patch: in the list only: [{string.Join("; ", described.Except(meant))}]; meant only: [{string.Join("; ", meant.Except(described))}]");
    // The iterator whose MoveNext is patched is the one of the overload with the three parameters (the other, with none, is a different coroutine).
    var locations = typeof(ZoneSystem).GetMethod("GenerateLocationsTimeSliced", Any, null, [typeof(ZoneSystem.ZoneLocation), typeof(System.Diagnostics.Stopwatch), typeof(ZPackage)], null)!;
    var moveNext = AccessTools.EnumeratorMoveNext(locations);
    Check(all.Any(h => TargetOf(h).DeclaringType == moveNext.DeclaringType && TargetOf(h).Name == "MoveNext"), "GenerateLocationsTimeSliced's hook is the MoveNext of the overload with a location, a stopwatch and a package");

    // Each transpiler on the IL of the very method its hook is bound to (the client's assembly): it changes it, and does not say it found less than it expected.
    int bound = 0;
    foreach (var hook in all.Where(h => KindOf(h) == "transpiler"))
    {
      var target = TargetOf(hook);
      int mark = CapturingLogHandler.Lines.Count;
      var before = Instructions("client", target);
      var after = Run(PatchNameOf(hook), before, target);
      bound++;
      Check(!after.Select(Show).SequenceEqual(before.Select(Show)) && !CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("did not find")),
        $"{Describe(target)}: {PatchNameOf(hook)} changes it ({before.Count} -> {after.Count} instructions) and finds all it expects");
    }
    Check(bound == 26, $"26 of the hooks are transpilers (the other five: the three overloads of InInterior, the AI's tile, the Valkyrie), each run on its own method ({bound})");
    var redirected = all.Where(h => PatchNameOf(h) == "RedirectInterior").Select(h => $"{TargetOf(h).DeclaringType!.Name}.{TargetOf(h).Name}").OrderBy(x => x, StringComparer.Ordinal).ToList();
    var callers = InteriorCallers.Select(c => $"{c.type}.{c.method}").OrderBy(x => x, StringComparer.Ordinal).ToList();
    Check(redirected.SequenceEqual(callers), $"the methods RedirectInterior is bound to are the methods that call Character.InInterior (the inventory of both game builds): [{string.Join(", ", redirected.Except(callers).Concat(callers.Except(redirected)))}] differ");

    // The three ways the game asks InInterior, and the patch for each (by the names of its own parameters).
    var inInterior = typeof(Character).GetMethods(Any).Where(m => m.Name == "InInterior").ToList();
    Check(inInterior.Count == 3 && inInterior.Select(m => m.GetParameters().Length == 0 ? "Character" : m.GetParameters()[0].ParameterType.Name).OrderBy(s => s).SequenceEqual(["Character", "Transform", "Vector3"]),
      "Character.InInterior is the three overloads the patches cover");

    Section("the settings and the Deep North weather");
    var dumped = new List<string>();
    HighWorld(81f).Dump(dumped.Add);
    var plain = new List<string>();
    HighWorld(2f).Dump(plain.Add);
    Check(dumped.Any(l => l.StartsWith("High terrain: the land can reach 16170 m")) && !plain.Any(l => l.Contains("High terrain")), "the settings' dump (bc info, the log) says when the patches are on, and only then");
    var range = (AcceptableValueRange<float>)SettingsSchema.HeightmapAmount.Range!;
    var exportRange = (AcceptableValueRange<float>)SettingsSchema.ExportHeightmapAmount.Range!;
    Check(SettingsSchema.MaxHeightmapAmount == 81f && range.MaxValue == 81f && range.MinValue == 0f, "Heightmap Amount's range is 0 to 81");
    Check(exportRange.MaxValue == 81f && exportRange.MinValue == 0.01f, "and the export's Default Heightmap Amount's 0.01 to 81");
    Check(SettingsSchema.HeightmapAmount.Description.Contains("-30 m to 16,170 m"), "its description says what 81 gives");
    Check(!BC.DeepNorthWeatherUsesZ(new BC.BetterContinentsSettings()) && !BC.DeepNorthWeatherUsesZ(HighWorld(2f)) && !BC.DeepNorthWeatherUsesZ(HighWorld(5f)) && BC.DeepNorthWeatherUsesZ(HighWorld(81f))
          && !BC.DeepNorthWeatherUsesZ(HighWorld(81f, enabled: false)),
      "the Deep North weather asks at the camera's x and z on a high world, and as the game does on every other without a biome map");
  }

  // Harmony runs a method's transpilers again on every change to the method's patches (the high-terrain patches of EnvMan.UpdateEnvironment and UpdateWind, the world
  // size's wind patch), so Better Continents' Deep North transpilers run again and again: the count of calls they rewrote is set, not added to, and the log says what
  // they did once for each count.
  private static void DeepNorthLogTests()
  {
    Section("Deep North weather: the transpilers say what they rewrote once for each count, however often Harmony runs them");
    var patch = typeof(BC).GetNestedType("EnvManDeepNorthPatch", BindingFlags.NonPublic) ?? throw new InvalidOperationException("no EnvManDeepNorthPatch");
    int updateBefore = BC.DeepNorthWeather.UpdateEnvironmentCalls, biomeBefore = BC.DeepNorthWeather.GetBiomeCalls;
    try
    {
      // The client's assembly only: DeepNorthWeather.Rewrite finds WorldGenerator.IsDeepnorth as the method itself, not by its name, so it knows the assembly this suite
      // has loaded (the client's); the dedicated server's is a second assembly here, and the only one in the game that runs it.
      foreach (var (name, assembly) in GameAssemblies().Where(a => a.name == "client"))
        foreach (var (method, transpiler) in new[] { ("UpdateEnvironment", "UpdateEnvironmentTranspiler"), ("GetBiome", "GetBiomeTranspiler") })
        {
          var m = Method(assembly, "EnvMan", method);
          var before = Instructions(name, m);
          var run = patch.GetMethod(transpiler, BindingFlags.NonPublic | BindingFlags.Static)!;
          int Calls() => method == "GetBiome" ? BC.DeepNorthWeather.GetBiomeCalls : BC.DeepNorthWeather.UpdateEnvironmentCalls;
          void Set(int n) { if (method == "GetBiome") BC.DeepNorthWeather.GetBiomeCalls = n; else BC.DeepNorthWeather.UpdateEnvironmentCalls = n; }
          int Said(int mark) => CapturingLogHandler.Lines.Skip(mark).Count(l => l.Contains($"Deep North weather: EnvMan.{method} now asks"));
          List<CodeInstruction> Transpile() => ((IEnumerable<CodeInstruction>)run.Invoke(null, [before.Select(i => new CodeInstruction(i)).ToList()])!).ToList();

          Set(0);
          int mark = CapturingLogHandler.Lines.Count;
          var after = Transpile();
          Transpile();
          Transpile();
          Check(after.Count == before.Count + 2 && Calls() == 1,
            $"{name} EnvMan.{method}: its one IsDeepnorth call is rewritten, and three runs of the transpiler leave the count at 1 (set, not added to: {Calls()})");
          Check(Said(mark) == 1, $"{name} EnvMan.{method}: three runs of the transpiler say it once ({Said(mark)})");
          Set(2);
          mark = CapturingLogHandler.Lines.Count;
          Transpile();
          Check(Said(mark) == 1 && Calls() == 1, $"{name} EnvMan.{method}: a run that finds another count than the last says so again ({Said(mark)})");
        }
    }
    finally
    {
      BC.DeepNorthWeather.UpdateEnvironmentCalls = updateBefore;
      BC.DeepNorthWeather.GetBiomeCalls = biomeBefore;
    }
  }
}

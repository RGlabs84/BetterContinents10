// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The toggles: which settings want them, that every target and every patch method is in the installed game (a target that was found
// ambiguous took a world's load down once: BaseAI.CanHearTarget has two overloads), and the rules around them (Heightmap Amount's range,
// the Deep North weather).

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
      var name = (string)toggle.GetType().GetField("Name")!.GetValue(toggle)!;
      var list = ((Array)toggle.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).First(f => f.FieldType.Name == "Hook[]").GetValue(toggle)!).Cast<object>().ToList();
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
        HashSet<string> targetNames;
        try { targetNames = target.GetParameters().Select(p => p.Name).ToHashSet(); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException) { targetNames = parameters.Select(p => p.Name).ToHashSet(); }
        if (kind == "Transpiler")
          Check(parameters[0].ParameterType == typeof(IEnumerable<CodeInstruction>) && patch.ReturnType == typeof(IEnumerable<CodeInstruction>) && parameters.Skip(1).All(p => p.ParameterType == typeof(MethodBase)),
            $"{description}: the transpiler takes the instructions (and the method)");
        else
          Check(parameters.All(p => p.Name is "__instance" or "__result" || targetNames.Contains(p.Name)) && (kind == "Postfix" || patch.ReturnType == typeof(bool) || patch.ReturnType == typeof(void)),
            $"{description}: the {kind.ToLowerInvariant()}'s parameters ({string.Join(", ", parameters.Select(p => p.Name))}) are the method's or Harmony's");
      }
    }
    Check(hooks == 31, $"31 hooks in all: 3 + 12 for InInterior, 2 events, 6 rays, 1 tile, 6 altitudes, 1 Valkyrie ({hooks})");

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
}

// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).
//
// The bridge between Expand World Size and Better Continents, from Expand World Size's side: ExpandWorldSize.dll 1.45.0 (the Thunderstore
// package, md5 d663c41a..., in libs-Tools/1.0/thirdparty/expand-world-size-2026-10-26) finds Better Continents by the names in the IL of
// its ExpandWorldSize.BetterContinents.Run, called Apply in the source on GitHub (read with Mono.Cecil; nothing of Expand World Size runs). Each name is resolved against the
// real BetterContinents.dll the way its AccessTools calls resolve them, and what it calls afterwards (SetSize, SetStretch, the flag that
// says the world is Better Continents') is called as it calls it. A rename in Better Continents, or a new lookup in a later Expand World
// Size, fails here instead of silently turning the bridge off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx;
using BetterContinents;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace EwsTests;

internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines) Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }
  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines) Lines.Add("[Exception] " + exception.GetType().Name + ": " + exception.Message);
  }
}

internal static class Program
{
  const string Libs = "/home/rohan/WubarrkCODING/libs-Tools";
  const string EwsDll = Libs + "/1.0/thirdparty/expand-world-size-2026-10-26/ExpandWorldSize-1.45.0/ExpandWorldSize.dll";
  const string EwsMd5 = "d663c41a";

  static int checks, failures;
  static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }
  static void Section(string s) => System.Console.WriteLine("== " + s);

  static int Main()
  {
    var dirs = new[]
    {
      AppContext.BaseDirectory, Libs + "/1.0/client", Libs, Libs + "/BepInEx/core",
      // Last: Vector3's ToString lives in a module libs-Tools does not hold (a log line that shows a position needs it).
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
    };
    AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
      var file = name.Name == "com.rlabrecque.steamworks.net" ? "steamworks.net" : name.Name;
      foreach (var dir in dirs)
      {
        var path = Path.Combine(dir, file + ".dll");
        if (File.Exists(path))
          return context.LoadFromAssemblyPath(Path.GetFullPath(path));
      }
      return null;
    };
    return Run();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run()
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    try
    {
      Names();
      Calls();
    }
    catch (Exception e)
    {
      failures++;
      System.Console.WriteLine("CRASH " + e);
    }
    System.Console.WriteLine($"ews-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  static MethodInfo? SetSize, SetStretch;
  static FieldInfo? SettingsField, EnabledField;

  static void Names()
  {
    Section("the names Expand World Size 1.45 looks Better Continents up by");
    var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(EwsDll))).ToLowerInvariant();
    C(md5.StartsWith(EwsMd5), $"the Expand World Size DLL under test is 1.45.0 (md5 {md5})");
    using var module = ModuleDefinition.ReadModule(EwsDll, new ReaderParameters { InMemory = true });
    var plugin = module.GetTypes().SelectMany(t => t.CustomAttributes).FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
    C(plugin != null && (string)plugin.ConstructorArguments[0].Value == EWS.GUID, $"its plugin id is \"{EWS.GUID}\" (EWS.GUID)");
    var bridge = module.GetTypes().Single(t => t.FullName == "ExpandWorldSize.BetterContinents");
    var guid = bridge.Fields.Single(f => f.Name == "GUID").Constant as string;
    var bcPlugin = typeof(BC).GetCustomAttribute<BepInPlugin>();
    C(guid != null && bcPlugin != null && bcPlugin.GUID == guid, $"it finds Better Continents by the plugin id \"{guid}\", which is Better Continents' ({bcPlugin?.GUID})");

    var apply = bridge.Methods.Single(m => m.Name is "Apply" or "Run");
    var words = apply.Body.Instructions.Where(i => i.Operand is string).Select(i => (string)i.Operand).Distinct().ToList();
    var expected = new[]
    {
      "BetterContinents", "BetterContinents.BetterContinents", "Settings", "BetterContinents.BetterContinents+BetterContinentsSettings", "EnabledForThisWorld", "SetSize",
      "BetterContinents.WorldSizeHelper", "SetStretch", "WorldSize", "EdgeSize",
    };
    C(words.Where(w => !w.Contains(' ') && !w.Contains('"')).OrderBy(w => w, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(w => w, StringComparer.Ordinal)),
      "the lookup (Run in the 1.45 DLL, Apply in the source) has exactly these names (a new one would fail here first): " + string.Join(", ", words.Where(w => !w.Contains(' '))));

    // The way Apply resolves them (AccessTools.Field / Method / assembly.GetType).
    var assembly = typeof(BC).Assembly;
    var type = assembly.GetType("BetterContinents.BetterContinents");
    C(type == typeof(BC), "BetterContinents.BetterContinents is the plugin's class");
    SettingsField = AccessTools.Field(type, "Settings");
    C(SettingsField != null && SettingsField.IsStatic && SettingsField.FieldType == typeof(BC.BetterContinentsSettings), "its static field Settings holds the world's settings");
    var settingsType = assembly.GetType("BetterContinents.BetterContinents+BetterContinentsSettings");
    C(settingsType == typeof(BC.BetterContinentsSettings), "BetterContinents.BetterContinents+BetterContinentsSettings is the settings class");
    EnabledField = AccessTools.Field(settingsType, "EnabledForThisWorld");
    C(EnabledField != null && EnabledField.FieldType == typeof(bool), "EnabledForThisWorld is a bool field (not a property)");
    SetSize = AccessTools.Method(type, "SetSize");
    C(SetSize != null && SetSize.IsStatic && SetSize.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(float), typeof(float)]), "SetSize(float, float) is static and not ambiguous (AccessTools.Method by name alone)");
    var helper = assembly.GetType("BetterContinents.WorldSizeHelper");
    SetStretch = helper == null ? null : AccessTools.Method(helper, "SetStretch");
    C(SetStretch != null && SetStretch.IsStatic && SetStretch.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(float), typeof(float)]), "WorldSizeHelper.SetStretch(float, float) is static and not ambiguous");
    // With SetSize found Apply stops before it looks at the two fields of Better Continents before 0.10.
    C(AccessTools.Field(type, "WorldSize") == null && AccessTools.Field(type, "EdgeSize") == null, "(the fields WorldSize and EdgeSize of the old bridge are not needed, and are not there)");

    // RefreshSize calls them as (radius, edge) and (stretch, biome stretch).
    var refresh = bridge.Methods.Single(m => m.Name == "RefreshSize");
    var calls = refresh.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).FullName).ToList();
    C(calls.Any(c => c.Contains("Configuration::get_WorldRadius")) && calls.Any(c => c.Contains("Configuration::get_WorldEdgeSize")) && calls.Any(c => c.Contains("Configuration::get_WorldStretch")) && calls.Any(c => c.Contains("Configuration::get_BiomeStretch")),
      "RefreshSize hands over Configuration.WorldRadius, WorldEdgeSize, WorldStretch and BiomeStretch");
  }

  static void Calls()
  {
    Section("what Expand World Size 1.45 does with them");
    var saved = BC.Settings;
    try
    {
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      C(!(bool)EnabledField!.GetValue(SettingsField!.GetValue(null))!, "IsEnabled: a world without Better Continents is not enabled");
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
      C((bool)EnabledField!.GetValue(SettingsField!.GetValue(null))!, "IsEnabled: a Better Continents world is");
      // A world on the game's terrain (Better Continents on, shaping nothing) still reads as enabled to Expand World Size: it then
      // leaves the world's size, edge and wind to Better Continents (EnabledForThisWorld is all it asks). Pinned, not endorsed.
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true };
      C((bool)EnabledField.GetValue(SettingsField.GetValue(null))! && !BC.Settings.ShapesWorld, "IsEnabled: a world on the game's terrain reads as enabled although Better Continents shapes nothing (the field is all Expand World Size reads)");

      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
      SetSize!.Invoke(null, [12000f, 500f]);
      C(BC.ExpandWorldSizeGeometry is { } g && g.WorldRadius == 12000f && g.EdgeSize == 500f && g.TotalRadius == 12500f, "SetSize(12000, 500): Better Continents holds Expand World Size's radius and edge (total radius 12500)");
      C(BC.Geometry.TotalRadius == 12500f && BC.TotalRadius == 12500f && BC.WorldRadius == 12000f, "and the maps span it (Geometry, TotalRadius, WorldRadius)");
      C(BC.ExpandWorldSizeSizes, "and Better Continents knows Expand World Size sizes the world");
      SetStretch!.Invoke(null, [1f, 1f]);
      C(WorldSizeHelper.ExpandWorldSizeStretch == 1f, "SetStretch(1, 1): a stretch of 1 is nothing");
      SetStretch.Invoke(null, [2f, 1f]);
      C(WorldSizeHelper.ExpandWorldSizeStretch == 2f, "SetStretch(2, 1): Better Continents keeps it");
      SetStretch.Invoke(null, [1f, 1f]);
      SetSize.Invoke(null, [10000f, 500f]);
      C(BC.Geometry.IsVanilla && BC.ExpandWorldSizeGeometry!.IsVanilla, "SetSize(10000, 500) again: the game's size");
    }
    finally
    {
      BC.Settings = saved;
    }
  }
}

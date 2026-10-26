// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).
//
// Better Continents' side of Upgrade World, against the real UpgradeWorld.dll 1.87.0 (the Thunderstore package, md5
// 2910b235b878e9ad4eb461691d4ad0d6, in libs-Tools/1.0/thirdparty/upgrade-world-2026-10-26) and the game's own code:
//   - the names Better Continents looks Upgrade World's members up by string (the plugin id, the config section and key of the World
//     radius, locations_restore's Rebuild, the upgrade command's constructor, the skip of ClearNonPlacedLocations),
//   - Upgrade World's own config bound for real (Settings.Init) and the World radius rules run on it,
//   - Upgrade World's locations_restore merge run on a made-up registry, with and without Better Continents' safeguard (the pins go
//     into the registry that locations_restore empties): without it the pins of the ids are removed, with it they stay,
//   - the game's ClearNonPlacedLocations with Upgrade World's skip on: the original is skipped and Better Continents' postfix still runs,
//   - the warning for "upgrade ashlands" / "upgrade deepnorth".
// Nothing here starts the game; the pieces of the game it needs are made without Unity (RuntimeHelpers.GetUninitializedObject).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Configuration;
using BetterContinents;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace UwTests;

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
  public static List<string> During(Action action)
  {
    int start;
    lock (Lines) start = Lines.Count;
    action();
    lock (Lines) return [.. Lines.Skip(start)];
  }
}

internal static class Program
{
  const string Libs = "/home/rohan/WubarrkCODING/libs-Tools";
  const string UwDll = Libs + "/1.0/thirdparty/upgrade-world-2026-10-26/UpgradeWorld-1.87.0/UpgradeWorld.dll";
  const string UwMd5 = "2910b235b878e9ad4eb461691d4ad0d6";
  const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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
      Path.GetDirectoryName(UwDll)!,
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
    var work = Path.Combine(Path.GetTempPath(), "bc-uw-tests-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    try
    {
      var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(UwDll))).ToLowerInvariant();
      C(md5 == UwMd5, $"the Upgrade World DLL under test is 1.87.0 (md5 {md5})");
      var uw = LoadUpgradeWorld(work);
      Names(uw);
      Config(uw, work);
      Restore(uw, work);
      Skip(uw);
      Upgrade(uw);
    }
    catch (Exception e)
    {
      failures++;
      System.Console.WriteLine("CRASH " + e);
    }
    finally
    {
      try { Directory.Delete(work, true); } catch (IOException) { }
    }
    System.Console.WriteLine($"uw-tests: {checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
  }

  // Upgrade World is built against the game's assemblies with their private members made public. Mono, the game's runtime, does not
  // check access; .NET does unless the assembly says so (Better Continents' DLL has the same attribute). The DLL is read, given the
  // attribute and loaded from a copy; its code is the real one.
  static Assembly LoadUpgradeWorld(string work)
  {
    var module = ModuleDefinition.ReadModule(UwDll);
    var attribute = new TypeReference("System", "Attribute", module, module.TypeSystem.CoreLibrary);
    var type = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit, attribute);
    var ctor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
    ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    var il = ctor.Body.GetILProcessor();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true });
    il.Emit(OpCodes.Ret);
    type.Methods.Add(ctor);
    module.Types.Add(type);
    foreach (var game in new[] { "assembly_valheim", "assembly_utils" })
      module.Assembly.CustomAttributes.Add(new CustomAttribute(ctor) { ConstructorArguments = { new CustomAttributeArgument(module.TypeSystem.String, game) } });
    var folder = Path.Combine(work, "uw");
    Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "UpgradeWorld.dll");
    module.Write(file);
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
  }

  // ------------------------------------------------------------------------------------------------ the names

  static void Names(Assembly uw)
  {
    Section("the names Better Continents looks Upgrade World's members up by");
    var plugin = uw.GetType("UpgradeWorld.UpgradeWorld");
    var attribute = plugin?.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
    C(attribute != null && (string)attribute.ConstructorArguments[0].Value! == UW.GUID, $"the plugin id is \"{UW.GUID}\" (BepInPlugin on UpgradeWorld.UpgradeWorld)");
    C((string?)attribute?.ConstructorArguments[2].Value is { } version && version.StartsWith("1.8"), "its version is 1.8x: " + attribute?.ConstructorArguments[2].Value);
    var rebuild = UW.FindRestoreRebuild(uw);
    C(rebuild != null && rebuild.IsStatic && rebuild.IsPrivate && rebuild.DeclaringType!.FullName == UW.RestoreType, $"{UW.RestoreType}.{UW.RestoreRebuild}(Dictionary<Vector2s, LocationInstance>) is found: a private static method");
    C(UW.FindRestoreRebuild(typeof(string).Assembly) == null, "(and a DLL without it finds nothing)");
    var upgrade = UW.FindUpgradeConstructor(uw);
    C(upgrade != null && upgrade.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(["Terminal", "String", "List`1", "FiltererParameters"]),
      $"{UW.UpgradeType}(Terminal, string, List<string>, FiltererParameters) is found");
    var dummy = uw.GetType("UpgradeWorld.DummyClearNonPlacedLocations");
    C(dummy?.GetField("Skip", Any) is { IsStatic: true } && dummy.GetMethod("Prefix", Any) is { IsStatic: true }, "UpgradeWorld.DummyClearNonPlacedLocations has a static Skip and a static Prefix (the dummy ClearNonPlacedLocations call of locations_add)");
    var settings = uw.GetType("UpgradeWorld.Settings");
    C(settings?.GetMethod("Init", [typeof(ConfigFile)]) is { IsStatic: true }, "UpgradeWorld.Settings.Init(ConfigFile) is there (the config is bound by it)");
    C(UW.ConfigSection == "1. General" && UW.RadiusKey == "World radius" && UW.DefaultRadius == 10500, "and Better Continents holds the section, the key and the default it was written against");
    // The members of the game that Rebuild and the merge use.
    foreach (var name in new[] { "m_locationInstances", "m_locationIDCache", "m_locationGroupCache", "m_locationMaxGroupCache", "m_generatedZones", "m_locations" })
      C(typeof(ZoneSystem).GetField(name, Any) != null, $"the game's ZoneSystem still has {name}");
    C(typeof(ZoneSystem).GetMethod("ClearNonPlacedLocations", Any, Type.EmptyTypes) != null && typeof(ZoneSystem).GetMethod("RegisterLocation", Any) != null,
      "and ClearNonPlacedLocations() and RegisterLocation, which Better Continents patches");
  }

  // ------------------------------------------------------------------------------------------------ the World radius

  static void Config(Assembly uw, string work)
  {
    Section("Upgrade World's own config, bound for real: the World radius");
    var file = new ConfigFile(Path.Combine(work, "upgrade_world.cfg"), true);
    uw.GetType("UpgradeWorld.Settings")!.GetMethod("Init", [typeof(ConfigFile)])!.Invoke(null, [file]);
    var entry = UW.FindRadiusEntry(file);
    C(entry != null && entry.Value == UW.DefaultRadius, "its \"1. General\" / \"World radius\" is an int entry of 10500 by default");
    C(UW.FindRadiusEntry(null) == null && UW.FindRadiusEntry(new ConfigFile(Path.Combine(work, "other.cfg"), true)) == null, "a config without it finds nothing");

    C(UW.RadiusFor(10500f) == 10560 && UW.RadiusFor(65000f) == 65024 && UW.RadiusFor(64f) == 64 && UW.RadiusFor(65f) == 128, "the radius is whole zones, rounded up: 10500 to 10560, 65000 to 65024");
    C(UW.RadiusFor(0f) == UW.DefaultRadius && UW.RadiusFor(float.NaN) == UW.DefaultRadius, "a size that is nothing leaves the default");
    int value;
    C(UW.DecideRadius(10500, 10500f, true, out _) == UW.RadiusAction.None, "a vanilla world: 165 zones either way, nothing to do (on or off)");
    C(UW.DecideRadius(10500, 10560f, true, out _) == UW.RadiusAction.None, "10560 m is the same 165 zones");
    C(UW.DecideRadius(10500, 12500f, true, out value) == UW.RadiusAction.Raised && value == 12544, "a world of 12500 m, on: raised to 12544");
    C(UW.DecideRadius(10500, 12500f, false, out value) == UW.RadiusAction.Suggested && value == 12544, "... off: suggested");
    C(UW.DecideRadius(20000, 12500f, true, out _) == UW.RadiusAction.None, "never lowered");

    var said = new List<string>();
    UW.ForgetNotes();
    C(UW.ApplyRadius(file, 24500f, false, said.Add) == UW.RadiusAction.Suggested && entry!.Value == 10500 && said.Count == 1, "off: the config is not touched, one line says how");
    C(said[0].Contains("24500 m from its centre") && said[0].Contains("10500 m") && said[0].Contains("Upgrade World Radius") && said[0].Contains("07 BetterContinents.Misc") && said[0].Contains("24512 m"), "it says the size, the setting and the section: " + said[0]);
    C(UW.ApplyRadius(file, 24500f, false, said.Add) == UW.RadiusAction.Suggested && said.Count == 1, "and says it once");
    C(UW.ApplyRadius(file, 24500f, true, said.Add) == UW.RadiusAction.Raised && entry!.Value == 24512, "on: the config's value is 24512 now");
    C(said.Count == 2 && said[1].Contains("was 10500 m; it is 24512 m now"), "and the line says what it was: " + said.Last());
    C(UW.ApplyRadius(file, 24500f, true, said.Add) == UW.RadiusAction.None && said.Count == 2, "on again: nothing more to do, nothing more said");
    C(UW.ApplyRadius(file, 12000f, true, said.Add) == UW.RadiusAction.None && entry!.Value == 24512, "a smaller world later does not lower it");
    C(UW.ApplyRadius(null, 24500f, true, said.Add) == UW.RadiusAction.None && said[^1].Contains("was not found"), "an Upgrade World without the setting is left alone, and the log says so: " + said[^1]);
    foreach (var line in said)
      C(!new[] { "replace", "obsolete", "no longer need", "instead of" }.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)), "calm wording: " + line);
    var setting = SettingsSchema.UpgradeWorldRadius;
    C(setting.Default == false && setting.Section == "07 BetterContinents.Misc" && setting.Scope == SettingScope.Live, "the setting is Off by default, in Misc, read while the game runs");
    C(!new[] { "replace", "obsolete", "no longer need", "instead of" }.Any(w => setting.Description.Contains(w, StringComparison.OrdinalIgnoreCase)), "and its description is calm");
  }

  // ------------------------------------------------------------------------------------------------ locations_restore

  static readonly Dictionary<uint, string> PrefabNames = [];

  static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
  static object Make(Type type) => RuntimeHelpers.GetUninitializedObject(type);

  static ZoneSystem.ZoneLocation Location(string name)
  {
    // The name the game's SoftReference gives (Name): a patch below reads it from the asset id.
    var location = new ZoneSystem.ZoneLocation { m_prefabName = name, m_group = "", m_groupMax = "", m_enable = true, m_quantity = 1 };
    var id = (object)default(SoftReferenceableAssets.AssetID);
    uint key = (uint)(PrefabNames.Count + 1);
    id.GetType().GetField("v0", Any)!.SetValue(id, key);
    PrefabNames[key] = name;
    var reference = (object)location.m_prefab;
    reference.GetType().GetField("m_assetID", Any)!.SetValue(reference, id);
    location.m_prefab = (SoftReferenceableAssets.SoftReference<GameObject>)reference;
    return location;
  }

  static ZoneSystem NewZones(IEnumerable<ZoneSystem.ZoneLocation> locations)
  {
    var zones = Make<ZoneSystem>();
    typeof(ZoneSystem).GetField("m_locations", Any)!.SetValue(zones, locations.ToList());
    typeof(ZoneSystem).GetField("m_generatedZones", Any)!.SetValue(zones, new HashSet<Vector2s>());
    foreach (var name in new[] { "m_locationInstances", "m_locationIDCache", "m_locationGroupCache", "m_locationMaxGroupCache" })
    {
      var field = typeof(ZoneSystem).GetField(name, Any)!;
      field.SetValue(zones, Activator.CreateInstance(field.FieldType));
    }
    typeof(ZoneSystem).GetField("s_instance", Any)!.SetValue(null, zones);
    return zones;
  }

  static BC.BetterContinentsSettings PinSettings(string work)
  {
    // A location map of 129 x 129 pixels with one pin, PinA, and a start position override.
    var png = Path.Combine(work, "pins", "locationmap.png");
    Directory.CreateDirectory(Path.GetDirectoryName(png)!);
    using (var image = new Image<Rgba32>(129, 129, new Rgba32(0, 0, 0, 255)))
    {
      image[100, 40] = new Rgba32(0, 255, 0, 255);
      image.SaveAsPng(png);
    }
    File.WriteAllLines(Path.ChangeExtension(png, ".txt"), ["PinA: 0,255,0"]);
    var settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, OverrideStartPosition = true, StartPositionX = 0f, StartPositionY = 0f, Version = 12 };
    typeof(BC.BetterContinentsSettings).GetField("LocationMap", Any)!.SetValue(settings, ImageMapLocation.Create(png, exactPins: true)!);
    return settings;
  }

  static void Restore(Assembly uw, string work)
  {
    Section("locations_restore: the merge of Upgrade World 1.87 over a registry that holds Better Continents' pins");
    // The game's SoftReference.Name reads the asset database; here it reads the name the test gave the location.
    var harmony = new Harmony("bc-uw-tests");
    var nameGetter = typeof(SoftReferenceableAssets.SoftReference<GameObject>).GetProperty("Name")!.GetGetMethod()!;
    harmony.Patch(nameGetter, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(NamePrefix), Any)));
    uw.GetType("UpgradeWorld.Settings")!.GetMethod("Init", [typeof(ConfigFile)])!.Invoke(null, [new ConfigFile(Path.Combine(work, "restore.cfg"), true)]);
    var rebuild = UW.FindRestoreRebuild(uw)!;
    var restoreType = uw.GetType(UW.RestoreType)!;
    var merge = restoreType.GetMethod("Merge", Any)!;

    var saved = (BC.Settings, BC.LocationGroundHeight);
    BC.LocationGroundHeight = (x, z) => 30f;
    BC.Settings = PinSettings(work);
    try
    {
      foreach (var guarded in new[] { false, true })
      {
        PrefabNames.Clear();
        var locations = new[] { Location("StartTemple"), Location("PinA"), Location("Crypt4") };
        var zones = NewZones(locations);
        var pinned = PlaceOld(zones);
        C(pinned.Count == 2, $"the registry before: the start override and the pin ({string.Join(", ", pinned.Values.Select(i => i.m_location.m_prefabName))})");

        if (guarded)
          harmony.Patch(rebuild, postfix: new HarmonyMethod(typeof(UW).GetMethod("RebuildPostfix", Any)));

        // What RestoreLocations.OnExecute does: Rebuild([]), then the search loop (here: it finds a Crypt4 somewhere), then the old
        // registry back, then Merge (RestoreLocations.cs:35-71).
        rebuild.Invoke(null, [new Dictionary<Vector2s, ZoneSystem.LocationInstance>()]);
        var emptied = zones.m_locationInstances.Values.Select(i => i.m_location.m_prefabName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Register(zones, locations[2], new Vector3(640f, 30f, 640f));
        var fresh = new Dictionary<Vector2s, ZoneSystem.LocationInstance>(zones.m_locationInstances);
        zones.m_locationInstances = new(pinned);

        var operation = Make(restoreType);
        var args = Make(uw.GetType("UpgradeWorld.FiltererParameters")!);
        foreach (var field in args.GetType().GetFields(Any))
        {
          var type = field.FieldType;
          if (type == typeof(float) && field.Name == "Chance")
            field.SetValue(args, 1f);
          else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            field.SetValue(args, Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments())));
          else if (!type.IsValueType && type != typeof(string) && !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) != null && field.GetValue(args) == null)
            field.SetValue(args, Activator.CreateInstance(type));
        }
        Set(operation, "Ids", new HashSet<string> { "PinA", "StartTemple" });
        Set(operation, "Args", args);
        Set(operation, "Explored", false);
        Set(operation, "Pins", new List<Vector3>());
        var merged = (Dictionary<Vector2s, ZoneSystem.LocationInstance>)merge.Invoke(operation, [pinned, fresh])!;
        var kept = merged.Values.Select(i => i.m_location.m_prefabName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!guarded)
        {
          C(emptied.Count == 0, "without the safeguard: the registry Upgrade World empties holds no pin when its search loop runs");
          C(!kept.Contains("PinA") && !kept.Contains("StartTemple"), "... and the merge removes the unplaced pins of the ids it was given (" + string.Join(", ", kept) + "): the problem, reproduced against the real merge");
        }
        else
        {
          C(emptied.SequenceEqual(["PinA", "StartTemple"]), "with the safeguard: the emptied registry holds the start override and the pin when the loop runs");
          C(kept.Contains("PinA") && kept.Contains("StartTemple"), "... and the real merge finds each pin where it is and keeps it (" + string.Join(", ", kept) + ")");
          C(!LogHandler.Lines.Any(l => l.Contains("NOT placed")), "... without a warning about a zone that is taken");
          // A Rebuild that is given a registry to put back is none of Better Continents' business.
          zones.m_locationInstances = [];
          rebuild.Invoke(null, [new Dictionary<Vector2s, ZoneSystem.LocationInstance>(pinned)]);
          C(zones.m_locationInstances.Count == 2, "Rebuild(old) puts back the old registry as it is: two entries, no pin put in twice");
          harmony.Unpatch(rebuild, HarmonyPatchType.Postfix, harmony.Id);
        }
      }

      Section("the pins in the registry again: quiet when they are there already (locations_add's dummy ClearNonPlacedLocations)");
      PrefabNames.Clear();
      var locs = new[] { Location("StartTemple"), Location("PinA"), Location("Crypt4") };
      var again = NewZones(locs);
      PlaceOld(again);
      int before = again.m_locationInstances.Count;
      var lines = LogHandler.During(() => BC.ReassertLocationPins(again));
      C(again.m_locationInstances.Count == before && !lines.Any(l => l.StartsWith("[Warning]") || l.StartsWith("[Error]")), "a second round of the same pins registers none again and warns of none: " + string.Join(" | ", lines.Where(l => !l.StartsWith("[Log]"))));
    }
    finally
    {
      (BC.Settings, BC.LocationGroundHeight) = saved;
      typeof(ZoneSystem).GetField("s_instance", Any)!.SetValue(null, null);
    }
  }

  // ZoneSystem.RegisterLocation is private in the game.
  static void Register(ZoneSystem zones, ZoneSystem.ZoneLocation location, Vector3 position) =>
    typeof(ZoneSystem).GetMethod("RegisterLocation", Any)!.Invoke(zones, [location, position, false]);

  static Dictionary<Vector2s, ZoneSystem.LocationInstance> PlaceOld(ZoneSystem zones)
  {
    BC.ReassertLocationPins(zones);
    return new Dictionary<Vector2s, ZoneSystem.LocationInstance>(zones.m_locationInstances);
  }

  static void Set(object target, string name, object? value)
  {
    for (var type = target.GetType(); type != null; type = type.BaseType)
      if (type.GetField(name, Any | BindingFlags.DeclaredOnly) is { } field)
      {
        field.SetValue(target, value);
        return;
      }
    throw new MissingFieldException(target.GetType().FullName, name);
  }

  static bool NamePrefix(ref SoftReferenceableAssets.SoftReference<GameObject> __instance, ref string __result)
  {
    var id = __instance.GetType().GetField("m_assetID", Any)!.GetValue(__instance)!;
    var key = (uint)id.GetType().GetField("v0", Any)!.GetValue(id)!;
    __result = PrefabNames.TryGetValue(key, out var name) ? name : "";
    return false;
  }

  // ------------------------------------------------------------------------------------------------ the dummy ClearNonPlacedLocations

  static void Skip(Assembly uw)
  {
    Section("locations_add's dummy ClearNonPlacedLocations: the original is skipped, Better Continents' pins are put in");
    var harmony = new Harmony("bc-uw-tests-skip");
    var dummy = uw.GetType("UpgradeWorld.DummyClearNonPlacedLocations")!;
    var clear = typeof(ZoneSystem).GetMethod("ClearNonPlacedLocations", Any, Type.EmptyTypes)!;
    var postfix = typeof(BC).GetNestedType("ZoneSystemPatch", BindingFlags.NonPublic)!.GetMethod("ClearNonPlacedLocationsPostfix", Any)!;
    harmony.Patch(clear, prefix: new HarmonyMethod(dummy.GetMethod("Prefix", Any)), postfix: new HarmonyMethod(postfix));
    var skip = dummy.GetField("Skip", Any)!;
    var saved = (BC.Settings, BC.LocationGroundHeight);
    var work = Path.Combine(Path.GetTempPath(), "bc-uw-tests-skip-" + Environment.ProcessId);
    try
    {
      BC.LocationGroundHeight = (x, z) => 30f;
      BC.Settings = PinSettings(work);
      PrefabNames.Clear();
      var locations = new[] { Location("StartTemple"), Location("PinA") };
      var zones = NewZones(locations);
      // A location that is not placed and no pin: the original ClearNonPlacedLocations removes it.
      Register(zones, locations[1], new Vector3(1600f, 30f, 1600f));
      skip.SetValue(null, true);
      clear.Invoke(zones, null);
      skip.SetValue(null, false);
      var names = zones.m_locationInstances.Values.Select(i => i.m_location.m_prefabName).OrderBy(n => n, StringComparer.Ordinal).ToList();
      C(zones.m_locationInstances.ContainsKey(ZoneSystem.GetZone(new Vector3(1600f, 30f, 1600f))), "with the skip on, the original did not run: the unplaced location is still in the registry");
      C(names.Count(n => n == "PinA") == 2 && names.Contains("StartTemple"), "and Better Continents' postfix did run: the pin and the start override were put in (" + string.Join(", ", names) + ")");
      clear.Invoke(zones, null);
      C(zones.m_locationInstances.Count == 2 && !zones.m_locationInstances.ContainsKey(ZoneSystem.GetZone(new Vector3(1600f, 30f, 1600f))), "with the skip off the original runs as the game does: the unplaced location goes, the pins are put in again");
    }
    finally
    {
      skip.SetValue(null, false);
      harmony.UnpatchAll(harmony.Id);
      (BC.Settings, BC.LocationGroundHeight) = saved;
      typeof(ZoneSystem).GetField("s_instance", Any)!.SetValue(null, null);
      try { Directory.Delete(work, true); } catch (IOException) { }
    }
  }

  // ------------------------------------------------------------------------------------------------ upgrade ashlands

  static void Upgrade(Assembly uw)
  {
    Section("upgrade ashlands / deepnorth on a world that is not the game's layout");
    var harmony = new Harmony("bc-uw-tests-upgrade");
    var ctor = UW.FindUpgradeConstructor(uw)!;
    try
    {
      harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(UW).GetMethod("UpgradePrefix", Any)));
      C(Harmony.GetPatchInfo(ctor)?.Prefixes.Any(p => p.PatchMethod.Name == "UpgradePrefix") == true, "the prefix binds to the upgrade command's constructor (positional parameters __0 and __1 accepted by Harmony)");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
    }
    var big = new WorldGeometry(24000f, 500f);
    var settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
    C(UW.UpgradeSelectsByDistance("ashlands", settings, big) && UW.UpgradeSelectsByDistance("DeepNorth", settings, big), "a world laid out to 24000 m: both are by distance from the game's layout");
    C(!UW.UpgradeSelectsByDistance("ashlands", settings, WorldGeometry.Vanilla), "a world with the game's layout and no biome map: no warning");
    C(!UW.UpgradeSelectsByDistance("mistlands", settings, big) && !UW.UpgradeSelectsByDistance(null, settings, big) && !UW.UpgradeSelectsByDistance("tarpits", settings, big), "other upgrades: no warning");
    C(!UW.UpgradeSelectsByDistance("ashlands", new BC.BetterContinentsSettings { EnabledForThisWorld = false }, big), "a world without Better Continents: no warning");
    var savedGeometry = BC.Geometry;
    var savedSettings = BC.Settings;
    try
    {
      BC.Geometry = big;
      BC.Settings = settings;
      UW.ForgetNotes();
      var log = new List<string>();
      var console = new List<string>();
      UW.Note("mistlands", log.Add, console.Add);
      C(log.Count == 0, "mistlands: nothing said");
      UW.Note("Ashlands", log.Add, console.Add);
      UW.Note("deepnorth", log.Add, console.Add);
      C(log.Count == 1 && console.Count == 1 && log[0] == console[0], "said once, to the log and the console that asked: " + log.FirstOrDefault());
      C(log[0].Contains("11,600 m") && log[0].Contains("not by biome") && log[0].Contains("bc regen dry biome=AshLands") && log[0].Contains("bc regen biome=AshLands"), "it says how it chooses and the biome-keyed way");
      UW.ForgetNotes();
      log.Clear();
      UW.Note("deepnorth", log.Add, null);
      C(log.Count == 1 && log[0].Contains("biome=DeepNorth") && log[0].Contains("south of the centre"), "deepnorth names its biome: " + log.FirstOrDefault());
      C(!new[] { "replace", "obsolete", "no longer need", "instead of" }.Any(w => log[0].Contains(w, StringComparison.OrdinalIgnoreCase)), "calm wording");
    }
    finally
    {
      BC.Geometry = savedGeometry;
      BC.Settings = savedSettings;
      UW.ForgetNotes();
    }
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Every transpiler of HighTerrainPatches on the IL of the client's assembly and of the dedicated server's (a second assembly, loaded
// in a context of its own), the patches that can run here run on stand-ins, and an inventory of the methods of both assemblies
// that hold the numbers the patches replace.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  // The dedicated server's game assemblies, next to the client's: the same code in a different build. Loaded in a context of their
  // own, which finds every assembly it needs in the server's folder first.
  private sealed class ServerContext : AssemblyLoadContext
  {
    private readonly string dir;
    public ServerContext(string dir) : base("server", false) => this.dir = dir;

    protected override Assembly Load(AssemblyName name)
    {
      var path = Path.Combine(dir, name.Name + ".dll");
      return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
  }

  private static Assembly serverAssembly;

  private static (string name, Assembly assembly)[] GameAssemblies()
  {
    serverAssembly ??= new ServerContext(Path.Combine(Libs, "1.0", "server")).LoadFromAssemblyPath(Path.Combine(Libs, "1.0", "server", "assembly_valheim.dll"));
    return [("client", typeof(Character).Assembly), ("server", serverAssembly)];
  }

  private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

  // A method of an assembly by its type and name, and its number of parameters when the name is not enough; the iterator of a
  // coroutine by the name of the method it is made from.
  private static MethodBase Method(Assembly assembly, string type, string name, int? parameters = null, bool moveNext = false)
  {
    var t = assembly.GetType(type) ?? throw new InvalidOperationException($"no type {type}");
    var all = t.GetMethods(Any).Where(m => m.Name == name && (parameters == null || m.GetParameters().Length == parameters)).ToList();
    if (all.Count != 1)
      throw new InvalidOperationException($"{type}.{name}: {all.Count} methods");
    return moveNext ? AccessTools.EnumeratorMoveNext(all[0]) : all[0];
  }

  private static string Show(CodeInstruction code) => code.operand switch
  {
    float f => $"{code.opcode.Name} {f.ToString("R", CultureInfo.InvariantCulture)}",
    MemberInfo m => $"{code.opcode.Name} {m.Name}",
    Label => code.opcode.Name,
    null => code.opcode.Name,
    var o => $"{code.opcode.Name} {o}",
  };

  // A method's instructions as Harmony reads them, or Cecil's when .NET cannot load the types of its signature here.
  private static List<CodeInstruction> Instructions(string assembly, MethodBase method, int? parameters = null)
  {
    try
    {
      return PatchProcessor.GetOriginalInstructions(method);
    }
    catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
    {
      return Decode(Path.Combine(Libs, "1.0", assembly, "assembly_valheim.dll"), method.DeclaringType!.FullName!, method.Name, parameters);
    }
  }

  // A transpiler run on copies of the instructions (with their labels and blocks, which Clone drops).
  private static List<CodeInstruction> Run(string transpiler, List<CodeInstruction> instructions, MethodBase original = null) =>
    ((IEnumerable<CodeInstruction>)typeof(HighTerrainPatches).GetMethod(transpiler, BindingFlags.NonPublic | BindingFlags.Static)
      .Invoke(null, [instructions.Select(i => new CodeInstruction(i)).ToList(), original])).ToList();

  // What a transpiler added and what it removed, as the multisets of instructions "opcode name".
  private static (List<string> added, List<string> removed) Delta(List<CodeInstruction> before, List<CodeInstruction> after)
  {
    var added = after.Select(Show).ToList();
    var removed = new List<string>();
    foreach (var s in before.Select(Show))
      if (!added.Remove(s))
        removed.Add(s);
    return (added, removed);
  }

  private static bool LabelsKept(List<CodeInstruction> before, List<CodeInstruction> after) =>
    before.SelectMany(i => i.labels).SequenceEqual(after.SelectMany(i => i.labels)) && before.Sum(i => i.blocks.Count) == after.Sum(i => i.blocks.Count);

  private static bool IsInInteriorCall(CodeInstruction i) =>
    (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase { Name: "InInterior" } m && m.DeclaringType?.Name == "Character";

  // The game's methods each transpiler rewrites, as (type, method, parameters).
  private static readonly (string type, string method, int? parameters, int calls)[] InteriorCallers =
  [
    ("Character", "Awake", null, 1), ("Character", "UpdateWalking", null, 1), ("Player", "PlacePiece", null, 1), ("Player", "UpdatePlacementGhost", null, 1),
    ("Attack", "Start", null, 1), ("BaseAI", "CanHearTarget", 3, 1), ("BaseAI", "CanUseAttack", null, 1), ("EnvMan", "UpdateEnvironment", null, 1),
    ("EnvMan", "UpdateWind", null, 1), ("RenderGroupSystem", "LateUpdate", null, 1), ("Location", "GetLocation", null, 1), ("Teleport", "Interact", null, 2),
  ];

  private static void IlTests()
  {
    foreach (var (name, assembly) in GameAssemblies())
    {
      Section($"the transpilers on the {name}'s game assembly");
      int mark = CapturingLogHandler.Lines.Count;

      foreach (var (type, method, parameters, calls) in InteriorCallers)
      {
        var m = Method(assembly, type, method, parameters);
        var before = Instructions(name, m);
        var after = Run("RedirectInterior", before, m);
        Check(before.Count(IsInInteriorCall) == calls && after.Count(IsInInteriorCall) == 0
              && after.Count(i => i.opcode == OpCodes.Call && i.operand is MethodInfo { Name: "Interior" } mi && mi.DeclaringType == typeof(HighTerrain)) == calls
              && after.Count == before.Count && LabelsKept(before, after),
          $"{name} {type}.{method}: its {calls} call(s) of Character.InInterior become HighTerrain.Interior, nothing else changed");
      }
      // Each overload keeps its argument: the Vector3, the Transform, and the character itself.
      var overloads = new[] { ("Location", "GetLocation", "Vector3"), ("BaseAI", "CanHearTarget", "Transform"), ("EnvMan", "UpdateEnvironment", "Character") };
      foreach (var (type, method, argument) in overloads)
      {
        var m = Method(assembly, type, method, method == "CanHearTarget" ? 3 : null);
        var after = Run("RedirectInterior", Instructions(name, m), m);
        var target = after.Select(i => i.operand).OfType<MethodInfo>().First(mi => mi.Name == "Interior" && mi.DeclaringType == typeof(HighTerrain));
        Check(target.GetParameters().Single().ParameterType.Name == argument, $"{name} {type}.{method} calls the HighTerrain.Interior of a {argument}");
      }

      // `y > 3000f` of a point, where an event is told to a player and where one is started.
      foreach (var (method, branch) in new[] { ("IsInsideRandomEventArea", "brfalse.s"), ("GetValidEventPoints", "brtrue.s") })
      {
        var m = Method(assembly, "RandEventSystem", method);
        var before = Instructions(name, m);
        var after = Run("RedirectEventHeight", before, m);
        var (added, removed) = Delta(before, after);
        var was = method == "IsInsideRandomEventArea" ? "ble.un.s" : "bgt.s";
        Check(added.SequenceEqual(["call Interior", branch]) && removed.OrderBy(s => s).SequenceEqual(new[] { "ldfld y", "ldc.r4 3000", was }.OrderBy(s => s)) && LabelsKept(before, after),
          $"{name} RandEventSystem.{method}: `y > 3000` becomes HighTerrain.Interior of the point (added {string.Join(", ", added)}; removed {string.Join(", ", removed)})");
      }

      // The rays.
      foreach (var (type, method, parameters, transpiler, adds) in new (string, string, int?, string, string[])[]
      {
        ("ZoneSystem", "GetGroundHeight", 1, "RaiseGroundRay", ["call RayStart", "ldc.r4 6000", "call RayLength"]),
        ("ZoneSystem", "GetGroundHeight", 2, "RaiseGroundRay", ["call RayStart", "ldc.r4 6000", "call RayLength"]),
        ("Pathfinding", "FindGround", null, "RaiseGroundRay", ["call RayStart", "ldc.r4 6000", "call RayLength"]),
        ("ZoneSystem", "GetGroundData", null, "RaiseGroundDataRay", ["call RaiseOrigin", "ldc.r4 5000", "call RayLength"]),
        ("ClutterSystem", "GetGroundInfo", null, "RaiseClutterRay", ["call RaiseOrigin", "ldc.r4 500", "call RayLength"]),
        ("ZoneSystem", "IsBlocked", null, "RaiseBlockedRay", ["ldarg.1", "call BlockerLift"]),
      })
      {
        var m = Method(assembly, type, method, parameters);
        var before = Instructions(name, m);
        var after = Run(transpiler, before, m);
        var (added, removed) = Delta(before, after);
        bool blocked = transpiler == "RaiseBlockedRay";
        Check(added.OrderBy(s => s).SequenceEqual(adds.OrderBy(s => s)) && removed.SequenceEqual(blocked ? ["ldc.r4 2000"] : []) && LabelsKept(before, after),
          $"{name} {type}.{method}({parameters}): only {string.Join(", ", adds)} added (added {string.Join(", ", added)}; removed {string.Join(", ", removed)})");
        if (blocked)
        {
          int lift = after.FindIndex(i => i.opcode == OpCodes.Call && i.operand is MethodInfo { Name: "BlockerLift" });
          Check(after[lift - 1].opcode == OpCodes.Ldarg_1 && after[lift + 1].opcode == OpCodes.Add && after[lift + 2].opcode == OpCodes.Stind_R4 && after.FindIndex(i => i.operand is float f && f == 10000f) >= 0,
            $"{name} {type}.{method}: `p.y += 2000f` adds BlockerLift(p) (the ldarg.1 before it is the point) before it is stored, and the ray stays 10000 m");
          continue;
        }
        // Where they sit: the start right after the 6000 (or the addition of 5000 or 500), the length followed by its vanilla start.
        float up = transpiler == "RaiseGroundRay" ? 6000f : transpiler == "RaiseGroundDataRay" ? 5000f : 500f;
        float vanillaLength = transpiler == "RaiseClutterRay" ? 1000f : 10000f;
        int length = after.FindIndex(i => i.operand is float f && f == vanillaLength);
        Check(after[length + 1].operand is float vanillaStart && vanillaStart == up && Show(after[length + 2]) == "call RayLength", $"{name} {type}.{method}({parameters}): the {vanillaLength} m ray's length goes through RayLength with its start of {up} m");
        if (transpiler == "RaiseGroundRay")
        {
          int start = after.FindIndex(i => i.operand is float f && f == 6000f);
          Check(Show(after[start + 1]) == "call RayStart" && after[start + 2].opcode == OpCodes.Stfld, $"{name} {type}.{method}({parameters}): `origin.y = 6000f` goes through RayStart");
        }
        else
        {
          int offset = after.FindIndex(i => i.operand is float f && f == up);
          Check(Show(after[offset + 1]) == "call op_Multiply" && Show(after[offset + 2]) == "call op_Addition" && Show(after[offset + 3]) == "call RaiseOrigin",
            $"{name} {type}.{method}: the origin {up} m over the point goes through RaiseOrigin");
        }
      }

      // The altitude limits and the elevations.
      foreach (var (type, method, moveNext, transpiler, add) in new (string, string, bool, string, string)[]
      {
        ("ZoneSystem", "PlaceVegetation", false, "LiftMaxAltitude", "call MaxAltitude"),
        ("ZoneSystem", "GenerateLocationsTimeSliced", true, "LiftMaxAltitude", "call MaxAltitude"),
        ("SpawnSystem", "IsSpawnPointGood", false, "LiftMaxAltitude", "call MaxAltitude"),
        ("ClutterSystem", "GenerateVegPatch", false, "LiftMaxAltitude", "call MaxAltitude"),
        ("RandomSpawn", "Randomize", false, "LiftMaxElevation", "call MaxElevation"),
        ("RandomObject", "Randomize", false, "LiftMaxElevation", "call MaxElevation"),
      })
      {
        var m = moveNext ? Method(assembly, type, method, 3, moveNext: true) : Method(assembly, type, method);
        var before = Instructions(name, m);
        var after = Run(transpiler, before, m);
        var (added, removed) = Delta(before, after);
        int read = after.FindIndex(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo { Name: "m_maxAltitude" or "m_maxAlt" or "m_maxElevation" });
        Check(added.SequenceEqual([add]) && removed.Count == 0 && LabelsKept(before, after) && Show(after[read + 1]) == add,
          $"{name} {type}.{method}: the upper limit is read through {add} (added {string.Join(", ", added)}; removed {string.Join(", ", removed)})");
      }

      Check(!CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("High terrain")), $"{name}: no transpiler found less than it expected in a real method");
    }

    // A method without what the transpiler looks for is reported, and left as it was.
    var ground = Method(typeof(Character).Assembly, "ZoneSystem", "FindFloor");
    int before0 = CapturingLogHandler.Lines.Count;
    var original = PatchProcessor.GetOriginalInstructions(ground);
    var untouched = Run("RaiseGroundRay", original, ground);
    Check(untouched.Select(Show).SequenceEqual(original.Select(Show)) && CapturingLogHandler.Lines.Skip(before0).Any(l => l.Contains("did not find") && l.Contains("ZoneSystem.FindFloor")),
      "a method without the 6000 m and 10000 m of a ground ray is left alone, and the log says which");
  }

  // ---- patches that run here -------------------------------------------------------------------------------------------
  private static void RunningTests()
  {
    Section("patched for real, on the game's own methods that .NET can compile");
    var harmony = new Harmony("high-tests.running");
    try
    {
      // Character.InInterior(Vector3): the prefix replaces it (the game's own is 14 bytes of IL and is built into its callers).
      var inInterior = typeof(Character).GetMethod("InInterior", Any, null, [typeof(Vector3)], null);
      object Call(Vector3 p) => inInterior.Invoke(null, [p]);
      HighTerrain.Update(HighWorld(81f));
      HighTerrain.GroundSource = p => p.x > 0f ? 8000f : 30f;
      Check((bool)Call(new Vector3(10f, 8100f, 0f)) && (bool)Call(new Vector3(-10f, 5030f, 0f)), "unpatched: the game's rule, anything over 3000 m is inside");
      harmony.CreateProcessor(inInterior).AddPrefix(new HarmonyMethod(typeof(HighTerrainPatches).GetMethod("InteriorOfPoint", Any))).Patch();
      Check(!(bool)Call(new Vector3(10f, 8100f, 0f)) && (bool)Call(new Vector3(-10f, 5030f, 0f)) && !(bool)Call(new Vector3(-10f, 2500f, 0f)),
        "patched with the prefix: 8100 m over a mountain of 8000 m is outside, a dungeon (5030 m over the sea) is inside, 2500 m is outside");
      harmony.Unpatch(inInterior, HarmonyPatchType.All, harmony.Id);

      // RandEventSystem.IsInsideRandomEventArea, rewritten, on a system made without its engine object.
      var area = typeof(RandEventSystem).GetMethod("IsInsideRandomEventArea", Any);
      var system = RuntimeHelpers.GetUninitializedObject(typeof(RandEventSystem));
      GC.SuppressFinalize(system);
      var @event = (RandomEvent)RuntimeHelpers.GetUninitializedObject(typeof(RandomEvent));
      @event.m_pos = new Vector3(0f, 0f, 0f);
      @event.m_eventRange = 100f;
      bool Inside(Vector3 p) => (bool)area.Invoke(system, [@event, p]);
      Check(!Inside(new Vector3(10f, 8100f, 10f)) && !Inside(new Vector3(10f, 3001f, 10f)) && Inside(new Vector3(10f, 2999f, 10f)) && !Inside(new Vector3(500f, 100f, 10f)),
        "unpatched: an event is not told to anyone over 3000 m, nor beyond its range");
      harmony.CreateProcessor(area).AddTranspiler(new HarmonyMethod(typeof(HighTerrainPatches).GetMethod("RedirectEventHeight", Any))).Patch();
      HighTerrain.GroundSource = p => 8000f;
      Check(Inside(new Vector3(10f, 8100f, 10f)) && Inside(new Vector3(10f, 2999f, 10f)) && !Inside(new Vector3(500f, 8100f, 10f)),
        "patched: a player on a mountain of 8000 m is told the event, one beyond its range is not");
      HighTerrain.GroundSource = p => 30f;
      Check(!Inside(new Vector3(10f, 5030f, 10f)) && Inside(new Vector3(10f, 2999f, 10f)), "and a player in a dungeon (5030 m over a ground of 30 m) is not");
      harmony.Unpatch(area, HarmonyPatchType.All, harmony.Id);
      HighTerrain.GroundSource = p => 8000f;
      Check(!Inside(new Vector3(10f, 8100f, 10f)), "unpatched again: the game's own rule");

      // The AI's tile.
      var position = new Vector3(320f, 2500f, 640f);
      HighTerrainPatches.TilePosition(ref position);
      Check(position == new Vector3(320f, 10500f, 640f), "Pathfinding.GetTilePos' result over ground of 8000 m: centred on 10500 m, x and z as they were");
      HighTerrain.GroundSource = p => 30f;
      position = new Vector3(320f, 2500f, 640f);
      HighTerrainPatches.TilePosition(ref position);
      Check(position == new Vector3(320f, 2500f, 640f), "and over the sea level ground: the game's 2500 m");
    }
    catch (Exception e)
    {
      Check(false, $"patching and running the game's own methods: {e.GetType().FullName}: {e.Message} / {e.InnerException?.GetType().FullName}: {e.InnerException?.Message}");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
      HighTerrain.GroundSource = null;
      HighTerrain.Update(new BC.BetterContinentsSettings());
    }
  }

  // ---- "dump" ---------------------------------------------------------------------------------------------------------
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static int Dump()
  {
    Debug.unityLogger.logHandler = new CapturingLogHandler();
    foreach (var (name, assembly) in GameAssemblies())
    {
      foreach (var (transpiler, type, method, parameters, moveNext) in new (string, string, string, int?, bool)[]
      {
        ("RedirectInterior", "Teleport", "Interact", null, false), ("RedirectEventHeight", "RandEventSystem", "IsInsideRandomEventArea", null, false),
        ("RedirectEventHeight", "RandEventSystem", "GetValidEventPoints", null, false), ("RaiseGroundRay", "ZoneSystem", "GetGroundHeight", 1, false),
        ("RaiseGroundRay", "ZoneSystem", "GetGroundHeight", 2, false), ("RaiseGroundRay", "Pathfinding", "FindGround", null, false),
        ("RaiseGroundDataRay", "ZoneSystem", "GetGroundData", null, false), ("RaiseBlockedRay", "ZoneSystem", "IsBlocked", null, false),
        ("LiftMaxAltitude", "ZoneSystem", "PlaceVegetation", null, false), ("LiftMaxAltitude", "ZoneSystem", "GenerateLocationsTimeSliced", 3, true),
        ("LiftMaxAltitude", "SpawnSystem", "IsSpawnPointGood", null, false), ("LiftMaxAltitude", "ClutterSystem", "GenerateVegPatch", null, false),
        ("RaiseClutterRay", "ClutterSystem", "GetGroundInfo", null, false), ("LiftMaxElevation", "RandomSpawn", "Randomize", null, false),
      })
      {
        var m = Method(assembly, type, method, parameters, moveNext);
        var before = Instructions(name, m);
        var after = Run(transpiler, before, m);
        var (added, removed) = Delta(before, after);
        System.Console.WriteLine($"{name} {type}.{method}: {transpiler}: {before.Count} -> {after.Count} instructions; added {string.Join(", ", added)}; removed {string.Join(", ", removed)}");
      }
    }
    return 0;
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
  // Whether EnvMan's Deep North weather test asks at the camera's x and z instead of the game's (x, height): on a world with a
  // biome map (which IsDeepnorth then reads), and on a high world, where the camera's height itself puts the game's test
  // inside the Deep North's circle from about 8000 m, wherever it is.
  internal static bool DeepNorthWeatherUsesZ(BetterContinentsSettings settings) =>
    settings.EnabledForThisWorld && (settings.HasBiomeMap || HighTerrain.Wanted(settings));

  // DynamicPatch's first step: the state the patched game code reads, then each group on or off. One that fails is logged and the
  // others go on: the world loads either way, as vanilla's heights would. It comes first, and nothing in it depends on another
  // step, so that a step that throws later cannot leave the patches of a high world on for the next world.
  internal static void PatchHighTerrain()
  {
    HighTerrain.Update(Settings);
    foreach (var toggle in HighTerrainToggles)
    {
      try
      {
        toggle.Update(Settings);
      }
      catch (Exception e)
      {
        LogError($"High terrain: {toggle.Name}: {e.GetType().Name}: {e.Message}");
      }
    }
  }

  // A target that cannot be told from another (the game has a second overload, or a mod changed it) is "not found" for
  // Toggle.Update, which logs it and leaves the group off, instead of an exception that would stop DynamicPatch and so the
  // world's load.
  private static Hook OnGame(Type type, string method, Type[]? arguments, string patch, HookKind kind) =>
    OnGame(() => arguments == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, arguments), type.Name + "." + method, patch, kind);

  private static Hook OnGame(Func<MethodBase?> method, string description, string patch, HookKind kind) =>
    new(() =>
    {
      try
      {
        return method();
      }
      catch (Exception e)
      {
        LogWarning($"High terrain: {description}: {e.GetType().Name}: {e.Message}");
        return null;
      }
    }, description, typeof(HighTerrainPatches), patch, kind);

  // The patches of the game's height rules (HighTerrain.cs), on while the loaded world wants them (HighTerrain.Wanted: its High
  // Terrain setting, by default a heightmap read at an amount above 5) and off for every other world, which keeps the game's own
  // code. Switched by DynamicPatch with the others. Each group is a Toggle of its own, so that one the installed game (or another
  // mod) does not allow is logged and leaves the rest.
  private static readonly Toggle[] HighTerrainToggles =
  [
    // Character.InInterior is "higher than 3000 m" (Character.cs:4372): standing on a mountain above that is being in a
    // dungeon, which switches off the terrain's render group (RenderGroupSystem.cs:60, Heightmap.c_RenderGroup), the weather,
    // building and the random events. The three ways of asking, patched for every caller (a mod's), and each of the game's
    // twelve methods that ask (thirteen calls: Teleport.Interact asks twice), rewritten to call HighTerrain.Interior: Mono builds
    // the 12-14 byte InInterior methods into the methods that call them, so a patch of them is not seen by a caller that was
    // compiled before it (measured on the dedicated server).
    new("High terrain: Character.InInterior and the methods that call it", HighTerrain.Wanted,
      OnGame(typeof(Character), nameof(Character.InInterior), [typeof(Vector3)], nameof(HighTerrainPatches.InteriorOfPoint), HookKind.Prefix),
      OnGame(typeof(Character), nameof(Character.InInterior), [typeof(Transform)], nameof(HighTerrainPatches.InteriorOfTransform), HookKind.Prefix),
      OnGame(typeof(Character), nameof(Character.InInterior), [], nameof(HighTerrainPatches.InteriorOfCharacter), HookKind.Prefix),
      OnGame(typeof(Character), "Awake", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Character), "UpdateWalking", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Player), "PlacePiece", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Player), "UpdatePlacementGhost", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Attack), "Start", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(BaseAI), "CanHearTarget", [typeof(Transform), typeof(float), typeof(Character)], nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(BaseAI), "CanUseAttack", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(EnvMan), "UpdateEnvironment", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(EnvMan), "UpdateWind", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(RenderGroupSystem), "LateUpdate", null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Location), nameof(Location.GetLocation), null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler),
      OnGame(typeof(Teleport), nameof(Teleport.Interact), null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch what is inside a dungeon, so the game's own 3000 m rule stays: " },

    // RandEventSystem tests "y > 3000" itself, where an event is not told to a player or started near one (RandEventSystem.cs
    // :119, :436).
    new("High terrain: random events", HighTerrain.Wanted,
      OnGame(typeof(RandEventSystem), "IsInsideRandomEventArea", null, nameof(HighTerrainPatches.RedirectEventHeight), HookKind.Transpiler),
      OnGame(typeof(RandEventSystem), "GetValidEventPoints", null, nameof(HighTerrainPatches.RedirectEventHeight), HookKind.Transpiler))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch the random events' height rule, so they keep the game's own 3000 m: " },

    // The ground rays that start at a height of their own: GetGroundHeight at 6000 m (the player's way home after logging out,
    // the check that lifts a character from under the ground, tombstones, snapping), GetGroundData at 5000 m over its point
    // (which a zone's vegetation, with y 0, and a creature's spawn read the terrain by), IsBlocked at 2000 m over its point
    // (a zone's vegetation again), the grass's GetGroundInfo at 500 m over its point, and the AI's FindGround at 6000 m.
    new("High terrain: the ground rays", HighTerrain.Wanted,
      OnGame(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), [typeof(Vector3)], nameof(HighTerrainPatches.RaiseGroundRay), HookKind.Transpiler),
      OnGame(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), [typeof(Vector3), typeof(float).MakeByRefType()], nameof(HighTerrainPatches.RaiseGroundRay), HookKind.Transpiler),
      OnGame(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundData), null, nameof(HighTerrainPatches.RaiseGroundDataRay), HookKind.Transpiler),
      OnGame(typeof(ZoneSystem), nameof(ZoneSystem.IsBlocked), null, nameof(HighTerrainPatches.RaiseBlockedRay), HookKind.Transpiler),
      OnGame(typeof(Pathfinding), "FindGround", null, nameof(HighTerrainPatches.RaiseGroundRay), HookKind.Transpiler),
      OnGame(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo), null, nameof(HighTerrainPatches.RaiseClutterRay), HookKind.Transpiler))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch the ground rays, so ground above 5000 m is not found: " },

    // The AI's navigation tiles are 6000 m high and centred on 2500 m: -500 m to 5500 m (Pathfinding.cs:107-109, :850).
    new("High terrain: the AI's navigation tiles", HighTerrain.Wanted,
      OnGame(typeof(Pathfinding), nameof(Pathfinding.GetTilePos), null, nameof(HighTerrainPatches.TilePosition), HookKind.Postfix))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch the AI's navigation tiles, so creatures walk only between -500 m and 5500 m: " },

    // The upper altitude of a plant (ZoneSystem.PlaceVegetation), a location (the placement coroutine), a creature
    // (SpawnSystem.IsSpawnPointGood) and the grass (ClutterSystem.GenerateVegPatch): 1000 m over the water unless an entry says
    // otherwise, and the parts of a location that RandomSpawn and RandomObject switch off above 10000 m.
    new("High terrain: altitude limits", HighTerrain.Wanted,
      OnGame(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation), null, nameof(HighTerrainPatches.LiftMaxAltitude), HookKind.Transpiler),
      OnGame(() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GenerateLocationsTimeSliced),
        [typeof(ZoneSystem.ZoneLocation), typeof(System.Diagnostics.Stopwatch), typeof(ZPackage)])), "ZoneSystem.GenerateLocationsTimeSliced", nameof(HighTerrainPatches.LiftMaxAltitude), HookKind.Transpiler),
      OnGame(typeof(SpawnSystem), "IsSpawnPointGood", null, nameof(HighTerrainPatches.LiftMaxAltitude), HookKind.Transpiler),
      OnGame(typeof(ClutterSystem), "GenerateVegPatch", null, nameof(HighTerrainPatches.LiftMaxAltitude), HookKind.Transpiler),
      OnGame(typeof(RandomSpawn), nameof(RandomSpawn.Randomize), null, nameof(HighTerrainPatches.LiftMaxElevation), HookKind.Transpiler),
      OnGame(typeof(RandomObject), nameof(RandomObject.Randomize), null, nameof(HighTerrainPatches.LiftMaxElevation), HookKind.Transpiler))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch the altitude limits, so plants, creatures and locations stay under the game's 1000 m: " },

    // A new player is carried in by the Valkyrie from 500 m and down to 100 m (Valkyrie.cs:15-17, :63-71).
    new("High terrain: the Valkyrie", HighTerrain.Wanted,
      OnGame(typeof(Valkyrie), "Awake", null, nameof(HighTerrainPatches.ValkyrieAwake), HookKind.Prefix))
    { ViaProcessor = true, FailureMessage = "High terrain: could not patch the Valkyrie, so a new player on high ground starts under it: " },
  ];
}

// The patch methods of HighTerrain: prefixes and postfixes for the game's methods, and transpilers that find the game's own
// numbers by their IL and name them (type and member names, not the types: the same code is read in the client's assembly and
// the dedicated server's, which are two assemblies). A transpiler that finds nothing to change says so (the game was updated).
internal static class HighTerrainPatches
{
  // ---- Character.InInterior -------------------------------------------------------------------------------------------

  // The three overloads, by the names of their own parameters (Harmony passes them by name).
  internal static bool InteriorOfPoint(Vector3 position, ref bool __result)
  {
    __result = HighTerrain.Interior(position);
    return false;
  }

  internal static bool InteriorOfTransform(Transform me, ref bool __result)
  {
    __result = HighTerrain.Interior(me);
    return false;
  }

  internal static bool InteriorOfCharacter(Character __instance, ref bool __result)
  {
    __result = HighTerrain.Interior(__instance);
    return false;
  }

  private static readonly MethodInfo ForPoint = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.Interior), [typeof(Vector3)]);
  private static readonly MethodInfo ForTransform = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.Interior), [typeof(Transform)]);
  private static readonly MethodInfo ForCharacter = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.Interior), [typeof(Character)]);

  // Every call of Character.InInterior (any of the three) becomes the same call of HighTerrain.Interior: the same arguments
  // are on the stack and a bool comes back. The instruction itself is changed, so its labels and blocks stay.
  internal static IEnumerable<CodeInstruction> RedirectInterior(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var code = new List<CodeInstruction>(instructions);
    int changed = 0;
    foreach (var instruction in code)
    {
      if (!Calls(instruction, "Character", "InInterior") || instruction.operand is not MethodInfo method)
        continue;
      var parameters = method.GetParameters();
      var to = parameters.Length == 0 ? ForCharacter
        : parameters[0].ParameterType.Name == "Vector3" ? ForPoint
        : parameters[0].ParameterType.Name == "Transform" ? ForTransform : null;
      if (to == null)
        continue;
      instruction.opcode = OpCodes.Call;
      instruction.operand = to;
      changed++;
    }
    return Done(code, changed, original, "a call of Character.InInterior");
  }

  // ---- RandEventSystem: "position.y > 3000f" ---------------------------------------------------------------------------

  private static readonly MethodInfo Interior = ForPoint;

  // `<a Vector3>; ldfld Vector3::y; ldc.r4 3000; cgt` and the same with a branch (`bgt` when the event is not for a player up
  // there, `ble.un` the other way round) become `<the Vector3>; call HighTerrain.Interior` and the branch of the bool: what is
  // on the stack is the point itself (the game's two uses are `ldarg position` and `ldfld PlayerEventData::position`).
  internal static IEnumerable<CodeInstruction> RedirectEventHeight(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var code = new List<CodeInstruction>(instructions);
    int changed = 0;
    for (int i = 1; i + 2 < code.Count; i++)
    {
      if (!LoadsField(code[i], "Vector3", "y") || !IsFloat(code[i + 1], HighTerrain.InteriorHeight) || PushesAddress(code[i - 1]))
        continue;
      var compare = code[i + 2];
      OpCode? branch = null;
      if (compare.opcode == OpCodes.Bgt) branch = OpCodes.Brtrue;
      else if (compare.opcode == OpCodes.Bgt_S) branch = OpCodes.Brtrue_S;
      else if (compare.opcode == OpCodes.Ble || compare.opcode == OpCodes.Ble_Un) branch = OpCodes.Brfalse;
      else if (compare.opcode == OpCodes.Ble_S || compare.opcode == OpCodes.Ble_Un_S) branch = OpCodes.Brfalse_S;
      else if (compare.opcode != OpCodes.Cgt)
        continue;
      code[i].opcode = OpCodes.Call;
      code[i].operand = Interior;
      Remove(code, i + 1);
      if (branch != null)
        code[i + 1].opcode = branch.Value;
      else
        Remove(code, i + 1);
      changed++;
    }
    return Done(code, changed, original, "a test of a point's height against 3000");
  }

  // ---- the ground rays ------------------------------------------------------------------------------------------------

  private static readonly MethodInfo RayStart = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.RayStart));
  private static readonly MethodInfo RayLength = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.RayLength));
  private static readonly MethodInfo RaiseOrigin = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.RaiseOrigin));
  private static readonly MethodInfo BlockerLift = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.BlockerLift));

  // `origin.y = 6000f` (ZoneSystem.GetGroundHeight, both, and Pathfinding.FindGround) and the ray's 10000 m, which is as long again as
  // the start is higher.
  internal static IEnumerable<CodeInstruction> RaiseGroundRay(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var (source, code) = Copy(instructions);
    int starts = 0, lengths = 0;
    for (int i = 0; i + 1 < code.Count; i++)
    {
      if (IsFloat(code[i], 6000f) && StoresField(code[i + 1], "Vector3", "y"))
      {
        code.Insert(i + 1, new CodeInstruction(OpCodes.Call, RayStart));
        starts++;
      }
      else if (IsFloat(code[i], 10000f))
      {
        code.Insert(i + 1, new CodeInstruction(OpCodes.Ldc_R4, 6000f));
        code.Insert(i + 2, new CodeInstruction(OpCodes.Call, RayLength));
        lengths++;
      }
    }
    return starts == 1 && lengths == 1 ? code : NotFound(source, original, "the start (6000 m) and the length (10000 m) of a ground ray");
  }

  // `p + Vector3.up * 5000f` and the ray's 10000 m (ZoneSystem.GetGroundData), `p + Vector3.up * 500f` and 1000 m (the grass's
  // ClutterSystem.GetGroundInfo): the origin is lifted when it is under the top of the world, and the ray made as much longer.
  internal static IEnumerable<CodeInstruction> RaiseGroundDataRay(IEnumerable<CodeInstruction> instructions, MethodBase? original = null) =>
    RaiseUpRay(instructions, original, 5000f, 10000f, "ground data");

  internal static IEnumerable<CodeInstruction> RaiseClutterRay(IEnumerable<CodeInstruction> instructions, MethodBase? original = null) =>
    RaiseUpRay(instructions, original, 500f, 1000f, "grass");

  private static List<CodeInstruction> RaiseUpRay(IEnumerable<CodeInstruction> instructions, MethodBase? original, float up, float length, string what)
  {
    var (source, code) = Copy(instructions);
    int starts = 0, lengths = 0;
    for (int i = 0; i + 2 < code.Count; i++)
    {
      if (IsFloat(code[i], up) && Calls(code[i + 1], "Vector3", "op_Multiply") && Calls(code[i + 2], "Vector3", "op_Addition"))
      {
        code.Insert(i + 3, new CodeInstruction(OpCodes.Call, RaiseOrigin));
        starts++;
      }
      else if (IsFloat(code[i], length))
      {
        code.Insert(i + 1, new CodeInstruction(OpCodes.Ldc_R4, up));
        code.Insert(i + 2, new CodeInstruction(OpCodes.Call, RayLength));
        lengths++;
      }
    }
    return starts == 1 && lengths == 1 ? code : NotFound(source, original, $"the origin ({up:0} m over the point) and the length ({length:0} m) of the {what} ray");
  }

  // `p.y += 2000f` (ZoneSystem.IsBlocked): the 2000 becomes HighTerrain.BlockerLift(p), 2000 m or as much as puts the start 2000 m over the
  // ground below the point. `p` is the argument after `this`.
  internal static IEnumerable<CodeInstruction> RaiseBlockedRay(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var (source, code) = Copy(instructions);
    int changed = 0;
    bool shape = original == null || (!original.IsStatic && original.GetParameters().Length == 1 && original.GetParameters()[0].ParameterType.Name == "Vector3");
    for (int i = 0; shape && i + 1 < code.Count; i++)
    {
      if (!IsFloat(code[i], 2000f) || code[i + 1].opcode != OpCodes.Add)
        continue;
      code[i].opcode = OpCodes.Ldarg_1;
      code[i].operand = null;
      code.Insert(i + 1, new CodeInstruction(OpCodes.Call, BlockerLift));
      changed++;
    }
    return changed == 1 ? code : NotFound(source, original, "the 2000 m a blocker ray starts over its point");
  }

  // ---- the AI's navigation tiles ----------------------------------------------------------------------------------------

  // Pathfinding.GetTilePos(id) is (id.x * size, 2500, id.y * size).
  internal static void TilePosition(ref Vector3 __result) => __result.y = HighTerrain.TileCentre(__result.x, __result.z, __result.y);

  // ---- altitude limits ------------------------------------------------------------------------------------------------

  private static readonly MethodInfo MaxAltitude = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.MaxAltitude));
  private static readonly MethodInfo MaxElevation = AccessTools.Method(typeof(HighTerrain), nameof(HighTerrain.MaxElevation));

  // Every read of a field m_maxAltitude (ZoneVegetation, ZoneLocation and SpawnData each have one) or m_maxAlt (the grass's Clutter),
  // through HighTerrain.
  internal static IEnumerable<CodeInstruction> LiftMaxAltitude(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var code = new List<CodeInstruction>(instructions);
    int changed = 0;
    for (int i = 0; i < code.Count; i++)
    {
      if (!LoadsField(code[i], null, "m_maxAltitude") && !LoadsField(code[i], null, "m_maxAlt"))
        continue;
      code.Insert(i + 1, new CodeInstruction(OpCodes.Call, MaxAltitude));
      changed++;
      i++;
    }
    return Done(code, changed, original, "a read of m_maxAltitude or m_maxAlt");
  }

  // The same for m_maxElevation of RandomSpawn and RandomObject (an int).
  internal static IEnumerable<CodeInstruction> LiftMaxElevation(IEnumerable<CodeInstruction> instructions, MethodBase? original = null)
  {
    var code = new List<CodeInstruction>(instructions);
    int changed = 0;
    for (int i = 0; i < code.Count; i++)
    {
      if (!LoadsField(code[i], null, "m_maxElevation"))
        continue;
      code.Insert(i + 1, new CodeInstruction(OpCodes.Call, MaxElevation));
      changed++;
      i++;
    }
    return Done(code, changed, original, "a read of m_maxElevation");
  }

  // ---- the Valkyrie ---------------------------------------------------------------------------------------------------

  internal static void ValkyrieAwake(Valkyrie __instance)
  {
    var player = Player.m_localPlayer;
    if (!player)
      return;
    float lift = HighTerrain.ValkyrieLift(player.transform.position.y);
    if (lift <= 0f)
      return;
    __instance.m_startAltitude += lift;
    __instance.m_descentAltitude += lift;
  }

  // ---- reading IL by name ---------------------------------------------------------------------------------------------

  private static bool IsFloat(CodeInstruction i, float value) => i.opcode == OpCodes.Ldc_R4 && i.operand is float f && f == value;

  private static bool Calls(CodeInstruction i, string type, string name) =>
    (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase m && m.Name == name && m.DeclaringType?.Name == type;

  private static bool LoadsField(CodeInstruction i, string? type, string name) =>
    i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.Name == name && (type == null || f.DeclaringType?.Name == type);

  private static bool StoresField(CodeInstruction i, string type, string name) =>
    i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f.Name == name && f.DeclaringType?.Name == type;

  private static bool PushesAddress(CodeInstruction i) =>
    i.opcode == OpCodes.Ldflda || i.opcode == OpCodes.Ldarga || i.opcode == OpCodes.Ldarga_S || i.opcode == OpCodes.Ldloca || i.opcode == OpCodes.Ldloca_S
    || i.opcode == OpCodes.Ldsflda || i.opcode == OpCodes.Ldelema;

  // Takes an instruction out; what jumps to it, or starts or ends a block there, goes to the one after it.
  private static void Remove(List<CodeInstruction> code, int index)
  {
    var removed = code[index];
    var next = code[index + 1];
    next.labels.AddRange(removed.labels);
    next.blocks.InsertRange(0, removed.blocks);
    code.RemoveAt(index);
  }

  private static List<CodeInstruction> Done(List<CodeInstruction> code, int changed, MethodBase? method, string what) =>
    changed == 0 ? NotFound(code, method, what) : code;

  // The instructions as they were, and a copy of them (labels and blocks kept) to change: a transpiler that does not find all it
  // expects gives the first back, so that the game's rule is left exactly as it is.
  private static (List<CodeInstruction> source, List<CodeInstruction> copy) Copy(IEnumerable<CodeInstruction> instructions)
  {
    var source = new List<CodeInstruction>(instructions);
    return (source, source.ConvertAll(i => new CodeInstruction(i)));
  }

  private static List<CodeInstruction> NotFound(List<CodeInstruction> unchanged, MethodBase? method, string what)
  {
    BetterContinents.LogWarning($"High terrain: did not find {what} in {method?.DeclaringType?.Name}.{method?.Name} as expected: that game rule is left as it is (was the game updated?)");
    return unchanged;
  }
}

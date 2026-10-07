// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// An inventory of the methods of both game assemblies (the client's and the dedicated server's) that hold the numbers the high-terrain
// patches replace, read with Mono.Cecil: a game update that adds a caller of Character.InInterior, a ray that starts at a height of
// its own or a reader of an altitude limit fails it, and the patches are then to be extended.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using CodeInstruction = HarmonyLib.CodeInstruction;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SysEmit = System.Reflection.Emit;

internal static partial class Program
{
  // A method of the game read with Cecil, as Harmony's CodeInstructions: for the few that .NET cannot load here because they use the
  // engine's AI module (Pathfinding), which the reference folder lacks. A field or method of a type that is loaded is the real
  // member (what the transpilers read: the names of members, and of the types that declare them); any other operand is its text.
  private static List<CodeInstruction> Decode(string dll, string type, string method, int? parameters)
  {
    using var module = ModuleDefinition.ReadModule(dll);
    var definition = module.GetTypes().First(t => t.FullName == type).Methods.Single(m => m.Name == method && (parameters == null || m.Parameters.Count == parameters));
    var map = typeof(SysEmit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (SysEmit.OpCode)f.GetValue(null)).ToDictionary(o => o.Name);
    var il = new System.Reflection.Emit.DynamicMethod("decode", typeof(void), Type.EmptyTypes).GetILGenerator();
    var instructions = definition.Body.Instructions;
    var codes = instructions.Select(i => new CodeInstruction(map[i.OpCode.Name])).ToList();
    var labels = new Dictionary<Instruction, SysEmit.Label>();
    SysEmit.Label LabelOf(Instruction target)
    {
      if (!labels.TryGetValue(target, out var label))
      {
        labels[target] = label = il.DefineLabel();
        codes[instructions.IndexOf(target)].labels.Add(label);
      }
      return label;
    }
    for (int n = 0; n < codes.Count; n++)
      codes[n].operand = instructions[n].Operand switch
      {
        Instruction target => LabelOf(target),
        Instruction[] targets => targets.Select(LabelOf).ToArray(),
        FieldReference f => Member(f.DeclaringType, t => t.GetField(f.Name, Any)) ?? (object)f.ToString(),
        MethodReference m => Member(m.DeclaringType, t => t.GetMethods(Any).FirstOrDefault(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count)) ?? (object)m.ToString(),
        var other => other?.ToString() is { } text && other is not (float or double or int or long or sbyte or byte or short or string) ? text : other,
      };
    return codes;
  }

  private static object Member(TypeReference declaring, Func<Type, MemberInfo> find)
  {
    try
    {
      var name = declaring.FullName.Replace('/', '+');
      var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
      return type == null ? null : find(type);
    }
    catch (Exception)
    {
      return null;
    }
  }

  // ---- the inventory of the game's numbers -----------------------------------------------------------------------------
  private static string Name(TypeDefinition type, MethodDefinition method)
  {
    var iterator = Regex.Match(type.Name, @"^<(\w+)>d__\d+$");
    return iterator.Success ? $"{type.DeclaringType?.Name}::{iterator.Groups[1].Value}(MoveNext)" : $"{type.Name}::{method.Name}";
  }

  private static bool IsFloat(Instruction i, float value) => i.OpCode.Code == Code.Ldc_R4 && i.Operand is float f && f == value;

  // Every method of an assembly (by "Type::Method") for which the matcher says yes at some instruction.
  private static SortedSet<string> Sites(ModuleDefinition module, Func<Instruction[], int, bool> matches)
  {
    var found = new SortedSet<string>();
    foreach (var type in module.GetTypes())
      foreach (var method in type.Methods)
      {
        if (method.Body == null)
          continue;
        var code = method.Body.Instructions.ToArray();
        for (int i = 0; i < code.Length; i++)
          if (matches(code, i))
          {
            found.Add(Name(type, method));
            break;
          }
      }
    return found;
  }

  private static bool CallsOf(Instruction i, string type, string name) =>
    (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) && i.Operand is MethodReference m && m.Name == name && m.DeclaringType.Name == type;

  private static bool ReadsField(Instruction i, string name) => (i.OpCode.Code == Code.Ldfld || i.OpCode.Code == Code.Ldflda) && i.Operand is FieldReference f && f.Name == name;

  private static void InventoryTests()
  {
    foreach (var dll in new[] { "client", "server" })
    {
      Section($"every method of the {dll}'s game assembly that holds a number the patches replace");
      using var module = ModuleDefinition.ReadModule(Path.Combine(Libs, "1.0", dll, "assembly_valheim.dll"));
      void Expect(string what, SortedSet<string> got, params string[] expected) =>
        Check(got.SetEquals(expected), $"{dll}: {what}\n    expected: {string.Join(", ", expected.OrderBy(s => s))}\n    got:      {string.Join(", ", got)}");

      Expect("the methods that call Character.InInterior are the three overloads and the twelve methods (thirteen calls: Teleport.Interact asks twice) that are patched",
        Sites(module, (c, i) => CallsOf(c[i], "Character", "InInterior")),
        ["Character::InInterior", ..InteriorCallers.Select(c => $"{c.type}::{c.method}")]);
      Expect("the 3000 m test of a point's height (ldfld y; ldc.r4 3000; compare) is in InInterior and the two of the random events",
        Sites(module, (c, i) => i + 2 < c.Length && c[i].OpCode.Code == Code.Ldfld && c[i].Operand is FieldReference { Name: "y" } && IsFloat(c[i + 1], 3000f)
          && c[i + 2].OpCode.Code is Code.Cgt or Code.Bgt or Code.Bgt_S or Code.Ble or Code.Ble_S or Code.Ble_Un or Code.Ble_Un_S),
        "Character::InInterior", "RandEventSystem::IsInsideRandomEventArea", "RandEventSystem::GetValidEventPoints");
      Expect("the upper altitude m_maxAltitude is read by vegetation, locations and spawns, the grass's m_maxAlt by the grass (and a bird's, 20 m over the ground, by RandomFlyingBird, which is no limit)",
        Sites(module, (c, i) => ReadsField(c[i], "m_maxAltitude") || ReadsField(c[i], "m_maxAlt")),
        "ZoneSystem::PlaceVegetation", "ZoneSystem::GenerateLocationsTimeSliced(MoveNext)", "SpawnSystem::IsSpawnPointGood", "ClutterSystem::GenerateVegPatch", "RandomFlyingBird::RandomizeWaypoint");
      Expect("the upper elevation m_maxElevation is read by RandomSpawn and RandomObject only",
        Sites(module, (c, i) => ReadsField(c[i], "m_maxElevation")),
        "RandomSpawn::Randomize", "RandomObject::Randomize");
      Expect("an alt biome's upper mean height is read by BiomeSector.CanAddModifier only (Better Continents' own copy of it reads the same fields)",
        Sites(module, (c, i) => ReadsField(c[i], "m_maxAvgHeight")),
        "BiomeSector::CanAddModifier");
      Expect("`origin.y = 6000f` is the start of three ground rays",
        Sites(module, (c, i) => i + 1 < c.Length && IsFloat(c[i], 6000f) && c[i + 1].OpCode.Code == Code.Stfld && c[i + 1].Operand is FieldReference { Name: "y" }),
        "ZoneSystem::GetGroundHeight", "Pathfinding::FindGround");
      Expect("`Vector3.up * 5000f` added to a point is GetGroundData's ray",
        Sites(module, (c, i) => i + 2 < c.Length && IsFloat(c[i], 5000f) && CallsOf(c[i + 1], "Vector3", "op_Multiply") && CallsOf(c[i + 2], "Vector3", "op_Addition")),
        "ZoneSystem::GetGroundData");
      Expect("`Vector3.up * 500f` added to a point is the grass's ray (and the rays of CircleProjector, from its own height)",
        Sites(module, (c, i) => i + 2 < c.Length && IsFloat(c[i], 500f) && CallsOf(c[i + 1], "Vector3", "op_Multiply") && CallsOf(c[i + 2], "Vector3", "op_Addition")),
        "ClutterSystem::GetGroundInfo", "CircleProjector::Update");
      Expect("`p.y += 2000f` is IsBlocked's ray",
        Sites(module, (c, i) => i + 1 < c.Length && IsFloat(c[i], 2000f) && c[i + 1].OpCode.Code == Code.Add && i > 0 && c[i - 1].OpCode.Code == Code.Ldind_R4),
        "ZoneSystem::IsBlocked");
      Expect("the AI's tile is placed at 2500 m by GetTilePos only",
        Sites(module, (c, i) => i > 0 && i + 7 < c.Length && IsFloat(c[i], 2500f) && c[i + 7].OpCode.Code == Code.Newobj && c[i + 7].Operand is MethodReference { DeclaringType.Name: "Vector3" }
          && c[i - 1].OpCode.Code == Code.Mul),
        "Pathfinding::GetTilePos");
      Expect("a Location puts its dungeon 5000 m up in Awake only (one add of 5000 to a position's y)",
        Sites(module, (c, i) => i + 1 < c.Length && IsFloat(c[i], 5000f) && c[i + 1].OpCode.Code == Code.Add && i > 0 && c[i - 1].OpCode.Code == Code.Ldfld && c[i - 1].Operand is FieldReference { Name: "y" }),
        "Location::Awake");
    }
  }
}

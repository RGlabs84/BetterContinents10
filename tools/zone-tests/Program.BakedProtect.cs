// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BakedProtect on the game's own classes: which ZDOs are protected, the four fields a protected piece's components keep (and no
// others: never Smelter.m_requiresRoof, which stays the compiler's), and the three patches, applied to the game's methods and run
// as their prefixes run.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterContinents;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
  private static readonly int ProtectHash = "bc_protect".GetStableHashCode();

  private static ZDO Protected(ZDO zdo, int value = 1)
  {
    ZDOExtraData.Set(zdo.m_uid, ProtectHash, value);
    return zdo;
  }

  private static void BakedProtectTests()
  {
    Section("baked pieces: protection");
    ZDOExtraData.Reset();
    var layerPresent = BakedProtect.LayerPresent;
    try
    {
      BakedProtect.LayerPresent = () => true;
      C(BakedProtect.IsProtected(Protected(NewZdo("forge", 1))), "a ZDO with bc_protect is protected");
      C(!BakedProtect.IsProtected(NewZdo("forge", 2)), "one without it is not");
      C(!BakedProtect.IsProtected(Protected(NewZdo("forge", 3), 0)), "bc_protect false is not");
      C(!BakedProtect.IsProtected((ZDO)null), "no ZDO is not");
      C(!BakedProtect.IsProtected((ZNetView)null), "no ZNetView is not");
      var view = Make<ZNetView>();
      SetField(view, "m_zdo", Protected(NewZdo("door", 4)));
      C(BakedProtect.IsProtected(view), "a piece is protected through its ZNetView's ZDO");
      SetField(view, "m_zdo", null);
      C(!BakedProtect.IsProtected(view), "one whose ZNetView has no ZDO yet is not");
      // The key as the game's own setters write it: a bool is stored as an int, and read back as one.
      var set = NewZdo("door", 5);
      ZDOExtraData.Set(set.m_uid, "bc_protect".GetStableHashCode(), 1);
      C(set.GetBool("bc_protect") && BakedProtect.IsProtected(set), "the key is a bool as VALtimaOnline reads its own (GetInt != 0)");

      // A machine with no layer reads nothing: a layer that was dropped leaves its pieces' keys, which then do nothing.
      BakedProtect.LayerPresent = () => false;
      C(!BakedProtect.IsProtected(Protected(NewZdo("forge", 6))), "with no layer on this machine, protection is off");
      BakedProtect.LayerPresent = () => true;

      BakedFieldsTests();
      BakedPatchTests();
    }
    finally
    {
      BakedProtect.LayerPresent = layerPresent;
    }
  }

  // The four fields, and no field of any other. (WearNTear and Piece cannot be made here, their static constructors call into Unity: the
  // IL is read instead.)
  private static void BakedFieldsTests()
  {
    // What the code can write: every store in BakedProtect and its patches is one of the four, and nothing mentions Smelter.m_requiresRoof
    // (the spinning wheel's no-roof rule is VALtimaOnline's, keyed on its tags: build spec 0.1, item 2).
    var allowed = new HashSet<FieldInfo>
    {
      typeof(WearNTear).GetField(nameof(WearNTear.m_noRoofWear))!,
      typeof(WearNTear).GetField(nameof(WearNTear.m_noSupportWear))!,
      typeof(WearNTear).GetField(nameof(WearNTear.m_burnable))!,
      typeof(Piece).GetField(nameof(Piece.m_canBeRemoved))!,
    };
    var smelter = typeof(Smelter).GetField(nameof(Smelter.m_requiresRoof))!;
    var stored = new HashSet<FieldInfo>();
    bool touchesSmelter = false, alwaysFalse = true;
    int methods = 0;
    foreach (var type in new[] { typeof(BakedProtect) }.Concat(typeof(BakedProtect).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
      foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
      {
        if (method.GetMethodBody() == null)
          continue;
        methods++;
        var code = PatchProcessor.GetOriginalInstructions(method);
        for (int i = 0; i < code.Count; i++)
        {
          if (code[i].operand is not FieldInfo field)
            continue;
          if (field == smelter)
            touchesSmelter = true;
          if (code[i].opcode == System.Reflection.Emit.OpCodes.Stfld || code[i].opcode == System.Reflection.Emit.OpCodes.Stsfld)
          {
            stored.Add(field);
            // What goes in is false (ldc.i4.0), for each of the four.
            if (allowed.Contains(field) && (i == 0 || code[i - 1].opcode != System.Reflection.Emit.OpCodes.Ldc_I4_0))
              alwaysFalse = false;
          }
        }
      }
    C(methods >= 8, $"(the scan looked at BakedProtect's {methods} methods)");
    C(stored.SetEquals(allowed), $"BakedProtect stores to exactly those four fields: {string.Join(", ", stored.Select(f => f.DeclaringType!.Name + "." + f.Name))}");
    C(alwaysFalse, "and each is set to false");
    C(!touchesSmelter, "and never reads or writes Smelter.m_requiresRoof");
  }

  // The three patches target the game's own methods, and their parameters bind to them (Harmony would refuse them otherwise at start:
  // the game's methods cannot be patched in this process, since they call into Unity), and their prefixes answer as the build wants.
  private static void BakedPatchTests()
  {
    var targets = new Dictionary<Type, (string Type, string Method)>
    {
      [typeof(BakedProtect.AwakePatch)] = ("WearNTear", "Awake"),
      [typeof(BakedProtect.DamagePatch)] = ("WearNTear", "ApplyDamage"),
      [typeof(BakedProtect.RemovePatch)] = ("Piece", "CanBeRemoved"),
    };
    foreach (var (patch, (typeName, methodName)) in targets)
    {
      var attribute = patch.GetCustomAttributes<HarmonyPatch>().Single();
      var original = AccessTools.Method(attribute.info.declaringType, attribute.info.methodName);
      C(original != null && attribute.info.declaringType!.Name == typeName && attribute.info.methodName == methodName,
        $"{patch.Name} is on {typeName}.{methodName}, which the game has");
      var fix = patch.GetMethod(methodName == "Awake" ? "Postfix" : "Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
      var originalParameters = original!.GetParameters().ToDictionary(p => p.Name!);
      bool binds = fix.GetParameters().All(p =>
        p.Name == "__instance" ? p.ParameterType == original.DeclaringType
        : p.Name == "__result" ? p.ParameterType.GetElementType() == ((MethodInfo)original).ReturnType
        : originalParameters.TryGetValue(p.Name!, out var o) && o.ParameterType == p.ParameterType);
      C(binds, $"{patch.Name}'s parameters ({string.Join(", ", fix.GetParameters().Select(p => p.Name))}) all bind to {typeName}.{methodName}");
    }

    // The prefixes' decision (they pass their piece's ZNetView).
    ZNetView ViewOf(ZDO zdo)
    {
      var view = Make<ZNetView>();
      SetField(view, "m_zdo", zdo);
      return view;
    }
    (bool Runs, bool Result) Ask(ZNetView view)
    {
      bool result = true;
      bool runs = BakedProtect.Deny(view, ref result);
      return (runs, result);
    }
    var guarded = Ask(ViewOf(Protected(NewZdo("wood_wall", 20))));
    C(!guarded.Runs && !guarded.Result, "a protected piece: the game's own method (ApplyDamage, CanBeRemoved) is skipped, and the answer is no");
    var plain = Ask(ViewOf(NewZdo("wood_wall", 21)));
    C(plain.Runs && plain.Result, "any other piece: the game's own method runs, and the answer is left as it was");
    var none = Ask(null!);
    C(none.Runs && none.Result, "a piece with no ZNetView is the game's own business");
    var later = NewZdo("piece_chest", 22);
    var laterView = ViewOf(later);
    C(Ask(laterView).Runs, "a piece that is not protected yet runs as it does");
    ZDOExtraData.Set(later.m_uid, ProtectHash, 1);
    C(!Ask(laterView).Runs, "and the call after the key is written is denied: a piece protected while players have it loaded is protected at once");
    var off = BakedProtect.LayerPresent;
    BakedProtect.LayerPresent = () => false;
    C(Ask(laterView).Runs, "with no layer on the machine, nothing is denied");
    BakedProtect.LayerPresent = off;
  }
}

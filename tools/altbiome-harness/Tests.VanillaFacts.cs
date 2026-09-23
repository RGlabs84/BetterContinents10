// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HarmonyLib;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

internal static partial class Tests
{
  // The calls a method makes, read straight from an assembly file's IL (no loading, no JIT): "Type::Method" per call.
  internal static List<string> CallsIn(string assemblyPath, string typeName, string methodName)
  {
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    string Name(EntityHandle handle)
    {
      switch (handle.Kind)
      {
        case HandleKind.MethodDefinition:
          var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
          return md.GetString(md.GetTypeDefinition(def.GetDeclaringType()).Name) + "::" + md.GetString(def.Name);
        case HandleKind.MemberReference:
          var mr = md.GetMemberReference((MemberReferenceHandle)handle);
          var parent = mr.Parent.Kind switch
          {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent).Name),
            _ => "?",
          };
          return parent + "::" + md.GetString(mr.Name);
        default:
          return handle.Kind.ToString();
      }
    }
    foreach (var th in md.TypeDefinitions)
    {
      var type = md.GetTypeDefinition(th);
      if (md.GetString(type.Name) != typeName)
        continue;
      foreach (var mh in type.GetMethods())
      {
        var method = md.GetMethodDefinition(mh);
        if (md.GetString(method.Name) != methodName)
          continue;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        var calls = new List<string>();
        while (il.RemainingBytes > 0)
        {
          int op = il.ReadByte();
          if (op == 0xFE)
            op = 0x100 | il.ReadByte();
          switch (op)
          {
            case 0x28: // call
            case 0x6F: // callvirt
            case 0x73: // newobj
              calls.Add(Name(MetadataTokens.EntityHandle(il.ReadInt32())));
              break;
            default:
              SkipOperand(ref il, op);
              break;
          }
        }
        return calls;
      }
    }
    return [];
  }

  // Operand sizes of the ECMA-335 opcodes (enough to walk any method body).
  private static void SkipOperand(ref BlobReader il, int op)
  {
    switch (op)
    {
      case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: // ldarg.s ldarga.s starg.s ldloc.s ldloca.s stloc.s
      case 0x1F: // ldc.i4.s
      case >= 0x2B and <= 0x37: // short branches
      case 0xDE: // leave.s
        il.ReadByte();
        break;
      case 0x20: case 0x22: // ldc.i4, ldc.r4
      case >= 0x38 and <= 0x44: // long branches
      case 0xDD: // leave
      case 0x27: case 0x29: // jmp, calli
      case 0x70: case 0x71: case 0x72: case 0x74: case 0x75: case 0x79: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: case 0x80:
      case 0x81: case 0x8C: case 0x8D: case 0x8F: case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0:
      case 0x106: case 0x107: case 0x115: case 0x11C: case 0x116: // ldftn ldvirtftn initobj sizeof constrained.
        il.ReadInt32();
        break;
      case 0x119: // no.
        il.ReadByte();
        break;
      case 0x21: case 0x23: // ldc.i8, ldc.r8
        il.ReadInt64();
        break;
      case 0x45: // switch
        int n = il.ReadInt32();
        for (int i = 0; i < n; i++)
          il.ReadInt32();
        break;
      case 0x109: case 0x10A: case 0x10B: case 0x10C: case 0x10D: case 0x10E: // ldarg ldarga starg ldloc ldloca stloc (FE-prefixed, int16)
        il.ReadInt16();
        break;
      case 0x112: // unaligned.
        il.ReadByte();
        break;
    }
  }

  // What the installed 1.0.15 builds actually do at world load, read from their IL (the per-type decompile in
  // libs-Tools/Decompiled_1.0.15 shows an older body that read and wrote the cache).
  private static void VanillaFacts()
  {
    var libs = Path.Combine(RepoRoot, "..", "libs-Tools", "1.0");
    foreach (var (side, dll) in new[] { ("client", Path.Combine(libs, "client", "assembly_valheim.dll")), ("server", Path.Combine(libs, "server", "assembly_valheim.dll")) })
    {
      if (!File.Exists(dll))
      {
        Note($"skipped {side}: {dll} missing");
        continue;
      }
      var calls = CallsIn(dll, "AltBiomeWorldData", "VerifyBiomeData");
      Note($"1.0.15 {side} AltBiomeWorldData.VerifyBiomeData calls: {string.Join(", ", calls)}");
      Check(calls.SequenceEqual(new[] { "AltBiomeWorldData::RemoveCache", "AltBiomeWorldData::GenerateBiomePoints", "AltBiomeWorldData::GenerateSectors" }),
        $"1.0.15 {side}: VerifyBiomeData deletes the biome cache and regenerates the grid on every load (never TryLoadCache or SaveCache)");
    }
    var loaded = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.VerifyBiomeData)));
    Check(loaded.Any(i => i.Calls(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.RemoveCache), [typeof(string)])))
          && !loaded.Any(i => i.Calls(AccessTools.Method(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.TryLoadCache)))),
      "the assembly this harness runs against agrees (RemoveCache, no TryLoadCache)");
  }
}

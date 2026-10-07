// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// WorldSectors' transpilers on the game's IL, the client's and the dedicated server's (a different build: libs-Tools/1.0/server),
// and what each hook binds to in both.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
  // A transpiler run on copies of the instructions (with their labels and blocks, which Clone drops).
  private static List<CodeInstruction> Run(Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transpiler, List<CodeInstruction> instructions) =>
    transpiler(instructions.Select(i => new CodeInstruction(i)).ToList()).ToList();

  // Every int constant the transpiler changed, as (before, after) in order; null when it changed anything else.
  private static List<(int Before, int After)> IntChanges(List<CodeInstruction> before, List<CodeInstruction> after)
  {
    if (before.Count != after.Count)
      return null;
    var changes = new List<(int, int)>();
    for (int i = 0; i < before.Count; i++)
    {
      if (before[i].opcode == after[i].opcode && Equals(before[i].operand, after[i].operand) && before[i].labels.SequenceEqual(after[i].labels))
        continue;
      if (WorldSectors.IntLoad(before[i]) is not { } was || WorldSectors.IntLoad(after[i]) is not { } now || !before[i].labels.SequenceEqual(after[i].labels)
          || !before[i].blocks.SequenceEqual(after[i].blocks))
        return null;
      changes.Add((was, now));
    }
    return changes;
  }

  private static MethodBase Method(Assembly asm, string type, string name) =>
    asm.GetType(type)!.GetMethods(Any | BindingFlags.DeclaredOnly).Single(m => m.Name == name);

  private static string Text(MethodBase method) => string.Join("\n", PatchProcessor.GetOriginalInstructions(method).Select(i => i.ToString()));

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void IlTests()
  {
    Section("the transpilers on the game's IL: the client's and the dedicated server's");
    var cases = new (string Name, string Type, Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> Transpiler, int[] Before, int[] After)[]
    {
      // The four arrays of numbers, the two loops over 512 sectors, then the sizes of the arrays the seven calls give.
      ("GetSaveClonePerChunk", "ZDOMan", WorldSectors.GetSaveClonePerChunkTranspiler,
        [4096, 1024, 256, 64, 512, 512, 64, 32, 16, 64, 32, 16, 8], [65536, 16384, 4096, 1024, 2048, 2048, 256, 128, 64, 256, 128, 64, 32]),
      ("AddObjectsPerChunk", "ZDOMan", WorldSectors.AddObjectsPerChunkTranspiler, [64], [256]),
      ("DecideChunkSize", "ZDOMan", WorldSectors.DecideChunkSizeTranspiler, [64], [256]),
    };
    var client = typeof(ZDOMan).Assembly;
    foreach (var (label, asm) in new[] { ("client", client), ("server", serverGame) })
      foreach (var c in cases)
      {
        var original = PatchProcessor.GetOriginalInstructions(Method(asm, c.Type, c.Name));
        var patched = Run(c.Transpiler, original);
        var changes = IntChanges(original, patched);
        Check(changes != null && changes.Select(x => x.Before).SequenceEqual(c.Before) && changes.Select(x => x.After).SequenceEqual(c.After),
          $"{label} {c.Type}.{c.Name}: the transpiler changes the constants {string.Join(", ", c.Before)} to {string.Join(", ", c.After)} and nothing else"
          + (changes == null ? " (it changed something else)" : changes.Select(x => x.Before).SequenceEqual(c.Before) ? "" : $" (it changed {string.Join(", ", changes.Select(x => $"{x.Before}->{x.After}"))})"));
        // A method that is not what it expected is refused, not half patched.
        bool refused = Throws(() => Run(c.Transpiler, patched));
        Check(refused, $"{label} {c.Type}.{c.Name}: the transpiled IL is refused when run again (it has no 64 or 512 left to widen)");
        Check(Throws(() => Run(c.Transpiler, [])) && Throws(() => Run(c.Transpiler, original.Take(2).ToList())),
          $"{label} {c.Type}.{c.Name}: an empty method, or the first two instructions of it, is refused");
      }

    // A game whose constants moved: GetSaveClonePerChunk with each constant the transpiler widens changed by one in turn is refused
    // every time.
    var gs = PatchProcessor.GetOriginalInstructions(Method(client, "ZDOMan", "GetSaveClonePerChunk"));
    var widened = Run(WorldSectors.GetSaveClonePerChunkTranspiler, gs);
    int refusedBumps = 0, bumps = 0;
    for (int i = 0; i < gs.Count; i++)
    {
      if (gs[i].ToString() == widened[i].ToString() || WorldSectors.IntLoad(gs[i]) is not { } v)
        continue;
      var copy = gs.Select(x => new CodeInstruction(x)).ToList();
      copy[i].opcode = OpCodes.Ldc_I4;
      copy[i].operand = v + 1;
      bumps++;
      if (Throws(() => WorldSectors.GetSaveClonePerChunkTranspiler(copy).ToList()))
        refusedBumps++;
    }
    Check(bumps == 13 && refusedBumps == 13, $"GetSaveClonePerChunk: each of its 13 constants changed by one is refused ({refusedBumps} of {bumps})");
    // The chunk size each of the 7 calls gives (1, 2, 3 to DecideChunkSize; 0, 1, 2, 3 to AddObjectsPerChunk) is not widened, and is checked too.
    int callBumps = 0, callRefused = 0;
    for (int i = 4; i < gs.Count; i++)
    {
      if (gs[i].opcode != OpCodes.Call && gs[i].opcode != OpCodes.Callvirt || gs[i].operand is not MethodBase { Name: "DecideChunkSize" or "AddObjectsPerChunk" })
        continue;
      var copy = gs.Select(x => new CodeInstruction(x)).ToList();
      copy[i - 3].opcode = OpCodes.Ldc_I4;
      copy[i - 3].operand = (WorldSectors.IntLoad(gs[i - 3]) ?? 0) + 1;
      callBumps++;
      if (Throws(() => WorldSectors.GetSaveClonePerChunkTranspiler(copy).ToList()))
        callRefused++;
    }
    Check(callBumps == 7 && callRefused == 7, $"GetSaveClonePerChunk: the chunk size of each of its 7 calls changed by one is refused ({callRefused} of {callBumps})");

    // The client's and the server's IL are the same for every method the hooks rewrite or replace.
    var names = new[] { ("ZDOMan", "GetSaveClonePerChunk"), ("ZDOMan", "AddObjectsPerChunk"), ("ZDOMan", "DecideChunkSize"), ("ZDOMan", "ResetSectorArray"),
      ("ZoneSystem", "IndicesToIndex"), ("ZoneSystem", "IndexToSector"), ("ZoneSystem", "GetZonesChunk"), ("ZoneSystem", "GetZoneFromChunk"), ("ZoneSystem", "ChunkIndexFromXY") };
    Check(names.All(n => Text(Method(client, n.Item1, n.Item2)) == Text(Method(serverGame, n.Item1, n.Item2))),
      "the client's and the dedicated server's IL is the same for each method that is patched");
    var sectorToIndex = (Assembly a) => a.GetType("ZoneSystem")!.GetMethods(Any | BindingFlags.DeclaredOnly).Single(m => m.Name == "SectorToIndex" && m.GetParameters().Length == 2);
    Check(Text(sectorToIndex(client)) == Text(sectorToIndex(serverGame)), "and for ZoneSystem.SectorToIndex(int, int)");
    // Utils.SmallPosition is in assembly_utils.
    var small = (Assembly a) => a.GetType("Utils")!.GetMethod("SmallPosition", Any)!;
    Check(Text(small(typeof(Utils).Assembly)) == Text(small(serverUtils)), "and for Utils.SmallPosition");

    // The methods the hooks replace are too big for the JIT to fold into their callers (Mono inlines up to 20 bytes of IL),
    // which would bypass a Harmony patch.
    var replaced = new[] { ("ZoneSystem", "IndicesToIndex"), ("ZoneSystem", "IndexToSector"), ("ZoneSystem", "GetZonesChunk"), ("ZoneSystem", "GetZoneFromChunk"), ("ZDOMan", "ResetSectorArray") };
    Check(replaced.All(n => Method(client, n.Item1, n.Item2).GetMethodBody()!.GetILAsByteArray().Length > 20) && sectorToIndex(client).GetMethodBody()!.GetILAsByteArray().Length > 20
          && small(typeof(Utils).Assembly).GetMethodBody()!.GetILAsByteArray().Length > 20,
      "every method a hook replaces has more than 20 bytes of IL (SectorToIndex 60, IndicesToIndex 44, GetZonesChunk 43, GetZoneFromChunk 44, IndexToSector 34, ResetSectorArray 25)");
    // ChunkIndexFromXY (13 bytes) is the one that would be inlined: no hook patches it.
    Check(Method(client, "ZoneSystem", "ChunkIndexFromXY").GetMethodBody()!.GetILAsByteArray().Length <= 20
          && WorldSectors.HookList().All(h => h.Target?.Name != "ChunkIndexFromXY"), "ChunkIndexFromXY (13 bytes of IL, inlined by the JIT) is not patched: nothing depends on it");
  }

  private static bool Throws(Action action)
  {
    try
    {
      action();
      return false;
    }
    catch (InvalidOperationException)
    {
      return true;
    }
  }

  // ------------------------------------------------------------------------------------------------ what each hook binds to

  private static string Signature(MethodBase m) => $"{m.DeclaringType!.FullName}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})";

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void BindingTests()
  {
    Section("the hooks bind to the client's and the dedicated server's assemblies");
    var hooks = WorldSectors.HookList().ToList();
    Check(hooks.Count == 10 && hooks.All(h => h.Target != null && (h.Prefix != null) != (h.Transpiler != null)), $"ten hooks, each with a target in the client's assembly and one patch ({hooks.Count})");
    foreach (var (name, target, prefix, transpiler) in hooks)
    {
      if (target == null) continue;
      // The same method in the server's assembly: the same type, name and parameter types.
      var assembly = target.DeclaringType!.Assembly == typeof(Utils).Assembly ? serverUtils : serverGame;
      var found = assembly.GetType(target.DeclaringType.FullName)?.GetMethods(Any | BindingFlags.DeclaredOnly).Where(m => m.Name == target.Name && Signature(m) == Signature(target)).ToList();
      Check(found is { Count: 1 }, $"{name}: one method of that signature in the dedicated server's assembly");
      var gameMethods = found is { Count: 1 } ? new[] { target, found[0] } : new[] { target };
      var patch = prefix ?? transpiler;
      foreach (var game in gameMethods)
        foreach (var p in patch.GetParameters())
        {
          bool ok = p.Name switch
          {
            "__result" => p.ParameterType.IsByRef && p.ParameterType.GetElementType()!.FullName == ((MethodInfo)game).ReturnType.FullName,
            "__instance" => p.ParameterType.FullName == game.DeclaringType!.FullName,
            "instructions" => true,
            var n when n.StartsWith("___") => game.DeclaringType!.GetField(n.Substring(3), Any) is { } f && p.ParameterType.GetElementType()?.FullName == f.FieldType.FullName,
            var n => game.GetParameters().Any(g => g.Name == n && g.ParameterType.FullName == p.ParameterType.FullName),
          };
          Check(ok, $"{name}: the patch's parameter {p.Name} is in {game.DeclaringType!.Assembly.GetName().Name}'s {game.Name}");
        }
    }
    // The game's own names for what the prefixes read.
    Check(typeof(ZDOMan).GetField("m_objectsBySector", Any)?.FieldType == typeof(List<ZDO>[]), "ZDOMan.m_objectsBySector is a List<ZDO>[]");
    Check(typeof(ZDOMan).GetMethod("LoadChunks") != null && typeof(ChunkSaveMapping).GetMethod("Load") != null && typeof(ChunkSaveMapping).GetProperty("Chunks") != null,
      "ZDOMan.LoadChunks and ChunkSaveMapping.Load, which the migration waits on, and its Chunks");
    Check(new[] { serverGame }.All(a => a.GetType("ZDOMan")!.GetMethod("LoadChunks") != null && a.GetType("ChunkSaveMapping")!.GetMethod("Load") != null), "and in the dedicated server's assembly");
  }
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Better Continents' DLL is built against BepInEx's HarmonyX, whose Harmony.Patch has a sixth parameter (an IL manipulator) that the Lib.Harmony this suite
// runs on lacks: a method holding such a call cannot even be compiled here (DynamicPatch's PatchGetBaseHeight and Toggle's PatchDirectly). So the DLL is read
// into memory first with those calls pointed at the five-parameter Patch (the sixth argument is always the default null), written to a file of its own and loaded
// before anything else uses it, which DynamicPatch needs to run for real (Session.cs). Nothing else of it changes: the code that runs is the real one.
// tools/ewd-tests does the same. This is the first code that runs, and touches nothing of Better Continents: Program's statics refer to its types, and would
// load the original DLL before this one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Entry
{
  private static int Main(string[] args)
  {
    LibHarmonyRewrite.Load();
    return Program.Start(args);
  }
}

internal static class LibHarmonyRewrite
{
  // The calls of Harmony.Patch(original, prefix, postfix, transpiler, finalizer, ilmanipulator) that were changed.
  public static int Calls;

  // Cecil resolves the types of constants when it writes: the game's from the folders the suite loads them from, the framework's from this runtime's core library.
  private sealed class Resolver(string[] dirs) : IAssemblyResolver
  {
    private readonly Dictionary<string, AssemblyDefinition> cache = [];

    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
      if (cache.TryGetValue(name.Name, out var found))
        return found;
      var file = dirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists) ?? typeof(object).Assembly.Location;
      return cache[name.Name] = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = this });
    }

    public void Dispose() { }
  }

  public static void Load()
  {
    var libs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../libs-Tools"));
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../obj/Release/net4.8/BetterContinents.dll"));
    var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = new Resolver([AppContext.BaseDirectory, Path.Combine(libs, "1.0", "client"), libs]) });
    foreach (var type in module.GetTypes())
      foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var il in method.Body.Instructions.ToList())
        {
          if (il.Operand is not MethodReference call || call.Name != "Patch" || call.DeclaringType.FullName != "HarmonyLib.Harmony" || call.Parameters.Count != 6)
            continue;
          if (il.Previous.OpCode != OpCodes.Ldnull)
            throw new InvalidOperationException($"{method.FullName} passes a sixth argument to Harmony.Patch");
          il.Previous.OpCode = OpCodes.Nop;
          il.Previous.Operand = null;
          var five = new MethodReference("Patch", call.ReturnType, call.DeclaringType) { HasThis = true };
          foreach (var parameter in call.Parameters.Take(5))
            five.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
          il.Operand = five;
          Calls++;
        }
    // From a file: an assembly loaded from a stream is not the one the runtime finds when code asks for it by name.
    var folder = Directory.CreateTempSubdirectory("bc-high-rewritten-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { folder.Delete(true); } catch { } };
    var file = Path.Combine(folder.FullName, "BetterContinents.dll");
    module.Write(file);
    AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
  }
}

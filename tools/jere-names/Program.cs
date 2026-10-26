// Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).
//
// The names of Jere Kuusela's mods that Better Continents looks up by string, resolved against the DLLs it meets, by reading their
// metadata with Mono.Cecil (nothing of them runs). One line per name and DLL: OK with the full name that matched (a name can have
// moved between versions: Expand World Data 1.79 moved WorldInfo and BiomeManager into sub-namespaces), or MISSING. Exit code 1 when
// any is missing, and when Better Continents' source holds a name of these mods that this list does not know (so the list cannot
// fall behind the code).
//
// To add a lookup to Better Continents: add it here too, with the source line it is at.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

internal static class Program
{
  const string Thirdparty = "/home/rohan/WubarrkCODING/libs-Tools/1.0/thirdparty/";

  sealed record Dll(string Mod, string Version, string Path, string? Md5);

  static readonly Dll[] Dlls =
  [
    new("EWD", "1.73.0", Thirdparty + "expand-world-data-2026-09-29/ExpandWorldData-1.73.0/ExpandWorldData.dll", null),
    new("EWD", "1.79.0", Thirdparty + "expand-world-data-2026-10-26/ExpandWorldData-1.79.0/ExpandWorldData.dll", "5595d14a"),
    new("EWS", "1.45.0", Thirdparty + "expand-world-size-2026-10-26/ExpandWorldSize-1.45.0/ExpandWorldSize.dll", "d663c41a"),
    new("UW", "1.87.0", Thirdparty + "upgrade-world-2026-10-26/UpgradeWorld-1.87.0/UpgradeWorld.dll", "2910b235"),
  ];

  enum Kind { Plugin, Method, Field, Constructor, Strings }

  // Types: the full names the member may be under (the first that exists in the DLL is used). Params and Returns are full names as
  // Cecil writes them; null: not checked.
  sealed record Item(string Mod, string What, string BcSource, Kind Kind, string[] Types, string Name = "", string[]? Params = null, string? Returns = null, bool? Static = null, string[]? Literals = null, bool Lookup = true);

  const string Biome = "Heightmap/Biome";

  static readonly Item[] Items =
  [
    // ---- Expand World Data (compatiblity/EWD.cs) ----
    new("EWD", "plugin id", "compatiblity/EWD.cs:13 (EWD.GUID)", Kind.Plugin, [], "expand_world_data"),
    new("EWD", "WorldInfo.Set(radius, total, stretch, biomeStretch)", "compatiblity/EWD.cs:38,48,222", Kind.Method,
      ["ExpandWorldData.WorldInfo", "ExpandWorldData.Compatibility.WorldInfo"], "Set", ["System.Single", "System.Single", "System.Single", "System.Single"], null, true),
    new("EWD", "BiomeManager.TryGetBiome(string, out Biome)", "compatiblity/EWD.cs:98", Kind.Method,
      ["ExpandWorldData.BiomeManager", "ExpandWorldData.Biome.BiomeManager"], "TryGetBiome", ["System.String", Biome + "&"], "System.Boolean", true),
    new("EWD", "BiomeManager.TryGetDisplayName(Biome, out string)", "compatiblity/EWD.cs:99", Kind.Method,
      ["ExpandWorldData.BiomeManager", "ExpandWorldData.Biome.BiomeManager"], "TryGetDisplayName", [Biome, "System.String&"], "System.Boolean", true),
    new("EWD", "BiomeManager.LavaBiomes (static Biome field)", "compatiblity/EWD.cs:132", Kind.Field,
      ["ExpandWorldData.BiomeManager", "ExpandWorldData.Biome.BiomeManager"], "LavaBiomes", null, Biome, true),
    new("EWD", "BiomeManager.GetTerrain(Biome)", "compatiblity/EWD.cs:135", Kind.Method,
      ["ExpandWorldData.BiomeManager", "ExpandWorldData.Biome.BiomeManager"], "GetTerrain", [Biome], Biome, true),
    new("EWD", "Api.GetMinimapHeight(float, Biome)", "compatiblity/EWD.cs:82", Kind.Method,
      ["ExpandWorldData.Api"], "GetMinimapHeight", ["System.Single", Biome], "System.Single", true),

    // ---- Expand World Size (compatiblity/EWS.cs) ----
    new("EWS", "plugin id", "compatiblity/EWS.cs:11 (EWS.GUID)", Kind.Plugin, [], "expand_world_size"),
    new("EWS", "ZoneGrid.Width (static int field: the save grid's width)", "compatiblity/EWS.cs (spec P0-2)", Kind.Field,
      ["ExpandWorldSize.ZoneGrid"], "Width", null, "System.Int32", true, null, false),

    // ---- Upgrade World (compatiblity/UW.cs) ----
    new("UW", "plugin id", "compatiblity/UW.cs (UW.GUID)", Kind.Plugin, [], "upgrade_world"),
    new("UW", "Settings.Init(ConfigFile) binds \"1. General\" / \"World radius\"", "compatiblity/UW.cs (ConfigSection, RadiusKey)", Kind.Strings,
      ["UpgradeWorld.Settings"], "Init", null, null, true, ["1. General", "World radius"], false),
    new("UW", "Settings.configWorldRadius (static ConfigEntry<int>)", "compatiblity/UW.cs (FindRadiusEntry reads the entry by section and key)", Kind.Field,
      ["UpgradeWorld.Settings"], "configWorldRadius", null, "BepInEx.Configuration.ConfigEntry`1<System.Int32>", true, null, false),
    new("UW", "RestoreLocations.Rebuild(Dictionary<Vector2s, LocationInstance>)", "compatiblity/UW.cs (RestoreType, RestoreRebuild)", Kind.Method,
      ["UpgradeWorld.RestoreLocations"], "Rebuild", ["System.Collections.Generic.Dictionary`2<Vector2s,ZoneSystem/LocationInstance>"], "System.Void", true),
    new("UW", "Upgrade(Terminal, string, List<string>, FiltererParameters)", "compatiblity/UW.cs (UpgradeType)", Kind.Constructor,
      ["UpgradeWorld.Upgrade"], ".ctor", ["Terminal", "System.String", "System.Collections.Generic.List`1<System.String>", "UpgradeWorld.FiltererParameters"]),
  ];

  // Every string literal in Better Continents' source (outside tools/), read once.
  static readonly HashSet<string> Literals = ReadLiterals();

  static string SourceRoot()
  {
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    return File.Exists(Path.Combine(root, "BetterContinents.csproj")) ? root : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../.."));
  }

  static IEnumerable<string> SourceFiles(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f =>
    {
      // Better Continents' own source: not the tests, the build folders, the hidden work folders (.work holds whole copies of libs-Tools,
      // with the decompiled mods), the packages or the docs.
      var first = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)[0];
      return !first.StartsWith('.') && first is not ("tools" or "obj" or "bin" or "dist" or "libs-Tools" or "hexdist" or "HexiumDist" or "docs");
    });

  static HashSet<string> ReadLiterals()
  {
    var set = new HashSet<string>();
    var quoted = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
    foreach (var file in SourceFiles(SourceRoot()))
      foreach (var line in File.ReadLines(file))
        foreach (Match m in quoted.Matches(StripComment(line)))
          set.Add(m.Groups[1].Value);
    return set;
  }

  static int Main()
  {
    int missing = 0, ok = 0;
    foreach (var dll in Dlls)
    {
      if (!File.Exists(dll.Path))
      {
        Console.WriteLine($"MISSING FILE {dll.Mod} {dll.Version}: {dll.Path}");
        missing++;
        continue;
      }
      var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(dll.Path))).ToLowerInvariant();
      Console.WriteLine($"== {dll.Mod} {dll.Version} (md5 {md5[..8]}{(dll.Md5 != null && !md5.StartsWith(dll.Md5) ? $", expected {dll.Md5}" : "")})");
      using var module = ModuleDefinition.ReadModule(dll.Path, new ReaderParameters { InMemory = true });
      foreach (var item in Items.Where(i => i.Mod == dll.Mod))
      {
        var result = Resolve(module, item);
        if (result.Found && item.Lookup && result.TypeName != null && !Literals.Contains(result.TypeName) && !Literals.Contains(result.TypeName[(result.TypeName.LastIndexOfAny(['.', '/']) + 1)..]))
          result = (false, result.Detail + $"  <- but Better Continents' source looks up neither \"{result.TypeName}\" nor \"{result.TypeName[(result.TypeName.LastIndexOfAny(['.', '/']) + 1)..]}\" (it cannot find it in this DLL)", result.TypeName);
        Console.WriteLine($"  {(result.Found ? "OK     " : "MISSING")} {item.What}  [{item.BcSource}]  {result.Detail}");
        if (result.Found) ok++; else missing++;
      }
    }

    // Better Continents' own source: every name of these mods in it must be in the list.
    var unlisted = Unlisted();
    Console.WriteLine("== Better Continents' source");
    foreach (var line in unlisted)
      Console.WriteLine("  UNLISTED " + line);
    if (unlisted.Count == 0)
      Console.WriteLine("  OK       every name of these mods in the source is in the list");

    Console.WriteLine($"check-jere-names: {ok} resolved, {missing} missing, {unlisted.Count} unlisted");
    return missing == 0 && unlisted.Count == 0 ? 0 : 1;
  }

  static (bool Found, string Detail, string? TypeName) Resolve(ModuleDefinition module, Item item)
  {
    if (item.Kind == Kind.Plugin)
    {
      foreach (var type in module.GetTypes())
        foreach (var attribute in type.CustomAttributes)
          if (attribute.AttributeType.Name == "BepInPlugin" && attribute.ConstructorArguments.Count > 0 && (string)attribute.ConstructorArguments[0].Value == item.Name)
            return (true, $"{type.FullName}, version {attribute.ConstructorArguments[2].Value}", null);
      return (false, $"no BepInPlugin with id \"{item.Name}\"", null);
    }
    var tried = new List<string>();
    foreach (var typeName in item.Types)
    {
      var type = module.GetTypes().FirstOrDefault(t => t.FullName == typeName);
      if (type == null)
      {
        tried.Add(typeName + " (no such type)");
        continue;
      }
      switch (item.Kind)
      {
        case Kind.Method:
        case Kind.Constructor:
          {
            var found = type.Methods.Where(m => m.Name == item.Name && (item.Params == null || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(item.Params))).ToList();
            if (item.Static is bool s) found = found.Where(m => m.IsStatic == s).ToList();
            if (item.Returns != null) found = found.Where(m => m.ReturnType.FullName == item.Returns).ToList();
            if (found.Count == 1)
              return (true, $"{typeName}.{item.Name}({string.Join(", ", found[0].Parameters.Select(p => p.ParameterType.Name))}){(found[0].IsPublic ? "" : " [non-public]")}", typeName);
            tried.Add($"{typeName}: " + (found.Count == 0 ? "no method of that name and signature (has: " + string.Join("; ", type.Methods.Where(m => m.Name == item.Name).Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName))})")) + ")" : "ambiguous"));
            break;
          }
        case Kind.Field:
          {
            var field = type.Fields.FirstOrDefault(f => f.Name == item.Name && (item.Static is not bool s || f.IsStatic == s) && (item.Returns == null || f.FieldType.FullName == item.Returns));
            if (field != null)
              return (true, $"{typeName}.{item.Name} : {field.FieldType.Name}{(field.IsPublic ? "" : " [non-public]")}", typeName);
            tried.Add($"{typeName}: no field of that name and type (has: {string.Join(", ", type.Fields.Select(f => f.Name + ":" + f.FieldType.Name).Take(8))})");
            break;
          }
        case Kind.Strings:
          {
            var method = type.Methods.FirstOrDefault(m => m.Name == item.Name && m.HasBody);
            if (method == null)
            {
              tried.Add($"{typeName}: no method {item.Name}");
              break;
            }
            var strings = method.Body.Instructions.Where(i => i.Operand is string).Select(i => (string)i.Operand).ToHashSet();
            var lacking = item.Literals!.Where(l => !strings.Contains(l)).ToList();
            if (lacking.Count == 0)
              return (true, $"{typeName}.{item.Name} holds {string.Join(", ", item.Literals!.Select(l => $"\"{l}\""))}", typeName);
            tried.Add($"{typeName}.{item.Name} lacks {string.Join(", ", lacking.Select(l => $"\"{l}\""))}");
            break;
          }
      }
    }
    return (false, string.Join(" | ", tried), null);
  }

  // The names of these mods in Better Continents' source that the list does not hold.
  static List<string> Unlisted()
  {
    var root = SourceRoot();
    var known = Items.SelectMany(i => i.Types).ToHashSet();
    var guids = Items.Where(i => i.Kind == Kind.Plugin).Select(i => i.Name).ToHashSet();
    var typeToken = new Regex(@"\b(ExpandWorldData|ExpandWorldSize|UpgradeWorld)\.[A-Za-z_][A-Za-z0-9_.+]*");
    var guidToken = new Regex("\"((?:expand_world|upgrade_world)[a-z_]*)\"");
    var result = new List<string>();
    foreach (var file in SourceFiles(root))
    {
      var relative = Path.GetRelativePath(root, file);
      var lines = File.ReadAllLines(file);
      for (int n = 0; n < lines.Length; n++)
      {
        var code = StripComment(lines[n]);
        foreach (Match m in typeToken.Matches(code))
        {
          var token = m.Value.TrimEnd('.');
          // A listed type, a member of one, or the namespace that holds one (the bridge asks for a type under each namespace it has had).
          if (!known.Any(k => token == k || token.StartsWith(k + ".") || token.StartsWith(k + "+") || k.StartsWith(token + ".")))
            result.Add($"{relative}:{n + 1}: {token}");
        }
        foreach (Match m in guidToken.Matches(code))
          if (!guids.Contains(m.Groups[1].Value))
            result.Add($"{relative}:{n + 1}: plugin id \"{m.Groups[1].Value}\"");
      }
    }
    return result;
  }

  // The code of a line without its // comment (a // inside a string is kept).
  static string StripComment(string line)
  {
    bool inString = false;
    for (int i = 0; i < line.Length - 1; i++)
    {
      char c = line[i];
      if (c == '\\' && inString) { i++; continue; }
      if (c == '"') inString = !inString;
      else if (!inString && c == '/' && line[i + 1] == '/') return line[..i];
    }
    return line;
  }
}

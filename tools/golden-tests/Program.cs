// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

// Reference ("golden") checks for the unifying refactor. They record what Better Continents does today - the config it
// binds, the bytes it saves a world's settings as (every format, disk and network), what "bc info" prints, what every
// sampling function returns on a fixed grid, how each legend parser reads its legend, the shipped presets and the export's
// defaults and export.cfg - and fail on any difference from the recorded files in golden/. A refactor must reproduce them
// exactly; a change made on purpose is re-recorded with "record" after its diff has been read, and is said in the
// CHANGELOG.
//
//   dotnet run -c Release                 compare with golden/ (exit code 1 on any difference)
//   dotnet run -c Release -- record       rewrite golden/ from this build
//
// Like the other suites it loads the plugin's pre-ILRepack DLL (obj/Release/net4.8), so build the plugin first. The
// fixtures are written into <temp>/bc-golden, which is also the working directory, so every map path is relative
// ("fx/<scenario>/heightmap.png") and the saved bytes do not depend on where the suite runs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GoldenTest;

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

  // The lines logged while an action ran (timings removed: they differ between runs).
  public static List<string> During(Action action)
  {
    int start;
    lock (Lines) start = Lines.Count;
    action();
    lock (Lines)
      return [.. Lines.Skip(start).Where(l => !l.Contains("Time to ")).Select(Clean)];
  }

  // Paths of this run and timings, which differ between runs.
  static string Clean(string line) =>
    System.Text.RegularExpressions.Regex.Replace(line.Replace(Program.Work, "<work>"), @"\b\d+ ms\b", "<n> ms");
}

// The recorded results: one file per section, "key<TAB>value" lines in the order they were added.
internal static class Golden
{
  static readonly Dictionary<string, List<(string Key, string Value)>> Sections = [];
  static readonly Dictionary<string, HashSet<string>> Keys = [];

  public static void Add(string section, string key, string value)
  {
    if (!Sections.TryGetValue(section, out var list))
    {
      Sections[section] = list = [];
      Keys[section] = [];
    }
    var unique = key;
    for (int n = 2; !Keys[section].Add(unique); n++)
      unique = $"{key}#{n}";
    list.Add((unique, value));
  }

  public static void Lines(string section, string key, IEnumerable<string> lines)
  {
    int i = 0;
    foreach (var line in lines)
      Add(section, $"{key}/{++i:000}", line);
    Add(section, $"{key}/count", i.ToString());
  }

  static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
  static string Unescape(string s)
  {
    var sb = new StringBuilder(s.Length);
    for (int i = 0; i < s.Length; i++)
    {
      if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
      var c = s[++i];
      sb.Append(c switch { 't' => '\t', 'r' => '\r', 'n' => '\n', _ => c });
    }
    return sb.ToString();
  }

  public static string Dir
  {
    get
    {
      // The project folder: the first parent of the build output that holds GoldenTest.csproj.
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GoldenTest.csproj")))
        dir = dir.Parent;
      return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("GoldenTest.csproj"), "golden");
    }
  }

  // Writes every section (record) or compares with the files; the number of differences.
  public static int Finish(bool record)
  {
    Directory.CreateDirectory(Dir);
    int differences = 0;
    foreach (var (section, list) in Sections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
      var path = Path.Combine(Dir, section + ".tsv");
      if (record)
      {
        File.WriteAllLines(path, list.Select(kv => kv.Key + "\t" + Escape(kv.Value)), new UTF8Encoding(false));
        System.Console.WriteLine($"  recorded {section}: {list.Count} values");
        continue;
      }
      if (!File.Exists(path))
      {
        System.Console.WriteLine($"  MISSING golden/{section}.tsv ({list.Count} values): run with 'record' first");
        differences++;
        continue;
      }
      var expected = new List<(string Key, string Value)>();
      foreach (var line in File.ReadAllLines(path))
      {
        int tab = line.IndexOf('\t');
        if (tab > 0)
          expected.Add((line.Substring(0, tab), Unescape(line.Substring(tab + 1))));
      }
      var now = list.ToDictionary(kv => kv.Key, kv => kv.Value);
      var then = expected.ToDictionary(kv => kv.Key, kv => kv.Value);
      int shown = 0, diff = 0;
      void Show(string text)
      {
        diff++;
        if (shown++ < 40)
          System.Console.WriteLine("    " + text);
      }
      foreach (var (key, value) in expected)
      {
        if (!now.TryGetValue(key, out var got))
          Show($"- {key}: {Short(value)}   (gone)");
        else if (got != value)
          Show($"~ {key}:\n        was {Short(value)}\n        now {Short(got)}");
      }
      foreach (var (key, value) in list)
        if (!then.ContainsKey(key))
          Show($"+ {key}: {Short(value)}   (new)");
      System.Console.WriteLine(diff == 0 ? $"  PASS {section}: {list.Count} values as recorded" : $"  FAIL {section}: {diff} difference(s){(shown > 40 ? $", first 40 shown" : "")}");
      differences += diff;
    }
    foreach (var file in Directory.GetFiles(Dir, "*.tsv"))
    {
      var name = Path.GetFileNameWithoutExtension(file);
      if (!Sections.ContainsKey(name))
      {
        System.Console.WriteLine(record ? $"  note: golden/{name}.tsv was not produced by this run" : $"  FAIL {name}: recorded, but this build produced none of it");
        if (!record)
          differences++;
      }
    }
    return differences;
  }

  static string Short(string s) => s.Length > 300 ? s.Substring(0, 300) + $"... ({s.Length} chars)" : s;
}

internal static class Hash
{
  // A short digest of values: count and SHA-256 of their bits (floats exactly, no rounding).
  public static string Floats(IEnumerable<float> values)
  {
    using var sha = SHA256.Create();
    var bytes = new List<byte>();
    int n = 0;
    float min = float.PositiveInfinity, max = float.NegativeInfinity;
    foreach (var v in values)
    {
      bytes.AddRange(BitConverter.GetBytes(BitConverter.SingleToInt32Bits(v)));
      n++;
      if (v < min) min = v;
      if (v > max) max = v;
    }
    var digest = Convert.ToHexString(sha.ComputeHash([.. bytes])).Substring(0, 24).ToLowerInvariant();
    return n == 0 ? "n=0" : $"n={n} min={min:R} max={max:R} sha={digest}";
  }

  public static string Strings(IEnumerable<string> values)
  {
    using var sha = SHA256.Create();
    var all = values.ToList();
    var digest = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", all)))).Substring(0, 24).ToLowerInvariant();
    return $"n={all.Count} sha={digest}";
  }

  public static string Bytes(byte[] data)
  {
    using var sha = SHA256.Create();
    return $"len={data.Length} sha={Convert.ToHexString(sha.ComputeHash(data)).Substring(0, 24).ToLowerInvariant()}";
  }
}

internal static class Program
{
  public static string Work = "";

  static int Main(string[] args)
  {
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      "/home/rohan/WubarrkCODING/libs-Tools/1.0/client",
      "/home/rohan/WubarrkCODING/libs-Tools",
      "/home/rohan/WubarrkCODING/libs-Tools/BepInEx/core",
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
    };
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
      foreach (var d in dirs)
      {
        var p = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(p))
          return ctx.LoadFromAssemblyPath(p);
      }
      return null;
    };
    return Run(args.Any(a => a.Equals("record", StringComparison.OrdinalIgnoreCase)));
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run(bool record)
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    Work = Path.Combine(Path.GetTempPath(), "bc-golden");
    if (Directory.Exists(Work))
      Directory.Delete(Work, true);
    Directory.CreateDirectory(Work);
    var home = Directory.GetCurrentDirectory();
    Directory.SetCurrentDirectory(Work);
    int crashes = 0;
    try
    {
      Sections.All();
    }
    catch (Exception e)
    {
      System.Console.WriteLine("CRASH " + e);
      crashes++;
    }
    finally
    {
      Directory.SetCurrentDirectory(home);
    }
    System.Console.WriteLine(record ? "== recording golden/" : "== comparing with golden/");
    int differences = Golden.Finish(record);
    if (record)
    {
      System.Console.WriteLine(crashes == 0 ? "recorded" : "recorded, BUT THE RUN CRASHED (see above): do not commit this recording");
      return crashes == 0 ? 0 : 1;
    }
    System.Console.WriteLine($"{differences} difference(s), {crashes} crash(es)");
    return differences == 0 && crashes == 0 ? 0 : 1;
  }
}

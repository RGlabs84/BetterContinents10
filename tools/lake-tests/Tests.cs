// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
//
// The shared part of the partial class Tests: the counters, the check helpers and the fixtures every part's tests use. A part adds its own Tests.<Part>.cs with
// static methods named *Test and uses these; it does not change this file.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  public static int Checks, Failures;
  // A folder of this run's own (deleted at the end).
  public static string Work = "";

  // One check.
  public static void C(bool ok, string what)
  {
    Checks++;
    if (ok)
      return;
    Failures++;
    System.Console.WriteLine("FAIL " + what);
  }

  public static void Section(string title) => System.Console.WriteLine("== " + title);

  // A test that threw.
  public static void Crash(string test, Exception e)
  {
    Checks++;
    Failures++;
    System.Console.WriteLine($"FAIL {test} threw {e.GetType().Name}: {e.Message}");
    try
    {
      System.Console.WriteLine(e.StackTrace);
    }
    catch (Exception)
    {
      // Formatting a trace reads the custom attributes of every method on it, and a Unity method's name an assembly of Unity's this suite does not have: that
      // would end the whole run. The methods' names alone need nothing.
      foreach (var frame in new System.Diagnostics.StackTrace(e, true).GetFrames())
        System.Console.WriteLine("   at " + frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name);
    }
  }

  public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

  public static string Md5(byte[] bytes, int length)
  {
    using var md5 = System.Security.Cryptography.MD5.Create();
    return Convert.ToHexString(md5.ComputeHash(bytes, 0, length)).ToLowerInvariant();
  }

  // A folder under the run's own, empty.
  public static string NewDir(string name)
  {
    var dir = Path.Combine(Work, name);
    if (Directory.Exists(dir))
      Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    return dir;
  }

  // Whether an action throws LakeFormatException whose message contains every given part (not case sensitive).
  public static bool RefusesLake(Action action, params string[] parts)
  {
    try
    {
      action();
    }
    catch (LakeFormatException e)
    {
      return parts.All(p => e.Message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
    return false;
  }

  // ---- the released Better Continents (the old reader) ---------------------------------------------------------------------------------

  public sealed class ReleasedContext(string path) : AssemblyLoadContext("BetterContinents " + Path.GetFileName(path), isCollectible: true)
  {
    protected override Assembly Load(AssemblyName name) => name.Name == "BetterContinents" ? LoadFromAssemblyPath(path) : null;
  }

  // The plugin DLL of a released version, taken out of its package into the run's folder: from BC_RELEASES, the repository's HexiumDist, or the main
  // checkout's HexiumDist (read only). Null (and a note) when none is on this machine.
  public static string Released(string version)
  {
    var name = $"BetterContinents-v{version}-hexium.zip";
    var places = new List<string>();
    var env = Environment.GetEnvironmentVariable("BC_RELEASES");
    if (env != null)
      places.Add(Path.Combine(env, name));
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      if (File.Exists(Path.Combine(dir.FullName, "BetterContinents.csproj")))
      {
        places.Add(Path.Combine(dir.FullName, "HexiumDist", name));
        places.Add(Path.Combine(dir.Parent.FullName, "BetterContinents10", "HexiumDist", name));
        places.Add(Path.Combine(dir.Parent.Parent.FullName, "BetterContinents10", "HexiumDist", name));
        break;
      }
    var zip = places.FirstOrDefault(File.Exists);
    if (zip == null)
    {
      if (Environment.GetEnvironmentVariable("BC_REQUIRE_RELEASES") == "1")
        C(false, $"the released {version} package is not on this machine (looked in {string.Join(", ", places)})");
      else
        System.Console.WriteLine($"SKIPPED: the released {version} package is not on this machine (BC_RELEASES names a folder with {name})");
      return null;
    }
    var target = Path.Combine(Work, "released-" + version);
    Directory.CreateDirectory(target);
    var dll = Path.Combine(target, "BetterContinents.dll");
    if (!File.Exists(dll))
    {
      using var archive = ZipFile.OpenRead(zip);
      var entry = archive.Entries.First(e => e.FullName == "plugins/BetterContinents.dll");
      entry.ExtractToFile(dll);
    }
    return dll;
  }
}

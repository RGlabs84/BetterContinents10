// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// The shared part of the partial class Tests: the counters, the check helpers and the fixtures every slice's tests use. A slice adds its
// own Tests.<Slice>.cs with static methods named *Test and uses these; it does not change this file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

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
      // Formatting a trace reads the custom attributes of every method on it, and a Unity method's name an assembly of Unity's this suite does
      // not have: that would end the whole run. The methods' names alone need nothing.
      foreach (var frame in new System.Diagnostics.StackTrace(e, true).GetFrames())
        System.Console.WriteLine("   at " + frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name);
    }
  }

  // Whether an action throws BakedFormatException whose message contains every given part (not case sensitive).
  public static bool Refuses(Action action, params string[] parts)
  {
    try
    {
      action();
    }
    catch (BakedFormatException e)
    {
      return parts.All(p => e.Message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
    return false;
  }

  // The message of the BakedFormatException an action throws; null when it throws none.
  public static string RefusalOf(Action action)
  {
    try
    {
      action();
    }
    catch (BakedFormatException e)
    {
      return e.Message;
    }
    return null;
  }

  // ------------------------------------------------------------------------------------------------ fixtures

  // VALtima's real file (spec 2.7): ~/valheim-testbed/valtima-work/bcp/bake/placements.bcp, md5 e9d8f303b2e582d5ddf668a5be1dae3a. The tests that
  // need it say they were skipped when it is not on the machine, and fail when BC_REQUIRE_VALTIMA is set.
  public static readonly string ValtimaPath = Environment.GetEnvironmentVariable("BC_VALTIMA_BCP")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "valheim-testbed", "valtima-work", "bcp", "bake", "placements.bcp");

  private static byte[] valtimaBytes;

  // VALtima's file's bytes, or null when it is not here.
  public static byte[] Valtima()
  {
    if (valtimaBytes != null)
      return valtimaBytes;
    if (File.Exists(ValtimaPath))
      return valtimaBytes = File.ReadAllBytes(ValtimaPath);
    return null;
  }

  // For a test that needs VALtima's file: true, or false after saying it is skipped (a failure when the environment says it is required).
  public static bool NeedValtima(string test)
  {
    if (Valtima() != null)
      return true;
    if (Environment.GetEnvironmentVariable("BC_REQUIRE_VALTIMA") == "1")
      C(false, test + ": VALtima's placements.bcp is not at " + ValtimaPath);
    else
      System.Console.WriteLine($"SKIPPED {test}: VALtima's placements.bcp is not at {ValtimaPath}");
    return false;
  }

  public static string Md5(byte[] bytes, int length)
  {
    using var md5 = System.Security.Cryptography.MD5.Create();
    return Convert.ToHexString(md5.ComputeHash(bytes, 0, length)).ToLowerInvariant();
  }

  public static string Sha256(string text)
  {
    using var sha = System.Security.Cryptography.SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(text))).ToLowerInvariant();
  }

  public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}

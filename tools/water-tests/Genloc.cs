// Added by Wubarrk on 2026-10-13 for the forced genloc (0.10.5).
//
// bc genloc (Genloc.cs): the words, when it refuses, what a taken slot is (a live run, a dead one), the counts and the lines it says, the sign of life
// that both iterators of the game's generation give (the postfix on the installed game's IL), and the facts about the game's code it relies on, read
// from the installed IL. The coroutine itself and the slot of a real ZoneSystem need the engine, and are checked in the game.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterContinents;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
  private static void GenlocTests()
  {
    GenlocWordsTests();
    GenlocDecisionTests();
    GenlocLineTests();
    GenlocGameTests();
  }

  private static void GenlocWordsTests()
  {
    Section("genloc: the words");
    if (!running) return;
    bool words = Genloc.Parse("") == Genloc.Mode.Force && Genloc.Parse("   ") == Genloc.Mode.Force && Genloc.Parse("alt") == Genloc.Mode.Alt && Genloc.Parse("ALT") == Genloc.Mode.Alt
                 && Genloc.Parse("help") == Genloc.Mode.Help && Genloc.Parse("?") == Genloc.Mode.Help && Genloc.Parse("alt now") == Genloc.Mode.Bad
                 && Genloc.Parse("wipe") == Genloc.Mode.Bad && Genloc.Parse("force") == Genloc.Mode.Bad;
    Check(words, "'bc genloc' forces a run, 'alt' is the game's alt-biome branch, 'help' says more, anything else is the usage");
    Check(Genloc.Usage == "bc genloc [alt]" && Genloc.Help.Length == 3 && Genloc.Help.All(l => !l.Contains("replace", StringComparison.OrdinalIgnoreCase)),
      "the help is calm: it never says 'replace', and has no patch version of the game in it");
    Check(Genloc.Help.All(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\b1\.0\.\d+\b")), "(no patch version)");
    Check(Genloc.Refusal(false, false, false) == "no world is loaded" && Genloc.Refusal(true, false, true) == "no world is loaded" && Genloc.Refusal(false, true, true) == "no world is loaded",
      "without a world (the menu) it refuses");
    var client = Genloc.Refusal(true, true, false);
    Check(client != null && client.Contains("client") && client.Contains("where the world runs"), "on a client it refuses and says where to run it");
    Check(Genloc.Refusal(true, true, true) == null, "on the machine that runs the world it does not");
  }

  private static void GenlocDecisionTests()
  {
    Section("genloc: a taken slot");
    if (!running) return;
    Check(Genloc.Classify(false, 0.0) == Genloc.Verdict.Start && Genloc.Classify(false, double.PositiveInfinity) == Genloc.Verdict.Start, "an empty slot: start (whatever the last sign of life)");
    Check(Genloc.Classify(true, 0.0) == Genloc.Verdict.Live && Genloc.Classify(true, 0.4) == Genloc.Verdict.Live && Genloc.Classify(true, Genloc.StaleAfter - 0.01) == Genloc.Verdict.Live,
      "a taken slot with a sign of life a moment ago: a real run, which is left alone");
    Check(Genloc.Classify(true, Genloc.StaleAfter) == Genloc.Verdict.Stale && Genloc.Classify(true, 60.0) == Genloc.Verdict.Stale && Genloc.Classify(true, double.PositiveInfinity) == Genloc.Verdict.Stale,
      "a taken slot with no sign of life for 3 s or more (or ever): the coroutine is dead, and is stopped");

    var saved = Genloc.Beat;
    try
    {
      Genloc.Beat = 0;
      Check(double.IsPositiveInfinity(Genloc.SinceBeat(Stopwatch.GetTimestamp())), "no slice has ever run: infinitely long ago");
      long now = Stopwatch.GetTimestamp();
      Genloc.Beat = now - 2 * Stopwatch.Frequency;
      Check(Math.Abs(Genloc.SinceBeat(now) - 2.0) < 1e-9, "the age of the last slice is in seconds");
    }
    finally
    {
      Genloc.Beat = saved;
    }

    // Better Continents' own reasons are kept only while a forced run goes.
    var notes = (List<string>)typeof(Genloc).GetField("Notes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var collecting = typeof(Genloc).GetField("collecting", BindingFlags.NonPublic | BindingFlags.Static)!;
    notes.Clear();
    collecting.SetValue(null, false);
    Genloc.Note("not kept");
    collecting.SetValue(null, true);
    Genloc.Note("kept");
    for (int i = 0; i < 100; i++)
      Genloc.Note("many " + i);
    Check(notes.Count == 50 && notes[0] == "kept", "a reason is kept only while a run goes, and at most 50");
    collecting.SetValue(null, false);
    notes.Clear();
  }

  private static (string, bool, bool)[] Instances(params (string Type, int Placed, int Unplaced, int UnplacedGenerated)[] types)
  {
    var list = new List<(string, bool, bool)>();
    foreach (var (type, placed, unplaced, generated) in types)
    {
      for (int i = 0; i < placed; i++) list.Add((type, true, true));
      for (int i = 0; i < unplaced; i++) list.Add((type, false, i < generated));
    }
    return [.. list];
  }

  private static void GenlocLineTests()
  {
    Section("genloc: the counts and the lines");
    if (!running) return;
    var before = Genloc.Tally.From(Instances(("Crypt4", 10, 4, 1), ("Runestone_Meadows", 3, 0, 0), ("Vendor_BlackForest", 0, 1, 0)));
    Check(before.Total == 18 && before.Placed == 13 && before.Unplaced == 5 && before.UnplacedInGeneratedZones == 1 && before.Count("Crypt4") == 14 && before.Count("None") == 0,
      "the tally: total, placed (kept), not placed (rolled again), and those not placed in a zone that is generated already");
    var start = Genloc.StartLine(before);
    Check(start.Contains("18 location(s)") && start.Contains("13 placed") && start.Contains("5 not placed") && start.Contains("1 of those stand in zones that are generated already"),
      $"the start line: {start}");
    Check(!Genloc.StartLine(Genloc.Tally.From(Instances(("A", 2, 2, 0)))).Contains("generated already"), "(the generated-zone remark only when there is one)");
    var table = Genloc.Table(before).ToList();
    Check(table.Count == 3 && table[0].Contains("Crypt4: 14 (kept 10, rolled again 4)") && table[2].Contains("Vendor_BlackForest: 1 (kept 0, rolled again 1)"), "the table has one line per type, sorted: total, kept, rolled again");

    var after = Genloc.Tally.From(Instances(("Crypt4", 10, 6, 0), ("Runestone_Meadows", 3, 0, 0), ("Vendor_BlackForest", 0, 1, 0), ("Mistlands_Harbour1", 0, 2, 0)));
    var notPlaced = Genloc.Short([("Crypt4", 20), ("Runestone_Meadows", 3), ("Vendor_BlackForest", 1), ("Mistlands_Harbour1", 2), ("Gone", 0), ("Haldor", 1)], after);
    Check(notPlaced.SequenceEqual(["Crypt4 16/20", "Haldor 0/1"]), $"locations short of their quantity: {string.Join(", ", notPlaced)}");
    var summary = Genloc.SummaryLine(before, after, 12.34, notPlaced, ["location map: Haldor not placed, its zone holds Crypt4"], true, null);
    Check(summary.StartsWith("Genloc: done in 12.3 s: 18 -> 22 location(s)") && summary.Contains("13 placed ones kept") && summary.Contains("types that changed: Crypt4 14->16, Mistlands_Harbour1 0->2")
          && summary.Contains("short of their quantity: Crypt4 16/20, Haldor 0/1") && summary.Contains("Better Continents' own placements: location map: Haldor not placed")
          && summary.EndsWith("the location icons were sent to the clients."), $"the summary: {summary}");
    var same = Genloc.SummaryLine(before, before, 1.0, [], [], true, null);
    Check(same.Contains("no type changed") && !same.Contains("short of") && !same.Contains("own placements"), "(nothing changed, nothing short, nothing to note)");
    var stopped = Genloc.SummaryLine(before, before, 5.0, [], [], false, "the run died with an error, which Unity logged");
    Check(stopped.StartsWith("Genloc: STOPPED (the run died with an error, which Unity logged)") && stopped.EndsWith(".") && !stopped.Contains("icons"), "a run that died says so, and sends no icons");
    var many = Genloc.Tally.From(Enumerable.Range(0, 30).Select(i => ($"T{i:00}", true, true)));
    var after2 = Genloc.Tally.From(Enumerable.Range(0, 30).SelectMany(i => new[] { ($"T{i:00}", true, true), ($"T{i:00}", false, false) }));
    var capped = Genloc.SummaryLine(many, after2, 1.0, [], [], true, null);
    Check(capped.Contains("and 18 more"), "a long list of changed types is cut at 12");
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
      System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
      Check(Genloc.SummaryLine(before, after, 12.34, [], [], true, null).StartsWith("Genloc: done in 12.3 s"), "(the seconds read the same in any culture)");
    }
    finally
    {
      System.Globalization.CultureInfo.CurrentCulture = culture;
    }
  }

  private static void GenlocGameTests()
  {
    Section("genloc: the game's code it relies on, and the sign of life");
    if (!running) return;
    var slot = AccessTools.Field(typeof(ZoneSystem), "m_generateLocationsCoroutine");
    var setter = AccessTools.Field(typeof(ZoneSystem), "m_loadingScreenSetter");
    var generate = AccessTools.Method(typeof(ZoneSystem), "GenerateLocations");
    var sendIcons = AccessTools.Method(typeof(ZoneSystem), "SendLocationIcons", [typeof(long)]);
    var generated = AccessTools.Method(typeof(ZoneSystem), "IsZoneGenerated", [typeof(Vector2s)]);
    var clear = AccessTools.Method(typeof(ZoneSystem), "ClearNonPlacedLocations");
    Check(slot != null && slot.FieldType == typeof(Coroutine) && setter != null && setter.FieldType == typeof(SystemResourceManager.LoadingScreenSetter) && generate != null
          && sendIcons != null && generated != null && clear != null, "the slot (a Coroutine), the loading indicator, GenerateLocations, SendLocationIcons(long), IsZoneGenerated and ClearNonPlacedLocations are in the installed game");
    if (slot == null || generate == null) return;

    // GenerateLocations does nothing while the slot is taken, and otherwise starts the coroutine into the slot.
    var il = PatchProcessor.GetOriginalInstructions(generate);
    var start = AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", [typeof(System.Collections.IEnumerator)]);
    Check(il.Any(c => c.LoadsField(slot)) && il.Any(c => c.StoresField(slot)) && il.Any(c => c.Calls(start)),
      "GenerateLocations reads the slot, and starts the coroutine into it (so a taken slot makes it do nothing)");

    // The generation's own iterator: empties the slot only at the end of a run, clears what is not placed, and clears the loading indicator in a finally.
    var outer = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ZoneSystem), "GenerateLocationsTimeSliced", Type.EmptyTypes));
    Check(outer != null, "the generation's iterator is in the installed game");
    if (outer != null)
    {
      var body = PatchProcessor.GetOriginalInstructions(outer);
      Check(body.Count(c => c.StoresField(slot)) == 1, "its MoveNext writes the slot once: the empty slot at the end of a run (an exception leaves it taken)");
      Check(body.Any(c => c.Calls(clear)), "it clears the locations that are not placed");
      // The finally of an iterator is a method of its state machine (called by MoveNext and Dispose), not part of MoveNext itself.
      var unset = AccessTools.Method(typeof(SystemResourceManager.LoadingScreenSetter), "Unset");
      bool clears = outer.DeclaringType!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Any(m => m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(c => c.Calls(unset)));
      Check(unset != null && clears, "and clears the loading indicator in a finally (which StopCoroutine does not run: Release clears it)");
    }

    // The sign of life: a postfix on both iterators.
    var iterators = Genloc.Iterators().ToList();
    Check(iterators.Count == 2 && iterators.All(m => m != null) && iterators[0] != iterators[1], "both iterators of the generation are found in the installed game");
    // The postfix is bound by hand in Awake (Install), each iterator in a try of its own. Harmony cannot patch these two methods offline (their IL reaches the
    // engine's native calls: "ECall methods must be packaged into a system module"), which is the failure Install must survive: it says so and goes on,
    // and the run's counts are watched instead, with the longer patience.
    int mark = CapturingLogHandler.Mark();
    var harmony = new Harmony("water-tests.genloc");
    try
    {
      Genloc.Install(harmony);
    }
    catch (Exception e)
    {
      Check(false, $"Install threw {e.GetType().Name}: {e.Message}");
    }
    finally
    {
      harmony.UnpatchAll(harmony.Id);
    }
    var warnings = CapturingLogHandler.Since(mark).Where(l => l.Contains("Genloc: could not watch")).ToList();
    Check(warnings.Count == 2 && Genloc.Patience == Genloc.StaleAfterCounts, $"an install that fails is said once for each iterator, does not throw, and leaves the long patience ({warnings.Count} warnings, {Genloc.Patience} s)");
    var beats = typeof(Genloc).GetNestedType("Beats", BindingFlags.NonPublic | BindingFlags.Public)!;
    var postfix = beats.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    Check(postfix.GetParameters().Length == 0 && postfix.ReturnType == typeof(void), "the postfix takes no parameters, so nothing in it has to bind to the iterator");
    long saved = Genloc.Beat;
    Genloc.Beat = 0;
    postfix.Invoke(null, null);
    Check(Genloc.Beat != 0 && Genloc.SinceBeat(Stopwatch.GetTimestamp()) < 1.0, "a slice records its moment");
    Genloc.Beat = saved;
    Check(Genloc.StaleAfter == 3.0 && Genloc.StaleAfterCounts == 120.0, "3 s of patience with the slices reported, 120 s with only the counts");
  }
}

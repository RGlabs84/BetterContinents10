// Added by Wubarrk on 2026-10-13 for the forced genloc (0.10.5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// "bc genloc": makes the world's locations again, also when the game's own genloc does nothing.
//
// WHAT A GENERATION IS (Valheim 1.0, ZoneSystem.cs; read for this command)
//   GenerateLocations() (:1689) starts ZoneSystem.GenerateLocationsTimeSliced() as a coroutine, unless the slot m_generateLocationsCoroutine is
//   taken, in which case it does nothing and says nothing. The coroutine (:1699-1856):
//   - sets the loading indicator (m_loadingScreenSetter.Set(), a count that SystemResourceManager keeps), and clears it in a finally;
//   - ClearNonPlacedLocations() (:1625): every location instance that is not placed (m_placed false) leaves m_locationInstances and the three
//     caches (m_locationIDCache, m_locationGroupCache, m_locationMaxGroupCache). Placed ones stay. Better Continents' own postfix on it then
//     puts the location map's pins and the start position override back (ZoneSystemPatch.PlaceLocations);
//   - for every enabled location with a quantity, in priority order, rolls random zones and registers an instance in each zone that holds no
//     location and is NOT generated yet (m_generatedZones), until the location has its quantity (CountNrOfLocation counts placed ones too, and a
//     unique location that is placed already is not rolled again). So only zones that are not generated yet can get a location: a zone that is
//     generated keeps what it has, and an instance that is not placed and stands in a generated zone is dropped and cannot come back;
//   - at the end sets LocationsGenerated (the setter runs the GenerateLocationsCompleted callbacks) and empties the slot. If anything throws
//     inside it (another mod's patch on the way, or BC's on a biome map without a biome), Unity logs the exception and abandons the iterator;
//     the finally clears the loading indicator, but the slot is never emptied, so the next genloc does nothing;
//   - sends nothing to clients. They get the location icons when they join (OnNewPeer) and when a location with a placed icon is placed
//     (PlaceLocations), never after a generation. This command sends the list to everybody when a run ends.
//   A location instance is placed when its zone is generated (PlaceLocations): the new ones appear as players reach their zones.
//
// WHAT THIS COMMAND ADDS
//   - If the slot is taken it finds out whether anything is running: both iterators (the generation and its per-location rolls) report each
//     slice through a postfix (Beat), so a run that is alive has a sign of life within a few frames. No sign for StaleAfter seconds means the
//     coroutine is dead: it is stopped (StopCoroutine; Unity does not run its finally then, so the loading indicator is cleared here), the slot
//     is emptied, what was found is said, and a new run is started. A sign of life means a real run: it is not touched and no second one starts.
//   - It counts the instances by type before and after, and says when the run ends. Nothing here is a coroutine of ours: Tick (every frame) looks
//     at the slot, with a timeout, and drops its state when the world is left.
//   - "alt" is the game's own branch of genloc: AltBiomeWorldData.GenerateAltBiomes() on the world, exactly as Terminal.cs:515-523 does.
internal static class Genloc
{
  // ---- the parts that need no engine ---------------------------------------------------------------------------------------

  internal enum Mode { Force, Alt, Help, Bad }

  /// <summary>The words after "genloc": nothing (a forced run), "alt", or "help".</summary>
  internal static Mode Parse(string text)
  {
    var words = text.Split([' '], StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0)
      return Mode.Force;
    if (words.Length > 1)
      return Mode.Bad;
    return words[0].ToLowerInvariant() switch
    {
      "alt" => Mode.Alt,
      "help" or "?" => Mode.Help,
      _ => Mode.Bad,
    };
  }

  /// <summary>Why the command cannot run here, or null.</summary>
  internal static string? Refusal(bool zoneSystem, bool net, bool isServer)
  {
    if (!zoneSystem || !net)
      return "no world is loaded";
    if (!isServer)
      return "this machine is a client: the world's locations are made where the world runs (single player, the host, or the dedicated server), so run it there";
    return null;
  }

  internal enum Verdict { Start, Live, Stale }

  /// <summary>What a taken slot is. sinceBeat: seconds since the run last showed a sign of life (infinity when never).</summary>
  internal static Verdict Classify(bool slotTaken, double sinceBeat, double staleAfter = StaleAfter) =>
    !slotTaken ? Verdict.Start : sinceBeat < staleAfter ? Verdict.Live : Verdict.Stale;

  /// <summary>Seconds without a slice after which a run is taken to be dead; a live run reports every frame it has work.</summary>
  internal const double StaleAfter = 3.0;
  /// <summary>The same without the slices' postfix (it could not be installed): the only sign of life is the run's counts moving, which they do once a
  /// location, and one location can take a while.</summary>
  internal const double StaleAfterCounts = 120.0;

  /// <summary>How long without a sign of life a run is given: short when both iterators report their slices, long when only its counts can be watched.</summary>
  internal static double Patience => beatsInstalled >= 2 ? StaleAfter : StaleAfterCounts;
  /// <summary>The longest a run is watched.</summary>
  internal const double GiveUpAfter = 3 * 3600.0;

  /// <summary>Location instances by type: placed (kept), not placed (cleared and rolled again), and how many of the latter stand in zones that
  /// are generated already (they cannot be placed again there).</summary>
  internal sealed class Tally
  {
    public readonly SortedDictionary<string, (int Placed, int Unplaced)> Types = new(StringComparer.Ordinal);
    public int Placed, Unplaced, UnplacedInGeneratedZones;
    public int Total => Placed + Unplaced;

    public static Tally From(IEnumerable<(string Type, bool Placed, bool InGeneratedZone)> instances)
    {
      var tally = new Tally();
      foreach (var (type, placed, generated) in instances)
      {
        tally.Types.TryGetValue(type, out var counts);
        if (placed)
        {
          tally.Placed++;
          counts.Placed++;
        }
        else
        {
          tally.Unplaced++;
          counts.Unplaced++;
          if (generated)
            tally.UnplacedInGeneratedZones++;
        }
        tally.Types[type] = counts;
      }
      return tally;
    }

    public int Count(string type) => Types.TryGetValue(type, out var c) ? c.Placed + c.Unplaced : 0;
  }

  internal static string StartLine(Tally before) =>
    $"Genloc: starting. {before.Total} location(s) now: {before.Placed} placed (kept, with every zone that is generated), {before.Unplaced} not placed (cleared, then rolled again in the zones that are not generated yet)"
    + (before.UnplacedInGeneratedZones > 0 ? $"; {before.UnplacedInGeneratedZones} of those stand in zones that are generated already and cannot be placed again there" : "")
    + ". The new ones are placed as players reach their zones.";

  /// <summary>One line per type, for the log: total, kept, to be rolled again.</summary>
  internal static IEnumerable<string> Table(Tally before) =>
    before.Types.Select(t => $"  {t.Key}: {t.Value.Placed + t.Value.Unplaced} (kept {t.Value.Placed}, rolled again {t.Value.Unplaced})");

  /// <summary>The enabled locations that have fewer instances than their quantity, as "name n/m".</summary>
  internal static List<string> Short(IEnumerable<(string Name, int Quantity)> locations, Tally after) =>
    locations.Where(l => l.Quantity > 0 && after.Count(l.Name) < l.Quantity).Select(l => $"{l.Name} {after.Count(l.Name)}/{l.Quantity}").ToList();

  /// <summary>The line that ends a run. problem: why it did not finish, or null.</summary>
  internal static string SummaryLine(Tally before, Tally after, double seconds, IReadOnlyList<string> notPlaced, IReadOnlyList<string> notes, bool iconsSent, string? problem)
  {
    string Cap(IEnumerable<string> items, int n)
    {
      var list = items.ToList();
      return string.Join(", ", list.Take(n)) + (list.Count > n ? $" and {list.Count - n} more" : "");
    }
    var changed = before.Types.Keys.Union(after.Types.Keys).OrderBy(k => k, StringComparer.Ordinal)
      .Where(k => before.Count(k) != after.Count(k)).Select(k => $"{k} {before.Count(k)}->{after.Count(k)}").ToList();
    var text = new System.Text.StringBuilder();
    text.Append(FormattableString.Invariant($"Genloc: {(problem == null ? "done" : "STOPPED (" + problem + ")")} in {seconds:0.0} s: {before.Total} -> {after.Total} location(s), {after.Placed} placed ones kept, {after.Unplaced} to be placed as players reach them"));
    text.Append(changed.Count > 0 ? $"; types that changed: {Cap(changed, 12)}" : "; no type changed");
    if (notPlaced.Count > 0)
      text.Append($"; short of their quantity: {Cap(notPlaced, 8)}");
    if (notes.Count > 0)
      text.Append($"; Better Continents' own placements: {Cap(notes, 3)}");
    text.Append(iconsSent ? "; the location icons were sent to the clients." : ".");
    return text.ToString();
  }

  internal const string Usage = "bc genloc [alt]";

  internal static readonly string[] Help =
  [
    "bc genloc: makes the world's locations again. Every location that is not placed yet is cleared and rolled again, in the zones that are not generated yet; placed ones, and every zone that is generated, stay as they are.",
    "It does what genloc does, also when the game's own run is stuck or the world was loaded with its locations marked as made: it says what it found, and when the run ends it says what changed and sends the new location icons to the clients.",
    "It runs where the world runs (single player, the host, or the dedicated server), and does not start a second run while one is going. 'bc genloc alt' passes to the game's alt-biome branch, exactly as 'genloc alt' does.",
  ];

  // ---- what Better Continents says about its own placements while a run goes (ZoneSystemPatch.PlaceLocations) -------------------

  private static readonly List<string> Notes = [];
  private static bool collecting;

  /// <summary>A reason Better Continents could not place a location of the location map or the start position; kept while a forced run goes.</summary>
  internal static void Note(string reason)
  {
    if (collecting && Notes.Count < 50)
      Notes.Add(reason);
  }

  // ---- the sign of life ------------------------------------------------------------------------------------------------------

  /// <summary>When the run last showed a sign of life (Stopwatch ticks): either iterator running a slice, or, without the postfix, its counts moving; 0 before.</summary>
  internal static long Beat;

  internal static double SinceBeat(long now) => Beat == 0 ? double.PositiveInfinity : (now - Beat) / (double)Stopwatch.Frequency;

  private static int beatsInstalled;

  // Both iterators of the generation report every slice they run (about ten a second while there is work, a frame each), so that a taken slot
  // can be told from a dead one. Patched by hand in Awake, each in a try of its own: a Harmony patch of a method fails as a whole when any
  // patch of it fails (another mod's transpiler, say), and that must never stop Better Continents' own patches (PatchAll). Without it the
  // counts of the run are watched instead (Tick), with a longer patience.
  internal static class Beats
  {
    internal static void Postfix() => Beat = Stopwatch.GetTimestamp();
  }

  /// <summary>Awake, after PatchAll.</summary>
  internal static void Install(Harmony harmony)
  {
    foreach (var iterator in Iterators())
    {
      try
      {
        if (iterator == null)
        {
          LogWarning("Genloc: an iterator of the game's location generation was not found; the sign of life of its run is watched by its counts instead.");
          continue;
        }
        harmony.CreateProcessor(iterator).AddPostfix(new HarmonyMethod(AccessTools.Method(typeof(Beats), nameof(Beats.Postfix)))).Patch();
        beatsInstalled++;
      }
      catch (Exception e)
      {
        LogWarning($"Genloc: could not watch {iterator?.Name} of the game's location generation ({e.GetType().Name}: {e.Message}); the sign of life of its run is watched by its counts instead.");
      }
    }
  }

  /// <summary>The MoveNext of ZoneSystem.GenerateLocationsTimeSliced() and of its per-location overload.</summary>
  internal static IEnumerable<MethodBase?> Iterators()
  {
    yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ZoneSystem), "GenerateLocationsTimeSliced", Type.EmptyTypes));
    yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ZoneSystem), "GenerateLocationsTimeSliced",
      [typeof(ZoneSystem.ZoneLocation), typeof(Stopwatch), typeof(ZPackage)]));
  }

  // ---- the command ---------------------------------------------------------------------------------------------------------

  /// <summary>Says a line on the console (when there is one) and in the log.</summary>
  internal static void Say(string line)
  {
    try
    {
      if (Console.instance != null)
        Console.instance.Print(line);
    }
    catch (Exception)
    {
      // No console on this machine.
    }
    Log(line);
  }

  private static bool running;
  private static ZoneSystem? zones;
  private static long startedAt;
  private static Tally? before;

  /// <summary>"bc genloc ..." (the words after genloc).</summary>
  internal static void RunLine(string text, Action<string> say)
  {
    var mode = Parse(text);
    switch (mode)
    {
      case Mode.Help:
        foreach (var line in Help)
          say(line);
        say($"Usage: {Usage}");
        return;
      case Mode.Bad:
        say($"Usage: {Usage}");
        return;
    }
    var zs = ZoneSystem.instance;
    var net = ZNet.instance;
    var refusal = Refusal(zs != null, net != null, net != null && net.IsServer());
    if (refusal != null)
    {
      say($"Genloc: not started: {refusal}.");
      return;
    }
    if (mode == Mode.Alt)
      RunAlt(say);
    else
      RunForced(zs!, say);
  }

  // The game's own branch: Terminal.cs:515-523, genloc alt.
  private static void RunAlt(Action<string> say)
  {
    say("Genloc alt: passing to the game's alt-biome branch (genloc alt), AltBiomeWorldData.GenerateAltBiomes() on the world: it makes the alt biomes' placement again and no locations.");
    try
    {
      ZNet.instance.GetWorld().m_biomeData.GenerateAltBiomes();
      say("Genloc alt: done.");
    }
    catch (Exception e)
    {
      say($"Genloc alt: the game's alt-biome branch failed ({e.GetType().Name}: {e.Message}).");
    }
  }

  private static void RunForced(ZoneSystem zs, Action<string> say)
  {
    if (running)
    {
      say($"Genloc: a run started by this command is in progress ({(Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency:0} s, {zs.GenerateLocationsProgress:P0}); not starting a second one. It says when it ends.");
      return;
    }
    var slot = zs.m_generateLocationsCoroutine;
    double since = SinceBeat(Stopwatch.GetTimestamp());
    switch (Classify(slot != null, since, Patience))
    {
      case Verdict.Live:
        say($"Genloc: the game's own run is in progress (progress {zs.GenerateLocationsProgress:P0}, its last slice {since:0.0} s ago); not starting a second one. Run this again when it has ended.");
        return;
      case Verdict.Stale:
        say($"Genloc: the game's slot for the location run is taken (locations generated: {zs.LocationsGenerated}), but nothing has run in it for {(double.IsInfinity(since) ? "as long as this game has been up" : $"{since:0} s")}: "
          + "a run that threw (Unity logged the exception) leaves it so, and the game's genloc does nothing then. Stopping that coroutine and emptying the slot.");
        Release(zs);
        break;
    }
    Begin(zs, say);
  }

  // Stops the dead (or never-started) coroutine, empties the slot, and clears the loading indicator that its finally did not get to clear.
  private static void Release(ZoneSystem zs)
  {
    try
    {
      if (zs.m_generateLocationsCoroutine != null)
        zs.StopCoroutine(zs.m_generateLocationsCoroutine);
    }
    catch (Exception e)
    {
      Log($"Genloc: StopCoroutine on the stale run: {e.GetType().Name}: {e.Message}");
    }
    zs.m_generateLocationsCoroutine = null;
    zs.m_loadingScreenSetter.Unset();
  }

  private static Tally Snapshot(ZoneSystem zs) =>
    Tally.From(zs.m_locationInstances.Select(p => (p.Value.m_location?.m_prefabName ?? "?", p.Value.m_placed, zs.IsZoneGenerated(p.Key))));

  private static void Begin(ZoneSystem zs, Action<string> say)
  {
    before = Snapshot(zs);
    Notes.Clear();
    say(StartLine(before));
    foreach (var line in Table(before))
      Log(line);
    collecting = true;
    Beat = Stopwatch.GetTimestamp();
    zs.GenerateLocations();
    if (zs.m_generateLocationsCoroutine == null)
    {
      collecting = false;
      say("Genloc: the game did not start a run (its slot is empty again at once). Look at the log.");
      return;
    }
    zones = zs;
    startedAt = Stopwatch.GetTimestamp();
    running = true;
  }

  /// <summary>Every frame (BetterContinents.Update): looks at a run this command started. Not a coroutine: it ends when the slot empties, when the
  /// run shows no sign of life, when the world is left, or after GiveUpAfter.</summary>
  internal static void Tick()
  {
    if (beatsInstalled < 2)
      WatchCounts();
    if (!running)
      return;
    var zs = ZoneSystem.instance;
    if (zs == null || zs != zones)
    {
      Say("Genloc: the world was left before the run ended; nothing more to report.");
      Reset();
      return;
    }
    long now = Stopwatch.GetTimestamp();
    double seconds = (now - startedAt) / (double)Stopwatch.Frequency;
    if (zs.m_generateLocationsCoroutine == null)
    {
      Finish(zs, seconds, null);
      return;
    }
    if (SinceBeat(now) >= Patience)
    {
      Release(zs);
      Finish(zs, seconds, "the run died with an error, which Unity logged");
      return;
    }
    if (seconds > GiveUpAfter)
    {
      Say($"Genloc: still running after {seconds / 60:0} min; no longer watched.");
      Reset();
    }
  }

  // Without the slices' postfix: a taken slot shows life when the run's counts move (a location done, an instance made), and is first seen alive.
  private static (int Instances, float Progress, int Locations) counts;

  private static void WatchCounts()
  {
    var zs = ZoneSystem.instance;
    if (zs == null || zs.m_generateLocationsCoroutine == null)
    {
      counts = default;
      return;
    }
    var now = (zs.m_locationInstances.Count, zs.GenerateLocationsProgress, zs.m_locationsRun);
    if (counts != now || Beat == 0)
    {
      counts = now;
      Beat = Stopwatch.GetTimestamp();
    }
  }

  private static void Finish(ZoneSystem zs, double seconds, string? problem)
  {
    var after = Snapshot(zs);
    var notPlaced = Short(zs.m_locations.Where(l => l.m_enable).Select(l => (l.m_prefabName, l.m_quantity)), after);
    bool sent = false;
    if (problem == null)
    {
      try
      {
        zs.SendLocationIcons(0L);
        sent = true;
      }
      catch (Exception e)
      {
        Log($"Genloc: could not send the location icons: {e.GetType().Name}: {e.Message}");
      }
    }
    Say(SummaryLine(before!, after, seconds, notPlaced, [.. Notes], sent, problem));
    foreach (var type in before!.Types.Keys.Union(after.Types.Keys))
      Log($"  {type}: {before.Count(type)} -> {after.Count(type)} (placed ones: {(after.Types.TryGetValue(type, out var counts) ? counts.Placed : 0)})");
    Reset();
  }

  private static void Reset()
  {
    running = false;
    collecting = false;
    zones = null;
    before = null;
  }
}

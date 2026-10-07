// Added by Wubarrk on 2026-10-07 for 16k worlds (0.10.3).

using System;
using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;

namespace BetterContinents;

// A new world's settings, made while the game goes on.
//
// A world made in the New World screen takes its Better Continents settings when the game first saves it (WorldPatch): the
// preset chosen there, maps and all. With big maps that is a lot of work: about 20 s for a world of five 16384 pixel maps,
// each map decoded, checked and cut into tiles, then the settings written out. It ran on the game's main thread, inside
// FejdStartup.OnNewWorldDone, so for that long the game drew nothing and answered nothing, and a desktop could offer to close it.
//
// Now Done starts that work on a worker (RunsNow), behind the game's own "Please wait" popup, and the game's OnNewWorldDone
// runs again when the settings are ready (Finish), with them and their bytes to hand, so its save only writes them. The work is
// the same either way (Make): the choice and the config are read on the main thread first (Choose), and nothing in it needs the
// main thread (world import makes its presets on a worker the same way, WorldImport). A world saved for the first time any
// other way (a dedicated server's new world) has its settings made as it is saved, by the same Build.
internal static class NewWorldBuild
{
  /// <summary>What a new world's settings are made from, read on the main thread (Choose).</summary>
  internal sealed class Choice
  {
    /// <summary>The New World screen's "Disabled".</summary>
    internal bool Disabled;
    /// <summary>The preset file chosen; null for From Config.</summary>
    internal string? PresetPath;
    /// <summary>From Config: whether the config has Better Continents on.</summary>
    internal bool Enabled;
    /// <summary>The config as it was, with a world export's export.cfg over it when Directory is one (WorldImport.DirectoryValues).</summary>
    internal ConfigValues Values = ConfigValues.Live;
    internal WideSectorsMode WideSectors;
    internal WorldGeometry? ExpandWorldSize;
  }

  /// <summary>A new world's settings, and the bytes they are saved as.</summary>
  internal sealed class Made
  {
    internal BetterContinents.BetterContinentsSettings Settings = null!;
    /// <summary>The settings file's contents (its first Length bytes); null when Better Continents is off for the world.</summary>
    internal byte[]? Bytes;
    internal int Length;
  }

  /// <summary>Main thread: the New World screen's choice (Selected Preset), and the config it is read with.</summary>
  internal static Choice Choose()
  {
    var choice = new Choice
    {
      WideSectors = BetterContinents.ConfigWideSectors.Value,
      ExpandWorldSize = BetterContinents.ExpandWorldSizeGeometry,
    };
    var selected = BetterContinents.ConfigSelectedPreset.Value;
    if (selected == Presets.DisabledName)
      choice.Disabled = true;
    else if (selected == Presets.FromConfigName)
    {
      choice.Enabled = BetterContinents.ConfigEnabled.Value;
      if (choice.Enabled)
        choice.Values = WorldImport.DirectoryValues() ?? ConfigValues.Snapshot(WorldImport.PluginConfig(), []);
    }
    else
    {
      choice.PresetPath = selected;
      choice.Values = ConfigValues.Snapshot(WorldImport.PluginConfig(), []);
    }
    return choice;
  }

  /// <summary>Any thread: the settings a new world takes from the choice.</summary>
  internal static BetterContinents.BetterContinentsSettings Build(Choice choice)
  {
    var settings = Presets.Load(choice);
    // Wide Sectors: a world made from a preset file (one made before it, or on a machine that has it Off) that reaches past the game's
    // sectors is made wide too, by this machine's choice; a world made From Config already is.
    WorldSectors.NewWorld(settings, choice.WideSectors, choice.ExpandWorldSize);
    return settings;
  }

  /// <summary>Any thread: Build, and the bytes WorldPatch writes for it.</summary>
  internal static Made Make(Choice choice)
  {
    var made = new Made { Settings = Build(choice) };
    if (made.Settings.EnabledForThisWorld)
      made.Bytes = made.Settings.Bytes(out made.Length);
    return made;
  }

  // A world's settings being made, and what the game's OnNewWorldDone takes when Finish runs it again.
  private static bool busy, resuming;
  private static Made? ready;

  /// <summary>FejdStartup.OnNewWorldDone's prefix: whether the game's own runs now. Not while a world's settings are being made (Done
  /// pressed again), and not when this starts making them: Finish runs it when they are ready.</summary>
  internal static bool RunsNow(FejdStartup fejd, bool forceLocal)
  {
    if (resuming)
      return true;
    if (busy)
      return false;
    // Nothing to wait behind, or a name the game refuses (it says so, and makes nothing): the game goes on as it is.
    if (BetterContinents.instance == null || !UnifiedPopup.IsAvailable())
      return true;
    string name = fejd.m_newWorldName.text, seed = fejd.m_newWorldSeed.text;
    if (World.HaveWorld(name))
      return true;
    Choice choice;
    try
    {
      choice = Choose();
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"New world {name}: its settings could not be made beforehand, so they are made as it is saved: {e.Message}");
      return true;
    }
    // Better Continents is off for it: nothing to make.
    if (choice.Disabled || (choice.PresetPath == null && !choice.Enabled))
      return true;
    busy = true;
    BetterContinents.Log($"New world {name}: making its settings from '{BetterContinents.ConfigSelectedPreset.Value}' while the game goes on");
    var clock = Stopwatch.StartNew();
    var task = Task.Run(() => Make(choice));
    BetterContinents.instance.StartCoroutine(Finish(task, clock, fejd, forceLocal, name, seed));
    return false;
  }

  /// <summary>WorldPatch, as the game first saves a new world: the settings made for it beforehand, if any.</summary>
  internal static Made? Take()
  {
    var made = ready;
    ready = null;
    return made;
  }

  // The popup goes up when the work takes long enough to be seen.
  private const double PopupAfter = 0.25;
  private const string PopupText = "Better Continents is preparing the world's maps.";

  private static IEnumerator Finish(Task<Made> task, Stopwatch clock, FejdStartup fejd, bool forceLocal, string name, string seed)
  {
    PopupBase? popup = null;
    while (!task.IsCompleted)
    {
      if (popup == null && clock.Elapsed.TotalSeconds >= PopupAfter && UnifiedPopup.IsAvailable())
      {
        popup = new TaskPopup(Localization.instance.Localize("$menu_pleasewait"), PopupText, localizeText: false);
        UnifiedPopup.Push(popup);
      }
      yield return null;
    }
    // A popup the game put up over this one meanwhile is closed first.
    while (popup != null && Holds(popup) && !OnTop(popup))
      yield return null;
    if (popup != null && OnTop(popup))
      UnifiedPopup.Pop();
    busy = false;
    // The lines the work logged, before those of the save.
    BetterContinents.FlushWorkerLog();
    Made? made = null;
    try
    {
      made = task.Result;
      BetterContinents.Log($"New world {name}: its settings are ready, in {clock.Elapsed.TotalSeconds:0.0} s");
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"New world {name}: making its settings beforehand failed, so they are made as it is saved: {e.GetBaseException()}");
    }
    // The menu has gone meanwhile: no world is made.
    if (fejd == null)
      yield break;
    // The world gets the name and seed Done was pressed with.
    fejd.m_newWorldName.text = name;
    fejd.m_newWorldSeed.text = seed;
    ready = made;
    resuming = true;
    try
    {
      fejd.OnNewWorldDone(forceLocal);
    }
    finally
    {
      resuming = false;
      ready = null;
    }
  }

  // The game's popups are a stack; the one on top is shown.
  private static bool Holds(PopupBase popup) => UnifiedPopup.instance is { } ui && ui.popupStack.Contains(popup);
  private static bool OnTop(PopupBase popup) => UnifiedPopup.instance is { } ui && ui.popupStack.Count > 0 && ui.popupStack.Peek() == popup;
}

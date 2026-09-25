// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// The world export HUD, modelled on Wubarrk's Eye (EyeUIManager): a small status box that the Hud Hotkey (F9) shows
// and hides, and a window that the Window Hotkey (F7) opens and closes. The window has two tabs: Export (the map
// options, an estimate, Start and Cancel, and what the last export came to) and Import (the export folders, newest
// first, each made into a New World preset or loaded through the config). The status box steps aside while the window
// is open, as the Eye's does.
//
// It draws through Better Continents' UI registry. UI.Init's scene-change handler calls Register(), which re-adds the
// callback and drops the styles, because that handler has just destroyed the textures they used. The HUD shows only in
// a loaded world, on a machine with a graphics device, while the export HUD is on (LiveConfig.HudEnabled: [09
// BetterContinents.Export] Hud, where a server's value wins on its clients). The game's menu, its own "hide the HUD"
// (Ctrl+F3) and its cutscenes hide it too. In the main menu, bc_import does what the Import tab does.
//
// The hotkeys are read from Event.current, as Better Continents' own Alt+F8 is, on key up only: one event per key press,
// never the Layout or Repaint passes. They are ignored while the player types in the chat, the console, a sign, a map
// pin, the build search or the window's own fields, and they act after the drawing, so an event never changes what it
// is drawing halfway through.
public static class ExportHud
{
  private const string CallbackKey = "ExportHud";
  private static readonly int[] Sizes = [1024, 2048, 4096, 8192];

  // Below Wubarrk's Eye's own status box (20, 20, 320 x 110), so both fit on screen at once.
  private const float BoxX = 20f, BoxY = 140f, BoxWidth = 340f, Pad = 8f;
  private const float WindowWidth = 560f, LabelWidth = 150f, ToggleWidth = 132f;
  // The hover hint under the map list: three lines, so the window keeps its size while the pointer moves.
  private const float HintHeight = 50f;

  // DebugUtils' settings window (Alt+F8) is GUILayout.Window id hash(ModInfo.Name + "SettingsWindow"); ours differs.
  private static readonly int WindowId = PickWindowId();
  private static readonly GUI.WindowFunction WindowFunction = Window;

  private static bool statusVisible = true;
  private static bool windowVisible;
  private static int windowDrawnFrame = -10;
  private static Rect windowRect = new(380f, 60f, WindowWidth, 0f);

  // The window's working options: the configured defaults until the player changes them.
  private static WorldExport.Options? options;
  private static string amountText = "", seaLevelText = "", heatScaleText = "";
  private static string? exportRoot;

  private enum Mode { Options, Running, Blocked }
  private enum Tab { Export, Import }

  // What the window shows, fixed at the start of every GUI event by Capture. IMGUI needs the same controls in an event
  // as in the Layout pass before it, so a click that starts an export, or a toggle that brings up a note, changes the
  // window from the next event on.
  private static Mode mode;
  private static Tab tab = Tab.Export;
  private static Tab? requestedTab;
  private static (Mode, Tab)? shownView;
  private static bool canStart, showLast, showError, importRunning, importFailed;
  private static string? importResult;
  private static string blockedReason = "", startProblem = "", fieldInfo = "", notes = "", estimate = "";
  private static string hint = "", hoverHint = "";
  private static List<string> lastSummary = [];

  // The Import tab's list of export folders, read when the tab opens, on Refresh, and after an export or an import.
  private static List<ExportFolder> importList = [];
  private static int importSelected;
  private static bool importRescan = true;
  private static string importStamp = "";

  // The window's body scrolls when it is taller than the screen allows; its height is the one measured last frame.
  private static Vector2 scroll;
  private static float bodyHeight, measuredBody;

  private static readonly List<(string Text, GUIStyle Style)> lines = [];

  /// <summary>The window is on screen now: the input patches below free the cursor and hold the player's look and
  /// attacks. False from the first frame the window is not drawn (closed, the menu up, the HUD hidden or off, a new
  /// scene).</summary>
  internal static bool WindowShown => windowVisible && Time.frameCount - windowDrawnFrame <= 1;

  /// <summary>UI.Init's scene-change handler: (re-)adds the HUD. The window starts closed in every scene.</summary>
  public static void Register()
  {
    Styles.Reset();
    windowVisible = false;
    exportRoot = null;
    importRescan = true;
    UI.Add(CallbackKey, OnGUI);
  }

  /// <summary>A default in [09 BetterContinents.Export] changed (LiveConfig): the window takes the new value at once.</summary>
  internal static void DefaultChanged(ConfigEntryBase entry)
  {
    // Before the window's first use there is nothing to update: it reads the defaults then.
    if (options == null)
      return;
    var defaults = WorldExport.Options.Default();
    if (entry == ConfigExportSize)
      options.Size = defaults.Size;
    else if (entry == ConfigExportHeightmapAmount)
      amountText = Inv(options.HeightmapAmount = defaults.HeightmapAmount);
    else if (entry == ConfigExportSeaLevel)
      seaLevelText = Inv(options.SeaLevel = defaults.SeaLevel);
  }

  private static void OnGUI()
  {
    try
    {
      DrawHud();
    }
    catch (ExitGUIException)
    {
      throw;
    }
    catch (System.Exception e)
    {
      // UI.OnGUI runs every Better Continents callback in one loop: an exception here must not take the others down.
      if (!loggedError)
      {
        loggedError = true;
        LogError($"Export HUD: {e}");
      }
    }
  }

  private static bool loggedError;

  private static void DrawHud()
  {
    if (!InWorld() || !LiveConfig.HudEnabled)
    {
      // Closed quietly: the keyboard may belong to another mod's window by now.
      windowVisible = false;
      return;
    }
    // The game's menu, its "hide the HUD" (Ctrl+F3) and its cutscenes hide ours without closing anything.
    var hud = Hud.instance;
    bool hidden = Menu.IsVisible() || (hud != null && !hud.IsVisible());
    if (!hidden)
    {
      if (windowVisible)
        DrawWindow();
      else if (statusVisible && Event.current.type == EventType.Repaint)
        DrawStatusBox();
    }
    HandleHotkeys();
  }

  // In a world this machine has joined or runs, with a screen: not the main menu, not while connecting, not headless.
  private static bool InWorld()
  {
    if (Headless)
      return false;
    var net = ZNet.instance;
    if (net == null || net.IsDedicated() || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
      return false;
    var world = ZNet.World;
    return world != null && !world.m_menu;
  }

  private static bool? headless;
  private static bool Headless => headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

  private static void HandleHotkeys()
  {
    var e = Event.current;
    if (e == null || e.type != EventType.KeyUp || e.keyCode == KeyCode.None)
      return;
    // A plain key press only: Alt+F8 and friends belong to others.
    if ((e.modifiers & (EventModifiers.Shift | EventModifiers.Control | EventModifiers.Alt | EventModifiers.Command)) != 0)
      return;
    if (PlayerIsTyping())
      return;
    if (e.keyCode == Key(ConfigExportWindowKey))
    {
      SetWindow(!windowVisible);
      e.Use();
    }
    else if (e.keyCode == Key(ConfigExportHudKey))
    {
      statusVisible = !statusVisible;
      e.Use();
    }
  }

  private static KeyCode Key(ConfigEntry<KeyCode>? entry) => entry?.Value ?? KeyCode.None;

  // The game's own list (PlayerController.TakeInput), plus the window's text fields.
  private static bool PlayerIsTyping()
  {
    if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || Minimap.InTextInput())
      return true;
    if (Chat.instance != null && Chat.instance.HasFocus())
      return true;
    var hud = Hud.instance;
    if (hud != null && hud.m_buildUi != null && hud.m_buildUi.SearchFieldFocused)
      return true;
    return windowVisible && GUIUtility.keyboardControl != 0;
  }

  // The player opens or closes the window (hotkey or Close).
  private static void SetWindow(bool open)
  {
    if (open == windowVisible)
      return;
    windowVisible = open;
    shownView = null;
    exportRoot = null;
    importRescan = true;
    // A text field keeps the keyboard after its window has gone; let go of it, or the next window would open typing.
    if (!open)
      GUIUtility.keyboardControl = 0;
  }

  // ---- status box ----------------------------------------------------------------------------------------------------

  private static void DrawStatusBox()
  {
    var s = Styles.Get();
    lines.Clear();
    lines.Add(($"{ModInfo.Name} {ModInfo.Version} export", s.Header));
    lines.Add((WorldLine(), s.Text));
    lines.Add((StatusLine(), s.Text));
    if (WorldImport.IsRunning)
      lines.Add(($"Importing: {WorldImport.Phase} {WorldImport.Progress:0}%", s.Text));
    if (WorldExport.LastExportDir != null)
      lines.Add(($"Last: {WorldExport.LastExportDir}", s.Dim));
    if (WorldExport.LastError != null)
      lines.Add(($"Error: {WorldExport.LastError}", s.Error));
    if (!WorldExport.IsRunning && !WorldExport.CanExport(out var reason))
      lines.Add(($"Cannot export: {reason}.", s.Dim));
    var player = Player.m_localPlayer;
    if (player != null)
    {
      int size = CurrentOptions.Size;
      var pixel = WorldExport.PixelOf(player.transform.position, size);
      lines.Add(($"You: pixel {pixel.x},{pixel.y} @ {size}", s.Text));
    }
    var keys = HotkeyHint();
    if (keys.Length > 0)
      lines.Add((keys, s.Dim));

    float inner = BoxWidth - 2f * Pad;
    float height = 2f * Pad;
    foreach (var (text, style) in lines)
      height += style.CalcHeight(new GUIContent(text), inner);
    GUI.Box(new Rect(BoxX, BoxY, BoxWidth, height), GUIContent.none, s.Box);
    float y = BoxY + Pad;
    foreach (var (text, style) in lines)
    {
      var content = new GUIContent(text);
      float h = style.CalcHeight(content, inner);
      GUI.Label(new Rect(BoxX + Pad, y, inner, h), content, style);
      y += h;
    }
  }

  private static string WorldLine()
  {
    var world = ZNet.World;
    return world == null ? "World: -" : $"World: {world.m_name} (seed {world.m_seedName})";
  }

  private static string StatusLine()
  {
    if (WorldExport.IsRunning)
      return $"Exporting: {WorldExport.Phase} {WorldExport.Progress:0}% (overall {WorldExport.Overall:0}%)";
    return WorldExport.Phase switch
    {
      "Cancelled" => "Status: Idle (the last export was cancelled)",
      "Failed" => "Status: Idle (the last export failed)",
      _ => "Status: Idle",
    };
  }

  private static string HotkeyHint()
  {
    var window = Key(ConfigExportWindowKey);
    var hud = Key(ConfigExportHudKey);
    var parts = new List<string>();
    if (window != KeyCode.None)
      parts.Add($"{window}: export and import window");
    if (hud != KeyCode.None)
      parts.Add($"{hud}: hide this box");
    return string.Join(", ", parts);
  }

  // ---- window --------------------------------------------------------------------------------------------------------

  private static void DrawWindow()
  {
    var s = Styles.Get();
    Capture();
    // Let the window fit its new content when it changes view (tab, options, progress, refused).
    if (shownView != (mode, tab))
    {
      windowRect.height = 0f;
      shownView = (mode, tab);
    }
    windowRect = GUILayout.Window(WindowId, windowRect, WindowFunction, $"{ModInfo.Name} {ModInfo.Version} - world export and import", s.Window, GUILayout.Width(WindowWidth));
    windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - windowRect.width));
    windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - 60f));
    windowDrawnFrame = Time.frameCount;
  }

  private static void Capture()
  {
    var o = CurrentOptions;
    var blocked = LiveConfig.ExportBlockedReason();
    mode = WorldExport.IsRunning ? Mode.Running : blocked != null ? Mode.Blocked : Mode.Options;
    blockedReason = blocked ?? "";
    showLast = WorldExport.LastExportDir != null;
    showError = WorldExport.LastError != null;
    lastSummary = [.. WorldExport.LastSummary];
    importRunning = WorldImport.IsRunning;
    importResult = WorldImport.LastResult;
    importFailed = WorldImport.LastError != null;
    if (Event.current.type == EventType.Layout)
    {
      // Frame-wide values change only here, before a frame's first pass, so every pass of a frame draws the same: a tab
      // clicked in the last event, the body's measured height, the hover hint, the list of exports.
      if (requestedTab != null)
      {
        tab = requestedTab.Value;
        requestedTab = null;
        importRescan |= tab == Tab.Import;
        scroll = Vector2.zero;
      }
      bodyHeight = measuredBody;
      hint = hoverHint;
      if (tab == Tab.Import)
        RescanImportsIfNeeded();
    }
    estimate = Estimate(o);
    if (mode != Mode.Options)
      return;

    string? problem = null;
    if (TryNumber(amountText, out var amount))
      o.HeightmapAmount = amount;
    else
      problem = "the Heightmap Amount is not a number";
    if (TryNumber(seaLevelText, out var seaLevel))
      o.SeaLevel = seaLevel;
    else
      problem ??= "the Sea Level is not a number";
    if (TryNumber(heatScaleText, out var heatScale))
      o.HeatScale = heatScale;
    else
      problem ??= "the Heat Scale is not a number";
    problem ??= o.Validate();
    bool can = WorldExport.CanExport(out var reason);
    canStart = can && problem == null;
    startProblem = !can ? $"Cannot start: {reason}." : problem != null ? $"Cannot start: {problem}." : "";
    fieldInfo = problem == null
      ? $"Waterline at {Inv(WorldExportMath.WaterlineValue(o.HeightmapAmount, o.SeaLevel))}; heights from "
        + $"{WorldExportMath.ValueToMetres(0f, o.HeightmapAmount, o.SeaLevel):0} m to {WorldExportMath.ValueToMetres(1f, o.HeightmapAmount, o.SeaLevel):0} m."
      : "";
    notes = Notes(o);
  }

  private static string Notes(WorldExport.Options o)
  {
    var list = new List<string>();
    if (o.Size >= 8192)
      list.Add("8192 px holds about half a gigabyte while it runs"
               + (o.Preset ? " and nearly 2 GB while the New World preset is made (switch the preset off here and run bc_import in the main menu if memory is short)" : "")
               + ", and every player joining a world built from it downloads the maps.");
    if (o.AltBiomes && o.Size < 2048)
      list.Add("Below 2048 px the alt-biome map is only approximate.");
    if (o.Locations && LiveConfig.IsClient)
      list.Add("On a client the location map holds only the locations the server shows on the map; 'bc_export server' exports the server's own world (admins).");
    if (o.EdgeDropoff == false)
      list.Add("Drop-off off also removes the world edge: no edge push, and the sea goes on for ever.");
    return string.Join("\n", list);
  }

  // Pixels, a rough size per map before compression, the memory it holds, and a rough time. The times are guesses from
  // the size alone; the world and the machine decide.
  private static string Estimate(WorldExport.Options o)
  {
    double mpx = (double)o.Size * o.Size / 1e6;
    int grey16 = (o.Heightmap ? 1 : 0) + (o.Forest ? 1 : 0) + (o.Heat ? 1 : 0);
    int colour = (o.Biomes ? 1 : 0) + (o.AltBiomes ? 1 : 0) + (o.Locations ? 1 : 0) + (o.Paint ? 1 : 0);
    int grey8 = (o.Lava ? 1 : 0) + (o.Moss ? 1 : 0);
    double raw = mpx * (2 * grey16 + 3 * colour + grey8);
    // The terrain pass holds heights, lava, moss and paint at once (up to 7 bytes a pixel), every other pass one map at
    // a time (3 bytes a pixel at most).
    double memory = mpx * Math.Max((o.Heightmap ? 2 : 0) + (o.Lava ? 1 : 0) + (o.Moss ? 1 : 0) + (o.Paint ? 3 : 0), 3) + 30;
    // The preset build keeps the heightmap and biome map decoded while the others decode one at a time: about 26 bytes a
    // pixel at its peak (440 MB at 4096 px, measured offline), 20 without paint.
    double preset = mpx * (o.Paint ? 26 : 20) + 30;
    var time = o.Size <= 1024 ? "well under a minute" : o.Size <= 2048 ? "about a minute" : o.Size <= 4096 ? "a few minutes" : "ten minutes or more";
    var text = $"{o.Size} x {o.Size} px = {mpx:0.#} million pixels a map. Before compression a 16-bit map is {2 * mpx:0} MB and a colour map "
               + $"{3 * mpx:0} MB ({raw:0} MB for these maps; the PNGs are usually much smaller). About {memory:0} MB of memory while it runs";
    if (o.Preset)
      text += $", {preset:0} MB while the preset is made";
    return text + $"; {time} (a rough guess).";
  }

  private static void Window(int id)
  {
    var s = Styles.Get();
    GUILayout.BeginHorizontal();
    GUILayout.Label(WorldLine(), s.Text);
    if (GUILayout.Button("Close", GUILayout.ExpandWidth(false)))
      SetWindow(false);
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    if (GUILayout.Toggle(tab == Tab.Export, "Export this world", GUI.skin.button) && tab != Tab.Export)
      requestedTab = Tab.Export;
    if (GUILayout.Toggle(tab == Tab.Import, "Import an export", GUI.skin.button) && tab != Tab.Import)
      requestedTab = Tab.Import;
    GUILayout.EndHorizontal();

    // The body scrolls once it is taller than the screen below the window's top allows. Whether it scrolls is decided
    // here, from last frame's measured height, and the body is laid out at the same width either way (the scrollbar's
    // width is always reserved). A scrollbar the scroll view showed on its own narrowed the body, which wrapped its
    // text onto more lines and grew taller, so the view grew to fit it next frame, the scrollbar went, the text
    // unwrapped, the view shrank, and the window jittered between the two layouts every frame (seen 2026-09-25
    // with the Last export section shown).
    float cap = Mathf.Max(160f, Screen.height - windowRect.y - 110f);
    bool scrolls = bodyHeight > 0f && bodyHeight + 4f > cap;
    float h = bodyHeight > 0f ? Mathf.Min(bodyHeight + 4f, cap) : Mathf.Min(480f, cap);
    float bodyWidth = WindowWidth - s.Window.padding.horizontal - GUI.skin.verticalScrollbar.fixedWidth - 6f;
    scroll = GUILayout.BeginScrollView(scroll, false, scrolls, GUIStyle.none, scrolls ? GUI.skin.verticalScrollbar : GUIStyle.none, GUILayout.Height(h));
    GUILayout.BeginVertical(GUILayout.Width(bodyWidth));
    if (tab == Tab.Import)
      DrawImport(s);
    else
    {
      switch (mode)
      {
        case Mode.Blocked:
          GUILayout.Label($"World export is off here: {blockedReason}.", s.Error);
          break;
        case Mode.Running:
          DrawProgress(s);
          break;
        default:
          DrawOptions(s);
          break;
      }
      DrawLast(s);
    }
    GUILayout.EndVertical();
    if (Event.current.type == EventType.Repaint)
      measuredBody = GUILayoutUtility.GetLastRect().height;
    GUILayout.EndScrollView();

    // A click on the window's background takes the keyboard from its text fields, so the hotkeys work again.
    if (Event.current.type == EventType.MouseDown)
      GUI.FocusControl(null);
    GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
  }

  // ---- export tab ----------------------------------------------------------------------------------------------------

  private static void DrawOptions(Styles s)
  {
    var o = CurrentOptions;
    GUILayout.Label("Maps (point at one for what it holds and costs)", s.Header);
    o.Heightmap = MapToggle(o.Heightmap, "Heightmap", "how high the ground is (16-bit)",
      "heightmap.png: the ground height of every pixel, 16-bit grey, encoded for the Heightmap Amount and Sea Level below.", s);
    o.Biomes = MapToggle(o.Biomes, "Biomes", "which biome is where",
      "biomemap.png and biomemap.txt: the biome of every pixel, in Better Continents' biome colours.", s);
    o.Locations = MapToggle(o.Locations, "Locations", "one dot per location",
      "locationmap.png and .txt: the start temple, bosses, traders and dungeons, one pixel each. Complete only on the machine that runs the world.", s);
    o.Forest = MapToggle(o.Forest, "Forest", "how dense the forest is (16-bit)",
      "forestmap.png: the forest density, added to the game's own (or exact, below), so trees stand where they stood.", s);
    o.Heat = MapToggle(o.Heat, "Heat", "the Ashlands heat (16-bit)",
      "heatmap.png: the Ashlands heat along the south, read back with the Heat Scale below. It decides where the Ashlands' weather and lava damage are.", s);
    o.AltBiomes = MapToggle(o.AltBiomes, "Alt biomes", "Dark Meadows, Wolf Mountain... as placed",
      "altbiomemap.png and .txt: every alt biome exactly where the game put it; the new world plants these and no random ones.", s);
    o.Lava = MapToggle(o.Lava, "Lava", "where Ashlands lava is (8-bit)",
      "lavamap.png: the lava of the Ashlands, so it stays with the terrain whatever seed the new world has.", s);
    o.Moss = MapToggle(o.Moss, "Moss", "the Mistlands ground cover (8-bit)",
      "mossmap.png: the Mistlands ground cover, so it stays with the terrain whatever seed the new world has.", s);
    o.Paint = MapToggle(o.Paint, "Paint", "the ground paint of every biome (costs space)",
      $"paintmap.png: the ground paint (snow depth and the like) of every pixel. It costs 3 bytes a pixel ({3.0 * o.Size * o.Size / 1e6:0} MB at this size "
      + "before compression) in the folder, in the preset and in every world made from it, which every joining player downloads, and it keeps the old "
      + "paint where you later change the heights. With the same seed and unedited heights the game paints the ground the same without it.", s);
    o.Sources = MapToggle(o.Sources, "Sources", "this world's own Better Continents maps",
      "sources/: the maps this Better Continents world was made from, as its settings keep them. Its ground colour, vegetation and spawn maps also go into the folder, so the new world keeps them.", s);
    o.Preset = MapToggle(o.Preset, "New World preset", "pick the export in New World at once",
      "When the maps are written, the preset \"<world> <time>\" is made from the folder (a few seconds). It is not selected: pick it in the New World screen's Better Continents box. After editing the PNGs, the Import tab makes it again.", s);
    GUILayout.Label(hint.Length > 0 ? hint : "Point at a map to read what it holds.", s.Dim, GUILayout.Height(HintHeight));
    if (Event.current.type == EventType.Repaint)
      hoverHint = GUI.tooltip ?? "";

    GUILayout.Label("Encoding", s.Header);
    GUILayout.BeginHorizontal();
    GUILayout.Label($"Size: {o.Size} px", s.Text, GUILayout.Width(LabelWidth));
    foreach (var size in Sizes)
    {
      if (Radio(o.Size == size, size.ToString(CultureInfo.InvariantCulture)))
        o.Size = size;
    }
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    amountText = Field("Heightmap Amount", amountText, s);
    seaLevelText = Field("Sea Level", seaLevelText, s);
    heatScaleText = Field("Heat Scale", heatScaleText, s);
    if (fieldInfo.Length > 0)
      GUILayout.Label(fieldInfo, s.Dim);
    o.ZoneBlend = GUILayout.Toggle(o.ZoneBlend, "Heights as the game builds terrain (zone-corner biome blend)");
    o.ForestExact = GUILayout.Toggle(o.ForestExact, "Exact forest (Forestmap Multiply 1 / Add 1; harder to paint)");
    GUILayout.BeginHorizontal();
    GUILayout.Label("Map edge drop-off:", s.Text, GUILayout.Width(LabelWidth));
    if (Radio(o.EdgeDropoff == null, "as the world"))
      o.EdgeDropoff = null;
    if (Radio(o.EdgeDropoff == true, "on"))
      o.EdgeDropoff = true;
    if (Radio(o.EdgeDropoff == false, "off"))
      o.EdgeDropoff = false;
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    GUILayout.Label(estimate, s.Dim);
    if (notes.Length > 0)
      GUILayout.Label(notes, s.Dim);

    GUILayout.Space(6f);
    GUILayout.BeginHorizontal();
    bool enabled = GUI.enabled;
    GUI.enabled = enabled && canStart;
    if (GUILayout.Button("Start export"))
      StartExport();
    GUI.enabled = enabled;
    if (GUILayout.Button("Defaults", GUILayout.ExpandWidth(false)))
      options = NewOptions();
    GUILayout.EndHorizontal();
    if (startProblem.Length > 0)
      GUILayout.Label(startProblem, s.Error);
    GUILayout.Label($"Into {ExportRoot()}", s.Dim);
  }

  // A map's switch, its one-line description, and the longer text shown when the pointer is on either.
  private static bool MapToggle(bool on, string label, string what, string tip, Styles s)
  {
    GUILayout.BeginHorizontal();
    on = GUILayout.Toggle(on, new GUIContent(label, tip), GUILayout.Width(ToggleWidth));
    GUILayout.Label(new GUIContent(what, tip), s.Dim);
    GUILayout.EndHorizontal();
    return on;
  }

  private static void DrawProgress(Styles s)
  {
    GUILayout.Label($"Exporting: {WorldExport.Phase} {WorldExport.Progress:0}%", s.Text);
    Bar(WorldExport.Progress, s);
    GUILayout.Label($"Overall {WorldExport.Overall:0}%", s.Text);
    Bar(WorldExport.Overall, s);
    bool enabled = GUI.enabled;
    GUI.enabled = enabled && WorldExport.Phase != "Cancelling";
    if (GUILayout.Button("Cancel"))
      WorldExportCommands.CancelExport(ConsolePrint);
    GUI.enabled = enabled;
  }

  // The last export that finished: where it is, what it came to, and the folder buttons.
  private static void DrawLast(Styles s)
  {
    if (showLast)
    {
      GUILayout.Space(4f);
      GUILayout.Label("Last export", s.Header);
      GUILayout.Label(WorldExport.LastExportDir ?? "", s.Dim);
      foreach (var line in lastSummary)
        GUILayout.Label(line, line.StartsWith("WARNING") ? s.Error : s.Text);
      GUILayout.BeginHorizontal();
      if (GUILayout.Button("Copy the folder path", GUILayout.ExpandWidth(false)))
        GUIUtility.systemCopyBuffer = WorldExport.LastExportDir ?? "";
      if (GUILayout.Button("Open the folder", GUILayout.ExpandWidth(false)))
        OpenFolder(WorldExport.LastExportDir);
      GUILayout.FlexibleSpace();
      GUILayout.EndHorizontal();
    }
    if (showError)
      GUILayout.Label($"Error: {WorldExport.LastError}", s.Error);
  }

  private static void StartExport()
  {
    // The export announces itself in the console and the log; only a refusal needs saying here.
    if (!WorldExport.Start(CurrentOptions.Clone()))
      ConsolePrint($"World export cannot start: {WorldExport.LastError}");
  }

  // ---- import tab ----------------------------------------------------------------------------------------------------

  // Reads the export folders again when asked, or when an export or an import has finished since the last read.
  private static void RescanImportsIfNeeded()
  {
    var stamp = $"{WorldExport.LastExportDir}|{WorldImport.LastResult}|{WorldExport.IsRunning}";
    if (!importRescan && stamp == importStamp)
      return;
    importRescan = false;
    importStamp = stamp;
    var selectedPath = importSelected >= 0 && importSelected < importList.Count ? importList[importSelected].Path : null;
    try
    {
      importList = WorldImport.ListExports();
    }
    catch (System.Exception e)
    {
      importList = [];
      LogWarning($"Export HUD: cannot list the exports: {e.Message}");
    }
    int again = selectedPath == null ? -1 : importList.FindIndex(f => f.Path == selectedPath);
    importSelected = again >= 0 ? again : 0;
  }

  private static void DrawImport(Styles s)
  {
    GUILayout.Label("Make a world from an export", s.Header);
    GUILayout.Label("Pick an export and press Make preset: it is built from the PNGs as they are now and selected in the New World "
                    + "screen, so Start, New World and Create make the world. Use as config instead makes every new world read the "
                    + "folder's PNGs as they are when it is created, for when you keep editing them.", s.Dim);
    GUILayout.Space(4f);
    if (importList.Count == 0)
      GUILayout.Label($"No exports yet in {SafeRoot()}. Export this world in the other tab first.", s.Text);
    for (int i = 0; i < importList.Count; i++)
    {
      if (Radio(i == importSelected, importList[i].Label, expand: true))
        importSelected = i;
    }
    var f = importSelected >= 0 && importSelected < importList.Count ? importList[importSelected] : null;
    if (f != null)
    {
      GUILayout.Label(f.Path, s.Dim);
      GUILayout.Label($"Preset name: \"{f.PresetName}\"{(f.PresetExists ? " (made already; Make preset makes it again)" : "")}", s.Dim);
    }
    bool busy = f == null || importRunning || (WorldExport.IsRunning && WorldExport.RunningDir == f.Path);
    GUILayout.BeginHorizontal();
    bool enabled = GUI.enabled;
    GUI.enabled = enabled && !busy;
    if (GUILayout.Button("Make preset") && f != null)
      WorldImport.Start(f.Path, WorldImport.Mode.Preset, ConsolePrint);
    if (GUILayout.Button("Use as config") && f != null)
      WorldImport.Start(f.Path, WorldImport.Mode.Config, ConsolePrint);
    GUI.enabled = enabled;
    if (GUILayout.Button("Refresh", GUILayout.ExpandWidth(false)))
      importRescan = true;
    GUILayout.EndHorizontal();
    GUILayout.BeginHorizontal();
    GUI.enabled = enabled && f != null;
    if (GUILayout.Button("Open the folder", GUILayout.ExpandWidth(false)))
      OpenFolder(f?.Path);
    if (GUILayout.Button("Copy the folder path", GUILayout.ExpandWidth(false)))
      GUIUtility.systemCopyBuffer = f?.Path ?? "";
    GUI.enabled = enabled;
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    if (importRunning)
    {
      GUILayout.Label($"{WorldImport.Phase} {WorldImport.Progress:0}%", s.Text);
      Bar(WorldImport.Progress, s);
    }
    else if (importResult != null)
      GUILayout.Label(importResult, importFailed ? s.Error : s.Text);
    GUILayout.Label("The console's 'bc_import' does the same, also in the main menu ('bc_import help').", s.Dim);
  }

  private static string SafeRoot()
  {
    try
    {
      return WorldImport.RootDir;
    }
    catch
    {
      return "BetterContinents";
    }
  }

  // Shows a folder in the system's file manager. Unity opens a file:// URL with the desktop's handler (Explorer on
  // Windows, xdg-open on Linux, Finder on macOS); anything that goes wrong is said in the console, not thrown.
  private static void OpenFolder(string? dir)
  {
    try
    {
      if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
      {
        ConsolePrint($"There is no folder {dir} to open.");
        return;
      }
      Application.OpenURL(new Uri(Path.GetFullPath(dir) + Path.DirectorySeparatorChar).AbsoluteUri);
    }
    catch (System.Exception e)
    {
      ConsolePrint($"Could not open {dir}: {e.Message}. Its path is copied with 'Copy the folder path'.");
    }
  }

  // ---- shared --------------------------------------------------------------------------------------------------------

  private static WorldExport.Options CurrentOptions => options ??= NewOptions();

  private static WorldExport.Options NewOptions()
  {
    var o = WorldExport.Options.Default();
    amountText = Inv(o.HeightmapAmount);
    seaLevelText = Inv(o.SeaLevel);
    heatScaleText = Inv(o.HeatScale);
    return o;
  }

  // <save data>/BetterContinents/<world>/, where each export gets its own export-<time> folder.
  private static string ExportRoot()
  {
    if (exportRoot == null)
    {
      try
      {
        exportRoot = Path.Combine(Path.GetDirectoryName(WorldExport.DefaultExportDir()) ?? "", "export-<time>");
      }
      catch (System.Exception e)
      {
        exportRoot = $"(unknown: {e.Message})";
      }
    }
    return exportRoot;
  }

  // A radio button: true when the player turns it on.
  private static bool Radio(bool on, string label, bool expand = false) => GUILayout.Toggle(on, label, GUILayout.ExpandWidth(expand)) && !on;

  private static string Field(string label, string text, Styles s)
  {
    GUILayout.BeginHorizontal();
    GUILayout.Label(label, s.Text, GUILayout.Width(LabelWidth));
    text = GUILayout.TextField(text, GUILayout.Width(90f));
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    return text;
  }

  private static void Bar(float percent, Styles s)
  {
    var r = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true));
    if (Event.current.type != EventType.Repaint)
      return;
    GUI.DrawTexture(r, s.BarBack);
    GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(percent / 100f), r.height), s.BarFill);
  }

  // Invariant culture; a decimal comma is taken as a point.
  private static bool TryNumber(string text, out float value) =>
    float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

  private static string Inv(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

  private static void ConsolePrint(string line)
  {
    try
    {
      Console.instance?.Print(line);
    }
    catch
    {
      // No console on this peer; the export logs its own lines.
    }
  }

  private static int PickWindowId()
  {
    int settings = (ModInfo.Name + "SettingsWindow").GetHashCode();
    int id = (ModInfo.Name + "ExportWindow").GetHashCode();
    return id == settings ? id + 1 : id;
  }

  // Built inside a GUI call (GUI.skin is only valid there) and dropped on every scene change with UI's textures.
  private sealed class Styles
  {
    private static Styles? current;

    public readonly GUIStyle Box, Window, Header, Text, Dim, Error;
    public readonly Texture2D BarBack, BarFill;

    public static Styles Get() => current ??= new Styles();

    public static void Reset() => current = null;

    private Styles()
    {
      var gold = new Color(1f, 0.78f, 0.42f);
      var text = new Color(0.93f, 0.9f, 0.84f);
      // Like the Eye's: a dark translucent box with gold headings. No alignment is set anywhere: UI.cs notes that
      // assigning one fails on this game's IMGUI.
      Box = new GUIStyle(GUI.skin.box) { normal = { background = UI.CreateFillTexture(new Color(0f, 0f, 0f, 0.8f)) } };
      var windowBack = UI.CreateFillTexture(new Color(0.06f, 0.06f, 0.06f, 0.94f));
      Window = new GUIStyle(GUI.skin.window);
      Window.normal.background = windowBack;
      Window.onNormal.background = windowBack;
      Window.normal.textColor = gold;
      Window.onNormal.textColor = gold;
      Header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true, normal = { textColor = gold } };
      Text = new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = text } };
      Dim = new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(0.66f, 0.66f, 0.66f) } };
      Error = new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(1f, 0.5f, 0.45f) } };
      BarBack = UI.CreateFillTexture(new Color(0.2f, 0.2f, 0.2f, 1f));
      BarFill = UI.CreateFillTexture(gold);
    }
  }

  // While the window is on screen the game must behave as it does for its own inventory: a free cursor, no mouse look,
  // no attack on a click, and no hotbar or interact keys (the window's number fields take digits, and 1-8 are the
  // hotbar). The game locks the cursor again every frame (GameCamera.UpdateMouseCapture, from its LateUpdate), gates
  // look and attacks with PlayerController.InInventoryEtc and the rest with Player.TakeInput, so all three follow
  // WindowShown. The capture is skipped rather than undone afterwards, so the cursor is not hidden and shown again
  // every frame. Walking still works, as it does with the inventory open.
  [HarmonyPatch]
  private static class InputPatches
  {
    [HarmonyPrefix, HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    private static bool UpdateMouseCapturePrefix()
    {
      if (!WindowShown)
        return true;
      ZCursor.LockState = CursorLockMode.None;
      ZCursor.Show();
      return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlayerController), nameof(PlayerController.InInventoryEtc))]
    private static void InInventoryEtcPostfix(ref bool __result)
    {
      if (WindowShown)
        __result = true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
    private static void TakeInputPostfix(ref bool __result)
    {
      if (WindowShown)
        __result = false;
    }
  }
}

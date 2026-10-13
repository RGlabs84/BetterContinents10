// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// The export window's third tab: baked placements. Every button makes a bc_bake line and runs it through BakeCommands.RunLine, as the
// console does, so the window and the console do the same thing and say the same lines; the tab shows what bc_bake said, newest at the
// bottom. A change of the world goes in two steps, as at the console: a dry run, then Confirm, which runs the dry run's own line with
// "confirm". An area is pinned at the dry run ("at <x> <z>", where the player stood), so Confirm acts on what the dry run counted
// wherever the player has walked since; Confirm waits while the options differ from the dry run's.
// On a client of a dedicated server every line but info, stats, hide and show goes to the server ("server ...", which acts for its
// admins only), and the server's answer comes back as the game's remote prints, which the tab takes for ten minutes after each question
// and after each line of the answer (RemotePrintPatch).
// A click asks for a line; the line runs at the start of the next frame's Layout pass (CaptureBake), because IMGUI must draw the same
// controls in every pass of a frame, and the tab draws the lines as they were at that Layout pass.
public static partial class ExportHud
{
  private static readonly int[] BakeRadii = [8, 16, 32, 64, 128, 256];
  private const int MaxBakeLines = 300;
  private const float PaneHeight = 190f, RemoteWait = 600f;

  private enum BakeAsk { None, Bake, UnbakeHere, UnbakeWorld, Undo, Load, Orphans }

  private static int bakeRadius = 32;
  private static bool bakeTown = true, bakeAny, bakeConvert, bakeAll;

  // The dry run that Confirm runs: what it was, its line (with its "at"), what Confirm says it does, and the options it was made with.
  private static BakeAsk dryAsk;
  private static string? dryLine;
  private static string dryLabel = "", dryOptions = "";

  // The line a click asked for, run at the next Layout pass, and the dry run it is (None: a line that changes nothing).
  private static string? queuedLine;
  private static BakeAsk queuedDry;
  private static string queuedLabel = "";

  // What bc_bake said, oldest first, from the command's own output (now, or later from its coroutine) and the server's answer.
  private static readonly List<string> bakeLines = [];
  private static List<string> bakeShown = [];
  private static int bakeVersion, bakeShownVersion = -1;
  private static Vector2 paneScroll;
  private static float remoteUntil;

  // The tab's view of the world, taken at each Layout pass.
  private static string bakeLayerText = "", bakeRunningText = "";
  private static bool bakeOnClient, bakeHidden, bakeConfirmable;

  // The options a dry run of this kind was made with; Confirm runs only while they are still the same.
  private static string BakeOptions(BakeAsk ask) => ask switch
  {
    BakeAsk.Bake => $"{bakeRadius} {bakeTown} {bakeAny} {bakeConvert}",
    BakeAsk.UnbakeHere => $"{bakeRadius} {bakeAll}",
    BakeAsk.UnbakeWorld => bakeAll.ToString(),
    BakeAsk.Load => bakeConvert.ToString(),
    _ => "",
  };

  // Layout only (Capture): the line a click asked for, then what the tab draws this frame.
  private static void CaptureBake()
  {
    if (queuedLine != null)
    {
      var line = queuedLine;
      var dry = queuedDry;
      queuedLine = null;
      queuedDry = BakeAsk.None;
      RunBake(line);
      if (dry != BakeAsk.None)
      {
        dryAsk = dry;
        dryLine = line;
        dryLabel = queuedLabel;
        dryOptions = BakeOptions(dry);
      }
    }
    lock (bakeLines)
    {
      if (bakeShownVersion != bakeVersion)
      {
        bakeShown = [.. bakeLines];
        bakeShownVersion = bakeVersion;
        // The newest line in view.
        paneScroll.y = float.MaxValue;
      }
    }
    var net = ZNet.instance;
    bakeOnClient = net != null && !net.IsServer();
    var layer = BakedLayerStore.Current;
    bakeLayerText = layer == null
      ? BetterContinents.Settings.LayerSentApart ? "This world: its baked pieces are on their way from the server." : "This world: no baked pieces yet."
      : $"This world: layer revision {layer.Revision}, {layer.Placements:N0} pieces in {layer.Zones.Count:N0} zones.";
    var running = BakeRunner.Running;
    bakeRunningText = running != null ? $"Running: {running}. One change runs at a time; its last line says when it is done." : "";
    bakeConfirmable = dryLine != null && dryOptions == BakeOptions(dryAsk) && (bakeOnClient || running == null);
  }

  // One bc_bake line, as the console runs it; on a client of a dedicated server a line that changes the world goes to the server.
  private static void RunBake(string line)
  {
    var command = BakeCommandLine.Parse(line);
    var net = ZNet.instance;
    bool remote = command.Error == null && !BakeCommandLine.ReadOnly(command.Verb) && net != null && !net.IsServer();
    string full = remote ? "server " + line : line;
    Say("> bc_bake " + full);
    if (remote)
      remoteUntil = Time.realtimeSinceStartup + RemoteWait;
    try
    {
      BakeCommands.RunLine(full, text =>
      {
        Say(text);
        Log(text);
      });
    }
    catch (Exception e)
    {
      Say($"bc_bake: {e.Message}");
      LogError($"Export HUD: bc_bake {full}: {e}");
    }
  }

  private static void Say(string? text)
  {
    if (text == null)
      return;
    lock (bakeLines)
    {
      foreach (var part in text.Split('\n'))
        bakeLines.Add(part.TrimEnd('\r'));
      if (bakeLines.Count > MaxBakeLines)
        bakeLines.RemoveRange(0, bakeLines.Count - MaxBakeLines);
      bakeVersion++;
    }
  }

  // A click: the line runs at the next Layout pass. A dry run remembers what its Confirm will do.
  private static void Ask(string line, BakeAsk dry = BakeAsk.None, string label = "")
  {
    queuedLine = line;
    queuedDry = dry;
    queuedLabel = label;
  }

  private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

  // Where the player stands, for an area pinned at its dry run; null without a player.
  private static Vector3? PlayerAt => Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : null;

  private static void DrawBake(Styles s)
  {
    GUILayout.Label("Bake what players built into the world", s.Header);
    GUILayout.Label("Baked pieces become part of the world: every player's game draws them, so they cost no objects, no save size and no "
                    + "network traffic. Doors, chests, stations, beds, fires and lights stay real pieces, and nothing that gives items (food, "
                    + "flowers, crops) is ever baked. Each change starts as a dry run that says what it would do; Confirm does it.", s.Dim);
    GUILayout.Label(bakeLayerText, s.Text);
    if (bakeRunningText.Length > 0)
      GUILayout.Label(bakeRunningText, s.Text);
    if (bakeOnClient)
      GUILayout.Label("You are a player on a server: changes go to the server, which makes them for its admins only.", s.Dim);

    var at = PlayerAt;
    bool enabled = GUI.enabled;

    // ---- around you
    GUILayout.Space(4f);
    GUILayout.Label("Around you", s.Header);
    GUILayout.BeginHorizontal();
    GUILayout.Label("Radius:", s.Text, GUILayout.Width(70f));
    foreach (var r in BakeRadii)
      if (Radio(bakeRadius == r, $"{r} m"))
        bakeRadius = r;
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    bakeTown = GUILayout.Toggle(bakeTown, new GUIContent("Town: doors, chests and stations become protected parts of the layer",
      "town: the pieces that stay real (doors, chests, stations, beds, fires, lights, signs) are kept as protected parts of the bake, so an "
      + "undo or a later load of a town file finds them, and nobody can break or remove them. Pieces that are only comfort decor (rugs, chairs, "
      + "banners) are baked and give no comfort; pieces that are only lights or fires you do not cook on become lit copies that never need fuel."));
    bakeAny = GUILayout.Toggle(bakeAny, new GUIContent("Also pieces nobody built (ruins and the like)",
      "any: pieces without a builder (a location's walls, ruins) are baked too. A zone reset places a location again over them."));
    bakeConvert = GUILayout.Toggle(bakeConvert, new GUIContent("Convert: this world was made without Better Continents' maps",
      "convert: a world of the game's own terrain becomes a Better Continents world that keeps that terrain and carries baked pieces. "
      + "Every player then needs Better Continents. The dry run says when it is needed."));
    GUILayout.BeginHorizontal();
    GUI.enabled = enabled && at != null;
    if (GUILayout.Button("Bake: dry run") && at is { } p)
      Ask($"area {bakeRadius} at {Num(p.x)} {Num(p.z)}" + (bakeTown ? " town" : "") + (bakeAny ? " any" : "") + (bakeConvert ? " convert" : ""),
        BakeAsk.Bake, $"bake {bakeRadius} m around {Num(p.x)}, {Num(p.z)}");
    if (GUILayout.Button("Unbake: dry run") && at is { } q)
      Ask($"unbake {bakeRadius} at {Num(q.x)} {Num(q.z)}" + (bakeAll ? " all" : ""), BakeAsk.UnbakeHere,
        $"unbake {bakeRadius} m around {Num(q.x)}, {Num(q.z)}");
    GUI.enabled = enabled;
    bakeAll = GUILayout.Toggle(bakeAll, new GUIContent("All: a town file's pieces too",
      "all: unbaking also turns the pieces of a compiler's file (a town file) into real pieces, not only what was baked in game."), GUILayout.ExpandWidth(false));
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();

    // ---- undo and the way out
    GUILayout.Space(4f);
    GUILayout.Label("Undo and the way out", s.Header);
    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Undo the last change: dry run"))
      Ask("undo", BakeAsk.Undo, "undo the last change");
    if (GUILayout.Button("Unbake the whole world: dry run"))
      Ask("unbake world" + (bakeAll ? " all" : ""), BakeAsk.UnbakeWorld, bakeAll ? "unbake the whole world, town files' pieces too" : "unbake the whole world");
    GUILayout.EndHorizontal();
    GUILayout.Label("Unbaking the whole world makes every baked piece a real piece again: do it before removing Better Continents from a world "
                    + "(with All, a town file's pieces too).", s.Dim);

    // ---- the confirm, while a dry run waits for it
    if (dryLine != null)
    {
      GUILayout.Space(4f);
      GUILayout.Label(bakeConfirmable ? "Confirm does what the dry run below says." : "The options changed since the dry run, or a change is still running: run the dry run again.",
        bakeConfirmable ? s.Text : s.Error);
      GUILayout.BeginHorizontal();
      GUI.enabled = enabled && bakeConfirmable;
      if (GUILayout.Button($"Confirm: {dryLabel}"))
      {
        Ask(dryLine + " confirm");
        dryLine = null;
      }
      GUI.enabled = enabled;
      if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false)))
        dryLine = null;
      GUILayout.EndHorizontal();
    }

    // ---- the town file
    GUILayout.Space(4f);
    GUILayout.Label("A town file (placements.bcp)", s.Header);
    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Load the Directory's file: dry run"))
      Ask("load" + (bakeConvert ? " convert" : ""), BakeAsk.Load, "load placements.bcp from the Directory");
    if (GUILayout.Button("Export this world's layer"))
      Ask("export");
    GUILayout.EndHorizontal();
    GUILayout.Label("Load brings the world's pieces in line with the file and keeps what players put in chests and stations; Export writes the "
                    + "layer as it is now, to share or to edit.", s.Dim);

    // ---- look
    GUILayout.Space(4f);
    GUILayout.Label("Look", s.Header);
    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Info"))
      Ask("info");
    if (GUILayout.Button("Changes"))
      Ask("list");
    if (GUILayout.Button("Check"))
      Ask("check");
    if (GUILayout.Button("Orphans"))
      Ask("orphans");
    if (GUILayout.Button("Destroy orphans: dry run"))
      Ask("orphans destroy", BakeAsk.Orphans, "destroy the orphans");
    GUILayout.EndHorizontal();
    GUILayout.BeginHorizontal();
    if (GUILayout.Button("What is drawn here (stats)"))
      Ask("stats");
    if (GUILayout.Button(bakeHidden ? "Show baked pieces" : "Hide baked pieces"))
    {
      Ask(bakeHidden ? "show" : "hide");
      bakeHidden = !bakeHidden;
    }
    GUILayout.EndHorizontal();

    // ---- what bc_bake said
    GUILayout.Space(4f);
    GUILayout.BeginHorizontal();
    GUILayout.Label("What bc_bake said", s.Header);
    GUILayout.FlexibleSpace();
    if (GUILayout.Button("Copy", GUILayout.ExpandWidth(false)))
      GUIUtility.systemCopyBuffer = string.Join("\n", bakeShown);
    if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false)))
    {
      lock (bakeLines)
      {
        bakeLines.Clear();
        bakeVersion++;
      }
    }
    GUILayout.EndHorizontal();
    paneScroll = GUILayout.BeginScrollView(paneScroll, GUILayout.Height(PaneHeight));
    if (bakeShown.Count == 0)
      GUILayout.Label("Nothing yet. Info says what this world holds.", s.Dim);
    foreach (var line in bakeShown)
      GUILayout.Label(line, line.StartsWith(">") ? s.Header : s.Text);
    GUILayout.EndScrollView();
    GUILayout.Label("The console's 'bc_bake' does the same ('bc_bake help'), with a box as well as a circle.", s.Dim);
  }

  // The server's answer to a line asked with "server": the game prints it on the console (ZNet.RPC_RemotePrint); the tab shows it too,
  // while it waits for one.
  [HarmonyPatch(typeof(ZNet), "RPC_RemotePrint")]
  private static class RemotePrintPatch
  {
    private static void Postfix(string text)
    {
      if (Time.realtimeSinceStartup >= remoteUntil)
        return;
      Say(text);
      remoteUntil = Time.realtimeSinceStartup + RemoteWait;
    }
  }
}

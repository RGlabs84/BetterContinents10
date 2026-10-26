// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// The editor pack in the export window: a block in the Export tab (the pack's switch, its containers, amount, size and masks) and
// a section in the Import tab ("Editor return": the export's editor/return/ folder, how the picture came back, Start). The state of
// the Import tab's section is read from disk at the start of a frame and held, so every pass of a frame draws the same controls.
public static partial class ExportHud
{
  private static string editorAmountText = "auto";

  // The Import tab's "Editor return" state, for the selected export.
  private static string editorReturnKey = "";
  private static string editorReturnFound = "", editorReturnProblem = "";
  private static bool editorReturnReady;
  private static int editorOrientIndex = -1;   // -1: find it out; else EditorOrientation.Names
  private static bool editorUnitMetres, editorAmountAuto, editorSeaAuto, editorPresetAfter;

  private static readonly string[] EditorOrientChoices = ["auto", .. EditorOrientation.Names];

  // Capture(): the amount field of the Export tab. Null when all is well, else why the export cannot start.
  private static string? EditorCapture(WorldExport.Options o)
  {
    if (!o.Editor)
      return null;
    var text = editorAmountText.Trim().ToLowerInvariant();
    if (text.Length == 0 || text == "auto")
    {
      o.EditorAmount = 0f;
      return null;
    }
    if (!TryNumber(text, out var amount))
      return "the editor pack's Heightmap Amount is not a number (or 'auto')";
    o.EditorAmount = amount;
    return null;
  }

  // NewOptions(): the window's own fields go back with the options.
  private static void ResetEditorFields(WorldExport.Options o)
  {
    editorAmountText = o.EditorAmount > 0f ? Inv(o.EditorAmount) : "auto";
  }

  private static void DrawEditorPack(Styles s, WorldExport.Options o)
  {
    GUILayout.Space(4f);
    GUILayout.Label("Editor pack (Gaea and other terrain editors)", s.Header);
    o.Editor = MapToggle(o.Editor, "Editor pack", "the ground as 32-bit floats, and masks",
      "editor/: height.tif (32-bit float, the ground as Better Continents' value v), masks for water, slope, forest, biomes and locations, editor.json (how the pixels map to the world) and a README. "
      + "Load it in Gaea (or World Machine, Houdini...), sculpt, save the height in editor/return/, and bring it back in the Import tab. Nothing else in the export changes.", s);
    if (!o.Editor)
      return;
    GUILayout.BeginHorizontal();
    GUILayout.Label("Float files:", s.Text, GUILayout.Width(LabelWidth));
    bool tiff = GUILayout.Toggle((o.EditorFormats & WorldExport.EditorFormat.Tiff) != 0, "TIFF (32-bit)");
    bool raw = GUILayout.Toggle((o.EditorFormats & WorldExport.EditorFormat.Raw32) != 0, ".r32 (0 to 1)");
    var formats = (tiff ? WorldExport.EditorFormat.Tiff : 0) | (raw ? WorldExport.EditorFormat.Raw32 : 0);
    // At least one: the last one on cannot be switched off.
    if (formats != WorldExport.EditorFormat.None)
      o.EditorFormats = formats;
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    editorAmountText = Field("Editor amount (or auto)", editorAmountText, s);
    GUILayout.Label("The Heightmap Amount of the pack's values: auto is the larger of 2 and this world's own (2 spans -30 to 370 m, the sea at 0.15).", s.Dim);
    GUILayout.BeginHorizontal();
    GUILayout.Label($"Pack size: {o.EffectiveEditorSize} px", s.Text, GUILayout.Width(LabelWidth));
    if (Radio(o.EditorSize == 0, "same as the maps"))
      o.EditorSize = 0;
    foreach (var size in Sizes)
    {
      if (size > WorldExport.SizeLimit)
        continue;
      if (Radio(o.EditorSize == size, size.ToString(CultureInfo.InvariantCulture)))
        o.EditorSize = size;
    }
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    o.EditorMetres = GUILayout.Toggle(o.EditorMetres, "Also height-metres.tif (the same grid in metres)");
    GUILayout.Label("Masks", s.Text);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Water, "Water", "water.png: 255 where the ground is under the game's 30 m water plane.", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Inland, "Inland water", "water-inland.png: water that is not the Ocean biome (lakes, rivers, marsh).", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Slope, "Slope", "slope.png: degrees / 90, 16-bit, from the float heights.", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Forest, "Forest", "forest-density.png: how dense the forest is, 0 bare to 1 densest, 16-bit.", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Biomes, "Biomes", "biome-<name>.png: 255 where the biome is, one file for each biome that has ground.", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Index, "Biome index", "biome-index.png: the biome's number (biome-index.txt names them).", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Soft, "Soft biomes", "biome-soft-<name>.png: how much of the ground's height blend each biome has (16-bit); needs the zone-corner blend.", s);
    o.EditorMasks = MaskToggle(o.EditorMasks, WorldExport.EditorMask.Protect, "Locations", "protect-locations.png: white around every town, dungeon and boss place, so a sculptor can keep them as they are.", s);
    GUILayout.Label(EditorEstimate(o), s.Dim);
    if (o.EffectiveEditorSize > 8192)
      GUILayout.Label("Past 8192 px the float file is over 256 MB; the heights go back as the 16-bit heightmap.png plus its fine file, up to 16384 px.", s.Dim);
  }

  private static WorldExport.EditorMask MaskToggle(WorldExport.EditorMask masks, WorldExport.EditorMask which, string label, string tip, Styles s)
  {
    bool on = MapToggle((masks & which) != 0, label, "", tip, s);
    return on ? masks | which : masks & ~which;
  }

  // Disk the pack takes, before compression of the masks.
  private static string EditorEstimate(WorldExport.Options o)
  {
    double mpx = (double)o.EffectiveEditorSize * o.EffectiveEditorSize / 1e6;
    int floats = ((o.EditorFormats & WorldExport.EditorFormat.Tiff) != 0 ? 1 : 0) + ((o.EditorFormats & WorldExport.EditorFormat.Raw32) != 0 ? 1 : 0) + (o.EditorMetres ? 1 : 0);
    var m = o.EditorMasks;
    int eight = ((m & WorldExport.EditorMask.Water) != 0 ? 1 : 0) + ((m & WorldExport.EditorMask.Inland) != 0 ? 1 : 0) + ((m & WorldExport.EditorMask.Index) != 0 ? 1 : 0) + ((m & WorldExport.EditorMask.Protect) != 0 ? 1 : 0);
    int sixteen = ((m & WorldExport.EditorMask.Slope) != 0 ? 1 : 0) + ((m & WorldExport.EditorMask.Forest) != 0 ? 1 : 0);
    return $"The pack: {floats} float file(s) of {4 * mpx:0} MB each and masks of 1 to 2 bytes a pixel before compression ({eight} of 8 bits, {sixteen} of 16, and a file for each biome); "
           + "it takes about as long as the terrain pass of an export, and about the same memory.";
  }

  // ---- the Import tab: Editor return -----------------------------------------------------------------------------------------

  // At the start of a frame: what the selected export has in editor/return/, read again only when the selection or a rescan says so.
  private static void CaptureEditorReturn(ExportFolder? f)
  {
    if (Event.current.type != EventType.Layout)
      return;
    var key = (f?.Path ?? "") + "|" + importStamp + "|" + EditorIngest.IsRunning;
    if (key == editorReturnKey)
      return;
    editorReturnKey = key;
    editorReturnReady = false;
    editorReturnFound = editorReturnProblem = "";
    if (f == null)
      return;
    try
    {
      var editor = Path.Combine(f.Path, "editor");
      if (!Directory.Exists(editor))
      {
        editorReturnProblem = "This export has no editor/ folder: export with the editor pack on first.";
        return;
      }
      var ret = IngestOptionsFinder(f.Path, out var error);
      if (ret == null)
      {
        editorReturnProblem = error ?? "nothing found";
        return;
      }
      editorReturnFound = Path.GetFileName(ret);
      editorReturnReady = true;
    }
    catch (Exception e)
    {
      editorReturnProblem = e.Message;
    }
  }

  private static string? IngestOptionsFinder(string exportDir, out string? error) => EditorIngest.FindReturn(exportDir, new IngestOptions(), out error);

  private static void DrawEditorReturn(Styles s)
  {
    var f = importSelected >= 0 && importSelected < importList.Count ? importList[importSelected] : null;
    CaptureEditorReturn(f);
    GUILayout.Space(6f);
    GUILayout.Label("Editor return (the ground you made in Gaea or another editor)", s.Header);
    GUILayout.Label("Save the editor's height in the export's editor/return/ folder (height.tif, or .r32), then press Bring it back: a new folder "
                    + "<export>-edited is made beside the export with heightmap.png and its fine file, and the other maps copied. Pick that folder above and Make preset.", s.Dim);
    if (editorReturnReady)
      GUILayout.Label($"Found: editor/return/{editorReturnFound}", s.Text);
    else if (editorReturnProblem.Length > 0)
      GUILayout.Label(editorReturnProblem, s.Dim);
    GUILayout.BeginHorizontal();
    GUILayout.Label("Orientation:", s.Text, GUILayout.Width(LabelWidth));
    for (int k = 0; k < EditorOrientChoices.Length; k++)
    {
      if (k == 5)
      {
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("", s.Text, GUILayout.Width(LabelWidth));
      }
      if (Radio(editorOrientIndex + 1 == k, EditorOrientChoices[k]))
        editorOrientIndex = k - 1;
    }
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    GUILayout.BeginHorizontal();
    GUILayout.Label("Values are:", s.Text, GUILayout.Width(LabelWidth));
    if (Radio(!editorUnitMetres, "the pack's (Raw build)"))
      editorUnitMetres = false;
    if (Radio(editorUnitMetres, "metres"))
      editorUnitMetres = true;
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    editorAmountAuto = GUILayout.Toggle(editorAmountAuto, "Pick the Heightmap Amount to hold the edited heights (amount=auto)");
    editorSeaAuto = GUILayout.Toggle(editorSeaAuto, "Pick the Sea Level so the deepest ground fits (sealevel=auto)");
    editorPresetAfter = GUILayout.Toggle(editorPresetAfter, "Make the New World preset from the result at once");
    bool enabled = GUI.enabled;
    GUI.enabled = enabled && f != null && editorReturnReady && !EditorIngest.IsRunning;
    if (GUILayout.Button("Bring it back") && f != null)
    {
      var options = new IngestOptions
      {
        Orient = editorOrientIndex < 0 ? null : (EditorOrient)editorOrientIndex,
        Unit = editorUnitMetres ? EditorUnit.Metres : EditorUnit.V,
        UnitGiven = editorUnitMetres,
        AmountAuto = editorAmountAuto,
        SeaAuto = editorSeaAuto,
        Preset = editorPresetAfter,
      };
      EditorIngest.Start(f.Path, options, ConsolePrint);
    }
    GUI.enabled = enabled;
    if (EditorIngest.IsRunning)
    {
      GUILayout.Label($"{EditorIngest.Phase} {EditorIngest.Progress:0}%", s.Text);
      Bar(EditorIngest.Progress, s);
      if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false)))
        EditorIngest.Cancel();
    }
    else if (EditorIngest.LastResult != null)
      GUILayout.Label(EditorIngest.LastError != null ? $"Last: {EditorIngest.LastResult}" : $"Last: made {EditorIngest.LastResult}", EditorIngest.LastError != null ? s.Error : s.Text);
    GUILayout.Label("The console's 'bc_import edited' does the same ('bc_import edited help').", s.Dim);
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The baked layer in a world export (spec 3.3): the real export coroutine on a fake world whose settings hold a layer (a GameTerrain world, which
// exports as the vanilla world it is) writes placements.bcp beside the maps, byte for byte, and the README, export.cfg and manifest.json say so.
internal static class LayerExport
{
  public static void Run(string work)
  {
    System.Console.WriteLine("== the baked layer in a world export");
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Layer World", m_seedName = "LayerSeed", m_seed = 7 };
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);

    // A layer with records, a registry and ground.
    var edit = LayerEdit.New("export test");
    edit.PaletteIndexFor(new PaletteEntry([new Candidate("stone_wall_2x1", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab));
    edit.AddOperation(new OperationInfo(1, OperationKind.BakeArea, OperationState.InLayer, 1_700_000_000L, "Tester", 0, 0, 0, 0, 20f, 3, 0, 0, "0.10.4", [new ValueSet([ZdoValue.OfInt(1, 1)])]));
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0, source: 1, valueSet: 0), ZoneRecord.CreateYaw(0, 5, 40, 3, 90, source: 1, valueSet: 0), ZoneRecord.CreateYaw(0, 70, 41, 9, 10)]);
    var heights = new short[BakedFormat.GroundVertices];
    edit.SetSections(new ZoneKey(0, 0), new ZoneSections { Ground = new ZoneGround(30f, heights, Enumerable.Repeat((byte)255, BakedFormat.GroundVertices).ToArray()) });
    var layer = edit.Build().Layer;
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true, Version = 12, Layer = layer };

    var o = WorldExport.Options.Default();
    o.Size = 128;
    o.Biomes = false;
    o.Forest = false;
    o.Heat = false;
    o.AltBiomes = false;
    o.Lava = false;
    o.Moss = false;
    o.Paint = false;
    o.Locations = false;
    o.Sources = false;
    o.Preset = false;
    o.FineHeights = WorldExport.FineHeightsMode.Off;
    var dir = Path.Combine(work, "layer-export");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);
    var drive = EndToEnd.Drive(job);
    while (drive.MoveNext())
      Thread.Sleep(1);
    Program.C(!WorldExport.IsRunning && WorldExport.Phase == "Done" && WorldExport.LastError == null, $"the export of a world with a layer finishes: phase {WorldExport.Phase}, error {WorldExport.LastError ?? "none"}");
    var file = Path.Combine(dir, "placements.bcp");
    Program.C(File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(layer.Bytes.Take(layer.Length)), $"placements.bcp is the layer's bytes ({layer.Length:N0} of them), every source and the registry");
    Program.C(!Directory.GetFiles(dir).Any(f => f.EndsWith(".tmp")), "no .tmp file is left");
    var read = BakedLayer.Read(File.ReadAllBytes(file), (int)new FileInfo(file).Length);
    Program.C(read.Id == layer.Id && read.HasRegistry && read.RecordsOfSource(1) == 2, "and it reads as the same layer");
    var readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
    Program.C(readme.Contains("placements.bcp") && readme.Contains("baked layer"), "README.txt lists it");
    var cfg = File.ReadAllText(Path.Combine(dir, "export.cfg"));
    Program.C(cfg.Contains("placements.bcp") && cfg.Contains("baked layer"), "export.cfg says a new world takes it");
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
    var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
    Program.C(files.Contains("placements.bcp"), "manifest.json lists it among the files");
    var baked = doc.RootElement.GetProperty("bakedLayer");
    Program.C(baked.GetProperty("revision").GetInt64() == layer.Revision && baked.GetProperty("records").GetInt64() == 3 && baked.GetProperty("zones").GetInt32() == layer.Zones.Count
      && baked.GetProperty("bytes").GetInt32() == layer.Length && baked.GetProperty("file").GetString() == "placements.bcp", "and describes it (revision, records, zones, bytes)");
    Program.C(!doc.RootElement.GetProperty("betterContinentsWorld").GetBoolean(), "a world that keeps the game's own terrain is exported as a vanilla world");

    // A world without a layer exports no such file.
    BC.Settings = new BC.BetterContinentsSettings();
    var dir2 = Path.Combine(work, "layer-export-none");
    var job2 = EndToEnd.MakeJob(o, dir2, wg);
    EndToEnd.SetRunning(true);
    var drive2 = EndToEnd.Drive(job2);
    while (drive2.MoveNext())
      Thread.Sleep(1);
    Program.C(WorldExport.Phase == "Done" && !File.Exists(Path.Combine(dir2, "placements.bcp")) && !File.ReadAllText(Path.Combine(dir2, "README.txt")).Contains("placements.bcp"), "a world without a layer has no placements.bcp, and the README does not mention it");

    Cancelled(work, o, wg);
  }

  // A cancel while the layer is being written: placements.bcp goes with the rest of the export's files (and the folder the export made). Zone extras of
  // random bytes (kept as they are, and they do not compress) make a layer of tens of MB, so that the write is still going when the cancel comes.
  private static void Cancelled(string work, WorldExport.Options o, WorldGenerator wg)
  {
    var random = new System.Random(4);
    var edit = LayerEdit.New("export cancel test");
    edit.PaletteIndexFor(new PaletteEntry([new Candidate("stone_wall_2x1", 0, 0, 0)], BakedRole.Static, BakedCollision.Prefab));
    edit.AddRecords([ZoneRecord.CreateYaw(0, 3, 40, 3, 0)]);
    for (int i = 0; i < 3; i++)
    {
      var junk = new byte[8 * 1024 * 1024];
      random.NextBytes(junk);
      edit.SetSections(new ZoneKey(10 + i, 0), new ZoneSections { Extras = [new ZoneExtra("junk", junk)] });
    }
    var layer = edit.Build().Layer;
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true, Version = 12, Layer = layer };
    var dir = Path.Combine(work, "layer-export-cancel");
    var file = Path.Combine(dir, "placements.bcp");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);
    var drive = EndToEnd.Drive(job);
    bool cancelled = false, sawFile = false, sawPartial = false;
    while (drive.MoveNext())
    {
      if (File.Exists(file))
      {
        sawFile = true;
        try
        {
          if (new FileInfo(file).Length < layer.Length)
            sawPartial = true;
        }
        catch (IOException)
        {
        }
      }
      if (!cancelled && WorldExport.Phase == "Writing the baked layer")
      {
        cancelled = true;
        WorldExport.Cancel();
      }
      Thread.Sleep(1);
    }
    System.Console.WriteLine($"    a layer of {layer.Length:N0} bytes; the cancel came while the file was {(sawPartial ? "half written" : sawFile ? "there, whole" : "not yet there")}");
    Program.C(cancelled, "the export was in 'Writing the baked layer' at a frame, and was cancelled there");
    Program.C(WorldExport.Phase == "Cancelled" && !WorldExport.IsRunning && WorldExport.LastError == null, $"the export ends 'Cancelled' ({WorldExport.Phase})");
    Program.C(!File.Exists(file) && !Directory.Exists(dir), "placements.bcp is deleted with the folder the export made");
  }
}

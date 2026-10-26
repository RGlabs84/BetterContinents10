// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The lakes and the clearing mask in a world export (BUILD-SPEC-TARN.md 2.6): the real export coroutine on a fake world whose settings hold both (a GameTerrain world,
// which exports as the vanilla world it is) writes lakes.txt and clearmask.bin beside the maps in Tarn's formats, the lake lines in the round-trip form, and the README,
// export.cfg and manifest.json say so; a world made from the folder has the same lakes and mask; a world without them writes neither file.
internal static class LakeExport
{
  public static void Run(string work)
  {
    System.Console.WriteLine("== the lakes and the clearing mask in a world export");
    EndToEnd.PatchOnce();
    var world = new World { m_name = "Lake World", m_seedName = "LakeSeed", m_seed = 9 };
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);

    var rng = new System.Random(5);
    var lakes = new LakeSet(Enumerable.Range(0, 40).Select(i =>
    {
      float x = (float)((rng.NextDouble() * 2 - 1) * 9000), z = (float)((rng.NextDouble() * 2 - 1) * 9000);
      return new Lake("lake" + i, x, z, 31f + (float)rng.NextDouble() * 150f, x - 50.123456f, z - 80.987654f, x + 60.1f, z + 70.7f, (float)rng.NextDouble() * 20f);
    }).ToList(), 0.7f, 0);
    var bits = new byte[(64 * 64 + 7) / 8];
    for (int i = 0; i < bits.Length; i++)
      bits[i] = (byte)(i * 37);
    var file = new byte[16 + bits.Length];
    System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(file, 0);
    BitConverter.GetBytes(64).CopyTo(file, 8);
    BitConverter.GetBytes(512f).CopyTo(file, 12);
    bits.CopyTo(file, 16);
    var mask = ClearMask.FromTarnFile(file);
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true, Version = 12, Lakes = lakes, Clearing = mask };

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
    var dir = Path.Combine(work, "lake-export");
    var job = EndToEnd.MakeJob(o, dir, wg);
    EndToEnd.SetRunning(true);
    var drive = EndToEnd.Drive(job);
    while (drive.MoveNext())
      Thread.Sleep(1);
    Program.C(!WorldExport.IsRunning && WorldExport.Phase == "Done" && WorldExport.LastError == null, $"the export of a world with lakes and a mask finishes: phase {WorldExport.Phase}, error {WorldExport.LastError ?? "none"}");
    var lakeFile = Path.Combine(dir, "lakes.txt");
    var maskFile = Path.Combine(dir, "clearmask.bin");
    Program.C(File.Exists(maskFile) && File.ReadAllBytes(maskFile).SequenceEqual(file), "clearmask.bin is Tarn's .clear file, byte for byte");
    Program.C(File.Exists(lakeFile) && File.ReadAllLines(lakeFile)[0].StartsWith("# Tarn lakes: name;x;z;level;minX;minZ;maxX;maxZ;depth"), "lakes.txt starts with Tarn's header comment");
    Program.C(!Directory.GetFiles(dir).Any(f => f.EndsWith(".tmp")), "no .tmp file is left");
    var text = File.ReadAllLines(lakeFile).Where(l => l.Length > 0 && l[0] != '#').ToList();
    Program.C(text.Count == 40, $"one line a lake ({text.Count})");
    // Tarn reads it (its own float.Parse), and gets the lakes to the bit.
    bool same = true;
    for (int i = 0; i < text.Count; i++)
    {
      var p = text[i].Split(';');
      float G(int k) => float.Parse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture);
      var l = lakes.Lakes[i];
      same &= p.Length == 9 && p[0] == l.Name && G(1) == l.X && G(2) == l.Z && G(3) == l.Level && G(4) == l.MinX && G(5) == l.MinZ && G(6) == l.MaxX && G(7) == l.MaxZ && G(8) == l.Depth;
    }
    Program.C(same, "Tarn's own reader (float.Parse, invariant) gets every lake back to the bit from the lines");
    // A world made from the folder has the same lakes and mask.
    var made = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    made.ReadLakeFiles(dir);
    Program.C(made.Lakes != null && made.Lakes.Lakes.SequenceEqual(lakes.Lakes) && made.Clearing != null && made.Clearing.ToTarnFile().SequenceEqual(file), "a world made from the folder has the same lakes (to the bit) and the same mask");
    var readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
    Program.C(readme.Contains("lakes.txt") && readme.Contains("clearmask.bin") && readme.Contains("Tarn's format"), "README.txt lists both");
    var cfg = File.ReadAllText(Path.Combine(dir, "export.cfg"));
    Program.C(cfg.Contains("lakes.txt and clearmask.bin in this folder are the world's lakes and clearing mask") && cfg.Contains("[12 BetterContinents.Lakes]") && cfg.Contains("Lake Depth Scale = 0.7"), "export.cfg says a new world takes them, and keeps the depth scale (0.7)");
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
    var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
    Program.C(files.Contains("lakes.txt") && files.Contains("clearmask.bin"), "manifest.json lists both among the files");
    var l72 = doc.RootElement.GetProperty("lakes");
    var l73 = doc.RootElement.GetProperty("clearing");
    Program.C(l72.GetProperty("count").GetInt32() == 40 && Math.Abs(l72.GetProperty("depthScale").GetSingle() - 0.7f) < 1e-6 && l72.GetProperty("file").GetString() == "lakes.txt"
      && l73.GetProperty("size").GetInt32() == 64 && l73.GetProperty("extent").GetSingle() == 512f && l73.GetProperty("file").GetString() == "clearmask.bin" && l73.GetProperty("km2").GetDouble() > 0, "and describes them (count, depth scale, size, extent, km2)");

    // A world without them exports neither file.
    foreach (var (name, settings) in new[]
             {
               ("none", new BC.BetterContinentsSettings()),
               ("empty-set", new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true, Version = 12, Lakes = LakeSet.Empty.WithDepthScale(0.3f) }),
             })
    {
      BC.Settings = settings;
      var other = Path.Combine(work, "lake-export-" + name);
      var job2 = EndToEnd.MakeJob(o, other, wg);
      EndToEnd.SetRunning(true);
      var drive2 = EndToEnd.Drive(job2);
      while (drive2.MoveNext())
        Thread.Sleep(1);
      var text2 = File.ReadAllText(Path.Combine(other, "README.txt")) + File.ReadAllText(Path.Combine(other, "export.cfg"));
      Program.C(WorldExport.Phase == "Done" && !File.Exists(Path.Combine(other, "lakes.txt")) && !File.Exists(Path.Combine(other, "clearmask.bin")) && !text2.Contains("lakes.txt") && !text2.Contains("Lake Depth Scale"),
        $"a world with {(name == "none" ? "no lakes or mask" : "an empty lake set")} has no lakes.txt or clearmask.bin, and the README and export.cfg do not mention them");
    }

    // Lakes alone, a mask alone.
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true, GameTerrain = true, Version = 12, Clearing = mask };
    var maskDir = Path.Combine(work, "lake-export-mask");
    var job3 = EndToEnd.MakeJob(o, maskDir, wg);
    EndToEnd.SetRunning(true);
    var drive3 = EndToEnd.Drive(job3);
    while (drive3.MoveNext())
      Thread.Sleep(1);
    var cfg3 = File.ReadAllText(Path.Combine(maskDir, "export.cfg"));
    Program.C(File.Exists(Path.Combine(maskDir, "clearmask.bin")) && !File.Exists(Path.Combine(maskDir, "lakes.txt")) && cfg3.Contains("clearmask.bin in this folder is the world's clearing mask") && !cfg3.Contains("Lake Depth Scale"),
      "a world with only a mask exports only clearmask.bin, and says so");
    BC.Settings = new BC.BetterContinentsSettings();
  }
}

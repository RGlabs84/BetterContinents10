// Added by Wubarrk on 2026-10-26 for the BetterWorlds-complete export (0.10.7).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using BetterContinents;
using BC = BetterContinents.BetterContinents;

namespace ExportTest;

// The real export coroutine on a made-up Better Continents world that carries a cold map and a location exclusion mask of its own (the same way
// EndToEnd sets ForestMap): which of them reaches the folder, byte for byte or sampled, the settings version and world.json for a resized world,
// the cfg, README and manifest lines, and a replica of the rules BetterWorlds' importer applies to such a folder.
internal static class SourcesExport
{
  sealed class Result
  {
    public string Dir, Phase, Error;
    public List<string> Phases = [];
    public HashSet<string> Files = [];
    public string Cfg, Readme;
    public JsonDocument Manifest;
    public object Job;
    public float Cfgf(string section, string key) => new BepInEx.Configuration.ConfigFile(Path.Combine(Dir, "export.cfg"), false).Bind(section, key, -1f).Value;
  }

  static byte[] Png16(int n, ushort seed)
  {
    using var image = new Image<L16>(n, n);
    for (int y = 0; y < n; y++)
      for (int x = 0; x < n; x++)
        image[x, y] = new L16((ushort)(seed + 97 * x + 31 * y));
    using var stream = new MemoryStream();
    image.SaveAsPng(stream, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 });
    return stream.ToArray();
  }

  static BC.BetterContinentsSettings World(byte[] cold, float coldScale, byte[] mask)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, ColdMapScale = coldScale };
    void Field(string name, object value) => typeof(BC.BetterContinentsSettings).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, value);
    if (cold != null)
      Field("ColdMap", ImageMapFloat.Create(cold, false));
    if (mask != null)
      Field("LocationExclusionMap", ImageMapFloat.Create(mask, false));
    return s;
  }

  static WorldExport.Options Options(int size, Action<WorldExport.Options> tweak = null)
  {
    var o = WorldExport.Options.Default();
    o.Size = size;
    o.Heightmap = o.Biomes = o.Locations = o.Forest = o.Heat = o.AltBiomes = o.Lava = o.Moss = o.Paint = o.Preset = false;
    o.Sources = true;
    o.Cold = true;
    tweak?.Invoke(o);
    return o;
  }

  static Result Export(string work, string name, WorldExport.Options o, BC.BetterContinentsSettings settings, WorldGenerator wg, Action<Action<string, object>> jobFields = null)
  {
    EndToEnd.PatchOnce();
    BC.Settings = settings;
    var dir = Path.Combine(work, name);
    var job = EndToEnd.MakeJob(o, dir, wg);
    var jobType = job.GetType();
    void Set(string field, object value) => jobType.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(job, value);
    Set("BcWorld", true);
    Set("Settings", settings);
    jobFields?.Invoke(Set);
    EndToEnd.SetRunning(true);
    var drive = EndToEnd.Drive(job);
    var r = new Result { Dir = dir, Job = job };
    while (drive.MoveNext())
    {
      if (r.Phases.Count == 0 || r.Phases[^1] != WorldExport.Phase)
        r.Phases.Add(WorldExport.Phase);
      Thread.Sleep(1);
    }
    r.Phase = WorldExport.Phase;
    r.Error = WorldExport.LastError;
    if (Directory.Exists(dir))
      r.Files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToHashSet();
    if (r.Files.Contains("export.cfg")) r.Cfg = File.ReadAllText(Path.Combine(dir, "export.cfg"));
    if (r.Files.Contains("README.txt")) r.Readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
    if (r.Files.Contains("manifest.json")) r.Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
    return r;
  }

  public static void Run(string work)
  {
    System.Console.WriteLine("== a Better Continents world's own cold map and exclusion mask in the export");
    var world = new World { m_name = "Sources World", m_seedName = "SourcesSeed", m_seed = 11 };
    var wg = (WorldGenerator)RuntimeHelpers.GetUninitializedObject(typeof(WorldGenerator));
    typeof(WorldGenerator).GetField("m_world")!.SetValue(wg, world);
    var coldBytes = Png16(16, 300);
    var maskBytes = Png16(8, 5);
    var saved = BC.Settings;
    try
    {
      Own(work, wg, coldBytes, maskBytes);
      Sampled(work, wg, coldBytes, maskBytes);
      Resized(work, wg);
      LargeFile(work, wg);
    }
    finally
    {
      BC.Settings = saved;
    }
  }

  // ---- (i) the world's own cold map is copied, with its own scale; (iii) the mask is carried; nocold ------------------------------------------------

  static void Own(string work, WorldGenerator wg, byte[] coldBytes, byte[] maskBytes)
  {
    var r = Export(work, "src-own", Options(128), World(coldBytes, 7f, maskBytes), wg);
    Program.C(r.Phase == "Done" && r.Error == null, $"the export of a world with a cold map and a mask finishes ({r.Phase} {r.Error})");
    if (r.Phase != "Done")
      return;
    byte[] File_(string rel) => File.ReadAllBytes(Path.Combine(r.Dir, rel));
    Program.C(r.Files.Contains("coldmap.png") && File_("coldmap.png").SequenceEqual(coldBytes) && File_("sources/coldmap.png").SequenceEqual(coldBytes),
      "coldmap.png at the top of the folder is the world's own cold map, byte for byte, and so is the copy in sources/");
    Program.C(!r.Phases.Any(p => p.Contains("cold")) && !(bool)r.Job.GetType().GetField("ColdWritten", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Job), "nothing was sampled for it (no 'Sampling cold' phase)");
    Program.C(r.Cfgf("06 BetterContinents.Maps", "Coldmap Scale") == 7f, "export.cfg sets Coldmap Scale to the world's own 7");
    Program.C(r.Files.Contains("no_spawns_mask.png") && File_("no_spawns_mask.png").SequenceEqual(maskBytes) && File_("sources/no_spawns_mask.png").SequenceEqual(maskBytes),
      "no_spawns_mask.png is at the top of the folder and in sources/, byte for byte");
    Program.C(File.ReadAllText(Path.Combine(r.Dir, "sources/bc-settings.txt")).Contains("Coldmap scale 7") && File.ReadAllText(Path.Combine(r.Dir, "sources/bc-settings.txt")).Contains("Location exclusion mask"),
      "sources/bc-settings.txt keeps its 'Coldmap scale' line, and names the mask");
    Program.C(r.Readme.Contains("coldmap.png") && r.Readme.Contains("no_spawns_mask.png") && r.Readme.Contains("world.json") && r.Readme.Contains("bw_import"), "README.txt lists the cold map, the mask and world.json, and names BetterWorlds' way");
    var root = r.Manifest.RootElement;
    var files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
    Program.C(files.Contains("coldmap.png") && files.Contains("no_spawns_mask.png") && files.Contains("sources/no_spawns_mask.png") && files.Contains("world.json"), "manifest.json lists them among the files");
    var cold = root.GetProperty("cold");
    Program.C(cold.GetProperty("scale").GetSingle() == 7f && cold.GetProperty("source").GetString() == "the world's own map" && cold.GetProperty("deepNorthPixels").ValueKind == JsonValueKind.Null, "and says the cold map is the world's own, with its scale");
    Program.C(root.GetProperty("settingsVersion").GetInt32() == 12 && root.GetProperty("exportRevision").GetInt32() == 2 && root.GetProperty("layout").GetString() == "scaled" && root.GetProperty("fileBytes").GetProperty("coldmap.png").GetInt64() == coldBytes.Length,
      "settingsVersion 12, exportRevision 2, layout scaled, and the byte count of coldmap.png");
    Program.C(!r.Files.Any(f => f.EndsWith(".tmp")), "no .tmp file is left");
    BwReplica(r, "a world with its own cold map and mask");

    // nocold: sources/ as it was, nothing of the cold map at the top, no key, no manifest block.
    var n = Export(work, "src-nocold", Options(128, o => o.Cold = false), World(coldBytes, 7f, maskBytes), wg);
    Program.C(n.Phase == "Done" && !n.Files.Contains("coldmap.png") && n.Files.Contains("sources/coldmap.png") && File.ReadAllBytes(Path.Combine(n.Dir, "sources/coldmap.png")).SequenceEqual(coldBytes),
      $"with nocold there is no top-level coldmap.png, and sources/ still holds the world's own ({n.Phase} {n.Error})");
    Program.C(!n.Cfg.Contains("Coldmap Scale") && n.Manifest.RootElement.GetProperty("cold").ValueKind == JsonValueKind.Null && !n.Phases.Any(p => p.Contains("cold")), "and export.cfg has no Coldmap Scale, the manifest no cold block, no cold phase ran");
    Program.C(n.Files.Contains("no_spawns_mask.png") && n.Files.Contains("world.json"), "the mask and world.json do not depend on it");
  }

  // ---- (ii) sampled: a cold map that is not in use, Sources off, no cold map at all ------------------------------------------------------------------

  static void Sampled(string work, WorldGenerator wg, byte[] coldBytes, byte[] maskBytes)
  {
    foreach (var (name, settings, tweak, why) in new (string, BC.BetterContinentsSettings, Action<WorldExport.Options>, string)[]
    {
      ("src-scale0", World(coldBytes, 0f, maskBytes), null, "a cold map at Coldmap Scale 0 (not in use)"),
      ("src-nosources", World(coldBytes, 7f, maskBytes), o => o.Sources = false, "Sources off"),
      ("src-none", World(null, 10f, null), null, "a world without a cold map"),
    })
    {
      var r = Export(work, name, Options(128, tweak), settings, wg);
      Program.C(r.Phase == "Done" && r.Error == null && r.Phases.Any(p => p == "Sampling cold"), $"{why}: the cold map is sampled ({r.Phase} {r.Error})");
      if (r.Phase != "Done")
        continue;
      Program.C(r.Files.Contains("coldmap.png") && !File.ReadAllBytes(Path.Combine(r.Dir, "coldmap.png")).SequenceEqual(coldBytes), "and it is not the world's file");
      var bytes = File.ReadAllBytes(Path.Combine(r.Dir, "coldmap.png"));
      Program.C(bytes[24] == 16 && bytes[25] == 0, "it is 16-bit grey");
      Program.C(r.Cfgf("06 BetterContinents.Maps", "Coldmap Scale") == 10f, "export.cfg sets Coldmap Scale 10");
      var cold = r.Manifest.RootElement.GetProperty("cold");
      Program.C(cold.GetProperty("source").GetString() == "sampled" && cold.GetProperty("scale").GetSingle() == 10f && cold.GetProperty("deepNorthPixels").GetInt64() > 0, "the manifest says sampled, with the scale and the pixel count");
      var mask = r.Files.Contains("no_spawns_mask.png");
      Program.C(mask == (settings != null && name != "src-none" && name != "src-nosources"), $"the mask is at the top exactly when the world has one and Sources is on ({mask})");
      if (name == "src-none")
        BwReplica(r, why);
    }
  }

  // ---- (iv) a resized 0.9.x world: maps span 21000 m, World Size 20000 -------------------------------------------------------------------------------

  static void Resized(string work, WorldGenerator wg)
  {
    var r = Export(work, "src-resized", Options(128, o => o.Sources = false), World(null, 10f, null), wg, set =>
    {
      set("Total", 21000f);
      set("WorldSizeSetting", 20000f);
      set("EdgeSizeSetting", 500f);
      set("LocationsWritten", true);
    });
    Program.C(r.Phase == "Done" && r.Error == null, $"the export of a resized world finishes ({r.Phase} {r.Error})");
    if (r.Phase != "Done")
      return;
    var root = r.Manifest.RootElement;
    Program.C(root.GetProperty("settingsVersion").GetInt32() == 11 && root.GetProperty("layout").GetString() == "vanilla" && root.GetProperty("worldSize").GetSingle() == 20000f && root.GetProperty("edgeSize").GetSingle() == 500f,
      "maps spanning 21000 m with a World Size of 20000: settingsVersion 11, layout vanilla, worldSize 20000, edgeSize 500");
    var text = File.ReadAllText(Path.Combine(r.Dir, "world.json")).Trim();
    Program.C(text == "{ \"WorldRadius\": 20000, \"EdgeSize\": 500, \"Layout\": \"vanilla\" }", $"world.json is BetterWorlds' one line: {text}");
    Program.C(root.GetProperty("locations").GetProperty("pinGrid").GetString() == "Corner", "the location block names its pin grid (Corner, BetterWorlds' word for the i / size grid)");
    BwReplica(r, "a resized world");
    // The same size when the maps span what World Size and Edge Size give: the newest settings version, scaled.
    var r2 = Export(work, "src-sized", Options(128, o => o.Sources = false), World(null, 10f, null), wg, set =>
    {
      set("Total", 41000f);
      set("WorldSizeSetting", 20000f);
      set("EdgeSizeSetting", 500f);
    });
    var text2 = File.ReadAllText(Path.Combine(r2.Dir, "world.json")).Trim();
    Program.C(r2.Manifest.RootElement.GetProperty("settingsVersion").GetInt32() == 12 && text2 == "{ \"WorldRadius\": 20000, \"EdgeSize\": 500, \"Layout\": \"scaled\" }" && !text2.Contains("Sectors"),
      $"and a world whose maps span 41000 m is 12 and scaled, with no Sectors member: {text2}");
  }

  // ---- (vi) a map file past BetterWorlds' 256 MiB -------------------------------------------------------------------------------------------------

  static void LargeFile(string work, WorldGenerator wg)
  {
    // A sparse file on the real disk (not the tmpfs the work folder is on, where its pages would count as memory).
    var disk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "valheim-testbed");
    if (!Directory.Exists(disk))
    {
      System.Console.WriteLine("  SKIP the 256 MiB note (no ~/valheim-testbed to hold a sparse file)");
      return;
    }
    var dir = Path.Combine(disk, "bwx-sparse-" + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    try
    {
      using (var f = new FileStream(Path.Combine(dir, "huge.png"), FileMode.Create))
        f.SetLength(257L * 1024 * 1024);
      File.WriteAllBytes(Path.Combine(dir, "small.png"), [1, 2, 3]);
      BC.Settings = new BC.BetterContinentsSettings();
      var job = EndToEnd.MakeJob(WorldExport.Options.Default(), dir, wg);
      var type = job.GetType();
      ((List<string>)type.GetField("Written", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!).AddRange(["huge.png", "small.png", "sources/huge.png"]);
      type.GetMethod("NoteLargeFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null);
      var notes = (List<string>)type.GetField("Notes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!;
      Program.C(notes.Count == 1 && notes[0].StartsWith("huge.png is 257 MiB") && notes[0].Contains("256 MiB"), $"a map file over 256 MiB gets a note, a small one none ({string.Join(" | ", notes)})");
    }
    finally
    {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  // ---- a replica of what BetterWorlds' ProjectImporter decides about a folder (its working tree of 2026-10-26: DetectUncached 186-223, ReadFolder 389-454,
  // AddSourcesColdMap 724-740, DeriveWorldSize 959-977). A drift guard: when BetterWorlds changes these, the test above still states the contract. ----------

  static void BwReplica(Result r, string what)
  {
    var dir = r.Dir;
    int Detect()
    {
      // canonical JSON of BetterWorlds (any of its own names) => 13; none is written here.
      var manifest = Path.Combine(dir, "manifest.json");
      if (File.Exists(manifest))
      {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        if (doc.RootElement.TryGetProperty("settingsVersion", out var sv) && sv.TryGetInt32(out var v) && v >= 11)
          return v >= 13 ? 13 : v;
      }
      if (File.Exists(Path.Combine(dir, "placements.bcp")))
        return 12;
      return 11;
    }
    var cfgs = Directory.GetFiles(dir, "*.cfg");
    Program.C(cfgs.Length == 1 && Path.GetFileName(cfgs[0]) == "export.cfg", $"[{what}] BetterWorlds: export.cfg is the only *.cfg, so it is the one it takes");
    Program.C(r.Cfg.StartsWith("## Better Continents world export of \""), $"[{what}] BetterWorlds: the cfg's first line still marks it as an export, not a raw config");
    var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "world.json"))).RootElement;
    bool hasWorld = json.TryGetProperty("WorldRadius", out _) && json.TryGetProperty("EdgeSize", out _) && json.TryGetProperty("Layout", out _) && !json.TryGetProperty("Sectors", out _);
    Program.C(hasWorld, $"[{what}] BetterWorlds: world.json is taken as it is, so no size is derived from the cfg and the layout is not guessed");
    Program.C(Detect() == r.Manifest.RootElement.GetProperty("settingsVersion").GetInt32() && Detect() >= 11, $"[{what}] BetterWorlds detects settings version {Detect()} from the manifest, not by its heuristics");
    var forbidden = new[] { "altbiomes.json", "better_locations_config.json", "weather.json", "world_layers.json", "heightmap.cfg" };
    Program.C(!forbidden.Any(f => File.Exists(Path.Combine(dir, f))), $"[{what}] BetterWorlds: no canonical JSON of its own is written (it would be copied over what it derives from export.cfg)");
    foreach (var png in new[] { "coldmap.png", "no_spawns_mask.png" }.Where(f => File.Exists(Path.Combine(dir, f))))
    {
      var bytes = File.ReadAllBytes(Path.Combine(dir, png));
      // PNG colour type 0 (grey), depth 8 or 16: the role BetterWorlds' MapFiles table accepts for a cold map.
      Program.C(bytes.Length > 26 && bytes[25] == 0 && (bytes[24] == 8 || bytes[24] == 16), $"[{what}] BetterWorlds: {png} is a grey 8 or 16 bit PNG");
    }
    if (File.Exists(Path.Combine(dir, "coldmap.png")))
      Program.C(r.Cfg.Contains("Coldmap Scale = "), $"[{what}] BetterWorlds: the top-level cold map comes with its Coldmap Scale (AddSourcesColdMap reads sources/ only when the top level has none)");
  }
}

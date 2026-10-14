// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
//
// bc_lake (BUILD-SPEC-TARN.md 2.7): what a line means (LakeCommandLine), who may run it, and every word on made-up worlds with the game's side stood in for
// (LakeCommands.Seams): info, list, mask at, depth, remove (with its backup), load, import, export, mask load and export, regen, here and at (with the lake engine's
// spill stood in for), server, a vanilla world and its conversion, Tarn being installed; and the push of a change to the players (LakeTransfer) over real ZRpc objects on
// made-up sockets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  // ---- the line -----------------------------------------------------------------------------------------------------------------------------------

  private static LakeCommand LakeParse(string line) => LakeCommandLine.Parse(line);

  private static void LakeCommandLineTest()
  {
    Section("commands: what a line means");
    foreach (var empty in new[] { "", "help", "  ?  ", "HELP" })
      C(LakeParse(empty).Verb == LakeVerb.Help && LakeParse(empty).Error == null, $"'{empty}' is the help");
    C(LakeParse("help me").Error != null, "'help' takes no more words");
    foreach (var (word, verb) in new[] { ("info", LakeVerb.Info), ("regen", LakeVerb.Regen) })
      C(LakeParse(word).Verb == verb && LakeParse(word.ToUpperInvariant()).Verb == verb && LakeParse(word + " now").Error != null, $"'{word}' is {verb}, in any case, and takes no more words");
    C(LakeParse("info confirm").Error != null && LakeParse("regen confirm").Confirm && !LakeParse("regen").Confirm && LakeParse("regen convert").Error != null, "info takes no confirm; regen takes confirm only");
    C(LakeParse("list").Count == 10 && LakeParse("list 25").Count == 25 && LakeParse("list 0").Error != null && LakeParse("list x").Error != null && LakeParse("list 5 6").Error != null && LakeParse("list 5 confirm").Error != null, "list [n]: ten by default, 1 to 1000");
    var here = LakeParse("here");
    C(here.Verb == LakeVerb.Here && here.Radius == 120f && here.Margin == 0.3f && here.X == null, "here: Tarn's defaults, a radius of 120 m and a margin of 0.3 m [T: Plugin.cs]");
    var here2 = LakeParse("here 80 0.5");
    C(here2.Radius == 80f && here2.Margin == 0.5f && LakeParse("here 80").Radius == 80f && LakeParse("here 80").Margin == 0.3f, "here [radius] [margin]");
    C(LakeParse("here 3").Error != null && LakeParse("here 1001").Error != null && LakeParse("here 80 -1").Error != null && LakeParse("here 80 21").Error != null && LakeParse("here x").Error != null && LakeParse("here 1 2 3").Error != null && LakeParse("here confirm").Error != null,
      "a radius under 4 or over 1000, a margin outside 0 to 20, a word, a third number, confirm: refused");
    var at = LakeParse("at -4210.5 3190.25 200 0.2");
    C(at.Verb == LakeVerb.At && at.X == -4210.5f && at.Z == 3190.25f && at.Radius == 200f && at.Margin == 0.2f && LakeParse("at 1 2").Radius == 120f, "at <x> <z> [radius] [margin]");
    C(LakeParse("at").Error != null && LakeParse("at 1").Error != null && LakeParse("at x 2").Error != null && LakeParse("at 1 2 3 4 5").Error != null && LakeParse("at 1e999 2").Error != null, "at needs two numbers (a number that overflows is not one)");
    var remove = LakeParse("remove lake1 ä ö confirm");
    C(remove.Verb == LakeVerb.Remove && remove.Text == "lake1 ä ö" && remove.Confirm && LakeParse("remove all").Text == "all" && !LakeParse("remove all").Confirm && LakeParse("remove").Error != null && LakeParse("remove x convert").Error != null,
      "remove <name|all> [confirm]: a name keeps its spaces");
    C(LakeParse("depth").Scale == null && LakeParse("depth 0.8").Scale == 0.8f && LakeParse("depth 3").Scale == 3f && LakeParse("depth 0").Scale == 0f && LakeParse("depth 3.1").Error != null && LakeParse("depth -1").Error != null && LakeParse("depth x").Error != null && LakeParse("depth 1 2").Error != null,
      "depth [scale]: 0 to 3");
    var load = LakeParse("load C:\\maps\\My Town confirm convert");
    C(load.Verb == LakeVerb.Load && load.Text == "C:\\maps\\My Town" && load.Confirm && load.Convert, "load takes a folder with spaces as typed, and the words 'confirm' and 'convert' wherever they stand");
    C(LakeParse("load").Text == null && !LakeParse("load").Confirm && LakeParse("import").Verb == LakeVerb.Import && LakeParse("import /a b convert").Convert && LakeParse("import /a b convert").Text == "/a b", "load and import: the folder is optional");
    C(LakeParse("export /tmp/a b").Text == "/tmp/a b" && LakeParse("export").Text == null && LakeParse("export x confirm").Confirm && LakeParse("export x confirm").Text == "x" && !LakeParse("export x").Confirm && LakeParse("export x convert").Error != null, "export [folder] [confirm]");
    C(LakeParse("mask at 3 4").Verb == LakeVerb.MaskAt && LakeParse("mask at 3 4").X == 3f && LakeParse("mask at 3").Error != null && LakeParse("mask at 3 x").Error != null, "mask at <x> <z>");
    var maskLoad = LakeParse("mask load /tmp/a b.bin confirm");
    C(maskLoad.Verb == LakeVerb.MaskLoad && maskLoad.Text == "/tmp/a b.bin" && maskLoad.Confirm && LakeParse("mask load").Error != null && LakeParse("mask export /x").Verb == LakeVerb.MaskExport && LakeParse("mask export /x confirm").Confirm && LakeParse("mask export /x convert").Error != null && LakeParse("mask export").Error != null && LakeParse("mask").Error != null && LakeParse("mask frob").Error != null, "mask load <file> [confirm] | mask export <file>");
    var server = LakeParse("server remove all confirm");
    C(server.Verb == LakeVerb.Server && server.Text == "remove all confirm" && LakeParse("server").Error != null && LakeParse("server server info").Error != null && LakeParse("server frobnicate").Error != null && LakeParse("server list 0").Error != null, "'server' hands the rest on, and says now what the server would refuse");
    C(LakeParse("frobnicate").Error!.Contains("'frobnicate' is not understood") && LakeParse("tarn here").Error != null, "a word it does not know is said");
    C(LakeCommandLine.Usage.Contains("here [radius] [margin]") && LakeCommandLine.Usage.Contains("mask load <file>") && LakeCommandLine.Usage.Contains("remove <name|all>"), "the usage lists the words");

    Section("commands: who may run what");
    foreach (var verb in new[] { LakeVerb.Help, LakeVerb.Info, LakeVerb.Server })
      C(LakeCommandLine.Refusal(verb, false, false) == null && LakeCommandLine.Refusal(verb, false, true) == null, $"{verb} runs on any machine, even in the main menu");
    foreach (var verb in new[] { LakeVerb.List, LakeVerb.MaskAt, LakeVerb.Export, LakeVerb.MaskExport })
      C(LakeCommandLine.Refusal(verb, false, true) == null && LakeCommandLine.Refusal(verb, true, true) == null && LakeCommandLine.Refusal(verb, true, false) != null, $"{verb} runs on a client too, with a world loaded");
    foreach (var verb in new[] { LakeVerb.Here, LakeVerb.At, LakeVerb.Remove, LakeVerb.Depth, LakeVerb.Load, LakeVerb.Import, LakeVerb.MaskLoad, LakeVerb.Regen })
    {
      C(LakeCommandLine.Refusal(verb, true, true) == null, $"{verb} runs where the world is");
      C((LakeCommandLine.Refusal(verb, false, true) ?? "").Contains("bc_lake server"), $"{verb} on a client points to 'bc_lake server'");
      C((LakeCommandLine.Refusal(verb, true, false) ?? "").Contains("load a world first") && LakeCommandLine.Changes(verb), $"{verb} needs a world, and is a word that changes it");
    }
    C(!LakeCommandLine.Changes(LakeVerb.Info) && !LakeCommandLine.Changes(LakeVerb.Export) && !LakeCommandLine.Changes(LakeVerb.List), "info, list and export change nothing");
  }

  // ---- the world the commands run on ----------------------------------------------------------------------------------------------------------------

  private static Lake Lk(string name, float x, float z, float level = 60f) => new(name, x, z, level, x - 50f, z - 50f, x + 50f, z + 50f, 8f);

  private sealed class StubLink : BakedTransfer.IBakedLink
  {
    public bool Connected { get; set; } = true;
    public readonly List<(string Method, object[] Parameters)> Sent = [];
    public int SendQueueSize => 0;
    public float Now => 0f;
    public void Invoke(string method, params object[] parameters) => Sent.Add((method, parameters));
    public void Flush() { }
    public bool RaiseSendRate(string what, int length) => false;
    public void RestoreSendRate(string why) { }
  }

  // The game's side for bc_lake, stood in for, and put back.
  private sealed class CmdWorld : IDisposable
  {
    public readonly List<string> Said = [];
    public readonly string Folder;
    public readonly string Save;
    public readonly List<string> Remote = [], Local = [];
    public readonly List<(LakeSet Lakes, ClearMask Mask)> Converted = [];
    // What the conversion's broadcast answers: the number of players sent the settings, or -1 for a package too large for one message.
    public int ConvertSent = 1;
    public bool InsideSaves;
    public readonly List<LakeSet> Changed = [];
    public int ClearingPatched, Regenerated;
    public Func<float, float, float, float, LakeCommands.HereOutcome> Spill = (x, z, r, m) => LakeCommands.HereOutcome.NotAvailable;
    public List<(int X, int Z)> Zones = [];
    public readonly StubLink Joined = new(), Joining = new(), Left = new() { Connected = false };
    private readonly Dictionary<FieldInfo, object> saved = [];
    private readonly BC.BetterContinentsSettings settings = BC.Settings;
    private readonly List<BakedTransfer.LayerClient> clients = BakedTransfer.Clients.ToList();
    private static int serial;

    public CmdWorld(BC.BetterContinentsSettings world)
    {
      Folder = NewDir("cmd-" + ++serial);
      Save = Path.Combine(Folder, "save");
      foreach (var f in SeamFields())
        saved[f] = f.GetValue(null);
      BC.Settings = world;
      LakeCommands.Seams.InWorld = () => true;
      LakeCommands.Seams.RunsWorld = () => true;
      LakeCommands.Seams.IsServerMachine = () => true;
      LakeCommands.Seams.WorldName = () => "Raventest";
      LakeCommands.Seams.SaveFolder = () => Save;
      LakeCommands.Seams.MapDirectory = () => "";
      LakeCommands.Seams.Now = () => new DateTime(2026, 10, 13, 12, 30, 5);
      LakeCommands.Seams.CallerPlace = () => (0f, 0f);
      LakeCommands.Seams.Kind = () => BakeWorldKind.BetterContinents;
      LakeCommands.Seams.Peers = () => [];
      LakeCommands.Seams.Convert = (lakes, mask) =>
      {
        Converted.Add((lakes, mask));
        return ConvertSent;
      };
      LakeCommands.Seams.InsideWorldSaves = _ => InsideSaves;
      LakeCommands.Seams.PatchClearing = () => ClearingPatched++;
      LakeCommands.Seams.GeneratedZones = () => Zones;
      LakeCommands.Seams.Regenerate = () => Regenerated++;
      LakeCommands.Seams.ExportRefusal = () => null;
      LakeCommands.Seams.Deep = (x, z) => false;
      LakeCommands.Seams.Hot = (x, z) => false;
      LakeCommands.Seams.RunOnThisMachine = Local.Add;
      LakeCommands.Seams.RemoteCommand = Remote.Add;
      LakeCommands.Seams.Spill = (x, z, r, m) => Spill(x, z, r, m);
      LakeCommands.Seams.BuiltState = _ => null;
      LakeCommands.Seams.EngineInfo = null;
      LakeTransfer.LakesChanged = set => Changed.Add(set);
      TarnCompat.Installed = false;
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("alice", Joined, _ => { }) { JoinDone = true });
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("bob", Joining, _ => { }) { JoinDone = false });
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("carol", Left, _ => { }) { JoinDone = true });
    }

    private static IEnumerable<FieldInfo> SeamFields() =>
      typeof(LakeCommands).GetNestedType("Seams", BindingFlags.NonPublic | BindingFlags.Public)!.GetFields(BindingFlags.Public | BindingFlags.Static);

    public string Run(string line)
    {
      Said.Clear();
      LakeCommands.RunLine(line, Said.Add);
      return string.Join("\n", Said);
    }

    public bool Says(string part) => Said.Any(l => l.Contains(part, StringComparison.Ordinal));

    public int Pushes => Joined.Sent.Count(s => s.Method == LakeTransfer.RpcLakes);

    public void Dispose()
    {
      foreach (var (f, v) in saved)
        f.SetValue(null, v);
      BC.Settings = settings;
      LakeTransfer.LakesChanged = null;
      TarnCompat.Installed = false;
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.AddRange(clients);
    }
  }

  private static BC.BetterContinentsSettings CmdSettings(bool lakes = true, bool mask = true, bool gameTerrain = false)
  {
    var l = lakes ? new LakeSet([Lk("near", 100f, 100f), Lk("far", 3000f, -200f), Lk("lake3", -800f, 40f), Lk("Big Lake ä", 500f, 500f)], 0.5f, 0) : null;
    return World(l, mask ? SampleMask() : null, gameTerrain);
  }

  private static void LakeInfoTest()
  {
    Section("commands: info, list and mask at");
    using var w = new CmdWorld(CmdSettings());
    var info = w.Run("info");
    C(info.Contains("4 lakes, depth scale 0.5; nearest near (71 m), Big Lake ä (636 m)") && info.Contains("clearing mask 37 x 37 over +-124 m (6.68 m a pixel)"), "info: the lakes, the nearest, the mask: " + info.Replace("\n", " | "));
    C(info.Contains("km2 kept free of vegetation"), "on the machine that runs the world the mask's bits are opened for its area");
    var list = w.Run("list 3");
    C(w.Said.Count == 4 && w.Said[0].Contains("4 lakes, the 3 nearest") && w.Said[1].StartsWith("near: (100, 100) level 60.0, 71 m away") && w.Said[2].StartsWith("Big Lake ä:") && w.Said[3].StartsWith("lake3:"), "list n: the n nearest, with their distances: " + list.Replace("\n", " | "));
    LakeCommands.Seams.BuiltState = l => l.Name == "near" ? "built" : "not built";
    C(w.Run("list 1").Contains("71 m away, built"), "and whether each is built, once the lake engine says");
    LakeCommands.Seams.EngineInfo = () => ["bc_water: the engine's own line"];
    C(w.Run("info").Contains("the engine's own line"), "info ends with the lake engine's lines when there are some");
    LakeCommands.Seams.CallerPlace = () => null;
    w.Run("list");
    C(w.Said[0].Contains("the 4 first") && w.Said[1].StartsWith("near:") && !w.Said[1].Contains("m away"), "without a place to measure from (a server's own console) it lists in order without distances");
    // mask at
    LakeCommands.Seams.CallerPlace = () => (0f, 0f);
    var mask = SampleMask();
    var c = mask.CentreOf(0, 0);
    C(w.Run($"mask at {c.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {c.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}").Contains("is cleared") && w.Says("pixel row 0, column 0, marked"), "mask at a marked pixel: cleared, with the pixel");
    var free = mask.CentreOf(1, 36);
    C(w.Run($"mask at {free.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {free.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}").Contains("not cleared") && w.Says("not marked"), "and at one that is not: not cleared");
    C(w.Run("mask at 5000 5000").Contains("outside the mask"), "outside the mask");
    // Worlds without them.
    using var none = new CmdWorld(CmdSettings(false, false));
    C(none.Run("info").Contains("no lakes") && none.Says("no clearing mask") && none.Run("list").Contains("has no lakes") && none.Run("mask at 0 0").Contains("no clearing mask"), "a world without them says so");
    using var vanilla = new CmdWorld(new BC.BetterContinentsSettings { EnabledForThisWorld = false });
    C(vanilla.Run("info").Contains("not a Better Continents world"), "a vanilla world says how to get lakes");
    // An unreadable one.
    var broken = CmdSettings(false, false);
    broken.LakesError = "format 9 is newer";
    broken.ClearingError = "format 9 is newer";
    using var unreadable = new CmdWorld(broken);
    C(unreadable.Run("info").Contains("lakes in this world's settings cannot be read") && unreadable.Says("clearing mask in this world's settings cannot be read"), "lakes and a mask this build cannot read are said, and kept");
    // Hot and Deep.
    using var hot = new CmdWorld(CmdSettings());
    LakeCommands.Seams.Hot = (x, z) => x < 0;
    LakeCommands.Seams.Deep = (x, z) => x > 2000;
    C(hot.Run("info").Contains("2 of them lie in the world's hot sea"), "info says how many lakes lie in the world's hot sea or Deep North: " + hot.Said.FirstOrDefault(l => l.Contains("lie in")));
  }

  private static void LakeChangeTest()
  {
    Section("commands: depth, remove, here and at");
    using var w = new CmdWorld(CmdSettings());
    C(w.Run("depth").Contains("0.5 of their real depth"), "depth alone says the scale");
    var before = BC.Settings.Lakes;
    C(w.Run("depth 0.8").Contains("depth scale 0.8, saved with the world at its next save; sent to 1 player.") && BC.Settings.Lakes != before && BC.Settings.Lakes.DepthScale == 0.8f && BC.Settings.Lakes.Lakes.Count == 4 && BC.Settings.EffectiveLakeDepthScale == 0.8f, "depth <scale> swaps the set (the lakes are the same), tells the players: " + w.Said.FirstOrDefault());
    C(w.Pushes == 1 && w.Joining.Sent.Count == 0 && w.Left.Sent.Count == 0 && w.Changed.Count == 1 && ReferenceEquals(w.Changed[0], BC.Settings.Lakes), "one push, to the player whose join is over and who is connected; the lake engine is told");
    var pushed = LakeTransfer.Unpack(new ZPackage(((ZPackage)w.Joined.Sent[0].Parameters[0]).GetArray()));
    C(pushed.DepthScale == 0.8f && pushed.Lakes.SequenceEqual(BC.Settings.Lakes.Lakes) && pushed.Revision == BC.Settings.Lakes.Revision, "and what was sent is the new set");
    // A world with no lakes keeps the scale as an empty set.
    using (var none = new CmdWorld(CmdSettings(false, false)))
    {
      none.Run("depth 1.5");
      C(BC.Settings.Lakes != null && BC.Settings.Lakes.IsEmpty && BC.Settings.Lakes.DepthScale == 1.5f && !BC.Settings.HasLakes, "a world with no lakes keeps the depth scale as an empty set");
    }

    // remove one
    var old = BC.Settings.Lakes;
    C(w.Run("remove far").Contains("removed 1 lake; 3 left"), "remove <name>: " + w.Said.FirstOrDefault());
    C(BC.Settings.Lakes.Lakes.Select(l => l.Name).SequenceEqual(["near", "lake3", "Big Lake ä"]), "the others stay, in order");
    var backup = Path.Combine(w.Save, "lakes-before-2026-10-13-123005-000.txt");
    C(File.Exists(backup) && LakeSet.ParseLines(File.ReadAllLines(backup), _ => { }).Lakes.SequenceEqual(old.Lakes), "the old list was first written to lakes-before-<time>.txt (to the millisecond) in the save folder, in Tarn's format");
    C(w.Run("remove nothing").Contains("no lake is named 'nothing'") && w.Run("remove Big Lake ä").Contains("removed 1 lake; 2 left"), "an unknown name is said; a name with spaces and accents works");
    // remove all: a dry run
    var beforeAll = BC.Settings.Lakes;
    C(w.Run("remove all").Contains("a dry run") && ReferenceEquals(BC.Settings.Lakes, beforeAll), "remove all is a dry run");
    C(w.Run("remove all confirm").Contains("removed 2 lakes; 0 left") && BC.Settings.Lakes != null && BC.Settings.Lakes.IsEmpty && BC.Settings.Lakes.DepthScale == 0.8f, "'confirm' removes them all and keeps an empty set (so Tarn's files are not imported again)");
    C(w.Run("remove all confirm").Contains("has no lakes") && w.Run("list").Contains("no lakes"), "no lakes: nothing to remove");
    // here, at: the lake engine's spill is a placeholder until merged
    C(w.Run("here").Contains("bc_lake here: not available yet"), "here says 'not available yet' while the lake engine's spill is not in the build: " + w.Said.FirstOrDefault());
    C(w.Run("at 10 20 80").Contains("bc_lake at: not available yet"), "so does at");
    // With a spill that finds a lake.
    var asked = new List<(float, float, float, float)>();
    w.Spill = (x, z, r, m) =>
    {
      asked.Add((x, z, r, m));
      return new LakeCommands.HereOutcome(new Lake("ignored", x, z, 77.5f, x - 30f, z - 30f, x + 30f, z + 30f, 6.2f), "level 77.50 m (spills at 77.80), deepest 6.2 m, 2400 m2 wet");
    };
    LakeCommands.Seams.CallerPlace = () => (123f, -45f);
    var pushes = w.Pushes;
    var text = w.Run("here 90 0.4");
    C(asked.Count == 1 && asked[0] == (123f, -45f, 90f, 0.4f), "here asks the spill about the place of the one who asks, with the radius and the margin");
    C(text.Contains("lake1: level 77.50 m") && text.Contains("Saved with the world at its next save; sent to 1 player.") && BC.Settings.Lakes.Lakes.Single().Name == "lake1" && BC.Settings.Lakes.DepthScale == 0.8f && w.Pushes == pushes + 1, "the lake is named, added, kept and sent: " + text.Replace("\n", " | "));
    w.Run("at 500 600");
    C(asked[1] == (500f, 600f, 120f, 0.3f) && BC.Settings.Lakes.Lakes.Select(l => l.Name).SequenceEqual(["lake1", "lake2"]), "at <x> <z> uses that place and Tarn's defaults; the next lake is lake2");
    // Guards.
    LakeCommands.Seams.Hot = (x, z) => true;
    C(w.Run("here").Contains("this lake is inside the world's hot sea"), "a lake in the hot sea is said");
    LakeCommands.Seams.Hot = (x, z) => false;
    LakeCommands.Seams.Deep = (x, z) => true;
    C(w.Run("here").Contains("this lake is inside the world's Deep North"), "and one in the Deep North");
    // no caller
    LakeCommands.Seams.CallerPlace = () => null;
    C(w.Run("here").Contains("no one to take the place from"), "here at a console with no one in the world needs 'at'");
    // The limit.
    BC.Settings.ReplaceLakes(new LakeSet(Enumerable.Range(0, LakeSet.MaxLakes).Select(i => Lk("l" + i, 0, 0)).ToList(), 0.5f, 0));
    LakeCommands.Seams.CallerPlace = () => (0f, 0f);
    C(w.Run("here").Contains("at most 5,000 lakes"), "a world holds at most 5,000 lakes");

    // A spill that refuses says why.
    using var refused = new CmdWorld(CmdSettings(false, false));
    refused.Spill = (x, z, r, m) => new LakeCommands.HereOutcome(null, "this hollow drains to the sea: it spills at 31.0 m, the sea is 30.0 m");
    C(refused.Run("here").Contains("bc_lake here: this hollow drains to the sea") && BC.Settings.Lakes == null, "a refusal of the spill is said, and nothing is added");
  }

  private static void LakeLoadTest()
  {
    Section("commands: load, import, mask load and export, export");
    var dir = NewDir("load-folder");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(dir, "lakes.txt"));
    File.Copy(Fixture("clear64.bin"), Path.Combine(dir, "clearmask.bin"));
    using var w = new CmdWorld(CmdSettings(true, false));
    LakeCommands.Seams.MapDirectory = () => dir;
    var oldLakes = BC.Settings.Lakes;
    // Dry run.
    var dry = w.Run("load");
    C(dry.Contains("a dry run, nothing changes") && dry.Contains("5 lakes (lakes.txt) and a clearing mask of 64 x 64 over +-32 m") && dry.Contains("the world has 4 lakes and no mask") && dry.Contains("add 'confirm'") && dry.Contains("lakes-before-<time>.txt"), "load: a dry run says what the files hold and what the world has: " + dry.Replace("\n", " | "));
    C(ReferenceEquals(BC.Settings.Lakes, oldLakes) && BC.Settings.Clearing == null && !Directory.Exists(w.Save), "and changes nothing, writes nothing");
    var lines = w.Said.Count(l => l.Contains("left out"));
    C(lines == 2 && w.Said.Any(l => l.Contains("lakes.txt: line 6:")) && w.Said.Any(l => l.Contains("lakes.txt: line 8:")), "and it shows the two lines the parser left out (6 and 8), which the log has too");
    // Confirm.
    var text = w.Run("load confirm");
    C(text.Contains("loaded 5 lakes (lakes.txt) and a clearing mask of 64 x 64") && text.Contains("the lakes were sent to 1 player") && text.Contains("the mask is not sent to players"), "load confirm: " + text.Replace("\n", " | "));
    C(BC.Settings.Lakes.Lakes.Count == 5 && BC.Settings.Lakes.DepthScale == 0.5f && BC.Settings.Clearing is { Size: 64 } && w.ClearingPatched == 1 && w.Changed.Count == 1 && w.Pushes == 1, "the world has them, the clearing patch was switched, the lake engine told, the lakes pushed once");
    var backup = Path.Combine(w.Save, "lakes-before-2026-10-13-123005-000.txt");
    C(File.Exists(backup) && LakeSet.ParseLines(File.ReadAllLines(backup), _ => { }).Lakes.SequenceEqual(oldLakes.Lakes) && !Directory.GetFiles(w.Save).Any(f => f.Contains("clearmask-before")), "the old lakes were written first (there was no mask to write)");
    // Load again over a mask: it is written first.
    w.Run("load confirm");
    C(Directory.GetFiles(w.Save).Any(f => Path.GetFileName(f) == "clearmask-before-2026-10-13-123005-000.bin"), "the old mask is written before a new one replaces it");
    // Only a lakes file or only a mask in the folder.
    var onlyMask = NewDir("load-only-mask");
    File.Copy(Fixture("clear37.bin"), Path.Combine(onlyMask, "tarn-clear.bin"));
    var lakesBefore = BC.Settings.Lakes;
    C(w.Run($"load {onlyMask} confirm").Contains("loaded a clearing mask of 37 x 37") && ReferenceEquals(BC.Settings.Lakes, lakesBefore) && BC.Settings.Clearing.Size == 37, "a folder with only a mask (the pipeline's name) replaces only the mask");
    // Nothing there / not a folder / a problem file.
    C(w.Run($"load {NewDir("load-empty")}").Contains("nothing to load") && w.Run("load /nowhere/at/all").Contains("is not a folder"), "an empty folder, and a folder that is not there, are said");
    LakeCommands.Seams.MapDirectory = () => "";
    C(w.Run("load").Contains("name a folder"), "no folder and no Directory: said");
    var bad = NewDir("load-bad");
    File.WriteAllBytes(Path.Combine(bad, "clearmask.bin"), new byte[40]);
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(bad, "lakes.txt"));
    var badText = w.Run($"load {bad} confirm");
    C(badText.Contains("clearmask.bin cannot be read") && badText.Contains("left out") && badText.Contains("loaded 5 lakes (lakes.txt)") && BC.Settings.Clearing.Size == 37, "a mask that does not read is said and left out; the lakes are still loaded");

    // Warnings about the hot sea.
    LakeCommands.Seams.Hot = (x, z) => x < 0;
    C(w.Run($"load {dir}").Contains("of these lakes, ") && w.Said.Any(l => l.Contains("inside the world's hot sea")), "a dry run warns of lakes in the hot sea");
    LakeCommands.Seams.Hot = (x, z) => false;

    // import: Tarn's own folder.
    var config = NewDir("import-config");
    var tarn = Path.Combine(config, "Tarn");
    Directory.CreateDirectory(tarn);
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(tarn, "Raventest.txt"));
    File.Copy(Fixture("clear8.bin"), Path.Combine(tarn, "Raventest.clear"));
    File.WriteAllText(Path.Combine(config, "com.raveniron.tarn.cfg"), "[Look]\nDepthScale = 0.8\n");
    var oldConfig = LakeFiles.ConfigPath;
    LakeFiles.ConfigPath = () => config;
    try
    {
      var importDry = w.Run("import");
      C(importDry.Contains("bc_lake import: a dry run") && importDry.Contains("5 lakes (Raventest.txt)") && importDry.Contains("a clearing mask of 8 x 8"), "import: a dry run reads Tarn's <world>.txt and .clear: " + importDry.Replace("\n", " | "));
      w.Run("import confirm");
      C(BC.Settings.Lakes.Lakes.Count == 5 && BC.Settings.Lakes.DepthScale == 0.5f && BC.Settings.Clearing.Size == 8, "imported; the world keeps its own depth scale (0.5), not Tarn's (0.8): see the fixes tests for a world with none");
      File.Delete(Path.Combine(tarn, "Raventest.txt"));
      File.Delete(Path.Combine(tarn, "Raventest.clear"));
      C(w.Run("import").Contains("holds no Raventest.txt or Raventest.clear"), "Tarn's files named for another world are said not to be there");
      C(w.Run($"import {NewDir("no-tarn")}").Contains("holds no Raventest.txt"), "and an empty folder");
    }
    finally
    {
      LakeFiles.ConfigPath = oldConfig;
    }

    // mask load / export, export
    var export = NewDir("export-out");
    var maskFile = Path.Combine(export, "mask.bin");
    C(w.Run($"mask export {maskFile}").Contains("wrote the clearing mask (8 x 8)") && File.ReadAllBytes(maskFile).SequenceEqual(File.ReadAllBytes(Fixture("clear8.bin"))), "mask export writes Tarn's .clear file byte for byte");
    C(w.Run($"mask load {Path.Combine(dir, "clearmask.bin")}").Contains("a dry run") && BC.Settings.Clearing.Size == 8, "mask load is a dry run until 'confirm'");
    C(w.Run($"mask load {Path.Combine(dir, "clearmask.bin")} confirm").Contains("loaded a clearing mask of 64 x 64") && BC.Settings.Clearing.Size == 64 && w.Said.Any(l => l.Contains("not sent to players")), "mask load confirm replaces the mask and says it is not sent to players");
    C(w.Run($"mask load {Path.Combine(export, "nope.bin")}").Contains("does not exist") && w.Run($"mask load {export}").Contains("does not exist"), "a file that is not there, a folder without a mask: said");
    var exportDir = Path.Combine(export, "lakes-out");
    C(w.Run($"export {exportDir}").Contains("wrote lakes.txt (5 lakes) and clearmask.bin (64 x 64)"), "export writes both files");
    C(LakeSet.ParseLines(File.ReadAllLines(Path.Combine(exportDir, "lakes.txt")), _ => { }).Lakes.SequenceEqual(BC.Settings.Lakes.Lakes) && File.ReadAllBytes(Path.Combine(exportDir, "clearmask.bin")).SequenceEqual(BC.Settings.Clearing.ToTarnFile()), "the world's lakes to the bit and the mask to the byte");
    w.Run("export");
    C(Directory.GetDirectories(w.Save).Any(d => Path.GetFileName(d) == "lakes-2026-10-13-123005-000"), "no folder: lakes-<time> in the world's save folder");
    LakeCommands.Seams.ExportRefusal = () => "Allow Export is off on this server";
    C(w.Run("export").Contains("Allow Export is off") && w.Run("mask export x").Contains("Allow Export is off"), "export follows the server's Allow Export");
  }

  private static void LakeRegenTest()
  {
    Section("commands: regen");
    // The count, on its own: zones whose 64 m square a lake's box (plus the builder's 12 m) or a marked pixel's clear area touches.
    var lakes = new LakeSet([new Lake("a", 0, 0, 60, -40, -40, 40, 40, 5)], 0.5f, 0);
    var generated = new List<(int, int)> { (0, 0), (1, 0), (-1, 0), (2, 0), (0, 2), (10, 10), (-1, -1) };
    var c = LakeRegen.Touched(generated, lakes, null);
    // The box with 12 m: -52..52 -> zones -1..1 on each axis [the zone k spans 64k-32 .. 64k+32].
    C(c.Generated == 7 && c.ByLakes == 4 && c.ByMask == 0 && c.Either == 4, $"a lake's box and 12 m touch zones -1..1: (0,0), (1,0), (-1,0), (-1,-1) of the 7 generated ({c.ByLakes})");
    var hugeBox = new LakeSet([new Lake("huge", 0, 0, 60, -1e6f, -1e6f, 1e6f, 1e6f, 5)], 0.5f, 0);
    C(LakeRegen.Touched(generated, hugeBox, null).ByLakes == 7, "a box of the whole world touches every generated zone (and is counted by the generated zones, not by its own)");
    var mask = ClearMask.FromTarnFile(MakeClearFile(64, 32f, (r, col) => r == 0 && col == 0));
    // Pixel (0, 0) is at the north-west corner of a +-32 m mask: x -31.75, z 31.75 (zone (0, 0)), reaching 0.25 m past its square.
    var m = LakeRegen.Touched(generated, null, mask);
    C(m.ByMask == 2 && m.ByLakes == 0 && m.Either == 2, $"one marked pixel at the corner of zone (0, 0) touches that zone and, by its quarter-metre margin, the one to the west: {m.ByMask}");
    var both = LakeRegen.Touched(generated, lakes, mask);
    C(both.Either == 4 && both.ByLakes == 4 && both.ByMask == 2, "lakes and mask together are counted once per zone");
    C(LakeRegen.Zone(31.9f) == 0 && LakeRegen.Zone(32f) == 1 && LakeRegen.Zone(-32f) == 0 && LakeRegen.Zone(-32.1f) == -1, "zone numbers follow the game's grid (a zone is 64 m centred on 64 k)");

    using var w = new CmdWorld(CmdSettings());
    w.Zones = Enumerable.Range(-10, 21).SelectMany(x => Enumerable.Range(-10, 21).Select(z => (x, z))).ToList();
    var dry = w.Run("regen");
    C(dry.Contains("441 zones are generated; the lakes' boxes touch") && dry.Contains("a dry run, nothing changes") && dry.Contains("EVERY unprotected generated zone") && w.Regenerated == 0, "regen: a dry run counts, explains, and regenerates nothing: " + dry.Replace("\n", " | "));
    C(w.Run("regen confirm").Contains("regenerating the zones") && w.Regenerated == 1, "regen confirm runs what bc regen runs, once");
  }

  private static void LakeWorldKindTest()
  {
    Section("commands: a world that is not a Better Continents world, a client, Tarn installed");
    var dir = NewDir("kind-folder");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(dir, "lakes.txt"));
    File.Copy(Fixture("clear64.bin"), Path.Combine(dir, "clearmask.bin"));
    // Vanilla.
    using (var v = new CmdWorld(new BC.BetterContinentsSettings { EnabledForThisWorld = false }))
    {
      LakeCommands.Seams.Kind = () => BakeWorldKind.Vanilla;
      LakeCommands.Seams.MapDirectory = () => dir;
      foreach (var word in new[] { "here", "remove all confirm", "depth 1", "regen confirm" })
        C(v.Run(word).Contains("not a Better Continents world") && v.Converted.Count == 0, $"'{word}' on a vanilla world explains how to make it one");
      var dry = v.Run("load");
      C(dry.Contains("a dry run") && dry.Contains("not a Better Continents world") && dry.Contains("Add 'convert' and 'confirm'") && v.Converted.Count == 0, "load: a dry run says what converting means (the same words as the baking's)");
      C(v.Run("load confirm").Contains("Add 'convert' to go ahead") && v.Converted.Count == 0, "confirm without convert does not convert");
      LakeCommands.Seams.Peers = () => [("alice", ModInfo.Version), ("bob", null), ("carol", "0.9.4")];
      var peers = v.Run("load confirm convert");
      C(peers.Contains("bob (no Better Continents)") && peers.Contains("carol (Better Continents 0.9.4)") && !peers.Contains("alice (") && v.Converted.Count == 0, "convert is refused while a player is on another version: " + peers.Replace("\n", " | "));
      LakeCommands.Seams.Peers = () => [("alice", ModInfo.Version)];
      var done = v.Run("load confirm convert");
      C(v.Converted.Count == 1 && v.Converted[0].Lakes.Lakes.Count == 5 && v.Converted[0].Mask.Size == 64 && done.Contains("is a Better Continents world now"), "convert with every player on this version: the conversion gets the lakes and the mask: " + done.Replace("\n", " | "));
      C(v.Run("mask load " + Path.Combine(dir, "clearmask.bin") + " confirm convert").Contains("is a Better Continents world now") && v.Converted.Count == 2 && v.Converted[1].Lakes == null && v.Converted[1].Mask.Size == 64, "mask load ... convert does it with only the mask");
    }
    // The conversion itself carries both fields.
    var old = BC.Settings;
    var patched = BakedConvert.Patch;
    var lakes = new LakeSet([Lk("x", 1, 2)], 0.7f, 0);
    var mask = SampleMask();
    try
    {
      BakedConvert.Patch = () => { };
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      new BakedConvert().Convert(lakes, mask);
      C(BC.Settings.GameTerrain && BC.Settings.Lakes == lakes && BC.Settings.Clearing == mask, "BakedConvert.Convert(lakes, mask) builds the GameTerrain settings with them");
      var again = BC.Settings;
      new BakedConvert().Convert();
      C(BC.Settings.Lakes == lakes && BC.Settings.Clearing == mask && !ReferenceEquals(BC.Settings, again), "and the plain Convert() of a world that has them keeps them");
    }
    finally
    {
      BakedConvert.Patch = patched;
      BC.Settings = old;
    }
    // Unreadable settings file.
    using (var u = new CmdWorld(new BC.BetterContinentsSettings { EnabledForThisWorld = false }))
    {
      LakeCommands.Seams.Kind = () => BakeWorldKind.Unreadable;
      LakeCommands.Seams.MapDirectory = () => dir;
      C(u.Run("load confirm convert").Contains("cannot be read") && u.Converted.Count == 0 && u.Run("depth 1").Contains("cannot be read"), "a world whose settings file cannot be read is never converted or changed");
    }
    // A world whose lakes this build cannot read.
    var broken = CmdSettings(false, false);
    broken.LakesError = "format 9 is newer";
    using (var b = new CmdWorld(broken))
    {
      LakeCommands.Seams.MapDirectory = () => dir;
      C(b.Run("remove all confirm").Contains("cannot be read by this build") && b.Run("depth 2").Contains("cannot be read by this build") && b.Run("here").Contains("cannot be read by this build"), "lakes this build cannot read are not changed by remove, depth or here");
      C(b.Run("load").Contains("lakes this build cannot read, which are replaced"), "but load says it replaces them");
      b.Run("load confirm");
      C(BC.Settings.Lakes != null && BC.Settings.LakesError == null && BC.Settings.LakesBlobAsRead == null, "and 'load confirm' does");
    }
    // A client.
    using (var c = new CmdWorld(CmdSettings()))
    {
      LakeCommands.Seams.RunsWorld = () => false;
      LakeCommands.Seams.IsServerMachine = () => false;
      foreach (var word in new[] { "here", "at 1 2", "remove all confirm", "depth 1", "load", "import", "mask load x", "regen" })
        C(c.Run(word).Contains("bc_lake server"), $"'{word}' on a client points to 'bc_lake server'");
      C(c.Run("info").Contains("4 lakes") && c.Run("list").Contains("near") && c.Run("export " + NewDir("client-export")).Contains("wrote lakes.txt"), "the read-only words work on a client");
      var before = BC.Settings.Lakes;
      c.Run("server remove all confirm");
      C(c.Remote.SequenceEqual(["bc_lake remove all confirm"]) && c.Local.Count == 0 && ReferenceEquals(BC.Settings.Lakes, before), "server: sent to the server, nothing changed here: " + string.Join(" | ", c.Remote));
    }
    using (var s = new CmdWorld(CmdSettings()))
    {
      s.Run("server remove far");
      C(s.Local.SequenceEqual(["bc_lake remove far"]) && s.Remote.Count == 0, "on the machine that runs the world, server runs the words here");
      C(s.Run("server frobnicate").Contains("not understood") && s.Local.Count == 1, "a line the server would refuse is said before it travels");
    }
    // The Tarn mod is installed: the words that change the world refuse, the others work, the data is kept.
    using (var t = new CmdWorld(CmdSettings()))
    {
      TarnCompat.Installed = true;
      var lakesBefore = BC.Settings.Lakes;
      foreach (var word in new[] { "here", "at 1 2", "remove far", "depth 1", "load", "import", "mask load x", "regen confirm" })
        C(t.Run(word).Contains(TarnCompat.Message), $"'{word}' says that Better Continents' lakes are off while Tarn is installed");
      C(ReferenceEquals(BC.Settings.Lakes, lakesBefore), "and changes nothing");
      C(t.Run("info").Contains("Tarn mod is installed") && t.Says("4 lakes") && t.Run("list").Contains("near") && t.Run("mask at 0 0").Contains("mask"), "info, list and mask at work, and info says why the lakes are off");
      TarnCompat.Installed = false;
    }
  }

  // ---- the push -------------------------------------------------------------------------------------------------------------------------------------

  private sealed class WireEnd : ISocket
  {
    public WireEnd Peer = null;
    public bool Open = true;
    public readonly Queue<byte[]> Inbox = new();
    public readonly List<byte[]> Flight = new();
    public int FlightBytes;

    public bool IsConnected() => Open && Peer.Open;
    public void Send(ZPackage pkg)
    {
      if (!IsConnected()) return;
      var bytes = pkg.GetArray();
      Flight.Add(bytes);
      FlightBytes += bytes.Length;
    }
    public ZPackage Recv() => Inbox.Count > 0 ? new ZPackage(Inbox.Dequeue()) : null;
    public int GetSendQueueSize() => FlightBytes;
    public int GetCurrentSendRate() => 0;
    public bool IsHost() => false;
    public void Dispose() => Open = false;
    public void Close() => Open = false;
    public bool GotNewData() => Inbox.Count > 0;
    public string GetEndPointString() => "wire";
    public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
    public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) { localQuality = remoteQuality = 1f; ping = 0; outByteSec = inByteSec = 0f; }
    public ISocket Accept() => null;
    public int GetHostPort() => 0;
    public bool Flush() => true;
    public string GetHostName() => "wire";
    public void VersionMatch() { }
    public void Deliver()
    {
      foreach (var bytes in Flight)
        Peer.Inbox.Enqueue(bytes);
      Flight.Clear();
      FlightBytes = 0;
    }
  }

  private static void LakePushTest()
  {
    Section("push: a change of the lakes reaches the players in the world (BetterContinentsLakes)");
    var set = SampleLakes(300, 0.9f).Changed(SampleLakes(300, 0.9f).Lakes, 0.9f);
    var pkg = LakeTransfer.Pack(set);
    var read = LakeTransfer.Unpack(new ZPackage(pkg.GetArray()));
    C(read.Lakes.SequenceEqual(set.Lakes) && read.DepthScale == 0.9f && read.Revision == set.Revision && read.Revision == 1, "Pack and Unpack: the lakes to the bit, the depth scale and the revision");
    var big = new LakeSet(Enumerable.Range(0, LakeSet.MaxLakes).Select(i => RandomLake(new System.Random(i), i)).ToList(), 0.5f, 3);
    var bigPkg = LakeTransfer.Pack(big);
    C(bigPkg.GetArray().Length < 512 * 1024 && LakeTransfer.Unpack(new ZPackage(bigPkg.GetArray())).Lakes.Count == LakeSet.MaxLakes, $"5,000 lakes are one message under the 512 KB a reliable message holds ({bigPkg.GetArray().Length:N0} bytes)");

    // Refusals: cut short, too large a claim, a package that does not open.
    var raw = set.ToBlock();
    ZPackage Make(int revision, int rawLength, byte[] packed)
    {
      var p = new ZPackage();
      p.Write(revision);
      p.Write(rawLength);
      p.Write(packed);
      return new ZPackage(p.GetArray());
    }
    C(RefusesLake(() => LakeTransfer.Unpack(new ZPackage(new byte[] { 1, 0 })), "cut short"), "a package cut short");
    C(RefusesLake(() => LakeTransfer.Unpack(Make(1, LakeTransfer.MaxRawLength + 1, [1, 2, 3])), "say"), "a claim of more than 1 MB is refused before it is opened");
    C(RefusesLake(() => LakeTransfer.Unpack(Make(1, raw.Length, [255, 255, 255, 255])), "do not open") || RefusesLake(() => LakeTransfer.Unpack(Make(1, raw.Length, [255, 255, 255, 255])), "end after"), "bytes that do not inflate");
    var packedOnly = LakeTransfer.Pack(set).GetArray();
    C(RefusesLake(() => LakeTransfer.Unpack(Make(1, raw.Length + 5, new ZPackage(packedOnly).ReadInt() >= 0 ? ReadPacked(packedOnly) : [])), "end after"), "a raw length longer than what inflates");

    // Receive: a client takes the lakes; the lake engine is told.
    var saved = (BC.Settings, LakeTransfer.LakesChanged);
    var told = new List<LakeSet>();
    try
    {
      LakeTransfer.LakesChanged = told.Add;
      BC.Settings = World(SampleLakes(2, 0.4f), SampleMask());
      var mask = BC.Settings.Clearing;
      C(LakeTransfer.Receive(Wire(LakeTransfer.Pack(set))) && BC.Settings.Lakes.Lakes.SequenceEqual(set.Lakes) && BC.Settings.Lakes.DepthScale == 0.9f && BC.Settings.Lakes.Revision == 1 && told.Count == 1 && ReferenceEquals(BC.Settings.Clearing, mask),
        "a client takes the server's lakes (and keeps its mask), and the lake engine is told");
      var held = BC.Settings.Lakes;
      var log = LogHandler.During(() => C(!LakeTransfer.Receive(Make(5, raw.Length + 9, ReadPacked(packedOnly))), "a package that does not read changes nothing"));
      C(ReferenceEquals(BC.Settings.Lakes, held) && told.Count == 1 && log.Any(l => l.Contains("could not be read, so this client keeps the ones it has")), "the client keeps its lakes and says so in the log");
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      var log2 = LogHandler.During(() => C(!LakeTransfer.Receive(Wire(LakeTransfer.Pack(set))), "a world with no Better Continents settings here takes none"));
      C(BC.Settings.Lakes == null && log2.Any(l => l.Contains("left out")), "and says so");
      // A GameTerrain client takes them.
      BC.Settings = World(null, null, gameTerrain: true);
      C(LakeTransfer.Receive(Wire(LakeTransfer.Pack(set))) && BC.Settings.Lakes.Lakes.Count == 300, "a client that keeps the game's terrain takes them");
    }
    finally
    {
      (BC.Settings, LakeTransfer.LakesChanged) = saved;
    }

    // Over real ZRpc objects on made-up sockets.
    var serverEnd = new WireEnd();
    var clientEnd = new WireEnd();
    serverEnd.Peer = clientEnd;
    clientEnd.Peer = serverEnd;
    var serverRpc = new ZRpc(serverEnd);
    var clientRpc = new ZRpc(clientEnd);
    LakeTransfer.RegisterClient(clientRpc);
    var clients = BakedTransfer.Clients.ToList();
    var settings = BC.Settings;
    var changed = LakeTransfer.LakesChanged;
    try
    {
      BakedTransfer.Clients.Clear();
      var joined = new BakedTransfer.LayerClient("alice", new BakedTransfer.ZRpcLink(serverRpc), _ => { }) { JoinDone = true };
      var joining = new StubLink();
      BakedTransfer.Clients.Add(joined);
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("bob", joining, _ => { }) { JoinDone = false });
      BC.Settings = World(SampleLakes(1, 0.2f), SampleMask());
      var got = new List<LakeSet>();
      LakeTransfer.LakesChanged = got.Add;
      var log = LogHandler.During(() => C(LakeTransfer.Broadcast(set) == 1, "Broadcast sends to the one client whose join is over (not to the one still joining)"));
      C(log.Any(l => l.Contains("revision 1 (300 lakes, depth scale 0.9) sent to 1 player")), "and says so in the log");
      serverEnd.Deliver();
      clientRpc.Update(0f);
      C(got.Count == 1 && got[0].Lakes.SequenceEqual(set.Lakes) && BC.Settings.Lakes.DepthScale == 0.9f && joining.Sent.Count == 0, "the client's RPC handler takes the push off the wire into its settings; the player still joining was sent nothing");
    }
    finally
    {
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.AddRange(clients);
      BC.Settings = settings;
      LakeTransfer.LakesChanged = changed;
    }
  }

  // A package as the receiving end reads it: a new one over the same bytes, at its start.
  private static ZPackage Wire(ZPackage sent) => new(sent.GetArray());

  // The deflated bytes of a package Pack made.
  private static byte[] ReadPacked(byte[] package)
  {
    var p = new ZPackage(package);
    p.ReadInt();
    p.ReadInt();
    return p.ReadByteArray();
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// Offline checks of bc_bake (BakeCommandLine.cs, BakeCommands.cs, BakeRunner.Load.cs): what a line means, who may run it, what info and list say,
// a load of a compiler's file, the checks of a layer, and the way a world's layer is dropped from the main menu.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static BakeCommand BakeParse(string line) => BakeCommandLine.Parse(line);

  private static void BakeCommandLineTest()
  {
    Section("commands: what a line means");
    var empty = BakeParse("");
    C(empty.Verb == BakeVerb.Help && empty.Error == null && BakeParse("help").Verb == BakeVerb.Help && BakeParse("  ?  ").Verb == BakeVerb.Help, "no words, 'help' and '?' are the help");
    foreach (var (word, verb) in new[] { ("info", BakeVerb.Info), ("stats", BakeVerb.Stats), ("hide", BakeVerb.Hide), ("show", BakeVerb.Show), ("list", BakeVerb.List) })
      C(BakeParse(word).Verb == verb && BakeParse(word.ToUpperInvariant()).Verb == verb && BakeParse(word + " now").Error != null && BakeParse(word + " confirm").Error != null, $"'{word}' is {verb}, in any case, and takes no more words");

    var area = BakeParse("area 40");
    C(area.Verb == BakeVerb.Area && area.Error == null && area.Radius == 40f && area.AtX == null && !area.Words.Confirm && !area.Words.Town, "area 40: a circle around whoever asks, a dry run");
    var at = BakeParse("area 12.5 at 812 -1206.25 town any confirm");
    C(at.Radius == 12.5f && at.AtX == 812f && at.AtZ == -1206.25f && at.Words.Town && at.Words.Any && at.Words.Confirm && !at.Words.Convert, "area 12.5 at 812 -1206.25 town any confirm");
    var flagsFirst = BakeParse("area confirm town 40 convert");
    C(flagsFirst.Radius == 40f && flagsFirst.Words.Confirm && flagsFirst.Words.Town && flagsFirst.Words.Convert, "the words that change what it does stand wherever they like");
    C(BakeParse("area").Error != null && BakeParse("area x").Error != null && BakeParse("area 40 at 1").Error != null && BakeParse("area 40 near 1 2").Error != null && BakeParse("area 40 all").Error != null, "an area needs a radius, and 'at' two numbers");
    var circle = area.AreaFor(10f, 20f);
    C(circle != null && circle.Circle && circle.CenterX == 10f && circle.CenterZ == 20f && circle.Radius == 40f, "the circle is around the caller when no place is given");
    C(area.AreaFor(null, null) == null && at.AreaFor(null, null)!.CenterX == 812f, "and there is none without a caller, unless 'at' gave one");

    var box = BakeParse("box 1 2 -3 4.5 confirm");
    C(box.Verb == BakeVerb.Box && box.IsBox && box.X1 == 1f && box.Z1 == 2f && box.X2 == -3f && box.Z2 == 4.5f && box.Words.Confirm, "box x1 z1 x2 z2");
    C(box.AreaFor(0f, 0f)!.MinX == -3f && BakeParse("box 1 2 3").Error != null && BakeParse("box 1 2 3 x").Error != null, "a box needs four numbers");
    C(box.Echo(true) == "box 1 2 -3 4.5" && at.Echo(true) == "12.5 at 812 -1206.25" && area.Echo(true) == "40", "the words that make it again");

    var unbakeCircle = BakeParse("unbake 40 at 1 2 confirm");
    var unbakeBox = BakeParse("unbake box 0 0 10 10 all");
    var unbakeWorld = BakeParse("unbake world all confirm");
    C(unbakeCircle.Verb == BakeVerb.Unbake && unbakeCircle.Radius == 40f && unbakeCircle.AtX == 1f && unbakeCircle.Words.Confirm && !unbakeCircle.Words.All, "unbake 40 at 1 2 confirm");
    C(unbakeBox.IsBox && unbakeBox.X2 == 10f && unbakeBox.Words.All, "unbake box ... all");
    C(unbakeWorld.IsWorld && unbakeWorld.Words.All && unbakeWorld.Words.Confirm && unbakeWorld.AreaFor(0f, 0f) == null && unbakeWorld.Echo(true) == "world", "unbake world all confirm");
    C(BakeParse("unbake").Error != null && BakeParse("unbake town 40").Error != null && BakeParse("unbake world 4").Error != null && BakeParse("unbake box 1 2 3").Error != null, "an unbake needs its scope and takes only 'all' and 'confirm'");

    C(BakeParse("undo").Number == null && BakeParse("undo 7 confirm").Number == 7 && BakeParse("undo 7 confirm").Words.Confirm && BakeParse("undo x").Error != null && BakeParse("undo 0").Error != null
      && BakeParse("undo 70000").Error != null && BakeParse("undo town").Error != null, "undo [n] [confirm]");
    var drop = BakeParse("drop bake 3 confirm");
    C(drop.Verb == BakeVerb.Drop && drop.Number == 3 && drop.Words.Confirm && !drop.Compiler && BakeParse("drop compiler").Compiler && BakeParse("drop").Error != null && BakeParse("drop bake").Error != null
      && BakeParse("drop bake x").Error != null && BakeParse("drop compiler 3").Error != null, "drop compiler | bake n [confirm]");
    var world = BakeParse("drop My World 2 confirm");
    C(world.Verb == BakeVerb.DropWorld && world.Text == "My World 2" && world.Words.Confirm, "'drop' with a name is the main menu's: the name keeps its spaces and its case, less the words of the command: " + world.Text);

    var load = BakeParse("load C:\\maps\\My Town\\placements.bcp convert confirm");
    C(load.Verb == BakeVerb.Load && load.Text == "C:\\maps\\My Town\\placements.bcp" && load.Words.Convert && load.Words.Confirm, "load takes a path with spaces as typed: " + load.Text);
    C(BakeParse("load").Text == null && BakeParse("load confirm").Text == null && BakeParse("load town").Error != null && BakeParse("load x all").Error != null, "load [file] [convert] [confirm]; no file means the Directory's");
    C(BakeParse("export /tmp/a b.bcp").Text == "/tmp/a b.bcp" && BakeParse("export").Text == null && BakeParse("export x confirm").Error != null, "export [file]");
    var orphans = BakeParse("orphans destroy items confirm");
    C(orphans.Verb == BakeVerb.Orphans && orphans.Destroy && orphans.Items && orphans.Words.Confirm && !BakeParse("orphans").Destroy && BakeParse("orphans items").Error != null && BakeParse("orphans burn").Error != null, "orphans [destroy [items]] [confirm]");
    var check = BakeParse("check");
    C(check.Verb == BakeVerb.Check && !check.CheckAll && BakeParse("check all").CheckAll && BakeParse("check 3 -2").ZoneX == 3 && BakeParse("check 3 -2").ZoneZ == -2 && BakeParse("check 3").Error != null && BakeParse("check x y").Error != null, "check [all | zx zz]");
    var server = BakeParse("server area 40 at 1 2 confirm");
    C(server.Verb == BakeVerb.Server && server.Text == "area 40 at 1 2 confirm", "'server' hands the rest on as it is");
    C(BakeParse("frobnicate").Error!.Contains("'frobnicate' is not understood") && BakeParse("bake").Error != null, "a word it does not know is said");
    C(BakeCommandLine.Usage.Contains("unbake <radius>") && BakeCommandLine.Usage.Contains("drop <world>"), "the usage lists the words");

    Section("commands: who may run what (10.1)");
    foreach (var verb in new[] { BakeVerb.Help, BakeVerb.Info, BakeVerb.Stats, BakeVerb.Hide, BakeVerb.Show })
      C(BakeCommandLine.Refusal(verb, false, false) == null && BakeCommandLine.Refusal(verb, false, true) == null, $"{verb} runs on any machine, a client too");
    C(BakeCommandLine.Refusal(BakeVerb.Server, false, false) == null, "'server' too (it only asks one)");
    foreach (var verb in new[] { BakeVerb.List, BakeVerb.Check, BakeVerb.Area, BakeVerb.Box, BakeVerb.Unbake, BakeVerb.Undo, BakeVerb.Load, BakeVerb.Export, BakeVerb.Orphans, BakeVerb.Drop })
    {
      C(BakeCommandLine.Refusal(verb, true, true) == null, $"{verb} runs where the world is");
      C((BakeCommandLine.Refusal(verb, false, true) ?? "").Contains("bc_bake server"), $"{verb} on a client points to 'bc_bake server'");
      C((BakeCommandLine.Refusal(verb, true, false) ?? "").Contains("load a world first"), $"{verb} with no world loaded says so");
    }
    C(BakeCommandLine.Refusal(BakeVerb.DropWorld, false, false) == null && BakeCommandLine.Refusal(BakeVerb.DropWorld, true, false) == null && BakeCommandLine.Refusal(BakeVerb.DropWorld, true, true) != null,
      "'drop <world>' is for the main menu only");
  }

  private static void BakeReportTest()
  {
    Section("commands: what info and list say");
    var none = BakeReport.InfoLines(null, false, -1, null);
    C(none.Count == 1 && none[0] == "bc_bake info: this world has no baked layer.", "info of a world without a layer");
    C(BakeReport.InfoLines(null, true, -1, null)[0].Contains("keeps the game's own terrain"), "and of a game-terrain world");

    BakeRunner.Reset();
    var fx = BakeNewFx("report", BakeStandardWorld, out _);
    BakeRun(fx, BakeStandardArea(), "town confirm");
    var info = BakeReport.InfoLines(fx.Layer, false, 2, ["pine_special", "other"]);
    var text = string.Join("\n", info);
    C(info[0].StartsWith("bc_bake info: layer revision 1, 10 records in 2 zones,") && info[0].Contains("made by Better Continents"), "info: the revision, records, zones and producer: " + info[0]);
    C(text.Contains("Records by role: Static 7, Live 2, Seat 1.") && text.Contains("By source: bake 1 10."), "by role and by source: " + text);
    C(text.Contains("Palette ") && text.Contains("1 operations in the registry; the next is number 2") && text.Contains("Live pieces seeded in this world: 2.") && text.Contains("lacks (2): pine_special, other."), "the palette, the registry, the live pieces and the prefabs this game lacks");
    var compiler = CompilerFile([Wall(), Door(), Roof()], 0);
    var cinfo = string.Join("\n", BakeReport.InfoLines(compiler, false, -1, []));
    C(cinfo.Contains("By source: the compiler's file 43.") && cinfo.Contains("Ground in 1 zones, paint in 1, clear masks in 2, no vegetation at all in 1.") && cinfo.Contains("none."), "a compiler's layer: " + cinfo);

    BakeRunner.Reset();
    BakeUnbakeRun(fx, BakeWholeWorld, "confirm", "unbake world");
    BakeRunner.Reset();
    BakeDropRun(fx, new DropScope { Bake = 1 }, "");
    var journal = new BakeJournal(fx.Folder);
    var lines = BakeReport.ListLines(fx.Layer.Registry.Operations, journal.Numbers(), n => journal.GetState(n), n => journal.Read(n));
    C(lines[0].StartsWith("bc_bake list: 2 operations") && lines.Count == 3, "list: a header and a line for each operation: " + string.Join(" | ", lines));
    C(lines[1].Contains("bake") && lines[1].Contains("Tester") && lines[1].Contains("30 m around 30, 0") && lines[1].Contains("+10") && lines[1].EndsWith("done  undo file kept"), "a bake: " + lines[1]);
    C(lines[2].Contains("unbake") && lines[2].Contains("the whole world") && lines[2].Contains("-10") && lines[2].Contains("done"), "an unbake: " + lines[2]);
    BakeRunner.Reset();
    BakeUndoRun(fx, 2);
    var after = BakeReport.ListLines(fx.Layer.Registry.Operations, journal.Numbers(), n => journal.GetState(n), n => journal.Read(n));
    C(after[2].Contains("undone"), "an undone operation says so: " + after[2]);
    new BakeJournal(fx.Folder).SetState(1, BakeState.Abandoned);
    C(BakeReport.StateWord(null, BakeState.Abandoned) == "abandoned" && BakeReport.StateWord(null, BakeState.Removing) == "not in the layer" && BakeReport.StateWord(null, null) == "done", "an operation that is not in the layer");
    var op = fx.Layer.Registry.Operations[0];
    C(BakeReport.StateWord(op, BakeState.Removing) == "not finished" && BakeReport.StateWord(op, BakeState.Removed) == "done" && BakeReport.StateWord(op, BakeState.Settled) == "done", "and the states in the layer");
    var empty = BakeReport.ListLines([], [], _ => null, _ => null);
    C(empty.Count == 1 && empty[0].Contains("no operation yet"), "a world with no operation");
  }

  private static void BakeLoadTest()
  {
    Section("load: a compiler's new file in the running world (6.2)");
    BakeRunner.Reset();
    var fx = BakeCompilerWorld("load");
    var original = fx.Layer;
    // An in-game bake on top of the compiler's layer.
    BakeRun(fx, BakeStandardArea());
    // The world holds the standard pieces too; the bake ran with the compiler's facts only, so give it the pieces first.
    C(fx.Layer.Placements == 43, "(no piece of the standard world in this fixture: the layer is the compiler's 43)");

    var other = CompilerFile([Wall(), Door(), Roof(), Gate()], 1);
    var bytes = other.Bytes.Take(other.Length).ToArray();
    fx.Said.Clear();
    int events = fx.Clock.Events.Count;
    BakeDrive(BakeRunner.Load(fx.Ctx, "placements.bcp", bytes, BakeWordsOf("")), fx, "a load");
    var dry = string.Join("\n", fx.Said);
    C(fx.Clock.Events.Count == events && dry.Contains("bc_bake load placements.bcp: a dry run, nothing changes. 'bc_bake load placements.bcp confirm' loads it.") && dry.Contains("File: revision 1, 44 records in"), "a dry run says what the file holds and changes nothing: " + dry);
    C(dry.Contains("It replaces the layer's 43 compiler records with its 44") && dry.Contains("the 0 records baked in game stay.") && dry.Contains("kept in the undo folder first"), "and what it replaces");

    C(dry.Contains("The layer after it: revision 2, 44 records, ") && dry.Contains("The live pieces would follow: ") && fx.Reconcile.Previews == 1,
      "the dry run builds the merged layer and has the live pieces' planner say what it would do (B's Gather and Plan in the game): " + dry);
    C(fx.Layer.Revision == 1 && fx.Clock.Events.Count == events, "and the layer it built for that is dropped");

    fx.Said.Clear();
    BakeDrive(BakeRunner.Load(fx.Ctx, "placements.bcp", bytes, BakeWordsOf("confirm")), fx, "a load");
    var run = fx.Clock.Events.Skip(events).ToList();
    C(run.SequenceEqual(["layer", "push r2", "reconcile r2"]), "the layer, the push and the reconciliation of the live pieces: " + string.Join(" > ", run));
    C(fx.Layer.Revision == 2 && fx.Layer.Placements == 44 && fx.Layer.Registry.Operations.Single().Kind == OperationKind.Load && fx.Layer.Registry.Operations.Single().Added == 44 && fx.Layer.Registry.Operations.Single().Removed == 43,
      "revision 2 holds the file's 44 records, and the registry says a load added 44 and removed 43");
    C(File.Exists(Path.Combine(fx.Folder, "layer-r1.bcp")) && File.ReadAllBytes(Path.Combine(fx.Folder, "layer-r1.bcp")).SequenceEqual(original.Bytes.Take(original.Length)), "the layer as it was is kept in the undo folder, byte for byte");
    C(fx.Said.Any(l => l.StartsWith("bc_bake: load 1: layer revision 2")) && fx.Said.Any(l => l.Contains("To go back, 'bc_bake load' the file layer-r1.bcp")), "what it says: " + string.Join(" | ", fx.Said));
    C(!Directory.GetFiles(fx.Folder).Any(f => f.EndsWith(".bcj") || f.EndsWith(".state")), "a load has no undo file and no state: the new layer replaces the old in one step");
    C(new BakeJournal(fx.Folder).Pending().Count == 0, "so nothing is left for the next world load to settle");

    // Going back: the kept file is a layer like any other.
    fx.Said.Clear();
    BakeRunner.Reset();
    var back = File.ReadAllBytes(Path.Combine(fx.Folder, "layer-r1.bcp"));
    BakeDrive(BakeRunner.Load(fx.Ctx, "layer-r1.bcp", back, BakeWordsOf("confirm")), fx, "a load");
    C(fx.Layer.Placements == 43 && fx.Layer.Revision == 3 && fx.Layer.Registry.Operations.Count == 2, "loading the kept file goes back: 43 records, revision 3, two loads in the registry");

    Section("load: in-game records stay, and what a load refuses");
    BakeRunner.Reset();
    var gx = BakeNewFx("load-stay", BakeStandardWorld, out _, CompilerFile([Wall(), Door(), Roof()], 0));
    BakeRun(gx, BakeStandardArea());
    long baked = gx.Layer.Placements - gx.Layer.RecordsOfSource(0);
    var keysBefore = BakeKeysOfSource(gx.Port, 1);
    gx.Said.Clear();
    var variant = CompilerFile([Wall(), Door(), Roof(), Gate(), Beam()], 1);
    BakeDrive(BakeRunner.Load(gx.Ctx, "b.bcp", variant.Bytes.Take(variant.Length).ToArray(), BakeWordsOf("confirm")), gx, "a load");
    C(BakeKeysOfSource(gx.Port, 1).SequenceEqual(keysBefore) && gx.Layer.RecordsOfSource(0) == variant.RecordsOfSource(0) && gx.Layer.Placements == variant.Placements + baked,
      $"the {baked} records baked in the game are as they were; the compiler's are the file's");
    C(gx.Layer.Registry.Operations.Count == 2 && gx.Layer.Registry.Operations[0].State == OperationState.InLayer && gx.Layer.Registry.Operations[0].ValueSets.Length > 0, "the bake is in the registry with its value sets, and the load beside it");
    var withBakes = gx.Layer;
    // A file that holds in-game records: they are left out, said.
    gx.Said.Clear();
    BakeRunner.Reset();
    BakeDrive(BakeRunner.Load(gx.Ctx, "exported.bcp", withBakes.Bytes.Take(withBakes.Length).ToArray(), BakeWordsOf("")), gx, "a load");
    C(gx.Said.Any(l => l.Contains($"{baked} records baked in game are in this file; load replaces only the compiler's records.")), "a file with records baked in game says they are left out: " + string.Join(" | ", gx.Said));

    gx.Said.Clear();
    int before = gx.Clock.Events.Count;
    BakeRunner.Reset();
    BakeDrive(BakeRunner.Load(gx.Ctx, "junk.bcp", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40], BakeWordsOf("confirm")), gx, "a load");
    C(gx.Said.Single().StartsWith("bc_bake: junk.bcp is not a layer this version can read") && gx.Clock.Events.Count == before, "a file that is not a layer changes nothing: " + string.Join(" | ", gx.Said));
    gx.Said.Clear();
    gx.Transport.Unavailable = "sending a changed layer to the players is not available in this build";
    BakeDrive(BakeRunner.Load(gx.Ctx, "b.bcp", variant.Bytes.Take(variant.Length).ToArray(), BakeWordsOf("confirm")), gx, "a load");
    C(gx.Clock.Events.Count == before && gx.Said.Single().EndsWith("Nothing is changed."), "without a transport it changes nothing: " + string.Join(" | ", gx.Said));
    gx.Transport.Unavailable = null;

    var vanilla = BakeNewFx("load-vanilla", BakeStandardWorld, out _);
    vanilla.Convert.Kind = BakeWorldKind.Vanilla;
    BakeRunner.Reset();
    BakeDrive(BakeRunner.Load(vanilla.Ctx, "a.bcp", bytes, BakeWordsOf("confirm")), vanilla, "a load");
    C(vanilla.Clock.Count == 0 && vanilla.Said.Any(l => l.Contains("Add 'convert' to go ahead.")), "a world that is not a Better Continents world needs 'convert'");
    vanilla.Said.Clear();
    BakeRunner.Reset();
    BakeDrive(BakeRunner.Load(vanilla.Ctx, "a.bcp", bytes, BakeWordsOf("confirm convert")), vanilla, "a load");
    C(vanilla.Convert.Converted == 1 && vanilla.Layer != null && vanilla.Layer.Placements == 44 && vanilla.Layer.Revision == 1, "and with it the world is converted and gets the file's layer (revision 1)");
    BakeRunner.Reset();
  }

  private static void BakeCheckTest()
  {
    Section("check: duplicates, two sources in one place, Live records past 16.35 km, real pieces on records");
    BakeRunner.Reset();
    var fx = BakeNewFx("check", BakeStandardWorld, out var taken);
    BakeRun(fx, BakeStandardArea());
    var clean = BakeCheck.Lines(fx.Ctx, fx.Layer, fx.Port.ZonesWithRecords(), "the whole layer", false);
    C(clean[0] == "bc_bake check: 8 records in the whole layer (2 zones)." && clean[1] == "No problem found.", "a clean bake: " + string.Join(" | ", clean));

    // Duplicates, an overlap of two sources, a Live record far out, and a real piece where a record is.
    var edit = fx.Layer.Edit();
    var wall = BakeRecordsOf(fx).First(r => r.Prefab == "stone_wall_2x1");
    int index = wall.Record.Palette;
    var twin = wall.Record;
    edit.AddRecords([twin], 3f);
    var otherSource = wall.Record;
    otherSource.Source = 1;
    otherSource.Flags |= RecordFlags.Source;
    otherSource.X = (ushort)(otherSource.X + 100);
    otherSource.ValueSet = 0;
    var apart = fx.Layer.Palette[index];
    edit.AddRecords([otherSource], 3f);
    var liveIndex = edit.PaletteIndexFor(Door());
    var far = new ZoneKey(300, 0);
    edit.AddRecords([ZoneRecord.CreateYaw(liveIndex, far.OriginX + 4, 30, far.OriginZ + 4, 0, null, 77u)], 3f);
    var (broken, _) = edit.Build();
    var ctx = fx.Ctx;
    fx.World.Add(taken.First(p => p.PrefabName == "stone_wall_2x1"));
    var findings = BakeCheck.Run(ctx, broken, broken.Zones.Where(z => z.Placements > 0).Select(z => z.Key).ToList(), false, out long records);
    var kinds = findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
    C(records == 11 && kinds.Count(k => k.Key.StartsWith("Duplicates")) == 1 && kinds.Any(k => k.Key.StartsWith("Live records past 16.35 km")) && kinds.Any(k => k.Key.StartsWith("Records with a real piece")),
      "the findings: " + string.Join("; ", kinds.Select(k => $"{k.Key.Substring(0, Math.Min(30, k.Key.Length))}.. {k.Value}")));
    C(BakeCheck.Run(ctx, broken, broken.Zones.Where(z => z.Placements > 0).Select(z => z.Key).ToList(), true, out _).All(f => !f.Kind.StartsWith("Live records past")), "past 16.35 km is fine in a world with wide sectors");
  }

  private static void BakeDropWorldTest()
  {
    Section("drop <world>: the layer leaves a world's settings in the main menu (4.5)");
    var dir = Path.Combine(Work, "dropworld");
    Directory.CreateDirectory(dir);
    var layer = MakeSample(51).Layer;
    var inGameBefore = layer.Placements - layer.RecordsOfSource(0);
    C(inGameBefore > 0, "(the sample layer holds records baked in game)");

    string Write(string name, BC.BetterContinentsSettings settings)
    {
      var path = Path.Combine(dir, name);
      var bytes = SettingsBytes(settings, false);
      var file = new byte[bytes.Length + 4];
      BitConverter.GetBytes(bytes.Length).CopyTo(file, 0);
      Buffer.BlockCopy(bytes, 0, file, 4, bytes.Length);
      File.WriteAllBytes(path, file);
      return path;
    }

    var said = new List<string>();
    var path = Write("shaped", World(layer));
    var backup = Path.Combine(dir, "backups", "shaped", "BetterContinents.pre-drop");
    BakeDropWorld.RunOnFile(path, backup, "Shaped", false, said.Add);
    C(said[0].StartsWith("bc_bake drop Shaped: a dry run") && said[1] == $"This world's layer holds {BakeTally.Num(inGameBefore)} pieces baked in the game. Dropping it removes them for good (a copy of the file is kept at {backup}). "
      + "To keep them as real pieces, load the world and run 'bc_bake unbake world confirm' first.", "the dry run of a world with in-game records says what 4.5 says, and where the copy goes: " + string.Join(" | ", said));
    C(said[2].Contains("only the layer goes") && !File.Exists(backup), "and that only the layer goes; nothing was changed");
    var original = File.ReadAllBytes(path);
    said.Clear();
    BakeDropWorld.RunOnFile(path, backup, "Shaped", true, said.Add);
    C(File.ReadAllBytes(backup).SequenceEqual(original), "the old file is kept, byte for byte");
    var reread = BC.BetterContinentsSettings.Load(path);
    C(reread.EnabledForThisWorld && !reread.HasLayer && !reread.GameTerrain && reread.GlobalScale == 1.5f && reread.WorldSize == 12000f, "the world keeps its settings and loses the layer");
    C(said.Single().StartsWith("bc_bake drop: the layer of 'Shaped' is gone") && said.Single().Contains("The old file is kept at"), "what it says: " + said.Single());
    said.Clear();
    BakeDropWorld.RunOnFile(path, backup, "Shaped", true, said.Add);
    C(said.Single().Contains("has no layer"), "a second time there is no layer: " + said.Single());

    var terrain = Write("terrain", World(layer, gameTerrain: true));
    said.Clear();
    BakeDropWorld.RunOnFile(terrain, Path.Combine(dir, "backups", "terrain", "BetterContinents.pre-drop"), "Terrain", false, said.Add);
    C(said.Any(l => l.Contains("the file goes, and the world is the game's own world again")), "a game-terrain world says its file goes: " + string.Join(" | ", said));
    said.Clear();
    BakeDropWorld.RunOnFile(terrain, Path.Combine(dir, "backups", "terrain", "BetterContinents.pre-drop"), "Terrain", true, said.Add);
    C(!File.Exists(terrain) && File.Exists(Path.Combine(dir, "backups", "terrain", "BetterContinents.pre-drop")) && said.Single().Contains("the world is the game's own world again"), "and with 'confirm' the file is gone, kept as a copy");

    var compilerOnly = Write("compiler", World(CompilerFile([Wall(), Door(), Roof()], 0)));
    said.Clear();
    BakeDropWorld.RunOnFile(compilerOnly, Path.Combine(dir, "backups", "compiler", "x"), "Compiler", false, said.Add);
    C(said[1].Contains("holds 43 records from a compiler's file and none baked in the game"), "a layer of a compiler's records alone is not 'for good': " + said[1]);

    said.Clear();
    BakeDropWorld.RunOnFile(Path.Combine(dir, "missing"), "x", "Nowhere", true, said.Add);
    C(said.Single().Contains("has no Better Continents settings here"), "a world without settings");
    File.WriteAllBytes(Path.Combine(dir, "junk"), [9, 9, 9, 9, 9, 9, 9, 9]);
    said.Clear();
    BakeDropWorld.RunOnFile(Path.Combine(dir, "junk"), "x", "Junk", true, said.Add);
    C(said.Single().Contains("cannot be read") && said.Single().Contains("nothing is changed"), "settings that cannot be read are left alone: " + said.Single());
    var disabled = Write("disabled", new BC.BetterContinentsSettings { EnabledForThisWorld = false });
    said.Clear();
    BakeDropWorld.RunOnFile(disabled, "x", "Off", true, said.Add);
    C(said.Count == 1 && !said.Single().Contains("gone"), "settings of a world Better Continents is off for are not touched: " + string.Join(" | ", said));
    said.Clear();
    BakeDropWorld.Run("   ", false, said.Add);
    C(said.Single().Contains("the name of a world"), "no name is asked for");
  }
}

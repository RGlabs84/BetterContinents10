// Added by Wubarrk on 2026-10-14 for lakes and clearing (0.10.5).
//
// What the review of the data side found (lakes-a-review.md), each held by a test: export never writes over a file without 'confirm', backups never take each other's
// place, a lake edited while a client joins reaches it, a lake with a number that is not a number is never written or added, a damaged blob is always the format
// exception, the conversion's package is not sent over a reliable message's size, the clearing postfix answers before the twin guard's, a mask with absurd pixels is
// refused, an import keeps the world's tuned depth scale and the dry run shows the lines the parser left out, and files are put in place whole.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  // ---- 1. export never over a file ------------------------------------------------------------------------------------------------------------------

  private static void FixExportSafetyTest()
  {
    Section("fixes: export and mask export never write over a file without 'confirm'");
    using var w = new CmdWorld(CmdSettings());
    var folder = NewDir("fix-export");
    var mine = Path.Combine(folder, "lakes.txt");
    File.WriteAllText(mine, "Mine;1;2;60;0;0;9;9;3\n");
    var text = w.Run($"export {folder}");
    C(text.Contains("already holds lakes.txt") && text.Contains("Nothing was written") && text.Contains("'confirm'") && File.ReadAllText(mine) == "Mine;1;2;60;0;0;9;9;3\n" && !File.Exists(Path.Combine(folder, "clearmask.bin")),
      "a folder that holds the map maker's own lakes.txt is not written to: " + text.Replace("\n", " | "));
    text = w.Run($"export {folder} confirm");
    C(text.Contains("wrote lakes.txt (4 lakes) and clearmask.bin") && LakeSet.ParseLines(File.ReadAllLines(mine), _ => { }).Lakes.Count == 4 && File.Exists(Path.Combine(folder, "clearmask.bin")) && !Directory.GetFiles(folder).Any(f => f.EndsWith(".tmp")),
      "with 'confirm' it replaces it (whole: no .tmp left)");
    text = w.Run($"export {folder}");
    C(text.Contains("already holds lakes.txt and clearmask.bin") && text.Contains("replace them"), "both files there: both named");
    // A relative path is made whole, and said as such.
    var cwd = Directory.GetCurrentDirectory();
    try
    {
      var here = NewDir("fix-cwd");
      Directory.SetCurrentDirectory(here);
      text = w.Run("export rel-out");
      C(text.Contains(Path.Combine(Path.GetFullPath(here), "rel-out")) && File.Exists(Path.Combine(here, "rel-out", "lakes.txt")), "a relative folder is made a whole path before anything is written, and the answer says which: " + text);
      File.WriteAllText(Path.Combine(here, "precious.bin"), "PRECIOUS");
      text = w.Run("mask export precious.bin");
      C(text.Contains("precious.bin already exists") && text.Contains("Nothing was written") && File.ReadAllText(Path.Combine(here, "precious.bin")) == "PRECIOUS", "mask export onto a file that is there is refused, and the file is as it was");
      text = w.Run("mask export precious.bin confirm");
      C(text.Contains("wrote the clearing mask") && File.ReadAllBytes(Path.Combine(here, "precious.bin")).SequenceEqual(BC.Settings.Clearing.ToTarnFile()), "with 'confirm' it is replaced");
    }
    finally
    {
      Directory.SetCurrentDirectory(cwd);
    }
    // A folder takes the mask as clearmask.bin; there too an existing one is refused.
    var maskDir = NewDir("fix-mask-dir");
    C(w.Run($"mask export {maskDir}").Contains("wrote the clearing mask") && File.Exists(Path.Combine(maskDir, "clearmask.bin")) && w.Run($"mask export {maskDir}").Contains("already exists"), "mask export <folder> writes clearmask.bin in it, once");
    // The game's world saves.
    var saves = NewDir("fix-saves");
    w.InsideSaves = true;
    C(w.Run($"export {saves}").Contains("inside the game's world saves") && !File.Exists(Path.Combine(saves, "lakes.txt")), "a folder inside the world saves gets nothing");
    C(w.Run($"mask export {Path.Combine(saves, "world.fwl")}").Contains("inside the game's world saves") && !File.Exists(Path.Combine(saves, "world.fwl")), "nor does a file there");
    C(w.Run("export").Contains("inside the game's world saves"), "nor the default folder if it were inside them");
    w.InsideSaves = false;
    // A file where the folder should be; a name the system refuses.
    C(w.Run($"export {mine}").Contains("is a file, not a folder"), "a file named as the folder is said");
    C(w.Run("export a\0b").Contains("is not a path") && w.Run("mask export a\0b").Contains("is not a path"), "a name the system refuses is said, not thrown");
    // The default folder is new each time.
    w.Run("export");
    w.Run("export");
    var made = Directory.GetDirectories(w.Save).Select(Path.GetFileName).Where(n => n.StartsWith("lakes-")).OrderBy(n => n).ToList();
    C(made.SequenceEqual(["lakes-2026-10-13-123005-000", "lakes-2026-10-13-123005-000-2"]), "no folder: two exports in one clock tick are two folders: " + string.Join(", ", made));
  }

  // ---- 2. backups never replace each other ------------------------------------------------------------------------------------------------------------

  private static void FixBackupTest()
  {
    Section("fixes: backups never take each other's place");
    using var w = new CmdWorld(CmdSettings(true, true));
    var four = BC.Settings.Lakes;
    w.Run("remove far");
    w.Run("remove near");
    w.Run("remove lake3");
    var files = Directory.GetFiles(w.Save, "lakes-before-*.txt").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    C(files.SequenceEqual(["lakes-before-2026-10-13-123005-000-2.txt", "lakes-before-2026-10-13-123005-000-3.txt", "lakes-before-2026-10-13-123005-000.txt"]), "three removals in one clock tick leave three backups: " + string.Join(", ", files));
    int Count(string name) => LakeSet.ParseLines(File.ReadAllLines(Path.Combine(w.Save, name)), _ => { }).Lakes.Count;
    C(Count("lakes-before-2026-10-13-123005-000.txt") == 4 && Count("lakes-before-2026-10-13-123005-000-2.txt") == 3 && Count("lakes-before-2026-10-13-123005-000-3.txt") == 2, "the first holds the four lakes of the world as it was, the next the three, then the two");
    // The mask's backups too.
    var dir = NewDir("fix-backup-mask");
    File.Copy(Fixture("clear64.bin"), Path.Combine(dir, "clearmask.bin"));
    w.Run($"mask load {Path.Combine(dir, "clearmask.bin")} confirm");
    w.Run($"mask load {Path.Combine(dir, "clearmask.bin")} confirm");
    var masks = Directory.GetFiles(w.Save, "clearmask-before-*.bin").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    C(masks.SequenceEqual(["clearmask-before-2026-10-13-123005-000-2.bin", "clearmask-before-2026-10-13-123005-000.bin"]), "so are the mask's: " + string.Join(", ", masks));
    // The stamp is to the millisecond.
    var clock = new DateTime(2026, 10, 13, 12, 30, 5, 250);
    LakeCommands.Seams.Now = () => clock;
    w.Run("remove all confirm");
    C(File.Exists(Path.Combine(w.Save, "lakes-before-2026-10-13-123005-250.txt")), "and the name carries the milliseconds");
    // UniquePath on its own.
    var folder = NewDir("fix-unique");
    var first = LakeFiles.UniquePath(folder, "x-", "stamp", ".txt");
    File.WriteAllText(first, "a");
    var second = LakeFiles.UniquePath(folder, "x-", "stamp", ".txt");
    File.WriteAllText(second, "b");
    C(Path.GetFileName(first) == "x-stamp.txt" && Path.GetFileName(second) == "x-stamp-2.txt" && Path.GetFileName(LakeFiles.UniquePath(folder, "x-", "stamp", ".txt")) == "x-stamp-3.txt", "UniquePath: the name, then -2, then -3");
  }

  // ---- 3. a lake edited while a client joins -----------------------------------------------------------------------------------------------------------

  private static void FixJoinCatchUpTest()
  {
    Section("fixes: a lake edited while a client joins reaches it when its join is over");
    var saved = (BC.Settings, LakeTransfer.LakesChanged);
    try
    {
      var a = SampleLakes(3, 0.5f);
      BC.Settings = World(a, SampleMask());
      var link = new StubLink();
      var client = new BakedTransfer.LayerClient("alice", link, _ => { });
      // The package is made (it holds set a); a lake is added while the client downloads it; the join ends.
      LakeTransfer.NoteSerialized(client);
      var b = a.Added(new Lake("late", 5, 6, 70, 0, 0, 20, 20, 4));
      BC.Settings.ReplaceLakes(b);
      client.JoinDone = true;
      C(LakeTransfer.CatchUp(client) && link.Sent.Count == 1 && link.Sent[0].Method == LakeTransfer.RpcLakes, "a set that changed during the join is sent when it is over");
      var got = LakeTransfer.Unpack(new ZPackage(((ZPackage)link.Sent[0].Parameters[0]).GetArray()));
      C(got.Lakes.SequenceEqual(b.Lakes) && got.Revision == b.Revision, "and it is the current set");
      C(!LakeTransfer.CatchUp(client) && link.Sent.Count == 1, "only once (the note is spent)");

      // Nothing changed: nothing sent.
      var quiet = new StubLink();
      var quietClient = new BakedTransfer.LayerClient("bob", quiet, _ => { }) { JoinDone = true };
      LakeTransfer.NoteSerialized(quietClient);
      C(!LakeTransfer.CatchUp(quietClient) && quiet.Sent.Count == 0, "a set that did not change sends nothing");
      // A client that left, or never had a package noted.
      var gone = new StubLink { Connected = false };
      var goneClient = new BakedTransfer.LayerClient("carol", gone, _ => { }) { JoinDone = true };
      LakeTransfer.NoteSerialized(goneClient);
      BC.Settings.ReplaceLakes(b.WithDepthScale(1.5f));
      C(!LakeTransfer.CatchUp(goneClient) && gone.Sent.Count == 0, "a client that left is sent nothing");
      var strangerLink = new StubLink();
      C(!LakeTransfer.CatchUp(new BakedTransfer.LayerClient("dave", strangerLink, _ => { }) { JoinDone = true }) && strangerLink.Sent.Count == 0, "nor one whose package was not made here");
      // The first lake of a world that had none, added during the join; and a depth change alone.
      BC.Settings = World(null, null);
      var lateLink = new StubLink();
      var late = new BakedTransfer.LayerClient("erin", lateLink, _ => { });
      LakeTransfer.NoteSerialized(late);
      BC.Settings.ReplaceLakes(a);
      late.JoinDone = true;
      C(LakeTransfer.CatchUp(late) && lateLink.Sent.Count == 1, "the first lake of a world that had none, made during the join, is sent");
      var depthLink = new StubLink();
      var depth = new BakedTransfer.LayerClient("fred", depthLink, _ => { });
      LakeTransfer.NoteSerialized(depth);
      BC.Settings.SetLakeDepthScale(2f);
      depth.JoinDone = true;
      C(LakeTransfer.CatchUp(depth) && LakeTransfer.Unpack(new ZPackage(((ZPackage)depthLink.Sent[0].Parameters[0]).GetArray())).DepthScale == 2f, "a new depth scale made during the join is sent");
      // Without a push for a client still joining (the push skips it), the note makes it whole: the broadcast itself skips it.
      BakedTransfer.Clients.Clear();
      var joining = new StubLink();
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("gina", joining, _ => { }) { JoinDone = false });
      LakeTransfer.Broadcast(a);
      C(joining.Sent.Count == 0, "(the broadcast itself still skips a client that is joining)");
    }
    finally
    {
      BakedTransfer.Clients.Clear();
      (BC.Settings, LakeTransfer.LakesChanged) = saved;
    }
  }

  // The join's own code calls them, in the right places (read from the game-side IL of the join, as the placement tests read its stages).
  private static void FixJoinWiringTest()
  {
    Section("fixes: the join notes the lakes its package holds, and catches up once it is over");
    var machine = typeof(BC.ZNetPatch).GetNestedTypes(BindingFlags.NonPublic).First(t => t.Name.StartsWith("<SendSettings>"));
    var moveNext = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    var instructions = HarmonyLib.PatchProcessor.GetOriginalInstructions(moveNext);
    int Call(string type, string name)
    {
      for (int i = 0; i < instructions.Count; i++)
        if ((instructions[i].opcode == System.Reflection.Emit.OpCodes.Call || instructions[i].opcode == System.Reflection.Emit.OpCodes.Callvirt) && instructions[i].operand is MethodBase m && m.Name == name && m.DeclaringType?.Name == type)
          return i;
      return -1;
    }
    int serialize = Call("BetterContinentsSettings", "Serialize"), note = Call("LakeTransfer", "NoteSerialized"), send = Call("BakedTransfer", "SendPackets"),
      finished = Call("BakedTransfer", "JoinFinished"), catchUp = Call("LakeTransfer", "CatchUp");
    C(serialize >= 0 && note > serialize && note < send, $"the lakes are noted right after the package is made, before it is sent ({serialize} < {note} < {send})");
    C(finished >= 0 && catchUp > finished, $"and the catch-up comes right after the layer's JoinFinished ({finished} < {catchUp})");
    C(instructions.Count(i => i.operand is MethodBase m && m.Name == "CatchUp") == 1 && instructions.Count(i => i.operand is MethodBase m && m.Name == "NoteSerialized") == 1, "each once");
  }

  // ---- 4. numbers that are not numbers -------------------------------------------------------------------------------------------------------------------

  private static void FixNonFiniteTest()
  {
    Section("fixes: a lake with a number that is not finite is never written, never added, and does not make the list unreadable");
    var good = new Lake("good", 10, 20, 60, 0, 0, 50, 50, 5);
    var nan = new Lake("nan", float.NaN, 20, 60, 0, 0, 50, 50, 5);
    var inf = new Lake("inf", 10, 20, float.PositiveInfinity, 0, 0, 50, 50, 5);
    var set = new LakeSet([good, nan, good with { Name = "good2" }, inf], 0.5f, 0);
    byte[] blob = null;
    var log = LogHandler.During(() => blob = set.ToBlock());
    C(log.Any(l => l.StartsWith("[Warning]") && l.Contains("2 lake(s) with a number that is not finite are left out") && l.Contains("nan")), "ToBlock leaves them out with a word: " + string.Join(" | ", log.Where(l => l.Contains("finite"))));
    var back = LakeSet.FromBlock(blob);
    C(back.Lakes.Select(l => l.Name).SequenceEqual(["good", "good2"]) && BitConverter.ToInt32(blob, 8) == 2, "and what is written holds the other two (the count says so)");
    C(RefusesLake(() => new LakeSet([good], float.NaN, 0).ToBlock(), "depth scale"), "a depth scale that is not a number is refused");
    // The world's settings: a set with such a lake saves and reads back, readable.
    var world = World(set);
    var read = BC.BetterContinentsSettings.Load(new ZPackage(SettingsBytes(world, false)));
    C(read.Lakes != null && read.LakesError == null && read.Lakes.Lakes.Count == 2, "a world holding one saves a list the next start reads (two lakes), not an unreadable one");
    // An old build's blob with one: dropped on read, with a word, the rest reads (also through Load).
    var old = new List<byte>();
    old.AddRange(BitConverter.GetBytes(1));
    old.AddRange(BitConverter.GetBytes(0.5f));
    old.AddRange(BitConverter.GetBytes(2));
    foreach (var l in new[] { nan, good })
    {
      var name = System.Text.Encoding.UTF8.GetBytes(l.Name);
      old.Add((byte)name.Length);
      old.AddRange(name);
      foreach (var v in new[] { l.X, l.Z, l.Level, l.MinX, l.MinZ, l.MaxX, l.MaxZ, l.Depth })
        old.AddRange(BitConverter.GetBytes(v));
    }
    var pkgBytes = SettingsBytes(World(), false).Concat(BitConverter.GetBytes(81)).Concat(BitConverter.GetBytes(old.Count)).Concat(old).ToArray();
    BC.BetterContinentsSettings loaded = null;
    var loadLog = LogHandler.During(() => loaded = BC.BetterContinentsSettings.Load(new ZPackage(pkgBytes)));
    C(loaded.LakesError == null && loaded.Lakes?.Lakes.Select(l => l.Name).SequenceEqual(["good"]) == true && loadLog.Any(l => l.Contains("'nan'") && l.Contains("not finite")), "an older build's blob with a NaN lake reads: that lake is left out with a line in the log, the other stays");
    // Valid: what a command may add.
    C(LakeSet.Valid(good, out _) && !LakeSet.Valid(nan, out var why1) && why1.Contains("not finite") && !LakeSet.Valid(inf, out _), "Valid: finite numbers");
    C(!LakeSet.Valid(good with { MaxX = 0 }, out var why2) && why2.Contains("empty") && !LakeSet.Valid(good with { X = 99 }, out var why3) && why3.Contains("outside") && !LakeSet.Valid(good with { Level = 29f }, out var why4) && why4.Contains("under the sea")
      && !LakeSet.Valid(good with { Name = " " }, out _) && !LakeSet.Valid(good with { Name = new string('n', 65) }, out _), "an empty box, a point outside it, a level under the sea, a nameless or overlong lake: not valid");
    // here and at refuse such a lake and add nothing.
    using var w = new CmdWorld(CmdSettings(false, false));
    foreach (var (bad, what) in new[] { (nan, "NaN"), (inf, "infinity"), (good with { MaxZ = 0 }, "an empty box") })
    {
      w.Spill = (x, z, r, m) => new LakeCommands.HereOutcome(bad, "level ...");
      var pushes = w.Pushes;
      var said = w.Run("here");
      C(said.Contains("cannot be used") && BC.Settings.Lakes == null && w.Pushes == pushes && w.Changed.Count == 0, $"here with a spill that made {what}: refused, nothing added or pushed: " + said);
    }
    // depth: the scale is a number from 0 to 3 in the code as well as the parser.
    bool threw = false;
    try
    {
      BC.Settings.SetLakeDepthScale(float.NaN);
    }
    catch (ArgumentOutOfRangeException)
    {
      threw = true;
    }
    C(threw && BC.Settings.Lakes == null, "SetLakeDepthScale(NaN) is refused, and the world is as it was");
    C(LakeSet.Line(new Lake("#cold", 1, 2, 60, 0, 0, 9, 9, 3)).StartsWith("_#cold;") && LakeSet.ParseLines([LakeSet.Line(new Lake("#cold", 1, 2, 60, 0, 0, 9, 9, 3))], _ => { }).Lakes.Count == 1, "a name that starts with '#' is not written as a comment");
  }

  // ---- 6. a damaged blob is always the format exception -----------------------------------------------------------------------------------------------

  private static void FixDamagedBlobTest()
  {
    Section("fixes: a damaged blob is always LakeFormatException, so the world plays without lakes");
    var lakes = SampleLakes(5);
    var blob = lakes.ToBlock();
    int nameAt = 12;   // the first lake's name length
    foreach (var (bytes, what) in new[]
             {
               (new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, "a length of six bytes"),
               (new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 }, "a length of two billion"),
               (new byte[] { 0xE8, 0x07 }, "a length of 1,000 over what is left"),
               (new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 }, "a length that never ends"),
             })
    {
      var damaged = blob.Take(nameAt).Concat(bytes).Concat(blob.Skip(nameAt + 1)).ToArray();
      bool format = false;
      string other = "";
      try
      {
        LakeSet.FromBlock(damaged);
      }
      catch (LakeFormatException)
      {
        format = true;
      }
      catch (Exception e)
      {
        other = e.GetType().Name;
      }
      C(format && other == "", $"{what}: the format exception {other}");
    }
    // Through the settings: the world is still a Better Continents world, without lakes, and the bytes are kept.
    var damagedAt = blob.Take(nameAt).Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }).Concat(blob.Skip(nameAt + 1)).ToArray();
    var package = SettingsBytes(World(), false).Concat(BitConverter.GetBytes(81)).Concat(BitConverter.GetBytes(damagedAt.Length)).Concat(damagedAt).ToArray();
    BC.BetterContinentsSettings read = null;
    var log = LogHandler.During(() => read = BC.BetterContinentsSettings.Load(new ZPackage(package)));
    C(read.EnabledForThisWorld && read.Lakes == null && read.LakesError != null && SettingsBytes(read, false).SequenceEqual(package) && log.Any(l => l.Contains("Failed to read the lakes")),
      "Load: a Better Continents world that plays without lakes (LakesError), its bytes kept, a line in the log");
    // Every kind of damage, by chance: never another exception.
    var rng = new System.Random(21);
    int bad = 0, formatErrors = 0, readFine = 0;
    for (int i = 0; i < 4000; i++)
    {
      var copy = (byte[])blob.Clone();
      int flips = 1 + rng.Next(4);
      for (int f = 0; f < flips; f++)
        copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
      if (rng.Next(5) == 0)
        Array.Resize(ref copy, rng.Next(copy.Length));
      try
      {
        LakeSet.FromBlock(copy, _ => { });
        readFine++;
      }
      catch (LakeFormatException)
      {
        formatErrors++;
      }
      catch (Exception)
      {
        bad++;
      }
    }
    C(bad == 0, $"4000 damaged lake blobs: {formatErrors} refused as the format exception, {readFine} read, {bad} threw something else");
    var maskBlob = SampleMask().ToBlock();
    bad = 0;
    int refused = 0;
    for (int i = 0; i < 4000; i++)
    {
      var copy = (byte[])maskBlob.Clone();
      for (int f = 0, flips = 1 + rng.Next(4); f < flips; f++)
        copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
      if (rng.Next(5) == 0)
        Array.Resize(ref copy, rng.Next(copy.Length));
      try
      {
        ClearMask.FromBlock(copy).Verify();
      }
      catch (LakeFormatException)
      {
        refused++;
      }
      catch (Exception)
      {
        bad++;
      }
    }
    C(bad == 0, $"4000 damaged mask blobs: {refused} refused as the format exception, {bad} threw something else");
    // A pushed package with a damaged name is the format exception too.
    var pushed = LakeTransfer.Pack(lakes).GetArray();
    var packed = ReadPacked(pushed);
    C(RefusesLake(() => LakeTransfer.Unpack(new ZPackage(new byte[] { 1, 2, 3 })), "cut short") && packed.Length > 0, "and so is a pushed package that is cut");
  }

  // ---- 5. the conversion's package ---------------------------------------------------------------------------------------------------------------------

  private static void FixConvertBroadcastTest()
  {
    Section("fixes: the conversion does not broadcast a package over what a reliable message holds");
    var savedSettings = BC.Settings;
    var savedPatch = BakedConvert.Patch;
    var savedClients = BakedTransfer.Clients.ToList();
    try
    {
      BakedConvert.Patch = () => { };
      BakedTransfer.Clients.Clear();
      var link = new StubLink();
      BakedTransfer.Clients.Add(new BakedTransfer.LayerClient("alice", link, _ => { }) { JoinDone = true });
      // A small mask and a few lakes: sent to the player in the world.
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      int sent = new BakedConvert().Convert(SampleLakes(), SampleMask());
      C(sent == 1 && link.Sent.Count == 1 && link.Sent[0].Method == BakedTransfer.RpcSettingsUpdate && BC.Settings.GameTerrain, "a package that fits one message is sent, to the one player in the world");
      // A mask of noise: 2 MB that do not pack.
      var rng = new System.Random(5);
      var bits = new byte[16 + ClearMask.RawLengthOf(4096)];
      System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(bits, 0);
      BitConverter.GetBytes(4096).CopyTo(bits, 8);
      BitConverter.GetBytes(10500f).CopyTo(bits, 12);
      var noise = new byte[bits.Length - 16];
      rng.NextBytes(noise);
      noise.CopyTo(bits, 16);
      var big = ClearMask.FromTarnFile(bits);
      C(big.ToBlock().Length > BakedConvert.MaxBroadcastBytes, $"(the noise mask packs to {big.ToBlock().Length / 1024:N0} KB, over the {BakedConvert.MaxBroadcastBytes / 1024} KB)");
      link.Sent.Clear();
      BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = false };
      var log = LogHandler.During(() => sent = new BakedConvert().Convert(null, big));
      C(sent == -1 && link.Sent.Count == 0 && BC.Settings.GameTerrain && ReferenceEquals(BC.Settings.Clearing, big) && log.Any(l => l.Contains("too many for one message") && l.Contains("leave and join again")),
        "a package over 400 KB is not sent; the world is converted all the same, and the log says the players must join again: " + string.Join(" | ", log.Where(l => l.Contains("message"))));
    }
    finally
    {
      BakedConvert.Patch = savedPatch;
      BC.Settings = savedSettings;
      BakedTransfer.Clients.Clear();
      BakedTransfer.Clients.AddRange(savedClients);
    }
    // The command says it.
    var dir = NewDir("fix-convert");
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(dir, "lakes.txt"));
    using (var v = new CmdWorld(new BC.BetterContinentsSettings { EnabledForThisWorld = false }))
    {
      LakeCommands.Seams.Kind = () => BakeWorldKind.Vanilla;
      LakeCommands.Seams.MapDirectory = () => dir;
      v.ConvertSent = -1;
      var said = v.Run("load confirm convert");
      C(said.Contains("is a Better Continents world now") && said.Contains("must leave and join again") && !said.Contains("were sent"), "bc_lake load ... convert says the players must join again: " + said.Replace("\n", " | "));
      v.ConvertSent = 3;
      C(v.Run("load confirm convert").Contains("3 players in it were sent the settings"), "and how many were sent otherwise");
    }
  }

  // ---- 7. the clearing postfix runs first ---------------------------------------------------------------------------------------------------------------

  private static readonly List<bool> Seen = [];

  // Of the shape of the twin guard's postfix on the same method: it takes __result and returns a bool (Harmony runs the postfixes that return a value after the ones that
  // return nothing, whatever their priorities, and orders those among themselves by priority).
  private static bool SpyPostfix(bool __result)
  {
    Seen.Add(__result);
    return __result;
  }

  private static void FixPostfixOrderTest()
  {
    Section("fixes: the clearing postfix answers before the twin guard's (Priority.First), whatever order they were patched in");
    var target = InsideClearAreaMethod();
    var mask = SampleMask();
    var toggles = ClearingToggles();
    var spy = typeof(Tests).GetMethod(nameof(SpyPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
    var ours = AccessTools.Method(typeof(ClearVegetation), nameof(ClearVegetation.InsideClearAreaPostfix));
    var saved = (BC.Settings, BC.HarmonyInstance);
    var harmony = new Harmony("lake-tests.order");
    BC.HarmonyInstance = harmony;
    try
    {
      var zoneSystem = (ZoneSystem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZoneSystem));
      var areaType = typeof(ZoneSystem).GetNestedType("ClearArea", BindingFlags.NonPublic)!;
      var none = Activator.CreateInstance(typeof(List<>).MakeGenericType(areaType))!;
      var covered = mask.CentreOf(0, 0);
      bool Ask() => (bool)target.Invoke(zoneSystem, [none, new Vector3(covered.X, 30f, covered.Z)])!;
      BC.Settings = World(null, mask);
      // The twin guard's place: a postfix at the default priority, patched first (it is, in DynamicPatch: its toggle comes before the layer's and ours).
      harmony.Patch(target, postfix: new HarmonyMethod(spy));
      UpdateToggle(toggles[0], BC.Settings);
      Seen.Clear();
      var answer = Ask();
      C(answer && Seen.SequenceEqual([true]), $"the toggle's postfix runs before one patched earlier at the default priority: that one sees the cleared spot as already true ({string.Join(",", Seen)})");
      var patches = Harmony.GetPatchInfo(target).Postfixes.ToList();
      C(patches.Count == 2 && patches.Single(p => p.PatchMethod.Name == "InsideClearAreaPostfix").priority == Priority.First && patches.Single(p => p.PatchMethod.Name == "SpyPostfix").priority == Priority.Normal,
        "Harmony holds it at Priority.First against the other's Normal: " + string.Join(", ", patches.Select(p => $"{p.PatchMethod.Name} {p.priority}")));
      // Patched again (the toggle off and on, as a settings change does), the order holds.
      UpdateToggle(toggles[0], World());
      UpdateToggle(toggles[0], BC.Settings);
      Seen.Clear();
      Ask();
      C(Seen.SequenceEqual([true]), "also after the toggle was switched off and on again");
      // The control that shows what the priority is for: the same postfix at the default priority, patched after, runs last.
      UpdateToggle(toggles[0], World());
      harmony.UnpatchAll(harmony.Id);
      harmony.Patch(target, postfix: new HarmonyMethod(spy));
      harmony.Patch(target, postfix: new HarmonyMethod(ours));
      Seen.Clear();
      Ask();
      C(Seen.SequenceEqual([false]), $"(control: without the priority it would run after, and the guard would have seen the spot as free: {string.Join(",", Seen)})");
    }
    finally
    {
      foreach (var toggle in toggles)
        UpdateToggle(toggle, World());
      harmony.UnpatchAll(harmony.Id);
      (BC.Settings, BC.HarmonyInstance) = saved;
    }
    // The real postfixes on the method (the vegetation map and twin guard's, the baked layer's), as the game patches them: ours is held above both.
    var production = new Harmony("lake-tests.order.production");
    BC.HarmonyInstance = production;
    try
    {
      var twin = AccessTools.Method(typeof(BC).GetNestedType("ZoneSystemPatch", BindingFlags.NonPublic)!, "InsideClearAreaPostfix");
      var baked = AccessTools.Method(typeof(BakedVegetation), nameof(BakedVegetation.InsideClearAreaPostfix));
      production.Patch(target, postfix: new HarmonyMethod(twin));
      production.Patch(target, postfix: new HarmonyMethod(baked));
      BC.Settings = World(null, mask);
      UpdateToggle(toggles[0], BC.Settings);
      var held = Harmony.GetPatchInfo(target).Postfixes.ToList();
      var ourPriority = held.Single(p => p.PatchMethod.Name == "InsideClearAreaPostfix" && p.PatchMethod.DeclaringType == typeof(ClearVegetation)).priority;
      C(held.Count == 3 && ourPriority == Priority.First && held.Where(p => p.PatchMethod.DeclaringType != typeof(ClearVegetation)).All(p => p.priority < ourPriority),
        "against the twin guard's postfix and the baked layer's, as the game patches them: " + string.Join(", ", held.Select(p => $"{p.PatchMethod.DeclaringType!.Name}.{p.PatchMethod.Name} {p.priority}")));
    }
    finally
    {
      foreach (var toggle in toggles)
        UpdateToggle(toggle, World());
      production.UnpatchAll(production.Id);
      (BC.Settings, BC.HarmonyInstance) = saved;
    }
  }

  // ---- 8. the smallest pixel -----------------------------------------------------------------------------------------------------------------------------

  private static void FixPixelFloorTest()
  {
    Section("fixes: a mask whose pixels are absurdly small is refused");
    C(RefusesLake(() => ClearMask.FromTarnFile(MakeClearFile(1000, 4f, (r, c) => false)), "0.008 m", "under the 1 cm", "mistyped"), "1000 px over +-4 m (8 mm a pixel): refused, and the half-extent is said to look mistyped");
    C(ClearMask.FromTarnFile(MakeClearFile(1000, 5f, (r, c) => false)).MetresPerPixel == 0.01f, "1 cm a pixel is the least that is taken");
    C(ClearMask.FromTarnFile(MakeClearFile(255, 3f, (r, c) => (r + c) % 7 == 0)).Size == 255, "the 255 px / 3 m mask (2.3 cm a pixel) of the area test still is");
    C(RefusesLake(() => ClearMask.FromTarnFile(MakeClearFile(64, 2e7f, (r, c) => false)), "half-extent", "more than"), "a half-extent of 20,000 km: refused");
    C(ClearMask.FromTarnFile(MakeClearFile(64, 1e7f, (r, c) => false)).Extent == 1e7f, "10,000 km is taken");
    // A stored blob the same way (a hand-made header).
    var blob = SampleMask().ToBlock().ToArray();
    BitConverter.GetBytes(0.1f).CopyTo(blob, 8);
    C(RefusesLake(() => ClearMask.FromBlock(blob), "under the 1 cm"), "a blob with such a header is refused when read");
    var package = SettingsBytes(World(), false).Concat(BitConverter.GetBytes(82)).Concat(BitConverter.GetBytes(blob.Length)).Concat(blob).ToArray();
    var read = BC.BetterContinentsSettings.Load(new ZPackage(package));
    C(read.Clearing == null && read.ClearingError != null && read.ClearingError.Contains("1 cm") && SettingsBytes(read, false).SequenceEqual(package), "in the settings: ClearingError, the bytes kept");
    // A Directory file: an error naming the file, the world made without it.
    var dir = NewDir("fix-pixels");
    File.WriteAllBytes(Path.Combine(dir, "clearmask.bin"), MakeClearFile(1000, 4f, (r, c) => false));
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12 };
    var log = LogHandler.During(() => s.ReadLakeFiles(dir));
    C(s.Clearing == null && log.Any(l => l.StartsWith("[Error]") && l.Contains("clearmask.bin") && l.Contains("under the 1 cm") && l.Contains("made without it")), "a Directory's file: an error naming it, the world made without a mask");
    // The cost at the floor: a lookup stays small.
    var rng = new System.Random(3);
    var edge = ClearMask.FromTarnFile(MakeClearFile(2048, 10.24f, (r, c) => rng.Next(50) == 0));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    int hits = 0;
    for (int i = 0; i < 20000; i++)
      hits += edge.Covers(RandomFloat(rng, 10f), RandomFloat(rng, 10f)) ? 1 : 0;
    double us = watch.Elapsed.TotalMilliseconds * 1000 / 20000;
    System.Console.WriteLine($"   at the floor (2048 px over +-10.24 m, 1 cm a pixel): {us:0.0} us a lookup");
    C(us < 100, $"a lookup at the floor takes {us:0.0} us (under 100)");
  }

  // ---- 9, 10. import keeps the depth scale; the dry run shows what the parser left out -------------------------------------------------------------

  private static void FixImportScaleTest()
  {
    Section("fixes: import and load keep the world's depth scale, and the dry run shows the lines the parser left out");
    var config = NewDir("fix-import-config");
    var tarn = Path.Combine(config, "Tarn");
    Directory.CreateDirectory(tarn);
    File.Copy(Fixture("lakes-tarn.txt"), Path.Combine(tarn, "Raventest.txt"));
    File.WriteAllText(Path.Combine(config, "com.raveniron.tarn.cfg"), "[Look]\nDepthScale = 0.8\n");
    var old = LakeFiles.ConfigPath;
    LakeFiles.ConfigPath = () => config;
    try
    {
      using (var w = new CmdWorld(CmdSettings(true, false)))
      {
        BC.Settings.ReplaceLakes(BC.Settings.Lakes.WithDepthScale(1.2f));
        var dry = w.Run("import");
        C(dry.Contains("The depth scale stays the world's 1.2 (Tarn's own is 0.8; 'bc_lake depth 0.8' takes it)"), "the dry run says the world's tuned scale stays: " + dry.Replace("\n", " | "));
        C(w.Said.Any(l => l.Contains("Raventest.txt: line 6: 4 fields")) && w.Said.Any(l => l.Contains("Raventest.txt: line 8: field 4 'sixty'")) && w.Said.Any(l => l.Contains("line 9:") && l.Contains("under 30.5")) && w.Said.Any(l => l.Contains("line 10:") && l.Contains("empty box")),
          "and lists what the parser left out or found odd, with the lines");
        w.Run("import confirm");
        C(BC.Settings.Lakes.DepthScale == 1.2f && BC.Settings.Lakes.Lakes.Count == 5, "import confirm keeps the scale (1.2), not Tarn's");
        C(w.Said.Any(l => l.Contains("line 6:")), "the confirmed run shows the lines too");
      }
      using (var fresh = new CmdWorld(CmdSettings(false, false)))
      {
        var dry = fresh.Run("import");
        C(dry.Contains("The depth scale will be 0.8 (Tarn's own)"), "a world with no lakes takes Tarn's own scale, and the dry run says so: " + dry.Replace("\n", " | "));
        fresh.Run("import confirm");
        C(BC.Settings.Lakes.DepthScale == 0.8f, "which it gets");
      }
      using (var tuned = new CmdWorld(CmdSettings(false, false)))
      {
        BC.Settings.SetLakeDepthScale(1.7f);
        tuned.Run("import confirm");
        C(BC.Settings.Lakes.DepthScale == 1.7f && BC.Settings.Lakes.Lakes.Count == 5, "a world that holds only a tuned scale (no lakes) keeps it too");
      }
      // load: the notes, and a long list cut.
      var dir = NewDir("fix-load-notes");
      File.WriteAllLines(Path.Combine(dir, "lakes.txt"), new[] { "good;1;2;60;0;0;9;9;3" }.Concat(Enumerable.Range(0, 14).Select(i => "bad" + i + ";x")));
      using var l = new CmdWorld(CmdSettings(true, false));
      var loadDry = l.Run($"load {dir}");
      C(l.Said.Count(line => line.Contains("lakes.txt: line ")) == 10 && loadDry.Contains("and 4 more such lines") && loadDry.Contains("The depth scale stays the world's 0.5"), "load: ten of the lines the parser left out, then 'and 4 more', and the depth scale: " + loadDry.Replace("\n", " | "));
      var fresh2 = new CmdWorld(CmdSettings(false, false));
      LakeCommands.Seams.MapDirectory = () => dir;
      C(fresh2.Run("load").Contains("The depth scale will be 0.5 (the config's)"), "a world with no lakes: the config's scale");
      fresh2.Dispose();
    }
    finally
    {
      LakeFiles.ConfigPath = old;
    }
  }

  // ---- 11. files are put in place whole ------------------------------------------------------------------------------------------------------------------

  private static void FixAtomicWriteTest()
  {
    Section("fixes: a file is put in place whole");
    var dir = NewDir("fix-atomic");
    var lakes = Path.Combine(dir, "lakes.txt");
    LakeFiles.WriteLakes(lakes, SampleLakes(3));
    var before = File.ReadAllBytes(lakes);
    LakeFiles.WriteLakes(lakes, SampleLakes(4));
    var after = File.ReadAllLines(lakes);
    C(!before.SequenceEqual(File.ReadAllBytes(lakes)) && LakeSet.ParseLines(after, _ => { }).Lakes.Count == 4 && Directory.GetFiles(dir).Length == 1, "a file written over another is the new one, and no .tmp is left");
    var mask = Path.Combine(dir, "clearmask.bin");
    LakeFiles.WriteClearing(mask, SampleMask());
    LakeFiles.WriteClearing(mask, SampleMask());
    C(File.ReadAllBytes(mask).SequenceEqual(SampleMask().ToTarnFile()) && Directory.GetFiles(dir).Length == 2, "the mask too");
    // A write that fails leaves the old file and no .tmp.
    var blocked = Path.Combine(dir, "blocked.txt");
    Directory.CreateDirectory(blocked);
    bool threw = false;
    try
    {
      LakeFiles.WriteLakes(blocked, SampleLakes(1));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
      threw = true;
    }
    C(threw && Directory.Exists(blocked) && !File.Exists(blocked + ".tmp"), "a write that cannot be made leaves what was there and no .tmp");
  }
}

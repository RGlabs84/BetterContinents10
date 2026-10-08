// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterContinents;

// What a bc_bake line asks for (11.1).
internal enum BakeVerb
{
  Help,
  Info,
  Stats,
  Hide,
  Show,
  List,
  Check,
  Area,
  Box,
  Unbake,
  Undo,
  Load,
  Export,
  Orphans,
  Drop,
  // `bc_bake drop <world>`: in the main menu, on a world that is not loaded.
  DropWorld,
  Server,
}

// A parsed line. Error is set, and nothing else means anything, when the line is not understood.
internal sealed class BakeCommand
{
  public BakeVerb Verb { get; init; }
  public string? Error { get; init; }
  public BakeWords Words { get; init; }
  // Area, Box and Unbake: a circle (Radius, around At when it is given, else around the one who asks), a box, or the world.
  public bool IsBox { get; init; }
  public bool IsWorld { get; init; }
  public float Radius { get; init; }
  public float? AtX { get; init; }
  public float? AtZ { get; init; }
  public float X1 { get; init; }
  public float Z1 { get; init; }
  public float X2 { get; init; }
  public float Z2 { get; init; }
  // Undo <n>; Drop bake <n>.
  public int? Number { get; init; }
  // Drop compiler.
  public bool Compiler { get; init; }
  // Load and Export: the file, as typed (null: the default one). DropWorld: the world's name. Server: the rest of the line.
  public string? Text { get; init; }
  // Check: `all`, or a zone.
  public bool CheckAll { get; init; }
  public int? ZoneX { get; init; }
  public int? ZoneZ { get; init; }
  // Orphans: `destroy`, and `items` (the ones that hold items too).
  public bool Destroy { get; init; }
  public bool Items { get; init; }

  // The area the command covers, with the caller's place for a circle that names none; null for a world, or when a circle has no place.
  public BakeArea? AreaFor(float? callerX, float? callerZ)
  {
    if (IsWorld)
      return null;
    if (IsBox)
      return BakeArea.OfBox(X1, Z1, X2, Z2);
    float? x = AtX ?? callerX, z = AtZ ?? callerZ;
    return x == null || z == null ? null : BakeArea.OfCircle(x.Value, z.Value, Radius);
  }

  // The words that make the command again, for the dry run's "'bc_bake <this> confirm' bakes".
  public string Echo(bool at) => IsWorld ? "world" : IsBox ? $"box {Fmt(X1)} {Fmt(Z1)} {Fmt(X2)} {Fmt(Z2)}"
    : $"{Fmt(Radius)}" + (at && AtX != null && AtZ != null ? $" at {Fmt(AtX.Value)} {Fmt(AtZ.Value)}" : "");

  private static string Fmt(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

internal static class BakeCommandLine
{
  public const string Usage = "bc_bake help | info | stats | hide | show | list | check [all | <zx> <zz>] | area <radius> [at <x> <z>] [town] [any] [convert] [confirm] | "
    + "box <x1> <z1> <x2> <z2> [town] [any] [convert] [confirm] | unbake <radius> [at <x> <z>] | box <x1> <z1> <x2> <z2> | world [all] [confirm] | undo [<n>] [confirm] | "
    + "load [<file>] [convert] [confirm] | export [<file>] | orphans [destroy] [items] [confirm] | drop compiler | bake <n> [confirm] | drop <world> [confirm] | server <the same>";

  private static readonly HashSet<string> Flags = ["town", "any", "convert", "confirm", "all"];

  private static BakeCommand Fail(string message) => new() { Verb = BakeVerb.Help, Error = message };

  public static BakeCommand Parse(string text)
  {
    text = (text ?? "").Trim();
    var all = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    if (all.Length == 0)
      return new BakeCommand { Verb = BakeVerb.Help };
    var verb = all[0].ToLowerInvariant();
    var rest = all.Skip(1).ToList();
    var low = rest.Select(w => w.ToLowerInvariant()).ToList();
    // The words that change what a command does come out of the line wherever they stand; what is left is its arguments.
    var flags = new HashSet<string>(low.Where(Flags.Contains));
    var args = low.Where(w => !Flags.Contains(w)).ToList();
    var words = new BakeWords { Town = flags.Contains("town"), Any = flags.Contains("any"), Convert = flags.Contains("convert"), Confirm = flags.Contains("confirm"), All = flags.Contains("all") };
    switch (verb)
    {
      case "help":
      case "?":
        return new BakeCommand { Verb = BakeVerb.Help };
      case "info":
      case "stats":
      case "hide":
      case "show":
      case "list":
        if (args.Count > 0 || flags.Count > 0)
          return Fail($"bc_bake {verb}: takes no more words.");
        return new BakeCommand { Verb = verb switch { "info" => BakeVerb.Info, "stats" => BakeVerb.Stats, "hide" => BakeVerb.Hide, "show" => BakeVerb.Show, _ => BakeVerb.List } };
      case "check":
        {
          // Only `all` is a word of this command, which is also one of the flags.
          if (low.Count == 0)
            return new BakeCommand { Verb = BakeVerb.Check };
          if (low.Count == 1 && low[0] == "all")
            return new BakeCommand { Verb = BakeVerb.Check, CheckAll = true };
          if (low.Count == 2 && Int(low[0], out int zx) && Int(low[1], out int zz))
            return new BakeCommand { Verb = BakeVerb.Check, ZoneX = zx, ZoneZ = zz };
          return Fail("bc_bake check: 'all', or a zone as two numbers (check 3 -2).");
        }
      case "area":
        {
          var cmd = ParseCircle(args, out var error, out var radius, out var at);
          if (cmd == null)
            return Fail($"bc_bake area: {error} (area <radius> [at <x> <z>]).");
          var bad = flags.FirstOrDefault(f => f == "all");
          if (bad != null)
            return Fail("bc_bake area: 'all' does not go with it.");
          return new BakeCommand { Verb = BakeVerb.Area, Words = words, Radius = radius, AtX = at?.X, AtZ = at?.Z };
        }
      case "box":
        {
          if (!Box(args, out var x1, out var z1, out var x2, out var z2))
            return Fail("bc_bake box: four numbers, two corners (box <x1> <z1> <x2> <z2>).");
          if (flags.Contains("all"))
            return Fail("bc_bake box: 'all' does not go with it.");
          return new BakeCommand { Verb = BakeVerb.Box, Words = words, IsBox = true, X1 = x1, Z1 = z1, X2 = x2, Z2 = z2 };
        }
      case "unbake":
        {
          if (flags.Contains("town") || flags.Contains("any") || flags.Contains("convert"))
            return Fail("bc_bake unbake: only 'all' and 'confirm' go with it.");
          if (args.Count == 1 && args[0] == "world")
            return new BakeCommand { Verb = BakeVerb.Unbake, Words = words, IsWorld = true };
          if (args.Count >= 1 && args[0] == "box")
          {
            if (!Box(args.Skip(1).ToList(), out var x1, out var z1, out var x2, out var z2))
              return Fail("bc_bake unbake box: four numbers, two corners.");
            return new BakeCommand { Verb = BakeVerb.Unbake, Words = words, IsBox = true, X1 = x1, Z1 = z1, X2 = x2, Z2 = z2 };
          }
          if (ParseCircle(args, out var error, out var radius, out var at) == null)
            return Fail($"bc_bake unbake: {error} (unbake <radius> [at <x> <z>] | box <x1> <z1> <x2> <z2> | world).");
          return new BakeCommand { Verb = BakeVerb.Unbake, Words = words, Radius = radius, AtX = at?.X, AtZ = at?.Z };
        }
      case "undo":
        {
          if (flags.Any(f => f != "confirm"))
            return Fail("bc_bake undo: only 'confirm' goes with it.");
          if (args.Count == 0)
            return new BakeCommand { Verb = BakeVerb.Undo, Words = words };
          if (args.Count == 1 && Int(args[0], out int n) && n >= 1 && n <= ushort.MaxValue)
            return new BakeCommand { Verb = BakeVerb.Undo, Words = words, Number = n };
          return Fail("bc_bake undo: an operation's number, or none for the newest (undo 7).");
        }
      case "load":
      case "export":
        {
          var v = verb == "load" ? BakeVerb.Load : BakeVerb.Export;
          if (verb == "export" && flags.Count > 0)
            return Fail("bc_bake export: takes a file name and no other words.");
          if (verb == "load" && flags.Any(f => f is "town" or "any" or "all"))
            return Fail("bc_bake load: only 'convert' and 'confirm' go with it.");
          // The file is every word after the verb that is not one of the command's own, as typed, so a path with spaces needs no quotes.
          var path = FileOf(rest, verb == "load" ? ["convert", "confirm"] : []);
          return new BakeCommand { Verb = v, Words = words, Text = path.Length == 0 ? null : path };
        }
      case "orphans":
        {
          var destroy = args.Contains("destroy");
          var items = args.Contains("items");
          if (args.Any(a => a != "destroy" && a != "items") || flags.Any(f => f != "confirm"))
            return Fail("bc_bake orphans: 'destroy', 'items' and 'confirm' only.");
          if (items && !destroy)
            return Fail("bc_bake orphans: 'items' goes with 'destroy'.");
          return new BakeCommand { Verb = BakeVerb.Orphans, Words = words, Destroy = destroy, Items = items };
        }
      case "drop":
        {
          if (flags.Any(f => f != "confirm"))
            return Fail("bc_bake drop: only 'confirm' goes with it.");
          if (args.Count == 1 && args[0] == "compiler")
            return new BakeCommand { Verb = BakeVerb.Drop, Words = words, Compiler = true };
          if (args.Count == 2 && args[0] == "bake" && Int(args[1], out int n) && n >= 1 && n <= ushort.MaxValue)
            return new BakeCommand { Verb = BakeVerb.Drop, Words = words, Number = n };
          if (args.Count == 0)
            return Fail("bc_bake drop: 'compiler', 'bake <n>', or a world's name (in the main menu).");
          if (args[0] == "compiler" || args[0] == "bake")
            return Fail("bc_bake drop: 'compiler', or 'bake <n>' with the number of an in-game bake.");
          // A world's name, as typed (it may hold spaces): everything after "drop", less a trailing 'confirm'.
          return new BakeCommand { Verb = BakeVerb.DropWorld, Words = words, Text = FileOf(rest, ["confirm"]) };
        }
      case "server":
        return new BakeCommand { Verb = BakeVerb.Server, Text = string.Join(" ", all.Skip(1)) };
      default:
        return Fail($"bc_bake: '{all[0]}' is not understood.");
    }
  }

  // `<radius> [at <x> <z>]`.
  private static BakeCommand? ParseCircle(List<string> args, out string error, out float radius, out (float X, float Z)? at)
  {
    error = "";
    radius = 0;
    at = null;
    if (args.Count == 0 || !Num(args[0], out radius))
    {
      error = "a radius is needed";
      return null;
    }
    if (args.Count == 1)
      return new BakeCommand();
    if (args.Count == 4 && args[1] == "at" && Num(args[2], out float x) && Num(args[3], out float z))
    {
      at = (x, z);
      return new BakeCommand();
    }
    error = "after the radius only 'at <x> <z>' goes";
    return null;
  }

  private static bool Box(List<string> args, out float x1, out float z1, out float x2, out float z2)
  {
    x1 = z1 = x2 = z2 = 0;
    return args.Count == 4 && Num(args[0], out x1) && Num(args[1], out z1) && Num(args[2], out x2) && Num(args[3], out z2);
  }

  private static bool Num(string text, out float value) =>
    float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

  private static bool Int(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

  // The words after the verb, as typed, less the ones that are the command's own.
  private static string FileOf(List<string> rest, string[] own) =>
    string.Join(" ", rest.Where(w => !own.Contains(w.ToLowerInvariant())));

  // ------------------------------------------------------------------------------------------------ who may run it (10.1)

  // Read-only: every machine can run it, a client too.
  internal static bool ReadOnly(BakeVerb verb) => verb is BakeVerb.Help or BakeVerb.Info or BakeVerb.Stats or BakeVerb.Hide or BakeVerb.Show or BakeVerb.Server;

  // Why a machine may not run a verb, or null. `runsWorld`: single player, a host or a dedicated server; `inWorld`: a world is loaded.
  internal static string? Refusal(BakeVerb verb, bool runsWorld, bool inWorld)
  {
    if (ReadOnly(verb))
      return null;
    if (verb == BakeVerb.DropWorld)
      return inWorld ? "'drop <world>' runs in the main menu, with no world loaded: leave this one first." : null;
    if (!inWorld)
      return "load a world first.";
    if (!runsWorld)
      return "this runs where the world is. Ask the server: bc_bake server <the same words> (admins only).";
    return null;
  }
}

// What bc_bake says of a layer and its operations. Pure: the lines are made from a layer, so the tests read them.
internal static class BakeReport
{
  // `bc_bake info`: what this machine holds. `liveHere`: how many seeded pieces this machine's world holds (-1: not known); `missing`: the prefabs
  // the layer names that this game lacks (null: not known).
  internal static List<string> InfoLines(BakedLayer? layer, bool gameTerrain, int liveHere, IReadOnlyList<string>? missing)
  {
    var lines = new List<string>();
    if (layer == null)
    {
      lines.Add("bc_bake info: this world has no baked layer" + (gameTerrain ? " (it keeps the game's own terrain)" : "") + ".");
      return lines;
    }
    lines.Add($"bc_bake info: layer revision {layer.Revision}, {BakeTally.Num(layer.Placements)} records in {BakeTally.Num(layer.Zones.Count)} zones, {BakeRunner.Bytes(layer.Length)}, made by {layer.Producer}.");
    lines.Add($"Id {layer.Id.Substring(0, Math.Min(12, layer.Id.Length))}." + (gameTerrain ? " This world keeps the game's own terrain; the layer is all Better Continents carries here." : ""));
    var roles = new SortedDictionary<BakedRole, long>();
    foreach (var record in layer.AllRecords())
    {
      var role = layer.Palette[record.Palette].Role;
      roles[role] = roles.TryGetValue(role, out var n) ? n + 1 : 1;
    }
    var bySource = layer.RecordsBySource();
    lines.Add("Records by role: " + (roles.Count == 0 ? "none" : string.Join(", ", roles.Select(p => $"{p.Key} {BakeTally.Num(p.Value)}"))) + ". By source: "
      + (bySource.Count == 0 ? "none" : string.Join(", ", bySource.Select(p => p.Key == 0 ? $"the compiler's file {BakeTally.Num(p.Value)}" : $"bake {p.Key} {BakeTally.Num(p.Value)}"))) + ".");
    int ground = layer.Zones.Count(z => z.HasGround), paint = layer.Zones.Count(z => z.HasPaint), masks = layer.Zones.Count(z => z.HasClearMask), clear = layer.Zones.Count(z => z.NoVegetation);
    lines.Add($"Palette {BakeTally.Num(layer.Palette.Count)} entries. Ground in {BakeTally.Num(ground)} zones, paint in {BakeTally.Num(paint)}, clear masks in {BakeTally.Num(masks)}, no vegetation at all in {BakeTally.Num(clear)}.");
    var ops = layer.Registry.Operations;
    lines.Add(ops.Count == 0 ? "No operation in the registry." : $"{BakeTally.Num(ops.Count)} operations in the registry; the next is number {layer.Registry.NextOperation} ('bc_bake list').");
    if (liveHere >= 0)
      lines.Add($"Live pieces seeded in this world: {BakeTally.Num(liveHere)}.");
    if (missing != null)
      lines.Add(missing.Count == 0 ? "Pieces the layer names that this game lacks: none." : $"Pieces the layer names that this game lacks ({missing.Count}): {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? ", ..." : "")}.");
    return lines;
  }

  // "40 m around 812, -1206", "box 1, 2 to 3, 4", "the whole world", "the compiler's file".
  internal static string Where(OperationInfo op)
  {
    string Round(float v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
    if (op.Kind == OperationKind.Load)
      return "the compiler's file";
    if (op.Kind == OperationKind.Drop)
      return "records dropped";
    if (op.X1 == 0f && op.Z1 == 0f && op.X2 == 0f && op.Z2 == 0f && op.Radius == 0f)
      return "the whole world";
    if (op.Radius > 0f)
      return $"{op.Radius.ToString("0.#", CultureInfo.InvariantCulture)} m around {Round((op.X1 + op.X2) / 2f)}, {Round((op.Z1 + op.Z2) / 2f)}";
    return $"box {Round(op.X1)}, {Round(op.Z1)} to {Round(op.X2)}, {Round(op.Z2)}";
  }

  // How an operation stands, in a word: done (and what a later load will settle), undone, abandoned, or not finished.
  internal static string StateWord(OperationInfo? op, BakeState? state)
  {
    if (op != null && op.State == OperationState.Undone)
      return "undone";
    if (state == BakeState.Abandoned)
      return "abandoned";
    if (state == BakeState.Undone)
      return "undone";
    if (op == null)
      return state == null ? "done" : "not in the layer";
    if (state == null || state.Value is BakeState.Settled or BakeState.Removed or BakeState.Unbaking)
      return "done";
    if (state == BakeState.Applied && op.Kind == OperationKind.Drop)
      return "done";
    return "not finished";
  }

  // `bc_bake list`: one line for each operation the layer lists, and for each undo file the layer does not (abandoned ones, and any that left
  // the registry).
  internal static List<string> ListLines(IReadOnlyList<OperationInfo> operations, IEnumerable<int> journals, Func<int, BakeState?> stateOf, Func<int, BakeJournalData?> read)
  {
    var lines = new List<string>();
    var numbers = new SortedSet<int>(operations.Select(o => (int)o.Number));
    var kept = new HashSet<int>(journals);
    foreach (var n in kept)
      numbers.Add(n);
    if (numbers.Count == 0)
    {
      lines.Add("bc_bake list: no operation yet. 'bc_bake area <radius>' is a dry run of a bake.");
      return lines;
    }
    lines.Add($"bc_bake list: {numbers.Count} operations (the newest {BakeJournal.KeepOperations} keep an undo file, and every one that is not finished):");
    foreach (int n in numbers)
    {
      var op = operations.FirstOrDefault(o => o.Number == n);
      var state = stateOf(n);
      var data = kept.Contains(n) ? read(n) : null;
      var kind = op?.Kind ?? data?.Kind ?? OperationKind.BakeArea;
      string word = kind switch { OperationKind.BakeArea or OperationKind.BakeBox => "bake", OperationKind.Unbake => "unbake", OperationKind.Load => "load", OperationKind.Drop => "drop", _ => "?" };
      string when = op != null ? DateTimeOffset.FromUnixTimeSeconds(op.Time).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : data != null ? DateTimeOffset.FromUnixTimeSeconds(data.Time).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
      string who = op?.Who ?? data?.Who ?? "";
      string where = op != null ? Where(op) : data != null ? Where(data.ToOperation()) : "";
      string count = op != null ? (op.IsBake ? $"+{BakeTally.Num(op.Added)}" : $"-{BakeTally.Num(op.Removed)}") : data != null ? (data.IsBake ? $"+{BakeTally.Num(data.Records.Count)}" : $"-{BakeTally.Num(data.Records.Count)}") : "";
      lines.Add($"{n,5}  {word,-6}  {when}  {who}  {where}  {count}  {StateWord(op, state)}" + (kept.Contains(n) ? "  undo file kept" : ""));
    }
    return lines;
  }
}

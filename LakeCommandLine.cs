// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterContinents;

// What a bc_lake line asks for (BUILD-SPEC-TARN.md 2.7). Tarn's own console command was `tarn here | at | list | depth | reload | remove | info` [T: Plugin.cs]; these are
// its words, with the data words of Better Continents' lakes beside them. Pure: the line is parsed here, who may run it is Refusal, and what it does is LakeCommands.
internal enum LakeVerb
{
  Help,
  Info,
  List,
  MaskAt,
  MaskLoad,
  MaskExport,
  Here,
  At,
  Remove,
  Depth,
  Load,
  Import,
  Export,
  Regen,
  Server,
}

// A parsed line. Error is set, and nothing else means anything, when the line is not understood.
internal sealed class LakeCommand
{
  public LakeVerb Verb { get; init; }
  public string? Error { get; init; }
  public bool Confirm { get; init; }
  public bool Convert { get; init; }
  // List: how many lakes (default 10).
  public int Count { get; init; } = LakeCommandLine.DefaultListCount;
  // At and MaskAt: the place. Here: null (the one who asks).
  public float? X { get; init; }
  public float? Z { get; init; }
  // Here and At: the search radius in metres and the margin under the spill height (Tarn's defaults 120 and 0.3).
  public float Radius { get; init; } = LakeCommandLine.DefaultRadius;
  public float Margin { get; init; } = LakeCommandLine.DefaultMargin;
  // Depth: the new scale; null shows the current one.
  public float? Scale { get; init; }
  // Remove: the lake's name or "all". Load, Import, Export, MaskLoad, MaskExport: the folder or file, as typed (null: the default). Server: the rest of the line.
  public string? Text { get; init; }
}

internal static class LakeCommandLine
{
  public const int DefaultListCount = 10;
  public const float DefaultRadius = 120f, DefaultMargin = 0.3f;
  public const float MaxRadius = 1000f, MaxMargin = 20f;

  public const string Usage = "bc_lake [help | info | list [n] | here [radius] [margin] | at <x> <z> [radius] [margin] | remove <name|all> | depth [scale] | load [folder] | import [path] | export [folder] | mask at <x> <z> | mask load <file> | mask export <file> | regen | server ...]";

  private static readonly CultureInfo C = CultureInfo.InvariantCulture;

  private static LakeCommand Fail(string error) => new() { Verb = LakeVerb.Help, Error = $"bc_lake: {error}" };

  private static bool Number(string text, out float value) =>
    float.TryParse(text, NumberStyles.Float, C, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

  public static LakeCommand Parse(string line)
  {
    var tokens = (line ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
    if (tokens.Count == 0 || tokens[0] == "?" || tokens[0].Equals("help", StringComparison.OrdinalIgnoreCase))
      return tokens.Count > 1 ? Fail("'help' takes no more words") : new LakeCommand { Verb = LakeVerb.Help };
    var word = tokens[0].ToLowerInvariant();
    tokens.RemoveAt(0);
    if (word == "server")
      return tokens.Count == 0 ? Fail("'server' needs the words to run on the server: bc_lake server remove all confirm")
        : tokens[0].Equals("server", StringComparison.OrdinalIgnoreCase) ? Fail("'server server' is no use")
        : Parse(string.Join(" ", tokens)) is { Error: { } e } ? new LakeCommand { Verb = LakeVerb.Help, Error = e }
        : new LakeCommand { Verb = LakeVerb.Server, Text = string.Join(" ", tokens) };

    // The words that change what a command does stand wherever they like (a path may hold spaces, so they are taken out of the line, not split off its end).
    bool confirm = false, convert = false;
    var rest = new List<string>();
    foreach (var t in tokens)
    {
      if (t.Equals("confirm", StringComparison.OrdinalIgnoreCase)) confirm = true;
      else if (t.Equals("convert", StringComparison.OrdinalIgnoreCase)) convert = true;
      else rest.Add(t);
    }
    string? Text() => rest.Count == 0 ? null : string.Join(" ", rest);
    LakeCommand Plain(LakeVerb verb, bool allowConfirm = false, bool allowConvert = false)
    {
      if (confirm && !allowConfirm) return Fail($"'{word}' takes no 'confirm'");
      if (convert && !allowConvert) return Fail($"'{word}' takes no 'convert'");
      return new LakeCommand { Verb = verb, Confirm = confirm, Convert = convert };
    }

    switch (word)
    {
      case "info":
        return rest.Count > 0 ? Fail("'info' takes no more words") : Plain(LakeVerb.Info);
      case "regen":
        return rest.Count > 0 ? Fail("'regen' takes no more words") : Plain(LakeVerb.Regen, allowConfirm: true);
      case "list":
      {
        if (rest.Count > 1) return Fail("list [n]");
        int n = DefaultListCount;
        if (rest.Count == 1 && (!int.TryParse(rest[0], NumberStyles.Integer, C, out n) || n < 1 || n > 1000)) return Fail($"'{rest[0]}' is not a number of lakes between 1 and 1000");
        return confirm || convert ? Fail("'list' takes no 'confirm' or 'convert'") : new LakeCommand { Verb = LakeVerb.List, Count = n };
      }
      case "depth":
      {
        if (rest.Count > 1) return Fail("depth [scale]");
        float? scale = null;
        if (rest.Count == 1)
        {
          if (!Number(rest[0], out var v) || v < 0f || v > LakeSet.MaxDepthScale) return Fail($"'{rest[0]}' is not a depth scale between 0 and {LakeSet.MaxDepthScale:0}");
          scale = v;
        }
        return confirm || convert ? Fail("'depth' takes no 'confirm' or 'convert'") : new LakeCommand { Verb = LakeVerb.Depth, Scale = scale };
      }
      case "here":
      {
        if (rest.Count > 2) return Fail("here [radius] [margin]");
        if (confirm || convert) return Fail("'here' takes no 'confirm' or 'convert'");
        if (!Spill(rest, 0, out var radius, out var margin, out var error)) return Fail(error);
        return new LakeCommand { Verb = LakeVerb.Here, Radius = radius, Margin = margin };
      }
      case "at":
      {
        if (rest.Count < 2 || rest.Count > 4) return Fail("at <x> <z> [radius] [margin]");
        if (confirm || convert) return Fail("'at' takes no 'confirm' or 'convert'");
        if (!Number(rest[0], out var x) || !Number(rest[1], out var z)) return Fail("'at' needs two numbers: at <x> <z> [radius] [margin]");
        if (!Spill(rest, 2, out var radius, out var margin, out var error)) return Fail(error);
        return new LakeCommand { Verb = LakeVerb.At, X = x, Z = z, Radius = radius, Margin = margin };
      }
      case "remove":
        if (rest.Count == 0) return Fail("remove <name|all>");
        return convert ? Fail("'remove' takes no 'convert'") : new LakeCommand { Verb = LakeVerb.Remove, Text = Text(), Confirm = confirm };
      case "load":
        return new LakeCommand { Verb = LakeVerb.Load, Confirm = confirm, Convert = convert, Text = Text() };
      case "import":
        return new LakeCommand { Verb = LakeVerb.Import, Confirm = confirm, Convert = convert, Text = Text() };
      case "export":
        return confirm || convert ? Fail("'export' takes no 'confirm' or 'convert'") : new LakeCommand { Verb = LakeVerb.Export, Text = Text() };
      case "mask":
      {
        if (rest.Count == 0) return Fail("mask at <x> <z> | mask load <file> | mask export <file>");
        var sub = rest[0].ToLowerInvariant();
        var args = rest.Skip(1).ToList();
        switch (sub)
        {
          case "at":
            if (args.Count != 2 || !Number(args[0], out var x) || !Number(args[1], out var z)) return Fail("mask at <x> <z>");
            return confirm || convert ? Fail("'mask at' takes no 'confirm' or 'convert'") : new LakeCommand { Verb = LakeVerb.MaskAt, X = x, Z = z };
          case "load":
            if (args.Count == 0) return Fail("mask load <file>");
            return new LakeCommand { Verb = LakeVerb.MaskLoad, Text = string.Join(" ", args), Confirm = confirm, Convert = convert };
          case "export":
            if (args.Count == 0) return Fail("mask export <file>");
            return confirm || convert ? Fail("'mask export' takes no 'confirm' or 'convert'") : new LakeCommand { Verb = LakeVerb.MaskExport, Text = string.Join(" ", args) };
          default:
            return Fail($"'mask {rest[0]}' is not understood: mask at <x> <z> | mask load <file> | mask export <file>");
        }
      }
      default:
        return Fail($"'{word}' is not understood. {Usage}");
    }
  }

  // [radius] [margin] from the words at this index.
  private static bool Spill(List<string> rest, int from, out float radius, out float margin, out string error)
  {
    radius = DefaultRadius;
    margin = DefaultMargin;
    error = "";
    if (rest.Count > from && (!Number(rest[from], out radius) || radius < 4f || radius > MaxRadius))
    {
      error = $"'{rest[from]}' is not a radius between 4 and {MaxRadius:0} m";
      return false;
    }
    if (rest.Count > from + 1 && (!Number(rest[from + 1], out margin) || margin < 0f || margin > MaxMargin))
    {
      error = $"'{rest[from + 1]}' is not a margin between 0 and {MaxMargin:0} m";
      return false;
    }
    return true;
  }

  // Whether a verb only reads (it runs on any machine, a client too).
  internal static bool ReadOnly(LakeVerb verb) => verb is LakeVerb.Help or LakeVerb.Info or LakeVerb.List or LakeVerb.MaskAt or LakeVerb.MaskExport or LakeVerb.Export or LakeVerb.Server;

  // Why a machine may not run a verb, or null. `runsWorld`: single player, a host or a dedicated server; `inWorld`: a world is loaded.
  internal static string? Refusal(LakeVerb verb, bool runsWorld, bool inWorld)
  {
    if (verb is LakeVerb.Help or LakeVerb.Info or LakeVerb.Server)
      return null;
    if (!inWorld)
      return "load a world first.";
    if (ReadOnly(verb))
      return null;
    if (!runsWorld)
      return "this runs where the world is. Ask the server: bc_lake server <the same words> (admins only).";
    return null;
  }

  // Whether a verb changes the world's lakes or mask (the Tarn mod being installed refuses these).
  internal static bool Changes(LakeVerb verb) => verb is LakeVerb.Here or LakeVerb.At or LakeVerb.Remove or LakeVerb.Depth or LakeVerb.Load or LakeVerb.Import or LakeVerb.MaskLoad or LakeVerb.Regen;
}

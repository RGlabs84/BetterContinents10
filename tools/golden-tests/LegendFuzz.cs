// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// The legend fuzz. Every legend file people use today must keep loading exactly as it does in Better Continents 0.9.4
// (the user's rule for the unifying refactor), so the legend code may only be reorganised under this check: thousands
// of legend files, valid and broken in the ways people write them, made the same way on every run (seeded), read the way
// the game reads them - from the file beside the picture, and for paint, terrain and old biome maps also from the string
// a world's settings store - with the whole outcome of each recorded as a digest: what the legend became, the decoded
// pixels, the error count, every log line, any exception. golden/legend-fuzz.tsv was recorded from 0.9.4's code.
//   dotnet run -c Release -- fuzz <kind> <case>     one case: its legend file and its outcome in full
//   dotnet run -c Release -- fuzz-dump <file> <n>   every case's digests for n cases per kind, to compare two builds

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BetterContinents;
using SixLabors.ImageSharp.PixelFormats;

namespace GoldenTest;

internal static class LegendFuzz
{
  const string S = "legend-fuzz";
  public const int Cases = 2000;
  // A recorded line holds this many cases' digests, so a difference still names its case.
  const int PerLine = 10;
  public static readonly string[] Kinds = ["biome", "location", "paint", "terrain", "spawn", "alt"];
  const int Size = 8;

  // The pictures: the colours legends mostly name, each pixel its own (no two neighbours alike, so every location area is
  // one pixel), the last two rows half transparent (paint and terrain key on alpha too).
  static readonly uint[] Palette =
  [
    0x00FF00, 0x007F00, 0xFFFFFF, 0x8B4513, 0x0000FF, 0x7F7F00, 0xFF0000, 0x000000,
    0x123456, 0x7F7F7F, 0xFFFF00, 0x00FFFF, 0xA05A3C, 0x6A8CA0, 0xFE0102, 0x00FF80,
  ];
  static Rgba32 Pixel(int x, int y)
  {
    var c = Palette[(y * Size + x) % Palette.Length];
    return new((byte)(c >> 16), (byte)(c >> 8), (byte)c, y >= Size - 2 ? (byte)128 : (byte)255);
  }

  public static void Run()
  {
    var culture = CultureInfo.CurrentCulture;
    // ParseRGBA reads numbers in the current culture; the recording must not depend on this machine's.
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    try
    {
      var dir = Path.Combine("fx", "legend-fuzz");
      Directory.CreateDirectory(dir);
      foreach (var kind in Kinds)
      {
        var digests = new List<string>();
        var stored = new List<string>();
        for (int i = 0; i < Cases; i++)
        {
          var (outcome, fromSettings) = Outcome(dir, kind, i);
          digests.Add(Digest(outcome));
          if (fromSettings != null)
            stored.Add(Digest(fromSettings));
        }
        AddLines(kind, digests);
        if (stored.Count > 0)
          AddLines(kind + "-stored", stored);
      }
    }
    finally
    {
      CultureInfo.CurrentCulture = culture;
    }
  }

  static void AddLines(string kind, List<string> digests)
  {
    for (int i = 0; i < digests.Count; i += PerLine)
      Golden.Add(S, $"{kind}/{i:0000}-{Math.Min(i + PerLine, digests.Count) - 1:0000}", string.Join(" ", digests.Skip(i).Take(PerLine)));
  }

  static string Digest(List<string> outcome)
  {
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", outcome)))).Substring(0, 12).ToLowerInvariant();
  }

  // Every case's digest, one per line, for a deep comparison of two builds beyond the recorded cases
  // (dotnet run -- fuzz-dump <file> <cases per kind>; the first Cases of them are the recorded ones).
  public static void Dump(string file, int cases)
  {
    var culture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    try
    {
      var dir = Path.Combine("fx", "legend-fuzz");
      Directory.CreateDirectory(dir);
      using var output = new StreamWriter(file);
      foreach (var kind in Kinds)
        for (int i = 0; i < cases; i++)
        {
          var (outcome, fromSettings) = Outcome(dir, kind, i);
          output.WriteLine($"{kind}/{i} {Digest(outcome)}" + (fromSettings != null ? $" {Digest(fromSettings)}" : ""));
        }
    }
    finally
    {
      CultureInfo.CurrentCulture = culture;
    }
  }

  // One case in full (dotnet run -- fuzz <kind> <case>).
  public static void Show(string kind, int index)
  {
    var culture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    try
    {
      var dir = Path.Combine("fx", "legend-fuzz");
      Directory.CreateDirectory(dir);
      var (bytes, _) = Legend(kind, index);
      System.Console.WriteLine($"== {kind} case {index}: the legend file ({bytes.Length} bytes), escaped");
      System.Console.WriteLine(Escape(Encoding.UTF8.GetString(bytes)));
      var (outcome, fromSettings) = Outcome(dir, kind, index);
      System.Console.WriteLine($"== read from the file ({Digest(outcome)})");
      foreach (var line in outcome)
        System.Console.WriteLine("  " + line);
      if (fromSettings != null)
      {
        System.Console.WriteLine($"== read from a world's settings ({Digest(fromSettings)})");
        foreach (var line in fromSettings)
          System.Console.WriteLine("  " + line);
      }
    }
    finally
    {
      CultureInfo.CurrentCulture = culture;
    }
  }

  static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n\n").Replace("\t", "\\t")
    .Replace("\uFEFF", "\\uFEFF").Replace("\u200B", "\\u200B").Replace("\u00A0", "\\u00A0");

  // ---- reading a case the game's ways ---------------------------------------------------------------------------------

  static (List<string> FromFile, List<string>? FromSettings) Outcome(string dir, string kind, int index)
  {
    var png = Path.Combine(dir, kind + ".png");
    if (!File.Exists(png))
      Fixtures.ColourMap(png, Pixel, Size);
    var txt = Path.ChangeExtension(png, ".txt");
    var (bytes, _) = Legend(kind, index);
    File.WriteAllBytes(txt, bytes);
    var fromFile = new List<string>();
    List<string>? fromSettings = null;
    // A location map picks among equal candidates at random off the main thread (its own System.Random): seed it.
    if (kind == "location")
      typeof(ImageMapLocation).GetField("workerRandom", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Random(index));
    Read(fromFile, () => kind switch
    {
      "biome" => Biome(ImageMapBiome.Create(png)),
      "location" => Location(ImageMapLocation.Create(png)),
      "paint" => Colour(ImageMapPaint.Create(png)),
      "terrain" => Colour(ImageMapTerrain.Create(png)),
      "spawn" => Spawn(ImageMapSpawn.Create(png)),
      _ => Alt(ImageMapAltBiome.Create(png)),
    });
    fromFile.Add("legend file after: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(txt))).Substring(0, 16));
    // The string a world's settings keep, read again when the world loads: the file's lines joined with '|'.
    if (kind is "paint" or "terrain" or "biome")
    {
      fromSettings = [];
      var data = File.ReadAllBytes(png);
      var colours = string.Join("|", File.ReadAllLines(txt));
      Read(fromSettings, () => kind switch
      {
        "paint" => Colour(ImageMapPaint.Create(data, colours)),
        "terrain" => Colour(ImageMapTerrain.Create(data, colours)),
        _ => Biome(ImageMapBiome.Create(data, colours, png)),
      });
    }
    return (fromFile, fromSettings);
  }

  static void Read(List<string> outcome, Func<List<string>> read)
  {
    List<string>? result = null;
    List<string> log;
    try
    {
      log = LogHandler.During(() => result = read());
    }
    catch (Exception e)
    {
      // An exception escapes the loader: the game sees it the same way.
      log = [];
      result = [$"threw {e.GetType().Name}: {e.Message}"];
    }
    outcome.AddRange(result!);
    outcome.AddRange(log.Select(l => "log " + l));
  }

  static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
  static IEnumerable<(float X, float Y)> Points() =>
    Enumerable.Range(0, Size * Size).Select(i => ((i % Size + 0f) / (Size - 1), (i / Size + 0f) / (Size - 1)));

  static List<string> Biome(ImageMapBiome? map)
  {
    if (map == null) return ["no map"];
    return
    [
      $"errors {map.LegendErrors}",
      "legend " + string.Join(", ", map.LegendColors.OrderBy(kv => (uint)kv.Key).Select(kv => $"{(uint)kv.Key}={kv.Value}")),
      "unresolved " + string.Join(", ", map.UnresolvedLegend.Select(u => $"{u.Name}={u.Color}")),
      "pixels " + string.Join(",", Points().Select(p => ((uint)map.GetValue(p.X, p.Y)).ToString(CultureInfo.InvariantCulture))),
      "serialized " + Hash.Bytes(map.Serialize()),
    ];
  }

  static List<string> Location(ImageMapLocation? map)
  {
    if (map == null) return ["no map"];
    return
    [
      "legend " + string.Join(", ", map.LegendColors.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")),
      .. map.RemainingAreas.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"area {kv.Key}: " + string.Join(" ", kv.Value.Select(p => $"({F(p.x)},{F(p.y)})"))),
    ];
  }

  static List<string> Colour(ImageMapColor? map)
  {
    if (map == null) return ["no map"];
    var values = Points().Select(p => map.TryGetValue(p.X, p.Y, out var c) ? $"{F(c.r)},{F(c.g)},{F(c.b)},{F(c.a)}" : "-");
    return ["source colors " + map.SourceColors, "pixels " + string.Join(" ", values)];
  }

  static List<string> Spawn(ImageMapSpawn? map)
  {
    if (map == null) return ["no map"];
    return
    [
      "legend " + string.Join(" | ", map.LegendColors.Zip(map.LegendEntries, (c, e) => $"{c}={e.Data}")),
      "pixels " + string.Join(" ", Points().Select(p => map.GetEntry(p.X, p.Y)?.Data ?? "-")),
    ];
  }

  static List<string> Alt(ImageMapAltBiome? map)
  {
    if (map == null) return ["no map"];
    return
    [
      "classes " + string.Join(" | ", map.Classes.Select(c => c.Label + (c.IsNone ? " none" : ""))),
      "pins " + string.Join(" | ", map.Pins.Select(p => $"{F(p.X)},{F(p.Z)}->{p.Class}")),
      "map " + string.Join(",", map.Map),
      "block " + Hash.Bytes(map.ToBlock()),
    ];
  }

  // ---- making a case ----------------------------------------------------------------------------------------------------

  // The legend file of a case: its bytes as written (an encoding, perhaps a byte order mark) and its text.
  public static (byte[] Bytes, string Text) Legend(string kind, int index)
  {
    var r = new Random(1_000_003 * (Array.IndexOf(Kinds, kind) + 1) + index);
    int roll = r.Next(100);
    int count = roll < 4 ? 0 : roll < 75 ? 1 + r.Next(8) : roll < 95 ? 9 + r.Next(22) : 200 + r.Next(101);
    var newline = r.Next(100) switch { < 70 => "\n", < 90 => "\r\n", < 95 => "\r", _ => null };
    var text = new StringBuilder();
    for (int n = 0; n < count; n++)
    {
      text.Append(Line(kind, r));
      if (n < count - 1 || r.Next(2) == 0)
        text.Append(newline ?? Pick(r, "\n", "\r\n", "\r"));
    }
    var s = text.ToString();
    Encoding encoding = r.Next(100) switch { < 90 => new UTF8Encoding(false), < 97 => new UTF8Encoding(true), _ => new UnicodeEncoding(false, true) };
    return ([.. encoding.GetPreamble(), .. encoding.GetBytes(s)], s);
  }

  static string Pick(Random r, params string[] items) => items[r.Next(items.Length)];

  static string Line(string kind, Random r)
  {
    // Lines that are no entry at all.
    if (r.Next(100) < 12)
      return Pick(r, "", "   ", "\t", "# a note", "#", "  # an indented note", ":", "|", "::", "no colon here", "a:b:c",
        "#00FF00: Meadows", "# Meadows: 00FF00", "\uFEFF", "\u200B# hidden note", new string('x', 300), "名前: 00FF00", "Meadows",
        "Meadows:", ": 00FF00", "  :  ", "=", "Meadows = 00FF00");
    var (left, right) = Entry(kind, r);
    return r.Next(100) switch
    {
      < 55 => $"{left}: {right}",
      < 62 => $"{left}:{right}",
      < 66 => $"{left} : {right}",
      < 69 => $"  {left}: {right}  ",
      < 71 => $"{left}:: {right}",
      < 73 => $"{left}: {right}: extra",
      < 75 => $"{left} {right}",
      < 78 => $"{left}: {right} # a note",
      < 80 => $"# {left}: {right}",
      < 82 => $"#{left}: {right}",
      < 84 => $"\t{left}:\t{right}",
      < 86 => $"\uFEFF{left}: {right}",
      < 88 => $"\u200B{left}: {right}",
      < 90 => $"\u00A0{left}: {right}",
      < 94 => $"{left}: {right} | {Entry(kind, r).Left}: {Entry(kind, r).Right}",
      < 96 => $"{left}={right}",
      < 98 => $"{left}: {right}\t",
      _ => $"{left}:{right}|",
    };
  }

  static (string Left, string Right) Entry(string kind, Random r) => kind switch
  {
    "biome" => (BiomeName(r), Colour(r)),
    "location" => (LocationName(r), Colour(r)),
    "paint" => (Colour(r), Colour(r)),
    "terrain" => (r.Next(100) < 70 ? TerrainName(r) : Colour(r), Colour(r)),
    "spawn" => (Colour(r), Entries(r)),
    _ => (AltNames(r), r.Next(100) < 80 ? Colour(r) : Pin(r)),
  };

  // A colour as people write them, and get them wrong.
  static string Colour(Random r)
  {
    uint c = r.Next(100) < 85 ? Palette[r.Next(Palette.Length)] : (uint)r.Next(0x1000000);
    byte cr = (byte)(c >> 16), cg = (byte)(c >> 8), cb = (byte)c;
    var alpha = Pick(r, "255", "128", "0", "FF", "80");
    return r.Next(30) switch
    {
      < 6 => c.ToString("X6", CultureInfo.InvariantCulture),
      6 => "#" + c.ToString("X6", CultureInfo.InvariantCulture),
      7 => c.ToString("x6", CultureInfo.InvariantCulture),
      8 or 9 => $"{cr},{cg},{cb}",
      10 => $"{cr}, {cg}, {cb}",
      11 => $"{cr},{cg},{cb},{(alpha.Length == 2 ? "255" : alpha)}",
      12 => $" {cr} ,{cg} , {cb} ",
      13 => c.ToString("X6", CultureInfo.InvariantCulture) + (alpha.Length == 2 ? alpha : "FF"),
      14 => "#" + c.ToString("X6", CultureInfo.InvariantCulture) + (alpha.Length == 2 ? alpha : "80"),
      15 => $"{cr >> 4:X}{cg >> 4:X}{cb >> 4:X}",
      16 => $"#{cr >> 4:X}{cg >> 4:X}{cb >> 4:X}",
      17 => $"{256 + r.Next(100)},{cg},{cb}",
      18 => $"-{r.Next(3)},{cg},{cb}",
      19 => $"+{cr},{cg},{cb}",
      20 => $"{cr},{cg}",
      21 => $"{cr},{cg},{cb},255,1",
      22 => $"{cr},{cg},{cb},",
      23 => $"\t{c:X6}\t",
      24 => $"0x{c:X6}",
      25 => $"{cr}.0,{cg},{cb}",
      26 => Pick(r, "zzz", "", " ", "red", "GG0000", "1e2,0,0", "١,٢,٣", "00FF0", "00FF000", "#", "##00FF00", "00 FF 00", "0,0,0,0"),
      27 => $"{cr};{cg};{cb}",
      28 => $"{cr},,{cb}",
      _ => c.ToString("X6", CultureInfo.InvariantCulture),
    };
  }

  static string BiomeName(Random r) => Pick(r,
    "None", "Meadows", "Swamp", "Mountain", "BlackForest", "Plains", "AshLands", "DeepNorth", "Ocean", "Mistlands",
    "Meadows", "Ocean", "Mountain", "BlackForest", "Plains", "meadows", "MOUNTAIN", "Black Forest", "black_forest",
    "Deep-North", "Ash Lands", "ashlands", "1", "2", "4", "8", "16", "32", "64", "128", "256", "512", "0", "3", "1024",
    "-1", "99999999999", "All", "DeadWastes", "Bogland", "", " Plains", "Ocean ", "Meadows!", "名前", "None ", "Swamp_",
    "Black  Forest", "Mist lands", "0x1");

  static string LocationName(Random r) => Pick(r,
    "StartTemple", "Eikthyrnir", "GDKing", "GoblinKing", "Bonemass", "Dragonqueen", "Vendor_BlackForest",
    "Runestone_Meadows", "Crypt2", "Hildir_camp", "Ruin1", "Mistlands_DvergrBossEntrance1", "Runestone_Meadows:alias",
    "Name:Alias:More", "Custom Location", "", "  ", "StartTemple ", "starttemple", "Crypt2", "StartTemple", "名前");

  static string TerrainName(Random r) => Pick(r,
    "Default", "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "AshLands", "DeepNorth", "Ocean",
    "default", "OCEAN", "Black Forest", "deepnorth", " Swamp ", "", "Meadows ", "ashlands", "None", "Paint");

  static string Entries(Random r)
  {
    var tokens = new[] { "+Wolf", "-Boar", "Deer", "none", "*", "+Fir*", "-*tree*", "+*_small", "Greyling", "+Troll", " +Neck ", "+", "-", "", "none ", "+Pinetree_01", "-FirTree" };
    var list = Enumerable.Range(0, 1 + r.Next(4)).Select(_ => tokens[r.Next(tokens.Length)]);
    return string.Join(r.Next(2) == 0 ? "," : ", ", list);
  }

  static string AltNames(Random r)
  {
    var names = new[]
    {
      "Mushroom", "Lantern", "Bones", "Menhir", "Dark Meadows", "Peaceful Meadows", "Birch Meadows", "Smalltree Meadows",
      "Hut Swamp", "Bog Swamp", "Wolf Mountain", "Drake Mountain", "Fortress Mountain", "Rock Black Forest",
      "Ruin Black Forest", "Trees Mistlands", "Rockless Mistlands", "Swords Mistlands", "none", "None", "*Meadows", "*",
      "Unknown Alt", "!Mushroom", "! Lantern", "", "mushroom",
    };
    var list = Enumerable.Range(0, 1 + (r.Next(100) < 70 ? 0 : r.Next(3))).Select(_ => names[r.Next(names.Length)]);
    return string.Join(r.Next(2) == 0 ? "+" : " + ", list);
  }

  static string Pin(Random r) => Pick(r, "at 100, -200", "at 0,0", "@1.5,2.5", "@ 3, 4", "at x, y", "at 1e3, -1e3",
    "at NaN, 0", "at 1,2,3", "AT 5, 6", "at 9999999, 1", "@", "at ", "at 1.5", "@-0.5,-0.5");
}

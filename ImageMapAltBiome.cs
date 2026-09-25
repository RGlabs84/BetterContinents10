// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-25 for version-agnostic wording (0.9.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

// One thing the author asked for in the alt-biome legend: an alt biome name (or a * wildcard over names),
// optionally forced with a leading "!". Names are resolved against AltBiomeList.m_altBiomes when the world
// LOADS, never when it is baked: world creation happens on the main menu, where no ZoneSystem exists and the
// game's alt-biome list is still empty.
internal readonly struct AltBiomeEntry(string pattern, bool force)
{
  public readonly string Pattern = pattern;
  public readonly bool Force = force;
  public override string ToString() => (Force ? "!" : "") + Pattern;
}

// One planting instruction: every legend line with the same colour, or one "at x, z" point-plant line.
// Index 0 of ImageMapAltBiome.Classes is reserved for "unplanted" and is never a real class.
internal class AltBiomeClass
{
  public Color32 Color;
  // Created by an "at x, z" line: it has no pixels and is applied to the region under the point.
  public bool IsPin;
  // "none": the region is planted with nothing, which also closes it to the game's random placement.
  public bool IsNone;
  public readonly List<AltBiomeEntry> Entries = [];

  public string ColorHex => $"{Color.r:X2}{Color.g:X2}{Color.b:X2}";
  public string Names => IsNone ? "none" : string.Join(" + ", Entries.Select(e => e.ToString()));
  public string Label => IsPin ? $"point [{Names}]" : $"#{ColorHex} [{Names}]";
}

internal class AltBiomePin
{
  public float X;
  public float Z;
  public byte Class;
}

// The alt-biome map: a colour image laid over the world exactly like the biome map, plus a legend file
// (<image name>.txt, e.g. altbiomemap.txt) that says which alt biomes each colour plants.
//
// What is baked into the world settings is the RESOLVED form, not the PNG: a class index per pixel
// (run-length encoded, 0 = unplanted) plus the class table and point plants. Colour matching therefore happens
// once, on the author's machine, and every server and client decodes the same bytes - two machines can never
// disagree about which pixel belongs to which class.
internal class ImageMapAltBiome() : ImageMapBase()
{
  // Version of the baked block (DataKey.AltBiomeMap). The block is length-prefixed; new fields are appended at
  // the end and a reader stops at the fields it knows. An incompatible change would need a new DataKey.
  public const int BlockVersion = 1;

  // Pixels within this Euclidean RGB distance of a legend colour count as that colour. It absorbs the few
  // units of drift that colour-managed image editors introduce on export. The default palette keeps every
  // pair of colours (and every colour and the base-biome colours) more than twice this far apart, so a
  // pixel can never be within tolerance of two palette colours.
  public const int ColorTolerance = 20;
  // Alpha below this is "not painted", so an overlay exports straight from a transparent layer.
  public const int AlphaThreshold = 128;
  // Class indexes are bytes and 0 means unplanted.
  public const int MaxEntries = 254;

  public byte[] Map = [];
  public readonly List<AltBiomeClass> Classes = [new AltBiomeClass { IsNone = true }];
  public readonly List<AltBiomePin> Pins = [];
  // The legend exactly as read, kept for "bc info" and the report; it carries no paths.
  public string Legend = "";
  // Pixels per class, counted when the image is decoded (diagnostics only, not serialized).
  public int PlantedPixels;

  public bool HasPlantedPixels
  {
    get
    {
      foreach (var b in Map)
        if (b != 0)
          return true;
      return false;
    }
  }

  public static string LegendPath(string imagePath) =>
    Path.Combine(Path.GetDirectoryName(imagePath) ?? "", Path.GetFileNameWithoutExtension(imagePath) + ".txt");

  public static ImageMapAltBiome? Create(string path)
  {
    if (string.IsNullOrEmpty(path))
      return null;
    ImageMapAltBiome map = new() { FilePath = path };
    if (!map.LoadSourceImage())
      return null;
    if (!map.CreateMap())
      return null;
    // The PNG is not kept: the class map is what gets baked, and the source image can be many megabytes.
    map.SourceData = [];
    return map;
  }

  public bool CreateMap() => CreateMap<Rgba32>();

  public override bool LoadSourceImage()
  {
    if (!base.LoadSourceImage())
      return false;
    var path = LegendPath(FilePath);
    try
    {
      if (!File.Exists(path))
      {
        File.WriteAllText(path, DefaultLegend);
        BetterContinents.Log($"Wrote the default alt-biome legend to {path}");
      }
      Legend = File.ReadAllText(path);
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot read the alt-biome legend {path}: {ex.Message}. Using the default palette.");
      Legend = DefaultLegend;
    }
    ParseLegend(Legend, Path.GetFileName(path));
    return true;
  }

  #region Legend
  internal void ParseLegend(string text, string source)
  {
    Classes.RemoveRange(1, Classes.Count - 1);
    Pins.Clear();
    var lines = text.Replace("\r\n", "\n").Split('\n');
    for (int n = 0; n < lines.Length; n++)
    {
      var line = StripComment(lines[n]).Trim();
      if (line.Length == 0)
        continue;
      string Where() => $"{source} line {n + 1}";
      var colon = line.IndexOf(':');
      if (colon <= 0)
      {
        BetterContinents.LogWarning($"{Where()}: expected '<alt biome>[ + <alt biome>...]: <colour>' or '...: at <x>, <z>', ignoring \"{line}\".");
        continue;
      }
      var namesPart = line.Substring(0, colon).Trim();
      var valuePart = line.Substring(colon + 1).Trim();

      var entries = new List<AltBiomeEntry>();
      bool isNone = false;
      foreach (var raw in namesPart.Split('+'))
      {
        var token = raw.Trim();
        if (token.Length == 0)
          continue;
        bool force = token.StartsWith("!");
        if (force)
          token = token.Substring(1).Trim();
        if (token.Length == 0)
          continue;
        if (string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
          isNone = true;
        else
          entries.Add(new AltBiomeEntry(token, force));
      }
      if (isNone && entries.Count > 0)
      {
        BetterContinents.LogWarning($"{Where()}: 'none' cannot be combined with alt biome names, ignoring 'none'.");
        isNone = false;
      }
      if (!isNone && entries.Count == 0)
      {
        BetterContinents.LogWarning($"{Where()}: no alt biome names before ':', ignoring the line.");
        continue;
      }
      foreach (var entry in entries)
      {
        if (entry.Pattern.IndexOf('*') < 0 && !KnownVanillaNames.Contains(entry.Pattern))
          BetterContinents.Log($"{Where()}: '{entry.Pattern}' is not a vanilla alt biome name. That is fine if another mod adds it; it is checked when the world loads.");
      }

      if (TryParsePin(valuePart, out var px, out var pz))
      {
        if (Classes.Count > MaxEntries)
        {
          BetterContinents.LogError($"{Where()}: more than {MaxEntries} legend entries, ignoring the rest.");
          break;
        }
        var pinClass = new AltBiomeClass { IsPin = true, IsNone = isNone, Color = new Color32(0, 0, 0, 0) };
        pinClass.Entries.AddRange(entries);
        Classes.Add(pinClass);
        Pins.Add(new AltBiomePin { X = px, Z = pz, Class = (byte)(Classes.Count - 1) });
        continue;
      }
      if (!TryParseColor(valuePart, out var color))
      {
        BetterContinents.LogWarning($"{Where()}: cannot read '{valuePart}' as a colour (RRGGBB, #RRGGBB or r,g,b) or as 'at <x>, <z>', ignoring the line.");
        continue;
      }
      if (color.r == 0 && color.g == 0 && color.b == 0)
      {
        BetterContinents.LogWarning($"{Where()}: black (000000) is reserved for unplanted land, ignoring the line.");
        continue;
      }
      var existing = Classes.Skip(1).FirstOrDefault(c => !c.IsPin && c.Color.r == color.r && c.Color.g == color.g && c.Color.b == color.b);
      if (existing != null)
      {
        // Repeating a colour stacks: it is the same as joining the names with "+".
        if (existing.IsNone != isNone)
        {
          BetterContinents.LogWarning($"{Where()}: #{existing.ColorHex} is both 'none' and a list of alt biomes; keeping the alt biomes.");
          existing.IsNone = false;
        }
        foreach (var e in entries)
          if (!existing.Entries.Any(x => string.Equals(x.Pattern, e.Pattern, StringComparison.OrdinalIgnoreCase) && x.Force == e.Force))
            existing.Entries.Add(e);
        continue;
      }
      if (Classes.Count > MaxEntries)
      {
        BetterContinents.LogError($"{Where()}: more than {MaxEntries} legend entries, ignoring the rest.");
        break;
      }
      var cls = new AltBiomeClass { Color = color, IsNone = isNone };
      cls.Entries.AddRange(entries);
      foreach (var other in Classes.Skip(1).Where(c => !c.IsPin))
      {
        if (ColorDistanceSq(other.Color, color) <= 4 * ColorTolerance * ColorTolerance)
          BetterContinents.LogWarning($"{Where()}: #{cls.ColorHex} is within {2 * ColorTolerance} RGB units of #{other.ColorHex}; pixels between them may be assigned to either.");
      }
      Classes.Add(cls);
    }
    BetterContinents.Log($"Alt-biome legend {source}: {Classes.Count(c => !c.IsPin) - 1} colours, {Pins.Count} points.");
  }

  // "#" starts a comment at the start of a line, or after whitespace when followed by whitespace, so that
  // "#2D4613" can still be written as a colour after the ':'.
  internal static string StripComment(string line)
  {
    var trimmed = line.TrimStart();
    if (trimmed.StartsWith("#"))
      return "";
    for (int i = 1; i < line.Length; i++)
    {
      if (line[i] == '#' && char.IsWhiteSpace(line[i - 1]) && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
        return line.Substring(0, i);
    }
    return line;
  }

  private static bool TryParsePin(string value, out float x, out float z)
  {
    x = z = 0f;
    string rest;
    if (value.StartsWith("@"))
      rest = value.Substring(1);
    else if (value.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
      rest = value.Substring(3);
    else
      return false;
    var parts = rest.Split(',');
    return parts.Length == 2
           && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
           && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)
           && !float.IsNaN(x) && !float.IsInfinity(x) && !float.IsNaN(z) && !float.IsInfinity(z);
  }

  internal static bool TryParseColor(string value, out Color32 color)
  {
    color = new Color32(0, 0, 0, 255);
    value = value.Trim();
    var parts = value.Split(',');
    if (parts.Length >= 3)
    {
      if (parts.Length > 4)
        return false;
      var v = new byte[4] { 0, 0, 0, 255 };
      for (int i = 0; i < parts.Length; i++)
        if (!byte.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
          return false;
      color = new Color32(v[0], v[1], v[2], v[3]);
      return true;
    }
    if (value.StartsWith("#"))
      value = value.Substring(1);
    if (value.Length != 6 && value.Length != 8)
      return false;
    if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
      return false;
    if (value.Length == 6)
      color = new Color32((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex, 255);
    else
      color = new Color32((byte)(hex >> 24), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    return true;
  }

  internal static int ColorDistanceSq(Color32 a, Color32 b) =>
    (a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b);
  #endregion

  #region Decoding
  protected override bool LoadTextureToMap<T>(Image<T> image)
  {
    var sw = Stopwatch.StartNew();
    var img = (Image<Rgba32>)(Image)image;
    var palette = Classes.Select((c, i) => (c, i)).Where(t => t.i > 0 && !t.c.IsPin).ToList();
    var cache = new Dictionary<uint, byte>();
    var unknown = new Dictionary<uint, int>();
    int planted = 0;
    const int tolSq = ColorTolerance * ColorTolerance;
    Map = LoadPixels(img, (Rgba32 p) =>
    {
      uint key = ((uint)p.R << 24) | ((uint)p.G << 16) | ((uint)p.B << 8) | p.A;
      if (!cache.TryGetValue(key, out var cls))
      {
        cls = 0;
        // Transparent means "not painted", so the overlay can be exported straight from a layer.
        if (p.A >= AlphaThreshold)
        {
          var c = new Color32(p.R, p.G, p.B, 255);
          int best = ColorDistanceSq(c, new Color32(0, 0, 0, 255));
          bool matched = best <= tolSq;
          foreach (var (entry, index) in palette)
          {
            var d = ColorDistanceSq(c, entry.Color);
            if (d <= tolSq && d < best)
            {
              best = d;
              cls = (byte)index;
              matched = true;
            }
          }
          if (!matched)
            cls = 255;
        }
        cache[key] = cls;
      }
      if (cls == 255)
      {
        unknown[key] = unknown.TryGetValue(key, out var count) ? count + 1 : 1;
        return (byte)0;
      }
      if (cls != 0)
        planted++;
      return cls;
    });
    PlantedPixels = planted;

    BetterContinents.Log($"Alt-biome map {FilePath}: {Size}x{Size}, {planted} planted pixels, decoded in {sw.ElapsedMilliseconds} ms.");
    if (unknown.Count > 0)
    {
      var total = unknown.Values.Sum();
      BetterContinents.LogWarning($"Alt-biome map {Path.GetFileName(FilePath)}: {total} pixels in {unknown.Count} colours match no legend colour (within {ColorTolerance}) and are treated as unplanted. Most common:");
      foreach (var kv in unknown.OrderByDescending(kv => kv.Value).Take(5))
      {
        var c = new Color32((byte)(kv.Key >> 24), (byte)(kv.Key >> 16), (byte)(kv.Key >> 8), 255);
        var nearest = palette.OrderBy(t => ColorDistanceSq(c, t.c.Color)).Select(t => t.c).FirstOrDefault();
        var hint = nearest == null ? "" : $", nearest legend colour #{nearest.ColorHex} ({nearest.Names}) is {Mathf.Sqrt(ColorDistanceSq(c, nearest.Color)):0} away";
        BetterContinents.LogWarning($"    #{c.r:X2}{c.g:X2}{c.b:X2}: {kv.Value} pixels{hint}");
      }
    }
    return true;
  }

  // x, y are normalised map coordinates (0..1, like every other image map, but NOT clamped). Outside the image
  // nothing is planted: clamping would smear the border pixels over the part of the 12 m sector grid that lies
  // beyond the map.
  public byte GetClass(float x, float y)
  {
    if (Map.Length == 0 || Size <= 0 || !(x >= 0f) || !(y >= 0f) || x > 1f || y > 1f)
      return 0;
    int xi = Mathf.Clamp(Mathf.RoundToInt(x * (Size - 1)), 0, Size - 1);
    int yi = Mathf.Clamp(Mathf.RoundToInt(y * (Size - 1)), 0, Size - 1);
    return Map[yi * Size + xi];
  }
  #endregion

  #region Serialization
  // The baked block, written as one length-prefixed byte array under DataKey.AltBiomeMap.
  public byte[] ToBlock()
  {
    var pkg = new ZPackage();
    pkg.Write(BlockVersion);
    pkg.Write(Classes.Count - 1);
    for (int i = 1; i < Classes.Count; i++)
    {
      var c = Classes[i];
      pkg.Write(c.Color.r);
      pkg.Write(c.Color.g);
      pkg.Write(c.Color.b);
      pkg.Write(c.Color.a);
      pkg.Write((byte)((c.IsPin ? 1 : 0) | (c.IsNone ? 2 : 0)));
      pkg.Write(c.Entries.Count);
      foreach (var e in c.Entries)
      {
        pkg.Write(e.Pattern);
        pkg.Write(e.Force);
      }
    }
    pkg.Write(Pins.Count);
    foreach (var p in Pins)
    {
      pkg.Write(p.X);
      pkg.Write(p.Z);
      pkg.Write((int)p.Class);
    }
    pkg.Write(Size);
    pkg.Write(EncodeRle(Map));
    pkg.Write(Legend);
    // Version 2+ fields go here.
    return pkg.GetArray();
  }

  public static ImageMapAltBiome FromBlock(byte[] block)
  {
    var pkg = new ZPackage(block);
    int version = pkg.ReadInt();
    if (version < 1)
      throw new InvalidDataException($"invalid alt-biome map block version {version}");
    if (version > BlockVersion)
      BetterContinents.LogWarning($"The alt-biome map was written by a newer Better Continents (block version {version}); reading the parts this build knows.");
    var map = new ImageMapAltBiome();
    int classCount = pkg.ReadInt();
    if (classCount < 0 || classCount > MaxEntries)
      throw new InvalidDataException($"alt-biome map has {classCount} legend entries (at most {MaxEntries})");
    for (int i = 0; i < classCount; i++)
    {
      var c = new AltBiomeClass { Color = new Color32(pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte(), pkg.ReadByte()) };
      var flags = pkg.ReadByte();
      c.IsPin = (flags & 1) != 0;
      c.IsNone = (flags & 2) != 0;
      int entryCount = pkg.ReadInt();
      for (int k = 0; k < entryCount; k++)
      {
        var pattern = pkg.ReadString();
        var force = pkg.ReadBool();
        c.Entries.Add(new AltBiomeEntry(pattern, force));
      }
      map.Classes.Add(c);
    }
    int pinCount = pkg.ReadInt();
    for (int i = 0; i < pinCount; i++)
    {
      var pin = new AltBiomePin { X = pkg.ReadSingle(), Z = pkg.ReadSingle() };
      var cls = pkg.ReadInt();
      if (cls <= 0 || cls >= map.Classes.Count || !map.Classes[cls].IsPin)
        continue;
      pin.Class = (byte)cls;
      map.Pins.Add(pin);
    }
    map.Size = pkg.ReadInt();
    if (map.Size < 0 || map.Size > 16384)
      throw new InvalidDataException($"alt-biome map declares an impossible size {map.Size}");
    map.Map = DecodeRle(pkg.ReadByteArray(), map.Size * map.Size);
    map.Legend = pkg.ReadString();
    // A class index past the end of the table, or one that belongs to a point plant, cannot come from a
    // decoded image; clear it rather than read garbage later.
    for (int i = 0; i < map.Map.Length; i++)
    {
      var v = map.Map[i];
      if (v != 0 && (v >= map.Classes.Count || map.Classes[v].IsPin))
        map.Map[i] = 0;
    }
    return map;
  }

  private static byte[] EncodeRle(byte[] data)
  {
    using var ms = new MemoryStream();
    int i = 0;
    while (i < data.Length)
    {
      byte v = data[i];
      int run = 1;
      while (i + run < data.Length && data[i + run] == v)
        run++;
      ms.WriteByte(v);
      uint r = (uint)run;
      while (r >= 0x80)
      {
        ms.WriteByte((byte)(r | 0x80));
        r >>= 7;
      }
      ms.WriteByte((byte)r);
      i += run;
    }
    return ms.ToArray();
  }

  private static byte[] DecodeRle(byte[] rle, int expected)
  {
    var result = new byte[expected];
    int pos = 0, i = 0;
    while (i < rle.Length)
    {
      byte v = rle[i++];
      uint run = 0;
      int shift = 0;
      while (true)
      {
        if (i >= rle.Length || shift > 28)
          throw new InvalidDataException("Truncated alt-biome map data");
        byte b = rle[i++];
        run |= (uint)(b & 0x7F) << shift;
        if ((b & 0x80) == 0)
          break;
        shift += 7;
      }
      if (pos + run > expected)
        throw new InvalidDataException("Alt-biome map data is larger than its declared size");
      if (v != 0)
        for (int k = 0; k < run; k++)
          result[pos + k] = v;
      pos += (int)run;
    }
    if (pos != expected)
      throw new InvalidDataException($"Alt-biome map data has {pos} pixels, expected {expected}");
    return result;
  }
  #endregion

  public void Dump(Action<string> output)
  {
    output($"Altbiomemap file ({Size}) {FilePath}");
    for (int i = 1; i < Classes.Count; i++)
    {
      var c = Classes[i];
      if (c.IsPin)
      {
        var pin = Pins.FirstOrDefault(p => p.Class == i);
        output($"    point at {Inv(pin?.X ?? 0f)}, {Inv(pin?.Z ?? 0f)}: {c.Names}");
      }
      else
        output($"    #{c.ColorHex}: {c.Names}");
    }
  }

  private static string Inv(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

  #region Default palette
  // One colour per vanilla alt biome (AltBiomes_Erik.prefab, 32 entries), in the game's own list order,
  // with the base biomes each one plants on (its m_biome). The colours keep every pair of alt-biome
  // colours, and every alt-biome colour and every base-biome colour of the default biome legend (black and
  // white included), more than 2 x ColorTolerance apart, so tolerant matching can never confuse two of them.
  public static readonly (string Name, string Hex, Heightmap.Biome Biomes)[] DefaultPalette =
  [
    ("Mushroom", "FF969C", Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest),
    ("Lantern", "F2A300", Heightmap.Biome.Meadows | Heightmap.Biome.Swamp | Heightmap.Biome.Mountain | Heightmap.Biome.BlackForest
                          | Heightmap.Biome.Plains | Heightmap.Biome.DeepNorth | Heightmap.Biome.Mistlands),
    ("Bones", "F8E1B6", Heightmap.Biome.Swamp | Heightmap.Biome.Plains | Heightmap.Biome.Mistlands),
    ("Menhir", "DDA776", Heightmap.Biome.Meadows | Heightmap.Biome.Plains),
    ("Dark Meadows", "2D4613", Heightmap.Biome.Meadows),
    ("Peaceful Meadows", "ADFFDA", Heightmap.Biome.Meadows),
    ("Dandelion Meadows", "E5C54B", Heightmap.Biome.Meadows),
    ("Raspberry Meadows", "C2185B", Heightmap.Biome.Meadows),
    ("Smalltree Meadows", "6EC03D", Heightmap.Biome.Meadows),
    ("Birch Meadows", "CCFFAC", Heightmap.Biome.Meadows),
    ("Troll Black Forest", "2C7B76", Heightmap.Biome.BlackForest),
    ("Root Black Forest", "6F5E32", Heightmap.Biome.BlackForest),
    ("Ruin Black Forest", "9D6C4B", Heightmap.Biome.BlackForest),
    ("Rock Black Forest", "5A5264", Heightmap.Biome.BlackForest),
    ("Pinetree Black Forest", "004754", Heightmap.Biome.BlackForest),
    ("Blueberry Black Forest", "4A5CB6", Heightmap.Biome.BlackForest),
    ("Hut Swamp", "B1332D", Heightmap.Biome.Swamp),
    ("Bog Swamp", "6E2143", Heightmap.Biome.Swamp),
    ("Bat Swamp", "382B41", Heightmap.Biome.Swamp),
    ("Abomination Swamp", "95B17E", Heightmap.Biome.Swamp),
    ("Wolf Mountain", "93C7BC", Heightmap.Biome.Mountain),
    ("Drake Mountain", "5AAAC2", Heightmap.Biome.Mountain),
    ("Fortress Mountain", "B697AF", Heightmap.Biome.Mountain),
    ("Lox Plains", "CC605F", Heightmap.Biome.Plains),
    ("Goblin Plains", "FF7622", Heightmap.Biome.Plains),
    ("Death Plains", "671E07", Heightmap.Biome.Plains),
    ("Kalhygge Black Forest", "A9AE34", Heightmap.Biome.BlackForest),
    ("Rockless Mistlands", "D99FE8", Heightmap.Biome.Mistlands),
    ("Trees Mistlands", "472577", Heightmap.Biome.Mistlands),
    ("Swords Mistlands", "8A7FDB", Heightmap.Biome.Mistlands),
    ("Hare Mistlands", "D56EBB", Heightmap.Biome.Mistlands),
    ("BroodSwarm Mistlands", "8E2F8E", Heightmap.Biome.Mistlands),
  ];

  // Biome names in the order and spelling the biome legend uses (ImageMapBiome's default biomemap.txt).
  private static readonly Heightmap.Biome[] BiomeOrder =
  [
    Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
    Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
    Heightmap.Biome.Ocean,
  ];

  public static string BiomeList(Heightmap.Biome mask) =>
    string.Join(", ", BiomeOrder.Where(b => (mask & b) != 0).Select(b => b.ToString()));

  private static readonly HashSet<string> KnownVanillaNames =
    new(DefaultPalette.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

  public static string? DefaultColorFor(string name) =>
    DefaultPalette.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Hex).FirstOrDefault();

  // The legend BC writes beside an alt-biome map that has none. Lines end in "\n" on every platform, so the
  // file is byte for byte the same wherever it is written (palettes/altbiomemap.txt in the repository is
  // generated from this property).
  public static string DefaultLegend
  {
    get
    {
      var sb = new StringBuilder();
      void Line(string s = "") => sb.Append(s).Append('\n');
      Line("# Better Continents alt-biome legend. The image beside this file plants Valheim 1.0 alt biomes,");
      Line("# the same way biomemap.png plants base biomes. One line per planted colour:");
      Line("#");
      Line("#   <alt biome>[ + <alt biome> ...]: <colour>      plant these alt biomes where the image has this colour");
      Line("#   <alt biome>[ + <alt biome> ...]: at <x>, <z>   plant them on the whole region at world position x, z (metres)");
      Line("#   none: <colour>                                 a plain region: no alt biome, and no random one either");
      Line("#");
      Line("# Colours are RRGGBB, #RRGGBB or r,g,b. Pixels within 20 RGB units of a listed colour count as it.");
      Line("# Black (000000) and transparent pixels are unplanted: the game's own placement decides there.");
      Line("# A colour only takes over the base biomes its alt biomes belong to, so painting past a biome edge is");
      Line("# harmless: Dark Meadows painted across a meadow and its beach only changes the meadow.");
      Line("# Stack alt biomes on one region with +, or by listing the same colour on several lines.");
      Line("# Names are the game's internal names, case-insensitive (bc_altbiomes names lists them in game).");
      Line("#   *      wildcard, plants EVERY matching alt biome that fits the land, e.g. *Mistlands");
      Line("#   !Name  force an alt biome the game would refuse here (disabled, wrong base biome, incompatible)");
      Line("# Planted regions ignore the game's size, distance, height, position and neighbour rules, and count");
      Line("# toward the game's per-alt-biome maximum when it places the rest at random.");
      Line("#");
      Line("# Examples (remove the leading # to use them):");
      Line("# Dark Meadows + Lantern: 0080C0");
      Line("# none: C030F0");
      Line("# Troll Black Forest: at 2750, -1210");
      Line("#");
      Line("# Default palette, one colour per vanilla alt biome (the comment is the land it plants on):");
      int width = DefaultPalette.Max(p => p.Name.Length + 8);
      foreach (var (name, hex, biomes) in DefaultPalette)
        Line($"{name}: {hex}".PadRight(width) + " # " + BiomeList(biomes));
      return sb.ToString();
    }
  }
  #endregion
}

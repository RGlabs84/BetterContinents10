// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

// Every map's legend in one place: the text file beside a picture, <image name>.txt, that says what its colours mean.
//
// The six kinds of legend are read in six ways, and every way is frozen: a legend file must load exactly as it did in
// Better Continents 0.9.4 (the user's rule for the unifying refactor; golden-tests' legend fuzz checks it), and paint,
// terrain and old biome legends are also read again from a world's settings each time the world loads. So the ways
// are written down here side by side, and share what they have in common, but none of them may change.
//
// Biome: the file's lines joined with '|' (so a '|' splits a line too). An entry is "name: colour" with exactly one
//   ':'. Blank lines and '#' comments (after any byte order mark or zero-width space) are skipped. Strict colours. A
//   bad entry is logged, counted and skipped: a vanilla biome with a bad colour keeps its default one, and an unknown
//   name's colour reads as None. A legend with no entry gives the default colours.
// Location: as biome, but the name is everything before the last ':' (Expand World Data's "Name:Alias"), and a name
//   listed twice keeps its last colour.
// Paint: the lines joined with '|'. An entry is "mask: image colour" with exactly one ':'; anything else is skipped
//   without a word (there are no comments: a '#' line with one ':' is an entry). Lenient colours. The first entry for
//   an image colour wins. A broken number throws: read from the file, the whole legend is lost (logged); read from a
//   world's settings, the exception reaches the world's loading.
// Terrain: as paint, but the mask is a ground name (lower-cased in the player's culture) or a colour, and an empty
//   legend is the default one.
// Spawn and vegetation: the file's lines. An entry is "colour: entries" with exactly one ':'; blank lines, '#' lines
//   (culture-aware) and lines without exactly one ':' are skipped. Lenient colours; a broken number ends the legend at
//   that line (logged). White is always the first entry, "none". A missing legend is written empty.
// Alt biome: the file's text, at "\n" (and "\r\n"). An entry is "names: colour" or "names: at x, z", at the first ':';
//   '#' starts a comment at the start of a line, or alone after a space. Alt-biome colours. A bad line is logged with
//   its number and skipped; black is reserved for unplanted land; a colour listed again adds its names.
//
// Strict colours (TryParseColor32): a hex colour (ImageSharp's: #RGB, RRGGBB, RRGGBBAA...) or 3 or 4 numbers from 0
// to 255, read invariantly; anything else is not a colour. Lenient colours (ParseRGBA): the same hex, else a warning and
// transparent black, or 3 or 4 numbers read with byte.Parse in the player's culture, which throws on anything else.
// Alt-biome colours (TryParseAltBiomeColour): RRGGBB or RRGGBBAA with an optional '#', or 3 or 4 numbers.
internal static class Legends
{
  // The legend of a picture: <image name>.txt beside it.
  internal static string FileFor(string imagePath) =>
    Path.Combine(Path.GetDirectoryName(imagePath) ?? "", Path.GetFileNameWithoutExtension(imagePath) + ".txt");

  // A legend file the way the '|' legends (biome, location, paint, terrain) read it: its lines joined with '|'.
  internal static string ReadJoined(string path) => string.Join("|", File.ReadAllLines(path));

  // A missing '|' legend gets the default one, an entry a line.
  internal static void WriteDefault(string path, string defaultLegend) => File.WriteAllLines(path, defaultLegend.Split('|'));

  // A biome or location legend line to skip: blank, or a comment. Ordinal, not StartsWith("#"): that is culture-sensitive
  // and differs between the game's Mono and .NET for lines starting with an invisible character.
  internal static bool IsBlankOrComment(string line)
  {
    line = line.Trim().TrimStart('﻿', '​').Trim();
    return line.Length == 0 || line[0] == '#';
  }

  // Strict: false for anything that is not a hex colour or at least three comma-separated numbers from 0 to 255 (a
  // fourth is alpha).
  internal static bool TryParseColor32(string color, out Color32 result)
  {
    result = default;
    color = color.Trim();
    var split = color.Split(',');
    if (split.Length == 1)
    {
      if (!SixLabors.ImageSharp.Color.TryParseHex(color, out var hex))
        return false;
      Rgba32 rgba = hex;
      result = new Color32(rgba.R, rgba.G, rgba.B, rgba.A);
      return true;
    }
    if (split.Length < 3)
      return false;
    var parts = new byte[] { 0, 0, 0, 255 };
    for (int i = 0; i < Math.Min(split.Length, 4); i++)
      if (!byte.TryParse(split[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parts[i]))
        return false;
    result = new Color32(parts[0], parts[1], parts[2], parts[3]);
    return true;
  }

  // Lenient (paint, terrain, spawn and vegetation legends).
  internal static Color32 ParseColor32(string color)
  {
    var rgba = ParseRGBA(color);
    return new Color32(rgba.R, rgba.G, rgba.B, rgba.A);
  }
  internal static Rgba32 ParseRGBA(string color)
  {
    color = color.Trim();
    var split = color.Split(',').ToArray();
    if (split.Length == 1)
    {
      if (SixLabors.ImageSharp.Color.TryParseHex(color, out var c))
        return c;
      else
      {
        BetterContinents.LogWarning($"Cannot parse color {color}");
        return new Rgba32(0, 0, 0, 0);
      }
    }
    if (split.Length < 3)
    {
      BetterContinents.LogWarning($"Cannot parse color {color}");
      return new Rgba32(0, 0, 0, 0);
    }
    var a = split.Length == 3 ? "255" : split[3];
    return new Rgba32(byte.Parse(split[0]), byte.Parse(split[1]), byte.Parse(split[2]), byte.Parse(a));
  }

  // Alt-biome legends, and the colours a world export gives alt biomes.
  internal static bool TryParseAltBiomeColour(string value, out Color32 color)
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

  // An alt-biome legend line without its comment. "#" starts a comment at the start of a line, or after whitespace when
  // followed by whitespace, so that "#2D4613" can still be written as a colour after the ':'.
  internal static string StripAltBiomeComment(string line)
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
}

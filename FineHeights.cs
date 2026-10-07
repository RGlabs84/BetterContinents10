// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BetterContinents;

// Fine heights, format 1: a heightmap-fine.png beside a heightmap.png gives it 8 more bits. tools/fine_ref.py is the reference of
// everything here (the writer, the record, where the file is looked for), and checks a pair the way Better Continents does.
//
// A heightmap.png holds 16-bit heights, so a step of it is 200 * Heightmap Amount / 65535 metres: 1.5 cm at Amount 5, 25 cm at 81.
// Ground that climbs by less than a step a pixel climbs in terraces. The fine file (the same size, 8-bit grey, same folder, '-fine'
// before the name's extension) holds for every pixel a signed offset f in 1/256 of a step; with c the heightmap's value,
//   N = 256 c + f      v = N / 16776960      metres = (v * Heightmap Amount - 0.15 + Sea Level Adjustment) * 200
// so a pixel with f = 0 reads exactly as c / 65535 always did, and heightmap.png itself is what it was. An optional text chunk in the
// fine file (keyword BetterContinents) says 'Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 89ABCDEF': the CRC-32 of the whole
// heightmap.png file the fine file was made for, so that one edited since is not refined by it.
internal static class FineHeights
{
  // The fine format this version reads (a heightmap block's fine format byte, and the record's Fine Format).
  internal const int Format = 1;

  // N = v * Scale: 65535 steps of 256.
  internal const double Scale = 65535 * 256;

  // The most bits a fine file can use: all of its bytes.
  internal const int MaxBits = 8;

  // Where a heightmap's fine file is looked for: '-fine' before the name's last extension, in the same folder ('.png' when the name has
  // none: iceland -> iceland-fine.png); null for no path (a map made from a world's settings has none).
  internal static string? FinePath(string heightmapPath)
  {
    if (string.IsNullOrEmpty(heightmapPath))
      return null;
    string name;
    try
    {
      name = Path.GetFileName(heightmapPath);
    }
    catch (ArgumentException)
    {
      // A path with characters no file name holds: no file of it was read, so there is none beside it.
      return null;
    }
    var folder = heightmapPath.Substring(0, heightmapPath.Length - name.Length);
    int dot = name.LastIndexOf('.');
    string stem, extension;
    if (dot < 0 || dot == name.Length - 1)
    {
      stem = dot >= 0 ? name.Substring(0, dot) : name;
      extension = ".png";
    }
    else
    {
      stem = name.Substring(0, dot);
      extension = name.Substring(dot);
    }
    return folder + stem + "-fine" + extension;
  }

  // How many bits the fine bytes use, from the OR of all of them: 8 less the zero bits at the end; 0 when every byte is 0.
  internal static int BitsUsed(int orOfBytes)
  {
    if (orOfBytes == 0)
      return 0;
    int trailing = 0;
    while ((orOfBytes & 1) == 0)
    {
      orOfBytes >>= 1;
      trailing++;
    }
    return MaxBits - trailing;
  }

  // The text a fine file's record holds ('Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 89ABCDEF'), for the heightmap.png file
  // whose bytes have this CRC-32.
  internal static string RecordText(int bits, uint heightmapCrc) =>
    string.Format(CultureInfo.InvariantCulture, "Fine Format = {0}; Fine Bits = {1}; Heightmap CRC-32 = {2:X8}", Format, bits, heightmapCrc);

  // The CRC-32 of a file (a heightmap.png the fine file is made for), read a block at a time.
  internal static uint FileCrc(string path)
  {
    uint crc = 0;
    var block = new byte[1 << 20];
    using var stream = File.OpenRead(path);
    int n;
    while ((n = stream.Read(block, 0, block.Length)) > 0)
      crc = Crc32.Continue(crc, block, 0, n);
    return crc;
  }

  // What a fine file's record says: the parts that read (null for the ones that are not there).
  internal sealed class Record
  {
    internal long? Format;
    internal long? Bits;
    internal uint? HeightmapCrc;
    internal bool Any => Format != null || Bits != null || HeightmapCrc != null;
  }

  // The record among a PNG's text chunks: the first BetterContinents text with any fine part. Parts are 'key = value', split at ';'
  // and at '='; keys must match exactly (spaces around them aside); a value that does not read is skipped, as in the heightmap's own
  // record (HeightmapRecord.From), which this one is never mistaken for (it has other keys). Null when there is none.
  internal static Record? ReadRecord(IEnumerable<(string Keyword, string Text)> texts)
  {
    foreach (var (keyword, text) in texts)
    {
      if (keyword != HeightmapRecord.Keyword)
        continue;
      var record = new Record();
      foreach (var part in text.Split(';'))
      {
        var kv = part.Split('=');
        if (kv.Length != 2)
          continue;
        string key = kv[0].Trim(), value = kv[1].Trim();
        if ((key == "Fine Format" || key == "Fine Bits") && Integer(value) is { } number)
        {
          if (key == "Fine Format")
            record.Format = number;
          else
            record.Bits = number;
        }
        else if (key == "Heightmap CRC-32" && Crc(value) is { } crc)
          record.HeightmapCrc = crc;
      }
      if (record.Any)
        return record;
    }
    return null;
  }

  // [+-]?[0-9]+ (ASCII digits); a number too large for a long is as far from 1 as it can be.
  private static long? Integer(string value)
  {
    int at = value.Length > 0 && (value[0] == '+' || value[0] == '-') ? 1 : 0;
    if (at == value.Length)
      return null;
    for (int i = at; i < value.Length; i++)
      if (value[i] < '0' || value[i] > '9')
        return null;
    return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : value[0] == '-' ? long.MinValue : long.MaxValue;
  }

  // [0-9A-Fa-f]+ up to 0xFFFFFFFF, leading zeros as many as there are.
  private static uint? Crc(string value)
  {
    if (value.Length == 0)
      return null;
    foreach (var c in value)
      if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
        return null;
    var digits = value.TrimStart('0');
    if (digits.Length > 8)
      return null;
    return digits.Length == 0 ? 0u : uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
  }

  // A length in metres, as the log says it: 24.7 cm, 1.5 cm, 0.97 mm, 1.25 m.
  internal static string Length(double metres) =>
    metres >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0:0.##} m", metres)
    : metres >= 0.01 ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} cm", metres * 100)
    : string.Format(CultureInfo.InvariantCulture, "{0:0.##} mm", metres * 1000);

  // One step of a 16-bit heightmap, in metres, at a Heightmap Amount; and the same divided by the bits the fine bytes use.
  internal static double Step(float heightmapAmount) => 200.0 * heightmapAmount / 65535.0;
  internal static double Resolution(float heightmapAmount, int bits) => Step(heightmapAmount) / (1 << bits);

  // Bytes as the log says them: 9.2 MB, 413 KB.
  internal static string Bytes(long bytes) =>
    bytes >= 1_000_000 ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", bytes / 1e6)
    : string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", bytes / 1e3);
}

// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// Fine heights (FineHeights.cs, ImageMapFloat; tools/fine_ref.py is the reference): the four pairs fine_ref.py wrote (fine-fixtures/)
// read to the heights it expects, with and without the fine file; pairs written here round-trip against a double-precision bilinear
// reference at pixel centres, between pixels, on tile borders and at the edges, with Heightmap Alpha too; every way a fine file is
// refused says what fine_ref.py's judge() says and leaves the heightmap as it was; where the file is looked for; a world's tiles in
// format 2 (and format 1, as it was, for every other map); older readers refuse it; a world with fine heights saved, sent, shared
// and loaded; the cache with two grids and a damaged fine tile; PngRows reading 8-bit grey; the record, the file's header and CRC.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using ISImage = SixLabors.ImageSharp.Image;
using WorldCache = BetterContinents.BetterContinents.ZNetPatch.WorldCache;

internal static partial class Program
{
  private const float FineAmount = 81f;
  private const double FineScale = 65535.0 * 256.0;

  // The rule a new world of the newest settings version gives the heightmap.
  private static ImageMapFloat.FineRule FineRead(float amount = FineAmount) => new(ImageMapFloat.FineUse.Read, 12, amount);
  private static ImageMapFloat.FineRule FineNever => default;

  private static int LogMark()
  {
    lock (LogHandler.Lines)
      return LogHandler.Lines.Count;
  }

  private static List<string> LogSince(int mark)
  {
    lock (LogHandler.Lines)
      return LogHandler.Lines.Skip(mark).ToList();
  }

  // ---- the writer of the format (tools/fine_ref.py encode), and the files -------------------------------------------------

  // c = floor(x + 0.5) for x = v * 65535 (what a plain 16-bit writer makes), f the rest in 1/256 of a step, rounded to even to a
  // multiple of 2^(8 - bits) and kept in -128 .. 128 - 2^(8 - bits).
  private static (ushort C, sbyte F) FineEncode(double v, int bits)
  {
    double x = Math.Min(Math.Max(v, 0.0), 1.0) * 65535.0;
    double c = Math.Floor(x + 0.5);
    if (bits <= 0)
      return ((ushort)c, 0);
    int q = 1 << (8 - bits);
    double f = Math.Round((x - c) * (256.0 / q), MidpointRounding.ToEven) * q;
    return ((ushort)c, (sbyte)Math.Min(Math.Max(f, -128.0), 128.0 - q));
  }

  // Heights in 0..1 (file pixel x, y): rolling plains, a flat top of exactly 1, a flat sea of exactly 0, a noisy band.
  private static double FineHeight(int x, int y, int size)
  {
    double u = size > 1 ? x / (double)(size - 1) : 0.5, w = size > 1 ? y / (double)(size - 1) : 0.5;
    double v = 0.45 + 0.4 * Math.Sin(u * 9) * Math.Cos(w * 7) + 0.12 * Math.Sin(u * 61 + w * 43);
    if (u > 0.2 && u < 0.35 && w > 0.6 && w < 0.8)
      v = 1.0;
    if (u > 0.7 && w < 0.2)
      v = 0.0;
    if (u > 0.5 && u < 0.52)
      v += 0.2 * ((x * 7919 + y * 104729) % 13) / 12.0;
    return Math.Min(Math.Max(v, 0.0), 1.0);
  }

  // Where a pair's fine record goes (a tEXt chunk unless said).
  private enum FineRecord { None, After, Before, ZAfter, ITextBefore, WrongCrc, Format2, CrcOnly, BitsOnly }

  // A pair of arrays (file order, row 0 north) and the files of it in `dir`.
  private sealed class FinePairFiles
  {
    public string Dir = "", Heightmap = "", Fine = "";
    public int Size;
    public ushort[] C = [];
    public sbyte[] F = [];
    public ushort[] Alpha = [];
    public byte[] HeightmapBytes = [], FineBytes = [];
    public uint Crc;
  }

  private static byte[] FineChunk(string type, byte[] body)
  {
    var data = new byte[12 + body.Length];
    BinaryPrimitives.WriteUInt32BigEndian(data, (uint)body.Length);
    Encoding.ASCII.GetBytes(type).CopyTo(data, 4);
    body.CopyTo(data, 8);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + body.Length), Crc32.Compute(data, 4, 4 + body.Length));
    return data;
  }

  private static byte[] Latin1(string text) => text.Select(c => (byte)c).ToArray();

  private static byte[] Zlib(byte[] bytes)
  {
    using var output = new MemoryStream();
    output.WriteByte(0x78);
    output.WriteByte(0x9C);
    using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
      deflate.Write(bytes, 0, bytes.Length);
    uint a = 1, b = 0;
    foreach (var x in bytes)
    {
      a = (a + x) % 65521;
      b = (b + a) % 65521;
    }
    var adler = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
    output.Write(adler, 0, 4);
    return output.ToArray();
  }

  private static byte[] TextChunkBody(string keyword, string text) => [.. Latin1(keyword), 0, .. Latin1(text)];
  private static byte[] ZTextChunkBody(string keyword, string text) => [.. Latin1(keyword), 0, 0, .. Zlib(Latin1(text))];
  private static byte[] ITextChunkBody(string keyword, string text, bool compressed) =>
    [.. Latin1(keyword), 0, (byte)(compressed ? 1 : 0), 0, 0, 0, .. (compressed ? Zlib(Encoding.UTF8.GetBytes(text)) : Encoding.UTF8.GetBytes(text))];

  // A PNG with a chunk put in: before the image data (right after IHDR) or after it (before IEND).
  private static byte[] WithChunk(byte[] png, byte[] chunk, bool afterData) =>
    afterData ? [.. png[..^12], .. chunk, .. png[^12..]] : [.. png[..33], .. chunk, .. png[33..]];

  private static byte[] Grey8(int size, Func<int, int, byte> pixel) => Png(size, (x, y) => new L8(pixel(x, y)), PngColorType.Grayscale, PngBitDepth.Bit8);
  private static byte[] Grey16(int size, Func<int, int, ushort> pixel) => Png(size, (x, y) => new L16(pixel(x, y)), PngColorType.Grayscale, PngBitDepth.Bit16);

  // The record's text for a heightmap.png with this CRC.
  private static string FineRecordText(int bits, uint crc) => $"Fine Format = 1; Fine Bits = {bits}; Heightmap CRC-32 = {crc:X8}";

  // heightmap.png (16-bit grey, or grey and alpha) and heightmap-fine.png (8-bit grey) from heights, as fine_ref.py writes a pair.
  private static FinePairFiles FinePair(string dir, int size, Func<int, int, double> height, int bits, bool alpha = false, FineRecord record = FineRecord.After,
    string heightmapName = "heightmap.png", string fineName = "heightmap-fine.png")
  {
    Directory.CreateDirectory(dir);
    var pair = new FinePairFiles { Dir = dir, Size = size, C = new ushort[size * size], F = new sbyte[size * size], Alpha = new ushort[size * size] };
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
      {
        var (c, f) = FineEncode(height(x, y), bits);
        pair.C[y * size + x] = c;
        pair.F[y * size + x] = f;
        pair.Alpha[y * size + x] = (ushort)((x * 211 + y * 17) & 0xFFFF);
      }
    pair.HeightmapBytes = alpha
      ? Png(size, (x, y) => new La32(pair.C[y * size + x], pair.Alpha[y * size + x]), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16)
      : Grey16(size, (x, y) => pair.C[y * size + x]);
    pair.Crc = Crc32.Compute(pair.HeightmapBytes, 0, pair.HeightmapBytes.Length);
    var fine = Grey8(size, (x, y) => (byte)pair.F[y * size + x]);
    pair.FineBytes = FineRecordBytes(fine, record, bits, pair.Crc);
    pair.Heightmap = Path.Combine(dir, heightmapName);
    pair.Fine = Path.Combine(dir, fineName);
    File.WriteAllBytes(pair.Heightmap, pair.HeightmapBytes);
    File.WriteAllBytes(pair.Fine, pair.FineBytes);
    return pair;
  }

  private static byte[] FineRecordBytes(byte[] fine, FineRecord record, int bits, uint crc) => record switch
  {
    FineRecord.None => fine,
    FineRecord.After => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", FineRecordText(bits, crc))), true),
    FineRecord.Before => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", FineRecordText(bits, crc))), false),
    FineRecord.ZAfter => WithChunk(fine, FineChunk("zTXt", ZTextChunkBody("BetterContinents", FineRecordText(bits, crc))), true),
    FineRecord.ITextBefore => WithChunk(fine, FineChunk("iTXt", ITextChunkBody("BetterContinents", FineRecordText(bits, crc), true)), false),
    FineRecord.WrongCrc => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", FineRecordText(bits, crc ^ 1))), true),
    FineRecord.Format2 => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", "Fine Format = 2")), false),
    FineRecord.CrcOnly => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", $"Heightmap CRC-32 = {crc:x}")), true),
    _ => WithChunk(fine, FineChunk("tEXt", TextChunkBody("BetterContinents", "Fine Bits = 4")), true),
  };

  private static ImageMapFloat? FineMap(string path, ImageMapFloat.HeightAlpha alpha = ImageMapFloat.HeightAlpha.None, bool compact = false, ImageMapFloat.FineRule? rule = null) =>
    ImageMapFloat.Create(path, alpha, compact, rule ?? FineRead());

  // ---- the reference: a double-precision bilinear blend of N = 256 c + f, the position as the game works it out (float) ---------

  private static double FineReference(ushort[] c, sbyte[]? f, int size, float x, float y)
  {
    float xa = x * (size - 1);
    float ya = y * (size - 1);
    int xi = Mathf.FloorToInt(xa), yi = Mathf.FloorToInt(ya);
    double xd = xa - xi, yd = ya - yi;
    int x0 = Mathf.Clamp(xi, 0, size - 1), x1 = Mathf.Clamp(xi + 1, 0, size - 1);
    int y0 = Mathf.Clamp(yi, 0, size - 1), y1 = Mathf.Clamp(yi + 1, 0, size - 1);
    double N(int px, int py)
    {
      int at = (size - 1 - py) * size + px;
      return c[at] * 256.0 + (f == null ? 0 : f[at]);
    }
    double n00 = N(x0, y0), n10 = N(x1, y0), n01 = N(x0, y1), n11 = N(x1, y1);
    double top = n00 + (n10 - n00) * xd, bottom = n01 + (n11 - n01) * xd;
    return (top + (bottom - top) * yd) / FineScale;
  }

  // The largest distance, in units of N (1/256 of a step), between the map's samples and the reference over many points; the number
  // of points. Points past the NaN and infinity the other tests use.
  private static (double Worst, int Count) FineWorst(ImageMapFloat map, FinePairFiles pair, bool withFine, int seed)
  {
    double worst = 0;
    int count = 0;
    foreach (var (x, y) in Points(pair.Size, seed))
    {
      if (!float.IsFinite(x) || !float.IsFinite(y))
        continue;
      double got = map.GetValue(x, y), want = FineReference(pair.C, withFine ? pair.F : null, pair.Size, x, y);
      worst = Math.Max(worst, Math.Abs(got - want) * FineScale);
      count++;
    }
    return (worst, count);
  }

  // ---- the file's header, chunk by chunk (for building damaged files) --------------------------------------------------------

  private static List<(string Type, int At, int Length)> FineChunks(byte[] png)
  {
    var list = new List<(string, int, int)>();
    for (int at = 8; at + 12 <= png.Length;)
    {
      int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
      list.Add((Encoding.ASCII.GetString(png, at + 4, 4), at, length));
      at += 12 + length;
    }
    return list;
  }

  // The same PNG with another header (IHDR's width, height, depth, colour type and interlace method), its checksum made again.
  private static byte[] WithHeader(byte[] png, int? width = null, int? height = null, byte? depth = null, byte? colour = null, byte? interlace = null)
  {
    var copy = (byte[])png.Clone();
    if (width != null) BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(16), (uint)width);
    if (height != null) BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(20), (uint)height);
    if (depth != null) copy[24] = depth.Value;
    if (colour != null) copy[25] = colour.Value;
    if (interlace != null) copy[28] = interlace.Value;
    BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(29), Crc32.Compute(copy, 12, 17));
    return copy;
  }

  // ---- the section --------------------------------------------------------------------------------------------------------

  private static void FineHeightsTests()
  {
    FineFormatTests();
    FineFixtureTests();
    FineRoundTrips();
    FineNeutral();
    FineRejections();
    FineLookup();
    FineBlocks();
    FineWorlds();
    FineCache();
    FineSources();
    FinePngRows();
    FineSpeed();
  }

  // ---- the format: the record, the path, the bits, the worked example ------------------------------------------------------

  private static void FineFormatTests()
  {
    Section("fine heights: where the file is, what the record says, the bits");
    var paths = new (string Heightmap, string? Fine)[]
    {
      ("heightmap.png", "heightmap-fine.png"), (Path.Combine("a.b", "c.v2.png"), Path.Combine("a.b", "c.v2-fine.png")), ("MAP.PNG", "MAP-fine.PNG"),
      ("iceland", "iceland-fine.png"), ("file.", "file-fine.png"), (".png", "-fine.png"), ("", null),
      (Path.Combine("/maps", "my.maps", "Iceland 16k.png"), Path.Combine("/maps", "my.maps", "Iceland 16k-fine.png")), ("x.tar.gz", "x.tar-fine.gz"),
    };
    var wrong = paths.Where(p => FineHeights.FinePath(p.Heightmap) != p.Fine).Select(p => $"{p.Heightmap} -> {FineHeights.FinePath(p.Heightmap)}").ToList();
    C(wrong.Count == 0 && FineHeights.FinePath(null!) == null, "the fine file is the heightmap's path with '-fine' before its last extension ('.png' when it has none), no path no file" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""));
    C(FineHeights.BitsUsed(0) == 0 && new[] { 1, 16, 48, 128, 255, 72 }.Select(FineHeights.BitsUsed).SequenceEqual([8, 4, 4, 1, 8, 5]), "the bits a fine file uses: 8 less the zero bits at the end of the OR of its bytes");
    C(FineRecordText(4, 0x89ABCDEF) == "Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 89ABCDEF" && FineHeights.RecordText(4, 0x89ABCDEF) == FineRecordText(4, 0x89ABCDEF) && FineHeights.RecordText(8, 0x0000ABCD).EndsWith("= 0000ABCD"),
      "the record's text: format, bits and the heightmap's CRC-32 in 8 capital hex digits");

    (string Keyword, string Text)[] One(string text) => [("BetterContinents", text)];
    var good = FineHeights.ReadRecord(One("Heightmap CRC-32 = 0a1b2c3d ;Fine Bits=4; Fine Format = 1; Other = x"));
    C(good is { Format: 1, Bits: 4, HeightmapCrc: 0x0A1B2C3D }, "a record's parts in any order, spaces and case of the hex as they come, an unknown part ignored");
    C(FineHeights.ReadRecord([("Other", "Fine Format = 2"), ("BetterContinents", "Fine Format = 2")]) is { Format: 2 }, "only the BetterContinents keyword counts");
    C(FineHeights.ReadRecord(One("Heightmap Amount = 81; Sea Level Adjustment = 0")) == null, "the heightmap's own record is no fine record");
    C(FineHeights.ReadRecord(One("Heightmap CRC-32 = 0x12; Fine Format = one; Fine Bits = 1.5; Fine = 3")) == null, "values that do not read are skipped (0x12, one, 1.5), and with them the record");
    C(FineHeights.ReadRecord([("BetterContinents", "Heightmap Amount = 81; Sea Level Adjustment = 0"), ("BetterContinents", "Fine Bits = 4"), ("BetterContinents", "Fine Bits = 8")]) is { Bits: 4 },
      "the first BetterContinents text that has a fine part is the record");
    C(FineHeights.ReadRecord(One("Fine Format = +1; Fine Bits = -3; Heightmap CRC-32 = 000000000089ABCD")) is { Format: 1, Bits: -3, HeightmapCrc: 0x89ABCD }
      && FineHeights.ReadRecord(One("Heightmap CRC-32 = 123456789")) == null && FineHeights.ReadRecord(One("Heightmap CRC-32 = FFFFFFFF")) is { HeightmapCrc: 0xFFFFFFFF }
      && FineHeights.ReadRecord(One("Fine Format = 99999999999999999999")) is { Format: > 1 } && FineHeights.ReadRecord(One("Fine Format = ; Fine Bits = +")) == null
      && FineHeights.ReadRecord(One("Fine Format = 1 = 2; Fine Bits = 4")) is { Format: null, Bits: 4 },
      "signs, leading zeros in the CRC (9 digits past 0xFFFFFFFF refused), a number too large for a number, a part with two '='");

    // The worked example of the format: 123.456 m at Heightmap Amount 81 (Sea Level 0.5): v = 0.009472593, x = 620.7864, c = 621, f = -55.
    double v = (123.456 / 200 + 0.15) / 81;
    var (c, f) = FineEncode(v, 8);
    var (c4, f4) = FineEncode(v, 4);
    C(c == 621 && f == -55 && (byte)f == 201 && c4 == 621 && f4 == -48 && (byte)f4 == 208, $"123.456 m at Amount 81 is c 621, f -55 (byte 201); 4 bits f -48 (byte 208): c {c}, f {f}, 4 bits {f4}");
    var dir = Path.Combine(Work, "fine-worked");
    Directory.CreateDirectory(dir);
    foreach (var (cc, ff, metres, alone) in new[] { (c, f, 123.4557, 123.5088), (c4, f4, 123.4625, 123.5088) })
    {
      File.WriteAllBytes(Path.Combine(dir, "heightmap.png"), Grey16(2, (x, y) => cc));
      File.WriteAllBytes(Path.Combine(dir, "heightmap-fine.png"), Grey8(2, (x, y) => (byte)ff));
      var read = FineMap(Path.Combine(dir, "heightmap.png"))!;
      var plain = ImageMapFloat.Create(Path.Combine(dir, "heightmap.png"), ImageMapFloat.HeightAlpha.None, false, FineNever)!;
      double Metres(float value) => ((double)value * 81 - 0.15) * 200;
      C(read.HasFine && Math.Abs(Metres(read.GetValue(0.5f, 0.5f)) - metres) < 5e-4 && Math.Abs(Metres(plain.GetValue(0.5f, 0.5f)) - alone) < 5e-4,
        $"read, it is {Metres(read.GetValue(0.5f, 0.5f)):F4} m ({metres:F4}); without the fine file {Metres(plain.GetValue(0.5f, 0.5f)):F4} m ({alone:F4})");
    }
  }

  // ---- the pairs fine_ref.py wrote ---------------------------------------------------------------------------------------------

  private static string FixturesDir()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TileTests.csproj")))
      dir = dir.Parent;
    return Path.Combine(dir!.FullName, "fine-fixtures");
  }

  private static void FineFixtureTests()
  {
    Section("fine heights: the pairs fine_ref.py wrote read to the heights it expects, with the fine file and without it");
    foreach (var name in new[] { "f8", "f4-policy", "f8-alpha", "f4-before" })
    {
      var dir = Path.Combine(FixturesDir(), name);
      var lines = File.ReadAllLines(Path.Combine(dir, "samples.tsv"));
      var header = lines[0];
      int used = int.Parse(System.Text.RegularExpressions.Regex.Match(header, @"\((\d+) used\)").Groups[1].Value);
      uint crc = uint.Parse(System.Text.RegularExpressions.Regex.Match(header, @"heightmap CRC-32 ([0-9A-F]{8})").Groups[1].Value, NumberStyles.HexNumber);
      var samples = lines.Where(l => !l.StartsWith("#") && !l.StartsWith("map_x")).Select(l => l.Split('\t'))
        .Select(p => (X: float.Parse(p[0], CultureInfo.InvariantCulture), Y: float.Parse(p[1], CultureInfo.InvariantCulture),
          Fine: double.Parse(p[2], CultureInfo.InvariantCulture), Alone: double.Parse(p[3], CultureInfo.InvariantCulture))).ToList();
      var path = Path.Combine(dir, "heightmap.png");
      bool alphaPair = name == "f8-alpha";
      foreach (var mode in alphaPair ? new[] { ImageMapFloat.HeightAlpha.Blend, ImageMapFloat.HeightAlpha.None } : new[] { ImageMapFloat.HeightAlpha.None })
      {
        int mark = LogMark();
        var map = FineMap(path, mode)!;
        var log = LogSince(mark);
        var alone = ImageMapFloat.Create(path, mode, false, FineNever)!;
        double worstFine = 0, worstAlone = 0;
        foreach (var s in samples)
        {
          worstFine = Math.Max(worstFine, Math.Abs(map.GetValue(s.X, s.Y) - s.Fine) * FineScale);
          worstAlone = Math.Max(worstAlone, Math.Abs(alone.GetValue(s.X, s.Y) - s.Alone) * FineScale);
        }
        C(map.HasFine && map.FineBits == used && map.FineNote == null && map.Compact && !alone.HasFine && samples.Count > 200,
          $"{name} ({mode}): {used} bits, compact, no note; {samples.Count} sample points");
        C(worstFine <= 1.0 && worstAlone <= 1.0, $"{name} ({mode}): every sample within 1 unit of N (1/256 step) of fine_ref.py's, with the fine file ({worstFine:F3}) and without it ({worstAlone:F3})");
        C(log.Any(l => l.StartsWith($"[Log] [BetterContinents] Fine heights: heightmap-fine.png refines heightmap.png with {used} bits a pixel: ground in steps of")),
          $"{name} ({mode}): the log says the file refines the heightmap with {used} bits a pixel");
        if (alphaPair)
        {
          bool same = true;
          foreach (var (x, y) in Points(300, 5, 500))
            if (float.IsFinite(x) && float.IsFinite(y))
              same &= Same(map.GetAlpha(x, y), alone.GetAlpha(x, y));
          C(same && map.HasAlpha == (mode == ImageMapFloat.HeightAlpha.Blend), $"{name} ({mode}): the alpha reads as it does without the fine file");
        }
      }
      // The record's CRC is the heightmap file's.
      var bytes = File.ReadAllBytes(path);
      C(Crc32.Compute(bytes, 0, bytes.Length) == crc && FineHeights.FileCrc(path) == crc, $"{name}: the CRC-32 in the header is the heightmap file's (streamed, and in one go)");
    }
  }

  // ---- round trips: written here, read back --------------------------------------------------------------------------------------

  private static void FineRoundTrips()
  {
    Section("fine heights: pairs written here read to a double-precision bilinear reference, at every kind of point");
    foreach (var (size, bits, alpha) in new[] { (1, 8, false), (129, 8, false), (129, 4, false), (300, 8, true), (300, 4, false), (1024, 8, false) })
    {
      var pair = FinePair(Path.Combine(Work, $"fine-rt-{size}-{bits}-{alpha}"), size, (x, y) => FineHeight(x, y, size), bits, alpha);
      var mode = alpha ? ImageMapFloat.HeightAlpha.Blend : ImageMapFloat.HeightAlpha.None;
      var map = FineMap(pair.Heightmap, mode)!;
      var alone = ImageMapFloat.Create(pair.Heightmap, mode, false, FineNever)!;
      var (worst, count) = FineWorst(map, pair, true, size + bits);
      var (worstAlone, _) = FineWorst(alone, pair, false, size + bits);
      bool flat = pair.F.All(f => f == 0);
      C(map.HasFine == !flat && map.Size == size && map.Compact == !flat && worst <= 2.0,
        $"{size} px, {bits} bits{(alpha ? ", Heightmap Alpha Blend" : "")}: {count} samples within 2 units of N (1/256 step; {worst:F3} at worst){(flat ? " (every byte 0: no fine heights)" : $", {map.FineBits} bits used")}");
      C(worstAlone <= 2.0 && !alone.HasFine, $"{size} px, {bits} bits: without the fine file it reads c / 65535 ({worstAlone:F3} units from the reference)");
      if (alpha)
      {
        bool same = Points(size, 9, 300).Where(p => float.IsFinite(p.x) && float.IsFinite(p.y)).All(p => Same(map.GetAlpha(p.x, p.y), alone.GetAlpha(p.x, p.y)));
        C(same && map.HasAlpha, $"{size} px, Blend: the alpha, in a grid of its own, reads as without the fine file");
      }
    }

    // Pixel centres are N exactly (one unit is 1/256 of a step), whatever the tile. 257 pixels: the positions are exact in a float.
    const int S = 257;
    var centres = FinePair(Path.Combine(Work, "fine-centres"), S, (x, y) => FineHeight(x, y, S), 8);
    var m = FineMap(centres.Heightmap)!;
    int worstAt = 0;
    double worstCentre = 0;
    for (int py = 0; py < S; py += 7)
      for (int px = 0; px < S; px += 5)
      {
        double got = m.GetValue(px / (float)(S - 1), (S - 1 - py) / (float)(S - 1));
        int at = py * S + px;
        double want = (centres.C[at] * 256.0 + centres.F[at]) / FineScale;
        double error = Math.Abs(got - want) * FineScale;
        if (error > worstCentre) { worstCentre = error; worstAt = at; }
      }
    C(worstCentre <= 2.0, $"pixel centres read their own N ({worstCentre:F3} units at worst, pixel {worstAt % S}, {worstAt / S})");
  }

  // ---- neutral cases ---------------------------------------------------------------------------------------------------------

  private static void FineNeutral()
  {
    Section("fine heights: no file, a file of zeros, a file of one value: nothing changes, or the tiles say so");
    const int S = 260;
    var dir = Path.Combine(Work, "fine-neutral");
    var pair = FinePair(dir, S, (x, y) => FineHeight(x, y, S), 8, record: FineRecord.None);
    // No fine file at all: not a line in the log, the map as it was (a compact one writes the same block).
    File.Delete(pair.Fine);
    int mark = LogMark();
    var without = FineMap(pair.Heightmap, compact: true)!;
    var never = ImageMapFloat.Create(pair.Heightmap, ImageMapFloat.HeightAlpha.None, true, FineNever)!;
    C(!LogSince(mark).Any(l => l.Contains("Fine heights")) && !without.HasFine && without.FineNote == null && without.ToBlock().SequenceEqual(never.ToBlock()) && Points(S, 3).All(p => Same(without.GetValue(p.x, p.y), never.GetValue(p.x, p.y))),
      "no fine file: nothing is logged, no note, and the map, its samples and its block are what a map made without a fine rule has");
    var decodedWithout = FineMap(pair.Heightmap)!;
    C(!decodedWithout.Compact, "and a map that is not compact stays decoded");

    // A file of zeros: no refinement.
    File.WriteAllBytes(pair.Fine, Grey8(S, (x, y) => 0));
    mark = LogMark();
    var zeros = FineMap(pair.Heightmap)!;
    var zerosCompact = FineMap(pair.Heightmap, compact: true)!;
    C(!zeros.HasFine && !zeros.Compact && zeros.FineNote == "Fine heights: every byte of heightmap-fine.png is 0, so it changes nothing: the world is the one heightmap.png makes alone."
      && LogSince(mark).Any(l => l.EndsWith(zeros.FineNote!)) && Points(S, 3).All(p => Same(zeros.GetValue(p.x, p.y), without.GetValue(p.x, p.y))),
      "every byte 0: no fine heights, the map is not made compact by it, its samples are the heightmap's, and the note says so");
    C(zerosCompact.ToBlock().SequenceEqual(without.ToBlock()) && zerosCompact.ToBlock()[0] == 1, "a compact map made with it writes the block (format 1) of a map made without it, byte for byte");

    // A file of one value: every tile uniform, 2 bytes a tile in the block.
    File.WriteAllBytes(pair.Fine, Grey8(S, (x, y) => 5));
    var five = FineMap(pair.Heightmap)!;
    var parsed = FineBlockParts(five.ToBlock());
    int tiles = TileBlock.TilesFor(S) * TileBlock.TilesFor(S);
    C(five.HasFine && five.FineBits == 8 && parsed.Version == 2 && parsed.Fine != null && parsed.Fine.Count == tiles && Enumerable.Range(0, tiles).All(parsed.Fine.IsUniform)
      && parsed.Fine.Data.Length == 12 + 2 * tiles && parsed.FineBytes == 5 && parsed.Rest == 0,
      $"a file of 5s: 8 bits used, every one of the {tiles} fine tiles uniform, {parsed.Fine?.Data.Length} bytes of tiles (12 + 2 a tile)");
    double centre = five.GetValue(0.5f, 0.5f), coarse = without.GetValue(0.5f, 0.5f);
    C(Math.Abs(centre - coarse) * FineScale > 1 && Math.Abs(centre - coarse) * FineScale < 6, $"and every height is 5 units above the heightmap's ({(centre - coarse) * FineScale:F2} at the centre)");
  }

  // A heightmap block taken apart: its version, the format of the map, the record, the values' tiles and the alpha's, the fine format
  // and bits, and the fine tiles; what is left over.
  private sealed class BlockParts
  {
    public int Version, Format, FineFormat, FineBits, FineBytes, Rest;
    public bool HasRecord;
    public float Amount, Sea;
    public TileBlock Values = null!;
    public TileBlock? Alphas, Fine;
  }

  private static BlockParts FineBlockParts(byte[] block)
  {
    using var reader = new BinaryReader(new MemoryStream(block));
    var p = new BlockParts { Version = reader.ReadByte(), Format = reader.ReadByte() };
    p.HasRecord = reader.ReadBoolean();
    if (p.HasRecord)
    {
      p.Amount = reader.ReadSingle();
      p.Sea = reader.ReadSingle();
    }
    p.Values = TileBlock.ReadFrom(reader);
    if (reader.ReadBoolean())
      p.Alphas = TileBlock.ReadFrom(reader);
    if (p.Version == 2)
    {
      p.FineFormat = reader.ReadByte();
      p.FineBits = reader.ReadByte();
      p.Fine = TileBlock.ReadFrom(reader);
      var tile = new ushort[TileBlock.Pixels];
      p.Fine.Decode(0, tile);
      p.FineBytes = tile[0];
    }
    p.Rest = (int)(block.Length - reader.BaseStream.Position);
    return p;
  }

  // ---- rejections --------------------------------------------------------------------------------------------------------------

  private static void FineRejections()
  {
    Section("fine heights: every way a fine file is refused says so as fine_ref.py's judge() does, and leaves the heightmap as it was");
    const int S = 140;
    var good = FinePair(Path.Combine(Work, "fine-reject-good"), S, (x, y) => FineHeight(x, y, S), 8);
    string hn = "heightmap.png", fn = "heightmap-fine.png";
    var fineFile = good.FineBytes;
    var idat = FineChunks(fineFile).First(c => c.Type == "IDAT");

    // A directory with the good heightmap and what make does to it; the heightmap read with the rule, against the same file read
    // without any (the heightmap as it was), and the note and the log line.
    void Case(string name, Action<string> make, string level, string message, ImageMapFloat.HeightAlpha alpha = ImageMapFloat.HeightAlpha.None, ImageMapFloat.FineRule? rule = null)
    {
      var dir = Path.Combine(Work, "fine-reject-" + name.Replace(' ', '-'));
      Directory.CreateDirectory(dir);
      File.WriteAllBytes(Path.Combine(dir, hn), good.HeightmapBytes);
      make(dir);
      var path = Path.Combine(dir, hn);
      int mark = LogMark();
      ImageMapFloat? map = null;
      Exception? thrown = null;
      try { map = FineMap(path, alpha, false, rule); }
      catch (Exception e) { thrown = e; }
      var log = LogSince(mark);
      var reference = ImageMapFloat.Create(path, alpha, false, FineNever);
      bool coarseSame = map != null && reference != null && map.Compact == reference.Compact
        && Points(S, 4, 200).Where(p => float.IsFinite(p.x) && float.IsFinite(p.y)).All(p => Same(map.GetValue(p.x, p.y), reference.GetValue(p.x, p.y)));
      string expected = "Fine heights: " + message;
      string tag = (level switch { "error" => "[Error] ", "warning" => "[Warning] ", _ => "[Log] " }) + "[BetterContinents] ";
      C(thrown == null && map != null && !map.HasFine && coarseSame && map.FineNote == expected && log.Contains(tag + expected),
        $"{name}: {level}: {expected}" + (thrown != null ? $" (threw {thrown.GetType().Name}: {thrown.Message})" : map == null ? " (no map)" : map.FineNote != expected ? $" (note was: {map.FineNote})" : !coarseSame ? " (the heightmap changed)" : ""));
    }
    void WriteFine(string dir, byte[] bytes) => File.WriteAllBytes(Path.Combine(dir, fn), bytes);
    byte[] Zero(int size, Func<int, int, byte> pixel) => Grey8(size, pixel);

    Case("another size", d => WriteFine(d, Grey8(S - 1, (x, y) => (byte)(x ^ y))), "error", $"{fn} is {S - 1} x {S - 1} pixels and {hn} is {S} x {S}; they must be the same size.");
    Case("16-bit grey", d => WriteFine(d, Grey16(S, (x, y) => (ushort)(x * y))), "error", $"{fn} is 16-bit grey; it must be 8-bit grey (no alpha, no palette).");
    Case("RGB", d => WriteFine(d, Png(S, (x, y) => new Rgb24((byte)x, (byte)y, 7), PngColorType.Rgb, PngBitDepth.Bit8)), "error", $"{fn} is 8-bit RGB; it must be 8-bit grey (no alpha, no palette).");
    Case("grey and alpha", d => WriteFine(d, Png(S, (x, y) => new La16((byte)x, (byte)y), PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8)), "error", $"{fn} is 8-bit grey and alpha; it must be 8-bit grey (no alpha, no palette).");
    Case("palette", d =>
    {
      using var image = new Image<Rgba32>(S, S);
      var colours = new[] { new Rgba32(0, 0, 0, 255), new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255) };
      for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
          image[x, y] = colours[(x / 9 + y / 7) % 4];
      using var stream = new MemoryStream();
      image.Save(stream, new PngEncoder
      {
        ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit8, ChunkFilter = PngChunkFilter.ExcludeAll,
        Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.PaletteQuantizer(colours.Select(c => (SixLabors.ImageSharp.Color)c).ToArray(), new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions { Dither = null }),
      });
      WriteFine(d, stream.ToArray());
    }, "error", $"{fn} is 8-bit palette; it must be 8-bit grey (no alpha, no palette).");
    Case("not a PNG", d => WriteFine(d, Encoding.ASCII.GetBytes("not a picture")), "error", $"{fn} cannot be read: it is not a PNG file.");
    Case("empty", d => WriteFine(d, []), "error", $"{fn} cannot be read: it is not a PNG file.");
    Case("cut inside the image data", d => WriteFine(d, fineFile[..(idat.At + 20)]), "error", $"{fn} cannot be read: it is cut short (inside its IDAT chunk).");
    Case("cut before the end", d => WriteFine(d, fineFile[..^5]), "error", $"{fn} cannot be read: it is cut short (no IEND).");
    Case("damaged", d =>
    {
      var damaged = (byte[])fineFile.Clone();
      damaged[idat.At + 8 + idat.Length / 2] ^= 0x10;
      WriteFine(d, damaged);
    }, "error", $"{fn} cannot be read: its IDAT chunk at byte {idat.At} is damaged (the CRC does not match).");
    Case("larger than a map", d => WriteFine(d, WithHeader(fineFile, 16385, 16385)), "error", $"{fn} is 16385 x 16385 pixels, and the largest map Better Continents reads is 16384 x 16384.");
    Case("no IHDR first", d => WriteFine(d, [.. fineFile[..8], .. FineChunk("tEXt", TextChunkBody("A", "b")), .. fineFile[8..]]), "error", $"{fn} cannot be read: it does not start with IHDR.");
    Case("invalid IHDR", d => WriteFine(d, WithHeader(fineFile, colour: 5)), "error", $"{fn} cannot be read: its IHDR is not valid.");
    Case("no image data", d => WriteFine(d, [.. fineFile[..33], .. fineFile[^12..]]), "error", $"{fn} cannot be read: it holds no image data.");
    Case("fine format 2", d => WriteFine(d, FineRecordBytes(Zero(S, (x, y) => (byte)good.F[y * S + x]), FineRecord.Format2, 8, good.Crc)), "warning", $"{fn} is in fine format 2, which this Better Continents cannot read.");
    Case("another heightmap", d => WriteFine(d, FineRecordBytes(Zero(S, (x, y) => (byte)good.F[y * S + x]), FineRecord.WrongCrc, 8, good.Crc)), "warning",
      $"{fn} was made for another {hn} (its record names CRC-32 {good.Crc ^ 1:X8}; this one is {good.Crc:X8}). If {hn} was edited, make {fn} again or delete it.");
    var edited = Grey16(S, (x, y) => (ushort)(good.C[y * S + x] ^ (x == 3 && y == 4 ? 1 : 0)));
    uint editedCrc = Crc32.Compute(edited, 0, edited.Length);
    Case("an edited heightmap", d =>
    {
      WriteFine(d, fineFile);
      File.WriteAllBytes(Path.Combine(d, hn), edited);
    }, "warning", $"{fn} was made for another {hn} (its record names CRC-32 {good.Crc:X8}; this one is {editedCrc:X8}). If {hn} was edited, make {fn} again or delete it.");
    Case("an 8-bit heightmap", d =>
    {
      WriteFine(d, fineFile);
      File.WriteAllBytes(Path.Combine(d, hn), Grey8(S, (x, y) => (byte)(good.C[y * S + x] >> 8)));
    }, "warning", $"{fn} is not read: {hn} is 8-bit grey; fine heights refine a 16-bit grey heightmap (grey, or grey and alpha).");
    Case("an RGB heightmap", d =>
    {
      WriteFine(d, fineFile);
      File.WriteAllBytes(Path.Combine(d, hn), Png(S, (x, y) => new Rgb24((byte)(good.C[y * S + x] >> 8), 0, 0), PngColorType.Rgb, PngBitDepth.Bit8));
    }, "warning", $"{fn} is not read: {hn} is 8-bit RGB; fine heights refine a 16-bit grey heightmap (grey, or grey and alpha).");
    Case("a heightmap that is not a PNG", d =>
    {
      WriteFine(d, fineFile);
      using var image = new Image<L16>(S, S);
      using var stream = new MemoryStream();
      image.SaveAsBmp(stream);
      File.WriteAllBytes(Path.Combine(d, hn), stream.ToArray());
    }, "error", $"{fn} is not read: {hn} cannot be read: it is not a PNG file.");
    Case("Heightmap Alpha as it was", d => WriteFine(d, fineFile), "warning", $"{fn} is not read: Heightmap Alpha as it was before (Legacy) reads {hn} as 8-bit grey.", ImageMapFloat.HeightAlpha.Legacy);
    Case("settings version 11", d => WriteFine(d, fineFile), "warning", $"{fn} is not read: the world is saved in settings version 11, which has no fine heights.",
      rule: new(ImageMapFloat.FineUse.OldVersion, 11, FineAmount));
    Case("Compact Maps Off", d => WriteFine(d, fineFile), "warning", $"{fn} is not read: Compact Maps is Off, and a world with fine heights keeps its maps as compressed tiles (Auto or On reads it).",
      rule: new(ImageMapFloat.FineUse.CompactOff, 12, FineAmount));

    // Used: the record present and right, absent, only a lower-case CRC, only the bits, before the data, in every kind of text chunk.
    foreach (var (name, record) in new[] { ("a record that matches (after the data)", FineRecord.After), ("a record before the data", FineRecord.Before), ("no record", FineRecord.None),
      ("a lower-case CRC and nothing else", FineRecord.CrcOnly), ("Fine Bits and nothing else", FineRecord.BitsOnly), ("a zTXt record after the data", FineRecord.ZAfter), ("an iTXt record before the data", FineRecord.ITextBefore) })
    {
      var pair = FinePair(Path.Combine(Work, "fine-used-" + record), S, (x, y) => FineHeight(x, y, S), 8, record: record);
      var map = FineMap(pair.Heightmap)!;
      var (worst, _) = FineWorst(map, pair, true, 21);
      C(map.HasFine && map.FineNote == null && worst <= 2.0, $"used: {name} ({worst:F3} units from the reference)");
    }

    // An interlaced fine file, which PngRows declines and ImageSharp reads whole.
    var plainPair = FinePair(Path.Combine(Work, "fine-interlaced"), S, (x, y) => FineHeight(x, y, S), 8, record: FineRecord.None);
    using (var image = new Image<L8>(S, S))
    {
      for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
          image[x, y] = new L8((byte)plainPair.F[y * S + x]);
      using var stream = new MemoryStream();
      image.Save(stream, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8, InterlaceMethod = PngInterlaceMode.Adam7, ChunkFilter = PngChunkFilter.ExcludeAll });
      File.WriteAllBytes(plainPair.Fine, stream.ToArray());
    }
    var interlaced = FineMap(plainPair.Heightmap)!;
    var (worstInterlaced, _) = FineWorst(interlaced, plainPair, true, 22);
    C(interlaced.HasFine && worstInterlaced <= 2.0 && PngRows<L8>.TryOpen(File.ReadAllBytes(plainPair.Fine)) == null,
      $"an interlaced fine file is read (by ImageSharp, whole): {worstInterlaced:F3} units from the reference");

    // A heightmap that cannot be read at all: the fine file says so, and no map is made.
    var broken = Path.Combine(Work, "fine-broken-coarse");
    Directory.CreateDirectory(broken);
    File.WriteAllBytes(Path.Combine(broken, hn), good.HeightmapBytes[..(good.HeightmapBytes.Length / 2)]);
    File.WriteAllBytes(Path.Combine(broken, fn), fineFile);
    int markBroken = LogMark();
    var none = FineMap(Path.Combine(broken, hn));
    C(none == null && LogSince(markBroken).Any(l => l.Contains($"Fine heights: {fn} is not read: {hn} cannot be read: it is cut short")),
      "a heightmap that is cut short makes no map, and the fine file says why it was not read");

    // A fine file without a heightmap: nothing to refine, nothing said.
    var alone = Path.Combine(Work, "fine-without-heightmap");
    Directory.CreateDirectory(alone);
    File.WriteAllBytes(Path.Combine(alone, fn), fineFile);
    int markAlone = LogMark();
    var noMap = FineMap(Path.Combine(alone, hn));
    C(noMap == null && !LogSince(markAlone).Any(l => l.Contains("Fine heights")), "a fine file with no heightmap: no map, and no word about fine heights");
  }

  // ---- where the file is looked for ----------------------------------------------------------------------------------------------

  private static BC.BetterContinentsSettings FineSettings(bool alpha = false) =>
    new() { EnabledForThisWorld = true, Version = 12, HeightmapAmount = FineAmount, HeightMapAlpha = alpha };

  private static ImageMapFloat? HeightOf(BC.BetterContinentsSettings s) => (ImageMapFloat?)Field("HeightMap").GetValue(s);

  private static void FineLookup()
  {
    Section("fine heights: the fine file is beside the heightmap's file, named by it");
    const int S = 70;
    var root = Path.Combine(Work, "fine-lookup");
    foreach (var (folder, name, fine) in new[]
      { ("a.b", "c.v2.png", "c.v2-fine.png"), ("maps v2", "iceland", "iceland-fine.png"), ("upper", "MAP.PNG", "MAP-fine.PNG"), ("dot", "file.", "file-fine.png"), ("plain", "heightmap.png", "heightmap-fine.png") })
    {
      var pair = FinePair(Path.Combine(root, folder), S, (x, y) => FineHeight(x, y, S), 8, heightmapName: name, fineName: fine);
      var map = FineMap(pair.Heightmap)!;
      var (worst, _) = FineWorst(map, pair, true, 31);
      C(map.HasFine && map.FineName == fine && worst <= 2.0, $"'{Path.Combine(folder, name)}' reads '{fine}'");
    }
    // Another name beside a heightmap.png-named fine file: not its fine file.
    var stray = FinePair(Path.Combine(root, "stray"), S, (x, y) => FineHeight(x, y, S), 8, heightmapName: "other.png", fineName: "heightmap-fine.png");
    int mark = LogMark();
    var strayMap = FineMap(stray.Heightmap)!;
    C(!strayMap.HasFine && strayMap.FineNote == null && !LogSince(mark).Any(l => l.Contains("Fine heights")), "a heightmap-fine.png beside 'other.png' is not its fine file");

    // A map made from a world's settings has no file path, or a path it was made from long ago: it reads none.
    var pair2 = FinePair(Path.Combine(root, "stored"), S, (x, y) => FineHeight(x, y, S), 8);
    int storedMark = LogMark();
    var stored = ImageMapFloat.Create(File.ReadAllBytes(pair2.Heightmap), pair2.Heightmap)!;
    var noPath = ImageMapFloat.Create(File.ReadAllBytes(pair2.Heightmap), ImageMapFloat.HeightAlpha.None);
    C(ImageMapFloat.Create("", ImageMapFloat.HeightAlpha.None, false, FineRead()) == null && !stored.HasFine && noPath != null && !noPath.HasFine && !LogSince(storedMark).Any(l => l.Contains("Fine heights")),
      "no path, or a picture a world stored: no look at the disk, whatever lies beside its old path");

    // A new world, bc h fn and bc reload: the heightmap kind, as the settings use it.
    Section("fine heights: bc h fn, bc reload and a decode again (bc h alpha)");
    var work = FinePair(Path.Combine(root, "reload"), S, (x, y) => FineHeight(x, y, S), 8);
    var s = FineSettings();
    mark = LogMark();
    bool set = BC.BetterContinentsSettings.MapKind.Height.Set(s, work.Heightmap);
    var map1 = HeightOf(s)!;
    var coarseOnly = ImageMapFloat.Create(work.Heightmap, ImageMapFloat.HeightAlpha.None, false, FineNever)!;
    C(set && map1.HasFine && s.CompactMaps && s.FineBits == 8 && LogSince(mark).Any(l => l.Contains("Compact Maps is on for this world: its heightmap has fine heights (heightmap-fine.png)")),
      "bc h fn: the heightmap has its fine heights, and the world is compact because of it (the log says so)");
    // A new file beside it, every byte 3 more (up to 127): bc reload reads it.
    double was = map1.GetValue(0.5f, 0.5f);
    var shiftedF = work.F.Select(f => (sbyte)Math.Min(f + 3, 127)).ToArray();
    var shifted = Grey8(S, (x, y) => (byte)shiftedF[y * S + x]);
    File.WriteAllBytes(work.Fine, FineRecordBytes(shifted, FineRecord.After, 8, work.Crc));
    BC.BetterContinentsSettings.MapKind.Height.Reload(s);
    var map2 = HeightOf(s)!;
    double centre = map2.GetValue(0.5f, 0.5f), shiftedWant = FineReference(work.C, shiftedF, S, 0.5f, 0.5f);
    C(map2 == map1 && map2.HasFine && Math.Abs(centre - shiftedWant) * FineScale <= 2.0 && Math.Abs(centre - was) * FineScale > 0.5 && s.CompactMaps,
      $"bc reload: the new fine file is read ({(centre - was) * FineScale:F2} units above what it was, the reference says {(shiftedWant - was) * FineScale:F2})");
    // Gone: the heightmap is read without it, and the log says what that does.
    File.Delete(work.Fine);
    mark = LogMark();
    BC.BetterContinentsSettings.MapKind.Height.Reload(s);
    C(!map2.HasFine && Same(map2.GetValue(0.3f, 0.6f), coarseOnly.GetValue(0.3f, 0.6f)) && LogSince(mark).Any(l => l.Contains("is now read without fine heights") && l.Contains("heightmap-fine.png is not there")),
      "bc reload with the fine file gone: the heightmap is read without it, and the log says so");
    // Back, the way it was made.
    File.WriteAllBytes(work.Fine, work.FineBytes);
    BC.BetterContinentsSettings.MapKind.Height.Reload(s);
    C(HeightOf(s)!.HasFine && Math.Abs(HeightOf(s)!.GetValue(0.5f, 0.5f) - was) * FineScale <= 2.0, "and the fine heights are back when the file is");

    // A world read from its tiles holds no picture: decoded again, it reads its file and the fine file beside it. One that holds its
    // picture (a world made without Compact Maps, or an older one) decodes that: no file is read.
    var loaded = Read(Save(s, false, 12));
    var loadedMap = HeightOf(loaded)!;
    C(loadedMap.HasFine && !loadedMap.SourceIsFromFile && loadedMap.SourceData.Length == 0 && loaded.CompactMaps, "a world read from its tiles holds fine heights and no picture");
    File.WriteAllBytes(work.Fine, FineRecordBytes(shifted, FineRecord.After, 8, work.Crc));
    BC.BetterContinentsSettings.MapKind.Height.Redecode(loaded);
    double reread = HeightOf(loaded)!.GetValue(0.5f, 0.5f);
    C(HeightOf(loaded)!.HasFine && HeightOf(loaded)!.SourceIsFromFile && Math.Abs(reread - shiftedWant) * FineScale <= 2.0 && Math.Abs(reread - was) * FineScale > 0.5,
      $"decoded again, it reads its file and the fine file beside it ({(reread - was) * FineScale:F2} units above what the tiles held)");
    var storedWorld = FineSettings();
    Field("HeightMap").SetValue(storedWorld, ImageMapFloat.Create(File.ReadAllBytes(work.Heightmap), work.Heightmap));
    mark = LogMark();
    BC.BetterContinentsSettings.MapKind.Height.Redecode(storedWorld);
    C(!HeightOf(storedWorld)!.HasFine && !LogSince(mark).Any(l => l.Contains("Fine heights")), "a world that holds its picture decodes that again, and reads no file: a fine file beside the path changes nothing");

    // Compact Maps Off: not read, and the world is not made compact by it.
    var off = FineSettings();
    off.SetCompactMaps(CompactMapsMode.Off);
    mark = LogMark();
    BC.BetterContinentsSettings.MapKind.Height.Set(off, work.Heightmap);
    C(!HeightOf(off)!.HasFine && !off.CompactMaps && HeightOf(off)!.FineNote!.Contains("Compact Maps is Off") && LogSince(mark).Any(l => l.Contains("Fine heights: heightmap-fine.png is not read: Compact Maps is Off")),
      "Compact Maps Off: the heightmap is made without them, and the world stays as it is");
    var old = FineSettings();
    old.Version = 11;
    BC.BetterContinentsSettings.MapKind.Height.Set(old, work.Heightmap);
    C(!HeightOf(old)!.HasFine && HeightOf(old)!.FineNote!.Contains("settings version 11"), "a world of settings version 11: not read");
    var legacy = FineSettings();
    legacy.Version = 11;
    legacy.HeightMapAlpha = true;
    BC.BetterContinentsSettings.MapKind.Height.Set(legacy, work.Heightmap);
    C(!HeightOf(legacy)!.HasFine && legacy.HeightmapAlphaMode == ImageMapFloat.HeightAlpha.Legacy, "and one that reads the heightmap as 8-bit grey (Heightmap Alpha as it was): not read");
    var rule = FineSettings().FineHeightsRule;
    var ruleLegacy = legacy.FineHeightsRule;
    C(rule.Use == ImageMapFloat.FineUse.Read && rule.Amount == FineAmount && ruleLegacy.Use == ImageMapFloat.FineUse.OldVersion && off.FineHeightsRule.Use == ImageMapFloat.FineUse.CompactOff,
      "the settings' rule: read, or why not (version, then Heightmap Alpha as it was, then Compact Maps Off)");
  }

  // ---- the block ---------------------------------------------------------------------------------------------------------------

  private static TileBlock FineTiles(int size, int channels, int bytes, Func<int, int, int> value) =>
    TileBlock.Encode(size, channels, bytes, true, (y0, rows, band) =>
    {
      for (int c = 0; c < channels; c++)
        for (int r = 0; r < rows; r++)
          for (int x = 0; x < size; x++)
            band[(c * TileBlock.Side + r) * size + x] = (ushort)(value(x, y0 + r) & (bytes == 2 ? 0xFFFF : 0xFF));
    });

  // A heightmap block as 0.10.2 wrote it (format 1), and with fine heights in format 2, from the pieces.
  private static byte[] AssembleBlock(byte version, byte format, TileBlock values, TileBlock? alphas, byte? fineFormat = null, byte fineBits = 8, TileBlock? fine = null, (float, float)? record = null)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(version);
    writer.Write(format);
    writer.Write(record != null);
    if (record is { } r)
    {
      writer.Write(r.Item1);
      writer.Write(r.Item2);
    }
    values.WriteTo(writer);
    writer.Write(alphas != null);
    alphas?.WriteTo(writer);
    if (fineFormat != null)
    {
      writer.Write(fineFormat.Value);
      writer.Write(fineBits);
      fine?.WriteTo(writer);
    }
    writer.Flush();
    return stream.ToArray();
  }

  private static bool Refused(Func<ImageMapFloat> read, out string message)
  {
    message = "";
    try
    {
      read();
      return false;
    }
    catch (InvalidDataException e)
    {
      message = e.Message;
      return true;
    }
  }

  // The version check of 0.10.2's ImageMapFloat.FromBlock (and of every build that reads format 1 only), copied from it.
  private static void OldFromBlock(byte[] block)
  {
    using var reader = new BinaryReader(new MemoryStream(block));
    var version = reader.ReadByte();
    if (version != 1)
      throw new InvalidDataException($"a map saved in format {version}, which this version of Better Continents cannot read");
  }

  private static void FineBlocks()
  {
    Section("fine heights: the block in format 2, every other block in format 1 as it was, and what older builds make of it");
    const int S = 200;
    var pair = FinePair(Path.Combine(Work, "fine-blocks"), S, (x, y) => FineHeight(x, y, S), 8);
    var map = FineMap(pair.Heightmap)!;
    var block = map.ToBlock();
    var parts = FineBlockParts(block);
    C(parts.Version == 2 && parts.Format == (int)ImageMapFloat.ValueFormat.Grey16 && !parts.HasRecord && parts.Alphas == null && parts.FineFormat == 1 && parts.FineBits == map.FineBits
      && parts.Fine!.Size == S && parts.Fine.Channels == 1 && parts.Fine.Bytes == 1 && parts.Values.Size == S && parts.Values.Bytes == 2 && parts.Rest == 0,
      $"format 2: the values' tiles, then the fine format (1), the bits ({parts.FineBits}) and the fine tiles; nothing more ({block.Length:N0} bytes)");
    var back = ImageMapFloat.FromBlock(block);
    C(back.HasFine && back.FineBits == map.FineBits && back.Compact && back.Size == S && Points(S, 8, 500).All(p => Same(back.GetValue(p.x, p.y), map.GetValue(p.x, p.y))) && back.ToBlock().SequenceEqual(block),
      "read back: the same fine heights, every sample the same bit for bit, and the same bytes written again");

    // A heightmap with Heightmap Alpha Blend: the alpha keeps its own tiles, and the record its place.
    var withAlpha = FinePair(Path.Combine(Work, "fine-blocks-alpha"), S, (x, y) => FineHeight(x, y, S), 4, alpha: true);
    var mapAlpha = FineMap(withAlpha.Heightmap, ImageMapFloat.HeightAlpha.Blend)!;
    var alphaParts = FineBlockParts(mapAlpha.ToBlock());
    var alphaBack = ImageMapFloat.FromBlock(mapAlpha.ToBlock());
    C(alphaParts.Version == 2 && alphaParts.Format == (int)ImageMapFloat.ValueFormat.GreyAlpha16 && alphaParts.Alphas != null && alphaParts.FineBits == mapAlpha.FineBits && alphaParts.Rest == 0
      && alphaBack.HasFine && alphaBack.HasAlpha && Points(S, 8, 300).All(p => Same(alphaBack.GetValue(p.x, p.y), mapAlpha.GetValue(p.x, p.y)) && Same(alphaBack.GetAlpha(p.x, p.y), mapAlpha.GetAlpha(p.x, p.y))),
      "with Heightmap Alpha Blend: the alpha's tiles are between the values' and the fine ones, and all of it reads back");
    var recorded = Path.Combine(Work, "fine-blocks-record");
    Directory.CreateDirectory(recorded);
    var recordedPair = FinePair(recorded, 64, (x, y) => FineHeight(x, y, 64), 8);
    WorldExportPng.SaveHeightmap(recordedPair.Heightmap, Enumerable.Range(0, 64 * 64).Select(i => new L16(recordedPair.C[i])).ToArray(), 64, new HeightmapRecord(1.25f, 0.4f));
    var recordedBytes = File.ReadAllBytes(recordedPair.Heightmap);
    File.WriteAllBytes(recordedPair.Fine, FineRecordBytes(Grey8(64, (x, y) => (byte)recordedPair.F[y * 64 + x]), FineRecord.After, 8, Crc32.Compute(recordedBytes, 0, recordedBytes.Length)));
    var recordedMap = FineMap(recordedPair.Heightmap)!;
    var recordedParts = FineBlockParts(recordedMap.ToBlock());
    C(recordedMap.HasFine && recordedMap.Record is { Amount: 1.25f, SeaLevel: 0.4f } && recordedParts.HasRecord && recordedParts.Amount == 1.25f && recordedParts.Sea == 0.4f && ImageMapFloat.FromBlock(recordedMap.ToBlock()).Record is { Amount: 1.25f },
      "a heightmap's own record (Heightmap Amount, Sea Level Adjustment) stays in the block, ahead of the tiles");

    // Every map without fine heights: format 1, the layout 0.10.2 wrote.
    var plain = ImageMapFloat.Create(pair.Heightmap, ImageMapFloat.HeightAlpha.None, true, FineNever)!;
    var plainBlock = plain.ToBlock();
    var plainParts = FineBlockParts(plainBlock);
    C(plainParts.Version == 1 && plainParts.Rest == 0 && AssembleBlock(1, (byte)plainParts.Format, plainParts.Values, plainParts.Alphas).SequenceEqual(plainBlock),
      "a map without fine heights writes format 1: the version, the format, whether there is a record, the values' tiles, whether there is an alpha: no more");
    var forest = ImageMapFloat.Create(pair.Heightmap, false, true)!;
    C(forest.ToBlock().SequenceEqual(plainBlock) && !ImageMapFloat.FromBlock(plainBlock).HasFine, "and any other float map (forest, rough, heat...) the same");

    // Older builds refuse format 2: the version check says so, as it said for any other format.
    bool oldRefuses = Refused(() => { OldFromBlock(block); return null!; }, out var oldMessage);
    C(oldRefuses && oldMessage == "a map saved in format 2, which this version of Better Continents cannot read" && !Refused(() => { OldFromBlock(plainBlock); return null!; }, out _),
      $"0.10.2's reader refuses the fine block ('{oldMessage}') and reads the plain one");
    // As a world's settings: Load throws, and the loader of a world's file (LoadFromSource) answers Disabled and writes nothing.
    var world = FineSettings();
    BC.BetterContinentsSettings.MapKind.Height.Set(world, pair.Heightmap);
    var saved = Save(world, false, 12);
    var file = Path.Combine(Work, "fine-world-settings");
    var fileBytes = BitConverter.GetBytes(saved.Length).Concat(saved).ToArray();
    File.WriteAllBytes(file, fileBytes);
    var before = File.ReadAllBytes(file);
    bool loaded = Read(saved).HasHeightMap;
    C(loaded && File.ReadAllBytes(file).SequenceEqual(before), "this build loads it, and reading a world's settings file changes no byte of it");

    // Damage. A block of tiles that is not a map's, in each way.
    Section("fine heights: a damaged block is refused, never read as a map");
    var values = FineTiles(S, 1, 2, (x, y) => x * 31 + y * 7);
    var fine = FineTiles(S, 1, 1, (x, y) => (x ^ y) & 0xFF);
    var control = AssembleBlock(2, 0, values, null, 1, 8, fine);
    C(ImageMapFloat.FromBlock(control).HasFine, "the control: a block put together here reads");
    (string Name, byte[] Block)[] bad =
    [
      ("fine tiles with a byte changed (their checksum)", [.. control[..^20], (byte)(control[^20] ^ 0x20), .. control[^19..]]),
      ("fine tiles of another size", AssembleBlock(2, 0, values, null, 1, 8, FineTiles(S + 1, 1, 1, (x, y) => x))),
      ("fine tiles of two channels", AssembleBlock(2, 0, values, null, 1, 8, FineTiles(S, 2, 1, (x, y) => x))),
      ("fine tiles of two bytes", AssembleBlock(2, 0, values, null, 1, 8, FineTiles(S, 1, 2, (x, y) => x))),
      ("fine bits 0", AssembleBlock(2, 0, values, null, 1, 0, fine)),
      ("fine bits 9", AssembleBlock(2, 0, values, null, 1, 9, fine)),
      ("fine format 2", AssembleBlock(2, 0, values, null, 2, 8, fine)),
      ("fine format 0", AssembleBlock(2, 0, values, null, 0, 8, fine)),
      ("a map of 8-bit grey and alpha holding fine tiles", AssembleBlock(2, (byte)ImageMapFloat.ValueFormat.GreyAlpha8, FineTiles(S, 1, 1, (x, y) => x), null, 1, 8, fine)),
      ("a map of the oldest kind (RGBA, red) holding fine tiles", AssembleBlock(2, (byte)ImageMapFloat.ValueFormat.Rgba8, FineTiles(S, 1, 1, (x, y) => x), null, 1, 8, fine)),
      ("format 3", AssembleBlock(3, 0, values, null, 1, 8, fine)),
      ("format 0", AssembleBlock(0, 0, values, null, null)),
      ("format 2 cut short in the fine tiles", control[..^30]),
      ("format 1 holding fine tiles after its end (read as format 1: trailing bytes are not looked for, as before)", AssembleBlock(1, 0, values, null, 1, 8, fine)),
    ];
    foreach (var (name, data) in bad)
    {
      bool refused = Refused(() => ImageMapFloat.FromBlock(data), out var message);
      if (name.StartsWith("format 1 holding"))
        C(!refused && !ImageMapFloat.FromBlock(data).HasFine, name);
      else
        C(refused, $"{name}: refused ({message})");
    }
  }

  // ---- a world with fine heights: saved, sent, shared, read ------------------------------------------------------------------------

  // The heightmaps of two worlds at many points, bit for bit (and the alpha).
  private static string? FineDiffers(BC.BetterContinentsSettings a, BC.BetterContinentsSettings b, int size)
  {
    var ma = HeightOf(a)!;
    var mb = HeightOf(b)!;
    if (ma.Size != mb.Size || ma.HasFine != mb.HasFine || ma.FineBits != mb.FineBits || ma.HasAlpha != mb.HasAlpha)
      return "state";
    foreach (var (x, y) in Points(size, 77, 800))
      if (!Same(ma.GetValue(x, y), mb.GetValue(x, y)) || !Same(ma.GetAlpha(x, y), mb.GetAlpha(x, y)))
        return $"({x}, {y})";
    return null;
  }

  private static void FineWorlds()
  {
    Section("fine heights: a world saved, sent to a client, saved in an older settings version, as a preset, and shared as a .bcworld");
    const int S = 260;
    foreach (bool alpha in new[] { false, true })
    {
      var pair = FinePair(Path.Combine(Work, $"fine-world-{alpha}"), S, (x, y) => FineHeight(x, y, S), 4, alpha);
      var world = FineSettings(alpha);
      BC.BetterContinentsSettings.MapKind.Height.Set(world, pair.Heightmap);
      var map = HeightOf(world)!;
      var disk = Save(world, false, 12);
      var back = Read(disk);
      var tag = alpha ? "with Heightmap Alpha Blend, " : "";
      C(world.CompactMaps && back.CompactMaps && HeightOf(back)!.HasFine && HeightOf(back)!.Compact && FineDiffers(world, back, S) == null,
        $"{tag}saved and read back: compact, with its fine heights, every sample bit for bit ({disk.Length:N0} bytes)");
      C(Save(back, false, 12).SequenceEqual(disk), $"{tag}and saved again it is the same bytes (its tiles are kept, not encoded again)");
      var net = Save(world, true, 12);
      var client = Read(net);
      C(FineDiffers(world, client, S) == null && net.Length <= disk.Length && client.CompactMaps, $"{tag}sent to a client (no paths), it reads the same ({net.Length:N0} bytes)");
      // The same world made without the fine file: the same settings in every way but the heightmap's block.
      var plainPair = FinePair(Path.Combine(Work, $"fine-world-plain-{alpha}"), S, (x, y) => FineHeight(x, y, S), 4, alpha, FineRecord.None);
      File.Delete(plainPair.Fine);
      var plain = FineSettings(alpha);
      BC.BetterContinentsSettings.MapKind.Height.Set(plain, plainPair.Heightmap);
      C(!HeightOf(plain)!.HasFine && !plain.CompactMaps, $"{tag}the world of the same pair without the fine file is not compact");

      // An older settings version has no room for them: the pictures are written, the fine heights are lost, and the log says so.
      int mark = LogMark();
      var older = Save(world, false, 11);
      var olderBack = Read(older);
      var plainSamples = ImageMapFloat.Create(pair.Heightmap, alpha ? ImageMapFloat.HeightAlpha.Blend : ImageMapFloat.HeightAlpha.None, false, FineNever)!;
      C(LogSince(mark).Any(l => l.Contains("Fine heights: the world is saved in settings version 11 (Override version), which has no room for them")) && !HeightOf(olderBack)!.HasFine
        && (alpha || Points(S, 5, 300).Where(p => float.IsFinite(p.x) && float.IsFinite(p.y)).All(p => Math.Abs(HeightOf(olderBack)!.GetValue(p.x, p.y) - plainSamples.GetValue(p.x, p.y)) * FineScale < 0.5)),
        $"{tag}saved in version 11 (Override version): the heightmap is written as a picture without its fine heights, and the log says so");
      mark = LogMark();
      Save(world, false, 11);
      C(!LogSince(mark).Any(l => l.Contains("Fine heights")), $"{tag}and says it once, not at every save");

      // A preset ("bc savepreset", bc_import) holds the block.
      var presetPath = Path.Combine(Work, $"fine-preset-{alpha}.BetterContinents");
      world.Save(presetPath, currentFormat: true);
      var preset = BC.BetterContinentsSettings.Load(presetPath);
      C(HeightOf(preset)!.HasFine && FineDiffers(world, preset, S) == null && preset.CompactMaps, $"{tag}as a preset file: loaded, it has them");

      // A .bcworld: the bytes a player is sent, shared as a file.
      var shared = Path.Combine(Work, $"fine-share-{alpha}");
      var export = WorldCacheShare.WriteExportFiles(net, "Fine World", "seed", shared);
      WorldCache.WorldCachePath = Path.Combine(Work, $"fine-cache-{alpha}");
      var outcomes = WorldCacheShare.ImportPath(export.FilePath, out var error);
      var stored = File.ReadAllBytes(WorldCache.GetCachePath(export.Id));
      C(error == null && outcomes.Count == 1 && outcomes[0].Imported && stored.SequenceEqual(net) && WorldCacheShare.LooksLikeSettingsPackageHeader(stored[..4]) && FineDiffers(world, Read(stored), S) == null,
        $"{tag}shared as a .bcworld and imported: the same bytes, and the world they make has the same fine heights");
    }
  }

  // ---- the tile cache with two grids ----------------------------------------------------------------------------------------------

  private static void FineCache()
  {
    Section("fine heights: the cache holds the fine tiles beside the values'; a fine tile that will not decode reads as 0, once logged");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int S = 1024;
    var pair = FinePair(Path.Combine(Work, "fine-cache"), S, (x, y) => FineHeight(x, y, S), 8);
    var map = FineMap(pair.Heightmap)!;
    long tile = 2L * TileBlock.Pixels + TileBlock.Pixels;
    TileCache.Override = 4 * tile;
    map.GetValue(0.5f, 0.5f);
    long peak = TileCache.Resident;
    bool done = false;
    var watcher = new Thread(() =>
    {
      while (!Volatile.Read(ref done))
      {
        long r = TileCache.Resident;
        if (r > peak) peak = r;
        Thread.Sleep(0);
      }
    });
    watcher.Start();
    int wrong = 0;
    const int Threads = 8, Each = 20000;
    Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, k =>
    {
      var random = new System.Random(k + 100);
      for (int i = 0; i < Each; i++)
      {
        float x = (float)random.NextDouble(), y = (float)random.NextDouble();
        if (Math.Abs(map.GetValue(x, y) - FineReference(pair.C, pair.F, S, x, y)) * FineScale > 2.0)
          Interlocked.Increment(ref wrong);
      }
    });
    Volatile.Write(ref done, true);
    watcher.Join();
    C(wrong == 0, $"{Threads * Each:N0} reads from {Threads} threads over 64 tiles of two grids, every one right");
    C(peak <= TileCache.Override * 5 / 4 + 2 * Threads * tile, $"the decoded tiles of both grids stayed near the budget (peak {peak / 1024:N0} KB, budget {TileCache.Override / 1024:N0} KB)");
    C(map.DecodedTiles <= 8 + 4 * Threads, $"tiles of both were dropped as others were read ({map.DecodedTiles} decoded now)");
    map.ReleasePixels();
    C(map.DecodedTiles == 0 && Math.Abs(map.GetValue(0.25f, 0.75f) - FineReference(pair.C, pair.F, S, 0.25f, 0.75f)) * FineScale < 2.0, "dropping them all (the lean import) leaves the map reading the same; it counts the fine tiles too");
    TileCache.Override = null;
    map.GetValue(0.3f, 0.3f);
    // One read decodes a tile of each grid: the cache counts both.
    C(map.DecodedTiles >= 2, $"a read decodes a tile of the values and one of the fine heights ({map.DecodedTiles} decoded)");

    // A fine tile that cannot be inflated: sealed with a right checksum, as a file damaged when it was written would be.
    var good = FineBlockParts(map.ToBlock());
    var data = (byte[])good.Fine!.Data.Clone();
    int at = 8, damagedTile = -1;
    for (int t = 0; t < good.Fine.Count && damagedTile < 0; t++)
    {
      byte mode = data[at];
      if (mode == TileBlock.Uniform)
      {
        at += 2;
        continue;
      }
      int length = BitConverter.ToInt32(data, at + 1);
      for (int i = 0; i < length; i++)
        data[at + 5 + i] = 0xFF;
      damagedTile = t;
    }
    var resealed = Crc32.Compute(data, 0, data.Length - 4);
    data[^4] = (byte)resealed; data[^3] = (byte)(resealed >> 8); data[^2] = (byte)(resealed >> 16); data[^1] = (byte)(resealed >> 24);
    var damagedBlock = AssembleBlock(2, 0, good.Values, null, 1, 8, TileBlock.Read(data));
    var damaged = ImageMapFloat.FromBlock(damagedBlock);
    int tx = damagedTile % good.Fine.Tiles, ty = damagedTile / good.Fine.Tiles;
    int mark = LogMark();
    double worstAlone = 0;
    for (int py = ty * 128 + 3; py < ty * 128 + 120; py += 9)
      for (int px = tx * 128 + 3; px < Math.Min(S - 1, tx * 128 + 120); px += 11)
      {
        int row = S - 1 - py;
        double want = pair.C[row * S + px] / 65535.0;
        double got = damaged.GetValue(px / (float)(S - 1), py / (float)(S - 1));
        worstAlone = Math.Max(worstAlone, Math.Abs(got - want) * FineScale);
      }
    var errors = LogSince(mark).Where(l => l.Contains("A map tile could not be decoded")).ToList();
    C(damagedTile >= 0 && worstAlone < 0.5 && errors.Count == 1, $"a fine tile that cannot be inflated (tile {damagedTile}) reads as 0 - the heightmap's own height - and one error is logged ({errors.Count}; {worstAlone:F3} units from the value alone)");
    // A sample reads the four pixels around it: those that touch the damaged tile are left out.
    bool Touches(float x, float y)
    {
      int x0 = (int)(x * (S - 1)), y0 = (int)(y * (S - 1));
      return (x0 >> 7 == tx || x0 + 1 >> 7 == tx) && (y0 >> 7 == ty || y0 + 1 >> 7 == ty);
    }
    var elsewhere = Points(S, 6, 600).Where(p => float.IsFinite(p.x) && float.IsFinite(p.y) && p.x >= 0 && p.x < 1 && p.y >= 0 && p.y < 1 && !Touches(p.x, p.y)).ToList();
    C(elsewhere.Count > 100 && elsewhere.All(p => Math.Abs(damaged.GetValue(p.x, p.y) - FineReference(pair.C, pair.F, S, p.x, p.y)) * FineScale <= 2.0), $"and every other tile reads as it did ({elsewhere.Count} samples)");
  }

  // ---- a world export's sources/ ----------------------------------------------------------------------------------------------------

  private static void FineSources()
  {
    Section("fine heights: a map written back out (a world export's sources/) as a pair that reads the same");
    const int S = 200;
    var pair = FinePair(Path.Combine(Work, "fine-sources-from"), S, (x, y) => FineHeight(x, y, S), 4);
    var map = FineMap(pair.Heightmap)!;
    var coarse = map.SourceBytes();
    var fine = map.FineSourceBytes(coarse);
    var dir = Path.Combine(Work, "fine-sources-to");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "heightmap.png"), coarse);
    File.WriteAllBytes(Path.Combine(dir, "heightmap-fine.png"), fine!);
    var info = PngInfo.Read(fine!);
    var record = FineHeights.ReadRecord(info.Texts);
    int mark = LogMark();
    var again = FineMap(Path.Combine(dir, "heightmap.png"))!;
    C(info.Width == S && info.Height == S && info.Depth == 8 && info.ColourType == 0 && !info.Interlaced && record is { Format: 1, Bits: 4 } && record.HeightmapCrc == Crc32.Compute(coarse, 0, coarse.Length),
      $"the fine file is 8-bit grey of the same size with the record: Fine Format 1, {record?.Bits} bits, and the CRC-32 of the heightmap.png beside it ({record?.HeightmapCrc:X8})");
    C(again.HasFine && again.FineNote == null && again.FineBits == map.FineBits && !LogSince(mark).Any(l => l.Contains("another heightmap.png")) && Points(S, 12, 800).All(p => Same(again.GetValue(p.x, p.y), map.GetValue(p.x, p.y))),
      "the pair reads as the map it was made from: fine heights, no note, every sample bit for bit");
    C(ImageMapFloat.Create(pair.Heightmap, ImageMapFloat.HeightAlpha.None, false, FineNever)!.FineSourceBytes(coarse) == null && FineMap(pair.Heightmap)!.FineSourceBytes(coarse) != null, "a map without fine heights has none to write");
  }

  // ---- PngRows reading 8-bit grey ---------------------------------------------------------------------------------------------------

  private static void FinePngRows()
  {
    Section("fine heights: PngRows reads 8-bit grey (the fine file) as ImageSharp does, every filter, every size");
    var filters = new[] { PngFilterMethod.None, PngFilterMethod.Sub, PngFilterMethod.Up, PngFilterMethod.Average, PngFilterMethod.Paeth, PngFilterMethod.Adaptive };
    int checks = 0, bad = 0;
    string? firstBad = null;
    foreach (int size in new[] { 1, 2, 3, 7, 31, 127, 128, 129, 300 })
      foreach (var filter in filters)
        for (int mix = 0; mix < 4; mix++)
        {
          using var grey8 = Picture(size, size, size * 7 + mix, (r, x, y) => new L8((byte)Bits(r, x, y, mix)));
          var png = Encode(grey8, PngColorType.Grayscale, PngBitDepth.Bit8, filter);
          checks++;
          bool ok;
          string why;
          try { ok = SameRows<L8>(png, out why); }
          catch (Exception e) { ok = false; why = e.Message; }
          if (!ok)
          {
            bad++;
            firstBad ??= $"{size} px {filter} mix {mix}: {why}";
          }
        }
    C(bad == 0, $"{checks} 8-bit grey pictures read as L8 give the same rows as ImageSharp" + (firstBad == null ? "" : $"; {bad} differ, first {firstBad}"));
    using var plain = Picture(40, 40, 2, (r, x, y) => new L8((byte)(x * 5 + y)));
    var grey = Encode(plain, PngColorType.Grayscale, PngBitDepth.Bit8);
    using var grey16 = Picture(40, 40, 2, (r, x, y) => new L16((ushort)(x * 1000 + y)));
    using var rgb = Picture(40, 40, 2, (r, x, y) => new Rgb24((byte)x, (byte)y, 7));
    using var greyAlpha = Picture(40, 40, 2, (r, x, y) => new La16((byte)x, (byte)y));
    using var rgba = Picture(40, 40, 2, (r, x, y) => new Rgba32((byte)x, (byte)y, 7, 200));
    var colours = new[] { new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255) };
    using var indexed = Picture(40, 40, 2, (r, x, y) => colours[(x + y) % 2]);
    using var paletted = new MemoryStream();
    indexed.Save(paletted, new PngEncoder { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit8, ChunkFilter = PngChunkFilter.ExcludeAll,
      Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.PaletteQuantizer(colours.Select(c => (SixLabors.ImageSharp.Color)c).ToArray(), new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions { Dither = null }) });
    var interlaced = (byte[])grey.Clone();
    interlaced[28] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(interlaced.AsSpan(29), Crc32.Compute(interlaced, 12, 17));
    C(PngRows<L8>.TryOpen(grey) != null && PngRows<L8>.TryOpen(Encode(grey16, PngColorType.Grayscale, PngBitDepth.Bit16)) == null && PngRows<L8>.TryOpen(Encode(rgb, PngColorType.Rgb, PngBitDepth.Bit8)) == null
      && PngRows<L8>.TryOpen(Encode(greyAlpha, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8)) == null && PngRows<L8>.TryOpen(Encode(rgba, PngColorType.RgbWithAlpha, PngBitDepth.Bit8)) == null
      && PngRows<L8>.TryOpen(paletted.ToArray()) == null && PngRows<L8>.TryOpen(interlaced) == null && PngRows<L8>.TryOpen(grey[..^20]) == null,
      "as L8 only 8-bit grey is read as rows: 16-bit grey, RGB, grey and alpha, RGBA, palette, interlaced and cut-short pictures are declined (ImageSharp reads them)");
    C(PngRows<L16>.TryOpen(grey) != null && PngRows<L8>.TryOpen(Encode(grey16, PngColorType.Grayscale, PngBitDepth.Bit16)) == null, "and the other pixel types read what they did");
  }

  // ---- speed (informational) -------------------------------------------------------------------------------------------------------

  private static void FineSpeed()
  {
    Section("fine heights: a sample with fine heights against one without, tiles cold and warm (informational)");
    const int S = 2048;
    var pair = FinePair(Path.Combine(Work, "fine-speed"), S, (x, y) => FineHeight(x, y, S), 8);
    // Every tile once: a sample at the middle of each of the 16 x 16 tiles' ... a grid with a step of 16 px reads every tile of 128.
    double ColdGrid(ImageMapFloat map)
    {
      var sw = Stopwatch.StartNew();
      float sink = 0;
      for (int y = 0; y < S; y += 16)
        for (int x = 0; x < S; x += 16)
          sink += map.GetValue(x / (float)(S - 1), y / (float)(S - 1));
      sw.Stop();
      if (sink == 1234.5f) System.Console.WriteLine();
      return sw.Elapsed.TotalMilliseconds;
    }
    var fine = FineMap(pair.Heightmap)!;
    var plain = ImageMapFloat.Create(pair.Heightmap, ImageMapFloat.HeightAlpha.None, true, FineNever)!;
    double coldPlain = ColdGrid(plain), coldFine = ColdGrid(fine);
    double warmPlain = ColdGrid(plain), warmFine = ColdGrid(fine);
    const int Count = 4_000_000;
    var xs = new float[Count];
    var ys = new float[Count];
    var random = new System.Random(3);
    for (int i = 0; i < Count; i++)
    {
      xs[i] = (float)random.NextDouble();
      ys[i] = (float)random.NextDouble();
    }
    double Time(Func<float, float, float> sample, bool sequential)
    {
      float sink = 0;
      var sw = Stopwatch.StartNew();
      for (int i = 0; i < Count; i++)
        sink += sequential ? sample(i % 4096 / 4096f, i / 4096 / 1024f) : sample(xs[i], ys[i]);
      sw.Stop();
      if (sink == 1234.5f) System.Console.WriteLine();
      return sw.Elapsed.TotalMilliseconds * 1e6 / Count;
    }
    Time(plain.GetValue, false);
    Time(fine.GetValue, false);
    double plainRandom = Time(plain.GetValue, false), fineRandom = Time(fine.GetValue, false);
    double plainRow = Time(plain.GetValue, true), fineRow = Time(fine.GetValue, true);
    System.Console.WriteLine($"    random points: without {plainRandom:F1} ns, with fine heights {fineRandom:F1} ns ({fineRandom / plainRandom:F2}x)");
    System.Console.WriteLine($"    along rows:    without {plainRow:F1} ns, with fine heights {fineRow:F1} ns ({fineRow / plainRow:F2}x)");
    System.Console.WriteLine($"    every tile once (256 tiles of 128 px): cold, without {coldPlain:F1} ms, with {coldFine:F1} ms; warm, without {warmPlain:F2} ms, with {warmFine:F2} ms");
    C(fineRandom < 4 * plainRandom + 50 && fineRow < 4 * plainRow + 50, "a sample with fine heights costs a few times a sample without at most");
  }
}

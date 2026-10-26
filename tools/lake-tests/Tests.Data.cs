// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// The data side (agent A) of the lakes work: Tarn's lake text and blob 81, Tarn's ".clear" file and blob 82, and the mask's test against a transcription of Tarn's own
// (the clear areas it built and the game's loop that asked them). The settings' keys, the old reader, the Directory, the push and the commands are in
// Tests.Settings.cs, Tests.Files.cs and Tests.Commands.cs. Transcriptions are marked [T: file] (Tarn's code) and [G: file] (the game's).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  // ---- Tarn's lake lines --------------------------------------------------------------------------------------------------------------------------

  // [T: Plugin.cs Lake.Parse], transcribed: the fields of a line, or null when it has fewer than nine. float.Parse throws on a bad number.
  private static Lake TarnParse(string line)
  {
    var p = line.Split(';');
    if (p.Length < 9) return null;
    float G(int i) => float.Parse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture);
    return new Lake(p[0], G(1), G(2), G(3), G(4), G(5), G(6), G(7), G(8));
  }

  // [T: Plugin.cs Lake.ToLine], transcribed: three decimals.
  private static string TarnLine(Lake l)
  {
    string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    return string.Join(";", l.Name, F(l.X), F(l.Z), F(l.Level), F(l.MinX), F(l.MinZ), F(l.MaxX), F(l.MaxZ), F(l.Depth));
  }

  private static float RandomFloat(Random rng, float range) => (float)((rng.NextDouble() * 2 - 1) * range);

  // A lake with a point, a box around it and a level above the sea, as a map maker's pipeline writes them (so reading it warns of nothing).
  private static Lake RandomLake(Random rng, int k)
  {
    float x = RandomFloat(rng, 20000), z = RandomFloat(rng, 20000);
    return new("lake" + k + (rng.Next(3) == 0 ? " ä ö" : ""), x, z, 31f + rng.NextSingle() * 200f,
      x - 10f - rng.NextSingle() * 500f, z - 10f - rng.NextSingle() * 500f, x + 10f + rng.NextSingle() * 500f, z + 10f + rng.NextSingle() * 500f, rng.NextSingle() * 30f);
  }

  private static void LakeLinesTest()
  {
    Section("lakes: Tarn's text format");
    var warnings = new List<string>();
    var set = LakeSet.ParseLines(File.ReadAllLines(Fixture("lakes-tarn.txt")), warnings.Add);
    C(set.Lakes.Select(l => l.Name).SequenceEqual(["Vikedal1", "Vikedal2", "Extra", "Low", "Flat"]), "Tarn's lines read: comments and blank lines skipped, a short line and a bad number left out, fields past the ninth ignored: "
      + string.Join(", ", set.Lakes.Select(l => l.Name)));
    var v1 = set.Lakes[0];
    C(v1 == new Lake("Vikedal1", -4210.5f, 3190.25f, 92f, -4420f, 3050f, -4010f, 3330f, 11.4f), "Vikedal1 is read to the bit");
    C(set.DepthScale == LakeSet.DefaultDepthScale && set.Revision == 0, "a parsed set has the default depth scale and no changes");
    C(warnings.Count == 4, "four warnings: " + string.Join(" | ", warnings));
    C(warnings.Any(w => w.StartsWith("line 6:") && w.Contains("4 fields") && w.Contains("left out")), "the short line is said, with its number (Tarn dropped it without a word)");
    C(warnings.Any(w => w.StartsWith("line 8:") && w.Contains("field 4 'sixty' is not a number") && w.Contains("left out")), "the bad number is said, with its line and field");
    C(warnings.Any(w => w.StartsWith("line 9:") && w.Contains("Low") && w.Contains("under 30.5")), "a lake under the sea's 30.5 m is warned about (and kept)");
    C(warnings.Any(w => w.StartsWith("line 10:") && w.Contains("Flat") && w.Contains("empty box")), "a lake with an empty box is warned about (and kept)");

    // Against a transcription of Tarn's reader, on made-up lines in the form Tarn writes.
    var rng = new Random(11);
    int same = 0;
    for (int i = 0; i < 2000; i++)
    {
      var tarnLake = RandomLake(rng, i);
      var line = TarnLine(tarnLake);
      var theirs = TarnParse(line);
      var mine = LakeSet.ParseLines([line], _ => { }).Lakes.SingleOrDefault();
      if (theirs != null && mine != null && theirs == mine)
        same++;
    }
    C(same == 2000, $"2000 lines in Tarn's own form (three decimals) read the same as Tarn's reader reads them: {same}");

    // Round trip: the numbers keep every bit through ToLines and back.
    var lakes = Enumerable.Range(0, 1500).Select(i => RandomLake(rng, i)).ToList();
    var original = new LakeSet(lakes, 0.5f, 0);
    var lines = original.ToLines().ToList();
    var back = LakeSet.ParseLines(lines, w => C(false, "round trip warned: " + w));
    C(back.Lakes.SequenceEqual(lakes), "1500 lakes through ToLines and ParseLines come back exactly (round-trip floats)");
    C(lines.All(l => l.Count(ch => ch == ';') == 8 && !l.Contains(',')), "every line has nine fields and no comma");
    // The file form starts with Tarn's header.
    C(original.ToFileLines().First() == LakeSet.FileHeader && LakeSet.FileHeader.StartsWith("# Tarn lakes: name;x;z;level;minX;minZ;maxX;maxZ;depth"), "the file starts with Tarn's header comment");

    // A machine whose culture writes decimal commas changes no number.
    var oldCulture = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = new CultureInfo("de-DE");
      var germanLines = original.ToLines().ToList();
      var germanBack = LakeSet.ParseLines(germanLines, w => C(false, "German culture warned: " + w));
      C(germanLines.SequenceEqual(lines) && germanBack.Lakes.SequenceEqual(lakes), "under the German culture the lines are the same and read the same");
      C(LakeSet.ParseLines(["x;1.5;2.5;60;0;0;10;10;3"], _ => { }).Lakes[0].X == 1.5f, "and a point is a point, not a thousands separator");
    }
    finally
    {
      CultureInfo.CurrentCulture = oldCulture;
    }

    // Names, limits and odd lines.
    var odd = new List<string>();
    var shaped = LakeSet.ParseLines([";1;2;60;0;0;9;9;3", new string('n', 90) + ";1;2;60;0;0;9;9;3", "  spaced  ;1;2;60;0;0;9;9;3  ", "﻿bom;1;2;60;0;0;9;9;3", "nan;NaN;2;60;0;0;9;9;3", "inf;1;2;60;0;0;9;9;1e999"], odd.Add);
    C(shaped.Lakes.Select(l => l.Name).SequenceEqual(["lake1", new string('n', 64), "spaced", "bom"]), "a nameless lake is named, a long name cut to 64, space trimmed, a BOM skipped: " + string.Join(", ", shaped.Lakes.Select(l => l.Name.Length > 10 ? l.Name.Length + " chars" : l.Name)));
    C(odd.Any(w => w.Contains("no name")) && odd.Any(w => w.Contains("longer than 64")) && odd.Any(w => w.StartsWith("line 5:") && w.Contains("finite")) && odd.Any(w => w.StartsWith("line 6:")), "each is said: " + string.Join(" | ", odd));
    var many = Enumerable.Range(0, LakeSet.MaxLakes + 3).Select(i => $"l{i};1;2;60;0;0;9;9;3").ToList();
    var manyWarnings = new List<string>();
    var capped = LakeSet.ParseLines(many, manyWarnings.Add);
    C(capped.Lakes.Count == LakeSet.MaxLakes && manyWarnings.Count == 1 && manyWarnings[0].Contains("at most 5,000"), "at most 5,000 lakes are read, and the rest is said once");
    C(LakeSet.Clean("a;b\r\nc") == "a_b  c" && new LakeSet([new Lake("a;b", 1, 2, 60, 0, 0, 9, 9, 3)], 0.5f, 0).ToLines().Single().StartsWith("a_b;"), "a name cannot carry the separator or a line break out");

    // Changes make new sets.
    var one = capped.Added(new Lake("lake9999", 1, 2, 60, 0, 0, 9, 9, 3));
    var gone = one.Removed("lake9999", out int removed);
    C(removed == 1 && gone.Lakes.Count == one.Lakes.Count - 1 && gone.Revision == one.Revision + 1 && ReferenceEquals(one.Removed("nothing", out int none), one) && none == 0, "Removed and Added make new sets; removing nothing is the same set");
    C(one.Removed("all", out int all).IsEmpty && all == one.Lakes.Count, "'all' removes every lake");
    C(new LakeSet([new Lake("lake1", 0, 0, 60, 0, 0, 1, 1, 1), new Lake("lake3", 0, 0, 60, 0, 0, 1, 1, 1)], 0.5f, 0).NextName() == "lake2", "the next name is the first free lakeN [T: Plugin.Here]");
  }

  private static void LakeBlockTest()
  {
    Section("lakes: blob 81");
    var rng = new Random(12);
    var lakes = Enumerable.Range(0, 300).Select(i => RandomLake(rng, i)).ToList();
    var set = new LakeSet(lakes, 0.8f, 5);
    var blob = set.ToBlock();
    C(BitConverter.ToInt32(blob, 0) == 1 && LakeSet.VersionOf(blob) == 1 && BitConverter.ToSingle(blob, 4) == 0.8f && BitConverter.ToInt32(blob, 8) == 300, "the blob starts with its format version 1, the depth scale and the count");
    var back = LakeSet.FromBlock(blob);
    C(back.Lakes.SequenceEqual(lakes) && back.DepthScale == 0.8f && back.Revision == 0, "read back: the same lakes to the bit and the depth scale (the revision is not saved)");
    C(back.ToBlock().SequenceEqual(blob), "and written again as the same bytes");
    C(LakeSet.Empty.ToBlock().Length == 12 && LakeSet.FromBlock(LakeSet.Empty.ToBlock()).IsEmpty, "an empty set is a 12-byte blob that reads as an empty set");
    // Refusals, each by name.
    var newer = (byte[])blob.Clone();
    BitConverter.GetBytes(7).CopyTo(newer, 0);
    C(RefusesLake(() => LakeSet.FromBlock(newer), "format 7", "newer Better Continents"), "a newer format is refused by name");
    C(RefusesLake(() => LakeSet.FromBlock(blob.Take(blob.Length - 3).ToArray()), "too soon") && RefusesLake(() => LakeSet.FromBlock([1, 0]), "too soon") && RefusesLake(() => LakeSet.FromBlock([]), "too soon"), "a cut blob is refused");
    C(RefusesLake(() => LakeSet.FromBlock(blob.Concat(new byte[] { 1, 2 }).ToArray()), "2 bytes follow"), "and one with bytes left over");
    var nan = (byte[])blob.Clone();
    BitConverter.GetBytes(float.NaN).CopyTo(nan, 4);
    C(RefusesLake(() => LakeSet.FromBlock(nan), "depth scale"), "a depth scale that is not a number");
    var count = (byte[])blob.Clone();
    BitConverter.GetBytes(LakeSet.MaxLakes + 1).CopyTo(count, 8);
    C(RefusesLake(() => LakeSet.FromBlock(count), "at most 5,000"), "a count over the limit");
    BitConverter.GetBytes(-1).CopyTo(count, 8);
    C(RefusesLake(() => LakeSet.FromBlock(count), "count"), "and a negative one");
    var zero = (byte[])blob.Clone();
    BitConverter.GetBytes(0).CopyTo(zero, 0);
    C(RefusesLake(() => LakeSet.FromBlock(zero), "format number"), "and format 0");
    // A lake with a number that is not finite (a build before this one could write it) is left out, with a word; the rest of the list reads.
    var badFloat = (byte[])blob.Clone();
    int firstFloat = 12 + 1 + Encoding(lakes[0].Name).Length;
    BitConverter.GetBytes(float.PositiveInfinity).CopyTo(badFloat, firstFloat);
    var warnings = new List<string>();
    var dropped = LakeSet.FromBlock(badFloat, warnings.Add);
    C(dropped.Lakes.Count == 299 && dropped.Lakes.SequenceEqual(lakes.Skip(1)) && warnings.Count == 1 && warnings[0].Contains("lake 1") && warnings[0].Contains("not finite"), "a lake with a number that is not finite is left out with a word, and the other 299 read: " + string.Join(" | ", warnings));
    C(LogHandler.During(() => LakeSet.FromBlock(badFloat)).Any(l => l.StartsWith("[Warning]") && l.Contains("not finite")), "and the word is in the log");
    var tooMany = new LakeSet(Enumerable.Range(0, LakeSet.MaxLakes + 1).Select(i => lakes[0]).ToList(), 0.5f, 0);
    C(RefusesLake(() => tooMany.ToBlock(), "at most 5,000"), "a set over the limit is not written");
    // A non-ASCII name survives.
    var ä = new LakeSet([new Lake("Östersjön ä", 1, 2, 60, 0, 0, 9, 9, 3)], 0.5f, 0);
    C(LakeSet.FromBlock(ä.ToBlock()).Lakes[0].Name == "Östersjön ä", "a name with accents survives the blob");
    byte[] Encoding(string s) => new System.Text.UTF8Encoding(false).GetBytes(s);
  }

  // ---- Tarn's ".clear" file ------------------------------------------------------------------------------------------------------------------------

  // The pattern of tools/lake-tests/make_fixtures.py (numpy writes the fixtures; this rule is its twin).
  private static bool MaskPattern(int r, int c, int size) => (r * 7 + c * 3 + (r * c) % 5) % 11 == 0 || r == c || (r == size - 1 && c > size / 2);

  // A ".clear" file of the given pixels, packed by this suite's own code (bit i is byte i / 8, value 0x80 >> (i % 8)).
  private static byte[] MakeClearFile(int size, float extent, Func<int, int, bool> marked)
  {
    var file = new byte[16 + (size * size + 7) / 8];
    System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(file, 0);
    BitConverter.GetBytes(size).CopyTo(file, 8);
    BitConverter.GetBytes(extent).CopyTo(file, 12);
    for (int r = 0; r < size; r++)
      for (int c = 0; c < size; c++)
        if (marked(r, c))
        {
          long i = (long)r * size + c;
          file[16 + (int)(i >> 3)] |= (byte)(0x80 >> (int)(i & 7));
        }
    return file;
  }

  private static void MaskFileTest()
  {
    Section("clearing: Tarn's .clear file (made by numpy's packbits)");
    foreach (var (name, size, extent) in new[] { ("clear37.bin", 37, 123.5f), ("clear64.bin", 64, 32f), ("clear8.bin", 8, 3f) })
    {
      var file = File.ReadAllBytes(Fixture(name));
      var mask = ClearMask.FromTarnFile(file);
      C(mask.Size == size && mask.Extent == extent && file.Length == 16 + (size * size + 7) / 8, $"{name}: {size} x {size} over +-{extent}, {file.Length} bytes");
      bool allRight = true;
      long expected = 0;
      for (int r = 0; r < size; r++)
        for (int c = 0; c < size; c++)
        {
          bool want = MaskPattern(r, c, size);
          expected += want ? 1 : 0;
          if (mask.At(r, c) != want)
            allRight = false;
        }
      C(allRight, $"{name}: every pixel is where numpy put it (row 0 the north edge, the high bit first)");
      C(mask.Marked == expected, $"{name}: {expected} pixels are marked: {mask.Marked}");
      float mpx = 2f * extent / size;
      C(Math.Abs(mask.Km2 - expected * (double)mpx * mpx / 1e6) < 1e-12 && mask.MetresPerPixel == mpx, $"{name}: Km2 is pixels x mpx^2 [T: Clearing.Km2]: {mask.Km2:0.000000}");
      C(mask.ToTarnFile().SequenceEqual(file), $"{name}: written back as the same bytes");
      C(mask.CentreOf(0, 0).X == 0.5f * mpx - extent && mask.CentreOf(0, 0).Z == extent - 0.5f * mpx, $"{name}: pixel (0, 0) is centred at the north-west corner [T: Clearing.AreasFor]");
      C(mask.PixelAt(mask.CentreOf(3, 5).X, mask.CentreOf(3, 5).Z) is (3, 5) && mask.PixelAt(extent + 1, 0) == null, $"{name}: a point finds its pixel, and outside the mask has none");
    }
    // Padding bits of the last byte: kept in the bytes, never counted.
    var padded = File.ReadAllBytes(Fixture("clear37.bin"));
    padded[^1] |= 0x7F;
    var pm = ClearMask.FromTarnFile(padded);
    C(pm.ToTarnFile().SequenceEqual(padded) && pm.Marked == ClearMask.FromTarnFile(File.ReadAllBytes(Fixture("clear37.bin"))).Marked, "set bits past the last pixel are kept in the file and not counted");
    // Trailing bytes are ignored as Tarn ignores them; the file written is exact.
    var longer = File.ReadAllBytes(Fixture("clear64.bin")).Concat(new byte[] { 1, 2, 3 }).ToArray();
    C(ClearMask.FromTarnFile(longer).ToTarnFile().Length == 16 + 512, "bytes after the bits are ignored, and not written back");
    // Refusals.
    var good = File.ReadAllBytes(Fixture("clear64.bin"));
    C(RefusesLake(() => ClearMask.FromTarnFile(good.Take(10).ToArray()), "16-byte header"), "a file shorter than its header");
    var magic = (byte[])good.Clone();
    magic[7] = (byte)'2';
    C(RefusesLake(() => ClearMask.FromTarnFile(magic), "TARNCLR1"), "a file with another magic");
    C(RefusesLake(() => ClearMask.FromTarnFile(good.Take(good.Length - 1).ToArray()), "511 of 512"), "a file with a byte too few says how many");
    foreach (var bad in new[] { 0, -4, ClearMask.MaxSize + 1 })
    {
      var b = (byte[])good.Clone();
      BitConverter.GetBytes(bad).CopyTo(b, 8);
      C(RefusesLake(() => ClearMask.FromTarnFile(b), "size") || RefusesLake(() => ClearMask.FromTarnFile(b), "pixels across"), $"a size of {bad}");
    }
    foreach (var bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
    {
      var b = (byte[])good.Clone();
      BitConverter.GetBytes(bad).CopyTo(b, 12);
      C(RefusesLake(() => ClearMask.FromTarnFile(b), "half-extent"), $"a half-extent of {bad}");
    }
    C(ClearMask.FromTarnFile(MakeClearFile(1, 5f, (r, c) => true)).At(0, 0), "a 1 x 1 mask is a mask");
  }

  private static void MaskBlockTest()
  {
    Section("clearing: blob 82");
    var file = File.ReadAllBytes(Fixture("clear37.bin"));
    var mask = ClearMask.FromTarnFile(file);
    var blob = mask.ToBlock();
    int raw = (37 * 37 + 7) / 8;
    C(BitConverter.ToInt32(blob, 0) == 1 && BitConverter.ToInt32(blob, 4) == 37 && BitConverter.ToSingle(blob, 8) == 123.5f && BitConverter.ToInt32(blob, 12) == raw
      && BitConverter.ToInt32(blob, 16) == blob.Length - 20, "the blob: version 1, size, extent, raw length, packed length");
    using (var inflate = new DeflateStream(new MemoryStream(blob, 20, blob.Length - 20), CompressionMode.Decompress))
    {
      var bits = new byte[raw + 1];
      int got = 0, n;
      while ((n = inflate.Read(bits, got, bits.Length - got)) > 0)
        got += n;
      C(got == raw && bits.Take(raw).SequenceEqual(file.Skip(16)), "the packed bytes are the raw deflate of the bit rows as the file holds them");
    }
    C(mask.ToBlock().SequenceEqual(blob) && ReferenceEquals(mask.ToBlock(), blob), "the blob is made once and kept (a re-save never deflates again)");
    C(ClearMask.FromTarnFile(file).ToBlock().SequenceEqual(blob), "and the same file in this process gives the same blob");
    var again = ClearMask.FromBlock(blob);
    C(!again.Decoded && again.Size == 37 && again.Extent == 123.5f, "read back: the header only, the bits not opened");
    C(again.ToBlock().SequenceEqual(blob) && !again.Decoded, "and written again without opening them");
    C(again.Km2 == mask.Km2 && again.Decoded && again.ToTarnFile().SequenceEqual(file), "Km2 and the file open the bits, and give what the file had");
    // A blob another runtime deflated differently is kept as it is read.
    byte[] packed;
    using (var ms = new MemoryStream())
    {
      using (var d = new DeflateStream(ms, CompressionLevel.NoCompression, true))
        d.Write(file, 16, raw);
      packed = ms.ToArray();
    }
    var other = new byte[20 + packed.Length];
    Array.Copy(blob, other, 16);
    BitConverter.GetBytes(packed.Length).CopyTo(other, 16);
    packed.CopyTo(other, 20);
    var fromOther = ClearMask.FromBlock(other);
    C(!other.SequenceEqual(blob) && fromOther.ToBlock().SequenceEqual(other) && fromOther.ToTarnFile().SequenceEqual(file), "a blob deflated another way is saved back as it was read, and still opens to the same bits");
    // Refusals.
    var newer = (byte[])blob.Clone();
    BitConverter.GetBytes(9).CopyTo(newer, 0);
    C(RefusesLake(() => ClearMask.FromBlock(newer), "format 9", "newer Better Continents"), "a newer format is refused by name");
    C(RefusesLake(() => ClearMask.FromBlock(blob.Take(15).ToArray()), "cut short") && RefusesLake(() => ClearMask.FromBlock([1]), "cut short"), "a blob cut short");
    var rawLength = (byte[])blob.Clone();
    BitConverter.GetBytes(raw + 1).CopyTo(rawLength, 12);
    C(RefusesLake(() => ClearMask.FromBlock(rawLength), "bytes of bits"), "a raw length that does not fit the size");
    var packedLength = (byte[])blob.Clone();
    BitConverter.GetBytes(blob.Length).CopyTo(packedLength, 16);
    C(RefusesLake(() => ClearMask.FromBlock(packedLength), "packed bytes"), "a packed length that does not fit the blob");
    var huge = (byte[])blob.Clone();
    BitConverter.GetBytes(ClearMask.MaxSize + 1).CopyTo(huge, 4);
    C(RefusesLake(() => ClearMask.FromBlock(huge), "pixels across"), "a size over the limit");
    // Damaged bits: the header reads, the bits do not.
    var damaged = (byte[])blob.Clone();
    for (int i = 20; i < damaged.Length; i++)
      damaged[i] = 0xFF;
    var dm = ClearMask.FromBlock(damaged);
    C(RefusesLake(dm.Verify, "do not open") || RefusesLake(dm.Verify, "end after") || RefusesLake(dm.Verify, "go on past"), "damaged bits are found when they are opened (Verify), by name");
    C(RefusesLake(() => dm.Covers(0, 0), "") && RefusesLake(() => _ = dm.Km2, "") && dm.ToBlock().SequenceEqual(damaged), "and keep refusing; the blob still saves back as it was");
    var short1 = (byte[])blob.Clone();
    Array.Resize(ref short1, 22);
    BitConverter.GetBytes(2).CopyTo(short1, 16);
    C(RefusesLake(() => ClearMask.FromBlock(short1).Verify(), "") , "bits that end too soon");
  }

  // What the mask costs (BUILD-SPEC-TARN.md 4.3 test 14, 4.5): Covers over millions of lookups, and a 16384 x 16384 mask (the largest) read, packed, sent and opened.
  private static void MaskCostTest()
  {
    Section("clearing: cost");
    var rng = new System.Random(3);
    var bits = RandomRoads(rng, 4096, 400, 40);
    var file = new byte[16 + bits.Length];
    System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(file, 0);
    BitConverter.GetBytes(4096).CopyTo(file, 8);
    BitConverter.GetBytes(10500f).CopyTo(file, 12);
    bits.CopyTo(file, 16);
    var mask = ClearMask.FromTarnFile(file);
    const int N = 5_000_000;
    var xs = new float[1 << 16];
    var zs = new float[1 << 16];
    for (int i = 0; i < xs.Length; i++)
    {
      xs[i] = RandomFloat(rng, 10500);
      zs[i] = RandomFloat(rng, 10500);
    }
    int covered = 0;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < N; i++)
      covered += mask.Covers(xs[i & 0xFFFF], zs[i & 0xFFFF]) ? 1 : 0;
    double ns = watch.Elapsed.TotalMilliseconds * 1e6 / N;
    System.Console.WriteLine($"   Covers: {ns:0.0} ns a lookup over {N:N0} random points of a 4096 x 4096 mask ({covered:N0} covered); a zone asks 500 to 3,000 times");
    C(ns < 400, $"a lookup takes {ns:0.0} ns (under 400: seven times what it is measured at on this machine)");

    watch.Restart();
    var big = new byte[16 + ClearMask.RawLengthOf(ClearMask.MaxSize)];
    System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(big, 0);
    BitConverter.GetBytes(ClearMask.MaxSize).CopyTo(big, 8);
    BitConverter.GetBytes(10500f).CopyTo(big, 12);
    var roads = RandomRoads(rng, ClearMask.MaxSize, 4000, 400);
    roads.CopyTo(big, 16);
    double made = watch.Elapsed.TotalSeconds;
    watch.Restart();
    var bigMask = ClearMask.FromTarnFile(big);
    double read = watch.Elapsed.TotalSeconds;
    watch.Restart();
    var blob = bigMask.ToBlock();
    double packed = watch.Elapsed.TotalSeconds;
    watch.Restart();
    var client = ClearMask.FromBlock(blob);
    double header = watch.Elapsed.TotalSeconds;
    watch.Restart();
    client.Verify();
    double opened = watch.Elapsed.TotalSeconds;
    System.Console.WriteLine($"   16384 x 16384: file {big.Length / 1048576.0:0.0} MB, read {read * 1000:0} ms, packed to {blob.Length / 1024.0:N0} KB in {packed:0.0} s, a client's header read {header * 1000:0} ms, opened {opened * 1000:0} ms, {bigMask.Km2:0.0} km2");
    C(blob.Length < 16 * 1024 * 1024 && client.Decoded && client.ToTarnFile().SequenceEqual(big), "the largest mask (4,000 random roads: far noisier than a real one) packs under 16 MB, and opens to the very bytes");
    C(!ClearMask.FromBlock(blob).Decoded, "a client holds only the packed blob until it asks for the bits");
  }

  // ---- the mask against Tarn's area test -----------------------------------------------------------------------------------------------------------

  private readonly record struct Area(float X, float Z, float R);

  // [T: Clearing.cs AreasFor, :131-149], transcribed: one square clear area per marked pixel within reach of the zone (64 m square around its centre).
  private static List<Area> TarnAreasFor(byte[] bits, int n, float extent, float zoneX, float zoneZ)
  {
    var added = new List<Area>();
    float mpx = 2f * extent / n, reach = 32f + mpx;
    int c0 = Math.Max(0, (int)MathF.Floor((zoneX - reach + extent) / mpx));
    int c1 = Math.Min(n - 1, (int)MathF.Floor((zoneX + reach + extent) / mpx));
    int r0 = Math.Max(0, (int)MathF.Floor((extent - (zoneZ + reach)) / mpx));
    int r1 = Math.Min(n - 1, (int)MathF.Floor((extent - (zoneZ - reach)) / mpx));
    float half = mpx * 0.5f + 0.25f;
    for (int r = r0; r <= r1; r++)
      for (int c = c0; c <= c1; c++)
      {
        long i = (long)r * n + c;
        if ((bits[i >> 3] & (0x80 >> (int)(i & 7))) != 0)
          added.Add(new Area((c + 0.5f) * mpx - extent, extent - (r + 0.5f) * mpx, half));
      }
    return added;
  }

  // [G: ZoneSystem.cs InsideClearArea, :1589-1599], transcribed.
  private static bool GameInside(List<Area> areas, float x, float z)
  {
    foreach (var a in areas)
      if (x > a.X - a.R && x < a.X + a.R && z > a.Z - a.R && z < a.Z + a.R)
        return true;
    return false;
  }

  private static byte[] RandomRoads(Random rng, int n, int roads, int blobs)
  {
    var bits = new byte[(n * n + 7) / 8];
    void Set(int r, int c)
    {
      if (r < 0 || c < 0 || r >= n || c >= n) return;
      long i = (long)r * n + c;
      bits[i >> 3] |= (byte)(0x80 >> (int)(i & 7));
    }
    for (int k = 0; k < roads; k++)
    {
      double r = rng.NextDouble() * n, c = rng.NextDouble() * n, a = rng.NextDouble() * Math.PI * 2;
      int width = 1 + rng.Next(3), length = Math.Max(10, (int)(n * (0.05 + rng.NextDouble() * 0.3)));
      for (int s = 0; s < length; s++)
      {
        r += Math.Sin(a); c += Math.Cos(a); a += (rng.NextDouble() - 0.5) * 0.1;
        for (int dr = -width / 2; dr <= width / 2; dr++)
          for (int dc = -width / 2; dc <= width / 2; dc++)
            Set((int)r + dr, (int)c + dc);
      }
    }
    for (int k = 0; k < blobs; k++)
    {
      int r = rng.Next(n), c = rng.Next(n), radius = 2 + rng.Next(Math.Max(3, n / 100));
      for (int dr = -radius; dr <= radius; dr++)
        for (int dc = -radius; dc <= radius; dc++)
          if (dr * dr + dc * dc <= radius * radius)
            Set(r + dr, c + dc);
    }
    return bits;
  }

  // The centre of the zone a coordinate is in [G: ZoneSystem.GetZone: the zone grid is 64 m squares centred on multiples of 64].
  private static float ZoneCentre(float v) => MathF.Floor((v + 32f) / 64f) * 64f;

  // Compares Covers with the oracle at these points: each is asked of the areas Tarn built for the zone it is in (the game only ever asks a spot of the zone that places it).
  private static (int Differences, int Covered, int Checked) CompareZone(ClearMask mask, byte[] bits, int n, float extent, IEnumerable<(float X, float Z)> points)
  {
    var cache = new Dictionary<(float, float), List<Area>>();
    int diff = 0, covered = 0, count = 0;
    foreach (var (x, z) in points)
    {
      var zone = (ZoneCentre(x), ZoneCentre(z));
      if (!cache.TryGetValue(zone, out var areas))
      {
        if (cache.Count > 4000)
          cache.Clear();
        cache[zone] = areas = TarnAreasFor(bits, n, extent, zone.Item1, zone.Item2);
      }
      bool want = GameInside(areas, x, z), got = mask.Covers(x, z);
      count++;
      covered += want ? 1 : 0;
      if (want != got)
      {
        if (diff < 5)
          System.Console.WriteLine($"   differs at ({x:R}, {z:R}) in the zone {zone}: Tarn's areas say {want}, Covers {got}");
        diff++;
      }
    }
    return (diff, covered, count);
  }

  private static IEnumerable<(float X, float Z)> ZonePoints(Random rng, float zoneX, float zoneZ, int count, List<Area> areas)
  {
    for (int i = 0; i < count; i++)
    {
      float x, z;
      int kind = rng.Next(3);
      if (kind == 0 || areas.Count == 0)
      {
        // anywhere in the zone [G: ZoneSystem.PlaceVegetation places within +-32 m of the zone's centre]
        x = zoneX + (rng.NextSingle() * 64f - 32f);
        z = zoneZ + (rng.NextSingle() * 64f - 32f);
      }
      else
      {
        // near the edge of an area: within a few centimetres of its sides, which is where a wrong comparison would show
        var a = areas[rng.Next(areas.Count)];
        float jitter() => (rng.NextSingle() - 0.5f) * 0.05f;
        x = kind == 1 ? a.X + (rng.Next(2) == 0 ? a.R : -a.R) + jitter() : a.X + (rng.NextSingle() * 2f - 1f) * a.R;
        z = kind == 1 ? a.Z + (rng.NextSingle() * 2f - 1f) * a.R : a.Z + (rng.Next(2) == 0 ? a.R : -a.R) + jitter();
      }
      yield return (x, z);
    }
  }

  private static void MaskCoversTest()
  {
    Section("clearing: Covers against Tarn's areas and the game's loop");
    foreach (var (n, extent, roads, zones, pointsPerZone) in new[] { (4096, 10500f, 400, 1500, 640), (255, 3f, 0, 1, 20000), (37, 123.5f, 0, 6, 4000), (64, 32f, 0, 4, 4000), (1000, 7.77f, 60, 8, 20000) })
    {
      var rng = new Random(1000 + n);
      byte[] bits;
      if (roads > 0)
        bits = RandomRoads(rng, n, roads, 40);
      else if (n == 255)
      {
        // a third of the pixels, at random: a mask of an extent of 3 m, whose pixels are 2 cm
        bits = new byte[(n * n + 7) / 8];
        for (long i = 0; i < (long)n * n; i++)
          if (rng.Next(3) == 0)
            bits[i >> 3] |= (byte)(0x80 >> (int)(i & 7));
      }
      else
        bits = File.ReadAllBytes(Fixture(n == 37 ? "clear37.bin" : "clear64.bin")).Skip(16).ToArray();
      var file = new byte[16 + bits.Length];
      System.Text.Encoding.ASCII.GetBytes("TARNCLR1").CopyTo(file, 0);
      BitConverter.GetBytes(n).CopyTo(file, 8);
      BitConverter.GetBytes(extent).CopyTo(file, 12);
      bits.CopyTo(file, 16);
      var mask = ClearMask.FromTarnFile(file);
      // the second mask came through blob 82, as a client's does
      var viaBlob = ClearMask.FromBlock(mask.ToBlock());
      int diff = 0, diffBlob = 0, covered = 0, total = 0;
      var marked = new List<(int R, int C)>();
      for (int r = 0; r < n && marked.Count < 20000; r += Math.Max(1, n / 600))
        for (int c = 0; c < n; c++)
          if (mask.At(r, c))
            marked.Add((r, c));
      float mpx = 2f * extent / n;
      for (int z = 0; z < zones; z++)
      {
        float zx, zz;
        if (n == 255 || n == 37 || n == 64)
          (zx, zz) = (z == 0 ? 0f : MathF.Round(RandomFloat(rng, extent) / 64f) * 64f, z == 0 ? 0f : MathF.Round(RandomFloat(rng, extent) / 64f) * 64f);
        else if (marked.Count > 0 && z % 5 != 4)
        {
          var (r, c) = marked[rng.Next(marked.Count)];
          var centre = mask.CentreOf(r, c);
          (zx, zz) = (MathF.Round(centre.X / 64f) * 64f, MathF.Round(centre.Z / 64f) * 64f);
        }
        else
          (zx, zz) = (MathF.Round(RandomFloat(rng, extent) / 64f) * 64f, MathF.Round(RandomFloat(rng, extent) / 64f) * 64f);
        var areas = TarnAreasFor(bits, n, extent, zx, zz);
        var points = ZonePoints(rng, zx, zz, pointsPerZone, areas).ToList();
        var (d1, cov, count) = CompareZone(mask, bits, n, extent, points);
        var (d2, _, _) = CompareZone(viaBlob, bits, n, extent, points);
        diff += d1;
        diffBlob += d2;
        covered += cov;
        total += count;
      }
      C(diff == 0 && total > 0, $"{n} x {n} over +-{extent} m (a pixel is {mpx:0.####} m): {total:N0} points, {covered:N0} of them covered, {diff} differ from Tarn's areas");
      C(diffBlob == 0, $"{n} x {n}: the same through blob 82, as a client's mask is read: {diffBlob} differ");
      // The corners and sides of pixels, exactly and one float step either side, for the strict inequality.
      int edgeDiff = 0, edgeCount = 0, edgeCovered = 0;
      for (int k = 0; k < Math.Min(marked.Count, 3000); k++)
      {
        var (r, c) = marked[rng.Next(marked.Count)];
        var (cx, cz) = mask.CentreOf(r, c);
        float half = mpx * 0.5f + 0.25f;
        var pts = new List<(float, float)>();
        foreach (var x in new[] { cx - half, cx + half, cx })
          foreach (var z in new[] { cz - half, cz + half, cz })
            foreach (var dx in new[] { 0, 1, -1 })
              foreach (var dz in new[] { 0, 1, -1 })
              {
                float px = dx == 0 ? x : dx > 0 ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
                float pz = dz == 0 ? z : dz > 0 ? MathF.BitIncrement(z) : MathF.BitDecrement(z);
                pts.Add((px, pz));
              }
        var (d, cov, count) = CompareZone(mask, bits, n, extent, pts);
        edgeDiff += d;
        edgeCount += count;
        edgeCovered += cov;
      }
      C(edgeDiff == 0, $"{n} x {n}: {edgeCount:N0} points on the sides and corners of pixels, exactly and a float step either side ({edgeCovered:N0} covered): {edgeDiff} differ");
    }
    // Outside the mask, and the odd ones.
    var m = ClearMask.FromTarnFile(File.ReadAllBytes(Fixture("clear64.bin")));
    C(!m.Covers(1e9, 0) && !m.Covers(0, -1e9) && !m.Covers(double.NaN, 0) && !m.Covers(0, double.PositiveInfinity) && !m.Covers(-1e30, 1e30), "a point far outside the mask, or not a number, is not covered");
    C(m.Touches(-33, -33, 33, 33) && !m.Touches(100, 100, 200, 200), "Touches: a rectangle over the mask, and one far from it");
  }
}

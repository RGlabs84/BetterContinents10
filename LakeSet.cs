// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BetterContinents;

// THE CONTRACT of the lakes work (BUILD-SPEC-TARN.md 2.1), written by the lead before the data agent (A) and the engine agent (B) started: the types the two
// share. A filled in the bodies; B reads Lakes, DepthScale and the Lake records and needs nothing else of A.

// One lake of the world's list: where Tarn's flood starts (X, Z, Level), the box it may not leave (MinX..MaxZ), and the depth of its water over the ground
// at the deepest. Level is the water's height in metres above the world's zero; the sea is at 30 m.
internal sealed record Lake(string Name, float X, float Z, float Level, float MinX, float MinZ, float MaxX, float MaxZ, float Depth);

// What a lake list or a clearing mask in a file or a settings blob could not be read for. Its message says what is wrong in words for a person.
internal sealed class LakeFormatException(string message) : Exception(message);

// The world's lakes, immutable: a change is a new set (settings.Lakes is swapped whole). Revision counts the changes of this session and is not saved.
internal sealed class LakeSet
{
  public static readonly LakeSet Empty = new(Array.Empty<Lake>(), DefaultDepthScale, 0);

  public const float DefaultDepthScale = 0.5f;
  public const float MaxDepthScale = 3f;
  public const int MaxLakes = 5000;
  // A lake's name: at most this many characters, no ';' (the text format's separator) and no line breaks.
  public const int MaxNameLength = 64;
  // The format of blob 72 (DataKey.Lakes). The first int32 of the blob; a newer one is kept as it was read (Serialize).
  public const int BlockVersion = 1;
  // The sea's height plus half a metre: a lake lower than this sits in the sea's own waves [BUILD-SPEC-TARN.md 3.6].
  public const float LowestLevel = 30.5f;

  // The file's first line when Better Continents writes one (Tarn writes this very line [T: Plugin.cs SaveLakes]); a '#' line is a comment to the reader.
  public const string FileHeader = "# Tarn lakes: name;x;z;level;minX;minZ;maxX;maxZ;depth (world metres, absolute heights; x, z = a point in the water)";

  private static readonly CultureInfo C = CultureInfo.InvariantCulture;

  public IReadOnlyList<Lake> Lakes { get; }
  public float DepthScale { get; }
  public int Revision { get; }

  public LakeSet(IReadOnlyList<Lake> lakes, float depthScale, int revision)
  {
    Lakes = lakes;
    DepthScale = depthScale;
    Revision = revision;
  }

  public bool IsEmpty => Lakes.Count == 0;

  // ---- changes: each one a new set, one revision on --------------------------------------------------------------------------------------------

  // The same set with these lakes (and, when given, this depth scale).
  public LakeSet Changed(IReadOnlyList<Lake> lakes, float? depthScale = null) => new(lakes, depthScale ?? DepthScale, Revision + 1);

  public LakeSet WithDepthScale(float depthScale) => new(Lakes, depthScale, Revision + 1);

  public LakeSet Added(Lake lake) => new(Lakes.Append(lake).ToList(), DepthScale, Revision + 1);

  // The set without the lakes of this name ("all" is every one), and how many went.
  public LakeSet Removed(string name, out int removed)
  {
    var kept = name == "all" ? new List<Lake>() : Lakes.Where(l => l.Name != name).ToList();
    removed = Lakes.Count - kept.Count;
    return removed == 0 ? this : new LakeSet(kept, DepthScale, Revision + 1);
  }

  // The lake with the first name "lake<k>" nobody has (Tarn's `here` counter [T: Plugin.cs Here]).
  public string NextName()
  {
    var names = new HashSet<string>(Lakes.Select(l => l.Name));
    int k = 1;
    while (names.Contains("lake" + k))
      k++;
    return "lake" + k;
  }

  // ---- Tarn's text format [T: Plugin.cs Lake.Parse / ToLine / LoadLakes] ----------------------------------------------------------------------
  // One lake a line, nine fields separated by ';': name;x;z;level;minX;minZ;maxX;maxZ;depth. Floats are invariant culture. Lines starting with '#' and
  // blank lines are skipped. Fields past the ninth are ignored. Tarn dropped a line with fewer fields without a word and logged a malformed number;
  // here each of those is a warning that names the line ("line 7: ..."; the caller puts the file's name before it).

  public static LakeSet ParseLines(IEnumerable<string> lines, Action<string> warn)
  {
    var list = new List<Lake>();
    int number = 0;
    foreach (var raw in lines)
    {
      number++;
      if (raw == null)
        continue;
      var text = raw.Trim().TrimStart('﻿').Trim();
      if (text.Length == 0 || text[0] == '#')
        continue;
      if (list.Count >= MaxLakes)
      {
        warn($"line {number}: a world holds at most {MaxLakes:N0} lakes: this line and the ones after it are left out");
        break;
      }
      var p = text.Split(';');
      if (p.Length < 9)
      {
        warn($"line {number}: {p.Length} field{(p.Length == 1 ? "" : "s")} where a lake has 9 (name;x;z;level;minX;minZ;maxX;maxZ;depth): left out");
        continue;
      }
      var v = new float[8];
      string? bad = null;
      for (int i = 0; i < 8 && bad == null; i++)
      {
        if (!float.TryParse(p[i + 1], NumberStyles.Float, C, out v[i]))
          bad = $"field {i + 2} '{p[i + 1].Trim()}' is not a number";
        else if (float.IsNaN(v[i]) || float.IsInfinity(v[i]))
          bad = $"field {i + 2} '{p[i + 1].Trim()}' is not a finite number";
      }
      if (bad != null)
      {
        warn($"line {number}: {bad}: left out");
        continue;
      }
      var name = Clean(p[0]);
      if (name.Length == 0)
      {
        name = "lake" + (list.Count + 1);
        warn($"line {number}: the lake has no name: called '{name}'");
      }
      else if (name.Length > MaxNameLength)
      {
        name = name.Substring(0, MaxNameLength);
        warn($"line {number}: the lake's name is longer than {MaxNameLength} characters: cut to '{name}'");
      }
      var lake = new Lake(name, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
      // Kept, with a word (the map maker may know better).
      if (lake.MaxX <= lake.MinX || lake.MaxZ <= lake.MinZ)
        warn($"line {number}: '{name}' has an empty box ({lake.MinX} {lake.MinZ} to {lake.MaxX} {lake.MaxZ}): the flood has nowhere to go");
      else if (lake.X < lake.MinX || lake.X > lake.MaxX || lake.Z < lake.MinZ || lake.Z > lake.MaxZ)
        warn($"line {number}: '{name}' has its point ({lake.X}, {lake.Z}) outside its box");
      if (lake.Level < LowestLevel)
        warn($"line {number}: '{name}' has its level at {lake.Level} m, under {LowestLevel} m (the sea is at 30 m): the water would sit in the sea's waves");
      list.Add(lake);
    }
    return new LakeSet(list, DefaultDepthScale, 0);
  }

  // A name the text format can carry: no ';' (the separator), no line breaks, no surrounding space.
  public static string Clean(string? name) => (name ?? "").Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();

  // The lake lines again, the numbers in the round-trip form (Tarn's own ToLine wrote three decimals [T: Plugin.cs]; it reads this form too).
  public IEnumerable<string> ToLines() => Lakes.Select(Line);

  // A name that starts with '#' would be read back as a comment: it gets an underscore in front (it can only come from a blob, never from the text reader or `here`).
  public static string Line(Lake l) => string.Join(";", Clean(l.Name) is { Length: > 0 } n && n[0] == '#' ? "_" + n : Clean(l.Name), Num(l.X), Num(l.Z), Num(l.Level), Num(l.MinX), Num(l.MinZ), Num(l.MaxX), Num(l.MaxZ), Num(l.Depth));

  // The file's lines: Tarn's header comment, then the lakes.
  public IEnumerable<string> ToFileLines() => new[] { FileHeader }.Concat(ToLines());

  public static string Num(float v) => v.ToString("R", C);

  public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

  // Whether every number of a lake is a number (a flood over NaN is nonsense, and a blob with one reads as damaged to an older build).
  public static bool IsFinite(Lake l) => Finite(l.X) && Finite(l.Z) && Finite(l.Level) && Finite(l.MinX) && Finite(l.MinZ) && Finite(l.MaxX) && Finite(l.MaxZ) && Finite(l.Depth);

  // A lake that may be added to a world by a command (`here`, `at`): finite numbers, a box with room in it, the point inside the box, a level above the sea's 30 m
  // (a lake lower than that drains), a name the text format can carry. The reason is said when not. (The text reader is gentler: it keeps an odd lake with a warning,
  // because a map maker may know better; a command makes the lake itself, and its numbers are the engine's.)
  public static bool Valid(Lake l, out string why)
  {
    why = "";
    if (!IsFinite(l))
      why = "a number of the lake is not finite";
    else if (l.MaxX <= l.MinX || l.MaxZ <= l.MinZ)
      why = "its box is empty";
    else if (l.X < l.MinX || l.X > l.MaxX || l.Z < l.MinZ || l.Z > l.MaxZ)
      why = "its point is outside its box";
    else if (l.Level < 30f)
      why = $"its level {l.Level} m is under the sea's 30 m";
    else if (string.IsNullOrWhiteSpace(l.Name) || Clean(l.Name).Length > MaxNameLength)
      why = "its name is empty or longer than 64 characters";
    return why.Length == 0;
  }

  // ---- blob 72 ---------------------------------------------------------------------------------------------------------------------------------
  // int version = 1; float depthScale; int count; then per lake: string name (BinaryWriter's: UTF-8, a 7-bit length first); float x, z, level, minX, minZ,
  // maxX, maxZ, depth. The first int32 is the format version, so the settings' BlockVersionOf reads it. Exact floats: the text file's decimals are not kept.

  // A name longer than this many UTF-8 bytes is not a name this reader reads (the longest a lake can have is MaxNameLength characters).
  private const int MaxNameBytes = 1024;

  public byte[] ToBlock()
  {
    if (Lakes.Count > MaxLakes)
      throw new LakeFormatException($"a world holds at most {MaxLakes:N0} lakes, this set has {Lakes.Count:N0}");
    if (!Finite(DepthScale))
      throw new LakeFormatException("the depth scale is not a number");
    // A lake with a number that is not a number is left out, with a word: written, it would make the whole list unreadable at the next start. (The commands never make
    // one: Valid.)
    var written = Lakes.Where(IsFinite).ToList();
    if (written.Count != Lakes.Count)
      BetterContinents.LogWarning($"Lakes: {Lakes.Count - written.Count} lake(s) with a number that is not finite are left out of what is saved: {string.Join(", ", Lakes.Where(l => !IsFinite(l)).Take(3).Select(l => l.Name))}");
    using var ms = new MemoryStream(16 + written.Count * 72);
    using (var w = new BinaryWriter(ms, new UTF8Encoding(false), true))
    {
      w.Write(BlockVersion);
      w.Write(DepthScale);
      w.Write(written.Count);
      foreach (var l in written)
      {
        var name = l.Name ?? "";
        w.Write(name.Length > MaxNameLength ? name.Substring(0, MaxNameLength) : name);
        w.Write(l.X);
        w.Write(l.Z);
        w.Write(l.Level);
        w.Write(l.MinX);
        w.Write(l.MinZ);
        w.Write(l.MaxX);
        w.Write(l.MaxZ);
        w.Write(l.Depth);
      }
    }
    return ms.ToArray();
  }

  public static LakeSet FromBlock(byte[] block) => FromBlock(block, w => BetterContinents.LogWarning("Lakes: " + w));

  // A name as BinaryWriter.Write(string) wrote it (UTF-8, a 7-bit length first), read with a bound on the length, so that a damaged length cannot make a reader allocate
  // gigabytes (Mono's BinaryReader would).
  private static string ReadName(BinaryReader r)
  {
    int length = 0, shift = 0;
    byte b;
    do
    {
      if (shift == 35)
        throw new LakeFormatException("a lake's name has a damaged length");
      b = r.ReadByte();
      length |= (b & 0x7F) << shift;
      shift += 7;
    } while ((b & 0x80) != 0);
    if (length < 0 || length > MaxNameBytes)
      throw new LakeFormatException($"a lake's name says {(uint)length:N0} bytes");
    var bytes = r.ReadBytes(length);
    if (bytes.Length != length)
      throw new EndOfStreamException();
    return new UTF8Encoding(false, true).GetString(bytes);
  }

  // Anything that is wrong with the bytes is a LakeFormatException (so that the settings keep the blob and the world plays without lakes). A lake with a number that is
  // not finite, which a build before this one could write, is left out with a word (warn), and the rest of the list reads.
  public static LakeSet FromBlock(byte[] block, Action<string> warn)
  {
    try
    {
      using var r = new BinaryReader(new MemoryStream(block));
      int version = r.ReadInt32();
      if (version > BlockVersion)
        throw new LakeFormatException($"the lakes are format {version}, made by a newer Better Continents than this one (it reads up to {BlockVersion})");
      if (version < 1)
        throw new LakeFormatException($"the lakes' format number is {version}");
      float scale = r.ReadSingle();
      if (!Finite(scale))
        throw new LakeFormatException("the lakes' depth scale is not a number");
      int count = r.ReadInt32();
      if (count < 0 || count > MaxLakes)
        throw new LakeFormatException($"the lakes' count is {count} (a world holds at most {MaxLakes:N0})");
      var list = new List<Lake>(count);
      var v = new float[8];
      for (int i = 0; i < count; i++)
      {
        var name = ReadName(r);
        for (int k = 0; k < 8; k++)
          v[k] = r.ReadSingle();
        var lake = new Lake(name, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
        if (IsFinite(lake))
          list.Add(lake);
        else
          warn($"lake {i + 1} '{name}' has a number that is not finite: left out");
      }
      if (r.BaseStream.Position != r.BaseStream.Length)
        throw new LakeFormatException($"{r.BaseStream.Length - r.BaseStream.Position} bytes follow the last lake");
      return new LakeSet(list, scale, 0);
    }
    catch (LakeFormatException)
    {
      throw;
    }
    catch (EndOfStreamException)
    {
      throw new LakeFormatException("the lakes end too soon (the blob is cut short)");
    }
    catch (Exception e)
    {
      throw new LakeFormatException("the lakes do not read (" + e.GetType().Name + ": " + e.Message + ")");
    }
  }

  // The first int32 of a blob, or 0 when it has none (the settings' BlockVersionOf).
  public static int VersionOf(byte[] block) => block.Length >= 4 ? BitConverter.ToInt32(block, 0) : 0;
}

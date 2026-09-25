// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterContinents;

public partial class BetterContinents
{
  // How a world's alt biomes are decided. See ALTBIOMES.md.
  public enum AltBiomeMode : byte
  {
    // The game's random placement on unplanted land (tuned by the options below), plus every planted region.
    Random = 0,
    // Only planted regions get alt biomes; the game's random placement is skipped.
    PlantedOnly = 1,
    // No alt biomes anywhere, planted regions included.
    Off = 2,
  }

  // Which part of the world the alt-biome point grid samples.
  public enum AltBiomeGridMode : byte
  {
    // Vanilla: everything within 10500 m of the centre.
    Vanilla = 0,
    // Stop at the world edge (World Size + Edge Size) when that is smaller than the vanilla disc, so alt
    // biomes and biome-point location candidates cannot land beyond the edge of the world.
    WorldEdge = 1,
  }

  // Per alt biome overrides of the game's RANDOM placement, keyed by AltBiome.m_name (never by a display name:
  // the display name is the localised prefix/suffix/override, which is empty for every vanilla entry) or by a
  // wildcard pattern over names. A null field keeps the vanilla value. Planted regions bypass all of this.
  public sealed class AltBiomeOverride
  {
    public bool? Enabled;
    public float? Chance;
    public int? MinAmount;
    public int? MaxAmount;
    public float? MinDistanceFromCenter;
    public int? MinEdgeSize;
    public int? MaxEdgeSize;
    public float? MinAvgHeight;
    public float? MaxAvgHeight;
    public bool? IgnoreWorldBounds;

    // Serialization mask bits. Append new fields with new bits at the end; never reuse a bit.
    private const int BitEnabled = 1 << 0;
    private const int BitChance = 1 << 1;
    private const int BitMinAmount = 1 << 2;
    private const int BitMaxAmount = 1 << 3;
    private const int BitMinDistance = 1 << 4;
    private const int BitMinEdge = 1 << 5;
    private const int BitMaxEdge = 1 << 6;
    private const int BitMinHeight = 1 << 7;
    private const int BitMaxHeight = 1 << 8;
    private const int BitIgnoreBounds = 1 << 9;

    public static readonly string[] FieldNames =
      ["enabled", "chance", "min", "max", "mindist", "minedge", "maxedge", "minheight", "maxheight", "ignorebounds"];

    public bool IsEmpty => Enabled == null && Chance == null && MinAmount == null && MaxAmount == null
                           && MinDistanceFromCenter == null && MinEdgeSize == null && MaxEdgeSize == null
                           && MinAvgHeight == null && MaxAvgHeight == null && IgnoreWorldBounds == null;

    public AltBiomeOverride Clone() => (AltBiomeOverride)MemberwiseClone();

    // Fills every field this entry leaves unset from a lower-precedence entry.
    public void FillFrom(AltBiomeOverride? lower)
    {
      if (lower == null)
        return;
      Enabled ??= lower.Enabled;
      Chance ??= lower.Chance;
      MinAmount ??= lower.MinAmount;
      MaxAmount ??= lower.MaxAmount;
      MinDistanceFromCenter ??= lower.MinDistanceFromCenter;
      MinEdgeSize ??= lower.MinEdgeSize;
      MaxEdgeSize ??= lower.MaxEdgeSize;
      MinAvgHeight ??= lower.MinAvgHeight;
      MaxAvgHeight ??= lower.MaxAvgHeight;
      IgnoreWorldBounds ??= lower.IgnoreWorldBounds;
    }

    public bool TrySet(string field, string value, out string error)
    {
      error = "";
      value = value.Trim();
      bool clear = value == "" || value.Equals("default", StringComparison.OrdinalIgnoreCase);
      switch (field.Trim().ToLowerInvariant())
      {
        case "enabled": return SetBool(ref Enabled, value, clear, out error);
        case "chance": return SetFloat(ref Chance, value, clear, 0f, 1f, out error);
        case "min": return SetInt(ref MinAmount, value, clear, 0, 10000, out error);
        case "max": return SetInt(ref MaxAmount, value, clear, 0, 10000, out error);
        case "mindist": return SetFloat(ref MinDistanceFromCenter, value, clear, 0f, 1000000f, out error);
        case "minedge": return SetInt(ref MinEdgeSize, value, clear, 0, 100000000, out error);
        case "maxedge": return SetInt(ref MaxEdgeSize, value, clear, 0, 100000000, out error);
        case "minheight": return SetFloat(ref MinAvgHeight, value, clear, -100000f, 100000f, out error);
        case "maxheight": return SetFloat(ref MaxAvgHeight, value, clear, -100000f, 100000f, out error);
        case "ignorebounds": return SetBool(ref IgnoreWorldBounds, value, clear, out error);
        default:
          error = $"unknown field '{field}' (valid: {string.Join(", ", FieldNames)})";
          return false;
      }
    }

    private static bool SetBool(ref bool? target, string value, bool clear, out string error)
    {
      error = "";
      if (clear) { target = null; return true; }
      if (bool.TryParse(value, out var b)) { target = b; return true; }
      if (value == "1" || value == "0") { target = value == "1"; return true; }
      error = $"'{value}' is not true/false";
      return false;
    }

    private static bool SetFloat(ref float? target, string value, bool clear, float min, float max, out string error)
    {
      error = "";
      if (clear) { target = null; return true; }
      if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || float.IsNaN(f) || float.IsInfinity(f))
      {
        error = $"'{value}' is not a number";
        return false;
      }
      target = Math.Min(max, Math.Max(min, f));
      return true;
    }

    private static bool SetInt(ref int? target, string value, bool clear, int min, int max, out string error)
    {
      error = "";
      if (clear) { target = null; return true; }
      if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
      {
        error = $"'{value}' is not a whole number";
        return false;
      }
      target = Math.Min(max, Math.Max(min, i));
      return true;
    }

    public override string ToString()
    {
      var parts = new List<string>();
      if (Enabled != null) parts.Add($"enabled={Enabled.Value.ToString().ToLowerInvariant()}");
      if (Chance != null) parts.Add($"chance={Inv(Chance.Value)}");
      if (MinAmount != null) parts.Add($"min={MinAmount.Value}");
      if (MaxAmount != null) parts.Add($"max={MaxAmount.Value}");
      if (MinDistanceFromCenter != null) parts.Add($"mindist={Inv(MinDistanceFromCenter.Value)}");
      if (MinEdgeSize != null) parts.Add($"minedge={MinEdgeSize.Value}");
      if (MaxEdgeSize != null) parts.Add($"maxedge={MaxEdgeSize.Value}");
      if (MinAvgHeight != null) parts.Add($"minheight={Inv(MinAvgHeight.Value)}");
      if (MaxAvgHeight != null) parts.Add($"maxheight={Inv(MaxAvgHeight.Value)}");
      if (IgnoreWorldBounds != null) parts.Add($"ignorebounds={IgnoreWorldBounds.Value.ToString().ToLowerInvariant()}");
      return string.Join(", ", parts);
    }

    // Each entry is written as its own length-prefixed package so that a reader which does not know a
    // later field bit can still skip the whole entry cleanly.
    public byte[] Serialize(string name)
    {
      var pkg = new ZPackage();
      pkg.Write(name);
      int mask = 0;
      if (Enabled != null) mask |= BitEnabled;
      if (Chance != null) mask |= BitChance;
      if (MinAmount != null) mask |= BitMinAmount;
      if (MaxAmount != null) mask |= BitMaxAmount;
      if (MinDistanceFromCenter != null) mask |= BitMinDistance;
      if (MinEdgeSize != null) mask |= BitMinEdge;
      if (MaxEdgeSize != null) mask |= BitMaxEdge;
      if (MinAvgHeight != null) mask |= BitMinHeight;
      if (MaxAvgHeight != null) mask |= BitMaxHeight;
      if (IgnoreWorldBounds != null) mask |= BitIgnoreBounds;
      pkg.Write(mask);
      if (Enabled != null) pkg.Write(Enabled.Value);
      if (Chance != null) pkg.Write(Chance.Value);
      if (MinAmount != null) pkg.Write(MinAmount.Value);
      if (MaxAmount != null) pkg.Write(MaxAmount.Value);
      if (MinDistanceFromCenter != null) pkg.Write(MinDistanceFromCenter.Value);
      if (MinEdgeSize != null) pkg.Write(MinEdgeSize.Value);
      if (MaxEdgeSize != null) pkg.Write(MaxEdgeSize.Value);
      if (MinAvgHeight != null) pkg.Write(MinAvgHeight.Value);
      if (MaxAvgHeight != null) pkg.Write(MaxAvgHeight.Value);
      if (IgnoreWorldBounds != null) pkg.Write(IgnoreWorldBounds.Value);
      return pkg.GetArray();
    }

    public static AltBiomeOverride Deserialize(byte[] data, out string name)
    {
      var pkg = new ZPackage(data);
      name = pkg.ReadString();
      int mask = pkg.ReadInt();
      var o = new AltBiomeOverride();
      if ((mask & BitEnabled) != 0) o.Enabled = pkg.ReadBool();
      if ((mask & BitChance) != 0) o.Chance = pkg.ReadSingle();
      if ((mask & BitMinAmount) != 0) o.MinAmount = pkg.ReadInt();
      if ((mask & BitMaxAmount) != 0) o.MaxAmount = pkg.ReadInt();
      if ((mask & BitMinDistance) != 0) o.MinDistanceFromCenter = pkg.ReadSingle();
      if ((mask & BitMinEdge) != 0) o.MinEdgeSize = pkg.ReadInt();
      if ((mask & BitMaxEdge) != 0) o.MaxEdgeSize = pkg.ReadInt();
      if ((mask & BitMinHeight) != 0) o.MinAvgHeight = pkg.ReadSingle();
      if ((mask & BitMaxHeight) != 0) o.MaxAvgHeight = pkg.ReadSingle();
      if ((mask & BitIgnoreBounds) != 0) o.IgnoreWorldBounds = pkg.ReadBool();
      // Unknown higher bits belong to a newer build; the rest of this entry is simply not read.
      return o;
    }
  }

  // Per-world alt-biome settings, baked into the world's BC settings (DataKey.AltBiomes). A world whose
  // settings carry no AltBiomes key behaves exactly as it did before 0.8.1 (see Legacy).
  public sealed class AltBiomeSettings
  {
    // Blob format. Append new fields at the END of Serialize and read them under a version check; a reader
    // stops at the fields it knows, so older builds ignore anything newer.
    public const int FormatVersion = 1;
    public const string Wildcard = "*";
    public const float VanillaSampleRadius = 10500f;

    public AltBiomeMode Mode = AltBiomeMode.Random;
    public AltBiomeGridMode Grid = AltBiomeGridMode.Vanilla;
    public bool UseFixedSeed;
    public int Seed;
    public bool FixNeighbourCheck;
    public bool MeanSectorHeight;
    public float MinSectorThickness;
    public float ChanceMultiplier = 1f;
    public float AmountMultiplier = 1f;
    public float EdgeScale = 1f;
    public float DistanceScale = 1f;
    // Keys are AltBiome.m_name values or wildcard patterns ("*Mistlands", "*"), compared case-insensitively.
    public readonly Dictionary<string, AltBiomeOverride> Overrides = new(StringComparer.OrdinalIgnoreCase);

    // Shared read-only instance for worlds without the key. Never mutate it; setters materialise their
    // own copy through BetterContinentsSettings.EditAltBiomes().
    public static readonly AltBiomeSettings Legacy = new();

    public AltBiomeSettings Clone()
    {
      var copy = new AltBiomeSettings
      {
        Mode = Mode,
        Grid = Grid,
        UseFixedSeed = UseFixedSeed,
        Seed = Seed,
        FixNeighbourCheck = FixNeighbourCheck,
        MeanSectorHeight = MeanSectorHeight,
        MinSectorThickness = MinSectorThickness,
        ChanceMultiplier = ChanceMultiplier,
        AmountMultiplier = AmountMultiplier,
        EdgeScale = EdgeScale,
        DistanceScale = DistanceScale,
      };
      foreach (var kv in Overrides)
        copy.Overrides[kv.Key] = kv.Value.Clone();
      return copy;
    }

    // True when these settings produce exactly the pre-0.8.1 behaviour for a world whose edge of the world
    // is at worldEdgeRadius. Such settings are not written at all, so a world that does not use the feature
    // saves byte for byte as 0.8.0 did (0.8.0 disables BC for a world whose settings hold an unknown key).
    public bool IsLegacyEquivalent(float worldEdgeRadius) =>
      Mode == AltBiomeMode.Random
      && !UseFixedSeed
      && !FixNeighbourCheck
      && !MeanSectorHeight
      && MinSectorThickness <= 0f
      && ChanceMultiplier == 1f
      && AmountMultiplier == 1f
      && EdgeScale == 1f
      && DistanceScale == 1f
      && Overrides.Count == 0
      && (Grid == AltBiomeGridMode.Vanilla || worldEdgeRadius >= VanillaSampleRadius);

    public static bool IsPattern(string key) => key.IndexOf('*') >= 0;

    // '*' matches any run of characters, everything else is compared case-insensitively.
    public static bool GlobMatches(string pattern, string name)
    {
      pattern ??= "";
      name ??= "";
      int p = 0, n = 0, star = -1, mark = 0;
      while (n < name.Length)
      {
        if (p < pattern.Length && pattern[p] != '*' && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))
        {
          p++;
          n++;
        }
        else if (p < pattern.Length && pattern[p] == '*')
        {
          star = p++;
          mark = n;
        }
        else if (star >= 0)
        {
          p = star + 1;
          n = ++mark;
        }
        else
          return false;
      }
      while (p < pattern.Length && pattern[p] == '*')
        p++;
      return p == pattern.Length;
    }

    // The override that applies to one alt biome, merged field by field: its exact name first, then the
    // wildcard patterns that match it (more literal characters first, then by ordinal order), then "*".
    public AltBiomeOverride? Resolve(string altName)
    {
      if (Overrides.Count == 0)
        return null;
      AltBiomeOverride? merged = null;
      void Add(AltBiomeOverride o)
      {
        if (merged == null)
          merged = o.Clone();
        else
          merged.FillFrom(o);
      }
      if (Overrides.TryGetValue(altName ?? "", out var own) && !IsPattern(altName ?? ""))
        Add(own);
      foreach (var kv in Overrides
                 .Where(kv => kv.Key != Wildcard && IsPattern(kv.Key) && GlobMatches(kv.Key, altName ?? ""))
                 .OrderByDescending(kv => kv.Key.Count(c => c != '*'))
                 .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        Add(kv.Value);
      if (Overrides.TryGetValue(Wildcard, out var all))
        Add(all);
      return merged;
    }

    public byte[] Serialize()
    {
      var pkg = new ZPackage();
      pkg.Write(FormatVersion);
      pkg.Write((byte)Mode);
      pkg.Write((byte)Grid);
      pkg.Write(UseFixedSeed);
      pkg.Write(Seed);
      pkg.Write(FixNeighbourCheck);
      pkg.Write(MeanSectorHeight);
      pkg.Write(MinSectorThickness);
      pkg.Write(ChanceMultiplier);
      pkg.Write(AmountMultiplier);
      pkg.Write(EdgeScale);
      pkg.Write(DistanceScale);
      // Sorted so the same settings always serialise to the same bytes: the package hash is the client-side
      // settings cache id.
      var entries = Overrides.Where(kv => !kv.Value.IsEmpty).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
      pkg.Write(entries.Count);
      foreach (var kv in entries)
        pkg.Write(kv.Value.Serialize(kv.Key));
      return pkg.GetArray();
    }

    public static AltBiomeSettings Deserialize(byte[] data)
    {
      var pkg = new ZPackage(data);
      var s = new AltBiomeSettings();
      int version = pkg.ReadInt();
      if (version < 1)
        throw new Exception($"invalid alt-biome settings format {version}");
      if (version > FormatVersion)
        LogWarning($"Alt-biome settings were written by a newer Better Continents (format {version}); reading the parts this build knows.");
      var mode = pkg.ReadByte();
      s.Mode = Enum.IsDefined(typeof(AltBiomeMode), mode) ? (AltBiomeMode)mode : AltBiomeMode.Random;
      var grid = pkg.ReadByte();
      s.Grid = Enum.IsDefined(typeof(AltBiomeGridMode), grid) ? (AltBiomeGridMode)grid : AltBiomeGridMode.Vanilla;
      s.UseFixedSeed = pkg.ReadBool();
      s.Seed = pkg.ReadInt();
      s.FixNeighbourCheck = pkg.ReadBool();
      s.MeanSectorHeight = pkg.ReadBool();
      s.MinSectorThickness = pkg.ReadSingle();
      s.ChanceMultiplier = pkg.ReadSingle();
      s.AmountMultiplier = pkg.ReadSingle();
      s.EdgeScale = pkg.ReadSingle();
      s.DistanceScale = pkg.ReadSingle();
      int count = pkg.ReadInt();
      for (int i = 0; i < count; i++)
      {
        var o = AltBiomeOverride.Deserialize(pkg.ReadByteArray(), out var name);
        if (!o.IsEmpty)
          s.Overrides[name] = o;
      }
      // Format 2+ fields would be read here, guarded by `if (version >= 2)`.
      return s;
    }

    public static AltBiomeSettings FromConfig() => FromConfig(ConfigValues.Live);

    // From the given config values (a world import reads an export.cfg over a snapshot of the config).
    internal static AltBiomeSettings FromConfig(ConfigValues c)
    {
      var s = new AltBiomeSettings
      {
        Mode = ParseEnum(c.Get(ConfigAltBiomeMode), AltBiomeMode.Random),
        Grid = ParseEnum(c.Get(ConfigAltBiomeGrid), AltBiomeGridMode.WorldEdge),
        FixNeighbourCheck = c.Get(ConfigAltBiomeFixNeighbourCheck),
        MeanSectorHeight = c.Get(ConfigAltBiomeMeanHeight),
        MinSectorThickness = Math.Max(0f, c.Get(ConfigAltBiomeMinThickness)),
        ChanceMultiplier = Math.Max(0f, c.Get(ConfigAltBiomeChanceMultiplier)),
        AmountMultiplier = Math.Max(0f, c.Get(ConfigAltBiomeAmountMultiplier)),
        EdgeScale = Math.Max(0.01f, c.Get(ConfigAltBiomeEdgeScale)),
        DistanceScale = Math.Max(0f, c.Get(ConfigAltBiomeDistanceScale)),
      };
      if (TryParseSeed(c.Get(ConfigAltBiomeSeed), out var seed))
      {
        s.UseFixedSeed = true;
        s.Seed = seed;
      }
      if (!TryParseOverrides(c.Get(ConfigAltBiomeOverrides), s.Overrides, out var error))
        LogError($"Alt Biomes / Overrides config: {error}");
      return s;
    }

    // Empty text or "world" means "use the world seed". A number is used as is; any other text is hashed the
    // same way Valheim hashes a world seed name.
    public static bool TryParseSeed(string text, out int seed)
    {
      seed = 0;
      text = (text ?? "").Trim();
      if (text == "" || text.Equals("world", StringComparison.OrdinalIgnoreCase))
        return false;
      if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
        seed = text.GetStableHashCode();
      return true;
    }

    public static T ParseEnum<T>(string text, T fallback) where T : struct =>
      Enum.TryParse<T>((text ?? "").Trim(), true, out var value) && Enum.IsDefined(typeof(T), value) ? value : fallback;

    // "Name: key=value, key=value; Other Name: key=value". A name may contain * wildcards; "*" alone applies to
    // every alt biome. Names are AltBiome.m_name, compared case-insensitively.
    public static bool TryParseOverrides(string text, Dictionary<string, AltBiomeOverride> into, out string error)
    {
      error = "";
      if (string.IsNullOrWhiteSpace(text))
        return true;
      var errors = new List<string>();
      foreach (var rawEntry in text.Split(';'))
      {
        var entry = rawEntry.Trim();
        if (entry == "") continue;
        var colon = entry.IndexOf(':');
        if (colon <= 0)
        {
          errors.Add($"'{entry}' has no 'Name:' prefix");
          continue;
        }
        var name = entry.Substring(0, colon).Trim();
        if (!into.TryGetValue(name, out var o))
          o = new AltBiomeOverride();
        foreach (var rawPair in entry.Substring(colon + 1).Split(','))
        {
          var pair = rawPair.Trim();
          if (pair == "") continue;
          var eq = pair.IndexOf('=');
          if (eq <= 0)
          {
            errors.Add($"'{pair}' in '{name}' is not key=value");
            continue;
          }
          if (!o.TrySet(pair.Substring(0, eq), pair.Substring(eq + 1), out var fieldError))
            errors.Add($"{name}: {fieldError}");
        }
        if (o.IsEmpty) into.Remove(name);
        else into[name] = o;
      }
      error = string.Join("; ", errors);
      return errors.Count == 0;
    }

    public string FormatOverrides() =>
      string.Join("; ", Overrides.Where(kv => !kv.Value.IsEmpty)
        .OrderBy(kv => kv.Key == Wildcard ? "" : kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => $"{kv.Key}: {kv.Value}"));

    public string SeedText => UseFixedSeed ? Seed.ToString(CultureInfo.InvariantCulture) : "world seed";

    public void Dump(Action<string> output, bool isLegacyDefault)
    {
      if (isLegacyDefault)
      {
        output("Alt biomes: legacy defaults (mode Random, grid Vanilla; no alt-biome settings stored in this world)");
        return;
      }
      output($"Alt biomes: mode {Mode}, grid {Grid}, placement seed {SeedText}");
      output($"Alt biomes: chance x{Inv(ChanceMultiplier)}, amount x{Inv(AmountMultiplier)}, region size x{Inv(EdgeScale)}, distance x{Inv(DistanceScale)}");
      output($"Alt biomes: neighbour fix {(FixNeighbourCheck ? "on" : "off")}, mean sector height {(MeanSectorHeight ? "on" : "off")}, min sector thickness {Inv(MinSectorThickness)}");
      if (Overrides.Count > 0)
        output($"Alt biome overrides: {FormatOverrides()}");
    }
  }

  private static string Inv(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

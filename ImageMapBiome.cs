// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

internal class ImageMapBiome() : ImageMapBase
{
    public static ImageMapBiome? Create(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        // The biomes Expand World Data defines by now (it loads its names after Better Continents' Start), for
        // anything that samples this map before the world loads, such as the New World preview.
        RefreshUsableBiomes();
        ImageMapBiome map = new()
        {
            FilePath = path
        };
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap())
            return null;
        return map;
    }
    public static ImageMapBiome? Create(byte[] data)
    {
        var decode = new Heightmap.Biome[256];
        for (int value = 0; value < decode.Length; value++)
            decode[value] = ByteToBiome((byte)value);
        var biomes = new Heightmap.Biome[data.Length];
        var counts = new long[decode.Length];
        for (int i = 0; i < data.Length; i++)
        {
            counts[data[i]]++;
            biomes[i] = decode[data[i]];
        }
        var present = Heightmap.Biome.None;
        for (int value = 0; value < counts.Length; value++)
            if (counts[value] > 0)
                present |= decode[value];
        ImageMapBiome map = new()
        {
            Map = biomes,
            Present = present,
            Size = (int)Math.Sqrt(data.Length)
        };
        LogSummary(counts, data.Length);
        return map;
    }
    public static ImageMapBiome? Create(byte[] data, string colors, string path)
    {
        var legend = ParseLegend(colors, $"Biome colors of {path}");
        ImageMapBiome map = new()
        {
            SourceData = data,
            FilePath = path,
            Colors = legend.Colors,
            Unresolved = legend.Unresolved,
            LegendErrors = legend.Errors
        };
        if (!map.CreateMap())
            return null;
        return map;
    }
    // Valheim 1.0 converts biomes to indices and back (BiomeHelpers.ToBiomeIndex / ToBiome) with switches that throw
    // for anything that is not one of the ten known values, so combined or unused values must never reach the game.
    // Expand World Data patches both conversions to cover every single flag bit, and gives the biomes it adds (its
    // expand_biomes yaml) the bits after Mistlands: 0x400 upward, then 0x80. Their indices run past
    // BiomeIndex.Count, so anything indexed by biome is sized by BiomeIndexCount, not by BiomeIndex.Count.
    // RefreshBiomeTable reads what the game accepts from those two conversions themselves.
    private sealed class BiomeTable(Heightmap.Biome[] indexToBiome, Heightmap.Biome supported)
    {
        // Index -> biome, None where no biome has that index.
        public readonly Heightmap.Biome[] IndexToBiome = indexToBiome;
        // Every single-bit biome the game can index.
        public readonly Heightmap.Biome Supported = supported;
    }
    private static readonly Heightmap.Biome VanillaBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.Swamp | Heightmap.Biome.Mountain
        | Heightmap.Biome.BlackForest | Heightmap.Biome.Plains | Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth
        | Heightmap.Biome.Ocean | Heightmap.Biome.Mistlands;
    // One immutable table, swapped whole: the terrain builder thread reads it while the main thread refreshes it.
    private static volatile BiomeTable Table = new(
        [.. Enumerable.Range(0, (int)Heightmap.BiomeIndex.Count).Select(i => ((Heightmap.BiomeIndex)i).ToBiome())], VanillaBiomes);

    private static bool IsSingleBit(Heightmap.Biome biome)
    {
        var bits = (uint)biome;
        return bits != 0 && (bits & (bits - 1)) == 0;
    }
    public static bool IsVanillaBiome(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (VanillaBiomes & biome) != 0);
    // A biome the game can index (None or one flag bit the conversions accept); IsUsableBiome says whether the world
    // can use it now.
    public static bool IsValidBiome(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (Table.Supported & biome) != 0);
    // Same as the game's ToIndex extension, but without throwing on unknown values.
    public static int ToSafeIndex(Heightmap.Biome biome) => IsValidBiome(biome) ? biome.ToIndex() : 0;
    // The size of anything indexed by ToSafeIndex: BiomeIndex.Count in vanilla, 33 with Expand World Data.
    public static int BiomeIndexCount => Table.IndexToBiome.Length;
    // The biome of an index below BiomeIndexCount; None for any other index.
    public static Heightmap.Biome ToSafeBiome(int index)
    {
        var table = Table.IndexToBiome;
        return index >= 0 && index < table.Length ? table[index] : Heightmap.Biome.None;
    }
    // Biomes beyond vanilla the game can index (Expand World Data's), None without them.
    public static Heightmap.Biome ExtraBiomes => Table.Supported & ~VanillaBiomes;

    // Biomes the world can use now. Indexing is not enough: the game's alt-biome grid (AltBiomeWorldData) gets a
    // BiomeTypeInfo only for the values Enum.GetValues gives, which with Expand World Data are the biomes its yaml
    // names at that moment, and any other value in the grid throws KeyNotFoundException, so the world never loads. A
    // map keeps whatever it holds, so nothing is lost while Expand World Data is missing or its yaml changes;
    // GetValue reads the rest as None, where the default generation decides. Refreshed at Start and before every
    // VerifyBiomeData, which builds that grid (world load, and Expand World Data's regeneration after a yaml change).
    private static volatile Heightmap.Biome Usable = VanillaBiomes;
    public static bool IsUsableBiome(Heightmap.Biome biome) => biome == Heightmap.Biome.None || (IsSingleBit(biome) && (Usable & biome) != 0);
    internal static void RefreshUsableBiomes()
    {
        var usable = VanillaBiomes;
        try
        {
            foreach (var value in Enum.GetValues(typeof(Heightmap.Biome)))
                if (value is Heightmap.Biome biome && IsSingleBit(biome) && IsValidBiome(biome))
                    usable |= biome;
        }
        catch (Exception e)
        {
            BetterContinents.LogWarning($"Cannot list the game's biomes ({e.Message}); biome maps use the vanilla biomes only.");
        }
        Usable = usable;
    }
    // The world's map holds biomes the world cannot use now: said once per combination, before the grid is built.
    private static Heightmap.Biome LastUnusable;
    internal void WarnUnusable()
    {
        var unusable = Present & ~Usable & ~Heightmap.Biome.None;
        if (unusable == LastUnusable)
            return;
        LastUnusable = unusable;
        if (unusable == Heightmap.Biome.None)
            return;
        var names = Enumerable.Range(0, 32).Select(bit => (Heightmap.Biome)(int)(1u << bit)).Where(b => (unusable & b) != 0)
            .Select(b => $"{BiomeName(b)} (0x{(uint)b:X})");
        BetterContinents.LogWarning($"The biome map holds biomes this game does not have now: {string.Join(", ", names)}. " +
            "The default generation decides there until they exist again; the map keeps them. Biomes beyond vanilla come from Expand World Data: " +
            "install it wherever the world is played, with the same expand_biomes yaml (it numbers the biomes in the order the yaml lists them).");
    }

    // One log line per loaded biome map: each biome's share, so a log shows what the world's map holds (by byte:
    // 0 = None, n = flag bit n - 1).
    private static void LogSummary(long[] counts, long total)
    {
        if (total <= 0)
            return;
        var parts = new List<string>();
        for (int value = 1; value <= 32; value++)
            if (counts[value] > 0)
            {
                var biome = (Heightmap.Biome)(int)(1u << (value - 1));
                var name = IsVanillaBiome(biome) ? BiomeName(biome) : $"{BiomeName(biome)} (0x{(uint)biome:X})";
                parts.Add($"{name} {(100.0 * counts[value] / total).ToString("0.00", CultureInfo.InvariantCulture)}%");
            }
        long none = counts[0] + Enumerable.Range(33, counts.Length - 33).Sum(value => counts[value]);
        if (none > 0)
            parts.Add($"None {(100.0 * none / total).ToString("0.00", CultureInfo.InvariantCulture)}%");
        BetterContinents.Log($"Biome map ({(int)Math.Sqrt(total)} x {(int)Math.Sqrt(total)}): {string.Join(", ", parts)}");
    }

    // At Start, once every mod has applied its patches in Awake: asks the game's own conversions which further flag
    // bits have an index that converts back to the same biome. Vanilla throws for each; Expand World Data answers for
    // all of them.
    internal static void RefreshBiomeTable()
    {
        var byIndex = new Dictionary<int, Heightmap.Biome>();
        for (int i = 0; i < (int)Heightmap.BiomeIndex.Count; i++)
            byIndex[i] = ((Heightmap.BiomeIndex)i).ToBiome();
        var supported = VanillaBiomes;
        for (int bit = 0; bit < 32; bit++)
        {
            var biome = (Heightmap.Biome)(int)(1u << bit);
            if ((VanillaBiomes & biome) != 0)
                continue;
            int index;
            try
            {
                index = (int)biome.ToBiomeIndex();
                if (index <= 0 || byIndex.ContainsKey(index) || ((Heightmap.BiomeIndex)index).ToBiome() != biome)
                    continue;
            }
            catch (Exception)
            {
                continue;
            }
            byIndex[index] = biome;
            supported |= biome;
        }
        var indexToBiome = new Heightmap.Biome[byIndex.Keys.Max() + 1];
        foreach (var kv in byIndex)
            indexToBiome[kv.Key] = kv.Value;
        Table = new BiomeTable(indexToBiome, supported);
        BetterContinents.RefreshPlainSectors();
        RefreshUsableBiomes();
        if (ExtraBiomes != Heightmap.Biome.None)
            BetterContinents.Log($"Biome maps accept {byIndex.Count - (int)Heightmap.BiomeIndex.Count} biomes beyond vanilla (Expand World Data's), indices up to {indexToBiome.Length - 1}.");
    }

    // Names in a biome map's legend (biomemap.txt), any case: the game's biome names, the names Expand World Data gives
    // the biomes it adds (its expand_biomes yaml), or a number, which is how Enum.ToString writes an added biome.
    // The result must be one biome the game can index: Enum.TryParse also returns combinations like "All".
    internal static bool TryParseBiome(string name, out Heightmap.Biome biome)
    {
        name = name.Trim();
        // With Expand World Data this also finds its biomes, as it patches Enum.TryParse for biomes (then only names
        // parse); EWD.TryGetBiome asks it directly in case that patch does not reach this call.
        if (Enum.TryParse(name, true, out biome) && IsValidBiome(biome))
            return true;
        if (EWD.TryGetBiome(name, out biome) && IsValidBiome(biome))
            return true;
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && IsValidBiome((Heightmap.Biome)value))
        {
            biome = (Heightmap.Biome)value;
            return true;
        }
        // Loosely: "Black Forest", "deep_north", "Ash-Lands" (the game's own spelling has spaces).
        var loose = new string([.. name.Where(ch => ch != ' ' && ch != '_' && ch != '-')]);
        if (loose.Length > 0 && loose != name && TryParseBiome(loose, out biome))
            return true;
        biome = Heightmap.Biome.None;
        return false;
    }
    // The legend name TryParseBiome reads back: the enum name for a vanilla biome, Expand World Data's name for one it
    // added, else the number.
    internal static string BiomeName(Heightmap.Biome biome)
    {
        if (IsVanillaBiome(biome))
            return biome.ToString();
        return EWD.TryGetName(biome, out var name) ? name : ((int)biome).ToString(CultureInfo.InvariantCulture);
    }
    // BiomeName, but a number for every biome beyond vanilla: for legends read back before Expand World Data may have
    // its names (a client receiving a world's settings from the server).
    private static string StableBiomeName(Heightmap.Biome biome) =>
        IsVanillaBiome(biome) ? biome.ToString() : ((int)biome).ToString(CultureInfo.InvariantCulture);

    // Biomes are stored one byte per pixel as the bit index of the flag plus one (0 = None, 1 = Meadows, ...,
    // 10 = Mistlands, 11 = the first biome Expand World Data adds, up to 32), and read back as that bit whether or not
    // this game has the biome now (see Usable), so a save never drops one.
    private static Heightmap.Biome ByteToBiome(byte value) =>
        value >= 1 && value <= 32 ? (Heightmap.Biome)(int)(1u << (value - 1)) : Heightmap.Biome.None;
    // The bit index of a single bit, by de Bruijn multiplication (no loop per pixel of a 67-million-pixel map).
    private static readonly byte[] DeBruijnBit =
        [0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9];
    private static byte BiomeToByte(Heightmap.Biome biome) =>
        IsSingleBit(biome) ? (byte)(DeBruijnBit[unchecked((uint)biome * 0x077CB531u) >> 27] + 1) : (byte)0;
    public byte[] Serialize() => [.. Map.Select(BiomeToByte)];

    private Heightmap.Biome[] Map = [];
    // Every biome in Map.
    private Heightmap.Biome Present;
    private Dictionary<Heightmap.Biome, Color32> Colors = [];
    // The legend entries that were skipped because their name is no biome here: their colour's pixels read as None,
    // the default generation deciding there, rather than as whichever biome's colour is nearest. A world export
    // writes them back as they were, so a game that has the biome reads them again.
    private List<(string Name, Color32 Color)> Unresolved = [];
    internal IReadOnlyList<(string Name, Color32 Color)> UnresolvedLegend => Unresolved;
    // Legend entries skipped when the legend was read: a reload keeps the world's current map when there are any.
    internal int LegendErrors { get; private set; }
    // World export (WorldExport): the decoded map (row 0 = south, like every map after loading) and the legend
    // colours it was decoded with (empty for a map read back from a world's settings, which stores biomes only).
    internal Heightmap.Biome[] Biomes => Map;
    internal IReadOnlyDictionary<Heightmap.Biome, Color32> LegendColors => Colors;
    public override bool LoadSourceImage()
    {
        if (!base.LoadSourceImage()) return false;
        var path = Legends.FileFor(FilePath);
        if (!File.Exists(path))
        {
            Legends.WriteDefault(path, DefaultColors);
            Colors = ParseColors(DefaultColors);
            return true;
        }
        try
        {
            var legend = ParseLegend(Legends.ReadJoined(path), path);
            Colors = legend.Colors;
            Unresolved = legend.Unresolved;
            LegendErrors = legend.Errors;
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Cannot load file {path}: {ex.Message}.");
            Colors = ParseColors(DefaultColors);
            Unresolved = [];
            LegendErrors = 1;
        }
        return true;
    }

    // "Name: colour" entries separated by '|' (the biome row of Legends' table); a line starting with '#' is a comment.
    // An entry whose name is not a biome the game can use here, or whose colour does not parse, is reported and
    // skipped, and the others still apply (before 0.9.3 one such entry discarded the whole legend for the default
    // colours).
    private sealed class Legend
    {
        public readonly Dictionary<Heightmap.Biome, Color32> Colors = [];
        public readonly List<(string Name, Color32 Color)> Unresolved = [];
        public int Errors;
    }
    private static Dictionary<Heightmap.Biome, Color32> ParseColors(string colors, string? source = null) => ParseLegend(colors, source).Colors;
    private static Legend ParseLegend(string colors, string? source)
    {
        var result = new Legend();
        foreach (var entry in colors.Split('|'))
        {
            if (Legends.IsBlankOrComment(entry))
                continue;
            var line = entry.Trim();
            var s = line.Split(':');
            if (s.Length != 2)
            {
                BetterContinents.LogError($"{source ?? "Biome colors"}: \"{line}\" is not \"name: colour\" (start a note with #), skipped.");
                result.Errors++;
                continue;
            }
            if (!Legends.TryParseColor32(s[1], out var color))
            {
                // A vanilla biome keeps its default colour, so a picture in the default colours still decodes right.
                if (TryParseBiome(s[0], out var named) && IsVanillaBiome(named) && DefaultColorTable().TryGetValue(named, out var fallback))
                {
                    BetterContinents.LogError($"{source ?? "Biome colors"}: invalid colour {s[1].Trim()} for {s[0].Trim()} (a hex colour like 8B4513, or r,g,b): its default colour is used.");
                    result.Errors++;
                    result.Colors[named] = fallback;
                    continue;
                }
                BetterContinents.LogError($"{source ?? "Biome colors"}: invalid colour {s[1].Trim()} for {s[0].Trim()} (a hex colour like 8B4513, or r,g,b), skipped.");
                result.Errors++;
                continue;
            }
            if (!TryParseBiome(s[0], out var biome))
            {
                BetterContinents.LogError($"{source ?? "Biome colors"}: invalid biome name {s[0].Trim()}, skipped: its colour reads as None. {BiomeNameHelp()}");
                result.Unresolved.Add((s[0].Trim(), color));
                result.Errors++;
                continue;
            }
            if (result.Colors.ContainsKey(biome))
                BetterContinents.LogWarning($"{source ?? "Biome colors"}: {s[0].Trim()} is listed more than once; its last colour is used.");
            result.Colors[biome] = color;
        }
        return result;
    }
    private static string BiomeNameHelp()
    {
        var names = string.Join(", ", VanillaNames);
        if (ExtraBiomes == Heightmap.Biome.None)
            return $"The biomes are {names}; the biomes Expand World Data adds need Expand World Data installed.";
        return $"The biomes are {names}, and the biomes Expand World Data adds, by the name in its expand_biomes yaml.";
    }
    private static readonly string[] VanillaNames = [.. Enumerable.Range(0, (int)Heightmap.BiomeIndex.Count).Select(i => ((Heightmap.BiomeIndex)i).ToBiome().ToString())];

    // Internal for WorldExport, which writes biomemap.png in exactly these colours and this legend.
    internal static readonly string DefaultColors = "None: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF";
    internal static Dictionary<Heightmap.Biome, Color32> DefaultColorTable() => ParseColors(DefaultColors);

    // World export: a colour for every biome index, so a written biome map tells every biome apart. Vanilla's biomes
    // keep the default legend's colours; the biomes Expand World Data adds (indices 10 to 32) get these, chosen
    // greedily for the largest distance from the default colours and from each other (at least 85 apart in RGB).
    private static readonly uint[] AddedBiomeColors =
    [
        0xFF00FF, 0x004488, 0x4477FF, 0x44FF77, 0x770044, 0xFF4477, 0x8811CC, 0xCC88EE, 0xCCEE77, 0x88EEDD, 0x88FF11, 0xFF8811,
        0x00AABB, 0x00BB55, 0xBB4433, 0x445544, 0xEE9999, 0x3300AA, 0xDD00AA, 0x55BB33, 0x110055, 0xBB9944, 0x00FFAA,
    ];
    private static Color32 AddedBiomeColor(int n)
    {
        var c = AddedBiomeColors[n % AddedBiomeColors.Length];
        return new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
    }
    // The export colour of a biome beyond vanilla, by its flag bit in Expand World Data's order (0x80 is index 10,
    // 0x400 to 0x80000000 are 11 to 32), so it does not depend on the game being able to index it here.
    private static Color32 AddedExportColor(Heightmap.Biome biome)
    {
        int bit = BiomeToByte(biome) - 1;
        return AddedBiomeColor(bit == 7 ? 0 : bit - 9);
    }
    internal static Dictionary<Heightmap.Biome, Color32> ExportColorTable()
    {
        var table = DefaultColorTable();
        for (int i = (int)Heightmap.BiomeIndex.Count; i < BiomeIndexCount; i++)
            if (ToSafeBiome(i) is var biome && biome != Heightmap.Biome.None)
                table[biome] = AddedExportColor(biome);
        return table;
    }
    // World export of a map's own legend (the default one for a map read back from a world's settings, which stores
    // biomes only), plus a colour for each biome the map holds that the legend lacks (biomes beyond vanilla, even one
    // this game cannot use now), in its export colour unless another entry already has that.
    internal static Dictionary<Heightmap.Biome, Color32> ExportLegend(IReadOnlyDictionary<Heightmap.Biome, Color32> legend, Heightmap.Biome[] map)
    {
        var colours = legend.Count > 0 ? legend.ToDictionary(kv => kv.Key, kv => kv.Value) : DefaultColorTable();
        var used = new HashSet<Heightmap.Biome>();
        foreach (var biome in map)
            used.Add(biome);
        foreach (var biome in used.Where(b => IsSingleBit(b) && !IsVanillaBiome(b) && !colours.ContainsKey(b)).OrderBy(b => (uint)b).ToList())
        {
            bool Free(Color32 c) => !colours.Values.Any(o => o.r == c.r && o.g == c.g && o.b == c.b);
            var own = AddedExportColor(biome);
            var candidates = new[] { own }.Concat(Enumerable.Range(0, AddedBiomeColors.Length).Select(AddedBiomeColor));
            colours[biome] = candidates.Where(Free).DefaultIfEmpty(own).First();
        }
        return colours;
    }
    // "Name: RRGGBB" legend lines, the default legend's order first.
    internal static IEnumerable<string> LegendLines(IReadOnlyDictionary<Heightmap.Biome, Color32> colours) =>
        colours.OrderBy(kv => IsVanillaBiome(kv.Key) ? Array.IndexOf(DefaultOrder, kv.Key) : 100L + (uint)kv.Key)
            .Select(kv => $"{BiomeName(kv.Key)}: {kv.Value.r:X2}{kv.Value.g:X2}{kv.Value.b:X2}");
    private static readonly Heightmap.Biome[] DefaultOrder = [.. DefaultColorTable().Keys];
    public bool CreateMap() => CreateMap<Rgba32>();
    protected override bool LoadTextureToMap<T>(Image<T> image)
    {
        if (Colors.Count == 0)
        {
            BetterContinents.LogError($"No biome colors defined for image {FilePath}.");
            Colors = ParseColors(DefaultColors);
            // A legend with nothing in it is an error too: a reload keeps the world's map.
            LegendErrors = Math.Max(LegendErrors, 1);
        }
        static int ColorDistance(Color32 a, Color32 b) =>
            (a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b);

        var st = new Stopwatch();
        st.Start();

        var colorMapping = new Dictionary<Color32, Heightmap.Biome>(new Color32Comparer());
        // The legend's colours, then those of skipped entries, which read as None.
        var candidates = Colors.Select(d => (Biome: d.Key, Color: d.Value))
            .Concat(Unresolved.Select(u => (Biome: Heightmap.Biome.None, u.Color))).ToList();
        var img = (Image<Rgba32>)(Image)image;
        Map = LoadPixels(img, pixel =>
        {
            var color = Convert(pixel);
            if (!colorMapping.TryGetValue(color, out var biome))
            {
                biome = candidates.OrderBy(d => ColorDistance(color, d.Color)).First().Biome;
                colorMapping.Add(color, biome);
            }
            return biome;
        });
        Present = colorMapping.Values.Aggregate(Heightmap.Biome.None, (all, biome) => all | biome);
        var counts = new long[256];
        foreach (var biome in Map)
            counts[BiomeToByte(biome)]++;
        LogSummary(counts, Map.Length);

        BetterContinents.Log($"Time to calculate biomes from {FilePath}: {st.ElapsedMilliseconds} ms");
        return true;
    }

    public Heightmap.Biome GetValue(float x, float y)
    {
        int topBiomeIdx = 0;
        int numBiomes = 0;
        float xa = x * (Size - 1);
        float ya = y * (Size - 1);

        int xi = Mathf.FloorToInt(xa);
        int yi = Mathf.FloorToInt(ya);

        float xd = xa - xi;
        float yd = ya - yi;

        // On the stack: GetBiome samples this for every grid point, zone corner and map pixel, from several
        // threads at once, and two small arrays per call were millions of garbage allocations per pass.
        Span<Heightmap.Biome> biomes = stackalloc Heightmap.Biome[4];
        Span<float> biomeWeights = stackalloc float[4];
        // A biome the world cannot use now reads as None (see Usable).
        var usable = Usable;
        SampleBiomeWeighted(xi + 0, yi + 0, (1 - xd) * (1 - yd), usable, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(xi + 1, yi + 0, xd * (1 - yd), usable, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(xi + 0, yi + 1, (1 - xd) * yd, usable, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(xi + 1, yi + 1, xd * yd, usable, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);

        return biomes[topBiomeIdx];
    }

    private void SampleBiomeWeighted(int xs, int ys, float weight, Heightmap.Biome usable, Span<float> biomeWeights, Span<Heightmap.Biome> biomes, ref int numBiomes, ref int topBiomeIdx)
    {
        var biome = Map[Mathf.Clamp(ys, 0, Size - 1) * Size + Mathf.Clamp(xs, 0, Size - 1)] & usable;
        int i = 0;
        for (; i < numBiomes; ++i)
        {
            if (biomes[i] == biome)
            {
                if (biomeWeights[i] + weight > biomeWeights[topBiomeIdx])
                    topBiomeIdx = i;
                biomeWeights[i] += weight;
                return;
            }
        }

        if (i == numBiomes)
        {
            if (biomeWeights[numBiomes] + weight > biomeWeights[topBiomeIdx])
                topBiomeIdx = numBiomes;
            biomes[numBiomes] = biome;
            biomeWeights[numBiomes++] = weight;
        }
    }

    public override void SerializeLegacy(ZPackage pkg, int version, bool network)
    {
        base.SerializeLegacy(pkg, version, network);
        if (version >= 8)
        {
            // StableBiomeName: the receiving client may not have Expand World Data's biome names yet. Skipped entries go
            // too, under their own names, so their colour reads as None on the other side as well.
            var colors = Colors.Select(d => $"{StableBiomeName(d.Key)}:{d.Value.r},{d.Value.g},{d.Value.b},{d.Value.a}")
                .Concat(Unresolved.Select(u => $"{u.Name}:{u.Color.r},{u.Color.g},{u.Color.b},{u.Color.a}"));
            pkg.Write(string.Join("|", colors));
        }
    }
    public static ImageMapBiome? LoadLegacy(ZPackage pkg, int version)
    {
        var path = pkg.ReadString();
        if (string.IsNullOrEmpty(path))
            return null;
        var data = pkg.ReadByteArray();
        var colors = version >= 8 ? pkg.ReadString() : DefaultColors;
        return Create(data, colors, path);
    }
}

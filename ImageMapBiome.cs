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
        BiomeRegistry.RefreshUsable();
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
            decode[value] = BiomeRegistry.FromByte((byte)value);
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
    // The world's map holds biomes the world cannot use now: said once per combination, before the grid is built.
    private static Heightmap.Biome LastUnusable;
    internal void WarnUnusable()
    {
        var unusable = Present & ~BiomeRegistry.Usable & ~Heightmap.Biome.None;
        if (unusable == LastUnusable)
            return;
        LastUnusable = unusable;
        if (unusable == Heightmap.Biome.None)
            return;
        var names = Enumerable.Range(0, 32).Select(bit => (Heightmap.Biome)(int)(1u << bit)).Where(b => (unusable & b) != 0)
            .Select(b => $"{BiomeRegistry.Name(b)} (0x{(uint)b:X})");
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
                var name = BiomeRegistry.IsVanilla(biome) ? BiomeRegistry.Name(biome) : $"{BiomeRegistry.Name(biome)} (0x{(uint)biome:X})";
                parts.Add($"{name} {(100.0 * counts[value] / total).ToString("0.00", CultureInfo.InvariantCulture)}%");
            }
        long none = counts[0] + Enumerable.Range(33, counts.Length - 33).Sum(value => counts[value]);
        if (none > 0)
            parts.Add($"None {(100.0 * none / total).ToString("0.00", CultureInfo.InvariantCulture)}%");
        BetterContinents.Log($"Biome map ({(int)Math.Sqrt(total)} x {(int)Math.Sqrt(total)}): {string.Join(", ", parts)}");
    }

    public byte[] Serialize() => [.. Map.Select(BiomeRegistry.ToByte)];

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
                if (BiomeRegistry.TryParse(s[0], out var named) && BiomeRegistry.IsVanilla(named) && DefaultColorTable().TryGetValue(named, out var fallback))
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
            if (!BiomeRegistry.TryParse(s[0], out var biome))
            {
                BetterContinents.LogError($"{source ?? "Biome colors"}: invalid biome name {s[0].Trim()}, skipped: its colour reads as None. {BiomeRegistry.NameHelp()}");
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
        int bit = BiomeRegistry.ToByte(biome) - 1;
        return AddedBiomeColor(bit == 7 ? 0 : bit - 9);
    }
    internal static Dictionary<Heightmap.Biome, Color32> ExportColorTable()
    {
        var table = DefaultColorTable();
        for (int i = (int)Heightmap.BiomeIndex.Count; i < BiomeRegistry.IndexCount; i++)
            if (BiomeRegistry.ToSafeBiome(i) is var biome && biome != Heightmap.Biome.None)
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
        foreach (var biome in used.Where(b => BiomeRegistry.IsSingleBit(b) && !BiomeRegistry.IsVanilla(b) && !colours.ContainsKey(b)).OrderBy(b => (uint)b).ToList())
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
        colours.OrderBy(kv => BiomeRegistry.IsVanilla(kv.Key) ? Array.IndexOf(DefaultOrder, kv.Key) : 100L + (uint)kv.Key)
            .Select(kv => $"{BiomeRegistry.Name(kv.Key)}: {kv.Value.r:X2}{kv.Value.g:X2}{kv.Value.b:X2}");
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
            counts[BiomeRegistry.ToByte(biome)]++;
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
        // A biome the world cannot use now reads as None (see BiomeRegistry.Usable).
        var usable = BiomeRegistry.Usable;
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
            // BiomeRegistry.StableName: the receiving client may not have Expand World Data's biome names yet. Skipped entries go
            // too, under their own names, so their colour reads as None on the other side as well.
            var colors = Colors.Select(d => $"{BiomeRegistry.StableName(d.Key)}:{d.Value.r},{d.Value.g},{d.Value.b},{d.Value.a}")
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

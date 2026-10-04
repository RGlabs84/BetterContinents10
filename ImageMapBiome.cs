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
    public static ImageMapBiome? Create(string path, bool compact = false)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        // The biomes Expand World Data defines by now (it loads its names after Better Continents' Start), for
        // anything that samples this map before the world loads, such as the New World preview.
        BiomeRegistry.RefreshUsable();
        ImageMapBiome map = new()
        {
            FilePath = path,
            Compact = compact
        };
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap())
            return null;
        return map;
    }
    // From the bytes a world made before 0.10 saved (one per pixel, row by row). A byte that is no biome reads as None,
    // and is saved back as 0, as always.
    public static ImageMapBiome? Create(byte[] data)
    {
        int size = (int)Math.Sqrt(data.Length);
        var counts = new long[256];
        foreach (var b in data)
            counts[Stored(b)]++;
        ImageMapBiome map = new()
        {
            grid = size > 0 ? ByteGrid.FromBytes(size, data, Stored, compact: false) : null,
            tail = [.. data.Skip(size * size).Select(Stored)],
            counts = counts,
            Size = size
        };
        map.Present = map.PresentFrom(Identity);
        LogSummary(counts, data.Length);
        return map;
    }

    // A byte as the map stores it: its biome's byte (BiomeRegistry.ToByte(FromByte(b))), 0 for one that is no biome.
    private static byte Stored(byte b) => b >= 1 && b <= 32 ? b : (byte)0;
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

    // The bytes a world saves (or, for network, sends): the map as read, which is also what the world saved when
    // FollowNames moved biomes; a client gets the map as this game reads it.
    public byte[] Serialize(bool network = false)
    {
        var bytes = new byte[Size * Size + tail.Length];
        if (grid != null)
            for (int y = 0; y < Size; y++)
                grid.CopyRow(y, bytes, y * Size);
        Array.Copy(tail, 0, bytes, Size * Size, tail.Length);
        if (network && decode != Identity)
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = BiomeRegistry.ToByte(decode[bytes[i]]);
        return bytes;
    }

    // A world made since 0.10 saves the names of the added biomes its map holds (Expand World Data numbers its biomes in
    // its yaml's order, so after the yaml changes the same bit can be another biome). FollowNames reads each by its name:
    // where Expand World Data numbers it differently now, the map follows; where it has no such biome now, its ground reads
    // as None (the default generation decides) rather than as whatever biome holds that bit. The map's bytes and the names
    // stay as saved (moved, StoredNames): only the biome each byte reads as changes (decode), so nothing is lost when the
    // yaml is put back.
    private bool moved;
    private Dictionary<Heightmap.Biome, string>? StoredNames;

    internal void FollowNames(Dictionary<Heightmap.Biome, string> names)
    {
        StoredNames = names;
        var to = new Heightmap.Biome[33];
        for (int value = 0; value < to.Length; value++)
            to[value] = BiomeRegistry.FromByte((byte)value);
        bool any = false;
        foreach (var kv in names.OrderBy(kv => (uint)kv.Key))
        {
            var now = EWD.TryGetBiome(kv.Value, out var biome) && BiomeRegistry.IsSingleBit(biome) ? biome : Heightmap.Biome.None;
            if (now == kv.Key || (Present & kv.Key) == 0)
                continue;
            any = true;
            to[BiomeRegistry.ToByte(kv.Key)] = now;
            BetterContinents.Log(now == Heightmap.Biome.None
                ? $"Biome map: {kv.Value} was biome 0x{(uint)kv.Key:X} when the world was saved, and Expand World Data has no such biome now: its ground reads as None (the default generation decides) until it has again."
                : $"Biome map: {kv.Value} was biome 0x{(uint)kv.Key:X} when the world was saved, and Expand World Data numbers it 0x{(uint)now:X} now: the map follows the name.");
        }
        if (!any)
            return;
        moved = true;
        var translated = (Heightmap.Biome[])Identity.Clone();
        Array.Copy(to, translated, to.Length);
        decode = translated;
        Present = PresentFrom(translated);
    }

    // The names a world made since 0.10 saves beside the map's bytes: the ones it read with those bytes; for a map read from
    // its file this session, Expand World Data's names for the added biomes it holds (a name read before, when there is
    // none now).
    internal Dictionary<Heightmap.Biome, string> NamesToSave()
    {
        if (moved && StoredNames != null)
            return StoredNames;
        var names = new Dictionary<Heightmap.Biome, string>();
        for (int bit = 0; bit < 32; bit++)
        {
            var biome = (Heightmap.Biome)(int)(1u << bit);
            if ((Present & biome) == 0 || BiomeRegistry.IsVanilla(biome))
                continue;
            if (EWD.TryGetName(biome, out var name))
                names[biome] = name;
            else if (StoredNames != null && StoredNames.TryGetValue(biome, out var stored))
                names[biome] = stored;
        }
        return names;
    }

    // Every pixel's biome as its byte (BiomeRegistry.ToByte, 0 for None), in tiles (MapTiles.cs).
    private ByteGrid? grid;
    // Bytes past the map's square, from a saved map whose length is no square number: kept and saved back, never read.
    private byte[] tail = [];
    // The biome each byte reads as: BiomeRegistry.FromByte, or as FollowNames moved them.
    private Heightmap.Biome[] decode = Identity;
    private static readonly Heightmap.Biome[] Identity = [.. Enumerable.Range(0, 256).Select(b => BiomeRegistry.FromByte((byte)b))];
    // How many pixels hold each byte (tail included).
    private long[] counts = new long[256];
    // Every biome in the map.
    private Heightmap.Biome Present;

    private Heightmap.Biome PresentFrom(Heightmap.Biome[] biomes)
    {
        var present = Heightmap.Biome.None;
        for (int b = 0; b < counts.Length; b++)
            if (counts[b] > 0)
                present |= biomes[b];
        return present;
    }
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
    // Biomes is a new array each time, the bytes past the square included; BiomeAt reads one pixel.
    internal Heightmap.Biome[] Biomes => [.. Serialize().Select(b => decode[b])];
    internal Heightmap.Biome BiomeAt(int x, int y) => decode[grid!.Get(x, y)];
    // Whether the saved map had bytes past its square (WorldExport then writes no picture of it, as before).
    internal bool HasTail => tail.Length > 0;
    // Every biome some pixel reads as (None included when one does).
    internal IEnumerable<Heightmap.Biome> UsedBiomes => Enumerable.Range(0, counts.Length).Where(b => counts[b] > 0).Select(b => decode[b]).Distinct();
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
    internal static Dictionary<Heightmap.Biome, Color32> ExportLegend(IReadOnlyDictionary<Heightmap.Biome, Color32> legend, IEnumerable<Heightmap.Biome> map)
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
        var counts = new long[256];
        var rows = Rows(img, 1, false, (row, band, at) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                var color = Convert(row[x]);
                if (!colorMapping.TryGetValue(color, out var biome))
                {
                    biome = candidates.OrderBy(d => ColorDistance(color, d.Color)).First().Biome;
                    colorMapping.Add(color, biome);
                }
                var b = BiomeRegistry.ToByte(biome);
                counts[b]++;
                band[at + x] = b;
            }
        });
        grid = ByteGrid.From(rows, Compact);
        tail = [];
        decode = Identity;
        moved = false;
        this.counts = counts;
        Present = colorMapping.Values.Aggregate(Heightmap.Biome.None, (all, biome) => all | biome);
        LogSummary(counts, (long)Size * Size);

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
        // The four pixels around the point, at the edge the edge pixel again.
        int x0 = Mathf.Clamp(xi, 0, Size - 1), x1 = Mathf.Clamp(xi + 1, 0, Size - 1);
        int y0 = Mathf.Clamp(yi, 0, Size - 1), y1 = Mathf.Clamp(yi + 1, 0, Size - 1);
        grid!.Quad(x0, x1, y0, y1, out var b00, out var b10, out var b01, out var b11);
        var biomeOf = decode;
        SampleBiomeWeighted(biomeOf[b00] & usable, (1 - xd) * (1 - yd), biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(biomeOf[b10] & usable, xd * (1 - yd), biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(biomeOf[b01] & usable, (1 - xd) * yd, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);
        SampleBiomeWeighted(biomeOf[b11] & usable, xd * yd, biomeWeights, biomes, ref numBiomes, ref topBiomeIdx);

        return biomes[topBiomeIdx];
    }

    private static void SampleBiomeWeighted(Heightmap.Biome biome, float weight, Span<float> biomeWeights, Span<Heightmap.Biome> biomes, ref int numBiomes, ref int topBiomeIdx)
    {
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
    // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) -----------------------------------------

    private const byte BlockVersion = 1;

    // The map's bytes as saved, in tiles; for network also the biome each byte reads as here when FollowNames moved any
    // (the client reads the map as the server does, without Expand World Data's names).
    internal byte[] ToBlock(bool network)
    {
        var block = grid?.Block ?? throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(BlockVersion);
        block.WriteTo(writer);
        writer.Write(tail.Length);
        writer.Write(tail);
        bool translated = network && decode != Identity;
        writer.Write(translated);
        if (translated)
            for (int b = 0; b <= 32; b++)
                writer.Write((uint)decode[b]);
        writer.Flush();
        return stream.ToArray();
    }

    internal static ImageMapBiome FromBlock(byte[] block)
    {
        using var reader = new BinaryReader(new MemoryStream(block, false));
        var version = reader.ReadByte();
        if (version != BlockVersion)
            throw new InvalidDataException($"a biome map saved in format {version}, which this version of Better Continents cannot read");
        var tiles = TileBlock.ReadFrom(reader);
        if (tiles.Channels != 1 || tiles.Bytes != 1)
            throw new InvalidDataException("a biome map's tiles are not one byte a pixel");
        int tailLength = reader.ReadInt32();
        if (tailLength < 0 || tailLength > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("a biome map ends early");
        var map = new ImageMapBiome
        {
            grid = new ByteGrid(tiles),
            tail = [.. reader.ReadBytes(tailLength).Select(Stored)],
            Size = tiles.Size,
            Compact = true
        };
        if (reader.ReadBoolean())
        {
            var translated = (Heightmap.Biome[])Identity.Clone();
            for (int b = 0; b <= 32; b++)
                translated[b] = (Heightmap.Biome)reader.ReadUInt32();
            map.decode = translated;
        }
        // Every tile read once, for the summary the log gives and the biomes the map holds.
        var counts = map.counts;
        var values = new ushort[TileBlock.Pixels];
        for (int t = 0; t < tiles.Count; t++)
        {
            long pixels = (long)tiles.Extent(t % tiles.Tiles) * tiles.Extent(t / tiles.Tiles);
            if (tiles.IsUniform(t))
            {
                counts[Stored((byte)tiles.UniformValue(t, 0))] += pixels;
                continue;
            }
            tiles.Decode(t, values);
            int w = tiles.Extent(t % tiles.Tiles), h = tiles.Extent(t / tiles.Tiles);
            for (int y = 0; y < h; y++)
                for (int i = y << TileBlock.Shift, end = i + w; i < end; i++)
                    counts[Stored((byte)values[i])]++;
        }
        foreach (var b in map.tail)
            counts[b]++;
        map.Present = map.PresentFrom(map.decode);
        var read = new long[256];
        for (int b = 0; b < counts.Length; b++)
            read[BiomeRegistry.ToByte(map.decode[b])] += counts[b];
        LogSummary(read, (long)map.Size * map.Size + map.tail.Length);
        return map;
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

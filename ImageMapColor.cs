// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

abstract class ImageMapColor() : ImageMapBase()
{

    // Every pixel's colour as the picture had it (RGBA), in tiles (MapTiles.cs); read through the legend it was decoded
    // with (ColorGrid).
    private ColorGrid? grid;
    public string SourceColors = "";
    protected Dictionary<Rgba32, Color32?> Colors = [];

    public bool CreateMap() => CreateMap<Rgba32>();

    // A paint or terrain map from its picture and legend file (bc fn, a new world); setup runs before the files are read.
    protected static T? FromFile<T>(string path, bool compact = false, Action<T>? setup = null) where T : ImageMapColor, new()
    {
        if (string.IsNullOrEmpty(path))
            return null;
        T map = new()
        {
            FilePath = path,
            Compact = compact,
        };
        setup?.Invoke(map);
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap())
            return null;
        return map;
    }
    // A paint or terrain map from a world's settings: the picture's bytes and the legend as the file read it then.
    protected static T? FromSettings<T>(byte[] data, string path, string colors) where T : ImageMapColor, new()
    {
        T map = new()
        {
            FilePath = path,
            SourceData = data,
            SourceColors = colors
        };
        map.ParseColors();
        if (!map.CreateMap())
            return null;
        return map;
    }

    protected bool LoadSourceImageAndColors(string defaultColors)
    {
        SourceColors = "";
        if (!base.LoadSourceImage()) return false;
        var path = Legends.FileFor(FilePath);
        // A missing legend is written out, but SourceColors stays empty: a world's settings keep "" (terrain then
        // reads its default legend again).
        if (!File.Exists(path))
        {
            Legends.WriteDefault(path, defaultColors);
            ParseColors();
            return true;
        }
        try
        {
            SourceColors = PrepareLegend(Legends.ReadJoined(path));
            ParseColors();
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Cannot load file {path}: {ex.Message}.");
        }
        return true;
    }

    protected override bool LoadTextureToMap<T>(Image<T> image)
    {
        var st = new Stopwatch();
        st.Start();

        var img = (Image<Rgba32>)(Image)image;
        grid = ColorGrid.From(Rows(img, 1, true,
            (row, band, at) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].R; },
            (row, band, at) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].G; },
            (row, band, at) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].B; },
            (row, band, at) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].A; }), Colors, Compact);

        BetterContinents.Log($"Time to calculate colors from {FilePath}: {st.ElapsedMilliseconds} ms");
        return true;
    }

    internal override void ReleasePixels()
    {
        if (grid is { Compressed: true })
            grid.DropAll();
        else
            grid = null;
    }

    // The tiles that are decoded now (tests and the console).
    internal int DecodedTiles => grid?.DecodedTiles() ?? 0;

    // The legend's colour for each colour of the picture, or the picture's own colour where the legend has none; read
    // tile by tile with the legend the map was decoded with.
    private sealed class ColorGrid : TileGrid<Color32?>
    {
        private readonly Dictionary<Rgba32, Color32?> colors;

        internal ColorGrid(TileBlock block, Dictionary<Rgba32, Color32?> colors) : base(block) => this.colors = colors;

        private ColorGrid(int size, Dictionary<Rgba32, Color32?> colors) : base(size) => this.colors = colors;

        // From the picture's rows: compressed (Compact Maps), or every tile decoded.
        internal static ColorGrid From(MapRows rows, Dictionary<Rgba32, Color32?> colors, bool compact)
        {
            if (compact)
                return new ColorGrid(TileBlock.Encode(rows), colors);
            var grid = new ColorGrid(rows.Size, colors);
            grid.Fill(rows);
            return grid;
        }

        internal override long TileBytes => 8L * TileBlock.Pixels;

        private Color32? Read(ushort r, ushort g, ushort b, ushort a)
        {
            var pixel = new Rgba32((byte)r, (byte)g, (byte)b, (byte)a);
            if (colors.TryGetValue(pixel, out var color))
                return color;
            return new Color32(pixel.R, pixel.G, pixel.B, pixel.A);
        }

        protected override Color32?[] FromValues(ushort[] values)
        {
            var tile = new Color32?[TileBlock.Pixels];
            const int P = TileBlock.Pixels;
            for (int i = 0; i < P; i++)
                tile[i] = Read(values[i], values[P + i], values[2 * P + i], values[3 * P + i]);
            return tile;
        }

        protected override Color32? UniformValue(ushort[] channels) => Read(channels[0], channels[1], channels[2], channels[3]);

        // The picture's own colour (RGBA), for writing it back out (a compressed grid's).
        internal Rgba32 Source(int x, int y)
        {
            var block = Block!;
            int t = (y >> Shift) * Tiles + (x >> Shift);
            if (block.IsUniform(t))
                return new Rgba32((byte)block.UniformValue(t, 0), (byte)block.UniformValue(t, 1), (byte)block.UniformValue(t, 2), (byte)block.UniformValue(t, 3));
            var values = Scratch(t);
            int i = ((y & Mask) << Shift) | (x & Mask);
            const int P = TileBlock.Pixels;
            return new Rgba32((byte)values[i], (byte)values[P + i], (byte)values[2 * P + i], (byte)values[3 * P + i]);
        }

        // The last tile read for Source, decoded once while a writer walks its rows.
        private int scratchTile = -1;
        private readonly ushort[] scratch = new ushort[4 * TileBlock.Pixels];
        private ushort[] Scratch(int t)
        {
            if (scratchTile != t)
            {
                Block!.Decode(t, scratch);
                scratchTile = t;
            }
            return scratch;
        }
    }

    // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) ---------------------------------------

    private const byte BlockVersion = 1;

    // The legend as the map keeps it (SourceColors), then the picture's colours in tiles.
    internal byte[] ToBlock()
    {
        var block = grid?.Block ?? throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(BlockVersion);
        writer.Write(SourceColors);
        block.WriteTo(writer);
        writer.Flush();
        return stream.ToArray();
    }

    protected static T FromBlock<T>(byte[] block) where T : ImageMapColor, new()
    {
        using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(block, false));
        var version = reader.ReadByte();
        if (version != BlockVersion)
            throw new System.IO.InvalidDataException($"a colour map saved in format {version}, which this version of Better Continents cannot read");
        var made = new T();
        ImageMapColor map = made;
        map.SourceColors = reader.ReadString();
        map.ParseColors();
        var tiles = TileBlock.ReadFrom(reader);
        if (tiles.Channels != 4 || tiles.Bytes != 1)
            throw new System.IO.InvalidDataException("a colour map's tiles are not RGBA");
        map.grid = new ColorGrid(tiles, map.Colors);
        map.Size = tiles.Size;
        map.Compact = true;
        return made;
    }

    // A compact map read from a world's tiles holds no picture: written back out from its tiles (RGBA), for the pictures'
    // format or a world export's sources.
    internal override byte[] SourceBytes()
    {
        if (SourceData.Length > 0 || grid is not { Compressed: true })
            return SourceData;
        lock (grid)
            return Png<Rgba32>((y, row) =>
            {
                for (int x = 0; x < Size; x++)
                    row[x] = grid.Source(x, y);
            }, SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha, SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8);
    }

    protected virtual void ParseColors()
    {
        Colors = [];
    }

    // The paint and terrain legend (their rows of Legends' table): "target: image colour" entries separated by '|', only
    // entries with exactly one ':' read. The image colour is read first (its warning comes first), then the target;
    // the first entry for an image colour wins. A broken number throws out of here.
    // The legend as read from its file, as the map keeps it (and a world's settings save it).
    protected virtual string PrepareLegend(string legend) => legend;

    protected static Dictionary<Rgba32, Color32?> ParseColors(string colors, Func<string, Color32?> target) =>
        colors.Split('|')
        .Select(s => s.Trim().Split(':')).Where(s => s.Length == 2)
        .Select(s => Tuple.Create(Legends.ParseRGBA(s[1]), target(s[0])))
        .Distinct(new FirstImageColour())
        .ToDictionary(s => s.Item1, s => s.Item2);

    private class FirstImageColour : IEqualityComparer<Tuple<Rgba32, Color32?>>
    {
        public bool Equals(Tuple<Rgba32, Color32?> x, Tuple<Rgba32, Color32?> y) => x.Item1.Equals(y.Item1);
        public int GetHashCode(Tuple<Rgba32, Color32?> obj) => obj.Item1.GetHashCode();
    }

    public bool TryGetValue(float x, float y, out UnityEngine.Color color)
    {
        float xa = x * (Size - 1);
        float ya = y * (Size - 1);

        int xi = Mathf.FloorToInt(xa);
        int yi = Mathf.FloorToInt(ya);

        float xd = xa - xi;
        float yd = ya - yi;

        int x0 = Mathf.Clamp(xi, 0, Size - 1);
        int x1 = Mathf.Clamp(xi + 1, 0, Size - 1);
        int y0 = Mathf.Clamp(yi, 0, Size - 1);
        int y1 = Mathf.Clamp(yi + 1, 0, Size - 1);

        grid!.Quad(x0, x1, y0, y1, out var p00, out var p10, out var p01, out var p11);
        if (p00 == null || p10 == null || p01 == null || p11 == null)
        {
            color = UnityEngine.Color.black;
            return false;
        }

        var a = Color32.Lerp(p00.Value, p10.Value, xd);
        var b = Color32.Lerp(p01.Value, p11.Value, xd);
        var c = Color32.Lerp(a, b, yd);
        color = new UnityEngine.Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
        return true;
    }
}

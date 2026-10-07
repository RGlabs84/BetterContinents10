// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

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

    protected override bool LoadTextureToMap<T>(MapPicture<T> picture)
    {
        var st = new Stopwatch();
        st.Start();

        var img = (MapPicture<Rgba32>)(object)picture;
        grid = ColorGrid.From(Rows(img, 1, true,
            (row, band, at, y) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].R; },
            (row, band, at, y) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].G; },
            (row, band, at, y) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].B; },
            (row, band, at, y) => { for (int x = 0; x < row.Length; x++) band[at + x] = row[x].A; }), Colors, Compact);

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

    // Every pixel's colour as the picture had it (red, green, blue and alpha in one number), in tiles; the legend's colour for
    // each is found when a sample reads it (Resolve), or the picture's own colour where the legend has none. The legend is the
    // one the map was decoded with. A pixel takes four bytes (the colour it reads as, with whether the legend gave one, took
    // eight), so a 16384 px map decoded whole is a gigabyte, not two, and each tile decodes without a lookup.
    private sealed class ColorGrid : TileGrid<uint>
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

        internal override long TileBytes => 4L * TileBlock.Pixels;

        private static uint Pack(ushort r, ushort g, ushort b, ushort a) => (uint)((byte)r | (byte)g << 8 | (byte)b << 16 | (byte)a << 24);

        // The legend's colour for a pixel, or its own colour where the legend has none.
        internal Color32? Resolve(uint pixel)
        {
            var rgba = new Rgba32((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16), (byte)(pixel >> 24));
            if (colors.TryGetValue(rgba, out var color))
                return color;
            return new Color32(rgba.R, rgba.G, rgba.B, rgba.A);
        }

        protected override uint[] FromValues(ushort[] values)
        {
            var tile = new uint[TileBlock.Pixels];
            const int P = TileBlock.Pixels;
            for (int i = 0; i < P; i++)
                tile[i] = Pack(values[i], values[P + i], values[2 * P + i], values[3 * P + i]);
            return tile;
        }

        protected override uint UniformValue(ushort[] channels) => Pack(channels[0], channels[1], channels[2], channels[3]);

        // The picture's own colours (RGBA bytes) of tile row ty, for writing the picture back out (a compressed grid's): row r of the
        // band (r from 0, at most 128 of them) pixel x's red at band[(r * Size + x) * 4], then green, blue and alpha. Every tile of the
        // row is decoded once (it was each of 128 times, once for each pixel row of it, on a 16384 px map: minutes).
        internal void ReadSourceBand(int ty, byte[] band)
        {
            var block = Block!;
            const int P = TileBlock.Pixels;
            int rows = block.Extent(ty);
            for (int tx = 0; tx < Tiles; tx++)
            {
                int t = ty * Tiles + tx, x0 = tx << Shift, w = block.Extent(tx);
                if (block.IsUniform(t))
                {
                    byte r = (byte)block.UniformValue(t, 0), g = (byte)block.UniformValue(t, 1), b = (byte)block.UniformValue(t, 2), a = (byte)block.UniformValue(t, 3);
                    for (int y = 0; y < rows; y++)
                        for (int x = 0, at = (y * Size + x0) * 4; x < w; x++, at += 4)
                        {
                            band[at] = r;
                            band[at + 1] = g;
                            band[at + 2] = b;
                            band[at + 3] = a;
                        }
                    continue;
                }
                block.Decode(t, scratch);
                for (int y = 0; y < rows; y++)
                    for (int x = 0, at = (y * Size + x0) * 4, i = y << Shift; x < w; x++, i++, at += 4)
                    {
                        band[at] = (byte)scratch[i];
                        band[at + 1] = (byte)scratch[P + i];
                        band[at + 2] = (byte)scratch[2 * P + i];
                        band[at + 3] = (byte)scratch[3 * P + i];
                    }
            }
        }

        private readonly ushort[] scratch = new ushort[4 * TileBlock.Pixels];
    }

    // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) ---------------------------------------

    private const byte BlockVersion = 1;

    // The legend as the map keeps it (SourceColors), then the picture's colours in tiles.
    internal byte[] ToBlock()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        WriteBlock(writer);
        writer.Flush();
        return stream.ToArray();
    }

    // The same, written where the caller says (a package is written straight into: no array of the block is made).
    internal void WriteBlock(System.IO.BinaryWriter writer)
    {
        var block = grid?.Block ?? throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
        writer.Write(BlockVersion);
        writer.Write(SourceColors);
        block.WriteTo(writer);
        writer.Flush();
    }

    protected static T FromBlock<T>(byte[] block) where T : ImageMapColor, new() => FromBlock<T>(new System.IO.MemoryStream(block, false));

    protected static T FromBlock<T>(System.IO.Stream block) where T : ImageMapColor, new()
    {
        using var reader = new System.IO.BinaryReader(block);
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
        {
            // A band of 128 rows at a time, the rows asked for from the top of the map down: each tile is decoded once.
            var band = new byte[4 * TileBlock.Side * Size];
            int bandRow = -1;
            return Png<Rgba32>((y, row) =>
            {
                int ty = y >> TileBlock.Shift;
                if (ty != bandRow)
                {
                    grid.ReadSourceBand(ty, band);
                    bandRow = ty;
                }
                int at = (y & TileBlock.Mask) * Size * 4;
                for (int x = 0; x < Size; x++, at += 4)
                    row[x] = new Rgba32(band[at], band[at + 1], band[at + 2], band[at + 3]);
            }, SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha, SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8);
        }
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

        grid!.Quad(x0, x1, y0, y1, out var q00, out var q10, out var q01, out var q11);
        // Four pixels, often one colour: the legend is asked once for each.
        var p00 = grid.Resolve(q00);
        var p10 = q10 == q00 ? p00 : grid.Resolve(q10);
        var p01 = q01 == q00 ? p00 : q01 == q10 ? p10 : grid.Resolve(q01);
        var p11 = q11 == q00 ? p00 : q11 == q10 ? p10 : q11 == q01 ? p01 : grid.Resolve(q11);
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

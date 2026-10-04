// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Diagnostics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

internal class ImageMapFloat : ImageMapBase
{
    // How a heightmap's alpha channel is read (BetterContinentsSettings.HeightmapAlphaMode).
    internal enum HeightAlpha
    {
        // Not at all: the image is read as 16-bit grey (L16).
        None,
        // Heightmap Alpha as it always was, and still is on a world made before 0.10: the image is read as 8-bit grey
        // with 8-bit alpha (La16), and the alpha is never used.
        Legacy,
        // Heightmap Alpha on a world made since 0.10 (settings version 12): 16-bit grey with 16-bit alpha (La32), and
        // the alpha blends the heightmap with the game's own terrain (WorldGeneratorPatch.GetBaseHeightPrefixV3Alpha).
        Blend,
    }

    // The pixel type the picture was read as, whose value the map's tiles hold unconverted (MapTiles.cs): the grey, or
    // for the oldest maps the red. Tables turns a value into the float the map has always given for it.
    internal enum ValueFormat : byte
    {
        // 16-bit grey (L16): every float map, and a heightmap without Heightmap Alpha.
        Grey16,
        // 8-bit grey with alpha (La16): HeightAlpha.Legacy.
        GreyAlpha8,
        // 16-bit grey with alpha (La32): HeightAlpha.Blend, its alpha in a grid of its own.
        GreyAlpha16,
        // RGBA (Rgba32): the maps of settings version 4 and older (CreateMapLegacy).
        Rgba8,
    }

    public static ImageMapFloat? Create(string path, bool alpha, bool compact = false) => Create(path, alpha ? HeightAlpha.Legacy : HeightAlpha.None, compact);
    public static ImageMapFloat? Create(string path, HeightAlpha alpha, bool compact = false)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        ImageMapFloat map = new()
        {
            FilePath = path,
            Compact = compact
        };
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap(alpha))
            return null;
        return map;
    }
    public static ImageMapFloat? Create(byte[] data, string path, bool legacy = false)
    {
        ImageMapFloat map = new()
        {
            FilePath = path,
            SourceData = data
        };
        if (legacy)
        {
            if (!map.CreateMapLegacy())
                return null;
        }
        else
        {
            if (!map.CreateMap(false))
                return null;
        }
        return map;
    }
    public static ImageMapFloat? Create(byte[] data, bool alpha) => Create(data, alpha ? HeightAlpha.Legacy : HeightAlpha.None);
    public static ImageMapFloat? Create(byte[] data, HeightAlpha alpha)
    {
        ImageMapFloat map = new()
        {
            SourceData = data
        };
        if (!map.CreateMap(alpha))
            return null;
        return map;
    }

    // The map's values and how they read, swapped as one when the map is decoded again.
    private sealed class Pixels(ValueFormat format, ValueGrid values, ValueGrid? alphas)
    {
        public readonly ValueFormat Format = format;
        public readonly ValueGrid Values = values;
        public readonly ValueGrid? Alphas = alphas;
        public readonly float[] Table = Tables.For(format);
    }
    private Pixels? pixels;

    // What a world export wrote into its heightmap.png: the settings its heights are encoded for. Null for any other map.
    public HeightmapRecord? Record { get; private set; }

    public bool CreateMap(bool alpha) => CreateMap(alpha ? HeightAlpha.Legacy : HeightAlpha.None);
    public bool CreateMap(HeightAlpha alpha) => alpha switch
    {
        HeightAlpha.Legacy => CreateMap<La16>(),
        HeightAlpha.Blend => CreateMap<La32>(),
        _ => CreateMap<L16>(),
    };
    public bool CreateMapLegacy() => CreateMap<Rgba32>();
    protected override bool LoadTextureToMap<T>(Image<T> image)
    {
        var sw = new Stopwatch();
        sw.Start();
        // Only a blending heightmap (La32) keeps its alpha: the legacy one (La16) was never read.
        pixels = image switch
        {
            Image<L16> grey => new Pixels(ValueFormat.Grey16, Grid(Rows(grey, 2, true, (row, band, at) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].PackedValue;
            })), null),
            Image<La16> greyAlpha => new Pixels(ValueFormat.GreyAlpha8, Grid(Rows(greyAlpha, 1, true, (row, band, at) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].L;
            })), null),
            Image<La32> greyAlpha => new Pixels(ValueFormat.GreyAlpha16, Grid(Rows(greyAlpha, 2, true, (row, band, at) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].L;
            })), Grid(Rows(greyAlpha, 2, true, (row, band, at) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].A;
            }))),
            Image<Rgba32> rgba => new Pixels(ValueFormat.Rgba8, Grid(Rows(rgba, 1, true, (row, band, at) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].R;
            })), null),
            _ => throw new NotSupportedException($"a float map read as {typeof(T).Name}"),
        };
        Record = HeightmapRecord.From(SixLabors.ImageSharp.MetadataExtensions.GetPngMetadata(image.Metadata).TextData);

        BetterContinents.Log($"Time to process {FilePath}: {sw.ElapsedMilliseconds} ms");

        return true;
    }

    private ValueGrid Grid(MapRows rows) => ValueGrid.From(rows, Compact);

    internal override void ReleasePixels()
    {
        if (pixels is { Values.Compressed: true } p)
        {
            p.Values.DropAll();
            p.Alphas?.DropAll();
        }
        else
            pixels = null;
    }

    public float GetValue(float x, float y)
    {
        var p = pixels!;
        return Sample(p.Values, p.Table, x, y);
    }

    // Whether the alpha is read (HeightAlpha.Blend), and its value: 1 (opaque) when it is not.
    public bool HasAlpha => pixels?.Alphas != null;
    public float GetAlpha(float x, float y)
    {
        var p = pixels;
        return p?.Alphas != null ? Sample(p.Alphas, Tables.Alpha.Values, x, y) : 1f;
    }

    // The tiles that are decoded now (tests and the console).
    internal int DecodedTiles => (pixels?.Values.DecodedTiles() ?? 0) + (pixels?.Alphas?.DecodedTiles() ?? 0);

    // Bilinear, between the pixel centres; x and y from 0 to 1 across the image.
    private float Sample(ValueGrid grid, float[] table, float x, float y)
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

        grid.Quad(x0, x1, y0, y1, out var v00, out var v10, out var v01, out var v11);
        float p00 = table[v00];
        float p10 = table[v10];
        float p01 = table[v01];
        float p11 = table[v11];

        return Mathf.Lerp(
            Mathf.Lerp(p00, p10, xd),
            Mathf.Lerp(p01, p11, xd),
            yd
        );
    }

    // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) -----------------------------------------

    private const byte BlockVersion = 1;

    internal byte[] ToBlock()
    {
        var p = pixels is { Values.Compressed: true } held ? held : throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(BlockVersion);
        writer.Write((byte)p.Format);
        writer.Write(Record != null);
        if (Record != null)
        {
            writer.Write(Record.Amount);
            writer.Write(Record.SeaLevel);
        }
        p.Values.Block!.WriteTo(writer);
        writer.Write(p.Alphas != null);
        p.Alphas?.Block!.WriteTo(writer);
        writer.Flush();
        return stream.ToArray();
    }

    internal static ImageMapFloat FromBlock(byte[] block)
    {
        using var reader = new BinaryReader(new MemoryStream(block, false));
        var version = reader.ReadByte();
        if (version != BlockVersion)
            throw new InvalidDataException($"a map saved in format {version}, which this version of Better Continents cannot read");
        var format = (ValueFormat)reader.ReadByte();
        HeightmapRecord? record = null;
        if (reader.ReadBoolean())
        {
            float amount = reader.ReadSingle();
            float seaLevel = reader.ReadSingle();
            record = new HeightmapRecord(amount, seaLevel);
        }
        var values = TileBlock.ReadFrom(reader);
        var alphas = reader.ReadBoolean() ? TileBlock.ReadFrom(reader) : null;
        int bytes = format is ValueFormat.Grey16 or ValueFormat.GreyAlpha16 ? 2 : 1;
        if (format > ValueFormat.Rgba8 || values.Channels != 1 || values.Bytes != bytes || (alphas != null) != (format == ValueFormat.GreyAlpha16)
            || (alphas != null && (alphas.Size != values.Size || alphas.Channels != 1 || alphas.Bytes != 2)))
            throw new InvalidDataException($"a map saved as {format} holds tiles that do not match it");
        return new ImageMapFloat
        {
            Size = values.Size,
            Record = record,
            Compact = true,
            pixels = new Pixels(format, new ValueGrid(values), alphas == null ? null : new ValueGrid(alphas)),
        };
    }

    // A compact map read from a world's tiles holds no picture: written back out from its tiles, in the pixel type it
    // was read as, for the pictures' format or a world export's sources.
    internal override byte[] SourceBytes()
    {
        if (SourceData.Length > 0 || pixels is not { } p)
            return SourceData;
        var values = new ushort[Size];
        var alphas = new ushort[Size];
        void Read(int y)
        {
            p.Values.CopyRow(y, values, 0);
            p.Alphas?.CopyRow(y, alphas, 0);
        }
        return p.Format switch
        {
            ValueFormat.Grey16 => Png<L16>((y, row) =>
            {
                Read(y);
                for (int x = 0; x < Size; x++)
                    row[x] = new L16(values[x]);
            }, PngColorType.Grayscale, PngBitDepth.Bit16, Record),
            ValueFormat.GreyAlpha16 => Png<La32>((y, row) =>
            {
                Read(y);
                for (int x = 0; x < Size; x++)
                    row[x] = new La32(values[x], alphas[x]);
            }, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16, Record),
            ValueFormat.GreyAlpha8 => Png<La16>((y, row) =>
            {
                Read(y);
                for (int x = 0; x < Size; x++)
                    row[x] = new La16((byte)values[x], 255);
            }, PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8, Record),
            _ => Png<Rgba32>((y, row) =>
            {
                Read(y);
                for (int x = 0; x < Size; x++)
                {
                    var v = (byte)values[x];
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }, PngColorType.RgbWithAlpha, PngBitDepth.Bit8, Record),
        };
    }

    // Each value's float, exactly as the whole picture used to be converted (LoadPixels: the pixel type's own
    // ToVector4().X, and the blending alpha / 65535f): built once, from the same code.
    private static class Tables
    {
        internal static float[] For(ValueFormat format) => format switch
        {
            ValueFormat.Grey16 => Grey16.Values,
            ValueFormat.GreyAlpha8 => GreyAlpha8.Values,
            ValueFormat.GreyAlpha16 => GreyAlpha16.Values,
            _ => Rgba8.Values,
        };

        private static float[] Build(int count, Func<int, float> value)
        {
            var table = new float[count];
            for (int v = 0; v < count; v++)
                table[v] = value(v);
            return table;
        }

        private static class Grey16
        {
            internal static readonly float[] Values = Build(65536, v => new L16((ushort)v).ToVector4().X);
        }
        private static class GreyAlpha8
        {
            internal static readonly float[] Values = Build(256, v => new La16((byte)v, 255).ToVector4().X);
        }
        private static class GreyAlpha16
        {
            internal static readonly float[] Values = Build(65536, v => new La32((ushort)v, 65535).ToVector4().X);
        }
        private static class Rgba8
        {
            internal static readonly float[] Values = Build(256, v => new Rgba32((byte)v, 0, 0, 255).ToVector4().X);
        }
        internal static class Alpha
        {
            internal static readonly float[] Values = Build(65536, v => (ushort)v / 65535f);
        }
    }
}

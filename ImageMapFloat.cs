// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Diagnostics;
using System.Globalization;
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

    // What the world being made says of fine heights, a heightmap-fine.png beside the heightmap (FineHeights.cs): it is read when the
    // file is there and the world allows it. Only the heightmap is given a rule (BetterContinentsSettings.FineHeights).
    internal enum FineUse
    {
        // Not the heightmap: no fine file is looked for.
        Never,
        // The file beside the heightmap is read.
        Read,
        // Not read, and the log says why when the file is there: the world is made in a settings version that has no fine heights
        // (Override version), it reads the heightmap as 8-bit grey (Heightmap Alpha as it was before 0.10), or Compact Maps is Off.
        OldVersion,
        LegacyAlpha,
        CompactOff,
    }

    // A FineUse with what the log says of it: the settings version, and the Heightmap Amount the heights are read at.
    internal readonly struct FineRule(FineUse use, int version = 0, float amount = 1f)
    {
        public readonly FineUse Use = use;
        public readonly int Version = version;
        public readonly float Amount = amount;
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
    public static ImageMapFloat? Create(string path, HeightAlpha alpha, bool compact = false, FineRule fine = default)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        ImageMapFloat map = new()
        {
            FilePath = path,
            Compact = compact,
            FineWish = fine
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
    private sealed class Pixels(ValueFormat format, ValueGrid values, ValueGrid? alphas, ByteGrid? fine = null, int fineBits = 0)
    {
        public readonly ValueFormat Format = format;
        public readonly ValueGrid Values = values;
        public readonly ValueGrid? Alphas = alphas;
        // The heightmap's fine heights (FineHeights.cs): each pixel's signed offset from its value, in 1/256 of a step, and how many bits
        // of the byte the file used. Only a 16-bit heightmap (Grey16, GreyAlpha16) has them.
        public readonly ByteGrid? Fine = fine;
        public readonly int FineBits = fineBits;
        public readonly float[] Table = Tables.For(format);
    }
    private Pixels? pixels;

    // Whether the world being made reads the heightmap's fine file, and why the file beside this map was not used (or was not needed:
    // every byte 0), for the log, bc info and the import's warnings; null when it was or there is none.
    internal FineRule FineWish;
    internal string? FineNote { get; private set; }
    internal bool HasFine => pixels?.Fine != null;
    internal int FineBits => pixels?.FineBits ?? 0;
    // The fine file's name for the log: the one beside this map's file.
    internal string FineName => FineHeights.FinePath(FilePath) is { } path ? Path.GetFileName(path) : "heightmap-fine.png";

    // What a world export wrote into its heightmap.png: the settings its heights are encoded for. Null for any other map.
    public HeightmapRecord? Record { get; private set; }

    public bool CreateMap(bool alpha) => CreateMap(alpha ? HeightAlpha.Legacy : HeightAlpha.None);
    public bool CreateMap(HeightAlpha alpha)
    {
        bool hadFine = HasFine, wasCompact = Compact;
        // A fine file that reads makes this map compact, before its values are made.
        var fine = ReadFine(alpha);
        pendingFine = fine;
        bool made = false;
        try
        {
            made = alpha switch
            {
                HeightAlpha.Legacy => CreateMap<La16>(),
                HeightAlpha.Blend => CreateMap<La32>(),
                _ => CreateMap<L16>(),
            };
        }
        finally
        {
            pendingFine = null;
            if (!made)
                Compact = wasCompact;
        }
        if (made && fine != null)
            BetterContinents.Log(fine.Success(this));
        else if (made && hadFine)
            BetterContinents.LogWarning($"Fine heights: {Path.GetFileName(FilePath)} is now read without fine heights ({(FineNote == null ? $"{FineName} is not there" : "see above")}), "
                + "so ground made before this and ground made after it can differ by up to half a step of the heightmap.");
        return made;
    }
    public bool CreateMapLegacy() => CreateMap<Rgba32>();
    protected override bool LoadTextureToMap<T>(MapPicture<T> picture)
    {
        var sw = new Stopwatch();
        sw.Start();
        // Only a blending heightmap (La32) keeps its alpha: the legacy one (La16) was never read.
        pixels = picture switch
        {
            MapPicture<L16> grey => new Pixels(ValueFormat.Grey16, Grid(Rows(grey, 2, true, (row, band, at, y) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].PackedValue;
            })), null, pendingFine?.Grid, pendingFine?.Bits ?? 0),
            MapPicture<La16> greyAlpha => new Pixels(ValueFormat.GreyAlpha8, Grid(Rows(greyAlpha, 1, true, (row, band, at, y) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].L;
            })), null),
            MapPicture<La32> greyAlpha => new Pixels(ValueFormat.GreyAlpha16, Grid(Rows(greyAlpha, 2, true, (row, band, at, y) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].L;
            })), Grid(Rows(greyAlpha, 2, true, (row, band, at, y) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].A;
            })), pendingFine?.Grid, pendingFine?.Bits ?? 0),
            MapPicture<Rgba32> rgba => new Pixels(ValueFormat.Rgba8, Grid(Rows(rgba, 1, true, (row, band, at, y) =>
            {
                for (int x = 0; x < row.Length; x++)
                    band[at + x] = row[x].R;
            })), null),
            _ => throw new NotSupportedException($"a float map read as {typeof(T).Name}"),
        };
        Record = HeightmapRecord.From(picture.Text);

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
            p.Fine?.DropAll();
        }
        else
            pixels = null;
    }

    public float GetValue(float x, float y)
    {
        var p = pixels!;
        return p.Fine is { } fine ? SampleFine(p.Values, fine, x, y) : Sample(p.Values, p.Table, x, y);
    }

    // Whether the alpha is read (HeightAlpha.Blend), and its value: 1 (opaque) when it is not.
    public bool HasAlpha => pixels?.Alphas != null;
    public float GetAlpha(float x, float y)
    {
        var p = pixels;
        return p?.Alphas != null ? Sample(p.Alphas, Tables.Alpha.Values, x, y) : 1f;
    }

    // The tiles that are decoded now (tests and the console).
    internal int DecodedTiles => (pixels?.Values.DecodedTiles() ?? 0) + (pixels?.Alphas?.DecodedTiles() ?? 0) + (pixels?.Fine?.DecodedTiles() ?? 0);

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

    // With fine heights: the same pixels, each read as N = 256 c + f (its value and its fine offset, f signed), a whole number below 2^24
    // and so exact in a float; the bilinear blend of those, and one multiplication in double and one rounding to a float (v = N / 16776960,
    // which for f = 0 is c / 65535: the float the coarse path reads, at the pixel's centre).
    private float SampleFine(ValueGrid grid, ByteGrid fine, float x, float y)
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

        grid.Quad(x0, x1, y0, y1, out var c00, out var c10, out var c01, out var c11);
        fine.Quad(x0, x1, y0, y1, out var f00, out var f10, out var f01, out var f11);
        float n00 = (c00 << 8) + (sbyte)f00;
        float n10 = (c10 << 8) + (sbyte)f10;
        float n01 = (c01 << 8) + (sbyte)f01;
        float n11 = (c11 << 8) + (sbyte)f11;

        float n = Mathf.Lerp(
            Mathf.Lerp(n00, n10, xd),
            Mathf.Lerp(n01, n11, xd),
            yd
        );
        return (float)(n * (1.0 / FineHeights.Scale));
    }

    // ---- fine heights: heightmap-fine.png beside the heightmap's file (FineHeights.cs) ----------------------------------------

    // What reading the fine file made: its tiles, how many bits its bytes use, and the file names for the log.
    private sealed class FineMap(ByteGrid grid, int bits, string fineName, string heightmapName)
    {
        internal readonly ByteGrid Grid = grid;
        internal readonly int Bits = bits;

        // The log line of a map made with it: what the fine file gives, the cost, and that it is for this version on.
        internal string Success(ImageMapFloat map)
        {
            float amount = map.FineWish.Amount;
            long bytes = Grid.Block!.Data.Length;
            double perPixel = bytes * 8.0 / ((double)map.Size * map.Size);
            return string.Format(CultureInfo.InvariantCulture,
                "Fine heights: {0} refines {1} with {2} bits a pixel: ground in steps of {3} at Heightmap Amount {4} ({1} alone: {5}), {6} of tiles, {7:0.00} bits a pixel. "
                + "A Better Continents older than this one cannot read a world made with fine heights.",
                fineName, heightmapName, Bits, FineHeights.Length(FineHeights.Resolution(amount, Bits)), amount, FineHeights.Length(FineHeights.Step(amount)),
                FineHeights.Bytes(bytes), perPixel);
        }
    }
    // The fine heights read for the map being made (CreateMap), until its values are.
    private FineMap? pendingFine;

    private enum FineLevel { Info, Warning, Error }

    private FineMap? Said(FineLevel level, string message)
    {
        FineNote = "Fine heights: " + message;
        switch (level)
        {
            case FineLevel.Info: BetterContinents.Log(FineNote); break;
            case FineLevel.Warning: BetterContinents.LogWarning(FineNote); break;
            default: BetterContinents.LogError(FineNote); break;
        }
        return null;
    }

    // The heightmap's fine heights, when the world reads them (FineWish) and its file is there: the checks and their messages are those
    // of tools/fine_ref.py (judge), in its order. Why the file is not used is logged and kept in FineNote, and the heightmap is made
    // without it; nothing is said when there is no file, and nothing is thrown. A fine file that reads makes this map compact.
    private FineMap? ReadFine(HeightAlpha alpha)
    {
        FineNote = null;
        if (FineWish.Use == FineUse.Never || !SourceIsFromFile || SourceData.Length == 0 || FineHeights.FinePath(FilePath) is not { } finePath)
            return null;
        string hn = Path.GetFileName(FilePath), fn = Path.GetFileName(finePath);
        try
        {
            if (!File.Exists(finePath))
                return null;
            var use = FineWish.Use == FineUse.Read && alpha == HeightAlpha.Legacy ? FineUse.LegacyAlpha : FineWish.Use;
            switch (use)
            {
                case FineUse.OldVersion:
                    return Said(FineLevel.Warning, $"{fn} is not read: the world is saved in settings version {FineWish.Version}, which has no fine heights.");
                case FineUse.LegacyAlpha:
                    return Said(FineLevel.Warning, $"{fn} is not read: Heightmap Alpha as it was before (Legacy) reads {hn} as 8-bit grey.");
                case FineUse.CompactOff:
                    return Said(FineLevel.Warning, $"{fn} is not read: Compact Maps is Off, and a world with fine heights keeps its maps as compressed tiles (Auto or On reads it).");
            }

            PngInfo coarse;
            try
            {
                coarse = PngInfo.Read(SourceData);
            }
            catch (PngInfoException e)
            {
                return Said(FineLevel.Error, $"{fn} is not read: {hn} cannot be read: {e.Message}.");
            }
            if (coarse.Depth != 16 || coarse.ColourType is not (0 or 4))
                return Said(FineLevel.Warning, $"{fn} is not read: {hn} is {coarse.Describe()}; fine heights refine a 16-bit grey heightmap (grey, or grey and alpha).");

            byte[] bytes;
            PngInfo png;
            try
            {
                bytes = File.ReadAllBytes(finePath);
                png = PngInfo.Read(bytes);
            }
            catch (PngInfoException e)
            {
                return Said(FineLevel.Error, $"{fn} cannot be read: {e.Message}.");
            }
            if (png.Width > MaxMapSize || png.Height > MaxMapSize)
                return Said(FineLevel.Error, $"{fn} is {png.Width} x {png.Height} pixels, and the largest map Better Continents reads is {MaxMapSize} x {MaxMapSize}.");
            if (png.Depth != 8 || png.ColourType != 0)
                return Said(FineLevel.Error, $"{fn} is {png.Describe()}; it must be 8-bit grey (no alpha, no palette).");
            if (png.Width != coarse.Width || png.Height != coarse.Height)
                return Said(FineLevel.Error, $"{fn} is {png.Width} x {png.Height} pixels and {hn} is {coarse.Width} x {coarse.Height}; they must be the same size.");
            var record = FineHeights.ReadRecord(png.Texts);
            if (record?.Format is { } format && format != FineHeights.Format)
                return Said(FineLevel.Warning, $"{fn} is in fine format {format}, which this Better Continents cannot read.");
            if (record?.HeightmapCrc is { } named)
            {
                uint actual = Crc32.Compute(SourceData, 0, SourceData.Length);
                if (named != actual)
                    return Said(FineLevel.Warning, string.Format(CultureInfo.InvariantCulture,
                        "{0} was made for another {1} (its record names CRC-32 {2:X8}; this one is {3:X8}). If {1} was edited, make {0} again or delete it.", fn, hn, named, actual));
            }
            // A heightmap that is no square is refused by the map's own load, which says so.
            if (coarse.Width != coarse.Height)
                return null;

            ByteGrid? grid = null;
            int used = 0;
            try
            {
                OpenPicture<L8>(bytes, finePath, (picture, whole) =>
                {
                    int all = 0;
                    var rows = Rows(picture, 1, true, (row, band, at, y) =>
                    {
                        for (int x = 0; x < row.Length; x++)
                        {
                            byte value = row[x].PackedValue;
                            band[at + x] = value;
                            all |= value;
                        }
                    });
                    grid = ByteGrid.From(rows, compact: true);
                    used = all;
                    return true;
                });
            }
            catch (Exception e)
            {
                return Said(FineLevel.Error, $"{fn} cannot be read: its image data cannot be read ({e.Message}).");
            }
            int bits = FineHeights.BitsUsed(used);
            if (grid == null || bits == 0)
                return Said(FineLevel.Info, $"every byte of {fn} is 0, so it changes nothing: the world is the one {hn} makes alone.");
            Compact = true;
            return new FineMap(grid, bits, fn, hn);
        }
        catch (Exception e)
        {
            return Said(FineLevel.Error, $"{fn} cannot be read: {e.Message}.");
        }
    }

    // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) -----------------------------------------

    private const byte BlockVersion = 1;
    // A heightmap with fine heights: format 1's fields, then the fine format (1), the bits the fine bytes use (1 to 8, for people) and
    // the fine bytes' tiles. Better Continents 0.10.0 to 0.10.2 refuse this format and leave the world's settings as they are.
    private const byte FineBlockVersion = 2;

    internal byte[] ToBlock()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteBlock(writer);
        writer.Flush();
        return stream.ToArray();
    }

    // The same, written where the caller says (a package is written straight into: no array of the block is made).
    internal void WriteBlock(BinaryWriter writer)
    {
        var p = pixels is { Values.Compressed: true } held ? held : throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
        writer.Write(p.Fine == null ? BlockVersion : FineBlockVersion);
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
        if (p.Fine != null)
        {
            writer.Write((byte)FineHeights.Format);
            writer.Write((byte)p.FineBits);
            p.Fine.Block!.WriteTo(writer);
        }
        writer.Flush();
    }

    internal static ImageMapFloat FromBlock(byte[] block) => FromBlock(new MemoryStream(block, false));

    internal static ImageMapFloat FromBlock(Stream block)
    {
        using var reader = new BinaryReader(block);
        var version = reader.ReadByte();
        if (version != BlockVersion && version != FineBlockVersion)
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
        TileBlock? fine = null;
        int fineBits = 0;
        if (version == FineBlockVersion)
        {
            int fineFormat = reader.ReadByte();
            fineBits = reader.ReadByte();
            if (fineFormat != FineHeights.Format)
                throw new InvalidDataException($"a map saved with fine heights in format {fineFormat}, which this version of Better Continents cannot read");
            fine = TileBlock.ReadFrom(reader);
        }
        int bytes = format is ValueFormat.Grey16 or ValueFormat.GreyAlpha16 ? 2 : 1;
        if (format > ValueFormat.Rgba8 || values.Channels != 1 || values.Bytes != bytes || (alphas != null) != (format == ValueFormat.GreyAlpha16)
            || (alphas != null && (alphas.Size != values.Size || alphas.Channels != 1 || alphas.Bytes != 2)))
            throw new InvalidDataException($"a map saved as {format} holds tiles that do not match it");
        if (fine != null && (bytes != 2 || fineBits < 1 || fineBits > FineHeights.MaxBits || fine.Size != values.Size || fine.Channels != 1 || fine.Bytes != 1))
            throw new InvalidDataException($"a map saved as {format} holds fine heights that do not match it");
        return new ImageMapFloat
        {
            Size = values.Size,
            Record = record,
            Compact = true,
            pixels = new Pixels(format, new ValueGrid(values), alphas == null ? null : new ValueGrid(alphas), fine == null ? null : new ByteGrid(fine), fineBits),
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

    // The fine heights written out as a heightmap-fine.png for the heightmap.png whose bytes are coarseBytes (SourceBytes, or the file
    // as it was written): 8-bit grey, with the record naming that file's CRC-32, so the pair reads as it does here. Null when the map
    // has no fine heights.
    internal byte[]? FineSourceBytes(byte[] coarseBytes)
    {
        if (pixels is not { Fine: { } fine } p)
            return null;
        return PngWriter.Write(Size, Size, 0, 8, (file, raw) => fine.CopyRow(Size - 1 - file, raw, 0),
            HeightmapRecord.Keyword, FineHeights.RecordText(p.FineBits, Crc32.Compute(coarseBytes, 0, coarseBytes.Length)));
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

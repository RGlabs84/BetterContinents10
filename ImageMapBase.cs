// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UnityEngine;

namespace BetterContinents;

// Loads and stores source image file in original format.
// Derived types will define the final type of the image pixels (the "map"), and
// how to access them
internal abstract class ImageMapBase()
{
  public string FilePath = "";

  public byte[] SourceData = [];

  public int Size;

  // Compact Maps (BetterContinentsSettings.CompactMaps): the map keeps its tiles compressed, decodes each when it is
  // read, and a world made since 0.10 saves them as they are (MapTiles.cs). Otherwise every tile is decoded when the
  // picture is read and kept, and the world saves the picture, as always.
  internal bool Compact;

  // The largest map Better Continents reads at all, in pixels across: 16384 x 16384 is 268 million pixels (537 MB as 16-bit
  // grey), and holds a world of 32 km at 2 m a pixel. The largest picture a new map reads on this machine is Max Map Size, at
  // most that. A picture a world was made with before is never refused (it is read from the world's settings, not from a file).
  internal const int LargestMapSize = 16384;
  internal static int MaxMapSize => BetterContinents.ConfigMaxMapSize is { Value: > 0 } limit ? Math.Min(limit.Value, LargestMapSize) : LargestMapSize;

  public virtual bool LoadSourceImage()
  {
    if (!File.Exists(FilePath))
    {
      BetterContinents.LogWarning($"Cannot find image {FilePath}: Image was not reloaded.");
      return false;
    }
    try
    {
      // The picture's size first, from the file's first bytes: a picture too large is refused before the whole file is read.
      if (PictureSize(FilePath) is { } picture && (picture.Width > MaxMapSize || picture.Height > MaxMapSize))
      {
        BetterContinents.LogError($"Cannot use texture {FilePath}: it is {picture.Width} x {picture.Height} pixels, and the largest map Better Continents reads is {MaxMapSize} x {MaxMapSize}" +
          (MaxMapSize < LargestMapSize ? $" (Max Map Size; it can be raised to {LargestMapSize}). " : ". ") +
          "Scale it down (at 2 m a pixel, 16384 pixels hold a world 32.7 km across).");
        return false;
      }
      SourceData = File.ReadAllBytes(FilePath);
      return true;
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot load image {FilePath}: {ex.Message}");
      return false;
    }
  }

  // A picture's width and height from its header, without reading the rest of the file: a PNG's own IHDR (always the first
  // chunk) or, for any other kind of file, what ImageSharp makes of its start; null when neither says.
  internal static (int Width, int Height)? PictureSize(string path)
  {
    using var stream = File.OpenRead(path);
    var header = new byte[24];
    int read = 0, n;
    while (read < header.Length && (n = stream.Read(header, read, header.Length - read)) > 0)
      read += n;
    if (read == header.Length && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71 && header[12] == 'I' && header[13] == 'H' && header[14] == 'D' && header[15] == 'R')
    {
      long width = (uint)(header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19]);
      long height = (uint)(header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23]);
      return (width > int.MaxValue ? int.MaxValue : (int)width, height > int.MaxValue ? int.MaxValue : (int)height);
    }
    try
    {
      stream.Position = 0;
      var info = Image.Identify(Configuration.Default, stream);
      return info == null ? null : (info.Width, info.Height);
    }
    catch (Exception)
    {
      // A format ImageSharp does not know, or a damaged start: the picture is read (and refused) the usual way.
      return null;
    }
  }

  protected static Color32 Convert(Rgba32 pixel) => new(pixel.R, pixel.G, pixel.B, pixel.A);

  protected Image<T> LoadImage<T>() where T : unmanaged, IPixel<T> => Image.Load<T>(Configuration.Default, SourceData);

  // Reads the picture's rows and builds the map from them (picture: map row 0 is the south).
  protected abstract bool LoadTextureToMap<T>(MapPicture<T> picture) where T : unmanaged, IPixel<T>;

  // Every pixel of the picture through a converter, row by row as the map holds them (the whole picture in one array).
  public R[] LoadPixels<T, R>(MapPicture<T> picture, Func<T, R> converter) where T : unmanaged, IPixel<T>
  {
    int size = picture.Width;
    var pixels = new R[(long)size * picture.Height];
    picture.ReadRows(0, picture.Height, (r, row) =>
    {
      long at = (long)r * size;
      for (int x = 0; x < row.Length; x++)
        pixels[at + x] = converter(row[x]);
    });
    return pixels;
  }

  protected bool CreateMap<T>() where T : unmanaged, IPixel<T>
  {
    try
    {
      var sw = new Stopwatch();
      sw.Start();

      // A PNG of a kind PngRows reads is decoded a band of rows at a time, as the map's tiles are made from it, and never
      // whole in memory; any other picture (and a PNG that turns out damaged) is decoded whole by ImageSharp, as always.
      if (PngRows<T>.TryOpen(SourceData) is { } streamed)
      {
        try
        {
          using (streamed)
          {
            if (!ValidateDimensions(streamed.Width, streamed.Height))
              return false;
            Size = streamed.Width;
            BetterContinents.Log($"Time to load {FilePath}: {sw.ElapsedMilliseconds} ms");
            return Made(LoadTextureToMap(streamed));
          }
        }
        catch (PngRowsException e)
        {
          BetterContinents.Log($"{FilePath}: {e.Message}; reading it whole instead.");
        }
      }

      using var image = LoadImage<T>();
      if (!ValidateDimensions(image.Width, image.Height))
      {
        return false;
      }
      Size = image.Width;

      BetterContinents.Log($"Time to load {FilePath}: {sw.ElapsedMilliseconds} ms");
      if (Size >= 4096)
        BetterContinents.Log($"{FilePath} is a picture of a kind that is read whole ({Size} x {Size}: {(long)Size * Size * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() >> 20} MB at once) " +
          "rather than a few rows at a time; 8-bit and 16-bit grey, RGB, RGBA and palette PNGs without interlacing are the kinds read by rows.");

      try
      {
        return Made(LoadTextureToMap(new ImagePicture<T>(image)));
      }
      finally
      {
        // ImageSharp keeps the buffers it rented for the next picture; this process has no next picture soon.
        ReleaseImageMemory();
      }
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot load texture {FilePath}: {ex.Message}");
      return false;
    }
  }

  // A compact map keeps its tiles, not the file's bytes (as a world read from its settings has none either): 168 MB for a
  // 16384 px heightmap, held by every map of a world being made until it is saved. A picture wanted again is read from its file
  // (Redecode), or written out from the tiles (SourceBytes).
  private bool Made(bool made)
  {
    if (made && Compact)
      SourceData = [];
    return made;
  }

  // ImageSharp's pooled buffers (up to a gigabyte after a 16384 px picture) are given back.
  private static void ReleaseImageMemory()
  {
    try
    {
      Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
    }
    catch (Exception)
    {
      // An allocator that cannot release holds nothing worth the trouble.
    }
  }

  protected bool ValidateDimensions(int width, int height)
  {
    if (width != height)
    {
      BetterContinents.LogError(
          $"Cannot use texture {FilePath}: its width ({width}) does not match its height ({height})");
      return false;
    }
    return true;
  }

  // World import (BetterContinentsSettings.CreateForImport): lets go of the decoded pixels of a map whose settings store
  // only SourceData, so a preset build does not hold every map at once. A compact map drops its decoded tiles (decoded
  // again if it is read); any other cannot be sampled afterwards, as before.
  internal virtual void ReleasePixels()
  {
  }

  // The picture's bytes as a world saves them in the pictures' format (Serialize.cs): the file as it was read. A compact
  // map read from a world's tiles holds no file, and the float and colour maps then write their tiles back out as a PNG.
  internal virtual byte[] SourceBytes() => SourceData;

  // One channel of a picture's row, into the band a map's tiles are made from: row[x]'s value at band[at + x].
  // y: the map row the picture's row is (0 = south), for a channel that has something to say about where a pixel is.
  protected delegate void RowValues<TPixel>(Span<TPixel> row, ushort[] band, int at, int y) where TPixel : unmanaged, IPixel<TPixel>;

  // The picture's pixels as rows for the tiles, a band of rows at a time, one RowValues per channel. median: the values
  // are amounts (heights, densities, colours) the median predictor may suit when compressing, not categories. The bands are
  // asked for from the top of the map down (TileBlock.Encode, TileGrid.Fill), which is the order a PNG's file has its rows in.
  protected static MapRows Rows<TPixel>(MapPicture<TPixel> picture, int bytes, bool median, params RowValues<TPixel>[] channels)
    where TPixel : unmanaged, IPixel<TPixel>
  {
    int size = picture.Width;
    return new MapRows(size, channels.Length, bytes, median, (y0, rows, band) =>
      picture.ReadRows(y0, rows, (r, row) =>
      {
        for (int c = 0; c < channels.Length; c++)
          channels[c](row, band, (c * TileBlock.Side + r) * size, y0 + r);
      }));
  }

  // A map's row y (row 0 = south, as maps hold them) into a picture's row.
  protected delegate void RowFill<TPixel>(int y, Span<TPixel> row) where TPixel : unmanaged, IPixel<TPixel>;

  // The map written out as a PNG, rows from the north as files have them (SourceBytes). record: the heightmap's
  // record, kept as the text chunk it came in; no other chunk is written (gAMA would shift legend colours).
  protected byte[] Png<TPixel>(RowFill<TPixel> fill, PngColorType type, PngBitDepth depth, HeightmapRecord? record = null)
    where TPixel : unmanaged, IPixel<TPixel>
  {
    // Written a row at a time (PngWriter): ImageSharp's encoder takes a whole image, 537 MB for a 16384 px 16-bit map.
    var pixels = new TPixel[Size];
    return PngWriter.Write(Size, Size, (int)type, (int)depth, (file, raw) =>
    {
      fill(Size - 1 - file, pixels);
      RawRow(pixels, raw);
    }, record == null ? null : HeightmapRecord.Keyword, record?.Text);
  }

  // A row of pixels as the bytes of a PNG of their type (16-bit values big-endian).
  private static void RawRow<TPixel>(TPixel[] pixels, byte[] raw) where TPixel : unmanaged, IPixel<TPixel>
  {
    if (typeof(TPixel) == typeof(L16))
    {
      var from = MemoryMarshal.Cast<TPixel, L16>(pixels);
      for (int x = 0; x < from.Length; x++)
      {
        raw[2 * x] = (byte)(from[x].PackedValue >> 8);
        raw[2 * x + 1] = (byte)from[x].PackedValue;
      }
    }
    else if (typeof(TPixel) == typeof(La32))
    {
      var from = MemoryMarshal.Cast<TPixel, La32>(pixels);
      for (int x = 0; x < from.Length; x++)
      {
        raw[4 * x] = (byte)(from[x].L >> 8);
        raw[4 * x + 1] = (byte)from[x].L;
        raw[4 * x + 2] = (byte)(from[x].A >> 8);
        raw[4 * x + 3] = (byte)from[x].A;
      }
    }
    else if (typeof(TPixel) == typeof(La16))
    {
      var from = MemoryMarshal.Cast<TPixel, La16>(pixels);
      for (int x = 0; x < from.Length; x++)
      {
        raw[2 * x] = from[x].L;
        raw[2 * x + 1] = from[x].A;
      }
    }
    else if (typeof(TPixel) == typeof(Rgba32))
    {
      var from = MemoryMarshal.Cast<TPixel, Rgba32>(pixels);
      for (int x = 0; x < from.Length; x++)
      {
        raw[4 * x] = from[x].R;
        raw[4 * x + 1] = from[x].G;
        raw[4 * x + 2] = from[x].B;
        raw[4 * x + 3] = from[x].A;
      }
    }
    else
      throw new NotSupportedException($"a PNG of {typeof(TPixel).Name} pixels");
  }

  public virtual void SerializeLegacy(ZPackage pkg, int version, bool network)
  {
    // File path may contain sensitive imformation so its removed from network serialization.
    pkg.Write(network ? "?" : FilePath);
    pkg.Write(SourceData);
  }
}

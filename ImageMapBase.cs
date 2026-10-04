// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

  public virtual bool LoadSourceImage()
  {
    if (!File.Exists(FilePath))
    {
      BetterContinents.LogWarning($"Cannot find image {FilePath}: Image was not reloaded.");
      return false;
    }
    try
    {
      SourceData = File.ReadAllBytes(FilePath);
      return true;
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot load image {FilePath}: {ex.Message}");
      return false;
    }
  }

  protected static Color32 Convert(Rgba32 pixel) => new(pixel.R, pixel.G, pixel.B, pixel.A);

  protected Image<T> LoadImage<T>() where T : unmanaged, IPixel<T> => Image.Load<T>(Configuration.Default, SourceData);

  protected abstract bool LoadTextureToMap<T>(Image<T> image) where T : unmanaged, IPixel<T>;

  public R[] LoadPixels<T, R>(Image<T> image, Func<T, R> converter) where T : unmanaged, IPixel<T>
  {
    var pixels = new R[image.Width * image.Height];
    image.ProcessPixelRows(acc =>
    {
      for (int y = 0; y < acc.Height; y++)
      {
        var row = acc.GetRowSpan(y);
        for (int x = 0; x < row.Length; x++)
        {
          pixels[y * row.Length + x] = converter(row[x]);
        }
      }
    });
    return pixels;
  }
  protected bool CreateMap<T>() where T : unmanaged, IPixel<T>
  {
    try
    {
      var sw = new Stopwatch();
      sw.Start();

      // Cast disambiguates to the correct return type for some reason
      using var image = LoadImage<T>();
      if (!ValidateDimensions(image.Width, image.Height))
      {
        return false;
      }
      Size = image.Width;

      image.Mutate(x => x.Flip(FlipMode.Vertical));

      BetterContinents.Log($"Time to load {FilePath}: {sw.ElapsedMilliseconds} ms");

      return LoadTextureToMap(image);
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot load texture {FilePath}: {ex.Message}");
      return false;
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
  protected delegate void RowValues<TPixel>(Span<TPixel> row, ushort[] band, int at) where TPixel : unmanaged, IPixel<TPixel>;

  // The picture's pixels as rows for the tiles, a band of rows at a time, one RowValues per channel. median: the values
  // are amounts (heights, densities, colours) the median predictor may suit when compressing, not categories.
  protected static MapRows Rows<TPixel>(Image<TPixel> image, int bytes, bool median, params RowValues<TPixel>[] channels)
    where TPixel : unmanaged, IPixel<TPixel>
  {
    int size = image.Width;
    return new MapRows(size, channels.Length, bytes, median, (y0, rows, band) =>
      image.ProcessPixelRows(accessor =>
      {
        for (int r = 0; r < rows; r++)
        {
          var row = accessor.GetRowSpan(y0 + r);
          for (int c = 0; c < channels.Length; c++)
            channels[c](row, band, (c * TileBlock.Side + r) * size);
        }
      }));
  }

  // A map's row y (row 0 = south, as maps hold them) into a picture's row.
  protected delegate void RowFill<TPixel>(int y, Span<TPixel> row) where TPixel : unmanaged, IPixel<TPixel>;

  // The map written out as a PNG, rows from the north as files have them (SourceBytes). record: the heightmap's
  // record, kept as the text chunk it came in; no other chunk is written (gAMA would shift legend colours).
  protected byte[] Png<TPixel>(RowFill<TPixel> fill, PngColorType type, PngBitDepth depth, HeightmapRecord? record = null)
    where TPixel : unmanaged, IPixel<TPixel>
  {
    using var image = new Image<TPixel>(Size, Size);
    image.ProcessPixelRows(accessor =>
    {
      for (int y = 0; y < Size; y++)
        fill(y, accessor.GetRowSpan(Size - 1 - y));
    });
    var encoder = new PngEncoder
    {
      ColorType = type,
      BitDepth = depth,
      CompressionLevel = PngCompressionLevel.DefaultCompression,
      ChunkFilter = record == null ? PngChunkFilter.ExcludeAll : PngChunkFilter.ExcludeAll & ~PngChunkFilter.ExcludeTextChunks,
    };
    if (record != null)
      image.Metadata.GetPngMetadata().TextData.Add(new PngTextData(HeightmapRecord.Keyword, record.Text, "", ""));
    using var stream = new MemoryStream();
    image.Save(stream, encoder);
    return stream.ToArray();
  }

  public virtual void SerializeLegacy(ZPackage pkg, int version, bool network)
  {
    // File path may contain sensitive imformation so its removed from network serialization.
    pkg.Write(network ? "?" : FilePath);
    pkg.Write(SourceData);
  }
}

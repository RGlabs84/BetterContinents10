// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3).

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
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

  // World import (BetterContinentsSettings.CreateForImport): drops the decoded pixels of a map whose settings store
  // only SourceData, so a preset build does not hold every map at once. The map cannot be sampled afterwards.
  internal virtual void ReleasePixels()
  {
  }

  public virtual void SerializeLegacy(ZPackage pkg, int version, bool network)
  {
    // File path may contain sensitive imformation so its removed from network serialization.
    pkg.Write(network ? "?" : FilePath);
    pkg.Write(SourceData);
  }

  // ParseColor32 for legends, which report a bad colour instead of reading it as transparent black: false for
  // anything that is not a hex colour or at least three comma-separated numbers from 0 to 255 (a fourth is alpha).
  protected static bool TryParseColor32(string color, out Color32 result)
  {
    result = default;
    color = color.Trim();
    var split = color.Split(',');
    if (split.Length == 1)
    {
      if (!SixLabors.ImageSharp.Color.TryParseHex(color, out var hex))
        return false;
      Rgba32 rgba = hex;
      result = new Color32(rgba.R, rgba.G, rgba.B, rgba.A);
      return true;
    }
    if (split.Length < 3)
      return false;
    var parts = new byte[] { 0, 0, 0, 255 };
    for (int i = 0; i < Math.Min(split.Length, 4); i++)
      if (!byte.TryParse(split[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parts[i]))
        return false;
    result = new Color32(parts[0], parts[1], parts[2], parts[3]);
    return true;
  }
  protected static Color32 ParseColor32(string color)
  {
    var rgba = ParseRGBA(color);
    return new Color32(rgba.R, rgba.G, rgba.B, rgba.A);
  }
  protected static Rgba32 ParseRGBA(string color)
  {
    color = color.Trim();
    var split = color.Split(',').ToArray();
    if (split.Length == 1)
    {
      if (SixLabors.ImageSharp.Color.TryParseHex(color, out var c))
        return c;
      else
      {
        BetterContinents.LogWarning($"Cannot parse color {color}");
        return new Rgba32(0, 0, 0, 0);
      }
    }
    if (split.Length < 3)
    {
      BetterContinents.LogWarning($"Cannot parse color {color}");
      return new Rgba32(0, 0, 0, 0);
    }
    var a = split.Length == 3 ? "255" : split[3];
    return new Rgba32(byte.Parse(split[0]), byte.Parse(split[1]), byte.Parse(split[2]), byte.Parse(a));
  }
}

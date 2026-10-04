// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Diagnostics;
using SixLabors.ImageSharp;
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

    public static ImageMapFloat? Create(string path, bool alpha) => Create(path, alpha ? HeightAlpha.Legacy : HeightAlpha.None);
    public static ImageMapFloat? Create(string path, HeightAlpha alpha)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        ImageMapFloat map = new()
        {
            FilePath = path
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
    private float[] Map = [];
    private float[] AlphaMap = [];

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
        Map = LoadPixels(image, pixel => pixel.ToVector4().X);
        Record = HeightmapRecord.From(SixLabors.ImageSharp.MetadataExtensions.GetPngMetadata(image.Metadata).TextData);
        // Only a blending heightmap (La32) keeps its alpha: the legacy one (La16) was never read.
        if (image is Image<La32> img)
            AlphaMap = LoadPixels(img, pixel => pixel.A / 65535f);
        else AlphaMap = [];

        BetterContinents.Log($"Time to process {FilePath}: {sw.ElapsedMilliseconds} ms");

        return true;
    }

    internal override void ReleasePixels()
    {
        Map = [];
        AlphaMap = [];
    }

    public float GetValue(float x, float y) => Sample(Map, x, y);

    // Whether the alpha is read (HeightAlpha.Blend), and its value: 1 (opaque) when it is not.
    public bool HasAlpha => AlphaMap.Length > 0;
    public float GetAlpha(float x, float y) => AlphaMap.Length > 0 ? Sample(AlphaMap, x, y) : 1f;

    // Bilinear, between the pixel centres; x and y from 0 to 1 across the image.
    private float Sample(float[] map, float x, float y)
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

        float p00 = map[y0 * Size + x0];
        float p10 = map[y0 * Size + x1];
        float p01 = map[y1 * Size + x0];
        float p11 = map[y1 * Size + x1];

        return Mathf.Lerp(
            Mathf.Lerp(p00, p10, xd),
            Mathf.Lerp(p01, p11, xd),
            yd
        );
    }

}

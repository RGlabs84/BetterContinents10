// Modified by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

internal class ImageMapPaint() : ImageMapColor()
{
    public static ImageMapPaint? Create(string path, bool compact = false) => FromFile<ImageMapPaint>(path, compact);
    public static ImageMapPaint? Create(byte[] data, string path, string colors) => FromSettings<ImageMapPaint>(data, path, colors);
    public static ImageMapPaint? LoadLegacy(ZPackage pkg)
    {
        var path = pkg.ReadString();
        if (string.IsNullOrEmpty(path))
            return null;
        return Create(pkg.ReadByteArray(), path);
    }

    public static ImageMapPaint? Create(byte[] data, string colors) => Create(data, "", colors);
    // A world made since 0.10: its legend and tiles (DataKey.TiledMap).
    internal static ImageMapPaint FromBlock(byte[] block) => FromBlock<ImageMapPaint>(block);
    internal static ImageMapPaint FromBlock(System.IO.Stream block) => FromBlock<ImageMapPaint>(block);
    private static readonly string DefaultColors = "";

    public override bool LoadSourceImage() => LoadSourceImageAndColors(DefaultColors);
    // The target is a paint colour.
    protected override void ParseColors()
    {
        Colors = ParseColors(SourceColors, target => Legends.ParseColor32(target));
    }
}

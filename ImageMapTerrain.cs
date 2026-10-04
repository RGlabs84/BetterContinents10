// Modified by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

internal class ImageMapTerrain() : ImageMapColor()
{
    public static ImageMapTerrain? Create(string path) => FromFile<ImageMapTerrain>(path);
    public static ImageMapTerrain? Create(byte[] data, string path, string colors) => FromSettings<ImageMapTerrain>(data, path, colors);
    public static ImageMapTerrain? Create(byte[] data, string colors) => Create(data, "", colors);
    private static readonly string DefaultColors = "Default: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF";

    private static readonly Dictionary<string, Color32?> TerrainGrounds = new() {
        {"default", null},
        {"meadows", new Color32(0, 0, 0, 0)},
        {"blackforest", new Color32(0, 0, 255, 0)},
        {"swamp", new Color32(255, 0, 0, 0)},
        {"mountain", new Color32(0, 255, 0, 0)},
        {"plains", new Color32(0, 0, 0, 255)},
        {"mistlands", new Color32(0, 0, 255, 255)},
        {"ashlands", new Color32(255, 0, 0, 255)},
        {"deepnorth", new Color32(0, 255, 0, 0)},
        {"ocean", new Color32(0, 0, 0, 0)}
    };
    public override bool LoadSourceImage() => LoadSourceImageAndColors(DefaultColors);
    // The target is a ground name (lower-cased in the player's culture, as always) or a paint colour; an empty legend
    // is the default one.
    protected override void ParseColors()
    {
        Colors = ParseColors(SourceColors == "" ? DefaultColors : SourceColors,
            target => TerrainGrounds.TryGetValue(target.Trim().ToLower(), out var color) ? color : Legends.ParseColor32(target));
    }
}

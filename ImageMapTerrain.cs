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
    // A new world's (TerrainNamesEwdGrounds): its legend may name an Expand World Data biome for the ground (NameEwdGrounds).
    public static ImageMapTerrain? Create(string path, bool namesEwdGrounds, bool compact = false) => FromFile<ImageMapTerrain>(path, compact, m => m.namesEwdGrounds = namesEwdGrounds);
    public static ImageMapTerrain? Create(byte[] data, string path, string colors) => FromSettings<ImageMapTerrain>(data, path, colors);
    public static ImageMapTerrain? Create(byte[] data, string colors) => Create(data, "", colors);
    // A world made since 0.10: its legend and tiles (DataKey.TiledMap).
    internal static ImageMapTerrain FromBlock(byte[] block) => FromBlock<ImageMapTerrain>(block);
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
    private bool namesEwdGrounds;

    public override bool LoadSourceImage() => LoadSourceImageAndColors(DefaultColors);
    protected override string PrepareLegend(string legend) => namesEwdGrounds ? NameEwdGrounds(legend) : legend;

    // A ground named by an Expand World Data biome, which the legend's own reading takes for a colour that does not parse
    // (transparent black, Meadows' ground, with a warning). In a new world's legend, read from its file, the name becomes
    // the colour the game paints that biome's ground with (Heightmap.GetBiomeColor, which Expand World Data answers from its
    // yaml), so the legend the world keeps holds a colour: the world, its clients and older builds read it without Expand
    // World Data's names (a client can have Better Continents' settings before them). Every other entry stays as written.
    internal static string NameEwdGrounds(string legend) =>
        string.Join("|", legend.Split('|').Select(entry =>
        {
            var parts = entry.Trim().Split(':');
            if (parts.Length != 2)
                return entry;
            var target = parts[0].Trim();
            if (TerrainGrounds.ContainsKey(target.ToLower()) || target.Contains(',') || SixLabors.ImageSharp.Color.TryParseHex(target, out _)
                || !EWD.TryGetBiome(target, out var biome) || biome == Heightmap.Biome.None)
                return entry;
            var ground = Heightmap.GetBiomeColor(biome);
            var hex = $"{ground.r:X2}{ground.g:X2}{ground.b:X2}{ground.a:X2}";
            BetterContinents.Log($"Terrain map legend: {target} is Expand World Data's biome, its ground colour {hex}.");
            return hex + ":" + parts[1];
        }));

    // The target is a ground name (lower-cased in the player's culture, as always) or a paint colour; an empty legend
    // is the default one.
    protected override void ParseColors()
    {
        Colors = ParseColors(SourceColors == "" ? DefaultColors : SourceColors,
            target => TerrainGrounds.TryGetValue(target.Trim().ToLower(), out var color) ? color : Legends.ParseColor32(target));
    }
}

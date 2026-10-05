// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data locations (0.9.3), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

internal class ImageMapLocation() : ImageMapBase()
{
    public static ImageMapLocation? Create(string path) => Create(path, false);
    // A new world's (ExactLocationPins): every pin lands exactly on its pixel (PinPosition).
    public static ImageMapLocation? Create(string path, bool exactPins)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        ImageMapLocation map = new()
        {
            FilePath = path,
            exactPins = exactPins
        };
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap())
            return null;
        return map;
    }
    public static ImageMapLocation? Create(ZPackage pkg)
    {
        ImageMapLocation map = new();
        map.Deserialize(pkg);
        return map;
    }
    public static ImageMapLocation? Create(ZPackage pkg, string path)
    {
        ImageMapLocation map = new()
        {
            FilePath = path
        };
        map.Deserialize(pkg);
        return map;
    }
    public Dictionary<string, List<Vector2>> RemainingAreas = [];
    private Dictionary<string, Color32> Colors = [];
    private bool exactPins;
    // A reload in a loaded world (MapKind.Reload) reads the picture again by that world's rule; a map read back from a
    // world's settings holds only its pins and was made without it.
    internal bool ExactPins { get => exactPins; set => exactPins = value; }
    // World export (WorldExport): the legend this map was decoded with (empty for a map read back from a world's
    // settings, which stores the chosen positions only).
    internal IReadOnlyDictionary<string, Color32> LegendColors => Colors;

    public override bool LoadSourceImage()
    {
        // spawnmap.png is the creature spawn map (ImageMapSpawn) since 0.7.x: a missing location map is no longer looked
        // for under that name, and nothing beside it is renamed.
        if (!base.LoadSourceImage()) return false;
        var path = Legends.FileFor(FilePath);
        if (!File.Exists(path))
        {
            Legends.WriteDefault(path, DefaultColors);
            Colors = ParseColors(DefaultColors);
            return true;
        }
        try
        {
            var colors = Legends.ReadJoined(path);
            Colors = ParseColors(colors);
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Cannot load file {path}: {ex.Message}.");
            Colors = ParseColors(DefaultColors);
        }
        return true;
    }
    // "Name: colour" entries separated by '|' (the location row of Legends' table); a line starting with '#' is a
    // comment. The name is everything before the last ':' (a colour has none), so Expand World Data's "Name:Alias"
    // locations are kept; before 0.9.3 they were dropped without a word. A name listed twice keeps its last colour, and
    // a colour that does not parse skips only its entry; before, either discarded the whole legend for the default one.
    private static Dictionary<string, Color32> ParseColors(string colors)
    {
        var result = new Dictionary<string, Color32>();
        foreach (var entry in colors.Split('|'))
        {
            if (Legends.IsBlankOrComment(entry))
                continue;
            var line = entry.Trim();
            int colon = line.LastIndexOf(':');
            var name = colon > 0 ? line.Substring(0, colon).Trim() : "";
            var colour = colon > 0 ? line.Substring(colon + 1).Trim() : "";
            if (name == "")
            {
                BetterContinents.LogError($"Location colors: \"{line}\" is not \"name: colour\" (start a note with #), skipped.");
                continue;
            }
            if (!Legends.TryParseColor32(colour, out var parsed))
            {
                BetterContinents.LogError($"Location colors: invalid colour {colour} for {name} (a hex colour like FF0000, or r,g,b), skipped.");
                continue;
            }
            if (result.ContainsKey(name))
                BetterContinents.LogWarning($"Location colors: {name} is listed more than once; its last colour is used.");
            result[name] = parsed;
        }
        return result;
    }

    public override void SerializeLegacy(ZPackage pkg, int version, bool network)
    {
        // File path may contain sensitive information so its removed from network serialization.
        pkg.Write(network ? "?" : FilePath);
        pkg.Write(RemainingAreas.Count);
        foreach (var kv in RemainingAreas)
        {
            pkg.Write(kv.Key);
            pkg.Write(kv.Value.Count);
            foreach (var v in kv.Value)
            {
                pkg.Write(v.x);
                pkg.Write(v.y);
            }
        }
        if (version >= 8)
        {
            var colors = Colors.Select(d => $"{d.Key}:{d.Value.r},{d.Value.g},{d.Value.b},{d.Value.a}");
            pkg.Write(string.Join("|", colors));
        }

    }
    public void Serialize(ZPackage pkg)
    {
        pkg.Write(RemainingAreas.Count);
        foreach (var kv in RemainingAreas)
        {
            pkg.Write(kv.Key);
            pkg.Write(kv.Value.Count);
            foreach (var v in kv.Value)
            {
                pkg.Write(v.x);
                pkg.Write(v.y);
            }
        }
    }

    public static ImageMapLocation? LoadLegacy(ZPackage pkg, int version)
    {
        var path = pkg.ReadString();
        if (string.IsNullOrEmpty(path))
            return null;
        var map = Create(pkg, path);
        // Colors are not needed but if they exist must be read.
        if (version >= 8)
            pkg.ReadString();
        return map;
    }

    public void Deserialize(ZPackage pkg)
    {
        int count = pkg.ReadInt();
        RemainingAreas = [];
        for (int i = 0; i < count; i++)
        {
            var spawn = pkg.ReadString();
            var positionsCount = pkg.ReadInt();
            var positions = new List<Vector2>();
            for (int k = 0; k < positionsCount; k++)
            {
                float x = pkg.ReadSingle();
                float y = pkg.ReadSingle();
                positions.Add(new Vector2(x, y));
            }
            RemainingAreas.Add(spawn, positions);
        }
    }

    // Internal for WorldExport, which gives each location its default colour here where it is not shared.
    internal static readonly string DefaultColors = "StartTemple: 255,0,0|Eikthyrnir: 255,153,0|GDKing: 0,255,0|GoblinKing: 255,255,0|Bonemass: 0,255,255|Dragonqueen: 74,134,232|Vendor_BlackForest: 0,0,255|AbandonedLogCabin02: 230,184,175|AbandonedLogCabin03: 230,184,175|AbandonedLogCabin04: 230,184,175|TrollCave02: 201,218,248|Crypt2: 255,242,204|Crypt3: 255,242,204|Crypt4: 255,242,204|SunkenCrypt4: 69,129,142|Dolmen03: 255,229,153|Dolmen01: 255,229,153|Dolmen02: 255,229,153|Ruin3: 221,126,107|StoneTower1: 204,65,37|StoneTower3: 204,65,37|MountainGrave01: 106,168,79|Grave1: 127,96,96|InfestedTree01: 182,215,168|WoodHouse1: 109,158,235|WoodHouse10: 109,158,235|WoodHouse11: 109,158,235|WoodHouse12: 109,158,235|WoodHouse13: 109,158,235|WoodHouse2: 109,158,235|WoodHouse3: 109,158,235|WoodHouse4: 109,158,235|WoodHouse5: 109,158,235|WoodHouse6: 109,158,235|WoodHouse7: 109,158,235|WoodHouse8: 109,158,235|WoodHouse9: 109,158,235|StoneHouse3: 118,165,175|StoneHouse4: 118,165,175|Meteorite: 147,196,125|StoneTowerRuins04: 166,28,0|StoneTowerRuins05: 166,28,0|SwampRuin1: 19,79,92|SwampRuin2: 19,79,92|Ruin1: 39,78,19|Ruin2: 39,78,19|DrakeLorestone: 133,32,12|Runestone_Boars: 91,15,0|Runestone_Draugr: 79,204,204|Runestone_Greydwarfs: 234,153,153|Runestone_Meadows: 224,102,102|Runestone_Mountains: 204,0,0|Runestone_Plains: 153,0,0|Runestone_Swamps: 102,0,0|ShipSetting01: 208,224,227|ShipWreck01: 252,229,205|ShipWreck02: 252,229,205|ShipWreck03: 252,229,205|ShipWreck04: 252,229,205|GoblinCamp2: 191,144,0|DrakeNest01: 255,217,102|FireHole: 241,194,50|Greydwarf_camp1: 217,228,211|StoneCircle: 162,196,201|StoneHenge1: 249,203,156|StoneHenge2: 249,203,156|StoneHenge3: 249,203,156|StoneHenge4: 249,203,156|StoneHenge5: 249,203,156|StoneHenge6: 249,203,156|StoneTowerRuins03: 246,178,107|StoneTowerRuins07: 246,178,107|StoneTowerRuins08: 246,178,107|StoneTowerRuins09: 246,178,107|StoneTowerRuins10: 246,178,107|SwampHut5: 230,145,56|SwampHut1: 230,145,56|SwampHut2: 230,145,56|SwampHut3: 230,145,56|SwampHut4: 230,145,56|Waymarker01: 164,195,244|Waymarker02: 164,195,244|MountainWell1: 56,118,29|SwampWell1: 12,52,61|WoodFarm1: 180,95,6|WoodVillage1: 120,63,4|Mistlands_DvergrBossEntrance1: 153,0,255|Hildir_camp: 255,105,180";
    public bool CreateMap() => CreateMap<Rgba32>();
    protected override bool LoadTextureToMap<T>(Image<T> image)
    {
        var black = new Color32(0, 0, 0, 255);
        var sw = new Stopwatch();
        sw.Start();
        var img = (Image<Rgba32>)(Image)image;
        var pixels = LoadPixels(img, Convert);
        int Index(int x, int y) => y * Size + x;

        bool Compare(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;
        Queue<Vector2i> q = new();

        void FloodFill(int x, int y, Action<int, int> fillfn)
        {
            var sourceColor = pixels[Index(x, y)];
            bool CheckValidity(int xc, int yc) => xc >= 0 && xc < Size && yc >= 0 && yc < Size && Compare(pixels[Index(xc, yc)], sourceColor);

            q.Clear();

            void Enqueue(int xa, int ya)
            {
                pixels[Index(xa, ya)] = black;
                q.Enqueue(new Vector2i(xa, ya));
            }

            void EnqueueIfValid(int xa, int ya)
            {
                if (CheckValidity(xa, ya))
                    Enqueue(xa, ya);
            }

            Enqueue(x, y);

            while (q.Count > 0)
            {
                var point = q.Dequeue();
                var x1 = point.x;
                var y1 = point.y;
                if (q.Count > Size * Size)
                {
                    throw new Exception($"Flood fill on spawn location failed:  started at pixel {x}, {Size - y}, color #{ColorUtility.ToHtmlStringRGB(sourceColor)}");
                }

                fillfn(x1, y1);

                EnqueueIfValid(x1 + 1, y1 + 0);
                EnqueueIfValid(x1 - 1, y1 + 0);
                EnqueueIfValid(x1 + 0, y1 + 1);
                EnqueueIfValid(x1 + 0, y1 - 1);
            }
        }

        // Determine the spawn positions by color first
        var colorSpawns = new Dictionary<Color32, List<Vector2>>(new Color32Comparer());
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; ++x)
            {
                int i = Index(x, y);
                var color = pixels[i];
                if (!color.Equals(black))
                {
                    var area = new List<Vector2Int>();

                    // Do this AFTER determining the SpawnColorMapping, as it changes the color in pixels to black
                    FloodFill(x, y, (fx, fy) => area.Add(new Vector2Int(fx, fy)));

                    if (!colorSpawns.TryGetValue(color, out var areas))
                    {
                        areas = [];
                        colorSpawns.Add(color, areas);
                    }

                    // Just select the actual position from the area now, there is no point delaying this until later
                    var position = exactPins ? PinPosition(Middle(area)) : Corner(area[PickIndex(area.Count)]);
                    areas.Add(position);
                    BetterContinents.Log($"Found #{ColorUtility.ToHtmlStringRGB(color)} area of {area.Count} size at {x}, {Size - y}, selected position {position.x}, {position.y}");
                }
            }
        }

        // Now we need to divvy up the color spawn areas between the associated spawn types. A new world deals them out
        // with its own seeded random, so the same map gives the same world every time.
        var dealer = exactPins ? new System.Random(PinSeed) : null;
        RemainingAreas = [];
        foreach (var colorPositions in colorSpawns)
        {
            var locations = Colors.Where(d => Compare(d.Value, colorPositions.Key)).ToList();
            if (locations.Count > 0)
            {
                foreach (var position in colorPositions.Value)
                {
                    var location = locations[dealer?.Next(locations.Count) ?? PickIndex(locations.Count)].Key;
                    if (!RemainingAreas.TryGetValue(location, out var positions))
                    {
                        positions = [];
                        RemainingAreas.Add(location, positions);
                    }

                    positions.Add(position);
                    BetterContinents.Log($"Selected {location} for spawn position {position.x}, {position.y}");
                }
            }
            else
            {
                BetterContinents.Log($"No spawns are mapped to color #{ColorUtility.ToHtmlStringRGB(colorPositions.Key)} (which has {colorPositions.Value.Count} spawn positions defined)");
            }
        }

        BetterContinents.Log($"Time to calculate spawns from {FilePath}: {sw.ElapsedMilliseconds} ms");

        return true;
    }

    // As always: the corner of the pixel, i / size across the map (up to a pixel south-west of where the other maps put
    // it, ImageMapFloat.Sample).
    private Vector2 Corner(Vector2Int pixel) => new(pixel.x / (float)Size, pixel.y / (float)Size);

    // Exactly where the height and biome maps put the pixel: i / (size - 1) across the map, so a map of 10501 pixels
    // over 21000 m puts its pins on the same 2 m grid as its heights.
    internal Vector2 PinPosition(Vector2Int pixel) =>
        Size > 1 ? new Vector2(pixel.x / (float)(Size - 1), pixel.y / (float)(Size - 1)) : new Vector2(0.5f, 0.5f);

    // The pixel of a pin nearest its middle (the first of those as near, in the order the pin was filled).
    internal static Vector2Int Middle(List<Vector2Int> area)
    {
        double sx = 0, sy = 0;
        foreach (var p in area)
        {
            sx += p.x;
            sy += p.y;
        }
        double cx = sx / area.Count, cy = sy / area.Count;
        var best = area[0];
        double nearest = double.MaxValue;
        foreach (var p in area)
        {
            double d = (p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy);
            if (d < nearest)
            {
                nearest = d;
                best = p;
            }
        }
        return best;
    }

    // The seed a new world's pins are dealt out with: any fixed number does.
    private const int PinSeed = 0x0BC10;

    // UnityEngine.Random is main-thread only, and a world import decodes this map on a worker (WorldImport), where a
    // thread's own System.Random picks instead. On the main thread the pick is exactly as before. An exported location
    // map has one pixel and one legend name per colour, so its picks are all certain anyway.
    [ThreadStatic] private static System.Random? workerRandom;
    private static int PickIndex(int count) =>
        BetterContinents.IsMainThread ? UnityEngine.Random.Range(0, count) : (workerRandom ??= new System.Random()).Next(count);

    public Vector2? FindSpawn(string spawn)
    {
        if (RemainingAreas.TryGetValue(spawn, out var positions))
        {
            int idx = UnityEngine.Random.Range(0, positions.Count);
            var position = positions[idx];
            positions.RemoveAt(idx);
            if (positions.Count == 0)
            {
                RemainingAreas.Remove(spawn);
            }
            return position;
        }
        return null;
    }

    public IEnumerable<Vector2> GetAllSpawns(string spawn) => RemainingAreas.TryGetValue(spawn, out var positions) ? positions : Enumerable.Empty<Vector2>();
}

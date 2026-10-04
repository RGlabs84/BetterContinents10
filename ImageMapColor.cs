// Modified by Wubarrk on 2026-09-24 for world export and import (0.9.0), and on 2026-10-04 for the unifying refactor (0.10.0).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace BetterContinents;

abstract class ImageMapColor() : ImageMapBase()
{

    private Color32?[] Map = [];
    public string SourceColors = "";
    protected Dictionary<Rgba32, Color32?> Colors = [];

    public bool CreateMap() => CreateMap<Rgba32>();

    // A paint or terrain map from its picture and legend file (bc fn, a new world); setup runs before the files are read.
    protected static T? FromFile<T>(string path, Action<T>? setup = null) where T : ImageMapColor, new()
    {
        if (string.IsNullOrEmpty(path))
            return null;
        T map = new()
        {
            FilePath = path,
        };
        setup?.Invoke(map);
        if (!map.LoadSourceImage())
            return null;
        if (!map.CreateMap())
            return null;
        return map;
    }
    // A paint or terrain map from a world's settings: the picture's bytes and the legend as the file read it then.
    protected static T? FromSettings<T>(byte[] data, string path, string colors) where T : ImageMapColor, new()
    {
        T map = new()
        {
            FilePath = path,
            SourceData = data,
            SourceColors = colors
        };
        map.ParseColors();
        if (!map.CreateMap())
            return null;
        return map;
    }

    protected bool LoadSourceImageAndColors(string defaultColors)
    {
        SourceColors = "";
        if (!base.LoadSourceImage()) return false;
        var path = Legends.FileFor(FilePath);
        // A missing legend is written out, but SourceColors stays empty: a world's settings keep "" (terrain then
        // reads its default legend again).
        if (!File.Exists(path))
        {
            Legends.WriteDefault(path, defaultColors);
            ParseColors();
            return true;
        }
        try
        {
            SourceColors = PrepareLegend(Legends.ReadJoined(path));
            ParseColors();
        }
        catch (Exception ex)
        {
            BetterContinents.LogError($"Cannot load file {path}: {ex.Message}.");
        }
        return true;
    }

    protected override bool LoadTextureToMap<T>(Image<T> image)
    {
        var st = new Stopwatch();
        st.Start();

        var img = (Image<Rgba32>)(Image)image;
        Map = LoadPixels(img, pixel =>
        {
            if (Colors.TryGetValue(pixel, out var color))
                return color;
            return new Color32(pixel.R, pixel.G, pixel.B, pixel.A);
        });

        BetterContinents.Log($"Time to calculate colors from {FilePath}: {st.ElapsedMilliseconds} ms");
        return true;
    }

    internal override void ReleasePixels() => Map = [];

    protected virtual void ParseColors()
    {
        Colors = [];
    }

    // The paint and terrain legend (their rows of Legends' table): "target: image colour" entries separated by '|', only
    // entries with exactly one ':' read. The image colour is read first (its warning comes first), then the target;
    // the first entry for an image colour wins. A broken number throws out of here.
    // The legend as read from its file, as the map keeps it (and a world's settings save it).
    protected virtual string PrepareLegend(string legend) => legend;

    protected static Dictionary<Rgba32, Color32?> ParseColors(string colors, Func<string, Color32?> target) =>
        colors.Split('|')
        .Select(s => s.Trim().Split(':')).Where(s => s.Length == 2)
        .Select(s => Tuple.Create(Legends.ParseRGBA(s[1]), target(s[0])))
        .Distinct(new FirstImageColour())
        .ToDictionary(s => s.Item1, s => s.Item2);

    private class FirstImageColour : IEqualityComparer<Tuple<Rgba32, Color32?>>
    {
        public bool Equals(Tuple<Rgba32, Color32?> x, Tuple<Rgba32, Color32?> y) => x.Item1.Equals(y.Item1);
        public int GetHashCode(Tuple<Rgba32, Color32?> obj) => obj.Item1.GetHashCode();
    }

    public bool TryGetValue(float x, float y, out UnityEngine.Color color)
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

        var p00 = Map[y0 * Size + x0];
        var p10 = Map[y0 * Size + x1];
        var p01 = Map[y1 * Size + x0];
        var p11 = Map[y1 * Size + x1];
        if (p00 == null || p10 == null || p01 == null || p11 == null)
        {
            color = UnityEngine.Color.black;
            return false;
        }

        var a = Color32.Lerp(p00.Value, p10.Value, xd);
        var b = Color32.Lerp(p01.Value, p11.Value, xd);
        var c = Color32.Lerp(a, b, yd);
        color = new UnityEngine.Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
        return true;
    }
}

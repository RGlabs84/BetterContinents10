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

internal class ImageMapSpawn() : ImageMapBase
{
  public static ImageMapSpawn? Create(string path, bool compact = false)
  {
    if (string.IsNullOrEmpty(path))
      return null;
    ImageMapSpawn map = new()
    {
      FilePath = path,
      Compact = compact
    };
    if (!map.LoadSourceImage())
      return null;

    if (!map.CreateMap())
      return null;
    return map;
  }
  public static ImageMapSpawn? Create(ZPackage pkg, string path)
  {
    ImageMapSpawn map = new()
    {
      FilePath = path
    };
    map.Deserialize(pkg);
    return map;
  }

  // Every pixel's legend index (255 = nothing), in tiles (MapTiles.cs); bytes past the square of a saved map whose
  // length is no square number in tail, read only where a pixel past the map's edge lands on them (Index).
  private ByteGrid? grid;
  private byte[] tail = [];
  private readonly List<Color32> Colors = [];
  private readonly List<SpawnEntry> Entries = [];
  // World export (WorldExport): the legend index per pixel (row 0 = south, 255 = nothing) and the legend itself,
  // index 0 being the hardcoded white "none". Enough to write the map back out when the source image is gone.
  // Indices is a new array each time.
  internal byte[] Indices
  {
    get
    {
      var indices = new byte[Length];
      if (grid != null)
        for (int y = 0; y < Size; y++)
          grid.CopyRow(y, indices, y * Size);
      Array.Copy(tail, 0, indices, Size * Size, tail.Length);
      return indices;
    }
  }
  internal IReadOnlyList<Color32> LegendColors => Colors;
  internal IReadOnlyList<SpawnEntry> LegendEntries => Entries;

  private int Length => grid == null ? 0 : Size * Size + tail.Length;

  // The index at i, counted row by row as the map was one array: as before, a pixel past the right edge reads the start
  // of the next row, and one past the whole map throws.
  private byte Index(int i)
  {
    if ((uint)i >= (uint)Length)
      throw new IndexOutOfRangeException();
    int square = Size * Size;
    return i >= square ? tail[i - square] : grid!.Get(i % Size, i / Size);
  }

  // Builds the tiles from the indices as saved (in the pictures' format: every tile decoded).
  private void SetIndices(byte[] indices)
  {
    Size = (int)Math.Sqrt(indices.Length);
    grid = Size > 0 ? ByteGrid.FromBytes(Size, indices, null, compact: false) : null;
    tail = [.. indices.Skip(Size * Size)];
  }

  // A map read from a world's settings has no picture, and its source is the indices as saved, as it always was (a world
  // export writes them out as a picture with the legend).
  internal override byte[] SourceBytes() => SourceData.Length > 0 || grid == null ? SourceData : Indices;


  public override bool LoadSourceImage()
  {
    Colors.Clear();
    Entries.Clear();

    // White is hardcoded to disable everything.
    Colors.Add(new Color32(255, 255, 255, 255));
    Entries.Add(new SpawnEntry("none"));

    if (!base.LoadSourceImage()) return false;
    var path = Legends.FileFor(FilePath);
    // A missing legend is written empty (not as an empty '|' legend would be, a single line break).
    if (!File.Exists(path))
    {
      File.WriteAllText(path, "");
      return true;
    }
    try
    {
      var lines = File.ReadAllLines(path);
      foreach (var line in lines)
      {
        if (line == "") continue;
        var trimmed = line.Trim();
        if (trimmed.StartsWith("#")) continue;
        var parts = trimmed.Split(':');
        if (parts.Length != 2) continue;
        var color = Legends.ParseColor32(parts[0]);
        var spawn = parts[1].Trim();
        Colors.Add(color);
        Entries.Add(new SpawnEntry(spawn));
      }
    }
    catch (Exception ex)
    {
      BetterContinents.LogError($"Cannot load file {path}: {ex.Message}.");
    }

    return true;
  }
  public void Deserialize(ZPackage pkg)
  {
    ReadLegend(pkg);
    SetIndices(pkg.ReadByteArray());
  }

  private void ReadLegend(ZPackage pkg)
  {
    Colors.Clear();
    Entries.Clear();

    int count = pkg.ReadInt();
    for (int i = 0; i < count; i++)
    {
      var r = pkg.ReadByte();
      var g = pkg.ReadByte();
      var b = pkg.ReadByte();
      var a = pkg.ReadByte();
      var color = new Color32(r, g, b, a);
      var spawn = pkg.ReadString();

      BetterContinents.Log($"Loaded spawn color {color} => {spawn}");
      Colors.Add(color);
      Entries.Add(new SpawnEntry(spawn));
    }
  }


  public bool CreateMap() => CreateMap<Rgba32>();

  public void Serialize(ZPackage pkg)
  {
    pkg.Write(Colors.Count);
    for (int i = 0; i < Colors.Count; i++)
    {
      var color = Colors[i];
      pkg.Write(color.r);
      pkg.Write(color.g);
      pkg.Write(color.b);
      pkg.Write(color.a);
      var entry = Entries[i];
      pkg.Write(entry.Data);
    }
    pkg.Write(Indices);
  }

  // ---- a world made since 0.10 saves and sends its tiles (DataKey.TiledMap) -------------------------------------------

  private const byte BlockVersion = 1;

  // The legend as Serialize writes it, then the indices in tiles and the bytes past the square.
  internal byte[] ToBlock()
  {
    var block = grid?.Block ?? throw new InvalidOperationException($"{FilePath} holds no compressed tiles to save");
    var legend = new ZPackage();
    legend.Write(Colors.Count);
    for (int i = 0; i < Colors.Count; i++)
    {
      var color = Colors[i];
      legend.Write(color.r);
      legend.Write(color.g);
      legend.Write(color.b);
      legend.Write(color.a);
      legend.Write(Entries[i].Data);
    }
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(BlockVersion);
    var legendBytes = legend.GetArray();
    writer.Write(legendBytes.Length);
    writer.Write(legendBytes);
    block.WriteTo(writer);
    writer.Write(tail.Length);
    writer.Write(tail);
    writer.Flush();
    return stream.ToArray();
  }

  internal static ImageMapSpawn FromBlock(byte[] block)
  {
    using var reader = new BinaryReader(new MemoryStream(block, false));
    var version = reader.ReadByte();
    if (version != BlockVersion)
      throw new InvalidDataException($"a spawn or vegetation map saved in format {version}, which this version of Better Continents cannot read");
    int legendLength = reader.ReadInt32();
    if (legendLength < 0 || legendLength > reader.BaseStream.Length - reader.BaseStream.Position)
      throw new InvalidDataException("a spawn or vegetation map ends early");
    var map = new ImageMapSpawn();
    var legend = new ZPackage(reader.ReadBytes(legendLength));
    map.ReadLegend(legend);
    var tiles = TileBlock.ReadFrom(reader);
    if (tiles.Channels != 1 || tiles.Bytes != 1)
      throw new InvalidDataException("a spawn or vegetation map's tiles are not one byte a pixel");
    int tailLength = reader.ReadInt32();
    if (tailLength < 0 || tailLength > reader.BaseStream.Length - reader.BaseStream.Position)
      throw new InvalidDataException("a spawn or vegetation map ends early");
    map.grid = new ByteGrid(tiles);
    map.tail = reader.ReadBytes(tailLength);
    map.Size = tiles.Size;
    map.Compact = true;
    return map;
  }

  public SpawnEntry? GetEntry(float x, float y)
  {
    if (Length == 0) return null;
    float xa = x * (Size - 1);
    float ya = y * (Size - 1);

    int xi = Mathf.RoundToInt(xa);
    int yi = Mathf.RoundToInt(ya);
    var index = Index(yi * Size + xi);

    if (index >= Entries.Count) return null;
    return Entries[index];
  }

  public void LoadPrefabs(ZNetScene scene)
  {
    foreach (var entry in Entries)
      entry.LoadPrefabs(scene);
  }

  protected override bool LoadTextureToMap<T>(Image<T> image)
  {
    var st = new Stopwatch();
    st.Start();

    var img = (Image<Rgba32>)(Image)image;
    var colorToIndex = new Dictionary<Rgba32, int>();

    // Build color to index mapping
    for (int i = 0; i < Colors.Count; i++)
    {
      var c = Colors[i];
      colorToIndex[new(c.r, c.g, c.b, c.a)] = i;
    }

    bool warned = false;
    byte IndexOf(Rgba32 pixel)
    {
      // Black color always means nothing is done.
      if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0 && pixel.A == 255)
        return (byte)255;

      if (colorToIndex.TryGetValue(pixel, out var index))
        return (byte)index;
      else
      {
        if (!warned)
        {
          warned = true;
          BetterContinents.LogWarning($"{Path.GetFileName(FilePath)}: Unknown color {pixel} found in the image.");
        }
        return (byte)255;
      }
    }
    grid = ByteGrid.From(Rows(img, 1, false, (row, band, at) =>
    {
      for (int x = 0; x < row.Length; x++)
        band[at + x] = IndexOf(row[x]);
    }), Compact);
    tail = [];

    BetterContinents.Log($"Time to calculate colors from {FilePath}: {st.ElapsedMilliseconds} ms");
    return true;
  }
}

internal class SpawnEntry
{
  private readonly HashSet<string> Enabled = [];
  private readonly HashSet<string> Disabled = [];
  private readonly HashSet<string> Excluded = [];
  private bool Reset = false;
  public readonly string Data;

  public SpawnEntry(string data)
  {
    Data = data;
    // On world load, ZNetScene is not loaded yet and will be handled later.
    // On image reload, ZNetScene is already loaded.
    // A world import reads the legend on a worker, where prefab names cannot be read; its preset stores only the text.
    if (BetterContinents.IsMainThread && ZNetScene.instance)
      LoadPrefabs(ZNetScene.instance);
  }

  public bool HasEnabled(string name) => Enabled.Contains(name);

  public bool HasDisabled(string name) => Disabled.Contains(name) || (Reset && !Excluded.Contains(name) && !Enabled.Contains(name));

  private enum Operation
  {
    Enable,
    Disable,
    Exclude
  }
  public void LoadPrefabs(ZNetScene scene)
  {
    var parts = Data.Split(',').Select(s => s.Trim());
    Reset = false;
    foreach (var part in parts)
    {
      if (part == "none")
      {
        Reset = true;
        continue;
      }
      var op = Operation.Exclude;
      var prefab = part;
      if (prefab.StartsWith("-"))
      {
        op = Operation.Disable;
        prefab = prefab.Substring(1);
      }
      if (prefab.StartsWith("+"))
      {
        op = Operation.Enable;
        prefab = prefab.Substring(1);
      }
      var prefabs = GetPrefabs(scene, prefab);
      foreach (var name in prefabs)
      {
        Enabled.Remove(name);
        Disabled.Remove(name);
        Excluded.Remove(name);
        switch (op)
        {
          case Operation.Enable:
            Enabled.Add(name);
            break;
          case Operation.Disable:
            Disabled.Add(name);
            break;
          case Operation.Exclude:
            Excluded.Add(name);
            break;
        }
      }
    }
  }
  private IEnumerable<string> GetPrefabs(ZNetScene scene, string name)
  {
    // Check if this is a wildcard pattern
    if (name.Contains("*"))
    {
      // Find all matching prefabs using wildcard pattern
      var matches = new List<string>();
      foreach (var item in scene.m_namedPrefabs.Values)
      {
        if (MatchesWildcard(item.name, name))
          matches.Add(item.name);
      }
      // Return matches if any found, otherwise return the original pattern
      return matches.Count > 0 ? matches : new[] { name };
    }

    // First try exact match
    if (scene.GetPrefab(name)) return [name];

    // Try case-insensitive exact match for non-wildcard names
    foreach (var item in scene.m_namedPrefabs.Values)
    {
      if (item.name.Equals(name, StringComparison.OrdinalIgnoreCase))
        return new[] { item.name };
    }

    // Return original name if no match found
    return [name];
  }

  private bool MatchesWildcard(string text, string pattern)
  {
    // Handle simple cases
    if (pattern == "*") return true;
    if (!pattern.Contains("*")) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    var parts = pattern.Split('*');

    // Case 1: *substring* (contains)
    if (pattern.StartsWith("*") && pattern.EndsWith("*") && parts.Length == 3 && parts[0] == "" && parts[2] == "")
    {
      return text.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Case 2: *suffix (ends with)
    if (pattern.StartsWith("*"))
    {
      return text.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
    }

    // Case 3: prefix* (starts with)
    if (pattern.EndsWith("*"))
    {
      return text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase);
    }

    // Case 4: prefix*suffix (starts with prefix and ends with suffix)
    if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
    {
      return text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) &&
             text.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase) &&
             text.Length >= parts[0].Length + parts[1].Length;
    }

    // Case 5: More complex patterns - not supported.
    return false;
  }


}
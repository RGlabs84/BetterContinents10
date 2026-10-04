// Added by Wubarrk on 2026-10-04 for the unifying refactor.

using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GoldenTest;

// Deterministic map fixtures: every pixel is a fixed function of its position, so the files are the same on every run.
// N is small (the suite samples functions, it does not need big maps) but not a power of two, so no grid lines up by luck.
internal static class Fixtures
{
  public const int N = 96;

  static readonly PngEncoder Grey16 = new() { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 };
  static readonly PngEncoder GreyAlpha16 = new() { ColorType = PngColorType.GrayscaleWithAlpha, BitDepth = PngBitDepth.Bit16 };
  static readonly PngEncoder Grey8 = new() { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 };
  static readonly PngEncoder Rgba = new() { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };

  static float U(int x) => x / (N - 1f);

  // An island with ripples, 0..1.
  public static float Height(int x, int y)
  {
    float u = U(x) - 0.5f, w = U(y) - 0.5f;
    return Math.Clamp(0.48f - MathF.Sqrt(u * u + w * w) * 0.85f + 0.07f * MathF.Sin(x * 0.21f) * MathF.Cos(y * 0.17f), 0f, 1f);
  }

  public static void Heightmap(string path, bool alpha = false)
  {
    if (alpha)
    {
      // 16-bit grey with 16-bit alpha, as an editor saves a heightmap with transparency.
      using var image = new Image<La32>(N, N);
      for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
          image[x, y] = new La32((ushort)(Height(x, y) * 65535f), (ushort)(U(x) * 65535f));
      image.Save(path, GreyAlpha16);
      return;
    }
    using var img = new Image<L16>(N, N);
    for (int y = 0; y < N; y++)
      for (int x = 0; x < N; x++)
        img[x, y] = new L16((ushort)(Height(x, y) * 65535f));
    img.Save(path, Grey16);
  }

  public static void Grey16Map(string path, Func<int, int, float> value)
  {
    using var img = new Image<L16>(N, N);
    for (int y = 0; y < N; y++)
      for (int x = 0; x < N; x++)
        img[x, y] = new L16((ushort)(Math.Clamp(value(x, y), 0f, 1f) * 65535f));
    img.Save(path, Grey16);
  }

  public static void Grey8Map(string path, Func<int, int, float> value)
  {
    using var img = new Image<L8>(N, N);
    for (int y = 0; y < N; y++)
      for (int x = 0; x < N; x++)
        img[x, y] = new L8((byte)(Math.Clamp(value(x, y), 0f, 1f) * 255f));
    img.Save(path, Grey8);
  }

  public static void ColourMap(string path, Func<int, int, Rgba32> colour, int size = N)
  {
    using var img = new Image<Rgba32>(size, size);
    for (int y = 0; y < size; y++)
      for (int x = 0; x < size; x++)
        img[x, y] = colour(x, y);
    img.Save(path, Rgba);
  }

  static Rgba32 C(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);

  // Biomes in bands and quadrants of the default legend colours, an ocean rim, a None patch, colours a little off the
  // palette (nearest-colour matching) and a patch in a colour whose legend name this game does not know.
  public static Rgba32 Biome(int x, int y)
  {
    float u = U(x) - 0.5f, w = U(y) - 0.5f;
    if (u * u + w * w > 0.2f) return C(0x0000FF);
    if (x > 40 && x < 48 && y > 40 && y < 48) return C(0x000000);
    if (x > 60 && x < 66 && y > 20 && y < 30) return C(0x8B4513);
    if ((x + y) % 23 == 0) return C(0x10F010);
    bool west = x < N / 2, north = y < N / 2;
    if (north && west) return y < 20 ? C(0xFFFFFF) : C(0x00FF00);
    if (north) return x > 80 ? C(0x00FFFF) : C(0x007F00);
    if (west) return x < 12 ? C(0x7F7F00) : C(0xFFFF00);
    return y > 80 ? C(0xFF0000) : C(0x7F7F7F);
  }

  public const string BiomeLegend =
    "# golden fixture legend\n" +
    "Ocean: 0000FF\nMeadows: 00FF00\nBlack Forest: 007F00\nSwamp: 7F7F00\nMountain: FFFFFF\nPlains: FFFF00\nMistlands: 7F7F7F\n" +
    "AshLands: FF0000\nDeepNorth: 00FFFF\nNone: 000000\n\nDeadWastes: 8B4513\n";

  public static Rgba32 Paint(int x, int y) => ((x / 12 + y / 12) % 4) switch
  {
    0 => C(0xFF0000),
    1 => C(0x00FF00, 128),
    2 => C(0x102030),
    _ => C(0x000000, 0),
  };
  // "mask colour: image colour"
  public const string PaintLegend = "80808080: FF0000\nFF000000: 102030\n";

  public static Rgba32 Terrain(int x, int y) => ((x / 16 + 2 * (y / 16)) % 5) switch
  {
    0 => C(0x00FF00),
    1 => C(0x0000FF),
    2 => C(0xFFFFFF),
    3 => C(0x123456),
    _ => C(0x000000),
  };
  // "ground (or mask colour): image colour"
  public const string TerrainLegend = "Meadows: 00FF00\nOcean: 0000FF\nMountain: FFFFFF\nFF8800FF: 123456\nDefault: 000000\n";

  // Single-pixel locations, one colour per name, on black.
  public static readonly (string Name, uint Colour, int X, int Y)[] Locations =
  [
    ("StartTemple", 0xFF0000, 48, 48),
    ("Eikthyrnir", 0xFF9900, 30, 60),
    ("Vendor_BlackForest", 0x0000FF, 70, 22),
    ("Runestone_Meadows:alias", 0x00FF80, 20, 30),
    ("Hildir_camp", 0xFF69B4, 75, 75),
  ];
  public static Rgba32 Location(int x, int y)
  {
    foreach (var l in Locations)
      if (l.X == x && l.Y == y) return C(l.Colour);
    // A two-pixel blob of a colour no legend line names.
    if (y == 10 && (x == 10 || x == 11)) return C(0xABCDEF);
    return C(0x000000);
  }
  public static string LocationLegend()
  {
    var s = "# golden locations\n";
    foreach (var l in Locations)
      s += $"{l.Name}: {(l.Colour >> 16) & 255},{(l.Colour >> 8) & 255},{l.Colour & 255}\n";
    return s;
  }

  public static Rgba32 Spawn(int x, int y) => ((x / 24 + y / 24) % 3) switch
  {
    0 => C(0xFF0000),
    1 => C(0x00FF00),
    _ => C(0x000000),
  };
  public const string VegetationLegend = "# vegetation\n255,0,0,255: -FirTree, +Beech1\n0,255,0,255: none, +Pinetree_01\n";
  public const string SpawnLegend = "255,0,0,255: -Greyling, +Neck\n0,255,0,255: none\n";

  public static Rgba32 AltBiome(int x, int y) => ((x / 20 + 3 * (y / 20)) % 5) switch
  {
    0 => C(0x6A8CA0),
    1 => C(0x3C5A28),
    2 => C(0xA05A3C),
    3 => C(0x101010),
    _ => C(0x000000),
  };
  public const string AltBiomeLegend =
    "# golden alt biomes\nWolf Mountain: 6A8CA0\nDark Meadows + Lantern: 3C5A28\n!Drake Mountain: A05A3C\nnone: 101010\nWolf Mountain: at 100, -200\n";

  // A folder of every map Better Continents loads by its standard name (the Directory setting).
  public static void FullFolder(string dir)
  {
    Directory.CreateDirectory(dir);
    Heightmap(Path.Combine(dir, "heightmap.png"));
    ColourMap(Path.Combine(dir, "biomemap.png"), Biome);
    File.WriteAllText(Path.Combine(dir, "biomemap.txt"), BiomeLegend);
    Grey16Map(Path.Combine(dir, "roughmap.png"), (x, y) => U(x));
    Grey16Map(Path.Combine(dir, "forestmap.png"), (x, y) => 0.5f + 0.5f * MathF.Sin(x * 0.3f + y * 0.11f));
    Grey16Map(Path.Combine(dir, "heatmap.png"), (x, y) => y > 70 ? (y - 70) / 25f : 0f);
    Grey8Map(Path.Combine(dir, "lavamap.png"), (x, y) => (x * 7 + y * 3) % 11 / 10f);
    Grey8Map(Path.Combine(dir, "mossmap.png"), (x, y) => (x * 5 + y * 9) % 13 / 12f);
    ColourMap(Path.Combine(dir, "paintmap.png"), Paint);
    File.WriteAllText(Path.Combine(dir, "paintmap.txt"), PaintLegend);
    ColourMap(Path.Combine(dir, "terrainmap.png"), Terrain);
    File.WriteAllText(Path.Combine(dir, "terrainmap.txt"), TerrainLegend);
    ColourMap(Path.Combine(dir, "locationmap.png"), Location);
    File.WriteAllText(Path.Combine(dir, "locationmap.txt"), LocationLegend());
    ColourMap(Path.Combine(dir, "vegetationmap.png"), Spawn);
    File.WriteAllText(Path.Combine(dir, "vegetationmap.txt"), VegetationLegend);
    ColourMap(Path.Combine(dir, "spawnmap.png"), Spawn);
    File.WriteAllText(Path.Combine(dir, "spawnmap.txt"), SpawnLegend);
    ColourMap(Path.Combine(dir, "altbiomemap.png"), AltBiome);
    File.WriteAllText(Path.Combine(dir, "altbiomemap.txt"), AltBiomeLegend);
  }
}

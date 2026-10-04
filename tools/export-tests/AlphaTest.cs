// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

// Heightmap Alpha, fixed for new worlds (settings version 12). An older world reads an alpha heightmap as 8-bit grey with
// 8-bit alpha and never uses the alpha (ImageMapFloat.HeightAlpha.Legacy), as before; a new one reads 16-bit grey with
// 16-bit alpha (Blend), and GetBaseHeight blends Better Continents' height with the game's own by the alpha. Checked: the
// three ways a heightmap is read, which one a world gets, the settings saved and read back, and the blend prefix and
// postfix as Harmony calls them (the game's own formula stood in for by a value, as it cannot run offline).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BetterContinents;
using BC = BetterContinents.BetterContinents;
using Alpha = BetterContinents.ImageMapFloat.HeightAlpha;

namespace ExportTest;

internal static class AlphaTest
{
  static void C(bool ok, string what) => Program.C(ok, what);
  static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

  // 2 x 2 pixels, transparent on the left (x 0), opaque on the right (x 1); greys that 8 bits cannot hold.
  static readonly ushort[] Grey = [1000, 40000, 20000, 65535];
  static readonly ushort[] Alphas = [0, 65535, 0, 65535];

  static byte[] Png(bool alpha)
  {
    using var stream = new MemoryStream();
    if (alpha)
    {
      using var image = new Image<La32>(2, 2);
      for (int i = 0; i < 4; i++)
        image[i % 2, i / 2] = new La32(Grey[i], Alphas[i]);
      image.Save(stream, new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha, BitDepth = PngBitDepth.Bit16 });
    }
    else
    {
      using var image = new Image<L16>(2, 2);
      for (int i = 0; i < 4; i++)
        image[i % 2, i / 2] = new L16(Grey[i]);
      image.Save(stream, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit16 });
    }
    return stream.ToArray();
  }

  static readonly FieldInfo HeightMapField = typeof(BC.BetterContinentsSettings).GetField("HeightMap", BindingFlags.NonPublic | BindingFlags.Instance)!;

  static BC.BetterContinentsSettings World(int version, bool alpha)
  {
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = version, HeightMapAlpha = alpha };
    HeightMapField.SetValue(s, ImageMapFloat.Create(Png(alpha), s.HeightmapAlphaMode));
    return s;
  }

  public static void Run()
  {
    System.Console.WriteLine("== Heightmap Alpha (a new world's: full precision, blended with the game's own terrain)");
    var corners = new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) };
    var grey = ImageMapFloat.Create(Png(false), Alpha.None)!;
    var legacy = ImageMapFloat.Create(Png(true), Alpha.Legacy)!;
    var blend = ImageMapFloat.Create(Png(true), Alpha.Blend)!;
    C(corners.All(p => Same(blend.GetValue(p.Item1, p.Item2), grey.GetValue(p.Item1, p.Item2))),
      "read to blend, the heights are the 16-bit greys, float for float as a plain grey heightmap reads them");
    C(corners.Any(p => !Same(legacy.GetValue(p.Item1, p.Item2), grey.GetValue(p.Item1, p.Item2)))
      && corners.All(p => Mathf.Abs(legacy.GetValue(p.Item1, p.Item2) - grey.GetValue(p.Item1, p.Item2)) < 1f / 255f),
      "read the old way, the heights keep 8 bits, as before");
    C(!grey.HasAlpha && !legacy.HasAlpha && blend.HasAlpha && legacy.GetAlpha(0f, 0f) == 1f,
      "only the blending heightmap keeps its alpha (the old way never read it)");
    C(blend.GetAlpha(0f, 0f) == 0f && blend.GetAlpha(0f, 1f) == 0f && blend.GetAlpha(1f, 0f) == 1f && blend.GetAlpha(1f, 1f) == 1f
      && Mathf.Abs(blend.GetAlpha(0.5f, 0.5f) - 0.5f) < 1e-6f,
      "its alpha: 0 on the left, 1 on the right, 0.5 between them");

    // Which way a world reads it.
    C(World(11, false).HeightmapAlphaMode == Alpha.None && World(12, false).HeightmapAlphaMode == Alpha.None,
      "Heightmap Alpha off: a grey heightmap, whatever the version");
    var old = World(11, true);
    var fresh = World(12, true);
    C(old.HeightmapAlphaMode == Alpha.Legacy && !old.BlendsHeightmapAlpha && old.HeightmapAlphaAt(0f, 0f) == 1f,
      "Heightmap Alpha on a world made before 0.10 (version 11): read as it always was, no blend");
    C(fresh.HeightmapAlphaMode == Alpha.Blend && fresh.BlendsHeightmapAlpha && fresh.HeightmapAlphaAt(0f, 0f) == 0f,
      "Heightmap Alpha on a world made since 0.10 (version 12): blended");

    // Saved and read back: the alpha key comes before the heightmap, so the map is read the same way again.
    BC.BetterContinentsSettings Back(BC.BetterContinentsSettings s)
    {
      var pkg = new ZPackage();
      s.Serialize(pkg, false, true, s.SavedVersion);
      return BC.BetterContinentsSettings.Load(new ZPackage(pkg.GetArray()));
    }
    var oldBack = Back(old);
    var freshBack = Back(fresh);
    C(oldBack.Version == 11 && oldBack.HeightmapAlphaMode == Alpha.Legacy && !oldBack.BlendsHeightmapAlpha,
      "a version 11 alpha world read back: the old way still");
    C(freshBack.Version == 12 && freshBack.BlendsHeightmapAlpha && freshBack.HeightmapAlphaAt(1f, 0f) == 1f && freshBack.HeightmapAlphaAt(0f, 0f) == 0f,
      "a version 12 alpha world read back: blended, the alpha as it was");

    // bc h alpha in debug mode: the heightmap the world holds is decoded again in its new mode (MapKind.Redecode), from the
    // bytes it holds, so the change shows at once, as the world reads it when it is loaded next.
    BC.BetterContinentsSettings Toggled(int version)
    {
      var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = version, HeightMapAlpha = false };
      HeightMapField.SetValue(s, ImageMapFloat.Create(Png(true), s.HeightmapAlphaMode));
      s.HeightMapAlpha = true;
      BC.BetterContinentsSettings.MapKind.Height.Redecode(s);
      return s;
    }
    bool Reads(BC.BetterContinentsSettings s, ImageMapFloat as_) =>
      HeightMapField.GetValue(s) is ImageMapFloat m && corners.All(p => Same(m.GetValue(p.Item1, p.Item2), as_.GetValue(p.Item1, p.Item2)));
    var on12 = Toggled(12);
    C(on12.BlendsHeightmapAlpha && on12.HeightmapAlphaAt(0f, 0f) == 0f && on12.HeightmapAlphaAt(1f, 0f) == 1f && Reads(on12, blend),
      "switched on live in a version 12 world: blended at once, the heights at full precision");
    var on11 = Toggled(11);
    C(!on11.BlendsHeightmapAlpha && on11.HeightmapAlphaAt(0f, 0f) == 1f && Reads(on11, legacy),
      "switched on live in a version 11 world: read the old way at once (8 bits, no blend), as its next load reads it");
    on12.HeightMapAlpha = false;
    BC.BetterContinentsSettings.MapKind.Height.Redecode(on12);
    C(!on12.BlendsHeightmapAlpha && on12.HeightmapAlphaAt(0f, 0f) == 1f && Reads(on12, grey),
      "switched off again: a plain grey heightmap, no blend");

    // The blend, as Harmony calls it: the prefix answers alone where the heightmap is opaque; elsewhere the game's own
    // formula runs (stood in for here by a value) and the postfix blends.
    var saved = BC.Settings;
    try
    {
      BC.Settings = fresh;
      BC.WorldGeneratorPatch.ApplyNoiseSettings();
      float total = BC.Geometry.TotalSize;
      float Plain(float u, float v)
      {
        float x = (u - 0.5f) * total, y = (v - 0.5f) * total, r = 0f;
        BC.WorldGeneratorPatch.GetBaseHeightPrefixV3(ref x, ref y, ref r, 1000f);
        return r;
      }
      (bool runs, float result) Blended(float u, float v, float game)
      {
        float x = (u - 0.5f) * total, y = (v - 0.5f) * total, r = 0f;
        bool runs = BC.WorldGeneratorPatch.GetBaseHeightPrefixV3Alpha(ref x, ref y, ref r, 1000f, out var state);
        if (runs)
          r = game;
        BC.WorldGeneratorPatch.GetBaseHeightPostfixV3Alpha(ref r, state);
        return (runs, r);
      }
      const float Game = 0.123f;
      var opaque = Blended(1f, 0f, Game);
      C(!opaque.runs && Same(opaque.result, Plain(1f, 0f)), "opaque: Better Continents' height alone, the game's formula skipped");
      var clear = Blended(0f, 1f, Game);
      C(clear.runs && Same(clear.result, Game), "transparent: the game's own height");
      var half = Blended(0.5f, 0.75f, Game);
      float a = fresh.HeightmapAlphaAt(0.5f, 0.75f);
      C(half.runs && a > 0.4f && a < 0.6f && Same(half.result, Mathf.Lerp(Game, Plain(0.5f, 0.75f), a)),
        $"half transparent ({a:0.###}): the two blended by the alpha");
    }
    finally
    {
      BC.Settings = saved;
      BC.WorldGeneratorPatch.ApplyNoiseSettings();
    }
  }
}

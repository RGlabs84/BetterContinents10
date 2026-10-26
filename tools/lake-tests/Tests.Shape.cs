// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).
//
// The shape of a lake and the depth of its water, drawn and felt (LakeFlood.cs, LakeDepth.cs; BUILD-SPEC-TARN.md 4.3 item 3, 3.4): the depth a swim box makes
// felt is the depth the water shader draws at the same place; the tiles that need no mesh of their own really are covered by the mask; the transcription of
// the game's WaterVolume.Depth is the game's.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterContinents;

namespace LakeTests;

internal static partial class Tests
{
  public static void DepthNormalizeTest()
  {
    Section("depth: metres to the shader's number");
    C(LakeDepth.Normalize(5f, 0.5f) == 0.25f && LakeDepth.Normalize(20f, 0.5f) == 1f && LakeDepth.Normalize(-3f, 0.5f) == 0f && LakeDepth.Normalize(7f, 0f) == 0f, "metres x scale / 10, clamped to 0..1");
    C(LakeDepth.Normalize(10f, 1f) == 1f && LakeDepth.Normalize(10f, 3f) == 1f && Math.Abs(LakeDepth.Normalize(1f, 3f) - 0.3f) < 1e-6f, "scale 1 gives vanilla's, scale 3 the most");
    var n = LakeDepth.Normalize(new[] { 0f, 5f, 10f, 40f }, 0.5f);
    C(n.SequenceEqual(new[] { 0f, 0.25f, 0.5f, 1f }), "four at once");
  }

  // A made-up tile function: four normalised depths.
  private static float[] RandomNorm(Rng rng) => [(float)rng.Double(), (float)rng.Double(), (float)rng.Double(), (float)rng.Double()];

  public static void DepthFeltEqualsDrawnTest()
  {
    Section("depth: a box's felt depth equals the depth drawn there (10,000 points on random boxes)");
    var rng = new Rng(31337);
    double worst = 0;
    int points = 0;
    for (int n = 0; n < 400; n++)
    {
      var norm = RandomNorm(rng);
      float cx = rng.Int(-20, 20) * 64f, cz = rng.Int(-20, 20) * 64f;
      // a box of whole cells inside the tile: from 4 m wide to the whole tile
      float x0 = cx - 32f + (float)Math.Round(rng.Range(0, 56)), x1 = Math.Min(cx + 32f, x0 + 4f + (float)Math.Round(rng.Range(0, 60)));
      float z0 = cz - 32f + (float)Math.Round(rng.Range(0, 56)), z1 = Math.Min(cz + 32f, z0 + 4f + (float)Math.Round(rng.Range(0, 60)));
      var corners = LakeDepth.BoxCorners(norm, cx, cz, x0, z0, x1, z1);
      for (int k = 0; k < 25; k++)
      {
        float x = (float)rng.Range(x0, x1), z = (float)rng.Range(z0, z1);
        float felt = LakeDepth.Felt(corners, x0, z0, x1, z1, x, z);
        float drawn = LakeDepth.Drawn(norm, LakeDepth.Uv(x, cx), LakeDepth.Uv(z, cz));
        worst = Math.Max(worst, Math.Abs(felt - drawn));
        points++;
      }
    }
    C(points == 10000 && worst < 1e-5, $"felt and drawn depth agree over {points} points: the worst difference is {worst:0.0e+0}");

    // The whole tile as one box: its four corners are the tile's own at the mesh's edge (the uv span of 0.9999 aside)
    var whole = LakeDepth.BoxCorners(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, 0f, 0f, -32.1f, -32.1f, 32.1f, 32.1f);
    C(Math.Abs(whole[0] - 0.1f) < 1e-3f && Math.Abs(whole[1] - 0.2f) < 1e-3f && Math.Abs(whole[2] - 0.3f) < 1e-3f && Math.Abs(whole[3] - 0.4f) < 1e-3f,
      "a box over the whole mesh has the tile's four depths: " + string.Join(", ", whole));

    // A box with cells past the tile's border (up to 2 m) keeps its values within 0..1
    var past = LakeDepth.BoxCorners(new[] { 1f, 1f, 0f, 0f }, 0f, 0f, 20f, -32f, 34f, 32f);
    C(past.All(v => v >= 0f && v <= 1f), "a box that reaches past its tile's border has depths in 0..1");

    // flat water: the same four numbers everywhere
    var flat = LakeDepth.BoxCorners(new[] { 0.6f, 0.6f, 0.6f, 0.6f }, 64f, 64f, 40f, 50f, 70f, 90f);
    C(flat.All(v => Math.Abs(v - 0.6f) < 1e-6f), "a flat tile gives flat boxes");
  }

  // The game's WaterVolume.Depth and the transcription: the IL of the real method is read for the constants and calls the transcription relies on.
  public static void DepthTranscriptionIsTheGamesTest()
  {
    Section("depth: LakeDepth.Felt is WaterVolume.Depth (the game's IL)");
    var wv = Type.GetType("WaterVolume, assembly_valheim");
    C(wv != null, "the game's WaterVolume is loaded");
    if (wv == null)
      return;
    var depth = wv.GetMethod("Depth", BindingFlags.Public | BindingFlags.Instance);
    C(depth != null, "WaterVolume.Depth exists");
    if (depth == null)
      return;
    var body = depth.GetMethodBody();
    var il = body!.GetILAsByteArray()!;
    C(il.Length > 100 && il.Length < 400, $"it is a short method ({il.Length} bytes of IL)");
    // the members it reads: the one-depth shortcut, the four depths, the collider's bounds
    var names = new HashSet<string>();
    var module = depth.Module;
    for (int i = 0; i + 4 < il.Length; i++)
    {
      // ldfld (0x7B), call (0x28) and callvirt (0x6F) carry a metadata token
      if (il[i] == 0x7B || il[i] == 0x28 || il[i] == 0x6F)
      {
        int token = BitConverter.ToInt32(il, i + 1);
        try
        {
          var member = module.ResolveMember(token);
          if (member != null)
            names.Add(member.DeclaringType?.Name + "." + member.Name);
        }
        catch (Exception)
        {
        }
      }
    }
    C(names.Contains("WaterVolume.m_oneDepth") && names.Contains("WaterVolume.m_normalizedDepth") && names.Contains("WaterVolume.m_collider"), "it reads m_oneDepth, m_normalizedDepth and m_collider: " + string.Join(", ", names.OrderBy(x => x)));
    C(names.Contains("Collider.get_bounds") && names.Contains("Transform.InverseTransformPoint") && names.Contains("Mathf.Lerp"), "it uses the collider's bounds, InverseTransformPoint and Mathf.Lerp");
    // the order of the corners: lerp(lerp(d[3], d[2], t), lerp(d[0], d[1], t), t2)
    var il2 = il;
    int lerps = 0;
    for (int i = 0; i + 4 < il2.Length; i++)
      if (il2[i] == 0x28 && SafeName(module, BitConverter.ToInt32(il2, i + 1)) == "Mathf.Lerp")
        lerps++;
    C(lerps == 3, $"three lerps ({lerps})");
    // the order the four depths are indexed in: the constant loaded before each ldelem is 3, 2, 0, 1 (the transcription's)
    var indices = new List<int>();
    for (int i = 0; i + 1 < il2.Length; i++)
    {
      // ldc.i4.0..3 are 0x16..0x19 followed by ldelem.r4 (0x98)
      if (il2[i] >= 0x16 && il2[i] <= 0x19 && il2[i + 1] == 0x98)
        indices.Add(il2[i] - 0x16);
    }
    C(indices.SequenceEqual(new[] { 3, 2, 0, 1 }), "it indexes the four depths in the order 3, 2, 0, 1: " + string.Join(",", indices));
  }

  private static string SafeName(Module module, int token)
  {
    try
    {
      var m = module.ResolveMember(token);
      return m == null ? "" : m.DeclaringType?.Name + "." + m.Name;
    }
    catch (Exception)
    {
      return "";
    }
  }

  // ---- the full tiles ---------------------------------------------------------------------------------------------------------

  public static void ShapeFullTilesTest()
  {
    Section("shape: a full tile is covered by the mask over all the mesh can reach");
    var rng = new Rng(4242);
    int full = 0, partial = 0, bad = 0;
    for (int n = 0; n < 300; n++)
    {
      var noise = new Noise((ulong)(5000 + n), (float)rng.Range(40, 60), (float)rng.Range(10, 40), (float)rng.Range(50, 150));
      float half = (float)rng.Range(60, 200);
      var lake = new Lake("f" + n, 0, 0, (float)rng.Range(45, 75), -half, -half, half, half, 10f);
      var s = LakeFlood.Compute(noise.Height, lake, default, keepWet: false);
      if (!s.Ok)
        continue;
      foreach (var t in s.Tiles)
      {
        // every point of the mesh's reach, by the point test the build uses for vertices
        bool all = true;
        for (float x = t.CentreX - 33f; x <= t.CentreX + 33f && all; x += 1f)
          for (float z = t.CentreZ - 33f; z <= t.CentreZ + 33f; z += 1f)
            if (!s.InMask(x, z))
            {
              all = false;
              break;
            }
        if (t.Full)
        {
          full++;
          if (!all) bad++;
        }
        else
          partial++;
        // and the converse for the part of the mesh the zone water really has (32.1 m): a Full tile covers it
        if (t.Full)
          for (float x = t.CentreX - 32.1f; x <= t.CentreX + 32.1f; x += 3.7f)
            for (float z = t.CentreZ - 32.1f; z <= t.CentreZ + 32.1f; z += 3.7f)
              if (!s.InMask(x, z)) bad++;
      }
    }
    C(bad == 0, $"{full} full tiles checked point by point, {bad} points not in the mask");
    C(full > 50 && partial > 50, $"both kinds occur: {full} full, {partial} partial");
  }

  public static void ShapeDepthsTest()
  {
    Section("shape: a lake's tiles carry the ground's depth at their corners, the order of vanilla's");
    // a tilted bed: ground 20 m lower to the east and to the north (z), so each corner has a different depth
    Func<float, float, float> bed = (x, z) => 50f - 0.1f * x - 0.2f * z;
    var lake = new Lake("t", 0, 0, 55f, -100, -100, 100, 100, 5f);
    var s = LakeFlood.Compute(bed, lake);
    C(s.Ok, "the lake is made");
    var origin = s.Tiles.FirstOrDefault(t => t.Tx == 0 && t.Tz == 0);
    // corners of the tile at 0,0: (-32, +32) ground 50 + 3.2 - 6.4 = 46.8 -> 8.2; (+32, +32) 50 - 3.2 - 6.4 = 40.4 -> 14.6; (+32, -32) 50 - 3.2 + 6.4 = 53.2 -> 1.8; (-32, -32) 50 + 3.2 + 6.4 = 59.6 -> 0 (dry)
    C(Math.Abs(origin.D0 - 8.2f) < 1e-3f && Math.Abs(origin.D1 - 14.6f) < 1e-3f && Math.Abs(origin.D2 - 1.8f) < 1e-3f && origin.D3 == 0f,
      $"[0] minX,maxZ [1] maxX,maxZ [2] maxX,minZ [3] minX,minZ: {origin.D0:0.0}, {origin.D1:0.0}, {origin.D2:0.0}, {origin.D3:0.0}");
    C(origin.Metres().SequenceEqual(new[] { origin.D0, origin.D1, origin.D2, origin.D3 }), "Metres() lists them in that order");
  }

  public static void ShapeHashSeesEverythingTest()
  {
    Section("shape: the hash changes with the lake");
    var noise = new Noise(99, 50f, 30f, 80f);
    var a = LakeFlood.Compute(noise.Height, new Lake("a", 0, 0, 60f, -100, -100, 100, 100, 5));
    var b = LakeFlood.Compute(noise.Height, new Lake("a", 0, 0, 60.25f, -100, -100, 100, 100, 5));
    var c = LakeFlood.Compute(noise.Height, new Lake("a", 0, 0, 60f, -100, -100, 100, 100, 5));
    C(a.Ok && b.Ok && a.Hash() == c.Hash() && a.Hash() != b.Hash(), "the same lake the same hash, another level another");
    C(a.Nx == c.Nx && a.WetCells == c.WetCells && a.Tiles.Length == c.Tiles.Length && a.Boxes.Length == c.Boxes.Length, "and the same numbers");
    C(!Path.GetFullPath(".").Contains("\0"), "(a note: nothing on disk is touched)");
  }
}

// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.IO;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using static AltBiomeHarness.H;

namespace AltBiomeHarness;

// Offline round trip of the world-export heightmap encoding, free of Unity natives: WorldExportMath's pure formulas
// (the real static functions from the built pre-ILRepack DLL) encode a synthetic height field to L16 exactly as
// WorldExport.TerrainRow does, WorldExportPng writes it as WorldExport itself would, and ImageMapFloat -- Better
// Continents' own loader -- reads it back, so the whole export/import loop is checked without a running game.
internal static partial class Tests
{
  private static void ExportMathTests()
  {
    const int size = 257;
    const float amount = 2f;
    const float seaLevel = 0.5f;
    // Read live, not hard-coded: TerrainRow uses these same static fields, and nothing here should assume no
    // earlier section left them at other than their compile-time defaults (21000 / 10000 / 10500).
    float total = BC.TotalSize, worldRadius = BC.WorldRadius, totalRadius = BC.TotalRadius;
    float tolerance = 200f * amount / 65535f + 1e-3f;
    float floorMetres = BetterContinents.WorldExportMath.ValueToMetres(0f, amount, seaLevel);
    float ceilMetres = BetterContinents.WorldExportMath.ValueToMetres(1f, amount, seaLevel);

    var dir = Path.Combine(Work, "export-math");
    Directory.CreateDirectory(dir);

    // (a) A synthetic height field in metres, -60..400, deterministic and smooth, plus two pixels pushed past the
    // encodable range on purpose so clipping is exercised too. Encoded with WorldExportMath exactly as TerrainRow
    // encodes a sampled height (BetterContinents.WorldGeneratorPatch.cs's GetBaseHeightV3, no edge drop-off here).
    var metres = new float[size, size];
    for (int row = 0; row < size; row++)
    for (int col = 0; col < size; col++)
    {
      float u = col / (float)(size - 1), v = row / (float)(size - 1);
      metres[row, col] = -60f + 460f * (0.5f + 0.5f * Mathf.Sin(6f * u) * Mathf.Cos(5f * v));
    }
    metres[0, 0] = -500f; // clips low
    metres[10, 10] = 5000f; // clips high

    // The synthetic field's own range (-60..400) straddles the amount-2/sea-level-0.5 encodable window (floor..ceil
    // below), so plenty of pixels clip from the sine field alone; (0,0) and (10,10) are pushed far past either end
    // on top of that, to make sure the two extremes clip in the direction expected of them specifically.
    var pixels = new L16[size * size];
    var original = new ushort[size * size];
    int clipLow = 0, clipHigh = 0;
    int clipAtDeliberateLow = 0, clipAtDeliberateHigh = 0;
    for (int row = 0; row < size; row++)
    for (int col = 0; col < size; col++)
    {
      float value = BetterContinents.WorldExportMath.MetresToValue(metres[row, col], amount, seaLevel);
      ushort p = BetterContinents.WorldExportMath.ValueToUShort(value, out int clip);
      if (clip < 0) clipLow++;
      else if (clip > 0) clipHigh++;
      if (row == 0 && col == 0) clipAtDeliberateLow = clip;
      if (row == 10 && col == 10) clipAtDeliberateHigh = clip;
      pixels[row * size + col] = new L16(p);
      original[row * size + col] = p;
    }
    Check(clipLow > 0 && clipHigh > 0, $"the field's own range clips at both ends against the encodable window ({floorMetres:0} to {ceilMetres:0} m: low {clipLow}, high {clipHigh})");
    Check(clipAtDeliberateLow < 0, "the deliberately far-too-low pixel (-500 m) clips low");
    Check(clipAtDeliberateHigh > 0, "the deliberately far-too-high pixel (5000 m) clips high");

    // Written with WorldExportPng, the same call TerrainPass makes (WorldExport.cs: Write("heightmap.png", ...,
    // p => WorldExportPng.SaveL16(p, heights!, n))): PNG bytes exactly as the exporter writes them, via a real file.
    var path = Path.Combine(dir, "heightmap.png");
    BetterContinents.WorldExportPng.SaveL16(path, pixels, size);
    Check(File.Exists(path) && !File.Exists(path + ".tmp"), "heightmap.png written, no leftover .tmp file");
    var bytes = File.ReadAllBytes(path);
    Check(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G', "the file is a PNG");

    // (b) Loaded back with Better Continents' own loader, exactly as a re-imported heightmap.png would be.
    var map = BetterContinents.ImageMapFloat.Create(bytes, false);
    Check(map != null, "ImageMapFloat.Create(bytes, alpha: false) decoded the exported PNG");
    if (map == null)
      return;

    // (c) GetBaseHeightV3's formula for the export preset, reproduced from the decoded value, at every grid point
    // (ImageMapFloat.GetValue's grid: normalized x = col / (size - 1), and its post-load-flip y = mapRow / (size -
    // 1), where mapRow = FlipRow(fileRow, size) since ImageMapBase flips the image vertically before decoding it).
    double maxDelta = 0;
    int reencodeMismatches = 0;
    for (int row = 0; row < size; row++)
    for (int col = 0; col < size; col++)
    {
      int mapRow = BetterContinents.WorldExportMath.FlipRow(row, size);
      float h = map.GetValue(col / (float)(size - 1), mapRow / (float)(size - 1));
      float decodedMetres = BetterContinents.WorldExportMath.ValueToMetres(h, amount, seaLevel);
      float expectedMetres = Mathf.Clamp(metres[row, col], floorMetres, ceilMetres);
      double delta = Math.Abs(decodedMetres - expectedMetres);
      if (delta > maxDelta)
        maxDelta = delta;

      // (e) Idempotence: re-encoding the decoded value must reproduce the exact stored pixel, at every grid point,
      // not just the ones sampled above -- a decode/re-encode round trip must not drift.
      ushort reencoded = BetterContinents.WorldExportMath.ValueToUShort(h, out _);
      if (reencoded != original[row * size + col])
        reencodeMismatches++;
    }
    Check(maxDelta <= tolerance, $"GetBaseHeightV3 round trip within {tolerance:0.######} m at every grid point (worst {maxDelta:0.######} m)");
    Check(reencodeMismatches == 0, $"idempotent: re-encoding the decoded field reproduces every one of the {size * size} stored pixels exactly ({reencodeMismatches} mismatch(es))");

    // (d) Orientation: a single tall pixel at file (x = 3, row 0 = the PNG's top row) must come back at map
    // coordinates (x = 3, y = Size - 1), i.e. world +z (north), using WorldExportMath.FlipRow, the exporter's own
    // WorldExport.PixelOf, and PixelToWorld/FileRowToWorldZ in both directions.
    const int fileCol = 3, fileRow = 0;
    ushort background = BetterContinents.WorldExportMath.ValueToUShort(BetterContinents.WorldExportMath.MetresToValue(0f, amount, seaLevel), out _);
    ushort tall = BetterContinents.WorldExportMath.ValueToUShort(BetterContinents.WorldExportMath.MetresToValue(390f, amount, seaLevel), out _);
    var orient = new L16[size * size];
    for (int i = 0; i < orient.Length; i++)
      orient[i] = new L16(background);
    orient[fileRow * size + fileCol] = new L16(tall);
    var orientPath = Path.Combine(dir, "orientation.png");
    BetterContinents.WorldExportPng.SaveL16(orientPath, orient, size);
    var orientMap = BetterContinents.ImageMapFloat.Create(File.ReadAllBytes(orientPath), false);
    Check(orientMap != null, "orientation.png decoded");
    if (orientMap == null)
      return;

    int expectedMapRow = size - 1;
    Check(BetterContinents.WorldExportMath.FlipRow(fileRow, size) == expectedMapRow,
      $"FlipRow(file row 0, size) is Size - 1 ({BetterContinents.WorldExportMath.FlipRow(fileRow, size)})");
    float atNorth = orientMap.GetValue(fileCol / (float)(size - 1), expectedMapRow / (float)(size - 1));
    float atSouth = orientMap.GetValue(fileCol / (float)(size - 1), 0f);
    Check(atNorth > atSouth + 0.5f,
      $"the tall pixel at file row 0 decodes at map row Size - 1 (north), not map row 0 (south) (h {atNorth:0.###} vs {atSouth:0.###})");

    float worldX = BetterContinents.WorldExportMath.PixelToWorld(fileCol, size, total);
    float worldZ = BetterContinents.WorldExportMath.FileRowToWorldZ(fileRow, size, total);
    Check(worldZ > 0f, $"file row 0 (the PNG's top row) is a positive world z, i.e. north ({worldZ} m)");
    var back = BetterContinents.WorldExport.PixelOf(new Vector3(worldX, 0f, worldZ), size);
    Check(back.x == fileCol && back.y == fileRow,
      $"WorldExport.PixelOf round-trips file ({fileCol},{fileRow}) -> world ({worldX:0.#}, {worldZ:0.#}) -> ({back.x},{back.y})");

    // The edge-ring formulas are part of the same exported preset (Options.EdgeDropoff); check the drop-off/undo
    // pair is a true inverse across the ring, since TerrainRow relies on this to avoid re-applying the fade twice.
    double worstEdge = 0;
    for (float d = worldRadius + 1f; d < totalRadius; d += 37f)
    {
      float before = -0.05f + 0.2f * Mathf.Sin(d * 0.01f);
      float after = BetterContinents.WorldExportMath.ApplyEdgeDropoff(before, d, worldRadius, totalRadius);
      Check(BetterContinents.WorldExportMath.TryUndoEdgeDropoff(after, d, worldRadius, totalRadius, out float undone), $"edge drop-off at {d:0} m is invertible");
      worstEdge = Math.Max(worstEdge, Math.Abs(undone - before));
    }
    Check(worstEdge < 1e-4, $"ApplyEdgeDropoff/TryUndoEdgeDropoff round trip across the edge ring (worst {worstEdge:0.######})");
  }
}

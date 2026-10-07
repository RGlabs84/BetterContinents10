// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The sector map on its own (WorldSectors.SectorMap), against the game's own ZoneSystem functions, which are not patched here.

using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Map = BetterContinents.WorldSectors.SectorMap;

internal static partial class Program
{
  private static void MapTests()
  {
    Section("the sector map: every zone from -1024 to 1023");
    const int N = Map.Width;
    var seen = new BitArray(N * N);
    int unmapped = 0, zero = 0, dup = 0, badRange = 0, badBlock = 0, badBack = 0, badInner = 0, inner = 0, portal = 0, badWide = 0, moved = 0;
    int badMoved = 0, mapped = 0;
    for (int zy = Map.FirstZone; zy <= Map.LastZone; zy++)
      for (int zx = Map.FirstZone; zx <= Map.LastZone; zx++)
      {
        uint index = Map.SectorToIndex(zx, zy);
        // The zones 1016 to 1023 by -256 to -249 have no sector: their chunk is where the zones of the game's chunk (1, 0) are.
        bool far = zx >= 1016 && zy >= -256 && zy <= -249;
        bool movedBlock = zx >= -248 && zx <= -241 && zy >= -256 && zy <= -249;
        if (far)
        {
          unmapped++;
          if (index != 0) badRange++;
          continue;
        }
        mapped++;
        if (index >= N * N) { badRange++; continue; }
        if (index == 0)
        {
          zero++;
          if (zx != -256 || zy != -256) badRange++;
        }
        else if (seen[(int)index])
          dup++;
        else
          seen[(int)index] = true;
        var chunk = Map.Chunk(index);
        var (ox, oy) = Map.ZoneFromChunk(chunk);
        if (zx < ox || zx > ox + 7 || zy < oy || zy > oy + 7 || ox % 8 != 0 || oy % 8 != 0) badBlock++;
        if (chunk == 1) portal++;
        if (Map.IndexToSector(index) != (zx, zy)) badBack++;
        bool innerZone = zx >= -256 && zx <= 255 && zy >= -256 && zy <= 255;
        if (Map.IsWideChunk(chunk) != (!innerZone || movedBlock)) badWide++;
        if (!innerZone) continue;
        inner++;
        // The game's own chunk for the zone (ZoneSystem.GetZonesChunk(SectorToIndex)).
        var game = ZoneSystem.GetZonesChunk(ZoneSystem.SectorToIndex(zx, zy)).Chunk;
        if (movedBlock)
        {
          moved++;
          if (game != 1 || chunk != 159) badMoved++;
        }
        else if (game != chunk)
          badInner++;
      }
    Check(mapped == N * N - 64 && unmapped == 64, $"2048 x 2048 zones: all have a sector but the 64 beyond 65 km at the south ({mapped} have one, {unmapped} do not)");
    Check(badRange == 0, $"every sector is below 2048 x 2048, and 0 only for zone (-256, -256) (the game's too) and the unmapped ({badRange} wrong)");
    Check(zero == 1 && dup == 0, $"every zone has a sector of its own: one zone at sector 0 and {dup} sectors shared");
    Check(badBlock == 0, $"a zone is within the 8 x 8 zones its chunk reaches from ZoneFromChunk, whose first zone is a multiple of 8 ({badBlock} are not)");
    Check(badBack == 0, $"IndexToSector gives the zone back ({badBack} do not come back)");
    Check(portal == 0, $"no zone is in chunk (1, 0), the key the game's portals are saved under ({portal} are)");
    Check(inner == 512 * 512 - 0 && moved == 64, $"the 512 x 512 zones the game has: {inner}, 64 of them chunk (1, 0)'s");
    Check(badInner == 0, $"a zone of the game's sectors is in the chunk the game puts it in, with its chunk number ({badInner} differ)");
    Check(badMoved == 0, $"the 64 zones the game files in chunk (1, 0) are in chunk (159, 0), where the game has none ({badMoved} wrong)");
    Check(badWide == 0, $"IsWideChunk is true for the chunk of every zone outside the game's, and of the moved ones only ({badWide} wrong)");

    // Outside the map: sector 0, as the game's.
    Check(Map.SectorToIndex(-1025, 0) == 0 && Map.SectorToIndex(1024, 0) == 0 && Map.SectorToIndex(0, -1025) == 0 && Map.SectorToIndex(0, 1024) == 0
          && Map.SectorToIndex(int.MinValue, 0) == 0 && Map.SectorToIndex(0, int.MaxValue) == 0, "a zone outside -1024 to 1023: sector 0");
    Check(Map.SectorToIndex(0, 0) != 0 && Map.SectorToIndex(1023, 1023) != 0 && Map.SectorToIndex(-1024, -1024) != 0, "the corners of the map have sectors");

    // The array's places.
    int badIndices = 0, badIndicesChunk = 0;
    for (uint y = 0; y < 600; y++)
      for (uint x = 0; x < 600; x++)
      {
        var index = Map.IndicesToIndex(x, y);
        if (index != (x >= N || y >= N ? 0 : y * N + x)) badIndices++;
        if (x < 512 && y < 512 && Map.Chunk(index) != ZoneSystem.GetZonesChunk(ZoneSystem.IndicesToIndex(x, y)).Chunk) badIndicesChunk++;
      }
    Check(badIndices == 0 && Map.IndicesToIndex(2048, 0) == 0 && Map.IndicesToIndex(0, 2048) == 0 && Map.IndicesToIndex(2047, 2047) == N * N - 1, "IndicesToIndex: row after row, 2048 a row, 0 beyond");
    Check(badIndicesChunk == 0, $"the chunk of a place of the game's 512 x 512 is the game's ({badIndicesChunk} differ)");

    // The chunk grid, 256 x 256 chunk numbers of 8 bits an axis.
    int unlike = 0, notBack = 0, same = 0;
    for (int cy = 0; cy < 256; cy++)
      for (int cx = 0; cx < 256; cx++)
      {
        var chunk = (ushort)(cx | cy << 8);
        var first = Map.ZoneFromChunk(chunk);
        if (cx < 64 && cy < 64)
        {
          var game = ZoneSystem.GetZoneFromChunk(new ZoneSystem.ChunkIndex(chunk, 0));
          if (game.x != first.x || game.y != first.y) unlike++;
          else same++;
        }
        if (Map.Chunk(Map.SectorToIndex(first.x, first.y)) != chunk)
          notBack++;
      }
    Check(unlike == 0 && same == 4096, $"ZoneFromChunk is the game's for the game's 64 x 64 chunks ({same} compared, {unlike} differ)");
    Check(notBack == 1, $"every chunk's first zone is in that chunk, but chunk (1, 0)'s, which holds none ({notBack} are not)");

    // The game merges 2 x 2, 4 x 4 and 8 x 8 chunks into one file (DecideChunkSize), never a group that has chunk row 0 or
    // chunk column 0: each merged group must be one square of zones.
    int groups = 0, broken = 0;
    for (int level = 1; level <= 3; level++)
    {
      int side = 1 << level;
      for (int gy = 1; gy < 256 / side; gy++)
        for (int gx = 1; gx < 256 / side; gx++)
        {
          groups++;
          var origin = Map.ZoneFromChunk((ushort)(gx * side | gy * side << 8));
          bool ok = true;
          for (int dy = 0; dy < side && ok; dy++)
            for (int dx = 0; dx < side && ok; dx++)
              ok = Map.ZoneFromChunk((ushort)((gx * side + dx) | (gy * side + dy) << 8)) == (origin.x + 8 * dx, origin.y + 8 * dy);
          if (!ok) broken++;
        }
    }
    Check(groups == 127 * 127 + 63 * 63 + 31 * 31 && broken == 0, $"every group the game may merge (2, 4 or 8 chunks across, not at chunk row or column 0) is a square of zones ({groups} groups, {broken} are not)");
    // The array's two halves meet where no group crosses: chunk 64 (zone 256) and chunk 160 (zone -1024).
    Check(Map.Zone(0) == -256 && Map.Zone(511) == 255 && Map.Zone(512) == 256 && Map.Zone(1279) == 1023 && Map.Zone(1280) == -1024 && Map.Zone(2047) == -257
          && Enumerable.Range(0, N).All(a => Map.Axis(Map.Zone(a)) == a), "places along an axis: 0 to 511 are zones -256 to 255, then 256 to 1023, then -1024 to -257");
  }
}

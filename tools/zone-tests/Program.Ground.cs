// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).
//
// The ground at the edge of a left-alone zone (ZoneReset.TerrainBorder): the terrain compiler's data, in TerrainComp's own
// format, with the edits on the sides that meet a reset zone cleared and nothing else touched.

using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using Edges = BetterContinents.ZoneReset.Edges;

internal static partial class Program
{
  private const int Pitch = 65;

  private static float Level(int row, int col) => row * 0.01f + col * 0.001f + 0.5f;
  private static float Smooth(int row, int col) => -(row + col) * 0.0005f;

  // What TerrainComp.Save writes: the version, the operations, the last operation's point and radius, then per vertex of the
  // grid whether it was edited (and the level and smooth deltas), then per vertex whether it was painted (and the colour).
  private static byte[] TcData(Func<int, int, bool> edited, Func<int, int, bool>? painted = null, int pitch = Pitch, byte[]? trailing = null)
  {
    var p = new ZPackage();
    p.Write(1);
    p.Write(7);
    p.Write(new Vector3(1f, 2f, 3f));
    p.Write(4.5f);
    p.Write(pitch * pitch);
    for (int row = 0; row < pitch; row++)
      for (int col = 0; col < pitch; col++)
      {
        bool e = edited(row, col);
        p.Write(e);
        if (e)
        {
          p.Write(Level(row, col));
          p.Write(Smooth(row, col));
        }
      }
    p.Write(pitch * pitch);
    for (int row = 0; row < pitch; row++)
      for (int col = 0; col < pitch; col++)
      {
        bool b = painted != null && painted(row, col);
        p.Write(b);
        if (b)
        {
          p.Write(0.1f * row);
          p.Write(0.2f);
          p.Write(0.3f * col);
          p.Write(1f);
        }
      }
    var body = p.GetArray();
    if (trailing != null)
      body = body.Concat(trailing).ToArray();
    return Utils.Compress(body);
  }

  private sealed record Tc(int Version, int Operations, Vector3 Point, float Radius, bool[] Edited, float[] Level, float[] Smooth, bool[] Painted, float[][] Colours, byte[] Rest);

  // TerrainComp.Load, read the same way, for comparing what comes out.
  private static Tc ReadTc(byte[] data)
  {
    var raw = Utils.Decompress(data);
    var p = new ZPackage(raw);
    int version = p.ReadInt(), operations = p.ReadInt();
    var point = p.ReadVector3();
    float radius = p.ReadSingle();
    int n = p.ReadInt();
    var edited = new bool[n];
    var level = new float[n];
    var smooth = new float[n];
    for (int i = 0; i < n; i++)
    {
      edited[i] = p.ReadBool();
      if (edited[i])
      {
        level[i] = p.ReadSingle();
        smooth[i] = p.ReadSingle();
      }
    }
    int m = p.ReadInt();
    var painted = new bool[m];
    var colours = new float[m][];
    for (int i = 0; i < m; i++)
    {
      painted[i] = p.ReadBool();
      if (painted[i])
        colours[i] = [p.ReadSingle(), p.ReadSingle(), p.ReadSingle(), p.ReadSingle()];
    }
    return new Tc(version, operations, point, radius, edited, level, smooth, painted, colours, raw.Skip(p.GetPos()).ToArray());
  }

  private static void GroundTests()
  {
    Section("ground at a border: TerrainComp's data with the meeting sides cleared");
    var all = TcData((r, c) => true, (r, c) => (r + c) % 3 == 0);
    var before = ReadTc(all);
    C(before.Edited.All(e => e) && before.Edited.Length == Pitch * Pitch, "the test data is a full 65 x 65 grid of edits");

    int Row(int i) => i / Pitch;
    int Col(int i) => i % Pitch;
    var cases = new (Edges Edges, Func<int, int, bool> OnEdge, int Count, string Name)[]
    {
      (Edges.North, (r, c) => r == 64, 65, "north: the last row"),
      (Edges.South, (r, c) => r == 0, 65, "south: the first row"),
      (Edges.East, (r, c) => c == 64, 65, "east: the last column"),
      (Edges.West, (r, c) => c == 0, 65, "west: the first column"),
      (Edges.NorthEast, (r, c) => r == 64 && c == 64, 1, "north east: one corner vertex"),
      (Edges.NorthWest, (r, c) => r == 64 && c == 0, 1, "north west: one corner vertex"),
      (Edges.SouthEast, (r, c) => r == 0 && c == 64, 1, "south east: one corner vertex"),
      (Edges.SouthWest, (r, c) => r == 0 && c == 0, 1, "south west: one corner vertex"),
      (Edges.North | Edges.East, (r, c) => r == 64 || c == 64, 129, "north and east: two sides sharing a corner"),
      (Edges.East | Edges.NorthEast | Edges.SouthEast, (r, c) => c == 64, 65, "east with both its corners: the corners are on the side already"),
      (Edges.North | Edges.South | Edges.East | Edges.West, (r, c) => r == 0 || r == 64 || c == 0 || c == 64, 256, "all four sides: the whole frame"),
    };
    foreach (var (edges, onEdge, count, name) in cases)
    {
      var result = ZoneReset.TerrainBorder.Clear(all, edges, out int cleared);
      bool ok = result != null && cleared == count;
      if (ok)
      {
        var after = ReadTc(result!);
        bool good = after.Version == before.Version && after.Operations == before.Operations && after.Point == before.Point && after.Radius == before.Radius && after.Rest.Length == 0;
        for (int i = 0; i < Pitch * Pitch && good; i++)
        {
          bool cut = onEdge(Row(i), Col(i));
          // The edge vertices lose their edit; every other vertex keeps its edit and both its deltas.
          good = cut ? !after.Edited[i] && after.Level[i] == 0f && after.Smooth[i] == 0f
                     : after.Edited[i] && after.Level[i] == before.Level[i] && after.Smooth[i] == before.Smooth[i];
        }
        ok = good && after.Painted.SequenceEqual(before.Painted) && after.Colours.Zip(before.Colours, (a, b) => a == null ? b == null : a.SequenceEqual(b)).All(x => x);
      }
      C(ok, name + (ok ? "" : $" (cleared {cleared})"));
    }

    // Only edited vertices count; an edge that was never edited changes nothing and gives nothing back.
    var interior = TcData((r, c) => r > 10 && r < 50 && c > 10 && c < 50);
    C(ZoneReset.TerrainBorder.Clear(interior, Edges.North | Edges.East | Edges.South | Edges.West, out int none) == null && none == 0, "edits that do not reach the edge: nothing to clear, null");
    var oneSide = TcData((r, c) => r == 64 && c > 20 && c < 30);
    var northCut = ZoneReset.TerrainBorder.Clear(oneSide, Edges.North, out int some);
    C(northCut != null && some == 9 && ReadTc(northCut).Edited.Count(e => e) == 0, "nine edited vertices on the north side: nine cleared");
    C(ZoneReset.TerrainBorder.Clear(oneSide, Edges.South | Edges.East | Edges.West, out int wrongSide) == null && wrongSide == 0, "the other sides hold no edits: nothing to clear");
    C(ZoneReset.TerrainBorder.Clear(all, Edges.None, out int noEdges) == null && noEdges == 0, "no side asked for: nothing");

    // The paint, and what a later game version might append after it, are kept as they are.
    var withTail = TcData((r, c) => true, (r, c) => r == 64, trailing: [1, 2, 3, 4, 5]);
    var tailCut = ZoneReset.TerrainBorder.Clear(withTail, Edges.North, out int tailCleared);
    var tailAfter = tailCut == null ? null : ReadTc(tailCut);
    C(tailAfter != null && tailCleared == 65 && tailAfter.Rest.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "bytes after the paint are carried over");
    C(tailAfter != null && tailAfter.Painted.Count(p => p) == 65 && tailAfter.Painted[64 * Pitch + 3] && tailAfter.Colours[64 * Pitch + 3]![0] == 0.1f * 64,
      "the paint on the cleared side stays painted: only heights are mended");

    // Data that is not TerrainComp's is left alone, never thrown on.
    C(ZoneReset.TerrainBorder.Clear([], Edges.North, out int e1) == null && e1 == 0, "empty data");
    C(ZoneReset.TerrainBorder.Clear([1, 2, 3, 4, 5, 6, 7, 8], Edges.North, out int e2) == null && e2 == 0, "bytes that are not compressed data");
    C(ZoneReset.TerrainBorder.Clear(Utils.Compress([1, 2, 3]), Edges.North, out int e3) == null && e3 == 0, "compressed data that is too short");
    var odd = TcData((r, c) => true, pitch: 10);
    C(ZoneReset.TerrainBorder.Clear(odd, Edges.North, out int e4) != null && e4 == 10, "a grid of 10 x 10 (another zone width) is cut the same way");
    var notSquare = new ZPackage();
    notSquare.Write(1);
    notSquare.Write(1);
    notSquare.Write(Vector3.zero);
    notSquare.Write(1f);
    notSquare.Write(50);
    for (int i = 0; i < 50; i++)
      notSquare.Write(true);
    C(ZoneReset.TerrainBorder.Clear(Utils.Compress(notSquare.GetArray()), Edges.North, out int e5) == null && e5 == 0, "a grid that is not square is not touched");
    var truncated = Utils.Compress(Utils.Decompress(all).Take(5000).ToArray());
    C(ZoneReset.TerrainBorder.Clear(truncated, Edges.North, out int e6) == null && e6 == 0, "data cut short is not touched");

    // A vertex index is row * 65 + column, row along z (north), column along x (east): TerrainComp.ApplyToHeightmap.
    C(ZoneReset.TerrainBorder.OnEdge(64, 0, Pitch, Edges.North) && !ZoneReset.TerrainBorder.OnEdge(0, 64, Pitch, Edges.North), "north is the last row, not the last column");
    C(ZoneReset.TerrainBorder.OnEdge(0, 64, Pitch, Edges.East) && !ZoneReset.TerrainBorder.OnEdge(64, 0, Pitch, Edges.East), "east is the last column, not the last row");

    // A whole zone's data is cut in a millisecond or two on this machine; that is told, not asserted: it depends on the machine.
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 200; i++)
      ZoneReset.TerrainBorder.Clear(all, Edges.North | Edges.West, out _);
    watch.Stop();
    System.Console.WriteLine($"  (cutting one zone's data took {watch.ElapsedMilliseconds / 200.0:0.00} ms here; not asserted)");
  }
}

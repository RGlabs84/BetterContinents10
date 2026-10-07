// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterContinents;

// WorldGenerator.MergePoints, the game's merging of the lake points FindLakes found: the same answer, found in far less time.
//
// The game takes the first point, and for as long as there is a point within `range` of it, merges the closest one in (the
// two's middle becomes the point), looking at EVERY remaining point for each one it merges; when none is near, the point is a
// lake and the next first point starts again. The work grows with the square of the points: vanilla's 10000 m world finds a few
// thousand points and takes milliseconds, but a world laid out to 32264 m (WorldSizeHelper.FindLakesTranspiler looks for lakes
// within World Size, every 128 m) looks at 200,000 points, and a map with a lot of sea makes most of them lake points: 137 s on
// a dedicated server for a world with no maps at all, and over 7 minutes (not finished) for VALtima's 16383 px maps with 65% sea.
// The game builds its WorldGenerator on the server and on every client that joins, so a player waits that long to join.
//
// Here the points sit in cells of `range`, so the closest point within `range` is found among the nine cells around a point.
// Everything the game's code decides is kept, so the lakes are the same ones to the last bit, in the same order: the closest point
// by UnityEngine's own Vector2.Distance (and == for "the same point"), the first of those as near by its place in the list as the
// game's swap-with-the-last removal leaves it, the middle as (a + b) * 0.5f. tools/size-tests compares it with the game's code on
// random points, points on a 128 m grid (where many are equally near), duplicates, clusters and the lake points of a real map.
internal static class LakeMerge
{
  // Fewer points than this are left to the game's own code (its cost is a few milliseconds there).
  internal const int FastAbove = 2000;

  // The game's list of merged points for these points and range, or null when this cannot say (a range that is no number or
  // beyond what the game's own search starts from: then the game's code runs). The list `points` is emptied, as the game's code
  // does.
  internal static List<Vector2>? Merge(List<Vector2> points, float range)
  {
    // The game's search starts from 99999 and takes the points nearer than `range` (and nearer than the best so far).
    if (!(range > 0f) || !(range < 99999f))
      return null;
    int n = points.Count;
    var position = new Vector2[n];
    points.CopyTo(position);

    // The game's list is `order`, from `head` to `tail` (RemoveAt(0) is head++, and the swap with the last then RemoveAt(Count - 1) is
    // tail--); `slot[id]` is where a point is in it, which is its place in the game's list, plus the head's.
    var order = new int[n];
    var slot = new int[n];
    var cellOf = new long[n];
    // The points of each cell of `range`.
    var cells = new Dictionary<long, List<int>>();
    for (int i = 0; i < n; i++)
    {
      order[i] = i;
      slot[i] = i;
      long cell = CellOf(position[i], range);
      cellOf[i] = cell;
      if (!cells.TryGetValue(cell, out var list))
        cells.Add(cell, list = new List<int>());
      list.Add(i);
    }

    var merged = new List<Vector2>();
    int head = 0, tail = n;
    while (head < tail)
    {
      int first = order[head];
      head++;
      Forget(cells, cellOf[first], first);
      var vector = position[first];
      while (head < tail)
      {
        int closest = Closest(cells, position, slot, vector, range);
        if (closest == -1)
          break;
        vector = (vector + position[closest]) * 0.5f;
        int at = slot[closest], last = order[tail - 1];
        order[at] = last;
        slot[last] = at;
        tail--;
        Forget(cells, cellOf[closest], closest);
      }
      merged.Add(vector);
    }
    points.Clear();
    return merged;
  }

  // The game's FindClosest over the points that are left: the nearest point within `range` that is not p itself, the first
  // in the list of those as near (the game compares in list order and takes the first of equals).
  private static int Closest(Dictionary<long, List<int>> cells, Vector2[] position, int[] slot, Vector2 p, float range)
  {
    int best = -1;
    float bestDistance = 99999f;
    int bestSlot = int.MaxValue;
    long cell = CellOf(p, range);
    int cx = (int)(cell >> 32), cy = (int)cell;
    for (int dx = -1; dx <= 1; dx++)
      for (int dy = -1; dy <= 1; dy++)
      {
        if (!cells.TryGetValue(Key(cx + dx, cy + dy), out var list))
          continue;
        for (int k = 0; k < list.Count; k++)
        {
          int id = list[k];
          var q = position[id];
          if (q == p)
            continue;
          float distance = Vector2.Distance(p, q);
          if (distance < range && (distance < bestDistance || (distance == bestDistance && slot[id] < bestSlot)))
          {
            best = id;
            bestDistance = distance;
            bestSlot = slot[id];
          }
        }
      }
    return best;
  }

  private static void Forget(Dictionary<long, List<int>> cells, long cell, int id)
  {
    var list = cells[cell];
    int at = list.IndexOf(id);
    list[at] = list[list.Count - 1];
    list.RemoveAt(list.Count - 1);
  }

  // A cell is a little more than `range` wide each way, so that two points more than one cell apart are surely farther than
  // `range` (whatever the floats round to). A point that is no number, or too far out to count, goes in a cell where no point is
  // found near it either (the game finds none: a distance that is no number is never less than anything).
  private static long CellOf(Vector2 p, float range) => Key(Cell(p.x, range), Cell(p.y, range));

  private static int Cell(float v, float range)
  {
    double c = Math.Floor((double)v / ((double)range * 1.01));
    return double.IsNaN(c) ? int.MinValue : c > 1e9 ? 1_000_000_000 : c < -1e9 ? -1_000_000_000 : (int)c;
  }

  private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;
}

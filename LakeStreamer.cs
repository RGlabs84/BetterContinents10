// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterContinents;

/// <summary>One lake of the world's set, as the streamer holds it: its shape (worked out on a worker when the player comes near), and the tiles it has made.</summary>
internal sealed class LakeRt
{
  public Lake Lake = null!;
  /// <summary>The shape being worked out, and what came of it.</summary>
  public Task<LakeShape>? Task;
  public CancellationTokenSource? Cts;
  public LakeShape? Shape;
  /// <summary>Why this lake has no water (its point is dry ...): it is not tried again while the world has this lake.</summary>
  public string? Failed;
  public bool Logged;
  /// <summary>The tiles made, by their index in the shape.</summary>
  public readonly Dictionary<int, TileObjects> Tiles = [];
  public int TilesWithWater => Tiles.Values.Count(t => !t.Empty);
  public int BoxesMade => Tiles.Values.Sum(t => t.Boxes);

  /// <summary>Metres from a point to the lake's box (0 inside).</summary>
  public float DistanceTo(Vector3 p)
  {
    float dx = Mathf.Max(0f, Mathf.Max(Lake.MinX - p.x, p.x - Lake.MaxX));
    float dz = Mathf.Max(0f, Mathf.Max(Lake.MinZ - p.z, p.z - Lake.MaxZ));
    return Mathf.Sqrt(dx * dx + dz * dz);
  }

  public bool HasResources => Task != null || Shape != null || Tiles.Count > 0;

  public void CancelAndClear()
  {
    Cts?.Cancel();
    foreach (var tile in Tiles.Values)
      LakeBuild.Destroy(tile);
    Tiles.Clear();
    Task = null;
    Cts = null;
    Shape = null;
  }
}

// THE LAKES A PLAYER SEES, MADE AS THEY COME NEAR (spec 2.12) [Tarn: Plugin.cs, Update and the build cursor]. Ticked from BetterContinents.Update beside SeaDraw.Tick.
//
// It compares the world's lake set (Settings.Lakes) by reference each tick: a new set keeps what it can (a lake that is the same record keeps its shape and its tiles), drops the
// rest, and starts over, so a live change, a conversion and a new world need no other signal. A change of the depth scale alone writes the depths again.
//
// For each lake within Lake Build Range of the player (the box's distance) a shape is worked out on a worker, one lake at a time (LakeFlood: no engine types, the generator's ground).
// Its tiles within range (the centre's distance <= range + 32 m) are made nearest first, one tile or box a step, in Lake Build Budget milliseconds a frame (LakeBuild); a tile
// further than range + 150 m is dropped; a lake's shape (bits and rectangles, a few KB to some hundred) goes when no tile of it is made and its box is 600 m further out than
// the range. A machine without graphics (a dedicated server) makes nothing: it holds the data.
internal static class LakeStreamer
{
  // The lake set the streamer works for (null: none, or the lakes are off), and its lakes.
  private static LakeSet? current;
  private static List<LakeRt> all = [];
  // The lakes with something to keep (a shape, a task, tiles); the ones near enough to look at.
  private static readonly List<LakeRt> active = [];
  // A 512 m grid over the lakes' boxes (the lakes whose box is too large for it are in `big`).
  private static readonly Dictionary<long, List<LakeRt>> index = [];
  private static List<LakeRt> big = [];
  private const float IndexCell = 512f;
  private const int IndexCellsMax = 256;

  // What is to be made, nearest first, and what is being made.
  private static List<(LakeRt Lake, int Tile)> queue = [];
  private static int queueAt;
  private static (LakeRt Lake, int Tile)? working;

  // One shape at a time on the worker.
  private static Task chain = Task.CompletedTask;
  private static float passTimer = 1f;
  private static bool tickFailed;
  private static float lastBuildMs;
  private static float worstBuildMs;
  private static int stepsThisRun;
  private static string stateWords = "no world";

  private static bool? graphics;
  /// <summary>A machine with a graphics device: it draws lakes. A dedicated server does not.</summary>
  internal static bool Graphics => graphics ??= SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

  /// <summary>The set the streamer works for now (null when the lakes are off).</summary>
  internal static LakeSet? Set => current;

  // ---- the tick --------------------------------------------------------------------------------------------------------------------

  /// <summary>Every frame (BetterContinents.Update).</summary>
  internal static void Tick()
  {
    try
    {
      TickInner();
    }
    catch (Exception e)
    {
      if (!tickFailed)
        BetterContinents.LogError($"Lakes: the streamer failed ({e.GetType().Name}: {e.Message}); it will try again next frame. {e.StackTrace}");
      tickFailed = true;
    }
  }

  private static void TickInner()
  {
    var settings = BetterContinents.Settings;
    bool inWorld = ZNet.instance != null && ZoneSystem.instance != null;
    var set = inWorld && LakeWater.Wanted(settings) ? settings.Lakes : null;
    if (!ReferenceEquals(set, current))
      Changed(set);
    if (!Graphics)
    {
      stateWords = current == null ? (inWorld ? "no lakes in this world" : "no world") : "this machine draws nothing (a server); the lakes are held";
      return;
    }
    float dt = Time.deltaTime;
    LakeSound.Update(dt);
    if (inWorld)
      LakeMap.Tick();
    if (current == null)
    {
      stateWords = inWorld ? (settings.Lakes is { IsEmpty: false } && !LakeWater.Wanted(settings) ? "off here: " + OffReason(settings) : "no lakes in this world") : "no world";
      return;
    }
    var generator = WorldGenerator.instance;
    var template = LakeWater.Template();
    if (generator == null || template == null || !ListenerPosition(out var at))
    {
      stateWords = "waiting for the world's ground and the zone water";
      return;
    }
    stateWords = "on";
    passTimer += dt;
    if (passTimer >= 0.5f)
    {
      passTimer = 0f;
      Pass(at, generator);
    }
    Collect();
    Build(template);
  }

  private static string OffReason(BetterContinents.BetterContinentsSettings settings) =>
    TarnCompat.Installed ? "the Tarn mod is installed" : !settings.EnabledForThisWorld ? "Better Continents is off for this world" : "no reason";

  /// <summary>Where the lakes look from: the player, else the main camera (a machine with no graphics has neither).</summary>
  internal static bool ListenerPosition(out Vector3 p)
  {
    if (Player.m_localPlayer != null)
    {
      p = Player.m_localPlayer.transform.position;
      return true;
    }
    var cam = Camera.main;
    if (cam != null && Graphics)
    {
      p = cam.transform.position;
      return true;
    }
    p = default;
    return false;
  }

  // ---- the set changes -------------------------------------------------------------------------------------------------------------

  private static bool SameLakes(LakeSet a, LakeSet b)
  {
    if (ReferenceEquals(a.Lakes, b.Lakes))
      return true;
    if (a.Lakes.Count != b.Lakes.Count)
      return false;
    for (int i = 0; i < a.Lakes.Count; i++)
      if (!a.Lakes[i].Equals(b.Lakes[i]))
        return false;
    return true;
  }

  private static void Changed(LakeSet? set)
  {
    var old = current;
    current = set;
    UpdateToggles();
    if (set != null && old != null && SameLakes(old, set))
    {
      // only the depth scale (or the revision) moved: the lakes keep their tiles
      if (old.DepthScale != set.DepthScale)
      {
        int n = LakeWater.Reapply();
        BetterContinents.Log($"Lakes: depth scale {set.DepthScale:0.##}: {n} lake volume(s) read their depths again");
      }
      return;
    }
    // A lake that is the same record as one the old set had keeps its shape and its tiles.
    var keep = new Dictionary<Lake, Queue<LakeRt>>();
    foreach (var rt in all)
    {
      if (set != null && rt.Failed == null)
      {
        if (!keep.TryGetValue(rt.Lake, out var queueOfLake))
          keep[rt.Lake] = queueOfLake = new Queue<LakeRt>();
        queueOfLake.Enqueue(rt);
      }
      else
        rt.CancelAndClear();
    }
    var fresh = new List<LakeRt>();
    int reused = 0;
    if (set != null)
      foreach (var lake in set.Lakes)
      {
        if (keep.TryGetValue(lake, out var same) && same.Count > 0)
        {
          fresh.Add(same.Dequeue());
          reused++;
        }
        else
          fresh.Add(new LakeRt { Lake = lake });
      }
    foreach (var left in keep.Values)
      foreach (var rt in left)
        rt.CancelAndClear();
    all = fresh;
    active.Clear();
    active.AddRange(all.Where(rt => rt.HasResources));
    BuildIndex();
    queue = [];
    queueAt = 0;
    working = null;
    passTimer = 1f;
    if (set == null)
    {
      LakeWater.Clear();
      LakeBuild.DestroyRoot();
      LakeMap.LakesChanged();
      LakeSound.Reset();
      if (old != null)
        BetterContinents.Log("Lakes: the world has no lakes now: the lake water is gone.");
      return;
    }
    LakeMap.LakesChanged();
    BetterContinents.Log($"Lakes: {set.Lakes.Count} lake(s) (depth scale {set.DepthScale:0.##})" + (reused > 0 ? $"; {reused} kept as they were" : "")
      + (Graphics ? "" : "; this machine draws none of them"));
    foreach (var lake in set.Lakes.Take(20))
      foreach (var note in LakeWater.RulesNotes(lake))
        BetterContinents.Log("Lakes: " + note);
    if (old != null && old.DepthScale != set.DepthScale)
      LakeWater.Reapply();
  }

  private static void UpdateToggles()
  {
    try
    {
      BetterContinents.UpdateLakeToggles();
    }
    catch (Exception e)
    {
      BetterContinents.LogError($"Lakes: switching the lake patches failed ({e.GetType().Name}: {e.Message})");
    }
  }

  private static long CellKey(int cx, int cz) => ((long)cz << 32) | (uint)cx;

  private static void BuildIndex()
  {
    index.Clear();
    big = [];
    foreach (var rt in all)
    {
      int x0 = (int)Math.Floor(rt.Lake.MinX / IndexCell), x1 = (int)Math.Floor(rt.Lake.MaxX / IndexCell);
      int z0 = (int)Math.Floor(rt.Lake.MinZ / IndexCell), z1 = (int)Math.Floor(rt.Lake.MaxZ / IndexCell);
      if (!(x1 >= x0) || !(z1 >= z0) || (long)(x1 - x0 + 1) * (z1 - z0 + 1) > IndexCellsMax)
      {
        big.Add(rt);
        continue;
      }
      for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        {
          if (!index.TryGetValue(CellKey(cx, cz), out var list))
            index[CellKey(cx, cz)] = list = [];
          list.Add(rt);
        }
    }
  }

  // The lakes whose box is within `range` of a point.
  private static IEnumerable<LakeRt> Near(Vector3 at, float range)
  {
    var seen = new HashSet<LakeRt>();
    int x0 = (int)Math.Floor((at.x - range) / IndexCell), x1 = (int)Math.Floor((at.x + range) / IndexCell);
    int z0 = (int)Math.Floor((at.z - range) / IndexCell), z1 = (int)Math.Floor((at.z + range) / IndexCell);
    for (int cz = z0; cz <= z1; cz++)
      for (int cx = x0; cx <= x1; cx++)
        if (index.TryGetValue(CellKey(cx, cz), out var list))
          foreach (var rt in list)
            if (seen.Add(rt) && rt.DistanceTo(at) <= range)
              yield return rt;
    foreach (var rt in big)
      if (seen.Add(rt) && rt.DistanceTo(at) <= range)
        yield return rt;
  }

  // ---- the pass, twice a second ----------------------------------------------------------------------------------------------------

  private static void Pass(Vector3 at, WorldGenerator generator)
  {
    float range = LakeSettings.Range, dropTile = range + 150f, dropShape = range + 600f;
    // 1. what is far goes
    for (int a = active.Count - 1; a >= 0; a--)
    {
      var rt = active[a];
      if (rt.Tiles.Count > 0 && rt.Shape != null)
        foreach (int t in rt.Tiles.Keys.ToList())
        {
          var tile = rt.Shape.Tiles[t];
          if (Distance(at, tile.CentreX, tile.CentreZ) > dropTile && working?.Lake != rt)
          {
            LakeBuild.Destroy(rt.Tiles[t]);
            rt.Tiles.Remove(t);
          }
        }
      if (rt.Tiles.Count == 0 && working?.Lake != rt && rt.DistanceTo(at) > dropShape)
      {
        rt.CancelAndClear();
        active.RemoveAt(a);
      }
    }
    // 2. what is near gets its shape
    foreach (var rt in Near(at, range))
    {
      if (rt.Failed != null)
        continue;
      if (!active.Contains(rt))
        active.Add(rt);
      if (rt.Task == null && rt.Shape == null)
        StartShape(rt, generator);
    }
    // 3. the tiles to make, nearest first
    var candidates = new List<(float Distance, LakeRt Lake, int Tile)>();
    foreach (var rt in active)
    {
      if (rt.Shape == null)
        continue;
      var tiles = rt.Shape.Tiles;
      for (int t = 0; t < tiles.Length; t++)
      {
        if (rt.Tiles.ContainsKey(t))
          continue;
        float d = Distance(at, tiles[t].CentreX, tiles[t].CentreZ);
        if (d <= range + LakeShape.TileSize / 2f)
          candidates.Add((d, rt, t));
      }
    }
    candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    queue = candidates.Select(c => (c.Lake, c.Tile)).ToList();
    queueAt = 0;
  }

  private static float Distance(Vector3 at, float x, float z)
  {
    float dx = at.x - x, dz = at.z - z;
    return Mathf.Sqrt(dx * dx + dz * dz);
  }

  private static void StartShape(LakeRt rt, WorldGenerator generator)
  {
    var cts = new CancellationTokenSource();
    var token = cts.Token;
    var lake = rt.Lake;
    Func<float, float, float> height = generator.GetHeight;
    rt.Cts = cts;
    var task = chain.ContinueWith(_ => LakeFlood.Compute(height, lake, token), token, TaskContinuationOptions.None, TaskScheduler.Default);
    rt.Task = task;
    chain = task;
  }

  // The shapes that came back.
  private static void Collect()
  {
    for (int a = active.Count - 1; a >= 0; a--)
    {
      var rt = active[a];
      var task = rt.Task;
      if (task == null || !task.IsCompleted)
        continue;
      rt.Task = null;
      if (task.IsCanceled)
      {
        rt.Cts = null;
        continue;
      }
      if (task.IsFaulted)
      {
        var e = task.Exception!.GetBaseException();
        rt.Failed = $"{e.GetType().Name}: {e.Message}";
        BetterContinents.LogError($"Lakes: lake '{rt.Lake.Name}': working out its shape failed ({rt.Failed})");
        active.RemoveAt(a);
        continue;
      }
      var shape = task.Result;
      if (!shape.Ok)
      {
        rt.Failed = shape.Failed;
        BetterContinents.LogWarning($"Lakes: lake '{rt.Lake.Name}': {shape.Failed}");
        active.RemoveAt(a);
        continue;
      }
      rt.Shape = shape;
      if (shape.Warning != null)
        BetterContinents.LogWarning($"Lakes: lake '{rt.Lake.Name}': {shape.Warning}");
      // Tiles are made on the next pass; ask for it now.
      passTimer = 1f;
    }
  }

  // ---- making the objects, a step at a time -------------------------------------------------------------------------------------------

  private static void Build(WaterVolume template)
  {
    if (working == null && queueAt >= queue.Count)
      return;
    var watch = Stopwatch.StartNew();
    double budget = LakeSettings.Budget;
    float reach = float.NaN;
    do
    {
      if (working == null)
      {
        while (queueAt < queue.Count)
        {
          var next = queue[queueAt++];
          if (next.Lake.Shape != null && !next.Lake.Tiles.ContainsKey(next.Tile) && active.Contains(next.Lake))
          {
            working = next;
            break;
          }
        }
        if (working == null)
          break;
      }
      var (rt, t) = working.Value;
      var shape = rt.Shape;
      if (shape == null)
      {
        working = null;
        continue;
      }
      try
      {
        if (!rt.Tiles.TryGetValue(t, out var objects))
        {
          objects = new TileObjects();
          rt.Tiles[t] = objects;
          if (float.IsNaN(reach))
            reach = LakeBuild.MeshReach(template);
          LakeBuild.MakeTile(shape, t, objects, template, reach);
          if (objects.Empty)
            BetterContinents.Log($"Lakes: lake '{rt.Lake.Name}': its tile ({shape.Tiles[t].Tx}, {shape.Tiles[t].Tz}) has no triangle in the mask: no surface is drawn for its {shape.Tiles[t].BoxCount} box(es)");
          if (!objects.Empty && !rt.Logged)
          {
            rt.Logged = true;
            BetterContinents.Log($"Lakes: lake '{rt.Lake.Name}' built at ({rt.Lake.X:0}, {rt.Lake.Z:0}): level {shape.Level:0.00}, {shape.AreaM2:0} m2 of water, "
              + $"{shape.Tiles.Length} tile(s) ({shape.Tiles.Count(t => t.Full)} with the game's own mesh), {shape.Boxes.Length} box(es)");
          }
        }
        else if (objects.Boxes < shape.Tiles[t].BoxCount)
          LakeBuild.MakeBox(shape, t, objects, template);
        // the tile is finished when it grazed the mask only, or all its boxes are made
        if (rt.Tiles.TryGetValue(t, out var state) && (state.Empty || state.Boxes >= shape.Tiles[t].BoxCount))
          working = null;
      }
      catch (Exception e)
      {
        BetterContinents.LogError($"Lakes: lake '{rt.Lake.Name}': making its water failed ({e.GetType().Name}: {e.Message}); the lake is left. {e.StackTrace}");
        rt.Failed = e.Message;
        rt.CancelAndClear();
        active.Remove(rt);
        working = null;
      }
      stepsThisRun++;
    }
    while (watch.Elapsed.TotalMilliseconds < budget);
    lastBuildMs = (float)watch.Elapsed.TotalMilliseconds;
    worstBuildMs = Math.Max(worstBuildMs, lastBuildMs);
  }

  // ---- for the commands and the tests ----------------------------------------------------------------------------------------------

  /// <summary>Words about a lake for bc_lake list and info: "not built", "working out its shape", "built: 12 of 40 tiles, 130 of 290 boxes", or why it failed.</summary>
  internal static string StateOf(Lake lake)
  {
    var rt = all.FirstOrDefault(r => r.Lake.Equals(lake));
    if (rt == null)
      return current == null ? "off" : "not in the world's set";
    if (rt.Failed != null)
      return "FAILED: " + rt.Failed;
    if (rt.Shape == null)
      return rt.Task != null ? "working out its shape" : Graphics ? "not built" : "held (this machine draws nothing)";
    int total = rt.Shape.Tiles.Length;
    if (rt.Tiles.Count == 0)
      return $"shape ready ({total} tiles, {rt.Shape.Boxes.Length} boxes), no tile made";
    int tilesDone = rt.Tiles.Values.Count(x => !x.Empty);
    return $"built: {tilesDone} of {total} tiles, {rt.BoxesMade} of {rt.Shape.Boxes.Length} boxes";
  }

  /// <summary>The n lakes nearest to a point (the lake's box), with their state: bc_lake list.</summary>
  internal static IEnumerable<(Lake Lake, float Distance, string State)> Nearest(int n, Vector3 at) =>
    all.OrderBy(rt => rt.DistanceTo(at)).Take(Math.Max(0, n)).Select(rt => (rt.Lake, rt.DistanceTo(at), StateOf(rt.Lake))).ToList();

  /// <summary>The shape of a lake by name once it is ready (the tests); null otherwise.</summary>
  internal static LakeShape? ShapeOf(string name) => all.FirstOrDefault(rt => rt.Lake.Name == name)?.Shape;

  /// <summary>How many tiles and boxes of a lake are made now (the tests).</summary>
  internal static (int Tiles, int Boxes) MadeOf(string name)
  {
    var rt = all.FirstOrDefault(r => r.Lake.Name == name);
    return rt == null ? (0, 0) : (rt.TilesWithWater, rt.BoxesMade);
  }

  /// <summary>Drops everything the streamer made (tiles, boxes, shapes) and makes it again as the player is near (the tests, a command). The set, the patches and the
  /// map are left as they are.</summary>
  internal static void Rebuild()
  {
    foreach (var rt in all)
    {
      rt.CancelAndClear();
      rt.Logged = false;
    }
    active.Clear();
    queue = [];
    queueAt = 0;
    working = null;
    LakeWater.Clear();
    LakeBuild.DestroyRoot();
    passTimer = 1f;
  }

  /// <summary>Lines for bc_lake info: the state, the counts, the template, the cost of the last steps.</summary>
  internal static void Report(Action<string> output)
  {
    output($"Lakes: the streamer is {stateWords}.");
    if (current == null)
      return;
    int shapes = all.Count(rt => rt.Shape != null), failed = all.Count(rt => rt.Failed != null), computing = all.Count(rt => rt.Task != null);
    int tiles = all.Sum(rt => rt.TilesWithWater), boxes = all.Sum(rt => rt.BoxesMade);
    output($"Lakes: {all.Count} lake(s): {shapes} shape(s) ready, {computing} being worked out, {failed} failed; {tiles} tile(s) and {boxes} box(es) made, {queue.Count - queueAt} tile(s) waiting; "
      + $"range {LakeSettings.Range:0} m, budget {LakeSettings.Budget:0.#} ms (the last step {lastBuildMs:0.0} ms, the longest {worstBuildMs:0.0} ms); Water.Instances {Water.Instances.Count}, WaterVolume.Instances {WaterVolume.Instances.Count}.");
    output($"Lakes: the map {(LakeMap.Painted ? "has the lakes painted on it" : "has no lakes on it (yet)")}.");
    foreach (var rt in all.Where(r => r.Failed != null).Take(5))
      output($"Lakes: lake '{rt.Lake.Name}' failed: {rt.Failed}");
    output("Lakes: " + LakeWater.DescribeTemplate().Replace("\n", " | "));
  }

  /// <summary>The words for bc_lake info when lakes are off here (Tarn is installed ...) as the streamer sees it.</summary>
  internal static string StateWords => stateWords;
}

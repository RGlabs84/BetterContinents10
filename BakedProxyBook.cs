// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace BetterContinents;

// WHEN A ZONE HAS A SHADOW PROXY, AND WHAT BECOMES OF IT.
//
// Every zone in the draw ring that the camera's shadows can reach gets, in time, up to two proxies (ProxyMerge): the far one, made of each
// piece's last level of detail, and the near one, made of its first, which a zone within one zone of the camera's has as well and draws instead.
// Both are made lazily and a few at a time: the parts' shapes are settled on the main thread (BakedMeshShapes), a worker merges, and the
// main thread makes the meshes a few milliseconds a frame. A zone's instances are told to stop casting (BuiltZone.ProxyCover) only for the kinds
// every ready proxy of it covers, and a proxy is never taken away before a drawing job that knows it is gone has been adopted: no instance is
// ever without a shadow, and for a few frames it may have two.
//
// This class is the bookkeeping, free of the engine: what makes a mesh, destroys one, draws one and settles a part are delegates (BakedProxy
// supplies them), so that tools/placement-tests runs it.

/// <summary>What a worker leaves of one merge.</summary>
internal sealed class ProxyBuild
{
  /// <summary>0 running, 1 done, 2 failed.</summary>
  internal volatile int Stage;
  internal ProxyBuilt? Built;
  internal Exception? Error;
}

/// <summary>One of a zone's two proxies: the near one (first level of detail) or the far one (last).</summary>
internal sealed class ProxyVariant(bool near)
{
  internal const int None = 0, Shapes = 1, Waiting = 2, Building = 3, Uploading = 4, Ready = 5, Failed = 6;
  internal readonly bool Near = near;
  internal int State;
  internal ProxyBuild? Build;
  internal ProxyChunk[] Chunks = [];
  /// <summary>How many of Chunks have their mesh.</summary>
  internal int Uploaded;
  /// <summary>Per entry of the zone's Kinds, whether this proxy holds a part of it.</summary>
  internal bool[]? Covered;
  /// <summary>The proxy is drawn once BakedDraw.ProxyAdopted reaches this: another proxy of the zone is drawn until a job that knows the coverage this one
  /// narrows has been adopted. 0 for the first proxy of a zone to be ready.</summary>
  internal int ActiveAt;
  internal long Vertices, Triangles, Bytes;
  internal string? FailedWhy;

  internal bool IsReady => State == Ready;
}

/// <summary>A built zone's shadow proxies.</summary>
internal sealed class ZoneProxy
{
  internal readonly BuiltZone Zone;
  /// <summary>The Shadows setting the proxies are made under (a change makes them again).</summary>
  internal readonly BakedShadows Policy;
  internal readonly ProxyVariant Near = new(true), Far = new(false);
  internal readonly ProxyVariant[] Both;
  /// <summary>The world box of the zone's pieces (minimum x, y, z, then maximum).</summary>
  internal readonly float[] Box = new float[6];
  /// <summary>The zone object's place: the meshes' vertices are relative to it.</summary>
  internal readonly Matrix4x4 ToWorld;
  /// <summary>The zone has left the ring (or the proxies are off): its proxies are gone or going.</summary>
  internal bool Dead;
  /// <summary>Zones from the camera's (Chebyshev), as of the last tick; and when the zone was last a near one (seconds).</summary>
  internal int Cheb;
  internal float NearSeen = -1f;
  internal float Distance2;

  internal ZoneProxy(BuiltZone zone, BakedShadows policy)
  {
    Zone = zone;
    Policy = policy;
    Both = [Near, Far];
    var m = Matrix4x4.identity;
    m.m03 = zone.Zx * 64f;
    m.m23 = zone.Zz * 64f;
    ToWorld = m;
    Box[0] = Box[1] = Box[2] = float.MaxValue;
    Box[3] = Box[4] = Box[5] = float.MinValue;
    for (int c = 0; c < BakedDraw.Cells; c++)
    {
      if (!zone.CellUsed[c])
        continue;
      for (int k = 0; k < 3; k++)
      {
        Box[k] = Math.Min(Box[k], zone.CellBox[c * 6 + k]);
        Box[3 + k] = Math.Max(Box[3 + k], zone.CellBox[c * 6 + 3 + k]);
      }
    }
  }

  internal ProxyVariant Variant(bool near) => near ? Near : Far;
  internal bool AnyReady => Near.IsReady || Far.IsReady;
}

/// <summary>What the book is told each frame.</summary>
internal struct ProxyFrame
{
  internal Vector3 Camera;
  /// <summary>The camera's zone.</summary>
  internal int ZoneX, ZoneZ;
  internal BuiltZone[] Ring;
  internal int RingVersion;
  internal BakedShadows Policy;
  /// <summary>Proxies are wanted at all (the switch is on, and a proxy material exists).</summary>
  internal bool Enabled;
  /// <summary>Drawing is hidden (bc_bake hide).</summary>
  internal bool Hidden;
  /// <summary>How far from the camera a shadow can fall (metres); 0 where the game draws no shadows.</summary>
  internal float Reach;
  internal float Now;
}

internal sealed class ProxyBook
{
  /// <summary>A near proxy stays this long (seconds) after its zone stops being a near one.</summary>
  internal const float Linger = 4f;
  /// <summary>How many merges run on workers at once.</summary>
  internal const int MaxBuilds = 2;
  /// <summary>The main thread's milliseconds a frame: settling shapes, and making meshes (one mesh at least a frame, whatever it costs).</summary>
  internal const double ShapeBudgetMs = 1.5, UploadBudgetMs = 2.0;
  /// <summary>The graphics memory the near proxies may take together (the default of NearBudget); a near proxy that would take it past is not made (the far one stands).</summary>
  internal const long NearBudgetBytes = 320L << 20;
  /// <summary>A far proxy is dropped when its zone is this much farther than the shadows reach (metres): so that a zone at the edge does not come and go.</summary>
  internal const float DropMargin = 64f;

  internal long NearBudget = NearBudgetBytes;

  // ---- what the engine supplies --------------------------------------------------------------------------------------------------

  internal Action<Action> Work = job => ThreadPool.QueueUserWorkItem(_ => job());
  /// <summary>Settles a part's shadow shape (RenderPart.ProxyState and Shape); true once it is settled (it may take frames: a read back from the GPU).</summary>
  internal Func<RenderPart, bool> Settle = _ => true;
  /// <summary>Makes a chunk's mesh and how it is drawn, and lets go of its numbers; false if it cannot.</summary>
  internal Func<ProxyChunk, ZoneProxy, bool> Upload = (_, _) => true;
  internal Action<ProxyChunk> Free = _ => { };
  internal Action<ProxyChunk, Matrix4x4> Draw = (_, _) => { };

  // ---- state ----------------------------------------------------------------------------------------------------------------------

  private sealed class Retired(ProxyChunk[] chunks, int at, Matrix4x4 toWorld)
  {
    internal readonly ProxyChunk[] Chunks = chunks;
    internal readonly int At = at;
    internal readonly Matrix4x4 ToWorld = toWorld;
  }

  private readonly List<ZoneProxy> live = [];
  private readonly List<Retired> retired = [];
  private readonly List<ZoneProxy> pickZones = [];
  private readonly List<ProxyVariant> pickVariants = [];
  private int building;
  private int ringVersion = -1;
  private BakedShadows policy = BakedShadows.All;
  private float now;

  // statistics
  private int builds, uploads, failures, budgetSkips, drawsLastFrame;
  private double buildMsSum, buildMsMax, uploadMsSum, uploadMsMax;
  private double frameUploadMs;

  internal IReadOnlyList<ZoneProxy> Zones => live;
  internal int Retiring => retired.Count;
  internal int Building => Volatile.Read(ref building);

  // ---- the frame --------------------------------------------------------------------------------------------------------------------

  /// <summary>Main thread, once a frame before the drawing job is started: follows the ring, starts and finishes proxies, takes back what is gone.</summary>
  internal void Tick(in ProxyFrame f)
  {
    now = f.Now;
    if (f.Hidden)
    {
      // nothing is drawn: what was taken back goes at once
      FreeRetired(true);
      return;
    }
    bool on = f.Enabled && f.Policy != BakedShadows.Off && f.Reach > 0f;
    if (!on)
    {
      if (live.Count > 0)
      {
        foreach (var zp in live)
          RetireZone(zp);
        live.Clear();
      }
      ringVersion = -1;
      FreeRetired(false);
      return;
    }
    Sync(f);
    Wants(f);
    SettleShapes();
    StartBuilds();
    PollBuilds();
    MakeMeshes();
    FreeRetired(false);
  }

  // the proxies follow the ring: a zone that leaves it (also a zone that is built again: a new BuiltZone) loses its proxies, a zone that joins gets some
  private void Sync(in ProxyFrame f)
  {
    if (f.RingVersion == ringVersion && f.Policy == policy)
      return;
    ringVersion = f.RingVersion;
    policy = f.Policy;
    var inRing = new HashSet<BuiltZone>(f.Ring);
    for (int i = live.Count - 1; i >= 0; i--)
    {
      var zp = live[i];
      if (inRing.Contains(zp.Zone) && zp.Policy == f.Policy)
        continue;
      RetireZone(zp);
      live.RemoveAt(i);
    }
    foreach (var zone in f.Ring)
    {
      if (zone.Proxy != null && !zone.Proxy.Dead)
        continue;
      var zp = new ZoneProxy(zone, f.Policy);
      zone.Proxy = zp;
      live.Add(zp);
    }
  }

  // which proxies each zone wants, by where it is from the camera
  private void Wants(in ProxyFrame f)
  {
    float reach2 = f.Reach * f.Reach, drop2 = (f.Reach + DropMargin) * (f.Reach + DropMargin);
    foreach (var zp in live)
    {
      zp.Cheb = Math.Max(Math.Abs(zp.Zone.Zx - f.ZoneX), Math.Abs(zp.Zone.Zz - f.ZoneZ));
      zp.Distance2 = BoxDistance2(zp.Box, f.Camera);
      bool inReach = zp.Distance2 <= reach2;
      if (zp.Cheb <= ProxyRules.NearZones && inReach)
        zp.NearSeen = now;
      bool wantNear = zp.Cheb <= ProxyRules.NearZones && inReach;
      bool keepNear = zp.NearSeen >= 0f && now - zp.NearSeen < Linger;
      bool wantFar = f.Policy == BakedShadows.All && inReach;
      bool keepFar = f.Policy == BakedShadows.All && zp.Distance2 <= drop2;
      Want(zp, zp.Near, wantNear, keepNear);
      Want(zp, zp.Far, wantFar, keepFar);
    }
  }

  private void Want(ZoneProxy zp, ProxyVariant v, bool want, bool keep)
  {
    if (v.State == ProxyVariant.None)
    {
      if (want)
        v.State = ProxyVariant.Shapes;
    }
    else if (!keep)
      RetireVariant(zp, v);
  }

  // ---- shapes, merges, meshes ---------------------------------------------------------------------------------------------------------

  private void Pick(int state)
  {
    pickZones.Clear();
    pickVariants.Clear();
    foreach (var zp in live)
      foreach (var v in zp.Both)
        if (v.State == state)
        {
          pickZones.Add(zp);
          pickVariants.Add(v);
        }
    // the nearest zones first, near proxies before far ones
    for (int i = 1; i < pickVariants.Count; i++)
    {
      var z = pickZones[i];
      var v = pickVariants[i];
      int j = i - 1;
      while (j >= 0 && Before(z, v, pickZones[j], pickVariants[j]))
      {
        pickZones[j + 1] = pickZones[j];
        pickVariants[j + 1] = pickVariants[j];
        j--;
      }
      pickZones[j + 1] = z;
      pickVariants[j + 1] = v;
    }
  }

  private static bool Before(ZoneProxy a, ProxyVariant av, ZoneProxy b, ProxyVariant bv) =>
    av.Near != bv.Near ? av.Near : a.Distance2 < b.Distance2;

  private void SettleShapes()
  {
    Pick(ProxyVariant.Shapes);
    var clock = Stopwatch.StartNew();
    for (int i = 0; i < pickVariants.Count; i++)
    {
      if (i > 0 && clock.Elapsed.TotalMilliseconds >= ShapeBudgetMs)
        return;
      var zp = pickZones[i];
      var v = pickVariants[i];
      bool settled = true;
      foreach (var ki in zp.Zone.Kinds)
      {
        var kind = ki.Kind;
        int lod = ProxyRules.LodOf(kind, v.Near);
        foreach (int index in ProxyRules.PartsOf(kind, v.Near))
        {
          var part = kind.Piece!.Parts[index];
          if (!BakedDraw.CastsFor(kind, part, lod, zp.Policy))
            continue;
          if (!Settle(part))
            settled = false;
        }
      }
      if (settled)
        v.State = ProxyVariant.Waiting;
    }
  }

  private void StartBuilds()
  {
    if (Volatile.Read(ref building) >= MaxBuilds)
      return;
    Pick(ProxyVariant.Waiting);
    for (int i = 0; i < pickVariants.Count && Volatile.Read(ref building) < MaxBuilds; i++)
    {
      var zone = pickZones[i].Zone;
      var shadows = pickZones[i].Policy;
      var v = pickVariants[i];
      bool near = v.Near;
      var build = new ProxyBuild();
      v.Build = build;
      v.State = ProxyVariant.Building;
      Interlocked.Increment(ref building);
      Work(() =>
      {
        try
        {
          build.Built = ProxyMerge.Merge(zone, near, shadows);
          build.Stage = 1;
        }
        catch (Exception e)
        {
          build.Error = e;
          build.Stage = 2;
        }
        finally
        {
          Interlocked.Decrement(ref building);
        }
      });
    }
  }

  private void PollBuilds()
  {
    foreach (var zp in live)
      foreach (var v in zp.Both)
      {
        if (v.State != ProxyVariant.Building || v.Build == null || v.Build.Stage == 0)
          continue;
        var build = v.Build;
        v.Build = null;
        if (build.Stage == 2)
        {
          Fail(v, build.Error?.Message ?? "the merge failed");
          if (build.Error != null)
            BetterContinents.LogError($"baked placements: the shadow proxy of zone {zp.Zone.Zx},{zp.Zone.Zz} could not be made: {build.Error}");
          continue;
        }
        var built = build.Built!;
        builds++;
        buildMsSum += built.Ms;
        buildMsMax = Math.Max(buildMsMax, built.Ms);
        if (v.Near && LiveBytes(true) + built.Bytes > NearBudget)
        {
          budgetSkips++;
          Fail(v, "the near proxies' memory budget");
          continue;
        }
        v.Chunks = built.Chunks;
        v.Covered = built.KindCovered;
        v.Vertices = built.Vertices;
        v.Triangles = built.Triangles;
        v.Bytes = built.Bytes;
        v.Uploaded = 0;
        v.State = built.Chunks.Length == 0 ? ProxyVariant.Ready : ProxyVariant.Uploading;
        if (v.State == ProxyVariant.Ready)
          BecameReady(zp, v);
      }
  }

  private void MakeMeshes()
  {
    frameUploadMs = 0;
    Pick(ProxyVariant.Uploading);
    var clock = Stopwatch.StartNew();
    bool any = false;
    for (int i = 0; i < pickVariants.Count; i++)
    {
      var zp = pickZones[i];
      var v = pickVariants[i];
      while (v.State == ProxyVariant.Uploading && v.Uploaded < v.Chunks.Length)
      {
        if (any && clock.Elapsed.TotalMilliseconds >= UploadBudgetMs)
        {
          EndFrame();
          return;
        }
        var chunk = v.Chunks[v.Uploaded];
        long t0 = Stopwatch.GetTimestamp();
        bool ok;
        try
        {
          ok = Upload(chunk, zp);
        }
        catch (Exception e)
        {
          BetterContinents.LogError($"baked placements: a shadow proxy mesh could not be made: {e}");
          ok = false;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        any = true;
        if (!ok)
        {
          Fail(v, "a mesh could not be made");
          break;
        }
        chunk.Live = true;
        v.Uploaded++;
        uploads++;
        uploadMsSum += ms;
        frameUploadMs += ms;
      }
      if (v.State == ProxyVariant.Uploading && v.Uploaded == v.Chunks.Length)
        BecameReady(zp, v);
    }
    EndFrame();
  }

  private void EndFrame() => uploadMsMax = Math.Max(uploadMsMax, frameUploadMs);

  // a proxy that cannot be made: its meshes go, the zone keeps casting by instancing (or by its other proxy)
  private void Fail(ProxyVariant v, string why)
  {
    failures++;
    foreach (var chunk in v.Chunks)
      FreeChunk(chunk);
    v.Chunks = [];
    v.Covered = null;
    v.Uploaded = 0;
    v.Build = null;
    v.State = ProxyVariant.Failed;
    v.FailedWhy = why;
  }

  private void FreeChunk(ProxyChunk chunk)
  {
    if (chunk.Live)
      Free(chunk);
    chunk.Live = false;
    chunk.Vertices = chunk.Normals = null;
    chunk.Indices = null;
  }

  // ---- coverage ----------------------------------------------------------------------------------------------------------------------

  private void BecameReady(ZoneProxy zp, ProxyVariant v)
  {
    v.State = ProxyVariant.Ready;
    bool other = zp.Variant(!v.Near).IsReady;
    Publish(zp);
    v.ActiveAt = other ? BakedDraw.ProxyVersion : 0;
  }

  /// <summary>The kinds of a zone that every ready proxy of it covers (a draw that is not there casts nothing, so whichever is drawn must hold them all).</summary>
  internal static bool[]? Coverage(ZoneProxy zp)
  {
    if (zp.Dead)
      return null;
    bool[]? mask = null;
    foreach (var v in zp.Both)
    {
      if (!v.IsReady || v.Covered == null)
        continue;
      if (mask == null)
        mask = (bool[])v.Covered.Clone();
      else
        for (int g = 0; g < mask.Length && g < v.Covered.Length; g++)
          mask[g] &= v.Covered[g];
    }
    if (mask != null && Array.IndexOf(mask, true) < 0)
      return null;
    return mask;
  }

  private void Publish(ZoneProxy zp)
  {
    zp.Zone.ProxyCover = Coverage(zp);
    BakedDraw.ProxyVersion++;
  }

  // ---- taking proxies back --------------------------------------------------------------------------------------------------------------

  private void RetireZone(ZoneProxy zp)
  {
    zp.Dead = true;
    if (zp.Zone.Proxy == zp)
      zp.Zone.Proxy = null;
    var held = new List<ProxyChunk>();
    foreach (var v in zp.Both)
    {
      // only a ready proxy is leaned on; the meshes of one that is still being made go now
      var mine = new List<ProxyChunk>();
      Collect(v, mine);
      if (v.IsReady)
        held.AddRange(mine);
      else
        foreach (var chunk in mine)
          FreeChunk(chunk);
      v.State = ProxyVariant.None;
    }
    // the instances stop leaning on it, and its meshes stay drawn until a job that knows has been adopted
    if (zp.Zone.ProxyCover != null)
    {
      zp.Zone.ProxyCover = null;
      BakedDraw.ProxyVersion++;
    }
    if (held.Count > 0)
      retired.Add(new Retired(held.ToArray(), BakedDraw.ProxyVersion, zp.ToWorld));
  }

  private void RetireVariant(ZoneProxy zp, ProxyVariant v)
  {
    bool wasReady = v.IsReady;
    var held = new List<ProxyChunk>();
    Collect(v, held);
    v.State = ProxyVariant.None;
    if (!wasReady)
    {
      // never relied on: its meshes, if any were made, go now
      foreach (var chunk in held)
        FreeChunk(chunk);
      return;
    }
    var other = zp.Variant(!v.Near);
    Publish(zp);
    if (other.IsReady && other.ActiveAt <= BakedDraw.ProxyAdopted)
    {
      // the other proxy is drawn and holds more kinds than the two did together
      foreach (var chunk in held)
        FreeChunk(chunk);
      return;
    }
    retired.Add(new Retired(held.ToArray(), BakedDraw.ProxyVersion, zp.ToWorld));
  }

  // The variant's chunks: those with a mesh are returned (to be freed now or when a job has been adopted), the others have nothing to free.
  private void Collect(ProxyVariant v, List<ProxyChunk> into)
  {
    foreach (var chunk in v.Chunks)
    {
      if (chunk.Live)
        into.Add(chunk);
      else
        FreeChunk(chunk);
    }
    v.Chunks = [];
    v.Covered = null;
    v.Build = null;
    v.Uploaded = 0;
    v.Vertices = v.Triangles = v.Bytes = 0;
    v.FailedWhy = null;
  }

  // what was taken back goes when a job made after it has been adopted: no frame in front still leans on it
  private void FreeRetired(bool all)
  {
    for (int i = retired.Count - 1; i >= 0; i--)
    {
      var r = retired[i];
      if (!all && r.At > BakedDraw.ProxyAdopted)
        continue;
      foreach (var chunk in r.Chunks)
        FreeChunk(chunk);
      retired.RemoveAt(i);
    }
  }

  /// <summary>Frees everything now (a world ends, or the layer goes).</summary>
  internal void Reset()
  {
    foreach (var zp in live)
    {
      zp.Dead = true;
      zp.Zone.ProxyCover = null;
      zp.Zone.Proxy = null;
      foreach (var v in zp.Both)
      {
        foreach (var chunk in v.Chunks)
          FreeChunk(chunk);
        v.Chunks = [];
        v.State = ProxyVariant.None;
        v.Build = null;
      }
    }
    live.Clear();
    FreeRetired(true);
    ringVersion = -1;
    BakedDraw.ProxyVersion++;
  }

  // ---- drawing -----------------------------------------------------------------------------------------------------------------------

  private bool Drawable(ProxyVariant v) => v.IsReady && v.ActiveAt <= BakedDraw.ProxyAdopted;

  /// <summary>The proxy of a zone that is drawn now: the near one while the zone is one of the camera's near ones (or when it has no other), else the far one.</summary>
  internal ProxyVariant? Drawn(ZoneProxy zp)
  {
    bool near = Drawable(zp.Near), far = Drawable(zp.Far);
    if (near && (zp.Cheb <= ProxyRules.NearZones || !far))
      return zp.Near;
    return far ? zp.Far : null;
  }

  /// <summary>Main thread, once a frame after the drawing job's own submission: draws every proxy mesh the shadows can reach.</summary>
  internal void Submit(in ProxyFrame f)
  {
    drawsLastFrame = 0;
    if (f.Hidden || f.Reach <= 0f)
      return;
    float reach2 = f.Reach * f.Reach;
    foreach (var zp in live)
    {
      var v = Drawn(zp);
      if (v != null)
        DrawChunks(v.Chunks, zp.ToWorld, zp.Zone.Zx * 64f, zp.Zone.Zz * 64f, f.Camera, reach2);
    }
    foreach (var r in retired)
      DrawChunks(r.Chunks, r.ToWorld, r.ToWorld.m03, r.ToWorld.m23, f.Camera, reach2);
  }

  private void DrawChunks(ProxyChunk[] chunks, Matrix4x4 toWorld, float ox, float oz, Vector3 cam, float reach2)
  {
    foreach (var chunk in chunks)
    {
      if (!chunk.Live)
        continue;
      float dx = Math.Max(Math.Max(chunk.MinX + ox - cam.x, cam.x - (chunk.MaxX + ox)), 0f);
      float dy = Math.Max(Math.Max(chunk.MinY - cam.y, cam.y - chunk.MaxY), 0f);
      float dz = Math.Max(Math.Max(chunk.MinZ + oz - cam.z, cam.z - (chunk.MaxZ + oz)), 0f);
      if (dx * dx + dy * dy + dz * dz > reach2)
        continue;
      Draw(chunk, toWorld);
      drawsLastFrame++;
    }
  }

  private static float BoxDistance2(float[] box, Vector3 p)
  {
    if (box[0] > box[3])
      return float.MaxValue;
    float dx = Math.Max(Math.Max(box[0] - p.x, p.x - box[3]), 0f);
    float dy = Math.Max(Math.Max(box[1] - p.y, p.y - box[4]), 0f);
    float dz = Math.Max(Math.Max(box[2] - p.z, p.z - box[5]), 0f);
    return dx * dx + dy * dy + dz * dz;
  }

  // ---- numbers ----------------------------------------------------------------------------------------------------------------------

  private long LiveBytes(bool nearOnly)
  {
    long bytes = 0;
    foreach (var zp in live)
      foreach (var v in zp.Both)
        if ((v.State == ProxyVariant.Uploading || v.State == ProxyVariant.Ready) && (!nearOnly || v.Near))
          bytes += v.Bytes;
    foreach (var r in retired)
      foreach (var chunk in r.Chunks)
        if (!nearOnly)
          bytes += chunk.Bytes;
    return bytes;
  }

  /// <summary>The numbers bc_bake stats shows.</summary>
  internal ProxyNumbers Numbers(int zonesInRing)
  {
    var n = new ProxyNumbers { ZonesInRing = zonesInRing, ZonesTracked = live.Count, Retiring = retired.Count, Builds = builds, Uploads = uploads, Failures = failures, BudgetSkips = budgetSkips, Draws = drawsLastFrame };
    n.BuildAverageMs = builds == 0 ? 0 : buildMsSum / builds;
    n.BuildMaxMs = buildMsMax;
    n.UploadAverageMs = uploads == 0 ? 0 : uploadMsSum / uploads;
    n.UploadMaxFrameMs = uploadMsMax;
    foreach (var zp in live)
    {
      if (zp.AnyReady)
        n.ZonesWithProxy++;
      foreach (var v in zp.Both)
      {
        if (v.IsReady)
        {
          if (v.Near) n.Near++; else n.Far++;
          n.Triangles += v.Triangles;
          n.Vertices += v.Vertices;
          n.Bytes += v.Bytes;
        }
        else if (v.State == ProxyVariant.Failed)
          n.FailedNow++;
        else if (v.State != ProxyVariant.None)
          n.Waiting++;
      }
    }
    foreach (var r in retired)
      foreach (var chunk in r.Chunks)
        n.RetiringBytes += chunk.Bytes;
    return n;
  }
}

internal struct ProxyNumbers
{
  internal int ZonesInRing, ZonesTracked, ZonesWithProxy, Near, Far, Waiting, FailedNow, Retiring, Builds, Uploads, Failures, BudgetSkips, Draws;
  internal long Triangles, Vertices, Bytes, RetiringBytes;
  internal double BuildAverageMs, BuildMaxMs, UploadAverageMs, UploadMaxFrameMs;

  internal readonly string Line()
  {
    var c = CultureInfo.InvariantCulture;
    return string.Format(c,
      "Shadow proxies: {0} of {1} ring zones have one ({2} near, from the pieces' full detail; {3} far, from their last level), {4:N0} triangles in {5:N0} vertices, {6:0.0} MB of graphics memory; " +
      "{7:N0} proxy draws last frame; {8} waiting, {9} failed now ({10} ever, {11} near ones over the memory budget), {12} being taken back.",
      ZonesWithProxy, ZonesInRing, Near, Far, Triangles, Vertices, Bytes / 1048576.0, Draws, Waiting, FailedNow, Failures, BudgetSkips, Retiring);
  }

  internal readonly string TimeLine()
  {
    var c = CultureInfo.InvariantCulture;
    return string.Format(c,
      "Shadow proxy cost: building (worker) {0:0.0} ms a proxy on average, {1:0.0} ms at most ({2:N0} built); making meshes (main thread) {3:0.00} ms a mesh on average, {4:0.00} ms a frame at most ({5:N0} meshes).",
      BuildAverageMs, BuildMaxMs, Builds, UploadAverageMs, UploadMaxFrameMs, Uploads);
  }
}

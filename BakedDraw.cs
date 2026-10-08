// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownDraw.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

/// <summary>Which instances cast shadows (the Shadows setting, 9.4).</summary>
internal enum BakedShadows
{
  /// <summary>LOD0 parts cast as their renderers say; the last LOD never casts.</summary>
  Near,
  /// <summary>Both cast: the game's own look (LOD1 starts inside the shadow distance).</summary>
  All,
  /// <summary>None cast.</summary>
  Off,
}

// The drawing of baked placements (U1): Valheim's own pieces by GPU instancing, batched across every zone of the draw ring. A worker
// culls 16 m cells against the camera's frustum and picks each instance's LOD at the player's LOD bias, whenever the camera has moved 2 m
// or turned 5 degrees (or the ring changed); the main thread only hands the finished matrix lists to Graphics.RenderMeshInstanced.
// Every instance keeps its own object-to-world matrix: the game's piece shader brightens each piece by a hash of its origin, which
// merged meshes would lose.
//
// A batch that casts is drawn in runs (segments), each with the bounds of what it holds: Unity culls every shadow pass (each cascade, each
// face of each point light that casts) by a call's bounds, so a call whose bounds spanned the whole ring was drawn into every one of them
// (VALtima's Britain d1 at U1: 4.8 M triangles submitted, 94.8 M drawn). Near the camera a segment is one zone; farther out, one of four
// slabs (east, west, north, south) that stay clear of the near zones, so the lights and the near cascades never meet them.
//
// A zone that has a shadow proxy (BakedProxyBook: its casters merged into a few meshes that are drawn for shadows alone) sends the instances the
// proxy covers to draws that cast nothing, so the shadow passes meet a few proxy meshes in place of the thousands of calls. What a proxy does
// not take (a cutout's shadow needs its texture, a mesh the CPU and the GPU would not give up) keeps casting by instancing, as before.
//
// Unity 6's RenderParams start with shadows, received shadows, light probes and reflection probes all OFF. Every one is set: a piece drawn
// without them is flat and unlit next to the game's own pieces.

/// <summary>One list of instance matrices in one buffer, with the world bounds of what it holds.</summary>
internal sealed class BatchBuf
{
  internal Matrix4x4[] M = [];
  internal int Count;
  internal float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
  /// <summary>The runs the list is drawn in: where each starts in M, and its bounds (six floats, minimum then maximum).</summary>
  internal int[] SegStart = new int[4];
  internal float[] SegBox = new float[24];
  /// <summary>Each run's key: the zone or slab it casts from (not negative), or NoCast for the run that casts nothing.</summary>
  internal int[] SegKey = new int[4];
  internal int Segs;
  private int segKey;
  /// <summary>The key of the run that casts no shadow (what a shadow proxy covers, and a batch that casts nothing).</summary>
  internal const int NoCast = -1;

  internal void Begin()
  {
    Count = 0;
    Segs = 0;
    MinX = MinY = MinZ = float.MaxValue;
    MaxX = MaxY = MaxZ = float.MinValue;
  }

  /// <summary>Instances added from here on belong to the segment <paramref name="key"/>: a new one unless it is the last one's.</summary>
  internal void Open(int key)
  {
    if (Segs > 0 && key == segKey)
      return;
    segKey = key;
    if (Segs == SegStart.Length)
    {
      Array.Resize(ref SegStart, Segs * 2);
      Array.Resize(ref SegBox, Segs * 12);
      Array.Resize(ref SegKey, Segs * 2);
    }
    SegStart[Segs] = Count;
    SegKey[Segs] = key;
    int at = Segs * 6;
    SegBox[at] = SegBox[at + 1] = SegBox[at + 2] = float.MaxValue;
    SegBox[at + 3] = SegBox[at + 4] = SegBox[at + 5] = float.MinValue;
    Segs++;
  }

  /// <summary>Where segment <paramref name="s"/> ends in M.</summary>
  internal int SegEnd(int s) => s + 1 < Segs ? SegStart[s + 1] : Count;

  internal Bounds SegBounds(int s)
  {
    int at = s * 6;
    var min = new Vector3(SegBox[at], SegBox[at + 1], SegBox[at + 2]);
    var max = new Vector3(SegBox[at + 3], SegBox[at + 4], SegBox[at + 5]);
    return new Bounds((min + max) * 0.5f, max - min);
  }

  /// <summary>The calls drawing this list takes, <paramref name="limit"/> instances a call at most; with <paramref name="castingOnly"/>, those of the runs that cast.</summary>
  internal int Calls(int limit, bool castingOnly = false)
  {
    int calls = 0;
    for (int s = 0; s < Segs; s++)
      if (!castingOnly || SegKey[s] != NoCast)
        calls += (SegEnd(s) - SegStart[s] + limit - 1) / limit;
    return calls;
  }

  internal void Add(in Matrix4x4 m)
  {
    if (Count == M.Length)
      Array.Resize(ref M, Math.Max(64, M.Length * 2));
    M[Count++] = m;
  }

  /// <summary>Takes in a box (six floats from <paramref name="at"/>, minimum then maximum), into the whole list's bounds and the open segment's.</summary>
  internal void Union(float[] box, int at)
  {
    int g = (Segs - 1) * 6;
    if (g >= 0)
    {
      if (box[at] < SegBox[g]) SegBox[g] = box[at];
      if (box[at + 1] < SegBox[g + 1]) SegBox[g + 1] = box[at + 1];
      if (box[at + 2] < SegBox[g + 2]) SegBox[g + 2] = box[at + 2];
      if (box[at + 3] > SegBox[g + 3]) SegBox[g + 3] = box[at + 3];
      if (box[at + 4] > SegBox[g + 4]) SegBox[g + 4] = box[at + 4];
      if (box[at + 5] > SegBox[g + 5]) SegBox[g + 5] = box[at + 5];
    }
    if (box[at] < MinX) MinX = box[at];
    if (box[at + 1] < MinY) MinY = box[at + 1];
    if (box[at + 2] < MinZ) MinZ = box[at + 2];
    if (box[at + 3] > MaxX) MaxX = box[at + 3];
    if (box[at + 4] > MaxY) MaxY = box[at + 4];
    if (box[at + 5] > MaxZ) MaxZ = box[at + 5];
  }

  internal Bounds Bounds
  {
    get
    {
      if (Count == 0)
        return new Bounds(Vector3.zero, Vector3.zero);
      var min = new Vector3(MinX, MinY, MinZ);
      var max = new Vector3(MaxX, MaxY, MaxZ);
      return new Bounds((min + max) * 0.5f, max - min);
    }
  }
}

/// <summary>
/// One draw list: a part of a palette entry's piece at one LOD (0, or 1 for the last one), in one variant of its material slot and one seed
/// bucket (section 2.5): every instance drawn with that mesh, material and property block, across every zone of the ring. Two buffers: the
/// main thread submits one while the worker fills the other.
/// </summary>
internal sealed class Batch
{
  internal readonly BakedKind Kind;
  internal readonly RenderPart Part;
  /// <summary>0: the piece's first LOD; 1: its last.</summary>
  internal readonly int Lod;
  internal readonly int Variant, Bucket;
  internal readonly BatchBuf[] Buf = [new BatchBuf(), new BatchBuf()];
  /// <summary>The buffer the main thread submits; the worker fills the other.</summary>
  internal int Front;
  /// <summary>Whether this batch casts shadows in the frame in front (the main thread's) and in the one the worker is making.</summary>
  internal bool Casts, JobCasts;
  /// <summary>The worker's scratch: the (zone, kind, cell) whose box this batch last took in.</summary>
  internal int Stamp;
  // main thread only
  internal RenderParams Params;
  internal Mesh? Mesh;
  internal bool Dead;
  /// <summary>Instances in one RenderMeshInstanced call: 1,023, or 511 for a shader that needs inverse matrices (found when a call is refused).</summary>
  internal int CallLimit = BakedDraw.MaxPerCall;

  internal Batch(BakedKind kind, RenderPart part, int lod, int variant, int bucket)
  {
    Kind = kind;
    Part = part;
    Lod = lod;
    Variant = variant;
    Bucket = bucket;
    var material = part.VariantMaterials != null && variant < part.VariantMaterials.Length && part.VariantMaterials[variant] != null
      ? part.VariantMaterials[variant]
      : part.Material;
    Mesh = part.Mesh;
    if (material == null || part.Mesh == null)
    {
      Dead = true;
      return;
    }
    Params = new RenderParams(material)
    {
      layer = kind.UnityLayer,
      shadowCastingMode = ShadowCastingMode.Off,
      receiveShadows = part.ReceiveShadows,
      lightProbeUsage = LightProbeUsage.BlendProbes,
      reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
      motionVectorMode = MotionVectorGenerationMode.Camera,
      matProps = part.BucketProps != null && bucket < part.BucketProps.Length ? part.BucketProps[bucket] : null,
    };
  }
}

/// <summary>What a cull-and-LOD job reads (set by the main thread before the job starts) and what it leaves.</summary>
internal sealed class CullJob
{
  internal int Id;
  internal float CamX, CamY, CamZ;
  /// <summary>Six planes, inside positive: normal x, y, z and the offset, as BakedFrustum.Make writes them.</summary>
  internal readonly float[] Planes = new float[24];
  /// <summary>A relative screen height h is reached at distance size * K / h (Unity's LOD groups, scaled by the LOD bias).</summary>
  internal float K;
  internal float DetailScale = 1f, DrawScale = 1.5f;
  internal BakedShadows Shadows;
  /// <summary>How far outside the frustum an instance still counts for the shadow it casts into the view (metres).</summary>
  internal float CasterMargin;
  internal BuiltZone[] Zones = [];
  internal Batch[] Batches = [];
  internal BakedKind[] Kinds = [];
  // what the job leaves: per kind (by BakedKind.Index) how many instances it put at each LOD, and its own cost
  internal int[] KindLod0 = [], KindLod1 = [];
  internal int ZonesSeen, CellsSeen, InstancesDrawn;
  internal double Ms;
  internal Exception? Error;
  internal volatile bool Done;
  // the pose the job was made for, to decide when the next one is due
  internal Vector3 Eye, Forward;
  internal int RingVersion, BatchVersion;
  /// <summary>BakedDraw.LitVersion when the job was made: the lit copies it knew of (BuiltZone.Lit) are the ones published up to it.</summary>
  internal int LitVersion;
  /// <summary>BakedDraw.ProxyVersion when the job was made: the proxy coverage it read (BuiltZone.ProxyCover) is the one published up to it, or a later one.</summary>
  internal int ProxyVersion;
}

/// <summary>The camera's frustum as six planes, widened for how far the camera can move or turn before the next job.</summary>
internal static class BakedFrustum
{
  /// <summary>
  /// Writes six inward-facing planes (nx, ny, nz, d: a point is inside when n . p + d is not negative): the sides of the view, widened by
  /// <paramref name="angleMarginDeg"/> each, then every plane pushed out by <paramref name="moveMargin"/> metres, and the far plane.
  /// Whatever the camera sees after it moves that far or turns that much is inside, so a job made for one pose serves the next few frames.
  /// </summary>
  internal static void Make(float[] planes, Vector3 pos, Vector3 forward, Vector3 right, Vector3 up, float verticalFovDeg, float aspect, float far,
    float angleMarginDeg, float moveMargin)
  {
    float a = Mathf.Min(verticalFovDeg * 0.5f + angleMarginDeg, 85f) * Mathf.Deg2Rad;
    float b = Mathf.Min(Mathf.Atan(Mathf.Tan(verticalFovDeg * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg + angleMarginDeg, 85f) * Mathf.Deg2Rad;
    float ca = Mathf.Cos(a), sa = Mathf.Sin(a), cb = Mathf.Cos(b), sb = Mathf.Sin(b);
    Plane(planes, 0, right * cb + forward * sb, pos, moveMargin);        // left
    Plane(planes, 1, -right * cb + forward * sb, pos, moveMargin);       // right
    Plane(planes, 2, up * ca + forward * sa, pos, moveMargin);           // bottom
    Plane(planes, 3, -up * ca + forward * sa, pos, moveMargin);          // top
    Plane(planes, 4, forward, pos, moveMargin);                          // near: a point behind the camera by the margin is still in
    // far: a point farther than far (and the margin) from the camera along its axis is out
    var f = -forward;
    planes[20] = f.x; planes[21] = f.y; planes[22] = f.z;
    planes[23] = Vector3.Dot(forward, pos) + far + moveMargin;
  }

  private static void Plane(float[] planes, int i, Vector3 n, Vector3 pos, float margin)
  {
    int at = i * 4;
    planes[at] = n.x;
    planes[at + 1] = n.y;
    planes[at + 2] = n.z;
    planes[at + 3] = -(n.x * pos.x + n.y * pos.y + n.z * pos.z) + margin;
  }

  /// <summary>
  /// How far inside the frustum a box is, by its worst plane: the smallest, over the planes, of the signed distance of the box's corner
  /// that lies farthest toward the plane's inside. Not negative: the box touches or is inside the frustum; down to -margin: it is outside by
  /// no more than that; below: it is out. The six floats from <paramref name="at"/> are the minimum corner, then the maximum.
  /// </summary>
  internal static float Depth(float[] planes, float[] box, int at)
  {
    float depth = float.MaxValue;
    for (int p = 0; p < 6; p++)
    {
      int q = p * 4;
      float nx = planes[q], ny = planes[q + 1], nz = planes[q + 2];
      float x = nx >= 0f ? box[at + 3] : box[at];
      float y = ny >= 0f ? box[at + 4] : box[at + 1];
      float z = nz >= 0f ? box[at + 5] : box[at + 2];
      float d = nx * x + ny * y + nz * z + planes[q + 3];
      if (d < depth)
        depth = d;
    }
    return depth;
  }
}

internal static class BakedDraw
{
  /// <summary>The most instances of one RenderMeshInstanced call.</summary>
  internal const int MaxPerCall = 1023, MinPerCall = 511;
  /// <summary>A job is due when the camera has moved this far (metres) or turned this much (degrees) since the last one's pose.</summary>
  internal const float RefreshDistance = 2f, RefreshAngle = 5f;
  /// <summary>What a job's frustum allows the camera beyond that, for the frames a job takes to arrive.</summary>
  internal const float MoveMargin = 3f, AngleMargin = 8f;
  /// <summary>A piece one cell outside the view still casts into it.</summary>
  internal const float CasterReach = 16f;
  /// <summary>A casting batch is drawn a zone at a time within this many zones of the camera's (Chebyshev), by slab beyond.</summary>
  internal const int NearZones = 2;
  /// <summary>Added to the sort key of a zone no shadow proxy covers, so that the covered zones come first (SegmentKey stays below it).</summary>
  private const int ProxiedKeys = 1000;
  /// <summary>Cells of a zone: 4 x 4 of 16 m.</summary>
  internal const int CellsAcross = 4, Cells = 16;
  internal const float CellSize = 16f;

  // ---- who casts ----------------------------------------------------------------------------------------------------------------

  /// <summary>Whether a part's batch casts shadows at a LOD under the Shadows setting. Palette flag 2 (NoShadows) never casts.</summary>
  internal static bool CastsFor(BakedKind kind, RenderPart part, int lod, BakedShadows policy)
  {
    if (policy == BakedShadows.Off || kind.NoShadows || part.Shadows == ShadowCastingMode.Off)
      return false;
    return policy == BakedShadows.All || lod == 0;
  }

  // ---- the job: cull cells, choose each instance's LOD, fill the batches (worker thread) ------------------------------------------

  /// <summary>
  /// Fills the back buffer of every batch with the instances of the job's zones that are in view: a cell outside the frustum is skipped;
  /// every instance of the others goes into the batches of its LOD's parts, or nowhere when it is past the cull distance. Pure: no Unity object
  /// is touched.
  /// </summary>
  internal static void Fill(CullJob job)
  {
    long started = Stopwatch.GetTimestamp();
    try
    {
      Run(job);
    }
    catch (Exception e)
    {
      job.Error = e;
    }
    job.Ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    job.Done = true;
  }

  private static void Run(CullJob job)
  {
    foreach (var b in job.Batches)
    {
      b.Buf[1 - b.Front].Begin();
      b.JobCasts = !b.Dead && CastsFor(b.Kind, b.Part, b.Lod, job.Shadows);
      b.Stamp = 0;
    }
    int kinds = job.Kinds.Length;
    job.KindLod0 = new int[kinds];
    job.KindLod1 = new int[kinds];
    foreach (var k in job.Kinds)
      k.JobStamp = -1;
    var state = new byte[Cells];
    int stamp = 0, zones = 0, cells = 0, drawn = 0;
    float margin = job.Shadows == BakedShadows.Off ? 0f : job.CasterMargin;
    // the zones in segment order, so that each segment's instances arrive together; the zones a shadow proxy covers come first, since what they
    // send to a casting segment is only what the proxy cannot take. Each zone's coverage is read once.
    int camZx = Mathf.RoundToInt(job.CamX / 64f), camZz = Mathf.RoundToInt(job.CamZ / 64f);
    var keys = new int[job.Zones.Length];
    var sortKeys = new int[job.Zones.Length];
    bool[]?[] covers = new bool[job.Zones.Length][];
    var order = new int[job.Zones.Length];
    for (int i = 0; i < order.Length; i++)
    {
      covers[i] = job.Shadows == BakedShadows.Off ? null : job.Zones[i].ProxyCover;
      keys[i] = SegmentKey(job.Zones[i].Zx - camZx, job.Zones[i].Zz - camZz);
      sortKeys[i] = (covers[i] != null ? 0 : ProxiedKeys) + keys[i];
      order[i] = i;
    }
    Array.Sort(sortKeys, order);
    foreach (int zi in order)
    {
      var zone = job.Zones[zi];
      int key = keys[zi];
      var cover = covers[zi];
      // 2 = in view, 1 = outside it by no more than the casters' reach, 0 = out
      bool any = false;
      for (int c = 0; c < Cells; c++)
      {
        byte s = 0;
        if (zone.CellUsed[c])
        {
          float depth = BakedFrustum.Depth(job.Planes, zone.CellBox, c * 6);
          s = depth >= 0f ? (byte)2 : depth >= -margin ? (byte)1 : (byte)0;
        }
        state[c] = s;
        any |= s != 0;
      }
      if (!any)
        continue;
      zones++;
      for (int c = 0; c < Cells; c++)
        if (state[c] == 2)
          cells++;
      // the lit copies standing in this zone, as the main thread published them (one whole snapshot, never changed after): a Copy record with a lit
      // copy is not drawn here, or it would be drawn twice
      var lit = zone.Lit;
      for (int g = 0; g < zone.Kinds.Length; g++)
      {
        var ki = zone.Kinds[g];
        var kind = ki.Kind;
        bool covered = cover != null && g < cover.Length && cover[g];
        if (kind.JobStamp != job.Id)
          Memo(job, kind);
        for (int c = 0; c < Cells; c++)
        {
          if (state[c] == 0 || ki.CellStart[c + 1] == ki.CellStart[c])
            continue;
          stamp++;
          drawn += FillCell(job, ki, c, state[c] == 2, stamp, key, lit, covered);
        }
      }
    }
    job.ZonesSeen = zones;
    job.CellsSeen = cells;
    job.InstancesDrawn = drawn;
  }

  /// <summary>
  /// The segment a zone's instances go to in a casting batch, by where the zone is from the camera's (in zones): within NearZones, its own;
  /// beyond, the slab it lies in (east or west past NearZones on x, else north or south). A slab never comes nearer than NearZones zones.
  /// </summary>
  internal static int SegmentKey(int dx, int dz)
  {
    if (Math.Abs(dx) <= NearZones && Math.Abs(dz) <= NearZones)
      return (dx + NearZones) * (2 * NearZones + 1) + dz + NearZones;
    const int slabs = (2 * NearZones + 1) * (2 * NearZones + 1);
    return dx > NearZones ? slabs : dx < -NearZones ? slabs + 1 : dz > NearZones ? slabs + 2 : slabs + 3;
  }

  // the numbers an entry's instances are compared with, worked out once per job: past CullK2 times an instance's squared scale it is out;
  // within NearK2 times it, LOD0
  private static void Memo(CullJob job, BakedKind kind)
  {
    kind.JobStamp = job.Id;
    var piece = kind.Piece!;
    float size = piece.EffectiveSize;
    float cull = piece.EffectiveCullScreen;
    float near = piece.EffectiveNearScreen;
    float cullD = cull > 0f ? size * job.K / cull * job.DrawScale : float.PositiveInfinity;
    float nearD = near > 0f ? size * job.K / near * job.DetailScale : 0f;
    kind.CullK2 = cullD * cullD;
    kind.NearK2 = nearD * nearD;
    // no LOD switch: one set of parts, a piece flagged LOD0Only, or a piece with no LOD group
    kind.JobSingle = piece.SingleLod || near <= 0f || kind.Lod0Only;
  }

  private static int FillCell(CullJob job, KindInstances ki, int cell, bool inView, int stamp, int segment, bool[]? litCopies, bool covered)
  {
    var kind = ki.Kind;
    var piece = kind.Piece!;
    int from = ki.CellStart[cell], to = ki.CellStart[cell + 1];
    var lod = ki.Lod;
    float cx = job.CamX, cy = job.CamY, cz = job.CamZ;
    float nearK2 = kind.NearK2, cullK2 = kind.CullK2;
    bool single = kind.JobSingle;
    var combo = ki.Combo;
    var bucket = ki.Bucket;
    var copyIndex = litCopies != null ? ki.CopyIndex : null;
    bool plain = piece.Plain;
    var batches = kind.Batches;
    var parts = piece.Parts;
    int lod0 = 0, lod1 = 0;
    for (int i = from; i < to; i++)
    {
      // a Copy record whose lit copy stands is that copy's to draw
      if (copyIndex != null)
      {
        int ci = copyIndex[i];
        if (ci >= 0 && ci < litCopies!.Length && litCopies[ci])
          continue;
      }
      var r = lod[i];
      float dx = r.x - cx, dy = r.y - cy, dz = r.z - cz;
      float d2 = dx * dx + dy * dy + dz * dz;
      float s2 = r.w;
      if (d2 > cullK2 * s2)
        continue;
      bool near = single || d2 <= nearK2 * s2;
      var list = near ? piece.NearParts : piece.FarParts;
      var first = near ? kind.NearFirst : kind.FarFirst;
      int c = combo != null ? combo[i] : 0, k = bucket != null ? bucket[i] : 0;
      bool placed = false;
      for (int n = 0; n < list.Length; n++)
      {
        var part = parts[list[n]];
        int at = first[n];
        if (!plain)
          at += c / part.SlotStride % part.SlotCount * part.Buckets + (part.Buckets > 1 ? k : 0);
        var b = batches[at];
        // a part the zone's shadow proxy takes casts through the proxy, not through this draw
        bool cast = b.JobCasts && !(covered && part.ProxyState == RenderPart.ProxyOk);
        // an instance outside the view is only here for the shadow it casts into it; a shadow-only part is drawn only to cast
        if (b.Dead || (!inView && !cast) || (!cast && part.Shadows == ShadowCastingMode.ShadowsOnly))
          continue;
        var buf = b.Buf[1 - b.Front];
        if (b.Stamp != stamp)
        {
          b.Stamp = stamp;
          // what casts nothing is one segment: only the camera culls it, and fewer calls cost less
          buf.Open(cast ? segment : BatchBuf.NoCast);
          buf.Union(ki.CellBox, cell * 6);
        }
        buf.Add(part.LocalIndex < 0 ? ki.Root[i] : ki.Locals[part.LocalIndex][i]);
        placed = true;
      }
      if (placed)
      {
        if (near)
          lod0++;
        else
          lod1++;
      }
    }
    if (kind.Index >= 0 && kind.Index < job.KindLod0.Length)
    {
      job.KindLod0[kind.Index] += lod0;
      job.KindLod1[kind.Index] += lod1;
    }
    return lod0 + lod1;
  }

  // ---- the main thread: adopt a finished job, submit ----------------------------------------------------------------------------

  private static CullJob? frame;
  private static CullJob? running;
  private static int jobId;
  private static Batch[] batchSnapshot = [];
  private static BakedKind[] kindSnapshot = [];
  private static int batchVersion = -1;

  // the cost of the job, and of this thread's submitting, as running averages (stopwatch milliseconds)
  private static double jobMsAverage, jobMsMax, submitMsAverage;
  private static readonly double[] SubmitMsByLod = new double[2];
  private static int jobsRun;
  private static int callsLastFrame, instancesLastFrame;

  // the settings a job is made with (their defaults where the config is not bound: the offline suite)
  private static float DetailScale => BakedSettings.DetailScale?.Value ?? 1f;
  private static float DrawScale => BakedSettings.DrawScale?.Value ?? 1.5f;
  internal static BakedShadows ShadowSetting => BakedSettings.Shadows?.Value ?? BakedShadows.All;

  /// <summary>Drawing is switched off (bc_bake hide): no job, no submission.</summary>
  internal static bool Hidden;

  /// <summary>
  /// How many times the main thread has published a change of which Copy records have a lit copy standing (BuiltZone.Lit), and the last of them a
  /// finished job knew of. A change starts a job (the record is drawn, or not, by the next one); a lit copy that stops standing is destroyed only after
  /// a job that knows is adopted (LitAdopted reaches the version of its publication), so that no record is ever neither drawn nor lit.
  /// </summary>
  internal static int LitVersion, LitAdopted;

  /// <summary>
  /// The same for the shadow proxies (BakedProxyBook): how many times the main thread has published a change of which kinds a zone's proxy covers
  /// (BuiltZone.ProxyCover), and the last of them a finished job knew of. A change starts a job; a proxy whose coverage was taken back is destroyed
  /// only after a job that knows is adopted (ProxyAdopted reaches the version of its publication), so that no instance is ever left without a shadow.
  /// </summary>
  internal static int ProxyVersion, ProxyAdopted;

  /// <summary>Whether a job is running now.</summary>
  internal static bool Busy => running != null;

  /// <summary>Forgets every job and frame (a new world).</summary>
  internal static void Reset()
  {
    frame = null;
    running = null;
    batchSnapshot = [];
    kindSnapshot = [];
    batchVersion = -1;
  }

  /// <summary>
  /// Main thread, once a frame: takes in a finished job (its buffers become the ones submitted), and, when the camera has moved or turned
  /// enough since the last job, the ring or the settings changed, starts the next one. Returns true when a job finished this frame.
  /// </summary>
  internal static bool Tick(Camera cam, BuiltZone[] ring, int ringVersion)
  {
    bool adopted = false;
    var done = running;
    if (done != null && done.Done)
    {
      running = null;
      if (done.Error != null)
        LogError($"baked placements: the drawing job failed: {done.Error}");
      else
      {
        Adopt(done);
        adopted = true;
      }
    }
    if (running != null || Hidden)
      return adopted;
    float k = QualitySettings.lodBias / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
    Vector3 eye = cam.transform.position, forward = cam.transform.forward;
    bool due = frame == null || ringVersion != frame.RingVersion || BakedKinds.Version != frame.BatchVersion
      || LitVersion != frame.LitVersion || ProxyVersion != frame.ProxyVersion || DetailScale != frame.DetailScale || DrawScale != frame.DrawScale || ShadowSetting != frame.Shadows
      || (eye - frame.Eye).sqrMagnitude >= RefreshDistance * RefreshDistance
      || Vector3.Angle(forward, frame.Forward) >= RefreshAngle
      || Mathf.Abs(k - frame.K) > frame.K * 0.02f;
    if (!due)
      return adopted;
    Start(cam, ring, ringVersion, k);
    return adopted;
  }

  private static void Start(Camera cam, BuiltZone[] ring, int ringVersion, float k)
  {
    if (batchVersion != BakedKinds.Version)
    {
      batchVersion = BakedKinds.Version;
      var batches = new List<Batch>();
      var kinds = BakedKinds.All;
      for (int i = 0; i < kinds.Count; i++)
      {
        kinds[i].Index = i;
        batches.AddRange(kinds[i].Batches);
      }
      batchSnapshot = batches.ToArray();
      kindSnapshot = kinds.ToArray();
    }
    var t = cam.transform;
    var job = new CullJob
    {
      Id = ++jobId,
      Eye = t.position, Forward = t.forward,
      CamX = t.position.x, CamY = t.position.y, CamZ = t.position.z,
      K = k,
      DetailScale = DetailScale, DrawScale = DrawScale, Shadows = ShadowSetting,
      CasterMargin = CasterReach,
      Zones = ring, Batches = batchSnapshot, Kinds = kindSnapshot,
      RingVersion = ringVersion, BatchVersion = batchVersion, LitVersion = LitVersion, ProxyVersion = ProxyVersion,
    };
    BakedFrustum.Make(job.Planes, t.position, t.forward, t.right, t.up, cam.fieldOfView, cam.aspect, cam.farClipPlane, AngleMargin, MoveMargin);
    running = job;
    ThreadPool.QueueUserWorkItem(_ => Fill(job));
  }

  // the job's buffers become the ones in front; a batch's shadows follow the setting the job was made for
  internal static void Adopt(CullJob job)
  {
    foreach (var b in job.Batches)
    {
      b.Front = 1 - b.Front;
      if (b.Casts != b.JobCasts)
      {
        b.Casts = b.JobCasts;
        b.Params.shadowCastingMode = b.Casts ? b.Part.Shadows : ShadowCastingMode.Off;
      }
    }
    foreach (var kind in job.Kinds)
    {
      if (kind.Index >= 0 && kind.Index < job.KindLod0.Length)
      {
        kind.Lod0Count = job.KindLod0[kind.Index];
        kind.Lod1Count = job.KindLod1[kind.Index];
      }
    }
    frame = job;
    LitAdopted = Math.Max(LitAdopted, job.LitVersion);
    ProxyAdopted = Math.Max(ProxyAdopted, job.ProxyVersion);
    jobsRun++;
    jobMsAverage = jobsRun == 1 ? job.Ms : jobMsAverage * 0.95 + job.Ms * 0.05;
    jobMsMax = Math.Max(job.Ms, jobMsMax * 0.99);
  }

  /// <summary>Main thread, every frame: hands the front buffers to Unity. Never touches anything a job is writing.</summary>
  internal static void Submit()
  {
    if (frame == null || Hidden)
    {
      callsLastFrame = instancesLastFrame = 0;
      return;
    }
    long started = Stopwatch.GetTimestamp();
    BakedKind? current = null;
    int currentLod = 0;
    long groupStart = 0;
    int calls = 0, instances = 0;
    var batches = frame.Batches;
    double lod0Ticks = 0, lod1Ticks = 0;
    for (int n = 0; n < batches.Length; n++)
    {
      var b = batches[n];
      var buf = b.Buf[b.Front];
      if (buf.Count == 0 || b.Dead)
        continue;
      // time each (entry, LOD) group of batches on its own
      if (b.Kind != current || b.Lod != currentLod)
      {
        long now = Stopwatch.GetTimestamp();
        if (current != null)
          Close(current, currentLod, now - groupStart, ref lod0Ticks, ref lod1Ticks);
        current = b.Kind;
        currentLod = b.Lod;
        groupStart = now;
      }
      calls += Draw(b, buf);
      instances += buf.Count;
    }
    long end = Stopwatch.GetTimestamp();
    if (current != null)
      Close(current, currentLod, end - groupStart, ref lod0Ticks, ref lod1Ticks);
    double toMs = 1000.0 / Stopwatch.Frequency;
    submitMsAverage = submitMsAverage * 0.95 + (end - started) * toMs * 0.05;
    SubmitMsByLod[0] = SubmitMsByLod[0] * 0.95 + lod0Ticks * toMs * 0.05;
    SubmitMsByLod[1] = SubmitMsByLod[1] * 0.95 + lod1Ticks * toMs * 0.05;
    callsLastFrame = calls;
    instancesLastFrame = instances;
  }

  private static void Close(BakedKind kind, int lod, long ticks, ref double lod0Ticks, ref double lod1Ticks)
  {
    kind.SubmitTicks[lod] = kind.SubmitTicks[lod] * 0.9 + ticks * 0.1;
    if (lod == 0)
      lod0Ticks += ticks;
    else
      lod1Ticks += ticks;
  }

  private static int Draw(Batch b, BatchBuf buf)
  {
    var rp = b.Params;
    int calls = 0;
    try
    {
      for (int s = 0; s < buf.Segs; s++)
      {
        rp.worldBounds = buf.SegBounds(s);
        // a run the job made for what a proxy covers casts nothing, in a batch that casts elsewhere
        rp.shadowCastingMode = buf.SegKey[s] != BatchBuf.NoCast ? b.Part.Shadows : ShadowCastingMode.Off;
        int end = buf.SegEnd(s);
        for (int start = buf.SegStart[s]; start < end; start += b.CallLimit)
        {
          Graphics.RenderMeshInstanced(rp, b.Mesh!, b.Part.Submesh, buf.M, Math.Min(b.CallLimit, end - start), start);
          calls++;
        }
      }
    }
    catch (Exception e)
    {
      // a shader that needs inverse matrices holds 511 instances a call: try that before giving the part up
      if (b.CallLimit > MinPerCall && buf.Count > MinPerCall)
      {
        b.CallLimit = MinPerCall;
        return calls + Draw(b, buf);
      }
      if (!b.Dead)
        LogError($"baked placements: cannot draw \"{b.Part.Mesh?.name}\" with material \"{rp.material?.name}\" (shader \"{rp.material?.shader?.name}\"): {e.Message}");
      b.Dead = true;   // never again this session
    }
    return calls;
  }

  // ---- bc_bake stats ----------------------------------------------------------------------------------------------------------------

  /// <summary>
  /// The per-kind report of what is in view now (bc_bake stats), one line at a time: for LOD0 and the last LOD apart, the instances, the
  /// calls, the triangles, the shadow-casting calls and the main thread's milliseconds submitting them.
  /// </summary>
  internal static void Report(Action<string> say, int zonesInRing)
  {
    var f = frame;
    if (f == null)
    {
      say("Baked placements: nothing has been drawn yet" + (Hidden ? " (drawing is hidden: bc_bake show)" : "") + ".");
      return;
    }
    double toMs = 1000.0 / Stopwatch.Frequency;
    var rows = new List<Row>();
    var byKind = new Dictionary<BakedKind, Row>();
    foreach (var b in f.Batches)
    {
      var buf = b.Buf[b.Front];
      if (buf.Count == 0 || b.Dead)
        continue;
      if (!byKind.TryGetValue(b.Kind, out var row))
      {
        row = new Row { Name = b.Kind.Name };
        row.Instances[0] = b.Kind.Lod0Count;
        row.Instances[1] = b.Kind.Lod1Count;
        row.Ms[0] = b.Kind.SubmitTicks[0] * toMs;
        row.Ms[1] = b.Kind.SubmitTicks[1] * toMs;
        byKind[b.Kind] = row;
        rows.Add(row);
      }
      int calls = buf.Calls(b.CallLimit);
      row.Calls[b.Lod] += calls;
      row.Triangles[b.Lod] += (long)buf.Count * b.Part.Triangles;
      if (b.Casts)
        row.Casting[b.Lod] += buf.Calls(b.CallLimit, castingOnly: true);
    }
    rows.Sort((a, b) => (b.Ms[0] + b.Ms[1]).CompareTo(a.Ms[0] + a.Ms[1]));
    var total = new Row { Name = "total" };
    foreach (var r in rows)
      for (int l = 0; l < 2; l++)
      {
        total.Instances[l] += r.Instances[l];
        total.Calls[l] += r.Calls[l];
        total.Triangles[l] += r.Triangles[l];
        total.Casting[l] += r.Casting[l];
        total.Ms[l] += r.Ms[l];
      }
    say($"Baked placements in view: {rows.Count} kinds from {f.ZonesSeen} of {zonesInRing} zones in the ring ({f.CellsSeen} cells in view), " +
        $"shadows {f.Shadows}{(Hidden ? ", drawing hidden" : "")}.");
    say($"{"",-30}{"---- LOD0 (full detail) ----------------",-40}  {"---- last LOD ---------------------------",-40}");
    say($"{"kind",-30}{"inst",8}{"calls",6}{"triangles",11}{"cast",6}{"ms",7}  {"inst",8}{"calls",6}{"triangles",11}{"cast",6}{"ms",7}");
    int shown = 0;
    foreach (var r in rows)
    {
      if (++shown > 30)
      {
        say($"  and {rows.Count - 30} more kinds");
        break;
      }
      say(Line(r));
    }
    say(Line(total));
    say($"Main thread: {submitMsAverage:0.00} ms a frame submitting ({SubmitMsByLod[0]:0.00} ms LOD0, {SubmitMsByLod[1]:0.00} ms last LOD; " +
        $"{callsLastFrame:N0} calls, {instancesLastFrame:N0} instances last frame).");
    say($"Worker: {jobMsAverage:0.00} ms a cull-and-LOD job on average, {jobMsMax:0.00} ms at most ({jobsRun:N0} jobs; one starts when the camera moves " +
        $"{RefreshDistance:0} m or turns {RefreshAngle:0} degrees).");
  }

  private sealed class Row
  {
    internal string Name = "";
    internal readonly int[] Instances = new int[2], Calls = new int[2], Casting = new int[2];
    internal readonly long[] Triangles = new long[2];
    internal readonly double[] Ms = new double[2];
  }

  private static string Line(Row r) =>
    string.Format(CultureInfo.InvariantCulture, "{0,-30}{1,8:N0}{2,6:N0}{3,11:N0}{4,6:N0}{5,7:0.000}  {6,8:N0}{7,6:N0}{8,11:N0}{9,6:N0}{10,7:0.000}",
      r.Name.Length > 29 ? r.Name.Substring(0, 29) : r.Name,
      r.Instances[0], r.Calls[0], r.Triangles[0], r.Casting[0], r.Ms[0],
      r.Instances[1], r.Calls[1], r.Triangles[1], r.Casting[1], r.Ms[1]);
}

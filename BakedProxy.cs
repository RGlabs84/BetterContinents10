// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// THE SHADOW PROXIES, ON THE ENGINE'S SIDE (the arithmetic is BakedProxyMerge.cs, the bookkeeping BakedProxyBook.cs).
//
// A proxy mesh is drawn with Graphics.RenderMesh and ShadowCastingMode.ShadowsOnly, with a material made from a real opaque piece material: the
// same shader the instanced pieces use, so its shadow caster pass applies the same normal bias; its _Cutoff is set to 0 and its _Cull to Off,
// so the proxy casts from both sides whatever the texture's alpha is. The vertices are the pieces' own, positions and normals only.
//
// The switch: the environment variable BC_SHADOW_PROXIES=0 starts the game with no proxy, and BakedProxy.Enabled turns them off and on in a running
// game (a test driver reaches it by reflection); there is no setting for it.

internal static class BakedProxy
{
  /// <summary>Proxies are made and drawn at all. False when BC_SHADOW_PROXIES is 0; a test driver may set it.</summary>
  internal static bool Enabled = Environment.GetEnvironmentVariable("BC_SHADOW_PROXIES") != "0";

  /// <summary>How far beyond the shadow distance a proxy is still drawn and made (metres): a zone's pieces reach out of its box.</summary>
  internal const float ReachMargin = 96f;

  internal static readonly ProxyBook Book = new()
  {
    Settle = BakedMeshShapes.Settle,
    NewStore = _ => new MeshDataStore(),
    Upload = Upload,
    Free = FreeMesh,
    Draw = DrawChunk,
  };

  private static Material? material;
  private static bool blocked;
  private static string blockedWhy = "";
  private static readonly HashSet<string> Reported = [];

  /// <summary>Main thread, once a frame before the drawing job is taken in: follows the ring, makes and takes back proxies.</summary>
  internal static void Tick(Camera cam, BuiltZone[] ring, int ringVersion)
  {
    try
    {
      if (ring.Length > 0 && material == null && !blocked)
        MakeMaterial();
      // the reads that finished since the last frame are taken in first, so that the book settles their parts this frame
      BakedMeshShapes.Pump(Time.realtimeSinceStartup);
      Book.Tick(Frame(cam, ring, ringVersion));
    }
    catch (Exception e)
    {
      LogOnce("tick", e);
    }
  }

  /// <summary>Main thread, once a frame after the pieces are submitted: draws the proxies the shadows can reach.</summary>
  internal static void Submit(Camera cam, BuiltZone[] ring, int ringVersion)
  {
    try
    {
      Book.Submit(Frame(cam, ring, ringVersion));
    }
    catch (Exception e)
    {
      LogOnce("submit", e);
    }
  }

  /// <summary>A world ends or the layer goes: every proxy and what was read for them goes now.</summary>
  internal static void Reset()
  {
    try
    {
      Book.Reset();
    }
    catch (Exception e)
    {
      LogOnce("reset", e);
    }
    BakedMeshShapes.Clear();
    if (material != null)
      UnityEngine.Object.Destroy(material);
    material = null;
    blocked = false;
    blockedWhy = "";
  }

  // the reach of the last frame, for the stats (which run offline in the tests, where Unity's quality settings cannot be asked)
  private static float lastReach;

  private static ProxyFrame Frame(Camera cam, BuiltZone[] ring, int ringVersion)
  {
    var p = cam.transform.position;
    lastReach = ShadowReach();
    return new ProxyFrame
    {
      Camera = p,
      ZoneX = Mathf.RoundToInt(p.x / 64f),
      ZoneZ = Mathf.RoundToInt(p.z / 64f),
      Ring = ring,
      RingVersion = ringVersion,
      Policy = BakedDraw.ShadowSetting,
      Enabled = Enabled && !blocked,
      Hidden = BakedDraw.Hidden,
      Reach = lastReach,
      Now = Time.realtimeSinceStartup,
    };
  }

  /// <summary>How far from the camera a shadow can fall now: the shadow distance and a margin; 0 where the game has shadows off.</summary>
  internal static float ShadowReach() =>
    QualitySettings.shadows == ShadowQuality.Disable || QualitySettings.shadowDistance <= 0f ? 0f : QualitySettings.shadowDistance + ReachMargin;

  // ---- the material -----------------------------------------------------------------------------------------------------------------

  // A copy of a real opaque piece material whose shader has a shadow caster pass (the game's Custom/Piece when a piece uses it): a shader the
  // build is certain to carry, because a piece in the world is drawn with it.
  private static void MakeMaterial()
  {
    Material? donor = null;
    bool anyKind = false;
    foreach (var kind in BakedKinds.All)
    {
      var piece = kind.Piece;
      if (piece == null)
        continue;
      anyKind = true;
      foreach (var part in piece.Parts)
      {
        var m = part.Material;
        if (m == null || BakedMeshShapes.Classify(m, out _) != RenderPart.ProxyOk || !HasShadowCaster(m))
          continue;
        if (m.shader.name == "Custom/Piece")
        {
          donor = m;
          break;
        }
        donor ??= m;
      }
      if (donor != null && donor.shader.name == "Custom/Piece")
        break;
    }
    if (donor == null)
    {
      // nothing is known yet: try again when a kind has been resolved; a game that has kinds and no such material gets no proxies
      if (anyKind)
        Block("no opaque piece material with a shadow caster pass");
      return;
    }
    var copy = new Material(donor) { name = "BC shadow proxy", enableInstancing = false, hideFlags = HideFlags.HideAndDontSave };
    if (copy.HasProperty("_Cutoff"))
      copy.SetFloat("_Cutoff", 0f);
    if (copy.HasProperty("_Cull"))
      copy.SetFloat("_Cull", 0f);
    material = copy;
    if (BakedKinds.Diagnostics())
      Log($"baked placements: shadow proxy material: a copy of \"{donor.name}\" (shader \"{donor.shader.name}\", queue {donor.renderQueue})");
  }

  private static bool HasShadowCaster(Material m)
  {
    try
    {
      for (int i = 0; i < m.passCount; i++)
        if (string.Equals(m.GetPassName(i), "ShadowCaster", StringComparison.OrdinalIgnoreCase))
          return true;
      return m.FindPass("ShadowCaster") >= 0;
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static void Block(string why)
  {
    blocked = true;
    blockedWhy = why;
    LogWarning($"baked placements: shadow proxies are off: {why}; the pieces cast their own shadows");
  }

  // ---- the meshes ---------------------------------------------------------------------------------------------------------------------

  // The chunk's store (a MeshData a worker has written into) becomes a mesh: the mesh takes its memory without a copy, the bounds are set by hand, and the mesh is
  // uploaded and loses its CPU copy. The book times this whole call; apart from the three lines of Params it is what runs on the main thread. False if it cannot be made.
  private static bool Upload(ProxyChunk c, ZoneProxy zp)
  {
    if (material == null || c.Store is not MeshDataStore store)
      return false;
    var mesh = new Mesh { name = "BC shadow proxy" };
    try
    {
      store.ApplyTo(mesh);
      mesh.bounds = store.Bounds;
      mesh.UploadMeshData(true);
    }
    catch (Exception)
    {
      UnityEngine.Object.Destroy(mesh);
      throw;
    }
    c.Store = null;
    c.Mesh = mesh;
    var min = new Vector3(c.MinX, c.MinY, c.MinZ);
    var max = new Vector3(c.MaxX, c.MaxY, c.MaxZ);
    c.Params = new RenderParams(material)
    {
      layer = c.Layer,
      shadowCastingMode = ShadowCastingMode.ShadowsOnly,
      receiveShadows = false,
      lightProbeUsage = LightProbeUsage.Off,
      reflectionProbeUsage = ReflectionProbeUsage.Off,
      motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
      worldBounds = new Bounds((min + max) * 0.5f + new Vector3(zp.Zone.Zx * 64f, 0f, zp.Zone.Zz * 64f), max - min),
    };
    return true;
  }

  private static void FreeMesh(ProxyChunk c)
  {
    if (c.Mesh != null)
      UnityEngine.Object.Destroy(c.Mesh);
    c.Mesh = null;
  }

  private static void DrawChunk(ProxyChunk c, Matrix4x4 toWorld)
  {
    if (c.Mesh == null)
      return;
    try
    {
      Graphics.RenderMesh(c.Params, c.Mesh, 0, toWorld);
    }
    catch (Exception e)
    {
      // a proxy that cannot be drawn is not tried again: the pieces cast their own shadows
      if (!blocked)
        Block($"a proxy cannot be drawn ({e.Message})");
    }
  }

  // ---- bc_bake stats ----------------------------------------------------------------------------------------------------------------

  internal static void Report(Action<string> say, int zonesInRing)
  {
    if (!Enabled)
    {
      say("Shadow proxies: switched off (BC_SHADOW_PROXIES=0, or BakedProxy.Enabled): every piece casts its own shadows.");
      return;
    }
    if (blocked)
    {
      say($"Shadow proxies: off, {blockedWhy}; every piece casts its own shadows.");
      return;
    }
    float reach = lastReach;
    if (reach <= 0f)
    {
      say("Shadow proxies: none yet, or the game has shadows off (shadow distance 0).");
      return;
    }
    if (BakedDraw.ShadowSetting == BakedShadows.Off)
    {
      say("Shadow proxies: none, the Shadows setting is Off.");
      return;
    }
    var n = Book.Numbers(zonesInRing);
    say(n.Line());
    say(n.TimeLine());
    say($"Shadow proxies reach {reach:0} m (the shadow distance and {ReachMargin:0}); material " +
        (material != null ? $"\"{material.name}\" (shader \"{material.shader.name}\")" : "not made yet") + ".");
    foreach (var line in BakedMeshShapes.ExclusionLines())
      say(line);
  }

  private static void LogOnce(string what, Exception e)
  {
    string key = what + ": " + e.Message;
    if (Reported.Add(key))
      LogError($"baked placements: shadow proxies, {what}: {e}");
  }
}

// WHAT A PART'S SHADOW IS MADE OF: whether its material's shadow is its shape, and the shape itself, read once per mesh and kept. The shape is read OFF the main
// thread. A mesh the CPU can read: the main thread acquires a read-only MeshData (Mesh.AcquireReadOnlyMeshData, not thread safe: it stays on the main thread, as
// does the dispose), a worker copies the positions, normals and triangles out of it (MeshData's getters are IsThreadSafe) and makes the shape, and the main thread
// disposes the MeshData when the worker is out. A mesh it cannot read is read back from the GPU (Mesh.GetVertexBuffer and GetIndexBuffer, then
// AsyncGPUReadback, which takes a few frames): the callback only keeps the bytes, a worker decodes them. A part is "Reading" until its worker has finished; where
// a read fails the part keeps casting by instancing.
internal static class BakedMeshShapes
{
  /// <summary>Meshes read back from the GPU at once.</summary>
  internal const int MaxReads = 4;
  /// <summary>Meshes read by workers from their CPU copy at once.</summary>
  internal const int MaxCpuReads = 8;
  /// <summary>Seconds a read back may take before it is given up.</summary>
  internal const float ReadTimeout = 10f;
  /// <summary>How long Clear waits for workers still in a MeshData (milliseconds) before leaving them to Pump.</summary>
  internal const int ClearWaitMs = 2000;

  /// <summary>Runs a job on a worker.</summary>
  internal static Action<Action> Work = job => ThreadPool.QueueUserWorkItem(_ => job());

  private static readonly Dictionary<long, ProxyShape?> Shapes = new();
  private static readonly Dictionary<int, (byte State, string Why)> Materials = new();
  private static readonly Dictionary<long, Read> Reads = new();
  // reads dropped (a world ended) whose workers had not finished: their MeshData is disposed when they have
  private static readonly List<Read> Orphans = [];
  private static readonly List<long> Done = [];
  private static readonly HashSet<string> Reported = [];
  private static int cpuRead, gpuRead, unreadable, cpuActive;
  // worker time spent reading and decoding (Stopwatch ticks)
  private static long workerTicks;

  private sealed class Read(Mesh mesh, int sub, float started, bool gpu)
  {
    internal readonly Mesh Mesh = mesh;
    internal readonly int Sub = sub;
    internal readonly float Started = started;
    internal readonly bool Gpu = gpu;
    internal GraphicsBuffer? Vertices, Indices;
    internal byte[]? VertexBytes, IndexBytes;
    internal bool Failed;
    internal int VertexCount, Stride, PosOffset, PosDimension, NormalOffset, NormalDimension, IndexStart, IndexCount, BaseVertex;
    internal VertexAttributeFormat PosFormat, NormalFormat;
    internal bool HasNormals, Wide;
    internal string Name = "";
    // a CPU read: the read-only MeshData the worker copies from (made and disposed by the main thread)
    internal Mesh.MeshDataArray Data;
    internal bool HasData;
    // a worker has been given the job (a GPU read: the decode, once the bytes are in)
    internal bool Sent;
    /// <summary>Set last by the worker: no worker is in this read any more, and Shape and Error are final.</summary>
    internal volatile bool Complete;
    internal ProxyShape? Shape;
    internal string? Error;

    internal bool Finished => Failed || (VertexBytes != null && IndexBytes != null);
  }

  /// <summary>Forgets every shape and every read in flight (a world ends).</summary>
  internal static void Clear()
  {
    foreach (var r in Reads.Values)
      Retire(r);
    Reads.Clear();
    cpuActive = 0;
    Shapes.Clear();
    Materials.Clear();
    Reported.Clear();
    cpuRead = gpuRead = unreadable = 0;
    workerTicks = 0;
    // a worker may be in a MeshData: it is given a moment (a mesh takes microseconds) and what is left is let go by Pump
    var wait = Stopwatch.StartNew();
    DrainOrphans();
    while (Orphans.Count > 0 && wait.ElapsedMilliseconds < ClearWaitMs)
    {
      Thread.Sleep(1);
      DrainOrphans();
    }
  }

  // a read nobody will use: the GPU buffers go now (the decode works on a byte array), a MeshData goes when its worker is out
  private static void Retire(Read r)
  {
    Release(r);
    if (r.Gpu || !r.HasData)
      return;
    if (r.Complete)
      DisposeData(r);
    else
      Orphans.Add(r);
  }

  private static void DrainOrphans()
  {
    for (int i = Orphans.Count - 1; i >= 0; i--)
      if (Orphans[i].Complete)
      {
        DisposeData(Orphans[i]);
        Orphans.RemoveAt(i);
      }
  }

  private static void DisposeData(Read r)
  {
    if (!r.HasData)
      return;
    r.HasData = false;
    var data = r.Data;
    r.Data = default;
    data.Dispose();
  }

  // ---- settling a part -------------------------------------------------------------------------------------------------------------

  /// <summary>
  /// Settles a part's RenderPart.ProxyState (and Shape): its materials are checked first (a cutout needs no mesh), then its mesh read. True once it is
  /// settled; false while a read back from the GPU is under way (ask again next frame).
  /// </summary>
  internal static bool Settle(RenderPart part)
  {
    byte state = part.ProxyState;
    if (state != RenderPart.ProxyUnknown && state != RenderPart.ProxyReading)
      return true;
    var mesh = part.Mesh;
    if (state == RenderPart.ProxyUnknown)
    {
      byte c = ClassifyPart(part, out string why);
      if (c == RenderPart.ProxyOk && (mesh == null || part.Submesh < 0))
      {
        c = RenderPart.ProxyOther;
        why = "no mesh";
      }
      if (c != RenderPart.ProxyOk)
      {
        part.ProxyState = c;
        Note(part, why);
        return true;
      }
    }
    if (mesh == null)
    {
      part.ProxyState = RenderPart.ProxyOther;
      return true;
    }
    long key = Key(mesh, part.Submesh);
    if (Shapes.TryGetValue(key, out var shape))
    {
      Finish(part, shape);
      return true;
    }
    if (Reads.ContainsKey(key))
    {
      part.ProxyState = RenderPart.ProxyReading;
      return false;
    }
    if (mesh.isReadable)
    {
      if (cpuActive >= MaxCpuReads)
      {
        part.ProxyState = RenderPart.ProxyReading;
        return false;
      }
      if (!StartCpuRead(mesh, part.Submesh, key))
      {
        Shapes[key] = null;
        unreadable++;
        Finish(part, null);
        return true;
      }
      part.ProxyState = RenderPart.ProxyReading;
      return false;
    }
    if (!SystemInfo.supportsAsyncGPUReadback)
    {
      Shapes[key] = null;
      unreadable++;
      Finish(part, null);
      return true;
    }
    if (Reads.Count - cpuActive >= MaxReads)
    {
      part.ProxyState = RenderPart.ProxyReading;
      return false;
    }
    if (!StartRead(mesh, part.Submesh, key))
    {
      Shapes[key] = null;
      unreadable++;
      Finish(part, null);
      return true;
    }
    part.ProxyState = RenderPart.ProxyReading;
    return false;
  }

  private static void Finish(RenderPart part, ProxyShape? shape)
  {
    part.Shape = shape;
    part.ProxyState = shape != null ? RenderPart.ProxyOk : RenderPart.ProxyUnreadable;
    if (shape == null)
      Note(part, "its mesh can be read neither by the CPU nor back from the GPU");
  }

  private static long Key(Mesh mesh, int sub) => ((long)mesh.GetInstanceID() << 8) ^ (uint)sub;

  // the diagnostic line of a part left to the instancing
  private static void Note(RenderPart part, string why)
  {
    if (!BakedKinds.Diagnostics())
      return;
    string name = part.Mesh != null ? part.Mesh.name : "?";
    if (Reported.Add(name + "|" + why))
      Log($"baked placements: shadow proxy: part \"{name}\" stays instanced: {why}");
  }

  // ---- materials -----------------------------------------------------------------------------------------------------------------------

  private static byte ClassifyPart(RenderPart part, out string why)
  {
    byte result = RenderPart.ProxyOk;
    why = "";
    Consider(part.Material, ref result, ref why);
    if (part.VariantMaterials != null)
      foreach (var m in part.VariantMaterials)
        Consider(m, ref result, ref why);
    return result;
  }

  private static void Consider(Material? m, ref byte result, ref string why)
  {
    if (m == null)
      return;
    byte c = Classify(m, out string w);
    if (c != RenderPart.ProxyOk && result == RenderPart.ProxyOk)
    {
      result = c;
      why = $"material \"{m.name}\": {w}";
    }
  }

  /// <summary>Whether a material's shadow is its shape alone (ProxyRules.Classify, on what the material says).</summary>
  internal static byte Classify(Material m, out string why)
  {
    int id = m.GetInstanceID();
    if (Materials.TryGetValue(id, out var known))
    {
      why = known.Why;
      return known.State;
    }
    byte state;
    try
    {
      bool cutoff = m.HasProperty("_Cutoff");
      var facts = new ProxyRules.MaterialFacts(
        m.shader != null ? m.shader.name : "", m.renderQueue, m.GetTag("RenderType", true, "Opaque"),
        m.IsKeywordEnabled("_ALPHATEST_ON"), m.IsKeywordEnabled("_ALPHABLEND_ON"), m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"),
        cutoff, cutoff ? m.GetFloat("_Cutoff") : 0f, m.HasProperty("_Mode") ? (int)m.GetFloat("_Mode") : -1);
      state = ProxyRules.Classify(facts, out why);
    }
    catch (Exception e)
    {
      state = RenderPart.ProxyOther;
      why = "its material could not be read: " + e.Message;
    }
    Materials[id] = (state, why);
    return state;
  }

  // ---- reading a mesh -----------------------------------------------------------------------------------------------------------------

  // Main thread: the mesh's CPU data is held read-only for a worker (cheap: no copy), and the worker is sent.
  private static bool StartCpuRead(Mesh mesh, int sub, long key)
  {
    var read = new Read(mesh, sub, Time.realtimeSinceStartup, false) { Name = mesh.name };
    try
    {
      if (sub >= mesh.subMeshCount || mesh.GetSubMesh(sub).topology != MeshTopology.Triangles)
        return false;
      read.Data = Mesh.AcquireReadOnlyMeshData(mesh);
      read.HasData = true;
    }
    catch (Exception e)
    {
      Note2($"mesh \"{mesh.name}\" could not be read: {e.Message}");
      DisposeData(read);
      return false;
    }
    Reads[key] = read;
    cpuActive++;
    read.Sent = true;
    Work(() => CpuReadJob(read));
    return true;
  }

  // Worker: copies the positions, normals and triangles of the submesh out of the MeshData into plain arrays (the getters convert any vertex format to floats
  // and apply the submesh's base vertex) and makes the shape.
  private static unsafe void CpuReadJob(Read r)
  {
    long t0 = Stopwatch.GetTimestamp();
    try
    {
      var data = r.Data[0];
      if (r.Sub >= data.subMeshCount)
        return;
      var d = data.GetSubMesh(r.Sub);
      int vertexCount = data.vertexCount;
      if (d.topology != MeshTopology.Triangles || vertexCount <= 0 || d.indexCount < 3)
        return;
      var vertices = new Vector3[vertexCount];
      fixed (Vector3* p = vertices)
        data.GetVertices(NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<Vector3>(p, vertexCount, Allocator.None));
      Vector3[]? normals = null;
      if (data.HasVertexAttribute(VertexAttribute.Normal))
      {
        normals = new Vector3[vertexCount];
        fixed (Vector3* p = normals)
          data.GetNormals(NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<Vector3>(p, vertexCount, Allocator.None));
      }
      var triangles = new int[d.indexCount];
      fixed (int* p = triangles)
        data.GetIndices(NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<int>(p, triangles.Length, Allocator.None), r.Sub, true);
      r.Shape = ProxyShape.Make(vertices, normals, triangles);
    }
    catch (Exception e)
    {
      r.Error = e.Message;
    }
    finally
    {
      Interlocked.Add(ref workerTicks, Stopwatch.GetTimestamp() - t0);
      r.Complete = true;
    }
  }

  // Worker: the bytes read back from the GPU become a shape.
  private static void DecodeJob(Read r)
  {
    long t0 = Stopwatch.GetTimestamp();
    try
    {
      var positions = ProxyDecode.ReadVectors(r.VertexBytes!, r.VertexCount, r.Stride, r.PosOffset, r.PosFormat, r.PosDimension, false);
      var normals = r.HasNormals ? ProxyDecode.ReadVectors(r.VertexBytes!, r.VertexCount, r.Stride, r.NormalOffset, r.NormalFormat, r.NormalDimension, true) : null;
      var indices = ProxyDecode.ReadIndices(r.IndexBytes!, r.Wide, r.IndexStart, r.IndexCount, r.BaseVertex, r.VertexCount);
      if (positions != null && indices != null)
        r.Shape = ProxyShape.Make(positions, normals, indices);
    }
    catch (Exception e)
    {
      r.Error = e.Message;
    }
    finally
    {
      Interlocked.Add(ref workerTicks, Stopwatch.GetTimestamp() - t0);
      r.Complete = true;
    }
  }

  private static void Note2(string text)
  {
    if (Reported.Add(text))
      LogWarning("baked placements: shadow proxy: " + text);
  }

  // The request: the mesh's vertex buffer (the stream with the positions) and its index buffer are copied back. Where the normals are in another
  // stream, or in a format that is not read, they are made up from the triangles.
  private static bool StartRead(Mesh mesh, int sub, long key)
  {
    var read = new Read(mesh, sub, Time.realtimeSinceStartup, true) { Name = mesh.name };
    try
    {
      if (sub >= mesh.subMeshCount || !mesh.HasVertexAttribute(VertexAttribute.Position))
        return false;
      var d = mesh.GetSubMesh(sub);
      if (d.topology != MeshTopology.Triangles)
        return false;
      int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
      read.VertexCount = mesh.vertexCount;
      read.Stride = mesh.GetVertexBufferStride(stream);
      read.PosOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
      read.PosFormat = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
      read.PosDimension = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
      if (ProxyDecode.ComponentSize(read.PosFormat) == 0 || read.PosFormat == VertexAttributeFormat.SNorm8 || read.PosFormat == VertexAttributeFormat.SNorm16)
        return false;
      if (mesh.HasVertexAttribute(VertexAttribute.Normal) && mesh.GetVertexAttributeStream(VertexAttribute.Normal) == stream)
      {
        read.NormalOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
        read.NormalFormat = mesh.GetVertexAttributeFormat(VertexAttribute.Normal);
        read.NormalDimension = mesh.GetVertexAttributeDimension(VertexAttribute.Normal);
        read.HasNormals = ProxyDecode.ComponentSize(read.NormalFormat) != 0;
      }
      read.IndexStart = d.indexStart;
      read.IndexCount = d.indexCount;
      read.BaseVertex = d.baseVertex;
      read.Wide = mesh.indexFormat == IndexFormat.UInt32;
      read.Vertices = mesh.GetVertexBuffer(stream);
      read.Indices = mesh.GetIndexBuffer();
      AsyncGPUReadback.Request(read.Vertices, request =>
      {
        if (request.hasError) read.Failed = true;
        else read.VertexBytes = request.GetData<byte>().ToArray();
      });
      AsyncGPUReadback.Request(read.Indices, request =>
      {
        if (request.hasError) read.Failed = true;
        else read.IndexBytes = request.GetData<byte>().ToArray();
      });
    }
    catch (Exception e)
    {
      Note2($"mesh \"{mesh.name}\" cannot be read back from the GPU: {e.Message}");
      Release(read);
      return false;
    }
    Reads[key] = read;
    return true;
  }

  /// <summary>
  /// Main thread, once a frame: sends the decode of a GPU read whose bytes are in, takes in the reads whose worker is out (disposing the MeshData of a CPU read),
  /// and gives up on a GPU read that is too slow.
  /// </summary>
  internal static void Pump(float now)
  {
    DrainOrphans();
    if (Reads.Count == 0)
      return;
    Done.Clear();
    foreach (var kv in Reads)
    {
      var r = kv.Value;
      if (r.Gpu && !r.Sent)
      {
        if (r.Finished && !r.Failed)
        {
          r.Sent = true;
          Work(() => DecodeJob(r));
        }
        else if (r.Failed || now - r.Started > ReadTimeout)
        {
          if (!r.Failed)
            Note2($"mesh \"{r.Name}\": the read back from the GPU took more than {ReadTimeout:0} s");
          r.Complete = true;
          Done.Add(kv.Key);
        }
      }
      else if (r.Complete)
        Done.Add(kv.Key);
    }
    foreach (long key in Done)
    {
      var r = Reads[key];
      Reads.Remove(key);
      if (!r.Gpu)
        cpuActive--;
      if (r.Error != null)
        Note2(r.Gpu ? $"mesh \"{r.Name}\" was read back from the GPU but could not be decoded: {r.Error}" : $"mesh \"{r.Name}\" could not be read: {r.Error}");
      if (r.Shape != null)
      {
        if (r.Gpu) gpuRead++; else cpuRead++;
      }
      else
        unreadable++;
      Release(r);
      DisposeData(r);
      Shapes[key] = r.Shape;
    }
    Done.Clear();
  }

  private static void Release(Read r)
  {
    try
    {
      r.Vertices?.Dispose();
      r.Indices?.Dispose();
    }
    catch (Exception)
    {
      // the buffer is the mesh's; letting go of our handle is all that is asked
    }
    r.Vertices = r.Indices = null;
  }

  // ---- bc_bake stats ----------------------------------------------------------------------------------------------------------------

  /// <summary>The parts the proxies leave to the instancing, and why, over the kinds drawn so far.</summary>
  internal static IEnumerable<string> ExclusionLines()
  {
    var seen = new HashSet<PieceKind>();
    var counts = new int[6];
    var names = new List<string>[6];
    for (int i = 0; i < names.Length; i++)
      names[i] = [];
    foreach (var kind in BakedKinds.All)
    {
      var piece = kind.Piece;
      if (piece == null || !seen.Add(piece))
        continue;
      foreach (var part in piece.Parts)
      {
        int s = part.ProxyState;
        counts[s]++;
        if (s != RenderPart.ProxyOk && !names[s].Contains(piece.Name))
          names[s].Add(piece.Name);
      }
    }
    string Names(int s) => names[s].Count == 0 ? "" : " (" + string.Join(", ", names[s].Take(6)) + (names[s].Count > 6 ? ", ..." : "") + ")";
    var c = CultureInfo.InvariantCulture;
    yield return string.Format(c,
      "Shadow proxy parts of {0:N0} pieces: {1:N0} merged, {2:N0} left to the instancing as unreadable{3}, {4:N0} as cutout or blended{5}, {6:N0} of other shaders{7}; {8:N0} not looked at yet, {9:N0} being read back.",
      seen.Count, counts[RenderPart.ProxyOk], counts[RenderPart.ProxyUnreadable], Names(RenderPart.ProxyUnreadable), counts[RenderPart.ProxyCutout],
      Names(RenderPart.ProxyCutout), counts[RenderPart.ProxyOther], Names(RenderPart.ProxyOther), counts[RenderPart.ProxyUnknown], counts[RenderPart.ProxyReading]);
    yield return string.Format(c, "Shadow proxy shapes: {0:N0} read by the CPU, {1:N0} read back from the GPU, {2:N0} that neither could read, {3:N0} reads under way; the workers spent {4:0.0} ms reading and decoding them.",
      cpuRead, gpuRead, unreadable, Reads.Count, Interlocked.Read(ref workerTicks) * 1000.0 / Stopwatch.Frequency);
  }
}

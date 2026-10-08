// Added by Wubarrk on 2026-10-08 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
      Book.Tick(Frame(cam, ring, ringVersion));
      BakedMeshShapes.Pump(Time.realtimeSinceStartup);
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

  private static ProxyFrame Frame(Camera cam, BuiltZone[] ring, int ringVersion)
  {
    var p = cam.transform.position;
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
      Reach = ShadowReach(),
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

  // The chunk's numbers become a mesh, which is uploaded and loses its CPU copy; false if it cannot be made.
  private static bool Upload(ProxyChunk c, ZoneProxy zp)
  {
    if (material == null || c.Vertices == null || c.Normals == null || c.Indices == null)
      return false;
    var mesh = new Mesh { name = "BC shadow proxy", indexFormat = IndexFormat.UInt32 };
    try
    {
      mesh.SetVertices(c.Vertices, 0, c.VertexCount);
      mesh.SetNormals(c.Normals, 0, c.VertexCount);
      mesh.SetIndices(c.Indices, 0, c.IndexCount, MeshTopology.Triangles, 0, false);
      var min = new Vector3(c.MinX, c.MinY, c.MinZ);
      var max = new Vector3(c.MaxX, c.MaxY, c.MaxZ);
      mesh.bounds = new Bounds((min + max) * 0.5f, max - min);
      mesh.UploadMeshData(true);
      float ox = zp.Zone.Zx * 64f, oz = zp.Zone.Zz * 64f;
      c.Params = new RenderParams(material)
      {
        layer = c.Layer,
        shadowCastingMode = ShadowCastingMode.ShadowsOnly,
        receiveShadows = false,
        lightProbeUsage = LightProbeUsage.Off,
        reflectionProbeUsage = ReflectionProbeUsage.Off,
        motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
        worldBounds = new Bounds((min + max) * 0.5f + new Vector3(ox, 0f, oz), max - min),
      };
    }
    catch (Exception)
    {
      UnityEngine.Object.Destroy(mesh);
      throw;
    }
    c.Mesh = mesh;
    c.Vertices = c.Normals = null;
    c.Indices = null;
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
    float reach = ShadowReach();
    if (reach <= 0f)
    {
      say("Shadow proxies: none, the game has shadows off (shadow distance 0).");
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

// WHAT A PART'S SHADOW IS MADE OF: whether its material's shadow is its shape, and the shape itself, read once per mesh and kept. A mesh the CPU can read
// is read; one it cannot is read back from the GPU (Mesh.GetVertexBuffer and GetIndexBuffer, then AsyncGPUReadback, which takes a few frames), and where
// that fails the part keeps casting by instancing.
internal static class BakedMeshShapes
{
  /// <summary>Meshes read back from the GPU at once.</summary>
  internal const int MaxReads = 4;
  /// <summary>Seconds a read back may take before it is given up.</summary>
  internal const float ReadTimeout = 10f;

  private static readonly Dictionary<long, ProxyShape?> Shapes = new();
  private static readonly Dictionary<int, (byte State, string Why)> Materials = new();
  private static readonly Dictionary<long, Read> Reads = new();
  private static readonly List<long> Done = [];
  private static readonly HashSet<string> Reported = [];
  private static int cpuRead, gpuRead, gpuFailed;

  private sealed class Read(Mesh mesh, int sub, float started)
  {
    internal readonly Mesh Mesh = mesh;
    internal readonly int Sub = sub;
    internal readonly float Started = started;
    internal GraphicsBuffer? Vertices, Indices;
    internal byte[]? VertexBytes, IndexBytes;
    internal bool Failed;
    internal int VertexCount, Stride, PosOffset, PosDimension, NormalOffset, NormalDimension, IndexStart, IndexCount, BaseVertex;
    internal VertexAttributeFormat PosFormat, NormalFormat;
    internal bool HasNormals, Wide;
    internal string Name = "";

    internal bool Finished => Failed || (VertexBytes != null && IndexBytes != null);
  }

  /// <summary>Forgets every shape and every read in flight (a world ends).</summary>
  internal static void Clear()
  {
    foreach (var r in Reads.Values)
      Release(r);
    Reads.Clear();
    Shapes.Clear();
    Materials.Clear();
    Reported.Clear();
    cpuRead = gpuRead = gpuFailed = 0;
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
      shape = ReadCpu(mesh, part.Submesh);
      Shapes[key] = shape;
      if (shape != null)
        cpuRead++;
      Finish(part, shape);
      return true;
    }
    if (!SystemInfo.supportsAsyncGPUReadback)
    {
      Shapes[key] = null;
      Finish(part, null);
      return true;
    }
    if (Reads.Count >= MaxReads)
    {
      part.ProxyState = RenderPart.ProxyReading;
      return false;
    }
    if (!StartRead(mesh, part.Submesh, key))
    {
      Shapes[key] = null;
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

  private static ProxyShape? ReadCpu(Mesh mesh, int sub)
  {
    try
    {
      if (sub >= mesh.subMeshCount || mesh.GetSubMesh(sub).topology != MeshTopology.Triangles)
        return null;
      var vertices = mesh.vertices;
      var normals = mesh.normals;
      return ProxyShape.Make(vertices, normals.Length == vertices.Length ? normals : null, mesh.GetTriangles(sub, true));
    }
    catch (Exception e)
    {
      Note2($"mesh \"{mesh.name}\" could not be read: {e.Message}");
      return null;
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
    var read = new Read(mesh, sub, Time.realtimeSinceStartup) { Name = mesh.name };
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

  /// <summary>Main thread, once a frame: finishes the reads that are done, failed or too slow.</summary>
  internal static void Pump(float now)
  {
    if (Reads.Count == 0)
      return;
    Done.Clear();
    foreach (var kv in Reads)
    {
      var r = kv.Value;
      if (r.Finished || now - r.Started > ReadTimeout)
        Done.Add(kv.Key);
    }
    foreach (long key in Done)
    {
      var r = Reads[key];
      Reads.Remove(key);
      ProxyShape? shape = null;
      if (!r.Failed && r.VertexBytes != null && r.IndexBytes != null)
      {
        try
        {
          var positions = ProxyDecode.ReadVectors(r.VertexBytes, r.VertexCount, r.Stride, r.PosOffset, r.PosFormat, r.PosDimension, false);
          var normals = r.HasNormals ? ProxyDecode.ReadVectors(r.VertexBytes, r.VertexCount, r.Stride, r.NormalOffset, r.NormalFormat, r.NormalDimension, true) : null;
          var indices = ProxyDecode.ReadIndices(r.IndexBytes, r.Wide, r.IndexStart, r.IndexCount, r.BaseVertex, r.VertexCount);
          if (positions != null && indices != null)
            shape = ProxyShape.Make(positions, normals, indices);
        }
        catch (Exception e)
        {
          Note2($"mesh \"{r.Name}\" was read back from the GPU but could not be decoded: {e.Message}");
        }
      }
      else if (now - r.Started > ReadTimeout)
        Note2($"mesh \"{r.Name}\": the read back from the GPU took more than {ReadTimeout:0} s");
      if (shape != null)
        gpuRead++;
      else
        gpuFailed++;
      Release(r);
      Shapes[key] = shape;
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
    yield return string.Format(c, "Shadow proxy shapes: {0:N0} read by the CPU, {1:N0} read back from the GPU, {2:N0} that neither could read, {3:N0} reads under way.",
      cpuRead, gpuRead, gpuFailed, Reads.Count);
  }
}

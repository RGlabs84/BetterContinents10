// Added by Wubarrk on 2026-10-09 for baked placements (0.10.4).

using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterContinents;

// A CHUNK'S STORE ON THE ENGINE'S SIDE: a writable Mesh.MeshData, so that a worker packs the vertices and indices straight into the memory the mesh will own.
//
// Which Unity calls may run off the main thread (checked in the game's UnityEngine.CoreModule, decompiled under libs-Tools/GraphicsSystem-Decompiled,
// UnityEngine/Mesh.cs): every method of the MeshData struct that this class uses on a worker (SetVertexBufferParams, SetIndexBufferParams, GetVertexData,
// GetIndexData, subMeshCount, SetSubMesh) is declared [NativeMethod(IsThreadSafe = true)], and NativeArray's view of that memory is plain managed code. The
// MeshDataArray's own life is not: Mesh.AllocateWritableMeshData (CreateNewMeshDatas), MeshDataArray.Dispose (ReleaseMeshDatas) and
// Mesh.ApplyAndDisposeWritableMeshData carry no such attribute, so the main thread makes the array, hands it to a worker, takes it back, applies it and
// disposes it. The book (BakedProxyBook.cs) never releases a store a worker is in.

internal sealed class MeshDataStore : ChunkStore
{
  /// <summary>What the apply and the submesh leave alone: the indices are made right by the merge, the bounds are set by hand, nothing else uses the mesh yet.</summary>
  internal const MeshUpdateFlags Flags =
    MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

  private Mesh.MeshDataArray array;
  private bool live;
  /// <summary>The box of the positions as the mesh reads them; set when the chunk is closed.</summary>
  internal Bounds Bounds;

  /// <summary>Main thread.</summary>
  internal MeshDataStore()
  {
    array = Mesh.AllocateWritableMeshData(1);
    live = true;
  }

  /// <summary>Worker.</summary>
  internal override unsafe void Open(VertexLayout layout, int vertexCount, int indexCount, bool wide, out Span<byte> vertices, out Span<byte> indices)
  {
    var data = array[0];
    var attributes = new VertexAttributeDescriptor[layout.Fields.Length];
    for (int i = 0; i < attributes.Length; i++)
    {
      var f = layout.Fields[i];
      attributes[i] = new VertexAttributeDescriptor(f.Attribute, f.Format, f.Dimension, 0);
    }
    data.SetVertexBufferParams(vertexCount, attributes);
    data.SetIndexBufferParams(indexCount, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
    var v = data.GetVertexData<byte>();
    var x = data.GetIndexData<byte>();
    vertices = new Span<byte>(NativeArrayUnsafeUtility.GetUnsafePtr(v), v.Length);
    indices = new Span<byte>(NativeArrayUnsafeUtility.GetUnsafePtr(x), x.Length);
  }

  /// <summary>Worker.</summary>
  internal override void Close(Bounds bounds, int vertexCount, int indexCount)
  {
    var data = array[0];
    data.subMeshCount = 1;
    data.SetSubMesh(0, new SubMeshDescriptor(0, indexCount) { bounds = bounds, firstVertex = 0, vertexCount = vertexCount, baseVertex = 0 }, Flags);
    Bounds = bounds;
  }

  /// <summary>Main thread, the chunk written: the mesh takes the memory (no copy) and the array is gone. Set the mesh's bounds from <see cref="Bounds"/> after.</summary>
  internal void ApplyTo(Mesh mesh)
  {
    var taken = array;
    array = default;
    live = false;
    try
    {
      Mesh.ApplyAndDisposeWritableMeshData(taken, mesh, Flags);
    }
    catch (Exception)
    {
      // refused before it was disposed: dispose it here
      taken.Dispose();
      throw;
    }
  }

  /// <summary>Main thread, no worker in the store: the array goes if it was not applied.</summary>
  internal override void Release()
  {
    if (!live)
      return;
    live = false;
    var taken = array;
    array = default;
    taken.Dispose();
  }
}

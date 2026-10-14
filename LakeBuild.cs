// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BetterContinents;

/// <summary>The objects of one 64 m tile of a lake: a holder with the tile's volume, its surface and its boxes under it (destroying the holder takes them all).</summary>
internal sealed class TileObjects
{
  public GameObject? Holder;
  public MeshRenderer? Surface;
  public Mesh? OwnMesh;
  /// <summary>The volumes this tile made (its own and its boxes'), to take out of the registries when it goes.</summary>
  public readonly List<WaterVolume> Volumes = [];
  /// <summary>The boxes made so far, of the tile's BoxCount.</summary>
  public int Boxes;
  /// <summary>The tile grazed the mask only: nothing was made, and nothing is tried again.</summary>
  public bool Empty;
}

// THE OBJECTS OF A LAKE, ON THE MAIN THREAD [Tarn: LakeBuilder.cs, BuildTile and BuildBox]: a tile is vanilla's zone water at its own height, a box is the water you touch.
// Both are clones of what a zone has (LakeWater.Template), made one tile or box a step so that the streamer can keep to its budget.
//
// A TILE: an Instantiate of the zone prefab's water volume at y = level - the template's surface offset, all its colliders switched off (the boxes are the water you touch);
// an Instantiate of the template's surface renderer object with its stray WaterVolumes destroyed ("never a second ocean") and its colliders off; the surface's mesh is the
// game's own when the tile is FULL (the mask covers all the water it can draw), else a copy with every triangle removed whose three vertices are all outside the mask (a
// triangle is kept if any vertex is in the mask). m_heightmap = null, m_forceDepth = -1, m_useGlobalWind = true; the four depths are put in by LakeWater's postfix.
//
// A BOX: a GameObject made inactive, on the template's layer (the WaterVolume layer), with a trigger BoxCollider added BEFORE the WaterVolume (its Awake reads the collider),
// from the lowest ground in it less 4 m to 10 m above the level (vanilla's reaches 10 m above its surface too), m_waterSurface the tile's renderer (UpdateMaterials needs one),
// registered as a swim box before it is activated (so its Start never paints), activated last.
internal static class LakeBuild
{
  private static Transform? root;

  /// <summary>The object everything hangs under.</summary>
  internal static Transform Root()
  {
    if (root == null)
      root = new GameObject("BCLakes").transform;
    return root;
  }

  /// <summary>Drops the root (and every tile with it); the registries are the caller's.</summary>
  internal static void DestroyRoot()
  {
    if (root != null)
      Object.Destroy(root.gameObject);
    root = null;
  }

  // How far the template's surface mesh reaches from its centre: the mask test of a full tile assumes LakeShape.FullHalf.
  internal static float MeshReach(WaterVolume template)
  {
    var surface = template.m_waterSurface;
    var mf = surface != null ? surface.GetComponent<MeshFilter>() : null;
    if (surface == null || mf == null || mf.sharedMesh == null)
      return float.PositiveInfinity;
    var extents = mf.sharedMesh.bounds.extents;
    var scale = surface.transform.lossyScale;
    return Math.Max(extents.x * Math.Abs(scale.x), extents.z * Math.Abs(scale.z));
  }

  /// <summary>Makes the tile's volume and surface. Returns false (and marks the tile Empty) when the mask only grazes it. Throws when the template cannot be cloned.</summary>
  internal static void MakeTile(LakeShape shape, int index, TileObjects objects, WaterVolume template, float meshReach)
  {
    var tile = shape.Tiles[index];
    float level = shape.Level;
    string name = $"BCLake_{shape.Lake.Name}_{tile.Tx}_{tile.Tz}";
    var surfaceTemplate = template.m_waterSurface;
    if (surfaceTemplate == null)
      throw new InvalidOperationException("the zone water has no surface renderer");
    var holder = new GameObject(name);
    holder.transform.SetParent(Root(), false);
    objects.Holder = holder;

    var go = Object.Instantiate(template.gameObject, holder.transform);
    go.name = name + "_volume";
    go.transform.rotation = template.transform.rotation;
    go.transform.localScale = template.transform.lossyScale;
    go.transform.position = new Vector3(tile.CentreX, level - template.m_surfaceOffset, tile.CentreZ);
    foreach (var c in go.GetComponentsInChildren<Collider>(true))
      c.enabled = false;
    var wv = go.GetComponent<WaterVolume>();

    var st = surfaceTemplate.transform;
    var surf = Object.Instantiate(st.gameObject, holder.transform);
    surf.name = name + "_surface";
    foreach (var stray in surf.GetComponentsInChildren<WaterVolume>(true))
    {
      if (stray.gameObject == surf)
        Object.DestroyImmediate(stray);
      else
        Object.DestroyImmediate(stray.gameObject);
    }
    foreach (var c in surf.GetComponentsInChildren<Collider>(true))
      c.enabled = false;
    surf.transform.rotation = st.rotation;
    surf.transform.localScale = st.lossyScale;
    surf.transform.position = go.transform.position + (st.position - template.transform.position);

    var mf = surf.GetComponent<MeshFilter>();
    var mr = surf.GetComponent<MeshRenderer>();
    if (mr == null)
      throw new InvalidOperationException("the zone water's surface has no MeshRenderer");
    // A full tile draws the game's own mesh. Otherwise a copy that keeps the triangles with a vertex in the mask [Tarn: LakeBuilder.cs:192-219].
    if (mf != null && mf.sharedMesh != null && !(tile.Full && meshReach <= LakeShape.FullHalf))
    {
      var mesh = Object.Instantiate(mf.sharedMesh);
      mesh.name = surf.name;
      var verts = mesh.vertices;
      var keep = new bool[verts.Length];
      var toWorld = surf.transform.localToWorldMatrix;
      for (int v = 0; v < verts.Length; v++)
      {
        var w = toWorld.MultiplyPoint3x4(verts[v]);
        keep[v] = shape.InMask(w.x, w.z);
      }
      int kept = 0;
      for (int sm = 0; sm < mesh.subMeshCount; sm++)
      {
        var tris = mesh.GetTriangles(sm);
        var outTris = new List<int>(tris.Length);
        for (int k = 0; k + 2 < tris.Length; k += 3)
          if (keep[tris[k]] || keep[tris[k + 1]] || keep[tris[k + 2]])
          {
            outTris.Add(tris[k]);
            outTris.Add(tris[k + 1]);
            outTris.Add(tris[k + 2]);
          }
        mesh.SetTriangles(outTris, sm);
        kept += outTris.Count / 3;
      }
      if (kept == 0)
      {
        // only the margin grazed this square
        Object.Destroy(mesh);
        Object.Destroy(holder);
        objects.Holder = null;
        objects.Empty = true;
        return;
      }
      mf.sharedMesh = mesh;
      objects.OwnMesh = mesh;
    }

    wv.m_waterSurface = mr;
    wv.m_heightmap = null!;          // no zone terrain: our corner depths stand in for it (LakeWater.DetectWaterDepthPostfix)
    wv.m_forceDepth = -1f;
    wv.m_useGlobalWind = true;
    objects.Surface = mr;
    objects.Volumes.Add(wv);
    LakeWater.Register(new LakeWater.Volume
    {
      Water = wv,
      Box = false,
      TileMetres = tile.Metres(),
      TileX = tile.CentreX,
      TileZ = tile.CentreZ,
      Centre = new Vector3(tile.CentreX, level, tile.CentreZ),
    });
  }

  /// <summary>Makes the tile's next box.</summary>
  internal static void MakeBox(LakeShape shape, int index, TileObjects objects, WaterVolume template)
  {
    var tile = shape.Tiles[index];
    var box = shape.Boxes[tile.BoxStart + objects.Boxes];
    float level = shape.Level;
    float bottom = box.Bottom, top = level + 10f;
    var go = new GameObject($"{objects.Holder!.name}_box{objects.Boxes}");
    go.SetActive(false);                                    // Awake waits until the box and the surface are set
    go.layer = template.gameObject.layer;
    go.transform.SetParent(objects.Holder.transform, false);
    go.transform.position = new Vector3((box.MinX + box.MaxX) / 2f, level, (box.MinZ + box.MaxZ) / 2f);
    var collider = go.AddComponent<BoxCollider>();
    collider.isTrigger = true;
    collider.size = new Vector3(box.MaxX - box.MinX, top - bottom, box.MaxZ - box.MinZ);
    collider.center = new Vector3(0f, (top + bottom) / 2f - level, 0f);
    var wv = go.AddComponent<WaterVolume>();
    wv.m_waterSurface = objects.Surface!;                  // UpdateMaterials needs one; it and SetupMaterial are skipped for boxes
    wv.m_heightmap = null!;
    wv.m_forceDepth = -1f;
    wv.m_useGlobalWind = true;
    wv.m_surfaceOffset = 0f;
    objects.Volumes.Add(wv);
    LakeWater.Register(new LakeWater.Volume
    {
      Water = wv,
      Box = true,
      TileMetres = tile.Metres(),
      TileX = tile.CentreX,
      TileZ = tile.CentreZ,
      MinX = box.MinX,
      MinZ = box.MinZ,
      MaxX = box.MaxX,
      MaxZ = box.MaxZ,
      Centre = go.transform.position,
    });
    go.SetActive(true);
    objects.Boxes++;
  }

  /// <summary>Takes a tile's objects out of the registries and destroys them.</summary>
  internal static void Destroy(TileObjects objects)
  {
    foreach (var wv in objects.Volumes)
      LakeWater.Unregister(wv);
    objects.Volumes.Clear();
    // The surface's material is an instance made for this tile (WaterVolume.SetupMaterial reads .material): destroying the renderer does not destroy it.
    if (objects.Surface != null)
    {
      var material = objects.Surface.sharedMaterial;
      if (material != null && material.name.EndsWith(" (Instance)", StringComparison.Ordinal))
        Object.Destroy(material);
    }
    if (objects.Holder != null)
      Object.Destroy(objects.Holder);
    if (objects.OwnMesh != null)
      Object.Destroy(objects.OwnMesh);
    objects.Holder = null;
    objects.OwnMesh = null;
    objects.Surface = null;
  }
}

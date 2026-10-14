// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5), ported from Tarn by Raven Iron (Wubarrk and NomadicWar, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace BetterContinents;

/// <summary>Where the engine reads the live lake settings (the world's section 12): bound by SettingsSchema (the data agent's lines: <c>e => LakeSettings.BuildRange = e</c> and
/// the like), null where the config is not (the offline suites). The defaults are the ones of the schema, so a null entry still gives a value.</summary>
internal static class LakeSettings
{
  internal static ConfigEntry<float>? BuildRange, BuildBudget, Sound;
  internal static ConfigEntry<bool>? OnMap;

  /// <summary>Tiles within this of the player are made, and dropped 150 m further out (Lake Build Range, 150 to 2000).</summary>
  internal static float Range => Math.Max(150f, Math.Min(2000f, BuildRange?.Value ?? 400f));
  /// <summary>The main thread's milliseconds a frame for making tile objects (Lake Build Budget, 0.5 to 20).</summary>
  internal static float Budget => Math.Max(0.5f, Math.Min(20f, BuildBudget?.Value ?? 2f));
  /// <summary>The volume of the lake sound (Lake Sound, 0 to 1).</summary>
  internal static float SoundVolume => Math.Max(0f, Math.Min(1f, Sound?.Value ?? 0.35f));
  /// <summary>Whether lakes are painted on the map (Lakes On Map).</summary>
  internal static bool PaintOnMap => OnMap?.Value ?? true;
}

// THE WATER OF A LAKE, AS THE GAME SEES IT [Tarn: Water.cs, Plugin.cs]: the registries of the water volumes the lakes made, the zone water template they are cloned
// from, and the patches on WaterVolume (their toggles are in LakeWater.Toggles.cs).
//
// A lake's water is two kinds of WaterVolume, both clones of what a zone has (LakeBuild):
//  - a TILE: the drawn water, one 64 m square on the zone grid with its own surface; its colliders are off. Its four depths are the ground's under it at its corners
//    (LakeShape), written by the postfix on DetectWaterDepth, which a zone's volume gets from its terrain.
//  - a BOX: the water you touch (swimming, floating, wet, the underwater view find a volume by its trigger box on the WaterVolume layer). It shares its tile's surface only
//    because UpdateMaterials needs one; SetupMaterial and UpdateMaterials are skipped for it, so it never paints its depths onto the tile and costs no native call a frame.
//    Its four depths are the tile's own interpolated at its corners (LakeDepth), so what is felt is what the tile draws.
internal static class LakeWater
{
  /// <summary>What the lakes know of one of their water volumes.</summary>
  internal sealed class Volume
  {
    public WaterVolume Water = null!;
    public bool Box;
    // A tile: the ground's depth in metres at its corners, in vanilla's order; a box: its tile's, and where the tile and the box are.
    public float[] TileMetres = [];
    public float TileX, TileZ;
    public float MinX, MinZ, MaxX, MaxZ;
    public Vector3 Centre;
  }

  /// <summary>Every lake volume that is alive, by the volume (DetectWaterDepth's postfix looks it up).</summary>
  internal static readonly Dictionary<WaterVolume, Volume> Volumes = [];
  /// <summary>The swim boxes among them (UpdateMaterials runs for every volume every frame: this is the cheap test).</summary>
  internal static readonly HashSet<WaterVolume> CollisionOnly = [];

  /// <summary>Whether a water volume is one of the lakes' swim boxes (no surface of its own: AshlandsWater and SeaDraw leave it alone).</summary>
  internal static bool IsCollisionOnly(WaterVolume volume) => CollisionOnly.Count > 0 && CollisionOnly.Contains(volume);

  /// <summary>Whether the lakes are on for these settings: the world is Better Continents', it has lakes, and the Tarn mod is not installed.</summary>
  internal static bool Wanted(BetterContinents.BetterContinentsSettings settings) =>
    settings.EnabledForThisWorld && settings.Lakes is { IsEmpty: false } && !TarnCompat.Installed;

  internal static void Register(Volume volume)
  {
    Volumes[volume.Water] = volume;
    if (volume.Box)
      CollisionOnly.Add(volume.Water);
  }

  /// <summary>The record of a water volume that is a lake's tile (not a box), or null: the probe's.</summary>
  internal static Volume? TileOf(WaterVolume water) => Volumes.TryGetValue(water, out var volume) && !volume.Box ? volume : null;

  /// <summary>The swim box whose rectangle holds a point (the water that is felt there), or null.</summary>
  internal static WaterVolume? BoxAt(float x, float z)
  {
    foreach (var volume in Volumes.Values)
      if (volume.Box && volume.Water != null && x >= volume.MinX && x <= volume.MaxX && z >= volume.MinZ && z <= volume.MaxZ)
        return volume.Water;
    return null;
  }

  internal static void Unregister(WaterVolume water)
  {
    Volumes.Remove(water);
    CollisionOnly.Remove(water);
  }

  internal static void Clear()
  {
    Volumes.Clear();
    CollisionOnly.Clear();
  }

  /// <summary>Visual tiles that are alive: the lake sound's.</summary>
  internal static int TileCount => Volumes.Count(v => !v.Value.Box);

  // ---- the patches -----------------------------------------------------------------------------------------------------------------

  // WaterVolume.DetectWaterDepth postfix: a lake volume has no zone terrain (its heightmap is null, so vanilla would read its forced depth, -1): the depths are the
  // lake's [Tarn: Water.cs:65-76].
  internal static void DetectWaterDepthPostfix(WaterVolume __instance)
  {
    if (Volumes.Count == 0 || !Volumes.TryGetValue(__instance, out var volume))
      return;
    try
    {
      Apply(volume, BetterContinents.Settings.Lakes?.DepthScale ?? LakeSet.DefaultDepthScale);
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"Lakes: a lake volume's depth failed ({e.GetType().Name}: {e.Message})");
    }
  }

  // WaterVolume.SetupMaterial prefix: a swim box must not paint its depths onto the tile surface it borrows [Tarn: Water.cs:79-84]. Every other volume: no opinion.
  internal static bool SetupMaterialPrefix(WaterVolume __instance) => !IsCollisionOnly(__instance);

  // WaterVolume.UpdateMaterials prefix: for a box it would set the clock on the shared tile's material once more, a native call each, every frame.
  internal static bool UpdateMaterialsPrefix(WaterVolume __instance) => !IsCollisionOnly(__instance);

  /// <summary>A volume's four depths for the shader and for Depth(): a tile's from its corners, a box's from its tile's (LakeDepth), each over 10 m times the world's
  /// Lake Depth Scale and clamped to 0..1 [Tarn: Water.cs:50-58].</summary>
  internal static float[] DepthsOf(Volume volume, float depthScale)
  {
    var tile = LakeDepth.Normalize(volume.TileMetres, depthScale);
    return volume.Box ? LakeDepth.BoxCorners(tile, volume.TileX, volume.TileZ, volume.MinX, volume.MinZ, volume.MaxX, volume.MaxZ) : tile;
  }

  /// <summary>Writes a volume's four depths, and m_oneDepth back to "per corner".</summary>
  internal static void Apply(Volume volume, float depthScale)
  {
    var depths = DepthsOf(volume, depthScale);
    var target = volume.Water.m_normalizedDepth;
    for (int i = 0; i < 4; i++)
      target[i] = depths[i];
    volume.Water.m_oneDepth = -1f;
  }

  /// <summary>After the world's Lake Depth Scale changed: every lake volume reads its depths again, and the tiles paint them (the boxes' SetupMaterial is skipped). Returns how many.</summary>
  internal static int Reapply()
  {
    int n = 0;
    foreach (var volume in Volumes.Values.ToList())
    {
      if (volume.Water == null)
        continue;
      volume.Water.DetectWaterDepth();
      volume.Water.SetupMaterial();
      n++;
    }
    return n;
  }

  // ---- the template ----------------------------------------------------------------------------------------------------------------

  private static ZoneSystem? templateOf;
  private static WaterVolume? template;
  private static bool templateLogged;

  /// <summary>The water volume of the zone prefab (what a zone's water is cloned from), or null while the game has no zone prefab [Tarn: Plugin.cs:215-221].</summary>
  internal static WaterVolume? Template()
  {
    var zs = ZoneSystem.instance;
    if (zs == null || zs.m_zonePrefab == null)
      return null;
    if (templateOf != zs || template == null)
    {
      templateOf = zs;
      template = zs.m_zonePrefab.GetComponentInChildren<WaterVolume>(true);
    }
    if (template != null && !templateLogged)
    {
      templateLogged = true;
      BetterContinents.Log("Lakes: the zone water template:\n" + DescribeTemplate());
    }
    return template;
  }

  /// <summary>The zone water template in words (bc_lake info), for when the game's zone water changes [Tarn: Plugin.cs:223-246].</summary>
  internal static string DescribeTemplate()
  {
    var zs = ZoneSystem.instance;
    var wv = zs != null && zs.m_zonePrefab != null ? zs.m_zonePrefab.GetComponentInChildren<WaterVolume>(true) : null;
    if (zs == null || wv == null)
      return "no zone water template found";
    var sb = new StringBuilder();
    var root = zs.m_zonePrefab!.transform;
    sb.Append($"zone prefab '{root.name}' root pos {root.position}\n");
    sb.Append($"water '{wv.name}' pos {wv.transform.position} (parent '{(wv.transform.parent ? wv.transform.parent.name : "-")}') lossy scale {wv.transform.lossyScale} layer {LayerMask.LayerToName(wv.gameObject.layer)}\n");
    sb.Append($"  surfaceOffset {wv.m_surfaceOffset} forceDepth {wv.m_forceDepth} globalWind {wv.m_useGlobalWind}\n");
    foreach (var c in wv.GetComponents<Collider>())
      sb.Append($"  collider {c.GetType().Name}" + (c is BoxCollider b ? $" size {b.size} centre {b.center} trigger {b.isTrigger}" : "") + "\n");
    if (wv.m_waterSurface != null)
    {
      var st = wv.m_waterSurface.transform;
      var mf = st.GetComponent<MeshFilter>();
      sb.Append($"surface '{st.name}' pos {st.position} (parent '{(st.parent ? st.parent.name : "-")}') lossy scale {st.lossyScale} layer {LayerMask.LayerToName(st.gameObject.layer)}" +
                (mf && mf.sharedMesh ? $" mesh centre {mf.sharedMesh.bounds.center} size {mf.sharedMesh.bounds.size}, {mf.sharedMesh.vertexCount} verts, {mf.sharedMesh.subMeshCount} submesh(es)" : "") +
                $"; components: {string.Join(", ", st.GetComponents<Component>().Select(c => c.GetType().Name))}\n");
      foreach (var c in st.GetComponents<Collider>())
        sb.Append($"  surface collider {c.GetType().Name}" + (c is BoxCollider b ? $" size {b.size} centre {b.center} trigger {b.isTrigger}" : "") + "\n");
    }
    return sb.ToString();
  }

  // ---- what the world's rules do to a lake (spec 3.5) ------------------------------------------------------------------------------

  /// <summary>Words about a lake that lies in the world's calm Deep North or hot sea, where the world's rules (the sea field, WorldGenerator) make its water calm, or
  /// make swimmers and boats in it burn; empty when the lake lies in neither. Reads the generator, so it runs wherever the world does.</summary>
  internal static IEnumerable<string> RulesNotes(Lake lake)
  {
    if (WorldGenerator.instance == null)
      yield break;
    float x = lake.X, z = lake.Z;
    bool deep = false, hot = false;
    try
    {
      deep = WorldGenerator.IsDeepnorth(x, z);
      hot = WorldGenerator.GetAshlandsOceanGradient(x, z) > 0f;
    }
    catch (Exception)
    {
      // a world generator that cannot answer yet says nothing
    }
    if (hot)
      yield return $"lake '{lake.Name}' is inside the world's hot sea: swimmers and boats in it take heat damage, and its water is drawn red";
    if (deep)
      yield return $"lake '{lake.Name}' is inside the world's Deep North: its water is calm";
  }
}

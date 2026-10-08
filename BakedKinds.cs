// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4), ported from VALtimaOnline's Towns/TownPieceLibrary.cs and Towns/TownPieceKind.cs (Wubarrk's own code, contributed to Better Continents under the LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// What a baked placement's palette entry is, to the client: the piece the game draws for it (harvested once from ZNetScene's prefab, with
// the first candidate the game has), how it is drawn and how it collides. A piece is the game's own mesh and material drawn by GPU
// instancing, so no Iron Gate asset is shipped: this reads them from the running game.
//
// Two layers, so the worker threads can run without the game:
//   - PieceKind, RenderPart, VariantSlot: one harvested prefab (and tint): its parts in each LOD, its LOD sizes, its look variants, its
//     colliders. Shared by every palette entry that draws it.
//   - BakedKind: one palette entry, resolved: the piece it draws, where that piece's anchor is, its role, its canonical boxes, its batches.
// Everything a worker reads is plain data (numbers, arrays, Matrix4x4 and Vector3 values); the Unity objects (meshes, materials, property
// blocks) are only touched on the main thread. The offline suite builds kinds with no Unity objects at all (tools/placement-tests).

/// <summary>A canonical collision box of a palette entry, in the frame of the entry's first candidate (metres).</summary>
internal readonly struct KindBox(Vector3 centre, Vector3 size)
{
  internal readonly Vector3 Centre = centre, Size = size;
}

/// <summary>
/// The look variants of a record (section 2.5): the variant of a MaterialVariation slot a record is drawn with, and the bucket of its
/// RandomMaterialValues seed. The hash, the derived seed and the unit u come from the format (BakedFormat.LookHash, DerivedSeed and
/// VariantUnit: the point of the record in world millimetres, so every machine draws the same piece without a ZDO); this turns them into
/// a choice among weighted variants. Pure: slice B uses the same functions when a record becomes a real piece (the ZDO then keeps what the
/// drawing showed), and the offline suite checks them across calls.
/// </summary>
internal static class BakedLook
{
  /// <summary>The seed buckets a record is drawn in: bucket = seed mod 8.</summary>
  internal const int Buckets = 8;

  /// <summary>The bucket a seed is drawn in.</summary>
  internal static int Bucket(int seed) => seed & (Buckets - 1);

  /// <summary>
  /// The variant for a unit u (0 up to, not including, 1): the first variant whose running weight passes u times the total weight. A
  /// variant of weight 0 is never chosen, and a slot with no weight at all is variant 0.
  /// </summary>
  internal static int Variant(double u, IReadOnlyList<float> weights)
  {
    int n = weights.Count;
    double total = 0;
    for (int i = 0; i < n; i++)
      total += Math.Max(weights[i], 0f);
    if (n == 0 || total <= 0)
      return 0;
    double target = u * total, run = 0;
    for (int i = 0; i < n; i++)
    {
      run += Math.Max(weights[i], 0f);
      if (run > target)
        return i;
    }
    return n - 1;
  }

  /// <summary>The variant of MaterialVariation slot <paramref name="slot"/> (its ZDO key is "MatVar" + slot) for a record's look hash.</summary>
  internal static int Variant(uint hash, int slot, IReadOnlyList<float> weights) => Variant(BakedFormat.VariantUnit(hash, slot), weights);
}

/// <summary>One MaterialVariation of a piece: the slot it replaces, and its variants' weights.</summary>
internal sealed class VariantSlot
{
  /// <summary>MaterialVariation.m_materialIndex: the ZDO key is "MatVar" + Key, and the tag of a palette entry names it the same way.</summary>
  internal int Key;
  internal float[] Weights = [];
  /// <summary>This slot's place in a combination: a combination index is the sum of variant * Stride over the piece's slots.</summary>
  internal int Stride = 1;
  internal int Count => Weights.Length;
}

/// <summary>One mesh, submesh and material of a piece, in the piece's own frame.</summary>
internal sealed class RenderPart
{
  internal Mesh? Mesh;
  internal int Submesh;
  internal Material? Material;
  internal Matrix4x4 Local;
  internal bool LocalIsIdentity;
  /// <summary>Which of the piece's distinct non-identity part matrices (PieceKind.Locals) this one is; -1 for the identity.</summary>
  internal int LocalIndex = -1;
  internal ShadowCastingMode Shadows;
  internal bool ReceiveShadows;
  /// <summary>Drawn at LOD0 (the piece's first LOD, or a renderer outside any LOD group), and drawn at the last LOD.</summary>
  internal bool InNear, InFar;
  /// <summary>The piece's VariantSlot that replaces this part's material (-1: none), with that slot's stride and variant count.</summary>
  internal int Slot = -1, SlotStride = 1, SlotCount = 1;
  /// <summary>The material of each variant of Slot (a variant the game has no material for keeps Material).</summary>
  internal Material?[]? VariantMaterials;
  /// <summary>8 when RandomMaterialValues sets properties on this part's renderer (one property block per bucket), else 1.</summary>
  internal int Buckets = 1;
  internal MaterialPropertyBlock?[]? BucketProps;
  /// <summary>Triangles one instance draws (for bc_bake stats).</summary>
  internal int Triangles;
}

/// <summary>
/// A Valheim piece as the baked placements draw it: the meshes and materials of its intact state (LOD0 near, the last LOD far), its LOD
/// sizes, its look variants and its solid colliders, all read from the game's own prefab at run time.
/// </summary>
internal sealed class PieceKind
{
  internal string Name = "";
  /// <summary>The name with its tint, for the log.</summary>
  internal string Label = "";
  internal GameObject? Prefab;
  /// <summary>Every part of either LOD; NearParts and FarParts index it.</summary>
  internal RenderPart[] Parts = [];
  internal int[] NearParts = [], FarParts = [];
  /// <summary>Near and far are the same parts: there is no LOD switch.</summary>
  internal bool SingleLod;
  /// <summary>The LOD group's size (0: no LOD group), the relative screen height where LOD0 gives way (0: never) and where the last LOD is
  /// culled (0: never), and the group's reference point in the piece's frame.</summary>
  internal float LodSize, NearScreen, CullScreen;
  internal Vector3 LodCenter;
  /// <summary>The drawn parts' bounding radius around the piece's origin.</summary>
  internal float Radius;
  /// <summary>The distinct non-identity part matrices: a zone keeps one array of instance matrices for each.</summary>
  internal Matrix4x4[] Locals = [];
  internal VariantSlot[] Slots = [];
  /// <summary>How many combinations of slot variants there are (the product of the slots' counts; 1 for none).</summary>
  internal int Combos = 1;
  /// <summary>Some part has seed buckets.</summary>
  internal bool HasBuckets;
  internal bool HasChair;
  // colliders, in the piece's frame
  internal Matrix4x4[] Boxes = [];          // unit cube [-0.5, 0.5] to the piece's frame, one per box collider
  internal Vector3[][] MeshVertices = [];   // concave, readable mesh colliders
  internal int[][] MeshTriangles = [];
  internal Mesh?[] ConvexMeshes = [];       // convex mesh colliders (stairs, glass panes): one real collider each
  internal Matrix4x4[] ConvexLocal = [];

  /// <summary>No look variants at all: one batch per part.</summary>
  internal bool Plain => Slots.Length == 0 && !HasBuckets;
  internal bool HasLodGroup => LodSize > 0f;

  /// <summary>The size the LOD rule and the 1% cull use: the LOD group's, or the piece's own diameter without one.</summary>
  internal float EffectiveSize => LodSize > 0f ? LodSize : Math.Max(Radius * 2f, 0.5f);
  /// <summary>Where the last LOD (or the only one) is culled, as a relative screen height: the LOD group's, or 1% without one.</summary>
  internal float EffectiveCullScreen => LodSize > 0f ? CullScreen : 0.01f;
  /// <summary>Where LOD0 gives way (0: there is no switch).</summary>
  internal float EffectiveNearScreen => LodSize > 0f && !SingleLod ? NearScreen : 0f;
}

/// <summary>
/// One palette entry of the layer, resolved on this machine: what it draws (the first candidate the game has), where that piece's anchor
/// is, its role, collision and flags, its canonical boxes, and the batches its instances are drawn in. Shared by every zone that uses an
/// entry like it: the palette's order is not part of its identity (BakedKinds.Resolve keys by what the entry says).
/// </summary>
internal sealed class BakedKind
{
  internal string Name = "";
  internal BakedRole Role;
  internal BakedCollision Collision;
  internal byte Layer;
  internal PaletteFlags Flags;
  /// <summary>The piece drawn (Static, Seat), or whose colliders are used (collision 3).</summary>
  internal PieceKind? Piece;
  /// <summary>The prefab a local copy is made from (Copy, Seat).</summary>
  internal GameObject? Prefab;
  /// <summary>The drawn candidate's anchor, and the first candidate's (the canonical boxes and a trunk follow the first one, whatever is drawn).</summary>
  internal Vector3 Anchor, Anchor0;
  /// <summary>The canonical boxes, unit cube to the first candidate's frame.</summary>
  internal Matrix4x4[] Boxes = [];
  /// <summary>Per slot of Piece: the variant the entry's MatVar tag fixes, or -1 for one derived from each record's point.</summary>
  internal int[] FixedVariant = [];
  /// <summary>The batches this entry's instances are drawn in, and where the first batch of each part of the piece's NearParts (LOD0) and FarParts
  /// (the last LOD) is: a part has one batch for each variant of its slot and each seed bucket. A piece with no LOD switch has no far batches.</summary>
  internal Batch[] Batches = [];
  internal int[] NearFirst = [], FarFirst = [];
  /// <summary>The Unity layer of the colliders and the drawing (0 in the palette is the game's "piece" layer; resolved by BakedKinds).</summary>
  internal int UnityLayer;

  internal bool NoShadows => (Flags & PaletteFlags.NoShadows) != 0;
  internal bool Lod0Only => (Flags & PaletteFlags.LOD0Only) != 0;
  internal bool NoCopyLight => (Flags & PaletteFlags.NoCopyLight) != 0;
  /// <summary>The entry's prefab is a consumable kind (BakedConsumables, spec 0.2): the server places its records as ordinary objects when a zone
  /// generates, so this client never draws one and gives it no collider, whatever its role.</summary>
  internal bool Consumable;
  /// <summary>Instanced: a Static or Seat entry with a piece that has something to draw.</summary>
  internal bool Draws => Piece != null && Piece.NearParts.Length > 0 && (Role == BakedRole.Static || Role == BakedRole.Seat);

  /// <summary>Its place in BakedKinds.All (-1 until it joins): the cull-and-LOD job counts its instances by it.</summary>
  internal int Index = -1;

  // what the cull-and-LOD job works out once per job for this entry (the worker only)
  internal int JobStamp = -1;
  internal float NearK2, CullK2;
  internal bool JobSingle;
  // what the last job drew, for bc_bake stats (written by the worker, read by the main thread between jobs)
  internal int Lod0Count, Lod1Count;
  // the main thread's stopwatch ticks spent submitting this entry's LOD0 and last-LOD batches, a running average over frames
  internal readonly double[] SubmitTicks = new double[2];
}

/// <summary>A palette entry as the client reads it: BakedKinds.Resolve turns it into a BakedKind. Filled from the layer's palette by the
/// adapter in BakedClient.</summary>
internal sealed class EntryDef
{
  internal string[] Names = [];
  internal Vector3[] Anchors = [];
  internal BakedRole Role;
  internal BakedCollision Collision;
  internal byte Layer;
  internal PaletteFlags Flags;
  internal KindBox[] Boxes = [];
  internal string Tint = "";
  internal Color TintColor = Color.white;
  internal string TintFilter = "";
  /// <summary>The entry's MatVar tags: slot key (the number after "MatVar") to variant.</summary>
  internal Dictionary<int, int>? MatVar;

  /// <summary>An entry of the layer's palette, as the client reads it.</summary>
  internal static EntryDef From(PaletteEntry e)
  {
    var def = new EntryDef
    {
      Names = new string[e.Candidates.Length], Anchors = new Vector3[e.Candidates.Length],
      Role = e.Role, Collision = e.Collision, Layer = e.Layer, Flags = e.Flags,
      Boxes = new KindBox[e.Boxes.Length],
      Tint = e.Tint, TintColor = new Color(e.TintR / 1000f, e.TintG / 1000f, e.TintB / 1000f, 1f), TintFilter = e.TintFilter,
    };
    for (int i = 0; i < e.Candidates.Length; i++)
    {
      def.Names[i] = e.Candidates[i].Name;
      def.Anchors[i] = e.Candidates[i].Anchor;
    }
    for (int i = 0; i < e.Boxes.Length; i++)
      def.Boxes[i] = new KindBox(e.Boxes[i].Centre, e.Boxes[i].Size);
    // the entry's look: "MatVar<i>" is the variant of MaterialVariation slot i
    foreach (var tag in e.Tags)
      if (tag.Type == TagType.Int && tag.Key.StartsWith(BakedFormat.KeyMatVar, StringComparison.Ordinal)
          && int.TryParse(tag.Key.Substring(BakedFormat.KeyMatVar.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
        (def.MatVar ??= new Dictionary<int, int>())[slot] = tag.Number;
    return def;
  }

  private string? signature;

  /// <summary>Everything the entry says that decides how it draws and collides: two entries with the same signature share one BakedKind.</summary>
  internal string Signature
  {
    get
    {
      if (signature != null)
        return signature;
      var sb = new StringBuilder();
      for (int i = 0; i < Names.Length; i++)
      {
        var a = i < Anchors.Length ? Anchors[i] : Vector3.zero;
        sb.Append(Names[i]).Append('@').Append(Num(a.x)).Append(',').Append(Num(a.y)).Append(',').Append(Num(a.z)).Append(';');
      }
      sb.Append('|').Append((int)Role).Append(',').Append((int)Collision).Append(',').Append(Layer).Append(',').Append((int)Flags).Append('|');
      foreach (var b in Boxes)
        sb.Append(Num(b.Centre.x)).Append(',').Append(Num(b.Centre.y)).Append(',').Append(Num(b.Centre.z)).Append(',')
          .Append(Num(b.Size.x)).Append(',').Append(Num(b.Size.y)).Append(',').Append(Num(b.Size.z)).Append(';');
      sb.Append('|').Append(Tint).Append(',').Append(Num(TintColor.r)).Append(',').Append(Num(TintColor.g)).Append(',').Append(Num(TintColor.b))
        .Append(',').Append(TintFilter).Append('|');
      if (MatVar != null)
      {
        var keys = new List<int>(MatVar.Keys);
        keys.Sort();
        foreach (var k in keys)
          sb.Append(k).Append('=').Append(MatVar[k]).Append(';');
      }
      return signature = sb.ToString();
    }
  }

  private static string Num(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>The kinds of this session, resolved once each from ZNetScene's prefabs (main thread). A piece's mod may be missing: a palette
/// entry lists the piece and its base-game stand-ins, and the first one the game has is drawn (V7).</summary>
internal static class BakedKinds
{
  private static readonly Dictionary<string, BakedKind> Entries = new();
  private static readonly Dictionary<string, PieceKind?> Pieces = new();
  private static readonly Dictionary<Material, Material> Instancing = new();
  private static readonly Dictionary<string, Material> Tinted = new();
  private static readonly HashSet<string> Reported = new();
  private static readonly HashSet<Material> Described = new();
  private static readonly List<string> MissingNames = new();
  private static string? tintSetting;
  private static Dictionary<string, Color> tintOverrides = new(StringComparer.OrdinalIgnoreCase);
  private static int pieceLayer = -1;

  /// <summary>Every kind resolved so far, in the order they were resolved: the order their batches are drawn in. Main thread.</summary>
  internal static readonly List<BakedKind> All = [];
  /// <summary>Counts up whenever a kind with batches joins All.</summary>
  internal static int Version;

  /// <summary>The entries (by their candidates) that no candidate of is in this game, for bc_bake info.</summary>
  internal static IReadOnlyList<string> Missing => MissingNames;

  /// <summary>The Tints setting; "" where the config is not bound (the offline suite).</summary>
  internal static Func<string> TintSetting = () => BakedSettings.Tints?.Value ?? "";
  /// <summary>The Diagnostics setting.</summary>
  internal static Func<bool> Diagnostics = () => BakedSettings.Diagnostics?.Value ?? false;

  /// <summary>The game's "piece" layer, which the palette's layer 0 stands for.</summary>
  internal static int PieceLayer => pieceLayer >= 0 ? pieceLayer : pieceLayer = Math.Max(LayerMask.NameToLayer("piece"), 0);

  /// <summary>Forgets what was harvested (the Tints setting changed): entries are resolved again as zones are built.</summary>
  internal static void Forget()
  {
    Entries.Clear();
    Pieces.Clear();
    Tinted.Clear();
  }

  /// <summary>A world ends: every kind and its batches go (the game's prefabs may not be the same ones in the next).</summary>
  internal static void ResetAll()
  {
    BakedConsumables.Forget();
    Forget();
    All.Clear();
    Version++;
  }

  /// <summary>An entry, resolved on this machine (cached by what it says).</summary>
  internal static BakedKind Resolve(EntryDef e)
  {
    ReadTintSetting();
    Color tint = e.Tint.Length > 0 && tintOverrides.TryGetValue(e.Tint, out var over) ? over : e.TintColor;
    string key = e.Signature + "#" + ColorKey(tint);
    // a kind whose prefab the game has destroyed since is resolved again
    if (Entries.TryGetValue(key, out var known) && Alive(known))
      return known;
    var kind = Build(e, tint);
    Entries[key] = kind;
    if (kind.Batches.Length > 0)
    {
      All.Add(kind);
      Version++;
    }
    return kind;
  }

  // a prefab the kind found is still there (a destroyed one reads as null to Unity but is not a null reference)
  private static bool Alive(BakedKind k) =>
    (ReferenceEquals(k.Prefab, null) || k.Prefab != null) && (k.Piece == null || ReferenceEquals(k.Piece.Prefab, null) || k.Piece.Prefab != null);

  /// <summary>The name of the consumable prefab an entry resolves to, or null when it does not: the first candidate the game has decides (the one
  /// that would be drawn), so a missing mod's pickable whose stand-in is a plain bush is a bush. Null too where no world is up to ask.</summary>
  internal static string? ConsumableOf(EntryDef e)
  {
    if (ZNetScene.instance == null)
      return null;
    foreach (string name in e.Names)
    {
      var prefab = ZNetScene.instance.GetPrefab(name);
      if (prefab == null)
        continue;
      return BakedConsumables.Is(prefab) ? name : null;
    }
    return null;
  }

  private static BakedKind Build(EntryDef e, Color tint)
  {
    var k = new BakedKind
    {
      Name = e.Names.Length > 0 ? e.Names[0] : "?",
      Role = e.Role, Collision = e.Collision, Layer = e.Layer, Flags = e.Flags,
      Anchor0 = e.Anchors.Length > 0 ? e.Anchors[0] : Vector3.zero,
      UnityLayer = e.Layer == 0 ? PieceLayer : e.Layer,
    };
    k.Anchor = k.Anchor0;
    k.Boxes = new Matrix4x4[e.Boxes.Length];
    for (int i = 0; i < e.Boxes.Length; i++)
      k.Boxes[i] = BakedMath.BoxMatrix(e.Boxes[i].Centre, e.Boxes[i].Size);
    // a consumable kind is decided by the prefab the entry resolves to (its first candidate the game has), whatever its role: its records are
    // real objects the server placed when the zone generated, so nothing is drawn and nothing is built to stand on
    var consumable = ConsumableOf(e);
    if (consumable != null)
    {
      k.Consumable = true;
      k.Boxes = [];
      BakedConsumables.Note(consumable);
      return k;
    }
    // a live record is the real piece (the server seeds it): nothing to draw, nothing to stand on
    if (e.Role == BakedRole.Live || ZNetScene.instance == null)
      return k;

    // the piece is read for what is drawn (Static, Seat) and for collision 3; a copy (Copy) only needs its prefab
    bool needPiece = e.Role == BakedRole.Static || e.Role == BakedRole.Seat || e.Collision == BakedCollision.Prefab;
    bool found = false;
    for (int i = 0; i < e.Names.Length && !found; i++)
    {
      string name = e.Names[i];
      var prefab = ZNetScene.instance.GetPrefab(name);
      if (prefab == null)
      {
        if (Reported.Add("missing:" + name))
          Log($"baked piece \"{name}\" is not in this game" + (i + 1 < e.Names.Length ? $"; \"{e.Names[i + 1]}\" stands in" : ""));
        continue;
      }
      PieceKind? piece = null;
      if (needPiece)
      {
        piece = HarvestOnce(prefab, name, e.Tint, tint, e.TintFilter);
        if (piece == null)
          continue;
      }
      k.Piece = piece;
      k.Prefab = prefab;
      k.Name = e.Tint.Length > 0 ? $"{name} ({e.Tint})" : name;
      k.Anchor = i < e.Anchors.Length ? e.Anchors[i] : Vector3.zero;
      found = true;
    }
    if (!found && e.Names.Length > 0 && Reported.Add("none:" + string.Join(",", e.Names)))
    {
      LogWarning($"none of {string.Join(", ", e.Names)} is in this game: those baked pieces are not drawn" +
                 (e.Collision == BakedCollision.Boxes || e.Collision == BakedCollision.Trunk ? " (their colliders still stand)" : ""));
      MissingNames.Add(string.Join(" / ", e.Names));
    }
    if (k.Piece != null)
    {
      k.FixedVariant = new int[k.Piece.Slots.Length];
      for (int s = 0; s < k.FixedVariant.Length; s++)
        k.FixedVariant[s] = e.MatVar != null && e.MatVar.TryGetValue(k.Piece.Slots[s].Key, out int v) ? Math.Max(0, Math.Min(v, k.Piece.Slots[s].Count - 1)) : -1;
      if (k.Draws)
        MakeBatches(k);
    }
    return k;
  }

  // ---- batches ------------------------------------------------------------------------------------------------------------------

  // one batch for each part of each LOD, variant of its slot and bucket: the lists the cull-and-LOD job fills and the main thread submits.
  // LOD0's come first, then the last LOD's, so the main thread can time each apart.
  internal static void MakeBatches(BakedKind k)
  {
    var piece = k.Piece!;
    var batches = new List<Batch>();
    k.NearFirst = AddBatches(k, piece, piece.NearParts, 0, batches);
    k.FarFirst = piece.SingleLod || k.Lod0Only ? [] : AddBatches(k, piece, piece.FarParts, 1, batches);
    k.Batches = batches.ToArray();
  }

  private static int[] AddBatches(BakedKind k, PieceKind piece, int[] parts, int lod, List<Batch> batches)
  {
    var first = new int[parts.Length];
    for (int n = 0; n < parts.Length; n++)
    {
      var part = piece.Parts[parts[n]];
      first[n] = batches.Count;
      for (int v = 0; v < part.SlotCount; v++)
        for (int b = 0; b < part.Buckets; b++)
          batches.Add(new Batch(k, part, lod, v, b));
    }
    return first;
  }

  // ---- harvesting ---------------------------------------------------------------------------------------------------------------

  private static PieceKind? HarvestOnce(GameObject prefab, string name, string tint, Color tintColor, string filter)
  {
    string key = tint.Length > 0 ? $"{name}|{tint}|{ColorKey(tintColor)}|{filter}" : name;
    if (Pieces.TryGetValue(key, out var known) && (known == null || known.Prefab != null))
      return known;
    PieceKind? kind = null;
    try
    {
      kind = Harvest(prefab, name, tint, tintColor, filter);
    }
    catch (Exception ex)
    {
      LogWarning($"baked piece \"{name}\" could not be read: {ex.Message}");
    }
    Pieces[key] = kind;
    return kind;
  }

  private static PieceKind? Harvest(GameObject prefab, string name, string tint, Color tintColor, string filter)
  {
    Transform root = prefab.transform;
    Matrix4x4 toRoot0 = Matrix4x4.Scale(root.localScale) * root.worldToLocalMatrix;   // Instantiate keeps the root's own scale
    Matrix4x4 ToRoot(Transform t) => toRoot0 * t.localToWorldMatrix;
    var wnt = prefab.GetComponent<WearNTear>();

    // the intact state's renderers: active and enabled, never the worn or broken states
    var renderers = new List<MeshRenderer>();
    foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
    {
      if (!r.enabled || !ActiveUnder(r.transform, root) || Damaged(r.transform, root, wnt))
        continue;
      var f = r.GetComponent<MeshFilter>();
      if (f == null || f.sharedMesh == null)
        continue;
      renderers.Add(r);
    }

    // which LOD each renderer is in; the group with the most of them sets the distances
    var first = new Dictionary<Renderer, int>();
    var last = new Dictionary<Renderer, bool>();
    LODGroup? main = null;
    int mainHits = -1;
    foreach (var g in prefab.GetComponentsInChildren<LODGroup>(true))
    {
      if (!ActiveUnder(g.transform, root) || Damaged(g.transform, root, wnt))
        continue;
      var lods = g.GetLODs();
      int hits = 0;
      for (int i = 0; i < lods.Length; i++)
        foreach (var r in lods[i].renderers)
        {
          if (r == null || r is not MeshRenderer mr || !renderers.Contains(mr))
            continue;
          hits++;
          first[r] = first.TryGetValue(r, out int f0) ? Math.Min(f0, i) : i;
          if (i == lods.Length - 1)
            last[r] = true;
          else if (!last.ContainsKey(r))
            last[r] = false;
        }
      if (hits > mainHits)
      {
        mainHits = hits;
        main = g;
      }
    }

    // RandomMaterialValues sets properties on every renderer under it (MaterialMan): those parts are drawn in seed buckets
    var randomOf = new Dictionary<Renderer, RandomMaterialValues>();
    foreach (var rmv in prefab.GetComponentsInChildren<RandomMaterialValues>(true))
    {
      if (!ActiveUnder(rmv.transform, root) || Damaged(rmv.transform, root, wnt) || rmv.m_vectorProperties.Count == 0)
        continue;
      foreach (var r in rmv.GetComponentsInChildren<MeshRenderer>(true))
        if (renderers.Contains(r) && !randomOf.ContainsKey(r))
          randomOf[r] = rmv;
    }
    var bucketProps = new Dictionary<RandomMaterialValues, MaterialPropertyBlock[]>();

    var slots = new List<VariantSlot>();
    var slotOf = new Dictionary<MaterialVariation, int>();
    int combos = 1;
    var parts = new List<RenderPart>();
    var locals = new List<Matrix4x4>();
    float radius = 0f;
    bool hasBuckets = false;
    foreach (var r in renderers)
    {
      bool grouped = first.ContainsKey(r);
      bool inNear = !grouped || first[r] == 0;
      bool inFar = !grouped || last[r];
      if (!inNear && !inFar)
        continue;
      var mesh = r.GetComponent<MeshFilter>().sharedMesh;
      Matrix4x4 local = ToRoot(r.transform);
      radius = Mathf.Max(radius, RadiusOf(mesh.bounds, local));
      var mats = r.sharedMaterials;
      var variation = r.GetComponent<MaterialVariation>();
      randomOf.TryGetValue(r, out var random);
      for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
      {
        var m = Prepare(mats[s], tint, tintColor, filter);
        if (m == null)
          continue;
        var part = new RenderPart
        {
          Mesh = mesh, Submesh = s, Material = m, Local = local, LocalIsIdentity = local == Matrix4x4.identity,
          Shadows = r.shadowCastingMode, ReceiveShadows = r.receiveShadows, InNear = inNear, InFar = inFar,
          Triangles = (int)(mesh.GetIndexCount(s) / 3),
        };
        if (!part.LocalIsIdentity)
        {
          int at = locals.IndexOf(local);
          if (at < 0)
          {
            at = locals.Count;
            locals.Add(local);
          }
          part.LocalIndex = at;
        }
        // a MaterialVariation replaces the material of its own slot; its variants are drawn in batches of their own
        if (variation != null && s == variation.m_materialIndex && variation.m_materials.Count > 0)
        {
          if (!slotOf.TryGetValue(variation, out int slot))
          {
            int count = variation.m_materials.Count;
            if (combos * count <= 255)
            {
              var weights = new float[count];
              for (int v = 0; v < count; v++)
                weights[v] = variation.m_materials[v].m_weight;
              slot = slots.Count;
              slots.Add(new VariantSlot { Key = variation.m_materialIndex, Weights = weights, Stride = combos });
              combos *= count;
            }
            else
            {
              slot = -1;
              LogWarning($"baked piece \"{name}\": more look variants than a record can hold; the extra material variation is drawn as its first material");
            }
            slotOf[variation] = slot;
          }
          if (slot >= 0)
          {
            var vs = slots[slot];
            part.Slot = slot;
            part.SlotStride = vs.Stride;
            part.SlotCount = vs.Count;
            part.VariantMaterials = new Material?[vs.Count];
            for (int v = 0; v < vs.Count; v++)
              part.VariantMaterials[v] = variation.m_materials[v].m_material != null
                ? Prepare(variation.m_materials[v].m_material, tint, tintColor, filter) ?? m
                : m;
          }
        }
        if (random != null)
        {
          if (!bucketProps.TryGetValue(random, out var blocks))
            bucketProps[random] = blocks = BucketBlocks(random);
          part.Buckets = BakedLook.Buckets;
          part.BucketProps = blocks;
          hasBuckets = true;
        }
        parts.Add(part);
      }
    }

    var kind = new PieceKind
    {
      Name = name, Label = tint.Length > 0 ? $"{name} ({tint})" : name, Prefab = prefab,
      Parts = parts.ToArray(), Radius = Mathf.Max(radius, 0.5f), Locals = locals.ToArray(),
      Slots = slots.ToArray(), Combos = combos, HasBuckets = hasBuckets,
      HasChair = prefab.GetComponentInChildren<Chair>(true) != null,
    };
    var near = new List<int>();
    var far = new List<int>();
    for (int i = 0; i < parts.Count; i++)
    {
      if (parts[i].InNear)
        near.Add(i);
      if (parts[i].InFar)
        far.Add(i);
    }
    // a piece whose LODs hold no part of one kind draws the other's (VALtima's rule): near = far, or far = near
    if (near.Count == 0)
      near.AddRange(far);
    if (far.Count == 0)
      far.AddRange(near);
    kind.NearParts = near.ToArray();
    kind.FarParts = far.ToArray();
    kind.SingleLod = SameParts(kind.NearParts, kind.FarParts);
    if (main != null)
    {
      var lods = main.GetLODs();
      Matrix4x4 g2r = ToRoot(main.transform);
      kind.LodSize = main.size * MaxScale(g2r);
      kind.NearScreen = lods.Length > 1 ? lods[0].screenRelativeTransitionHeight : 0f;
      kind.CullScreen = lods.Length > 0 ? lods[lods.Length - 1].screenRelativeTransitionHeight : 0f;
      kind.LodCenter = g2r.MultiplyPoint3x4(main.localReferencePoint);
    }
    Colliders(prefab, root, wnt, ToRoot, kind);

    if (kind.NearParts.Length == 0 && kind.Boxes.Length + kind.MeshVertices.Length + kind.ConvexMeshes.Length == 0)
    {
      LogWarning($"baked piece \"{name}\" has nothing to draw or stand on");
      return null;
    }
    Describe(kind, root);
    return kind;
  }

  // one property block for each of the 8 seed buckets: bucket b has the values RandomMaterialValues gives seed b (property i gets seed b + i,
  // as the component does)
  private static MaterialPropertyBlock[] BucketBlocks(RandomMaterialValues random)
  {
    var blocks = new MaterialPropertyBlock[BakedLook.Buckets];
    for (int b = 0; b < blocks.Length; b++)
    {
      var block = new MaterialPropertyBlock();
      for (int i = 0; i < random.m_vectorProperties.Count; i++)
      {
        var property = random.m_vectorProperties[i];
        Vector4 value = property.GetValue(b + i);
        foreach (string propertyName in property.m_propertyNames)
          block.SetVector(Shader.PropertyToID(propertyName), value);
      }
      blocks[b] = block;
    }
    return blocks;
  }

  private static void Colliders(GameObject prefab, Transform root, WearNTear? wnt, Func<Transform, Matrix4x4> toRoot, PieceKind kind)
  {
    int solid = LayerMask.GetMask("Default", "static_solid", "piece", "terrain");
    var boxes = new List<Matrix4x4>();
    var verts = new List<Vector3[]>();
    var tris = new List<int[]>();
    var convex = new List<Mesh?>();
    var convexLocal = new List<Matrix4x4>();
    foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
    {
      if (!c.enabled || c.isTrigger || !ActiveUnder(c.transform, root) || Damaged(c.transform, root, wnt))
        continue;
      if (((1 << c.gameObject.layer) & solid) == 0)
        continue;
      Matrix4x4 m = toRoot(c.transform);
      switch (c)
      {
        case BoxCollider b:
          boxes.Add(m * Matrix4x4.TRS(b.center, Quaternion.identity, b.size));
          break;
        case MeshCollider mc when mc.sharedMesh != null:
          var mesh = mc.sharedMesh;
          if (mc.convex)
          {
            convex.Add(mesh);
            convexLocal.Add(m);
          }
          else if (mesh.isReadable)
          {
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++)
              v[i] = m.MultiplyPoint3x4(v[i]);
            verts.Add(v);
            tris.Add(mesh.triangles);
          }
          else
            boxes.Add(m * Matrix4x4.TRS(mesh.bounds.center, Quaternion.identity, mesh.bounds.size));
          break;
        case CapsuleCollider cc:
          var size = Vector3.one * (cc.radius * 2f);
          size[Mathf.Clamp(cc.direction, 0, 2)] = Mathf.Max(cc.height, cc.radius * 2f);
          boxes.Add(m * Matrix4x4.TRS(cc.center, Quaternion.identity, size));
          break;
        case SphereCollider sc:
          boxes.Add(m * Matrix4x4.TRS(sc.center, Quaternion.identity, Vector3.one * (sc.radius * 2f)));
          break;
      }
    }
    kind.Boxes = boxes.ToArray();
    kind.MeshVertices = verts.ToArray();
    kind.MeshTriangles = tris.ToArray();
    kind.ConvexMeshes = convex.ToArray();
    kind.ConvexLocal = convexLocal.ToArray();
  }

  // active up to the prefab's root (a prefab asset, or a mod's prefab under an inactive holder, is never active in a hierarchy)
  private static bool ActiveUnder(Transform t, Transform root)
  {
    for (var x = t; x != null; x = x.parent)
    {
      if (!x.gameObject.activeSelf)
        return false;
      if (x == root)
        return true;
    }
    return true;
  }

  // under the worn or broken state (when those are not the intact state itself)
  private static bool Damaged(Transform t, Transform root, WearNTear? wnt)
  {
    if (wnt == null)
      return false;
    for (var x = t; x != null && x != root; x = x.parent)
    {
      var go = x.gameObject;
      if (go == wnt.m_new)
        return false;
      if ((go == wnt.m_worn || go == wnt.m_broken) && go != wnt.m_new)
        return true;
    }
    return false;
  }

  private static bool SameParts(int[] a, int[] b)
  {
    if (a.Length != b.Length)
      return false;
    for (int i = 0; i < a.Length; i++)
      if (a[i] != b[i])
        return false;
    return true;
  }

  private static float RadiusOf(Bounds b, Matrix4x4 m)
  {
    float r = 0f;
    for (int i = 0; i < 8; i++)
    {
      var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
      r = Mathf.Max(r, m.MultiplyPoint3x4(c).magnitude);
    }
    return r;
  }

  private static float MaxScale(Matrix4x4 m) =>
    Mathf.Max(((Vector3)m.GetColumn(0)).magnitude, Mathf.Max(((Vector3)m.GetColumn(1)).magnitude, ((Vector3)m.GetColumn(2)).magnitude));

  // ---- materials ----------------------------------------------------------------------------------------------------------------

  // the piece's own material, as it is when it can be instanced; a copy with instancing on when it cannot; a tinted copy (its _Color
  // set) when the palette entry tints it. A tint copy is the piece's own material on its own mesh, never a donor.
  private static Material? Prepare(Material? m, string tint, Color color, string filter)
  {
    if (m == null)
      return null;
    if (tint.Length > 0 && filter.Length > 0 && TintApplies(m.name, filter) && m.HasProperty("_Color"))
    {
      string key = $"{m.GetInstanceID()}|{tint}|{ColorKey(color)}";
      if (!Tinted.TryGetValue(key, out var t) || t == null)
      {
        t = new Material(m) { name = $"{m.name} (baked {tint})", enableInstancing = true };
        t.SetColor("_Color", new Color(color.r, color.g, color.b, m.color.a));
        Tinted[key] = t;
      }
      return t;
    }
    if (m.enableInstancing)
      return m;
    if (!Instancing.TryGetValue(m, out var copy) || copy == null)
    {
      copy = new Material(m) { name = $"{m.name} (baked instanced)", enableInstancing = true };
      Instancing[m] = copy;
    }
    return copy;
  }

  // whether a palette entry's tint filter names this material. The clay mod's material is "bfp_clay" in game while its bundle says
  // "bcp_clay", and a compiler may carry the bundle's name: the filter also matches by its part after the last "_".
  private static bool TintApplies(string material, string filter)
  {
    if (material.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
      return true;
    int k = filter.LastIndexOf('_');
    return k >= 0 && k + 1 < filter.Length && material.IndexOf(filter.Substring(k + 1), StringComparison.OrdinalIgnoreCase) >= 0;
  }

  private static string ColorKey(Color c) => string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", c.r, c.g, c.b);

  // the Tints setting: name=r,g,b pairs, separated by semicolons; read again whenever the setting changes
  private static void ReadTintSetting()
  {
    string setting = TintSetting();
    if (setting == tintSetting)
      return;
    tintSetting = setting;
    tintOverrides = ParseTints(setting);
    if (tintOverrides.Count > 0)
      Log($"baked placements: tints from the config: {string.Join("; ", tintOverrides.Keys)} (zones built from now on)");
  }

  /// <summary>The Tints setting's colours by name.</summary>
  internal static Dictionary<string, Color> ParseTints(string setting)
  {
    var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
    foreach (string pair in setting.Split(';'))
    {
      int eq = pair.IndexOf('=');
      if (eq <= 0)
        continue;
      string[] v = pair.Substring(eq + 1).Split(',');
      if (v.Length != 3)
        continue;
      if (float.TryParse(v[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
          && float.TryParse(v[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
          && float.TryParse(v[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
        map[pair.Substring(0, eq).Trim()] = new Color(r, g, b, 1f);
    }
    return map;
  }

  // ---- diagnostics (measure the materials before blaming the shader) ------------------------------------------------------------

  private static void Describe(PieceKind k, Transform root)
  {
    var sb = new StringBuilder();
    sb.Append($"baked piece {k.Label}: {k.NearParts.Length} near part(s), {k.FarParts.Length} far");
    if (k.LodSize > 0)
      sb.Append(string.Format(CultureInfo.InvariantCulture, ", LOD size {0:0.00} m, detail to {1:0.####}, culled below {2:0.####}", k.LodSize, k.NearScreen, k.CullScreen));
    else
      sb.Append(", no LOD group");
    sb.Append($"; colliders: {k.Boxes.Length} box(es), {k.MeshVertices.Length} mesh(es), {k.ConvexMeshes.Length} convex");
    if (k.Slots.Length > 0)
      sb.Append($"; {k.Slots.Length} material variation(s), {k.Combos} combination(s)");
    if (k.HasBuckets)
      sb.Append("; random material values in " + BakedLook.Buckets + " buckets");
    if (root.localScale != Vector3.one)
      sb.Append($"; root scale {root.localScale}");
    Log(sb.ToString());
    if (!Diagnostics())
      return;
    foreach (var part in k.Parts)
      if (part.Material != null)
        DescribeMaterial(part.Material, part);
  }

  private static void DescribeMaterial(Material m, RenderPart p)
  {
    if (!Described.Add(m))
      return;
    var sb = new StringBuilder();
    sb.Append($"  material \"{m.name}\": shader \"{(m.shader != null ? m.shader.name : "none")}\", queue {m.renderQueue}");
    if (m.HasProperty("_MainTex"))
    {
      var t = m.GetTexture("_MainTex");
      Vector2 o = m.GetTextureOffset("_MainTex"), s = m.GetTextureScale("_MainTex");
      sb.Append(string.Format(CultureInfo.InvariantCulture, ", _MainTex {0} ST ({1:0.###}, {2:0.###}, {3:0.###}, {4:0.###})", t != null ? t.name : "none", s.x, s.y, o.x, o.y));
    }
    if (m.HasProperty("_Color"))
    {
      var c = m.GetColor("_Color");
      sb.Append(string.Format(CultureInfo.InvariantCulture, ", _Color ({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", c.r, c.g, c.b, c.a));
    }
    if (m.HasTexture("_ValueNoise"))
    {
      var t = m.GetTexture("_ValueNoise");
      sb.Append($", _ValueNoise {(t != null ? t.name : "none")}");
    }
    sb.Append($", mesh \"{p.Mesh?.name}\" {p.Mesh?.vertexCount} verts, submesh {p.Submesh}, shadows {p.Shadows}");
    Log(sb.ToString());
  }
}

// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// What the client renderer's tests build with (slice C): pieces, kinds, batches and zones made of plain numbers, with no Unity object (no mesh,
// no material), which is all the worker side of the renderer ever reads.
using System;
using System.Collections.Generic;
using System.Linq;
using BetterContinents;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlacementTests;

internal static class ClientKit
{
  /// <summary>One part of a piece: drawn at LOD0 (near), at the last LOD (far), or both.</summary>
  public static RenderPart Part(bool near, bool far, ShadowCastingMode shadows = ShadowCastingMode.On, int triangles = 100) =>
    new() { InNear = near, InFar = far, Shadows = shadows, Triangles = triangles, LocalIsIdentity = true, Local = Matrix4x4.identity };

  /// <summary>
  /// A piece with a LOD group (size, relative screen heights where LOD0 gives way and where the last LOD is culled): one part at LOD0 and one at
  /// the last LOD, around its origin.
  /// </summary>
  public static PieceKind TwoLods(string name, float size, float nearScreen, float cullScreen, float radius = 1f)
  {
    var piece = new PieceKind
    {
      Name = name, Label = name, Radius = radius, LodSize = size, NearScreen = nearScreen, CullScreen = cullScreen,
      Parts = [Part(true, false), Part(false, true, triangles: 12)],
    };
    piece.NearParts = [0];
    piece.FarParts = [1];
    return piece;
  }

  /// <summary>A piece drawn the same at every distance (no LOD group): its one part is in both lists.</summary>
  public static PieceKind NoLods(string name, float radius)
  {
    var piece = new PieceKind { Name = name, Label = name, Radius = radius, Parts = [Part(true, true)], SingleLod = true };
    piece.NearParts = [0];
    piece.FarParts = [0];
    return piece;
  }

  /// <summary>A palette entry's kind, resolved by hand: no game involved, batches made and alive.</summary>
  public static BakedKind Kind(string name, PieceKind? piece, BakedRole role = BakedRole.Static, BakedCollision collision = BakedCollision.None,
    PaletteFlags flags = PaletteFlags.None, Vector3? anchor = null, Vector3? anchor0 = null, KindBox[]? boxes = null, int layer = 10)
  {
    var kind = new BakedKind
    {
      Name = name, Role = role, Collision = collision, Flags = flags, Piece = piece, UnityLayer = layer,
      Anchor = anchor ?? Vector3.zero, Anchor0 = anchor0 ?? anchor ?? Vector3.zero,
      FixedVariant = piece == null ? [] : Enumerable.Repeat(-1, piece.Slots.Length).ToArray(),
    };
    kind.Boxes = (boxes ?? []).Select(b => BakedMath.BoxMatrix(b.Centre, b.Size)).ToArray();
    if (piece != null)
    {
      BakedKinds.MakeBatches(kind);
      foreach (var b in kind.Batches)
        b.Dead = false;
    }
    return kind;
  }

  /// <summary>A zone of records: (palette entry, world x, y, z, yaw in degrees, scale or null).</summary>
  public static ZoneData Zone(int zx, int zz, params (int Entry, double X, double Y, double Z, double Yaw, Vector3? Scale)[] records)
  {
    var data = new ZoneData(new ZoneKey(zx, zz), records.Length);
    var scales = new List<ushort>();
    for (int k = 0; k < records.Length; k++)
    {
      var r = records[k];
      data.Palette[k] = (ushort)r.Entry;
      data.X[k] = BakedFormat.Quantize(r.X, zx);
      data.Z[k] = BakedFormat.Quantize(r.Z, zz);
      data.Y[k] = BakedFormat.QuantizeY(r.Y);
      data.Yaw[k] = BakedFormat.QuantizeYaw(r.Yaw);
      if (r.Scale is { } s)
      {
        data.Flags[k] |= RecordFlags.Scale;
        scales.Add(ZoneRecord.QuantizeScale(s.x));
        scales.Add(ZoneRecord.QuantizeScale(s.y));
        scales.Add(ZoneRecord.QuantizeScale(s.z));
      }
    }
    data.ScaleRun = scales.ToArray();
    return data;
  }

  /// <summary>The batch of a kind that holds a part's instances at a LOD (variant and bucket 0).</summary>
  public static Batch BatchOf(BakedKind kind, int lod, int partInList) =>
    kind.Batches[(lod == 0 ? kind.NearFirst : kind.FarFirst)[partInList]];

  /// <summary>The matrices a batch holds in the buffer a job filled (the back one before the job is adopted).</summary>
  public static List<Matrix4x4> Filled(Batch batch, bool adopted)
  {
    var buf = batch.Buf[adopted ? batch.Front : 1 - batch.Front];
    return buf.M.Take(buf.Count).ToList();
  }

  /// <summary>A job that sees everything: planes every box is inside of.</summary>
  public static CullJob Job(BuiltZone[] zones, BakedKind[] kinds, float camX, float camY, float camZ, float k, float drawScale = 1f, float detailScale = 1f,
    BakedShadows shadows = BakedShadows.All)
  {
    var batches = kinds.SelectMany(kind => kind.Batches).ToArray();
    for (int i = 0; i < kinds.Length; i++)
      kinds[i].Index = i;
    var job = new CullJob
    {
      Id = 1, CamX = camX, CamY = camY, CamZ = camZ, K = k, DrawScale = drawScale, DetailScale = detailScale, Shadows = shadows,
      CasterMargin = BakedDraw.CasterReach, Zones = zones, Batches = batches, Kinds = kinds,
    };
    for (int p = 0; p < 6; p++)
    {
      job.Planes[p * 4 + 3] = 1f;
    }
    return job;
  }

  /// <summary>The camera's basis for a yaw (about y) and a pitch (up), in degrees, with an optional roll about the view axis.</summary>
  public static (Vector3 Forward, Vector3 Right, Vector3 Up) Basis(double yaw, double pitch, double roll = 0)
  {
    double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180, r = roll * Math.PI / 180;
    var forward = new Vector3((float)(Math.Sin(y) * Math.Cos(p)), (float)Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
    var right0 = new Vector3((float)Math.Cos(y), 0f, (float)-Math.Sin(y));
    var up0 = new Vector3((float)(-Math.Sin(y) * Math.Sin(p)), (float)Math.Cos(p), (float)(-Math.Cos(y) * Math.Sin(p)));
    float cr = (float)Math.Cos(r), sr = (float)Math.Sin(r);
    return (forward, right0 * cr + up0 * sr, up0 * cr - right0 * sr);
  }
}

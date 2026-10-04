// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

using UnityEngine;

namespace BetterContinents;

// A size of the world: the radius where the land starts to drop away into the edge ocean (WorldRadius), and the width
// of that edge (EdgeSize). Vanilla's is 10000 m and 500 m: a disc 10500 m in radius (TotalRadius), 21000 m across
// (TotalSize).
//
// Better Continents works with two. Its maps span BetterContinents.Geometry: every image map and the noise cover
// TotalSize, centred on the world's centre, and the land drops away past its WorldRadius. And WorldSizeHelper moves the
// game's own edge of the world (the kill zone, the ship's push back, the water's edge) to the world's "World Size" and
// "Edge Size". A WorldGeometry never changes, so a reader on any thread (the minimap's workers, the game's heightmap
// builder) takes one and sees a single size throughout, however often it is swapped.
internal sealed class WorldGeometry
{
  public static readonly WorldGeometry Vanilla = new(10000f, 500f);

  public readonly float WorldRadius;
  public readonly float EdgeSize;
  public readonly float TotalRadius;
  public readonly float TotalSize;

  public WorldGeometry(float worldRadius, float edgeSize)
  {
    WorldRadius = worldRadius;
    EdgeSize = edgeSize;
    TotalRadius = worldRadius + edgeSize;
    TotalSize = TotalRadius * 2f;
  }

  // Vanilla's own size: the game's edge needs no patch for it.
  public bool IsVanilla => WorldRadius == 10000f && EdgeSize == 500f;

  // The same size (a NaN equals a NaN here, so a broken size is not patched again at every turn).
  public bool SameAs(WorldGeometry other) => WorldRadius.Equals(other.WorldRadius) && EdgeSize.Equals(other.EdgeSize);

  // A world coordinate as a map coordinate: 0 to 1 across TotalSize, clamped.
  public float Normalize(float x) => Mathf.Clamp(x / TotalSize + 0.5f, 0f, 1f);

  private static readonly Vector2 Half = Vector2.one * 0.5f;
  // A map position (0 to 1) as a world position.
  public Vector2 NormalizedToWorld(Vector2 p) => (p - Half) * TotalSize;

  // Better Continents' base height past WorldRadius: down towards -0.2 at TotalRadius, and to -2 over the last 10 m.
  public float DropOff(float height, float distance)
  {
    if (distance > WorldRadius)
    {
      float t = Utils.LerpStep(WorldRadius, TotalRadius, distance);
      height = Mathf.Lerp(height, -0.2f, t);
      var edge = TotalRadius - 10;
      if (distance > edge)
      {
        float t2 = Utils.LerpStep(edge, TotalRadius, distance);
        height = Mathf.Lerp(height, -2f, t2);
      }
    }
    return height;
  }

  public override string ToString() => $"world radius {WorldRadius}, edge {EdgeSize}";
}

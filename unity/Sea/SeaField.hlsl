// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The sea field, read on the GPU. The twin of SeaField.cs in the mod: the same layout, the same arithmetic in the same order (SeaField.cs's header comment
// is the contract; where this file and that one differ, that one is right).
//
// The field is one RG16 texture (two unorm8 channels, point filter, no mips, clamped), set as the global _BCSeaField:
//   G = calm: the distance into the world's own Deep North, in metres, 0 .. 255 (255 = 255 m or more; 0 = open sea);
//   R = hot: the signed distance to the border of the world's hot sea, in metres, positive inside, stored + 128 and clamped to 1 .. 255
//       (so metres = byte - 128, -127 .. +127).
// Texel (ix, iz) is byte row iz, column ix of the uploaded data, so Load(int3(ix, iz, 0)); its centre is at
//   (origin x + (ix + 0.5) * spacing, origin z + (iz + 0.5) * spacing).
// _BCSeaRect = (origin x, origin z, 1 / spacing, N): the field is N x N texels, spacing metres apart.
// Outside the grid the border texels apply. Without a field (_BCSeaRect.w < 1) the game's own circles apply (BCGame... below), so a
// material that is drawn before the field is bound looks as the game's water does.
//
// The far sea's depth (Soft Far Sea) is a second texture, _BCSeaDepth (R8, bilinear): one texel per zone corner, 64 m apart,
//   holding clamp01((30 - ground height) / 10). _BCSeaDepthRect = (x of texel 0, z of texel 0, 1 / spacing, N); the texel's value is that of the
//   point (origin + i * spacing), not of a cell centre. _BCSeaSoft = 1 turns it on (0 = the far sea is deep sea everywhere, as the game draws it).

#ifndef BC_SEA_FIELD_INCLUDED
#define BC_SEA_FIELD_INCLUDED

Texture2D<float4> _BCSeaField;
float4 _BCSeaRect;
Texture2D<float4> _BCSeaDepth;
float4 _BCSeaDepthRect;
float _BCSeaSoft;

#ifndef BC_SEA_COMPUTE_CHECK
SamplerState sampler_linear_clamp;
#endif

// The width of the calm sea's ramp and of the hot sea's edge, in metres (the game's: 200 and 25).
#define BC_SEA_CALM_RAMP 200.0
#define BC_SEA_HOT_EDGE 25.0

bool BCSeaFieldReady()
{
    return _BCSeaRect.w >= 1.0;
}

// A unorm8 channel as its byte, 0 .. 255 (exact: the texture stores bytes).
float BCSeaByte(float v)
{
    return floor(v * 255.0 + 0.5);
}

// The calm distance in metres at xz: four Loads, bilinear between the four nearest texel centres by SeaField.CalmAt's arithmetic (SeaField.cs, "Waves(x, z)"):
//   u = (x - originX) * invSpacing - 0.5, clamped to -1 .. N; iu = floor(u); tx = u - iu; the two texel columns clamped to 0 .. N-1 (the border texels apply);
//   a = c00 + (c10 - c00) * tx; b = c01 + (c11 - c01) * tx; calm = a + (b - a) * tz.
float BCSeaCalmMetres(float2 xz)
{
    float2 t = (xz - _BCSeaRect.xy) * _BCSeaRect.z - 0.5;
    t = clamp(t, -1.0, _BCSeaRect.w);
    float2 fl = floor(t);
    float2 fr = t - fl;
    int last = (int)_BCSeaRect.w - 1;
    int2 i0 = clamp((int2)fl, 0, last);
    int2 i1 = clamp((int2)fl + 1, 0, last);
    float c00 = BCSeaByte(_BCSeaField.Load(int3(i0.x, i0.y, 0)).g);
    float c10 = BCSeaByte(_BCSeaField.Load(int3(i1.x, i0.y, 0)).g);
    float c01 = BCSeaByte(_BCSeaField.Load(int3(i0.x, i1.y, 0)).g);
    float c11 = BCSeaByte(_BCSeaField.Load(int3(i1.x, i1.y, 0)).g);
    float a = c00 + (c10 - c00) * fr.x;
    float b = c01 + (c11 - c01) * fr.x;
    return a + (b - a) * fr.y;
}

// The game's own Deep North: 1 - WorldGenerator.DeepNorthWaveFade(x, z).
float BCGameWaveStrength(float2 xz)
{
    float fade = saturate((length(float2(xz.x, xz.y + 4000.0)) - (12000.0 + 100.0 * sin(atan2(xz.x, xz.y) * 20.0))) / BC_SEA_CALM_RAMP);
    return 1.0 - fade;
}

// W: the wave strength at xz, 0 (calm) .. 1 (full waves): 1 - clamp01(calm * 0.005), as SeaField.Waves.
float BCSeaWaves(float2 xz)
{
    if (!BCSeaFieldReady())
        return BCGameWaveStrength(xz);
    return 1.0 - saturate(BCSeaCalmMetres(xz) * 0.005);
}

// The game's own Ashlands ring in its fragment stage: 0 inside the circle of radius 12000 around (0, 4000), 1 from 25 m outside its edge (the edge is 11987.5 + 100 sin(20 angle)).
float BCGameHotRing(float2 xz)
{
    float2 p = float2(xz.x, xz.y - 4000.0);
    return saturate((length(p) - (11987.5 + 100.0 * sin(20.0 * atan2(p.x, p.y)))) * (1.0 / BC_SEA_HOT_EDGE));
}

#ifndef BC_SEA_COMPUTE_CHECK
// The red ring at xz, 0 .. 1 (1 = hot sea): clamp01(0.5 + hot / 25), the game's 25 m edge centred on the border; a hardware bilinear read of R.
float BCSeaHotRing(float2 xz)
{
    if (!BCSeaFieldReady())
        return BCGameHotRing(xz);
    float2 uv = (xz - _BCSeaRect.xy) * (_BCSeaRect.z / _BCSeaRect.w);
    float hot = _BCSeaField.SampleLevel(sampler_linear_clamp, uv, 0).r * 255.0 - 128.0;
    return saturate(0.5 + hot / BC_SEA_HOT_EDGE);
}

// The depth under the far sea, 0 .. 1, from the zone-corner grid (hardware bilinear).
float BCSeaDepth01(float2 xz)
{
    float2 uv = ((xz - _BCSeaDepthRect.xy) * _BCSeaDepthRect.z + 0.5) / _BCSeaDepthRect.w;
    return saturate(_BCSeaDepth.SampleLevel(sampler_linear_clamp, uv, 0).r);
}
#endif

#endif

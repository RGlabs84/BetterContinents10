// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The game's ten waves, on the GPU. The twin of SeaWaves.cs (One) in the mod, line for line: BCSeaWaveOne is WaterVolume.CalcWave's private overload
// (WaterVolume.cs:186-230, Valheim 1.0) for ONE wind, with the Deep North weights replaced by the sea field's W (SeaField.hlsl):
//   waves 1-5 x W; wave 6 x max(W, 0.1), without waveFactor; waves 7-10 x max(W, 0.1) x waveFactor.
// The constants (speeds, lengths, heights, sharpness, directions) are the game's: the boats feel the same waves that are drawn.
//
//   xz         world position
//   depth      the zone's normalised depth at xz (0 .. 1)
//   wind       one wind as the game keeps it: direction (x, z), intensity w
//   time       the drawn clock (WaterVolume.s_waterTime, the shader's _WaterTime)
//   waveFactor 1 for the shader (the Leviathan feels 0.5)
//   w          the sea field's W at xz (SeaField.hlsl: BCSeaWaves)

#ifndef BC_SEA_WAVES_INCLUDED
#define BC_SEA_WAVES_INCLUDED

float BCSeaTrochSin(float x, float k)
{
    return sin(x - cos(x) * k) * 0.5 + 0.5;
}

float BCSeaCreateWave(float2 xz, float time, float waveSpeed, float waveLength, float waveHeight, float2 dir, float2 tangent, float sharpness)
{
    float2 v = -(xz.y * dir + xz.x * tangent);
    float num = time * waveSpeed;
    return (BCSeaTrochSin(num + v.y * waveLength, sharpness) * BCSeaTrochSin(num * 0.123 + v.x * 0.13123 * waveLength, sharpness) - 0.2) * waveHeight;
}

float BCSeaWaveOne(float2 xz, float depth, float4 wind, float time, float waveFactor, float w)
{
    // the directions of s_createWaveDirections[1..9] (normalised, as Vector2.normalized makes them) and their tangents (-y, x)
    const float2 dir1 = float2(0.957149267, 0.289595187), tan1 = float2(-0.289595187, 0.957149267);
    const float2 dir2 = float2(-0.109165095, 0.994023621), tan2 = float2(-0.994023621, -0.109165095);
    const float2 dir3 = float2(0.959618032, 0.281306446), tan3 = float2(-0.281306446, 0.959618032);
    const float2 dir4 = float2(0.188733578, -0.982028306), tan4 = float2(0.982028306, 0.188733578);
    const float2 dir5 = float2(-0.632776499, -0.77433449), tan5 = float2(0.77433449, -0.632776499);
    const float2 dir6 = float2(0.288534701, 0.957469404), tan6 = float2(-0.957469404, 0.288534701);
    const float2 dir7 = float2(0.967817426, -0.251653492), tan7 = float2(0.251653492, 0.967817426);
    const float2 dir8 = float2(-0.591760695, 0.806113601), tan8 = float2(-0.806113601, -0.591760695);
    const float2 dir9 = float2(0.547449291, -0.836838841), tan9 = float2(0.836838841, 0.547449291);

    // wave 1 runs along the wind
    float2 dir0 = float2(wind.x, wind.z);
    float len0 = length(dir0);
    dir0 = len0 > 1e-5 ? dir0 / len0 : float2(0.0, 0.0);
    float2 tan0 = float2(-dir0.y, dir0.x);

    float num = lerp(0.0, wind.w, saturate(depth));
    float t = time / 20.0;
    float lowW = max(w, 0.1);

    float w1 = BCSeaCreateWave(xz, t, 10.0, 0.04, 8.0 * w, dir0, tan0, 0.5);
    float w2 = BCSeaCreateWave(xz, t, 14.123, 0.08, 6.0 * w, dir1, tan1, 0.5);
    float w3 = BCSeaCreateWave(xz, t, 22.312, 0.1, 4.0 * w, dir2, tan2, 0.5);
    float w4 = BCSeaCreateWave(xz, t, 31.42, 0.2, 2.0 * w, dir3, tan3, 0.5);
    float w5 = BCSeaCreateWave(xz, t, 35.42, 0.4, 1.0 * w, dir4, tan4, 0.5);
    float w6 = BCSeaCreateWave(xz, t, 38.1223, 1.0, 0.8 * lowW, dir5, tan5, 0.7);
    float w7 = BCSeaCreateWave(xz, t, 41.1223, 1.2, 0.6 * lowW * waveFactor, dir6, tan6, 0.8);
    float w8 = BCSeaCreateWave(xz, t, 51.5123, 1.3, 0.4 * lowW * waveFactor, dir7, tan7, 0.9);
    float w9 = BCSeaCreateWave(xz, t, 54.2, 1.3, 0.3 * lowW * waveFactor, dir8, tan8, 0.9);
    float w10 = BCSeaCreateWave(xz, t, 56.123, 1.5, 0.2 * lowW * waveFactor, dir9, tan9, 0.9);
    return (w1 + w2 + w3 + w4 + w5 + w6 + w7 + w8 + w9 + w10) * num;
}

#endif

// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The vertex stage of BC's water, shared by the water shader (its depth prepass, its colour pass) and the probe: the zone water's waves.
// Written from the behaviour of the game's Custom/Water (libs-Tools WATER-SHADER-FACTS.md, section 4); the waves come from SeaWaves.hlsl and the
// sea field's W from SeaField.hlsl. The far sea (_IsLod >= 0.5) is flat: nothing here touches it.

#ifndef BC_WATER_VERTEX_INCLUDED
#define BC_WATER_VERTEX_INCLUDED

#include "SeaField.hlsl"
#include "SeaWaves.hlsl"

// set by the game on the zone water's material (WaterVolume.SetupMaterial, UpdateMaterials) and globally (EnvMan)
float _depth[4];
float _WaterTime;
float _UseGlobalWind;
float4 _GlobalWind1;
float4 _GlobalWind2;
float _GlobalWindAlpha;
float4 _NetRefPos;
// material properties
float _VisibleMaxDistance;
float _IsLod;
float _WaterEdge;

// The height of the zone water at xz: the two winds blended by the transition's alpha. w is the sea field's W there.
float BCWaterHeight(float2 xz, float depth, float w)
{
    bool globalWind = _UseGlobalWind > 0.5;
    float4 wind1 = globalWind ? _GlobalWind1 : float4(1.0, 0.0, 0.0, 0.0);
    float4 wind2 = globalWind ? _GlobalWind2 : float4(1.0, 0.0, 0.0, 0.0);
    float alpha = globalWind ? _GlobalWindAlpha : 0.0;
    BC_PRECISE float a = BCSeaWaveOne(xz, depth, wind1, _WaterTime, 1.0, w);
    if (alpha == 0.0)
        return a;
    BC_PRECISE float b = BCSeaWaveOne(xz, depth, wind2, _WaterTime, 1.0, w);
    BC_PRECISE float h = a + (b - a) * alpha;
    return h;
}

// Moves a vertex of the zone water (object space) to its wave height and sets its normal, as the game's vertex stage does:
//   y = H * nearFade + y, minus 500 m x smoothstep over the 50 m past _WaterEdge; the normal from the height 0.5 m on each side;
//   crest = how far the vertex stands above its four neighbours (the foam on the crests).
// uv is the mesh's own uv (0 .. 1 over the zone), which picks the depth between the zone's four corner depths.
// BC_WATER_PREPASS: only the height is needed (the depth prepass).
void BCWaterDisplace(inout float4 vertex, inout float3 normal, float2 uv, out float crest)
{
    crest = 0.0;
    if (_IsLod >= 0.5)
        return;
    float2 xz = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0)).xz;
    float nearFade = 1.0 - saturate((distance(xz, _NetRefPos.xz) - (_VisibleMaxDistance - 20.0)) / 20.0);
    float depth = lerp(lerp(_depth[3], _depth[2], uv.x), lerp(_depth[0], _depth[1], uv.x), uv.y);
    float w = BCSeaWaves(xz);
    BC_PRECISE float h0 = BCWaterHeight(xz, depth, w);
    float edge = saturate((length(xz) - _WaterEdge) * 0.02);
    BC_PRECISE float y = h0 * nearFade + vertex.y - 500.0 * (edge * edge * (3.0 - 2.0 * edge));
    vertex.y = y;
#ifndef BC_WATER_PREPASS
    // the height 0.5 m east, north, west and south, with this vertex's own W
    float hE = BCWaterHeight(xz + float2(0.5, 0.0), depth, w);
    float hN = BCWaterHeight(xz + float2(0.0, 0.5), depth, w);
    float hW = BCWaterHeight(xz + float2(-0.5, 0.0), depth, w);
    float hS = BCWaterHeight(xz + float2(0.0, -0.5), depth, w);
    normal = normalize(float3(-20.0 * (hE - h0), 1.0, -20.0 * (hN - h0)));
    crest = max(h0 - hE, 0.0) + max(h0 - hN, 0.0) + max(h0 - hW, 0.0) + max(h0 - hS, 0.0);
#endif
}

#endif

// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// The surface functions of BC's water (a Unity surface shader, BetterContinentsWater.shader), shared by its two sub-shaders: the vertex function, the
// tessellation factors, the surface function (colour, foam, normals, refraction, the Ashlands red), the fog and the lighting model.
// Written from the behaviour of the game's Custom/Water, read in its compiled programs (libs-Tools WATER-SHADER-FACTS.md, sections 3 to 8); no
// compiled text is copied. The differences from the game's shader are the sea field's W and red mask (SeaField.hlsl), the Soft Far Sea depth,
// and the waves' weights (SeaWaves.hlsl).
//
// The including shader defines first:
//   BC_WATER_PREPASS   the depth prepass: only waves and the far sea's hiding, no colour
//   BC_WATER_TESS      the tessellated sub-shader (hull factors from BCTess)
//   BC_BRDF            Unity's BRDF function for the lighting (BRDF1_Unity_PBS or BRDF3_Unity_PBS, as the game's two sub-shaders use)

#ifndef BC_WATER_SURFACE_INCLUDED
#define BC_WATER_SURFACE_INCLUDED

#include "UnityCG.cginc"
#include "Tessellation.cginc"
#ifndef BC_WATER_PREPASS
#include "UnityPBSLighting.cginc"
#endif
#include "BCWaterVertex.hlsl"

// ---- properties (the same names as Custom/Water's; _NormalFine, _AshlandsDistance and _AshlandsYOffset are kept on the material but never read)
sampler2D _MainTex;                    // never set (the game's shader has no _MainTex either); only for uv_MainTex's scale and offset
float _Tess;
#ifndef BC_WATER_PREPASS
sampler2D _FoamTex;
sampler2D _FoamHighTex;
sampler2D _RandomFoamTex;
sampler2D _Normal;
sampler2D _BubbleTexture;
sampler3D _CurlTex;
float _Glossiness;
float _Metallic;
float _NormalPower;
float _NormalScale;
float _WaveVel;
float _DepthFade;
float _ShoreFade;
float _FoamDepth;
float _RefractionScale;
float _RefractionMax;
float4 _ColorTop;
float4 _ColorBottom;
float4 _ColorBottomShallow;
float4 _SurfaceColor;
float4 _FoamColor;
float4 _AshlandsColorTop;
float4 _AshlandsColorBottom;
float4 _AshlandsColorBottomShallow;
float4 _AshlandsSurfaceColor;
float4 _AshlandsFoamColor;
// set by the game (EnvMan) and by the engine
float4 _SunDir;
float4 _SunFogColor;
float unity_FogDensity;
float unity_FogStart;
float unity_FogEnd;
sampler2D _BackgroundTex;              // the GrabPass
sampler2D_float _CameraDepthTexture;
#endif

struct Input
{
#ifndef BC_WATER_PREPASS
    float2 uv_MainTex;
    float4 screenPos;
    float4 color : COLOR;              // .w = the crest (the game's does the same: its COLOR0.w), written by BCVert
#endif
    float3 worldPos;
};

// One parameter: a surface shader with tessellation calls its vertex function (in the domain stage) with the vertex alone. The crest goes to the
// vertex colour's alpha, which the stages interpolate, and the surface function reads it back from Input.color.
void BCVert(inout appdata_full v)
{
    float crest;
    BCWaterDisplace(v.vertex, v.normal, v.texcoord.xy, crest);
    v.color = float4(1.0, 1.0, 1.0, crest);
}

#ifdef BC_WATER_TESS
// The tessellation factors: Unity's distance-based factors, full detail within 10 m, the minimum from 50 m, times _Tess; all 1 with the keyword off.
float4 BCTess(appdata_full v0, appdata_full v1, appdata_full v2)
{
#ifdef TESSELATION_ON
    return UnityDistanceBasedTess(v0.vertex, v1.vertex, v2.vertex, 10.0, 50.0, _Tess);
#else
    return float4(1.0, 1.0, 1.0, 1.0);
#endif
}
#endif

#ifdef BC_WATER_PREPASS

// ---- the depth prepass: depth only. The far sea hides within _VisibleMaxDistance of _NetRefPos (the zone water is drawn there).
half4 LightingBCNone(SurfaceOutput s, half3 lightDir, half atten)
{
    return half4(0, 0, 0, 0);
}

void BCSurf(Input IN, inout SurfaceOutput o)
{
    if (_IsLod > 0.5 && distance(IN.worldPos.xz, _NetRefPos.xz) < _VisibleMaxDistance)
        clip(-1.0);
    o.Albedo = 0;
    o.Emission = 0;
    o.Alpha = 1;
}

#else

// ---- the colour pass

float BCSmooth(float x)
{
    return x * x * (3.0 - 2.0 * x);
}

// One wind's normal: three scrolling samples of the normal map, blended and pulled towards up by the depth.
float3 BCWindNormal(float4 wind, float2 xz, float k, float t)
{
    float2 q = float2(xz.y * wind.x - xz.x * wind.z, xz.x * wind.x + xz.y * wind.z) * _NormalScale;
    float3 n1 = UnpackNormalmapRGorAG(tex2D(_Normal, q * 0.2 + float2(0.0, -0.2 * t)));
    float3 n2 = UnpackNormalmapRGorAG(tex2D(_Normal, q + float2(0.0, -0.22 * t)));
    float3 n3 = UnpackNormalmapRGorAG(tex2D(_Normal, q + float2(0.01 * t, -0.2 * t)));
    float3 sum = n1 + 0.3 * n2 + 0.3 * n3;
    return normalize(k * sum + float3(0.0, 0.0, 1.0));
}

void BCSurf(Input IN, inout SurfaceOutputStandard o)
{
    float3 P = IN.worldPos;
    float2 xz = P.xz;
    bool isLod = _IsLod > 0.5;
    float fromCentre = length(xz);
    if (isLod && _WaterEdge < fromCentre)
        clip(-1.0);

    float eyeDepth = IN.screenPos.w;
    float h = BCSmooth(saturate((P.y - 29.0) / 6.0));

    // the depth under the water: the zone's four corner depths (the far sea: deep sea, or the sea's own depth with Soft Far Sea)
    float2 uv = IN.uv_MainTex;
    float depth = lerp(lerp(_depth[3], _depth[2], uv.x), lerp(_depth[0], _depth[1], uv.x), uv.y);
    if (isLod)
        depth = _BCSeaSoft > 0.5 ? BCSeaDepth01(xz) : 1.0;

    // the Ashlands red: the field's ring, broken up by the foam texture moving through the curl noise (an overlay blend)
    float ring = BCSeaHotRing(xz);
    float2 curl = tex3D(_CurlTex, float3(P.x, 0.33 * _Time.y + P.y, P.z) * 0.1).xy;
    float nRaw = tex2D(_FoamTex, xz * 0.0666666701 + 0.05 * curl).x;
    float nSmooth = BCSmooth(saturate(nRaw * 3.030303));
    float overlay = ring <= 0.5 ? nSmooth * (2.0 * ring) : 1.0 - (2.0 * (1.0 - ring)) * (1.0 - nSmooth);
    float m = BCSmooth(saturate(overlay * 1.11111116));

    // the water's normal (tangent space) from the two winds
    float t = _Time.x * _WaveVel;
    float k = min(max(depth, 0.2), 1.0);
    float3 nWind1 = BCWindNormal(_GlobalWind1, xz, k, t);
    float3 nWind2 = BCWindNormal(_GlobalWind2, xz, k, t);
    float3 n = lerp(nWind1, nWind2, _GlobalWindAlpha);
    n = (n - float3(0.0, 0.0, 1.0)) * _NormalPower + float3(0.0, 0.0, 1.0);

    // refraction and the scene's depth under the water
    float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
    float2 shift = clamp(n.xy * _RefractionScale * 5.0, -_RefractionMax, _RefractionMax);
    float2 refractedUV = screenUV + shift;
    float depthCentre = LinearEyeDepth(tex2D(_CameraDepthTexture, screenUV).r);
    float depthRefracted = LinearEyeDepth(tex2D(_CameraDepthTexture, refractedUV).r);
    bool keepCentre = depthRefracted < eyeDepth;
    float2 bgUV = keepCentre ? screenUV : refractedUV;
    float depthBg = keepCentre ? depthCentre : depthRefracted;
    float3 bg = tex2D(_BackgroundTex, bgUV).rgb;
    float fade = sqrt(saturate(abs(depthBg - eyeDepth) / _DepthFade));
    float shoreT = abs(depthCentre - eyeDepth) - 0.4 * n.y - 0.15;
    float shoreA = saturate(shoreT / _ShoreFade);
    float shoreB = saturate(shoreT / _FoamDepth);

    // colours: shallow to deep by depth, bottom to top by height, towards the Ashlands twins by the red mask
    float3 bottom = lerp(lerp(_ColorBottomShallow.rgb, _AshlandsColorBottomShallow.rgb, m), lerp(_ColorBottom.rgb, _AshlandsColorBottom.rgb, m), depth) * fade;
    float3 top = lerp(_ColorTop.rgb, _AshlandsColorTop.rgb, m);
    float3 c = lerp(bottom, top * fade, h * depth);
    float3 surfaceColor = lerp(_SurfaceColor.rgb, _AshlandsSurfaceColor.rgb, m);
    float4 foam = lerp(_FoamColor, _AshlandsFoamColor, m);

    // foam: random patches from the wind, along the shore, on the crests, (the Ashlands' bubble layer is left out, see below)
    float windIntensity = lerp(_GlobalWind1.w, _GlobalWind2.w, _GlobalWindAlpha);
    float2 wobble = 0.005 * float2(sin(_Time.x * 20.0 + P.x), sin(_Time.x * 30.0 + P.z));
    float randomFoam = 5.0 * windIntensity * depth * tex2D(_RandomFoamTex, xz * 0.03 + wobble).x;
    c = lerp(c, foam.rgb, randomFoam * foam.a);
    float gloss = _Glossiness * (1.0 - randomFoam);
    float crest = IN.color.w;
    float crest2 = crest * crest;
    float crest3 = crest2 * crest;
    float shore = depth * min(shoreA, min(1.0 - shoreB + 1.5 * crest2, 1.0));
    c = lerp(c, foam.rgb, 8.0 * shore * foam.a * tex2D(_FoamTex, xz * 0.1 + wobble).x);
    c = lerp(c, top, crest2);
    c = lerp(c, float3(0.9, 0.9, 0.9), crest3);
    // The game draws a layer of large bubble patches on its Ashlands sea (the _BubbleTexture property, kept on the material). Better Continents leaves it
    // out on purpose (the user, 2026-10-13: they look terrible): the hot sea is red, with the same foam and waves as any sea.

    // the world's edge: foam over the last 200 m
    float edgeBand = BCSmooth(saturate((fromCentre - (_WaterEdge - 200.0)) / 200.0));
    float2 outward = xz / max(fromCentre, 0.001);
    float edgeFoam = 5.0 * edgeBand * tex2D(_FoamHighTex, xz * 0.001 - outward * _Time.x * 0.2).x
                   + edgeBand * tex2D(_FoamTex, xz * 0.05 - outward * _Time.x * 7.0).x;
    c = lerp(c, _FoamColor.rgb, edgeFoam);

    // the alpha: from the shore; the far sea fades in over the last 20 m before _VisibleMaxDistance; everything fades 300 to 800 m from the camera
    float alpha = shoreA * shoreA;
    if (isLod)
        alpha = min(saturate((distance(xz, _NetRefPos.xz) - (_VisibleMaxDistance - 20.0)) / 20.0), alpha);
    alpha *= 1.0 - saturate((distance(xz, _WorldSpaceCameraPos.xz) - 300.0) * 0.002);

    o.Albedo = c;
    o.Normal = n;
    o.Metallic = _Metallic * (1.0 - shore);
    o.Smoothness = lerp(gloss, 0.1, shore);
    o.Occlusion = 1.0;
    o.Emission = bg * surfaceColor * (1.0 - fade);
    o.Alpha = alpha;
}

// The game's own fog, in place of Unity's: the colour leans towards the sun's fog colour looking at the sun; there is more fog by distance and in
// the linear range. The extra-light pass fogs to black (it is added to the base pass).
void BCFin(Input IN, SurfaceOutputStandard o, inout fixed4 color)
{
    float eyeDepth = IN.screenPos.w;
    float3 toPoint = normalize(IN.worldPos - _WorldSpaceCameraPos);
    float3 fogColor = lerp(unity_FogColor.rgb, pow(_SunFogColor.rgb, 2.2), saturate(dot(toPoint, _SunDir.xyz)));
    float expo = 1.0 - min(exp2(-eyeDepth * unity_FogDensity), 1.0);
    float linearRange = 1.0 - saturate((unity_FogEnd - eyeDepth) / (unity_FogEnd - unity_FogStart));
#ifdef UNITY_PASS_FORWARDADD
    fogColor = float3(0.0, 0.0, 0.0);
#endif
    color.rgb = lerp(color.rgb, fogColor, max(expo, linearRange));
}

// Unity's Standard lighting with the BRDF the including sub-shader names (the game's sub-shader 0 uses BRDF1, sub-shader 1 BRDF3).
half4 LightingBCStd(SurfaceOutputStandard s, float3 viewDir, UnityGI gi)
{
    s.Normal = normalize(s.Normal);
    half oneMinusReflectivity;
    half3 specColor;
    s.Albedo = DiffuseAndSpecularFromMetallic(s.Albedo, s.Metallic, /*out*/ specColor, /*out*/ oneMinusReflectivity);
    half outputAlpha;
    s.Albedo = PreMultiplyAlpha(s.Albedo, s.Alpha, oneMinusReflectivity, /*out*/ outputAlpha);
    half4 c = BC_BRDF(s.Albedo, specColor, oneMinusReflectivity, s.Smoothness, s.Normal, viewDir, gi.light, gi.indirect);
    c.a = outputAlpha;
    return c;
}

void LightingBCStd_GI(SurfaceOutputStandard s, UnityGIInput data, inout UnityGI gi)
{
    LightingStandard_GI(s, data, gi);
}

#endif

#endif

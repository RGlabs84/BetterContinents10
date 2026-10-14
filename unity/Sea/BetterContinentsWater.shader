// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// BetterContinents/Water: Better Continents' own sea. A Unity surface shader that mirrors the game's Custom/Water (Valheim 1.0), written from the
// behaviour read in the game's compiled programs (libs-Tools WATER-SHADER-FACTS.md); no compiled text is copied. It goes onto the game's own water
// materials in place of Custom/Water ("material.shader = ..."), keeps all 33 property names, types and defaults, and draws the same way:
//   - two sub-shaders: LOD 250 with distance tessellation (the game's Tessellation setting on) and LOD 200 without it;
//   - queue Transparent-100 (2900), a GrabPass "_BackgroundTex", a depth prepass (ZWrite On, ColorMask 0) and the colour pass
//     (ZWrite Off, alpha-blended: Blend SrcAlpha OneMinusSrcAlpha, extra lights SrcAlpha One);
//   - in every pass: ZTest LEqual, Cull Back, Offset 0 10 and Stencil Ref 2 Comp NotEqual (the ship's water mask keeps a hull dry).
// What differs from the game's: waves out to the world's edge and the calm only in the world's own Deep North, and the Ashlands red where the
// world's hot sea is, both read from the sea field (SeaField.hlsl); with Soft Far Sea, the far sea takes the depth of the sea under it.
// BC's inputs are global (_BCSeaField, _BCSeaRect, _BCSeaDepth, _BCSeaDepthRect, _BCSeaSoft), so no material changes but its shader.
// Not mirrored (they change nothing that is drawn): the game's colour-less extra-light pass of the prepass and its deferred pass (the water is
// forward-rendered), TESSELATION_ON in the LOD 200 sub-shader (the game's compiled variants of it are identical), the Fallback "Diffuse".
Shader "BetterContinents/Water"
{
    Properties
    {
        _ColorTop ("Color top", Color) = (1, 1, 1, 1)
        _ColorBottom ("Color bottom deep", Color) = (1, 1, 1, 1)
        _ColorBottomShallow ("Color bottom shallow", Color) = (1, 1, 1, 1)
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        _FoamTex ("Foam", 2D) = "white" {}
        _FoamHighTex ("Foam high", 2D) = "white" {}
        _RandomFoamTex ("Random Foam", 2D) = "white" {}
        _Normal ("Normalmap", 2D) = "bump" {}
        _NormalFine ("NormalFine", 2D) = "bump" {}
        _NormalPower ("NormalPower", Range(0, 5)) = 1
        _NormalScale ("NormalScale", Float) = 1
        _WaveVel ("WaveVel", Float) = 1
        _DepthFade ("DepthFade", Float) = 1
        _ShoreFade ("ShoreFade", Float) = 1
        _FoamDepth ("FoamDepth", Float) = 0.05
        _FoamColor ("FoamColor", Color) = (1, 1, 1, 1)
        _RefractionScale ("Refraction Scale", Float) = 1
        _RefractionMax ("Refraction Max", Float) = 0.01
        _SurfaceColor ("SurfaceColor", Color) = (1, 1, 1, 1)
        _Tess ("Tessellation", Range(1, 32)) = 4
        [MaterialToggle] _IsLod ("Is lod", Float) = 0
        _VisibleMaxDistance ("Lod distance", Float) = 0
        _WaterEdge ("World water edge", Float) = 9100
        [Header(Ashlands Settings)] [Space(10)] _CurlTex ("Curl Noise", 3D) = "white{}" {}
        _BubbleTexture ("Bubble Texture", 2D) = "white" {}
        _AshlandsDistance ("Distance", Float) = 12000
        _AshlandsYOffset ("Y Offset", Float) = 4000
        _AshlandsSurfaceColor ("Surface Color", Color) = (1, 1, 1, 1)
        _AshlandsFoamColor ("Foam Color", Color) = (1, 1, 1, 1)
        _AshlandsColorTop ("Color top", Color) = (1, 1, 1, 1)
        _AshlandsColorBottom ("Color bottom deep", Color) = (1, 1, 1, 1)
        _AshlandsColorBottomShallow ("Color bottom shallow", Color) = (1, 1, 1, 1)
    }

    // LOD 250: the tessellated sub-shader (the game sets maximumLOD 250 with its Tessellation setting on)
    SubShader
    {
        Tags { "Queue" = "Transparent-100" "RenderType" = "Transparent" }
        LOD 250

        GrabPass { "_BackgroundTex" }

        // the depth prepass
        ZTest LEqual
        ZWrite On
        ColorMask 0
        Cull Back
        Offset 0, 10
        Stencil { Ref 2 Comp NotEqual }
        CGPROGRAM
        #pragma surface BCSurf BCNone vertex:BCVert tessellate:BCTess noforwardadd noambient novertexlights nolightmap nodynlightmap nodirlightmap noshadow noshadowmask nofog nometa exclude_path:deferred exclude_path:prepass
        #pragma target 4.6
        #pragma multi_compile __ TESSELATION_ON
        #define BC_WATER_PREPASS
        #define BC_WATER_TESS
        #include "BCWaterSurface.hlsl"
        ENDCG

        // the colour pass
        ZTest LEqual
        ZWrite Off
        ColorMask RGB
        Cull Back
        Offset 0, 10
        Stencil { Ref 2 Comp NotEqual }
        CGPROGRAM
        #pragma surface BCSurf BCStd vertex:BCVert tessellate:BCTess finalcolor:BCFin alpha:fade nofog nometa noshadow nolightmap nodynlightmap nodirlightmap noshadowmask exclude_path:deferred exclude_path:prepass
        #pragma target 4.6
        #pragma multi_compile __ TESSELATION_ON
        #define BC_WATER_TESS
        #define BC_BRDF BRDF1_Unity_PBS
        #include "BCWaterSurface.hlsl"
        ENDCG
    }

    // LOD 200: without tessellation (the game's Tessellation setting off); its lighting is Unity's simpler BRDF, as the game's
    SubShader
    {
        Tags { "Queue" = "Transparent-100" "RenderType" = "Transparent" }
        LOD 200

        GrabPass { "_BackgroundTex" }

        ZTest LEqual
        ZWrite On
        ColorMask 0
        Cull Back
        Offset 0, 10
        Stencil { Ref 2 Comp NotEqual }
        CGPROGRAM
        #pragma surface BCSurf BCNone vertex:BCVert noforwardadd noambient novertexlights nolightmap nodynlightmap nodirlightmap noshadow noshadowmask nofog nometa exclude_path:deferred exclude_path:prepass
        #pragma target 3.5
        #define BC_WATER_PREPASS
        #include "BCWaterSurface.hlsl"
        ENDCG

        ZTest LEqual
        ZWrite Off
        ColorMask RGB
        Cull Back
        Offset 0, 10
        Stencil { Ref 2 Comp NotEqual }
        CGPROGRAM
        #pragma surface BCSurf BCStd vertex:BCVert finalcolor:BCFin alpha:fade nofog nometa noshadow nolightmap nodynlightmap nodirlightmap noshadowmask exclude_path:deferred exclude_path:prepass
        #pragma target 3.5
        #define BC_BRDF BRDF3_Unity_PBS
        #include "BCWaterSurface.hlsl"
        ENDCG
    }
}

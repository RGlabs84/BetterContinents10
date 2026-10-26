// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
// BetterContinents/WaterProbe: a replacement shader for `bc water probe`. Camera.RenderWithShader(probe, "RenderType") draws every object whose shader has
// RenderType=Transparent (the water, BC's and the game's) with this shader and with the object's own material properties. It writes the world position
// of the displaced zone-water vertex into an RGBAFloat target (rgb = x, y, z; a = 1 for zone water, 0 for the far sea), so the C# side can compare the drawn
// height with WaterVolume.GetWaterSurface. It uses the water's own vertex code (BCWaterVertex.hlsl), so it follows BC's sea field and the same clock.
// Use with tessellation off: the mesh's own vertices are then what is read.
Shader "BetterContinents/WaterProbe"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite On
            Cull Off
            Blend Off
            ColorMask RGBA
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #define BC_WATER_PREPASS
            #include "UnityCG.cginc"
            #include "BCWaterVertex.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float crest;
                BCWaterDisplace(v.vertex, v.normal, v.uv, crest);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.world, _IsLod > 0.5 ? 0.0 : 1.0);
            }
            ENDCG
        }
    }
}

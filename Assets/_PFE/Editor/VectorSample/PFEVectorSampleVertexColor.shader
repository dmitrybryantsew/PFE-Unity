// Test shader #1 for the Vector Sample window.
//
// Why this exists: Unity's built-in vector shaders (Unlit/Vector, Unlit/VectorGradient) are the
// documented pairing for VectorUtils-built sprites, but nothing in this project has ever rendered
// one, so "does a vector sprite draw under this URP 2D configuration?" was an open question. This
// shader answers it from our own code instead of from Unity's.
//
// The load-bearing line is the pass tag:
//     Tags { "LightMode" = "SRPDefaultUnlit" }
// URP only draws passes whose LightMode it knows about. SRPDefaultUnlit is in URP's
// DrawObjectsPass tag list, and — decisively for this project, which is a 2D renderer — it is read
// by name in the 2D passes:
//     Runtime/2D/Passes/Render2DLightingPass.cs:26   k_LegacyPassName = new ShaderTagId("SRPDefaultUnlit")
//     Runtime/2D/Rendergraph/DrawRenderer2DPass.cs:16 k_LegacyPassName = new ShaderTagId("SRPDefaultUnlit")
// A legacy pass with no LightMode at all is *implicitly renamed* to SRPDefaultUnlit, so this tag is
// equivalent to what Sprites/Default already does here — it is written out so the mechanism is
// visible rather than implied.
//
// What it does: output = vertexColor * tint * atlas. The vertex colors carry the SolidFill colors
// baked by VectorUtils.BuildSprite; the atlas carries gradients and bitmap fills. With no atlas
// assigned, _MainTex falls back to white and the result is the flat vertex color — which is exactly
// what makes this shader useful as the "atlas path works" test rather than only a happy path.
Shader "PFE/Vector Sample/VertexColor"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "SRPDefaultUnlit"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * i.color;
            }
            ENDCG
        }
    }

    Fallback Off
}

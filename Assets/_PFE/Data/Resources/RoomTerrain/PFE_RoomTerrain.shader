// Draws a terrain shape mask with the material bitmap projected in world space.
//
// This is the second half of the quarter renderer: the Tilemap supplies geometry and a shape mask,
// and everything inside the mask comes from one texture sampled by the fragment's world position.
// Because the sample coordinate is absolute rather than per-tile, cell boundaries do not exist in the
// shader — there is nothing to line up, and no per-material phase offset to get wrong.
//
// Unlit on purpose. Lighting parity is a separate tier: it needs a light buffer (the visibility/radiance
// texture the fog pass already builds) and a shader that samples it. Until then _Ambient is the single
// room brightness, so this can be compared against the shipped tiles at the same darkness instead of
// looking better because it is brighter.
Shader "PFE/RoomTerrain"
{
    Properties
    {
        // Bound per chunk by the TilemapRenderer: the mask sprite's texture.
        [PerRendererData] _MainTex ("Shape mask", 2D) = "white" {}
        _MaterialTex ("Material texture", 2D) = "white" {}
        _TexelsPerMeter ("Texture texels per world unit", Float) = 40
        _MaterialOrigin ("Bitmap fill origin (room top-left, world)", Vector) = (0, 0, 0, 0)
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Ambient ("Ambient brightness", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        TEXTURE2D(_MaterialTex);
        SAMPLER(sampler_MaterialTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MaterialTex_TexelSize;
            float4 _MaterialOrigin;
            float _TexelsPerMeter;
            half4 _Color;
            half _Ambient;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            float4 color : COLOR;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float2 world : TEXCOORD1;
            float4 color : COLOR;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = positions.positionCS;
            output.world = positions.positionWS.xy;
            output.uv = input.uv;
            output.color = input.color * _Color;
            return output;
        }

        half4 Frag(Varyings input) : SV_Target
        {
            // The mask is alpha only: it decides where the material is allowed to exist.
            half mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;

            // Bitmap rows run down from the fill origin and Unity's v runs up, so subtracting the
            // room's top edge makes world-down map to v-down instead of mirroring the texture.
            float2 texel = (input.world - _MaterialOrigin.xy) * _TexelsPerMeter;
            half4 material = SAMPLE_TEXTURE2D(_MaterialTex, sampler_MaterialTex,
                                              texel * _MaterialTex_TexelSize.xy) * input.color;

            material.a *= mask;
            if (material.a <= 0) discard;

            material.rgb *= _Ambient;
            return material;
        }
        ENDHLSL

        // URP's 2D renderer only draws passes tagged Universal2D; this project uses Renderer2D.
        Pass
        {
            Name "RoomTerrain2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        // Same body for a 3D renderer, so the shader is not silently invisible if the pipeline asset
        // is ever swapped. Both passes share the HLSLINCLUDE above, so they cannot drift.
        Pass
        {
            Name "RoomTerrainForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }

    Fallback Off
}

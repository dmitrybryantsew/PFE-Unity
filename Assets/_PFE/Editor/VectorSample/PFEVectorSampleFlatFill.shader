// Test shader #2 for the Vector Sample window — the *discriminator*.
//
// Same geometry, same vertex colors, same pass tag as PFEVectorSampleVertexColor, with exactly one
// difference: it never samples the atlas. Output = vertexColor * tint.
//
// Why that matters. When a vector sprite comes out wrong there are two very different causes, and
// from a screenshot they look the same:
//
//   * the ATLAS / UV path is broken  -> gradients and bitmap fills are wrong, flat fills are fine
//   * the GEOMETRY is broken         -> everything is wrong, including flat fills
//
// Rendering the same sprite with this shader and with PFEVectorSampleVertexColor separates them:
// if FlatFill looks right and VertexColor looks wrong, the fault is in GenerateAtlasAndFillUVs /
// UVs / _MainTex, not in the tessellation. If *both* look wrong, stop looking at the atlas.
//
// It is also the honest way to read a 449-file PatternFilled shape: those carry their fill as an
// embedded bitmap, so FlatFill shows the bare path geometry with no fill texture at all — which is
// precisely the "did the pattern survive?" question, with the pattern removed on purpose.
Shader "PFE/Vector Sample/FlatFill"
{
    Properties
    {
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
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color  : COLOR;
            };

            fixed4 _Color;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    Fallback Off
}

// The corpse dissolve — AS3 `fe/serv/Desintegr.as`, as a sprite shader.
//
// A dying body in the oracle is not faded out: its bitmap is colour-transformed for ten frames and then
// `BitmapData.pixelDissolve`d away over thirty, while a GlowFilter halo ramps up around it. Unity has no
// per-pixel API that cheap, so the same three effects are done here in one fragment program:
//
//   * _Mul* / _Add*   — the ColorTransform. The oracle applies it to its own bitmap once per frame, so the
//                       multipliers COMPOUND; the C# side (`DesintegrRules.TintAt`) already raises them to
//                       the frame count and hands over the accumulated pair.
//   * _Dissolve       — the erosion threshold, 0..1. Pixels whose noise value falls below it are clipped
//                       away, so the body disappears a random block at a time exactly as pixelDissolve
//                       does, and the pattern is stable frame to frame because the noise is a function of
//                       the sprite's own UV.
//   * _GlowAlpha      — the GlowFilter. A real blur needs a second pass, so this is a four-tap rim: the
//                       max alpha of the neighbours minus this fragment's own, which lights up the
//                       silhouette's outside edge and the burning edge of the erosion. The oracle's blur
//                       radius (`3` px) becomes `_GlowRadiusUV`, computed per sprite from its texel size.
//
// Unlit and alpha-blended like `Sprite-Unlit-Default`, which is the material every unit sprite already
// renders with — so a dissolving body is lit exactly as it was while alive.
Shader "PFE/UnitDissolve"
{
    Properties
    {
        // Bound per draw by the SpriteRenderer: the sprite's own texture. `[PerRendererData]` is what
        // stops the inspector (and the material asset) from claiming to own it.
        [PerRendererData] _MainTex ("Sprite texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)

        // ── The colour transform, accumulated on the CPU ─────────────────────────────
        _MulR ("Colour multiplier R", Float) = 1
        _MulG ("Colour multiplier G", Float) = 1
        _MulB ("Colour multiplier B", Float) = 1
        _AddR ("Colour offset R", Float) = 0
        _AddG ("Colour offset G", Float) = 0
        _AddB ("Colour offset B", Float) = 0

        // ── The erosion ──────────────────────────────────────────────────────────────
        // 0 = untouched, 1 = gone. Driven from `DesintegrRules.ErodeFraction`.
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        // Noise cells across the sprite's shorter side. Higher = finer crumb. The oracle erodes in
        // individual pixels; ~40 reads as the same granularity on a 55x70 unit without shimmering.
        _DissolveCells ("Dissolve cells", Float) = 40
        // Width of the burning band at the erosion front, in noise units.
        _EdgeWidth ("Edge width", Range(0.01, 0.5)) = 0.08

        // ── The glow ─────────────────────────────────────────────────────────────────
        _GlowColor ("Glow colour", Color) = (1, 1, 1, 1)
        _GlowAlpha ("Glow alpha", Range(0, 1)) = 0
        // Blur radius in UV, from `DesintegrRules.GlowBlurPixels` and the sprite's texel size.
        _GlowRadiusUV ("Glow radius (UV)", Float) = 0.03
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
            "CanUseSpriteAtlas" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_TexelSize;
            half4  _Color;
            float  _MulR, _MulG, _MulB;
            float  _AddR, _AddG, _AddB;
            float  _Dissolve;
            float  _DissolveCells;
            float  _EdgeWidth;
            half4  _GlowColor;
            float  _GlowAlpha;
            float  _GlowRadiusUV;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
            float4 color      : COLOR;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
            float4 color      : COLOR;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            output.color = input.color * _Color;
            return output;
        }

        // A cheap 2D hash. Two properties matter and both are load-bearing:
        //   * it must be a function of the SPRITE's uv, so the eroded blocks stay put while the threshold
        //     climbs — a screen-space or time-varying noise would make the body flicker instead of erode;
        //   * it must be quantised, so the blocks read as pixels rather than as film grain.
        float Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        half SampleAlpha(float2 uv)
        {
            return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a;
        }

        half4 Frag(Varyings input) : SV_Target
        {
            half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
            half4 col = tex * input.color;

            // ── The glow, computed from the UNMODIFIED alpha ─────────────────────────────────────
            //
            // It has to be sampled before the erosion clips anything, or the halo would disappear in
            // the same step as the pixel it surrounds. Four axis taps, maxed — the oracle's GlowFilter
            // is a 3px Gaussian; a max over the same radius gives the same silhouette outline for a
            // quarter of the taps, and a blur would only soften it.
            half rim = 0;
            if (_GlowAlpha > 0)
            {
                float2 r = float2(_GlowRadiusUV, _GlowRadiusUV);
                half neighbour = 0;
                neighbour = max(neighbour, SampleAlpha(input.uv + float2( r.x, 0)));
                neighbour = max(neighbour, SampleAlpha(input.uv + float2(-r.x, 0)));
                neighbour = max(neighbour, SampleAlpha(input.uv + float2(0,  r.y)));
                neighbour = max(neighbour, SampleAlpha(input.uv + float2(0, -r.y)));
                rim = saturate(neighbour - col.a) * _GlowAlpha;
            }

            // ── The erosion ──────────────────────────────────────────────────────────────────────
            float2 cell = floor(input.uv * _DissolveCells);
            float noise = Hash21(cell);

            // 0 at _Dissolve == 0, so an untouched sprite pays one compare and keeps every pixel.
            if (_Dissolve > 0)
            {
                clip(noise - _Dissolve);

                // The burning front: the surviving pixels within _EdgeWidth of the threshold.
                half edge = saturate(1.0 - (noise - _Dissolve) / _EdgeWidth);
                col.rgb += _GlowColor.rgb * (_GlowAlpha * edge);
            }

            // ── The colour transform ─────────────────────────────────────────────────────────────
            //
            // After the erosion so the added offset cannot resurrect a clipped pixel, and before the
            // alpha is rebuilt so the halo keeps the tip's own colour rather than the body's.
            col.rgb = col.rgb * float3(_MulR, _MulG, _MulB) + float3(_AddR, _AddG, _AddB);

            // The halo exists outside the silhouette, where `col.a` is 0 — that is the whole point of a
            // glow, so the alpha is the max of the two rather than the sprite's own.
            col.a = max(col.a, rim);
            col.rgb += _GlowColor.rgb * rim * 0.6;

            return col;
        }
        ENDHLSL

        // URP's 2D renderer only draws passes tagged Universal2D; this project uses Renderer2D.
        Pass
        {
            Name "UnitDissolve2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        // Same body for a 3D renderer, so the shader is not silently invisible if the pipeline asset is
        // ever swapped. Both passes share the HLSLINCLUDE above, so they cannot drift.
        Pass
        {
            Name "UnitDissolveForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }

    Fallback Off
}

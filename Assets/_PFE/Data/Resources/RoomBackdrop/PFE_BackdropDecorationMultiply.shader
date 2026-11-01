// Room backdrop DECORATIONS (the graffiti / plants / pipes painted on the wall), darkened by the room's
// darkness and by the baked tile shadow map — as a shader, instead of a composited texture per placement.
//
// This is the second half of S2. `PFE_BackdropMultiply` took the wall fill off the main thread; this takes
// `CreateDecorationCompositeSprite`, which was the same per-pixel composite over every placement's own
// pixels: `backdrop.decorations` was 154.8 ms of a 776.2 ms `backdrop.createVisuals` (doc 09 §16.4
// prediction 3). It also allocated one `Texture2D` + one `Sprite` per placement, which is the managed churn
// the plan budgets at < 2 MB per transition.
//
// Three things are deliberately NOT in here:
//   * **Flip.** The CPU path mirrors the pixels *inside* the sprite rect and keeps the pivot fraction, which
//     is not the same operation as `SpriteRenderer.flipX` (that mirrors around the pivot). Rather than
//     re-derive it per fragment, a flipped entry keeps the CPU path — see
//     `RoomBackdropRenderer.ShouldUseDecorationShader`. No entry in `RoomBackgroundLookup.asset` sets
//     `flipX`/`flipY` today (511 entries, 0 flips), so this costs nothing and stays correct if one appears.
//   * **The tint.** It arrives as the sprite mesh's vertex colour from `SpriteRenderer.color` — the same
//     call the CPU path makes, so its colour-space handling is identical by construction.
//   * **Lighting.** No Light2D anywhere in this project; the darkness is baked, not lit.
//
// The texture this samples is NOT the authored source. It is `GetReadableTexture(source)` — the
// sRGB-flagged copy that holds *linear* numbers, which is what the shipped compositor displays (doc 09
// §4.2, §16.1). `RoomBackdropRenderer` creates a `Sprite` over that copy at the source sprite's own rect,
// so `_MainTex` binds it and the sprite's UVs are unchanged. Point this at the authored texture instead
// and every decoration in the game gets brighter — the same one-line change the wall shader warns about.
//
// The composite below is a transcription of `BackdropCompositeMath` (shadow sample + `LinearDarken`),
// exactly as `PFE_BackdropMultiply` is. Both files must stay in step with that reference; the fixtures in
// `BackdropShaderContractTests` pin the uniform names and the `pow(darken, 2.2)` that §16.9 cost a build.
Shader "PFE/BackdropDecorationMultiply"
{
    Properties
    {
        // The sprite's own texture, bound by `SpriteRenderer` from the sprite. Unlike the wall shader this
        // one *does* read it: the decoration is a picture, not a tiling fill.
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}

        _ShadowTex ("Shadow alpha (in .a)", 2D) = "black" {}

        // Room content rect, in top-down content pixels (y grows down from the room's top edge).
        _ContentOrigin ("Content origin (px)", Vector) = (0, 0, 0, 0)
        _ContentSize ("Content size (px)", Vector) = (1, 1, 0, 0)
        _PixelsPerUnit ("Pixels per world unit", Float) = 100

        _Darkness ("Darkness alpha", Range(0, 1)) = 0.6667

        // Shadow map: texels per content pixel, and its size in texels.
        _ShadowScale ("Shadow texels per content px", Float) = 0.25
        _ShadowSize ("Shadow size (texels)", Vector) = (1, 1, 0, 0)
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

        // Same blend and same `rgb *= a` as `Sprites/Default`, which is what the CPU path's composite
        // sprite is drawn with. Matching it is what keeps an alpha-cut decoration (a plant, a pipe) from
        // gaining a dark fringe: the tint and the darkness are multiplied in before the premultiply, so
        // they cannot leak into transparent texels.
        Blend One OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        TEXTURE2D(_ShadowTex);
        SAMPLER(sampler_ShadowTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _ContentOrigin;
            float4 _ContentSize;
            float4 _ShadowSize;
            float _PixelsPerUnit;
            float _Darkness;
            float _ShadowScale;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float4 color : COLOR;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 world : TEXCOORD0;
            float4 color : COLOR;
            float2 uv : TEXCOORD1;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = positions.positionCS;
            output.world = positions.positionWS.xy;
            output.color = input.color;
            output.uv = input.uv;
            return output;
        }

        half4 Frag(Varyings input) : SV_Target
        {
            // World -> top-down content pixels, the inverse of `GetAnchoredPosition`. World y grows up and
            // content y grows down from the room's top edge, which is why the second line subtracts.
            float px = input.world.x * _PixelsPerUnit - _ContentOrigin.x;
            float py = _ContentOrigin.y + _ContentSize.y - input.world.y * _PixelsPerUnit;

            // Shadow map, bilinear, 0 outside the content rect — `BackdropCompositeMath.ShadowAlphaAt`.
            // The bounds test is explicit because the CPU reference returns 0 outside rather than
            // repeating the border, and `TextureWrapMode.Clamp` would smear the edge texel instead.
            float shadow = 0.0;
            if (px >= 0.0 && py >= 0.0 && px < _ShadowSize.x / _ShadowScale && py < _ShadowSize.y / _ShadowScale)
            {
                float2 shadowCoord = (float2(px, py) + 0.5) * _ShadowScale - 0.5;
                float2 shadowUV = (shadowCoord + 0.5) / _ShadowSize.xy;
                shadow = SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, shadowUV).a;
            }

            half4 colour = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

            // The composite, from `ApplyBackdropComposite` — but NOT applied the obvious way. The CPU path
            // multiplies `(1 - DarknessAlpha) * (1 - shadowAlpha)` into the *stored bytes* and the sprite
            // shader then decodes them, so its display value is `byte * tint` where `byte = art^2.2 *
            // darken`. This fragment runs in linear space, after the decode, where the same result needs
            // `darken^2.2`. Multiplying by `darken` here instead is off by `darken^(1/2.2 - 1)` — 1.8x too
            // bright at the shipped darkness of 170/255. That was the wall shader's first build (doc 09
            // §16.9); the exponent is repeated here rather than re-derived.
            float darken = (1.0 - _Darkness) * (1.0 - shadow);
            colour.rgb *= pow(darken, 2.2) * input.color.rgb;
            colour.a *= input.color.a;
            colour.rgb *= colour.a;
            return colour;
        }
        ENDHLSL

        Pass
        {
            Name "BackdropDecoration2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "BackdropDecorationForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }

    Fallback Off
}

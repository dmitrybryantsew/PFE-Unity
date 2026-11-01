// The room backdrop: a tiling bitmap fill, darkened by the room's darkness and by the baked tile
// shadow map — as a shader, instead of a CPU-composited texture per fill rect.
//
// This replaces the per-pixel loop in `RoomBackdropRenderer.CreateBackdropSprite`, which for a 48x25
// room walked 1.92M pixels on the main thread (536.9 ms of `backdrop.sprites.sample`) to produce a
// bitmap that the GPU then sampled one-to-one. The arithmetic is the same arithmetic —
// `BackdropCompositeMath` is the reference implementation and its fixtures are the specification — it
// just runs per fragment instead of per pixel on one core.
//
// Three things are deliberately NOT in here:
//   * The sharpen pass. It is a 5-tap convolution over the composited image; the CPU path keeps it, and
//     `RoomBackdropRenderer` falls back to the CPU whenever a sharpen strength is configured. This
//     shader is the no-sharpen case, which is the shipped configuration.
//   * The tint. It arrives as the sprite mesh's vertex colour, set by `SpriteRenderer.color` — the same
//     component and the same call the CPU path uses, so the tint's colour-space handling is identical
//     by construction rather than by argument.
//   * Lighting. There is no Light2D anywhere in this project (checked: no component, no prefab), so a
//     sprite here is drawn by the 2D renderer's unlit path; the darkness is baked, not lit.
//
// The texture this samples is NOT the authored source. It is `GetReadableTexture(source)` — the
// sRGB-flagged copy that holds *linear* numbers, which is what the shipped compositor displays (doc 09
// §4.2, §16.1). Sampling it reproduces the shipped picture exactly, gamma round trip included. Point
// this at the source texture instead and every room in the game gets brighter.
Shader "PFE/BackdropMultiply"
{
    Properties
    {
        // `SpriteRenderer` binds the sprite's texture here. The quad path never reads it — the fill is
        // derived from world position — but the property must exist for the binding to be legal.
        [PerRendererData] _MainTex ("Sprite (unused)", 2D) = "white" {}

        _BackdropTex ("Backdrop texture", 2D) = "white" {}
        _ShadowTex ("Shadow alpha (in .a)", 2D) = "black" {}

        // Room content rect, in top-down content pixels (y grows down from the room's top edge).
        _ContentOrigin ("Content origin (px)", Vector) = (0, 0, 0, 0)
        _ContentSize ("Content size (px)", Vector) = (1, 1, 0, 0)
        _PixelsPerUnit ("Pixels per world unit", Float) = 100

        // The tiling fill: content pixels per source texel, plus an offset as a fraction of the texture.
        _TexScale ("Content px per texel", Vector) = (1, 1, 0, 0)
        _TexOffset ("Texel offset (fraction of texture)", Vector) = (0, 0, 0, 0)
        _Flip ("Flip (x, y)", Vector) = (0, 0, 0, 0)

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

        // `Sprites/Default` and `Sprite-Lit-Default` both blend premultiplied and both do `rgb *= a`
        // in the fragment; this pass mirrors them. The backdrop art is JPEG, so alpha is 1 everywhere
        // and the choice cannot be observed — but matching the reference costs nothing and stops the
        // question from being asked again the first time a PNG wall is used.
        Blend One OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BackdropTex);
        SAMPLER(sampler_BackdropTex);
        TEXTURE2D(_ShadowTex);
        SAMPLER(sampler_ShadowTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _BackdropTex_TexelSize;
            float4 _ContentOrigin;
            float4 _ContentSize;
            float4 _TexScale;
            float4 _TexOffset;
            float4 _Flip;
            float4 _ShadowSize;
            float _PixelsPerUnit;
            float _Darkness;
            float _ShadowScale;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float4 color : COLOR;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 world : TEXCOORD0;
            float4 color : COLOR;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = positions.positionCS;
            output.world = positions.positionWS.xy;
            output.color = input.color;
            return output;
        }

        // Non-negative modulo. HLSL's fmod keeps the dividend's sign, and the tiling fill goes negative
        // on any room whose content origin is not the world origin.
        float PosMod(float value, float modulo)
        {
            float r = fmod(value, modulo);
            return r < 0.0 ? r + modulo : r;
        }

        half4 Frag(Varyings input) : SV_Target
        {
            // `_BackdropTex_TexelSize` is (1/w, 1/h, w, h) in Unity.
            float texW = _BackdropTex_TexelSize.z;
            float texH = _BackdropTex_TexelSize.w;

            // World -> top-down content pixels. World y grows up and content y grows down from the
            // room's top edge, which is why the second line subtracts. This is the inverse of
            // `GetBackdropSegmentPosition`, which is what places the quad.
            float px = input.world.x * _PixelsPerUnit - _ContentOrigin.x;
            float py = _ContentOrigin.y + _ContentSize.y - input.world.y * _PixelsPerUnit;

            // Tiling fill: divide, offset, flip, wrap — the same order as
            // BackdropCompositeMath.TilingSample, including the truncation. round() here would shift
            // the sample by one texel on every negative coordinate.
            float scaledX = floor(px / _TexScale.x + _TexOffset.x * texW);
            float scaledY = floor(py / _TexScale.y + _TexOffset.y * texH);

            if (_Flip.x > 0.5) scaledX = texW - 1.0 - scaledX;
            if (_Flip.y > 0.5) scaledY = texH - 1.0 - scaledY;

            // Sample at the texel centre: a bilinear sampler asked exactly for a centre returns that
            // texel, so the asset's own filter mode cannot blur the result.
            float2 uv = (float2(PosMod(scaledX, texW), PosMod(scaledY, texH)) + 0.5) * _BackdropTex_TexelSize.xy;
            half4 colour = SAMPLE_TEXTURE2D(_BackdropTex, sampler_BackdropTex, uv);

            // Shadow map, bilinear, 0 outside the content rect.
            //
            // The bounds test is explicit because the CPU reference returns 0 outside rather than
            // repeating the border (`BackdropCompositeMath.ShadowAlphaAt`), and `TextureWrapMode.Clamp`
            // would happily smear the edge texel instead.
            //
            // `_ShadowTex` is uploaded with array row 0 = content row 0, i.e. top-down, so v is not
            // flipped. The CPU's bilinear coordinate is
            //     s = clamp((p + 0.5) * scale - 0.5, 0, size - 1)
            // and the texel centre of top-down row s is at v = (s + 0.5) / size, which is what this is.
            // The clamp is left to the sampler.
            float shadow = 0.0;
            if (px >= 0.0 && py >= 0.0 && px < _ShadowSize.x / _ShadowScale && py < _ShadowSize.y / _ShadowScale)
            {
                float2 shadowCoord = (float2(px, py) + 0.5) * _ShadowScale - 0.5;
                float2 shadowUV = (shadowCoord + 0.5) / _ShadowSize.xy;
                shadow = SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, shadowUV).a;
            }

            // The composite, from `ApplyBackdropComposite` — but NOT applied the obvious way.
            //
            // The CPU path multiplies `(1 - DarknessAlpha) * (1 - shadowAlpha)` into the *stored bytes*
            // of the composed texture, and the sprite shader then decodes those bytes. So its display
            // value is
            //     byte * tint^(1/2.2)   where byte = art^2.2 * darken
            // This fragment runs in linear space, i.e. *after* the decode, where the same result is
            //     (c * darken^2.2)^(1/2.2) = c^(1/2.2) * darken = byte * darken
            // hence the exponent. Multiplying by `darken` here instead (the obvious transcription) is
            // off by `darken^(1/2.2 - 1)`: the display multiplier becomes 0.607 where it must be 0.333
            // at the shipped darkness of 170/255, i.e. **1.8x too bright**, and it weakens every shadow
            // by the same factor. That was the first version of this shader, and it is what "the
            // backdrop is too bright" looked like.
            //
            // `tint` is deliberately NOT raised to the power: the CPU path applies it in linear space
            // too (`renderer.color` is a vertex colour), so it is already in the right space.
            float darken = (1.0 - _Darkness) * (1.0 - shadow);
            colour.rgb *= pow(darken, 2.2) * input.color.rgb;
            colour.a *= input.color.a;
            colour.rgb *= colour.a;
            return colour;
        }
        ENDHLSL

        Pass
        {
            Name "Backdrop2D"
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "BackdropForward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }

    Fallback Off
}

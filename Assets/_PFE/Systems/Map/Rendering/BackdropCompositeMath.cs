using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The backdrop composite's arithmetic, as functions rather than as a loop body — because the same
    /// arithmetic now exists twice: once in C# (the fallback path) and once in
    /// <c>PFE_BackdropMultiply.shader</c> (the default path).
    ///
    /// <para><b>Why this is extracted.</b> A shader cannot be unit-tested in this project — there is no
    /// offline HLSL runner and no GPU in the harness. What <i>can</i> be tested is the specification the
    /// shader is a transcription of, and only if that specification exists as code. So the CPU path calls
    /// these functions and the fixtures pin them; the shader is then checked by reading it against a
    /// tested reference instead of against an intention.</para>
    ///
    /// <para><b>Two coordinate systems, and they are not the same one.</b> The backdrop is authored
    /// top-down in <i>content pixels</i> (y grows down from the room's top-left content corner), while
    /// Unity world y grows up. <see cref="TilingSample.Texel"/> takes content pixels; converting world to
    /// content pixels is the caller's job and is done once per fragment in the shader.</para>
    /// </summary>
    public static class BackdropCompositeMath
    {
        /// <summary>
        /// The backdrop wall is a tiling bitmap fill: a content pixel maps to a source texel by dividing
        /// by <see cref="Scale"/>, adding <see cref="Offset"/>, flipping, and wrapping.
        /// </summary>
        public readonly struct TilingSample
        {
            /// <summary>Source texture size in texels.</summary>
            public readonly int TexWidth;
            public readonly int TexHeight;

            /// <summary>Content pixels per source texel, per axis. <c>1</c> is one texel per pixel.</summary>
            public readonly Vector2 Scale;

            /// <summary>Offset in source texels, expressed as a fraction of the texture (0..1 scaled by size).</summary>
            public readonly Vector2 Offset;

            public readonly bool FlipX;
            public readonly bool FlipY;

            public TilingSample(int texWidth, int texHeight, Vector2 scale, Vector2 offset, bool flipX, bool flipY)
            {
                TexWidth = texWidth;
                TexHeight = texHeight;

                // A zero scale would divide by zero per pixel; the caller is expected to have validated it,
                // and clamping here keeps a bad room from turning into a black screen.
                Scale = new Vector2(scale.x == 0f ? 1f : scale.x, scale.y == 0f ? 1f : scale.y);
                Offset = offset;
                FlipX = flipX;
                FlipY = flipY;
            }

            /// <summary>
            /// The source texel for a content pixel. Truncating rather than rounding is deliberate and is
            /// the original's behaviour: <c>FloorToInt</c> of a negative quotient is not <c>Round</c>, and
            /// the difference is a one-texel shift on every other row.
            /// </summary>
            public void Texel(float contentX, float contentY, out int sourceX, out int sourceY)
            {
                // Keep the division exactly as it was. (a / s) and (a * (1/s)) are not bit-identical, and
                // a 1-ULP difference flips FloorToInt for boundary values — which would silently shift the
                // sampled texel by one pixel. The shader divides too, for the same reason.
                int scaledX = Mathf.FloorToInt(contentX / Scale.x + Offset.x * TexWidth);
                int scaledY = Mathf.FloorToInt(contentY / Scale.y + Offset.y * TexHeight);

                if (FlipX) scaledX = TexWidth - 1 - scaledX;
                if (FlipY) scaledY = TexHeight - 1 - scaledY;

                sourceX = PositiveModulo(scaledX, TexWidth);
                sourceY = PositiveModulo(scaledY, TexHeight);
            }
        }

        /// <summary>
        /// Bilinear sample of the baked shadow map at a content pixel, or 0 outside it.
        ///
        /// <para><b>The outside case is not "clamp to the edge".</b> The shadow map covers the room's
        /// content rect only, and the original returns 0 outside it rather than repeating the border —
        /// which is what keeps a shadow from smearing into the room's border band. The shader reproduces
        /// this with an explicit bounds test rather than by relying on <c>TextureWrapMode.Clamp</c>.</para>
        /// </summary>
        public static float ShadowAlphaAt(float contentX, float contentY, float[] shadow, Vector2Int size, float resolutionScale)
        {
            if (shadow == null || shadow.Length == 0 || size.x <= 0 || size.y <= 0 || resolutionScale <= 0f)
            {
                return 0f;
            }

            if (contentX < 0f || contentY < 0f || contentX >= size.x / resolutionScale || contentY >= size.y / resolutionScale)
            {
                return 0f;
            }

            float scaledX = Mathf.Clamp((contentX + 0.5f) * resolutionScale - 0.5f, 0f, size.x - 1f);
            float scaledY = Mathf.Clamp((contentY + 0.5f) * resolutionScale - 0.5f, 0f, size.y - 1f);

            int x0 = Mathf.FloorToInt(scaledX);
            int y0 = Mathf.FloorToInt(scaledY);
            int x1 = Mathf.Min(x0 + 1, size.x - 1);
            int y1 = Mathf.Min(y0 + 1, size.y - 1);

            float tx = scaledX - x0;
            float ty = scaledY - y0;

            float a = shadow[y0 * size.x + x0];
            float b = shadow[y0 * size.x + x1];
            float c = shadow[y1 * size.x + x0];
            float d = shadow[y1 * size.x + x1];

            float top = Mathf.Lerp(a, b, tx);
            float bottom = Mathf.Lerp(c, d, tx);
            return Mathf.Lerp(top, bottom, ty);
        }

        /// <summary>Modulo that stays non-negative for negative input — the tiling wrap.</summary>
        public static int PositiveModulo(int value, int modulo)
        {
            if (modulo <= 0) return 0;

            int remainder = value % modulo;
            return remainder < 0 ? remainder + modulo : remainder;
        }

        /// <summary>
        /// The room's darkness as an alpha: <c>(BackdropBaseDarkness + darkness) / 255</c>, clamped.
        /// Note that this is the <i>alpha</i>, not the multiplier — the composite uses <c>1 - alpha</c>,
        /// and the shader's <c>_Darkness</c> uniform carries this value, not the multiplier.
        ///
        /// <para>Kept here so the uniform and the CPU path cannot disagree about the clamp.</para>
        /// </summary>
        public static float DarknessAlpha(int baseDarkness, int roomDarkness)
        {
            int darkness = Mathf.Clamp(baseDarkness + roomDarkness, 0, 255);
            return darkness / 255f;
        }

        // ------------------------------------------------------------------ colour space

        /// <summary>
        /// The exponent this project's compositor has always behaved as. It is a model of Unity's sRGB
        /// transfer function, not the function itself — good to well under a percent over the range the
        /// backdrop uses, and the same model doc 09 §4.2 uses to describe the gamma round trip.
        /// </summary>
        public const float Gamma = 2.2f;

        /// <summary>
        /// The displayed value the **CPU** compositor produces for one channel: the darkness multiplies
        /// the composed texture's *stored byte*, and nothing re-encodes it afterwards.
        /// </summary>
        public static float DisplayUnderCpu(float storedByte, float darken)
        {
            return storedByte * darken;
        }

        /// <summary>
        /// The displayed value a **shader** produces from the same stored byte: sampling an sRGB-flagged
        /// texture decodes it, the multiply happens in linear space, and the sRGB backbuffer encodes the
        /// result.
        /// </summary>
        public static float DisplayUnderShader(float storedByte, float linearMultiplier)
        {
            return Mathf.Pow(Mathf.Pow(storedByte, Gamma) * linearMultiplier, 1f / Gamma);
        }

        /// <summary>
        /// The linear-space multiplier a shader must use so that its displayed value equals the CPU's
        /// byte-space multiply. This is the <c>pow(darken, 2.2)</c> in
        /// <c>PFE_BackdropMultiply.shader</c>, and it exists because multiplying by <paramref name="darken"/>
        /// directly is a different operation in a different space — brighter by
        /// <c>darken^(1/2.2 - 1)</c>, which at the shipped darkness of 170/255 is <b>1.8x</b>.
        /// </summary>
        public static float LinearDarken(float darken)
        {
            return Mathf.Pow(darken, Gamma);
        }
    }
}

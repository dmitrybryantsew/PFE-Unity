using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map.Rendering
{
    /// <summary>
    /// Pins the backdrop composite's arithmetic — the specification <c>PFE_BackdropMultiply.shader</c> is a
    /// transcription of.
    ///
    /// <para><b>Why this file exists at all.</b> There is no GPU and no HLSL runner in this harness, so the
    /// shader cannot be executed here and a claim that it "does the same thing" is unfalsifiable on its own.
    /// What can be executed is the arithmetic, so the arithmetic was extracted into
    /// <see cref="BackdropCompositeMath"/>, the CPU path now calls it, and these fixtures pin it. A
    /// transcription error in the shader is then findable by reading two short expressions side by side
    /// instead of by hoping a screenshot looks right.</para>
    ///
    /// <para><b>What these fixtures do not do.</b> They cannot fail because the shader is wrong. They fail
    /// when the <i>reference</i> is wrong or when someone changes it, which is a weaker guarantee that is
    /// worth stating out loud (doc 09 §16.5).</para>
    /// </summary>
    public sealed class BackdropCompositeMathTests
    {
        // ---------------------------------------------------------------- tiling fill

        [Test]
        public void Texel_AtScaleOneWithNoOffset_IsTheContentPixel()
        {
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.one, Vector2.zero, false, false);

            tiling.Texel(0f, 0f, out int x, out int y);
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);

            tiling.Texel(5f, 3f, out x, out y);
            Assert.AreEqual(5, x);
            Assert.AreEqual(3, y);
        }

        [Test]
        public void Texel_DividesByScale_AndTruncates()
        {
            // Scale 2 means two content pixels per texel: 4 -> 2, and 3 -> floor(1.5) = 1.
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, new Vector2(2f, 2f), Vector2.zero, false, false);

            tiling.Texel(4f, 4f, out int x, out int y);
            Assert.AreEqual(2, x);
            Assert.AreEqual(2, y);

            tiling.Texel(3f, 3f, out x, out y);
            Assert.AreEqual(1, x, "floor, not round — 3/2 is 1.5");
            Assert.AreEqual(1, y);
        }

        [Test]
        public void Texel_FloorsTowardsNegativeInfinity_SoANegativePixelWrapsToTheLastTexel()
        {
            // The distinction the shader comment warns about: floor(-1) is -1, which wraps to 7; a
            // truncation towards zero would give 0 and shift the whole fill by one texel per tile.
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.one, Vector2.zero, false, false);

            tiling.Texel(-1f, -1f, out int x, out int y);
            Assert.AreEqual(7, x, "floor(-1) = -1, wrapped into 0..7");
            Assert.AreEqual(7, y);

            tiling.Texel(-0.5f, -0.5f, out x, out y);
            Assert.AreEqual(7, x, "floor(-0.5) = -1 too");
            Assert.AreEqual(7, y);
        }

        [Test]
        public void Texel_FlipsBeforeWrapping()
        {
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.one, Vector2.zero, true, true);

            tiling.Texel(0f, 0f, out int x, out int y);
            Assert.AreEqual(7, x, "8 - 1 - 0");
            Assert.AreEqual(7, y);

            tiling.Texel(7f, 7f, out x, out y);
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);

            // The flip is applied to the scaled coordinate *before* the wrap, so a coordinate past the
            // texture edge flips into a negative number and then wraps — 8 - 1 - 8 = -1 -> 7.
            tiling.Texel(8f, 8f, out x, out y);
            Assert.AreEqual(7, x);
            Assert.AreEqual(7, y);
        }

        [Test]
        public void Texel_WrapsPositivelyPastTheTextureSize()
        {
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.one, Vector2.zero, false, false);

            tiling.Texel(8f, 8f, out int x, out int y);
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);

            tiling.Texel(10f, 11f, out x, out y);
            Assert.AreEqual(2, x);
            Assert.AreEqual(3, y);

            tiling.Texel(19f, 19f, out x, out y);
            Assert.AreEqual(3, x, "19 mod 8");
            Assert.AreEqual(3, y);
        }

        [Test]
        public void Texel_AppliesTheOffsetInTexels_NotAsAFractionOfTheRoom()
        {
            // `Offset` is a fraction of the *texture*, so 0.5 of an 8-texel texture is 4 texels.
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.one, new Vector2(0.5f, 0.25f), false, false);

            tiling.Texel(0f, 0f, out int x, out int y);
            Assert.AreEqual(4, x, "0 + 0.5 * 8");
            Assert.AreEqual(2, y, "0 + 0.25 * 8");
        }

        [Test]
        public void Texel_WithAZeroScale_DoesNotDivideByZero()
        {
            // The constructor clamps a zero scale to 1. A room with a broken backdrop scale should draw a
            // wrong-sized fill, not produce NaN coordinates and a black screen.
            var tiling = new BackdropCompositeMath.TilingSample(8, 8, Vector2.zero, Vector2.zero, false, false);

            tiling.Texel(3f, 3f, out int x, out int y);
            Assert.AreEqual(3, x);
            Assert.AreEqual(3, y);
        }

        // ---------------------------------------------------------------- shadow map

        [Test]
        public void ShadowAlphaAt_IsZeroOutsideTheContentRect()
        {
            float[] shadow = { 1f, 1f, 1f, 1f };

            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(-0.01f, 0.5f, shadow, new Vector2Int(2, 2), 1f),
                "left of the rect");
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(0.5f, -0.01f, shadow, new Vector2Int(2, 2), 1f),
                "above the rect");
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(2f, 0.5f, shadow, new Vector2Int(2, 2), 1f),
                "right edge is exclusive");
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(0.5f, 2f, shadow, new Vector2Int(2, 2), 1f),
                "bottom edge is exclusive");

            Assert.AreEqual(1f, BackdropCompositeMath.ShadowAlphaAt(0f, 0f, shadow, new Vector2Int(2, 2), 1f),
                "the top-left corner is inside");
        }

        [Test]
        public void ShadowAlphaAt_InterpolatesBilinearly()
        {
            // At resolutionScale 1 the texel coordinate is the content coordinate, so this is readable:
            //   row 0 (y=0): 0 1        row 1 (y=1): 0 1
            float[] shadow = { 0f, 1f, 0f, 1f };
            var size = new Vector2Int(2, 2);

            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(0f, 0f, shadow, size, 1f), 1e-6f);
            Assert.AreEqual(1f, BackdropCompositeMath.ShadowAlphaAt(1f, 0f, shadow, size, 1f), 1e-6f);
            Assert.AreEqual(0.5f, BackdropCompositeMath.ShadowAlphaAt(0.5f, 0f, shadow, size, 1f), 1e-6f,
                "halfway between the two columns");
            Assert.AreEqual(0.25f, BackdropCompositeMath.ShadowAlphaAt(0.25f, 0f, shadow, size, 1f), 1e-6f);
            Assert.AreEqual(0.5f, BackdropCompositeMath.ShadowAlphaAt(0.5f, 0.5f, shadow, size, 1f), 1e-6f,
                "same in x and y");
        }

        [Test]
        public void ShadowAlphaAt_ClampsInsideTheEdgeBandInsteadOfReadingPastTheArray()
        {
            // The CPU reference clamps the texel coordinate to size-1, so the last half-texel of the rect
            // repeats the edge texel rather than indexing out of bounds. The shader relies on
            // TextureWrapMode.Clamp for the same thing.
            float[] shadow = { 0f, 1f, 0f, 1f };
            var size = new Vector2Int(2, 2);

            Assert.AreEqual(1f, BackdropCompositeMath.ShadowAlphaAt(1.9f, 0f, shadow, size, 1f), 1e-6f);
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(0f, 1.9f, shadow, size, 1f), 1e-6f);
        }

        [Test]
        public void ShadowAlphaAt_WithResolutionScale_ShrinksTheRectInContentPixels()
        {
            // ShadowResolutionScale is 0.25 in the renderer, so a 2x2 map covers 8x8 content pixels.
            float[] shadow = { 0f, 1f, 0f, 1f };
            var size = new Vector2Int(2, 2);

            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(8f, 0f, shadow, size, 0.25f),
                "8 content px is the right edge of a 2-texel map at 0.25");
            Assert.AreEqual(1f, BackdropCompositeMath.ShadowAlphaAt(7.99f, 0f, shadow, size, 0.25f), 1e-6f);
        }

        [Test]
        public void ShadowAlphaAt_WithNoMap_IsZeroEverywhere()
        {
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(1f, 1f, null, new Vector2Int(2, 2), 1f));
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(1f, 1f, new float[0], new Vector2Int(2, 2), 1f));
            Assert.AreEqual(0f, BackdropCompositeMath.ShadowAlphaAt(1f, 1f, new[] { 1f }, new Vector2Int(0, 0), 1f));
        }

        // ---------------------------------------------------------------- wrap + darkness

        [Test]
        public void PositiveModulo_StaysNonNegative()
        {
            Assert.AreEqual(7, BackdropCompositeMath.PositiveModulo(-1, 8));
            Assert.AreEqual(7, BackdropCompositeMath.PositiveModulo(-9, 8));
            Assert.AreEqual(0, BackdropCompositeMath.PositiveModulo(0, 8));
            Assert.AreEqual(0, BackdropCompositeMath.PositiveModulo(8, 8));
            Assert.AreEqual(2, BackdropCompositeMath.PositiveModulo(10, 8));
            Assert.AreEqual(0, BackdropCompositeMath.PositiveModulo(5, 0), "a zero modulus is a caller bug, not a crash");
        }

        [Test]
        public void DarknessAlpha_ClampsTheSumToAFullByte()
        {
            Assert.AreEqual(170f / 255f, BackdropCompositeMath.DarknessAlpha(170, 0), 1e-6f);
            Assert.AreEqual(1f, BackdropCompositeMath.DarknessAlpha(250, 20), 1e-6f, "clamped at 255");
            Assert.AreEqual(0f, BackdropCompositeMath.DarknessAlpha(0, -100), 1e-6f, "clamped at 0");
        }

        [Test]
        public void DarknessAlpha_IsTheAlpha_NotTheMultiplier()
        {
            // The shader's `_Darkness` uniform carries this value and applies `1 - _Darkness`. Pinning the
            // convention here is the cheapest way to stop the two paths from inverting it independently —
            // the shipped base darkness of 170 must darken, not brighten.
            float alpha = BackdropCompositeMath.DarknessAlpha(170, 0);

            Assert.AreEqual(0.666667f, alpha, 1e-5f);
            Assert.AreEqual(0.333333f, 1f - alpha, 1e-5f);
            Assert.Less(1f - alpha, 1f, "the room must get darker, not lighter");
        }

        // ---------------------------------------------------------------- colour space

        [Test]
        public void LinearDarken_MakesTheShaderMatchTheCpuByteSpaceMultiply()
        {
            // The load-bearing relationship. The CPU compositor multiplies the darkness into the composed
            // texture's *stored byte*; a shader samples that byte through an sRGB decode, multiplies, and
            // is encoded again on the way out. Those are different operations unless the shader's
            // multiplier is raised to the gamma first. If this fails, `pow(darken, 2.2)` in
            // PFE_BackdropMultiply.shader has been "simplified" back to `darken` and every room in the
            // game is ~1.8x too bright.
            float[] storedBytes = { 0.05f, 0.2f, 0.5f, 0.9f };
            float[] darkens = { 0.1f, 0.333f, 0.5f, 0.75f, 1f };

            foreach (float stored in storedBytes)
            {
                foreach (float darken in darkens)
                {
                    float cpu = BackdropCompositeMath.DisplayUnderCpu(stored, darken);
                    float shader = BackdropCompositeMath.DisplayUnderShader(
                        stored,
                        BackdropCompositeMath.LinearDarken(darken));

                    Assert.AreEqual(cpu, shader, 1e-5f,
                        $"stored {stored} darken {darken}: the shader must land on the CPU's value");
                }
            }
        }

        [Test]
        public void MultiplyingInLinearSpaceInstead_IsBrightByTheGammaRatio()
        {
            // The bug that shipped for one build: the shader used `darken` where it needed
            // `darken^2.2`. This pins *how wrong* that was, so the next reader does not have to
            // rediscover it — at the shipped darkness (1 - 170/255 = 0.333) the room was 1.82x too
            // bright, and the error grows as a room gets darker, which is why it looked like "the
            // backdrop is too bright" rather than like a colour cast.
            const float shippedDarken = 1f - 170f / 255f;

            float cpu = BackdropCompositeMath.DisplayUnderCpu(0.5f, shippedDarken);
            float wrong = BackdropCompositeMath.DisplayUnderShader(0.5f, shippedDarken);

            Assert.AreEqual(1.82f, wrong / cpu, 0.02f, "the measured error was ~1.8x");

            // And the ratio is exactly darken^(1/2.2 - 1) — note the sign: multiplying by `darken` in
            // linear space gives `b * darken^(1/2.2)`, which is *larger* than `b * darken`, so the ratio
            // is above 1 and grows as the room gets darker. (The first version of this assertion had the
            // exponent the other way up and predicted 0.549; the run reported 1.821, which is how the
            // algebra was caught.)
            float predicted = Mathf.Pow(shippedDarken, 1f / BackdropCompositeMath.Gamma - 1f);
            Assert.AreEqual(predicted, wrong / cpu, 1e-4f);
            Assert.Greater(predicted, 1f, "the wrong version must be brighter, not darker");
        }

        [Test]
        public void LinearDarken_IsIdentityWhenNothingDarkens()
        {
            Assert.AreEqual(1f, BackdropCompositeMath.LinearDarken(1f), 1e-6f,
                "an undarkened, unshadowed fragment must be untouched");
            Assert.AreEqual(0f, BackdropCompositeMath.LinearDarken(0f), 1e-6f);
        }
    }
}

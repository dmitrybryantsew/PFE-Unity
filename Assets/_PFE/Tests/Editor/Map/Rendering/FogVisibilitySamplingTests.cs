using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the fog <b>read-back</b> — the lookup a world-anchored marker uses to decide how much of
    /// itself the fog is allowed to cover.
    ///
    /// <para><b>The behaviour.</b> AS3 adds a unit's <c>hpBar</c> to <c>Grafon.visObjs[3]</c>
    /// (<c>Unit.as:2896-2899</c>) and <c>Grafon</c> adds that layer <i>before</i> <c>visLight</c>
    /// (<c>Grafon.as:193-200</c>), so the bar is composited under the fog. The port draws the starred
    /// enemy's star and name on the HUD canvas instead, above every world layer, and it stayed at full
    /// brightness in an unlit tile. <see cref="FogVisibilitySampling"/> supplies the two numbers that
    /// close the gap: the mask value at the marker's tile, and the alpha that reproduces the oracle's
    /// composite.</para>
    ///
    /// <para><b>What is worth pinning here, and what is not.</b> The arithmetic is three lines, so the
    /// tests are aimed at the three ways it goes silently wrong: the <b>row order</b> (this buffer is
    /// Y-up, unlike the AS3 lookup that <c>FogOcclusionMath.ResolveRayLookupCoord</c> mirrors), the
    /// <b>half-open edge</b> (a point exactly on the far boundary belongs to no tile), and the
    /// <b>short buffer</b> (a half-built mask must read as "not covered", never as "dark"). Each
    /// "must refuse" case is paired with a control that must succeed, because an <c>Is.False</c>
    /// assertion also passes when the code under test never runs.</para>
    /// </summary>
    [TestFixture]
    public class FogVisibilitySamplingTests
    {
        const float Tolerance = 1e-4f;

        /// <summary>AS3 <c>Tile.tileX</c> — 40 in every shipped room (<c>Tile.as:8</c>).</summary>
        const float Tile = 40f;

        /// <summary>
        /// A 3 x 2 mask whose value is its own index — so a test that reads the wrong cell reports a
        /// number that names the cell it actually read.
        /// </summary>
        static float[] IndexBuffer()
        {
            return new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        }

        // ── the lookup ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void PointInsideTheBottomLeftTile_ReadsIndexZero()
        {
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(20f, 20f), Tile, out float value));
            Assert.AreEqual(0f, value, Tolerance);
        }

        [Test]
        public void RowIndexGrowsUpward_SoPixel39IsTheBottomRowAndPixel41TheOneAbove()
        {
            // The buffer is indexed `y * width + x` with y counting UP from the room's bottom row, which
            // is the order `Texture2D.SetPixels` uploads in and the order the light pass writes in.
            // Reading it as Y-down would flip the marker's fade to the wrong side of the room.
            float[] buffer = IndexBuffer();

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, 3, 2, new Vector2(20f, 39f), Tile, out float bottom));
            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, 3, 2, new Vector2(20f, 41f), Tile, out float above));

            Assert.AreEqual(0f, bottom, Tolerance, "y = 39 px is the bottom row (row 0).");
            Assert.AreEqual(0.3f, above, Tolerance, "y = 41 px is row 1, i.e. index 1 * 3 + 0.");
        }

        [Test]
        public void ColumnIndexGrowsRightward_SoPixel39IsColumnZeroAndPixel41TheNext()
        {
            float[] buffer = IndexBuffer();

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, 3, 2, new Vector2(39f, 20f), Tile, out float left));
            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, 3, 2, new Vector2(41f, 20f), Tile, out float right));

            Assert.AreEqual(0f, left, Tolerance);
            Assert.AreEqual(0.1f, right, Tolerance, "x = 41 px is column 1, i.e. index 0 * 3 + 1.");
        }

        [Test]
        public void TopRightTile_ReadsTheLastIndex()
        {
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(119f, 79f), Tile, out float value));
            Assert.AreEqual(0.5f, value, Tolerance, "Row 1, column 2 is index 1 * 3 + 2.");
        }

        [Test]
        public void FullBrightnessBuffer_ReadsOneEverywhere()
        {
            // The control for the refusal cases below: the same call path, a buffer that is fully lit.
            // Without this, a TrySample that always returned false would still pass every Is.False test.
            var buffer = new[] { 1f, 1f, 1f, 1f, 1f, 1f };

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, 3, 2, new Vector2(60f, 60f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        // ── refusals: "not covered" is not "dark" ────────────────────────────────────────────────

        [Test]
        public void PointLeftOfTheRoom_IsNotCovered()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(-1f, 20f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance, "Outside the mask nothing is drawn over the marker.");
        }

        [Test]
        public void PointBelowTheRoom_IsNotCovered()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(20f, -1f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        [Test]
        public void PointExactlyOnTheFarEdge_IsOutsideTheLastTile()
        {
            // Half-open, matching the tile grid: tile (width-1) spans [80, 120), so 120 is the first
            // pixel of nothing. Clamping it to the last column instead would make a marker on the room
            // boundary inherit that tile's darkness by accident.
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(120f, 20f), Tile, out float pastRight));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(20f, 80f), Tile, out float pastTop));
            Assert.AreEqual(1f, pastRight, Tolerance);
            Assert.AreEqual(1f, pastTop, Tolerance);
        }

        [Test]
        public void LastPixelInsideTheFarEdge_IsStillCovered()
        {
            // The control for the half-open edge: 119.9 px is inside tile column 2.
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(119.9f, 79.9f), Tile, out float value));
            Assert.AreEqual(0.5f, value, Tolerance);
        }

        [Test]
        public void BufferShorterThanTheDeclaredGrid_IsRefused()
        {
            // A half-built mask (the grid grew and the buffer did not) must not be read past its end.
            var shortBuffer = new[] { 0f, 0f, 0f };

            Assert.IsFalse(FogVisibilitySampling.TrySample(shortBuffer, 3, 2, new Vector2(20f, 20f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        [Test]
        public void EmptyInputs_AreRefused()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(null, 3, 2, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 0, 2, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 0, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 2, new Vector2(20f, 20f), 0f, out _));
        }

        // ── the marker's alpha ───────────────────────────────────────────────────────────────────

        [Test]
        public void LitAndDrawn_MarkerIsFullyOpaque()
        {
            Assert.AreEqual(1f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(1f, true), Tolerance);
        }

        [Test]
        public void UnlitButDrawn_MarkerFullyDisappears()
        {
            // AS3's mask texel at visibility 0 is opaque black, so nothing under it survives.
            Assert.AreEqual(0f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(0f, true), Tolerance);
        }

        [Test]
        public void PartlyRevealed_MarkerKeepsExactlyThatFraction()
        {
            // `marker * v + black * (1 - v) == marker * v`, so the alpha IS the visibility. 0.4 is a
            // real intermediate value — the reveal rises in 0.1 steps (`LightRevealRiseSpeed`).
            Assert.AreEqual(0.4f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(0.4f, true), Tolerance);
        }

        [Test]
        public void UndrawnAnchor_IsOpaqueZeroWhateverTheLight()
        {
            // AS3 `Unit.addVisual():2893-2896` hides `hpBar` outright for an `invis` unit, and
            // `visDetails():2983` re-tests `&& !this.invis`. A star over a stealthed enemy would give
            // away exactly what the spell hides.
            Assert.AreEqual(0f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(1f, false), Tolerance);
            Assert.AreEqual(0f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(0.5f, false), Tolerance);
        }

        [Test]
        public void OutOfRangeVisibility_IsClamped()
        {
            Assert.AreEqual(1f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(1.5f, true), Tolerance);
            Assert.AreEqual(0f, FogVisibilitySampling.ResolveWorldAnchoredAlpha(-0.2f, true), Tolerance);
        }
    }
}

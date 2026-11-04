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
    /// <para><b>The buffer layout.</b> The mask texture is one texel wider and taller than the room's
    /// tile grid, and game tile <c>(x, y)</c> lives at texel <c>(x, y + 1)</c> — the AS3
    /// <c>lightBmp</c> alignment whose never-written border cells keep the floor row's bottom half and
    /// the right wall's right half dark (see <c>RoomBackdropRenderer.VisibilityMaskTexelIndex</c>).
    /// The tests below therefore declare a 3 x 2 game grid as a 4 x 3 buffer whose game-tile values
    /// are their own indices, so a read of the wrong cell reports the cell it actually read.</para>
    ///
    /// <para><b>What is worth pinning here, and what is not.</b> The arithmetic is three lines, so the
    /// tests are aimed at the ways it goes silently wrong: the <b>row order</b> (this buffer is
    /// Y-up, unlike the AS3 lookup that <c>FogOcclusionMath.ResolveRayLookupCoord</c> mirrors), the
    /// <b>texel offset</b> (game tile <c>(x, y)</c> is at <c>(y + 1) * width + x</c>, not
    /// <c>y * width + x</c> — forgetting the <c>+ 1</c> reads the tile below), the <b>half-open
    /// edge</b> (a point exactly on the far boundary belongs to no tile), and the <b>short buffer</b>
    /// (a half-built mask must read as "not covered", never as "dark"). Each "must refuse" case is
    /// paired with a control that must succeed, because an <c>Is.False</c> assertion also passes when
    /// the code under test never runs.</para>
    /// </summary>
    [TestFixture]
    public class FogVisibilitySamplingTests
    {
        const float Tolerance = 1e-4f;

        /// <summary>AS3 <c>Tile.tileX</c> — 40 in every shipped room (<c>Tile.as:8</c>).</summary>
        const float Tile = 40f;

        /// <summary>Texture dimensions for a 3 x 2 game grid: one border column and row extra.</summary>
        const int TexWidth = 4;
        const int TexHeight = 3;

        /// <summary>
        /// A 4 x 3 mask buffer (3 x 2 game grid plus the border ring) whose GAME-TILE texel value
        /// identifies its own game index <c>y * 3 + x</c> as <c>(index + 1) / 10</c> — so a test that
        /// reads the wrong cell reports a number that names the cell it actually read, with every
        /// value inside the <c>[0, 1]</c> range the sampler clamps to. The border ring (texel row 0
        /// and column 3) is 0, a value no game tile holds, so reading it is detectable too.
        /// </summary>
        static float[] IndexBuffer()
        {
            // Texel layout (row 0 is the BOTTOM border row; game row y sits at texel row y + 1).
            // Game tile (x, y) holds (y * 3 + x + 1) / 10:
            //   texel row 2 (game y = 1):  0.4  0.5  0.6 | 0
            //   texel row 1 (game y = 0):  0.1  0.2  0.3 | 0
            //   texel row 0 (border):       0    0    0  | 0
            return new[]
            {
                0f, 0f, 0f, 0f,
                0.1f, 0.2f, 0.3f, 0f,
                0.4f, 0.5f, 0.6f, 0f,
            };
        }

        /// <summary>Every game-tile texel fully lit, border ring dark — the all-ones control.</summary>
        static float[] LitBuffer()
        {
            return new[]
            {
                0f, 0f, 0f, 0f,
                1f, 1f, 1f, 0f,
                1f, 1f, 1f, 0f,
            };
        }

        // ── the lookup ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void PointInsideTheBottomLeftTile_ReadsItsOwnIndex()
        {
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(20f, 20f), Tile, out float value));
            Assert.AreEqual(0.1f, value, Tolerance, "Game tile (0, 0) holds (0 + 1) / 10.");
        }

        [Test]
        public void RowIndexGrowsUpward_SoPixel39IsTheBottomRowAndPixel41TheOneAbove()
        {
            // Game tile (x, y) sits at texel (x, y + 1) with y counting UP from the room's bottom
            // row, which is the order `Texture2D.SetPixels` uploads in and the order the light pass
            // writes in. Reading it as Y-down would flip the marker's fade to the wrong side of the
            // room.
            float[] buffer = IndexBuffer();

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, TexWidth, TexHeight, new Vector2(20f, 39f), Tile, out float bottom));
            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, TexWidth, TexHeight, new Vector2(20f, 41f), Tile, out float above));

            Assert.AreEqual(0.1f, bottom, Tolerance, "y = 39 px is the bottom game row (game row 0).");
            Assert.AreEqual(0.4f, above, Tolerance, "y = 41 px is game row 1, game tile (0, 1).");
        }

        [Test]
        public void GameTile_ReadsItsOwnTexel_NotTheTileBelowIt()
        {
            // The `+ 1` row offset pin: game row 0 lives at texel row 1, so a read that forgot the
            // offset would return the border ring's 0 instead of the tile's own value.
            float[] buffer = IndexBuffer();

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, TexWidth, TexHeight, new Vector2(100f, 20f), Tile, out float bottomRight));
            Assert.AreEqual(0.3f, bottomRight, Tolerance,
                "Game tile (2, 0) is texel (2, 1) — index 1 * 4 + 2 = 6, holding 0.3. " +
                "Without the + 1 row offset this read texel (2, 0), the border ring's 0.");
        }

        [Test]
        public void ColumnIndexGrowsRightward_SoPixel39IsColumnZeroAndPixel41TheNext()
        {
            float[] buffer = IndexBuffer();

            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, TexWidth, TexHeight, new Vector2(39f, 20f), Tile, out float left));
            Assert.IsTrue(FogVisibilitySampling.TrySample(buffer, TexWidth, TexHeight, new Vector2(41f, 20f), Tile, out float right));

            Assert.AreEqual(0.1f, left, Tolerance);
            Assert.AreEqual(0.2f, right, Tolerance, "x = 41 px is game column 1.");
        }

        [Test]
        public void TopRightTile_ReadsItsOwnIndex()
        {
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(119f, 79f), Tile, out float value));
            Assert.AreEqual(0.6f, value, Tolerance, "Game tile (2, 1) is texel (2, 2) — index 2 * 4 + 2 = 10, holding 0.6.");
        }

        [Test]
        public void FullBrightnessBuffer_ReadsOneEverywhere()
        {
            // The control for the refusal cases below: the same call path, a buffer that is fully lit.
            // Without this, a TrySample that always returned false would still pass every Is.False test.
            Assert.IsTrue(FogVisibilitySampling.TrySample(LitBuffer(), TexWidth, TexHeight, new Vector2(60f, 60f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        // ── refusals: "not covered" is not "dark" ────────────────────────────────────────────────

        [Test]
        public void PointLeftOfTheRoom_IsNotCovered()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(-1f, 20f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance, "Outside the mask nothing is drawn over the marker.");
        }

        [Test]
        public void PointBelowTheRoom_IsNotCovered()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(20f, -1f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        [Test]
        public void PointExactlyOnTheFarEdge_IsOutsideTheLastTile()
        {
            // Half-open, matching the tile grid: game tile (2, y) spans [80, 120), so 120 is the
            // first pixel of nothing — even though the texture's border column covers those pixels
            // visually, no GAME tile owns them, and a marker there must not inherit a darkness.
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(120f, 20f), Tile, out float pastRight));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(20f, 80f), Tile, out float pastTop));
            Assert.AreEqual(1f, pastRight, Tolerance);
            Assert.AreEqual(1f, pastTop, Tolerance);
        }

        [Test]
        public void LastPixelInsideTheFarEdge_IsStillCovered()
        {
            // The control for the half-open edge: 119.9 px is inside game tile column 2.
            Assert.IsTrue(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(119.9f, 79.9f), Tile, out float value));
            Assert.AreEqual(0.6f, value, Tolerance);
        }

        [Test]
        public void BufferShorterThanTheDeclaredGrid_IsRefused()
        {
            // A half-built mask (the grid grew and the buffer did not) must not be read past its end.
            var shortBuffer = new[] { 0f, 0f, 0f };

            Assert.IsFalse(FogVisibilitySampling.TrySample(shortBuffer, TexWidth, TexHeight, new Vector2(20f, 20f), Tile, out float value));
            Assert.AreEqual(1f, value, Tolerance);
        }

        [Test]
        public void EmptyInputs_AreRefused()
        {
            Assert.IsFalse(FogVisibilitySampling.TrySample(null, TexWidth, TexHeight, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 0, 2, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 3, 0, new Vector2(20f, 20f), Tile, out _));
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), 1, 1, new Vector2(20f, 20f), Tile, out _),
                "A 1x1 texture is all border ring and no game tile — there is nothing to sample.");
            Assert.IsFalse(FogVisibilitySampling.TrySample(IndexBuffer(), TexWidth, TexHeight, new Vector2(20f, 20f), 0f, out _));
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

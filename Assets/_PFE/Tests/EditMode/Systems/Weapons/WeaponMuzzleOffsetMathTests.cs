using NUnit.Framework;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponMuzzleOffsetMath"/> — where a weapon's barrel tip is, measured from its own
    /// artwork.
    ///
    /// <para><b>Why this is measured rather than imported.</b> AS3 reads the muzzle from a named child
    /// of the weapon symbol (<c>Weapon.as:1395-1420</c> transforms <c>vis.emit</c>). That marker is not
    /// in the extracted SWF data and the source SWF is not in the repository, so the only remaining
    /// source is the pixels. The consequence is a heuristic, and these tests pin exactly which
    /// heuristic: <i>the rightmost art column, at the middle of that column</i>.</para>
    ///
    /// <para><b>The case that matters most is
    /// <see cref="TryFindBarrelTipPixels_UsesTheBoreLineNotTheBoundingBoxCentre"/>.</b> A gun's bounding
    /// box is dominated by the grip and magazine hanging below the bore, so a bounding-box-centre muzzle
    /// would fire out of the weapon's belly — which is the same class of visible error as the bug being
    /// fixed. That test is written so a box-centre implementation fails it.</para>
    ///
    /// <para>Pixel arrays are <b>bottom-up</b> (<c>index = y * width + x</c>, <c>y = 0</c> is the bottom
    /// row), matching <see cref="Texture2D.GetPixels32"/> and therefore matching
    /// <see cref="Sprite.pivot"/>'s space.</para>
    /// </summary>
    [TestFixture]
    public class WeaponMuzzleOffsetMathTests
    {
        private const float Tolerance = 1e-5f;
        private const byte Art = 255;

        private static Color32[] Canvas(int width, int height, out int w, out int h)
        {
            w = width;
            h = height;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, 0);   // transparent white
            return pixels;
        }

        private static void Paint(Color32[] pixels, int width, int x, int y, byte alpha = Art)
            => pixels[y * width + x] = new Color32(255, 255, 255, alpha);

        private static void PaintRect(Color32[] pixels, int width, int x0, int y0, int x1, int y1, byte alpha = Art)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Paint(pixels, width, x, y, alpha);
        }

        // ── Finding the tip ───────────────────────────────────────────────────

        [Test]
        public void TryFindBarrelTipPixels_ReturnsTheRightmostColumnsBoreLine()
        {
            Color32[] px = Canvas(8, 8, out int w, out _);
            PaintRect(px, w, 0, 2, 4, 4);   // body
            PaintRect(px, w, 5, 3, 7, 3);   // barrel, one pixel thick

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 tip));
            Assert.AreEqual(7.5f, tip.x, Tolerance, "rightmost art column is x = 7, centre at 7.5");
            Assert.AreEqual(3.5f, tip.y, Tolerance, "that column holds only y = 3, centre at 3.5");
        }

        [Test]
        public void TryFindBarrelTipPixels_UsesTheBoreLineNotTheBoundingBoxCentre()
        {
            // A gun whose magazine hangs well below the barrel. The bounding box is y 0..6 (centre 3.5);
            // the barrel is y 4..5 (centre 5.0). A box-centre implementation returns 3.5 and fails here.
            Color32[] px = Canvas(8, 8, out int w, out _);
            PaintRect(px, w, 0, 0, 3, 6);   // body + magazine, tall
            PaintRect(px, w, 4, 4, 7, 5);   // barrel, above the magazine

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 tip));
            Assert.AreEqual(7.5f, tip.x, Tolerance);
            Assert.AreEqual(5.0f, tip.y, Tolerance, "the bore line, not the bounding box's centre");
            // `Assert.AreNotEqual` has no delta overload in this NUnit, so state the same thing directly.
            Assert.IsTrue(Mathf.Abs(tip.y - 3.5f) > Tolerance,
                "3.5 is the bounding box's centre — the wrong answer");
        }

        [Test]
        public void TryFindBarrelTipPixels_SkipsEmptyColumns()
        {
            // Art that stops at x = 5 with transparent columns to its right must report x = 5, not the
            // rect's edge. A "rightmost column of the rect" implementation fails this.
            Color32[] px = Canvas(8, 8, out int w, out _);
            PaintRect(px, w, 0, 2, 5, 4);

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 tip));
            Assert.AreEqual(5.5f, tip.x, Tolerance);
            Assert.AreEqual(3.5f, tip.y, Tolerance);
        }

        [Test]
        public void TryFindBarrelTipPixels_IgnoresArtOutsideTheRect()
        {
            // These textures are larger than the sprite they carry: the PNG is exported at the symbol's
            // bounds and the sprite entry crops to the frame's. Art in the margin belongs to no part of
            // this sprite and must not drag the tip outwards.
            Color32[] px = Canvas(8, 8, out int w, out _);
            PaintRect(px, w, 0, 2, 2, 4);   // inside the rect
            PaintRect(px, w, 6, 2, 7, 4);   // outside it

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 5, 8), out Vector2 clipped));
            Assert.AreEqual(2.5f, clipped.x, Tolerance, "the rect stops at x = 5, so the tip is the art at x = 2");

            // Control: widen the rect and the same pixels now report the outer art — proving the
            // previous assertion was about the rect and not about the art being absent.
            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 wide));
            Assert.AreEqual(7.5f, wide.x, Tolerance);
        }

        [Test]
        public void TryFindBarrelTipPixels_ClampsARectThatRunsPastTheTexture()
        {
            // A malformed meta must not read out of bounds. The rect asks for 4 columns of nothing to the
            // right of an 8-wide texture; the answer is still the art at x = 3.
            Color32[] px = Canvas(8, 8, out int w, out _);
            PaintRect(px, w, 0, 2, 3, 4);

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 12, 12), out Vector2 tip));
            Assert.AreEqual(3.5f, tip.x, Tolerance);
        }

        [Test]
        public void TryFindBarrelTipPixels_FailsOnAnEmptyRegion()
        {
            Color32[] px = Canvas(8, 8, out int w, out _);

            Assert.IsFalse(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out _),
                "no art anywhere is a failure, not a tip at the origin");

            // Control: the same array with a single opaque pixel does succeed, so the failure above is
            // about the art and not about the call being broken.
            Paint(px, w, 1, 1);
            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 tip));
            Assert.AreEqual(1.5f, tip.x, Tolerance);
            Assert.AreEqual(1.5f, tip.y, Tolerance);
        }

        [Test]
        public void TryFindBarrelTipPixels_RejectsNullOrEmptyInput()
        {
            Assert.IsFalse(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(null, 8, 8, new RectInt(0, 0, 8, 8), out _));
            Assert.IsFalse(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(new Color32[0], 0, 0, new RectInt(0, 0, 8, 8), out _));
            Assert.IsFalse(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(new Color32[4], 8, 8, new RectInt(9, 9, 2, 2), out _),
                "a rect entirely outside the texture has no art");
        }

        [Test]
        public void TryFindBarrelTipPixels_ThresholdsFaintPixelsOut()
        {
            // The import keeps the authored alpha (`alphaIsTransparency`), and these textures are mostly
            // fully transparent padding. One pixel just under the threshold must not be mistaken for art.
            Color32[] px = Canvas(8, 8, out int w, out _);
            Paint(px, w, 6, 6, (byte)(WeaponMuzzleOffsetMath.OpaqueAlphaThreshold - 1));
            Paint(px, w, 2, 2, WeaponMuzzleOffsetMath.OpaqueAlphaThreshold);

            Assert.IsTrue(WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(px, w, 8, new RectInt(0, 0, 8, 8), out Vector2 tip));
            Assert.AreEqual(2.5f, tip.x, Tolerance, "the faint pixel at x = 6 is not art");
        }

        // ── Converting to a local offset ──────────────────────────────────────

        [Test]
        public void ToLocalOffset_SubtractsThePivotInTextureSpace()
        {
            // tip (7.5, 3.5); pivot measured from the rect's own corner is (4, 4) and the rect starts at
            // the texture origin, so the pivot sits at (4, 4) in texture pixels.
            Vector2 offset = WeaponMuzzleOffsetMath.ToLocalOffset(
                tipTexturePixels: new Vector2(7.5f, 3.5f),
                spritePivotPixels: new Vector2(4f, 4f),
                rectPositionPixels: Vector2.zero,
                pixelsPerUnit: 100f);

            Assert.AreEqual(0.035f, offset.x, Tolerance);
            Assert.AreEqual(-0.005f, offset.y, Tolerance);
        }

        [Test]
        public void ToLocalOffset_AddsTheRectOriginBeforeSubtracting()
        {
            // The bug this guards: Sprite.pivot is measured from the sprite rect's corner, not the
            // texture's, so a rect that does not start at (0, 0) shifts every offset unless the rect's
            // origin is added first. Here the rect starts at (10, 5), so the pivot is at (14, 9).
            Vector2 offset = WeaponMuzzleOffsetMath.ToLocalOffset(
                tipTexturePixels: new Vector2(17.5f, 3.5f),
                spritePivotPixels: new Vector2(4f, 4f),
                rectPositionPixels: new Vector2(10f, 5f),
                pixelsPerUnit: 100f);

            Assert.AreEqual(0.035f, offset.x, Tolerance, "17.5 - 14");
            Assert.AreEqual(-0.055f, offset.y, Tolerance, "3.5 - 9");

            // And the same numbers with the rect origin ignored would give the first test's answer —
            // proving the two cases are distinguishable.
            Assert.IsTrue(Mathf.Abs(offset.y - (-0.005f)) > Tolerance,
                "ignoring the rect origin would have produced -0.005 here");
        }

        [Test]
        public void ToLocalOffset_ScalesByPixelsPerUnit()
        {
            Vector2 at100 = WeaponMuzzleOffsetMath.ToLocalOffset(new Vector2(10f, 10f), Vector2.zero, Vector2.zero, 100f);
            Assert.AreEqual(0.1f, at100.x, Tolerance);

            Vector2 at50 = WeaponMuzzleOffsetMath.ToLocalOffset(new Vector2(10f, 10f), Vector2.zero, Vector2.zero, 50f);
            Assert.AreEqual(0.2f, at50.x, Tolerance);
        }

        [Test]
        public void ToLocalOffset_ReturnsZeroForANonPositivePpu()
        {
            // A zero PPU would divide by zero and produce an infinity that TransformPoint happily
            // propagates into the muzzle position. Zero is the honest "no answer" here.
            Assert.AreEqual(Vector2.zero, WeaponMuzzleOffsetMath.ToLocalOffset(new Vector2(5f, 5f), Vector2.zero, Vector2.zero, 0f));
            Assert.AreEqual(Vector2.zero, WeaponMuzzleOffsetMath.ToLocalOffset(new Vector2(5f, 5f), Vector2.zero, Vector2.zero, -100f));
        }
    }
}

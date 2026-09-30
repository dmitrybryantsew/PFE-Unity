using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Tests for <see cref="UnitSpriteAnchor"/> — where a unit's sprite is drawn relative to the unit's
    /// origin.
    ///
    /// <para><b>Why this needs pinning.</b> The defect it fixes is invisible to every other kind of
    /// check: the sprite asset is valid, the pivot is a legal value inside the sprite, the build is
    /// green, and the unit still renders 34px into the floor. Nothing in the pipeline disagrees with
    /// anything else — the asset carries a <i>centre</i> pivot and the runtime assumed the oracle's
    /// <i>feet</i> pivot, and both are individually defensible. So the numbers are asserted here against
    /// the oracle's own arithmetic, <c>Unit.as:2844-2859</c>.</para>
    ///
    /// <para>The real-data case is <c>training</c>: its resting frame is
    /// <c>Art/Units/Visuals/visualTrain/f001.png</c>, measured at 62x88, and its <c>.meta</c> carries
    /// <c>alignment: 0</c> (Center) with <c>spritePivot: {x: 0.5, y: 0.5}</c> — i.e. pivot (31, 44).</para>
    /// </summary>
    public class UnitSpriteAnchorTests
    {
        // The measured 'training' resting frame, and the asset's actual (centre) pivot.
        static readonly Vector2 TrainingSize = new Vector2(62f, 88f);
        static readonly Vector2 TrainingCentrePivot = new Vector2(31f, 44f);

        /// <summary>The oracle's "not declared" sentinel (<c>Unit.as:514-516</c>).</summary>
        static readonly Vector2Int Undeclared = new Vector2Int(-1, -1);

        /// <summary>
        /// Where the sprite's bottom edge ends up, in pixels relative to the unit's origin.
        /// A renderer offset by <paramref name="offset"/> draws a sprite whose bottom edge sits
        /// <c>pivot.y</c> below the renderer's origin.
        /// </summary>
        static float SpriteBottomRelativeToFeet(Vector2 offset, Vector2 pivot)
        {
            return offset.y - pivot.y;
        }

        // ── The default anchor: 10px below the feet ──────────────────────────

        [Test]
        public void CentrePivotedSprite_IsLiftedSoItsBottomSitsTenPixelsBelowTheFeet()
        {
            // 88/2 = 44px below the feet today; AS3 wants 10px below, so +34px up. That 34 is the whole
            // bug: at the camp's zoom it is the ~50 screen px the dummies were sunk by.
            Vector2 offset = UnitSpriteAnchor.PixelOffset(TrainingCentrePivot, TrainingSize, Undeclared);

            Assert.That(offset.x, Is.EqualTo(0f).Within(0.001f),
                "a centre pivot is already horizontally centred, so x needs no correction");
            Assert.That(offset.y, Is.EqualTo(34f).Within(0.001f));
            Assert.That(SpriteBottomRelativeToFeet(offset, TrainingCentrePivot), Is.EqualTo(-10f).Within(0.001f));
        }

        [Test]
        public void SpriteAlreadyCarryingTheOraclePivot_IsNotMoved()
        {
            // The self-correcting property: a sprite the importer DID pivot the oracle's way (the
            // Multiple-mode sheet cells) must yield exactly zero, or units would start floating instead.
            var oraclePivot = new Vector2(31f, UnitSpriteAnchor.DefaultFeetGapPixels);

            Vector2 offset = UnitSpriteAnchor.PixelOffset(oraclePivot, TrainingSize, Undeclared);

            Assert.That(offset.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(offset.y, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void DefaultAnchor_HoldsTheTenPixelGapForAnySpriteHeight()
        {
            // The 10 is an ABSOLUTE constant in the oracle (`-blitY + 10`), not a fraction of the cell,
            // so the invariant is "bottom is 10px below the feet" at every size — not "pivot is 10%".
            foreach (float height in new[] { 40f, 62f, 88f, 120f, 200f })
            {
                var size = new Vector2(60f, height);
                var centrePivot = new Vector2(30f, height * 0.5f);

                Vector2 offset = UnitSpriteAnchor.PixelOffset(centrePivot, size, Undeclared);

                Assert.That(SpriteBottomRelativeToFeet(offset, centrePivot), Is.EqualTo(-10f).Within(0.001f),
                    $"a {height}px sprite must still hang 10px below the feet");
            }
        }

        [Test]
        public void OffsetIsUpPositive_BecauseRoomPixelSpaceIsBottomUp()
        {
            // A centre-pivoted sprite is too low, so the correction must be POSITIVE. A sign error here
            // would bury the unit twice as deep instead of lifting it, and still look "roughly placed".
            Vector2 offset = UnitSpriteAnchor.PixelOffset(TrainingCentrePivot, TrainingSize, Undeclared);

            Assert.That(offset.y, Is.GreaterThan(0f), "the sprite is drawn too low, so it must be moved up");
        }

        // ── The declared registration point ──────────────────────────────────

        [Test]
        public void DeclaredRegistrationPoint_UsesTheOracleFormula()
        {
            // AS3: `visBmp.y = blitDY >= 0 ? -blitDY : -blitY + 10`. Declared, the origin sits
            // `blitY - blitDY` px above the bitmap's bottom edge. With blitY = 100 and blitDY = 20 that
            // is 80px, so the sprite's bottom must land 80px below the feet.
            var size = new Vector2(40f, 100f);
            var centrePivot = new Vector2(20f, 50f);

            Vector2 offset = UnitSpriteAnchor.PixelOffset(centrePivot, size, new Vector2Int(-1, 20));

            Assert.That(SpriteBottomRelativeToFeet(offset, centrePivot), Is.EqualTo(-80f).Within(0.001f));
        }

        [Test]
        public void DeclaredRegistrationX_IsAnAbsoluteOffset_NotAHalfWidth()
        {
            // AS3: `visBmp.x = blitDX >= 0 ? -blitDX : -blitX / 2`. A declared sprDX is the distance from
            // the origin to the bitmap's LEFT edge — it is not a centring fraction, so it must not be
            // halved the way the default is.
            var size = new Vector2(40f, 100f);
            var centrePivot = new Vector2(20f, 50f);

            Vector2 offset = UnitSpriteAnchor.PixelOffset(centrePivot, size, new Vector2Int(5, -1));

            Assert.That(offset.x, Is.EqualTo(15f).Within(0.001f), "origin is 5px right of the left edge, pivot is 20px in");
        }

        [Test]
        public void RegistrationPointAgreesWithUnitSheetLayout_WhenTheSpriteFillsItsCell()
        {
            // UnitSheetLayout.PivotFor answers the same question as a 0-1 fraction of the CELL. When the
            // drawn sprite is exactly the cell, the two must agree — otherwise the importer and the
            // runtime would disagree about where the feet are, which is how this bug got in.
            var cell = new Vector2Int(88, 88);
            foreach (Vector2Int registration in new[]
                     {
                         new Vector2Int(-1, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 10),
                         new Vector2Int(-1, 40), new Vector2Int(-1, 88),
                     })
            {
                Vector2 fraction = UnitSheetLayout.PivotFor(registration, cell);
                var spritePivot = new Vector2(cell.x * fraction.x, cell.y * fraction.y);

                Vector2 offset = UnitSpriteAnchor.PixelOffset(spritePivot, cell, registration);

                Assert.That(offset.y, Is.EqualTo(0f).Within(0.001f),
                    $"registration {registration} must be a no-op when the sprite already carries PivotFor's pivot");
            }
        }

        // ── Degenerate input ─────────────────────────────────────────────────

        [Test]
        public void NullSprite_YieldsNoOffset()
        {
            Vector2 offset = UnitSpriteAnchor.PixelOffset((Sprite)null, Undeclared);

            Assert.That(offset, Is.EqualTo(Vector2.zero));
        }
    }
}

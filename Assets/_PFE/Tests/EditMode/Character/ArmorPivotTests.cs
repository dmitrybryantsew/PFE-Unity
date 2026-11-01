using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// <see cref="ArmorPivots"/> — the pivot an armour sprite must be drawn with.
    ///
    /// <para><b>Why this is a fixture.</b> In AS3 an armour is a frame of the same symbol
    /// (<c>Obj.setArmor</c> is <c>m.gotoAndStop(aid)</c>), so the registration point is one fixed
    /// point and every frame's art hangs off it. The port stores each frame as its own PNG and writes
    /// frame 1's pivot for all of them — a <b>normalised</b> pivot, i.e. a fraction of the frame's own
    /// trimmed rect. When the armour frame trims differently the fraction lands somewhere else, and the
    /// part is drawn displaced. Nothing throws; the picture is just wrong, and only for the outfits
    /// whose frames trim differently, which is why the A/B is not a sufficient check.</para>
    ///
    /// <para><b>What is pinned here.</b> The pure arithmetic (<see cref="ArmorPivots.FromTrim"/>) on the
    /// two real cases that were measured in the shipped asset, plus the invariant that makes the formula
    /// mean what it says: the registration point is the same canvas point before and after. The
    /// <see cref="Sprite"/>-shaped guards are pinned by the two cases that must return their input
    /// untouched and can be reached without a texture.</para>
    /// </summary>
    public class ArmorPivotTests
    {
        const float Tolerance = 1e-5f;

        /// <summary>
        /// `fleg1` frame 1 trims to 21×24 at (5,0) with pivot (0.5411765, 0.96460176); the chitin frame
        /// trims to 25×37 at (3,0). The old code drew the armour leg with frame 1's pivot, which put the
        /// anchor 12.5 px too high in the canvas — i.e. the whole leg 12.5 px too low on a 42 px canvas.
        /// </summary>
        [Test]
        public void ATallerArmourFrame_PutsTheAnchorLowerInItsOwnRect()
        {
            Vector2 pivot = ArmorPivots.FromTrim(
                new Vector2(0.5411765f, 0.96460176f),
                new Vector2(5f, 0f), new Vector2(21f, 24f),
                new Vector2(3f, 0f), new Vector2(25f, 37f));

            Assert.AreEqual(0.53458826f, pivot.x, Tolerance, "x");
            Assert.AreEqual(0.62568764f, pivot.y, Tolerance, "y");
        }

        /// <summary>
        /// `ali`'s horn: frame 1 is 26×34 with pivot x = 1 (its right edge), the armour frame is 41×58
        /// on the same canvas. The anchor belongs at 26 px in the canvas, which is 26/41 of the wider
        /// sprite — not at its right edge, where the old code put it (a 15 px shift).
        /// </summary>
        [Test]
        public void AWiderArmourFrame_KeepsTheAnchorAtTheSameCanvasColumn()
        {
            Vector2 pivot = ArmorPivots.FromTrim(
                new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(26f, 34f),
                new Vector2(0f, 0f), new Vector2(41f, 58f));

            Assert.AreEqual(26f / 41f, pivot.x, Tolerance, "x");
            Assert.AreEqual(0f, pivot.y, Tolerance, "y");
        }

        /// <summary>
        /// The definition of the correction: the anchor is the same point of the shared canvas, whether
        /// it is expressed against frame 1's trim or the armour frame's. Every other fixture here is a
        /// special case of this, and this one is the reason the formula is what it is.
        /// </summary>
        [Test]
        public void TheAnchorIsTheSameCanvasPoint_ForAnyTwoTrims()
        {
            var cases = new[]
            {
                (new Vector2(0.5411765f, 0.96460176f), new Vector2(5f, 0f), new Vector2(21f, 24f), new Vector2(3f, 0f), new Vector2(25f, 37f)),
                (new Vector2(0.5f, 0.5f), new Vector2(0f, 5f), new Vector2(114f, 75f), new Vector2(0f, 3f), new Vector2(114f, 78f)),
                (new Vector2(0.7358491f, 0.9481268f), new Vector2(1f, 4f), new Vector2(35f, 74f), new Vector2(1f, 4f), new Vector2(39f, 80f)),
                (new Vector2(0.372f, 0.986f), new Vector2(1f, 0f), new Vector2(35f, 61f), new Vector2(0f, 0f), new Vector2(36f, 75f))
            };

            foreach (var (basePivot, baseOrigin, baseSize, armorOrigin, armorSize) in cases)
            {
                Vector2 armorPivot = ArmorPivots.FromTrim(basePivot, baseOrigin, baseSize, armorOrigin, armorSize);

                float baseX = basePivot.x * baseSize.x + baseOrigin.x;
                float baseY = basePivot.y * baseSize.y + baseOrigin.y;
                float armorX = armorPivot.x * armorSize.x + armorOrigin.x;
                float armorY = armorPivot.y * armorSize.y + armorOrigin.y;

                Assert.AreEqual(baseX, armorX, Tolerance, $"anchor x for {baseSize} -> {armorSize}");
                Assert.AreEqual(baseY, armorY, Tolerance, $"anchor y for {baseSize} -> {armorSize}");
            }
        }

        /// <summary>
        /// The common case: the armour frame trims exactly like frame 1, so there is nothing to correct.
        /// Most outfits are this (measured: `pip`, `tre`, `antirad`, `antihim` move every part by under
        /// 2 px), and the fixture exists so a future "always recompute" cannot quietly start moving them.
        /// </summary>
        [Test]
        public void AFrameThatTrimsLikeFrameOne_KeepsItsPivotExactly()
        {
            Vector2 pivot = ArmorPivots.FromTrim(
                new Vector2(0.5411765f, 0.96460176f),
                new Vector2(5f, 0f), new Vector2(21f, 24f),
                new Vector2(5f, 0f), new Vector2(21f, 24f));

            Assert.AreEqual(0.5411765f, pivot.x, 1e-7f);
            Assert.AreEqual(0.96460176f, pivot.y, 1e-7f);
        }

        /// <summary>
        /// A part with no base sprite — <c>helm</c>, <c>helm2</c>, <c>morda_armor</c> — has no frame 1 to
        /// be normalised against, and its baked pivot was computed by the head-variant path. Returning it
        /// untouched is the contract; overwriting it would move every helmet in the game.
        /// </summary>
        [Test]
        public void APartWithNoBaseSprite_KeepsItsBakedPivot()
        {
            var part = new CharacterPartDefinition
            {
                partName = "helm2",
                baseSprite = null,
                pivotNormalized = new Vector2(0.611719f, 0.250763f)
            };
            var armorOverride = new ArmorPartOverride
            {
                partIndex = 24,
                pivotOverride = new Vector2(0.25f, 0.75f)
            };

            Vector2 pivot = ArmorPivots.Resolve(part, armorOverride);

            Assert.AreEqual(0.25f, pivot.x, 0f);
            Assert.AreEqual(0.75f, pivot.y, 0f);
        }

        /// <summary>
        /// A baked pivot that is <i>not</i> a copy of the part's was computed on purpose — the composite
        /// <c>morda_armor</c> frame, a head variant's own pivot — and must not be recomputed even when
        /// the part does have a base sprite. This is the guard that keeps the fix from undoing the head
        /// work that already agrees with the original.
        /// </summary>
        [Test]
        public void AComputedPivot_IsNotRecomputed()
        {
            var part = new CharacterPartDefinition
            {
                partName = "morda_base",
                baseSprite = null,
                pivotNormalized = new Vector2(0.7504873f, 0.39075842f)
            };
            var armorOverride = new ArmorPartOverride
            {
                partIndex = 16,
                pivotOverride = new Vector2(0.7100f, 0.3600f)
            };

            Vector2 pivot = ArmorPivots.Resolve(part, armorOverride);

            Assert.AreEqual(0.71f, pivot.x, 0f);
            Assert.AreEqual(0.36f, pivot.y, 0f);
        }

        /// <summary>
        /// A degenerate armour rect cannot be normalised against; the input pivot is the only sane answer
        /// and returning it keeps the failure visible in the picture rather than in a division by zero.
        /// </summary>
        [Test]
        public void AZeroSizedArmourRect_ReturnsThePivotUnchanged()
        {
            Vector2 pivot = ArmorPivots.FromTrim(
                new Vector2(0.5f, 0.5f),
                new Vector2(0f, 0f), new Vector2(10f, 10f),
                new Vector2(0f, 0f), new Vector2(0f, 10f));

            Assert.AreEqual(0.5f, pivot.x, 0f);
            Assert.AreEqual(0.5f, pivot.y, 0f);
        }
    }
}

using NUnit.Framework;
using PFE.Entities.Enemies.Archetypes;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Entities.Enemies
{
    /// <summary>
    /// Pins <see cref="GriffinArmMath"/> — where the griffin's arm sits, where its hand is, and the one
    /// identity that keeps the limb and the gun from drifting apart.
    ///
    /// <para><b>What is at risk here, and why these tests rather than a screenshot.</b> Every number in
    /// the arm's geometry was either transcribed from a table or measured out of a SWF; nothing about it
    /// is self-evident from the code. The three conversions that can silently go wrong are the twips →
    /// pixels rule, the y-down → y-up flip, and the per-row length of a ragged table — and each of them
    /// produces a limb that is drawn, plausible and in the wrong place. A test that pins the
    /// <i>arithmetic</i> is what turns "the arm looks right on my machine" into a claim that survives the
    /// next edit.</para>
    ///
    /// <para><b>The one identity test.</b> AS3 writes the arm's transform as
    /// <c>rotation = rot·180/π + 90·(1 − storona)</c> with <c>scaleX = storona</c>
    /// (<c>UnitMerc.as:89-91</c>), and the port writes a held weapon's vis as
    /// <see cref="WeaponVisMath.VisRotationDeg"/> with <see cref="WeaponVisMath.VisScaleX"/>. Those are
    /// the same rule, which is why the rig calls the shared helpers instead of copying them. If someone
    /// ever changes one of the two, this test fails and names the divergence — instead of the limb and
    /// the barrel quietly rotating at different rates.</para>
    /// </summary>
    public class GriffinArmMathTests
    {
        // The griffin body's own declaration: <vis sprX='170' sprDX='96' sprDY='132'/>
        // (AllData.as:428), i.e. a 170x170 cell whose registration point is at (96, 132).
        static readonly Vector2Int GriffonCell = new Vector2Int(170, 170);
        static readonly Vector2Int GriffonRegistration = new Vector2Int(96, 132);

        [Test]
        public void WalkFramePutsTheShoulderEightPixelsRightAndTwentySevenUp()
        {
            // wPos[0][*] is (104, 105) for all 25 walk frames, so any column gives the same answer.
            Assert.IsTrue(GriffinArmMath.TryResolveWPos(0, 25, out Vector2Int wPos));
            Assert.AreEqual(new Vector2Int(104, 105), wPos);

            // AS3: arm.x = X + (wPos.x + visBmp.x) * storona = X + (104 - 96) = X + 8
            //      arm.y = Y +  wPos.y + visBmp.y            = Y + (105 - 132) = Y - 27
            // Room pixels are y-up, so the 27 below the unit's origin becomes +27 above it.
            Vector2 offset = GriffinArmMath.ArmOffsetPixels(wPos, GriffonRegistration, GriffonCell);
            Assert.AreEqual(8f, offset.x, 0.0001f);
            Assert.AreEqual(27f, offset.y, 0.0001f);
        }

        [Test]
        public void UndeclaredRegistrationFallsBackToCentredAndTenAboveTheCellBottom()
        {
            // A negative component means "not declared" (Unit.as:2848-2858), which centres x and puts
            // the origin a flat 10px above the cell's bottom. The cell is 170 tall, so visBmp.y is
            // -160 and the arm origin lands 160 cell-pixels above the cell's top... which in room
            // pixels is +160.
            Vector2 visBmp = GriffinArmMath.SpriteBitmapOffsetPixels(
                new Vector2Int(-1, -1), GriffonCell);
            Assert.AreEqual(-85f, visBmp.x, 0.0001f);
            Assert.AreEqual(-160f, visBmp.y, 0.0001f);
        }

        [Test]
        public void ShortRowsClampInsteadOfIndexingPastTheirEnd()
        {
            // Row 1 (fly/jump) holds 14 entries in a 25-column sheet, so column 20 of that row has no
            // authored frame. AS3 reads `undefined` there and throws on `_loc3_.x`; the port clamps to
            // the row's last entry, which keeps the arm where that frame put it.
            const int columns = 25;
            int lastOfRowOne = 1 * columns + 24;
            Assert.IsTrue(GriffinArmMath.TryResolveWPos(lastOfRowOne, columns, out Vector2Int clamped));

            int lastAuthored = 1 * columns + 13;
            Assert.IsTrue(GriffinArmMath.TryResolveWPos(lastAuthored, columns, out Vector2Int authored));

            Assert.AreEqual(authored, clamped, "a short row must clamp to its own last frame");
            Assert.AreEqual(new Vector2Int(104, 104), clamped);
        }

        [Test]
        public void ACellOutsideTheTableIsRefusedRatherThanGuessed()
        {
            // UnitAnimator.CurrentCellIndex is -1 when the state has no frames; a rig that treated that
            // as cell 0 would snap the arm to the walk pose for one frame on every state change.
            Assert.IsFalse(GriffinArmMath.TryResolveWPos(-1, 25, out _));

            // Row 4 does not exist: sprGriffon1 is a 4-row sheet.
            Assert.IsFalse(GriffinArmMath.TryResolveWPos(4 * 25, 25, out _));

            // A definition whose sheet import has not run has 0 columns.
            Assert.IsFalse(GriffinArmMath.TryResolveWPos(0, 0, out _));
        }

        [Test]
        public void ArmRotationIsTheSameRuleAsAHeldWeaponsVis()
        {
            // This is the identity the whole design leans on: `90 * (1 - storona)` is `facingLeft ? 180 : 0`,
            // so AS3's arm rule and AS3's `Weapon.animate` vis rule are the same function. Swept rather
            // than spot-checked, because the claim is "for every aim", not "at 0 and 90 degrees".
            for (int degrees = -360; degrees <= 360; degrees += 15)
            {
                float rotRad = degrees * Mathf.Deg2Rad;

                Assert.AreEqual(
                    WeaponVisMath.VisRotationDeg(rotRad, 0f, facingLeft: false),
                    GriffinArmMath.ArmRotationDeg(rotRad, facingLeft: false),
                    0.0001f,
                    $"facing right at {degrees} deg");

                Assert.AreEqual(
                    WeaponVisMath.VisRotationDeg(rotRad, 0f, facingLeft: true),
                    GriffinArmMath.ArmRotationDeg(rotRad, facingLeft: true),
                    0.0001f,
                    $"facing left at {degrees} deg");
            }
        }

        [Test]
        public void EmitFlipsYOnceAndOnlyOnce()
        {
            // emit is authored y-DOWN in the arm's local space: (16.30, 14.95) is 14.95 px *below* the
            // shoulder. Room pixels are y-up, so the hand is 14.95 px above... no: below, hence -14.95.
            Vector2 emit = GriffinArmMath.EmitLocalPixels(new Vector2(16.30f, 14.95f));
            Assert.AreEqual(16.30f, emit.x, 0.0001f);
            Assert.AreEqual(-14.95f, emit.y, 0.0001f);
        }

        [Test]
        public void ABakedPivotMakesTheArtOffsetZero()
        {
            // What the importer writes: the measured origin as a Unity pivot, from the sprite's
            // bottom-left. If the import bakes it, the runtime's compensation must compute to nothing —
            // otherwise the two corrections would stack and double the error.
            //
            // The round trip is spelled out rather than assumed: the importer stores a NORMALISED pivot
            // (origin / size) and Unity hands it back in pixels, so the test goes through the same
            // normalise-then-multiply the engine does.
            GriffinArmMath.GriffinArmVariant variant = GriffinArmMath.Variants[0];
            Vector2 size = new Vector2(33f, 29f);

            Vector2 normalised = new Vector2(
                variant.OriginPixels.x / size.x,
                1f - variant.OriginPixels.y / size.y);

            Vector2 pivotBackInPixels = new Vector2(normalised.x * size.x, normalised.y * size.y);

            Vector2 offset = GriffinArmMath.ArmArtOffsetPixels(pivotBackInPixels, size, variant.OriginPixels);
            Assert.AreEqual(0f, offset.x, 0.0001f);
            Assert.AreEqual(0f, offset.y, 0.0001f);
        }

        [Test]
        public void ACentredPivotIsCompensatedBackToTheShoulder()
        {
            // The DisplayObject import's documented placeholder is a centre pivot
            // (UnitSpriteImporter.ImportVisual). The rig must correct for it, and the correction is the
            // measured origin minus the centre — not zero, and not the origin itself.
            GriffinArmMath.GriffinArmVariant variant = GriffinArmMath.Variants[0];
            Vector2 size = new Vector2(33f, 29f);
            Vector2 centrePivot = new Vector2(size.x * 0.5f, size.y * 0.5f);

            Vector2 offset = GriffinArmMath.ArmArtOffsetPixels(centrePivot, size, variant.OriginPixels);
            Assert.AreEqual(16.5f - variant.OriginPixels.x, offset.x, 0.0001f);
            Assert.AreEqual(14.5f - (size.y - variant.OriginPixels.y), offset.y, 0.0001f);
        }

        [Test]
        public void TheArmIsChosenByTheBodySheetItIsDrawnWith()
        {
            // AS3 pairs them by one number: Res.getVis("visualGrifArm" + tr) and <vis blit='sprGriffon' + tr>.
            Assert.AreEqual(0, GriffinArmMath.VariantIndexForSpriteSheet("sprGriffon1"));
            Assert.AreEqual(4, GriffinArmMath.VariantIndexForSpriteSheet("sprGriffon5"));

            // Every other unit in the family must get no arm rather than variant 0.
            Assert.AreEqual(-1, GriffinArmMath.VariantIndexForSpriteSheet("sprRaider5"));
            Assert.AreEqual(-1, GriffinArmMath.VariantIndexForSpriteSheet(null));
            Assert.AreEqual(-1, GriffinArmMath.VariantIndexForSpriteSheet(string.Empty));

            // A sixth griffin sheet would have no limb to draw; refusing is better than drawing arm 1.
            Assert.AreEqual(-1, GriffinArmMath.VariantIndexForSpriteSheet("sprGriffon6"));
        }

        [Test]
        public void AllFiveVariantsDescribeTheFiveArmSymbolsInOrder()
        {
            Assert.AreEqual(5, GriffinArmMath.Variants.Length);
            for (int i = 0; i < GriffinArmMath.Variants.Length; i++)
            {
                Assert.AreEqual("visualGrifArm" + (i + 1), GriffinArmMath.Variants[i].SymbolName);
                Assert.AreEqual(i, GriffinArmMath.VariantIndexForSpriteSheet("sprGriffon" + (i + 1)));
            }
        }

        [Test]
        public void EveryMeasuredEmitLandsInsideItsLimb()
        {            // A guard against a transposed or un-converted number. The measured origins and emits are
            // in the same cell-pixel space, so the emit must be inside the exported PNG — this is the
            // check that caught the twips/pixels confusion (emit at x = 326 on a 33px limb).
            Vector2[] pngSizes =
            {
                new Vector2(33f, 29f), new Vector2(35f, 32f), new Vector2(35f, 33f),
                new Vector2(35f, 30f), new Vector2(35f, 30f),
            };

            for (int i = 0; i < GriffinArmMath.Variants.Length; i++)
            {
                GriffinArmMath.GriffinArmVariant variant = GriffinArmMath.Variants[i];
                Vector2 png = pngSizes[i];

                Assert.Greater(variant.EmitPixels.x, -variant.OriginPixels.x, variant.SymbolName);
                Assert.Less(variant.EmitPixels.x, png.x - variant.OriginPixels.x, variant.SymbolName);
                Assert.Greater(variant.EmitPixels.y, -variant.OriginPixels.y, variant.SymbolName);
                Assert.Less(variant.EmitPixels.y, png.y - variant.OriginPixels.y, variant.SymbolName);
            }
        }

        [Test]
        public void MeasuredOriginsAreTheOnesTheSwfProbeReports()
        {
            // The values, not just their relationships. Every other test in this file is relational, and
            // that is precisely how a real error survived: the probe once applied the SWF matrix as
            // `x' = a*x + b*y` instead of the spec's `x' = a*x + c*y`. Three of a limb's four parts are
            // unrotated, so b = c = 0 hid the swap, and the fourth was written off as "a placement that
            // draws nothing" and excluded. Every relational check still passed while four of the five
            // origins were 0.14-0.44 px wrong.
            //
            // So this test duplicates the table on purpose. Changing a number here means re-running
            //   python docs/Diagnostics/scripts/griffin_arm_swf_probe.py <assets.swf> 2245 2237 2229 2221 2214
            // which prints these same numbers from the SWF and exits non-zero if they disagree. Two
            // places to update is the point: a hand-edit to one of them is then a deliberate act.
            Vector2[] expectedOrigins =
            {
                new Vector2(10.37f,  6.61f),   // visualGrifArm1, 33x29
                new Vector2(10.80f,  9.88f),   // visualGrifArm2, 35x32
                new Vector2(10.37f, 10.04f),   // visualGrifArm3, 35x33
                new Vector2(10.37f,  7.36f),   // visualGrifArm4, 35x30
                new Vector2(10.37f,  7.36f),   // visualGrifArm5, 35x30
            };
            Vector2[] expectedEmits =
            {
                new Vector2(16.30f, 14.95f),
                new Vector2(17.30f, 10.95f),
                new Vector2(17.95f, 10.20f),
                new Vector2(17.95f, 10.20f),
                new Vector2(17.95f, 10.20f),
            };

            Assert.AreEqual(expectedOrigins.Length, GriffinArmMath.Variants.Length,
                "the arm table changed size, so this test no longer covers it");

            for (int i = 0; i < GriffinArmMath.Variants.Length; i++)
            {
                GriffinArmMath.GriffinArmVariant variant = GriffinArmMath.Variants[i];
                Assert.AreEqual(expectedOrigins[i].x, variant.OriginPixels.x, 0.005f, variant.SymbolName + " origin.x");
                Assert.AreEqual(expectedOrigins[i].y, variant.OriginPixels.y, 0.005f, variant.SymbolName + " origin.y");
                Assert.AreEqual(expectedEmits[i].x, variant.EmitPixels.x, 0.005f, variant.SymbolName + " emit.x");
                Assert.AreEqual(expectedEmits[i].y, variant.EmitPixels.y, 0.005f, variant.SymbolName + " emit.y");
            }
        }
    }
}

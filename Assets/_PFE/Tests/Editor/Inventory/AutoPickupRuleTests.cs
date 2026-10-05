using NUnit.Framework;
using PFE.Systems.Inventory;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins the walk-over auto-collection decision against the oracle's own numbers
    /// (<c>fe/loc/Loot.as:281-326</c>).
    ///
    /// <para><b>Every band assertion is paired with a positive control.</b> A rule that answered
    /// <c>false</c> to everything would satisfy every "is not taken" assertion in this file, and the
    /// failure it produces in play — loot that is never picked up — is exactly the bug the feature
    /// exists to fix. So each "outside" case sits next to an "inside" case on the same axis.</para>
    /// </summary>
    [TestFixture]
    public class AutoPickupRuleTests
    {
        // ── the oracle's numbers, unrounded ───────────────────────────────────

        [Test]
        public void Constants_MatchTheOracle()
        {
            // Loot.as:290 (±20 take band), :15 + :314 (takeR = osnRad = 50 magnet band),
            // UnitPlayer.as:2551/:2570/:2685 (isTake = 40), Loot.as:314 (magnet from isTake >= 20),
            // Loot.as:318-320 (delta / 5).
            Assert.AreEqual(20f, AutoPickupRule.TakeBandPixels, "Loot.as:290 take band");
            Assert.AreEqual(50f, AutoPickupRule.MagnetRadiusPixels, "Loot.as:15/:314 magnet radius");
            Assert.AreEqual(40, AutoPickupRule.TakeWindowTicks, "UnitPlayer.as:2551/:2570/:2685");
            Assert.AreEqual(20, AutoPickupRule.MagnetWindowThreshold, "Loot.as:314");
            Assert.AreEqual(5f, AutoPickupRule.MagnetDivisor, "Loot.as:318-320");
        }

        [Test]
        public void ToUnits_ConvertsAtTheArtImportScale()
        {
            // 100 px per unit — the same scale WorldConstants.ACTION_REACH and
            // WorldItemPickup.PickupRadius use.
            Assert.AreEqual(0.2f, AutoPickupRule.TakeBand, 1e-5f);
            Assert.AreEqual(0.5f, AutoPickupRule.MagnetRadius, 1e-5f);
        }

        // ── the take band: open on both axes, and both axes must pass ─────────

        [Test]
        public void TakeBand_AcceptsTheCentre()
        {
            Assert.IsTrue(AutoPickupRule.WithinTakeBand(0f, 0f), "positive control: dead centre");
        }

        [Test]
        public void TakeBand_IsOpen_NotClosed()
        {
            // The oracle writes four STRICT comparisons, so the boundary belongs to neither side.
            Assert.IsTrue(AutoPickupRule.WithinTakeBand(0.199f, 0f), "just inside");
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(0.2f, 0f), "exactly on the boundary is OUT");
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(-0.2f, 0f), "exactly on the boundary is OUT (negative)");
        }

        [Test]
        public void TakeBand_RequiresBothAxes()
        {
            // Control: x is fine, so the failure below is caused by y and nothing else.
            Assert.IsTrue(AutoPickupRule.WithinTakeBand(0.1f, 0.1f), "positive control: both axes inside");
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(0.1f, 0.25f), "y outside must reject even with x inside");
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(0.25f, 0.1f), "x outside must reject even with y inside");
        }

        // ── the magnet band ───────────────────────────────────────────────────

        [Test]
        public void MagnetBand_IsWiderThanTheTakeBand()
        {
            // The whole point: ground loot sits between the two bands. If this ever becomes equal to the
            // take band, grounded loot can never be lifted into range.
            Assert.IsTrue(AutoPickupRule.MagnetRadius > AutoPickupRule.TakeBand);
        }

        [Test]
        public void MagnetBand_AcceptsAndRejects()
        {
            Assert.IsTrue(AutoPickupRule.WithinMagnetBand(0.49f, 0f), "positive control: inside");
            Assert.IsFalse(AutoPickupRule.WithinMagnetBand(0.5f, 0f), "boundary is OUT");
            Assert.IsFalse(AutoPickupRule.WithinMagnetBand(0f, 0.6f), "well outside");
        }

        [Test]
        public void MagnetBand_RequiresBothAxes()
        {
            Assert.IsTrue(AutoPickupRule.WithinMagnetBand(0.3f, 0.3f), "positive control");
            Assert.IsFalse(AutoPickupRule.WithinMagnetBand(0.3f, 0.7f), "y outside");
            Assert.IsFalse(AutoPickupRule.WithinMagnetBand(0.7f, 0.3f), "x outside");
        }

        // ── the window gates ──────────────────────────────────────────────────

        [Test]
        public void WindowGates_AreAtOneAndTwenty()
        {
            Assert.IsTrue(AutoPickupRule.TakeEngaged(1), "isTake >= 1 takes");
            Assert.IsFalse(AutoPickupRule.TakeEngaged(0), "isTake 0 does not take");

            Assert.IsTrue(AutoPickupRule.MagnetEngaged(20), "isTake >= 20 magnetises");
            Assert.IsFalse(AutoPickupRule.MagnetEngaged(19), "isTake 19 does not magnetise");
        }

        [Test]
        public void MagnetStep_ClosesAFifthOfTheGap()
        {
            Assert.AreEqual(0.1f, AutoPickupRule.MagnetStep(0.5f), 1e-5f);
            Assert.AreEqual(-0.1f, AutoPickupRule.MagnetStep(-0.5f), 1e-5f);
            Assert.AreEqual(0f, AutoPickupRule.MagnetStep(0f), 1e-5f);
        }

        // ── the load-bearing claim: the band is measured from the BODY CENTRE ─

        [Test]
        public void BodyCentre_IsHalfTheBodyAboveTheFeet()
        {
            // littlepip: <phis sX='50' sY='70'/> → 0.7 units tall.
            Assert.AreEqual(3.35f, AutoPickupRule.BodyCentreY(3.0f, 0.7f), 1e-5f);
        }

        [Test]
        public void GroundLoot_IsOutsideTheTakeBandButInsideTheMagnetBand()
        {
            // THE test for this feature. The player stands at y = 0; loot dropped at its feet is also at
            // y = 0. The oracle measures the take band from the player's CENTRE (Loot.as:289), which is
            // 35 px up — so the loot is outside the ±20 px take band and can only be taken after the
            // magnet has lifted it into range.
            //
            // Measured from the feet instead, this would read as "inside" and the port would appear to
            // work in every unit test while the magnet — the thing that actually lifts grounded loot —
            // was never needed and never exercised.
            const float feetY = 0f;
            const float bodyHeight = 0.7f;
            const float lootY = 0f;

            float centre = AutoPickupRule.BodyCentreY(feetY, bodyHeight);
            float dy = centre - lootY;

            Assert.AreEqual(0.35f, dy, 1e-5f, "ground loot is 35 px below the centre");
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(0f, dy), "35 px > the 20 px take band");
            Assert.IsTrue(AutoPickupRule.WithinMagnetBand(0f, dy), "35 px < the 50 px magnet band");
        }

        [Test]
        public void LootAtTheBodyCentre_IsAlreadyInTheTakeBand()
        {
            // The counterpart to the test above, so the pair cannot both pass for the wrong reason: loot
            // that HAS been lifted to the centre is taken immediately, with no magnet step needed.
            const float bodyHeight = 0.7f;
            float centre = AutoPickupRule.BodyCentreY(0f, bodyHeight);

            Assert.IsTrue(AutoPickupRule.WithinTakeBand(0f, centre - centre), "loot at the centre");
            Assert.IsTrue(AutoPickupRule.WithinTakeBand(0f, 0f), "dy of zero");
        }

        [Test]
        public void LootFarAway_IsInNeitherBand()
        {
            // The absent control for both bands at once: nothing about a distant loot is collectable.
            const float far = 3f;
            Assert.IsFalse(AutoPickupRule.WithinTakeBand(far, far), "outside the take band");
            Assert.IsFalse(AutoPickupRule.WithinMagnetBand(far, far), "outside the magnet band");
        }
    }
}

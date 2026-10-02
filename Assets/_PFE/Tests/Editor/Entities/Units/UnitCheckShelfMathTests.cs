using NUnit.Framework;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitCheckShelfMath"/> — the port of AS3 <c>Unit.checkShelf</c>
    /// (<c>fe/unit/Unit.as:2713-2741</c>), the function that lets a unit stand on a crate.
    ///
    /// <para><b>Why this file exists.</b> "I can't stand on a crate" is a missing behaviour, not a
    /// mistuned one: the port had no prop-level landing test at all, and groundedness was a tile-only
    /// question, so a unit standing on a crate was ungrounded and sank through it. The ported arithmetic
    /// is three things, and each has an inversion that looks reasonable:</para>
    ///
    /// <list type="bullet">
    /// <item>the landing test, which is a <b>crossing</b> and not a proximity — and which is written in
    /// the opposite direction in the oracle because AS3 is y-down;</item>
    /// <item>the support-follow bound, <b>strict</b> against 10 px, so exactly 10 still carries;</item>
    /// <item>the momentum split, whose <c>+=</c> in AS3 becomes a <c>-=</c> in the port because the prop
    /// layer's y is up — get that wrong and a unit pushes the crate under it through the ceiling.</item>
    /// </list>
    ///
    /// <para>Every test below pairs a positive with a negative control on the same geometry, so a rule
    /// that simply always answers <c>true</c> or always <c>false</c> cannot pass the file.</para>
    /// </summary>
    [TestFixture]
    public class UnitCheckShelfMathTests
    {
        const float Surface = 180f;

        // ── IsLandingOnSurface: AS3's crossing test, translated out of y-down ─────────────────

        [Test]
        public void Landing_StepCrossesTheSurface_Lands()
        {
            // Feet at 185, step takes them to 176: they were above the top (180) and are now below it.
            Assert.IsTrue(UnitCheckShelfMath.IsLandingOnSurface(185f, 176f, Surface),
                "A step that crosses the top edge is a landing.");
        }

        [Test]
        public void Landing_StepStopsShortOfTheSurface_DoesNotLand()
        {
            // Negative control on the same geometry: 3 px of a 5 px gap is not an arrival. A rule that
            // only asked "is the surface below me" would land here, and a unit would hover onto a crate
            // it never reached.
            Assert.IsFalse(UnitCheckShelfMath.IsLandingOnSurface(185f, 182f, Surface),
                "A step that does not reach the top is not a landing.");
        }

        [Test]
        public void Landing_FeetAlreadyBelowTheSurface_DoesNotLand()
        {
            // The other half, and the one a sign flip breaks. The feet start BELOW the top, so the
            // surface is not something they can arrive at from above — it is behind them. Landing here
            // would yank the unit upward every tick.
            Assert.IsFalse(UnitCheckShelfMath.IsLandingOnSurface(Surface - 2f, Surface - 10f, Surface),
                "Feet already past the surface: nothing to land on from above.");
        }

        [Test]
        public void Landing_FastFallAcrossTheSurface_Lands()
        {
            // The crossing is scale-free: a 200 px step across the top is still a crossing. This is what
            // makes substepping unnecessary for correctness of the test itself — but see
            // RoomObjectPhysicsLayer.TryFindPropCrossedThisStep for why a motor still substeps.
            Assert.IsTrue(UnitCheckShelfMath.IsLandingOnSurface(Surface + 200f, Surface - 20f, Surface),
                "A long step that spans the top edge crosses it.");
        }

        [Test]
        public void Landing_FeetArriveExactlyOnTheSurface_Lands()
        {
            Assert.IsTrue(UnitCheckShelfMath.IsLandingOnSurface(Surface + 1f, Surface, Surface),
                "Arriving exactly on the top edge is a landing.");
        }

        [Test]
        public void SeatedUnit_StaysSeated()
        {
            // ── The one deliberate deviation from the oracle ─────────────────────────────────────
            //
            // AS3's second comparison is strict (`Y2 + param1 > box.Y1`), so with no vertical motion
            // (Y2 == box.Y1) it would answer false. AS3 never asks: `dy == 0` means neither of run()'s
            // vertical branches executes and checkShelf is not called at all.
            //
            // The port resolves groundedness as a STATE as well as an event, so it does ask — and a
            // seated unit must stay seated. With the strict form it would re-enter gravity, sink a pixel
            // and be caught again, i.e. flicker every tick.
            Assert.IsTrue(UnitCheckShelfMath.IsLandingOnSurface(Surface, Surface, Surface),
                "A unit already seated on the surface is still on it. AS3 reaches this state without " +
                "asking (dy == 0 skips both branches); the port asks, so the comparison is `>=`.");
        }

        [Test]
        public void SeatedUnit_OnePixelSink_StaysSeated()
        {
            // The same case one step later: gravity has not been applied (the unit is grounded), so
            // feetAfter == feetBefore. Also covers a float-rounding wobble of a fraction of a pixel.
            Assert.IsTrue(UnitCheckShelfMath.IsLandingOnSurface(Surface, Surface - 0.5f, Surface),
                "A grounded unit's tiny residual step must not unseat it.");
        }

        [Test]
        public void Landing_OnePixelAboveTheSurfaceWithNoMotion_DoesNotLand()
        {
            // Negative control for the `>=` deviation: it must not become a magnet. A unit whose feet are
            // 1 px ABOVE the surface and which does not move is not standing on it.
            Assert.IsFalse(UnitCheckShelfMath.IsLandingOnSurface(Surface + 1f, Surface + 1f, Surface),
                "The `>=` relaxation covers only the exactly-seated case, not a unit hovering above it.");
        }

        // ── IsSupportStillCarrying: AS3's ±10 px detach ───────────────────────────────────────

        [Test]
        public void SupportFollow_ZeroDisplacement_StillCarries()
        {
            Assert.IsTrue(UnitCheckShelfMath.IsSupportStillCarrying(0f, 0f),
                "A support that did not move is still a floor.");
        }

        [Test]
        public void SupportFollow_ExactlyTenPixels_StillCarries()
        {
            // The boundary. AS3 is `cdx > 10 || cdx < -10`, both STRICT, so exactly 10 carries — the same
            // shape as `Obj.shelf`'s exactly-40 px crate. Writing this as `>= 10` silently detaches on a
            // fast-moving platform.
            Assert.IsTrue(UnitCheckShelfMath.IsSupportStillCarrying(10f, 10f),
                "10 px is not more than 10 px, so it still carries (Unit.as:2016).");
            Assert.That(UnitCheckShelfMath.SupportFollowMaxDeltaPixels, Is.EqualTo(10f).Within(1e-4f),
                "The bound is AS3's literal 10.");
        }

        [Test]
        public void SupportFollow_ElevenPixels_Detaches()
        {
            Assert.IsFalse(UnitCheckShelfMath.IsSupportStillCarrying(11f, 0f),
                "A support shoved 11 px in one tick is no longer a floor.");
            Assert.IsFalse(UnitCheckShelfMath.IsSupportStillCarrying(-11f, 0f),
                "…and the same in the other direction.");
            Assert.IsFalse(UnitCheckShelfMath.IsSupportStillCarrying(0f, 11f),
                "…vertically too — a crate that dropped away takes the floor with it.");
            Assert.IsFalse(UnitCheckShelfMath.IsSupportStillCarrying(0f, -11f),
                "…and one that rose. AS3 tests cdy on both sides (:2016).");
        }

        // ── MomentumShareDownwardPixelsPerSecond: AS3's landing push ─────────────────────────

        [Test]
        public void Momentum_EqualMasses_SplitEvenly()
        {
            Assert.That(
                UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(100f, 1f, 1f),
                Is.EqualTo(50f).Within(1e-4f),
                "massa / (massa + box.massa) with equal masses gives half the speed.");
        }

        [Test]
        public void Momentum_HeavierUnit_GivesMoreOfItsSpeed()
        {
            Assert.That(
                UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(100f, 3f, 1f),
                Is.EqualTo(75f).Within(1e-4f),
                "A three-times-heavier unit hands the crate three quarters of its speed.");
        }

        [Test]
        public void Momentum_HeavierSupport_GivesLess()
        {
            Assert.That(
                UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(100f, 1f, 3f),
                Is.EqualTo(25f).Within(1e-4f),
                "A heavy crate barely notices the unit landing on it.");
        }

        [Test]
        public void Momentum_RisingUnit_TransfersNothing()
        {
            // The sign guard. A unit moving UP is not landing, so it must not push the crate under it
            // downward. Inverting this makes every jump press the crate down.
            Assert.That(
                UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(-100f, 1f, 1f),
                Is.EqualTo(0f),
                "A rising unit transfers no downward momentum.");
            Assert.That(
                UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(0f, 1f, 1f),
                Is.EqualTo(0f),
                "Neither does a stationary one.");
        }

        [Test]
        public void Momentum_BothMassesZero_ReturnsZeroAndNotNaN()
        {
            // AS3 would compute 0/0 here and hand the prop NaN, which then propagates into its position
            // and destroys it. A prop and a unit that author neither mass is the reachable case: both
            // getters fall back to a derived mass, but the unit's Massa can be 0 when it has no
            // definition at all.
            float share = UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(100f, 0f, 0f);

            Assert.That(share, Is.EqualTo(0f), "Zero total mass shares nothing.");
            Assert.IsFalse(float.IsNaN(share), "…and must not be NaN — that would corrupt the prop.");
        }

        // ── SupportVelocityAfterLanding: the y-down → y-up flip ─────────────────────────────

        [Test]
        public void SupportVelocity_ALandingPushesTheSupportDown()
        {
            // The single most dangerous line in the port of this rule. AS3's `dy` is positive downward,
            // so `_loc4_.dy += share` makes the crate fall faster. The prop layer's velocity.y is
            // positive UPWARD, so the port's `+=` is a `-=`. Getting it wrong makes a unit push the
            // crate it is standing on up through the ceiling.
            Vector2 after = UnitCheckShelfMath.SupportVelocityAfterLanding(Vector2.zero, 30f);

            Assert.That(after.y, Is.EqualTo(-30f).Within(1e-4f),
                "A downward share must lower the support's y velocity in a y-up space.");
        }

        [Test]
        public void SupportVelocity_PreservesHorizontal()
        {
            Vector2 after = UnitCheckShelfMath.SupportVelocityAfterLanding(new Vector2(5f, 0f), 30f);

            Assert.That(after.x, Is.EqualTo(5f).Within(1e-4f),
                "AS3 touches only dy (Unit.as:2736) — a crate sliding sideways keeps sliding.");
        }

        // ── FeetWorldPixelY: the room-local → world conversion ──────────────────────────────

        [Test]
        public void FeetWorldPixelY_AddsTheRoomOrigin()
        {
            // A prop's top edge comes out of GetApproximateBounds() in ROOM-LOCAL pixels; the motor's
            // posY is WORLD pixels. Adding the origin is the whole conversion — and forgetting it is
            // invisible in a room at land position (0,0), which is every room most tests are built on.
            Assert.That(UnitCheckShelfMath.FeetWorldPixelY(100f, 640f), Is.EqualTo(740f).Within(1e-4f),
                "room-local + room origin = world.");
            Assert.That(UnitCheckShelfMath.FeetWorldPixelY(100f, 0f), Is.EqualTo(100f).Within(1e-4f),
                "…and a room at the origin is the identity, which is why the bug hides there.");
        }
    }
}

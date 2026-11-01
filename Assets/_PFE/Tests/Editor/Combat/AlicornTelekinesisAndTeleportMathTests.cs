using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Systems.Telekinesis;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the two pieces of alicorn arithmetic that are pure geometry: the telekinetic hold/throw
    /// (<c>UnitAlicorn.as:1038-1084</c>, <c>:1140-1246</c>) and <c>actPort</c>'s point selection
    /// (<c>:1248-1317</c>).
    ///
    /// <para><b>Both are Y-mirror traps, which is why they get a fixture rather than a code review.</b>
    /// AS3's Y grows down and the port's grows up, so the oracle's <c>teleY = Y - 40</c> is <c>+40</c>
    /// here and its <c>ceil(y / tile) * tile - 1</c> snap is <c>floor(y / tile) * tile + 1</c>. A sign
    /// carried across unchanged does not throw — it drives the held crate into the floor and teleports
    /// the alicorn into the ceiling, both of which read as "the feature does nothing".</para>
    /// </summary>
    [TestFixture]
    public class AlicornTelekinesisAndTeleportMathTests
    {
        // ── the hold point (:1042-1048) ──────────────────────────────────────────────────────────

        [Test]
        public void HoldTarget_PutsAUnitOneHundredFiftyPixelsOut_AndFortyAbove()
        {
            Vector2 t = AlicornTelekinesisMath.HoldTarget(1000f, 500f, facing: 1, targetIsUnit: true);

            Assert.AreEqual(1150f, t.x, 1e-4);
            Assert.AreEqual(540f, t.y, 1e-4, "AS3 `Y - 40` with Y down is `+40` with Y up");
        }

        [Test]
        public void HoldTarget_PutsAPropEightyPixelsOut_OnTheSameSideAsTheFacing()
        {
            Vector2 east = AlicornTelekinesisMath.HoldTarget(1000f, 500f, facing: 1, targetIsUnit: false);
            Vector2 west = AlicornTelekinesisMath.HoldTarget(1000f, 500f, facing: -1, targetIsUnit: false);

            Assert.AreEqual(1080f, east.x, 1e-4);
            Assert.AreEqual(920f, west.x, 1e-4);
            Assert.AreEqual(540f, east.y, 1e-4);
            Assert.AreEqual(540f, west.y, 1e-4);
        }

        // ── the hold controller (:1050-1065) ────────────────────────────────────────────────────

        [Test]
        public void HoldAccelStep_AcceleratesTowardTheTarget_OneAccelPerAxisPerTick()
        {
            Vector2 delta = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 0f, objectY: 0f,
                target: new Vector2(100f, 0f),
                velocity: Vector2.zero,
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(4f, delta.x, 1e-4);
            Assert.AreEqual(0f, delta.y, 1e-4, "already inside the vertical deadzone");
        }

        [Test]
        public void HoldAccelStep_StopsAcceleratingAtThePerAxisCap()
        {
            // **This is the test that says the return value is a VELOCITY, not a delta.** At the cap the
            // oracle's `dx < teleSpeed` is false, so `dx` is left alone and the function hands back 16 —
            // the velocity it was given. A caller that treated that as a delta would add 16 to a body
            // already doing 16, every tick.
            Vector2 v = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 0f, objectY: 0f,
                target: new Vector2(100f, 0f),
                velocity: new Vector2(16f, 0f),
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(16f, v.x, 1e-4, "`dx < teleSpeed` is strict — unchanged, so the answer is 16");
        }

        [Test]
        public void HoldAccelStep_ReturnsTheNewVelocity_NotTheChange()
        {
            // Same fact from the other side: one tick of accel off a standing start is 0 + 4, and the
            // difference a caller must add is 4. Pinned as a pair so neither half can drift alone.
            Vector2 v = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 0f, objectY: 0f,
                target: new Vector2(100f, 0f),
                velocity: Vector2.zero,
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(4f, v.x, 1e-4);
            Assert.AreEqual(4f, v.x - 0f, 1e-4, "the delta a caller adds is 4");
        }

        [Test]
        public void HoldAccelStep_DoesNothingInsideTheDeadzone()
        {
            Vector2 delta = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 90f, objectY: 0f,
                target: new Vector2(100f, 0f),
                velocity: Vector2.zero,
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(0f, delta.x, 1e-4, "|90 - 100| = 10 < derp 15");
        }

        [Test]
        public void HoldAccelStep_BrakesTowardTheTargetWhenOvershooting()
        {
            Vector2 delta = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 200f, objectY: 0f,
                target: new Vector2(100f, 0f),
                velocity: Vector2.zero,
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(-4f, delta.x, 1e-4);
        }

        [Test]
        public void HoldAccelStep_IsSymmetricInY_SoAPositiveTargetPushesUp()
        {
            // The mirror check: with Y up, a target above the object must produce a POSITIVE dy. The
            // oracle's `if(teleObj.Y > teleY + derp) dy -= teleAccel` is "push it down" only because its
            // Y grows down.
            Vector2 delta = AlicornTelekinesisMath.HoldAccelStep(
                objectX: 0f, objectY: 0f,
                target: new Vector2(0f, 100f),
                velocity: Vector2.zero,
                teleSpeed: 16f, teleAccel: 4f, deadzone: 15f);

            Assert.AreEqual(4f, delta.y, 1e-4);
        }

        // ── the throw (:1205-1246) ──────────────────────────────────────────────────────────────

        [Test]
        public void ThrowImpulse_ForAUnit_IsTheFacingDirection_AndTheMagnitudeIsTheForce()
        {
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: true, facing: 1,
                objectX: 0f, objectCentreY: 0f, celX: 0f, celY: 0f,
                throwForce: 30f, massa: 0f,
                out float ix, out float iy);

            Assert.Greater(ix, 0f, "thrown the way the caster faces");
            Assert.Greater(iy, 0f, "and slightly up — AS3's y:-30 mirrored");
            Assert.AreEqual(30f, Mathf.Sqrt(ix * ix + iy * iy), 1e-3, "norma(vec, speed)");
        }

        [Test]
        public void ThrowImpulse_ForAUnit_IgnoresTheTargetEntirely()
        {
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: true, facing: 1,
                objectX: 0f, objectCentreY: 0f, celX: -5000f, celY: 9000f,
                throwForce: 30f, massa: 0f,
                out float aX, out float aY);

            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: true, facing: 1,
                objectX: 0f, objectCentreY: 0f, celX: 0f, celY: 0f,
                throwForce: 30f, massa: 0f,
                out float bX, out float bY);

            Assert.AreEqual(aX, bX, 1e-4);
            Assert.AreEqual(aY, bY, 1e-4);
        }

        [Test]
        public void ThrowImpulse_ForAUnit_FacesWestWhenTheFacingIsNegative()
        {
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: true, facing: -1,
                objectX: 0f, objectCentreY: 0f, celX: 0f, celY: 0f,
                throwForce: 30f, massa: 0f,
                out float ix, out float iy);

            Assert.Less(ix, 0f);
            Assert.Greater(iy, 0f);
        }

        [Test]
        public void ThrowImpulse_ForAProp_AimsAtTheTargetWithAnUpwardBias()
        {
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: false, facing: 1,
                objectX: 1000f, objectCentreY: 500f,
                celX: 1400f, celY: 500f,
                throwForce: 30f, massa: 1f,
                out float ix, out float iy);

            Assert.Greater(ix, 0f, "toward +x");
            Assert.Greater(iy, 0f, "|dx| / 4 arcs it upward");
            Assert.AreEqual(30f, Mathf.Sqrt(ix * ix + iy * iy), 1e-3);
        }

        [Test]
        public void ThrowImpulse_ForAHeavyProp_IsSlowerBySqrtMassa()
        {
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: false, facing: 1,
                objectX: 0f, objectCentreY: 0f, celX: 400f, celY: 0f,
                throwForce: 45f, massa: 4f,
                out float ix, out float iy);

            Assert.AreEqual(22.5f, Mathf.Sqrt(ix * ix + iy * iy), 1e-3, "45 / sqrt(4)");
        }

        [Test]
        public void ThrowImpulse_IsZero_WhenTheTargetSitsOnTheObject()
        {
            // AS3's `norma` would divide by zero and add NaN to the velocity. A NaN position makes the
            // prop disappear, which is a worse failure than a throw that does not happen.
            AlicornTelekinesisMath.ThrowImpulse(
                targetIsUnit: false, facing: 1,
                objectX: 100f, objectCentreY: 100f, celX: 100f, celY: 100f,
                throwForce: 30f, massa: 1f,
                out float ix, out float iy);

            Assert.AreEqual(0f, ix);
            Assert.AreEqual(0f, iy);
        }

        // ── findBox's gates (:1143-1157) ────────────────────────────────────────────────────────

        [Test]
        public void PrefersUnitTarget_IsAQuarter()
        {
            Assert.IsTrue(AlicornTelekinesisMath.PrefersUnitTarget(0.2499f));
            Assert.IsFalse(AlicornTelekinesisMath.PrefersUnitTarget(0.25f));
        }

        [Test]
        public void AcceptsProp_IsThirtyPercent()
        {
            Assert.IsTrue(AlicornTelekinesisMath.AcceptsProp(0.2999f));
            Assert.IsFalse(AlicornTelekinesisMath.AcceptsProp(0.3f));
        }

        [Test]
        public void InTeleRange_Is600Pixels_Inclusive()
        {
            Assert.IsTrue(AlicornTelekinesisMath.InTeleRange(600f * 600f));
            Assert.IsFalse(AlicornTelekinesisMath.InTeleRange(600f * 600f + 1f));
            Assert.IsTrue(AlicornTelekinesisMath.InTeleRange(0f));
        }

        // ── actPort's ladder (:1248-1317) ───────────────────────────────────────────────────────

        static readonly Vector2 Sprite = new Vector2(60f, 120f);
        const float LimitX = 1920f;   // 48 tiles * 40
        const float LimitY = 1000f;   // 25 tiles * 40
        const float Tile = 40f;

        [Test]
        public void ActPort_NearRung_JumpsTwoHundredToFiveHundredPixelsBehindThePlayer()
        {
            // attempt 1, magnitude 0 -> distance 200; branch 0 < 0.7 -> behind the player (west).
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: false, attempt: 1,
                playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0f, rollMagnitude: 0f, rollJitter: 0.5f);

            Assert.AreEqual(800f, p.x, 1e-4);
            Assert.AreEqual(481f, p.y, 1e-4, "floor(500/40)*40 + 1 — the mirrored snap");
        }

        [Test]
        public void ActPort_NearRung_CanJumpInFront()
        {
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: false, attempt: 1,
                playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0.9f, rollMagnitude: 0f, rollJitter: 0.5f);

            Assert.AreEqual(1200f, p.x, 1e-4);
        }

        [Test]
        public void ActPort_NearRung_KeepsThePlayersAltitude()
        {
            // `_loc4_ = _loc2_.Y` — no jitter at all on the first rung, before the snap.
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: false, attempt: 2,
                playerLocal: new Vector2(1000f, 617f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0f, rollMagnitude: 0f, rollJitter: 0.99f);

            Assert.AreEqual(601f, p.y, 1e-4, "floor(617/40)=15 -> 600, +1");
        }

        [Test]
        public void ActPort_FarRung_IsAttemptsFiveThroughNine_AndAddsVerticalJitter()
        {
            // attempt 5, magnitude 0.5 -> 200 + 0.5*800 = 600; branch 0.9 >= 0.5 -> in front.
            // jitter 0.75 -> (0.75*2 - 1) * 80 = +40.
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: false, attempt: 5,
                playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0.9f, rollMagnitude: 0.5f, rollJitter: 0.75f);

            Assert.AreEqual(1600f, p.x, 1e-4);
            Assert.AreEqual(521f, p.y, 1e-4, "540 snaps to floor(540/40)*40 + 1");
        }

        [Test]
        public void ActPort_Reactive_SkipsBothNearRungs_AndPicksAnywhereInTheRoom()
        {
            // attempt 1 and reactive -> the uniform rung, NOT the 200 px hop.
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: true, attempt: 1,
                playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0.9f, rollMagnitude: 0.5f, rollJitter: 0.25f);

            Assert.AreEqual(960f, p.x, 1e-4, "0.5 * limX, snapped");
            Assert.AreEqual(241f, p.y, 1e-4, "0.25 * limY = 250, snapped to 241");
        }

        [Test]
        public void ActPort_AttemptTen_FallsThroughToTheUniformRung_EvenWhenNotReactive()
        {
            Vector2 p = AlicornTeleportMath.PickPoint(
                reactive: false, attempt: 10,
                playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                rollBranch: 0.9f, rollMagnitude: 0.5f, rollJitter: 0.25f);

            Assert.AreEqual(960f, p.x, 1e-4);
            Assert.AreEqual(241f, p.y, 1e-4);
        }

        [Test]
        public void SnapAndClamp_ClampsInsideTheRoomOnAllFourSides()
        {
            Vector2 low = AlicornTeleportMath.SnapAndClamp(
                x: 10f, y: 10f, spriteSize: Sprite,
                roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile);

            Assert.AreEqual(60f, low.x, 1e-4, "x >= scX");
            Assert.AreEqual(40f, low.y, 1e-4, "y >= 40 — the floor margin, not the body height");

            Vector2 high = AlicornTeleportMath.SnapAndClamp(
                x: 5000f, y: 5000f, spriteSize: Sprite,
                roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile);

            Assert.AreEqual(1860f, high.x, 1e-4, "limX - scX");
            Assert.AreEqual(880f, high.y, 1e-4, "limY - scY — the ceiling reserves the whole body");
        }

        [Test]
        public void SnapAndClamp_RoundsHalfUp_NotToEven()
        {
            // 100 / 40 = 2.5. AS3's Math.round sends that to 3; System.Math.Round (banker's) sends it to
            // 2. A two-tile difference is invisible until it puts a body through a wall.
            Vector2 p = AlicornTeleportMath.SnapAndClamp(
                x: 100f, y: 0f, spriteSize: Vector2.zero,
                roomLimitX: 10000f, roomLimitY: 10000f, tileSize: Tile);

            Assert.AreEqual(120f, p.x, 1e-4);
        }

        [Test]
        public void SnapAndClamp_WithNoTileSize_OnlyClamps()
        {
            Vector2 p = AlicornTeleportMath.SnapAndClamp(
                x: 137f, y: 291f, spriteSize: Sprite,
                roomLimitX: LimitX, roomLimitY: LimitY, tileSize: 0f);

            Assert.AreEqual(137f, p.x, 1e-4);
            Assert.AreEqual(291f, p.y, 1e-4);
        }

        [Test]
        public void EveryRung_LandsInsideTheRoom()
        {
            // The point of the clamp, stated as one property over the whole ladder: no combination of
            // rolls and attempts may produce a destination outside the body-safe rect.
            for (int attempt = 1; attempt <= AlicornTeleportMath.MaxAttempts; attempt++)
            {
                for (int reactive = 0; reactive <= 1; reactive++)
                {
                    for (float magnitude = 0f; magnitude <= 1f; magnitude += 0.1f)
                    {
                        Vector2 p = AlicornTeleportMath.PickPoint(
                            reactive: reactive == 1, attempt: attempt,
                            playerLocal: new Vector2(1000f, 500f), playerFacing: 1,
                            spriteSize: Sprite, roomLimitX: LimitX, roomLimitY: LimitY, tileSize: Tile,
                            rollBranch: 0.3f, rollMagnitude: magnitude, rollJitter: 0.6f);

                        Assert.GreaterOrEqual(p.x, Sprite.x, "attempt " + attempt);
                        Assert.LessOrEqual(p.x, LimitX - Sprite.x, "attempt " + attempt);
                        Assert.GreaterOrEqual(p.y, AlicornTeleportMath.EdgeMarginPixels, "attempt " + attempt);
                        Assert.LessOrEqual(p.y, LimitY - Sprite.y, "attempt " + attempt);
                    }
                }
            }
        }
    }
}

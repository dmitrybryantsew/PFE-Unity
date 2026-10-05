using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the AS3 px/frame → Unity per-second conversions for projectile physics.
    ///
    /// <para><b>Why these tests exist.</b> Before this fixture nothing asserted any of these numbers,
    /// which is exactly how the port accumulated four separate divergent gravity/acceleration
    /// literals — three of them fixed years apart. Every assertion below names the wrong value it is
    /// guarding against, so a regression fails with the reason attached rather than as an opaque
    /// number mismatch.</para>
    ///
    /// <para>They are pure-arithmetic tests: <see cref="ProjectilePhysicsMath"/> takes its inputs as
    /// arguments, so no GameObject, pool or running engine is needed. What they do <i>not</i> cover is
    /// the call site — <c>Projectile.Initialize</c> assigns the result in one line, and exercising that
    /// would require PlayMode.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/Weapon.as</c> (bullets) and <c>fe/weapon/Trasser.as</c>
    /// (thrown objects).</para>
    /// </summary>
    [TestFixture]
    public class ProjectilePhysicsMathTests
    {
        /// <summary>
        /// Values here are far apart (0.3 vs 9 vs 18), so a loose delta still catches every real
        /// regression while tolerating float32 rounding in <c>0.01f * 30f * 30f</c>.
        /// </summary>
        private const float Tolerance = 1e-4f;

        // ── The two conversion factors ───────────────────────────────────────────────────────

        [Test]
        public void AccelerationScale_IsNine_NotTheVelocityFactor()
        {
            Assert.AreEqual(9f, ProjectilePhysicsMath.AccelerationScale, Tolerance,
                "A per-frame² acceleration converts by fps²/PPU = 9.");

            // The two idioms, spelled out. Getting these confused is the port's most common bug.
            Assert.AreEqual(0.3f, ProjectilePhysicsMath.VelocityScale, Tolerance,
                "A per-frame velocity converts by fps/PPU = 0.3.");
            Assert.AreEqual(30f, ProjectilePhysicsMath.AccelerationScale / ProjectilePhysicsMath.VelocityScale,
                Tolerance, "The acceleration factor is exactly fps = 30 times the velocity factor.");

            // `Is.Not.EqualTo(...).Within(...)` rather than the classic AreNotEqual: NUnit has no
            // AreNotEqual(double, double, double, string) overload, so that call silently binds to
            // AreNotEqual(object, object, string, params object[]) and fails to compile.
            Assert.That(ProjectilePhysicsMath.AccelerationScale, Is.Not.EqualTo(30f).Within(Tolerance),
                "`× SimClock.FramesPerSecond` is the VELOCITY idiom; using it on an acceleration is 3.33× too strong.");
            Assert.That(ProjectilePhysicsMath.AccelerationScale, Is.Not.EqualTo(0.3f).Within(Tolerance),
                "`× SimClock.FramesPerSecond / PPU` is also the velocity idiom; using it on an acceleration is 30× too weak.");
        }

        [Test]
        public void AccelerationScale_MatchesTheCanonicalTileQueryConstant()
        {
            // Not a tautology in intent: it pins the weapon path to the SAME constant the motor uses,
            // so the two cannot drift apart the way the port's three gravity literals did.
            Assert.AreEqual(TileQueryConstants.PerFrameAccelerationToUnitsPerSecondSquared,
                ProjectilePhysicsMath.AccelerationScale, Tolerance);
            Assert.AreEqual(TileQueryConstants.GravityUnitsPerSecondSquared,
                ProjectilePhysicsMath.AccelerationScale, Tolerance,
                "World.ddy is 1 px/frame², so gravity and the bare acceleration factor coincide.");
        }

        // ── BulletAcceleration: Weapon.as:1524-1558 ──────────────────────────────────────────

        [Test]
        public void BulletAcceleration_WithNoTerms_IsZero()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 0f, 0f, 0);
            Assert.AreEqual(0f, a.x, Tolerance);
            Assert.AreEqual(0f, a.y, Tolerance, "AS3 zeroes ddx/ddy at Weapon.as:1524.");
        }

        [Test]
        public void BulletAcceleration_GravityOnly_IsDownwardNine()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 1f, 0f, 0);
            Assert.AreEqual(0f, a.x, Tolerance);
            Assert.AreEqual(-9f, a.y, Tolerance,
                "Weapon.as:1558 `b.ddy += World.ddy * grav` = 1 × 9, downward (negative in Y-up).");

            Assert.That(a.y, Is.Not.EqualTo(-18f).Within(Tolerance),
                "The port used a 0.6 ddy literal here, making gravity 2× too strong.");
        }

        [Test]
        public void BulletAcceleration_GravityScalesLinearly()
        {
            Vector2 half = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 0.5f, 0f, 0);
            Assert.AreEqual(-4.5f, half.y, Tolerance,
                "`grav` is a MULTIPLIER on World.ddy, not a ddy itself.");
        }

        [Test]
        public void BulletAcceleration_FlameStrong_IsSevenPointTwo_NotTwentyFour()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.up, 0f, 0f, 1);
            Assert.AreEqual(0f, a.x, Tolerance);
            Assert.AreEqual(7.2f, a.y, Tolerance,
                "Weapon.as:1545 lift is 0.8 px/frame² upward → 0.8 × 9 = 7.2 units/s².");

            Assert.That(a.y, Is.Not.EqualTo(24f).Within(Tolerance),
                "0.8 × SimClock.FramesPerSecond = 24 is the velocity idiom: 3.33× too strong.");
        }

        [Test]
        public void BulletAcceleration_FlameWeak_IsOnePointEight_NotSix()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.up, 0f, 0f, 2);
            Assert.AreEqual(1.8f, a.y, Tolerance,
                "Weapon.as:1551 lift is 0.2 px/frame² upward → 0.2 × 9 = 1.8 units/s².");

            Assert.That(a.y, Is.Not.EqualTo(6f).Within(Tolerance),
                "0.2 × SimClock.FramesPerSecond = 6 is the velocity idiom: 3.33× too strong.");
        }

        [Test]
        public void BulletAcceleration_FlameAndGravity_AccumulateRatherThanOverwrite()
        {
            // THE regression guard for the structural half of the bug. AS3 adds all three terms
            // (Weapon.as:1536-1558); the port ASSIGNED in the flame branch, which discarded gravity.
            // 7.2 + (-9.0) = -1.8. A port that overwrote would report +7.2.
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.up, 1f, 0f, 1);

            Assert.AreEqual(-1.8f, a.y, Tolerance,
                "Flame lift ADDS to gravity (AS3 uses `+=` throughout), so a flame weapon with grav=1 "
                + "nets -1.8, not +7.2. Assigning instead of adding silently drops gravity.");

            Assert.That(a.y, Is.Not.EqualTo(7.2f).Within(Tolerance),
                "Overwriting rather than accumulating is the bug this pins.");
        }

        [Test]
        public void BulletAcceleration_Thrust_ScalesByNine_NotThirty()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 0f, 1f, 0);
            Assert.AreEqual(9f, a.x, Tolerance,
                "Weapon.as:1536 `b.ddx += cos(rot) * accel` is px/frame² → 1 × 9.");

            Assert.That(a.x, Is.Not.EqualTo(30f).Within(Tolerance),
                "`accel × SimClock.FramesPerSecond` = 30 is the velocity idiom: 3.33× too strong.");
        }

        [Test]
        public void BulletAcceleration_ThrustAndGravity_Compose()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 1f, 1f, 0);
            Assert.AreEqual(9f, a.x, Tolerance, "Horizontal thrust is unaffected by gravity.");
            Assert.AreEqual(-9f, a.y, Tolerance,
                "A rocket accelerating along +x still falls at full gravity until it gains lift.");
        }

        [Test]
        public void BulletAcceleration_ThrustFollowsTheAimDirection()
        {
            // 45° up-right, accel 0.7 px/frame²: 0.7 × 9 / √2 ≈ 4.4548 on each axis.
            var dir = new Vector2(1f, 1f).normalized;
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(dir, 0f, 0.7f, 0);

            Assert.AreEqual(4.4548f, a.x, 1e-3f);
            Assert.AreEqual(4.4548f, a.y, 1e-3f);
        }

        [Test]
        public void BulletAcceleration_AllThreeTerms_SumAsAs3Does()
        {
            // 1 × 9 (thrust +x) + 7.2 (flame 1) - 9 (gravity) = (9, -1.8).
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.right, 1f, 1f, 1);
            Assert.AreEqual(9f, a.x, Tolerance);
            Assert.AreEqual(-1.8f, a.y, Tolerance);
        }

        // ── Thrown objects: Trasser.as:74-204 ───────────────────────────────────────────────

        [Test]
        public void SlidingFriction_IsEighteen_NotFour()
        {
            Assert.AreEqual(18f, ProjectilePhysicsMath.SlidingFriction(2f), Tolerance,
                "Trasser.as:78/82 applies `dx -= brake` once per frame, so brake (2, Trasser.as:45) "
                + "is a px/frame² quantity → 2 × 9 = 18 units/s².");

            Assert.That(ProjectilePhysicsMath.SlidingFriction(2f), Is.Not.EqualTo(4f).Within(Tolerance),
                "The port's `brake / SimClock.FramesPerSecond * 60f` = 4 was neither idiom: 4.5× too weak.");
        }

        [Test]
        public void SlidingFriction_ScalesLinearlyWithBrake()
        {
            Assert.AreEqual(9f, ProjectilePhysicsMath.SlidingFriction(1f), Tolerance);
            Assert.AreEqual(0f, ProjectilePhysicsMath.SlidingFriction(0f), Tolerance);
        }

        [Test]
        public void SettleThresholdVelocity_UsesTheVelocityIdiom()
        {
            // The trap in miniature: this threshold is compared against `dy`, which is a VELOCITY,
            // so it converts by ×0.3. Using the acceleration idiom would give 18 — 30× too high.
            Assert.AreEqual(0.6f, ProjectilePhysicsMath.SettleThresholdVelocity, Tolerance,
                "Trasser.as:204 `else if(this.dy > 2)`: 2 px/frame × 0.3 = 0.6 units/s.");

            Assert.That(ProjectilePhysicsMath.SettleThresholdVelocity, Is.Not.EqualTo(1f).Within(Tolerance),
                "The port's `0.5f / SimClock.FramesPerSecond * 60f` = 1.0 was 1.667× too high.");
            Assert.That(ProjectilePhysicsMath.SettleThresholdVelocity, Is.Not.EqualTo(18f).Within(Tolerance),
                "2 × 9 would be the acceleration idiom, applied to a velocity.");
        }

        [Test]
        public void SettleThreshold_IsExactlyTheAs3ThresholdInVelocityUnits()
        {
            Assert.AreEqual(
                ProjectilePhysicsMath.SettleThresholdPxPerFrame * ProjectilePhysicsMath.VelocityScale,
                ProjectilePhysicsMath.SettleThresholdVelocity, Tolerance);
        }

        // ── Thrown-object constants: WThrow overrides the bullet class's own defaults ─────────

        [Test]
        public void ThrowBounceRetention_IsWThrowsValue_NotTheBulletClassDefault()
        {
            Assert.AreEqual(0.4f, ProjectilePhysicsMath.ThrowBounceRetention, Tolerance,
                "WThrow.as:22 declares skok = 0.4 and WThrow.as:188 assigns it onto the bullet.");

            Assert.That(ProjectilePhysicsMath.ThrowBounceRetention, Is.Not.EqualTo(0.5f).Within(Tolerance),
                "0.5 is PhisBullet.as:23's own default, which WThrow overrides. Shipping it made every "
                + "bounce 25% more energetic than AS3.");
        }

        [Test]
        public void ThrowFloorDamping_IsWThrowsValue_NotTheBulletClassDefault()
        {
            Assert.AreEqual(0.6f, ProjectilePhysicsMath.ThrowFloorDamping, Tolerance,
                "WThrow.as:24 declares tormoz = 0.6 and WThrow.as:189 assigns it onto the bullet.");

            Assert.That(ProjectilePhysicsMath.ThrowFloorDamping, Is.Not.EqualTo(0.7f).Within(Tolerance),
                "0.7 is PhisBullet.as:25's own default, which WThrow overrides. Shipping it damped "
                + "horizontal speed 17% harder than AS3.");
        }

        [Test]
        public void BrakePxPerFrame2_IsTwo()
        {
            // Both classes that apply `dx -= brake` declare it as 2, and WThrow assigns 2 either way,
            // so unlike skok/tormoz this one was already correct — pinned so it stays that way.
            Assert.AreEqual(2f, ProjectilePhysicsMath.BrakePxPerFrame2, Tolerance,
                "Trasser.as:45 and WThrow.as:20 both declare brake = 2.");
        }

        // ── The bullet view's stretch (Bullet.as:213-226) ─────────────────────
        //
        // The block the port never implemented at all: `springMode` was imported, stored on the
        // WeaponDefinition and read by nothing, so a `spring='2'` laser drew an ordinary round. The
        // three branches are pinned individually because two of them are easy to confuse — the beam
        // measures DISTANCE FROM THE ORIGIN and the smear measures VELOCITY.

        [Test]
        public void ViewStretch_IsTheOraclesOwnDivisor_NotAUnitConversion()
        {
            // Both AS3 branches end in `/100`, and that 100 is the length the art is authored at. It is
            // NOT PixelToUnit's inverse, even though that is also 100 — the two agree numerically and
            // must not be conflated, because one would survive a PPU change and the other would not.
            Assert.AreEqual(100f, ProjectilePhysicsMath.ViewStretchReferencePx, Tolerance,
                "Bullet.as:215 `... / 100` and Bullet.as:221 `vis.scaleX = this.vel / 100`.");
        }

        [Test]
        public void ViewStretch_WithABeamChild_ScalesByDistanceFromTheOrigin()
        {
            // `if(vis.laser && this.spring >= 2) vis.laser.scaleX = dist / 100`. 250px of travel is a
            // 2.5x beam; at the muzzle it is zero-length, which is what makes it grow out of the gun.
            Assert.AreEqual(2.5f,
                ProjectilePhysicsMath.BulletViewScaleX(hasBeamChild: true, spring: 2,
                                                       velocityPxPerFrame: 999f,
                                                       distanceFromOriginPx: 250f), Tolerance,
                "the beam spans the distance travelled — Bullet.as:215");

            Assert.AreEqual(0f,
                ProjectilePhysicsMath.BulletViewScaleX(true, 2, 999f, 0f), Tolerance,
                "…and is zero-length at the instant of firing, not velocity-sized");
        }

        [Test]
        public void ViewStretch_WithNoBeamChild_FallsThroughToTheOtherBranches()
        {
            // The gate is the CHILD's existence as well as `spring`, and this is the case the port is
            // in today: every prefab lacks the child, so a spring='2' weapon must NOT be stretched by
            // distance. Getting this wrong would stretch ordinary rounds as if they were beams.
            Assert.AreEqual(1f,
                ProjectilePhysicsMath.BulletViewScaleX(hasBeamChild: false, spring: 2,
                                                       velocityPxPerFrame: 50f,
                                                       distanceFromOriginPx: 250f), Tolerance,
                "no `laser` child means no beam, whatever spring says — Bullet.as:213");
        }

        [Test]
        public void ViewStretch_FastSpringOneRound_IsSmearedByVelocity_NotByDistance()
        {
            // `else if(this.spring == 1 && this.vel > 100) vis.scaleX = this.vel / 100`. The threshold
            // is on the AS3-space speed in px/frame, so 100 is inclusive-exclusive at the boundary:
            // vel == 100 must NOT smear (the oracle's `>` is strict), and the distance must be ignored.
            Assert.AreEqual(2f,
                ProjectilePhysicsMath.BulletViewScaleX(false, 1, 200f, 0f), Tolerance,
                "a fast spring-1 round smears by velocity — Bullet.as:221");

            Assert.AreEqual(1f,
                ProjectilePhysicsMath.BulletViewScaleX(false, 1, 100f, 0f), Tolerance,
                "vel == 100 is NOT > 100, so it stays unstretched");

            Assert.AreEqual(2f,
                ProjectilePhysicsMath.BulletViewScaleX(false, 1, 200f, 9999f), Tolerance,
                "…and the smear does not depend on how far the round has flown");
        }

        // ── Which transform the stretch writes (Bullet.as:213-227) ───────────────

        /// <summary>
        /// The beam arm targets the CHILD; the smear and the reset target the WHOLE VIEW. These are
        /// different transforms, so a rule that returned only a value could not express the block.
        /// </summary>
        [Test]
        public void ViewStretchTarget_BeamGoesToTheChild_TheOtherArmsToTheView()
        {
            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.BeamChild,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: true, spring: 2,
                                                            velocityPxPerFrame: 0f, hasDetonated: false),
                "a beam stretches `vis.laser` — Bullet.as:215");

            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.View,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: false, spring: 1,
                                                            velocityPxPerFrame: 200f, hasDetonated: false),
                "a smear stretches the whole view — Bullet.as:221");

            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.View,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: false, spring: 0,
                                                            velocityPxPerFrame: 0f, hasDetonated: false),
                "and everything else resets the view to natural size — Bullet.as:226");
        }

        /// <summary>
        /// <b>The one arm that writes nothing.</b> AS3's smear branch is
        /// <c>else if(spring == 1 &amp;&amp; vel &gt; 100) { if(!babah) vis.scaleX = vel/100; }</c> — so a round
        /// that has already detonated falls through the inner <c>if</c> and out of the chain entirely,
        /// leaving the last smear standing. Resetting it to 1 instead would be a visible pop.
        ///
        /// <para>The paired assertion is the control: the <i>same</i> inputs with <c>babah</c> false
        /// must target the view, or "None" would be indistinguishable from "this arm never runs".</para>
        /// </summary>
        [Test]
        public void ViewStretchTarget_DetonatedRoundInTheSmearArm_WritesNothing_NotAReset()
        {
            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.None,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: false, spring: 1,
                                                            velocityPxPerFrame: 200f, hasDetonated: true),
                "babah inside the smear arm leaves the previous smear alone — Bullet.as:219");

            // Control: identical but alive ⇒ the view, so `None` is not just "unreachable".
            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.View,
                ProjectilePhysicsMath.BulletViewScaleTarget(false, 1, 200f, hasDetonated: false),
                "…and the very same inputs with a live round DO write the view");
        }

        /// <summary>
        /// The beam arm wins on ORDER, and it is gated on the child as well as on <c>spring</c> — so a
        /// <c>spring &gt;= 2</c> weapon with no <c>laser</c> child must fall through to the view reset
        /// rather than being stretched by distance.
        /// </summary>
        [Test]
        public void ViewStretchTarget_SpringTwoWithNoBeamChild_FallsThroughToTheViewReset()
        {
            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.View,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: false, spring: 2,
                                                            velocityPxPerFrame: 999f, hasDetonated: false),
                "no `laser` child means no beam, whatever spring says — Bullet.as:213");

            // The velocity does not smuggle it into the smear arm either: that arm requires spring == 1.
            Assert.AreEqual(1f,
                ProjectilePhysicsMath.BulletViewScaleX(hasBeamChild: false, spring: 2,
                                                       velocityPxPerFrame: 999f, distanceFromOriginPx: 0f),
                Tolerance, "spring 2 with no child is a plain reset, not a velocity smear");
        }

        /// <summary>
        /// The beam arm outranks the smear arm: with a child and <c>spring &gt;= 2</c>, a speed above the
        /// smear threshold must still stretch the child by distance.
        /// </summary>
        [Test]
        public void ViewStretchTarget_BeamOutranksTheSmear()
        {
            Assert.AreEqual(ProjectilePhysicsMath.ViewScaleTarget.BeamChild,
                ProjectilePhysicsMath.BulletViewScaleTarget(hasBeamChild: true, spring: 2,
                                                            velocityPxPerFrame: 999f, hasDetonated: true),
                "the first matching branch wins, and `babah` only guards the smear — Bullet.as:213");
        }

        // ── The thrown object's spin ─────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>WThrow.as:192</c> — <c>dr = this.throwTip == 2 ? 0 : b.dx</c>. A sticky throwable
        /// (<c>throwTip == 2</c>: the dynamite/bomb family) is given no spin at all, and nothing later
        /// can start one, so the two other inputs must not be able to override it.
        /// </summary>
        [Test]
        public void Spin_StickyThrowable_NeverSpins_WhateverItsVelocity()
        {
            Assert.AreEqual(0f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(true, false, 18f, 18f), Tolerance,
                "a throwTip==2 object is spawned with dr = 0 — WThrow.as:192");

            Assert.AreEqual(0f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(true, true, 18f, 9f), Tolerance,
                "…and settling must not start a spin it was never given");
        }

        /// <summary>
        /// AS3 <c>PhisBullet.as:73</c> — <c>if(stay) { …this.dr = dx }</c>, i.e. while resting the spin
        /// follows the brake-decayed horizontal speed, which is what lets a grenade stop rolling.
        /// </summary>
        [Test]
        public void Spin_WhileResting_FollowsTheDecayingHorizontalSpeed()
        {
            // In flight: the spawn value. Resting: the live value, which the resting brake is shrinking.
            Assert.AreEqual(18f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(false, false, 18f, 7f), Tolerance,
                "in flight dr is the SPAWN dx, not the live one — WThrow.as:192");

            Assert.AreEqual(7f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(false, true, 18f, 7f), Tolerance,
                "once resting dr follows the live dx — PhisBullet.as:73");
        }

        /// <summary>
        /// The regression this function exists to prevent: a bounce flips the sign of <c>dx</c>, and
        /// AS3 does <b>not</b> refresh <c>dr</c> when it does. Recomputing the spin from the live
        /// velocity every frame would reverse the spin at every wall — a visible difference.
        /// </summary>
        [Test]
        public void Spin_AfterABounce_KeepsTheSpawnSign_NotTheLiveOne()
        {
            // Spawned travelling right (+18). It hits a wall and rebounds, so the live dx is now -18.
            // The oracle keeps spinning the way it started.
            Assert.AreEqual(18f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(false, false, 18f, -18f), Tolerance,
                "dr is NOT refreshed on a bounce — the sign stays as spawned");

            // The control: the same live value, once resting, IS used — so the test above is not
            // passing merely because the function ignores its last argument.
            Assert.AreEqual(-18f,
                ProjectilePhysicsMath.ThrownSpinDeltaDegrees(false, true, 18f, -18f), Tolerance,
                "…but the resting branch does read it, so the argument is not ignored");
        }
    }
}

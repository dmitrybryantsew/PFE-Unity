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
                "`× FlashFps` is the VELOCITY idiom; using it on an acceleration is 3.33× too strong.");
            Assert.That(ProjectilePhysicsMath.AccelerationScale, Is.Not.EqualTo(0.3f).Within(Tolerance),
                "`× FlashFps / PPU` is also the velocity idiom; using it on an acceleration is 30× too weak.");
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
                "0.8 × FlashFps = 24 is the velocity idiom: 3.33× too strong.");
        }

        [Test]
        public void BulletAcceleration_FlameWeak_IsOnePointEight_NotSix()
        {
            Vector2 a = ProjectilePhysicsMath.BulletAcceleration(Vector2.up, 0f, 0f, 2);
            Assert.AreEqual(1.8f, a.y, Tolerance,
                "Weapon.as:1551 lift is 0.2 px/frame² upward → 0.2 × 9 = 1.8 units/s².");

            Assert.That(a.y, Is.Not.EqualTo(6f).Within(Tolerance),
                "0.2 × FlashFps = 6 is the velocity idiom: 3.33× too strong.");
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
                "`accel × FlashFps` = 30 is the velocity idiom: 3.33× too strong.");
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
                "The port's `brake / FlashFps * 60f` = 4 was neither idiom: 4.5× too weak.");
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
                "The port's `0.5f / FlashFps * 60f` = 1.0 was 1.667× too high.");
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
    }
}

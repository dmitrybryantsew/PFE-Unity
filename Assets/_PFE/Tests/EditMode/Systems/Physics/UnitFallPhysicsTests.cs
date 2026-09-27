using NUnit.Framework;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins the last site of the gravity census: the motor-less unit path in
    /// <c>UnitController</c>, which held a hand-derived <c>GRAVITY = 30.0f</c> and a
    /// <c>FRICTION_GROUND</c> scaled by a hardcoded <c>60f</c>.
    ///
    /// <para><b>Every assertion names the wrong value it guards against.</b> The reason four
    /// divergent gravity literals coexisted in this port for years is that none of them was
    /// assertable — while the arithmetic lives inside a <c>MonoBehaviour</c>, the test needs a
    /// GameObject, a collider and a running engine, so it never gets written. Extracting it to
    /// <see cref="UnitFallPhysics"/> is what makes this file possible, and the wrong-value names are
    /// what make a future regression fail with its reason attached instead of as a gameplay bug
    /// someone has to bisect.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitFallPhysicsTests
    {
        private const float Tolerance = 1e-3f;

        // ── The constants ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>World.ddy = 1</c> px/frame² (<c>World.as:46</c>) converted to units/s².
        ///
        /// <para>The two named wrong values are the two idioms the port actually shipped: <c>30f</c>
        /// is <c>× fps</c> (a per-frame <i>velocity</i> factor applied to an acceleration — 3.33×
        /// strong, and literally the value in <c>UnitController</c> until this change), and
        /// <c>0.3f</c> is <c>× fps / PPU</c> (30× weak).</para>
        /// </summary>
        [Test]
        public void GravityUnitsPerSecondSquared_IsOnePixelPerFrameSquared()
        {
            Assert.That(UnitFallPhysics.GravityUnitsPerSecondSquared, Is.EqualTo(9f).Within(Tolerance),
                "1 px/frame² x 0.01 units/px x 30² = 9 units/s².");

            Assert.That(UnitFallPhysics.GravityUnitsPerSecondSquared,
                Is.Not.EqualTo(30f).Within(Tolerance),
                "30 is `x fps` — the per-frame VELOCITY idiom applied to an acceleration, 3.33x too " +
                "strong. It was UnitController.GRAVITY's value.");

            Assert.That(UnitFallPhysics.GravityUnitsPerSecondSquared,
                Is.Not.EqualTo(0.3f).Within(Tolerance),
                "0.3 is `x fps / PPU` — the correct idiom for a per-frame velocity, 30x too weak here.");
        }

        /// <summary>
        /// <c>World.maxdy = 20</c> px/frame (<c>World.as:48</c>) is a <b>velocity</b>, so it converts
        /// by the per-frame velocity factor. This assertion is structural rather than numeric: it
        /// says the clamp really is <c>World.maxdy</c>, not a number someone liked.
        /// </summary>
        [Test]
        public void TerminalFallSpeed_IsWorldMaxDy_NotAnInventedNumber()
        {
            Assert.That(
                UnitFallPhysics.TerminalFallSpeed / TileQueryConstants.PerFrameVelocityToUnitsPerSecond,
                Is.EqualTo(TileQueryConstants.MaxDy).Within(Tolerance),
                "The clamp must be AS3's World.maxdy (20 px/frame) expressed in units/s.");

            Assert.That(UnitFallPhysics.TerminalFallSpeed, Is.EqualTo(6f).Within(Tolerance),
                "20 px/frame x 0.3 = 6 units/s — the same clamp RoomObjectPhysicsLayer applies to " +
                "props as 600 px/s.");

            Assert.That(UnitFallPhysics.TerminalFallSpeed, Is.Not.EqualTo(20f).Within(Tolerance),
                "20 is World.maxdy read as units/s — px/frame mistaken for units/s, 3.33x too fast.");
        }

        /// <summary>
        /// AS3 <c>brake = 1</c> px/frame (<c>Unit.as:230</c>) is subtracted from <c>dx</c> once per
        /// frame, so it is a px/frame² acceleration. The named wrong value, <c>60f</c>, is what the
        /// port actually used: <c>1.0f * deltaTime * 60f</c>, wrong frame rate <i>and</i> wrong
        /// idiom, netting 6.67× too strong.
        /// </summary>
        [Test]
        public void BrakeUnitsPerSecondSquared_IsOnePixelPerFrameSquared()
        {
            Assert.That(UnitFallPhysics.BrakeUnitsPerSecondSquared, Is.EqualTo(9f).Within(Tolerance),
                "brake 1 px/frame² x 9 = 9 units/s².");

            Assert.That(UnitFallPhysics.BrakeUnitsPerSecondSquared,
                Is.Not.EqualTo(60f).Within(Tolerance),
                "60 is the old `x fps * 60f` scaling — 6.67x too strong, and 60 is not AS3's frame rate.");
        }

        // ── FallSpeed ────────────────────────────────────────────────────────────────────────

        [Test]
        public void FallSpeed_AcceleratesAtGravity_OverWallClockTime()
        {
            float velocity = 0f;

            // 0.5 s at 30 Hz. Half a second of gravity is 4.5 units/s, still under the 6 units/s
            // clamp, so this measures the slope rather than the clamp.
            for (int i = 0; i < 15; i++)
            {
                velocity = UnitFallPhysics.FallSpeed(velocity, 1f / 30f);
            }

            Assert.That(velocity, Is.EqualTo(-4.5f).Within(Tolerance),
                "9 units/s² x 0.5 s = 4.5 units/s downward.");
        }

        /// <summary>
        /// The claim the <c>UnitController</c> doc comment makes, asserted rather than asserted-in-
        /// prose: an acceleration integrated against any fixed delta gives the same speed after the
        /// same <i>wall-clock</i> time. That is why running this path at Unity's 50 Hz
        /// <c>FixedUpdate</c> instead of the sim's 30 Hz tick is a cadence difference and not a
        /// magnitude one.
        /// </summary>
        [Test]
        public void FallSpeed_IsRateIndependent_OverTheSameWallClockTime()
        {
            float at30 = 0f;
            float at50 = 0f;

            for (int i = 0; i < 3; i++) at30 = UnitFallPhysics.FallSpeed(at30, 1f / 30f);   // 0.1 s
            for (int i = 0; i < 5; i++) at50 = UnitFallPhysics.FallSpeed(at50, 1f / 50f);   // 0.1 s

            Assert.That(at30, Is.EqualTo(at50).Within(Tolerance),
                "0.1 s of falling is 0.9 units/s whatever the step size — if this fails, the step " +
                "size has leaked into the rate and the frame rate is being applied twice.");
            Assert.That(at30, Is.EqualTo(-0.9f).Within(Tolerance),
                "9 units/s² x 0.1 s = 0.9 units/s.");
        }

        [Test]
        public void FallSpeed_ClampsAtTerminalVelocity()
        {
            // Already at the clamp: AS3's gate is `dy < World.maxdy`, so it stops adding rather than
            // snapping the velocity — an object launched downward faster than terminal keeps it.
            Assert.That(UnitFallPhysics.FallSpeed(-UnitFallPhysics.TerminalFallSpeed, 1f / 30f),
                Is.EqualTo(-UnitFallPhysics.TerminalFallSpeed).Within(Tolerance),
                "At the clamp, gravity must add nothing.");

            Assert.That(UnitFallPhysics.FallSpeed(-7f, 1f / 30f), Is.EqualTo(-7f).Within(Tolerance),
                "Past the clamp, AS3 does not slow the object back down to maxdy — the gate is one-way.");

            // Just short of the clamp: one more step must land exactly on it, never past it.
            Assert.That(UnitFallPhysics.FallSpeed(-5.9f, 1f / 30f),
                Is.EqualTo(-UnitFallPhysics.TerminalFallSpeed).Within(Tolerance),
                "A step that would overshoot the clamp lands on the clamp. Without this the port's " +
                "fall speed was unbounded, which is the same omission RoomObjectPhysicsLayer had.");
        }

        // ── GroundBrake ──────────────────────────────────────────────────────────────────────

        [Test]
        public void GroundBrake_RemovesOneSecondOfBrakePerSecond()
        {
            float velocity = 30f;

            for (int i = 0; i < 30; i++)
            {
                velocity = UnitFallPhysics.GroundBrake(velocity, 1f / 30f);
            }

            Assert.That(velocity, Is.EqualTo(21f).Within(Tolerance),
                "9 units/s² for 1 s removes 9 units/s: 30 -> 21.");
        }

        [Test]
        public void GroundBrake_StopsAtZero_RatherThanReversing()
        {
            Assert.That(UnitFallPhysics.GroundBrake(0.1f, 1f / 30f), Is.EqualTo(0f).Within(Tolerance),
                "A reduction larger than the remaining speed must clamp to zero, not flip the sign — " +
                "the old inline code did this with an explicit `if (x < 0) x = 0` and that guard " +
                "must survive the extraction.");

            Assert.That(UnitFallPhysics.GroundBrake(-0.1f, 1f / 30f), Is.EqualTo(0f).Within(Tolerance),
                "Same in the negative direction.");

            Assert.That(UnitFallPhysics.GroundBrake(0f, 1f / 30f), Is.EqualTo(0f).Within(Tolerance),
                "A resting unit stays at rest.");
        }

        [Test]
        public void GroundBrake_DoesNotDependOnSign()
        {
            Assert.That(UnitFallPhysics.GroundBrake(5f, 1f / 30f),
                Is.EqualTo(-UnitFallPhysics.GroundBrake(-5f, 1f / 30f)).Within(Tolerance),
                "Braking is symmetric: it must slow motion toward zero whichever way the unit faces.");
        }
    }
}

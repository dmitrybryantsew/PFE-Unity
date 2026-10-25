using NUnit.Framework;
using PFE.Character.Animation;
using PFE.Entities.Player;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins the swim impulse against the oracle's own arithmetic, and against the numbers the shipped
    /// build produced.
    ///
    /// <para>The point of these fixtures is the <b>comparison</b>, not the constants: a swim speed is
    /// only wrong in relation to something. So each one states the AS3 value it is derived from and
    /// the value the previous implementation produced, and asserts where the new one lands between
    /// them. A future edit that "tidies" the impulse back into an assigned speed fails here.</para>
    /// </summary>
    [TestFixture]
    public class SwimMathTests
    {
        /// <summary>littlepip's <c>speed</c> — <c>AllData.as:22</c> <c>&lt;move speed='7' accel='3.5'/&gt;</c>.</summary>
        private const float LittlepipWalkSpeed = 7f;

        /// <summary>The same row's <c>accel='3.5'</c>, i.e. <c>plavdy = accel * 0.5</c>.</summary>
        private const float LittlepipPlavdy = 1.75f;

        /// <summary>The motor's <c>swimGravityScale</c> (<c>Player.prefab</c>).</summary>
        private const float SwimGravityScale = 0.35f;

        [Test]
        public void UpImpulse_ReproducesTheOraclesPlavdy()
        {
            // AS3 UnitPlayer.as:360 — `plavdy = accel * 0.5`, with accel = 3.5 for littlepip.
            float impulse = SwimMath.UpImpulsePixelsPerFrame(LittlepipWalkSpeed);

            Assert.That(impulse, Is.EqualTo(LittlepipPlavdy).Within(1e-5f),
                "the swim impulse must be the oracle's plavdy (accel / 2 = 1.75 px/frame)");
        }

        [Test]
        public void UpImpulse_IsNotDerivedFromTheStaleAccelerationField()
        {
            // The control for the previous fixture: UnitDef_Littlepip.asset says acceleration: 2,
            // while AllData.as:22 says accel='3.5'. Sourcing the impulse from the asset's field would
            // give 1.0, and this is what that would look like. Asserting the WRONG number keeps the
            // reason for the walk-speed derivation visible instead of implicit.
            float fromStaleField = 2f * 0.5f;

            Assert.That(fromStaleField, Is.Not.EqualTo(SwimMath.UpImpulsePixelsPerFrame(LittlepipWalkSpeed)).Within(1e-3f),
                "the asset's acceleration: 2 does not match AllData's accel='3.5' — if it is ever " +
                "corrected, this fixture should be deleted, not the derivation changed");
        }

        [Test]
        public void DownImpulse_IsExactlyHalfTheAscent()
        {
            // AS3 UnitPlayer.as:2873 — `dy += plavdy * mult / 2` for keySit.
            Assert.That(SwimMath.DownImpulsePixelsPerFrame(LittlepipWalkSpeed),
                Is.EqualTo(SwimMath.UpImpulsePixelsPerFrame(LittlepipWalkSpeed) * 0.5f).Within(1e-6f));
        }

        [Test]
        public void TerminalUpSpeed_IsFasterThanTheShippedBuildAndThanTheAs3Derivation()
        {
            float impulse = SwimMath.UpImpulsePixelsPerFrame(LittlepipWalkSpeed);
            float terminal = SwimMath.TerminalSpeedPixelsPerFrame(impulse, SwimGravityScale);

            // The shipped build: `walkSpeed * 0.5` ASSIGNED as a desired speed, then damped by the
            // same water drag in the same step. That product is what the player actually moved at.
            float shipped = LittlepipWalkSpeed * 0.5f * SwimMath.WaterDragPerFrame;

            // The oracle's own terminal, from its own ordering (impulse applied after the drag) and
            // its own swim gravity of 1: v = 4 - 5 * plavdy.
            float as3Terminal = 4f - 5f * LittlepipPlavdy;

            Assert.That(shipped, Is.EqualTo(2.8f).Within(1e-4f),
                "the old build swam up at 2.8 px/frame — this is the number the report was about");
            Assert.That(terminal, Is.EqualTo(5.6f).Within(1e-4f),
                "the impulse converges on 4 * (plavdy - swimGravity) = 5.6 px/frame");
            Assert.That(System.Math.Abs(terminal), Is.GreaterThan(System.Math.Abs(shipped) * 1.9f),
                "and it is at least twice the old speed, which is the whole point of the change");
            Assert.That(System.Math.Abs(terminal), Is.GreaterThan(System.Math.Abs(as3Terminal)),
                "it also exceeds the oracle's 4.75, because this motor's swim gravity is 0.35 where " +
                "AS3's ddyPlav is 1 — recorded so the overshoot is a decision, not a surprise");
        }

        [Test]
        public void TerminalUpSpeed_CollapsesToZeroWhenTheImpulseOnlyMatchesGravity()
        {
            // Guard against a formula that ignores gravity: an impulse equal to the swim gravity
            // cannot lift anything, and a `drag * impulse / (1 - drag)` would claim it lifts at 4x.
            Assert.That(SwimMath.TerminalSpeedPixelsPerFrame(0.35f, 0.35f), Is.EqualTo(0f).Within(1e-5f));

            // ...and an impulse BELOW gravity sinks, which is the sink a non-swimmer gets for free.
            Assert.That(SwimMath.TerminalSpeedPixelsPerFrame(0.1f, 0.35f), Is.LessThan(0f));
        }

        // ── The water splash (Unit.as:2671-2686) ─────────────────────────────────────────────

        /// <summary>
        /// The band between the two water samples: <c>CheckWater</c> reads 0.75·h above the feet for full
        /// submersion and 0.25·h for "in water", so the two lines are <c>(0.75 - 0.25) · 50 px</c> apart.
        /// </summary>
        private const float WaterSampleBandPixels = 25f;

        [Test]
        public void WaterSplash_IsSilentAtTheSpeedASurfaceBobCrosses()
        {
            // The whole reason AS3 gates the splash on |dy| > 5, derived rather than asserted: a
            // swimmer leaves the fully-submerged line at the swim terminal, coasts the band under the
            // motor's swim gravity, and arrives at the "in water" line at this speed.
            float terminal = SwimMath.TerminalSpeedPixelsPerFrame(
                SwimMath.UpImpulsePixelsPerFrame(LittlepipWalkSpeed), SwimGravityScale);
            float crossingSpeed = SwimMath.CoastSpeedAfterPixels(terminal, SwimGravityScale, WaterSampleBandPixels);

            Assert.That(crossingSpeed, Is.EqualTo(3.72f).Within(0.01f),
                "5.6 px/frame decelerating at 0.35 over 25 px arrives at 3.72");
            Assert.That(crossingSpeed, Is.LessThan(SwimMath.SplashMinVerticalSpeedPixelsPerFrame),
                "…which is under the oracle's 5, so the bob is silent — that is the gate's purpose, " +
                "not spam prevention bolted on afterwards");

            // Both directions, because the bob crosses the line twice per cycle.
            Assert.That(SwimMath.ShouldSplashLeavingWater(crossingSpeed), Is.False,
                "breaching at 3.72 must not splash; before the gate this fired on every stroke, and " +
                "the re-entry fired again — the reported glitch");
            Assert.That(SwimMath.ShouldSplashEnteringWater(-crossingSpeed), Is.False);
        }

        [Test]
        public void WaterSplash_OnlyFiresInTheDirectionOfTravel()
        {
            // Falling is NEGATIVE here: the oracle's Y runs down, this motor's does not. Comparing a
            // Unity velocity against AS3's `dy > 5` literal would silence every plunge and splash on
            // every breach.
            Assert.That(SwimMath.ShouldSplashEnteringWater(-12f), Is.True, "a fall into water splashes");
            Assert.That(SwimMath.ShouldSplashLeavingWater(-12f), Is.False, "…and is not an exit");
            Assert.That(SwimMath.ShouldSplashLeavingWater(7f), Is.True, "a breach splashes");
            Assert.That(SwimMath.ShouldSplashEnteringWater(7f), Is.False, "…and is not an entry");

            // AS3 tests `dy > 5` strictly, so exactly 5 is silent in both directions.
            Assert.That(SwimMath.ShouldSplashEnteringWater(-5f), Is.False);
            Assert.That(SwimMath.ShouldSplashLeavingWater(5f), Is.False);
        }

        [Test]
        public void WaterSplashVolume_IsTheOraclesDyOverTen()
        {
            Assert.That(SwimMath.SplashVolume(-6f), Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(SwimMath.SplashVolume(8f), Is.EqualTo(0.8f).Within(1e-5f));

            // A plunge at 50 px/frame is 5.0 in the oracle, which Flash clamps. So does this — the
            // port's only addition to the rule, and it exists so the audible result does not depend on
            // an engine detail.
            Assert.That(SwimMath.SplashVolume(-50f), Is.EqualTo(1f));
        }

        // ── The state's wiring, not just the arithmetic ──────────────────────────────────────

        private sealed class RecordingSink : IAnimationEventSink
        {
            public readonly System.Collections.Generic.List<(string Id, float Volume)> Events = new();

            public void FireEvent(string eventId, Vector2 worldPosition, float volumeScale = 1f)
            {
                Events.Add((eventId, volumeScale));
            }
        }

        private static PlayerMovementSnapshot InWater(float velocityY)
        {
            return new PlayerMovementSnapshot(
                PlayerLocomotionState.Swim,
                new Vector2(0f, velocityY),
                new Vector2(220f, 40f),
                isGrounded: false,
                isInWater: true,
                isFullySubmerged: true,
                isOnLadder: false,
                isDashing: false,
                isCrouching: false,
                isRunning: false,
                facingDirection: 1,
                aimAngle: 0f,
                justDoubleJumped: false,
                justLanded: false,
                normalizedMoveSpeed: 0f,
                isLevitating: false,
                justTeleported: false);
        }

        [Test]
        public void SwimState_SurfaceBob_FiresNoSplash()
        {
            var state = new SwimAnimState();
            var sink = new RecordingSink();

            // The velocity is read from the snapshot CanEnter was shown — that is the driver's order,
            // and the value has to be this frame's for the gate to mean anything.
            state.CanEnter(InWater(3.72f));
            state.OnEnter("fall", sink);

            state.CanEnter(InWater(-3.72f));
            state.OnExit("fall", sink);

            Assert.That(sink.Events, Is.Empty,
                "a swimmer bobbing at the surface must be silent in both directions");
        }

        [Test]
        public void SwimState_PlungeAndBreach_FireOneSplashEachAtTheOraclesVolume()
        {
            var state = new SwimAnimState();
            var sink = new RecordingSink();

            state.CanEnter(InWater(-6f));
            state.OnEnter("fall", sink);

            state.CanEnter(InWater(7f));
            state.OnExit("fall", sink);

            Assert.That(sink.Events, Has.Count.EqualTo(1 + 1),
                "one entry splash and one exit splash, not one per frame");
            Assert.That(sink.Events[0].Id, Is.EqualTo(AnimEvent.WaterEnter));
            Assert.That(sink.Events[0].Volume, Is.EqualTo(0.6f).Within(1e-5f), "6 / 10");
            Assert.That(sink.Events[1].Id, Is.EqualTo(AnimEvent.WaterExit));
            Assert.That(sink.Events[1].Volume, Is.EqualTo(0.7f).Within(1e-5f), "7 / 10");
        }
    }
}

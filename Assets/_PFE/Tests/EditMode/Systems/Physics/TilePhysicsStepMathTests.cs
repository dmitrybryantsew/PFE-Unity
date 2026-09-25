using NUnit.Framework;
using PFE.Core;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins the arithmetic that separates the legacy motor path from the sim-driven one. These three
    /// numbers are the whole of the P1 speed fix, so they are tested in isolation rather than through
    /// a room, a GameObject and a running engine.
    /// </summary>
    [TestFixture]
    public class TilePhysicsStepMathTests
    {
        private const float Fixed50Hz = 0.02f;

        // ── The bug being fixed ──────────────────────────────────────────────

        [Test]
        public void LegacyPath_ReproducesTheKnown2xSpeedError()
        {
            // 1.2 canonical frames per step, at 50 steps/s = 60 px/s, where AS3 moves 30 px/s.
            float framesPerStep = TilePhysicsStepMath.PositionFramesPerStep(
                simDriven: false, fixedDeltaTime: Fixed50Hz, stepScale: 1f);

            Assert.That(framesPerStep, Is.EqualTo(1.2f).Within(1e-5f));

            float pixelsPerSecond = framesPerStep * (1f / Fixed50Hz);
            Assert.That(pixelsPerSecond, Is.EqualTo(60f).Within(1e-3f),
                "the legacy path moves a unit of dx 60 px/s; AS3 moves it 30");
            Assert.That(pixelsPerSecond / SimClock.CanonicalTicksPerSecond, Is.EqualTo(2f).Within(1e-3f),
                "which is exactly 2x too fast");
        }

        [Test]
        public void LegacyRateScale_IsExactlyOne_SoLegacyBehaviourIsUnchanged()
        {
            // Gravity, acceleration and friction are applied once per step with no scaling today.
            // Returning exactly 1f is what makes the A/B honest: with the sim flag off, nothing moves.
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                float scale = TilePhysicsStepMath.RateFramesPerStep(
                    simDriven: false, stepScale: 30f / rate);

                Assert.That(scale, Is.EqualTo(1f), "legacy rate accumulation must not be scaled");
            }
        }

        [Test]
        public void LegacyGravity_IsAlsoTooStrong_BecauseItRunsAtTheWrongTickRate()
        {
            // The second, compounding error. Gravity is applied once per step; at 50 steps/s that is
            // 50 applications a second where AS3 applies it 30 times. 1.667x too strong, on top of
            // the 2x position error. They do not cancel — the game feels fast AND heavy.
            const int stepsPerSecond = 50;

            float legacyApplications = stepsPerSecond * TilePhysicsStepMath.RateFramesPerStep(false, 1f);
            float simApplications = SimClock.CanonicalTicksPerSecond;

            Assert.That(legacyApplications / simApplications, Is.EqualTo(5f / 3f).Within(1e-4f),
                "legacy gravity is 1.667x too strong");
        }

        // ── The fix ──────────────────────────────────────────────────────────

        [Test]
        public void SimPath_AtCanonicalRate_IsOneFramePerStep()
        {
            // One tick is one AS3 frame. This is the replica-exact configuration.
            float framesPerStep = TilePhysicsStepMath.PositionFramesPerStep(
                simDriven: true, fixedDeltaTime: Fixed50Hz, stepScale: 1f);

            Assert.That(framesPerStep, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(new SimClock(30).StepScale, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void SimPath_SpeedIsIndependentOfTickRate()
        {
            // The headline invariant: for every supported rate, frames-per-step times steps-per-second
            // is 30. So a unit of dx always covers 30 px/s, whatever the tick rate or the display.
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                float stepScale = new SimClock(rate).StepScale;

                float framesPerStep = TilePhysicsStepMath.PositionFramesPerStep(
                    simDriven: true, fixedDeltaTime: Fixed50Hz, stepScale: stepScale);

                float pixelsPerSecond = framesPerStep * rate;

                Assert.That(pixelsPerSecond, Is.EqualTo(30f).Within(1e-3f),
                    $"at {rate} Hz a unit of dx must still cover 30 px/s");
            }
        }

        [Test]
        public void SimPath_RateScaleEqualsStepScale()
        {
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                float stepScale = new SimClock(rate).StepScale;

                Assert.That(
                    TilePhysicsStepMath.RateFramesPerStep(true, stepScale),
                    Is.EqualTo(stepScale).Within(1e-6f));
            }
        }

        [Test]
        public void SimPath_IsFasterThanLegacy_AtEverySupportedRate()
        {
            // Guards against a sign/ratio slip that would make the "fix" slower than the bug.
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                float legacy = TilePhysicsStepMath.PositionFramesPerStep(false, Fixed50Hz, 1f) * 50f;
                float sim = TilePhysicsStepMath.PositionFramesPerStep(
                    true, Fixed50Hz, new SimClock(rate).StepScale) * rate;

                Assert.That(sim, Is.LessThan(legacy),
                    "the sim path must be slower than the 2x-fast legacy path");
            }
        }

        // ── Exponential decay ────────────────────────────────────────────────

        [Test]
        public void DecayOverStep_IsIdentityAtOneFrame()
        {
            // Always true on the legacy path, which is why legacy friction/water drag are unchanged.
            foreach (float factor in new[] { 0.7f, 0.95f, 0.8f, 0.5f })
            {
                Assert.That(
                    TilePhysicsStepMath.DecayOverStep(factor, 1f),
                    Is.EqualTo(factor),
                    "a one-frame step must apply the per-frame factor exactly once");
            }
        }

        [Test]
        public void DecayOverStep_HalfFrameIsSquareRoot()
        {
            // At 60 Hz a step covers half a canonical frame, so the correct factor is brake^0.5.
            Assert.That(
                TilePhysicsStepMath.DecayOverStep(0.7f, 0.5f),
                Is.EqualTo(0.83666f).Within(1e-4f));
        }

        [Test]
        public void DecayOverStep_ComposesToTheSameDecayOverOneFrame()
        {
            // The property that makes the exponent correct: applying f^h for 1/h steps must equal
            // applying f once. Without this, friction would make units stop faster at a higher tick
            // rate, which is a feel change, not a timing change.
            foreach (float h in new[] { 0.5f, 1f / 3f, 0.25f })
            {
                int steps = (int)System.Math.Round(1f / h);
                float perStep = TilePhysicsStepMath.DecayOverStep(0.7f, h);

                float accumulated = 1f;
                for (int i = 0; i < steps; i++)
                {
                    accumulated *= perStep;
                }

                Assert.That(accumulated, Is.EqualTo(0.7f).Within(1e-3f),
                    $"decay over {steps} steps of {h} canonical frames must equal one full frame");
            }
        }

        // ── Timers ───────────────────────────────────────────────────────────

        [Test]
        public void StepSeconds_SimPathUsesSimDt_SoTimerDurationIsTickRateIndependent()
        {
            Assert.That(
                TilePhysicsStepMath.StepSeconds(true, Fixed50Hz, 1f / 120f),
                Is.EqualTo(1f / 120f).Within(1e-6f),
                "the sim path must use SimDt, not Unity's fixed delta");

            Assert.That(
                TilePhysicsStepMath.StepSeconds(false, Fixed50Hz, 1f / 120f),
                Is.EqualTo(Fixed50Hz).Within(1e-6f),
                "the legacy path must keep using Unity's fixed delta, unchanged");
        }
    }
}

namespace PFE.Tests.Editor.Core
{
    using NUnit.Framework;
    using PFE.Core;

    /// <summary>
    /// Locks down the one invariant the whole P1 tick migration rests on: the canonical unit stays
    /// pixels-per-30Hz-frame, and the configured tick rate only ever enters through
    /// <see cref="SimClock.StepScale"/>.
    /// </summary>
    [TestFixture]
    public class SimClockTests
    {
        [Test]
        public void DefaultClock_IsCanonical_WithUnitStepScale()
        {
            var clock = new SimClock();

            Assert.That(clock.TicksPerSecond, Is.EqualTo(SimClock.CanonicalTicksPerSecond));
            Assert.That(clock.IsCanonical, Is.True, "the default must be replica-exact");
            Assert.That(clock.StepScale, Is.EqualTo(1f).Within(1e-6f),
                "at the canonical rate one tick is exactly one AS3 frame, so nothing may be scaled");
        }

        [Test]
        public void CanonicalRate_IsThirty()
        {
            // AS3 World.as:44 `public static const fps:* = 30`. If this ever changes, every
            // constant in REPLICA_BEHAVIOR_CONTRACT.md is invalid.
            Assert.That(SimClock.CanonicalTicksPerSecond, Is.EqualTo(30));
        }

        [Test]
        public void StepScale_AtEachSupportedRate_IsCanonicalOverRate()
        {
            var cases = new[]
            {
                (rate: 30, scale: 1f),
                (rate: 60, scale: 0.5f),
                (rate: 90, scale: 1f / 3f),
                (rate: 120, scale: 0.25f)
            };

            foreach ((int rate, float scale) in cases)
            {
                var clock = new SimClock(rate);

                Assert.That(clock.TicksPerSecond, Is.EqualTo(rate));
                Assert.That(clock.StepScale, Is.EqualTo(scale).Within(1e-5f),
                    $"a canonical rate must scale by 30/{rate} to become a per-tick advance");
                Assert.That(clock.IsCanonical, Is.EqualTo(rate == 30));
            }
        }

        [Test]
        public void StepScale_TimesRate_ReproducesCanonicalUnit()
        {
            // The defining property: canonical_rate * StepScale == configured_rate, i.e. a rate
            // expressed per canonical frame advances the same distance per second at any tick rate.
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                var clock = new SimClock(rate);
                float canonicalUnitsPerSecond = SimClock.CanonicalTicksPerSecond * clock.StepScale;

                Assert.That(canonicalUnitsPerSecond, Is.EqualTo((float)rate).Within(1e-4f),
                    $"30 canonical frames/sec scaled at {rate} Hz must still cover one second");
            }
        }

        [Test]
        public void SimDt_IsReciprocalOfRate()
        {
            Assert.That(new SimClock(30).SimDt, Is.EqualTo(1f / 30f).Within(1e-6f));
            Assert.That(new SimClock(60).SimDt, Is.EqualTo(1f / 60f).Within(1e-6f));
            Assert.That(new SimClock(120).SimDt, Is.EqualTo(1f / 120f).Within(1e-6f));
        }

        [Test]
        public void UnsupportedRate_SnapsToNearestSupported()
        {
            Assert.That(new SimClock(50).TicksPerSecond, Is.EqualTo(60));
            Assert.That(new SimClock(70).TicksPerSecond, Is.EqualTo(60));
            Assert.That(new SimClock(100).TicksPerSecond, Is.EqualTo(90));
            Assert.That(new SimClock(200).TicksPerSecond, Is.EqualTo(120));
            Assert.That(new SimClock(20).TicksPerSecond, Is.EqualTo(30));
        }

        [Test]
        public void ExactMidpoint_SnapsDown()
        {
            // 45 is equidistant from 30 and 60. The tie must resolve deterministically to the lower
            // rate, and this test is here so the choice is recorded rather than accidental.
            Assert.That(new SimClock(45).TicksPerSecond, Is.EqualTo(30));
        }

        [Test]
        public void NonPositiveRate_FallsBackToDefault()
        {
            Assert.That(new SimClock(0).TicksPerSecond, Is.EqualTo(SimClock.DefaultTicksPerSecond));
            Assert.That(new SimClock(-5).TicksPerSecond, Is.EqualTo(SimClock.DefaultTicksPerSecond));
        }

        [Test]
        public void SettingRate_ValidatesLikeTheConstructor()
        {
            var clock = new SimClock(30);

            clock.TicksPerSecond = 120;
            Assert.That(clock.TicksPerSecond, Is.EqualTo(120));
            Assert.That(clock.StepScale, Is.EqualTo(0.25f).Within(1e-6f));

            clock.TicksPerSecond = 0;
            Assert.That(clock.TicksPerSecond, Is.EqualTo(30), "a bad value must not brick the clock");
        }

        [Test]
        public void IsSupported_IsFalseOnlyAfterASnap()
        {
            Assert.That(new SimClock(60).IsSupported, Is.True);
            Assert.That(new SimClock(45).IsSupported, Is.False, "45 was snapped, so it is not a live rate");
        }

        [Test]
        public void SupportedRates_MirrorTheCommunityBuildOptions()
        {
            // The community build exposes 30 / 60 / 90 / 120. If a rate is added there, it must be
            // added here or the port cannot match it.
            Assert.That(SimClock.SupportedTicksPerSecond, Is.EqualTo(new[] { 30, 60, 90, 120 }));
        }
    }
}

using NUnit.Framework;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="NoiseMath"/> — AS3's acoustic model: <c>Unit.noise</c> / <c>makeNoise()</c> /
    /// <c>listen()</c> / <c>budilo()</c> and <c>UnitPlayer.observation()</c>.
    ///
    /// <para><b>Why this fixture exists.</b> The port shipped a hearing system that could not be wrong in
    /// a way anything would notice. It drew a fixed 320 px circle and asked whether the target was inside
    /// it, so "the enemy always knows where I am" was a constant, not a bug report with a location. The
    /// oracle's rule is a <i>product</i> — <c>noise × ear × earMult</c> — where <c>noise</c> is zero
    /// unless the target just did something loud. That is six lines of arithmetic, and the only way to
    /// keep it from silently regressing to a constant is to assert the six lines. Every case below names
    /// the oracle rule it pins, and the zero-noise case is the one the reported symptom reduces to.</para>
    ///
    /// <para><b>These tests are deliberately unkind to "sensible" defaults.</b> A helper that floors the
    /// radius so the AI still works, a comparison loosened from <c>&gt;</c> to <c>&gt;=</c>, a decay
    /// applied one tick early — each of them makes the system behave, and each of them is a different
    /// game. The fixture pins the boundaries rather than the middle, because the middle is the part that
    /// looks right either way.</para>
    /// </summary>
    [TestFixture]
    public sealed class NoiseMathTests
    {
        // ── The reported bug, reduced ───────────────────────────────────────────────────────────

        /// <summary>
        /// A target with <c>noise = 0</c> is inaudible <b>at zero distance</b>.
        ///
        /// <para>This is the whole bug report in one assertion. The oracle returns 0 before it computes a
        /// distance at all (<c>if(_loc2_ &lt;= 0) return 0;</c>), so a stationary target is not "heard
        /// faintly" — it is not heard. The port's old <c>CheckHearing</c> returned <c>true</c> for any
        /// target inside 320 px, which included a player standing perfectly still.</para>
        /// </summary>
        [Test]
        public void HearingIntensity_ZeroNoise_IsInaudibleEvenAtZeroDistance()
        {
            float atZero = NoiseMath.HearingIntensity(
                noise: 0, ear: 1f, earMultiplier: 1f, distanceSquaredPixels: 0f);

            Assert.That(atZero, Is.EqualTo(0f),
                "Unit.listen(): `if(_loc2_ <= 0) return 0;` — a silent target is unhearable at ANY range, " +
                "including point blank. This is the reported symptom.");
        }

        /// <summary>
        /// A <b>deaf</b> listener hears nothing, however loud the target.
        ///
        /// <para><c>ear = 0</c> is the second factor of the product, and it is authored in the shipped
        /// data for every small robot. A port that treats <c>ear</c> as a scale factor rather than a
        /// factor of a product will still hear at some minimum radius.</para>
        /// </summary>
        [Test]
        public void HearingIntensity_DeafListener_HearsNothingHoweverLoud()
        {
            Assert.That(NoiseMath.HearingIntensity(800, ear: 0f, earMultiplier: 1f, distanceSquaredPixels: 0f),
                Is.EqualTo(0f),
                "ear is a FACTOR: spritebot/vortex/roller author ear='0' and must be stone deaf.");

            Assert.That(NoiseMath.HearingIntensity(800, ear: 1f, earMultiplier: 0f, distanceSquaredPixels: 0f),
                Is.EqualTo(0f),
                "So is earMult — a muted room is silent regardless of what happens in it.");
        }

        /// <summary>
        /// The radius is the product, not a constant, and the constant the port used (320) sits between
        /// the oracle's walk and run values — so the port over-heard every case at once.
        /// </summary>
        [Test]
        public void HearingRadius_IsTheProduct_NotThePortsConstant()
        {
            Assert.That(NoiseMath.HearingRadius(200, 1f, 1f), Is.EqualTo(200f),
                "A full run (noiseRun default 200, Unit.as:122272) with an ordinary listener: 200 px.");
            Assert.That(NoiseMath.HearingRadius(50, 1f, 1f), Is.EqualTo(50f),
                "A walk (noiseRun/4) is a quarter of that — 1.25 tiles, not 8.");
            Assert.That(NoiseMath.HearingRadius(600, 1f, 1f), Is.EqualTo(600f),
                "A zombie's own noiseRun is 600 (<vis noise='600'/>), so it is the loud one.");
            Assert.That(NoiseMath.HearingRadius(200, 2.5f, 1f), Is.EqualTo(500f),
                "A sharp-eared unit (ear='2.5', the boss at :2761) hears 2.5x further.");
            Assert.That(NoiseMath.HearingRadius(200, 1f, 0.5f), Is.EqualTo(100f),
                "An easy-difficulty room (earMult halved, :32526) halves it again.");

            Assert.That(NoiseMath.HearingRadius(200, 1f, 1f), Is.Not.EqualTo(320f),
                "320 was the port's HearingRangePixels — larger than even a sprint (200), which is why " +
                "it over-heard in every case and infinitely when the player stood still.");
        }

        // ── listen(): the graded intensity ─────────────────────────────────────────────────────

        /// <summary>
        /// Intensity is <b>4</b> at the centre and falls to <b>0</b> at the rim — the graded curve the
        /// port replaced with a bool.
        /// </summary>
        [Test]
        public void HearingIntensity_IsGraded_NotBoolean()
        {
            const int noise = 200;   // radius 200
            const float r2 = noise * noise;

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, 0f), Is.EqualTo(4f),
                "d = 0 -> (1 - 0) * 4 = 4. The ceiling.");

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, r2 * 0.25f), Is.EqualTo(3f),
                "d = r/2 -> (1 - 0.25) * 4 = 3.");

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, r2 * 0.75f), Is.EqualTo(1f),
                "d = 0.866r -> (1 - 0.75) * 4 = 1 exactly — the commit threshold, and it does NOT commit.");

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, r2 * 0.9f), Is.LessThan(1f),
                "d = 0.949r -> 0.4. Heard, but only as suspicion.");
        }

        /// <summary>
        /// The rim test is <b>strict</b>: <c>r² &gt; d²</c>, so a target exactly on the boundary is
        /// inaudible.
        /// </summary>
        [Test]
        public void HearingIntensity_IsStrictAtTheRim()
        {
            const int noise = 200;
            const float r2 = noise * noise;

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, r2), Is.EqualTo(0f),
                "Exactly on the rim: `if(_loc2_ * _loc2_ > _loc3_)` is false, so the oracle returns 0.");

            Assert.That(NoiseMath.HearingIntensity(noise, 1f, 1f, r2 - 0.5f), Is.GreaterThan(0f),
                "A hair inside the rim is audible. Pinned because a `>=` rewrite would move this case " +
                "to the other side and nobody would see it in play.");
        }

        /// <summary>
        /// <c>if(_loc3_ &gt; 1)</c> — the gate that separates "react now" from "become suspicious".
        /// </summary>
        [Test]
        public void CommitsImmediately_IsStrictlyGreaterThanOne()
        {
            Assert.That(NoiseMath.CommitsImmediately(1f), Is.False,
                "Intensity exactly 1 (d = 0.866r) only feeds obs; it does not commit.");
            Assert.That(NoiseMath.CommitsImmediately(1.0001f), Is.True);
            Assert.That(NoiseMath.CommitsImmediately(4f), Is.True);
            Assert.That(NoiseMath.CommitsImmediately(0.99f), Is.False);
            Assert.That(NoiseMath.CommitsImmediately(0f), Is.False,
                "A silent target must never commit — this is the assertion the old bool API could not make.");
        }

        // ── makeNoise(): raise-only, and the decay ─────────────────────────────────────────────

        /// <summary>
        /// <c>if(this.noise &lt; param1) this.noise = param1;</c> — a quiet sound cannot cut a loud one
        /// short.
        /// </summary>
        [Test]
        public void MakeNoise_RaisesOnly()
        {
            Assert.That(NoiseMath.MakeNoise(0, 200), Is.EqualTo(200));
            Assert.That(NoiseMath.MakeNoise(200, 50), Is.EqualTo(200),
                "A footstep during a gunshot does not silence the gunshot.");
            Assert.That(NoiseMath.MakeNoise(50, 200), Is.EqualTo(200));
            Assert.That(NoiseMath.MakeNoise(200, 200), Is.EqualTo(200));
        }

        /// <summary>
        /// A non-positive amount is an <b>early out</b> — it does not even refresh the ripple cooldown
        /// (<c>:125044-125047</c>).
        /// </summary>
        [Test]
        public void MakeNoise_NonPositiveAmount_IsAnEarlyOut()
        {
            Assert.That(NoiseMath.MakeNoise(200, 0), Is.EqualTo(200),
                "AS3 returns BEFORE touching noise_t, so a zero call is not a 'silent noise'.");
            Assert.That(NoiseMath.MakeNoise(0, 0), Is.EqualTo(0));
            Assert.That(NoiseMath.MakeNoise(200, -50), Is.EqualTo(200));
        }

        /// <summary>
        /// The decay: 20 per tick, clamped at zero, and the timing that makes the whole mechanic feel like
        /// sound rather than a state.
        /// </summary>
        [Test]
        public void TickNoise_DecaysTwentyPerTick_AndClampsAtZero()
        {
            Assert.That(NoiseMath.TickNoise(200), Is.EqualTo(180));
            Assert.That(NoiseMath.TickNoise(20), Is.EqualTo(0));
            Assert.That(NoiseMath.TickNoise(10), Is.EqualTo(0),
                "Clamped, not negative — a negative noise is an inaudible AND wrong value.");
            Assert.That(NoiseMath.TickNoise(0), Is.EqualTo(0));

            // The durations the player actually feels, derived rather than restated.
            Assert.That(TicksToSilence(200), Is.EqualTo(10),
                "A full run (200) is audible for 10 ticks = 0.33 s after you stop.");
            Assert.That(TicksToSilence(50), Is.EqualTo(3),
                "A walk (50) for 3 ticks = 0.1 s.");
            Assert.That(TicksToSilence(400), Is.EqualTo(20),
                "A punch (weapon noise 400, :2194) for 20 ticks = 0.67 s — attacking is twice as loud as " +
                "sprinting, which is the whole shape of the stealth design.");
        }

        private static int TicksToSilence(int noise)
        {
            int ticks = 0;
            while (noise > 0 && ticks < 1000)
            {
                noise = NoiseMath.TickNoise(noise);
                ticks++;
            }

            return ticks;
        }

        // ── Movement noise: the thresholds ─────────────────────────────────────────────────────

        /// <summary>
        /// The three movement tiers, <c>&gt;12</c> / <c>&gt;7</c> / <c>&gt;3</c>, with the boundaries
        /// pinned on both sides.
        /// </summary>
        [Test]
        public void MovementNoise_SplitsIntoQuarter_Half_AndFull()
        {
            const int run = 200;

            Assert.That(NoiseMath.MovementNoise(run, 13f, grounded: true), Is.EqualTo(200), "> 12 -> full");
            Assert.That(NoiseMath.MovementNoise(run, 8f, grounded: true), Is.EqualTo(100), "> 7 -> half");
            Assert.That(NoiseMath.MovementNoise(run, 4f, grounded: true), Is.EqualTo(50), "> 3 -> quarter");

            Assert.That(NoiseMath.MovementNoise(run, -13f, grounded: true), Is.EqualTo(200),
                "The oracle tests both signs because dx is signed; the magnitude is what matters.");
        }

        /// <summary>
        /// The boundaries are <b>strict</b> and every one of them is reachable in the shipped data.
        /// </summary>
        [Test]
        public void MovementNoise_BoundariesAreStrict()
        {
            const int run = 200;

            Assert.That(NoiseMath.MovementNoise(run, 12f, grounded: true), Is.EqualTo(100),
                "Exactly 12 is NOT '> 12'. zombie1 runs at 13 px/frame but littlepip's authored speed is " +
                "7 — the boundaries are hit in practice, not theoretically.");

            Assert.That(NoiseMath.MovementNoise(run, 7f, grounded: true), Is.EqualTo(50),
                "Exactly 7 is NOT '> 7'. littlepip's own <move speed='7'/> sits here, so the player's " +
                "walk lands on the quarter tier and not the half.");

            Assert.That(NoiseMath.MovementNoise(run, 3f, grounded: true), Is.EqualTo(0),
                "Exactly 3 is NOT '> 3' — and it is not a makeNoise(0) call either: below this the " +
                "oracle calls makeNoise not at all.");
        }

        /// <summary>
        /// A stationary unit makes <b>no call</b>, which is what makes it go fully silent.
        /// </summary>
        [Test]
        public void MovementNoise_StationaryIsSilent()
        {
            Assert.That(NoiseMath.MovementNoise(200, 0f, grounded: true), Is.EqualTo(0));
            Assert.That(NoiseMath.MovementNoise(200, 0.4f, grounded: true), Is.EqualTo(0),
                "Sub-pixel drift is below the 3 px/frame floor.");
            Assert.That(NoiseMath.MovementNoise(200, 2.9f, grounded: true), Is.EqualTo(0));
        }

        /// <summary>
        /// The whole block is gated on AS3's <c>stay</c> — an airborne unit makes no movement noise.
        /// </summary>
        [Test]
        public void MovementNoise_IsGatedOnGrounded()
        {
            Assert.That(NoiseMath.MovementNoise(200, 20f, grounded: false), Is.EqualTo(0),
                "`if(stay && ...)` — jumping is a stealth tool in the original. Landing makes its own " +
                "noise instead (LandingNoise).");
        }

        /// <summary>
        /// <c>noiseRun / 4</c> truncates, as AS3's int coercion does. Pinned with an odd value because a
        /// power of two would hide the difference.
        /// </summary>
        [Test]
        public void MovementNoise_QuarterAndHalf_TruncateLikeAs3()
        {
            Assert.That(NoiseMath.MovementNoise(250, 4f, grounded: true), Is.EqualTo(62),
                "250 / 4 = 62.5 -> AS3 coerces to int by truncation, giving 62, not 63 and not 62.5.");
            Assert.That(NoiseMath.MovementNoise(250, 8f, grounded: true), Is.EqualTo(125));
            Assert.That(NoiseMath.MovementNoise(0, 20f, grounded: true), Is.EqualTo(0),
                "A slime with noiseRun = 0 is silent however fast it moves.");
        }

        // ── Landing noise ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>dy &gt; 16</c> is a full-noise landing, <c>dy &gt; 9</c> half, below that silent — with the
        /// terminal fall speed (20 px/frame) making the top tier reachable.
        /// </summary>
        [Test]
        public void LandingNoise_HasTwoTiers_AndIsSilentBelowNine()
        {
            Assert.That(NoiseMath.LandingNoise(200, 17f), Is.EqualTo(200), "> 16 -> full");
            Assert.That(NoiseMath.LandingNoise(200, 16f), Is.EqualTo(100),
                "Exactly 16 is NOT '> 16' — it takes the half tier.");
            Assert.That(NoiseMath.LandingNoise(200, 10f), Is.EqualTo(100), "> 9 -> half");
            Assert.That(NoiseMath.LandingNoise(200, 9f), Is.EqualTo(0), "Exactly 9 -> silent.");
            Assert.That(NoiseMath.LandingNoise(200, 8f), Is.EqualTo(0));
            Assert.That(NoiseMath.LandingNoise(200, 20f), Is.EqualTo(200),
                "20 is World.maxdy, the terminal fall speed — so a full-height drop is the loudest " +
                "possible landing and it IS reachable.");
        }

        // ── The ripple cooldown ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>noise_t == 0 || isEvent &amp;&amp; noise_t &lt;= 20</c> — continuous sources refresh only when
        /// the cooldown has expired; events also refresh in its last third.
        /// </summary>
        [Test]
        public void NoiseRipple_RefreshesDifferentlyForEventsAndContinuousMotion()
        {
            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(0, isEvent: false), Is.True,
                "Expired -> refresh, either way.");

            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(30, isEvent: false), Is.False,
                "A running unit does not re-ripple every tick.");
            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(21, isEvent: false), Is.False);
            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(20, isEvent: false), Is.False,
                "Exactly 20 does not refresh a continuous source — only an event.");
            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(20, isEvent: true), Is.True,
                "Two gunshots in quick succession draw two ripples.");
            Assert.That(NoiseMath.ShouldRefreshNoiseRipple(21, isEvent: true), Is.False,
                "But not three, four, five — the cap is real.");
        }

        // ── observation(): the suspicion accumulator ──────────────────────────────────────────

        /// <summary>
        /// The accumulator's four rules, in one place: add, re-arm the hold, clamp to twice the threshold,
        /// and commit at <c>&gt;=</c>.
        /// </summary>
        [Test]
        public void Observation_AccumulatesClampsAndCommitsAtMaxObs()
        {
            var state = default(NoiseMath.Observation);

            Assert.That(NoiseMath.IsObserved(state, NoiseMath.DefaultMaxObservation), Is.False,
                "obs starts at 0 (:139203).");

            state = NoiseMath.AddObservation(state, 4f, NoiseMath.DefaultMaxObservation);
            Assert.That(state.Value, Is.EqualTo(4f));
            Assert.That(state.HoldTicks, Is.EqualTo(30), "A positive observation re-arms isObs = 30.");
            Assert.That(NoiseMath.IsObserved(state, 20f), Is.False);

            for (int i = 0; i < 4; i++)
            {
                state = NoiseMath.AddObservation(state, 4f, 20f);
            }

            Assert.That(state.Value, Is.EqualTo(20f));
            Assert.That(NoiseMath.IsObserved(state, 20f), Is.True,
                "`obs >= maxObs` — inclusive, unlike every other boundary in this file.");

            state = NoiseMath.AddObservation(state, 400f, 20f);
            Assert.That(state.Value, Is.EqualTo(40f),
                "The ceiling is maxObs * 2 = 40, not 20 — so a fully-seen target can decay from 40 back " +
                "down to 20 and stay committed the whole way.");
        }

        /// <summary>
        /// A non-positive amount is ignored entirely — it must not re-arm the hold.
        /// </summary>
        [Test]
        public void Observation_IgnoresNonPositiveAmounts()
        {
            var state = NoiseMath.AddObservation(default, 5f, 20f);
            state = NoiseMath.TickObservation(state);

            int hold = state.HoldTicks;
            float value = state.Value;

            state = NoiseMath.AddObservation(state, 0f, 20f);

            Assert.That(state.HoldTicks, Is.EqualTo(hold), "0 does not re-arm isObs — `if(param1 > 0)`.");
            Assert.That(state.Value, Is.EqualTo(value));

            state = NoiseMath.AddObservation(state, -3f, 20f);
            Assert.That(state.Value, Is.EqualTo(value), "A negative observation is not a subtraction.");
        }

        /// <summary>
        /// The decay: 0.1 per tick, and it starts one tick <b>after</b> the hold reaches zero — the
        /// oracle's statement order, which a tidy-up would silently change.
        /// </summary>
        [Test]
        public void Observation_DecaysAtPointOnePerTick_StartingTheTickAfterTheHoldExpires()
        {
            var state = NoiseMath.AddObservation(default, 4f, 20f);   // value 4, hold 30

            // Burn the hold down. 30 ticks of counting, then the value starts moving.
            for (int i = 0; i < 30; i++)
            {
                state = NoiseMath.TickObservation(state);
                Assert.That(state.Value, Is.EqualTo(4f), $"obs must not decay while the hold is armed (tick {i}).");
            }

            Assert.That(state.HoldTicks, Is.EqualTo(0), "After 30 ticks the hold has just reached 0.");

            // The oracle tests `isObs <= 0` BEFORE it decrements, so this tick — the one where the hold is
            // exactly 0 on entry — is the first that decays.
            state = NoiseMath.TickObservation(state);
            Assert.That(state.Value, Is.EqualTo(3.9f).Within(1e-4f),
                "Decay begins on the tick the hold reads 0, not the one after it.");
            Assert.That(state.HoldTicks, Is.EqualTo(-1), "isObs is NOT clamped — it keeps counting down.");

            for (int i = 0; i < 39; i++)
            {
                state = NoiseMath.TickObservation(state);
            }

            // 4.0 / 0.1 is exactly 40 ticks on paper, but 0.1 is not representable in binary floating
            // point, so forty subtractions leave a positive residue of about 1.6e-06 rather than zero.
            // AS3 accumulates the same residue (its `Number` is also a binary float), which is why this
            // asserts a bound rather than equality — the alternative is a test that passes only because
            // the compiler happened to fold the loop.
            Assert.That(state.Value, Is.LessThan(1e-3f),
                "Forty ticks of decay empties a 4.0 meter to within float noise, not to exactly zero.");
            Assert.That(state.Value, Is.GreaterThanOrEqualTo(0f), "It must never go negative.");

            state = NoiseMath.TickObservation(state);
            Assert.That(state.Value, Is.EqualTo(0f),
                "One more tick floors it at 0. AS3 has no floor and lets obs drift a hair below zero; " +
                "the port clamps, because AS3's own HUD arithmetic (obs / maxObs * 40 + 1) would index a " +
                "negative frame. Unobservable in every other consumer, so the clamp is a free divergence.");

            state = NoiseMath.TickObservation(state);
            Assert.That(state.Value, Is.EqualTo(0f));
            Assert.That(NoiseMath.IsObserved(state, 20f), Is.False,
                "A drained meter is not observed, however long it has been draining.");
        }

        /// <summary>
        /// The decay-versus-inflow ratio is the design: a faint sound accumulates and a loud one commits.
        /// </summary>
        [Test]
        public void Observation_DecayVersusInflow_IsWhatMakesRhythmMatter()
        {
            Assert.That(NoiseMath.ObservationDecayPerTick, Is.EqualTo(0.1f));
            Assert.That(NoiseMath.DefaultMaxObservation, Is.EqualTo(20f));
            Assert.That(NoiseMath.ObservationHoldTicks, Is.EqualTo(30));

            // A gunshot at point blank (intensity 4) fills the meter in five ticks.
            Assert.That(Mathf.CeilToInt(20f / 4f), Is.EqualTo(5));
            // A footstep at the rim of the radius (intensity 0.4) needs fifty — longer than the 30-tick
            // hold, so it only accumulates if the player keeps walking.
            Assert.That(Mathf.CeilToInt(20f / 0.4f), Is.EqualTo(50));
        }

        // ── budilo(): alarm propagation ───────────────────────────────────────────────────────

        /// <summary>
        /// The alarm radius is scaled by the <b>receiver's</b> ear — so a deaf ally never gets the call.
        /// </summary>
        [Test]
        public void AlarmReaches_IsScaledByTheReceiversEar()
        {
            const float radius = NoiseMath.DefaultAlarmRadius;   // 500

            Assert.That(NoiseMath.AlarmReaches(100f * 100f, radius, 1f, 1f, false), Is.True,
                "100 px away, ordinary ears: heard.");
            Assert.That(NoiseMath.AlarmReaches(600f * 600f, radius, 1f, 1f, false), Is.False,
                "600 px away with a 500 px radius: not heard.");
            Assert.That(NoiseMath.AlarmReaches(600f * 600f, radius, 2.5f, 1f, false), Is.True,
                "The same shout reaches a 2.5-ear unit — the radius is 1250 for it.");
            Assert.That(NoiseMath.AlarmReaches(100f * 100f, radius, 0f, 1f, false), Is.False,
                "A deaf ally does not receive the alarm at any distance.");
            Assert.That(NoiseMath.AlarmReaches(100f * 100f, radius, 1f, 0.5f, false), Is.True,
                "earMult scales it too (Location.budilo:36411).");
            Assert.That(NoiseMath.AlarmReaches(300f * 300f, radius, 1f, 0.5f, false), Is.False,
                "With earMult 0.5 the radius is 250, so 300 px is out of range.");
        }

        /// <summary>
        /// Robots are the exception, and the reason is in the data: every robot authors <c>ear='0'</c>, so
        /// without the branch the robot network would be unreachable.
        /// </summary>
        [Test]
        public void AlarmReaches_RobotsIgnoreEar_AndTheDataIsWhy()
        {
            const float radius = NoiseMath.DefaultAlarmRadius;

            Assert.That(NoiseMath.AlarmReaches(100f * 100f, radius, 0f, 1f, receiverIsRobot: true), Is.True,
                "A robot with ear='0' still receives the alarm — spritebot/vortex/roller/roller2 all " +
                "author ear='0' (:3800-3845), so gating the robot branch on ear would make it dead code.");

            Assert.That(NoiseMath.AlarmReaches(600f * 600f, radius, 0f, 1f, receiverIsRobot: true), Is.False,
                "The plain radius still bounds it.");
            Assert.That(NoiseMath.AlarmReaches(600f * 600f, radius, 1f, 2f, receiverIsRobot: true), Is.False,
                "And robots ignore earMult too, as the oracle's branch does.");
        }

        /// <summary>
        /// The alarm's positional error, and the difference between a shout and a room-wide call.
        /// </summary>
        [Test]
        public void AlarmSpread_UnitIs125Px_LocationIsCappedAt200Px()
        {
            Assert.That(NoiseMath.AlarmHalfSpread(NoiseMath.UnitAlarmSpreadPixels), Is.EqualTo(125f),
                "`(Math.random() - 0.5) * 250` spans ±125 px (:126092).");

            Assert.That(NoiseMath.LocationAlarmSpread(1000f), Is.EqualTo(400f),
                "`_loc9_ = param3 / 2` = 500, capped at 400 (:36414-36418).");
            Assert.That(NoiseMath.AlarmHalfSpread(NoiseMath.LocationAlarmSpread(1000f)), Is.EqualTo(200f),
                "So a room-wide alarm is ±200 px — deliberately vaguer than a shout's ±125.");
            Assert.That(NoiseMath.LocationAlarmSpread(400f), Is.EqualTo(200f),
                "Below the cap it is just radius/2.");
        }

        // ── The randomised investigation point ────────────────────────────────────────────────

        /// <summary>
        /// A sound-only commit is deliberately imprecise — <b>±100 px on both axes, from two independent
        /// draws</b>.
        /// </summary>
        [Test]
        public void RandomisedOffset_SpansPlusMinusHalfTheSpread_OnBothAxesIndependently()
        {
            const float spread = NoiseMath.SoundOnlyCommitSpreadPixels;   // 200

            Vector2 low = NoiseMath.RandomisedOffset(spread, 0f, 0f);
            Assert.That(low.x, Is.EqualTo(-100f).Within(1e-4f));
            Assert.That(low.y, Is.EqualTo(-100f).Within(1e-4f));

            Vector2 high = NoiseMath.RandomisedOffset(spread, 1f, 1f);
            Assert.That(high.x, Is.EqualTo(100f).Within(1e-4f));
            Assert.That(high.y, Is.EqualTo(100f).Within(1e-4f));

            Vector2 mixed = NoiseMath.RandomisedOffset(spread, 0f, 1f);
            Assert.That(mixed.x, Is.EqualTo(-100f).Within(1e-4f));
            Assert.That(mixed.y, Is.EqualTo(100f).Within(1e-4f),
                "Two independent draws — the error is a square, not a diagonal. Reusing one roll for both " +
                "axes would put every investigation on the 45° line.");

            Vector2 centre = NoiseMath.RandomisedOffset(spread, 0.5f, 0.5f);
            Assert.That(centre.sqrMagnitude, Is.EqualTo(0f).Within(1e-6f),
                "A 0.5 draw is the exact centre, so the offset is never biased by a rounding choice.");
        }

        // ── observation()'s input scaling and the vision tiers ───────────────────────────────

        /// <summary>
        /// <c>observ</c> scales how fast a unit builds suspicion — and it is <b>not</b> a range.
        /// </summary>
        [Test]
        public void ObservationInput_ScalesByObservationPower()
        {
            Assert.That(NoiseMath.ObservationInput(4f, 0f), Is.EqualTo(4f),
                "observ = 0 (most zombies) leaves the intensity alone: `param2 > _loc3_` is false.");
            Assert.That(NoiseMath.ObservationInput(4f, 6f), Is.EqualTo(8.8f).Within(1e-4f),
                "observ = 6 (zombie9) multiplies by 1 + 6*0.2 = 2.2, so it commits in half the time.");
            Assert.That(NoiseMath.ObservationInput(20f, 6f), Is.EqualTo(44f).Within(1e-4f),
                "A sharp observer at close range fills the meter in one tick and pins it at the ceiling.");

            Assert.That(NoiseMath.ObservationInput(4f, -1f), Is.EqualTo(4f / 1.3f).Within(1e-4f),
                "A negative observ DIVIDES — `param1 /= 1 - (param2 - _loc3_) * 0.3`. The shipped data " +
                "never authors one, but the oracle accepts it and leaving the branch out would make " +
                "this silently wrong rather than obviously incomplete.");

            Assert.That(NoiseMath.ObservationInput(0f, 6f), Is.EqualTo(0f),
                "Nothing perceived is nothing scaled — a sharp observer does not see through walls.");
        }

        /// <summary>
        /// Vision's two tiers: 20 inside <c>detecting</c>, 4 out to the look range, nothing beyond.
        /// </summary>
        [Test]
        public void VisionIntensity_HasTwoTiers_AndNothingBeyondTheRange()
        {
            const float detecting = 40f;
            const float range = 480f;

            Assert.That(NoiseMath.VisionIntensity(0f, detecting, range), Is.EqualTo(20f),
                "Point blank -> `return 20`. Against maxObs = 20 that is a commit in one tick.");
            Assert.That(NoiseMath.VisionIntensity(detecting * detecting - 0.5f, detecting, range),
                Is.EqualTo(20f));
            Assert.That(NoiseMath.VisionIntensity(detecting * detecting, detecting, range), Is.EqualTo(4f),
                "Exactly at `detecting` it drops to the 4 tier — `<`, not `<=`.");
            Assert.That(NoiseMath.VisionIntensity(range * range - 0.5f, detecting, range), Is.EqualTo(4f),
                "4 commits in 5 ticks (0.17 s) — the oracle's reaction time at the far edge.");
            Assert.That(NoiseMath.VisionIntensity(range * range, detecting, range), Is.EqualTo(0f),
                "Exactly at the range limit: not seen.");

            Assert.That(NoiseMath.VisionCloseIntensity, Is.EqualTo(NoiseMath.MaxHearingIntensity * 5f),
                "AS3's `return 20` against `listen()`'s ceiling of 4 — sight at close range is five " +
                "times a point-blank shout, which is why it commits instantly.");
            Assert.That(NoiseMath.VisionRangeIntensity, Is.EqualTo(NoiseMath.MaxHearingIntensity),
                "…but at range, sight and the loudest possible sound commit at exactly the same speed. " +
                "That equality is the oracle's, and it is what makes the two senses comparable.");
        }

        // ── The declared defaults, pinned so a "tidy-up" cannot move them ─────────────────────

        /// <summary>
        /// The oracle's declared constants, as a single tripwire. Each of these is a number someone could
        /// reasonably decide to "improve"; this test is where they have to argue with the oracle instead.
        /// </summary>
        [Test]
        public void DeclaredConstants_MatchTheOracle()
        {
            Assert.That(NoiseMath.DefaultNoiseRun, Is.EqualTo(200), "Unit.as:122272");
            Assert.That(NoiseMath.NoiseDecayPerTick, Is.EqualTo(20), "Unit.as:124878");
            Assert.That(NoiseMath.NoiseRippleTicks, Is.EqualTo(30), "Unit.as:122274");
            Assert.That(NoiseMath.DefaultEar, Is.EqualTo(1f), "Unit.as:122286");
            Assert.That(NoiseMath.DefaultEarMultiplier, Is.EqualTo(1f), "Location:33803");
            Assert.That(NoiseMath.MaxHearingIntensity, Is.EqualTo(4f), "Unit.as:126348");
            Assert.That(NoiseMath.SoundOnlyCommitIntensity, Is.EqualTo(1f), "Unit.as:126487");
            Assert.That(NoiseMath.DeathAlarmRadius, Is.EqualTo(800f), "Unit.as:29075");
            Assert.That(NoiseMath.DoorNoise, Is.EqualTo(300), "Unit.as:119192");
            Assert.That(NoiseMath.DefaultAlarmRadius, Is.EqualTo(500f), "Unit.as:126071");
            Assert.That(NoiseMath.DefaultLocationAlarmRadius, Is.EqualTo(1000f), "Location:36405");

            Assert.That(NoiseMath.SoundOnlyCommitSpreadPixels, Is.Not.EqualTo(NoiseMath.UnitAlarmSpreadPixels),
                "A sound-only commit (±100 px) and a unit alarm (±125 px) are DIFFERENT numbers in the " +
                "oracle — 200 and 250 as multipliers. Sharing one constant would be a quiet 25% change.");
        }
    }
}

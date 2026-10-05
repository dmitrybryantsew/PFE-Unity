using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ExplosionPulseRules"/> — the port of AS3's multi-pulse blast, the mechanism that
    /// makes <c>fgren</c>, <c>molotov</c>, <c>gasgr</c> and <c>acidgr</c> a damage <i>area</i>.
    ///
    /// <para><b>Why this is worth a fixture, and why the first test replays the oracle.</b> The schedule
    /// is a closed form of a countdown loop, and the loop is
    /// <i>decrement-then-test</i> — so the obvious reading ("pulse <c>k</c> at <c>k * 10</c>") is wrong
    /// by one frame on every pulse after the first. A closed form that is off by one still produces a
    /// plausible-looking grenade; nothing would go red. So
    /// <see cref="ScheduleMatchesTheOraclesCountdownLoop"/> re-implements AS3's loop literally, for six
    /// different <c>explKol</c> values, and asserts the closed form agrees tick for tick. That is the
    /// only assertion here that could not be written by reading the implementation.</para>
    ///
    /// <para>Every "no pulse" assertion is paired with a positive control at a neighbouring tick,
    /// because "nothing happened" is unfalsifiable on its own — the failure mode this project keeps
    /// hitting.</para>
    /// </summary>
    [TestFixture]
    public class ExplosionPulseRulesTests
    {
        // ── The pulse count ──────────────────────────────────────────────────────────────────

        [Test]
        public void PulseCount_TreatsZeroAndOneAsASinglePulse()
        {
            // AS3's `explKol <= 0` branch runs explRun() once and sets no countdown; 1 sets a countdown
            // of zero, which is the same thing. Both are one pulse.
            Assert.AreEqual(1, ExplosionPulseRules.PulseCount(0));
            Assert.AreEqual(1, ExplosionPulseRules.PulseCount(1));
            Assert.AreEqual(1, ExplosionPulseRules.PulseCount(-5));
        }

        [Test]
        public void PulseCount_IsTheAttributeValueAboveOne()
        {
            Assert.AreEqual(2,  ExplosionPulseRules.PulseCount(2));
            Assert.AreEqual(10, ExplosionPulseRules.PulseCount(10));
            Assert.AreEqual(12, ExplosionPulseRules.PulseCount(12));
        }

        [Test]
        public void HasSustainedPhase_OnlyAboveOne()
        {
            Assert.IsFalse(ExplosionPulseRules.HasSustainedPhase(0));
            Assert.IsFalse(ExplosionPulseRules.HasSustainedPhase(1));
            Assert.IsTrue(ExplosionPulseRules.HasSustainedPhase(2));
            Assert.IsTrue(ExplosionPulseRules.HasSustainedPhase(10));
        }

        // ── The schedule, against a literal replay of the oracle ─────────────────────────────

        /// <summary>
        /// Re-implements AS3 <c>Bullet.explosion()</c> (<c>:683-691</c>) and <c>run()</c>
        /// (<c>:240-250</c>) literally, and returns the ticks at which <c>explRun()</c> fires.
        ///
        /// <para>Written from the oracle, not from <see cref="ExplosionPulseRules"/>: the point of the
        /// test is that the two agree, so building the expectation out of the thing under test would
        /// assert only that it equals itself — the mistake that let a 2× mutation of
        /// <c>BlindScatterX</c> turn nothing red.</para>
        /// </summary>
        private static List<int> OraclePulseTicks(int explKol)
        {
            const int period = 10;   // Bullet.explPeriod, Bullet.as:121

            var ticks = new List<int>();

            // `explosion()`: explRun() runs once, then the countdown is set — but only when explKol > 0.
            ticks.Add(0);
            int explT = explKol > 0 ? (explKol - 1) * period : 0;

            int frame = 0;
            while (explT > 0)
            {
                frame++;

                // `if (expl_t > 0) --expl_t; else --liv;` — the countdown comes first…
                explT--;

                // …and only then the test. This ordering is why the second pulse is at frame 9.
                if (explT > 0 && explT % period == 1) ticks.Add(frame);
            }

            return ticks;
        }

        [Test]
        public void ScheduleMatchesTheOraclesCountdownLoop()
        {
            foreach (int explKol in new[] { 0, 1, 2, 3, 10, 12 })
            {
                List<int> expected = OraclePulseTicks(explKol);
                var actual = new List<int>();

                for (int t = 0; t <= ExplosionPulseRules.TotalTicks(explKol) + 2; t++)
                {
                    int due = ExplosionPulseRules.PulseDueAt(explKol, t);
                    if (due >= 0) actual.Add(t);
                }

                Assert.AreEqual(expected, actual,
                    $"explKol={explKol}: the closed-form schedule must equal the oracle's loop.");
            }
        }

        [Test]
        public void TheSecondPulseLandsAtTickNine_NotTen()
        {
            // The single most likely thing to get wrong. Stated on its own so a regression names itself.
            Assert.AreEqual(1, ExplosionPulseRules.PulseDueAt(10, 9));
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 10));
        }

        [Test]
        public void PulseZeroIsDueAtTickZero()
        {
            Assert.AreEqual(0, ExplosionPulseRules.PulseDueAt(10, 0));
            Assert.AreEqual(0, ExplosionPulseRules.PulseDueAt(0, 0));
            Assert.AreEqual(0, ExplosionPulseRules.PulseDueAt(1, 0));
        }

        [Test]
        public void NoPulseIsDueBetweenPulses()
        {
            // Negative control for the assertion above: a tick in the middle of a gap is silent…
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 1));
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 5));
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 8));

            // …and the positive control that the same input shape does fire at 9.
            Assert.AreEqual(1, ExplosionPulseRules.PulseDueAt(10, 9));
        }

        [Test]
        public void NoPulseIsDuePastTheEndOfTheTrain()
        {
            // explKol=10 → last pulse at 89, so 99 and 109 are past it…
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 99));
            Assert.AreEqual(-1, ExplosionPulseRules.PulseDueAt(10, 109));

            // …while explKol=12 still has one at 109. Same tick, different weapon: this is the pair
            // that proves the bound is read from explKol rather than hardcoded.
            Assert.AreEqual(11, ExplosionPulseRules.PulseDueAt(12, 109));
        }

        [Test]
        public void TheReportedWeaponsGetThePulseCountsTheirDataCarries()
        {
            // fgren / molotov explkol='10', gasgr / acidgr explkol='12'. Named because these four are
            // the owner's report — the numbers come from AllData.as, not from the port.
            Assert.AreEqual(10, ExplosionPulseRules.PulseCount(10), "fgren, molotov");
            Assert.AreEqual(12, ExplosionPulseRules.PulseCount(12), "gasgr, acidgr");
        }

        [Test]
        public void LastPulseTickIsOneBeforeTheTotal()
        {
            Assert.AreEqual(0,  ExplosionPulseRules.LastPulseTick(0));
            Assert.AreEqual(0,  ExplosionPulseRules.LastPulseTick(1));
            Assert.AreEqual(9,  ExplosionPulseRules.LastPulseTick(2));
            Assert.AreEqual(89, ExplosionPulseRules.LastPulseTick(10));
            Assert.AreEqual(109, ExplosionPulseRules.LastPulseTick(12));

            Assert.AreEqual(ExplosionPulseRules.TotalTicks(10) - 1,
                            ExplosionPulseRules.LastPulseTick(10));
        }

        [Test]
        public void IsFinishedOneTickAfterTheLastPulse()
        {
            Assert.IsFalse(ExplosionPulseRules.IsFinished(10, 88));
            Assert.IsFalse(ExplosionPulseRules.IsFinished(10, 89));   // the last pulse's own tick
            Assert.IsTrue(ExplosionPulseRules.IsFinished(10, 90));    // the frame AS3 removes the object
        }

        [Test]
        public void ASinglePulseBlastIsFinishedImmediately()
        {
            Assert.IsTrue(ExplosionPulseRules.IsFinished(0, 0));
            Assert.IsTrue(ExplosionPulseRules.IsFinished(1, 0));
        }

        // ── The shape dispatch ───────────────────────────────────────────────────────────────

        [Test]
        public void ExplTipOne_IsABlastOnEveryPulse()
        {
            // fgren / molotov: no expltip attribute, so AS3's default 1.
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(ExplosionShape.Blast, ExplosionPulseRules.ShapeFor(1, i), $"pulse {i}");
        }

        [Test]
        public void ExplTipTwo_IsGasOnEveryPulse()
        {
            // gasgr / zombivenom / zombipink / robogas.
            for (int i = 0; i < 12; i++)
                Assert.AreEqual(ExplosionShape.Gas, ExplosionPulseRules.ShapeFor(2, i), $"pulse {i}");
        }

        [Test]
        public void ExplTipThree_OpensWithABlastThenRunsAsGas()
        {
            // acidgr / zombiacid. AS3 calls explRun() BEFORE assigning expl_t, so the first pulse is the
            // expl_t == 0 case — a blast — and every later one is gas.
            Assert.AreEqual(ExplosionShape.Blast, ExplosionPulseRules.ShapeFor(3, 0));
            Assert.AreEqual(ExplosionShape.Gas,   ExplosionPulseRules.ShapeFor(3, 1));
            Assert.AreEqual(ExplosionShape.Gas,   ExplosionPulseRules.ShapeFor(3, 11));
        }

        [Test]
        public void AnUnknownExplTipDamagesNothing()
        {
            // AS3's two `if`s both miss, so neither explBlast nor explGas runs. No shipped weapon is in
            // this state — which is exactly why it needs a test: it is the silent case.
            Assert.AreEqual(ExplosionShape.None, ExplosionPulseRules.ShapeFor(0, 0));
            Assert.AreEqual(ExplosionShape.None, ExplosionPulseRules.ShapeFor(0, 5));
            Assert.AreEqual(ExplosionShape.None, ExplosionPulseRules.ShapeFor(4, 0));

            Assert.IsFalse(ExplosionPulseRules.DamagesAt(0, 0));
            Assert.IsFalse(ExplosionPulseRules.DamagesAt(4, 0));
        }

        [Test]
        public void EveryKnownExplTipDamagesEveryPulse()
        {
            foreach (int explTip in new[] { 1, 2, 3 })
            {
                Assert.IsTrue(ExplosionPulseRules.DamagesAt(explTip, 0), $"explTip={explTip} pulse 0");
                Assert.IsTrue(ExplosionPulseRules.DamagesAt(explTip, 7), $"explTip={explTip} pulse 7");
            }
        }

        [Test]
        public void OnlyTheFirstPulseIsInitial()
        {
            Assert.IsTrue(ExplosionPulseRules.IsInitialPulse(0));
            Assert.IsFalse(ExplosionPulseRules.IsInitialPulse(1));
            Assert.IsFalse(ExplosionPulseRules.IsInitialPulse(11));

            // The negative control for the same predicate, on a pulse index the caller can actually
            // reach: pulse 1 is the first pulse of the sustained phase, and it is the one whose fire and
            // acid visuals must NOT draw. "Pulse 0 is initial" alone would still hold if the predicate
            // were `pulseIndex >= 0`.
            Assert.IsTrue(ExplosionPulseRules.IsInitialPulse(0) &&
                          !ExplosionPulseRules.IsInitialPulse(1));
        }
    }
}

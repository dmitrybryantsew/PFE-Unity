using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Offline fixtures for <see cref="BossRaiderRules"/> — the raider leader's rotation, animation
    /// ladder, contact scales and reinforcement gate (<c>UnitBossRaider.as</c>).
    ///
    /// <para><b>Every expected value here was written from the AS3 expression, not read off the C#.</b>
    /// The oracle's literal appears in each message so a failure names the line of
    /// <c>UnitBossRaider.as</c> that disagrees rather than merely the method. This matters more than
    /// usual for this boss: <b>each of the five things pinned below was previously an invented value
    /// that passed a structural review</b> — a rotation window, a stand-off distance, a constant
    /// animation, a flat contact scale, and a reinforcement trigger with no gate.</para>
    /// </summary>
    [TestFixture]
    public class BossRaiderRulesTests
    {
        // ── the rotation (UnitBossRaider.as:320-339) ──────────────────────────────────────────────

        /// <summary>
        /// <c>++aiState; if(aiState &gt; 5) aiState = 1;</c> (<c>:326-330</c>) — the wrap is
        /// <b>5 → 1</b>, never 5 → 0. State 0 is the spawn state and the only one that stands still, so
        /// a port that wrapped to 0 would give the boss an idle beat it does not have.
        /// </summary>
        [Test]
        public void NextState_WrapsFiveToOne_AndNeverReturnsToZero()
        {
            Assert.AreEqual(1, BossRaiderRules.NextState(0), "0 -> 1: the first increment (:326)");
            Assert.AreEqual(2, BossRaiderRules.NextState(1), "1 -> 2");
            Assert.AreEqual(3, BossRaiderRules.NextState(2), "2 -> 3");
            Assert.AreEqual(4, BossRaiderRules.NextState(3), "3 -> 4");
            Assert.AreEqual(5, BossRaiderRules.NextState(4), "4 -> 5");
            Assert.AreEqual(1, BossRaiderRules.NextState(5), "5 -> 1: `if(aiState > 5) aiState = 1` (:327-330)");
        }

        /// <summary>
        /// A full cycle from the spawn state visits every state exactly once and returns to 1 — six
        /// steps, five distinct living states, and <b>no 0</b>.
        /// </summary>
        [Test]
        public void AFullCycle_VisitsOneThroughFive_AndComesBackToOne()
        {
            int state = 0;
            var seen = new System.Collections.Generic.List<int>();

            for (int i = 0; i < 6; i++)
            {
                state = BossRaiderRules.NextState(state);
                seen.Add(state);
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 1 }, seen,
                "the ladder is a 5-long ring entered at 1 (:326-330)");
            CollectionAssert.DoesNotContain(seen, 0,
                "state 0 is the spawn state; the rotation must never re-enter it");
        }

        /// <summary>
        /// <c>if(aiState == 2 || aiState == 4 || aiState == 5) aiTCh = 30;</c> (<c>:331-334</c>).
        /// <b>Three states, not four</b> — 1 and 3 take the long window.
        /// </summary>
        [Test]
        public void ShortTimer_IsStatesTwoFourFive_Only()
        {
            Assert.IsFalse(BossRaiderRules.UsesShortTimer(0), "state 0 is not in the oracle's list");
            Assert.IsFalse(BossRaiderRules.UsesShortTimer(1), "state 1 takes the long window");
            Assert.IsTrue(BossRaiderRules.UsesShortTimer(2), ":331");
            Assert.IsFalse(BossRaiderRules.UsesShortTimer(3), "state 3 is the firing state — long window");
            Assert.IsTrue(BossRaiderRules.UsesShortTimer(4), ":331");
            Assert.IsTrue(BossRaiderRules.UsesShortTimer(5), ":331");
        }

        /// <summary>
        /// The two timer arms: <c>30</c> exactly for 2/4/5, and <c>floor(random*100) + 150</c> — i.e.
        /// <b>150..249</b> — for the rest. The upper bound is the one that matters: the port's old
        /// body used <c>Random.Range(150, 250)</c> against a draw of <c>0..99</c> here, so 250 was
        /// unreachable and 249 was the real ceiling either way.
        /// </summary>
        [Test]
        public void TimerFor_IsThirtyOrOneFiftyToTwoFortyNine()
        {
            Assert.AreEqual(30, BossRaiderRules.TimerFor(2, 0), ":333 — `aiTCh = 30`, no draw");
            Assert.AreEqual(30, BossRaiderRules.TimerFor(4, 99), ":333 — the roll is ignored in this arm");
            Assert.AreEqual(30, BossRaiderRules.TimerFor(5, 57), ":333");

            Assert.AreEqual(150, BossRaiderRules.TimerFor(1, 0), ":337 — `floor(random*100) + 150`, roll 0");
            Assert.AreEqual(249, BossRaiderRules.TimerFor(1, 99), ":337 — roll 99 is the ceiling");
            Assert.AreEqual(150, BossRaiderRules.TimerFor(3, 0), ":337 — state 3 uses the same arm");
            Assert.AreEqual(200, BossRaiderRules.TimerFor(3, 50), ":337 — 150 + 50");
        }

        /// <summary>
        /// The draw range constant is <b>100</b>, so the caller's <c>Random.Range(0, 100)</c> yields
        /// 0..99 and the window tops out at 249. A range of 101 would make 250 reachable, which
        /// <c>Math.random()</c> cannot produce.
        /// </summary>
        [Test]
        public void TheRollRange_IsOneHundred_SoTheWindowTopsOutAt249()
        {
            Assert.AreEqual(100, BossRaiderRules.LongAiTChRollRange, ":337 — `Math.random() * 100`");
            Assert.AreEqual(
                BossRaiderRules.LongAiTChBase + BossRaiderRules.LongAiTChRollRange - 1,
                BossRaiderRules.TimerFor(1, BossRaiderRules.LongAiTChRollRange - 1),
                "the largest reachable window is base + range - 1");
        }

        // ── the animation ladder (UnitBossRaider.as:133-191) ──────────────────────────────────────

        /// <summary>
        /// The branch order, one case per arm of <c>animate()</c>. <b>The order is the content</b>:
        /// a dead boss in state 4 must draw <c>die</c>, not <c>bac</c>; a grounded boss in state 4 must
        /// draw <c>bac</c>, not <c>stay</c>; and a boss drifting at 1.0 px/frame must draw
        /// <c>trot</c>, because the oracle's band is <b>strict</b> (<c>dx &lt; 1 &amp;&amp; dx &gt; -1</c>).
        /// </summary>
        [Test]
        public void ResolveAnimState_FollowsTheOraclesBranchOrder()
        {
            // `if(sost == 3) "die"` (:136-143) — and it wins over everything, including state 4.
            Assert.AreEqual("die", BossRaiderRules.ResolveAnimState(false, 0, true, 0f), ":136 — dead, idle");
            Assert.AreEqual("die", BossRaiderRules.ResolveAnimState(false, 4, true, 0f), ":136 — dead beats bac");
            Assert.AreEqual("die", BossRaiderRules.ResolveAnimState(false, 1, false, 12f), ":136 — dead beats airborne");

            // `else if(aiState == 4 || aiState == 6) "bac"` (:144-151) — wins over grounded and velocity.
            Assert.AreEqual("bac", BossRaiderRules.ResolveAnimState(true, 4, true, 0f), ":144 — the quake wind-up");
            Assert.AreEqual("bac", BossRaiderRules.ResolveAnimState(true, 4, true, 9f), ":144 — even while moving");
            Assert.AreEqual("bac", BossRaiderRules.ResolveAnimState(true, 6, true, 0f),
                ":144 — state 6 is unreachable from the rotation but transcribed");
            Assert.AreEqual("bac", BossRaiderRules.ResolveAnimState(true, 4, false, 0f), ":144 — even airborne");

            // Grounded, |dx| < 1 -> "stay" (:154-161).
            Assert.AreEqual("stay", BossRaiderRules.ResolveAnimState(true, 0, true, 0f), ":154 — idle");
            Assert.AreEqual("stay", BossRaiderRules.ResolveAnimState(true, 3, true, 0.99f), ":154 — inside the band");
            Assert.AreEqual("stay", BossRaiderRules.ResolveAnimState(true, 3, true, -0.99f), ":154 — and the negative half");

            // Grounded, moving, state 1 -> "run" (:162-169); any other state -> "trot" (:170-174).
            Assert.AreEqual("run", BossRaiderRules.ResolveAnimState(true, 1, true, 1f), ":162 — state 1 at exactly 1");
            Assert.AreEqual("run", BossRaiderRules.ResolveAnimState(true, 1, true, 18f), ":162 — the 6x run");
            Assert.AreEqual("trot", BossRaiderRules.ResolveAnimState(true, 2, true, 3f), ":170 — walking in state 2");
            Assert.AreEqual("trot", BossRaiderRules.ResolveAnimState(true, 3, true, -3f), ":170 — state 3");
            Assert.AreEqual("trot", BossRaiderRules.ResolveAnimState(true, 5, true, 3f), ":170 — state 5");

            // Airborne -> "jump" (:176-190), whatever the state.
            Assert.AreEqual("jump", BossRaiderRules.ResolveAnimState(true, 1, false, 18f), ":176 — the leap");
            Assert.AreEqual("jump", BossRaiderRules.ResolveAnimState(true, 3, false, 0f), ":176 — falling, no drift");
        }

        /// <summary>
        /// The <c>stay</c> band is <b>two-sided and strict</b>: <c>dx &lt; 1 &amp;&amp; dx &gt; -1</c>
        /// (<c>:154</c>). <c>1</c> and <c>-1</c> themselves are outside it, so a boss moving at exactly
        /// 1.0 px/frame draws <c>trot</c>. An <c>abs(dx) &lt;= 1</c> reading — the obvious "fix" — would
        /// move the boundary and make the walking clip unreachable at one speed.
        /// </summary>
        [Test]
        public void TheStayBand_IsStrictOnBothSides()
        {
            Assert.AreEqual("trot", BossRaiderRules.ResolveAnimState(true, 3, true, 1f), "1 is not < 1");
            Assert.AreEqual("trot", BossRaiderRules.ResolveAnimState(true, 3, true, -1f), "-1 is not > -1");
            Assert.AreEqual("stay", BossRaiderRules.ResolveAnimState(true, 3, true, 0.999f), "just inside");
            Assert.AreEqual("stay", BossRaiderRules.ResolveAnimState(true, 3, true, -0.999f), "just inside, negative");
        }

        // ── the two contact scales (UnitBossRaider.as:513, :558) ─────────────────────────────────

        /// <summary>
        /// State 1 (<c>:513</c>) is the <b>relative</b> closing speed: <c>|dx - celUnit.dx| &gt; 8</c>.
        /// A boss closing at 20 against a player running at 20 is closing at <b>zero</b> and pays half;
        /// the port's old reading (the boss's own <c>dx</c> alone) paid full for that exchange.
        /// </summary>
        [Test]
        public void ChargeContactScale_UsesTheRelativeClosingSpeed()
        {
            Assert.AreEqual(1f, BossRaiderRules.ChargeContactScale(20f, 0f), ":513 — closing at 20");
            Assert.AreEqual(1f, BossRaiderRules.ChargeContactScale(0f, -20f), ":513 — target running at it");
            Assert.AreEqual(0.5f, BossRaiderRules.ChargeContactScale(20f, 20f),
                ":513 — same speed, closing at 0: a chase is not a charge");
            Assert.AreEqual(0.5f, BossRaiderRules.ChargeContactScale(0f, 0f), ":513 — both still");
        }

        /// <summary>
        /// The boundary is <b>strictly greater than 8</b>, so 8.0 exactly is a half-strength hit. This
        /// is the kind of off-by-one that reads identically in play.
        /// </summary>
        [Test]
        public void ContactScales_AreStrictlyGreaterThanEight()
        {
            Assert.AreEqual(0.5f, BossRaiderRules.ChargeContactScale(8f, 0f), "8 is not > 8");
            Assert.AreEqual(1f, BossRaiderRules.ChargeContactScale(8.01f, 0f), "just past 8");
            Assert.AreEqual(0.5f, BossRaiderRules.VortexContactScale(8f), "8 is not > 8");
            Assert.AreEqual(1f, BossRaiderRules.VortexContactScale(-8.01f), "just past -8, absolute");
        }

        /// <summary>
        /// State 5 (<c>:558</c>) is the boss's <b>own</b> drift, not the closing speed — the two oracle
        /// lines genuinely differ and must not be unified. A boss standing still while the player runs
        /// into it pays <b>half</b> here, where state 1 would pay full.
        /// </summary>
        [Test]
        public void VortexContactScale_UsesTheBossesOwnDrift_NotTheClosingSpeed()
        {
            Assert.AreEqual(0.5f, BossRaiderRules.VortexContactScale(0f),
                ":558 — `Math.abs(dx)`, so a stationary boss is half even if the player charges");
            Assert.AreEqual(1f, BossRaiderRules.VortexContactScale(9f), ":558 — drifting at 9");
            Assert.AreEqual(1f, BossRaiderRules.VortexContactScale(-9f), ":558 — either direction");
        }

        // ── the reinforcement gate (UnitBossRaider.as:229-243) ───────────────────────────────────

        /// <summary>
        /// <c>_loc6_ = Math.floor((maxhp - hp) / maxhp * 4)</c> — <b>0..4</b>, and it is a floor, so a
        /// boss at 76 % HP has crossed no quarter and a boss at 74 % has crossed one.
        /// </summary>
        [Test]
        public void QuarterFor_FloorsTheMissingFractionTimesFour()
        {
            Assert.AreEqual(0, BossRaiderRules.QuarterFor(2000f, 2000f), "full HP");
            Assert.AreEqual(0, BossRaiderRules.QuarterFor(2000f, 1520f), "76% — no quarter yet");
            Assert.AreEqual(1, BossRaiderRules.QuarterFor(2000f, 1500f), "75% exactly — the first quarter");
            Assert.AreEqual(1, BossRaiderRules.QuarterFor(2000f, 1400f), "70% HP -> 0.3*4 = 1.2 -> 1");
            Assert.AreEqual(2, BossRaiderRules.QuarterFor(2000f, 1000f), "50% HP -> 0.5*4 = 2 exactly");
            Assert.AreEqual(2, BossRaiderRules.QuarterFor(2000f, 600f), "30% HP -> 0.7*4 = 2.8 -> 2, not 3");
            Assert.AreEqual(3, BossRaiderRules.QuarterFor(2000f, 400f), "20% HP -> 0.8*4 = 3.2 -> 3");
            Assert.AreEqual(4, BossRaiderRules.QuarterFor(2000f, 0f), "dead");
        }

        /// <summary>
        /// A max HP of zero must answer 0 rather than divide by zero — the oracle cannot reach this
        /// (a unit with no <c>maxhp</c> has no HP to lose), but a NaN here would make
        /// <c>quarter &gt; called</c> false forever and silence the wave instead of crashing.
        /// </summary>
        [Test]
        public void QuarterFor_WithNoMaxHp_IsZeroRatherThanNaN()
        {
            Assert.AreEqual(0, BossRaiderRules.QuarterFor(0f, 0f));
            Assert.AreEqual(0, BossRaiderRules.QuarterFor(0f, -10f));
        }

        /// <summary>
        /// <c>if(tr == 2 &amp;&amp; globalDif &gt; 1)</c> then <c>if(_loc6_ &gt; called)</c> — three
        /// terms, each independently load-bearing. A carbine boss never calls; Easy never calls; and a
        /// quarter already spent never calls again.
        /// </summary>
        [Test]
        public void ShouldCallReinforcement_RequiresAllThreeTerms()
        {
            // The one arm that fires: flamer, Normal (2) or above, a fresh quarter.
            Assert.IsTrue(BossRaiderRules.ShouldCallReinforcement(2, 2, 1, 0), ":233-239 — tr2, Normal, 1st quarter");
            Assert.IsTrue(BossRaiderRules.ShouldCallReinforcement(2, 4, 4, 3), ":233-239 — hardest difficulty, 4th quarter");

            // `tr == 2` — the carbine variant has no budget at all, on any difficulty.
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(1, 4, 4, 0), ":233 — tr1 never calls");
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(0, 4, 4, 0), ":233 — nor tr0");

            // `globalDif > 1` — 0 and 1 are the two refused tiers.
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(2, 1, 4, 0), ":233 — Easy is silent");
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(2, 0, 4, 0), ":233 — the tier below Easy too");

            // `_loc6_ > called` — a quarter already paid for does not pay twice.
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(2, 2, 1, 1), ":236 — quarter 1 already called");
            Assert.IsFalse(BossRaiderRules.ShouldCallReinforcement(2, 2, 2, 4), ":236 — ahead of the debt");
        }

        /// <summary>
        /// The budget is <b>three waves while alive plus a fourth on the killing blow</b> — not four
        /// during the fight.
        /// </summary>
        /// <remarks>
        /// <b>This is the off-by-one that reads as a tuning choice.</b>
        /// <c>floor((maxhp-hp)/maxhp*4)</c> is strictly below 4 for every living <c>hp</c>, so a boss
        /// standing at 1 HP has crossed three quarters, not four. The fourth appears only when
        /// <c>hp == 0</c>, and the oracle reaches it because <c>damage()</c> computes the quarter
        /// <i>after</i> <c>super.damage()</c> — i.e. after <c>die()</c> has already run. That is why the
        /// port's <c>CheckQuarterHpThresholds</c> override drops the base class's <c>!IsAlive</c> term:
        /// with it, the boss would call three waves and never the last one.
        /// </remarks>
        [Test]
        public void TheReinforcementBudget_IsThreeWavesPlusTheKillingBlow()
        {
            int called = 0;
            int waves = 0;

            // Walk 100% -> 1 HP in 5% steps, as a stream of damage events would.
            for (int percentHp = 95; percentHp >= 1; percentHp -= 5)
            {
                int quarter = BossRaiderRules.QuarterFor(2000f, percentHp * 20f);

                if (BossRaiderRules.ShouldCallReinforcement(2, 2, quarter, called))
                {
                    called++;
                    waves++;
                }
            }

            Assert.AreEqual(3, waves,
                ":235 — a living boss tops out at floor(0.9995*4) = 3 quarters, not 4");
            Assert.AreEqual(3, called, "and `called` ends at 3");

            // The killing blow: hp == 0 is the only way to reach the fourth quarter.
            Assert.AreEqual(4, BossRaiderRules.QuarterFor(2000f, 0f), "hp 0 -> missing 1.0 -> floor(4)");
            Assert.IsTrue(BossRaiderRules.ShouldCallReinforcement(2, 2, 4, called),
                ":236 — `4 > called(3)`, so the dying boss calls the last wave");
        }
    }
}

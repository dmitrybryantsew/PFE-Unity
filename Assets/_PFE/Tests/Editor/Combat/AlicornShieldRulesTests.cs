using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the alicorn's shield ladder — AS3 <c>UnitAlicorn.control():571-594</c>,
    /// <c>castShit():1087-1093</c>, and the constructor constants at <c>:33/:111/:152/:172-178</c>.
    ///
    /// <para><b>Why this fixture and not the controller's.</b> <c>AlicornController</c> is a
    /// <c>MonoBehaviour</c>: reaching it means <c>AddComponent</c>, which is a Unity <c>ECall</c> and
    /// throws wherever the native engine is absent. The controller-level alicorn tests in
    /// <c>EnemyFamiliesAndBossesTests</c> therefore cannot run in the offline harness — they are
    /// editor-only. The arithmetic they would have checked was extracted to
    /// <see cref="AlicornShieldRules"/> precisely so that it can be checked <i>here</i>, where it does
    /// run.</para>
    ///
    /// <para>The helper below replays the controller's own three-step tick (advance the timer, cast if
    /// the ladder says so, recompute the resistance) so the fixture exercises the same ordering the
    /// production call does — an ordering bug would otherwise be invisible.</para>
    /// </summary>
    [TestFixture]
    public class AlicornShieldRulesTests
    {
        /// <summary>The controller's <c>UpdateShieldTick</c> body, over plain values.</summary>
        static void Ladder(ref int tShit, ref float shieldHp, bool alerted, int tier, bool osob = false)
        {
            tShit = AlicornShieldRules.Tick(tShit, alerted, tier, osob);

            if (AlicornShieldRules.ShouldCast(shieldHp, tShit))
            {
                shieldHp = AlicornShieldRules.MaxShieldHp(tier);
                tShit = AlicornShieldRules.CastTimerAfterCast;
            }
        }

        // ── constants ───────────────────────────────────────────────────────────

        [Test]
        public void Constants_MatchTheOraclePerTier()
        {
            // UnitAlicorn.as:33 `shitMaxHp = 300`, :173 raises it to 500 on tr3.
            Assert.AreEqual(300, AlicornShieldRules.MaxShieldHp(1));
            Assert.AreEqual(300, AlicornShieldRules.MaxShieldHp(2));
            Assert.AreEqual(500, AlicornShieldRules.MaxShieldHp(3));

            // :152 `shitArmor = 25`, :172 raises it to 50 on tr3.
            Assert.AreEqual(25f, AlicornShieldRules.ShieldArmor(1), 1e-4f);
            Assert.AreEqual(25f, AlicornShieldRules.ShieldArmor(2), 1e-4f);
            Assert.AreEqual(50f, AlicornShieldRules.ShieldArmor(3), 1e-4f);

            // :111 `t_shit = 90`, :178 sets 45 on tr3.
            Assert.AreEqual(90, AlicornShieldRules.InitialCastTimer(1));
            Assert.AreEqual(90, AlicornShieldRules.InitialCastTimer(2));
            Assert.AreEqual(45, AlicornShieldRules.InitialCastTimer(3));

            // The family template parses to tier 0 and takes the tr1/tr2 row — the oracle never spawns
            // it, but it must not fall off the ladder into a zero-sized pool.
            Assert.AreEqual(300, AlicornShieldRules.MaxShieldHp(0));
            Assert.AreEqual(25f, AlicornShieldRules.ShieldArmor(0), 1e-4f);
            Assert.AreEqual(90, AlicornShieldRules.InitialCastTimer(0));
        }

        // ── the tick gate ───────────────────────────────────────────────────────

        [Test]
        public void Tick_AnUnawareUnitBelowTheThresholdIsFrozen()
        {
            // :571 — `aiSpok > 0 || tr == 3 && osob || t_shit > 150`. At the initial 90 none of the
            // three holds, so the timer never moves and the shield never arrives. The old port ran a
            // flat 90-tick cooldown that cast regardless of awareness; that is the divergence this pins.
            int t = AlicornShieldRules.InitialCastTimer(1);

            for (int i = 0; i < 500; i++)
                t = AlicornShieldRules.Tick(t, alerted: false, tier: 1, osob: false);

            Assert.AreEqual(90, t, "An unaware tr1/tr2 alicorn's timer is frozen at 90.");
        }

        [Test]
        public void Tick_AboveTheThresholdCountsDownRegardlessOfAwareness()
        {
            // The `t_shit > 150` term: straight after a cast (1000) the timer falls on its own.
            Assert.AreEqual(999, AlicornShieldRules.Tick(1000, alerted: false, tier: 1, osob: false));
            Assert.AreEqual(150, AlicornShieldRules.Tick(151, alerted: false, tier: 1, osob: false));

            // 150 is NOT above the threshold, so the timer stops exactly there for an unaware unit.
            Assert.AreEqual(150, AlicornShieldRules.Tick(150, alerted: false, tier: 1, osob: false));
        }

        [Test]
        public void Tick_AwarenessCountsDownAndOsobIsTr3Only()
        {
            Assert.AreEqual(89, AlicornShieldRules.Tick(90, alerted: true, tier: 1, osob: false));

            // `osob` is the third term of the disjunction and is gated on `tr == 3`.
            Assert.AreEqual(44, AlicornShieldRules.Tick(45, alerted: false, tier: 3, osob: true));
            Assert.AreEqual(45, AlicornShieldRules.Tick(45, alerted: false, tier: 1, osob: true),
                "osob must not help a tr1/tr2 unit — the oracle gates it on `this.tr == 3`.");
        }

        // ── the cast condition ──────────────────────────────────────────────────

        [Test]
        public void ShouldCast_NeedsAnEmptyPoolAndAnExpiredTimer()
        {
            Assert.IsTrue(AlicornShieldRules.ShouldCast(shieldHp: 0f, castTimerTicks: 0));
            Assert.IsTrue(AlicornShieldRules.ShouldCast(shieldHp: 0f, castTimerTicks: -7),
                "AS3 never floors t_shit, so a negative timer is a real state.");

            Assert.IsFalse(AlicornShieldRules.ShouldCast(shieldHp: 300f, castTimerTicks: 0),
                "A shielded alicorn does not re-cast over its own shield.");
            Assert.IsFalse(AlicornShieldRules.ShouldCast(shieldHp: 0f, castTimerTicks: 1));
        }

        // ── the resistance ──────────────────────────────────────────────────────

        [Test]
        public void AllVulnerability_IsIdentityWithoutAPool_AndTieredWithOne()
        {
            Assert.AreEqual(1f, AlicornShieldRules.AllVulnerabilityMultiplier(0f, 1), 1e-4f);
            Assert.AreEqual(1f, AlicornShieldRules.AllVulnerabilityMultiplier(0f, 3), 1e-4f);

            Assert.AreEqual(0.6f, AlicornShieldRules.AllVulnerabilityMultiplier(300f, 1), 1e-4f);
            Assert.AreEqual(0.6f, AlicornShieldRules.AllVulnerabilityMultiplier(300f, 2), 1e-4f);
            Assert.AreEqual(0.4f, AlicornShieldRules.AllVulnerabilityMultiplier(500f, 3), 1e-4f);

            // The last hit that empties the pool: the multiplier is recomputed from the post-hit pool,
            // so it is back to 1 on the same tick the shield stops existing.
            Assert.AreEqual(1f, AlicornShieldRules.AllVulnerabilityMultiplier(0f, 3), 1e-4f);
        }

        // ── the whole ladder ────────────────────────────────────────────────────

        [Test]
        public void Ladder_CastsOnTheNinetiethAwareTick_ThenLocksOutFor850()
        {
            int t = AlicornShieldRules.InitialCastTimer(1);
            float hp = 0f;

            // 89 aware ticks is one short...
            for (int i = 0; i < 89; i++) Ladder(ref t, ref hp, alerted: true, tier: 1);

            Assert.AreEqual(1, t);
            Assert.AreEqual(0f, hp, 1e-4f, "89 ticks must not have cast yet.");

            // ...and the 90th is the one that does, in the same tick that reaches zero.
            Ladder(ref t, ref hp, alerted: true, tier: 1);

            Assert.AreEqual(300f, hp, 1e-4f, "castShit fills the pool to shitMaxHp.");
            Assert.AreEqual(1000, t, "castShit writes t_shit = 1000.");
            Assert.AreEqual(0.6f, AlicornShieldRules.AllVulnerabilityMultiplier(hp, 1), 1e-4f);

            // Now 850 unaware ticks: the `> 150` term keeps the timer falling until it reaches 150, and
            // then it stops — the shielded alicorn cannot re-cast sooner than 850 ticks (~14 s at 60 Hz).
            for (int i = 0; i < 850; i++) Ladder(ref t, ref hp, alerted: false, tier: 1);

            Assert.AreEqual(150, t, "The timer parks at the threshold while the unit is unaware.");
            Assert.AreEqual(300f, hp, 1e-4f, "and the pool is still up, so nothing re-cast.");

            // Awareness resumes: the remaining 150 ticks run down, and the cast needs an EMPTY pool —
            // which only happens once the damage pipeline has spent it.
            for (int i = 0; i < 150; i++) Ladder(ref t, ref hp, alerted: true, tier: 1);

            Assert.AreEqual(0, t);
            Assert.AreEqual(300f, hp, 1e-4f,
                "The timer expiring while the pool is full must NOT re-cast (AS3 `shithp <= 0 &&`).");

            // The pool is spent by damage, and the next tick raises it again.
            hp = 0f;
            Ladder(ref t, ref hp, alerted: true, tier: 1);

            Assert.AreEqual(300f, hp, 1e-4f, "Empty pool + expired timer = the second shield.");
            Assert.AreEqual(1000, t);
        }

        [Test]
        public void Ladder_Tr3_StartsWithAShorterTimerAndALargerPool()
        {
            int t = AlicornShieldRules.InitialCastTimer(3);
            float hp = 0f;

            Assert.AreEqual(45, t);

            for (int i = 0; i < 45; i++) Ladder(ref t, ref hp, alerted: true, tier: 3);

            Assert.AreEqual(500f, hp, 1e-4f, "UnitAlicorn.as:173");
            Assert.AreEqual(0.4f, AlicornShieldRules.AllVulnerabilityMultiplier(hp, 3), 1e-4f,
                "UnitAlicorn.as:589");
        }
    }
}

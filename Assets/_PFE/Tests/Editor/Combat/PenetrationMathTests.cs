using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="PenetrationMath"/> — the port of AS3's <c>probiv</c> penetration budget,
    /// <c>weapon/Bullet.as:533</c> and <c>unit/Unit.as:3684-3696</c>.
    ///
    /// <para><b>Why this needs a guard at all.</b> The port had no penetration mechanic — it had a
    /// pass-through <i>chance</i>, fed by the weapon's <c>@pier</c>, which is a flat armour figure in
    /// the 5..70 range. <c>Clamp01</c> turned every one of the 29 weapons carrying <c>@pier</c> into a
    /// 100% penetrator, so "which quantity is this" was the whole bug. The tests below therefore pin
    /// the three branches of the spend separately, because a single happy-path case cannot distinguish
    /// "the branch is right" from "the branch is never reached".</para>
    ///
    /// <para>The end-to-end wiring — that <c>Projectile.HandleImpact</c> calls this instead of rolling —
    /// belongs to the projectile's own tests. What is pinned here is the arithmetic, which is Unity-free
    /// by construction and so can also be executed outside the editor against wrong variants.</para>
    /// </summary>
    [TestFixture]
    public class PenetrationMathTests
    {
        private const float Tolerance = 1e-4f;

        // ── The stop gate (weapon/Bullet.as:533) ─────────────────────────────

        [Test]
        public void KeepsFlying_RequiresBothAPenetratorAndDamageLeft()
        {
            // AS3: `if(!(this.probiv > 0 && this.damage > 0)) { ...stop... }`. Both halves matter: the
            // first is "is this a penetrator at all", the second is what eventually stops it.
            Assert.IsTrue(PenetrationMath.KeepsFlying(0.5f, 10f), "a penetrator with damage left flies on");
            Assert.IsFalse(PenetrationMath.KeepsFlying(0f, 10f), "an ordinary round stops");
            Assert.IsFalse(PenetrationMath.KeepsFlying(0.5f, 0f), "an exhausted penetrator stops");
            Assert.IsFalse(PenetrationMath.KeepsFlying(0f, 0f));
        }

        [Test]
        public void KeepsFlying_DoesNotTreatTheWeaponsPierAsAPenetration()
        {
            // The regression this whole file exists for. `@pier` values are 5, 30, 50 and 70. Fed to
            // the old chance roll they all clamped to 1.0 — a certainty — so a weapon with `@pier`
            // pierced every unit, every shot, and a weapon without it never pierced at all.
            //
            // `KeepsFlying` is only reachable with a probiv, and the projectile now takes that from
            // WeaponDefinition.penetration, which is read from `<dop probiv>` — a 0..1 figure — and
            // never from `@pier`. This asserts the shape of the *gate* on a probiv-like input; the
            // importer side is pinned by the weapon-import tests.
            Assert.IsTrue(PenetrationMath.KeepsFlying(0.3f, 5f));
            Assert.IsFalse(PenetrationMath.KeepsFlying(0f, 5f),
                "a round with no probiv stops, however large its @pier is");
        }

        // ── The spend (Unit.as:3684-3696) ────────────────────────────────────

        [Test]
        public void Spend_BigTargetSwallowsTheRound()
        {
            // Branch 1: `if(this.maxhp > param1 * 20) param3.damage = 0`. A target twenty times the
            // size of the hit absorbs it completely — the round does not come out the other side.
            float spent = PenetrationMath.Spend(remainingDamage: 10f, penetration: 0.8f, targetMaxHealth: 300f);

            Assert.AreEqual(0f, spent, Tolerance, "300 > 10 * 20, so the round is consumed outright");
        }

        [Test]
        public void Spend_LargerTargetDampsByProbiv()
        {
            // Branch 2: `else if(this.maxhp > param1) param3.damage *= param3.probiv`.
            float spent = PenetrationMath.Spend(remainingDamage: 10f, penetration: 0.5f, targetMaxHealth: 40f);

            Assert.AreEqual(5f, spent, Tolerance, "40 > 10 but not > 200, so 10 * 0.5");
        }

        [Test]
        public void Spend_TargetTheHitWouldKillLetsMostOfItThrough()
        {
            // Branch 3: `param3.damage *= 1 - (1 - param3.probiv) * this.maxhp / param1`.
            //
            // 10 * (1 - (1 - 0.5) * 8 / 10) = 10 * (1 - 0.4) = 6.
            float spent = PenetrationMath.Spend(remainingDamage: 10f, penetration: 0.5f, targetMaxHealth: 8f);

            Assert.AreEqual(6f, spent, Tolerance, "a target the hit would kill costs it only 40%");
        }

        [Test]
        public void Spend_BranchOrderIsLoadBearing()
        {
            // The three branches overlap at their boundaries and the oracle tests them in order, so the
            // order is the behaviour. At maxhp == damage * 20 exactly, branch 1 fails (`>` is strict)
            // and branch 2 applies. Swapping the two would silently change this case.
            float spent = PenetrationMath.Spend(remainingDamage: 10f, penetration: 0.5f, targetMaxHealth: 200f);

            Assert.AreEqual(5f, spent, Tolerance,
                "200 is not > 200, so this is branch 2 — `maxhp > damage` — and the round is damped");
        }

        [Test]
        public void Spend_FullPenetrationCostsNothing()
        {
            Assert.AreEqual(10f, PenetrationMath.Spend(10f, 1f, 8f), Tolerance,
                "probiv = 1 passes the whole round through");
        }

        [Test]
        public void Spend_IsMeaninglessAtZeroPenetration_BecauseTheGateStopsItFirst()
        {
            // probiv = 0 is an ordinary round, and AS3 gates the entire spend on `probiv > 0`
            // (`Unit.as:3684`), so `Spend` is unreachable at zero. It is NOT harmless there, though:
            // branch 3 degenerates to `remaining - maxhp` (here 10 - 8 = 2), which reads as "the round
            // was cost something" for a round that was never a penetrator at all. That is exactly why
            // the gate is a separate function and has to be asked first — a caller that reached for the
            // spend alone would quietly spend non-penetrating rounds.
            Assert.IsFalse(PenetrationMath.KeepsFlying(0f, 10f),
                "the gate is what makes this unreachable, and the gate is what must be asked");
            Assert.AreEqual(2f, PenetrationMath.Spend(10f, 0f, 8f), Tolerance,
                "documented degeneracy, not a behaviour anyone should rely on");
        }

        [Test]
        public void Spend_NeverReturnsNaNOrNegative_AndItsTwoDefencesCoverEachOther()
        {
            // The contract: whatever a caller passes, what comes back is a usable number. A NaN here is
            // not a wrong number, it is a permanent corruption of a unit's velocity.
            //
            // This is written against the CONTRACT rather than against one line, and that is a
            // correction. An earlier version of this test asserted `Spend(0, 0.5, 8) == 0` with a
            // comment claiming it pinned the `remainingDamage <= 0f` early-out. It did not: with that
            // early-out removed by mutation, all eleven tests in this file still passed. The reason is
            // that the implementation defends the degenerate cases TWICE — the early-out and the
            // `spent > 0f ? spent : 0f` clamp — and for every reachable input branch 1 catches the
            // non-positive damage before either is consulted, so removing EITHER half alone is
            // unobservable. Measured over a 112-case grid (damage x probiv x maxhp, both signs) the
            // early-out changes the result in exactly three cases, all needing a negative max health.
            //
            // So the check that bites is removing BOTH defences, which is what the mutation harness
            // does; the assertions below are the ones that go red when it does.
            foreach (float damage in new[] { 0f, -1f, -10f, -100f })
            {
                foreach (float maxHealth in new[] { 0f, -1f, -100f, 8f })
                {
                    float spent = PenetrationMath.Spend(damage, 0.5f, maxHealth);

                    Assert.IsFalse(float.IsNaN(spent),
                        $"damage {damage} against maxhp {maxHealth} produced NaN");
                    Assert.GreaterOrEqual(spent, 0f,
                        $"damage {damage} against maxhp {maxHealth} produced a negative spend");
                }
            }

            Assert.AreEqual(0f, PenetrationMath.Spend(0f, 0.5f, 8f), Tolerance);
            Assert.AreEqual(0f, PenetrationMath.Spend(-1f, 0.5f, 8f), Tolerance);
        }

        [Test]
        public void Spend_IsMonotonicInPenetration()
        {
            // A property rather than a case: more probiv must never cost the round more. This is what a
            // sign error in branch 3 would break, and it holds across all three branches.
            float previous = -1f;

            for (int i = 0; i <= 10; i++)
            {
                float probiv = i / 10f;
                float spent  = PenetrationMath.Spend(remainingDamage: 10f, penetration: probiv,
                                                    targetMaxHealth: 6f);

                Assert.GreaterOrEqual(spent, previous,
                    $"probiv {probiv} cost {spent}, less than the previous step's {previous}");
                previous = spent;
            }

            Assert.AreEqual(10f, previous, Tolerance, "at probiv = 1 the round is untouched");
        }

        [Test]
        public void Spend_AWholeSequenceEventuallyExhaustsTheRound()
        {
            // The end-to-end property the mechanic exists for: a penetrator must not run the room. This
            // is the shape the old `Chance()` roll could not produce at all — a chance never spends
            // anything, so a lucky round passed through every unit forever.
            float remaining = 40f;
            const float probiv = 0.3f;
            int passedThrough = 0;

            while (PenetrationMath.KeepsFlying(probiv, remaining) && passedThrough < 1000)
            {
                remaining = PenetrationMath.Spend(remaining, probiv, targetMaxHealth: 50f);
                passedThrough++;
            }

            Assert.Less(passedThrough, 1000, "the budget must run out");
            Assert.AreEqual(0f, remaining, Tolerance);
            Assert.Greater(passedThrough, 1, "one hit must not exhaust a penetrator");
        }

        // ── An evaded hit costs nothing (Unit.as:4067-4109) ──────────────────

        [Test]
        public void SpendOnHit_AnEvadedHitLeavesTheBudgetUntouched()
        {
            // `Unit.udarBullet` calls `this.damage(...)` — the only place the spend lives — on the
            // LANDED path, and the miss branch returns -1 at `Unit.as:4109` without ever reaching it.
            // So the two calls below must differ, and only the landed one may cost anything.
            Assert.AreEqual(6f, PenetrationMath.SpendOnHit(10f, 0.5f, 8f, landed: true), Tolerance,
                "a landed hit spends, exactly as Spend does");
            Assert.AreEqual(10f, PenetrationMath.SpendOnHit(10f, 0.5f, 8f, landed: false), Tolerance,
                "an evaded hit must return the budget unchanged");
        }

        [Test]
        public void SpendOnHit_AUnitThatKeepsEvadingNeverExhaustsTheRound()
        {
            // The regression this function exists for, stated as a property. Spending on a miss drains
            // the round on units it never touched, so it stops a unit or two sooner than the oracle's
            // would — and nothing about the individual numbers looks wrong while that happens.
            float remaining = 40f;
            const float probiv = 0.3f;

            for (int i = 0; i < 200; i++)
                remaining = PenetrationMath.SpendOnHit(remaining, probiv, targetMaxHealth: 50f, landed: false);

            Assert.AreEqual(40f, remaining, Tolerance, "200 evasions cost the round nothing");
            Assert.IsTrue(PenetrationMath.KeepsFlying(probiv, remaining),
                "and it is still a penetrator afterwards");
        }

        [Test]
        public void SpendOnHit_StillExhaustsTheRoundWhenTheHitsLand()
        {
            // The complement, so the test above cannot be satisfied by a function that never spends at
            // all — which is precisely the degenerate implementation a one-sided property invites.
            float remaining = 40f;
            const float probiv = 0.3f;
            int landed = 0;

            while (PenetrationMath.KeepsFlying(probiv, remaining) && landed < 1000)
            {
                remaining = PenetrationMath.SpendOnHit(remaining, probiv, targetMaxHealth: 50f, landed: true);
                landed++;
            }

            Assert.Less(landed, 1000, "landed hits must exhaust the budget");
            Assert.AreEqual(0f, remaining, Tolerance);
        }
    }
}

using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="MeleeChargeMath"/> — AS3's <b>power charge</b>: the <c>t_auto</c>/<c>pow</c>
    /// clock (<c>Weapon.as:1226-1233</c>), the debounce branch that feeds it
    /// (<c>Weapon.as:1297-1302</c>), the freeze predicate (<c>WClub.as:340</c>) and the multiplier
    /// (<c>WClub.as:351</c>).
    ///
    /// <para><b>Why this exists as a separate fixture.</b> The class that consumes the rule,
    /// <c>MeleeWeaponController</c>, touches <c>UnityEngine.Vector2</c>, so every fixture that drives
    /// it — <c>MeleeHitWindowTests</c>, <c>MeleeOwnerStatTests</c> — fails in the offline wall with
    /// <c>SecurityException: ECall methods must be packaged into a system module</c>. Measured on the
    /// 2026-10-12 baseline: those two fixtures are <b>0 passed / 26 failed</b> offline. The charge
    /// therefore had no offline coverage at all until the arithmetic moved here, which is the same
    /// reason <c>UnitSweepMath</c>, <c>WeaponWearMath</c> and <c>MeleeTileSweepMath</c> are separate
    /// files.</para>
    ///
    /// <para><b>Every boundary below is derived, not observed.</b> At <c>rapid_act = 15</c>:
    /// <c>5/6 * 15 = 12.5</c>, so <c>t_attack</c> 12 charges and 13 does not; <c>2.15 * 15 = 32.25</c>,
    /// so <c>pow</c> 32 charges and 33 does not. Both oracle bounds are <i>strict</i>
    /// (<c>&lt;</c>, not <c>&lt;=</c>), and a fixture that guessed the boundary would pass on a mutant
    /// that used the other one.</para>
    /// </summary>
    [TestFixture]
    public class MeleeChargeMathTests
    {
        /// <summary>The club's speed in AS3 (<c>WClub.as:113</c> — <c>speed = 15</c>), which is what
        /// <c>rapid_act</c> collapses to when <c>skillConf</c>, <c>rapidMult</c> and
        /// <c>rapidMultCont</c> are all 1. Chosen because it makes both bounds fractional
        /// (12.5 and 32.25), so an off-by-one in the comparison shows up.</summary>
        private const float RapidAct = 15f;

        // ── The t_auto / pow clock ────────────────────────────────────────────

        [Test]
        public void TheClockCountsDownWhileTheWindowIsArmed()
        {
            int tAuto = 3;
            int pow   = 5;

            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);

            Assert.That(tAuto, Is.EqualTo(2), "t_auto decrements by one per frame");
            Assert.That(pow, Is.EqualTo(5), "pow survives while the window is still armed");
        }

        [Test]
        public void TheClockZeroesPowOnTheLapseFrame()
        {
            // `else this.pow = 0` is the ELSE of the t_auto test (Weapon.as:1230-1233), so the reset
            // happens on the frame t_auto is ALREADY 0 — one frame after it was decremented to 0, not
            // on that same frame. This is what bounds the charge.
            int tAuto = 0;
            int pow   = 40;

            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);

            Assert.That(tAuto, Is.EqualTo(0), "t_auto stays at 0");
            Assert.That(pow, Is.EqualTo(0), "pow dies the moment the window lapses");
        }

        [Test]
        public void TheClockTakesThreeFramesToZeroPowAfterTheLastPress()
        {
            // The full decay from a fresh press: 3 -> 2 -> 1 -> 0 -> pow reset. Four calls, and the
            // reset lands on the fourth. A "release" implemented as an immediate zero would pass the
            // single-step test above and fail this one.
            int tAuto = 3;
            int pow   = 9;

            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);   // 2
            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);   // 1
            Assert.That(pow, Is.EqualTo(9), "still inside the window after two frames");

            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);   // 0
            Assert.That(tAuto, Is.EqualTo(0));
            Assert.That(pow, Is.EqualTo(9), "the frame that reaches 0 is not yet the reset frame");

            MeleeChargeMath.AdvanceClock(ref tAuto, ref pow);   // lapse
            Assert.That(pow, Is.EqualTo(0), "three frames after the last press, pow is gone");
        }

        // ── The debounce branch that feeds the clock ─────────────────────────

        [Test]
        public void TheDebounceBranchNeedsANonAutoWeaponAndAnArmedWindow()
        {
            Assert.IsTrue(MeleeChargeMath.TakesDebounceBranch(isAuto: false, tAuto: 3));
            Assert.IsTrue(MeleeChargeMath.TakesDebounceBranch(isAuto: false, tAuto: 1),
                "any armed window counts, not just a freshly armed one");
        }

        [Test]
        public void AnAutoWeaponNeverTakesTheDebounceBranch()
        {
            // AS3 `!this.auto` (Weapon.as:1297). An auto weapon fires again on the held button
            // instead, so it never charges — which is also why `HasCombo` excludes auto weapons.
            Assert.IsFalse(MeleeChargeMath.TakesDebounceBranch(isAuto: true, tAuto: 3));
            Assert.IsFalse(MeleeChargeMath.TakesDebounceBranch(isAuto: true, tAuto: 0));
        }

        [Test]
        public void AnUnarmedWindowDoesNotTakeTheDebounceBranch()
        {
            Assert.IsFalse(MeleeChargeMath.TakesDebounceBranch(isAuto: false, tAuto: 0),
                "t_auto == 0 is the normal attack path, not the charge path");
        }

        // ── The freeze predicate ──────────────────────────────────────────────

        [Test]
        public void TheChargeNeedsAllFourTerms()
        {
            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 12, 3, RapidAct), "the happy path");

            Assert.IsFalse(MeleeChargeMath.IsCharging(false, 12, 3, RapidAct), "not a power weapon");
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 12, 2, RapidAct), "pow must exceed 2");
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 13, 3, RapidAct), "still winding up");
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 12, 33, RapidAct), "past the cap");
        }

        [Test]
        public void TheChargeArmsOnlyBelowFiveSixthsOfRapidAct()
        {
            // Strict `<` against 5/6 (WClub.as:340). At rapid_act 15 that is 12.5, so 12 arms and 13
            // does not — a `<=` mutant would let 13 through, and at 12.5 exactly it would let the
            // wind-up frame itself charge.
            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 12, 3, RapidAct));
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 13, 3, RapidAct));
        }

        [Test]
        public void TheArmBoundaryIsStrictWhereItLandsOnAWholeFrame()
        {
            // At rapid_act 12 the bound 5/6 * 12 = 10 is an INTEGER, and that is the only case where
            // `<` and `<=` differ for an int t_attack. Every other fixture here uses rapid_act 15,
            // where 12.5 is fractional and the two operators agree for every integer — so without this
            // case a `<=` mutant passes the whole file. Derived, not observed: 5/6 * 12 == 10 exactly.
            const float rapidAct12 = 12f;

            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 9, 3, rapidAct12),
                "9 is below the bound");
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 10, 3, rapidAct12),
                "10 IS the bound, and the oracle's test is strict (`t_attack < rapid_act*5/6`)");
        }

        [Test]
        public void TheCapBoundaryIsStrictWhereItLandsOnAWholeFrame()
        {
            // Same reasoning for the upper bound: 2.15 * 20 == 43 exactly, so rapid_act 20 is the case
            // that distinguishes `<` from `<=`. pow 43 must NOT charge — the swing releases there.
            const float rapidAct20 = 20f;

            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 16, 42, rapidAct20),
                "42 is one below the cap");
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 16, 43, rapidAct20),
                "43 IS the cap, and the oracle's test is strict (`pow < rapid_act*2.15`)");
        }

        [Test]
        public void AChargeCannotStartDuringTheWindUpHoweverLongTheButtonIsHeld()
        {
            // The whole point of the 5/6 gate: mashing from the very first frame still has to let the
            // swing wind up. Without it a charge could freeze a swing that had not started moving,
            // which would look like the weapon jamming on press.
            for (int tAttack = 13; tAttack <= 60; tAttack++)
                Assert.IsFalse(MeleeChargeMath.IsCharging(true, tAttack, 999, RapidAct),
                    $"t_attack {tAttack} is still in the wind-up");
        }

        [Test]
        public void TheChargeNeedsMoreThanTwoHeldFrames()
        {
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 12, 2, RapidAct));
            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 12, 3, RapidAct));
        }

        [Test]
        public void TheChargeReleasesOnTheFramePowReachesTheCap()
        {
            // Strict `<` against 2.15 * rapid_act (WClub.as:340). At rapid_act 15 the cap is 32.25,
            // so 32 still charges and 33 does not — the release is one frame earlier than a `<=`
            // reading of the same constant would give.
            Assert.IsTrue(MeleeChargeMath.IsCharging(true, 12, 32, RapidAct));
            Assert.IsFalse(MeleeChargeMath.IsCharging(true, 12, 33, RapidAct));
        }

        // ── The multiplier ────────────────────────────────────────────────────

        [Test]
        public void TheMultiplierIsOnePlusPowOverTheCap()
        {
            // WClub.as:351 — 1 + pow / (rapid_act * 2.15).
            Assert.That(MeleeChargeMath.PowerMultiplier(0, RapidAct), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(MeleeChargeMath.PowerMultiplier(32, RapidAct),
                Is.EqualTo(1f + 32f / 32.25f).Within(1e-6f));
        }

        [Test]
        public void TheMultiplierIsJustUnderTwoAtTheCap()
        {
            // The cap is self-consistent: pow is bounded by rapid_act * 2.15 and the divisor is the
            // same quantity, so the ceiling of the charge is "almost exactly double damage" and does
            // not drift if `rapid_act` changes. A fixture that only checked a mid-range value would
            // not notice the two constants being decoupled.
            float atCap = MeleeChargeMath.PowerMultiplier(32, RapidAct);   // 32.25 is not an int
            Assert.That(atCap, Is.GreaterThan(1.95f));
            Assert.That(atCap, Is.LessThan(2f));
        }

        [Test]
        public void TheMultiplierGrowsWithPowAndShrinksWithRapidAct()
        {
            Assert.That(MeleeChargeMath.PowerMultiplier(20, RapidAct),
                Is.GreaterThan(MeleeChargeMath.PowerMultiplier(10, RapidAct)));

            // A longer swing charges to the same absolute cap, so the same pow is worth less — which
            // is why the charge is measured against rapid_act and not a fixed frame count.
            Assert.That(MeleeChargeMath.PowerMultiplier(20, 40f),
                Is.LessThan(MeleeChargeMath.PowerMultiplier(20, RapidAct)));
        }

        // ── The constants are the oracle's ────────────────────────────────────

        [Test]
        public void TheConstantsAreTheOracleLiterals()
        {
            // Pinned so the numbers cannot drift silently. 2.15 and 5/6 appear four and two times
            // respectively across WClub.as:340-378; a "tidy" rewrite to 2.0 or 0.8 would be a real
            // behavioural change dressed as a cleanup.
            Assert.That(MeleeChargeMath.ChargeCapFactor, Is.EqualTo(2.15f));
            Assert.That(MeleeChargeMath.ChargeArmFraction, Is.EqualTo(5f / 6f));
            Assert.That(MeleeChargeMath.ChargeMinPow, Is.EqualTo(2));
        }
    }
}

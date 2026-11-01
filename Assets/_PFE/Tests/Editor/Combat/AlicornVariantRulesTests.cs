using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the alicorn's per-variant table and every gate the family's four rows hang off —
    /// <c>UnitAlicorn.as:121-229</c> (the constructor's four branches), <c>:415-429</c> (the two reactive
    /// reflexes), <c>:534-551</c> (the <c>pole</c> objective) and <c>:1026-1084</c> (the telekinesis
    /// cycle).
    ///
    /// <para><b>Why a fixture and not the brain.</b> <c>AlicornBrain</c> and <c>AlicornController</c> are
    /// <c>MonoBehaviours</c>; touching either needs <c>AddComponent</c>, which is a Unity <c>ECall</c> and
    /// throws where the native engine is absent. The rules were extracted to
    /// <see cref="AlicornVariantRules"/> for exactly this reason — see
    /// <c>AlicornShieldRulesTests</c>'s header for the same argument about the shield.</para>
    ///
    /// <para><b>The numbers are the oracle's, and the test names say where.</b> Every expectation below
    /// is a literal read off <c>UnitAlicorn.as</c> — not a value copied back out of the implementation.
    /// A fixture that asserts whatever the code happens to do is a change detector, not a guard.</para>
    /// </summary>
    [TestFixture]
    public class AlicornVariantRulesTests
    {
        // ── the variant table (constructor, :153-180) ─────────────────────────────────────────────

        [Test]
        public void Tier1_IsThePlainCaster_EveryValueIsTheFieldDefault()
        {
            AlicornTuning t = AlicornVariantRules.For(1);

            // :153-156 — tr1 overrides nothing, so the row is :35/:39/:55/:57/:59/:65/:67.
            Assert.AreEqual(1, t.Tier);
            Assert.AreEqual(1, t.SuperSilaTip, "superSilaTip default is 1 (cloak)");
            Assert.AreEqual(16f, t.TeleSpeed);
            Assert.AreEqual(4f, t.TeleAccel);
            Assert.AreEqual(30f, t.ThrowForce);
            Assert.AreEqual(600, t.TeleResTicks);
            Assert.AreEqual(-500, t.SuperUsedCooldown);
        }

        [Test]
        public void Tier2_IsTheTelekineticVariant_StrongerThrowAndFasterCycle()
        {
            AlicornTuning t = AlicornVariantRules.For(2);

            // :157-168 — super = teleport, teleSpeed 20, accel 5, throwForce 45, tTeleRes 400, tUsed -250.
            Assert.AreEqual(2, t.Tier);
            Assert.AreEqual(2, t.SuperSilaTip);
            Assert.AreEqual(20f, t.TeleSpeed);
            Assert.AreEqual(5f, t.TeleAccel);
            Assert.AreEqual(45f, t.ThrowForce);
            Assert.AreEqual(400, t.TeleResTicks);
            Assert.AreEqual(-250, t.SuperUsedCooldown);
        }

        [Test]
        public void Tier3_IsThePsionicVariant_TheShieldIsTheOnlyDifference()
        {
            AlicornTuning t = AlicornVariantRules.For(3);

            // :169-180 — super = psionic bolt. Its shield half lives in AlicornShieldRules; every
            // telekinesis value stays at the field default.
            Assert.AreEqual(3, t.Tier);
            Assert.AreEqual(3, t.SuperSilaTip);
            Assert.AreEqual(16f, t.TeleSpeed);
            Assert.AreEqual(4f, t.TeleAccel);
            Assert.AreEqual(30f, t.ThrowForce);
            Assert.AreEqual(600, t.TeleResTicks);
            Assert.AreEqual(-500, t.SuperUsedCooldown);
        }

        [Test]
        public void Tier0_TheFamilyTemplate_AnswersWithTheTier1Row()
        {
            // `alicorn` (no digit) is never spawned by the oracle, but a bare test spawn or a data typo
            // resolves to 0. It must not fall through to a zeroed row: teleSpeed 0 would make the hold
            // controller refuse to move anything.
            AlicornTuning t = AlicornVariantRules.For(0);

            Assert.AreEqual(1, t.SuperSilaTip);
            Assert.AreEqual(16f, t.TeleSpeed);
            Assert.AreEqual(4f, t.TeleAccel);
            Assert.AreEqual(30f, t.ThrowForce);
            Assert.AreEqual(600, t.TeleResTicks);
        }

        [Test]
        public void Tier2_IsTheOnlyTierThatDiffersFromTheDefaults()
        {
            // Guards against a future edit that "tidies" the switch into one row: the whole reason the
            // family has four entries is that exactly one of them is different.
            AlicornTuning one = AlicornVariantRules.For(1);
            AlicornTuning two = AlicornVariantRules.For(2);
            AlicornTuning three = AlicornVariantRules.For(3);

            Assert.AreNotEqual(one.TeleSpeed, two.TeleSpeed);
            Assert.AreNotEqual(one.ThrowForce, two.ThrowForce);
            Assert.AreNotEqual(one.TeleResTicks, two.TeleResTicks);
            Assert.AreNotEqual(one.SuperUsedCooldown, two.SuperUsedCooldown);
            Assert.AreNotEqual(one.SuperSilaTip, two.SuperSilaTip);

            Assert.AreEqual(one.TeleSpeed, three.TeleSpeed);
            Assert.AreEqual(one.ThrowForce, three.ThrowForce);
            Assert.AreNotEqual(one.SuperSilaTip, three.SuperSilaTip);
        }

        // ── osob (:155/:167/:179, forced on globalDif 4 at :181-184) ─────────────────────────────

        [Test]
        public void OsobChance_Is70_50_40_AndZeroForTheTemplate()
        {
            Assert.AreEqual(0.7f, AlicornVariantRules.OsobChance(1), 1e-6);
            Assert.AreEqual(0.5f, AlicornVariantRules.OsobChance(2), 1e-6);
            Assert.AreEqual(0.4f, AlicornVariantRules.OsobChance(3), 1e-6);
            Assert.AreEqual(0f, AlicornVariantRules.OsobChance(0), 1e-6);
        }

        [Test]
        public void RollsOsob_IsAStrictLessThan_AtTheTierChance()
        {
            Assert.IsTrue(AlicornVariantRules.RollsOsob(2, 0.4999f, maxDifficulty: false));
            Assert.IsFalse(AlicornVariantRules.RollsOsob(2, 0.5f, maxDifficulty: false),
                "Math.random() < 0.5 is strict — an exact 0.5 is a miss");
        }

        [Test]
        public void RollsOsob_IsForcedOnTheHardestDifficulty()
        {
            Assert.IsTrue(AlicornVariantRules.RollsOsob(2, 0.999f, maxDifficulty: true));
            Assert.IsTrue(AlicornVariantRules.RollsOsob(3, 0.999f, maxDifficulty: true));
        }

        [Test]
        public void RollsOsob_IsNeverTrueForTheTemplate_EvenAtRollZero()
        {
            // Chance 0 must not mean "always": `roll < 0` is false for every non-negative draw, and the
            // guard is written as `chance > 0 && roll < chance` rather than `roll < chance` alone so a
            // negative roll cannot sneak through either.
            Assert.IsFalse(AlicornVariantRules.RollsOsob(0, 0f, maxDifficulty: false));
            Assert.IsFalse(AlicornVariantRules.RollsOsob(0, -1f, maxDifficulty: false));
        }

        // ── the reactive reflexes (:415-429) ─────────────────────────────────────────────────────

        [Test]
        public void ReactiveBlast_IsTier1_Osob_Unblasted_Melee_AndCalm()
        {
            Assert.IsTrue(AlicornVariantRules.ShouldBlastOnMeleeHit(
                tier: 1, osob: true, alreadyBlasted: false, hitByMeleeWeapon: true, familyState: 1));
        }

        [Test]
        public void ReactiveBlast_IsRefusedByEachGateInTurn()
        {
            // One assert per term, so a dropped term names itself.
            Assert.IsFalse(AlicornVariantRules.ShouldBlastOnMeleeHit(2, true, false, true, 1), "tier");
            Assert.IsFalse(AlicornVariantRules.ShouldBlastOnMeleeHit(1, false, false, true, 1), "osob");
            Assert.IsFalse(AlicornVariantRules.ShouldBlastOnMeleeHit(1, true, true, true, 1), "blasted");
            Assert.IsFalse(AlicornVariantRules.ShouldBlastOnMeleeHit(1, true, false, false, 1),
                "a bolt is not tip == 1");
            Assert.IsFalse(AlicornVariantRules.ShouldBlastOnMeleeHit(1, true, false, true, 2),
                "aiState <= 1 only — an alerted alicorn does not startle");
        }

        [Test]
        public void ReactiveBlast_FiresWhileIdleAndPatrolling()
        {
            Assert.IsTrue(AlicornVariantRules.ShouldBlastOnMeleeHit(1, true, false, true, 0));
            Assert.IsTrue(AlicornVariantRules.ShouldBlastOnMeleeHit(1, true, false, true, 1));
        }

        [Test]
        public void ReactivePort_IsTier2_Osob_Unblasted_PlayerSourced_AndCalm()
        {
            Assert.IsTrue(AlicornVariantRules.ShouldPortAwayFromPlayerHit(
                tier: 2, osob: true, alreadyBlasted: false, hitByPlayerWeapon: true, familyState: 1));
        }

        [Test]
        public void ReactivePort_IsRefusedByEachGateInTurn()
        {
            Assert.IsFalse(AlicornVariantRules.ShouldPortAwayFromPlayerHit(1, true, false, true, 1), "tier");
            Assert.IsFalse(AlicornVariantRules.ShouldPortAwayFromPlayerHit(2, false, false, true, 1), "osob");
            Assert.IsFalse(AlicornVariantRules.ShouldPortAwayFromPlayerHit(2, true, true, true, 1), "blasted");
            Assert.IsFalse(AlicornVariantRules.ShouldPortAwayFromPlayerHit(2, true, false, false, 1),
                "param3.weap.owner.player — a turret cannot trigger it");
            Assert.IsFalse(AlicornVariantRules.ShouldPortAwayFromPlayerHit(2, true, false, true, 2), "calm");
        }

        [Test]
        public void TheTwoReflexesAreOnDifferentTiers_SoAtMostOneCanFire()
        {
            // Both share the `blasted` latch. If a future edit widened either tier test, the latch would
            // silently spend the other reflex.
            for (int tier = 0; tier <= 3; tier++)
            {
                bool blast = AlicornVariantRules.ShouldBlastOnMeleeHit(tier, true, false, true, 1);
                bool port = AlicornVariantRules.ShouldPortAwayFromPlayerHit(tier, true, false, true, 1);
                Assert.IsFalse(blast && port, "tier " + tier + " would spend both reflexes on one hit");
            }
        }

        // ── the super ladder (:526-533, :727-735, :1102-1138) ────────────────────────────────────

        [Test]
        public void Super_IsReadyStrictlyAfterTheWindUp()
        {
            Assert.IsFalse(AlicornVariantRules.IsSuperReady(AlicornVariantRules.SuperWindUpTicks),
                "t_gotov > tGotov, not >=");
            Assert.IsTrue(AlicornVariantRules.IsSuperReady(AlicornVariantRules.SuperWindUpTicks + 1));
        }

        [Test]
        public void Super_CooldownIs80To120PercentOfTheTierFloor()
        {
            // `Math.floor(tUsed * (Math.random() * 0.4 + 0.8))` (:1127).
            Assert.AreEqual(-200, AlicornVariantRules.SuperCooldownAfterUse(2, 0f));
            Assert.AreEqual(-250, AlicornVariantRules.SuperCooldownAfterUse(2, 0.5f));
            Assert.AreEqual(-300, AlicornVariantRules.SuperCooldownAfterUse(2, 1f));

            Assert.AreEqual(-400, AlicornVariantRules.SuperCooldownAfterUse(1, 0f));
            Assert.AreEqual(-600, AlicornVariantRules.SuperCooldownAfterUse(1, 1f));
        }

        [Test]
        public void Super_EndsOnTheTickTheTimerReachesZero()
        {
            Assert.IsFalse(AlicornVariantRules.ShouldEndSuper(1));
            Assert.IsTrue(AlicornVariantRules.ShouldEndSuper(0));
        }

        // ── the telekinesis cycle (:1026-1084) ───────────────────────────────────────────────────

        [Test]
        public void TeleGrabTick_IsExactlyZero()
        {
            Assert.IsTrue(AlicornVariantRules.IsTeleGrabTick(0));
            Assert.IsFalse(AlicornVariantRules.IsTeleGrabTick(1));
            Assert.IsFalse(AlicornVariantRules.IsTeleGrabTick(-1));
        }

        [Test]
        public void TeleAbortPhase_IsMinus50ThroughMinus149()
        {
            Assert.AreEqual(-50, AlicornVariantRules.TeleAbortPhase(0f));
            Assert.AreEqual(-149, AlicornVariantRules.TeleAbortPhase(0.999f));
            Assert.AreEqual(-100, AlicornVariantRules.TeleAbortPhase(0.5f));
        }

        [Test]
        public void TeleAbortPhase_IsAlwaysNegative_SoTheCycleNeverStallsAtZero()
        {
            // The bug this pins: a caller that "skipped" at phase 0 instead of re-phasing left t_tele
            // there forever, because it only passes through 0 once.
            for (float roll = 0f; roll < 1f; roll += 0.05f)
            {
                Assert.Less(AlicornVariantRules.TeleAbortPhase(roll), 0, "roll " + roll);
            }
        }

        [Test]
        public void TeleCycleAbortsAtThrowPlusTen()
        {
            Assert.IsFalse(AlicornVariantRules.ShouldAbortTeleCycle(
                AlicornVariantRules.TeleThrowTicks + AlicornVariantRules.TeleCycleAbortOffset - 1));
            Assert.IsTrue(AlicornVariantRules.ShouldAbortTeleCycle(
                AlicornVariantRules.TeleThrowTicks + AlicornVariantRules.TeleCycleAbortOffset));
        }

        [Test]
        public void TeleThrowWindow_OpensAt60()
        {
            Assert.IsFalse(AlicornVariantRules.ShouldThrowHeld(59));
            Assert.IsTrue(AlicornVariantRules.ShouldThrowHeld(60));
        }

        [Test]
        public void TelePhaseAfterThrow_IsMinusTeleRes_AndTheDropIsHalfOfIt()
        {
            Assert.AreEqual(-600, AlicornVariantRules.TelePhaseAfterThrow(1));
            Assert.AreEqual(-400, AlicornVariantRules.TelePhaseAfterThrow(2));
            Assert.AreEqual(-300, AlicornVariantRules.TelePhaseAfterDrop(1));
            Assert.AreEqual(-200, AlicornVariantRules.TelePhaseAfterDrop(2));
        }

        [Test]
        public void ThrowSpeed_IsDividedBySqrtMassa_OnlyAboveOne()
        {
            Assert.AreEqual(30f, AlicornVariantRules.ThrowSpeed(30f, 1f), 1e-5);
            Assert.AreEqual(30f, AlicornVariantRules.ThrowSpeed(30f, 0.5f), 1e-5);
            Assert.AreEqual(15f, AlicornVariantRules.ThrowSpeed(30f, 4f), 1e-5);
            Assert.AreEqual(45f / 2f, AlicornVariantRules.ThrowSpeed(45f, 4f), 1e-5);
        }

        [Test]
        public void LevitationForPlayer_IsHalvedExceptOnTier2()
        {
            Assert.AreEqual(60, AlicornVariantRules.LevitationForPlayer(2, 60));
            Assert.AreEqual(30, AlicornVariantRules.LevitationForPlayer(1, 60));
            Assert.AreEqual(30, AlicornVariantRules.LevitationForPlayer(3, 60));
        }

        [Test]
        public void PropIsGrabbable_NeedsLiftable_Unheld_AndMassaAtLeastOne()
        {
            Assert.IsTrue(AlicornVariantRules.PropIsGrabbable(true, false, 1f));
            Assert.IsFalse(AlicornVariantRules.PropIsGrabbable(false, false, 1f), "levitPoss && wall == 0");
            Assert.IsFalse(AlicornVariantRules.PropIsGrabbable(true, true, 1f), "already levitating");
            Assert.IsFalse(AlicornVariantRules.PropIsGrabbable(true, false, 0.5f), "massa >= 1");
        }

        // ── the pole objective (:534-551, :458-463) ─────────────────────────────────────────────

        [Test]
        public void ShouldOpenPole_IsStrictlyBelowSeventyPercent()
        {
            Assert.IsFalse(AlicornVariantRules.ShouldOpenPole(70f, 100f), "hp >= maxhp * 0.7 holds");
            Assert.IsTrue(AlicornVariantRules.ShouldOpenPole(69.9f, 100f));
            Assert.IsTrue(AlicornVariantRules.ShouldOpenPole(0f, 100f));
        }

        [Test]
        public void ShouldOpenPole_OpensForAUnitWithNoMaximum()
        {
            // maxhp 0 is not a state the oracle reaches, but the division it would imply is NaN, and a
            // NaN comparison is false in both directions — the door would never open and nothing would
            // say why. Answering `true` is the safe direction: the objective completes.
            Assert.IsTrue(AlicornVariantRules.ShouldOpenPole(0f, 0f));
        }

        // ── alarma (:431-450) ────────────────────────────────────────────────────────────────────

        [Test]
        public void Alarm_RemembersFor75WhenTheLandIsAlarmed_Otherwise30()
        {
            Assert.AreEqual(75, AlicornVariantRules.AlarmMaxSpok(landAlarmed: true));
            Assert.AreEqual(30, AlicornVariantRules.AlarmMaxSpok(landAlarmed: false));
        }

        [Test]
        public void Alarm_StartsAwarenessTenAboveMaxSpok()
        {
            Assert.AreEqual(85, AlicornVariantRules.AlarmAwareness(75));
            Assert.AreEqual(40, AlicornVariantRules.AlarmAwareness(30));
        }

        // ── the field defaults the whole family shares ───────────────────────────────────────────

        [Test]
        public void SharedConstants_MatchTheOracleFieldDefaults()
        {
            Assert.AreEqual(90, AlicornVariantRules.SuperWindUpTicks, "tGotov = 90 (:37)");
            Assert.AreEqual(300, AlicornVariantRules.SuperDurationTicks, "tSuper = 300 (:41)");
            Assert.AreEqual(-500, AlicornVariantRules.SuperUsedCooldown, "tUsed = -500 (:39)");
            Assert.AreEqual(-100, AlicornVariantRules.TeleCycleStart, "tTele = -100 (:117)");
            Assert.AreEqual(60, AlicornVariantRules.TeleThrowTicks, "tTeleThrow = 60 (:55)");
            Assert.AreEqual(600, AlicornVariantRules.TeleResTicks, "tTeleRes = 600 (:57)");
            Assert.AreEqual(15f, AlicornVariantRules.HoldDeadzone, "derp = 15 (:69)");
            Assert.AreEqual(600f, AlicornVariantRules.TeleRangePixels, "optDistTele = 600 (:97)");
            Assert.AreEqual(200f, AlicornVariantRules.ContactReachPixels, "optDistAtt = 200 (:95)");
            Assert.AreEqual(80f, AlicornVariantRules.ContactReachVerticalPixels, "celDY < 80 (:1008)");
            Assert.AreEqual(150f, AlicornVariantRules.UnitHoldOffsetX, "storona * 150 (:1042)");
            Assert.AreEqual(80f, AlicornVariantRules.PropHoldOffsetX, "storona * 80 (:1047)");
            Assert.AreEqual(-40f, AlicornVariantRules.HoldOffsetY, "teleY = Y - 40 (:1043)");
            Assert.AreEqual(1f, AlicornVariantRules.PropMinMassa, "massa >= 1 (:1155)");
            Assert.AreEqual(0.25f, AlicornVariantRules.UnitGrabChance, "isrnd(0.25) (:1143)");
            Assert.AreEqual(0.3f, AlicornVariantRules.PropGrabChance, "isrnd(0.3) (:1155)");
            Assert.AreEqual(0.7f, AlicornVariantRules.PoleHpFraction, "hp < maxhp * 0.7 (:545)");
            Assert.AreEqual(60, AlicornVariantRules.DefaultTeleEnemy, "Pers.teleEnemy = 60 (Pers.as:245)");
            Assert.AreEqual(2, AlicornVariantRules.LevitationForProp, "levit = 2 (:1188)");
            Assert.AreEqual(200f, AlicornVariantRules.DescentSinkWeight, "spd.y = 200 (:792)");
        }

        // ── flight: the state-4 descent's horizontal half (:790-798) ─────────────────────────────

        // The expectations below are computed BY HAND from the oracle's own expression, not read back
        // out of the implementation: AS3 builds the pair `(celX - X, 200)`, `norma` rescales it to the
        // unit's accel, and this port fixes the vertical component at `_descentSpeed = 2`. So the
        // horizontal component is `2 * |dx| / 200`, clamped at 2, signed by `dx`.
        //
        //     dx = ±100  ->  2 * 100 / 200  =  1.0    (the full `setCel` offset, :606)
        //     dx =  ±50  ->  2 *  50 / 200  =  0.5
        //     dx =  ±20  ->  2 *  20 / 200  =  0.2
        //     dx = ±400  ->  4.0  ->  clamped to 2.0

        [Test]
        public void DescentDrift_AtTheFullSetCelOffset_IsHalfTheSinkRate()
        {
            // ±100 is the widest offset `setCel(null, X + Math.random() * 200 - 100, …)` can roll
            // (:606), so 0.5 is the largest share of the sink the drift can ever take — the oracle's
            // 0.45 : 0.9 split, to within the clamp.
            Assert.AreEqual(1.0f, AlicornVariantRules.DescentDriftSpeed(100f, 2f), 1e-5f, "dx = +100");
            Assert.AreEqual(-1.0f, AlicornVariantRules.DescentDriftSpeed(-100f, 2f), 1e-5f, "dx = -100");
        }

        [Test]
        public void DescentDrift_IsProportionalToTheRemainingDistance()
        {
            Assert.AreEqual(0.5f, AlicornVariantRules.DescentDriftSpeed(50f, 2f), 1e-5f, "dx = +50");
            Assert.AreEqual(-0.5f, AlicornVariantRules.DescentDriftSpeed(-50f, 2f), 1e-5f, "dx = -50");
            Assert.AreEqual(0.2f, AlicornVariantRules.DescentDriftSpeed(20f, 2f), 1e-5f, "dx = +20");
            Assert.AreEqual(-0.2f, AlicornVariantRules.DescentDriftSpeed(-20f, 2f), 1e-5f, "dx = -20");
        }

        [Test]
        public void DescentDrift_DecaysAsTheTargetIsApproached()
        {
            // The oracle's `spd.x = celX - X` is recomputed every tick, so the term shrinks as the unit
            // closes in. A constant slide would overshoot and oscillate around `celX` instead.
            float far = System.Math.Abs(AlicornVariantRules.DescentDriftSpeed(100f, 2f));
            float near = System.Math.Abs(AlicornVariantRules.DescentDriftSpeed(50f, 2f));
            float almost = System.Math.Abs(AlicornVariantRules.DescentDriftSpeed(10f, 2f));

            Assert.Greater(far, near);
            Assert.Greater(near, almost);
        }

        [Test]
        public void DescentDrift_IsClampedToTheSinkRate()
        {
            // `norma` guarantees the pair shares one magnitude, so the horizontal component can never
            // outrun the vertical one. Without the clamp a large `dx` would send the unit sideways
            // faster than it falls.
            Assert.AreEqual(2f, AlicornVariantRules.DescentDriftSpeed(400f, 2f), 1e-5f, "dx = +400 clamps");
            Assert.AreEqual(-2f, AlicornVariantRules.DescentDriftSpeed(-400f, 2f), 1e-5f, "dx = -400 clamps");
        }

        [Test]
        public void DescentDrift_IsZeroAtTheTargetAndWhenThereIsNoSink()
        {
            Assert.AreEqual(0f, AlicornVariantRules.DescentDriftSpeed(0f, 2f), 1e-5f, "dx = 0");
            Assert.AreEqual(0f, AlicornVariantRules.DescentDriftSpeed(0.5f, 2f), 1e-5f, "below the deadzone");
            Assert.AreEqual(0f, AlicornVariantRules.DescentDriftSpeed(100f, 0f), 1e-5f, "no sink to match");
            Assert.AreEqual(0f, AlicornVariantRules.DescentDriftSpeed(-100f, 0f), 1e-5f, "no sink, negative dx");
        }

        [Test]
        public void DescentDrift_MirrorsItsSign()
        {
            // The Y-mirror trap this project keeps hitting: a descent that drifts AWAY from the target
            // looks exactly like one that drifts toward it until you watch which wall it lands on.
            foreach (float dx in new[] { 10f, 33f, 75f, 100f, 250f })
            {
                float positive = AlicornVariantRules.DescentDriftSpeed(dx, 2f);
                float negative = AlicornVariantRules.DescentDriftSpeed(-dx, 2f);
                Assert.Greater(positive, 0f, "dx = " + dx + " must drift right");
                Assert.AreEqual(-positive, negative, 1e-5f, "dx = -" + dx + " must drift left by the same amount");
            }
        }
    }
}

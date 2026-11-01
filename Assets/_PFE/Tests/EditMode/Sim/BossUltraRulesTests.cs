using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Offline fixtures for <see cref="BossUltraRules"/> — the Ultra Sentinel's fight ladder
    /// (<c>UnitBossUltra.as</c>).
    ///
    /// <para><b>Every expected value here was written from the AS3 expression, not read off the C#.</b>
    /// The oracle's own literals appear in each assertion's message so a failure says which line of
    /// <c>UnitBossUltra.as</c> disagrees, rather than merely which method does. That is the difference
    /// between a test and a snapshot: <c>Assert.AreEqual(2000f, ShieldMaxHp(0, 1f))</c> is only worth
    /// something because <c>shitMaxHp * 4</c> with <c>shitMaxHp = 500</c> was computed by hand first.</para>
    ///
    /// <para><b>What is <i>not</i> covered, and cannot be here.</b> The brain
    /// (<c>BossUltraBrain</c>) is a <c>MonoBehaviour</c>: <c>AddComponent</c> reaches a Unity
    /// <c>ECall</c>, so it cannot be constructed on this host. Everything it decides is a call into this
    /// class plus a read or a write on the unit, so what remains unasserted is the <i>wiring</i> — which
    /// timer is decremented when. That is covered by reading the two files side by side, and by the
    /// EditMode fixture <c>BossUltraRulesTests</c> in the real suite.</para>
    /// </summary>
    [TestFixture]
    public class BossUltraRulesTests
    {
        const float Eps = 1e-4f;

        /// <summary>
        /// The tolerance for <see cref="BossUltraRules.ShieldMaxHp"/>, and it is wider than
        /// <see cref="Eps"/> for a reason worth writing down.
        /// </summary>
        /// <remarks>
        /// <c>shitMaxHp *= 1 + level * 0.12</c> (<c>:127</c>) is <c>Number</c> arithmetic in AS3 —
        /// a <b>double</b> — and the port stores <c>0.12</c> as a <c>float</c>. At level 10 that
        /// difference is <c>1100</c> against <c>1099.99988</c>: 1.2e-4 on a shield pool that is only
        /// ever compared against zero and subtracted from, so it is not observable and is not worth
        /// restructuring the constant to avoid. The tolerance records the divergence rather than
        /// hiding it behind a blanket epsilon.
        /// </remarks>
        const float ShieldEps = 0.01f;

        // ── the constructor's constants (UnitBossUltra.as:12-102) ────────────────────────────────

        [Test]
        public void ConstructorConstants_MatchTheOracle()
        {
            Assert.AreEqual(80, BossUltraRules.InitialAiTCh, ":89 aiTCh = 80");
            Assert.AreEqual(3, BossUltraRules.InitialMovePointIndex, ":59 mp = 3");
            Assert.AreEqual(15, BossUltraRules.TurnDelayTicks, ":67 t_turn = 15");
            Assert.AreEqual(500f, BossUltraRules.ShieldMaxHpBase, Eps, ":34 shitMaxHp = 500");
            Assert.AreEqual(15f, BossUltraRules.ShieldArmor, Eps, ":90 shitArmor = 15");
            Assert.AreEqual(300, BossUltraRules.InitialShieldTimer, ":69 t_shit = 300");
            Assert.AreEqual(150, BossUltraRules.DeathTimerTicks, ":102 timerDie = 150");

            Assert.AreEqual(5, BossUltraRules.MovePointCount, ":42-57 movePoints.length");
        }

        [Test]
        public void LadderTimers_MatchTheOracle()
        {
            Assert.AreEqual(60, BossUltraRules.MoveStateTicks, ":318 aiTCh = 60");
            Assert.AreEqual(30, BossUltraRules.AimStateTicks, ":323 aiTCh = 30");
            Assert.AreEqual(5, BossUltraRules.AimStateTicksLowCeiling, ":336 aiTCh = 5");
            Assert.AreEqual(15, BossUltraRules.AimStateTicksGas, ":340 aiTCh = 15");

            Assert.AreEqual(80, BossUltraRules.AttackTicksRocket, ":351 attState 0");
            Assert.AreEqual(120, BossUltraRules.AttackTicksGrenade, ":355 attState 2");
            Assert.AreEqual(60, BossUltraRules.AttackTicksGas, ":359 attState 4");
            Assert.AreEqual(150, BossUltraRules.AttackTicksOtherBase, ":363 + 150");
            Assert.AreEqual(100, BossUltraRules.AttackTicksOtherSpread, ":363 random * 100");

            Assert.AreEqual(10, BossUltraRules.AimRefreshModulus, ":367 % 10 == 1");
            Assert.AreEqual(5, BossUltraRules.TelegraphModulus, ":405 % 5 == 1");
            Assert.AreEqual(0.33f, BossUltraRules.AttackStateRollChance, Eps, ":324/:332 isrnd(0.33)");
            Assert.AreEqual(0.1f, BossUltraRules.ContactAttackChance, Eps, ":493 isrnd(0.1)");
        }

        [Test]
        public void ShieldConstants_MatchTheOracle()
        {
            Assert.AreEqual(1000, BossUltraRules.ShieldRechargeTimer, ":453 t_shit = 1000");
            Assert.AreEqual(2000, BossUltraRules.PhaseShieldTimer, ":442 t_shit = 2000");
            Assert.AreEqual(4f, BossUltraRules.PhaseShieldMultiplier, Eps, ":441 shitMaxHp * 4");
            Assert.AreEqual(0.2f, BossUltraRules.ShieldedEmpVulnerability, Eps, ":284 shithp > 0");
            Assert.AreEqual(1f, BossUltraRules.UnshieldedEmpVulnerability, Eps, ":284 else");
            Assert.AreEqual(0.12f, BossUltraRules.LevelShieldScale, Eps, ":127 level * 0.12");
        }

        [Test]
        public void LowCeilingLines_MatchTheOracle()
        {
            // `Y < 17 * 40` and `celY > 16 * 40` (:332), with tileX/tileY = 40 (World.as:36-37).
            Assert.AreEqual(680f, BossUltraRules.LowCeilingBossY, Eps, ":332 17*40");
            Assert.AreEqual(640f, BossUltraRules.LowCeilingTargetY, Eps, ":332 16*40");
        }

        // ── the five arena points (:42-57, :316-317) ─────────────────────────────────────────────

        [TestCase(0, 420f, 320f, "10,7 -> 10*40+20, 7*40+40")]
        [TestCase(1, 1500f, 320f, "37,7 -> 37*40+20, 7*40+40")]
        [TestCase(2, 980f, 560f, "24,13 -> 24*40+20, 13*40+40")]
        [TestCase(3, 300f, 760f, "7,18 -> 7*40+20, 18*40+40")]
        [TestCase(4, 1620f, 760f, "40,18 -> 40*40+20, 18*40+40")]
        public void MovePointPixels_AreTheAuthoredTiles(int index, float x, float y, string source)
        {
            Vector2 point = BossUltraRules.MovePointPixels(index);
            Assert.AreEqual(x, point.x, Eps, source);
            Assert.AreEqual(y, point.y, Eps, source);
        }

        [Test]
        public void MovePointPixels_ClampsAnOutOfRangeIndex()
        {
            // AS3 would throw on the array read. A clamped answer is the port's choice, and the point of
            // this test is that it is a *clamp* rather than an exception or a zero.
            Assert.AreEqual(BossUltraRules.MovePointPixels(0).x, BossUltraRules.MovePointPixels(-1).x, Eps);
            Assert.AreEqual(BossUltraRules.MovePointPixels(4).x, BossUltraRules.MovePointPixels(9).x, Eps);
        }

        [TestCase(3, 0.0f, 0, ":306-315 r=0, r != mp(3) -> 0")]
        [TestCase(0, 0.0f, 1, ":310 r=0 == mp(0) -> r++ -> 1")]
        [TestCase(4, 0.99f, 0, ":310-313 r=4 == mp(4) -> 5 -> >=5 -> 0")]
        [TestCase(1, 0.5f, 2, ":306 r=floor(0.5*5)=2, != mp(1) -> 2")]
        [TestCase(2, 0.5f, 3, ":310 r=2 == mp(2) -> 3")]
        public void NextMovePointIndex_ReRollsAndOnlyNudgesOnARepeat(
            int previous, float roll, int expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.NextMovePointIndex(previous, roll), source);
        }

        [Test]
        public void NextMovePointIndex_IsNeverThePreviousPoint()
        {
            // The whole point of the `if(r == mp) r++` arm: across the entire roll space, the boss never
            // "repositions" to where it already is.
            for (int previous = 0; previous < BossUltraRules.MovePointCount; previous++)
            {
                for (int step = 0; step < 200; step++)
                {
                    float roll = step / 200f;
                    int next = BossUltraRules.NextMovePointIndex(previous, roll);
                    Assert.AreNotEqual(previous, next, $"roll {roll} from point {previous}");
                    Assert.GreaterOrEqual(next, 0);
                    Assert.Less(next, BossUltraRules.MovePointCount);
                }
            }
        }

        // ── the ladder (:292-303) ────────────────────────────────────────────────────────────────

        [TestCase(0, 0, 1, ":292 ++aiState")]
        [TestCase(1, 0, 2, ":292 ++aiState")]
        [TestCase(2, 0, 3, ":292 ++aiState")]
        [TestCase(3, 0, 1, ":297 aiState > 3 && attState != 4 -> 1")]
        [TestCase(3, 1, 1, ":297 same")]
        [TestCase(3, 2, 1, ":297 same")]
        [TestCase(3, 4, 2, ":295-299 attState == 4 -> 2")]
        public void AdvanceState_WrapsToTwoOnlyAfterAGasBeat(int aiState, int attackState, int expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.AdvanceState(aiState, attackState), source);
        }

        // ── the aim-state length (:323-340) ──────────────────────────────────────────────────────

        [TestCase(0, false, 30, ":323 aiTCh = 30")]
        [TestCase(1, false, 30, ":323 aiTCh = 30")]
        [TestCase(2, false, 30, ":323 aiTCh = 30")]
        [TestCase(2, true, 5, ":336 low ceiling arm -> aiTCh = 5")]
        [TestCase(4, false, 15, ":340 attState == 4 -> aiTCh = 15")]
        [TestCase(4, true, 15, ":336 then :340 - the 15 wins, but :336 cannot fire with attState 4")]
        public void AimTicksFor_AppliesTheThreeWritesInOrder(int attackState, bool lowCeilingArm,
            int expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.AimTicksFor(attackState, lowCeilingArm), source);
        }

        // ── the attack-state length (:346-365) ───────────────────────────────────────────────────

        [TestCase(0, 0.0f, 80, ":351 attState 0")]
        [TestCase(0, 0.99f, 80, ":351 attState 0 ignores the roll")]
        [TestCase(2, 0.5f, 120, ":355 attState 2")]
        [TestCase(4, 0.5f, 60, ":359 attState 4")]
        [TestCase(1, 0.0f, 150, ":363 floor(0*100) + 150")]
        [TestCase(1, 0.99f, 249, ":363 floor(0.99*100) + 150")]
        [TestCase(3, 0.5f, 200, ":363 floor(0.5*100) + 150")]
        public void AttackTicksFor_UsesTheRollOnlyOnTheCatchAllArm(
            int attackState, float roll, int expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.AttackTicksFor(attackState, roll), source);
        }

        // ── the low-ceiling test (:332) ──────────────────────────────────────────────────────────

        [TestCase(679f, 641f, 0.1f, true, "boss above 680 AND target below 640 AND roll")]
        [TestCase(680f, 641f, 0.1f, false, "Y < 680 is strict")]
        [TestCase(679f, 640f, 0.1f, false, "celY > 640 is strict")]
        [TestCase(679f, 641f, 0.4f, false, "isrnd(0.33) fails")]
        public void RollsLowCeiling_NeedsBothStrictComparisonsAndTheRoll(
            float bossY, float targetY, float roll, bool expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.RollsLowCeiling(bossY, targetY, roll), source);
        }

        // ── the attack roll (:324-336) ───────────────────────────────────────────────────────────

        [TestCase(0, 0.1f, 0.2f, 0.9f, false, 4, ":324 gas arm fires")]
        [TestCase(4, 0.1f, 0.2f, 0.9f, false, 0, ":324 attState == 4 refuses the gas arm; pair roll 0")]
        [TestCase(4, 0.1f, 0.8f, 0.9f, false, 1, ":324 same; pair roll 1")]
        [TestCase(0, 0.9f, 0.49f, 0.9f, false, 0, ":327 floor(random*2) = 0")]
        [TestCase(0, 0.9f, 0.5f, 0.9f, false, 1, ":327 floor(random*2) = 1")]
        [TestCase(0, 0.9f, 0.2f, 0.1f, true, 2, ":332 low ceiling arm overrides to 2")]
        [TestCase(0, 0.9f, 0.2f, 0.4f, true, 0, ":332 the low-ceiling roll failed, so the pair roll stands")]
        [TestCase(4, 0.9f, 0.2f, 0.1f, true, 2, ":332 low ceiling arm overrides even after the 4 refusal")]
        public void RollAttackState_ReachesTwoAndFourOnlyByTheirOwnRoutes(
            int previous, float gasRoll, float pairRoll, float lowCeilingRoll, bool lowCeiling,
            int expected, string source)
        {
            Assert.AreEqual(expected,
                BossUltraRules.RollAttackState(previous, gasRoll, pairRoll, lowCeilingRoll, lowCeiling),
                source);
        }

        [Test]
        public void RollAttackState_TheCoinFlipNeverProducesTwoOrFour()
        {
            // `Math.floor(random*2)` is 0 or 1 and nothing else, so with the gas arm refused and the
            // low-ceiling arm not firing, 2 and 4 are unreachable. That asymmetry is the fight's shape.
            for (int step = 0; step <= 100; step++)
            {
                float pairRoll = step / 100f;
                int state = BossUltraRules.RollAttackState(4, 0.99f, pairRoll, 0.99f, false);
                Assert.IsTrue(state == 0 || state == 1, $"pairRoll {pairRoll} produced {state}");
            }
        }

        // ── the aim refresh (:367) ──────────────────────────────────────────────────────────────

        [TestCase(1, 0, 11, true, "repositioning, aiTCh % 10 == 1")]
        [TestCase(1, 0, 10, false, "aiTCh % 10 != 1")]
        [TestCase(1, 0, 1, true, "1 % 10 == 1")]
        [TestCase(2, 1, 21, true, "aiming the gatling")]
        [TestCase(2, 0, 21, false, "aiming the rockets - never re-aims")]
        [TestCase(2, 4, 21, false, "aiming the gas - never re-aims")]
        [TestCase(2, 2, 21, false, "aiming the grenades - never re-aims")]
        [TestCase(3, 1, 11, true, "firing the gatling")]
        [TestCase(3, 2, 11, false, "firing grenades - never re-aims")]
        [TestCase(0, 1, 11, false, "state 0 never aims")]
        public void RefreshesAimThisTick_OnlyForTheTrackingWeapons(
            int aiState, int attackState, int aiTCh, bool expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.RefreshesAimThisTick(aiState, attackState, aiTCh), source);
        }

        // ── the facing delay (:420-436) ─────────────────────────────────────────────────────────

        [Test]
        public void TickFacing_ResetsTheCounterWhenAlreadyFacing()
        {
            BossUltraRules.TickFacing(1, 1, 15, false, out int facing, out int turnTimer);
            Assert.AreEqual(1, facing, ":432 storona == aiNapr");
            Assert.AreEqual(15, turnTimer, ":434 t_turn = 15");
        }

        [Test]
        public void TickFacing_CountsDownBeforeFlipping()
        {
            BossUltraRules.TickFacing(1, -1, 15, false, out int facing, out int turnTimer);
            Assert.AreEqual(1, facing, ":424 --t_turn; still counting");
            Assert.AreEqual(14, turnTimer, ":424 15 - 1");
        }

        [Test]
        public void TickFacing_FlippedOnTheTickTheCounterReachesZero()
        {
            BossUltraRules.TickFacing(1, -1, 1, false, out int facing, out int turnTimer);
            Assert.AreEqual(-1, facing, ":428 storona = aiNapr");
            Assert.AreEqual(15, turnTimer, ":429 t_turn = 15");
        }

        [Test]
        public void TickFacing_SkipsTheTurnDuringTheGrenadeBarrage()
        {
            // `!(aiState == 3 && attState == 2)` (:420) — the barrage does not turn.
            BossUltraRules.TickFacing(1, -1, 1, true, out int facing, out int turnTimer);
            Assert.AreEqual(1, facing, "the grenade beat must not turn");
            Assert.AreEqual(1, turnTimer, "and must not consume the counter either");
        }

        // ── flight (:390-403) ────────────────────────────────────────────────────────────────────

        [TestCase(0f, 2.5f, 0f, "d2 = 0 -> zero thrust")]
        [TestCase(100f, 2.5f, 0.025f, "inside 100 px: accel * d2 / 10000")]
        [TestCase(10000f, 2.5f, 2.5f, "at 100 px the curve meets accel")]
        [TestCase(20000f, 2.5f, 2.5f, "outside 100 px: capped at accel")]
        [TestCase(-5f, 2.5f, 0f, "a negative d2 is clamped, not propagated")]
        public void SeekLimit_IsTheBrakingCurve(float distanceSquared, float acceleration,
            float expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.SeekLimit(distanceSquared, acceleration), Eps, source);
        }

        [Test]
        public void ClampLength_ShortensOnlyWhenLonger()
        {
            Vector2 unchanged = BossUltraRules.ClampLength(new Vector2(3f, 4f), 10f);
            Assert.AreEqual(3f, unchanged.x, Eps, "5 < 10 - untouched");
            Assert.AreEqual(4f, unchanged.y, Eps, "5 < 10 - untouched");

            Vector2 shortened = BossUltraRules.ClampLength(new Vector2(3f, 4f), 2.5f);
            Assert.AreEqual(1.5f, shortened.x, Eps, "3 * 2.5 / 5");
            Assert.AreEqual(2f, shortened.y, Eps, "4 * 2.5 / 5");
            Assert.AreEqual(2.5f, shortened.magnitude, Eps, "the result has exactly the limit as its length");
        }

        [Test]
        public void ClampLength_LeavesAZeroVectorAlone()
        {
            Vector2 zero = BossUltraRules.ClampLength(new Vector2(0f, 0f), 5f);
            Assert.AreEqual(0f, zero.x, Eps);
            Assert.AreEqual(0f, zero.y, Eps);
        }

        [TestCase(999f, 1, 0.8f, ":396 d2 < 1000 -> * 0.8")]
        [TestCase(1000f, 1, 1f, ":396 the comparison is strict")]
        [TestCase(5000f, 1, 1f, ":396 outside the arrival radius")]
        [TestCase(0f, 2, 0.7f, ":401 aiState >= 2 -> * 0.7")]
        [TestCase(0f, 3, 0.7f, ":401 aiState >= 2 -> * 0.7")]
        [TestCase(0f, 0, 1f, ":376-388 state 0 damps not at all")]
        public void DampFactor_IsPerState(float distanceToPointSquared, int aiState, float expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.DampFactor(distanceToPointSquared, aiState), Eps, source);
        }

        // ── castShit (:448-455) ─────────────────────────────────────────────────────────────────

        [TestCase(0f, 0, 4, 0.9f, true, ":451 globalDif == 4")]
        [TestCase(0f, 0, 3, 0.49f, true, ":451 globalDif == 3 && hp < maxhp/2")]
        [TestCase(0f, 0, 3, 0.5f, false, ":451 the half-HP test is strict")]
        [TestCase(0f, 0, 2, 0.1f, false, ":451 difficulty 2 has no shield at all")]
        [TestCase(0f, 0, 0, 0.1f, false, ":451 difficulty 0")]
        [TestCase(1f, 0, 4, 0.9f, false, ":449 shithp > 0 refuses")]
        [TestCase(0f, 1, 4, 0.9f, false, ":449 t_shit > 0 refuses")]
        [TestCase(0f, -5, 4, 0.9f, true, ":449 a negative timer is ready")]
        public void ShouldCastShield_IsGatedOnDifficultyAndTheTimer(
            float shieldHp, int shieldTimer, int globalDifficulty, float hpFraction,
            bool expected, string source)
        {
            Assert.AreEqual(expected,
                BossUltraRules.ShouldCastShield(shieldHp, shieldTimer, globalDifficulty, hpFraction), source);
        }

        // ── the phase change (:438-445) ─────────────────────────────────────────────────────────

        [TestCase(false, 0.4999f, true, ":438 hp < maxhp / 2")]
        [TestCase(false, 0.5f, false, ":438 the comparison is strict")]
        [TestCase(true, 0.1f, false, ":438 !usil")]
        public void ShouldEnterPhaseTwo_FiresOnceAtHalfHp(bool alreadyEnhanced, float hpFraction, bool expected,
            string source)
        {
            Assert.AreEqual(expected, BossUltraRules.ShouldEnterPhaseTwo(alreadyEnhanced, hpFraction), source);
        }

        [TestCase(500f, 2000f, ":441 shitMaxHp * 4 with the base 500")]
        [TestCase(1100f, 4400f, ":441 a level-10 Very Hard pool")]
        public void PhaseTwoShieldHp_QuadruplesTheCapacity(float shieldMaxHp, float expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.PhaseTwoShieldHp(shieldMaxHp), Eps, source);
        }

        // ── the EMP row (:284) ──────────────────────────────────────────────────────────────────

        [TestCase(1f, 0.2f, "shield up -> a fifth of the damage")]
        [TestCase(0.001f, 0.2f, "any positive pool counts")]
        [TestCase(0f, 1f, "shield down -> the row's own emp='1'")]
        [TestCase(-1f, 1f, "a negative pool is not > 0")]
        public void EmpVulnerability_FollowsThePool(float shieldHp, float expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.EmpVulnerability(shieldHp), Eps, source);
        }

        // ── setLevel (:113-149) ─────────────────────────────────────────────────────────────────

        [TestCase(0, 1f, ":118 difficulty 0")]
        [TestCase(1, 1f, ":118 difficulty 1")]
        [TestCase(2, 1f, ":118 difficulty 2")]
        [TestCase(3, 1.2f, ":120 difficulty 3")]
        [TestCase(4, 1.5f, ":124 difficulty 4")]
        public void DifficultyHpMultiplier_IsOnePointTwoAndOnePointFive(int globalDifficulty, float expected,
            string source)
        {
            Assert.AreEqual(expected, BossUltraRules.DifficultyHpMultiplier(globalDifficulty), Eps, source);
        }

        [TestCase(0, 1f, 500f, ":127 500 * (1 + 0) * 1")]
        [TestCase(0, 1.5f, 750f, ":127 500 * 1 * 1.5")]
        [TestCase(10, 1f, 1100f, ":127 500 * (1 + 10*0.12) = 500 * 2.2")]
        [TestCase(10, 1.5f, 1650f, ":127 1100 * 1.5")]
        [TestCase(-5, 1f, 500f, "a negative level is clamped to 0")]
        [TestCase(0, 1.2f, 600f, ":127 500 * 1.2")]
        public void ShieldMaxHp_ScalesWithLevelAndDifficulty(int level, float difficultyMultiplier,
            float expected, string source)
        {
            Assert.AreEqual(expected, BossUltraRules.ShieldMaxHp(level, difficultyMultiplier), ShieldEps, source);
        }

        // ── the wiring the brain depends on ─────────────────────────────────────────────────────

        [Test]
        public void ARoundTripThroughTheLadderReturnsToTheMoveState()
        {
            // 1 (fly) -> 2 (aim) -> 3 (attack) -> 1. The single asymmetry is the gas beat, which returns
            // to 2 instead — so a gas volley is followed by another aim rather than a reposition.
            int state = 1;
            int attackState = 1;

            state = BossUltraRules.AdvanceState(state, attackState);
            Assert.AreEqual(2, state, "1 -> 2");
            state = BossUltraRules.AdvanceState(state, attackState);
            Assert.AreEqual(3, state, "2 -> 3");
            state = BossUltraRules.AdvanceState(state, attackState);
            Assert.AreEqual(1, state, "3 -> 1 for a gatling beat");

            state = 3;
            attackState = 4;
            state = BossUltraRules.AdvanceState(state, attackState);
            Assert.AreEqual(2, state, "3 -> 2 for a gas beat");
        }

        [Test]
        public void ThePhaseTwoPoolIsReachableFromTheSpawnedCapacity()
        {
            // The chain the controller actually runs: Initialize computes the capacity from the room's
            // level and the difficulty, then the brain quadruples it at half HP. A default-difficulty,
            // level-0 room is the shipped case and must land on the oracle's literal 2000.
            float capacity = BossUltraRules.ShieldMaxHp(0, BossUltraRules.DifficultyHpMultiplier(2));
            Assert.AreEqual(500f, capacity, Eps, "level 0 on Normal is the base 500");
            Assert.AreEqual(2000f, BossUltraRules.PhaseTwoShieldHp(capacity), Eps, "500 * 4");
        }
    }
}

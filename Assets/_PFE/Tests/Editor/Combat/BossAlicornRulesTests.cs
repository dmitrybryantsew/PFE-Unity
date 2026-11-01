using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the Manehattan alicorn boss's decision layer — <c>UnitBossAlicorn.as:72-93</c> (the seven
    /// move points), <c>:391-477</c> (the <c>aiTCh</c> rotation), <c>:478-488</c> (the aim cadence),
    /// <c>:568-589</c> (<c>attack()</c>), <c>:489-498</c> (the hover bob), <c>:703-738</c> (the shield
    /// break) and <c>:141-164</c> (the difficulty scaling).
    ///
    /// <para><b>Why a fixture and not the brain.</b> <c>BossAlicornBrain</c> is a
    /// <c>MonoBehaviour</c>; touching it needs <c>AddComponent</c>, which is a Unity <c>ECall</c> and
    /// throws where the native engine is absent — so a guard that drove the brain directly would not
    /// run in the offline harness, and a guard that cannot run is decoration. The arithmetic was
    /// extracted to <see cref="BossAlicornRules"/> for exactly this reason.</para>
    ///
    /// <para><b>Every expected value below is a literal read off <c>UnitBossAlicorn.as</c></b>, not a
    /// number copied back out of the implementation. The test names carry the line numbers.</para>
    /// </summary>
    [TestFixture]
    public class BossAlicornRulesTests
    {
        // ── the seven move points (:72-93, :415) ──────────────────────────────────────────────────

        /// <summary>
        /// <c>movePoints[i].x * 40 + 20</c> / <c>.y * 40 + 40</c> — the only positions the boss can
        /// occupy. The literals are the oracle's table, transcribed.
        /// </summary>
        [Test]
        public void MovePoints_AreTheSevenAuthoredPixels()
        {
            // x: 24, 8, 40, 7, 42, 11, 38  →  980, 340, 1620, 300, 1700, 460, 1540
            Assert.AreEqual(980f, BossAlicornRules.MovePointPixelX(0));
            Assert.AreEqual(340f, BossAlicornRules.MovePointPixelX(1));
            Assert.AreEqual(1620f, BossAlicornRules.MovePointPixelX(2));
            Assert.AreEqual(300f, BossAlicornRules.MovePointPixelX(3));
            Assert.AreEqual(1700f, BossAlicornRules.MovePointPixelX(4));
            Assert.AreEqual(460f, BossAlicornRules.MovePointPixelX(5));
            Assert.AreEqual(1540f, BossAlicornRules.MovePointPixelX(6));

            // y: 5, 7, 7, 11, 11, 15, 15  →  240, 320, 320, 480, 480, 640, 640
            Assert.AreEqual(240f, BossAlicornRules.MovePointPixelY(0));
            Assert.AreEqual(320f, BossAlicornRules.MovePointPixelY(1));
            Assert.AreEqual(320f, BossAlicornRules.MovePointPixelY(2));
            Assert.AreEqual(480f, BossAlicornRules.MovePointPixelY(3));
            Assert.AreEqual(480f, BossAlicornRules.MovePointPixelY(4));
            Assert.AreEqual(640f, BossAlicornRules.MovePointPixelY(5));
            Assert.AreEqual(640f, BossAlicornRules.MovePointPixelY(6));
        }

        /// <summary>
        /// <c>if(_loc4_ == mp) _loc4_++; if(_loc4_ &gt;= 7) _loc4_ = 0;</c> (<c>:406-413</c>) — the boss
        /// never teleports to the point it is already on.
        /// </summary>
        [Test]
        public void RollMovePoint_NeverReturnsTheCurrentPoint()
        {
            for (int current = 0; current < BossAlicornRules.MovePointCount; current++)
            {
                for (int step = 0; step < 100; step++)
                {
                    float roll = step / 100f;
                    int next = BossAlicornRules.RollMovePoint(current, roll);
                    Assert.AreNotEqual(current, next, $"roll {roll} on point {current} must move");
                    Assert.GreaterOrEqual(next, 0);
                    Assert.Less(next, BossAlicornRules.MovePointCount);
                }
            }
        }

        /// <summary>
        /// The skip is an increment, not a modulo — so landing on the last point while standing on it
        /// wraps to <b>0</b>, not 1.
        /// </summary>
        [Test]
        public void RollMovePoint_WrapsToZero_NotOne_WhenTheSkipFallsOffTheEnd()
        {
            // roll 0.99 → floor(6.93) = 6, which is the current point, so it increments to 7 → 0.
            Assert.AreEqual(0, BossAlicornRules.RollMovePoint(6, 0.99f));
        }

        // ── the rotation (:391-477) ───────────────────────────────────────────────────────────────

        /// <summary><c>++aiState; if(aiState &gt; 3) aiState = 1;</c> — it wraps to <b>1</b>, never 0.</summary>
        [Test]
        public void NextAiState_WrapsTo1_NotTo0()
        {
            Assert.AreEqual(1, BossAlicornRules.NextAiState(0));
            Assert.AreEqual(2, BossAlicornRules.NextAiState(1));
            Assert.AreEqual(3, BossAlicornRules.NextAiState(2));
            Assert.AreEqual(1, BossAlicornRules.NextAiState(3));
        }

        /// <summary>
        /// <c>aiTCh = 30</c> in the <c>aiState == 1</c> arm (<c>:416</c>), assigned <b>before</b> the
        /// beam upgrade at <c>:417-420</c> — which is why an upgraded beat keeps its 30 and still
        /// arrives with the teleport flash.
        /// </summary>
        /// <remarks>
        /// A constant-vs-literal pin, and that is deliberate: <c>80</c> is the constructor's
        /// <c>aiTCh = 80</c> (<c>:111</c>), and it is the value a reader most plausibly confuses with
        /// the per-step timer. This assertion is the thing that makes that confusion a failure.
        /// </remarks>
        [Test]
        public void AiTChAfterTeleport_IsThirty_NotTheConstructorsEighty()
        {
            Assert.AreEqual(30, BossAlicornRules.AiTChAfterTeleport);
            Assert.AreEqual(80, BossAlicornRules.InitialAiTCh);
            Assert.AreNotEqual(BossAlicornRules.InitialAiTCh, BossAlicornRules.AiTChAfterTeleport);
        }

        /// <summary>
        /// <c>attState = Math.floor(Math.random() * 5)</c> (<c>:404</c>) — <b>0..4</b>. The beam (5) is
        /// unreachable from the roll; it comes only from the difficulty upgrade below.
        /// </summary>
        [Test]
        public void RollAttackState_IsZeroToFour_AndNeverTheBeam()
        {
            Assert.AreEqual(0, BossAlicornRules.RollAttackState(0f));
            Assert.AreEqual(0, BossAlicornRules.RollAttackState(0.199f));
            Assert.AreEqual(1, BossAlicornRules.RollAttackState(0.2f));
            Assert.AreEqual(3, BossAlicornRules.RollAttackState(0.79f));
            Assert.AreEqual(4, BossAlicornRules.RollAttackState(0.999f));

            // The discriminating case: the top of the range must still be 4, not 5.
            Assert.AreNotEqual(BossAlicornRules.BeamAttackState, BossAlicornRules.RollAttackState(1f));
        }

        /// <summary>
        /// <c>if(globalDif == 4 &amp;&amp; isrnd(0.2) || globalDif == 3 &amp;&amp; isrnd(0.1)) attState = 5</c>
        /// (<c>:417-420</c>).
        /// </summary>
        [Test]
        public void UpgradesToBeam_IsDifficultyGated()
        {
            // Normal difficulty can never produce the beam, at any roll.
            Assert.IsFalse(BossAlicornRules.UpgradesToBeam(1, 0f));
            Assert.IsFalse(BossAlicornRules.UpgradesToBeam(2, 0.05f));

            // dif 3: 10 %.
            Assert.IsTrue(BossAlicornRules.UpgradesToBeam(3, 0.09f));
            Assert.IsFalse(BossAlicornRules.UpgradesToBeam(3, 0.11f));

            // dif 4: 20 %.
            Assert.IsTrue(BossAlicornRules.UpgradesToBeam(4, 0.19f));
            Assert.IsFalse(BossAlicornRules.UpgradesToBeam(4, 0.21f));
        }

        /// <summary>
        /// <c>aiState == 2</c> (<c>:441-456</c>): 30 normally, 45/15 for the grab, 20 for invisibility.
        /// </summary>
        [Test]
        public void AiTChForState2_MatchesTheThreeArms()
        {
            Assert.AreEqual(30, BossAlicornRules.AiTChForState2(0, false));
            Assert.AreEqual(30, BossAlicornRules.AiTChForState2(5, false));
            Assert.AreEqual(45, BossAlicornRules.AiTChForState2(BossAlicornRules.TelekinesisAttackState, true));
            Assert.AreEqual(15, BossAlicornRules.AiTChForState2(BossAlicornRules.TelekinesisAttackState, false));
            Assert.AreEqual(20, BossAlicornRules.AiTChForState2(BossAlicornRules.InvisibilityAttackState, false));
        }

        /// <summary>
        /// <c>aiState == 3</c> (<c>:457-476</c>): 30 / 120 / 75 / 50..149.
        /// </summary>
        [Test]
        public void AiTChForState3_MatchesTheFourArms()
        {
            Assert.AreEqual(30, BossAlicornRules.AiTChForState3(BossAlicornRules.TelekinesisAttackState, 0f));
            Assert.AreEqual(120, BossAlicornRules.AiTChForState3(BossAlicornRules.InvisibilityAttackState, 0f));
            Assert.AreEqual(75, BossAlicornRules.AiTChForState3(BossAlicornRules.BeamAttackState, 0f));

            // `Math.floor(Math.random() * 100) + 50` — 50 at roll 0, 149 at the top.
            Assert.AreEqual(50, BossAlicornRules.AiTChForState3(0, 0f));
            Assert.AreEqual(149, BossAlicornRules.AiTChForState3(0, 0.999f));
        }

        // ── the aim cadence (:478) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>((aiState == 1 || (aiState &gt; 1 &amp;&amp; attState &lt;= 2)) &amp;&amp; aiTCh % 10 == 1)
        /// || (attState == 5 &amp;&amp; aiState == 3)</c>.
        ///
        /// <para>The <c>% 10 == 1</c> term is the part that is easy to lose: the boss aims three times
        /// per 30-tick beat, not every tick.</para>
        /// </summary>
        [Test]
        public void ShouldAim_IsACadenceOnTheRangedSteps_PlusTheBeam()
        {
            // aiState 1, any attack state: only on the 1-mod-10 ticks.
            Assert.IsTrue(BossAlicornRules.ShouldAim(1, 0, 21));
            Assert.IsTrue(BossAlicornRules.ShouldAim(1, 4, 11));
            Assert.IsFalse(BossAlicornRules.ShouldAim(1, 0, 20));
            Assert.IsFalse(BossAlicornRules.ShouldAim(1, 0, 25));

            // aiState 2/3 with a ranged attack state: same cadence.
            Assert.IsTrue(BossAlicornRules.ShouldAim(2, 2, 21));
            Assert.IsTrue(BossAlicornRules.ShouldAim(3, 0, 1));

            // aiState 2/3 with the grab or the invisibility step: never, whatever the timer.
            Assert.IsFalse(BossAlicornRules.ShouldAim(2, BossAlicornRules.TelekinesisAttackState, 21));
            Assert.IsFalse(BossAlicornRules.ShouldAim(3, BossAlicornRules.InvisibilityAttackState, 1));

            // The beam aims on step 3 regardless of the cadence.
            Assert.IsTrue(BossAlicornRules.ShouldAim(3, BossAlicornRules.BeamAttackState, 74));
            Assert.IsFalse(BossAlicornRules.ShouldAim(2, BossAlicornRules.BeamAttackState, 74));
        }

        // ── attack() (:568-589) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(aiState == 3) { if(attState &lt;= 2 || attState == 5) currentWeapon.attack(); }</c> —
        /// the boss never fires on the grab or the invisibility beat.
        /// </summary>
        [Test]
        public void ShouldFire_IsStepThreeOnly_AndNeverOnGrabOrInvisibility()
        {
            Assert.IsTrue(BossAlicornRules.ShouldFire(3, 0));
            Assert.IsTrue(BossAlicornRules.ShouldFire(3, 2));
            Assert.IsTrue(BossAlicornRules.ShouldFire(3, BossAlicornRules.BeamAttackState));

            Assert.IsFalse(BossAlicornRules.ShouldFire(3, BossAlicornRules.TelekinesisAttackState));
            Assert.IsFalse(BossAlicornRules.ShouldFire(3, BossAlicornRules.InvisibilityAttackState));

            Assert.IsFalse(BossAlicornRules.ShouldFire(1, 0));
            Assert.IsFalse(BossAlicornRules.ShouldFire(2, 0));
        }

        /// <summary><c>if(attState == 4 &amp;&amp; aiTCh % 30 == 1) emit();</c> — four over the 120-tick beat.</summary>
        [Test]
        public void ShouldEmitScythe_IsEveryThirtiethTickOfTheInvisibilityStep()
        {
            Assert.IsTrue(BossAlicornRules.ShouldEmitScythe(3, BossAlicornRules.InvisibilityAttackState, 91));
            Assert.IsTrue(BossAlicornRules.ShouldEmitScythe(3, BossAlicornRules.InvisibilityAttackState, 61));
            Assert.IsFalse(BossAlicornRules.ShouldEmitScythe(3, BossAlicornRules.InvisibilityAttackState, 90));

            // Only on step 3, and only for attState 4.
            Assert.IsFalse(BossAlicornRules.ShouldEmitScythe(2, BossAlicornRules.InvisibilityAttackState, 91));
            Assert.IsFalse(BossAlicornRules.ShouldEmitScythe(3, 0, 91));
        }

        /// <summary>
        /// <c>if(attState != 4 &amp;&amp; dist2 &lt; 100*100 &amp;&amp; isrnd(0.1)) attKorp(celUnit, 0.5);</c>
        /// </summary>
        [Test]
        public void ShouldContactAttack_HasA100PixelReach_AndSkipsTheInvisibilityStep()
        {
            const float inside = 99f * 99f;
            const float outside = 101f * 101f;

            Assert.IsTrue(BossAlicornRules.ShouldContactAttack(3, 0, inside, 0.05f));
            Assert.IsFalse(BossAlicornRules.ShouldContactAttack(3, 0, outside, 0.05f));
            Assert.IsFalse(BossAlicornRules.ShouldContactAttack(3, 0, inside, 0.5f));

            // The one deliberate hole in the kit.
            Assert.IsFalse(BossAlicornRules.ShouldContactAttack(
                3, BossAlicornRules.InvisibilityAttackState, inside, 0f));

            Assert.IsFalse(BossAlicornRules.ShouldContactAttack(2, 0, inside, 0f));
        }

        // ── flight (:378-389, :323-331) ───────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(!stay &amp;&amp; !isFly) { --t_fly; if(&lt;=0) isFly = true; } else t_fly = 3;</c> — three
        /// <b>airborne</b> ticks, then hovering, with no impulse.
        ///
        /// <para><b><c>!stay</c> is the trap and this test exists for it.</b> <c>stay</c> is "standing on
        /// something", so the countdown runs while the boss is falling, not while it is grounded. The
        /// first version of this fixture passed <c>onGround: true</c> and expected a decrement — which
        /// is the reading that makes a grounded boss lift off, the opposite of the oracle's.</para>
        /// </summary>
        [Test]
        public void TickFlightTimer_LiftsOffAfterThreeAirborneTicks()
        {
            int tFly = BossAlicornRules.FlyTicks;
            bool flying;

            tFly = BossAlicornRules.TickFlightTimer(tFly, onGround: false, isFlying: false, out flying);
            Assert.IsFalse(flying, "one airborne tick is not enough");
            Assert.AreEqual(2, tFly);

            tFly = BossAlicornRules.TickFlightTimer(tFly, onGround: false, isFlying: false, out flying);
            Assert.IsFalse(flying);
            Assert.AreEqual(1, tFly);

            tFly = BossAlicornRules.TickFlightTimer(tFly, onGround: false, isFlying: false, out flying);
            Assert.IsTrue(flying, "the third airborne tick lifts the boss");
            Assert.AreEqual(0, tFly);
        }

        /// <summary>
        /// A <b>grounded</b> tick never decrements — it tops the timer back up. This is the
        /// discriminating case against the misreading above: a grounded boss must not lift off.
        /// </summary>
        [Test]
        public void TickFlightTimer_KeepsAGroundedBossGrounded()
        {
            for (int i = 0; i < 20; i++)
            {
                int tFly = BossAlicornRules.TickFlightTimer(1, onGround: true, isFlying: false, out bool flying);
                Assert.IsFalse(flying, "a grounded boss must never lift off");
                Assert.AreEqual(BossAlicornRules.FlyTicks, tFly);
            }
        }

        /// <summary>A grounded tick while already flying keeps the timer topped up, and stays flying.</summary>
        [Test]
        public void TickFlightTimer_DoesNotLandAnAirborneBoss()
        {
            int tFly = BossAlicornRules.TickFlightTimer(1, onGround: true, isFlying: true, out bool flying);
            Assert.IsTrue(flying);
            Assert.AreEqual(BossAlicornRules.FlyTicks, tFly);
        }

        /// <summary><c>if(tile.phis || tile.shelf) isFly = false; else isFly = true;</c></summary>
        [Test]
        public void IsFlyingAfterTeleport_LandsOnSolidOrShelf_AndHoversOtherwise()
        {
            Assert.IsFalse(BossAlicornRules.IsFlyingAfterTeleport(solidOrShelfBelow: true));
            Assert.IsTrue(BossAlicornRules.IsFlyingAfterTeleport(solidOrShelfBelow: false));
        }

        // ── the hover bob (:489-498) ──────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>t_float += 0.0726; floatX = sin*2; floatY = cos*2; dx *= 0.9; dy *= 0.9; dx += floatX;
        /// dy += floatY;</c>
        ///
        /// <para><b>The Y assertion is the one that matters.</b> AS3's Y grows down, so at
        /// <c>t = 0</c> the <c>cos</c> term pushes the body <i>down</i>. The port's velocity is y-up, so
        /// the same step must make <c>vy</c> <i>more negative</i>. A port that copied the <c>+</c> puts
        /// the hover half a period out of phase — invisible in a screenshot, and exactly what a fixture
        /// is for.</para>
        /// </summary>
        [Test]
        public void FloatBob_AdvancesThePhase_DampsTheVelocity_AndMirrorsY()
        {
            float tFloat = 0f;
            float vx = 0f;
            float vy = 0f;

            BossAlicornRules.FloatBob(ref tFloat, ref vx, ref vy);

            Assert.AreEqual(BossAlicornRules.FloatPhaseStep, tFloat, 1e-5f);

            // sin(0.0726) * 2 ≈ +0.1451 — to the right, unmirrored.
            Assert.AreEqual(0.1451f, vx, 1e-3f);
            Assert.Greater(vx, 0f, "the horizontal bob is not mirrored");

            // cos(0.0726) * 2 ≈ +1.9947 in AS3 (downward) → -1.9947 in the port's y-up velocity.
            Assert.AreEqual(-1.9947f, vy, 1e-3f);
            Assert.Less(vy, 0f, "AS3's +cos is a DOWNWARD push; the port must subtract it");
        }

        /// <summary>
        /// The damping is applied to the velocity the caller passes in — <c>dx *= 0.9</c> — before the
        /// bob is added. A caller that added the bob without damping would accelerate the hover.
        /// </summary>
        [Test]
        public void FloatBob_DampsTheIncomingVelocity()
        {
            float tFloat = 0f;
            float vx = 10f;
            float vy = 10f;

            BossAlicornRules.FloatBob(ref tFloat, ref vx, ref vy);

            Assert.AreEqual(10f * BossAlicornRules.FloatDamping + 0.1451f, vx, 1e-3f);
            Assert.AreEqual(10f * BossAlicornRules.FloatDamping - 1.9947f, vy, 1e-3f);
        }

        // ── the shield break (:703-738) ───────────────────────────────────────────────────────────

        /// <summary><c>hp = maxhp - 0.1</c> — the second stage starts one tenth of a point below full.</summary>
        [Test]
        public void HpAfterShieldBreak_IsMaxHpMinusATenth()
        {
            Assert.AreEqual(4499.9f, BossAlicornRules.HpAfterShieldBreak(4500f), 1e-3f);
            Assert.AreEqual(6, BossAlicornRules.ScythesOnShieldBreak);
        }

        // ── difficulty (:141-164) ─────────────────────────────────────────────────────────────────

        /// <summary><c>1 / 1.2 / 1.5</c> for difficulties below 3, 3 and 4.</summary>
        [Test]
        public void DifficultyScale_IsTheOracleLadder()
        {
            Assert.AreEqual(1f, BossAlicornRules.DifficultyScale(1));
            Assert.AreEqual(1f, BossAlicornRules.DifficultyScale(2));
            Assert.AreEqual(1.2f, BossAlicornRules.DifficultyScale(3));
            Assert.AreEqual(1.5f, BossAlicornRules.DifficultyScale(4));
        }

        /// <summary><c>1 + level * 0.07</c> — and only the melee arm reads it.</summary>
        [Test]
        public void BladeLevelScale_IsOnePlusSevenPercentPerLevel()
        {
            Assert.AreEqual(1f, BossAlicornRules.BladeLevelScale(0f), 1e-5f);
            Assert.AreEqual(1.7f, BossAlicornRules.BladeLevelScale(10f), 1e-5f);
        }

        // ── telekinesis (:591-701) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(celUnit &amp;&amp; isrnd(0.5))</c> — the boss goes for the player on a coin flip, twice the
        /// family alicorn's 25 %.
        /// </summary>
        [Test]
        public void PrefersUnitTarget_IsACoinFlip_NotTheFamilysQuarter()
        {
            Assert.IsTrue(BossAlicornRules.PrefersUnitTarget(0.49f));
            Assert.IsFalse(BossAlicornRules.PrefersUnitTarget(0.51f));

            // The discriminating case against the family constant: 0.3 grabs for the boss, not the
            // alicorn.
            Assert.IsTrue(BossAlicornRules.PrefersUnitTarget(0.3f));
            Assert.IsFalse(AlicornVariantRules.UnitGrabChance > 0.3f);
        }

        /// <summary><c>getRasst2 &lt;= optDistTele * optDistTele</c> with <c>optDistTele = 1000</c>.</summary>
        [Test]
        public void InTeleRange_Is1000Pixels()
        {
            Assert.IsTrue(BossAlicornRules.InTeleRange(999f * 999f));
            Assert.IsFalse(BossAlicornRules.InTeleRange(1001f * 1001f));
        }

        /// <summary>
        /// <c>throwForce = 60</c>, <c>/ sqrt(massa)</c> for a heavy prop, and <c>* 0.6</c> within 200 px
        /// of either wall.
        /// </summary>
        [Test]
        public void ThrowSpeed_Is60_HalvedByMassaFour_AndDampedNearAWall()
        {
            Assert.AreEqual(60f, BossAlicornRules.ThrowSpeed(60f, 0f, nearWall: false), 1e-3f);
            Assert.AreEqual(60f, BossAlicornRules.ThrowSpeed(60f, 1f, nearWall: false), 1e-3f);

            // massa 4 → 60 / 2 = 30.
            Assert.AreEqual(30f, BossAlicornRules.ThrowSpeed(60f, 4f, nearWall: false), 1e-3f);

            // Near a wall: 60 * 0.6 = 36.
            Assert.AreEqual(36f, BossAlicornRules.ThrowSpeed(60f, 0f, nearWall: true), 1e-3f);
        }

        /// <summary><c>X &lt; 200 || X &gt; limX - 200</c>.</summary>
        [Test]
        public void IsNearWall_IsA200PixelMargin()
        {
            const float roomWidth = 1920f;

            Assert.IsTrue(BossAlicornRules.IsNearWall(199f, roomWidth));
            Assert.IsFalse(BossAlicornRules.IsNearWall(201f, roomWidth));

            Assert.IsTrue(BossAlicornRules.IsNearWall(1721f, roomWidth));
            Assert.IsFalse(BossAlicornRules.IsNearWall(1719f, roomWidth));
        }

        /// <summary>
        /// The boss's throw force is <b>60</b> — the hardest in the game, and half again the family
        /// <c>tr2</c>'s 45. Pinned so a "tidy-up" that reused the family table is caught.
        /// </summary>
        [Test]
        public void ThrowForce_IsNotTheFamilyValue()
        {
            Assert.AreEqual(60f, BossAlicornRules.ThrowForce);
            Assert.AreNotEqual(AlicornVariantRules.For(2).ThrowForce, BossAlicornRules.ThrowForce);
        }
    }
}

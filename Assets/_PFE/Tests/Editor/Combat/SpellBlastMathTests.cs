using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <c>sp_blast</c> — AS3 <c>Spell.cast_blast</c> (<c>fe/unit/Spell.as:371-418</c>).
    ///
    /// <para><b>The single most important assertion here is that nothing takes damage.</b> The row is
    /// <c>&lt;item id='sp_blast' dam='20' rad='500' …&gt;</c> and <c>dam</c> is a <i>velocity ceiling</i>,
    /// not HP. The alicorn's <c>tr1</c> reflex is a shove, and a future reader "fixing" the missing damage
    /// would be adding a burst the oracle does not have. There is no damage function on
    /// <see cref="SpellBlastMath"/> to call, and that absence is the design.</para>
    /// </summary>
    [TestFixture]
    public class SpellBlastMathTests
    {
        // ── who the pulse touches (:386) ─────────────────────────────────────────────────────────

        [Test]
        public void Pulse_ReachesEnemiesOnly_AndA_Corpse_IsLegal()
        {
            // `if(!(_loc1_.fixed || _loc1_.fraction == owner.fraction || !owner.isMeet(_loc1_)))`
            Assert.IsTrue(SpellBlastMath.IsEligibleTarget(
                isFixed: false, sameFaction: false, sameRoom: true, isUnraveled: false, isSelf: false));

            Assert.IsFalse(SpellBlastMath.IsEligibleTarget(true, false, true, false, false), "fixed");
            Assert.IsFalse(SpellBlastMath.IsEligibleTarget(false, true, true, false, false), "own side");
            Assert.IsFalse(SpellBlastMath.IsEligibleTarget(false, false, false, false, false), "other room");
            Assert.IsFalse(SpellBlastMath.IsEligibleTarget(false, false, true, true, false), "sost == 4");
            Assert.IsFalse(SpellBlastMath.IsEligibleTarget(false, false, true, false, true), "self");
        }

        [Test]
        public void Pulse_ThrowsACorpse_BecauseIsMeetDoesNotRequireSost1()
        {
            // `isMeet` excludes `sost == 4` (an unravel) and nothing else — a body is `sost == 3` and is
            // therefore a legal target. This is the assertion that stops someone "fixing" the predicate
            // to take `isAlive`, which would make every corpse immune to the blast.
            Assert.IsTrue(SpellBlastMath.IsEligibleTarget(false, false, true, isUnraveled: false, isSelf: false),
                "a dead body has isUnraveled == false, so it is eligible");
        }

        // ── the radius (:391) ────────────────────────────────────────────────────────────────────

        [Test]
        public void Radius_IsInclusiveAtTheRim()
        {
            // `if(_loc4_ <= this.rad * this.rad)` on squared distance.
            Assert.IsTrue(SpellBlastMath.IsInRadius(500f * 500f, 500f));
            Assert.IsTrue(SpellBlastMath.IsInRadius(0f, 500f));
            Assert.IsFalse(SpellBlastMath.IsInRadius(500f * 500f + 1f, 500f));
            Assert.IsFalse(SpellBlastMath.IsInRadius(1f, 0f), "a zero radius reaches nothing");
        }

        // ── the impulse (:394-398) ───────────────────────────────────────────────────────────────

        [Test]
        public void Impulse_IsStrongestAtTheCentre_AndZeroAtTheRim()
        {
            // dam 20, power 2 (the alicorn's spellPower) → a ceiling of 40 px/frame.
            float centre = SpellBlastMath.Impulse(20f, 2f, distance: 0f, radius: 500f, roll: 0.5f,
                                                  knocked: 1f, massa: 1f);
            float middle = SpellBlastMath.Impulse(20f, 2f, 250f, 500f, 0.5f, 1f, 1f);
            float rim = SpellBlastMath.Impulse(20f, 2f, 500f, 500f, 0.5f, 1f, 1f);

            // roll 0.5 → spread 1.0, so the centre is the ceiling exactly.
            Assert.AreEqual(40f, centre, 1e-4f);
            Assert.AreEqual(20f, middle, 1e-4f, "half the falloff, half the impulse");
            Assert.AreEqual(0f, rim, 1e-4f);
        }

        [Test]
        public void Impulse_IsClampedAtDamageTimesPower()
        {
            // `if(_loc5_ > dam*power) _loc5_ = dam*power;` — reachable, because knocked/massa can exceed
            // 1 for a light, easily-knocked unit.
            float clamped = SpellBlastMath.Impulse(20f, 2f, 0f, 500f, 0.999f, knocked: 3f, massa: 1f);
            Assert.AreEqual(40f, clamped, 1e-4f, "the ceiling wins over a knocked/massa of 3");
        }

        [Test]
        public void Impulse_ScalesWithKnockedOverMassa_AndTreatsZeroMassAsOne()
        {
            float heavy = SpellBlastMath.Impulse(20f, 2f, 0f, 500f, 0.5f, knocked: 1f, massa: 2f);
            float light = SpellBlastMath.Impulse(20f, 2f, 0f, 500f, 0.5f, knocked: 1f, massa: 0.5f);
            float massless = SpellBlastMath.Impulse(20f, 2f, 0f, 500f, 0.5f, knocked: 1f, massa: 0f);

            Assert.AreEqual(20f, heavy, 1e-4f);
            Assert.AreEqual(40f, light, 1e-4f, "0.5 mass doubles it — and the clamp stops it there");
            Assert.AreEqual(40f, massless, 1e-4f,
                "AS3 divides by zero and Infinity is caught by the clamp; the port substitutes 1, so the " +
                "result is the ceiling too — same number, and no NaN on the way to it");
        }

        [Test]
        public void Impulse_SpreadStaysInsideTheOraclesEightToTwelvePercentBand()
        {
            // `(Math.random() * 0.4 + 0.8)`.
            float low = SpellBlastMath.Impulse(100f, 1f, 0f, 500f, roll: 0f, knocked: 1f, massa: 1f);
            float high = SpellBlastMath.Impulse(100f, 1f, 0f, 500f, roll: 0.9999f, knocked: 1f, massa: 1f);

            Assert.AreEqual(80f, low, 0.05f);
            Assert.AreEqual(100f, high, 0.05f);
        }

        [Test]
        public void Impulse_IsZeroForADegenerateRow()
        {
            Assert.AreEqual(0f, SpellBlastMath.Impulse(0f, 2f, 0f, 500f, 0.5f, 1f, 1f), "no dam");
            Assert.AreEqual(0f, SpellBlastMath.Impulse(20f, 0f, 0f, 500f, 0.5f, 1f, 1f), "no power");
            Assert.AreEqual(0f, SpellBlastMath.Impulse(20f, 2f, 0f, 0f, 0.5f, 1f, 1f), "no radius");
        }

        // ── the split (:399-400) ─────────────────────────────────────────────────────────────────

        [Test]
        public void Velocity_IsTheImpulseAlongTheUnitVector()
        {
            SpellBlastMath.Velocity(deltaX: 300f, deltaY: 400f, distance: 500f, impulse: 10f,
                                    out float vx, out float vy);

            // 300/500*10 = 6, 400/500*10 = 8 — a 3-4-5 triangle, so the magnitude is exactly the impulse.
            Assert.AreEqual(6f, vx, 1e-4f);
            Assert.AreEqual(8f, vy, 1e-4f);
            Assert.AreEqual(10f, (float)System.Math.Sqrt(vx * vx + vy * vy), 1e-4f);
        }

        [Test]
        public void Velocity_IsZeroAtZeroDistance_WhereAS3WouldProduceNaN()
        {
            SpellBlastMath.Velocity(0f, 0f, 0f, 10f, out float vx, out float vy);
            Assert.AreEqual(0f, vx);
            Assert.AreEqual(0f, vy);
        }

        // ── the side effects (:401-402) ──────────────────────────────────────────────────────────

        [Test]
        public void Stun_IsAFloorOfRandomTimesPowerTimesDamage()
        {
            // `u.stun += Math.floor(Math.random() * this.power * this.dam);`
            Assert.AreEqual(0, SpellBlastMath.StunAdd(2f, 20f, 0f));
            Assert.AreEqual(39, SpellBlastMath.StunAdd(2f, 20f, 0.9999f));
            Assert.AreEqual(20, SpellBlastMath.StunAdd(2f, 20f, 0.5f));
            Assert.AreEqual(0, SpellBlastMath.StunAdd(0f, 20f, 0.9f), "no power, no stagger");
        }

        [Test]
        public void ThrowWindow_IsThirtyTicks()
        {
            Assert.AreEqual(30, SpellBlastMath.ThrowTicks);
        }
    }
}

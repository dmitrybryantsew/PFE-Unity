using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="CorpseDamageMath"/> — the port's stand-in for AS3's unclamped <c>Unit.hp</c>
    /// below zero, which is what destroys a body.
    ///
    /// <para><b>Why this needs a guard at all.</b> The behaviour it encodes was absent, not wrong: a
    /// corpse in the port had no HP to lose, so the oracle's threshold
    /// <c>hp &lt;= -maxhp * 2</c> (<c>Unit.as:4333</c>) could not be reached by any amount of damage.
    /// A fixture that only checked "enough damage destroys it" would pass just as happily against an
    /// implementation that destroys on the first hit, so the cases below pin the <b>boundary</b> and
    /// the <b>part of a hit that counts</b> separately.</para>
    ///
    /// <para>The accumulator is equivalent to the oracle's negative HP only because it is fed the
    /// pre-hit HP every time; that is the property <see cref="OverkillAdded_CountsOnlyThePartBelowZero"/>
    /// pins, and it is the one a "just add the whole hit" implementation would get wrong while still
    /// looking plausible in game — a body would die in half the shots it should.</para>
    /// </summary>
    [TestFixture]
    public class CorpseDamageMathTests
    {
        private const float Tolerance = 1e-4f;

        // ── What counts as overkill (Unit.as:3699, `hp -= param1`) ─────────────

        [Test]
        public void OverkillAdded_CountsOnlyThePartBelowZero()
        {
            // hp 10, hit 50 → AS3 leaves hp at -40, so 40 of the hit is body damage.
            Assert.AreEqual(40f, CorpseDamageMath.OverkillAdded(hpBefore: 10f, damage: 50f), Tolerance);

            // hp 10, hit 10 → hp lands exactly on 0. Nothing is below the line yet.
            Assert.AreEqual(0f, CorpseDamageMath.OverkillAdded(hpBefore: 10f, damage: 10f), Tolerance);

            // hp 10, hit 4 → still alive; a live unit must never accumulate body damage.
            Assert.AreEqual(0f, CorpseDamageMath.OverkillAdded(hpBefore: 10f, damage: 4f), Tolerance);

            // Already a body: every point of the next hit is overkill.
            Assert.AreEqual(25f, CorpseDamageMath.OverkillAdded(hpBefore: 0f, damage: 25f), Tolerance);
        }

        [Test]
        public void OverkillAdded_IgnoresHealingAndZeroHits()
        {
            // AS3 `Unit.damage()` routes a negative `param1` to `heal()` and returns 0 (`:3606-3610`),
            // so it never reaches the subtraction. A heal must not be banked as body damage.
            Assert.AreEqual(0f, CorpseDamageMath.OverkillAdded(hpBefore: 0f, damage: -30f), Tolerance);
            Assert.AreEqual(0f, CorpseDamageMath.OverkillAdded(hpBefore: 5f, damage: 0f), Tolerance);
        }

        [Test]
        public void OverkillAdded_IgnoresAStaleNegativeHp()
        {
            // Not reachable through UnitStats (it clamps), but the accumulator is also read for a
            // target whose stored HP is 0 — and a negative input must not silently discount the hit.
            Assert.AreEqual(20f, CorpseDamageMath.OverkillAdded(hpBefore: -100f, damage: 20f), Tolerance);
        }

        // ── The destroy threshold (Unit.as:4333, `hp > -maxhp * 2` survives) ───

        [Test]
        public void IsDestroyed_ThresholdIsExactlyTwiceMaxHp()
        {
            // AS3 keeps the body while `hp > -maxhp*2` STRICTLY, so exactly 2*maxhp of overkill is
            // already past the line. zombie0 is `hp='50'`, which makes this 100.
            Assert.IsFalse(CorpseDamageMath.IsDestroyed(overkill: 99.9f, maxHp: 50f),
                "a body one tenth short of the threshold is still a body");
            Assert.IsTrue(CorpseDamageMath.IsDestroyed(overkill: 100f, maxHp: 50f),
                "the oracle's comparison is `>` on the negative side, so the boundary destroys");
            Assert.IsTrue(CorpseDamageMath.IsDestroyed(overkill: 250f, maxHp: 50f));
        }

        [Test]
        public void IsDestroyed_ScalesWithMaxHpNotWithTheHit()
        {
            // The same overkill destroys a small body and not a large one — the threshold is a
            // property of the target, which is the whole reason `die()` reads `this.maxhp`.
            Assert.IsTrue(CorpseDamageMath.IsDestroyed(overkill: 100f, maxHp: 50f));
            Assert.IsFalse(CorpseDamageMath.IsDestroyed(overkill: 100f, maxHp: 500f));
        }

        [Test]
        public void IsDestroyed_ZeroMaxHpDestroysImmediately()
        {
            // Not a special case: `hp > -0 * 2` is false for any hp at or below zero, so a unit with
            // no maximum health has no body to keep. Written down so the branch is a decision.
            Assert.IsTrue(CorpseDamageMath.IsDestroyed(overkill: 0f, maxHp: 0f));
        }

        // ── Healing a body (Unit.as:4032-4046, `hp += param1`) ────────────────

        [Test]
        public void OverkillAfterHeal_PaysTheGraveBeforeTheBar()
        {
            // AS3's hp is one number: a heal from -30 with +20 lands at -10 and puts nothing in the
            // bar. That is "all of it went into the overkill, none into HP".
            float left = CorpseDamageMath.OverkillAfterHeal(overkill: 30f, healAmount: 20f, out float toHp);
            Assert.AreEqual(10f, left, Tolerance);
            Assert.AreEqual(0f, toHp, Tolerance);

            // ...and a heal larger than the debt pays it off and puts the rest into HP.
            left = CorpseDamageMath.OverkillAfterHeal(overkill: 30f, healAmount: 50f, out toHp);
            Assert.AreEqual(0f, left, Tolerance);
            Assert.AreEqual(20f, toHp, Tolerance);
        }

        [Test]
        public void OverkillAfterHeal_PassesEverythingThroughWhenThereIsNoDebt()
        {
            float left = CorpseDamageMath.OverkillAfterHeal(overkill: 0f, healAmount: 25f, out float toHp);
            Assert.AreEqual(0f, left, Tolerance);
            Assert.AreEqual(25f, toHp, Tolerance);

            // A heal exactly equal to the debt clears it and adds nothing.
            left = CorpseDamageMath.OverkillAfterHeal(overkill: 30f, healAmount: 30f, out toHp);
            Assert.AreEqual(0f, left, Tolerance);
            Assert.AreEqual(0f, toHp, Tolerance);
        }

        [Test]
        public void OverkillAfterHeal_NonPositiveHealChangesNothing()
        {
            float left = CorpseDamageMath.OverkillAfterHeal(overkill: 30f, healAmount: -5f, out float toHp);
            Assert.AreEqual(30f, left, Tolerance);
            Assert.AreEqual(0f, toHp, Tolerance);
        }

        // ── The dissolve length (serv/Desintegr.as:38-39) ──────────────────────

        [Test]
        public void DissolveFrames_IsTheOraclesTwoPhases()
        {
            // burnTime1 (10) + burnTime2 (30) — the unit is removed when `burn.vse` goes true, which
            // is `burnN >= 40` (`Desintegr.as:137-140`, `Unit.as:1839-1843`).
            Assert.AreEqual(40, CorpseDamageMath.DissolveFrames);
        }
    }
}

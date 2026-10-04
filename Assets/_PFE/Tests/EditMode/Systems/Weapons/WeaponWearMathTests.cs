using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponWearMath"/> — AS3's <c>Weapon.breaking</c> and the two damage multipliers
    /// built from it (<c>Weapon.as:1356-1362,1629</c>; <c>WClub.as:581-588,649</c>).
    ///
    /// <para><b>Why this fixture exists.</b> The only consumers are weapon controllers built from a
    /// <c>WeaponDefinition</c> <c>ScriptableObject</c>, so a fixture that drives one cannot run
    /// outside the editor — the controller tests are all in the offline <c>ECall</c> class. Extracting
    /// the formula to a pure static is what lets these run in the offline wall. The split is the point,
    /// not a side effect (same rationale as <see cref="WeaponSpreadMathTests"/>).</para>
    ///
    /// <para><b>Expectations are expressions, not decimal literals.</b> A hand-computed literal for
    /// <c>(100-49)/100*2-1</c> is a second, silent copy of the formula, and a wrong literal makes the
    /// test agree with a wrong implementation.</para>
    /// </summary>
    [TestFixture]
    public class WeaponWearMathTests
    {
        private const float Tolerance = 1e-6f;

        // ── Breaking: the knee at half durability ─────────────────────────────

        [Test]
        public void Breaking_AtOrAboveHalfDurability_IsZero()
        {
            // AS3's `else { breaking = 0 }`. The whole point of the branch: a weapon that is not yet
            // worn past halfway must deal EXACTLY the data damage, not a fraction above it.
            Assert.AreEqual(0f, WeaponWearMath.Breaking(100, 100), Tolerance, "a new weapon is unworn.");
            Assert.AreEqual(0f, WeaponWearMath.Breaking(100, 75), Tolerance);
            Assert.AreEqual(0f, WeaponWearMath.Breaking(100, 50), Tolerance,
                "the knee itself is the else branch: `hp < maxhp/2`, so hp == maxhp/2 is still 0.");
        }

        [Test]
        public void Breaking_JustBelowHalfDurability_IsTheOraclesFraction()
        {
            // (maxhp - hp)/maxhp * 2 - 1 at hp = 49, maxhp = 100.
            float expected = (100f - 49f) / 100f * 2f - 1f;   // 0.02
            Assert.AreEqual(expected, WeaponWearMath.Breaking(100, 49), Tolerance);
        }

        [Test]
        public void Breaking_AtZeroDurability_IsOne()
        {
            Assert.AreEqual(1f, WeaponWearMath.Breaking(100, 0), Tolerance,
                "worn to nothing, breaking saturates at 1 (WClub.as:583 / Weapon.as:1358).");
        }

        [Test]
        public void Breaking_IsMonotonicAsDurabilityFalls()
        {
            // Not a literal check — a property. The penalty must never reward more wear, which is
            // what an inverted or signed formula would do.
            float prev = WeaponWearMath.Breaking(100, 50);
            for (int hp = 49; hp >= 0; hp--)
            {
                float cur = WeaponWearMath.Breaking(100, hp);
                Assert.GreaterOrEqual(cur, prev, $"breaking fell going from {hp + 1} to {hp}.");
                prev = cur;
            }
        }

        [Test]
        public void Breaking_UsesIntegerHalf_AsTheOracleDoes()
        {
            // `maxhp / 2` is integer division in AS3 (maxhp:int). For maxhp = 101 the knee is at
            // hp < 50, so hp == 50 is still unworn. A float division would move the knee to 50.5 and
            // give 50 a tiny non-zero penalty.
            Assert.AreEqual(0f, WeaponWearMath.Breaking(101, 50), Tolerance);
            Assert.Greater(WeaponWearMath.Breaking(101, 49), 0f);
        }

        [Test]
        public void Breaking_WithNoDurabilityPool_IsZero()
        {
            // maxHp <= 0 has no pool to wear out. Also the guard that keeps the division safe.
            Assert.AreEqual(0f, WeaponWearMath.Breaking(0, 0), Tolerance);
            Assert.AreEqual(0f, WeaponWearMath.Breaking(0, 50), Tolerance);
        }

        // ── The two damage shapes ─────────────────────────────────────────────

        [Test]
        public void BaseDamageMultiplier_IsOneUnworn_AndPointSevenAtFullWear()
        {
            // Weapon.resultDamage (Weapon.as:1629): (1 - breaking*0.3).
            Assert.AreEqual(1f, WeaponWearMath.BaseDamageMultiplier(0f), Tolerance,
                "no wear is no penalty — the multiplier is exactly 1, not merely close.");
            Assert.AreEqual(1f - 0.3f, WeaponWearMath.BaseDamageMultiplier(1f), Tolerance);
            Assert.AreEqual(1f - 0.5f * 0.3f, WeaponWearMath.BaseDamageMultiplier(0.5f), Tolerance);
        }

        [Test]
        public void MeleeDamageMultiplier_IsOneUnworn_AndPointFourAtFullWear()
        {
            // WClub.resultDamage (WClub.as:649): (1 - breaking*0.6) — twice as steep as the base.
            Assert.AreEqual(1f, WeaponWearMath.MeleeDamageMultiplier(0f), Tolerance);
            Assert.AreEqual(1f - 0.6f, WeaponWearMath.MeleeDamageMultiplier(1f), Tolerance);
            Assert.AreEqual(1f - 0.5f * 0.6f, WeaponWearMath.MeleeDamageMultiplier(0.5f), Tolerance);
        }

        [Test]
        public void TheTwoShapes_AgreeUnworn_AndDivergeWithWear()
        {
            // Control against the two functions being one function: they must be identical at 0 and
            // strictly different once the weapon is worn, or a copy-paste would pass unnoticed.
            Assert.AreEqual(WeaponWearMath.BaseDamageMultiplier(0f),
                            WeaponWearMath.MeleeDamageMultiplier(0f), Tolerance,
                "both are exactly 1 when unworn.");

            Assert.Less(WeaponWearMath.MeleeDamageMultiplier(1f),
                        WeaponWearMath.BaseDamageMultiplier(1f),
                "at full wear the melee shape must be the harsher one (0.4 vs 0.7).");
        }

        // ── The composition the state exposes ─────────────────────────────────

        [Test]
        public void BreakingFeedsTheMeleeMultiplier_ForTheWorkedExample()
        {
            // A weapon at 25% durability: breaking = (100-25)/100*2 - 1 = 0.5, so a melee hit carries
            // 70% damage. This is the end-to-end arithmetic the controller performs
            // (State.Breaking() -> MeleeDamageMultiplier).
            float breaking = WeaponWearMath.Breaking(100, 25);
            Assert.AreEqual(0.5f, breaking, Tolerance);
            Assert.AreEqual(0.7f, WeaponWearMath.MeleeDamageMultiplier(breaking), Tolerance);
        }
    }
}

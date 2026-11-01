using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the "starred enemy" table — AS3 <c>Unit.setHero()</c> (<c>Unit.as:1625-1672</c>),
    /// <c>setHeroVulners()</c> (<c>:1674-1695</c>) and the promotion roll in
    /// <c>Location.as:1191-1198</c>.
    ///
    /// <para><b>Why a fixture and not the controller.</b> <c>UnitController</c> is a
    /// <c>MonoBehaviour</c>; <c>ApplyHeroTier</c> reads <c>UnitStats</c> and writes
    /// <c>ReactiveProperty</c> values, so touching it needs <c>AddComponent</c> — a Unity <c>ECall</c>
    /// that throws where the native engine is absent. The numbers were extracted to
    /// <see cref="EliteEnemyRules"/> for the same reason <c>AlicornVariantRules</c> exists.</para>
    ///
    /// <para><b>Every expectation is a literal read off the oracle</b>, not a value copied back out of
    /// the implementation. The line reference is in each test's name or body. A fixture that asserts
    /// whatever the code happens to do is a change detector, not a guard.</para>
    /// </summary>
    [TestFixture]
    public class EliteEnemyRulesTests
    {
        private const float Tolerance = 1e-5f;

        // ── the roll (Location.as:1191-1198) ─────────────────────────────────────────────────────

        [Test]
        public void RollChance_AtDifficultyZero_IsTwoPercent()
        {
            // `Math.min(0.05, this.locDifLevel / 100 + 0.02)` with locDifLevel == 0.
            Assert.AreEqual(0.02f, EliteEnemyRules.RollChance(0f), Tolerance);
        }

        [Test]
        public void RollChance_RisesOnePercentPerDifficultyPoint()
        {
            // 1/100 + 0.02 = 0.03, 2/100 + 0.02 = 0.04.
            Assert.AreEqual(0.03f, EliteEnemyRules.RollChance(1f), Tolerance);
            Assert.AreEqual(0.04f, EliteEnemyRules.RollChance(2f), Tolerance);
        }

        [Test]
        public void RollChance_IsCappedAtFivePercent()
        {
            // The cap bites at difficulty 3 (0.03 + 0.02) and never lifts.
            Assert.AreEqual(0.05f, EliteEnemyRules.RollChance(3f), Tolerance);
            Assert.AreEqual(0.05f, EliteEnemyRules.RollChance(20f), Tolerance);
            Assert.AreEqual(0.05f, EliteEnemyRules.RollChance(1000f), Tolerance);
        }

        [Test]
        public void RollChance_NegativeDifficulty_IsClampedAtTheBaseChance()
        {
            // Deliberate divergence, documented on the method: AS3 would compute a negative chance and
            // silently promote nobody, which is indistinguishable from "the feature is broken".
            Assert.AreEqual(0.02f, EliteEnemyRules.RollChance(-50f), Tolerance);
        }

        [Test]
        public void RollTier_SplitsTheUnitIntervalIntoFourEqualBands()
        {
            // `Math.floor(Math.random() * 4 + 1)`
            Assert.AreEqual(1, EliteEnemyRules.RollTier(0.0));
            Assert.AreEqual(1, EliteEnemyRules.RollTier(0.2499));
            Assert.AreEqual(2, EliteEnemyRules.RollTier(0.25));
            Assert.AreEqual(2, EliteEnemyRules.RollTier(0.4999));
            Assert.AreEqual(3, EliteEnemyRules.RollTier(0.5));
            Assert.AreEqual(3, EliteEnemyRules.RollTier(0.7499));
            Assert.AreEqual(4, EliteEnemyRules.RollTier(0.75));
            Assert.AreEqual(4, EliteEnemyRules.RollTier(0.9999));
        }

        [Test]
        public void RollTier_ACallerSuppliedOne_ClampsToTierFour()
        {
            // `Math.random()` never returns 1, but a caller's generator can. Tier 5 matches no branch in
            // setHero, so it would hand out the name and the XP with no stat block at all.
            Assert.AreEqual(4, EliteEnemyRules.RollTier(1.0));
        }

        // ── the tier branches (Unit.as:1648-1670) ───────────────────────────────────────────────

        [Test]
        public void HealthMultiplier_IsTheOraclesFourBranches()
        {
            // tier 1 ×2.5; tier 2 and 3 share ×3; tier 4 ×2.
            Assert.AreEqual(2.5f, EliteEnemyRules.HealthMultiplier(1), Tolerance);
            Assert.AreEqual(3f, EliteEnemyRules.HealthMultiplier(2), Tolerance);
            Assert.AreEqual(3f, EliteEnemyRules.HealthMultiplier(3), Tolerance);
            Assert.AreEqual(2f, EliteEnemyRules.HealthMultiplier(4), Tolerance);
        }

        [Test]
        public void DamageMultiplier_IsTheOraclesFourBranches()
        {
            // `this.dam *= 1.8` / `1.2` (tiers 2 and 3) / `1.4`.
            Assert.AreEqual(1.8f, EliteEnemyRules.DamageMultiplier(1), Tolerance);
            Assert.AreEqual(1.2f, EliteEnemyRules.DamageMultiplier(2), Tolerance);
            Assert.AreEqual(1.2f, EliteEnemyRules.DamageMultiplier(3), Tolerance);
            Assert.AreEqual(1.4f, EliteEnemyRules.DamageMultiplier(4), Tolerance);
        }

        [Test]
        public void WeaponDamageMultiplier_IsTierOneOnly()
        {
            // `if(this.currentWeapon) this.currentWeapon.damage *= 1.5` sits inside the `hero == 1`
            // branch — the other three tiers get no weapon multiplier at all.
            Assert.AreEqual(1.5f, EliteEnemyRules.WeaponDamageMultiplier(1), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.WeaponDamageMultiplier(2), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.WeaponDamageMultiplier(3), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.WeaponDamageMultiplier(4), Tolerance);
        }

        [Test]
        public void SpeedAndObservationBonuses_AreTierFourOnly()
        {
            // `walkSpeed *= 1.4; sitSpeed *= 1.4; runSpeed *= 1.25; observ += 8` — one branch.
            Assert.AreEqual(1.4f, EliteEnemyRules.WalkSpeedMultiplier(4), Tolerance);
            Assert.AreEqual(1.25f, EliteEnemyRules.RunSpeedMultiplier(4), Tolerance);
            Assert.AreEqual(8f, EliteEnemyRules.ObservationBonus(4), Tolerance);

            for (int tier = 0; tier <= 3; tier++)
            {
                Assert.AreEqual(1f, EliteEnemyRules.WalkSpeedMultiplier(tier), Tolerance, "walk tier " + tier);
                Assert.AreEqual(1f, EliteEnemyRules.RunSpeedMultiplier(tier), Tolerance, "run tier " + tier);
                Assert.AreEqual(0f, EliteEnemyRules.ObservationBonus(tier), Tolerance, "observ tier " + tier);
            }
        }

        [Test]
        public void AnUnpromotedUnit_GetsNoStatBlock()
        {
            // `hero == 0` matches no branch. This is the guard against "everything is an elite".
            Assert.AreEqual(1f, EliteEnemyRules.HealthMultiplier(0), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.DamageMultiplier(0), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.WeaponDamageMultiplier(0), Tolerance);
        }

        // ── setHeroVulners (Unit.as:1674-1695) ──────────────────────────────────────────────────

        [Test]
        public void EveryTier_ResistsEmpAndNecrotic()
        {
            // `vulner[D_EMP] *= 0.8; vulner[D_NECRO] *= 0.7;` — outside every `hero ==` branch.
            for (int tier = 0; tier <= EliteEnemyRules.MaxTier; tier++)
            {
                float emp = tier == 3 ? 0.8f * 0.75f : 0.8f;
                Assert.AreEqual(emp, EliteEnemyRules.VulnerabilityMultiplier(tier, DamageType.EMP),
                                Tolerance, "EMP at tier " + tier);
                Assert.AreEqual(0.7f, EliteEnemyRules.VulnerabilityMultiplier(tier, DamageType.Necrotic),
                                Tolerance, "necro at tier " + tier);
            }
        }

        [Test]
        public void TierZero_StillGetsTheSharedReductions()
        {
            // The block is NOT an elite perk: `Location.as:1204` calls `setHero(0)` unconditionally, and
            // `setHeroVulners()` is the last statement of `setHero`. Reading tier 0 as "no reductions"
            // silently drops the block from every hero-capable row that is not currently promoted.
            Assert.AreEqual(0.8f, EliteEnemyRules.VulnerabilityMultiplier(0, DamageType.EMP), Tolerance);
            Assert.AreEqual(0.7f, EliteEnemyRules.VulnerabilityMultiplier(0, DamageType.Necrotic), Tolerance);

            // …and nothing tier-specific, because `if(this.hero == 2)` / `== 3` are false at 0.
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(0, DamageType.PhysicalBullet), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(0, DamageType.Laser), Tolerance);
        }

        [Test]
        public void TierThree_StacksItsExtraEmpReductionOntoTheSharedOne()
        {
            // `vulner[D_EMP] *= 0.8` then, in the `hero == 3` branch, `*= 0.75`. Multiplied, not
            // overwritten: 0.6. A port that assigned 0.75 would make tier 3 the *least* EMP-resistant.
            Assert.AreEqual(0.6f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.EMP), Tolerance);
        }

        [Test]
        public void TierTwo_IsTheBulletSponge()
        {
            Assert.AreEqual(0.5f, EliteEnemyRules.VulnerabilityMultiplier(2, DamageType.PhysicalBullet), Tolerance);
            Assert.AreEqual(0.65f, EliteEnemyRules.VulnerabilityMultiplier(2, DamageType.PhysicalMelee), Tolerance);
            Assert.AreEqual(0.65f, EliteEnemyRules.VulnerabilityMultiplier(2, DamageType.Blade), Tolerance);
            Assert.AreEqual(0.75f, EliteEnemyRules.VulnerabilityMultiplier(2, DamageType.Explosive), Tolerance);

            // …and only tier 2.
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(1, DamageType.PhysicalBullet), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.PhysicalBullet), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(4, DamageType.PhysicalBullet), Tolerance);
        }

        [Test]
        public void TierThree_IsTheElementalCaster()
        {
            Assert.AreEqual(0.6f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.Laser), Tolerance);
            Assert.AreEqual(0.5f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.Plasma), Tolerance);
            Assert.AreEqual(0.7f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.Spark), Tolerance);
            Assert.AreEqual(0.7f, EliteEnemyRules.VulnerabilityMultiplier(3, DamageType.Fire), Tolerance);

            // …and only tier 3.
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(2, DamageType.Laser), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(4, DamageType.Fire), Tolerance);
        }

        [Test]
        public void AnOutOfRangeTier_ReducesNothing()
        {
            // Neither the roll (clamped) nor an authored placement produces 5 or -1, so this is the
            // "corrupt data" answer rather than a game state: leave the table alone.
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(5, DamageType.EMP), Tolerance);
            Assert.AreEqual(1f, EliteEnemyRules.VulnerabilityMultiplier(-1, DamageType.Necrotic), Tolerance);
        }

        [Test]
        public void ExpressibleTypes_CoversEverySlotTheReductionsTouch()
        {
            // The caller loops over this array, so a type missing here is a reduction that silently
            // never happens. Every type the oracle reduces and the port can store must be present.
            var expected = new[]
            {
                DamageType.PhysicalBullet, DamageType.Blade, DamageType.PhysicalMelee,
                DamageType.Fire, DamageType.Explosive, DamageType.Laser, DamageType.Plasma,
                DamageType.Spark, DamageType.EMP, DamageType.Necrotic,
            };

            foreach (DamageType type in expected)
            {
                Assert.Contains(type, EliteEnemyRules.ExpressibleTypes, "missing " + type);
            }
        }
    }
}

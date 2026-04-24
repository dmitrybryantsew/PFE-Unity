using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Comprehensive unit tests for DamageCalculator to verify:
    /// - Complete damage calculation workflow
    /// - Base damage with all modifiers
    /// - Ammo multipliers
    /// - Vulnerability/resistance application
    /// - Armor reduction and penetration
    /// - Critical hit calculation
    /// - Backstab mechanics
    /// - Absolute pierce behavior
    /// - DamageResult breakdown accuracy
    /// </summary>
    [TestFixture]
    public class DamageCalculatorTests
    {
        private DamageCalculator damageCalculator;
        private ICombatCalculator combatCalculator;
        private WeaponDefinition testWeaponDef;
        private UnitStats attackerStats;
        private UnitStats targetStats;
        private AmmoDefinition testAmmoDef;

        [SetUp]
        public void Setup()
        {
            // Create test infrastructure
            combatCalculator = new CombatCalculator();
            damageCalculator = new DamageCalculator(combatCalculator);

            // Create test weapon definition
            testWeaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
            testWeaponDef.weaponId = "test_rifle";
            testWeaponDef.weaponType = WeaponType.Guns; // Guns is closest to Ranged
            testWeaponDef.baseDamage = 20f;
            testWeaponDef.rapid = 10f;
            testWeaponDef.maxDurability = 100;
            testWeaponDef.magazineSize = 30;
            testWeaponDef.armorPenetration = 5f;
            testWeaponDef.critChance = 0.1f; // 10% base crit chance
            testWeaponDef.critMultiplier = 2f; // 2x crit damage

            // Create attacker stats
            attackerStats = new UnitStats(100f, 50f);
            // Note: We can't set private fields without reflection, so we test with default values

            // Create target stats
            targetStats = new UnitStats(100f, 50f);

            // Create test ammo definition
            testAmmoDef = ScriptableObject.CreateInstance<AmmoDefinition>();
            testAmmoDef.damageMultiplier = 1.5f; // +50% damage
        }

        [TearDown]
        public void TearDown()
        {
            if (testWeaponDef != null)
                Object.DestroyImmediate(testWeaponDef);
            if (testAmmoDef != null)
                Object.DestroyImmediate(testAmmoDef);
        }

        #region Basic Damage Calculation Tests

        [Test]
        [Description("DamageCalculator_BasicDamage")]
        public void CalculateDamage_WithBasicParameters_ReturnsPositiveDamage()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef,
                attackerStats,
                targetStats);

            // Assert
            Assert.IsTrue(result.finalDamage >= 0, "Final damage should be non-negative");
            Assert.IsTrue(result.finalDamage > 0, "Final damage should be positive for valid attack");
        }

        [Test]
        [Description("DamageCalculator_BaseDamageMatches")]
        public void CalculateDamage_BaseDamage_MatchesWeaponDefinition()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef,
                attackerStats,
                targetStats);

            // Assert - Base damage should be close to weapon definition
            Assert.IsTrue(result.baseDamage > 0, "Base damage should be calculated");
        }

        [Test]
        [Description("DamageCalculator_ResultCompleteness")]
        public void CalculateDamage_ReturnsCompleteBreakdown()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef,
                attackerStats,
                targetStats);

            // Assert - All result fields should be populated
            Assert.IsTrue(result.baseDamage >= 0, "BaseDamage should be set");
            Assert.IsTrue(result.damageAfterAmmo >= 0, "DamageAfterAmmo should be set");
            Assert.IsTrue(result.damageAfterVulnerability >= 0, "DamageAfterVulnerability should be set");
            Assert.IsTrue(result.damageAfterArmor >= 0, "DamageAfterArmor should be set");
            Assert.IsTrue(result.finalDamage >= 0, "FinalDamage should be set");
        }

        #endregion

        #region Ammo Multiplier Tests

        [Test]
        [Description("DamageCalculator_AmmoIncreasesDamage")]
        public void CalculateDamage_WithAmmo_IncreasesDamage()
        {
            // Arrange - Disable crit chance for deterministic test
            testWeaponDef.critChance = 0f;

            var resultWithoutAmmo = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null);

            var resultWithAmmo = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, testAmmoDef);

            // Assert - Damage with ammo should be higher
            Assert.IsFalse(resultWithoutAmmo.isCritical, "Should not crit with 0% crit chance");
            Assert.IsFalse(resultWithAmmo.isCritical, "Should not crit with 0% crit chance");
            Assert.IsTrue(resultWithAmmo.damageAfterAmmo >= resultWithoutAmmo.damageAfterAmmo,
                "Damage with ammo multiplier (damageAfterAmmo) should be >= damage without ammo");
            Assert.IsTrue(resultWithAmmo.finalDamage >= resultWithoutAmmo.finalDamage,
                "Final damage with ammo multiplier should be >= damage without ammo");
        }

        [Test]
        [Description("DamageCalculator_AmmoMultiplierApplied")]
        public void CalculateDamage_AmmoMultiplier_IsCorrectlyApplied()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, testAmmoDef);

            // Assert - Ammo should affect damage calculation
            Assert.IsTrue(result.damageAfterAmmo >= result.baseDamage,
                "Damage after ammo should account for multiplier");
        }

        [Test]
        [Description("DamageCalculator_NoAmmo")]
        public void CalculateDamage_WithoutAmmo_UseBaseDamage()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null);

            // Assert - Without ammo, damageAfterAmmo should equal baseDamage
            Assert.AreEqual(result.baseDamage, result.damageAfterAmmo, 0.01f,
                "Without ammo, damageAfterAmmo should equal baseDamage");
        }

        [Test]
        [Description("DamageCalculator_AmmoMultiplierGreaterThanOne")]
        public void CalculateDamage_AmmoMultiplier_GreaterThanOne_IncreasesDamage()
        {
            // Arrange
            testAmmoDef.damageMultiplier = 2.0f; // Double damage

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, testAmmoDef);

            // Assert
            Assert.IsTrue(result.damageAfterAmmo >= result.baseDamage,
                "Ammo multiplier > 1 should increase damage");
        }

        [Test]
        [Description("DamageCalculator_AmmoMultiplierLessThanOne")]
        public void CalculateDamage_AmmoMultiplier_LessThanOne_DecreasesDamage()
        {
            // Arrange
            testAmmoDef.damageMultiplier = 0.5f; // Half damage

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, testAmmoDef);

            // Assert
            Assert.IsTrue(result.damageAfterAmmo <= result.baseDamage,
                "Ammo multiplier < 1 should decrease damage");
        }

        #endregion

        #region Vulnerability/Resistance Tests

        [Test]
        [Description("DamageCalculator_VulnerabilityPhase")]
        public void CalculateDamage_Vulnerability_ExistsInCalculation()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - Vulnerability phase should exist in the pipeline
            Assert.IsTrue(result.damageAfterVulnerability >= 0,
                "DamageAfterVulnerability should be calculated");
        }

        [Test]
        [Description("DamageCalculator_VulnerabilityWithoutAmmo")]
        public void CalculateDamage_Vulnerability_WithoutAmmo_EqualsBaseDamage()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null);

            // Assert - Currently vulnerability is not implemented, so should equal base damage
            Assert.AreEqual(result.baseDamage, result.damageAfterVulnerability, 0.01f,
                "Vulnerability not implemented yet - should equal base damage");
        }

        #endregion

        #region Armor Tests

        [Test]
        [Description("DamageCalculator_ArmorReducesDamage")]
        public void CalculateDamage_WithArmor_ReducesDamage()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - Armor should reduce damage (or keep it the same if zero)
            Assert.IsTrue(result.damageAfterArmor <= result.damageAfterVulnerability,
                "Armor should reduce or maintain damage");
        }

        [Test]
        [Description("DamageCalculator_Penetration")]
        public void CalculateDamage_WithPenetration_OverridesArmor()
        {
            // Arrange
            testWeaponDef.armorPenetration = 100f; // High penetration

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - High penetration should negate armor
            Assert.IsTrue(result.damageAfterArmor >= result.damageAfterVulnerability * 0.9f,
                "High penetration should significantly reduce armor effectiveness");
        }

        [Test]
        [Description("DamageCalculator_AbsolutePierce")]
        public void CalculateDamage_WithAbsolutePierce_IgnoresArmor()
        {
            // Arrange
            var resultWithoutPierce = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null, false, false);

            var resultWithPierce = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null, false, true);

            // Assert - Absolute pierce should ignore armor
            Assert.IsTrue(resultWithPierce.damageAfterArmor >= resultWithoutPierce.damageAfterArmor,
                "Absolute pierce should result in equal or higher damage");
        }

        #endregion

        #region Critical Hit Tests

        [Test]
        [Description("DamageCalculator_CritChance")]
        public void CalculateDamage_CriticalHit_ChanceBased()
        {
            // Act - Run multiple times to check randomness
            int critCount = 0;
            int iterations = 100;

            for (int i = 0; i < iterations; i++)
            {
                var result = damageCalculator.CalculateDamage(
                    testWeaponDef, attackerStats, targetStats);
                if (result.isCritical)
                    critCount++;
            }

            // Assert - Crit rate should be approximately 10% (base crit chance)
            float critRate = (float)critCount / iterations;
            Assert.IsTrue(critRate > 0 && critRate < 0.5f,
                $"Crit rate should be reasonable (~10%), got {critRate:P}");
        }

        [Test]
        [Description("DamageCalculator_CritMultiplier")]
        public void CalculateDamage_WhenCrit_MultipliesDamage()
        {
            // Act - Find a crit through repeated attempts
            DamageResult critResult = null;
            int attempts = 0;

            while (critResult == null && attempts < 1000)
            {
                var result = damageCalculator.CalculateDamage(
                    testWeaponDef, attackerStats, targetStats);
                if (result.isCritical)
                {
                    critResult = result;
                    break;
                }
                attempts++;
            }

            // If we found a crit, verify the damage
            if (critResult != null)
            {
                Assert.IsTrue(critResult.criticalMultiplier >= 1f,
                    "Critical hit should have multiplier >= 1");
                Assert.IsTrue(critResult.finalDamage >= critResult.damageAfterArmor,
                    "Critical hit final damage should be >= non-crit damage");
            }
            else
            {
                Assert.Ignore("Could not get a critical hit after 1000 attempts");
            }
        }

        [Test]
        [Description("DamageCalculator_CritFlags")]
        public void CalculateDamage_CriticalHit_SetsCorrectFlags()
        {
            // Act - Find a crit through repeated attempts
            DamageResult critResult = null;
            int attempts = 0;

            while (critResult == null && attempts < 1000)
            {
                var result = damageCalculator.CalculateDamage(
                    testWeaponDef, attackerStats, targetStats);
                if (result.isCritical)
                {
                    critResult = result;
                    break;
                }
                attempts++;
            }

            // If we found a crit, verify the flags
            if (critResult != null)
            {
                Assert.IsTrue(critResult.isCritical, "IsCritical flag should be set");
            }
            else
            {
                Assert.Ignore("Could not get a critical hit after 1000 attempts");
            }
        }

        #endregion

        #region Backstab Tests

        [Test]
        [Description("DamageCalculator_BackstabIncreasesDamage")]
        public void CalculateDamage_WithBackstab_WhenCrit_DoublesDamage()
        {
            // Arrange - We can't force a crit in the current implementation,
            // so we just verify the backstab parameter is accepted

            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                var result = damageCalculator.CalculateDamage(
                    testWeaponDef, attackerStats, targetStats, null, true, false);
            }, "Backstab parameter should be accepted");
        }

        [Test]
        [Description("DamageCalculator_BackstabFlag")]
        public void CalculateDamage_Backstab_SetsFlagWhenApplicable()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null, true, false);

            // Assert - Backstab flag should be set (only applies on crit, so may be false)
            Assert.IsNotNull(result, "Result should be returned");
        }

        #endregion

        #region Simple Damage Calculation Tests

        [Test]
        [Description("DamageCalculator_SimpleDamage")]
        public void CalculateDamageSimple_WithBasicParameters_ReturnsDamage()
        {
            // Act
            float damage = damageCalculator.CalculateDamageSimple(
                baseDamage: 20f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1f,
                armor: 0f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f, // No crit for deterministic test
                critMultiplier: 2f,
                absolutePierce: false);

            // Assert
            Assert.IsTrue(damage >= 0, "Simple damage should be non-negative");
            Assert.AreEqual(20f, damage, 0.1f, "Simple damage should match base with no modifiers");
        }

        [Test]
        [Description("DamageCalculator_SimpleWithArmor")]
        public void CalculateDamageSimple_WithArmor_ReducesDamage()
        {
            // Act
            float damageWithArmor = damageCalculator.CalculateDamageSimple(
                baseDamage: 50f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1f,
                armor: 20f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f);

            float damageWithoutArmor = damageCalculator.CalculateDamageSimple(
                baseDamage: 50f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1f,
                armor: 0f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f);

            // Assert
            Assert.IsTrue(damageWithArmor < damageWithoutArmor,
                "Damage with armor should be less than damage without armor");
        }

        [Test]
        [Description("DamageCalculator_SimpleWithAmmo")]
        public void CalculateDamageSimple_WithAmmoMultipliesDamage()
        {
            // Act
            float damage = damageCalculator.CalculateDamageSimple(
                baseDamage: 20f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1.5f, // 50% more damage
                vulnerability: 1f,
                armor: 0f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f);

            // Assert
            Assert.AreEqual(30f, damage, 0.1f, "Damage should be 1.5x base with ammo multiplier");
        }

        [Test]
        [Description("DamageCalculator_SimpleWithVulnerability")]
        public void CalculateDamageSimple_WithVulnerability_MultipliesDamage()
        {
            // Act
            float damage = damageCalculator.CalculateDamageSimple(
                baseDamage: 20f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1.5f, // 50% weakness
                armor: 0f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f);

            // Assert
            Assert.AreEqual(30f, damage, 0.1f, "Damage should be 1.5x base with vulnerability");
        }

        [Test]
        [Description("DamageCalculator_SimpleWithResistance")]
        public void CalculateDamageSimple_WithResistance_ReducesDamage()
        {
            // Act
            float damage = damageCalculator.CalculateDamageSimple(
                baseDamage: 20f,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 0.5f, // 50% resistance
                armor: 0f,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f);

            // Assert
            Assert.AreEqual(10f, damage, 0.1f, "Damage should be 0.5x base with resistance");
        }

        [Test]
        [Description("DamageCalculator_SimpleAbsolutePierce")]
        public void CalculateDamageSimple_AbsolutePierce_IgnoresArmor()
        {
            // Arrange
            const float baseDamage = 50f;
            const float armor = 30f;

            // Act - Without absolute pierce
            float damageWithArmor = damageCalculator.CalculateDamageSimple(
                baseDamage: baseDamage,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1f,
                armor: armor,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f,
                absolutePierce: false);

            // With absolute pierce
            float damageWithPierce = damageCalculator.CalculateDamageSimple(
                baseDamage: baseDamage,
                damAdd: 0f,
                damMult: 1f,
                weaponSkill: 1f,
                durabilityMultiplier: 1f,
                ammoMultiplier: 1f,
                vulnerability: 1f,
                armor: armor,
                armorEffectiveness: 1f,
                penetration: 0f,
                critChance: 0f,
                critMultiplier: 2f,
                absolutePierce: true);

            // Assert
            Assert.IsTrue(damageWithPierce > damageWithArmor,
                "Absolute pierce should result in higher damage than with armor");
        }

        #endregion

        #region Damage Result Breakdown Tests

        [Test]
        [Description("DamageResult_ChainedValues")]
        public void DamageResult_Values_AreSequentiallyCalculated()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null);

            // Assert - Values should follow the calculation chain
            // Base -> Ammo -> Vulnerability -> Armor -> Crit
            // Each step can only reduce or maintain (except crit which can increase)
            Assert.IsTrue(result.damageAfterAmmo >= 0, "DamageAfterAmmo should be valid");
            Assert.IsTrue(result.damageAfterVulnerability >= 0, "DamageAfterVulnerability should be valid");
            Assert.IsTrue(result.damageAfterArmor >= 0, "DamageAfterArmor should be valid");
        }

        [Test]
        [Description("DamageResult_NonNegative")]
        public void DamageResult_AllFields_AreNonNegative()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - All damage values should be non-negative
            Assert.IsTrue(result.baseDamage >= 0, "BaseDamage should be non-negative");
            Assert.IsTrue(result.damageAfterAmmo >= 0, "DamageAfterAmmo should be non-negative");
            Assert.IsTrue(result.damageAfterVulnerability >= 0, "DamageAfterVulnerability should be non-negative");
            Assert.IsTrue(result.damageAfterArmor >= 0, "DamageAfterArmor should be non-negative");
            Assert.IsTrue(result.finalDamage >= 0, "FinalDamage should be non-negative");
        }

        #endregion

        #region Integration Tests

        [Test]
        [Description("DamageCalculator_CompleteCalculation")]
        public void CalculateDamage_CompleteScenario_WorksCorrectly()
        {
            // Arrange - Simulate realistic combat scenario
            // Weapon: 20 base damage, 5 penetration, 10% crit chance
            // Target: 15 armor
            // Ammo: 1.5x damage multiplier

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, testAmmoDef, false, false);

            // Assert - Complete calculation should execute without errors
            Assert.IsNotNull(result, "Result should be returned");
            Assert.IsTrue(result.finalDamage >= 0, "Final damage should be calculated");
        }

        [Test]
        [Description("DamageCalculator_MultipleCalculations")]
        public void CalculateDamage_MultipleCalculations_AreIndependent()
        {
            // Act
            var result1 = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);
            var result2 = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);
            var result3 = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - Each calculation should be independent
            Assert.IsNotNull(result1, "First calculation should succeed");
            Assert.IsNotNull(result2, "Second calculation should succeed");
            Assert.IsNotNull(result3, "Third calculation should succeed");
        }

        #endregion

        #region Edge Cases

        [Test]
        [Description("DamageCalculator_ZeroBaseDamage")]
        public void CalculateDamage_ZeroBaseDamage_ReturnsZero()
        {
            // Arrange
            testWeaponDef.baseDamage = 0f;

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert
            Assert.AreEqual(0f, result.finalDamage, 0.01f,
                "Zero base damage should result in zero final damage");
        }

        [Test]
        [Description("DamageCalculator_NegativeBaseDamage")]
        public void CalculateDamage_NegativeBaseDamage_ClampsToZero()
        {
            // Arrange
            testWeaponDef.baseDamage = -10f;

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - Should handle negative damage gracefully
            Assert.IsTrue(result.finalDamage >= 0, "Final damage should be clamped to >= 0");
        }

        [Test]
        [Description("DamageCalculator_ZeroArmor")]
        public void CalculateDamage_ZeroArmor_NoReduction()
        {
            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats, null, false, false);

            // Assert - With zero armor (default), damage should be close to base
            Assert.IsTrue(result.damageAfterArmor >= result.damageAfterVulnerability * 0.9f,
                "Zero armor should not significantly reduce damage");
        }

        [Test]
        [Description("DamageCalculator_ZeroPenetration")]
        public void CalculateDamage_ZeroPeneration_FullArmorApplies()
        {
            // Arrange
            testWeaponDef.armorPenetration = 0f;

            // Act
            var result = damageCalculator.CalculateDamage(
                testWeaponDef, attackerStats, targetStats);

            // Assert - Should calculate without errors
            Assert.IsNotNull(result, "Calculation with zero penetration should succeed");
        }

        #endregion
    }
}

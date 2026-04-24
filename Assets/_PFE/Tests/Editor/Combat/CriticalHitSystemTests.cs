using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for CriticalHitSystem to verify:
    /// - Critical hit chance calculation
    /// - Critical hit damage multiplier
    /// - Critical hit rolling
    /// - Backstab detection
    /// - Backstab multiplier
    /// - Absolute pierce rolling
    /// - Combined critical and backstab damage
    /// </summary>
    [TestFixture]
    public class CriticalHitSystemTests
    {
        private CriticalHitSystem criticalHitSystem;
        private ICombatCalculator combatCalculator;
        private WeaponDefinition testWeaponDef;
        private UnitStats testOwnerStats;

        [SetUp]
        public void Setup()
        {
            combatCalculator = new CombatCalculator();
            criticalHitSystem = new CriticalHitSystem(combatCalculator);

            // Create test weapon definition
            testWeaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
            testWeaponDef.weaponId = "test_weapon";
            testWeaponDef.critChance = 0.1f; // 10% base crit chance
            testWeaponDef.critMultiplier = 2f; // 2x crit damage

            // Create test owner stats
            testOwnerStats = new UnitStats(100f, 50f);
            testOwnerStats.critChanceBonus = 0.05f; // 5% bonus crit chance
            testOwnerStats.critDamageBonus = 0.5f; // +0.5 crit damage
            testOwnerStats.critChanceBonusAdditional = 0.02f; // +2% additional crit
        }

        [TearDown]
        public void TearDown()
        {
            if (testWeaponDef != null)
                Object.DestroyImmediate(testWeaponDef);
            // UnitStats is not a ScriptableObject, no need to destroy
        }

        #region Critical Chance Calculation

        [Test]
        public void CalculateCriticalChance_WithDefaults_ReturnsWeaponBaseChance()
        {
            // Arrange
            UnitStats emptyStats = new UnitStats(100f, 50f);

            // Act
            float critChance = criticalHitSystem.CalculateCriticalChance(testWeaponDef, emptyStats);

            // Assert
            Assert.AreEqual(0.1f, critChance, 0.001f, "Should return weapon base crit chance");

            // UnitStats is not a ScriptableObject, no need to destroy
        }

        [Test]
        public void CalculateCriticalChance_WithOwnerBonus_AddsCorrectly()
        {
            // Act
            float critChance = criticalHitSystem.CalculateCriticalChance(testWeaponDef, testOwnerStats);

            // Assert
            // 0.1 (weapon) + 0.05 (owner) + 0.02 (additional) = 0.17
            Assert.AreEqual(0.17f, critChance, 0.001f, "Should sum all crit chance sources");
        }

        [Test]
        public void CalculateCriticalChance_WithAdditionalChance_AddsOnTop()
        {
            // Act
            float critChance = criticalHitSystem.CalculateCriticalChance(
                testWeaponDef,
                testOwnerStats,
                0.03f); // Additional 3%

            // Assert
            // 0.1 + 0.05 + 0.02 + 0.03 = 0.2
            Assert.AreEqual(0.2f, critChance, 0.001f, "Should include additional chance");
        }

        [Test]
        public void CalculateCriticalChance_HighValues_CanExceedOne()
        {
            // Arrange
            testWeaponDef.critChance = 0.5f;
            testOwnerStats.critChanceBonus = 0.3f;
            testOwnerStats.critChanceBonusAdditional = 0.3f;

            // Act
            float critChance = criticalHitSystem.CalculateCriticalChance(testWeaponDef, testOwnerStats);

            // Assert
            Assert.AreEqual(1.1f, critChance, 0.001f, "Crit chance can exceed 100%");
        }

        #endregion

        #region Critical Multiplier Calculation

        [Test]
        public void CalculateCriticalMultiplier_WithDefaults_ReturnsWeaponMultiplier()
        {
            // Arrange
            UnitStats emptyStats = new UnitStats(100f, 50f);

            // Act
            float critMultiplier = criticalHitSystem.CalculateCriticalMultiplier(testWeaponDef, emptyStats);

            // Assert
            Assert.AreEqual(2f, critMultiplier, 0.001f, "Should return weapon crit multiplier");

            // UnitStats is not a ScriptableObject, no need to destroy
        }

        [Test]
        public void CalculateCriticalMultiplier_WithOwnerBonus_AddsCorrectly()
        {
            // Act
            float critMultiplier = criticalHitSystem.CalculateCriticalMultiplier(testWeaponDef, testOwnerStats);

            // Assert
            // 2 (weapon) + 0.5 (owner bonus) = 2.5
            Assert.AreEqual(2.5f, critMultiplier, 0.001f, "Should add owner bonus");
        }

        [Test]
        public void CalculateCriticalMultiplier_WithAdditionalBonus_AddsOnTop()
        {
            // Act
            float critMultiplier = criticalHitSystem.CalculateCriticalMultiplier(
                testWeaponDef,
                testOwnerStats,
                1f); // Additional +1.0

            // Assert
            // 2 + 0.5 + 1 = 3.5
            Assert.AreEqual(3.5f, critMultiplier, 0.001f, "Should include additional multiplier");
        }

        #endregion

        #region Critical Hit Rolling

        [Test]
        public void RollCriticalHit_ZeroChance_NeverSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollCriticalHit(0f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(0, successes, "Should never succeed with 0% chance");
        }

        [Test]
        public void RollCriticalHit_OneChance_AlwaysSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollCriticalHit(1f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(trials, successes, "Should always succeed with 100% chance");
        }

        [Test]
        public void RollCriticalHit_FiftyPercentChance_ApproximatelyHalfSuccess()
        {
            // Arrange
            int successes = 0;
            int trials = 1000;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollCriticalHit(0.5f))
                    successes++;
            }

            // Assert
            // Allow for some variance, but should be approximately 50%
            Assert.Greater(successes, 400, "Should have at least 40% success rate");
            Assert.Less(successes, 600, "Should have at most 60% success rate");
        }

        #endregion

        #region Backstab Detection

        [Test]
        public void IsBackstab_AttackerDirectlyBehind_ReturnsTrue()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + Vector3.back; // Directly behind

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(attackerPosition, targetPosition, targetForward);

            // Assert
            Assert.IsTrue(isBackstab, "Attacker directly behind should be backstab");
        }

        [Test]
        public void IsBackstab_AttackerDirectlyInFront_ReturnsFalse()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + Vector3.forward; // Directly in front

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(attackerPosition, targetPosition, targetForward);

            // Assert
            Assert.IsFalse(isBackstab, "Attacker in front should not be backstab");
        }

        [Test]
        public void IsBackstab_AttackerAtSide_ReturnsFalse()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + Vector3.right; // To the right

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(attackerPosition, targetPosition, targetForward);

            // Assert
            Assert.IsFalse(isBackstab, "Attacker at side should not be backstab (90° threshold)");
        }

        [Test]
        public void IsBackstab_AttackerDiagonallyBehind_ReturnsTrue()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + (Vector3.back + Vector3.right).normalized; // Diagonally behind

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(attackerPosition, targetPosition, targetForward);

            // Assert
            Assert.IsTrue(isBackstab, "Attacker diagonally behind should be backstab (within 90°)");
        }

        [Test]
        public void IsBackstab_CustomAngle_WiderThreshold()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + (Vector3.back + Vector3.right * 2f).normalized; // More to the side

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(
                attackerPosition,
                targetPosition,
                targetForward,
                120f); // Wider 120° threshold

            // Assert
            Assert.IsTrue(isBackstab, "Should be backstab with wider angle threshold");
        }

        [Test]
        public void IsBackstab_CustomAngle_NarrowerThreshold()
        {
            // Arrange
            Vector3 targetPosition = Vector3.zero;
            Vector3 targetForward = Vector3.forward;
            Vector3 attackerPosition = targetPosition + (Vector3.back + Vector3.right).normalized; // Diagonally behind

            // Act
            bool isBackstab = criticalHitSystem.IsBackstab(
                attackerPosition,
                targetPosition,
                targetForward,
                45f); // Narrower 45° threshold

            // Assert
            Assert.IsFalse(isBackstab, "Should not be backstab with narrow angle threshold");
        }

        #endregion

        #region Backstab Multiplier

        [Test]
        public void CalculateBackstabMultiplier_WithBackstab_ReturnsTwo()
        {
            // Act
            float multiplier = criticalHitSystem.CalculateBackstabMultiplier(true);

            // Assert
            Assert.AreEqual(2f, multiplier, "Backstab should double damage");
        }

        [Test]
        public void CalculateBackstabMultiplier_WithoutBackstab_ReturnsOne()
        {
            // Act
            float multiplier = criticalHitSystem.CalculateBackstabMultiplier(false);

            // Assert
            Assert.AreEqual(1f, multiplier, "No backstab should not multiply damage");
        }

        #endregion

        #region Absolute Pierce

        [Test]
        public void RollAbsolutePierce_ZeroChance_NeverSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollAbsolutePierce(0f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(0, successes, "Should never succeed with 0% chance");
        }

        [Test]
        public void RollAbsolutePierce_PositiveChance_CanSucceed()
        {
            // Arrange
            int successes = 0;
            int trials = 1000;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollAbsolutePierce(0.5f))
                    successes++;
            }

            // Assert
            // Should have some successes with 50% chance
            Assert.Greater(successes, 400, "Should have at least 40% success rate");
            Assert.Less(successes, 600, "Should have at most 60% success rate");
        }

        [Test]
        public void RollAbsolutePierce_OneChance_AlwaysSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollAbsolutePierce(1f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(trials, successes, "Should always succeed with 100% chance");
        }

        [Test]
        public void RollAbsolutePierce_NegativeChance_NeverSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (criticalHitSystem.RollAbsolutePierce(-0.1f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(0, successes, "Negative chance should never succeed");
        }

        #endregion

        #region Combined Damage Application

        [Test]
        public void ApplyCriticalAndBackstab_NoModifiers_ReturnsBaseDamage()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(100f, 2f, false, false);

            // Assert
            Assert.AreEqual(100f, damage, "No modifiers should return base damage");
        }

        [Test]
        public void ApplyCriticalAndBackstab_CritOnly_MultipliesByCritMultiplier()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(100f, 2.5f, true, false);

            // Assert
            Assert.AreEqual(250f, damage, "100 * 2.5 = 250");
        }

        [Test]
        public void ApplyCriticalAndBackstab_BackstabOnly_DoublesDamage()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(100f, 2f, false, true);

            // Assert
            Assert.AreEqual(200f, damage, "100 * 2 = 200");
        }

        [Test]
        public void ApplyCriticalAndBackstab_CritAndBackstab_MultipliesBoth()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(100f, 2f, true, true);

            // Assert
            // 100 * 2 (crit) * 2 (backstab) = 400
            Assert.AreEqual(400f, damage, "100 * 2 * 2 = 400");
        }

        [Test]
        public void ApplyCriticalAndBackstab_CritAndBackstab_DifferentCritMultiplier()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(100f, 3f, true, true);

            // Assert
            // 100 * 3 (crit) * 2 (backstab) = 600
            Assert.AreEqual(600f, damage, "100 * 3 * 2 = 600");
        }

        [Test]
        public void ApplyCriticalAndBackstab_WithZeroBaseDamage_ReturnsZero()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(0f, 2f, true, true);

            // Assert
            Assert.AreEqual(0f, damage, "Zero base damage should remain zero");
        }

        [Test]
        public void ApplyCriticalAndBackstab_LargeNumbers_CalculatesCorrectly()
        {
            // Act
            float damage = criticalHitSystem.ApplyCriticalAndBackstab(1000f, 5f, true, true);

            // Assert
            // 1000 * 5 * 2 = 10000
            Assert.AreEqual(10000f, damage, "1000 * 5 * 2 = 10000");
        }

        #endregion
    }
}

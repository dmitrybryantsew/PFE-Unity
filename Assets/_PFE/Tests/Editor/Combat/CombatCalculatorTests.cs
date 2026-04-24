using NUnit.Framework;
using PFE.Systems.Combat;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for CombatCalculator to verify:
    /// - Frame-to-second conversions
    /// - Fire rate calculations
    /// - Durability calculations
    /// - Accuracy calculations
    /// - Damage calculations
    /// - Critical hit calculations
    /// - Melee calculations
    /// - Projectile conversions
    /// </summary>
    [TestFixture]
    public class CombatCalculatorTests
    {
        private CombatCalculator calculator;

        [SetUp]
        public void Setup()
        {
            calculator = new CombatCalculator();
        }

        #region Frame Conversions

        [Test]
        public void FramesToSeconds_TenFrames_ReturnsOneThird()
        {
            // Act
            float seconds = calculator.FramesToSeconds(10f);

            // Assert
            Assert.AreEqual(1f / 3f, seconds, 0.001f, "10 frames at 30 FPS = 0.333 seconds");
        }

        [Test]
        public void FramesToSeconds_ThirtyFrames_ReturnsOne()
        {
            // Act
            float seconds = calculator.FramesToSeconds(30f);

            // Assert
            Assert.AreEqual(1f, seconds, 0.001f, "30 frames at 30 FPS = 1 second");
        }

        [Test]
        public void FramesToSeconds_SixtyFrames_ReturnsTwo()
        {
            // Act
            float seconds = calculator.FramesToSeconds(60f);

            // Assert
            Assert.AreEqual(2f, seconds, 0.001f, "60 frames at 30 FPS = 2 seconds");
        }

        [Test]
        public void SecondsToFrames_OneSecond_ReturnsThirty()
        {
            // Act
            float frames = calculator.SecondsToFrames(1f);

            // Assert
            Assert.AreEqual(30f, frames, "1 second at 30 FPS = 30 frames");
        }

        [Test]
        public void SecondsToFrames_TwoSeconds_ReturnsSixty()
        {
            // Act
            float frames = calculator.SecondsToFrames(2f);

            // Assert
            Assert.AreEqual(60f, frames, "2 seconds at 30 FPS = 60 frames");
        }

        [Test]
        public void CalculateFireRate_RapidTen_ReturnsThreeShotsPerSecond()
        {
            // Act
            float fireRate = calculator.CalculateFireRate(10f);

            // Assert
            Assert.AreEqual(3f, fireRate, "rapid=10 means 3 shots/sec at 30 FPS");
        }

        [Test]
        public void CalculateFireRate_RapidThirty_ReturnsOneShotPerSecond()
        {
            // Act
            float fireRate = calculator.CalculateFireRate(30f);

            // Assert
            Assert.AreEqual(1f, fireRate, "rapid=30 means 1 shot/sec at 30 FPS");
        }

        [Test]
        public void CalculateFireRate_RapidFive_ReturnsSixShotsPerSecond()
        {
            // Act
            float fireRate = calculator.CalculateFireRate(5f);

            // Assert
            Assert.AreEqual(6f, fireRate, "rapid=5 means 6 shots/sec at 30 FPS");
        }

        #endregion

        #region Durability Calculations

        [Test]
        public void CalculateBreaking_FullDurability_ReturnsMinusOne()
        {
            // Act
            float breaking = calculator.CalculateBreaking(100, 100);

            // Assert
            Assert.AreEqual(-1f, breaking, 0.001f, "100% durability should have breaking = -1");
        }

        [Test]
        public void CalculateBreaking_HalfDurability_ReturnsZero()
        {
            // Act
            float breaking = calculator.CalculateBreaking(100, 50);

            // Assert
            Assert.AreEqual(0f, breaking, 0.001f, "50% durability should have breaking = 0");
        }

        [Test]
        public void CalculateBreaking_ZeroDurability_ReturnsOne()
        {
            // Act
            float breaking = calculator.CalculateBreaking(100, 0);

            // Assert
            Assert.AreEqual(1f, breaking, 0.001f, "0% durability should have breaking = 1");
        }

        [Test]
        public void CalculateBreaking_QuarterDurability_ReturnsZeroPointFive()
        {
            // Act
            float breaking = calculator.CalculateBreaking(100, 25);

            // Assert
            Assert.AreEqual(0.5f, breaking, 0.001f, "25% durability should have breaking = 0.5");
        }

        [Test]
        public void CalculateDeviationMultiplier_AtFullDurability_ReturnsOne()
        {
            // Act
            float multiplier = calculator.CalculateDeviationMultiplier(-1f);

            // Assert
            Assert.AreEqual(-1f, multiplier, 0.001f, "Full durability: 1 + (-1) * 2 = -1");
        }

        [Test]
        public void CalculateDeviationMultiplier_AtHalfDurability_ReturnsOne()
        {
            // Act
            float multiplier = calculator.CalculateDeviationMultiplier(0f);

            // Assert
            Assert.AreEqual(1f, multiplier, 0.001f, "Half durability: 1 + 0 * 2 = 1");
        }

        [Test]
        public void CalculateDeviationMultiplier_AtZeroDurability_ReturnsThree()
        {
            // Act
            float multiplier = calculator.CalculateDeviationMultiplier(1f);

            // Assert
            Assert.AreEqual(3f, multiplier, 0.001f, "Zero durability: 1 + 1 * 2 = 3");
        }

        [Test]
        public void CalculateDurabilityDamageMultiplier_AtFullDurability_ReturnsOnePointThree()
        {
            // Act
            float multiplier = calculator.CalculateDurabilityDamageMultiplier(-1f);

            // Assert
            Assert.AreEqual(1.3f, multiplier, 0.001f, "Full durability: 1 - (-1) * 0.3 = 1.3");
        }

        [Test]
        public void CalculateDurabilityDamageMultiplier_AtHalfDurability_ReturnsOne()
        {
            // Act
            float multiplier = calculator.CalculateDurabilityDamageMultiplier(0f);

            // Assert
            Assert.AreEqual(1f, multiplier, 0.001f, "Half durability: 1 - 0 * 0.3 = 1");
        }

        [Test]
        public void CalculateDurabilityDamageMultiplier_AtZeroDurability_ReturnsZeroPointSeven()
        {
            // Act
            float multiplier = calculator.CalculateDurabilityDamageMultiplier(1f);

            // Assert
            Assert.AreEqual(0.7f, multiplier, 0.001f, "Zero durability: 1 - 1 * 0.3 = 0.7");
        }

        [Test]
        public void CalculateJamChance_AtZeroDurability_ReturnsCorrectValue()
        {
            // Act
            float jamChance = calculator.CalculateJamChance(1f, 30, 1f);

            // Assert
            Assert.AreEqual(1f / 30f, jamChance, 0.001f, "breaking=1, mag=30: jam = 1/30");
        }

        [Test]
        public void CalculateJamChance_SmallMagazine_IncreasesChance()
        {
            // Act
            float jamChance = calculator.CalculateJamChance(1f, 10, 1f);

            // Assert
            Assert.AreEqual(1f / 10f, jamChance, 0.001f, "breaking=1, mag=10: jam = 1/10");
        }

        [Test]
        public void CalculateMisfireChance_AtZeroDurability_ReturnsZeroPointTwo()
        {
            // Act
            float misfireChance = calculator.CalculateMisfireChance(1f, 1f);

            // Assert
            Assert.AreEqual(0.2f, misfireChance, 0.001f, "breaking=1: misfire = 1/5 = 0.2");
        }

        [Test]
        public void CalculateMisfireChance_AtHalfDurability_ReturnsZero()
        {
            // Act
            float misfireChance = calculator.CalculateMisfireChance(0f, 1f);

            // Assert
            Assert.AreEqual(0f, misfireChance, 0.001f, "breaking=0: misfire = 0");
        }

        #endregion

        #region Accuracy Calculations

        [Test]
        public void CalculateEffectiveDeviation_WithDefaults_ReturnsBaseDeviation()
        {
            // Act
            float deviation = calculator.CalculateEffectiveDeviation(5f, 0f);

            // Assert
            Assert.AreEqual(5f, deviation, 0.001f, "Base deviation with no modifiers");
        }

        [Test]
        public void CalculateEffectiveDeviation_WithDurabilityPenalty_IncreasesDeviation()
        {
            // Act
            float deviation = calculator.CalculateEffectiveDeviation(5f, 1f);

            // Assert
            Assert.AreEqual(15f, deviation, 0.001f, "Zero durability triples deviation");
        }

        [Test]
        public void CalculateEffectiveDeviation_WithSkillBonus_ReducesDeviation()
        {
            // Act
            float deviation = calculator.CalculateEffectiveDeviation(5f, 0f, 2f, 1f);

            // Assert
            Assert.AreEqual(2.5f, deviation, 0.001f, "skillConf=2 halves deviation");
        }

        [Test]
        public void CalculateEffectiveDeviation_WithWeaponSkill_ReducesDeviation()
        {
            // Act
            float deviation = calculator.CalculateEffectiveDeviation(5f, 0f, 1f, 2f);

            // Assert
            Assert.AreEqual(2.5f, deviation, 0.01f, "weaponSkill=2 approximately halves deviation");
        }

        #endregion

        #region Damage Calculations

        [Test]
        public void CalculateBaseDamage_WithDefaults_ReturnsBaseDamage()
        {
            // Act
            float damage = calculator.CalculateBaseDamage(100f);

            // Assert
            Assert.AreEqual(100f, damage, "Base damage with no modifiers");
        }

        [Test]
        public void CalculateBaseDamage_WithDamAdd_AddsDamage()
        {
            // Act
            float damage = calculator.CalculateBaseDamage(100f, 20f);

            // Assert
            Assert.AreEqual(120f, damage, "100 + 20 = 120");
        }

        [Test]
        public void CalculateBaseDamage_WithDamMultiplies_MultipliesDamage()
        {
            // Act
            float damage = calculator.CalculateBaseDamage(100f, 0f, 1.5f);

            // Assert
            Assert.AreEqual(150f, damage, "100 * 1.5 = 150");
        }

        [Test]
        public void CalculateBaseDamage_WithAllModifiers_CombinesCorrectly()
        {
            // Act
            float damage = calculator.CalculateBaseDamage(100f, 20f, 1.5f, 1.2f, 1.1f, 0.9f);

            // Assert
            float expected = (100f + 20f) * 1.5f * 1.2f * 1.1f * 0.9f;
            Assert.AreEqual(expected, damage, 0.01f, "All modifiers combine multiplicatively");
        }

        [Test]
        public void CalculateEffectiveArmor_WithPenetration_ReducesArmor()
        {
            // Act
            float effectiveArmor = calculator.CalculateEffectiveArmor(50f, 1f, 10f);

            // Assert
            Assert.AreEqual(40f, effectiveArmor, 0.001f, "50 - 10 = 40");
        }

        [Test]
        public void CalculateEffectiveArmor_HighPenetration_DoesNotGoNegative()
        {
            // Act
            float effectiveArmor = calculator.CalculateEffectiveArmor(50f, 1f, 100f);

            // Assert
            Assert.AreEqual(0f, effectiveArmor, "Should clamp to 0");
        }

        [Test]
        public void ApplyArmor_DamageGreaterThanArmor_ReturnsRemainingDamage()
        {
            // Act
            float remainingDamage = calculator.ApplyArmor(100f, 30f);

            // Assert
            Assert.AreEqual(70f, remainingDamage, "100 - 30 = 70");
        }

        [Test]
        public void ApplyArmor_ArmorGreaterThanDamage_ReturnsZero()
        {
            // Act
            float remainingDamage = calculator.ApplyArmor(30f, 50f);

            // Assert
            Assert.AreEqual(0f, remainingDamage, "Should not go negative");
        }

        [Test]
        public void CalculateLevelPenalty_PlayerEqualOrHigher_ReturnsNoPenalty()
        {
            // Act
            float penalty1 = calculator.CalculateLevelPenalty(5, 5);
            float penalty2 = calculator.CalculateLevelPenalty(5, 10);

            // Assert
            Assert.AreEqual(1f, penalty1, "Equal level: no penalty");
            Assert.AreEqual(1f, penalty2, "Higher level: no penalty");
        }

        [Test]
        public void CalculateLevelPenalty_OneLevelBelow_ReturnsZeroPointNine()
        {
            // Act
            float penalty = calculator.CalculateLevelPenalty(5, 4);

            // Assert
            Assert.AreEqual(0.9f, penalty, 0.001f, "1 level below: 10% penalty");
        }

        [Test]
        public void CalculateLevelPenalty_FiveLevelsBelow_ReturnsZeroPointFive()
        {
            // Act
            float penalty = calculator.CalculateLevelPenalty(5, 0);

            // Assert
            Assert.AreEqual(0.5f, penalty, 0.001f, "5 levels below: 50% penalty");
        }

        #endregion

        #region Critical Hit Calculations

        [Test]
        public void CalculateCriticalChance_WithDefaults_ReturnsBaseCrit()
        {
            // Act
            float critChance = calculator.CalculateCriticalChance(0.1f);

            // Assert
            Assert.AreEqual(0.1f, critChance, "Base crit chance only");
        }

        [Test]
        public void CalculateCriticalChance_WithOwnerCrit_AddsToBase()
        {
            // Act
            float critChance = calculator.CalculateCriticalChance(0.1f, 0.05f);

            // Assert
            Assert.AreEqual(0.15f, critChance, "0.1 + 0.05 = 0.15");
        }

        [Test]
        public void CalculateCriticalChance_WithAllSources_AddsCorrectly()
        {
            // Act
            float critChance = calculator.CalculateCriticalChance(0.1f, 0.05f, 0.03f);

            // Assert
            Assert.AreEqual(0.18f, critChance, "0.1 + 0.05 + 0.03 = 0.18");
        }

        [Test]
        public void CalculateCriticalMultiplier_WithDefaults_ReturnsOwnerMultiplier()
        {
            // Act
            float critMultiplier = calculator.CalculateCriticalMultiplier(2f);

            // Assert
            Assert.AreEqual(2f, critMultiplier, "Owner multiplier only");
        }

        [Test]
        public void CalculateCriticalMultiplier_WithCritDamPlus_AddsToMultiplier()
        {
            // Act
            float critMultiplier = calculator.CalculateCriticalMultiplier(2f, 0.5f);

            // Assert
            Assert.AreEqual(2.5f, critMultiplier, "2 + 0.5 = 2.5");
        }

        [Test]
        public void RollCriticalHit_ZeroChance_AlwaysFails()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (calculator.RollCriticalHit(0f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(0, successes, "Should never succeed with 0% chance");
        }

        [Test]
        public void RollCriticalHit_HundredChance_AlwaysSucceeds()
        {
            // Arrange
            int successes = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (calculator.RollCriticalHit(1f))
                    successes++;
            }

            // Assert
            Assert.AreEqual(trials, successes, "Should always succeed with 100% chance");
        }

        #endregion

        #region Melee Calculations

        [Test]
        public void CalculatePowerAttackMultiplier_NoCharge_ReturnsOne()
        {
            // Act
            float multiplier = calculator.CalculatePowerAttackMultiplier(0f);

            // Assert
            Assert.AreEqual(1f, multiplier, "No charge: no damage bonus");
        }

        [Test]
        public void CalculatePowerAttackMultiplier_FullCharge_ReturnsTwo()
        {
            // Act
            float multiplier = calculator.CalculatePowerAttackMultiplier(2.15f);

            // Assert
            Assert.AreEqual(2f, multiplier, 0.01f, "Full charge: ~2x damage");
        }

        [Test]
        public void CalculatePowerAttackMultiplier_HalfCharge_ReturnsOnePointFive()
        {
            // Act
            float multiplier = calculator.CalculatePowerAttackMultiplier(1.075f);

            // Assert
            Assert.AreEqual(1.5f, multiplier, 0.01f, "Half charge: ~1.5x damage");
        }

        [Test]
        public void CalculateComboMultiplier_BelowThreshold_ReturnsOne()
        {
            // Act
            float multiplier = calculator.CalculateComboMultiplier(3);

            // Assert
            Assert.AreEqual(1f, multiplier, "Less than 4 hits: no combo bonus");
        }

        [Test]
        public void CalculateComboMultiplier_AtThreshold_ReturnsTwo()
        {
            // Act
            float multiplier = calculator.CalculateComboMultiplier(4);

            // Assert
            Assert.AreEqual(2f, multiplier, "4th hit: 2x damage");
        }

        [Test]
        public void CalculateComboMultiplier_AboveThreshold_ReturnsTwo()
        {
            // Act
            float multiplier = calculator.CalculateComboMultiplier(10);

            // Assert
            Assert.AreEqual(2f, multiplier, "More than 4 hits: still 2x damage");
        }

        #endregion

        #region Projectile Conversions

        [Test]
        public void PixelSpeedToUnitySpeed_OriginalSpeed_ReturnsCorrectUnitySpeed()
        {
            // Act
            float unitySpeed = calculator.PixelSpeedToUnitySpeed(100f);

            // Assert
            Assert.AreEqual(30f, unitySpeed, 0.01f, "100 pixels/frame = 30 units/second");
        }

        [Test]
        public void PixelSpeedToUnitySpeed_HalfSpeed_ReturnsFifteenUnitsPerSecond()
        {
            // Act
            float unitySpeed = calculator.PixelSpeedToUnitySpeed(50f);

            // Assert
            Assert.AreEqual(15f, unitySpeed, 0.01f, "50 pixels/frame = 15 units/second");
        }

        [Test]
        public void CalculateProjectileMaxDistance_WithLifetimeAndSpeed_ReturnsCorrectDistance()
        {
            // Act
            float maxDistance = calculator.CalculateProjectileMaxDistance(60, 100f);

            // Assert
            // 60 frames = 2 seconds, 100 pixels/frame = 30 units/second
            // 2 * 30 = 60 units
            Assert.AreEqual(60f, maxDistance, 0.01f, "60 frames * 100 pixels/frame = 60 units");
        }

        [Test]
        public void CalculateProjectileMaxDistance_WithFasterSpeed_ReturnsGreaterDistance()
        {
            // Act
            float maxDistance = calculator.CalculateProjectileMaxDistance(60, 200f);

            // Assert
            // 60 frames = 2 seconds, 200 pixels/frame = 60 units/second
            // 2 * 60 = 120 units
            Assert.AreEqual(120f, maxDistance, 0.01f, "Faster projectile travels further");
        }

        #endregion

        #region Constants

        [Test]
        public void OriginalFPS_IsThirty()
        {
            // Assert
            Assert.AreEqual(30f, CombatCalculator.ORIGINAL_FPS, "Original game runs at 30 FPS");
        }

        #endregion
    }
}

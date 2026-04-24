using NUnit.Framework;
using PFE.Systems.Combat;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for DurabilitySystem to verify:
    /// - Breaking status calculation
    /// - Durability percentage calculation
    /// - Deviation multiplier based on durability
    /// - Damage multiplier based on durability
    /// - Jam chance calculation
    /// - Misfire chance calculation
    /// - Jam and misfire rolling
    /// - Durability cost calculation
    /// - Durability damage application
    /// - Broken weapon detection
    /// - Repair calculation
    /// </summary>
    [TestFixture]
    public class DurabilitySystemTests
    {
        private DurabilitySystem durabilitySystem;
        private ICombatCalculator combatCalculator;

        [SetUp]
        public void Setup()
        {
            combatCalculator = new CombatCalculator();
            durabilitySystem = new DurabilitySystem(combatCalculator);
        }

        #region Breaking Status Calculation

        [Test]
        public void CalculateBreaking_FullDurability_ReturnsMinusOne()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(100, 100);

            // Assert
            Assert.AreEqual(-1f, breaking, 0.001f, "100% durability should have breaking = -1");
        }

        [Test]
        public void CalculateBreaking_ThreeQuarterDurability_ReturnsMinusZeroPointFive()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(100, 75);

            // Assert
            Assert.AreEqual(-0.5f, breaking, 0.001f, "75% durability should have breaking = -0.5");
        }

        [Test]
        public void CalculateBreaking_HalfDurability_ReturnsZero()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(100, 50);

            // Assert
            Assert.AreEqual(0f, breaking, 0.001f, "50% durability should have breaking = 0");
        }

        [Test]
        public void CalculateBreaking_QuarterDurability_ReturnsZeroPointFive()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(100, 25);

            // Assert
            Assert.AreEqual(0.5f, breaking, 0.001f, "25% durability should have breaking = 0.5");
        }

        [Test]
        public void CalculateBreaking_ZeroDurability_ReturnsOne()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(100, 0);

            // Assert
            Assert.AreEqual(1f, breaking, 0.001f, "0% durability should have breaking = 1");
        }

        [Test]
        public void CalculateBreaking_DifferentMaxDurability_ScalesCorrectly()
        {
            // Act
            float breaking = durabilitySystem.CalculateBreaking(200, 100);

            // Assert
            // (200 - 100) / 200 * 2 - 1 = 0
            Assert.AreEqual(0f, breaking, 0.001f, "50% of 200 should have breaking = 0");
        }

        #endregion

        #region Durability Percentage Calculation

        [Test]
        public void CalculateDurabilityPercentage_FullDurability_ReturnsOne()
        {
            // Act
            float percentage = durabilitySystem.CalculateDurabilityPercentage(100, 100);

            // Assert
            Assert.AreEqual(1f, percentage, 0.001f, "100% durability should return 1.0");
        }

        [Test]
        public void CalculateDurabilityPercentage_HalfDurability_ReturnsZeroPointFive()
        {
            // Act
            float percentage = durabilitySystem.CalculateDurabilityPercentage(100, 50);

            // Assert
            Assert.AreEqual(0.5f, percentage, 0.001f, "50% durability should return 0.5");
        }

        [Test]
        public void CalculateDurabilityPercentage_ZeroDurability_ReturnsZero()
        {
            // Act
            float percentage = durabilitySystem.CalculateDurabilityPercentage(100, 0);

            // Assert
            Assert.AreEqual(0f, percentage, 0.001f, "0% durability should return 0.0");
        }

        [Test]
        public void CalculateDurabilityPercentage_WithFraction_RoundsCorrectly()
        {
            // Act
            float percentage = durabilitySystem.CalculateDurabilityPercentage(100, 33);

            // Assert
            Assert.AreEqual(0.33f, percentage, 0.001f, "33/100 should return 0.33");
        }

        #endregion

        #region Deviation Multiplier Calculation

        [Test]
        public void CalculateDeviationMultiplier_FullDurability_ReturnsMinusOne()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDeviationMultiplier(-1f);

            // Assert
            Assert.AreEqual(-1f, multiplier, 0.001f, "Full durability: 1 + (-1) * 2 = -1");
        }

        [Test]
        public void CalculateDeviationMultiplier_HalfDurability_ReturnsOne()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDeviationMultiplier(0f);

            // Assert
            Assert.AreEqual(1f, multiplier, 0.001f, "Half durability: 1 + 0 * 2 = 1");
        }

        [Test]
        public void CalculateDeviationMultiplier_ZeroDurability_ReturnsThree()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDeviationMultiplier(1f);

            // Assert
            Assert.AreEqual(3f, multiplier, 0.001f, "Zero durability: 1 + 1 * 2 = 3 (deviation triples)");
        }

        [Test]
        public void CalculateDeviationMultiplier_QuarterDurability_ReturnsTwo()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDeviationMultiplier(0.5f);

            // Assert
            Assert.AreEqual(2f, multiplier, 0.001f, "25% durability: 1 + 0.5 * 2 = 2");
        }

        #endregion

        #region Damage Multiplier Calculation

        [Test]
        public void CalculateDamageMultiplier_FullDurability_ReturnsOnePointThree()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDamageMultiplier(-1f);

            // Assert
            Assert.AreEqual(1.3f, multiplier, 0.001f, "Full durability: 1 - (-1) * 0.3 = 1.3 (30% bonus)");
        }

        [Test]
        public void CalculateDamageMultiplier_HalfDurability_ReturnsOne()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDamageMultiplier(0f);

            // Assert
            Assert.AreEqual(1f, multiplier, 0.001f, "Half durability: 1 - 0 * 0.3 = 1");
        }

        [Test]
        public void CalculateDamageMultiplier_ZeroDurability_ReturnsZeroPointSeven()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDamageMultiplier(1f);

            // Assert
            Assert.AreEqual(0.7f, multiplier, 0.001f, "Zero durability: 1 - 1 * 0.3 = 0.7 (30% penalty)");
        }

        [Test]
        public void CalculateDamageMultiplier_QuarterDurability_ReturnsZeroPointEightFive()
        {
            // Act
            float multiplier = durabilitySystem.CalculateDamageMultiplier(0.5f);

            // Assert
            Assert.AreEqual(0.85f, multiplier, 0.001f, "25% durability: 1 - 0.5 * 0.3 = 0.85");
        }

        #endregion

        #region Jam Chance Calculation

        [Test]
        public void CalculateJamChance_ZeroDurability_StandardMagazine_ReturnsCorrectValue()
        {
            // Act
            float jamChance = durabilitySystem.CalculateJamChance(1f, 30, 1f);

            // Assert
            Assert.AreEqual(1f / 30f, jamChance, 0.001f, "breaking=1, mag=30: jam = 1/30");
        }

        [Test]
        public void CalculateJamChance_HalfDurability_ReturnsZero()
        {
            // Act
            float jamChance = durabilitySystem.CalculateJamChance(0f, 30, 1f);

            // Assert
            Assert.AreEqual(0f, jamChance, "Half durability should have 0% jam chance");
        }

        [Test]
        public void CalculateJamChance_SmallMagazine_IncreasesChance()
        {
            // Act
            float jamChance = durabilitySystem.CalculateJamChance(1f, 10, 1f);

            // Assert
            Assert.AreEqual(0.1f, jamChance, 0.001f, "breaking=1, mag=10: jam = 1/10 = 0.1");
        }

        [Test]
        public void CalculateJamChance_LargeMagazine_ReduceChance()
        {
            // Act
            float jamChance = durabilitySystem.CalculateJamChance(1f, 50, 1f);

            // Assert
            Assert.AreEqual(1f / 50f, jamChance, 0.001f, "breaking=1, mag=50: jam = 1/50");
        }

        [Test]
        public void CalculateJamChance_WithJamMultiplier_ScalesCorrectly()
        {
            // Act
            float jamChance = durabilitySystem.CalculateJamChance(1f, 30, 2f);

            // Assert
            Assert.AreEqual(2f / 30f, jamChance, 0.001f, "breaking=1, mag=30, mult=2: jam = 2/30");
        }

        #endregion

        #region Misfire Chance Calculation

        [Test]
        public void CalculateMisfireChance_ZeroDurability_ReturnsZeroPointTwo()
        {
            // Act
            float misfireChance = durabilitySystem.CalculateMisfireChance(1f, 1f);

            // Assert
            Assert.AreEqual(0.2f, misfireChance, 0.001f, "breaking=1: misfire = 1/5 = 0.2");
        }

        [Test]
        public void CalculateMisfireChance_HalfDurability_ReturnsZero()
        {
            // Act
            float misfireChance = durabilitySystem.CalculateMisfireChance(0f, 1f);

            // Assert
            Assert.AreEqual(0f, misfireChance, "Half durability should have 0% misfire chance");
        }

        [Test]
        public void CalculateMisfireChance_WithJamMultiplier_ScalesCorrectly()
        {
            // Act
            float misfireChance = durabilitySystem.CalculateMisfireChance(1f, 2f);

            // Assert
            Assert.AreEqual(0.4f, misfireChance, 0.001f, "breaking=1, mult=2: misfire = 1/5 * 2 = 0.4");
        }

        [Test]
        public void CalculateMisfireChance_QuarterDurability_ReturnsZeroPointOne()
        {
            // Act
            float misfireChance = durabilitySystem.CalculateMisfireChance(0.5f, 1f);

            // Assert
            Assert.AreEqual(0.1f, misfireChance, 0.001f, "breaking=0.5: misfire = 0.5/5 = 0.1");
        }

        #endregion

        #region Jam Rolling

        [Test]
        public void RollJam_ZeroChance_NeverJams()
        {
            // Arrange
            int jams = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollJam(0f))
                    jams++;
            }

            // Assert
            Assert.AreEqual(0, jams, "Should never jam with 0% chance");
        }

        [Test]
        public void RollJam_OneChance_AlwaysJams()
        {
            // Arrange
            int jams = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollJam(1f))
                    jams++;
            }

            // Assert
            Assert.AreEqual(trials, jams, "Should always jam with 100% chance");
        }

        [Test]
        public void RollJam_FiftyPercentChance_ApproximatelyHalfJams()
        {
            // Arrange
            int jams = 0;
            int trials = 1000;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollJam(0.5f))
                    jams++;
            }

            // Assert
            Assert.Greater(jams, 400, "Should have at least 40% jam rate");
            Assert.Less(jams, 600, "Should have at most 60% jam rate");
        }

        #endregion

        #region Misfire Rolling

        [Test]
        public void RollMisfire_ZeroChance_NeverMisfires()
        {
            // Arrange
            int misfires = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollMisfire(0f))
                    misfires++;
            }

            // Assert
            Assert.AreEqual(0, misfires, "Should never misfire with 0% chance");
        }

        [Test]
        public void RollMisfire_OneChance_AlwaysMisfires()
        {
            // Arrange
            int misfires = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollMisfire(1f))
                    misfires++;
            }

            // Assert
            Assert.AreEqual(trials, misfires, "Should always misfire with 100% chance");
        }

        [Test]
        public void RollMisfile_TwentyPercentChance_ApproximatelyTwentyPercentMisfires()
        {
            // Arrange
            int misfires = 0;
            int trials = 1000;

            // Act
            for (int i = 0; i < trials; i++)
            {
                if (durabilitySystem.RollMisfire(0.2f))
                    misfires++;
            }

            // Assert
            Assert.Greater(misfires, 150, "Should have at least 15% misfire rate");
            Assert.Less(misfires, 250, "Should have at most 25% misfire rate");
        }

        #endregion

        #region Durability Cost Calculation

        [Test]
        public void CalculateDurabilityCost_DefaultCost_ReturnsOne()
        {
            // Act
            int cost = durabilitySystem.CalculateDurabilityCost();

            // Assert
            Assert.AreEqual(1, cost, "Default durability cost should be 1");
        }

        [Test]
        public void CalculateDurabilityCost_WithAmmoHp_AddsToBase()
        {
            // Act
            int cost = durabilitySystem.CalculateDurabilityCost(1, 2);

            // Assert
            Assert.AreEqual(3, cost, "Cost should be 1 + 2 = 3");
        }

        [Test]
        public void CalculateDurabilityCost_HighAmmoHp_ReturnsCorrectSum()
        {
            // Act
            int cost = durabilitySystem.CalculateDurabilityCost(1, 5);

            // Assert
            Assert.AreEqual(6, cost, "Cost should be 1 + 5 = 6");
        }

        [Test]
        public void CalculateDurabilityCost_CustomBaseCost_ReturnsCorrectSum()
        {
            // Act
            int cost = durabilitySystem.CalculateDurabilityCost(2, 3);

            // Assert
            Assert.AreEqual(5, cost, "Cost should be 2 + 3 = 5");
        }

        #endregion

        #region Durability Damage Application

        [Test]
        public void ApplyDurabilityDamage_SufficientDurability_ReducesCorrectly()
        {
            // Act
            int remaining = durabilitySystem.ApplyDurabilityDamage(100, 10);

            // Assert
            Assert.AreEqual(90, remaining, "100 - 10 = 90");
        }

        [Test]
        public void ApplyDurabilityDamage_InsufficientDurability_ClampsToZero()
        {
            // Act
            int remaining = durabilitySystem.ApplyDurabilityDamage(5, 10);

            // Assert
            Assert.AreEqual(0, remaining, "Should clamp to 0");
        }

        [Test]
        public void ApplyDurabilityDamage_ExactlyDepletes_ReturnsZero()
        {
            // Act
            int remaining = durabilitySystem.ApplyDurabilityDamage(10, 10);

            // Assert
            Assert.AreEqual(0, remaining, "10 - 10 = 0");
        }

        [Test]
        public void ApplyDurabilityDamage_ZeroDamage_NoChange()
        {
            // Act
            int remaining = durabilitySystem.ApplyDurabilityDamage(100, 0);

            // Assert
            Assert.AreEqual(100, remaining, "No damage should be applied");
        }

        #endregion

        #region Broken Weapon Detection

        [Test]
        public void IsBroken_PositiveDurability_ReturnsFalse()
        {
            // Act
            bool isBroken = durabilitySystem.IsBroken(100);

            // Assert
            Assert.IsFalse(isBroken, "Weapon with 100 durability should not be broken");
        }

        [Test]
        public void IsBroken_OneDurability_ReturnsFalse()
        {
            // Act
            bool isBroken = durabilitySystem.IsBroken(1);

            // Assert
            Assert.IsFalse(isBroken, "Weapon with 1 durability should not be broken");
        }

        [Test]
        public void IsBroken_ZeroDurability_ReturnsTrue()
        {
            // Act
            bool isBroken = durabilitySystem.IsBroken(0);

            // Assert
            Assert.IsTrue(isBroken, "Weapon with 0 durability should be broken");
        }

        [Test]
        public void IsBroken_NegativeDurability_ReturnsTrue()
        {
            // Act
            bool isBroken = durabilitySystem.IsBroken(-10);

            // Assert
            Assert.IsTrue(isBroken, "Weapon with negative durability should be broken");
        }

        #endregion

        #region Repair Calculation

        [Test]
        public void CalculateRepair_PartialRepair_IncreasesDurability()
        {
            // Act
            int repaired = durabilitySystem.CalculateRepair(50, 100, 20);

            // Assert
            Assert.AreEqual(70, repaired, "50 + 20 = 70");
        }

        [Test]
        public void CalculateRepair_FullRepair_RestoresToMax()
        {
            // Act
            int repaired = durabilitySystem.CalculateRepair(50, 100, 100);

            // Assert
            Assert.AreEqual(100, repaired, "Should not exceed max durability");
        }

        [Test]
        public void CalculateRepair_ExcessRepair_ClampsToMax()
        {
            // Act
            int repaired = durabilitySystem.CalculateRepair(80, 100, 50);

            // Assert
            Assert.AreEqual(100, repaired, "Should clamp to max durability");
        }

        [Test]
        public void CalculateRepair_EmptyWeapon_RestoresSomeDurability()
        {
            // Act
            int repaired = durabilitySystem.CalculateRepair(0, 100, 30);

            // Assert
            Assert.AreEqual(30, repaired, "0 + 30 = 30");
        }

        [Test]
        public void CalculateRepair_ZeroRepairAmount_NoChange()
        {
            // Act
            int repaired = durabilitySystem.CalculateRepair(50, 100, 0);

            // Assert
            Assert.AreEqual(50, repaired, "No repair should be applied");
        }

        #endregion
    }
}

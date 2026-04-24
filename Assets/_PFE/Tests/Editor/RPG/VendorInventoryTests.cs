using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for VendorInventory system.
    /// Tests barter-based inventory scaling and price multipliers.
    /// </summary>
    public class VendorInventoryTests
    {
        private VendorInventory CreateTestVendor(CharacterStats stats)
        {
            var go = new GameObject("TestVendor");
            var vendor = go.AddComponent<VendorInventory>();
            vendor.SetPlayerStats(stats);
            return vendor;
        }

        private CharacterStats CreateTestCharacter()
        {
            var go = new GameObject("TestCharacter");
            var stats = go.AddComponent<CharacterStats>();

            var levelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            levelCurve.baseHp = 100;
            levelCurve.hpPerLevel = 15;
            levelCurve.organHpPerLevel = 40;
            levelCurve.baseOrganHp = 200;
            levelCurve.skillPointsPerLevel = 5;

            stats.Initialize(levelCurve);
            return stats;
        }

        [Test]
        [Description("Regular vendor inventory should scale with barter skill")]
        public void GetInventorySize_RegularVendor_ScalesWithBarter()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: false, randomVendor: false);

            // Act
            stats.SetSkillLevel("barter", 0);
            int size0 = vendor.GetInventorySize();

            stats.SetSkillLevel("barter", 5);
            int size5 = vendor.GetInventorySize();

            // Assert
            // Base: 10, multiplier: 6, so barter 5 = 10 + 6*5 = 40, plus random bonus (0-4)
            // Random multiplier: 0.5-1.2, so range is roughly 20-53 items
            Assert.Greater(size5, size0, "Higher barter should give larger inventory");
            Assert.GreaterOrEqual(size5, 15, "Barter 5 should give at least 15 items (worst case random)");
            Assert.LessOrEqual(size5, 60, "Barter 5 should give at most 60 items (best case random)");
        }

        [Test]
        [Description("Doctor vendor inventory should scale with barter skill")]
        public void GetInventorySize_DoctorVendor_ScalesWithBarter()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: true, randomVendor: false);

            // Act
            stats.SetSkillLevel("barter", 0);
            int size0 = vendor.GetInventorySize();

            stats.SetSkillLevel("barter", 5);
            int size5 = vendor.GetInventorySize();

            // Assert
            // Base: 5, multiplier: 3, so barter 5 = 5 + 3*5 = 20, plus random bonus (0-2)
            // Random multiplier: 0.5-1.2, so range is roughly 11-26 items
            Assert.Greater(size5, size0, "Higher barter should give larger inventory");
            Assert.GreaterOrEqual(size5, 10, "Barter 5 should give at least 10 items (worst case random)");
            Assert.LessOrEqual(size5, 30, "Barter 5 should give at most 30 items (best case random)");
        }

        [Test]
        [Description("Price multiplier should decrease with barter skill")]
        public void GetPriceMultiplier_HigherBarter_LowerPrices()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 0);
            float mult0 = vendor.GetPriceMultiplier();

            stats.SetSkillLevel("barter", 5);
            float mult5 = vendor.GetPriceMultiplier();

            stats.SetSkillLevel("barter", 10);
            float mult10 = vendor.GetPriceMultiplier();

            // Assert
            Assert.AreEqual(1.0f, mult0, 0.001f, "Barter 0 should have 1.0 multiplier (no discount)");
            Assert.AreEqual(0.85f, mult5, 0.001f, "Barter 5 should have 0.85 multiplier (15% discount)");
            Assert.AreEqual(0.70f, mult10, 0.001f, "Barter 10 should have 0.70 multiplier (30% discount)");
            Assert.Less(mult5, mult0, "Higher barter should give lower multiplier");
            Assert.Less(mult10, mult5, "Even higher barter should give even lower multiplier");
        }

        [Test]
        [Description("Price multiplier should cap at minimum")]
        public void GetPriceMultiplier_VeryHighBarter_CapsAtMinimum()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 50); // Very high barter
            float mult = vendor.GetPriceMultiplier();

            // Assert
            // Should cap at 0.3 (70% discount max)
            Assert.AreEqual(0.3f, mult, 0.001f, "Price multiplier should cap at 0.3");
        }

        [Test]
        [Description("Discount percentage should match barter skill")]
        public void GetDiscountPercentage_MatchesBarter()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 0);
            int disc0 = vendor.GetDiscountPercentage();

            stats.SetSkillLevel("barter", 5);
            int disc5 = vendor.GetDiscountPercentage();

            stats.SetSkillLevel("barter", 10);
            int disc10 = vendor.GetDiscountPercentage();

            // Assert
            Assert.AreEqual(0, disc0, "Barter 0 should have 0% discount");
            Assert.AreEqual(15, disc5, "Barter 5 should have 15% discount");
            Assert.AreEqual(30, disc10, "Barter 10 should have 30% discount");
        }

        [Test]
        [Description("Buy price should be affected by barter skill")]
        public void CalculateBuyPrice_HigherBarter_LowerPrice()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 0);
            int price0 = vendor.CalculateBuyPrice(1000);

            stats.SetSkillLevel("barter", 10);
            int price10 = vendor.CalculateBuyPrice(1000);

            // Assert
            Assert.AreEqual(1000, price0, "Barter 0 should pay full price");
            Assert.AreEqual(700, price10, "Barter 10 should pay 70% of price (30% discount)");
        }

        [Test]
        [Description("Sell price should be affected by barter skill")]
        public void CalculateSellPrice_HigherBarter_BetterPrice()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 0);
            int sell0 = vendor.CalculateSellPrice(1000);

            stats.SetSkillLevel("barter", 10);
            int sell10 = vendor.CalculateSellPrice(1000);

            // Assert
            // Base sell is 50% of price, modified by barter multiplier
            Assert.AreEqual(500, sell0, "Barter 0 should sell at 50% of price");
            Assert.AreEqual(350, sell10, "Barter 10 should sell at 35% of price (50% * 0.7)");
        }

        [Test]
        [Description("Inventory limit multiplier should scale with barter")]
        public void GetInventoryLimitMultiplier_ScalesWithBarter()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.SetSkillLevel("barter", 0);
            float mult0 = vendor.GetInventoryLimitMultiplier();

            stats.SetSkillLevel("barter", 5);
            float mult5 = vendor.GetInventoryLimitMultiplier();

            stats.SetSkillLevel("barter", 10);
            float mult10 = vendor.GetInventoryLimitMultiplier();

            // Assert
            // limitBuys = 1 + 0.2 * barterLevel
            Assert.AreEqual(1.0f, mult0, 0.001f, "Barter 0 should have 1.0x inventory limit");
            Assert.AreEqual(2.0f, mult5, 0.001f, "Barter 5 should have 2.0x inventory limit");
            Assert.AreEqual(3.0f, mult10, 0.001f, "Barter 10 should have 3.0x inventory limit");
        }

        [Test]
        [Description("Random vendor should have fixed inventory size")]
        public void GetInventorySize_RandomVendor_FixedSize()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: false, randomVendor: true);

            // Act
            stats.SetSkillLevel("barter", 0);
            int size0 = vendor.GetInventorySize();

            stats.SetSkillLevel("barter", 10);
            int size10 = vendor.GetInventorySize();

            // Assert
            // Random vendor has base 30 items regardless of barter
            // Random bonus: 0-4, then multiplier: 0.5-1.2
            // Min: (30 + 0) * 0.5 = 15
            // Max: (30 + 4) * 1.2 = 40.8 ≈ 41
            Assert.GreaterOrEqual(size0, 15, "Random vendor should have at least 15 items");
            Assert.LessOrEqual(size0, 41, "Random vendor should have at most 41 items");
            Assert.GreaterOrEqual(size10, 15, "Random vendor should have at least 15 items");
            Assert.LessOrEqual(size10, 41, "Random vendor should have at most 41 items");
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test objects
            var testCharacters = GameObject.FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
            foreach (var obj in testCharacters)
            {
                if (obj.gameObject.name.StartsWith("TestCharacter"))
                {
                    GameObject.DestroyImmediate(obj.gameObject);
                }
            }

            var vendors = GameObject.FindObjectsByType<VendorInventory>(FindObjectsSortMode.None);
            foreach (var obj in vendors)
            {
                if (obj.gameObject.name.StartsWith("TestVendor"))
                {
                    GameObject.DestroyImmediate(obj.gameObject);
                }
            }
        }
    }
}

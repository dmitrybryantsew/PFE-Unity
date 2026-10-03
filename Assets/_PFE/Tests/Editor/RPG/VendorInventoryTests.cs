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
        [Description("Doctor vendor inventory should scale with the barter LEVEL stat")]
        public void GetInventorySize_DoctorVendor_ScalesWithBarter()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: true, randomVendor: false);

            // Act
            stats.barterLvl = 0;
            int size0 = vendor.GetInventorySize();

            stats.barterLvl = 5;
            int size5 = vendor.GetInventorySize();

            // Assert
            // AS3: count = 5 + 3*5 = 20, then * (0.5 .. 1.2) => 10..24
            Assert.Greater(size5, size0, "Higher barterLvl should give larger inventory");
            Assert.GreaterOrEqual(size5, 10, "Barter 5 should give at least 10 items (worst-case roll)");
            Assert.LessOrEqual(size5, 24, "Barter 5 should give at most 24 items (best-case roll)");
        }

        [Test]
        [Description("Regular vendor inventory should scale with the barter LEVEL stat (not the raw skill)")]
        public void GetInventorySize_RegularVendor_ScalesWithBarterLevel()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: false, randomVendor: false);

            // Act — AS3 reads pers.barterLvl, a separate field from the "barter" skill.
            stats.barterLvl = 0;
            int size0 = vendor.GetInventorySize();

            stats.barterLvl = 5;
            int size5 = vendor.GetInventorySize();

            // Assert
            // AS3: count = 10 + 6*5 = 40, then * (0.5 .. 1.2) => 20..48
            Assert.Greater(size5, size0, "Higher barterLvl should give larger inventory");
            Assert.GreaterOrEqual(size5, 20, "Barter 5 should give at least 20 items (worst-case roll)");
            Assert.LessOrEqual(size5, 48, "Barter 5 should give at most 48 items (best-case roll)");
        }

        [Test]
        [Description("Inventory size must ignore the raw barter skill and read barterLvl only")]
        public void GetInventorySize_ReadsBarterLevel_NotTheRawSkill()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: false, randomVendor: false);

            // Raising the skill must NOT change the size once barterLvl is pinned.
            stats.barterLvl = 0;
            stats.SetSkillLevel("barter", 25);

            // Act
            int size = vendor.GetInventorySize();

            // Assert — AS3 count = 10 + 6*0 = 10, so the roll range is 5..12
            Assert.GreaterOrEqual(size, 5, "barterLvl 0 must yield the low base, regardless of skill");
            Assert.LessOrEqual(size, 12, "barterLvl 0 must yield the low base, regardless of skill");
        }

        [Test]
        [Description("Price multiplier must come from the barterMult/capsMult field, not a derived curve")]
        public void GetPriceMultiplier_ReadsTheCapsMultField()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act / Assert — default is 1 (no discount)
            Assert.AreEqual(1.0f, vendor.GetPriceMultiplier(), 0.001f, "Default capsMult is 1.0");

            // Someone (a perk) discounts prices by setting the field; the vendor must obey it.
            stats.capsMult = 0.6f;
            Assert.AreEqual(0.6f, vendor.GetPriceMultiplier(), 0.001f,
                "GetPriceMultiplier must read capsMult (the barterMult destination) directly");

            // And it must be genuinely independent of the barter skill / barterLvl.
            stats.SetSkillLevel("barter", 50);
            stats.barterLvl = 50;
            Assert.AreEqual(0.6f, vendor.GetPriceMultiplier(), 0.001f,
                "The price multiplier must not be re-derived from skill or barterLvl");
        }

        [Test]
        [Description("Discount percentage should follow capsMult")]
        public void GetDiscountPercentage_FollowsCapsMult()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.capsMult = 1.0f;
            int disc0 = vendor.GetDiscountPercentage();

            stats.capsMult = 0.85f;
            int disc15 = vendor.GetDiscountPercentage();

            stats.capsMult = 0.70f;
            int disc30 = vendor.GetDiscountPercentage();

            // Assert
            Assert.AreEqual(0, disc0, "capsMult 1.0 should be a 0% discount");
            Assert.AreEqual(15, disc15, "capsMult 0.85 should be a 15% discount");
            Assert.AreEqual(30, disc30, "capsMult 0.70 should be a 30% discount");
        }

        [Test]
        [Description("Buy price should follow capsMult")]
        public void CalculateBuyPrice_FollowsCapsMult()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.capsMult = 1.0f;
            int price0 = vendor.CalculateBuyPrice(1000);

            stats.capsMult = 0.70f;
            int price10 = vendor.CalculateBuyPrice(1000);

            // Assert
            Assert.AreEqual(1000, price0, "capsMult 1.0 should pay full price");
            Assert.AreEqual(700, price10, "capsMult 0.70 should pay 70% of price");
        }

        [Test]
        [Description("Sell price should follow capsMult")]
        public void CalculateSellPrice_FollowsCapsMult()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act
            stats.capsMult = 1.0f;
            int sell0 = vendor.CalculateSellPrice(1000);

            stats.capsMult = 0.70f;
            int sell10 = vendor.CalculateSellPrice(1000);

            // Assert — base sell is 50% of price, times capsMult
            Assert.AreEqual(500, sell0, "capsMult 1.0 should sell at 50% of price");
            Assert.AreEqual(350, sell10, "capsMult 0.70 should sell at 35% of price (50% * 0.7)");
        }

        [Test]
        [Description("Inventory limit multiplier must read the limitBuys field (default 1), not a derived curve")]
        public void GetInventoryLimitMultiplier_ReadsLimitBuys()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);

            // Act / Assert — AS3 Pers.limitBuys defaults to 1
            Assert.AreEqual(1.0f, vendor.GetInventoryLimitMultiplier(), 0.001f,
                "limitBuys must default to 1 (Pers.as:317)");

            // A perk raises it; the vendor must obey the field, not the barter skill.
            stats.limitBuys = 2.0f;
            stats.SetSkillLevel("barter", 10);
            stats.barterLvl = 10;
            Assert.AreEqual(2.0f, vendor.GetInventoryLimitMultiplier(), 0.001f,
                "GetInventoryLimitMultiplier must read limitBuys directly");
        }

        [Test]
        [Description("The limitBuys default must be 1, so restock caps are not zeroed")]
        public void LimitBuys_DefaultIsNotZero()
        {
            // This is the regression guard for a real bug: the field was declared 0f, which made
            // ceil(stock * 0) = 0 and silently stopped every vendor from restocking.
            var stats = CreateTestCharacter();
            Assert.AreEqual(1.0f, stats.limitBuys, 0.001f,
                "Default limitBuys must be 1 (AS3 Pers.as:317), never 0");
        }

        [Test]
        [Description("Random vendor should have a barter-independent size band")]
        public void GetInventorySize_RandomVendor_FixedSize()
        {
            // Arrange
            var stats = CreateTestCharacter();
            var vendor = CreateTestVendor(stats);
            vendor.SetVendorType(doctor: false, randomVendor: true);

            // Act
            stats.barterLvl = 0;
            int size0 = vendor.GetInventorySize();

            stats.barterLvl = 10;
            int size10 = vendor.GetInventorySize();

            // Assert — AS3: count = 30, then * (0.5 .. 1.2) => 15..36
            Assert.GreaterOrEqual(size0, 15, "Random vendor should have at least 15 items");
            Assert.LessOrEqual(size0, 36, "Random vendor should have at most 36 items");
            Assert.GreaterOrEqual(size10, 15, "Random vendor size must not depend on barterLvl");
            Assert.LessOrEqual(size10, 36, "Random vendor size must not depend on barterLvl");
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

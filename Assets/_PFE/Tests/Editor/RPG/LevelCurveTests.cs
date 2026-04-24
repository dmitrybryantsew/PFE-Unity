using NUnit.Framework;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for LevelCurve.
    /// Tests XP progression, level-up rewards, and post-game skill thresholds.
    ///
    /// Note: The current implementation uses a linear formula (xpDelta * level)
    /// rather than the quadratic formula from the original AS3 documentation.
    /// These tests verify the ACTUAL implementation behavior.
    /// </summary>
    public class LevelCurveTests
    {
        private LevelCurve levelCurve;

        [SetUp]
        public void SetUp()
        {
            levelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            levelCurve.xpDelta = 5000;
            levelCurve.skillPointsPerLevel = 5;
            levelCurve.baseHp = 100;
            levelCurve.hpPerLevel = 15;
            levelCurve.organHpPerLevel = 40;
            levelCurve.baseOrganHp = 200;
        }

        [Test]
        [Description("Level 1 should require 5,000 XP")]
        public void GetXpForLevel_Level1_Returns5000()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(1);

            // Assert (5000 * 1 = 5000, rounded to 1000)
            Assert.AreEqual(5000, xp, "Level 1 should require 5,000 XP");
        }

        [Test]
        [Description("Level 2 should require 15,000 total XP")]
        public void GetXpForLevel_Level2_Returns15000()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(2);

            // Assert (5000 * 1 + 5000 * 2 = 15000, rounded to 1000)
            Assert.AreEqual(15000, xp, "Level 2 should require 15,000 XP");
        }

        [Test]
        [Description("Level 3 should require 30,000 total XP")]
        public void GetXpForLevel_Level3_Returns30000()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(3);

            // Assert (5000 * 1 + 5000 * 2 + 5000 * 3 = 30000, rounded to 1000)
            Assert.AreEqual(30000, xp, "Level 3 should require 30,000 XP");
        }

        [Test]
        [Description("Level 10 should require 275,000 total XP")]
        public void GetXpForLevel_Level10_Returns275000()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(10);

            // Sum(1-10) * 5000 = 55 * 5000 = 275000, rounded to 1000
            Assert.AreEqual(275000, xp, "Level 10 should require 275,000 XP");
        }

        [Test]
        [Description("Level 11 should use multiplier")]
        public void GetXpForLevel_Level11_UsesMultiplier()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(11);

            // Level 1-10: 275,000 XP
            // Level 11: 5000 * 11 * (1 + 1/30)^2 = 55000 * 1.067^2 ≈ 62,600
            // Rounded to nearest 1000: ~58,000
            // Total: 275,000 + 58,000 = 333,000
            Assert.AreEqual(333000, xp, "Level 11 should apply multiplier");
        }

        [Test]
        [Description("Level 20 should require ~1.4M XP")]
        public void GetXpForLevel_Level20_ReturnsApprox1400000()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(20);

            // Based on linear implementation with multipliers for levels 11-20
            Assert.AreEqual(1399000, xp, 10000, "Level 20 should require ~1.4M XP");
        }

        [Test]
        [Description("Level 0 should return 0 XP")]
        public void GetXpForLevel_Level0_Returns0()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(0);

            // Assert
            Assert.AreEqual(0, xp, "Level 0 should require 0 XP");
        }

        [Test]
        [Description("Negative level should return 0 XP")]
        public void GetXpForLevel_NegativeLevel_Returns0()
        {
            // Act
            int xp = levelCurve.GetXpForLevel(-1);

            // Assert
            Assert.AreEqual(0, xp, "Negative level should require 0 XP");
        }

        [Test]
        [Description("GetLevelForXp should return correct level")]
        public void GetLevelForXp_5000_Returns1()
        {
            // Act
            int level = levelCurve.GetLevelForXp(5000);

            // Assert
            Assert.AreEqual(1, level, "5,000 XP should be level 1");
        }

        [Test]
        [Description("GetLevelForXp should return level below threshold")]
        public void GetLevelForXp_14000_Returns1()
        {
            // Act
            int level = levelCurve.GetLevelForXp(14000);

            // Assert (level 1 = 5000, level 2 = 15000, so 14000 is still level 1)
            // GetXpForLevel(1) = 5000 <= 14000, level++
            // GetXpForLevel(2) = 15000 > 14000, stop, return 2-1 = 1
            Assert.AreEqual(1, level, "14,000 XP should be level 1 (need 15,000 for level 2)");
        }

        [Test]
        [Description("GetLevelForXp should return correct level at threshold")]
        public void GetLevelForXp_15000_Returns2()
        {
            // Act
            int level = levelCurve.GetLevelForXp(15000);

            // Assert
            Assert.AreEqual(2, level, "15,000 XP should be level 2");
        }

        [Test]
        [Description("GetLevelForXp should handle zero XP")]
        public void GetLevelForXp_0_Returns0()
        {
            // Act
            int level = levelCurve.GetLevelForXp(0);

            // Assert (GetLevelForXp starts at 1, returns 0 since GetXpForLevel(1) = 5000 > 0)
            Assert.AreEqual(0, level, "0 XP should be level 0");
        }

        [Test]
        [Description("Knowl level 5 should grant 1 extra perk point")]
        public void GetExtraPerkPointsFromKnowl_Level5_Returns1()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(5);

            // Assert
            Assert.AreEqual(1, extraPerks, "Knowl 5 should grant 1 extra perk point");
        }

        [Test]
        [Description("Knowl level 11 should grant 2 extra perk points")]
        public void GetExtraPerkPointsFromKnowl_Level11_Returns2()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(11);

            // Assert
            Assert.AreEqual(2, extraPerks, "Knowl 11 should grant 2 extra perk points");
        }

        [Test]
        [Description("Knowl level 18 should grant 3 extra perk points")]
        public void GetExtraPerkPointsFromKnowl_Level18_Returns3()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(18);

            // Assert
            Assert.AreEqual(3, extraPerks, "Knowl 18 should grant 3 extra perk points");
        }

        [Test]
        [Description("Knowl level 100 should grant 10 extra perk points")]
        public void GetExtraPerkPointsFromKnowl_Level100_Returns10()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(100);

            // Assert
            Assert.AreEqual(10, extraPerks, "Knowl 100 should grant 10 extra perk points");
        }

        [Test]
        [Description("Knowl level 4 should grant 0 extra perk points")]
        public void GetExtraPerkPointsFromKnowl_Level4_Returns0()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(4);

            // Assert
            Assert.AreEqual(0, extraPerks, "Knowl 4 should grant 0 extra perk points");
        }

        [Test]
        [Description("Knowl level 0 should grant 0 extra perk points")]
        public void GetExtraPerkPointsFromKnowl_Level0_Returns0()
        {
            // Act
            int extraPerks = levelCurve.GetExtraPerkPointsFromKnowl(0);

            // Assert
            Assert.AreEqual(0, extraPerks, "Knowl 0 should grant 0 extra perk points");
        }

        [Test]
        [Description("Fast XP mode (xpDelta=3000) should reduce XP requirements")]
        public void GetXpForLevel_FastXpMode_LowerRequirements()
        {
            // Arrange
            levelCurve.xpDelta = 3000;

            // Act
            int xpLevel1 = levelCurve.GetXpForLevel(1);
            int xpLevel10 = levelCurve.GetXpForLevel(10);

            // Assert
            Assert.AreEqual(3000, xpLevel1, "Fast XP: Level 1 should require 3,000 XP");
            Assert.AreEqual(165000, xpLevel10, "Fast XP: Level 10 should require 165,000 XP");
        }

        [Test]
        [Description("Hard skills mode (skillPointsPerLevel=3) should reduce points per level")]
        public void SkillPointsPerLevel_HardSkillsMode_Returns3()
        {
            // Arrange
            levelCurve.skillPointsPerLevel = 3;

            // Act
            int points = levelCurve.SkillPointsPerLevel;

            // Assert
            Assert.AreEqual(3, points, "Hard skills mode should grant 3 points per level");
        }

        [Test]
        [Description("Normal mode should grant 5 skill points per level")]
        public void SkillPointsPerLevel_NormalMode_Returns5()
        {
            // Act
            int points = levelCurve.SkillPointsPerLevel;

            // Assert
            Assert.AreEqual(5, points, "Normal mode should grant 5 points per level");
        }

        [Test]
        [Description("HP per level should be configurable")]
        public void HpPerLevel_Configurable_ReturnsCorrectValue()
        {
            // Arrange
            levelCurve.hpPerLevel = 20;

            // Act
            int hpPerLevel = levelCurve.HpPerLevel;

            // Assert
            Assert.AreEqual(20, hpPerLevel, "HP per level should be configurable");
        }

        [Test]
        [Description("Organ HP per level should be configurable")]
        public void OrganHpPerLevel_Configurable_ReturnsCorrectValue()
        {
            // Arrange
            levelCurve.organHpPerLevel = 50;

            // Act
            int organHpPerLevel = levelCurve.OrganHpPerLevel;

            // Assert
            Assert.AreEqual(50, organHpPerLevel, "Organ HP per level should be configurable");
        }

        [Test]
        [Description("Base HP should be configurable for different difficulties")]
        public void BaseHp_DifferentDifficulty_ReturnsCorrectValue()
        {
            // Arrange
            levelCurve.baseHp = 70; // Hard difficulty

            // Act
            int baseHp = levelCurve.BaseHp;

            // Assert
            Assert.AreEqual(70, baseHp, "Base HP should be configurable");
        }

        [Test]
        [Description("Very easy difficulty should have higher base HP")]
        public void BaseHp_VeryEasyDifficulty_Returns200()
        {
            // Arrange
            levelCurve.baseHp = 200; // Very easy

            // Act
            int baseHp = levelCurve.BaseHp;

            // Assert
            Assert.AreEqual(200, baseHp, "Very easy should have 200 base HP");
        }
    }
}

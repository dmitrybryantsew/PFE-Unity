using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for CharacterStats.
    /// Tests skill/perk management, level-up system, and stat recalculation.
    /// </summary>
    public class CharacterStatsTests
    {
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
        [Description("Character should start at level 1")]
        public void Initialize_Level1_Returns1()
        {
            // Arrange & Act
            var stats = CreateTestCharacter();

            // Assert
            Assert.AreEqual(1, stats.Level, "Character should start at level 1");
        }

        [Test]
        [Description("Character should have base HP from level curve")]
        public void Initialize_BaseHp_Returns100()
        {
            // Arrange & Act
            var stats = CreateTestCharacter();

            // Assert
            Assert.AreEqual(100f, stats.MaxHp, "Character should have base HP of 100");
        }

        [Test]
        [Description("All skills should start at level 0")]
        public void Initialize_Skills_AllZero()
        {
            // Arrange & Act
            var stats = CreateTestCharacter();

            // Assert
            Assert.AreEqual(0, stats.GetSkillLevel("melee"), "Melee should start at 0");
            Assert.AreEqual(0, stats.GetSkillLevel("smallguns"), "Small guns should start at 0");
            Assert.AreEqual(0, stats.GetSkillLevel("science"), "Science should start at 0");
        }

        [Test]
        [Description("Adding XP should trigger level up")]
        public void AddXp_EnoughXp_LevelUp()
        {
            // Arrange
            var stats = CreateTestCharacter();

            // Act
            stats.AddXp(5000); // Enough for level 2

            // Assert
            Assert.AreEqual(2, stats.Level, "Character should be level 2");
        }

        [Test]
        [Description("Level up should grant skill points")]
        public void LevelUp_GrantsSkillPoints()
        {
            // Arrange
            var stats = CreateTestCharacter();
            int initialPoints = stats.SkillPoints;

            // Act
            stats.AddXp(5000); // Level up

            // Assert
            Assert.AreEqual(initialPoints + 5, stats.SkillPoints, "Should grant 5 skill points per level");
        }

        [Test]
        [Description("Level up should grant perk point")]
        public void LevelUp_GrantsPerkPoint()
        {
            // Arrange
            var stats = CreateTestCharacter();
            int initialPoints = stats.PerkPoints;

            // Act
            stats.AddXp(5000); // Level up

            // Assert
            Assert.AreEqual(initialPoints + 1, stats.PerkPoints, "Should grant 1 perk point per level");
        }

        [Test]
        [Description("Adding skill points should increase skill level")]
        public void AddSkillPoint_Success_IncreasesLevel()
        {
            // Arrange
            var stats = CreateTestCharacter();
            stats.GrantSkillPoints(5);

            // Act
            bool success = stats.AddSkillPoint("melee", 5);

            // Assert
            Assert.IsTrue(success, "Should successfully add skill points");
            Assert.AreEqual(5, stats.GetSkillLevel("melee"), "Melee should be level 5");
        }

        [Test]
        [Description("Should fail when not enough skill points")]
        public void AddSkillPoint_NotEnoughPoints_ReturnsFalse()
        {
            // Arrange
            var stats = CreateTestCharacter();

            // Act
            bool success = stats.AddSkillPoint("melee", 5);

            // Assert
            Assert.IsFalse(success, "Should fail without enough skill points");
            Assert.AreEqual(0, stats.GetSkillLevel("melee"), "Melee should remain at 0");
        }

        [Test]
        [Description("Should fail when trying to exceed skill cap")]
        public void AddSkillPoint_ExceedsCap_ReturnsFalse()
        {
            // Arrange
            var stats = CreateTestCharacter();
            stats.GrantSkillPoints(100);
            stats.SetSkillLevel("melee", 20); // Max for regular skills

            // Act
            bool success = stats.AddSkillPoint("melee", 1);

            // Assert
            Assert.IsFalse(success, "Should fail when at max level");
        }

        [Test]
        [Description("Skill tier calculation should match thresholds")]
        public void GetSkillTier_MatchesThresholds()
        {
            // Arrange
            var stats = CreateTestCharacter();

            // Act & Assert
            stats.SetSkillLevel("melee", 0);
            Assert.AreEqual(0, stats.GetSkillTier("melee"), "Level 0 should be tier 0");

            stats.SetSkillLevel("melee", 2);
            Assert.AreEqual(1, stats.GetSkillTier("melee"), "Level 2 should be tier 1");

            stats.SetSkillLevel("melee", 5);
            Assert.AreEqual(2, stats.GetSkillTier("melee"), "Level 5 should be tier 2");

            stats.SetSkillLevel("melee", 9);
            Assert.AreEqual(3, stats.GetSkillTier("melee"), "Level 9 should be tier 3");

            stats.SetSkillLevel("melee", 14);
            Assert.AreEqual(4, stats.GetSkillTier("melee"), "Level 14 should be tier 4");

            stats.SetSkillLevel("melee", 20);
            Assert.AreEqual(5, stats.GetSkillTier("melee"), "Level 20 should be tier 5");
        }

        [Test]
        [Description("Adding perk should increase rank")]
        public void AddPerk_Success_IncreasesRank()
        {
            // Arrange
            var stats = CreateTestCharacter();
            stats.GrantPerkPoints(1);
            stats.SetSkillLevel("melee", 1); // Meet oak perk requirement

            // Act
            bool success = stats.AddPerk("oak");

            // Assert
            Assert.IsTrue(success, "Should successfully add perk");
            Assert.AreEqual(1, stats.GetPerkRank("oak"), "Oak perk should be rank 1");
        }

        [Test]
        [Description("Should fail when not enough perk points")]
        public void AddPerk_NotEnoughPoints_ReturnsFalse()
        {
            // Arrange
            var stats = CreateTestCharacter();

            // Act
            bool success = stats.AddPerk("oak");

            // Assert
            Assert.IsFalse(success, "Should fail without perk points");
        }

        [Test]
        [Description("Should fail when perk prerequisites not met")]
        public void AddPerk_PrerequisitesNotMet_ReturnsFalse()
        {
            // Arrange
            var stats = CreateTestCharacter();
            stats.GrantPerkPoints(1);
            // Melee skill is 0, but oak requires melee 1

            // Act
            bool success = stats.AddPerk("oak");

            // Assert
            Assert.IsFalse(success, "Should fail when prerequisites not met");
        }

        [Test]
        [Description("Medic skill should increase max HP")]
        public void RecalculateStats_MedicSkill_IncreasesMaxHp()
        {
            // Arrange
            var stats = CreateTestCharacter();
            float baseHp = stats.MaxHp;

            // Act
            stats.SetSkillLevel("medic", 2); // Tier 1: +20 HP

            // Assert
            Assert.AreEqual(baseHp + 20f, stats.MaxHp, "Medic tier 1 should add 20 HP");
        }

        [Test]
        [Description("Attack skill should increase damage multiplier")]
        public void RecalculateStats_AttackSkill_IncreasesDamage()
        {
            // Arrange
            var stats = CreateTestCharacter();
            float baseDamage = stats.AllDamMult;

            // Act
            stats.SetSkillLevel("attack", 10); // +5% per level = +50%

            // Assert
            Assert.AreEqual(baseDamage + 0.5f, stats.AllDamMult, 0.001f, "Attack 10 should add 50% damage");
        }

        [Test]
        [Description("Survival skill should increase skin resistance")]
        public void RecalculateStats_SurvivalSkill_IncreasesSkin()
        {
            // Arrange
            var stats = CreateTestCharacter();

            // Act
            stats.SetSkillLevel("survival", 5); // +1 per level

            // Assert
            Assert.AreEqual(5f, stats.skin, 0.001f, "Survival 5 should give 5 skin");
        }

        [Test]
        [Description("Level up should increase max HP")]
        public void LevelUp_IncreasesMaxHp()
        {
            // Arrange
            var stats = CreateTestCharacter();
            float baseHp = stats.MaxHp;

            // Act
            stats.AddXp(5000); // Level up

            // Assert
            Assert.AreEqual(baseHp + 15f, stats.MaxHp, "Level up should add 15 HP");
        }

        [Test]
        [Description("Knowl skill should grant extra perk points at thresholds")]
        public void SetSkillLevel_KnowlSkill_GrantsExtraPerks()
        {
            // Arrange
            var stats = CreateTestCharacter();
            int basePerks = stats.PerkPoints;

            // Act
            stats.SetSkillLevel("knowl", 5); // First threshold

            // Assert
            Assert.AreEqual(basePerks + 1, stats.PerkPoints, "Knowl 5 should grant 1 extra perk point");
        }

        [Test]
        [Description("Factor tracking should record stat sources")]
        public void RecalculateStats_TracksFactors()
        {
            // Arrange
            var stats = CreateTestCharacter();
            float initialHp = stats.MaxHp;

            // Act
            stats.SetSkillLevel("medic", 2); // Adds HP
            float finalHp = stats.MaxHp;
            var factors = stats.GetFactorsForStat("maxhp");

            // Assert
            Assert.Greater(finalHp, initialHp, "Medic skill should increase max HP");
            Assert.IsNotEmpty(factors, $"Should have factors for maxhp (count: {factors.Count}, HP: {initialHp} -> {finalHp})");
            Assert.IsTrue(System.Array.Exists(factors.ToArray(), f => f.sourceId == "medic"),
                "Should track medic as source");
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test objects
            var testObjects = GameObject.FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
            foreach (var obj in testObjects)
            {
                if (obj.gameObject.name.StartsWith("TestCharacter"))
                {
                    GameObject.DestroyImmediate(obj.gameObject);
                }
            }
        }
    }
}

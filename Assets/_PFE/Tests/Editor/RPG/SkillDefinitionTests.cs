using NUnit.Framework;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for SkillDefinition.
    /// Tests skill tier calculations, post-game skill thresholds, and modifier values.
    /// </summary>
    public class SkillDefinitionTests
    {
        private SkillDefinition CreateTestSkill()
        {
            var skill = ScriptableObject.CreateInstance<SkillDefinition>();
            skill.skillId = "testskill";
            skill.displayName = "Test Skill";
            skill.description = "A skill for testing";
            skill.isPostGame = false;
            skill.maxLevel = 20;
            skill.sortOrder = 0;
            return skill;
        }

        [Test]
        [Description("Skill tier should be 0 for level below 2")]
        public void SkillTier_Level0_Returns0()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier = skill.GetSkillTier(0);
            int tier1 = skill.GetSkillTier(1);

            // Assert
            Assert.AreEqual(0, tier, "Level 0 should return tier 0");
            Assert.AreEqual(0, tier1, "Level 1 should return tier 0");
        }

        [Test]
        [Description("Skill tier should be 1 for levels 2-4")]
        public void SkillTier_Level2_Returns1()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier2 = skill.GetSkillTier(2);
            int tier4 = skill.GetSkillTier(4);

            // Assert
            Assert.AreEqual(1, tier2, "Level 2 should return tier 1");
            Assert.AreEqual(1, tier4, "Level 4 should return tier 1");
        }

        [Test]
        [Description("Skill tier should be 2 for levels 5-8")]
        public void SkillTier_Level5_Returns2()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier5 = skill.GetSkillTier(5);
            int tier8 = skill.GetSkillTier(8);

            // Assert
            Assert.AreEqual(2, tier5, "Level 5 should return tier 2");
            Assert.AreEqual(2, tier8, "Level 8 should return tier 2");
        }

        [Test]
        [Description("Skill tier should be 3 for levels 9-13")]
        public void SkillTier_Level9_Returns3()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier9 = skill.GetSkillTier(9);
            int tier13 = skill.GetSkillTier(13);

            // Assert
            Assert.AreEqual(3, tier9, "Level 9 should return tier 3");
            Assert.AreEqual(3, tier13, "Level 13 should return tier 3");
        }

        [Test]
        [Description("Skill tier should be 4 for levels 14-19")]
        public void SkillTier_Level14_Returns4()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier14 = skill.GetSkillTier(14);
            int tier19 = skill.GetSkillTier(19);

            // Assert
            Assert.AreEqual(4, tier14, "Level 14 should return tier 4");
            Assert.AreEqual(4, tier19, "Level 19 should return tier 4");
        }

        [Test]
        [Description("Skill tier should be 5 for level 20")]
        public void SkillTier_Level20_Returns5()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier20 = skill.GetSkillTier(20);

            // Assert
            Assert.AreEqual(5, tier20, "Level 20 should return tier 5");
        }

        [Test]
        [Description("Post-skill tier should be 0 for levels below 5")]
        public void PostSkillTier_Level0_Returns0()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier = skill.GetPostSkillTier(0);
            int tier4 = skill.GetPostSkillTier(4);

            // Assert
            Assert.AreEqual(0, tier, "Level 0 should return post-tier 0");
            Assert.AreEqual(0, tier4, "Level 4 should return post-tier 0");
        }

        [Test]
        [Description("Post-skill tier should be 1 for levels 5-10")]
        public void PostSkillTier_Level5_Returns1()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier5 = skill.GetPostSkillTier(5);
            int tier10 = skill.GetPostSkillTier(10);

            // Assert
            Assert.AreEqual(1, tier5, "Level 5 should return post-tier 1");
            Assert.AreEqual(1, tier10, "Level 10 should return post-tier 1");
        }

        [Test]
        [Description("Post-skill tier should match all thresholds")]
        public void PostSkillTier_AllThresholds_MatchDocumentation()
        {
            // Arrange
            var skill = CreateTestSkill();
            int[] thresholds = { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };

            // Act & Assert
            for (int i = 0; i < thresholds.Length; i++)
            {
                int tier = skill.GetPostSkillTier(thresholds[i]);
                Assert.AreEqual(i + 1, tier, $"Level {thresholds[i]} should return post-tier {i + 1}");
            }
        }

        [Test]
        [Description("Post-skill tier should be 10 for max level")]
        public void PostSkillTier_Level100_Returns10()
        {
            // Arrange
            var skill = CreateTestSkill();

            // Act
            int tier = skill.GetPostSkillTier(100);

            // Assert
            Assert.AreEqual(10, tier, "Level 100 should return post-tier 10");
        }

        #region StatModifier Tests

        [Test]
        [Description("StatModifier GetValueForLevel returns correct value from values array")]
        public void StatModifier_GetValueForLevel_WithValuesArray_ReturnsCorrectValue()
        {
            // Arrange
            var modifier = new StatModifier
            {
                statId = "testStat",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = new float[] { 10f, 20f, 30f, 40f, 50f, 60f },
                valueDelta = 0f
            };

            // Act & Assert
            Assert.AreEqual(10f, modifier.GetValueForLevel(0), "Level 0 should return 10");
            Assert.AreEqual(20f, modifier.GetValueForLevel(1), "Level 1 should return 20");
            Assert.AreEqual(30f, modifier.GetValueForLevel(2), "Level 2 should return 30");
            Assert.AreEqual(40f, modifier.GetValueForLevel(3), "Level 3 should return 40");
            Assert.AreEqual(50f, modifier.GetValueForLevel(4), "Level 4 should return 50");
            Assert.AreEqual(60f, modifier.GetValueForLevel(5), "Level 5 should return 60");
        }

        [Test]
        [Description("StatModifier GetValueForLevel with valueDelta calculates linear progression")]
        public void StatModifier_GetValueForLevel_WithValueDelta_CalculatesLinearProgression()
        {
            // Arrange
            var modifier = new StatModifier
            {
                statId = "meleeR",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = new float[] { 100f }, // Base value
                valueDelta = 30f // +30 per level
            };

            // Act & Assert
            // From docs: meleeR skill has v0='100' vd='30'
            Assert.AreEqual(100f, modifier.GetValueForLevel(0), "Level 0 should return 100");
            Assert.AreEqual(130f, modifier.GetValueForLevel(1), "Level 1 should return 130");
            Assert.AreEqual(160f, modifier.GetValueForLevel(2), "Level 2 should return 160");
            Assert.AreEqual(190f, modifier.GetValueForLevel(3), "Level 3 should return 190");
            Assert.AreEqual(220f, modifier.GetValueForLevel(4), "Level 4 should return 220");
            Assert.AreEqual(250f, modifier.GetValueForLevel(5), "Level 5 should return 250");
        }

        [Test]
        [Description("StatModifier GetValueForLevel with single value returns that value for all levels")]
        public void StatModifier_GetValueForLevel_WithSingleValue_ReturnsSameValue()
        {
            // Arrange
            var modifier = new StatModifier
            {
                statId = "maxhp",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = new float[] { 20f },
                valueDelta = 0f
            };

            // Act & Assert
            Assert.AreEqual(20f, modifier.GetValueForLevel(0), "Level 0 should return 20");
            Assert.AreEqual(20f, modifier.GetValueForLevel(1), "Level 1 should return 20");
            Assert.AreEqual(20f, modifier.GetValueForLevel(5), "Level 5 should return 20");
        }

        [Test]
        [Description("StatModifier GetValueForLevel with out of bounds index returns first value")]
        public void StatModifier_GetValueForLevel_LevelOutOfBounds_ReturnsFirstValue()
        {
            // Arrange
            var modifier = new StatModifier
            {
                statId = "testStat",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = new float[] { 10f, 20f, 30f },
                valueDelta = 0f
            };

            // Act
            float result = modifier.GetValueForLevel(10); // Index 10 doesn't exist

            // Assert - Should fall back to first value when index is out of bounds
            // Based on implementation: if (values != null && values.Length > level) return values[level];
            // If level > values.Length, it falls through to valueDelta check
            Assert.AreEqual(10f, result, "Out of bounds level should return first value");
        }

        [Test]
        [Description("StatModifier GetValueForLevel with empty values returns 0")]
        public void StatModifier_GetValueForLevel_EmptyValues_ReturnsZero()
        {
            // Arrange
            var modifier = new StatModifier
            {
                statId = "testStat",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = null,
                valueDelta = 0f
            };

            // Act
            float result = modifier.GetValueForLevel(5);

            // Assert
            Assert.AreEqual(0f, result, "Null values should return 0");
        }

        [Test]
        [Description("StatModifier GetValueForLevel combines valueDelta with base value correctly")]
        public void StatModifier_GetValueForLevel_CombinedValueDelta_WorksCorrectly()
        {
            // Arrange - Test with negative delta (reduction)
            var modifier = new StatModifier
            {
                statId = "barterMult",
                type = ModifierType.Multiply,
                target = ModifierTarget.Player,
                values = new float[] { 1.0f },
                valueDelta = -0.03f // -3% per level
            };

            // Act & Assert
            // From docs: barter skill barterMult: -3% prices per level
            Assert.AreEqual(1.0f, modifier.GetValueForLevel(0), "Level 0 should return 1.0");
            Assert.AreEqual(0.97f, modifier.GetValueForLevel(1), 0.001f, "Level 1 should return 0.97");
            Assert.AreEqual(0.94f, modifier.GetValueForLevel(2), 0.001f, "Level 2 should return 0.94");
            Assert.AreEqual(0.85f, modifier.GetValueForLevel(5), 0.001f, "Level 5 should return 0.85");
        }

        [Test]
        [Description("StatModifier supports different modifier types")]
        public void StatModifier_Type_AffectsCalculation()
        {
            // Arrange - Test all modifier types
            var addModifier = new StatModifier
            {
                statId = "maxhp",
                type = ModifierType.Add,
                target = ModifierTarget.Player,
                values = new float[] { 20f },
                valueDelta = 0f
            };

            var multiplyModifier = new StatModifier
            {
                statId = "allVulnerMult",
                type = ModifierType.Multiply,
                target = ModifierTarget.Player,
                values = new float[] { 0.97f },
                valueDelta = 0f
            };

            var setModifier = new StatModifier
            {
                statId = "isDJ",
                type = ModifierType.Set,
                target = ModifierTarget.Player,
                values = new float[] { 1f },
                valueDelta = 0f
            };

            var weaponSkillModifier = new StatModifier
            {
                statId = "weaponSkill",
                type = ModifierType.WeaponSkill,
                target = ModifierTarget.Player,
                values = new float[] { 0.05f },
                valueDelta = 0f
            };

            // Act & Assert
            // These tests verify that the modifier types are stored correctly
            // Actual application of modifiers is tested in CharacterStats tests
            Assert.AreEqual(ModifierType.Add, addModifier.type);
            Assert.AreEqual(ModifierType.Multiply, multiplyModifier.type);
            Assert.AreEqual(ModifierType.Set, setModifier.type);
            Assert.AreEqual(ModifierType.WeaponSkill, weaponSkillModifier.type);

            // Verify GetValueForLevel works for all types
            Assert.AreEqual(20f, addModifier.GetValueForLevel(0));
            Assert.AreEqual(0.97f, multiplyModifier.GetValueForLevel(0));
            Assert.AreEqual(1f, setModifier.GetValueForLevel(0));
            Assert.AreEqual(0.05f, weaponSkillModifier.GetValueForLevel(0));
        }

        #endregion
    }
}

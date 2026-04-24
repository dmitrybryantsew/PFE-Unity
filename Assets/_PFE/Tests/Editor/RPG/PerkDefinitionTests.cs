using NUnit.Framework;
using PFE.Systems.RPG.Data;
using UnityEngine;
using System.Collections.Generic;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for PerkDefinition.
    /// Tests perk prerequisites, multi-rank progression, and requirement checking.
    /// </summary>
    public class PerkDefinitionTests
    {
        private class MockCharacterStats : ICharacterStats
        {
            public int Level { get; set; }
            private Dictionary<string, int> skills = new Dictionary<string, int>();
            private Dictionary<string, int> perks = new Dictionary<string, int>();

            public MockCharacterStats()
            {
                Level = 1;
            }

            public void SetSkillLevel(string skillId, int level)
            {
                skills[skillId] = level;
            }

            public void SetPerkRank(string perkId, int rank)
            {
                perks[perkId] = rank;
            }

            public int GetSkillLevel(string skillId)
            {
                return skills.TryGetValue(skillId, out int level) ? level : 0;
            }

            public int GetPerkRank(string perkId)
            {
                return perks.TryGetValue(perkId, out int rank) ? rank : 0;
            }
        }

        [Test]
        [Description("PerkRequirement should check level requirement")]
        public void PerkRequirement_LevelRequirement_Met()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Level,
                level = 5,
                levelDelta = 0
            };

            var stats = new MockCharacterStats { Level = 5 };

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Level requirement should be met");
        }

        [Test]
        [Description("PerkRequirement should fail when level too low")]
        public void PerkRequirement_LevelRequirement_NotMet()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Level,
                level = 5,
                levelDelta = 0
            };

            var stats = new MockCharacterStats { Level = 3 };

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsFalse(met, "Level requirement should not be met");
        }

        [Test]
        [Description("PerkRequirement should check skill requirement")]
        public void PerkRequirement_SkillRequirement_Met()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Skill,
                skillId = "melee",
                level = 3,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("melee", 3);

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Skill requirement should be met");
        }

        [Test]
        [Description("PerkRequirement should accept guns requirement with smallguns")]
        public void PerkRequirement_GunsRequirement_SmallGuns()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Guns,
                level = 2,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("smallguns", 2);

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Guns requirement should accept smallguns");
        }

        [Test]
        [Description("PerkRequirement should accept guns requirement with energy")]
        public void PerkRequirement_GunsRequirement_Energy()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Guns,
                level = 2,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("energy", 2);

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Guns requirement should accept energy");
        }

        [Test]
        [Description("PerkRequirement should scale with perk rank")]
        public void PerkRequirement_LevelDelta_ScalesWithRank()
        {
            // Arrange
            var req = new PerkRequirement
            {
                type = RequirementType.Skill,
                skillId = "tele",
                level = 2,
                levelDelta = 3 // +3 per rank
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("tele", 5);

            // Act & Assert
            bool rank1 = req.IsMet(stats, 1); // Needs 2 + 0*3 = 2
            bool rank2 = req.IsMet(stats, 2); // Needs 2 + 1*3 = 5

            Assert.IsTrue(rank1, "Rank 1 should require Tele 2");
            Assert.IsTrue(rank2, "Rank 2 should require Tele 5");
        }
    }
}

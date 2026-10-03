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

            public int GetSkillTier(string skillId)
            {
                int lvl = GetSkillLevel(skillId);
                if (lvl >= 20) return 5;
                if (lvl >= 14) return 4;
                if (lvl >= 9) return 3;
                if (lvl >= 5) return 2;
                if (lvl >= 2) return 1;
                return 0;
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
            // `level` on a Skill requirement is a TIER, not a raw point count. AS3 compares
            // `getSkLevel(skills[id])` -- a 0..5 tier -- against reqlevel (Pers.as:1452-1458), and
            // every skill/guns `lvl` in AllData.as is 1..5 (max 5), while the `id='level'` rows run
            // to 20. Raw 9 is tier 3 (thresholds 2/5/9/14/20, Pers.as:995-1020).
            var req = new PerkRequirement
            {
                type = RequirementType.Skill,
                skillId = "melee",
                level = 3,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();

            // Boundary, both sides. The old fixture asserted only the accept case, with raw points
            // equal to the tier, which passed under the pre-oracle "compare raw levels" rule and hid
            // the unit mismatch entirely.
            stats.SetSkillLevel("melee", 9);   // tier 3
            Assert.IsTrue(req.IsMet(stats, 1), "tier 3 should satisfy a tier-3 requirement");

            stats.SetSkillLevel("melee", 8);   // tier 2
            Assert.IsFalse(req.IsMet(stats, 1), "tier 2 must NOT satisfy a tier-3 requirement");

            stats.SetSkillLevel("melee", 20);  // tier 5
            Assert.IsTrue(req.IsMet(stats, 1), "tier 5 should satisfy a tier-3 requirement");
        }

        [Test]
        [Description("PerkRequirement should accept guns requirement with smallguns")]
        public void PerkRequirement_GunsRequirement_SmallGuns()
        {
            // `level = 2` is tier 2, i.e. raw 5..8.
            var req = new PerkRequirement
            {
                type = RequirementType.Guns,
                level = 2,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("smallguns", 5);   // tier 2

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Guns requirement should accept smallguns");
        }

        [Test]
        [Description("PerkRequirement should accept guns requirement with energy")]
        public void PerkRequirement_GunsRequirement_Energy()
        {
            // Same tier-2 gate, reached through the OTHER arm of the OR: energy must qualify on its
            // own, with smallguns left at 0.
            var req = new PerkRequirement
            {
                type = RequirementType.Guns,
                level = 2,
                levelDelta = 0
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("energy", 5);   // tier 2

            // Act
            bool met = req.IsMet(stats, 1);

            // Assert
            Assert.IsTrue(met, "Guns requirement should accept energy");
        }

        [Test]
        [Description("PerkRequirement should scale with perk rank")]
        public void PerkRequirement_LevelDelta_ScalesWithRank()
        {
            // `level` is a tier, so the delta moves the gate through the tier ladder:
            //   rank 1 -> 2 + 0*3 = tier 2   (raw 5..8)
            //   rank 2 -> 2 + 1*3 = tier 5   (raw 20+)
            var req = new PerkRequirement
            {
                type = RequirementType.Skill,
                skillId = "tele",
                level = 2,
                levelDelta = 3 // +3 tiers per rank
            };

            var stats = new MockCharacterStats();
            stats.SetSkillLevel("tele", 9);   // tier 3

            // Rank 1 passes and rank 2 fails at the SAME stat value -- which is the only way to show
            // the delta actually moved the gate. The old fixture asserted both true from raw 5, which
            // cannot distinguish "the delta applied" from "it was ignored".
            Assert.IsTrue(req.IsMet(stats, 1), "Rank 1 should require tier 2 -- tier 3 satisfies it");
            Assert.IsFalse(req.IsMet(stats, 2), "Rank 2 should require tier 5 -- tier 3 must NOT satisfy it");

            stats.SetSkillLevel("tele", 20);  // tier 5
            Assert.IsTrue(req.IsMet(stats, 2), "Rank 2 should be satisfied at tier 5");
        }
    }
}

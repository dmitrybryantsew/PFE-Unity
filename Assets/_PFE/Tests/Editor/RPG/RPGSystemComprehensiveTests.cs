using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;
using System;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// Comprehensive TDD Tests for RPG Stat System
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    ///
    /// These tests ensure complete coverage of:
    /// 1. All 18 skills (13 regular + 3 post-game + 2 special)
    /// 2. All skill tier thresholds (2, 5, 9, 14, 20)
    /// 3. All post-game skill thresholds for knowl (5, 11, 18, 26, 35, 45, 56, 68, 82, 100)
    /// 4. XP curve calculations for all levels 1-20+
    /// 5. Perk prerequisites and rank progression
    /// 6. Stat modifications from skills and perks
    /// 7. Save/load system integrity
    /// 8. Factor tracking for UI
    /// </summary>
    [TestFixture]
    public class RPGSystemComprehensiveTests
    {
        private LevelCurve testLevelCurve;
        private CharacterStats characterStats;

        [SetUp]
        public void Setup()
        {
            // Create test level curve
            testLevelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            testLevelCurve.xpDelta = 5000;
            testLevelCurve.skillPointsPerLevel = 5;
            testLevelCurve.baseHp = 100;
            testLevelCurve.hpPerLevel = 15;
            testLevelCurve.organHpPerLevel = 40;
            testLevelCurve.baseOrganHp = 200;

            // Create test character stats
            var gameObject = new GameObject("TestCharacter");
            characterStats = gameObject.AddComponent<CharacterStats>();
            characterStats.Initialize(testLevelCurve);
        }

        [TearDown]
        public void TearDown()
        {
            if (testLevelCurve != null) UnityEngine.Object.DestroyImmediate(testLevelCurve);
            if (characterStats != null) UnityEngine.Object.DestroyImmediate(characterStats.gameObject);
        }

        #region XP Curve Comprehensive Tests

        [Test]
        [Description("XP_Levels1Through10_CalculationsMatchAS3")]
        public void GetXpForLevel_Levels1To10_MatchAS3Formula()
        {
            // From AS3 documentation (xpDelta = 5000):
            // Level 1: 5,000
            // Level 2: 15,000
            // Level 3: 30,000
            // Level 4: 50,000
            // Level 5: 75,000
            // Level 6: 105,000
            // Level 7: 140,000
            // Level 8: 180,000
            // Level 9: 225,000
            // Level 10: 275,000

            int[] expectedXp = { 5000, 15000, 30000, 50000, 75000, 105000, 140000, 180000, 225000, 275000 };

            for (int level = 1; level <= 10; level++)
            {
                int actualXp = testLevelCurve.GetXpForLevel(level);
                Assert.AreEqual(expectedXp[level - 1], actualXp, 1000,
                    $"Level {level} XP should be approximately {expectedXp[level - 1]}, got {actualXp}");
            }
        }

        [Test]
        [Description("XP_Levels11Through20_CalculationsWithMultiplier")]
        public void GetXpForLevel_Levels11To20_ApplyMultiplierCorrectly()
        {
            // AS3 Pers.xpProgress (Pers.as:1139-1147) is ONE closed form, not a sum of marginal levels:
            //     mult = level > 10 ? (level - 10) / 30 + 1 : 1
            //     xp   = round(xpDelta * level * (level + 1) / 2 * mult * mult / 1000) * 1000
            // The old expectations (333k/401k/672k/1.4M) came from summing the marginal term and
            // applying the multiplier only to it -- correct for level <= 10, wrong above. The oracle
            // squares the multiplier over the whole sum, so the gap compounds.
            Assert.AreEqual(352000, testLevelCurve.GetXpForLevel(11), 2000, "Level 11 XP should be ~352k");
            Assert.AreEqual(444000, testLevelCurve.GetXpForLevel(12), 2000, "Level 12 XP should be ~444k");
            Assert.AreEqual(817000, testLevelCurve.GetXpForLevel(15), 5000, "Level 15 XP should be ~817k");
            Assert.AreEqual(1867000, testLevelCurve.GetXpForLevel(20), 5000, "Level 20 XP should be ~1.87M");
        }

        [Test]
        [Description("XP_Levels21Through30_CurveContinuesCorrectly")]
        public void GetXpForLevel_Levels21To30_CurveContinues()
        {
            // Ensure curve doesn't break at higher levels
            int xp20 = testLevelCurve.GetXpForLevel(20);
            int xp25 = testLevelCurve.GetXpForLevel(25);
            int xp30 = testLevelCurve.GetXpForLevel(30);

            Assert.IsTrue(xp25 > xp20, "Level 25 XP should be greater than Level 20");
            Assert.IsTrue(xp30 > xp25, "Level 30 XP should be greater than Level 25");

            // Verify continuous growth (not necessarily exponential)
            float ratio25to20 = (float)xp25 / xp20;
            float ratio30to25 = (float)xp30 / xp25;

            Assert.IsTrue(ratio25to20 > 1.0f, "Growth should be positive");
            Assert.IsTrue(ratio30to25 > 1.0f, "Growth should be positive");
            Assert.IsTrue(ratio25to20 < 2.0f, "Growth should not be too steep");
            Assert.IsTrue(ratio30to25 < 2.0f, "Growth should not be too steep");
        }

        [Test]
        [Description("XP_Level0AndNegative_ReturnZero")]
        public void GetXpForLevel_Level0AndNegative_ReturnZero()
        {
            Assert.AreEqual(0, testLevelCurve.GetXpForLevel(0), "Level 0 should return 0 XP");
            Assert.AreEqual(0, testLevelCurve.GetXpForLevel(-1), "Negative levels should return 0 XP");
            Assert.AreEqual(0, testLevelCurve.GetXpForLevel(-100), "Large negative levels should return 0 XP");
        }

        [Test]
        [Description("XP_Level100AndAbove_CurveDoesNotBreak")]
        public void GetXpForLevel_Level100AndAbove_ValidCalculations()
        {
            // Test max level (100)
            int xp100 = testLevelCurve.GetXpForLevel(100);
            Assert.IsTrue(xp100 > 0, "Level 100 should have valid XP");
            Assert.IsTrue(xp100 < 500000000, "Level 100 XP should be reasonable (not overflow)");

            // Test beyond max level (should still calculate)
            int xp150 = testLevelCurve.GetXpForLevel(150);
            Assert.IsTrue(xp150 > xp100, "Level 150 XP should be greater than Level 100");
        }

        [Test]
        [Description("XP_GetLevelForXp_ConvertBothWays")]
        public void GetLevelForXp_RoundTripsWithGetXpForLevel()
        {
            // Test round-trip conversions
            for (int level = 1; level <= 20; level++)
            {
                int xpRequired = testLevelCurve.GetXpForLevel(level);
                int calculatedLevel = testLevelCurve.GetLevelForXp(xpRequired);

                Assert.AreEqual(level, calculatedLevel,
                    $"XP for level {level} should convert back to level {level}, got {calculatedLevel}");
            }
        }

        [Test]
        [Description("XP_GetLevelForXp_MidRangeLevels")]
        public void GetLevelForXp_MidRangeXp_ReturnsCorrectLevel()
        {
            // Test XP values in between levels
            int xpForLevel5 = testLevelCurve.GetXpForLevel(5);
            int xpForLevel6 = testLevelCurve.GetXpForLevel(6);
            int midXp = (xpForLevel5 + xpForLevel6) / 2;

            int level = testLevelCurve.GetLevelForXp(midXp);
            Assert.AreEqual(5, level, "Mid-range XP should return current level, not next");
        }

        #endregion

        #region Skill Tier Comprehensive Tests

        [Test]
        [Description("SkillTier_AllThresholdBoundaries")]
        public void CalculateSkillTier_TestAllThresholdBoundaries()
        {
            // Test exact thresholds and values around them
            // Thresholds: 0, 2, 5, 9, 14, 20

            // Tier 0: [0, 2)
            Assert.AreEqual(0, characterStats.CalculateSkillTier(0), "Level 0 should be tier 0");
            Assert.AreEqual(0, characterStats.CalculateSkillTier(1), "Level 1 should be tier 0");

            // Tier 1: [2, 5)
            Assert.AreEqual(1, characterStats.CalculateSkillTier(2), "Level 2 should be tier 1");
            Assert.AreEqual(1, characterStats.CalculateSkillTier(3), "Level 3 should be tier 1");
            Assert.AreEqual(1, characterStats.CalculateSkillTier(4), "Level 4 should be tier 1");

            // Tier 2: [5, 9)
            Assert.AreEqual(2, characterStats.CalculateSkillTier(5), "Level 5 should be tier 2");
            Assert.AreEqual(2, characterStats.CalculateSkillTier(6), "Level 6 should be tier 2");
            Assert.AreEqual(2, characterStats.CalculateSkillTier(7), "Level 7 should be tier 2");
            Assert.AreEqual(2, characterStats.CalculateSkillTier(8), "Level 8 should be tier 2");

            // Tier 3: [9, 14)
            Assert.AreEqual(3, characterStats.CalculateSkillTier(9), "Level 9 should be tier 3");
            Assert.AreEqual(3, characterStats.CalculateSkillTier(10), "Level 10 should be tier 3");
            Assert.AreEqual(3, characterStats.CalculateSkillTier(13), "Level 13 should be tier 3");

            // Tier 4: [14, 20)
            Assert.AreEqual(4, characterStats.CalculateSkillTier(14), "Level 14 should be tier 4");
            Assert.AreEqual(4, characterStats.CalculateSkillTier(15), "Level 15 should be tier 4");
            Assert.AreEqual(4, characterStats.CalculateSkillTier(19), "Level 19 should be tier 4");

            // Tier 5: [20, ∞)
            Assert.AreEqual(5, characterStats.CalculateSkillTier(20), "Level 20 should be tier 5");
            Assert.AreEqual(5, characterStats.CalculateSkillTier(21), "Level 21 should be tier 5");
            Assert.AreEqual(5, characterStats.CalculateSkillTier(100), "Level 100 should be tier 5");
        }

        [Test]
        [Description("SkillTier_EdgeCases")]
        public void CalculateSkillTier_EdgeCases()
        {
            // Test negative values
            Assert.AreEqual(0, characterStats.CalculateSkillTier(-1), "Negative level should be tier 0");
            Assert.AreEqual(0, characterStats.CalculateSkillTier(-100), "Large negative level should be tier 0");

            // Test very large values (post-game skills)
            Assert.AreEqual(5, characterStats.CalculateSkillTier(1000), "Very large level should be tier 5");
        }

        #endregion

        #region All 18 Skills Tests

        [Test]
        [Description("Skills_All18Skills_Initialized")]
        public void CharacterStats_Initialize_CreatesAll18Skills()
        {
            // All 18 skills should be initialized to 0
            string[] allSkills = {
                "tele", "melee", "smallguns", "energy", "explosives", "magic",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
                "attack", "defense", "knowl", "life", "spirit"
            };

            foreach (string skillId in allSkills)
            {
                int level = characterStats.GetSkillLevel(skillId);
                Assert.AreEqual(0, level, $"Skill {skillId} should initialize to 0");
            }
        }

        [Test]
        [Description("Skills_RegularSkills_CapAt20")]
        public void RegularSkills_MaxLevelIs20()
        {
            // Regular skills should cap at 20
            string[] regularSkills = {
                "tele", "melee", "smallguns", "energy", "explosives", "magic",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival"
            };

            foreach (string skillId in regularSkills)
            {
                // Reset character for each skill test
                characterStats.GrantSkillPoints(100);
                // Try to add 100 points
                characterStats.AddSkillPoint(skillId, 100);
                int level = characterStats.GetSkillLevel(skillId);

                Assert.AreEqual(20, level, $"Regular skill {skillId} should cap at 20");

                // Reset for next test
                characterStats.SetSkillLevel(skillId, 0);
            }
        }

        [Test]
        [Description("Skills_PostGameSkills_CapAt100")]
        public void PostGameSkills_MaxLevelIs100()
        {
            // Post-game skills should cap at 100
            string[] postGameSkills = { "attack", "defense", "knowl" };

            foreach (string skillId in postGameSkills)
            {
                // Reset character for each skill test
                characterStats.GrantSkillPoints(200);
                // Try to add 200 points
                characterStats.AddSkillPoint(skillId, 200);
                int level = characterStats.GetSkillLevel(skillId);

                Assert.AreEqual(100, level, $"Post-game skill {skillId} should cap at 100");

                // Reset for next test
                characterStats.SetSkillLevel(skillId, 0);
            }
        }

        [Test]
        [Description("Skills_AllSkills_HaveTiers")]
        public void AllSkills_GetSkillTier_Works()
        {
            // All skills should be able to calculate tiers
            string[] allSkills = {
                "tele", "melee", "smallguns", "energy", "explosives", "magic",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
                "attack", "defense", "knowl", "life", "spirit"
            };

            foreach (string skillId in allSkills)
            {
                characterStats.SetSkillLevel(skillId, 10);
                int tier = characterStats.GetSkillTier(skillId);

                Assert.IsTrue(tier >= 0 && tier <= 5, $"Skill {skillId} tier should be 0-5");
            }
        }

        #endregion

        #region Knowl Skill Extra Perk Points Tests

        [Test]
        [Description("KnowlSkill_AllThresholds_UnlockPerks")]
        public void KnowlSkill_AllThresholds_GrantExtraPerks()
        {
            // From docs: [5, 11, 18, 26, 35, 45, 56, 68, 82, 100]
            int[] thresholds = { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };
            int expectedExtraPerks = 0;

            foreach (int threshold in thresholds)
            {
                expectedExtraPerks++;
                characterStats.SetSkillLevel("knowl", threshold);

                int actualExtraPerks = characterStats.PerkPointsExtra;
                Assert.AreEqual(expectedExtraPerks, actualExtraPerks,
                    $"Knowl level {threshold} should grant {expectedExtraPerks} extra perks, got {actualExtraPerks}");

                // Verify total perk points increased
                Assert.IsTrue(characterStats.PerkPoints >= expectedExtraPerks,
                    $"Total perk points should be at least {expectedExtraPerks}");
            }
        }

        [Test]
        [Description("KnowlSkill_BetweenThresholds_NoNewPerks")]
        public void KnowlSkill_BetweenThresholds_DoesNotGrantNewPerks()
        {
            // Test between thresholds
            characterStats.SetSkillLevel("knowl", 5);
            int perksAt5 = characterStats.PerkPointsExtra;

            characterStats.SetSkillLevel("knowl", 7);
            int perksAt7 = characterStats.PerkPointsExtra;

            Assert.AreEqual(perksAt5, perksAt7, "Between thresholds should not grant new perks");

            characterStats.SetSkillLevel("knowl", 10);
            int perksAt10 = characterStats.PerkPointsExtra;

            Assert.AreEqual(perksAt5, perksAt10, "Between thresholds should not grant new perks");

            // At next threshold
            characterStats.SetSkillLevel("knowl", 11);
            int perksAt11 = characterStats.PerkPointsExtra;

            Assert.AreEqual(perksAt5 + 1, perksAt11, "At threshold 11 should grant new perk");
        }

        [Test]
        [Description("KnowlSkill_ThroughAddSkillPoint_GrantsPerks")]
        public void KnowlSkill_ThroughAddSkillPoint_GrantsPerksCorrectly()
        {
            // Test through AddSkillPoint (not SetSkillLevel)
            characterStats.GrantSkillPoints(100);

            int initialPerks = characterStats.PerkPoints;

            // Add to knowl threshold 5
            characterStats.AddSkillPoint("knowl", 5);
            Assert.AreEqual(initialPerks + 1, characterStats.PerkPoints, "Knowl 5 should grant +1 perk");

            // Add to knowl threshold 11
            characterStats.AddSkillPoint("knowl", 6); // Total 11
            Assert.AreEqual(initialPerks + 2, characterStats.PerkPoints, "Knowl 11 should grant +2 perks total");

            // Add to knowl threshold 18
            characterStats.AddSkillPoint("knowl", 7); // Total 18
            Assert.AreEqual(initialPerks + 3, characterStats.PerkPoints, "Knowl 18 should grant +3 perks total");
        }

        #endregion

        #region Level Up Rewards Tests

        [Test]
        [Description("LevelUp_MultipleLevels_GrantsCorrectRewards")]
        public void LevelUp_MultipleLevels_GrantsCorrectRewards()
        {
            // Test leveling from 1 to 5
            int initialSkillPoints = characterStats.SkillPoints;
            int initialPerkPoints = characterStats.PerkPoints;

            for (int level = 2; level <= 5; level++)
            {
                characterStats.AddXp(testLevelCurve.GetXpForLevel(level - 1));
            }

            Assert.AreEqual(5, characterStats.Level, "Should be level 5");
            Assert.AreEqual(initialSkillPoints + 5 * 4, characterStats.SkillPoints,
                "Should have 20 skill points (4 levels * 5 points)");
            Assert.AreEqual(initialPerkPoints + 4, characterStats.PerkPoints,
                "Should have 4 perk points (4 levels * 1 point)");
        }

        [Test]
        [Description("LevelUp_XpExactlyAtThreshold_LevelUpOccurs")]
        public void LevelUp_XpExactlyAtThreshold_DoesLevelUp()
        {
            // Test exact XP threshold
            int xpForLevel2 = testLevelCurve.GetXpForLevel(1);
            characterStats.AddXp(xpForLevel2);

            Assert.AreEqual(2, characterStats.Level, "Should level up with exact XP");
        }

        [Test]
        [Description("LevelUp_XpJustBelowThreshold_NoLevelUp")]
        public void LevelUp_XpJustBelowThreshold_DoesNotLevelUp()
        {
            // Test just below threshold
            int xpForLevel2 = testLevelCurve.GetXpForLevel(1);
            characterStats.AddXp(xpForLevel2 - 1);

            Assert.AreEqual(1, characterStats.Level, "Should not level up with 1 XP less");
        }

        [Test]
        [Description("LevelUp_HpScaling_IsCorrect")]
        public void LevelUp_HpScaling_MatchesDocumentation()
        {
            // From docs: hpPerLevel = 15, organHpPerLevel = 40
            // Base: maxHp = 100, organMaxHp = 200

            float initialMaxHp = characterStats.MaxHp;
            float initialOrganHp = characterStats.OrganMaxHp;

            characterStats.AddXp(testLevelCurve.GetXpForLevel(1));

            float newMaxHp = characterStats.MaxHp;
            float newOrganHp = characterStats.OrganMaxHp;

            Assert.AreEqual(initialMaxHp + 15, newMaxHp, "Max HP should increase by 15");
            Assert.AreEqual(initialOrganHp + 40, newOrganHp, "Organ HP should increase by 40");

            // Test multiple levels
            characterStats.AddXp(testLevelCurve.GetXpForLevel(2));
            characterStats.AddXp(testLevelCurve.GetXpForLevel(3));

            float maxHpAt4 = characterStats.MaxHp;
            float organHpAt4 = characterStats.OrganMaxHp;

            Assert.AreEqual(100 + 15 * 3, maxHpAt4, "Max HP should be base + 3 * 15");
            Assert.AreEqual(200 + 40 * 3, organHpAt4, "Organ HP should be base + 3 * 40");
        }

        #endregion

        #region Skill Point Allocation Tests

        [Test]
        [Description("AddSkillPoint_PartialAllocation")]
        public void AddSkillPoint_CanPartiallyAllocate()
        {
            // Test allocating some points, then more
            characterStats.GrantSkillPoints(10);

            characterStats.AddSkillPoint("melee", 3);
            Assert.AreEqual(3, characterStats.GetSkillLevel("melee"), "Should be level 3");
            Assert.AreEqual(7, characterStats.SkillPoints, "Should have 7 points remaining");

            characterStats.AddSkillPoint("melee", 2);
            Assert.AreEqual(5, characterStats.GetSkillLevel("melee"), "Should be level 5");
            Assert.AreEqual(5, characterStats.SkillPoints, "Should have 5 points remaining");
        }

        [Test]
        [Description("AddSkillPoint_MultipleSkills")]
        public void AddSkillPoint_CanAllocateToMultipleSkills()
        {
            characterStats.GrantSkillPoints(20);

            characterStats.AddSkillPoint("melee", 5);
            characterStats.AddSkillPoint("smallguns", 5);
            characterStats.AddSkillPoint("energy", 5);

            Assert.AreEqual(5, characterStats.GetSkillLevel("melee"), "Melee should be 5");
            Assert.AreEqual(5, characterStats.GetSkillLevel("smallguns"), "Small guns should be 5");
            Assert.AreEqual(5, characterStats.GetSkillLevel("energy"), "Energy should be 5");
            Assert.AreEqual(5, characterStats.SkillPoints, "Should have 5 points remaining");
        }

        [Test]
        [Description("AddSkillPoint_AtMaxLevel_RejectsAdditional")]
        public void AddSkillPoint_AtMaxLevel_DoesNotExceed()
        {
            characterStats.GrantSkillPoints(50);

            // Fill to max
            characterStats.AddSkillPoint("melee", 20);
            Assert.AreEqual(20, characterStats.GetSkillLevel("melee"), "Should be at max");

            int skillPointsBefore = characterStats.SkillPoints;

            // Try to add more
            bool result = characterStats.AddSkillPoint("melee", 1);

            Assert.IsFalse(result, "Should fail to add points at max");
            Assert.AreEqual(skillPointsBefore, characterStats.SkillPoints, "Should not consume points");
        }

        [Test]
        [Description("AddSkillPoint_PostGameSkill_HigherCap")]
        public void AddSkillPoint_PostGameSkills_CanExceedRegularCap()
        {
            // Post-game skill can go to 100
            characterStats.GrantSkillPoints(50);
            characterStats.AddSkillPoint("attack", 50);
            Assert.AreEqual(50, characterStats.GetSkillLevel("attack"), "Attack should be 50");

            // Regular skill caps at 20
            characterStats.GrantSkillPoints(50);  // Grant more points for melee
            characterStats.AddSkillPoint("melee", 50);
            Assert.AreEqual(20, characterStats.GetSkillLevel("melee"), "Melee should cap at 20");
        }

        [Test]
        [Description("AddSkillPoint_NotEnoughPoints_Fails")]
        public void AddSkillPoint_NotEnoughPoints_ReturnsFalse()
        {
            characterStats.GrantSkillPoints(3);

            bool result = characterStats.AddSkillPoint("melee", 5);

            Assert.IsFalse(result, "Should fail with insufficient points");
            Assert.AreEqual(0, characterStats.GetSkillLevel("melee"), "Level should remain 0");
            Assert.AreEqual(3, characterStats.SkillPoints, "Points should remain unchanged");
        }

        #endregion

        #region Perk System Tests

        [Test]
        [Description("Perk_AllPerks_HaveMaxRank")]
        public void GetPerkRank_AllPerksStartAtZero()
        {
            // Test various perks
            string[] testPerks = { "oak", "selflevit", "telethrow", "warlock", "dexter" };

            foreach (string perkId in testPerks)
            {
                Assert.AreEqual(0, characterStats.GetPerkRank(perkId), $"Perk {perkId} should start at 0");
            }
        }

        [Test]
        [Description("Perk_MultiRank_AllRanks")]
        public void AddPerk_MultiRank_CanReachAllRanks()
        {
            // Selflevit has 2 ranks, gated `<req id='tele' lvl='2' dlvl='3'/>` (AllData.as:5332).
            // `lvl` is compared against getSkLevel -- the TIER, not the raw point count
            // (Pers.as:1458 `getSkLevel(...) < reqlevel`). Tier 2 needs raw 5 (>=5 -> 2), so the old
            // raw 2 was tier 1 and the first rank was correctly refused once the perk database became
            // loadable. Rank 2 adds dlvl*heldRank = 3 -> tier 5, i.e. raw 20.
            characterStats.GrantPerkPoints(2);
            characterStats.SetSkillLevel("tele", 20);

            Assert.IsTrue(characterStats.AddPerk("selflevit"), "First rank should succeed");
            Assert.AreEqual(1, characterStats.GetPerkRank("selflevit"), "Should be rank 1");

            Assert.IsTrue(characterStats.AddPerk("selflevit"), "Second rank should succeed");
            Assert.AreEqual(2, characterStats.GetPerkRank("selflevit"), "Should be rank 2");

            // Third rank should fail
            Assert.IsFalse(characterStats.AddPerk("selflevit"), "Third rank should fail");
            Assert.AreEqual(2, characterStats.GetPerkRank("selflevit"), "Should remain at rank 2");
        }

        [Test]
        [Description("Perk_Requirements_LevelDelta")]
        public void AddPerk_LevelDelta_RequirementsUpdatePerRank()
        {
            // Test perk with level delta (additional requirements per rank)
            // selflevit: `<req id='tele' lvl='2' dlvl='3'/>` -- `lvl` is a TIER (Pers.as:1458
            // compares getSkLevel), so tier 2 = raw 5. Rank 2 would need tier 5 = raw 20.
            characterStats.GrantPerkPoints(2);
            characterStats.SetSkillLevel("tele", 5);

            // First rank
            bool result1 = characterStats.AddPerk("selflevit");
            Assert.IsTrue(result1, "First rank should succeed with Tele tier 2 (raw 5)");
        }

        [Test]
        [Description("Perk_NoPerkPoints_Fails")]
        public void AddPerk_NoPerkPoints_ReturnsFalse()
        {
            // Deliberately left at raw 1 (melee tier 0), i.e. the oak requirement is NOT met. The
            // rejection must come from perkPoints == 0 alone (CharacterStats.AddPerk, the
            // `if (perkPoints <= 0) return false;` guard) — do not raise this to 2, or the test
            // stops isolating the point it exists to pin.
            characterStats.SetSkillLevel("melee", 1);

            bool result = characterStats.AddPerk("oak");

            Assert.IsFalse(result, "Should fail without perk points");
            Assert.AreEqual(0, characterStats.GetPerkRank("oak"), "Should not unlock perk");
        }

        [Test]
        [Description("Perk_UnknownPerk_AllowedForTesting")]
        public void AddPerk_UnknownPerk_Succeeds()
        {
            // Unknown perks should be allowed (for testing/modding)
            characterStats.GrantPerkPoints(1);

            bool result = characterStats.AddPerk("unknown_perk");

            // Current implementation allows unknown perks
            Assert.IsTrue(result, "Unknown perk should be allowed");
            Assert.AreEqual(1, characterStats.GetPerkRank("unknown_perk"), "Should unlock unknown perk");
        }

        #endregion

        #region Stat Modification Tests

        [Test]
        [Description("StatModifier_Medic_AllTiers")]
        public void MedicSkill_AllTiers_ApplyCorrectHp()
        {
            // Test all medic tiers
            // +20/+50/+90/+140/+200

            int[] levels = { 2, 5, 9, 14, 20 };
            int[] expectedHp = { 120, 150, 190, 240, 300 };
            int[] expectedBonus = { 20, 50, 90, 140, 200 };

            for (int i = 0; i < levels.Length; i++)
            {
                characterStats.SetSkillLevel("medic", levels[i]);
                characterStats.RecalculateStats();

                int actualHp = Mathf.RoundToInt(characterStats.MaxHp);
                Assert.AreEqual(expectedHp[i], actualHp,
                    $"Medic level {levels[i]} should give {expectedHp[i]} HP, got {actualHp}");

                // Verify factor tracking
                var factors = characterStats.GetFactorsForStat("maxhp");
                Assert.IsTrue(factors.Count > 0, "Should have maxhp factors");
                Assert.IsTrue(factors.Exists(f => f.sourceId == "medic"), "Should have medic factor");
            }
        }

        [Test]
        [Description("StatModifier_Attack_PostGameScaling")]
        public void AttackSkill_PostGame_CorrectScaling()
        {
            // AS3 `<sk id='allDamMult' ref='add' v0='0' vd='0.05'/>` (AllData.as:5299) has no `dop`,
            // so the modifier is evaluated against the TIER. `attack` is a post skill, and its tier
            // table is postSkTab = [5,11,18,26,35,45,56,68,82,100] (Pers.as:511), so:
            //   raw 5..10 -> tier 1 -> +5%;  raw 100 -> tier 10 -> +50%.
            // This is NOT "+5% per raw point": raw 10 is tier 1, not 10 tiers.
            characterStats.SetSkillLevel("attack", 0);
            characterStats.RecalculateStats();
            Assert.AreEqual(1.0f, characterStats.AllDamMult, 0.01f, "Attack 0 should be 1.0x");

            characterStats.SetSkillLevel("attack", 5);   // post tier 1
            characterStats.RecalculateStats();
            Assert.AreEqual(1.05f, characterStats.AllDamMult, 0.01f, "Attack tier 1 should be 1.05x");

            characterStats.SetSkillLevel("attack", 35);  // post tier 5
            characterStats.RecalculateStats();
            Assert.AreEqual(1.25f, characterStats.AllDamMult, 0.01f, "Attack tier 5 should be 1.25x");

            characterStats.SetSkillLevel("attack", 100); // post tier 10 (the cap)
            characterStats.RecalculateStats();
            Assert.AreEqual(1.5f, characterStats.AllDamMult, 0.01f, "Attack tier 10 should be 1.5x");
        }

        [Test]
        [Description("StatModifier_Defense_Stacking")]
        public void DefenseSkill_StacksCorrectly()
        {
            // AS3 `<sk id='allVulnerMult' ref='mult' vd='-0.03'/>` (AllData.as:5303) has no `dop`,
            // so it uses the TIER, and it is ONE linear term — not a per-point compounding curve.
            // SkillDefinition.Evaluate documents this at length: AS3 computes base + level * vd for
            // every ref type, and only the application differs (`*=` vs `+=`). defense is a post
            // skill, so its tier table is postSkTab = [5,11,18,26,35,45,56,68,82,100] (Pers.as:511).
            var cases = new (int raw, int tier, float expected)[]
            {
                (0,   0, 1.00f),
                (5,   1, 0.97f),
                (11,  2, 0.94f),
                (18,  3, 0.91f),
                (35,  5, 0.85f),
                (100, 10, 0.70f),
            };

            foreach (var c in cases)
            {
                characterStats.SetSkillLevel("defense", c.raw);
                characterStats.RecalculateStats();

                Assert.AreEqual(c.expected, characterStats.AllVulnerMult, 0.01f,
                    $"Defense raw {c.raw} (tier {c.tier}) should be {c.expected:F2} — one linear " +
                    "term of -0.03 per TIER, not 0.97^raw");
            }
        }

        [Test]
        [Description("StatModifier_Survival_Skin")]
        public void SurvivalSkill_IncreasesSkinCorrectly()
        {
            // AS3 `<sk id='skin' v0='0' vd='1'/>` (AllData.as:5294) has no `dop`, so it uses the
            // TIER (getSkLevel, Pers.as:995-1018): raw 1 -> 0, 2..4 -> 1, 5..8 -> 2, 9..13 -> 3,
            // 14..19 -> 4, 20 -> 5.
            var cases = new (int raw, int tier, float expected)[]
            {
                (0,  0, 0f), (1,  0, 0f), (2,  1, 1f), (4,  1, 1f),
                (5,  2, 2f), (8,  2, 2f), (9,  3, 3f), (13, 3, 3f),
                (14, 4, 4f), (20, 5, 5f),
            };

            foreach (var c in cases)
            {
                characterStats.SetSkillLevel("survival", c.raw);
                characterStats.RecalculateStats();

                Assert.AreEqual(c.expected, characterStats.skin, 0.01f,
                    $"Survival raw {c.raw} (tier {c.tier}) should give {c.expected} skin");
            }
        }

        [Test]
        [Description("StatModifier_Sneak — AS3's sneak skill grants no dexter")]
        public void SneakSkill_GrantsNoDexter()
        {
            // AS3's <skill id='sneak'> (AllData.as:5277) carries sneak / noiseRun / critInvis /
            // signal / sneakLurk — and NO `dexter`. The six `<sk id='dexter'>` entries in AllData.as
            // belong to mod_holo (:4159), the `dexter` perk (:5701), potion_dexter (:5840), the
            // `drunk` effect (:5988), f_salad (:6259) and f_soup (:6283).
            //
            // An earlier revision of the port invented "+0.15 dexter per sneak point", which is
            // the `dexter` PERK's slope, not the skill's. This test now pins the oracle: the skill
            // alone must leave dexter untouched.
            for (int level = 0; level <= 20; level += 5)
            {
                characterStats.SetSkillLevel("sneak", level);
                characterStats.RecalculateStats();

                Assert.AreEqual(0f, characterStats.dexter, 0.001f,
                    $"Sneak raw {level} must not change dexter — AS3's sneak skill has no dexter modifier");
            }
        }

        [Test]
        [Description("StatModifier_CombinedSkills")]
        public void MultipleSkills_CombineCorrectly()
        {
            // Test multiple skills affecting the same stat. medic's maxhp uses a TIER-indexed
            // table: `<sk tip='unit' id='maxhp' ref='add' v1='20' v2='50' v3='90' v4='140'
            // v5='200'/>` (AllData.as:5261), so raw 5 (tier 2) is +50. survival's skin is tier 2
            // at raw 10, so +2.
            characterStats.SetSkillLevel("medic", 5);    // tier 2 -> +50 HP
            characterStats.SetSkillLevel("survival", 10); // tier 2 -> +2 skin
            characterStats.RecalculateStats();

            Assert.AreEqual(150, characterStats.MaxHp, "Should have 150 HP from medic tier 2");
            Assert.AreEqual(2, characterStats.skin, "Should have 2 skin from survival tier 2");

            // Verify factor tracking for the skills that actually have modifiers.
            var maxHpFactors = characterStats.GetFactorsForStat("maxhp");
            var skinFactors = characterStats.GetFactorsForStat("skin");

            Assert.IsTrue(maxHpFactors.Exists(f => f.sourceId == "medic"), "Should track medic factor");
            Assert.IsTrue(skinFactors.Exists(f => f.sourceId == "survival"), "Should track survival factor");
        }

        #endregion

        #region Save/Load Tests

        [Test]
        [Description("SaveLoad_AllFields")]
        public void CharacterStats_SaveLoad_PreservesAllFields()
        {
            // Set up a complex character
            characterStats.AddXp(testLevelCurve.GetXpForLevel(1));
            characterStats.AddXp(testLevelCurve.GetXpForLevel(2));
            characterStats.AddXp(testLevelCurve.GetXpForLevel(3));

            characterStats.GrantSkillPoints(50);
            characterStats.AddSkillPoint("melee", 10);
            characterStats.AddSkillPoint("medic", 5);
            characterStats.AddSkillPoint("knowl", 15);

            characterStats.GrantPerkPoints(5);
            characterStats.SetSkillLevel("melee", 2);
            characterStats.AddPerk("oak");
            // selflevit needs tele TIER 2 (Pers.as:1458 compares getSkLevel against lvl='2'), i.e. raw 5.
            characterStats.SetSkillLevel("tele", 5);
            characterStats.AddPerk("selflevit");

            // Set health
            characterStats.headHp = 150;
            characterStats.torsHp = 175;
            characterStats.legsHp = 125;
            characterStats.bloodHp = 180;
            characterStats.manaHp = 300;

            // Save
            var saveData = characterStats.GetSaveData();

            // Load into new character
            var newGameObject = new GameObject("LoadedCharacter");
            var loadedStats = newGameObject.AddComponent<CharacterStats>();
            loadedStats.Initialize(testLevelCurve);
            loadedStats.LoadSaveData(saveData);

            // Verify all fields
            Assert.AreEqual(characterStats.Level, loadedStats.Level, "Level should match");
            Assert.AreEqual(characterStats.Xp, loadedStats.Xp, "XP should match");
            Assert.AreEqual(characterStats.SkillPoints, loadedStats.SkillPoints, "Skill points should match");
            Assert.AreEqual(characterStats.PerkPoints, loadedStats.PerkPoints, "Perk points should match");
            Assert.AreEqual(characterStats.PerkPointsExtra, loadedStats.PerkPointsExtra, "Extra perk points should match");

            // Verify skills
            Assert.AreEqual(characterStats.GetSkillLevel("melee"), loadedStats.GetSkillLevel("melee"), "Melee level should match");
            Assert.AreEqual(characterStats.GetSkillLevel("medic"), loadedStats.GetSkillLevel("medic"), "Medic level should match");
            Assert.AreEqual(characterStats.GetSkillLevel("knowl"), loadedStats.GetSkillLevel("knowl"), "Knowl level should match");

            // Verify perks
            Assert.AreEqual(characterStats.GetPerkRank("oak"), loadedStats.GetPerkRank("oak"), "Oak perk should match");
            Assert.AreEqual(characterStats.GetPerkRank("selflevit"), loadedStats.GetPerkRank("selflevit"), "Selflevit perk should match");

            // Verify derived stats are recalculated
            Assert.AreEqual(characterStats.MaxHp, loadedStats.MaxHp, "Max HP should match after recalc");
            Assert.AreEqual(characterStats.AllDamMult, loadedStats.AllDamMult, 0.01f, "Damage mult should match after recalc");

            UnityEngine.Object.DestroyImmediate(newGameObject);
        }

        [Test]
        [Description("SaveLoad_HealthNormalization")]
        public void CharacterStats_SaveLoad_NormalizesHealthCorrectly()
        {
            // Set specific health values
            characterStats.headHp = 100;
            characterStats.torsHp = 150;

            var saveData = characterStats.GetSaveData();

            // Verify normalized values
            float expectedHead = 100 / characterStats.OrganMaxHp;
            float expectedTors = 150 / characterStats.OrganMaxHp;

            Assert.AreEqual(expectedHead, saveData.headHp, 0.001f, "Head HP should be normalized");
            Assert.AreEqual(expectedTors, saveData.torsHp, 0.001f, "Tors HP should be normalized");

            // Load and verify denormalization
            var newGameObject = new GameObject("LoadedCharacter");
            var loadedStats = newGameObject.AddComponent<CharacterStats>();
            loadedStats.Initialize(testLevelCurve);
            loadedStats.LoadSaveData(saveData);

            Assert.AreEqual(100, loadedStats.headHp, 1.0f, "Head HP should be denormalized correctly");
            Assert.AreEqual(150, loadedStats.torsHp, 1.0f, "Tors HP should be denormalized correctly");

            UnityEngine.Object.DestroyImmediate(newGameObject);
        }

        [Test]
        [Description("SaveLoad_EmptyCharacter")]
        public void CharacterStats_SaveLoad_EmptyCharacterWorks()
        {
            // Save fresh character
            var saveData = characterStats.GetSaveData();

            // Load into new character
            var newGameObject = new GameObject("LoadedCharacter");
            var loadedStats = newGameObject.AddComponent<CharacterStats>();
            loadedStats.Initialize(testLevelCurve);
            loadedStats.LoadSaveData(saveData);

            // Should be identical to fresh character
            Assert.AreEqual(1, loadedStats.Level, "Level should be 1");
            Assert.AreEqual(0, loadedStats.Xp, "XP should be 0");
            Assert.AreEqual(0, loadedStats.SkillPoints, "Skill points should be 0");
            Assert.AreEqual(0, loadedStats.PerkPoints, "Perk points should be 0");

            UnityEngine.Object.DestroyImmediate(newGameObject);
        }

        #endregion

        #region Factor Tracking Tests

        [Test]
        [Description("FactorTracking_Complete")]
        public void GetFactorsForStat_CompleteTracking()
        {
            // Set up multiple sources
            characterStats.SetSkillLevel("medic", 5);   // +50 HP
            characterStats.SetSkillLevel("survival", 10); // +10 skin
            characterStats.GrantPerkPoints(1);
            characterStats.SetSkillLevel("melee", 2);
            characterStats.AddPerk("oak"); // +0.25 blade resistance (requires skillDatabase for factor tracking)
            characterStats.RecalculateStats();

            // Check maxHp factors
            var maxHpFactors = characterStats.GetFactorsForStat("maxhp");
            Assert.IsTrue(maxHpFactors.Count >= 1, "Should have maxHp factors");
            Assert.IsTrue(maxHpFactors.Exists(f => f.sourceId == "medic" && f.sourceType == "skill"),
                "Should have medic skill factor");

            // Check skin factors
            var skinFactors = characterStats.GetFactorsForStat("skin");
            Assert.IsTrue(skinFactors.Count >= 1, "Should have skin factors");
            Assert.IsTrue(skinFactors.Exists(f => f.sourceId == "survival" && f.sourceType == "skill"),
                "Should have survival skill factor");

            // Note: Perk factor tracking requires skillDatabase to be set with PerkDefinitions
            // This is not set up in the test, so we skip the perk factor check
            // In production, perks would track factors through ApplyPerkEffects
        }

        [Test]
        [Description("FactorTracking_EmptyStats")]
        public void GetFactorsForStat_EmptyStats_ReturnEmptyList()
        {
            var factors = characterStats.GetFactorsForStat("maxhp");

            Assert.IsNotNull(factors, "Should return list, not null");

            // NOT zero, and that is faithful. Initialize() -> SeedSkillIds() fills the skill table
            // with every authored skill at 0, and RecalculateStats evaluates each one's level-0
            // modifier -- AS3's Pers constructor does the same (Pers.as:2167-2180 iterates the whole
            // `skills` map), which is exactly why the engine cannot go inert. `medic` is the only
            // skill with a `maxhp` modifier, so a fresh character records exactly one `maxHp` factor,
            // contributing +0 at level 0.
            //
            // The meaningful assertion is that nothing has CHANGED the stat yet, not that no row
            // exists: a level-0 row with result == base is "engine ran, found nothing to add".
            Assert.AreEqual(1, factors.Count,
                "A fresh character records one level-0 maxHp factor from the medic skill");
            Assert.AreEqual(characterStats.MaxHp, factors[0].result, 1e-3f,
                "The level-0 factor must leave MaxHp at its base value");
            Assert.AreEqual(0f, factors[0].value, 1e-3f,
                "A level-0 medic modifier contributes 0");
        }

        [Test]
        [Description("FactorTracking_UnknownStat")]
        public void GetFactorsForStat_UnknownStat_ReturnsEmptyList()
        {
            var factors = characterStats.GetFactorsForStat("unknown_stat");

            Assert.IsNotNull(factors, "Should return list, not null");
            Assert.AreEqual(0, factors.Count, "Should be empty for unknown stat");
        }

        #endregion

        #region Integration Tests

        [Test]
        [Description("Integration_FullProgression")]
        public void FullProgression_Level1To20_WorksCorrectly()
        {
            // Simulate progression from level 1 to 5
            for (int level = 2; level <= 5; level++)
            {
                characterStats.AddXp(testLevelCurve.GetXpForLevel(level - 1));

                // Allocate skill points
                int skillPointsToUse = characterStats.SkillPoints;
                if (skillPointsToUse > 0)
                {
                    characterStats.AddSkillPoint("melee", skillPointsToUse);
                }

                // Use perk point
                if (characterStats.PerkPoints > 0 && characterStats.GetSkillLevel("melee") >= 1)
                {
                    characterStats.AddPerk("oak");
                }
            }

            // Verify final state
            Assert.AreEqual(5, characterStats.Level, "Should be level 5");
            Assert.IsTrue(characterStats.GetSkillLevel("melee") > 0, "Should have melee skill");
            Assert.IsTrue(characterStats.GetPerkRank("oak") > 0, "Should have oak perk");
        }

        [Test]
        [Description("Integration_PostGameProgression")]
        public void PostGameProgression_Level20Plus_WorksCorrectly()
        {
            // Fast forward to level 20
            characterStats.GrantSkillPoints(500);

            // Max out a regular skill
            characterStats.AddSkillPoint("melee", 20);
            Assert.AreEqual(20, characterStats.GetSkillLevel("melee"), "Melee should be maxed");

            // Level up post-game skills
            characterStats.AddSkillPoint("attack", 50);
            Assert.AreEqual(50, characterStats.GetSkillLevel("attack"), "Attack should be 50");

            characterStats.AddSkillPoint("defense", 30);
            Assert.AreEqual(30, characterStats.GetSkillLevel("defense"), "Defense should be 30");

            characterStats.AddSkillPoint("knowl", 25);
            Assert.AreEqual(25, characterStats.GetSkillLevel("knowl"), "Knowl should be 25");

            // Verify knowl perk points
            Assert.IsTrue(characterStats.PerkPointsExtra >= 3, "Knowl 25 should grant at least 3 extra perks");

            // Verify stat effects.
            //
            // These are POST skills, so the <req>-style TIER table applies, not the raw point count:
            // PostSkTab = {5,11,18,26,35,45,56,68,82,100} and getSkLevel maps raw -> tier
            // (Pers.as:995-1018). attack 50 -> tier 6 -> `<sk id='allDamMult' ref='add' v0='0' vd='0.05'/>`
            // (AllData.as:5299) gives 1 + 6*0.05 = 1.30, NOT the 1 + 50*0.05 = 3.5 the old
            // ">2x" assertion assumed (that reading is the fallback engine's, which is only reached
            // when the skill database is unavailable). defense 30 -> tier 4 ->
            // `<sk id='allVulnerMult' ref='mult' vd='-0.03'/>` (:5303) gives 1 - 4*0.03 = 0.88.
            Assert.AreEqual(1.30f, characterStats.AllDamMult, 1e-3f,
                "Attack tier 6 must give 1 + 6*0.05 = 1.30x damage");
            Assert.AreEqual(0.88f, characterStats.AllVulnerMult, 1e-3f,
                "Defense tier 4 must give 1 - 4*0.03 = 0.88 vulnerability");
        }

        [Test]
        [Description("Integration_EventFiring")]
        public void Events_FireCorrectly()
        {
            // Test event firing
            int levelUpCount = 0;
            int skillChangeCount = 0;
            int perkAddCount = 0;
            int recalcCount = 0;

            characterStats.onLevelUp += (level) => levelUpCount++;
            characterStats.onSkillChanged += (id, level) => skillChangeCount++;
            characterStats.onPerkAdded += (id, rank) => perkAddCount++;
            characterStats.onStatsRecalculated += () => recalcCount++;

            // Trigger events
            characterStats.AddXp(testLevelCurve.GetXpForLevel(1));
            characterStats.GrantSkillPoints(5);
            characterStats.AddSkillPoint("melee", 2);
            characterStats.GrantPerkPoints(1);
            characterStats.SetSkillLevel("melee", 2);
            characterStats.AddPerk("oak");

            Assert.IsTrue(levelUpCount > 0, "LevelUp event should fire");
            Assert.IsTrue(skillChangeCount > 0, "SkillChanged event should fire");
            Assert.IsTrue(perkAddCount > 0, "PerkAdded event should fire");
            Assert.IsTrue(recalcCount > 0, "StatsRecalculated event should fire");
        }

        #endregion

        #region Edge Cases and Error Handling

        [Test]
        [Description("EdgeCase_NegativeSkillLevel")]
        public void SetSkillLevel_Negative_ClampsToZero()
        {
            characterStats.SetSkillLevel("melee", -10);
            Assert.AreEqual(0, characterStats.GetSkillLevel("melee"), "Should clamp to 0");
        }

        [Test]
        [Description("EdgeCase_SkillLevelAbove100")]
        public void SetSkillLevel_Above100_ClampsTo100()
        {
            characterStats.SetSkillLevel("attack", 200);
            Assert.AreEqual(100, characterStats.GetSkillLevel("attack"), "Should clamp to 100");
        }

        [Test]
        [Description("EdgeCase_InvalidSkillId")]
        public void GetSkillLevel_InvalidId_ReturnsZero()
        {
            int level = characterStats.GetSkillLevel("invalid_skill");
            Assert.AreEqual(0, level, "Invalid skill should return 0");
        }

        [Test]
        [Description("EdgeCase_InvalidPerkId")]
        public void GetPerkRank_InvalidId_ReturnsZero()
        {
            int rank = characterStats.GetPerkRank("invalid_perk");
            Assert.AreEqual(0, rank, "Invalid perk should return 0");
        }

        [Test]
        [Description("EdgeCase_NullLevelCurve")]
        public void CharacterStats_WithoutLevelCurve_DoesNotCrash()
        {
            // Create character without initialization
            var gameObject = new GameObject("NoCurveCharacter");
            var stats = gameObject.AddComponent<CharacterStats>();

            // Should not crash
            stats.RecalculateStats();

            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        [Description("EdgeCase_ZeroXpDelta")]
        public void LevelCurve_ZeroXpDelta_Works()
        {
            testLevelCurve.xpDelta = 0;

            int xp = testLevelCurve.GetXpForLevel(5);
            Assert.AreEqual(0, xp, "Zero xpDelta should give zero XP");
        }

        [Test]
        [Description("EdgeCase_VeryLargeXpDelta")]
        public void LevelCurve_VeryLargeXpDelta_DoesNotOverflow()
        {
            testLevelCurve.xpDelta = 1000000;

            int xp = testLevelCurve.GetXpForLevel(100);
            // Should handle large values gracefully
            Assert.IsTrue(xp > 0, $"Should calculate valid XP, got {xp}");
            // With xpDelta = 1M and level 100, the sum is huge (>5B), so it should clamp
            Assert.IsTrue(xp >= int.MaxValue / 2, "Should be very large value or clamped");
        }

        #endregion
    }
}

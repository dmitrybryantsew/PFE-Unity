using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for telekinesis stats in CharacterStats (AS3: Pers.as & AllData.as).
    /// </summary>
    [TestFixture]
    public class CharacterStatsTelekinesisTests
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
        public void Initialize_BaseTelekinesisStats_MatchAS3Defaults()
        {
            var stats = CreateTestCharacter();

            Assert.AreEqual(0.6f, stats.MaxTeleMassa, 1e-4f, "Base maxTeleMassa should be 0.6 (AllData.as:5212 level 0)");
            Assert.AreEqual(360000f, stats.TeleDist, 1e-4f, "Base teleDist should be 360000 (Pers.as:235)");
            Assert.AreEqual(0.1f, stats.TelePorog, 1e-4f, "Base telePorog should be 0.1 (AllData.as:5213 level 0)");
            Assert.AreEqual(2.2f, stats.TeleMult, 1e-4f, "Base teleMult should be 2.2 (AllData.as:5210 level 0)");
            Assert.AreEqual(0.0f, stats.ThrowForce, 1e-4f, "Base throwForce should be 0 (Pers.as:237)");
            Assert.AreEqual(200f, stats.ThrowDmagic, 1e-4f, "Base throwDmagic should be 200 (Pers.as:239)");
            Assert.AreEqual(0.05f, stats.ThrowDmanaMult, 1e-4f, "Base throwDmanaMult should be 0.05 (Pers.as:241)");
            Assert.AreEqual(1.0f, stats.AllDManaMult, 1e-4f, "Base allDManaMult should be 1.0 (Pers.as:227)");
            Assert.AreEqual(0, stats.Telemaster, "Base telemaster should be 0 (Pers.as:233)");
        }

        [TestCase(2, 1.6f)]   // Tier 1 (2-4 points)
        [TestCase(5, 3.0f)]   // Tier 2 (5-8 points)
        [TestCase(9, 6.0f)]   // Tier 3 (9-13 points)
        [TestCase(14, 12.0f)] // Tier 4 (14-19 points)
        [TestCase(20, 25.0f)] // Tier 5 (20 points)
        public void TeleSkill_IncreasesMaxTeleMassa(int skillLevel, float expectedMass)
        {
            var stats = CreateTestCharacter();
            stats.SetSkillLevel("tele", skillLevel);

            Assert.AreEqual(expectedMass, stats.MaxTeleMassa, 1e-4f,
                $"Telekinesis skill level {skillLevel} should have maxTeleMassa = {expectedMass}");
        }

        [TestCase(2, 0.2f)]   // Tier 1 (2-4 points)
        [TestCase(5, 0.3f)]   // Tier 2 (5-8 points)
        [TestCase(9, 0.5f)]   // Tier 3 (9-13 points)
        [TestCase(14, 0.8f)]  // Tier 4 (14-19 points)
        [TestCase(20, 1.2f)]  // Tier 5 (20 points)
        public void TeleSkill_IncreasesTelePorog(int skillLevel, float expectedPorog)
        {
            var stats = CreateTestCharacter();
            stats.SetSkillLevel("tele", skillLevel);

            Assert.AreEqual(expectedPorog, stats.TelePorog, 1e-4f,
                $"Telekinesis skill level {skillLevel} should have telePorog = {expectedPorog}");
        }

        [TestCase(1, 2.1f)]
        [TestCase(2, 2.0f)]
        [TestCase(3, 1.9f)]
        [TestCase(4, 1.8f)]
        [TestCase(5, 1.7f)]
        public void TeleSkill_DecreasesTeleMult(int skillLevel, float expectedMult)
        {
            var stats = CreateTestCharacter();
            stats.SetSkillLevel("tele", skillLevel);

            Assert.AreEqual(expectedMult, stats.TeleMult, 1e-4f,
                $"Telekinesis skill level {skillLevel} should have teleMult = {expectedMult}");
        }
    }
}

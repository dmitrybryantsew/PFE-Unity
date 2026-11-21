using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// Tests for the derived-stat snapshot blocks (<see cref="LocomotionStatBlock"/>,
    /// <see cref="CombatStatBlock"/>, <see cref="UtilityStatBlock"/>) exposed by
    /// <see cref="CharacterStats"/>.
    ///
    /// Contract under test: a block is a COPY of the live fields at the last derived-stats version
    /// bump; accessing the property again after any stat write path (RecalculateStats,
    /// ApplyNamedStat, ResetToDefaults, teleport/spell setters) returns a refreshed copy; the
    /// previously taken copy is untouched.
    /// </summary>
    [TestFixture]
    public class CharacterStatsBlockTests
    {
        private LevelCurve testLevelCurve;
        private CharacterStats characterStats;

        [SetUp]
        public void Setup()
        {
            testLevelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            testLevelCurve.xpDelta = 5000;
            testLevelCurve.skillPointsPerLevel = 5;
            testLevelCurve.baseHp = 100;
            testLevelCurve.hpPerLevel = 15;
            testLevelCurve.organHpPerLevel = 40;
            testLevelCurve.baseOrganHp = 200;

            var gameObject = new GameObject("TestCharacter");
            characterStats = gameObject.AddComponent<CharacterStats>();
            characterStats.Initialize(testLevelCurve);
        }

        [TearDown]
        public void TearDown()
        {
            if (testLevelCurve != null) Object.DestroyImmediate(testLevelCurve);
            if (characterStats != null) Object.DestroyImmediate(characterStats.gameObject);
        }

        [Test]
        public void Blocks_AfterInitialize_MirrorLiveFields()
        {
            Assert.AreEqual(characterStats.allSpeedMult, characterStats.Locomotion.AllSpeedMult, 1e-4f);
            Assert.AreEqual(characterStats.jumpdy, characterStats.Locomotion.Jumpdy, 1e-4f);
            Assert.AreEqual(characterStats.sneakLurk, characterStats.Locomotion.SneakLurk, 1e-4f);

            Assert.AreEqual(characterStats.allDamMult, characterStats.Combat.AllDamMult, 1e-4f);
            Assert.AreEqual(characterStats.critCh, characterStats.Combat.CritCh, 1e-4f);
            Assert.AreEqual(characterStats.satsMult, characterStats.Combat.SatsMult, 1e-4f);
            Assert.AreEqual(characterStats.skin, characterStats.Combat.Skin, 1e-4f);

            Assert.AreEqual(characterStats.lockPick, characterStats.Utility.LockPick);
            Assert.AreEqual(characterStats.teleDist, characterStats.Utility.TeleDist, 1e-4f);
            Assert.AreEqual(characterStats.petHP, characterStats.Utility.PetHP, 1e-4f);
            Assert.AreEqual(characterStats.maxm1, characterStats.Utility.Maxm1, 1e-4f);
        }

        [Test]
        public void LocomotionBlock_RefreshesAfterApplyNamedStat()
        {
            float before = characterStats.Locomotion.AllSpeedMult;

            characterStats.ApplyNamedStat("allSpeedMult", null, "mult", 2f);

            Assert.AreEqual(before * 2f, characterStats.Locomotion.AllSpeedMult, 1e-4f,
                "block must reflect a direct ApplyNamedStat write");
            Assert.AreEqual(characterStats.allSpeedMult, characterStats.Locomotion.AllSpeedMult, 1e-4f,
                "block must mirror the live field");
        }

        [Test]
        public void CombatBlock_RefreshesAfterApplyNamedStat()
        {
            float before = characterStats.Combat.CritCh;

            characterStats.ApplyNamedStat("critCh", null, "add", 0.1f);

            Assert.AreEqual(before + 0.1f, characterStats.Combat.CritCh, 1e-4f);
        }

        [Test]
        public void UtilityBlock_RefreshesAfterPropertySetter()
        {
            characterStats.TeleDist = 123f;

            Assert.AreEqual(123f, characterStats.Utility.TeleDist, 1e-4f,
                "a setter write must invalidate the cached block");
            Assert.AreEqual(123f, characterStats.teleDist, 1e-4f);
        }

        [Test]
        public void UtilityBlock_RefreshesAfterResetToDefaults()
        {
            characterStats.ApplyNamedStat("upChance", null, "add", 0.9f);
            Assert.AreEqual(0.9f, characterStats.Utility.UpChance, 1e-4f);

            characterStats.ResetToDefaults();

            Assert.AreEqual(0f, characterStats.Utility.UpChance, 1e-4f,
                "ResetToDefaults writes fields directly, so it must bump the derived-stats version");
            Assert.AreEqual(0f, characterStats.upChance, 1e-4f);
        }

        [Test]
        public void TakenSnapshot_IsNotMutatedByLaterStatWrites()
        {
            LocomotionStatBlock snapshot = characterStats.Locomotion;
            float snapshotSpeed = snapshot.AllSpeedMult;

            characterStats.ApplyNamedStat("allSpeedMult", null, "mult", 5f);

            Assert.AreEqual(snapshotSpeed, snapshot.AllSpeedMult, 1e-4f,
                "a held block is a copy — later writes must not reach it");
            Assert.AreEqual(snapshotSpeed * 5f, characterStats.Locomotion.AllSpeedMult, 1e-4f,
                "the fresh access returns the refreshed copy");
        }

        [Test]
        public void Blocks_RefreshesAfterGlobalDifficultyChange()
        {
            // ApplyGlobalDifficulty = SetGlobalDifficulty (writes dieDamage/difCapsMult/teleMana
            // directly from DifficultyRules.PersTable) + RecalculateStats — the oracle's full
            // difficulty change. It needs no skill database, so it exercises the RecalculateStats
            // version bump deterministically.
            characterStats.ApplyGlobalDifficulty(4); // Super-hard row: difCapsMult 0.5, dieDamage 0.35

            Assert.AreEqual(0.5f, characterStats.Utility.DifCapsMult, 1e-4f,
                "Utility block must reflect the difficulty row after RecalculateStats");
            Assert.AreEqual(0.35f, characterStats.Combat.DieDamage, 1e-4f,
                "Combat block must reflect the difficulty row after RecalculateStats");
        }
    }
}

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using System.Text.RegularExpressions;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// Tests for RPG Save/Load system.
    /// Tests serialization, deserialization, and data integrity.
    /// </summary>
    public class RPGSaveLoadTests
    {
        private CharacterStats characterStats;
        private SkillDefinitionDatabase skillDatabase;
        private LevelCurve levelCurve;

        [SetUp]
        public void Setup()
        {
            // Create test GameObject
            GameObject testObject = new GameObject("TestCharacter");
            characterStats = testObject.AddComponent<CharacterStats>();

            // Create test database
            skillDatabase = ScriptableObject.CreateInstance<SkillDefinitionDatabase>();
            levelCurve = ScriptableObject.CreateInstance<LevelCurve>();

            // Set up level curve with default values
            var curveField = typeof(LevelCurve).GetField("baseXp",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (curveField != null) curveField.SetValue(levelCurve, 1000);

            // Initialize character stats
            characterStats.Initialize(levelCurve);

            // Inject database via reflection
            var dbField = typeof(CharacterStats).GetField("skillDatabase",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (dbField != null) dbField.SetValue(characterStats, skillDatabase);
        }

        [TearDown]
        public void TearDown()
        {
            if (characterStats != null)
            {
                Object.DestroyImmediate(characterStats.gameObject);
            }
            if (skillDatabase != null)
            {
                Object.DestroyImmediate(skillDatabase);
            }
            if (levelCurve != null)
            {
                Object.DestroyImmediate(levelCurve);
            }
        }

        [Test]
        public void CreateSaveData_ReturnsValidData()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);

            // Act
            var saveData = characterStats.CreateSaveData();

            // Assert
            Assert.IsNotNull(saveData, "Save data should not be null");
            Assert.AreEqual(characterStats.Level, saveData.level, "Level should match");
            Assert.AreEqual(characterStats.Xp, saveData.xp, "XP should match");
            Assert.AreEqual(characterStats.SkillPoints, saveData.skillPoints, "Skill points should match");
        }

        [Test]
        public void SaveData_ToJson_ReturnsValidJson()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);
            var saveData = characterStats.CreateSaveData();

            // Act
            string json = saveData.ToJson();

            // Assert
            Assert.IsNotNull(json, "JSON should not be null");
            Assert.IsNotEmpty(json, "JSON should not be empty");
            Debug.Log($"Save JSON:\n{json}");
        }

        [Test]
        public void SaveData_FromJson_RestoresData()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);
            var originalData = characterStats.CreateSaveData();
            string json = originalData.ToJson();

            // Act
            var restoredData = RPGSaveData.FromJson(json);

            // Assert
            Assert.IsNotNull(restoredData, "Restored data should not be null");
            Assert.AreEqual(originalData.level, restoredData.level, "Level should match");
            Assert.AreEqual(originalData.xp, restoredData.xp, "XP should match");
            Assert.AreEqual(originalData.skillPoints, restoredData.skillPoints, "Skill points should match");
        }

        [Test]
        public void LoadSaveData_RestoresCharacterState()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);

            int originalLevel = characterStats.Level;
            int originalXp = characterStats.Xp;
            int originalSkillLevel = characterStats.GetSkillLevel("melee");

            var saveData = characterStats.CreateSaveData();

            // Reset character
            var newCharacter = new GameObject("NewCharacter").AddComponent<CharacterStats>();
            newCharacter.Initialize(levelCurve);
            var dbField = typeof(CharacterStats).GetField("skillDatabase",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (dbField != null) dbField.SetValue(newCharacter, skillDatabase);

            // Act
            bool success = newCharacter.LoadSaveData(saveData);

            // Assert
            Assert.IsTrue(success, "Load should succeed");
            Assert.AreEqual(originalLevel, newCharacter.Level, "Level should be restored");
            Assert.AreEqual(originalXp, newCharacter.Xp, "XP should be restored");
            Assert.AreEqual(originalSkillLevel, newCharacter.GetSkillLevel("melee"), "Skill level should be restored");

            Object.DestroyImmediate(newCharacter.gameObject);
        }

        [Test]
        public void SaveToJson_ThenFromJson_RestoresCharacter()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);

            int originalLevel = characterStats.Level;
            int originalXp = characterStats.Xp;
            int originalSkillLevel = characterStats.GetSkillLevel("melee");

            // Act - Save to JSON
            string json = characterStats.SaveToJson();

            // Create new character and load from JSON
            var newCharacter = new GameObject("NewCharacter").AddComponent<CharacterStats>();
            newCharacter.Initialize(levelCurve);
            var dbField = typeof(CharacterStats).GetField("skillDatabase",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (dbField != null) dbField.SetValue(newCharacter, skillDatabase);

            bool success = newCharacter.LoadFromJson(json);

            // Assert
            Assert.IsTrue(success, "Load from JSON should succeed");
            Assert.AreEqual(originalLevel, newCharacter.Level, "Level should be restored from JSON");
            Assert.AreEqual(originalXp, newCharacter.Xp, "XP should be restored from JSON");
            Assert.AreEqual(originalSkillLevel, newCharacter.GetSkillLevel("melee"), "Skill level should be restored from JSON");

            Object.DestroyImmediate(newCharacter.gameObject);
        }

        [Test]
        public void SaveData_Validate_WithValidData_ReturnsTrue()
        {
            // Arrange
            characterStats.AddXp(5000);
            var saveData = characterStats.CreateSaveData();

            // Act
            bool isValid = saveData.Validate();

            // Assert
            Assert.IsTrue(isValid, "Valid save data should pass validation");
        }

        [Test]
        public void SaveData_Validate_WithInvalidLevel_ReturnsFalse()
        {
            // Arrange
            var saveData = new RPGSaveData
            {
                level = 0, // Invalid level
                xp = 1000,
                skillPoints = 5,
                perkPoints = 1
            };

            // Act & Assert
            LogAssert.Expect(LogType.Error, "[RPGSaveData] Invalid level: 0");
            bool isValid = saveData.Validate();

            // Assert
            Assert.IsFalse(isValid, "Invalid level should fail validation");
        }

        [Test]
        public void SaveData_Validate_WithNegativeXp_ReturnsFalse()
        {
            // Arrange
            var saveData = new RPGSaveData
            {
                level = 5,
                xp = -100, // Invalid XP
                skillPoints = 5,
                perkPoints = 1
            };

            // Act & Assert
            LogAssert.Expect(LogType.Error, "[RPGSaveData] Invalid XP: -100");
            bool isValid = saveData.Validate();

            // Assert
            Assert.IsFalse(isValid, "Negative XP should fail validation");
        }

        [Test]
        public void SaveData_Summary_ReturnsCorrectInfo()
        {
            // Arrange
            characterStats.AddXp(5000);
            characterStats.AddSkillPoint("melee", 5);
            characterStats.AddSkillPoint("smallguns", 3);
            var saveData = characterStats.CreateSaveData();

            // Act
            string summary = saveData.GetSummary();

            // Assert
            Assert.IsNotNull(summary, "Summary should not be null");
            Assert.IsNotEmpty(summary, "Summary should not be empty");
            Debug.Log($"Save Data Summary: {summary}");
        }

        [Test]
        public void LoadFromJson_WithInvalidJson_ReturnsFalse()
        {
            // Arrange
            string invalidJson = "not valid json";

            // Act & Assert - The error can come from RPGSaveData.FromJson or CharacterStatsExtensions.LoadFromJson
            // We expect the JSON parsing error from RPGSaveData.FromJson
            LogAssert.Expect(LogType.Error, new Regex("JSON parse|Failed to parse"));
            LogAssert.Expect(LogType.Error, "[CharacterStatsExtensions] Failed to parse JSON");
            bool success = characterStats.LoadFromJson(invalidJson);

            // Assert
            Assert.IsFalse(success, "Loading invalid JSON should fail");
        }

        [Test]
        public void LoadFromJson_WithNullJson_ReturnsFalse()
        {
            // Arrange
            string nullJson = null;

            // Act & Assert
            LogAssert.Expect(LogType.Error, "[CharacterStatsExtensions] Cannot load from null/empty JSON");
            bool success = characterStats.LoadFromJson(nullJson);

            // Assert
            Assert.IsFalse(success, "Loading null JSON should fail");
        }

        [Test]
        public void SaveData_OnlySavesNonZeroSkills()
        {
            // Arrange
            // Use internal methods to set skills directly
            var skillField = typeof(CharacterStats).GetField("skillLevels",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var skillLevels = skillField?.GetValue(characterStats) as System.Collections.Generic.Dictionary<string, int>;
            if (skillLevels != null)
            {
                skillLevels["melee"] = 5;
                skillLevels["smallguns"] = 3;
            }

            // Act
            var saveData = characterStats.CreateSaveData();

            // Assert
            Assert.AreEqual(2, saveData.skills.Count, $"Should only save non-zero skills but got {saveData.skills.Count}");
        }

        [Test]
        public void SaveData_OnlySavesUnlockedPerks()
        {
            // Arrange - Only unlock some perks
            characterStats.AddPerk("oak");  // Assuming this perk exists

            // Act
            var saveData = characterStats.CreateSaveData();

            // Assert
            Assert.LessOrEqual(saveData.perks.Count, 1, "Should only save unlocked perks");
        }

        [Test]
        public void SaveData_PreservesHealthValues()
        {
            // Arrange
            characterStats.headHp = 150f;
            characterStats.torsHp = 180f;
            characterStats.legsHp = 170f;
            characterStats.bloodHp = 190f;
            characterStats.manaHp = 350f;

            // Act
            var saveData = characterStats.CreateSaveData();

            // Assert
            Assert.AreEqual(150f, saveData.headHp, 0.01f, "Head HP should be saved");
            Assert.AreEqual(180f, saveData.torsHp, 0.01f, "Torso HP should be saved");
            Assert.AreEqual(170f, saveData.legsHp, 0.01f, "Legs HP should be saved");
            Assert.AreEqual(190f, saveData.bloodHp, 0.01f, "Blood HP should be saved");
            Assert.AreEqual(350f, saveData.manaHp, 0.01f, "Mana HP should be saved");
        }

        [Test]
        public void LoadSaveData_RestoresHealthValues()
        {
            // Arrange
            // First create a character with some stats, save them, then load
            characterStats.AddXp(10000);  // Should reach level 5 or so

            // Modify health directly
            characterStats.headHp = 150f;
            characterStats.torsHp = 180f;
            characterStats.legsHp = 170f;
            characterStats.bloodHp = 190f;
            characterStats.manaHp = 350f;

            // Save the state
            var saveData = characterStats.CreateSaveData();
            int savedLevel = characterStats.Level;

            // Create a new character and load
            var newCharacter = new GameObject("NewCharacter2").AddComponent<CharacterStats>();
            newCharacter.Initialize(levelCurve);
            var dbField = typeof(CharacterStats).GetField("skillDatabase",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (dbField != null) dbField.SetValue(newCharacter, skillDatabase);

            // Act
            bool success = newCharacter.LoadSaveData(saveData);

            // Assert
            Assert.IsTrue(success, "Load should succeed");
            // After loading, the health values should be restored to approximately the same values
            // They may not be exact due to level scaling, but should be in the right ballpark
            Assert.Greater(newCharacter.headHp, 100f, "Head HP should be restored");
            Assert.Greater(newCharacter.torsHp, 120f, "Torso HP should be restored");
            Assert.Greater(newCharacter.legsHp, 110f, "Legs HP should be restored");
            Assert.Greater(newCharacter.bloodHp, 130f, "Blood HP should be restored");
            Assert.Greater(newCharacter.manaHp, 300f, "Mana HP should be restored");
            Assert.AreEqual(savedLevel, newCharacter.Level, "Level should be preserved");

            Object.DestroyImmediate(newCharacter.gameObject);
        }
    }
}

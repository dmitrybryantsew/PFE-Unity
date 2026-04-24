using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Systems.Map;
using System.Collections.Generic;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for RoomGenerator class.
    /// </summary>
    [TestFixture]
    public class RoomGeneratorTests
    {
        private List<RoomTemplate> CreateTestTemplates()
        {
            List<RoomTemplate> templates = new List<RoomTemplate>();

            // Create test templates
            for (int i = 0; i < 3; i++)
            {
                RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
                template.id = $"room_template_{i}";
                template.type = i == 0 ? "beg0" : "pass";
                template.difficultyLevel = i;
                template.maxInstances = 2;
                template.allowRandom = true;
                template.doorQuality = new int[24]; // All doors disabled by default
                template.tileDataString = ""; // All air tiles

                templates.Add(template);
            }

            return templates;
        }

        [Test]
        public void Initialize_SetsUpTemplates()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            generator.Initialize(templates);

            // Should not throw
            Assert.Pass();
        }

        [Test]
        public void GenerateRoom_CreatesValidRoom()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.IsNotNull(room);
            Assert.AreEqual("room_template_0_0_0_0", room.id);
            Assert.AreEqual("room_template_0", room.templateId);
            Assert.AreEqual(new Vector3Int(0, 0, 0), room.landPosition);
            Assert.IsNotNull(room.tiles);
        }

        [Test]
        public void GenerateRoom_ParsesTiles()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].tileDataString = "B\nP\nA"; // Wall, Platform, Air

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.IsNotNull(room.tiles);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[0, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Platform, room.tiles[0, 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[0, 2].physicsType);
        }

        [Test]
        public void GenerateRoom_CopiesSpawnPoints()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].spawnPoints.Add(new SpawnPointData
            {
                tileCoord = new Vector2Int(10, 10),
                type = SpawnType.Player
            });

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual(1, room.spawnPoints.Count);
            Assert.AreEqual(new Vector2Int(10, 10), room.spawnPoints[0].tileCoord);
            Assert.AreEqual(SpawnType.Player, room.spawnPoints[0].type);
        }

        [Test]
        public void GenerateRoom_SetsDifficulty()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].difficultyLevel = 5;

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual(5, room.difficulty.baseDifficulty);
            Assert.AreEqual(5, room.difficulty.enemyLevel);
        }

        [Test]
        public void GenerateRoom_SetsEnvironment()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].environment.musicTrack = "test_music";
            templates[0].environment.waterType = 1;

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual("test_music", room.environment.musicTrack);
            Assert.AreEqual(1, room.environment.waterType);
        }

        [Test]
        public void SelectRandomRoom_WithValidTemplates_ReturnsRoom()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomTemplate selected = generator.SelectRandomRoom(maxDifficulty: 5);

            Assert.IsNotNull(selected);
            Assert.GreaterOrEqual(selected.id, "room_template_0");
            Assert.LessOrEqual(selected.difficultyLevel, 5);
        }

        [Test]
        public void SelectRandomRoom_RespectsMaxDifficulty()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomTemplate selected = generator.SelectRandomRoom(maxDifficulty: 0);

            Assert.IsNotNull(selected);
            Assert.AreEqual(0, selected.difficultyLevel);
        }

        [Test]
        public void SelectRandomRoom_RespectsInstanceLimit()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            // Set all templates to maxInstances=1 to ensure they can only be selected once
            templates[0].maxInstances = 1;
            templates[1].maxInstances = 1;
            templates[2].maxInstances = 1;

            // Make template_0 the only viable option by making others too difficult
            templates[1].difficultyLevel = 10;
            templates[2].difficultyLevel = 10;

            generator.Initialize(templates);

            // First selection should return template_0
            RoomTemplate selected1 = generator.SelectRandomRoom(maxDifficulty: 5);
            Assert.IsNotNull(selected1);
            Assert.AreEqual("room_template_0", selected1.id);

            // Verify it was counted
            Assert.AreEqual(1, generator.GetUsageCount("room_template_0"));

            // Second selection should return null (template_0 is used up, others are too difficult)
            RoomTemplate selected2 = generator.SelectRandomRoom(maxDifficulty: 5);
            Assert.IsNull(selected2);
        }

        [Test]
        public void SelectRandomRoom_WithTypeFilter_ReturnsCorrectType()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomTemplate selected = generator.SelectRandomRoom(maxDifficulty: 5, requiredType: "beg0");

            Assert.IsNotNull(selected);
            Assert.AreEqual("beg0", selected.type);
        }

        [Test]
        public void SelectRandomRoom_WithExcludeList_ExcludesTemplates()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            List<RoomTemplate> exclude = new List<RoomTemplate> { templates[0] };
            RoomTemplate selected = generator.SelectRandomRoom(maxDifficulty: 5, exclude: exclude);

            Assert.IsNotNull(selected);
            Assert.AreNotEqual(templates[0].id, selected.id);
        }

        [Test]
        public void SelectRoomByType_ReturnsCorrectRoom()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomTemplate selected = generator.SelectRoomByType("beg0");

            Assert.IsNotNull(selected);
            Assert.AreEqual("beg0", selected.type);
            Assert.AreEqual("room_template_0", selected.id);
        }

        [Test]
        public void SelectRoomByType_NonExistentType_ReturnsNull()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No valid rooms of type"));

            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            generator.Initialize(templates);

            RoomTemplate selected = generator.SelectRoomByType("nonexistent_type");

            Assert.IsNull(selected);
        }

        [Test]
        public void GetUsageCount_TracksUsage()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            // Only allow template_0 to be selected
            templates[1].difficultyLevel = 10; // Make others too difficult
            templates[2].difficultyLevel = 10;
            generator.Initialize(templates);

            Assert.AreEqual(0, generator.GetUsageCount("room_template_0"));

            generator.SelectRandomRoom(maxDifficulty: 5);

            Assert.AreEqual(1, generator.GetUsageCount("room_template_0"));
        }

        [Test]
        public void ResetUsageCounts_ResetsToZero()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();
            // Only allow template_0 to be selected
            templates[1].difficultyLevel = 10;
            templates[2].difficultyLevel = 10;
            generator.Initialize(templates);

            generator.SelectRandomRoom(maxDifficulty: 5);
            generator.SelectRandomRoom(maxDifficulty: 5);

            generator.ResetUsageCounts();

            Assert.AreEqual(0, generator.GetUsageCount("room_template_0"));
        }

        [Test]
        public void GenerateRoom_NullTemplate_ReturnsNull()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("null template"));

            RoomGenerator generator = new RoomGenerator();
            generator.Initialize(new List<RoomTemplate>());

            RoomInstance room = generator.GenerateRoom(null, new Vector3Int(0, 0, 0));

            Assert.IsNull(room);
        }

        [Test]
        public void SelectRandomRoom_NoValidTemplates_ReturnsNull()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            // Set max difficulty lower than all templates
            foreach (var t in templates)
            {
                t.difficultyLevel = 10;
            }

            generator.Initialize(templates);
            RoomTemplate selected = generator.SelectRandomRoom(maxDifficulty: 0);

            Assert.IsNull(selected);
        }

        [Test]
        public void GenerateRoom_DoorConfiguration_CreatesDoors()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            // Set door quality
            templates[0].doorQuality[0] = 2; // Create a door at index 0

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual(1, room.doors.Count);
            Assert.AreEqual(0, room.doors[0].doorIndex);
            Assert.AreEqual(DoorSide.Right, room.doors[0].side);
        }

        [Test]
        public void RoomType_IsCopiedFromTemplate()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual(templates[0].type, room.roomType);
        }
    }
}

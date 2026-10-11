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
        /// <summary>
        /// The project's real tile form database. <see cref="RoomGenerator.GenerateRoom"/> decodes
        /// <see cref="RoomTemplate.tileDataString"/> through it, so constructing the generator with
        /// <c>null</c> makes any template with tile data throw inside <see cref="TileDecoder.Decode"/>.
        /// </summary>
        private static TileFormDatabase LoadTileFormDatabase()
        {
            TileFormDatabase database = UnityEditor.AssetDatabase
                .LoadAssetAtPath<TileFormDatabase>("Assets/_PFE/Data/TileFormDatabase.asset");

            Assert.NotNull(database, "Expected the tile form database at Assets/_PFE/Data/TileFormDatabase.asset");
            return database;
        }

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
            RoomGenerator generator = new RoomGenerator(LoadTileFormDatabase());
            List<RoomTemplate> templates = CreateTestTemplates();

            // Real tile codes: 'B' is a wall fForm, "_-" is air + the '-' shelf overlay, "." is air.
            // Row 0 of the template is the TOP row, stored at the highest Unity Y.
            templates[0].tileDataString = "B\n_-\n.";

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            int topRow = WorldConstants.ROOM_HEIGHT - 1;
            Assert.IsNotNull(room.tiles);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[0, topRow].physicsType);
            Assert.AreEqual(TilePhysicsType.Platform, room.tiles[0, topRow - 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[0, topRow - 2].physicsType);
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
        public void GenerateRoom_DoorConfiguration_CandidatesStartInactive()
        {
            // A template's doorQuality is a CANDIDATE MASK, not the door set. AS3 matches the shared wall
            // (min(a[i], b[i+11]) >= 2, Land.as:426/:443) and then carves a DRAW from that list — three
            // slots with replacement on the right, one on the lower side (Land.as:471-488).
            //
            // So a freshly generated room must have every slot INACTIVE; the draw activates the few.
            // Pre-activating them here is what made the carver open the entire mask and gave the player a
            // walk-through trigger on every wall (RoomVisualController.EnsureBoundaryDoorTriggers skips
            // inactive doors). Measured before the fix: mean 17.4 of 24 slots carved per room in
            // rooms_plant, against roughly 4-8 in the oracle. See 06_DOOR_CARVE_AUDIT.md.
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].doorQuality[0] = 2;
            templates[0].doorQuality[6] = 3;

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            Assert.AreEqual(2, room.doors.Count, "both candidates must still be created");
            foreach (DoorInstance door in room.doors)
            {
                Assert.IsFalse(door.isActive,
                    $"slot {door.doorIndex} is only a candidate and must start inactive");
            }
        }

        [Test]
        public void GenerateRoom_DoorConfiguration_UsesUnityTileCoordinates()
        {
            RoomGenerator generator = new RoomGenerator();
            List<RoomTemplate> templates = CreateTestTemplates();

            templates[0].doorQuality[6] = 2;  // AS3 bottom side
            templates[0].doorQuality[11] = 2; // AS3 left side
            templates[0].doorQuality[17] = 2; // AS3 top side

            generator.Initialize(templates);
            RoomInstance room = generator.GenerateRoom(templates[0], new Vector3Int(0, 0, 0));

            DoorInstance bottom = room.doors.Find(door => door.doorIndex == 6);
            DoorInstance left = room.doors.Find(door => door.doorIndex == 11);
            DoorInstance top = room.doors.Find(door => door.doorIndex == 17);

            Assert.NotNull(bottom);
            Assert.NotNull(left);
            Assert.NotNull(top);
            Assert.AreEqual(DoorSide.Bottom, bottom.side);
            Assert.AreEqual(new Vector2Int(4, 0), bottom.tilePosition);
            Assert.AreEqual(DoorSide.Left, left.side);
            // AS3 Location.setDoor(): left row = (index - 11) * 4 + 3 = 3, counted from the TOP,
            // so the Unity row is ROOM_HEIGHT - 1 - 3 = 21 (was 23 while ROOM_HEIGHT was still 27).
            Assert.AreEqual(new Vector2Int(0, 21), left.tilePosition);
            Assert.AreEqual(DoorSide.Top, top.side);
            Assert.AreEqual(new Vector2Int(4, WorldConstants.ROOM_HEIGHT - 1), top.tilePosition);
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

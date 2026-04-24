using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Data;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for RoomTemplate class.
    /// </summary>
    [TestFixture]
    public class RoomTemplateTests
    {
        [Test]
        public void RoomTemplate_DefaultValues_AreCorrect()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();

            Assert.AreEqual(-1, template.fixedPosition.x);
            Assert.AreEqual(-1, template.fixedPosition.y);
            Assert.AreEqual(-1, template.fixedPosition.z);
            Assert.AreEqual(0, template.difficultyLevel);
            Assert.AreEqual(2, template.maxInstances);
            Assert.IsTrue(template.allowRandom);
            Assert.AreEqual(24, template.doorQuality.Length);
        }

        [Test]
        public void IsStartingRoom_Beg0_ReturnsTrue()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "beg0";

            Assert.IsTrue(template.IsStartingRoom());
        }

        [Test]
        public void IsStartingRoom_Beg1_ReturnsTrue()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "beg1";

            Assert.IsTrue(template.IsStartingRoom());
        }

        [Test]
        public void IsStartingRoom_Pass_ReturnsFalse()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "pass";

            Assert.IsFalse(template.IsStartingRoom());
        }

        [Test]
        public void IsEndingRoom_End_ReturnsTrue()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "end";

            Assert.IsTrue(template.IsEndingRoom());
        }

        [Test]
        public void IsEndingRoom_End1_ReturnsTrue()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "end1";

            Assert.IsTrue(template.IsEndingRoom());
        }

        [Test]
        public void IsEndingRoom_Pass_ReturnsFalse()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.type = "pass";

            Assert.IsFalse(template.IsEndingRoom());
        }

        [Test]
        public void ParseTiles_EmptyString_CreatesAllAir()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.IsNotNull(tiles);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH, tiles.GetLength(0));
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, tiles.GetLength(1));
        }

        [Test]
        public void ParseTiles_WallChar_CreatesWallTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "B";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Wall, tiles[0, 0].physicsType);
            Assert.AreEqual("tWall1", tiles[0, 0].GetFrontGraphic());
        }

        [Test]
        public void ParseTiles_AirChar_CreatesAirTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "A";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Air, tiles[0, 0].physicsType);
        }

        [Test]
        public void ParseTiles_PlatformChar_CreatesPlatformTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "P";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Platform, tiles[0, 0].physicsType);
            Assert.IsTrue(tiles[0, 0].IsPlatform());
        }

        [Test]
        public void ParseTiles_StairChar_CreatesStairTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "S";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Stair, tiles[0, 0].physicsType);
            Assert.IsTrue(tiles[0, 0].IsStair());
        }

        [Test]
        public void ParseTiles_SlopeUpChar_CreatesSlopedTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "/";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Stair, tiles[0, 0].physicsType);
            Assert.AreEqual(1, tiles[0, 0].slopeType);
        }

        [Test]
        public void ParseTiles_SlopeDownChar_CreatesSlopedTile()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "\\";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Stair, tiles[0, 0].physicsType);
            Assert.AreEqual(-1, tiles[0, 0].slopeType);
        }

        [Test]
        public void ParseTiles_MultipleRows_ParsesCorrectly()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "AAA\nBBB\nXXX";

            TileData[,] tiles = template.ParseTiles(null);

            // Row 0: AAA
            Assert.AreEqual(TilePhysicsType.Air, tiles[0, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, tiles[1, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, tiles[2, 0].physicsType);

            // Row 1: BBB
            Assert.AreEqual(TilePhysicsType.Wall, tiles[0, 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, tiles[1, 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, tiles[2, 1].physicsType);

            // Row 2: XXX (unknown char defaults to Air)
            Assert.AreEqual(TilePhysicsType.Air, tiles[0, 2].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, tiles[1, 2].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, tiles[2, 2].physicsType);
        }

        [Test]
        public void ParseTiles_PreservesGridPositions()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "ABC\nDEF";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(0, tiles[0, 0].gridPosition.x);
            Assert.AreEqual(0, tiles[0, 0].gridPosition.y);

            Assert.AreEqual(1, tiles[1, 0].gridPosition.x);
            Assert.AreEqual(0, tiles[1, 0].gridPosition.y);

            Assert.AreEqual(0, tiles[0, 1].gridPosition.x);
            Assert.AreEqual(1, tiles[0, 1].gridPosition.y);
        }

        [Test]
        public void ObjectSpawnData_DefaultValues()
        {
            ObjectSpawnData data = new ObjectSpawnData();

            Assert.IsNull(data.type);
            Assert.IsNull(data.id);
            Assert.IsNull(data.definitionId);
            Assert.IsNull(data.definition);
            Assert.AreEqual(new Vector2Int(0, 0), data.tileCoord);
            Assert.IsNull(data.code);
            Assert.IsNull(data.uid);
            Assert.IsNotNull(data.attributes);
            Assert.IsNotNull(data.items);
            Assert.IsNotNull(data.scripts);
            Assert.IsNull(data.parameters);
        }

        [Test]
        public void ObjectSpawnData_EnsureStructuredData_ParsesLegacyParameters()
        {
            ObjectSpawnData data = new ObjectSpawnData
            {
                parameters = "code=\"abc123\" uid=\"doorA\" light=\"1\" lock=\"2.5\""
            };

            data.EnsureStructuredData();

            Assert.AreEqual("abc123", data.code);
            Assert.AreEqual("doorA", data.uid);
            Assert.AreEqual("1", data.GetAttribute("light"));
            Assert.AreEqual("2.5", data.GetAttribute("lock"));
        }

        [Test]
        public void ObjectSpawnData_GetResolvedDefinitionId_UsesAssignedDefinition()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "safe";

            ObjectSpawnData data = new ObjectSpawnData
            {
                id = "legacy_safe",
                definitionId = "placeholder",
                definition = definition
            };

            Assert.AreEqual("safe", data.GetResolvedDefinitionId());
        }

        [Test]
        public void SpawnPointData_DefaultValues()
        {
            SpawnPointData data = new SpawnPointData();

            Assert.AreEqual(new Vector2Int(0, 0), data.tileCoord);
            // type is an enum, default value is first value (Player)
            Assert.AreEqual(SpawnType.Player, data.type);
            // unitId has default value "" not null
            Assert.IsEmpty(data.unitId);
            Assert.AreEqual(1f, data.facingDirection);
        }

        [Test]
        public void RoomEnvironmentData_DefaultValues()
        {
            RoomEnvironmentData data = new RoomEnvironmentData();

            Assert.AreEqual("", data.musicTrack);
            Assert.AreEqual(0, data.waterType);
            Assert.AreEqual(100, data.waterLevel);
            Assert.IsFalse(data.hasSky);
        }

        [Test]
        public void ParseTiles_RussianCharacters_ParseCorrectly()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "БА";  // Russian Б (wall) and А (platform)

            TileData[,] tiles = template.ParseTiles(null);

            // Russian Б should be Wall
            Assert.AreEqual(TilePhysicsType.Wall, tiles[0, 0].physicsType);
            Assert.IsTrue(tiles[0, 0].indestructible);

            // Russian А should be Platform
            Assert.AreEqual(TilePhysicsType.Platform, tiles[1, 0].physicsType);
        }

        [Test]
        public void ParseTiles_UnderscoreAndDot_ParsesAsAir()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "._";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Air, tiles[0, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, tiles[1, 0].physicsType);
        }

        [Test]
        public void ParseTiles_CHaracter_ParsesAsWall()
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "CCC";

            TileData[,] tiles = template.ParseTiles(null);

            Assert.AreEqual(TilePhysicsType.Wall, tiles[0, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, tiles[1, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, tiles[2, 0].physicsType);
        }

        // TODO: Add comprehensive tests for TileFormDatabase integration
        // These tests should verify that ParseTiles correctly resolves tile forms
        // from the database and applies their properties (physics, visuals, etc.)
        /*
        [Test]
        public void ParseTiles_WithFormDatabase_ResolvesTileForms()
        {
            // Arrange
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.tileDataString = "ABC"; // A, B, C are tile form IDs
            
            TileFormDatabase formDb = ScriptableObject.CreateInstance<TileFormDatabase>();
            // Add test tile forms to database
            // formDb.AddFForm(new TileForm { id = "A", ... });
            // formDb.AddFForm(new TileForm { id = "B", ... });
            // formDb.AddFForm(new TileForm { id = "C", ... });
            formDb.Initialize();
            
            // Act
            TileData[,] tiles = template.ParseTiles(formDb);
            
            // Assert
            // Verify tiles have correct properties from tile forms
            Assert.AreEqual("A", tiles[0, 0].formId);
            // Add more assertions for physics type, visual ID, etc.
        }
        */

        // GameDatabase Integration Tests

        [Test]
        public void GameDatabase_RegisterRoomTemplate_ValidTemplate_RegistersSuccessfully()
        {
            GameDatabase db = new GameDatabase();
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.id = "test_room_01";
            template.type = "pass";

            db.RegisterRoomTemplate(template);

            Assert.AreSame(template, db.GetRoomTemplate("test_room_01"));
        }

        [Test]
        public void GameDatabase_RegisterRoomTemplate_Null_DoesNotRegister()
        {
            GameDatabase db = new GameDatabase();

            // Should not throw exception
            db.RegisterRoomTemplate(null);

            Assert.IsNull(db.GetRoomTemplate("null"));
        }

        [Test]
        public void GameDatabase_RegisterRoomTemplate_EmptyId_DoesNotRegister()
        {
            GameDatabase db = new GameDatabase();
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.id = "";
            template.type = "pass";

            db.RegisterRoomTemplate(template);

            // Should not be registered with empty string ID
            Assert.IsNull(db.GetRoomTemplate(""));
        }

        [Test]
        public void GameDatabase_RegisterRoomTemplate_DuplicateId_DoesNotOverwrite()
        {
            GameDatabase db = new GameDatabase();
            RoomTemplate template1 = ScriptableObject.CreateInstance<RoomTemplate>();
            template1.id = "duplicate_room";
            template1.type = "pass";

            RoomTemplate template2 = ScriptableObject.CreateInstance<RoomTemplate>();
            template2.id = "duplicate_room";
            template2.type = "beg0";

            db.RegisterRoomTemplate(template1);
            db.RegisterRoomTemplate(template2);

            // Should still be the first template
            RoomTemplate retrieved = db.GetRoomTemplate("duplicate_room");
            Assert.AreSame(template1, retrieved);
            Assert.AreEqual("pass", retrieved.type);
        }

        [Test]
        public void GameDatabase_GetRoomTemplate_InvalidId_ReturnsNull()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template = db.GetRoomTemplate("non_existent_room");

            Assert.IsNull(template);
        }

        [Test]
        public void GameDatabase_GetRoomTemplate_NullId_ReturnsNull()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template = db.GetRoomTemplate(null);

            Assert.IsNull(template);
        }

        [Test]
        public void GameDatabase_GetRoomTemplate_EmptyId_ReturnsNull()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template = db.GetRoomTemplate("");

            Assert.IsNull(template);
        }

        [Test]
        public void GameDatabase_GetAllRoomTemplateIDs_EmptyDatabase_ReturnsEmpty()
        {
            GameDatabase db = new GameDatabase();

            var ids = db.GetAllRoomTemplateIDs();

            Assert.IsNotNull(ids);
            Assert.IsEmpty(ids);
        }

        [Test]
        public void GameDatabase_GetAllRoomTemplateIDs_MultipleTemplates_ReturnsAllIds()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template1 = ScriptableObject.CreateInstance<RoomTemplate>();
            template1.id = "room_01";
            template1.type = "pass";

            RoomTemplate template2 = ScriptableObject.CreateInstance<RoomTemplate>();
            template2.id = "room_02";
            template2.type = "beg0";

            RoomTemplate template3 = ScriptableObject.CreateInstance<RoomTemplate>();
            template3.id = "room_03";
            template3.type = "roof";

            db.RegisterRoomTemplate(template1);
            db.RegisterRoomTemplate(template2);
            db.RegisterRoomTemplate(template3);

            var ids = db.GetAllRoomTemplateIDs();

            Assert.AreEqual(3, new System.Collections.Generic.List<string>(ids).Count);
        }

        [Test]
        public void GameDatabase_GetRoomTemplatesByType_NoMatchingType_ReturnsEmptyList()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.id = "test_room";
            template.type = "pass";

            db.RegisterRoomTemplate(template);

            var rooms = db.GetRoomTemplatesByType("beg0");

            Assert.IsNotNull(rooms);
            Assert.IsEmpty(rooms);
        }

        [Test]
        public void GameDatabase_GetRoomTemplatesByType_MatchingTypes_ReturnsCorrectRooms()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template1 = ScriptableObject.CreateInstance<RoomTemplate>();
            template1.id = "room_beg_01";
            template1.type = "beg0";

            RoomTemplate template2 = ScriptableObject.CreateInstance<RoomTemplate>();
            template2.id = "room_beg_02";
            template2.type = "beg0";

            RoomTemplate template3 = ScriptableObject.CreateInstance<RoomTemplate>();
            template3.id = "room_pass_01";
            template3.type = "pass";

            db.RegisterRoomTemplate(template1);
            db.RegisterRoomTemplate(template2);
            db.RegisterRoomTemplate(template3);

            var begRooms = db.GetRoomTemplatesByType("beg0");

            Assert.AreEqual(2, begRooms.Count);
            Assert.Contains(template1, begRooms);
            Assert.Contains(template2, begRooms);
        }

        [Test]
        public void GameDatabase_GetAllRoomTemplates_MultipleTemplates_ReturnsAll()
        {
            GameDatabase db = new GameDatabase();

            RoomTemplate template1 = ScriptableObject.CreateInstance<RoomTemplate>();
            template1.id = "room_01";
            template1.type = "pass";

            RoomTemplate template2 = ScriptableObject.CreateInstance<RoomTemplate>();
            template2.id = "room_02";
            template2.type = "beg0";

            db.RegisterRoomTemplate(template1);
            db.RegisterRoomTemplate(template2);

            var allTemplates = db.GetAllRoomTemplates();

            var templateList = new System.Collections.Generic.List<RoomTemplate>(allTemplates);
            Assert.AreEqual(2, templateList.Count);
        }

        [Test]
        public void GameDatabase_GetAllRoomTemplates_EmptyDatabase_ReturnsEmpty()
        {
            GameDatabase db = new GameDatabase();

            var allTemplates = db.GetAllRoomTemplates();

            var templateList = new System.Collections.Generic.List<RoomTemplate>(allTemplates);
            Assert.AreEqual(0, templateList.Count);
        }

        [Test]
        public void GameDatabase_RegisterMapObjectDefinition_ValidDefinition_RegistersSuccessfully()
        {
            GameDatabase db = new GameDatabase();
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "safe";

            db.RegisterMapObjectDefinition(definition);

            Assert.AreSame(definition, db.GetMapObjectDefinition("safe"));
        }
    }
}

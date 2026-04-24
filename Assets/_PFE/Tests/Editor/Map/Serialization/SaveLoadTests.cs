using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using System;
using System.Collections.Generic;
using System.IO;

namespace PFE.Tests.Map.Serialization
{
    /// <summary>
    /// Tests for Save/Load system.
    /// </summary>
    public class SaveLoadTests
    {
        private string testSavePath;
        private LandMap testMap;
        private RoomInstance testRoom;

        [SetUp]
        public void SetUp()
        {
            // Create test save directory
            testSavePath = Path.Combine(Application.persistentDataPath, "TestSaves");
            Directory.CreateDirectory(testSavePath);

            // Create test map
            testMap = new LandMap();
            testMap.Initialize(new Vector3Int(0, 0, 0), new Vector3Int(5, 5, 2));

            // Create test room
            testRoom = new RoomInstance
            {
                id = "test_room_1",
                templateId = "template_1",
                landPosition = new Vector3Int(0, 0, 0),
                roomType = "test"
            };
            testRoom.InitializeTiles();

            // Add some test tiles
            testRoom.tiles[5, 5].physicsType = TilePhysicsType.Wall;
            testRoom.tiles[5, 5].hitPoints = 500;
            testRoom.tiles[5, 5].SetFrontGraphic("wall_brick");

            // Add test door
            testRoom.doors.Add(new DoorInstance
            {
                doorIndex = 0,
                side = DoorSide.Right,
                tilePosition = new Vector2Int(10, 5),
                targetRoomPosition = new Vector3Int(1, 0, 0),
                targetDoorIndex = 0,
                quality = DoorQuality.Normal
            });

            // Add test spawn point
            testRoom.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = new Vector2Int(5, 5),
                type = SpawnType.Player,
                unitId = "player_1"
            });

            // Add test unit
            testRoom.units.Add(new UnitInstance
            {
                unitId = "enemy_1",
                unitType = "grunt",
                position = new Vector2(200, 150),
                currentHealth = 100,
                maxHealth = 100
            });

            // Add test object
            testRoom.objects.Add(new ObjectInstance
            {
                objectId = "item_1",
                objectType = "health_pack",
                definitionId = "safe",
                definition = ScriptableObject.CreateInstance<MapObjectDefinition>(),
                code = "obj_code_1",
                uid = "obj_uid_1",
                attributes = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "light", value = "1" },
                    new MapObjectAttributeData { key = "lock", value = "2.5" }
                },
                items = new List<MapObjectItemData>
                {
                    new MapObjectItemData
                    {
                        id = "stimpak",
                        attributes = new List<MapObjectAttributeData>
                        {
                            new MapObjectAttributeData { key = "imp", value = "1" }
                        }
                    }
                },
                scripts = new List<MapObjectScriptData>
                {
                    new MapObjectScriptData
                    {
                        eventName = "open",
                        actions = new List<MapObjectScriptActionData>
                        {
                            new MapObjectScriptActionData { act = "off", targ = "door_a", val = "1" }
                        }
                    }
                },
                runtimeState = new MapObjectRuntimeStateData
                {
                    isOpen = true,
                    lootState = 2,
                    hasLockValue = true,
                    lockValue = 1.5f,
                    dynamicState = new MapObjectDynamicStateData
                    {
                        isDynamic = true,
                        isThrown = true,
                        velocity = new Vector2(80f, 120f),
                        throwGraceTime = 0.15f
                    }
                },
                position = new Vector2(250, 150)
            });
            testRoom.objects[0].definition.objectId = "safe";
            testRoom.objects[0].RefreshLegacyParameters();

            // Add room to map
            testMap.AddRoom(testRoom, new Vector3Int(0, 0, 0));
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test saves
            if (Directory.Exists(testSavePath))
            {
                Directory.Delete(testSavePath, recursive: true);
            }
        }

        [Test]
        public void RoomStateSnapshot_CreateFromRoom_CapturesAllData()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot);
            Assert.AreEqual("test_room_1", snapshot.roomId);
            Assert.AreEqual("template_1", snapshot.templateId);
            Assert.AreEqual(new Vector3Int(0, 0, 0), snapshot.landPosition);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH, snapshot.width);
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, snapshot.height);
            Assert.AreEqual("test", snapshot.roomType);
        }

        [Test]
        public void RoomStateSnapshot_TileData_IsSerialized()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot.tiles);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH * WorldConstants.ROOM_HEIGHT, snapshot.tiles.Length);

            // Find the wall tile we added
            TileStateSnapshot wallTile = null;
            for (int i = 0; i < snapshot.tiles.Length; i++)
            {
                if (snapshot.tiles[i] != null && snapshot.tiles[i].gridX == 5 && snapshot.tiles[i].gridY == 5)
                {
                    wallTile = snapshot.tiles[i];
                    break;
                }
            }

            Assert.NotNull(wallTile);
            Assert.AreEqual((int)TilePhysicsType.Wall, wallTile.physicsType);
            Assert.AreEqual(500, wallTile.hitPoints);
            Assert.AreEqual("wall_brick", wallTile.frontGraphic);
        }

        [Test]
        public void RoomStateSnapshot_Doors_AreSerialized()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot.doors);
            Assert.AreEqual(1, snapshot.doors.Length);
            Assert.AreEqual(0, snapshot.doors[0].doorIndex);
            Assert.AreEqual((int)DoorSide.Right, snapshot.doors[0].side);
            Assert.AreEqual((int)DoorQuality.Normal, snapshot.doors[0].quality);
        }

        [Test]
        public void RoomStateSnapshot_SpawnPoints_AreSerialized()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot.spawnPoints);
            Assert.AreEqual(1, snapshot.spawnPoints.Length);
            Assert.AreEqual(5, snapshot.spawnPoints[0].tileX);
            Assert.AreEqual(5, snapshot.spawnPoints[0].tileY);
            Assert.AreEqual((int)SpawnType.Player, snapshot.spawnPoints[0].spawnType);
        }

        [Test]
        public void RoomStateSnapshot_Units_AreSerialized()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot.units);
            Assert.AreEqual(1, snapshot.units.Length);
            Assert.AreEqual("enemy_1", snapshot.units[0].unitId);
            Assert.AreEqual("grunt", snapshot.units[0].unitType);
            Assert.AreEqual(200f, snapshot.units[0].posX, 0.01f);
            Assert.AreEqual(150f, snapshot.units[0].posY, 0.01f);
            Assert.AreEqual(100f, snapshot.units[0].currentHealth, 0.01f);
            Assert.AreEqual(100f, snapshot.units[0].maxHealth, 0.01f);
        }

        [Test]
        public void RoomStateSnapshot_Objects_AreSerialized()
        {
            // Act
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);

            // Assert
            Assert.NotNull(snapshot.objects);
            Assert.AreEqual(1, snapshot.objects.Length);
            Assert.AreEqual("item_1", snapshot.objects[0].objectId);
            Assert.AreEqual("health_pack", snapshot.objects[0].objectType);
            Assert.AreEqual("safe", snapshot.objects[0].definitionId);
            Assert.AreEqual("obj_code_1", snapshot.objects[0].code);
            Assert.AreEqual("obj_uid_1", snapshot.objects[0].uid);
            Assert.AreEqual(2, snapshot.objects[0].attributes.Length);
            Assert.AreEqual(1, snapshot.objects[0].items.Length);
            Assert.AreEqual(1, snapshot.objects[0].scripts.Length);
            Assert.IsTrue(snapshot.objects[0].runtimeState.isOpen);
            Assert.IsTrue(snapshot.objects[0].runtimeState.dynamicState.isDynamic);
            Assert.IsTrue(snapshot.objects[0].runtimeState.dynamicState.isThrown);
            Assert.AreEqual(80f, snapshot.objects[0].runtimeState.dynamicState.velocityX, 0.01f);
        }

        [Test]
        public void RoomStateSnapshot_RestoreToRoom_RestoresData()
        {
            // Arrange
            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(testRoom);
            RoomInstance newRoom = new RoomInstance
            {
                id = "new_room",
                landPosition = new Vector3Int(1, 1, 1)
            };
            newRoom.InitializeTiles();

            // Act
            snapshot.RestoreToRoom(newRoom);

            // Assert
            Assert.AreEqual("test_room_1", newRoom.id);
            Assert.AreEqual("template_1", newRoom.templateId);
            Assert.AreEqual(new Vector3Int(0, 0, 0), newRoom.landPosition);
            Assert.AreEqual(1, newRoom.doors.Count);
            Assert.AreEqual(1, newRoom.spawnPoints.Count);
            Assert.AreEqual(1, newRoom.units.Count);
            Assert.AreEqual(1, newRoom.objects.Count);
            Assert.AreEqual("safe", newRoom.objects[0].definitionId);
            Assert.AreEqual("obj_code_1", newRoom.objects[0].code);
            Assert.AreEqual("obj_uid_1", newRoom.objects[0].uid);
            Assert.AreEqual("1", newRoom.objects[0].GetAttribute("light"));
            Assert.AreEqual(1, newRoom.objects[0].items.Count);
            Assert.AreEqual(1, newRoom.objects[0].scripts.Count);
            Assert.IsTrue(newRoom.objects[0].runtimeState.isOpen);
            Assert.IsTrue(newRoom.objects[0].runtimeState.dynamicState.isDynamic);
            Assert.IsTrue(newRoom.objects[0].runtimeState.dynamicState.isThrown);
            Assert.AreEqual(120f, newRoom.objects[0].runtimeState.dynamicState.velocity.y, 0.01f);
            Assert.AreEqual(1, newRoom.ObjectPhysicsLayer.DynamicObjectCount);
        }

        [Test]
        public void WorldSaveData_CreateFromMap_CreatesValidData()
        {
            // Arrange
            PlayerStateSnapshot playerState = new PlayerStateSnapshot
            {
                posX = 100,
                posY = 200,
                roomX = 0,
                roomY = 0,
                roomZ = 0,
                health = 100,
                maxHealth = 100
            };

            // Act
            WorldSaveData saveData = WorldSaveData.CreateFromMap(testMap, playerState);

            // Assert
            Assert.NotNull(saveData);
            Assert.NotNull(saveData.saveId);
            Assert.Greater(saveData.timestamp, 0);
            Assert.AreEqual(1, saveData.rooms.Length);
        }

        [Test]
        public void WorldSerializer_SerializeToJson_ProducesValidJson()
        {
            // Arrange
            PlayerStateSnapshot playerState = new PlayerStateSnapshot();
            WorldSaveData saveData = WorldSaveData.CreateFromMap(testMap, playerState);

            // Act
            string json = WorldSerializer.SerializeToJson(saveData);

            // Assert
            Assert.NotNull(json);
            Assert.Greater(json.Length, 0);
            Assert.IsTrue(json.Contains("\"saveId\""));
            Assert.IsTrue(json.Contains("\"rooms\""));
        }

        [Test]
        public void WorldDeserializer_DeserializeFromJson_RestoresData()
        {
            // Arrange
            PlayerStateSnapshot playerState = new PlayerStateSnapshot();
            WorldSaveData originalData = WorldSaveData.CreateFromMap(testMap, playerState);
            string json = WorldSerializer.SerializeToJson(originalData);

            // Act
            WorldSaveData restoredData = WorldDeserializer.DeserializeFromJson(json);

            // Assert
            Assert.NotNull(restoredData);
            Assert.AreEqual(originalData.saveId, restoredData.saveId);
            Assert.AreEqual(originalData.rooms.Length, restoredData.rooms.Length);
            Assert.AreEqual(originalData.minX, restoredData.minX);
        }

        [Test]
        public void TileStateSnapshot_SerializesAllProperties()
        {
            // Arrange
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(10, 20),
                physicsType = TilePhysicsType.Platform,
                indestructible = true,
                hitPoints = 2000,
                damageThreshold = 100,
                heightLevel = 2,
                slopeType = -1,
                stairType = 1,
                isLedge = true,
                hasWater = true,
                material = MaterialType.Metal,
                doorId = "door_123",
                trapId = "trap_456"
            };
            tile.SetFrontGraphic("test_front");
            tile.SetBackGraphic("test_back");

            // Act
            TileStateSnapshot snapshot = TileStateSnapshot.CreateFrom(tile);

            // Assert
            Assert.NotNull(snapshot);
            Assert.AreEqual(10, snapshot.gridX);
            Assert.AreEqual(20, snapshot.gridY);
            Assert.AreEqual((int)TilePhysicsType.Platform, snapshot.physicsType);
            Assert.IsTrue(snapshot.indestructible);
            Assert.AreEqual(2000, snapshot.hitPoints);
            Assert.AreEqual(100, snapshot.damageThreshold);
            Assert.AreEqual(2, snapshot.heightLevel);
            Assert.AreEqual(-1, snapshot.slopeType);
            Assert.AreEqual(1, snapshot.stairType);
            Assert.IsTrue(snapshot.isLedge);
            Assert.IsTrue(snapshot.hasWater);
            Assert.AreEqual((int)MaterialType.Metal, snapshot.material);
            Assert.AreEqual("door_123", snapshot.doorId);
            Assert.AreEqual("trap_456", snapshot.trapId);
            Assert.AreEqual("test_front", snapshot.frontGraphic);
            Assert.AreEqual("test_back", snapshot.backGraphic);
        }

        [Test]
        public void TileStateSnapshot_RestoreTo_RestoresAllProperties()
        {
            // Arrange
            TileStateSnapshot snapshot = new TileStateSnapshot
            {
                gridX = 15,
                gridY = 25,
                physicsType = (int)TilePhysicsType.Stair,
                indestructible = false,
                hitPoints = 750,
                damageThreshold = 50,
                heightLevel = 1,
                slopeType = 1,
                stairType = 2,
                isLedge = false,
                hasWater = false,
                material = (int)MaterialType.Wood,
                doorId = "door_abc",
                trapId = "trap_def",
                frontGraphic = "front_x",
                backGraphic = "back_y"
            };

            TileData tile = new TileData();

            // Act
            snapshot.RestoreTo(tile);

            // Assert
            Assert.AreEqual(new Vector2Int(15, 25), tile.gridPosition);
            Assert.AreEqual(TilePhysicsType.Stair, tile.physicsType);
            Assert.IsFalse(tile.indestructible);
            Assert.AreEqual(750, tile.hitPoints);
            Assert.AreEqual(50, tile.damageThreshold);
            Assert.AreEqual(1, tile.heightLevel);
            Assert.AreEqual(1, tile.slopeType);
            Assert.AreEqual(2, tile.stairType);
            Assert.IsFalse(tile.isLedge);
            Assert.IsFalse(tile.hasWater);
            Assert.AreEqual(MaterialType.Wood, tile.material);
            Assert.AreEqual("door_abc", tile.doorId);
            Assert.AreEqual("trap_def", tile.trapId);
            Assert.AreEqual("front_x", tile.GetFrontGraphic());
            Assert.AreEqual("back_y", tile.GetBackGraphic());
        }

        [Test]
        public void RoomDifficultySnapshot_SerializesCorrectly()
        {
            // Arrange
            RoomDifficulty difficulty = new RoomDifficulty
            {
                baseDifficulty = 5.5f,
                enemyLevel = 3.2f,
                lockLevel = 2.1f,
                mechLevel = 1.8f,
                weaponLevel = 4.0f,
                hiddenEnemyCount = 3
            };
            difficulty.enemyCounts[1] = 5;
            difficulty.enemyCounts[2] = 3;

            // Act
            RoomDifficultySnapshot snapshot = RoomDifficultySnapshot.CreateFrom(difficulty);

            // Assert
            Assert.NotNull(snapshot);
            Assert.AreEqual(5.5f, snapshot.baseDifficulty, 0.01f);
            Assert.AreEqual(3.2f, snapshot.enemyLevel, 0.01f);
            Assert.AreEqual(2.1f, snapshot.lockLevel, 0.01f);
            Assert.AreEqual(1.8f, snapshot.mechLevel, 0.01f);
            Assert.AreEqual(4.0f, snapshot.weaponLevel, 0.01f);
            Assert.AreEqual(3, snapshot.hiddenEnemyCount);
            Assert.AreEqual(5, snapshot.enemyCounts[1]);
            Assert.AreEqual(3, snapshot.enemyCounts[2]);
        }

        [Test]
        public void RoomEnvironmentSnapshot_SerializesCorrectly()
        {
            // Arrange
            RoomEnvironment environment = new RoomEnvironment
            {
                musicTrack = "music_boss",
                colorScheme = "red",
                hasSky = true,
                waterLevel = 150,
                waterType = 2,  // lava
                waterOpacity = 0.7f,
                waterDamage = 10.5f,
                waterDamageType = 3,
                radiation = 0.5f,
                radiationDamage = 2.0f,
                darkness = 128
            };

            // Act
            RoomEnvironmentSnapshot snapshot = RoomEnvironmentSnapshot.CreateFrom(environment);

            // Assert
            Assert.NotNull(snapshot);
            Assert.AreEqual("music_boss", snapshot.musicTrack);
            Assert.AreEqual("red", snapshot.colorScheme);
            Assert.IsTrue(snapshot.hasSky);
            Assert.AreEqual(150, snapshot.waterLevel);
            Assert.AreEqual(2, snapshot.waterType);
            Assert.AreEqual(0.7f, snapshot.waterOpacity, 0.01f);
            Assert.AreEqual(10.5f, snapshot.waterDamage, 0.01f);
            Assert.AreEqual(3, snapshot.waterDamageType);
            Assert.AreEqual(0.5f, snapshot.radiation, 0.01f);
            Assert.AreEqual(2.0f, snapshot.radiationDamage, 0.01f);
            Assert.AreEqual(128, snapshot.darkness);
        }

        [Test]
        public void DoorStateSnapshot_SerializesAllProperties()
        {
            // Arrange
            DoorInstance door = new DoorInstance
            {
                doorIndex = 3,
                side = DoorSide.Bottom,
                tilePosition = new Vector2Int(20, 15),
                targetRoomPosition = new Vector3Int(1, 2, 0),
                targetDoorIndex = 5,
                quality = DoorQuality.Wide,
                isLocked = true,
                lockLevel = 3,
                keyItemId = "key_red",
                isActive = true,
                doorGraphic = "door_metal"
            };

            // Act
            DoorStateSnapshot snapshot = DoorStateSnapshot.CreateFrom(door);

            // Assert
            Assert.NotNull(snapshot);
            Assert.AreEqual(3, snapshot.doorIndex);
            Assert.AreEqual((int)DoorSide.Bottom, snapshot.side);
            Assert.AreEqual(20, snapshot.tileX);
            Assert.AreEqual(15, snapshot.tileY);
            Assert.AreEqual(1, snapshot.targetRoomX);
            Assert.AreEqual(2, snapshot.targetRoomY);
            Assert.AreEqual(0, snapshot.targetRoomZ);
            Assert.AreEqual(5, snapshot.targetDoorIndex);
            Assert.AreEqual((int)DoorQuality.Wide, snapshot.quality);
            Assert.IsTrue(snapshot.isLocked);
            Assert.AreEqual(3, snapshot.lockLevel);
            Assert.AreEqual("key_red", snapshot.keyItemId);
            Assert.IsTrue(snapshot.isActive);
            Assert.AreEqual("door_metal", snapshot.doorGraphic);
        }

        [Test]
        public void SaveMetadata_CreatesCorrectDisplayInfo()
        {
            // Arrange
            SaveMetadata metadata = new SaveMetadata
            {
                saveId = "test_save_12345",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),  // Use current time to avoid timezone issues
                saveVersion = "1.0",
                gameVersion = 1,
                roomCount = 25,
                fileSize = 1024 * 512  // 512 KB
            };

            // Act
            string displayName = metadata.GetDisplayName();
            string fileSizeString = metadata.GetFileSizeString();
            DateTime dateTime = metadata.GetDateTime();

            // Assert
            // GetDisplayName uses saveId.Substring(0, 8) which gives "test_sav"
            Assert.IsTrue(displayName.Contains("test_sav"), $"DisplayName was: {displayName}");
            Assert.AreEqual("512.0 KB", fileSizeString);
            // The year should match current year
            Assert.AreEqual(DateTime.Now.Year, dateTime.Year);
        }

        [Test]
        public void WorldSaveData_RestoreToMap_PopulatesMap()
        {
            // Arrange
            PlayerStateSnapshot playerState = new PlayerStateSnapshot();
            WorldSaveData saveData = WorldSaveData.CreateFromMap(testMap, playerState);
            LandMap newMap = new LandMap();
            newMap.Initialize(new Vector3Int(0, 0, 0), new Vector3Int(5, 5, 2));

            // Act
            saveData.RestoreToMap(newMap);

            // Assert
            RoomInstance restoredRoom = newMap.GetRoom(new Vector3Int(0, 0, 0));
            Assert.NotNull(restoredRoom);
            Assert.AreEqual("test_room_1", restoredRoom.id);
            Assert.AreEqual(1, restoredRoom.doors.Count);
            Assert.AreEqual(1, restoredRoom.spawnPoints.Count);
        }
    }
}

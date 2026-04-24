using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for RoomInstance class.
    /// </summary>
    [TestFixture]
    public class RoomInstanceTests
    {
        private RoomInstance CreateTestRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();
            return room;
        }

        [Test]
        public void RoomInstance_Initialization_CreatesTileGrid()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.tiles);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH, room.width);
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, room.height);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH * WorldConstants.ROOM_HEIGHT, room.tiles.Length);
        }

        [Test]
        public void GetTileAt_ValidPosition_ReturnsTile()
        {
            RoomInstance room = CreateTestRoom();

            TileData tile = room.GetTileAtCoord(new Vector2Int(10, 15));

            Assert.IsNotNull(tile);
            Assert.AreEqual(10, tile.gridPosition.x);
            Assert.AreEqual(15, tile.gridPosition.y);
        }

        [Test]
        public void GetTileAt_OutOfBounds_ReturnsNull()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(-1, 0)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(0, -1)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(48, 0)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(0, 27)));
        }

        [Test]
        public void GetTileAt_PixelPosition_ReturnsCorrectTile()
        {
            RoomInstance room = CreateTestRoom();

            // Pixel (100,150) should map to tile (2,3)
            TileData tile = room.GetTileAt(new Vector2(100, 150));

            Assert.IsNotNull(tile);
            Assert.AreEqual(2, tile.gridPosition.x);
            Assert.AreEqual(3, tile.gridPosition.y);
        }

        [Test]
        public void CheckCollision_NoCollision_ReturnsFalse()
        {
            RoomInstance room = CreateTestRoom();
            // All tiles are air by default

            bool collision = room.CheckCollision(new Vector2(100, 100), new Vector2(20, 40));

            Assert.IsFalse(collision);
        }

        [Test]
        public void CheckCollision_WithWall_ReturnsTrue()
        {
            RoomInstance room = CreateTestRoom();
            // Set a tile as wall
            room.tiles[5, 5].physicsType = TilePhysicsType.Wall;

            // Position that overlaps with tile (5,5)
            Vector2 pos = WorldCoordinates.TileToPixel(new Vector2Int(5, 5));
            bool collision = room.CheckCollision(pos, new Vector2(10, 10));

            Assert.IsTrue(collision);
        }

        [Test]
        public void Activate_SetsActiveAndVisited()
        {
            RoomInstance room = CreateTestRoom();

            room.Activate();

            Assert.IsTrue(room.isActive);
            Assert.IsTrue(room.isVisited);
        }

        [Test]
        public void Deactivate_SetsInactive()
        {
            RoomInstance room = CreateTestRoom();
            room.Activate();

            room.Deactivate();

            Assert.IsFalse(room.isActive);
            Assert.IsTrue(room.isVisited); // Should remain true
        }

        [Test]
        public void GetPlayerSpawnPoint_NoSpawnPoints_ReturnsCenter()
        {
            RoomInstance room = CreateTestRoom();

            Vector2 spawn = room.GetPlayerSpawnPoint();

            float expectedX = room.width * WorldConstants.TILE_SIZE / 2;
            float expectedY = room.height * WorldConstants.TILE_SIZE / 2;
            Assert.AreEqual(expectedX, spawn.x, 0.001f);
            Assert.AreEqual(expectedY, spawn.y, 0.001f);
        }

        [Test]
        public void GetPlayerSpawnPoint_WithSpawnPoints_ReturnsFirstPlayerSpawn()
        {
            RoomInstance room = CreateTestRoom();
            room.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = new Vector2Int(10, 10),
                type = SpawnType.Player
            });

            Vector2 spawn = room.GetPlayerSpawnPoint();

            Vector2 expected = WorldCoordinates.TileToPixel(new Vector2Int(10, 10));
            Assert.AreEqual(expected.x, spawn.x, 0.001f);
            Assert.AreEqual(expected.y, spawn.y, 0.001f);
        }

        [Test]
        public void GetGroundHeight_FlatTile_ReturnsTileY()
        {
            RoomInstance room = CreateTestRoom();

            float height = room.GetGroundHeight(new Vector2(100, 100));

            // Should be at the bottom of the tile
            Assert.AreEqual(80f, height, 0.001f); // Tile y=2, so yMin = 2*40 = 80
        }

        [Test]
        public void GetGroundHeight_SlopedTile_CalculatesCorrectly()
        {
            RoomInstance room = CreateTestRoom();
            // Create a sloped tile (slope type 1 = / low-left to high-right)
            room.tiles[5, 5].slopeType = 1;

            Rect bounds = room.tiles[5, 5].GetBounds();

            // At left edge of tile, should be at bottom (high y = yMax)
            // Need to pass a Y position within the tile so GetTileAt finds tile (5,5)
            float height = room.GetGroundHeight(new Vector2(bounds.xMin, bounds.center.y));
            Assert.AreEqual(bounds.yMax, height, 0.001f);
        }

        [Test]
        public void RoomInstance_Difficulty_HasDefaultValues()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.difficulty);
            Assert.AreEqual(0f, room.difficulty.baseDifficulty);
            Assert.AreEqual(0f, room.difficulty.enemyLevel);
            Assert.IsNotNull(room.difficulty.enemyCounts);
        }

        [Test]
        public void RoomInstance_Environment_HasDefaultValues()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.environment);
            Assert.AreEqual("", room.environment.musicTrack);
            Assert.AreEqual(0, room.environment.waterType);
            Assert.IsFalse(room.environment.HasWater());
        }

        [Test]
        public void RoomInstance_DoorsList_Initialized()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.doors);
            Assert.AreEqual(0, room.doors.Count);
        }

        [Test]
        public void RoomInstance_SpawnPointsList_Initialized()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.spawnPoints);
            Assert.AreEqual(0, room.spawnPoints.Count);
        }

        [Test]
        public void SetEnemyCount_WorksCorrectly()
        {
            RoomInstance room = CreateTestRoom();

            room.difficulty.SetEnemyCount(1, 2, 5);

            int count = room.difficulty.GetEnemyCount(1);
            Assert.GreaterOrEqual(count, 2);
            Assert.LessOrEqual(count, 5);
        }

        [Test]
        public void GetTotalEnemyCount_SumsAllTypes()
        {
            RoomInstance room = CreateTestRoom();
            room.difficulty.enemyCounts[1] = 3;
            room.difficulty.enemyCounts[2] = 2;
            room.difficulty.enemyCounts[3] = 1;

            int total = room.difficulty.GetTotalEnemyCount();

            Assert.AreEqual(6, total);
        }

        [Test]
        public void RoomWithAllWallTiles_HasCollision()
        {
            RoomInstance room = CreateTestRoom();

            // Fill room with walls
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    room.tiles[x, y].physicsType = TilePhysicsType.Wall;
                }
            }

            bool collision = room.CheckCollision(new Vector2(100, 100), new Vector2(10, 10));
            Assert.IsTrue(collision);
        }

        [Test]
        public void PlatformTile_IsSolidButNotWall()
        {
            RoomInstance room = CreateTestRoom();
            room.tiles[5, 5].physicsType = TilePhysicsType.Platform;

            Assert.IsTrue(room.tiles[5, 5].IsSolid());
            Assert.IsTrue(room.tiles[5, 5].IsPlatform());
        }

        [Test]
        public void Update_InactiveRoom_DoesNothing()
        {
            RoomInstance room = CreateTestRoom();
            room.Deactivate();

            // Should not throw exception
            room.Update();
        }

        [Test]
        public void RebuildRuntimeLayers_TracksOnlyDynamicProps()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition dynamicDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            dynamicDefinition.objectId = "woodbox";
            dynamicDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            MapObjectDefinition staticDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            staticDefinition.objectId = "wallcab";
            staticDefinition.physicalCapability = MapObjectPhysicalCapability.Static;

            room.objects.Add(new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = dynamicDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            });

            room.objects.Add(new ObjectInstance
            {
                objectId = "wallcab",
                objectType = "box",
                definition = staticDefinition,
                definitionId = "wallcab",
                position = new Vector2(160f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            });

            room.RebuildRuntimeLayers();

            Assert.AreEqual(1, room.ObjectPhysicsLayer.DynamicObjectCount);
            Assert.AreSame(room.objects[0], room.ObjectPhysicsLayer.DynamicObjects[0]);
        }

        [Test]
        public void TryFindNearestTelekineticObject_ReturnsClosestEligibleProp()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition telekineticDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            telekineticDefinition.objectId = "woodbox";
            telekineticDefinition.size = 1;
            telekineticDefinition.width = 1;
            telekineticDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            MapObjectDefinition throwableDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            throwableDefinition.objectId = "mcrate4";
            throwableDefinition.size = 2;
            throwableDefinition.width = 2;
            throwableDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicThrowable;

            ObjectInstance farTelekinetic = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = telekineticDefinition,
                definitionId = "woodbox",
                position = new Vector2(240f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            ObjectInstance nearTelekinetic = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = telekineticDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            ObjectInstance nonTelekinetic = new ObjectInstance
            {
                objectId = "mcrate4",
                objectType = "box",
                definition = throwableDefinition,
                definitionId = "mcrate4",
                position = new Vector2(100f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            room.AddObject(farTelekinetic);
            room.AddObject(nearTelekinetic);
            room.AddObject(nonTelekinetic);

            bool found = room.TryFindNearestTelekineticObject(new Vector2(100f, 100f), 200f, out ObjectInstance foundObject);

            Assert.IsTrue(found);
            Assert.AreSame(nearTelekinetic, foundObject);
        }

        [Test]
        public void TryApplyObjectImpulse_EnablesThrownStateForThrowableProp()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition throwableDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            throwableDefinition.objectId = "mcrate4";
            throwableDefinition.size = 2;
            throwableDefinition.width = 2;
            throwableDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicThrowable;

            ObjectInstance prop = new ObjectInstance
            {
                objectId = "mcrate4",
                objectType = "box",
                definition = throwableDefinition,
                definitionId = "mcrate4",
                position = new Vector2(160f, 160f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            room.AddObject(prop);

            bool applied = room.TryApplyObjectImpulse(prop, new Vector2(120f, -50f), true);

            Assert.IsTrue(applied);
            Assert.IsTrue(prop.runtimeState.dynamicState.isThrown);
            Assert.Greater(prop.runtimeState.dynamicState.throwGraceTime, 0f);
            Assert.AreEqual(new Vector2(120f, -50f), prop.runtimeState.dynamicState.velocity);
        }

        [Test]
        public void Update_ActiveRoom_StepsDynamicPropPhysics()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition dynamicDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            dynamicDefinition.objectId = "woodbox";
            dynamicDefinition.size = 1;
            dynamicDefinition.width = 1;
            dynamicDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            ObjectInstance dynamicObject = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = dynamicDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };
            dynamicObject.runtimeState.dynamicState.velocity = Vector2.zero;
            room.objects.Add(dynamicObject);

            room.Activate();
            float initialY = dynamicObject.position.y;

            room.Update();

            Assert.Greater(dynamicObject.position.y, initialY);
            Assert.AreEqual(1, room.ObjectPhysicsLayer.DynamicObjectCount);
        }
    }
}

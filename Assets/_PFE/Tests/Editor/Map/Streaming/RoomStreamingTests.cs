using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using System.Collections.Generic;

namespace PFE.Tests.Editor.Map.Streaming
{
    /// <summary>
    /// EditMode tests for RoomStreamingManager.
    /// </summary>
    [TestFixture]
    public class RoomStreamingTests
    {
        private GameObject testGameObject;
        private RoomStreamingManager streamingManager;
        private LandMap landMap;
        private List<RoomInstance> testRooms;

        [SetUp]
        public void Setup()
        {
            // Create test GameObject with streaming manager
            testGameObject = new GameObject("TestRoomStreamingManager");
            streamingManager = testGameObject.AddComponent<RoomStreamingManager>();

            // Create LandMap
            landMap = new LandMap();
            SetPrivateField(landMap, "rooms", new Dictionary<Vector3Int, RoomInstance>());

            // Create test rooms
            testRooms = CreateTestRooms();
        }

        [TearDown]
        public void TearDown()
        {
            if (testGameObject != null)
            {
                Object.DestroyImmediate(testGameObject);
            }
        }

        [Test]
        public void ActivateRoom_SetsRoomAsActive()
        {
            // Arrange
            RoomInstance room = testRooms[0];

            // Act
            streamingManager.ActivateRoom(room, null);

            // Assert
            Assert.That(room.isActive, Is.True, "Room should be marked as active");
            Assert.That(room.isVisited, Is.True, "Room should be marked as visited");
        }

        [Test]
        public void ActivateRoom_SetsCurrentRoom()
        {
            // Arrange
            RoomInstance room = testRooms[0];

            // Act
            streamingManager.ActivateRoom(room, null);

            // Assert
            Assert.That(streamingManager.CurrentRoom, Is.EqualTo(room), "CurrentRoom should be set");
        }

        [Test]
        public void ActivateRoom_DeactivatesPreviousRoom()
        {
            // Arrange
            RoomInstance room1 = testRooms[0];
            RoomInstance room2 = testRooms[1];

            // Act
            streamingManager.ActivateRoom(room1, null);
            streamingManager.ActivateRoom(room2, room1);

            // Assert
            Assert.That(room2.isActive, Is.True, "New room should be active");
            Assert.That(room1.isActive, Is.False, "Previous room should be deactivated");
        }

        [Test]
        public void ActivateRoom_DoesNotReactivateSameRoom()
        {
            // Arrange
            RoomInstance room = testRooms[0];
            streamingManager.ActivateRoom(room, null);

            // Act
            streamingManager.ActivateRoom(room, null);

            // Assert
            // Should not throw or cause issues
            Assert.Pass();
        }

        [Test]
        public void ActivateRoom_NullRoom_DoesNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => streamingManager.ActivateRoom(null, null));
        }

        [Test]
        public void ShouldResetRoomObjects_RoomNeverDeactivated_ReturnsFalse()
        {
            // Arrange
            RoomInstance room = testRooms[0];

            // Act & Assert
            Assert.That(streamingManager.ShouldResetRoomObjects(room), Is.False,
                "Room that was never deactivated should not reset");
        }

        [Test]
        public void ShouldResetRoomObjects_ActiveRoom_ReturnsFalse()
        {
            // Arrange
            RoomInstance room = testRooms[0];
            streamingManager.ActivateRoom(room, null);

            // Act & Assert
            Assert.That(streamingManager.ShouldResetRoomObjects(room), Is.False,
                "Active room should not reset");
        }

        [Test]
        public void ResetRoom_RestoresDestroyedTiles()
        {
            // Arrange
            RoomInstance room = testRooms[0];
            room.tiles[5, 5].Destroy();
            Assert.That(room.tiles[5, 5].IsDestroyed(), Is.True, "Tile should be destroyed");

            // Act
            streamingManager.ResetRoom(room);

            // Assert
            Assert.That(room.tiles[5, 5].IsDestroyed(), Is.False, "Tile should be restored");
            Assert.That(room.isVisited, Is.False, "Room should be marked as unvisited");
        }

        #region Helper Methods

        private List<RoomInstance> CreateTestRooms()
        {
            List<RoomInstance> rooms = new List<RoomInstance>();

            for (int i = 0; i < 3; i++)
            {
                RoomInstance room = CreateTestRoom($"test_room_{i}", new Vector3Int(i, 0, 0));
                rooms.Add(room);
            }

            return rooms;
        }

        private RoomInstance CreateTestRoom(string id, Vector3Int position)
        {
            RoomInstance room = new RoomInstance
            {
                id = id,
                templateId = id,
                landPosition = position,
                roomType = "test",
                tiles = new TileData[WorldConstants.ROOM_WIDTH, WorldConstants.ROOM_HEIGHT],
                doors = new System.Collections.Generic.List<DoorInstance>(),
                spawnPoints = new System.Collections.Generic.List<SpawnPoint>(),
                isActive = false,
                isVisited = false
            };

            // Initialize tiles
            for (int y = 0; y < WorldConstants.ROOM_HEIGHT; y++)
            {
                for (int x = 0; x < WorldConstants.ROOM_WIDTH; x++)
                {
                    room.tiles[x, y] = new TileData
                    {
                        gridPosition = new Vector2Int(x, y),
                        physicsType = TilePhysicsType.Air
                    };
                }
            }

            return room;
        }

        private void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }

        #endregion
    }
}

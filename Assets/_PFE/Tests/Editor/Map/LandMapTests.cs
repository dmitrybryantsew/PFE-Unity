using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Systems.Map;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for LandMap class.
    /// </summary>
    [TestFixture]
    public class LandMapTests
    {
        private LandMap CreateTestLand()
        {
            LandMap land = new LandMap();
            land.Initialize(new Vector3Int(0, 0, 0), new Vector3Int(4, 6, 2));
            return land;
        }

        private RoomInstance CreateTestRoom(string id, Vector3Int pos)
        {
            RoomInstance room = new RoomInstance
            {
                id = id,
                landPosition = pos,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };
            room.InitializeTiles();
            return room;
        }

        [Test]
        public void LandMap_Initializtion_SetsBounds()
        {
            LandMap land = CreateTestLand();

            Assert.AreEqual(new Vector3Int(0, 0, 0), land.minBounds);
            Assert.AreEqual(new Vector3Int(4, 6, 2), land.maxBounds);
            Assert.AreEqual(0, land.GetRoomCount());
            Assert.IsNull(land.currentRoom);
            Assert.IsNull(land.previousRoom);
        }

        [Test]
        public void AddRoom_AddsRoomToMap()
        {
            LandMap land = CreateTestLand();
            RoomInstance room = CreateTestRoom("test_room", new Vector3Int(1, 1, 0));

            land.AddRoom(room, new Vector3Int(1, 1, 0));

            Assert.AreEqual(1, land.GetRoomCount());
            Assert.IsTrue(land.HasRoom(new Vector3Int(1, 1, 0)));
        }

        [Test]
        public void AddRoom_DuplicatePosition_KeepsTheIncumbentAndNamesTheLoser()
        {
            // The shape of the camp bug: room_0_0 and room_0_0_1 both imported to (0,0,0) while z
            // was collapsed to 0. The overwrite was silent, so which room the player spawned into
            // depended on Resources.LoadAll order.
            LandMap land = CreateTestLand();
            RoomInstance incumbent = CreateTestRoom("room_0_0", new Vector3Int(0, 0, 0));
            RoomInstance duplicate = CreateTestRoom("room_0_0_1", new Vector3Int(0, 0, 0));

            land.AddRoom(incumbent, new Vector3Int(0, 0, 0));

            LogAssert.Expect(LogType.Warning,
                new Regex(@"Two rooms claim .*keeping 'room_0_0', dropping 'room_0_0_1'"));
            land.AddRoom(duplicate, new Vector3Int(0, 0, 0));

            Assert.AreEqual(1, land.GetRoomCount(), "A duplicate must not add a second entry.");
            Assert.AreEqual("room_0_0", land.GetRoom(new Vector3Int(0, 0, 0)).id,
                "The incumbent is kept, so the outcome no longer depends on insertion order.");
        }

        [Test]
        public void AddRoom_SameColumnDifferentLevel_IsNotACollision()
        {
            // Complement to the guard above: without this, a guard that rejected every AddRoom
            // would still pass the duplicate test. (0,0,0) and (0,0,1) are different rooms — this
            // is the positive control for the whole z axis at the LandMap level.
            LandMap land = CreateTestLand();

            land.AddRoom(CreateTestRoom("room_0_0", new Vector3Int(0, 0, 0)), new Vector3Int(0, 0, 0));
            land.AddRoom(CreateTestRoom("room_0_0_1", new Vector3Int(0, 0, 1)), new Vector3Int(0, 0, 1));

            Assert.AreEqual(2, land.GetRoomCount(), "Two levels of one column are two rooms.");
            Assert.AreEqual("room_0_0", land.GetRoom(new Vector3Int(0, 0, 0)).id);
            Assert.AreEqual("room_0_0_1", land.GetRoom(new Vector3Int(0, 0, 1)).id);
        }

        [Test]
        public void GetRoom_ReturnsCorrectRoom()
        {
            LandMap land = CreateTestLand();
            RoomInstance room = CreateTestRoom("test_room", new Vector3Int(2, 3, 0));

            land.AddRoom(room, new Vector3Int(2, 3, 0));

            RoomInstance retrieved = land.GetRoom(new Vector3Int(2, 3, 0));
            Assert.IsNotNull(retrieved);
            Assert.AreEqual("test_room", retrieved.id);
        }

        [Test]
        public void GetRoom_NonExistent_ReturnsNull()
        {
            LandMap land = CreateTestLand();

            RoomInstance retrieved = land.GetRoom(new Vector3Int(99, 99, 0));
            Assert.IsNull(retrieved);
        }

        [Test]
        public void SwitchRoom_ActivatesNewRoom()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));

            bool success = land.SwitchRoom(new Vector3Int(0, 0, 0));

            Assert.IsTrue(success);
            Assert.IsNotNull(land.currentRoom);
            Assert.AreEqual("room1", land.currentRoom.id);
            Assert.IsTrue(land.currentRoom.isActive);
        }

        [Test]
        public void SwitchRoom_DeactivatesPreviousRoom()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));

            land.SwitchRoom(new Vector3Int(0, 0, 0));
            land.SwitchRoom(new Vector3Int(1, 0, 0));

            Assert.AreEqual("room2", land.currentRoom.id);
            Assert.AreEqual("room1", land.previousRoom.id);
            Assert.IsFalse(land.previousRoom.isActive);
        }

        [Test]
        public void SwitchRoom_NonExistent_ReturnsFalse()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No room at position"));

            LandMap land = CreateTestLand();

            bool success = land.SwitchRoom(new Vector3Int(99, 99, 0));

            Assert.IsFalse(success);
        }

        [Test]
        public void IsInBounds_ValidPosition_ReturnsTrue()
        {
            LandMap land = CreateTestLand();

            Assert.IsTrue(land.IsInBounds(new Vector3Int(0, 0, 0)));
            Assert.IsTrue(land.IsInBounds(new Vector3Int(3, 5, 1)));
            Assert.IsTrue(land.IsInBounds(new Vector3Int(2, 3, 0)));
        }

        [Test]
        public void IsInBounds_InvalidPosition_ReturnsFalse()
        {
            LandMap land = CreateTestLand();

            Assert.IsFalse(land.IsInBounds(new Vector3Int(-1, 0, 0)));
            Assert.IsFalse(land.IsInBounds(new Vector3Int(0, -1, 0)));
            Assert.IsFalse(land.IsInBounds(new Vector3Int(4, 0, 0)));
            Assert.IsFalse(land.IsInBounds(new Vector3Int(0, 6, 0)));
            Assert.IsFalse(land.IsInBounds(new Vector3Int(0, 0, 2)));
        }

        [Test]
        public void GetAdjacentPositions_ReturnsValidNeighbors()
        {
            LandMap land = CreateTestLand();
            Vector3Int pos = new Vector3Int(2, 3, 0);

            List<Vector3Int> adjacent = land.GetAdjacentPositions(pos);

            Assert.AreEqual(4, adjacent.Count);
            Assert.Contains(new Vector3Int(1, 3, 0), adjacent);
            Assert.Contains(new Vector3Int(3, 3, 0), adjacent);
            Assert.Contains(new Vector3Int(2, 2, 0), adjacent);
            Assert.Contains(new Vector3Int(2, 4, 0), adjacent);
        }

        [Test]
        public void GetAdjacentPositions_AtEdge_ReturnsFewerNeighbors()
        {
            LandMap land = CreateTestLand();
            Vector3Int pos = new Vector3Int(0, 0, 0); // Corner

            List<Vector3Int> adjacent = land.GetAdjacentPositions(pos);

            Assert.AreEqual(2, adjacent.Count);
            Assert.Contains(new Vector3Int(1, 0, 0), adjacent);
            Assert.Contains(new Vector3Int(0, 1, 0), adjacent);
        }

        [Test]
        public void FindPath_SimplePath_ReturnsCorrectPath()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));
            RoomInstance room3 = CreateTestRoom("room3", new Vector3Int(2, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));
            land.AddRoom(room3, new Vector3Int(2, 0, 0));

            List<Vector3Int> path = land.FindPath(new Vector3Int(0, 0, 0), new Vector3Int(2, 0, 0));

            Assert.IsNotNull(path);
            Assert.AreEqual(3, path.Count);
            Assert.AreEqual(new Vector3Int(0, 0, 0), path[0]);
            Assert.AreEqual(new Vector3Int(1, 0, 0), path[1]);
            Assert.AreEqual(new Vector3Int(2, 0, 0), path[2]);
        }

        [Test]
        public void FindPath_NoPath_ReturnsNull()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(2, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(2, 0, 0));
            // No room at (1, 0, 0) - no path possible

            List<Vector3Int> path = land.FindPath(new Vector3Int(0, 0, 0), new Vector3Int(2, 0, 0));

            Assert.IsNull(path);
        }

        [Test]
        public void AddSpecialRoom_AddsToSpecialStorage()
        {
            LandMap land = CreateTestLand();
            RoomInstance room = CreateTestRoom("prob_room", new Vector3Int(0, 0, 0));

            land.AddSpecialRoom("test_prob", room, new Vector3Int(0, 0, 0));

            RoomInstance retrieved = land.GetSpecialRoom("test_prob", new Vector3Int(0, 0, 0));
            Assert.IsNotNull(retrieved);
            Assert.AreEqual("prob_room", retrieved.id);
        }

        [Test]
        public void GetSpecialRoom_NonExistent_ReturnsNull()
        {
            LandMap land = CreateTestLand();

            RoomInstance retrieved = land.GetSpecialRoom("nonexistent", new Vector3Int(0, 0, 0));
            Assert.IsNull(retrieved);
        }

        [Test]
        public void Clear_RemovesAllRooms()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));
            land.SwitchRoom(new Vector3Int(0, 0, 0));

            land.Clear();

            Assert.AreEqual(0, land.GetRoomCount());
            Assert.IsNull(land.currentRoom);
            Assert.IsNull(land.previousRoom);
        }

        [Test]
        public void GetAllRooms_ReturnsAllRooms()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));

            List<RoomInstance> allRooms = land.GetAllRooms().ToList();

            Assert.AreEqual(2, allRooms.Count);
        }

        [Test]
        public void GetCurrentPosition_ReturnsCurrentRoomPosition()
        {
            LandMap land = CreateTestLand();
            RoomInstance room = CreateTestRoom("room1", new Vector3Int(2, 3, 0));

            land.AddRoom(room, new Vector3Int(2, 3, 0));
            land.SwitchRoom(new Vector3Int(2, 3, 0));

            Assert.AreEqual(new Vector3Int(2, 3, 0), land.GetCurrentPosition());
        }

        // ── SetCurrentRoom (P0 Defect 2 — door-transition bookkeeping) ────────────────────
        //
        // A door transition goes through RoomStreamingManager.ActivateRoom, which flips the
        // rooms' isActive flags but never told LandMap anything. `currentRoom` therefore stayed
        // on the room the player started in, and because RoomInstance.Update() early-returns
        // when !isActive, LandMap.Update() became a no-op for the room the player was actually
        // standing in. These tests pin the narrow fix.

        [Test]
        public void SetCurrentRoom_UpdatesCurrentAndPrevious()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));

            land.SwitchRoom(new Vector3Int(0, 0, 0));
            Assert.AreEqual("room1", land.currentRoom.id);

            land.SetCurrentRoom(room2);

            Assert.AreEqual("room2", land.currentRoom.id,
                "LandMap still points at the room the player left.");
            Assert.AreEqual("room1", land.previousRoom.id,
                "previousRoom bookkeeping must mirror SwitchRoom.");
            Assert.AreEqual(new Vector3Int(1, 0, 0), land.GetCurrentPosition(),
                "currentCoord must follow the new room.");
        }

        [Test]
        public void SetCurrentRoom_DoesNotTouchActivationState()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));
            RoomInstance room2 = CreateTestRoom("room2", new Vector3Int(1, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.AddRoom(room2, new Vector3Int(1, 0, 0));

            land.SwitchRoom(new Vector3Int(0, 0, 0));

            // Reproduce what the streaming manager has already done by the time
            // RoomTransitionManager calls SetCurrentRoom.
            room1.Deactivate();
            room2.Activate();

            land.SetCurrentRoom(room2);

            Assert.IsTrue(room2.isActive,
                "SetCurrentRoom must not activate — RoomStreamingManager owns activation.");
            Assert.IsFalse(room1.isActive,
                "SetCurrentRoom must not deactivate the room the player left.");
        }

        [Test]
        public void SetCurrentRoom_IsIdempotentAndNullSafe()
        {
            LandMap land = CreateTestLand();
            RoomInstance room1 = CreateTestRoom("room1", new Vector3Int(0, 0, 0));

            land.AddRoom(room1, new Vector3Int(0, 0, 0));
            land.SwitchRoom(new Vector3Int(0, 0, 0));

            land.SetCurrentRoom(room1);
            Assert.AreEqual("room1", land.currentRoom.id);
            Assert.IsNull(land.previousRoom,
                "Re-selecting the same room must not demote it to previousRoom.");

            Assert.DoesNotThrow(() => land.SetCurrentRoom(null));
            Assert.AreEqual("room1", land.currentRoom.id,
                "A null room must not clear currentRoom.");
        }
    }
}

using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class DoorCarverTests
    {
        [Test]
        public void ApplyBorder_UsesExistingRoomBounds()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 1);

            Assert.AreEqual(WorldConstants.ROOM_WIDTH, room.width);
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, room.height);
            Assert.AreEqual(0, room.borderOffset);
        }

        [Test]
        public void ApplyBorder_AssignsComposableBorderMaterial()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 1);

            TileData bottomLeft = room.tiles[0, 0];
            Assert.AreEqual(TilePhysicsType.Wall, bottomLeft.physicsType);
            Assert.AreEqual("A", bottomLeft.GetFrontGraphic());
            Assert.AreEqual("A", bottomLeft.GetBackGraphic());
            Assert.IsTrue(bottomLeft.indestructible);
        }

        [Test]
        public void CarveDoor_CarvesExistingEdgeCells()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            DoorCarver.CarveDoor(room, 0, (int)DoorQuality.Narrow);

            int rightCol = WorldConstants.ROOM_WIDTH - 1;
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[rightCol, 23].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[rightCol, 24].physicsType);
            Assert.AreEqual(string.Empty, room.tiles[rightCol, 23].GetFrontGraphic());
        }

        [Test]
        public void CarveDoor_ConvertsAs3BottomRowToUnityBottomRow()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            DoorCarver.CarveDoor(room, 6, (int)DoorQuality.Narrow);

            Assert.AreEqual(TilePhysicsType.Air, room.tiles[5, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[6, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[5, WorldConstants.ROOM_HEIGHT - 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[6, WorldConstants.ROOM_HEIGHT - 1].physicsType);
        }

        [Test]
        public void ApplyBorder_MainFramesExistingSolidEdges_WhenRamkaIsZero()
        {
            RoomInstance room = CreateAirRoom();
            room.tiles[0, 10].physicsType = TilePhysicsType.Wall;

            DoorCarver.ApplyBorder(room, 0);

            TileData edgeTile = room.tiles[0, 10];
            Assert.AreEqual(TilePhysicsType.Wall, edgeTile.physicsType);
            Assert.AreEqual("A", edgeTile.GetFrontGraphic());
            Assert.AreEqual("A", edgeTile.GetBackGraphic());
            Assert.IsTrue(edgeTile.indestructible);
        }

        [Test]
        public void ApplyBorder_ConvertsAs3BottomRamkaToUnityBottomRow()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 3);

            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[12, 0].physicsType);
            Assert.AreEqual("A", room.tiles[12, 0].GetFrontGraphic());
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[12, WorldConstants.ROOM_HEIGHT - 1].physicsType);
        }

        [Test]
        public void FinalizeSpecificRoom_DoesNotCarveTemplateDoors()
        {
            RoomInstance room = CreateAirRoom();
            int rightCol = WorldConstants.ROOM_WIDTH - 1;
            room.tiles[rightCol, 23].physicsType = TilePhysicsType.Wall;
            room.tiles[rightCol, 24].physicsType = TilePhysicsType.Wall;
            room.doors.Add(new DoorInstance
            {
                doorIndex = 0,
                side = DoorSide.Right,
                quality = DoorQuality.Narrow,
                isActive = true
            });

            var template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.environment.waterLevel = WorldConstants.ROOM_HEIGHT;

            RoomSetup.FinalizeSpecificRoom(room, template);

            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[rightCol, 23].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[rightCol, 24].physicsType);

            Object.DestroyImmediate(template);
        }

        private static RoomInstance CreateAirRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "door_carver_test",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };
            room.InitializeTiles();
            return room;
        }
    }
}

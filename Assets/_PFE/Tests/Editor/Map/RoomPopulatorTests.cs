using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class RoomPopulatorTests
    {
        [Test]
        public void PopulateRoom_ConvertsLegacyAs3ObjectCoordinatesIntoUnityRoomSpace()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room",
                width = WorldConstants.ROOM_WIDTH + 2,
                height = WorldConstants.ROOM_HEIGHT + 2,
                borderOffset = 1
            };
            room.InitializeTiles();

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "woodbox",
                type = "box",
                tileCoord = new Vector2Int(4, 23),
                code = "legacy_box"
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            ObjectInstance spawned = room.objects.Find(obj => obj != null && obj.code == "legacy_box");
            Assert.NotNull(spawned);
            Assert.AreEqual(220f, spawned.position.x, 0.01f);
            Assert.AreEqual(41f, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }

        [Test]
        public void PopulateRoom_LeavesLegacyUnitBottomAnchorUnshifted()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room_units",
                width = WorldConstants.ROOM_WIDTH + 2,
                height = WorldConstants.ROOM_HEIGHT + 2,
                borderOffset = 1
            };
            room.InitializeTiles();

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "raider",
                type = "unit",
                tileCoord = new Vector2Int(4, 23)
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "raider");
            Assert.NotNull(spawned);
            Assert.AreEqual(220f, spawned.position.x, 0.01f);
            Assert.AreEqual(81f, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }
    }
}

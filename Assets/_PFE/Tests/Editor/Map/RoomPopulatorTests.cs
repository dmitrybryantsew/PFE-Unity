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
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
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
            Assert.AreEqual(180f, spawned.position.x, 0.01f);

            // AS3 Location.createObj(): y = (ny + 1) * Tile.tileY - 1 with the TOP-DOWN row ny = 23
            // => 959 px below the room's top edge => 41 px above the bottom of a 1000 px room.
            // (This used to read 121: the same formula against the old, wrong 27-row room height.)
            float as3Y = (23 + 1) * WorldConstants.TILE_SIZE - 1;
            float expectedY = room.height * WorldConstants.TILE_SIZE - as3Y;
            Assert.AreEqual(41f, expectedY, 0.01f);
            Assert.AreEqual(expectedY, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }

        [Test]
        public void PopulateRoom_PlacesUnitsOnTheAs3BottomAnchor()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room_units",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
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
            Assert.AreEqual(180f, spawned.position.x, 0.01f);

            // AS3 Location.createUnit() (Location.as:1211) uses the SAME bottom anchor as
            // createObj(): putLoc(this, (nx + 0.5 * size) * tileX, (ny + 1) * tileY - 1). There is no
            // extra tile of lift for units, so a unit and a box authored on the same tile must land
            // on the same Y. The old expectation (161) placed units one tile higher than AS3 does.
            Assert.AreEqual(41f, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }
    }
}

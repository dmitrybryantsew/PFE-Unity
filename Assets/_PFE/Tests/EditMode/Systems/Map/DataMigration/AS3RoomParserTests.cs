using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map.DataMigration;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Map.DataMigration
{
    /// <summary>
    /// Tests for AS3 XML room parser
    /// </summary>
    [TestFixture]
    public class AS3RoomParserTests
    {
        private AS3RoomParser parser;

        [SetUp]
        public void Setup()
        {
            parser = new AS3RoomParser();
        }

        [Test]
        public void ParseSimpleRoom_ValidXml_ReturnsRoomData()
        {
            // Arrange
            string xml = @"<data><land serial='1'/>
<room name='test_room' x='1' y='2'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            Assert.IsNotNull(collection);
            Assert.AreEqual(1, collection.rooms.Count, "Should parse 1 room");

            AS3RoomData room = collection.rooms[0];
            Assert.AreEqual("test_room", room.name);
            Assert.AreEqual(1, room.x);
            Assert.AreEqual(2, room.y);
            Assert.AreEqual(3, room.tileLayers.Count);
        }

        [Test]
        public void ParseRoomWithObjects_ValidXml_ParsesObjects()
        {
            // Arrange
            string xml = @"<data><land serial='1'/>
<room name='room_with_objects' x='0' y='0'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
    <obj id='chest' code='test123' x='1' y='1'/>
    <obj id='player' code='player1' x='0' y='1'/>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            Assert.AreEqual(1, collection.rooms.Count);
            AS3RoomData room = collection.rooms[0];
            Assert.AreEqual(2, room.objects.Count);

            AS3Object chest = room.objects.Find(o => o.id == "chest");
            Assert.IsNotNull(chest);
            Assert.AreEqual("test123", chest.code);
            Assert.AreEqual(1, chest.x);
            Assert.AreEqual(1, chest.y);

            AS3Object player = room.objects.Find(o => o.id == "player");
            Assert.IsNotNull(player);
            Assert.AreEqual("player1", player.code);
        }

        [Test]
        public void ParseRoomWithItems_ValidXml_ParsesItems()
        {
            // Arrange
            string xml = @"<data><land serial='1'/>
<room name='room_with_items' x='0' y='0'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
    <obj id='chest' x='1' y='1'>
        <item id='col1' imp='1'/>
        <item id='pot0'/>
    </obj>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            AS3RoomData room = collection.rooms[0];
            AS3Object chest = room.objects[0];
            Assert.AreEqual(2, chest.items.Count);
            Assert.AreEqual("col1", chest.items[0].id);
            Assert.AreEqual("pot0", chest.items[1].id);
        }

        [Test]
        public void GetTile_ValidCoordinates_ReturnsCorrectCharacter()
        {
            // Arrange
            AS3RoomData room = new AS3RoomData();
            room.tileLayers.Add("ABC");
            room.tileLayers.Add("DEF");
            room.tileLayers.Add("GHI");

            // Act & Assert
            Assert.AreEqual('A', room.GetTile(0, 0));
            Assert.AreEqual('B', room.GetTile(1, 0));
            Assert.AreEqual('E', room.GetTile(1, 1));
            Assert.AreEqual('I', room.GetTile(2, 2));
        }

        [Test]
        public void GetTile_OutOfBounds_ReturnsUnderscore()
        {
            // Arrange
            AS3RoomData room = new AS3RoomData();
            room.tileLayers.Add("ABC");

            // Act & Assert
            Assert.AreEqual('_', room.GetTile(-1, 0));
            Assert.AreEqual('_', room.GetTile(0, -1));
            Assert.AreEqual('_', room.GetTile(10, 0));
            Assert.AreEqual('_', room.GetTile(0, 10));
        }

        [Test]
        public void IsValid_ValidRoom_ReturnsTrue()
        {
            // Arrange
            AS3RoomData room = new AS3RoomData
            {
                name = "valid_room",
                x = 0,
                y = 0
            };

            // Create valid 48x27 tile data
            for (int i = 0; i < WorldConstants.ROOM_HEIGHT; i++)
            {
                room.tileLayers.Add(new string('C', WorldConstants.ROOM_WIDTH));
            }

            // Act
            bool isValid = room.IsValid();

            // Assert
            Assert.IsTrue(isValid);
        }

        [Test]
        public void IsValid_WrongHeight_ReturnsFalse()
        {
            // Arrange
            AS3RoomData room = new AS3RoomData
            {
                name = "invalid_room",
                x = 0,
                y = 0
            };

            // Only 10 rows instead of 27
            for (int i = 0; i < 10; i++)
            {
                room.tileLayers.Add(new string('C', WorldConstants.ROOM_WIDTH));
            }

            // Act
            bool isValid = room.IsValid();

            // Assert
            Assert.IsFalse(isValid);
        }

        [Test]
        public void IsValid_WrongWidth_ReturnsFalse()
        {
            // Arrange
            AS3RoomData room = new AS3RoomData
            {
                name = "invalid_room",
                x = 0,
                y = 0
            };

            // Create 27 rows but only 10 columns
            for (int i = 0; i < WorldConstants.ROOM_HEIGHT; i++)
            {
                room.tileLayers.Add(new string('C', 10));
            }

            // Act
            bool isValid = room.IsValid();

            // Assert
            Assert.IsFalse(isValid);
        }

        [Test]
        public void ParseBackgrounds_ValidXml_ParsesBackgrounds()
        {
            // Arrange
            string xml = @"<data><land serial='1'/>
<room name='room_with_backgrounds' x='0' y='0'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
    <back id='light2' x='2' y='2'/>
    <back id='electro' x='5' y='4'/>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            AS3RoomData room = collection.rooms[0];
            Assert.AreEqual(2, room.backgrounds.Count);

            AS3Background back1 = room.backgrounds[0];
            Assert.AreEqual("light2", back1.id);
            Assert.AreEqual(2, back1.x);
            Assert.AreEqual(2, back1.y);
        }
    }
}

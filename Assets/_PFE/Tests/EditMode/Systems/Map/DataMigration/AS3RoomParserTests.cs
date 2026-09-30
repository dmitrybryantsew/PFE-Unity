using System;
using System.IO;
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

        [Test]
        public void ParseRoomWithZ_ReadsTheLevelIntoZ()
        {
            // RoomsCamp.as:239 — `<room name="room_0_0_1" x="0" y="0" z="1">`, the upper level of
            // the same (0,0) column as room_0_0. Land.as:726-727 is
            // `this.locs[rx][ry][rz] = newLoc(room, rx, ry, rz)`.
            string xml = @"<data><land serial='1'/>
<room name='room_0_0_1' x='0' y='0' z='1'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            AS3RoomData room = collection.rooms[0];
            Assert.AreEqual(0, room.x);
            Assert.AreEqual(0, room.y);
            Assert.AreEqual(1, room.z,
                "z is a separate grid axis. Dropping it stacks room_0_0_1 onto room_0_0, and " +
                "whichever wins the load-order overwrite is the room the player spawns into.");
        }

        [Test]
        public void ParseRoomWithoutZ_DefaultsToTheGroundLevel()
        {
            // Complement to the test above. If z were read from the wrong attribute, or defaulted
            // to anything but 0, an absent z would not read as ground level — and the z test above
            // would still pass, because it supplies z explicitly.
            string xml = @"<data><land serial='1'/>
<room name='room_1_0' x='1' y='0'>
    <a>C.C.C</a>
    <a>C._.C</a>
    <a>C.C.C</a>
</room></data>";

            // Act
            AS3RoomCollection collection = parser.ParseXmlString(xml);

            // Assert
            Assert.AreEqual(0, collection.rooms[0].z,
                "A room with no z attribute sits on the ground level.");
        }

        // ==========================================================
        //  COLLECTION IDENTITY
        //
        //  A collection is a *land*, not a file. RoomsCamp.as declares three of them
        //  (rooms_rbl, rooms_covert, rooms_src) and all three define room_0_0; RoomsSerial2.as declares
        //  eight. The importer used to name the output folder after the *source file* while the parser
        //  named the collection after the *field*, so all of a file's lands were written into one folder
        //  and the later ones silently overwrote the earlier ones — 76 of 639 rooms were lost that way.
        //  These tests pin the field-derived id, which is what makes per-land folders possible.
        // ==========================================================

        private string tempDir;

        [SetUp]
        public void SetupTempDir()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "pfe_room_parser_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void RemoveTempDir()
        {
            if (tempDir != null && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }

        private string WriteSource(string fileName, string body)
        {
            string path = Path.Combine(tempDir, fileName);
            File.WriteAllText(path, body);
            return path;
        }

        private const string OneRoom =
            "<room name='room_0_0' x='0' y='0'><a>C.C.C</a><a>C._.C</a><a>C.C.C</a></room>";

        [Test]
        public void ParseFile_NamedXmlField_UsesTheFieldNameAsTheCollectionId()
        {
            // RoomsCamp.as:7 — `internal var rooms_rbl:XML = <all>`, addressed by the oracle as
            // GameData.as:53 `<land id='rbl' file='rooms_rbl'>` and registered by Rooms.as:2798.
            string path = WriteSource("RoomsCamp.as",
                "package fe.rooms { public class RoomsCamp { internal var rooms_rbl:XML = <all>" +
                OneRoom + "</all>; } }");

            AS3RoomCollection collection = parser.ParseFile(path);

            Assert.AreEqual(1, collection.rooms.Count);
            Assert.AreEqual("rooms_rbl", collection.rooms[0].sourceCollectionId,
                "The collection id must be the AS3 field name: it is the key Rooms.as registers the land " +
                "under and the key AS3LandDefaultsDatabase merges that land's inherited options by.");
        }

        [Test]
        public void ParseFile_GenericRoomsField_FallsBackToTheFileDerivedId()
        {
            // Nine files declare the single land as plain `rooms` (RoomsPlant.as, RoomsCanter.as, ...),
            // which names nothing. GameData.as calls those lands rooms_plant / rooms_canter, so the file
            // name is the only thing that distinguishes them — without this all nine collapse onto "rooms".
            string path = WriteSource("RoomsPlant.as",
                "package fe.rooms { public class RoomsPlant { internal var rooms:XML = <all>" +
                OneRoom + "</all>; } }");

            AS3RoomCollection collection = parser.ParseFile(path);

            Assert.AreEqual(1, collection.rooms.Count);
            Assert.AreEqual("rooms_plant", collection.rooms[0].sourceCollectionId,
                "A generic `rooms` field must fall back to the file-derived id, or all nine " +
                "single-land files share one collection.");
        }

        [Test]
        public void ParseFile_GenericRoomsField_DoesNotCollapseDistinctFilesOntoOneId()
        {
            // Complement to the test above: one file proving the fallback fires is not enough, because a
            // hardcoded "rooms" would also pass it if the file happened to be called Rooms.as. Two files
            // must yield two different ids.
            string plant = parser.ParseFile(WriteSource("RoomsPlant.as",
                "internal var rooms:XML = <all>" + OneRoom + "</all>;")).rooms[0].sourceCollectionId;
            string canter = parser.ParseFile(WriteSource("RoomsCanter.as",
                "internal var rooms:XML = <all>" + OneRoom + "</all>;")).rooms[0].sourceCollectionId;

            Assert.AreEqual("rooms_plant", plant);
            Assert.AreEqual("rooms_canter", canter);
            Assert.AreNotEqual(plant, canter,
                "Two single-land files must not share a collection id.");
        }

        [Test]
        public void ParseFile_MultipleFieldsInOneFile_KeepsTheirRoomIdsApart()
        {
            // The collision that lost the camp. RoomsCamp.as declares room_0_0 in rooms_rbl AND in
            // rooms_src; both used to be written to Camp/room_0_0.asset, so rooms_src won by load order
            // and the camp's real main room — the one holding the two indoor2 Z doors — disappeared.
            string path = WriteSource("RoomsCamp.as",
                "package fe.rooms { public class RoomsCamp {" +
                "internal var rooms_rbl:XML = <all>" + OneRoom + "</all>;" +
                "internal var rooms_src:XML = <all>" + OneRoom + "</all>;" +
                "} }");

            AS3RoomCollection collection = parser.ParseFile(path);

            Assert.AreEqual(2, collection.rooms.Count,
                "Both fields declare a room; the parser must keep both.");
            Assert.AreEqual(2, collection.collectionIds.Count);
            CollectionAssert.Contains(collection.collectionIds, "rooms_rbl");
            CollectionAssert.Contains(collection.collectionIds, "rooms_src");

            string first = collection.rooms[0].sourceCollectionId;
            string second = collection.rooms[1].sourceCollectionId;
            Assert.AreNotEqual(first, second,
                "Same room id in two lands must still carry two different collection ids, or the " +
                "importer writes them to one path and one of them is lost.");
        }
    }
}

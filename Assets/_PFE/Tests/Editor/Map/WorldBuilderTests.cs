using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Systems.Map;
using System.Collections.Generic;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for WorldBuilder - procedural world generation.
    /// </summary>
    [TestFixture]
    public class WorldBuilderTests
    {
        private WorldBuilder worldBuilder;
        private LandMap landMap;
        private RoomGenerator roomGenerator;
        private List<RoomTemplate> testTemplates;

        [SetUp]
        public void Setup()
        {
            landMap = new LandMap();
            roomGenerator = new RoomGenerator();
            worldBuilder = new WorldBuilder();

            testTemplates = CreateTestTemplates();
            roomGenerator.Initialize(testTemplates);
            worldBuilder.Initialize(landMap, roomGenerator, testTemplates);
        }

        [Test]
        public void Initialize_ValidInput_SetsUpBuilder()
        {
            // Assert
            Assert.That(landMap, Is.Not.Null);
            Assert.That(roomGenerator, Is.Not.Null);
            Assert.That(testTemplates, Is.Not.Empty);
        }

        [Test]
        public void BuildRandomWorld_WithTemplates_CreatesWorld()
        {
            // Act
            bool success = worldBuilder.BuildRandomWorld(stage: 1);

            // Assert
            Assert.That(success, Is.True, "World building should succeed");
            Assert.That(landMap.GetRoomCount(), Is.GreaterThan(0), "Should have created rooms");
            Assert.That(landMap.currentRoom, Is.Not.Null, "Should have activated a room");
        }

        [Test]
        public void BuildRandomWorld_DefaultBounds_CreatesExpectedRoomCount()
        {
            // Act
            worldBuilder.BuildRandomWorld(stage: 1);

            // Assert
            // Default bounds are 4x6x1 = 24 rooms
            int expectedCount = 4 * 6 * 1;
            Assert.That(landMap.GetRoomCount(), Is.EqualTo(expectedCount),
                $"Should create {expectedCount} rooms with default bounds");
        }

        [Test]
        public void BuildRandomWorld_CustomBounds_RespectsBounds()
        {
            // Arrange
            Vector3Int customMin = new Vector3Int(0, 0, 0);
            Vector3Int customMax = new Vector3Int(2, 3, 1);

            // Act
            worldBuilder.BuildRandomWorld(stage: 1, customMin, customMax);

            // Assert
            int expectedCount = 2 * 3 * 1;
            Assert.That(landMap.GetRoomCount(), Is.EqualTo(expectedCount),
                $"Should create {expectedCount} rooms with custom bounds");
        }

        [Test]
        public void BuildRandomWorld_CreatesStartingRoom()
        {
            // Act
            worldBuilder.BuildRandomWorld(stage: 1);

            // Assert
            RoomInstance startRoom = landMap.GetRoom(new Vector3Int(0, 0, 0));
            Assert.That(startRoom, Is.Not.Null, "Should have room at starting position");
            Assert.That(startRoom.roomType, Is.EqualTo("beg0").Or.EqualTo("beg1"),
                "Starting room should be of type beg0 or beg1");
            Assert.That(landMap.currentRoom, Is.EqualTo(startRoom),
                "Current room should be the starting room");
        }

        [Test]
        public void BuildRandomWorld_CreatesDoorConnections()
        {
            // Act
            worldBuilder.BuildRandomWorld(stage: 1);

            // Assert
            int roomsWithDoors = 0;
            foreach (var room in landMap.GetAllRooms())
            {
                int activeDoors = 0;
                foreach (var door in room.doors)
                {
                    if (door.isActive) activeDoors++;
                }

                if (activeDoors > 0)
                {
                    roomsWithDoors++;
                }
            }

            Assert.That(roomsWithDoors, Is.GreaterThan(0),
                "Should have rooms with active door connections");
        }

        [Test]
        public void BuildSpecificWorld_WithFixedPositions_PlacesRoomsCorrectly()
        {
            // Arrange
            List<RoomTemplate> specificTemplates = new List<RoomTemplate>
            {
                CreateTestTemplate("room1", "beg0", new Vector3Int(0, 0, 0)),
                CreateTestTemplate("room2", "pass", new Vector3Int(1, 0, 0)),
                CreateTestTemplate("room3", "pass", new Vector3Int(0, 1, 0))
            };

            // Act
            bool success = worldBuilder.BuildSpecificWorld(specificTemplates);

            // Assert
            Assert.That(success, Is.True);
            Assert.That(landMap.GetRoomCount(), Is.EqualTo(3));
            Assert.That(landMap.HasRoom(new Vector3Int(0, 0, 0)), Is.True);
            Assert.That(landMap.HasRoom(new Vector3Int(1, 0, 0)), Is.True);
            Assert.That(landMap.HasRoom(new Vector3Int(0, 1, 0)), Is.True);
        }

        [Test]
        public void BuildSpecificWorld_WithoutMatchingDoors_DoesNotCreateFallbackConnection()
        {
            // Arrange: room1 has doors, roomBelow has NO doors (all 0 quality)
            var room1 = CreateTestTemplate("room1", "beg0", new Vector3Int(0, 0, 0));
            var roomBelow = CreateTestTemplate("roomBelow", "pass", new Vector3Int(0, 1, 0));
            for (int i = 0; i < 24; i++)
            {
                roomBelow.doorQuality[i] = 0;
            }

            // Act
            bool success = worldBuilder.BuildSpecificWorld(new List<RoomTemplate> { room1, roomBelow });

            // Assert
            Assert.That(success, Is.True);
            var instance1 = landMap.GetRoom(new Vector3Int(0, 0, 0));
            Assert.That(instance1, Is.Not.Null);

            foreach (var door in instance1.doors)
            {
                if (door.side == DoorSide.Bottom)
                {
                    Assert.That(door.isActive, Is.False, "Authored world must not force fallback doors to roomBelow without matching doors");
                }
            }
        }

        [Test]
        public void BuildSpecificWorld_StartsAtBeginningRoom()
        {
            // Arrange
            List<RoomTemplate> specificTemplates = new List<RoomTemplate>
            {
                CreateTestTemplate("other", "pass", new Vector3Int(2, 0, 0)),
                CreateTestTemplate("start", "beg0", new Vector3Int(0, 0, 0))
            };

            // Act
            worldBuilder.BuildSpecificWorld(specificTemplates);

            // Assert
            RoomInstance current = landMap.currentRoom;
            Assert.That(current, Is.Not.Null);
            Assert.That(current.templateId, Is.EqualTo("start"));
            Assert.That(current.landPosition, Is.EqualTo(new Vector3Int(0, 0, 0)));
        }

        [Test]
        public void BuildRandomWorld_WithoutTemplates_FailsGracefully()
        {
            // Arrange
            var emptyBuilder = new WorldBuilder();
            emptyBuilder.Initialize(landMap, roomGenerator, new List<RoomTemplate>());

            // Expect error log
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*No room templates available.*"));

            // Act
            bool success = emptyBuilder.BuildRandomWorld(stage: 1);

            // Assert
            Assert.That(success, Is.False, "Should fail without templates");
            Assert.That(landMap.GetRoomCount(), Is.EqualTo(0), "Should not create rooms");
        }

        [Test]
        public void BuildDoorConnections_AdjacentRooms_HasMatchingDoors()
        {
            // Arrange
            worldBuilder.BuildRandomWorld(stage: 1);

            // Act & Assert
            // Check that rooms have valid door connections
            foreach (var room in landMap.GetAllRooms())
            {
                foreach (var door in room.doors)
                {
                    if (door.isActive)
                    {
                        // Verify target room exists
                        RoomInstance targetRoom = landMap.GetRoom(door.targetRoomPosition);
                        Assert.That(targetRoom, Is.Not.Null,
                            $"Door from {room.landPosition} should point to existing room at {door.targetRoomPosition}");

                        // Verify target door exists
                        DoorInstance targetDoor = null;
                        foreach (var d in targetRoom.doors)
                        {
                            if (d.doorIndex == door.targetDoorIndex)
                            {
                                targetDoor = d;
                                break;
                            }
                        }
                        Assert.That(targetDoor, Is.Not.Null,
                            $"Target door {door.targetDoorIndex} should exist in target room");
                        Assert.That(targetDoor.isActive, Is.True,
                            $"Target door should be active");
                    }
                }
            }
        }

        [Test]
        public void BuildRandomWorld_NoRepetitionInAdjacentRooms()
        {
            // Arrange
            // Use smaller bounds to ensure we have enough unique templates
            // 2x2 grid = 4 rooms, we have 12+ pass templates available
            Vector3Int customMin = new Vector3Int(0, 0, 0);
            Vector3Int customMax = new Vector3Int(2, 2, 1);

            // Set maxInstances to 1 to make repetition obvious
            foreach (var t in testTemplates)
            {
                t.maxInstances = 1;
            }

            // Act
            worldBuilder.BuildRandomWorld(stage: 1, customMin, customMax);

            // Assert
            // Check adjacent rooms don't have same template
            for (int x = customMin.x; x < customMax.x; x++)
            {
                for (int y = customMin.y; y < customMax.y; y++)
                {
                    Vector3Int pos = new Vector3Int(x, y, 0);
                    RoomInstance room = landMap.GetRoom(pos);

                    if (room == null) continue;

                    // Check right neighbor
                    if (x < customMax.x - 1)
                    {
                        RoomInstance rightRoom = landMap.GetRoom(new Vector3Int(x + 1, y, 0));
                        if (rightRoom != null)
                        {
                            Assert.That(room.templateId, Is.Not.EqualTo(rightRoom.templateId),
                                $"Adjacent rooms at {pos} and {new Vector3Int(x + 1, y, 0)} should not be identical");
                        }
                    }

                    // Check bottom neighbor
                    if (y < customMax.y - 1)
                    {
                        RoomInstance bottomRoom = landMap.GetRoom(new Vector3Int(x, y + 1, 0));
                        if (bottomRoom != null)
                        {
                            Assert.That(room.templateId, Is.Not.EqualTo(bottomRoom.templateId),
                                $"Adjacent rooms at {pos} and {new Vector3Int(x, y + 1, 0)} should not be identical");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A cell whose template declares an interior (<c>&lt;options back="…"&gt;</c>) must end up with that
        /// interior registered at <b><c>z + 1</c> of the same cell</b>.
        ///
        /// <para><b>Why the registry and not just "was it built".</b> <c>LandMap.GetLayerToggleTarget</c> —
        /// the port of <c>Land.gotoLoc(5)</c>'s <c>locZ = 1 - locZ</c> lookup (<c>Land.as:1333</c>,
        /// <c>:1351</c>) — reads <b>only</b> <c>LandMap.rooms</c>. <c>ComeInAction.CanExecute</c> is that
        /// call, so an interior that exists but is registered elsewhere is a Z door that refuses and does
        /// nothing. Both procedural paths had exactly that bug (<c>BuildRandomWorld</c> and the
        /// <c>MapBridge</c> override used <c>AddSpecialRoom</c>, and <c>BuildProceduralLand</c> never read
        /// <c>backgroundRoomId</c> at all), so asserting "a room exists" is not enough — the assertion has to
        /// be on the lookup the door actually performs.</para>
        ///
        /// <para>A 1x1 grid is used deliberately: the single cell <i>is</i> <c>startPosition</c>, so
        /// <c>SelectRoomForPosition</c> takes the <c>beg0</c> branch (<c>WorldBuilder.cs:306-309</c>) and the
        /// case is deterministic without reaching into the generator's RNG.</para>
        /// </summary>
        [Test]
        public void BuildRandomWorld_TemplateWithBackgroundRoom_RegistersInteriorAtZPlusOne()
        {
            // Arrange
            List<RoomTemplate> templates = new List<RoomTemplate>
            {
                CreateTestTemplate("beg0_1", "beg0"),
                CreateTestTemplate("beg0_1_back", "back"),
            };
            templates[0].backgroundRoomId = "beg0_1_back";
            templates[1].allowRandom = false;

            var map = new LandMap();
            var generator = new RoomGenerator();
            var builder = new WorldBuilder();
            generator.Initialize(templates);
            builder.Initialize(map, generator, templates);

            // Act
            bool built = builder.BuildRandomWorld(
                stage: 1,
                customMin: new Vector3Int(0, 0, 0),
                customMax: new Vector3Int(1, 1, 1));

            // Assert
            Assert.That(built, Is.True);

            RoomInstance cell = map.GetRoom(new Vector3Int(0, 0, 0));
            Assert.That(cell, Is.Not.Null, "the single cell room should have been built");
            Assert.That(cell.templateId, Is.EqualTo("beg0_1"),
                "the start cell must have taken the beg0 template that declares the interior");
            Assert.That(cell.hasBackgroundLayer, Is.True,
                "the cell must know it owns an interior — ReportMissingOppositeLayer keys its message on this");

            Assert.That(map.HasRoom(new Vector3Int(0, 0, 1)), Is.True,
                "the interior belongs at z + 1 of its cell, in the room grid itself");

            RoomInstance interior = map.GetRoom(new Vector3Int(0, 0, 1));
            Assert.That(interior.noMap, Is.True,
                "the interior shares its cell's minimap tile, so it must be flagged noMap");

            Assert.That(map.GetLayerToggleTarget(new Vector3Int(0, 0, 0)),
                Is.EqualTo(new Vector3Int(0, 0, 1)),
                "the Z-door toggle must resolve — this is the exact call ComeInAction.CanExecute makes, " +
                "and a null here is the 'the door does nothing' symptom");

            Assert.That(map.GetLayerToggleTarget(new Vector3Int(0, 0, 1)),
                Is.EqualTo(new Vector3Int(0, 0, 0)),
                "…and it must resolve back the other way, or the player is sealed inside");
        }

        /// <summary>
        /// The absent control for the case above: a cell whose template declares <b>no</b> interior must get
        /// no <c>z + 1</c> room, and its toggle must refuse. Without this, an implementation that registered
        /// a layer for every cell would pass the positive case.
        /// </summary>
        [Test]
        public void BuildRandomWorld_TemplateWithoutBackgroundRoom_RegistersNoInterior()
        {
            // Arrange
            List<RoomTemplate> templates = new List<RoomTemplate>
            {
                CreateTestTemplate("beg0_1", "beg0"),
            };

            var map = new LandMap();
            var generator = new RoomGenerator();
            var builder = new WorldBuilder();
            generator.Initialize(templates);
            builder.Initialize(map, generator, templates);

            // Act
            bool built = builder.BuildRandomWorld(
                stage: 1,
                customMin: new Vector3Int(0, 0, 0),
                customMax: new Vector3Int(1, 1, 1));

            // Assert
            Assert.That(built, Is.True);

            RoomInstance cell = map.GetRoom(new Vector3Int(0, 0, 0));
            Assert.That(cell, Is.Not.Null);
            Assert.That(cell.hasBackgroundLayer, Is.False,
                "a template with no `backgroundRoomId` declares no interior");
            Assert.That(map.HasRoom(new Vector3Int(0, 0, 1)), Is.False,
                "nothing may be invented at z + 1");
            Assert.That(map.GetLayerToggleTarget(new Vector3Int(0, 0, 0)), Is.Null,
                "with no interior the toggle must refuse rather than resolve to a room that is not there");
        }

        /// <summary>
        /// The authored path registers the interior the same way — asserted because the two paths are the
        /// pair that drifted apart, and this is the half that was already right. If a future change moves
        /// one and not the other, exactly one of these two tests goes red.
        /// </summary>
        [Test]
        public void BuildSpecificWorld_TemplateWithBackgroundRoom_RegistersInteriorAtZPlusOne()
        {
            // Arrange
            List<RoomTemplate> templates = new List<RoomTemplate>
            {
                CreateTestTemplate("room1", "beg0", new Vector3Int(0, 0, 0)),
                CreateTestTemplate("room2", "pass", new Vector3Int(1, 0, 0)),
                // Not placeable: `HasFixedPosition` rejects a negative z, so the interior can only arrive
                // through `backgroundRoomId`.
                CreateTestTemplate("room1_back", "back"),
            };
            templates[0].backgroundRoomId = "room1_back";
            templates[2].allowRandom = false;

            var map = new LandMap();
            var generator = new RoomGenerator();
            var builder = new WorldBuilder();
            generator.Initialize(templates);
            builder.Initialize(map, generator, templates);

            // Act
            bool built = builder.BuildSpecificWorld(templates);

            // Assert
            Assert.That(built, Is.True);
            Assert.That(map.HasRoom(new Vector3Int(0, 0, 1)), Is.True);
            Assert.That(map.GetLayerToggleTarget(new Vector3Int(0, 0, 0)),
                Is.EqualTo(new Vector3Int(0, 0, 1)));
            Assert.That(map.GetRoom(new Vector3Int(1, 0, 1)), Is.Null,
                "only the cell whose template declares an interior gets one");
        }

        #region Test Helpers

        private List<RoomTemplate> CreateTestTemplates()
        {
            List<RoomTemplate> templates = new List<RoomTemplate>();

            // Starting rooms
            templates.Add(CreateTestTemplate("beg0_1", "beg0"));
            templates.Add(CreateTestTemplate("beg0_2", "beg0"));

            // Standard rooms
            for (int i = 0; i < 10; i++)
            {
                templates.Add(CreateTestTemplate($"pass_{i}", "pass"));
            }

            // Rooftop rooms
            templates.Add(CreateTestTemplate("roof_1", "roof"));
            templates.Add(CreateTestTemplate("roof_2", "roof"));

            return templates;
        }

        private RoomTemplate CreateTestTemplate(string id, string type, Vector3Int? fixedPos = null)
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.id = id;
            template.type = type;
            template.difficultyLevel = 0;
            template.maxInstances = 2;
            template.allowRandom = true;

            // Set fixed position if provided
            if (fixedPos.HasValue)
            {
                template.fixedPosition = fixedPos.Value;
            }
            else
            {
                template.fixedPosition = new Vector3Int(-1, -1, -1);
            }

            // Create default door configuration (all doors narrow quality)
            template.doorQuality = new int[24];
            for (int i = 0; i < 24; i++)
            {
                template.doorQuality[i] = (int)DoorQuality.Narrow;
            }

            // Create minimal tile data
            template.tileDataString = "";
            for (int y = 0; y < 27; y++)
            {
                template.tileDataString += new string('.', 48) + "\n"; // All air tiles
            }

            return template;
        }

        #endregion
    }
}

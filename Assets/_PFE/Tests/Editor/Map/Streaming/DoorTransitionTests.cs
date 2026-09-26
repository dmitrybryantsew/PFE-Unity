using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Interaction;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Map.Streaming;

namespace PFE.Tests.Editor.Map.Streaming
{
    [TestFixture]
    public class DoorTransitionTests
    {
        private GameObject _holderGo;
        private RoomInstance _room;

        /// <summary>
        /// A tile that a 40x80 px door prop sitting at (200, 100) actually covers.
        ///
        /// <para>The tests below used to assert on tile (10, 5). That is off by a factor of two on
        /// both axes: <see cref="WorldConstants.TILE_SIZE"/> is 40 px, so the door spans
        /// 180..219 px =&gt; tiles 4..5 in X, and 100..179 px =&gt; tiles 2..4 in Y. Tile (10, 5) is
        /// never stamped, so those assertions passed only while they expected "Air" by accident and
        /// failed the moment they expected "Wall".</para>
        /// </summary>
        private static readonly Vector2Int DoorCoveredTile = new Vector2Int(4, 2);

        [SetUp]
        public void Setup()
        {
            _holderGo = new GameObject("DoorTestHolder");
            _room = new RoomInstance
            {
                id = "room_test",
                landPosition = new Vector3Int(1, 0, 0),
                width = 48,
                height = 25
            };
            _room.InitializeTiles();
        }

        [TearDown]
        public void Teardown()
        {
            if (_holderGo != null)
            {
                Object.DestroyImmediate(_holderGo);
            }
        }

        [Test]
        public void ComputeSpawnPosition_LeftDoor_SpawnsInsideRoomToRight()
        {
            var fromDoor = new DoorInstance { targetDoorIndex = 11 };
            var targetDoor = new DoorInstance
            {
                doorIndex = 11,
                side = DoorSide.Left,
                tilePosition = new Vector2Int(0, 15)
            };
            _room.doors = new List<DoorInstance> { targetDoor };

            Vector3 spawn = RoomTransitionManager.ComputeSpawnPosition(fromDoor, _room, Vector3.zero, offsetUnits: 0.4f);

            Vector2 doorTilePixel = WorldCoordinates.TileToPixel(targetDoor.tilePosition) +
                                    new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f);
            Vector3 doorWorldUnity = WorldCoordinates.PixelToUnity(WorldCoordinates.LandToWorld(_room.landPosition, doorTilePixel));

            // Left door must push player to the RIGHT (+X)
            Assert.That(spawn.x, Is.GreaterThan(doorWorldUnity.x));
            Assert.That(spawn.x, Is.EqualTo(doorWorldUnity.x + 0.4f).Within(0.001f));
        }

        [Test]
        public void ComputeSpawnPosition_RightDoor_SpawnsInsideRoomToLeft()
        {
            var fromDoor = new DoorInstance { targetDoorIndex = 3 };
            var targetDoor = new DoorInstance
            {
                doorIndex = 3,
                side = DoorSide.Right,
                tilePosition = new Vector2Int(47, 15)
            };
            _room.doors = new List<DoorInstance> { targetDoor };

            Vector3 spawn = RoomTransitionManager.ComputeSpawnPosition(fromDoor, _room, Vector3.zero, offsetUnits: 0.4f);

            Vector2 doorTilePixel = WorldCoordinates.TileToPixel(targetDoor.tilePosition) +
                                    new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f);
            Vector3 doorWorldUnity = WorldCoordinates.PixelToUnity(WorldCoordinates.LandToWorld(_room.landPosition, doorTilePixel));

            // Right door must push player to the LEFT (-X)
            Assert.That(spawn.x, Is.LessThan(doorWorldUnity.x));
            Assert.That(spawn.x, Is.EqualTo(doorWorldUnity.x - 0.4f).Within(0.001f));
        }

        [Test]
        public void ComputeSpawnPosition_TopDoor_SpawnsInsideRoomDown()
        {
            var fromDoor = new DoorInstance { targetDoorIndex = 18 };
            var targetDoor = new DoorInstance
            {
                doorIndex = 18,
                side = DoorSide.Top,
                tilePosition = new Vector2Int(20, 24)
            };
            _room.doors = new List<DoorInstance> { targetDoor };

            Vector3 spawn = RoomTransitionManager.ComputeSpawnPosition(fromDoor, _room, Vector3.zero, offsetUnits: 0.4f);

            Vector2 doorTilePixel = WorldCoordinates.TileToPixel(targetDoor.tilePosition) +
                                    new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f);
            Vector3 doorWorldUnity = WorldCoordinates.PixelToUnity(WorldCoordinates.LandToWorld(_room.landPosition, doorTilePixel));

            // Top door must push player DOWN (-Y)
            Assert.That(spawn.y, Is.LessThan(doorWorldUnity.y));
            Assert.That(spawn.y, Is.EqualTo(doorWorldUnity.y - 0.4f).Within(0.001f));
        }

        [Test]
        public void ComputeSpawnPosition_BottomDoor_SpawnsInsideRoomUp()
        {
            var fromDoor = new DoorInstance { targetDoorIndex = 8 };
            var targetDoor = new DoorInstance
            {
                doorIndex = 8,
                side = DoorSide.Bottom,
                tilePosition = new Vector2Int(20, 0)
            };
            _room.doors = new List<DoorInstance> { targetDoor };

            Vector3 spawn = RoomTransitionManager.ComputeSpawnPosition(fromDoor, _room, Vector3.zero, offsetUnits: 0.4f);

            Vector2 doorTilePixel = WorldCoordinates.TileToPixel(targetDoor.tilePosition) +
                                    new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f);
            Vector3 doorWorldUnity = WorldCoordinates.PixelToUnity(WorldCoordinates.LandToWorld(_room.landPosition, doorTilePixel));

            // Bottom door must push player UP (+Y)
            Assert.That(spawn.y, Is.GreaterThan(doorWorldUnity.y));
            Assert.That(spawn.y, Is.EqualTo(doorWorldUnity.y + 0.4f).Within(0.001f));
        }

        [Test]
        public void DoorPropPresenter_TogglesTileCollisionAndState()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f), // Covers tiles X 4..5, Y 2..4 at TILE_SIZE 40
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };

            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80); // 2x4 tiles

            var go = new GameObject("TestDoor");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            presenter.Initialize(_room, doorObj, visualDef, renderer, null);

            TileData tileClosed = _room.GetTileAtCoord(DoorCoveredTile);
            Assert.That(tileClosed.physicsType, Is.EqualTo(TilePhysicsType.Wall), "Closed door must stamp Wall collision");
            Assert.That(presenter.IsOpen, Is.False);

            // Open door
            presenter.SetOpen(true);
            TileData tileOpened = _room.GetTileAtCoord(DoorCoveredTile);
            Assert.That(tileOpened.physicsType, Is.EqualTo(TilePhysicsType.Air), "Open door must clear Wall collision to Air");
            Assert.That(presenter.IsOpen, Is.True);

            // Close door again
            presenter.SetOpen(false);
            TileData tileReclosed = _room.GetTileAtCoord(DoorCoveredTile);
            Assert.That(tileReclosed.physicsType, Is.EqualTo(TilePhysicsType.Wall), "Re-closed door must restore Wall collision");
            Assert.That(presenter.IsOpen, Is.False);

            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void AreaTriggerSystem_ExecuteAction_OpenAndClose_UpdatesDoorTileCollision()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                uid = "corridorDoor",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };
            _room.objects = new List<ObjectInstance> { doorObj };

            var triggerSystem = new AreaTriggerSystem();

            // Execute script action "open"
            var openAction = new MapObjectScriptActionData { act = "open", targ = "corridorDoor" };
            triggerSystem.ExecuteAction(_room, openAction);

            Assert.That(doorObj.runtimeState.isOpen, Is.True);
            TileData openTile = _room.GetTileAtCoord(DoorCoveredTile);
            Assert.That(openTile.physicsType, Is.EqualTo(TilePhysicsType.Air));

            // Execute script action "close"
            var closeAction = new MapObjectScriptActionData { act = "close", targ = "corridorDoor" };
            triggerSystem.ExecuteAction(_room, closeAction);

            Assert.That(doorObj.runtimeState.isOpen, Is.False);
            TileData closedTile = _room.GetTileAtCoord(DoorCoveredTile);
            Assert.That(closedTile.physicsType, Is.EqualTo(TilePhysicsType.Wall));
        }

        [Test]
        public void DoorTrigger_IsReadyForTransition_And_SetEnabled()
        {
            var go = new GameObject("TestDoorTrigger");
            go.transform.SetParent(_holderGo.transform);
            var trigger = go.AddComponent<DoorTrigger>();

            var door = new DoorInstance
            {
                doorIndex = 0,
                side = DoorSide.Right,
                isActive = true
            };

            trigger.SetDoor(door, _room);
            Assert.That(trigger.IsReadyForTransition(), Is.True);

            trigger.SetEnabled(false);
            Assert.That(trigger.IsReadyForTransition(), Is.False);

            trigger.SetEnabled(true);
            Assert.That(trigger.IsReadyForTransition(), Is.True);
        }

        [Test]
        public void DoorPropPresenter_IInteractable_PlayerCanOpenAndCloseDoor()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };

            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80);

            var go = new GameObject("InteractableDoor");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            presenter.Initialize(_room, doorObj, visualDef, renderer, null);

            var playerGo = new GameObject("TestPlayer");
            playerGo.transform.SetParent(_holderGo.transform);
            playerGo.transform.position = new Vector3(10f, 10f, 0f); // Far away (> 2.5m)

            IInteractable interactable = presenter;

            // When player is far away:
            Assert.That(interactable.CanInteract(playerGo), Is.False);
            Assert.That(interactable.ActionText, Is.EqualTo("Open"));

            // Move player within reach (e.g. 1.2m away)
            playerGo.transform.position = new Vector3(1.2f, 0f, 0f);
            Assert.That(interactable.CanInteract(playerGo), Is.True);

            // Player approaches door. CanInteract(user) measures the distance to the user, so
            // proximity alone is enough here — there is no need to fake an OnTriggerEnter2D.
            // Driving a physics callback through SendMessage in EditMode trips Unity's internal
            // "Assertion failed on expression: 'ShouldRunBehaviour()'", which fails the test.
            playerGo.tag = "Player";

            Assert.That(interactable.CanInteract(playerGo), Is.True);
            Assert.That(interactable.ActionText, Is.EqualTo("Open"));

            // Player presses interact ('E') -> Door opens
            interactable.Interact(playerGo);
            Assert.That(presenter.IsOpen, Is.True);
            Assert.That(interactable.ActionText, Is.EqualTo("Close"));
            Assert.That(_room.GetTileAtCoord(DoorCoveredTile).physicsType, Is.EqualTo(TilePhysicsType.Air));

            // Interact() is deliberately rate-limited to one toggle per rendered frame (it guards
            // on Time.frameCount). An EditMode test never renders a frame between two calls, so a
            // second press in the same frame is a no-op — assert that contract explicitly instead
            // of relying on OnInteractPressed() to get through.
            interactable.Interact(playerGo);
            Assert.That(presenter.IsOpen, Is.True, "second Interact() in the same frame must be ignored");

            // Close through ToggleOpen(), which is what a real second press does on a later frame.
            presenter.ToggleOpen();
            Assert.That(presenter.IsOpen, Is.False);
            Assert.That(interactable.ActionText, Is.EqualTo("Open"));
            Assert.That(_room.GetTileAtCoord(DoorCoveredTile).physicsType, Is.EqualTo(TilePhysicsType.Wall));

            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void DoorPropPresenter_Animation_StepsFramesSequentially()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };

            var dummyTex = new Texture2D(4, 4);
            var s0 = Sprite.Create(dummyTex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f));
            var s1 = Sprite.Create(dummyTex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f));
            var s2 = Sprite.Create(dummyTex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f));

            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80);
            visualDef.frames = new[] { s0, s1, s2 };

            var go = new GameObject("AnimatedDoor");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            presenter.Initialize(_room, doorObj, visualDef, renderer, null);

            // Initially closed at frame 0
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));
            Assert.That(renderer.sprite, Is.EqualTo(s0));

            // Start opening -> targets frame 2
            presenter.SetOpen(true);
            Assert.That(presenter.TargetFrame, Is.EqualTo(2));
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0)); // Hasn't stepped yet

            // Partial tick (< 0.075s) should stay at frame 0
            presenter.TickAnimation(0.04f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));

            // Total elapsed reaches 0.08s (>= FrameDuration 0.075s) -> steps to frame 1
            presenter.TickAnimation(0.04f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(1));
            Assert.That(renderer.sprite, Is.EqualTo(s1));

            // Another frame duration -> steps to frame 2 (fully open)
            presenter.TickAnimation(0.08f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(2));
            Assert.That(renderer.sprite, Is.EqualTo(s2));

            // Start closing -> targets frame 0
            presenter.SetOpen(false);
            Assert.That(presenter.TargetFrame, Is.EqualTo(0));
            Assert.That(presenter.CurrentFrame, Is.EqualTo(2));

            // Step down to frame 1
            presenter.TickAnimation(0.08f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(1));
            Assert.That(renderer.sprite, Is.EqualTo(s1));

            // Step down to frame 0 (fully closed)
            presenter.TickAnimation(0.08f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));
            Assert.That(renderer.sprite, Is.EqualTo(s0));

            Object.DestroyImmediate(visualDef);
            Object.DestroyImmediate(dummyTex);
        }

        [Test]
        public void DoorPropPresenter_ClosingDoor_WithPlayerInside_PushesPlayerToSideNotUp()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = true }
            };

            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80); // Width = 0.40m, Height = 0.80m

            var go = new GameObject("TestEjectionDoor");
            go.transform.SetParent(_holderGo.transform);
            go.transform.position = new Vector3(2.0f, 1.0f, 0f); // Door center X = 2.0, bottom Y = 1.0
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            presenter.Initialize(_room, doorObj, visualDef, renderer, null);

            var playerGo = new GameObject("Player");
            playerGo.tag = "Player";
            playerGo.transform.SetParent(_holderGo.transform);

            // 1. Place player inside door, slightly left of door center
            playerGo.transform.position = new Vector3(1.95f, 1.0f, 0f);

            // Door closes while player is inside
            presenter.SetOpen(false);

            // Assert: Player must NOT be popped upward (Y must remain at 1.0f floor level)
            Assert.That(playerGo.transform.position.y, Is.EqualTo(1.0f).Within(0.001f),
                "Player Y must NOT be displaced upward when door closes");

            // Assert: Player must be pushed horizontally to the LEFT outside door bounds (doorLeft = 1.80f)
            Assert.That(playerGo.transform.position.x, Is.LessThan(1.80f),
                "Player must be pushed horizontally to the left of the door");

            // 2. Place player inside door, slightly right of door center
            playerGo.transform.position = new Vector3(2.05f, 1.0f, 0f);
            presenter.SetOpen(true); // Open first
            presenter.SetOpen(false); // Close with player inside

            Assert.That(playerGo.transform.position.y, Is.EqualTo(1.0f).Within(0.001f),
                "Player Y must NOT be displaced upward when door closes");

            // Assert: Player must be pushed horizontally to the RIGHT outside door bounds (doorRight = 2.20f)
            Assert.That(playerGo.transform.position.x, Is.GreaterThan(2.20f),
                "Player must be pushed horizontally to the right of the door");

            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void ComputeEdgeSpawnPosition_Direction2_Right_EntersLeftOfTargetRoom()
        {
            var fromRoom = new RoomInstance
            {
                id = "room_1",
                landPosition = new Vector3Int(1, 0, 0),
                width = 48,
                height = 25
            };
            var toRoom = new RoomInstance
            {
                id = "room_2",
                landPosition = new Vector3Int(2, 0, 0),
                width = 48,
                height = 25
            };

            // Player exits right edge of room_1 at Y = 5.0m
            Vector3 fromOrigin = RoomTransitionManager.GetRoomOriginUnity(fromRoom);
            Vector3 playerPos = new Vector3(fromOrigin.x + 19.2f, fromOrigin.y + 5.0f, 0f);

            Vector3 spawn = RoomTransitionManager.ComputeEdgeSpawnPosition(2, fromRoom, toRoom, playerPos, offsetUnits: 0.4f);

            Vector3 toOrigin = RoomTransitionManager.GetRoomOriginUnity(toRoom);
            // Must enter toRoom at left edge + 0.4m
            Assert.That(spawn.x, Is.EqualTo(toOrigin.x + 0.4f).Within(0.001f));
            // Must preserve relative Y height
            Assert.That(spawn.y, Is.EqualTo(toOrigin.y + 5.0f).Within(0.001f));
        }

        [Test]
        public void ComputeEdgeSpawnPosition_Direction1_Left_EntersRightOfTargetRoom()
        {
            var fromRoom = new RoomInstance
            {
                id = "room_2",
                landPosition = new Vector3Int(2, 0, 0),
                width = 48,
                height = 25
            };
            var toRoom = new RoomInstance
            {
                id = "room_1",
                landPosition = new Vector3Int(1, 0, 0),
                width = 48,
                height = 25
            };

            // Player exits left edge of room_2 at Y = 3.0m
            Vector3 fromOrigin = RoomTransitionManager.GetRoomOriginUnity(fromRoom);
            Vector3 playerPos = new Vector3(fromOrigin.x - 0.1f, fromOrigin.y + 3.0f, 0f);

            Vector3 spawn = RoomTransitionManager.ComputeEdgeSpawnPosition(1, fromRoom, toRoom, playerPos, offsetUnits: 0.4f);

            Vector3 toOrigin = RoomTransitionManager.GetRoomOriginUnity(toRoom);
            float toWidth = toRoom.width * WorldConstants.TILE_SIZE * 0.01f;
            // Must enter toRoom at right edge - 0.4m
            Assert.That(spawn.x, Is.EqualTo(toOrigin.x + toWidth - 0.4f).Within(0.001f));
            // Must preserve relative Y height
            Assert.That(spawn.y, Is.EqualTo(toOrigin.y + 3.0f).Within(0.001f));
        }

        [Test]
        public void ComputeEdgeSpawnPosition_Direction3_Bottom_EntersTopOfTargetRoom()
        {
            var fromRoom = new RoomInstance
            {
                id = "room_top",
                landPosition = new Vector3Int(0, 1, 0),
                width = 48,
                height = 25
            };
            var toRoom = new RoomInstance
            {
                id = "room_bottom",
                landPosition = new Vector3Int(0, 0, 0),
                width = 48,
                height = 25
            };

            Vector3 fromOrigin = RoomTransitionManager.GetRoomOriginUnity(fromRoom);
            Vector3 playerPos = new Vector3(fromOrigin.x + 8.0f, fromOrigin.y - 0.1f, 0f);

            Vector3 spawn = RoomTransitionManager.ComputeEdgeSpawnPosition(3, fromRoom, toRoom, playerPos, offsetUnits: 0.4f);

            Vector3 toOrigin = RoomTransitionManager.GetRoomOriginUnity(toRoom);
            float toHeight = toRoom.height * WorldConstants.TILE_SIZE * 0.01f;
            Assert.That(spawn.x, Is.EqualTo(toOrigin.x + 8.0f).Within(0.001f));
            Assert.That(spawn.y, Is.EqualTo(toOrigin.y + toHeight - 0.4f).Within(0.001f));
        }

        [Test]
        public void ComputeEdgeSpawnPosition_Direction4_Top_EntersBottomOfTargetRoom()
        {
            var fromRoom = new RoomInstance
            {
                id = "room_bottom",
                landPosition = new Vector3Int(0, 0, 0),
                width = 48,
                height = 25
            };
            var toRoom = new RoomInstance
            {
                id = "room_top",
                landPosition = new Vector3Int(0, 1, 0),
                width = 48,
                height = 25
            };

            Vector3 fromOrigin = RoomTransitionManager.GetRoomOriginUnity(fromRoom);
            Vector3 playerPos = new Vector3(fromOrigin.x + 8.0f, fromOrigin.y + 10.5f, 0f);

            Vector3 spawn = RoomTransitionManager.ComputeEdgeSpawnPosition(4, fromRoom, toRoom, playerPos, offsetUnits: 0.4f);

            Vector3 toOrigin = RoomTransitionManager.GetRoomOriginUnity(toRoom);
            Assert.That(spawn.x, Is.EqualTo(toOrigin.x + 8.0f).Within(0.001f));
            Assert.That(spawn.y, Is.EqualTo(toOrigin.y + 0.4f).Within(0.001f));
        }

        [Test]
        public void RoomVisualController_ClearVisuals_DestroysChildrenAndResetsState()
        {
            var go = new GameObject("TestRVC");
            go.transform.SetParent(_holderGo.transform);
            var rvc = go.AddComponent<RoomVisualController>();

            // Create some dummy child under DoorTriggers
            var doorTriggers = new GameObject("DoorTriggers");
            doorTriggers.transform.SetParent(go.transform);
            var childTrigger = new GameObject("ChildTrigger");
            childTrigger.transform.SetParent(doorTriggers.transform);

            Assert.That(doorTriggers.transform.childCount, Is.EqualTo(1));

            rvc.ClearVisuals();

            Assert.That(doorTriggers.transform.childCount, Is.EqualTo(0));
            Assert.That(rvc.IsVisible, Is.False);
        }

        [Test]
        public void BuildSpecificWorld_BuildsDoorConnections_BetweenAdjacentRooms()
        {
            var landMap = new LandMap();
            var generator = new RoomGenerator();
            var worldBuilder = new WorldBuilder();

            var t1 = ScriptableObject.CreateInstance<RoomTemplate>();
            t1.id = "room_1";
            t1.fixedPosition = new Vector3Int(1, 0, 0);
            t1.doorQuality = new int[24];
            t1.doorQuality[3] = 2; // Right door 3
            t1.tileDataString = "";

            var t2 = ScriptableObject.CreateInstance<RoomTemplate>();
            t2.id = "room_2";
            t2.fixedPosition = new Vector3Int(2, 0, 0);
            t2.doorQuality = new int[24];
            t2.doorQuality[14] = 2; // Left door 14 (matches right door 3)
            t2.tileDataString = "";

            var templates = new List<RoomTemplate> { t1, t2 };
            generator.Initialize(templates);
            worldBuilder.Initialize(landMap, generator, templates);
            bool success = worldBuilder.BuildSpecificWorld(templates, new Vector3Int(1, 0, 0));

            Assert.IsTrue(success);
            Assert.AreEqual(2, landMap.GetRoomCount());

            var r1 = landMap.GetRoom(new Vector3Int(1, 0, 0));
            var r2 = landMap.GetRoom(new Vector3Int(2, 0, 0));
            Assert.IsNotNull(r1);
            Assert.IsNotNull(r2);

            var doorR1 = r1.doors.Find(d => d.doorIndex == 3);
            var doorR2 = r2.doors.Find(d => d.doorIndex == 14);

            Assert.IsNotNull(doorR1);
            Assert.IsNotNull(doorR2);

            Assert.IsTrue(doorR1.isActive, "doorR1 should be active");
            Assert.IsTrue(doorR2.isActive, "doorR2 should be active");
            Assert.AreEqual(new Vector3Int(2, 0, 0), doorR1.targetRoomPosition);
            Assert.AreEqual(14, doorR1.targetDoorIndex);
            Assert.AreEqual(new Vector3Int(1, 0, 0), doorR2.targetRoomPosition);
            Assert.AreEqual(3, doorR2.targetDoorIndex);

            Object.DestroyImmediate(t1);
            Object.DestroyImmediate(t2);
        }

        [Test]
        public void DoorPropPresenter_Barricade_CannotBeInteractedOrOpened()
        {
            var septumObj = new ObjectInstance
            {
                objectId = "septum",
                definitionId = "septum",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };
            septumObj.attributes.Add(new MapObjectAttributeData { key = "inter", value = "0" });

            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80);

            var go = new GameObject("SeptumBarricade");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            presenter.Initialize(_room, septumObj, visualDef, renderer, null);

            var playerGo = new GameObject("TestPlayer");
            playerGo.transform.SetParent(_holderGo.transform);
            playerGo.transform.position = new Vector3(0.5f, 0f, 0f); // Right next to barricade (0.5m)

            IInteractable interactable = presenter;

            // Barricade is NOT interactable
            Assert.That(presenter.IsInteractableDoor, Is.False);
            Assert.That(interactable.CanInteract(playerGo), Is.False);
            Assert.That(interactable.ActionText, Is.EqualTo(string.Empty));

            // Pressing interact does NOT open barricade
            interactable.Interact(playerGo);
            Assert.That(presenter.IsOpen, Is.False);

            presenter.ToggleOpen();
            Assert.That(presenter.IsOpen, Is.False);

            // Barricade still stamps solid wall collision
            Assert.That(_room.GetTileAtCoord(DoorCoveredTile).physicsType, Is.EqualTo(TilePhysicsType.Wall));
        }

        [Test]
        public void AreaTriggerPresenter_DebugVisualsToggle()
        {
            var areaObj = new ObjectInstance
            {
                objectId = "area",
                objectType = "area",
                position = new Vector2(200f, 100f),
                uid = "trTest"
            };

            var go = new GameObject("TestAreaTrigger");
            go.transform.SetParent(_holderGo.transform);
            var presenter = go.AddComponent<AreaTriggerPresenter>();

            AreaTriggerPresenter.SetDebugOverride(false);
            presenter.Initialize(_room, areaObj, null);

            var debugChild = go.transform.Find("__AreaDebugVisual");
            Assert.That(debugChild == null || !debugChild.gameObject.activeSelf, Is.True);

            AreaTriggerPresenter.SetDebugOverride(true);
            // OnEnable never runs for a component added in EditMode, so _activePresenters is empty
            // and the static broadcast has no listener. Call the instance method the broadcast
            // would have called — that is the path that actually creates the overlay.
            presenter.UpdateDebugVisual();
            debugChild = go.transform.Find("__AreaDebugVisual");
            Assert.That(debugChild, Is.Not.Null);
            Assert.That(debugChild.gameObject.activeSelf, Is.True);

            var spriteRenderer = debugChild.GetComponent<SpriteRenderer>();
            Assert.That(spriteRenderer, Is.Not.Null);
            Assert.That(spriteRenderer.color.r, Is.GreaterThan(0.5f)); // Purple / magenta component
            Assert.That(spriteRenderer.color.b, Is.GreaterThan(0.7f));

            AreaTriggerPresenter.SetDebugOverride(false);
            presenter.UpdateDebugVisual();
            Assert.That(debugChild.gameObject.activeSelf, Is.False);

            AreaTriggerPresenter.SetDebugOverride(null);
        }

        [Test]
        public void AreaTriggerPresenter_ResolveAreaType_CorrectlyIdentifiesTypes()
        {
            // Attribute override: passage
            var objPass = new ObjectInstance
            {
                objectId = "area",
                attributes = new List<MapObjectAttributeData> { new() { key = "areaType", value = "passage" } }
            };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(objPass), Is.EqualTo(AreaTriggerType.Passage));

            // Attribute override: teleport
            var objTele = new ObjectInstance
            {
                objectId = "area",
                attributes = new List<MapObjectAttributeData> { new() { key = "areaType", value = "teleport" } }
            };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(objTele), Is.EqualTo(AreaTriggerType.Teleport));

            // Attribute override: hazard
            var objHaz = new ObjectInstance
            {
                objectId = "area",
                attributes = new List<MapObjectAttributeData> { new() { key = "areaType", value = "hazard" } }
            };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(objHaz), Is.EqualTo(AreaTriggerType.Hazard));

            // Attribute override: event
            var objEvt = new ObjectInstance
            {
                objectId = "area",
                attributes = new List<MapObjectAttributeData> { new() { key = "areaType", value = "event" } }
            };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(objEvt), Is.EqualTo(AreaTriggerType.Event));

            // Naming convention: trTele (teleport / level exit)
            var teleObj = new ObjectInstance { objectId = "area", uid = "trTele1" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(teleObj), Is.EqualTo(AreaTriggerType.Teleport));

            var exitObj = new ObjectInstance { objectId = "area", uid = "exit_zone" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(exitObj), Is.EqualTo(AreaTriggerType.Teleport));

            // Naming convention: passage / shaft / ladder
            var shaftObj = new ObjectInstance { objectId = "area", uid = "shaftLadder" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(shaftObj), Is.EqualTo(AreaTriggerType.Passage));

            var passObj = new ObjectInstance { objectId = "area", uid = "passCorridor" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(passObj), Is.EqualTo(AreaTriggerType.Passage));

            // Naming convention: hazard / rad / trap
            var radObj = new ObjectInstance { objectId = "area", uid = "radZone" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(radObj), Is.EqualTo(AreaTriggerType.Hazard));

            // Default event: trSit, trDownJump, trPunch
            var defaultObj = new ObjectInstance { objectId = "area", uid = "trSit" };
            Assert.That(AreaTriggerPresenter.ResolveAreaType(defaultObj), Is.EqualTo(AreaTriggerType.Event));
        }

        [Test]
        public void AreaTriggerPresenter_ColorsByType_AreDistinct()
        {
            Color eventFill = AreaTriggerPresenter.GetFillColor(AreaTriggerType.Event);
            Color passFill = AreaTriggerPresenter.GetFillColor(AreaTriggerType.Passage);
            Color teleFill = AreaTriggerPresenter.GetFillColor(AreaTriggerType.Teleport);
            Color hazFill = AreaTriggerPresenter.GetFillColor(AreaTriggerType.Hazard);

            Assert.That(eventFill, Is.Not.EqualTo(passFill));
            Assert.That(passFill, Is.Not.EqualTo(teleFill));
            Assert.That(teleFill, Is.Not.EqualTo(hazFill));

            Color eventWire = AreaTriggerPresenter.GetWireColor(AreaTriggerType.Event);
            Color passWire = AreaTriggerPresenter.GetWireColor(AreaTriggerType.Passage);
            Color teleWire = AreaTriggerPresenter.GetWireColor(AreaTriggerType.Teleport);
            Color hazWire = AreaTriggerPresenter.GetWireColor(AreaTriggerType.Hazard);

            Assert.That(eventWire, Is.Not.EqualTo(passWire));
            Assert.That(passWire, Is.Not.EqualTo(teleWire));
            Assert.That(teleWire, Is.Not.EqualTo(hazWire));
        }

        [Test]
        public void DoorPropPresenter_DebugVisualsToggle()
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };
            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80);

            var go = new GameObject("TestDoorDebug");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();

            DoorPropPresenter.SetDebugOverride(false);
            presenter.Initialize(_room, doorObj, visualDef, renderer, null);

            var debugChild = go.transform.Find("__DoorDebugVisual");
            Assert.That(debugChild == null || !debugChild.gameObject.activeSelf, Is.True);

            DoorPropPresenter.SetDebugOverride(true);
            // OnEnable never runs for a component added in EditMode, so _activePresenters is empty
            // and the static broadcast has no listener. Call the instance method the broadcast
            // would have called — that is the path that actually creates the overlay.
            presenter.UpdateDebugVisual();
            debugChild = go.transform.Find("__DoorDebugVisual");
            Assert.That(debugChild, Is.Not.Null);
            Assert.That(debugChild.gameObject.activeSelf, Is.True);

            var spriteRenderer = debugChild.GetComponent<SpriteRenderer>();
            Assert.That(spriteRenderer, Is.Not.Null);
            Assert.That(spriteRenderer.color.r, Is.GreaterThan(0.8f)); // Yellow component

            DoorPropPresenter.SetDebugOverride(false);
            presenter.UpdateDebugVisual();
            Assert.That(debugChild.gameObject.activeSelf, Is.False);

            DoorPropPresenter.SetDebugOverride(null);
            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void ObjectColliderDebugPresenter_DebugVisualsToggle()
        {
            var obj = new ObjectInstance
            {
                objectId = "crate",
                objectType = "container",
                position = new Vector2(100f, 100f)
            };
            var visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(50, 50);

            var go = new GameObject("TestObjectDebug");
            go.transform.SetParent(_holderGo.transform);
            var presenter = go.AddComponent<ObjectColliderDebugPresenter>();

            ObjectColliderDebugPresenter.SetDebugOverride(false);
            presenter.Initialize(_room, obj, visualDef);

            var debugChild = go.transform.Find("__ObjectDebugVisual");
            Assert.That(debugChild == null || !debugChild.gameObject.activeSelf, Is.True);

            ObjectColliderDebugPresenter.SetDebugOverride(true);
            // OnEnable never runs for a component added in EditMode, so _activePresenters is empty
            // and the static broadcast has no listener. Call the instance method the broadcast
            // would have called — that is the path that actually creates the overlay.
            presenter.UpdateDebugVisual();
            debugChild = go.transform.Find("__ObjectDebugVisual");
            Assert.That(debugChild, Is.Not.Null);
            Assert.That(debugChild.gameObject.activeSelf, Is.True);

            var spriteRenderer = debugChild.GetComponent<SpriteRenderer>();
            Assert.That(spriteRenderer, Is.Not.Null);
            Assert.That(spriteRenderer.color.g, Is.GreaterThan(0.7f)); // Teal green component

            ObjectColliderDebugPresenter.SetDebugOverride(false);
            presenter.UpdateDebugVisual();
            Assert.That(debugChild.gameObject.activeSelf, Is.False);

            ObjectColliderDebugPresenter.SetDebugOverride(null);
            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void RoomTransitionManager_TransitionThroughEdge_ValidatesAllDirections()
        {
            var landMap = new LandMap();
            var current = new RoomInstance { id = "current", width = 48, height = 25 };
            var left = new RoomInstance { id = "left", width = 48, height = 25 };
            var right = new RoomInstance { id = "right", width = 48, height = 25 };
            var bottom = new RoomInstance { id = "bottom", width = 48, height = 25 };
            var top = new RoomInstance { id = "top", width = 48, height = 25 };

            landMap.AddRoom(current, new Vector3Int(1, 1, 0));
            landMap.AddRoom(left, new Vector3Int(0, 1, 0));
            landMap.AddRoom(right, new Vector3Int(2, 1, 0));
            landMap.AddRoom(bottom, new Vector3Int(1, 2, 0)); // Flash AS3: bottom room is y + 1
            landMap.AddRoom(top, new Vector3Int(1, 0, 0));    // Flash AS3: top room is y - 1
            landMap.SwitchRoom(new Vector3Int(1, 1, 0));

            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(_holderGo.transform);
            playerGo.transform.position = Vector3.zero;

            // Direction 1: Left (x - 1)
            var managerGo1 = new GameObject("Manager1");
            managerGo1.transform.SetParent(_holderGo.transform);
            var m1 = managerGo1.AddComponent<RoomTransitionManager>();
            m1.SetLandMap(landMap);
            Assert.That(m1.TransitionThroughEdge(1, playerGo), Is.True, "Direction 1 (Left) must start transition to (0, 1, 0)");

            // Direction 2: Right (x + 1)
            var managerGo2 = new GameObject("Manager2");
            managerGo2.transform.SetParent(_holderGo.transform);
            var m2 = managerGo2.AddComponent<RoomTransitionManager>();
            m2.SetLandMap(landMap);
            Assert.That(m2.TransitionThroughEdge(2, playerGo), Is.True, "Direction 2 (Right) must start transition to (2, 1, 0)");

            // Direction 3: Bottom (y + 1 in LandMap grid)
            var managerGo3 = new GameObject("Manager3");
            managerGo3.transform.SetParent(_holderGo.transform);
            var m3 = managerGo3.AddComponent<RoomTransitionManager>();
            m3.SetLandMap(landMap);
            Assert.That(m3.TransitionThroughEdge(3, playerGo), Is.True, "Direction 3 (Bottom) must start transition to (1, 2, 0)");

            // Direction 4: Top (y - 1 in LandMap grid)
            var managerGo4 = new GameObject("Manager4");
            managerGo4.transform.SetParent(_holderGo.transform);
            var m4 = managerGo4.AddComponent<RoomTransitionManager>();
            m4.SetLandMap(landMap);
            Assert.That(m4.TransitionThroughEdge(4, playerGo), Is.True, "Direction 4 (Top) must start transition to (1, 0, 0)");

            // Invalid direction
            Assert.That(m4.TransitionThroughEdge(99, playerGo), Is.False);
        }

        [Test]
        public void RoomVisualController_ClearVisuals_CallsDestroyAllTiles_WithoutThrowing()
        {
            var go = new GameObject("TestRVC_Clear");
            go.transform.SetParent(_holderGo.transform);
            var rvc = go.AddComponent<RoomVisualController>();

            Assert.DoesNotThrow(() => rvc.ClearVisuals());
        }
    }
}

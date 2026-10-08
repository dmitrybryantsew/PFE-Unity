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

        /// <summary>
        /// Builds a door sheet with <paramref name="frameCount"/> distinct frames. A 3-frame sheet is
        /// the close/open/die trio; any other count is a Z-door <c>comein</c> clip.
        /// </summary>
        private static MapObjectVisualDefinition CreateDoorSheet(Texture2D texture, int frameCount, out Sprite[] frames)
        {
            frames = new Sprite[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frames[i] = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f));
            }

            var visual = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visual.pixelSize = new Vector2Int(40, 80);
            visual.frames = frames;
            return visual;
        }

        private DoorPropPresenter CreateDoorPresenter(
            MapObjectVisualDefinition visual,
            out SpriteRenderer renderer,
            bool isOpen = false,
            bool isDestroyed = false)
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f),
                runtimeState = new MapObjectRuntimeStateData
                {
                    isOpen = isOpen,
                    isDestroyed = isDestroyed
                }
            };

            var go = new GameObject("AnimatedDoor");
            go.transform.SetParent(_holderGo.transform);
            renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();
            presenter.Initialize(_room, doorObj, visual, renderer, null);
            return presenter;
        }

        [Test]
        public void DoorPropPresenter_OpeningASheet_SeeksTheOpenLabelNotTheDestroyedFrame()
        {
            var texture = new Texture2D(4, 4);
            MapObjectVisualDefinition visual = CreateDoorSheet(texture, 3, out Sprite[] frames);
            DoorPropPresenter presenter = CreateDoorPresenter(visual, out SpriteRenderer renderer);

            // Closed -> the "close" label, which is frame 1 of the Flash timeline.
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));
            Assert.That(renderer.sprite, Is.EqualTo(frames[0]));

            // Open -> the "open" label (frames[] 1). It must NOT be frames[2]: that is the "die"
            // label, and showing it is the bug this test pins.
            presenter.SetOpen(true);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(1), "open must seek the 'open' label");
            Assert.That(renderer.sprite, Is.EqualTo(frames[1]));
            Assert.That(renderer.sprite, Is.Not.EqualTo(frames[2]), "frames[2] is 'die', not 'open'");

            // AS3 seeks rather than animates (Box.setVisState uses gotoAndStop), so ticking must not
            // walk any further — the old implementation stepped 0 -> 1 -> 2 over ~0.15s.
            presenter.TickAnimation(DoorPropPresenter.FrameDuration * 5f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(1), "open/close must not animate");
            Assert.That(renderer.sprite, Is.EqualTo(frames[1]));

            // Close -> back to the "close" label, immediately.
            presenter.SetOpen(false);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));
            Assert.That(renderer.sprite, Is.EqualTo(frames[0]));

            Object.DestroyImmediate(visual);
            Object.DestroyImmediate(texture);
        }

        [Test]
        public void DoorPropPresenter_DestroyedDoor_ShowsTheDieFrame()
        {
            var texture = new Texture2D(4, 4);
            MapObjectVisualDefinition visual = CreateDoorSheet(texture, 3, out Sprite[] frames);
            DoorPropPresenter presenter = CreateDoorPresenter(visual, out SpriteRenderer renderer, isDestroyed: true);

            Assert.That(presenter.IsDestroyed, Is.True);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(2), "destroyed must seek the 'die' label");
            Assert.That(renderer.sprite, Is.EqualTo(frames[2]));

            Object.DestroyImmediate(visual);
            Object.DestroyImmediate(texture);
        }

        [Test]
        public void DoorPropPresenter_ComeInSheet_HasNoOpenStateSoStaysClosed()
        {
            // Z-door sheets (indoor1..4 = 11 frames, instdoor/inbasedoor/inencldoor = 20) author only
            // a "comein" label. AS3 never opens them, and gotoAndStop("open") would throw and be
            // swallowed — so isOpen must resolve to the closed frame, not to comein's index.
            var texture = new Texture2D(4, 4);
            MapObjectVisualDefinition visual = CreateDoorSheet(texture, 11, out Sprite[] frames);
            Assert.That(visual.IsDoorStateSheet, Is.False);
            Assert.That(visual.OpenStateFrame, Is.EqualTo(-1));
            Assert.That(visual.DestroyedStateFrame, Is.EqualTo(-1));
            Assert.That(visual.ComeInFrame, Is.EqualTo(1));

            DoorPropPresenter presenter = CreateDoorPresenter(visual, out SpriteRenderer renderer, isOpen: true);

            Assert.That(presenter.CurrentFrame, Is.EqualTo(0));
            Assert.That(renderer.sprite, Is.EqualTo(frames[0]));

            Object.DestroyImmediate(visual);
            Object.DestroyImmediate(texture);
        }

        [Test]
        public void DoorPropPresenter_BeginComeIn_PlaysTheClipFromTheComeInFrame()
        {
            var texture = new Texture2D(4, 4);
            MapObjectVisualDefinition visual = CreateDoorSheet(texture, 11, out Sprite[] frames);
            DoorPropPresenter presenter = CreateDoorPresenter(visual, out SpriteRenderer renderer);

            Assert.That(presenter.IsPlayingComeIn, Is.False);

            presenter.BeginComeIn();
            Assert.That(presenter.IsPlayingComeIn, Is.True);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(1), "comein starts on its label frame");
            Assert.That(renderer.sprite, Is.EqualTo(frames[1]));

            presenter.TickAnimation(DoorPropPresenter.FrameDuration);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(2));

            // Run past the end: the clip stops on the last frame and clears its playing flag.
            presenter.TickAnimation(DoorPropPresenter.FrameDuration * 20f);
            Assert.That(presenter.CurrentFrame, Is.EqualTo(frames.Length - 1));
            Assert.That(presenter.IsPlayingComeIn, Is.False);

            Object.DestroyImmediate(visual);
            Object.DestroyImmediate(texture);
        }

        [Test]
        public void DoorPropPresenter_CanInteract_UsesTheAs3ReachOfTwoUnits()
        {
            var texture = new Texture2D(4, 4);
            MapObjectVisualDefinition visual = CreateDoorSheet(texture, 3, out _);
            DoorPropPresenter presenter = CreateDoorPresenter(visual, out _);

            var playerGo = new GameObject("TestPlayer");
            playerGo.transform.SetParent(_holderGo.transform);
            playerGo.transform.position = new Vector3(2.0f, 0f, 0f); // exactly AS3's 200 px reach

            Assert.That(DoorPropPresenter.ActionReach, Is.EqualTo(2.0f).Within(0.0001f));
            Assert.That(presenter.CanInteract(playerGo), Is.True);

            // 2.4 units is inside the old 2.5 threshold and outside AS3's reach.
            playerGo.transform.position = new Vector3(2.4f, 0f, 0f);
            Assert.That(presenter.CanInteract(playerGo), Is.False);

            // AS3 always measures against the player, so a null user is not "in reach".
            Assert.That(presenter.CanInteract(null), Is.False);

            Object.DestroyImmediate(visual);
            Object.DestroyImmediate(texture);
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

        // ---------------------------------------------------------------------------------------
        // ApplyRestoredRoom. Loading a save IS a room swap, and it used to skip the swap entirely:
        // WorldSaveData.RestoreToMap calls LandMap.SwitchRoom, which only flips the LandMap's own
        // bookkeeping. The streaming manager kept the OLD room active (so the restored room never
        // ticked - RoomInstance.Update early-returns when inactive) and the visual controller kept
        // drawing the OLD room. The symptom was a load that reported the right room and put the
        // player at the right coordinates, over the wrong room's tiles.
        // ---------------------------------------------------------------------------------------

        private static RoomInstance CreateRoom(string id, Vector3Int pos)
        {
            var room = new RoomInstance
            {
                id = id,
                landPosition = pos,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };
            room.InitializeTiles();
            return room;
        }

        /// <summary>LandMap holding roomA at (0,0,0) and roomB at (1,0,0), current = roomA.</summary>
        private (LandMap map, RoomInstance roomA, RoomInstance roomB) CreateTwoRoomMap()
        {
            var map = new LandMap();
            RoomInstance roomA = CreateRoom("roomA", new Vector3Int(0, 0, 0));
            RoomInstance roomB = CreateRoom("roomB", new Vector3Int(1, 0, 0));
            map.AddRoom(roomA, new Vector3Int(0, 0, 0));
            map.AddRoom(roomB, new Vector3Int(1, 0, 0));
            map.SwitchRoom(new Vector3Int(0, 0, 0));
            return (map, roomA, roomB);
        }

        private RoomTransitionManager CreateManager(LandMap map, RoomStreamingManager streaming)
        {
            var managerGo = new GameObject("RestoreManager");
            managerGo.transform.SetParent(_holderGo.transform);
            var manager = managerGo.AddComponent<RoomTransitionManager>();
            manager.SetLandMap(map);
            if (streaming != null)
            {
                manager.SetStreamingManager(streaming);
            }
            return manager;
        }

        private RoomStreamingManager CreateStreamingManager()
        {
            var go = new GameObject("RestoreStreaming");
            go.transform.SetParent(_holderGo.transform);
            return go.AddComponent<RoomStreamingManager>();
        }

        [Test]
        public void ApplyRestoredRoom_LoadIntoDifferentRoom_ActivatesItInStreamingManager()
        {
            var (map, roomA, roomB) = CreateTwoRoomMap();
            var streaming = CreateStreamingManager();
            streaming.ActivateRoom(roomA);          // the player stood in A when the save was taken
            var manager = CreateManager(map, streaming);

            // What WorldSaveData.RestoreToMap does - and the ONLY step the load path used to take.
            map.SwitchRoom(new Vector3Int(1, 0, 0));

            manager.ApplyRestoredRoom(roomB);

            Assert.That(streaming.CurrentRoom, Is.SameAs(roomB),
                "The streaming manager must follow the load, or the restored room never ticks.");
            Assert.That(streaming.PreviousRoom, Is.SameAs(roomA),
                "The room being left must be recorded as previous, not the restored one.");
            Assert.That(roomB.isActive, Is.True, "The restored room must be active.");
            Assert.That(roomA.isActive, Is.False, "The room left behind must be deactivated.");
            Assert.That(map.currentRoom, Is.SameAs(roomB), "LandMap bookkeeping must follow.");
            Assert.That(map.GetCurrentPosition(), Is.EqualTo(new Vector3Int(1, 0, 0)));
        }

        [Test]
        public void ApplyRestoredRoom_WithoutPriorSwitchRoom_UpdatesLandMapBookkeeping()
        {
            var (map, roomA, roomB) = CreateTwoRoomMap();
            var streaming = CreateStreamingManager();
            streaming.ActivateRoom(roomA);
            var manager = CreateManager(map, streaming);

            // No SwitchRoom first: ApplyRestoredRoom must do all of the bookkeeping itself, since a
            // caller that hands it a room is entitled to a consistent LandMap afterwards.
            manager.ApplyRestoredRoom(roomB);

            Assert.That(map.currentRoom, Is.SameAs(roomB));
            Assert.That(map.previousRoom, Is.SameAs(roomA));
            Assert.That(map.GetCurrentPosition(), Is.EqualTo(new Vector3Int(1, 0, 0)));
            Assert.That(streaming.CurrentRoom, Is.SameAs(roomB));
        }

        [Test]
        public void ApplyRestoredRoom_SameRoom_KeepsItActive()
        {
            var (map, roomA, _) = CreateTwoRoomMap();
            var streaming = CreateStreamingManager();
            streaming.ActivateRoom(roomA);
            var manager = CreateManager(map, streaming);

            // Saving and loading without moving rooms. ActivateRoom refuses to reactivate, so this
            // is the path where only the re-render has anything to do - it must not deactivate.
            Assert.DoesNotThrow(() => manager.ApplyRestoredRoom(roomA));

            Assert.That(streaming.CurrentRoom, Is.SameAs(roomA));
            Assert.That(roomA.isActive, Is.True, "A same-room load must not leave the room inactive.");
            Assert.That(map.currentRoom, Is.SameAs(roomA));
        }

        [Test]
        public void ApplyRestoredRoom_NullRoom_DoesNotThrow()
        {
            var (map, _, _) = CreateTwoRoomMap();
            var manager = CreateManager(map, CreateStreamingManager());

            Assert.DoesNotThrow(() => manager.ApplyRestoredRoom(null));
        }

        [Test]
        public void ApplyRestoredRoom_NoVisualControllerWired_StillSwapsTheRoom()
        {
            // EditMode has no Camera.main and no render assets, so this is also the "degrades
            // gracefully" path: the warnings fire but the room swap must still complete.
            var (map, roomA, roomB) = CreateTwoRoomMap();
            var streaming = CreateStreamingManager();
            streaming.ActivateRoom(roomA);
            var manager = CreateManager(map, streaming);

            Assert.DoesNotThrow(() => manager.ApplyRestoredRoom(roomB));

            Assert.That(streaming.CurrentRoom, Is.SameAs(roomB));
            Assert.That(roomB.isActive, Is.True);
        }

        // ── Door open must force a relight (AS3 `Box.setDoor` → `Location.isRelight`) ─────────────
        //
        // `Box.as:688-692` is `if(param1) { loc.isRelight = true; loc.isRebuild = true; }` where
        // `param1` is the *new* open state — so **opening** force-relights and closing does not. The
        // room's step gate (`Location.as:3398`) then runs the full `lighting()` pass on the next frame
        // regardless of camera motion. Without the port wiring this, a door opened while the player
        // stood still left the mask stale until the player crossed a tile — the "open a door and the
        // doorway stays dark" symptom. See RoomInstance.RequestRelight / RoomBackdropRenderer.

        /// <summary>Builds a closed `door1` presenter over the fixture room, as the collision test does.</summary>
        private DoorPropPresenter CreateClosedDoorPresenter(out MapObjectVisualDefinition visualDef)
        {
            var doorObj = new ObjectInstance
            {
                objectId = "door1",
                objectType = "door",
                position = new Vector2(200f, 100f), // covers tiles X 4..5, Y 2..4
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };

            visualDef = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visualDef.pixelSize = new Vector2Int(40, 80);

            var go = new GameObject("RelightDoor");
            go.transform.SetParent(_holderGo.transform);
            var renderer = go.AddComponent<SpriteRenderer>();
            var presenter = go.AddComponent<DoorPropPresenter>();
            presenter.Initialize(_room, doorObj, visualDef, renderer, null);
            return presenter;
        }

        [Test]
        public void InitializingAClosedDoor_DoesNotRequestARelight()
        {
            // The control for the two tests below, and its own oracle point: `Box.initDoor` stamps
            // `opac` (Box.as:667) but never touches `isRelight`. A room full of closed doors must not
            // each buy a full light pass at activation.
            DoorPropPresenter presenter = CreateClosedDoorPresenter(out MapObjectVisualDefinition visualDef);

            Assert.That(_room.ConsumeRelightRequest(), Is.False,
                "Initialize() must not request a relight — AS3 initDoor does not set isRelight");

            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void OpeningADoor_RequestsARelight()
        {
            DoorPropPresenter presenter = CreateClosedDoorPresenter(out MapObjectVisualDefinition visualDef);

            // Drain whatever init left, so the assertion below can only be reading the open path.
            Assert.That(_room.ConsumeRelightRequest(), Is.False, "precondition: init requested nothing");

            presenter.SetOpen(true);

            Assert.That(_room.ConsumeRelightRequest(), Is.True,
                "opening a door is AS3's isRelight (Box.as:690) — it must force the full light pass, " +
                "which is what reveals the room behind it without the player having to move");

            Object.DestroyImmediate(visualDef);
        }

        [Test]
        public void ClosingADoor_DoesNotRequestARelight()
        {
            // The asymmetry is the oracle's: `Box.as:688` guards the force flags with `if(param1)`,
            // so a *close* relies on the camera-movement trigger to pick up the light it now blocks.
            DoorPropPresenter presenter = CreateClosedDoorPresenter(out MapObjectVisualDefinition visualDef);

            presenter.SetOpen(true);
            _room.ConsumeRelightRequest(); // drain the open request, so this reads the close path only

            presenter.SetOpen(false);

            Assert.That(_room.ConsumeRelightRequest(), Is.False,
                "closing must not request a relight — Box.as:688 sets isRelight only when opening");

            Object.DestroyImmediate(visualDef);
        }
    }
}

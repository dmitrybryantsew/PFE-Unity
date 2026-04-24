using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Map.Rendering
{
    [TestFixture]
    public class RoomObjectVisualManagerTests
    {
        GameObject _root;
        Transform _staticParent;
        Transform _physicalParent;
        readonly List<Object> _createdAssets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("RoomObjectVisualManagerTests");
            _staticParent = new GameObject("BackgroundObjects").transform;
            _staticParent.SetParent(_root.transform, false);
            _physicalParent = new GameObject("BackgroundPhysicalObjects").transform;
            _physicalParent.SetParent(_root.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdAssets.Count - 1; i >= 0; i--)
            {
                if (_createdAssets[i] != null)
                {
                    Object.DestroyImmediate(_createdAssets[i]);
                }
            }

            _createdAssets.Clear();

            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        [Test]
        public void RefreshAll_SpawnsStaticAndPhysicalPropsIntoSeparateSortingLayers()
        {
            RoomInstance room = CreateRoom();

            room.objects.Add(CreateObject("locker", MapObjectPhysicalCapability.Static, new Vector2(80f, 120f)));
            room.objects.Add(CreateObject("woodbox", MapObjectPhysicalCapability.DynamicTelekinetic, new Vector2(120f, 120f)));

            RoomObjectVisualManager manager = new RoomObjectVisualManager(room, _staticParent, _physicalParent);

            manager.RefreshAll();

            Assert.AreEqual(1, _staticParent.childCount);
            Assert.AreEqual(1, _physicalParent.childCount);

            SpriteRenderer staticRenderer = _staticParent.GetChild(0).GetComponent<SpriteRenderer>();
            SpriteRenderer physicalRenderer = _physicalParent.GetChild(0).GetComponent<SpriteRenderer>();

            Assert.NotNull(staticRenderer);
            Assert.NotNull(physicalRenderer);
            Assert.AreEqual(MapSortingLayers.BackgroundObject, staticRenderer.sortingLayerName);
            Assert.AreEqual(MapSortingLayers.BackgroundPhysicalObjects, physicalRenderer.sortingLayerName);
        }

        [Test]
        public void UpdateVisuals_MovesPresenterWhenDynamicObjectMoves()
        {
            RoomInstance room = CreateRoom();
            ObjectInstance obj = CreateObject("woodbox", MapObjectPhysicalCapability.DynamicTelekinetic, new Vector2(120f, 160f));
            room.objects.Add(obj);

            RoomObjectVisualManager manager = new RoomObjectVisualManager(room, _staticParent, _physicalParent);
            manager.RefreshAll();

            Transform presenter = _physicalParent.GetChild(0);
            Vector3 initialPosition = presenter.localPosition;

            obj.position = new Vector2(200f, 240f);
            manager.UpdateVisuals(1f / 60f);

            Assert.AreNotEqual(initialPosition, presenter.localPosition);
            Assert.AreEqual(WorldCoordinates.PixelToUnity(obj.position), presenter.localPosition);
        }

        [Test]
        public void UpdateVisuals_UsesStateFramesInsteadOfLoopingImportedFrames()
        {
            RoomInstance room = CreateRoom();
            MapObjectVisualDefinition visual = CreateVisual("locker_states", 3);
            ObjectInstance obj = CreateObject("locker", MapObjectPhysicalCapability.Static, new Vector2(80f, 120f), visual);
            room.objects.Add(obj);

            RoomObjectVisualManager manager = new RoomObjectVisualManager(room, _staticParent, _physicalParent);
            manager.RefreshAll();

            SpriteRenderer renderer = _staticParent.GetChild(0).GetComponent<SpriteRenderer>();
            Assert.AreSame(visual.frames[0], renderer.sprite);

            manager.UpdateVisuals(1f);
            Assert.AreSame(visual.frames[0], renderer.sprite);

            obj.runtimeState.isOpen = true;
            manager.UpdateVisuals(1f / 60f);
            Assert.AreSame(visual.frames[1], renderer.sprite);

            obj.runtimeState.lootState = 1;
            manager.UpdateVisuals(1f / 60f);
            Assert.AreSame(visual.frames[2], renderer.sprite);
        }

        [Test]
        public void RefreshAll_KeepsDestroyedObjectsVisibleOnTheirDestroyedFrame()
        {
            RoomInstance room = CreateRoom();
            MapObjectVisualDefinition visual = CreateVisual("door_states", 3);
            ObjectInstance obj = CreateObject("door1", MapObjectPhysicalCapability.Static, new Vector2(80f, 120f), visual);
            obj.runtimeState.isDestroyed = true;
            room.objects.Add(obj);

            RoomObjectVisualManager manager = new RoomObjectVisualManager(room, _staticParent, _physicalParent);
            manager.RefreshAll();

            Assert.AreEqual(1, _staticParent.childCount);

            SpriteRenderer renderer = _staticParent.GetChild(0).GetComponent<SpriteRenderer>();
            Assert.NotNull(renderer);
            Assert.IsTrue(renderer.enabled);
            Assert.AreSame(visual.frames[2], renderer.sprite);
        }

        RoomInstance CreateRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            room.InitializeTiles();
            return room;
        }

        ObjectInstance CreateObject(string objectId, MapObjectPhysicalCapability capability, Vector2 position)
        {
            return CreateObject(objectId, capability, position, CreateVisual(objectId));
        }

        ObjectInstance CreateObject(string objectId, MapObjectPhysicalCapability capability, Vector2 position, MapObjectVisualDefinition visual)
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = objectId;
            definition.physicalCapability = capability;
            definition.visual = visual;
            _createdAssets.Add(definition);

            return new ObjectInstance
            {
                objectId = objectId,
                objectType = "box",
                definitionId = objectId,
                definition = definition,
                position = position,
                runtimeState = new MapObjectRuntimeStateData()
            };
        }

        MapObjectVisualDefinition CreateVisual(string visualId, int frameCount = 1)
        {
            Sprite[] frames = new Sprite[Mathf.Max(1, frameCount)];
            for (int i = 0; i < frames.Length; i++)
            {
                Texture2D texture = new Texture2D(40, 40, TextureFormat.RGBA32, false);
                texture.SetPixel(0, 0, new Color(1f, Mathf.Clamp01(i * 0.25f), 1f, 1f));
                texture.Apply(false, false);
                _createdAssets.Add(texture);

                Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 40f, 40f), new Vector2(0.5f, 0f), 100f);
                _createdAssets.Add(sprite);
                frames[i] = sprite;
            }

            MapObjectVisualDefinition visual = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visual.visualId = visualId;
            visual.frames = frames;
            visual.pivot = new Vector2(0.5f, 0f);
            _createdAssets.Add(visual);
            return visual;
        }
    }
}

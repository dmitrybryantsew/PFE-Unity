using System;
using System.Collections.Generic;
using System.IO;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Spawns lightweight sprite presenters for room objects that have imported visuals.
    /// Keeps static props and physical props in separate sorting layers while staying
    /// decoupled from gameplay logic.
    /// </summary>
    public sealed class RoomObjectVisualManager
    {
        const string DefinitionResourcesRoot = "MapObjects/Definitions";
        const string VisualResourcesRoot = "MapObjects/Visuals";

        static Material s_presenterMaterial;

        readonly RoomInstance _room;
        readonly Transform _staticParent;
        readonly Transform _physicalParent;
        readonly Dictionary<ObjectInstance, Presenter> _presenters = new Dictionary<ObjectInstance, Presenter>();
        readonly Dictionary<string, MapObjectDefinition> _definitionCache = new Dictionary<string, MapObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, MapObjectVisualDefinition> _visualCache = new Dictionary<string, MapObjectVisualDefinition>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _missingDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _missingVisuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<ObjectInstance> _seenObjects = new HashSet<ObjectInstance>();
        readonly List<ObjectInstance> _staleObjects = new List<ObjectInstance>();

        sealed class Presenter
        {
            public readonly GameObject gameObject;
            public readonly Transform transform;
            public readonly SpriteRenderer renderer;

            public MapObjectVisualDefinition visual;
            public bool usesPhysicalLayer;
            public int frameIndex;

            public Presenter(GameObject presenterObject, SpriteRenderer spriteRenderer)
            {
                gameObject = presenterObject;
                transform = presenterObject.transform;
                renderer = spriteRenderer;
            }
        }

        public RoomObjectVisualManager(RoomInstance room, Transform staticParent, Transform physicalParent)
        {
            _room = room;
            _staticParent = staticParent;
            _physicalParent = physicalParent != null ? physicalParent : staticParent;
        }

        public void RefreshAll()
        {
            SyncPresenters(forceRefresh: true);
            UpdatePresenters(0f);
        }

        public void UpdateVisuals(float deltaTime)
        {
            SyncPresenters(forceRefresh: false);
            UpdatePresenters(deltaTime);
        }

        public void DestroyAll()
        {
            foreach (KeyValuePair<ObjectInstance, Presenter> pair in _presenters)
            {
                DestroyPresenter(pair.Value);
            }

            _presenters.Clear();
            _seenObjects.Clear();
            _staleObjects.Clear();
        }

        void SyncPresenters(bool forceRefresh)
        {
            if (_room?.objects == null)
            {
                DestroyAll();
                return;
            }

            _seenObjects.Clear();
            _staleObjects.Clear();

            foreach (KeyValuePair<ObjectInstance, Presenter> pair in _presenters)
            {
                _staleObjects.Add(pair.Key);
            }

            for (int i = 0; i < _room.objects.Count; i++)
            {
                ObjectInstance obj = _room.objects[i];
                if (obj == null || !_seenObjects.Add(obj))
                {
                    continue;
                }

                _staleObjects.Remove(obj);

                if (!TryResolveRenderableVisual(obj, out MapObjectVisualDefinition visual))
                {
                    RemovePresenter(obj);
                    continue;
                }

                bool shouldUsePhysicalLayer = obj.IsDynamicPhysicalProp();
                if (!_presenters.TryGetValue(obj, out Presenter presenter))
                {
                    presenter = CreatePresenter(obj, visual, shouldUsePhysicalLayer);
                    _presenters.Add(obj, presenter);
                    continue;
                }

                if (forceRefresh ||
                    presenter.visual != visual ||
                    presenter.usesPhysicalLayer != shouldUsePhysicalLayer)
                {
                    ApplyPresenterVisual(presenter, obj, visual, shouldUsePhysicalLayer, resetAnimation: true);
                }
            }

            for (int i = 0; i < _staleObjects.Count; i++)
            {
                RemovePresenter(_staleObjects[i]);
            }
        }

        void UpdatePresenters(float deltaTime)
        {
            foreach (KeyValuePair<ObjectInstance, Presenter> pair in _presenters)
            {
                UpdatePresenter(pair.Key, pair.Value, deltaTime);
            }
        }

        Presenter CreatePresenter(ObjectInstance obj, MapObjectVisualDefinition visual, bool usesPhysicalLayer)
        {
            string objectLabel = !string.IsNullOrWhiteSpace(obj.code) ? obj.code : obj.objectId;
            GameObject presenterObject = new GameObject($"Object_{objectLabel}");
            Transform parent = usesPhysicalLayer ? _physicalParent : _staticParent;
            presenterObject.transform.SetParent(parent, false);

            SpriteRenderer renderer = presenterObject.AddComponent<SpriteRenderer>();
            renderer.color = Color.white;
            Material presenterMaterial = GetPresenterMaterial();
            if (presenterMaterial != null)
            {
                renderer.sharedMaterial = presenterMaterial;
            }

            Presenter presenter = new Presenter(presenterObject, renderer);
            ApplyPresenterVisual(presenter, obj, visual, usesPhysicalLayer, resetAnimation: true);
            return presenter;
        }

        void ApplyPresenterVisual(
            Presenter presenter,
            ObjectInstance obj,
            MapObjectVisualDefinition visual,
            bool usesPhysicalLayer,
            bool resetAnimation)
        {
            if (presenter == null)
            {
                return;
            }

            presenter.visual = visual;
            presenter.usesPhysicalLayer = usesPhysicalLayer;

            if (resetAnimation)
            {
                presenter.frameIndex = ResolveFrameIndex(obj, visual);
            }

            Transform targetParent = usesPhysicalLayer ? _physicalParent : _staticParent;
            if (presenter.transform.parent != targetParent)
            {
                presenter.transform.SetParent(targetParent, false);
            }

            presenter.renderer.sortingLayerName = usesPhysicalLayer
                ? MapSortingLayers.BackgroundPhysicalObjects
                : MapSortingLayers.BackgroundObject;

            presenter.frameIndex = ResolveFrameIndex(obj, visual);
            Sprite sprite = ResolveFrame(visual, presenter.frameIndex);
            presenter.renderer.sprite = sprite;
            presenter.renderer.sortingOrder = ComputeSortingOrder(obj, visual);
            presenter.transform.localPosition = ResolveLocalPosition(obj, visual, sprite);
            presenter.renderer.enabled = sprite != null;
        }

        void UpdatePresenter(ObjectInstance obj, Presenter presenter, float deltaTime)
        {
            if (obj == null || presenter == null || presenter.visual == null)
            {
                return;
            }

            if (!obj.isActive)
            {
                presenter.renderer.enabled = false;
                return;
            }

            int nextFrame = ResolveFrameIndex(obj, presenter.visual);
            if (nextFrame != presenter.frameIndex)
            {
                presenter.frameIndex = nextFrame;
                presenter.renderer.sprite = ResolveFrame(presenter.visual, presenter.frameIndex);
            }

            presenter.renderer.sortingOrder = ComputeSortingOrder(obj, presenter.visual);
            presenter.transform.localPosition = ResolveLocalPosition(obj, presenter.visual, presenter.renderer.sprite);
            presenter.renderer.enabled = presenter.renderer.sprite != null;
        }

        void RemovePresenter(ObjectInstance obj)
        {
            if (obj == null || !_presenters.TryGetValue(obj, out Presenter presenter))
            {
                return;
            }

            _presenters.Remove(obj);
            DestroyPresenter(presenter);
        }

        static void DestroyPresenter(Presenter presenter)
        {
            if (presenter?.gameObject == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(presenter.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(presenter.gameObject);
            }
        }

        bool TryResolveRenderableVisual(ObjectInstance obj, out MapObjectVisualDefinition visual)
        {
            visual = null;
            if (obj == null || !obj.isActive)
            {
                return false;
            }

            MapObjectDefinition definition = ResolveDefinition(obj);
            if (definition?.visual != null && definition.visual.HasFrames)
            {
                visual = definition.visual;
                return true;
            }

            string visualId = definition != null
                ? definition.GetResolvedVisualId()
                : obj.GetResolvedDefinitionId();

            if (string.IsNullOrWhiteSpace(visualId))
            {
                return false;
            }

            visual = LoadVisual(visualId);
            if (visual == null || !visual.HasFrames)
            {
                return false;
            }

            if (definition != null && definition.visual == null)
            {
                definition.visual = visual;
            }

            return true;
        }

        MapObjectDefinition ResolveDefinition(ObjectInstance obj)
        {
            if (obj?.definition != null)
            {
                return obj.definition;
            }

            string definitionId = obj?.GetResolvedDefinitionId();
            if (string.IsNullOrWhiteSpace(definitionId) || _missingDefinitions.Contains(definitionId))
            {
                return null;
            }

            if (!_definitionCache.TryGetValue(definitionId, out MapObjectDefinition definition))
            {
                definition = Resources.Load<MapObjectDefinition>($"{DefinitionResourcesRoot}/{SanitizeResourceId(definitionId)}");
                if (definition != null)
                {
                    _definitionCache[definitionId] = definition;
                }
                else
                {
                    _missingDefinitions.Add(definitionId);
                }
            }

            if (definition != null && obj != null)
            {
                obj.definition = definition;
                if (string.IsNullOrWhiteSpace(obj.definitionId))
                {
                    obj.definitionId = definition.objectId;
                }
            }

            return definition;
        }

        MapObjectVisualDefinition LoadVisual(string visualId)
        {
            if (string.IsNullOrWhiteSpace(visualId) || _missingVisuals.Contains(visualId))
            {
                return null;
            }

            if (_visualCache.TryGetValue(visualId, out MapObjectVisualDefinition visual))
            {
                return visual;
            }

            visual = Resources.Load<MapObjectVisualDefinition>($"{VisualResourcesRoot}/{SanitizeResourceId(visualId)}");
            if (visual != null)
            {
                _visualCache[visualId] = visual;
                return visual;
            }

            _missingVisuals.Add(visualId);
            return null;
        }

        static Sprite ResolveFrame(MapObjectVisualDefinition visual, int frameIndex)
        {
            if (visual == null || !visual.HasFrames)
            {
                return null;
            }

            int clampedFrame = Mathf.Clamp(frameIndex, 0, visual.frames.Length - 1);
            return visual.frames[clampedFrame];
        }

        static int ResolveFrameIndex(ObjectInstance obj, MapObjectVisualDefinition visual)
        {
            if (visual == null || !visual.HasFrames)
            {
                return 0;
            }

            int lastFrameIndex = visual.frames.Length - 1;
            if (lastFrameIndex <= 0 || obj?.runtimeState == null)
            {
                return 0;
            }

            if (obj.runtimeState.isDestroyed || obj.runtimeState.isExploded)
            {
                return lastFrameIndex;
            }

            if (obj.runtimeState.lootState > 0)
            {
                return Mathf.Min(lastFrameIndex, visual.frames.Length >= 3 ? 2 : 1);
            }

            if (obj.runtimeState.isOpen)
            {
                return Mathf.Min(lastFrameIndex, 1);
            }

            return 0;
        }

        static int ComputeSortingOrder(ObjectInstance obj, MapObjectVisualDefinition visual)
        {
            int depthOrder = Mathf.FloorToInt(obj.position.y / Mathf.Max(1f, WorldConstants.TILE_SIZE));
            return visual.sortingOrder - depthOrder;
        }

        static Vector3 ResolveLocalPosition(ObjectInstance obj, MapObjectVisualDefinition visual, Sprite sprite)
        {
            Vector2 anchorPixels = obj.position;
            if (visual != null)
            {
                anchorPixels += visual.localOffset;
            }

            if (visual == null || sprite == null)
            {
                return WorldCoordinates.PixelToUnity(anchorPixels);
            }

            Vector2 spriteSize = sprite.rect.size;
            Vector2 desiredPivotPixels = new Vector2(
                spriteSize.x * visual.pivot.x,
                spriteSize.y * visual.pivot.y);
            Vector2 pivotCompensation = sprite.pivot - desiredPivotPixels;
            return WorldCoordinates.PixelToUnity(anchorPixels + pivotCompensation);
        }

        static Material GetPresenterMaterial()
        {
            if (s_presenterMaterial != null)
            {
                return s_presenterMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            if (shader == null)
            {
                return null;
            }

            s_presenterMaterial = new Material(shader)
            {
                name = "RoomObjectPresenter_Unlit"
            };
            s_presenterMaterial.hideFlags = HideFlags.HideAndDontSave;
            return s_presenterMaterial;
        }

        static string SanitizeResourceId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return string.Empty;
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = rawId;
            for (int i = 0; i < invalidChars.Length; i++)
            {
                sanitized = sanitized.Replace(invalidChars[i], '_');
            }

            return sanitized;
        }
    }
}

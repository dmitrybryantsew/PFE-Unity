using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PFE.Data.Definitions;
using PFE.Systems.Map.Actions;
using PFE.Systems.Map.Scripting;
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

        public AreaTriggerSystem TriggerSystem { get; set; }

        ObjectActionDispatcher _objectActions;

        /// <summary>
        /// The <c>allact</c> dispatcher, used for one question: <b>does this object carry a script we can
        /// actually run?</b> — the admission test for <see cref="DoorPropPresenter"/>.
        ///
        /// <para>Built lazily from <see cref="DoorPropPresenter.CreateSceneDispatcher"/> so there is a
        /// single composition site, and settable so a test can supply a double. Both of its dependencies
        /// are read at call time, which means this must not be touched before
        /// <c>CampaignManager.Initialize</c> has run or <c>map</c> will not be among the registered ids
        /// and the camp's wall map will get no interaction surface for the room's lifetime.</para>
        /// </summary>
        public ObjectActionDispatcher ObjectActions
        {
            get
            {
                if (_objectActions == null)
                {
                    _objectActions = DoorPropPresenter.CreateSceneDispatcher();
                }

                return _objectActions;
            }
            set => _objectActions = value;
        }

        /// <summary>
        /// Whether <paramref name="obj"/> should get a <see cref="DoorPropPresenter"/> — i.e. whether it
        /// is interactable at all.
        ///
        /// <para><b>Extracted as a pure function because this rule has now been wrong twice.</b> It first
        /// admitted only objectType <c>"door"</c>, family Door, or a visual id starting with <c>"door"</c>
        /// — which excluded the seven <c>allact='comein'</c> Z doors on all three counts, so pressing E on
        /// the camp's backroom door did nothing. Family Transition was added for them. Then the camp's wall
        /// map (<c>wmap</c>, <c>allact='map'</c>, <c>tip='box'</c>, family Furniture, visual
        /// <c>viswmap</c>) missed all three of the same tests, so the camp could not send the player
        /// anywhere — the same failure with the same cause. Both times nothing went red, because the
        /// question is a shape question and the answer was a list of shapes.</para>
        ///
        /// <para>Taking the shape values rather than an <see cref="ObjectInstance"/> is what makes it
        /// testable offline: an <c>ObjectInstance</c> needs a <c>MapObjectDefinition</c> to carry a
        /// definition-level <c>allact</c>, and that is a <c>ScriptableObject</c> (an ECall in the offline
        /// harness), while every input here is a string or a bool.</para>
        /// </summary>
        /// <param name="objectType">The placement's <c>type</c>, e.g. <c>"box"</c> or <c>"door"</c>.</param>
        /// <param name="definitionIsInteractiveFamily">
        /// Whether the definition's family is one of the families that own their interaction surface
        /// rather than carrying a script: <c>Door</c>, <c>Transition</c> and <c>Checkpoint</c>.
        ///
        /// <para><b>Checkpoint was added third, for the same reason the first two were.</b> AS3 does not
        /// give a checkpoint an <c>allact</c> — <c>CheckPoint</c> constructs its own <c>Interact</c> with
        /// <c>actFun = activate</c> and <c>userAction = "activate"</c> (<c>CheckPoint.as:86-90</c>), and
        /// <c>Location.as:2008</c> picks the class by <c>tip</c>. The port's <c>DoorPropPresenter</c> is
        /// "the port of AS3's Box interaction surface — and, today, the only IInteractable", so a
        /// checkpoint that is not admitted here cannot be interacted with <i>at all</i>: it falls to
        /// <c>ObjectColliderDebugPresenter</c>, which implements nothing. That was the checkpoint's exact
        /// state — placed, drawn, and inert.</para>
        /// </param>
        /// <param name="visualObjectId">The visual's <c>objectId</c>, or null.</param>
        /// <param name="allAct">The resolved <c>allact</c>, or null/empty when there is none.</param>
        /// <param name="dispatcherHandles">
        /// Asked only when every shape test has failed, and only for a non-empty <paramref name="allAct"/>.
        /// Passing a delegate rather than a dispatcher keeps the caller's dispatcher lazy — it is built
        /// on first use, not once per object.
        /// </param>
        public static bool UsesDoorPresenter(
            string objectType,
            bool definitionIsInteractiveFamily,
            string visualObjectId,
            string allAct,
            Func<string, bool> dispatcherHandles)
        {
            if (string.Equals(objectType, "door", StringComparison.OrdinalIgnoreCase)) return true;
            if (definitionIsInteractiveFamily) return true;

            if (!string.IsNullOrEmpty(visualObjectId) &&
                visualObjectId.StartsWith("door", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The last clause is "this object carries a script the dispatcher can actually run", which no
            // shape test can answer. Deliberately not "has an allact": an unported id would then get a
            // presenter, Dispatch would report it unhandled, and Interact would fall through to
            // ToggleOpen() — so pressing E on a `hack_robot` terminal or a `stand` would start toggling it
            // open. That is a behaviour change for every unported branch, and this pass is not that.
            //
            // No clause is needed for `prob`, even though AS3's gate is `allact || prob != null`
            // (Interact.as:1315). Every object that carries a prob is already admitted above: the prob
            // carriers are exactly four ids — `exit`, `doorout`, `doorprob`, `doorboss` — and all four are
            // in MapObjectDefinition.TransitionIds, so the family clause catches them. A `prob` clause here
            // would be unreachable code that reads like a safeguard, and the next person to move a prob id
            // out of that set would trust it.
            return !string.IsNullOrEmpty(allAct) && dispatcherHandles != null && dispatcherHandles(allAct);
        }

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

        public RoomObjectVisualManager(RoomInstance room, Transform staticParent, Transform physicalParent, AreaTriggerSystem triggerSystem = null)
        {
            _room = room;
            _staticParent = staticParent;
            _physicalParent = physicalParent != null ? physicalParent : staticParent;
            TriggerSystem = triggerSystem;
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
            presenter.transform.localScale = ResolveLocalScale(obj, visual);
            bool isArea = string.Equals(obj.objectType, "area", StringComparison.OrdinalIgnoreCase) ||
                          (visual != null && string.Equals(visual.visualId, "visArea", StringComparison.OrdinalIgnoreCase));
            presenter.renderer.enabled = !isArea && sprite != null && obj.isActive;

            if (isArea)
            {
                AreaTriggerPresenter triggerPresenter = presenter.gameObject.GetComponent<AreaTriggerPresenter>();
                if (triggerPresenter == null)
                {
                    triggerPresenter = presenter.gameObject.AddComponent<AreaTriggerPresenter>();
                }
                triggerPresenter.Initialize(_room, obj, TriggerSystem);
            }

            // DoorPropPresenter is the port of AS3's Box interaction surface — and, today, the only
            // IInteractable in the project, so anything it does not claim cannot be interacted with at
            // all. AS3 picks the class by `tip` (`Location.as:2008`: `tip == "box" || tip == "door"`
            // -> `new Box(...)`), so a `tip='box'` object with a script is a first-class citizen there.
            //
            // This test used to admit only objectType "door", family Door, or a visual id starting with
            // "door" — which excluded the seven Z doors on all three counts: they import as type "box",
            // the classifier files them under family Transition (`MapObjectDefinition.TransitionIds`),
            // and their visual is `visindoor2`, whose objectId is "indoor2". They therefore fell to
            // ObjectColliderDebugPresenter, which implements nothing, and pressing E on the camp's main
            // backroom door did nothing at all.
            //
            // Family Transition is exactly the set with no other route: eight definitions — `exit` plus
            // `inbasedoor`, `indoor1..4`, `inencldoor`, `instdoor`, all `tip='box'` and all `inter>0`,
            // seven of them the `allact='comein'` Z doors. The presenter then decides for itself whether
            // to stamp tiles and whether to open and close (see DoorPropPresenter.IsDoorBox), so the
            // Z doors get interaction without acquiring a solid closed state they never had in AS3.
            //
            // See UsesDoorPresenter for the full history — this rule has been wrong twice, and the second
            // time was the camp's wall map (`wmap`, `allact='map'`), which is what lets the camp send the
            // player to a land at all. The dispatcher clause is asked last and lazily, so a door never
            // pays for building a dispatcher it does not consult.
            bool usesDoorPresenter = UsesDoorPresenter(
                obj.objectType,
                obj.definition != null &&
                    (obj.definition.family == MapObjectFamily.Door ||
                     obj.definition.family == MapObjectFamily.Transition ||
                     obj.definition.family == MapObjectFamily.Checkpoint),
                visual != null ? visual.objectId : null,
                obj.GetAllAct(),
                allAct => ObjectActions.Handles(allAct));
            if (usesDoorPresenter)
            {
                DoorPropPresenter doorPresenter = presenter.gameObject.GetComponent<DoorPropPresenter>();
                if (doorPresenter == null)
                {
                    doorPresenter = presenter.gameObject.AddComponent<DoorPropPresenter>();
                }
                doorPresenter.Initialize(_room, obj, visual, presenter.renderer, TriggerSystem);

                // Hand over the dispatcher the admission test above was answered from, so the presenter
                // runs the same one. Without this each presenter would build its own from the same
                // inputs — correct today, but it is a second composition site that can drift.
                doorPresenter.ObjectActions = ObjectActions;
            }

            if (!isArea && !usesDoorPresenter)
            {
                ObjectColliderDebugPresenter colPresenter = presenter.gameObject.GetComponent<ObjectColliderDebugPresenter>();
                if (colPresenter == null)
                {
                    colPresenter = presenter.gameObject.AddComponent<ObjectColliderDebugPresenter>();
                }
                colPresenter.Initialize(_room, obj, visual);
            }
        }

        void UpdatePresenter(ObjectInstance obj, Presenter presenter, float deltaTime)
        {
            if (obj == null || presenter == null || presenter.visual == null)
            {
                return;
            }

            bool isArea = string.Equals(obj.objectType, "area", StringComparison.OrdinalIgnoreCase) ||
                          (presenter.visual != null && string.Equals(presenter.visual.visualId, "visArea", StringComparison.OrdinalIgnoreCase));

            if (!obj.isActive)
            {
                presenter.renderer.enabled = false;
                AreaTriggerPresenter triggerPresenter = presenter.gameObject.GetComponent<AreaTriggerPresenter>();
                if (triggerPresenter != null)
                {
                    triggerPresenter.UpdateActiveState();
                }
                ObjectColliderDebugPresenter colPresenter = presenter.gameObject.GetComponent<ObjectColliderDebugPresenter>();
                if (colPresenter != null)
                {
                    colPresenter.UpdateDebugVisual();
                }
                return;
            }

            DoorPropPresenter doorPresenter = presenter.gameObject.GetComponent<DoorPropPresenter>();
            if (doorPresenter == null)
            {
                int nextFrame = ResolveFrameIndex(obj, presenter.visual);
                if (nextFrame != presenter.frameIndex)
                {
                    presenter.frameIndex = nextFrame;
                    presenter.renderer.sprite = ResolveFrame(presenter.visual, presenter.frameIndex);
                }
            }
            else
            {
                presenter.frameIndex = doorPresenter.CurrentFrame;
            }

            presenter.renderer.sortingOrder = ComputeSortingOrder(obj, presenter.visual);
            presenter.transform.localPosition = ResolveLocalPosition(obj, presenter.visual, presenter.renderer.sprite);
            presenter.transform.localScale = ResolveLocalScale(obj, presenter.visual);
            presenter.renderer.enabled = !isArea && presenter.renderer.sprite != null;

            AreaTriggerPresenter activeTriggerPresenter = presenter.gameObject.GetComponent<AreaTriggerPresenter>();
            if (activeTriggerPresenter != null)
            {
                activeTriggerPresenter.UpdateActiveState();
            }

            ObjectColliderDebugPresenter activeColPresenter = presenter.gameObject.GetComponent<ObjectColliderDebugPresenter>();
            if (activeColPresenter != null)
            {
                activeColPresenter.UpdateDebugVisual();
            }
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
                // Imported prop sheets are in AS3 movieclip order: 0 = closed, 1 = open,
                // 2 = looted/empty, and (when present) the last frame = destroyed.
                // This branch used to return 2 for any sheet with 3+ frames, which collapsed
                // "open", "looted" and "destroyed" onto the same sprite and made the lootState
                // branch above dead code. Openable props are animated by DoorPropPresenter anyway,
                // so this is the container/box state mapping.
                return visual.frames.Length >= 3 ? 1 : Mathf.Min(lastFrameIndex, 1);
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

            bool isArea = string.Equals(obj?.objectType, "area", StringComparison.OrdinalIgnoreCase) ||
                          (visual != null && string.Equals(visual.visualId, "visArea", StringComparison.OrdinalIgnoreCase));
            if (isArea)
            {
                // Area triggers are anchored at their bottom-left in room pixel space.
                // Do not apply sprite pivot compensation.
                return WorldCoordinates.PixelToUnity(anchorPixels);
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

        static Vector3 ResolveLocalScale(ObjectInstance obj, MapObjectVisualDefinition visual)
        {
            if (obj == null)
            {
                return Vector3.one;
            }

            bool isArea = string.Equals(obj.objectType, "area", StringComparison.OrdinalIgnoreCase) ||
                          (visual != null && string.Equals(visual.visualId, "visArea", StringComparison.OrdinalIgnoreCase));

            if (isArea)
            {
                float w = TryParseFloatAttribute(obj, "w", 2f);
                float h = TryParseFloatAttribute(obj, "h", 2f);
                if (w <= 0f) w = 2f;
                if (h <= 0f) h = 2f;

                float baseWidth = visual != null && visual.pixelSize.x > 0 ? visual.pixelSize.x : 100f;
                float baseHeight = visual != null && visual.pixelSize.y > 0 ? visual.pixelSize.y : 100f;

                float scaleX = (w * WorldConstants.TILE_SIZE) / baseWidth;
                float scaleY = (h * WorldConstants.TILE_SIZE) / baseHeight;
                return new Vector3(scaleX, scaleY, 1f);
            }

            return Vector3.one;
        }

        static float TryParseFloatAttribute(ObjectInstance obj, string key, float defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            string raw = obj.GetAttribute(key, string.Empty);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : defaultValue;
        }

        /// <summary>
        /// Delegates to <see cref="SpritePresenterMaterial"/> so the prop presenters and the unit
        /// spawner cannot drift onto different shaders.
        /// </summary>
        static Material GetPresenterMaterial()
        {
            return SpritePresenterMaterial.Get();
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

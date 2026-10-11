using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Interaction;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Physics;
using PFE.Sim.Campaign;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Presenter component for in-room interactive door props (door1, door1a, door1b, septum, etc.).
    /// Direct port of AS3 Box.as door logic (initDoor, setDoor, attDoor).
    ///
    /// <para>Owns the door's <i>state</i>: solid tile stamping, the open/closed flag, horizontal
    /// ejection when closing while occupied, and the sprite frame for that state. It does <b>not</b>
    /// own input — <see cref="Interact"/> is called by the player's interaction path, and no other
    /// method here reads the keyboard. See the note above <see cref="Update"/> for why.</para>
    ///
    /// <para>Frame changes are <b>seeks, not animations</b>, because that is what AS3 does:
    /// <c>gotoAndStop("open" | "close" | "die")</c> for the three door states, and
    /// <c>gotoAndPlay("comein")</c> for the one Z-door clip (<c>Box.as:508-532</c>).</para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public class DoorPropPresenter : MonoBehaviour, IInteractable, IHoldInteractable
    {
        /// <summary>
        /// Seconds per frame of a played clip. The map-object sprites ship in
        /// <c>texture1.swf</c>, whose header declares <b>24 fps</b> — measured, not assumed. The
        /// old 0.075 was labelled "matching Flash AS3 30 FPS" and matched neither 30 fps (0.0333)
        /// nor the actual asset rate.
        /// </summary>
        public const float FrameDuration = 1f / 24f;

        /// <summary>Map-object source SWF frame rate, for callers that want it by name.</summary>
        public const float SourceFrameRate = 24f;

        private static readonly Color DoorTriggerFillColor = new Color(1.0f, 0.85f, 0.15f, 0.35f);
        private static readonly Color DoorTriggerWireColor = new Color(1.0f, 0.9f, 0.3f, 0.9f);
        private static readonly Color BarricadeFillColor = new Color(0.9f, 0.3f, 0.1f, 0.35f);
        private static readonly Color BarricadeWireColor = new Color(1.0f, 0.4f, 0.2f, 0.9f);
        private static readonly List<DoorPropPresenter> _activePresenters = new List<DoorPropPresenter>();
        private static Sprite _whiteDebugSprite;
        private static bool? _debugOverride;

        private RoomInstance _room;
        private ObjectInstance _objectInstance;
        private MapObjectVisualDefinition _visual;
        private SpriteRenderer _renderer;
        private AreaTriggerSystem _triggerSystem;
        private BoxCollider2D _triggerCollider;
        private GameObject _debugVisualGo;
        private SpriteRenderer _debugSpriteRenderer;
        private CheckpointAreaTrigger _checkpointArea;

        public static bool ShowDebugVisuals
        {
            get
            {
                if (_debugOverride.HasValue) return _debugOverride.Value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                return settings != null && settings.ShowDoorColliderDebug;
            }
            set
            {
                _debugOverride = value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                if (settings != null)
                {
                    settings.ShowDoorColliderDebug = value;
                }
                UpdateAllDebugVisuals();
            }
        }

        public static void SetDebugOverride(bool? overrideValue)
        {
            _debugOverride = overrideValue;
            UpdateAllDebugVisuals();
        }

        public static void UpdateAllDebugVisuals()
        {
            for (int i = _activePresenters.Count - 1; i >= 0; i--)
            {
                if (_activePresenters[i] != null)
                {
                    _activePresenters[i].UpdateDebugVisual();
                }
                else
                {
                    _activePresenters.RemoveAt(i);
                }
            }
        }

        private void OnEnable()
        {
            if (!_activePresenters.Contains(this))
            {
                _activePresenters.Add(this);
            }
            UpdateDebugVisual();
        }

        private void OnDisable()
        {
            _activePresenters.Remove(this);
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(false);
            }
        }

        /// <summary>
        /// How close the player must be to work this door — AS3 <c>World.w.actionDist</c>, shared
        /// with the player's own target search so the two cannot disagree.
        /// </summary>
        public const float ActionReach = WorldConstants.ACTION_REACH;

        private int _lastInteractFrame = -1;

        private ObjectActionDispatcher _objectActions;

        /// <summary>
        /// The <c>allact</c> dispatcher — the port of AS3's <c>Interact.act()</c> switch.
        ///
        /// <para>Built lazily rather than in <c>Awake</c> so <c>RoomTransitionManager.Instance</c> is
        /// resolved once the scene is up, and settable so a test can supply a double instead of a live
        /// manager. Resolving that manager at call time is what the rest of the codebase does
        /// (<c>DoorTrigger.cs:117</c>, <c>TilePhysicsController.cs:696</c>).</para>
        /// </summary>
        public ObjectActionDispatcher ObjectActions
        {
            get
            {
                if (_objectActions == null)
                {
                    _objectActions = CreateSceneDispatcher();
                }

                return _objectActions;
            }
            set => _objectActions = value;
        }

        /// <summary>
        /// The one place the scene's <c>allact</c> dispatcher is composed.
        ///
        /// <para>Public and static because two callers need it and they must not disagree:
        /// <see cref="DoorPropPresenter"/> to run a script, and <see cref="RoomObjectVisualManager"/> to
        /// decide whether an object <i>has</i> a runnable script at all. Two copies of this wiring is how
        /// one of them silently stops registering an action — and "registered but unreachable" is exactly
        /// the failure this seam exists to prevent (see the admission rule in
        /// <see cref="RoomObjectVisualManager"/>).</para>
        ///
        /// <para>Both dependencies are read at call time, so the caller must be past
        /// <c>CampaignManager.Initialize</c> for <c>map</c> to be registered. Both are tolerated as
        /// null: the wall map is then reported as unhandled rather than throwing out of an
        /// interaction.</para>
        ///
        /// <para><b>Three of the five capabilities come from two objects.</b>
        /// <see cref="PFE.Systems.Campaign.CampaignManager"/> is both the travel-map host (<c>map</c>) and
        /// the land-script host (<c>exit</c>), and
        /// <see cref="PFE.Systems.Map.Streaming.RoomTransitionManager"/> is both the layer-transition seam
        /// (<c>comein</c>) and the prob-room host (<c>prob</c> / <c>probreturn</c>). They are passed
        /// separately rather than as one bag so a test can double exactly the capability under test —
        /// <c>exit</c> and <c>map</c> have nothing to do with each other, and a test for the descent loop
        /// must not need a transition manager to exist.</para>
        /// </summary>
        public static ObjectActionDispatcher CreateSceneDispatcher()
        {
            // `map` needs the campaign state, which is not a MonoBehaviour and so cannot be found with
            // FindFirstObjectByType; CampaignManager.Current is the accessor for exactly this case.
            PFE.Systems.Campaign.CampaignManager campaign = PFE.Systems.Campaign.CampaignManager.Current;
            PFE.Systems.Map.Streaming.RoomTransitionManager transition =
                PFE.Systems.Map.Streaming.RoomTransitionManager.Instance;

            return ObjectActionDispatcher.CreateDefault(
                transition,
                travelMapHost: campaign,
                exitHost: campaign,
                probHost: transition);
        }

        private int _currentFrameIndex;
        private int _targetFrameIndex;
        private float _frameTimer;

        /// <summary>Index of the running <c>comein</c> clip, or -1 when it is not playing.</summary>
        private int _comeInFrameIndex = -1;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;
        public bool IsOpen => _objectInstance?.runtimeState?.isOpen ?? false;

        /// <summary>
        /// Whether this prop has been wrecked — AS3's <c>Box.setVisState("die")</c> state
        /// (<c>Box.as:810</c>). Reads the same two flags <c>RoomObjectVisualManager.ResolveFrameIndex</c>
        /// uses, so a door and a container agree on what "destroyed" means.
        /// </summary>
        public bool IsDestroyed =>
            _objectInstance?.runtimeState != null &&
            (_objectInstance.runtimeState.isDestroyed || _objectInstance.runtimeState.isExploded);

        /// <summary>
        /// Determines whether this door prop can be opened/closed by the player via interact key.
        /// Faithful to Flash AS3 (fe.loc.Box / fe.serv.Interact):
        /// - Only objects with inter.action == 1 (door1, door1a, door2, etc.) are interactive doors.
        /// - Destructible/solid barriers like septum (wooden wall / barricade), grate, hgrate, window1/2
        ///   have inter="0" (or no inter action) and cannot be opened via interaction.
        /// </summary>
        public bool IsInteractableDoor
        {
            get
            {
                if (_objectInstance == null) return false;

                // Explicit room instance attribute override (e.g. inter="0" in room XML)
                string interAttr = _objectInstance.GetAttribute("inter", null);
                if (!string.IsNullOrEmpty(interAttr))
                {
                    if (int.TryParse(interAttr, out int interVal))
                    {
                        return interVal > 0;
                    }
                }

                // Explicit blacklist for barrier/obstacle props that stamp wall collision but never open
                string id = _objectInstance.GetResolvedDefinitionId();
                if (string.Equals(id, "septum", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "grate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "hgrate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "window1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "window2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "platform1", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Check definition attribute
                if (_objectInstance.definition != null)
                {
                    string defInter = _objectInstance.definition.GetAttribute("inter", null);
                    if (!string.IsNullOrEmpty(defInter))
                    {
                        if (int.TryParse(defInter, out int defInterVal))
                        {
                            return defInterVal > 0;
                        }
                    }

                    // If definition is explicitly marked non-interactive or has empty/0 interactionMode
                    if (!_objectInstance.definition.isInteractive ||
                        string.IsNullOrEmpty(_objectInstance.definition.interactionMode) ||
                        _objectInstance.definition.interactionMode == "0")
                    {
                        return false;
                    }
                }

                // Known openable door families
                return id.StartsWith("door", StringComparison.OrdinalIgnoreCase) ||
                       id.StartsWith("hatch", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "stdoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "basedoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "encldoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "enclpole", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "alib1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "alib2", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Whether this object is a <b>door box</b> — AS3's <c>door=</c> rule
        /// (<c>Box.as:290-297</c>), resolved by <see cref="ObjectInstance.IsDoorBox"/>.
        ///
        /// <para><b>This is not the same question as <see cref="IsInteractableDoor"/>, and conflating
        /// them is what walled off the Z doors.</b> <see cref="IsInteractableDoor"/> answers "may the
        /// player work this?" (it is true for the <c>inter='8'</c> Z doors); this answers "does it have
        /// a solid closed state that <c>initDoor</c>/<c>setDoor</c> manage?" — which for the Z doors is
        /// <b>no</b>: they author no <c>door=</c>, so AS3 never stamps their tiles and never gives them
        /// an open/close state. Only the tile stamping and the open/close toggle hang off this; the
        /// trigger collider and the <c>allact</c> dispatch hang off the other.</para>
        /// </summary>
        public bool IsDoorBox => _objectInstance != null && _objectInstance.IsDoorBox();

        public string ActionText => IsInteractableDoor ? (IsOpen ? "Close" : "Open") : string.Empty;

        /// <summary>
        /// Frames the action key must be held before this door opens or closes — AS3
        /// <c>Interact.t_action</c>, authored as <c>time</c> on the object.
        ///
        /// <para>Read from the imported object rather than hard-coded, so one presenter serves both
        /// kinds of door. The seven Z doors author <c>time='10'</c> and are held for a third of a
        /// second; the metal doors and terminals author 15/20/30; and plain <c>door1</c>/<c>door2</c>/
        /// <c>hatch</c> author nothing at all, so this returns 0 and the caller keeps acting on the
        /// press — which is why adding the hold changed nothing for them.</para>
        /// </summary>
        public int HoldFrames => _objectInstance != null ? _objectInstance.GetHoldFrames() : 0;

        /// <summary>
        /// This door's world position — what the hold's range guard measures against, and the
        /// coordinates the effect runs with.
        ///
        /// <para>Same value the presenter already hands to <see cref="ObjectActionContext"/>, so a
        /// held <c>comein</c> arrives at the same cell it would have reached before the hold
        /// existed.</para>
        /// </summary>
        public Vector3 WorldPosition => transform.position;
        public int CurrentFrame => _currentFrameIndex;
        public int TargetFrame => _targetFrameIndex;

        public void Initialize(
            RoomInstance room,
            ObjectInstance obj,
            MapObjectVisualDefinition visual,
            SpriteRenderer renderer,
            AreaTriggerSystem triggerSystem)
        {
            _room = room;
            _objectInstance = obj;
            _visual = visual;
            _renderer = renderer;
            _triggerSystem = triggerSystem;

            _triggerCollider = GetComponent<BoxCollider2D>();
            if (_triggerCollider != null)
            {
                _triggerCollider.isTrigger = true;
                ConfigureCollider();
            }

            // In AS3 (Box.as:653), initDoor stamps solid collision if closed — but initDoor is only
            // reached when the definition authors `door=` (Box.as:290-297). A `comein` Z door does not,
            // and it stands on open ground (RoomsCamp.as places indoor2 at 16,15 and 16,7; both are `_`
            // in the imported tile map). Stamping Wall there would seal the doorway the player is
            // supposed to walk into, so the stamping follows IsDoorBox, not IsInteractableDoor.
            if (IsDoorBox)
            {
                ApplyTileCollision(IsOpen);
            }

            EnsureCheckpointArea();

            UpdateVisualFrame();
        }

        private void GetDoorBounds(out Vector2 size, out Vector2 offset)
        {
            if (_renderer != null && _renderer.sprite != null)
            {
                size = (Vector2)_renderer.sprite.bounds.size;
                offset = (Vector2)_renderer.sprite.bounds.center;
                return;
            }

            float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
            float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.8f;
            size = new Vector2(w, h);
            offset = Vector2.zero;
        }

        private void ConfigureCollider()
        {
            if (_triggerCollider == null) return;

            // If the player cannot work this object at all (a septum barricade, or a prop that only
            // stamps collision), disable the trigger collider so it never captures cursor raycasts or
            // proximity interaction scans.
            //
            // This test used to be `!IsInteractableDoor` alone, which silently disabled the collider on a
            // CHECKPOINT — a checkpoint authors no `inter` attribute and its `interactionMode` is empty,
            // so IsInteractableDoor is false for it. With the collider disabled,
            // Physics2D.OverlapCircleAll at the cursor could never hit it, so PlayerController's
            // FindCursorTarget returned null and the checkpoint was untargetable no matter what
            // CanInteract answered. Two gates had to pass and both were closed; fixing only CanInteract
            // would have left E still doing nothing.
            if (!AdmitsPlayerInteraction(IsInteractableDoor, IsCheckpointObject))
            {
                _triggerCollider.enabled = false;
                return;
            }

            _triggerCollider.enabled = true;
            GetDoorBounds(out Vector2 size, out Vector2 offset);
            _triggerCollider.size = size;
            _triggerCollider.offset = offset;
        }

        /// <summary>
        /// Builds the checkpoint's walk-into area — AS3 <c>CheckPoint</c>'s own <c>Area</c> with
        /// <c>over = areaActivate</c> (<c>CheckPoint.as:90-92</c>).
        ///
        /// <para><b>A child object with its own collider, deliberately not this component's.</b> The
        /// presenter's own <see cref="BoxCollider2D"/> is the port's <i>cursor-targeting</i> affordance
        /// and is sized off the sprite; AS3's area is the <c>size</c>x<code>wid</code> tile box
        /// (<c>:48-53</c>). Sizing them separately means adding the walk-into path cannot move the
        /// region the cursor can select. AS3 does use one box for both — <c>onCursor</c> reads the same
        /// <c>X1/X2/Y1/Y2</c> at <c>:295</c> — so the sprite-sized target box remains a divergence,
        /// recorded here rather than silently widened into the new code.</para>
        ///
        /// <para><b>A main checkpoint gets no area.</b> AS3 sets <c>this.area = null</c> for one
        /// (<c>:127</c>) — it is teleported from, never walked into. The <c>main</c> normalisation that
        /// makes its activation refuse would already stop the walk-in from doing anything, but the
        /// oracle also declines to <i>create</i> the area, so the trigger is not built at all.</para>
        /// </summary>
        private void EnsureCheckpointArea()
        {
            if (!IsCheckpointObject || _objectInstance == null) return;
            if (_checkpointArea != null) return;

            if (!string.IsNullOrEmpty(_objectInstance.GetAttribute("main", string.Empty))) return;

            // `size` is the width and `wid` is the height (CheckPoint.as:48-49) — the oracle's names,
            // which read backwards. GetDoorTileDimensions maps them to (widthTiles, heightTiles) in
            // that order for every box, checkpoint included.
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);

            CheckpointRules.CheckpointAreaBox box = CheckpointRules.ResolveAreaBox(
                widthTiles, heightTiles, WorldConstants.TILE_SIZE / 100f);

            if (box.halfWidth <= 0f || box.height <= 0f) return;

            var areaGo = new GameObject("__CheckpointArea");
            areaGo.transform.SetParent(transform, false);

            // AS3 `Y1 = Y - scY`, `Y2 = Y`: the box is bottom-anchored on the checkpoint, so its centre
            // is half its height up. X is already centred — `X1 = X - scX/2`.
            areaGo.transform.localPosition = new Vector3(0f, box.centreYOffset, 0f);

            var collider = areaGo.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
            collider.size = new Vector2(box.halfWidth * 2f, box.height);

            _checkpointArea = areaGo.AddComponent<CheckpointAreaTrigger>();
            _checkpointArea.Initialize(() => ActivateCheckpoint(CheckpointEntry.Area));
        }

        public void SetOpen(bool open)
        {
            if (_objectInstance == null) return;
            if (!IsInteractableDoor && open) return;

            _objectInstance.runtimeState.isOpen = open;

            if (!open)
            {
                EjectOverlappingUnits();
            }

            ApplyTileCollision(open);

            // AS3 `Box.setDoor()` force-relights the room when the door is *opened*, and only then:
            // `if(param1) { loc.isRelight = true; loc.isRebuild = true; }` (Box.as:688-692, where
            // param1 is the new open state). The room's per-frame gate (Location.as:3398) then runs
            // the full light pass on the next frame regardless of camera motion, so opening a door
            // immediately reveals what is behind it — including the hole a closed grate's low `opac`
            // already lets through.
            //
            // Closing deliberately does *not* request a relight, matching the oracle: light that
            // should now be blocked is picked up by the next camera/player movement instead. Init
            // does not either — AS3 `initDoor` stamps `opac` (Box.as:667) without touching the flag,
            // and Initialize() calls ApplyTileCollision directly rather than through here.
            if (open && _room != null)
            {
                _room.RequestRelight();
            }

            UpdateVisualFrame();

            if (_triggerSystem != null && _room != null)
            {
                _triggerSystem.OnObjectInteracted(_room, _objectInstance);
            }
        }

        public void ToggleOpen()
        {
            // AS3 only ever calls setDoor() on a box whose definition authors `door=` (Box.as:290-297).
            // Without this guard, `Interact`'s fallback would "open" a Z door — playing an open/close
            // frame animation and reporting success for an object that has no open state at all.
            if (!IsDoorBox || !IsInteractableDoor) return;
            SetOpen(!IsOpen);
        }

        public void GetDoorTileDimensions(out int widthTiles, out int heightTiles)
        {
            widthTiles = 1;
            heightTiles = 2;

            if (_objectInstance?.definition != null)
            {
                if (_objectInstance.definition.size > 0)
                    widthTiles = _objectInstance.definition.size;
                if (_objectInstance.definition.width > 0)
                    heightTiles = _objectInstance.definition.width;
                return;
            }

            if (_objectInstance != null)
            {
                string rawSize = _objectInstance.GetAttribute("size", string.Empty);
                if (int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0)
                    widthTiles = s;

                string rawWid = _objectInstance.GetAttribute("wid", string.Empty);
                if (int.TryParse(rawWid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0)
                    heightTiles = w;

                if (!string.IsNullOrEmpty(rawSize) || !string.IsNullOrEmpty(rawWid))
                    return;
            }

            if (_visual != null && _visual.pixelSize.x > 0 && _visual.pixelSize.y > 0)
            {
                widthTiles = Mathf.Max(1, Mathf.RoundToInt((float)_visual.pixelSize.x / WorldConstants.TILE_SIZE));
                heightTiles = Mathf.Max(1, Mathf.RoundToInt((float)_visual.pixelSize.y / WorldConstants.TILE_SIZE));
            }
        }

        public void GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax)
        {
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);

            float widthPx = widthTiles * WorldConstants.TILE_SIZE;
            float leftPx = (_objectInstance != null ? _objectInstance.position.x : 0f) - widthPx * 0.5f;
            float bottomPx = _objectInstance != null ? _objectInstance.position.y : 0f;

            txMin = Mathf.FloorToInt(leftPx / WorldConstants.TILE_SIZE + 0.01f);
            txMax = txMin + widthTiles - 1;
            tyMin = Mathf.FloorToInt(bottomPx / WorldConstants.TILE_SIZE);
            tyMax = tyMin + heightTiles - 1;
        }

        public void ApplyTileCollision(bool isOpen)
        {
            if (_room?.tiles == null || _objectInstance == null) return;

            GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax);

            TilePhysicsType targetType = isOpen ? TilePhysicsType.Air : TilePhysicsType.Wall;

            // AS3 Box.setDoor() writes `opac` alongside `phis` on every tile the door covers
            // (Box.as:684-685): 0 while open, the door's own `@opac` while closed. The default is 1
            // (Box.as:72), so an unauthored door blocks light completely — only a door that authors
            // `opac` opens a hole in its own shadow. The data does that on 11 doors, 0.1 (grates) to
            // 0.8 (wooden doors) — AllData.as:4859-5039.
            //
            // Without this term the port closed every door to a full Wall, i.e. opacity 1, so a closed
            // grate or window was solid black where AS3 costs a shadow ray only 0.1-0.2 and lets you
            // see through. `phis` stays 1 for both cases, so this changes light only — enemy LOS and
            // movement still treat a closed door as solid, which is what Box.as:684 does.
            float doorOcclusion = isOpen ? 0f : ResolveClosedDoorOcclusion();

            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = _room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null)
                    {
                        tile.physicsType = targetType;
                        tile.doorOcclusion = doorOcclusion;
                    }
                }
            }
        }

        /// <summary>
        /// AS3 <c>Box.door_opac</c> for this door — its <c>@opac</c>, defaulting to 1 exactly as
        /// <c>Box.as:72</c> declares it. A door with no definition keeps that default, which is also
        /// the behaviour this port had before the term existed, so a missing definition can only fail
        /// back to the old result rather than to "see through the door".
        /// </summary>
        private float ResolveClosedDoorOcclusion()
        {
            return _objectInstance?.definition != null
                ? _objectInstance.definition.GetDoorOcclusion()
                : 1f;
        }

        /// <summary>
        /// Puts the renderer on the frame for the door's current state.
        ///
        /// <para><b>AS3 seeks here; it does not animate.</b> <c>Box.setVisState</c> runs
        /// <c>vis.gotoAndStop("open" | "close" | "die")</c> for every state, and
        /// <c>gotoAndPlay</c> only for <c>comein</c> (<c>Box.as:508-532</c>). So opening or closing
        /// a door is a single frame change.</para>
        ///
        /// <para><b>What was wrong.</b> The open target was resolved as index 2 of any sheet with
        /// 3+ frames, and the renderer then walked 0 → 1 → 2 one frame at a time. Index 2 of a
        /// 3-frame door sheet is the <c>die</c> frame, not <c>open</c> — so opening a door visibly
        /// animated from closed, through open, to <i>destroyed</i>, and closing walked back the
        /// same way. The labels were measured from the source SWF; see
        /// <see cref="MapObjectVisualDefinition"/>'s label accessors for the evidence.</para>
        /// </summary>
        public void UpdateVisualFrame()
        {
            if (_visual == null || !_visual.HasFrames) return;

            _currentFrameIndex = ResolveStateFrameIndex();
            _targetFrameIndex = _currentFrameIndex;
            _frameTimer = 0f;
            _comeInFrameIndex = -1;
            UpdateRendererSprite();
        }

        /// <summary>
        /// frames[] index for the door's current state, resolved from the sheet's Flash labels.
        /// Falls back to the closed frame when the sheet does not implement the state — which is
        /// what AS3's swallowed <c>gotoAndStop</c> exception amounts to (<c>Box.as:529-531</c>).
        /// </summary>
        private int ResolveStateFrameIndex()
        {
            if (_visual == null || !_visual.HasFrames) return 0;

            if (IsDestroyed && _visual.GetFrame(_visual.DestroyedStateFrame) != null)
            {
                return _visual.DestroyedStateFrame;
            }

            if (IsOpen && _visual.GetFrame(_visual.OpenStateFrame) != null)
            {
                return _visual.OpenStateFrame;
            }

            return _visual.ClosedStateFrame;
        }

        /// <summary>
        /// Starts the <c>comein</c> clip — the one label AS3 <i>plays</i> rather than seeks to,
        /// fired from <c>Interact.beginAct()</c> (<c>Interact.as:1670-1676</c>).
        ///
        /// <para><b>Known timing gap.</b> AS3 fires <c>beginAct()</c> when the hold is <i>armed</i>
        /// (<c>UnitPlayer.as:1983</c>); this is called when the action has <i>completed</i>, because
        /// the presenter is not told about the arm. The clip still plays from the right frame, just
        /// after the hold instead of during it.</para>
        /// </summary>
        public void BeginComeIn()
        {
            if (_visual == null || !_visual.HasFrames) return;

            int start = _visual.ComeInFrame;
            if (start < 0) return;

            _comeInFrameIndex = start;
            _currentFrameIndex = start;
            _targetFrameIndex = start;
            _frameTimer = 0f;
            UpdateRendererSprite();
        }

        /// <summary>True while the <c>comein</c> clip is running.</summary>
        public bool IsPlayingComeIn => _comeInFrameIndex >= 0;

        /// <summary>
        /// Advances the <c>comein</c> clip. Open/close/die do not animate at all, so this is a
        /// no-op unless <see cref="BeginComeIn"/> started the clip.
        /// </summary>
        public void TickAnimation(float deltaTime)
        {
            if (!IsPlayingComeIn || _visual == null || !_visual.HasFrames) return;

            int lastFrame = _visual.frames.Length - 1;

            _frameTimer += deltaTime;
            while (_frameTimer >= FrameDuration && _comeInFrameIndex < lastFrame)
            {
                _frameTimer -= FrameDuration;
                _comeInFrameIndex++;
                _currentFrameIndex = _comeInFrameIndex;
                UpdateRendererSprite();
            }

            if (_comeInFrameIndex >= lastFrame)
            {
                _comeInFrameIndex = -1; // clip finished
            }
        }

        private void UpdateRendererSprite()
        {
            if (_renderer == null || _visual == null || !_visual.HasFrames) return;
            int clamped = Mathf.Clamp(_currentFrameIndex, 0, _visual.frames.Length - 1);
            _renderer.sprite = _visual.frames[clamped];
        }

        /// <summary>
        /// Detects any units overlapping the door volume and displaces them horizontally to the
        /// left or right side of the door. Matches original Flash AS3 behavior where closing a door
        /// while inside pushes the character to the side on the ground, never upward into the air.
        /// </summary>
        public void EjectOverlappingUnits()
        {
            var candidates = new HashSet<GameObject>();

            // 1. Explicitly check Player
            var player = GameObject.FindWithTag("Player");
            if (player != null)
            {
                candidates.Add(player);
            }
            else
            {
                var locomotion = FindFirstObjectByType<PFE.Entities.Player.PlayerLocomotionController>();
                if (locomotion != null)
                {
                    candidates.Add(locomotion.gameObject);
                }
            }

            // 2. Physics overlap query for other units
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);
            float widthUnits = widthTiles * WorldConstants.TILE_SIZE * 0.01f;
            float heightUnits = heightTiles * WorldConstants.TILE_SIZE * 0.01f;

            Vector2 localOffsetUnits = _visual != null ? (Vector2)_visual.localOffset * 0.01f : Vector2.zero;
            float doorCenterX = transform.position.x - localOffsetUnits.x;
            float doorBottomY = transform.position.y - localOffsetUnits.y;
            Vector2 boxCenter = new Vector2(doorCenterX, doorBottomY + heightUnits * 0.5f);
            Vector2 boxSize = new Vector2(widthUnits + 0.4f, heightUnits);

            Collider2D[] overlaps = Physics2D.OverlapBoxAll(boxCenter, boxSize, 0f);
            if (overlaps != null)
            {
                for (int i = 0; i < overlaps.Length; i++)
                {
                    var col = overlaps[i];
                    if (col == null || col.isTrigger || col.gameObject == gameObject) continue;
                    if (col.CompareTag("Player") ||
                        col.GetComponent<TilePhysicsController>() != null ||
                        col.GetComponent<IMovementMotor>() != null ||
                        col.GetComponent<UnitController>() != null)
                    {
                        candidates.Add(col.gameObject);
                    }
                }
            }

            foreach (var unit in candidates)
            {
                if (unit != null)
                {
                    EjectUnitIfOverlapping(unit);
                }
            }
        }

        /// <summary>
        /// Eject an individual unit if it overlaps the door's solid volume.
        /// Pushes horizontally to the nearest open side, keeping Y unchanged.
        /// </summary>
        public bool EjectUnitIfOverlapping(GameObject unit)
        {
            if (unit == null) return false;

            GetDoorTileDimensions(out int widthTiles, out int heightTiles);
            float widthUnits = widthTiles * WorldConstants.TILE_SIZE * 0.01f;
            float heightUnits = heightTiles * WorldConstants.TILE_SIZE * 0.01f;

            Vector2 localOffsetUnits = _visual != null ? (Vector2)_visual.localOffset * 0.01f : Vector2.zero;
            float doorCenterX = transform.position.x - localOffsetUnits.x;
            float doorBottomY = transform.position.y - localOffsetUnits.y;
            float doorLeft = doorCenterX - widthUnits * 0.5f;
            float doorRight = doorCenterX + widthUnits * 0.5f;
            float doorTop = doorBottomY + heightUnits;

            Vector3 unitPos = unit.transform.position;
            float halfWidth = 0.15f;
            float unitBottom = unitPos.y;
            float unitTop = unitPos.y + 0.5f;

            var tpc = unit.GetComponent<TilePhysicsController>();
            if (tpc != null)
            {
                halfWidth = tpc.CollisionWidth * 0.5f * 0.01f;
                unitBottom = unitPos.y;
                unitTop = unitPos.y + tpc.CollisionHeight * 0.01f;
            }
            else
            {
                var col = unit.GetComponent<Collider2D>();
                if (col != null && !col.isTrigger)
                {
                    halfWidth = Mathf.Max(0.12f, col.bounds.extents.x);
                    unitBottom = col.bounds.min.y;
                    unitTop = col.bounds.max.y;
                }
            }

            float unitLeft = unitPos.x - halfWidth;
            float unitRight = unitPos.x + halfWidth;

            // Check AABB overlap between unit and solid door footprint
            bool overlapX = unitRight > doorLeft + 0.005f && unitLeft < doorRight - 0.005f;
            bool overlapY = unitTop > doorBottomY && unitBottom < doorTop;

            if (!overlapX || !overlapY)
            {
                return false;
            }

            // Margin outside the door's solid bounds to ensure no immediate re-collision with wall
            const float clearMargin = 0.02f;
            float pushLeftX = doorLeft - halfWidth - clearMargin;
            float pushRightX = doorRight + halfWidth + clearMargin;

            bool prefersLeft = unitPos.x < doorCenterX;
            bool leftBlocked = IsPositionBlockedByWall(pushLeftX, unitPos.y);
            bool rightBlocked = IsPositionBlockedByWall(pushRightX, unitPos.y);

            float targetX;
            if (prefersLeft)
            {
                if (!leftBlocked)
                    targetX = pushLeftX;
                else if (!rightBlocked)
                    targetX = pushRightX;
                else
                    targetX = pushLeftX;
            }
            else
            {
                if (!rightBlocked)
                    targetX = pushRightX;
                else if (!leftBlocked)
                    targetX = pushLeftX;
                else
                    targetX = pushRightX;
            }

            // CRITICAL: Keep Y unchanged so the unit is pushed strictly horizontally to the side,
            // never upward into the ceiling.
            Vector3 newPos = new Vector3(targetX, unitPos.y, unitPos.z);

            if (tpc != null)
            {
                tpc.SetUnityPosition(newPos);
                tpc.TeleportTo(tpc.PixelPosition.x, tpc.PixelPosition.y);
            }
            else
            {
                var movementMotor = unit.GetComponent<IMovementMotor>();
                if (movementMotor != null)
                {
                    movementMotor.SetUnityPosition(newPos);
                    movementMotor.SetDesiredHorizontalSpeed(0f);
                }
                else
                {
                    var rb = unit.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        rb.position = new Vector2(targetX, unitPos.y);
                        rb.linearVelocity = Vector2.zero;
                    }
                    unit.transform.position = newPos;
                }
            }

            return true;
        }

        private bool IsPositionBlockedByWall(float worldX, float worldY)
        {
            if (_room?.tiles == null) return false;

            float roomOriginPixelX = WorldCoordinates.RoomOriginPixelX(_room.landPosition.x, _room.borderOffset);
            float roomOriginPixelY = WorldCoordinates.RoomOriginPixelY(_room.landPosition.y, _room.borderOffset);

            float localPixelX = (worldX * 100f) - roomOriginPixelX;
            float localPixelY = (worldY * 100f) - roomOriginPixelY;

            int tx = Mathf.FloorToInt(localPixelX / WorldConstants.TILE_SIZE);
            int ty = Mathf.FloorToInt(localPixelY / WorldConstants.TILE_SIZE);

            TileData tile = _room.GetTileAtCoord(new Vector2Int(tx, ty));
            return tile != null && tile.physicsType == TilePhysicsType.Wall;
        }

        /// <summary>
        /// Whether the player may work this door from where it stands.
        ///
        /// <para>AS3 gates the press on <c>loc.celDist &lt;= World.w.actionDist</c>
        /// (<c>UnitPlayer.as:1931</c>) with <c>actionDist = 40000</c> (<c>World.as:264</c>), a
        /// <i>squared</i> distance — so the reach is 200 source pixels. This project keeps source
        /// pixels as world units and scales art at 100 px per unit, so that is 2.0 world units. The
        /// previous 2.5 was 25% too generous and let the player work doors from outside the AS3
        /// zone.</para>
        ///
        /// <para><c>user == null</c> answers false: AS3 always measures against the player, and the
        /// old proximity-flag fallback here existed only to serve the input poll that this component
        /// no longer owns.</para>
        /// </summary>
        public bool CanInteract(GameObject user)
        {
            if (!AdmitsPlayerInteraction(IsInteractableDoor, IsCheckpointObject)) return false;

            if (user == null) return false;

            float dist = Vector2.Distance(transform.position, user.transform.position);
            return dist <= ActionReach;
        }

        /// <summary>
        /// Whether the player may <b>target</b> this object at all — the guard <see cref="CanInteract"/>
        /// applies before it measures distance.
        ///
        /// <para><b>Why the door rule alone is not enough.</b> <see cref="IsInteractableDoor"/> answers
        /// "may the player work this <i>door</i>?" — it is derived from the <c>inter</c> attribute and
        /// <c>interactionMode</c>, and a checkpoint authors neither (the imported <c>checkpoint</c>
        /// definition has an empty <c>interactionMode</c>), so it answers <b>false</b> for one. AS3 never
        /// asks that question of a checkpoint: <c>CheckPoint</c>'s constructor builds its own
        /// <c>Interact</c> with <c>actFun = activate</c> (<c>CheckPoint.as:87</c>) and
        /// <c>active = true; action = 100</c> (<c>:89-90</c>), so it is workable by construction. Gating it
        /// on a door attribute is what made pressing E on a checkpoint do nothing: <see cref="Interact"/>
        /// handled the checkpoint correctly, but this guard refused the target first, so
        /// <see cref="Interact"/> was never reached.</para>
        ///
        /// <para>The two terms are exactly the set <see cref="Interact"/> can act on and nothing more.
        /// Widening this to "has any script" would re-open the terminal/bench regression documented on
        /// <see cref="RoomObjectVisualManager.UsesDoorPresenter"/>.</para>
        /// </summary>
        public static bool AdmitsPlayerInteraction(bool isInteractableDoor, bool isCheckpointObject)
        {
            return isInteractableDoor || isCheckpointObject;
        }

        /// <summary>
        /// Whether this object is a checkpoint — AS3 <c>tip='checkpoint'</c>, which
        /// <c>Location.as:2008</c> turns into a <c>CheckPoint</c> instead of a <c>Box</c>.
        /// </summary>
        private bool IsCheckpointObject =>
            _objectInstance != null &&
            (string.Equals(_objectInstance.objectType, "checkpoint", StringComparison.OrdinalIgnoreCase) ||
             (_objectInstance.definition != null &&
              _objectInstance.definition.family == MapObjectFamily.Checkpoint));

        /// <summary>
        /// AS3 <c>CheckPoint.activate()</c> (<c>CheckPoint.as:184-248</c>).
        ///
        /// <para>The decision and the runtime state live in <c>CampaignManager.ActivateCheckpoint</c>
        /// (rules in <c>PFE.Sim.Campaign.CheckpointRules</c>); what stays here is the object's own reads
        /// (<c>code</c>, <c>@tele</c>, <c>@main</c>) and the save that ends the oracle's method.</para>
        ///
        /// <para><b>The save is issued only when the activation actually ran.</b> AS3 returns early for an
        /// already-active or locked checkpoint and never reaches <c>saveGame()</c>
        /// (<c>CheckPoint.as:186-190</c>), so saving on every touch would be a write per press.</para>
        ///
        /// <para><b>Both of AS3's entry points land here.</b> The button arrives with
        /// <see cref="CheckpointEntry.Activate"/>; the walk-into area with
        /// <see cref="CheckpointEntry.Area"/>, which the campaign refuses unless the checkpoint is still
        /// fresh (<c>CheckPoint.as:271</c>). Still unported: the return-teleport interaction
        /// (<c>teleport</c>, <c>CheckPoint.as:250-267</c>), whose target rule is ported and tested but
        /// which no interaction surface offers yet.</para>
        /// </summary>
        /// <param name="entry">Which of AS3's two entry points is running.</param>
        private void ActivateCheckpoint(CheckpointEntry entry = CheckpointEntry.Activate)
        {
            PFE.Systems.Campaign.CampaignManager campaign = PFE.Systems.Campaign.CampaignManager.Current;
            if (campaign == null)
            {
                Debug.LogWarning("[DoorPropPresenter] checkpoint touched but there is no CampaignManager: " +
                                 "no checkpoint state was written and nothing was saved.");
                return;
            }

            ObjectInstance obj = _objectInstance;
            Vector3Int room = _room != null ? _room.landPosition : Vector3Int.zero;

            // `@tele` (CheckPoint.as:97-100) and `@main` (:123-126) come off the object's own XML.
            bool teleOn = !string.IsNullOrEmpty(obj.GetAttribute("tele", string.Empty));
            bool main = !string.IsNullOrEmpty(obj.GetAttribute("main", string.Empty));

            // createCheck's `param1`. AS3 keeps it in a local; the port writes it onto the placement
            // because the decision is made here rather than in the placement.
            bool isBegin = !string.IsNullOrEmpty(
                obj.GetAttribute(RoomPopulator.BeginCheckpointAttribute, string.Empty));

            // AS3 `inter.lock > 0 || inter.mine > 0` (CheckPoint.as:186-189). Both are read off the
            // DEFINITION row, which is the row `Interact` reads (`param2`, Interact.as:218-252) — a
            // placed checkpoint is built with no XML of its own (Location.createCheck passes no fourth
            // argument), so there is no placement-level override to look for.
            bool locked = CheckpointRules.IsLocked(
                ReadDefinitionAttribute(obj, "lock"),
                ReadDefinitionAttribute(obj, "mine"));

            if (!campaign.ActivateCheckpoint(campaign.CurrentLandId.CurrentValue, room, obj.code,
                    isBegin, teleOn, main, locked, entry))
            {
                return;
            }

            // AS3 CheckPoint.as:247 — `World.w.saveGame()`, the last line of activate().
            SaveManager.Instance?.RequestCheckpointSave();
        }

        /// <summary>
        /// An attribute off the object's <b>definition</b> row rather than the placement.
        ///
        /// <para><see cref="ObjectInstance.GetAttribute"/> reads the placement's own attributes; the
        /// definition is a separate object with its own <c>GetAttribute</c>. <c>lock</c> and <c>mine</c>
        /// live on the definition (<c>AllData.as:5008-5012</c>) and AS3 reads them from there, so the
        /// placement-first order that <c>GetHoldFrames</c>/<c>GetAllAct</c> use does not apply.</para>
        /// </summary>
        private static string ReadDefinitionAttribute(ObjectInstance obj, string key)
        {
            return obj?.definition != null ? obj.definition.GetAttribute(key, string.Empty) : string.Empty;
        }

        public void Interact(GameObject user)
        {
            // A checkpoint is not an `allact` in AS3 — CheckPoint builds its own Interact with
            // `actFun = activate` (CheckPoint.as:86-90) — so it is handled by family here, before the
            // dispatcher, and never falls through to ToggleOpen().
            if (IsCheckpointObject)
            {
                if (Time.frameCount == _lastInteractFrame) return;
                _lastInteractFrame = Time.frameCount;
                ActivateCheckpoint();
                return;
            }

            if (!IsInteractableDoor) return;

            if (Time.frameCount == _lastInteractFrame) return;
            _lastInteractFrame = Time.frameCount;

            // AS3 runs an object's `allact` script from Interact.act() (Interact.as:889). This method
            // is the "the action fired" entry point, not "the button went down": a door that authors
            // a `time` is held first (see HoldFrames) and reaches here only when the hold completes,
            // while a door that authors none reaches here on the press. That is the same split AS3
            // makes, where the zero-time branch fires is_act directly (UnitPlayer.as:1985-1988) and
            // every other branch arms t_action first.
            var context = new ObjectActionContext(_room, _objectInstance, user, transform.position);
            ObjectActionOutcome outcome = ObjectActions.Dispatch(in context);

            // Handled AND Refused both stop here. AS3 refuses a `comein` by returning null from
            // Land.gotoLoc and nothing happens; falling through to ToggleOpen() would instead open a
            // Z door onto a layer that does not exist. Only "no script" and "script not ported yet"
            // reach the fallback, which is what keeps the term* terminals behaving as they do today.
            if (outcome == ObjectActionOutcome.Handled || outcome == ObjectActionOutcome.Refused)
            {
                // AS3's beginAct() plays the `comein` clip for a Z door (Interact.as:1670-1676) —
                // the only state that animates. See BeginComeIn for the trigger-timing gap.
                if (outcome == ObjectActionOutcome.Handled &&
                    string.Equals(_objectInstance?.GetAllAct(), ComeInAction.Id, StringComparison.OrdinalIgnoreCase))
                {
                    BeginComeIn();
                }

                return;
            }

            ToggleOpen();
        }

        // Interaction entry points removed on purpose — this component must not read input.
        //
        // It used to own three more paths besides Interact(): an E/mouse poll in Update() whose
        // second branch fired on `_isPlayerNear || dist <= 1.8f` "even without pointing cursor",
        // an OnMouseDown() that toggled instantly, and an OnInteractPressed() with a third copy of
        // the cursor raycast. Together they meant a door responded to E from anywhere in the room
        // rather than from under the cursor, and a click skipped the hold that `time=10` doors
        // require. AS3 has exactly one entry point — UnitPlayer.actAction(), which resolves the cell
        // under the cursor and arms t_action (UnitPlayer.as:1922-2001) — so ownership lives there
        // now: PlayerController.ResolveInteractTarget() picks the target and
        // PlayerActionInteractor runs the hold. This class is a presenter plus an IInteractable.
        //
        // Unity's own OnMouseDown/OnMouseUp are deliberately not used either: they fire on the
        // collider under the pointer with no reach check and no hold, which is the same defect.

        private void Update()
        {
            TickAnimation(Time.deltaTime);

            // F9 toggles the door-collider debug overlay. This is the only key this component
            // reads, and it is editor/debug scaffolding rather than gameplay.
            bool f9Pressed = Input.GetKeyDown(KeyCode.F9) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f9Key.wasPressedThisFrame);
            if (f9Pressed)
            {
                ShowDebugVisuals = !ShowDebugVisuals;
                Debug.Log($"[DoorPropPresenter] Door Collider debug overlay: {(ShowDebugVisuals ? "ENABLED (Yellow)" : "DISABLED")}");
            }
        }

        public void UpdateDebugVisual()
        {
            bool shouldShow = ShowDebugVisuals && (_objectInstance == null || _objectInstance.isActive);

            if (!shouldShow)
            {
                if (_debugVisualGo != null)
                {
                    _debugVisualGo.SetActive(false);
                }
                return;
            }

            EnsureDebugVisual();
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(true);
            }
        }

        private void EnsureDebugVisual()
        {
            if (_debugVisualGo != null) return;

            _debugVisualGo = new GameObject("__DoorDebugVisual");
            _debugVisualGo.transform.SetParent(transform, false);

            Vector2 size;
            Vector2 offset;

            if (IsInteractableDoor && _triggerCollider != null)
            {
                size = _triggerCollider.size;
                offset = _triggerCollider.offset;
            }
            else
            {
                GetDoorBounds(out size, out offset);
            }

            _debugVisualGo.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
            _debugVisualGo.transform.localScale = new Vector3(size.x, size.y, 1f);

            _debugSpriteRenderer = _debugVisualGo.AddComponent<SpriteRenderer>();
            _debugSpriteRenderer.sprite = GetWhiteDebugSprite();
            _debugSpriteRenderer.color = IsInteractableDoor ? DoorTriggerFillColor : BarricadeFillColor;
            _debugSpriteRenderer.sortingLayerName = MapSortingLayers.Foreground;
            _debugSpriteRenderer.sortingOrder = 998;
        }

        private static Sprite GetWhiteDebugSprite()
        {
            if (_whiteDebugSprite == null)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _whiteDebugSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }
            return _whiteDebugSprite;
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugVisuals) return;

            Vector2 size;
            Vector2 offset;

            if (IsInteractableDoor && _triggerCollider != null)
            {
                size = _triggerCollider.size;
                offset = _triggerCollider.offset;
            }
            else
            {
                GetDoorBounds(out size, out offset);
            }

            Vector3 center = transform.position + new Vector3(offset.x, offset.y, 0f);
            Vector3 cubeSize = new Vector3(size.x, size.y, 0.05f);

            Color fill = IsInteractableDoor ? DoorTriggerFillColor : BarricadeFillColor;
            Color wire = IsInteractableDoor ? DoorTriggerWireColor : BarricadeWireColor;

            Gizmos.color = fill;
            Gizmos.DrawCube(center, cubeSize);

            Gizmos.color = wire;
            Gizmos.DrawWireCube(center, cubeSize);

#if UNITY_EDITOR
            string id = _objectInstance?.objectId ?? _visual?.objectId ?? "door";
            string status = IsInteractableDoor ? (IsOpen ? "Open" : "Closed") : "Solid Barrier";
            UnityEditor.Handles.Label(center, $"[{id}] ({status})");
#endif
        }
    }
}

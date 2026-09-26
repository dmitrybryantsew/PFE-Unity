using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Interaction;
using PFE.Systems.Map;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Physics;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Presenter component for in-room interactive door props (door1, door1a, door1b, septum, etc.).
    /// Direct port of AS3 Box.as door logic (initDoor, setDoor, attDoor).
    /// Handles player interaction, open/close frame switching with smooth frame animation,
    /// solid tile physics stamping, and horizontal ejection when closing while occupied.
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public class DoorPropPresenter : MonoBehaviour, IInteractable
    {
        public const float FrameDuration = 0.075f; // ~13.3 FPS, matching Flash AS3 30 FPS movieclip timing

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

        private bool _isPlayerNear;
        private int _lastInteractFrame = -1;

        private int _currentFrameIndex;
        private int _targetFrameIndex;
        private float _frameTimer;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;
        public bool IsOpen => _objectInstance?.runtimeState?.isOpen ?? false;

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

        public string ActionText => IsInteractableDoor ? (IsOpen ? "Close" : "Open") : string.Empty;
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

            // In AS3 (Box.as:653), initDoor stamps solid collision if closed
            ApplyTileCollision(IsOpen);
            UpdateVisualFrame(instant: true);
        }

        private void ConfigureCollider()
        {
            if (_triggerCollider == null) return;

            // If not an interactable door (e.g. septum barricade), disable the trigger collider
            // so it never captures cursor raycasts or proximity interaction scans.
            if (!IsInteractableDoor)
            {
                _triggerCollider.enabled = false;
                return;
            }

            _triggerCollider.enabled = true;
            float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
            float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.8f;

            // Interaction area has generous padding (AS3 actionDist reach zone)
            _triggerCollider.size = new Vector2(w + 1.2f, h + 0.4f);
            _triggerCollider.offset = new Vector2(0f, h * 0.5f);
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
            UpdateVisualFrame(instant: false);

            if (_triggerSystem != null && _room != null)
            {
                _triggerSystem.OnObjectInteracted(_room, _objectInstance);
            }
        }

        public void ToggleOpen()
        {
            if (!IsInteractableDoor) return;
            SetOpen(!IsOpen);
        }

        public void ApplyTileCollision(bool isOpen)
        {
            if (_room?.tiles == null || _objectInstance == null) return;

            // Compute covered tile range
            Vector2 size = _visual != null ? (Vector2)_visual.pixelSize : new Vector2(40f, 80f);
            float leftPx = _objectInstance.position.x - size.x * 0.5f;
            float rightPx = _objectInstance.position.x + size.x * 0.5f - 1f;
            float bottomPx = _objectInstance.position.y;
            float topPx = _objectInstance.position.y + size.y - 1f;

            int txMin = Mathf.FloorToInt(leftPx / WorldConstants.TILE_SIZE);
            int txMax = Mathf.FloorToInt(rightPx / WorldConstants.TILE_SIZE);
            int tyMin = Mathf.FloorToInt(bottomPx / WorldConstants.TILE_SIZE);
            int tyMax = Mathf.FloorToInt(topPx / WorldConstants.TILE_SIZE);

            TilePhysicsType targetType = isOpen ? TilePhysicsType.Air : TilePhysicsType.Wall;

            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = _room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null)
                    {
                        tile.physicsType = targetType;
                    }
                }
            }
        }

        public void UpdateVisualFrame(bool instant = false)
        {
            if (_visual == null || !_visual.HasFrames) return;

            _targetFrameIndex = IsOpen ? GetOpenFrameIndex() : 0;
            if (instant)
            {
                _currentFrameIndex = _targetFrameIndex;
                _frameTimer = 0f;
                UpdateRendererSprite();
            }
        }

        private int GetOpenFrameIndex()
        {
            if (_visual == null || !_visual.HasFrames) return 0;
            return _visual.frames.Length >= 3 ? 2 : (_visual.frames.Length - 1);
        }

        public void TickAnimation(float deltaTime)
        {
            if (_currentFrameIndex == _targetFrameIndex) return;

            _frameTimer += deltaTime;
            while (_frameTimer >= FrameDuration && _currentFrameIndex != _targetFrameIndex)
            {
                _frameTimer -= FrameDuration;
                _currentFrameIndex += (_targetFrameIndex > _currentFrameIndex) ? 1 : -1;
                UpdateRendererSprite();
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
            float doorW = (_visual != null && _visual.pixelSize.x > 0) ? (_visual.pixelSize.x * 0.01f) : 0.6f;
            float doorH = (_visual != null && _visual.pixelSize.y > 0) ? (_visual.pixelSize.y * 0.01f) : 0.8f;
            Vector2 boxCenter = (Vector2)transform.position + new Vector2(0f, doorH * 0.5f);
            Vector2 boxSize = new Vector2(doorW + 0.2f, doorH);

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
        /// Eject an individual unit if it overlaps the door's volume.
        /// Pushes horizontally to the nearest open side, keeping Y unchanged.
        /// </summary>
        public bool EjectUnitIfOverlapping(GameObject unit)
        {
            if (unit == null) return false;

            float doorW = (_visual != null && _visual.pixelSize.x > 0) ? (_visual.pixelSize.x * 0.01f) : 0.6f;
            float doorH = (_visual != null && _visual.pixelSize.y > 0) ? (_visual.pixelSize.y * 0.01f) : 0.8f;

            float doorCenterX = transform.position.x;
            float doorBottomY = transform.position.y;
            float doorLeft = doorCenterX - doorW * 0.5f;
            float doorRight = doorCenterX + doorW * 0.5f;
            float doorTop = doorBottomY + doorH;

            Vector3 unitPos = unit.transform.position;
            float halfWidth = 0.15f;
            float unitBottom = unitPos.y;
            float unitTop = unitPos.y + 0.5f;

            var col = unit.GetComponent<Collider2D>();
            if (col != null && !col.isTrigger)
            {
                halfWidth = Mathf.Max(0.12f, col.bounds.extents.x);
                unitBottom = col.bounds.min.y;
                unitTop = col.bounds.max.y;
            }
            else
            {
                var motor = unit.GetComponent<TilePhysicsController>();
                if (motor != null)
                {
                    halfWidth = 0.15f;
                    unitBottom = unitPos.y;
                    unitTop = unitPos.y + 0.5f;
                }
            }

            float unitLeft = unitPos.x - halfWidth;
            float unitRight = unitPos.x + halfWidth;

            // Check AABB overlap between unit and door frame
            bool overlapX = unitRight > doorLeft + 0.01f && unitLeft < doorRight - 0.01f;
            bool overlapY = unitTop > doorBottomY && unitBottom < doorTop;

            if (!overlapX || !overlapY)
            {
                return false;
            }

            // Margin outside the door's solid bounds to ensure no immediate re-collision with wall
            const float clearMargin = 0.08f;
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

            var movementMotor = unit.GetComponent<IMovementMotor>();
            if (movementMotor != null)
            {
                movementMotor.SetUnityPosition(newPos);
            }
            else if (unit.TryGetComponent<TilePhysicsController>(out var tpc))
            {
                tpc.SetUnityPosition(newPos);
            }
            else
            {
                var rb = unit.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.position = new Vector2(targetX, unitPos.y);
                }
                unit.transform.position = newPos;
            }

            return true;
        }

        private bool IsPositionBlockedByWall(float worldX, float worldY)
        {
            if (_room?.tiles == null) return false;

            float roomOriginPixelX = _room.landPosition.x * WorldConstants.ROOM_SIZE_PIXELS.x;
            float roomOriginPixelY = _room.landPosition.y * WorldConstants.ROOM_SIZE_PIXELS.y;

            float localPixelX = (worldX * 100f) - roomOriginPixelX;
            float localPixelY = (worldY * 100f) - roomOriginPixelY;

            int tx = Mathf.FloorToInt(localPixelX / WorldConstants.TILE_SIZE);
            int ty = Mathf.FloorToInt(localPixelY / WorldConstants.TILE_SIZE);

            TileData tile = _room.GetTileAtCoord(new Vector2Int(tx, ty));
            return tile != null && tile.physicsType == TilePhysicsType.Wall;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other.CompareTag("Player") || other.GetComponent<PFE.Systems.Physics.IMovementMotor>() != null)
            {
                _isPlayerNear = true;
            }
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (other.CompareTag("Player") || other.GetComponent<PFE.Systems.Physics.IMovementMotor>() != null)
            {
                _isPlayerNear = false;
            }
        }

        public bool CanInteract(GameObject user)
        {
            if (!IsInteractableDoor) return false;

            if (user != null)
            {
                float dist = Vector2.Distance(transform.position, user.transform.position);
                return dist <= 2.5f; // AS3 actionDist reach zone (200px = 2.0m)
            }
            return _isPlayerNear;
        }

        public void Interact(GameObject user)
        {
            if (!IsInteractableDoor) return;

            if (Time.frameCount == _lastInteractFrame) return;
            _lastInteractFrame = Time.frameCount;

            ToggleOpen();
        }

        public void OnInteractPressed()
        {
            if (!IsInteractableDoor) return;

            var player = GameObject.FindWithTag("Player");
            if (_isPlayerNear || CanInteract(player))
            {
                Interact(player);
            }
        }

        /// <summary>
        /// Check if mouse cursor is currently pointing over this door.
        /// </summary>
        public bool IsMouseOver()
        {
            if (!IsInteractableDoor) return false;

            Camera cam = Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            if (cam == null) return false;

            Vector3 mouseWorld = cam.ScreenToWorldPoint(Input.mousePosition);
            Vector2 mousePos = new Vector2(mouseWorld.x, mouseWorld.y);

            // 1. Check trigger collider
            if (_triggerCollider != null && _triggerCollider.OverlapPoint(mousePos))
            {
                return true;
            }

            // 2. Check sprite renderer bounds
            if (_renderer != null && _renderer.bounds.Contains(new Vector3(mousePos.x, mousePos.y, transform.position.z)))
            {
                return true;
            }

            // 3. Fallback: check door rectangle with margin
            float w = _visual != null ? Mathf.Max(0.5f, _visual.pixelSize.x * 0.01f) : 0.6f;
            float h = _visual != null ? Mathf.Max(0.8f, _visual.pixelSize.y * 0.01f) : 0.8f;
            Vector2 center = (Vector2)transform.position + new Vector2(0f, h * 0.5f);
            Rect r = new Rect(center.x - w * 0.5f - 0.25f, center.y - h * 0.5f - 0.2f, w + 0.5f, h + 0.4f);
            return r.Contains(mousePos);
        }

        private void OnMouseDown()
        {
            if (!IsInteractableDoor) return;

            var player = GameObject.FindWithTag("Player");
            if (player == null || Vector2.Distance(transform.position, player.transform.position) <= 2.5f)
            {
                Interact(player);
            }
        }

        private void Update()
        {
            TickAnimation(Time.deltaTime);

            bool mouseOver = IsMouseOver();

            bool ePressed = Input.GetKeyDown(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasPressedThisFrame);

            bool mouseClicked = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) ||
                (UnityEngine.InputSystem.Mouse.current != null &&
                 (UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame || UnityEngine.InputSystem.Mouse.current.rightButton.wasPressedThisFrame));

            var player = GameObject.FindWithTag("Player");
            float dist = player != null ? Vector2.Distance(transform.position, player.transform.position) : 0f;
            bool inReach = player == null || dist <= 2.5f;

            // 1. Cursor is pointing at door and pressed E or clicked mouse
            if (mouseOver && (ePressed || mouseClicked) && inReach)
            {
                Interact(player);
                return;
            }

            // 2. Player is near door and pressed E (even without pointing cursor)
            if ((_isPlayerNear || dist <= 1.8f) && ePressed)
            {
                Interact(player);
                return;
            }

            // Hotkey F9: toggle door collider debug overlay
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
                float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
                float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.8f;
                size = new Vector2(w, h);
                offset = new Vector2(0f, h * 0.5f);
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
                float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
                float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.8f;
                size = new Vector2(w, h);
                offset = new Vector2(0f, h * 0.5f);
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

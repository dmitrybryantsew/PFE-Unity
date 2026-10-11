using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// MonoBehaviour component that handles door interaction in the scene.
    /// Attached to door GameObjects to detect player collisions and trigger room transitions.
    /// From AS3: Door interaction system (fe/units/hero.as lines 2300-2450)
    /// </summary>
    // BoxCollider2D, not Collider2D: Collider2D is abstract, so Unity can never auto-add it and
    // AddComponent<DoorTrigger>() on a bare GameObject silently returns null. The code below only
    // ever reads or configures a BoxCollider2D anyway.
    [RequireComponent(typeof(BoxCollider2D))]
    public class DoorTrigger : MonoBehaviour
    {
        [Header("Door Configuration")]
        [Tooltip("The door instance this trigger represents")]
        public DoorInstance doorInstance;

        [Tooltip("The room this door belongs to")]
        public RoomInstance owningRoom;

        [Tooltip("Visual indicator for active door")]
        [SerializeField] private SpriteRenderer doorSprite;

        [Header("Transition Settings")]
        [Tooltip("Cooldown in seconds before another transition can occur")]
        [SerializeField] private float transitionCooldown = 0.5f;

        [Tooltip("Enable/disable this door")]
        [SerializeField] private bool isEnabled = true;

        private float lastTransitionTime = -999f;
        private Collider2D triggerCollider;

        private static readonly Color BoundaryFillColor = new Color(0.0f, 0.85f, 1.0f, 0.35f);
        private static readonly Color BoundaryWireColor = new Color(0.2f, 0.95f, 1.0f, 0.9f);
        private static Sprite _whiteDebugSprite;
        private GameObject _debugVisualGo;
        private SpriteRenderer _debugSpriteRenderer;

        #region Initialization

        private void Awake()
        {
            triggerCollider = GetComponent<Collider2D>();
            if (triggerCollider != null)
            {
                triggerCollider.isTrigger = true;
            }
        }

        private void Start()
        {
            UpdateDoorVisual();
        }

        private void Update()
        {
            UpdateDebugVisual();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Set the door data for this trigger.
        /// </summary>
        public void SetDoor(DoorInstance door, RoomInstance room)
        {
            doorInstance = door;
            owningRoom = room;
            isEnabled = door != null && door.isActive;
            UpdateDoorVisual();
        }

        /// <summary>
        /// Enable or disable this door trigger.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            isEnabled = enabled;
            if (triggerCollider != null)
            {
                triggerCollider.enabled = enabled;
            }
            UpdateDoorVisual();
        }

        /// <summary>
        /// Check if this door is ready for transition.
        /// </summary>
        public bool IsReadyForTransition()
        {
            return isEnabled &&
                   doorInstance != null &&
                   doorInstance.isActive &&
                   Time.time > lastTransitionTime + transitionCooldown;
        }

        /// <summary>
        /// Trigger room transition (called by player or manually).
        /// </summary>
        public bool TryTriggerTransition(GameObject player)
        {
            if (!IsReadyForTransition())
            {
                return false;
            }

            lastTransitionTime = Time.time;

            // Notify the room transition system
            RoomTransitionManager.Instance?.TransitionThroughDoor(doorInstance, player);

            return true;
        }

        /// <summary>
        /// Configure trigger collider size and offset.
        /// </summary>
        public void ConfigureCollider(Vector2 size, Vector2 offset)
        {
            if (triggerCollider == null)
            {
                triggerCollider = GetComponent<Collider2D>();
            }

            if (triggerCollider is BoxCollider2D box)
            {
                box.size = size;
                box.offset = offset;
                box.isTrigger = true;
            }
        }

        #endregion

        #region Unity Events

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!isEnabled || doorInstance == null || !doorInstance.isActive)
            {
                return;
            }

            if (IsPlayerActor(other))
            {
                TryTriggerTransition(other.gameObject);
            }
        }

        /// <summary>
        /// Whether <paramref name="other"/> is the player — and <b>only</b> the player.
        ///
        /// <para><b>The old clause was <c>other.CompareTag("Player") ||
        /// other.GetComponent&lt;IMovementMotor&gt;() != null</c>, and that <c>||</c> was the bug.</b>
        /// Every spawned NPC carrying a motor satisfies it — <c>PfeDebugSettings.UnitMotor</c> gives them
        /// a <see cref="PFE.Systems.Physics.TilePhysicsController"/>, which implements
        /// <c>IMovementMotor</c> — so an enemy bumping a door trigger called
        /// <c>RoomTransitionManager.TransitionThroughDoor</c> with <i>its own</i> GameObject as the
        /// "player": the room was re-rendered, <c>landMap.currentRoom</c> flipped and the camera snapped,
        /// while the real player was left standing in the old room's coordinates. Reported as
        /// "map graphics fully disappear while I am in the middle of the room".</para>
        ///
        /// <para><b>AS3 only ever moves the player.</b> <c>UnitPlayer.outLoc</c>
        /// (<c>UnitPlayer.as:540</c>) overrides the base <c>Unit.outLoc</c> (<c>Unit.as:1882</c>), which
        /// returns <c>false</c> and never changes room. The player root is tagged <c>"Player"</c> and
        /// carries the <c>PlayerController</c> (<c>PlayerRigBuilder.cs:127,133</c>), so either half of
        /// the test would do; both are kept so a missing tag cannot silently disable every door. Public
        /// so the rule is assertable without a live physics callback.</para>
        /// </summary>
        public static bool IsPlayerActor(Collider2D other)
        {
            if (other == null)
            {
                return false;
            }

            if (other.CompareTag("Player"))
            {
                return true;
            }

            return other.GetComponentInParent<PFE.Entities.Player.PlayerController>() != null;
        }

        #endregion

        #region Private Methods

        private void UpdateDoorVisual()
        {
            if (doorSprite == null) return;

            // Show door as enabled/disabled based on state
            doorSprite.enabled = isEnabled && doorInstance != null && doorInstance.isActive;

            // Could change color/sprite based on door quality
            if (doorSprite.enabled && doorInstance != null)
            {
                // Optional: Visual feedback for door quality
                // doorSprite.color = GetColorForQuality(doorInstance.quality);
            }
        }

        public void UpdateDebugVisual()
        {
            var settings = Resources.Load<PFE.Core.PfeDebugSettings>("PfeDebugSettings");
            bool shouldShow = (settings != null && settings.ShowDoorColliderDebug) && isEnabled && doorInstance != null && doorInstance.isActive;

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

            _debugVisualGo = new GameObject("__BoundaryDoorDebugVisual");
            _debugVisualGo.transform.SetParent(transform, false);

            Vector2 size = Vector2.one * 0.4f;
            Vector2 offset = Vector2.zero;
            if (triggerCollider is BoxCollider2D box)
            {
                size = box.size;
                offset = box.offset;
            }

            _debugVisualGo.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
            _debugVisualGo.transform.localScale = new Vector3(size.x, size.y, 1f);

            _debugSpriteRenderer = _debugVisualGo.AddComponent<SpriteRenderer>();
            _debugSpriteRenderer.sprite = GetWhiteDebugSprite();
            _debugSpriteRenderer.color = BoundaryFillColor;
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

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (doorInstance == null) return;

            // Draw door position
            Gizmos.color = isEnabled ? Color.green : Color.red;
            Gizmos.DrawWireSphere(transform.position, 0.5f);

            // Draw connection to target room
            if (doorInstance.isActive && doorInstance.targetRoomPosition != Vector3Int.zero)
            {
                Gizmos.color = Color.yellow;
                Vector3 targetPos = new Vector3(
                    doorInstance.targetRoomPosition.x * WorldConstants.ROOM_SIZE_PIXELS.x,
                    WorldCoordinates.LandRowToWorldPixelY(doorInstance.targetRoomPosition.y),
                    0
                );
                Gizmos.DrawLine(transform.position, targetPos);
            }

            var settings = Resources.Load<PFE.Core.PfeDebugSettings>("PfeDebugSettings");
            if (settings != null && settings.ShowDoorColliderDebug && triggerCollider != null)
            {
                Gizmos.color = BoundaryFillColor;
                Gizmos.DrawCube(triggerCollider.bounds.center, triggerCollider.bounds.size);
                Gizmos.color = BoundaryWireColor;
                Gizmos.DrawWireCube(triggerCollider.bounds.center, triggerCollider.bounds.size);

#if UNITY_EDITOR
                UnityEditor.Handles.Label(triggerCollider.bounds.center, $"[Passage Door] -> {doorInstance.targetRoomPosition} ({doorInstance.side})");
#endif
            }
        }

        #endregion
    }
}

using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// MonoBehaviour component that handles door interaction in the scene.
    /// Attached to door GameObjects to detect player collisions and trigger room transitions.
    /// From AS3: Door interaction system (fe/units/hero.as lines 2300-2450)
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
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

        #endregion

        #region Unity Events

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!isEnabled || doorInstance == null || !doorInstance.isActive)
            {
                return;
            }

            // Check if player entered the door trigger
            if (other.CompareTag("Player"))
            {
                TryTriggerTransition(other.gameObject);
            }
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
                    doorInstance.targetRoomPosition.y * WorldConstants.ROOM_SIZE_PIXELS.y,
                    0
                );
                Gizmos.DrawLine(transform.position, targetPos);
            }
        }

        #endregion
    }
}

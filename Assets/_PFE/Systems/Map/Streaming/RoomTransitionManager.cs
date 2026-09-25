using UnityEngine;
using System;
using PFE.Systems.Map;
using PFE.Systems.Physics;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// Manages room transitions when the player uses doors.
    /// Handles coordinate conversion, player repositioning, and camera transitions.
    /// From AS3: Room transition system (fe/land/Land.as lines 1800-2100)
    /// </summary>
    public class RoomTransitionManager : MonoBehaviour
    {
        private static RoomTransitionManager _instance;
        public static RoomTransitionManager Instance => _instance;

        [Header("References")]
        [Tooltip("The LandMap containing all rooms")]
        [SerializeField] private LandMap landMap;

        [Tooltip("The RoomStreamingManager for activating/deactivating rooms")]
        [SerializeField] private RoomStreamingManager streamingManager;

        [Header("Transition Settings")]
        [Tooltip("Duration of room transition in seconds")]
        [SerializeField] private float transitionDuration = 0.3f;

        [Tooltip("Offset from door when spawning player in new room")]
        [SerializeField] private float spawnOffset = 1f;

        // Events
        public event Action<RoomInstance, RoomInstance> OnRoomTransitionStart;
        public event Action<RoomInstance, RoomInstance> OnRoomTransitionComplete;

        private bool isTransitioning = false;
        private float transitionStartTime;

        #region Initialization

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Start()
        {
            // Auto-find references if not set
            // Note: LandMap is not a MonoBehaviour, so FindObjectOfType won't work
            // It must be set via Inspector or assigned programmatically
        }

        #endregion

        #region Public API

        /// <summary>
        /// Set the LandMap reference (call this if not set in Inspector).
        /// </summary>
        public void SetLandMap(LandMap map)
        {
            landMap = map;
        }

        /// <summary>
        /// Transition to a new room through a door.
        /// </summary>
        /// <param name="door">The door being used</param>
        /// <param name="player">The player GameObject</param>
        public void TransitionThroughDoor(DoorInstance door, GameObject player)
        {
            if (isTransitioning || door == null || player == null || landMap == null)
            {
                Debug.LogWarning("RoomTransitionManager: Invalid transition request");
                return;
            }

            if (!door.isActive)
            {
                Debug.LogWarning($"RoomTransitionManager: Door {door.doorIndex} is not active");
                return;
            }

            RoomInstance currentRoom = landMap.currentRoom;
            RoomInstance targetRoom = landMap.GetRoom(door.targetRoomPosition);

            if (targetRoom == null)
            {
                Debug.LogError($"RoomTransitionManager: Target room at {door.targetRoomPosition} does not exist");
                return;
            }

            StartCoroutine(PerformTransition(currentRoom, targetRoom, door, player));
        }

        /// <summary>
        /// Direct transition to a specific room (for debugging or teleportation).
        /// </summary>
        public void TransitionToRoom(Vector3Int roomPosition, GameObject player)
        {
            if (isTransitioning || player == null || landMap == null)
            {
                return;
            }

            RoomInstance currentRoom = landMap.currentRoom;
            RoomInstance targetRoom = landMap.GetRoom(roomPosition);

            if (targetRoom == null)
            {
                Debug.LogError($"RoomTransitionManager: Room at {roomPosition} does not exist");
                return;
            }

            // Find spawn point in target room
            Vector3 spawnPos = WorldCoordinates.LandToWorld(roomPosition, Vector2.zero);
            spawnPos.z = player.transform.position.z;

            StartCoroutine(PerformTransition(currentRoom, targetRoom, null, player, spawnPos));
        }

        #endregion

        #region Private Methods

        private System.Collections.IEnumerator PerformTransition(
            RoomInstance fromRoom,
            RoomInstance toRoom,
            DoorInstance door,
            GameObject player,
            Vector3? customSpawnPos = null)
        {
            isTransitioning = true;
            transitionStartTime = Time.time;

            // Notify start of transition
            OnRoomTransitionStart?.Invoke(fromRoom, toRoom);

            // Calculate spawn position
            Vector3 spawnPos = customSpawnPos ?? CalculateSpawnPosition(door, toRoom, player.transform.position);

            // Begin transition
            Debug.Log($"Transitioning from {fromRoom.id} to {toRoom.id}");

            // Update streaming (activate new room, deactivate old)
            if (streamingManager != null)
            {
                streamingManager.ActivateRoom(toRoom, fromRoom);
            }

            // LandMap's own bookkeeping was never updated here, so `landMap.currentRoom` kept
            // pointing at the room the player started in. RoomStreamingManager had just set
            // that old room's isActive = false, and RoomInstance.Update() early-returns when
            // inactive — so LandMap.Update() became a no-op and the room the player is actually
            // in never ticked its units or objects. It also made the NEXT transition compute
            // `fromRoom` from the wrong room. Activation stays with the streaming manager; this
            // only fixes the bookkeeping.
            if (landMap != null && toRoom != null)
            {
                landMap.SetCurrentRoom(toRoom);
            }

            // Move player to spawn position
            player.transform.position = spawnPos;

            // P0-2: the transform move alone desyncs the movement motor. TilePhysicsController
            // keeps its own authoritative posX/posY plus currentRoom, and none of those were
            // updated — so the next tick collided against the OLD room's tiles at the NEW
            // room's coordinates (falling through floors / sticking in walls), or fought the
            // transform back to the old position. Hand off explicitly instead.
            // motor == null is legitimate: an AI-driven or legacy object has no motor, and a
            // transform-only move is correct for it.
            IMovementMotor motor = player.GetComponent<IMovementMotor>();
            if (motor != null && toRoom != null)
            {
                motor.RepositionForRoom(toRoom, spawnPos);
            }

            // Wait for transition duration
            yield return new WaitForSeconds(transitionDuration);

            // Transition complete
            OnRoomTransitionComplete?.Invoke(toRoom, fromRoom);

            isTransitioning = false;

            Debug.Log($"Transition complete. Now in {toRoom.id}");
        }

        private Vector3 CalculateSpawnPosition(DoorInstance door, RoomInstance targetRoom, Vector3 currentPlayerPos)
        {
            // Find the target door in the new room
            DoorInstance targetDoor = null;
            if (door != null && targetRoom != null)
            {
                foreach (var d in targetRoom.doors)
                {
                    if (d.doorIndex == door.targetDoorIndex)
                    {
                        targetDoor = d;
                        break;
                    }
                }
            }

            if (targetDoor == null)
            {
                // No target door, spawn at center of room
                Vector3 roomCenter = WorldCoordinates.LandToWorld(targetRoom.landPosition, Vector2.zero);
                roomCenter.x += WorldConstants.ROOM_SIZE_PIXELS.x * 0.5f;
                roomCenter.y += WorldConstants.ROOM_SIZE_PIXELS.y * 0.5f;
                return roomCenter;
            }

            // Calculate spawn position relative to target door
            Vector3 doorWorldPos = WorldCoordinates.LandToWorld(
                targetRoom.landPosition,
                targetDoor.tilePosition
            );

            // Determine spawn offset based on door side
            Vector2 offset = Vector2.zero;
            switch (targetDoor.side)
            {
                case DoorSide.Left:
                    offset = new Vector2(-this.spawnOffset, 0);
                    break;
                case DoorSide.Right:
                    offset = new Vector2(this.spawnOffset, 0);
                    break;
                case DoorSide.Top:
                    offset = new Vector2(0, this.spawnOffset);
                    break;
                case DoorSide.Bottom:
                    offset = new Vector2(0, -this.spawnOffset);
                    break;
            }

            return doorWorldPos + (Vector3)offset;
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (!isTransitioning) return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, Vector3.one * 2f);
        }

        #endregion
    }
}

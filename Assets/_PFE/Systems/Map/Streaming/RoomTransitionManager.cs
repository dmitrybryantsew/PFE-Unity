using UnityEngine;
using System;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
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
        public static RoomTransitionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<RoomTransitionManager>();
                }
                return _instance;
            }
        }

        [Header("References")]
        [Tooltip("The LandMap containing all rooms")]
        [SerializeField] private LandMap landMap;

        [Tooltip("The RoomStreamingManager for activating/deactivating rooms")]
        [SerializeField] private RoomStreamingManager streamingManager;

        [Tooltip("The RoomVisualController for re-rendering rooms on transition")]
        [SerializeField] private RoomVisualController visualController;

        [Tooltip("The TileAssetDatabase for rendering room tiles")]
        [SerializeField] private TileAssetDatabase tileDatabase;

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
            if (visualController == null)
            {
                visualController = FindFirstObjectByType<RoomVisualController>();
            }

            if (streamingManager == null)
            {
                streamingManager = FindFirstObjectByType<RoomStreamingManager>();
            }
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
        /// Set the RoomVisualController and optional TileAssetDatabase.
        /// </summary>
        public void SetVisualController(RoomVisualController controller, TileAssetDatabase database = null)
        {
            visualController = controller;
            if (database != null)
            {
                tileDatabase = database;
            }
        }

        /// <summary>
        /// Set the RoomStreamingManager reference.
        /// </summary>
        public void SetStreamingManager(RoomStreamingManager manager)
        {
            streamingManager = manager;
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

            // Find spawn point in target room (center in Unity units)
            Vector2 roomCenterPixels = new Vector2(
                (roomPosition.x + 0.5f) * WorldConstants.ROOM_SIZE_PIXELS.x,
                (roomPosition.y + 0.5f) * WorldConstants.ROOM_SIZE_PIXELS.y
            );
            Vector3 spawnPos = WorldCoordinates.PixelToUnity(roomCenterPixels);
            spawnPos.z = player.transform.position.z;

            StartCoroutine(PerformTransition(currentRoom, targetRoom, null, player, spawnPos));
        }

        /// <summary>
        /// Transition to an adjacent room when crossing room boundaries.
        /// Port of AS3 Land.gotoLoc(1..4).
        /// 1 = Left, 2 = Right, 3 = Bottom, 4 = Top
        /// Returns true if a transition was started.
        /// </summary>
        public bool TransitionThroughEdge(int direction, GameObject player)
        {
            if (isTransitioning || player == null || landMap == null) return false;

            RoomInstance current = landMap.currentRoom;
            if (current == null) return false;

            Vector3Int targetPos = current.landPosition;
            switch (direction)
            {
                case 1: targetPos.x -= 1; break; // Left
                case 2: targetPos.x += 1; break; // Right
                case 3: targetPos.y += 1; break; // Bottom (Y increases downward in LandMap grid)
                case 4: targetPos.y -= 1; break; // Top (Y decreases upward in LandMap grid)
                default: return false;
            }

            RoomInstance targetRoom = landMap.GetRoom(targetPos);
            if (targetRoom == null) return false;

            Vector3 spawnPos = ComputeEdgeSpawnPosition(direction, current, targetRoom, player.transform.position);

            StartCoroutine(PerformTransition(current, targetRoom, null, player, spawnPos));
            return true;
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

            // Yield a frame so any physics callback that triggered the transition (e.g. OnTriggerEnter2D)
            // finishes completely before rooms are swapped and game objects recreated
            yield return null;

            if (player == null || toRoom == null)
            {
                isTransitioning = false;
                yield break;
            }

            // Notify start of transition
            OnRoomTransitionStart?.Invoke(fromRoom, toRoom);

            // Calculate spawn position
            Vector3 spawnPos = customSpawnPos ?? CalculateSpawnPosition(door, toRoom, player.transform.position);

            // Begin transition
            Debug.Log($"Transitioning from {fromRoom?.id} to {toRoom.id}");

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

            // Re-render visuals for the new room
            if (visualController != null)
            {
                TileAssetDatabase db = tileDatabase != null ? tileDatabase : visualController.TileAssetDatabase;
                if (db != null)
                {
                    visualController.Initialize(toRoom, db);
                }
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

            // Snap camera directly to player in the new room
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                var follow = mainCam.GetComponent<PFE.Core.CameraFollow>();
                float camZ = follow != null ? follow.offset.z : -10f;
                mainCam.transform.position = new Vector3(spawnPos.x, spawnPos.y, camZ);
            }

            // Wait for transition duration
            yield return new WaitForSeconds(transitionDuration);

            // Transition complete
            OnRoomTransitionComplete?.Invoke(toRoom, fromRoom);

            isTransitioning = false;

            Debug.Log($"Transition complete. Now in {toRoom.id}");
        }

        public Vector3 CalculateSpawnPosition(DoorInstance door, RoomInstance targetRoom, Vector3 currentPlayerPos)
        {
            return ComputeSpawnPosition(door, targetRoom, currentPlayerPos, spawnOffset);
        }

        public static Vector3 GetRoomOriginUnity(RoomInstance room)
        {
            if (room == null) return Vector3.zero;
            int borderOffset = Mathf.Max(0, room.borderOffset);
            Vector2 roomPixelPos = new Vector2(
                room.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE - borderOffset * WorldConstants.TILE_SIZE,
                room.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE - borderOffset * WorldConstants.TILE_SIZE
            );
            return WorldCoordinates.PixelToUnity(roomPixelPos);
        }

        /// <summary>
        /// Compute entry spawn position when walking through an edge/passage into an adjacent room.
        /// Port of AS3 Land.gotoLoc(1..4).
        /// Direction: 1 = Left, 2 = Right, 3 = Bottom, 4 = Top
        /// </summary>
        public static Vector3 ComputeEdgeSpawnPosition(
            int direction,
            RoomInstance fromRoom,
            RoomInstance toRoom,
            Vector3 currentPlayerPos,
            float offsetUnits = 0.4f)
        {
            if (toRoom == null) return currentPlayerPos;

            Vector3 fromOrigin = GetRoomOriginUnity(fromRoom);
            Vector3 toOrigin = GetRoomOriginUnity(toRoom);

            float relX = currentPlayerPos.x - fromOrigin.x;
            float relY = currentPlayerPos.y - fromOrigin.y;

            float toWidthUnits = toRoom.width * WorldConstants.TILE_SIZE * 0.01f;
            float toHeightUnits = toRoom.height * WorldConstants.TILE_SIZE * 0.01f;

            float spawnX = toOrigin.x;
            float spawnY = toOrigin.y;

            switch (direction)
            {
                case 1: // Exited Left, enter toRoom from Right
                    spawnX = toOrigin.x + toWidthUnits - offsetUnits;
                    spawnY = toOrigin.y + Mathf.Clamp(relY, offsetUnits, toHeightUnits - offsetUnits);
                    break;

                case 2: // Exited Right, enter toRoom from Left
                    spawnX = toOrigin.x + offsetUnits;
                    spawnY = toOrigin.y + Mathf.Clamp(relY, offsetUnits, toHeightUnits - offsetUnits);
                    break;

                case 3: // Exited Bottom, enter toRoom from Top
                    spawnX = toOrigin.x + Mathf.Clamp(relX, offsetUnits, toWidthUnits - offsetUnits);
                    spawnY = toOrigin.y + toHeightUnits - offsetUnits;
                    break;

                case 4: // Exited Top, enter toRoom from Bottom
                    spawnX = toOrigin.x + Mathf.Clamp(relX, offsetUnits, toWidthUnits - offsetUnits);
                    spawnY = toOrigin.y + offsetUnits;
                    break;

                default:
                    return currentPlayerPos;
            }

            return new Vector3(spawnX, spawnY, currentPlayerPos.z);
        }

        public static Vector3 ComputeSpawnPosition(DoorInstance door, RoomInstance targetRoom, Vector3 currentPlayerPos, float offsetUnits = 0.4f)
        {
            if (targetRoom == null)
            {
                return currentPlayerPos;
            }

            // Find the target door in the new room
            DoorInstance targetDoor = null;
            if (door != null && targetRoom.doors != null)
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
                Vector2 roomCenterPixels = new Vector2(
                    (targetRoom.landPosition.x + 0.5f) * WorldConstants.ROOM_SIZE_PIXELS.x,
                    (targetRoom.landPosition.y + 0.5f) * WorldConstants.ROOM_SIZE_PIXELS.y
                );
                Vector3 centerUnity = WorldCoordinates.PixelToUnity(roomCenterPixels);
                centerUnity.z = currentPlayerPos.z;
                return centerUnity;
            }

            // Calculate door center position in world pixels
            Vector2 tilePixel = WorldCoordinates.TileToPixel(targetDoor.tilePosition) +
                                new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f);
            Vector2 doorWorldPixel = WorldCoordinates.LandToWorld(targetRoom.landPosition, tilePixel);

            // Convert to Unity world coordinates
            Vector3 doorWorldUnity = WorldCoordinates.PixelToUnity(doorWorldPixel);

            // Determine spawn offset based on door side (offset pushes player INSIDE the target room)
            Vector2 offset = Vector2.zero;
            switch (targetDoor.side)
            {
                case DoorSide.Left:
                    // Door is at left room boundary: push right (+X) into room
                    offset = new Vector2(offsetUnits, 0f);
                    break;
                case DoorSide.Right:
                    // Door is at right room boundary: push left (-X) into room
                    offset = new Vector2(-offsetUnits, 0f);
                    break;
                case DoorSide.Top:
                    // Door is at top room boundary: push down (-Y) into room
                    offset = new Vector2(0f, -offsetUnits);
                    break;
                case DoorSide.Bottom:
                    // Door is at bottom room boundary: push up (+Y) into room
                    offset = new Vector2(0f, offsetUnits);
                    break;
            }

            Vector3 spawnPos = doorWorldUnity + (Vector3)offset;
            spawnPos.z = currentPlayerPos.z;
            return spawnPos;
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

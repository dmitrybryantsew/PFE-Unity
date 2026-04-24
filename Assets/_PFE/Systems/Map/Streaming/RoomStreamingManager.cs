using UnityEngine;
using System;
using System.Collections.Generic;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// Manages room activation, deactivation, and state management for the game world.
    /// Implements the AS3 streaming system where only one room is fully active at a time.
    /// From AS3: Land streaming system (fe/land/Land.as lines 1500-1800)
    ///
    /// System Architecture:
    /// - Current Room: Fully active, all systems running
    /// - Previous Room: Limited updates for 150 frames (buffer state)
    /// - Other Rooms: Frozen (no CPU usage, state preserved)
    /// </summary>
    public class RoomStreamingManager : MonoBehaviour
    {
        [Header("Streaming Settings")]
        [Tooltip("How many frames to keep previous room partially active (0 = deactivate immediately)")]
        [SerializeField] private int previousRoomBufferFrames = 150;

        [Tooltip("Frames away before objects reset to initial state")]
        [SerializeField] private int objectResetThreshold = 2;

        // Events
        public event Action<RoomInstance> OnRoomActivated;
        public event Action<RoomInstance> OnRoomDeactivated;
        public event Action<RoomInstance> OnRoomBuffered;

        // State tracking
        private RoomInstance currentActiveRoom;
        private RoomInstance previousRoom;
        private int previousRoomFrameCount;
        private Dictionary<RoomInstance, int> framesSinceDeactivation = new Dictionary<RoomInstance, int>();

        #region Properties

        public RoomInstance CurrentRoom => currentActiveRoom;
        public RoomInstance PreviousRoom => previousRoom;

        #endregion

        #region Initialization

        private void Update()
        {
            // Update previous room buffer
            UpdatePreviousRoomBuffer();

            // Track frames for all inactive rooms
            UpdateInactiveRoomTracking();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Activate a new room and deactivate the current one.
        /// </summary>
        /// <param name="newRoom">The room to activate</param>
        /// <param name="oldRoom">The room to deactivate (can be null)</param>
        public void ActivateRoom(RoomInstance newRoom, RoomInstance oldRoom)
        {
            if (newRoom == null)
            {
                Debug.LogWarning("RoomStreamingManager: Cannot activate null room");
                return;
            }

            // Don't reactivate the same room
            if (currentActiveRoom == newRoom)
            {
                return;
            }

            Debug.Log($"RoomStreamingManager: Activating {newRoom.id}");

            // Deactivate current room
            if (currentActiveRoom != null && currentActiveRoom != newRoom)
            {
                DeactivateRoomInternal(currentActiveRoom);
            }

            // Store previous room for buffering
            if (oldRoom != null && oldRoom != newRoom)
            {
                SetPreviousRoom(oldRoom);
            }

            // Activate new room
            ActivateRoomInternal(newRoom);
        }

        /// <summary>
        /// Deactivate a specific room immediately.
        /// </summary>
        public void ForceDeactivateRoom(RoomInstance room)
        {
            if (room == null) return;

            if (room == currentActiveRoom)
            {
                Debug.LogWarning($"RoomStreamingManager: Cannot force deactivate current room {room.id}");
                return;
            }

            DeactivateRoomInternal(room);
        }

        /// <summary>
        /// Reset a room to its initial state.
        /// </summary>
        public void ResetRoom(RoomInstance room)
        {
            if (room == null) return;

            Debug.Log($"RoomStreamingManager: Resetting room {room.id}");

            // Reset all units and objects
            // This would be implemented when unit/object systems are added

            // Reset tiles
            if (room.tiles != null)
            {
                for (int y = 0; y < WorldConstants.ROOM_HEIGHT; y++)
                {
                    for (int x = 0; x < WorldConstants.ROOM_WIDTH; x++)
                    {
                        if (room.tiles[x, y].IsDestroyed())
                        {
                            // Reset tile to initial state
                            room.tiles[x, y].Reset();
                        }
                    }
                }
            }

            room.isVisited = false;
        }

        /// <summary>
        /// Check if a room should reset its objects.
        /// Returns true if the room has been deactivated and enough frames have passed.
        /// </summary>
        public bool ShouldResetRoomObjects(RoomInstance room)
        {
            if (room == null || room == currentActiveRoom) return false;

            // If room was never activated/deactivated, don't reset
            if (!framesSinceDeactivation.ContainsKey(room)) return false;

            return framesSinceDeactivation[room] >= objectResetThreshold;
        }

        #endregion

        #region Private Methods

        private void ActivateRoomInternal(RoomInstance room)
        {
            if (room == null) return;

            room.isActive = true;
            room.isVisited = true;

            // Reset frame tracking
            if (framesSinceDeactivation.ContainsKey(room))
            {
                framesSinceDeactivation.Remove(room);
            }

            // Notify listeners
            OnRoomActivated?.Invoke(room);

            currentActiveRoom = room;
        }

        private void DeactivateRoomInternal(RoomInstance room)
        {
            if (room == null) return;

            room.isActive = false;

            // Start tracking frames since deactivation
            if (!framesSinceDeactivation.ContainsKey(room))
            {
                framesSinceDeactivation[room] = 0;
            }

            // Notify listeners
            OnRoomDeactivated?.Invoke(room);

            Debug.Log($"RoomStreamingManager: Deactivated {room.id}");
        }

        private void SetPreviousRoom(RoomInstance room)
        {
            previousRoom = room;
            previousRoomFrameCount = 0;

            OnRoomBuffered?.Invoke(room);

            Debug.Log($"RoomStreamingManager: Set {room.id} as previous room (buffer: {previousRoomBufferFrames} frames)");
        }

        private void UpdatePreviousRoomBuffer()
        {
            if (previousRoom == null) return;

            previousRoomFrameCount++;

            // Check if buffer time is over
            if (previousRoomFrameCount >= previousRoomBufferFrames)
            {
                Debug.Log($"RoomStreamingManager: Previous room {previousRoom.id} buffer expired");
                DeactivateRoomInternal(previousRoom);
                previousRoom = null;
            }
        }

        private void UpdateInactiveRoomTracking()
        {
            // Increment frame count for all inactive rooms
            var rooms = new List<RoomInstance>(framesSinceDeactivation.Keys);
            foreach (var room in rooms)
            {
                if (room != currentActiveRoom && room != previousRoom)
                {
                    framesSinceDeactivation[room]++;
                }
            }
        }

        #endregion

        #region Debug

        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            GUILayout.Label("Room Streaming Status");
            GUILayout.Label($"Current Room: {(currentActiveRoom != null ? currentActiveRoom.id : "None")}");
            GUILayout.Label($"Previous Room: {(previousRoom != null ? previousRoom.id : "None")}");
            if (previousRoom != null)
            {
                GUILayout.Label($"Buffer: {previousRoomFrameCount}/{previousRoomBufferFrames} frames");
            }
            GUILayout.Label($"Inactive Rooms Tracked: {framesSinceDeactivation.Count}");
            GUILayout.EndArea();
        }

        #endregion
    }
}

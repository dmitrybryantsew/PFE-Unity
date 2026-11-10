using UnityEngine;
using System;
using System.Collections.Generic;
using PFE.Systems.Map;
// Unity 6000 ships its own UnityEngine.EntityId. The project-wide convention for this collision is
// a using alias (see RoomPopulator.cs, IPlayerSlotContext.cs); an unqualified `using PFE.Core.Ids`
// makes `EntityId` ambiguous in any file that also imports UnityEngine.
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// Manages room activation, deactivation, and state management for the game world.
    /// From AS3: Land streaming system (fe/land/Land.as lines 1500-1800)
    ///
    /// System Architecture:
    /// - Active Rooms: every room with a connected player present (plus the presentation room)
    /// - Presentation Room: the one the camera follows; <c>CurrentRoom</c> is an alias for it
    /// - Previous Room: limited updates for 150 frames (buffer state)
    /// - Other Rooms: Frozen (no CPU usage, state preserved)
    ///
    /// <para><b>Phase 3.1 — the active set is a set.</b> Before this change the manager held exactly
    /// one <c>currentActiveRoom</c> and deactivated it before activating the next, so two players in
    /// two rooms was unrepresentable (readiness finding F-04). The physics half was already
    /// set-based — <c>PhysicsWorldService</c> keys its chain-geometry mirror by room and loops over
    /// every entry — so the fix is to let this manager raise activation events for more than one room
    /// at a time, not to build a second physics world.</para>
    ///
    /// <para><b>Behaviour-preserving for single-player.</b> <see cref="ActivateRoom"/> is the legacy
    /// one-player driver (MapBridge / RoomTransitionManager / the dev console): it still deactivates
    /// the current room, buffers the previous one, activates exactly one room and leaves
    /// <see cref="CurrentRoom"/> pointing at it. The 23 consumers of <c>CurrentRoom</c> need no edit.
    /// <see cref="ApplyRoomPresence"/> is the multiplayer entry point and is the only path that can
    /// make <see cref="ActiveRooms"/> hold more than one room. Do not mix the two drivers in one
    /// session.</para>
    ///
    /// <para><b>The decisions, not the bookkeeping, live in <see cref="ActiveRoomSetRules"/>.</b> This
    /// is a <c>MonoBehaviour</c> with <c>Update()</c>-frame state, which no offline harness can
    /// instantiate; extracting the pure rules is what makes "two scripted players in two rooms both
    /// simulate" executable without the editor. The wiring here is pinned by
    /// <c>ActiveRoomSetWiringLintTests</c>.</para>
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
        private readonly HashSet<RoomInstance> _activeRooms = new HashSet<RoomInstance>();
        private readonly Dictionary<RoomInstance, int> _connectedPlayers = new Dictionary<RoomInstance, int>();
        private RoomInstance _presentationRoom;
        private RoomInstance previousRoom;
        private int previousRoomFrameCount;
        private Dictionary<RoomInstance, int> framesSinceDeactivation = new Dictionary<RoomInstance, int>();

        /// <summary>
        /// How many times each room has become active in this session — the room-level counterpart of
        /// an entity's <c>lifecycleSequence</c> (Phase 3.3). <c>SimRoomDelta.LifecycleSequence</c> is
        /// projected from it so a delta captured during a previous occupancy of the same room is
        /// recognisable as stale rather than merely old.
        /// </summary>
        private readonly Dictionary<RoomInstance, int> _roomLifecycleSequences =
            new Dictionary<RoomInstance, int>();

        #region Properties

        /// <summary>Every room whose geometry/view is live. One entry in single-player, 2+ in co-op.</summary>
        public IReadOnlyCollection<RoomInstance> ActiveRooms => _activeRooms;

        /// <summary>The room the camera follows. Always a member of <see cref="ActiveRooms"/>.</summary>
        public RoomInstance PresentationRoom => _presentationRoom;

        /// <summary>
        /// Alias kept for the 23 existing consumers. A rename would be a silent behaviour change
        /// across the map, HUD, AI and physics layers; the set-based model needs no such rename.
        /// </summary>
        public RoomInstance CurrentRoom => _presentationRoom;

        public RoomInstance PreviousRoom => previousRoom;

        /// <summary>
        /// How many times this room has been activated this session; 0 for a room that never was.
        /// Bumped in <see cref="SetActiveInternal"/>, which is reached only for rooms that were not
        /// already active, so the counter means "occupancies", not "ticks spent active".
        /// </summary>
        public int GetRoomLifecycleSequence(RoomInstance room)
        {
            if (room == null) return 0;
            return _roomLifecycleSequences.TryGetValue(room, out int sequence) ? sequence : 0;
        }

        /// <summary>
        /// Whether this room may originate authoritative simulation state. Presence, not activity:
        /// a view-only presentation room is active but must never simulate (§10 of the readiness
        /// plan; Roadmap 05 §1.5 rule 2).
        /// </summary>
        public bool MaySimulate(RoomInstance room)
        {
            if (room == null || !_activeRooms.Contains(room)) return false;
            _connectedPlayers.TryGetValue(room, out int players);
            return ActiveRoomSetRules.MaySimulate(new ActiveRoomPresence(KeyOf(room), players));
        }

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
        /// Multiplayer entry point (Phase 3.1): reconcile the active-room set with connected-player
        /// presence. Rooms that lost their last player release their chain geometry; rooms that gained
        /// one build it. No transport, no netcode — presence is supplied by the caller.
        /// </summary>
        /// <param name="connectedPlayers">Room -> number of connected players physically present.</param>
        /// <param name="presentationRoom">The local camera's room; always kept active, view-only when empty.</param>
        public void ApplyRoomPresence(
            IReadOnlyDictionary<RoomInstance, int> connectedPlayers,
            RoomInstance presentationRoom)
        {
            if (connectedPlayers == null) throw new ArgumentNullException(nameof(connectedPlayers));

            _connectedPlayers.Clear();
            foreach (KeyValuePair<RoomInstance, int> entry in connectedPlayers)
            {
                if (entry.Key == null || entry.Value <= 0) continue;
                _connectedPlayers[entry.Key] = entry.Value;
            }

            _presentationRoom = presentationRoom;

            var records = new List<ActiveRoomPresence>(_connectedPlayers.Count);
            var byKey = new Dictionary<ulong, RoomInstance>();
            foreach (KeyValuePair<RoomInstance, int> entry in _connectedPlayers)
            {
                ulong key = KeyOf(entry.Key);
                records.Add(new ActiveRoomPresence(key, entry.Value));
                byKey[key] = entry.Key;
            }
            foreach (RoomInstance room in _activeRooms) byKey[KeyOf(room)] = room;
            if (_presentationRoom != null) byKey[KeyOf(_presentationRoom)] = _presentationRoom;

            ulong presentationKey = _presentationRoom != null
                ? KeyOf(_presentationRoom)
                : ActiveRoomSetRules.NoPresentationRoom;

            ulong[] desired = ActiveRoomSetRules.ResolveActiveRooms(records, presentationKey);

            var current = new List<ulong>(_activeRooms.Count);
            foreach (RoomInstance room in _activeRooms) current.Add(KeyOf(room));

            ActiveRoomSetRules.Diff(current, desired, out ulong[] toActivate, out ulong[] toDeactivate);

            // Deactivate first, activate second: one physics world, one chain set per room.
            foreach (ulong key in toDeactivate)
            {
                if (byKey.TryGetValue(key, out RoomInstance room)) DeactivateRoomInternal(room);
            }
            foreach (ulong key in toActivate)
            {
                if (byKey.TryGetValue(key, out RoomInstance room)) SetActiveInternal(room);
            }
        }

        /// <summary>
        /// Reset streaming tracking and deactivate current and previous rooms (used during land transitions).
        /// </summary>
        public void ResetStreaming()
        {
            if (_presentationRoom != null)
            {
                _presentationRoom.isActive = false;
                _presentationRoom = null;
            }

            if (previousRoom != null)
            {
                previousRoom.isActive = false;
                previousRoom = null;
            }

            _activeRooms.Clear();
            _connectedPlayers.Clear();
            framesSinceDeactivation.Clear();
        }

        /// <summary>
        /// Activate a new room and deactivate the current one. Single-player driver; unchanged in
        /// behaviour from before Phase 3.1.
        /// </summary>
        /// <param name="newRoom">The room to activate</param>
        /// <param name="oldRoom">The room to deactivate (can be null)</param>
        public void ActivateRoom(RoomInstance newRoom, RoomInstance oldRoom = null)
        {
            if (newRoom == null)
            {
                Debug.LogWarning("RoomStreamingManager: Cannot activate null room");
                return;
            }

            // Don't reactivate the same room. The set check keeps this a true no-op in single-player
            // while still letting the legacy driver collapse a (mistakenly mixed) multi-room session.
            if (_presentationRoom == newRoom
                && _activeRooms.Count == 1
                && _activeRooms.Contains(newRoom))
            {
                return;
            }

            Debug.Log($"RoomStreamingManager: Activating {newRoom.id}");

            // One local player is present in the room the camera follows. This is bookkeeping only:
            // no events are raised and no set is reconciled here.
            SetSinglePlayerPresence(newRoom);

            // Deactivate current room(s). In single-player this is exactly the one previous call to
            // DeactivateRoomInternal(_presentationRoom) the old code made.
            var toDrop = new List<RoomInstance>();
            foreach (RoomInstance room in _activeRooms)
            {
                if (room != newRoom) toDrop.Add(room);
            }
            foreach (RoomInstance room in toDrop)
            {
                DeactivateRoomInternal(room);
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

            if (room == _presentationRoom)
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
            if (room == null || _activeRooms.Contains(room)) return false;

            // If room was never activated/deactivated, don't reset
            if (!framesSinceDeactivation.ContainsKey(room)) return false;

            return framesSinceDeactivation[room] >= objectResetThreshold;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Stable room key for the pure rules, and — since Phase 3.3 — the same key the snapshot layer
        /// writes into <c>SimRoomDelta.RoomHash</c>. Both now come from
        /// <see cref="PFE.Sim.State.SimSnapshotProjectionRules.RoomKey"/>: two copies of a
        /// <c>"room:"</c> prefix would let a delta address a room the receiver cannot match, and the
        /// mismatch would look like an empty room rather than an error.
        /// </summary>
        private static ulong KeyOf(RoomInstance room)
        {
            return PFE.Sim.State.SimSnapshotProjectionRules.RoomKey(room != null ? room.id : null);
        }

        private void SetSinglePlayerPresence(RoomInstance room)
        {
            _connectedPlayers.Clear();
            if (room != null) _connectedPlayers[room] = 1;
        }

        private void ActivateRoomInternal(RoomInstance room)
        {
            if (room == null) return;

            SetActiveInternal(room);

            // Assign AFTER the activation event, exactly as the pre-3.1 code assigned
            // currentActiveRoom after OnRoomActivated. Subscribers still see the old CurrentRoom.
            _presentationRoom = room;
        }

        /// <summary>
        /// The set-based activation primitive. Unlike <see cref="ActivateRoomInternal"/> it does not
        /// move the camera: a second player's room becomes active without becoming the presentation
        /// room. Bookkeeping precedes the event so subscribers see the post-transition set.
        /// </summary>
        private void SetActiveInternal(RoomInstance room)
        {
            if (room == null) return;

            room.isActive = true;
            room.isVisited = true;

            // Reset frame tracking
            if (framesSinceDeactivation.ContainsKey(room))
            {
                framesSinceDeactivation.Remove(room);
            }

            _activeRooms.Add(room);

            // Phase 3.3: a room becoming active is a lifecycle event. Reached only for a room that was
            // not already active (ApplyRoomPresence diffs against the current set), so this counts
            // occupancies rather than calls.
            _roomLifecycleSequences.TryGetValue(room, out int lifecycleSequence);
            _roomLifecycleSequences[room] = PFE.Core.Ids.RuntimeEntityIdRules.NextLifecycleSequence(lifecycleSequence);

            // Notify listeners — PhysicsWorldService builds this room's chain geometry here.
            OnRoomActivated?.Invoke(room);
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

            _activeRooms.Remove(room);

            // Notify listeners — PhysicsWorldService releases this room's chain geometry here.
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
                if (!_activeRooms.Contains(room) && room != previousRoom)
                {
                    framesSinceDeactivation[room]++;
                }
            }
        }

        #endregion

        #region Debug

        private void OnGUI()
        {
            // Off unless asked for. This readout used to draw in every development build with no way
            // to switch it off, which is screen clutter you cannot remove exactly when you are trying
            // to look at the thing underneath it. Console: `col on room`.
            if (!Debug.isDebugBuild) return;
            if (!PFE.Core.DebugOverlays.IsOn(PFE.Core.DebugOverlayChannel.RoomData)) return;

            GUILayout.BeginArea(new Rect(10, 10, 300, 220));
            GUILayout.Label("Room Streaming Status");
            GUILayout.Label($"Current Room: {(_presentationRoom != null ? _presentationRoom.id : "None")}");
            GUILayout.Label($"Active Rooms: {_activeRooms.Count}");
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

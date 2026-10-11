using UnityEngine;
using System;
using System.Collections.Generic;
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
    public class RoomTransitionManager : MonoBehaviour, IRoomLayerTransition, PFE.Systems.Map.Actions.IProbRoomHost
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

        /// <summary>
        /// Cells whose opposite layer has already been reported as missing, so a player leaning on the
        /// interact key gets one explanation rather than one per press.
        /// </summary>
        private readonly HashSet<Vector3Int> _reportedMissingLayers = new HashSet<Vector3Int>();

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
                WorldCoordinates.LandRowToWorldPixelY(roomPosition.y) + 0.5f * WorldConstants.ROOM_SIZE_PIXELS.y
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

            // AS3 gotoLoc reads a prob room's neighbours from the prob land's grid, which holds only
            // that one room, so a step off a prob room's edge always refuses (Land.as:1335-1350). The
            // port has no second grid, so the same rule is the predicate below. It is not optional:
            // a prob room's coordinate is the origin, so without this a player at the edge of one
            // would step into the real land's origin cell or its neighbour.
            if (!PFE.Systems.Map.Generation.ProbTransition.AdmitsGridStep(current)) return false;

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

        /// <summary>
        /// Whether the player can step to the opposite z layer of the cell they are in.
        /// See <see cref="IRoomLayerTransition"/>.
        ///
        /// <para><b>A missing opposite layer is reported, once per cell.</b> Refusing silently is the
        /// worst available behaviour: <c>ComeInAction</c> turns this into <c>Refused</c>, the door
        /// presenter returns without opening, and the player sees a Z door that does nothing at all —
        /// indistinguishable from a door whose script was never ported. The one real cause seen so far
        /// is stale data rather than stale code: <c>room_0_0_1</c> is authored <c>z="1"</c> in
        /// <c>RoomsCamp.as:239</c>, but an asset imported before <c>AS3ToUnityConverter</c> stopped
        /// hardcoding z still carries <c>fixedPosition z: 0</c>, so it collides with <c>room_0_0</c> and
        /// <c>LandMap.AddRoom</c> drops it.</para>
        /// </summary>
        public bool CanToggleLayer
        {
            get
            {
                if (isTransitioning || landMap == null || landMap.currentRoom == null)
                {
                    return false;
                }

                // A prob room has no opposite layer to step to: AS3 reads the toggle target from the
                // prob land's grid, which holds only this room (Land.as:1343-1350). Refusing here also
                // keeps the "no room occupies (x, y, 1)" warning below from firing for a cell the prob
                // room never claimed — it would name a real cell and blame a stale import for it.
                if (!PFE.Systems.Map.Generation.ProbTransition.AdmitsGridStep(landMap.currentRoom))
                {
                    return false;
                }

                Vector3Int here = landMap.currentRoom.landPosition;
                if (landMap.GetLayerToggleTarget(here) != null)
                {
                    return true;
                }

                ReportMissingOppositeLayer(here);
                return false;
            }
        }

        /// <summary>
        /// Name the cell and the coordinate that was looked for, once per cell.
        ///
        /// <para>The coordinate is the actionable part: <c>(x, y, 0)</c> with no <c>(x, y, 1)</c> means
        /// the room exists in the source XML but did not survive the import. Without the pair printed,
        /// "the door does nothing" is a symptom with no address.</para>
        /// </summary>
        private void ReportMissingOppositeLayer(Vector3Int here)
        {
            if (!_reportedMissingLayers.Add(here))
            {
                return;
            }

            Vector3Int wanted = new Vector3Int(here.x, here.y, 1 - here.z);
            Debug.LogWarning(
                $"[RoomTransitionManager] A `comein` (Z door) was refused at land {here}: no room " +
                $"occupies {wanted}, so the toggle has nowhere to go and the door does nothing. " +
                "If the source XML authors a room there with z=\"1\", its imported asset predates the " +
                "z fix and still holds z: 0 — re-import the rooms (PFE/Map/Room Template Importer).");
        }

        /// <summary>
        /// Step to the opposite z layer of the current cell, arriving at <paramref name="worldPosition"/>.
        /// Port of AS3 <c>Land.gotoLoc(5, x, y)</c> — the branch an object reaches when its
        /// <c>allact</c> is <c>comein</c> (<c>Interact.as:1619-1621</c>).
        ///
        /// <para>Unlike <see cref="TransitionThroughEdge"/> this does not move x or y, and unlike a door
        /// it does not look up a target door: the arrival point is the trigger itself, which AS3 passes in
        /// as <c>param2</c>/<c>param3</c> (<c>Land.as:1367-1371</c>). Both layers share one Unity world
        /// origin, so the same coordinates are correct on either side — that is a property of the AS3
        /// model, not a shortcut.</para>
        ///
        /// <para>Routed through <see cref="PerformTransition"/> so streaming, re-render, the movement
        /// motor's authoritative position and the camera snap all stay on one code path; a bespoke swap
        /// here is exactly how the "bookkeeping updated, nothing else did" bug happened before.</para>
        ///
        /// <para><b>Known gap:</b> AS3 also sets <c>loc_t = 150</c> (<c>Land.as:1377</c>), a five-second
        /// lock on further transitions. This port has no equivalent — <c>isTransitioning</c> only covers
        /// the coroutine. It matters more here than for an edge crossing, because the player arrives
        /// standing on the trigger and can immediately toggle back. The lock belongs with the interaction
        /// timer rather than being invented here.</para>
        /// </summary>
        public bool ToggleLayer(GameObject player, Vector3 worldPosition)
        {
            if (isTransitioning || player == null || landMap == null)
            {
                return false;
            }

            RoomInstance current = landMap.currentRoom;
            if (current == null)
            {
                return false;
            }

            Vector3Int? target = landMap.GetLayerToggleTarget(current.landPosition);
            if (target == null)
            {
                return false;
            }

            RoomInstance targetRoom = landMap.GetRoom(target.Value);
            if (targetRoom == null)
            {
                return false;
            }

            Vector3 spawnPos = new Vector3(worldPosition.x, worldPosition.y, player.transform.position.z);
            StartCoroutine(PerformTransition(current, targetRoom, null, player, spawnPos));
            return true;
        }

        /// <summary>
        /// Apply a room that came out of a save file. Same swap as <see cref="PerformTransition"/>,
        /// but synchronous - a load has no door and no transition to animate.
        ///
        /// <para>A load is not a door transition, so it used to skip the swap entirely.
        /// <c>WorldSaveData.RestoreToMap</c> calls <c>LandMap.SwitchRoom</c>, which only flips the
        /// LandMap's OWN bookkeeping (<c>currentRoom</c> / <c>currentCoord</c>, plus
        /// <c>RoomInstance.isActive</c>). Nothing told <see cref="RoomStreamingManager"/> and
        /// nothing told the visual controller, so loading into a different room reported the right
        /// room, moved the player to the right coordinates, and then drew, ticked and collided
        /// against the room the player had been standing in before.</para>
        ///
        /// <para>Order is the same three steps, and it matters: activate in the streaming manager
        /// (which also rebuilds the low-level physics world geometry, via
        /// <c>PhysicsWorldService.OnRoomActivated</c>), mirror the bookkeeping onto the LandMap,
        /// then re-render last - <c>RoomVisualController.Initialize</c> reads the room's tile
        /// state, so it has to run after the restored tiles are in place.</para>
        /// </summary>
        /// <param name="room">Room to make current - normally <c>landMap.currentRoom</c> after a load.</param>
        /// <param name="player">Optional; used only for the camera snap. Located by type when null.</param>
        public void ApplyRestoredRoom(RoomInstance room, GameObject player = null)
        {
            if (room == null || landMap == null)
            {
                Debug.LogWarning("RoomTransitionManager: Cannot apply a null room (or no LandMap is wired)");
                return;
            }

            // Read the streaming manager's idea of the current room BEFORE activating it, so the
            // room being left is what gets recorded as previous/buffered rather than the new one.
            RoomInstance previous = streamingManager != null ? streamingManager.CurrentRoom : landMap.previousRoom;

            if (streamingManager != null)
            {
                // No-ops when the load stayed in the same room (ActivateRoom refuses to reactivate);
                // the re-render below still has to run, because the tiles may have been restored.
                streamingManager.ActivateRoom(room, previous);
            }

            landMap.SetCurrentRoom(room);

            if (visualController != null)
            {
                TileAssetDatabase db = tileDatabase != null ? tileDatabase : visualController.TileAssetDatabase;
                if (db != null)
                {
                    visualController.Initialize(room, db);
                }
                else
                {
                    Debug.LogWarning("[RoomTransitionManager] Restored room left unrendered: no TileAssetDatabase available.");
                }
            }
            else
            {
                Debug.LogWarning("[RoomTransitionManager] Restored room left unrendered: no RoomVisualController wired.");
            }

            // Snap the camera the way a door transition does. CameraFollow smooths, so without this
            // the view pans in from the room the player just left.
            GameObject target = player;
            if (target == null)
            {
                var pc = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
                if (pc != null)
                {
                    target = pc.gameObject;
                }
            }

            Camera mainCam = Camera.main;
            if (mainCam != null && target != null)
            {
                var follow = mainCam.GetComponent<PFE.Core.CameraFollow>();
                float camZ = follow != null ? follow.offset.z : -10f;
                Vector3 playerPos = target.transform.position;
                mainCam.transform.position = new Vector3(playerPos.x, playerPos.y, camZ);
            }

            Debug.Log($"[RoomTransitionManager] Applied restored room {room.id} (left: {(previous != null ? previous.id : "None")})");
        }

        // =====================================================================
        //  IProbRoomHost — the detached-room half of AS3 Land.gotoProb
        //  (Land.as:1391-1438). The pure decision is ProbTransition; this is the
        //  engine work it deliberately does not do.
        // =====================================================================

        /// <summary>
        /// The prob rooms built for the current land, keyed by prob id — AS3 <c>this.probs</c>
        /// (<c>Land.as:801-802</c>). Set once per land build, by whoever composed the context.
        /// </summary>
        private PFE.Systems.Map.Generation.IProbDoorContext _probContext;

        /// <summary>
        /// Where to put the player back — AS3 <c>retLocX/retLocY/retLocZ</c> plus <c>retX/retY</c>
        /// (<c>Land.as:1411-1424</c>). Valid only while <see cref="_savedRoom"/> is non-null.
        /// </summary>
        private PFE.Systems.Map.Generation.ProbReturnPoint _savedReturn;

        /// <summary>AS3 <c>retLoc*</c> resolved back to a room — the room the player came from.</summary>
        private RoomInstance _savedRoom;

        /// <summary>Prob ids already reported as unbuilt, so one bad door says so once.</summary>
        private readonly HashSet<string> _reportedMissingProbRooms = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Hand over the prob rooms for the land being entered — AS3 builds them with the land
        /// (<c>Land.buildProbs</c>, <c>Land.as:752-768</c>), so this is set alongside a land build and not
        /// per door.
        ///
        /// <para>Called with the same <c>ProbDoorContext</c> the world builder was given, so the room the
        /// door opens into is the room the builder built and registered — not a second lookup that could
        /// disagree. A null argument is a legitimate state (a land with no prob rooms) and turns the prob
        /// branch into a reported refusal.</para>
        /// </summary>
        public void SetProbContext(PFE.Systems.Map.Generation.IProbDoorContext context)
        {
            _probContext = context;
            _reportedMissingProbRooms.Clear();
        }

        /// <inheritdoc />
        public bool CanEnterProb
        {
            get
            {
                if (_probContext == null || landMap == null || landMap.currentRoom == null) return false;

                // A prob room is a detached room, so the player must not already be inside one: entering
                // from inside would overwrite the only record of where to come back to, and the return
                // door would then lead into the prob room the player just left.
                return !IsInProbRoom;
            }
        }

        /// <inheritdoc />
        public bool IsInProbRoom => _savedRoom != null;

        /// <inheritdoc />
        public bool TryEnterProb(string probId, Vector3 doorWorldPosition)
        {
            if (!CanEnterProb)
            {
                return false;
            }

            if (!_probContext.TryGetRoom(probId, out RoomInstance probRoom) || probRoom == null)
            {
                // AS3 ativateLoc's `this.probs[this.prob] == null` guard (Land.as:1237-1240). The cause is
                // always the same: the door was placed but its room never built — see
                // WorldBuilder.PlaceCellObjects, which has to build the exit room as well as the
                // trial/battle ones.
                if (_reportedMissingProbRooms.Add(probId))
                {
                    Debug.LogWarning(
                        $"[RoomTransitionManager] prob '{probId}' has no built room, so the door that opens " +
                        "into it refuses (AS3 ativateLoc returns false and moves nobody). The door was " +
                        "placed without its room: check that the prob id is in the land's room collection " +
                        "and that the builder registered it.");
                }

                return false;
            }

            GameObject player = ResolvePlayerObject();
            if (player == null)
            {
                Debug.LogWarning("[RoomTransitionManager] Cannot enter a prob room: no PlayerController in the scene.");
                return false;
            }

            RoomInstance from = landMap.currentRoom;

            // The save happens BEFORE the swap starts, because PerformTransition yields a frame — and
            // because AS3 writes retLoc*/retX/retY before ativateLoc (Land.as:1411-1424), so a failed
            // activation still has them and rolls the room back with them (:1431-1435).
            //
            // Units: the positions are this port's Unity world units, not AS3's source pixels. The oracle
            // passes gg.X/gg.Y (pixels) and stores them verbatim; the port has one world space and
            // converts once, at the end, in PerformTransition. Storing what the port actually has keeps a
            // round trip through PixelToUnity out of the save/restore pair, which is where a units bug
            // would hide.
            Vector3 playerPos = player.transform.position;
            _savedReturn = PFE.Systems.Map.Generation.ProbTransition.SaveReturnPoint(
                from.landPosition,
                playerPos.x,
                playerPos.y,
                doorWorldPosition.x,
                doorWorldPosition.y);
            _savedRoom = from;

            // AS3's entry branch ends with setGGToSpawnPoint() (Land.as:1426), so the player arrives at the
            // prob room's own spawn point rather than at the door they used.
            Vector3 arrival = WorldCoordinates.PixelToUnity(probRoom.GetPlayerSpawnPoint());
            arrival.z = playerPos.z;

            StartCoroutine(PerformTransition(from, probRoom, null, player, arrival));
            return true;
        }

        /// <inheritdoc />
        public bool TryReturnFromProb()
        {
            if (_savedRoom == null || landMap == null)
            {
                return false;
            }

            GameObject player = ResolvePlayerObject();
            if (player == null)
            {
                Debug.LogWarning("[RoomTransitionManager] Cannot leave a prob room: no PlayerController in the scene.");
                return false;
            }

            PFE.Systems.Map.Generation.ProbArrival arrival =
                PFE.Systems.Map.Generation.ProbTransition.PlanReturn(_savedReturn);

            Vector3 target = arrival.Kind == PFE.Systems.Map.Generation.ProbArrivalKind.SavedPlayerPosition
                ? new Vector3(arrival.PlayerX, arrival.PlayerY, player.transform.position.z)
                // The (0,0) sentinel: AS3 falls back to the room's spawn point (Land.as:1401-1408).
                : WorldCoordinates.PixelToUnity(_savedRoom.GetPlayerSpawnPoint());

            RoomInstance destination = _savedRoom;

            // Spend the save before starting the swap: the coroutine yields a frame, and leaving the save
            // live across it would let a second press re-enter the same return.
            _savedRoom = null;

            StartCoroutine(PerformTransition(landMap.currentRoom, destination, null, player, target));
            return true;
        }

        /// <summary>
        /// The player object, found by type when the caller has no reference. Same lookup
        /// <see cref="ApplyRestoredRoom"/> uses, so the two cannot drift onto different objects.
        /// </summary>
        private GameObject ResolvePlayerObject()
        {
            var pc = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            return pc != null ? pc.gameObject : null;
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
                WorldCoordinates.RoomOriginPixelX(room.landPosition.x, borderOffset),
                WorldCoordinates.RoomOriginPixelY(room.landPosition.y, borderOffset)
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
                    WorldCoordinates.LandRowToWorldPixelY(targetRoom.landPosition.y) + 0.5f * WorldConstants.ROOM_SIZE_PIXELS.y
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

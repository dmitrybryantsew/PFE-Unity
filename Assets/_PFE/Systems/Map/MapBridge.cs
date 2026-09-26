using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Streaming;

public class MapBridge : MonoBehaviour
{
    [SerializeField] private RoomVisualController _visualController;
    [SerializeField] private TileAssetDatabase _tileDatabase;

    public RoomVisualController VisualController => _visualController;

    [Header("Player Spawning")]
    [Tooltip("Tag of the player GameObject to find and spawn")]
    [SerializeField] private string _playerTag = "Player";

    [Tooltip("Offset from bottom-left of walkable tile when spawning player")]
    [SerializeField] private Vector3 _spawnOffset = new Vector3(0.5f, 0.5f, 0);

    [Header("Debug Room Override")]
    [SerializeField] private bool _useRoomOverride = false;
    [SerializeField] private RoomTemplate _overrideRoomTemplate;
    [SerializeField] private string _overrideTemplateId = "";
    [SerializeField] private string _overrideRoomType = "";

    private GameManager _gameManager;
    private RoomGenerator _roomGenerator;
    private Transform _playerTransform;
    private TileTextureLookup _tileTextureLookup;
    private MaterialRenderDatabase _materialRenderDatabase;
    private TileMaskLookup _tileMaskLookup;
    private RoomBackgroundLookup _roomBackgroundLookup;
    private PFE.Core.PfeDebugSettings _debugSettings;
    private PFE.Core.SimClock _simClock;
    private PFE.Core.SimLoop _simLoop;

    // Inject GameManager via VContainer
    //
    // Note: C# default values would NOT make a parameter optional here. VContainer's
    // ResolveOrParameter never consults ParameterInfo.HasDefaultValue — it checks only explicitly
    // supplied inject parameters, then calls Resolve(type) and throws if that fails. So every
    // parameter below must be registered, and `= null` defaults are deliberately omitted rather
    // than left in to imply an optionality the container does not honour.
    [Inject]
    public void Construct(GameManager gameManager, RoomGenerator roomGenerator, TileTextureLookup tileTextureLookup, MaterialRenderDatabase materialRenderDatabase, TileMaskLookup tileMaskLookup, RoomBackgroundLookup roomBackgroundLookup, PFE.Core.PfeDebugSettings debugSettings, PFE.Core.SimClock simClock, PFE.Core.SimLoop simLoop)
    {
        _gameManager = gameManager;
        _roomGenerator = roomGenerator;
        _tileTextureLookup = tileTextureLookup;
        _materialRenderDatabase = materialRenderDatabase;
        _tileMaskLookup = tileMaskLookup;
        _roomBackgroundLookup = roomBackgroundLookup;
        _debugSettings = debugSettings;
        _simClock = simClock;
        _simLoop = simLoop;

        if (_useRoomOverride)
            gameManager.SetSkipWorldBuild(true);
    }

    private void Start()
    {
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Start() called - waiting for world generation...");

        if (_visualController == null)
        {
            Debug.LogError("[MapBridge] RoomVisualController is not assigned! Assign it in the inspector.");
            return;
        }

        if (_tileDatabase == null)
        {
            Debug.LogError("[MapBridge] TileAssetDatabase is not assigned! Assign it in the inspector.");
            return;
        }

        _visualController.OnGotoLand += HandleGotoLand;

        // Wait for Game Manager to finish generating the world
        StartCoroutine(WaitForInitialization());
    }

    private System.Collections.IEnumerator WaitForInitialization()
    {
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Coroutine started - checking GameManager...");

        // Wait for GameManager to exist and be injected
        int waitFrames = 0;
        while (_gameManager == null && waitFrames < 300)
        {
            waitFrames++;
            yield return null;
        }

        if (_gameManager == null)
        {
            Debug.LogError("[MapBridge] GameManager was never injected! Check VContainer setup.");
            yield break;
        }

        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] GameManager found after {waitFrames} frames, waiting for initialization...");

        // Wait until GameManager says it's ready (poll every frame)
        float timeout = 30f;
        float timer = 0f;
        int checkCount = 0;

        while (timer < timeout)
        {
            try
            {
                bool initialized = _gameManager.IsInitialized();
                checkCount++;

                if (initialized)
                {
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                        Debug.Log($"[MapBridge] GameManager is initialized! (checked {checkCount} times)");
                    break;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MapBridge] Exception calling IsInitialized(): {ex.Message}");
                yield break;
            }

            timer += Time.deltaTime;
            yield return null;
        }

        if (timer >= timeout)
        {
            bool finalCheck = _gameManager.IsInitialized();
            Debug.LogError($"[MapBridge] TIMEOUT after {checkCount} checks! GameManager.IsInitialized()={finalCheck}, Instance={_gameManager.GetHashCode()}");
            yield break;
        }

        // Continue with initialization
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Proceeding with room rendering...");

        // Get the current room from logic
        var landMap = _gameManager.GetLandMap();

        if (landMap != null)
        {
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log($"[MapBridge] LandMap has {landMap.GetRoomCount()} rooms");
            if (_useRoomOverride)
            {
                TryApplyRoomOverride(landMap);
                PFE.Core.Profiling.PfeProfiler.Mark("map.roomOverride.applied");
            }

            var currentRoom = landMap.currentRoom;

            if (currentRoom != null)
            {
                if (_debugSettings?.LogMapBridgeLifecycle == true)
                {
                    Debug.Log($"[MapBridge] Current room: {currentRoom.id} at {currentRoom.landPosition}, type: {currentRoom.roomType}");
                    Debug.Log($"[MapBridge] Room dimensions: {currentRoom.width}x{currentRoom.height}, tiles array: {(currentRoom.tiles == null ? "NULL" : "initialized")}");
                }

                if (currentRoom.tiles != null)
                {
                    int nonAirTiles = 0;
                    for (int x = 0; x < currentRoom.width; x++)
                    {
                        for (int y = 0; y < currentRoom.height; y++)
                        {
                            if (currentRoom.tiles[x, y] != null && currentRoom.tiles[x, y].physicsType != TilePhysicsType.Air)
                                nonAirTiles++;
                        }
                    }
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                        Debug.Log($"[MapBridge] Room has {nonAirTiles} non-air tiles to render");
                }

                _visualController.ConfigureCompositorAssets(_tileTextureLookup, _materialRenderDatabase, _roomBackgroundLookup, _tileMaskLookup);
                if (_debugSettings?.LogMapBridgeLifecycle == true)
                    Debug.Log($"[MapBridge] Using TileAssetDatabase '{_tileDatabase.name}' with {_tileDatabase.GetCount()} overlay sprites.");

                // Initialize the visual controller with the logical room data
                using (PFE.Core.Profiling.PfeProfiler.Region("room.init.visuals", "boot: MEASURED 4007ms (2026-09-25) — still the dominant phase: tiles.createAll 2518 + backdrop.createVisuals 1453"))
                {
                    _visualController.Initialize(currentRoom, _tileDatabase);
                }

                // Spawn player at valid position
                using (PFE.Core.Profiling.PfeProfiler.Region("map.spawnPlayer", "boot: believed trivial"))
                {
                    SpawnPlayer(currentRoom);
                }

                // Setup RoomTransitionManager & RoomStreamingManager
                var transitionManager = RoomTransitionManager.Instance;
                if (transitionManager == null)
                {
                    var rtmGo = new GameObject("RoomTransitionManager");
                    transitionManager = rtmGo.AddComponent<RoomTransitionManager>();
                }
                transitionManager.SetLandMap(landMap);
                transitionManager.SetVisualController(_visualController, _tileDatabase);

                var streamingManager = FindFirstObjectByType<RoomStreamingManager>();
                if (streamingManager == null)
                {
                    var rsmGo = new GameObject("RoomStreamingManager");
                    streamingManager = rsmGo.AddComponent<RoomStreamingManager>();
                }
                transitionManager.SetStreamingManager(streamingManager);
            }
            else
            {
                Debug.LogError("[MapBridge] Logic generated no current room! Check WorldBuilder.SwitchRoom()");
            }
        }
        else
        {
            Debug.LogError("[MapBridge] LandMap is null!");
        }
    }

    private bool TryApplyRoomOverride(LandMap landMap)
    {
        if (landMap == null || _roomGenerator == null)
        {
            Debug.LogWarning("[MapBridge] Cannot apply room override: LandMap or RoomGenerator is missing.");
            return false;
        }

        RoomTemplate template = ResolveOverrideTemplate();
        if (template == null)
        {
            Debug.LogWarning("[MapBridge] Room override enabled, but no matching template was found.");
            return false;
        }

        // If the override template belongs to a multi-room collection (e.g. "Base"), build the full collection
        // so adjacent rooms (e.g. room_2_0) and door connections exist, starting the player in the override room.
        var allLoaded = _gameManager?.GetLoadedRoomTemplates();
        var worldBuilder = _gameManager?.GetWorldBuilder();
        if (worldBuilder != null && allLoaded != null && !string.IsNullOrWhiteSpace(template.sourceCollectionId))
        {
            var collectionTemplates = new List<RoomTemplate>();
            for (int i = 0; i < allLoaded.Count; i++)
            {
                var t = allLoaded[i];
                if (t != null && string.Equals(t.sourceCollectionId, template.sourceCollectionId, System.StringComparison.OrdinalIgnoreCase))
                {
                    collectionTemplates.Add(t);
                }
            }

            if (collectionTemplates.Count > 1)
            {
                bool built = worldBuilder.BuildSpecificWorld(collectionTemplates, template.fixedPosition);
                if (built && landMap.currentRoom != null)
                {
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                    {
                        Debug.Log($"[MapBridge] Debug room override loaded full collection '{template.sourceCollectionId}' ({collectionTemplates.Count} rooms) starting at {landMap.currentRoom.id}.");
                    }
                    return true;
                }
            }
        }

        Vector3Int roomPosition = template.fixedPosition;
        landMap.Initialize(roomPosition, roomPosition + Vector3Int.one);

        RoomInstance room = _roomGenerator.GenerateRoom(template, roomPosition);
        if (room == null)
        {
            Debug.LogError($"[MapBridge] Failed to generate override room from template '{template.id}'.");
            return false;
        }

        FinalizeOverrideRoom(room, template);

        if (!string.IsNullOrEmpty(template.backgroundRoomId))
        {
            RoomTemplate backgroundTemplate = FindTemplateById(template.backgroundRoomId, template);
            if (backgroundTemplate != null)
            {
                RoomInstance backgroundRoom = _roomGenerator.GenerateRoom(backgroundTemplate, roomPosition);
                FinalizeOverrideRoom(backgroundRoom, backgroundTemplate);
                backgroundRoom.roomType = "back";
                landMap.AddSpecialRoom("background", backgroundRoom, roomPosition);
                room.backgroundRoom = backgroundRoom;
                room.hasBackgroundLayer = true;
            }
            else
            {
                Debug.LogWarning($"[MapBridge] Background room template '{template.backgroundRoomId}' was not found for override room '{template.id}'.");
            }
        }

        landMap.AddRoom(room, roomPosition);
        landMap.SwitchRoom(roomPosition);
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] Debug room override loaded template '{template.id}' ({template.type}).");
        return true;
    }

    private void FinalizeOverrideRoom(RoomInstance room, RoomTemplate template)
    {
        if (room == null || template == null)
        {
            return;
        }

        if (template.specificMapOnly)
        {
            if (_debugSettings?.LogMapIntegrityDiagnostics == true)
            {
                Debug.Log(
                    $"[MapBridge] Finalizing override room '{template.GetContentId()}' as specific/authored. " +
                    "Skipping random border and door carving.");
            }

            RoomSetup.FinalizeSpecificRoom(room, template, _debugSettings);
            return;
        }

        if (_debugSettings?.LogMapIntegrityDiagnostics == true)
        {
            Debug.Log($"[MapBridge] Finalizing override room '{template.GetContentId()}' as random/procedural.");
        }

        RoomSetup.FinalizeRoom(room, template, debugSettings: _debugSettings);
    }

    private RoomTemplate ResolveOverrideTemplate()
    {
        if (_overrideRoomTemplate != null)
        {
            return _overrideRoomTemplate;
        }

        if (!string.IsNullOrWhiteSpace(_overrideTemplateId))
        {
            RoomTemplate template = FindTemplateById(_overrideTemplateId);
            if (template != null)
            {
                return template;
            }
        }

        if (!string.IsNullOrWhiteSpace(_overrideRoomType))
        {
            var templates = _gameManager.GetLoadedRoomTemplates();
            for (int i = 0; i < templates.Count; i++)
            {
                RoomTemplate template = templates[i];
                if (template != null && string.Equals(template.type, _overrideRoomType, System.StringComparison.Ordinal))
                {
                    return template;
                }
            }
        }

        return null;
    }

    private RoomTemplate FindTemplateById(string templateId, RoomTemplate context = null)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        var templates = _gameManager.GetLoadedRoomTemplates();
        for (int i = 0; i < templates.Count; i++)
        {
            RoomTemplate template = templates[i];
            if (template != null && string.Equals(template.GetContentId(), templateId, System.StringComparison.Ordinal))
            {
                return template;
            }
        }

        if (context != null && !string.IsNullOrWhiteSpace(context.sourceCollectionId))
        {
            for (int i = 0; i < templates.Count; i++)
            {
                RoomTemplate template = templates[i];
                if (template != null &&
                    string.Equals(template.id, templateId, System.StringComparison.Ordinal) &&
                    string.Equals(template.sourceCollectionId, context.sourceCollectionId, System.StringComparison.Ordinal))
                {
                    return template;
                }
            }
        }

        for (int i = 0; i < templates.Count; i++)
        {
            RoomTemplate template = templates[i];
            if (template != null && string.Equals(template.id, templateId, System.StringComparison.Ordinal))
            {
                return template;
            }
        }

        return null;
    }

    /// <summary>
    /// Spawn player at a valid walkable position in the room.
    /// </summary>
    private void SpawnPlayer(RoomInstance room)
    {
        GameObject playerObj = GameObject.FindWithTag(_playerTag);
        if (playerObj == null)
        {
            var playerController = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (playerController != null)
                playerObj = playerController.gameObject;
        }

        if (playerObj == null)
        {
            Debug.LogWarning("[MapBridge] No player found in scene!");
            return;
        }

        _playerTransform = playerObj.transform;

        // NEW: Use RoomSetup for spawn position (pixel-accurate, finds air-above-ground)
        Vector3 spawnPosition = RoomSetup.FindPlayerSpawnUnity(room);
        _playerTransform.position = spawnPosition;
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] Player spawned at {spawnPosition}");

        // NEW: Connect TilePhysicsController to current room
        var tilePhysics = playerObj.GetComponent<PFE.Systems.Physics.TilePhysicsController>();
        if (tilePhysics != null)
        {
            tilePhysics.SetRoom(room);
            tilePhysics.SetPixelPosition(
                spawnPosition.x * 100f,
                spawnPosition.y * 100f);
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log("[MapBridge] TilePhysicsController connected to room");

            // P1: hand the motor to the fixed-step simulation. Opt-in — with the flag off the motor
            // stays on the legacy FixedUpdate path, byte-identical to the pre-P1 behaviour.
            if (_debugSettings != null && _debugSettings.SimTickMotor)
            {
                // AttachSimulation owns the null guard (it warns and stays legacy), so the clock is
                // only dereferenced here once we know it arrived.
                tilePhysics.AttachSimulation(_simClock, _simLoop);
                if (_simClock != null)
                {
                    // Deliberately not gated on LogMapBridgeLifecycle: this confirms an opt-in
                    // behaviour change, and silence would be ambiguous with "the flag did nothing".
                    Debug.Log(
                        "[MapBridge] Motor is sim-driven at " + _simClock.TicksPerSecond +
                        " Hz (tick fix ON; step scale " + _simClock.StepScale.ToString("0.###") + ")");
                }
            }
        }

        SetupCameraFollow();
    }
    /// <summary>
    /// Find a valid walkable tile to spawn the player.
    /// Prioritizes floor/platform tiles near the center of the room.
    /// </summary>
    private Vector3 FindValidSpawnPosition(RoomInstance room)
    {
        if (room?.tiles == null)
        {
            Debug.LogWarning("[MapBridge] Room tiles not available, spawning at origin");
            return Vector3.zero;
        }

        // First try: Find ground/platform tiles near center
        int centerX = room.width / 2;
        int centerY = room.height / 2;

        // Search outward from center for walkable tile
        for (int radius = 0; radius < Mathf.Max(room.width, room.height); radius++)
        {
            for (int x = Mathf.Max(0, centerX - radius); x <= Mathf.Min(room.width - 1, centerX + radius); x++)
            {
                for (int y = Mathf.Max(0, centerY - radius); y <= Mathf.Min(room.height - 1, centerY + radius); y++)
                {
                    var tile = room.tiles[x, y];
                    if (tile != null && IsWalkable(tile))
                    {
                        // Found walkable tile, spawn above it
                        Vector3 worldPos = new Vector3(
                            room.landPosition.x + x + _spawnOffset.x,
                            room.landPosition.y + y + _spawnOffset.y,
                            0
                        );
                        if (_debugSettings?.LogMapBridgeLifecycle == true)
                            Debug.Log($"[MapBridge] Found spawn position at tile ({x}, {y}), world pos: {worldPos}");
                        return worldPos;
                    }
                }
            }
        }

        // Fallback: Center of room
        Vector3 fallbackPos = new Vector3(
            room.landPosition.x + centerX,
            room.landPosition.y + centerY,
            0
        );
        Debug.LogWarning($"[MapBridge] No walkable tile found, spawning at room center: {fallbackPos}");
        return fallbackPos;
    }

    /// <summary>
    /// Check if a tile is walkable (floor, platform, or stair).
    /// </summary>
    private bool IsWalkable(TileData tile)
    {
        return tile.physicsType == TilePhysicsType.Wall ||
               tile.physicsType == TilePhysicsType.Platform ||
               tile.physicsType == TilePhysicsType.Stair;
    }

    /// <summary>
    /// Setup camera to follow the player.
    /// </summary>
    private void SetupCameraFollow()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("[MapBridge] No main camera found!");
            return;
        }

        // Add CameraFollow component via reflection to avoid compile issues
        var followType = System.Type.GetType("PFE.Core.CameraFollow, Assembly-CSharp");
        if (followType == null)
        {
            // Fallback: just position camera at player
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log("[MapBridge] CameraFollow not yet compiled, positioning camera directly");
            mainCamera.transform.position = new Vector3(
                _playerTransform.position.x,
                _playerTransform.position.y,
                -10
            );
            return;
        }

        var follow = mainCamera.GetComponent(followType);
        if (follow == null)
        {
            follow = mainCamera.gameObject.AddComponent(followType);
        }

        // Set target via reflection
        var setTargetMethod = followType.GetMethod("SetTarget");
        setTargetMethod?.Invoke(follow, new object[] { _playerTransform });

        // Set properties
        followType.GetField("smoothSpeed")?.SetValue(follow, 5f);
        followType.GetField("offset")?.SetValue(follow, new Vector3(0, 0, -10));

        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Camera follow setup complete");
    }

    private void OnDestroy()
    {
        if (_visualController != null)
        {
            _visualController.OnGotoLand -= HandleGotoLand;
        }
    }

    private void HandleGotoLand(string targetLand)
    {
        Debug.Log($"[MapBridge] HandleGotoLand triggered for: '{targetLand}'");
        StartCoroutine(PerformLandTransition(targetLand));
    }

    public System.Collections.IEnumerator PerformLandTransition(string targetLand, Vector3Int? targetPosition = null)
    {
        Debug.Log($"[MapBridge] Starting land transition to '{targetLand}'...");

        // Disable player controller during transition
        GameObject playerObj = GameObject.FindWithTag(_playerTag);
        if (playerObj == null)
        {
            var pc = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (pc != null) playerObj = pc.gameObject;
        }

        var playerController = playerObj != null ? playerObj.GetComponent<PFE.Entities.Player.PlayerController>() : null;
        if (playerController != null)
        {
            playerController.enabled = false;
        }

        // Reset room streaming & ongoing room transitions
        var rtm = RoomTransitionManager.Instance;
        if (rtm != null)
        {
            rtm.StopAllCoroutines();
        }

        var rsm = FindFirstObjectByType<RoomStreamingManager>();
        if (rsm != null)
        {
            rsm.ResetStreaming();
        }

        // Yield a frame so physics callbacks finish cleanly
        yield return null;

        // Clear visuals
        _visualController.ClearVisuals();

        // Get LandMap and clear
        var landMap = _gameManager.GetLandMap();
        if (landMap != null)
        {
            landMap.Clear();
        }

        // Resolve collection name
        string collectionName = ResolveCollectionName(targetLand);
        var templates = _gameManager.GetTemplatesForCollection(collectionName);
        if (templates == null || templates.Count == 0)
        {
            Debug.LogError($"[MapBridge] No templates found for collection '{collectionName}' (targetLand: '{targetLand}')!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        // Determine starting room position (in Surf, entry is room_0_1 at (0, 1, 0))
        Vector3Int startPos = targetPosition ?? ResolveEntrancePosition(templates, collectionName);

        var worldBuilder = _gameManager.GetWorldBuilder();
        bool built = worldBuilder.BuildSpecificWorld(templates, startPos);
        if (!built)
        {
            Debug.LogError($"[MapBridge] Failed to build world for collection '{collectionName}'!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        yield return null;

        var currentRoom = landMap.currentRoom;
        if (currentRoom != null)
        {
            _visualController.Initialize(currentRoom, _tileDatabase);
            SpawnPlayer(currentRoom);

            if (rtm != null)
            {
                rtm.SetLandMap(landMap);
                rtm.SetVisualController(_visualController, _tileDatabase);
            }

            if (rsm != null)
            {
                rsm.ActivateRoom(currentRoom);
            }
        }

        if (playerController != null)
        {
            playerController.enabled = true;
        }

        Debug.Log($"[MapBridge] Successfully transitioned to land '{targetLand}' ({collectionName}), current room: {currentRoom?.id}");
    }

    private static string ResolveCollectionName(string targetLand)
    {
        if (string.IsNullOrWhiteSpace(targetLand)) return "Base";
        string lower = targetLand.Trim().ToLowerInvariant();
        return lower switch
        {
            "surf" => "Surf",
            "base" or "begin" => "Base",
            "camp" => "Camp",
            "canter" => "Canter",
            "encl" => "Encl",
            "mane" => "Mane",
            "mbase" => "Mbase",
            "pi" => "Pi",
            "plant" => "Plant",
            "prob" => "Prob",
            "sewer" => "Sewer",
            "stable" => "Stable",
            _ => char.ToUpperInvariant(targetLand[0]) + targetLand.Substring(1)
        };
    }

    private static Vector3Int ResolveEntrancePosition(List<RoomTemplate> templates, string collectionName)
    {
        if (string.Equals(collectionName, "Surf", StringComparison.OrdinalIgnoreCase))
        {
            // The outside bunker exit room in rooms_surf is room_0_1 at (0, 1, 0)
            return new Vector3Int(0, 1, 0);
        }

        // Look for beg0 or first spawn point
        var entry = templates.Find(t => t.type == "beg0")
                 ?? templates.Find(t => t.spawnPoints != null && t.spawnPoints.Exists(sp => sp.type == 0))
                 ?? templates.Find(t => t.fixedPosition.x >= 0);

        return entry != null ? entry.fixedPosition : Vector3Int.zero;
    }
}

using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

public class MapBridge : MonoBehaviour
{
    [SerializeField] private RoomVisualController _visualController;
    [SerializeField] private TileAssetDatabase _tileDatabase;

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

    // Inject GameManager via VContainer
    [Inject]
    public void Construct(GameManager gameManager, RoomGenerator roomGenerator, TileTextureLookup tileTextureLookup, MaterialRenderDatabase materialRenderDatabase, TileMaskLookup tileMaskLookup, RoomBackgroundLookup roomBackgroundLookup, PFE.Core.PfeDebugSettings debugSettings)
    {
        _gameManager = gameManager;
        _roomGenerator = roomGenerator;
        _tileTextureLookup = tileTextureLookup;
        _materialRenderDatabase = materialRenderDatabase;
        _tileMaskLookup = tileMaskLookup;
        _roomBackgroundLookup = roomBackgroundLookup;
        _debugSettings = debugSettings;

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
                _visualController.Initialize(currentRoom, _tileDatabase);

                // Spawn player at valid position
                SpawnPlayer(currentRoom);
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

        Vector3Int roomPosition = Vector3Int.zero;
        landMap.Initialize(roomPosition, roomPosition + Vector3Int.one);

        RoomInstance room = _roomGenerator.GenerateRoom(template, roomPosition);
        if (room == null)
        {
            Debug.LogError($"[MapBridge] Failed to generate override room from template '{template.id}'.");
            return false;
        }

        RoomSetup.FinalizeRoom(room, template);

        if (!string.IsNullOrEmpty(template.backgroundRoomId))
        {
            RoomTemplate backgroundTemplate = FindTemplateById(template.backgroundRoomId);
            if (backgroundTemplate != null)
            {
                RoomInstance backgroundRoom = _roomGenerator.GenerateRoom(backgroundTemplate, roomPosition);
                RoomSetup.FinalizeRoom(backgroundRoom, backgroundTemplate);
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

    private RoomTemplate FindTemplateById(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        var templates = _gameManager.GetLoadedRoomTemplates();
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
}

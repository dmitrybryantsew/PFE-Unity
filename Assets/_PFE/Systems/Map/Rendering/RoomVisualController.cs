using System.Text;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Entities.Player;
using PFE.Core;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// MonoBehaviour controller for visual representation of a room.
    /// Manages tile rendering and GameObject lifecycle for a single room.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomVisualController : MonoBehaviour
    {
        private const int BackgroundTileSortingOffset = -3000;
        private static readonly Vector3 BackgroundTileLayerLocalPosition = new Vector3(0f, 0f, -1f);

        [Header("Room Data")]
        [SerializeField] private RoomInstance roomInstance = null;

        [Header("Rendering")]
        [SerializeField] private TileAssetDatabase tileAssetDatabase = null;
        [SerializeField] private TileFormDatabase tileFormDatabase = null;
        [SerializeField] private TileTextureLookup tileTextureLookup = null;
        [SerializeField] private MaterialRenderDatabase materialRenderDb = null;
        [SerializeField] private TileMaskLookup tileMaskLookup = null;
        [SerializeField] private RoomBackgroundLookup roomBackgroundLookup = null;
        [SerializeField] private RoomBackdropSettingsLookup roomBackdropSettingsLookup = null;
        [SerializeField] private PfeDebugSettings debugSettings = null;
        [Tooltip("Scales room backwall texture sampling. Values above 1 make the texture appear larger; values below 1 make it tile more densely.")]
        [SerializeField] private Vector2 backdropTextureScale = Vector2.one;
        [Tooltip("Offsets room backwall texture sampling in texture-repeat units. 1 moves by one full texture repeat.")]
        [SerializeField] private Vector2 backdropTextureOffset = Vector2.zero;
        [SerializeField] private bool flipBackdropTextureX = false;
        [SerializeField] private bool flipBackdropTextureY = true;
        [SerializeField] private Color backdropTint = Color.white;
        [SerializeField] private float backdropBrightness = 1f;
        [Tooltip("Optional sharpening applied to the generated backwall texture. Useful for matching the crisper Flash presentation.")]
        [Range(0f, 2f)]
        [SerializeField] private float backdropSharpenStrength = 0f;
        [SerializeField] private Color backgroundAssetTint = Color.white;
        [SerializeField] private float backgroundAssetBrightness = 1f;
        [Tooltip("Debug toggle to isolate whether backdrop shadow baking is causing the room to look too dark or blurry.")]
        [SerializeField] private bool disableBackdropShadowBakeForDebug = false;
        [SerializeField] private Transform tileParent = null;
        [SerializeField] private Transform backgroundParent = null;
        [SerializeField] private Transform backgroundTileParent = null;
        [SerializeField] private Transform backgroundObjectParent = null;
        [SerializeField] private Transform backgroundPhysicalObjectParent = null;
        [SerializeField] private Transform lightingParent = null;
        [SerializeField] private Transform fogOfWarParent = null;

        [Header("Editor Preview")]
        [SerializeField] private RoomTemplate previewTemplate = null;
        [SerializeField] private Vector3Int previewLandPosition = Vector3Int.zero;

        [Header("Debug")]
        [SerializeField] private bool showContourDebugOverlay = false;
        [SerializeField] private bool useLongDebugExport = false;

        [Header("State")]
        [SerializeField] private bool isInitialized = false;
        [SerializeField] private bool isVisible = true;

        private TileVisualManager tileVisualManager;
        private TileVisualManager backgroundTileVisualManager;
        private RoomBackdropRenderer roomBackdropRenderer;
        private RoomObjectVisualManager roomObjectVisualManager;
        private Transform visibilityRevealTargetTransform;

        /// <summary>
        /// Get the room instance this controller renders.
        /// </summary>
        public RoomInstance RoomInstance => roomInstance;

        /// <summary>
        /// Is this room currently visible?
        /// </summary>
        public bool IsVisible => isVisible;
        public bool ShowContourDebugOverlay
        {
            get => showContourDebugOverlay;
            set => showContourDebugOverlay = value;
        }

        public bool UseLongDebugExport
        {
            get => useLongDebugExport;
            set => useLongDebugExport = value;
        }

        public void ConfigureCompositorAssets(TileTextureLookup textureLookup, MaterialRenderDatabase materialDatabase, RoomBackgroundLookup backgroundLookup = null, TileMaskLookup maskLookup = null)
        {
            tileTextureLookup = textureLookup;
            materialRenderDb = materialDatabase;
            if (backgroundLookup != null)
            {
                roomBackgroundLookup = backgroundLookup;
            }
            if (maskLookup != null)
            {
                tileMaskLookup = maskLookup;
            }
        }

        /// <summary>
        /// Initialize the room visual controller.
        /// </summary>
        public void Initialize(RoomInstance room, TileAssetDatabase assetDatabase)
        {
            debugSettings = ResolveDebugSettings();
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Initializing room {room?.id}...");
            }
            
            roomInstance = room;
            tileAssetDatabase = ResolveTileAssetDatabase(assetDatabase);
            visibilityRevealTargetTransform = null;

            if (room == null)
            {
                Debug.LogError("[RoomVisualController] Cannot initialize with null room!");
                return;
            }
            
            if (tileAssetDatabase == null)
            {
                Debug.LogError("[RoomVisualController] Cannot initialize with null asset database!");
                return;
            }

            // Create parent transforms
            if (tileParent == null)
            {
                GameObject tileParentObj = new GameObject("Tiles");
                tileParentObj.transform.SetParent(transform);
                tileParent = tileParentObj.transform;
            }

            if (backgroundParent == null)
            {
                GameObject bgParentObj = new GameObject("Background");
                bgParentObj.transform.SetParent(transform);
                backgroundParent = bgParentObj.transform;
            }

            EnsureBackgroundTileParent();
            EnsureOverlayParents();
            EnsureObjectParents();
            CleanupLegacyOverlayChildren();

            // Create tile visual managers
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Creating TileVisualManager for {room.width}x{room.height} room");
            }
            TileCompositor compositor = null;
            if (tileTextureLookup != null && materialRenderDb != null)
            {
                compositor = new TileCompositor(tileTextureLookup, materialRenderDb, tileMaskLookup);
            }
            else
            {
                Debug.LogWarning("[RoomVisualController] TileCompositor disabled: TileTextureLookup or MaterialRenderDatabase is missing.");
            }

            tileVisualManager = new TileVisualManager(
                room,
                tileAssetDatabase,
                tileParent,
                compositor,
                debugSettings,
                0,
                Color.white,
                ResolveBackgroundTileTint(room),
                MapSortingLayers.MainTiles,
                MapSortingLayers.Foreground);

            // Create background layer if exists
            if (room.hasBackgroundLayer && room.backgroundRoom != null)
            {
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log("[RoomVisualController] Creating background layer...");
                }
                backgroundTileVisualManager = new TileVisualManager(
                    room.backgroundRoom,
                    tileAssetDatabase,
                    backgroundTileParent,
                    compositor,
                    debugSettings,
                    BackgroundTileSortingOffset,
                    ResolveBackgroundTileTint(room),
                    ResolveBackgroundTileTint(room),
                    MapSortingLayers.BackgroundTiles,
                    MapSortingLayers.BackgroundTiles);
            }
            else if (room.hasBackgroundLayer)
            {
                Debug.LogWarning($"[RoomVisualController] Room '{room.id}' expects a background layer, but no background room instance is attached.");
            }

            ApplyStoredRoomTintSettingsIfAvailable(room);
            ApplyStoredBackdropSettingsIfAvailable(room);
            roomBackdropRenderer = new RoomBackdropRenderer(
                room,
                tileTextureLookup,
                roomBackgroundLookup,
                roomBackdropSettingsLookup,
                GetCurrentRoomStorageKey(),
                backgroundParent,
                fogOfWarParent,
                backdropTextureScale,
                backdropTextureOffset,
                flipBackdropTextureX,
                flipBackdropTextureY,
                GetCurrentBackdropTintSettings(),
                backdropSharpenStrength,
                GetCurrentBackgroundAssetTintSettings(),
                disableBackdropShadowBakeForDebug);
            KonturCalculator.CalculateAll(room);
            // Create all tiles
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log("[RoomVisualController] Creating main layer tiles...");
            }
            tileVisualManager.CreateAllTiles();
            int mainTiles = tileVisualManager.GetTileCount();
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Created {mainTiles} main layer tiles");
            }

            if (backgroundTileVisualManager != null)
            {
                backgroundTileVisualManager.CreateAllTiles();
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log($"[RoomVisualController] Created {backgroundTileVisualManager.GetTileCount()} background tiles");
                }
            }

            roomBackdropRenderer.CreateVisuals();
            roomObjectVisualManager = new RoomObjectVisualManager(room, backgroundObjectParent, backgroundPhysicalObjectParent);
            roomObjectVisualManager.RefreshAll();

            // Set world position
            Vector3 worldPos = GetRoomWorldPosition();
            transform.position = worldPos;
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Room positioned at world: {worldPos}");
            }

            isInitialized = true;
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Room {room.id} initialized successfully with {mainTiles} tiles!");
            }
        }

        private TileAssetDatabase ResolveTileAssetDatabase(TileAssetDatabase requestedDatabase)
        {
            if (HasOverlaySprites(requestedDatabase))
            {
                return requestedDatabase;
            }

            if (HasOverlaySprites(tileAssetDatabase))
            {
                if (requestedDatabase != null && requestedDatabase != tileAssetDatabase)
                {
                    Debug.LogWarning(
                        $"[RoomVisualController] Requested TileAssetDatabase '{requestedDatabase.name}' has no tileFront sprites. " +
                        $"Falling back to serialized database '{tileAssetDatabase.name}'.");
                }

                return tileAssetDatabase;
            }

            return requestedDatabase ?? tileAssetDatabase;
        }

        private static bool HasOverlaySprites(TileAssetDatabase database)
        {
            return database != null && database.GetCount() > 0;
        }

        /// <summary>
        /// Get world position for this room.
        /// Accounts for expanded room dimensions (border adds 1 tile on each side).
        /// </summary>
        private Vector3 GetRoomWorldPosition()
        {
            if (roomInstance == null)
                return Vector3.zero;

            // Calculate base position from land grid (using standard room size)
            // Then offset by -1 tile to account for border expansion
            int borderOffset = (roomInstance.width - WorldConstants.ROOM_WIDTH) / 2;
            
            Vector2 roomPixelPos = new Vector2(
                roomInstance.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE - borderOffset * WorldConstants.TILE_SIZE,
                roomInstance.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE - borderOffset * WorldConstants.TILE_SIZE
            );

            return WorldCoordinates.PixelToUnity(roomPixelPos);
        }

        /// <summary>
        /// Update visual state.
        /// </summary>
        public void UpdateVisuals()
        {
            if (!isInitialized)
                return;

            tileVisualManager?.UpdateAllTiles();
            backgroundTileVisualManager?.UpdateAllTiles();
            roomObjectVisualManager?.RefreshAll();
        }

        private void LateUpdate()
        {
            if (!isInitialized)
            {
                return;
            }

            roomObjectVisualManager?.UpdateVisuals(Time.deltaTime);

            if (roomBackdropRenderer == null)
            {
                return;
            }

            if (visibilityRevealTargetTransform == null)
            {
                PlayerController player = FindFirstObjectByType<PlayerController>();
                if (player != null)
                {
                    visibilityRevealTargetTransform = player.transform;
                }
            }

            roomBackdropRenderer.UpdateVisibilityMask(visibilityRevealTargetTransform != null
                ? (Vector3?)visibilityRevealTargetTransform.position
                : null);
        }

        /// <summary>
        /// Set visibility of this room.
        /// </summary>
        public void SetVisible(bool visible)
        {
            isVisible = visible;
            gameObject.SetActive(visible);
        }

        /// <summary>
        /// Update a specific tile.
        /// </summary>
        public void UpdateTile(Vector2Int coord)
        {
            if (!isInitialized || tileVisualManager == null)
                return;

            tileVisualManager.UpdateTile(coord);
        }

        public void ApplyEditedPreviewTile(TileData editedTile)
        {
            if (editedTile == null || roomInstance == null)
            {
                return;
            }

            TileData targetTile = roomInstance.GetTileAtCoord(editedTile.gridPosition);
            if (targetTile == null)
            {
                return;
            }

            CopyEditedTileFields(editedTile, targetTile);
            UpdateTile(editedTile.gridPosition);
        }

        /// <summary>
        /// Destroy a specific tile.
        /// </summary>
        public void DestroyTile(Vector2Int coord)
        {
            if (!isInitialized || tileVisualManager == null)
                return;

            tileVisualManager.DestroyTile(coord);
        }

        /// <summary>
        /// Flash a tile (for damage).
        /// </summary>
        public void FlashTile(Vector2Int coord, Color color, float duration = 0.1f)
        {
            if (!isInitialized || tileVisualManager == null)
                return;

            tileVisualManager.FlashTile(coord, color, duration);
        }

        /// <summary>
        /// Activate this room (show and enable).
        /// </summary>
        public void Activate()
        {
            SetVisible(true);
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Activated room at {roomInstance?.landPosition}");
            }
        }

        /// <summary>
        /// Deactivate this room (hide but don't destroy).
        /// </summary>
        public void Deactivate()
        {
            SetVisible(false);
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Deactivated room at {roomInstance?.landPosition}");
            }
        }

        /// <summary>
        /// Destroy this room's visuals.
        /// </summary>
        public void DestroyRoom()
        {
            tileVisualManager?.DestroyAllTiles();
            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();
            roomObjectVisualManager = null;
            visibilityRevealTargetTransform = null;

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        /// Refresh sprites (useful after changing asset database).
        /// </summary>
        public void RefreshSprites()
        {
            tileVisualManager?.RefreshSprites();
            backgroundTileVisualManager?.RefreshSprites();
            roomObjectVisualManager?.RefreshAll();
            RefreshBackdropVisuals();
        }

        /// <summary>
        /// Get tile renderer for a specific coordinate.
        /// </summary>
        public TileRenderer GetTileRenderer(Vector2Int coord)
        {
            if (!isInitialized || tileVisualManager == null)
                return null;

            return tileVisualManager.GetTileRenderer(coord);
        }

        /// <summary>
        /// Get total number of rendered tiles.
        /// </summary>
        public int GetTileCount()
        {
            if (!isInitialized || tileVisualManager == null)
                return 0;

            return tileVisualManager.GetTileCount();
        }

        public string BuildRoomDebugString(bool longFormat)
        {
            if (roomInstance == null || roomInstance.tiles == null)
            {
                return string.Empty;
            }

            return longFormat
                ? BuildLongDebugString(roomInstance)
                : BuildShortDebugString(roomInstance);
        }

        private void OnDestroy()
        {
            tileVisualManager?.DestroyAllTiles();
            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();
            roomObjectVisualManager = null;
            visibilityRevealTargetTransform = null;
        }

        private void OnValidate()
        {
            ClampBackdropSettings();

            #if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= RefreshBackdropPreviewDelayed;
            UnityEditor.EditorApplication.delayCall += RefreshBackdropPreviewDelayed;
            #endif
        }

        private void RefreshBackdropVisuals()
        {
            if (roomInstance == null || backgroundParent == null)
            {
                return;
            }

            

            EnsureBackgroundTileParent();
            EnsureOverlayParents();
            EnsureObjectParents();
            CleanupLegacyOverlayChildren();
            ApplyBackgroundLayerRendering(roomInstance);
            roomBackdropRenderer?.DestroyVisuals();
            visibilityRevealTargetTransform = null;
            roomBackdropRenderer = new RoomBackdropRenderer(
                roomInstance,
                tileTextureLookup,
                roomBackgroundLookup,
                roomBackdropSettingsLookup,
                GetCurrentRoomStorageKey(),
                backgroundParent,
                fogOfWarParent,
                backdropTextureScale,
                backdropTextureOffset,
                flipBackdropTextureX,
                flipBackdropTextureY,
                GetCurrentBackdropTintSettings(),
                backdropSharpenStrength,
                GetCurrentBackgroundAssetTintSettings(),
                disableBackdropShadowBakeForDebug);
            roomBackdropRenderer.CreateVisuals();
        }

        private void ApplyBackgroundLayerRendering(RoomInstance room)
        {
            if (backgroundTileVisualManager == null)
            {
                return;
            }

            EnsureBackgroundTileParent();
            backgroundTileVisualManager.SetSortingOrderOffset(BackgroundTileSortingOffset);
            backgroundTileVisualManager.SetSortingLayers(MapSortingLayers.BackgroundTiles, MapSortingLayers.BackgroundTiles);
            backgroundTileVisualManager.SetTintColor(ResolveBackgroundTileTint(room));
            backgroundTileVisualManager.SetSecondaryTintColor(ResolveBackgroundTileTint(room));
        }

        private void EnsureBackgroundTileParent()
        {
            if (backgroundParent == null)
            {
                return;
            }

            if (backgroundTileParent == null)
            {
                Transform existing = backgroundParent.Find("BackgroundTiles");
                if (existing != null)
                {
                    backgroundTileParent = existing;
                }
                else
                {
                    GameObject tileLayerObject = new GameObject("BackgroundTiles");
                    tileLayerObject.transform.SetParent(backgroundParent, false);
                    backgroundTileParent = tileLayerObject.transform;
                }
            }

            backgroundTileParent.localPosition = BackgroundTileLayerLocalPosition;
            backgroundTileParent.localRotation = Quaternion.identity;
            backgroundTileParent.localScale = Vector3.one;
        }

        private void EnsureOverlayParents()
        {
            lightingParent = EnsureNamedChild(transform, lightingParent, "Lighting");
            fogOfWarParent = EnsureNamedChild(transform, fogOfWarParent, "FogOfWar");
        }

        private void EnsureObjectParents()
        {
            backgroundObjectParent = EnsureNamedChild(transform, backgroundObjectParent, "BackgroundObjects");
            backgroundPhysicalObjectParent = EnsureNamedChild(transform, backgroundPhysicalObjectParent, "BackgroundPhysicalObjects");
        }

        private static Transform EnsureNamedChild(Transform parent, Transform current, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            if (current == null)
            {
                Transform existing = parent.Find(childName);
                if (existing != null)
                {
                    current = existing;
                }
                else
                {
                    GameObject childObject = new GameObject(childName);
                    childObject.transform.SetParent(parent, false);
                    current = childObject.transform;
                }
            }

            current.localPosition = Vector3.zero;
            current.localRotation = Quaternion.identity;
            current.localScale = Vector3.one;
            return current;
        }

        private void CleanupLegacyOverlayChildren()
        {
            CleanupNamedChildren(backgroundParent, "LightingOverlay", "VisibilityMask");
            CleanupNamedChildren(lightingParent, "LightingOverlay", "VisibilityMask");
            CleanupNamedChildren(fogOfWarParent, "LightingOverlay", "VisibilityMask");
        }

        private static void CleanupNamedChildren(Transform parent, params string[] childNames)
        {
            if (parent == null || childNames == null || childNames.Length == 0)
            {
                return;
            }

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                for (int j = 0; j < childNames.Length; j++)
                {
                    if (!string.Equals(child.name, childNames[j], System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Application.isPlaying)
                    {
                        Destroy(child.gameObject);
                    }
                    else
                    {
                        DestroyImmediate(child.gameObject);
                    }

                    break;
                }
            }
        }

        public string GetCurrentRoomStorageKey()
        {
            if (!string.IsNullOrWhiteSpace(roomInstance?.templateId))
            {
                return roomInstance.templateId;
            }

            return roomInstance?.id ?? string.Empty;
        }

        public RoomBackdropSettingsLookup.BackdropSettings GetCurrentBackdropSettings()
        {
            return new RoomBackdropSettingsLookup.BackdropSettings
            {
                textureScale = backdropTextureScale,
                textureOffset = backdropTextureOffset,
                flipX = flipBackdropTextureX,
                flipY = flipBackdropTextureY,
                overrideGlobalTint = true,
                tint = GetCurrentBackdropTintSettings()
            };
        }

        public RoomBackdropSettingsLookup.TintSettings GetCurrentBackdropTintSettings()
        {
            return new RoomBackdropSettingsLookup.TintSettings
            {
                tint = backdropTint,
                brightness = backdropBrightness
            };
        }

        public RoomBackdropSettingsLookup.TintSettings GetCurrentBackgroundAssetTintSettings()
        {
            return new RoomBackdropSettingsLookup.TintSettings
            {
                tint = backgroundAssetTint,
                brightness = backgroundAssetBrightness
            };
        }

        public void SetBackdropSettingsLookup(RoomBackdropSettingsLookup lookup)
        {
            roomBackdropSettingsLookup = lookup;
        }

        private void ApplyStoredBackdropSettingsIfAvailable(RoomInstance room)
        {
            if (room?.environment == null || string.IsNullOrWhiteSpace(room.environment.backgroundWall))
            {
                return;
            }

            string roomKey = ResolveRoomStorageKey(room);
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            RoomBackdropSettingsLookup lookup = ResolveBackdropSettingsLookup();
            if (lookup == null || !lookup.TryGetBackdrop(roomKey, out RoomBackdropSettingsLookup.BackdropSettings settings))
            {
                return;
            }

            backdropTextureScale = settings.textureScale;
            backdropTextureOffset = settings.textureOffset;
            flipBackdropTextureX = settings.flipX;
            flipBackdropTextureY = settings.flipY;
            if (settings.overrideGlobalTint)
            {
                ApplyTintSettings(settings.tint, isBackdrop: true);
            }
            ClampBackdropSettings();
        }

        private void ApplyStoredRoomTintSettingsIfAvailable(RoomInstance room)
        {
            string roomKey = ResolveRoomStorageKey(room);
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            RoomBackdropSettingsLookup lookup = ResolveBackdropSettingsLookup();
            if (lookup == null)
            {
                return;
            }

            if (lookup.TryGetRoomBackdropTint(roomKey, out RoomBackdropSettingsLookup.TintSettings backdropTintSettings))
            {
                ApplyTintSettings(backdropTintSettings, isBackdrop: true);
            }

            if (lookup.TryGetRoomDecorationTint(roomKey, out RoomBackdropSettingsLookup.TintSettings decorationTintSettings))
            {
                ApplyTintSettings(decorationTintSettings, isBackdrop: false);
            }

            ClampBackdropSettings();
        }

        private RoomBackdropSettingsLookup ResolveBackdropSettingsLookup()
        {
            if (roomBackdropSettingsLookup != null)
            {
                return roomBackdropSettingsLookup;
            }

            roomBackdropSettingsLookup = Resources.Load<RoomBackdropSettingsLookup>(RoomBackdropSettingsLookup.ResourcesPath);
            return roomBackdropSettingsLookup;
        }

        private PfeDebugSettings ResolveDebugSettings()
        {
            if (debugSettings != null)
            {
                return debugSettings;
            }

            debugSettings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
            return debugSettings;
        }

        private void ClampBackdropSettings()
        {
            backdropTextureScale.x = Mathf.Clamp(backdropTextureScale.x, 0.1f, 4f);
            backdropTextureScale.y = Mathf.Clamp(backdropTextureScale.y, 0.1f, 4f);
            backdropTextureOffset.x = Mathf.Clamp(backdropTextureOffset.x, -2f, 2f);
            backdropTextureOffset.y = Mathf.Clamp(backdropTextureOffset.y, -2f, 2f);
            backdropBrightness = Mathf.Clamp(backdropBrightness, 0f, 2.5f);
            backdropSharpenStrength = Mathf.Clamp(backdropSharpenStrength, 0f, 2f);
            backgroundAssetBrightness = Mathf.Clamp(backgroundAssetBrightness, 0f, 2.5f);
        }

        private void ApplyTintSettings(RoomBackdropSettingsLookup.TintSettings settings, bool isBackdrop)
        {
            if (isBackdrop)
            {
                backdropTint = settings.tint;
                backdropBrightness = settings.brightness;
            }
            else
            {
                backgroundAssetTint = settings.tint;
                backgroundAssetBrightness = settings.brightness;
            }
        }

        private static string ResolveRoomStorageKey(RoomInstance room)
        {
            if (!string.IsNullOrWhiteSpace(room?.templateId))
            {
                return room.templateId;
            }

            return room?.id ?? string.Empty;
        }

        private static Color ResolveBackgroundTileTint(RoomInstance room)
        {
            return RoomBackdropRenderer.ResolveBackgroundTileTint(room?.environment);
        }

        #if UNITY_EDITOR
        private void RefreshBackdropPreviewDelayed()
        {
            if (this == null)
            {
                return;
            }

            RefreshBackdropVisuals();
        }
        #endif

        #if UNITY_EDITOR
        public RoomTemplate PreviewTemplate
        {
            get => previewTemplate;
            set => previewTemplate = value;
        }

        public void LoadPreviewRoom()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[RoomVisualController] Preview loading is editor-only. Stop play mode to use it.");
                return;
            }

            if (previewTemplate == null)
            {
                Debug.LogWarning("[RoomVisualController] No preview template selected.");
                return;
            }

            if (tileAssetDatabase == null)
            {
                Debug.LogError("[RoomVisualController] TileAssetDatabase is required for preview loading.");
                return;
            }

            if (tileFormDatabase == null)
            {
                Debug.LogError("[RoomVisualController] TileFormDatabase is required for preview loading.");
                return;
            }

            RoomInstance previewRoom = BuildPreviewRoom(previewTemplate, previewLandPosition);
            if (previewRoom == null)
            {
                Debug.LogError($"[RoomVisualController] Failed to build preview room for template '{previewTemplate.name}'.");
                return;
            }

            ClearPreviewVisuals();
            Initialize(previewRoom, tileAssetDatabase);
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public void ClearPreviewRoom()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ClearPreviewVisuals();
            roomInstance = null;
            isInitialized = false;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        private RoomInstance BuildPreviewRoom(RoomTemplate template, Vector3Int landPosition)
        {
            RoomGenerator generator = new RoomGenerator(tileFormDatabase);
            RoomInstance room = generator.GenerateRoom(template, landPosition);
            if (room == null)
            {
                return null;
            }

            RoomSetup.FinalizeRoom(room, template);

            if (!string.IsNullOrEmpty(template.backgroundRoomId))
            {
                RoomTemplate backgroundTemplate = LoadRoomTemplateById(template.backgroundRoomId);
                if (backgroundTemplate != null)
                {
                    RoomInstance backgroundRoom = generator.GenerateRoom(backgroundTemplate, landPosition);
                    RoomSetup.FinalizeRoom(backgroundRoom, backgroundTemplate);
                    backgroundRoom.roomType = "back";
                    room.backgroundRoom = backgroundRoom;
                    room.hasBackgroundLayer = true;
                }
                else
                {
                    Debug.LogWarning($"[RoomVisualController] Preview background room template '{template.backgroundRoomId}' was not found for '{template.id}'.");
                }
            }

            return room;
        }

        private void ClearPreviewVisuals()
        {
            tileVisualManager?.DestroyAllTiles();
            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();

            tileVisualManager = null;
            backgroundTileVisualManager = null;
            roomBackdropRenderer = null;
            roomObjectVisualManager = null;
            visibilityRevealTargetTransform = null;
            isInitialized = false;

            DestroyChildren(tileParent);
            DestroyChildren(backgroundParent);
            DestroyChildren(backgroundObjectParent);
            DestroyChildren(backgroundPhysicalObjectParent);
            DestroyChildren(lightingParent);
            DestroyChildren(fogOfWarParent);
        }

        private static void DestroyChildren(Transform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object child = parent.GetChild(i).gameObject;
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        private static void CopyEditedTileFields(TileData source, TileData destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            destination.physicsType = source.physicsType;
            destination.indestructible = source.indestructible;
            destination.hitPoints = source.hitPoints;
            destination.damageThreshold = source.damageThreshold;
            destination.SetFrontGraphic(source.GetFrontGraphic());
            destination.SetBackGraphic(source.GetBackGraphic());
            destination.SetZadGraphic(source.GetZadGraphic());
            destination.visualId = source.visualId;
            destination.visualId2 = source.visualId2;
            destination.frontRear = source.frontRear;
            destination.vidRear = source.vidRear;
            destination.vid2Rear = source.vid2Rear;
            destination.opacity = source.opacity;
            destination.heightLevel = source.heightLevel;
            destination.slopeType = source.slopeType;
            destination.stairType = source.stairType;
            destination.isLedge = source.isLedge;
            destination.hasWater = source.hasWater;
            destination.lurk = source.lurk;
            destination.kontur1 = source.kontur1;
            destination.kontur2 = source.kontur2;
            destination.kontur3 = source.kontur3;
            destination.kontur4 = source.kontur4;
            destination.pontur1 = source.pontur1;
            destination.pontur2 = source.pontur2;
            destination.pontur3 = source.pontur3;
            destination.pontur4 = source.pontur4;
            destination.material = source.material;
            destination.canPlaceObjects = source.canPlaceObjects;
            destination.doorId = source.doorId;
            destination.trapId = source.trapId;
        }

        private static RoomTemplate LoadRoomTemplateById(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:RoomTemplate", new[] { "Assets/_PFE/Data/Resources/Rooms" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                RoomTemplate template = UnityEditor.AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template != null && template.id == templateId)
                {
                    return template;
                }
            }

            return null;
        }
        #endif

        #if UNITY_EDITOR
        private static GUIStyle _debugLabelStyle;

        /// <summary>
        /// Draw room bounds in editor.
        /// </summary>
        private void OnDrawGizmos()
        {
            if (roomInstance == null)
                return;

            // Use transform.position (which is already set to room world position)
            // rather than recalculating, to avoid mismatches
            Vector3 center = transform.position;
            // Use actual room dimensions (may be expanded due to border)
            Vector3 size = WorldCoordinates.PixelToUnity(new Vector2(
                roomInstance.width * WorldConstants.TILE_SIZE,
                roomInstance.height * WorldConstants.TILE_SIZE
            ));

            Gizmos.color = new Color(0, 1, 1, 0.3f);
            Gizmos.DrawWireCube(center, size);

            // Draw room label
            UnityEditor.Handles.Label(center, $"Room {roomInstance.landPosition}\n{roomInstance.roomType}");

            if (!showContourDebugOverlay || roomInstance.tiles == null)
            {
                return;
            }

            DrawContourDebugOverlay();
        }

        private void DrawContourDebugOverlay()
        {
            for (int x = 0; x < roomInstance.width; x++)
            {
                for (int y = 0; y < roomInstance.height; y++)
                {
                    TileData tile = roomInstance.tiles[x, y];
                    if (tile == null)
                    {
                        continue;
                    }

                    string label = BuildDebugLabel(tile);
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    Vector3 world = transform.position + WorldCoordinates.PixelToUnity(tile.GetBounds().center);
                    UnityEditor.Handles.Label(world, label, GetDebugLabelStyle());
                }
            }
        }

        private string BuildDebugLabel(TileData tile)
        {
            if (tile == null)
            {
                return string.Empty;
            }

            if (tile.physicsType == TilePhysicsType.Wall)
            {
                return $"{SanitizeToken(tile.GetFrontGraphic())}\n{tile.kontur1}{tile.kontur2}\n{tile.kontur3}{tile.kontur4}";
            }

            if (!string.IsNullOrWhiteSpace(tile.GetBackGraphic()))
            {
                return $"{SanitizeToken(tile.GetBackGraphic())}\n{tile.pontur1}{tile.pontur2}\n{tile.pontur3}{tile.pontur4}";
            }

            return string.Empty;
        }

        private static GUIStyle GetDebugLabelStyle()
        {
            if (_debugLabelStyle == null)
            {
                _debugLabelStyle = CreateDebugLabelStyle();
            }

            return _debugLabelStyle;
        }

        private static GUIStyle CreateDebugLabelStyle()
        {
            GUIStyle style = new GUIStyle(UnityEditor.EditorStyles.miniLabel);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 10;
            style.richText = false;
            style.normal.textColor = new Color(1f, 0.95f, 0.4f, 0.95f);
            return style;
        }
        #endif

        private string BuildShortDebugString(RoomInstance room)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"room={room.id} template={room.templateId} size={room.width}x{room.height} type={room.roomType}");

            for (int y = 0; y < room.height; y++)
            {
                sb.Append("row ");
                sb.Append(y.ToString("D2"));
                sb.Append(": ");

                for (int x = 0; x < room.width; x++)
                {
                    if (x > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(BuildShortTileToken(room.tiles[x, y]));
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string BuildLongDebugString(RoomInstance room)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"room={room.id} template={room.templateId} size={room.width}x{room.height} type={room.roomType}");

            for (int y = 0; y < room.height; y++)
            {
                for (int x = 0; x < room.width; x++)
                {
                    TileData tile = room.tiles[x, y];
                    if (tile == null)
                    {
                        sb.Append('[').Append(x).Append(',').Append(y).AppendLine("] null");
                        continue;
                    }

                    MaterialRenderEntry frontMaterial = materialRenderDb != null ? materialRenderDb.GetFrontMaterial(tile.GetFrontGraphic()) : null;
                    MaterialRenderEntry backMaterial = materialRenderDb != null ? materialRenderDb.GetBackMaterial(tile.GetBackGraphic()) : null;

                    sb.Append('[').Append(x).Append(',').Append(y).Append("] ");
                    sb.Append("phys=").Append(tile.physicsType);
                    sb.Append(" front=").Append(SanitizeToken(tile.GetFrontGraphic()));
                    sb.Append(" back=").Append(SanitizeToken(tile.GetBackGraphic()));
                    sb.Append(" k=").Append(tile.kontur1).Append(tile.kontur2).Append(tile.kontur3).Append(tile.kontur4);
                    sb.Append(" p=").Append(tile.pontur1).Append(tile.pontur2).Append(tile.pontur3).Append(tile.pontur4);
                    sb.Append(" h=").Append(tile.heightLevel);
                    sb.Append(" v1=").Append(tile.visualId);
                    sb.Append(" v2=").Append(tile.visualId2);
                    sb.Append(" frontMask=").Append(SanitizeToken(frontMaterial?.mainMask));
                    sb.Append(" borderMask=").Append(SanitizeToken(frontMaterial?.borderMask));
                    sb.Append(" floorMask=").Append(SanitizeToken(frontMaterial?.floorMask));
                    sb.Append(" backMask=").Append(SanitizeToken(backMaterial?.mainMask));
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private string BuildShortTileToken(TileData tile)
        {
            if (tile == null)
            {
                return "null";
            }

            if (tile.physicsType == TilePhysicsType.Wall)
            {
                return $"{SanitizeToken(tile.GetFrontGraphic())}[{tile.kontur1}{tile.kontur2}{tile.kontur3}{tile.kontur4}]";
            }

            if (!string.IsNullOrWhiteSpace(tile.GetBackGraphic()))
            {
                return $"{SanitizeToken(tile.GetBackGraphic())}<{tile.pontur1}{tile.pontur2}{tile.pontur3}{tile.pontur4}>";
            }

            return "_";
        }

        private static string SanitizeToken(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "_" : value.Replace(' ', '_');
        }
    }
}

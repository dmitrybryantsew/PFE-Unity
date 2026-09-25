using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Bootstraps the map rendering system at runtime.
    /// Creates MapRenderer GameObject with required components if not present in scene.
    /// </summary>
    public class MapRendererBootstrapper : MonoBehaviour
    {
        [SerializeField] private bool createIfMissing = true;
        [SerializeField] private TileAssetDatabase tileDatabase;
        [SerializeField] private TileTextureLookup tileTextureLookup;
        [SerializeField] private MaterialRenderDatabase materialRenderDatabase;
        [SerializeField] private TileMaskLookup tileMaskLookup;
        [SerializeField] private RoomBackgroundLookup roomBackgroundLookup;
        
        private GameManager gameManager;
        private RoomGenerator roomGenerator;
        private PfeDebugSettings debugSettings;
        private MapBridge mapBridge;
        
        [Inject]
        public void Construct(
            GameManager gm,
            RoomGenerator generator,
            TileTextureLookup textureLookup,
            MaterialRenderDatabase materialDatabase,
            TileMaskLookup maskLookup,
            RoomBackgroundLookup backgroundLookup,
            PfeDebugSettings debug)
        {
            gameManager = gm;
            roomGenerator = generator;
            tileTextureLookup = tileTextureLookup != null ? tileTextureLookup : textureLookup;
            materialRenderDatabase = materialRenderDatabase != null ? materialRenderDatabase : materialDatabase;
            tileMaskLookup = tileMaskLookup != null ? tileMaskLookup : maskLookup;
            roomBackgroundLookup = roomBackgroundLookup != null ? roomBackgroundLookup : backgroundLookup;
            debugSettings = debug;
        }
        
        private void Start()
        {
            Debug.Log("[MapRendererBootstrapper] Starting...");
            
            // Check if MapBridge already exists in scene
            mapBridge = FindFirstObjectByType<MapBridge>();
            
            if (mapBridge == null && createIfMissing)
            {
                Debug.Log("[MapRendererBootstrapper] MapBridge not found - creating MapRenderer...");
                CreateMapRenderer();
            }
            else if (mapBridge != null)
            {
                Debug.Log("[MapRendererBootstrapper] MapBridge already exists in scene");
            }
        }
        
        private void CreateMapRenderer()
        {
            // Create MapRenderer GameObject
            GameObject mapRendererObj = new GameObject("MapRenderer");
            mapRendererObj.transform.position = Vector3.zero;
            
            // Add RoomVisualController
            RoomVisualController visualController = mapRendererObj.AddComponent<RoomVisualController>();
            
            // Add MapBridge
            MapBridge bridge = mapRendererObj.AddComponent<MapBridge>();
            
            // Set up references via reflection or serialized fields
            // MapBridge uses [SerializeField] private fields, so we need to set them
            var visualControllerField = typeof(MapBridge).GetField("_visualController", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tileDatabaseField = typeof(MapBridge).GetField("_tileDatabase", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (visualControllerField != null)
            {
                visualControllerField.SetValue(bridge, visualController);
                Debug.Log("[MapRendererBootstrapper] Set visualController reference");
            }
            
            if (tileDatabaseField != null)
            {
                // Try to find tile database if not assigned
                if (tileDatabase == null)
                {
                    tileDatabase = FindFirstObjectByType<TileAssetDatabase>();
                    if (tileDatabase == null)
                    {
                        // Create a runtime tile database
                        tileDatabase = ScriptableObject.CreateInstance<TileAssetDatabase>();
                        Debug.Log("[MapRendererBootstrapper] Created runtime TileAssetDatabase");
                    }
                }
                tileDatabaseField.SetValue(bridge, tileDatabase);
                Debug.Log("[MapRendererBootstrapper] Set tileDatabase reference");
            }
            
            if (gameManager != null && roomGenerator != null)
            {
                bridge.Construct(
                    gameManager,
                    roomGenerator,
                    tileTextureLookup,
                    materialRenderDatabase,
                    tileMaskLookup,
                    roomBackgroundLookup,
                    debugSettings);
                Debug.Log("[MapRendererBootstrapper] Injected runtime dependencies into MapBridge");
            }
            else
            {
                Debug.LogError("[MapRendererBootstrapper] Cannot inject MapBridge: GameManager or RoomGenerator is missing.");
            }
            
            Debug.Log("[MapRendererBootstrapper] MapRenderer creation complete!");
        }
    }
}

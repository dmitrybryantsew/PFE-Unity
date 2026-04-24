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
        
        private GameManager gameManager;
        private MapBridge mapBridge;
        
        [Inject]
        public void Construct(GameManager gm)
        {
            gameManager = gm;
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
            
            // Trigger injection manually since we created this at runtime
            // The GameManager should already be available via VContainer
            var constructMethod = typeof(MapBridge).GetMethod("Construct", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (constructMethod != null && gameManager != null)
            {
                constructMethod.Invoke(bridge, new object[] { gameManager, tileTextureLookup, materialRenderDatabase });
                Debug.Log("[MapRendererBootstrapper] Injected runtime dependencies into MapBridge");
            }
            
            // Call Start manually to begin initialization
            var startMethod = typeof(MapBridge).GetMethod("Start", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (startMethod != null)
            {
                startMethod.Invoke(bridge, null);
                Debug.Log("[MapRendererBootstrapper] Called MapBridge.Start()");
            }
            
            Debug.Log("[MapRendererBootstrapper] MapRenderer creation complete!");
        }
    }
}

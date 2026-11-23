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
        // Scene-serialized overrides for the render lookups — used in preference to the injected
        // bundle so a standalone rig can point at a different asset set without a container change.
        // Either may be null, in which case the injected bundle's entry wins.
        [SerializeField] private MaterialRenderDatabase materialRenderDatabase;
        [SerializeField] private RoomBackgroundLookup roomBackgroundLookup;

        private GameManager gameManager;
        private RoomGenerator roomGenerator;
        private PfeDebugSettings debugSettings;
        private MapBridge mapBridge;

        // P1: forwarded to MapBridge.Construct below. This bootstrapper is the ONLY manual caller of
        // that method (the other path is VContainer's own injection), so when MapBridge.Construct
        // gained required sim parameters this call site had to be updated in the same change.
        private SimClock simClock;
        private SimLoop simLoop;

        // Stage B: same reason as above — MapBridge.Construct gained the physics world service, so
        // this call site has to forward it. Named `worldService` rather than `physicsWorldService`
        // because `PhysicsWorld` is also a type name in scope here.
        private PFE.Systems.Physics.IPhysicsWorldService worldService;

        // Prop impact damage: MapBridge.Construct gained the damage authority so a crate landing on an
        // enemy resolves through armour, and this bootstrapper is the only manual caller of that
        // method — so this call site had to be updated in the same change, exactly as the two comments
        // above say. `[Inject]` resolves it: DamageSystem is registered with
        // RegisterEntryPoint<DamageSystem>().AsSelf().
        private PFE.Systems.Combat.DamageSystem damageSystem;

        // Wave 2 thread G: MapBridge.Construct now takes the render lookups as one
        // RoomRenderAssetBundle and the authoritative transition decisions as a LandSessionService.
        // The bundle arrives from the container; the session is stateless, so a bare instance is a
        // faithful stand-in when the container has none (this bootstrapper's manual call path).
        private RoomRenderAssetBundle renderAssets;

        [Inject]
        public void Construct(
            GameManager gm,
            RoomGenerator generator,
            RoomRenderAssetBundle renderAssetBundle,
            PfeDebugSettings debug,
            SimClock clock,
            SimLoop loop,
            PFE.Systems.Physics.IPhysicsWorldService physicsWorldService,
            PFE.Systems.Combat.DamageSystem damage)
        {
            gameManager = gm;
            roomGenerator = generator;
            renderAssets = renderAssetBundle;
            debugSettings = debug;
            simClock = clock;
            simLoop = loop;
            worldService = physicsWorldService;
            damageSystem = damage;
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

            if (visualControllerField != null)
            {
                visualControllerField.SetValue(bridge, visualController);
                Debug.Log("[MapRendererBootstrapper] Set visualController reference");
            }

            if (gameManager != null && renderAssets != null)
            {
                // Scene-serialized overrides win over the injected bundle entries, preserving the
                // bootstrapper's original "my field first" semantics after the bundle grouping.
                var merged = new RoomRenderAssetBundle(
                    materialRenderDatabase != null ? materialRenderDatabase : renderAssets.Materials,
                    roomBackgroundLookup != null ? roomBackgroundLookup : renderAssets.Backgrounds,
                    renderAssets.TileTextures,
                    renderAssets.TileMasks);

                bridge.Construct(
                    gameManager,
                    roomGenerator,
                    merged,
                    debugSettings,
                    simClock,
                    simLoop,
                    worldService,
                    damageSystem,
                    new LandSessionService());
                Debug.Log("[MapRendererBootstrapper] Injected runtime dependencies into MapBridge");
            }
            else
            {
                Debug.LogError("[MapRendererBootstrapper] Cannot inject MapBridge: GameManager or the render asset bundle is missing.");
            }

            Debug.Log("[MapRendererBootstrapper] MapRenderer creation complete!");
        }
    }
}

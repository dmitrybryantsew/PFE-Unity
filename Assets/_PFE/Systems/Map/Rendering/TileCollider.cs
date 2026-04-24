using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Audio;
using PFE.Core;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Adds and manages Unity Collider2D components for tiles.
    /// Attached to tile GameObjects alongside TileRenderer.
    /// </summary>
    [RequireComponent(typeof(TileRenderer))]
    public class TileCollider : MonoBehaviour
    {
        [Header("Tile Data")]
        [SerializeField] private TileData tileData;
        [SerializeField] private Collider2D tileCollider;
        private PfeDebugSettings debugSettings;

        // Layer constants
        private const int LAYER_GROUND = 6;
        private const int LAYER_PLATFORM = 7;

        /// <summary>
        /// Initialize the tile collider based on tile data.
        /// </summary>
        public void Initialize(TileData data, PfeDebugSettings debugSettings = null)
        {
            tileData = data;
            this.debugSettings = debugSettings;
            
            if (tileData == null)
            {
                Debug.LogWarning("[TileCollider] Cannot initialize with null tile data");
                return;
            }

            // Remove any existing collider
            if (tileCollider != null)
            {
                if (Application.isPlaying)
                    Destroy(tileCollider);
                else
                    DestroyImmediate(tileCollider);
            }

            // Create collider based on physics type
            switch (tileData.physicsType)
            {
                case TilePhysicsType.Wall:
                    CreateSolidCollider();
                    break;

                case TilePhysicsType.Platform:
                    CreatePlatformCollider();
                    break;

                case TilePhysicsType.Stair:
                    CreateStairCollider();
                    break;

                case TilePhysicsType.Air:
                    // No collider for air
                    break;
            }

            // Stamp surface material for impact sound resolution.
            // Air tiles have no collider so the component is harmless but present.
            var tileSurface = GetComponent<TileSurface>() ?? gameObject.AddComponent<TileSurface>();
            tileSurface.Set(TileSurface.FromMaterialType(tileData.material));
        }

        /// <summary>
        /// Create a solid BoxCollider2D for walls.
        /// </summary>
        private void CreateSolidCollider()
        {
            BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();
            
            // Set size to match tile (40 pixels = 0.4 units)
            float size = WorldConstants.TILE_SIZE / 100f;
            float heightScale = tileData != null ? Mathf.Clamp01(1f - tileData.heightLevel * 0.25f) : 1f;
            float height = size * heightScale;
            boxCollider.size = new Vector2(size, height);
            
            // Shift shorter tiles upward so they still sit on the cell floor.
            boxCollider.offset = new Vector2(0f, (size - height) * 0.5f);
            
            // Set layer
            gameObject.layer = LAYER_GROUND;
            
            tileCollider = boxCollider;
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Wall collider at {transform.position}");
            }
        }

        /// <summary>
        /// Create a one-way platform collider.
        /// </summary>
        private void CreatePlatformCollider()
        {
            BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();
            
            // Platform is thinner (just the top surface)
            float width = WorldConstants.TILE_SIZE / 100f;
            float height = 0.1f; // Thin platform
            boxCollider.size = new Vector2(width, height);
            
            // Offset to top of tile
            boxCollider.offset = new Vector2(0, (WorldConstants.TILE_SIZE / 100f - height) * 0.5f);
            
            // Set as trigger for one-way (or use PlatformEffector2D)
            gameObject.layer = LAYER_PLATFORM;
            
            // Add PlatformEffector2D for one-way collision
            PlatformEffector2D effector = gameObject.AddComponent<PlatformEffector2D>();
            effector.useOneWay = true;
            effector.useOneWayGrouping = true;
            effector.surfaceArc = 180f; // Only collide from top
            
            tileCollider = boxCollider;
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Platform collider at {transform.position}");
            }
        }

        /// <summary>
        /// Create a stair/slope collider.
        /// </summary>
        private void CreateStairCollider()
        {
            float size = WorldConstants.TILE_SIZE / 100f;
            gameObject.layer = LAYER_GROUND;

            if (tileData != null && tileData.slopeType != 0)
            {
                PolygonCollider2D polygonCollider = gameObject.AddComponent<PolygonCollider2D>();
                Vector2[] points = tileData.slopeType > 0
                    ? new[]
                    {
                        new Vector2(-size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, size * 0.5f)
                    }
                    : new[]
                    {
                        new Vector2(-size * 0.5f, size * 0.5f),
                        new Vector2(-size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, -size * 0.5f)
                    };
                polygonCollider.SetPath(0, points);
                tileCollider = polygonCollider;
            }
            else
            {
                BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();
                boxCollider.size = new Vector2(size, size * 0.5f);
                boxCollider.offset = new Vector2(0, -size * 0.25f);
                tileCollider = boxCollider;
            }
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Stair collider at {transform.position}");
            }
        }

        /// <summary>
        /// Get the tile data.
        /// </summary>
        public TileData GetTileData()
        {
            return tileData;
        }

        /// <summary>
        /// Update collider if tile data changes.
        /// </summary>
        public void RefreshCollider()
        {
            if (tileData != null)
            {
                Initialize(tileData, debugSettings);
            }
        }
    }
}

using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Simple tile renderer that creates colored squares as placeholders.
    /// Used when actual tile sprites are not yet imported.
    /// </summary>
    public class SimpleTileRenderer : MonoBehaviour
    {
        [Header("Tile Data")]
        [SerializeField] private TileData tileData;
        
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        
        // Color palette for different tile types
        private static readonly Color ColorWall = new Color(0.4f, 0.4f, 0.4f, 1f);      // Gray
        private static readonly Color ColorPlatform = new Color(0.8f, 0.6f, 0.3f, 1f);  // Brown/wood
        private static readonly Color ColorStair = new Color(0.6f, 0.6f, 0.8f, 1f);     // Blue-gray
        private static readonly Color ColorHazard = new Color(0.9f, 0.2f, 0.2f, 1f);    // Red
        private static readonly Color ColorDoor = new Color(0.4f, 0.7f, 0.4f, 1f);      // Green
        
        /// <summary>
        /// Initialize with tile data - creates a simple colored square.
        /// </summary>
        public void Initialize(TileData data)
        {
            tileData = data;
            
            if (tileData == null)
            {
                Debug.LogWarning("[SimpleTileRenderer] Cannot initialize with null tile data");
                return;
            }

            // Get or create SpriteRenderer
            spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer == null)
            {
                spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
            }

            // Create a simple 1x1 white sprite if needed
            if (spriteRenderer.sprite == null)
            {
                spriteRenderer.sprite = CreatePlaceholderSprite();
            }

            // Set color based on physics type
            spriteRenderer.color = GetTileColor(tileData.physicsType);
            
            // Tile sprites use centered pivots, so place them at the tile bounds center.
            transform.localPosition = GetLocalPosition();
            
            // Set sorting order (Y-based for depth)
            spriteRenderer.sortingLayerName = MapSortingLayers.MainTiles;
            spriteRenderer.sortingOrder = -tileData.gridPosition.y;
            
            // Set scale to match tile size (40 pixels = 0.4 units)
            float tileSize = WorldConstants.TILE_SIZE / 100f;
            transform.localScale = new Vector3(tileSize, tileSize, 1f);
        }
        
        /// <summary>
        /// Create a simple white 1x1 sprite for placeholders.
        /// </summary>
        private Sprite CreatePlaceholderSprite()
        {
            // Create a 32x32 white texture
            Texture2D texture = new Texture2D(32, 32);
            Color[] pixels = new Color[32 * 32];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.white;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            
            // Create sprite from texture
            return Sprite.Create(texture, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 32f);
        }
        
        /// <summary>
        /// Get color for tile type.
        /// </summary>
        private Color GetTileColor(TilePhysicsType type)
        {
            return type switch
            {
                TilePhysicsType.Wall => ColorWall,
                TilePhysicsType.Platform => ColorPlatform,
                TilePhysicsType.Stair => ColorStair,
                _ => Color.clear
            };
        }
        
        /// <summary>
        /// Get local position for this tile within the room transform.
        /// </summary>
        private Vector3 GetLocalPosition()
        {
            Rect tileBounds = tileData != null
                ? tileData.GetBounds()
                : WorldCoordinates.TileToRect(Vector2Int.zero);
            Vector3 unityPos = WorldCoordinates.PixelToUnity(tileBounds.center);

            // Set Z for layering
            unityPos.z = tileData.gridPosition.y * 0.01f;

            return unityPos;
        }
        
        /// <summary>
        /// Set visibility.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (spriteRenderer != null)
            {
                spriteRenderer.enabled = visible;
            }
        }
        
        /// <summary>
        /// Flash tile for damage indication.
        /// </summary>
        public void Flash(Color flashColor, float duration = 0.1f)
        {
            if (spriteRenderer == null) return;
            
            StartCoroutine(FlashCoroutine(flashColor, duration));
        }
        
        private System.Collections.IEnumerator FlashCoroutine(Color flashColor, float duration)
        {
            Color originalColor = spriteRenderer.color;
            spriteRenderer.color = flashColor;
            
            yield return new WaitForSeconds(duration);
            
            spriteRenderer.color = originalColor;
        }
    }
}

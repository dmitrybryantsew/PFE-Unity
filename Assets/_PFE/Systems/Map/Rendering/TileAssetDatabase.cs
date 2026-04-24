using UnityEngine;
using System.Collections.Generic;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// ScriptableObject database for tile sprites.
    /// Maps tile visual IDs to Unity sprites for rendering.
    /// </summary>
    [CreateAssetMenu(fileName = "TileAssetDatabase", menuName = "PFE/Map/Tile Asset Database")]
    public class TileAssetDatabase : ScriptableObject
    {
        private const int OverlayTextureSize = 40;

        [System.Serializable]
        public class TileSpriteEntry
        {
            public string tileId = "";
            public Sprite sprite = null;
            public TilePhysicsType physicsType = TilePhysicsType.Air;
            public MaterialType material = MaterialType.Default;
        }

        [Header("Tile Sprites")]
        [Tooltip("Mapping of tile IDs to sprites")]
        [SerializeField] private List<TileSpriteEntry> tileSprites = new List<TileSpriteEntry>();

        [Header("Default Sprites")]
        [Tooltip("Default sprite for each physics type (used as fallback)")]
        [SerializeField] private Sprite defaultAirSprite = null;
        [SerializeField] private Sprite defaultWallSprite = null;
        [SerializeField] private Sprite defaultPlatformSprite = null;
        [SerializeField] private Sprite defaultStairSprite = null;

        [Header("Sprite Collections")]
        [Tooltip("All sprites for this tileset (for inspector view)")]
        [SerializeField] private List<Sprite> allSprites = new List<Sprite>();

        [System.NonSerialized] private Dictionary<string, Sprite> generatedOverlaySprites = new Dictionary<string, Sprite>();
        [System.NonSerialized] private HashSet<string> missingOverlayWarnings = new HashSet<string>();

        /// <summary>
        /// Get sprite by tile ID.
        /// </summary>
        public Sprite GetSprite(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return null;

            foreach (var entry in tileSprites)
            {
                if (entry.tileId == tileId)
                    return entry.sprite;
            }

            return null;
        }

        /// <summary>
        /// Get sprite by visual ID.
        /// </summary>
        public Sprite GetSpriteByVisualId(int visualId)
        {
            string tileId = $"tile_{visualId}";
            return GetSprite(tileId);
        }

        public Sprite GetOverlaySprite(TileData tile, int visualId)
        {
            if (visualId <= 0)
                return null;

            Sprite sprite = GetSpriteByVisualId(visualId);
            if (sprite != null)
            {
                return sprite;
            }

            WarnMissingOverlaySprite(tile, visualId);
            return GetGeneratedOverlaySprite(tile, visualId);
        }

        /// <summary>
        /// Get default sprite for physics type.
        /// </summary>
        public Sprite GetDefaultSprite(TilePhysicsType physicsType)
        {
            switch (physicsType)
            {
                case TilePhysicsType.Air:
                    return defaultAirSprite;
                case TilePhysicsType.Wall:
                    return defaultWallSprite;
                case TilePhysicsType.Platform:
                    return defaultPlatformSprite;
                case TilePhysicsType.Stair:
                    return defaultStairSprite;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Get sprite for a tile, with fallback to default.
        /// </summary>
        public Sprite GetSpriteOrDefault(string tileId, TilePhysicsType physicsType)
        {
            Sprite sprite = GetSprite(tileId);
            if (sprite == null)
            {
                sprite = GetDefaultSprite(physicsType);
            }
            return sprite;
        }

        /// <summary>
        /// Get sprite for a tile, with fallback to default by visual ID.
        /// </summary>
        public Sprite GetSpriteOrDefault(int visualId, TilePhysicsType physicsType)
        {
            Sprite sprite = GetSpriteByVisualId(visualId);
            if (sprite == null)
            {
                sprite = GetDefaultSprite(physicsType);
            }
            return sprite;
        }

        /// <summary>
        /// Add a tile sprite entry.
        /// </summary>
        public void AddTileSprite(string tileId, Sprite sprite, TilePhysicsType physicsType = TilePhysicsType.Air, MaterialType material = MaterialType.Default)
        {
            // Remove existing entry if present
            tileSprites.RemoveAll(entry => entry.tileId == tileId);

            // Add new entry
            tileSprites.Add(new TileSpriteEntry
            {
                tileId = tileId,
                sprite = sprite,
                physicsType = physicsType,
                material = material
            });

            if (!allSprites.Contains(sprite))
            {
                allSprites.Add(sprite);
            }
        }

        /// <summary>
        /// Remove a tile sprite entry.
        /// </summary>
        public void RemoveTileSprite(string tileId)
        {
            tileSprites.RemoveAll(entry => entry.tileId == tileId);
        }

        /// <summary>
        /// Clear all entries.
        /// </summary>
        public void Clear()
        {
            tileSprites.Clear();
            allSprites.Clear();
            ClearGeneratedOverlayCache();
        }

        /// <summary>
        /// Get total number of tile sprites.
        /// </summary>
        public int GetCount()
        {
            return tileSprites.Count;
        }

        /// <summary>
        /// Check if tile ID exists.
        /// </summary>
        public bool HasTile(string tileId)
        {
            foreach (var entry in tileSprites)
            {
                if (entry.tileId == tileId)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get all tile IDs.
        /// </summary>
        public List<string> GetAllTileIds()
        {
            List<string> ids = new List<string>();
            foreach (var entry in tileSprites)
            {
                ids.Add(entry.tileId);
            }
            return ids;
        }

        private void OnEnable()
        {
            ClearGeneratedOverlayCache();
            if (missingOverlayWarnings == null)
            {
                missingOverlayWarnings = new HashSet<string>();
            }
            else
            {
                missingOverlayWarnings.Clear();
            }
        }

        private void WarnMissingOverlaySprite(TileData tile, int visualId)
        {
            if (tile == null)
            {
                return;
            }

            if (missingOverlayWarnings == null)
            {
                missingOverlayWarnings = new HashSet<string>();
            }

            string key = $"{name}:{visualId}:{tile.stairType}:{tile.slopeType}:{tile.isLedge}";
            if (!missingOverlayWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[TileAssetDatabase] Missing imported overlay sprite for visualId={visualId} " +
                $"(db='{name}', entries={GetCount()}, stair={tile.stairType}, slope={tile.slopeType}, ledge={tile.isLedge}, tile={tile.gridPosition}). " +
                "Using generated placeholder.");
        }

        private Sprite GetGeneratedOverlaySprite(TileData tile, int visualId)
        {
            string key = $"overlay_{visualId}_{tile.physicsType}_{tile.stairType}_{tile.slopeType}_{tile.isLedge}";
            if (generatedOverlaySprites.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Texture2D texture = new Texture2D(OverlayTextureSize, OverlayTextureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[OverlayTextureSize * OverlayTextureSize];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.clear;
            }

            if (tile.stairType != 0)
            {
                DrawStairOverlay(pixels, tile.stairType);
            }
            else if (tile.slopeType != 0)
            {
                DrawSlopeOverlay(pixels, tile.slopeType);
            }
            else if (tile.isLedge || tile.physicsType == TilePhysicsType.Platform)
            {
                DrawPlatformOverlay(pixels);
            }
            else
            {
                DrawFrameOverlay(pixels);
            }

            texture.SetPixels(pixels);
            texture.Apply();

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, OverlayTextureSize, OverlayTextureSize),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = key;
            generatedOverlaySprites[key] = sprite;
            return sprite;
        }

        private void DrawStairOverlay(Color[] pixels, int direction)
        {
            Color fill = new Color(0.78f, 0.74f, 0.66f, 0.92f);
            Color stroke = new Color(0.2f, 0.16f, 0.12f, 1f);

            for (int x = 0; x < OverlayTextureSize; x++)
            {
                int stepIndex = direction > 0 ? x / 8 : (OverlayTextureSize - 1 - x) / 8;
                int height = Mathf.Clamp((stepIndex + 1) * 8, 0, OverlayTextureSize);
                for (int y = 0; y < height; y++)
                {
                    SetPixel(pixels, x, y, fill);
                    if (y == height - 1 || x % 8 == 0)
                    {
                        SetPixel(pixels, x, y, stroke);
                    }
                }
            }
        }

        private void DrawSlopeOverlay(Color[] pixels, int direction)
        {
            Color fill = new Color(0.72f, 0.72f, 0.78f, 0.9f);
            Color stroke = new Color(0.18f, 0.18f, 0.24f, 1f);

            for (int x = 0; x < OverlayTextureSize; x++)
            {
                int height = direction > 0
                    ? Mathf.RoundToInt((x + 1) / (float)OverlayTextureSize * OverlayTextureSize)
                    : Mathf.RoundToInt((OverlayTextureSize - x) / (float)OverlayTextureSize * OverlayTextureSize);

                for (int y = 0; y < height; y++)
                {
                    SetPixel(pixels, x, y, fill);
                    if (y == height - 1)
                    {
                        SetPixel(pixels, x, y, stroke);
                    }
                }
            }
        }

        private void DrawPlatformOverlay(Color[] pixels)
        {
            Color fill = new Color(0.65f, 0.56f, 0.34f, 0.95f);
            Color stroke = new Color(0.22f, 0.16f, 0.08f, 1f);

            for (int y = OverlayTextureSize - 10; y < OverlayTextureSize - 4; y++)
            {
                for (int x = 2; x < OverlayTextureSize - 2; x++)
                {
                    SetPixel(pixels, x, y, fill);
                    if (y == OverlayTextureSize - 10 || y == OverlayTextureSize - 5)
                    {
                        SetPixel(pixels, x, y, stroke);
                    }
                }
            }
        }

        private void DrawFrameOverlay(Color[] pixels)
        {
            Color stroke = new Color(1f, 1f, 1f, 0.65f);

            for (int x = 0; x < OverlayTextureSize; x++)
            {
                SetPixel(pixels, x, 0, stroke);
                SetPixel(pixels, x, OverlayTextureSize - 1, stroke);
            }

            for (int y = 0; y < OverlayTextureSize; y++)
            {
                SetPixel(pixels, 0, y, stroke);
                SetPixel(pixels, OverlayTextureSize - 1, y, stroke);
            }
        }

        private void SetPixel(Color[] pixels, int x, int y, Color color)
        {
            if (x < 0 || x >= OverlayTextureSize || y < 0 || y >= OverlayTextureSize)
                return;

            pixels[y * OverlayTextureSize + x] = color;
        }

        private void ClearGeneratedOverlayCache()
        {
            if (generatedOverlaySprites == null)
            {
                generatedOverlaySprites = new Dictionary<string, Sprite>();
                return;
            }

            foreach (var sprite in generatedOverlaySprites.Values)
            {
                if (sprite != null && sprite.texture != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(sprite.texture);
                    }
                    else
                    {
                        DestroyImmediate(sprite.texture);
                    }
                }
            }

            generatedOverlaySprites.Clear();
        }
    }
}

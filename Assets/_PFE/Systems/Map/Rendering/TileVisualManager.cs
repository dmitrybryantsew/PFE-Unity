using UnityEngine;
using System.Collections.Generic;
using PFE.Core;
//using TileCollider;
namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Manages visual GameObjects for tiles in a room.
    /// Handles creation, pooling, updating, and destruction of tile sprites.
    /// </summary>
    public class TileVisualManager
    {
        private RoomInstance room;
        private TileAssetDatabase assetDatabase;
        private Transform tileParent;
        private TileCompositor compositor;
        private int sortingOrderOffset;
        private Color tintColor;
        private Color secondaryTintColor;
        private string baseSortingLayerName;
        private string frontSortingLayerName;
        private PfeDebugSettings debugSettings;
        private Dictionary<Vector2Int, TileRenderer> tileRenderers = new Dictionary<Vector2Int, TileRenderer>();

        /// <summary>
        /// Initialize the tile visual manager.
        /// </summary>
        public TileVisualManager(RoomInstance room, TileAssetDatabase assetDatabase, Transform parent, TileCompositor compositor)
            : this(room, assetDatabase, parent, compositor, null, 0, Color.white, Color.white, MapSortingLayers.MainTiles, MapSortingLayers.Foreground)
            {}

        public TileVisualManager(RoomInstance room, TileAssetDatabase assetDatabase, Transform parent, TileCompositor compositor, PfeDebugSettings debugSettings, int sortingOrderOffset, Color tintColor, Color secondaryTintColor, string baseSortingLayerName, string frontSortingLayerName)
        {
            this.room = room;
            this.assetDatabase = assetDatabase;
            this.tileParent = parent;
            this.compositor = compositor;
            this.debugSettings = debugSettings;
            this.sortingOrderOffset = sortingOrderOffset;
            this.tintColor = tintColor;
            this.secondaryTintColor = secondaryTintColor;
            this.baseSortingLayerName = string.IsNullOrWhiteSpace(baseSortingLayerName) ? MapSortingLayers.MainTiles : baseSortingLayerName;
            this.frontSortingLayerName = string.IsNullOrWhiteSpace(frontSortingLayerName) ? this.baseSortingLayerName : frontSortingLayerName;
        }

        /// <summary>
        /// Create all tile GameObjects for the room.
        /// </summary>
        public void CreateAllTiles()
        {
            if (room == null)
            {
                Debug.LogError("[TileVisualManager] Cannot create tiles: room is null");
                return;
            }

            if (assetDatabase == null)
            {
                Debug.LogError("[TileVisualManager] Cannot create tiles: asset database is null");
                return;
            }

            if (room.tiles == null)
            {
                Debug.LogError($"[TileVisualManager] Room {room.id} has null tiles array!");
                return;
            }

            if (debugSettings != null && debugSettings.LogTileVisualCreationSummary)
            {
                Debug.Log($"[TileVisualManager] Creating tiles for {room.id}: {room.width}x{room.height} = {room.width * room.height} potential tiles");
            }

            int createdCount = 0;
            int skippedAirCount = 0;
            int nullTileCount = 0;

            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    Vector2Int coord = new Vector2Int(x, y);
                    TileData tile = room.GetTileAtCoord(coord);

                    if (tile == null)
                    {
                        nullTileCount++;
                        continue;
                    }

                    if (ShouldRenderTile(tile))
                    {
                        CreateTile(tile);
                        createdCount++;
                    }
                    else
                    {
                        skippedAirCount++;
                    }
                }
            }

            if (debugSettings != null && debugSettings.LogTileVisualCreationSummary)
            {
                Debug.Log($"[TileVisualManager] Tile creation complete: {createdCount} created, {skippedAirCount} air skipped, {nullTileCount} null tiles");
            }
        }

        /// <summary>
        /// Create a single tile GameObject.
        /// </summary>
        public TileRenderer CreateTile(TileData tile)
        {
            if (tile == null)
                return null;

            // Check if already exists
            if (tileRenderers.ContainsKey(tile.gridPosition))
            {
                return tileRenderers[tile.gridPosition];
            }

            // Create GameObject
            GameObject tileObj = new GameObject($"Tile_{tile.gridPosition.x}_{tile.gridPosition.y}");
            tileObj.transform.SetParent(tileParent);

            // Add renderer component
            TileRenderer renderer = tileObj.AddComponent<TileRenderer>();
            TileVisualData visualData = ResolveVisualData(tile);
            renderer.Initialize(tile, assetDatabase, visualData);
            renderer.SetSortingLayers(baseSortingLayerName, frontSortingLayerName);
            renderer.SetSortingOrderOffset(sortingOrderOffset);
            renderer.SetColorTint(tintColor);
            renderer.SetSecondaryColorTint(secondaryTintColor);

            // Add collider component for solid tiles
            if (tile.physicsType != TilePhysicsType.Air)
            {
                TileCollider collider = tileObj.AddComponent<TileCollider>();
                collider.Initialize(tile, debugSettings);
            }

            // Store reference
            tileRenderers[tile.gridPosition] = renderer;

            return renderer;
        }

        /// <summary>
        /// Update a single tile's visuals.
        /// </summary>
        public void UpdateTile(Vector2Int coord)
        {
            TileData tile = room.GetTileAtCoord(coord);
            if (tile == null)
                return;

            bool shouldRender = ShouldRenderTile(tile);
            if (!tileRenderers.ContainsKey(coord))
            {
                if (shouldRender)
                {
                    CreateTile(tile);
                }
                return;
            }

            TileRenderer renderer = tileRenderers[coord];
            TileVisualData visualData = ResolveVisualData(tile);
            renderer.UpdateSprite(assetDatabase, visualData);
            renderer.SetSortingLayers(baseSortingLayerName, frontSortingLayerName);
            renderer.SetSortingOrderOffset(sortingOrderOffset);
            renderer.SetColorTint(tintColor);
            renderer.SetSecondaryColorTint(secondaryTintColor);
            renderer.UpdateVisuals();

            // Handle destroyed tiles
            if (tile.IsDestroyed() || !shouldRender)
            {
                DestroyTile(coord);
            }
        }

        /// <summary>
        /// Destroy a single tile GameObject.
        /// </summary>
        public void DestroyTile(Vector2Int coord)
        {
            if (!tileRenderers.ContainsKey(coord))
                return;

            TileRenderer renderer = tileRenderers[coord];
            if (renderer != null)
            {
                renderer.DestroyTile();
            }

            tileRenderers.Remove(coord);
        }

        /// <summary>
        /// Destroy all tile GameObjects.
        /// </summary>
        public void DestroyAllTiles()
        {
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    Object.DestroyImmediate(kvp.Value.gameObject);
                }
            }

            tileRenderers.Clear();
        }

        /// <summary>
        /// Get renderer for a tile.
        /// </summary>
        public TileRenderer GetTileRenderer(Vector2Int coord)
        {
            if (tileRenderers.ContainsKey(coord))
            {
                return tileRenderers[coord];
            }
            return null;
        }

        /// <summary>
        /// Set visibility for all tiles.
        /// </summary>
        public void SetAllVisible(bool visible)
        {
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetVisible(visible);
                }
            }
        }

        /// <summary>
        /// Flash a tile (for damage indication).
        /// </summary>
        public void FlashTile(Vector2Int coord, Color color, float duration = 0.1f)
        {
            TileRenderer renderer = GetTileRenderer(coord);
            if (renderer != null)
            {
                renderer.Flash(color, duration);
            }
        }

        /// <summary>
        /// Get total number of rendered tiles.
        /// </summary>
        public int GetTileCount()
        {
            return tileRenderers.Count;
        }

        /// <summary>
        /// Update all tiles (call after tile data changes).
        /// </summary>
        public void UpdateAllTiles()
        {
            List<Vector2Int> coordsToUpdate = new List<Vector2Int>(tileRenderers.Keys);

            foreach (var coord in coordsToUpdate)
            {
                UpdateTile(coord);
            }
        }

        /// <summary>
        /// Refresh tile sprites (useful after changing asset database).
        /// </summary>
        public void RefreshSprites()
        {
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    TileVisualData visualData = ResolveVisualData(kvp.Value.TileData);
                    kvp.Value.UpdateSprite(assetDatabase, visualData);
                    kvp.Value.SetSortingLayers(baseSortingLayerName, frontSortingLayerName);
                    kvp.Value.SetSortingOrderOffset(sortingOrderOffset);
                    kvp.Value.SetColorTint(tintColor);
                    kvp.Value.SetSecondaryColorTint(secondaryTintColor);
                }
            }
        }

        public void SetSortingOrderOffset(int offset)
        {
            sortingOrderOffset = offset;
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetSortingOrderOffset(offset);
                }
            }
        }

        public void SetTintColor(Color color)
        {
            tintColor = color;
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetColorTint(color);
                }
            }
        }

        public void SetSecondaryTintColor(Color color)
        {
            secondaryTintColor = color;
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetSecondaryColorTint(color);
                }
            }
        }

        public void SetSortingLayers(string baseLayerName, string frontLayerName = null)
        {
            baseSortingLayerName = string.IsNullOrWhiteSpace(baseLayerName) ? MapSortingLayers.MainTiles : baseLayerName;
            frontSortingLayerName = string.IsNullOrWhiteSpace(frontLayerName) ? baseSortingLayerName : frontLayerName;
            foreach (var kvp in tileRenderers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetSortingLayers(baseSortingLayerName, frontSortingLayerName);
                }
            }
        }

        private bool ShouldRenderTile(TileData tile)
        {
            if (tile == null)
            {
                return false;
            }

            if (tile.physicsType == TilePhysicsType.Air && compositor != null && HasBackGraphic(tile))
            {
                return true;
            }

            return tile.physicsType != TilePhysicsType.Air && ResolveVisualData(tile).HasAnySprite();
        }

        private bool HasBackGraphic(TileData tile)
        {
            return tile != null && !string.IsNullOrEmpty(tile.GetBackGraphic());
        }

        private TileVisualData ResolveVisualData(TileData tile)
        {
            TileVisualData visualData = new TileVisualData
            {
                MainSprite = ResolveFrontSprite(tile),
                BackSprite = ResolveBackSprite(tile),
                HeightScale = Mathf.Clamp01(1f - tile.heightLevel * 0.25f)
            };

            AssignOverlaySprites(tile, visualData);
            return visualData;
        }

        private Sprite ResolveFrontSprite(TileData tile)
        {
            if (tile == null)
            {
                return null;
            }

            Sprite sprite = null;
            string frontGraphic = tile.GetFrontGraphic();
            if (!string.IsNullOrEmpty(frontGraphic) && compositor != null)
            {
                sprite = compositor.GetFrontTileSprite(
                    frontGraphic,
                    tile.gridPosition.x,
                    tile.gridPosition.y,
                    tile.kontur1,
                    tile.kontur2,
                    tile.kontur3,
                    tile.kontur4);
            }

            bool hasOverlayVisual = tile.visualId > 0 || tile.visualId2 > 0;
            if (sprite == null && tile.physicsType != TilePhysicsType.Air && (!hasOverlayVisual || tile.physicsType == TilePhysicsType.Wall))
            {
                sprite = assetDatabase != null ? assetDatabase.GetDefaultSprite(tile.physicsType) : null;
            }

            return sprite;
        }

        private Sprite ResolveBackSprite(TileData tile)
        {
            if (!HasBackGraphic(tile) || compositor == null)
            {
                return null;
            }

            return compositor.GetBackTileSprite(
                tile.GetBackGraphic(),
                tile.gridPosition.x,
                tile.gridPosition.y,
                tile.pontur1,
                tile.pontur2,
                tile.pontur3,
                tile.pontur4);
        }

        private void AssignOverlaySprites(TileData tile, TileVisualData visualData)
        {
            if (tile == null || assetDatabase == null)
            {
                return;
            }

            Sprite overlay1 = assetDatabase.GetOverlaySprite(tile, tile.visualId);
            Sprite overlay2 = assetDatabase.GetOverlaySprite(tile, tile.visualId2);

            if (tile.vidRear)
            {
                visualData.RearOverlaySprite1 = overlay1;
            }
            else
            {
                visualData.FrontOverlaySprite1 = overlay1;
            }

            if (tile.vid2Rear)
            {
                visualData.RearOverlaySprite2 = overlay2;
            }
            else
            {
                visualData.FrontOverlaySprite2 = overlay2;
            }
        }
    }
}

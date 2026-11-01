using UnityEngine;
using System.Collections.Generic;
using PFE.Core;
using Profiler = PFE.Core.Profiling.PfeProfiler;
//using TileCollider;
namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Manages visual GameObjects for tiles in a room.
    /// Handles creation, pooling, updating, and destruction of tile sprites.
    ///
    /// <para><b>This is the fallback path now.</b> A room is drawn by the quarter renderer by default
    /// (<c>RoomTerrainRenderer</c>) and its tile objects are built collider-only by
    /// <see cref="TileCollisionBuilder"/>, which is what removes the compositing cost from the default
    /// path. This class still runs when the quarter renderer cannot be built, and it is the A/B control
    /// that proves the new picture is right, so it is kept whole rather than deleted.</para>
    /// </summary>
    public class TileVisualManager : ITileOwner
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
        /// The sprites of every tile this manager owns are hidden, while the tile objects — and so their
        /// colliders — stay.
        ///
        /// <para>Set by <c>RoomVisualController</c> when another renderer (the quarter terrain renderer)
        /// is drawing the room's terrain. Remembered rather than applied once, because
        /// <see cref="CreateTile"/> keeps creating tile objects after the initial build
        /// (<see cref="UpdateTile"/> creates one for a tile that has just become renderable), and a tile
        /// created after the switch would otherwise appear in the old renderer's picture on top of the
        /// new one.</para>
        /// </summary>
        private bool visualsSuppressed;

        /// <summary>
        /// The room this manager renders.
        ///
        /// <para>Exposed so a per-tile <see cref="TileCollider"/> can route its mutation
        /// notification through the room — the shared object both the visual side and the derived
        /// geometry side already agree on. The collider holds no query service, so
        /// <c>ITileQueryService.NotifyTilesMutated</c> is not reachable from there.</para>
        /// </summary>
        public RoomInstance Room => room;

        /// <inheritdoc />
        public TileDecalLayer Decals { get; set; }

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

            // Coalesce the tile atlas uploads for the whole room into one Apply() per atlas. Without
            // this every generated tile would re-upload the full 2048x2048 texture it lives in.
            // try/finally because a stuck bake depth would leave later rooms with sprites that were
            // never uploaded, i.e. invisible tiles.
            compositor?.BeginTileBake();
            try
            {
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

                        TileVisualData visualData = null;
                        bool renderTile;
                        using (Profiler.Region("tiles.shouldRender", "boot: MEASURED 0.008ms/call (2026-09-25) — was 13.6ms/call before the TileCompositor fixes. Now effectively free"))
                        {
                            renderTile = ShouldRenderTile(tile, out visualData);
                        }

                        if (renderTile)
                        {
                            CreateTile(tile, visualData);
                            createdCount++;
                        }
                        else
                        {
                            skippedAirCount++;
                        }
                    }
                }
            }
            finally
            {
                compositor?.EndTileBake();
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
            return CreateTile(tile, null);
        }

        /// <summary>
        /// Create a single tile GameObject, reusing already-resolved visual data when supplied.
        /// </summary>
        public TileRenderer CreateTile(TileData tile, TileVisualData visualData)
        {
            if (tile == null)
                return null;

            // Check if already exists
            if (tileRenderers.ContainsKey(tile.gridPosition))
            {
                return tileRenderers[tile.gridPosition];
            }

            // Create GameObject
            GameObject tileObj;
            using (Profiler.Region("tiles.createTile.go", "boot: per-tile GameObject + parent, ~1136 calls"))
            {
                tileObj = new GameObject($"Tile_{tile.gridPosition.x}_{tile.gridPosition.y}");
                tileObj.transform.SetParent(tileParent);
            }

            // Add renderer component
            TileRenderer renderer;
            using (Profiler.Region("tiles.createTile.renderer", "boot: per-tile TileRenderer add + Initialize + sprite assignment"))
            {
                renderer = tileObj.AddComponent<TileRenderer>();
                if (visualData == null)
                {
                    // Only reached from UpdateTile / RefreshSprites now — CreateAllTiles hands
                    // in the already-resolved data.
                    visualData = ResolveVisualData(tile);
                }
                renderer.Initialize(tile, assetDatabase, visualData);
                renderer.SetVisualsSuppressed(visualsSuppressed);
                renderer.SetSortingLayers(baseSortingLayerName, frontSortingLayerName);
                renderer.SetSortingOrderOffset(sortingOrderOffset);
                renderer.SetColorTint(tintColor);
                renderer.SetSecondaryColorTint(secondaryTintColor);
            }

            // Add collider component for solid tiles
            if (tile.physicsType != TilePhysicsType.Air)
            {
                using (Profiler.Region("tiles.createTile.collider", "boot: per-tile TileCollider add + Initialize for non-air tiles"))
                {
                    TileCollider collider = tileObj.AddComponent<TileCollider>();
                    collider.Initialize(tile, debugSettings, this);
                }
            }

            // Store reference
            tileRenderers[tile.gridPosition] = renderer;

            return renderer;
        }

        /// <summary>
        /// Hide (or restore) every tile sprite this manager owns, without destroying a single tile
        /// object.
        ///
        /// <para><b>Nothing in the game calls this any more.</b> It was how the quarter renderer's first
        /// wiring kept the room's collision alive: build the sprites, build the colliders, hide the
        /// sprites. Now a room builds <i>either</i> sprites and colliders (this manager, the fallback) or
        /// colliders alone (<see cref="TileCollisionBuilder"/>, the default), so there is nothing to
        /// suppress. Kept, and kept working, because it is the only way to change renderer inside a live
        /// room — see <see cref="TileRenderer"/>'s <c>visualsSuppressed</c> for the whole reason.</para>
        ///
        /// <para><b>Why not <c>SetActive(false)</c> on the parent.</b> That would take the
        /// <see cref="TileCollider"/>s with it — the room would lose its collision, and the first symptom
        /// would be a player walking through a wall rather than a missing picture.</para>
        /// </summary>
        public void SetVisualsSuppressed(bool suppressed)
        {
            visualsSuppressed = suppressed;

            foreach (TileRenderer renderer in tileRenderers.Values)
            {
                if (renderer != null)
                {
                    renderer.SetVisualsSuppressed(suppressed);
                }
            }
        }

        /// <summary>Whether this manager's sprites are suppressed in favour of another renderer.</summary>
        public bool VisualsSuppressed => visualsSuppressed;

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
                    if (Application.isPlaying)
                    {
                        Object.Destroy(kvp.Value.gameObject);
                    }
                    else
                    {
                        Object.DestroyImmediate(kvp.Value.gameObject);
                    }
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
            return ShouldRenderTile(tile, out _);
        }

        /// <summary>
        /// Resolves the tile's visual data once and reports whether it should be rendered.
        /// The resolved data is handed to CreateTile so ResolveVisualData is not run a second
        /// time — it used to be called here and again inside CreateTile for every tile.
        /// </summary>
        private bool ShouldRenderTile(TileData tile, out TileVisualData visualData)
        {
            visualData = null;

            if (tile == null)
            {
                return false;
            }

            if (tile.physicsType == TilePhysicsType.Air)
            {
                // An air tile still needs a renderer whenever it carries ANY visual. The old
                // test only accepted a back graphic, which silently dropped every overlay that
                // does not promote physics off Air — and slopes are exactly that: TileDecoder
                // sets slopeType for diagon but never raises physicsType, so "_В"/"_Г"/"_Й" stay
                // Air with an empty back graphic. 2398 tiles across the 557 shipped rooms are
                // slope-on-air, and all of them rendered as nothing at all (the cell showed the
                // dark room backdrop plus the tile's own baked shadow, which reads as a black
                // hole). Stairs and shelves never hit this because they do promote to
                // Stair/Platform.
                bool hasAnyVisual = HasBackGraphic(tile)
                    || !string.IsNullOrEmpty(tile.GetFrontGraphic())
                    || tile.visualId > 0
                    || tile.visualId2 > 0;

                if (!hasAnyVisual)
                {
                    return false;
                }

                // The slope/stair overlay lives on a child renderer, so it is only reachable
                // through the resolved data. Plain air has none of the above and still bails
                // out early, so the common case costs nothing extra.
            }

            visualData = ResolveVisualData(tile);
            return visualData.HasAnySprite();
        }

        private bool HasBackGraphic(TileData tile)
        {
            return tile != null && !string.IsNullOrEmpty(tile.GetBackGraphic());
        }

        private TileVisualData ResolveVisualData(TileData tile)
        {
            Sprite main;
            using (Profiler.Region("tiles.resolve.front", "boot: MEASURED 999ms / 1136 calls (2026-09-25). Of that, tiles.generateFront is 963ms over 508 misses"))
            {
                main = ResolveFrontSprite(tile);
            }

            Sprite back;
            using (Profiler.Region("tiles.resolve.back", "boot: MEASURED 1029ms / 1136 calls (2026-09-25). Of that, tiles.generateBack is 995ms over 614 misses"))
            {
                back = ResolveBackSprite(tile);
            }

            TileVisualData visualData = new TileVisualData
            {
                MainSprite = main,
                BackSprite = back,
                HeightScale = Mathf.Clamp01(1f - tile.heightLevel * 0.25f)
            };

            using (Profiler.Region("tiles.resolve.overlay", "boot: assetDatabase.GetOverlaySprite x2 — measured ~2ms total, believed trivial"))
            {
                AssignOverlaySprites(tile, visualData);
            }
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

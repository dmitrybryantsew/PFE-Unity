using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Core;
using PFE.Data.Definitions;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Adds and manages Unity Collider2D components for tiles.
    /// Attached to tile GameObjects alongside TileRenderer.
    ///
    /// Also implements <see cref="IDestructibleTile"/> — see P0 Defect 3. This component
    /// already sits on every non-air tile GameObject, which is precisely the object
    /// Projectile.cs resolves via GetComponent&lt;IDestructibleTile&gt;(), so implementing
    /// the interface here wires destruction up with no extra components and no manual
    /// per-room setup.
    /// </summary>
    [RequireComponent(typeof(TileRenderer))]
    public class TileCollider : MonoBehaviour, IDestructibleTile
    {
        [Header("Tile Data")]
        [SerializeField] private TileData tileData;
        [SerializeField] private Collider2D tileCollider;
        private PfeDebugSettings debugSettings;

        // The manager that built this tile. Needed so destruction can go through
        // TileVisualManager.DestroyTile, which also drops the tile from its
        // `tileRenderers` registry. Not a Unity object, so not serialized.
        private TileVisualManager owner;

        // Layer constants
        private const int LAYER_GROUND = 6;
        private const int LAYER_PLATFORM = 7;

        /// <summary>
        /// Initialize the tile collider based on tile data.
        /// </summary>
        public void Initialize(TileData data, PfeDebugSettings debugSettings = null,
                               TileVisualManager owner = null)
        {
            tileData = data;
            this.debugSettings = debugSettings;

            // RefreshCollider() re-enters here with no owner; keep the one we already have.
            if (owner != null)
            {
                this.owner = owner;
            }
            
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

        #region IDestructibleTile

        /// <summary>
        /// P0-4. Damages this tile by <paramref name="destroyAmount"/>.
        ///
        /// Damage semantics ported verbatim from AS3 <c>Tile.udar(param1:int)</c>:
        /// <code>
        ///   if (indestruct || thre &gt; param1) return false;
        ///   hp -= param1;
        /// </code>
        /// That is: indestructible tiles ignore it, damage below <c>thre</c> is ignored
        /// entirely (no chip damage), otherwise HP is reduced by the full amount.
        ///
        /// AS3 has NO material-resistance or damage-type modifier in this path —
        /// <c>Tile.mat</c> is not consulted by <c>udar</c>. So neither is implemented here.
        /// The "Explosive ignores Metal resistance" wording on IDestructibleTile is
        /// speculative API design, not original behaviour. See REPLICA_BEHAVIOR_CONTRACT.
        /// </summary>
        public void ApplyDestruction(Vector3 worldPosition, float destroyAmount, DamageType damageType)
        {
            DamageThisTile(destroyAmount);
        }

        /// <summary>
        /// P0-4. Damages this tile as part of an area explosion.
        ///
        /// This deliberately does NOT iterate a radius. Projectile.Detonate() already
        /// enumerates every tile inside the blast with Physics2D.OverlapCircleAll and
        /// calls this once per tile, so the caller owns the radius filter. Applying a
        /// full area of damage here would multiply the damage by the number of
        /// overlapping tiles.
        /// </summary>
        public void ApplyDestructionRadius(Vector3 worldPosition, float radius,
                                           float destroyAmount, DamageType damageType)
        {
            DamageThisTile(destroyAmount);
        }

        private void DamageThisTile(float destroyAmount)
        {
            if (tileData == null) return;

            // AS3 udar() takes an int.
            int damage = Mathf.RoundToInt(destroyAmount);
            if (damage <= 0) return;

            if (!tileData.TakeDamage(damage)) return;   // indestructible, or below threshold
            if (!tileData.IsDestroyed()) return;        // chipped but standing

            DestroyThisTile();
        }

        private void DestroyThisTile()
        {
            // Mirrors AS3 Tile.die(): phis=0, opac=0, visuals cleared.
            tileData.Destroy();

            // Drop the collider immediately so projectiles and units pass through.
            // Without this the tile stays solid even though it renders as destroyed.
            if (tileCollider != null)
            {
                if (Application.isPlaying)
                    Destroy(tileCollider);
                else
                    DestroyImmediate(tileCollider);
                tileCollider = null;
            }

            // Route through the owning manager when there is one: TileVisualManager.DestroyTile
            // also removes the tile from its `tileRenderers` registry. Calling
            // TileRenderer.DestroyTile() directly leaves a stale entry pointing at a GameObject
            // that destroys itself 0.5 s later, so anything iterating the registry (UpdateVisuals,
            // RefreshSprites) can touch a destroyed renderer.
            if (owner != null)
            {
                owner.DestroyTile(tileData.gridPosition);
            }
            else
            {
                // Fallback for tiles created outside TileVisualManager (tests, editor preview).
                TileRenderer renderer = GetComponent<TileRenderer>();
                if (renderer != null)
                {
                    renderer.DestroyTile();
                }
            }

            // The tile grid is authoritative and read live, so no *collision* cache needs
            // invalidating. Two other things are stale:
            //
            //   1. Derived geometry — the LowLevelPhysics2D chain mirror still describes this tile
            //      as solid, and the run it belonged to has changed shape. Routed below.
            //   2. The kontur (edge) data of the surrounding tiles — their visible borders still
            //      assume this tile is solid. TODO(P2): recompute via KonturCalculator.
            //
            // Deliberately NOT via ITileQueryService.NotifyTilesMutated: this component holds no
            // query service, and constructing one here would MarkDirty a throwaway instance that
            // nobody reads. The room is the object both the visual side and the derived-geometry
            // side already share.
            //
            // The region carries a one-tile border. Removing a tile splits the run it belonged to
            // and can expose new faces on its neighbours, so the affected area is never just the
            // tile itself — a listener that rebuilt only the exact coordinate would leave stale
            // geometry one tile out.
            RoomInstance room = owner != null ? owner.Room : null;
            if (room != null)
            {
                Vector2Int coord = tileData.gridPosition;
                room.NotifyTilesMutated(new RectInt(coord.x - 1, coord.y - 1, 3, 3));
            }
        }

        #endregion

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

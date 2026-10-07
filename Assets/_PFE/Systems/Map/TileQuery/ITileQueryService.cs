using UnityEngine;
using PFE.Core;
using PFE.Systems.Map.Serialization;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Selects which tile-query implementation is bound by VContainer.
    /// Intended to be driven by <c>PfeDebugSettings</c>. This enum exists so the choice is made
    /// in exactly ONE place (the DI registration) instead of being scattered through consumers.
    /// </summary>
    public enum TileQueryBackend
    {
        /// <summary>
        /// Reserved for the Box2D v3 / LowLevelPhysics2D chain-shape backend. Not implemented yet;
        /// it is the "new" side of the Stage B dual-run in
        /// docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md.
        /// </summary>
        Chain = 1,

        /// <summary>
        /// Unified tile collision service (P2). The only implementation: reconciled constants
        /// (AS3 ddy=1, porog=10, maxdelta=9) and full model coverage.
        /// </summary>
        Unified = 2,
    }

    /// <summary>
    /// Per-query modifiers. Groups the arguments that AS3 passed positionally.
    /// </summary>
    public readonly struct TileQueryOptions
    {
        /// <summary>AS3 <c>transT</c>. When true the entity passes through stairs.</summary>
        public readonly bool IsTransparent;

        /// <summary>AS3 <c>throu</c> / <c>t_throw</c>. When true the entity drops through one-way platforms.</summary>
        public readonly bool CanFallThroughPlatforms;

        /// <summary>Vertical velocity in pixels per 30Hz frame. Used for one-way platform resolution.</summary>
        public readonly float VelocityY;

        public TileQueryOptions(bool isTransparent = false, bool canFallThroughPlatforms = false, float velocityY = 0f)
        {
            IsTransparent = isTransparent;
            CanFallThroughPlatforms = canFallThroughPlatforms;
            VelocityY = velocityY;
        }

        public static TileQueryOptions Default => new TileQueryOptions();
    }

    /// <summary>
    /// Result of a tile raycast.
    /// </summary>
    public readonly struct TileRaycastHit
    {
        /// <summary>The tile that was hit. Null when the hit came from a non-tile source.</summary>
        public readonly TileData Tile;

        /// <summary>Hit position, in world pixels.</summary>
        public readonly Vector2 Point;

        public TileRaycastHit(TileData tile, Vector2 point)
        {
            Tile = tile;
            Point = point;
        }
    }

    /// <summary>
    /// The minimal payload needed to replicate one changed tile: where it is, whether it still
    /// blocks, and how much HP it has left.
    ///
    /// Deliberately much smaller than <see cref="TileStateSnapshot"/>, which also carries graphics
    /// ids and strings (frontGraphic, backGraphic, doorId, trapId). Those never change at runtime,
    /// so they belong in the one-time full sync, not in the per-tick delta stream.
    /// </summary>
    public readonly struct TileMutation
    {
        /// <summary>Room-local tile coordinate.</summary>
        public readonly Vector2Int Coord;

        /// <summary>Current physics type. Air means the tile was destroyed.</summary>
        public readonly TilePhysicsType PhysicsType;

        /// <summary>Remaining hit points.</summary>
        public readonly int HitPoints;

        public TileMutation(Vector2Int coord, TilePhysicsType physicsType, int hitPoints)
        {
            Coord = coord;
            PhysicsType = physicsType;
            HitPoints = hitPoints;
        }
    }

    /// <summary>
    /// The single seam every tile-collision consumer depends on.
    ///
    /// Purpose: today three models disagree about what "solid" means and even about which
    /// coordinate space they operate in:
    ///   1. <c>TilePhysicsController</c>  - private sampling, own porog/step-up, world pixels
    ///   2. <c>TileCollisionSystem</c>    - world pixels (subtracts room origin)
    ///   3. <c>RoomInstance.CheckCollision</c> - ROOM-LOCAL pixels (does NOT subtract room origin)
    /// Consumers should depend on this interface and contain no backend-specific branching.
    ///
    /// COORDINATE SPACE: every method below takes and returns WORLD PIXELS unless the parameter
    /// is explicitly named <c>tileCoord</c>. Note this is a CHANGE for existing
    /// <c>RoomInstance.CheckCollision</c> callers, which are room-local today. Migrating them is
    /// P2 work and is a behaviour change, so it must not happen inside Stage A.
    ///
    /// See docs/Roadmap/03_P2_UNIFIED_TILE_COLLISION.md.
    /// </summary>
    public interface ITileQueryService
    {
        /// <summary>Which implementation this is. Diagnostic only; never branch on it in gameplay code.</summary>
        [LocalOnly]
        TileQueryBackend Backend { get; }

        /// <summary>The room this service queries.</summary>
        [LocalOnly]
        RoomInstance Room { get; }

        /// <summary>
        /// This room's origin in <b>world pixels</b> — the offset between room-local pixels and the
        /// world pixels every other method here expects. Add it to a room-local pixel coordinate to
        /// get the world coordinate of the same point.
        ///
        /// <para>Zero for a room at land position (0,0) with no border, which is exactly why a
        /// consumer that forgets it looks correct in every test built on an origin room. Derived
        /// geometry — the LowLevelPhysics2D chain mirror — MUST add it, or a room one land step away
        /// from the origin has its whole mirror displaced by one room width.</para>
        /// </summary>
        [LocalOnly]
        Vector2 OriginPixel { get; }

        // ── Queries. Computed locally on every machine; never transmitted. ──────────────────

        /// <summary>Primitive solidity test at a room-local tile coordinate.</summary>
        [LocalOnly(Note = "Pure function of local tile state.")]
        bool IsSolidAt(Vector2Int tileCoord);

        /// <summary>AABB overlap test against tiles. Bounds in world pixels.</summary>
        [LocalOnly]
        bool CheckCollision(Rect boundsPx, TileQueryOptions options);

        /// <summary>Surface height under a point, in world pixels. Handles slopes and stairs.</summary>
        [LocalOnly]
        float GetGroundHeight(Vector2 positionPx);

        /// <summary>True when the bounds are resting on ground. Maps to the AS3 <c>stay</c> flag.</summary>
        [LocalOnly]
        bool IsOnGround(Rect boundsPx);

        /// <summary>First tile hit along a ray. Origin, direction and distance in world pixels.</summary>
        [LocalOnly]
        TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx);

        /// <summary>Canonical classification of a single tile coordinate.</summary>
        [LocalOnly(Note = "Pure function of local tile state.")]
        TileQueryFlags Classify(Vector2Int tileCoord);

        /// <summary>
        /// The AS3 surface identity of a single tile coordinate — the "kind" half of the
        /// classification, deliberately kept out of <see cref="TileQueryFlags"/>.
        ///
        /// <para>This is the tag each consumer predicates over: a projectile collides with
        /// <see cref="SurfaceKind.Solid"/> only, a dynamic prop with <see cref="SurfaceKind.Solid"/>
        /// and <see cref="SurfaceKind.Shelf"/>, and neither with <see cref="SurfaceKind.Diagon"/> or
        /// <see cref="SurfaceKind.Stair"/>. One classification, one predicate per consumer — instead
        /// of one shared <c>CheckCollision</c> whose answer has to be right for all of them at once.</para>
        ///
        /// <para>Delegates to <see cref="SurfaceKindRule.Of"/> so the seam and any pure predicate
        /// (e.g. <see cref="PropCollisionRule"/>) read one definition and cannot drift.</para>
        /// </summary>
        [LocalOnly(Note = "Pure function of local tile state.")]
        SurfaceKind ClassifySurface(Vector2Int tileCoord);

        /// <summary>
        /// Swept move of an AABB (pixel space) by delta, subdivided by maxdelta (9px).
        /// Returns final position, collision flags, and remainder. Deterministic.
        /// </summary>
        [LocalOnly]
        TileMoveResult ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask);

        // ── Mutations. Host-only. ──────────────────────────────────────────────────────────

        /// <summary>
        /// Applies damage in a tile-radius around a point, destroying tiles that reach zero HP.
        /// Returns true when anything was damaged.
        /// Implementations that cache derived geometry MUST refresh the affected region here,
        /// and MUST record the affected tiles in the pending mutation set.
        /// </summary>
        [Authoritative(Note = "Destruction changes world state; only the host may originate it.")]
        bool ApplyDamage(Vector2 positionPx, int damage, int radiusTiles = 1);

        /// <summary>
        /// Informs the service that tiles changed by some path other than <see cref="ApplyDamage"/>
        /// (for example direct <c>TileData.Destroy()</c> calls). Tile region is in ROOM-LOCAL tile
        /// coordinates. Backends holding derived geometry rebuild the affected chunks here, and
        /// every implementation MUST record the region in the pending mutation set.
        /// </summary>
        [Authoritative]
        void NotifyTilesMutated(RectInt tileRegion);

        // ── Replication plumbing (P4). Marked LocalOnly: this is transport, not Sim state. ──

        /// <summary>
        /// Full mutable state of every tile in the room. Used for reconciliation when a client's
        /// generated room cannot be assumed identical to the host's (see remarks).
        ///
        /// In the common case this is NOT needed on the wire: both sides generate rooms from the
        /// same <c>RoomTemplate</c>, so the base grid is already known and only
        /// <see cref="DrainMutations"/> needs sending. It exists as the safe fallback for when
        /// generation diverges (unseeded RNG today - see P3).
        /// </summary>
        [LocalOnly(Note = "Replication transport, not authoritative state.")]
        TileStateSnapshot[] CaptureFullState();

        /// <summary>Overwrites local tile state from a host snapshot.</summary>
        [LocalOnly]
        void ApplyFullState(TileStateSnapshot[] snapshot);

        /// <summary>
        /// Removes and returns the tiles mutated since the last call, then clears the set.
        /// Empty array when nothing changed. This is the per-tick delta stream.
        /// </summary>
        [LocalOnly]
        TileMutation[] DrainMutations();

        /// <summary>
        /// Applies host-authored mutations locally. Implementations MUST rebuild any derived
        /// geometry for the affected tiles, exactly as <see cref="NotifyTilesMutated"/> would.
        /// </summary>
        [LocalOnly]
        void ApplyMutations(TileMutation[] mutations);
    }
}

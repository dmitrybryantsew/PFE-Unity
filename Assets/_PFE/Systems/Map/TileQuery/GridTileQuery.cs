using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Physics;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// AS3-faithful tile query backed by the motor's collision math (<see cref="TileCollisionMath"/>).
    ///
    /// STRANGLER STAGE A1: every query delegates to the existing <see cref="TilePhysicsController"/>
    /// algorithm (via <see cref="TileCollisionMath"/>) and must return bit-identical results to the motor.
    /// Divergence between the models is P2 work resolved behind a diff harness in Stage B.
    ///
    /// This is the Replica reference implementation: golden traces are recorded against it.
    ///
    /// The mutation set added here (for replication) is purely observational: it records which
    /// tiles changed. It never alters a query result.
    /// </summary>
    public sealed class GridTileQuery : ITileQueryService
    {
        private readonly TileCollisionSystem _inner;
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();

        public TileQueryBackend Backend => TileQueryBackend.Grid;

        public RoomInstance Room { get; }

        public GridTileQuery(RoomInstance room)
        {
            Room = room;
            _inner = new TileCollisionSystem(room);
        }

        // ── Queries (delegating to TileCollisionMath Model 1) ──────────────────────────────

        public bool IsSolidAt(Vector2Int tileCoord)
        {
            return TileCollisionMath.IsSolidAt(Room, tileCoord);
        }

        public bool CheckCollision(Rect boundsPx, TileQueryOptions options)
        {
            Vector2 origin = RoomOriginPixel;
            return TileCollisionMath.CheckCollision(
                Room,
                boundsPx,
                origin.x, origin.y,
                platformThreshold: 8f,
                options.IsTransparent,
                options.CanFallThroughPlatforms,
                options.VelocityY);
        }

        public float GetGroundHeight(Vector2 positionPx)
        {
            Vector2 origin = RoomOriginPixel;
            return TileCollisionMath.GetGroundHeight(
                Room,
                positionPx.x, positionPx.y,
                origin.x, origin.y);
        }

        public bool IsOnGround(Rect boundsPx)
        {
            Vector2 origin = RoomOriginPixel;
            return TileCollisionMath.IsOnGround(
                Room,
                boundsPx,
                origin.x, origin.y,
                platformThreshold: 8f);
        }

        public TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
        {
            (TileData tile, Vector2 point)? hit = _inner.Raycast(originPx, direction, maxDistancePx);
            if (!hit.HasValue) return null;
            return new TileRaycastHit(hit.Value.tile, hit.Value.point);
        }

        public TileQueryFlags Classify(Vector2Int tileCoord)
        {
            if (Room == null || Room.tiles == null) return TileQueryFlags.None;
            TileData tile = Room.GetTileAtCoord(tileCoord);
            if (tile == null) return TileQueryFlags.None;

            TileQueryFlags flags = TileQueryFlags.None;
            if (tile.physicsType == TilePhysicsType.Wall) flags |= TileQueryFlags.Solid;
            if (tile.physicsType == TilePhysicsType.Platform) flags |= TileQueryFlags.Platform;
            if (tile.IsClimbableLadder()) flags |= TileQueryFlags.Ladder;
            if (tile.hasWater) flags |= TileQueryFlags.Water;
            if (tile.IsSlopeSurface()) flags |= TileQueryFlags.Slope;
            if (!tile.indestructible && tile.hitPoints > 0) flags |= TileQueryFlags.Destructible;

            return flags;
        }

        public TileMoveResult ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask)
        {
            float maxDist = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
            int subSteps = TileCollisionMath.CalculateSubSteps(maxDist, TileQueryConstants.MaxDelta);
            Vector2 stepDelta = delta / subSteps;

            Vector2 currentCenter = box.Center;
            float hw = box.HalfSize.x;
            float hh = box.Height;
            Vector2 origin = RoomOriginPixel;

            bool hitCeiling = false;
            bool hitFloor = false;
            bool hitLeft = false;
            bool hitRight = false;
            bool landedOnPlatform = false;

            bool checkPlatform = (mask & TileQueryFlags.Platform) != 0 && (mask & TileQueryFlags.IgnoreOneWay) == 0;

            for (int i = 0; i < subSteps; i++)
            {
                // Horizontal step
                if (Mathf.Abs(stepDelta.x) > 0.0001f)
                {
                    float targetX = currentCenter.x + stepDelta.x;
                    float feetY = currentCenter.y - box.HalfSize.y;
                    const float ceilingInset = 1f;
                    float hhHoriz = hh - ceilingInset;

                    if (TileCollisionMath.CheckTileCollisionAt(Room, targetX, feetY, hw, hhHoriz, origin.x, origin.y))
                    {
                        if (stepDelta.x < 0f) hitLeft = true;
                        else hitRight = true;
                        currentCenter.x = TileCollisionMath.ResolveHorizontal(Room, currentCenter.x, targetX, feetY, hw, hhHoriz, origin.x, origin.y);
                    }
                    else
                    {
                        currentCenter.x = targetX;
                    }
                }

                // Vertical step
                if (Mathf.Abs(stepDelta.y) > 0.0001f)
                {
                    float targetY = currentCenter.y + stepDelta.y;
                    float targetFeetY = targetY - box.HalfSize.y;
                    float currentFeetY = currentCenter.y - box.HalfSize.y;

                    if (stepDelta.y <= 0f)
                    {
                        bool hitPlat;
                        if (TileCollisionMath.CheckGroundCollisionAt(
                            Room, currentCenter.x, targetFeetY, hw,
                            origin.x, origin.y,
                            platformThreshold: 8f,
                            canFallThrough: !checkPlatform,
                            out hitPlat))
                        {
                            hitFloor = true;
                            if (hitPlat) landedOnPlatform = true;
                            float resolvedFeetY = TileCollisionMath.ResolveVerticalDown(
                                Room, currentCenter.x, currentFeetY, targetFeetY, hw, origin.x, origin.y);
                            currentCenter.y = resolvedFeetY + box.HalfSize.y;
                        }
                        else
                        {
                            currentCenter.y = targetY;
                        }
                    }
                    else
                    {
                        if (TileCollisionMath.CheckCeilingCollisionAt(
                            Room, currentCenter.x, targetFeetY, hw, hh, origin.x, origin.y, isOnLadder: false))
                        {
                            hitCeiling = true;
                            float resolvedFeetY = TileCollisionMath.ResolveVerticalUp(
                                Room, currentCenter.x, currentFeetY, targetFeetY, hw, hh, origin.x, origin.y);
                            currentCenter.y = resolvedFeetY + box.HalfSize.y;
                        }
                        else
                        {
                            currentCenter.y = targetY;
                        }
                    }
                }
            }

            Vector2 remainder = (box.Center + delta) - currentCenter;
            return new TileMoveResult(
                currentCenter,
                remainder,
                hitCeiling,
                hitFloor,
                hitLeft,
                hitRight,
                landedOnPlatform,
                subSteps);
        }

        // ── Mutations (host-only) ──────────────────────────────────────────────────────────

        public bool ApplyDamage(Vector2 positionPx, int damage, int radiusTiles = 1)
        {
            bool damaged = _inner.ApplyDamage(positionPx, damage, radiusTiles);

            // Record the affected square so the delta stream can carry it. Conservative: it marks
            // the whole queried square, not only tiles that actually lost HP.
            Vector2Int centre = WorldCoordinates.PixelToTile(new Vector2(
                positionPx.x - RoomOriginPixel.x,
                positionPx.y - RoomOriginPixel.y));
            MarkDirty(new RectInt(
                centre.x - radiusTiles,
                centre.y - radiusTiles,
                radiusTiles * 2 + 1,
                radiusTiles * 2 + 1));

            return damaged;
        }

        public void NotifyTilesMutated(RectInt tileRegion)
        {
            MarkDirty(tileRegion);
            // No geometry to rebuild: the grid backend reads TileData directly and cannot go stale.
            // A caching backend (see ChainTileQuery) rebuilds its chunks here instead.
        }

        // ── Replication plumbing ───────────────────────────────────────────────────────────

        public TileStateSnapshot[] CaptureFullState()
        {
            if (Room == null || Room.tiles == null) return new TileStateSnapshot[0];

            List<TileStateSnapshot> all = new List<TileStateSnapshot>(Room.width * Room.height);
            for (int x = 0; x < Room.width; x++)
            {
                for (int y = 0; y < Room.height; y++)
                {
                    TileData tile = Room.tiles[x, y];
                    if (tile == null) continue;
                    all.Add(TileStateSnapshot.CreateFrom(tile));
                }
            }
            return all.ToArray();
        }

        public void ApplyFullState(TileStateSnapshot[] snapshot)
        {
            if (Room == null || Room.tiles == null || snapshot == null) return;

            foreach (TileStateSnapshot entry in snapshot)
            {
                if (entry == null) continue;
                TileData tile = Room.GetTileAtCoord(new Vector2Int(entry.gridX, entry.gridY));
                if (tile == null) continue;

                // Only the runtime-mutable subset is restored. Visual fields (frontGraphic,
                // backGraphic, visualId, ...) come from the RoomTemplate and are already correct
                // on both machines, so replaying them would add bytes for no benefit.
                tile.physicsType = (TilePhysicsType)entry.physicsType;
                tile.indestructible = entry.indestructible;
                tile.hitPoints = entry.hitPoints;
                tile.damageThreshold = entry.damageThreshold;
            }

            // Deliberately does NOT mark tiles dirty. Receiving state is not originating it: only
            // the [Authoritative] mutations (ApplyDamage, NotifyTilesMutated) feed the outgoing
            // delta stream. If this marked dirty, every client would echo state back to the host.
        }

        public TileMutation[] DrainMutations()
        {
            if (_dirty.Count == 0) return new TileMutation[0];

            List<TileMutation> mutations = new List<TileMutation>(_dirty.Count);
            foreach (Vector2Int coord in _dirty)
            {
                TileData tile = Room != null ? Room.GetTileAtCoord(coord) : null;
                if (tile == null) continue;
                mutations.Add(new TileMutation(coord, tile.physicsType, tile.hitPoints));
            }

            _dirty.Clear();
            return mutations.ToArray();
        }

        public void ApplyMutations(TileMutation[] mutations)
        {
            if (Room == null || mutations == null) return;

            foreach (TileMutation mutation in mutations)
            {
                TileData tile = Room.GetTileAtCoord(mutation.Coord);
                if (tile == null) continue;

                tile.physicsType = mutation.PhysicsType;
                tile.hitPoints = mutation.HitPoints;
            }

            // No derived geometry to rebuild for the grid backend. A caching backend must rebuild
            // chunks for every applied mutation here.
        }

        // ── Internals ──────────────────────────────────────────────────────────────────────

        private void MarkDirty(RectInt region)
        {
            for (int x = region.xMin; x < region.xMax; x++)
            {
                for (int y = region.yMin; y < region.yMax; y++)
                {
                    _dirty.Add(new Vector2Int(x, y));
                }
            }
        }

        /// <summary>
        /// Room origin in world pixels.
        ///
        /// TODO(P2): this duplicates the calculation in the TileCollisionSystem constructor
        /// because that value is private there. Once ITileQueryService is the single accessor,
        /// expose one shared helper and delete this copy. Until then, changing one without the
        /// other will silently mis-attribute mutations.
        /// </summary>
        private Vector2 RoomOriginPixel
        {
            get
            {
                if (Room == null) return Vector2.zero;
                int border = Room.borderOffset;
                return new Vector2(
                    Room.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE
                        - border * WorldConstants.TILE_SIZE,
                    Room.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE
                        - border * WorldConstants.TILE_SIZE);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Physics;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Unified tile collision query service (P2).
    ///
    /// Replaces the three divergent collision models (TilePhysicsController, TileCollisionSystem,
    /// RoomInstance.CheckCollision) with a single authoritative implementation that:
    ///   1. Reconciles all physical constants to AS3 (ddy=1.0, porog=10, maxdelta=9, etc.)
    ///   2. Operates strictly in world pixel coordinates (subtracts room origin internally)
    ///   3. Provides swept-move physics (ResolveMove) with sub-stepping
    ///   4. Classifies tile cells into TileQueryFlags
    ///   5. Tracks mutations for P4 replication
    /// </summary>
    public sealed class UnifiedTileQueryService : ITileQueryService
    {
        private readonly TileCollisionSystem _legacyInner;
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();

        public TileQueryBackend Backend => TileQueryBackend.Unified;

        public RoomInstance Room { get; }

        public UnifiedTileQueryService(RoomInstance room)
        {
            Room = room;
            _legacyInner = new TileCollisionSystem(room);
        }

        // ── Queries ────────────────────────────────────────────────────────────────────────

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
                platformThreshold: TileQueryConstants.PorogGrounded,
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
                platformThreshold: TileQueryConstants.PorogGrounded);
        }

        public TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
        {
            (TileData tile, Vector2 point)? hit = _legacyInner.Raycast(originPx, direction, maxDistancePx);
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
                            platformThreshold: TileQueryConstants.PorogGrounded,
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
            bool damaged = _legacyInner.ApplyDamage(positionPx, damage, radiusTiles);

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

                tile.physicsType = (TilePhysicsType)entry.physicsType;
                tile.indestructible = entry.indestructible;
                tile.hitPoints = entry.hitPoints;
                tile.damageThreshold = entry.damageThreshold;
            }
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

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
    ///
    /// <para><b>Stage D: the last of the three models it replaced is gone.</b> <c>TileCollisionSystem</c>
    /// survived as this class's inner reader for the sight ray and the radius-damage loop; both moved
    /// here verbatim, so there is now exactly one tile-query implementation in the project.</para>
    /// </summary>
    public sealed class UnifiedTileQueryService : ITileQueryService
    {
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();

        public TileQueryBackend Backend => TileQueryBackend.Unified;

        public RoomInstance Room { get; }

        public UnifiedTileQueryService(RoomInstance room)
        {
            Room = room;
        }

        // ── Queries ────────────────────────────────────────────────────────────────────────

        public bool IsSolidAt(Vector2Int tileCoord)
        {
            return TileCollisionMath.IsSolidAt(Room, tileCoord);
        }

        public bool CheckCollision(Rect boundsPx, TileQueryOptions options)
        {
            Vector2 origin = OriginPixel;
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
            Vector2 origin = OriginPixel;
            return TileCollisionMath.GetGroundHeight(
                Room,
                positionPx.x, positionPx.y,
                origin.x, origin.y);
        }

        public bool IsOnGround(Rect boundsPx)
        {
            return IsOnGround(boundsPx, TileQueryOptions.Default);
        }

        /// <summary>
        /// The <c>throu</c>-aware ground probe. Only <see cref="TileQueryOptions.CanFallThroughPlatforms"/>
        /// is consulted; see the interface's remarks for why this is an overload rather than a defaulted
        /// parameter on <see cref="IsOnGround(Rect)"/>.
        /// </summary>
        public bool IsOnGround(Rect boundsPx, TileQueryOptions options)
        {
            Vector2 origin = OriginPixel;
            return TileCollisionMath.IsOnGround(
                Room,
                boundsPx,
                origin.x, origin.y,
                platformThreshold: TileQueryConstants.PorogGrounded,
                canFallThroughPlatforms: options.CanFallThroughPlatforms);
        }

        /// <summary>
        /// The support span for the unit's <c>shX1</c>/<c>shX2</c> overhang — see the interface's remarks
        /// for why this returns a span rather than the fractions. Same coordinate convention as
        /// <see cref="IsOnGround(Rect, TileQueryOptions)"/>: world-pixel bounds in, room origin
        /// subtracted inside <c>TileCollisionMath</c>.
        /// </summary>
        public bool TryGetSupportSpan(
            Rect boundsPx,
            TileQueryOptions options,
            out float supportLeftWorldPx,
            out float supportRightWorldPx)
        {
            Vector2 origin = OriginPixel;
            return TileCollisionMath.TryGetSupportSpan(
                Room,
                boundsPx,
                origin.x, origin.y,
                platformThreshold: TileQueryConstants.PorogGrounded,
                canFallThrough: options.CanFallThroughPlatforms,
                out supportLeftWorldPx,
                out supportRightWorldPx);
        }

        /// <summary>
        /// The line-of-sight ray, AS3 <c>Unit.look()</c>'s predicate (<c>phis == 1</c>, sample inside
        /// the tile's own box). Moved verbatim from <c>TileCollisionSystem</c> — the sight question and
        /// its exact answer live here now, not in a second implementation the seam kept as an inner.
        ///
        /// <para><b>Deliberately not <c>TileData.IsSolid()</c>.</b> That helper is "blocks movement"
        /// and includes platforms and stairs; sight in the oracle is <c>phis == 1</c> exactly, so a
        /// ladder, a catwalk and a closed grate (<c>levelTwoSolid</c>) are all transparent here.</para>
        /// </summary>
        public TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
        {
            if (Room == null) return null;

            Vector2 origin = OriginPixel;
            float distance = 0f;
            Vector2 currentPos = originPx;
            Vector2 step = direction.normalized * WorldConstants.TILE_SIZE * 0.25f;

            while (distance < maxDistancePx)
            {
                // Convert world pixel coordinates to room-local tile coordinates
                Vector2 localPos = new Vector2(currentPos.x - origin.x, currentPos.y - origin.y);
                Vector2Int tileCoord = WorldCoordinates.PixelToTile(localPos);
                TileData tile = Room.GetTileAtCoord(tileCoord);

                // AS3 `if(_loc15_.phis == 1 && ...bounds...) return 0;` (Unit.as:4626) — a wall stops
                // sight outright…
                if (tile != null && tile.physicsType == TilePhysicsType.Wall && !tile.levelTwoSolid)
                {
                    // …but only if the sample point is actually INSIDE the tile's own box — AS3 tests
                    // against phX1..phX2/phY1..phY2, and phY1 is lowered by a quarter cell per zForm
                    // level. Testing the cell instead made a half-height step opaque across its whole
                    // cell.
                    Rect box = tile.GetBounds();
                    if (localPos.x >= box.xMin && localPos.x <= box.xMax &&
                        localPos.y >= box.yMin && localPos.y <= box.yMax)
                    {
                        return new TileRaycastHit(tile, currentPos);
                    }
                }

                currentPos += step;
                distance += step.magnitude;
            }

            return null;
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

        public SurfaceKind ClassifySurface(Vector2Int tileCoord)
        {
            if (Room == null || Room.tiles == null) return SurfaceKind.None;
            return SurfaceKindRule.Of(Room.GetTileAtCoord(tileCoord));
        }

        public TileMoveResult ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask,
                                          float stepUpPixels = 0f)
        {
            float maxDist = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
            int subSteps = TileCollisionMath.CalculateSubSteps(maxDist, TileQueryConstants.MaxDelta);
            Vector2 stepDelta = delta / subSteps;

            Vector2 currentCenter = box.Center;
            float hw = box.HalfSize.x;
            float hh = box.Height;
            Vector2 origin = OriginPixel;

            bool hitCeiling = false;
            bool hitFloor = false;
            bool hitLeft = false;
            bool hitRight = false;
            bool landedOnPlatform = false;
            float stepUpTotal = 0f;

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
                        // AS3 answers a blocked horizontal step with a step-up BEFORE it stops: if
                        // the obstacle's top is within `porog`, `Y = tile.phY1` and the step is
                        // allowed (`Unit.as:2131` leftward, `:2208` rightward). `stepUpPixels` is
                        // that allowance — 0 for anything that is not a walking unit, so this is a
                        // pure no-op for every other caller.
                        if (stepUpPixels > 0f &&
                            TileCollisionMath.TryStepUp(
                                Room, currentCenter.x, feetY, hw, hh, stepDelta.x, stepUpPixels,
                                origin.x, origin.y, out float steppedFeetY) &&
                            !TileCollisionMath.CheckTileCollisionAt(
                                Room, targetX, steppedFeetY, hw, hhHoriz, origin.x, origin.y))
                        {
                            stepUpTotal += steppedFeetY - feetY;
                            currentCenter.y = steppedFeetY + box.HalfSize.y;
                            currentCenter.x = targetX;
                        }
                        else
                        {
                            if (stepDelta.x < 0f) hitLeft = true;
                            else hitRight = true;
                            currentCenter.x = TileCollisionMath.ResolveHorizontal(Room, currentCenter.x, targetX, feetY, hw, hhHoriz, origin.x, origin.y);
                        }
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
                subSteps,
                stepUpTotal);
        }

        // ── Mutations (host-only) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Applies damage in a tile-radius around a point, destroying tiles that reach zero HP.
        /// Returns true when anything was damaged.
        /// Implementations that cache derived geometry MUST refresh the affected region here,
        /// and MUST record the affected tiles in the pending mutation set.
        /// </summary>
        public bool ApplyDamage(Vector2 positionPx, int damage, int radiusTiles = 1)
        {
            // Inlined from TileCollisionSystem.ApplyDamage — the port of AS3 Location.hitTile()'s
            // radius loop, so the seam is the only tile-damage implementation left. The rule is
            // TileHitRules' and nowhere else; `TileHitDecision` distinguishes "refused because the hit
            // is too weak" from "refused because this is a ladder" — neither counts as damaged.
            //
            // This path has no weapon behind it and no decal to stamp (the oracle's own default for a
            // scripted area is `Area.damTiles(..., 11)` — decal type 0); weapon hits reach decals
            // through TileDamageApplicator instead.
            bool damaged = false;
            if (Room != null)
            {
                Vector2 origin = OriginPixel;
                Vector2Int centerTile = WorldCoordinates.PixelToTile(
                    new Vector2(positionPx.x - origin.x, positionPx.y - origin.y));

                bool destroyOn = Room.environment == null || Room.environment.destroyOn;

                for (int x = centerTile.x - radiusTiles; x <= centerTile.x + radiusTiles; x++)
                {
                    for (int y = centerTile.y - radiusTiles; y <= centerTile.y + radiusTiles; y++)
                    {
                        TileData tile = Room.GetTileAtCoord(new Vector2Int(x, y));
                        TileHitDecision decision =
                            TileHitRules.Resolve(tile, damage, 0, destroyOn, Room.isActive);

                        if (decision.Outcome == TileHitOutcome.Ignored ||
                            decision.Outcome == TileHitOutcome.Refused ||
                            decision.Outcome == TileHitOutcome.VisualOnly)
                        {
                            continue;
                        }

                        damaged = true;

                        // AS3 `Tile.die()`: the tile grid is authoritative, so death is a data write;
                        // the visual side and the derived geometry both react to the notification below.
                        if (decision.ShouldDie)
                        {
                            tile.Destroy();
                        }
                    }
                }
            }

            Vector2Int centre = WorldCoordinates.PixelToTile(new Vector2(
                positionPx.x - OriginPixel.x,
                positionPx.y - OriginPixel.y));

            // Goes through NotifyTilesMutated rather than MarkDirty so the damage path and the
            // "something else changed the tiles" path have exactly one exit: mark dirty AND tell
            // the room. Two exits is how one of them ends up missing the room notification.
            NotifyTilesMutated(new RectInt(
                centre.x - radiusTiles,
                centre.y - radiusTiles,
                radiusTiles * 2 + 1,
                radiusTiles * 2 + 1));

            return damaged;
        }

        public void NotifyTilesMutated(RectInt tileRegion)
        {
            MarkDirty(tileRegion);

            // Forward to the room so listeners holding derived geometry *outside* this service
            // rebuild too — today the LowLevelPhysics2D chain mirror, owned by PhysicsWorldService.
            // MarkDirty alone only touches this instance's own dirty set, and this service is not a
            // shared instance: PhysicsWorldService news up its own, so a caller here and a listener
            // there would otherwise have no channel between them. RoomInstance is that channel.
            Room?.NotifyTilesMutated(tileRegion);
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

        /// <summary>
        /// This room's origin in world pixels. Public because it is a seam contract, not an internal
        /// detail: any consumer building derived geometry in world space needs it, and re-deriving
        /// the formula elsewhere would be a second definition of where a room is.
        /// </summary>
        public Vector2 OriginPixel
        {
            get
            {
                if (Room == null) return Vector2.zero;
                int border = Room.borderOffset;
                return new Vector2(
                    WorldCoordinates.RoomOriginPixelX(Room.landPosition.x, border),
                    WorldCoordinates.RoomOriginPixelY(Room.landPosition.y, border));
            }
        }
    }
}

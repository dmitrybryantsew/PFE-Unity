using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Pure static collision functions extracted from <see cref="TilePhysicsController"/>.
    ///
    /// STRANGLER STAGE A: These are bit-identical extractions of the motor's private collision
    /// methods. They take explicit parameters instead of reading from MonoBehaviour fields,
    /// making them callable from both the motor and <c>ITileQueryService</c> implementations.
    ///
    /// IMPORTANT: Do not "improve" logic in this file during Stage A. Every method must return
    /// the same values as the original private method it was extracted from. Divergence from
    /// AS3 (gravity 0.98, platformThreshold 8, mirrored slopes, etc.) is preserved here and
    /// fixed in Stage B behind a diff harness.
    ///
    /// See docs/Roadmap/03_P2_UNIFIED_TILE_COLLISION.md.
    /// </summary>
    public static class TileCollisionMath
    {
        // ── Coordinate helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a world-pixel X position to a room-local tile X coordinate.
        /// </summary>
        public static int WorldPixelToTileX(float worldPx, float roomWorldPixelX)
        {
            return Mathf.FloorToInt((worldPx - roomWorldPixelX) / WorldConstants.TILE_SIZE);
        }

        /// <summary>
        /// Convert a world-pixel Y position to a room-local tile Y coordinate.
        /// </summary>
        public static int WorldPixelToTileY(float worldPy, float roomWorldPixelY)
        {
            return Mathf.FloorToInt((worldPy - roomWorldPixelY) / WorldConstants.TILE_SIZE);
        }

        // ── Core collision checks ─────────────────────────────────────────────────────────

        /// <summary>
        /// Check if position overlaps any solid (Wall) tiles.
        /// Extracted from TilePhysicsController.HasSolidOverlapAt.
        ///
        /// Bounds: the AABB is built as [x-hw+ε, y+ε] to [x+hw-ε, y+hh-ε] (feet-anchored).
        /// Only Wall tiles count; platforms and stairs do not.
        /// </summary>
        public static bool HasSolidOverlapAt(
            RoomInstance room,
            float x, float y, float hw, float hh,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            if (room == null || room.tiles == null)
            {
                return false;
            }

            Rect bounds = Rect.MinMaxRect(
                x - hw + 0.01f,
                y + 0.01f,
                x + hw - 0.01f,
                y + hh - 0.01f);

            if (bounds.width <= 0f || bounds.height <= 0f)
            {
                return false;
            }

            int tileLeft = Mathf.FloorToInt((bounds.xMin - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((bounds.xMax - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((bounds.yMin - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt((bounds.yMax - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null || tile.physicsType != TilePhysicsType.Wall)
                    {
                        continue;
                    }

                    Rect tileBounds = tile.GetBounds();
                    tileBounds.position += new Vector2(roomWorldPixelX, roomWorldPixelY);
                    if (bounds.Overlaps(tileBounds))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check if an AABB at (x, y) with half-width hw and height hh collides with any Wall tile.
        /// The AABB spans [x-hw, y] to [x+hw, y+hh] (y is feet, y+hh is head).
        /// Extracted from TilePhysicsController.CheckTileCollisionAt.
        /// </summary>
        public static bool CheckTileCollisionAt(
            RoomInstance room,
            float x, float y, float hw, float hh,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            if (room == null || room.tiles == null) return false;

            float left = x - hw;
            float right = x + hw;
            float bottom = y;
            float top = y + hh;

            int tileLeft = Mathf.FloorToInt((left - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((right - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((bottom - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt((top - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    var tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile != null && tile.physicsType == TilePhysicsType.Wall)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check ground collision (walls and platforms below feet).
        /// Extracted from TilePhysicsController.CheckGroundCollisionAt.
        ///
        /// NOTE: The original method sets <c>isOnPlatform = true</c> as a side effect when a
        /// platform is hit. This static version returns the platform-hit flag via the out parameter
        /// instead. Callers must propagate the flag themselves.
        /// </summary>
        public static bool CheckGroundCollisionAt(
            RoomInstance room,
            float x, float y, float hw,
            float roomWorldPixelX, float roomWorldPixelY,
            float platformThreshold,
            bool canFallThrough,
            out bool hitPlatform)
        {
            hitPlatform = false;
            if (room == null || room.tiles == null) return false;

            float left = x - hw;
            float right = x + hw;

            int tileLeft = Mathf.FloorToInt((left - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((right - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileY = Mathf.FloorToInt((y - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                var tile = room.GetTileAtCoord(new Vector2Int(tx, tileY));
                if (tile == null) continue;

                if (tile.physicsType == TilePhysicsType.Wall)
                {
                    return true;
                }

                if (tile.physicsType == TilePhysicsType.Platform && !canFallThrough)
                {
                    float tileTop = roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;
                    if (y <= tileTop && y >= tileTop - platformThreshold)
                    {
                        hitPlatform = true;
                        return true;
                    }
                }

                if (tile.IsSlopeSurface())
                {
                    float localX = x - roomWorldPixelX;
                    float groundH = tile.GetGroundHeight(localX) + roomWorldPixelY;
                    if (y <= groundH)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check ceiling collision above head.
        /// Extracted from TilePhysicsController.CheckCeilingCollisionAt.
        /// </summary>
        public static bool CheckCeilingCollisionAt(
            RoomInstance room,
            float x, float y, float hw, float hh,
            float roomWorldPixelX, float roomWorldPixelY,
            bool isOnLadder)
        {
            if (room == null || room.tiles == null) return false;

            float headY = y + hh;

            int tileY = Mathf.FloorToInt((headY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileLeft = Mathf.FloorToInt((x - hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((x + hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                var tile = room.GetTileAtCoord(new Vector2Int(tx, tileY));
                if (tile == null)
                {
                    continue;
                }

                if (tile.physicsType == TilePhysicsType.Wall)
                {
                    if (isOnLadder && tile.stairType != 0)
                    {
                        continue;
                    }

                    return true;
                }
            }
            return false;
        }

        // ── Resolution helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve horizontal collision by stepping 1 pixel at a time from fromX toward toX.
        /// Returns the last valid X before collision.
        /// Extracted from TilePhysicsController.ResolveHorizontal.
        /// </summary>
        public static float ResolveHorizontal(
            RoomInstance room,
            float fromX, float toX, float y, float hw, float hh,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            float dir = Mathf.Sign(toX - fromX);
            float step = 1f;

            float testX = fromX;
            while (Mathf.Abs(testX - fromX) < Mathf.Abs(toX - fromX))
            {
                float nextX = testX + dir * step;
                if (CheckTileCollisionAt(room, nextX, y, hw, hh, roomWorldPixelX, roomWorldPixelY))
                {
                    return testX;
                }
                testX = nextX;
            }
            return testX;
        }

        /// <summary>
        /// Resolve downward collision — snap to ground surface.
        /// Returns the Y coordinate of the ground surface (tile top or slope height).
        /// Extracted from TilePhysicsController.ResolveVerticalDown.
        /// </summary>
        public static float ResolveVerticalDown(
            RoomInstance room,
            float x, float fromY, float toY, float hw,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            int tileY = Mathf.FloorToInt((toY - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            // Tile top in world pixel coordinates
            float tileTop = roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;

            // Check for slope
            int tileCenterX = Mathf.FloorToInt((x - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            var tile = room.GetTileAtCoord(new Vector2Int(tileCenterX, tileY));
            if (tile != null && tile.IsSlopeSurface())
            {
                float localX = x - roomWorldPixelX;
                return tile.GetGroundHeight(localX) + roomWorldPixelY;
            }

            return tileTop;
        }

        /// <summary>
        /// Resolve upward collision — snap below ceiling.
        /// Returns the Y coordinate such that the head is flush under the blocking tile.
        /// Extracted from TilePhysicsController.ResolveVerticalUp.
        /// </summary>
        public static float ResolveVerticalUp(
            RoomInstance room,
            float x, float fromY, float toY, float hw, float hh,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            float headY = toY + hh;

            int tileY = Mathf.FloorToInt((headY - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            float tileBottom = roomWorldPixelY + tileY * WorldConstants.TILE_SIZE;

            return tileBottom - hh;
        }

        // ── Water sampling ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sample water state at a position. Returns partial and full submersion flags.
        /// Extracted from TilePhysicsController.CheckWater.
        ///
        /// 25% of sprite height → partial submersion (isInWater / wading).
        /// 75% of sprite height → full submersion (isFullySubmerged / isPlav).
        /// </summary>
        public static void CheckWater(
            RoomInstance room,
            float posX, float posY, float height,
            float roomWorldPixelX, float roomWorldPixelY,
            out bool isInWater, out bool isFullySubmerged)
        {
            isInWater = false;
            isFullySubmerged = false;

            if (room == null || room.tiles == null) return;

            int tileX = Mathf.FloorToInt((posX - roomWorldPixelX) / WorldConstants.TILE_SIZE);

            // Transcription of AS3 Unit.as:2627-2655 (checkWater). Order matters:
            //
            //   if (tile at Y - scY*0.75).water > 0   ->  isPlav = true;   inWater = true
            //   else { isPlav = false;
            //          if (scY <= Tile.tileY)              inWater = false;
            //          else                                inWater = (tile at Y - scY*0.25).water > 0; }
            //
            // The 25% sample is consulted ONLY when the body is taller than one tile. A short or
            // crouched unit (scY <= 40) standing in a single tile of water is NOT "in water" in
            // AS3 unless the 75% sample is also wet.

            // 75% height check — full submersion (AS3 isPlav). Takes priority.
            float highSampleY = posY + height * 0.75f;
            int highTileY = Mathf.FloorToInt((highSampleY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            var highTile = room.GetTileAtCoord(new Vector2Int(tileX, highTileY));

            if (highTile != null && highTile.hasWater)
            {
                isInWater = true;
                isFullySubmerged = true;
                return;
            }

            isFullySubmerged = false;

            if (height <= WorldConstants.TILE_SIZE)
            {
                isInWater = false;
                return;
            }

            // 25% height check — wading / partial submersion
            float lowSampleY = posY + height * 0.25f;
            int lowTileY = Mathf.FloorToInt((lowSampleY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            var lowTile = room.GetTileAtCoord(new Vector2Int(tileX, lowTileY));
            isInWater = lowTile != null && lowTile.hasWater;
        }

        // ── Ground height query (for ITileQueryService.GetGroundHeight) ────────────────────

        /// <summary>
        /// Get the ground surface height at a world-pixel position, checking the tile at feet level.
        /// If the tile is a slope, returns the interpolated surface height.
        /// Otherwise returns the tile top.
        /// </summary>
        public static float GetGroundHeight(
            RoomInstance room,
            float worldX, float worldY,
            float roomWorldPixelX, float roomWorldPixelY)
        {
            if (room == null || room.tiles == null) return worldY;

            int tileX = Mathf.FloorToInt((worldX - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileY = Mathf.FloorToInt((worldY - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            var tile = room.GetTileAtCoord(new Vector2Int(tileX, tileY));
            if (tile == null) return worldY;

            if (tile.IsSlopeSurface())
            {
                float localX = worldX - roomWorldPixelX;
                return tile.GetGroundHeight(localX) + roomWorldPixelY;
            }

            // For solid/platform tiles, ground is the tile top
            if (tile.physicsType == TilePhysicsType.Wall || tile.physicsType == TilePhysicsType.Platform)
            {
                return roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;
            }

            return worldY;
        }

        // ── Solidity query (for ITileQueryService.IsSolidAt) ──────────────────────────────

        /// <summary>
        /// Check if a tile at the given room-local coordinate is solid (Wall only).
        /// This matches TilePhysicsController's definition of solid for horizontal/vertical
        /// collision: only Wall tiles block, not platforms or stairs.
        /// </summary>
        public static bool IsSolidAt(RoomInstance room, Vector2Int tileCoord)
        {
            if (room == null || room.tiles == null) return false;

            TileData tile = room.GetTileAtCoord(tileCoord);
            return tile != null && tile.physicsType == TilePhysicsType.Wall;
        }

        // ── AABB collision check (for ITileQueryService.CheckCollision) ───────────────────

        /// <summary>
        /// AABB overlap test against tiles in world pixel space.
        /// This combines wall collision and platform collision logic from the motor.
        ///
        /// For walls: any overlap is a collision.
        /// For platforms: uses the motor's platform threshold logic (position-based).
        /// </summary>
        public static bool CheckCollision(
            RoomInstance room,
            Rect boundsPx,
            float roomWorldPixelX, float roomWorldPixelY,
            float platformThreshold,
            bool isTransparent,
            bool canFallThroughPlatforms,
            float velocityY)
        {
            if (room == null || room.tiles == null) return false;

            int tileLeft = Mathf.FloorToInt((boundsPx.xMin - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((boundsPx.xMax - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((boundsPx.yMin - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt((boundsPx.yMax - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    var tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null) continue;

                    // Air tiles: check slope if not transparent
                    if (tile.physicsType == TilePhysicsType.Air)
                    {
                        if (tile.IsSlopeSurface() && !isTransparent)
                        {
                            // Slope collision check
                            float localX = (boundsPx.xMin + boundsPx.xMax) * 0.5f - roomWorldPixelX;
                            float groundH = tile.GetGroundHeight(localX) + roomWorldPixelY;
                            if (boundsPx.yMin <= groundH)
                            {
                                return true;
                            }
                        }
                        continue;
                    }

                    // Wall: always collides if AABB overlaps
                    if (tile.physicsType == TilePhysicsType.Wall)
                    {
                        Rect tileBoundsLocal = tile.GetBounds();
                        Rect tileBoundsWorld = new Rect(
                            tileBoundsLocal.xMin + roomWorldPixelX,
                            tileBoundsLocal.yMin + roomWorldPixelY,
                            tileBoundsLocal.width,
                            tileBoundsLocal.height);

                        if (boundsPx.Overlaps(tileBoundsWorld))
                        {
                            return true;
                        }
                        continue;
                    }

                    // Platform: one-way from above
                    if (tile.physicsType == TilePhysicsType.Platform)
                    {
                        if (canFallThroughPlatforms) continue;
                        if (velocityY > 0) continue; // moving upward

                        float tileTopPx = roomWorldPixelY + (ty + 1) * WorldConstants.TILE_SIZE;

                        // Check horizontal overlap
                        float tileLeftPx = roomWorldPixelX + tx * WorldConstants.TILE_SIZE;
                        float tileRightPx = tileLeftPx + WorldConstants.TILE_SIZE;
                        if (boundsPx.xMax <= tileLeftPx || boundsPx.xMin >= tileRightPx)
                        {
                            continue;
                        }

                        // Platform threshold check
                        if (boundsPx.yMin <= tileTopPx && boundsPx.yMin >= tileTopPx - platformThreshold)
                        {
                            return true;
                        }
                        continue;
                    }

                    // Stair: collides unless transparent
                    if (tile.physicsType == TilePhysicsType.Stair)
                    {
                        if (isTransparent) continue;

                        Rect tileBoundsLocal = tile.GetBounds();
                        Rect tileBoundsWorld = new Rect(
                            tileBoundsLocal.xMin + roomWorldPixelX,
                            tileBoundsLocal.yMin + roomWorldPixelY,
                            tileBoundsLocal.width,
                            tileBoundsLocal.height);

                        if (boundsPx.Overlaps(tileBoundsWorld))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // ── IsOnGround query (for ITileQueryService.IsOnGround) ──────────────────────────

        /// <summary>
        /// Check if an AABB is resting on ground (the tile row below feet is solid or platform).
        /// Uses a 1-pixel probe below the feet.
        ///
        /// <para><b><paramref name="canFallThroughPlatforms"/> is AS3's <c>throu</c> term.</b> With it
        /// set, a one-way platform is not ground at all, so the caller reports the unit as airborne and
        /// gravity takes it down through the shelf — which is the whole mechanic. Walls are unaffected:
        /// <c>CheckGroundCollisionAt</c>'s <c>Wall</c> branch returns before the platform branch is ever
        /// consulted, which is exactly AS3's <c>phis == 1</c> returning <c>1</c> unconditionally
        /// (<c>Unit.as:2568-2582</c>).</para>
        /// </summary>
        /// <param name="platformThreshold">
        /// AS3 <c>porog</c> — the band below a platform's top edge that still counts as standing on it.
        /// Irrelevant when <paramref name="canFallThroughPlatforms"/> is set, because the platform is
        /// skipped before the band is tested.
        /// </param>
        /// <param name="canFallThroughPlatforms">
        /// AS3 <c>Unit.throu</c> / <c>t_throw</c>. <c>false</c> (the default) is every existing caller's
        /// behaviour and must stay that way — the parameter is additive so that "the player is standing
        /// on the catwalk" cannot change because a zombie somewhere asked to drop.
        /// </param>
        public static bool IsOnGround(
            RoomInstance room,
            Rect boundsPx,
            float roomWorldPixelX, float roomWorldPixelY,
            float platformThreshold,
            bool canFallThroughPlatforms = false)
        {
            if (room == null || room.tiles == null) return false;

            float probeY = boundsPx.yMin - 1f;
            float hw = boundsPx.width * 0.5f;
            float centerX = boundsPx.center.x;

            bool hitPlatform;
            return CheckGroundCollisionAt(
                room, centerX, probeY, hw,
                roomWorldPixelX, roomWorldPixelY,
                platformThreshold,
                canFallThrough: canFallThroughPlatforms,
                out hitPlatform);
        }

        // ── Support span (AS3 Unit.shX1 / shX2) ───────────────────────────────────────────

        /// <summary>
        /// The horizontal span of the tiles holding a unit up, in <b>world pixels</b> — AS3's
        /// <c>min(tile.phX1)</c> and <c>max(tile.phX2)</c> over the tiles under the unit's feet.
        ///
        /// <para><b>What this is for.</b> <c>Unit.as:2301</c>/<c>:2305</c> write
        /// <c>shX1 = -(X1 - tile.phX1)/scX</c> and <c>shX2 = (X2 - tile.phX2)/scX</c> inside the tile
        /// loop, each reduced by a <b>min</b>. Because <c>-(X1 - phX1)</c> is smallest when
        /// <c>phX1</c> is smallest, the reduction is exactly "the leftmost supporting tile's left edge"
        /// — so the pair of fractions is fully described by one span, and one pass here replaces a
        /// per-tile reduce at the call site. <c>UnitOverhangMath</c> turns the span into the two
        /// fractions; see its remarks for why <c>shX1</c>/<c>shX2</c> are overhang fractions and not
        /// wall-contact flags.</para>
        ///
        /// <para><b>What counts as supporting.</b> The same three branches as
        /// <see cref="CheckGroundCollisionAt"/>: a <c>Wall</c>; a <c>Platform</c> inside the
        /// <c>porog</c> band below its top edge; and a slope surface at or below the probe. This is
        /// deliberately <b>not</b> <c>TileData.IsSolid()</c>, which would admit <c>Stair</c> — a ladder
        /// is not a floor, and a unit "standing" on one is on its rungs.</para>
        ///
        /// <para><b>Why the predicate below is a second copy of that method's branches.</b> It is a
        /// duplication on purpose, and it is pinned by a test rather than by good intentions:
        /// <c>CheckGroundCollisionAt</c> returns <c>true</c> on the <i>first</i> wall it meets, and
        /// restructuring that early return is how the player ends up falling through a floor. A span
        /// needs <i>every</i> supporting tile, so it cannot share that loop shape. The test
        /// <c>SupportSpan_IsFoundExactlyWhenIsOnGroundSaysSo</c> asserts the two agree, which is the
        /// control that makes the duplication safe — the failure it is designed to catch is one copy
        /// being edited and the other not.</para>
        ///
        /// <para><b>Return value and the empty case.</b> <c>false</c> means no supporting tile was
        /// found, and the caller must then read the overhang as <c>UnitOverhangMath.NoSupportOverhang</c>
        /// (AS3 leaves <c>shX1 = shX2 = 1</c> when nothing collides) — <b>not</b> as "unknown" and not
        /// as "not at an edge". A unit standing on a crate over air is genuinely at the edge.</para>
        /// </summary>
        /// <param name="boundsPx">The unit's AABB in <b>world pixels</b>, with <c>yMin</c> at the feet —
        /// the same rect <see cref="IsOnGround"/> takes.</param>
        /// <param name="roomWorldPixelX">The room origin in world pixels; subtracted to reach room-local
        /// tile coordinates, exactly as <see cref="CheckGroundCollisionAt"/> does.</param>
        /// <param name="platformThreshold">AS3 <c>porog</c>.</param>
        /// <param name="canFallThrough">AS3 <c>throu</c> — when set, a one-way platform is not support
        /// and is skipped, which is what keeps a zombie that asked to drop from reading a catwalk as the
        /// ledge it is standing on.</param>
        /// <param name="supportLeftWorldPx">Leftmost supporting tile's left edge, world pixels.</param>
        /// <param name="supportRightWorldPx">Rightmost supporting tile's right edge, world pixels.</param>
        public static bool TryGetSupportSpan(
            RoomInstance room,
            Rect boundsPx,
            float roomWorldPixelX, float roomWorldPixelY,
            float platformThreshold,
            bool canFallThrough,
            out float supportLeftWorldPx, out float supportRightWorldPx)
        {
            supportLeftWorldPx = 0f;
            supportRightWorldPx = 0f;
            if (room == null || room.tiles == null) return false;

            float probeY = boundsPx.yMin - 1f;
            float hw = boundsPx.width * 0.5f;
            float centerX = boundsPx.center.x;

            int tileLeft = WorldPixelToTileX(centerX - hw, roomWorldPixelX);
            int tileRight = WorldPixelToTileX(centerX + hw, roomWorldPixelX);
            int tileY = WorldPixelToTileY(probeY, roomWorldPixelY);

            bool found = false;
            float left = 0f;
            float right = 0f;

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                TileData tile = room.GetTileAtCoord(new Vector2Int(tx, tileY));
                if (tile == null) continue;

                if (!IsSupportingTile(
                        tile, centerX, probeY, tileY,
                        roomWorldPixelX, roomWorldPixelY,
                        platformThreshold, canFallThrough))
                {
                    continue;
                }

                float tileLeftWorld = roomWorldPixelX + tx * WorldConstants.TILE_SIZE;
                float tileRightWorld = tileLeftWorld + WorldConstants.TILE_SIZE;

                // Independent min/max, which is what the oracle's two separate `min` reductions do. A
                // gap under the unit therefore widens the span rather than splitting it, in both.
                if (!found || tileLeftWorld < left) left = tileLeftWorld;
                if (!found || tileRightWorld > right) right = tileRightWorld;
                found = true;
            }

            if (!found) return false;

            supportLeftWorldPx = left;
            supportRightWorldPx = right;
            return true;
        }

        /// <summary>
        /// One tile's half of <see cref="CheckGroundCollisionAt"/> — the three branches that make a
        /// cell "ground" — factored out so <see cref="TryGetSupportSpan"/> can ask the question of every
        /// tile in the span instead of stopping at the first. See that method's remarks for the
        /// duplication and the test that pins it.
        /// </summary>
        private static bool IsSupportingTile(
            TileData tile,
            float x, float y, int tileY,
            float roomWorldPixelX, float roomWorldPixelY,
            float platformThreshold,
            bool canFallThrough)
        {
            if (tile.physicsType == TilePhysicsType.Wall)
            {
                return true;
            }

            if (tile.physicsType == TilePhysicsType.Platform && !canFallThrough)
            {
                float tileTop = roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;
                if (y <= tileTop && y >= tileTop - platformThreshold)
                {
                    return true;
                }
            }

            if (tile.IsSlopeSurface())
            {
                float localX = x - roomWorldPixelX;
                float groundH = tile.GetGroundHeight(localX) + roomWorldPixelY;
                if (y <= groundH)
                {
                    return true;
                }
            }

            return false;
        }

        // ── Sub-step calculation ──────────────────────────────────────────────────────────

        /// <summary>
        /// Calculate the number of sub-steps for a given movement distance.
        /// AS3: floor(max(|dx|, |dy|) / maxdelta) + 1 (Unit.as:1811-1824).
        /// </summary>
        public static int CalculateSubSteps(float maxDistance, float maxSubStepDistance)
        {
            return Mathf.FloorToInt(maxDistance / Mathf.Max(1f, maxSubStepDistance)) + 1;
        }
    }
}

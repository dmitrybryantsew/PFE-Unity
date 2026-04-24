using System;
using UnityEngine;
using PFE.Systems.Physics;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Tile-based collision detection system.
    /// Based on ActionScript collision in Location.as and Box.as.
    ///
    /// Key features:
    /// - AABB collision detection against tile grid
    /// - Slope physics (diagonal tiles)
    /// - Platform collision (one-way from top)
    /// - Stair collision
    /// - Tile destruction
    ///
    /// From AS3: collisionTile(), collisionUnit(), getMaxY()
    /// </summary>
    public class TileCollisionSystem
    {
        private RoomInstance room;
        private const float PLATFORM_COLLISION_THRESHOLD = 10f; // porog in AS3
        
        // Room's world pixel origin (for coordinate conversion)
        private float roomWorldPixelX;
        private float roomWorldPixelY;

        /// <summary>
        /// Initialize collision system for a room.
        /// </summary>
        public TileCollisionSystem(RoomInstance room)
        {
            this.room = room;
            
            // Calculate room's world pixel position
            // This accounts for land position and border offset
            if (room != null)
            {
                int borderOffsetTiles = room.borderOffset;
                roomWorldPixelX = room.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;
                roomWorldPixelY = room.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;
            }
        }

        /// <summary>
        /// Check collision between a box and tiles.
        /// Based on AS3: Box.collisionTile()
        /// </summary>
        /// <param name="bounds">World space bounds to check (in pixels)</param>
        /// <param name="isTransparent">Can entity pass through stairs (transT in AS3)</param>
        /// <param name="canFallThroughPlatforms">Can entity fall through platforms (throu/t_throw in AS3)</param>
        /// <param name="velocityY">Current vertical velocity (for platform collision)</param>
        /// <returns>True if collision detected</returns>
        public bool CheckCollision(Rect bounds, bool isTransparent = false, bool canFallThroughPlatforms = false, float velocityY = 0f)
        {
            if (room == null) return false;

            // Convert world pixel coordinates to room-local tile coordinates
            Vector2Int minTile = WorldCoordinates.PixelToTile(new Vector2(bounds.xMin - roomWorldPixelX, bounds.yMin - roomWorldPixelY));
            Vector2Int maxTile = WorldCoordinates.PixelToTile(new Vector2(bounds.xMax - roomWorldPixelX, bounds.yMax - roomWorldPixelY));

            for (int x = minTile.x; x <= maxTile.x; x++)
            {
                for (int y = minTile.y; y <= maxTile.y; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile == null) continue;

                    if (CheckTileCollision(tile, bounds, isTransparent, canFallThroughPlatforms, velocityY))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check collision with a specific tile.
        /// Based on AS3: Box.collisionTile(tile)
        /// Converts between world pixel coordinates (bounds) and room-local tile coordinates.
        /// </summary>
        public bool CheckTileCollision(TileData tile, Rect bounds, bool isTransparent = false,
            bool canFallThroughPlatforms = false, float velocityY = 0f)
        {
            if (tile == null) return false;

            // Air tiles don't collide
            if (tile.physicsType == TilePhysicsType.Air)
            {
                // But stairs might if not transparent
                if (tile.IsStair() && !isTransparent)
                {
                    return CheckSlopeCollision(tile, bounds);
                }
                return false;
            }

            // Get tile bounds in room-local coordinates and convert to world coordinates
            Rect tileBoundsLocal = tile.GetBounds();
            Rect tileBoundsWorld = new Rect(
                tileBoundsLocal.xMin + roomWorldPixelX,
                tileBoundsLocal.yMin + roomWorldPixelY,
                tileBoundsLocal.width,
                tileBoundsLocal.height
            );

            // Platform collision: special handling for platforms above/below entity
            if (tile.IsPlatform())
            {
                return CheckPlatformCollision(tile, tileBoundsWorld, bounds, canFallThroughPlatforms, velocityY);
            }

            // Check AABB intersection for other tile types
            if (!bounds.Overlaps(tileBoundsWorld))
            {
                return false;
            }

            // Stair collision
            if (tile.IsStair() && isTransparent)
            {
                return false; // Transparent entities pass through stairs
            }

            // Wall and solid stairs collide
            return true;
        }

        /// <summary>
        /// Check platform collision (one-way from top).
        /// Based on AS3 platform collision logic with shelf and porog.
        /// In Unity coordinates: Y increases upward, so "above" means higher Y value.
        /// </summary>
        private bool CheckPlatformCollision(TileData tile, Rect tileBoundsWorld, Rect bounds, bool canFallThrough, float velocityY)
        {
            // Can fall through platforms?
            if (canFallThrough)
            {
                return false;
            }

            // Platform only collides from above (entity falling down onto it)
            float entityBottom = bounds.yMin; // Bottom of entity (lowest Y value in Unity)
            float entityTop = bounds.yMax;    // Top of entity
            float tileTop = tileBoundsWorld.yMax;  // Top of platform (highest Y value)

            // Check horizontal overlap first (entity must be above tile horizontally)
            if (bounds.xMax <= tileBoundsWorld.xMin || bounds.xMin >= tileBoundsWorld.xMax)
            {
                return false; // No horizontal overlap
            }

            // If entity is moving upward (jumping), no collision
            if (velocityY > 0)
            {
                return false;
            }

            // If entity bottom is above the tile top + threshold, no collision yet
            // (entity is still far above the platform)
            if (entityBottom > tileTop + PLATFORM_COLLISION_THRESHOLD)
            {
                return false;
            }

            // If entity top is below the tile top - threshold, already passed through
            // (entity is completely below the platform)
            if (entityTop < tileTop - PLATFORM_COLLISION_THRESHOLD)
            {
                return false;
            }

            // Entity is within threshold and falling, collide
            return true;
        }

        /// <summary>
        /// Check slope collision.
        /// Uses GetGroundHeight for accurate slope physics.
        /// Converts world coordinates to room-local for tile query.
        /// </summary>
        private bool CheckSlopeCollision(TileData tile, Rect bounds)
        {
            float centerX = (bounds.xMin + bounds.xMax) * 0.5f;
            // Convert world X to room-local X for ground height calculation
            float localX = centerX - roomWorldPixelX;
            float groundHeight = tile.GetGroundHeight(localX) + roomWorldPixelY;

            // Check if entity is below the slope at center point
            return bounds.yMin <= groundHeight;
        }

        /// <summary>
        /// Get ground height at a position (handles slopes).
        /// Based on AS3: Location.getMaxY(), Tile.getMaxY()
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        public float GetGroundHeight(Vector2 position)
        {
            if (room == null) return position.y;

            // Convert world pixel coordinates to room-local tile coordinates
            Vector2Int tileCoord = WorldCoordinates.PixelToTile(new Vector2(position.x - roomWorldPixelX, position.y - roomWorldPixelY));
            TileData tile = room.GetTileAtCoord(tileCoord);

            if (tile == null)
            {
                return position.y;
            }

            // Get ground height in room-local coordinates, then convert to world
            float localX = position.x - roomWorldPixelX;
            return tile.GetGroundHeight(localX) + roomWorldPixelY;
        }

        /// <summary>
        /// Get ground height at the bottom center of a bounds.
        /// Useful for grounding entities.
        /// </summary>
        public float GetGroundHeight(Rect bounds)
        {
            float centerX = (bounds.xMin + bounds.xMax) * 0.5f;
            return GetGroundHeight(new Vector2(centerX, bounds.yMin));
        }

        /// <summary>
        /// Check if entity is on solid ground.
        /// Based on AS3: stay flag
        /// </summary>
        public bool IsOnGround(Rect bounds)
        {
            if (room == null) return false;

            float groundHeight = GetGroundHeight(bounds);
            return Mathf.Abs(bounds.yMin - groundHeight) < 2f; // Small threshold
        }

        /// <summary>
        /// Apply damage to tiles at a position.
        /// Returns true if any tile was damaged.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        public bool ApplyDamage(Vector2 position, int damage, int radiusTiles = 1)
        {
            if (room == null) return false;

            // Convert world pixel coordinates to room-local tile coordinates
            Vector2Int centerTile = WorldCoordinates.PixelToTile(new Vector2(position.x - roomWorldPixelX, position.y - roomWorldPixelY));
            bool damaged = false;

            for (int x = centerTile.x - radiusTiles; x <= centerTile.x + radiusTiles; x++)
            {
                for (int y = centerTile.y - radiusTiles; y <= centerTile.y + radiusTiles; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null && tile.TakeDamage(damage))
                    {
                        damaged = true;

                        // Destroy tile if needed
                        if (tile.IsDestroyed())
                        {
                            tile.Destroy();
                        }
                    }
                }
            }

            return damaged;
        }

        /// <summary>
        /// Cast a ray and get first collision point.
        /// Returns tile hit and position, or null if no collision.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        public (TileData tile, Vector2 point)? Raycast(Vector2 origin, Vector2 direction, float maxDistance)
        {
            if (room == null) return null;

            float distance = 0f;
            Vector2 currentPos = origin;
            Vector2 step = direction.normalized * WorldConstants.TILE_SIZE * 0.25f;

            while (distance < maxDistance)
            {
                // Convert world pixel coordinates to room-local tile coordinates
                Vector2 localPos = new Vector2(currentPos.x - roomWorldPixelX, currentPos.y - roomWorldPixelY);
                Vector2Int tileCoord = WorldCoordinates.PixelToTile(localPos);
                TileData tile = room.GetTileAtCoord(tileCoord);

                if (tile != null && tile.IsSolid())
                {
                    if (tile.IsPlatform() && direction.y < 0) // Only check platforms from above
                    {
                        Rect bounds = tile.GetBounds();
                        // Convert bounds to world coordinates for comparison
                        Rect worldBounds = new Rect(bounds.xMin + roomWorldPixelX, bounds.yMin + roomWorldPixelY, bounds.width, bounds.height);
                        if (currentPos.y >= worldBounds.yMax)
                        {
                            return (tile, currentPos);
                        }
                    }
                    else if (!tile.IsPlatform())
                    {
                        return (tile, currentPos);
                    }
                }

                currentPos += step;
                distance += step.magnitude;
            }

            return null;
        }
    }
}

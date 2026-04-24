using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Coordinate conversion utilities.
    /// Handles conversion between different coordinate systems in the game.
    ///
    /// From AS3 docs:
    /// 1. Land Grid [x,y,z] -> Which room
    /// 2. Tile Grid [x,y] -> Which tile in room
    /// 3. Pixel Space -> Exact position
    /// 4. Screen Space -> Render position
    /// 5. Unity Space -> Unity world position
    /// </summary>
    public static class WorldCoordinates
    {
        /// <summary>
        /// Convert land grid position to world space (pixels)
        /// worldX = (landX * roomWidth * tileSize) + localX
        /// </summary>
        public static Vector2 LandToWorld(Vector3Int landCoord, Vector2 localPos)
        {
            return new Vector2(
                landCoord.x * WorldConstants.ROOM_SIZE_PIXELS.x + localPos.x,
                landCoord.y * WorldConstants.ROOM_SIZE_PIXELS.y + localPos.y
            );
        }

        /// <summary>
        /// Convert world position to land grid coordinates
        /// </summary>
        public static Vector3Int WorldToLand(Vector2 worldPos)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / WorldConstants.ROOM_SIZE_PIXELS.x),
                Mathf.FloorToInt(worldPos.y / WorldConstants.ROOM_SIZE_PIXELS.y),
                0
            );
        }

        /// <summary>
        /// Convert world position to local position within room
        /// </summary>
        public static Vector2 WorldToLocal(Vector2 worldPos)
        {
            return new Vector2(
                worldPos.x % WorldConstants.ROOM_SIZE_PIXELS.x,
                worldPos.y % WorldConstants.ROOM_SIZE_PIXELS.y
            );
        }

        /// <summary>
        /// Convert pixel position to tile coordinates
        /// tileX = floor(pixelX / tileSize)
        /// </summary>
        public static Vector2Int PixelToTile(Vector2 pixelPos)
        {
            return new Vector2Int(
                Mathf.FloorToInt(pixelPos.x / WorldConstants.TILE_SIZE),
                Mathf.FloorToInt(pixelPos.y / WorldConstants.TILE_SIZE)
            );
        }

        /// <summary>
        /// Convert tile coordinates to pixel position (top-left corner)
        /// pixelX = tileX * tileSize
        /// </summary>
        public static Vector2 TileToPixel(Vector2Int tileCoord)
        {
            return new Vector2(
                tileCoord.x * WorldConstants.TILE_SIZE,
                tileCoord.y * WorldConstants.TILE_SIZE
            );
        }

        /// <summary>
        /// Get pixel bounds of a tile
        /// </summary>
        public static Rect TileToRect(Vector2Int tileCoord)
        {
            Vector2 pos = TileToPixel(tileCoord);
            return new Rect(pos.x, pos.y, WorldConstants.TILE_SIZE, WorldConstants.TILE_SIZE);
        }

        /// <summary>
        /// Convert Unity world position to pixel position.
        /// Assuming 100 pixels = 1 Unity unit (1 meter).
        /// </summary>
        public static Vector2 UnityToPixel(Vector3 unityPos)
        {
            return new Vector2(unityPos.x * 100f, unityPos.y * 100f);
        }

        /// <summary>
        /// Convert pixel position to Unity world position.
        /// 100 pixels = 1 Unity unit (1 meter).
        /// </summary>
        public static Vector3 PixelToUnity(Vector2 pixelPos)
        {
            return new Vector3(pixelPos.x / 100f, pixelPos.y / 100f, 0);
        }

        /// <summary>
        /// Convert tile coordinates directly to Unity world position
        /// </summary>
        public static Vector3 TileToUnity(Vector2Int tileCoord)
        {
            Vector2 pixelPos = TileToPixel(tileCoord);
            return PixelToUnity(pixelPos);
        }

        /// <summary>
        /// Convert Unity world position to tile coordinates
        /// </summary>
        public static Vector2Int UnityToTile(Vector3 unityPos)
        {
            Vector2 pixelPos = UnityToPixel(unityPos);
            return PixelToTile(pixelPos);
        }

        /// <summary>
        /// Check if a tile coordinate is within standard room bounds (48x25)
        /// </summary>
        public static bool IsTileInBounds(Vector2Int tileCoord)
        {
            return tileCoord.x >= 0 && tileCoord.x < WorldConstants.ROOM_WIDTH &&
                   tileCoord.y >= 0 && tileCoord.y < WorldConstants.ROOM_HEIGHT;
        }

        /// <summary>
        /// Check if a tile coordinate is within a specific room's bounds
        /// </summary>
        public static bool IsTileInBounds(Vector2Int tileCoord, RoomInstance room)
        {
            if (room == null) return IsTileInBounds(tileCoord);
            return tileCoord.x >= 0 && tileCoord.x < room.width &&
                   tileCoord.y >= 0 && tileCoord.y < room.height;
        }

        /// <summary>
        /// Clamp tile coordinates to standard room bounds (48x25)
        /// </summary>
        public static Vector2Int ClampTileToBounds(Vector2Int tileCoord)
        {
            return new Vector2Int(
                Mathf.Clamp(tileCoord.x, 0, WorldConstants.ROOM_WIDTH - 1),
                Mathf.Clamp(tileCoord.y, 0, WorldConstants.ROOM_HEIGHT - 1)
            );
        }

        /// <summary>
        /// Clamp tile coordinates to a specific room's bounds
        /// </summary>
        public static Vector2Int ClampTileToBounds(Vector2Int tileCoord, RoomInstance room)
        {
            if (room == null) return ClampTileToBounds(tileCoord);
            return new Vector2Int(
                Mathf.Clamp(tileCoord.x, 0, room.width - 1),
                Mathf.Clamp(tileCoord.y, 0, room.height - 1)
            );
        }
    }
}

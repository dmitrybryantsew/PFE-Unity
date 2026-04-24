using UnityEngine;
using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Carves door openings in tile grids when rooms are connected.
    /// Direct port of AS3 Location.setDoor() logic.
    ///
    /// AS3 door layout (24 slots):
    ///   0-5:   Right side  (6 vertical door positions, each at column spaceX-1)
    ///   6-10:  Bottom side (5 horizontal door positions, each at row spaceY-1)
    ///   11-16: Left side   (6 vertical door positions, each at column 0)
    ///   17-21: Top side    (5 horizontal door positions, each at row 0)
    ///
    /// AS3 setDoor() calculates tile positions using:
    ///   Right side:  column = spaceX-1, row = doorIndex * 4 + 3
    ///   Bottom side: row = spaceY-1, column = (doorIndex-6) * 9 + 5
    ///   Left side:   column = 0, row = (doorIndex-11) * 4 + 3
    ///   Top side:    row = 0, column = (doorIndex-17) * 9 + 5
    /// </summary>
    public static class DoorCarver
    {
        /// <summary>
        /// Carve all active door openings in a room's tile grid.
        /// Call this AFTER tiles are initialized but BEFORE objects are placed.
        /// </summary>
        public static void CarveAllDoors(RoomInstance room)
        {
            if (room == null || room.tiles == null || room.doors == null)
                return;

            foreach (var door in room.doors)
            {
                if (door.isActive && door.quality >= DoorQuality.Narrow)
                {
                    CarveDoor(room, door.doorIndex, (int)door.quality);
                }
            }
        }

        /// <summary>
        /// Carve a single door opening in the tile grid.
        /// Directly mirrors AS3 Location.setDoor(doorIndex, quality).
        /// Accounts for border expansion (adds 1 tile offset on each side).
        /// </summary>
        public static void CarveDoor(RoomInstance room, int doorIndex, int quality)
        {
            if (quality < 2) return; // Quality < 2 means no opening
            if (doorIndex < 0 || doorIndex > 21) return;

            // Calculate border offset (if room was expanded)
            int borderOffset = (room.width - WorldConstants.ROOM_WIDTH) / 2;

            if (doorIndex >= 17) // Top side (indices 17-21)
            {
                int baseCol = (doorIndex - 17) * 9 + 4 + borderOffset;
                int topRow = borderOffset; // Top border row
                // Core 2-tile opening
                CarveTile(room, baseCol + 1, topRow);
                CarveTile(room, baseCol + 2, topRow);
                // Clear tiles behind the opening for walkability
                ClearPhysics(room, baseCol + 1, topRow + 1);
                ClearPhysics(room, baseCol + 2, topRow + 1);

                if (quality > 2) // Wide door
                {
                    CarveTile(room, baseCol, topRow);
                    CarveTile(room, baseCol + 3, topRow);
                    ClearPhysics(room, baseCol, topRow + 1);
                    ClearPhysics(room, baseCol + 3, topRow + 1);
                }
            }
            else if (doorIndex >= 11) // Left side (indices 11-16, mapped from AS3 indices 11-16)
            {
                int baseRow = (doorIndex - 11) * 4 + 3 + borderOffset;
                int leftCol = borderOffset; // Left border column
                // Core 2-tile opening
                CarveTile(room, leftCol, baseRow);
                CarveTile(room, leftCol, baseRow - 1);
                // Clear tiles behind the opening
                ClearPhysics(room, leftCol + 1, baseRow);
                ClearPhysics(room, leftCol + 1, baseRow - 1);

                if (quality > 2)
                {
                    CarveTile(room, leftCol, baseRow - 2);
                    ClearPhysics(room, leftCol + 1, baseRow - 2);
                }
            }
            else if (doorIndex >= 6) // Bottom side (indices 6-10, mapped from AS3 indices 6-10)
            {
                int baseCol = (doorIndex - 6) * 9 + 4 + borderOffset;
                int bottomRow = room.height - 1 - borderOffset; // Bottom border row (accounting for expansion)
                // Core 2-tile opening
                CarveTile(room, baseCol + 1, bottomRow);
                CarveTile(room, baseCol + 2, bottomRow);
                ClearPhysics(room, baseCol + 1, bottomRow - 1);
                ClearPhysics(room, baseCol + 2, bottomRow - 1);

                if (quality > 2)
                {
                    CarveTile(room, baseCol, bottomRow);
                    CarveTile(room, baseCol + 3, bottomRow);
                    ClearPhysics(room, baseCol, bottomRow - 1);
                    ClearPhysics(room, baseCol + 3, bottomRow - 1);
                }
            }
            else // Right side (indices 0-5)
            {
                int baseRow = doorIndex * 4 + 3 + borderOffset;
                int rightCol = room.width - 1 - borderOffset; // Right border column (accounting for expansion)
                // Core 2-tile opening
                CarveTile(room, rightCol, baseRow);
                CarveTile(room, rightCol, baseRow - 1);
                ClearPhysics(room, rightCol - 1, baseRow);
                ClearPhysics(room, rightCol - 1, baseRow - 1);

                if (quality > 2)
                {
                    CarveTile(room, rightCol, baseRow - 2);
                    ClearPhysics(room, rightCol - 1, baseRow - 2);
                }
            }
        }

        /// <summary>
        /// Carve a tile to air (create door hole).
        /// </summary>
        private static void CarveTile(RoomInstance room, int x, int y)
        {
            if (x < 0 || x >= room.width || y < 0 || y >= room.height) return;

            var tile = room.tiles[x, y];
            if (tile == null) return;

            tile.physicsType = TilePhysicsType.Air;
            tile.hitPoints = 0;
            tile.visualId = 0;
            tile.SetFrontGraphic("");
        }

        /// <summary>
        /// Clear physics on adjacent tile (make walkable behind door).
        /// Only clears if currently solid.
        /// </summary>
        private static void ClearPhysics(RoomInstance room, int x, int y)
        {
            if (x < 0 || x >= room.width || y < 0 || y >= room.height) return;

            var tile = room.tiles[x, y];
            if (tile == null) return;

            // Only clear solid wall tiles, preserve platforms and stairs
            if (tile.physicsType == TilePhysicsType.Wall && !tile.indestructible)
            {
                tile.physicsType = TilePhysicsType.Air;
                tile.visualId = 0;
                tile.SetFrontGraphic("");
            }
        }

        /// <summary>
        /// Apply border frame (ramka) around room edges.
        /// EXPANDS the room array by 2 tiles (1 on each side) and fills the new
        /// outer layer with indestructible border tiles.
        ///
        /// From AS3 Location constructor: fills border tiles with solid walls.
        /// ramka types:
        ///   0 = no border
        ///   1 = full border (all 4 sides)
        ///   2 = left+right walls only
        ///   3 = bottom only
        ///   4 = left+right+bottom
        ///   5 = partial left+right (columns 0-10 and 37-47)
        ///   6 = partial bottom (rows 16+)
        ///   7 = partial left+right + partial bottom
        ///   8 = right wall only
        /// </summary>
        public static void ApplyBorder(RoomInstance room, int ramka)
        {
            if (ramka == 0 || room == null || room.tiles == null) return;

            // Step 1: Expand the room array to make room for border
            ExpandRoomForBorder(room);

            int w = room.width;
            int h = room.height;

            // Step 2: Fill border tiles (now on the outer ring of expanded array)
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    bool isBorder = (x == 0 || x == w - 1 || y == 0 || y == h - 1);
                    if (!isBorder) continue;

                    bool shouldSolid = false;

                    switch (ramka)
                    {
                        case 1: // Full border
                            shouldSolid = true;
                            break;
                        case 2: // Left + right walls
                            shouldSolid = (x == 0 || x == w - 1);
                            break;
                        case 3: // Bottom only
                            shouldSolid = (y == h - 1);
                            break;
                        case 4: // Left + right + bottom
                            shouldSolid = (x == 0 || x == w - 1 || y == h - 1);
                            break;
                        case 5: // Partial left+right (columns 0-10 and 37-47)
                            shouldSolid = (x <= 10 || x >= 37);
                            break;
                        case 6: // Partial bottom (rows 16+)
                            shouldSolid = (y >= 16);
                            break;
                        case 7: // Partial left+right + partial bottom
                            shouldSolid = ((x <= 10 || x >= 37) && (y >= 16));
                            break;
                        case 8: // Right wall only
                            shouldSolid = (x == w - 1);
                            break;
                    }

                    if (shouldSolid)
                    {
                        var tile = room.tiles[x, y];
                        if (tile != null)
                        {
                            tile.physicsType = TilePhysicsType.Wall;
                            tile.indestructible = true;
                            tile.hitPoints = 9999;
                            tile.SetFrontGraphic("tBorder");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Expand room tile array by 2 tiles (1 on each side).
        /// Existing tiles are shifted inward to make room for border.
        /// </summary>
        private static void ExpandRoomForBorder(RoomInstance room)
        {
            int oldW = room.width;
            int oldH = room.height;
            int newW = oldW + 2;
            int newH = oldH + 2;

            // Create new expanded array
            TileData[,] newTiles = new TileData[newW, newH];

            // Copy existing tiles to center of new array (offset by 1,1)
            for (int x = 0; x < oldW; x++)
            {
                for (int y = 0; y < oldH; y++)
                {
                    var tile = room.tiles[x, y];
                    if (tile != null)
                    {
                        // Update grid position to new coordinates
                        tile.gridPosition = new Vector2Int(x + 1, y + 1);
                        newTiles[x + 1, y + 1] = tile;
                    }
                }
            }

            // Initialize new border tiles (outer ring)
            for (int x = 0; x < newW; x++)
            {
                for (int y = 0; y < newH; y++)
                {
                    // Only initialize tiles on the border that don't exist yet
                    if (x == 0 || x == newW - 1 || y == 0 || y == newH - 1)
                    {
                        if (newTiles[x, y] == null)
                        {
                            newTiles[x, y] = new TileData
                            {
                                gridPosition = new Vector2Int(x, y),
                                physicsType = TilePhysicsType.Air // Will be set to Wall by ApplyBorder
                            };
                        }
                    }
                }
            }

            // Update room dimensions and array
            room.tiles = newTiles;
            room.width = newW;
            room.height = newH;
            room.borderOffset = 1; // Track that we added 1 tile border on each side

            // Update door positions (shift by 1 to account for border offset)
            foreach (var door in room.doors)
            {
                // Door positions are relative to room, they need adjustment
                // This is handled by the door carving logic using room.width/height
            }

            // Update spawn points (shift by 1 tile to account for border offset)
            foreach (var spawn in room.spawnPoints)
            {
                spawn.tileCoord += new Vector2Int(1, 1);
            }
        }

        /// <summary>
        /// Apply water level to room tiles.
        /// From AS3: if (j >= waterLevel) space[i][j].water = 1
        /// </summary>
        public static void ApplyWaterLevel(RoomInstance room, int waterLevel)
        {
            if (room == null || room.tiles == null) return;
            if (waterLevel >= room.height) return; // No water

            for (int x = 0; x < room.width; x++)
            {
                for (int y = waterLevel; y < room.height; y++)
                {
                    var tile = room.tiles[x, y];
                    if (tile != null)
                    {
                        tile.hasWater = true;
                    }
                }
            }
        }
    }
}
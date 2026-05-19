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
        /// Uses the same edge cells as AS3 Location.setDoor().
        /// </summary>
        public static void CarveDoor(RoomInstance room, int doorIndex, int quality)
        {
            if (quality < 2) return; // Quality < 2 means no opening
            if (doorIndex < 0 || doorIndex > 21) return;

            int borderOffset = Mathf.Max(0, room.borderOffset);

            if (doorIndex >= 17) // Top side (indices 17-21)
            {
                int baseCol = (doorIndex - 17) * 9 + 4 + borderOffset;
                int topRow = As3RowToUnityY(room, borderOffset); // AS3 row 0
                int innerRow = As3RowToUnityY(room, borderOffset + 1);
                // Core 2-tile opening
                CarveTile(room, baseCol + 1, topRow);
                CarveTile(room, baseCol + 2, topRow);
                // Clear tiles behind the opening for walkability
                ClearPhysics(room, baseCol + 1, innerRow);
                ClearPhysics(room, baseCol + 2, innerRow);

                if (quality > 2) // Wide door
                {
                    CarveTile(room, baseCol, topRow);
                    CarveTile(room, baseCol + 3, topRow);
                    ClearPhysics(room, baseCol, innerRow);
                    ClearPhysics(room, baseCol + 3, innerRow);
                }
            }
            else if (doorIndex >= 11) // Left side (indices 11-16, mapped from AS3 indices 11-16)
            {
                int baseRow = (doorIndex - 11) * 4 + 3 + borderOffset;
                int row0 = As3RowToUnityY(room, baseRow);
                int row1 = As3RowToUnityY(room, baseRow - 1);
                int row2 = As3RowToUnityY(room, baseRow - 2);
                int leftCol = borderOffset; // Left border column
                // Core 2-tile opening
                CarveTile(room, leftCol, row0);
                CarveTile(room, leftCol, row1);
                // Clear tiles behind the opening
                ClearPhysics(room, leftCol + 1, row0);
                ClearPhysics(room, leftCol + 1, row1);

                if (quality > 2)
                {
                    CarveTile(room, leftCol, row2);
                    ClearPhysics(room, leftCol + 1, row2);
                }
            }
            else if (doorIndex >= 6) // Bottom side (indices 6-10, mapped from AS3 indices 6-10)
            {
                int baseCol = (doorIndex - 6) * 9 + 4 + borderOffset;
                int bottomRow = As3RowToUnityY(room, room.height - 1 - borderOffset);
                int innerRow = As3RowToUnityY(room, room.height - 2 - borderOffset);
                // Core 2-tile opening
                CarveTile(room, baseCol + 1, bottomRow);
                CarveTile(room, baseCol + 2, bottomRow);
                ClearPhysics(room, baseCol + 1, innerRow);
                ClearPhysics(room, baseCol + 2, innerRow);

                if (quality > 2)
                {
                    CarveTile(room, baseCol, bottomRow);
                    CarveTile(room, baseCol + 3, bottomRow);
                    ClearPhysics(room, baseCol, innerRow);
                    ClearPhysics(room, baseCol + 3, innerRow);
                }
            }
            else // Right side (indices 0-5)
            {
                int baseRow = doorIndex * 4 + 3 + borderOffset;
                int row0 = As3RowToUnityY(room, baseRow);
                int row1 = As3RowToUnityY(room, baseRow - 1);
                int row2 = As3RowToUnityY(room, baseRow - 2);
                int rightCol = room.width - 1 - borderOffset; // Right border column
                // Core 2-tile opening
                CarveTile(room, rightCol, row0);
                CarveTile(room, rightCol, row1);
                ClearPhysics(room, rightCol - 1, row0);
                ClearPhysics(room, rightCol - 1, row1);

                if (quality > 2)
                {
                    CarveTile(room, rightCol, row2);
                    ClearPhysics(room, rightCol - 1, row2);
                }
            }
        }

        private static int As3RowToUnityY(RoomInstance room, int as3Row)
        {
            return room.height - 1 - as3Row;
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
            if (room == null || room.tiles == null) return;

            room.borderOffset = 0;

            int w = room.width;
            int h = room.height;

            // AS3 first marks ramka edge cells solid while parsing, then
            // Location.mainFrame() converts every solid edge cell to border material.
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    bool isBorder = (x == 0 || x == w - 1 || y == 0 || y == h - 1);
                    if (!isBorder) continue;

                    var tile = room.tiles[x, y];
                    if (tile == null) continue;

                    if (ShouldApplyRamkaAtEdge(ramka, x, y, w, h))
                    {
                        tile.physicsType = TilePhysicsType.Wall;
                    }

                    if ((int)tile.physicsType >= (int)TilePhysicsType.Wall)
                    {
                        ApplyMainFrame(tile, "A");
                    }
                }
            }
        }

        private static bool ShouldApplyRamkaAtEdge(int ramka, int x, int unityY, int width, int height)
        {
            int as3Row = height - 1 - unityY;

            switch (ramka)
            {
                case 1: // Full border
                    return true;
                case 2: // Left + right walls
                    return x == 0 || x == width - 1;
                case 3: // AS3 bottom row
                    return as3Row == height - 1;
                case 4: // Left + right + AS3 bottom row
                    return x == 0 || x == width - 1 || as3Row == height - 1;
                case 5: // Partial left+right columns
                    return x <= 10 || x >= 37;
                case 6: // Partial AS3 lower rows
                    return as3Row >= 16;
                case 7: // Partial left+right + partial AS3 lower rows
                    return (x <= 10 || x >= 37) && as3Row >= 16;
                case 8: // Right wall only
                    return x == width - 1;
                default:
                    return false;
            }
        }

        private static void ApplyMainFrame(TileData tile, string materialId)
        {
            if (tile == null)
            {
                return;
            }

            // Mirrors Tile.mainFrame() for the default border material closely enough
            // for Unity's material compositor: the front/back IDs must remain form IDs,
            // not texture names like tBorder.
            tile.physicsType = TilePhysicsType.Wall;
            tile.visualId = 0;
            tile.visualId2 = 0;
            tile.slopeType = 0;
            tile.stairType = 0;
            tile.SetFrontGraphic(materialId);
            tile.SetBackGraphic("A");
            tile.indestructible = true;
            tile.hitPoints = 10000;
            tile.opacity = 1f;
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

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
    ///   Right side:  column = spaceX-1, row = doorIndex * 4 + 3          (Location.as:884)
    ///   Bottom side: row = spaceY-1, column = (doorIndex-6) * 9 + 4      (Location.as:857)
    ///   Left side:   column = 0, row = (doorIndex-11) * 4 + 3            (Location.as:837)
    ///   Top side:    row = 0, column = (doorIndex-17) * 9 + 4            (Location.as:814)
    ///
    /// The horizontal sides were documented here as `+ 5` until 2026-10-11; the oracle says `+ 4`
    /// and the code below has always used `+ 4`, so this comment — not the carve — was the thing
    /// that was wrong. The two-tile opening is `[+1, +2]` on the border row and `+0, +3` when the
    /// door is wide, which is the oracle's own `space[_loc3_ + 1]` / `space[_loc3_ + 2]` pair.
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
                // Open the tiles behind the opening too (AS3 holes both the border row and the row inside it)
                Hole(room, baseCol + 1, innerRow);
                Hole(room, baseCol + 2, innerRow);

                if (quality > 2) // Wide door
                {
                    CarveTile(room, baseCol, topRow);
                    CarveTile(room, baseCol + 3, topRow);
                    Hole(room, baseCol, innerRow);
                    Hole(room, baseCol + 3, innerRow);
                }

                // AS3 Location.as:819-820 (+ :827-828 when wide) — `setNoObj(_loc3_+1, 0, 0, 2)`.
                // The span runs from AS3 row 0 DOWN to row 2, so the port starts on the third row and
                // walks UP: AS3 rows grow downward, port rows grow upward.
                int noObjTopRow = As3RowToUnityY(room, borderOffset + 2);
                MarkNoObj(room, baseCol + 1, noObjTopRow, 0, 2);
                MarkNoObj(room, baseCol + 2, noObjTopRow, 0, 2);
                if (quality > 2)
                {
                    MarkNoObj(room, baseCol, noObjTopRow, 0, 2);
                    MarkNoObj(room, baseCol + 3, noObjTopRow, 0, 2);
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
                Hole(room, leftCol + 1, row0);
                Hole(room, leftCol + 1, row1);

                if (quality > 2)
                {
                    CarveTile(room, leftCol, row2);
                    Hole(room, leftCol + 1, row2);
                }

                // AS3 Location.as:842-843 — `setNoObj(0, _loc3_, 5, 0)` / `(0, _loc3_-1, 5, 0)`: the wall
                // column plus five columns inward, on the two carved rows. The wide branch adds NO
                // marking here (its third row is only holed), so neither does this one.
                MarkNoObj(room, leftCol, row0, 5, 0);
                MarkNoObj(room, leftCol, row1, 5, 0);
            }
            else if (doorIndex >= 6) // Bottom side (indices 6-10, mapped from AS3 indices 6-10)
            {
                int baseCol = (doorIndex - 6) * 9 + 4 + borderOffset;
                int bottomRow = As3RowToUnityY(room, room.height - 1 - borderOffset);
                int innerRow = As3RowToUnityY(room, room.height - 2 - borderOffset);
                // Core 2-tile opening
                CarveTile(room, baseCol + 1, bottomRow);
                CarveTile(room, baseCol + 2, bottomRow);
                Hole(room, baseCol + 1, innerRow);
                Hole(room, baseCol + 2, innerRow);

                if (quality > 2)
                {
                    CarveTile(room, baseCol, bottomRow);
                    CarveTile(room, baseCol + 3, bottomRow);
                    Hole(room, baseCol, innerRow);
                    Hole(room, baseCol + 3, innerRow);
                }

                // AS3 Location.as:862-863 (+ :870-871 when wide) — `setNoObj(_loc3_+1, spaceY-1, 0, -2)`.
                // AS3 rows spaceY-1 .. spaceY-3; `bottomRow` is the largest of the three, so the port
                // walks up from it. This is the row `rooms_plant/яма` stands its whole garrison on.
                MarkNoObj(room, baseCol + 1, bottomRow, 0, 2);
                MarkNoObj(room, baseCol + 2, bottomRow, 0, 2);
                if (quality > 2)
                {
                    MarkNoObj(room, baseCol, bottomRow, 0, 2);
                    MarkNoObj(room, baseCol + 3, bottomRow, 0, 2);
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
                Hole(room, rightCol - 1, row0);
                Hole(room, rightCol - 1, row1);

                if (quality > 2)
                {
                    CarveTile(room, rightCol, row2);
                    Hole(room, rightCol - 1, row2);
                }

                // AS3 Location.as:889-890 — `setNoObj(spaceX-1, _loc3_, -5, 0)` / `(spaceX-1, _loc3_-1,
                // -5, 0)`: the wall column plus five columns inward. As on the left, the wide branch
                // adds no marking.
                MarkNoObj(room, rightCol, row0, -5, 0);
                MarkNoObj(room, rightCol, row1, -5, 0);
            }
        }

        /// <summary>
        /// AS3 <c>Location.setNoObj(param1, param2, param3, param4)</c> (<c>Location.as:936-975</c>) —
        /// mark a span of tiles <c>place = false</c>, in <b>port</b> tile coordinates.
        ///
        /// <para><b>This is the oracle's ONLY writer of <c>place = false</c>.</b> A grep over
        /// <c>Location.as</c> finds the four assignments (<c>:944</c>, <c>:953</c>, <c>:962</c>,
        /// <c>:971</c>) and nothing else, and every one is reached from <c>setDoor()</c>. There is no
        /// border ring, no "objects should not spawn on the edges" rule and no other caller. That is why
        /// <c>RoomSetup</c>'s former whole-ring version had to go: it marked roughly forty times the
        /// tiles the oracle does, and once <c>RoomSetup</c> began running it <i>before</i>
        /// <c>PopulateRoom</c> it silently emptied 335 of the 550 shipped rooms.</para>
        ///
        /// <para><b>The oracle's span arithmetic, transcribed.</b> A positive
        /// <paramref name="spanX"/> walks right from <paramref name="x"/> and <i>includes</i> it
        /// (<c>while(_loc5_ &lt;= param1 + param3)</c>), so <c>spanX = 5</c> marks six columns; a negative
        /// one starts at <c>x + spanX</c> and walks back up to <c>x</c>, so <c>spanX = -5</c> also marks
        /// six. The vertical pair is identical and independent, and a 0 marks nothing — the oracle's own
        /// <c>if(param3 &gt; 0)</c> / <c>if(param4 &gt; 0)</c> guards skip it.</para>
        ///
        /// <para><b>Why the marking belongs here rather than in a later pass.</b> AS3 does it inside
        /// <c>setDoor</c>, while the Location is still being built, so it is final before
        /// <c>setObjects</c> runs. Fusing it into the carve keeps the door geometry in one place — the
        /// carve and the mark are the same four <c>_loc3_</c> expressions — and makes it impossible for a
        /// caller to carve a door without also marking its slot.</para>
        /// </summary>
        private static void MarkNoObj(RoomInstance room, int x, int y, int spanX, int spanY)
        {
            if (spanX > 0)
            {
                for (int i = x; i <= x + spanX; i++)
                {
                    MarkNoObjTile(room, i, y);
                }
            }
            else if (spanX < 0)
            {
                for (int i = x + spanX; i <= x; i++)
                {
                    MarkNoObjTile(room, i, y);
                }
            }

            if (spanY > 0)
            {
                for (int j = y; j <= y + spanY; j++)
                {
                    MarkNoObjTile(room, x, j);
                }
            }
            else if (spanY < 0)
            {
                for (int j = y + spanY; j <= y; j++)
                {
                    MarkNoObjTile(room, x, j);
                }
            }
        }

        /// <summary>
        /// <c>space[x][y].place = false</c>, bounds-checked.
        ///
        /// <para>An off-grid coordinate is <i>ignored</i>, not clamped: AS3's arrays would fault there, so
        /// there is no behaviour to copy, and clamping would mark a tile the oracle never touched.</para>
        /// </summary>
        private static void MarkNoObjTile(RoomInstance room, int x, int y)
        {
            if (room == null || room.tiles == null) return;
            if (x < 0 || x >= room.width || y < 0 || y >= room.height) return;

            TileData tile = room.tiles[x, y];
            if (tile != null)
            {
                tile.canPlaceObjects = false;
            }
        }

        private static int As3RowToUnityY(RoomInstance room, int as3Row)
        {
            // Delegates to the shared mirror so the AS3→port row flip has exactly one definition.
            // See WorldCoordinates' "AS3 ↔ port Y axis" remarks.
            return WorldCoordinates.As3RowToUnityRow(as3Row, room.height);
        }

        /// <summary>
        /// Carve a tile to air (create door hole). AS3 <c>Tile.hole()</c> (<c>Tile.as:260-268</c>).
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
            // AS3's hole() drops solidity unconditionally, including on a framed (indestructible) border
            // tile. Leaving the flag set made the tile read as "still a wall" to later passes.
            tile.indestructible = false;
        }

        /// <summary>
        /// Open the tile behind a door — AS3 calls <c>hole()</c> on it too (<c>Location.as:817-818</c>), and
        /// <c>hole()</c> is unconditional (<c>Tile.as:260-268</c>).
        ///
        /// <para><b>This used to be conditional and it broke doors.</b> The old body refused to open a tile
        /// marked <c>indestructible</c> — and <see cref="ApplyMainFrame"/> marks every framed border tile
        /// exactly that. A door carved into a framed wall therefore left the tile behind it solid: an
        /// opening that opens onto a wall. Matching AS3 means opening it whatever its previous state.</para>
        /// </summary>
        private static void Hole(RoomInstance room, int x, int y)
        {
            CarveTile(room, x, y);
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

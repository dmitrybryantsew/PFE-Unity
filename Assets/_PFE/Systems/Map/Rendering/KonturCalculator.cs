using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Calculates kontur (edge/contour) values for tiles based on neighbor analysis.
    /// Direct port of AS3 Location.tileKontur() + insKontur() + uslKontur() + uslBontur().
    /// 
    /// Call CalculateAll() after tiles are decoded to populate edge data.
    /// The rendering system reads kont1-4 (wall edges) and pont1-4 (background edges)
    /// to select the correct tile appearance.
    /// 
    /// Kontur values (from insKontur):
    ///   0 = fully surrounded corner (adjacent + diagonal neighbors present)
    ///   1 = inner corner (adjacent present but diagonal missing)
    ///   2 = horizontal edge (top/bottom neighbor missing, side present)
    ///   3 = vertical edge (left/right neighbor missing, top/bottom present)
    ///   4 = outer corner (both adjacent neighbors missing)
    /// </summary>
    public static class KonturCalculator
    {
        /// <summary>
        /// Calculate kontur values for all tiles in a room.
        /// Call after TileDecoder has populated the tile grid.
        /// </summary>
        public static void CalculateAll(RoomInstance room)
        {
            if (room?.tiles == null) return;

            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    var tile = room.tiles[x, y];
                    if (tile == null) continue;

                    CalculateTile(room, x, y, tile);
                }
            }
        }

        /// <summary>
        /// Calculate kontur for a single tile.
        /// Port of AS3: Location.tileKontur(i, j, t)
        /// </summary>
        private static void CalculateTile(RoomInstance room, int x, int y, TileData tile)
        {
            if (tile.physicsType == TilePhysicsType.Wall)
            {
                bool top = IsWallNeighbor(room, x, y + 1);
                bool bottom = IsWallNeighbor(room, x, y - 1);
                bool left = IsWallNeighbor(room, x - 1, y);   // was x + 1
                bool right = IsWallNeighbor(room, x + 1, y);   // was x - 1

                bool topLeft = IsWallNeighbor(room, x - 1, y + 1);  // was x + 1
                bool topRight = IsWallNeighbor(room, x + 1, y + 1);  // was x - 1
                bool bottomLeft = IsWallNeighbor(room, x - 1, y - 1);  // was x + 1
                bool bottomRight = IsWallNeighbor(room, x + 1, y - 1);  // was x - 1

                tile.kontur1 = InsKontur(top, left, topLeft);
                tile.kontur2 = InsKontur(top, right, topRight);
                tile.kontur3 = InsKontur(bottom, left, bottomLeft);
                tile.kontur4 = InsKontur(bottom, right, bottomRight);

                string back = tile.GetBackGraphic();
                if (!string.IsNullOrEmpty(back))
                {
                    bool ponturTop = top || IsPonturNeighbor(room, x, y + 1);
                    bool ponturBottom = bottom || IsPonturNeighbor(room, x, y - 1);
                    bool ponturLeft = left || IsPonturNeighbor(room, x - 1, y);
                    bool ponturRight = right || IsPonturNeighbor(room, x + 1, y);

                    tile.pontur1 = InsKontur(ponturTop, ponturLeft, topLeft);
                    tile.pontur2 = InsKontur(ponturTop, ponturRight, topRight);
                    tile.pontur3 = InsKontur(ponturBottom, ponturLeft, bottomLeft);
                    tile.pontur4 = InsKontur(ponturBottom, ponturRight, bottomRight);
                }
            }
            else
            {
                string back = tile.GetBackGraphic();
                if (!string.IsNullOrEmpty(back))
                {
                    bool top = IsBackNeighbor(room, x, y + 1, back);
                    bool bottom = IsBackNeighbor(room, x, y - 1, back);
                    bool left = IsBackNeighbor(room, x - 1, y, back);   // was x + 1
                    bool right = IsBackNeighbor(room, x + 1, y, back);   // was x - 1

                    bool topLeft = IsBackNeighbor(room, x - 1, y + 1, back);
                    bool topRight = IsBackNeighbor(room, x + 1, y + 1, back);
                    bool bottomLeft = IsBackNeighbor(room, x - 1, y - 1, back);
                    bool bottomRight = IsBackNeighbor(room, x + 1, y - 1, back);

                    tile.pontur1 = InsKontur(top, left, topLeft);
                    tile.pontur2 = InsKontur(top, right, topRight);
                    tile.pontur3 = InsKontur(bottom, left, bottomLeft);
                    tile.pontur4 = InsKontur(bottom, right, bottomRight);
                }
            }
        }

        /// <summary>
        /// Determine corner edge type from two adjacent and one diagonal neighbor.
        /// Direct port of AS3: Location.insKontur(adj1, adj2, diag)
        /// 
        /// adj1 = adjacent neighbor in one direction (e.g., top)
        /// adj2 = adjacent neighbor in other direction (e.g., left)
        /// diag = diagonal neighbor (e.g., top-left)
        /// </summary>
        private static int InsKontur(bool adj1, bool adj2, bool diag)
        {
            if (adj1 && adj2)
                return diag ? 0 : 1; // 0=full, 1=inner corner
            if (!adj1 && adj2)
                return 2; // horizontal edge
            if (adj1 && !adj2)
                return 3; // vertical edge
            return 4; // outer corner
        }

        /// <summary>
        /// Check if neighbor is a wall (for wall kontur calculation).
        /// Port of AS3: Location.uslKontur(x, y)
        /// Out-of-bounds counts as wall (room borders are solid).
        /// </summary>
        private static bool IsWallNeighbor(RoomInstance room, int x, int y)
        {
            // Out of bounds = wall (room border)
            if (x < 0 || x >= room.width || y < 0 || y >= room.height)
                return true;

            var tile = room.tiles[x, y];
            if (tile == null) return false;

            // Wall or has a door object attached
            return tile.physicsType == TilePhysicsType.Wall ||
                   !string.IsNullOrEmpty(tile.doorId);
        }

        /// <summary>
        /// Check if neighbor has the same background (for background kontur calculation).
        /// Port of AS3: Location.uslBontur(x, y, backId, isSky)
        /// </summary>
        private static bool IsBackNeighbor(RoomInstance room, int x, int y, string backId)
        {
            if (x < 0 || x >= room.width || y < 0 || y >= room.height)
                return true;

            var tile = room.tiles[x, y];
            if (tile == null) return false;

            // Same background, or is a wall, or has a shelf
            return tile.GetBackGraphic() == backId ||
                   tile.physicsType == TilePhysicsType.Wall ||
                   tile.isLedge;
        }

        private static bool IsPonturNeighbor(RoomInstance room, int x, int y)
        {
            if (x < 0 || x >= room.width || y < 0 || y >= room.height)
            {
                return true;
            }

            var tile = room.tiles[x, y];
            if (tile == null)
            {
                return false;
            }

            return !string.IsNullOrEmpty(tile.GetBackGraphic()) || tile.isLedge;
        }
    }
}

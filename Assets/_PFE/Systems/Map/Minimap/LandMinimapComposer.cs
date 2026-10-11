using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Minimap
{
    /// <summary>Where the player is, for the minimap's "you are here" marker. Absent when the panel
    /// has no player (the tab is reachable with no player, so this is a normal state, not an error).</summary>
    public readonly struct MinimapPlayerMarker
    {
        public readonly bool Present;

        /// <summary>The land cell the player is standing in.</summary>
        public readonly Vector3Int Cell;

        /// <summary>Position within that room, in <b>room-local pixels</b> — the space
        /// <c>UnitInstance.position</c> is in (y up, 0 = the room's floor).</summary>
        public readonly Vector2 RoomLocalPixels;

        public MinimapPlayerMarker(bool present, Vector3Int cell, Vector2 roomLocalPixels)
        {
            Present = present;
            Cell = cell;
            RoomLocalPixels = roomLocalPixels;
        }

        public static MinimapPlayerMarker None => default;
    }

    /// <summary>What the build found — enough for the panel to say "49 rooms, 3 not yet visited"
    /// without walking the map a second time.</summary>
    public readonly struct LandMinimapStats
    {
        public readonly int RoomsTotal;
        public readonly int RoomsDrawn;
        public readonly int RoomsHidden;
        public readonly Vector2Int MinCell;
        public readonly Vector2Int MaxCell;
        public readonly bool RevealAll;

        public LandMinimapStats(
            int roomsTotal, int roomsDrawn, int roomsHidden,
            Vector2Int minCell, Vector2Int maxCell, bool revealAll)
        {
            RoomsTotal = roomsTotal;
            RoomsDrawn = roomsDrawn;
            RoomsHidden = roomsHidden;
            MinCell = minCell;
            MaxCell = maxCell;
            RevealAll = revealAll;
        }
    }

    /// <summary>
    /// Turns a live <see cref="LandMap"/> into a <see cref="LandMinimapBuffer"/>.
    ///
    /// <para><b>The oracle it follows.</b> <c>Land.drawMap</c> (<c>Land.as:1517-1538</c>) clears the
    /// bitmap, walks <c>locs[x][y][0]</c> for x in <c>[minLocX, maxLocX)</c> and y in
    /// <c>[minLocY, maxLocY)</c>, and calls <c>Location.drawMap</c> on each room that is either
    /// <c>World.w.drawAllMap</c> or <c>visited</c>; then it records the player's <c>ggX/ggY</c> for the
    /// marker. This method is that loop, with the port's room store in place of the AS3 arrays.</para>
    ///
    /// <para><b>Visibility is per room, not per tile.</b> The oracle gates the whole room on
    /// <c>visited</c> and then fades individual tiles by their <c>visi</c> (<c>Location.as:2761-2786</c>).
    /// The port has no per-tile visibility — see <see cref="MinimapPalette"/> — so a room is either
    /// drawn at full strength or not drawn at all. <c>revealAll</c> is the port of
    /// <c>World.w.drawAllMap</c> (the console's <c>map</c> command, <c>LandMap.RevealAllRooms</c>).</para>
    ///
    /// <para><b>Pure and side-effect free.</b> It reads the map and writes only the buffer it returns;
    /// it never activates a room, never touches the RNG, and never mutates a tile. That is what makes it
    /// safe to call from a debug panel on every rebuild.</para>
    /// </summary>
    public static class LandMinimapComposer
    {
        /// <summary>
        /// Build the image, or return <c>false</c> when there is nothing to draw (no map, or a map with
        /// no positioned rooms). A <c>false</c> result always leaves <paramref name="buffer"/> null and
        /// <paramref name="stats"/> default, so a caller can branch on the bool alone.
        /// </summary>
        public static bool TryBuild(
            LandMap map,
            bool revealAll,
            in MinimapPlayerMarker player,
            out LandMinimapBuffer buffer,
            out LandMinimapStats stats)
        {
            buffer = null;
            stats = default;

            if (map == null)
            {
                return false;
            }

            var rooms = new List<RoomInstance>();
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null)
                {
                    continue;
                }

                rooms.Add(room);
                Vector3Int p = room.landPosition;
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }

            if (rooms.Count == 0)
            {
                return false;
            }

            int columns = maxX - minX + 1;
            int rows = maxY - minY + 1;

            buffer = new LandMinimapBuffer(columns, rows, WorldConstants.ROOM_WIDTH, WorldConstants.ROOM_HEIGHT);
            buffer.Fill(MinimapPalette.EmptyCell);

            Vector3Int currentCell = map.currentRoom != null ? map.currentRoom.landPosition : new Vector3Int(int.MinValue, 0, 0);

            int drawn = 0, hidden = 0;

            foreach (RoomInstance room in rooms)
            {
                int cellColumn = room.landPosition.x - minX;
                int cellRow = room.landPosition.y - minY;

                if (!revealAll && !room.isVisited)
                {
                    hidden++;
                    continue;
                }

                drawn++;

                if (room.tiles == null)
                {
                    // A domain reload drops Unity's multidimensional arrays, so a live room can have
                    // null tiles. Drawing the cell flat in a distinct colour is the honest answer —
                    // "the room is here, I cannot read it" — and is not the same as "no room here".
                    buffer.FillCellTiles(
                        cellColumn, cellRow, 0, 0, buffer.TileWidth - 1, buffer.TileHeight - 1,
                        MinimapPalette.NoTileData);
                }
                else
                {
                    StampTiles(buffer, room, cellColumn, cellRow);
                }

                StampDoors(buffer, room, cellColumn, cellRow);
                StampObjects(buffer, room, cellColumn, cellRow);
                StampUnits(buffer, room, cellColumn, cellRow);

                if (player.Present && room.landPosition == player.Cell)
                {
                    StampPlayer(buffer, room, cellColumn, cellRow, player.RoomLocalPixels);
                }

                if (room.landPosition == currentCell)
                {
                    buffer.SetCellOutline(cellColumn, cellRow, MinimapPalette.CurrentRoomOutline);
                }
            }

            stats = new LandMinimapStats(
                rooms.Count, drawn, hidden, new Vector2Int(minX, minY), new Vector2Int(maxX, maxY), revealAll);
            return true;
        }

        /// <summary>The port's tile grid, one texel each, coloured by the oracle's palette.</summary>
        private static void StampTiles(LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow)
        {
            int width = Mathf.Min(room.width, buffer.TileWidth);
            int height = Mathf.Min(room.height, buffer.TileHeight);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    TileData tile = room.tiles[x, y];
                    if (tile == null)
                    {
                        continue;
                    }

                    buffer.SetTile(cellColumn, cellRow, x, y, MinimapPalette.TileColor(FactsFor(tile)));
                }
            }
        }

        /// <summary>
        /// The primitive view of a tile the palette branches on. Kept next to the composer because it is
        /// the port-side half of the transcription: every line here has an AS3 counterpart named in
        /// <see cref="MinimapTileFacts"/>.
        /// </summary>
        private static MinimapTileFacts FactsFor(TileData tile)
        {
            // `doorOcclusion >= 0` rather than `!= NoDoorOcclusion`: the sentinel is -1 and a real
            // occlusion is 0..1, so the sign is the property and does not depend on a float equality
            // against a constant. A door that is OPEN writes 0 — still "a door covers this tile", which
            // is what the oracle's tile-level `door` flag means too.
            bool hasDoor = tile.doorOcclusion >= 0f;

            return new MinimapTileFacts(
                isWall: tile.physicsType == TilePhysicsType.Wall,
                isShelf: tile.physicsType == TilePhysicsType.Platform,
                isSlope: tile.slopeType != 0,
                isStair: tile.stairType != 0 || tile.physicsType == TilePhysicsType.Stair,
                hasWater: tile.hasWater,
                isIndestructible: tile.indestructible,
                hasDoor: hasDoor,
                hitPoints: tile.hitPoints);
        }

        /// <summary>
        /// A door opening. Read from the room's <see cref="DoorInstance"/> list, not from the tiles: a
        /// door's tiles are plain ground until the door prop stamps them solid, so a tile-only reading
        /// loses every open door — and "there is no door between these two rooms" is exactly the
        /// question a land-integrity check asks.
        /// </summary>
        private static void StampDoors(LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow)
        {
            if (room.doors == null)
            {
                return;
            }

            foreach (DoorInstance door in room.doors)
            {
                if (door == null || !door.isActive)
                {
                    continue;
                }

                buffer.SetTile(
                    cellColumn,
                    cellRow,
                    Mathf.Clamp(door.tilePosition.x, 0, buffer.TileWidth - 1),
                    Mathf.Clamp(door.tilePosition.y, 0, buffer.TileHeight - 1),
                    MinimapPalette.MarkerDoor);
            }
        }

        /// <summary>
        /// The oracle's four object overlays (<c>Location.as:2792-2816</c>), plus the port's exit split.
        ///
        /// <para><b>How each oracle category maps.</b> The oracle reads <c>Interact.cont</c> (a container
        /// holds something), <c>Interact.prob</c> (the object enters a prob room), the <c>CheckPoint</c>
        /// class, and <c>Unit.npc</c>. The port has no <c>cont</c> string — a container's contents are
        /// the object's own <c>items</c> list — so "holds something" is <c>items.Count &gt; 0</c>; prob
        /// is <see cref="ObjectInstance.GetProb"/>; and a checkpoint is an object whose type names one.
        /// An exit is called out separately: the oracle paints it gold like any other interactable, but
        /// it is the object the descent loop turns on, so the port gives it its own colour.</para>
        ///
        /// <para><b>Priority is by specificity</b>, so a prob door that is also a container shows as a
        /// prob door: the rarest thing is the one worth seeing.</para>
        /// </summary>
        private static void StampObjects(LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow)
        {
            if (room.objects == null)
            {
                return;
            }

            foreach (ObjectInstance obj in room.objects)
            {
                if (obj == null)
                {
                    continue;
                }

                Color32 color;
                if (!string.IsNullOrEmpty(obj.GetProb()))
                {
                    color = MinimapPalette.MarkerProb;
                }
                else if (ContainsIgnoreCase(obj.objectType, "checkpoint"))
                {
                    color = MinimapPalette.MarkerCheckpoint;
                }
                else if (ContainsIgnoreCase(obj.objectType, "exit"))
                {
                    color = MinimapPalette.MarkerExit;
                }
                else if (obj.items != null && obj.items.Count > 0)
                {
                    color = MinimapPalette.MarkerInteractable;
                }
                else
                {
                    continue;
                }

                StampObjectFootprint(buffer, room, cellColumn, cellRow, obj.position, obj.GetApproximatePixelSize(), color);
            }
        }

        /// <summary>
        /// Units. <b>A recorded divergence:</b> the oracle marks only <c>Unit.npc</c>; the port's
        /// <c>UnitInstance</c> carries no npc flag, so every live unit is marked. A land whose units are
        /// all enemies therefore shows every one of them — noisier than the oracle, but it never claims a
        /// unit is not there when it is.
        /// </summary>
        private static void StampUnits(LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow)
        {
            if (room.units == null)
            {
                return;
            }

            foreach (UnitInstance unit in room.units)
            {
                if (unit == null || unit.IsDead)
                {
                    continue;
                }

                Vector2Int tile = WorldCoordinates.PixelToTile(unit.position);
                buffer.SetTile(
                    cellColumn, cellRow,
                    Mathf.Clamp(tile.x, 0, buffer.TileWidth - 1),
                    Mathf.Clamp(tile.y, 0, buffer.TileHeight - 1),
                    MinimapPalette.MarkerNpc);
            }
        }

        /// <summary>
        /// The player. The oracle positions a <c>plTag</c> sprite at <c>ggX/ggY</c>
        /// (<c>Land.as:1535-1536</c>) — the player's position in the land's pixel space divided by the
        /// tile size (<c>PipPageInfo.as:831-832</c>). This is the same idea reduced to the tile the
        /// player's feet are in, which is what a one-texel-per-tile image can express.
        /// </summary>
        private static void StampPlayer(
            LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow, Vector2 roomLocalPixels)
        {
            Vector2Int tile = WorldCoordinates.PixelToTile(roomLocalPixels);
            buffer.SetTile(
                cellColumn, cellRow,
                Mathf.Clamp(tile.x, 0, buffer.TileWidth - 1),
                Mathf.Clamp(tile.y, 0, buffer.TileHeight - 1),
                MinimapPalette.Player);
        }

        /// <summary>
        /// Fill the tiles an object's footprint covers, from its room-local pixel position and size.
        /// The oracle's <c>drawMapObj</c> does the same with the object's pixel bounds
        /// (<c>Location.as:2819-2833</c>).
        /// </summary>
        private static void StampObjectFootprint(
            LandMinimapBuffer buffer, RoomInstance room, int cellColumn, int cellRow,
            Vector2 position, Vector2 sizePixels, Color32 color)
        {
            // The footprint is centred on x and grows up from y (GetApproximateBounds), so the min/max
            // are built the same way here rather than by re-deriving the rect.
            float minX = position.x - sizePixels.x * 0.5f;
            float maxX = position.x + sizePixels.x * 0.5f;
            float minY = position.y;
            float maxY = position.y + sizePixels.y;

            int fromTileX = Mathf.Clamp(Mathf.FloorToInt(minX / WorldConstants.TILE_SIZE), 0, buffer.TileWidth - 1);
            int toTileX = Mathf.Clamp(Mathf.FloorToInt((maxX - 0.01f) / WorldConstants.TILE_SIZE), 0, buffer.TileWidth - 1);
            int fromTileY = Mathf.Clamp(Mathf.FloorToInt(minY / WorldConstants.TILE_SIZE), 0, buffer.TileHeight - 1);
            int toTileY = Mathf.Clamp(Mathf.FloorToInt((maxY - 0.01f) / WorldConstants.TILE_SIZE), 0, buffer.TileHeight - 1);

            buffer.FillCellTiles(cellColumn, cellRow, fromTileX, fromTileY, toTileX, toTileY, color);
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

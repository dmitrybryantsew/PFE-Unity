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

        // ── The LAND-level Y flip ────────────────────────────────────────────────────────────
        //
        // AS3's land grid runs Y DOWNWARD, exactly like its tile grid: the camera target is
        // `ggY = (landY - minLocY) * cellsY * tileY + ...` (Land.as:1536) and Flash screen Y
        // grows down, so a larger `landY` is LOWER on screen. Unity's world +Y grows UP.
        //
        // The port copied AS3's `landY` straight into `landPosition.y` and then multiplied it by
        // `ROOM_HEIGHT * TILE_SIZE` with a POSITIVE sign, so every land with more than one row was
        // rendered vertically MIRRORED against the original: grid row 1 drew above grid row 0.
        // The 23 single-row authored lands hid it (one cell has no vertical arrangement to mirror);
        // the 12 multi-row authored lands and all 10 procedural lands did not.
        //
        // The flip belongs HERE, at the one place a land row becomes a world Y. The port's grid
        // keeps AS3's numbering — `landPosition.y + 1` is still "the row below", `DoorSide.Bottom`
        // is still the neighbour at `y + 1`, and `Land.gotoLoc` case 3 still steps `y + 1` — because
        // all of that was already transcribed faithfully from the oracle and is only correct once
        // the rendering agrees with it. Changing the grid instead would have meant renumbering
        // every one of those against AS3, for no gain.
        //
        // Only `landRow` is negated; `borderOffsetTiles` is a WITHIN-cell inset and is not a land
        // quantity. It is 0 in every path today (`RoomInstance.borderOffset` is only ever assigned
        // 0, in Doorcarver.cs), so its sign is not observable either way and is left as-is rather
        // than guessed at.

        /// <summary>
        /// A land grid row → the world pixel Y of that row's origin (its bottom edge). Row 0 sits at
        /// 0 and each row below it is one room height more negative, so a larger row is lower.
        /// </summary>
        public static float LandRowToWorldPixelY(int landRow)
        {
            return -(landRow * WorldConstants.ROOM_SIZE_PIXELS.y);
        }

        /// <summary>
        /// The world pixel Y of a room's origin. <paramref name="borderOffsetTiles"/> is the room's
        /// within-cell inset, passed through unchanged (see the section remarks).
        /// </summary>
        public static float RoomOriginPixelY(int landRow, int borderOffsetTiles)
        {
            return LandRowToWorldPixelY(landRow) - borderOffsetTiles * WorldConstants.TILE_SIZE;
        }

        /// <summary>
        /// The world pixel X of a room's origin. X is not flipped — only the land's Y axis is.
        /// </summary>
        public static float RoomOriginPixelX(int landColumn, int borderOffsetTiles)
        {
            return landColumn * WorldConstants.ROOM_SIZE_PIXELS.x
                   - borderOffsetTiles * WorldConstants.TILE_SIZE;
        }

        /// <summary>
        /// Convert land grid position to world space (pixels)
        /// worldX = (landX * roomWidth * tileSize) + localX
        /// worldY = -(landY * roomHeight * tileSize) + localY   — see the land-Y section above.
        /// </summary>
        public static Vector2 LandToWorld(Vector3Int landCoord, Vector2 localPos)
        {
            return new Vector2(
                landCoord.x * WorldConstants.ROOM_SIZE_PIXELS.x + localPos.x,
                LandRowToWorldPixelY(landCoord.y) + localPos.y
            );
        }

        /// <summary>
        /// Convert world position to land grid coordinates. The Y row is the inverse of
        /// <see cref="LandRowToWorldPixelY"/>: negate, then floor, so that a row spans
        /// <c>[-row * H, (1 - row) * H)</c>.
        /// </summary>
        public static Vector3Int WorldToLand(Vector2 worldPos)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / WorldConstants.ROOM_SIZE_PIXELS.x),
                -Mathf.FloorToInt(worldPos.y / WorldConstants.ROOM_SIZE_PIXELS.y),
                0
            );
        }

        /// <summary>
        /// Convert world position to local position within its room. Both components land in
        /// <c>[0, size)</c>: the Y flip makes world Y negative below row 0, so a plain `%` would
        /// return a negative local Y and every consumer would need its own fix-up.
        /// </summary>
        public static Vector2 WorldToLocal(Vector2 worldPos)
        {
            float sizeX = WorldConstants.ROOM_SIZE_PIXELS.x;
            float sizeY = WorldConstants.ROOM_SIZE_PIXELS.y;

            float localX = worldPos.x % sizeX;
            if (localX < 0f) localX += sizeX;

            float localY = worldPos.y % sizeY;
            if (localY < 0f) localY += sizeY;

            return new Vector2(localX, localY);
        }

        // ── AS3 ↔ port Y axis ────────────────────────────────────────────────────────────────
        //
        // AS3 rooms index tile ROWS from the TOP (row 0 is the ceiling) and run Y downward.
        // The port's tile grid — what `RoomInstance.GetTileAtCoord` and
        // `ITileQueryService.Classify` index — runs the other way: port row 0 is the FLOOR.
        // So the two are related by a mirror about the room's full height, and NOT by a plain
        // floor. The mirror is load-bearing at cell boundaries, which is why it is one shared
        // function rather than a `- 1` each caller re-derives:
        //
        //   * `Doorcarver.As3RowToUnityY` (the door carver) has always used this form.
        //   * `RoomBackdropRenderer` reaches the same value the long way round, as
        //     `borderOffset + contentHeight - as3Row - decorationHeight`, which reduces to
        //     `height - 1 - as3Row` once the border is added back on both sides.
        //   * `ThrownObject.CellAt` derives it from the other direction and lands on
        //     `ceil(portY / 40) - 1` for a Y-up port pixel.
        //
        // A plain `floor` agrees with this everywhere EXCEPT on an exact tile boundary
        // (`portY = k * 40`), where it names the row above — see `ThrownObject.CellAt`'s remarks
        // for why that single-cell error is a real defect and not an epsilon.

        /// <summary>
        /// AS3 tile row → port tile row. See the section remarks above for why this is not a
        /// plain floor and why it is shared.
        /// </summary>
        /// <param name="as3Row">AS3 row index, 0 = ceiling.</param>
        /// <param name="roomHeightTiles">The room's height in tiles (<c>RoomInstance.height</c>).</param>
        public static int As3RowToUnityRow(int as3Row, int roomHeightTiles)
        {
            return roomHeightTiles - 1 - as3Row;
        }

        /// <summary>Port tile row → AS3 tile row. The exact inverse of <see cref="As3RowToUnityRow"/>.</summary>
        public static int UnityRowToAs3Row(int unityRow, int roomHeightTiles)
        {
            return roomHeightTiles - 1 - unityRow;
        }

        /// <summary>
        /// AS3 room-local pixel Y (downward) → port tile row. The AS3 side of this pair is the
        /// space <c>Emitter.emit</c> is called in and therefore the space
        /// <c>ParticleState.Y</c> lives in.
        /// </summary>
        public static int As3YToUnityRow(float as3Y, int roomHeightTiles)
        {
            return As3RowToUnityRow(Mathf.FloorToInt(as3Y / WorldConstants.TILE_SIZE), roomHeightTiles);
        }

        /// <summary>
        /// AS3 room-local pixel Y (downward, 0 = ceiling) → port room-local pixel Y (upward,
        /// 0 = floor). This is the position half of the same mirror, for consumers that place
        /// something in room-local space rather than naming a tile.
        /// </summary>
        /// <remarks>
        /// The mirror is its own inverse — <c>H - (H - y) = y</c> — so the same call converts in
        /// either direction. That is a property worth relying on deliberately rather than a
        /// coincidence: it is why <see cref="UnityWorldToAs3RoomLocal"/> can be written with this
        /// call on the way out.
        /// </remarks>
        public static float As3YToUnityLocalY(float as3Y, int roomHeightTiles)
        {
            return roomHeightTiles * WorldConstants.TILE_SIZE - as3Y;
        }

        /// <summary>
        /// Unity world position → <b>AS3 room-local pixels</b>, the space <c>Emitter.emit</c> and
        /// <c>ParticleState.X/Y</c> are in.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one conversion every particle emitter call site needs</b>, and the
        /// reason it lives here rather than at each caller: the particle pipeline has roughly ninety
        /// call sites across twenty files, and a per-caller conversion would be the same three lines
        /// copy-pasted ninety times — with the Y mirror wrong in whichever copy was written last.
        /// See <see cref="ParticleState"/>-side notes in <c>ParticleSpec.cs</c> for why the pipeline is
        /// in AS3 space at all.</para>
        /// <para>Two steps, and both are load-bearing: subtract the room origin (a Unity world position
        /// is a <i>world</i> pixel, and the room's origin is not the world origin in any room but the
        /// first), then mirror Y.</para>
        /// </remarks>
        /// <param name="unityPos">The Unity world position (e.g. a projectile's impact point).</param>
        /// <param name="originPixel">
        /// The room's origin in world pixels — <c>ITileQueryService.OriginPixel</c>. Zero for a room at
        /// land (0,0), which is exactly why a consumer that forgets it looks perfect in the room
        /// everyone tests in.
        /// </param>
        /// <param name="roomHeightTiles">The room's height in tiles (<c>RoomInstance.height</c>).</param>
        public static Vector2 UnityWorldToAs3RoomLocal(Vector3 unityPos, Vector2 originPixel, int roomHeightTiles)
        {
            Vector2 portLocal = UnityToPixel(unityPos) - originPixel;

            // The mirror is its own inverse, so the same call does the flip either way.
            return new Vector2(portLocal.x, As3YToUnityLocalY(portLocal.y, roomHeightTiles));
        }

        /// <summary>
        /// The exact inverse of <see cref="UnityWorldToAs3RoomLocal"/> — AS3 room-local pixels back to
        /// a Unity world position. Needed by anything that has an AS3-space point (a stored impact, a
        /// round trip through a fixture) and must place a Unity object at it.
        /// </summary>
        public static Vector3 As3RoomLocalToUnityWorld(Vector2 as3Local, Vector2 originPixel, int roomHeightTiles)
        {
            Vector2 portLocal = new Vector2(as3Local.x, As3YToUnityLocalY(as3Local.y, roomHeightTiles));
            return PixelToUnity(portLocal + originPixel);
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

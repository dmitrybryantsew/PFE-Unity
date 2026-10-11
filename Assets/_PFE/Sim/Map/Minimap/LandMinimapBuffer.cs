using UnityEngine;

namespace PFE.Systems.Map.Minimap
{
    /// <summary>
    /// A mutable minimap image: one <b>texel per tile</b>, sized to a land grid of
    /// <c>columns × rows</c> rooms of <c>tileWidth × tileHeight</c> tiles each.
    ///
    /// <para><b>This is the same shape the oracle builds.</b> <c>Land.prepareRooms</c> allocates
    /// <c>new BitmapData(World.cellsX * (maxLocX - minLocX), World.cellsY * (maxLocY - minLocY), …)</c>
    /// (<c>Land.as:1140</c>) and <c>Location.drawMap</c> writes one pixel per tile at
    /// <c>(landX - minLocX) * cellsX + tileX</c> (<c>Location.as:2787</c>). The pixel buffer is the
    /// oracle's own representation, not an approximation of it.</para>
    ///
    /// <para><b>Unity-free arithmetic, Unity-typed pixels.</b> Every method here is a pure function of
    /// its arguments, so the whole class runs in the offline test wall. It holds <see cref="Color32"/>
    /// rather than a packed int because that is what <c>Texture2D.SetPixels32</c> consumes and what a
    /// test can assert on without unpacking bytes.</para>
    /// </summary>
    public sealed class LandMinimapBuffer
    {
        /// <summary>Rooms across, left to right. Never flipped.</summary>
        public int Columns { get; }

        /// <summary>Rooms down, top to bottom (AS3's land-row order — row 0 is the top row).</summary>
        public int Rows { get; }

        public int TileWidth { get; }
        public int TileHeight { get; }

        /// <summary>Image width in texels (<c>Columns * TileWidth</c>).</summary>
        public int Width { get; }

        /// <summary>Image height in texels (<c>Rows * TileHeight</c>).</summary>
        public int Height { get; }

        /// <summary>Row-major, <b>bottom-up</b>: index 0 is the bottom-left texel, which is what
        /// <c>Texture2D.SetPixels32</c> expects.</summary>
        public Color32[] Pixels { get; }

        public LandMinimapBuffer(int columns, int rows, int tileWidth, int tileHeight)
        {
            Columns = Mathf.Max(0, columns);
            Rows = Mathf.Max(0, rows);
            TileWidth = Mathf.Max(0, tileWidth);
            TileHeight = Mathf.Max(0, tileHeight);

            Width = Columns * TileWidth;
            Height = Rows * TileHeight;
            Pixels = new Color32[Width * Height];
        }

        // ── Layout: the one place the two Y reversals are reconciled ────────────────────────────
        //
        // There are three Y conventions in play and they do not all agree:
        //
        //   1. The LAND grid keeps AS3's numbering: row 0 is the TOP row and row +1 is BELOW.
        //      (WorldCoordinates' land-Y section — the port only negates at render time.)
        //   2. The ROOM's own tile grid is the port's: row 0 is the FLOOR and row +1 is UP.
        //      (RoomInstance.GetTileAtCoord / ITileQueryService.Classify.)
        //   3. A Unity Texture2D is BOTTOM-UP: texel row 0 is the bottom of the drawn image.
        //
        // An image must be drawn with land row 0 at the TOP and, within each room, the floor at the
        // BOTTOM. Going straight from (1) to (3) is one reversal; going from (2) to (3) is a second,
        // opposite one. They cancel:
        //
        //   TexelY = (Rows - 1 - cellRow) * TileHeight + portTileY
        //
        // Check the corners. A room at land row 0 occupies the top band of the image, texel rows
        // [(Rows-1)*TileHeight, Rows*TileHeight) — its last row is the image's top. Its port tile row 0
        // (the floor) lands at the band's first texel row, which is the band's bottom. Both correct.
        //
        // X is not mirrored anywhere: the oracle indexes it as `(landX - minLocX) * cellsX + tileX`
        // with no flip (Location.as:2787), and the port agrees.

        /// <summary>Texel column of a tile. No flip on X.</summary>
        public static int TexelX(int cellColumn, int tileX, int tileWidth)
        {
            return cellColumn * tileWidth + tileX;
        }

        /// <summary>
        /// Texel row of a tile. See the layout section above for the derivation of why the two Y
        /// reversals cancel into one addition.
        /// </summary>
        /// <param name="rows">The land grid's height in rooms.</param>
        /// <param name="cellRowFromTop">AS3 land row — 0 is the top row.</param>
        /// <param name="portTileY">The port's tile row within the room — 0 is the floor.</param>
        public static int TexelY(int rows, int cellRowFromTop, int portTileY, int tileHeight)
        {
            return (rows - 1 - cellRowFromTop) * tileHeight + portTileY;
        }

        /// <summary>Write one tile's colour. Out-of-range coordinates are ignored, so a caller may pass
        /// a tile row straight from the room without clamping.</summary>
        public void SetTile(int cellColumn, int cellRowFromTop, int tileX, int tileY, Color32 color)
        {
            int x = TexelX(cellColumn, tileX, TileWidth);
            int y = TexelY(Rows, cellRowFromTop, tileY, TileHeight);
            SetPixel(x, y, color);
        }

        /// <summary>Write one texel. Out-of-range coordinates are ignored.</summary>
        public void SetPixel(int x, int y, Color32 color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return;
            }

            Pixels[y * Width + x] = color;
        }

        /// <summary>Read one texel, or <c>default</c> when the coordinate is outside the image.</summary>
        public Color32 GetPixel(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return default;
            }

            return Pixels[y * Width + x];
        }

        /// <summary>Fill the whole image.</summary>
        public void Fill(Color32 color)
        {
            for (int i = 0; i < Pixels.Length; i++)
            {
                Pixels[i] = color;
            }
        }

        /// <summary>
        /// Draw a one-texel border around a room's block. Used for the "you are here" cell, where a
        /// one-tile-wide outline reads at any zoom and cannot be confused with a wall.
        /// </summary>
        public void SetCellOutline(int cellColumn, int cellRowFromTop, Color32 color)
        {
            int left = cellColumn * TileWidth;
            int right = left + TileWidth - 1;
            int bottom = TexelY(Rows, cellRowFromTop, 0, TileHeight);
            int top = bottom + TileHeight - 1;

            for (int x = left; x <= right; x++)
            {
                SetPixel(x, bottom, color);
                SetPixel(x, top, color);
            }

            for (int y = bottom; y <= top; y++)
            {
                SetPixel(left, y, color);
                SetPixel(right, y, color);
            }
        }

        /// <summary>
        /// Fill a rectangle of a room's block, given a tile range in the port's tile space (y = 0 is
        /// the floor). The range is inclusive on both ends and clamped to the room.
        /// </summary>
        public void FillCellTiles(
            int cellColumn,
            int cellRowFromTop,
            int fromTileX,
            int fromTileY,
            int toTileX,
            int toTileY,
            Color32 color)
        {
            if (fromTileX > toTileX) (fromTileX, toTileX) = (toTileX, fromTileX);
            if (fromTileY > toTileY) (fromTileY, toTileY) = (toTileY, fromTileY);

            for (int tileX = fromTileX; tileX <= toTileX; tileX++)
            {
                for (int tileY = fromTileY; tileY <= toTileY; tileY++)
                {
                    SetTile(cellColumn, cellRowFromTop, tileX, tileY, color);
                }
            }
        }
    }
}

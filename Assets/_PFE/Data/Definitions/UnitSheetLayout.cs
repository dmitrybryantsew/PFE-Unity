using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// The arithmetic that turns an AS3 unit sheet into cells, and a sheet file into an AS3 symbol name.
    ///
    /// <para><b>Why this is a separate type.</b> It is pure — no asset pipeline, no editor, no file
    /// system beyond the filename — so the two rules most likely to be silently wrong can be tested
    /// directly. Both are the kind that produce a <i>plausible</i> result rather than an error:
    /// a sheet mirrored vertically still has the right number of cells, and a pivot off by a flip still
    /// sits inside the cell.</para>
    ///
    /// <para>Lives in <c>PFE.Core</c> rather than the importer so the runtime can address the same grid
    /// with the same code the importer sliced it with — the two must agree on
    /// <see cref="CellIndex"/> and <see cref="CellName"/> or every lookup misses.</para>
    /// </summary>
    public static class UnitSheetLayout
    {
        /// <summary>
        /// The pixel rect of one cell within its sheet, for Unity's sprite-rect space.
        ///
        /// <para><b>The row is flipped.</b> PNG row 0 is the <i>top</i> row, and AS3 counts rows
        /// downward from it (<c>blitRect.y = row * blitY</c>, <c>Unit.as:2867</c>), but Unity sprite
        /// rects have a bottom-left origin. So AS3 row <c>r</c> of <c>rows</c> sits at
        /// <c>y = (rows - 1 - r) * cellY</c>.</para>
        /// </summary>
        public static Rect CellRect(int row, int col, int rows, Vector2Int cell)
        {
            int y = (rows - 1 - row) * cell.y;
            return new Rect(col * cell.x, y, cell.x, cell.y);
        }

        /// <summary>
        /// The Unity pivot for a cell, from the AS3 registration point.
        ///
        /// <para><c>sprDX</c>/<c>sprDY</c> name a pixel <i>inside the cell</i> that must land on the
        /// unit's origin: <c>Unit.as:2846-2858</c> offsets the cell to <c>(-blitDX, -blitDY)</c>. They
        /// are not a draw size. Flash's y grows downward while Unity's pivot grows upward, so the y term
        /// is flipped.</para>
        ///
        /// <para>A negative component means "not declared" — the oracle's own default is <c>-1</c>
        /// (<c>Unit.as:511-514</c>), which is what makes its <c>&gt;= 0</c> test a presence test. AS3 then
        /// centres horizontally (<c>-blitX / 2</c>) and sits 10px above the cell's bottom
        /// (<c>-blitY + 10</c>), i.e. <c>pivotY = 10 / cellY</c>. That is the "feet on the ground"
        /// default, reproduced rather than replaced with a plain centre.</para>
        ///
        /// <para>The result is <b>not</b> clamped: AS3 permits a registration point outside the cell, and
        /// clamping is a loss of information the caller should report rather than hide.</para>
        /// </summary>
        public static Vector2 PivotFor(Vector2Int registration, Vector2Int cell)
        {
            float x = registration.x >= 0 ? (float)registration.x / cell.x : 0.5f;
            float y = registration.y >= 0 ? 1f - (float)registration.y / cell.y : 10f / cell.y;
            return new Vector2(x, y);
        }

        /// <summary>
        /// The flat index of a cell in a row-major sheet array:
        /// <c>row * columns + column</c>.
        ///
        /// <para>Row-major over the whole sheet, because AS3 keeps the state's row and its frame as two
        /// independent numbers and indexes the sheet with both.</para>
        /// </summary>
        public static int CellIndex(int row, int col, int columns) => row * columns + col;

        /// <summary>
        /// The stable name of a cell sprite. Carries both coordinates so a lookup can be rebuilt from the
        /// imported asset by name, and so a name is unique even when two states share a row.
        /// </summary>
        public static string CellName(string sheetId, int row, int col) => $"{sheetId}_r{row}_c{col}";

        /// <summary>
        /// The true pixel size of a PNG, read from its own IHDR chunk.
        ///
        /// <para><b>Why not just ask the imported <c>Texture2D</c>.</b> Because that answers a different
        /// question — "how big is the texture after Unity's import settings" — and the two differ
        /// silently whenever a setting resamples. Unity's <c>maxTextureSize</c> defaults to <b>2048</b>
        /// and quietly downscales anything larger, so a 2880x1080 sheet arrives as 2048x768. Slicing a
        /// grid from that size produces a wrong row count, wrong column count, and every rect in the
        /// wrong place, with no error anywhere. Reading the file makes the grid independent of import
        /// settings entirely.</para>
        ///
        /// <para>A PNG begins with an 8-byte signature, then the IHDR chunk: a 4-byte length, the ASCII
        /// type <c>IHDR</c>, then width and height as big-endian 32-bit integers. Those are at byte
        /// offsets 16 and 20.</para>
        /// </summary>
        /// <returns>False if the file is missing, not a PNG, or too short to hold a header.</returns>
        public static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    var header = new byte[24];
                    if (stream.Read(header, 0, header.Length) != header.Length)
                    {
                        return false;
                    }

                    // Signature: 89 50 4E 47 0D 0A 1A 0A
                    if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                    {
                        return false;
                    }

                    // The first chunk must be IHDR, at offsets 12..15.
                    if (header[12] != (byte)'I' || header[13] != (byte)'H' ||
                        header[14] != (byte)'D' || header[15] != (byte)'R')
                    {
                        return false;
                    }

                    width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

                    return width > 0 && height > 0;
                }
            }
            catch (Exception)
            {
                // Unreadable or vanished file. The caller reports a missing size; it must not throw.
                return false;
            }
        }

        /// <summary>
        /// The cell a unit shows when it is standing still, as <c>(column, row)</c> — the same
        /// component order as <see cref="UnitDefinition.iconCell"/>.
        ///
        /// <para><b>Why this is not <c>iconCell</c>.</b> <c>icoX</c>/<c>icoY</c> look like "the cell
        /// that is the unit's picture", but the oracle puts them somewhere else entirely:
        /// <c>Unit.initIco</c> (<c>Unit.as:910-948</c>) blits that cell into <c>Unit.arrIcos</c>, a
        /// static table whose <i>only</i> reader is <c>PipPageInfo.as:439-442</c> — the PipBoy unit
        /// list. The world renderer never looks at it. Five units declare <c>icoY</c>
        /// (<c>merc1</c>–<c>merc5</c>, <c>icoY='2'</c>) and the <c>merc</c> family puts <c>die</c> at
        /// row 2, so using the icon cell as the resting frame draws a corpse.</para>
        ///
        /// <para>The resting state is whatever <c>animState</c> holds, and every unit class
        /// initialises that to <c>"stay"</c> (<c>Unit.as:2860</c>, <c>UnitAlicorn.as:189</c>,
        /// <c>UnitAnt.as:52</c>, …). All 26 <c>stay</c> declarations in <c>AllData.as</c> resolve to
        /// row 0 — 23 by the absent-<c>y</c> default, 3 written as <c>y='0'</c> — but the row is read
        /// from the state rather than assumed.</para>
        /// </summary>
        /// <returns>
        /// <c>(0,0)</c> when the set has no <c>stay</c> state with frames, which is the correct
        /// fallback: the sheet's first cell.
        /// </returns>
        public static Vector2Int RestingCell(AnimationSet animations)
        {
            AnimationFrame stay = animations != null ? animations.Get("stay") : default;
            return stay.HasFrames
                ? new Vector2Int(stay.firstFrame, stay.row)
                : Vector2Int.zero;
        }

        /// <summary>
        /// The AS3 symbol name for an exported sheet file.
        ///
        /// <para>The JPEXS export prefixes every bitmap with its symbol index — <c>24_sprRaider5.png</c> is
        /// the symbol <c>sprRaider5</c> — and that index is not part of the name AS3 uses, so it must be
        /// stripped before the sheet can be matched to <c>&lt;vis blit='sprRaider5'/&gt;</c>. Only a
        /// leading <c>digits_</c> is removed, so a name that merely contains an underscore survives
        /// unchanged.</para>
        /// </summary>
        public static string SheetNameFromFile(string pathOrFileName)
        {
            return Regex.Replace(Path.GetFileNameWithoutExtension(pathOrFileName), @"^\d+_", string.Empty);
        }

        /// <summary>
        /// The frame index in a DisplayObject export filename. The export writes <c>1.png</c>,
        /// <c>2.png</c> … and the importer copies them out as <c>f001.png</c>; both spellings parse to
        /// the same number, so ordering stays numeric (a lexical sort would place frame 10 between 1
        /// and 2).
        /// </summary>
        public static int FrameNumberOf(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(1);
            }

            return int.TryParse(name, out int n) ? n : 0;
        }
    }
}

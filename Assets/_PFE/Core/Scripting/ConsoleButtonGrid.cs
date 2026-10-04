namespace PFE.Core.Scripting
{
    /// <summary>
    /// The geometry of the developer console's quick-action button grid.
    ///
    /// <para><b>Why this exists.</b> The button row used to be a <c>GUILayout.BeginHorizontal</c> of
    /// fixed-width buttons. A horizontal row does not wrap, so once the row grew past the window the
    /// buttons simply ran off the right edge: they were laid out, but the last few were off-screen and
    /// unclickable. The row is now a grid whose cells divide the console's own width, so it cannot
    /// overflow at any window size — columns are as many as fit, and the last row may be partial.</para>
    ///
    /// <para><b>Why the arithmetic lives here and not inline in <c>OnGUI</c>.</b> "Does it fit?" is the
    /// exact question the user asked, and it is answerable by a test only if the numbers are reachable
    /// without a Unity window. This type uses no <c>UnityEngine</c> at all — no <c>Rect</c>, no
    /// <c>Mathf</c>, no <c>MonoBehaviour</c> — so the offline test wall can execute it. The caller builds
    /// the <c>Rect</c>s from the values here.</para>
    ///
    /// <para>The contract is one line: <c>ColumnX(lastColumn) + CellDrawWidth &lt;= areaWidth</c> for
    /// every column. Nothing in the grid is ever placed outside the area it was given.</para>
    /// </summary>
    public static class ConsoleButtonGrid
    {
        /// <summary>
        /// Lay <paramref name="buttonCount"/> buttons out inside an area <paramref name="areaWidth"/>
        /// pixels wide.
        ///
        /// <para>Columns are as many as fit at <paramref name="minCellWidth"/> each, clamped to
        /// <c>[1, buttonCount]</c> — so a narrow console falls back to one column (a vertical list) and a
        /// wide one gets a single row. Every column is exactly <c>areaWidth / columns</c> wide, so the
        /// grid spans the whole area rather than a fixed width that happens to be smaller.</para>
        /// </summary>
        public static ConsoleButtonGridLayout Compute(
            int buttonCount,
            float areaWidth,
            float minCellWidth = DefaultMinCellWidth,
            float cellHeight = DefaultCellHeight,
            float spacing = DefaultSpacing)
        {
            if (buttonCount <= 0)
            {
                return new ConsoleButtonGridLayout(0, 0, 0f, cellHeight, spacing, 0f);
            }

            // A degenerate area still has to produce a drawable grid; the caller clamps the drawn cell
            // width, so one column of zero width is the honest answer rather than a divide by zero.
            if (areaWidth < 0f) areaWidth = 0f;
            if (minCellWidth <= 0f) minCellWidth = 1f;

            int columns = (int)(areaWidth / minCellWidth);
            if (columns < 1) columns = 1;
            if (columns > buttonCount) columns = buttonCount;

            float cellWidth = areaWidth / columns;
            int rows = (buttonCount + columns - 1) / columns;
            float totalHeight = rows * (cellHeight + spacing);

            return new ConsoleButtonGridLayout(columns, rows, cellWidth, cellHeight, spacing, totalHeight);
        }

        /// <summary>Narrowest cell the console draws before dropping to fewer columns.</summary>
        public const float DefaultMinCellWidth = 120f;

        /// <summary>Height of one button row.</summary>
        public const float DefaultCellHeight = 22f;

        /// <summary>Gap left between cells, horizontally and between rows.</summary>
        public const float DefaultSpacing = 4f;
    }

    /// <summary>
    /// The result of <see cref="ConsoleButtonGrid.Compute"/>: how the buttons are arranged, and how tall
    /// the whole arrangement is so the caller can reserve exactly that much vertical space.
    /// </summary>
    public readonly struct ConsoleButtonGridLayout
    {
        public ConsoleButtonGridLayout(
            int columns,
            int rows,
            float cellWidth,
            float cellHeight,
            float spacing,
            float totalHeight)
        {
            Columns = columns;
            Rows = rows;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
            Spacing = spacing;
            TotalHeight = totalHeight;
        }

        /// <summary>How many cells per row.</summary>
        public int Columns { get; }

        /// <summary>How many rows the buttons occupy.</summary>
        public int Rows { get; }

        /// <summary>Full width of one column, gap included.</summary>
        public float CellWidth { get; }

        /// <summary>Height of one row.</summary>
        public float CellHeight { get; }

        /// <summary>Gap between cells.</summary>
        public float Spacing { get; }

        /// <summary>Height to reserve for the whole grid, rows and inter-row gaps included.</summary>
        public float TotalHeight { get; }

        /// <summary>Left edge of a column, in area-local pixels.</summary>
        public float ColumnX(int column)
        {
            return column * CellWidth;
        }

        /// <summary>Top edge of a row, in area-local pixels.</summary>
        public float RowY(int row)
        {
            return row * (CellHeight + Spacing);
        }

        /// <summary>Width of a button once the inter-column gap is removed.</summary>
        public float CellDrawWidth
        {
            get { return CellWidth - Spacing; }
        }
    }
}

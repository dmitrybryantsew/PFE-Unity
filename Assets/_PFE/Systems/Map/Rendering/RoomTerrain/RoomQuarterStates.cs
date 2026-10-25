using System;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// Per-cell, per-quarter edge states for one room — the shape layer the quarter renderer draws.
    ///
    /// <para><b>Ported from Remains-Unity <c>RoomTerrainShapes.Compute</c>, which is itself a
    /// reconstruction of the Flash renderer's own rule.</b> The rule, in the original's terms: a
    /// quarter looks at its two orthogonal neighbours and the diagonal between them, and takes one of
    /// five states. Outside the room counts as filled, so the room's own border closes.</para>
    ///
    /// <para><b>Why this is computed here rather than read from <c>TileData.kontur1..4</c>.</b>
    /// <see cref="KonturCalculator"/> only fills <c>kontur1..4</c> for <c>physicsType == Wall</c>, and
    /// leaves every platform, shelf and stair at the default 0. This renderer draws a mask for every
    /// cell that carries a front material, so it needs an answer for those cells too — but the answer
    /// the original gives them is <b>0</b>, not a computed edge: <c>Location.tileKontur</c> fills
    /// <c>kont1..4</c> inside <c>if (phis == 1)</c> only, and <c>Tile.kont1</c> is declared
    /// <c>= 0</c>. A platform's front is therefore a plain square, and an earlier version of this file
    /// claimed it "needs state 4" — that was an invention, and it would have chewed the corners off
    /// every beam. The two paths agree on wall cells (which is what the A/B cross-check measures) and
    /// deliberately agree on non-walls by both saying 0.</para>
    ///
    /// <para><b>The two predicates are not the same predicate, and that is the oracle's shape.</b>
    /// The front layer asks "is the neighbour solid"; the back layer asks "is the neighbour the same
    /// background", which also accepts a solid, a shelf, and — for a room whose backdrop is
    /// <c>sky</c> — any other background at all. Collapsing those into one test flattens the back wall
    /// against the front wall's silhouette, which reads as "the back wall has no top edge".</para>
    /// </summary>
    public sealed class RoomQuarterStates
    {
        public readonly int Width;
        public readonly int Height;

        // [y * Width + x] * 4 + (int)quarter. sbyte: the values are -1..4.
        private readonly sbyte[] _front;
        private readonly sbyte[] _back;

        private RoomQuarterStates(int width, int height)
        {
            Width = width;
            Height = height;
            _front = new sbyte[width * height * 4];
            _back = new sbyte[width * height * 4];

            for (int i = 0; i < _front.Length; i++)
            {
                _front[i] = RoomQuarterRules.NotDrawn;
                _back[i] = RoomQuarterRules.NotDrawn;
            }
        }

        /// <summary>Front (solid) state for one quarter of a cell, or <see cref="RoomQuarterRules.NotDrawn"/>.</summary>
        public int Front(int x, int y, RoomQuarter quarter)
        {
            if (!InBounds(x, y)) return RoomQuarterRules.NotDrawn;
            return _front[Index(x, y, quarter)];
        }

        /// <summary>Back (background) state for one quarter of a cell, or <see cref="RoomQuarterRules.NotDrawn"/>.</summary>
        public int Back(int x, int y, RoomQuarter quarter)
        {
            if (!InBounds(x, y)) return RoomQuarterRules.NotDrawn;
            return _back[Index(x, y, quarter)];
        }

        /// <summary>True when the cell has any front quarter drawn.</summary>
        public bool HasFront(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            int i = (y * Width + x) * 4;
            return _front[i] != RoomQuarterRules.NotDrawn
                || _front[i + 1] != RoomQuarterRules.NotDrawn
                || _front[i + 2] != RoomQuarterRules.NotDrawn
                || _front[i + 3] != RoomQuarterRules.NotDrawn;
        }

        /// <summary>Counts the cells with any front quarter drawn. Diagnostics only.</summary>
        public int CountFrontCells()
        {
            int count = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    if (HasFront(x, y)) count++;
            return count;
        }

        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        private int Index(int x, int y, RoomQuarter quarter) => (y * Width + x) * 4 + (int)quarter;

        /// <summary>
        /// Computes the shape layer.
        ///
        /// <para><b>The delegates are only ever called for in-bounds coordinates.</b> Out-of-bounds is
        /// handled here, as "filled", because that is the oracle's rule and because a caller that had
        /// to remember it would eventually forget — one room edge drawn as a void instead of a wall.
        /// The same is true of the back predicate: out-of-bounds is filled, and a delegate that
        /// returns 0 for "no background" is never asked about a cell outside the room.</para>
        /// </summary>
        /// <param name="width">Room width in tiles.</param>
        /// <param name="height">Room height in tiles. Row 0 is the floor row.</param>
        /// <param name="isSolid">True when the cell blocks: a wall. In-bounds only.</param>
        /// <param name="backId">Background identity of the cell; <c>0</c> means "no background". Two
        /// cells belong to the same background surface when their ids are equal, so the id must be
        /// stable per background graphic, not a per-cell counter.</param>
        /// <param name="isShelf">True when the cell carries a shelf/catwalk. In-bounds only.</param>
        /// <param name="skyBackdrop">The room's backdrop is <c>sky</c>, which in the original means
        /// every background joins every other one into a single silhouette.</param>
        /// <param name="hasFront">True when the cell draws a front graphic at all — a wall, a platform,
        /// a slope overlay. Defaults to null, meaning "solid cells only", which is the behaviour this
        /// had before platforms were drawn at all. See the class remarks for why a non-solid cell with
        /// a front graphic is <see cref="RoomQuarterRules.Inside"/> rather than a computed edge.</param>
        public static RoomQuarterStates Compute(
            int width,
            int height,
            Func<int, int, bool> isSolid,
            Func<int, int, int> backId,
            Func<int, int, bool> isShelf,
            bool skyBackdrop,
            Func<int, int, bool> hasFront = null)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (isSolid == null) throw new ArgumentNullException(nameof(isSolid));
            if (backId == null) throw new ArgumentNullException(nameof(backId));

            var shapes = new RoomQuarterStates(width, height);
            if (width == 0 || height == 0) return shapes;

            bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height;
            bool Solid(int x, int y) => !Inside(x, y) || isSolid(x, y);
            bool HasBack(int x, int y) => Inside(x, y) && backId(x, y) != 0;
            bool Shelf(int x, int y) => Inside(x, y) && isShelf != null && isShelf(x, y);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool solid = isSolid(x, y);
                    int ownBack = backId(x, y);

                    if (solid)
                    {
                        // Fill takes *offset* predicates, not absolute ones: it asks about (0,+1),
                        // (-1,0) and (-1,+1) relative to the cell. Passing `Solid` directly compiles
                        // and reads correctly, and silently asks about the wrong cells — which is how
                        // this was written the first time, and what the three Compute_* tests caught.
                        Fill(shapes._front, width, x, y,
                            (dx, dy) => Solid(x + dx, y + dy),
                            (dx, dy) => Solid(x + dx, y + dy));

                        // Back behind a solid cell: the orthogonals also accept a back wall or a
                        // shelf, so a back panel does not get a seam where it meets a beam.
                        if (ownBack != 0)
                        {
                            Fill(shapes._back, width, x, y,
                                (dx, dy) => Solid(x + dx, y + dy)
                                            || HasBack(x + dx, y + dy)
                                            || Shelf(x + dx, y + dy),
                                (dx, dy) => Solid(x + dx, y + dy));
                        }
                    }
                    else if (ownBack != 0)
                    {
                        bool Same(int dx, int dy)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (!Inside(nx, ny)) return true;

                            int other = backId(nx, ny);
                            return other == ownBack
                                   || (skyBackdrop && other != 0)
                                   || Solid(nx, ny)
                                   || Shelf(nx, ny);
                        }

                        Fill(shapes._back, width, x, y, Same, Same);
                    }

                    // A non-solid cell that draws a front graphic — a platform, a beam, a slope — is
                    // drawn with all four quarters plain. `Location.tileKontur` fills `kont1..4` only
                    // inside `if (phis == 1)` and `Tile.kont1` defaults to 0, so a `phis == 0` tile's
                    // front is a full square in the original; `drawKusok` reads that 0 and selects frame
                    // 1. Computing real edges here instead would cut the corners off every beam, which
                    // is a nicer-sounding rule and the wrong one.
                    if (!solid && hasFront != null && hasFront(x, y))
                    {
                        Fill(shapes._front, width, x, y, (dx, dy) => true, (dx, dy) => true);
                    }
                }
            }

            return shapes;
        }

        /// <summary>
        /// Fills the four quarters of one cell from the neighbour offsets that belong to each quarter.
        /// Y-up: the top quarters look at <c>(0, +1)</c>, the bottom ones at <c>(0, -1)</c>.
        /// </summary>
        private static void Fill(
            sbyte[] target,
            int width,
            int x,
            int y,
            Func<int, int, bool> orthogonal,
            Func<int, int, bool> diagonal)
        {
            int i = (y * width + x) * 4;

            target[i + (int)RoomQuarter.TopLeft] = (sbyte)RoomQuarterRules.State(
                orthogonal(0, 1), orthogonal(-1, 0), diagonal(-1, 1));

            target[i + (int)RoomQuarter.TopRight] = (sbyte)RoomQuarterRules.State(
                orthogonal(0, 1), orthogonal(1, 0), diagonal(1, 1));

            target[i + (int)RoomQuarter.BottomLeft] = (sbyte)RoomQuarterRules.State(
                orthogonal(0, -1), orthogonal(-1, 0), diagonal(-1, -1));

            target[i + (int)RoomQuarter.BottomRight] = (sbyte)RoomQuarterRules.State(
                orthogonal(0, -1), orthogonal(1, 0), diagonal(1, -1));
        }
    }
}

using System;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// Which quarter of a tile a mask shape is drawn into.
    ///
    /// <para><b>Why quarters at all.</b> The Flash original draws a tile as four independent 20×20
    /// shapes, each choosing one of five edge variants from its own two orthogonal neighbours and the
    /// diagonal between them. The port's shipped path (<see cref="TileCompositor"/>) collapses that into
    /// one 40×40 bitmap per tile and then has to sample a quadrant of a *combination* mask — which is
    /// where the inverted turns come from. The parallel renderer keeps the four quarters as four
    /// placements on a half-cell Tilemap, so each quarter selects its own frame and nothing has to be
    /// mirrored.</para>
    ///
    /// <para><b>Y runs up here, like everywhere else in the port.</b> <see cref="TopLeft"/> is the
    /// quarter that looks at the tile <i>above</i>; row 0 is the floor row. See
    /// <c>TOPIC_workflow_constraints.md</c> #18 — AS3 runs down and the port runs up, and the two are
    /// related by a mirror, never by a floor.</para>
    /// </summary>
    public enum RoomQuarter
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3
    }

    /// <summary>
    /// The five edge states a quarter can take, and the two predicates that select one.
    ///
    /// <para><b>This is <see cref="KonturCalculator"/>'s <c>insKontur</c> under a different name, and
    /// that is deliberate.</b> Both are ports of AS3 <c>Location.insKontur(adj1, adj2, diag)</c>, and
    /// the numbers are the same five. The new renderer does not call <c>insKontur</c> because it needs
    /// the states for <i>every</i> cell that carries art, whereas <c>KonturCalculator</c> only fills
    /// <c>kontur1..4</c> for <c>physicsType == Wall</c>. Having both is what makes the A/B window able
    /// to cross-check them: two independent implementations agreeing on 1 200 cells is evidence, one
    /// implementation agreeing with itself is not.</para>
    /// </summary>
    public static class RoomQuarterRules
    {
        /// <summary>The cell is not drawn at all — the state a quarter holds before it is filled.</summary>
        public const int NotDrawn = -1;

        /// <summary>Both orthogonal neighbours and the diagonal are filled: no edge, a plain square.</summary>
        public const int Inside = 0;

        /// <summary>Both orthogonal neighbours are filled but the diagonal is not: a concave notch.</summary>
        public const int InnerCorner = 1;

        /// <summary>The neighbour on the Y axis is missing: a horizontal edge (a floor or a ceiling).</summary>
        public const int HorizontalEdge = 2;

        /// <summary>The neighbour on the X axis is missing: a vertical edge (a side wall).</summary>
        public const int VerticalEdge = 3;

        /// <summary>Both orthogonal neighbours are missing: a convex outer corner.</summary>
        public const int OuterCorner = 4;

        /// <summary>How many states exist. Mask frame sets carry one image per state.</summary>
        public const int StateCount = 5;

        /// <summary>
        /// AS3 <c>Location.insKontur</c> / Remains <c>RoomTerrainShapes.State</c>.
        ///
        /// <para>The parameter order matters and the names are the trap: <paramref name="yNeighbourFilled"/>
        /// is the neighbour along Y (the quarter's own vertical direction — up for a top quarter, down
        /// for a bottom one), <b>not</b> "the vertical edge". Swapping the two arguments produces a
        /// plausible renderer with every edge turned 90°, which is exactly the class of defect this
        /// whole workstream exists to stop guessing at.</para>
        /// </summary>
        public static int State(bool yNeighbourFilled, bool xNeighbourFilled, bool diagonalFilled)
        {
            if (yNeighbourFilled && xNeighbourFilled)
                return diagonalFilled ? Inside : InnerCorner;
            if (xNeighbourFilled)
                return HorizontalEdge;
            if (yNeighbourFilled)
                return VerticalEdge;
            return OuterCorner;
        }

        /// <summary>True for the two quarters in the upper half of the tile.</summary>
        public static bool IsTop(RoomQuarter quarter)
        {
            return quarter == RoomQuarter.TopLeft || quarter == RoomQuarter.TopRight;
        }

        /// <summary>True for the two quarters in the right half of the tile.</summary>
        public static bool IsRight(RoomQuarter quarter)
        {
            return quarter == RoomQuarter.TopRight || quarter == RoomQuarter.BottomRight;
        }

        /// <summary>The four quarters, in the enum's order. Allocating a new array each call is the
        /// only safe form: a shared static array is mutable state in a struct-shaped API.</summary>
        public static RoomQuarter[] All()
        {
            return new[]
            {
                RoomQuarter.TopLeft,
                RoomQuarter.TopRight,
                RoomQuarter.BottomLeft,
                RoomQuarter.BottomRight
            };
        }

        /// <summary>Manifest spelling (<c>topLeft</c>, <c>bottomRight</c>, …).</summary>
        public static string Name(RoomQuarter quarter)
        {
            switch (quarter)
            {
                case RoomQuarter.TopLeft: return "topLeft";
                case RoomQuarter.TopRight: return "topRight";
                case RoomQuarter.BottomLeft: return "bottomLeft";
                case RoomQuarter.BottomRight: return "bottomRight";
                default: throw new ArgumentOutOfRangeException(nameof(quarter));
            }
        }

        /// <summary>Parses the manifest spelling, case-insensitively. False for anything else.</summary>
        public static bool TryParse(string name, out RoomQuarter quarter)
        {
            quarter = RoomQuarter.TopLeft;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            switch (name.Trim().ToLowerInvariant())
            {
                case "topleft": quarter = RoomQuarter.TopLeft; return true;
                case "topright": quarter = RoomQuarter.TopRight; return true;
                case "bottomleft": quarter = RoomQuarter.BottomLeft; return true;
                case "bottomright": quarter = RoomQuarter.BottomRight; return true;
                default: return false;
            }
        }
    }
}

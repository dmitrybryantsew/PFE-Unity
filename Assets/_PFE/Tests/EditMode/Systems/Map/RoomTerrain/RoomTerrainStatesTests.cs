using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins what a <b>destroyed</b> tile contributes to the quarter renderer's shape layer — which is
    /// nothing.
    ///
    /// <para><b>The failure this exists for.</b> <see cref="TileData.Destroy"/> clears <c>phis</c>,
    /// <c>opac</c> and the visual ids, but it leaves <c>frontGraphic</c> and <c>backGraphic</c> in place.
    /// The quarter renderer resolves its masks from those two strings, not from the ids, so before this
    /// rule the cell kept drawing its wall — invisible only because the layer's colour alpha happens to
    /// be the tile's <c>opacity</c>, which <c>Destroy</c> zeroes. The picture was therefore right by
    /// accident: any writer that restores <c>opacity</c> (a door carve sets it to 1) puts the wall back,
    /// and the shape layer's own counters report cells that draw nothing.</para>
    ///
    /// <para><b>Why this is offline-testable and the picture is not.</b> <c>BuildStates</c> is a pure
    /// function of the room's tile grid — no shader, no Tilemap, no GameObject. That is exactly the
    /// property that makes "the room's picture is a function of the tile grid" a claim instead of a
    /// hope, so it is the part worth pinning without a screen.</para>
    ///
    /// <para><b>Every expected number below was computed by hand from
    /// <see cref="RoomQuarterRules.State"/> before the run</b>, not read off the output. The rule is
    /// <c>State(yNeighbourFilled, xNeighbourFilled, diagonalFilled)</c>: both filled → Inside (0) or
    /// InnerCorner (1); only the X neighbour filled → HorizontalEdge (2); only the Y neighbour filled →
    /// VerticalEdge (3); neither → OuterCorner (4). Out-of-bounds counts as filled, which is why the
    /// top row's top quarters are never OuterCorner.</para>
    /// </summary>
    public sealed class RoomTerrainStatesTests
    {
        // ─── The fixture room ───────────────────────────────────

        /// <summary>A 3×3 room of plain air. Row 0 is the floor row, so (1,2) is the top-middle cell.</summary>
        private static RoomInstance AirRoom()
        {
            var room = new RoomInstance { width = 3, height = 3, tiles = new TileData[3, 3] };

            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    room.tiles[x, y] = new TileData
                    {
                        gridPosition = new Vector2Int(x, y),
                        physicsType = TilePhysicsType.Air,
                        opacity = 1f
                    };
                }
            }

            return room;
        }

        private static TileData Wall(RoomInstance room, int x, int y, string front = "", string back = "")
        {
            TileData tile = room.tiles[x, y];
            tile.physicsType = TilePhysicsType.Wall;
            tile.SetFrontGraphic(front);
            tile.SetBackGraphic(back);
            return tile;
        }

        // ─── A destroyed wall draws no front ────────────────────

        [Test]
        public void BuildStates_DestroyedWallStopsDrawingItsFront()
        {
            RoomInstance room = AirRoom();
            TileData wall = Wall(room, 1, 1, front: "w");

            RoomQuarterStates before = RoomTerrainRenderer.BuildStates(room);

            Assert.IsTrue(before.HasFront(1, 1), "a wall with a front graphic must draw a front");

            // All four neighbours and all four diagonals are air, so every quarter of an isolated wall
            // is a convex corner: State(false, false, false) = OuterCorner = 4.
            Assert.AreEqual(RoomQuarterRules.OuterCorner, before.Front(1, 1, RoomQuarter.TopLeft),
                "an isolated wall's top-left quarter is a convex corner");
            Assert.AreEqual(1, before.CountFrontCells(), "one wall, one front cell");

            wall.Destroy();

            RoomQuarterStates after = RoomTerrainRenderer.BuildStates(room);

            Assert.IsFalse(after.HasFront(1, 1),
                "a destroyed tile must draw no front at all — its front graphic string survives Destroy()");

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.NotDrawn, after.Front(1, 1, quarter),
                    $"quarter {quarter} of a destroyed tile must be NotDrawn");
            }

            Assert.AreEqual(0, after.CountFrontCells(),
                "the shape layer must count no front cell for a room whose only wall is destroyed");
        }

        // ─── A destroyed wall draws no back ─────────────────────

        [Test]
        public void BuildStates_DestroyedWallStopsDrawingItsBack()
        {
            RoomInstance room = AirRoom();
            TileData wall = Wall(room, 1, 1, back: "b");

            RoomQuarterStates before = RoomTerrainRenderer.BuildStates(room);

            // Solid cell with a back: the back's orthogonal predicate also accepts a back wall or a
            // shelf, and every neighbour here is bare air — so all four quarters are convex corners.
            Assert.AreEqual(RoomQuarterRules.OuterCorner, before.Back(1, 1, RoomQuarter.TopLeft),
                "an isolated wall's back is a convex corner");

            wall.Destroy();

            RoomQuarterStates after = RoomTerrainRenderer.BuildStates(room);

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.NotDrawn, after.Back(1, 1, quarter),
                    $"quarter {quarter} of a destroyed tile's back must be NotDrawn — " +
                    "Destroy() clears the visual ids but not the back graphic string");
            }
        }

        // ─── The neighbour above the wall loses its edge ────────

        [Test]
        public void BuildStates_DestructionOpensTheEdgeOfTheTileAbove()
        {
            RoomInstance room = AirRoom();
            TileData lower = Wall(room, 1, 1, front: "w");
            Wall(room, 1, 2, front: "w");

            RoomQuarterStates before = RoomTerrainRenderer.BuildStates(room);

            // (1,2) bottom-left: y-neighbour = (1,1) = solid; x-neighbour = (0,2) = air;
            // diagonal = (0,1) = air. State(true, false, false) = VerticalEdge = 3. The bottom-right
            // quarter mirrors it against (2,2)/(2,1) and is also a vertical edge.
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, before.Front(1, 2, RoomQuarter.BottomLeft),
                "with a wall beneath it, the upper wall's bottom-left quarter is a side edge");
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, before.Front(1, 2, RoomQuarter.BottomRight),
                "and so is its bottom-right");
            Assert.AreEqual(2, before.CountFrontCells(), "two walls, two front cells");

            lower.Destroy();

            RoomQuarterStates after = RoomTerrainRenderer.BuildStates(room);

            // The cell below is gone, so the bottom quarters look at air on both axes and at an air
            // diagonal: State(false, false, false) = OuterCorner = 4.
            Assert.AreEqual(RoomQuarterRules.OuterCorner, after.Front(1, 2, RoomQuarter.BottomLeft),
                "the wall above must now close its own bottom edge — the neighbour is no longer solid");
            Assert.AreEqual(RoomQuarterRules.OuterCorner, after.Front(1, 2, RoomQuarter.BottomRight),
                "and the same on the right");
            Assert.AreEqual(1, after.CountFrontCells(), "only the surviving wall draws a front");
        }

        // ─── One definition of "this cell draws" ────────────────

        [Test]
        public void IsDrawn_IsFalseForDestroyedTiles()
        {
            var tile = new TileData { physicsType = TilePhysicsType.Wall, opacity = 1f };
            tile.SetFrontGraphic("w");

            Assert.IsTrue(RoomTerrainRenderer.IsDrawn(tile), "a live wall is drawn");

            tile.Destroy();

            Assert.IsFalse(RoomTerrainRenderer.IsDrawn(tile),
                "Destroy() zeroes opacity and the visual ids but not the graphic strings, so the " +
                "predicate must be IsDestroyed(), not \"has art\"");

            Assert.IsFalse(RoomTerrainRenderer.IsDrawn(null), "a missing tile is not drawn");
        }
    }
}

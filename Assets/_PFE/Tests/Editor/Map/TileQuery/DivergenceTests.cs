using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// Stage B3: Tests comparing GridTileQuery (legacy) vs UnifiedTileQueryService (unified).
    /// Tracks and validates known divergences (e.g. platformThreshold 8 vs 10)
    /// and verifies absolute agreement on common geometry.
    /// </summary>
    [TestFixture]
    public class DivergenceTests
    {
        private RoomInstance _room;

        [SetUp]
        public void SetUp()
        {
            string[] ascii = {
                "################",
                "#..............#",
                "#....====......#",
                "#....H.........#",
                "#....H..../....#",
                "#....H.../.....#",
                "################"
            };
            _room = SyntheticRoomBuilder.BuildFromAscii(ascii);
        }

        [Test]
        public void Solidity_WallsAndAir_AgreeExactly()
        {
            var grid = new GridTileQuery(_room);
            var unified = new UnifiedTileQueryService(_room);

            for (int x = 0; x < _room.width; x++)
            {
                for (int y = 0; y < _room.height; y++)
                {
                    Vector2Int coord = new Vector2Int(x, y);
                    bool g = grid.IsSolidAt(coord);
                    bool u = unified.IsSolidAt(coord);
                    Assert.AreEqual(g, u, $"Solidity disagreed at {coord}");
                }
            }
        }

        [Test]
        public void Classification_ClassifiesAllTypes()
        {
            var unified = new UnifiedTileQueryService(_room);

            // Wall at (0, 0)
            Assert.AreEqual(TileQueryFlags.Solid, unified.Classify(new Vector2Int(0, 0)) & TileQueryFlags.Solid);

            // Platform at (5, 4)
            Assert.AreEqual(TileQueryFlags.Platform, unified.Classify(new Vector2Int(5, 4)) & TileQueryFlags.Platform);

            // Ladder at (5, 3)
            Assert.AreEqual(TileQueryFlags.Ladder, unified.Classify(new Vector2Int(5, 3)) & TileQueryFlags.Ladder);

            // Slope at (10, 2)
            Assert.AreEqual(TileQueryFlags.Slope, unified.Classify(new Vector2Int(10, 2)) & TileQueryFlags.Slope);
        }

        [Test]
        public void PlatformThreshold_DivergenceDocumented()
        {
            var grid = new GridTileQuery(_room);
            var unified = new UnifiedTileQueryService(_room);

            // Platform at y=4 -> top is at (4 + 1) * 40 = 200px.
            // A probe at y = 200 - 9 = 191px is:
            // - within unified porog 10 (200 - 10 = 190) -> collides!
            // - outside grid porog 8 (200 - 8 = 192) -> does NOT collide!
            //
            // The probe must stay INSIDE the platform's own tile row (y = 160..200). A taller
            // probe (this used to be 50px tall, spanning 191..241) reaches the ceiling wall row
            // above (y = 240..280), so BOTH backends collide on that wall and the porog
            // difference is masked entirely.
            Rect probeAt9px = new Rect(200f, 191f, 30f, 5f);

            bool gridCollides = grid.CheckCollision(probeAt9px, TileQueryOptions.Default);
            bool unifiedCollides = unified.CheckCollision(probeAt9px, TileQueryOptions.Default);

            // Expected divergence: unified has porog=10 (AS3 parity), grid has porog=8 (port invention)
            Assert.IsFalse(gridCollides, "Grid with porog=8 should not collide at 9px below top");
            Assert.IsTrue(unifiedCollides, "Unified with porog=10 (AS3) MUST collide at 9px below top");
        }

        [Test]
        public void ResolveMove_HorizontalWall_BlocksBothIdentically()
        {
            var grid = new GridTileQuery(_room);
            var unified = new UnifiedTileQueryService(_room);

            // Moving into left border wall from x=80 toward x=0
            TileBox box = TileBox.FromFeet(new Vector2(80f, 40f), halfWidth: 15f, height: 50f);
            Vector2 delta = new Vector2(-100f, 0f);

            TileMoveResult resGrid = grid.ResolveMove(box, delta, TileQueryFlags.Solid);
            TileMoveResult resUnified = unified.ResolveMove(box, delta, TileQueryFlags.Solid);

            Assert.AreEqual(resGrid.Position.x, resUnified.Position.x, 0.1f);
            Assert.IsTrue(resGrid.HitLeft);
            Assert.IsTrue(resUnified.HitLeft);
        }

        [Test]
        public void DivergenceLogger_TracksDivergenceCount()
        {
            var grid = new GridTileQuery(_room);
            var unified = new UnifiedTileQueryService(_room);
            var logger = new TileQueryDivergenceLogger(grid, unified);

            // Probe at 9px where they diverge (kept inside the platform's tile row, see
            // PlatformThreshold_DivergenceDocumented for why the height matters).
            Rect probeAt9px = new Rect(200f, 191f, 30f, 5f);
            logger.CheckCollision(probeAt9px, TileQueryOptions.Default);

            Assert.AreEqual(1, logger.DivergenceCount, "Divergence logger should record 1 divergence");
        }
    }
}

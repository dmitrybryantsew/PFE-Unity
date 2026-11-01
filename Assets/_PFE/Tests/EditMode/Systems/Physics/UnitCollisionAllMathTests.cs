using NUnit.Framework;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Fixtures for <see cref="UnitCollisionAllMath"/> — <c>Unit.collisionAll</c>
    /// (<c>Unit.as:2541-2566</c>) and <c>collisionTile</c> (<c>:2568-2583</c>).
    ///
    /// <para>Every assertion pins an oracle literal, and the ones that matter most are the
    /// <b>negative</b> ones: edge contact is not overlap, a raised tile's empty top is not solid, an
    /// open-sky room is not solid anywhere, and a flying body passes a shelf. A rule that returns
    /// "true whenever it is near a wall" passes every positive test in this file and fails five of
    /// the negatives.</para>
    /// </summary>
    [TestFixture]
    public class UnitCollisionAllMathTests
    {
        private const float Tile = 40f;

        // ── A hand-built grid: [column, AS3 row] ───────────────────────────────────────────────

        private sealed class FakeGrid : ICollisionTileGrid
        {
            private readonly CollisionTile[,] _tiles;
            private readonly bool[,] _present;

            public FakeGrid(int columns, int rows)
            {
                _tiles = new CollisionTile[columns, rows];
                _present = new bool[columns, rows];
            }

            public bool OpenSky { get; set; }

            public int Columns => _tiles.GetLength(0);

            public int Rows => _tiles.GetLength(1);

            public float TileSize => Tile;

            public void Set(int column, int row, CollisionTile tile)
            {
                _tiles[column, row] = tile;
                _present[column, row] = true;
            }

            public bool TryGetTile(int column, int as3Row, out CollisionTile tile)
            {
                tile = default;
                if (column < 0 || column >= Columns || as3Row < 0 || as3Row >= Rows) return false;
                if (!_present[column, as3Row]) return false;

                tile = _tiles[column, as3Row];
                return true;
            }
        }

        private static CollisionTile FullTile(int column, int as3Row, bool solid = true,
            bool ghostWall = false, bool shelf = false)
        {
            float x = column * Tile;
            float y = as3Row * Tile;
            return new CollisionTile(x, x + Tile, y, y + Tile, solid, ghostWall, shelf);
        }

        /// <summary>A pet-sized body standing on its feet at (x, y), y down.</summary>
        private static CollisionBody Pet(float feetX, float feetY, float height = 100f)
        {
            return CollisionBody.FromFeet(feetX, feetY, halfWidth: 25f, height: height,
                transparentToGhostWalls: true,
                ignoresShelves: true,
                stays: false);
        }

        // ── The box is feet-anchored (Unit.as:2504-2507) ──────────────────────────────────────

        [Test]
        public void FromFeet_PutsTheWholeBoxAboveTheFeet()
        {
            CollisionBody body = CollisionBody.FromFeet(feetX: 100f, feetY: 200f,
                halfWidth: 20f, height: 60f);

            Assert.That(body.X1, Is.EqualTo(80f), "X1 = X - scX/2");
            Assert.That(body.X2, Is.EqualTo(120f), "X2 = X + scX/2");
            Assert.That(body.Y2, Is.EqualTo(200f), "Y2 = Y — the feet");
            Assert.That(body.Y1, Is.EqualTo(140f), "Y1 = Y - scY — the head");
        }

        [Test]
        public void Any_ABodyWhoseHeadIsInATile_Collides_EvenThoughItsFeetAreBelow()
        {
            // This is the case a feet-CENTRED box misses entirely: the pet's feet are at 100 and its
            // head at 0, so it overlaps the tile at row 0 (y 0..40) while standing 60 px below it.
            var grid = new FakeGrid(1, 4);
            grid.Set(0, 0, FullTile(0, 0));

            Assert.That(UnitCollisionAllMath.Any(Pet(20f, 100f, height: 100f), grid), Is.True,
                "a box centred on the feet would span 50..150 and miss this tile");
        }

        // ── collisionTile arm 1: air and ghost walls (Unit.as:2570) ───────────────────────────

        [Test]
        public void CollidesWithTile_AirIsNeverSolid()
        {
            var air = new CollisionTile(0f, 40f, 0f, 40f, solid: false);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(20f, 20f), air), Is.False,
                "phis == 0 && !shelf -> 0");
        }

        [Test]
        public void CollidesWithTile_GhostWallIsSolidUnlessTheBodyIsTransT()
        {
            var ghost = new CollisionTile(0f, 40f, 0f, 40f, solid: true, ghostWall: true);

            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(20f, 20f), ghost), Is.False,
                "transT && phis == 3 -> 0 (the pet's constructor sets transT, UnitPet.as:107)");

            CollisionBody solidBody = CollisionBody.FromFeet(20f, 20f, 25f, 100f,
                transparentToGhostWalls: false);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(solidBody, ghost), Is.True,
                "a non-transT body treats phis == 3 as a wall");
        }

        // ── collisionTile arm 2: the AABB, strict edges (Unit.as:2574) ────────────────────────

        [Test]
        public void CollidesWithTile_TouchingAnEdgeIsNotOverlap()
        {
            var tile = FullTile(1, 1);   // x 40..80, y 40..80

            // Feet exactly on the tile's top edge, body rising away from it.
            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(60f, 40f, height: 30f), tile), Is.False,
                "Y2 <= phY1 -> 0; this is why a unit can stand ON a surface");

            // Right edge exactly on the tile's left edge.
            CollisionBody leftOf = CollisionBody.FromFeet(15f, 60f, 25f, 30f);
            Assert.That(leftOf.X2, Is.EqualTo(40f), "the body's right edge is on the seam");
            Assert.That(UnitCollisionAllMath.CollidesWithTile(leftOf, tile), Is.False,
                "X2 <= phX1 -> 0");

            // One pixel further in and it collides.
            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(60f, 41f, height: 30f), tile), Is.True);
        }

        [Test]
        public void CollidesWithTile_OverlappingTheCornerCollides()
        {
            var tile = FullTile(1, 1);   // x 40..80, y 40..80
            CollisionBody corner = CollisionBody.FromFeet(45f, 45f, 5f, 5f);   // x 40..50, y 40..45

            Assert.That(UnitCollisionAllMath.CollidesWithTile(corner, tile), Is.True);
        }

        // ── collisionTile arm 3: the shelf (Unit.as:2578) ─────────────────────────────────────

        [Test]
        public void CollidesWithTile_AShelfNeverBlocksAFlyingBody()
        {
            var shelf = new CollisionTile(0f, 40f, 0f, 40f, solid: false, shelf: true);

            // `isFly` alone satisfies the last disjunct of :2578.
            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(20f, 10f), shelf), Is.False);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(20f, 39f), shelf), Is.False);

            // The case the step allowance alone would NOT clear: feet 1 px inside the step window, so
            // `Y2 - allowance > phY1` is false and the shelf would catch a walker. Only the `isFly`
            // term can answer 0 here — this is the assertion that pins IgnoresShelves.
            CollisionBody hovering = CollisionBody.FromFeet(20f, 1f, 10f, 30f,
                ignoresShelves: true, jumpStepAllowance: 10f);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(hovering, shelf), Is.False,
                "isFly passes a shelf inside the step window too");
        }

        [Test]
        public void CollidesWithTile_AShelfBlocksOnlyWithinTheStepWindow()
        {
            // A shelf's cell: phY1 = 0 (its top), phY2 = 40. In AS3's y-down space a body's feet are
            // its Y2, and `Y2 - allowance > phY1` means "the feet are more than a step below the
            // surface" — the pass-through arm. Everything else about a shelf is arm 2.
            var shelf = new CollisionTile(0f, 40f, 0f, 40f, solid: false, shelf: true);

            // Standing exactly on the top edge: arm 2 rejects (Y2 <= phY1), so no collision.
            CollisionBody onTop = CollisionBody.FromFeet(20f, 0f, 10f, 30f);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(onTop, shelf), Is.False,
                "Y2 <= phY1 -> 0");

            // Feet 5 px below the top with no allowance: 5 - 0 > 0, so it passes up through.
            CollisionBody passing = CollisionBody.FromFeet(20f, 5f, 10f, 30f);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(passing, shelf), Is.False,
                "Y2 - porog_jump > phY1 -> 0; this is how a jump comes up through a beam");

            // The same feet with a 10 px step allowance: -5 > 0 is false, so the shelf is solid —
            // the step window. This is the only state in which a shelf answers 1 to a walker.
            CollisionBody stepping = CollisionBody.FromFeet(20f, 5f, 10f, 30f, jumpStepAllowance: 10f);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(stepping, shelf), Is.True,
                "inside the allowance the shelf catches the body");

            // Deep underneath, the allowance no longer reaches the surface: 35 - 10 > 0.
            CollisionBody deepUnder = CollisionBody.FromFeet(20f, 35f, 10f, 30f, jumpStepAllowance: 10f);
            Assert.That(UnitCollisionAllMath.CollidesWithTile(deepUnder, shelf), Is.False);
        }

        [Test]
        public void CollidesWithTile_AWallThatIsAlsoAShelfStillBlocks()
        {
            // `phis == 1` fails the `phis == 0 || transT && phis == 3` guard, so the shelf arm never
            // runs: a beam overlay on a wall does not demote the wall.
            var wallShelf = new CollisionTile(0f, 40f, 0f, 40f, solid: true, shelf: true);

            Assert.That(UnitCollisionAllMath.CollidesWithTile(Pet(20f, 20f), wallShelf), Is.True);
        }

        // ── collisionAll itself (Unit.as:2541-2566) ──────────────────────────────────────────

        [Test]
        public void Any_AnOpenSkyRoomIsNeverSolid()
        {
            var grid = new FakeGrid(4, 4) { OpenSky = true };
            for (int x = 0; x < 4; x++)
            {
                for (int y = 0; y < 4; y++) grid.Set(x, y, FullTile(x, y));
            }

            Assert.That(UnitCollisionAllMath.Any(Pet(80f, 80f), grid), Is.False,
                "`if(loc.sky) return false;` (:2544)");
        }

        [Test]
        public void Any_OutOfBoundsCellsAreSkipped_NotSolid()
        {
            var grid = new FakeGrid(2, 2);
            grid.Set(0, 0, FullTile(0, 0));

            // A body entirely left of the grid must not collide with the "room edge".
            Assert.That(UnitCollisionAllMath.Any(Pet(-100f, 20f), grid), Is.False);
        }

        [Test]
        public void Any_CellsWithNoTileDataAreSkipped()
        {
            var grid = new FakeGrid(2, 2);   // nothing set: TryGetTile answers false everywhere

            Assert.That(UnitCollisionAllMath.Any(Pet(20f, 20f), grid), Is.False);
        }

        [Test]
        public void Any_FindsTheSolidTileAmongEmptyOnes()
        {
            var grid = new FakeGrid(3, 3);
            grid.Set(0, 0, FullTile(0, 0, solid: false));
            grid.Set(1, 1, FullTile(1, 1));           // the one wall, at x 40..80, y 40..80
            grid.Set(2, 2, FullTile(2, 2, solid: false));

            Assert.That(UnitCollisionAllMath.Any(Pet(60f, 70f, height: 20f), grid), Is.True);
            Assert.That(UnitCollisionAllMath.Any(Pet(20f, 20f, height: 20f), grid), Is.False);
        }

        [Test]
        public void Any_ARaisedTileIsSolidOnlyInItsLowerPart()
        {
            // heightLevel = 2: `phY1 = (Y + zForm/4) * tileY` drops the top by half a cell, and
            // `phY2` stays on the cell's bottom seam (loc/Tile.as:281-297). In AS3 (y down) that is
            // y 20..40 of the cell at row 0.
            var grid = new FakeGrid(1, 2);
            grid.Set(0, 0, new CollisionTile(0f, 40f, 20f, 40f, solid: true));

            // Head at y 5, feet at 15 — inside the cell, above the solid part.
            Assert.That(UnitCollisionAllMath.Any(Pet(20f, 15f, height: 10f), grid), Is.False,
                "the empty top of a quarter tile is not solid");

            // Head at 25, feet at 35 — inside the solid part.
            Assert.That(UnitCollisionAllMath.Any(Pet(20f, 35f, height: 10f), grid), Is.True);
        }

        // ── The port→AS3 mirror (TileData.GetBounds is y-up) ─────────────────────────────────

        [Test]
        public void FromPortBounds_SwapsTheEdgesWhenMirroringIntoAs3Space()
        {
            // A full cell at port row 0 in a 25-row room: yMin 0, yMax 40 → AS3 y 960..1000.
            CollisionTile tile = CollisionTile.FromPortBounds(
                x1: 0f, x2: 40f, yMinUp: 0f, yMaxUp: 40f, roomHeightPixels: 1000f, solid: true);

            Assert.That(tile.Y1, Is.EqualTo(960f), "AS3 phY1 is the y-up rect's TOP (yMax)");
            Assert.That(tile.Y2, Is.EqualTo(1000f), "AS3 phY2 is the y-up rect's BOTTOM (yMin)");
        }

        [Test]
        public void FromPortBounds_ARaisedTileKeepsItsFootOnTheCellFloor()
        {
            // heightLevel = 2 at port row 3 → GetBounds gives y 120..140 (cell is 120..160).
            CollisionTile tile = CollisionTile.FromPortBounds(
                x1: 0f, x2: 40f, yMinUp: 120f, yMaxUp: 140f, roomHeightPixels: 1000f, solid: true);

            // The cell spans AS3 840..880; the solid part is its LOWER half, 860..880.
            Assert.That(tile.Y1, Is.EqualTo(860f), "top lowered by zForm/4 of a tile");
            Assert.That(tile.Y2, Is.EqualTo(880f), "the foot stays on the cell's bottom seam");
        }
    }
}

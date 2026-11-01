using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.EditMode.Systems.Map.TileCollision
{
    /// <summary>
    /// Pins the two rules <see cref="TileCollisionBuilder"/> owns: <b>which tiles get a collider</b>, and
    /// <b>where their objects sit</b>.
    ///
    /// <para><b>Why these two and not "it builds objects".</b> The builder exists so a room can be
    /// collided with and not drawn. Everything it gets wrong is therefore invisible in the picture and
    /// visible only as a gameplay symptom: a tile half a cell off reads as "the player stands on air",
    /// and a missing collider reads as "I walked through a wall". Both are numbers, so both are pinned
    /// here as numbers rather than as a screenshot.</para>
    ///
    /// <para><b>The position is copied from <c>TileRenderer.GetLocalPosition</c>, deliberately.</b> That
    /// method cannot be called here (it is private, and it lives on a <c>MonoBehaviour</c>), so the
    /// expected values below are written out from <see cref="TileData.GetBounds"/> by hand —
    /// <c>((x + 0.5) · 40, (y + 0.5 + heightLevel/4) · 40)</c> source pixels, divided by 100. Writing the
    /// number down first is what makes the fixture a check rather than a restatement of the code.</para>
    /// </summary>
    public sealed class TileCollisionBuilderTests
    {
        /// <summary><see cref="WorldConstants.TILE_SIZE"/>, restated so the arithmetic below is readable.</summary>
        private const float CellPixels = 40f;

        private const float PixelsPerUnit = 100f;

        private static TileData Tile(int x, int y, TilePhysicsType physicsType, int heightLevel = 0)
        {
            return new TileData
            {
                gridPosition = new Vector2Int(x, y),
                physicsType = physicsType,
                heightLevel = heightLevel
            };
        }

        // ─── Which tiles get a collider ─────────────────────────────────────────

        [Test]
        public void NeedsCollision_IsTrueForEverySolidTypeAndFalseForAir()
        {
            Assert.IsTrue(TileCollisionBuilder.NeedsCollision(Tile(0, 0, TilePhysicsType.Wall)), "wall");
            Assert.IsTrue(TileCollisionBuilder.NeedsCollision(Tile(0, 0, TilePhysicsType.Platform)), "platform");
            Assert.IsTrue(TileCollisionBuilder.NeedsCollision(Tile(0, 0, TilePhysicsType.Stair)), "stair");
            Assert.IsFalse(TileCollisionBuilder.NeedsCollision(Tile(0, 0, TilePhysicsType.Air)), "air");
            Assert.IsFalse(TileCollisionBuilder.NeedsCollision(null), "null tile");
        }

        [Test]
        public void NeedsCollision_DoesNotDependOnTheArt()
        {
            // The one place the collision-only path deliberately differs from the shipped one. The
            // shipped path only ever built a tile object for a tile it could draw, so a non-air tile
            // with no graphic got no collider — an invisible hole in a wall. AS3's rule is `phis`, not
            // art (`Tile.udar` never consults the graphic), so the collider follows the data here.
            TileData blank = Tile(4, 4, TilePhysicsType.Wall);

            // Prove the setup: this tile really has nothing to draw.
            Assert.IsTrue(string.IsNullOrEmpty(blank.GetFrontGraphic()), "the tile must have no front graphic");
            Assert.IsTrue(string.IsNullOrEmpty(blank.GetBackGraphic()), "the tile must have no back graphic");
            Assert.AreEqual(0, blank.visualId, "the tile must have no overlay");

            Assert.IsTrue(TileCollisionBuilder.NeedsCollision(blank),
                "a non-air tile is solid whether or not the art exists");
        }

        // ─── Where the object sits ──────────────────────────────────────────────

        [Test]
        public void CellPixels_IsTheShippedTileSize()
        {
            // The fixture's own constant has to be the shipped one, or every expected value below is
            // arithmetic against a number nobody uses. Same cross-check as
            // RoomTerrainWiringTests.CellSize_MatchesTheShippedTileSize, one level down.
            Assert.AreEqual((float)WorldConstants.TILE_SIZE, CellPixels, 1e-6f,
                "the expected values in this fixture are written against WorldConstants.TILE_SIZE");
        }

        [Test]
        public void LocalPositionFor_IsTheCentreOfTheCell()
        {
            // Tile (3,5) at heightLevel 0:
            //   bounds.x = 3·40 .. 4·40        → centre x = 140 px
            //   bounds.y = 5·40 .. 6·40        → centre y = 220 px
            //   z        = gridPosition.y·0.01 = 0.05
            // divided by 100 px/unit → (1.4, 2.2, 0.05).
            Vector3 position = TileCollisionBuilder.LocalPositionFor(Tile(3, 5, TilePhysicsType.Wall));

            Assert.AreEqual(1.4f, position.x, 1e-5f, "x is the cell centre, not its corner");
            Assert.AreEqual(2.2f, position.y, 1e-5f, "y is the cell centre, not its corner");
            Assert.AreEqual(0.05f, position.z, 1e-5f, "z is the shipped path's depth order");
        }

        [Test]
        public void LocalPositionFor_MovesTheObjectDownAsTheTileGetsShorter()
        {
            // GetBounds() shortens a raised tile from the TOP (AS3 `setZForm` writes only `phY1`), so
            // its centre is (40 - 10·h)/2 above the cell floor instead of 20 — i.e. it moves DOWN by
            // 5·h px. Two levels is 10 px = 0.1 units.
            //
            // This used to assert the opposite (the object moved UP by a quarter cell per level),
            // because GetBounds() lifted the whole cell. The collider's own offset was measured from
            // the object, so the two errors added up to a box a full half-cell above the ledge.
            Vector3 flat = TileCollisionBuilder.LocalPositionFor(Tile(3, 5, TilePhysicsType.Wall, 0));
            Vector3 raised = TileCollisionBuilder.LocalPositionFor(Tile(3, 5, TilePhysicsType.Wall, 2));

            Assert.AreEqual(flat.y - 2f * 5f / PixelsPerUnit, raised.y, 1e-5f,
                "two height levels shorten the tile by half a cell, so its centre drops a quarter cell");
            Assert.AreEqual(flat.x, raised.x, 1e-5f, "height does not move the cell sideways");
            Assert.AreEqual(flat.z, raised.z, 1e-5f, "height does not change the depth order");
        }

        [Test]
        public void LocalPositionFor_StepsExactlyOneCellPerTile()
        {
            // The cross-check that catches a scale mistake: the step between neighbours must be the
            // shipped tile size, not the mask's, not 1, and not the sprite's PPU-derived size.
            float step = CellPixels / PixelsPerUnit;

            Vector3 origin = TileCollisionBuilder.LocalPositionFor(Tile(0, 0, TilePhysicsType.Wall));
            Vector3 right = TileCollisionBuilder.LocalPositionFor(Tile(1, 0, TilePhysicsType.Wall));
            Vector3 up = TileCollisionBuilder.LocalPositionFor(Tile(0, 1, TilePhysicsType.Wall));

            Assert.AreEqual(step, right.x - origin.x, 1e-5f, "one cell to the right is one tile wide");
            Assert.AreEqual(0f, right.y - origin.y, 1e-5f, "x does not move y");
            Assert.AreEqual(step, up.y - origin.y, 1e-5f, "one cell up is one tile tall");
            Assert.AreEqual(0f, up.x - origin.x, 1e-5f, "y does not move x");

            // Guard against the whole fixture passing on a degenerate cell (0 would satisfy nothing
            // above, but a 1-unit step would — and 1 is exactly the mistake of forgetting the /100).
            Assert.AreEqual(0.4f, step, 1e-6f, "a cell is 0.4 units at 40 px and 100 px/unit");
        }

        [Test]
        public void LocalPositionFor_IsZeroForNoTile()
        {
            Assert.AreEqual(Vector3.zero, TileCollisionBuilder.LocalPositionFor(null));
        }

        // ─── The default ────────────────────────────────────────────────────────

        [Test]
        public void Settings_DefaultToTheQuarterRenderer()
        {
            // This is a decision, not an implementation detail: it is what every room in the game is
            // drawn with, and what decides that the tiles are built for collision only. Flipping the
            // initialiser is a global behaviour change and should fail a test rather than quietly
            // change the picture. (The shipped settings asset carries the same value; a fixture cannot
            // read it offline, so the initialiser is the thing pinned.)
            //
            // EDITOR ONLY, and the same known limitation as
            // RoomTerrainWiringTests.TryResolve_BuildsOnlyWhenAllThreeInputsArePresent:
            // ScriptableObject.CreateInstance is an ECall, so the offline harness reports this one as a
            // SecurityException rather than a pass. It is green in the Editor.
            var settings = ScriptableObject.CreateInstance<PfeDebugSettings>();

            try
            {
                Assert.IsTrue(settings.QuadRoomTerrain,
                    "the quarter renderer is the shipped terrain path; `terrain quad off` is the escape hatch");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}

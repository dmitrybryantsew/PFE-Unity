using NUnit.Framework;
using PFE.Systems.Map.Minimap;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map.Minimap
{
    /// <summary>
    /// Pins <see cref="LandMinimapBuffer"/>'s layout arithmetic — the one place three Y conventions are
    /// reconciled.
    ///
    /// <para><b>Why this needs a guard.</b> The land grid numbers rows downward (AS3), the room's own
    /// tile grid numbers them upward (the port), and a Unity <c>Texture2D</c> is bottom-up. Two of those
    /// reversals cancel into a single addition, and that cancellation is the kind of "it looks right in
    /// the room everyone tests in" arithmetic this project has paid for before (the land-Y mirror). The
    /// whole class is pure, so it runs offline and a mutant bites immediately.</para>
    /// </summary>
    [TestFixture]
    public sealed class LandMinimapBufferTests
    {
        private static readonly Color32 A = new Color32(1, 2, 3, 255);
        private static readonly Color32 B = new Color32(4, 5, 6, 255);
        private static readonly Color32 Fill = new Color32(9, 9, 9, 255);

        // ── X is a plain concatenation ───────────────────────────────────────────────────────

        [Test]
        public void TexelX_IsNotMirrored()
        {
            Assert.That(LandMinimapBuffer.TexelX(0, 0, 48), Is.EqualTo(0));
            Assert.That(LandMinimapBuffer.TexelX(1, 0, 48), Is.EqualTo(48), "cell 1 starts right after cell 0.");
            Assert.That(LandMinimapBuffer.TexelX(2, 5, 48), Is.EqualTo(101), "2 * 48 + 5.");
        }

        // ── Y: land row 0 at the top, the floor at the bottom of a room ──────────────────────

        [Test]
        public void TexelY_PutsLandRowZeroAtTheTopOfTheImage()
        {
            const int rows = 3;
            const int tileHeight = 25;

            // The image is 75 tall. Land row 0 is the TOP band: texel rows [50, 75).
            Assert.That(LandMinimapBuffer.TexelY(rows, 0, 0, tileHeight), Is.EqualTo(50),
                "land row 0's floor sits at the bottom of the top band.");
            Assert.That(LandMinimapBuffer.TexelY(rows, 0, tileHeight - 1, tileHeight), Is.EqualTo(74),
                "…and its ceiling is the image's top texel row.");
        }

        [Test]
        public void TexelY_PutsTheBottomLandRowAtTheBottomOfTheImage()
        {
            const int rows = 3;
            const int tileHeight = 25;

            Assert.That(LandMinimapBuffer.TexelY(rows, 2, 0, tileHeight), Is.EqualTo(0),
                "the last land row is the bottom band, and the band's bottom is the image's bottom.");
            Assert.That(LandMinimapBuffer.TexelY(rows, 2, tileHeight - 1, tileHeight), Is.EqualTo(24));
        }

        [Test]
        public void TexelY_KeepsTheFloorBelowTheCeilingWithinARoom()
        {
            const int rows = 2;
            const int tileHeight = 25;

            // Port tile row 0 is the FLOOR, so within one room a larger tile row must be a LARGER texel
            // row (higher on the drawn image). If this inverts, every room is drawn upside down.
            int floor = LandMinimapBuffer.TexelY(rows, 0, 0, tileHeight);
            int ceiling = LandMinimapBuffer.TexelY(rows, 0, tileHeight - 1, tileHeight);

            Assert.That(floor, Is.LessThan(ceiling),
                "the floor (port tile row 0) must be BELOW the ceiling in the image.");
        }

        /// <summary>
        /// The control for the formula: the obvious implementation — <c>cellRow * tileHeight + tileY</c>,
        /// with no flip — must disagree. If it ever agrees, the guard above is vacuous.
        /// </summary>
        [Test]
        public void TexelY_IsNotTheNaiveNoFlipFormula()
        {
            const int rows = 3;
            const int tileHeight = 25;

            int correct = LandMinimapBuffer.TexelY(rows, 0, 0, tileHeight);
            int naive = 0 * tileHeight + 0;

            Assert.That(correct, Is.Not.EqualTo(naive),
                "land row 0 must be flipped to the top band; a no-flip formula would put it at the bottom.");
            Assert.That(correct - naive, Is.EqualTo((rows - 1) * tileHeight),
                "the offset is exactly one room height per land row from the bottom.");
        }

        // ── Writing and reading ──────────────────────────────────────────────────────────────

        [Test]
        public void SetTile_And_GetPixel_Agree()
        {
            var buffer = new LandMinimapBuffer(columns: 2, rows: 2, tileWidth: 4, tileHeight: 3);
            buffer.Fill(Fill);

            // Cell (1,1) is the bottom-right cell. Its tile (2,1) is at texel (6, 1).
            buffer.SetTile(cellColumn: 1, cellRowFromTop: 1, tileX: 2, tileY: 1, color: A);
            Assert.That(buffer.GetPixel(6, 1), Is.EqualTo(A));

            // Cell (0,0) is the top-left cell; its floor tile is at texel row 3.
            buffer.SetTile(cellColumn: 0, cellRowFromTop: 0, tileX: 0, tileY: 0, color: B);
            Assert.That(buffer.GetPixel(0, 3), Is.EqualTo(B));
        }

        [Test]
        public void SetTile_OutsideTheImage_IsIgnored()
        {
            var buffer = new LandMinimapBuffer(columns: 1, rows: 1, tileWidth: 4, tileHeight: 3);
            buffer.Fill(Fill);

            // Tile row 5 is past the 3-tall image; it must not wrap around to another row.
            buffer.SetTile(cellColumn: 0, cellRowFromTop: 0, tileX: 0, tileY: 5, color: A);

            for (int y = 0; y < buffer.Height; y++)
            {
                for (int x = 0; x < buffer.Width; x++)
                {
                    Assert.That(buffer.GetPixel(x, y), Is.EqualTo(Fill),
                        $"({x},{y}) must be untouched; an out-of-range write must not wrap.");
                }
            }
        }

        [Test]
        public void GetPixel_OutsideTheImage_IsDefault()
        {
            var buffer = new LandMinimapBuffer(1, 1, 4, 3);
            Assert.That(buffer.GetPixel(-1, 0), Is.EqualTo(default(Color32)));
            Assert.That(buffer.GetPixel(0, buffer.Height), Is.EqualTo(default(Color32)));
        }

        [Test]
        public void Fill_CoversEveryTexel()
        {
            var buffer = new LandMinimapBuffer(2, 3, 5, 4);
            Assert.That(buffer.Width, Is.EqualTo(10));
            Assert.That(buffer.Height, Is.EqualTo(12));

            buffer.Fill(A);

            foreach (Color32 pixel in buffer.Pixels)
            {
                Assert.That(pixel, Is.EqualTo(A));
            }
        }

        // ── The two composite writers ────────────────────────────────────────────────────────

        [Test]
        public void SetCellOutline_DrawsOnlyTheBorder()
        {
            var buffer = new LandMinimapBuffer(1, 1, 4, 3);
            buffer.Fill(Fill);

            buffer.SetCellOutline(0, 0, A);

            // Corners and edges are the outline…
            Assert.That(buffer.GetPixel(0, 0), Is.EqualTo(A));
            Assert.That(buffer.GetPixel(3, 0), Is.EqualTo(A));
            Assert.That(buffer.GetPixel(0, 2), Is.EqualTo(A));
            Assert.That(buffer.GetPixel(3, 2), Is.EqualTo(A));
            Assert.That(buffer.GetPixel(2, 0), Is.EqualTo(A), "a bottom edge texel");
            Assert.That(buffer.GetPixel(0, 1), Is.EqualTo(A), "a left edge texel");

            // …and the interior is not, so the outline does not blank the room it marks.
            Assert.That(buffer.GetPixel(1, 1), Is.EqualTo(Fill));
            Assert.That(buffer.GetPixel(2, 1), Is.EqualTo(Fill));
        }

        [Test]
        public void FillCellTiles_NormalisesAReversedRange()
        {
            var buffer = new LandMinimapBuffer(1, 1, 5, 4);
            buffer.Fill(Fill);

            // Deliberately reversed on both axes.
            buffer.FillCellTiles(0, 0, fromTileX: 3, fromTileY: 2, toTileX: 1, toTileY: 0, color: A);

            Assert.That(buffer.GetPixel(LandMinimapBuffer.TexelX(0, 1, 5), LandMinimapBuffer.TexelY(1, 0, 1, 4)),
                Is.EqualTo(A), "tile (1,1) is inside the normalised range.");
            Assert.That(buffer.GetPixel(LandMinimapBuffer.TexelX(0, 3, 5), LandMinimapBuffer.TexelY(1, 0, 2, 4)),
                Is.EqualTo(A), "tile (3,2) is inside the normalised range.");
            Assert.That(buffer.GetPixel(LandMinimapBuffer.TexelX(0, 4, 5), LandMinimapBuffer.TexelY(1, 0, 3, 4)),
                Is.EqualTo(Fill), "tile (4,3) is outside the range.");
        }
    }
}

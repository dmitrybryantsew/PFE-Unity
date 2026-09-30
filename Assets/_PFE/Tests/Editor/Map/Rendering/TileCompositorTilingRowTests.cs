using NUnit.Framework;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the row conversion used when a tile's material texture is sampled.
    ///
    /// <para><b>The bug these exist for.</b> <c>TileCompositor.GenerateTilePixels</c> iterates
    /// <c>y</c> top-down, because <c>ComputeEdgeMask</c> is written top-down (<c>py &lt; TILE_PX/2</c>
    /// is the "top-left quadrant"), and mirrors at the write:
    /// <c>pixels[(TILE_PX - 1 - y) * TILE_PX + x]</c>. <c>SampleSpriteAlphaCore</c> mirrors at the
    /// read: <c>texY = sampleRect.y + (TILE_PX - 1) - py</c>. The two tiling-texture readers did
    /// neither — they used the top-down row directly as a bottom-up buffer index, so every tiling
    /// texture was read vertically mirrored.</para>
    ///
    /// <para><b>Why only shelves showed it.</b> A wall or floor fill supplies interior pattern only;
    /// its silhouette comes from a mask, so a vertical mirror is nearly invisible. The beam materials
    /// declare <c>shelf='1'</c> and <b>no</b> <c>vid</c> (<c>AllData.as:6530-6570</c>: ids
    /// <c>'-'</c>, <c>'Д'</c>, <c>'Е'</c>, <c>'К'</c>, <c>'Н'</c>, <c>'Р'</c>), so they carry no
    /// <c>tileFront</c> frame and are composited purely from <c>&lt;main tex=…/&gt;</c> — their whole
    /// silhouette is the texture's alpha. Mirrored, the beam drew below the cell top while
    /// <c>TileCollider</c> stayed top-aligned to the cell edge, so units stood on air.</para>
    ///
    /// <para><b>The oracle.</b> AS3 fills with <c>beginBitmapFill(texture)</c> then
    /// <c>drawRect(0, 0, kusokX*tileX, kusokY*tileY)</c> (<c>Grafon.as:815</c>) and blits the chunk
    /// with <c>Matrix(tx=0, ty=0)</c> (<c>Grafon.as:887-899</c>). No matrix means the bitmap's
    /// top-left sits at the chunk origin and its phase runs top-down, matching Flash's y-down space.
    /// So cell row <c>y</c> must sample texture row <c>y</c> counted from the <i>top</i>.</para>
    /// </summary>
    [TestFixture]
    public class TileCompositorTilingRowTests
    {
        // tShelf.png is 240x40 and its opaque content occupies top-down rows 0..33
        // (measured: top margin 0, bottom margin 6). This is the "Ржавая балка" texture that
        // AllData.as:6530 gives to the '-' form.
        const int ShelfTextureHeight = 40;
        const int ShelfOpaqueTopDownRows = 34;

        /// <summary>
        /// The texture buffer exactly as <c>Texture2D.GetPixels()</c> hands it over: row 0 at the
        /// BOTTOM. tShelf's opaque content is its top 34 rows, so in this buffer that is rows 6..39.
        /// </summary>
        static bool[] ShelfBottomUpBuffer()
        {
            var buffer = new bool[ShelfTextureHeight];
            int firstOpaqueBufferRow = ShelfTextureHeight - ShelfOpaqueTopDownRows;
            for (int row = 0; row < ShelfTextureHeight; row++)
            {
                buffer[row] = row >= firstOpaqueBufferRow;
            }
            return buffer;
        }

        [Test]
        public void TopDownRowZero_AddressesTheTopOfTheBuffer()
        {
            Assert.AreEqual(
                ShelfTextureHeight - 1,
                TileCompositor.ToBottomUpRow(0, ShelfTextureHeight),
                "GetPixels() is bottom-up, so the texture's top row is the buffer's last row. " +
                "Returning 0 here is the regression: it reads the texture upside down.");
        }

        [Test]
        public void LastTopDownRow_AddressesTheBottomOfTheBuffer()
        {
            Assert.AreEqual(0, TileCompositor.ToBottomUpRow(ShelfTextureHeight - 1, ShelfTextureHeight));
        }

        [Test]
        public void MappingIsAMirror_NotAnOffset()
        {
            // The property that distinguishes the fix from the bug. An offset (the old
            // `PositiveModulo(row, height)`) is the identity on [0, height); a mirror is `height-1-row`.
            for (int row = 0; row < ShelfTextureHeight; row++)
            {
                Assert.AreEqual(
                    ShelfTextureHeight - 1 - row,
                    TileCompositor.ToBottomUpRow(row, ShelfTextureHeight),
                    $"row {row}: a vertical mirror must be height-1-row, not the identity.");
            }
        }

        [Test]
        public void MappingIsItsOwnInverse()
        {
            for (int row = 0; row < ShelfTextureHeight; row++)
            {
                int once = TileCompositor.ToBottomUpRow(row, ShelfTextureHeight);
                Assert.AreEqual(row, TileCompositor.ToBottomUpRow(once, ShelfTextureHeight));
            }
        }

        [Test]
        public void RowsBeyondTheTexture_WrapBeforeMirroring()
        {
            // Tiling: a tile row past the first must keep sampling, so the modulo comes first.
            Assert.AreEqual(
                TileCompositor.ToBottomUpRow(3, ShelfTextureHeight),
                TileCompositor.ToBottomUpRow(ShelfTextureHeight + 3, ShelfTextureHeight));
            Assert.AreEqual(
                TileCompositor.ToBottomUpRow(3, ShelfTextureHeight),
                TileCompositor.ToBottomUpRow(-(ShelfTextureHeight - 3), ShelfTextureHeight),
                "Negative rows must wrap positively, not index out of range.");
        }

        [Test]
        public void DegenerateHeight_DoesNotIndexBelowZero()
        {
            Assert.AreEqual(0, TileCompositor.ToBottomUpRow(7, 0));
            Assert.AreEqual(0, TileCompositor.ToBottomUpRow(7, -4));
        }

        [Test]
        public void ShelfBeamTop_CoincidesWithTheCellTop()
        {
            // THE SYMPTOM, as data. TileCollider aligns the platform collider's top to the cell's top
            // edge, so cell row 0 is the surface a unit stands on. It must sample an opaque texel.
            bool[] buffer = ShelfBottomUpBuffer();

            Assert.IsTrue(
                buffer[TileCompositor.ToBottomUpRow(0, ShelfTextureHeight)],
                "Cell row 0 is the walkable surface (TileCollider's platform box is top-aligned to " +
                "the cell edge). It sampled a transparent texel, so the beam's visible top sat below " +
                "the unit's feet — the reported 'standing on air'.");
        }

        [Test]
        public void ShelfBeam_IsNotPushedBelowTheCellTop()
        {
            bool[] buffer = ShelfBottomUpBuffer();

            int firstOpaqueCellRow = -1;
            for (int cellRow = 0; cellRow < ShelfTextureHeight; cellRow++)
            {
                if (buffer[TileCompositor.ToBottomUpRow(cellRow, ShelfTextureHeight)])
                {
                    firstOpaqueCellRow = cellRow;
                    break;
                }
            }

            Assert.AreEqual(
                0,
                firstOpaqueCellRow,
                "The mirrored read put the beam's top at cell row 6 (tShelf), 3 (tConBeam) or 14 " +
                "(tCloudBeam) while the collider stayed at row 0.");
        }
    }
}

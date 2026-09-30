using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Tests for <see cref="UnitSheetLayout"/> — the grid arithmetic and filename parsing that turn an
    /// AS3 unit sheet into Unity sprites.
    ///
    /// <para>These two rules are the ones that fail <i>plausibly</i>: a vertically mirrored sheet still
    /// has the right cell count, and a pivot with the flip applied the wrong way still lands inside the
    /// cell. Neither would look like a bug in a log, so they are pinned here rather than trusted to
    /// review. The numbers are taken from the real data — <c>sprRaider1</c> is 2880x1080 in 120x120
    /// cells, <c>hellhound1</c> 4800x1190 in 200x170.</para>
    /// </summary>
    public class UnitSheetLayoutTests
    {
        static readonly Vector2Int RaiderCell = new Vector2Int(120, 120);   // sprRaider1, 24 cols x 9 rows
        static readonly Vector2Int HellhoundCell = new Vector2Int(200, 170); // sprHellhound, 24 cols x 7 rows
        static readonly Vector2Int MoleratCell = new Vector2Int(85, 58);     // sprMolerat, 14 cols x 4 rows

        // ── CellRect: the row flip ───────────────────────────────────────────

        [Test]
        public void CellRect_FirstRow_IsAtTheTopOfTheTexture()
        {
            // AS3 row 0 is the PNG's top row. Of 9 rows, that is the 9th from the bottom, so
            // y = (9 - 1 - 0) * 120 = 960, and the cell spans 960..1080 — the top of a 1080px texture.
            Rect rect = UnitSheetLayout.CellRect(row: 0, col: 0, rows: 9, cell: RaiderCell);

            Assert.That(rect.y, Is.EqualTo(960f));
            Assert.That(rect.yMax, Is.EqualTo(1080f), "the first AS3 row must touch the texture's top edge");
        }

        [Test]
        public void CellRect_LastRow_IsAtTheBottomOfTheTexture()
        {
            Rect rect = UnitSheetLayout.CellRect(row: 8, col: 0, rows: 9, cell: RaiderCell);

            Assert.That(rect.y, Is.EqualTo(0f), "the last AS3 row must touch the texture's bottom edge");
            Assert.That(rect.yMax, Is.EqualTo(120f));
        }

        [Test]
        public void CellRect_IsFlipped_NotCopiedStraightThrough()
        {
            // Paired control for the two tests above: if the flip were dropped, row 0 would sit at
            // y = 0 and row 8 at y = 960 — the exact inverse. Assert both directions so a regression
            // cannot pass by swapping them.
            Rect first = UnitSheetLayout.CellRect(row: 0, col: 0, rows: 9, cell: RaiderCell);
            Rect last = UnitSheetLayout.CellRect(row: 8, col: 0, rows: 9, cell: RaiderCell);

            Assert.That(first.y, Is.GreaterThan(last.y), "row 0 must be ABOVE row 8 in Unity's y-up space");
            Assert.That(first.y, Is.Not.EqualTo(0f));
            Assert.That(last.y, Is.Not.EqualTo(960f));
        }

        [Test]
        public void CellRect_ColumnAdvancesByCellWidth_AndIsNotFlipped()
        {
            Rect rect = UnitSheetLayout.CellRect(row: 0, col: 23, rows: 9, cell: RaiderCell);

            Assert.That(rect.x, Is.EqualTo(23 * 120f), "columns count left to right in both spaces");
            Assert.That(rect.width, Is.EqualTo(120f));
            Assert.That(rect.xMax, Is.EqualTo(2880f), "the last column must touch the texture's right edge");
        }

        [Test]
        public void CellRect_UsesCellHeightForTheFlip_NotCellWidth()
        {
            // hellhound1 is 200x170 — a non-square cell, which is exactly where using sprX for both
            // axes goes wrong. 7 rows of 170: row 6 is the bottom, row 0 spans 1020..1190.
            Rect bottom = UnitSheetLayout.CellRect(row: 6, col: 0, rows: 7, cell: HellhoundCell);
            Rect top = UnitSheetLayout.CellRect(row: 0, col: 0, rows: 7, cell: HellhoundCell);

            Assert.That(bottom.y, Is.EqualTo(0f));
            Assert.That(bottom.height, Is.EqualTo(170f));
            Assert.That(top.y, Is.EqualTo(6 * 170f));
            Assert.That(top.yMax, Is.EqualTo(1190f), "the sheet is 1190 tall, not 7*200");
        }

        [Test]
        public void CellRect_SingleRowSheet_IsTheWholeTexture()
        {
            // ant1 is 1170x32 in 78x32 cells: one row, so the flip is the identity. This is the case
            // that must not regress into an off-by-one.
            Rect rect = UnitSheetLayout.CellRect(row: 0, col: 0, rows: 1, cell: new Vector2Int(78, 32));

            Assert.That(rect.y, Is.EqualTo(0f));
            Assert.That(rect.height, Is.EqualTo(32f));
        }

        [Test]
        public void CellRect_EveryCellStaysInsideTheSheet()
        {
            // The invariant that catches a wrong flip direction for any grid: no cell may fall outside
            // the texture, and the grid must exactly tile it.
            const int cols = 24;
            const int rows = 7;
            var cell = HellhoundCell;
            float width = cols * cell.x;
            float height = rows * cell.y;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    Rect rect = UnitSheetLayout.CellRect(row, col, rows, cell);

                    Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(0f));
                    Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(0f));
                    Assert.That(rect.xMax, Is.LessThanOrEqualTo(width));
                    Assert.That(rect.yMax, Is.LessThanOrEqualTo(height));
                }
            }
        }

        // ── PivotFor: the registration point ─────────────────────────────────

        [Test]
        public void Pivot_DeclaredRegistration_FlipsY()
        {
            // A registration point 30px down from the cell's top: in Unity's y-up pivot space that is
            // 1 - 30/120 = 0.75.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(60, 30), RaiderCell);

            Assert.That(pivot.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(pivot.y, Is.EqualTo(0.75f).Within(1e-5f));
        }

        [Test]
        public void Pivot_RegistrationOnTheBottomEdge_IsZero()
        {
            // The feet-on-the-ground case: sprDY equal to the cell height puts the origin at the bottom.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(60, 120), RaiderCell);

            Assert.That(pivot.y, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Pivot_NotDeclared_UsesTheOracleFallback()
        {
            // AS3's own default is -1 (Unit.as:511-514), and a negative component then means "centre
            // horizontally, 10px above the cell bottom" — pivotY = 10 / cellY.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(-1, -1), RaiderCell);

            Assert.That(pivot.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(pivot.y, Is.EqualTo(10f / 120f).Within(1e-5f));
        }

        [Test]
        public void Pivot_NotDeclared_ScalesTheFallbackByCellHeight()
        {
            // The fallback is a pixel distance, so it must be divided by THIS cell's height.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(-1, -1), HellhoundCell);

            Assert.That(pivot.y, Is.EqualTo(10f / 170f).Within(1e-5f));
        }

        [Test]
        public void Pivot_HalfDeclared_MixesDeclaredXWithFallbackY()
        {
            // The axes are independent: a declared sprDX must not drag the y fallback with it.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(85, -1), MoleratCell);

            Assert.That(pivot.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(pivot.y, Is.EqualTo(10f / 58f).Within(1e-5f));
        }

        [Test]
        public void Pivot_ZeroIsDeclared_NotAbsent()
        {
            // The presence test is `>= 0`, so 0 is a REAL registration point — the cell's top-left —
            // and must not be mistaken for "not declared". Getting this wrong silently pivots every
            // such unit at its centre instead.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(0, 0), RaiderCell);

            Assert.That(pivot.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(pivot.y, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Pivot_OutsideTheCell_IsNotClamped()
        {
            // AS3 permits a registration point outside the cell, so this function must not silently
            // clamp — the importer clamps and reports instead, because clamping loses information.
            Vector2 pivot = UnitSheetLayout.PivotFor(new Vector2Int(200, 400), RaiderCell);

            Assert.That(pivot.x, Is.EqualTo(200f / 120f).Within(1e-5f));
            Assert.That(pivot.y, Is.EqualTo(1f - 400f / 120f).Within(1e-5f));
            Assert.That(pivot.y, Is.LessThan(0f));
        }

        // ── Cell naming and indexing ─────────────────────────────────────────

        [Test]
        public void CellName_CarriesBothCoordinates()
        {
            Assert.That(UnitSheetLayout.CellName("sprRaider5", 3, 17), Is.EqualTo("sprRaider5_r3_c17"));
        }

        [Test]
        public void CellName_IsTheSameForTwoStatesSharingARow()
        {
            // Not a collision to avoid: raider's `death` and `fall` are both y='5', and they share the
            // same sprites by design, so the name must depend only on (row, column).
            string death = UnitSheetLayout.CellName("sprRaider1", 5, 0);
            string fall = UnitSheetLayout.CellName("sprRaider1", 5, 0);

            Assert.That(death, Is.EqualTo(fall));
            Assert.That(UnitSheetLayout.CellName("sprRaider1", 5, 0),
                        Is.Not.EqualTo(UnitSheetLayout.CellName("sprRaider1", 6, 0)));
        }

        [Test]
        public void CellIndex_IsRowMajor()
        {
            Assert.That(UnitSheetLayout.CellIndex(0, 0, 24), Is.EqualTo(0));
            Assert.That(UnitSheetLayout.CellIndex(0, 23, 24), Is.EqualTo(23));
            Assert.That(UnitSheetLayout.CellIndex(1, 0, 24), Is.EqualTo(24));
            Assert.That(UnitSheetLayout.CellIndex(1, 2, 24), Is.EqualTo(26));
            Assert.That(UnitSheetLayout.CellIndex(8, 23, 24), Is.EqualTo(8 * 24 + 23));
        }

        // ── Filename parsing ─────────────────────────────────────────────────

        [Test]
        public void SheetNameFromFile_StripsTheExportIndex()
        {
            Assert.That(UnitSheetLayout.SheetNameFromFile("24_sprRaider5.png"), Is.EqualTo("sprRaider5"));
            Assert.That(UnitSheetLayout.SheetNameFromFile("1_sprZebra5.png"), Is.EqualTo("sprZebra5"));
            Assert.That(UnitSheetLayout.SheetNameFromFile("43_sprAlicorn1.png"), Is.EqualTo("sprAlicorn1"));
        }

        [Test]
        public void SheetNameFromFile_AcceptsAFullPath()
        {
            Assert.That(
                UnitSheetLayout.SheetNameFromFile(@"E:\Games\UnityGames\pfeToUnity\sprite.swf\images\24_sprRaider5.png"),
                Is.EqualTo("sprRaider5"));
        }

        [Test]
        public void SheetNameFromFile_LeavesNamesWithoutAnIndexAlone()
        {
            // Control for the test above: the pattern is `^\d+_`, so a name that merely CONTAINS digits
            // or an underscore must survive untouched. sprBl3 is a real sheet whose name ends in a digit.
            Assert.That(UnitSheetLayout.SheetNameFromFile("sprBl3.png"), Is.EqualTo("sprBl3"));
            Assert.That(UnitSheetLayout.SheetNameFromFile("sprAlicorn1.png"), Is.EqualTo("sprAlicorn1"));
            Assert.That(UnitSheetLayout.SheetNameFromFile("sprThunderHead.png"), Is.EqualTo("sprThunderHead"));
        }

        [Test]
        public void FrameNumberOf_ParsesBothExportAndCopiedNames()
        {
            Assert.That(UnitSheetLayout.FrameNumberOf("1.png"), Is.EqualTo(1));
            Assert.That(UnitSheetLayout.FrameNumberOf("10.png"), Is.EqualTo(10));
            Assert.That(UnitSheetLayout.FrameNumberOf("f007.png"), Is.EqualTo(7));
            Assert.That(UnitSheetLayout.FrameNumberOf("f060.png"), Is.EqualTo(60));
        }

        [Test]
        public void FrameNumberOf_SortsNumerically_NotLexically()
        {
            // The reason this function exists: a lexical sort would order 1, 10, 2 and scramble the
            // slime's 60-frame animation.
            string[] frames = { "10.png", "2.png", "1.png", "60.png" };
            System.Array.Sort(frames, (a, b) =>
                UnitSheetLayout.FrameNumberOf(a).CompareTo(UnitSheetLayout.FrameNumberOf(b)));

            Assert.That(frames, Is.EqualTo(new[] { "1.png", "2.png", "10.png", "60.png" }));
        }

        // ── TryReadPngSize ───────────────────────────────────────────────────
        //
        // These matter more than they look. The importer originally measured Unity's *imported* texture
        // while maxTextureSize was still at its 2048 default, so it read a downscaled size and sliced
        // every grid wrongly: sprRaider1 came back 2048x768 and became a 17x6 grid instead of 24x9,
        // with 405 states reported out of range and two sheets dropped entirely. Reading the file is
        // what makes the grid independent of import settings, so it is pinned here.

        static byte[] PngHeader(int width, int height)
        {
            var b = new byte[24];
            b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
            b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
            b[8] = 0; b[9] = 0; b[10] = 0; b[11] = 13;                       // IHDR payload length
            b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
            b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16);
            b[18] = (byte)(width >> 8); b[19] = (byte)width;
            b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16);
            b[22] = (byte)(height >> 8); b[23] = (byte)height;
            return b;
        }

        static string WriteTempFile(byte[] bytes)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pfe_sheet_" + System.Guid.NewGuid().ToString("N") + ".png");
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }

        [Test]
        public void TryReadPngSize_ReadsTheRealSheetDimensions()
        {
            // sprRaider7's actual exported size.
            string path = WriteTempFile(PngHeader(3120, 1300));
            try
            {
                int w, h;
                Assert.That(UnitSheetLayout.TryReadPngSize(path, out w, out h), Is.True);
                Assert.That(w, Is.EqualTo(3120));
                Assert.That(h, Is.EqualTo(1300));

                // And that it grids to the real 24x10 rather than a downscaled 2048x853.
                Assert.That(w / 130, Is.EqualTo(24));
                Assert.That(h / 130, Is.EqualTo(10));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void TryReadPngSize_ReadsBigEndian_NotLittleEndian()
        {
            // 256 and 1 are chosen so a little-endian reader returns 65536 and 16777216 instead —
            // the failure would otherwise be invisible, since both are positive.
            string path = WriteTempFile(PngHeader(256, 1));
            try
            {
                int w, h;
                Assert.That(UnitSheetLayout.TryReadPngSize(path, out w, out h), Is.True);
                Assert.That(w, Is.EqualTo(256), "PNG dimensions are big-endian");
                Assert.That(h, Is.EqualTo(1));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void TryReadPngSize_RejectsANonPng()
        {
            string path = WriteTempFile(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 });
            try
            {
                int w, h;
                Assert.That(UnitSheetLayout.TryReadPngSize(path, out w, out h), Is.False);
                Assert.That(w, Is.EqualTo(0));
                Assert.That(h, Is.EqualTo(0));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void TryReadPngSize_RejectsATruncatedFile()
        {
            var truncated = new byte[20];
            System.Array.Copy(PngHeader(100, 100), truncated, 20);
            string path = WriteTempFile(truncated);
            try
            {
                int w, h;
                Assert.That(UnitSheetLayout.TryReadPngSize(path, out w, out h), Is.False,
                    "a file too short to hold a full header must not be parsed as one");
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void TryReadPngSize_RejectsAMissingFile()
        {
            int w, h;
            Assert.That(UnitSheetLayout.TryReadPngSize(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfe_no_such_sheet_9f3a.png"),
                out w, out h), Is.False);
        }

        // ── RestingCell: the frame a standing unit shows ─────────────────────

        [Test]
        public void RestingCell_WithNoAnimations_IsTheFirstCell()
        {
            // The sheet's own first cell is the honest fallback: it is a real frame of this unit,
            // whereas refusing to pick one would leave the unit invisible.
            Assert.That(UnitSheetLayout.RestingCell(null), Is.EqualTo(Vector2Int.zero));
        }

        [Test]
        public void RestingCell_WithNoStayState_IsTheFirstCell()
        {
            var set = new AnimationSet { walk = new AnimationFrame { row = 4, length = 12 } };

            Assert.That(UnitSheetLayout.RestingCell(set), Is.EqualTo(Vector2Int.zero),
                "a set with no `stay` must not borrow another state's row");
        }

        [Test]
        public void RestingCell_ReadsStayAtRowZero_ForTheRaiderShape()
        {
            // The real declaration, verbatim: `<blit id='stay'/>` — no `y`, no `len`. So AS3 row 0
            // and a single frame, i.e. the sheet's top-left cell.
            var set = new AnimationSet { stay = new AnimationFrame { row = 0, length = 1 } };

            Assert.That(UnitSheetLayout.RestingCell(set), Is.EqualTo(Vector2Int.zero));
        }

        [Test]
        public void RestingCell_ReadsTheRowFromTheState_RatherThanAssumingZero()
        {
            // Paired with the test above: same input shape, different row, different answer. Without
            // this, a hardcoded `Vector2Int.zero` would pass the whole suite.
            var set = new AnimationSet { stay = new AnimationFrame { row = 3, length = 1 } };

            Assert.That(UnitSheetLayout.RestingCell(set), Is.EqualTo(new Vector2Int(0, 3)));
        }

        [Test]
        public void RestingCell_ReadsFirstFrame_RatherThanAssumingZero()
        {
            // AS3 `@ff` -> firstf. The cursor starts at this cell, so it is the frame drawn first.
            var set = new AnimationSet { stay = new AnimationFrame { row = 2, length = 6, firstFrame = 5 } };

            Assert.That(UnitSheetLayout.RestingCell(set), Is.EqualTo(new Vector2Int(5, 2)));
        }

        [Test]
        public void RestingCell_IsNotTheIconCell_ForTheFiveUnitsThatDeclareIcoY()
        {
            // Regression for the bug this method was extracted to fix. merc1..merc5 declare
            // `icoY='2'`, and the `merc` family puts `die` at row 2 — so the icon cell (0,2) is the
            // DEATH frame. `Unit.initIco` (Unit.as:910-948) slices it into `arrIcos`, a static UI
            // table read only by PipPageInfo.as:439-442 (the PipBoy unit list); the world renderer
            // never sees it. The resting frame must therefore come from `stay`, not from icoY.
            var set = new AnimationSet
            {
                stay = new AnimationFrame { row = 0, length = 1 },
                die = new AnimationFrame { row = 2, length = 25 },
            };

            Vector2Int rest = UnitSheetLayout.RestingCell(set);

            Assert.That(rest, Is.EqualTo(Vector2Int.zero));
            Assert.That(rest, Is.Not.EqualTo(new Vector2Int(0, 2)),
                "icoY='2' would select row 2, which is `die` — a corpse as the standing sprite");
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Core.Scripting;
using UnityEngine;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The developer console's quick-action button grid.
    ///
    /// <para>The user's report was literally "buttons in console are not fitting" — a single horizontal
    /// row of fixed-width buttons that ran off the right edge. The grid's whole job is that it cannot, so
    /// the load-bearing test here is <see cref="NoCell_IsPlacedOutsideTheArea"/>. The rest pin the
    /// degenerate cases (very narrow, very wide, none) that a screenshot will never exercise.</para>
    /// </summary>
    [TestFixture]
    public class ConsoleButtonGridTests
    {
        private const float Epsilon = 0.001f;

        /// <summary>The console's real button count, so the numbers here are the numbers on screen.</summary>
        private const int ButtonCount = 14;

        /// <summary>The widths the buttons had before the grid, summed — the row that overflowed.</summary>
        private static readonly float[] OldFixedWidths =
            { 130f, 130f, 95f, 95f, 60f, 130f, 95f, 65f, 55f, 80f, 85f, 60f, 60f, 75f };

        [Test]
        public void EveryButton_GetsItsOwnCell()
        {
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, 994f);

            var seen = new HashSet<(int Row, int Column)>();
            for (int i = 0; i < ButtonCount; i++)
            {
                bool added = seen.Add((i / grid.Columns, i % grid.Columns));
                Assert.IsTrue(added, $"button {i} shares its cell with another");
            }

            Assert.AreEqual(ButtonCount, seen.Count);
        }

        [Test]
        public void NoCell_IsPlacedOutsideTheArea()
        {
            // The user's complaint, negated — and swept across window sizes rather than one screenshot.
            float[] widths = { 130f, 200f, 400f, 800f, 994f, 1280f, 1920f, 3840f };

            foreach (float areaWidth in widths)
            {
                ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, areaWidth);

                for (int column = 0; column < grid.Columns; column++)
                {
                    float right = grid.ColumnX(column) + grid.CellDrawWidth;
                    Assert.LessOrEqual(right, areaWidth + Epsilon,
                        $"column {column} of a {areaWidth}px grid ends at {right}");
                }

                // And the whole grid is as tall as it says, so the log below it is not overlapped.
                float lastBottom = grid.RowY(grid.Rows - 1) + grid.CellHeight;
                Assert.LessOrEqual(lastBottom, grid.TotalHeight + Epsilon,
                    $"the {areaWidth}px grid's last row ends below its reported height");
            }
        }

        [Test]
        public void TheGrid_UsesTheFullWidth()
        {
            // "give them grid area to screen width": the last column reaches the right edge rather than
            // stopping short at some fixed total.
            const float areaWidth = 994f;
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, areaWidth);

            float lastRight = grid.ColumnX(grid.Columns - 1) + grid.CellDrawWidth;
            Assert.AreEqual(areaWidth - grid.Spacing, lastRight, Epsilon);
        }

        [Test]
        public void TheOldFixedRow_WouldHaveOverflowed_ButTheGridDoesNot()
        {
            // A regression pin whose arrange step is checked: if the old widths ever stop overflowing,
            // this test would silently prove nothing, so it asserts the premise first.
            const float areaWidth = 994f;

            float oldTotal = 0f;
            foreach (float w in OldFixedWidths) oldTotal += w;
            Assert.Greater(oldTotal, areaWidth, "the old fixed row must actually overflow, or this proves nothing");

            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(OldFixedWidths.Length, areaWidth);
            Assert.LessOrEqual(grid.Columns * grid.CellWidth, areaWidth + Epsilon);
        }

        [Test]
        public void ANarrowArea_FallsBackToASingleColumn()
        {
            // Narrower than one minimum cell: one column, one button per row — a vertical list, never a
            // row that pokes out of the side.
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, 100f);

            Assert.AreEqual(1, grid.Columns);
            Assert.AreEqual(ButtonCount, grid.Rows);
        }

        [Test]
        public void AWideArea_UsesASingleRow()
        {
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, 1920f);

            Assert.AreEqual(ButtonCount, grid.Columns);
            Assert.AreEqual(1, grid.Rows);
        }

        [Test]
        public void Columns_NeverExceedTheButtonCount()
        {
            // A 4K console must not reserve empty trailing columns.
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, 3840f);

            Assert.AreEqual(ButtonCount, grid.Columns);
        }

        [Test]
        public void TotalHeight_IsRowsTimesCellPlusSpacing()
        {
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(ButtonCount, 400f);

            Assert.Greater(grid.Rows, 1, "a 400px area must wrap, or this does not exercise the sum");
            Assert.AreEqual(grid.Rows * (grid.CellHeight + grid.Spacing), grid.TotalHeight, Epsilon);
        }

        [Test]
        public void NoButtons_OccupiesNoSpace()
        {
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(0, 994f);

            Assert.AreEqual(0, grid.Rows);
            Assert.AreEqual(0f, grid.TotalHeight, Epsilon);
        }
    }

    /// <summary>
    /// The settings flag behind the grid. One value, three front ends (Inspector, title-bar toggle,
    /// <c>ui buttons on|off</c>); these pin its default and that it is writable.
    /// </summary>
    [TestFixture]
    public class ConsoleQuickButtonsSettingTests
    {
        private PfeDebugSettings _settings;

        [SetUp]
        public void SetUp()
        {
            // A throwaway instance, never the project asset: mutating the real ScriptableObject would
            // leak into every later test through DebugOverlays' cached reference.
            _settings = ScriptableObject.CreateInstance<PfeDebugSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            // Fully qualified: `using PFE.Core;` plus `using UnityEngine;` makes a bare `Object`
            // ambiguous, and the compiler would resolve it by erroring at the call site.
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public void Default_ShowsTheButtonGrid()
        {
            // Unlike the overlay mask (default None), the console's buttons default to shown: they are
            // the console's primary affordance, and the console is only visible while it is open.
            Assert.IsTrue(_settings.ShowConsoleQuickButtons);
        }

        [Test]
        public void TheToggle_RoundTrips()
        {
            _settings.ShowConsoleQuickButtons = false;
            Assert.IsFalse(_settings.ShowConsoleQuickButtons);

            _settings.ShowConsoleQuickButtons = true;
            Assert.IsTrue(_settings.ShowConsoleQuickButtons);
        }
    }
}

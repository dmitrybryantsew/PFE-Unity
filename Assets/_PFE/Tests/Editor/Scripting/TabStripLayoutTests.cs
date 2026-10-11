using NUnit.Framework;
using PFE.Core.Scripting;

namespace PFE.Tests.Scripting
{
    /// <summary>
    /// The tab strip's wrapping arithmetic (<see cref="TabStripLayout"/>). <b>Unity-free</b> — it takes
    /// measured widths, not styles — so these run offline through the wall harness as well as in the
    /// editor, which is the reason the arithmetic was extracted from the view in the first place.
    ///
    /// <para>This rule has been wrong once, and the defect was arithmetic, not drawing: the strip budgeted
    /// a hard-coded 4 px per button for "GUILayout's spacing" while GUILayout really advances by the
    /// style's own <c>margin</c>. With eleven tabs the difference accumulated across ten gaps and pushed
    /// the newest tab past the window's right edge, with nothing on screen to say so. The last case below
    /// pins the exact reason it never went red.</para>
    /// </summary>
    [TestFixture]
    public sealed class TabStripLayoutTests
    {
        // ── TabWidth: the quantity GUILayout really advances by ─────────────────

        [Test]
        public void TabWidth_AddsTheStyleMarginToTheMeasuredCaption()
        {
            // CalcSize already includes the button's padding and border; the margin is what GUILayout adds
            // *between* elements, and leaving it out of the budget is the bug this class exists to fix.
            Assert.That(TabStripLayout.TabWidth(40f, 6f), Is.EqualTo(46f));
        }

        [Test]
        public void TabWidth_ZeroMarginIsTheMeasuredCaption()
        {
            Assert.That(TabStripLayout.TabWidth(40f, 0f), Is.EqualTo(40f));
        }

        // ── RowStarts: greedy packing ───────────────────────────────────────────

        [Test]
        public void RowStarts_EmptyOrNullIsNoRows()
        {
            Assert.That(TabStripLayout.RowStarts(new float[0], 500f), Is.Empty);
            Assert.That(TabStripLayout.RowStarts(null, 500f), Is.Empty);
        }

        [Test]
        public void RowStarts_EverythingFits_IsOneRowStartingAtZero()
        {
            Assert.That(TabStripLayout.RowStarts(new[] { 100f, 100f, 100f }, 300f),
                Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void RowStarts_ExactFitDoesNotWrap()
        {
            // "until the next one would pass the edge" — equal is not past.
            Assert.That(TabStripLayout.RowStarts(new[] { 100f, 100f }, 200f), Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void RowStarts_OnePixelShortWrapsBeforeTheSecondTab()
        {
            Assert.That(TabStripLayout.RowStarts(new[] { 100f, 100f }, 199f), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void RowStarts_OversizedTabGetsItsOwnRowRatherThanAnEmptyRowBeforeEach()
        {
            // The `used > 0` guard: a tab wider than the whole strip must not wrap *before* itself, which
            // would emit an empty row ahead of every such tab.
            Assert.That(TabStripLayout.RowStarts(new[] { 500f, 50f }, 200f), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void RowStarts_NonPositiveAvailableDegradesToOneRow()
        {
            Assert.That(TabStripLayout.RowStarts(new[] { 10f, 10f }, 0f), Is.EqualTo(new[] { 0 }));
            Assert.That(TabStripLayout.RowStarts(new[] { 10f, 10f }, -5f), Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void RowStarts_NegativeWidthCountsAsZero()
        {
            // A negative advance is clamped to zero, not subtracted: an unclamped -50 would let the row
            // absorb 100 more than it has, and the third tab would wrongly share the first row.
            Assert.That(TabStripLayout.RowStarts(new[] { 100f, -50f, 100f }, 150f),
                Is.EqualTo(new[] { 0, 2 }));
        }

        [Test]
        public void RowStarts_RowsPartitionTheStrip()
        {
            float[] widths = { 70f, 70f, 70f, 70f, 70f };
            int[] starts = TabStripLayout.RowStarts(widths, 160f);

            Assert.That(starts[0], Is.EqualTo(0), "row 0 always starts at 0");
            Assert.That(starts[starts.Length - 1], Is.LessThan(widths.Length));
            for (int r = 1; r < starts.Length; r++)
            {
                Assert.That(starts[r], Is.GreaterThan(starts[r - 1]), "row starts strictly increase");
            }
        }

        // ── The regression: the margin is the quantity the old budget left out ──

        [Test]
        public void RowStarts_ElevenTabs_WrapWhenTheRealAdvanceIsBudgeted()
        {
            // Eleven tabs, each caption 60 wide, style margin 8 → real advance 68. 11 × 68 = 748 > 700.
            const float caption = 60f;
            const float margin = 8f;
            const float available = 700f;

            var advances = new float[11];
            var captionsOnly = new float[11];
            for (int i = 0; i < 11; i++)
            {
                advances[i] = TabStripLayout.TabWidth(caption, margin);
                captionsOnly[i] = caption;
            }

            // With the real advance the eleventh tab passes the edge and wraps onto its own row.
            Assert.That(TabStripLayout.RowStarts(advances, available), Is.EqualTo(new[] { 0, 10 }));
            Assert.That(TabStripLayout.Wraps(advances, available), Is.True);

            // With the caption alone (the bug) the very same strip "fits" — which is exactly why the
            // overrun never went red: the arithmetic said it fit while the strip ran off the edge.
            Assert.That(TabStripLayout.RowStarts(captionsOnly, available), Is.EqualTo(new[] { 0 }));
        }

        // ── Wraps ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The strip the overlay actually ships — <b>every</b> tab, including the Land Map tab added last.
        ///
        /// <para><b>Why this case and not "N tabs fit".</b> The defect the wrap exists to prevent is
        /// not "the strip is too wide"; it is <em>the last tab being unreachable</em> — pushed past the
        /// right edge with nothing on screen to say so. So the assertion that matters is that the final
        /// index lands on a later row, which is what makes it clickable. A test that only checked the row
        /// count would pass for a strip that wrapped but dropped the tail.</para>
        ///
        /// <para><b>The count now comes from the overlay, not from a literal.</b> It used to be a hand-copied
        /// <c>const int tabs = 14</c>, which read "thirteen" and stayed green while the strip grew to
        /// fourteen — nothing tied it to <c>TabNames</c>, so the test could not go red on a tab addition and
        /// its own name carried a claim that was silently false. It reads
        /// <see cref="PlayerDebugEditorOverlay.TabCount"/> now, so adding a tab changes the fixture.</para>
        /// </summary>
        [Test]
        public void RowStarts_EveryShippedTabIsReachableAndTheLastOneWraps()
        {
            // 100px advance is a realistic caption for these emoji + text labels at the overlay's font.
            const float advance = 100f;
            const float available = 900f;

            int tabs = PlayerDebugEditorOverlay.TabCount;
            Assert.That(tabs, Is.GreaterThan(1),
                "TabNames must hold more than one tab or this fixture tests nothing.");

            var widths = new float[tabs];
            for (int i = 0; i < tabs; i++) widths[i] = advance;

            int[] rowStarts = TabStripLayout.RowStarts(widths, available);

            // The scenario is only interesting while the shipped strip really is too wide for one row.
            // If this ever fails, the premise changed (far fewer tabs) — not the arithmetic.
            Assert.That(rowStarts.Length, Is.GreaterThan(1),
                $"{tabs} tabs at a realistic caption must wrap.");
            Assert.That(rowStarts[0], Is.EqualTo(0),
                "…and the first row must still start at tab 0.");

            // Every tab is on some row: the row starts partition [0, tabCount).
            var onARow = new bool[tabs];
            for (int r = 0; r < rowStarts.Length; r++)
            {
                int from = rowStarts[r];
                int to = r + 1 < rowStarts.Length ? rowStarts[r + 1] : tabs;
                for (int i = from; i < to; i++) onARow[i] = true;
            }

            for (int i = 0; i < tabs; i++)
            {
                Assert.That(onARow[i], Is.True, $"Tab {i} is on no row — it would be invisible and unclickable.");
            }

            // The last tab is the one a fixed strip pushes off the edge, so it is the one that must be
            // proven to be on a row AFTER the first.
            Assert.That(rowStarts[rowStarts.Length - 1], Is.GreaterThan(0),
                $"Tab {tabs - 1} ({PlayerDebugEditorOverlay.TabCount} shipped) must wrap onto a later row, "
                + "not sit past the right edge.");
            Assert.That(TabStripLayout.Wraps(widths, available), Is.True);
        }

        [Test]
        public void Wraps_IsFalseForASingleRow()
        {
            Assert.That(TabStripLayout.Wraps(new[] { 10f, 10f }, 100f), Is.False);
        }

        [Test]
        public void Wraps_IsTrueWhenARowIsBroken()
        {
            Assert.That(TabStripLayout.Wraps(new[] { 100f, 100f }, 150f), Is.True);
        }
    }
}

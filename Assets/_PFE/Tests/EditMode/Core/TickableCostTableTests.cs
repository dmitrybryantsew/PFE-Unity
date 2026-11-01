using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Profiling;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The arithmetic behind the per-tickable table.
    ///
    /// <para><b>Why a table needs its own tests.</b> The table is the instrument the PlayMode perf
    /// fixture reads to decide "where to dig next", and an instrument that silently mis-groups or
    /// mis-scales produces a ranking that looks perfectly reasonable and points at the wrong system.
    /// These tests pin the three things a reader has to trust: grouping by type, the ms conversion, and
    /// the fact that a mid-sample rebuild is *reported* rather than quietly zeroing the totals.</para>
    ///
    /// <para>The PlayMode fixture's reconciliation assertion (<c>UnaccountedFraction</c>) is the other
    /// half: it checks the table against a wall clock it does not control. This file checks the table
    /// against arithmetic.</para>
    /// </summary>
    public class TickableCostTableTests
    {
        /// <summary>Raw stopwatch ticks that correspond to exactly one millisecond.</summary>
        private static long OneMs => Stopwatch.Frequency / 1000;

        [Test]
        public void Note_GroupsInstancesOfTheSameTypeIntoOneRow()
        {
            var table = new TickableCostTable { Enabled = true };
            table.Rebuild(new[] { "UnitController", "UnitController", "EnemyBrain" });

            table.Note(0, OneMs);
            table.Note(1, OneMs * 2);
            table.Note(2, OneMs * 3);

            List<TickableCostTable.Entry> rows = table.Snapshot();

            Assert.AreEqual(2, rows.Count, "two distinct type names must produce two rows, not three");

            // Descending by total: EnemyBrain (3 ms) first, UnitController (3 ms across two instances)
            // second — the sort is by total, and 3 == 3 is a tie, so assert on the values rather than
            // on the order.
            TickableCostTable.Entry units = Find(rows, "UnitController");
            Assert.AreEqual(2, units.Instances, "two instances of one type must be counted as two");
            Assert.AreEqual(2, units.Calls);
            Assert.AreEqual(3.0, units.TotalMs, 0.01, "1 ms + 2 ms");
            Assert.AreEqual(1.5, units.AverageMs, 0.01, "average is per CALL, not per instance");
            Assert.AreEqual(2.0, units.MaxMs, 0.01, "the worst single call, not the sum");
        }

        [Test]
        public void Reset_ClearsTotalsButKeepsTheRegistration()
        {
            var table = new TickableCostTable { Enabled = true };
            table.Rebuild(new[] { "A" });
            table.Note(0, OneMs * 5);

            Assert.AreEqual(1, table.Calls);

            table.Reset();

            Assert.AreEqual(0, table.Calls, "reset must clear the call count");
            Assert.AreEqual(0.0, table.TotalMs, 0.0001);
            Assert.AreEqual(1, table.RowCount, "reset must not drop the rows — that is Rebuild's job");
        }

        [Test]
        public void Rebuild_AfterSamplesAreRecorded_IsReportedAsInvalidated()
        {
            var table = new TickableCostTable { Enabled = true };
            table.Rebuild(new[] { "A", "B" });
            table.Note(0, OneMs);
            table.Note(1, OneMs);

            Assert.IsFalse(table.InvalidatedSinceReset, "nothing has changed yet");

            // A registration change mid-sample. The rows are index-aligned with a dispatch order that
            // just moved, so the totals cannot be carried across — they are dropped, and the drop must
            // be visible, because "the totals went to zero" otherwise reads as "the tick got cheap".
            table.Rebuild(new[] { "B", "A", "C" });

            Assert.IsTrue(table.InvalidatedSinceReset,
                "a rebuild after sampling began must be reported, not swallowed");
            Assert.AreEqual(0, table.Calls, "the old totals cannot survive a reindex");
            Assert.AreEqual(3, table.RowCount);

            string report = table.BuildReport(0.0, 1);
            StringAssert.Contains("registration changed", report,
                "the report must carry the warning, or the reader acts on half a sample");
        }

        [Test]
        public void Rebuild_BeforeAnySample_IsNotAnInvalidation()
        {
            var table = new TickableCostTable { Enabled = true };

            // The normal startup path: SimLoop rebuilds the rows on the first tick, before anything is
            // recorded. If that counted as an invalidation every report would carry the warning.
            table.Rebuild(new[] { "A" });
            table.Rebuild(new[] { "A", "B" });

            Assert.IsFalse(table.InvalidatedSinceReset);
        }

        [Test]
        public void UnaccountedFraction_IsTheResidualOverTheLoopTotal()
        {
            var table = new TickableCostTable { Enabled = true };
            table.Rebuild(new[] { "A" });
            table.Note(0, OneMs * 90);

            // 90 ms accounted against a 100 ms dispatch loop: 10 % of the loop is not in any tickable.
            Assert.AreEqual(0.10, table.UnaccountedFraction(100.0), 0.001);

            // A caller that measured less than the table recorded is a caller bug, not a negative
            // residual — it must read 0, never a number that could be mistaken for headroom.
            Assert.AreEqual(0.0, table.UnaccountedFraction(50.0), 0.0001);

            // No loop total means no fraction; dividing by it would be a NaN in a report.
            Assert.AreEqual(0.0, table.UnaccountedFraction(0.0), 0.0001);
        }

        [Test]
        public void Note_WithAStaleIndex_DropsTheSampleInsteadOfChargingAnotherRow()
        {
            var table = new TickableCostTable { Enabled = true };
            table.Rebuild(new[] { "A" });

            table.Note(0, OneMs);
            table.Note(7, OneMs * 4);   // out of range — a reindex that Rebuild has not seen yet

            Assert.AreEqual(1, table.Calls, "the stale sample must not be recorded");
            Assert.AreEqual(1.0, table.TotalMs, 0.01, "and it must not be charged to row 0 either");
        }

        private static TickableCostTable.Entry Find(List<TickableCostTable.Entry> rows, string name)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Name == name) return rows[i];
            }

            Assert.Fail($"no row named '{name}' — rows were: {string.Join(", ", rows.ConvertAll(r => r.Name))}");
            return default;
        }
    }
}

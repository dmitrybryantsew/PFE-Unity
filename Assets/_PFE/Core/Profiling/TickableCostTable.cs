// Per-tickable cost aggregation for SimLoop.
//
// WHY THIS EXISTS
// `sim.tick` is one PfeProfiler region wrapping a `for` over every registered ISimTickable. On
// 2026-10-26 that region reported 9.8 ms of SELF time per tick against a 13.4 ms total — i.e. 73 %
// of the tick sat inside one of ~50 tickables, and the region could only say "inside one of them".
// The obvious fix — one `PfeProfiler.Region` per tickable — is not allowed: a region id must be a
// static string (PfeProfiler.cs rule), and 50 ids per tickable instance would grow the registry
// without bound and make the report unreadable.
//
// So the breakdown is collected here instead, keyed by the tickable's TYPE NAME. That is a fixed,
// small set (one row per class, not per instance), so it can be aggregated cheaply and printed as a
// ranked table.
//
// COST
// Two `Stopwatch.GetTimestamp()` calls per tickable per tick, plus one array write. With 77 tickables
// at 30 Hz that is ~4,600 timestamp reads per second against a 13 ms tick — noise. It is still
// switched OFF by default and only ever enabled by a measurement driver (a test, or a console verb);
// `Note` is not reached at all while `Enabled` is false, because SimLoop skips the timing call site.
//
// WHAT IT DOES NOT DO
// It measures each tickable's WHOLE span, including any nested `PfeProfiler` regions inside it. A
// tickable that is expensive only because it wraps something else will look expensive here. Cross-read
// against the region table (same dump) before optimising: a row whose cost is fully covered by a child
// region is a wrapper, not a hotspot.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PFE.Core.Profiling
{
    /// <summary>
    /// Aggregates time spent inside each <c>ISimTickable</c>, grouped by type name, and renders the
    /// ranked table. Owned by <see cref="PFE.Core.SimLoop"/>; enable it from a measurement driver.
    /// </summary>
    public sealed class TickableCostTable
    {
        /// <summary>One tickable instance's running totals. Rebuilt when the registration changes.</summary>
        private struct Row
        {
            public string Name;
            public int Calls;
            public long Ticks;
            public long MaxTicks;
        }

        /// <summary>
        /// One type's totals across every instance of it — what the report prints.
        /// </summary>
        public readonly struct Entry
        {
            public Entry(string name, int instances, int calls, double totalMs, double maxMs)
            {
                Name = name;
                Instances = instances;
                Calls = calls;
                TotalMs = totalMs;
                MaxMs = maxMs;
            }

            /// <summary>Tickable type name, e.g. <c>UnitController</c>.</summary>
            public string Name { get; }

            /// <summary>How many instances of this type are registered.</summary>
            public int Instances { get; }

            /// <summary>Total <c>SimTick</c> calls recorded for this type.</summary>
            public int Calls { get; }

            /// <summary>Total milliseconds spent in this type.</summary>
            public double TotalMs { get; }

            /// <summary>Slowest single call seen, in milliseconds.</summary>
            public double MaxMs { get; }

            /// <summary>
            /// Mean cost of one call, in milliseconds. Each instance calls once per tick, so this is
            /// also "what one instance of this type costs per tick" — the number that separates
            /// "27 units are slow" from "one unit is slow".
            /// </summary>
            public double AverageMs => Calls > 0 ? TotalMs / Calls : 0.0;
        }

        private Row[] _rows = Array.Empty<Row>();
        private double _totalMs;
        private int _calls;
        private bool _invalidated;

        /// <summary>
        /// Turns collection on. False by default: a disabled table costs one branch at the call site
        /// and never reaches <see cref="Note"/>.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Rows are per registered tickable instance, so this equals the tickable count.</summary>
        public int RowCount => _rows.Length;

        /// <summary>Summed milliseconds over every recorded call since the last <see cref="Reset"/>.</summary>
        public double TotalMs => _totalMs;

        /// <summary>Summed <c>SimTick</c> calls since the last <see cref="Reset"/>.</summary>
        public int Calls => _calls;

        /// <summary>
        /// True when the tickable registration changed after samples had been recorded, i.e. rows were
        /// rebuilt mid-sample and the totals before that point are gone.
        ///
        /// <para>This exists because <see cref="Rebuild"/> has to zero the rows — the array is
        /// index-aligned with a dispatch order that just changed, so carrying totals across would
        /// charge one type's time to another. The consequence is silent data loss, which reads exactly
        /// like "the tick got cheap". Printing the flag is the difference between the two.</para>
        /// </summary>
        public bool InvalidatedSinceReset => _invalidated;

        /// <summary>
        /// Rebuilds the row array for a new registration. Called from <c>SimLoop.EnsureOrder</c>, i.e.
        /// only when the tickable list actually changed — never per tick.
        /// </summary>
        /// <param name="names">Type name per registered tickable, index-aligned with the dispatch order.</param>
        public void Rebuild(string[] names)
        {
            int count = names?.Length ?? 0;

            // Samples recorded before this point cannot be re-attributed to the new index order.
            if (_calls > 0) _invalidated = true;

            if (_rows.Length != count)
            {
                _rows = count == 0 ? Array.Empty<Row>() : new Row[count];
            }

            for (int i = 0; i < count; i++)
            {
                _rows[i].Name = names[i];
                _rows[i].Calls = 0;
                _rows[i].Ticks = 0;
                _rows[i].MaxTicks = 0;
            }

            _totalMs = 0.0;
            _calls = 0;
        }

        /// <summary>
        /// Records one <c>SimTick</c> call. Called only while <see cref="Enabled"/>.
        /// </summary>
        /// <param name="index">Row index, index-aligned with the dispatch order.</param>
        /// <param name="elapsedTicks">Raw <c>Stopwatch</c> ticks the call took.</param>
        public void Note(int index, long elapsedTicks)
        {
            if ((uint)index >= (uint)_rows.Length)
            {
                // A stale index means the rows and the dispatch list disagreed — which would be a bug
                // in EnsureOrder, not in the caller. Dropping the sample keeps the report honest
                // (the residual in BuildReport grows) instead of writing to another tickable's row.
                return;
            }

            if (elapsedTicks < 0) elapsedTicks = 0;

            ref Row row = ref _rows[index];
            row.Calls++;
            row.Ticks += elapsedTicks;
            if (elapsedTicks > row.MaxTicks) row.MaxTicks = elapsedTicks;

            _totalMs += ToMs(elapsedTicks);
            _calls++;
        }

        /// <summary>Clears every total but keeps the registration and the <see cref="Enabled"/> flag.</summary>
        public void Reset()
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i].Calls = 0;
                _rows[i].Ticks = 0;
                _rows[i].MaxTicks = 0;
            }

            _totalMs = 0.0;
            _calls = 0;

            // Reset starts a new measurement, so an earlier invalidation no longer applies.
            _invalidated = false;
        }

        /// <summary>
        /// Grouped, descending-by-total snapshot. Allocates, so call it from a report writer and never
        /// from a tick.
        /// </summary>
        public List<Entry> Snapshot()
        {
            var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);

            for (int i = 0; i < _rows.Length; i++)
            {
                ref Row row = ref _rows[i];
                if (row.Name == null) continue;

                double totalMs = ToMs(row.Ticks);
                double maxMs = ToMs(row.MaxTicks);

                if (byName.TryGetValue(row.Name, out Entry existing))
                {
                    byName[row.Name] = new Entry(
                        row.Name,
                        existing.Instances + 1,
                        existing.Calls + row.Calls,
                        existing.TotalMs + totalMs,
                        maxMs > existing.MaxMs ? maxMs : existing.MaxMs);
                }
                else
                {
                    byName[row.Name] = new Entry(row.Name, 1, row.Calls, totalMs, maxMs);
                }
            }

            var list = new List<Entry>(byName.Values);
            list.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            return list;
        }

        /// <summary>
        /// The ranked table, plus the reconciliation line that says whether the table can be trusted.
        ///
        /// <para><b>The reconciliation is the point.</b> <paramref name="tickTotalMs"/> is measured by
        /// the caller over the same span the rows were recorded in (SimLoop times the whole dispatch
        /// loop). If the rows do not add up to it, the table is missing or double-counting tickables and
        /// its ranking must not be acted on — which is a different conclusion from "the tick is cheap",
        /// and the only way to tell them apart is to print the residual.</para>
        /// </summary>
        /// <param name="tickTotalMs">Wall time the caller measured for the dispatch loop itself.</param>
        /// <param name="frames">Frames the sample covers, for the per-frame figure.</param>
        /// <param name="top">How many rows to print.</param>
        public string BuildReport(double tickTotalMs, int frames, int top = 24)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- tickables (ms, grouped by type; total = whole SimTick span) --");

            if (_invalidated)
            {
                sb.AppendLine("  !! the tickable registration changed after sampling began — rows were rebuilt and");
                sb.AppendLine("  !! every total below belongs only to the period SINCE that change. Re-run with a");
                sb.AppendLine("  !! settled world, or reset after the change, before comparing anything.");
            }

            sb.AppendLine("  type                                inst   calls      total        avg        max");

            List<Entry> rows = Snapshot();
            int shown = 0;
            for (int i = 0; i < rows.Count && shown < top; i++)
            {
                Entry e = rows[i];
                sb.Append("  ").Append(e.Name.PadRight(34))
                  .Append(e.Instances.ToString(Inv).PadLeft(5))
                  .Append(e.Calls.ToString(Inv).PadLeft(8))
                  .Append(e.TotalMs.ToString("F1", Inv).PadLeft(11))
                  .Append(e.AverageMs.ToString("F3", Inv).PadLeft(11))
                  .Append(e.MaxMs.ToString("F3", Inv).PadLeft(11))
                  .AppendLine();
                shown++;
            }

            if (shown == 0)
            {
                sb.AppendLine("  (no calls recorded — was TickableCosts.Enabled set before the sample?)");
            }

            double residual = tickTotalMs - _totalMs;
            double share = tickTotalMs > 1e-9 ? _totalMs / tickTotalMs * 100.0 : 0.0;

            sb.AppendLine();
            sb.Append("  accounted        ").Append(_totalMs.ToString("F1", Inv).PadLeft(10)).Append(" ms  (")
              .Append(share.ToString("F1", Inv)).Append(" % of the dispatch loop)").AppendLine();
            sb.Append("  loop overhead    ").Append(residual.ToString("F1", Inv).PadLeft(10)).Append(" ms  (")
              .Append((100.0 - share).ToString("F1", Inv)).Append(" %)").AppendLine();
            sb.Append("  dispatch total   ").Append(tickTotalMs.ToString("F1", Inv).PadLeft(10)).Append(" ms  over ")
              .Append(_calls.ToString(Inv)).Append(" calls in ").Append(frames.ToString(Inv)).Append(" frames")
              .AppendLine();

            if (frames > 0)
            {
                sb.Append("  per frame        ").Append((tickTotalMs / frames).ToString("F3", Inv))
                  .Append(" ms of dispatch across ").Append((_calls / (double)frames).ToString("F2", Inv))
                  .Append(" calls/frame").AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Fraction of the dispatch loop NOT attributed to any tickable. 0.0 when the loop total is 0.
        /// The PlayMode fixture asserts this stays small: a large residual means the instrument is
        /// wrong, and a wrong instrument reads exactly like a fast tick.
        /// </summary>
        public double UnaccountedFraction(double tickTotalMs)
        {
            if (tickTotalMs <= 1e-9) return 0.0;
            double residual = tickTotalMs - _totalMs;
            return residual <= 0 ? 0.0 : residual / tickTotalMs;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}

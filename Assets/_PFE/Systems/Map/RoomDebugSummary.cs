using System;
using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The counting rules behind the F2 overlay's <b>Room</b> tab: "what is in this room, and how much
    /// of each kind".
    ///
    /// <para><b>Why this is a separate, engine-free class.</b> The tab's whole job is a decision table —
    /// which id names a row, what an empty id counts as, and what order the rows come out in — and a
    /// decision table buried in a <c>Draw*Tab</c> method can only be checked by looking at a screenshot.
    /// Here it takes a plain sequence of id strings and returns plain structs, so the whole table runs on
    /// the offline wall (no <c>UnityEngine</c> type appears anywhere below) and the tab is left
    /// untestable-but-obvious, which is the right shape.</para>
    ///
    /// <para><b>It deliberately knows nothing about <c>UnitInstance</c> or <c>ObjectInstance</c>.</b> Both
    /// carry more than an id, but the tab only ever needs "group these by a string and count them" — and
    /// a signature that took the domain types would make this class untestable offline while buying
    /// nothing. Callers project the id out (<c>u =&gt; u.unitId</c>,
    /// <c>o =&gt; o.GetResolvedDefinitionId()</c>) and pass the sequence.</para>
    /// </summary>
    public static class RoomDebugSummary
    {
        /// <summary>
        /// The label an absent id is counted under. A row for the empty string is unusable in a readout —
        /// it looks like a rendering fault — and the count itself is real information (a unit or object
        /// that reached the room without an id), so it is named rather than dropped.
        /// </summary>
        public const string UnnamedId = "(unnamed)";

        /// <summary>One row of a grouped count.</summary>
        public readonly struct IdCount
        {
            /// <summary>The id, or <see cref="UnnamedId"/> when the source was null/blank.</summary>
            public readonly string Id;

            /// <summary>How many items carried that id.</summary>
            public readonly int Count;

            public IdCount(string id, int count)
            {
                Id = id;
                Count = count;
            }
        }

        /// <summary>
        /// Count occurrences of each id, ordered by <b>count descending, then id ordinal ascending</b>.
        ///
        /// <para><b>The order is part of the contract, not a presentation detail.</b> A per-frame readout
        /// that reshuffles between repaints cannot be read while playing, and a dictionary's enumeration
        /// order is not stable across insertions — so the sort is explicit, and the id tie-break is
        /// <see cref="string.CompareOrdinal(string,string)"/> rather than a culture comparison, which
        /// would order the same two ids differently on a machine with a different locale.</para>
        /// </summary>
        public static List<IdCount> GroupCounts(IEnumerable<string> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            if (ids != null)
            {
                foreach (string raw in ids)
                {
                    string id = string.IsNullOrWhiteSpace(raw) ? UnnamedId : raw;
                    counts.TryGetValue(id, out int existing);
                    counts[id] = existing + 1;
                }
            }

            var rows = new List<IdCount>(counts.Count);
            foreach (KeyValuePair<string, int> pair in counts)
            {
                rows.Add(new IdCount(pair.Key, pair.Value));
            }

            rows.Sort(static (a, b) =>
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Id, b.Id);
            });

            return rows;
        }

        /// <summary>
        /// The ids of one group, joined for a single-line readout: <c>"raider ×3, robot ×1"</c>.
        ///
        /// <para>Kept here so the separator and the multiplication sign are one decision rather than three
        /// that can drift, and so the "×" cannot become an ASCII <c>x</c> in one place and a real
        /// multiplication sign in another.</para>
        /// </summary>
        public static string Format(IReadOnlyList<IdCount> rows, int maxRows)
        {
            if (rows == null || rows.Count == 0)
            {
                return "none";
            }

            int shown = maxRows <= 0 || maxRows > rows.Count ? rows.Count : maxRows;

            var parts = new List<string>(shown + 1);
            for (int i = 0; i < shown; i++)
            {
                parts.Add(rows[i].Count > 1 ? $"{rows[i].Id} \u00d7{rows[i].Count}" : rows[i].Id);
            }

            if (shown < rows.Count)
            {
                parts.Add($"+{rows.Count - shown} more");
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// One <c>kolEn</c> group's <b>budget</b> (what the land's difficulty asked for) against the
        /// room's <b>capacity</b> (how many points the room's author actually gave it).
        /// </summary>
        public readonly struct GroupCapacity
        {
            /// <summary>One of <c>enl1</c>/<c>enl2</c>/<c>enf1</c>/<c>enc1</c>/<c>lov</c>.</summary>
            public readonly string GroupId;

            /// <summary>AS3 <c>kolEn[i]</c> — the budget, drawn from the land's difficulty alone.</summary>
            public readonly int Budget;

            /// <summary>AS3 <c>ups[i].length</c> — the authored enemy points for this group.</summary>
            public readonly int Capacity;

            /// <summary>
            /// <c>min(Budget, Capacity)</c> — how many of this group AS3 can actually place
            /// (<c>Location.as:1058-1093</c> stops early once the points run out).
            /// </summary>
            public readonly int Placeable;

            /// <summary>
            /// The generator asked for this group and the room has nowhere to put it. <b>This is the
            /// reading the budget alone cannot give you</b>, and the one that separates "authored with no
            /// enemy points, on purpose" from "the population pass never ran".
            /// </summary>
            public readonly bool BudgetWithNoPoints;

            public GroupCapacity(string groupId, int budget, int capacity)
            {
                GroupId = groupId;

                // Clamped rather than trusted. `enemyCounts` is only written by SetEnemyCount, and a
                // negative would make min() report a negative placeable count — a number that reads as
                // corruption rather than as a missing value.
                int safeBudget = budget > 0 ? budget : 0;
                int safeCapacity = capacity > 0 ? capacity : 0;

                Budget = safeBudget;
                Capacity = safeCapacity;
                Placeable = safeBudget < safeCapacity ? safeBudget : safeCapacity;
                BudgetWithNoPoints = safeBudget > 0 && safeCapacity == 0;
            }
        }

        /// <summary>
        /// Pair the difficulty record's <c>kolEn</c> budget with the room's authored point capacity, one
        /// row per group, <b>in <c>kolEn</c> index order</b>.
        ///
        /// <para><b>This order deliberately differs from <see cref="GroupCounts"/>' count-descending
        /// order.</b> That method groups an open-ended set of ids and is read as a ranking; this one is a
        /// fixed five-slot table that must line up with the <c>enl1..lov</c> line the record prints above
        /// it, so a row that moved would silently mis-pair a budget with the wrong capacity. Index 0 is
        /// skipped for the same reason the oracle's <c>setRandomUnits</c> skips it: <c>tipEn[0]</c> is the
        /// empty string and <c>ups[0]</c> is the discard slot.</para>
        ///
        /// <para>Both inputs may be null or short; a missing slot reads as <c>0</c>, which is the honest
        /// answer for "no budget" and for "no capacity" alike, and never as a crash.</para>
        /// </summary>
        public static List<GroupCapacity> GroupCapacities(
            IReadOnlyList<int> budgets, IReadOnlyList<int> capacities)
        {
            var rows = new List<GroupCapacity>(LocationEnemySelector.GroupCount - 1);

            for (int i = 1; i < LocationEnemySelector.GroupCount; i++)
            {
                int budget = budgets != null && i < budgets.Count ? budgets[i] : 0;
                int capacity = capacities != null && i < capacities.Count ? capacities[i] : 0;
                rows.Add(new GroupCapacity(LocationEnemySelector.GroupIds[i], budget, capacity));
            }

            return rows;
        }

        /// <summary>
        /// Sum of <see cref="GroupCapacity.Placeable"/> — how many enemies the random-enemy pass can
        /// place in this room. <b>This, not the budget total, is the room's intended garrison.</b>
        /// </summary>
        public static int PlaceableTotal(IReadOnlyList<GroupCapacity> rows)
        {
            if (rows == null) return 0;

            int total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Placeable;
            }

            return total;
        }

        /// <summary>
        /// Sum of <see cref="GroupCapacity.Capacity"/> — how many enemy points the room authored in
        /// total. <b>The discriminator an empty room needs:</b> with no points the budget had nothing to
        /// spend on and the emptiness is authored data; with points and no units, the pass failed.
        /// </summary>
        public static int CapacityTotal(IReadOnlyList<GroupCapacity> rows)
        {
            if (rows == null) return 0;

            int total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Capacity;
            }

            return total;
        }

        /// <summary>How many groups have a budget but no points — the groups that cannot place anything.</summary>
        public static int CountBudgetWithoutPoints(IReadOnlyList<GroupCapacity> rows)
        {
            if (rows == null) return 0;

            int count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].BudgetWithNoPoints) count++;
            }

            return count;
        }

        /// <summary>
        /// The per-group line for the readout: <c>"enl1=1/0   enl2=1/0   …"</c>, each entry being
        /// <c>budget/points</c>. A single token per group rather than two parallel lines, because two
        /// lines can be read out of step with each other and this one cannot.
        /// </summary>
        public static string FormatCapacity(IReadOnlyList<GroupCapacity> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                parts.Add($"{rows[i].GroupId}={rows[i].Budget}/{rows[i].Capacity}");
            }

            return string.Join("   ", parts);
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.Scripting
{
    /// <summary>
    /// The counting rules behind the F2 overlay's <b>Room</b> tab (<see cref="RoomDebugSummary"/>).
    /// <b>Unity-free</b> — it takes sequences of id strings and returns plain structs — so these run
    /// offline through the wall harness as well as in the editor, which is the reason the grouping was
    /// pulled out of the <c>DrawRoomTab</c> method in the first place.
    ///
    /// <para>Two properties are load-bearing and are pinned explicitly, because both are the kind that
    /// fails silently: the <b>order</b> (a per-frame readout that reshuffles between repaints cannot be
    /// read while playing) and the <b>blank-id bucket</b> (dropping an id-less entry under-reports the
    /// room, which is a wrong answer, not a missing one).</para>
    /// </summary>
    [TestFixture]
    public sealed class RoomDebugSummaryTests
    {
        // ── GroupCounts: counting ───────────────────────────────────────────────

        [Test]
        public void GroupCounts_CountsEachIdOncePerOccurrence()
        {
            List<RoomDebugSummary.IdCount> rows =
                RoomDebugSummary.GroupCounts(new[] { "raider", "raider", "robot" });

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0].Id, Is.EqualTo("raider"));
            Assert.That(rows[0].Count, Is.EqualTo(2));
            Assert.That(rows[1].Id, Is.EqualTo("robot"));
            Assert.That(rows[1].Count, Is.EqualTo(1));
        }

        [Test]
        public void GroupCounts_OrdersByCountDescending()
        {
            // Unequal counts on purpose: with all-equal counts the ordering assertion cannot fail, and a
            // test that cannot fail is not a test. "robot" appears first in the input but least often, so
            // it must be sorted last.
            List<RoomDebugSummary.IdCount> rows =
                RoomDebugSummary.GroupCounts(new[] { "robot", "a", "a", "a", "b", "b" });

            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].Id, Is.EqualTo("a"));
            Assert.That(rows[0].Count, Is.EqualTo(3));
            Assert.That(rows[1].Id, Is.EqualTo("b"));
            Assert.That(rows[1].Count, Is.EqualTo(2));
            Assert.That(rows[2].Id, Is.EqualTo("robot"));
            Assert.That(rows[2].Count, Is.EqualTo(1));

            for (int i = 1; i < rows.Count; i++)
            {
                Assert.That(rows[i - 1].Count, Is.GreaterThanOrEqualTo(rows[i].Count),
                    "counts must be non-increasing");
            }
        }

        [Test]
        public void GroupCounts_TieBreaksByIdOrdinalAscending()
        {
            // Equal counts, so the tie-break decides — and it must be ordinal, not insertion order.
            List<RoomDebugSummary.IdCount> rows =
                RoomDebugSummary.GroupCounts(new[] { "zebra", "ant", "zebra", "ant" });

            Assert.That(rows[0].Id, Is.EqualTo("ant"));
            Assert.That(rows[0].Count, Is.EqualTo(2));
            Assert.That(rows[1].Id, Is.EqualTo("zebra"));
            Assert.That(rows[1].Count, Is.EqualTo(2));
        }

        [Test]
        public void GroupCounts_BlankAndNullIdsCountUnderUnnamed()
        {
            // The count is real information (something reached the room without an id) and must not be
            // silently dropped just because the label would be empty.
            List<RoomDebugSummary.IdCount> rows =
                RoomDebugSummary.GroupCounts(new[] { null, "", "   ", "raider" });

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0].Id, Is.EqualTo(RoomDebugSummary.UnnamedId));
            Assert.That(rows[0].Count, Is.EqualTo(3));
            Assert.That(rows[1].Id, Is.EqualTo("raider"));
            Assert.That(rows[1].Count, Is.EqualTo(1));

            // The bucket must be USABLE as a row label — an empty one is the very thing the constant
            // exists to prevent, and asserting only against the constant would make this test blind to a
            // constant that was itself set to the empty string (the assertion and the value would move
            // together). The spelling is deliberately not pinned; emptiness is.
            Assert.That(rows[0].Id, Is.Not.Empty,
                "the unnamed bucket must render as something; an empty label is a row that looks broken");
            Assert.That(rows[0].Id, Is.Not.EqualTo("raider"),
                "…and it must not collide with a real id");
        }

        [Test]
        public void GroupCounts_EmptyAndNullInputsAreEmpty()
        {
            Assert.That(RoomDebugSummary.GroupCounts(new string[0]), Is.Empty);
            Assert.That(RoomDebugSummary.GroupCounts(null), Is.Empty);
        }

        // ── Format: one-line rendering ──────────────────────────────────────────

        [Test]
        public void Format_OmitsTheMultiplierForASingleOccurrence()
        {
            var rows = new List<RoomDebugSummary.IdCount>
            {
                new RoomDebugSummary.IdCount("raider", 3),
                new RoomDebugSummary.IdCount("robot", 1)
            };

            Assert.That(RoomDebugSummary.Format(rows, 8), Is.EqualTo("raider \u00d73, robot"));
        }

        [Test]
        public void Format_TruncatesAndSaysHowManyWereLeft()
        {
            // A truncated list must announce the truncation — otherwise it reads as "that is all of them".
            var rows = new List<RoomDebugSummary.IdCount>
            {
                new RoomDebugSummary.IdCount("a", 2),
                new RoomDebugSummary.IdCount("b", 2),
                new RoomDebugSummary.IdCount("c", 1),
                new RoomDebugSummary.IdCount("d", 1)
            };

            Assert.That(RoomDebugSummary.Format(rows, 2), Is.EqualTo("a \u00d72, b \u00d72, +2 more"));
        }

        [Test]
        public void Format_NonPositiveMaxRowsMeansNoCap()
        {
            var rows = new List<RoomDebugSummary.IdCount>
            {
                new RoomDebugSummary.IdCount("a", 2),
                new RoomDebugSummary.IdCount("b", 2),
                new RoomDebugSummary.IdCount("c", 1)
            };

            Assert.That(RoomDebugSummary.Format(rows, 0), Is.EqualTo("a \u00d72, b \u00d72, c"));
        }

        [Test]
        public void Format_EmptyIsNone()
        {
            Assert.That(RoomDebugSummary.Format(new List<RoomDebugSummary.IdCount>(), 8), Is.EqualTo("none"));
            Assert.That(RoomDebugSummary.Format(null, 8), Is.EqualTo("none"));
        }

        // ── GroupCapacities: the budget against the room's authored points ──────
        //
        // This is the table that separates "authored with no enemy points" from "the population pass
        // never ran". The budget alone cannot do it: `Land.setLocDif` draws kolEn from the LAND's
        // difficulty and never consults the room, so a healthy-looking budget is the NORMAL case even
        // for a room that authors nothing.

        /// <summary>
        /// The real case this table was written for. <c>rooms_plant/яма</c> resolves to
        /// <c>enl1=1 enl2=1 enf1=1 enc1=9 lov=5</c> — a budget of 17 — against <b>zero</b> authored enemy
        /// points. AS3 places <c>min(budget, points)</c> per group (<c>Location.as:1060</c>), so the
        /// room's intended garrison is 0, not 17. The overlay printed the 17 as "total intended enemies",
        /// which reads as a failed population pass rather than as authored data.
        /// </summary>
        [Test]
        public void GroupCapacities_TheYamaShape_IsAStarvedRoomNotA17EnemyRoom()
        {
            int[] budget = { 0, 1, 1, 1, 9, 5 };
            int[] points = { 0, 0, 0, 0, 0, 0 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(rows.Count, Is.EqualTo(5));
            Assert.That(RoomDebugSummary.PlaceableTotal(rows), Is.EqualTo(0),
                "a budget with no points places nothing");
            Assert.That(RoomDebugSummary.CapacityTotal(rows), Is.EqualTo(0));
            Assert.That(RoomDebugSummary.CountBudgetWithoutPoints(rows), Is.EqualTo(5));
        }

        [Test]
        public void GroupCapacities_PlaceableIsTheMinOfBudgetAndPoints()
        {
            // Every group is chosen so both sides of the min get exercised — a budget-bound group, a
            // capacity-bound group, and an exact tie. With all groups on the same side, min() and max()
            // would be indistinguishable and the assertion could not fail.
            int[] budget = { 0, 2, 9, 5, 4, 1 };
            int[] points = { 0, 7, 3, 5, 0, 6 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(rows[0].GroupId, Is.EqualTo("enl1"));
            Assert.That(rows[0].Placeable, Is.EqualTo(2), "budget-bound");

            Assert.That(rows[1].GroupId, Is.EqualTo("enl2"));
            Assert.That(rows[1].Placeable, Is.EqualTo(3), "capacity-bound");

            Assert.That(rows[2].GroupId, Is.EqualTo("enf1"));
            Assert.That(rows[2].Placeable, Is.EqualTo(5), "exact tie");

            Assert.That(rows[3].GroupId, Is.EqualTo("enc1"));
            Assert.That(rows[3].Placeable, Is.EqualTo(0), "points but no budget places nothing");

            Assert.That(rows[4].GroupId, Is.EqualTo("lov"));
            Assert.That(rows[4].Placeable, Is.EqualTo(1));

            Assert.That(RoomDebugSummary.PlaceableTotal(rows), Is.EqualTo(11));
            Assert.That(RoomDebugSummary.CapacityTotal(rows), Is.EqualTo(21));
        }

        [Test]
        public void GroupCapacities_AreOrderedByKolEnIndexNotByCount()
        {
            // The order is part of the contract and must NOT match GroupCounts' count-descending sort:
            // this table is read against the `enl1..lov` line the record prints above it, so a row that
            // moved would pair one group's budget with another group's capacity. The budgets here ascend
            // while the ids are in kolEn order, so a count sort would reverse them.
            int[] budget = { 0, 1, 2, 3, 9, 8 };
            int[] points = { 0, 0, 0, 0, 0, 0 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(rows[0].GroupId, Is.EqualTo("enl1"));
            Assert.That(rows[1].GroupId, Is.EqualTo("enl2"));
            Assert.That(rows[2].GroupId, Is.EqualTo("enf1"));
            Assert.That(rows[3].GroupId, Is.EqualTo("enc1"));
            Assert.That(rows[4].GroupId, Is.EqualTo("lov"));
        }

        [Test]
        public void GroupCapacities_NullAndShortInputsReadAsZero()
        {
            // `upPointCounts` is [NonSerialized], so a deserialised room hands over null; and a record
            // from before the field existed can hand over a short array. Neither may throw, and neither
            // may be reported as "has capacity" — an array that silently claimed every group has points
            // is the one wrong answer this field exists to prevent.
            List<RoomDebugSummary.GroupCapacity> fromNull =
                RoomDebugSummary.GroupCapacities(null, null);

            Assert.That(fromNull.Count, Is.EqualTo(5));
            Assert.That(RoomDebugSummary.CapacityTotal(fromNull), Is.EqualTo(0));
            Assert.That(RoomDebugSummary.PlaceableTotal(fromNull), Is.EqualTo(0));

            List<RoomDebugSummary.GroupCapacity> fromShort =
                RoomDebugSummary.GroupCapacities(new[] { 0, 4 }, new[] { 0, 2 });

            Assert.That(fromShort.Count, Is.EqualTo(5), "the table is always the full five slots");
            Assert.That(fromShort[0].Budget, Is.EqualTo(4));
            Assert.That(fromShort[0].Capacity, Is.EqualTo(2));
            Assert.That(fromShort[1].Budget, Is.EqualTo(0), "a missing slot is zero, not a crash");
            Assert.That(fromShort[4].Capacity, Is.EqualTo(0));
        }

        [Test]
        public void GroupCapacities_ANegativeCountNeverReportsAPlaceableNegative()
        {
            // SetEnemyCount is the only writer and cannot produce a negative, but a negative placeable
            // would read as corruption rather than as a missing value, so the clamp is pinned.
            int[] budget = { 0, -3, 5, 0, 0, 0 };
            int[] points = { 0, 4, -2, 0, 0, 0 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(rows[0].Placeable, Is.EqualTo(0));
            Assert.That(rows[0].Budget, Is.EqualTo(0));
            Assert.That(rows[1].Placeable, Is.EqualTo(0));
            Assert.That(rows[1].Capacity, Is.EqualTo(0));
        }

        [Test]
        public void CountBudgetWithoutPoints_IgnoresAGroupWithNoBudget()
        {
            // A group with neither budget nor points is not "starved" — it is simply not this room's
            // business, and counting it would inflate the warning the overlay prints.
            int[] budget = { 0, 0, 3, 0, 0, 7 };
            int[] points = { 0, 0, 0, 0, 5, 0 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(RoomDebugSummary.CountBudgetWithoutPoints(rows), Is.EqualTo(2),
                "enl2 and lov have a budget and no points; enc1 has points and no budget");
        }

        [Test]
        public void FormatCapacity_RendersBudgetSlashPointsPerGroup()
        {
            int[] budget = { 0, 1, 1, 1, 9, 5 };
            int[] points = { 0, 4, 0, 2, 0, 5 };

            List<RoomDebugSummary.GroupCapacity> rows =
                RoomDebugSummary.GroupCapacities(budget, points);

            Assert.That(RoomDebugSummary.FormatCapacity(rows),
                Is.EqualTo("enl1=1/4   enl2=1/0   enf1=1/2   enc1=9/0   lov=5/5"));
        }

        [Test]
        public void FormatCapacity_EmptyIsNone()
        {
            Assert.That(RoomDebugSummary.FormatCapacity(new List<RoomDebugSummary.GroupCapacity>()),
                Is.EqualTo("none"));
            Assert.That(RoomDebugSummary.FormatCapacity(null), Is.EqualTo("none"));
        }
    }
}

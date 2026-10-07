using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Entities.Enemies;
using UnityEngine;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The enemy-AI overlay's selection rules: cycling, nearest-pick, and the state label.
    ///
    /// <para><b>Why these are pure functions.</b> They are called from an IMGUI panel, which cannot be
    /// executed outside the editor — so every branch that can be wrong was moved here instead. Both
    /// have a silent failure mode: an off-by-one in cycling lands the index out of range, and "nothing
    /// selected" is indistinguishable from "a working overlay with nothing under the cursor"; and an
    /// unstable nearest-pick makes the selection flicker between two equidistant enemies, which reads
    /// as a rendering bug rather than as a comparison operator.</para>
    /// </summary>
    [TestFixture]
    public class EnemyAISelectionTests
    {
        // ── Cycle ────────────────────────────────────────────────────────────

        [Test]
        public void Cycle_StepsForwardAndBack()
        {
            Assert.AreEqual(1, EnemyAISelection.Cycle(0, 3, 1));
            Assert.AreEqual(2, EnemyAISelection.Cycle(1, 3, 1));
            Assert.AreEqual(1, EnemyAISelection.Cycle(2, 3, -1));
            Assert.AreEqual(0, EnemyAISelection.Cycle(1, 3, -1));
        }

        [Test]
        public void Cycle_WrapsAtBothEnds()
        {
            Assert.AreEqual(0, EnemyAISelection.Cycle(2, 3, 1),
                "stepping forward from the last must reach the first — a clamp would pin the selection " +
                "and read as 'the key stopped working'");

            Assert.AreEqual(2, EnemyAISelection.Cycle(0, 3, -1),
                "stepping BACK from the first must reach the last. C#'s % keeps the dividend's sign, so " +
                "(-1 % 3) is -1 and needs the fixup — without it this returns a negative index, which " +
                "reads as 'nothing selected'");
        }

        [Test]
        public void Cycle_EmptyList_IsNoSelection()
        {
            // The case a naive `(current + 1) % count` turns into a divide-by-zero.
            Assert.AreEqual(EnemyAISelection.NoSelection, EnemyAISelection.Cycle(0, 0, 1));
            Assert.AreEqual(EnemyAISelection.NoSelection, EnemyAISelection.Cycle(0, 0, -1));
            Assert.AreEqual(EnemyAISelection.NoSelection, EnemyAISelection.Cycle(0, -5, 1));
        }

        [Test]
        public void Cycle_FromNoSelection_EntersAtTheRightEnd()
        {
            Assert.AreEqual(0, EnemyAISelection.Cycle(EnemyAISelection.NoSelection, 3, 1),
                "stepping forward with nothing selected starts at the first");
            Assert.AreEqual(2, EnemyAISelection.Cycle(EnemyAISelection.NoSelection, 3, -1),
                "stepping back with nothing selected starts at the last");
        }

        [Test]
        public void Cycle_FromAnOutOfRangeIndex_ReentersRatherThanExtrapolating()
        {
            // Reachable in play: the selection is held across frames while the enemy list shrinks as
            // units die. Extrapolating from a stale index would land somewhere arbitrary.
            Assert.AreEqual(0, EnemyAISelection.Cycle(99, 3, 1));
            Assert.AreEqual(2, EnemyAISelection.Cycle(99, 3, -1));
            Assert.AreEqual(0, EnemyAISelection.Cycle(-7, 3, 1));
        }

        [Test]
        public void Cycle_DirectionZero_NormalisesWithoutMoving()
        {
            Assert.AreEqual(1, EnemyAISelection.Cycle(1, 3, 0));
            Assert.AreEqual(EnemyAISelection.NoSelection, EnemyAISelection.Cycle(99, 3, 0),
                "direction 0 must report an out-of-range index as no-selection, not clamp it to a real one");
            Assert.AreEqual(EnemyAISelection.NoSelection, EnemyAISelection.Cycle(EnemyAISelection.NoSelection, 3, 0));
        }

        [Test]
        public void Cycle_ASingleCandidate_StaysOnIt()
        {
            for (int dir = -1; dir <= 1; dir++)
            {
                Assert.AreEqual(0, EnemyAISelection.Cycle(0, 1, dir), $"direction {dir}");
            }
        }

        [Test]
        public void Cycle_AFullLapReturnsToTheStart()
        {
            // The property, not one example: N steps forward must be the identity for every start.
            const int count = 5;

            for (int start = 0; start < count; start++)
            {
                int index = start;
                for (int step = 0; step < count; step++) index = EnemyAISelection.Cycle(index, count, 1);

                Assert.AreEqual(start, index, $"a full lap from {start} must return to {start}");
            }
        }

        [Test]
        public void Cycle_NeverReturnsAnIndexOutsideTheList()
        {
            // Exhaustive over a small domain: this is the property that matters, and it is cheap.
            const int count = 4;

            for (int current = -2; current <= count + 2; current++)
            {
                for (int dir = -2; dir <= 2; dir++)
                {
                    int index = EnemyAISelection.Cycle(current, count, dir);

                    bool valid = index == EnemyAISelection.NoSelection || (index >= 0 && index < count);
                    Assert.IsTrue(valid, $"Cycle({current}, {count}, {dir}) returned {index}");
                }
            }
        }

        // ── Nearest ──────────────────────────────────────────────────────────

        private static readonly Vector2[] Three =
        {
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(100f, 0f),
        };

        [Test]
        public void Nearest_PicksTheClosest()
        {
            Assert.AreEqual(0, EnemyAISelection.Nearest(Three, new Vector2(1f, 0f), 1000f));
            Assert.AreEqual(1, EnemyAISelection.Nearest(Three, new Vector2(9f, 0f), 1000f));
            Assert.AreEqual(2, EnemyAISelection.Nearest(Three, new Vector2(95f, 0f), 1000f));
        }

        [Test]
        public void Nearest_RespectsTheRadius()
        {
            // (50,0) sits in the GAP between the candidate at 10 and the one at 100: 40 units from the
            // nearest, so nothing qualifies inside a 5-unit radius. The first draft of this test queried
            // from (1,0), which is 1 unit from the candidate at the origin -- the comment talked about the
            // candidate at 100 being far away and the query point was never checked. It went red on the
            // first offline run, which is exactly what a radius test is for.
            Assert.AreEqual(EnemyAISelection.NoSelection,
                EnemyAISelection.Nearest(Three, new Vector2(50f, 0f), 5f),
                "the radius is measured from the QUERY POINT, so a candidate 40 units away is excluded " +
                "even though it is the nearest one");

            // And a point outside every candidate, as the original intent described.
            Assert.AreEqual(EnemyAISelection.NoSelection,
                EnemyAISelection.Nearest(Three, new Vector2(-20f, 0f), 5f));
        }

        [Test]
        public void Nearest_ExactlyAtTheRadius_IsIncluded()
        {
            // `distSq > limitSq` skips, so equality is inclusive. Pinned because the boundary is the
            // difference between "a click at the very edge selects" and "it silently does not".
            Assert.AreEqual(0, EnemyAISelection.Nearest(Three, new Vector2(5f, 0f), 5f));
        }

        [Test]
        public void Nearest_TiesGoToTheLowestIndex()
        {
            var symmetric = new[] { new Vector2(-10f, 0f), new Vector2(10f, 0f) };

            Assert.AreEqual(0, EnemyAISelection.Nearest(symmetric, Vector2.zero, 100f),
                "two equidistant candidates must resolve the SAME way every frame; a pick that " +
                "alternates makes the selection flicker and reads as a rendering bug");

            // And it is stable when asked repeatedly, which is the property that actually matters.
            for (int i = 0; i < 20; i++)
            {
                Assert.AreEqual(0, EnemyAISelection.Nearest(symmetric, Vector2.zero, 100f));
            }
        }

        [Test]
        public void Nearest_EmptyOrNull_IsNoSelection()
        {
            Assert.AreEqual(EnemyAISelection.NoSelection,
                EnemyAISelection.Nearest(null, Vector2.zero, 100f));
            Assert.AreEqual(EnemyAISelection.NoSelection,
                EnemyAISelection.Nearest(new List<Vector2>(), Vector2.zero, 100f));
        }

        [Test]
        public void Nearest_InfiniteRadius_PicksTheClosestAnywhere()
        {
            Assert.AreEqual(2, EnemyAISelection.Nearest(Three, new Vector2(1000f, 0f), float.PositiveInfinity));
        }

        [Test]
        public void Nearest_NeverReturnsAnIndexOutsideTheList()
        {
            for (int i = -3; i <= 3; i++)
            {
                int index = EnemyAISelection.Nearest(Three, new Vector2(i, 0f), 1000f);
                Assert.IsTrue(index >= 0 && index < Three.Length, $"at x={i} returned {index}");
            }
        }

        // ── DescribeState ────────────────────────────────────────────────────

        [Test]
        public void DescribeState_LabelsTheFourAS3States()
        {
            StringAssert.Contains("aiState 0", EnemyAISelection.DescribeState(EnemyAIState.Idle));
            StringAssert.Contains("aiState 1", EnemyAISelection.DescribeState(EnemyAIState.Patrol));
            StringAssert.Contains("aiState 2", EnemyAISelection.DescribeState(EnemyAIState.Alert));
            StringAssert.Contains("aiState 3", EnemyAISelection.DescribeState(EnemyAIState.CombatChase));
        }

        [Test]
        public void DescribeState_Attack_SaysItIsPortOnly()
        {
            // The single most important label here. EnemyAIState's own doc records that its 7 is NOT
            // AS3's 7 — AS3's 7 is the super attack — and a readout printing a bare "7" would invite
            // exactly that misreading.
            string text = EnemyAISelection.DescribeState(EnemyAIState.Attack);

            StringAssert.Contains("PORT-ONLY", text);
            StringAssert.Contains("super", text);
        }

        [Test]
        public void DescribeState_Dead_SaysItIsNotAState()
        {
            string text = EnemyAISelection.DescribeState(EnemyAIState.Dead);

            StringAssert.Contains("sost", text);
            StringAssert.Contains("not an aiState", text);
        }

        [Test]
        public void DescribeState_BuriedAndDigging_NameTheirAS3Meaning()
        {
            StringAssert.Contains("aiState 5", EnemyAISelection.DescribeState(EnemyAIState.Buried));
            StringAssert.Contains("aiState 6", EnemyAISelection.DescribeState(EnemyAIState.Digging));
        }

        [Test]
        public void DescribeState_AnUnlabelledValue_SaysSo()
        {
            // The default arm. A new enum member must be visibly unlabelled rather than silently
            // inherit an existing label — the same rule the parsers' default arms follow.
            string text = EnemyAISelection.DescribeState((EnemyAIState)99);

            StringAssert.Contains("UNLABELLED", text);
            StringAssert.Contains("99", text);
        }

        [Test]
        public void DescribeState_CoversEveryDeclaredMember()
        {
            // Every member of the enum must have its own label. A member added without one falls to
            // the default arm and would be caught here rather than in a screenshot.
            foreach (EnemyAIState state in System.Enum.GetValues(typeof(EnemyAIState)))
            {
                string text = EnemyAISelection.DescribeState(state);

                Assert.IsFalse(string.IsNullOrEmpty(text), $"{state} has no label");
                Assert.IsFalse(text.Contains("UNLABELLED"), $"{state} fell through to the default arm");
            }
        }
    }
}

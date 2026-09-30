using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="UnitSweepMath"/> — AS3's bullet-versus-unit sub-step loop.
    ///
    /// <para>This is the regression guard for "bullets sometimes go through enemies". The bug was never
    /// that the test was wrong; it was that the test <i>did not happen</i> between two samples 40 px
    /// apart. So the load-bearing cases are the ones where a single sample provably misses a box that
    /// the sub-stepped loop finds — those fail loudly if anyone ever "simplifies" the loop back into an
    /// end-of-tick overlap test.</para>
    ///
    /// <para><b>Every expected position here is derived from the sub-step grid, not guessed.</b>
    /// <c>StepsFor(40, 0)</c> is <c>floor(40/9) + 1 = 5</c>, so a 40 px move is five steps of 8 px and
    /// the positions tested are 8, 16, 24, 32, 40. Several of the first draft's expectations assumed 16
    /// was reachable on a 400 px move (it is not — that is 45 steps of 8.89 px) and two placed the box
    /// where no sub-step lands at all. Those drafts were wrong, not the implementation: the arithmetic
    /// was checked by running these same cases against deliberately-wrong variants before committing
    /// them. If you change a delta here, re-derive the grid.</para>
    /// </summary>
    [TestFixture]
    public class UnitSweepMathTests
    {
        private static UnitBoxPx Box(float centreX, float centreY, float width, float height)
            => UnitBoxPx.FromCentre(centreX, centreY, width, height);

        private static List<UnitBoxPx> Boxes(params UnitBoxPx[] boxes) => new List<UnitBoxPx>(boxes);

        // ── The constant ─────────────────────────────────────────────────────

        [Test]
        public void MaxDelta_MatchesTheTileQueryConstant()
        {
            // The duplicate exists only so UnitSweepMath stays Unity-free and can be executed offline.
            // This is what keeps the duplicate honest: if either side is ever retuned, this fails rather
            // than the two silently disagreeing about how far a sub-step may move.
            Assert.AreEqual(TileQueryConstants.MaxDelta, UnitSweepMath.MaxDeltaPx, 1e-6f);
            Assert.AreEqual(9f, UnitSweepMath.MaxDeltaPx, 1e-6f, "AS3: World.as:52");
        }

        // ── Step count ───────────────────────────────────────────────────────

        [Test]
        public void StepsFor_BelowTheThreshold_IsASingleStep()
        {
            // AS3's short-circuit branch (`Bullet.as:192-196`). It is redundant with the formula, and
            // this is where that claim is checked rather than asserted. (5, 5) is the case that catches
            // an implementation summing the axes instead of taking the larger one.
            Assert.AreEqual(1, UnitSweepMath.StepsFor(8.9f, 0f));
            Assert.AreEqual(1, UnitSweepMath.StepsFor(0f, 8.9f));
            Assert.AreEqual(1, UnitSweepMath.StepsFor(0f, 0f));
            Assert.AreEqual(1, UnitSweepMath.StepsFor(5f, 5f));
        }

        [Test]
        public void StepsFor_AtTheThreshold_Splits()
        {
            // The boundary AS3 switches on: `|dx| < maxdelta` is false at exactly 9, so 9 takes the
            // sub-stepping branch. Off-by-one here moves the seam by a whole sub-step, and this is the
            // only case that catches a ceil()-based implementation.
            Assert.AreEqual(2, UnitSweepMath.StepsFor(9f, 0f));
            Assert.AreEqual(2, UnitSweepMath.StepsFor(0f, 9f));
        }

        [Test]
        public void StepsFor_IsDrivenByTheLargerComponent()
        {
            // A slow axis must not buy extra sub-steps, and a negative delta must not look like no
            // movement.
            Assert.AreEqual(5, UnitSweepMath.StepsFor(40f, 0f));
            Assert.AreEqual(5, UnitSweepMath.StepsFor(0f, 40f));
            Assert.AreEqual(5, UnitSweepMath.StepsFor(40f, 3f));
            Assert.AreEqual(5, UnitSweepMath.StepsFor(-40f, 0f), "sign must not matter");
        }

        [Test]
        public void StepsFor_NeverLetsASubStepExceedMaxDelta()
        {
            // The whole anti-tunnelling argument, swept over a range rather than spot-checked: if this
            // ever fails, a point can step over a box again and the bug is back.
            for (int delta = 0; delta <= 600; delta++)
            {
                int steps = UnitSweepMath.StepsFor(delta, 0f);

                Assert.GreaterOrEqual(steps, 1, $"delta={delta} must still get one test");
                Assert.LessOrEqual(delta / (float)steps, UnitSweepMath.MaxDeltaPx + 1e-4f,
                    $"delta={delta} advanced {delta / (float)steps} px in one sub-step");
            }
        }

        // ── The headline case: a box a single sample would skip ──────────────

        [Test]
        public void TryFirstHit_FindsABoxThatAnEndOfTickSampleWouldMiss()
        {
            // One tick of a `skorost` 100 bullet: 40 px. A 12 px unit sits 10..22 px along that path.
            var boxes = Boxes(Box(centreX: 16f, centreY: 0f, width: 12f, height: 12f));

            // Control first: the endpoint really is outside the box, so this case cannot pass by
            // accident on an implementation that only samples where the tick ends.
            Assert.IsFalse(boxes[0].Contains(40f, 0f),
                "control: the end-of-tick position must be clear of the box");

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                out int index, out float hitX, out float hitY);

            Assert.IsTrue(hit, "the sub-stepped loop must find a box the endpoint sample misses");
            Assert.AreEqual(0, index);
            Assert.AreEqual(16f, hitX, 1e-3f, "5 sub-steps of 8 px: the box is entered at x=16");
            Assert.AreEqual(0f, hitY, 1e-3f);
        }

        [Test]
        public void TryFirstHit_ABoxAtLeastMaxDeltaWideIsNeverSkipped()
        {
            // The guarantee, stated properly. It is NOT "the first hit is near the origin" — a hit can
            // be anywhere along the path; what sub-stepping buys is that the gap BETWEEN TESTS is at
            // most MaxDelta, so a box at least that wide cannot fall between two of them. Swept across
            // the path rather than spot-checked, because the failure mode being guarded against is
            // exactly "most positions work".
            for (int i = 0; i <= 30; i++)
            {
                var boxes = Boxes(Box(centreX: 5f + i, centreY: 0f,
                                      width: UnitSweepMath.MaxDeltaPx, height: 12f));

                Assert.IsTrue(
                    UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                        out _, out _, out _),
                    $"a {UnitSweepMath.MaxDeltaPx} px box centred at x={5f + i} was skipped");
            }
        }

        [Test]
        public void TryFirstHit_BoxBehindTheStart_IsNotHit()
        {
            // Advance-then-test, AS3's order (`Bullet.as:417-418` before `:503`). The origin is the
            // previous tick's last sub-step, so it is not re-tested here.
            var boxes = Boxes(Box(centreX: 0f, centreY: 0f, width: 12f, height: 12f));

            Assert.IsTrue(boxes[0].Contains(0f, 0f), "control: the box does contain the origin");

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                out _, out _, out _);

            Assert.IsFalse(hit, "the origin is not tested, so a box only there is missed");
        }

        // ── Selection and reporting ──────────────────────────────────────────

        [Test]
        public void TryFirstHit_TakesTheEarliestSubStep_NotTheNearestBox()
        {
            // A far box and a near box on the same line: the loop walks outward, so the near one wins
            // by being reached first — not by any distance comparison.
            var boxes = Boxes(
                Box(centreX: 320f, centreY: 0f, width: 12f, height: 12f),
                Box(centreX: 16f,  centreY: 0f, width: 12f, height: 12f));

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 400f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                out int index, out float hitX, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(1, index, "the near box is reached first even though it is later in the list");
            // Deliberately not pinned to an exact x: 400 px gives 45 sub-steps of 8.89 px, so pinning 16
            // would pin the arithmetic rather than the behaviour.
            Assert.Less(hitX, 100f, "the hit must be at the near box, not the far one");
        }

        [Test]
        public void TryFirstHit_ReportsListOrder_WhenTwoBoxesShareTheSubStep()
        {
            // Two boxes containing the same sub-step position. AS3 iterates `loc.units` and acts on the
            // first whose bounds contain the point, so list order decides — and a caller that needs
            // determinism has to pass a stable order.
            //
            // Both boxes must contain the SAME position and neither may contain the one before it, or
            // the earlier position decides the outcome and the tie never happens. Box 0's centre is
            // further from the hit than box 1's, so "list order" and "nearest centre" disagree, and only
            // one of them is AS3.
            var boxes = Boxes(
                Box(centreX: 20f, centreY: 0f, width: 12f, height: 12f),   // [14,26], centre 20
                Box(centreX: 18f, centreY: 0f, width: 16f, height: 12f));  // [10,26], centre 18

            Assert.IsTrue(boxes[0].Contains(16f, 0f), "control: box 0 contains the tie position");
            Assert.IsTrue(boxes[1].Contains(16f, 0f), "control: box 1 contains the tie position");
            Assert.IsFalse(boxes[0].Contains(8f, 0f), "control: box 0 excludes the earlier sub-step");
            Assert.IsFalse(boxes[1].Contains(8f, 0f), "control: box 1 excludes the earlier sub-step");

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                out int index, out _, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0, index, "list order decides, not the nearer centre");
        }

        [Test]
        public void TryFirstHit_ReportsTheSubStepPosition_NotTheBoxCentre()
        {
            // The hit point feeds the impact position and the damage-context travel distance, so a box
            // centre would be a silent positional error of up to half a unit's width. The box's centre
            // has to differ from the sub-step position that lands in it, or the check cannot tell the
            // two apart: [10,50] is entered at x=16 and centred at 30.
            var boxes = Boxes(Box(centreX: 30f, centreY: 0f, width: 40f, height: 12f));

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f, boxes, UnitSweepMath.MaxDeltaPx,
                out _, out float hitX, out float hitY);

            Assert.IsTrue(hit);
            Assert.AreEqual(16f, hitX, 1e-3f, "the sub-step position, not the box centre (30)");
            Assert.AreEqual(0f, hitY, 1e-3f);
        }

        [Test]
        public void TryFirstHit_WithNoBoxes_ReturnsFalse()
        {
            Assert.IsFalse(UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f,
                new List<UnitBoxPx>(), UnitSweepMath.MaxDeltaPx, out int index, out _, out _));
            Assert.AreEqual(-1, index, "no hit must report -1, not a valid-looking index");

            Assert.IsFalse(UnitSweepMath.TryFirstHit(0f, 0f, 40f, 0f,
                null, UnitSweepMath.MaxDeltaPx, out _, out _, out _));
        }

        [Test]
        public void TryFirstHit_WalksDiagonalSegmentsToo()
        {
            // Nothing in AS3 restricts the path to an axis; the step count comes from the larger
            // component and both axes advance by the same divisor, so the point stays on the diagonal.
            var boxes = Boxes(Box(centreX: 12f, centreY: 12f, width: 8f, height: 8f));

            bool hit = UnitSweepMath.TryFirstHit(0f, 0f, 40f, 40f, boxes, UnitSweepMath.MaxDeltaPx,
                out _, out float hitX, out float hitY);

            Assert.IsTrue(hit);
            Assert.AreEqual(8f, hitX, 1e-3f, "5 sub-steps of (8,8): the box [8,16]^2 is entered at (8,8)");
            Assert.AreEqual(8f, hitY, 1e-3f);
        }

        // ── The box itself ───────────────────────────────────────────────────

        [Test]
        public void Contains_IsInclusiveOnAllFourEdges()
        {
            // AS3's test is `>=`/`<=` on every bound (`Bullet.as:515`), so a point exactly on an edge
            // counts as a hit. Making any one edge exclusive would shrink every unit by a pixel on that
            // side and read as "some shots near the edge miss".
            var box = new UnitBoxPx(10f, 20f, 30f, 40f);

            Assert.IsTrue(box.Contains(10f, 35f), "left edge");
            Assert.IsTrue(box.Contains(20f, 35f), "right edge");
            Assert.IsTrue(box.Contains(15f, 30f), "bottom edge");
            Assert.IsTrue(box.Contains(15f, 40f), "top edge");
            Assert.IsTrue(box.Contains(15f, 35f), "inside");

            Assert.IsFalse(box.Contains(9.9f, 35f), "left of the left edge");
            Assert.IsFalse(box.Contains(20.1f, 35f), "right of the right edge");
            Assert.IsFalse(box.Contains(15f, 29.9f), "below the bottom edge");
            Assert.IsFalse(box.Contains(15f, 40.1f), "above the top edge");
        }

        [Test]
        public void FromCentre_ProducesTheBoxAS3WouldHave()
        {
            var box = UnitBoxPx.FromCentre(100f, 50f, 20f, 40f);

            Assert.AreEqual(90f, box.MinX, 1e-4f);
            Assert.AreEqual(110f, box.MaxX, 1e-4f);
            Assert.AreEqual(30f, box.MinY, 1e-4f);
            Assert.AreEqual(70f, box.MaxY, 1e-4f);
            Assert.AreEqual(20f, box.Width, 1e-4f);
            Assert.AreEqual(40f, box.Height, 1e-4f);
        }
    }
}

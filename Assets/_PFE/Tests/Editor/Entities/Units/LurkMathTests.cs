using System.Collections.Generic;
using NUnit.Framework;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="LurkMath"/> — the offline half of S7, i.e. AS3's
    /// <c>UnitPlayer.lurk()</c> (<c>:2992-3053</c>) and its per-tick block (<c>:1030-1056</c>).
    ///
    /// <para><b>Why this fixture exists.</b> The port has the lurk *art* — `LurkAnimState`,
    /// `AnimationAction.Lurk/Unlurk` — with no gameplay caller, and `TileData.lurk` is imported
    /// but read by nothing. This is the geometry that decides "did the player get cover, and
    /// where did they end up standing": the scan, the grade-2 edge snap, the tile fallback, and
    /// the three exit conditions.</para>
    ///
    /// <para>The boundaries that a plausible re-implementation gets wrong are: strictly-inside
    /// on all four rect edges, the probe at <c>y - 10</c> (the chest, not the feet),
    /// <c>&gt;</c> rather than <c>&gt;=</c> on the best-so-far grade (so the first box wins a
    /// tie), and <b>AS3's round-half-up rather than .NET's banker's rounding</b>.</para>
    /// </summary>
    [TestFixture]
    public sealed class LurkMathTests
    {
        // ── helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>A grade-1 box 60 wide centred on x, standing on the floor at y = 0.</summary>
        private static LurkBox Box1(float centreX, int lurk = 1)
            => LurkBox.Centered(lurk, centreX, -20f, 30f, 20f);

        private static readonly List<LurkBox> Empty = new List<LurkBox>();

        private static List<LurkBox> One(LurkBox box)
        {
            return new List<LurkBox> { box };
        }

        // ── the gate ─────────────────────────────────────────────────────────────────────────

        /// <summary>Four independent conditions, all required — AS3's conjunction.</summary>
        [Test]
        public void CanLurk_AllFourConditionsAreRequired()
        {
            Assert.That(LurkMath.CanLurk(true, false, 0f, 1), Is.True, "the neutral case must lurk");

            Assert.That(LurkMath.CanLurk(false, false, 0f, 1), Is.False, "!stay — airborne");
            Assert.That(LurkMath.CanLurk(true, true, 0f, 1), Is.False, "isSit");
            Assert.That(LurkMath.CanLurk(true, false, 5f, 1), Is.False, "dx == 5 — the oracle is strictly &lt; 5");
            Assert.That(LurkMath.CanLurk(true, false, -5f, 1), Is.False, "dx == -5 — strictly &gt; -5");
            Assert.That(LurkMath.CanLurk(true, false, 0f, 0), Is.False, "stayPhis &lt; 1");
        }

        /// <summary>
        /// The dx window is open at 5 — so 4.99 larks and 5 does not. Pinned because "running
        /// takes cover" is the difference between a panic button and a mechanic.
        /// </summary>
        [Test]
        public void CanLurk_DxWindowIsOpen_NotClosed()
        {
            Assert.That(LurkMath.CanLurk(true, false, 4.999f, 1), Is.True);
            Assert.That(LurkMath.CanLurk(true, false, -4.999f, 1), Is.True);
            Assert.That(LurkMath.CanLurk(true, false, 5f, 1), Is.False);
        }

        // ── the box scan ─────────────────────────────────────────────────────────────────────

        /// <summary>A unit inside exactly one box takes cover in it.</summary>
        [Test]
        public void BestLurkBox_InsideTheOnlyBox_Wins()
        {
            var boxes = One(Box1(0f));
            Assert.That(LurkMath.BestLurkBox(boxes, 0f, 0f), Is.EqualTo(0));
        }

        /// <summary>
        /// The probe is the unit's <b>chest</b> — AS3 probes <c>Y - 10</c>, not <c>Y</c>.
        /// Discriminating pair: a unit standing so low that its head rises above the box's top
        /// face is <b>not</b> in cover, even though its feet are.
        /// </summary>
        [Test]
        public void BestLurkBox_ProbeIsTenAboveTheFeet_NotTheFeet()
        {
            // Box spans y ∈ [-40, 0]. Standing on the floor at y = 0 the chest probes at -10 —
            // comfortably inside, so the unit takes cover.
            Assert.That(LurkMath.BestLurkBox(One(Box1(0f)), 0f, 0f), Is.EqualTo(0),
                "feet on the floor, chest inside — cover");

            // At y = -15 the chest probes at -25, still inside.
            Assert.That(LurkMath.BestLurkBox(One(Box1(0f)), 0f, -15f), Is.EqualTo(0));

            // At y = -35 the chest probes at -45, which is <b>above</b> the box's top face (-40).
            // The feet (-35) are inside, so a probe at the feet would wrongly return cover.
            Assert.That(LurkMath.BestLurkBox(One(Box1(0f)), 0f, -35f), Is.EqualTo(-1),
                "head above the box — this is the case that separates y from y - 10");
        }

        /// <summary>
        /// Strictly inside on all four edges. Equality is the common case at tile boundaries,
        /// which is exactly why the strictness is load-bearing rather than academic.
        /// </summary>
        [Test]
        public void BestLurkBox_BoundariesAreExclusive()
        {
            var box = Box1(0f); // x ∈ [-30, 30], y ∈ [-40, 0]

            Assert.That(LurkMath.BestLurkBox(One(box), 30f, 0f), Is.EqualTo(-1), "on the right face");
            Assert.That(LurkMath.BestLurkBox(One(box), -30f, 0f), Is.EqualTo(-1), "on the left face");
            Assert.That(LurkMath.BestLurkBox(One(box), 0f, 10f), Is.EqualTo(-1),
                "chest exactly on the top face (y = 10 → probe 0 = Y2)");
            Assert.That(LurkMath.BestLurkBox(One(box), 0f, -30f), Is.EqualTo(-1),
                "chest exactly on the base (y = -30 → probe -40 = Y1)");
            Assert.That(LurkMath.BestLurkBox(One(box), 29.999f, 9.999f), Is.EqualTo(0), "just inside on all four");
        }

        /// <summary>
        /// The highest grade wins, and a lower-grade box cannot displace it — the running
        /// best-so-far is AS3's <c>lurkTip</c>, which starts at 0 and gates the first pick too.
        /// </summary>
        [Test]
        public void BestLurkBox_HighestGradeWins()
        {
            var boxes = new List<LurkBox> { Box1(-10f, 1), Box1(10f, 3), Box1(0f, 2) };
            Assert.That(LurkMath.BestLurkBox(boxes, 0f, -20f), Is.EqualTo(1), "grade 3");
        }

        /// <summary>
        /// <c>&gt;</c> on the best-so-far, not <c>&gt;=</c> — so the <b>first</b> box of the
        /// winning grade is kept, and iteration order is load-bearing. The assertion is on the
        /// resulting snap position, not the index, because that is what proves *which* box won.
        /// </summary>
        [Test]
        public void BestLurkBox_TiesGoToTheFirstBox_NotTheLast()
        {
            var boxes = new List<LurkBox> { Box1(-10f, 2), Box1(10f, 2) };

            // Unit at x = 0. Winning box at -10 snaps right (X2 - 10 = 10); at +10 it snaps
            // left (X1 + 10 = -10). So the snap position names the winner.
            LurkPlacement forward = LurkMath.TryLurk(boxes, NeverLurk, 0f, 0f);
            Assert.That(forward.BoxIndex, Is.EqualTo(0), "the earlier box of equal grade");
            Assert.That(forward.LurkX, Is.EqualTo(10f), "snapped from the box at -10");

            boxes.Reverse();
            LurkPlacement reversed = LurkMath.TryLurk(boxes, NeverLurk, 0f, 0f);
            Assert.That(reversed.BoxIndex, Is.EqualTo(0), "still index 0, but a *different* box");
            Assert.That(reversed.LurkX, Is.EqualTo(-10f), "snapped from the box at +10");
        }

        /// <summary>A grade-0 box is not cover, even centred on the unit.</summary>
        [Test]
        public void BestLurkBox_GradeZeroIsNeverCover()
        {
            Assert.That(LurkMath.BestLurkBox(One(Box1(0f, 0)), 0f, -20f), Is.EqualTo(-1));
        }

        /// <summary>A null or empty location finds nothing rather than throwing.</summary>
        [Test]
        public void BestLurkBox_NoBoxes_IsMinusOne()
        {
            Assert.That(LurkMath.BestLurkBox(null, 0f, 0f), Is.EqualTo(-1));
            Assert.That(LurkMath.BestLurkBox(Empty, 0f, 0f), Is.EqualTo(-1));
        }

        // ── the grade-2 edge snap — lurk's whole point ───────────────────────────────────────

        /// <summary>
        /// Grade 2 snaps to the <b>near face</b>, compared against the box's centre: right of the
        /// centre → the right edge, left → the left edge.
        /// </summary>
        [Test]
        public void SnapLurkX_GradeTwoSnapsToTheNearestEdge()
        {
            var box = Box1(0f); // grade 1 — centre
            Assert.That(LurkMath.SnapLurkX(box, 100f), Is.EqualTo(0f), "grade 1 aims at the centre");

            var edge = new LurkBox(2, 0f, -30f, -40f, 30f, 0f);
            Assert.That(LurkMath.SnapLurkX(edge, 5f), Is.EqualTo(20f), "right of centre → X2 - 10");
            Assert.That(LurkMath.SnapLurkX(edge, -5f), Is.EqualTo(-20f), "left of centre → X1 + 10");
        }

        /// <summary>
        /// The comparison against the centre is strict, so a unit exactly on the centre takes the
        /// left edge. That is AS3's literal <c>X &gt; this.lurkBox.X</c>.
        /// </summary>
        [Test]
        public void SnapLurkX_CentreComparisonIsStrict()
        {
            var edge = new LurkBox(2, 0f, -30f, -40f, 30f, 0f);
            Assert.That(LurkMath.SnapLurkX(edge, 0f), Is.EqualTo(-20f), "exactly on centre → left");
        }

        /// <summary>Grade 3 is <b>not</b> special-cased — the oracle's test is <c>== 2</c>.</summary>
        [Test]
        public void SnapLurkX_GradeThreeFallsThroughToTheCentre()
        {
            var box3 = new LurkBox(3, 7f, -30f, -40f, 30f, 0f);
            Assert.That(LurkMath.SnapLurkX(box3, 100f), Is.EqualTo(7f), "grade 3 behaves like grade 1");
        }

        /// <summary>The facing the snap implies, and the paths that leave it alone.</summary>
        [Test]
        public void SnapStorona_OnlyTheGradeTwoPathWritesFacing()
        {
            var edge = new LurkBox(2, 0f, -30f, -40f, 30f, 0f);
            Assert.That(LurkMath.SnapStorona(edge, 5f), Is.EqualTo(1));
            Assert.That(LurkMath.SnapStorona(edge, -5f), Is.EqualTo(-1));
            Assert.That(LurkMath.SnapStorona(Box1(0f), 5f), Is.EqualTo(0), "grade 1 must not clobber storona");
            Assert.That(LurkMath.SnapStorona(new LurkBox(3, 0f, -30f, -40f, 30f, 0f), 5f), Is.EqualTo(0));
        }

        // ── the tile fallback ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Both flanks must be lurk floor — AS3's <c>&amp;&amp;</c> over <c>X ± 20</c>. One lone
        /// protruding tile is not cover.
        /// </summary>
        [Test]
        public void FlankTilesLurk_BothFlanksRequired()
        {
            Assert.That(LurkMath.FlankTilesLurk((x, y) => true, 0f, 0f), Is.True);
            Assert.That(LurkMath.FlankTilesLurk((x, y) => x < 0f, 0f, 0f), Is.False, "right flank only");
            Assert.That(LurkMath.FlankTilesLurk((x, y) => x > 0f, 0f, 0f), Is.False, "left flank only");
            Assert.That(LurkMath.FlankTilesLurk((x, y) => false, 0f, 0f), Is.False);
            Assert.That(LurkMath.FlankTilesLurk(null, 0f, 0f), Is.False, "no tile source — no cover");
        }

        /// <summary>The fallback probes at the same <c>y - 10</c> as the box scan.</summary>
        [Test]
        public void FlankTilesLurk_ProbesAtTheChest_NotTheFeet()
        {
            var seen = new List<float>();
            bool onlyAtTen(float x, float y)
            {
                seen.Add(y);
                return true;
            }

            LurkMath.FlankTilesLurk(onlyAtTen, 100f, 50f);

            Assert.That(seen, Is.EquivalentTo(new[] { 40f, 40f }), "y - 10 at both flanks");
            Assert.That(LurkMath.FlankTilesLurk((x, y) => y == 50f, 0f, 50f), Is.False,
                "a predicate that is only true at the feet level finds nothing");
        }

        /// <summary>
        /// The flank offsets are ±20. A predicate keyed exactly on the oracle's two probe points
        /// must fire; one keyed on ±19 or ±21 must not.
        /// </summary>
        [Test]
        public void FlankTilesLurk_ProbePointsAreExactlyPlusMinusTwenty()
        {
            Assert.That(LurkMath.FlankTilesLurk((x, y) => x == -20f || x == 20f, 0f, 0f), Is.True);
            Assert.That(LurkMath.FlankTilesLurk((x, y) => x == -19f || x == 21f, 0f, 0f), Is.False);
        }

        // ── rounding — the one place .NET is not a drop-in ───────────────────────────────────

        /// <summary>
        /// <b>The rounding trap.</b> AS3's <c>Math.round</c> is <c>floor(x + 0.5)</c>; .NET's
        /// <c>MathF.Round</c> is banker's rounding, which sends 0.5 → 0 and 2.5 → 2 where the
        /// oracle gives 1 and 3. Half-tile positions are exactly where the fallback fires, so a
        /// framework <c>Round</c> here would shift every such snap half a tile.
        /// </summary>
        [Test]
        public void SnapToTile_MatchesAs3RoundHalfUp_NotBankers()
        {
            Assert.That(LurkMath.SnapToTile(20f), Is.EqualTo(40f), "0.5 tiles → 1, not 0");
            Assert.That(LurkMath.SnapToTile(100f), Is.EqualTo(120f), "2.5 tiles → 3, not 2");
            Assert.That(LurkMath.SnapToTile(-20f), Is.EqualTo(0f), "half rounds toward +∞, as AS3 does");
            Assert.That(LurkMath.SnapToTile(0f), Is.EqualTo(0f));
            Assert.That(LurkMath.SnapToTile(21f), Is.EqualTo(40f), "just over half");
            Assert.That(LurkMath.SnapToTile(19f), Is.EqualTo(0f), "just under half");
            Assert.That(LurkMath.SnapToTile(-21f), Is.EqualTo(-40f));
            Assert.That(LurkMath.SnapToTile(-19f), Is.EqualTo(0f));
        }

        /// <summary>A degenerate tile size must not divide by zero or invent a snap.</summary>
        [Test]
        public void SnapToTile_DegenerateSize_ReturnsX()
        {
            Assert.That(LurkMath.SnapToTile(37f, 0f), Is.EqualTo(37f));
            Assert.That(LurkMath.SnapToTile(37f, -10f), Is.EqualTo(37f));
        }

        // ── the whole verb ───────────────────────────────────────────────────────────────────

        private static bool NeverLurk(float x, float y) => false;
        private static bool AlwaysLurk(float x, float y) => true;

        /// <summary>The box path, end to end: a grade-1 box puts the unit at its centre.</summary>
        [Test]
        public void TryLurk_BoxPath_SnapsToTheCentre()
        {
            LurkPlacement p = LurkMath.TryLurk(One(Box1(0f)), NeverLurk, 0f, 0f);

            Assert.That(p.Lurked, Is.True);
            Assert.That(p.LurkTip, Is.EqualTo(1));
            Assert.That(p.BoxIndex, Is.EqualTo(0));
            Assert.That(p.LurkX, Is.EqualTo(0f), "grade 1 aims at the box centre");
            Assert.That(p.Storona, Is.EqualTo(0));
        }

        /// <summary>The grade-2 path, end to end: snap to the edge <b>and</b> set the facing.</summary>
        [Test]
        public void TryLurk_GradeTwoBox_SnapsToTheEdgeAndSetsFacing()
        {
            var boxes = One(new LurkBox(2, 0f, -30f, -40f, 30f, 0f));
            LurkPlacement p = LurkMath.TryLurk(boxes, NeverLurk, 5f, 0f);

            Assert.That(p.Lurked, Is.True);
            Assert.That(p.LurkTip, Is.EqualTo(2));
            Assert.That(p.LurkX, Is.EqualTo(20f), "right of centre → X2 - 10");
            Assert.That(p.Storona, Is.EqualTo(1));
        }

        /// <summary>
        /// The tile fallback: no box in range, but a run of lurk floor a tile wide. Note the
        /// placement reports <see cref="LurkMath.NoBox"/> — there is no box to invalidate later.
        /// </summary>
        [Test]
        public void TryLurk_TileFallback_WhenNoBoxOverlaps()
        {
            LurkPlacement p = LurkMath.TryLurk(Empty, AlwaysLurk, 61f, 0f);

            Assert.That(p.Lurked, Is.True);
            Assert.That(p.LurkTip, Is.EqualTo(1), "the fallback always reports grade 1");
            Assert.That(p.BoxIndex, Is.EqualTo(LurkMath.NoBox));
            Assert.That(p.LurkX, Is.EqualTo(80f), "61 snaps to 80 — 1.5 tiles rounds up");
        }

        /// <summary>
        /// Neither path: nothing happens, and the caller is told it may fall through to the
        /// armour-ability branch (out of scope here, which is why it is a sentinel and not a
        /// silently successful placement).
        /// </summary>
        [Test]
        public void TryLurk_NoCoverAnywhere_DoesNotLurk()
        {
            LurkPlacement p = LurkMath.TryLurk(Empty, NeverLurk, 61f, 0f);

            Assert.That(p.Lurked, Is.False);
            Assert.That(p.LurkTip, Is.EqualTo(0));
            Assert.That(p.BoxIndex, Is.EqualTo(LurkMath.Nothing));
        }

        /// <summary>A box that exists but does not contain the unit falls through to the tiles.</summary>
        [Test]
        public void TryLurk_BoxOutOfReach_FallsThroughToTiles()
        {
            LurkPlacement p = LurkMath.TryLurk(One(Box1(500f)), AlwaysLurk, 0f, 0f);

            Assert.That(p.BoxIndex, Is.EqualTo(LurkMath.NoBox), "the box was not used");
            Assert.That(p.Lurked, Is.True, "but the tiles still gave cover");
        }

        /// <summary>A null box list is treated as "no boxes", not as a crash.</summary>
        [Test]
        public void TryLurk_NullBoxList_TilePathStillWorks()
        {
            LurkPlacement p = LurkMath.TryLurk(null, AlwaysLurk, 0f, 0f);
            Assert.That(p.Lurked, Is.True);
            Assert.That(p.BoxIndex, Is.EqualTo(LurkMath.NoBox));
        }

        // ── the exit block ───────────────────────────────────────────────────────────────────

        /// <summary>Three independent conditions, any one of which drops lurk.</summary>
        [Test]
        public void ShouldExitLurk_AllThreeConditionsFire()
        {
            var boxes = One(Box1(0f));

            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 0f, 0f, true), Is.False, "held, settled");
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, false, 0f, 0f, true), Is.True, "!stay");
            Assert.That(LurkMath.ShouldExitLurk(One(new LurkBox(1, 0f, -30f, -40f, 30f, 0f, stay: true, wall: 0)),
                0, true, 0f, 0f, true), Is.True, "the box stopped being a wall");
            Assert.That(LurkMath.ShouldExitLurk(One(new LurkBox(1, 0f, -30f, -40f, 30f, 0f, stay: false, wall: 1)),
                0, true, 0f, 0f, true), Is.True, "the box stopped staying put");
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 11f, 0f, false), Is.True, "drifted 11 px");
        }

        /// <summary>
        /// The drift test cannot fire while the entry animation is still driving the unit —
        /// otherwise a grade-2 box snapped 20 px away would cancel itself on its first frame.
        /// </summary>
        [Test]
        public void ShouldExitLurk_DriftIsSuppressedDuringTheEntryAnimation()
        {
            var boxes = One(new LurkBox(2, 0f, -30f, -40f, 30f, 0f));

            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 100f, 20f, true), Is.False,
                "inLurkWork — the snap point is still being walked to");
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 100f, 20f, false), Is.True,
                "same geometry once the animation is over");
        }

        /// <summary>The drift test is strictly greater than 10 — AS3's <c>&gt; 10 || &lt; -10</c>.</summary>
        [Test]
        public void ShouldExitLurk_DriftBoundaryIsStrict()
        {
            var boxes = One(Box1(0f));

            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 10f, 0f, false), Is.False, "exactly 10");
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, 10.001f, 0f, false), Is.True);
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, -10f, 0f, false), Is.False, "exactly -10");
            Assert.That(LurkMath.ShouldExitLurk(boxes, 0, true, -10.001f, 0f, false), Is.True);
        }

        /// <summary>The tile path has no box, so only two of the three conditions can reach it.</summary>
        [Test]
        public void ShouldExitLurk_TilePath_HasNoBoxToInvalidate()
        {
            Assert.That(LurkMath.ShouldExitLurk(Empty, LurkMath.NoBox, true, 0f, 0f, false), Is.False);
            Assert.That(LurkMath.ShouldExitLurk(Empty, LurkMath.NoBox, false, 0f, 0f, false), Is.True, "!stay");
            Assert.That(LurkMath.ShouldExitLurk(Empty, LurkMath.NoBox, true, 50f, 0f, false), Is.True, "drift");
        }

        /// <summary>An out-of-range box index must not throw — the caller's list can resize.</summary>
        [Test]
        public void ShouldExitLurk_InvalidBoxIndex_DoesNotThrow()
        {
            Assert.That(LurkMath.ShouldExitLurk(One(Box1(0f)), 7, true, 0f, 0f, false), Is.False);
            Assert.That(LurkMath.ShouldExitLurk(null, 0, true, 0f, 0f, false), Is.False);
        }

        // ── the peek drive ───────────────────────────────────────────────────────────────────

        /// <summary>4 px/frame toward the snap point, deadbanded at 3, only during the entry.</summary>
        [Test]
        public void LurkDriveDx_DeadbandAndSpeed()
        {
            Assert.That(LurkMath.LurkDriveDx(0f, 10f, true), Is.EqualTo(4f), "snap is to the right");
            Assert.That(LurkMath.LurkDriveDx(0f, -10f, true), Is.EqualTo(-4f), "snap is to the left");
            Assert.That(LurkMath.LurkDriveDx(0f, 3f, true), Is.EqualTo(0f), "exactly 3 — inside the deadband");
            Assert.That(LurkMath.LurkDriveDx(0f, -3f, true), Is.EqualTo(0f), "exactly -3");
            Assert.That(LurkMath.LurkDriveDx(0f, 3.001f, true), Is.EqualTo(4f), "just outside");
            Assert.That(LurkMath.LurkDriveDx(0f, 10f, false), Is.EqualTo(0f), "entry over — stand still");
        }

        // ── a declared-constants tripwire ────────────────────────────────────────────────────

        /// <summary>
        /// If someone edits a constant, this fails and the edit gets a citation to defend.
        /// The values are AS3's literals at <c>UnitPlayer.as:2992-3053</c> and <c>:1030-1056</c>.
        /// </summary>
        [Test]
        public void DeclaredConstants_AreTheOraclesLiterals()
        {
            Assert.That(LurkMath.TileSize, Is.EqualTo(40f));
            Assert.That(LurkMath.ProbeOffsetY, Is.EqualTo(10f));
            Assert.That(LurkMath.FlankOffsetX, Is.EqualTo(20f));
            Assert.That(LurkMath.SnapInset, Is.EqualTo(10f));
            Assert.That(LurkMath.EdgeSnapGrade, Is.EqualTo(2));
            Assert.That(LurkMath.ExitDriftPixels, Is.EqualTo(10f));
            Assert.That(LurkMath.DriveDeadband, Is.EqualTo(3f));
            Assert.That(LurkMath.DriveSpeed, Is.EqualTo(4f));
        }
    }
}



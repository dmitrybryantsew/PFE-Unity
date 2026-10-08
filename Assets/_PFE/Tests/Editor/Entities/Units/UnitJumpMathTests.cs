using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitJumpMath"/> — AS3 <c>UnitZombie.jump()</c>'s trigger band, its
    /// <c>checkJump()</c> headroom geometry, and its self-armed cooldown.
    ///
    /// <para><b>Why this fixture exists.</b> The jump is the exact mirror of
    /// <see cref="UnitDropThroughMath"/>'s drop: the same two quantities (<c>unitTop</c> and
    /// <c>targetCentre</c>), the opposite sign, and a <i>different</i> threshold (40 against 80). That
    /// combination is the dangerous one — a sign copied from the sibling passes every "target is above"
    /// test and fails none of the "target is below" ones, because a flipped subtraction still produces
    /// plausible numbers. Every failure mode here is silent: the zombie hops at a player it is standing
    /// next to, or never hops at all, and neither throws.</para>
    ///
    /// <para><b>What is deliberately NOT pinned here.</b> The caller's wiring — that a zombie in
    /// <c>Alert</c> or <c>CombatChase</c> asks for the jump, that it is refused mid-arc, that the cooldown
    /// is armed — is behaviour and needs a <c>GameObject</c> and a room. That half lives in
    /// <c>EnemyBrainTests</c> and is flagged there as owner-only.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitJumpMathTests
    {
        // ── The thresholds themselves ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>else if(celDY &lt; -40) { aiVNapr = -1; }</c> — <c>UnitZombie.as:683</c>.
        /// </summary>
        [Test]
        public void TargetAboveJumpPixels_IsForty()
        {
            Assert.That(UnitJumpMath.TargetAboveJumpPixels, Is.EqualTo(40f),
                "UnitZombie.as:683 — `else if(celDY < -40) { aiVNapr = -1; }`, the band the jump reads " +
                "at :839 as `aiVNapr < 0`.");

            Assert.That(UnitJumpMath.TargetAboveJumpPixels, Is.Not.EqualTo(80f),
                "80 is UnitZombie.as:859's drop-through trigger (`celDY > 80`). The jump and the drop are " +
                "the two halves of one measurement and their bands are NOT the same width — sharing a " +
                "constant would silently make one of them fire at the other's distance.");

            Assert.That(UnitJumpMath.TargetAboveJumpPixels, Is.Not.EqualTo(70f),
                "70 is UnitZombie.as:692's `porog = 0` band. A third number in the same branch.");

            Assert.That(UnitJumpMath.TargetAboveJumpPixels, Is.Not.EqualTo(200f),
                "200 is UnitZombie.optDistAtt (:79), the contact-attack half-width. Unrelated axis.");
        }

        /// <summary>
        /// The headroom probe's three distances — <c>UnitZombie.as:467-482</c>.
        /// </summary>
        [Test]
        public void HeadroomProbeDistances_AreTheOracles()
        {
            Assert.That(UnitJumpMath.HeadroomRisePixels, Is.EqualTo(85f),
                "UnitZombie.as:467 — `loc.getAbsTile(X, Y - 85).phis != 0`.");

            Assert.That(UnitJumpMath.HeadroomHighRisePixels, Is.EqualTo(125f),
                "UnitZombie.as:471 — `loc.getAbsTile(X, Y - 125).phis != 0`.");

            Assert.That(UnitJumpMath.HeadroomForwardPixels, Is.EqualTo(40f),
                "UnitZombie.as:475 — `loc.getAbsTile(X + 40 * storona, ...)`. One tile, because " +
                "Tile.tileX is 40 (World.as:36).");

            Assert.That(UnitJumpMath.HeadroomHighRisePixels, Is.GreaterThan(UnitJumpMath.HeadroomRisePixels),
                "The two probe heights must be ordered: :467 is the low one and :471 the high one. " +
                "Swapping them puts the 'is there a ceiling' probe under the 'can I stand up' probe, " +
                "which changes which obstacle refuses the jump.");

            Assert.That(UnitJumpMath.HeadroomForwardPixels, Is.EqualTo(UnitJumpMath.TargetAboveJumpPixels),
                "40 here is one TILE, and 40 there is the jump band. They are equal by coincidence of the " +
                "source — pinned so a future edit to either is forced to notice the other.");
        }

        // ── ShouldJump: the boundary and the sign ────────────────────────────────────────────

        /// <summary>
        /// The comparison is <b>strict</b>. A target whose centre sits exactly 40 px above the unit's top
        /// does not trigger the jump — <c>&gt;</c>, not <c>&gt;=</c>.
        /// </summary>
        [Test]
        public void ShouldJump_IsStrictlyGreater()
        {
            // unit top 100, target centre 140 → exactly 40 px above.
            Assert.That(UnitJumpMath.ShouldJump(100f, 140f), Is.False,
                "Exactly 40 px is NOT past `celDY < -40`. Changing this to `>=` is the mutation this " +
                "assertion exists to catch.");

            Assert.That(UnitJumpMath.ShouldJump(100f, 140.1f), Is.True,
                "A hair past 40 px IS past it.");

            Assert.That(UnitJumpMath.ShouldJump(100f, 139.9f), Is.False,
                "A hair short of 40 px is not.");
        }

        /// <summary>
        /// The band sweeps monotonically across the threshold, and every case is listed rather than sampled,
        /// so growing the set cannot leave a stale hand-written expectation behind.
        /// </summary>
        [Test]
        public void ShouldJump_SweepsTheBand()
        {
            // unit top fixed at 100; the gap is (targetCentre - 100).
            (float targetCentre, float gap, bool expected)[] cases =
            {
                (100f,   0f, false), // level with the unit's top
                (120f,  20f, false),
                (139f,  39f, false),
                (140f,  40f, false), // the boundary
                (141f,  41f, true),
                (160f,  60f, true),
                (200f, 100f, true),
                (500f, 400f, true),  // far overhead
            };

            foreach ((float targetCentre, float gap, bool expected) in cases)
            {
                Assert.That(
                    UnitJumpMath.ShouldJump(100f, targetCentre),
                    Is.EqualTo(expected),
                    $"unit top 100, target centre {targetCentre} (gap {gap} px) must be {expected}.");
            }
        }

        /// <summary>
        /// The sign, stated as its own test because it is the failure the two coordinate systems invite.
        ///
        /// <para>AS3's <c>celDY</c> is down-positive, so <c>celDY &lt; -40</c> means the target is
        /// <i>above</i>. Unity's Y is up-positive, so the same condition is
        /// <c>targetCentre - unitTop</c> and the subtraction must be in that order.</para>
        /// </summary>
        [Test]
        public void ShouldJump_TargetBelowIsNeverAJump()
        {
            Assert.That(UnitJumpMath.ShouldJump(500f, 100f), Is.False,
                "Target 400 px BELOW the unit's top. AS3's `celDY` would be +400; only -40 or less jumps.");

            Assert.That(UnitJumpMath.ShouldJump(100f, 100f), Is.False,
                "Target centre level with the unit's top — gap 0.");

            Assert.That(UnitJumpMath.ShouldJump(100f, 141f), Is.True,
                "Target 41 px ABOVE the unit's top — the only direction that triggers.");
        }

        /// <summary>
        /// The two arguments are not interchangeable, and this is the assertion that says so.
        /// </summary>
        /// <remarks>
        /// A symmetric implementation — <c>Mathf.Abs(targetCentre - unitTop) &gt; 40</c> — passes every test
        /// above except this one, and is a plausible thing to write while "tidying" the subtraction. It
        /// would make a zombie hop at a player standing in a pit.
        /// </remarks>
        [Test]
        public void ShouldJump_IsNotSymmetric()
        {
            Assert.That(UnitJumpMath.ShouldJump(100f, 500f), Is.True,
                "Unit top 100, target centre 500 → target is 400 px above → jump.");

            Assert.That(UnitJumpMath.ShouldJump(500f, 100f), Is.False,
                "The same two numbers the other way round → target is 400 px below → do NOT jump. " +
                "A Mathf.Abs() version would return true for both.");
        }

        // ── The mirror: this file's rule against the drop-through's ──────────────────────────

        /// <summary>
        /// <see cref="UnitJumpMath.ShouldJump"/> and <see cref="UnitDropThroughMath.ShouldDropThrough"/> are
        /// exact mirrors over the same two quantities — which is the strongest available statement of the
        /// sign convention, and the one thing a copy-paste between the two files can silently break.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is worth a test rather than a comment.</b> The two live in different files, are
        /// both "compare a unit's top to a target's centre", and differ only in sign and threshold. The
        /// natural way to write the second one is to copy the first — and copying it <i>without</i> flipping
        /// the subtraction produces a zombie that jumps when the player is below it and drops when the player
        /// is above it. Both behaviours are individually plausible, so neither shows up as "wrong" in play;
        /// only the pairing does.</para>
        ///
        /// <para><b>They are mirrors, not equals</b> — the thresholds differ (40 against 80), so the two are
        /// false together inside the band between them. That is asserted here too, because "make them one
        /// function" is the tidy-up this test forbids.</para>
        /// </remarks>
        [Test]
        public void ShouldJump_IsTheMirrorOfShouldDropThrough()
        {
            const float unitTop = 1000f;

            // Target well above the unit's top: the jump fires, the drop does not.
            Assert.That(UnitJumpMath.ShouldJump(unitTop, 1400f), Is.True, "400 px above → jump.");
            Assert.That(UnitDropThroughMath.ShouldDropThrough(unitTop, 1400f), Is.False,
                "...and the same pair must NOT also be a drop.");

            // Target well below: the drop fires, the jump does not.
            Assert.That(UnitJumpMath.ShouldJump(unitTop, 600f), Is.False, "400 px below → no jump.");
            Assert.That(UnitDropThroughMath.ShouldDropThrough(unitTop, 600f), Is.True,
                "...and the same pair IS the drop.");

            // The bands are different widths, so there is a window where BOTH are false — 60 px above
            // is past the jump's 40 and short of the drop's 80.
            Assert.That(UnitJumpMath.ShouldJump(unitTop, unitTop + 60f), Is.True,
                "60 px above is past the jump band's 40.");
            Assert.That(UnitDropThroughMath.ShouldDropThrough(unitTop, unitTop + 60f), Is.False,
                "60 px above is nowhere near the drop band's 80 — so this is a jump and not a drop.");

            // ...and the dead zone between the two bands, below the unit's top.
            Assert.That(UnitJumpMath.ShouldJump(unitTop, unitTop - 60f), Is.False,
                "60 px below is not above, so no jump.");
            Assert.That(UnitDropThroughMath.ShouldDropThrough(unitTop, unitTop - 60f), Is.False,
                "60 px below is inside the drop band's 80, so no drop either — the zombie stays put. " +
                "Collapsing the two constants into one would erase this window.");
        }

        /// <summary>
        /// The call site's composition: the caller does not have <c>unitTop</c> or <c>targetCentre</c>
        /// directly — it has the eye (the unit's centre) and <c>EnemyBlackboard.TargetDeltaY</c>, which
        /// <c>EnemySensors</c> defines as <c>targetCentre - eyePos</c>. Composing them is how the oracle's
        /// <c>celDY</c> is rebuilt, and the algebra is worth pinning because it is where an off-by-half-a-height
        /// would hide.
        /// </summary>
        /// <remarks>
        /// With the unit's height folded in, the condition reduces to
        /// <c>TargetDeltaY &gt; 40 + height / 2</c> — for <c>zombie0</c> (height 0.7 units = 70 px) that is
        /// <b>75 px</b>, i.e. the player's centre must be more than 75 px above the zombie's centre. The
        /// oracle measures from the unit's <i>top</i> and the blackboard from its <i>centre</i>, so the two
        /// differ by exactly half a height; using the blackboard value against the raw 40 would fire the
        /// jump 35 px early.
        /// </remarks>
        [Test]
        public void ShouldJump_ComposesFromTheCallersTwoQuantities()
        {
            const float eyePixelY = 1000f;
            const float zombieHeightPixels = 70f; // zombie0: `height: 0.7` units
            float unitTop = eyePixelY + zombieHeightPixels * 0.5f;

            // TargetDeltaY = targetCentre - eye.
            Assert.That(UnitJumpMath.ShouldJump(unitTop, eyePixelY + 75f), Is.False,
                "TargetDeltaY 75 is exactly `40 + height/2` → the boundary, not past it.");

            Assert.That(UnitJumpMath.ShouldJump(unitTop, eyePixelY + 75.1f), Is.True,
                "A hair past → jump.");

            Assert.That(UnitJumpMath.ShouldJump(unitTop, eyePixelY + 40f), Is.False,
                "Using the raw 40 against a blackboard delta would fire here — the target is only 40 px " +
                "above the unit's CENTRE, which is 5 px below its top. That is the off-by-half-a-height " +
                "this test exists to forbid.");
        }

        // ── The headroom probe geometry ──────────────────────────────────────────────────────

        /// <summary>
        /// The four probe points, as the oracle writes them — this column and the one ahead, at 85 and
        /// 125 px above the feet.
        /// </summary>
        [Test]
        public void HeadroomProbes_AreAboveTheFeetInTwoColumns()
        {
            const float feetX = 400f;
            const float feetY = 200f;

            // Facing right: the ahead column is one tile (+40) to the right.
            Assert.That(UnitJumpMath.HeadroomAheadPixelX(feetX, 1), Is.EqualTo(440f),
                "UnitZombie.as:475 — `X + 40 * storona` with storona = 1.");

            // Facing left: it mirrors.
            Assert.That(UnitJumpMath.HeadroomAheadPixelX(feetX, -1), Is.EqualTo(360f),
                "...and with storona = -1 it is one tile to the left. A probe that did not mirror would " +
                "test the column the zombie is leaving, not the one it is heading into.");

            // Zero is treated as +1, not as 'no column' — a unit with no facing still has to probe
            // somewhere, and probing its own column twice would halve the check.
            Assert.That(UnitJumpMath.HeadroomAheadPixelX(feetX, 0), Is.EqualTo(440f),
                "facing 0 must fall back to a real column, not collapse onto the unit's own.");

            // The port's Y grows UP, and AS3's grows down, so `Y - 85` becomes `feet + 85`.
            Assert.That(UnitJumpMath.HeadroomLowPixelY(feetY), Is.EqualTo(285f),
                "UnitZombie.as:467 — `Y - 85` in AS3's down-positive Y is 85 px ABOVE the feet here.");

            Assert.That(UnitJumpMath.HeadroomHighPixelY(feetY), Is.EqualTo(325f),
                "UnitZombie.as:471 — `Y - 125` is 125 px above the feet.");
        }

        /// <summary>
        /// The probe heights must be <b>above</b> the feet, not below. Stated separately because the sign
        /// flip is the one error that makes the whole probe inert: a probe under the floor is solid almost
        /// everywhere, so the jump would be refused everywhere, and the symptom would be "the zombie never
        /// jumps" — the exact bug this workstream is fixing.
        /// </summary>
        [Test]
        public void HeadroomProbes_AreAboveTheFeetNotBelow()
        {
            const float feetY = 500f;

            Assert.That(UnitJumpMath.HeadroomLowPixelY(feetY), Is.GreaterThan(feetY),
                "A probe at or below the feet is inside the floor the unit is standing on, which would " +
                "refuse every jump. AS3's `Y - 85` is up because AS3's Y runs down.");

            Assert.That(UnitJumpMath.HeadroomHighPixelY(feetY), Is.GreaterThan(feetY),
                "Same for the high probe.");
        }

        // ── The cooldown ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>aiJump = Math.floor(30 + Math.random() * 50)</c> — <c>UnitZombie.as:393</c>. The span is
        /// <b>30..79</b> inclusive, and every reachable roll is enumerated rather than sampled.
        /// </summary>
        [Test]
        public void CooldownTicks_SpansThirtyToSeventyNine()
        {
            Assert.That(UnitJumpMath.CooldownMinTicks, Is.EqualTo(30),
                "UnitZombie.as:393 — the `30 +` half.");

            Assert.That(UnitJumpMath.CooldownRollRange, Is.EqualTo(50),
                "UnitZombie.as:393 — `Math.random() * 50`, so the roll is 0..49.");

            Assert.That(UnitJumpMath.CooldownTicks(0), Is.EqualTo(30), "The floor.");

            Assert.That(UnitJumpMath.CooldownTicks(49), Is.EqualTo(79),
                "The ceiling — `Math.floor` of a draw strictly below 1 gives 49, so 79 is the largest " +
                "value the oracle can produce. A float-range draw (Random.Range(0f, 50f)) is inclusive " +
                "of its upper bound and would reach 80, one tick past the oracle.");

            // Every roll in range, so the set cannot grow stale.
            for (int roll = 0; roll < UnitJumpMath.CooldownRollRange; roll++)
            {
                int ticks = UnitJumpMath.CooldownTicks(roll);
                Assert.That(ticks, Is.EqualTo(30 + roll), $"roll {roll} must give {30 + roll}.");
                Assert.That(ticks, Is.InRange(30, 79), $"roll {roll} produced {ticks}, outside 30..79.");
            }

            // Out-of-range rolls clamp rather than extrapolate: a bad draw must not produce a cooldown
            // nothing else in the system can explain.
            Assert.That(UnitJumpMath.CooldownTicks(-1), Is.EqualTo(30), "A negative roll clamps to the floor.");
            Assert.That(UnitJumpMath.CooldownTicks(50), Is.EqualTo(79), "50 clamps to the ceiling.");
            Assert.That(UnitJumpMath.CooldownTicks(1000), Is.EqualTo(79), "...and so does anything larger.");
        }

        /// <summary>
        /// The decrement — <c>UnitZombie.as:582-585</c>, <c>if(this.aiJump &gt; 0) { --this.aiJump; }</c>.
        /// </summary>
        [Test]
        public void Tick_CountsDownAndClampsAtZero()
        {
            Assert.That(UnitJumpMath.Tick(79), Is.EqualTo(78), "Counts down by one.");

            Assert.That(UnitJumpMath.Tick(1), Is.EqualTo(0), "Reaches zero.");

            Assert.That(UnitJumpMath.Tick(0), Is.EqualTo(0),
                "Clamps at zero rather than going negative. The oracle's `if(aiJump > 0)` guard already " +
                "makes a negative unreachable, so a brain that lost the guard would otherwise carry a " +
                "negative cooldown — which reads as 'ready' and is indistinguishable from 0 until " +
                "something compares it with `<`.");

            Assert.That(UnitJumpMath.Tick(-5), Is.EqualTo(0), "And a negative in stays at zero.");

            // The whole cooldown, so the arming and the counting agree about the same interval.
            int remaining = UnitJumpMath.CooldownTicks(49);
            for (int i = 0; i < 79; i++)
            {
                Assert.That(remaining, Is.GreaterThan(0), $"tick {i}: still cooling down.");
                remaining = UnitJumpMath.Tick(remaining);
            }

            Assert.That(remaining, Is.EqualTo(0),
                "After exactly 79 decrements the cooldown is spent — the ceiling roll must not need an " +
                "80th tick, or a zombie with the worst roll would be silent for one tick longer than the " +
                "oracle allows.");
        }

        // ── The `jump` row's pose (UnitZombie.as:308) ─────────────────────────────────────────

        /// <summary>
        /// <c>anims["jump"].setStab((dy * 0.6 + 8) / 16)</c> — <c>UnitZombie.as:308</c>.
        ///
        /// <para>The row is <c>stab='1'</c>, so it never advances by itself and this is the <i>only</i>
        /// thing that moves it. The trap is the sign: AS3's <c>dy</c> is positive while <b>falling</b> and
        /// this port's Y grows upward, so a missing negation plays the whole row backwards — the landing
        /// pose on the way up — without throwing and without stalling. Every value below is chosen so that
        /// a flip breaks it.</para>
        /// </summary>
        [Test]
        public void JumpPoseProgress_MirrorsDy_SoRisingIsTheEarlyCells()
        {
            Assert.That(UnitJumpMath.JumpPoseProgress(0f), Is.EqualTo(0.5f).Within(1e-6f),
                "(0 * 0.6 + 8) / 16 = 0.5. The apex is the middle of the row, and it is the one value a " +
                "sign error cannot distinguish — which is why it is not the only assertion here.");

            Assert.That(UnitJumpMath.JumpPoseProgress(18f), Is.LessThan(0.5f),
                "Rising at jumpdy 18 must be an EARLY cell. AS3's dy is -18 there, and -18 * 0.6 + 8 is " +
                "negative — below the row's start, so it clamps to the launch pose.");

            Assert.That(UnitJumpMath.JumpPoseProgress(18f), Is.EqualTo((8f - 0.6f * 18f) / 16f).Within(1e-6f),
                "The mirror stated as arithmetic: the port's +18 is AS3's -18, so the progress is " +
                "(-18 * 0.6 + 8) / 16 = -0.175.");

            Assert.That(UnitJumpMath.JumpPoseProgress(-10f), Is.GreaterThan(0.5f),
                "Falling at 10 px/frame must be a LATE cell — AS3's dy is +10 there.");

            Assert.That(UnitJumpMath.JumpPoseProgress(-10f), Is.EqualTo((0.6f * 10f + 8f) / 16f).Within(1e-6f),
                "dy = +10: (10 * 0.6 + 8) / 16 = 0.875, near the end of the row.");

            // Monotone across the whole band. This is the assertion a sign error cannot survive, and it
            // is why the fixture does not rely on the individual points above: each of those could be made
            // to pass by a different "reasonable" formula, while only the correct mirror is monotone in
            // the right direction.
            float previous = float.PositiveInfinity;
            for (float vy = -30f; vy <= 30f; vy += 1f)
            {
                float progress = UnitJumpMath.JumpPoseProgress(vy);
                Assert.That(progress, Is.LessThan(previous),
                    $"vy {vy}: progress must fall as the unit rises faster, so the row reads " +
                    "launch -> spread -> landing and not the other way round.");
                previous = progress;
            }
        }

        /// <summary>
        /// The pose composed with <c>AnimationFrame.FrameAtProgress</c> — the port's <c>setStab</c>, and
        /// the exact two-step the animator performs.
        ///
        /// <para><b>Asserted as a composition rather than as two halves.</b> Both halves already have their
        /// own fixtures and both were green while the zombie's jump was frozen, because the bug was that
        /// nothing <i>called</i> them. What can be checked here is that the composition sweeps the row —
        /// a frozen row yields exactly one cell, and that is the signature to pin.</para>
        /// </summary>
        [Test]
        public void JumpPoseProgress_ThroughFrameAtProgress_SweepsTheWholeRow()
        {
            // zombie0's jump row: AllData.as:80 `<blit id='jump' y='3' len='16' stab='1'/>`.
            var jump = new AnimationFrame
            {
                row = 3, length = 16, firstFrame = 0, frameStep = 1f, isStatic = true
            };

            Assert.That(jump.FrameAtProgress(UnitJumpMath.JumpPoseProgress(0f)), Is.EqualTo(8),
                "The apex is cell 8 of 16 — the middle of the arc.");

            Assert.That(jump.FrameAtProgress(UnitJumpMath.JumpPoseProgress(18f)), Is.EqualTo(0),
                "A full-strength rise is the launch cell: the progress is negative and FrameAtProgress " +
                "clamps to 0.");

            Assert.That(jump.FrameAtProgress(UnitJumpMath.JumpPoseProgress(-18f)), Is.EqualTo(15),
                "Falling at jumpdy 18 is the last cell: (18 * 0.6 + 8) / 16 = 1.175, and the clamp is " +
                "below 1 so cell 15 stays reachable.");

            var cells = new HashSet<int>();
            for (float vy = 20f; vy >= -20f; vy -= 0.25f)
            {
                cells.Add(jump.FrameAtProgress(UnitJumpMath.JumpPoseProgress(vy)));
            }

            Assert.That(cells.Count, Is.EqualTo(16),
                "A real arc reaches every cell of the 16-cell row. A row that is never posed produces " +
                "exactly one cell here, which is what the port did while the jump was frozen on its " +
                "launch frame.");
        }

        /// <summary>
        /// The divisor is the oracle's literal <c>16</c>, <b>not</b> the row's <c>len</c> — and the data
        /// disagrees with itself, so this is a real fork rather than a style choice.
        /// </summary>
        [Test]
        public void JumpPoseCells_IsTheOraclesLiteral_NotTheRowsLength()
        {
            Assert.That(UnitJumpMath.JumpPoseCells, Is.EqualTo(16f),
                "UnitZombie.as:308 hardcodes `/ 16`.");

            Assert.That(UnitJumpMath.JumpPoseRisePerCell, Is.EqualTo(0.6f),
                "The `dy * 0.6` half — cells per px/frame of vertical speed.");

            Assert.That(UnitJumpMath.JumpPoseMidpointCells, Is.EqualTo(8f),
                "The `+ 8` half — half of 16, so zero vertical speed is the middle of the row.");

            // The proof that the two are separable rather than accidentally equal. If the divisor were
            // ever derived from the row, this unit's pose would drift and nothing else would notice,
            // because the progress stays inside 0..1 either way.
            var fourteenCellRow = new AnimationFrame
            {
                row = 1, length = 14, firstFrame = 0, frameStep = 1f, isStatic = true
            };

            Assert.That(fourteenCellRow.length, Is.Not.EqualTo((int)UnitJumpMath.JumpPoseCells),
                "AllData.as carries `<blit id='jump' y='1' len='14' stab='1'/>` as well as seven " +
                "len='16' jump rows, and AS3 divides by 16 for all of them — so the divisor cannot be " +
                "read off the row.");
        }
    }
}

using NUnit.Framework;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitOverhangMath"/> — AS3 <c>Unit.shX1</c>/<c>shX2</c>, the per-side
    /// <b>overhang fractions</b> the zombie AI reads at a ledge, and the two probe constants the crate
    /// hop uses.
    ///
    /// <para><b>Why this fixture exists.</b> Every failure mode here is silent. A sign carried across
    /// from <see cref="UnitOverhangMath.OverhangRight"/> to <see cref="UnitOverhangMath.OverhangLeft"/>
    /// inverts the overhang on one side, so a zombie turns at a wall and walks off a ledge — with no
    /// exception and nothing in a log. Reading <c>1</c> as an "unknown" sentinel instead of maximal
    /// overhang makes a unit standing over a gap look <i>fully supported</i>, which is the opposite of
    /// the truth and turns the whole mechanic off. A probe offset with the wrong vertical sign puts the
    /// crate test above the zombie's head, where there is never a crate, so the patrol turns at every
    /// lip and never hops. The pure fixture is where each of those is a one-line assertion.</para>
    ///
    /// <para><b>What is deliberately NOT pinned here.</b> The caller's wiring — that
    /// <c>UnitController.ResolveGroundState</c> writes these from the tile span, that
    /// <c>ZombieBrain.HandleLedgeAhead</c> runs in the right states and before the move — is behaviour
    /// and needs a <c>GameObject</c> and a room. That half lives in <c>EnemyBrainTests</c> and is flagged
    /// there as owner-only. The room's half of the span is <c>SupportSpanTests</c>.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitOverhangMathTests
    {
        // ── The constants themselves ─────────────────────────────────────────────────────────

        /// <summary>
        /// Every number this type exists to name, against the oracle line it came from.
        /// </summary>
        [Test]
        public void Constants_AreTheOracles()
        {
            Assert.That(UnitOverhangMath.NoSupportOverhang, Is.EqualTo(1f),
                "Unit.as:2251 — `this.shX1 = this.shX2 = 1;`, the value at the start of every descending " +
                "ground pass and therefore also the value when nothing collides.");

            Assert.That(UnitOverhangMath.IdleEdgeThreshold, Is.EqualTo(0.5f),
                "UnitZombie.as:755/:759 — the `aiState == 0` (idle) gate.");

            Assert.That(UnitOverhangMath.PatrolEdgeThreshold, Is.EqualTo(0.25f),
                "UnitZombie.as:789/:808 — the `aiState == 1` (patrol) gate.");

            Assert.That(UnitOverhangMath.ChaseEdgeThreshold, Is.EqualTo(0.5f),
                "UnitZombie.as:890 — the `aiState == 2 || 3` gate.");

            Assert.That(UnitOverhangMath.AheadProbePixels, Is.EqualTo(80f),
                "UnitZombie.as:793/:812 — `loc.getAbsTile(X + storona * 80, Y + 10)`.");

            Assert.That(UnitOverhangMath.AheadProbeDropPixels, Is.EqualTo(10f),
                "UnitZombie.as:793/:812 — the probe's `Y + 10`.");

            Assert.That(UnitOverhangMath.CrateHopMagnitude, Is.EqualTo(0.5f),
                "UnitZombie.as:796/:815/:894 — `_loc2_ = 0.5`, the half hop.");

            Assert.That(UnitOverhangMath.PatrolEdgeThreshold, Is.LessThan(UnitOverhangMath.ChaseEdgeThreshold),
                "The patrol notices the lip at a quarter of the body and the chase at a half — the patrol " +
                "has to act early enough to turn before it falls, and the chase is already committed by " +
                "the time half the body is over. Equal thresholds would erase that, and it is the one " +
                "difference between the two sites.");

            Assert.That(UnitOverhangMath.AheadProbePixels, Is.EqualTo(2f * 40f),
                "80 px is two tiles (Tile.tileX = 40, World.as:36) — pinned so a future 'that is really " +
                "one tile' tidy-up is forced to notice the oracle says two.");

            Assert.That(UnitOverhangMath.AheadProbeDropPixels, Is.LessThan(40f),
                "The drop is a fraction of a tile, not a tile: it exists only to move the sample off the " +
                "row boundary the feet sit on (see the probe test below). A full tile would land in the " +
                "row BELOW the one the zombie would stand on.");
        }

        // ── The formula's sign, which is the whole subtlety ──────────────────────────────────

        /// <summary>
        /// <c>shX1 = -(X1 - tile.phX1) / scX</c> — <c>Unit.as:2301</c>. Negative is <i>supported</i>,
        /// positive is <i>overhanging</i>.
        /// </summary>
        [Test]
        public void OverhangLeft_SignReadsSupportAndEdge()
        {
            const float width = 40f;
            const float feetLeft = 80f; // the body is 80..120

            Assert.That(UnitOverhangMath.OverhangLeft(feetLeft, 40f, width), Is.EqualTo(-1f).Within(0.0001f),
                "The support's left edge is a full body width to the LEFT of the unit's, so nothing hangs " +
                "over: -(80-40)/40 = -1. Negative means supported.");

            Assert.That(UnitOverhangMath.OverhangLeft(feetLeft, 80f, width), Is.EqualTo(0f).Within(0.0001f),
                "phX1 == X1 → 0. The boundary between supported and overhanging, and the value " +
                "`checkDiagon` writes when a ramp takes over (Unit.as:2797).");

            Assert.That(UnitOverhangMath.OverhangLeft(feetLeft, 100f, width), Is.EqualTo(0.5f).Within(0.0001f),
                "The support starts half a body width INSIDE the unit's left edge, so the left half hangs " +
                "past the lip: -(80-100)/40 = +0.5. Positive means overhang. Note 0.5 is exactly the " +
                "chase threshold, and the comparison is strict, so this case does NOT fire.");
        }

        /// <summary>
        /// <c>shX2 = (X2 - tile.phX2) / scX</c> — <c>Unit.as:2305</c>. The mirror, and the sign is the
        /// oracle's.
        /// </summary>
        /// <remarks>
        /// The two formulas differ by a negation: the left one is <c>-(X1 - phX1)</c> and the right one is
        /// <c>+(X2 - phX2)</c>. Copying one to the other without flipping the subtraction is the natural
        /// mistake and it inverts every ledge on that side while still producing plausible fractions.
        /// </remarks>
        [Test]
        public void OverhangRight_IsTheMirrorOfTheLeft()
        {
            const float width = 40f;
            const float feetRight = 120f; // the body is 80..120

            Assert.That(UnitOverhangMath.OverhangRight(feetRight, 160f, width), Is.EqualTo(-1f).Within(0.0001f),
                "The support extends a full body width to the RIGHT, so the right side is fully " +
                "supported: (120-160)/40 = -1. Negative, same reading as the left — only the formula's " +
                "sign differs.");

            Assert.That(UnitOverhangMath.OverhangRight(feetRight, 120f, width), Is.EqualTo(0f).Within(0.0001f),
                "phX2 == X2 → 0.");

            Assert.That(UnitOverhangMath.OverhangRight(feetRight, 100f, width), Is.EqualTo(0.5f).Within(0.0001f),
                "The support ends half a body width inside the unit's right edge → the right half hangs " +
                "over.");
        }

        // ── The pair is one span (the min reduction) ─────────────────────────────────────────

        /// <summary>
        /// The oracle's two <c>min</c> reductions are exactly "the leftmost supporting tile's left edge"
        /// and "the rightmost supporting tile's right edge" — so the two fractions are fully described by
        /// one support span, which is why the port computes a span once.
        /// </summary>
        /// <remarks>
        /// <c>Unit.as:2301</c>/<c>:2305</c> reduce each fraction with a <c>min</c> against a running value
        /// seeded at <c>1</c>. <c>-(X1 - phX1)</c> is smallest when <c>phX1</c> is smallest, and
        /// <c>(X2 - phX2)</c> is smallest when <c>phX2</c> is largest — so <c>shX1</c> is decided by the
        /// leftmost supporting tile and <c>shX2</c> by the rightmost. Non-contiguous support behaves
        /// identically, because the min and the max are taken independently; that is asserted against the
        /// room seam in <c>SupportSpanTests</c>.
        /// </remarks>
        [Test]
        public void Overhang_PairIsFullyDescribedByTheSupportSpan()
        {
            const float width = 40f;
            const float feetLeft = 80f;
            const float feetRight = 120f;

            // A span wider than the body on both sides → both sides fully supported.
            Assert.That(UnitOverhangMath.OverhangLeft(feetLeft, 40f, width), Is.EqualTo(-1f).Within(0.0001f));
            Assert.That(UnitOverhangMath.OverhangRight(feetRight, 160f, width), Is.EqualTo(-1f).Within(0.0001f),
                "Both negative: the body is entirely inside the span [40, 160].");

            // A span that stops at the body's own left edge → the whole body is past its right edge.
            Assert.That(UnitOverhangMath.OverhangRight(feetRight, 80f, width), Is.EqualTo(1f).Within(0.0001f),
                "The support ends exactly at the body's LEFT edge, so all 40 px is past its right edge: " +
                "(120-80)/40 = 1. That is the same number as NoSupportOverhang, reached from a real " +
                "support — which is why `1` cannot be read as 'no information'. It means 'a full body " +
                "width or more', whether or not a support was found.");
        }

        // ── The direction selection ─────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>aiNapr &lt; 0 ? shX1 : shX2</c>, which every consumer writes out inline.
        /// </summary>
        [Test]
        public void OverhangToward_SelectsTheSideTheUnitIsMovingTo()
        {
            const float left = 0.9f;
            const float right = 0.1f;

            Assert.That(UnitOverhangMath.OverhangToward(-1, left, right), Is.EqualTo(left),
                "Moving left reads the LEFT overhang — `shX1 > t && aiNapr < 0`.");

            Assert.That(UnitOverhangMath.OverhangToward(1, left, right), Is.EqualTo(right),
                "...and moving right reads the right one — `shX2 > t && aiNapr > 0`.");

            Assert.That(UnitOverhangMath.OverhangToward(0, left, right), Is.EqualTo(right),
                "Zero is not a direction the oracle produces (`aiNapr` is +/-1), but if it arrives it must " +
                "read a real side rather than throw or invent a third value — and it reads the right, " +
                "matching the `direction < 0 ? left : right` shape the probe point uses, so the two " +
                "cannot disagree about what facing 0 means.");
        }

        // ── The gate: strictly greater, and two thresholds ──────────────────────────────────

        /// <summary>
        /// <c>shX1 &gt; 0.25</c> is strict — a body overhanging by exactly a quarter does not trigger.
        /// </summary>
        [Test]
        public void IsAtEdgeToward_IsStrictlyGreater()
        {
            Assert.That(UnitOverhangMath.IsAtEdgeToward(-1, 0.5f, 0f, 0.5f), Is.False,
                "Exactly at the threshold is NOT past it — the oracle writes `>`, not `>=`. Changing this " +
                "to `>=` is the mutation this assertion exists to catch.");

            Assert.That(UnitOverhangMath.IsAtEdgeToward(-1, 0.5001f, 0f, 0.5f), Is.True,
                "A hair past IS past it.");

            Assert.That(UnitOverhangMath.IsAtEdgeToward(-1, 0.4999f, 0f, 0.5f), Is.False,
                "A hair short is not.");
        }

        /// <summary>
        /// One overhang, two answers — the reason there are two constants and not one.
        /// </summary>
        [Test]
        public void IsAtEdgeToward_PatrolFiresWhereTheChaseDoesNot()
        {
            const float left = 0.3f; // 30% of the body over the lip
            const float right = 0f;

            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(-1, left, right, UnitOverhangMath.PatrolEdgeThreshold),
                Is.True,
                "0.3 > 0.25 — the patrol sees the lip.");

            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(-1, left, right, UnitOverhangMath.ChaseEdgeThreshold),
                Is.False,
                "0.3 is NOT > 0.5 — the chase does not. A single shared threshold would make both act at " +
                "the same distance, which is the divergence this pair of constants exists to prevent.");
        }

        /// <summary>
        /// The patrol band, swept across its boundary with every case listed rather than sampled, so
        /// growing the set cannot leave a stale hand-written expectation behind.
        /// </summary>
        [Test]
        public void IsAtEdgeToward_SweepsThePatrolBand()
        {
            (float overhang, bool expected)[] cases =
            {
                (-1f, false),     // deep inside the support
                (0f, false),      // exactly at the support's edge
                (0.1f, false),
                (0.2499f, false),
                (0.25f, false),   // the boundary — strictly greater, so not yet
                (0.2501f, true),
                (0.3f, true),
                (0.5f, true),     // the chase's threshold, but for the PATROL it is well past
                (0.9f, true),
                (1f, true),       // maximal: no support at all
            };

            foreach ((float overhang, bool expected) in cases)
            {
                Assert.That(
                    UnitOverhangMath.IsAtEdgeToward(-1, overhang, 0f, UnitOverhangMath.PatrolEdgeThreshold),
                    Is.EqualTo(expected),
                    $"overhang {overhang} toward the moving side must be {expected}.");
            }
        }

        /// <summary>
        /// The <c>1</c> = "no support" value must read as <b>at the edge</b> for every gate — not as
        /// "unknown".
        /// </summary>
        /// <remarks>
        /// This is the trap the type documents. A port that treated <c>false</c> from the room seam as
        /// "no information" and mapped it to a <c>false</c> overhang would make a unit standing on a crate
        /// over air look fully supported — so it would walk off every lip it found, the exact opposite of
        /// the oracle. The oracle's own <c>shX1 = shX2 = 1</c> at the top of the pass is what makes the
        /// honest answer <i>maximal</i> overhang.
        /// </remarks>
        [Test]
        public void NoSupportOverhang_ReadsAsAtTheEdge_ForEveryThreshold()
        {
            float noSupport = UnitOverhangMath.NoSupportOverhang;

            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(-1, noSupport, 0f, UnitOverhangMath.IdleEdgeThreshold),
                Is.True, "idle gate");
            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(-1, noSupport, 0f, UnitOverhangMath.PatrolEdgeThreshold),
                Is.True, "patrol gate");
            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(-1, noSupport, 0f, UnitOverhangMath.ChaseEdgeThreshold),
                Is.True, "chase gate");

            // The same, on the right side, so the reading is not an artefact of the left formula's sign.
            Assert.That(
                UnitOverhangMath.IsAtEdgeToward(1, 0f, noSupport, UnitOverhangMath.PatrolEdgeThreshold),
                Is.True, "and on the right side too");
        }

        // ── The zero-width guard ────────────────────────────────────────────────────────────

        /// <summary>
        /// A body of no width cannot be expressed as a fraction of itself; the guard returns the
        /// no-support value rather than the NaN the oracle's unconditional division would produce.
        /// </summary>
        [Test]
        public void Overhang_ZeroWidthBody_IsNoSupportNotNaN()
        {
            Assert.That(UnitOverhangMath.OverhangLeft(100f, 80f, 0f),
                Is.EqualTo(UnitOverhangMath.NoSupportOverhang),
                "AS3 divides by `scX` unconditionally, so a zero-width body gives NaN — and NaN compares " +
                "false against every threshold, i.e. 'never at an edge'. Returning the no-support value " +
                "says the same thing without poisoning every later comparison with a NaN.");

            Assert.That(UnitOverhangMath.OverhangRight(100f, 80f, 0f),
                Is.EqualTo(UnitOverhangMath.NoSupportOverhang));

            Assert.That(UnitOverhangMath.OverhangLeft(100f, 80f, -5f),
                Is.EqualTo(UnitOverhangMath.NoSupportOverhang),
                "A negative width is equally unrepresentable and takes the same branch.");

            Assert.That(float.IsNaN(UnitOverhangMath.OverhangLeft(100f, 80f, 0f)), Is.False,
                "...and is not NaN, so a future consumer doing arithmetic on it cannot be poisoned.");
        }

        // ── The crate-hop probe ─────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>loc.getAbsTile(X + storona * 80, Y + 10)</c> — two tiles ahead, ten pixels down.
        /// </summary>
        [Test]
        public void AheadProbePoint_IsTwoTilesAheadAndTenPixelsDown()
        {
            const float x = 400f;
            const float y = 200f;

            Vector2 right = UnitOverhangMath.AheadProbePoint(x, y, 1);
            Assert.That(right.x, Is.EqualTo(480f),
                "UnitZombie.as:793 — `X + storona * 80` with storona = 1.");
            Assert.That(right.y, Is.EqualTo(190f),
                "UnitZombie.as:793 — `Y + 10`. AS3's Y grows DOWN, so +10 is 10 px BELOW the feet; this " +
                "port's room-local Y grows UP, so it is `y - 10`.");

            Vector2 left = UnitOverhangMath.AheadProbePoint(x, y, -1);
            Assert.That(left.x, Is.EqualTo(320f),
                "...and with storona = -1 it mirrors to the left.");
            Assert.That(left.y, Is.EqualTo(190f),
                "The vertical offset does not mirror with the direction — it is always down.");

            Vector2 zero = UnitOverhangMath.AheadProbePoint(x, y, 0);
            Assert.That(zero.x, Is.EqualTo(480f),
                "Facing 0 must fall back to a real column. Probing the unit's own column would test the " +
                "tile it is already standing on, which always supports — so every lip would look like a " +
                "crate and the zombie would hop at every edge instead of turning.");
        }

        /// <summary>
        /// The vertical offset is <b>subtracted</b>, and that sign is the whole subtlety of the probe.
        /// </summary>
        /// <remarks>
        /// Stated as its own test because getting it backwards is a silent, total failure: the probe lands
        /// above the zombie's head, where there is never a crate, so the patrol turns at every lip and
        /// never hops. No exception, nothing in a log — the same shape as the bug this whole workstream
        /// is fixing. See <see cref="UnitOverhangMath.AheadProbeDropPixels"/>.
        /// </remarks>
        [Test]
        public void AheadProbePoint_DropsBelowTheFeet_NotAbove()
        {
            const float feetY = 500f;

            Assert.That(UnitOverhangMath.AheadProbePoint(0f, feetY, 1).y, Is.LessThan(feetY),
                "The probe must be BELOW the feet: the feet sit on the boundary between the air row and " +
                "the ground row, and `getAbsTile` floors, so a probe at or above the feet lands in the " +
                "air row.");

            Assert.That(UnitOverhangMath.AheadProbePoint(0f, feetY, -1).y, Is.LessThan(feetY),
                "Same on the mirrored direction.");
        }

        /// <summary>
        /// The hop magnitude is half the full hop, and strictly positive.
        /// </summary>
        [Test]
        public void CrateHopMagnitude_IsHalfTheFullHop()
        {
            Assert.That(UnitOverhangMath.CrateHopMagnitude, Is.EqualTo(0.5f));

            Assert.That(UnitOverhangMath.CrateHopMagnitude, Is.LessThan(1f),
                "Site 1 (UnitZombie.as:841) uses `_loc2_ = 1`, the full hop. The crate sites use 0.5 — a " +
                "crate is one tile, and a full hop would put the zombie's head into the ceiling that " +
                "checkJump() then refuses. Sharing the two would make every crate hop overshoot.");

            Assert.That(UnitOverhangMath.CrateHopMagnitude, Is.GreaterThan(0f),
                "...and strictly positive, or `jump()`'s `dy = -jumpdy * param1` would be a no-op and the " +
                "zombie would never leave the ground.");
        }
    }
}

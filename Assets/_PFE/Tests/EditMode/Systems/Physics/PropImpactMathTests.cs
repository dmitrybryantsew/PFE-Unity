using NUnit.Framework;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins <see cref="PropImpactMath"/> — the arithmetic of AS3 <c>Box.attDrop</c>
    /// (<c>fe/loc/Box.as:914-940</c>) and <c>Unit.udarBox</c> (<c>fe/unit/Unit.as:4209-4240</c>).
    ///
    /// <para><b>Every assertion names the wrong value it guards against.</b> The reason this
    /// arithmetic is worth a fixture is that all four of its numbers invert into something plausible:
    /// the 50 threshold read as a per-second speed is 900× too small (every settling crate damages
    /// whatever it touches), the 0.25 prop reflection read as the unit's is a 4× knockback, the
    /// <c>massa</c> scale read as <c>GetResolvedMass</c> is 50× damage, and <c>neujazMax</c> read as
    /// the throw grace is 67% too long. None of those throws; all of them are gameplay.</para>
    ///
    /// <para><b>Two gates, not one.</b> The oracle refuses the sweep at <c>:632</c> (any axis beyond
    /// ±5 px/frame) and then refuses the impact at <c>:918</c> (<c>vel2 &lt; 50</c>). They are
    /// <i>nearly</i> the same test, and the corner where they differ — <c>|dx| = |dy| = 5</c> exactly,
    /// where <c>vel2</c> is exactly 50 and would pass — is pinned below so nobody "simplifies" one
    /// into the other.</para>
    /// </summary>
    [TestFixture]
    public sealed class PropImpactMathTests
    {
        private const float Tolerance = 1e-4f;

        // ── The constants ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>World.boxDamage = 0.2</c> (<c>World.as:64</c>). The scale on the whole formula.
        /// </summary>
        [Test]
        public void BoxDamageCoefficient_IsWorldBoxDamage()
        {
            Assert.That(PropImpactMath.BoxDamageCoefficient, Is.EqualTo(0.2f).Within(Tolerance),
                "World.as:64 is `boxDamage = 0.2`.");

            Assert.That(PropImpactMath.BoxDamageCoefficient, Is.Not.EqualTo(1f).Within(Tolerance),
                "1 would make the coefficient a no-op — a crate would do 5x AS3's damage.");
        }

        /// <summary>
        /// <c>Box.as:918</c> is <c>vel2 &lt; 50</c>, and <c>vel2</c> is px <b>per frame</b> squared.
        /// So the threshold is a speed of <c>√50 ≈ 7.071</c> px/frame, i.e. ≈ 212 px/s — not 50 px/s
        /// and not 50 px/frame.
        /// </summary>
        [Test]
        public void MinimumImpactVelocitySquared_IsFiftyPixelsPerFrameSquared()
        {
            Assert.That(PropImpactMath.MinimumImpactVelocitySquared, Is.EqualTo(50f).Within(Tolerance),
                "Box.as:918 — `if(this.vel2 < 50) return;`.");

            Assert.That(Mathf.Sqrt(PropImpactMath.MinimumImpactVelocitySquared),
                Is.EqualTo(7.0711f).Within(1e-3f),
                "In px/frame the threshold is a speed of sqrt(50) ~ 7.07, i.e. ~212 px/s.");

            Assert.That(PropImpactMath.MinimumImpactVelocitySquared, Is.Not.EqualTo(1500f).Within(Tolerance),
                "1500 is 50 px/frame x 30 — the threshold converted the WRONG WAY, as if it were a " +
                "speed that needed scaling up to px/s. Comparing a px/s velocity against 1500 makes " +
                "the gate unreachable for anything but a plummet.");
        }

        /// <summary><c>Box.as:632</c> — the ±5 px/frame pre-filter, in AS3's own unit.</summary>
        [Test]
        public void ImpactSweepMinimumSpeed_IsFivePixelsPerFrame()
        {
            Assert.That(PropImpactMath.ImpactSweepMinimumSpeedPixelsPerFrame,
                Is.EqualTo(5f).Within(Tolerance), "Box.as:632 — `dy > 5 || dy < -5 || dx > 5 || dx < -5`.");

            Assert.That(PropImpactMath.ImpactSweepMinimumSpeedPixelsPerFrame,
                Is.Not.EqualTo(150f).Within(Tolerance),
                "150 is 5 px/frame x 30. Feeding this a px/s velocity would refuse every crate that " +
                "is actually falling — the same unit error as the 50 threshold, one gate up.");
        }

        /// <summary>
        /// <c>Box.as:930</c> — the throw grace is a <b>literal 12</b>, not <c>neujazMax</c>. The two
        /// are different numbers and AS3 uses the literal.
        /// </summary>
        [Test]
        public void ThrowGraceInvulnerabilityTicks_IsTwelve_NotNeujazMax()
        {
            Assert.That(PropImpactMath.ThrowGraceInvulnerabilityTicks, Is.EqualTo(12),
                "Box.as:930 — `_loc1_.neujaz = 12;`.");

            Assert.That(PropImpactMath.ThrowGraceInvulnerabilityTicks, Is.Not.EqualTo(20),
                "20 is Unit.neujazMax (Unit.as:394) — the generic contact grace. Using it here would " +
                "make the throw window 67% longer, so a thrown crate would keep passing through " +
                "people for 8 extra ticks.");
        }

        // ── Unit conversion ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The prop layer integrates in px/<b>second</b>; every comparison in this file is in px per
        /// 30 Hz <b>frame</b>. The conversion happens once, at the boundary.
        /// </summary>
        [Test]
        public void ToPixelsPerFrame_DividesByTheCanonicalTickRate()
        {
            Vector2 perFrame = PropImpactMath.ToPixelsPerFrame(new Vector2(600f, 300f));

            Assert.That(perFrame.x, Is.EqualTo(20f).Within(Tolerance), "600 px/s at 30 Hz is 20 px/frame.");
            Assert.That(perFrame.y, Is.EqualTo(10f).Within(Tolerance), "300 px/s at 30 Hz is 10 px/frame.");
        }

        /// <summary>
        /// <c>VelocitySquaredPixelsPerFrame</c> must produce AS3's <c>vel2</c>, and the wrong value it
        /// guards against is the same quantity left in px/s — 900× larger, which passes the 50
        /// threshold for a crate that is barely drifting.
        /// </summary>
        [Test]
        public void VelocitySquaredPixelsPerFrame_IsInPerFrameUnits_NotPerSecond()
        {
            float vel2 = PropImpactMath.VelocitySquaredPixelsPerFrame(new Vector2(600f, 0f));

            Assert.That(vel2, Is.EqualTo(400f).Within(Tolerance),
                "600 px/s = 20 px/frame; 20^2 = 400.");

            Assert.That(vel2, Is.Not.EqualTo(360000f).Within(Tolerance),
                "360000 is 600^2 — the velocity left in px/s. Every impact would clear the 50 gate, " +
                "so a crate nudged by a pixel would wound whatever it touched.");
        }

        /// <summary>
        /// <c>Box.as:632</c>'s gate, in AS3's unit. <b>Strict</b> on all four comparisons — exactly
        /// ±5 on an axis does not sweep.
        /// </summary>
        [Test]
        public void IsMovingFastEnoughToSweep_ExactlyFive_DoesNotSweep()
        {
            Assert.That(PropImpactMath.IsMovingFastEnoughToSweep(new Vector2(5f, 5f)), Is.False,
                "`dx > 5` and `dy > 5` are both false at exactly 5 — the oracle's comparisons are strict.");

            Assert.That(PropImpactMath.IsMovingFastEnoughToSweep(new Vector2(-5f, -5f)), Is.False,
                "`dx < -5` and `dy < -5` are likewise false at exactly -5.");

            Assert.That(PropImpactMath.IsMovingFastEnoughToSweep(new Vector2(5.001f, 0f)), Is.True,
                "A hair over 5 on one axis is enough, regardless of the other axis.");

            Assert.That(PropImpactMath.IsMovingFastEnoughToSweep(new Vector2(-5.001f, 0f)), Is.True,
                "The gate is symmetric — a crate moving up fast is as dangerous as one falling.");
        }

        /// <summary>
        /// <c>Box.as:918</c>'s gate. Inclusive: <c>vel2 == 50</c> passes, because the oracle writes
        /// <c>&lt; 50</c>.
        /// </summary>
        [Test]
        public void IsImpactHardEnough_AtExactlyFifty_Passes()
        {
            Assert.That(PropImpactMath.IsImpactHardEnough(50f), Is.True,
                "`if(this.vel2 < 50) return;` — 50 is not less than 50, so the sweep continues.");

            Assert.That(PropImpactMath.IsImpactHardEnough(49.999f), Is.False,
                "Just under the threshold is refused.");

            Assert.That(PropImpactMath.IsImpactHardEnough(0f), Is.False, "A stationary prop is refused.");
        }

        /// <summary>
        /// <b>The one point where the two gates disagree.</b> At <c>|dx| = |dy| = 5</c> exactly,
        /// <c>vel2</c> is exactly 50 and <see cref="PropImpactMath.IsImpactHardEnough"/> says yes —
        /// but <c>:632</c> refuses the sweep before <c>:918</c> is reached. Pinned so that "these two
        /// gates are the same, merge them" fails here rather than silently changing behaviour.
        /// </summary>
        [Test]
        public void TheTwoGates_DisagreeAtTheExactCorner()
        {
            var corner = new Vector2(5f, 5f);
            float vel2 = corner.x * corner.x + corner.y * corner.y;

            Assert.That(vel2, Is.EqualTo(PropImpactMath.MinimumImpactVelocitySquared).Within(Tolerance),
                "2 x 5^2 is exactly the vel2 threshold — the corner is the boundary, not near it.");

            Assert.That(PropImpactMath.IsImpactHardEnough(vel2), Is.True,
                "The vel2 gate alone would let this impact through.");

            Assert.That(PropImpactMath.IsMovingFastEnoughToSweep(corner), Is.False,
                "The sweep gate refuses it first. The two gates are NOT interchangeable: the sweep " +
                "gate is stricter at this corner, and looser everywhere outside the 5x5 square.");
        }

        // ── ImpactDamage ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Unit.as:4237</c> — <c>param1.massa * (param1.vel2 - 50) * World.boxDamage</c>. The
        /// <c>- 50</c> is the point: at the threshold the damage is exactly zero, not 10.
        /// </summary>
        [Test]
        public void ImpactDamage_AtTheThreshold_IsZero()
        {
            Assert.That(PropImpactMath.ImpactDamage(1f, 50f), Is.EqualTo(0f).Within(Tolerance),
                "`vel2 - 50` is the margin above the threshold, so at the threshold the damage is 0.");

            Assert.That(PropImpactMath.ImpactDamage(1f, 50f), Is.Not.EqualTo(10f).Within(Tolerance),
                "10 would be `massa * vel2 * boxDamage` — the `- 50` dropped, which makes every " +
                "grazing touch wound for a flat 10.");
        }

        /// <summary>Both inputs scale it: <c>massa * margin * 0.2</c>.</summary>
        [Test]
        public void ImpactDamage_ScalesWithMassaAndWithTheMargin()
        {
            Assert.That(PropImpactMath.ImpactDamage(1f, 100f), Is.EqualTo(10f).Within(Tolerance),
                "1 * (100 - 50) * 0.2 = 10.");

            Assert.That(PropImpactMath.ImpactDamage(2f, 100f), Is.EqualTo(20f).Within(Tolerance),
                "Doubling the mass doubles the damage.");

            Assert.That(PropImpactMath.ImpactDamage(1f, 150f), Is.EqualTo(20f).Within(Tolerance),
                "Doubling the margin doubles the damage — a crate falling twice as fast hurts twice " +
                "as much, not four times (vel2 is already the square).");
        }

        /// <summary>
        /// <b>The massa scale.</b> <c>ObjectInstance.GetAs3Massa()</c> is already ÷50;
        /// <c>GetResolvedMass()</c> is 50× larger. Passing the latter would multiply every prop's
        /// impact damage by 50.
        /// </summary>
        [Test]
        public void ImpactDamage_ExpectsTheAs3MassaScale_NotGetResolvedMass()
        {
            Assert.That(PropImpactMath.ImpactDamage(1f, 100f), Is.EqualTo(10f).Within(Tolerance),
                "AS3-scale: a mass-1 crate at vel2 100 does 10.");

            Assert.That(PropImpactMath.ImpactDamage(50f, 100f), Is.EqualTo(500f).Within(Tolerance),
                "The same crate read as GetResolvedMass() (50x) does 500. That is the trap: the two " +
                "scales are both named `massa` and only one belongs in this formula.");

            Assert.That(PropImpactMath.ImpactDamage(1f, 100f),
                Is.Not.EqualTo(PropImpactMath.ImpactDamage(50f, 100f)).Within(Tolerance),
                "If these ever became equal the scale distinction has been erased.");
        }

        /// <summary>
        /// A soft impact returns 0 rather than a negative. AS3's <c>damage()</c> <i>heals</i> on a
        /// negative amount (<c>Unit.as:3605-3608</c>), and that branch is unreachable in the oracle
        /// only because <c>:918</c> returns first — so a caller that skips the sweep gate must not be
        /// handed a heal.
        /// </summary>
        [Test]
        public void ImpactDamage_BelowTheThreshold_IsZero_NotNegative()
        {
            Assert.That(PropImpactMath.ImpactDamage(1f, 20f), Is.EqualTo(0f).Within(Tolerance),
                "20 - 50 = -30; left unclamped that is a NEGATIVE damage, which AS3's damage() turns " +
                "into a heal.");

            Assert.That(PropImpactMath.ImpactDamage(1f, 20f), Is.GreaterThanOrEqualTo(0f),
                "Never negative.");
        }

        /// <summary>A prop with no mass does no damage — and must not produce NaN or a negative.</summary>
        /// <remarks>
        /// <para><b>The zero case is an <i>equivalent mutant</i> and is recorded as one rather than
        /// covered.</b> Relaxing the guard from <c>massa &lt;= 0</c> to <c>massa &lt; 0</c> changes
        /// nothing: with the guard open, <c>0 * (vel2 - 50) * 0.2</c> is <c>0.0</c> for every finite
        /// <c>vel2</c> (and <c>-0.0</c> below the threshold, which still compares equal to <c>0</c>).
        /// A mutation run confirmed it survives, and no honest assertion can distinguish it.</para>
        ///
        /// <para><b>What the guard actually buys is the negative case.</b> A negative <c>massa</c>
        /// would make the product negative, and AS3's <c>damage()</c> turns a negative amount into a
        /// <i>heal</i> (<c>Unit.as:3605-3608</c>) — so the guard is load-bearing, just not at zero.
        /// The <c>&lt;=</c> is kept because "a non-positive mass does nothing" is the clearer
        /// statement of intent than "a negative mass does nothing".</para>
        /// </remarks>
        [Test]
        public void ImpactDamage_WithNonPositiveMassa_IsZero()
        {
            Assert.That(PropImpactMath.ImpactDamage(0f, 400f), Is.EqualTo(0f).Within(Tolerance),
                "Zero mass: AS3 would compute 0 damage; a divide-by-mass reading would blow up.");

            Assert.That(PropImpactMath.ImpactDamage(-3f, 400f), Is.EqualTo(0f).Within(Tolerance),
                "A negative mass would otherwise produce a negative damage, i.e. a heal.");

            Assert.That(float.IsNaN(PropImpactMath.ImpactDamage(0f, 400f)), Is.False, "No NaN.");
        }

        /// <summary>
        /// <b>The damage reads the PRE-collision velocity.</b> <c>param1.vel2</c> is computed once at
        /// <c>Box.as:917</c> and never recomputed, so the momentum exchange at <c>:4229-4230</c> —
        /// which rewrites <c>param1.dx</c>/<c>dy</c> — does not feed back into it. A refactor that
        /// resolved the exchange first and read <c>vel2</c> afterwards would weaken every impact in
        /// proportion to how much speed the crate kept.
        /// </summary>
        [Test]
        public void ImpactDamage_ReadsThePreCollisionVelocity_NotThePostExchangeOne()
        {
            var propVelocity = new Vector2(0f, 20f); // px/frame, falling onto a unit
            Vector2 unitVelocity = Vector2.zero;

            float preVel2 = PropImpactMath.VelocitySquaredPixelsPerFrame(
                PropImpactMath.FromPixelsPerFrame(propVelocity));

            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                unitVelocity, 1f, 1f, false, propVelocity, 1f);

            float postVel2 = PropImpactMath.VelocitySquaredPixelsPerFrame(
                PropImpactMath.FromPixelsPerFrame(exchange.PropVelocityPixelsPerFrame));

            Assert.That(preVel2, Is.EqualTo(400f).Within(Tolerance), "20^2 = 400.");
            Assert.That(postVel2, Is.LessThan(preVel2),
                "The exchange slows the crate — that is what makes the two readings differ at all.");

            Assert.That(PropImpactMath.ImpactDamage(1f, preVel2), Is.EqualTo(70f).Within(Tolerance),
                "AS3 reads vel2 as computed at :917: (400 - 50) * 0.2 = 70.");

            Assert.That(PropImpactMath.ImpactDamage(1f, preVel2),
                Is.Not.EqualTo(PropImpactMath.ImpactDamage(1f, postVel2)).Within(Tolerance),
                "Reading the post-exchange velocity would give a different, smaller number — which " +
                "is exactly the silent behaviour change this test exists to catch.");
        }

        // ── ResolveExchange ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Equal masses head-on with <c>knocked = 1</c>: the unit's reflection is <b>full</b> (it
        /// takes the prop's velocity), while the prop's is scaled by a flat <b>0.25</b>.
        ///
        /// <para>The two sides use <i>different</i> coefficients — the unit's own <c>knocked</c> and a
        /// hard-coded 0.25 for the prop. Reading 0.25 as the unit's coefficient would quarter every
        /// knockback.</para>
        /// </summary>
        [Test]
        public void ResolveExchange_EqualMassesHeadOn_UnitTakesThePropsVelocity_PropKeepsAQuarter()
        {
            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                unitVelocityPixelsPerFrame: new Vector2(10f, 0f),
                unitMassa: 1f,
                unitKnocked: 1f,
                unitIsFixed: false,
                propVelocityPixelsPerFrame: new Vector2(-10f, 0f),
                propMassa: 1f);

            Assert.That(exchange.UnitVelocityPixelsPerFrame.x, Is.EqualTo(-10f).Within(Tolerance),
                "Centre of mass is 0, so `(-10 + 0) * 1 + 0 = -10` — a full elastic exchange.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.EqualTo(2.5f).Within(Tolerance),
                "`(10 + 0) * 0.25 + 0 = 2.5` — the prop's reflection is scaled by the flat 0.25, not " +
                "by knocked and not by 1.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.Not.EqualTo(10f).Within(Tolerance),
                "10 would be a full exchange for the prop too — it would bounce off a unit as hard as " +
                "it hit it.");
        }

        /// <summary>A heavy prop launches a light unit much harder than its own speed.</summary>
        [Test]
        public void ResolveExchange_HeavierProp_ThrowsTheUnitHarderThanThePropWasMoving()
        {
            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                new Vector2(10f, 0f), 1f, 1f, false, new Vector2(-10f, 0f), 9f);

            Assert.That(exchange.UnitVelocityPixelsPerFrame.x, Is.EqualTo(-26f).Within(Tolerance),
                "Centre of mass is -8; `(-10 - 8) * 1 - 8 = -26`. A 9x-mass crate flings a 1x unit " +
                "away at 2.6x the crate's own speed.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.EqualTo(-7.5f).Within(Tolerance),
                "`(10 - 8) * 0.25 - 8 = -7.5` — the heavy crate keeps going, barely slowed.");
        }

        /// <summary>
        /// <c>knocked</c> scales the unit's reflection and nothing else. At <c>knocked = 0</c> the unit
        /// ends at the centre-of-mass velocity, which is AS3's own reading of
        /// <c>dx = (-dx + _loc2_) * knocked + _loc2_</c>.
        /// </summary>
        [Test]
        public void ResolveExchange_UnitReflectionIsScaledByKnocked_ThePropIsNot()
        {
            PropImpactExchange half = PropImpactMath.ResolveExchange(
                new Vector2(10f, 0f), 1f, 0.5f, false, new Vector2(-10f, 0f), 9f);

            Assert.That(half.UnitVelocityPixelsPerFrame.x, Is.EqualTo(-17f).Within(Tolerance),
                "`(-10 - 8) * 0.5 - 8 = -17` — half the reflection, same centre of mass.");

            Assert.That(half.PropVelocityPixelsPerFrame.x, Is.EqualTo(-7.5f).Within(Tolerance),
                "The prop's result is identical to the knocked=1 case: knocked scales only the unit.");

            PropImpactExchange zero = PropImpactMath.ResolveExchange(
                new Vector2(10f, 0f), 1f, 0f, false, new Vector2(-10f, 0f), 9f);

            Assert.That(zero.UnitVelocityPixelsPerFrame.x, Is.EqualTo(-8f).Within(Tolerance),
                "`knocked = 0` collapses the unit to the centre-of-mass velocity (-8), it does not " +
                "freeze it at its own. That is the oracle's arithmetic, not a bug.");
        }

        /// <summary>
        /// A <c>fixed</c> unit does not move at all; the prop simply halves its velocity
        /// (<c>Unit.as:4232-4236</c>). Note the halving is on the <b>prop</b>, and it is 0.5 rather
        /// than the 0.25 of the moving branch.
        /// </summary>
        [Test]
        public void ResolveExchange_FixedUnit_DoesNotMoveAndThePropIsHalved()
        {
            var unitVelocity = new Vector2(10f, -3f);
            var propVelocity = new Vector2(-10f, 4f);

            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                unitVelocity, 1f, 1f, true, propVelocity, 1f);

            Assert.That(exchange.UnitVelocityPixelsPerFrame.x, Is.EqualTo(10f).Within(Tolerance),
                "A fixed unit is a statue — its velocity is handed back unchanged.");
            Assert.That(exchange.UnitVelocityPixelsPerFrame.y, Is.EqualTo(-3f).Within(Tolerance),
                "Both axes, not just x.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.EqualTo(-5f).Within(Tolerance),
                "The prop is halved: `param1.dx *= 0.5` at Unit.as:4234.");
            Assert.That(exchange.PropVelocityPixelsPerFrame.y, Is.EqualTo(2f).Within(Tolerance),
                "And its y too — the fixed branch is a flat halving of both axes.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.Not.EqualTo(-2.5f).Within(Tolerance),
                "-2.5 would be the 0.25 of the MOVING branch applied to a fixed unit. The two " +
                "branches use different coefficients (0.5 vs 0.25).");
        }

        /// <summary>
        /// Two zero masses divide by zero in AS3, which would hand both bodies NaN and destroy them.
        /// The halving branch is the closest non-destructive reading; what matters is that nothing is
        /// NaN.
        /// </summary>
        [Test]
        public void ResolveExchange_ZeroTotalMass_ProducesNoNaN()
        {
            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                new Vector2(4f, 4f), 0f, 1f, false, new Vector2(-4f, -4f), 0f);

            Assert.That(float.IsNaN(exchange.UnitVelocityPixelsPerFrame.x), Is.False, "unit x");
            Assert.That(float.IsNaN(exchange.UnitVelocityPixelsPerFrame.y), Is.False, "unit y");
            Assert.That(float.IsNaN(exchange.PropVelocityPixelsPerFrame.x), Is.False, "prop x");
            Assert.That(float.IsNaN(exchange.PropVelocityPixelsPerFrame.y), Is.False, "prop y");

            Assert.That(exchange.UnitVelocityPixelsPerFrame.x, Is.EqualTo(4f).Within(Tolerance),
                "The massless unit is left alone, like a fixed one.");
            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.EqualTo(-2f).Within(Tolerance),
                "The massless prop is halved, like the fixed branch.");
        }

        /// <summary>
        /// <b>The exchange is per axis, not along the contact normal.</b> AS3 computes a centre of mass
        /// for x and for y independently and reflects each axis about its own component. A real
        /// physics engine would exchange along the diagonal; this does not, and "correcting" it changes
        /// every knockback in the game.
        /// </summary>
        [Test]
        public void ResolveExchange_IsPerAxis_NotAlongTheContactNormal()
        {
            // A purely vertical prop velocity against a purely horizontal unit velocity: the axes
            // cannot interact, so each side's x result must depend only on the x inputs.
            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                new Vector2(10f, 0f), 1f, 1f, false, new Vector2(0f, -10f), 1f);

            Assert.That(exchange.UnitVelocityPixelsPerFrame.x, Is.EqualTo(0f).Within(Tolerance),
                "x centre of mass is (0 + 10)/2 = 5; `(-10 + 5) + 5 = 0`.");
            Assert.That(exchange.UnitVelocityPixelsPerFrame.y, Is.EqualTo(-10f).Within(Tolerance),
                "y centre of mass is (-10 + 0)/2 = -5; `(0 - 5) - 5 = -10`. The unit is thrown DOWN " +
                "the y axis only — the x and y results are computed independently.");

            Assert.That(exchange.PropVelocityPixelsPerFrame.x, Is.EqualTo(6.25f).Within(Tolerance),
                "`(-0 + 5) * 0.25 + 5 = 6.25` on x.");
            Assert.That(exchange.PropVelocityPixelsPerFrame.y, Is.EqualTo(-3.75f).Within(Tolerance),
                "`(10 - 5) * 0.25 - 5 = -3.75` on y.");

            // Negative control: changing the y input must not move the x output.
            PropImpactExchange otherY = PropImpactMath.ResolveExchange(
                new Vector2(10f, 0f), 1f, 1f, false, new Vector2(0f, -40f), 1f);

            Assert.That(otherY.UnitVelocityPixelsPerFrame.x,
                Is.EqualTo(exchange.UnitVelocityPixelsPerFrame.x).Within(Tolerance),
                "A 4x larger y velocity left the x result identical — if these ever diverge, someone " +
                "has reintroduced a normal-based exchange.");
        }
    }
}

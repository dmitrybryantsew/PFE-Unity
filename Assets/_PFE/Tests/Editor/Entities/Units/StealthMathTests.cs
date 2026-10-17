using NUnit.Framework;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="StealthMath"/> — AS3's <b>target-side</b> stealth model: <c>Unit.visibility</c>,
    /// <c>stealthMult</c>, <c>demask</c>, <c>detecting</c>, and <c>UnitPlayer.lurked</c>/<c>isSit</c>.
    ///
    /// <para><b>Why this fixture exists.</b> The port shipped the whole <i>listener</i> half of perception
    /// (<c>NoiseMath</c>) and none of the target half, so an observer's sight range was a flat
    /// <c>EnemySensors.VisionRangePixels</c> constant and crouching, cover and cloaks were
    /// unrepresentable — there was no term for them to act on. These are the numbers that were missing,
    /// and the boundaries are the part that a plausible re-implementation gets wrong: <c>|dx| &lt; 2</c>
    /// on <b>both</b> axes, <c>stealthMult &lt; 0.5</c> rather than <c>&lt;=</c>, and <c>demask</c> added
    /// <b>outside</b> the multiply.</para>
    ///
    /// <para>Every case names the oracle line it pins, so a disagreement can be settled by reading the
    /// ActionScript rather than by trusting this file.</para>
    /// </summary>
    [TestFixture]
    public sealed class StealthMathTests
    {
        // ── visibility — two values, not three ────────────────────────────────────────────────

        /// <summary>
        /// AS3 has exactly two <c>visibility</c> values and the still test needs <b>both</b> axes.
        /// </summary>
        [Test]
        public void Visibility_StillIs1500_MovingIs2000()
        {
            Assert.That(StealthMath.Visibility(stillOnBothAxes: false, lurked: false),
                Is.EqualTo(StealthMath.VisibilityMoving));
            Assert.That(StealthMath.Visibility(stillOnBothAxes: true, lurked: false),
                Is.EqualTo(StealthMath.VisibilityStill));

            Assert.That(StealthMath.VisibilityMoving, Is.EqualTo(2000), "unit/UnitPlayer.as:1093");
            Assert.That(StealthMath.VisibilityStill, Is.EqualTo(1500), "unit/UnitPlayer.as:1096");
        }

        /// <summary>
        /// The stillness threshold is <b>strictly</b> less than 2 on both axes
        /// (<c>dx &lt; 2 &amp;&amp; dx &gt; -2 &amp;&amp; dy &lt; 2 &amp;&amp; dy &gt; -2</c>).
        /// </summary>
        [Test]
        public void Visibility_ExactlyTwoPixelsPerFrame_IsNotStill()
        {
            var state = StealthMath.StealthState.Default;

            Assert.That(StealthMath.Tick(state, 2f, 0f).Visibility,
                Is.EqualTo(StealthMath.VisibilityMoving),
                "|dx| == 2 is NOT still: the oracle's test is `< 2`, so a unit at exactly the threshold " +
                "keeps the moving visibility. A `<=` here would make the boundary case a different game.");

            Assert.That(StealthMath.Tick(state, 1.999f, 0f).Visibility,
                Is.EqualTo(StealthMath.VisibilityStill),
                "…and just inside it is still, which is the positive control for the case above.");

            Assert.That(StealthMath.Tick(state, 0f, 2f).Visibility,
                Is.EqualTo(StealthMath.VisibilityMoving),
                "The still test is a conjunction over BOTH axes — a unit falling at 2 px/frame is moving.");
        }

        /// <summary>
        /// Lurking halves <c>visibility</c> <i>after</i> the still check, so a lurking unit is 750 whether
        /// it is standing still or not.
        /// </summary>
        [Test]
        public void Visibility_LurkedHalvesIt_WhicheverTheStillTestSaid()
        {
            Assert.That(StealthMath.Visibility(stillOnBothAxes: true, lurked: true), Is.EqualTo(750),
                "1500 × 0.5, per unit/UnitPlayer.as:1127 — applied after :1096, not instead of it.");
            Assert.That(StealthMath.Visibility(stillOnBothAxes: false, lurked: true), Is.EqualTo(1000),
                "2000 × 0.5. A moving lurker is still twice as visible as a still, un-lurked one.");
        }

        // ── detecting — the point-blank bubble ────────────────────────────────────────────────

        /// <summary>
        /// <c>stealthMult &lt; 0.5</c> deletes the point-blank bubble, and the comparison is
        /// <b>strict</b>.
        /// </summary>
        [Test]
        public void Detecting_StealthMultAtHalf_KeepsTheBubble()
        {
            Assert.That(StealthMath.Detecting(lurked: false, stealthMult: 1f),
                Is.EqualTo(StealthMath.DetectingBase));
            Assert.That(StealthMath.Detecting(lurked: false, stealthMult: 0.5f),
                Is.EqualTo(StealthMath.DetectingBase),
                "0.5 exactly KEEPS the bubble — unit/UnitPlayer.as:1129 is `< 0.5`, not `<= 0.5`. This is " +
                "the boundary a tidy-up would get wrong.");

            Assert.That(StealthMath.Detecting(lurked: false, stealthMult: 0.49f),
                Is.EqualTo(StealthMath.DetectingSuppressed),
                "…and just below it deletes the bubble.");
            Assert.That(StealthMath.Detecting(lurked: false, stealthMult: StealthMath.CloakStealthMult),
                Is.EqualTo(StealthMath.DetectingSuppressed),
                "The shipped cloak is 0.2, so a cloak also removes the point-blank tier.");

            Assert.That(StealthMath.DetectingBase, Is.EqualTo(80), "unit/Unit.as:464");
        }

        /// <summary>
        /// Lurking zeroes <c>detecting</c> on its own, without touching <c>stealthMult</c>.
        /// </summary>
        [Test]
        public void Detecting_Lurked_HidesTheBubble()
        {
            Assert.That(StealthMath.Detecting(lurked: true, stealthMult: 1f),
                Is.EqualTo(StealthMath.DetectingSuppressed),
                "unit/UnitPlayer.as:1125 — `if(lurked) detecting = 0`. A lurker behind a guard is not seen " +
                "at any distance, which is the whole point of cover.");
        }

        // ── demask — the anti-stealth channel ─────────────────────────────────────────────────

        /// <summary>
        /// <c>demask</c> decays 5 per tick and — unlike the oracle — floors at zero.
        /// </summary>
        [Test]
        public void Demask_DecaysFivePerTick_AndFloorsAtZero()
        {
            Assert.That(StealthMath.TickDemask(200f), Is.EqualTo(195f), "unit/Unit.as:3139-3142");
            Assert.That(StealthMath.TickDemask(5f), Is.EqualTo(0f));
            Assert.That(StealthMath.TickDemask(3f), Is.EqualTo(0f),
                "The oracle does not clamp and would step to −2; the port floors. Nothing reads a " +
                "negative demask as meaningful, and look() ADDS it to the range, so a negative value " +
                "would shrink the range below the stealth value.");
            Assert.That(StealthMath.TickDemask(0f), Is.EqualTo(0f));
            Assert.That(StealthMath.DemaskDecayPerTick, Is.EqualTo(5f));
            Assert.That(StealthMath.DemaskMax, Is.EqualTo(200f));

            // 200 peak / 5 per tick = 40 ticks ≈ 1.33 s at 30 Hz. That duration is the play-test's
            // acceptance for "fire once, then stop", so it is pinned here as a number.
            Assert.That(StealthMath.DemaskMax / StealthMath.DemaskDecayPerTick, Is.EqualTo(40f),
                "The reveal lasts 40 ticks — about 1.33 s at 30 Hz.");
        }

        /// <summary>
        /// Every oracle write to <c>demask</c> is a <b>raise</b>, never an assignment.
        /// </summary>
        [Test]
        public void Demask_RaiseIsMax_NotAssign()
        {
            Assert.That(StealthMath.RaiseDemask(200f, 50f), Is.EqualTo(200f),
                "A quiet event during a loud one must not shorten the reveal. Every oracle site is " +
                "`if(demask < X) demask = X;` — an assignment would make a footstep mute a gunshot.");
            Assert.That(StealthMath.RaiseDemask(0f, 200f), Is.EqualTo(200f));
            Assert.That(StealthMath.RaiseDemask(100f, 0f), Is.EqualTo(100f),
                "Non-positive is ignored entirely, so a melee weapon's shineRadius of 0 is a no-op.");
            Assert.That(StealthMath.RaiseDemask(100f, -5f), Is.EqualTo(100f));
        }

        /// <summary>
        /// The fire writer's acceptance: a shot is a <b>max</b>, so a faint shot cannot shorten a louder
        /// reveal already in progress — the property that makes "fire once, then stop" decay from the
        /// shot's own value rather than from whatever happened to be there.
        /// </summary>
        /// <remarks>
        /// <c>weapon/Weapon.as:1575-1578</c> — <c>if(this.owner.demask &lt; this.shine)
        /// this.owner.demask = this.shine;</c>. The reader is
        /// <c>ProjectileSpawner.RevealShot</c>; this fixture pins the arithmetic it calls.
        /// </remarks>
        [Test]
        public void Demask_MaxSemantics_AFaintShotDoesNotLowerAFullDemask()
        {
            Assert.That(StealthMath.RaiseDemask(500f, 300f), Is.EqualTo(500f),
                "A faint shot (shine 300) during a full reveal (500) must not lower it. An assignment " +
                "instead of a max would make a pistol shot cut a minigun's reveal short.");
            Assert.That(StealthMath.RaiseDemask(0f, 300f), Is.EqualTo(300f),
                "With nothing in progress, the shot sets the reveal to its own shine.");
            Assert.That(StealthMath.RaiseDemask(200f, 500f), Is.EqualTo(500f),
                "A shot is louder than a sprint: the default shine (500) beats the fast-move reveal " +
                "(200). This ordering is the whole reason a firing player is easy to see.");
        }

        /// <summary>
        /// The fire writer must stamp the weapon's <b>own</b> <c>shine</c>, not the oracle's default.
        /// </summary>
        /// <remarks>
        /// The discriminating case is a weapon authored below the default: if the reader passed a
        /// hard-coded <c>500</c> instead of the plan's <c>Cues.ShineRadius</c>, a <c>shine='300'</c>
        /// weapon would still reveal at <c>500</c>. <c>Weapon.as:627-629</c> is where the attribute
        /// overwrites the field, and <c>Weapon.as:216</c> is the default this test contrasts against.
        /// </remarks>
        [Test]
        public void Demask_FireStampsShineRadius_NotTheDefault()
        {
            const float authoredShine = 300f;

            Assert.That(StealthMath.RaiseDemask(0f, authoredShine), Is.EqualTo(authoredShine),
                "A weapon's own <vis shine='300'> must reach demask as 300.");
            Assert.That(StealthMath.RaiseDemask(0f, authoredShine), Is.Not.EqualTo(500f),
                "If this were 500, the reader hard-coded the default instead of reading the weapon.");
            Assert.That(StealthMath.RaiseDemask(0f, 500f), Is.EqualTo(500f),
                "The oracle's own default (Weapon.as:216) is 500, so an un-authored weapon still " +
                "reveals — a shot is the loudest thing in the game.");
        }

        /// <summary>
        /// Levitation and telekinesis are the two <b>per-tick state</b> demask writers, and unlike the
        /// fast-move raise they write a flat <c>200</c> regardless of <c>visiMult</c>.
        /// </summary>
        /// <remarks>
        /// <c>unit/UnitPlayer.as:1113-1116</c> (levitation) and <c>:1239-1242</c> (telekinesis) — both
        /// <c>if(&lt;state&gt; &amp;&amp; demask &lt; 200) demask = 200;</c>, run inside the step block. They reach
        /// <see cref="StealthMath.Tick"/> rather than a call site for exactly that reason.
        /// </remarks>
        [Test]
        public void Tick_LevitationAndTelekinesis_RaiseTheFullReveal()
        {
            Assert.That(StealthMath.StateRevealDemask(levitating: true, telekinesisHolding: false),
                Is.EqualTo(StealthMath.DemaskMax), "Levitation raises to 200.");
            Assert.That(StealthMath.StateRevealDemask(levitating: false, telekinesisHolding: true),
                Is.EqualTo(StealthMath.DemaskMax), "Holding an object telekinetically raises to 200.");
            Assert.That(StealthMath.StateRevealDemask(levitating: false, telekinesisHolding: false),
                Is.EqualTo(0f), "Neither state: no raise (0 means 'do not raise').");

            var levitating = StealthMath.Tick(
                StealthMath.StealthState.Default, 0f, 0f, levitating: true);
            Assert.That(levitating.Demask, Is.EqualTo(StealthMath.DemaskMax),
                "A still, levitating unit is still fully revealed — levitation is not movement-gated.");

            // Flat 200, NOT scaled by visiMult: a geared (visiMult 0.5) levitating unit still reads the
            // full reveal, where the same gear would damp a sprint to 100.
            var geared = StealthMath.StealthState.Default;
            geared.VisiMult = 0.5f;
            var gearedLevitating = StealthMath.Tick(geared, 0f, 0f, levitating: true);
            Assert.That(gearedLevitating.Demask, Is.EqualTo(StealthMath.DemaskMax),
                "Levitation writes the oracle's literal 200; only MovementDemask is scaled by visiMult.");
            Assert.That(StealthMath.MovementDemask(0f, 0f, 0.5f), Is.EqualTo(0f),
                "Control: with no movement, the fast-move writer contributes nothing, so the 200 above " +
                "can only have come from the levitation term.");
        }

        /// <summary>
        /// The fast-move reveal fires strictly above 10 px/frame and is the <b>only</b> writer scaled by
        /// <c>visiMult</c>.
        /// </summary>
        [Test]
        public void MovementDemask_AboveTenPixels_AndScaledByVisiMult()
        {
            Assert.That(StealthMath.MovementDemask(10f, 0f, 1f), Is.EqualTo(0f),
                "The oracle is `dx > 10`, so exactly 10 does not reveal.");
            Assert.That(StealthMath.MovementDemask(11f, 0f, 1f), Is.EqualTo(200f));
            Assert.That(StealthMath.MovementDemask(-11f, 0f, 1f), Is.EqualTo(200f),
                "The sign does not matter — the oracle tests both directions.");
            Assert.That(StealthMath.MovementDemask(0f, 11f, 1f), Is.EqualTo(200f),
                "Falling fast reveals as much as running fast.");
            Assert.That(StealthMath.MovementDemask(11f, 0f, 0.5f), Is.EqualTo(100f),
                "The value is `200 * pers.visiMult` — the one writer armour sneak damps.");
            Assert.That(StealthMath.FastMoveDx, Is.EqualTo(10f), "unit/UnitPlayer.as:1098-1103");
        }

        /// <summary>
        /// One <c>Tick</c> decays then raises, so a unit that is still running reads a full
        /// <c>DemaskMax</c> rather than <c>DemaskMax − 5</c>.
        /// </summary>
        [Test]
        public void Tick_DecaysThenRaises_SoARunningUnitReadsTheFullReveal()
        {
            var running = StealthMath.Tick(StealthMath.StealthState.Default, 11f, 0f);

            Assert.That(running.Demask, Is.EqualTo(StealthMath.DemaskMax),
                "Decay-then-raise. Reversing the two lines costs 5 per tick and is invisible in play — " +
                "which is exactly why the order is a decision with a test rather than an accident.");
            Assert.That(running.Visibility, Is.EqualTo(StealthMath.VisibilityMoving));
        }

        // ── look()'s range product — the keystone ─────────────────────────────────────────────

        /// <summary>
        /// The neutral target yields exactly the observer's base range. This is the check that catches
        /// "the product silently changed every enemy's range".
        /// </summary>
        [Test]
        public void SightRange_NeutralTarget_IsExactlyTheBaseRange()
        {
            float range = StealthMath.EffectiveSightRangePixels(
                baseRangePixels: 480f,
                visibility: StealthMath.VisibilityMoving,
                stealthMult: StealthMath.DefaultStealthMult,
                locVisMult: StealthMath.DefaultVisiMult,
                demask: 0f,
                observerVision: 1f);

            Assert.That(range, Is.EqualTo(480f).Within(1e-4f),
                "A moving, uncloaked, ungeared target must be seen at the port's existing range — " +
                "otherwise wiring the stealth model silently multiplies every enemy's sight range and " +
                "the change is indistinguishable from a stealth regression.");
        }

        /// <summary>
        /// Standing still shrinks the range by the <c>visibility</c> ratio, end to end.
        /// </summary>
        [Test]
        public void SightRange_StillTarget_IsThreeQuarters()
        {
            float moving = StealthMath.EffectiveSightRangePixels(480f, StealthMath.VisibilityMoving, 1f, 1f, 0f, 1f);
            float still = StealthMath.EffectiveSightRangePixels(480f, StealthMath.VisibilityStill, 1f, 1f, 0f, 1f);

            Assert.That(still, Is.EqualTo(360f).Within(1e-4f), "480 × 1500/2000 = 360.");
            Assert.That(still, Is.LessThan(moving),
                "The whole mechanic in one relation: standing still must be harder to see than moving.");
        }

        /// <summary>
        /// A cloak cuts the range to a fifth — and, because it is below 0.5, also deletes the point-blank
        /// bubble (see <see cref="Detecting_StealthMultAtHalf_KeepsTheBubble"/>).
        /// </summary>
        [Test]
        public void SightRange_CloakedTarget_IsAFifth()
        {
            float range = StealthMath.EffectiveSightRangePixels(
                480f, StealthMath.VisibilityMoving, StealthMath.CloakStealthMult, 1f, 0f, 1f);

            Assert.That(range, Is.EqualTo(96f).Within(1e-4f), "480 × 0.2.");
        }

        /// <summary>
        /// <c>demask</c> is added <b>outside</b> the multiply and is therefore not scaled by
        /// <c>stealthMult</c> — the whole anti-stealth design.
        /// </summary>
        [Test]
        public void SightRange_DemaskIsAddedOutsideTheMultiply()
        {
            float range = StealthMath.EffectiveSightRangePixels(
                480f, StealthMath.VisibilityMoving, StealthMath.CloakStealthMult, 1f, 200f, 1f);

            Assert.That(range, Is.EqualTo(296f).Within(1e-4f),
                "480 × 1 × 0.2 × 1 + 200 = 296 — the addition is outside the multiply.");
            Assert.That(range, Is.Not.EqualTo(136f),
                "136 is what a port that scaled demask by stealthMult would produce. Gear cannot shrink " +
                "the reveal from acting; that asymmetry is the design.");
        }

        /// <summary>
        /// A fully cloaked player who fires is still seen by the <c>demask</c> alone.
        /// </summary>
        [Test]
        public void SightRange_ZeroStealthMult_StillSeesWithDemask()
        {
            float range = StealthMath.EffectiveSightRangePixels(
                480f, StealthMath.VisibilityMoving, stealthMult: 0f, locVisMult: 1f, demask: 200f,
                observerVision: 1f);

            Assert.That(range, Is.EqualTo(200f).Within(1e-4f),
                "stealthMult 0 makes the first term vanish, so the range IS the demask — a muzzle flash " +
                "survives any cloak, which is what makes 'stealth is about not acting' true.");
        }

        /// <summary>
        /// The location's brightness and the observer's own vision scale the range, in the oracle's
        /// places.
        /// </summary>
        [Test]
        public void SightRange_LocationBrightnessAndObserverVisionScale()
        {
            Assert.That(
                StealthMath.EffectiveSightRangePixels(480f, StealthMath.VisibilityMoving, 1f, 0.5f, 0f, 1f),
                Is.EqualTo(240f).Within(1e-4f),
                "loc.visMult scales the visibility term, inside the multiply (unit/Unit.as:4587).");

            Assert.That(
                StealthMath.EffectiveSightRangePixels(480f, StealthMath.VisibilityMoving, 1f, 1f, 0f, 2f),
                Is.EqualTo(960f).Within(1e-4f),
                "The observer's own `vision` scales the WHOLE product, including demask — the oracle " +
                "writes `(… + demask) * (param3 ? param3 : this.vision)`.");
        }

        /// <summary>
        /// A degenerate input yields zero rather than a negative or infinite range.
        /// </summary>
        [Test]
        public void SightRange_DegenerateInputs_AreZero()
        {
            Assert.That(StealthMath.EffectiveSightRangePixels(0f, 2000, 1f, 1f, 0f, 1f), Is.EqualTo(0f));
            Assert.That(StealthMath.EffectiveSightRangePixels(480f, 2000, 1f, 1f, 0f, 0f), Is.EqualTo(0f),
                "A blind observer (vision 0) has no range.");
            Assert.That(StealthMath.EffectiveSightRangePixels(480f, 0, 1f, 1f, 0f, 1f), Is.EqualTo(0f),
                "A vanished target (visibility 0) with no reveal has no range.");
        }

        // ── the base term ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>sneak − demask/20</c>, floored at zero — and <c>sneak</c> is a dead field in the oracle.
        /// </summary>
        [Test]
        public void ObservationBase_SneakMinusDemaskOverTwenty_FlooredAtZero()
        {
            Assert.That(StealthMath.ObservationBase(0f, 0f), Is.EqualTo(0f));
            Assert.That(StealthMath.ObservationBase(2f, 0f), Is.EqualTo(2f));
            Assert.That(StealthMath.ObservationBase(0f, 200f), Is.EqualTo(0f),
                "0 − 10 → clamped to 0 (unit/UnitPlayer.as:3108-3112).");
            Assert.That(StealthMath.ObservationBase(3f, 40f), Is.EqualTo(1f), "3 − 2 = 1.");
            Assert.That(StealthMath.DemaskPerSneak, Is.EqualTo(20f));
        }

        // ── the declared constants, as one tripwire ───────────────────────────────────────────

        /// <summary>
        /// The oracle's declared numbers. Each is a value someone could reasonably "improve"; this is
        /// where they have to argue with the ActionScript instead.
        /// </summary>
        [Test]
        public void DeclaredConstants_MatchTheOracle()
        {
            Assert.That(StealthMath.VisibilityMoving, Is.EqualTo(2000), "unit/UnitPlayer.as:1093");
            Assert.That(StealthMath.VisibilityStill, Is.EqualTo(1500), "unit/UnitPlayer.as:1096");
            Assert.That(StealthMath.StillVelocityThreshold, Is.EqualTo(2f), "unit/UnitPlayer.as:1096");
            Assert.That(StealthMath.LurkVisibilityMultiplier, Is.EqualTo(0.5f), "unit/UnitPlayer.as:1127");
            Assert.That(StealthMath.DetectingBase, Is.EqualTo(80), "unit/Unit.as:464");
            Assert.That(StealthMath.StealthMultSuppressDetectingBelow, Is.EqualTo(0.5f), "unit/UnitPlayer.as:1129");
            Assert.That(StealthMath.DemaskMax, Is.EqualTo(200f), "unit/Unit.as:3765-3768");
            Assert.That(StealthMath.DemaskDecayPerTick, Is.EqualTo(5f), "unit/Unit.as:3139-3142");
            Assert.That(StealthMath.FastMoveDx, Is.EqualTo(10f), "unit/UnitPlayer.as:1098");
            Assert.That(StealthMath.DemaskPerSneak, Is.EqualTo(20f), "unit/UnitPlayer.as:3108");
            Assert.That(StealthMath.CloakStealthMult, Is.EqualTo(0.2f), "AllData.as:5948, :5952");
            Assert.That(StealthMath.DefaultVisiMult, Is.EqualTo(1f), "unit/Pers.as:305");
            Assert.That(StealthMath.DefaultStealthMult, Is.EqualTo(1f), "unit/Unit.as:462");
            Assert.That(StealthMath.DefaultSneakLurk, Is.EqualTo(5f), "unit/Pers.as:311");
            Assert.That(StealthMath.SitSneakLurkDivisor, Is.EqualTo(5f), "unit/UnitPlayer.as:3131");

            // The port's ONE deliberate calibration: the reference visibility is the moving value, so a
            // neutral target keeps the port's existing sight range instead of the oracle's absolute one.
            // See StealthMath.ReferenceVisibility for why the absolute range is not wired.
            Assert.That(StealthMath.ReferenceVisibility, Is.EqualTo(StealthMath.VisibilityMoving),
                "If this moves, every enemy's sight range moves with it — read the ReferenceVisibility " +
                "remarks before changing it.");
        }
    }
}

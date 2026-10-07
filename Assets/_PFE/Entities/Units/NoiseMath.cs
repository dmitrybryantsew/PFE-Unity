using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3's acoustic model — <c>Unit.noise</c>, <c>Unit.makeNoise()</c>, <c>Unit.listen()</c>,
    /// <c>Unit.budilo()</c> and <c>UnitPlayer.observation()</c> — as pure functions of their numbers.
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="StaggerMath"/> and
    /// <see cref="ContactInvulnerabilityMath"/>: every one of these quantities lives on a
    /// <c>MonoBehaviour</c> in the shipping code, so while the arithmetic sat inside the brain and the
    /// sensors it could not be executed without a scene, and every way to get it wrong is silent. A
    /// hearing radius that is 6× too large does not throw — it presents as "the enemy always knows where
    /// I am", which is indistinguishable from an AI-tuning complaint and was in fact the bug this file
    /// exists to fix. The oracle's whole acoustic system is about forty lines of arithmetic; putting it
    /// here makes all forty assertable.</para>
    ///
    /// <para><b>The one thing to understand before reading any of it: there is no hear radius in the
    /// original.</b> The audible distance is the <i>product</i>
    /// <c>target.noise × listener.ear × location.earMult</c>
    /// (<c>actionscript_project_context.txt:126338</c>). <c>noise</c> is <b>0</b> unless the target has
    /// just done something loud, and it decays 20 per tick — 600 per second at 30 Hz
    /// (<c>:124876-124878</c>). So a target that is standing still, or walking slowly, has an audible
    /// radius of <b>zero</b>: it is not "quiet at range", it is unhearable at any distance. Any port that
    /// draws a circle and asks "is the target inside it" has already lost the mechanic.</para>
    ///
    /// <para><b>Citations</b> are into <c>pfeToUnity/pfe/actionscript_project_context.txt</c>, the
    /// concatenated oracle dump — the same convention the sibling documents use. Line numbers into the
    /// original <c>.as</c> files are not recoverable from that dump, so no method below invents one; each
    /// rule is quoted instead.</para>
    /// </summary>
    public static class NoiseMath
    {
        // ────────────────────────────────────────────────────────────────────────────────────────
        //  Unit.noise — the target's half
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.noiseRun:int = 200</c> (<c>:122272</c>) — how loud this unit is when it moves at
        /// full speed. Overwritten per unit from the XML <c>noise</c> attribute
        /// (<c>if(node.@noise.length()) this.noiseRun = node.@noise;</c>, <c>:123110-123112</c>), which in
        /// the shipped data is the <c>&lt;vis noise='…'/&gt;</c> element: <c>600</c> for every zombie and
        /// raider, <c>0</c> for slimes.
        ///
        /// <para><b>Not the same thing as the sneak skill's <c>noiseRun</c>.</b> The skill tree also
        /// declares <c>&lt;sk id='noiseRun' v0='300' vd='-60'/&gt;</c> (<c>:7445</c>) and silent-movement
        /// perks set it to a multiplier of <c>0</c> (<c>:8320</c>, <c>:8359</c>). Nothing in the oracle
        /// ever assigns the <i>stat</i> into <c>Unit.noiseRun</c> — the unit field is written only by the
        /// XML import above — so the port keeps them separate too:
        /// <see cref="PFE.Systems.RPG.CharacterStats.noiseRun"/> is the stat, this is the unit's base.
        /// If they are ever joined, it must be at the unit field, and this comment is the reason not to
        /// join them the other way round.</para>
        /// </summary>
        public const int DefaultNoiseRun = 200;

        /// <summary>
        /// AS3's per-tick decay, <c>if(this.noise &gt; 0) this.noise -= 20;</c> (<c>:124876-124878</c>).
        /// At the port's canonical 30 Hz that is <b>600 per second</b>.
        ///
        /// <para><b>This constant is why the mechanic reads as "noise" rather than "a state".</b> A full
        /// run (noise 200) is audible for 10 ticks — a third of a second — after the unit stops; a walk
        /// (50) for 3 ticks. The radius is never a property of the listener; it is a property of what the
        /// target did in the last fraction of a second.</para>
        /// </summary>
        public const int NoiseDecayPerTick = 20;

        /// <summary>
        /// AS3 <c>Unit.noise_t:int = 30</c> (<c>:122274</c>) — the cooldown on the <i>visible ripple</i>
        /// that <c>makeNoise</c> emits for non-player units (<c>:125057</c>). It does not affect the
        /// audible radius at all; it exists so a unit running continuously does not spawn a ripple every
        /// tick.
        ///
        /// <para>Modelled because it is the only part of <c>makeNoise</c> with state, and because the
        /// ripple is the player's only feedback that the mechanic exists — see
        /// <see cref="ShouldRefreshNoiseRipple"/>. The port does not draw the ripple yet; that is recorded
        /// as an open item rather than silently dropped.</para>
        /// </summary>
        public const int NoiseRippleTicks = 30;

        /// <summary>
        /// AS3 <c>Unit.noiseDie:Number = 800</c> (<c>:29075</c>), used by
        /// <c>loc.budilo(X, Y - scY / 2, this.noiseDie)</c> in <c>die()</c> (<c>:29834-29836</c>) — the
        /// alarm a corpse raises. Far larger than any weapon, so a kill pulls the room.
        /// </summary>
        public const float DeathAlarmRadius = 800f;

        /// <summary>AS3 <c>Unit.noiseDoorOpen:int = 300</c> (<c>:119192</c>).</summary>
        public const int DoorNoise = 300;

        /// <summary>
        /// The movement thresholds, AS3 <c>Unit</c>'s per-tick block (<c>:124884-124896</c>):
        /// <code>
        /// if(stay &amp;&amp; (dx &gt; 12 || dx &lt; -12))    this.makeNoise(this.noiseRun);
        /// else if(stay &amp;&amp; (dx &gt; 7 || dx &lt; -7)) this.makeNoise(this.noiseRun / 2);
        /// else if(stay &amp;&amp; (dx &gt; 3 || dx &lt; -3)) this.makeNoise(this.noiseRun / 4);
        /// </code>
        /// In <b>pixels per frame</b> — which is exactly the quantity <c>Unit.dx</c> holds and exactly
        /// what <see cref="PFE.Entities.Units.UnitController.VelocityPixelsPerFrame"/> returns, so these
        /// are used verbatim with no rescaling.
        ///
        /// <para><b>The comparisons are strict, and the boundaries are reachable.</b> A unit whose run
        /// speed is exactly 7 px/frame — which the shipped data contains — makes the <i>quarter</i> noise,
        /// not the half. A unit at exactly 3 px/frame makes none at all. Both are the oracle's behaviour
        /// and both are asserted in the fixture, because "off by one at the boundary" here is invisible in
        /// play and would look like a tuning preference.</para>
        ///
        /// <para><b>Below <see cref="WalkNoiseDx"/> there is no call to <c>makeNoise</c> at all</b> — not
        /// a call with zero. That distinction is load-bearing: it means a stationary unit does not refresh
        /// <c>noise_t</c> either, so it goes fully silent rather than emitting a silent ripple.</para>
        /// </summary>
        public const float RunNoiseDx = 12f;

        /// <inheritdoc cref="RunNoiseDx"/>
        public const float TrotNoiseDx = 7f;

        /// <inheritdoc cref="RunNoiseDx"/>
        public const float WalkNoiseDx = 3f;

        /// <summary>
        /// The landing thresholds, AS3's shelf-drop branch (<c>:124160-124168</c>):
        /// <code>
        /// if(dy &gt; 16)     this.makeNoise(this.noiseRun, true);
        /// else if(dy &gt; 9) this.makeNoise(this.noiseRun / 2, true);
        /// </code>
        /// <c>dy</c> is the downward speed at the moment of landing, in px/frame, and the second argument
        /// <c>true</c> is <c>makeNoise</c>'s "this is an event, not a continuous state" flag — see
        /// <see cref="ShouldRefreshNoiseRipple"/>.
        /// </summary>
        public const float HardLandingDy = 16f;

        /// <inheritdoc cref="HardLandingDy"/>
        public const float SoftLandingDy = 9f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  Unit.ear — the listener's half
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.ear:Number = 1</c> (<c>:122286</c>), imported per unit from the XML
        /// <c>ear</c> attribute (<c>:122998-123000</c>) — authored on the <c>&lt;comb&gt;</c> element.
        ///
        /// <para><b><c>ear = 0</c> is common in the shipped data and means stone deaf.</b> Every small
        /// robot in the roster authors it — <c>spritebot</c>, <c>vortex</c>, <c>roller</c>, <c>roller2</c>
        /// (<c>:3800-3845</c>) — as do the two <c>hp='1000'</c> sentinels at <c>:4038</c>. That is not an
        /// oversight in the data: it is why <c>budilo()</c> carries a separate branch for robots
        /// (see <see cref="AlarmReaches"/>), which would otherwise be unreachable.</para>
        /// </summary>
        public const float DefaultEar = 1f;

        /// <summary>
        /// AS3 <c>Location.earMult:Number = 1</c> (<c>:33803</c>), halved on easy difficulty —
        /// <c>if(World.w.game.globalDif &lt; 2) param1.earMult *= 0.5;</c> (<c>:32526</c>).
        ///
        /// <para><b>It is a difficulty scalar, not a data field — there is nothing to author.</b> The
        /// whole oracle mentions <c>earMult</c> four times: the declaration, the one halving above, and
        /// the two reads (<c>:126338</c>, <c>:36411</c>). There is <b>no XML attribute for it anywhere</b>
        /// (<c>grep '@earMult'</c> over the whole dump returns nothing), and its only writer
        /// <c>setLocDif</c> has exactly one caller, <c>Land.newLoc</c> (<c>:32511</c>), which builds each
        /// <c>Location</c> once — so the value is <b>1 on normal/hard and 0.5 on easy</b>, and nothing else
        /// can ever move it.</para>
        ///
        /// <para>The port has no runtime <c>globalDif</c>, so the halving is <b>not wired</b> and this sits
        /// at the oracle's declared default of 1. Wiring it faithfully means adding a global difficulty
        /// scalar and multiplying it in at <see cref="HearingRadius"/>/<see cref="AlarmReaches"/> — <i>not</i>
        /// exposing it as per-unit authoring, which the oracle does not have.</para>
        /// </summary>
        public const float DefaultEarMultiplier = 1f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  listen() — the graded intensity
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.listen()</c>'s ceiling: <c>return (1 - _loc3_ / (_loc2_ * _loc2_)) * 4;</c>
        /// (<c>:126348</c>). Intensity is <b>4</b> at zero distance and falls to <b>0</b> at the edge of
        /// the radius.
        ///
        /// <para><b>The factor 4 is not decoration — it is the commit threshold.</b> The caller reacts
        /// immediately only when the intensity exceeds 1 (<c>if(_loc3_ &gt; 1)</c>, <c>:126487</c>), i.e.
        /// only within <c>d &lt; sqrt(3)/2 · r ≈ 0.866·r</c>. Between 0.866·r and r the sound is heard but
        /// only feeds the suspicion meter. Collapsing intensity to a bool loses that whole inner/outer
        /// distinction, which is the difference between "an enemy at the edge of your noise turns toward
        /// you" and "an enemy at the edge of your noise knows exactly where you are".</para>
        /// </summary>
        public const float MaxHearingIntensity = 4f;

        /// <summary>
        /// AS3 <c>Unit.listen()</c> (<c>:126332-126353</c>) — the audible radius, as the product the
        /// oracle uses: <c>noise × ear × earMult</c>.
        ///
        /// <para>Separated from <see cref="HearingIntensity"/> because the two are read by different
        /// things: the debug overlay draws this as a circle, and the sensors need the intensity. Deriving
        /// one from the other at both call sites is how the drawn circle and the tested radius drift
        /// apart.</para>
        /// </summary>
        /// <param name="noise">The <b>target's</b> current noise, in px of radius per unit of
        /// <c>ear</c>. <c>0</c> (the resting value) makes this <c>0</c>.</param>
        /// <param name="ear">The <b>listener's</b> ear. <c>0</c> is deaf.</param>
        /// <param name="earMultiplier">The location's <c>earMult</c>.</param>
        /// <remarks>
        /// <b>The product is the whole design, so do not "helpfully" floor it.</b> Any factor at zero makes
        /// the radius zero, and that is the intent in all three cases: a silent target, a deaf listener, a
        /// muted room. A guard that returned some minimum radius "so the AI still works" would resurrect
        /// exactly the bug this file was written to fix.
        /// </remarks>
        public static float HearingRadius(int noise, float ear, float earMultiplier)
        {
            if (noise <= 0 || ear <= 0f || earMultiplier <= 0f)
            {
                return 0f;
            }

            return noise * ear * earMultiplier;
        }

        /// <summary>
        /// AS3 <c>Unit.listen()</c> (<c>:126332-126353</c>) — the <b>graded</b> audibility of a target,
        /// <c>(1 − d²/r²) × 4</c>, or <c>0</c> when it is not audible.
        ///
        /// <para><b>Distance is between unit centres</b> in the oracle — <c>param1.Y - param1.scY/2 - Y +
        /// scY/2</c>, i.e. both units' mid-heights — which is what the port's
        /// <c>EnemySensors.GetUnitCenterPixels</c> already computes. Pass a squared distance between
        /// centres; a feet-to-feet distance is a different (and slightly larger) number.</para>
        /// </summary>
        /// <param name="noise">The target's noise.</param>
        /// <param name="ear">The listener's ear.</param>
        /// <param name="earMultiplier">The location's <c>earMult</c>.</param>
        /// <param name="distanceSquaredPixels">Squared centre-to-centre distance, in AS3 pixels.</param>
        /// <returns><c>0</c> when inaudible; otherwise up to <see cref="MaxHearingIntensity"/>.</returns>
        /// <remarks>
        /// <para><b>The <c>&lt;=</c> early-out is a real branch, not a guard.</b> AS3 returns 0 <i>before</i>
        /// it computes any distance, so a target with <c>noise = 0</c> is inaudible even at zero distance.
        /// The port's old code returned <c>true</c> at zero distance for every target; this is the line
        /// that changes that, and the first fixture asserts it directly.</para>
        ///
        /// <para><b>The comparison is <c>r² &gt; d²</c>, strict.</b> A target exactly on the rim is
        /// inaudible. That is not pedantry — the oracle writes it that way and the boundary is reachable
        /// whenever a radius happens to be a round number, which <c>noiseRun = 200</c> guarantees.</para>
        /// </remarks>
        public static float HearingIntensity(
            int noise, float ear, float earMultiplier, float distanceSquaredPixels)
        {
            float radius = HearingRadius(noise, ear, earMultiplier);
            if (radius <= 0f)
            {
                return 0f;
            }

            float radiusSquared = radius * radius;
            if (radiusSquared <= distanceSquaredPixels)
            {
                return 0f;
            }

            return (1f - distanceSquaredPixels / radiusSquared) * MaxHearingIntensity;
        }

        /// <summary>
        /// AS3 <c>if(_loc3_ &gt; 1)</c> (<c>:126487</c>) — whether a heard sound is loud enough to make the
        /// listener react <i>now</i>, rather than only adding to its suspicion.
        ///
        /// <para>Strictly greater. An intensity of exactly 1 — which is
        /// <c>d = sqrt(3)/2 · r ≈ 0.866 · r</c> — does <b>not</b> commit; it only nudges
        /// <c>obs</c>. Asserted, because <c>&gt;=</c> here would make the whole outer 13 % of the radius
        /// behave like the centre and is exactly the kind of off-by-one that reads as AI tuning.</para>
        /// </summary>
        public static bool CommitsImmediately(float intensity)
        {
            return intensity > SoundOnlyCommitIntensity;
        }

        /// <summary>
        /// The threshold <see cref="CommitsImmediately"/> tests against: AS3's literal <c>1</c>.
        /// </summary>
        public const float SoundOnlyCommitIntensity = 1f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  makeNoise() — the raise
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>if(this.noise &lt; param1) this.noise = param1;</c> (<c>:125048-125051</c>) — a new sound
        /// may only <b>raise</b> the noise, never cut a louder one short.
        ///
        /// <para><b>This is the rule that makes the whole model work, and it is the opposite of the
        /// damage-stagger rule it superficially resembles.</b> Because noise is raise-only, a footstep
        /// during a gunshot does not silence the gunshot; and because it decays every tick, a continuous
        /// walk still settles at the walk value rather than accumulating. Replace this with an assignment
        /// and a walking player would mute his own rifle, which reads as "the gun is quiet" rather than as
        /// a bug in the noise model.</para>
        ///
        /// <para><b><c>amount &lt;= 0</c> is an early out, not a no-op.</b> AS3 returns before touching
        /// <c>noise_t</c> (<c>:125044-125047</c>), so a zero-amount call does not even refresh the ripple
        /// cooldown. Callers therefore need no pre-check, and a slime with <c>noiseRun = 0</c> stays
        /// perfectly silent instead of emitting ripples.</para>
        /// </summary>
        public static int MakeNoise(int current, int amount)
        {
            if (amount <= 0)
            {
                return current;
            }

            return amount > current ? amount : current;
        }

        /// <summary>
        /// AS3's per-tick noise decay, <c>if(this.noise &gt; 0) this.noise -= 20;</c>
        /// (<c>:124876-124878</c>), clamped at zero for the same reason
        /// <see cref="StaggerMath.Tick"/> is: a negative noise would be an inaudible <i>and</i> wrong
        /// value that nothing else in the system can explain.
        /// </summary>
        public static int TickNoise(int noise)
        {
            return noise > 0 ? Mathf.Max(0, noise - NoiseDecayPerTick) : 0;
        }

        /// <summary>
        /// AS3 <c>if(this.noise_t &gt; 0) --this.noise_t;</c> (<c>:124880-124882</c>).
        /// </summary>
        public static int TickNoiseRipple(int rippleTicks)
        {
            return rippleTicks > 0 ? rippleTicks - 1 : 0;
        }

        /// <summary>
        /// AS3 <c>if(this.noise_t == 0 || param2 &amp;&amp; this.noise_t &lt;= 20)</c>
        /// (<c>:125053-125055</c>) — whether this call should restart the ripple cooldown.
        ///
        /// <para><b>The <c>param2</c> half is what makes an event feel like an event.</b> A continuous
        /// source (walking, running) refreshes only when the cooldown has fully expired — one ripple per
        /// 30 ticks. An event (<c>param2 = true</c>: landing, a gunshot, a door) also refreshes while the
        /// cooldown is still inside its last 10 ticks, so two gunshots in quick succession draw two
        /// ripples instead of one. Getting this backwards is invisible except as "the ripple sometimes
        /// does not appear", which is why it is a named function with its own test rather than an inlined
        /// <c>||</c>.</para>
        /// </summary>
        public static bool ShouldRefreshNoiseRipple(int rippleTicks, bool isEvent)
        {
            if (rippleTicks == 0)
            {
                return true;
            }

            return isEvent && rippleTicks <= NoiseRippleTicks - 10;
        }

        /// <summary>
        /// AS3's movement-noise block (<c>:124884-124896</c>) — the noise a unit generates by moving at
        /// <paramref name="dxPixelsPerFrame"/>, or <c>0</c> for "make no noise call at all".
        ///
        /// <para><b>Zero means "do not call <c>makeNoise</c>", not "call it with zero".</b> The distinction
        /// survives because <see cref="MakeNoise"/> early-outs on a non-positive amount, so a caller that
        /// passes this result straight through gets the oracle's behaviour either way — but it matters for
        /// <see cref="ShouldRefreshNoiseRipple"/>, which the caller must not run for a zero.</para>
        /// </summary>
        /// <param name="noiseRun">The unit's base noise (<c>Unit.noiseRun</c>).</param>
        /// <param name="dxPixelsPerFrame">Signed horizontal velocity, px/frame. Only the magnitude
        /// matters; the oracle tests both signs because its <c>dx</c> is signed.</param>
        /// <param name="grounded">AS3's <c>stay</c> — the whole block is gated on it. An airborne unit
        /// makes no movement noise (it makes a landing noise instead), which is why jumping is a stealth
        /// tool in the original.</param>
        /// <remarks>
        /// <b>The quarter/half/full split uses integer division, as AS3 does.</b> <c>this.noiseRun / 4</c>
        /// on an <c>int</c> truncates toward zero when it reaches <c>makeNoise(param1:int)</c>, so
        /// <c>noiseRun = 250</c> gives <b>62</b>, not 62.5. That is the oracle's coercion, not a rounding
        /// choice, and it is asserted with an odd <c>noiseRun</c> precisely because a power of two would
        /// hide it.
        /// </remarks>
        public static int MovementNoise(int noiseRun, float dxPixelsPerFrame, bool grounded)
        {
            if (!grounded || noiseRun <= 0)
            {
                return 0;
            }

            float speed = Mathf.Abs(dxPixelsPerFrame);

            if (speed > RunNoiseDx)
            {
                return noiseRun;
            }

            if (speed > TrotNoiseDx)
            {
                return noiseRun / 2;
            }

            if (speed > WalkNoiseDx)
            {
                return noiseRun / 4;
            }

            return 0;
        }

        /// <summary>
        /// AS3's shelf-drop landing branch (<c>:124160-124168</c>) — the noise a landing makes, from the
        /// downward speed <c>dy</c> at the moment of contact, in px/frame.
        /// </summary>
        /// <remarks>
        /// <b>Strictly greater, and both thresholds are reachable in normal play.</b> The oracle's
        /// terminal fall speed is <c>maxdy = 20</c> px/frame, so a full-height fall (<c>dy &gt; 16</c>)
        /// makes the <i>full</i> <c>noiseRun</c> and a short hop (<c>dy &gt; 9</c>) makes half. A fall
        /// slower than 9 px/frame is silent, which is what lets a player lower himself off a low ledge
        /// without being heard.
        /// </remarks>
        public static int LandingNoise(int noiseRun, float dyPixelsPerFrame)
        {
            if (noiseRun <= 0)
            {
                return 0;
            }

            float speed = Mathf.Abs(dyPixelsPerFrame);

            if (speed > HardLandingDy)
            {
                return noiseRun;
            }

            if (speed > SoftLandingDy)
            {
                return noiseRun / 2;
            }

            return 0;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  observation() — the suspicion accumulator
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitPlayer.maxObs:Number = 20</c> (<c>:139205</c>) — the suspicion a target must
        /// accumulate before an enemy commits to it.
        ///
        /// <para><b>The accumulator belongs to the <i>target</i>, not to each enemy.</b> It is declared on
        /// <c>UnitPlayer</c>, so every enemy in the room feeds the same meter: three guards who each hear
        /// a faint footstep commit in a third of the time one would. That is the oracle's design and it is
        /// also the single most surprising thing about this system — a port that put a private suspicion
        /// value on each brain would look identical with one enemy and be completely different with
        /// four.</para>
        /// </summary>
        public const float DefaultMaxObservation = 20f;

        /// <summary>
        /// AS3 <c>UnitPlayer.minusObs:Number = 0.1</c> (<c>:139207</c>) — the per-tick suspicion decay,
        /// applied once the hold has expired (<c>:140155-140158</c>).
        ///
        /// <para><b>0.1 per tick against an intensity of up to 4 per tick is the ratio that matters.</b> A
        /// gunshot at close range fills the meter in about five ticks; a faint footstep at the rim of the
        /// radius takes forty, and decays away in the gaps. The decay is what makes a <i>rhythmic</i> sound
        /// (walking) accumulate and a single click not.</para>
        /// </summary>
        public const float ObservationDecayPerTick = 0.1f;

        /// <summary>
        /// AS3 <c>if(param1 &gt; 0) this.isObs = 30;</c> (<c>:142189-142192</c>) — a positive observation
        /// re-arms a 30-tick hold during which <c>obs</c> does not decay.
        /// </summary>
        public const int ObservationHoldTicks = 30;

        /// <summary>
        /// AS3 <c>if(this.obs &gt; this.maxObs * 2) this.obs = this.maxObs * 2;</c>
        /// (<c>:142192-142195</c>) — the meter's ceiling is <b>twice</b> the commit threshold, not the
        /// threshold. It is not an off-by-one in the oracle: a target that has been thoroughly seen can
        /// stay committed while decaying from 40 down to 20.
        /// </summary>
        public const float ObservationCeilingFactor = 2f;

        /// <summary>
        /// The suspicion meter — AS3's <c>obs</c> / <c>isObs</c> pair (<c>:139203-139209</c>).
        ///
        /// <para>A struct rather than two loose fields so the two cannot be ticked in the wrong order at
        /// one call site and the right one at another; <see cref="TickObservation"/> is the only thing
        /// that advances them and it always advances both.</para>
        /// </summary>
        public struct Observation
        {
            /// <summary>AS3 <c>obs</c> — accumulated suspicion, in the same units as the intensity.</summary>
            public float Value;

            /// <summary>
            /// AS3 <c>isObs</c> — ticks remaining before <see cref="Value"/> starts decaying. Goes
            /// negative and stays there, which is how the oracle encodes "decay is running" — see
            /// <see cref="TickObservation"/>.
            /// </summary>
            public int HoldTicks;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.observation(param1)</c>'s accumulate half (<c>:142187-142196</c>):
        /// <c>obs += param1; if(param1 &gt; 0) isObs = 30; if(obs &gt; maxObs*2) obs = maxObs*2;</c>.
        /// </summary>
        /// <param name="state">The meter to add to.</param>
        /// <param name="amount">
        /// The already-scaled intensity. Callers must clamp it at zero themselves — the oracle does that
        /// inside <c>observation()</c> before this point (<c>:142183-142186</c>), and doing it here as well
        /// would hide a caller that forgot.
        /// </param>
        /// <param name="maxObservation">AS3 <c>maxObs</c>.</param>
        public static Observation AddObservation(Observation state, float amount, float maxObservation)
        {
            if (amount <= 0f)
            {
                return state;
            }

            state.Value += amount;

            float ceiling = maxObservation * ObservationCeilingFactor;
            if (state.Value > ceiling)
            {
                state.Value = ceiling;
            }

            state.HoldTicks = ObservationHoldTicks;
            return state;
        }

        /// <summary>
        /// AS3's per-tick decay (<c>:140155-140161</c>):
        /// <code>
        /// if(this.obs &gt; 0 &amp;&amp; this.isObs &lt;= 0) this.obs -= this.minusObs;
        /// if(this.isObs &gt;= 0) --this.isObs;
        /// </code>
        /// </summary>
        /// <remarks>
        /// <para><b>Two subtleties, both of them the oracle's.</b> First, the hold counts down through
        /// <c>0</c> to <c>-1</c> and the decay test is <c>isObs &lt;= 0</c>, so decay starts on the tick
        /// <i>after</i> the hold reaches zero — one tick later than a naive reading. Second, <c>isObs</c>
        /// is <b>not</b> clamped: it keeps counting down forever, which is harmless but means the field is
        /// not a duration and must not be displayed as one.</para>
        ///
        /// <para>Written in the oracle's statement order on purpose. Swapping the two lines moves the
        /// whole decay curve by a tick, which is unnoticeable in play and would make the fixture's
        /// boundary cases pass for the wrong reason.</para>
        ///
        /// <para><b>One deliberate divergence: the value is floored at zero.</b> AS3 has no floor, so
        /// after a long drain <c>obs</c> sits a hair below zero — harmless to both of the oracle's own
        /// tests (<c>obs &gt; 0</c> stops the decay, <c>obs &gt;= maxObs</c> is false) but not to its
        /// HUD, which indexes an animation frame with <c>obs / maxObs * 40 + 1</c> (<c>:17651</c>). The
        /// clamp costs nothing observable and removes a negative frame index.</para>
        /// </remarks>
        public static Observation TickObservation(Observation state)
        {
            if (state.Value > 0f && state.HoldTicks <= 0)
            {
                state.Value -= ObservationDecayPerTick;
                if (state.Value < 0f)
                {
                    state.Value = 0f;
                }
            }

            if (state.HoldTicks >= 0)
            {
                state.HoldTicks--;
            }

            return state;
        }

        /// <summary>
        /// AS3 <c>return this.obs &gt;= this.maxObs;</c> (<c>:142196</c>) — the commit test.
        ///
        /// <para><c>&gt;=</c>, unlike almost every other boundary in this file. The asymmetry is the
        /// oracle's and it is asserted so that a later "tidy-up" of the comparisons cannot quietly align
        /// this one with the rest.</para>
        /// </summary>
        public static bool IsObserved(Observation state, float maxObservation)
        {
            return state.Value >= maxObservation;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.observation(param1, param2)</c>'s scaling half — what the raw sensor
        /// intensity becomes once the target's own stealth and the observer's own sharpness are folded
        /// in (<c>:142156-142186</c>).
        ///
        /// <para><b>Only two of the oracle's nine inputs exist in the port, and the other seven are
        /// named here rather than silently assumed.</b> The oracle computes
        /// <c>_loc3_ = sneak − demask/20</c> (clamped at 0), then scales by <c>param2</c> against it,
        /// then multiplies by <c>pers.visiMult × stealthMult</c>, then subtracts <c>sneakLurk</c> when
        /// lurking and a fifth of it when sitting. The port has none of <c>sneak</c>, <c>demask</c>,
        /// <c>visiMult</c>, <c>stealthMult</c>, <c>lurked</c>, <c>isSit</c> or <c>sneakLurk</c> wired to a
        /// live value, so all seven take their oracle defaults — <c>0, 0, 1, 1, false, false, 0</c> —
        /// which collapses the whole pipeline to the two branches below. <b>This function is where they
        /// go when they arrive</b>, and the collapse is written down so that a future reader can tell
        /// "not ported yet" from "the oracle does nothing here".</para>
        /// </summary>
        /// <param name="intensity">The raw sensor intensity (<c>listen()</c>'s or <c>look()</c>'s).</param>
        /// <param name="observationPower">
        /// AS3 <c>param2</c> — the <b>observer's</b> <c>observ</c> attribute. The oracle's sentinel for
        /// "no scaling" is <c>-1000</c>; the hearing call site passes only one argument, so hearing is
        /// never scaled by it. With the port's <c>sneak = 0</c> and <c>demask = 0</c>, the comparison
        /// base is exactly 0, which is why the two branches below are a plain <c>&gt; 0</c> /
        /// <c>&lt; 0</c> test.
        /// </param>
        /// <remarks>
        /// <b>A sharp observer notices faster; a dull one can be duller than nothing.</b>
        /// <c>observ = 6</c> (<c>zombie9</c>) multiplies the intensity by 2.2, so it commits in about
        /// half the time; a negative value <i>divides</i>, because the oracle's second branch is a
        /// division by <c>1 − param2·0.3</c>. The shipped data never authors a negative <c>obs</c>, so
        /// that branch is unreachable in play — it is implemented because leaving it out would make this
        /// function silently wrong for an input the oracle accepts.
        /// </remarks>
        public static float ObservationInput(float intensity, float observationPower)
        {
            if (intensity <= 0f)
            {
                return 0f;
            }

            if (observationPower > 0f)
            {
                intensity *= 1f + observationPower * 0.2f;
            }
            else if (observationPower < 0f)
            {
                intensity /= 1f - observationPower * 0.3f;
            }

            return intensity < 0f ? 0f : intensity;
        }

        /// <summary>
        /// AS3 <c>Unit.look()</c>'s two flat tiers (<c>:126426-126437</c>) — the vision contribution to
        /// the suspicion meter.
        ///
        /// <para><b>Why vision needs an intensity at all.</b> Sight and sound feed the <i>same</i> meter
        /// (<c>findCel</c> calls <c>observation()</c> for both, <c>:126469-126474</c>), which is the only
        /// reason the two senses are comparable. A port that commits on a bool sighting and accumulates
        /// on sound has two different notions of "noticed", and the sighting one wins every time.</para>
        ///
        /// <para><c>20</c> inside <c>detecting</c> and <c>4</c> out to the look range, straight from the
        /// oracle's two <c>return</c> statements. Against <c>maxObs = 20</c> that is a commit in
        /// <b>1 tick</b> at close range and <b>5 ticks</b> (0.17 s) at the far edge — the oracle's
        /// reaction time, and the reason a guard you walk into reacts before one you merely cross.</para>
        /// </summary>
        /// <param name="distanceSquaredPixels">Squared distance from the observer's eye to the target's
        /// centre, in AS3 pixels.</param>
        /// <param name="closeProximityPixels">AS3's <c>detecting</c> radius.</param>
        /// <param name="visionRangePixels">
        /// The port's flat sight range — AS3's <c>lookR</c>. See the remarks for the one band this does
        /// not reproduce.
        /// </param>
        /// <remarks>
        /// <b>Not modelled: AS3's outer decay band.</b> Beyond <c>lookR</c> the oracle returns
        /// <c>lookR²/d² · 4</c> out to four times the range (<c>if(_loc8_ &gt; _loc7_² · 16) return 0</c>),
        /// so a sharp-eyed unit sees a faint suggestion of a target at 4× its range. The port's vision
        /// range is already a hard cut at <c>EnemySensors.VisionRangePixels</c> and its line-of-sight
        /// test is a bool, so there is no soft band to put the decay in; reproducing it means porting
        /// <c>look()</c>'s distance curve, which is its own task. Vision therefore ends where the port's
        /// range says it ends, and that is a known divergence rather than an oversight.
        /// </remarks>
        public static float VisionIntensity(
            float distanceSquaredPixels, float closeProximityPixels, float visionRangePixels)
        {
            if (closeProximityPixels > 0f &&
                distanceSquaredPixels < closeProximityPixels * closeProximityPixels)
            {
                return VisionCloseIntensity;
            }

            if (visionRangePixels > 0f && distanceSquaredPixels < visionRangePixels * visionRangePixels)
            {
                return VisionRangeIntensity;
            }

            return 0f;
        }

        /// <summary>
        /// AS3 <c>look()</c>'s <c>return 20;</c> for a target inside <c>detecting</c>
        /// (<c>:126431-126434</c>).
        /// </summary>
        public const float VisionCloseIntensity = 20f;

        /// <summary>
        /// AS3 <c>look()</c>'s <c>return 4;</c> for a target inside the look range
        /// (<c>:126435-126438</c>). The same ceiling <c>listen()</c> has, which is why a faint sound and
        /// a distant sighting commit at the same speed.
        /// </summary>
        public const float VisionRangeIntensity = 4f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  budilo() — alarm propagation
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.budilo(param1:Number = 500)</c> (<c>:126071</c>) — the default alarm radius a unit
        /// shouts to its own faction.
        /// </summary>
        public const float DefaultAlarmRadius = 500f;

        /// <summary>
        /// AS3 <c>_loc2_.alarma(X + (Math.random() - 0.5) * 250, ...)</c> (<c>:126092</c>) — the
        /// unit-level alarm hands out the position with <b>±125 px</b> of error.
        /// </summary>
        public const float UnitAlarmSpreadPixels = 250f;

        /// <summary>
        /// AS3 <c>_loc9_ = param3 / 2; if(_loc9_ &gt; 400) _loc9_ = 400;</c> (<c>:36414-36418</c>) — the
        /// location-level alarm's spread is half its radius, capped. With the default radius of 1000 that
        /// is 400, so a room-wide alarm is <c>±200 px</c> — deliberately vaguer than a shout.
        /// </summary>
        public const float LocationAlarmSpreadCapPixels = 400f;

        /// <summary>
        /// AS3 <c>Location.budilo(param3:Number = 1000)</c> (<c>:36405</c>) — the map-wide alarm's default
        /// radius.
        /// </summary>
        public const float DefaultLocationAlarmRadius = 1000f;

        /// <summary>
        /// Whether an alarm reaches a listener — AS3's two branches, which differ only in what the radius
        /// is scaled by.
        ///
        /// <para><b>Robots are the exception, and the data explains why.</b> The unit branch is
        /// <c>dist² &lt; radius² × receiver.ear²</c> (<c>:126090</c>) and the location branch is the same
        /// with an extra <c>earMult</c> (<c>:36411</c>, <c>:36423</c>) — both gate on the <i>receiver's</i>
        /// ear. The robot branch (<c>:126083-126087</c>) is a plain <c>dist² &lt; radius²</c> with no
        /// <c>ear</c> factor. That looks like a special case bolted on; it is the opposite. Every robot in
        /// the roster authors <c>ear='0'</c> (see <see cref="DefaultEar"/>), so without the branch the
        /// robot network would be unreachable — the branch is what makes <c>ear='0'</c> mean "not an
        /// animal" rather than "deaf", and it is only correct together with the data.</para>
        /// </summary>
        /// <param name="distanceSquaredPixels">Squared distance from the alarm to the receiver.</param>
        /// <param name="alarmRadius">The alarm's base radius.</param>
        /// <param name="receiverEar">The <b>receiver's</b> ear.</param>
        /// <param name="receiverEarMultiplier">The <b>receiver's</b> location <c>earMult</c>. Ignored for
        /// robots, as the oracle ignores it.</param>
        /// <param name="receiverIsRobot">Whether the receiver is mechanical.</param>
        public static bool AlarmReaches(
            float distanceSquaredPixels,
            float alarmRadius,
            float receiverEar,
            float receiverEarMultiplier,
            bool receiverIsRobot)
        {
            if (alarmRadius <= 0f)
            {
                return false;
            }

            if (receiverIsRobot)
            {
                return distanceSquaredPixels < alarmRadius * alarmRadius;
            }

            float radius = alarmRadius * receiverEar * receiverEarMultiplier;
            if (radius <= 0f)
            {
                return false;
            }

            return distanceSquaredPixels < radius * radius;
        }

        /// <summary>
        /// The half-extent of the positional error an alarm hands out — AS3 <c>(Math.random() - 0.5) *
        /// spread</c> spans <c>±spread/2</c>, so the caller passes the oracle's multiplier
        /// (<see cref="UnitAlarmSpreadPixels"/>) and gets back the actual <c>±</c> figure.
        /// </summary>
        /// <param name="spread">The oracle's multiplier — 250 for a unit alarm, or the capped
        /// <c>min(radius/2, 400)</c> for a location alarm.</param>
        public static float AlarmHalfSpread(float spread)
        {
            return spread <= 0f ? 0f : spread * 0.5f;
        }

        /// <summary>
        /// AS3 <c>Location.budilo</c>'s spread multiplier, <c>_loc9_ = param3 / 2</c> capped at 400
        /// (<c>:36414-36418</c>). Pass the result to <see cref="AlarmHalfSpread"/>.
        /// </summary>
        public static float LocationAlarmSpread(float alarmRadius)
        {
            float half = alarmRadius * 0.5f;
            return half > LocationAlarmSpreadCapPixels ? LocationAlarmSpreadCapPixels : half;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  The randomised investigation point
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>setCel(null, X + (Math.random() - 0.5) * 200, Y + (Math.random() - 0.5) * 200)</c>
        /// (<c>:126483</c>) — a commit based on <b>sound alone</b> sends the enemy to the target's
        /// position with <c>±100 px</c> of error, because a sound gives a direction and a distance, not a
        /// point.
        ///
        /// <para><b>This is the difference the player feels.</b> With the port's exact-position homing, an
        /// enemy that hears a footstep walks to the pixel you were standing on; with the oracle's, it
        /// walks to somewhere near it and then has to search. It is also the reason a quiet player can
        /// still be missed by an enemy that heard him.</para>
        /// </summary>
        public const float SoundOnlyCommitSpreadPixels = 200f;

        /// <summary>
        /// AS3's <c>(Math.random() - 0.5) * spread</c> applied on both axes — returns the offset to add to
        /// the heard position.
        /// </summary>
        /// <param name="spread">The oracle's multiplier (200 for a sound-only commit, 250 for an alarm).</param>
        /// <param name="rollX">A draw in <c>[0, 1)</c> — AS3 <c>Math.random()</c>.</param>
        /// <param name="rollY">A second, independent draw in <c>[0, 1)</c>.</param>
        /// <remarks>
        /// <b>Two independent draws, not one.</b> The oracle calls <c>Math.random()</c> separately for x
        /// and y, so the error is a <i>square</i> of side <c>spread</c>, not a radial offset — the corners
        /// are reachable and the average error is larger than <c>spread/2</c> per axis. Reusing one draw
        /// for both would confine the enemy to the diagonal, which is subtle enough to survive a
        /// play-test and obvious in a fixture.
        /// </remarks>
        public static Vector2 RandomisedOffset(float spread, float rollX, float rollY)
        {
            return new Vector2((rollX - 0.5f) * spread, (rollY - 0.5f) * spread);
        }
    }
}

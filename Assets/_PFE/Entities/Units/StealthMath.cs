using System;

namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3's <b>target-side</b> stealth model — <c>Unit.visibility</c>, <c>Unit.stealthMult</c>,
    /// <c>Unit.demask</c>, <c>Unit.detecting</c>, <c>UnitPlayer.lurked</c>/<c>isSit</c> and
    /// <c>Pers.visiMult</c>/<c>sneakLurk</c> — as pure functions of their numbers.
    ///
    /// <para><b>Why this file exists, and how it differs from <see cref="NoiseMath"/>.</b>
    /// <see cref="NoiseMath"/> is the <i>listener</i> half: how an enemy hears and how suspicion
    /// accumulates. This is the <i>target</i> half: what makes a unit hard to see. The two meet in one
    /// place — AS3's <c>UnitPlayer.observation()</c>, which scales the raw sensor intensity by the
    /// target's own stealth before adding it to the meter (see
    /// <see cref="NoiseMath.ObservationInput"/>). Until this file existed the port had the listener half
    /// only, so <c>observation()</c>'s scaling collapsed to a no-op and an enemy's sight range was a
    /// flat constant. See <c>docs/StealthSystem/</c> for the full reading of the oracle.</para>
    ///
    /// <para><b>Citations</b> are into the oracle root
    /// <c>pfeToUnity\pfe\scripts\fe\</c> (paths relative to it), matching the convention of
    /// <c>docs/StealthSystem/01_ORACLE_STEALTH_END_TO_END.md</c>. Every constant below carries its
    /// <c>file:line</c>.</para>
    /// </summary>
    public static class StealthMath
    {
        // ────────────────────────────────────────────────────────────────────────────────────────
        //  visibility — the target's "how big do I look" number
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitPlayer.visibility = 2000</c> (<c>unit/UnitPlayer.as:1093</c>) — the value written
        /// at the top of every tick and kept whenever the unit is <i>not</i> still on both axes.
        ///
        /// <para><b>It is not a base that later states refine — it is a fall-through.</b> The only other
        /// write in the block is <see cref="VisibilityStill"/>, gated on <c>|dx| &lt; 2 &amp;&amp; |dy| &lt;
        /// 2</c>. So a unit falling (<c>dy</c> non-zero) or walking keeps 2000; there are exactly two
        /// values, not three.</para>
        /// </summary>
        public const int VisibilityMoving = 2000;

        /// <summary>
        /// AS3 <c>UnitPlayer.visibility = 1500</c> for a still unit (<c>unit/UnitPlayer.as:1096</c>).
        /// <c>1500 / 2000 = 0.75</c> — standing still shrinks an observer's sight range by a quarter.
        /// </summary>
        public const int VisibilityStill = 1500;

        /// <summary>
        /// The stillness test's threshold — AS3's <c>dx &lt; 2 &amp;&amp; dx &gt; -2 &amp;&amp; dy &lt; 2
        /// &amp;&amp; dy &gt; -2</c> (<c>unit/UnitPlayer.as:1096</c>). <b>Strictly less than 2, on
        /// <i>both</i> axes.</b> A unit at exactly 2 px/frame on either axis is not still.
        /// </summary>
        public const float StillVelocityThreshold = 2f;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:1127</c> — <c>visibility *= 0.5</c> while lurking. Applied <i>after</i>
        /// the still check, so a lurking unit is <c>750</c> whether it is standing still or not.
        /// </summary>
        public const float LurkVisibilityMultiplier = 0.5f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  detecting — the target's point-blank bubble
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.detecting = 80</c> (<c>unit/Unit.as:464</c>) — the radius inside which an observer
        /// sees this target <b>regardless of facing or cone</b> (<c>unit/Unit.as:4632-4634</c> returns
        /// the top intensity tier inside it).
        ///
        /// <para><b>It belongs to the target, not the observer.</b> <c>look()</c> reads
        /// <c>param1.detecting</c> — the target's — and it is the one lever that can delete the
        /// "always sees you at point blank" tier entirely. See <see cref="DetectingSuppressed"/>.</para>
        /// </summary>
        public const int DetectingBase = 80;

        /// <summary>
        /// AS3's suppressed value — <c>detecting = 0</c>, written by <c>UnitPlayer.as:1125</c> (lurking)
        /// and <c>:1131</c> (cloaked). With it, the point-blank tier and the behind-the-observer
        /// exemption both disappear (<c>unit/Unit.as:4598</c>, <c>:4632</c>).
        /// </summary>
        public const int DetectingSuppressed = 0;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:1129</c> — <c>if(stealthMult &lt; 0.5) detecting = 0;</c>. The
        /// comparison is <b>strict</b>, so <c>stealthMult == 0.5</c> exactly keeps the bubble. The
        /// shipped cloak is <c>0.2</c> (<see cref="CloakStealthMult"/>), which is below it.
        /// </summary>
        public const float StealthMultSuppressDetectingBelow = 0.5f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  demask — the anti-stealth channel
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The ceiling AS3 clamps <c>demask</c> to in every write — <c>if(this.demask &lt; 200)
        /// this.demask = 200;</c> (<c>unit/Unit.as:3765-3768</c> on taking damage; the same 200 in the
        /// movement, levitation, telekinesis and weapon-fire writers).
        ///
        /// <para><b>It is added to the sight range <i>unscaled</i>.</b> <c>look()</c> is
        /// <c>(visibility · stealthMult · locVisMult + demask) · vision</c> (<c>unit/Unit.as:4587</c>),
        /// so stealth gear cannot hide a muzzle flash — 200 px of reveal survives
        /// <c>stealthMult = 0.2</c>. That asymmetry is the design: stealth is about not acting.</para>
        /// </summary>
        public const float DemaskMax = 200f;

        /// <summary>
        /// AS3's per-tick decay, <c>if(this.demask &gt; 0) this.demask -= 5;</c>
        /// (<c>unit/Unit.as:3139-3142</c>). At 30 Hz that is 150/second, so the 200 peak lasts
        /// <b>40 ticks ≈ 1.33 s</b>.
        /// </summary>
        public const float DemaskDecayPerTick = 5f;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:1098-1103</c> — the fast-move reveal. The threshold is
        /// <c>dx &gt; 10 || dx &lt; -10 || dy &gt; 10 || dy &lt; -10</c>, and the value raised to is
        /// <c>200 * pers.visiMult</c>, <b>scaled by <c>visiMult</c></b> — so armour <c>sneak</c> does
        /// damp the reveal from a sprint, unlike the other four writers.
        /// </summary>
        public const float FastMoveDx = 10f;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:3108</c> — <c>_loc3_ = this.sneak - demask / 20;</c>. Twenty points of
        /// reveal cancel one point of sneak. See <see cref="ObservationBase"/>.
        /// </summary>
        public const float DemaskPerSneak = 20f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the stealth multipliers
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>AS3 <c>Pers.visiMult = 1</c> (<c>unit/Pers.as:305</c>) — the neutral value.</summary>
        public const float DefaultVisiMult = 1f;

        /// <summary>AS3 <c>Unit.stealthMult = 1</c> (<c>unit/Unit.as:462</c>) — the neutral value.</summary>
        public const float DefaultStealthMult = 1f;

        /// <summary>
        /// AS3's cloak value — <c>stealthMult = 0.2</c>, set by both the <c>stealth</c> and
        /// <c>stealth_armor</c> effects (<c>AllData.as:5948</c>, <c>:5952</c>). A flat 80 % cut, not a
        /// per-item curve. It is below <see cref="StealthMultSuppressDetectingBelow"/>, so the cloak
        /// also deletes the point-blank bubble.
        /// </summary>
        public const float CloakStealthMult = 0.2f;

        /// <summary>
        /// The <c>visibility</c> value at which an observer's range is exactly its base — i.e. the
        /// reference the port calibrates against.
        ///
        /// <para><b>This is the one port-side calibration in this file, and it is deliberate.</b> The
        /// oracle's absolute range is <c>(2000 · 1 · 1 + 0) · 1 = 2000 px</c> for a moving player, while
        /// the port's <c>EnemySensors.VisionRangePixels</c> is <c>480</c>. Wiring the oracle's absolute
        /// number would multiply every enemy's sight range by 4.17 in one step — an unverifiable gameplay
        /// change that has nothing to do with stealth. So the port expresses the <i>same formula</i>
        /// relative to its own calibrated base: a target at
        /// <see cref="VisibilityMoving"/> gets the current range, and the stealth channels scale it
        /// <i>down</i> from there. The absolute-range divergence is named in
        /// <c>docs/StealthSystem/03_GAP_LEDGER.md</c> §3 and is <b>not</b> touched here.</para>
        /// </summary>
        public const float ReferenceVisibility = VisibilityMoving;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  sneakLurk — the posture bonus
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Pers.sneakLurk = 5</c> (<c>unit/Pers.as:311</c>) — the <b>flat amount subtracted</b>
        /// from the suspicion contribution while lurking (<c>unit/UnitPlayer.as:3125</c>). The
        /// <c>sneak</c> skill raises it to <c>2.5 + 0.5·level</c> (<c>AllData.as:5282</c>).
        /// </summary>
        public const float DefaultSneakLurk = 5f;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:3131</c> — <c>if(isSit) param1 -= this.pers.sneakLurk / 5;</c>. Sitting
        /// is a fifth of lurking, and it does <i>not</i> zero <c>detecting</c> or halve
        /// <c>visibility</c>.
        /// </summary>
        public const float SitSneakLurkDivisor = 5f;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the state
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The target-side stealth state — the seven numbers AS3 recomputes every tick in
        /// <c>unit/UnitPlayer.as:1089-1132</c>.
        ///
        /// <para>A struct rather than loose fields so <see cref="Tick"/> is the only thing that advances
        /// them, and so the whole state can be passed to the two readers (the sensors' range product and
        /// the suspicion scaling) without either reaching into a MonoBehaviour.</para>
        /// </summary>
        public struct StealthState
        {
            /// <summary>AS3 <c>visibility</c> — <see cref="VisibilityMoving"/>/<see cref="VisibilityStill"/>, ×½ while lurking.</summary>
            public int Visibility;

            /// <summary>
            /// AS3 <c>Pers.visiMult</c> — the armour/perk concealment multiplier. <b>Scales the
            /// suspicion contribution only, never the sight range</b> (<c>visiMult</c> does not appear
            /// anywhere in <c>unit/Unit.as</c>).
            /// </summary>
            public float VisiMult;

            /// <summary>AS3 <c>Unit.stealthMult</c> — 1, or <see cref="CloakStealthMult"/> while cloaked. Scales both the range and the suspicion.</summary>
            public float StealthMult;

            /// <summary>AS3 <c>Unit.demask</c> — 0…<see cref="DemaskMax"/>.</summary>
            public float Demask;

            /// <summary>AS3 <c>Pers.sneakLurk</c> — the flat subtraction applied while lurking/sitting.</summary>
            public float SneakLurk;

            /// <summary>AS3 <c>UnitPlayer.lurked</c> — in cover. Zeroes <c>detecting</c> and halves <c>visibility</c>.</summary>
            public bool Lurked;

            /// <summary>AS3 <c>Unit.isSit</c> — resting. Only the <c>sneakLurk/5</c> subtraction.</summary>
            public bool IsSit;

            /// <summary>AS3 <c>Unit.detecting</c> — <see cref="DetectingBase"/> or <see cref="DetectingSuppressed"/>.</summary>
            public int Detecting;

            /// <summary>
            /// The neutral state: a moving, uncloaked, ungeared unit at rest.
            ///
            /// <para><b>The default <c>Visibility</c> is <see cref="VisibilityMoving"/>, not the
            /// oracle's <c>Unit.visibility = 1000</c>.</b> That is the calibration
            /// <see cref="ReferenceVisibility"/> documents: a non-player target keeps the port's current
            /// range instead of having it halved. Flagged as a divergence in
            /// <c>docs/StealthSystem/03_GAP_LEDGER.md</c> §2, not hidden.</para>
            /// </summary>
            public static StealthState Default
            {
                get
                {
                    StealthState s;
                    s.Visibility = VisibilityMoving;
                    s.VisiMult = DefaultVisiMult;
                    s.StealthMult = DefaultStealthMult;
                    s.Demask = 0f;
                    s.SneakLurk = DefaultSneakLurk;
                    s.Lurked = false;
                    s.IsSit = false;
                    s.Detecting = DetectingBase;
                    return s;
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the per-tick writes
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>visibility</c> block (<c>unit/UnitPlayer.as:1093-1096, 1127</c>).
        /// </summary>
        /// <param name="stillOnBothAxes">
        /// AS3's <c>dx &lt; 2 &amp;&amp; dx &gt; -2 &amp;&amp; dy &lt; 2 &amp;&amp; dy &gt; -2</c>.
        /// <b>Both</b> axes; a falling or walking unit is not still.
        /// </param>
        /// <param name="lurked">AS3 <c>UnitPlayer.lurked</c>.</param>
        public static int Visibility(bool stillOnBothAxes, bool lurked)
        {
            int visibility = stillOnBothAxes ? VisibilityStill : VisibilityMoving;

            if (lurked)
            {
                // The oracle multiplies the field in place; as an int the truncation is the oracle's
                // own (1500 * 0.5 = 750, 2000 * 0.5 = 1000) and both are exact, so no rounding choice
                // is being made here.
                visibility = (int)(visibility * LurkVisibilityMultiplier);
            }

            return visibility;
        }

        /// <summary>
        /// AS3's <c>detecting</c> block (<c>unit/UnitPlayer.as:1118, 1125, 1131</c>):
        /// <c>detecting = 80; if(lurked) detecting = 0; if(stealthMult &lt; 0.5) detecting = 0;</c>.
        /// </summary>
        /// <remarks>
        /// <b>Strictly less than <see cref="StealthMultSuppressDetectingBelow"/>.</b> A
        /// <c>stealthMult</c> of exactly <c>0.5</c> keeps the bubble; only a value below it deletes the
        /// point-blank tier. Asserted, because <c>&lt;=</c> is the natural "tidy-up" and it is wrong.
        /// </remarks>
        public static int Detecting(bool lurked, float stealthMult)
        {
            if (lurked || stealthMult < StealthMultSuppressDetectingBelow)
            {
                return DetectingSuppressed;
            }

            return DetectingBase;
        }

        /// <summary>
        /// AS3's per-tick demask decay, <c>if(demask &gt; 0) demask -= 5;</c>
        /// (<c>unit/Unit.as:3139-3142</c>).
        /// </summary>
        /// <remarks>
        /// <b>Deliberate divergence: floored at zero.</b> AS3 does not clamp, so a <c>demask</c> set to a
        /// value that is not a multiple of 5 steps below zero and stays there. Nothing reads a negative
        /// <c>demask</c> as meaningful, but <c>look()</c> <i>adds</i> it to the range
        /// (<c>unit/Unit.as:4587</c>), so a negative value would <i>shrink</i> the range below the
        /// stealth value — an invisible wrong-direction bug. The clamp costs nothing observable, for the
        /// same reason <see cref="NoiseMath.TickNoise"/> clamps <c>noise</c>.
        /// </remarks>
        public static float TickDemask(float demask)
        {
            if (demask <= 0f)
            {
                return 0f;
            }

            float next = demask - DemaskDecayPerTick;
            return next < 0f ? 0f : next;
        }

        /// <summary>
        /// AS3's demask <i>raise</i> — the shared body of all four "you just did something loud" writes
        /// (<c>unit/Unit.as:3765-3768</c> on a hit, <c>unit/UnitPlayer.as:1113-1116</c> levitation,
        /// <c>:1239-1242</c> telekinesis, <c>weapon/Weapon.as:1575-1578</c> firing).
        /// </summary>
        /// <remarks>
        /// <b>Raise-only, never assign.</b> Every oracle write is guarded by
        /// <c>if(this.demask &lt; X) this.demask = X;</c>, so a quiet event during a loud one cannot
        /// lower the reveal — the same shape as <see cref="NoiseMath.MakeNoise"/>, and for the same
        /// reason: an assignment would make a footstep mute a gunshot.
        /// </remarks>
        /// <param name="demask">The current value.</param>
        /// <param name="amount">The value to raise to. Non-positive is ignored.</param>
        public static float RaiseDemask(float demask, float amount)
        {
            if (amount <= 0f)
            {
                return demask;
            }

            return amount > demask ? amount : demask;
        }

        /// <summary>
        /// AS3's fast-move reveal (<c>unit/UnitPlayer.as:1098-1103</c>) — the demask a unit raises by
        /// moving faster than <see cref="FastMoveDx"/> on either axis, or <c>0</c> for "do not raise".
        /// </summary>
        /// <remarks>
        /// <b>The value is <c>200 × visiMult</c>, and it is the only writer scaled by <c>visiMult</c>.</b>
        /// Levitation, telekinesis, a hit and a shot all write a flat 200 regardless of gear; running
        /// does not. That is the oracle's, and it is what lets stealth armour damp a sprint without
        /// hiding a gunshot.
        /// </remarks>
        /// <param name="dx">Signed horizontal velocity, px/frame.</param>
        /// <param name="dy">Signed vertical velocity, px/frame.</param>
        /// <param name="visiMult">AS3 <c>Pers.visiMult</c>.</param>
        public static float MovementDemask(float dx, float dy, float visiMult)
        {
            float speed = Math.Abs(dx) > Math.Abs(dy) ? Math.Abs(dx) : Math.Abs(dy);
            if (speed <= FastMoveDx)
            {
                return 0f;
            }

            return DemaskMax * visiMult;
        }

        /// <summary>
        /// AS3's two <b>state</b> reveals — levitation (<c>unit/UnitPlayer.as:1113-1116</c>) and
        /// telekinesis (<c>:1239-1242</c>), each <c>if(&lt;state&gt; &amp;&amp; demask &lt; 200) demask = 200</c>.
        /// Returns <see cref="DemaskMax"/> while either state holds, else <c>0</c> for "do not raise".
        /// </summary>
        /// <remarks>
        /// <para><b>Per-tick state, not events</b> — which is why these live here beside the fast-move
        /// raise rather than at a call site. AS3 runs both inside <c>UnitPlayer</c>'s own step block,
        /// every frame the state holds: levitating for 20 frames writes 20 times. The <c>max</c> in
        /// <see cref="RaiseDemask"/> makes that idempotent, and the decay
        /// (<see cref="DemaskDecayPerTick"/> = 5) is what ends the reveal once the state drops.</para>
        ///
        /// <para><b>A flat 200, not scaled by <c>visiMult</c>.</b> Unlike the fast-move raise
        /// (<see cref="MovementDemask"/>, <c>200 × visiMult</c>), these two write the oracle's literal
        /// <c>200</c> regardless of gear — stealth armour damps a sprint, not a levitation.</para>
        ///
        /// <para>Weapon fire and taking a hit are <b>not</b> here: they are true events and reach
        /// <see cref="RaiseDemask"/> from their own call sites, which is how AS3 reaches them (a shot
        /// inside <c>Weapon.shoot()</c>, a hit inside <c>Unit.damage()</c>).</para>
        /// </remarks>
        /// <param name="levitating">AS3 <c>levit &gt; 0</c>.</param>
        /// <param name="telekinesisHolding">AS3 <c>this.teleObj</c> being non-null.</param>
        public static float StateRevealDemask(bool levitating, bool telekinesisHolding)
            => levitating || telekinesisHolding ? DemaskMax : 0f;

        /// <summary>
        /// One step of the whole target-side state — the port's stand-in for AS3's
        /// <c>UnitPlayer</c> per-tick block (<c>unit/UnitPlayer.as:1089-1132</c>) plus the demask decay
        /// that lives in <c>Unit.step</c> (<c>unit/Unit.as:3139-3142</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Order: decay, then the fast-move raise, then the state raises.</b> AS3 splits these
        /// across two functions (<c>Unit.step</c> and <c>UnitPlayer</c>'s own block) whose relative
        /// order the port cannot observe, so the choice is made for the behaviour it produces: a unit
        /// that is still running reads a full <see cref="DemaskMax"/>, not <c>DemaskMax − 5</c>.
        /// Reversing the decay and any raise costs 5 per tick and is invisible in play — which is
        /// exactly why it is a decision and not an accident.</para>
        ///
        /// <para>Weapon fire and taking a hit are <b>not</b> here: they are events and reach
        /// <see cref="RaiseDemask"/> from their own call sites (see <see cref="StateRevealDemask"/>'s
        /// remarks).</para>
        /// </remarks>
        /// <param name="state">The state to advance.</param>
        /// <param name="dx">This step's signed horizontal velocity, px/frame.</param>
        /// <param name="dy">This step's signed vertical velocity, px/frame.</param>
        /// <param name="levitating">AS3 <c>levit &gt; 0</c> — see <see cref="StateRevealDemask"/>.</param>
        /// <param name="telekinesisHolding">AS3 <c>this.teleObj</c> — see <see cref="StateRevealDemask"/>.</param>
        public static StealthState Tick(StealthState state, float dx, float dy,
            bool levitating = false, bool telekinesisHolding = false)
        {
            state.Demask = TickDemask(state.Demask);

            float movement = MovementDemask(dx, dy, state.VisiMult);
            if (movement > 0f)
            {
                state.Demask = RaiseDemask(state.Demask, movement);
            }

            float posture = StateRevealDemask(levitating, telekinesisHolding);
            if (posture > 0f)
            {
                state.Demask = RaiseDemask(state.Demask, posture);
            }

            bool still = Math.Abs(dx) < StillVelocityThreshold && Math.Abs(dy) < StillVelocityThreshold;
            state.Visibility = Visibility(still, state.Lurked);
            state.Detecting = Detecting(state.Lurked, state.StealthMult);

            return state;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the readers
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit.look()</c>'s sight range (<c>unit/Unit.as:4585-4587</c>):
        /// <c>_loc7_ = (param1.visibility * param1.stealthMult * loc.visMult + param1.demask)
        /// * (param3 ? param3 : this.vision)</c>.
        /// </summary>
        /// <remarks>
        /// <para><b><c>visiMult</c> is deliberately absent, and that is not an omission.</b> The armour
        /// <c>sneak</c> channel lands in <c>Pers.visiMult</c> and the whole oracle mentions it four
        /// times — none of them in <c>unit/Unit.as</c>. It scales the <i>suspicion</i> contribution
        /// (<see cref="NoiseMath.ObservationInput"/>) and never the sight range. A port that folded it
        /// in here would make armour <c>sneak</c> do two jobs the oracle gave it one of.</para>
        ///
        /// <para><b><c>demask</c> is inside the sum and therefore multiplied by the observer's
        /// <c>vision</c>, but not by the target's stealth.</b> That is the whole anti-stealth design:
        /// gear cannot shrink the reveal from acting.</para>
        ///
        /// <para><b>The absolute scale is the port's, not the oracle's.</b>
        /// <paramref name="baseRangePixels"/> is the observer's calibrated range at
        /// <see cref="ReferenceVisibility"/>; the oracle's own absolute range would be
        /// <c>visibility · vision</c>, which is 4.17× the port's for a moving player. See
        /// <see cref="ReferenceVisibility"/>.</para>
        /// </remarks>
        /// <param name="baseRangePixels">The observer's range with a reference-visibility, uncloaked, ungeared target — the port's <c>EnemySensors.VisionRangePixels</c>.</param>
        /// <param name="visibility">The target's <c>visibility</c>.</param>
        /// <param name="stealthMult">The target's <c>stealthMult</c>.</param>
        /// <param name="locVisMult">The location's <c>visMult</c> — AS3 <c>Location.visMult</c>, the room/land brightness.</param>
        /// <param name="demask">The target's <c>demask</c>.</param>
        /// <param name="observerVision">AS3 <c>this.vision</c> — the observer's own vision scale. Defaults to 1, the oracle's declared default.</param>
        public static float EffectiveSightRangePixels(
            float baseRangePixels,
            int visibility,
            float stealthMult,
            float locVisMult,
            float demask,
            float observerVision = 1f)
        {
            if (baseRangePixels <= 0f || observerVision <= 0f)
            {
                return 0f;
            }

            float visibilityRatio = visibility / ReferenceVisibility;
            float range = baseRangePixels * visibilityRatio * stealthMult * locVisMult + demask;

            return range > 0f ? range * observerVision : 0f;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.observation()</c>'s base term (<c>unit/UnitPlayer.as:3108-3110</c>):
        /// <c>_loc3_ = this.sneak - demask / 20; if(_loc3_ &lt; 0) _loc3_ = 0;</c>.
        /// </summary>
        /// <remarks>
        /// <b><c>sneak</c> is a dead field in the oracle.</b> <c>UnitPlayer.sneak</c>
        /// (<c>unit/UnitPlayer.as:151</c>) is declared and read here but <b>assigned nowhere in the
        /// entire oracle</b> — the only write to a field named <c>sneak</c> is <c>Armor.sneak</c>
        /// (<c>unit/Armor.as:271</c>), which is a different field feeding <c>visiMult</c>. So this term
        /// is always <c>≤ 0</c> and clamps to 0 in the shipped game. It is implemented rather than
        /// dropped because the parameter exists and a caller may pass one; see
        /// <c>docs/StealthSystem/05_VERIFICATION_AND_QUIRKS.md</c> Q1.
        /// </remarks>
        public static float ObservationBase(float sneak, float demask)
        {
            float baseValue = sneak - demask / DemaskPerSneak;
            return baseValue < 0f ? 0f : baseValue;
        }
    }
}

using System;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// <c>UnitTurret</c>'s <b><c>aiState</c> ladder</b> — the five states a mount walks through, and the
    /// numbers that make it walk them — transcribed from <c>fe/unit/UnitTurret.as</c>.
    ///
    /// <para><b>Why this exists, and what was wrong without it.</b> The port had a single boolean
    /// ("committed or not") and drove the mount's <i>state lamp</i> from
    /// <c>EnemyBrain.CurrentState</c> — a <b>generic</b> enemy enum whose 0/1/2 happen to coincide with
    /// the turret's and whose 3 is <c>CombatChase</c>, not "attacking". Worse, <c>TrapBrain</c> never
    /// wrote that enum at all outside <see cref="TrapBrain.Disabled"/>, so a turret's lamp stayed on
    /// <b>frame 1 for its whole life</b> — and frame 1 of <c>turret_light_1277</c> is the <i>empty</i>
    /// frame. The lamp the oracle lights in three colours never lit at all. See
    /// <see cref="LampStateIndex"/>'s call site in <c>TurretPartPresenter</c>.</para>
    ///
    /// <para><b>The ladder, with the lamp frame it produces</b> (lamp = <c>aiState + 1</c>,
    /// <c>UnitTurret.as:310-313</c>; frame 1 is empty in the symbol):</para>
    /// <code>
    /// 0 Dormant        shutter closed, barrel parked       lamp 1  (dark)
    /// 1 Watch          sweeps aRot, cone-limited           lamp 2  (yellow)
    /// 2 Spotted        has a target, 30..39 tick delay     lamp 3  (ORANGE — "it has seen you")
    /// 3 Attacking      committed, fires every tick         lamp 4  (RED    — "it is shooting you")
    /// 4 LosingInterest target gone, 1..29 ticks of budget  lamp 5  (orange again)
    /// </code>
    ///
    /// <para><b>State 2 is the one the port was missing entirely.</b> The oracle does not fire the
    /// instant <c>findCel()</c> succeeds: it enters 2, waits <c>floor(random*10)+30</c> ticks
    /// (<c>:659</c>), and only then promotes to 3 and calls <c>budilo(750)</c> (<c>:584-586</c>). That
    /// delay <i>is</i> the orange stage — a player who breaks line of sight inside it never gets
    /// shot.</para>
    ///
    /// <para><b>Why a static class over a struct.</b> A <c>MonoBehaviour</c> cannot be exercised
    /// offline, and every rule below is a pure function of (state, timers, whether a target is
    /// visible). Written on the brain they would be reachable only through a scene, which is how
    /// "the lamp looks fine to me" ships.</para>
    /// </summary>
    public static class TurretAIStateMath
    {
        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the states
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>aiState 0</c> — <b>dormant</b>. Only a <c>hidden</c> mount (<c>hturret</c>,
        /// <c>hturret2</c>) has one: the shutter is closed, the barrel parks at
        /// <c>forceRot = π/2</c> and vision is cut to <see cref="DormantVisionScale"/>
        /// (<c>UnitTurret.as:597-601</c>, <c>:695-699</c>).</summary>
        public const int Dormant = 0;

        /// <summary><c>aiState 1</c> — <b>watch</b>. The barrel walks <c>aRot</c> one entry every
        /// <c>period</c> ticks and the mount is <i>cone-limited</i>: <c>overLook = false</c>
        /// (<c>:703-715</c>), so a target behind it is only seen inside the 80 px bubble.</summary>
        public const int Watch = 1;

        /// <summary><c>aiState 2</c> — <b>spotted</b>. A target has been found and the mount is
        /// counting down <c>aiTCh</c> (30..39 ticks) before it commits (<c>:648-661</c>). The barrel
        /// already tracks (<c>findCel = true</c>, <c>drot = actDrot</c>), but nothing is fired. This is
        /// the orange lamp.</summary>
        public const int Spotted = 2;

        /// <summary><c>aiState 3</c> — <b>attacking</b>. Committed: <c>control()</c> calls
        /// <c>currentWeapon.attack()</c> every tick while <c>World.w.enemyAct &gt;= 3</c>
        /// (<c>:737-740</c>). The red lamp.</summary>
        public const int Attacking = 3;

        /// <summary><c>aiState 4</c> — <b>losing interest</b>. The target has gone; <c>aiSpok</c> is
        /// draining from 29 down to 1 and the mount keeps tracking the last position at
        /// <c>watchDrot</c> (<c>:621-624</c>, <c>:723-726</c>).</summary>
        public const int LosingInterest = 4;

        /// <summary>How many states the ladder has — <c>aiState</c> never leaves 0..4 for a turret.</summary>
        public const int StateCount = 5;

        /// <summary>
        /// Frames in <c>turret_light_1277</c> — the lamp symbol has six, and
        /// <c>vis.osn.light.gotoAndStop(aiState + 1)</c> (<c>UnitTurret.as:310-313</c>) only ever reaches
        /// the first five. Frame 6 is the boss mount's (<c>aiState 5</c>), which no shipped turret uses.
        /// </summary>
        public const int LampFrameCount = 6;

        /// <summary>
        /// The lamp frame a state lights — <c>aiState + 1</c>, clamped into the symbol's six frames.
        ///
        /// <code>
        /// aiState 0 -> frame 1   EMPTY   (dormant / hacked off — no light at all)
        /// aiState 1 -> frame 2   yellow  (watching)
        /// aiState 2 -> frame 3   ORANGE  (spotted you, still deciding)
        /// aiState 3 -> frame 4   RED     (attacking)
        /// aiState 4 -> frame 5   orange  (lost you, still searching)
        /// </code>
        ///
        /// <para>Frame 1 being empty is the symbol's own design, not a missing asset — which is why an
        /// idle mount shows no lamp and that is correct. Clamped rather than wrapped: a wrap would draw
        /// the red attack light for a state the ladder never produces.</para>
        /// </summary>
        public static int LampFrame(int state)
        {
            int frame = state + 1;
            if (frame < 1) return 1;
            return frame > LampFrameCount ? LampFrameCount : frame;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  vision — the field that makes "too blind" measurable
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>vision = 0.35</c> in <c>aiState 0</c> (<c>UnitTurret.as:695-699</c>). A closed shutter
        /// sees a third as far as an open one, and a <c>hidden2</c> mount sees <b>nothing at all</b>
        /// (<c>:696-699</c> sets <c>vision = 0</c>, which <c>look()</c> turns into an outright
        /// <c>return 0</c> at <c>Unit.as:4573</c>). That second arm is <see cref="DormantVisionScaleBlind"/>
        /// — it is a separate value, not a rounding of this one.
        /// </summary>
        public const float DormantVisionScale = 0.35f;

        /// <summary>
        /// The <c>hidden2</c> arm — <c>UnitTurret.as:696-699</c>'s <c>if(this.hidden == 2)
        /// vision = 0</c>. A completely inert mount: no sight, no point-blank tier
        /// (<c>Unit.as:4573</c> returns before the <c>detecting</c> test).
        /// </summary>
        public const float DormantVisionScaleBlind = 0f;

        /// <summary>
        /// <c>vision = 1</c> in every state from 1 up (<c>UnitTurret.as:712</c>, <c>:732</c>). The
        /// oracle's own declared default (<c>Unit.as:484</c>), and what every mount in the shipped
        /// data runs on.
        /// </summary>
        public const float AwakeVisionScale = 1f;

        /// <summary>
        /// The <c>vision</c> scale for a state — <c>UnitTurret.as:695-734</c>. <paramref name="hidden"/>
        /// is the mount's <c>hidden</c> field (1 = shutter, 2 = inert), because the <c>hidden2</c> arm
        /// lives inside the <c>aiState 0</c> body and cannot be expressed by the state alone.
        /// </summary>
        public static float VisionScale(int state, int hidden = 1)
        {
            if (state != Dormant) return AwakeVisionScale;
            return hidden == 2 ? DormantVisionScaleBlind : DormantVisionScale;
        }

        /// <summary>
        /// Whether the mount ignores its facing cone this state — <c>UnitTurret.control()</c>'s
        /// <c>overLook</c> writes: <b>true</b> in 0 (<c>:693</c>), <b>false</b> in 1 (<c>:711</c>),
        /// <b>true</b> from 2 up (<c>:731</c>).
        ///
        /// <para><b>This is the second half of "too blind".</b> <c>findCel()</c> passes
        /// <c>this.overLook</c> as <c>look()</c>'s <c>param2</c>, and <c>param2</c> is what skips the
        /// behind-the-back rejection (<c>Unit.as:4598</c>). A watching mount is a half-plane observer;
        /// a <i>committed</i> one sees 360°, which is why a turret that has engaged you does not lose
        /// you when you step behind its barrel. The port applied the half-plane in every state.</para>
        /// </summary>
        public static bool Omnidirectional(int state) => state != Watch;

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  what each state does with the weapon
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the barrel tracks the target instead of the sweep — <c>currentWeapon.findCel</c>,
        /// written <c>true</c> for every state from 2 up (<c>:722</c>, <c>:692</c> false for 0,
        /// <c>:709</c> false for 1).
        /// </summary>
        public static bool AimsAtTarget(int state) => state >= Spotted;

        /// <summary>
        /// Whether the mount pulls the trigger — <c>if(World.w.enemyAct &gt;= 3 &amp;&amp; aiState == 3)
        /// currentWeapon.attack()</c> (<c>:737-740</c>). <b>Only state 3</b>: a spotted mount tracks
        /// but holds fire, and a losing-interest mount has already given up.
        /// </summary>
        public static bool Fires(int state) => state == Attacking;

        /// <summary>
        /// The barrel's easing rate for a state, in radians per frame — <c>currentWeapon.drot</c>
        /// (<c>:694</c> 0.5 dormant, <c>:710</c> watchDrot, <c>:725</c> watchDrot while losing interest,
        /// <c>:729</c> actDrot while committed).
        /// </summary>
        public static float Drot(int state, int tip)
        {
            switch (state)
            {
                case Dormant: return TurretSweepMath.HiddenDrot;
                case Watch:
                case LosingInterest: return TurretSweepMath.WatchDrot(tip);
                default: return TurretSweepMath.ActDrot;
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the timers
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>Unit.maxSpok = 30</c> (<c>Unit.as:362</c>) — the awareness budget a mount holds
        /// while it can see you.</summary>
        public const int MaxSpok = 30;

        /// <summary>
        /// <c>aiSpok = maxSpok + 10</c> — written on a commit (<c>UnitTurret.as:584</c>), on an alarm
        /// (<c>:359</c>) and on every successful look while attacking (<c>:635</c>). The <c>+10</c> is
        /// why the mount stays <b>red</b> for its first ten lost looks before it even reaches
        /// <see cref="LosingInterest"/>.
        /// </summary>
        public const int SpokOnSighting = MaxSpok + 10;

        /// <summary><c>aiTCh = floor(random * 10) + 30</c> — the spotted delay
        /// (<c>UnitTurret.as:659</c>).</summary>
        public const int SpottedDelayMinTicks = 30;

        /// <summary>The span of <see cref="SpottedDelayMinTicks"/>'s roll — <c>random * 10</c>.</summary>
        public const int SpottedDelaySpanTicks = 10;

        /// <summary><c>aiTCh = floor(random * 50) + 40</c> — the re-look interval while the mount still
        /// has budget (<c>UnitTurret.as:619</c>).</summary>
        public const int RetryMinTicks = 40;

        /// <summary>The span of <see cref="RetryMinTicks"/>'s roll — <c>random * 50</c>.</summary>
        public const int RetrySpanTicks = 50;

        /// <summary><c>floor(roll01 * span) + min</c>, with the roll clamped to <c>[0,1)</c> so a caller
        /// that hands over exactly <c>1f</c> cannot step one past the oracle's range.</summary>
        public static int RollTicks(float roll01, int min, int span)
        {
            if (roll01 < 0f) roll01 = 0f;
            if (roll01 >= 1f) roll01 = 0.99999994f;
            return (int)(roll01 * span) + min;
        }

        /// <summary>The spotted delay — <c>UnitTurret.as:659</c>.</summary>
        public static int SpottedDelayTicks(float roll01)
            => RollTicks(roll01, SpottedDelayMinTicks, SpottedDelaySpanTicks);

        /// <summary>The re-look interval — <c>UnitTurret.as:619</c>.</summary>
        public static int RetryTicks(float roll01)
            => RollTicks(roll01, RetryMinTicks, RetrySpanTicks);

        // ────────────────────────────────────────────────────────────────────────────────────────
        //  the ladder
        // ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One mount's live <c>aiState</c> / <c>aiTCh</c> / <c>aiSpok</c>, plus the two side effects a
        /// step can have. Kept as a struct so <see cref="Advance"/> is a pure function of it and a
        /// fixture can drive a whole engagement without a scene.
        /// </summary>
        public struct Step
        {
            /// <summary><c>UnitTurret.aiState</c>.</summary>
            public int State;

            /// <summary><c>Unit.aiTCh</c> — the shared countdown.</summary>
            public int Ticks;

            /// <summary><c>Unit.aiSpok</c> — the awareness budget.</summary>
            public int Spok;

            /// <summary>Set for one step when the watch sweep must step to its next <c>aRot</c> entry
            /// (<c>UnitTurret.as:608-613</c>).</summary>
            public bool AdvanceSweep;

            /// <summary>Set for one step when the 2 → 3 promotion must call <c>budilo(750)</c>
            /// (<c>UnitTurret.as:586</c>).</summary>
            public bool RangAlarm;

            /// <summary>
            /// The state a freshly placed mount starts in — <c>UnitTurret.putLoc</c>'s
            /// <c>aiState = 1</c> (<c>:255</c>), or <c>aiState = 0</c> for a hidden one
            /// (<c>:256-261</c>). <c>aiTCh</c> is left at 0, which is what makes the first
            /// <see cref="Advance"/> take the "countdown expired" branch and start the watch period.
            /// </summary>
            public static Step Initial(bool hidden)
            {
                Step s;
                s.State = hidden ? Dormant : Watch;
                s.Ticks = 0;
                s.Spok = 0;
                s.AdvanceSweep = false;
                s.RangAlarm = false;
                return s;
            }
        }

        /// <summary>
        /// One tick of <c>UnitTurret.control()</c> — <b>both</b> of its blocks, in the oracle's order:
        /// the cadence chain (<c>:576-629</c>), which runs every tick, then the perception block
        /// (<c>:638-674</c>), which runs only on the <c>aiTCh % 10 == 1</c> ticks.
        ///
        /// <para><b>Why the two are one function.</b> They share all three fields, and the promotion to
        /// state 2 is <i>inside</i> the perception block while the promotion to state 3 is inside the
        /// cadence chain — splitting them would let a caller run one without the other, which is
        /// exactly the "he spotted me but never got angry" shape. <paramref name="lookedThisTick"/>
        /// is the caller's <c>aiTCh % 10 == 1</c>.</para>
        ///
        /// <para><b><paramref name="hasTarget"/> is <c>celUnit != null</c>, not "in range".</b> The
        /// oracle's cadence chain reads the field <c>findCel()</c> left behind; it does not re-test
        /// distance. A caller that passed "target within N px" would re-introduce the invented
        /// engagement radius this file's sibling removed.</para>
        /// </summary>
        /// <param name="s">The mount's current step.</param>
        /// <param name="hasTarget">The oracle's <c>celUnit != null</c>.</param>
        /// <param name="hidden">The mount's <c>hidden</c> field (0, 1 or 2).</param>
        /// <param name="periodTicks">The watch period — <see cref="TurretSweepMath.PeriodTicks"/>.</param>
        /// <param name="roll01">One draw in [0,1), used only when a timer is (re)rolled.</param>
        /// <param name="lookedThisTick">Whether this is a <c>aiTCh % 10 == 1</c> tick.</param>
        public static Step Advance(Step s, bool hasTarget, int hidden, int periodTicks,
                                   float roll01, bool lookedThisTick)
        {
            s.AdvanceSweep = false;
            s.RangAlarm = false;

            // ── `if(aiTCh > 0) --aiTCh;` (:576-579) ───────────────────────────────────────────────
            //
            // Decrement FIRST and do nothing else this tick when it was positive. That is why the
            // spotted delay is a real 30..39 ticks and not 29..38.
            if (s.Ticks > 0)
            {
                s.Ticks--;
            }
            else if (s.State == Spotted)
            {
                // ── `else if(aiState == 2)` (:580-592) ────────────────────────────────────────────
                if (hasTarget)
                {
                    s.Spok = SpokOnSighting;
                    s.State = Attacking;
                    s.RangAlarm = true;   // budilo(750)
                }
                else
                {
                    s.State = Watch;      // the target slipped away inside the delay
                }
            }
            else
            {
                // ── the `else` (:593-629) ────────────────────────────────────────────────────────
                if (s.Spok <= 0)
                {
                    if (s.State == Watch && hidden > 0)
                    {
                        s.State = Dormant;
                    }
                    else if (s.State > 0)
                    {
                        s.State = Watch;
                    }

                    if (s.State == Watch)
                    {
                        s.AdvanceSweep = true;
                    }

                    s.Ticks = periodTicks;
                }
                else
                {
                    s.Ticks = RetryTicks(roll01);
                }

                if (s.Spok > 0)
                {
                    s.State = LosingInterest;
                }

                if (s.Spok >= MaxSpok)
                {
                    s.State = Attacking;
                }
            }

            if (!lookedThisTick)
            {
                return s;
            }

            // ── `if(enemyAct > 1 && aiTCh % 10 == 1) { … if(!stun && findCel()) … }` (:638-674) ────
            if (hasTarget)
            {
                if (s.State <= Watch)
                {
                    s.State = Spotted;
                    s.Ticks = SpottedDelayTicks(roll01);
                }
                else if (s.State >= Attacking)
                {
                    s.State = Attacking;
                }
            }
            else if (s.Spok > 0)
            {
                s.Spok--;
            }

            return s;
        }

        /// <summary>
        /// <c>alarma()</c>'s effect on the ladder — <c>UnitTurret.as:343-362</c>: the mount jumps
        /// straight to <see cref="Attacking"/> with a full budget, skipping the spotted delay. Used when
        /// it takes a hit or a neighbour shouts.
        /// </summary>
        public static Step Alarm(Step s)
        {
            s.State = Attacking;
            s.Spok = SpokOnSighting;
            s.AdvanceSweep = false;
            s.RangAlarm = false;
            return s;
        }
    }
}

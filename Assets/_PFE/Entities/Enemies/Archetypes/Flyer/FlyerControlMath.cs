using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The flyer family's <b>control arithmetic</b> — the state selection in
    /// <c>UnitBloat.control()</c> (<c>UnitBloat.as:201-312</c>), <c>UnitBat.control()</c>
    /// (<c>UnitBat.as:116-291</c>) and <c>UnitBloatEmitter.control()</c>
    /// (<c>UnitBloatEmitter.as:111-146</c>), transcribed as pure functions of their numbers.
    ///
    /// <para><b>Why extracted.</b> Same reason as <c>ArmedShooterStateMath</c> and <c>UnitJumpMath</c>:
    /// every threshold here is a bare literal in the oracle, every one of them is silent when wrong, and
    /// the port's previous flyer movement was invented inside a <c>MonoBehaviour</c> where no test could
    /// reach it. The three symptoms it produced — "flyers fall", "they do not fly around", "the nest
    /// spawns nothing" — were three different parts of this machine, none of them assertable.</para>
    ///
    /// <para><b>Two ladders, and they are not the port's <see cref="EnemyAIState"/>.</b> The bloat uses
    /// <c>aiState</c> 0/1/2 and the bat 0..6; neither maps onto Idle/Patrol/Alert/CombatChase, and the
    /// bat's 4/5/6 mean pursue/stall/dive — not the zombie's pre/buried/dig. The brain keeps the oracle's
    /// integer in <c>_familyState</c> and maps it onto <see cref="EnemyAIState"/> only for the F3 panel.</para>
    ///
    /// <para><b>Y is mirrored here, not at the call site.</b> AS3's <c>aiDy</c> is down-positive and this
    /// port's Y grows up. Every direction this class returns is in <b>AS3's own convention</b> (the values
    /// the oracle stores in <c>aiDx</c>/<c>aiDy</c>), so a reader can compare them with the source line
    /// for line; <see cref="FlyerBrain"/> negates the vertical term on the way into a velocity, which is
    /// the one place the two spaces meet.</para>
    /// </summary>
    public static class FlyerControlMath
    {
        // ── Bloat: aiState 0/1/2 (UnitBloat.as:220-252) ──────────────────────────────────────────

        /// <summary><c>aiState 0</c> — no target, drift on a fixed random unit vector.</summary>
        public const int BloatDrift = 0;

        /// <summary><c>aiState 1</c> — a target exists but the bloat is not committing; it spits.</summary>
        public const int BloatStalk = 1;

        /// <summary><c>aiState 2</c> — committed charge along the unit vector to the target.</summary>
        public const int BloatCharge = 2;

        /// <summary>
        /// <c>aiTCh = Math.floor(Math.random() * 50) + 20</c> (<c>UnitBloat.as:222</c>) — the floor.
        /// </summary>
        public const int BloatReRollMinTicks = 20;

        /// <summary>The roll's span, so the caller must draw an int in <c>[0, 50)</c>.</summary>
        public const int BloatReRollRollRange = 50;

        /// <summary><c>aiTCh = 20</c> on a commit (<c>:231</c>) — a charge lasts 20 ticks.</summary>
        public const int BloatChargeTicks = 20;

        /// <summary>
        /// <c>isrnd(0.1)</c> (<c>:243</c>) — the chance an untargeted bloat re-rolls its drift vector.
        /// </summary>
        public const float BloatDriftRedrawChance = 0.1f;

        /// <summary><c>this.aiDx = isrnd() ? 0.7 : -0.7</c> (<c>:245-246</c>).</summary>
        public const float BloatDriftMagnitude = 0.7f;

        /// <summary>
        /// <c>attRasst</c> — the engage range, <c>400</c> by default (<c>UnitBloat.as:27</c>), read from
        /// <c>&lt;un attr&gt;</c> when the row authors one.
        ///
        /// <para><b>Not imported, and that is a recorded data gap.</b> <c>UnitDataImporter</c> does not
        /// read <c>&lt;un attr|attch|shootch|gryz|bulb&gt;</c>, so every bloat asset carries none of
        /// them and every bloat uses this default. <c>bloat8/9/10</c> author <c>attch</c>/<c>shootch</c>
        /// (<c>AllData.as:1013</c>, <c>:1022</c>, <c>:1032</c>) and will read the template values until
        /// the importer learns the attribute. Named here so the number has a provenance instead of
        /// looking like a tuning choice.</para>
        /// </summary>
        public const float BloatAttackRangePixels = 400f;

        /// <summary><c>attCh</c> — the chance a bloat inside <see cref="BloatAttackRangePixels"/>
        /// commits to a charge, <c>0.4</c> by default (<c>UnitBloat.as:29</c>). Same data gap as the
        /// range.</summary>
        public const float BloatCommitChance = 0.4f;

        /// <summary><c>shootCh</c> — the per-tick fire chance while in state 1, <c>0.1</c> by default
        /// (<c>UnitBloat.as:19</c>).</summary>
        public const float BloatShootChance = 0.1f;

        /// <summary>
        /// <c>sitSpeed = maxSpeed * 0.5</c> (<c>UnitBloat.as:65</c>) — the drift's speed cap. State 0 is
        /// a lazy float, not a walk: at the walk cap a drifting bloat crosses a room.
        /// </summary>
        public const float BloatDriftSpeedFactor = 0.5f;

        // ── Bat: aiState 0..6 (UnitBat.as:130-291) ───────────────────────────────────────────────

        /// <summary><c>aiState 0</c> — parked, <c>stay</c> row, no motion.</summary>
        public const int BatIdle = 0;

        /// <summary><c>aiState 1</c> — wander: random <c>aiDx</c>, <c>aiDy = -0.7</c> (upward).</summary>
        public const int BatWander = 1;

        /// <summary><c>aiState 2</c> — fly to the last known position.</summary>
        public const int BatMoveToLastKnown = 2;

        /// <summary><c>aiState 3</c> — chase at walk speed, <c>isrnd(0.1)</c> contact.</summary>
        public const int BatChase = 3;

        /// <summary><c>aiState 4</c> — pursue at run speed, contact every tick.</summary>
        public const int BatPursue = 4;

        /// <summary><c>aiState 5</c> — stall: <c>maxSpeed = 0</c> for 20 ticks.</summary>
        public const int BatStall = 5;

        /// <summary><c>aiState 6</c> — the dive: <c>accel * 3</c>, <c>runSpeed * 2.5</c>, 2× damage.</summary>
        public const int BatDive = 6;

        /// <summary><c>aiTCh = Math.floor(Math.random() * 100) + 100</c> (<c>UnitBat.as:137</c>).</summary>
        public const int BatReRollMinTicks = 100;

        /// <summary>The roll's span, so the caller must draw an int in <c>[0, 100)</c>.</summary>
        public const int BatReRollRollRange = 100;

        /// <summary><c>aiTCh = 20</c> for the stall (<c>:142</c>, <c>:169</c>).</summary>
        public const int BatStallTicks = 20;

        /// <summary><c>aiTCh = 10</c> after the awareness-lost re-roll (<c>:160</c>).</summary>
        public const int BatWanderTicks = 10;

        /// <summary>
        /// <c>if(this.aiRasst &lt; 380)</c> (<c>:164</c>) — inside this range an aware bat either stalls
        /// or pursues; outside it, it just closes.
        /// </summary>
        public const float BatStallRangePixels = 380f;

        /// <summary><c>aiDy = -0.7</c> (<c>:150</c>) — the wander drifts <b>upward</b> in AS3's space.</summary>
        public const float BatWanderDriftY = -0.7f;

        /// <summary><c>aiDx = isrnd() ? 0.7 : -0.7</c> (<c>:149</c>).</summary>
        public const float BatWanderDriftX = 0.7f;

        /// <summary>
        /// <c>maxSpeed = runSpeed * 2.5</c> for the dive (<c>:269</c>) — 5× a walk speed of
        /// <c>moveSpeed * 2</c>… which is why <c>UnitController.SetFlySpeedCapPixelsPerFrame</c> has to
        /// exist: the definition's <c>moveSpeed</c> would clamp this to a walk.
        /// </summary>
        public const float BatDiveSpeedFactor = 2.5f;

        /// <summary><c>dx += this.aiDx * accel * 3</c> (<c>:270-271</c>) — the dive's thrust multiplier.</summary>
        public const float BatDiveThrustFactor = 3f;

        /// <summary><c>isrnd(0.1)</c> (<c>:275</c>) — the contact chance in state 3 only.</summary>
        public const float BatChaseContactChance = 0.1f;

        /// <summary><c>maxSpok</c> — the awareness ceiling, 30 (<c>Unit.as:362</c>).</summary>
        public const int BatMaxAwareness = 30;

        /// <summary>
        /// <c>aiSpok = maxSpok + 10</c> on a successful <c>findCel()</c> (<c>UnitBat.as:192</c>) — the
        /// extra 10 is the drain budget once sight breaks.
        /// </summary>
        public const int BatAwarenessOnSight = BatMaxAwareness + 10;

        // ── Emitter (UnitBloatEmitter.as:111-146) ────────────────────────────────────────────────

        /// <summary><c>aiTCh = 10</c> — the emitter's find-target cadence (<c>:123</c>).</summary>
        public const int EmitterProbeTicks = 10;

        /// <summary><c>this.emit_t = 100</c> — one child every 100 ticks (<c>:140</c>).</summary>
        public const int EmitterEmitTicks = 100;

        /// <summary><c>kolChild &lt; 5</c> — the nest's live-children cap (<c>:137</c>).</summary>
        public const int EmitterMaxChildren = 5;

        /// <summary><c>dropLoot()</c>'s <c>while(_loc1_ &lt; 5)</c> (<c>:104-108</c>).</summary>
        public const int EmitterDeathEmits = 5;

        /// <summary>
        /// <c>dropLoot()</c>'s <c>this.emit(); this.emit();</c> and the hero arm's two more
        /// (<c>UnitBloat.as:176-185</c>) — the bloat that carries <c>&lt;un bulb='1'&gt;</c>.
        /// </summary>
        public const int BloatDeathEmits = 2;

        /// <summary>The extra pair a <c>hero</c> bloat emits (<c>UnitBloat.as:180-184</c>).</summary>
        public const int BloatHeroDeathEmits = 4;

        /// <summary>
        /// The speed cap for a bloat state — AS3 <c>maxSpeed</c>, assigned in the same <c>if</c> that
        /// picks the state (<c>UnitBloat.as:232</c>, <c>:237</c>, <c>:250</c>).
        /// </summary>
        public static float BloatMaxSpeed(int state, float walkSpeed, float runSpeed)
        {
            switch (state)
            {
                case BloatCharge: return runSpeed;
                case BloatStalk: return walkSpeed;
                default: return walkSpeed * BloatDriftSpeedFactor;
            }
        }

        /// <summary>
        /// The speed cap for a bat state, or <c>null</c> for "leave it as it was".
        ///
        /// <para><b>State 0 assigns nothing, and that is the oracle.</b> <c>UnitBat.control()</c> writes
        /// <c>maxSpeed</c> only inside the state-1..4 / 5 / 6 arms (<c>:249-272</c>), so a parked bat
        /// keeps the cap the last state set. Returning <c>null</c> rather than a guessed value is what
        /// lets the caller hold the previous cap instead of inventing one.</para>
        /// </summary>
        public static float? BatMaxSpeed(int state, float walkSpeed, float runSpeed)
        {
            switch (state)
            {
                case BatWander:
                case BatMoveToLastKnown:
                case BatChase: return walkSpeed;
                case BatPursue: return runSpeed;
                case BatStall: return 0f;
                case BatDive: return runSpeed * BatDiveSpeedFactor;
                default: return null;
            }
        }

        /// <summary>
        /// Whether this bat state adds thrust at all — states 1..4 and 6 do, states 0 and 5 do not
        /// (<c>UnitBat.as:249-272</c>).
        /// </summary>
        public static bool BatThrusts(int state)
            => state >= BatWander && state <= BatPursue || state == BatDive;

        /// <summary>The thrust multiplier for a bat state — <c>3</c> in the dive, <c>1</c> otherwise.</summary>
        public static float BatThrustFactor(int state) => state == BatDive ? BatDiveThrustFactor : 1f;

        /// <summary>
        /// One expiry of the bloat's <c>aiTCh</c> with a target in sight — the
        /// <c>if(celUnit)</c> arm of <c>UnitBloat.as:223-240</c>.
        /// </summary>
        /// <param name="distance">AS3 <c>aiRasst</c> — the distance to the target, in pixels.</param>
        /// <param name="targetDeltaX">AS3 <c>celDX</c>.</param>
        /// <param name="targetDeltaY">AS3 <c>celDY</c> — <b>down-positive</b>.</param>
        /// <param name="committed">
        /// <c>isrnd(attCh)</c>, already drawn by the caller — <c>attCh</c> is data and the draw is not
        /// this class's to make.
        /// </param>
        /// <param name="timerRoll">An int in <c>[0, <see cref="BloatReRollRollRange"/>)</c>.</param>
        public static FlyerStep BloatDecideWithTarget(
            float distance,
            float targetDeltaX,
            float targetDeltaY,
            bool committed,
            int timerRoll)
        {
            int timer = BloatReRollMinTicks + timerRoll;
            int facing = targetDeltaX > 0f ? 1 : -1;

            // `if(aiRasst < attRasst && isrnd(attCh))` — the commit needs BOTH, and the range test is
            // strict (`<`, not `<=`).
            if (distance < BloatAttackRangePixels && committed && distance > 0f)
            {
                return new FlyerStep(
                    BloatCharge,
                    BloatChargeTicks,
                    targetDeltaX / distance,
                    targetDeltaY / distance,
                    facing);
            }

            // `else { aiState = 1; maxSpeed = walkSpeed; }` — the direction is NOT redrawn here: a
            // stalking bloat keeps the drift vector it had, which is what makes it sidle toward the
            // player instead of charging in a straight line.
            return new FlyerStep(BloatStalk, timer, 0f, 0f, facing, redrawDirection: false);
        }

        /// <summary>
        /// One expiry of the bloat's <c>aiTCh</c> with no target — <c>UnitBloat.as:241-251</c>.
        /// </summary>
        /// <param name="redrawDrift"><c>isrnd(0.1)</c>, drawn by the caller.</param>
        /// <param name="rollX">A float in <c>[0, 1)</c> for the x sign.</param>
        /// <param name="rollY">A float in <c>[0, 1)</c> for the y sign.</param>
        /// <param name="driftX">The current <c>aiDx</c>.</param>
        /// <param name="driftY">The current <c>aiDy</c>.</param>
        /// <param name="timerRoll">An int in <c>[0, <see cref="BloatReRollRollRange"/>)</c>.</param>
        public static FlyerStep BloatDecideIdle(
            bool redrawDrift,
            float rollX,
            float rollY,
            float driftX,
            float driftY,
            int timerRoll)
        {
            float dx = driftX;
            float dy = driftY;

            if (redrawDrift)
            {
                dx = rollX < 0.5f ? BloatDriftMagnitude : -BloatDriftMagnitude;
                dy = rollY < 0.5f ? BloatDriftMagnitude : -BloatDriftMagnitude;
            }

            // `storona = this.aiDx > 0 ? 1 : -1` — read from the (possibly new) drift vector.
            int facing = dx > 0f ? 1 : -1;

            return new FlyerStep(BloatDrift, BloatReRollMinTicks + timerRoll, dx, dy, facing);
        }

        /// <summary>
        /// One expiry of the bat's <c>aiTCh</c>, split into <b>one function per arm</b> of
        /// <c>UnitBat.as:130-187</c>.
        ///
        /// <para><b>Why per-arm and not one function over a struct of inputs.</b> The oracle's arms do
        /// not draw the same number of random values — the stall exit draws none, the awareness-lost arm
        /// draws one only when it was not already wandering, and every other arm draws exactly one. A
        /// single function taking pre-drawn rolls would have to draw all of them up front, which shifts
        /// <c>Math.random()</c> for the whole room and makes two runs of the same room diverge. The
        /// caller therefore picks the arm with the oracle's own <c>if</c>-chain and draws only what that
        /// arm consumes; the arithmetic of each arm stays here, where it can be asserted.</para>
        /// </summary>
        /// <remarks>
        /// The order of the arms is load-bearing: the state-6 and state-5 exits are tested <b>before</b>
        /// the awareness arms, so a bat mid-dive always finishes its recovery before it re-reads the
        /// situation. Testing awareness first makes a bat dive forever.
        /// </remarks>
        public static class BatArms
        {
            /// <summary><c>aiState == 6</c> → <c>4</c>: the dive recovers into a pursue.</summary>
            public static FlyerStep DiveRecover(int timerRoll)
                => new FlyerStep(BatPursue, BatReRollMinTicks + timerRoll, 0f, 0f, 0, false);

            /// <summary><c>aiState == 5</c> → <c>6</c>: the stall becomes the dive. No draw.</summary>
            public static FlyerStep StallBecomeDive()
                => new FlyerStep(BatDive, BatStallTicks, 0f, 0f, 0, false);

            /// <summary>
            /// <c>aiSpok == 0</c> — it has lost track of everything and drops to the wander.
            /// </summary>
            /// <param name="state">The current state; only <c>&gt; 1</c> re-draws the direction.</param>
            /// <param name="wanderRoll">Drawn <b>only</b> when <paramref name="state"/> is above 1.</param>
            /// <remarks>
            /// <b>This is the one arm that sets <c>RedrawDirection</c>.</b> <c>UnitBat.as:146-158</c> writes
            /// <c>aiDx</c>, <c>aiDy</c> <i>and</i> <c>storona</c> inside the <c>if(aiState &gt; 1)</c> block;
            /// every other arm of the ladder writes only <c>aiState</c>/<c>aiTCh</c> and lets the
            /// <c>findCel</c> steer block re-derive the vector. Returning <c>false</c> here would leave a bat
            /// that lost its target mid-chase flying on the old chase vector at wander speed — the drift
            /// would be a straight line away from where the player used to be.
            /// </remarks>
            public static FlyerStep AwarenessLost(int state, float wanderRoll)
            {
                if (state > BatWander)
                {
                    float dx = wanderRoll < 0.5f ? BatWanderDriftX : -BatWanderDriftX;
                    return new FlyerStep(
                        BatWander, BatWanderTicks, dx, BatWanderDriftY, dx > 0f ? 1 : -1,
                        redrawDirection: true);
                }

                // Already wandering: only the timer is re-armed.
                return new FlyerStep(state, BatWanderTicks, 0f, 0f, 0, false);
            }

            /// <summary>
            /// <c>aiSpok &gt;= maxSpok &amp;&amp; aiRasst &lt; 380</c> — the coin between holding station
            /// and pressing the attack.
            /// </summary>
            /// <param name="stallRoll">
            /// <c>isrnd()</c> — <c>Math.random() &lt; 0.5</c>. <c>isPlav</c> is always false here: no
            /// flyer declares <c>&lt;move plav&gt;</c>, so the oracle's <c>!isPlav</c> term is a constant.
            /// </param>
            public static FlyerStep AwareClose(bool stallRoll, int timerRoll)
            {
                return stallRoll
                    ? new FlyerStep(BatStall, BatStallTicks, 0f, 0f, 0, false)
                    : new FlyerStep(BatPursue, BatReRollMinTicks + timerRoll, 0f, 0f, 0, false);
            }

            /// <summary>
            /// <c>aiSpok &gt;= maxSpok &amp;&amp; aiRasst &gt;= 380</c> — too far to commit:
            /// <c>hp &lt; maxhp ? 4 : 3</c>.
            /// </summary>
            public static FlyerStep AwareFar(bool wounded, int timerRoll)
            {
                return new FlyerStep(
                    wounded ? BatPursue : BatChase,
                    BatReRollMinTicks + timerRoll,
                    0f,
                    0f,
                    0,
                    false);
            }

            /// <summary><c>0 &lt; aiSpok &lt; maxSpok</c> — aware but not committed: go and look.</summary>
            public static FlyerStep Uncommitted(int timerRoll)
                => new FlyerStep(BatMoveToLastKnown, BatReRollMinTicks + timerRoll, 0f, 0f, 0, false);
        }

        /// <summary>
        /// Whether a bat state re-derives <c>aiDx</c>/<c>aiDy</c> from <c>celX</c>/<c>celY</c> — the
        /// <c>if(aiState == 2 || 3 || 4 || 5)</c> block of <c>UnitBat.as:202-220</c>.
        /// </summary>
        public static bool BatSteersAtTarget(int state)
            => state == BatMoveToLastKnown || state == BatChase || state == BatPursue || state == BatStall;

        /// <summary>
        /// The emitter's emit gate — <c>UnitBloatEmitter.as:133-141</c>:
        /// <c>if(emit_t &gt; 0) --emit_t; else if(aiState == 2 &amp;&amp; kolChild &lt; 5) { emit(true); emit_t = 100; }</c>.
        /// </summary>
        public static bool EmitterShouldEmit(int emitTimer, int state, int childCount)
            => emitTimer <= 0 && state == 2 && childCount < EmitterMaxChildren;

        /// <summary>
        /// <c>Location.randomCid("bloat")</c> (<c>Location.as:1877-1899</c>) — which bloat variant a nest's
        /// child is. The caller has already applied the <c>locDifLevel &gt; 3</c> gate
        /// (<c>UnitBloatEmitter.as:69-72</c>); this is the roll behind it.
        /// </summary>
        /// <param name="biome">AS3 <c>loc.biom</c> — the room's biome index; <c>5</c> is the one that
        /// forces the 4..6 band.</param>
        /// <param name="difficulty">AS3 <c>loc.locDifLevel</c>.</param>
        /// <param name="roll">A float in <c>[0, 1)</c>.</param>
        /// <remarks>
        /// The bands are not a ladder of increasing ranges — the biome test wins over every difficulty,
        /// and each difficulty band's range is <b>exclusive of the next tier's floor</b>:
        /// <c>dif &gt;= 10</c> can still roll a <c>bloat0</c>, so a hard room's nest is mostly weak children
        /// with an occasional strong one.
        /// </remarks>
        public static int BloatVariantRoll(int biome, float difficulty, float roll)
        {
            // The band is (start, size). The biome test is FIRST and wins over every difficulty — that is
            // the oracle's own order, not a preference.
            int start;
            int size;

            if (biome == 5)
            {
                start = 4;
                size = 3;
            }
            else if (difficulty >= 10f)
            {
                start = 0;
                size = 5;
            }
            else if (difficulty >= 4f)
            {
                start = 0;
                size = 4;
            }
            else if (difficulty >= 2f)
            {
                start = 0;
                size = 3;
            }
            else
            {
                // `else { _loc2_ = 0; }` — a band of one, whatever the roll.
                return 0;
            }

            // `Math.random()` is in [0, 1), so `floor(roll * size)` is already inside the band for any
            // draw the game can make. The clamp is for a caller that hands this a literal 1: without it,
            // `floor(1 * 5)` is 5 and a `bloat5` would be emitted from a band that has no such member.
            int offset = Mathf.FloorToInt(roll * size);
            if (offset < 0) offset = 0;
            if (offset >= size) offset = size - 1;

            return start + offset;
        }

        /// <summary>
        /// AS3 <c>isrnd(p)</c> (<c>Math.random() &lt; p</c>), as a function so a caller cannot
        /// accidentally test <c>&lt;=</c>.
        /// </summary>
        public static bool IsRnd(float roll, float chance) => roll < chance;
    }

    /// <summary>One decision of a flyer's control ladder: the state, the timer it re-armed, and the
    /// direction it wants (in AS3's own <c>aiDx</c>/<c>aiDy</c> convention, Y down-positive).
    /// </summary>
    public readonly struct FlyerStep
    {
        public readonly int State;
        public readonly int Timer;
        public readonly float DirX;
        public readonly float DirY;

        /// <summary>
        /// <c>0</c> when the oracle does not touch <c>storona</c> on this arm — the bat's ladder never
        /// assigns it outside the wander (<c>UnitBat.as:151-158</c>), so a pursuing bat keeps the facing
        /// its steering derived.
        /// </summary>
        public readonly int Facing;

        /// <summary>
        /// <c>false</c> when the oracle leaves <c>aiDx</c>/<c>aiDy</c> untouched — a stalking bloat and
        /// a pursuing bat keep the vector the last <c>findCel</c> gave them.
        /// </summary>
        public readonly bool RedrawDirection;

        public FlyerStep(int state, int timer, float dirX, float dirY, int facing,
            bool redrawDirection = true)
        {
            State = state;
            Timer = timer;
            DirX = dirX;
            DirY = dirY;
            Facing = facing;
            RedrawDirection = redrawDirection;
        }
    }
}

using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The ArmedShooter family's <b>chase-state arithmetic</b> — AS3 <c>UnitRaider.control()</c>'s
    /// <c>aiState</c> re-roll (<c>:746-780</c>), the state-4 hold body (<c>:1418-1428</c>), the chase's
    /// ledge hop (<c>:1372-1375</c>) and the <c>turnX</c> wall reaction (<c>:1379-1414</c>), transcribed
    /// as pure functions of their numbers.
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="TurretSweepMath"/> and
    /// <c>UnitJumpMath</c>: every threshold here is a bare literal in the oracle and every way to get
    /// one wrong is silent. The port's chase read as "it does not jump, it does not get agitated, and it
    /// pushes at walls" — three symptoms of arithmetic that no test could reach because it lived inside
    /// a <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>The shape of the oracle's machine, because the port did not have it.</b> <c>aiState</c>
    /// is <i>not</i> re-decided every frame. The state bodies run every frame, but the <b>selection</b>
    /// happens only when <c>aiTCh</c> has drained — a 100..199-tick cycle — and that selection is what
    /// re-arms <c>aiTCh</c>. A port that picks a state from the current distance every tick therefore
    /// cannot reproduce the oracle's behaviour at all: it either pins the unit in the first state the
    /// distance test matches (the port's bug: a ranged raider parked in state 4 for the whole fight) or
    /// flickers between two states every tick.</para>
    /// </summary>
    public static class ArmedShooterStateMath
    {
        // ── The family's own state numbers (UnitRaider.as:624-1453) ─────────────────────────────
        //
        // Distinct from PFE.Entities.Enemies.EnemyAIState: the family state is a second ladder that
        // lives *inside* aiState 3. The port keeps it in `_familyState` for exactly that reason.

        /// <summary><c>aiState 3</c> — close on the target.</summary>
        public const int Chase = 3;

        /// <summary><c>aiState 0</c> — idle, at the end of a patrol leg (<c>UnitRaider.as:1074</c>).</summary>
        public const int Idle = 0;

        /// <summary><c>aiState 1</c> — patrolling (<c>UnitRaider.as:1078</c>).</summary>
        public const int Patrol = 1;

        /// <summary><c>aiState 2</c> — alert: alarmed, closing on a position it has not committed to.</summary>
        public const int Alert = 2;

        /// <summary><c>aiState 6</c> — the grenade throw.</summary>
        public const int Grenade = 6;

        /// <summary>
        /// <c>aiState 4</c> — the hold: stand still, face the target, and hop backwards when the target
        /// comes within <see cref="HoldExitDistancePixels"/>.
        /// </summary>
        public const int Hold = 4;

        /// <summary><c>aiState 7</c> — the dash wind-up. Dead in the shipped data (<c>dash</c> is never
        /// set), kept because the oracle's arm exists.</summary>
        public const int DashWindUp = 7;

        /// <summary><c>aiState 8</c> — the charge.</summary>
        public const int Charge = 8;

        // ── overLook: the per-state 360° flag (UnitRaider.as:1074, 1078, 1178) ─────────────────

        /// <summary>
        /// AS3 <c>Unit.overLook</c> as <c>UnitRaider.control()</c> sets it for a family state —
        /// <c>null</c> meaning "this state does not touch it, keep what the last one wrote".
        /// </summary>
        /// <remarks>
        /// <para><b>This is the flag behind "he forgot about me when I got behind him".</b>
        /// <c>overLook</c> is the second argument <c>findCel()</c> passes to <c>look()</c>
        /// (<c>Unit.as:4671</c>), and <c>param2</c> being <c>true</c> drops the facing cone, the
        /// behind-the-back rejection (<c>:4598</c>) <i>and</i> the below-the-eye range penalty
        /// (<c>:4589</c>) — i.e. an overlooking unit tracks a target through 360°. The raider writes
        /// it in its state bodies: <b>false</b> in 0 and 1, <b>true</b> in 2/3/6/8.</para>
        ///
        /// <para><b>States 4, 5 and 7 return <c>null</c> on purpose.</b> The oracle's shape is
        /// <c>if(aiState == 0){…} if(aiState == 1){…} else if(aiState == 2 || 3 || 6 || 8){…}</c> — a
        /// separate <c>if</c> for 0 and an <c>else if</c> chain for the rest — so states 4 (hold), 5
        /// (spotted, deciding) and 7 (dash wind-up) match no arm and the field simply keeps its value.
        /// Since every one of them is entered from 2/3 or from 0/1, "keep" resolves to the right answer
        /// without the port inventing a rule the oracle does not have.</para>
        ///
        /// <para><b>The caller must latch it, not recompute a default.</b> A brain that mapped
        /// <c>null</c> to <c>false</c> would drop the raider out of 360° for the four frames it spends
        /// in state 5 — which is exactly when the player is most likely to be circling behind it.</para>
        /// </remarks>
        /// <param name="familyState">The port's <c>_familyState</c>, i.e. the oracle's <c>aiState</c>.</param>
        /// <returns><c>true</c>/<c>false</c> when this state assigns <c>overLook</c>; otherwise <c>null</c>.</returns>
        public static bool? OverLookForFamilyState(int familyState)
        {
            switch (familyState)
            {
                case Idle:
                case Patrol:
                    return false;

                case Alert:
                case Chase:
                case Grenade:
                case Charge:
                    return true;

                default:
                    return null;
            }
        }

        // ── The re-roll cadence: `aiTCh = Math.floor(Math.random() * 100) + 100` (:772) ──────────

        /// <summary>The floor of the chase cycle — <c>+ 100</c> at <c>:772</c>, i.e. 3.3 s at 30 Hz.</summary>
        public const int ReRollMinTicks = 100;

        /// <summary>
        /// The span of the cycle's roll — <c>Math.floor(Math.random() * 100)</c>, so the caller must draw
        /// an <b>int</b> in <c>[0, ReRollRollRange)</c>. A float draw in <c>[0, 100]</c> would be inclusive
        /// of 100 and produce a 200-tick cycle the oracle cannot.
        /// </summary>
        public const int ReRollRollRange = 100;

        /// <summary>
        /// <c>aiTCh = Math.floor(Math.random() * 100) + 100</c> (<c>:772</c>) — the length of the next
        /// chase cycle, 100..199 ticks inclusive.
        /// </summary>
        public static int ReRollTicks(int roll)
        {
            return ReRollMinTicks + Mathf.Clamp(roll, 0, ReRollRollRange - 1);
        }

        // ── The four-arm timer assignment (:772-790) ───────────────────────────────────────────────

        /// <summary>
        /// The draw every arm of <see cref="NextStateTimerTicks"/> takes. One int in
        /// <c>[0, StateTimerRollRange)</c> serves all four arms — see that method for why one draw is
        /// faithful rather than a simplification.
        /// </summary>
        public const int StateTimerRollRange = 100;

        /// <summary>State 0/1 timer floor — <c>floor(rand*50) + 40</c>, so 40..89 ticks.</summary>
        public const int IdlePatrolMinTicks = 40;

        /// <summary>State 0/1 timer span — the <c>rand*50</c> half of the same line.</summary>
        public const int IdlePatrolRollRange = 50;

        /// <summary>Melee-hold timer floor — <c>floor(rand*10) + 10</c>, so 10..19 ticks.</summary>
        public const int MeleeHoldMinTicks = 10;

        /// <summary>Melee-hold timer span — the <c>rand*10</c> half of the same line.</summary>
        public const int MeleeHoldRollRange = 10;

        /// <summary>
        /// The dash wind-up's fixed length — <c>else if(aiState == 7) aiTCh = 20;</c> (<c>:786-789</c>).
        /// </summary>
        public const int DashWindUpTicks = 20;

        /// <summary>
        /// The charge's fixed length — <c>aiState = 8; aiTCh = 35;</c> (<c>:716-720</c>), and again
        /// <c>aiTCh = 5</c> after a landed contact hit in state 8 (<c>:1488-1492</c>).
        /// </summary>
        public const int ChargeTicks = 35;

        /// <summary>
        /// What a landed contact hit does to the charge timer — <c>if(aiState == 8) aiTCh = 5;</c>
        /// (<c>:1488-1492</c>). Five ticks, not zero: the oracle cuts the charge short rather than
        /// ending it, so the body carries through the hit and then re-decides.
        /// </summary>
        public const int ChargeHitTicks = 5;

        /// <summary>
        /// How long the state this unit just chose will last — <c>UnitRaider.as:772-790</c>, the
        /// <b>four</b>-arm chain at the end of the state-selection block:
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(aiState &lt;= 1)                           aiTCh = Math.floor(Math.random() * 50) + 40;
        /// else if(this.attackerType == 1 &amp;&amp; aiState == 4) aiTCh = Math.floor(Math.random() * 10) + 10;
        /// else if(aiState == 7)                      aiTCh = 20;
        /// else                                       aiTCh = Math.floor(Math.random() * 100) + 100;
        /// </code>
        ///
        /// <para><b>The port had the last arm only, for every state.</b> <see cref="ReRollTicks"/> was
        /// written for the general chase cycle and then used as the re-roll's only answer, so a unit
        /// entering state 7 was given <b>100–199 ticks</b> of wind-up where the oracle gives
        /// <b>20</b> — and state 7's body is <c>walk = 0</c>, i.e. standing still. That was invisible
        /// while <c>dash</c> was never set, because nothing could enter state 7; the moment the zebra's
        /// <c>dash = true</c> was ported it would have turned "does not move" into "freezes for up to
        /// 6.6 seconds, then charges", which is a worse bug and a harder one to read. The chain has to
        /// land with the flag.</para>
        ///
        /// <para><b>One draw for four arms is exact, not an approximation.</b> Each arm draws once from
        /// a different range (<c>*50</c>, <c>*10</c>, <c>*100</c>; state 7 draws none but the block is
        /// reached through the same single <c>Math.random()</c> call site in AS3's own reading — it
        /// short-circuits before the draw). Taking one int in <c>[0,100)</c> and reducing it
        /// <c>mod 50</c> or <c>mod 10</c> is uniform on each arm's range, because 100 is a multiple of
        /// both. And exactly one draw per re-roll is what keeps the shared random stream in step with
        /// the oracle — the same reason <c>TickIdle</c> consumes its dice even when the unit is pinned
        /// to its post.</para>
        /// </remarks>
        /// <param name="familyState">The state just chosen — the oracle's <c>aiState</c>.</param>
        /// <param name="attackerType">AS3's <c>attackerType</c>; only the melee-hold arm reads it.</param>
        /// <param name="roll">An int in <c>[0, <see cref="StateTimerRollRange"/>)</c>.</param>
        public static int NextStateTimerTicks(int familyState, int attackerType, int roll)
        {
            int draw = Mathf.Clamp(roll, 0, StateTimerRollRange - 1);

            if (familyState <= 1)
            {
                return IdlePatrolMinTicks + draw % IdlePatrolRollRange;
            }

            if (attackerType == 1 && familyState == Hold)
            {
                return MeleeHoldMinTicks + draw % MeleeHoldRollRange;
            }

            if (familyState == DashWindUp)
            {
                return DashWindUpTicks;
            }

            return ReRollTicks(draw % ReRollRollRange);
        }

        // ── The spotted-deciding delay: `aiTCh = floor(rand*20 + tupizna)` (:813-820) ─────────────
        /// <summary>
        /// The span of the spotted-deciding roll — <c>Math.floor(Math.random() * 20)</c>, so the caller
        /// must draw an <b>int</b> in <c>[0, SpottedRollRange)</c>.
        /// </summary>
        public const int SpottedRollRange = 20;

        /// <summary>
        /// How long a unit that has just committed a target stands in state 5 before acting on it —
        /// <c>UnitRaider.as:810-821</c>:
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(aiState &lt;= 1) {
        ///    aiState = 5;
        ///    if(this.attackerType &gt;= 2) aiTCh = Math.floor(Math.random() * 20 + this.tupizna);
        ///    else                        aiTCh = Math.floor(Math.random() * 20 + this.tupizna / 4);
        /// }
        /// </code>
        ///
        /// <para><b>The two arms are different lengths, and the port used the ranged one for both.</b>
        /// A melee ArmedShooter waits a <i>quarter</i> of `tupizna` — the oracle's way of saying a
        /// knife-armed raider reacts faster than a rifleman lining up a shot. Using the full value made
        /// every melee raider hesitate as long as a sniper.</para>
        ///
        /// <para><b>Integer division is exact here, not an approximation.</b> The oracle floors after
        /// adding, i.e. <c>floor(roll + tupizna/4)</c>, and for any integer <c>roll &gt;= 0</c> and real
        /// <c>a &gt;= 0</c>, <c>floor(roll + a) == roll + floor(a)</c>. So <c>tupizna / 4</c> in integer
        /// arithmetic is the same number for every <c>tupizna</c>, including the odd ones.</para>
        /// </remarks>
        /// <param name="tupizna">The family's class constant: 40, or 10 for zebra/encl/merc/ranger.</param>
        /// <param name="attackerType">AS3's <c>attackerType</c>; <c>&gt;= 2</c> is ranged or thrown.</param>
        /// <param name="roll">An int in <c>[0, <see cref="SpottedRollRange"/>)</c>.</param>
        public static int SpottedDelayTicks(int tupizna, int attackerType, int roll)
        {
            int span = attackerType >= 2 ? tupizna : tupizna / 4;
            return Mathf.Clamp(roll, 0, SpottedRollRange - 1) + span;
        }

        // ── The 3 ↔ 4 selection (:756-766) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>aiSpok &gt;= maxSpok + 8</c> (<c>:756</c>) — the extra eight decrements the ranged arm
        /// demands on top of the chase band. Two seconds of unbroken sight, at one decrement per ten
        /// ticks.
        /// </summary>
        public const int HoldAwarenessMargin = 8;

        /// <summary>
        /// <c>aiState = isrnd(0.3) ? 3 : 4</c> (<c>:758</c>) — the chance of choosing the chase over the
        /// hold, i.e. a ranged raider holds <b>70 %</b> of its cycles and closes on the other 30 %.
        /// </summary>
        public const float ChaseRollChance = 0.3f;

        /// <summary>
        /// The oracle's <c>aiSpok &gt;= maxSpok + 8 &amp;&amp; celDX² + celDY² &lt; aiDist²</c> arm
        /// (<c>:756-757</c>), which is the <b>only</b> way into state 4 for a ranged attacker.
        /// </summary>
        /// <param name="ranged"><c>attackerType == 2</c> — a melee or thrown attacker never holds.</param>
        /// <param name="aiSpok">The family's awareness counter, in oracle decrements.</param>
        /// <param name="maxSpok">30, or 50 for <c>encl</c>/<c>merc</c>.</param>
        /// <param name="distanceSquared">Target distance squared, in pixels.</param>
        /// <param name="aiDistSquared">The unit's <c>aiDist</c>, squared.</param>
        public static bool MayHold(bool ranged, int aiSpok, int maxSpok, float distanceSquared, float aiDistSquared)
        {
            return ranged
                && aiSpok >= maxSpok + HoldAwarenessMargin
                && distanceSquared < aiDistSquared;
        }

        /// <summary>
        /// The state a re-roll lands on: <see cref="Hold"/> when it is allowed and the 30 % roll fails,
        /// <see cref="Chase"/> otherwise.
        ///
        /// <para><b>The roll is consumed either way.</b> Callers must pass a fresh draw even when
        /// <paramref name="mayHold"/> is false, for the same reason the pinned wander roll is consumed in
        /// <c>TickIdle</c>: skipping a draw shifts the shared random stream and changes every other
        /// unit's rolls for the rest of the room.</para>
        /// </summary>
        public static int SelectChaseState(bool mayHold, float roll)
        {
            if (!mayHold) return Chase;
            return roll < ChaseRollChance ? Chase : Hold;
        }

        // ── The hold body (:1418-1428) ──────────────────────────────────────────────────────────

        /// <summary>
        /// <c>celDX² + celDY² &lt; 200²</c> (<c>:1421</c>) — the distance at which the hold ends and the
        /// backwards hop happens. 200 px is the family's <c>optDistAtt</c>, i.e. the range it considers
        /// "in your face".
        /// </summary>
        public const float HoldExitDistancePixels = 200f;

        /// <summary><c>if(isrnd(0.6))</c> (<c>:1423</c>) — six holds in ten end with a hop, four with a
        /// plain return to the chase.</summary>
        public const float BackHopChance = 0.6f;

        /// <summary>
        /// <c>dx = runSpeed * (celDX &gt; 0 ? -1 : 1) * 2</c> (<c>:1425</c>) — <b>twice</b> the run speed,
        /// away from the target. The doubling is what makes the hop read as a flinch rather than as a
        /// retreat.
        /// </summary>
        public const float BackHopSpeedMultiplier = 2f;

        /// <summary><c>this.jump(0.4)</c> (<c>:1426</c>) — a 40 % hop: enough to clear the ground, not
        /// enough to leave the floor.</summary>
        public const float BackHopMagnitude = 0.4f;

        /// <summary>
        /// The whole state-4 tail: is the target inside <see cref="HoldExitDistancePixels"/> for a ranged
        /// attacker?
        ///
        /// <para><b>Two separate answers, and conflating them is a bug.</b> This one decides whether the
        /// hold <i>ends</i> (the oracle's <c>aiState = 3</c>, which is inside the distance test but
        /// outside the roll); <see cref="ShouldBackHop"/> decides whether it ends with a hop. A port that
        /// gated both on the roll would leave a raider standing in front of a player who rolled badly.
        /// </para>
        /// </summary>
        public static bool ShouldEndHold(bool ranged, float distanceSquared)
        {
            return ranged && distanceSquared < HoldExitDistancePixels * HoldExitDistancePixels;
        }

        /// <summary><c>if(isrnd(0.6))</c> — the hop that ends six holds in ten.</summary>
        public static bool ShouldBackHop(float roll)
        {
            return roll < BackHopChance;
        }

        // ── The chase's ledge hop (:1372-1375) ──────────────────────────────────────────────────

        /// <summary>
        /// <c>if(stay &amp;&amp; isrnd(0.5) &amp;&amp; aiVNapr &lt;= 0 &amp;&amp; (shX1 &gt; 0.5 &amp;&amp;
        /// aiNapr &lt; 0 || shX2 &gt; 0.5 &amp;&amp; aiNapr &gt; 0)) _loc2_ = 0.5;</c> (<c>:1372-1375</c>) —
        /// the chase's only ledge reaction: at a lip, with the target not below, <b>half the time it hops
        /// instead of turning</b>. The other half it simply walks off the edge, which is why raiders
        /// drop off catwalks in the original.
        /// </summary>
        public const float ChaseLipHopChance = 0.5f;

        /// <summary><c>_loc2_ = 0.5</c> — a half hop. Same magnitude as the patrol's crate hop.</summary>
        public const float ChaseLipHopMagnitude = 0.5f;

        /// <summary>
        /// The chase lip hop's predicate, with the oracle's own short-circuit order: grounded, then the
        /// roll, then "the target is not below", then the edge.
        /// </summary>
        /// <param name="grounded">AS3 <c>stay</c>.</param>
        /// <param name="roll">A fresh <c>isrnd(0.5)</c> draw. Consumed even when the other terms are
        /// false, for the stream reason <see cref="SelectChaseState"/> documents.</param>
        /// <param name="targetBelow"><c>aiVNapr == 1</c> — see <see cref="IsTargetBelow"/>.</param>
        /// <param name="atEdgeTowardDirection">The <c>shX</c> test for the direction of travel.</param>
        public static bool ShouldHopAtLip(bool grounded, float roll, bool targetBelow, bool atEdgeTowardDirection)
        {
            return grounded
                && roll < ChaseLipHopChance
                && !targetBelow
                && atEdgeTowardDirection;
        }

        // ── The wall reaction (:1379-1414) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>isrnd(0.1)</c> (<c>:1392</c>) — the per-frame chance of giving up on the jump and taking
        /// the turn instead. It is an <i>escape hatch</i>, not the main path: the main path is
        /// <see cref="WallTurnPenalty"/> draining <c>aiTTurn</c>.
        /// </summary>
        public const float WallJumpAbandonChance = 0.1f;

        /// <summary>
        /// <c>celDY &gt; 100</c> (<c>:1392</c>) — no jump when the target is more than 100 px below:
        /// jumping would carry the unit <i>away</i> from it. Measured with the same
        /// <c>celDY</c> mirror <see cref="IsTargetBelow"/> uses, at a different threshold.
        /// </summary>
        public const float WallJumpTargetBelowPixels = 100f;

        /// <summary><c>aiTTurn -= 10</c> (<c>:1393</c>) — the price of a refused jump. Two refusals take
        /// the timer below zero and force the turn.</summary>
        public const int WallTurnPenalty = 10;

        /// <summary><c>aiTTurn = Math.floor(Math.random() * 20) + 5</c> (<c>:1397</c>) — 5..24 ticks of
        /// patience before the next attempt.</summary>
        public const int WallTurnMinTicks = 5;

        /// <summary>The span of that roll; callers draw an int in <c>[0, WallTurnRollRange)</c>.</summary>
        public const int WallTurnRollRange = 20;

        /// <summary><c>aiTTurn</c>'s declared value (<c>Unit.as:352</c>).</summary>
        public const int WallTurnInitialTicks = 10;

        /// <summary><c>aiTTurn = Math.floor(Math.random() * 20) + 5</c> — 5..24 ticks.</summary>
        public static int WallTurnTicks(int roll)
        {
            return WallTurnMinTicks + Mathf.Clamp(roll, 0, WallTurnRollRange - 1);
        }

        /// <summary>
        /// The oracle's <c>else</c> branch at <c>:1392</c> — jump, or penalise the turn timer?
        ///
        /// <code>
        /// if(isrnd(0.1) || turnY > 0 || celDY > 100 || !this.checkJump())
        ///    aiTTurn -= 10;
        /// else
        ///    _loc2_ = 1;
        /// </code>
        ///
        /// <para><b>Read the negation carefully: every term is a reason NOT to jump.</b> A coin flip
        /// (10 %), a ceiling hit (<c>turnY &gt; 0</c>), a target well below, or no headroom all refuse
        /// the jump. The refusal does not turn the unit on the spot — it only shortens the timer, so a
        /// blocked raider hops roughly once before giving up and walking the other way.</para>
        /// </summary>
        public static bool ShouldJumpAtWall(bool hitCeiling, bool targetWellBelow, bool hasHeadroom, float roll)
        {
            return !(roll < WallJumpAbandonChance || hitCeiling || targetWellBelow || !hasHeadroom);
        }

        /// <summary>
        /// <c>if(aiTTurn &lt; 0 &amp;&amp; stay) { aiNapr = this.tstor = turnX; … }</c> (<c>:1395-1400</c>) —
        /// has the patience run out?
        /// </summary>
        public static bool ShouldTurnAtWall(int aiTTurn, bool grounded)
        {
            return aiTTurn < 0 && grounded;
        }

        // ── The celDY mirror, shared with the jump gate ─────────────────────────────────────────

        /// <summary>
        /// <c>celDY &gt; 40</c> (<c>:928</c>) — the target is <b>below</b> this unit's top by more than
        /// 40 px, which is the oracle's <c>aiVNapr = 1</c>.
        ///
        /// <para><b>This is the mirror of <c>UnitJumpMath.ShouldJump</c>, on the same measurement.</b>
        /// <c>celDY = celY - Y + scY</c> (<c>:4734</c>) is <i>the target's centre minus this unit's
        /// top</i>, in AS3's Y-down space; the port's Y runs up, so the oracle's <c>&gt; 40</c> is
        /// <c>unitTop - targetCentre &gt; 40</c> here and its <c>&lt; -40</c> is the same expression
        /// negated. Two callers read the two directions — the jump gate reads "above", this reads
        /// "below" — and both must come from one measurement or they can disagree about the dead zone
        /// between them.</para>
        /// </summary>
        public static bool IsTargetBelow(float unitTopPixelY, float targetCentrePixelY, float pixels)
        {
            return unitTopPixelY - targetCentrePixelY > pixels;
        }

        // ── The two speeds, and the row value they are derived from ─────────────────────────────────

        /// <summary>
        /// <c>walkSpeed = 2.5</c> when the row authors <c>&lt;un walker&gt;</c>
        /// (<c>UnitRaider.as:153-156</c>) — a literal in the oracle, not a scale.
        /// </summary>
        public const float WalkerWalkSpeed = 2.5f;

        /// <summary>
        /// <c>walkSpeed = maxSpeed; if(this.walker) walkSpeed = 2.5;</c> (<c>UnitRaider.as:152-156</c>).
        /// </summary>
        /// <param name="maxSpeed">The <b>row's</b> speed, i.e. <c>moveSpeed</c> — not a walk speed that
        /// was already derived from it.</param>
        /// <param name="walker">The row's <c>&lt;un walker&gt;</c> flag.</param>
        public static float WalkSpeedFor(float maxSpeed, bool walker)
        {
            return walker ? WalkerWalkSpeed : maxSpeed;
        }

        /// <summary>
        /// <c>runSpeed = maxSpeed * 2</c> (<c>UnitRaider.as:160</c>), overridden to
        /// <c>maxSpeed * 3</c> by <c>UnitZebra.as:31</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The operand is <paramref name="maxSpeed"/>, the row value — and the whole defect was
        /// passing the walk speed instead.</b> The two differ for exactly the rows that also author
        /// <c>walker</c>, because that flag rewrites <c>walkSpeed</c> to the literal 2.5. So the zebra —
        /// the only ArmedShooter that is both a walker and a dasher — got <c>2.5 × 2 = 5</c> where the
        /// oracle gives <c>4 × 3 = 12</c>. A unit that runs at 5 px/frame cannot catch a player who
        /// walks at 4 and sprints faster, so the fight never happened: the boss stood where it was
        /// placed. The row value has to be carried separately from the derived walk speed for the
        /// multiplication to land on the right number.</para>
        ///
        /// <para><paramref name="runMultiplier"/> is the importer's transcription of the
        /// <c>× 2</c>; it is consulted only when the family does not override, and 2 is the fallback
        /// for a row that authored nothing.</para>
        /// </remarks>
        /// <param name="maxSpeed">The row's <c>moveSpeed</c>.</param>
        /// <param name="runMultiplier">The row's <c>runMultiplier</c>, or 0 when it authored none.</param>
        /// <param name="dashes">The family sets <c>dash</c> — <c>UnitZebra.as:25</c>, the only writer.</param>
        public static float RunSpeedFor(float maxSpeed, float runMultiplier, bool dashes)
        {
            float multiplier = dashes ? 3f : (runMultiplier > 0f ? runMultiplier : 2f);
            return maxSpeed * multiplier;
        }

        // ── The per-class constants the family's subclasses set in their constructors ───────────────

        /// <summary>
        /// The six ArmedShooter numbers that are a <b>class</b> fact rather than an imported row value.
        /// </summary>
        /// <remarks>
        /// <para>Every one of these is declared with a default on <c>UnitRaider</c> and overwritten by a
        /// subclass constructor that runs after <c>super(...)</c>. None of them appears in
        /// <c>AllData.as</c>, so an importer cannot see them and a brain that reads only the unit row
        /// gets the base class's answer for every variant. That is precisely what happened: all six
        /// were left at the raider default for the whole family, and the zebra — the one class that
        /// overrides four of them — behaved like a raider in a zebra costume.</para>
        ///
        /// <para><b>A struct rather than six loose functions</b> so a caller cannot mix one class's
        /// <c>tupizna</c> with another's <c>durak</c>, and so the fixture can assert the whole tuple
        /// against one transcription of each constructor.</para>
        /// </remarks>
        public readonly struct ClassConstants
        {
            public ClassConstants(int tupizna, bool durak, bool dash, bool flyer, float visionMultiplier, int maxSpok)
            {
                Tupizna = tupizna;
                Durak = durak;
                Dash = dash;
                Flyer = flyer;
                VisionMultiplier = visionMultiplier;
                MaxSpok = maxSpok;
            }

            /// <summary><c>UnitRaider.tupizna</c> — the wait before acting on a first sighting.</summary>
            public int Tupizna { get; }

            /// <summary><c>UnitRaider.durak</c> — run speed in states 2/3, at the cost of steering.</summary>
            public bool Durak { get; }

            /// <summary><c>UnitRaider.dash</c> — the states-7/8 wind-up and charge.</summary>
            public bool Dash { get; }

            /// <summary><c>UnitRaider.flyer</c> — hovers instead of walking.</summary>
            public bool Flyer { get; }

            /// <summary><c>Unit.visionMult</c> — the sight-range class scale.</summary>
            public float VisionMultiplier { get; }

            /// <summary><c>Unit.maxSpok</c> — the awareness ladder's commit threshold.</summary>
            public int MaxSpok { get; }
        }

        /// <summary>
        /// The constants for one family id, transcribed from the class constructors.
        /// </summary>
        /// <remarks>
        /// <para><b>The sources, one per override, and there is no seventh family.</b>
        /// <c>UnitRaider.as:53-57</c> declares the defaults (<c>tupizna 40</c>, <c>durak true</c>,
        /// <c>dash false</c>, <c>flyer false</c>); <c>Unit.as:362</c> declares <c>maxSpok 30</c> and
        /// <c>Unit.as:219</c> <c>visionMult 1</c>. Then:</para>
        /// <list type="bullet">
        /// <item><description><c>UnitZebra.as:22-25,31</c> — <c>tupizna 10</c>, <c>visionMult 1.3</c>,
        /// <c>durak false</c>, <c>dash true</c>.</description></item>
        /// <item><description><c>UnitEncl.as:18,21,23,25</c> — <c>flyer true</c>, <c>tupizna 10</c>,
        /// <c>durak false</c>, <c>maxSpok 50</c>.</description></item>
        /// <item><description><c>UnitMerc.as:22,25,27</c> — <c>flyer true</c>, <c>tupizna 10</c>,
        /// <c>maxSpok 50</c>. <b>Not</b> <c>durak</c>: the merc keeps the raider's <c>true</c>, and
        /// copying the encl's answer here would make a griffin steer like a zebra.</description></item>
        /// <item><description><c>UnitRanger.as:21-22</c> — <c>tupizna 10</c>, <c>durak false</c>.</description></item>
        /// <item><description><c>UnitSlaver</c> overrides none of them, so it takes the raider tuple
        /// whole. It is listed rather than left to the fall-through because the fall-through is what a
        /// reader will suspect of being wrong.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="familyId">AS3 <c>parentId</c> — <c>zebra5</c> → <c>zebra</c>. Case-sensitive,
        /// because the importer writes the row's own lowercase spelling.</param>
        public static ClassConstants ClassConstantsFor(string familyId)
        {
            switch (familyId)
            {
                case "zebra":
                    return new ClassConstants(10, durak: false, dash: true, flyer: false, 1.3f, 30);

                case "encl":
                    return new ClassConstants(10, durak: false, dash: false, flyer: true, 1f, 50);

                case "merc":
                    return new ClassConstants(10, durak: true, dash: false, flyer: true, 1f, 50);

                case "ranger":
                    return new ClassConstants(10, durak: false, dash: false, flyer: false, 1f, 30);

                case "slaver":
                default:
                    return new ClassConstants(40, durak: true, dash: false, flyer: false, 1f, 30);
            }
        }
    }
}

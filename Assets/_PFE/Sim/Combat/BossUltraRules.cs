using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The Ultra Sentinel's fight ladder as pure arithmetic — AS3 <c>UnitBossUltra</c>
    /// (<c>UnitBossUltra.as</c>): the constructor constants at <c>:12-102</c>, <c>control():255-446</c>,
    /// <c>castShit():448-455</c> and the phase-two block at <c>:438-445</c>.
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The state lives on a <c>MonoBehaviour</c>
    /// that <c>AddComponent</c> builds, and <c>AddComponent</c> reaches a Unity <c>ECall</c>, so a
    /// fixture cannot construct the brain on the offline host. Splitting the <i>rules</i> out means the
    /// ladder is pinned by an ordinary NUnit fixture and the brain is reduced to "read the unit, ask
    /// this, write the unit back" — the same split <see cref="AlicornShieldRules"/>,
    /// <see cref="WeaponHoldPointMath"/> and <c>UnitShieldOverlayRules</c> already use.</para>
    ///
    /// <para><b>Every coordinate here is AS3 room-local pixels</b> — x from the left wall, <b>y
    /// downward from the ceiling</b>. That is deliberate: the oracle's comparisons (<c>Y &lt; 17*40</c>,
    /// <c>celY &gt; 16*40</c>, <c>moveY = y*40+40</c>) only mean what they meant in the original if they
    /// are evaluated in the original's own space. The single Y flip belongs at the edge that talks to
    /// <c>UnitController</c>, which is where <c>BossUltraBrain</c> negates the vertical velocity.</para>
    /// </summary>
    public static class BossUltraRules
    {
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Constructor constants (UnitBossUltra.as:12-102)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>:89</c> — <c>aiTCh = 80</c>, the delay before the first state change.</summary>
        public const int InitialAiTCh = 80;

        /// <summary><c>:59</c> — <c>mp = 3</c>, the move point the unit starts standing on.</summary>
        public const int InitialMovePointIndex = 3;

        /// <summary><c>:67</c> — <c>t_turn = 15</c>, the ticks the boss waits before turning around.</summary>
        public const int TurnDelayTicks = 15;

        /// <summary><c>:34</c> — <c>shitMaxHp = 500</c>.</summary>
        public const float ShieldMaxHpBase = 500f;

        /// <summary><c>:90</c> — <c>shitArmor = 15</c>, the flat reduction while the pool is positive.</summary>
        public const float ShieldArmor = 15f;

        /// <summary><c>:69</c> — <c>t_shit = 300</c>.</summary>
        public const int InitialShieldTimer = 300;

        /// <summary><c>:453</c> — <c>castShit</c> writes <c>t_shit = 1000</c> after filling the pool.</summary>
        public const int ShieldRechargeTimer = 1000;

        /// <summary><c>:442</c> — the phase-two block writes <c>t_shit = 2000</c>.</summary>
        public const int PhaseShieldTimer = 2000;

        /// <summary><c>:441</c> — <c>shithp = shitMaxHp * 4</c> on entering phase two (500 → 2000).</summary>
        public const float PhaseShieldMultiplier = 4f;

        /// <summary><c>:285</c> — <c>vulner[D_EMP] = shithp &gt; 0 ? 0.2 : 1</c>.</summary>
        public const float ShieldedEmpVulnerability = 0.2f;

        /// <summary>The same line's other arm — the row's own <c>vulner emp='1'</c>.</summary>
        public const float UnshieldedEmpVulnerability = 1f;

        /// <summary><c>:102</c> — <c>timerDie = 150</c>: the death-throes window, in frames.</summary>
        public const int DeathTimerTicks = 150;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The arena's five move points (UnitBossUltra.as:42-57)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>:42-57</c> — <c>movePoints.length</c>. Five fixed points, not a patrol path.</summary>
        public const int MovePointCount = 5;

        /// <summary>
        /// The five authored points, in AS3 tile coordinates, exactly as the oracle writes them:
        /// <c>{10,7} {37,7} {24,13} {7,18} {40,18}</c> (<c>:42-57</c>).
        ///
        /// <para>They are the boss room's corners and centre — <c>rooms_core/bossultra</c> is 48×25 with
        /// its floor at rows 21-23 — so the fight is a floating sentinel repositioning around the
        /// arena, which is the whole reason <c>control()</c> has a move state at all.</para>
        /// </summary>
        private static readonly int[,] MovePoints =
        {
            { 10, 7 },
            { 37, 7 },
            { 24, 13 },
            { 7, 18 },
            { 40, 18 },
        };

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  State timers (UnitBossUltra.as:304-365)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>:318</c> — <c>aiTCh = 60</c> while repositioning.</summary>
        public const int MoveStateTicks = 60;

        /// <summary><c>:323</c> — <c>aiTCh = 30</c> while choosing an attack.</summary>
        public const int AimStateTicks = 30;

        /// <summary><c>:335</c> — the low-ceiling arm cuts the aim state to <c>5</c>.</summary>
        public const int AimStateTicksLowCeiling = 5;

        /// <summary><c>:339</c> — the gas attack's aim state is <c>15</c> ticks.</summary>
        public const int AimStateTicksGas = 15;

        /// <summary><c>:351</c> — <c>attState == 0</c> (rockets) attacks for 80 ticks.</summary>
        public const int AttackTicksRocket = 80;

        /// <summary>
        /// <c>:355</c> — <c>attState == 2</c> (the grenade barrage) lasts 120 ticks.
        /// </summary>
        /// <remarks>
        /// <b>State 2 is the grenade state, not a telegraph.</b> The telegraph is the <c>laser</c>/
        /// <c>plasma</c>/<c>spark</c> particle emit in <c>control()</c> (<c>:405-418</c>), which fires
        /// for states 0, 1 and 4 and never for 2. State 2 means <c>thWeapon</c> — <c>roboplagr</c>,
        /// built with <c>findCel = false</c> — throwing shells along a <c>forceRot</c> that sweeps 0.1 rad
        /// per tick, so 120 ticks is about 1.9 revolutions of a rotating barrage.
        /// </remarks>
        public const int AttackTicksGrenade = 120;

        /// <summary><c>:359</c> — <c>attState == 4</c> (gas) attacks for 60 ticks.</summary>
        public const int AttackTicksGas = 60;

        /// <summary><c>:363</c> — every other attack lasts <c>random*100 + 150</c> ticks.</summary>
        public const int AttackTicksOtherBase = 150;

        /// <summary>The spread of the same expression.</summary>
        public const int AttackTicksOtherSpread = 100;

        /// <summary><c>:367</c> — the aim point is refreshed when <c>aiTCh % 10 == 1</c>.</summary>
        public const int AimRefreshModulus = 10;

        /// <summary><c>:405</c> — the telegraph particles fire when <c>aiTCh % 5 == 1</c>.</summary>
        public const int TelegraphModulus = 5;

        /// <summary><c>:324</c>/<c>:332</c> — both <c>isrnd(0.33)</c> rolls in the aim state.</summary>
        public const float AttackStateRollChance = 0.33f;

        /// <summary><c>:493</c> — <c>isrnd(0.1)</c>, the extra body hit riding along an attack.</summary>
        public const float ContactAttackChance = 0.1f;

        /// <summary>
        /// <c>:332</c> — <c>Y &lt; 17*40</c>, i.e. the boss is in the upper 680 px of the room
        /// (AS3 y runs down, so "less than" is "above").
        /// </summary>
        public const float LowCeilingBossY = 17f * TileQueryConstants.TileSize;

        /// <summary><c>:332</c> — <c>celY &gt; 16*40</c>, i.e. the target is below that line.</summary>
        public const float LowCeilingTargetY = 16f * TileQueryConstants.TileSize;

        /// <summary><c>:127</c> — <c>shitMaxHp *= 1 + level * 0.12</c> in <c>setLevel</c>.</summary>
        public const float LevelShieldScale = 0.12f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Move points
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Move point <paramref name="index"/> in AS3 room-local pixels — <c>moveX = x*40 + 20;
        /// moveY = y*40 + 40</c> (<c>:316-317</c>).
        /// </summary>
        /// <remarks>
        /// The <c>+20</c> is half a tile: the points are authored as tiles and the unit's <c>X</c> is a
        /// body centre. The <c>+40</c> on Y is a whole tile, because AS3's <c>Y</c> is the <i>feet</i>
        /// and the point names the tile the feet stand in.
        /// </remarks>
        public static Vector2 MovePointPixels(int index)
        {
            if (index < 0 || index >= MovePointCount)
            {
                // AS3 would throw on the array read; a clamped answer keeps a corrupt save from
                // dropping the boss out of its own arena.
                index = Mathf.Clamp(index, 0, MovePointCount - 1);
            }

            return new Vector2(
                MovePoints[index, 0] * TileQueryConstants.TileSize + TileQueryConstants.TileSize * 0.5f,
                MovePoints[index, 1] * TileQueryConstants.TileSize + TileQueryConstants.TileSize);
        }

        /// <summary>
        /// The next move point — <c>:306-315</c>:
        /// <code>
        /// r = Math.floor(Math.random() * 5);
        /// if(r == this.mp) r++;
        /// if(r &gt;= 5) r = 0;
        /// this.mp = r;
        /// </code>
        /// </summary>
        /// <param name="previous">The point the boss is standing on (<c>mp</c>).</param>
        /// <param name="roll">One <c>Math.random()</c> value in <c>[0,1)</c>.</param>
        /// <remarks>
        /// <b>It is a re-roll, not a walk.</b> The point is chosen uniformly from the five and only
        /// nudged when it would repeat, so the boss can jump from the left corner to the right one —
        /// which is what "repositioning" means for a flying sentinel and is why the move state has a
        /// 60-tick budget for a trip that can cross the whole room.
        /// </remarks>
        public static int NextMovePointIndex(int previous, float roll)
        {
            int next = Mathf.FloorToInt(roll * MovePointCount);
            if (next < 0) next = 0;
            if (next >= MovePointCount) next = MovePointCount - 1;

            if (next == previous) next++;
            if (next >= MovePointCount) next = 0;

            return next;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The state ladder
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>:292-303</c> — <c>++aiState</c>, wrapping to <b>2</b> after a gas attack and to <b>1</b>
        /// otherwise.
        /// </summary>
        /// <remarks>
        /// The wrap is the boss's whole rhythm: 1 (fly to a point) → 2 (pick an attack) → 3 (attack) →
        /// 1. The one asymmetry is <c>attState == 4</c>: the gas volley goes straight back to state 2, so
        /// a gas attack is followed by another aim rather than by a reposition. That is the oracle's
        /// text, not a simplification — <c>:295-302</c>.
        /// </remarks>
        public static int AdvanceState(int aiState, int attackState)
        {
            int next = aiState + 1;
            if (next > 3) next = attackState == 4 ? 2 : 1;
            return next;
        }

        /// <summary>
        /// The aim-state length for a freshly rolled <paramref name="attackState"/> — <c>:323-340</c>.
        /// </summary>
        /// <remarks>
        /// Four writes to <c>aiTCh</c> in one branch, in this order: <c>30</c>, then <c>5</c> if the
        /// low-ceiling arm fired, then <c>15</c> if the attack is the gas volley. The low-ceiling arm
        /// sets 5 <i>and</i> forces <c>attState = 2</c>, so the 15 can never overwrite it in practice —
        /// but the order is kept because that is how the oracle is written.
        /// </remarks>
        public static int AimTicksFor(int attackState, bool lowCeilingArm)
        {
            int ticks = AimStateTicks;
            if (lowCeilingArm) ticks = AimStateTicksLowCeiling;
            if (attackState == 4) ticks = AimStateTicksGas;
            return ticks;
        }

        /// <summary>The attack-state length — <c>:346-365</c>.</summary>
        /// <param name="attackState">0 rockets, 1 gatling, 2 grenades, 4 gas.</param>
        /// <param name="roll">One <c>Math.random()</c> value; drawn <b>only</b> on the catch-all arm.</param>
        public static int AttackTicksFor(int attackState, float roll)
        {
            switch (attackState)
            {
                case 0: return AttackTicksRocket;
                case 2: return AttackTicksGrenade;
                case 4: return AttackTicksGas;
                default: return Mathf.FloorToInt(roll * AttackTicksOtherSpread) + AttackTicksOtherBase;
            }
        }

        /// <summary>
        /// Whether the low-ceiling arm fires — <c>:332</c>: <c>Y &lt; 680 &amp;&amp; celY &gt; 640 &amp;&amp;
        /// isrnd(0.33)</c>.
        /// </summary>
        /// <param name="bossAs3Y">The boss's own feet, AS3 y (down-positive).</param>
        /// <param name="targetAs3Y">The aim point's y, AS3 (down-positive).</param>
        /// <param name="roll">One <c>Math.random()</c> value; the caller must not draw it unless the
        /// first two terms hold, because the oracle's <c>&amp;&amp;</c> short-circuits.</param>
        public static bool RollsLowCeiling(float bossAs3Y, float targetAs3Y, float roll)
        {
            return bossAs3Y < LowCeilingBossY && targetAs3Y > LowCeilingTargetY && roll < AttackStateRollChance;
        }

        /// <summary>
        /// The aim state's attack roll — <c>:324-336</c>.
        /// <code>
        /// if(attState != 4 &amp;&amp; isrnd(0.33)) attState = 4;
        /// else attState = Math.floor(Math.random() * 2);
        /// if(Y &lt; 680 &amp;&amp; celY &gt; 640 &amp;&amp; isrnd(0.33)) { attState = 2; aiTCh = 5; }
        /// </code>
        /// </summary>
        /// <param name="previous">The last <c>attState</c>.</param>
        /// <param name="gasRoll">The <c>isrnd(0.33)</c> for the gas arm.</param>
        /// <param name="pairRoll">The <c>Math.floor(random*2)</c> draw for the 0/1 arm.</param>
        /// <param name="lowCeilingRoll">The third draw, for the low-ceiling arm.</param>
        /// <param name="lowCeiling">Whether the first two terms of the low-ceiling test hold.</param>
        /// <remarks>
        /// <b>The 0/1 arm is never 2 and never 4.</b> Only the gas roll produces 4 and only the
        /// low-ceiling arm produces 2, so a fight that never puts the boss above the player never sees
        /// the grenade barrage at all — the two non-gatling/non-rocket states are each reachable by
        /// exactly one route, and neither is reachable by the coin flip.
        /// </remarks>
        public static int RollAttackState(
            int previous, float gasRoll, float pairRoll, float lowCeilingRoll, bool lowCeiling)
        {
            int state;
            if (previous != 4 && gasRoll < AttackStateRollChance)
            {
                state = 4;
            }
            else
            {
                state = pairRoll < 0.5f ? 0 : 1;
            }

            if (lowCeiling && lowCeilingRoll < AttackStateRollChance)
            {
                state = 2;
            }

            return state;
        }

        /// <summary>
        /// Whether the aim point is refreshed this tick — <c>:367</c>:
        /// <c>(aiState == 1 || aiState &gt; 1 &amp;&amp; attState == 1) &amp;&amp; aiTCh % 10 == 1</c>.
        /// </summary>
        /// <remarks>
        /// <b>Not every tick, and the exceptions are the point.</b> While repositioning the boss tracks
        /// the player once every ten ticks; while firing the gatling it tracks every ten; while firing
        /// rockets, gas or grenades it never re-aims at all, so those volleys commit to the point they
        /// were aimed at. That is what makes the rockets dodgeable.
        /// </remarks>
        public static bool RefreshesAimThisTick(int aiState, int attackState, int aiTCh)
        {
            bool stateWantsAim = aiState == 1 || (aiState > 1 && attackState == 1);
            return stateWantsAim && aiTCh % AimRefreshModulus == 1;
        }

        /// <summary>
        /// The facing delay — <c>:420-436</c>. Returns the new <c>storona</c> and the new <c>t_turn</c>.
        /// </summary>
        /// <param name="facing">Current <c>storona</c>.</param>
        /// <param name="wanted">Current <c>aiNapr</c> (the side the target is on).</param>
        /// <param name="turnTimer">Current <c>t_turn</c>.</param>
        /// <param name="skipsTurn">True when <c>aiState == 3 &amp;&amp; attState == 2</c> — the telegraph
        /// state does not turn.</param>
        /// <remarks>
        /// <b>A 15-tick commitment.</b> The boss does not snap to face the player: it counts down and
        /// flips when the count reaches zero, and the counter resets whenever it is already facing the
        /// right way. So a player who strafes across it every few frames never gets it to turn.
        /// </remarks>
        public static void TickFacing(
            int facing, int wanted, int turnTimer, bool skipsTurn, out int newFacing, out int newTurnTimer)
        {
            newFacing = facing;
            newTurnTimer = turnTimer;

            if (skipsTurn)
            {
                return;
            }

            if (facing != wanted)
            {
                newTurnTimer = turnTimer - 1;
                if (newTurnTimer <= 0)
                {
                    newFacing = wanted;
                    newTurnTimer = TurnDelayTicks;
                }
            }
            else
            {
                newTurnTimer = TurnDelayTicks;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Flight
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The seek vector's length limit — <c>:391</c>: <c>norma(spd, Math.min(accel, accel*d²/10000))</c>.
        /// </summary>
        /// <remarks>
        /// The <c>d²/10000</c> term is a braking curve: within 100 px of the point the thrust shrinks
        /// with the square of the distance, so the sentinel arrives instead of overshooting. Outside
        /// 100 px it is full acceleration.
        /// </remarks>
        public static float SeekLimit(float distanceSquared, float acceleration)
        {
            if (distanceSquared < 0f) distanceSquared = 0f;
            float scaled = acceleration * distanceSquared / 10000f;
            return Mathf.Min(acceleration, scaled);
        }

        /// <summary>
        /// <c>norma(v, len)</c> (<c>Obj.as:248-257</c>) — shorten <paramref name="vector"/> to
        /// <paramref name="limit"/> if it is longer, otherwise leave it alone.
        /// </summary>
        public static Vector2 ClampLength(Vector2 vector, float limit)
        {
            float lengthSquared = vector.x * vector.x + vector.y * vector.y;
            if (lengthSquared <= limit * limit || lengthSquared <= 0f)
            {
                return vector;
            }

            float length = Mathf.Sqrt(lengthSquared);
            return new Vector2(vector.x * limit / length, vector.y * limit / length);
        }

        /// <summary>
        /// The per-state velocity damping — <c>:394-403</c>.
        /// </summary>
        /// <remarks>
        /// State 1 damps hard (0.8) once inside the arrival radius, which is the other half of the
        /// braking curve; states 2 and 3 damp at 0.7 every tick, so an attacking boss bleeds off its
        /// speed and hangs in the air; state 0 damps not at all — it is already standing on a point.
        /// </remarks>
        public static float DampFactor(float distanceToPointSquared, int aiState)
        {
            if (aiState == 1) return distanceToPointSquared < 1000f ? 0.8f : 1f;
            if (aiState >= 2) return 0.7f;
            return 1f;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Shield (castShit + the phase-two block)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>castShit()</c> (<c>:448-455</c>), called once per reposition (<c>:319</c>):
        /// <code>
        /// if(shithp &lt;= 0 &amp;&amp; t_shit &lt;= 0
        ///    &amp;&amp; (globalDif == 4 || globalDif == 3 &amp;&amp; hp &lt; maxhp / 2))
        ///    { shithp = shitMaxHp; t_shit = 1000; }
        /// </code>
        /// </summary>
        /// <param name="shieldHp">AS3 <c>shithp</c>.</param>
        /// <param name="shieldTimer">AS3 <c>t_shit</c>.</param>
        /// <param name="globalDifficulty">AS3 <c>game.globalDif</c>, 0..4.</param>
        /// <param name="hpFraction">AS3 <c>hp / maxhp</c>.</param>
        /// <remarks>
        /// <b>The shield only exists on Hard and Very Hard.</b> On difficulties 0-2 this returns false
        /// for the whole fight, so the pool the phase-two block grants is the only shield the boss ever
        /// has there. That is the oracle's gate, not an omission — and it is why the boss is a very
        /// different fight at difficulty 4 (a 2000-point pool plus a 500-point recharge every
        /// reposition).
        /// </remarks>
        public static bool ShouldCastShield(
            float shieldHp, int shieldTimer, int globalDifficulty, float hpFraction)
        {
            if (shieldHp > 0f || shieldTimer > 0) return false;

            if (globalDifficulty == 4) return true;
            return globalDifficulty == 3 && hpFraction < 0.5f;
        }

        /// <summary>
        /// <c>:438-445</c> — the one HP threshold in the fight:
        /// <c>if(!usil &amp;&amp; hp &lt; maxhp/2)</c>.
        /// </summary>
        public static bool ShouldEnterPhaseTwo(bool alreadyEnhanced, float hpFraction)
        {
            return !alreadyEnhanced && hpFraction < 0.5f;
        }

        /// <summary>The phase-two pool — <c>shitMaxHp * 4</c> (<c>:441</c>).</summary>
        public static float PhaseTwoShieldHp(float shieldMaxHp) => shieldMaxHp * PhaseShieldMultiplier;

        /// <summary>
        /// <c>vulner[D_EMP] = shithp &gt; 0 ? 0.2 : 1</c> (<c>:285</c>) — recomputed every tick.
        /// </summary>
        public static float EmpVulnerability(float shieldHp)
            => shieldHp > 0f ? ShieldedEmpVulnerability : UnshieldedEmpVulnerability;

        /// <summary>
        /// <c>setLevel()</c>'s difficulty term (<c>:116-128</c>): <c>1.2</c> on Hard, <c>1.5</c> on Very
        /// Hard, <c>1</c> otherwise.
        /// </summary>
        /// <remarks>
        /// <b>This is the boss's own buff, on top of the constructor's HP scaling.</b> AS3 composes the
        /// two — <c>Unit.as:1130-1141</c> already multiplied max HP by 0.4/0.7/1 for the low
        /// difficulties, and <c>setLevel</c> then multiplies the result by this — which is why the port
        /// applies it in <c>BossUltraController.Initialize</c>, after
        /// <c>DifficultyRules.EnemyHpMultiplier</c> has already run in the spawner.
        /// </remarks>
        public static float DifficultyHpMultiplier(int globalDifficulty)
        {
            if (globalDifficulty == 3) return 1.2f;
            if (globalDifficulty == 4) return 1.5f;
            return 1f;
        }

        /// <summary>
        /// <c>shitMaxHp</c> after <c>setLevel</c> — <c>500 * (1 + level*0.12) * difficultyMultiplier</c>
        /// (<c>:127</c>).
        /// </summary>
        /// <param name="level">AS3 <c>setLevel</c>'s argument — the room's <c>enemyLevel</c>
        /// (<c>Location.as:1203/1292</c>), which the port carries as
        /// <c>EnemyController.LocationDifficulty</c>.</param>
        /// <param name="difficultyMultiplier">See <see cref="DifficultyHpMultiplier"/>.</param>
        /// <remarks>
        /// <b>Note what does <i>not</i> scale.</b> <c>UnitBossUltra.setLevel</c> overrides the base
        /// method and never calls it, so the boss's HP has <i>no</i> level term at all — only the shield
        /// and the weapon damage do (<c>:116-128</c>). A level-10 room gives the sentinel the same body
        /// it always had and a shield 2.2× thicker.
        /// </remarks>
        public static float ShieldMaxHp(int level, float difficultyMultiplier)
        {
            if (level < 0) level = 0;
            return ShieldMaxHpBase * (1f + level * LevelShieldScale) * difficultyMultiplier;
        }
    }
}

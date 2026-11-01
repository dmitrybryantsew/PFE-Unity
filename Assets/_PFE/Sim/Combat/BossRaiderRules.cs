using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The raider leader's fight as pure arithmetic — AS3 <c>UnitBossRaider</c>
    /// (<c>UnitBossRaider.as</c>): the rotation at <c>control():320-339</c>, the animation branch order
    /// at <c>animate():133-191</c>, the two contact scales at <c>:513</c>/<c>:558</c>, and the
    /// quarter-HP reinforcement gate at <c>damage():229-243</c>.
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The state lives on a <c>MonoBehaviour</c>
    /// that <c>AddComponent</c> builds, and <c>AddComponent</c> reaches a Unity <c>ECall</c>, so a
    /// fixture cannot construct the brain on the offline host. Splitting the <i>rules</i> out means the
    /// ladder is pinned by an ordinary NUnit fixture and the brain is reduced to "read the unit, ask
    /// this, write the unit back" — the same split <see cref="BossUltraRules"/>,
    /// <see cref="BossAlicornRules"/> and <see cref="AlicornShieldRules"/> already use.</para>
    ///
    /// <para><b>Why these five things and not the whole <c>control()</c>.</b> Every entry here is a
    /// place where the port previously held an <i>invented</i> value that passed a structural review:
    /// a random rotation window, a hard-coded 600 px stand-off, a <c>"stay"</c> animation for every
    /// state, a flat contact scale, and a reinforcement trigger with no gate. The rest of
    /// <c>control()</c> is physics the port's motor already owns (accel curves, wall turns, gravity),
    /// and moving that here would be a second copy of the motor rather than a rule.</para>
    /// </summary>
    public static class BossRaiderRules
    {
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The rotation (UnitBossRaider.as:320-339)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>:70</c> — <c>aiTCh = 80</c> in the constructor, the delay before the first change.</summary>
        public const int InitialAiTCh = 80;

        /// <summary>
        /// <c>:331-334</c> — <c>if(aiState == 2 || aiState == 4 || aiState == 5) aiTCh = 30;</c>.
        /// <b>Three states, not four</b>: 3 is the firing state and gets a long window, which is what
        /// makes the burst (12 ticks) fit inside it with room to spare.
        /// </summary>
        public const int ShortAiTCh = 30;

        /// <summary><c>:337</c> — <c>Math.floor(Math.random() * 100) + 150</c> for every other state.</summary>
        public const int LongAiTChBase = 150;

        /// <summary>
        /// The <c>100</c> in the same line. <c>Math.random()</c> is <c>[0,1)</c>, so
        /// <c>floor(random*100)</c> is <b>0..99</b> and the window is <b>150..249</b> — the port's old
        /// <c>Random.Range(150, 250)</c> was the same range with a different draw, but the port's
        /// <c>Random.Range(0, 100)</c> has to stay exclusive of 100 to keep the upper bound at 249.
        /// </summary>
        public const int LongAiTChRollRange = 100;

        /// <summary><c>:326-330</c> — <c>++aiState; if(aiState &gt; 5) aiState = 1;</c>.</summary>
        public const int FirstState = 1;

        /// <summary>The same wrap's other end. <b>State 0 is never re-entered</b> — it is the spawn state.</summary>
        public const int LastState = 5;

        /// <summary>
        /// <c>++aiState; if(aiState &gt; 5) aiState = 1;</c> (<c>:326-330</c>).
        ///
        /// <para><b>From 0 the first step is 1</b>, because the oracle increments before it wraps and
        /// 0 is only ever the value the constructor left behind. A port that mapped 5 → 0 would put the
        /// boss back in the idle state every cycle, and the idle state is the only one that stops it.</para>
        /// </summary>
        public static int NextState(int state) => state >= LastState ? FirstState : state + 1;

        /// <summary>The per-state timer (<c>:331-338</c>) — <paramref name="roll"/> is <c>0..99</c>.</summary>
        public static bool UsesShortTimer(int state) => state == 2 || state == 4 || state == 5;

        /// <summary>
        /// <c>aiTCh</c> for the state just entered. <paramref name="roll"/> is the caller's draw from
        /// <c>[0, 100)</c>; this method never rolls, so the caller keeps the RNG stream in the order the
        /// oracle draws it.
        /// </summary>
        public static int TimerFor(int state, int roll) =>
            UsesShortTimer(state) ? ShortAiTCh : LongAiTChBase + roll;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The animation branch order (UnitBossRaider.as:133-191)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>animate()</c>'s ladder, in the oracle's order. <b>The order is the whole content</b>:
        /// <c>die</c> beats <c>bac</c>, <c>bac</c> beats grounded, and the <c>stay</c>/<c>run</c>/
        /// <c>trot</c> split inside the grounded arm is a three-way test on velocity and state, not a
        /// lookup by state.
        ///
        /// <para><b>State 6 is unreachable and is kept anyway.</b> <c>animate():144</c> tests
        /// <c>aiState == 4 || aiState == 6</c>, but the rotation only ever produces 0-5 — so the
        /// <c>6</c> is dead in the oracle too. It is transcribed rather than dropped because a future
        /// edit to the ladder would silently lose the branch otherwise.</para>
        ///
        /// <para><b>Why <c>dx</c> and not "is moving".</b> The oracle's test is
        /// <c>dx &lt; 1 &amp;&amp; dx &gt; -1</c> — a <b>strict</b> two-sided band on the AS3 velocity,
        /// in px/frame, so a boss drifting at 1.0 px/frame draws <c>trot</c>, not <c>stay</c>. The
        /// <c>stay</c> label is also this unit's resting clip (<c>length: 20</c>), which is why the
        /// previous body's constant <c>"stay"</c> looked plausible and was still wrong.</para>
        /// </summary>
        /// <param name="alive">AS3 <c>sost != 3</c>.</param>
        /// <param name="bossState">AS3 <c>aiState</c>.</param>
        /// <param name="grounded">AS3 <c>stay</c>.</param>
        /// <param name="velocityX">AS3 <c>dx</c>, in px/frame.</param>
        public static string ResolveAnimState(bool alive, int bossState, bool grounded, float velocityX)
        {
            // `if(sost == 3) { if(animState != "die") gotoAndStop("die"); }` (:136-143).
            if (!alive) return "die";

            // `else if(aiState == 4 || aiState == 6) { ... "bac"; }` (:144-151) — the quake wind-up.
            if (bossState == 4 || bossState == 6) return "bac";

            if (grounded)
            {
                // `if(dx < 1 && dx > -1) "stay"; else if(aiState == 1) "run"; else "trot";` (:152-175).
                if (velocityX < 1f && velocityX > -1f) return "stay";
                if (bossState == 1) return "run";
                return "trot";
            }

            // The final `else` (:176-190) — airborne, which for this unit is only ever a jump.
            return "jump";
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The two contact scales (UnitBossRaider.as:513, :558)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Math.abs(dx - celUnit.dx) &gt; 8</c> — the <b>relative</b> closing speed, in px/frame.
        /// </summary>
        public const float FullContactSpeed = 8f;

        /// <summary>
        /// State 1's body hit (<c>:513</c>): <c>attKorp(celUnit, Math.abs(dx - celUnit.dx) &gt; 8 ? 1 : 0.5)</c>.
        ///
        /// <para><b>Relative, not absolute — and that is the difference between a charge and a
        /// scrape.</b> A boss running at 9 px/frame into a player running away at 9 px/frame is closing
        /// at <b>zero</b>, and the oracle pays half. Reading only the boss's own <c>dx</c> pays full,
        /// which turns every chase into a full-damage contact tick. The port had that reading until
        /// this rule was extracted.</para>
        /// </summary>
        public static float ChargeContactScale(float attackerDx, float targetDx) =>
            Math.Abs(attackerDx - targetDx) > FullContactSpeed ? 1f : 0.5f;

        /// <summary>
        /// State 5's body hit (<c>:558</c>): <c>attKorp(celUnit, Math.abs(dx) &gt; 8 ? 1 : 0.5)</c>.
        ///
        /// <para><b>Absolute here, relative there — and the asymmetry is the oracle's.</b> State 5 is
        /// the vortex beat, where the boss is not charging: the test is on its own residual drift. Do
        /// not "fix" this one to match <see cref="ChargeContactScale"/>; the two lines really do differ
        /// (<c>:513</c> vs <c>:558</c>).</para>
        /// </summary>
        public static float VortexContactScale(float attackerDx) =>
            Math.Abs(attackerDx) > FullContactSpeed ? 1f : 0.5f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The reinforcement gate (UnitBossRaider.as:229-243)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>World.w.game.globalDif &gt; 1</c> — <b>Normal and above</b>. Difficulty is 0-indexed
        /// (<c>Game.as:41</c>, <c>globalDif = 2</c> is Normal), so this refuses only Easy and the tier
        /// below it.
        /// </summary>
        public const int ReinforcementMinDifficulty = 2;

        /// <summary><c>tr == 2</c> — the flamer is the only variant with a reinforcement budget.</summary>
        public const int ReinforcementVariant = 2;

        /// <summary>
        /// <c>_loc6_ = Math.floor((maxhp - hp) / maxhp * 4)</c> — how many quarters are gone, <b>0..4</b>.
        /// The oracle keeps this in a local of <c>damage()</c>; it is a rule, so it lives here.
        ///
        /// <para><b>4 needs a corpse, and that is not a corner case.</b> <c>missing = (maxhp-hp)/maxhp</c>
        /// is strictly below 1 while the boss is alive, so <c>missing*4 &lt; 4</c> and the floor tops out
        /// at <b>3</b> for every living HP value. The fourth quarter appears only on the killing blow,
        /// where <c>hp = 0</c> — and because <c>damage()</c> runs its reinforcement block <i>after</i>
        /// <c>super.damage()</c> (which is what calls <c>die()</c>), the oracle really does call that
        /// wave from a corpse. A reader who assumes "four quarters for a living boss" gets the budget
        /// wrong by one; the <c>IsAlive</c> gate that would hide it is a port invention, removed in
        /// <c>BossRaiderBrain.CheckQuarterHpThresholds</c>.</para>
        /// </summary>
        public static int QuarterFor(float maxHp, float hp)
        {
            if (maxHp <= 0f) return 0;
            return (int)Math.Floor((maxHp - hp) / maxHp * 4f);
        }

        /// <summary>
        /// <c>if(tr == 2 &amp;&amp; globalDif &gt; 1) { _loc6_ = floor(...); if(_loc6_ &gt; called) { loc.enemySpawn(true,true); ++called; } }</c>
        /// (<c>:233-241</c>).
        ///
        /// <para><b>Three terms, all load-bearing.</b> A <c>tr == 1</c> boss never calls for help at
        /// all; on Easy it never does either; and <paramref name="called"/> is what makes the budget
        /// <b>four</b> and not "one per damage event" — the guard is on the quarter index, so a boss
        /// parked at 20 % HP does not spawn a wave every tick.</para>
        /// </summary>
        /// <param name="variant">AS3 <c>tr</c>.</param>
        /// <param name="difficulty">AS3 <c>globalDif</c>, 0..4.</param>
        /// <param name="quarter">The freshly crossed quarter, 1..4.</param>
        /// <param name="called">AS3 <c>called</c> — how many waves this boss has already called.</param>
        public static bool ShouldCallReinforcement(int variant, int difficulty, int quarter, int called) =>
            variant == ReinforcementVariant &&
            difficulty >= ReinforcementMinDifficulty &&
            quarter > called;
    }
}

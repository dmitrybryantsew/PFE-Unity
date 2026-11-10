using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The numbers and the selection rules of <c>UnitBossNecr</c> — the Centurion zebra's necromancer,
    /// "Mchavi", in <c>bossnecr1</c>.
    ///
    /// <para><b>Why a rules class rather than literals in the brain.</b> Every value here is a
    /// transcription of <c>fe/unit/UnitBossNecr.as</c>, and the port's own history is that a boss
    /// brain written from memory passes a structural review and still fights wrong — the alicorn, the
    /// raider and the Centurion all shipped an invented constant that only a fixture against the
    /// oracle's literal caught. Keeping them here means an offline harness can pin each one without a
    /// Unity scene, and the brain becomes wiring.</para>
    ///
    /// <para><b>Both halves that used to be listed here as absent are now delivered.</b> The curse
    /// circles go out as <see cref="MagSymbol"/> instances over the port's existing
    /// <c>ActiveEffectSet</c>; the summons go out through
    /// <c>BossSummonSpawner.Summon</c>, which builds a real unit at an <c>enspawn</c> marker. What this
    /// class still owns is the <i>selection</i> of both — which curse, which summon, at which marker,
    /// with which health and damage multipliers — because those are the parts that were invented wrong
    /// the first time.</para>
    /// </summary>
    public static class BossNecrRules
    {
        // ── UnitBossNecr.as:20-26 ────────────────────────────────────────────────────────────────
        public const int ProtectCooldown = 600;      // timeProtectCuld
        public const int ProtectDuration = 250;      // timeProtect
        public const int CurseCooldown = 300;        // timeCurseCuld
        public const int AttackCooldown = 350;       // timeAttackCuld
        public const int MaxSummons = 6;             // kol_emit (:16)

        /// <summary>
        /// <c>summonAtkMult</c> (<c>:50</c>) — every summoned body's damage is scaled by this.
        /// Difficulty 3 raises it to 0.65 and 4 to 0.8 (<c>:79-86</c>).
        /// </summary>
        public const float SummonAttackScale = 0.5f;

        /// <summary>
        /// <c>protculd_t = timeProtectCuld / 3</c> (<c>:44</c>) — the first defence arrives after 200
        /// ticks, not after a full 600. The port had a flat 600 for every cycle, so the first shadow
        /// form was five seconds late.
        /// </summary>
        public const int FirstProtectCooldown = ProtectCooldown / 3;

        /// <summary>
        /// <c>curseculd_t = timeCurseCuld / 2</c> (<c>:45</c>).
        /// </summary>
        public const int FirstCurseCooldown = CurseCooldown / 2;

        /// <summary><c>atk_t = 150</c> (<c>:30</c>) — the first blast/summon beat.</summary>
        public const int FirstAttackCooldown = 150;

        /// <summary>
        /// The three rotating defence forms, in the order <c>prot_n</c> visits them.
        ///
        /// <para><c>prot_n</c> starts at <b>1</b> (<c>:42</c>), so the first form cast is
        /// <see cref="Shadow"/> — not the heal. <c>castProtect()</c> then increments and wraps at 3
        /// (<c>:615-621</c>), giving shadow → invisible → heal → shadow…</para>
        /// </summary>
        public enum ProtectForm
        {
            /// <summary><c>heal(this.healHp)</c>, where <c>healHp = maxhp / 10</c> (<c>:103</c>).</summary>
            Heal = 0,

            /// <summary>
            /// <c>isShadow = invulner = transp = true</c> (<c>:604-612</c>): immune to damage, cannot
            /// attack, and cannot levitate for the duration.
            /// </summary>
            Shadow = 1,

            /// <summary>
            /// <c>superInvis = true; isVis = false</c> (<c>:613-619</c>): invisible but still
            /// damageable — the opposite trade from <see cref="Shadow"/>.
            /// </summary>
            Invisible = 2,
        }

        /// <summary>
        /// The form <c>castProtect()</c> applies for a given <c>prot_n</c>.
        ///
        /// <para><c>param1 == 0</c> is the timer-driven call and <c>param1 == 1</c> is the levitation
        /// trigger; both land in the same chain (<c>:598-627</c>), and with <c>param1 == 0</c> the heal
        /// arm needs <c>prot_n == 0</c>, the shadow arm <c>prot_n == 1</c>, and the rest is
        /// invisible.</para>
        /// </summary>
        public static ProtectForm FormFor(int protN) =>
            protN == 0 ? ProtectForm.Heal : (protN == 1 ? ProtectForm.Shadow : ProtectForm.Invisible);

        /// <summary><c>++prot_n; if(prot_n &gt;= 3) prot_n = 0;</c> (<c>:615-621</c>).</summary>
        public static int NextProtN(int protN) => protN >= 2 ? 0 : protN + 1;

        /// <summary>
        /// The self-heal amount: <c>healHp = maxhp / 10</c> (<c>:103</c>), recomputed in
        /// <c>setLevel</c> (<c>:95-96</c>) so it follows the difficulty-scaled maximum.
        /// </summary>
        public static float HealAmount(float maxHp) => maxHp / 10f;

        /// <summary>
        /// <c>aiTCh = floor(rand * 100 + 40)</c> (<c>:426</c>) — 40..139.
        /// </summary>
        /// <param name="roll">A draw in [0,1), i.e. <c>UnityEngine.Random.value</c>.</param>
        public static int StateTimer(float roll) => (int)Math.Floor(roll * 100f + 40f);

        /// <summary>
        /// <c>atk_t = floor((rand * 0.6 + 0.7) * timeAttackCuld)</c> (<c>:385</c>) — 245..454.
        /// </summary>
        public static int AttackTimer(float roll) =>
            (int)Math.Floor((roll * 0.6f + 0.7f) * AttackCooldown);

        /// <summary>
        /// <c>curseculd_t = floor((rand * 1.6 + 0.2) * timeCurseCuld)</c> (<c>:342</c>) — 60..539.
        /// </summary>
        public static int CurseTimer(float roll) =>
            (int)Math.Floor((roll * 1.6f + 0.2f) * CurseCooldown);

        /// <summary>
        /// The curse pool, in the oracle's order (<c>:56</c>).
        ///
        /// <para><b>The pool is not the same size in both phases.</b> <c>castCurse</c> indexes
        /// <c>curses[floor(rand * (phase == 2 ? 9 : 5))]</c> (<c>:715</c>), so <c>sacrifice</c> and
        /// <c>antil</c> — the two that make phase 2 dangerous — cannot be drawn in phase 1 even though
        /// they are in the array.</para>
        /// </summary>
        static readonly string[] Curses =
        {
            "stupor", "weak", "pinkcloud", "relat", "fetter",
            "sacrifice", "sacrifice", "antil", "antil",
        };

        /// <summary>How many of <see cref="Curses"/> a phase can draw from.</summary>
        public static int CursePoolSize(int phase) => phase == 2 ? 9 : 5;

        /// <summary>The curse id for a draw, or null when the phase/roll pair is out of range.</summary>
        public static string CurseFor(int phase, float roll)
        {
            int pool = CursePoolSize(phase);
            int index = (int)Math.Floor(roll * pool);
            if (index < 0) index = 0;
            if (index >= pool) index = pool - 1;
            return Curses[index];
        }

        /// <summary>
        /// The <c>param5</c> values one curse beat uses, which are also its line offsets and its lifetime
        /// extensions — see <see cref="MagSymbolRules"/>.
        ///
        /// <para>Supersedes the earlier <c>CurseCircleCount</c>/<c>CurseCircleSpacing</c> pair: those
        /// described the same three shapes from the count side, and keeping both invited the two to
        /// drift. The phase gate (a five-circle line is phase 2 only) lives in
        /// <see cref="MagSymbolRules.ChooseBeat"/>, which is the code that actually runs.</para>
        /// </summary>
        public static int[] CurseSpacings(CurseBeat beat) => MagSymbolRules.SpacingsFor(beat);

        /// <summary>
        /// <c>while(_loc2_ &lt; 3)</c> (<c>:639</c>) — one <c>spawn()</c> call places <b>three</b>
        /// bodies, one per <c>enspawn</c> marker, not one.
        /// </summary>
        public const int SummonsPerCall = 3;

        /// <summary>
        /// <c>summonAtkMult</c> for a difficulty setting — <c>0.5</c> normally, <c>0.65</c> at
        /// <c>globalDif == 3</c> and <c>0.8</c> at <c>4</c> (<c>:50</c>, <c>:87-94</c>).
        /// </summary>
        /// <param name="globalDif">AS3 <c>World.w.game.globalDif</c>, 0..4.</param>
        public static float SummonAttackScaleFor(int globalDif) =>
            globalDif == 3 ? 0.65f : (globalDif == 4 ? 0.8f : SummonAttackScale);

        /// <summary>
        /// Which summon <c>spawn(atk_n % (phase == 1 ? 6 : 8))</c> asks for (<c>:382</c>,
        /// <c>:629-683</c>), or <c>null</c> when the beat summons nothing.
        ///
        /// <para>Only the <b>odd</b> indices produce anything: <c>1</c> → <c>zombie tr7</c>,
        /// <c>3</c> → <c>zombie tr8</c>, <c>5</c> → <c>necros</c>, <c>7</c> → <c>zombie tr9</c>; every
        /// even index falls through the chain's final <c>return</c> and summons nothing. That is the
        /// whole reason the boss's <c>atk_n</c> is incremented twice on a blast beat.</para>
        ///
        /// <para><b>The id returned is already composed.</b> AS3 builds the XML node
        /// <c>&lt;un id='zombie' tr='7' …/&gt;</c>, and <c>UnitZombie.as:92-104</c> then rewrites its own
        /// <c>id</c> to <c>"zombie" + tr</c> — so the unit that reaches the world is <c>zombie7</c>,
        /// which is exactly what <c>Resources/Units/zombie7.asset</c> holds. Returning the family name
        /// and letting the caller compose it would re-run the <c>randomCid</c> draw
        /// <c>createUnit</c> spends on the way in (<c>Location.as:1185</c>), and a summon must not touch
        /// the room's spawn stream: <c>SpawnUnitNow</c> deliberately draws nothing from it either.</para>
        /// </summary>
        /// <param name="atkN">AS3 <c>atk_n</c>.</param>
        /// <param name="phase">1 or 2; the modulus is 6 in phase 1 and 8 in phase 2.</param>
        public static string SummonUnitFor(int atkN, int phase)
        {
            int index = atkN % (phase == 2 ? 8 : 6);
            switch (index)
            {
                case 1: return "zombie7";   // <un id='zombie' tr='7' hpmult='0.85'/>
                case 3: return "zombie8";   // <un id='zombie' tr='8' hpmult='0.75'/>
                case 5: return "necros";    // <un id='necros' hpmult='0.65'/>
                case 7: return "zombie9";   // <un id='zombie' tr='9' hpmult='0.65'/>
                default: return null;
            }
        }

        /// <summary>
        /// The <c>hpmult</c> the summon is placed with (<c>:634-660</c>). It multiplies the unit row's
        /// own <c>&lt;comb hp&gt;</c> — <c>Unit.as:1128</c> <c>this.hp = this.maxhp = node.@hp *
        /// this.hpmult</c> — so a <c>zombie7</c> summon is a <i>weaker</i> zombie, not a different one.
        /// </summary>
        public static float SummonHpMult(int atkN, int phase)
        {
            int index = atkN % (phase == 2 ? 8 : 6);
            switch (index)
            {
                case 1: return 0.85f;
                case 3: return 0.75f;
                case 5: return 0.65f;
                case 7: return 0.65f;
                default: return 0f;
            }
        }

        /// <summary>
        /// <c>enspawn[param2]</c> (<c>Location.as:3010-3014</c>) — which marker the
        /// <paramref name="waveIndex"/>-th summon of a call lands on.
        ///
        /// <para><b>The fallback is the oracle's, and it is a random marker rather than a failure.</b>
        /// <c>waveSpawn</c> does <c>_loc4_ = this.enspawn[param2]; if(_loc4_ == null) _loc4_ =
        /// this.enspawn[floor(random * length)];</c> — so a room with fewer than three <c>enspawn</c>
        /// rows still spawns all three bodies, stacked on whichever markers exist.
        /// <c>bossnecr1</c> authors exactly three (<c>RoomsSerial.as:5820, 5821, 5824</c>), so the
        /// fallback never fires there; it exists for every other room a future script puts her in.</para>
        /// </summary>
        /// <returns>The index into the room's ordered enemy markers, or <c>-1</c> when it has none.</returns>
        public static int WavePointIndex(int waveIndex, int pointCount, float roll)
        {
            if (pointCount <= 0) return -1;
            if (waveIndex >= 0 && waveIndex < pointCount) return waveIndex;

            int index = (int)Math.Floor(roll * pointCount);
            if (index < 0) index = 0;
            if (index >= pointCount) index = pointCount - 1;
            return index;
        }

        /// <summary>
        /// <c>die()</c>'s first arm (<c>:567-584</c>): the phase-1 death revives at
        /// <c>hp = maxhp - 0.1</c> with <c>dexter = 2</c>, <c>blood = 0</c> and a shorter curse
        /// cooldown (<c>timeCurseCuld = 220</c>).
        /// </summary>
        public static float HpAfterRevive(float maxHp) => Math.Max(1f, maxHp - 0.1f);

        /// <summary>The curse cooldown phase 2 runs with (<c>:580</c>).</summary>
        public const int PhaseTwoCurseCooldown = 220;

        // ══ The aiState machine ══════════════════════════════════════════════════════════════════
        //
        // `control():282-565` is a four-state machine with its own timers, and it is the half that
        // makes her *fight* rather than merely exist. The port used to skip it: `BossNecrBrain` kited
        // from the generic `TickCombatChase` at a made-up 150 px, which is why she backed away forever
        // and never cast `necrblast3`.
        //
        // The states are not a difficulty knob — each one is a distinct behaviour:
        //   0 Stand   — decelerate, face the way momentum carries, `walk = 0` (:461-472)
        //   1 Move    — accelerate toward `aiNapr` at run speed, occasionally jump (:473-554)
        //   2 Blast   — root, face the player, fire the weapon for 120 of the 140 ticks (:555-564)
        //   3 Protect — the "cast" pose; on expiry it casts a defence form (:394-398)

        /// <summary>The oracle's four <c>aiState</c> values (<c>:355-564</c>).</summary>
        public enum NecrAiState
        {
            /// <summary><c>aiState 0</c> — standing; <c>walk = 0</c>, facing follows <c>dx</c>.</summary>
            Stand = 0,

            /// <summary><c>aiState 1</c> — running at <c>runSpeed</c> toward <c>aiNapr</c>.</summary>
            Move = 1,

            /// <summary><c>aiState 2</c> — the <c>necrblast3</c> burst: rooted, weapon firing.</summary>
            Blast = 2,

            /// <summary><c>aiState 3</c> — the protect cast; leaves by casting a defence form.</summary>
            Protect = 3,
        }

        /// <summary><c>aiTCh = 80</c> in the constructor (<c>:75</c>).</summary>
        public const int InitialStateTimer = 80;

        /// <summary><c>runSpeed = maxSpeed * 3</c> (<c>:71</c>) — the reason she closes distance.</summary>
        public const float RunSpeedMultiplier = 3f;

        /// <summary><c>aiState = 2; aiTCh = 140;</c> (<c>:368-369</c>) — the blast lasts 140 ticks.</summary>
        public const int BlastStateTimer = 140;

        /// <summary>
        /// <c>if(aiTCh &lt; 120) currentWeapon.attack();</c> (<c>:560</c>).
        ///
        /// <para>With <see cref="BlastStateTimer"/> = 140 that is a <b>20-tick wind-up</b> and then 120
        /// ticks of firing — the burst is not instant on entry, which is the window the player uses to
        /// break line of sight.</para>
        /// </summary>
        public const int BlastFireThreshold = 120;

        /// <summary><c>aiState = 3; aiTCh = 30;</c> (<c>:229-230</c>) — the protect cast pose.</summary>
        public const int ProtectCastTimer = 30;

        /// <summary>
        /// <c>if(aiTCh % 10 == 1)</c> (<c>:432</c>, <c>:445</c>) — retarget, and the moment the kiting
        /// flip is decided. Every tenth tick, not every tick.
        /// </summary>
        public const int RetargetPeriod = 10;

        /// <summary>
        /// <c>celDX &lt; 400</c> / <c>celDX &gt; -400</c> (<c>:447-454</c>) — she backs away while the
        /// player is within 400 px, not within the 150 the port invented.
        /// </summary>
        public const float KiteRange = 400f;

        /// <summary>
        /// <c>celDY &lt; 80 &amp;&amp; celDY &gt; -80</c> (<c>:445</c>) — she only repositions when the
        /// player is roughly level with her.
        /// </summary>
        public const float KiteVerticalBand = 80f;

        /// <summary><c>isrnd(0.8)</c> (<c>:402</c>) — on leaving state 0 she usually runs *away*.</summary>
        public const float FleeRoll = 0.8f;

        /// <summary><c>isrnd(0.5)</c> (<c>:414</c>) — in any other state, half the time she flips.</summary>
        public const float TurnRoll = 0.5f;

        /// <summary><c>aiState = isrnd(0.7) ? 1 : 0</c> (<c>:418</c>).</summary>
        public const float MoveRoll = 0.7f;

        /// <summary><c>isrnd(0.3)</c> (<c>:421</c>) — jump out of the next movement tick.</summary>
        public const float JumpRoll = 0.3f;

        /// <summary><c>isrnd(0.3)</c> (<c>:425</c>) — raise the throw flag for <see cref="ThrowTicks"/>.</summary>
        public const float ThrowRoll = 0.3f;

        /// <summary><c>throu_t = 5</c> (<c>:427</c>).</summary>
        public const int ThrowTicks = 5;

        /// <summary><c>curseculd_t -= floor(dmg / 40)</c> (<c>:232</c>) — damage accelerates curses.</summary>
        public const int DamageCurseDivisor = 40;

        /// <summary><c>atk_t -= floor(dmg / 30)</c> (<c>:233</c>) — and accelerates the attack beat.</summary>
        public const int DamageAttackDivisor = 30;

        /// <summary>What one pass of the attack-timer block does (<c>:362-384</c>).</summary>
        public enum AttackBeat
        {
            /// <summary>Nothing — <c>spawn</c> was called with an index it ignores.</summary>
            Nothing,

            /// <summary><c>aiState = 2</c> — the <c>necrblast3</c> burst.</summary>
            Blast,

            /// <summary><c>spawn(atk_n % (phase == 1 ? 6 : 8))</c>.</summary>
            Summon,
        }

        /// <summary>
        /// The attack-timer block (<c>:362-384</c>), including the increment that makes it subtle.
        ///
        /// <code>
        /// ++atk_n;
        /// if(atk_n % 2 == 0) {
        ///    if(!isShadow &amp;&amp; findCel()) { aiState = 2; … }
        ///    else { ++atk_n; }
        /// }
        /// if(atk_n % 2 == 1) spawn(atk_n % (phase == 1 ? 6 : 8));
        /// </code>
        ///
        /// <para><b>The inner <c>++atk_n</c> is not a typo and not a no-op.</b> When the blast is
        /// refused — she is shadowed, or has no target — the extra increment turns the same tick's
        /// index odd, so <c>if(atk_n % 2 == 1)</c> immediately fires and <b>she summons instead of
        /// blasting</b>. So the beat is never wasted: every attack-timer expiry produces a blast or a
        /// summon, and a shadowed boss summons twice as often as a visible one. A port that reads the
        /// block as "even blasts, odd summons" gets a boss that silently skips half her beats while
        /// shadowed.</para>
        /// </summary>
        /// <param name="atkN">The index on entry; the caller stores <paramref name="nextAtkN"/> back.</param>
        /// <param name="isShadow">True while the shadow form is up — it forbids the blast.</param>
        /// <param name="hasTarget"><c>findCel()</c> — whether a target is resolvable.</param>
        public static AttackBeat NextAttack(int atkN, bool isShadow, bool hasTarget,
                                            out int nextAtkN)
        {
            nextAtkN = atkN + 1;

            bool blast = false;
            if (nextAtkN % 2 == 0)
            {
                if (!isShadow && hasTarget)
                {
                    blast = true;
                }
                else
                {
                    nextAtkN++;
                }
            }

            if (blast) return AttackBeat.Blast;
            if (nextAtkN % 2 == 1) return AttackBeat.Summon;
            return AttackBeat.Nothing;
        }

        /// <summary>What the state timer's expiry does — the transition plus its side decisions.</summary>
        public struct StateTransition
        {
            /// <summary>The state the next tick runs in.</summary>
            public NecrAiState State;

            /// <summary>The direction she will move, <c>aiNapr</c> — also copied to <c>storona</c>.</summary>
            public int Direction;

            /// <summary>True when <c>aiState</c> was 3 on entry, so the caller must cast a defence.</summary>
            public bool CastProtect;

            /// <summary><c>_loc2_ = 1</c> — jump on the next movement tick.</summary>
            public bool Jump;

            /// <summary><c>throu_t = 5; throu = true;</c> — the throw pose.</summary>
            public bool Throw;

            /// <summary>How many random draws this transition spent.</summary>
            public int Draws;
        }

        /// <summary>
        /// The <c>aiTCh == 0</c> block (<c>:392-431</c>).
        ///
        /// <para><b>State 3 falls through into the state-0 arm.</b> The oracle reads
        /// <c>if(aiState == 3){ aiState = 0; castProtect(); } if(aiState == 0){ … } else { … }</c> — two
        /// separate <c>if</c>s, not <c>else if</c>. So leaving the protect cast sets the state to 0 and
        /// then takes the state-0 branch in the same pass, which always ends in state 1. Reading it as a
        /// chain would leave her standing for a whole timer period after every defence.</para>
        ///
        /// <para><b>Draw order and count matter.</b> State 3 and state 0 spend one draw
        /// (<see cref="FleeRoll"/>) then two more (<see cref="JumpRoll"/>, <see cref="ThrowRoll"/>) — 3
        /// in total; states 1 and 2 spend <see cref="TurnRoll"/>, then <see cref="MoveRoll"/>, then the
        /// same two — 4. The stream is shared with the curse draws and the cooldown, so a port that
        /// spends a different number desynchronises everything after it.</para>
        /// </summary>
        /// <param name="state">The state on entry to the block.</param>
        /// <param name="celDX">The target's x minus hers — decides which way "away" is.</param>
        /// <param name="currentDirection">
        /// <c>aiNapr</c> on entry. The turn draw only overwrites it half the time (<c>:414</c>), so the
        /// caller's value is what survives a failed draw.
        /// </param>
        /// <param name="roll">A <c>Math.random</c>-equivalent source, called in the oracle's order.</param>
        public static StateTransition OnStateTimerExpiry(NecrAiState state, float celDX,
                                                         int currentDirection, Func<float> roll)
        {
            var transition = new StateTransition { Direction = currentDirection };
            NecrAiState current = state;

            if (current == NecrAiState.Protect)
            {
                current = NecrAiState.Stand;
                transition.CastProtect = true;
            }

            // The second `if` tests the state the first one just wrote — hence the fall-through.
            if (current == NecrAiState.Stand)
            {
                transition.State = NecrAiState.Move;
                transition.Direction = roll() < FleeRoll ? AwayFrom(celDX) : Toward(celDX);
                transition.Draws++;
            }
            else
            {
                if (roll() < TurnRoll) transition.Direction = AwayFrom(celDX);
                transition.Draws++;

                transition.State = roll() < MoveRoll ? NecrAiState.Move : NecrAiState.Stand;
                transition.Draws++;
            }

            // Both of these are spent whether or not they succeed — a failed draw still advances the
            // oracle's `Math.random` stream, so the count has to be unconditional.
            if (roll() < JumpRoll) transition.Jump = true;
            transition.Draws++;

            if (roll() < ThrowRoll) transition.Throw = true;
            transition.Draws++;

            return transition;
        }

        /// <summary>
        /// <c>celDX &gt; 0 ? -1 : 1</c> — the direction that increases the distance, i.e. away.
        /// </summary>
        public static int AwayFrom(float celDX) => celDX > 0f ? -1 : 1;

        /// <summary><c>celDX &gt; 0 ? 1 : -1</c> — the direction that closes the distance.</summary>
        public static int Toward(float celDX) => celDX > 0f ? 1 : -1;

        /// <summary>
        /// The kiting flip inside the <c>aiTCh % 10 == 1 &amp;&amp; aiState == 1</c> block
        /// (<c>:445-455</c>).
        ///
        /// <para>Returns 0 when the block does not fire — wrong state, wrong tick phase, the player
        /// outside the vertical band, or outside <see cref="KiteRange"/> — meaning "keep the current
        /// direction". Note this is a <b>reposition, not a flee</b>: it re-points her along the ground
        /// every ten ticks so a player who walks under her cannot sit there forever.</para>
        /// </summary>
        public static int KiteDirection(float celDX, float celDY, int aiTCh, NecrAiState state)
        {
            if (aiTCh % RetargetPeriod != 1) return 0;
            if (state != NecrAiState.Move) return 0;
            if (celDY >= KiteVerticalBand || celDY <= -KiteVerticalBand) return 0;

            if (celDX < 0f && celDX > -KiteRange) return 1;
            if (celDX > 0f && celDX < KiteRange) return -1;
            return 0;
        }

        /// <summary>
        /// <c>maxSpeed = walkSpeed; if(aiState == 1) maxSpeed = runSpeed;</c> (<c>:456-460</c>).
        /// </summary>
        public static float MaxSpeedFor(NecrAiState state, float walkSpeed, float runSpeed) =>
            state == NecrAiState.Move ? runSpeed : walkSpeed;

        /// <summary>
        /// The two <c>damage()</c> timer shavings (<c>:232-233</c>) — a hit brings the next curse and
        /// the next attack beat forward.
        /// </summary>
        /// <param name="damageDealt">
        /// What <c>super.damage()</c> <b>returned</b> — the damage actually applied, after armour and
        /// resistance, not the amount offered. An attack absorbed by the shadow form returns 0 and
        /// shaves nothing, which is why the caller must pass the result rather than the input.
        /// </param>
        public static void ShaveTimersForDamage(float damageDealt, ref int curseCooldownTicks,
                                                ref int attackTimerTicks)
        {
            curseCooldownTicks -= (int)Math.Floor(damageDealt / DamageCurseDivisor);
            attackTimerTicks -= (int)Math.Floor(damageDealt / DamageAttackDivisor);
        }

        // ── animation (UnitBossNecr.animate():120-212) ────────────────────────────────────────────

        /// <summary>
        /// Which clip <c>animate()</c> selects, in the oracle's own branch order.
        /// </summary>
        /// <remarks>
        /// <para><b>The port returned <c>"stay"</c> for every living state, and that is the "no
        /// animation" half of the report.</b> <c>visualNecrBoss</c> is a 12-frame state selector whose
        /// labels name the body clip to draw (<c>stay</c>, <c>trot</c>, <c>run</c>, <c>cast</c>,
        /// <c>jump</c>, <c>laz</c>, <c>plav</c>, <c>punch</c>, <c>kick</c>, <c>die</c>, <c>derg</c>,
        /// <c>walk</c>), so a single answer freezes the boss on frame one of <c>stay</c> for the whole
        /// fight: she slides around the arena with her feet still.</para>
        ///
        /// <para><b>The order is the whole rule.</b> <c>die</c> beats <c>cast</c>; <c>cast</c> beats the
        /// grounded ladder; the grounded ladder beats <c>derg</c>, which beats <c>jump</c>. Reading
        /// <c>aiState == 3</c> before the death test would play the cast pose on a corpse, and reading
        /// <c>aiState == 1</c> before <c>stay</c> would play <c>run</c> while she is rooted in the blast
        /// state — which is the state that most needs to look different from moving.</para>
        ///
        /// <para><b><c>sost == 2</c> and <c>sost == 3</c> are the same answer</b> (<c>:123-139</c>):
        /// both are the <c>die</c> clip. <c>sost == 2</c> is the phase-one death that becomes the revive,
        /// so the port's <c>!alive</c> test covers the pair; the revive itself re-enters <c>stay</c> on
        /// the next tick because <c>sost</c> returns to 1.</para>
        /// </remarks>
        /// <param name="alive"><c>sost &lt; 2</c>.</param>
        /// <param name="state">The <c>aiState</c> integer.</param>
        /// <param name="grounded">AS3 <c>stay</c> — the unit is standing on something.</param>
        /// <param name="levitating">AS3 <c>levit</c>.</param>
        /// <param name="velocityX">AS3 <c>dx</c> in px/frame; <c>|dx| &lt; 1</c> reads as standing still.</param>
        public static string ResolveAnimState(
            bool alive, NecrAiState state, bool grounded, bool levitating, float velocityX)
        {
            if (!alive)
            {
                return "die";
            }

            if (state == NecrAiState.Protect)
            {
                return "cast";
            }

            if (grounded)
            {
                if (Math.Abs(velocityX) < StationaryVelocity)
                {
                    return "stay";
                }

                return state == NecrAiState.Move ? "run" : "trot";
            }

            return levitating ? "derg" : "jump";
        }

        /// <summary>
        /// <c>if(dx &lt; 1 &amp;&amp; dx &gt; -1)</c> (<c>:150</c>) — the "standing still" band. A strict
        /// comparison in the oracle, so exactly 1 px/frame counts as moving.
        /// </summary>
        public const float StationaryVelocity = 1f;
    }
}

using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// Boss brain for the necromancer — <c>UnitBossNecr</c>, the Centurion zebra's "Mchavi" in
    /// <c>bossnecr1</c>.
    ///
    /// <para><b>Two-phase encounter with a rotating defence.</b> On the first lethal hit she revives at
    /// <c>maxhp - 0.1</c> with a shorter curse cooldown; on the second she dies for good and takes her
    /// summons with her. Between hits she cycles three defence forms — <b>shadow</b> (immune, but also
    /// unable to attack), <b>invisible</b> (untargetable to the eye, still damageable) and <b>heal</b> —
    /// and fires <c>necrbullet</c>.</para>
    ///
    /// <para><b>The numbers live in <see cref="BossNecrRules"/>.</b> They are transcriptions of
    /// <c>fe/unit/UnitBossNecr.as</c> with the line number beside each, so an offline fixture can pin
    /// them; this class is the wiring.</para>
    ///
    /// <para><b>The curses are real circles now, not direct damage.</b> <c>castCurse</c> places a
    /// <see cref="MagSymbol"/> — a 100 px radius, a tick countdown, and one effect applied only to a
    /// player still standing inside it (<c>fe/weapon/MagSymbol.as</c>). The rules live in
    /// <see cref="MagSymbolRules"/>. Before that existed this brain reported damage straight to the
    /// player on the curse beat, at any range and through walls.</para>
    ///
    /// <para><b>The summons are real bodies now.</b> <c>spawn()</c> puts three units at the room's
    /// <c>enspawn</c> markers through <see cref="BossSummonSpawner"/>, capped at six alive at once, and
    /// they die with her on the death that sticks. Before that the arena simply stayed empty — the
    /// selection rules were pinned in <see cref="BossNecrRules.SummonUnitFor"/> with nothing to call
    /// them.</para>
    ///
    /// <para><b>What this brain still does not do.</b> The <c>magsymbol</c> emit at each spawn marker
    /// (<c>Location.as:3016-3019</c>) has no port: <c>magsymbol</c> is not an id in the port's particle
    /// table, so emitting it would draw nothing and warn on every beat. The summons also arrive without
    /// their <c>emergence(30)</c> rise (<c>:3015</c>, no seam), and <c>_loc4_.inter.cont = ""</c>
    /// (<c>:666</c>) has no equivalent because there is no per-unit interact container to clear.</para>
    /// </summary>
    public class BossNecrBrain : BossBrain
    {
        /// <summary>
        /// <c>prot_n</c> starts at <b>1</b> (<c>UnitBossNecr.as:42</c>), so the first form she casts is
        /// shadow — not the heal. Starting at 0 (as this brain did) put the heal first and every later
        /// form one step out of phase.
        /// </summary>
        int _protN = 1;

        int _protCooldownTicks = BossNecrRules.FirstProtectCooldown;
        int _protTicks;                       // `prot_t`
        int _curseCooldownTicks = BossNecrRules.FirstCurseCooldown;

        bool _isShadow;
        bool _superInvis;

        // ── the aiState machine (control():282-565) ──────────────────────────────────────────────
        //
        // These were absent: the brain kited from the generic `TickCombatChase` at an invented 150 px
        // and never entered a blast state at all, so `necrblast3` — her only area attack — could not
        // happen. See BossNecrRules.NecrAiState for what each state does.

        BossNecrRules.NecrAiState _aiState = BossNecrRules.NecrAiState.Stand;

        /// <summary><c>aiTCh</c> — the state timer; <c>aiTCh = 80</c> at construction (<c>:75</c>).</summary>
        int _aiTCh = BossNecrRules.InitialStateTimer;

        /// <summary><c>atk_n</c> — the attack index the blast/summon cadence keys off.</summary>
        int _attackIndex;

        /// <summary><c>atk_t</c> — <c>150</c> at construction (<c>:30</c>).</summary>
        int _attackTicks = BossNecrRules.FirstAttackCooldown;

        /// <summary><c>aiNapr</c> — the direction she is trying to travel.</summary>
        int _aiNapr = -1;

        /// <summary><c>storona</c> — the direction she is facing.</summary>
        int _storona = -1;

        /// <summary><c>throu_t</c> — the throw pose's countdown (<c>:309-316</c>).</summary>
        int _throuTicks;

        float _walkSpeed = 6f;
        float _runSpeed = 18f;

        /// <summary>
        /// Gates the direct-damage fallback in <see cref="ExecuteAttackAction"/>.
        ///
        /// <para>The blast state calls the attack <b>every tick</b> for 120 ticks, as
        /// <c>currentWeapon.attack()</c> does. A weapon mount rate-limits itself, but the fallback —
        /// used only when no mount exists — would otherwise deal damage on all 120.</para>
        /// </summary>
        int _fallbackCooldownTicks;

        /// <summary>The unit <c>celX</c>/<c>celY</c> currently point at (<c>:432-441</c>).</summary>
        UnitController _celTarget;

        /// <summary>
        /// AS3 <c>kolChild</c> — how many summons she has alive right now, capped by
        /// <see cref="BossNecrRules.MaxSummons"/> (<c>kol_emit</c>, <c>:16</c>).
        ///
        /// <para><b>It is a count of the living, not a lifetime budget.</b> <c>spawn()</c> refuses while
        /// <c>kolChild &gt;= kol_emit</c> (<c>:633-636</c>) and the number goes back down when a summon
        /// dies — <c>--this.mother.kolChild</c> in <c>Unit.as:4386</c>, reached from the child's own
        /// death. So the cap is "six bodies on the field at once", and killing them is how the player
        /// buys room. A one-way budget would leave the arena empty for the second half of the fight
        /// after six spawns; a count that never decremented would stop her at six forever.</para>
        /// </summary>
        int _kolChild;

        /// <summary>
        /// The live summons, so the final death can take them with her (<c>die():585-595</c>:
        /// <c>for each(_loc2_ in loc.units) if(_loc2_.mother == this) _loc2_.die();</c>).
        ///
        /// <para>AS3 finds them by the <c>mother</c> back-reference; the port keeps the list, because a
        /// brain has no object list to walk and the summons live in the room's own <c>units</c>.</para>
        /// </summary>
        readonly List<UnitController> _summons = new List<UnitController>();

        /// <summary>The oracle's <c>aiState</c>, for tests and the debug overlay.</summary>
        public BossNecrRules.NecrAiState AiState => _aiState;

        /// <summary>The oracle's <c>aiTCh</c>.</summary>
        public int StateTimerTicks => _aiTCh;

        /// <summary>The oracle's <c>atk_n</c>.</summary>
        public int AttackIndex => _attackIndex;

        /// <summary>The form currently cycling: 0 heal, 1 shadow, 2 invisible.</summary>
        public int ProtectCycle => _protN;

        /// <summary>AS3 <c>kolChild</c> — the summons alive right now.</summary>
        public int SummonCount => _kolChild;

        /// <summary>True while any defence form is running.</summary>
        public bool IsProtectActive => _protTicks > 0;

        /// <summary>
        /// True while she is in the shadow form — <b>immune to damage and unable to deal it</b>
        /// (<c>:604-612</c>). Both halves matter: the oracle's <c>attack()</c> is unreachable while
        /// <c>isShadow</c>, which is why the wiki's advice is to stop shooting and wait it out.
        /// </summary>
        public bool IsShadow => _isShadow;

        /// <summary>True while she is invisible (<c>superInvis</c>, <c>:613-619</c>).</summary>
        public bool IsSuperInvisible => _superInvis;

        protected override bool StopsToAttack => true;

        protected override void Awake()
        {
            base.Awake();
            _phaseIndex = 1;
            ApplyTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyTuning();
        }

        protected virtual void ApplyTuning()
        {
            if (_controller == null || _controller.Definition == null) return;
            UnitDefinition def = _controller.Definition;

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 6f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * 1.5f;
            _alertSpeed = spd;

            // `walkSpeed = maxSpeed; runSpeed = maxSpeed * 3;` (:69-71). The state machine picks
            // between the two by state (BossNecrRules.MaxSpeedFor), which is why both are kept.
            _walkSpeed = spd;
            _runSpeed = spd * BossNecrRules.RunSpeedMultiplier;
        }

        /// <summary>
        /// The tick, in the shape <c>BossAlicornBrain</c> established: guards, perception, the oracle's
        /// own <c>control()</c>, then the drawing and weapon tails.
        /// </summary>
        /// <remarks>
        /// <b>This deliberately does not call <c>base.SimTick</c>.</b> The base is
        /// <c>EnemyBrain</c>'s <see cref="EnemyAIState"/> machine, and the oracle's boss does not use
        /// it — <c>UnitBossNecr.control()</c> is its own four-state machine. What the base still
        /// contributes is hoisted into named calls here: <see cref="EnemyBrain.UpdatePerception"/> for
        /// target acquisition, <c>UpdateAnimation</c>, <c>TickWeapons</c>, and <c>BossBrain</c>'s
        /// quarter-HP and arena hooks.
        /// </remarks>
        public override void SimTick(int tickIndex)
        {
            if (_controller == null) return;

            // The circles step even when she is dead: a curse already in the air still resolves, which
            // is the oracle's behaviour (`loc.objs` outlive the caster).
            if (!_controller.IsAlive)
            {
                TickSymbols();
                UpdateAnimation();
                return;
            }

            // `if(World.w.enemyAct <= 0) { … return; }` (:299-304) — the global freeze stops the
            // decision half only; physics, drawing and the weapon mounts keep running.
            if (EnemyActivity.IsFrozen)
            {
                UpdateAnimation();
                TickWeapons();
                TickSymbols();
                return;
            }

            UpdatePerception(tickIndex);

            TickControl();

            TickSymbols();

            // BossBrain's own tail, kept for the same reason the alicorn keeps it: neither is authored
            // for this boss, but they are the hooks a scripted encounter plugs into.
            CheckQuarterHpThresholds();

            UpdateAnimation();
            TickWeapons();
            ClampToArenaBounds();
        }

        /// <summary>
        /// AS3 <c>control():282-565</c>, in the oracle's order.
        /// </summary>
        /// <remarks>
        /// <para><b>What this replaces.</b> The brain used to run a timer-driven defence —
        /// <c>if (protculd_t &gt; 0) --protculd_t; else CastProtect();</c> — so she cycled heal → shadow
        /// → invisible on a clock whether or not anything was happening. The oracle is <b>reactive</b>:
        /// a defence is only ever cast out of state 3, and state 3 is only ever entered from
        /// <c>damage()</c> (<c>:227-231</c>). So the fight's rhythm is "hit her and she answers", which
        /// is also why the wiki's advice is to stop shooting during the shadow form rather than to wait
        /// out a fixed cycle.</para>
        ///
        /// <para><b>Order matters throughout.</b> The curse beat runs before the attack beat, the attack
        /// beat before the state timer, and the state timer before the movement arm — because the
        /// movement arm reads <c>aiState</c> and <c>maxSpeed</c> that the earlier blocks may have just
        /// changed. Reordering any of them changes the fight without changing a single constant.</para>
        /// </remarks>
        void TickControl()
        {
            UnitController player = ResolvePlayer();

            // `if(loc.gg.invulner) return;` (:295-298). Not a guard on damage — the whole tick is
            // skipped, timers included, so an invulnerable player freezes her outright.
            if (player != null && player.IsInvulnerable) return;

            // `if(Boolean(levit) && protculd_t <= 0) castProtect(1);` (:305-308) is unreachable here:
            // `levit` is player-only in this port (PlayerLocomotionController.IsLevitating) and nothing
            // sets it for an NPC. Recorded rather than stubbed, because a future levitation seam would
            // have to reinstate it — with param 1, which forces the shadow form.

            // `if(throu_t > 0) --throu_t; else throu = false;` (:309-316). Nothing reads `throu` in the
            // port yet; the timer is modelled so the field is not a silent hole.
            if (_throuTicks > 0) _throuTicks--;

            if (_fallbackCooldownTicks > 0) _fallbackCooldownTicks--;

            // `if(prot_t > 0) --prot_t; if(prot_t == 1) resetProtect();` (:317-324). The reset fires
            // with one tick still on the clock, so the form is cleared *before* the next cast rather
            // than after it — otherwise the new form and the old overlap for a tick and the shadow's
            // invulnerability outlives its own duration.
            if (_protTicks > 0)
            {
                _protTicks--;
                if (_protTicks == 1)
                {
                    ResetProtect();
                }
            }

            // `if(protculd_t > 0) --protculd_t;` (:325-328) — counted down, never acted on.
            if (_protCooldownTicks > 0) _protCooldownTicks--;

            TickCurse();

            // `if(aiState != 3 && aiState != 2)` (:355-386) — no attack beat while casting or blasting.
            if (_aiState != BossNecrRules.NecrAiState.Protect
                && _aiState != BossNecrRules.NecrAiState.Blast)
            {
                if (_attackTicks > 0)
                {
                    _attackTicks--;
                }
                else
                {
                    TickAttackBeat();
                }
            }

            // `invis = this.superInvis;` (:387) — the invisible form drives the flag the renderer reads;
            // the brain already applied it in CastProtect.

            // `if(aiTCh > 0) --aiTCh; else { …transition… }` (:388-431).
            //
            // `_loc2_` is a *local* in `control()`, declared at :294 and consumed at :548 — so a jump
            // decided by the timer block happens in the same tick, not the next one. Deferring it would
            // fire a jump a tick late, and would fire one even when the transition landed in state 0,
            // which the oracle's state-0 arm never does.
            float jumpMagnitude = 0f;
            if (_aiTCh > 0)
            {
                _aiTCh--;
            }
            else
            {
                jumpMagnitude = TickStateTimerExpiry();
            }

            // `if(aiTCh % 10 == 1)` — retarget (:432-441).
            if (_aiTCh % BossNecrRules.RetargetPeriod == 1)
            {
                _celTarget = PickCelTarget(player);
            }

            float celDX = 0f;
            float celDY = 0f;
            if (_celTarget != null)
            {
                float selfScY;
                Vector2 selfAs3 = As3Position(_controller, out selfScY);
                float targetScY;
                Vector2 targetAs3 = As3Position(_celTarget, out targetScY);

                celDX = targetAs3.x - selfAs3.x;

                // `celDY = celY - Y + scY;` (:444) — her own height is part of the measurement.
                celDY = targetAs3.y - selfAs3.y + selfScY;
            }

            // The kiting flip (:445-455) — a reposition every ten ticks, not a flee.
            int kite = BossNecrRules.KiteDirection(celDX, celDY, _aiTCh, _aiState);
            if (kite != 0)
            {
                _aiNapr = _storona = kite;
            }

            float maxSpeed = BossNecrRules.MaxSpeedFor(_aiState, _walkSpeed, _runSpeed);

            switch (_aiState)
            {
                case BossNecrRules.NecrAiState.Stand:
                    // (:461-472) — decelerate, and face the way momentum is still carrying her.
                    float dx = _controller.VelocityPixelsPerFrame.x;
                    if (dx > 0.5f) _storona = 1;
                    if (dx < -0.5f) _storona = -1;
                    StopMovement();
                    ApplyFacing(_storona);
                    break;

                case BossNecrRules.NecrAiState.Move:
                    // (:473-554). The oracle integrates `dx += accel` toward `maxSpeed`; the port's
                    // motor owns acceleration (`accel`/`brake` are family data it reads), so commanding
                    // the target speed is the same behaviour with the ramp left where it belongs.
                    ApplyFacing(_aiNapr);
                    MoveHorizontal(_aiNapr * maxSpeed);

                    if (jumpMagnitude > 0f)
                    {
                        DoJump(jumpMagnitude);
                    }

                    break;

                case BossNecrRules.NecrAiState.Blast:
                    // (:555-564) — root, face the player, fire through the window.
                    StopMovement();
                    _aiNapr = _storona = celDX > 0f ? 1 : -1;
                    ApplyFacing(_aiNapr);

                    if (_aiTCh < BossNecrRules.BlastFireThreshold)
                    {
                        FireBlast();
                    }

                    break;
            }
        }

        /// <summary>
        /// The attack-timer block (<c>:362-384</c>) — one blast-or-summon beat, then the timer reset.
        /// </summary>
        void TickAttackBeat()
        {
            int nextAtkN;
            BossNecrRules.AttackBeat beat = BossNecrRules.NextAttack(
                _attackIndex,
                _isShadow,
                _blackboard.TargetUnit != null,
                out nextAtkN);
            _attackIndex = nextAtkN;

            switch (beat)
            {
                case BossNecrRules.AttackBeat.Blast:
                    EnterBlast();
                    break;

                case BossNecrRules.AttackBeat.Summon:
                    // `spawn(atk_n % (phase == 1 ? 6 : 8))` (:382).
                    SummonWave(nextAtkN);
                    break;
            }

            _attackTicks = BossNecrRules.AttackTimer(UnityEngine.Random.value);
        }

        /// <summary>
        /// <c>spawn(param1)</c> (<c>UnitBossNecr.as:629-683</c>) — three bodies at the room's
        /// <c>enspawn</c> markers, or nothing at all.
        /// </summary>
        /// <remarks>
        /// <para><b>Three, not one.</b> The loop is <c>while(_loc2_ &lt; 3)</c> and each pass spawns the
        /// <i>same</i> unit at the <i>next</i> marker, so one beat puts a trio on the field. A port that
        /// spawned one would make the fight a third as crowded and the cap unreachable in practice.</para>
        ///
        /// <para><b>An even <c>atk_n</c> spawns nothing, and that is not a bug.</b> Only
        /// <c>atk_n % 6</c> ∈ {1, 3, 5} (or <c>% 8</c> ∈ {1, 3, 5, 7} in phase 2) selects a unit; the
        /// chain's last arm is <c>if(param1 != 7) return;</c>, so 0/2/4 fall out before the loop. Since
        /// the blast beat increments <c>atk_n</c> twice, the practical cadence is "every other attack
        /// beat summons" — which is exactly what the doubled increment is for.</para>
        ///
        /// <para><b>The two <c>kol_emit</c> checks are both real.</b> One guards the whole call, the
        /// other sits inside the loop after each successful spawn, so a call that starts with five live
        /// summons places <i>one</i> and then stops rather than three.</para>
        /// </remarks>
        void SummonWave(int atkN)
        {
            if (_controller == null || !_controller.IsAlive) return;

            // `if(kolChild >= kol_emit) return;` (:633-636).
            if (_kolChild >= BossNecrRules.MaxSummons) return;

            string unitId = BossNecrRules.SummonUnitFor(atkN, _phaseIndex);
            if (string.IsNullOrEmpty(unitId)) return;

            RoomInstance room = _controller.Room;
            if (room == null) return;

            float hpMult = BossNecrRules.SummonHpMult(atkN, _phaseIndex);
            float attackScale = BossNecrRules.SummonAttackScaleFor(GlobalDifficulty.Value);

            // `_loc4_.fraction = fraction` (:665) — the boss's own team, so the summons fight for her
            // rather than for whoever their row happens to name.
            FactionType? faction = _controller.Faction;

            for (int i = 0; i < BossNecrRules.SummonsPerCall; i++)
            {
                UnitController summon = BossSummonSpawner.Summon(
                    room, unitId, hpMult, i, faction, attackScale);

                if (summon != null)
                {
                    _summons.Add(summon);

                    // `--this.mother.kolChild` (Unit.as:4386) — the count is of the living.
                    summon.Died += OnSummonDied;
                    _kolChild++;
                }

                // The in-loop guard (:677-680): stop as soon as the cap is reached, even mid-trio.
                if (_kolChild >= BossNecrRules.MaxSummons) return;
            }
        }

        /// <summary>
        /// A summon died — <c>Unit.as:4386</c>'s <c>--this.mother.kolChild</c>.
        /// </summary>
        /// <remarks>
        /// <b>Unsubscribed on the way out.</b> <c>Died</c> is an event on the child's controller, and
        /// nothing else drops the subscription when the child is destroyed — a room teardown destroys
        /// the summon without a death, so the brain would keep a dangling handler on a destroyed
        /// component for as long as it lives.
        /// </remarks>
        void OnSummonDied(UnitController summon)
        {
            if (summon != null)
            {
                summon.Died -= OnSummonDied;
                _summons.Remove(summon);
            }

            if (_kolChild > 0) _kolChild--;
        }

        /// <summary>
        /// <c>die()</c>'s phase-2 arm (<c>:585-595</c>) — every unit whose <c>mother</c> is her dies with
        /// her. Called from <c>BossNecrController.OnFinalDeath</c>, i.e. only on the death that sticks,
        /// so the phase-1 revive leaves the trio standing.
        /// </summary>
        public void KillSummons()
        {
            // Snapshot first: each death raises `Died`, which mutates `_summons` through OnSummonDied.
            UnitController[] snapshot = _summons.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                UnitController summon = snapshot[i];
                if (summon != null && summon.IsAlive)
                {
                    // `_loc2_.die()` (:591) is a death, not a damage event, and the port has no public
                    // kill: `RaiseDeath` is protected so that no caller can skip the authority gate.
                    // Overkill damage through the normal path is the honest equivalent — it reaches the
                    // same `OnDeath`, drops the same loot and fires the same `Died`, which is what
                    // `--this.mother.kolChild` rides on (Unit.as:4386). The one difference is the
                    // corpse's overkill accumulator, which belongs to a body that is being removed.
                    summon.TakeDamage(summon.MaxHealth + 1f);
                }
            }

            _summons.Clear();
            _kolChild = 0;
        }

        /// <summary>
        /// <c>aiState = 2; aiTCh = 140; newPart("necrblast3"); dx *= 0.5; walk = 0; resetProtect();</c>
        /// (<c>:368-373</c>).
        /// </summary>
        /// <remarks>
        /// <b><c>resetProtect()</c> here is not tidying.</b> Entering the blast drops the shadow form, so
        /// the burst is a window in which she is genuinely vulnerable — that is the fight's opening. A
        /// port that kept the form up would make the blast state both invulnerable and damaging.
        /// </remarks>
        void EnterBlast()
        {
            _aiState = BossNecrRules.NecrAiState.Blast;
            _aiTCh = BossNecrRules.BlastStateTimer;

            // `newPart("necrblast3")` → `Emitter.emit(id, loc, X, Y - scY/2)` (Unit.as:2961-2967): her
            // chest, not her feet.
            float scY = _controller.SpriteSizePixels.y;
            if (scY <= 0f && _controller.Definition != null)
            {
                scY = _controller.Definition.Height * TileQueryConstants.UnitToPixel;
            }

            Vector2 feet = (Vector2)_controller.WorldPosition * TileQueryConstants.UnitToPixel;
            _controller.EmitParticle("necrblast3", feet + new Vector2(0f, scY * 0.5f));

            // `dx *= 0.5` then `walk = 0`.
            MoveHorizontal(_controller.VelocityPixelsPerFrame.x * 0.5f);

            ResetProtect();
        }

        /// <summary><c>currentWeapon.attack()</c> in state 2 (<c>:562</c>).</summary>
        void FireBlast()
        {
            ExecuteAttackAction();
        }

        /// <summary>
        /// <c>jump(param1)</c> (<c>:263-280</c>): <c>dy = -jumpdy * param1</c> and a sideways nudge.
        /// </summary>
        /// <param name="magnitude">
        /// The oracle's <c>_loc2_</c>. It is <b>1</b> from the timer's jump draw (<c>:421</c>), and the
        /// Move arm may then *override* it to <c>0.4</c> when a shoulder probe finds a wall ahead
        /// (<c>:522-525</c>) or <c>0.6</c> once she has climbed 35 px above her spawn
        /// (<c>:526-529</c>).
        /// </param>
        /// <remarks>
        /// <b>Only the 1.0 case is reachable here.</b> Both overrides need data the port does not expose
        /// to a brain — the <c>shX1</c>/<c>shX2</c> shoulder probes and the spawn <c>begY</c> — so the
        /// magnitude stays 1 rather than being guessed at 0.4, which would make every jump a quarter as
        /// high as the oracle's.
        /// </remarks>
        void DoJump(float magnitude)
        {
            if (!_controller.IsGrounded) return;

            // Positive-up: the port's JumpVertical takes an "up" value while AS3's `dy = -jumpdy` is
            // positive-down. Carrying the minus sign across drives her into the floor.
            float jumpForce = _controller.Definition != null ? _controller.Definition.JumpForce : 15f;
            JumpVertical(jumpForce * magnitude);
        }

        /// <summary>
        /// The <c>aiTCh == 0</c> block (<c>:392-431</c>), with the transition rules in
        /// <see cref="BossNecrRules.OnStateTimerExpiry"/>.
        /// </summary>
        /// <returns>
        /// The jump magnitude for this tick's movement arm — <c>_loc2_</c>, which is <b>1</b> when the
        /// jump draw passes and 0 otherwise. Returned rather than stored because the oracle's
        /// <c>_loc2_</c> is a local of <c>control()</c> that does not survive the tick.
        /// </returns>
        float TickStateTimerExpiry()
        {
            float celDX = 0f;
            if (_celTarget != null)
            {
                float selfScY;
                Vector2 selfAs3 = As3Position(_controller, out selfScY);
                float targetScY;
                Vector2 targetAs3 = As3Position(_celTarget, out targetScY);
                celDX = targetAs3.x - selfAs3.x;
            }

            BossNecrRules.StateTransition transition = BossNecrRules.OnStateTimerExpiry(
                _aiState, celDX, _aiNapr, () => UnityEngine.Random.value);

            // `if(aiState == 3) { aiState = 0; castProtect(); }` (:394-398) — the cast happens before the
            // new state's movement arm, which is why the transition reports it separately.
            if (transition.CastProtect)
            {
                CastProtect();
            }

            _aiState = transition.State;
            _aiNapr = _storona = transition.Direction;

            if (transition.Throw) _throuTicks = BossNecrRules.ThrowTicks;

            _aiTCh = BossNecrRules.StateTimer(UnityEngine.Random.value);

            return transition.Jump ? 1f : 0f;
        }

        /// <summary>
        /// <c>if(loc.gg.pet &amp;&amp; loc.gg.pet.sost == 1 &amp;&amp; isrnd(0.4)) setCel(pet) else
        /// setCel(gg)</c> (<c>:432-441</c>).
        /// </summary>
        /// <remarks>
        /// The pet arm is not modelled — the port has no pet registry, so <c>_celTarget</c> is always the
        /// player. The draw is therefore <b>not</b> spent, which is correct for the current port and a
        /// divergence to revisit the moment pets exist.
        /// </remarks>
        UnitController PickCelTarget(UnitController player)
        {
            return player;
        }

        /// <summary>
        /// <c>damage()</c>'s side effects (<c>:220-235</c>) — the reactive half of the fight.
        /// </summary>
        /// <remarks>
        /// <b>Taking a hit is what raises a defence.</b> <c>if(protculd_t &lt;= 0 &amp;&amp; dealt &gt; 0
        /// &amp;&amp; aiState != 3 &amp;&amp; aiState != 2) { aiState = 3; aiTCh = 30; }</c>. The
        /// <c>dealt &gt; 0</c> term is what stops the shadow form re-triggering itself: an absorbed hit
        /// returns 0, so no new cast state is entered while she is already immune.
        /// </remarks>
        public override void OnDamaged(float amount, Vector2 damageSourcePx)
        {
            // The base guards its alarm logic on `_currentState == Dead`. This brain does not run the
            // EnemyAIState machine, so that field never reaches Dead — the liveness check stands in for
            // it, and must come *before* the base call rather than after.
            if (_controller == null || !_controller.IsAlive) return;

            base.OnDamaged(amount, damageSourcePx);

            if (_aiState == BossNecrRules.NecrAiState.Stand)
            {
                _aiState = BossNecrRules.NecrAiState.Move;
            }

            if (_protCooldownTicks <= 0
                && amount > 0f
                && _aiState != BossNecrRules.NecrAiState.Protect
                && _aiState != BossNecrRules.NecrAiState.Blast)
            {
                _aiState = BossNecrRules.NecrAiState.Protect;
                _aiTCh = BossNecrRules.ProtectCastTimer;
            }

            BossNecrRules.ShaveTimersForDamage(amount, ref _curseCooldownTicks, ref _attackTicks);
        }

        /// <summary>
        /// <c>castProtect()</c> (<c>:598-627</c>) — the three forms, with the effects the port used to
        /// declare and never apply.
        /// </summary>
        /// <remarks>
        /// <b>This is the defect the report is about.</b> The shipped brain set
        /// <c>_protectDurationTicks = 250</c> and then did nothing with it for two of the three forms:
        /// no invulnerability, no invisibility. A player could hit her straight through a shadow form
        /// that the wiki describes as immune, and the fight had none of its rhythm.
        /// </remarks>
        protected virtual void CastProtect()
        {
            if (_controller == null || !_controller.IsAlive) return;

            _protCooldownTicks = BossNecrRules.ProtectCooldown;
            ResetProtect();

            BossNecrRules.ProtectForm form = BossNecrRules.FormFor(_protN);

            switch (form)
            {
                case BossNecrRules.ProtectForm.Heal:
                    // `heal(this.healHp)`, healHp = maxhp / 10 (:103, :603).
                    if (_controller.UnitStats != null)
                    {
                        _controller.UnitStats.Heal(BossNecrRules.HealAmount(_maxHp));
                    }
                    break;

                case BossNecrRules.ProtectForm.Shadow:
                    // `isShadow = invulner = transp = true; levitPoss = false; prot_t = timeProtect;
                    //  atk_t = 5;` (:604-612).
                    _isShadow = true;
                    _controller.RuntimeInvulnerable = true;
                    _protTicks = BossNecrRules.ProtectDuration;

                    // `atk_t = 5` — the attack beat is pulled forward to the moment the shadow drops,
                    // so she comes out of the defence already swinging rather than after a full timer.
                    _attackTicks = 5;
                    break;

                default:
                    // `superInvis = true; isVis = false; curA = celA = 0; prot_t = timeProtect;`
                    // (:613-619).
                    _superInvis = true;
                    SetVisualVisible(false);
                    _protTicks = BossNecrRules.ProtectDuration;
                    break;
            }

            _protN = BossNecrRules.NextProtN(_protN);
        }

        /// <summary><c>resetProtect()</c> (<c>:718-724</c>) — drop every form at once.</summary>
        protected virtual void ResetProtect()
        {
            _isShadow = false;
            _superInvis = false;
            _protTicks = 0;

            // The two effects that live outside the brain. `RuntimeInvulnerable` is the same bit AS3
            // raises on `invulner`; nothing else writes it for an enemy, so clearing it here cannot
            // stomp a lock some other system owns.
            if (_controller != null)
            {
                _controller.RuntimeInvulnerable = false;
            }

            SetVisualVisible(true);
        }

        /// <summary>
        /// Show or hide the unit's art — AS3 <c>vis.visible</c> / <c>isVis</c>.
        ///
        /// <para>Hides the <i>sprite</i>, not the unit: the collider, the brain and the HP bar all stay
        /// live, which is exactly what <c>superInvis</c> means — hard to see, still damageable.</para>
        /// </summary>
        void SetVisualVisible(bool visible)
        {
            if (_animator != null)
            {
                _animator.SetVisible(visible);
            }
        }

        /// <summary>
        /// The curse circles currently in flight. AS3 keeps them on the location's object list
        /// (<c>loc.addObj</c>); the port keeps them here because nothing else in the port consumes a
        /// <c>MagSymbol</c> and a scene object per circle would buy a lifecycle for no behaviour.
        /// </summary>
        readonly List<MagSymbol> _symbols = new List<MagSymbol>();

        /// <summary>The player, resolved once and re-resolved if it dies or the room changes.</summary>
        UnitController _player;

        /// <summary>
        /// The room's origin in world pixels — room-local space is <c>worldPx - this</c>.
        /// </summary>
        /// <remarks>
        /// <b>Only a room at land (0,0) has a zero origin.</b> Every curse position is clamped into
        /// <c>[100, lim - 100]</c> of <i>room</i> pixels, so treating world pixels as room pixels puts
        /// that clamp in the wrong place in every other room — and <c>bossnecr1</c> is not at (0,0).
        /// </remarks>
        Vector2 RoomOriginPixels =>
            _controller != null && _controller.Room != null
                ? WorldCoordinates.RoomOriginPixel(_controller.Room)
                : Vector2.zero;

        /// <summary>The room's height in pixels — <c>loc.limY</c>, and the axis of the AS3 Y flip.</summary>
        float RoomHeightPixels =>
            _controller != null && _controller.Room != null
                ? _controller.Room.height * WorldConstants.TILE_SIZE
                : 0f;

        /// <summary>
        /// A unit's <c>X</c>/<c>Y</c> in AS3 room-local pixels — its feet, <b>Y down</b> — plus its
        /// <c>scY</c>.
        /// </summary>
        Vector2 As3Position(UnitController unit, out float scY)
        {
            Vector2 local = (Vector2)unit.WorldPosition * TileQueryConstants.UnitToPixel
                            - RoomOriginPixels;

            scY = unit.SpriteSizePixels.y;
            if (scY <= 0f)
            {
                // Before the sprite import has run there is no measured height. The definition's own
                // figure is the number the importer would have produced from it, so this is the same
                // value one frame later rather than a different guess.
                scY = unit.Definition != null
                    ? unit.Definition.Height * TileQueryConstants.UnitToPixel
                    : 70f;
            }

            return new Vector2(local.x, ZombieSuperMath.PortYToAs3Y(local.y, RoomHeightPixels));
        }

        /// <summary>AS3 room-local pixels back to this port's world pixels, for the particle emit.</summary>
        Vector2 As3ToWorldPixels(Vector2 as3)
        {
            Vector2 origin = RoomOriginPixels;
            return new Vector2(
                as3.x + origin.x,
                ZombieSuperMath.As3YToPortY(as3.y, RoomHeightPixels) + origin.y);
        }

        /// <summary>
        /// The curse beat — <c>control()</c>'s cooldown and three-arm draw
        /// (<c>UnitBossNecr.as:321-341</c>).
        /// </summary>
        /// <remarks>
        /// <b>This used to be a direct damage hit.</b> The shipped brain reported
        /// <see cref="PendingDamage"/> straight to the player every cooldown, at any range and through
        /// walls — because the port had no way to place a curse <i>in the world</i>. It does now:
        /// <see cref="MagSymbol"/> is the oracle's own delivery, and the effect lands only if the player
        /// is still inside the circle when it resolves.
        /// </remarks>
        void TickCurse()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_curseCooldownTicks > 0)
            {
                _curseCooldownTicks--;
                return;
            }

            _curseCooldownTicks = BossNecrRules.CurseTimer(UnityEngine.Random.value);

            UnitController player = ResolvePlayer();
            if (player == null) return;

            // `castCurse` measures from the player's feet (`gg.X`, `gg.Y`) and aims at the body's centre
            // (`gg.Y - gg.scY/2`), 15 ticks ahead of its current velocity.
            float scY;
            Vector2 playerAs3 = As3Position(player, out scY);
            Vector2 velocity = player.VelocityPixelsPerFrame;

            // AS3's `dy` is down-positive and the motor's is up-positive; only Y is flipped.
            Vector2 delta = new Vector2(velocity.x, -velocity.y);

            float limX = 0f;
            float limY = 0f;
            RoomInstance room = _controller.Room;
            if (room != null)
            {
                limX = room.width * WorldConstants.TILE_SIZE;
                limY = room.height * WorldConstants.TILE_SIZE;
            }

            CurseBeat beat = MagSymbolRules.ChooseBeat(_phaseIndex, () => UnityEngine.Random.value);
            int[] spacings = MagSymbolRules.SpacingsFor(beat);

            for (int i = 0; i < spacings.Length; i++)
            {
                int spacing = spacings[i];

                Vector2 at;
                switch (beat)
                {
                    case CurseBeat.Line:
                        at = MagSymbolRules.LinePosition(playerAs3, scY, delta, spacing);
                        break;
                    case CurseBeat.Spread:
                        at = MagSymbolRules.SpreadPosition(
                            playerAs3, scY, delta,
                            UnityEngine.Random.value, UnityEngine.Random.value,
                            UnityEngine.Random.value, UnityEngine.Random.value);
                        break;
                    default:
                        at = MagSymbolRules.SinglePosition(
                            playerAs3, scY, delta,
                            UnityEngine.Random.value, UnityEngine.Random.value);
                        break;
                }

                if (limX > 0f && limY > 0f)
                {
                    at = MagSymbolRules.Clamp(at, limX, limY);
                }

                // Each circle draws its own curse, as `castCurse` does — so a five-circle line can mix
                // two different debuffs.
                string spell = BossNecrRules.CurseFor(_phaseIndex, UnityEngine.Random.value);
                _symbols.Add(new MagSymbol(at, spell, spacing));
            }
        }

        /// <summary>
        /// <c>MagSymbol.step()</c> for every circle in flight, then <c>spellCast()</c> for the ones whose
        /// life ran out this tick.
        /// </summary>
        void TickSymbols()
        {
            if (_symbols.Count == 0) return;

            for (int i = _symbols.Count - 1; i >= 0; i--)
            {
                MagSymbol symbol = _symbols[i];
                MagSymbolStep step = symbol.Step();

                if ((step & MagSymbolStep.Emitted) != 0)
                {
                    _controller?.EmitParticle("magsymbol",
                        As3ToWorldPixels(symbol.PositionAs3Pixels));
                }

                if ((step & MagSymbolStep.Applied) != 0)
                {
                    ApplySymbol(symbol);
                }

                if ((step & MagSymbolStep.Expired) != 0)
                {
                    _symbols.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// <c>MagSymbol.spellCast()</c> — the curse lands only on a player who is still inside it.
        /// </summary>
        /// <remarks>
        /// The oracle's guard is <c>gg.loc == loc &amp;&amp; !gg.invulner &amp;&amp; gg.sost &lt;= 2</c>
        /// followed by a squared-distance test. The room half is implicit here (the boss and its list die
        /// with the room), and <c>sost &lt;= 2</c> is <see cref="UnitController.IsAlive"/>.
        ///
        /// <para><b>The distance is not feet-to-circle.</b> <c>getRasst2</c> measures to a point three
        /// quarters up the player's body (<c>Obj.as:206</c>) while the circle was <i>placed</i> at the
        /// body's centre — see <see cref="MagSymbolRules"/>. Both halves have to be in AS3 room-local
        /// pixels for that to mean anything, which is why the player is re-projected here rather than
        /// reusing a world-pixel position.</para>
        /// </remarks>
        void ApplySymbol(MagSymbol symbol)
        {
            UnitController player = ResolvePlayer();
            if (player == null || !player.IsAlive || player.IsInvulnerable) return;

            // `gg.loc == loc`. The circle's position is room-local, so measuring it against a player
            // standing in a different room would compare two unrelated origins — and could "hit".
            if (player.Room != _controller.Room) return;

            float scY;
            Vector2 playerAs3 = As3Position(player, out scY);
            if (!MagSymbolRules.Hits(playerAs3, scY, symbol.PositionAs3Pixels)) return;

            player.UnitStats?.Effects.AddEffect(symbol.SpellId);
        }

        /// <summary>
        /// The player, for a system that has no target list of its own.
        /// </summary>
        /// <remarks>
        /// <c>World.w.gg</c> is a global in AS3; the port has no player registry, and the one place that
        /// needed to enumerate units — <c>EnemyBrain</c>'s candidate sweep — does exactly this
        /// (<c>FindObjectsByType&lt;UnitController&gt;</c>). Resolved once and cached, because a curse
        /// beat would otherwise walk the whole scene several times per circle.
        /// </remarks>
        UnitController ResolvePlayer()
        {
            if (_player != null && _player.IsAlive) return _player;

            _player = null;
            var units = FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            for (int i = 0; i < units.Length; i++)
            {
                if (units[i] != null && units[i].IsPlayer)
                {
                    _player = units[i];
                    break;
                }
            }

            return _player;
        }

        public override bool TryReviveSecondLife()
        {
            // UnitBossNecr.as:567-584
            if (_phaseIndex == 1)
            {
                ResetProtect();
                SetPhase(2);
                _isSecondLifeActive = true;

                // `timeCurseCuld = 220` (:580) — phase 2 curses come faster.
                _curseCooldownTicks = BossNecrRules.PhaseTwoCurseCooldown;
                return true;
            }

            return false;
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 500f;
        }

        /// <summary>
        /// <c>currentWeapon.attack()</c> in state 2 (<c>:520-524</c>) — she fires <c>necrbullet</c>,
        /// which the constructor hardcodes (<c>:77</c>) and the unit node does not list.
        /// </summary>
        /// <remarks>
        /// <b>This used to be a direct damage poke.</b> The shipped brain reported damage straight to
        /// the player at any range inside 500 px — through walls, with no projectile to dodge and no
        /// line of sight. The oracle fires a real bullet, so the fight has something to read and dodge.
        /// The direct path stays as the fallback for the case where the weapon mount is missing, so a
        /// brainless-armed spawn still does something rather than standing silent.
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            // A shadow form cannot deal damage either (`isShadow` gates `attack()`).
            if (_isShadow) return;

            EnemyController enemy = _controller as EnemyController;
            EnemyWeaponMount mount = enemy != null ? enemy.GetWeaponMount(BossNecrController.WeaponId) : null;
            if (mount != null && mount.IsArmed)
            {
                // `setCel(loc.gg)`'s point, not the target's feet: half a body up and a quarter body
                // along her facing (`Unit.as:4707-4708`). At the feet the blast lands in the floor tile
                // she stands on and the boss reads as unable to aim.
                mount.SetAimTarget(EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit));
                mount.FireOnce();
                return;
            }

            // The fallback is rate-limited. `attack()` is called every tick through a 120-tick blast
            // window, which a weapon mount handles by refusing to fire faster than its own cooldown —
            // but this path has no cooldown of its own, so without this gate a mountless spawn would
            // deal direct damage on all 120 ticks.
            if (_fallbackCooldownTicks > 0) return;
            _fallbackCooldownTicks = 30;

            float dmg = _controller.Definition != null ? _controller.Definition.damage : 30f;
            if (_phaseIndex == 2) dmg *= 1.3f; // Stronger in ghost phase

            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.Acid),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "die";
            return "stay";
        }
    }
}

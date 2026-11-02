using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The Manehattan alicorn boss — a line-by-line port of AS3 <c>UnitBossAlicorn.control()</c>
    /// (<c>fe/unit/UnitBossAlicorn.as:349-566</c>) plus <c>attack()</c> (<c>:568-589</c>),
    /// <c>teleport()</c> (<c>:309-337</c>), <c>die()</c> (<c>:703-738</c>) and <c>animate()</c>
    /// (<c>:216-274</c>).
    ///
    /// <para><b>What this replaced, and why it had to be replaced rather than tuned.</b> The previous
    /// class was an invention wearing the oracle's name: it teleported <i>next to the player</i> on a
    /// 180-tick cooldown, rolled <c>attState</c> over <c>0..5</c> on every teleport, dealt damage
    /// directly instead of firing one of four weapons, and animated <c>"fly"</c> forever. None of those
    /// four behaviours is in <c>UnitBossAlicorn</c>. The real fight is:</para>
    ///
    /// <list type="number">
    ///   <item><description><b>A timer rotation, not a perception ladder.</b> <c>aiState</c> cycles
    ///     1→2→3→1 on <c>aiTCh</c>, and only step 3 attacks. The boss does not chase, does not patrol,
    ///     and does not react to being shot at.</description></item>
    ///   <item><description><b>Seven teleport points, never the one it is standing on.</b> Step 1 picks
    ///     an <c>attState</c> and a move point, teleports there, and re-reads whether it is airborne
    ///     from the tile below its feet.</description></item>
    ///   <item><description><b>One of four weapons per beat</b> — <c>alilight2</c>, <c>aliblade</c>,
    ///     <c>alipsy2</c>, and on hard difficulty <c>alimray</c> — or a telekinetic grab-and-throw, or
    ///     invisibility with scythes.</description></item>
    ///   <item><description><b>A one-shot dome.</b> The first lethal hit does not kill: it shatters the
    ///     dome and leaves the boss at <c>maxhp - 0.1</c>.</description></item>
    /// </list>
    ///
    /// <para><b>Why <c>SimTick</c> does not call <c>base</c>.</b> <see cref="EnemyBrain.SimTick"/> is a
    /// perception-driven state machine — Idle/Patrol/Alert/CombatChase — and this boss has no such
    /// ladder: its <c>aiState</c> is a <i>counter</i>, and its <c>aiState == 1</c> means "teleport and
    /// pick an attack", not "heard something". Running the base machine and overriding
    /// <c>TickCombatChase</c> (what the previous class did) left the boss's own ladder fighting the
    /// base's transitions. The perception half is still run — <c>UpdatePerception</c> — because the aim
    /// gate needs a target and the contact attack needs one too.</para>
    ///
    /// <para><b>All the arithmetic is in <see cref="BossAlicornRules"/> and
    /// <see cref="BossAlicornTelekinesis"/></b>, both Unity-free where they can be, and pinned by
    /// offline fixtures. This class is the plumbing: which timer changes when, and what gets written
    /// where.</para>
    /// </summary>
    public class BossAlicornBrain : BossBrain
    {
        // ── the four weapons, in the oracle's order (UnitBossAlicorn.as:128) ──────────────────────
        //
        // `weaps = [alilight2, aliblade, alipsy2, alimray]`, and `attState` indexes it directly for
        // 0..2, with 5 meaning `weaps[3]`. The ids are NOT read from the unit asset: the asset's
        // `weapons:` list is a roll table for the importer, and this boss's loadout is a constructor
        // literal that no data carries.
        public static readonly string[] WeaponIds = { "alilight2", "aliblade", "alipsy2", "alimray" };

        /// <summary>The index into <see cref="WeaponIds"/> for the beam (<c>attState == 5</c>).</summary>
        const int BeamWeaponIndex = 3;

        [Header("Boss Alicorn (UnitBossAlicorn)")]
        [SerializeField] bool _isShit = BossAlicornRules.ShieldOnRespawn;

        /// <summary>
        /// The dome this boss draws, resolved on first use — see <see cref="BreakDome"/> for why it
        /// cannot be cached at construction.
        /// </summary>
        UnitShieldOverlay _dome;

        /// <summary>
        /// AS3 <c>attState</c> (<c>:101</c>) — the attack-pattern selector for the current beat. Public
        /// so the debug overlay and the fixtures can read it; the oracle has no writer outside
        /// <c>control()</c>.
        /// </summary>
        [SerializeField] int _attState;

        /// <summary>AS3 <c>aiState</c> (<c>:101</c>) — the rotation step, 0..3.</summary>
        [SerializeField] int _aiState;

        /// <summary>AS3 <c>aiTCh</c> (<c>:118</c> initial 80) — ticks until the next rotation step.</summary>
        [SerializeField] int _aiTCh = BossAlicornRules.InitialAiTCh;

        /// <summary>AS3 <c>mp</c> (<c>:95</c>) — the move point the boss is standing on.</summary>
        [SerializeField] int _movePoint = BossAlicornRules.InitialMovePoint;

        /// <summary>AS3 <c>t_turn</c> (<c>:103</c>) — ticks left before the facing flips.</summary>
        int _tTurn = BossAlicornRules.TurnTicks;

        /// <summary>AS3 <c>t_fly</c> (<c>:107</c>) — grounded ticks before lifting off.</summary>
        int _tFly = BossAlicornRules.FlyTicks;

        /// <summary>AS3 <c>t_shit</c> (<c>:105</c>) — decremented and never read. See the rules class.</summary>
        int _tShit = BossAlicornRules.InitialShieldTimer;

        /// <summary>AS3 <c>t_float</c> (<c>:40</c>) — the hover bob's phase.</summary>
        float _tFloat;

        /// <summary>AS3 <c>superInvis</c> (<c>:62</c>) — the invisibility step is active.</summary>
        bool _superInvis;

        /// <summary>AS3 <c>isBlast</c> (<c>:38</c>) — the death burst is emitted exactly once.</summary>
        bool _isBlast;

        /// <summary>AS3 <c>isFly</c> for this unit — see <see cref="BossAlicornController.IsFlying"/>.</summary>
        bool _isFly;

        /// <summary>The active index into <see cref="WeaponIds"/>; -1 until the first rotation step.</summary>
        int _weaponIndex = -1;

        BossAlicornTelekinesis _telekinesis;

        /// <summary>
        /// How many scythes <see cref="EmitScythe"/> has actually put into the room. Exposed so the
        /// debug overlay and the fixtures can see that <c>emit()</c> fired — the count is a
        /// <i>result</i> now, not a substitute for one.
        /// </summary>
        int _scythesEmitted;

        /// <summary>
        /// AS3 has no NPC level in this port (see <see cref="BossAlicornRules.BladeLevelScale"/>), so
        /// <c>emit()</c>'s <c>1 + level * 0.1</c> is the identity. Named rather than inlined so the
        /// call site reads as the oracle's expression.
        /// </summary>
        const int NpcLevel = 0;

        /// <summary>Distinct per blade, so six spawned in one tick cannot share an entity id.</summary>
        static int _scytheSerial;

        /// <summary>
        /// The live visual controller for <see cref="Room"/>, cached for the shield break's six
        /// back-to-back spawns. <c>RoomVisualController.FindForRoom</c> does a scene scan.
        /// </summary>
        RoomVisualController _scytheRoom;

        // One warning per failure cause, not one per blade: the invisibility step emits every thirty
        // ticks, so a missing definition would otherwise print four lines a beat for the whole fight.
        static bool _scytheRoomReported;
        static bool _scytheDefinitionReported;
        static bool _scytheControllerReported;
        static bool _scytheSpawnReported;

        public bool IsShit => _isShit;
        public int AttState => _attState;
        public int AiState => _aiState;
        public int AiTCh => _aiTCh;
        public int MovePoint => _movePoint;
        public int ScythesEmitted => _scythesEmitted;
        public bool IsSuperInvisible => _superInvis;
        public bool IsFlying => _isFly;
        public int WeaponIndex => _weaponIndex;
        public string CurrentWeaponId => _weaponIndex >= 0 ? WeaponIds[_weaponIndex] : null;

        public UnitController SelfController => _controller;
        public ITileQueryService TileQueryService => _tileQuery;
        public RoomInstance Room => _controller != null ? _controller.Room : null;

        /// <summary>
        /// AS3 <c>t_shit</c> — kept public because the oracle's own field is, and because a reader
        /// looking for "the shield timer" should find that it exists and is dead rather than missing.
        /// </summary>
        public int ShieldTimerTicks => _tShit;

        /// <summary>
        /// Unused, and deliberately so — <c>UnitBossAlicorn</c> has no <c>EnemyBrain</c> Attack state.
        /// </summary>
        /// <remarks>
        /// <see cref="EnemyBrain"/> declares this abstract because the port's family brains all reach
        /// their attack through the shared <c>Attack</c> state. This boss does not: its attack is
        /// <c>attack()</c> called from inside step 3 of its own rotation (<c>:568-589</c>), and
        /// <see cref="SimTick"/> never enters the base state machine, so
        /// <c>TryEnterAttackState</c> is never reached either. Implementing it as an empty body is
        /// therefore honest rather than a stub — there is no path that could call it. Putting the real
        /// attack here would give the boss a <i>second</i> firing path on top of the rotation.
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
        }

        /// <summary>
        /// <c>animate():230</c> — the boss never stops to attack: its whole attack is ranged and its
        /// locomotion is teleportation. The base's Attack state does not exist for it.
        /// </summary>
        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            _telekinesis = new BossAlicornTelekinesis(this);
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyTuning();
        }

        /// <summary>
        /// AS3's constructor writes <c>walkSpeed = plavSpeed = maxSpeed</c> (<c>:115-116</c>) and never
        /// touches them again — the boss has no run and no patrol. The base's three speed fields are
        /// therefore all the same number, and the base's movement code never runs anyway.
        /// </summary>
        protected virtual void ApplyTuning()
        {
            if (_controller == null || _controller.Definition == null) return;

            float speed = _controller.Definition.moveSpeed > 0 ? _controller.Definition.moveSpeed : 3f;
            _patrolSpeed = speed;
            _alertSpeed = speed;
            _chaseSpeed = speed;
        }

        // ── the tick ─────────────────────────────────────────────────────────────────────────────

        public override void SimTick(int tickIndex)
        {
            if (_controller == null)
            {
                return;
            }

            // `if(sost == 2) { dx = 0; dy = 0; return; }` (:356-361) — the death sequence. The brain
            // still draws, because AS3's `animate()` runs after `control()` and selects `die` from
            // `sost`, not from the return.
            if (!_controller.IsAlive)
            {
                TickDeathAnimation();
                UpdateAnimation();
                return;
            }

            // `if(World.w.enemyAct <= 0) { … return; }` (:368-373) — the global freeze. The oracle
            // still runs physics and drawing from this branch; only the decision half stops.
            if (EnemyActivity.IsFrozen)
            {
                UpdateAnimation();
                TickWeapons();
                return;
            }

            UpdatePerception(tickIndex);

            TickControl();

            // BossBrain's own tail. Neither an arena rect nor a quarter-HP event is authored for this
            // boss, so both are no-ops today — but they are the hooks a scripted encounter plugs into,
            // and skipping them would silently disable them the moment one is authored.
            CheckQuarterHpThresholds();

            UpdateAnimation();
            TickWeapons();
            ClampToArenaBounds();
        }

        /// <summary>
        /// AS3 <c>control():362-565</c>, in the oracle's order. Every line below has its source line in
        /// the comment; the ones without are the port's substitutions and say so.
        /// </summary>
        void TickControl()
        {
            // `if(this.t_shit > 0) { --this.t_shit; }` (:374-376). Decremented, never read — the
            // shield timer is vestigial. Modelled so that a reader comparing the two files sees the
            // same field rather than a hole.
            if (_tShit > 0) _tShit--;

            // `if(!stay && !isFly) { --t_fly; if(<=0) isFly = true; } else t_fly = 3;` (:378-389).
            bool flying;
            _tFly = BossAlicornRules.TickFlightTimer(_tFly, _controller.IsGrounded, _isFly, out flying);
            _isFly = flying;

            // `invis = superInvis;` (:390) — the base flag the oracle's `Unit.invis` mirrors. The port
            // renders through SetVisible instead; see EnterInvisibility.
            ApplyInvisibilityVisual();

            // `if(aiTCh > 0) --aiTCh; else { …the rotation… }` (:391-477).
            if (_aiTCh > 0)
            {
                _aiTCh--;
            }
            else
            {
                AdvanceRotation();
            }

            // `if(…aiTCh % 10 == 1…) setCel(loc.gg);` (:478-488).
            if (BossAlicornRules.ShouldAim(_aiState, _attState, _aiTCh))
            {
                Aim();
            }

            // `if(isFly) { …t_float… }` (:489-498) — the boss's only locomotion.
            if (_isFly)
            {
                ApplyFloatBob();
            }

            // `if(aiState == 0) { … } if(aiState > 0) { …turn… }` (:503-531).
            TickFacing();

            // `this.attack();` (:532).
            Attack();

            // The hold block (:533-565) — the tail of `control()`, after the attack.
            _telekinesis.TickHold();
        }

        // ── the rotation (:391-477) ──────────────────────────────────────────────────────────────

        /// <summary>
        /// One expiry of <c>aiTCh</c> — the oracle's three-arm ladder.
        /// </summary>
        void AdvanceRotation()
        {
            _aiState = BossAlicornRules.NextAiState(_aiState);

            if (_aiState == 1)
            {
                AdvanceStepOne();
            }
            else if (_aiState == 2)
            {
                AdvanceStepTwo();
            }
            else if (_aiState == 3)
            {
                AdvanceStepThree();
            }
        }

        /// <summary>
        /// Step 1 (<c>:402-440</c>): roll an attack state and a move point, teleport, then resolve what
        /// the attack state means.
        /// </summary>
        void AdvanceStepOne()
        {
            _attState = BossAlicornRules.RollAttackState(UnityEngine.Random.value);
            _movePoint = BossAlicornRules.RollMovePoint(_movePoint, UnityEngine.Random.value);

            // `teleport(movePoints[mp].x*40+20, movePoints[mp].y*40+40, attState == 4 ? 0 : 1)`
            // (:415). **The emitter flag is read BEFORE the difficulty upgrade below**, which is why
            // this call cannot be moved: an upgraded-to-5 beat still arrives with the teleport flash,
            // exactly as the oracle's expression evaluates.
            TeleportToMovePoint(_movePoint, _attState != BossAlicornRules.InvisibilityAttackState);

            _aiTCh = BossAlicornRules.AiTChAfterTeleport;

            // `if(globalDif == 4 && isrnd(0.2) || globalDif == 3 && isrnd(0.1)) attState = 5;`
            // (:417-420).
            int difficulty = GlobalDifficulty.Value;
            if (BossAlicornRules.UpgradesToBeam(difficulty, UnityEngine.Random.value))
            {
                _attState = BossAlicornRules.BeamAttackState;
            }

            // `if(attState <= 2) currentWeapon = weaps[attState];` (:421-424).
            if (_attState <= 2)
            {
                SelectWeapon(_attState);
            }

            // `if(attState == 4) { superInvis = true; isVis = false; curA = celA = 0; } else { … }`
            // (:425-435).
            if (_attState == BossAlicornRules.InvisibilityAttackState)
            {
                EnterInvisibility();
            }
            else
            {
                LeaveInvisibility();
            }

            // `if(attState == 5) currentWeapon = weaps[3];` (:436-439).
            if (_attState == BossAlicornRules.BeamAttackState)
            {
                SelectWeapon(BeamWeaponIndex);
            }
        }

        /// <summary>
        /// Step 2 (<c>:441-456</c>): the wind-up. The only side effect is the grab, whose result sets
        /// the next timer — a failed grab retries in 15 ticks instead of 45.
        /// </summary>
        void AdvanceStepTwo()
        {
            bool grabbed = false;
            if (_attState == BossAlicornRules.TelekinesisAttackState)
            {
                grabbed = _telekinesis.TryGrab();
            }

            _aiTCh = BossAlicornRules.AiTChForState2(_attState, grabbed);
        }

        /// <summary>
        /// Step 3 (<c>:457-476</c>): the release. The throw happens here, not in step 2.
        /// </summary>
        void AdvanceStepThree()
        {
            if (_attState == BossAlicornRules.TelekinesisAttackState)
            {
                _telekinesis.Throw();
            }

            _aiTCh = BossAlicornRules.AiTChForState3(_attState, UnityEngine.Random.value);
        }

        // ── the attack (:568-589) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>attack()</c> — the three things that can happen on step 3, in the oracle's order.
        /// </summary>
        void Attack()
        {
            if (_aiState != 3)
            {
                return;
            }

            // `if(attState <= 2 || attState == 5) currentWeapon.attack();` (:576-579).
            if (BossAlicornRules.ShouldFire(_aiState, _attState))
            {
                FireCurrentWeapon();
            }

            // `if(attState == 4 && aiTCh % 30 == 1) emit();` (:580-583).
            if (BossAlicornRules.ShouldEmitScythe(_aiState, _attState, _aiTCh))
            {
                EmitScythe();
            }

            // `if(attState != 4 && dist2 < 100*100 && isrnd(0.1)) attKorp(celUnit, 0.5);` (:584-587).
            //
            // **The distance gate is tested before the roll on purpose.** AS3's `&&` short-circuits,
            // so the oracle draws a random number only when something is close enough; drawing one
            // every tick here would be a different sequence even though each individual roll is fair.
            UnitController target = _blackboard.TargetUnit;
            if (_aiState == 3 &&
                _attState != BossAlicornRules.InvisibilityAttackState &&
                target != null &&
                _blackboard.TargetDistanceSq < BossAlicornRules.ContactAttackRangePixels * BossAlicornRules.ContactAttackRangePixels)
            {
                if (BossAlicornRules.ShouldContactAttack(_aiState, _attState, _blackboard.TargetDistanceSq, UnityEngine.Random.value))
                {
                    _controller.TryContactAttack(target, 0.5f);
                }
            }
        }

        /// <summary>
        /// <c>currentWeapon.attack()</c> — a <b>call</b>, not a burst: the weapon's own <c>t_attack</c>
        /// paces the rounds, so the brain asks every tick of step 3 and the mount decides.
        /// </summary>
        void FireCurrentWeapon()
        {
            if (_weaponIndex < 0) return;

            EnemyController enemy = _controller as EnemyController;
            EnemyWeaponMount mount = enemy != null ? enemy.GetWeaponMount(WeaponIds[_weaponIndex]) : null;
            mount?.FireOnce();
        }

        /// <summary>Switch <c>currentWeapon</c>. AS3 assigns the array slot; the port names the id.</summary>
        void SelectWeapon(int index)
        {
            if (index < 0 || index >= WeaponIds.Length) return;
            _weaponIndex = index;
        }

        /// <summary>
        /// <c>setCel(loc.gg)</c> (<c>:478-488</c>) — aim at the player, with the oracle's 20 % pet
        /// preference.
        /// </summary>
        /// <remarks>
        /// <b>Deviation, recorded.</b> AS3's <c>loc.gg</c> is a global handle on the player, so the
        /// boss aims at him whether or not it can see him. The port has no player global; this uses
        /// the visible target and then the last known position, the same substitution
        /// <c>AlicornBrain.TryTeleportAway</c> documents. In a locked boss room the two agree; behind a
        /// crate they do not, and the port's boss loses the aim for as long as the sighting does.
        /// </remarks>
        void Aim()
        {
            EnemyController enemy = _controller as EnemyController;
            if (enemy == null) return;

            UnitController target = _blackboard.TargetUnit;
            if (target != null)
            {
                // Unity world units — the space EnemyWeaponMount.SetAimTarget works in. The point is
                // `setCel`'s: a quarter body along the target's facing and half a body up
                // (`Unit.as:4707-4708`). `transform.position` is her FEET, and a bolt fired from a
                // hovering boss at that point lands in the floor tile she stands on — the reported
                // "the alicorn boss never hits me".
                enemy.SetWeaponAimPoint(EnemySensors.GetUnitAimWorld(target));
                return;
            }

            if (_blackboard.HasLastKnownTargetPosition)
            {
                Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;
                enemy.SetWeaponAimPoint(
                    (_blackboard.LastKnownTargetPosition - origin) * TileQueryConstants.PixelToUnit);
            }
        }

        // ── facing (:503-531) ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>aiState == 0</c> follows the drift; <c>aiState &gt; 0</c> turns toward the target over
        /// <c>t_turn</c> ticks rather than snapping.
        /// </summary>
        void TickFacing()
        {
            if (_aiState == 0)
            {
                float dx = _controller.VelocityPixelsPerFrame.x;
                if (dx > 0.5f) ApplyFacing(1);
                if (dx < -0.5f) ApplyFacing(-1);
                return;
            }

            // `aiNapr = celX > X ? 1 : -1;` (:517). `celX` is whatever the last aim wrote, so a boss
            // with no sighting keeps turning toward the stale point — which is what the oracle does.
            float delta = _blackboard.TargetUnit != null
                ? _blackboard.TargetDeltaX
                : _blackboard.HasLastKnownTargetPosition
                    ? _blackboard.LastKnownTargetPosition.x - ((Vector2)transform.position * TileQueryConstants.UnitToPixel).x
                    : 0f;

            if (delta == 0f)
            {
                return;
            }

            int aiNapr = delta > 0f ? 1 : -1;
            if (_controller.FacingDirection != aiNapr)
            {
                if (--_tTurn <= 0)
                {
                    ApplyFacing(aiNapr);
                    _tTurn = BossAlicornRules.TurnTicks;
                }
            }
            else
            {
                _tTurn = BossAlicornRules.TurnTicks;
            }
        }

        // ── the hover bob (:489-498) ─────────────────────────────────────────────────────────────

        void ApplyFloatBob()
        {
            Vector2 velocity = _controller.VelocityPixelsPerFrame;
            float vx = velocity.x;
            float vy = velocity.y;

            BossAlicornRules.FloatBob(ref _tFloat, ref vx, ref vy);

            // The oracle ASSIGNS (`dx = dx * 0.9 + floatX`); the port's only write is additive, so the
            // difference is applied. Feeding the result back as a delta is the same arithmetic.
            Vector2 delta = new Vector2(vx - velocity.x, vy - velocity.y);
            if (delta.sqrMagnitude > 0f)
            {
                _controller.AddVelocityPixelsPerFrame(delta);
            }
        }

        // ── teleport (:309-337) ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>teleport(x, y, param3)</c> — the departure flash, the move, the flight re-read, the
        /// arrival flash.
        /// </summary>
        /// <param name="movePoint">Index into the seven authored points.</param>
        /// <param name="emitArrival">AS3 <c>param3 &gt; 0</c> — the invisibility step arrives silently.</param>
        void TeleportToMovePoint(int movePoint, bool emitArrival)
        {
            Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;
            Vector2 selfWorld = (Vector2)transform.position * TileQueryConstants.UnitToPixel;
            Vector2 selfLocal = selfWorld - origin;

            float height = _controller.SpriteSizePixels.y;

            // `Emitter.emit("telered", loc, X, Y - scY/2, {rx:scX, ry:scY, kol:30})` (:311-315) — the
            // departure is emitted at the OLD position, before the move.
            _controller.EmitParticle("telered", selfWorld + new Vector2(0f, height * 0.5f));

            // `teleport(movePoints[mp].x*40+20, movePoints[mp].y*40+40, …)` (:415). The Y is MIRRORED:
            // AS3's room-local Y grows down from the ceiling, the port's grows up from the floor, so
            // the authored row has to be measured from the other end. The room height is the live
            // value, not a constant, because `movePoints` is expressed relative to whatever arena the
            // boss was placed in. See BossAlicornRules.MovePointPixelY.
            int roomHeightTiles = Room != null ? Room.height : 0;
            Vector2 targetLocal = new Vector2(
                BossAlicornRules.MovePointPixelX(movePoint),
                BossAlicornRules.MovePointPixelY(movePoint, roomHeightTiles));
            Vector2 targetWorld = targetLocal + origin;

            if (_motor != null)
            {
                // `setPos` plus `dx = dy = 0` (:316-317) — the motor's TeleportTo is the only thing
                // that can move the body without the next tick colliding against the old position,
                // and it already clears the velocity.
                _motor.TeleportTo(targetWorld.x, targetWorld.y);
            }
            else
            {
                transform.position = targetWorld * TileQueryConstants.PixelToUnit;
            }

            // **Deviation, recorded: `currentWeapon.setNull()` (:318-322) is not ported, and it has no
            // observable effect in this port's structure.**
            //
            // `Weapon.setNull()` (`Weapon.as:952-961`) does two things: `t_attack = t_reload = 0`, and
            // `X = owner.weaponX; Y = owner.weaponY; animate()`. The second is already covered — the
            // port poses every mount from its owner on every tick (`EnemyWeaponMount.Tick` →
            // `ResolveHoldPoint`), so a teleport moves the weapon with her whether or not this line
            // runs. The first is a cooldown reset on the weapon she is about to switch AWAY from: the
            // port gives each weapon id its own mount (`BossController.EquipBossWeapons`), so the
            // clock that is cleared belongs to a mount that will not be fired again until the
            // rotation comes back round to it — at least `AiTChAfterTeleport` (45) ticks later, and
            // no `rapid` in the boss's loadout is that long. A reset that cannot change a shot is
            // still a reset; it is recorded here rather than added because there is no state in which
            // it could be observed, and this project's rule is that an unobservable change is a
            // hypothesis, not a fix. If a future weapon gets `rapid >= 45`, or the mounts are shared,
            // this becomes real and must be ported.

            // `var _loc4_:Tile = loc.getAbsTile(X, Y + 10);` (:323). AS3's Y grows down, so `Y + 10`
            // is ten pixels BELOW the feet; in the port's y-up space that is `y - 10`. Sampling `+10`
            // instead would ask about the air above the head and put the boss in the air on every
            // point.
            bool solidOrShelfBelow = false;            RoomInstance room = Room;
            if (room != null)
            {
                TileData below = room.GetTileAt(new Vector2(targetLocal.x, targetLocal.y - 10f));
                solidOrShelfBelow = below != null && below.IsSolidOrShelf();
            }

            _isFly = BossAlicornRules.IsFlyingAfterTeleport(solidOrShelfBelow);
            _tFly = BossAlicornRules.FlyTicks;

            // `levit = 0;` (:332) — a teleport cancels any levitation the boss was under.
            if (_controller.IsLevitated)
            {
                _controller.SetLevitated(false);
            }

            // `if(param3 > 0) Emitter.emit("teleport", loc, X, Y - scY/2);` (:333-336).
            if (emitArrival)
            {
                _controller.EmitParticle("teleport", targetWorld + new Vector2(0f, height * 0.5f));
            }
        }

        // ── invisibility (:425-435, :253-269) ────────────────────────────────────────────────────

        void EnterInvisibility()
        {
            _superInvis = true;
            ApplyInvisibilityVisual();
        }

        void LeaveInvisibility()
        {
            _superInvis = false;
            ApplyInvisibilityVisual();
        }

        /// <summary>
        /// <c>animate():253-269</c> — <c>if(superInvis &amp;&amp; pers.infravis == 0) celA = 0; else
        /// celA = 100;</c> with <c>curA</c> ramping five points a tick.
        /// </summary>
        /// <remarks>
        /// <para><b>The port renders the ramp as a visibility switch, and that is the one visible
        /// difference.</b> AS3 fades <c>vis.alpha</c> over twenty ticks; the port has no per-unit alpha
        /// and <see cref="UnitController.SetVisible"/> is binary. The <i>state</i> — invisible to a
        /// player without infravis, visible to one with it — is faithful, and that is what the fight
        /// depends on.</para>
        ///
        /// <para><b><c>infravis</c> is honoured.</b> <c>pers.infravis</c> is the player's perk, and the
        /// port has it (<c>CharacterStats.infravis</c>), so a player who has it keeps seeing the boss
        /// through the invisibility step — which is the whole point of the perk.</para>
        /// </remarks>
        void ApplyInvisibilityVisual()
        {
            if (_controller == null) return;

            bool hidden = _superInvis && !PlayerHasInfravis();
            if (hidden == !_controller.IsVisible)
            {
                return;
            }

            _controller.SetVisible(!hidden);
        }

        /// <summary><c>World.w.pers.infravis == 0</c> — the player's see-through-cloak perk.</summary>
        bool PlayerHasInfravis()
        {
            UnitController target = _blackboard.TargetUnit;
            if (target is PlayerController player && player.CharacterStats != null)
            {
                return player.CharacterStats.infravis > 0;
            }

            return false;
        }

        // ── scythes (:294-307, :727-732) ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>emit(param1 = -1)</c> (<c>:294-307</c>) — one <c>scythe</c> at a random point, bound to
        /// the boss when an index is given.
        /// </summary>
        /// <param name="bindIndex">
        /// AS3 <c>param1</c>. <c>-1</c> (the default) is a <b>free</b> blade — it flies at the player
        /// and is gone. <c>0..5</c> is a <b>bound</b> one: it orbits the boss at 200 px, is eight times
        /// as tough, and dies when she does.
        /// </param>
        /// <remarks>
        /// <para><b>The whole of the oracle's body, in its own order.</b> <c>createUnit("scythe", rand,
        /// rand, true)</c>; then <c>fraction = fraction</c>; then <c>dam *= 1 + level * 0.1</c>; then
        /// <c>oduplenie = 0</c>; then, only for a bound blade, <c>bind = this; maxhp *= 8; hp = maxhp;
        /// bindN = param1</c>.</para>
        ///
        /// <para><b>The spawn point is mirrored, and that is not cosmetic.</b> AS3's box is y 100..600
        /// <i>from the ceiling</i>; taking the literal into a y-up room drops every blade into the
        /// floor, where it dies on its first <c>run()</c>. <see cref="BossAlicornRules.ScytheSpawnPixels"/>
        /// does the mirror against the live room height.</para>
        ///
        /// <para><b>Position is a pixel, not a marker.</b> Unlike a necromancer summon there is no
        /// <c>enspawn</c> row to land on — the oracle picks a random point inside a fixed rectangle —
        /// so this goes through <c>RoomVisualController.RoomPixelToWorld</c> rather than
        /// <c>SpawnPoint.GetEnemyUnityPosition</c>.</para>
        ///
        /// <para><b><c>oduplenie = 0</c> is not ported</b> and needs no substitute: it is AS3's
        /// "this unit is a duplicate, so do not copy it again" guard, read only by the clone path the
        /// port does not have.</para>
        /// </remarks>
        void EmitScythe(int bindIndex = -1)
        {
            RoomInstance room = Room;
            if (room == null)
            {
                ReportScytheGapOnce(ref _scytheRoomReported,
                    "[BossAlicorn] emit() could not place a scythe: the brain has no room (the tile " +
                    "query has not been handed over). AS3 cannot reach this state — `loc` is the " +
                    "location.");
                return;
            }

            if (!ResourcesUnitDefinitionProvider.Shared.TryGetUnit(ScytheController.ControllerId, out UnitDefinition definition)
                || definition == null)
            {
                ReportScytheGapOnce(ref _scytheDefinitionReported,
                    $"[BossAlicorn] '{ScytheController.ControllerId}' has no UnitDefinition, so emit() " +
                    "dropped the blade. AS3's createUnit always has one — the id comes from AllData.");
                return;
            }

            // Cached per brain: FindForRoom does a scene scan, and the shield break spawns six in one
            // tick. The room cannot change under a live boss, so one resolution is enough.
            if (_scytheRoom == null || _scytheRoom.RoomInstance != room)
            {
                _scytheRoom = RoomVisualController.FindForRoom(room);
            }

            if (_scytheRoom == null)
            {
                ReportScytheGapOnce(ref _scytheControllerReported,
                    $"[BossAlicorn] No live RoomVisualController owns room '{room.id}', so emit() " +
                    "could not build a scythe. AS3 cannot reach this state — `loc` is the location.");
                return;
            }

            // `createUnit("scythe", Math.random() * 1600 + 160, Math.random() * 500 + 100, true)`.
            // Two draws, x then y, in that order — the oracle's argument order, so a seeded run
            // consumes the stream identically.
            float rollX = UnityEngine.Random.value;
            float rollY = UnityEngine.Random.value;
            BossAlicornRules.Vector2Pixels spawn = BossAlicornRules.ScytheSpawnPixels(
                rollX, rollY, room.height);

            float health = definition.health;
            var instance = new UnitInstance
            {
                entityId = PFE.Core.Ids.EntityId
                    .CreateForRoomSpawn(room.id, "scythe", _scytheSerial++)
                    .ToString(),
                unitId = ScytheController.ControllerId,
                unitType = ScytheController.ControllerId,
                isDead = false,
                maxHealth = health,
                currentHealth = health,
                // Named explicitly rather than left empty: `scythe.asset` authors no controllerId, so
                // the spawner would otherwise have to reach the same answer through its unitId
                // fallback. Naming it makes the intent readable at the call site.
                controllerId = ScytheController.ControllerId,
                attributes = new List<MapObjectAttributeData>(),
                // `_loc2_.fraction = fraction` (:297) — the blades fight for the boss, and the
                // definition's own `fraction: 2` (Raider) must not survive.
                fraction = _controller.Faction,
                // `createUnit`'s fourth argument is `true`, i.e. "place immediately". No `turn` is
                // authored, so the facing is the spawner's default.
                facingDirection = 1,
                digger = 0,
            };

            Vector3 worldPosition = _scytheRoom.RoomPixelToWorld(
                new Vector2(spawn.X, spawn.Y));

            if (!_scytheRoom.SpawnUnitNow(instance, worldPosition) ||
                !_scytheRoom.TryGetSpawnedUnit(instance, out GameObject spawned) || spawned == null)
            {
                ReportScytheGapOnce(ref _scytheSpawnReported,
                    $"[BossAlicorn] emit() built a 'scythe' record but the room refused to spawn it " +
                    $"(no spawner or no physical parent in '{room.id}').");
                return;
            }

            var scythe = spawned.GetComponent<ScytheController>();
            if (scythe == null)
            {
                ReportScytheGapOnce(ref _scytheControllerReported,
                    "[BossAlicorn] emit() spawned a 'scythe' but it has no ScytheController — " +
                    "RoomUnitSpawner's controller table is missing the entry, so the blade would be " +
                    "an inert collider.");
                return;
            }

            // `_loc2_.dam *= 1 + level * 0.1` (:298).
            scythe.ApplyEmitDamageScale(BossAlicornRules.ScytheDamageScale(NpcLevel));

            if (bindIndex >= 0)
            {
                // `_loc2_.bind = this; _loc2_.maxhp *= 8; _loc2_.hp = _loc2_.maxhp; bindN = param1;`
                scythe.BindTo(_controller, bindIndex);
            }
            else
            {
                // `cel = World.w.gg` (:60) — a free blade latches its angle at the player.
                scythe.SetTarget(_blackboard.TargetUnit ?? FindPlayerUnit());
            }

            _scythesEmitted++;
        }

        /// <summary>
        /// The player, for a blade that has to latch an angle — <c>World.w.gg</c>.
        /// </summary>
        /// <remarks>
        /// The blackboard's target is preferred (it is the oracle's own <c>celUnit</c> and is already
        /// resolved), but it is <c>null</c> whenever the boss has lost sight, and a blade with no
        /// target would hover at its spawn point for its whole life. AS3 has no such state, so this
        /// falls back to the same scene lookup <see cref="ScytheController"/> makes.
        /// </remarks>
        static UnitController FindPlayerUnit()
        {
            return UnityEngine.Object.FindFirstObjectByType<PlayerController>();
        }

        static void ReportScytheGapOnce(ref bool flag, string message)
        {
            if (flag)
            {
                return;
            }

            flag = true;
            Debug.LogWarning(message);
        }

        /// <summary>
        /// Play the dome's one-shot break — the visual half of <c>this.visshit.gotoAndPlay(2)</c>
        /// (<c>:712</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Resolved lazily and from the CHILDREN.</b> The overlay is added by
        /// <c>RoomUnitSpawner</c> to the unit's <c>Visual</c> child, and only after
        /// <c>controller.Initialize</c> — so a cache taken in <c>Awake</c> would be permanently null,
        /// and <c>GetComponent</c> on this object would never find it at all. Both mistakes produce the
        /// same silence: the dome stays up through the phase transition with no warning anywhere.</para>
        ///
        /// <para><b>Nothing here is required for the fight to work.</b> <c>_isShit</c> is already
        /// cleared by the caller, so the revive, the six blades and the phase change all happen whether
        /// or not a dome exists — the clip is the only thing that needs the component. That is why this
        /// is a null-conditional call and not an error path: an un-imported overlay must not stop the
        /// boss from breaking.</para>
        /// </remarks>
        void BreakDome()
        {
            if (_dome == null)
            {
                _dome = GetComponentInChildren<UnitShieldOverlay>(true);
            }

            _dome?.BreakOnce();
        }

        // ── death (:703-738) ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>die()</c>: while the dome is up, the first lethal hit does <b>not</b> kill.
        /// </summary>
        /// <returns><c>true</c> when the boss revived into its second stage.</returns>
        public override bool TryReviveSecondLife()
        {
            if (!_isShit)
            {
                return false;
            }

            // `this.isShit = false; this.visshit.gotoAndPlay(2);` (:711-712).
            _isShit = false;
            BreakDome();

            // `hp = maxhp - 0.1;` (:713) — written by the controller, which owns the HP, through
            // BossController.HpAfterRevive. Doing it here as well would be two writers.

            // `aiState = 0; aiTCh = 10;` (:714-715) — the boss restarts its rotation ten ticks after
            // the dome breaks, i.e. it teleports almost immediately.
            _aiState = 0;
            _aiTCh = 10;

            // `blood = 1; bloodEmit = Emitter.arr["blood"]; bloodEmit.cast(loc, X, Y - 50, {kol:100})`
            // (:716-722).
            Vector2 selfWorld = (Vector2)transform.position * TileQueryConstants.UnitToPixel;
            _controller.EmitParticle("blood", selfWorld + new Vector2(0f, _controller.SpriteSizePixels.y * 0.5f));

            // `superInvis = false; dropTeleObj(); isBlast = false;` (:706-708).
            LeaveInvisibility();
            _telekinesis.Drop();
            _isBlast = false;

            // `sost = 1; mat = 0; timerDie = 90;` (:724-726) — alive again, and no longer
            // spell-immune. `mat = 0` is the "can now be damaged by magic" flag; the port's armour
            // model has no equivalent, recorded in the boss doc.

            // `while(_loc2_ < 6) emit(_loc2_++)` (:727-732) — six BOUND scythes, one per ring slot.
            // The index is the ring phase: `bindN` spaces them 60° apart around her.
            for (int i = 0; i < BossAlicornRules.ScythesOnShieldBreak; i++)
            {
                EmitScythe(i);
            }

            SetPhase(2);
            _isSecondLifeActive = true;
            return true;
        }

        /// <summary>
        /// <c>animate():221-229</c> — the death row and its one-shot burst, which the oracle emits
        /// inside <c>animate()</c> rather than in <c>die()</c>.
        /// </summary>
        void TickDeathAnimation()
        {
            if (_isBlast)
            {
                return;
            }

            _isBlast = true;

            // `die():706-708` — `this.superInvis = false; this.dropTeleObj(); this.isBlast = false;`
            // runs on **both** paths, *before* the `if(isShit)` branch. The revive path repeats the
            // two calls inside TryReviveSecondLife; the FINAL death has no other caller, and without
            // this one a grabbed player stays levitated forever — TickControl stops running the
            // instant IsAlive goes false, so nothing else would ever release the hold.
            LeaveInvisibility();
            _telekinesis.Drop();

            Vector2 selfWorld = (Vector2)transform.position * TileQueryConstants.UnitToPixel;
            _controller.EmitParticle("bloodblast2", selfWorld + new Vector2(0f, _controller.SpriteSizePixels.y * 0.5f));
        }

        /// <summary>
        /// AS3 <c>animate()</c> (<c>:216-274</c>), branch for branch: <c>sost == 2</c> → <c>die</c>,
        /// then <c>isFly</c> → <c>fly</c>, then <c>stay &amp;&amp; |dx| &gt; 1</c> → <c>walk</c>, else
        /// <c>stay</c>.
        /// </summary>
        /// <remarks>
        /// <para><b><c>walk</c> has no frames, in the oracle as well as here.</b> <c>bossalicorn</c>
        /// authors exactly three rows — <c>stay</c> (r0/l20), <c>fly</c> (r1/l14), <c>die</c> (r2/l14)
        /// — and every other row is <c>length: 0</c>. In AS3 the missing <c>&lt;blit&gt;</c> makes
        /// <c>anims["walk"].restart()</c> throw inside <c>animate()</c>'s own <c>try/catch</c>, so the
        /// sprite keeps its previous cell. Returning the id here would make the port's animator blank
        /// the state instead, so the grounded-and-moving arm falls back to <c>stay</c>: same picture,
        /// no exception.</para>
        /// </remarks>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive) return "die";
            if (_isFly) return "fly";

            bool moving = Mathf.Abs(_controller.VelocityPixelsPerFrame.x) > 1f;
            if (_controller.IsGrounded && moving && HasAuthoredFrames("walk"))
            {
                return "walk";
            }

            return "stay";
        }

        /// <summary>Whether this unit's sheet authors any frame for <paramref name="as3Id"/>.</summary>
        bool HasAuthoredFrames(string as3Id)
        {
            UnitDefinition definition = _controller != null ? _controller.Definition : null;
            if (definition == null || definition.animations == null) return false;
            return definition.animations.Get(as3Id).HasFrames;
        }
    }
}

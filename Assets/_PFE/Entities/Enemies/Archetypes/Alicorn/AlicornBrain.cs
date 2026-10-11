using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Alicorn / Magic enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitAlicorn.as</c>.
    /// Elite flying caster with levitation, magic projectile, telekinesis, teleport, and shield.
    /// </summary>
    public class AlicornBrain : EnemyBrain
    {
        /// <summary>
        /// AS3 <c>Unit.isFly</c> (<c>Unit.as:284</c>) — declared <c>= false</c>, and the alicorn only
        /// leaves the ground through <c>jump()</c> (<c>UnitAlicorn.as:484-491</c>, which sets
        /// <c>isFly = true</c> after applying <c>dy = -jumpdy</c>).
        ///
        /// <para><b>This used to default to <c>true</c>, and that single literal was the whole
        /// "the alicorn is always airborne" defect.</b> With it true from <c>Awake</c>, and with
        /// <c>TickIdle</c>/<c>TickAlert</c>/<c>TickCombatChase</c> each re-asserting it, the only route
        /// back to the ground was the state-4 descent — which was itself unreachable because those same
        /// overrides reset <see cref="_familyState"/> to 2/3 on the very next tick. The unit drew the
        /// <c>fly</c> row forever. See <see cref="TakeOff"/> for the AS3 takeoff it now mirrors.</para>
        /// </summary>
        [SerializeField] protected bool _isFly;
        /// <summary>
        /// AS3 <c>Unit.isSit</c> (<c>Unit.as:282</c>), default <c>false</c>. It is a base-class flag set
        /// only by <c>setSit()</c> (<c>Unit.as:2512-2517</c>), which no enemy path calls, so for an
        /// alicorn enemy this is always <c>false</c> and the <c>polz</c>/<c>sit</c> arm of
        /// <see cref="ResolveAnimState"/> is unreachable today. It is modelled rather than omitted
        /// because <c>UnitAlicorn.animate()</c> branches on it (<c>UnitAlicorn.as:297-308</c>), and an
        /// omitted branch is the failure mode this guide set exists to prevent.
        /// </summary>
        [SerializeField] protected bool _isSit;
        [SerializeField] protected float _flightSpeed = 3.5f;
        [SerializeField] protected float _magicRangePixels = 480f;

        /// <summary>
        /// AS3 <c>jumpdy</c> (<c>Unit.as:236</c>, family row <c>jump='15'</c>) — the takeoff impulse
        /// <c>jump()</c> applies. Positive here because the port's <c>JumpVertical</c> is positive-up
        /// while AS3's <c>dy = -jumpdy</c> is positive-down (the mirror documented in the family guide
        /// §4).
        /// </summary>
        [SerializeField] protected float _jumpForce = 15f;

        /// <summary>Vertical speed of the state-4 descent, in px per frame.</summary>
        [SerializeField] protected float _descentSpeed = 2f;

        // Timers
        protected int _aiTCh;
        protected int _teleportCooldownTicks;
        protected int _spellCooldownTicks;

        /// <summary>
        /// AS3 <c>aiAttackT = 16</c> (<c>UnitAlicorn.as:1016</c>) — how many frames the weapon's trigger
        /// is held once the attack decision is made. The weapon fires on each of them
        /// (<c>:1018-1023</c>: <c>--aiAttackT; if(aiAttackT &gt; 0) currentWeapon.attack();</c>), and
        /// the weapon's own <c>t_attack</c> paces the individual rounds.
        /// </summary>
        protected const int AttackBurstTicks = 16;

        /// <summary>Frames left in the current weapon burst; 0 when the alicorn is not firing.</summary>
        protected int _attackBurstTicks;

        /// <summary>
        /// Family internal state (UnitAlicorn.as:493-1085):
        /// 0: Idle, 1: Patrol, 2: Alert, 3: CombatChase, 4: Descend-and-land
        /// </summary>
        protected int _familyState;

        /// <summary>
        /// AS3 <c>aiState == 4</c> — "fly to a point far below and land"
        /// (<c>UnitAlicorn.as:603-608</c>, exit at <c>:833-837</c>).
        ///
        /// <para><b>Why this is a flag and not just <c>_familyState == 4</c>.</b> The port set
        /// <c>_familyState</c> at the top of every tick override, so the 4 written by the descent
        /// decision was overwritten by <c>1</c> on the very next tick: the descent lasted exactly one
        /// frame and the unit could never reach the ground. AS3's <c>aiState</c> is not recomputed
        /// while it holds 4 either — it is cleared by <c>isFly = false; aiState = 1</c> on landing.
        /// Keeping the mode in its own flag makes the descent run to completion regardless of which
        /// state tick is dispatching.</para>
        /// </summary>
        protected bool _descending;

        public bool IsFlying => _isFly;
        public bool IsSitting { get => _isSit; set => _isSit = value; }
        public bool IsDescending => _descending;
        public int FamilyState => _familyState;

        /// <summary>
        /// The unit as an <see cref="EnemyController"/>, which is where the weapon mounts live.
        /// <c>_controller</c> is declared on <see cref="EnemyBrain"/> as the base <c>UnitController</c>,
        /// so the weapon half needs the cast; it is null only for a brain attached to something that is
        /// not an enemy controller, which is not a state an alicorn can be in.
        /// </summary>
        protected EnemyController EnemyController => _controller as EnemyController;

        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            ApplyDefinitionTuning();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ApplyDefinitionTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyDefinitionTuning();
        }

        protected virtual void ApplyDefinitionTuning()
        {
            if (_controller == null || _controller.Definition == null) return;
            UnitDefinition def = _controller.Definition;

            _flightSpeed = def.moveSpeed > 0 ? def.moveSpeed : 3.5f;
            _patrolSpeed = _flightSpeed * 0.7f;
            _chaseSpeed = _flightSpeed;
            _alertSpeed = _flightSpeed * 0.8f;
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;
            if (_teleportCooldownTicks > 0) _teleportCooldownTicks--;
            if (_spellCooldownTicks > 0) _spellCooldownTicks--;

            if (_controller is AlicornController alicorn)
            {
                // AS3 `UnitAlicorn.control():571` reads `aiSpok > 0` — the awareness budget, which
                // survives losing sight of the target. This used to pass `TargetUnit != null`, i.e. "is
                // the target visible right now": a different and much stricter condition, which stalled
                // the shield timer at 150 the moment the player broke line of sight.
                alicorn.UpdateShieldTick(_blackboard.AlertTimerTicks > 0);
            }

            // Before the state tick, so the trigger is released on the same tick the unit dies or the
            // burst expires rather than one tick later.
            UpdateWeaponFire();

            base.SimTick(tickIndex);

            if (_controller == null || !_controller.IsAlive) return;

            // The descent owns the vertical axis until it lands, and it is checked before the flight
            // model because AS3's `aiState == 4` branch (`:824-840`) replaces the hover steering rather
            // than running alongside it.
            if (_descending)
            {
                // Reported after the state tick, because the tick override writes its own aiState
                // (2/3) at the top and the oracle's aiState is 4 for the whole descent.
                _familyState = 4;
                UpdateDescent();
                return;
            }

            if (_isFly)
            {
                UpdateFlightPhysics(tickIndex);
            }
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.jump()</c> (<c>:484-491</c>) — the only way off the ground:
        /// <c>if(!isFly) dy = -jumpdy * param1; isFly = true;</c>.
        ///
        /// <para>The <c>!isFly</c> guard is load-bearing: <c>jump()</c> is called every tick while the
        /// unit is navigating upward (<c>:997-1001</c>), and without the guard each of those ticks would
        /// re-apply the full impulse. Only the first call moves the unit; the rest merely keep it in the
        /// air.</para>
        ///
        /// <para><paramref name="param"/> is AS3's <c>param1</c> — <c>0.5</c> for the ledge hop in
        /// state 1 (<c>:890</c>/<c>:909</c>), <c>1</c> (or <c>1.5</c> when <c>isPlav</c>) for the combat
        /// takeoff in states 2/3 (<c>:999</c>).</para>
        /// </summary>
        protected virtual void TakeOff(float param = 1f)
        {
            if (!_isFly)
            {
                // Positive-up: the port's JumpVertical takes an "up" value, AS3's `dy = -jumpdy` is
                // positive-down. Carrying the minus sign across drives the unit into the floor.
                JumpVertical(_jumpForce * param);
            }
            _isFly = true;
        }

        /// <summary>
        /// AS3's state-4 descent (<c>:824-840</c>): stop steering, sink, and land.
        /// <c>isFly = false; aiState = 1</c> on touchdown.
        /// </summary>
        protected virtual void UpdateDescent()
        {
            MoveHorizontal(0f);

            if (_controller.IsGrounded)
            {
                Land();
                return;
            }

            JumpVertical(-_descentSpeed);
        }

        protected virtual void Land()
        {
            _isFly = false;
            _descending = false;
            _familyState = 1;
            SetState(EnemyAIState.Patrol);
        }

        /// <summary>
        /// The <c>isFly</c> branch of <c>UnitAlicorn.control()</c> (<c>:780-842</c>): steer toward the
        /// current goal while holding station, otherwise hover with the <c>floatY</c> bob.
        ///
        /// <para>There is still no <c>norma</c>-steered 2-D velocity in <see cref="EnemyBrain"/> — the
        /// base offers <c>MoveHorizontal</c> plus this positive-up <c>JumpVertical</c> — so the altitude
        /// half is a proportional approach to the goal rather than the oracle's normalised steering.
        /// That is the honest reduced form, not a claim of parity.</para>
        /// </summary>
        protected virtual void UpdateFlightPhysics(int tickIndex)
        {
            if (!_blackboard.HasLastKnownTargetPosition)
            {
                // Ambient hover bob (`floatY`, :783-788).
                JumpVertical(Mathf.Sin(tickIndex * 0.1f) * 1.5f);
                return;
            }

            // Hold roughly 60 px above the target — the stand-off the magic bolt wants.
            float desiredYOffset = _blackboard.TargetDeltaY + 60f;
            if (desiredYOffset > 20f)
            {
                JumpVertical(2f);
            }
            else if (desiredYOffset < -20f)
            {
                JumpVertical(-1.5f);
            }
            else
            {
                JumpVertical(0f);   // hold station
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            StopMovement();
        }

        protected override void TickPatrol(int tickIndex)
        {
            _familyState = 1;
            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);

                // AS3 `UnitAlicorn.as:603-608` — the decision ladder's first arm, verbatim: while
                // airborne, and not already landing, the unit is told to fly to a point far below
                // (`setCel(null, X ± 100, Y + 1000)`) and enters state 4. The port used to gate this
                // behind a 25% roll that the oracle does not have, which is why an airborne alicorn
                // almost never came down even when the state-4 flag had not been clobbered.
                if (_isFly)
                {
                    _familyState = 4;
                    _descending = true;
                    return;
                }

                SetState(EnemyAIState.Idle);
                return;
            }

            float dir = (_controller != null && _controller.FacingDirection < 0) ? -1f : 1f;
            MoveHorizontal(dir * _patrolSpeed);
        }

        protected override void TickAlert(int tickIndex)
        {
            _familyState = 2;
            ConsiderTakeOff();
            base.TickAlert(tickIndex);
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.as:997-1001</c> — in states 2/3 the unit jumps when it is navigating
        /// <b>upward</b> (<c>aiVNapr &lt; 0</c> sets <c>_loc2_ = 1</c>, which the next block turns into
        /// <c>jump(_loc2_)</c>), and <c>jump()</c> is what sets <c>isFly</c>.
        ///
        /// <para>This is the replacement for the port's <c>_isFly = true</c> at the top of the two
        /// ticks: same observable — an engaged alicorn takes to the air — but it is now an event the
        /// oracle actually has, so the unit can also be found on the ground.</para>
        ///
        /// <para>Port Y is up, so a target <i>above</i> is <c>TargetDeltaY &gt; 0</c>; AS3's
        /// <c>celDY &gt; 80</c> test (<c>:976</c>) is the opposite sign because AS3 Y grows down.</para>
        /// </summary>
        protected virtual void ConsiderTakeOff()
        {
            if (_isFly || _descending) return;
            if (_controller == null || !_controller.IsAlive) return;
            if (!_blackboard.HasLastKnownTargetPosition) return;

            if (_blackboard.TargetDeltaY > 40f)
            {
                TakeOff();
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            _familyState = 3;
            ConsiderTakeOff();

            // The hunt ends on the awareness budget, not on the sighting — `EnemySensors` nulls
            // `TargetUnit` on the first obscured tick, and exiting here made the alicorn forget the
            // player the instant line of sight broke. See `EnemyBrain.ChaseBudgetExhausted`.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                // Keep closing on the last place the target was seen; the altitude half is the
                // `_isFly` branch in `SimTick`, which already steers on `LastKnownTargetPosition`.
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            // Teleport if player gets too close (UnitAlicorn actPort)
            if (dist < 80f && _teleportCooldownTicks <= 0)
            {
                TryTeleportAway();
            }

            // Stand-off distance around 200..350 px
            if (dist > 350f)
            {
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }
            else if (dist < 150f)
            {
                // Back away slightly
                float dir = dx >= 0 ? -1f : 1f;
                MoveHorizontal(dir * _patrolSpeed);
            }
            else
            {
                StopMovement();
            }

            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            return _blackboard.TargetUnit != null && _blackboard.TargetDistance <= _magicRangePixels && _blackboard.HasLineOfSight;
        }

        /// <summary>
        /// Hold the <c>alilight</c> trigger for the burst window and keep its aim on the target.
        /// </summary>
        /// <remarks>
        /// <para><b>Held, not pulsed.</b> AS3 calls <c>currentWeapon.attack()</c> once per frame for the
        /// whole burst (<c>UnitAlicorn.as:1018-1023</c>) and lets the weapon's own <c>t_attack</c>
        /// decide when a round leaves. Holding <c>BeginAttack</c> for the same window is the port's
        /// equivalent, and the weapon controller's cooldown does the pacing. Pulsing it for a single
        /// tick would make a <c>rapid='35'</c> weapon fire once per decision instead of once per
        /// cooldown, which is a slower and quieter alicorn than the oracle's.</para>
        ///
        /// <para><b>The aim is re-read every firing tick</b>, not latched at the decision, so the bolt
        /// follows a moving player — which is what the oracle does, since <c>attack()</c> resolves its
        /// direction from the unit's current target each frame.</para>
        ///
        /// <para><b>The aim is in Unity world units, and it has to be.</b> This is the whole of the
        /// reported "the alicorn fires at ~45° to the right instead of at the player". The value is
        /// consumed twice, both times in world units:</para>
        /// <list type="bullet">
        ///   <item><description><c>WeaponHoldPointMath.Inputs.AimX</c> — documented as "Cursor / aim
        ///     world X. AS3 <c>celX</c>", compared against <c>OwnerX ± scX/2</c>, which is also world
        ///     units. The player's own <c>PlayerWeaponLoadout.ResolveHoldPoint</c> passes
        ///     <c>_aimTarget.x</c> and documents it as "in Unity units".</description></item>
        ///   <item><description><c>RangedWeaponController.TickOneFlashFrame</c>:
        ///     <c>rot2 = Atan2(aimTarget.y - State.Y, aimTarget.x - State.X)</c>, where <c>State.X/Y</c>
        ///     are the weapon's world position (they lerp toward the hold point, itself world units).
        ///     Feeding pixels here makes the numerator and denominator both ≈ the target's <i>absolute</i>
        ///     pixel coordinates, so the angle collapses to the direction from the <b>world origin</b> to
        ///     the player — a fixed heading that barely moves as the alicorn or the player does, which is
        ///     exactly the observed symptom.</description></item>
        /// </list>
        /// <para>An earlier revision multiplied by <c>100f</c> ("world pixels — the same space the
        /// projectile is resolved in"). That reasoning is wrong: the projectile is <i>spawned</i> at a
        /// world-unit position, but the <i>angle</i> is computed from the aim point here, and the port's
        /// entire weapon stack — the hold point, the rotation, <c>PlayerWeaponLoadout</c> — speaks Unity
        /// units. The <c>TargetDeltaX/Y</c> family on <c>EnemyBlackboard</c> really is in pixels, which is
        /// what makes this easy to get wrong; those are for the <i>AI</i> range tests and never for the
        /// weapon.</para>
        /// </remarks>
        protected virtual void UpdateWeaponFire()
        {
            if (EnemyController == null) return;

            EnemyWeaponMount mount = EnemyController.GetWeaponMount("alilight");
            if (mount == null) return;

            bool firing = _attackBurstTicks > 0 && _controller.IsAlive;
            if (firing) _attackBurstTicks--;

            if (firing && _blackboard.TargetUnit != null)
            {
                // Unity world units — the space WeaponHoldPointMath and RangedWeaponController both
                // resolve in. Do NOT scale by 100 here; see the remarks above.
                mount.SetAimTarget(_blackboard.TargetUnit.transform.position);
            }

            mount.SetFiring(firing);
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;
            if (_spellCooldownTicks > 0) return;

            _spellCooldownTicks = UnityEngine.Random.Range(30, 60);

            // Magic bolt attack (alilight). AS3 `:1016` arms the burst; the weapon fires on the
            // following frames from `UpdateWeaponFire`, which is what makes the bolt leave the horn
            // rather than the decision point.
            _controller.MakeNoise(150, true);

            // The bolt carries the damage now. Applying it here as well would make every shot hit
            // twice — once on the projectile's impact and once instantly — so the direct report is
            // only the fallback for a unit whose mount could not be built (no WeaponDefinition for the
            // id, or a bare test spawn with no container), which keeps an unarmed alicorn from being
            // completely harmless.
            if (EnemyController != null && EnemyController.GetWeaponMount("alilight") != null)
            {
                _attackBurstTicks = AttackBurstTicks;
                return;
            }
            float baseDamage = _controller.Definition != null ? _controller.Definition.damage : 15f;

            // World units, like every other `PendingDamage.Direct` caller in the tree — the impact point
            // drives the floating-number and blood-spray overlays, which are Unity-space. This used to
            // carry the same `* 100f` as the aim target; see UpdateWeaponFire for why pixels are wrong
            // anywhere on the weapon path.
            Vector2 targetPos = _blackboard.TargetUnit.transform.position;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, DamageType.Laser),
                _blackboard.TargetUnit,
                targetPos);

            if (_controller.DamageSystem != null)
            {
                _controller.DamageSystem.Report(pending);
            }
            else
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        protected virtual void TryTeleportAway()
        {
            _teleportCooldownTicks = UnityEngine.Random.Range(90, 150);
            _controller.MakeNoise(200, true);

            // Offset position by 150-250 pixels away from player
            float dir = (_blackboard.TargetDeltaX >= 0) ? -1f : 1f;
            Vector3 offset = new Vector3(dir * UnityEngine.Random.Range(1.5f, 2.5f), UnityEngine.Random.Range(0.5f, 1.5f), 0f);
            transform.position += offset;
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.animate()</c> (<c>UnitAlicorn.as:269-327</c>), branch for branch.
        ///
        /// <para><b>The state set is exactly <c>{die, fall, death, fly, polz, sit, walk, stay}</c>.</b>
        /// An earlier version of this method returned <c>pre</c> while the spell cooldown was above 20,
        /// and <c>trot</c>/<c>jump</c> on the ground/airborne arms. The oracle sets none of those for an
        /// alicorn, and <c>alicorn1..3</c>'s imported sheet authors all three at <c>length: 0</c> — so
        /// <c>SetState</c> accepted the id and found no frames, and the unit drew nothing for up to 40
        /// ticks after every spell (<c>_spellCooldownTicks</c> is set to <c>Random.Range(30,60)</c> on a
        /// cast). The membership lint could not see it: <c>pre</c>/<c>trot</c>/<c>jump</c> are all valid
        /// <see cref="AnimationSet.As3Ids"/>. Do not add a state here that the oracle does not set.</para>
        ///
        /// <para><c>stay</c> in the oracle is groundedness (see <c>Unit.as:1962</c>), and <c>dx</c> is the
        /// per-frame horizontal velocity in pixels — hence the <c>±1</c> test, not a units-per-second
        /// one.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // `sost == 2 || sost == 3` — the two death states.
            if (!_controller.IsAlive)
            {
                if (_controller.IsGrounded)
                {
                    return _animator != null && string.Equals(_animator.StateName, "death", StringComparison.Ordinal)
                        ? "fall"
                        : "die";
                }
                return "death";
            }

            if (_isFly) return "fly";

            bool grounded = _controller.IsGrounded;
            float dx = _motor != null ? _motor.State.Velocity.x : 0f;
            bool moving = dx > 1f || dx < -1f;

            if (_isSit)
            {
                return grounded && moving ? "polz" : "sit";
            }

            if (grounded && moving) return "walk";

            return "stay";
        }
    }
}

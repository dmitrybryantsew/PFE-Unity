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
    /// ArmedShooter enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitRaider.as</c> and its subclasses
    /// (UnitSlaver, UnitZebra, UnitRanger, UnitEncl, UnitMerc, UnitNecros).
    /// </summary>
    public class ArmedShooterBrain : EnemyBrain
    {
        // ── Constants & Configuration from Oracle ─────────────────────────────

        /// <summary>AS3 UnitRaider.optDistAtt default window (200 px).</summary>
        protected const float OptDistAttackPixels = 200f;

        /// <summary>Default delay before acting on first sight (UnitRaider.tupizna = 40).</summary>
        protected int _tupizna = 40;

        /// <summary>
        /// Awareness ladder threshold (AS3 <c>maxSpok</c>). Set by
        /// <see cref="ApplyDefinitionTuning"/>: <c>Unit.as:362</c> declares 30 and only
        /// <c>UnitEncl</c>/<c>UnitMerc</c> raise it to 50 (<c>UnitEncl.as:25</c>, <c>UnitMerc.as:27</c>).
        /// Not serialized — the value is derived, never authored.
        /// </summary>
        protected int _maxSpok = 30;
        protected int _aiSpok;

        /// <summary>
        /// AS3 <c>UnitRaider.attackerType</c> (<c>UnitRaider.as:83</c>, declared <c>0</c>) — how this unit
        /// attacks:
        /// <list type="bullet">
        /// <item><description><b>0</b> — contact: the body hits, no weapon swing (<c>attKorp</c>).</description></item>
        /// <item><description><b>1</b> — the held <c>tip == 1</c> weapon is swung (<c>currentWeapon.attack()</c>).</description></item>
        /// <item><description><b>2</b> — ranged: the held weapon is fired.</description></item>
        /// <item><description><b>3</b> — thrown: the held weapon is thrown.</description></item>
        /// </list>
        ///
        /// <para><b>Read, not computed.</b> The value is derived once, by
        /// <see cref="ArmedShooterController"/> from the weapon it rolled
        /// (<c>UnitRaider.as:257-272</c>), and copied here in
        /// <see cref="ApplyDefinitionTuning"/>. The brain must not re-derive it: the previous
        /// id-and-substring inference was a second, divergent copy of the oracle's ladder, and it is
        /// precisely what made a <c>raider1</c> that rolled a gun fight as a contact attacker.</para>
        /// </summary>
        [SerializeField]
        protected int _attackerType;

        /// <summary>Tactical flags from AS3.</summary>
        [SerializeField] protected bool _isWalker;
        [SerializeField] protected bool _isSniper;
        /// <summary>
        /// Mirrors AS3 <c>UnitRaider.dash</c> (<c>UnitRaider.as:57</c>) — the gate on the states-7/8
        /// dash-and-charge arm (<c>:764</c>).
        ///
        /// <para><b>It is never assigned, and that is faithful.</b> AS3 declares
        /// <c>public var dash:Boolean = false</c> and then never writes it: <c>:57</c> is the declaration
        /// and <c>:764</c> is its only read, and no <c>&lt;un dash=…&gt;</c> exists anywhere in
        /// <c>AllData.as</c> (grep: 0 hits). So the dash arm is dead in the <b>oracle</b> too, and the
        /// port's dead arm is a copy of the oracle's dead arm, not a port defect. Kept because a
        /// faithful port reproduces the oracle's reachability, not its intent — and because deleting it
        /// would make the port *diverge* the day a subclass sets it.</para>
        /// </summary>
        [SerializeField] protected bool _canDash;
        [SerializeField] protected bool _durak = true; // allows sprinting
        [SerializeField] protected bool _isFlyer;
        [SerializeField] protected float _aiDist = 400f;

        /// <summary>
        /// Burst cadence in ticks (AS3 <c>aiAttackOch</c>, <c>UnitRaider.as:1525-1536</c>).
        ///
        /// <para><b>AS3 reads this from data and the port cannot yet.</b>
        /// <c>UnitRaider.getXmlParam()</c> assigns it from the unit's own <c>&lt;un och=…&gt;</c>
        /// attribute — <c>if(node0.un.@och.length()) this.aiAttackOch = node0.un.@och;</c>
        /// (<c>UnitRaider.as:319-321</c>). <c>UnitExtrasParser</c> deliberately reads only <c>res</c>
        /// from that node, and says why: the node carries ~19 attributes and giving them writers before
        /// anything reads them is the trap <c>canResurrect</c> was already in. So this is a <b>constant
        /// standing in for an unimported attribute</b>, not a tuned value. Do not "balance" it. Closing
        /// the <c>&lt;un&gt;</c> import is the work that replaces it.</para>
        /// </summary>
        protected int _aiAttackOch = 30;
        protected int _aiAttackT;

        /// <summary>Jump cooldown (AS3 aiJump, 30..79 ticks).</summary>
        protected int _jumpCooldownTicks;

        /// <summary>State timer (AS3 aiTCh).</summary>
        protected int _aiTCh;

        /// <summary>
        /// Family internal state (0..8 in UnitRaider.as:624-1453):
        /// 0: Idle, 1: Patrol, 2: Alert, 3: CombatChase, 4: Hold/strafe,
        /// 5: Spotted deciding, 6: Grenade/mine approach, 7: Dash wind-up, 8: Charge
        /// </summary>
        protected int _familyState;

        protected float _walkSpeed = 2f;
        protected float _runSpeed = 4f;

        public int AttackerType { get => _attackerType; set => _attackerType = value; }
        public int FamilyState => _familyState;

        /// <summary>
        /// This unit as an <see cref="ArmedShooterController"/>, which is where the rolled weapon and its
        /// mount live. <c>_controller</c> is declared on <see cref="EnemyBrain"/> as the base
        /// <c>UnitController</c>, so the weapon half needs the cast; null means the brain is attached to
        /// something that is not an ArmedShooter.
        /// </summary>
        protected ArmedShooterController ShooterController => _controller as ArmedShooterController;

        /// <summary>
        /// Whether the oracle called <c>currentWeapon.attack()</c> on this tick — AS3's "the trigger is
        /// held right now".
        ///
        /// <para><b>Why a per-tick latch rather than a persistent flag.</b> AS3 calls
        /// <c>currentWeapon.attack()</c> from <c>UnitRaider.attack()</c>, which the state machine runs
        /// every frame (<c>UnitRaider.as:1483-1553</c>). The weapon's own <c>t_attack</c> is what paces
        /// the rounds; the caller only decides whether the trigger is down <i>this</i> frame. So the latch
        /// is cleared at the top of every tick and re-armed by <see cref="ExecuteAttackAction"/>, which is
        /// exactly one frame of "held".</para>
        /// </summary>
        protected bool _weaponTriggerHeld;

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
            if (_controller == null || _controller.Definition == null)
            {
                return;
            }

            UnitDefinition def = _controller.Definition;

            _walkSpeed = _isWalker ? 2.5f : (def.moveSpeed > 0 ? def.moveSpeed : 2f);
            float runMult = def.runMultiplier > 0 ? def.runMultiplier : 2f;
            _runSpeed = _canDash ? _walkSpeed * 3f : _walkSpeed * runMult;

            _patrolSpeed = _walkSpeed;
            _chaseSpeed = _runSpeed;
            _alertSpeed = _runSpeed * 0.6f;

            if (def.isWalker) _isWalker = true;
            if (def.isSniper) _isSniper = true;
            if (def.detectionDistance > 0) _aiDist = def.detectionDistance;

            string defId = def.id != null ? def.id.ToLowerInvariant() : "";

            // AS3 sets `maxSpok` per class in the constructor, not from imported data: `Unit.as:362`
            // declares 30, and of the seven ArmedShooter classes only two override it —
            // `UnitEncl.as:25` and `UnitMerc.as:27`, both to 50. One 30 for the whole family made the
            // encl and merc variants commit to a target after 30 awareness ticks instead of 50, i.e. a
            // shorter and quieter engagement than the oracle's. `parentId` is the family id (`encl1` →
            // `encl`); the unit's own id is the fallback for a family template, which has no parent.
            string familyId = !string.IsNullOrEmpty(def.parentId) ? def.parentId : defId;
            _maxSpok = (familyId == "encl" || familyId == "merc") ? 50 : 30;

            // ── The attack branch, read off the weapon the controller rolled ──────────────────
            //
            // AS3 derives `attackerType` in the CONSTRUCTOR from the weapon it rolled and the unit's own
            // `krep` (`UnitRaider.as:257-272`), and every attack decision below branches on it. Both
            // halves live on the controller: the roll needs the weapon table (to skip an id
            // `Weapon.create` would reject) and the mount (to equip), neither of which a brain has.
            //
            // The block this replaces classified the unit from its ID — `raider1..4` melee,
            // `raider5..8` ranged, `raider9` thrown — and, for every other variant, from substrings of
            // the FIRST candidate's id (`contains "club"` → melee, `contains "grenade"` → thrown). Both
            // are inventions. `raider1`'s list is six melee candidates and `raider5`'s is five firearms,
            // but `raider2` mixes `bat`/`spear` with `mach`, `slaver1` mixes `pipe`/`cknife` with
            // `hunt`/`lshot`, and `zebra`/`encl`/`merc` are mixed too — the oracle's answer depends on
            // which candidate the difficulty-gated roll accepted, which is a per-spawn draw.
            //
            // 0 when there is no controller at all: the brain is then attached to something that is not
            // an ArmedShooter and has no weapon, which is AS3's own `!currentWeapon → 0` answer.
            _attackerType = ShooterController != null ? ShooterController.AttackerType : 0;
        }

        public override void SimTick(int tickIndex)
        {
            _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
            if (_aiAttackT > 0) _aiAttackT--;
            if (_aiTCh > 0) _aiTCh--;
            if (_aiSpok > 0 && tickIndex % 10 == 0) _aiSpok--;

            // Release last tick's trigger before the state machine runs. AS3's `attack()` is called once
            // per frame and decides from scratch whether `currentWeapon.attack()` happens, so a frame in
            // which no branch fires is a frame with the trigger up.
            //
            // This has to PUSH the release, not merely clear a local flag. `EnemyWeaponMount` latches
            // `_firing` until told otherwise, and `base.SimTick` ends with `TickWeaponMounts` — so a unit
            // that stops attacking (target lost, state drops to Alert/Patrol) would otherwise leave the
            // trigger held forever and keep shooting while it walks away. `ExecuteAttackAction` re-arms
            // this within the same tick, before the mount is advanced.
            _weaponTriggerHeld = false;
            SetWeaponTrigger(false);

            base.SimTick(tickIndex);

            UpdateDropThrough();
            UpdateJump(tickIndex);
        }

        /// <summary>
        /// Hold or release the rolled weapon's trigger, and keep its aim on the target.
        /// </summary>
        /// <remarks>
        /// <para><b>Aim is in Unity world units and must not be scaled.</b> The value feeds
        /// <c>WeaponHoldPointMath.Inputs.AimX</c> and <c>RangedWeaponController</c>'s rotation, both of
        /// which speak world units — the alicorn's <c>UpdateWeaponFire</c> documents the 45°-off bug a
        /// <c>* 100f</c> here produces. The <c>TargetDeltaX/Y</c> family really is in pixels; those are
        /// for the AI's range tests and never for the weapon.</para>
        ///
        /// <para><b>Re-read every firing tick</b>, not latched at the decision, so a shot follows a
        /// moving player — which is what the oracle does, since <c>attack()</c> resolves its direction
        /// from the unit's current target each frame.</para>
        /// </remarks>
        protected virtual void SetWeaponTrigger(bool firing)
        {
            ArmedShooterController ctrl = ShooterController;
            if (ctrl == null) return;

            EnemyWeaponMount mount = ctrl.WeaponMount;
            if (mount == null) return;

            if (firing && _blackboard.TargetUnit != null)
            {
                mount.SetAimTarget(_blackboard.TargetUnit.transform.position);
            }

            mount.SetFiring(firing);
        }

        protected virtual void UpdateDropThrough()
        {
            if (_controller == null) return;
            if (_familyState == 2 || _familyState == 3 || _familyState == 4)
            {
                bool targetBelow = _blackboard.HasLastKnownTargetPosition && _blackboard.TargetDeltaY > 80f;
                bool nearFloor = _tileQuery != null && UnitDropThroughMath.IsNearRoomFloor(transform.position.y * 100f, _tileQuery.OriginPixel.y);
                SetDropThroughPlatforms(targetBelow && !nearFloor);
            }
            else
            {
                SetDropThroughPlatforms(false);
            }
        }

        protected virtual void UpdateJump(int tickIndex)
        {
            if (_controller == null || !_controller.IsGrounded) return;
            if (_jumpCooldownTicks > 0) return;

            if ((_familyState == 2 || _familyState == 3 || _familyState == 8) && (tickIndex % 2 == 1))
            {
                bool targetAbove = _blackboard.HasLastKnownTargetPosition && _blackboard.TargetDeltaY < -40f;
                if (targetAbove)
                {
                    Vector2 currentPx = (Vector2)transform.position * 100f;
                    if (UnitJumpMath.ShouldJump(currentPx.y + 30f, _blackboard.LastKnownTargetPosition.y))
                    {
                        float jumpVelocity = _controller.Definition != null && _controller.Definition.jumpForce > 0
                            ? _controller.Definition.jumpForce
                            : 8f;
                        JumpVertical(jumpVelocity);
                        _jumpCooldownTicks = UnitJumpMath.CooldownTicks(UnityEngine.Random.Range(0, 50));
                    }
                }
            }
        }

        public override void Alarma(float sourceX = -1f, float sourceY = -1f)
        {
            base.Alarma(sourceX, sourceY);
            if (_familyState <= 1)
            {
                _aiSpok = _maxSpok + 10;
                _familyState = 3;
                SetState(EnemyAIState.CombatChase);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_aiSpok >= _maxSpok && _blackboard.TargetUnit != null)
            {
                _familyState = 3;
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                if (UnityEngine.Random.value < 0.5f)
                {
                    SetState(EnemyAIState.Patrol);
                    return;
                }
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                // Spotted deciding state
                _familyState = 5;
                _aiTCh = UnityEngine.Random.Range(0, 20) + _tupizna;
                SetState(EnemyAIState.Alert);
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
                _familyState = 5;
                _aiTCh = UnityEngine.Random.Range(0, 20) + _tupizna;
                SetState(EnemyAIState.Alert);
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
                SetState(EnemyAIState.Idle);
                return;
            }

            HandleLedgeAhead(tickIndex);
            float speed = _isWalker ? 2.5f : _walkSpeed;
            MoveHorizontal((_controller != null && _controller.FacingDirection < 0 ? -1f : 1f) * speed);
        }

        protected override void TickAlert(int tickIndex)
        {
            if (_familyState == 5)
            {
                StopMovement();
                if (_aiTCh <= 0)
                {
                    if (_blackboard.TargetUnit != null)
                    {
                        _familyState = 3;
                        SetState(EnemyAIState.CombatChase);
                    }
                    else
                    {
                        _familyState = 1;
                        SetState(EnemyAIState.Patrol);
                    }
                }
                return;
            }

            _familyState = 2;
            base.TickAlert(tickIndex);
        }

        protected override void TickCombatChase(int tickIndex)
        {
            // The hunt ends on the awareness budget, not on the sighting. `EnemySensors` nulls
            // `TargetUnit` on the first obscured tick, so the old `TargetUnit == null → Alert` exit
            // made every archetype forget the player the instant line of sight broke. See
            // `EnemyBrain.ChaseBudgetExhausted`.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                // Still hunting a target it cannot see: keep walking to the last place it was seen,
                // at chase speed, until the budget drains. AS3 `UnitRaider.control()`'s state-3 arm
                // steers on `celX`/`celY` exactly like this.
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            // State 4 (Hold / strafe) if at stand-off distance
            if (_attackerType == 2 && dist <= _aiDist && dist >= 80f)
            {
                _familyState = 4;
            }
            else if (_canDash && _familyState != 8 && dist <= 250f && dist >= 120f && UnityEngine.Random.value < 0.05f)
            {
                _familyState = 7; // Dash wind-up
                _aiTCh = 20;
            }
            else if (_familyState == 7)
            {
                StopMovement();
                if (_aiTCh <= 0)
                {
                    _familyState = 8; // Charge
                    _aiTCh = 35;
                }
                return;
            }
            else if (_familyState == 8)
            {
                if (_aiTCh <= 0)
                {
                    _familyState = 3;
                }
            }
            else
            {
                _familyState = 3;
            }

            // Facing towards target
            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            float speed = _familyState == 8 ? _runSpeed * 2.5f :
                          _familyState == 4 ? 0f :
                          _durak ? _runSpeed : _walkSpeed;

            if (speed > 0f)
            {
                HandleLedgeAhead(tickIndex);
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * speed);
            }
            else
            {
                StopMovement();
            }

            // Firing / attack
            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            float dist = _blackboard.TargetDistance;

            if (_attackerType == 1)
            {
                // AS3 `UnitRaider.attack()`'s melee-weapon gate (`:1496-1499`):
                // `Math.abs(celDX) < 120 && Math.abs(celDY) < currentWeapon.rapid * 8`.
                //
                // The vertical term is the WEAPON's own swing duration times 8 px, not a constant —
                // which is the whole reason a knife (rapid 12 → 96 px) reaches higher and lower than a
                // slow club (rapid 24 → 192 px). The port used a flat 60 px for both. 60 is kept as the
                // fallback for a unit whose mount could not be built (a bare test spawn), so the range
                // test never depends on weapon services existing.
                float rapid = ShooterController != null && ShooterController.WeaponMount != null
                              && ShooterController.WeaponMount.Definition != null
                    ? ShooterController.WeaponMount.Definition.rapid
                    : 0f;
                float yLimit = rapid > 0f ? rapid * 8f : 60f;

                return Mathf.Abs(_blackboard.TargetDeltaX) <= 120f
                    && Mathf.Abs(_blackboard.TargetDeltaY) <= yLimit;
            }

            if (_attackerType == 0 || _attackerType == 3)
            {
                return dist <= OptDistAttackPixels && Mathf.Abs(_blackboard.TargetDeltaY) <= 80f;
            }

            // Ranged. AS3 engages on `aiSpok >= maxSpok + 8 && dist² < aiDist²` (`:756`) — the awareness
            // budget, not a sighting — plus the `isLaz == 0` groundedness gate on the firing branch. The
            // port's `HasLineOfSight` here is stricter than the oracle's and is recorded rather than
            // changed: the ranged branch already requires a target to aim at, and loosening the range
            // test without the `aiAttackOch` work below would fire at nothing.
            return dist <= _aiDist && _blackboard.HasLineOfSight;
        }

        /// <summary>
        /// AS3 <c>UnitRaider.attack()</c> (<c>UnitRaider.as:1483-1553</c>), branch for branch.
        /// </summary>
        /// <remarks>
        /// <para><b>Every branch here calls <c>currentWeapon.attack()</c>, and the port's equivalent is
        /// <see cref="SetWeaponTrigger"/></b> — holding the mount's trigger for this tick and aiming it.
        /// The weapon controller's own <c>t_attack</c> paces the individual rounds, exactly as in the
        /// oracle, so nothing here decides when a bullet leaves.</para>
        ///
        /// <para><b>What this replaced.</b> The old body called <c>TryContactAttack</c> for attackerType 1
        /// (so a spear-armed <c>raider2</c> body-slammed instead of swinging) and, for 2 and 3, fabricated
        /// the damage outright with <c>PendingDamage.Direct</c> — no projectile, no flare, no report, and a
        /// damage number invented from <c>UnitDefinition.damage</c>. The <c>Direct</c> calls survive only
        /// as the fallback for a unit with no mount.</para>
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;
            if (_blackboard.TargetUnit == null) return;

            // AS3 tests `shok <= 0` inside each branch; hoisting it is equivalent for every branch that
            // exists here, because all four require a live target anyway.
            if (StaggerMath.BlocksAttack(_blackboard.ShockTimerTicks))
            {
                return;
            }

            // `if((attackerType == 0 || aiState == 8) && celUnit && shok <= 0)` — the contact arm, which
            // also owns the state-8 charge: a charging unit damages with its body whatever it is holding.
            if (_attackerType == 0 || _familyState == 8)
            {
                // `attKorp(celUnit, Math.abs(dx) > 8 ? 1 : 0.5)` — a body moving faster than 8 px/frame
                // hits for full scale, a standing one for half. `dx` is the unit's own per-frame x
                // velocity, which is what the motor's State.Velocity carries.
                float speed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                _controller.TryContactAttack(_blackboard.TargetUnit, speed > 8f ? 1f : 0.5f);
                return;
            }

            switch (_attackerType)
            {
                case 1: AttackWithMeleeWeapon(); break;
                case 2: AttackWithRangedWeapon(); break;
                case 3: AttackWithThrownWeapon(); break;
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 1</c> arm (<c>:1496-1506</c>) — swing the held <c>tip == 1</c> weapon,
        /// then take the independent point-blank body hit.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(Math.abs(celDX) &lt; 120 &amp;&amp; Math.abs(celDY) &lt; currentWeapon.rapid * 8 &amp;&amp; shok &lt;= 0 &amp;&amp; isrnd(0.3))
        ///    currentWeapon.attack();
        /// if(isrnd(0.1))
        ///    attKorp(celUnit, 0.5);
        /// </code>
        ///
        /// <para><b>The 0.3 is a per-frame trigger probability, not a fire rate.</b> The oracle calls
        /// <c>attack()</c> every frame and lets a 30 % roll decide whether the trigger is down; the
        /// weapon's own <c>rapid_act</c> is what paces the swings. So this must be a per-tick roll — a
        /// one-shot decision would swing once per approach.</para>
        /// </remarks>
        protected virtual void AttackWithMeleeWeapon()
        {
            // A unit whose mount could not be built (a bare test spawn, or a missing WeaponDefinition)
            // keeps the old body hit rather than becoming harmless. Gated on range so it is not a
            // per-tick damage aura.
            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                if (IsTargetInAttackRange())
                {
                    _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
                }
                return;
            }

            if (IsTargetInAttackRange() && UnityEngine.Random.value < 0.3f)
            {
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);
            }

            // The body hit rides alongside the swing and is not gated on the swing's roll or on range.
            if (UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 2</c> arm (<c>:1507-1541</c>) — fire the held weapon on an
        /// <c>aiAttackOch</c> duty cycle.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(!this.sniper) mazil = aiState == 4 ? 5 : 16;
        /// if(this.aiAttackOch == 0 &amp;&amp; shok &lt;= 0 &amp;&amp; (celUnit != null &amp;&amp; isrnd(0.1) || celUnit == null &amp;&amp; isrnd(0.03)))
        ///    currentWeapon.attack();
        /// if(this.aiAttackOch &gt; 0 &amp;&amp; (!this.sniper || celUnit)) {
        ///    if(this.aiAttackT &lt;= 0) this.aiAttackT = Math.round((Math.random() * 0.4 + 0.8) * this.aiAttackOch);
        ///    if(this.aiAttackT &gt; this.aiAttackOch * 0.25) currentWeapon.attack();
        ///    --this.aiAttackT;
        /// }
        /// if(dist² &lt; 100² &amp;&amp; isrnd(0.1)) attKorp(celUnit, 0.5);
        /// </code>
        ///
        /// <para><b>The <c>aiAttackT &gt; och * 0.25</c> test is a duty cycle, and it is what the port
        /// was missing.</b> <c>aiAttackT</c> is re-armed to 0.8–1.2 × <c>aiAttackOch</c> and then counts
        /// down, so the trigger is held for the top ~75 % of each window and released for the bottom
        /// quarter. The port fired a single fabricated hit at the window's start instead, which is both
        /// the wrong shape and the wrong delivery.</para>
        ///
        /// <para><b><c>mazil</c> is not modelled.</b> The oracle raises the shooter's inaccuracy to 16 px
        /// (5 while holding position, 25 while levitating). That value reaches the weapon through
        /// <c>Pers.mazilAdd</c>, and an NPC has no stat source in this port — so the spread is currently
        /// the weapon's own. Recorded in <c>ThrownWeaponController.Mazil</c> and in the worklog; it needs
        /// an NPC stat source, not a hard-coded literal here.</para>
        /// </remarks>
        protected virtual void AttackWithRangedWeapon()
        {
            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                // No mount to fire through. Keep the old direct report, on the window cadence, so a unit
                // whose weapon could not be equipped is not completely harmless — and so this is visibly
                // the fallback rather than a second damage path that could double-hit.
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = Mathf.RoundToInt(
                        (UnityEngine.Random.value * 0.4f + 0.8f) * _aiAttackOch);
                    FireRangedAttack();
                }
                return;
            }

            if (_aiAttackOch > 0)
            {
                if (_aiAttackT <= 0)
                {
                    // AS3's formula, verbatim. The port used to substitute a flat 40 for a sniper; the
                    // oracle has no such special case — its only sniper test is `(!this.sniper || celUnit)`,
                    // which is already satisfied here because a target is required to get this far.
                    _aiAttackT = Mathf.RoundToInt(
                        (UnityEngine.Random.value * 0.4f + 0.8f) * _aiAttackOch);
                }

                if (_aiAttackT > _aiAttackOch * 0.25f)
                {
                    _weaponTriggerHeld = true;
                    SetWeaponTrigger(true);
                }
            }
            else if (UnityEngine.Random.value < 0.1f)
            {
                // `aiAttackOch == 0` — the constant-free branch, a 10 % per-frame trigger.
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);
            }

            if (_blackboard.TargetDistance <= 100f && UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 3</c> arm (<c>:1542-1552</c>) — throw, then take the point-blank body
        /// hit.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(Boolean(celUnit) &amp;&amp; isrnd(0.02)) {
        ///    currentWeapon.attack();
        ///    if(currentWeapon is WThrow &amp;&amp; (currentWeapon as WThrow).kolAmmo &lt;= 0) this.attackerType = 0;
        /// }
        /// if(dist² &lt; 100² &amp;&amp; isrnd(0.1)) attKorp(celUnit, Math.abs(dx) &gt; 8 ? 1 : 0.5);
        /// </code>
        ///
        /// <para><b>The reclassification is ported, and it is why <c>RemainingThrows</c> exists.</b> An NPC
        /// thrower carries four rounds (<c>WThrow.kolAmmo</c>); once they are gone AS3 demotes it to a
        /// contact attacker, so it charges instead of standing at range holding nothing. Note the oracle
        /// checks the counter immediately after <c>attack()</c>, in the same frame — <c>getAmmo()</c> has
        /// already decremented it by then, so a throw that spends the last round flips the branch on the
        /// spot.</para>
        /// </remarks>
        protected virtual void AttackWithThrownWeapon()
        {
            float speed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);

            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                // No mount: the old direct report, on the port's own 60–100 tick cadence (there is no
                // oracle cadence to copy for a throw that has no weapon object to throw).
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = UnityEngine.Random.Range(60, 100);
                    FireThrownAttack();
                }
                return;
            }

            if (UnityEngine.Random.value < 0.02f)
            {
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);

                if (ShooterController.RemainingThrows == 0)
                {
                    _attackerType = 0;
                }
            }

            if (_blackboard.TargetDistance <= 100f && UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, speed > 8f ? 1f : 0.5f);
            }
        }

        /// <summary>
        /// Fallback ranged report for a unit whose weapon mount could not be built — a bare test spawn, or
        /// an id with no <see cref="WeaponDefinition"/>.
        /// </summary>
        /// <remarks>
        /// <b>This is no longer the attack.</b> The real path is the rolled weapon's mount, fired through
        /// <see cref="SetWeaponTrigger"/> — a projectile with its own visual, flare and report. This
        /// fabricated <c>PendingDamage.Direct</c> used to BE the ranged attack for every ArmedShooter,
        /// which is why "shooting enemies do not have a weapon" was literally true: nothing was drawn or
        /// played, only a damage number applied. Kept only so a unit whose weapon failed to equip is not
        /// harmless, and gated on the weapon window's cadence so it cannot become a per-tick damage aura.
        /// </remarks>
        protected virtual void FireRangedAttack()
        {
            if (_blackboard.TargetUnit == null || _controller == null) return;

            // Emit noise
            _controller.MakeNoise(250, true);

            // Directly report simulated ranged combat or contact impact
            float baseDamage = _controller.Definition != null ? _controller.Definition.damage : 10f;
            DamageType dType = _controller.Definition != null ? _controller.Definition.damageType : DamageType.PhysicalBullet;

            Vector2 origin = (Vector2)transform.position * 100f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, dType),
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

        /// <summary>
        /// Fallback thrown report for a unit whose weapon mount could not be built. See
        /// <see cref="FireRangedAttack"/> — same status, same reason.
        /// </summary>
        protected virtual void FireThrownAttack()
        {
            if (_blackboard.TargetUnit == null || _controller == null) return;
            _controller.MakeNoise(180, true);

            float baseDamage = _controller.Definition != null ? _controller.Definition.damage * 1.5f : 15f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, DamageType.Explosive),
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

        /// <summary>
        /// Animation state resolution matching AS3 UnitRaider.animate() branch order (UnitRaider.as:399-484).
        /// </summary>
        /// <summary>
        /// A port of <c>UnitRaider.animate()</c> (<c>UnitRaider.as:400-484</c>) — the family brain for
        /// <c>raider</c>, <c>slaver</c>, <c>zebra</c>, <c>ranger</c>, <c>encl</c>, <c>merc</c> and
        /// <c>necros</c>.
        ///
        /// <para><b>No <c>trup</c> gate on the death arm</b> — <c>grep -c trup UnitRaider.as</c> is 0, so
        /// unlike <c>UnitAnt</c> and <c>UnitMonstrik</c> this arm is always reachable, and every raider
        /// sheet does author <c>die</c>, <c>death</c> and <c>fall</c>.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                // UnitRaider.as:402-422 — `if(stay) { if(animState != "fall") { if(animState == "death")
                // "fall"; else "die"; } } else "death";`.
                //
                // The `animState != "fall"` guard is load-bearing and was missing: a body that is already
                // playing `fall` is left alone. Without it a grounded corpse flipped `fall` back to `die`,
                // restarting the death animation every frame instead of settling.
                if (!_controller.IsGrounded) return "death";
                if (ShowingAnimState("fall")) return "fall";
                return ShowingAnimState("death") ? "fall" : "die";
            }

            float currentSpeed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);

            // `else if(stay)`: dx == 0 or aiState == 7 -> stay; attacking (attackerType 0) or aiState 8
            // -> run; a walker patrolling -> walk; otherwise -> trot.
            if (_controller.IsGrounded)
            {
                if (currentSpeed < 0.1f || _familyState == 7)
                {
                    return "stay";
                }

                if ((_attackerType == 0 && _currentState == EnemyAIState.CombatChase) || _familyState == 8)
                {
                    return "run";
                }

                if (_isWalker && (_familyState <= 1 || _familyState == 4))
                {
                    return "walk";
                }

                return "trot";
            }

            // `else if(this.flyer) { if(isFly) "derg"; else "stay"; }` (UnitRaider.as:445-455).
            //
            // `derg` is a REAL row — raider, slaver and zebra each author one
            // (AllData.as:84/218/320) — and it used to be parser-dropped, which is why this returned the
            // invented `"fly"`. `"fly"` is not one of UnitRaider.animate()'s ids at all and no raider
            // sheet authors it, so the old return could only ever blank the sprite.
            //
            // ⚠ BOTH halves of the oracle's test are still missing, so this arm stays unreachable:
            // `_isFlyer` is declared and never assigned (the `<param>` attribute that would set it is not
            // imported), and the port has no equivalent of AS3's runtime `isFly`. The branch is kept
            // faithful rather than deleted so those two missing writers are visible exactly where they
            // are needed, and it takes the oracle's `isFly` arm — a unit that is a flyer and is airborne
            // is flying.
            if (_isFlyer)
            {
                return "derg";
            }

            // AS3's remaining arms — `levit` -> `derg`, `isLaz && mostLaz` -> `laz`, `aiPlav` -> `plav` —
            // are not ported: this brain has no levitate, wall-grip or swim state to read, so they have
            // no trigger to hang off. `jump` is the oracle's own final `else` and is what a raider in
            // free fall actually plays.
            return "jump";
        }
    }
}

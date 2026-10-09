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
        /// AS3 attackerType classification (UnitRaider.as:257-272):
        /// 0: Melee contact / club (weaponKrep == 1)
        /// 1: Point-blank melee (weaponKrep == 0)
        /// 2: Ranged firearm
        /// 3: Thrown weapon
        /// </summary>
        [SerializeField]
        protected int _attackerType = 2;

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

            // Classify weapon / attackerType (UnitRaider.as:257-272)
            if (defId.StartsWith("raider"))
            {
                if (defId == "raider1" || defId == "raider2" || defId == "raider3" || defId == "raider4")
                {
                    _attackerType = 0;
                }
                else if (defId == "raider5" || defId == "raider6" || defId == "raider7" || defId == "raider8")
                {
                    _attackerType = 2;
                }
                else if (defId == "raider9")
                {
                    _attackerType = 3;
                }
            }
            else if (def.weapons != null && def.weapons.Length > 0 && !string.IsNullOrEmpty(def.weapons[0].weaponId))
            {
                string wid = def.weapons[0].weaponId.ToLowerInvariant();
                if (wid.Contains("club") || wid.Contains("knife") || wid.Contains("axe") || wid.Contains("bat") || wid.Contains("sword"))
                {
                    _attackerType = 0;
                }
                else if (wid.Contains("grenade") || wid.Contains("throw") || wid.Contains("bomb") || wid.Contains("molotov"))
                {
                    _attackerType = 3;
                }
                else
                {
                    _attackerType = 2;
                }
            }
            else
            {
                // Default to contact if no weapons defined
                _attackerType = 0;
            }
        }

        public override void SimTick(int tickIndex)
        {
            _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
            if (_aiAttackT > 0) _aiAttackT--;
            if (_aiTCh > 0) _aiTCh--;
            if (_aiSpok > 0 && tickIndex % 10 == 0) _aiSpok--;

            base.SimTick(tickIndex);

            UpdateDropThrough();
            UpdateJump(tickIndex);
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
            if (_blackboard.TargetUnit == null)
            {
                SetState(EnemyAIState.Alert);
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

            if (_attackerType == 0 || _attackerType == 3)
            {
                return dist <= OptDistAttackPixels && Mathf.Abs(_blackboard.TargetDeltaY) <= 80f;
            }
            else if (_attackerType == 1)
            {
                return Mathf.Abs(_blackboard.TargetDeltaX) <= 120f && Mathf.Abs(_blackboard.TargetDeltaY) <= 60f;
            }
            else // Ranged
            {
                return dist <= _aiDist && _blackboard.HasLineOfSight;
            }
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            // Stagger blocks attack
            if (StaggerMath.BlocksAttack(_blackboard.ShockTimerTicks))
            {
                return;
            }

            if (_attackerType == 0)
            {
                // Contact melee attack
                float speed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                float scale = speed > 8f ? 1f : 0.5f;
                _controller.TryContactAttack(_blackboard.TargetUnit, scale);
            }
            else if (_attackerType == 1)
            {
                // Point blank melee / club
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
            else if (_attackerType == 2)
            {
                // Ranged attack cadence (UnitRaider.as:1525-1536)
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = _isSniper ? 40 : Mathf.RoundToInt((UnityEngine.Random.value * 0.4f + 0.8f) * _aiAttackOch);
                    FireRangedAttack();
                }
            }
            else if (_attackerType == 3)
            {
                // Thrown attack
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = UnityEngine.Random.Range(60, 100);
                    FireThrownAttack();
                }
            }
        }

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

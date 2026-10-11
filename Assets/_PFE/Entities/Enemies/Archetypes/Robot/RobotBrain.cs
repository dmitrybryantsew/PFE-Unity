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
    public enum RobotKind
    {
        Standard = 0,   // Robobrain, Protectron, Gutsy, Sentinel
        Drone = 1,      // Dron1..3, Dront (flying 2D)
        Roller = 2,     // Roller, Roller2 (ground ram)
        Vortex = 3,     // Vortex (puller)
        SpriteBot = 4,  // SpriteBot (zap)
        Msp = 5         // Msp (stationary wake-and-fire)
    }

    /// <summary>
    /// Robot enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitAIRobot.as</c> and small robot classes
    /// (UnitDron, UnitGutsy, UnitProtect, UnitSentinel, UnitSpriteBot, UnitVortex, UnitRoller, UnitMsp).
    /// </summary>
    public class RobotBrain : EnemyBrain
    {
        [SerializeField] protected RobotKind _kind = RobotKind.Standard;
        /// <summary>
        /// Burst cadence in ticks — the robot family's <c>aiAttackOch</c> (<c>UnitAIRobot.as</c>).
        ///
        /// <para><b>A constant standing in for an unimported attribute, not a tuned value.</b> AS3 takes
        /// this from the unit's own <c>&lt;un och=…&gt;</c> node (the same read as the raider's,
        /// <c>UnitRaider.as:319-321</c>), and <c>UnitExtrasParser</c> deliberately imports only
        /// <c>res</c> from that node — see its remarks. Nothing should "balance" this number; closing the
        /// <c>&lt;un&gt;</c> import is what replaces it.</para>
        /// </summary>
        [SerializeField] protected float _burstCooldownTicks = 40f;
        [SerializeField] protected float _combatRangePixels = 400f;

        protected int _aiTCh;
        protected int _attackTimer;
        protected int _familyState;
        protected bool _isDormant;

        public RobotKind Kind { get => _kind; set => _kind = value; }
        public int FamilyState => _familyState;

        protected override bool StopsToAttack => _kind == RobotKind.Standard;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 2f;
            _patrolSpeed = spd;
            _chaseSpeed = _kind == RobotKind.Roller ? spd * 2.5f : spd * 1.8f;
            _alertSpeed = spd * 1.2f;

            if (def.detectionDistance > 0)
            {
                _combatRangePixels = def.detectionDistance;
            }

            if (_kind == RobotKind.Msp)
            {
                _isDormant = true;
            }
        }

        /// <summary>
        /// Robot network Budilo: reaches robot brains by pure distance without the receiver ear multiplier,
        /// ensuring ear=0 robots (spritebot, vortex, roller) receive alarms (02_SHARED_SYSTEMS.md §3).
        /// </summary>
        protected override void Budilo(float radius = 500f)
        {
            _controller?.MakeNoise((int)(NoiseMath.DefaultNoiseRun * 1.2f), true);

            var hits = Physics2D.OverlapCircleAll(transform.position, radius * TileQueryConstants.PixelToUnit);
            foreach (var hit in hits)
            {
                if (hit != null && hit.TryGetComponent(out RobotController rc) && rc.Brain != null && rc.IsAlive)
                {
                    rc.Brain.RaiseRobotAlarm((Vector2)transform.position * 100f);
                }
            }
        }

        public void RaiseRobotAlarm(Vector2 noisePos)
        {
            _isDormant = false;
            _blackboard.HasHeardNoise = true;
            _blackboard.LastHeardNoisePosition = noisePos;
            _blackboard.AlertTimerTicks = 60;
            SetState(EnemyAIState.Alert);
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;
            if (_attackTimer > 0) _attackTimer--;

            base.SimTick(tickIndex);

            if (_kind == RobotKind.Drone && _controller != null && _controller.IsAlive)
            {
                UpdateDroneHover(tickIndex);
            }
        }

        protected virtual void UpdateDroneHover(int tickIndex)
        {
            // Drone 2D altitude adjustment
            if (_blackboard.HasLastKnownTargetPosition)
            {
                float targetDy = _blackboard.TargetDeltaY;
                float desiredY = targetDy + 50f;
                if (desiredY > 15f) JumpVertical(2f);
                else if (desiredY < -15f) JumpVertical(-1.5f);
            }
            else
            {
                float wave = Mathf.Sin(tickIndex * 0.15f) * 1.2f;
                JumpVertical(wave);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_isDormant)
            {
                StopMovement();
                if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
                {
                    _isDormant = false;
                    _familyState = 4; // wake state
                    _aiTCh = 25;
                }
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                _familyState = 5; // spotted pause
                _aiTCh = UnityEngine.Random.Range(20, 50);
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
                _aiTCh = UnityEngine.Random.Range(20, 50);
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
            float dir = (_controller != null && _controller.FacingDirection < 0) ? -1f : 1f;
            MoveHorizontal(dir * _patrolSpeed);
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
            _familyState = 3;

            // The hunt ends on the awareness budget, not on the sighting — see
            // `EnemyBrain.ChaseBudgetExhausted`. Exiting on `TargetUnit == null` made every archetype
            // forget the player on the first obscured tick.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            if (_kind == RobotKind.Roller)
            {
                // Charge straight at target to ram
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }
            else if (_kind == RobotKind.Vortex)
            {
                // Pull target toward vortex
                if (dist > 120f)
                {
                    float dir = dx >= 0 ? 1f : -1f;
                    MoveHorizontal(dir * _patrolSpeed);
                }
                else
                {
                    StopMovement();
                }
            }
            else if (_kind == RobotKind.Msp)
            {
                // Stationary turret-like
                StopMovement();
            }
            else
            {
                // Standard & Drone stand-off firing
                if (dist > _combatRangePixels * 0.7f)
                {
                    float dir = dx >= 0 ? 1f : -1f;
                    MoveHorizontal(dir * _chaseSpeed);
                }
                else if (dist < 100f)
                {
                    float dir = dx >= 0 ? -1f : 1f;
                    MoveHorizontal(dir * _patrolSpeed);
                }
                else
                {
                    StopMovement();
                }
            }

            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            float dist = _blackboard.TargetDistance;

            if (_kind == RobotKind.Roller)
            {
                return dist <= 100f && Mathf.Abs(_blackboard.TargetDeltaY) <= 50f;
            }
            return dist <= _combatRangePixels && _blackboard.HasLineOfSight;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_kind == RobotKind.Roller)
            {
                // Contact ram attack
                _controller.TryContactAttack(_blackboard.TargetUnit, 1.2f);
                return;
            }

            if (_attackTimer > 0) return;
            _attackTimer = Mathf.RoundToInt(_burstCooldownTicks > 0 ? _burstCooldownTicks : 40f);

            _controller.MakeNoise(250, true);

            float baseDamage = _controller.Definition != null ? _controller.Definition.damage : 12f;
            DamageType dType = _controller.Definition != null ? _controller.Definition.damageType : DamageType.Laser;

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

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return _controller.IsGrounded ? "fall" : "die";
            }

            if (_isDormant)
            {
                return "stay";
            }

            if (_kind == RobotKind.Drone)
            {
                return "fly";
            }

            if (_kind == RobotKind.Msp && _familyState == 4)
            {
                return "stay";
            }

            if (_controller.IsGrounded)
            {
                float spd = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                if (spd < 0.1f) return "stay";
                return _currentState == EnemyAIState.CombatChase ? "run" : "walk";
            }

            return "stay";
        }
    }
}

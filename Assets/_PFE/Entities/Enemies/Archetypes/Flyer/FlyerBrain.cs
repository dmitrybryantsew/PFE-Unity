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
    public enum FlyerKind
    {
        Bloat = 0,      // UnitBloat (drift, spit, contact, death emit)
        Bat = 1,        // UnitBat (6-state ladder with dive attack)
        Phoenix = 2,    // UnitPhoenix (tamed prop)
        Emitter = 3     // UnitBloatEmitter (nest spawner)
    }

    /// <summary>
    /// Flyer enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitBloat.as</c>, <c>UnitBat.as</c>,
    /// <c>UnitPhoenix.as</c>, and <c>UnitBloatEmitter.as</c>.
    /// </summary>
    public class FlyerBrain : EnemyBrain
    {
        [SerializeField] protected FlyerKind _kind = FlyerKind.Bloat;
        [SerializeField] protected float _flySpeed = 3f;

        protected int _aiTCh;
        protected int _attackTimer;
        protected int _familyState; // Bloat: 0..2; Bat: 0..6
        protected Vector2 _driftDir;

        public FlyerKind Kind { get => _kind; set => _kind = value; }
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
            if (_controller == null || _controller.Definition == null) return;
            UnitDefinition def = _controller.Definition;

            _flySpeed = def.moveSpeed > 0 ? def.moveSpeed : 3f;
            _patrolSpeed = _flySpeed;
            _chaseSpeed = _flySpeed * 1.5f;
            _alertSpeed = _flySpeed * 1.2f;

            _driftDir = UnityEngine.Random.insideUnitCircle.normalized;
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;
            if (_attackTimer > 0) _attackTimer--;

            base.SimTick(tickIndex);

            if (_controller != null && _controller.IsAlive)
            {
                UpdateFlyerMovement(tickIndex);
            }
        }

        protected virtual void UpdateFlyerMovement(int tickIndex)
        {
            if (_kind == FlyerKind.Emitter)
            {
                StopMovement();
                return;
            }

            if (_kind == FlyerKind.Phoenix)
            {
                StopMovement();
                return;
            }

            // Bat dive attack
            if (_kind == FlyerKind.Bat && _familyState == 6)
            {
                if (_blackboard.HasLastKnownTargetPosition)
                {
                    float dx = _blackboard.TargetDeltaX;
                    float dy = _blackboard.TargetDeltaY;
                    MoveHorizontal((dx >= 0 ? 1f : -1f) * _chaseSpeed * 2.5f);
                    JumpVertical(dy > 0 ? -3f : 3f);
                }
                return;
            }

            // Stall / hover (bat state 5)
            if (_kind == FlyerKind.Bat && _familyState == 5)
            {
                StopMovement();
                JumpVertical(Mathf.Sin(tickIndex * 0.2f) * 0.5f);
                return;
            }

            // General 2D flight adjustments
            if (_blackboard.HasLastKnownTargetPosition)
            {
                float dy = _blackboard.TargetDeltaY;
                if (dy > 30f) JumpVertical(-1.8f);
                else if (dy < -30f) JumpVertical(2f);
                else JumpVertical(Mathf.Sin(tickIndex * 0.1f) * 0.8f);
            }
            else
            {
                JumpVertical(Mathf.Sin(tickIndex * 0.1f) * 1f);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(30, 70);
                _driftDir = UnityEngine.Random.insideUnitCircle.normalized;
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
                _aiTCh = UnityEngine.Random.Range(30, 70);
                SetState(EnemyAIState.Idle);
                return;
            }

            MoveHorizontal(_driftDir.x * _patrolSpeed);
            JumpVertical(_driftDir.y * 1.5f);
        }

        protected override void TickAlert(int tickIndex)
        {
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

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            if (_kind == FlyerKind.Bat)
            {
                // Bat state progression: chase -> hover -> dive
                if (_familyState == 6)
                {
                    // Dive attack active
                    if (_aiTCh <= 0)
                    {
                        _familyState = 4;
                        _aiTCh = UnityEngine.Random.Range(60, 100);
                    }
                }
                else if (_familyState == 5)
                {
                    // Hover before dive
                    if (_aiTCh <= 0)
                    {
                        _familyState = 6; // Dive!
                        _aiTCh = 20;
                    }
                }
                else if (dist < 380f && UnityEngine.Random.value < 0.05f)
                {
                    _familyState = 5; // Hover
                    _aiTCh = 20;
                }
                else
                {
                    _familyState = 4;
                    float dir = dx >= 0 ? 1f : -1f;
                    MoveHorizontal(dir * _chaseSpeed);
                }
            }
            else
            {
                // Bloat combat chase: drift towards target
                _familyState = 2;
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
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

            if (_kind == FlyerKind.Bat)
            {
                return dist <= 80f;
            }
            // Bloat can attack via contact or spit up to 300 px
            return dist <= 300f && _blackboard.HasLineOfSight;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_kind == FlyerKind.Bat)
            {
                // Dive attack does 2x damage and inflicts bleed
                float scale = _familyState == 6 ? 2f : 1f;
                _controller.TryContactAttack(_blackboard.TargetUnit, scale);
                return;
            }

            if (_kind == FlyerKind.Bloat)
            {
                float dist = _blackboard.TargetDistance;
                if (dist <= 60f)
                {
                    _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
                }
                else if (_attackTimer <= 0)
                {
                    _attackTimer = UnityEngine.Random.Range(35, 60);
                    _controller.MakeNoise(100, true);

                    float baseDamage = _controller.Definition != null ? _controller.Definition.damage : 8f;
                    Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

                    var pending = PendingDamage.Direct(
                        DamageContext.Contact(baseDamage, DamageType.Acid),
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
            }
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return _controller.IsGrounded ? "fall" : "die";
            }

            if (_kind == FlyerKind.Bat)
            {
                return "fly";
            }

            if (_kind == FlyerKind.Phoenix)
            {
                return "stay";
            }

            return "stay";
        }
    }
}

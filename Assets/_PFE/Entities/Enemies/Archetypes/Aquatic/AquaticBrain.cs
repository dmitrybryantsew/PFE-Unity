using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Aquatic enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitFish.as</c>.
    /// Water-locked swimmer with an 8-state ladder, water-gated target commitment, and leap attack.
    /// </summary>
    public class AquaticBrain : EnemyBrain
    {
        [SerializeField] protected int _familyState; // 0..8 per UnitFish.as
        protected int _aiTCh;
        protected int _aiSpok;
        protected const int MaxSpok = 30;

        public int FamilyState => _familyState;
        public int AiSpok { get => _aiSpok; set => _aiSpok = value; }

        protected override bool StopsToAttack => false;

        public bool IsSubmerged
        {
            get
            {
                if (_motor != null)
                {
                    return _motor.State.IsFullySubmerged || _motor.State.IsInWater;
                }
                return false;
            }
        }

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
            float runMult = def.runMultiplier > 0 ? def.runMultiplier : 2f;

            _patrolSpeed = spd * 0.2f;
            _chaseSpeed = spd * runMult;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;

            // Tick ladder transitions (UnitFish.as:150-209)
            if (_familyState == 6 && _aiTCh <= 0)
            {
                _familyState = 4;
            }
            else if (_familyState == 7 && IsSubmerged)
            {
                _familyState = 4;
            }
            else if (_familyState == 5 && _aiTCh <= 0)
            {
                _familyState = 6;
                _aiTCh = 20;
            }

            // Target gate: fish only commits if target is in water (UnitFish.as:212-219)
            if (_blackboard.TargetUnit != null)
            {
                bool targetInWater = _blackboard.TargetUnit.IsInWater || _blackboard.TargetUnit.IsFullySubmerged;
                if (targetInWater)
                {
                    _aiSpok = MaxSpok + 10;
                }
                else
                {
                    if (tickIndex % 10 == 0 && _aiSpok > 0)
                    {
                        _aiSpok--;
                    }
                }
            }
            else
            {
                if (tickIndex % 10 == 0 && _aiSpok > 0)
                {
                    _aiSpok--;
                }
            }

            // Beached flop (UnitFish.as:244-252)
            if (!IsSubmerged && _controller != null && _controller.IsAlive)
            {
                if (tickIndex % 10 == 0 && _controller.IsGrounded)
                {
                    // Leap flop (positive = up)
                    JumpVertical(UnityEngine.Random.Range(5f, 15f));
                    MoveHorizontal((UnityEngine.Random.value - 0.5f) * 4f);
                }
            }

            base.SimTick(tickIndex);
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_aiSpok >= MaxSpok && _blackboard.TargetUnit != null)
            {
                _familyState = 4;
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_aiSpok > 0)
            {
                _familyState = 2;
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            StopMovement();
        }

        protected override void TickPatrol(int tickIndex)
        {
            _familyState = 1;
            if (_aiSpok >= MaxSpok && _blackboard.TargetUnit != null)
            {
                _familyState = 4;
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_aiSpok > 0)
            {
                _familyState = 2;
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Idle);
                return;
            }

            float dir = (_controller != null && _controller.FacingDirection < 0) ? -1f : 1f;
            MoveHorizontal(dir * _patrolSpeed);
        }

        protected override void TickAlert(int tickIndex)
        {
            _familyState = 2;
            if (_aiSpok >= MaxSpok && _blackboard.TargetUnit != null)
            {
                _familyState = 4;
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_aiSpok <= 0)
            {
                SetState(EnemyAIState.Patrol);
                return;
            }

            base.TickAlert(tickIndex);
        }

        protected override void TickCombatChase(int tickIndex)
        {
            // The fish keeps its OWN commitment ladder (`_aiSpok`, armed only while the target is in
            // water — `UnitFish.as:212-219`), so the budget half stays as it was. The SIGHTING half
            // had to go: `EnemySensors` nulls `TargetUnit` on the first obscured tick, so
            // `TargetUnit == null` exited the chase immediately and the fish forgot the player the
            // moment it swam behind anything. See `EnemyBrain.ChaseBudgetExhausted`.
            if (_aiSpok <= 0)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dx = _blackboard.TargetDeltaX;
            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            float speed;
            switch (_familyState)
            {
                case 5:
                    speed = 0f;
                    break;
                case 6:
                    speed = _chaseSpeed * 2.5f;
                    break;
                case 8:
                    speed = _patrolSpeed * 0.5f;
                    break;
                default:
                    speed = _chaseSpeed;
                    break;
            }

            float dir = dx >= 0 ? 1f : -1f;
            if (_familyState == 8) dir = -dir; // Reposition/flee

            MoveHorizontal(dir * speed);

            if (_familyState == 4 && _blackboard.TargetDistance <= 380f && UnityEngine.Random.value < 0.5f)
            {
                _familyState = 5;
                _aiTCh = 20;
            }

            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 80f && Mathf.Abs(_blackboard.TargetDeltaY) <= 50f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            float damageScale = _familyState == 6 ? 2f : 1f;
            bool hit = _controller.TryContactAttack(_blackboard.TargetUnit, damageScale);

            if (hit && UnityEngine.Random.value < 0.25f)
            {
                // Reposition roll after landed hit (UnitFish.as:298-302)
                _familyState = 8;
                _aiTCh = 40;
            }
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return "die";
            }

            if (IsSubmerged)
            {
                if (_familyState <= 1) return "plav";
                return "run";
            }

            return "stay";
        }
    }
}

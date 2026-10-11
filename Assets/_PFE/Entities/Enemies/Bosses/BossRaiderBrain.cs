using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// Boss brain for Raider Leader (UnitBossRaider).
    /// Runs a 5-state rotation (0..5), quakes, spawns vortex/reinforcements on quarter-HP loss.
    /// </summary>
    public class BossRaiderBrain : BossBrain
    {
        [SerializeField] protected int _bossState = 0; // 0..5 AS3 rotation
        [SerializeField] protected int _variant = 1;   // tr1 = carbine (aiDist=2000), tr2 = flamer (aiDist=500)

        protected int _aiTCh;
        protected float _attackCadenceTicks;

        public int BossState => _bossState;
        public int Variant { get => _variant; set => _variant = value; }

        protected override bool StopsToAttack => _bossState == 2 || _bossState == 3;

        protected override void Awake()
        {
            base.Awake();
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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 3f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * 6f; // AS3 runSpeed = maxSpeed * 6
            _alertSpeed = _chaseSpeed;
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;

            base.SimTick(tickIndex);

            if (_controller == null || !_controller.IsAlive) return;

            // 5-state rotation (UnitBossRaider.as:320-339)
            if (_aiTCh <= 0)
            {
                AdvanceRotation();
            }

            // Quake beat at state 4, aiTCh == 5 (UnitBossRaider.as:537-540, 563-567)
            if (_bossState == 4 && _aiTCh == 5)
            {
                ExecuteQuake();
            }
        }

        protected virtual void AdvanceRotation()
        {
            _bossState++;
            if (_bossState > 5) _bossState = 1;

            if (_bossState == 2 || _bossState == 4 || _bossState == 5)
            {
                _aiTCh = 30;
            }
            else
            {
                _aiTCh = UnityEngine.Random.Range(150, 250);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            if (_bossState != 0)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }
            StopMovement();
        }

        protected override void TickCombatChase(int tickIndex)
        {
            // Budget, not sighting — see `EnemyBrain.ChaseBudgetExhausted`.
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

            float dx = _blackboard.TargetDeltaX;
            float dist = _blackboard.TargetDistance;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            switch (_bossState)
            {
                case 1:
                    // Approach at run speed (UnitBossRaider.as:396-459)
                    MoveHorizontal((dx >= 0 ? 1f : -1f) * _chaseSpeed);
                    if (dist < 200f)
                    {
                        _bossState = 2;
                        _aiTCh = 30;
                    }
                    break;

                case 2:
                case 3:
                    // Face target, stop, fire (UnitBossRaider.as:497-534)
                    StopMovement();
                    if (dist > 300f)
                    {
                        _bossState = 1; // Re-engage chase
                    }
                    else
                    {
                        ExecuteAttackAction();
                    }
                    break;

                case 4:
                    // Quake slam prep
                    StopMovement();
                    break;

                case 5:
                    // Vortex summon & contact charge
                    MoveHorizontal((dx >= 0 ? 1f : -1f) * _patrolSpeed);
                    ExecuteAttackAction();
                    break;
            }

            // Melee contact check (UnitBossRaider.as:511-513, 556-559)
            if (dist <= 70f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            float maxDist = _variant == 1 ? 600f : 250f; // Carbine vs flamer
            return _blackboard.TargetDistance <= maxDist;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            // Direct damage shot
            float dmg = _controller.Definition != null ? _controller.Definition.damage : 19f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.PhysicalBullet),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected virtual void ExecuteQuake()
        {
            // Earth quake burst (UnitBossRaider.as:563-567)
            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 300f)
            {
                var pending = PendingDamage.Direct(
                    DamageContext.Contact(25f, DamageType.PhysicalMelee),
                    _blackboard.TargetUnit,
                    transform.position);
                _controller.DamageSystem?.Report(pending);
            }
            Budilo(600f);
        }

        protected override void OnQuarterHpPassed(int quarter)
        {
            base.OnQuarterHpPassed(quarter);
            Budilo(800f);
            if (_controller is BossRaiderController raiderCtrl)
            {
                raiderCtrl.SpawnReinforcementWave(quarter);
            }
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "die";
            return "stay";
        }
    }
}

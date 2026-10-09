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
    /// Boss brain for Mega Drone (UnitBossDron).
    /// Flight-based pursuer whose speed ramps as health drops, fires rocket & grenade bursts,
    /// and periodically summons drone reinforcements.
    /// </summary>
    public class BossDronBrain : BossBrain
    {
        [SerializeField] protected int _emitTimer = 500;
        protected int _sinTimer;

        protected override bool StopsToAttack => false;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 4f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * 1.5f;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            _sinTimer++;
            if (_emitTimer > 0) _emitTimer--;

            base.SimTick(tickIndex);

            // Reinforcement emission every 500 ticks (UnitBossDron.as:164-178, 263-267)
            if (_emitTimer <= 0 && _controller != null && _controller.IsAlive)
            {
                _emitTimer = 500;
                Budilo(500f);
            }

            // Contact probe
            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 70f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            if (_blackboard.TargetUnit == null)
            {
                StopMovement();
                return;
            }

            float dx = _blackboard.TargetDeltaX;
            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            // Speed formula: walkSpeed * (2 - hp/maxhp) * (1 - sin(aiTCh/12)*0.3) (UnitBossDron.as:249)
            float hpFactor = 2f - CurrentHpFraction;
            float sinFactor = 1f - Mathf.Sin(_sinTimer / 12f) * 0.3f;
            float currentSpeed = _patrolSpeed * hpFactor * sinFactor;

            MoveHorizontal((dx >= 0 ? 1f : -1f) * currentSpeed);

            if (tickIndex % 45 == 0 && IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 500f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            float dmg = _controller.Definition != null ? _controller.Definition.damage : 28f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.Explosive),
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

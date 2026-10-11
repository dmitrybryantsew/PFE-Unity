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
    /// Boss brain for Thunder Head (UnitThunderHead).
    /// Colossal robotic boss with orbital positioning, gravitational suction (vsos)
    /// that ramps with missing HP, and electric burst attacks.
    /// </summary>
    public class ThunderHeadBrain : BossBrain
    {
        [SerializeField] protected float _orbitAngle;
        [SerializeField] protected float _orbitRadius = 5f;
        [SerializeField] protected float _suctionIntensity = 1f;

        public float SuctionIntensity { get => _suctionIntensity; set => _suctionIntensity = value; }

        protected Vector3 _centerOrigin;

        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            _centerOrigin = transform.position;
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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 2f;
            _patrolSpeed = spd;
            _chaseSpeed = spd;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            base.SimTick(tickIndex);

            if (_controller == null || !_controller.IsAlive) return;

            // Orbital trajectory (UnitThunderHead.as:241-255)
            _orbitAngle += 0.02f;
            float targetX = _centerOrigin.x + Mathf.Cos(_orbitAngle) * _orbitRadius;
            float targetY = _centerOrigin.y + Mathf.Sin(_orbitAngle) * (_orbitRadius * 0.4f);
            transform.position = new Vector3(targetX, targetY, transform.position.z);

            // Gravitational suction (vsos) ramped with missing HP (UnitThunderHead.as:373-389, 499-526)
            if (_blackboard.TargetUnit != null)
            {
                float missingFrac = 1f - CurrentHpFraction;
                float pullForce = (2f + missingFrac * 6f) * _suctionIntensity; // Ramps with damage taken

                Vector3 toBoss = transform.position - _blackboard.TargetUnit.transform.position;
                if (toBoss.magnitude > 0.5f && toBoss.magnitude < 15f)
                {
                    // Pull target gently towards boss
                    Vector3 pull = toBoss.normalized * (pullForce * 0.01f);
                    _blackboard.TargetUnit.transform.position += pull;
                }
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            // Budget, not sighting — see `EnemyBrain.ChaseBudgetExhausted`. This unit is fixed in
            // place, so the "cannot see the target" arm stays a hold rather than a pursuit.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null) return;

            if (tickIndex % 40 == 0 && IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 700f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            float dmg = _controller.Definition != null ? _controller.Definition.damage : 40f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.Spark),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected override string ResolveAnimState()
        {
            // Vector art / code-built bitmap in AS3
            return null;
        }
    }
}

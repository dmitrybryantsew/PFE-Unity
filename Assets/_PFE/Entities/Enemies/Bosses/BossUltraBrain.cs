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
    /// Boss brain for Ultra Sentinel (UnitBossUltra).
    /// Two-stage fight: activates enhanced mode (usil) at 50% HP with a massive secondary shield
    /// and upgraded gatling weaponry.
    /// </summary>
    public class BossUltraBrain : BossBrain
    {
        [SerializeField] protected bool _usil = false; // Enhanced phase
        [SerializeField] protected int _attState = 0;
        [SerializeField] protected float _shieldHp = 0f;

        protected int _repositionTimer;

        public bool Usil => _usil;
        public float ShieldHp { get => _shieldHp; set => _shieldHp = value; }

        protected override bool StopsToAttack => true;

        protected override void Awake()
        {
            base.Awake();
            _phaseIndex = 1;
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
            _chaseSpeed = spd * 1.5f;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            if (_repositionTimer > 0) _repositionTimer--;

            // Threshold check: activate usil at 50% HP (UnitBossUltra.as:438-445)
            if (!_usil && CurrentHpFraction <= 0.5f && _controller != null && _controller.IsAlive)
            {
                ActivateUsil();
            }

            base.SimTick(tickIndex);
        }

        protected virtual void ActivateUsil()
        {
            _usil = true;
            SetPhase(2);
            _shieldHp = 2000f; // shitMaxHp * 4 = 500 * 4 = 2000
            Budilo(600f);
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

            // Reposition cadence among points
            if (_repositionTimer <= 0)
            {
                _repositionTimer = 180;
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _patrolSpeed);
            }
            else
            {
                StopMovement();
            }

            int cadence = _usil ? 25 : 45;
            if (tickIndex % cadence == 0 && IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 600f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            float baseDmg = _controller.Definition != null ? _controller.Definition.damage : 24f;
            if (_usil) baseDmg *= 1.4f;

            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDmg, DamageType.Laser),
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

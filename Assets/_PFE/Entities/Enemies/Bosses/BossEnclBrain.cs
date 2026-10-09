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
    /// Boss brain for Enclave Commander (UnitBossEncl).
    /// Flight patrol with sinusoidal drift, hard-coded arena bounds, and rotating focus fire.
    /// </summary>
    public class BossEnclBrain : BossBrain
    {
        [SerializeField] protected int _commanderIndex = 1; // tr = 1, 2, or 3
        [SerializeField] protected float _sinTimer;

        protected float _sinBaseX;
        protected bool _isFlying = true;

        public int CommanderIndex { get => _commanderIndex; set => _commanderIndex = value; }

        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            ApplyTuning();
            // Default arena bounds in px: minX 1000, maxX 1600, minY 250, maxY 850 (UnitBossEncl.as:25-31)
            // Converted to world units (/ 100f)
            SetArenaBounds(new Rect(10f, 2.5f, 6f, 6f));
            _sinBaseX = transform.position.x;
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
            base.SimTick(tickIndex);

            if (_controller == null || !_controller.IsAlive) return;

            // Sinusoidal drift in X (UnitBossEncl.as:33-35, 290-293)
            _sinTimer += 0.05f;
            float xOffset = Mathf.Sin(_sinTimer) * 1.5f;
            Vector3 pos = transform.position;
            transform.position = new Vector3(_sinBaseX + xOffset, pos.y, pos.z);

            // Contact melee probe
            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 60f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            if (_blackboard.TargetUnit == null) return;

            float dx = _blackboard.TargetDeltaX;
            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            // Bounded flight hover
            if (tickIndex % 45 == 0 && IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= 450f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            float dmg = _controller.Definition != null ? _controller.Definition.damage : 25f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.Plasma),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "stay";
            return "fly";
        }
    }
}

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
    /// Boss brain for Alicorn Leader (UnitBossAlicorn).
    /// Two-stage fight with initial spell shield (isShit), teleportation among move points,
    /// and multi-elemental magic attacks.
    /// </summary>
    public class BossAlicornBrain : BossBrain
    {
        [SerializeField] protected bool _isShit = true; // Shield stage active
        [SerializeField] protected int _attState = 0;   // 0..5 attack pattern selector
        [SerializeField] protected int _teleportCooldownTicks = 180;

        protected int _teleportTimer;

        public bool IsShit => _isShit;
        public int AttState => _attState;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 5f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * 1.5f;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            if (_teleportTimer > 0) _teleportTimer--;

            // Teleport cadence (UnitBossAlicorn.as:309-337, 415)
            if (_teleportTimer <= 0 && _controller != null && _controller.IsAlive && _blackboard.TargetUnit != null)
            {
                TeleportNearTarget();
            }

            base.SimTick(tickIndex);
        }

        protected virtual void TeleportNearTarget()
        {
            _teleportTimer = _isShit ? 180 : 120; // Teleports faster in stage 2

            if (_blackboard.TargetUnit == null) return;

            Vector3 targetPos = _blackboard.TargetUnit.transform.position;
            float offsetX = UnityEngine.Random.Range(2f, 5f) * (UnityEngine.Random.value < 0.5f ? 1f : -1f);
            float offsetY = UnityEngine.Random.Range(1f, 3f);

            transform.position = new Vector3(targetPos.x + offsetX, targetPos.y + offsetY, transform.position.z);
            _attState = UnityEngine.Random.Range(0, 6);
        }

        public override bool TryReviveSecondLife()
        {
            // UnitBossAlicorn.as:703-738: First death breaks shield and revives
            if (_isShit)
            {
                _isShit = false;
                SetPhase(2);
                _isSecondLifeActive = true;
                return true;
            }

            return false;
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

            StopMovement(); // Teleports rather than walking

            if (tickIndex % 50 == 0 && IsTargetInAttackRange())
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

            float baseDmg = _controller.Definition != null ? _controller.Definition.damage : 35f;
            DamageType dtype = _attState switch
            {
                0 => DamageType.Laser,
                1 => DamageType.PhysicalBullet,
                2 => DamageType.Plasma,
                _ => DamageType.Laser
            };

            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDmg, dtype),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "die";
            return "fly";
        }
    }
}

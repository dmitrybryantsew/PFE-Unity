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
    /// Boss brain for Necromancer (UnitBossNecr).
    /// Two-phase encounter with revival on first death, rotating protection cycle (heal/shadow/invis),
    /// and curse magic.
    /// </summary>
    public class BossNecrBrain : BossBrain
    {
        [SerializeField] protected int _protectCycle = 0; // 0 heal, 1 shadow, 2 invis
        [SerializeField] protected int _protectCooldownTicks = 600; // timeProtectCuld = 600
        [SerializeField] protected int _protectDurationTicks = 0;   // timeProtect = 250
        [SerializeField] protected int _curseCooldownTicks = 300;   // timeCurseCuld = 300

        public int ProtectCycle => _protectCycle;
        public bool IsProtectActive => _protectDurationTicks > 0;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 6f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * 1.5f;
            _alertSpeed = spd;
        }

        public override void SimTick(int tickIndex)
        {
            if (_protectCooldownTicks > 0) _protectCooldownTicks--;
            if (_protectDurationTicks > 0) _protectDurationTicks--;
            if (_curseCooldownTicks > 0) _curseCooldownTicks--;

            // Rotating protection cycle (UnitBossNecr.as:598-627)
            if (_protectCooldownTicks <= 0 && _controller != null && _controller.IsAlive)
            {
                TriggerProtectCycle();
            }

            base.SimTick(tickIndex);
        }

        protected virtual void TriggerProtectCycle()
        {
            _protectCycle = (_protectCycle + 1) % 3;
            _protectDurationTicks = 250;
            _protectCooldownTicks = 600;

            if (_protectCycle == 0 && _controller.UnitStats != null)
            {
                // Self-heal: healHp = maxhp / 10 (UnitBossNecr.as:103, 603)
                float healAmount = _maxHp * 0.1f;
                _controller.UnitStats.Heal(healAmount);
            }
        }

        public override bool TryReviveSecondLife()
        {
            // UnitBossNecr.as:567-584
            if (_phaseIndex == 1)
            {
                SetPhase(2);
                _isSecondLifeActive = true;
                _protectCooldownTicks = 150; // Faster cycle in phase 2
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

            // Stationary-ish caster: keeps distance
            float dist = _blackboard.TargetDistance;
            if (dist < 150f)
            {
                MoveHorizontal((dx >= 0 ? -1f : 1f) * _patrolSpeed); // Back off
            }
            else
            {
                StopMovement();
            }

            if (tickIndex % 40 == 0 && IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }

            if (_curseCooldownTicks <= 0)
            {
                CastCurse();
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

            float dmg = _controller.Definition != null ? _controller.Definition.damage : 30f;
            if (_phaseIndex == 2) dmg *= 1.3f; // Stronger in ghost phase

            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(dmg, DamageType.Acid),
                _blackboard.TargetUnit,
                targetPos);

            _controller.DamageSystem?.Report(pending);
        }

        protected virtual void CastCurse()
        {
            _curseCooldownTicks = _phaseIndex == 2 ? 180 : 300;
            if (_blackboard.TargetUnit == null) return;

            // Direct curse damage burst
            float curseDmg = 20f;
            var pending = PendingDamage.Direct(
                DamageContext.Contact(curseDmg, DamageType.Venom),
                _blackboard.TargetUnit,
                transform.position);

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

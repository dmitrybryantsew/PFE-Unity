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
    public enum TrapKind
    {
        Turret = 0,
        BearTrap = 1,
        Trigger = 2,
        Damager = 3,
        MagicWall = 4,
        Transmitter = 5,
        Destructible = 6
    }

    /// <summary>
    /// Brain for stationary emplacements, traps, turrets and triggers.
    /// A high-fidelity implementation of AS3 UnitTurret, UnitTrap, UnitTrigger, UnitDamager, UnitMWall, UnitTransmitter, UnitDestr.
    /// Never walks, patrols, or chases.
    /// </summary>
    public class TrapBrain : EnemyBrain
    {
        [SerializeField] protected TrapKind _kind = TrapKind.Turret;
        [SerializeField] protected bool _isArmed = true;
        [SerializeField] protected float _range = 400f;
        [SerializeField] protected float _damage = 35f;

        protected int _fireCooldownTicks;
        protected float _wallDecayAccumulator;

        public TrapKind Kind { get => _kind; set => _kind = value; }
        public bool IsArmed { get => _isArmed; set => _isArmed = value; }

        protected override bool StopsToAttack => false;

        /// <summary>
        /// Promotes a mounted turret to mobile when knocked off its mount (AS3 UnitTurret.otryv()).
        /// </summary>
        public virtual void DetachFromMount()
        {
            if (_controller != null)
            {
                _controller.MakeNoise(100, true);
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

            if (def.damage > 0) _damage = def.damage;

            string id = def.id != null ? def.id.ToLowerInvariant() : "";
            if (id.Contains("turret")) _kind = TrapKind.Turret;
            else if (id.Contains("trap")) _kind = TrapKind.BearTrap;
            else if (id.Contains("trig")) _kind = TrapKind.Trigger;
            else if (id.Contains("dam")) _kind = TrapKind.Damager;
            else if (id.Contains("wall") || id.Contains("mwall")) _kind = TrapKind.MagicWall;
            else if (id.Contains("transm")) _kind = TrapKind.Transmitter;
            else if (id.Contains("destr")) _kind = TrapKind.Destructible;
        }

        public override void SimTick(int tickIndex)
        {
            if (_fireCooldownTicks > 0) _fireCooldownTicks--;

            // Stationary emplacements never run motor locomotion
            StopMovement();

            if (_controller == null || !_controller.IsAlive)
            {
                return;
            }

            switch (_kind)
            {
                case TrapKind.MagicWall:
                    TickMagicWall();
                    break;
                case TrapKind.Transmitter:
                    TickTransmitter(tickIndex);
                    break;
                case TrapKind.BearTrap:
                    TickBearTrap(tickIndex);
                    break;
                case TrapKind.Turret:
                    TickTurret(tickIndex);
                    break;
                case TrapKind.Trigger:
                    TickTrigger(tickIndex);
                    break;
                case TrapKind.Damager:
                    TickDamager(tickIndex);
                    break;
                case TrapKind.Destructible:
                    break;
            }
        }

        protected virtual void TickMagicWall()
        {
            // UnitMWall.as: hp -= 0.2 per tick; exterminate at 0
            _wallDecayAccumulator += 0.2f;
            if (_wallDecayAccumulator >= 1f)
            {
                int dmg = (int)_wallDecayAccumulator;
                _wallDecayAccumulator -= dmg;
                var pending = PendingDamage.Direct(
                    DamageContext.Contact(dmg, DamageType.PhysicalMelee),
                    _controller,
                    transform.position);
                if (_controller.DamageSystem != null)
                {
                    _controller.DamageSystem.Report(pending);
                }
            }
        }

        protected virtual void TickTransmitter(int tickIndex)
        {
            // UnitTransmitter.as: damage every 30 ticks within radius
            if (tickIndex % 30 == 1 && _blackboard.TargetUnit != null)
            {
                float dist = _blackboard.TargetDistance;
                if (dist <= 400f) // distdam 400
                {
                    float factor = 1f - (dist / 400f);
                    float dmg = Mathf.Max(1f, _damage * factor);
                    var pending = PendingDamage.Direct(
                        DamageContext.Contact(dmg, DamageType.Acid),
                        _blackboard.TargetUnit,
                        transform.position);
                    _controller.DamageSystem?.Report(pending);
                }
            }
        }

        protected virtual void TickBearTrap(int tickIndex)
        {
            if (!_isArmed) return;

            // UnitTrap.as: proximity overlap -> damage(35, D_INSIDE) once
            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 30f)
            {
                _isArmed = false; // Sprung
                var pending = PendingDamage.Direct(
                    DamageContext.Contact(35f, DamageType.PhysicalMelee),
                    _blackboard.TargetUnit,
                    transform.position);
                _controller.DamageSystem?.Report(pending);
            }
        }

        protected virtual void TickTurret(int tickIndex)
        {
            if (_blackboard.TargetUnit == null) return;

            // Sweep and fire if in LOS and range
            if (_blackboard.HasLineOfSight && _blackboard.TargetDistance <= _range)
            {
                // Face target
                float dx = _blackboard.TargetDeltaX;
                if (dx > 5f) ApplyFacing(1);
                else if (dx < -5f) ApplyFacing(-1);

                if (_fireCooldownTicks <= 0)
                {
                    ExecuteAttackAction();
                    _fireCooldownTicks = 30; // ~1 attack per second cadence
                }
            }

            // Close-range contact attack (UnitTurret.as:741-744)
            if (_damage > 0 && _blackboard.TargetDistance <= 50f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        protected virtual void TickTrigger(int tickIndex)
        {
            if (!_isArmed) return;

            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 50f)
            {
                _isArmed = false; // One-shot latch
                Budilo(1200f);
            }
        }

        protected virtual void TickDamager(int tickIndex)
        {
            if (!_isArmed) return;

            if (_fireCooldownTicks <= 0 && _blackboard.TargetUnit != null && _blackboard.TargetDistance <= _range)
            {
                ExecuteAttackAction();
                _fireCooldownTicks = 45;
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            return _blackboard.TargetDistance <= _range;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            // Ranged direct attack or contact attack
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;
            var pending = PendingDamage.Direct(
                DamageContext.Contact(_damage > 0 ? _damage : 15f, DamageType.PhysicalBullet),
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
            // Flash vector MovieClips / no blit rows (guide 18 §7)
            return null;
        }
    }
}

using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
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

        /// <summary>
        /// This emplacement has been <b>switched off by a hacked robot terminal</b> — AS3
        /// <c>UnitTurret.sleep</c> (<c>UnitTurret.as:389</c>, set by <c>hack(0)</c>).
        ///
        /// <para><b>Why a separate flag from <see cref="IsArmed"/>.</b> <c>IsArmed</c> is the
        /// bear-trap/trigger latch — a trap that has already sprung. <c>sleep</c> is a turret that is
        /// <i>deliberately</i> inert and can be brought back by a second hack
        /// (<c>UnitTurret.as:394-406</c>). Overloading one flag would make a hacked-off turret
        /// indistinguishable from a spent bear trap, and the two are re-armed differently.</para>
        ///
        /// <para><b>It gates perception as well as firing.</b> AS3's <c>control()</c> opens with
        /// <c>if(World.w.enemyAct &lt;= 0 || this.sleep) return;</c> (<c>UnitTurret.as:572</c>) — before
        /// the cadence-gated <c>findCel()</c> — so a sleeping turret never acquires a target. Gating
        /// only the shot would leave it tracking the player with its barrel, which reads as "not
        /// hacked".</para>
        /// </summary>
        public bool Disabled
        {
            get => _disabled;
            set
            {
                _disabled = value;
                if (!value) return;

                if (_blackboard != null)
                {
                    _blackboard.TargetUnit = null;
                }

                SetState(EnemyAIState.Idle);
            }
        }

        [SerializeField] protected bool _disabled;

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

        /// <summary>
        /// AS3 <c>UnitTurret.putLoc</c>'s aim setup (`UnitTurret.as:179-269</c>), applied from the
        /// placement the way the oracle reads the placed <c>&lt;obj&gt;</c> node: the mount tip's
        /// <c>aRot</c> table, narrowed by the node's <c>@vis</c>.
        ///
        /// <para><b>Only tips 2 and 5 honour <c>@vis</c>.</b> <c>UnitTurret.as:136-139</c> stores the
        /// attribute for every tip, but the <c>angle</c> cases live inside the
        /// <c>turrettip == 2 || turrettip == 5</c> branch (`:201-231</c>). The shipped rooms author
        /// <c>vis</c> on 2 <c>landturret</c> and 3 <c>cturret</c> placements too — values the oracle
        /// ignores — and honouring them here would aim those turrets differently from the game.</para>
        ///
        /// <para><b>An absent <c>@vis</c> is not "face right".</b> The tip-2 table is
        /// <c>[45,135,-135,-45]</c> and <c>nRot</c> is a random index into it (`:252</c>), so the
        /// initial aim is random — which is exactly the coin flip
        /// <see cref="UnitController.ResolveFacing"/> already resolved at population time for the
        /// facing. Leaving it alone is the faithful arm, and the cone alone is not touched either:
        /// without an aim the AS3 unit still sweeps all four directions in turn, and the port's
        /// hemisphere is the aggregate of any two of them.</para>
        /// </summary>
        public override void OnPlacementApplied(UnitInstance placement)
        {
            base.OnPlacementApplied(placement);
            if (_kind != TrapKind.Turret || placement == null) return;

            int tip = TurretMountMath.TipOfDefinitionId(
                _controller != null && _controller.Definition != null ? _controller.Definition.id : null);
            if (tip != 2 && tip != 5) return;

            string vis = placement.GetAttribute("vis", null);
            EnemySensors.VisionCone? cone = TurretMountMath.ConeForVis(vis);
            if (cone == null)
            {
                // UnitTurret.as:216-231 has four cases and no else. An unrecognised value is reported
                // rather than silently treated as "no angle", because the oracle would leave the aim at
                // the table default and an authored typo would look identical.
                Debug.LogWarning(
                    $"[TrapBrain] '{name}': placement vis='{vis}' is not one of "
                    + "up/down/left/right (UnitTurret.as:216-231); the mount keeps its aRot default.",
                    this);
                return;
            }

            SetAim(cone.Value, TurretMountMath.FacingForVis(vis, _controller.FacingDirection));
        }

        /// <summary>
        /// Point this unit's sensors at one of the four half-planes, and turn the unit to match when
        /// the half has a horizontal side.
        ///
        /// <para><b>Both copies are written.</b> <c>ApplyFacing</c> only reaches the controller; the
        /// blackboard's <c>FacingDirection</c> is what the sensors read and it is written once, in
        /// <c>AttachController</c>/<c>Initialize</c> — before this runs. Writing only the controller
        /// would leave the sensor reading the pre-placement side, which is the same "written but never
        /// read at the right time" shape this project keeps finding.</para>
        /// </summary>
        protected void SetAim(EnemySensors.VisionCone cone, int facing)
        {
            _sensors.Cone = cone;
            if (cone == EnemySensors.VisionCone.Horizontal)
            {
                _blackboard.FacingDirection = facing;
                ApplyFacing(facing);
            }
        }

        public override void SimTick(int tickIndex)
        {
            // ── AS3's freeze gate ───────────────────────────────────────────────────────────────────
            //
            // `UnitTurret.control()` opens with `if(World.w.enemyAct <= 0 || this.sleep) return;`
            // (`UnitTurret.as:572`), before the `aiTCh` decrement at :576 and before the cadence-gated
            // `findCel()` at :638 (`enemyAct > 1 && aiTCh % 10 == 1`). So a frozen emplacement neither
            // acquires a target nor advances its cadence.
            //
            // This override deliberately does NOT call `base.SimTick` — see the perception note below —
            // so it cannot inherit the gate that lives there. It has to be repeated here.
            //
            // What still runs: the oracle's return is from `control()` only. `forces()`, `run()`,
            // `actions()` and `animate()` are all outside it (`Unit.as:1806-1840`), so the emplacement
            // keeps drawing and its weapon mount keeps advancing — which is why `TickWeapons()` is
            // called here exactly as the live path's own tail calls it.
            if (EnemyActivity.IsFrozen)
            {
                TickWeapons();
                return;
            }

            // ── A hacked-off turret ────────────────────────────────────────────────────────────────
            //
            // AS3 `if(World.w.enemyAct <= 0 || this.sleep) return;` (UnitTurret.as:572) — the return is
            // from control() only, so the mount keeps advancing and the sprite keeps drawing. That is
            // why TickWeapons() still runs and why the trigger is released explicitly: a turret that
            // was firing when it was hacked must stop, not finish its burst.
            if (_disabled)
            {
                if (_controller is TurretController hackedTurret && hackedTurret.TurretWeaponMount != null)
                {
                    hackedTurret.TurretWeaponMount.SetFiring(false);
                }

                StopMovement();
                TickWeapons();
                return;
            }

            if (_fireCooldownTicks > 0) _fireCooldownTicks--;

            // Stationary emplacements never run motor locomotion
            StopMovement();

            if (_controller == null || !_controller.IsAlive)
            {
                return;
            }

            // ── Perception, BEFORE the family switch ──────────────────────────────────────────────
            //
            // AS3's traps are not exempt from the base perception entry: `UnitTurret.control()` runs
            // the shared `findCel()` on the same tick as its own branch (`UnitTurret.as:648</c>), and
            // `UnitTrap`/`UnitTrigger`/`UnitDamager`/`UnitMWall` are all ordinary `Unit`s that call
            // `super.control()`. This override used to jump straight to its own switch, so
            // `EnemySensors.Evaluate` — the ONLY writer of `EnemyBlackboard.TargetUnit` in the whole
            // project — never ran for ANY trap-family unit, and every
            // `if (_blackboard.TargetUnit == null) return;` below was unconditionally true. That is
            // the reported "turrets never target the player and never attack": not per unit, per family.
            UpdatePerception(tickIndex);

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

            // Advance whatever the controller equipped, last, so the aim this tick published is the
            // one the weapon consumes — the same order the base ends its own tick with.
            TickWeapons();
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

        /// <summary>
        /// AS3 <c>UnitTurret.control()</c>'s attack half (`UnitTurret.as:716-744</c>).
        ///
        /// <para><b>The weapon is a real Weapon, not a damage call.</b> The oracle's constructor is
        /// <c>currentWeapon = new Weapon(this, "turret" + this.tr)</c> (`UnitTurret.as:126-127</c>)
        /// with <c>auto = true</c> (`:130</c>), and the tick's only attack is
        /// <c>currentWeapon.attack()</c> when the unit has committed to a target (`:737-740</c>).
        /// The port's equivalent is the mount the controller equipped: aim it at the target and hold
        /// the trigger while the target is in reach, and the mount's own cadence decides the shots.
        /// That is the same controller-equips / brain-fires split
        /// <see cref="ArmedShooterController"/> uses.</para>
        ///
        /// <para><b>The trigger is released before the target test, not in an else.</b> AS3's
        /// <c>attack()</c> is one call per frame that decides from scratch, so a frame in which no
        /// branch fires is a frame with the trigger up. <c>EnemyWeaponMount</c> latches
        /// <c>_firing</c> until told otherwise, and <c>TickWeaponMounts</c> runs at the end of this
        /// tick — a turret that loses its target would keep shooting at where it was.</para>
        /// </summary>
        protected virtual void TickTurret(int tickIndex)
        {
            EnemyWeaponMount mount = _controller is TurretController turretController
                ? turretController.TurretWeaponMount
                : null;

            if (mount != null)
            {
                mount.SetFiring(false);
            }

            UnitController target = _blackboard.TargetUnit;
            if (target == null) return;

            // aiState == 3 — the oracle's committed state, reached through findCel() and therefore
            // already gated on the cone and the line of sight. `_range` is the port's stand-in for
            // `Unit.look()`'s range (400 px), which the shared sensor's 480 px hard cut can only
            // over-, never under-shoot.
            if (_blackboard.HasLineOfSight && _blackboard.TargetDistance <= _range)
            {
                // Face target (AS3 `storona = celDX > 0 ? 1 : -1`, UnitTurret.as:720). Both copies are
                // written — see SetAim.
                float dx = _blackboard.TargetDeltaX;
                if (dx > 5f)
                {
                    _blackboard.FacingDirection = 1;
                    ApplyFacing(1);
                }
                else if (dx < -5f)
                {
                    _blackboard.FacingDirection = -1;
                    ApplyFacing(-1);
                }

                if (mount != null)
                {
                    // Aim in Unity world units — `TargetDeltaX/Y` are pixels and must not be scaled;
                    // the target's own transform is the point, exactly as ArmedShooterBrain aims.
                    mount.SetAimTarget(target.transform.position);
                    mount.SetFiring(true);
                }
                else if (_fireCooldownTicks <= 0)
                {
                    // No mount: a bare test spawn, or one whose weapon services never arrived. AS3's
                    // `currentWeapon.attack()` still happens, so the damage still lands — the direct
                    // hit is the fallback, not the mechanism.
                    ExecuteAttackAction();
                    _fireCooldownTicks = 30; // ~1 attack per second cadence
                }
            }

            // `attKorp(celUnit, 1)` — UnitTurret.as:741-744, the point-blank contact hit, which is
            // independent of the weapon and fires on every tick a target is in contact range.
            if (_damage > 0 && _blackboard.TargetDistance <= 50f)
            {
                _controller.TryContactAttack(target, 1f);
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

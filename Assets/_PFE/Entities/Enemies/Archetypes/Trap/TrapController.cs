using System;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(TrapBrain))]
    public class TrapController : EnemyController
    {
        public const string ControllerId = "UnitTurret";
        public const string ControllerAlias = "turret";

        public new TrapBrain Brain => _brain as TrapBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<TrapBrain>();
            }
        }
    }

    /// <summary>
    /// The turret mount — AS3 <c>UnitTurret</c>. Six mounts share one controller and are told apart by
    /// their definition id, which <c>UnitTurret.as:94</c> stamps as <c>"turret" + turrettip</c>.
    ///
    /// <para><b>What this class owns: the weapon.</b> The oracle's constructor builds it directly —
    /// <c>currentWeapon = new Weapon(this, "turret" + this.tr)</c> (`UnitTurret.as:126-127</c>) — with
    /// <c>tr</c> taken from the saved object, then the placed <c>&lt;obj&gt;</c> node, then a
    /// difficulty band (`:46-65</c>): 1-3 below <c>locDifLevel</c> 10, 1-5 below 15, 1-7 at or above
    /// it. There is no candidate table and no chance roll — the id is <c>"turret" + tr</c>, full stop,
    /// which is why <see cref="UnitWeaponSelector"/> is deliberately not used here the way
    /// <see cref="ArmedShooterController"/> uses it.</para>
    ///
    /// <para>The brain fires it: <c>TrapBrain.TickTurret</c> aims and holds the trigger, and
    /// <c>EnemyBrain.TickWeapons</c> advances the mount at the end of the tick.</para>
    /// </summary>
    [RequireComponent(typeof(TrapBrain))]
    public class TurretController : TrapController
    {
        public new const string ControllerId = "UnitTurret";
        public new const string ControllerAlias = "turret";

        /// <summary>The id this mount resolved to equip, or null when the placement carries no
        /// <c>tr</c> and no difficulty band was available. Equipped once.</summary>
        private string _turretWeaponId;

        /// <summary>The live mount for <see cref="_turretWeaponId"/>, or null when nothing was equipped
        /// (no weapon services, or a definition-less test spawn).</summary>
        public EnemyWeaponMount TurretWeaponMount =>
            _turretWeaponId != null ? GetWeaponMount(_turretWeaponId) : null;

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Turret;
        }

        /// <summary>
        /// AS3 <c>UnitTurret.hack(param1)</c> (<c>UnitTurret.as:385-414</c>) — the mount is the only
        /// unit in the game that answers a <c>hack_robot</c> terminal.
        ///
        /// <para><b>Three arms, and the difference between them is who the turret shoots.</b>
        /// <c>0</c> shuts it down (<c>sleep = true</c>, aim forced to <c>π/2</c>, no target search);
        /// <c>1</c> and <c>2</c> both hand it to the player (<c>fraction = F_PLAYER</c>) — <c>1</c>
        /// additionally resets the AI cadence and pays the turret's XP to the player, which is why it is
        /// the arm a security perk reaches. <c>warn = 0</c> at the end of all three.</para>
        ///
        /// <para><b>Not ported: the <c>π/2</c> barrel rotation and <c>findCel = false</c>.</b> Both are
        /// presentation/aim state that <see cref="TrapBrain.Disabled"/> already produces the visible
        /// effect of — no acquisition, no firing — and forcing the mount's rotation would fight the
        /// aim the brain republishes every tick. The XP payout on arm 1 is also skipped: the port's
        /// <c>loc.takeXP</c> has no equivalent call site here, and paying XP for a hack would need the
        /// award pipeline wired through the campaign.</para>
        /// </summary>
        public override void Hack(int mode = 0)
        {
            base.Hack(mode);

            TrapBrain trap = Brain;
            if (trap != null)
            {
                switch (mode)
                {
                    case 0:
                        trap.IsArmed = false;
                        trap.Disabled = true;
                        break;

                    case 1:
                    case 2:
                        trap.Disabled = false;
                        trap.IsArmed = true;
                        SetRuntimeFaction(FactionType.Player);
                        break;
                }
            }
            else
            {
                // No brain (a definition-less test spawn): the faction is the only observable part.
                if (mode == 1 || mode == 2)
                {
                    SetRuntimeFaction(FactionType.Player);
                }
            }

            Debug.Log($"[Turret] '{name}' hacked with mode {mode} " +
                      $"({(mode == 0 ? "shut down" : "reprogrammed to the player")}).");
        }

        /// <summary>
        /// AS3 <c>UnitTurret</c>'s constructor order: the weapon is built inside the constructor,
        /// which is before <c>putLoc</c> and therefore before the placement's own attributes are read
        /// for anything else. The port's equivalent moment is <see cref="Initialize"/> (where
        /// <c>Stats</c> first exists), so the id is resolved from the placement as soon as the
        /// placement arrives and the mount is built at both seams.
        /// </summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);
            TryEquipTurretWeapon();
        }

        /// <summary>
        /// Read the placement — AS3's <c>setPos</c> reaching back for the node the constructor was
        /// handed. This is the last act of the spawner, so both <c>Stats</c> and the weapon services
        /// are already in place.
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);
            if (placement == null) return;
            _turretWeaponId = ResolveTurretWeaponId(placement, LocationDifficulty);
            TryEquipTurretWeapon();
        }

        /// <summary>The late-arrival seam: weapon services handed over after Initialize.</summary>
        protected override void OnWeaponServicesReady() => TryEquipTurretWeapon();

        private void TryEquipTurretWeapon()
        {
            if (string.IsNullOrEmpty(_turretWeaponId)) return;
            if (Stats == null || !HasWeaponServices) return;
            EquipWeapon(_turretWeaponId);
        }

        /// <summary>
        /// AS3 <c>UnitTurret.as:46-65</c> — the <c>tr</c> that picks the weapon row, then
        /// <c>:126-127</c>'s <c>new Weapon(this, "turret" + this.tr)</c>. The arithmetic lives in
        /// <see cref="TurretMountMath"/> so the band roll is assertable offline.
        /// </summary>
        internal static string ResolveTurretWeaponId(UnitInstance placement, float locationDifficulty)
        {
            string authored = placement != null ? placement.GetAttribute("tr", null) : null;
            return TurretMountMath.WeaponId(
                TurretMountMath.Tr(authored, locationDifficulty, () => UnityEngine.Random.value));
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class BearTrapController : TrapController
    {
        public new const string ControllerId = "UnitTrap";
        public new const string ControllerAlias = "mtrap";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.BearTrap;
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class TriggerController : TrapController
    {
        public new const string ControllerId = "UnitTrigger";
        public new const string ControllerAlias = "trigcans";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Trigger;
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class DamagerController : TrapController
    {
        public new const string ControllerId = "UnitDamager";
        public new const string ControllerAlias = "damshot";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Damager;
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class MagicWallController : TrapController
    {
        public new const string ControllerId = "UnitMWall";
        public new const string ControllerAlias = "mwall";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.MagicWall;
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class TransmitterController : TrapController
    {
        public new const string ControllerId = "UnitTransmitter";
        public new const string ControllerAlias = "transmitter";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Transmitter;
        }
    }

    [RequireComponent(typeof(TrapBrain))]
    public class DestructiblePropController : TrapController
    {
        public new const string ControllerId = "UnitDestr";
        public new const string ControllerAlias = "destr1";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Destructible;
        }
    }
}

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

    [RequireComponent(typeof(TrapBrain))]
    public class TurretController : TrapController
    {
        public new const string ControllerId = "UnitTurret";
        public new const string ControllerAlias = "turret";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = TrapKind.Turret;
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

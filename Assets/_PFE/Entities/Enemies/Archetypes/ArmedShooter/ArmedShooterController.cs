using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ArmedShooterController : EnemyController
    {
        public const string ControllerId = "UnitRaider";
        public const string ControllerAlias = "raider";

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ArmedShooterBrain>();
            }
        }
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RaiderController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRaider";
        public new const string ControllerAlias = "raider";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class SlaverController : ArmedShooterController
    {
        public new const string ControllerId = "UnitSlaver";
        public new const string ControllerAlias = "slaver";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ZebraController : ArmedShooterController
    {
        public new const string ControllerId = "UnitZebra";
        public new const string ControllerAlias = "zebra";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RangerController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRanger";
        public new const string ControllerAlias = "ranger";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class EnclController : ArmedShooterController
    {
        public new const string ControllerId = "UnitEncl";
        public new const string ControllerAlias = "encl";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class MercController : ArmedShooterController
    {
        public new const string ControllerId = "UnitMerc";
        public new const string ControllerAlias = "merc";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class NecrosController : ArmedShooterController
    {
        public new const string ControllerId = "UnitNecros";
        public new const string ControllerAlias = "necros";
    }
}

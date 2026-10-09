using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(RobotBrain))]
    public class RobotController : EnemyController
    {
        public const string ControllerId = "UnitAIRobot";
        public const string ControllerAlias = "robot";

        public new RobotBrain Brain => _brain as RobotBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<RobotBrain>();
            }
        }
    }

    [RequireComponent(typeof(RobotBrain))]
    public class RobobrainController : RobotController
    {
        public new const string ControllerId = "UnitRobobrain";
        public new const string ControllerAlias = "robobrain";
    }

    [RequireComponent(typeof(RobotBrain))]
    public class DronController : RobotController
    {
        public new const string ControllerId = "UnitDron";
        public new const string ControllerAlias = "dron";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = RobotKind.Drone;
        }
    }

    [RequireComponent(typeof(RobotBrain))]
    public class ProtectController : RobotController
    {
        public new const string ControllerId = "UnitProtect";
        public new const string ControllerAlias = "protect";
    }

    [RequireComponent(typeof(RobotBrain))]
    public class GutsyController : RobotController
    {
        public new const string ControllerId = "UnitGutsy";
        public new const string ControllerAlias = "gutsy";
    }

    [RequireComponent(typeof(RobotBrain))]
    public class SentinelController : RobotController
    {
        public new const string ControllerId = "UnitSentinel";
        public new const string ControllerAlias = "sentinel";
    }

    [RequireComponent(typeof(RobotBrain))]
    public class SpriteBotController : RobotController
    {
        public new const string ControllerId = "UnitSpriteBot";
        public new const string ControllerAlias = "spritebot";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = RobotKind.SpriteBot;
        }
    }

    [RequireComponent(typeof(RobotBrain))]
    public class VortexController : RobotController
    {
        public new const string ControllerId = "UnitVortex";
        public new const string ControllerAlias = "vortex";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = RobotKind.Vortex;
        }
    }

    [RequireComponent(typeof(RobotBrain))]
    public class RollerController : RobotController
    {
        public new const string ControllerId = "UnitRoller";
        public new const string ControllerAlias = "roller";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = RobotKind.Roller;
        }
    }

    [RequireComponent(typeof(RobotBrain))]
    public class MspController : RobotController
    {
        public new const string ControllerId = "UnitMsp";
        public new const string ControllerAlias = "msp";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = RobotKind.Msp;
        }
    }
}

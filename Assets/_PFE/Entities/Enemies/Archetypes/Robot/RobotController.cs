using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The robot <i>brain</i> host — <b>not</b> the <c>UnitAIRobot</c> family.
    ///
    /// <para><b>Why <c>AiFamily</c> is deliberately NOT overridden here.</b> This class is the port's
    /// home for ten AS3 classes, and only five of them are <c>extends UnitAIRobot</c>:
    /// <c>UnitRobobrain</c>, <c>UnitSentinel</c>, <c>UnitProtect</c>, <c>UnitEqd</c> and
    /// <c>UnitGutsy</c> — those five read the placement <c>ai</c> as <c>quiet</c> and nothing else
    /// (<c>UnitAIRobot.as:49-52</c>). The other five — <c>UnitDron</c>, <c>UnitVortex</c>,
    /// <c>UnitRoller</c>, <c>UnitMsp</c>, <c>UnitSpriteBot</c> — are <c>extends Unit</c> and read no
    /// <c>aiTip</c> at all. So the override belongs on the five, and the base stays
    /// <see cref="UnitAiFamily.Generic"/>. Putting it here would hand <c>dron</c> and <c>roller</c> a
    /// spelling they do not have.</para>
    /// </summary>
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

        /// <summary>
        /// <c>UnitRobobrain.as:5</c> is <c>extends UnitAIRobot</c>, whose constructor reads the
        /// placement <c>ai</c> as <c>quiet</c> only (<c>UnitAIRobot.as:49-52</c>).
        /// </summary>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Robot;
    }

    /// <summary>
    /// <c>UnitEqd</c> (Эквидроид) — <c>UnitEqd.as:5</c> is <c>extends UnitAIRobot</c>, so it is the same
    /// brain as <c>robobrain</c> and inherits the same <see cref="RobotKind.Standard"/> shape. Its own
    /// additions are <c>isPort = true</c> and a speed ladder (<c>walkSpeed = maxSpeed</c>,
    /// <c>sitSpeed = maxSpeed * 0.35</c>, <c>runSpeed = maxSpeed * 2.5</c>); the port has no
    /// <c>isPort</c> yet (`families/13_Robot.md` §9), so there is nothing to override here — the class
    /// exists to give the id a registered home rather than to add behaviour.
    ///
    /// <para><b>Why it is needed at all.</b> <c>eqd.asset</c> has an empty <c>controllerId</c> AND an
    /// empty <c>parentId</c>, so <c>RoomUnitSpawner</c>'s 4-step chain falls all the way through to
    /// <c>unitId</c> → <c>"eqd"</c>, which was in no registry — leaving the unit on the brain-less base
    /// <c>UnitController</c>. It is reachable two ways: 7 shipped rooms place it, and
    /// <c>LocationEnemySelector.RandomUnit</c> returns it from the <c>enl2</c> / biome-2 / level-12
    /// rung. This is F2 in <c>REVIEW_2026-10-11_random_room_spawn_audit.md</c>.</para>
    /// </summary>
    [RequireComponent(typeof(RobotBrain))]
    public class EqdController : RobobrainController
    {
        public new const string ControllerId = "UnitEqd";
        public new const string ControllerAlias = "eqd";
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

        /// <summary>
        /// <c>UnitProtect.as:5</c> is <c>extends UnitAIRobot</c> — same <c>ai</c> vocabulary as the base
        /// robot (<c>UnitAIRobot.as:49-52</c>). Seven shipped placements author <c>ai="quiet"</c> on it.
        /// </summary>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Robot;
    }

    [RequireComponent(typeof(RobotBrain))]
    public class GutsyController : RobotController
    {
        public new const string ControllerId = "UnitGutsy";
        public new const string ControllerAlias = "gutsy";

        /// <summary>
        /// <c>UnitGutsy.as:7</c> is <c>extends UnitAIRobot</c>. Seven shipped placements author
        /// <c>ai="quiet"</c> on it.
        /// </summary>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Robot;
    }

    [RequireComponent(typeof(RobotBrain))]
    public class SentinelController : RobotController
    {
        public new const string ControllerId = "UnitSentinel";
        public new const string ControllerAlias = "sentinel";

        /// <summary>
        /// <c>UnitSentinel.as:6</c> is <c>extends UnitAIRobot</c>. <b>Eleven</b> shipped placements
        /// author <c>ai="quiet"</c> on it — the single largest group of <c>quiet</c> placements in the
        /// game, so this one override carries more data than the rest of the family combined.
        /// </summary>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Robot;
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

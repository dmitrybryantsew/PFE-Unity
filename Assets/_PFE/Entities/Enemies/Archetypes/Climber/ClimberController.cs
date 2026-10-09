using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(ClimberBrain))]
    public class ClimberController : EnemyController
    {
        public const string ControllerId = "UnitAnt";
        public const string ControllerAlias = "ant";

        public new ClimberBrain Brain => _brain as ClimberBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ClimberBrain>();
            }
        }
    }

    [RequireComponent(typeof(ClimberBrain))]
    public class AntController : ClimberController
    {
        public new const string ControllerId = "UnitAnt";
        public new const string ControllerAlias = "ant";
    }

    [RequireComponent(typeof(FlyerBrain))]
    public class AntEmitterController : EnemyController
    {
        public const string ControllerId = "UnitAntEmitter";
        public const string ControllerAlias = "eant";

        protected override void Awake()
        {
            base.Awake();
            if (TryGetComponent(out FlyerBrain fb))
            {
                fb.Kind = FlyerKind.Emitter;
            }
        }
    }
}

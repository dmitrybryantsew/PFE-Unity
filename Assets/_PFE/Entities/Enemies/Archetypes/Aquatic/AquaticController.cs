using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(AquaticBrain))]
    public class AquaticController : EnemyController
    {
        public const string ControllerId = "UnitFish";
        public const string ControllerAlias = "fish";

        public new AquaticBrain Brain => _brain as AquaticBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<AquaticBrain>();
            }
        }
    }

    [RequireComponent(typeof(AquaticBrain))]
    public class FishController : AquaticController
    {
        public new const string ControllerId = "UnitFish";
        public new const string ControllerAlias = "fish";
    }
}

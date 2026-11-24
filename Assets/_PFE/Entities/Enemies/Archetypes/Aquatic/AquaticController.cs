using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for aquatic units (AS3 <c>fe/unit/UnitFish.as</c>). Every fish id routes here;
    /// the family's only variant fact (which row the unit is) already lives on the definition.
    /// </summary>
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
}

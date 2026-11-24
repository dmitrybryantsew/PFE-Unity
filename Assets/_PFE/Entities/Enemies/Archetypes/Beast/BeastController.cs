using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for the whole beast family (AS3 <c>fe/unit/UnitMonstrik.as</c>,
    /// <c>UnitHellhound.as</c>, <c>UnitSlime.as</c>).
    ///
    /// <para><b>One controller, three bodies — the kind decides.</b> Every beast id routes here and
    /// <see cref="BeastBrain.Kind"/> (resolved from the unit id by
    /// <see cref="UnitArchetypeRules.BeastKindFor"/>) picks the behaviour: the hound's leap and howl,
    /// the slime's ceiling hang and momentum-free contact, the critter's punch. The slime's
    /// <c>collisionTip = 0</c> (<c>UnitSlime.as:65</c>) is answered by the id-keyed
    /// <see cref="PFE.Sim.Combat.Math.UnitCollisionImpulseMath.DisablesContactExchange"/>, not by a
    /// controller override.</para>
    /// </summary>
    [RequireComponent(typeof(BeastBrain))]
    public class BeastController : EnemyController
    {
        public const string ControllerId = "UnitMonstrik";
        public const string ControllerAlias = "monstrik";

        public new BeastBrain Brain => _brain as BeastBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<BeastBrain>();
            }
        }
    }
}

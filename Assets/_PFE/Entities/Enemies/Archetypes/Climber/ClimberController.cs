using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for climber units (AS3 <c>fe/unit/UnitAnt.as</c>). Every ant id routes here; the
    /// family's wall-climb behaviour lives on <see cref="ClimberBrain"/>.
    /// </summary>
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

    /// <summary>
    /// The ant nest — AS3 <c>UnitAntEmitter</c>, which <b>extends <c>UnitBloatEmitter</c></b> and is
    /// driven by <see cref="FlyerBrain"/> in its emitter mode, not by <see cref="ClimberBrain"/>. Kept
    /// as its own controller because its brain is the flyer's, not the climber's; only the Kind stamp
    /// remains, and the flyer brain resolves that from the id (<c>eant</c> →
    /// <see cref="FlyerKind.Emitter"/>) even without this line.
    /// </summary>
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

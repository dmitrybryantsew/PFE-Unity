using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
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

    [RequireComponent(typeof(BeastBrain))]
    public class MonstrikController : BeastController
    {
        public new const string ControllerId = "UnitMonstrik";
        public new const string ControllerAlias = "monstrik";

        protected override void Awake()
        {
            base.Awake();
            var b = GetComponent<BeastBrain>();
            if (b != null)
            {
                b.Kind = BeastKind.Critter;
            }
        }
    }

    [RequireComponent(typeof(BeastBrain))]
    public class HellhoundController : BeastController
    {
        public new const string ControllerId = "UnitHellhound";
        public new const string ControllerAlias = "hellhound";

        protected override void Awake()
        {
            base.Awake();
            var b = GetComponent<BeastBrain>();
            if (b != null)
            {
                b.Kind = BeastKind.Hound;
            }
        }
    }

    [RequireComponent(typeof(BeastBrain))]
    public class SlimeController : BeastController
    {
        public new const string ControllerId = "UnitSlime";
        public new const string ControllerAlias = "slime";

        protected override void Awake()
        {
            base.Awake();
            var b = GetComponent<BeastBrain>();
            if (b != null)
            {
                b.Kind = BeastKind.Slime;
            }
        }
    }
}

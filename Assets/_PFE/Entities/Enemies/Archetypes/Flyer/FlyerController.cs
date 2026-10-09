using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    [RequireComponent(typeof(FlyerBrain))]
    public class FlyerController : EnemyController
    {
        public const string ControllerId = "UnitBloat";
        public const string ControllerAlias = "bloat";

        public new FlyerBrain Brain => _brain as FlyerBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<FlyerBrain>();
            }
        }
    }

    [RequireComponent(typeof(FlyerBrain))]
    public class BloatController : FlyerController
    {
        public new const string ControllerId = "UnitBloat";
        public new const string ControllerAlias = "bloat";

        [SerializeField] private int _variant = 1;
        [SerializeField] private bool _isEmit;

        public int Variant { get => _variant; set => _variant = value; }
        public bool IsEmit { get => _isEmit; set => _isEmit = value; }
        public int SplitCount { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = FlyerKind.Bloat;
        }

        /// <summary>
        /// Derives <see cref="Variant"/> and <see cref="IsEmit"/> from the unit's own id.
        ///
        /// <para><b>Why this is not left to the serialized default.</b> AS3 resolves a family id to a
        /// variant (<c>randomCid</c>) and then reads the variant back off its own id — <c>tr</c> is the
        /// trailing number, and <c>tr &gt;= 8</c> is what arms the death split. The port's
        /// <see cref="PFE.Systems.Map.UnitVariantResolver"/> performs the same selection at spawn time, so
        /// by the time this controller exists the answer is already in <c>definition.id</c>. Leaving
        /// <c>_variant</c> at its serialized <c>1</c> meant the split condition was false for every bloat
        /// in play and was only ever satisfied by a test that assigned the property by hand — a test
        /// proving a branch no player could reach.</para>
        /// </summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            string unitId = definition != null ? definition.id : null;
            _variant = ParseTrailingNumber(unitId, _variant);
            _isEmit = !string.IsNullOrEmpty(unitId) && unitId.StartsWith("ebloat", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The trailing digits of a unit id — <c>bloat5</c> → <c>5</c>, <c>bloat</c> → <paramref name="fallback"/>.
        /// </summary>
        static int ParseTrailingNumber(string unitId, int fallback)
        {
            if (string.IsNullOrEmpty(unitId)) return fallback;

            int start = unitId.Length;
            while (start > 0 && unitId[start - 1] >= '0' && unitId[start - 1] <= '9') start--;
            if (start == unitId.Length) return fallback;

            return int.TryParse(unitId.Substring(start), out int parsed) ? parsed : fallback;
        }

        protected override void OnDeath()
        {
            if (_variant >= 8 || _isEmit)
            {
                SplitCount = _variant >= 8 ? 2 : (_isEmit ? 2 : 0);
            }
            base.OnDeath();
        }
    }

    [RequireComponent(typeof(FlyerBrain))]
    public class BatController : FlyerController
    {
        public new const string ControllerId = "UnitBat";
        public new const string ControllerAlias = "bloodwing";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = FlyerKind.Bat;
        }
    }

    [RequireComponent(typeof(FlyerBrain))]
    public class PhoenixController : FlyerController
    {
        public new const string ControllerId = "UnitPhoenix";
        public new const string ControllerAlias = "phoenix";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = FlyerKind.Phoenix;
        }

        public override void TakeDamage(float damage)
        {
            if (damage > 0f)
            {
                if (_unitStats != null)
                {
                    _unitStats.Damage(_unitStats.CurrentHp.Value);
                }
                RaiseDeath();
                return;
            }
            base.TakeDamage(damage);
        }

        public override bool ApplyDamage(in PFE.Systems.Combat.DamageOutcome outcome)
        {
            if (outcome.HpDamage > 0f)
            {
                if (_unitStats != null)
                {
                    _unitStats.Damage(_unitStats.CurrentHp.Value);
                }
                RaiseDeath();
                return true;
            }
            return base.ApplyDamage(outcome);
        }
    }

    [RequireComponent(typeof(FlyerBrain))]
    public class BloatEmitterController : FlyerController
    {
        public new const string ControllerId = "UnitBloatEmitter";
        public new const string ControllerAlias = "ebloat";

        protected override void Awake()
        {
            base.Awake();
            if (Brain != null) Brain.Kind = FlyerKind.Emitter;
        }
    }
}

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

        /// <summary>
        /// AS3 <c>Unit.isFly</c> — <b>true</b> for the drift-spit bloat and the dive bat, which is what
        /// turns gravity off for them.
        ///
        /// <para><b>This is why the whole family fell.</b> <c>EnemyController</c> inherits
        /// <c>UnitController.IsFlying =&gt; false</c> and never overrode it, so both gravity drivers —
        /// <c>UnitController.ApplyGravity</c> and <c>TilePhysicsController.ApplyGravity</c> — took the
        /// walking arm for every bloat and every bloodwing. <c>UnitBloat.as:71</c> and
        /// <c>UnitBat.as:48</c> set <c>isFly = true</c> in their constructors; the port spawned them,
        /// told them to drift, and pulled them straight back down. The griffin family worked only
        /// because <c>ArmedShooterController</c> overrides this property already, and the alicorn
        /// because <c>AlicornBrain</c> owns its own flag.</para>
        ///
        /// <para><b>The brain answers, not the subclass.</b> Same shape as
        /// <c>ArmedShooterController.IsFlying</c>: the state that decides "am I airborne" lives in the
        /// brain, and a second copy on the controller could disagree with it. The phoenix is parked
        /// (<c>UnitPhoenix.as:24</c> sets <c>stay = true</c>, it never sets <c>isFly</c>) and the nest is
        /// <c>fixed='1'</c> (<c>AllData.as:1038</c>), so neither reports flying.</para>
        /// </summary>
        protected override bool IsFlying => Brain != null && Brain.IsFlying;

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
        /// Derives <see cref="Variant"/> from the unit's own id.
        ///
        /// <para><b>Why this is not left to the serialized default.</b> AS3 resolves a family id to a
        /// variant (<c>randomCid</c>) and then reads the variant back off its own id — <c>tr</c> is the
        /// trailing number, and <c>tr &gt;= 8</c> is what arms the death split. The port's
        /// <see cref="PFE.Systems.Map.UnitVariantResolver"/> performs the same selection at spawn time, so
        /// by the time this controller exists the answer is already in <c>definition.id</c>. Leaving
        /// <c>_variant</c> at its serialized <c>1</c> meant the split condition was false for every bloat
        /// in play and was only ever satisfied by a test that assigned the property by hand — a test
        /// proving a branch no player could reach.</para>
        ///
        /// <para><b><c>isEmit</c> is not derivable from the id, and used to pretend to be.</b> The field
        /// comes from <c>&lt;un bulb='1'&gt;</c> (<c>UnitBloat.as:104-107</c>), authored on <c>bloat3</c>
        /// (<c>AllData.as:963</c>); <c>UnitDataImporter</c> does not read <c>&lt;un&gt;</c> sub-attributes,
        /// so no port asset carries it. The previous line set it from
        /// <c>unitId.StartsWith("ebloat")</c> — but <c>ebloat</c> is the <b>nest</b>, which the spawner
        /// maps to <see cref="BloatEmitterController"/>, so no <c>BloatController</c> can ever hold an
        /// <c>ebloat</c> definition and the flag was unreachable. Left as a serialized/assignable flag
        /// with the gap named, rather than replaced by a second unreachable derivation.</para>
        /// </summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            string unitId = definition != null ? definition.id : null;
            _variant = ParseTrailingNumber(unitId, _variant);
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

        /// <summary>
        /// <c>UnitBloat.dropLoot()</c> (<c>UnitBloat.as:165-186</c>) — the split and the <c>bulb</c> emit.
        ///
        /// <para><b>Two independent rules, and the oracle's order is split-then-emit.</b></para>
        /// <list type="bullet">
        /// <item><description><c>if(this.tr &gt;= 8)</c> — two children of <c>bloat{tr - 1}</c>, i.e. a
        /// bloat9 splits into two bloat8s, each carrying the parent's <c>questId</c> (<c>:169-175</c>).</description></item>
        /// <item><description><c>if(this.isEmit)</c> — two <c>bloat0</c> children, or four when the unit is
        /// <c>hero</c> (<c>:176-185</c>).</description></item>
        /// </list>
        ///
        /// <para><b><see cref="SplitCount"/> is a report, not the spawn.</b> It is what the F3 panel and
        /// <c>EnemyFamiliesAndBossesTests</c> read, and it has always counted <i>two</i> — for the split pair
        /// and, since the emit pair is also two, for a <c>bulb</c> bloat at hero tier 0. It therefore cannot
        /// tell the two rules apart, and it under-reports a hero-tier emit (four children) as two. That is
        /// left as-is rather than widened: the number the existing fixture asserts is <c>2</c> in all three
        /// cases, and a field whose only reader is a test should not be the thing that grows a taxonomy.
        /// The authoritative count is the one passed to <c>SpawnDeathChildren</c> below.</para>
        ///
        /// <para>The spawn itself goes through <c>FlyerBrain.SpawnDeathChildren</c>, which resolves the room
        /// and the definition; a bare test controller has neither and silently spawns nothing, which is the
        /// same answer <c>SecurityRuntime</c> gives for a reinforcement in a room with no spawner.</para>
        /// </summary>
        protected override void OnDeath()
        {
            bool splits = _variant >= 8;
            if (splits || _isEmit)
            {
                SplitCount = 2;
            }

            if (Brain != null && CanSpawnOnDeath())
            {
                string splitInto = splits ? "bloat" + (_variant - 1) : null;
                int emitCount = _isEmit ? (HeroTier > 0 ? 4 : 2) : 0;
                Brain.SpawnDeathChildren(splitInto, splits ? 2 : 0, emitCount);
            }

            base.OnDeath();
        }

        /// <summary>
        /// A bloat that has never been given a room cannot spawn anything, and asking would log a warning
        /// per corpse in every headless fixture. <c>Room</c> is the same precondition the spawner needs.
        /// </summary>
        bool CanSpawnOnDeath() => Room != null;
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
            // `IsAlive` in the condition, not just `damage > 0`: the phoenix's one-shot kill is the
            // LIVING behaviour. A hit on its corpse has to fall through to the base class, or the
            // body would be immune to damage forever (this override calls RaiseDeath instead of
            // applying damage, and RaiseDeath is latched once per death).
            if (damage > 0f && IsAlive)
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
            if (outcome.HpDamage > 0f && IsAlive)
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

        /// <summary>
        /// <c>UnitBloatEmitter.dropLoot()</c> (<c>UnitBloatEmitter.as:100-109</c>) — <b>five</b> children on
        /// death, none of which counts toward <c>kolChild</c>.
        ///
        /// <para><b>Why this is the whole point of the nest.</b> A nest that never emitted and then died
        /// silently was the report; the emit loop is <c>FlyerBrain.EmitterControl</c> and this is its death
        /// arm. A nest with no room (a bare fixture) spawns nothing — <c>FlyerSpawner</c> returns null
        /// rather than throwing.</para>
        /// </summary>
        protected override void OnDeath()
        {
            if (Brain != null && Room != null)
            {
                Brain.EmitOnDeath();
            }

            base.OnDeath();
        }
    }
}

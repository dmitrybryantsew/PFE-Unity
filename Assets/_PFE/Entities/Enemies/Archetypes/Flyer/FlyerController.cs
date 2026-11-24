using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for the whole flyer family (AS3 <c>fe/unit/UnitBloat.as</c>, <c>UnitBat.as</c>,
    /// <c>UnitPhoenix.as</c> and <c>UnitBloatEmitter.as</c>).
    ///
    /// <para><b>One controller, four bodies — the kind decides.</b> AS3 splits the family into classes;
    /// the port routes every flyer id here and lets <see cref="FlyerBrain.Kind"/> (resolved from the
    /// unit id by <see cref="UnitArchetypeRules.FlyerKindFor"/>) pick the behaviour. The three class
    /// facts the deleted leaf controllers carried live here, each gated on the kind: the bloat's
    /// <c>tr &gt;= 8</c> death split and <c>bulb</c> emit (<see cref="OnDeath"/>), the phoenix's
    /// one-hit death (<see cref="TakeDamage"/>), and the nest's five-children death emit
    /// (<see cref="OnDeath"/>).</para>
    ///
    /// <para><b>What this class deliberately does not own: the flight state.</b> AS3
    /// <c>Unit.isFly</c> — the difference between a flyer that falls and one that hovers — lives on
    /// the brain, which is what reads the target's height and the fall timer; this override only
    /// forwards it.</para>
    /// </summary>
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
        /// told them to drift, and pulled them straight back down.</para>
        ///
        /// <para><b>The brain answers, not the controller.</b> Same shape as
        /// <c>ArmedShooterController.IsFlying</c>: the state that decides "am I airborne" lives in
        /// the brain, and a second copy on the controller could disagree with it. The phoenix is parked
        /// (<c>UnitPhoenix.as:24</c> sets <c>stay = true</c>, it never sets <c>isFly</c>) and the nest is
        /// <c>fixed='1'</c> (<c>AllData.as:1038</c>), so neither reports flying.</para>
        /// </summary>
        protected override bool IsFlying => Brain != null && Brain.IsFlying;

        // ── UnitBloat death facts (the deleted BloatController) ───────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitBloat.tr</c> — read back off the unit's own id (<c>bloat5</c> → <c>5</c>), because
        /// <see cref="PFE.Systems.Map.UnitVariantResolver"/> has already committed to the variant at
        /// spawn time. <c>tr &gt;= 8</c> is what arms the death split (<c>UnitBloat.as:169-175</c>).
        /// </summary>
        [SerializeField] private int _variant = 1;

        /// <summary>
        /// AS3 <c>UnitBloat.isEmit</c> — the <c>&lt;un bulb='1'&gt;</c> flag (<c>UnitBloat.as:104-107</c>,
        /// authored on <c>bloat3</c>). The importer does not read <c>&lt;un&gt;</c> sub-attributes, so no
        /// imported asset carries it; it stays a serialized/assignable flag with the gap named, rather
        /// than replaced by a second unreachable derivation.
        /// </summary>
        [SerializeField] private bool _isEmit;

        /// <summary>
        /// What the F3 panel and the fixtures read — the count of death children, or <c>0</c> while the
        /// unit lives. Two for the split pair and, since the emit pair is also two, for a <c>bulb</c>
        /// bloat at hero tier 0; a hero-tier emit really spawns four (<see cref="OnDeath"/>), which this
        /// report under-counts by design — its only readers want "did it spawn any".
        /// </summary>
        public int SplitCount { get; private set; }

        /// <summary>The bloat variant (<c>tr</c>) — assignable because the fixture drives it directly.</summary>
        public int Variant { get => _variant; set => _variant = value; }

        /// <summary>The <c>bulb</c> emit flag — assignable for the same reason as <see cref="Variant"/>.</summary>
        public bool IsEmit { get => _isEmit; set => _isEmit = value; }

        /// <summary>AS3's constructor reading the placement — for this family, the id IS the placement.</summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            string unitId = definition != null ? definition.id : null;
            _variant = ParseTrailingNumber(unitId, _variant);
        }

        /// <summary>The trailing digits of a unit id — <c>bloat5</c> → <c>5</c>, <c>bloat</c> →
        /// <paramref name="fallback"/>.</summary>
        static int ParseTrailingNumber(string unitId, int fallback)
        {
            if (string.IsNullOrEmpty(unitId)) return fallback;

            int start = unitId.Length;
            while (start > 0 && unitId[start - 1] >= '0' && unitId[start - 1] <= '9') start--;
            if (start == unitId.Length) return fallback;

            return int.TryParse(unitId.Substring(start), out int parsed) ? parsed : fallback;
        }

        // ── UnitPhoenix one-hit death (the deleted PhoenixController) ─────────────────────────────

        /// <summary>Only the parked phoenix dies to a single hit (<c>UnitPhoenix</c> — the AS3 class
        /// kills itself in <c>damage()</c>); the gate keeps every other flyer on the ordinary ladder.</summary>
        bool IsPhoenix => Brain != null && Brain.Kind == FlyerKind.Phoenix;

        public override void TakeDamage(float damage)
        {
            // `IsAlive` in the condition, not just `damage > 0`: the phoenix's one-shot kill is the
            // LIVING behaviour. A hit on its corpse has to fall through to the base class, or the
            // body would be immune to damage forever (this arm calls RaiseDeath instead of
            // applying damage, and RaiseDeath is latched once per death).
            if (damage > 0f && IsAlive && IsPhoenix)
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
            if (outcome.HpDamage > 0f && IsAlive && IsPhoenix)
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

        // ── The family's two death arms (UnitBloat.dropLoot / UnitBloatEmitter.dropLoot) ──────────

        /// <summary>
        /// <c>UnitBloat.dropLoot()</c> (<c>UnitBloat.as:165-186</c>) — the <c>tr &gt;= 8</c> split and
        /// the <c>bulb</c> emit — and <c>UnitBloatEmitter.dropLoot()</c> (<c>:100-109</c>) — five
        /// children on the nest's death. Both are gated on the kind, and a flyer with no room (a bare
        /// fixture) spawns nothing, which is the same answer <c>SecurityRuntime</c> gives for a
        /// reinforcement in a room with no spawner.
        /// </summary>
        protected override void OnDeath()
        {
            FlyerBrain brain = Brain;
            if (brain != null && brain.Kind == FlyerKind.Bloat)
            {
                bool splits = _variant >= 8;
                if (splits || _isEmit)
                {
                    SplitCount = 2;
                }

                // A bloat that has never been given a room cannot spawn anything, and asking would log
                // a warning per corpse in every headless fixture. `Room` is the same precondition the
                // spawner needs.
                if (Room != null)
                {
                    string splitInto = splits ? "bloat" + (_variant - 1) : null;
                    int emitCount = _isEmit ? (HeroTier > 0 ? 4 : 2) : 0;
                    brain.SpawnDeathChildren(splitInto, splits ? 2 : 0, emitCount);
                }
            }
            else if (brain != null && brain.Kind == FlyerKind.Emitter && Room != null)
            {
                brain.EmitOnDeath();
            }

            base.OnDeath();
        }

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<FlyerBrain>();
            }
        }
    }
}

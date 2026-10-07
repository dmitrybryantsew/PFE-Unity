using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Base controller for living hostile enemies.
    /// Bridges <see cref="EnemyBrain"/> with <see cref="UnitController"/> locomotion and combat.
    /// </summary>
    public class EnemyController : UnitController
    {
        protected EnemyBrain _brain;

        public EnemyBrain Brain => _brain;

        protected override void Awake()
        {
            base.Awake();
            _brain = GetComponent<EnemyBrain>();

            // Hand ourselves over, because the brain's own Awake has ALREADY run and could not find us.
            //
            // `[RequireComponent(typeof(ZombieBrain))]` on the archetype controllers makes Unity create
            // the required component first, so `AddComponent<ZombieController>()` runs ZombieBrain.Awake
            // before this component exists at all. That Awake's `GetComponent<UnitController>()` returned
            // null and nothing ever retried it: `EnemyBrain._controller` stayed null for the unit's whole
            // life, which killed the animation (ResolveAnimState early-returns null) and threw
            // NullReferenceException on every attack. See EnemyBrain.ResolveComponents.
            _brain?.AttachController(this);
        }

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);
            if (_brain == null)
            {
                _brain = GetComponent<EnemyBrain>();
            }

            // Again, for the mirror case: a brain added AFTER this component found no controller in its
            // own Awake either (it ran before this Initialize, or before this component existed).
            // Idempotent, so repeating the Awake-time handover costs nothing.
            _brain?.AttachController(this);

            // The definition has only just arrived — `RoomUnitSpawner` adds the component first and
            // calls this second — so anything the brain reads out of it has to happen here rather than
            // in `Awake`, where `Stats` is still null.
            _brain?.OnDefinitionAssigned();
        }

        public override void SetTileQuery(ITileQueryService tileQuery)
        {
            base.SetTileQuery(tileQuery);
            if (_brain != null && SimLoop != null)
            {
                _brain.Initialize(tileQuery, SimLoop);
            }
        }

        /// <summary>
        /// Apply the placement's own fields, then let the brain read them — AS3's <c>setPos</c>.
        ///
        /// <para><b>Why the brain is told here rather than reading the record itself.</b> The brain holds
        /// no reference to <see cref="UnitInstance"/>: it drives a live unit through
        /// <see cref="UnitController"/> and the tile query, and the placement record is a spawn-layer
        /// object that the spawner owns. Handing it over once, at the same seam the oracle reads the
        /// placed <c>&lt;obj&gt;</c> node, keeps that direction of dependency one-way.</para>
        ///
        /// <para>The base call runs first, so a brain reading the record sees a unit whose facing has
        /// already been applied — AS3's constructor order too, where <c>storona</c> is resolved before
        /// the subclass constructor body runs (<c>Unit.as:596-613</c> before
        /// <c>UnitZombie.as:120-127</c>).</para>
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);
            _brain?.OnPlacementApplied(placement);
        }

        public override void AttachSimulation(PFE.Core.SimClock simClock, PFE.Core.SimLoop simLoop)
        {
            base.AttachSimulation(simClock, simLoop);
            if (_brain != null && _tileQuery != null)
            {
                _brain.Initialize(_tileQuery, simLoop);
            }
        }

        public override void TakeDamage(float damage)
        {
            base.TakeDamage(damage);
            if (_brain != null && IsAlive)
            {
                _brain.OnDamaged(damage, (Vector2)transform.position * 100f);
            }
        }

        public override bool ApplyDamage(in PFE.Systems.Combat.DamageOutcome outcome)
        {
            bool broke = base.ApplyDamage(outcome);
            if (_brain != null && IsAlive)
            {
                _brain.OnDamaged(outcome.HpDamage, (Vector2)transform.position * 100f);
            }
            return broke;
        }

        protected override void OnDeath()
        {
            base.OnDeath();
            if (_brain != null)
            {
                _brain.SetState(EnemyAIState.Dead);
            }
        }
    }
}

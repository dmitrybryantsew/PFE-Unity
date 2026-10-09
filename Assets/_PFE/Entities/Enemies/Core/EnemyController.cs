using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using UnityEngine;
using VContainer;

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

        // ── The two services an NPC needs to look and sound like a unit ──────────
        //
        // Both arrive by hand from RoomUnitSpawner, for the reason that class documents at length: a
        // spawned unit is built with AddComponent, which VContainer never observes, so [Inject] on a
        // controller cannot reach it. Before this seam existed NO enemy in the project referenced
        // ISoundService or the weapon stack at all — an enemy's weapon made no projectile and no
        // report, and its `die` voice never played, however much of both was imported and catalogued.

        private IProjectileFactory _projectileFactory;
        private IObjectResolver    _resolver;
        private ISoundService      _soundService;
        private PfeDebugSettings   _debugSettings;
        private PFE.Core.Rng.IRngService _rng;
        private IWeaponDefinitionProvider _weaponProvider;

        /// <summary>Live mounts by weapon id — an alicorn carries <c>alilight</c> and, at tr3, <c>alipsy</c>.</summary>
        private readonly Dictionary<string, EnemyWeaponMount> _weaponMounts =
            new Dictionary<string, EnemyWeaponMount>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>True once the spawner has handed the weapon services over.</summary>
        public bool HasWeaponServices => _projectileFactory != null;

        /// <summary>
        /// The mount for <paramref name="weaponId"/>, or null when the unit does not carry it.
        /// </summary>
        public EnemyWeaponMount GetWeaponMount(string weaponId)
            => weaponId != null && _weaponMounts.TryGetValue(weaponId, out EnemyWeaponMount m) ? m : null;

        /// <summary>
        /// Advance every weapon this enemy carries by one step, so a mount that was told to fire
        /// actually produces a projectile.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the brain drives this and not <c>UnitController.StepUnit</c>.</b> <c>StepUnit</c>
        /// is documented as "the hook for extra work in this unit's step, which both drivers call", and
        /// it is the obvious home for this — but the two drivers it names are the two <i>motor-less</i>
        /// ones. <c>TilePhysicsController.StepMotor</c> calls <c>Unit.TickEffects()</c> and
        /// <b>deliberately does not call <c>StepUnit</c></b> (see its comment at the call site), so a
        /// <c>StepUnit</c> override silently never runs for a motor-driven unit. The live settings asset
        /// has <c>unitMotor: 1</c>, so every spawned unit <i>is</i> motor-driven and a <c>StepUnit</c>
        /// hook would have been dead code in the shipped configuration — the mount would exist, hold a
        /// real weapon, and never fire, which is exactly the symptom this closes.</para>
        ///
        /// <para>The brain is the right owner instead: it is registered on <c>SimLoop</c> whenever the
        /// sim exists, and a brain that does not tick has no firing decision to carry out either, so
        /// there is no configuration in which the decision happens and the weapon does not advance.</para>
        /// </remarks>
        public void TickWeaponMounts(float dt)
        {
            if (_weaponMounts.Count == 0) return;

            foreach (KeyValuePair<string, EnemyWeaponMount> pair in _weaponMounts)
            {
                pair.Value?.Tick(dt);
            }
        }

        /// <summary>
        /// Hand the enemy the services it needs to fire a weapon and make a sound. Called by
        /// <c>RoomUnitSpawner</c> immediately after the component is added and <i>before</i>
        /// <c>Initialize</c>, so an archetype can equip in <c>Initialize</c>/<c>OnDefinitionAssigned</c>.
        ///
        /// <para>Every argument is optional and null is a legitimate state — a bare test spawn has no
        /// container. A unit with no <paramref name="factory"/> stays unarmed (loudly, once, from
        /// <see cref="EnemyWeaponMount"/>); a unit with no <paramref name="sound"/> is silent, which is
        /// the state every enemy in the project was in until now.</para>
        /// </summary>
        public virtual void SetWeaponServices(
            IProjectileFactory factory,
            IObjectResolver resolver = null,
            ISoundService sound = null,
            PfeDebugSettings debug = null,
            PFE.Core.Rng.IRngService rng = null,
            IWeaponDefinitionProvider weaponProvider = null)
        {
            _projectileFactory = factory;
            _resolver          = resolver;
            _soundService      = sound;
            _debugSettings     = debug;
            _rng               = rng;
            _weaponProvider    = weaponProvider;

            // The archetype equips here, not in the setter itself, so the two arrival orders both
            // work. The spawner hands the services over *before* Initialize (the normal order), but a
            // later handover — a spawner built by a test, or a container that arrived after the first
            // room — reaches an archetype that already ran Initialize. EquipWeapon is idempotent per
            // id, so re-entering it costs a dictionary hit rather than a second controller.
            OnWeaponServicesReady();
        }

        /// <summary>
        /// Called whenever the weapon services are (re-)assigned. Archetypes equip their weapons here.
        /// </summary>
        protected virtual void OnWeaponServicesReady()
        {
        }

        /// <summary>
        /// Equip a weapon by id and return its mount. Idempotent per id: calling twice returns the same
        /// mount rather than stacking a second controller on the unit.
        ///
        /// <para><b>Why the id and not a <see cref="WeaponDefinition"/>.</b> The caller is an archetype
        /// that knows the oracle's weapon id (<c>Weapon.create(this,"alilight")</c>), and a
        /// <c>ScriptableObject</c> reference cannot be authored on a component that is created at
        /// runtime. The id is also what the oracle actually writes.</para>
        /// </summary>
        protected EnemyWeaponMount EquipWeapon(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return null;
            if (_weaponMounts.TryGetValue(weaponId, out EnemyWeaponMount existing)) return existing;

            IWeaponDefinitionProvider provider = _weaponProvider ?? ResourcesWeaponDefinitionProvider.Shared;
            if (!provider.TryGetWeapon(weaponId, out WeaponDefinition def) || def == null)
            {
                Debug.LogWarning(
                    $"[{GetType().Name}] no WeaponDefinition for id '{weaponId}' on '{name}'. " +
                    "The enemy fights unarmed (no projectile, no flare, no report).");
                return null;
            }

            // One child GameObject per weapon, because a unit may carry several at once — the tr3
            // alicorn holds `alilight` and `alipsy` and fires them independently — and
            // EnemyWeaponMount is [DisallowMultipleComponent], so they cannot share a GameObject.
            var mountGo = new GameObject("Weapon_" + weaponId);
            mountGo.transform.SetParent(transform, false);

            EnemyWeaponMount mount = mountGo.AddComponent<EnemyWeaponMount>();
            mount.Initialize(def, Faction, _projectileFactory, _resolver, _soundService, _debugSettings, _rng);
            _weaponMounts[weaponId] = mount;
            return mount;
        }

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
            PlayDeathSound();
            if (_brain != null)
            {
                _brain.SetState(EnemyAIState.Dead);
            }
        }

        /// <summary>
        /// AS3 <c>Unit.as:4316-4318</c> — <c>if(param1 == 0 &amp;&amp; sost == 1 &amp;&amp; sndDie)
        /// sound(sndDie)</c>, i.e. the unit's <c>&lt;snd die='…'/&gt;</c> row, played at the unit.
        ///
        /// <para><b>This is the whole enemy audio path.</b> Until it existed, nothing under
        /// <c>Entities/Enemies</c> or <c>Entities/Units</c> referenced <c>ISoundService</c> at all, so
        /// no enemy in the project made a sound — the alicorn's <c>die='ali'</c> (→ the three
        /// <c>sound_unit/ali1..3.wav</c> variants, already imported and already in
        /// <c>SoundCatalog</c> as the group <c>ali</c>) never played.</para>
        ///
        /// <para>The sound <b>id</b> is data (<see cref="UnitDefinition.deathSoundId"/>) and may be
        /// empty, which is the honest state for a unit whose row carries no <c>die</c> attribute. That
        /// field was declared and written by nobody for all 148 units until the unit importer was fixed
        /// in the same change — so a silent death here means the asset has not been re-imported yet,
        /// not that this call is missing.</para>
        /// </summary>
        protected virtual void PlayDeathSound()
        {
            if (_soundService == null) return;

            string id = Stats != null ? Stats.deathSoundId : null;
            if (string.IsNullOrEmpty(id)) return;

            _soundService.Play(id, transform.position);
        }
    }
}

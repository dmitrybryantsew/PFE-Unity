using System.Collections.Generic;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// MonoBehaviour on the player. Owns the active weapon controller and
    /// drives it every FixedUpdate. Replaces PlayerController's weapon
    /// ownership so that PlayerController stays focused on movement and input routing.
    ///
    /// Responsibilities:
    ///   - Create the correct IWeaponController via WeaponControllerFactory on equip
    ///   - Dispose the previous controller when switching weapons
    ///   - Call controller.Tick() each FixedUpdate with mount positions and aim target
    ///   - Collect ShotPlans and forward them to ProjectileSpawner and WeaponPresenter
    ///   - Expose reactive state (ammo, reload) for HUD binding
    ///
    /// Setup:
    ///   1. Add this component to the player root GameObject alongside PlayerController.
    ///   2. Assign _startingWeaponDef in the Inspector (any pistol/rifle to start).
    ///   3. Assign _mounts (WeaponMounts component on the same or child GameObject).
    ///   4. Assign _weaponPresenter (WeaponPresenter component on the weapon child GameObject).
    ///   5. Assign _projectileSpawner (ProjectileSpawner component on the player or weapon child).
    ///   6. Assign _meleeHitVolume (MeleeHitVolume component on a trigger child of the weapon object).
    ///      Leave null if no melee weapons are used.
    ///
    /// Execution order: -100 so it runs before CharacterAnimationDriver (-50)
    /// but after PlayerLocomotionController (-200).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponLoadout : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Starting weapon")]
        [SerializeField]
        [Tooltip("WeaponDefinition asset equipped at game start. Assign any pistol/rifle for testing.")]
        private WeaponDefinition _startingWeaponDef;

        [Header("References")]
        [SerializeField]
        [Tooltip("WeaponMounts component providing hold point and horn point positions.")]
        private WeaponMounts _mounts;

        [SerializeField]
        [Tooltip("WeaponPresenter drives the weapon sprite. Assign the component on the weapon child.")]
        private WeaponPresenter _weaponPresenter;

        [SerializeField]
        [Tooltip("ProjectileSpawner converts ShotPlans into scene GameObjects.")]
        private ProjectileSpawner _projectileSpawner;

        [SerializeField]
        [Tooltip("MeleeHitVolume on a trigger child of the weapon GameObject. " +
                 "Assigned to MeleeWeaponController.HitVolume on equip. Leave null if not using melee.")]
        private MeleeHitVolume _meleeHitVolume;

        [Header("Aim")]
        [SerializeField]
        [Tooltip("World-space aim target this frame. Set each Update by PlayerController.HandleAiming().")]
        private Vector2 _aimTarget;

        // ── Injected dependencies ─────────────────────────────────────────────

        private IProjectileFactory _projectileFactory;
        private IObjectResolver    _resolver;
        private PfeDebugSettings   _debugSettings;
        private ISoundService      _soundService;
        private PFE.Core.Rng.IRngService _rng;

        /// <summary>
        /// Ammo source (player inventory). Assign before equipping when inventory is live.
        /// Leave null for training/infinite-ammo mode.
        /// </summary>
        public IAmmoSource AmmoSource
        {
            get => _ammoSource;
            set
            {
                _ammoSource = value;
                // Rebuild factory so future Equip() calls pick up the source.
                RebuildFactory();
            }
        }
        private IAmmoSource _ammoSource;

        /// <summary>
        /// Resolves a fired round's <b>id</b> to its ballistics row, so the round's damage/pierce/
        /// armour/knock/fire/det/type terms reach the shot.
        ///
        /// <para><b>Why it is built here and not injected as an interface.</b> The only production
        /// implementation is <see cref="ContentRegistryAmmoResolver"/>, which wraps the DI-singleton
        /// <see cref="ContentRegistry"/>. Registering the resolver itself would mean a second
        /// registration that has to be kept in step with the registry's; constructing it from the
        /// registry the container already owns keeps one source of truth. Null — before
        /// <see cref="Construct"/> runs, or in a plain-<c>Awake</c> test rig — means every shot
        /// resolves no round, which is AS3's failure branch and leaves the weapon's own numbers
        /// untouched.</para>
        /// </summary>
        private IAmmoResolver _ammoResolver;

        /// <summary>
        /// The owner's RPG multipliers — the <c>Pers</c> fields AS3 copies onto the weapon in
        /// <c>Weapon.setPers</c> (<c>Weapon.as:963-1050</c>): reload speed, recoil, jam chance and
        /// the energy-ammo recycle chance.
        ///
        /// <para>Resolved automatically on <see cref="Equip"/> from the parent
        /// <c>UnitController</c>'s <c>CharacterStats</c>, so the player gets them without any
        /// Inspector wiring. Left null (no CharacterStats in the hierarchy) the weapon falls back to
        /// the AS3 declaration defaults 1/1/1/0, which is exactly what AS3 does for a unit whose
        /// <c>Pers</c> was never set up.</para>
        /// </summary>
        public IWeaponStatSource WeaponStatSource
        {
            get => _weaponStatSource;
            set
            {
                _weaponStatSource = value;
                RebuildFactory();
            }
        }
        private IWeaponStatSource _weaponStatSource;

        /// <summary>
        /// Rebuild the controller factory so the next <see cref="Equip"/> sees the current
        /// dependencies. Every construction site goes through here: the three call sites used to
        /// each spell the argument list out, and the <c>Awake</c> copy had silently drifted — it
        /// passed no RNG, so a weapon equipped before <c>Construct</c> ran drew from an unseeded
        /// stream of its own.
        /// </summary>
        private void RebuildFactory()
        {
            _factory = new WeaponControllerFactory(_debugSettings, _ammoSource, _rng, _weaponStatSource, _ammoResolver);
        }

        [Inject]
        public void Construct(IProjectileFactory projectileFactory, IObjectResolver resolver,
                              PfeDebugSettings debugSettings, ISoundService soundService,
                              PFE.Core.Rng.IRngService rng = null, PFE.Data.ContentRegistry registry = null)
        {
            _projectileFactory = projectileFactory;
            _resolver          = resolver;
            _soundService      = soundService;
            _debugSettings     = debugSettings;
            _rng               = rng;
            // Turn the ammo id a weapon names into its ballistics row. Built from the registry the
            // container owns rather than a separate registration, so there is exactly one place the
            // lookup is configured. A null registry (a test rig) leaves the resolver null, and every
            // shot then fires with the weapon's own numbers — AS3's "неправильный патрон" branch.
            _ammoResolver = registry != null ? new ContentRegistryAmmoResolver(registry) : null;
            // Unconditional: injection always supplies at least as much as Awake had, and the old
            // `??=` here meant an Awake-created factory was never upgraded with the real RNG.
            RebuildFactory();
        }

        // ── Runtime ──────────────────────────────────────────────────────────

        private WeaponControllerFactory _factory;
        private IWeaponController _current;
        private bool _wasReloading;     // tracks previous-frame reload state for sound trigger
        private bool _prepWasAttacking; // tracks previous-frame attack state for prep sound edges
        private readonly object _prepSoundKey = new object(); // stable key for ISoundService loop

        // Pending shots from this FixedUpdate — forwarded to spawner/presenter.
        private readonly List<ShotPlan> _pendingPlans = new();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Currently active controller. Null if no weapon is equipped.</summary>
        public IWeaponController Current => _current;

        /// <summary>Set the world-space aim target. Called by PlayerController each Update.</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        /// <summary>Equip a weapon by definition, disposing the previous controller.</summary>
        public void Equip(WeaponDefinition def)
        {
            if (def == null)
            {
                Debug.LogWarning("[PlayerWeaponLoadout] Equip called with null definition — skipping.");
                return;
            }

            _soundService?.StopLoop(_prepSoundKey);
            _prepWasAttacking = false;
            _current?.Dispose();

            // Stamp the owner's faction onto the weapon state — this is the only place that knows
            // both the weapon and whose it is, and FactionRule needs the attacker's side at hit time.
            // AS3 reads the same value as `this.weap.owner.fraction`. Resolved through the parent
            // because the loadout sits on the player or one of its children; a missing UnitController
            // leaves Neutral, which makes the weapon hit everyone (AS3's own default) rather than
            // silently picking a side.
            FactionType ownerFaction = FactionType.Neutral;
            var ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();
            if (ownerUnit != null) ownerFaction = ownerUnit.Faction;

            // Resolve the owner's RPG stats before building the controller — AS3 reads them in
            // Weapon.setPers, which runs at equip time. Done here rather than in Awake because the
            // UnitController / CharacterStats may be added to the hierarchy after this component
            // wakes, and because the reference only needs refreshing when it actually changes: the
            // source is read live on every shot, so a perk taken mid-fight needs no re-equip.
            var charStats = ownerUnit != null
                ? ownerUnit.GetComponent<PFE.Systems.RPG.CharacterStats>()
                : null;
            if (!ReferenceEquals(charStats, _weaponStatSource))
            {
                _weaponStatSource = charStats;
                RebuildFactory();
            }

            _current = _factory.Create(def, ownerFaction);

            // Wire MeleeHitVolume to the controller when it's a melee weapon.
            if (_current is MeleeWeaponController meleeCtrl)
            {
                meleeCtrl.HitVolume = _meleeHitVolume;
                _meleeHitVolume?.SetActive(false);   // ensure disabled until first swing
            }
            else
            {
                // Ensure hit volume is off when any non-melee weapon is equipped.
                _meleeHitVolume?.SetActive(false);
            }

            // Notify sub-systems about the new weapon.
            _weaponPresenter?.SetState(_current.State, def);
            _projectileSpawner?.SetWeapon(def);

            // Sync weapon skill tier to owner's UnitStats (charStats resolved above).
            if (ownerUnit != null && ownerUnit.UnitStats != null && charStats != null)
            {
                ownerUnit.UnitStats.weaponSkillLevel = charStats.GetSkillTierForWeapon(def.skillLevel);
            }

            Debug.Log($"[PlayerWeaponLoadout] Equipped '{def.weaponId}'.");
        }

        // ── Input API (called by PlayerController) ────────────────────────────

        public void BeginAttack()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] BeginAttack() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.BeginAttack();
        }

        public void EndAttack()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] EndAttack() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.EndAttack();
        }

        public void StartReload()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[PlayerWeaponLoadout] StartReload() forwarding to '{_current?.GetType().Name ?? "null"}'.");
            _current?.StartReload();
        }

        // ── Reactive state accessors for HUD ──────────────────────────────────
        // R3 uses ReactiveProperty<T> directly (no IReadOnlyReactiveProperty interface like UniRx).

        public ReactiveProperty<int>   CurrentAmmo       => _current?.State.CurrentAmmoRP;
        public ReactiveProperty<bool>  IsReloading       => _current?.State.IsReloadingRP;
        public ReactiveProperty<float> ReloadProgress    => _current?.State.ReloadProgressRP;
        public ReactiveProperty<int>   CurrentDurability => _current?.State.CurrentDurabilityRP;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Route the Awake fallback through RebuildFactory like every other site. The old inline
            // `??=` spelling had already drifted once (it dropped the RNG) and would have dropped the
            // ammo resolver too — a factory built here is only ever replaced by Construct, so an
            // inline copy can go stale in exactly the window a weapon is equipped from the Inspector.
            if (_factory == null)
                RebuildFactory();

            if (_mounts == null)
                _mounts = GetComponent<WeaponMounts>() ?? GetComponentInChildren<WeaponMounts>();

            if (_weaponPresenter == null)
                _weaponPresenter = GetComponentInChildren<WeaponPresenter>();

            if (_projectileSpawner == null)
                _projectileSpawner = GetComponentInChildren<ProjectileSpawner>();

            if (_meleeHitVolume == null)
                _meleeHitVolume = GetComponentInChildren<MeleeHitVolume>();

            if (_mounts == null)
                Debug.LogWarning("[PlayerWeaponLoadout] No WeaponMounts found — mount positions will use transform.position. " +
                                 "Add WeaponMounts to the player and assign hold point Transforms.", this);
        }

        private void Start()
        {
            // Initialize the spawner with the factory resolved via DI.
            // Done in Start so [Inject] Construct() has already run.
            if (_projectileSpawner != null && _projectileFactory != null)
            {
                _projectileSpawner.Initialize(_projectileFactory, _resolver, _debugSettings);
                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.Log("[PlayerWeaponLoadout] ProjectileSpawner initialized with IProjectileFactory.");
            }
            else if (_projectileSpawner != null)
                Debug.LogWarning("[PlayerWeaponLoadout] IProjectileFactory not injected — ProjectileSpawner will not fire.", this);

            // MeleeHitVolume uses [Inject] for IPublisher<DamageDealtMessage> but is a
            // scene MonoBehaviour, not registered in the container. Inject it manually
            // via the resolver so MessagePipe publisher is wired without adding it to the
            // container separately.
            if (_meleeHitVolume != null && _resolver != null)
                _resolver.Inject(_meleeHitVolume);

            if (_startingWeaponDef != null)
                Equip(_startingWeaponDef);
            else
                Debug.LogWarning("[PlayerWeaponLoadout] No starting weapon assigned. Player will start unarmed.", this);
        }

        private void Update()
        {
            // Push current aim target to the presenter each frame so LateUpdate has fresh data.
            _weaponPresenter?.SetAimTarget(_aimTarget);
        }

        private void FixedUpdate()
        {
            if (_current == null) return;

            Vector2 holdPoint = _mounts != null ? _mounts.WeaponHoldPoint : (Vector2)transform.position;
            Vector2 hornPoint = _mounts != null ? _mounts.MagicHoldPoint  : (Vector2)transform.position;

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                var state = _current.State;
                if (state.TAttack > 0 || state.TReload > 0 || state.TPrep > 0)
                {
                    Debug.Log(
                        $"[PlayerWeaponLoadout] FixedUpdate pre-Tick weapon='{state.Def.weaponId}' " +
                        $"ammo={state.CurrentAmmo} tAttack={state.TAttack} tReload={state.TReload} tPrep={state.TPrep} " +
                        $"hold={holdPoint} aim={_aimTarget}.");
                }
            }

            _current.Tick(Time.fixedDeltaTime, holdPoint, hornPoint, _aimTarget);

            // ── Prep sound — minigun/flamer spin-up loop (AS3: sndPrep / t1 / t2) ──
            TickPrepSound();

            // ── Reload sound — fires once on the frame reload begins ──────────
            bool nowReloading = _current.State.IsReloadingRP.Value;
            if (_soundService != null && nowReloading && !_wasReloading)
            {
                var rSnd = _current.State.Def.soundReload;
                if (!string.IsNullOrEmpty(rSnd))
                    _soundService.Play(rSnd, holdPoint);
            }
            _wasReloading = nowReloading;

            // Collect ShotPlans for this tick.
            _pendingPlans.Clear();
            _pendingPlans.AddRange(_current.FlushShotPlans());

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                var state = _current.State;
                if (_pendingPlans.Count > 0 || state.TAttack > 0 || state.TReload > 0 || state.TPrep > 0)
                {
                    Debug.Log(
                        $"[PlayerWeaponLoadout] FixedUpdate post-Tick weapon='{state.Def.weaponId}' " +
                        $"ammo={state.CurrentAmmo} tAttack={state.TAttack} tReload={state.TReload} tPrep={state.TPrep} " +
                        $"plans={_pendingPlans.Count}.");
                }
            }

            if (_pendingPlans.Count > 0)
            {
                // Forward damage context of the latest MeleeSweep plan to MeleeHitVolume.
                // Multiple plans per tick are rare for melee, but we take the last one.
                if (_meleeHitVolume != null)
                {
                    for (int i = _pendingPlans.Count - 1; i >= 0; i--)
                    {
                        if (_pendingPlans[i].Kind == ShotKind.MeleeSweep)
                        {
                            _meleeHitVolume.SetDamageContext(_pendingPlans[i].Damage);
                            break;
                        }
                    }
                }

                // ── Fire sound — first plan with PlayShootSound flag ──────────
                if (_soundService != null)
                {
                    for (int i = 0; i < _pendingPlans.Count; i++)
                    {
                        if (_pendingPlans[i].Cues.PlayShootSound)
                        {
                            var sSnd = _current.State.Def.soundShoot;
                            if (!string.IsNullOrEmpty(sSnd))
                                _soundService.Play(sSnd, holdPoint);
                            break;
                        }
                    }
                }

                _projectileSpawner?.SpawnFromPlans(_pendingPlans);
            }
        }

        /// <summary>
        /// Mirrors the sndPrep block in Weapon.actions() — runs every FixedUpdate after Tick().
        ///
        /// minigun_s (and similar) is a single audio file with three sections:
        ///   [0 .. t1]   spin-up     (plays proportional to current prep charge on trigger press)
        ///   [t1 .. t2]  fire loop   (loops while weapon is actively firing)
        ///   [t2 .. end] spin-down   (triggered on trigger release while still spinning)
        ///
        /// AS3 uses millisecond positions (sndCh.position); soundPrepT1/T2 are stored in ms.
        /// Unity AudioSource.time is in seconds, so divide by 1000.
        /// </summary>
        private void TickPrepSound()
        {
            if (_soundService == null) return;
            var def = _current.State.Def;
            if (string.IsNullOrEmpty(def.soundPrep)) return;

            // State.WasAttack: whether attack was held during this tick's flash frames.
            // State.TPrep: current prep charge (>0 means weapon is spinning / wound up).
            bool isAttacking = _current.State.WasAttack;
            int  tPrep       = _current.State.TPrep;

            float t1Sec = def.soundPrepT1 / 1000f;
            float t2Sec = def.soundPrepT2 / 1000f;

            if (!_prepWasAttacking && isAttacking)
            {
                // Rising edge — start sound, seeking past the spin-up section proportional
                // to how wound-up the weapon already is. AS3: Snd.ps(sndPrep, x, y, t_prep*30).
                float startSec = tPrep * (1f / 30f); // 1 flash frame = 1/30 s → ms = frame*33, but AS3 uses *30
                _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, startSec);
            }
            else if (isAttacking && t1Sec > 0f && t2Sec > 0f)
            {
                // Loop-back: while firing, when playback approaches t2 jump back to t1.
                // AS3: if sndCh.position > snd_t_prep2 - 300 → restart at snd_t_prep1 + 200.
                float pos = _soundService.GetLoopTime(_prepSoundKey);
                if (pos >= 0f && pos > t2Sec - 0.3f)
                    _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, t1Sec + 0.2f);
            }
            else if (_prepWasAttacking && !isAttacking && tPrep > 0)
            {
                // Falling edge while still spinning — jump to spin-down section.
                // AS3: if sndCh.position < snd_t_prep2 - 400 → restart at snd_t_prep2 + 100.
                float pos = _soundService.GetLoopTime(_prepSoundKey);
                if (t2Sec > 0f && pos >= 0f && pos < t2Sec - 0.4f)
                    _soundService.PlayLoopFromTime(def.soundPrep, _prepSoundKey, t2Sec + 0.1f);
                // If already past t2 (in spin-down), let it play naturally to clip end.
            }
            else if (!isAttacking && tPrep == 0)
            {
                // Weapon fully wound down — stop.
                _soundService.StopLoop(_prepSoundKey);
            }

            _prepWasAttacking = isAttacking;
        }

        private void OnDestroy()
        {
            _soundService?.StopLoop(_prepSoundKey);
            _current?.Dispose();
            _current = null;
            _weaponPresenter?.ClearState();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_mounts == null)
                _mounts = GetComponent<WeaponMounts>() ?? GetComponentInChildren<WeaponMounts>();
            if (_weaponPresenter == null)
                _weaponPresenter = GetComponentInChildren<WeaponPresenter>();
            if (_projectileSpawner == null)
                _projectileSpawner = GetComponentInChildren<ProjectileSpawner>();
            if (_meleeHitVolume == null)
                _meleeHitVolume = GetComponentInChildren<MeleeHitVolume>();
        }
#endif
    }
}

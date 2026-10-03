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
using PFE.Systems.Magic;
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
            // The stat source doubles as the mana source: CharacterStats implements both, and a test
            // double that only supplies multipliers simply yields null mana ("no tracking"). This is
            // why no separate field is needed — the two seams are the same object in production.
            _factory = new WeaponControllerFactory(_debugSettings, _ammoSource, _rng, _weaponStatSource,
                                                   _ammoResolver, _weaponStatSource as IManaSource);
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

        /// <summary>
        /// The unit that owns this loadout. Cached because the hold point is derived from its body
        /// every frame (<see cref="WeaponHoldPointMath"/>) and the muzzle push needs it too, while
        /// <c>GetComponentInParent</c> is a hierarchy walk that has no business in a fixed step.
        /// </summary>
        private PFE.Entities.Units.UnitController _ownerUnit;

        /// <summary>
        /// The player's spell caster, resolved lazily and cached.
        ///
        /// <para>Lazy rather than cached in <c>Awake</c> because <see cref="PFE.Entities.Player.PlayerController"/>
        /// adds the caster during its own <c>Awake</c> and Unity does not order two components' <c>Awake</c>
        /// calls — a cache taken too early would hold a null forever. <c>GetComponentInParent</c> is a
        /// hierarchy walk, so it is done once, on the first spell selection, and only re-done while it is
        /// still null.</para>
        /// </summary>
        private PlayerSpellCaster _spellCaster;

        // Pending shots from this FixedUpdate — forwarded to spawner/presenter.
        private readonly List<ShotPlan> _pendingPlans = new();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Currently active controller. Null if no weapon is equipped.</summary>
        public IWeaponController Current => _current;

        /// <summary>Set the world-space aim target. Called by PlayerController each Update.</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        /// <summary>
        /// The current world-space aim target, in <b>Unity units</b>. Read by the spell caster, which
        /// needs the same point the weapon is aiming at: AS3 casts at <c>World.w.celX</c>/<c>celY</c>
        /// (<c>UnitPlayer.as:2234</c>), the cursor, and this is the port's equivalent.
        /// </summary>
        public Vector2 AimTarget => _aimTarget;

        /// <summary>Equip a weapon by definition, disposing the previous controller.</summary>
        public void Equip(WeaponDefinition def)
        {
            if (def == null)
            {
                Debug.LogWarning("[PlayerWeaponLoadout] Equip called with null definition — skipping.");
                return;
            }

            // ── A spell is not equipped; it is cast ──────────────────────────────────────────────
            //
            // This is the seam AS3 uses, and the reason the `spell` flag exists at all: the nine
            // supportive-magic items are tip='5' like the assault magic, but they are items you use
            // from the inventory. UnitPlayer.changeWeapon sees the flag and calls
            // `invent.useItem(id)` instead of switching (UnitPlayer.as:3627-3635), so the weapon in
            // hand is left untouched.
            //
            // Returning here — before the Dispose below — reproduces that: selecting a spell does
            // not disarm you.
            if (def.spell)
            {
                // The dispatch the earlier passes left as a refusal. `useItem`'s tip == "spell" branch
                // (Invent.as:625-633) calls `gg.changeSpell(id)`, and changeSpell only *toggles the
                // selection* — it never casts. The cast happens on the next tick, on the Def key or a
                // favourite hotkey (UnitPlayer.as:2226-2284), which PlayerSpellCaster now drives.
                //
                // LearnSpell first, then Select: `Select` is a lookup, so an id the book has never seen
                // would be treated as "unknown id" and clear the selection instead (the oracle relies on
                // that for the deselect case, Invent.as:849-852). The item being used is what proves the
                // spell is owned, which is exactly AS3's precondition — `invent.spells[id]` is populated
                // by addSpell when the item is taken, before useItem can be reached.
                var caster = ResolveSpellCaster();
                if (caster == null)
                {
                    Debug.LogWarning(
                        $"[PlayerWeaponLoadout] '{def.weaponId}' is a supportive spell (AS3 weapon@spell) " +
                        "but no PlayerSpellCaster was found on the player — the spell cannot be selected. " +
                        "Nothing is equipped, which is correct: a spell is never a weapon.");
                    return;
                }

                caster.LearnSpell(def.weaponId);
                Spell selected = caster.SelectSpell(def.weaponId);

                Debug.Log(selected != null
                    ? $"[PlayerWeaponLoadout] '{def.weaponId}' selected as the current spell " +
                      "(AS3 changeSpell) — cast it with the Def key."
                    : $"[PlayerWeaponLoadout] '{def.weaponId}' toggled OFF (AS3 changeSpell is a toggle) — " +
                      "no spell is selected.");
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
            // Awake caches the owner; re-resolve only if it was missing then, for a rig assembled
            // after Awake. The common path therefore does no hierarchy walk.
            if (_ownerUnit == null)
                _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

            FactionType ownerFaction = FactionType.Neutral;
            var ownerUnit = _ownerUnit;
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

            if (_current == null)
            {
                // The factory returns null for a definition with no held controller. Today that is
                // only a supportive spell, which this method refuses above — but the next lines
                // dereference _current, so the possibility is guarded rather than assumed. A silent
                // NRE here would surface as an unrelated crash in the presenter.
                Debug.LogWarning($"[PlayerWeaponLoadout] No controller for '{def.weaponId}' — nothing equipped.");
                return;
            }

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

        /// <summary>
        /// The player's <see cref="PlayerSpellCaster"/>, or null when the rig has none.
        ///
        /// <para>Resolved through the parent because the loadout may sit on a child of the player, and
        /// the caster is added to the player root. A null result is reported by the caller rather than
        /// swallowed, because "the spell did not select" and "there is no caster" have different fixes
        /// and look identical from the outside.</para>
        /// </summary>
        private PlayerSpellCaster ResolveSpellCaster()
        {
            if (_spellCaster == null)
                _spellCaster = GetComponentInParent<PlayerSpellCaster>();
            return _spellCaster;
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

            _ownerUnit = GetComponentInParent<PFE.Entities.Units.UnitController>();

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

            Vector2 holdPoint = ResolveHoldPoint();
            Vector2 hornPoint = ResolveHornPoint();

            // Hand the ranged controller the barrel tip the presenter drew last frame, so the round
            // leaves the muzzle instead of the weapon's origin. Must happen BEFORE Tick(), because
            // Tick() is what runs Shoot() and consumes the value.
            //
            // The value is one LateUpdate old, which is the correct pairing rather than a lag: the
            // presenter computes it from State.X/Y *after* the controller's own Tick wrote them, so
            // the point matches the frame the gun is currently drawn at. AS3 reads vis.emit at
            // fire time from the same display list it just animated.
            if (_current is RangedWeaponController ranged)
                ranged.MuzzleWorldPoint = _weaponPresenter != null ? _weaponPresenter.MuzzleWorldPosition : null;

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
        /// Whether an authored mount may be trusted as a world-space point for this rig.
        ///
        /// <para><b>The arrangement this rejects, and why it is not hypothetical.</b> The presenter
        /// writes the weapon's world position <i>from</i> the hold point. A hold point that is a
        /// descendant of the weapon therefore moves when the weapon moves, and the weapon chases it:
        /// measured from <c>Player.prefab</c>, the <c>muzzle</c> node sits 0.33 u out and 0.04 u up from
        /// the weapon node, and <c>RangedWeaponController</c> closes 1/5 of the gap every flash frame
        /// (<c>State.X += (holdPoint.x - State.X) / 5</c>) — so the gun walks away from the character at
        /// roughly 2 u/s. All three mounts in that prefab are wired to that node, which is the reported
        /// "levitation".</para>
        ///
        /// <para>A null <paramref name="mount"/> means "nothing authored" and is not usable either.
        /// A null presenter means nothing is being positioned, so there is no loop to create.</para>
        /// </summary>
        private bool MountIsUsable(Transform mount)
            => mount != null && (_weaponPresenter == null || !mount.IsChildOf(_weaponPresenter.transform));

        /// <summary>
        /// Where the held weapon sits this tick — the port of AS3 <c>UnitPlayer.setWeaponPos</c>.
        ///
        /// <para><b>Why the derived point wins.</b> AS3 has no hold-point marker: the point is computed
        /// every frame from the owner's body box and the cursor, and it is the only value that cannot
        /// depend on the thing it positions. The loadout used to read
        /// <c>WeaponMounts.WeaponHoldPoint</c> unconditionally, and in the shipped rig that resolves to a
        /// node inside the weapon itself — see <see cref="MountIsUsable"/> for the measured drift that
        /// produced. A derived point makes that wiring inert, which is the correct outcome for a
        /// mis-wired mount.</para>
        ///
        /// <para><b>An authored mount is still honoured where nothing can be derived.</b> With no
        /// <c>UnitController</c> there is no body box, so a rig that supplies its own point (and that
        /// point is not inside the weapon) keeps it.</para>
        /// </summary>
        private Vector2 ResolveHoldPoint()
        {
            if (_ownerUnit == null)
            {
                // No UnitController: an enemy rig or a bare test object. Nothing to derive a body box
                // from, so fall back rather than inventing one.
                Transform authored = _mounts != null ? _mounts.WeaponHoldPointTransform : null;
                return MountIsUsable(authored)
                    ? _mounts.WeaponHoldPoint
                    : (Vector2)transform.position;
            }

            UnitDefinition stats = _ownerUnit.Stats;
            WeaponDefinition def = _current.State.Def;

            var inputs = new WeaponHoldPointMath.Inputs
            {
                OwnerX     = _ownerUnit.transform.position.x,
                // The unit origin is the feet — see UnitController.FeetWorldY for the three places
                // that agree on it. AS3's `Y` is the same point.
                OwnerFeetY = _ownerUnit.FeetWorldY,
                BodyWidth  = stats != null && stats.Width  > 0f ? stats.Width  : 0.5f,
                BodyHeight = stats != null && stats.Height > 0f ? stats.Height : 0.7f,
                FacingSign = _ownerUnit.FacingDirection,
                AimX       = _aimTarget.x,
                // WeaponType is imported straight from the AS3 tip attribute
                // (`WeaponDataImporter.cs:213` — `(WeaponType)AttrI(rootAttrs, "tip", 0)`), so the
                // enum's numeric value *is* the oracle's `tip`.
                Tip        = (int)def.weaponType,
                // AS3 `weaponKrep` is the owner's `krep` attribute, which the port stores as
                // UnitDefinition.isStable (`UnitDataImporter.cs:553-558`, `krep == 1`). It is the
                // branch selector: zero means "derive the hold point", non-zero means "use the base
                // class's flat waist height".
                WeaponKrep = stats != null && stats.isStable ? 1f : 0f,

                // Not wired: the port tracks no per-unit stair state, no weapon-up state and no
                // weapon-swap phase, so these stay at their "none of that is happening" values. Each
                // only ever moves the weapon back toward the body, so leaving them false is the
                // open-ground behaviour — the one the reported bug is about. Named here rather than
                // silently defaulted so the next reader can see exactly what is and is not ported.
                OnStairs       = false,
                Stay           = false,
                WeaponUp       = false,
                SwappingWeapon = false,
            };

            // No tile predicate: the loadout has no room reference, and the two clamps that use it
            // only pull the weapon back off a wall it would otherwise poke into. Passing null means
            // they never fire, which is AS3's result in open ground.
            return WeaponHoldPointMath.Resolve(inputs, isSolidAt: null);
        }

        /// <summary>
        /// The magic / horn mount. AS3 derives it from the rig's muzzle bone and falls back to the
        /// body (<c>UnitPlayer.as:3561-3584</c>); the port takes an authored Transform when there is
        /// one and otherwise uses the oracle's own fallback heights.
        ///
        /// <para><b>Why the authored mount does not simply win.</b> The shipped rig points
        /// <c>_magicHoldPoint</c> at the same <c>muzzle</c> node inside the weapon, so reading it
        /// unguarded is the identical feedback loop the weapon hold point had — and worse for magic,
        /// because the magic controller <i>snaps</i> the weapon to the horn point rather than lerping
        /// toward it, so the loop has no damping at all. The mount is honoured only where the point
        /// cannot be derived and the mount is not inside the weapon.</para>
        /// </summary>
        private Vector2 ResolveHornPoint()
        {
            if (_ownerUnit == null)
            {
                if (_mounts == null) return (Vector2)transform.position;

                // Keep the documented fallback chain — horn, then hold point, then the rig origin —
                // but skip any link that lives inside the weapon. A circular mount is worse than no
                // mount: it drags the weapon instead of holding it.
                if (MountIsUsable(_mounts.MagicHoldPointTransform))  return _mounts.MagicHoldPoint;
                if (MountIsUsable(_mounts.WeaponHoldPointTransform)) return _mounts.WeaponHoldPoint;
                return (Vector2)transform.position;
            }

            UnitDefinition stats = _ownerUnit.Stats;
            return WeaponHoldPointMath.ResolveMagicFallback(
                _ownerUnit.transform.position.x,
                _ownerUnit.FeetWorldY,
                stats != null && stats.Height > 0f ? stats.Height : 0.7f);
        }

        /// <summary>
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

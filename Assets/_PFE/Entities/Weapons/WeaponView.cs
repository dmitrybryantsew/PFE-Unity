using UnityEngine;
using VContainer;
using R3;
using PFE.Data.Definitions;
using PFE.Core.Time;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Entities.Units;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Visual / audio representation of a weapon held by a character.
    /// Replaces Weapon.as from ActionScript (2,116 lines).
    ///
    /// Responsibilities:
    ///   - Rotate the weapon GameObject toward the mouse / target direction.
    ///   - Flip the weapon sprite when aiming left.
    ///   - Fire: call WeaponLogic, then spawn projectile via IProjectileFactory.
    ///   - Play shoot and reload sounds via ISoundService.
    ///
    /// The projectile prefab is NOT stored here. WeaponDefinition.projectileArchetype
    /// tells the ProjectileFactory which prefab to fetch from ProjectilePrefabRegistry.
    /// No SerializeField prefab references needed — just wire the dependencies.
    ///
    /// Setup in scene:
    ///   1. Add this component to a child GameObject of the character.
    ///   2. Optionally add a child named "Muzzle" for exact spawn position.
    ///   3. Call Initialize(weaponLogic, ownerStats) after construction.
    ///      WeaponFactory.CreateWeaponView() does this automatically.
    /// </summary>
    public class WeaponView : MonoBehaviour
    {
        [Header("Setup")]
        [SerializeField]
        [Tooltip("Transform where bullets spawn — auto-found by name 'Muzzle' if left empty.")]
        private Transform _muzzlePoint;

        [Header("Debug")]
        [SerializeField] private bool _showDebugGizmos;

        // ── Injected dependencies (via VContainer) ───────────────────────────

        private ITimeProvider      _timeProvider;
        private IProjectileFactory _projectileFactory;
        private ISoundService      _soundService;
        private PFE.Core.PfeDebugSettings _debugSettings;

        // ── Runtime state ────────────────────────────────────────────────────

        private WeaponLogic _weaponLogic;
        private UnitStats   _ownerStats;
        private bool        _isTriggerHeld;
        private CompositeDisposable _disposables;

        // ── VContainer injection point ───────────────────────────────────────

        [Inject]
        public void Construct(ITimeProvider timeProvider,
                              IProjectileFactory projectileFactory,
                              ISoundService soundService,
                              PFE.Core.PfeDebugSettings debugSettings)
        {
            _timeProvider      = timeProvider;
            _projectileFactory = projectileFactory;
            _soundService      = soundService;
            _debugSettings     = debugSettings;
            if (_debugSettings.LogDependencyInjectionConstruct)
                Debug.Log("[WeaponView] Construct() called — dependencies injected.");
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────

        private void Awake()
        {
            if (_muzzlePoint == null)
            {
                foreach (Transform child in GetComponentsInChildren<Transform>())
                {
                    if (child.name == "Muzzle") { _muzzlePoint = child; break; }
                }
                if (_muzzlePoint == null)
                {
                    _muzzlePoint = transform;
                    Debug.LogWarning("[WeaponView] No child named 'Muzzle' found — " +
                                     "using weapon transform as muzzle point.");
                }
            }

            _disposables = new CompositeDisposable();
        }

        private void Update()
        {
            if (_isTriggerHeld) Shoot();
        }

        private void OnDestroy() => _disposables?.Dispose();

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Links this view to a WeaponLogic instance and owner stats.
        /// Must be called once after instantiation (WeaponFactory does this).
        /// </summary>
        public void Initialize(WeaponLogic logic, UnitStats ownerStats)
        {
            _weaponLogic = logic;
            _ownerStats  = ownerStats;
            if (_debugSettings?.LogWeaponLifecycle == true)
                Debug.Log($"[WeaponView] Initialize() called — weapon: {logic?.WeaponDef?.weaponId ?? "null"}.");

            var def = logic.WeaponDef;
            if (def == null || string.IsNullOrEmpty(def.soundReload)) return;

            // Play reload sound when reload starts.
            logic.IsReloading
                .Subscribe(reloading =>
                {
                    if (reloading)
                        _soundService?.Play(def.soundReload, transform.position);
                })
                .AddTo(_disposables);
        }

        /// <summary>
        /// Rotate the weapon toward a world-space target (typically the mouse cursor).
        /// Flips the sprite Y when aiming left so the weapon is never upside-down.
        /// Matches AS3 Weapon.actions() rotation + storona flip logic.
        /// </summary>
        public void RotateTowards(Vector3 targetPos)
        {
            Vector3 dir   = targetPos - transform.position;
            float   angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);

            // Flip Y scale when pointing left (90° < |angle| ≤ 270°)
            bool pointingLeft = angle > 90f || angle < -90f;
            transform.localScale = pointingLeft
                ? new Vector3(1f, -1f, 1f)
                : new Vector3(1f,  1f, 1f);
        }

        /// <summary>Starts auto-fire (attack button held).</summary>
        public void BeginFiring()
        {
            _isTriggerHeld = true;
            if (_debugSettings?.LogWeaponLifecycle == true)
                Debug.Log("[WeaponView] BeginFiring().");
        }

        public void EndFiring()
        {
            _isTriggerHeld = false;
            if (_debugSettings?.LogWeaponLifecycle == true)
                Debug.Log("[WeaponView] EndFiring().");
        }

        // ── Internal ─────────────────────────────────────────────────────────

        private void Shoot()
        {
            if (_weaponLogic == null)
            {
                Debug.LogWarning("[WeaponView] Cannot shoot — Initialize() has not been called.");
                return;
            }

            bool fired = _weaponLogic.Fire(_ownerStats);
            if (_debugSettings?.LogWeaponFiring == true)
                Debug.Log($"[WeaponView] Shoot() — WeaponLogic.Fire returned {fired}, ammo={_weaponLogic.CurrentAmmo.Value}.");
            if (!fired) return;

            var def = _weaponLogic.WeaponDef;

            if (_projectileFactory == null)
            {
                Debug.LogError("[WeaponView] _projectileFactory is null — Construct() was never called on this instance. " +
                               "This WeaponView was not registered with VContainer.");
                return;
            }

            Vector2 direction = transform.right;
            _projectileFactory.Create(def, _muzzlePoint.position, direction);

            // Shoot sound.
            if (!string.IsNullOrEmpty(def.soundShoot))
                _soundService?.Play(def.soundShoot, _muzzlePoint.position);

            // TODO: spawn muzzle flash (1-2 frame coroutine).
        }

        // ── Debug ────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_showDebugGizmos || _muzzlePoint == null) return;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(_muzzlePoint.position, transform.right * 2f);
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_muzzlePoint.position, 0.1f);
        }
    }
}

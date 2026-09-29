using UnityEngine;
using R3;
using PFE.Systems.Weapons;
using PFE.Entities.Units;

namespace PFE.UI.HUD
{
    /// <summary>
    /// ViewModel for the Heads-Up Display (HUD).
    /// Bridges the gap between game logic (PlayerWeaponLoadout, UnitStats) and UI components.
    /// Exposes reactive properties that UI views can bind to using R3.
    ///
    /// Design Pattern: Model-View-ViewModel (MVVM)
    /// - Model: PlayerWeaponLoadout, UnitStats (game data and logic)
    /// - ViewModel: This class (transforms data for UI consumption)
    /// - View: HealthBarView, AmmoCounterView, etc. (display data)
    ///
    /// Benefits:
    /// - UI doesn't need to know about game logic directly
    /// - Reactive properties update UI automatically when data changes
    /// - Testable (can mock ViewModel without UI)
    /// - Separation of concerns (UI vs game logic)
    /// </summary>
    public class HUDViewModel : MonoBehaviour
    {
        [Header("Game Data Sources")]
        [SerializeField]
        [Tooltip("The player's weapon loadout. Null until the player is found; the bootstrapper " +
                 "re-binds when it appears.")]
        internal PlayerWeaponLoadout playerLoadout;

        [SerializeField]
        [Tooltip("Player stats to display health for")]
        internal UnitStats playerStats;

        // Reactive properties for UI binding
        private ReadOnlyReactiveProperty<int> _currentAmmo;
        private ReadOnlyReactiveProperty<int> _maxAmmo;
        private ReadOnlyReactiveProperty<float> _ammoPercent;
        private ReadOnlyReactiveProperty<float> _reloadProgress;
        private ReadOnlyReactiveProperty<bool> _isReloading;
        private ReadOnlyReactiveProperty<float> _healthPercent;
        private ReadOnlyReactiveProperty<float> _currentHealth;
        private ReadOnlyReactiveProperty<float> _maxHealth;
        private ReadOnlyReactiveProperty<bool> _isAlive;
        private ReadOnlyReactiveProperty<float> _currentMana;
        private ReadOnlyReactiveProperty<float> _maxMana;
        private ReadOnlyReactiveProperty<float> _manaPercent;

        // Composite disposable for cleanup
        private CompositeDisposable _disposables;

        /// <summary>
        /// Current ammo in weapon (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<int> CurrentAmmo => _currentAmmo;

        /// <summary>
        /// Max ammo capacity (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<int> MaxAmmo => _maxAmmo;

        /// <summary>
        /// Ammo percentage 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> AmmoPercent => _ammoPercent;

        /// <summary>
        /// Reload progress 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> ReloadProgress => _reloadProgress;

        /// <summary>
        /// Whether weapon is currently reloading (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<bool> IsReloading => _isReloading;

        /// <summary>
        /// Health percentage 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> HealthPercent => _healthPercent;

        /// <summary>
        /// Current health value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> CurrentHealth => _currentHealth;

        /// <summary>
        /// Maximum health value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> MaxHealth => _maxHealth;

        /// <summary>
        /// Whether player is alive (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<bool> IsAlive => _isAlive;

        /// <summary>
        /// Current mana value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> CurrentMana => _currentMana;

        /// <summary>
        /// Maximum mana value (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> MaxMana => _maxMana;

        /// <summary>
        /// Mana percentage 0-1 (for binding).
        /// </summary>
        public ReadOnlyReactiveProperty<float> ManaPercent => _manaPercent;

        /// <summary>
        /// Initialize the ViewModel with its game data sources.
        /// Call this when setting up the HUD.
        /// </summary>
        /// <param name="loadout">Player's weapon loadout</param>
        /// <param name="stats">Player's stats</param>
        public void Initialize(PlayerWeaponLoadout loadout, UnitStats stats)
        {
            playerLoadout = loadout;
            playerStats = stats;

            // Rebind, not SetupBindings: rebinding replaces the ReadOnlyReactiveProperty instances,
            // and the previous set must be disposed or every re-initialise leaks a live subscription
            // on UnitStats.CurrentHp.
            Rebind();
        }

        // ── Source selection ──────────────────────────────────────────────────
        // The loadout exposes the reactive shape the bindings below need (CurrentAmmo / IsReloading /
        // ReloadProgress as ReactiveProperty<T>). It is the only weapon source since Phase 2.2 retired
        // the legacy WeaponLogic path; a null loadout means "no weapon yet", not "try the other path".

        private ReactiveProperty<int> AmmoSource => playerLoadout?.CurrentAmmo;

        private ReactiveProperty<bool> ReloadingSource => playerLoadout?.IsReloading;

        private ReactiveProperty<float> ReloadProgressSource => playerLoadout?.ReloadProgress;

        /// <summary>
        /// Magazine capacity for the equipped weapon. Read through the live controller rather than
        /// cached: the loadout can swap weapons, and a stale capacity makes the ammo bar lie.
        /// </summary>
        private int MagazineSize => playerLoadout?.Current?.State?.Def?.magazineSize ?? 0;

        /// <summary>True when a weapon source exists and is currently carrying a live weapon.</summary>
        public bool HasWeapon => AmmoSource != null;

        /// <summary>
        /// The stats this ViewModel is currently bound to, or null. Exposed so a bootstrapper can
        /// tell "already bound to this player" from "the player was replaced" without guessing.
        /// </summary>
        public UnitStats StatsSource => playerStats;

        private void Awake()
        {
            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            // If sources are assigned via Inspector, setup bindings on Start.
            //
            // Rebind, not SetupBindings: the bootstrapper adds this component and then drives
            // Initialize() from its own Update, and Unity does not order Start against that. If
            // Start lands after an Initialize, a bare SetupBindings() would append a second set of
            // subscriptions to _disposables instead of replacing the first — the same
            // double-subscription trap the three HUD views had. Rebind() disposes first, so either
            // order converges on exactly one set. The guard stays so the normal path (sources
            // assigned later, at runtime) does not log "stats is null" as an error.
            if (playerStats != null && HasWeapon)
            {
                Rebind();
            }
        }

        /// <summary>
        /// Re-establish bindings against the current sources.
        ///
        /// <para>Needed because a <see cref="PlayerWeaponLoadout"/> creates its controller in its own
        /// <c>Start()</c>, and Unity does not order <c>Start</c> between components — so a HUD that
        /// bound in <c>Start</c> may have run before the weapon existed. The bootstrapper polls
        /// <c>Current</c> and calls this when the equipped controller changes (including weapon
        /// swaps, where the ammo <c>ReactiveProperty</c> instance itself is replaced).</para>
        /// </summary>
        public void Rebind()
        {
            // Drop the previous subscriptions, then take a fresh set. Without the dispose the old
            // ReactivePropertys would keep firing into a view that no longer reads them.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();

            _currentAmmo = null;
            _maxAmmo = null;
            _ammoPercent = null;
            _reloadProgress = null;
            _isReloading = null;
            _healthPercent = null;
            _currentHealth = null;
            _maxHealth = null;
            _isAlive = null;
            _currentMana = null;
            _maxMana = null;
            _manaPercent = null;

            SetupBindings();
        }

        /// <summary>
        /// Set up reactive property bindings from game data sources.
        /// This is where we transform raw game data into UI-friendly reactive properties.
        /// </summary>
        private void SetupBindings()
        {
            if (playerStats == null)
            {
                Debug.LogError("[HUDViewModel] Cannot setup bindings: stats is null!");
                return;
            }

            var ammo = AmmoSource;
            if (ammo == null)
            {
                // Not an error: the loadout equips its starting weapon in Start(), and a HUD built
                // before that legitimately has nothing to show yet. Rebind() will be called when it
                // arrives. Health below still binds, so the player is not left without a HUD.
                Debug.Log("[HUDViewModel] No weapon equipped yet — ammo bindings deferred until Rebind().");
            }
            else
            {
                // Weapon bindings
                _currentAmmo = ammo.ToReadOnlyReactiveProperty();
                _maxAmmo = ammo.Select(_ => MagazineSize).ToReadOnlyReactiveProperty();
                _ammoPercent = ammo.Select(a =>
                {
                    int size = MagazineSize;
                    return size > 0 ? (float)a / size : 0f;
                }).ToReadOnlyReactiveProperty();
                _reloadProgress = ReloadProgressSource.ToReadOnlyReactiveProperty();
                _isReloading = ReloadingSource.ToReadOnlyReactiveProperty();

                _disposables.Add(_currentAmmo);
                _disposables.Add(_maxAmmo);
                _disposables.Add(_ammoPercent);
                _disposables.Add(_reloadProgress);
                _disposables.Add(_isReloading);
            }

            // Stats bindings.
            //
            // Built here rather than using UnitStats.HpPercent: that property allocates a fresh
            // CombineLatest subscription on every read and never disposes it, so reading it once per
            // bind leaks a live subscription each time. Owning it here means Rebind() cleans it up.
            _healthPercent = playerStats.CurrentHp
                .CombineLatest(playerStats.MaxHp, (current, max) => max > 0 ? current / max : 0f)
                .ToReadOnlyReactiveProperty();
            _currentHealth = playerStats.CurrentHp.ToReadOnlyReactiveProperty();
            _maxHealth = playerStats.MaxHp.ToReadOnlyReactiveProperty();
            _isAlive = playerStats.CurrentHp.Select(hp => hp > 0).ToReadOnlyReactiveProperty();

            _disposables.Add(_healthPercent);
            _disposables.Add(_currentHealth);
            _disposables.Add(_maxHealth);
            _disposables.Add(_isAlive);

            // Mana bindings
            _currentMana = playerStats.Mana.ToReadOnlyReactiveProperty();
            _maxMana = playerStats.MaxMana.ToReadOnlyReactiveProperty();
            _manaPercent = playerStats.Mana.CombineLatest(playerStats.MaxMana, (current, max) =>
                max > 0 ? current / max : 0f
            ).ToReadOnlyReactiveProperty();

            _disposables.Add(_currentMana);
            _disposables.Add(_maxMana);
            _disposables.Add(_manaPercent);

            Debug.Log($"[HUDViewModel] Reactive bindings established (weapon={(ammo != null ? "yes" : "none")}).");
        }

        /// <summary>
        /// Update the weapon source (e.g., when switching weapons).
        /// Re-establishes bindings for the new weapon.
        /// </summary>
        /// <param name="newLoadout">New loadout to display</param>
        public void SetLoadout(PlayerWeaponLoadout newLoadout)
        {
            playerLoadout = newLoadout;

            Rebind();
        }

        /// <summary>
        /// Update the stats source (e.g., when switching characters).
        /// Re-establishes bindings for the new stats.
        /// </summary>
        /// <param name="newStats">New stats to display</param>
        public void SetStats(UnitStats newStats)
        {
            playerStats = newStats;

            Rebind();
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }

        /// <summary>
        /// Get ammo text formatted for display (e.g., "12 / 12").
        /// </summary>
        /// <returns>Formatted ammo string</returns>
        public string GetAmmoText()
        {
            var ammo = AmmoSource;
            if (ammo == null) return "-- / --";

            return $"{ammo.Value} / {MagazineSize}";
        }

        /// <summary>
        /// Get health text formatted for display (e.g., "85 / 100").
        /// </summary>
        /// <returns>Formatted health string</returns>
        public string GetHealthText()
        {
            if (playerStats == null) return "-- / --";

            float current = playerStats.CurrentHp.Value;
            float max = playerStats.MaxHp.Value;

            return $"{Mathf.Round(current)} / {Mathf.Round(max)}";
        }

        /// <summary>
        /// Get mana text formatted for display (e.g., "50 / 100").
        /// </summary>
        /// <returns>Formatted mana string</returns>
        public string GetManaText()
        {
            if (playerStats == null) return "-- / --";

            float current = playerStats.Mana.Value;
            float max = playerStats.MaxMana.Value;

            return $"{Mathf.Round(current)} / {Mathf.Round(max)}";
        }
    }
}

using UnityEngine;
using R3;
using PFE.Systems.Combat;
using PFE.Entities.Units;

namespace PFE.UI.HUD
{
    /// <summary>
    /// ViewModel for the Heads-Up Display (HUD).
    /// Bridges the gap between game logic (WeaponLogic, UnitStats) and UI components.
    /// Exposes reactive properties that UI views can bind to using R3.
    ///
    /// Design Pattern: Model-View-ViewModel (MVVM)
    /// - Model: WeaponLogic, UnitStats (game data and logic)
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
        [Tooltip("Weapon logic to display ammo and reload state for")]
        internal WeaponLogic playerWeapon;

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
        /// Initialize the ViewModel with game data sources.
        /// Call this when setting up the HUD.
        /// </summary>
        /// <param name="weapon">Player's weapon logic</param>
        /// <param name="stats">Player's stats</param>
        public void Initialize(WeaponLogic weapon, UnitStats stats)
        {
            playerWeapon = weapon;
            playerStats = stats;

            SetupBindings();
        }

        private void Awake()
        {
            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            // If sources are assigned via Inspector, setup bindings on Start
            if (playerWeapon != null && playerStats != null)
            {
                SetupBindings();
            }
        }

        /// <summary>
        /// Set up reactive property bindings from game data sources.
        /// This is where we transform raw game data into UI-friendly reactive properties.
        /// </summary>
        private void SetupBindings()
        {
            if (playerWeapon == null || playerStats == null)
            {
                Debug.LogError("[HUDViewModel] Cannot setup bindings: weapon or stats is null!");
                return;
            }

            // Weapon bindings
            _currentAmmo = playerWeapon.CurrentAmmo.ToReadOnlyReactiveProperty();
            _maxAmmo = playerWeapon.CurrentAmmo.Select(_ => playerWeapon.WeaponDef.magazineSize).ToReadOnlyReactiveProperty();
            _ammoPercent = playerWeapon.CurrentAmmo.Select(ammo =>
                playerWeapon.WeaponDef.magazineSize > 0 ? (float)ammo / playerWeapon.WeaponDef.magazineSize : 0
            ).ToReadOnlyReactiveProperty();
            _reloadProgress = playerWeapon.ReloadProgress.ToReadOnlyReactiveProperty();
            _isReloading = playerWeapon.IsReloading.ToReadOnlyReactiveProperty();

            // Stats bindings
            _healthPercent = playerStats.HpPercent;
            _currentHealth = playerStats.CurrentHp.ToReadOnlyReactiveProperty();
            _maxHealth = playerStats.MaxHp.ToReadOnlyReactiveProperty();
            _isAlive = playerStats.CurrentHp.Select(hp => hp > 0).ToReadOnlyReactiveProperty();

            // Mana bindings
            _currentMana = playerStats.Mana.ToReadOnlyReactiveProperty();
            _maxMana = playerStats.MaxMana.ToReadOnlyReactiveProperty();
            _manaPercent = playerStats.Mana.CombineLatest(playerStats.MaxMana, (current, max) =>
                max > 0 ? current / max : 0
            ).ToReadOnlyReactiveProperty();

            Debug.Log("[HUDViewModel] Reactive bindings established");
        }

        /// <summary>
        /// Update the weapon source (e.g., when switching weapons).
        /// Re-establishes bindings for the new weapon.
        /// </summary>
        /// <param name="newWeapon">New weapon to display</param>
        public void SetWeapon(WeaponLogic newWeapon)
        {
            playerWeapon = newWeapon;

            // Re-setup bindings with new weapon
            SetupBindings();
        }

        /// <summary>
        /// Update the stats source (e.g., when switching characters).
        /// Re-establishes bindings for the new stats.
        /// </summary>
        /// <param name="newStats">New stats to display</param>
        public void SetStats(UnitStats newStats)
        {
            playerStats = newStats;

            // Re-setup bindings with new stats
            SetupBindings();
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
            if (playerWeapon == null) return "-- / --";

            int current = playerWeapon.CurrentAmmo.Value;
            int max = playerWeapon.WeaponDef?.magazineSize ?? 0;

            return $"{current} / {max}";
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

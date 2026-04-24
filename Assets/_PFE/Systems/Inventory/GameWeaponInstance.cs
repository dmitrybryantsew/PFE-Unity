using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Runtime instance of a weapon in the game inventory.
    /// Extends GameItemInstance with weapon-specific properties.
    ///
    /// Based on ActionScript Weapon.as from the original game.
    /// </summary>
    [System.Serializable]
    public class GameWeaponInstance
    {
        /// <summary>
        /// Reference to static item definition
        /// </summary>
        [SerializeField]
        private ItemDefinition definition;

        /// <summary>
        /// Current health/hitpoints (hp in AS3)
        /// </summary>
        [SerializeField]
        private float currentHealth;

        /// <summary>
        /// Maximum health (maxhp in AS3)
        /// Calculated from base definition and condition
        /// </summary>
        [SerializeField]
        private float maxHealth;

        /// <summary>
        /// Ammo currently in weapon (hold in AS3)
        /// </summary>
        [SerializeField]
        private int currentAmmo;

        /// <summary>
        /// Loaded ammo type ID
        /// </summary>
        [SerializeField]
        private string loadedAmmoType;

        /// <summary>
        /// Storage/availability state (respect in AS3)
        /// 0=active, 1=hidden, 2=favorited, 3=scheme only
        /// </summary>
        [SerializeField]
        private WeaponRespect respect = WeaponRespect.Active;

        /// <summary>
        /// Variant/upgrade level
        /// </summary>
        [SerializeField]
        private int variant;

        // ===== Public Properties =====

        public ItemDefinition Definition => definition;
        public float CurrentHealth
        {
            get => currentHealth;
            set => currentHealth = Mathf.Clamp(value, 0, maxHealth);
        }
        public float MaxHealth => maxHealth;
        public int CurrentAmmo
        {
            get => currentAmmo;
            set => currentAmmo = Mathf.Max(0, value);
        }
        public string LoadedAmmoType => loadedAmmoType;
        public WeaponRespect Respect
        {
            get => respect;
            set => respect = value;
        }
        public int Variant => variant;

        /// <summary>
        /// Effective health accounting for multipliers
        /// </summary>
        public float EffectiveHealth => currentHealth; // TODO: Apply multipliers if needed

        /// <summary>
        /// Health as percentage (0-100)
        /// </summary>
        public float HealthPercent => maxHealth > 0 ? (currentHealth / maxHealth) * 100f : 0f;

        /// <summary>
        /// Whether this weapon is available for use
        /// Hidden weapons are available at base
        /// </summary>
        public bool IsAvailable => respect != WeaponRespect.Hidden;

        /// <summary>
        /// Weight of this weapon
        /// Only counts if Active or Favorited
        /// </summary>
        public float Mass
        {
            get
            {
                if (respect == WeaponRespect.Active || respect == WeaponRespect.Favorited)
                {
                    return definition != null ? definition.weight : 0f;
                }
                return 0f;
            }
        }

        // ===== Constructors =====

        public GameWeaponInstance(ItemDefinition weaponDefinition, float health = float.MaxValue,
                                  int ammo = 0, WeaponRespect respectState = WeaponRespect.Active,
                                  int variantLevel = 0)
        {
            if (weaponDefinition == null)
            {
                Debug.LogError("[GameWeaponInstance] Cannot create instance with null definition");
                return;
            }

            definition = weaponDefinition;
            maxHealth = 100f; // TODO: Get from weapon definition
            currentHealth = (health == float.MaxValue || health > maxHealth) ? maxHealth : health;
            currentAmmo = ammo;
            loadedAmmoType = ""; // TODO: Get from weapon definition
            respect = respectState;
            variant = variantLevel;
        }

        // ===== Public Methods =====

        /// <summary>
        /// Repair this weapon
        /// </summary>
        /// <param name="amount">Amount to repair</param>
        /// <param name="repairMultiplier">Repair skill multiplier</param>
        public void Repair(float amount, float repairMultiplier = 1f)
        {
            float repairAmount = amount * repairMultiplier;
            currentHealth = Mathf.Min(currentHealth + repairAmount, maxHealth);
        }

        /// <summary>
        /// Set loaded ammo type
        /// </summary>
        public void SetLoadedAmmo(string ammoType)
        {
            loadedAmmoType = ammoType;
        }

        /// <summary>
        /// Reload with specified ammo amount
        /// </summary>
        public void Reload(int ammoAmount)
        {
            currentAmmo = ammoAmount;
        }

        /// <summary>
        /// Consume one ammo
        /// </summary>
        /// <returns>True if ammo was available</returns>
        public bool ConsumeAmmo()
        {
            if (currentAmmo > 0)
            {
                currentAmmo--;
                return true;
            }
            return false;
        }

        // ===== Serialization =====

        public GameWeaponSaveData GetSaveData()
        {
            return new GameWeaponSaveData
            {
                weaponId = definition != null ? definition.itemId : "",
                currentHealth = currentHealth,
                currentAmmo = currentAmmo,
                loadedAmmoType = loadedAmmoType,
                respect = (int)respect,
                variant = variant
            };
        }
    }

    /// <summary>
    /// Serializable save data for weapon instance
    /// </summary>
    [System.Serializable]
    public class GameWeaponSaveData
    {
        public string weaponId;
        public float currentHealth;
        public int currentAmmo;
        public string loadedAmmoType;
        public int respect;
        public int variant;
    }
}

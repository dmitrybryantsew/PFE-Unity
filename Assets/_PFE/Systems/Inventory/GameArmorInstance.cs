using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Runtime instance of armor in the game inventory.
    ///
    /// Based on ActionScript Armor.as from the original game.
    /// </summary>
    [System.Serializable]
    public class GameArmorInstance
    {
        /// <summary>
        /// Reference to static item definition
        /// </summary>
        [SerializeField]
        private ItemDefinition definition;

        /// <summary>
        /// Current health/hitpoints
        /// </summary>
        [SerializeField]
        private float currentHealth;

        /// <summary>
        /// Maximum health
        /// </summary>
        [SerializeField]
        private float maxHealth;

        /// <summary>
        /// Level/upgrades
        /// </summary>
        [SerializeField]
        private int level;

        // ===== Public Properties =====

        public ItemDefinition Definition => definition;
        public float CurrentHealth
        {
            get => currentHealth;
            set => currentHealth = Mathf.Clamp(value, 0, maxHealth);
        }
        public float MaxHealth => maxHealth;
        public int Level => level;

        /// <summary>
        /// Health as percentage (0-100)
        /// </summary>
        public float HealthPercent => maxHealth > 0 ? (currentHealth / maxHealth) * 100f : 0f;

        // ===== Constructors =====

        public GameArmorInstance(ItemDefinition armorDefinition, float health = float.MaxValue, int armorLevel = 0)
        {
            if (armorDefinition == null)
            {
                Debug.LogError("[GameArmorInstance] Cannot create instance with null definition");
                return;
            }

            definition = armorDefinition;
            maxHealth = 100f; // TODO: Get from armor definition
            currentHealth = (health == float.MaxValue || health > maxHealth) ? maxHealth : health;
            level = armorLevel;
        }

        // ===== Public Methods =====

        /// <summary>
        /// Repair this armor
        /// </summary>
        public void Repair(float amount, float repairMultiplier = 1f)
        {
            float repairAmount = amount * repairMultiplier;
            currentHealth = Mathf.Min(currentHealth + repairAmount, maxHealth);
        }

        // ===== Serialization =====

        public GameArmorSaveData GetSaveData()
        {
            return new GameArmorSaveData
            {
                armorId = definition != null ? definition.itemId : "",
                currentHealth = currentHealth,
                level = level
            };
        }
    }

    /// <summary>
    /// Serializable save data for armor instance
    /// </summary>
    [System.Serializable]
    public class GameArmorSaveData
    {
        public string armorId;
        public float currentHealth;
        public int level;
    }
}

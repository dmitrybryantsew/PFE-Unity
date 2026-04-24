using System;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Runtime instance of an item in the game inventory.
    /// Separates static definition data (ItemDefinition) from runtime state.
    ///
    /// Based on ActionScript Item.as from the original game.
    /// </summary>
    [System.Serializable]
    public class GameItemInstance
    {
        /// <summary>
        /// Reference to static item definition (ScriptableObject)
        /// </summary>
        [SerializeField]
        private ItemDefinition definition;

        /// <summary>
        /// Current quantity/stack size (kol in AS3)
        /// </summary>
        [SerializeField]
        private int quantity;

        /// <summary>
        /// Condition 0.0-1.0 (sost in AS3)
        /// Used for weapons/armor durability
        /// </summary>
        [SerializeField]
        private float condition = 1f;

        /// <summary>
        /// Variant/upgrade level (variant in AS3)
        /// For weapon upgrades and tiered items
        /// </summary>
        [SerializeField]
        private int variant;

        /// <summary>
        /// Health multiplier (multHP in AS3)
        /// Modifies max durability calculation
        /// </summary>
        [SerializeField]
        private float healthMultiplier = 1f;

        /// <summary>
        /// New item status for UI (nov in AS3)
        /// 0=none, 1=new, 2=recent
        /// </summary>
        [SerializeField]
        private NewItemStatus newStatus = NewItemStatus.None;

        /// <summary>
        /// Acquisition timestamp (dat in AS3)
        /// Unix timestamp in milliseconds
        /// </summary>
        [SerializeField]
        private long acquisitionTimestamp;

        /// <summary>
        /// Quantity stored at base (vault in AS3)
        /// Separate from carried quantity
        /// </summary>
        [SerializeField]
        private int vaultQuantity;

        // ===== Public Properties =====

        public ItemDefinition Definition => definition;
        public int Quantity
        {
            get => quantity;
            set => quantity = Mathf.Max(0, value);
        }
        public float Condition
        {
            get => condition;
            set => condition = Mathf.Clamp01(value);
        }
        public int Variant
        {
            get => variant;
            set => variant = Mathf.Max(0, value);
        }
        public float HealthMultiplier
        {
            get => healthMultiplier;
            set => healthMultiplier = Mathf.Max(0, value);
        }
        public NewItemStatus NewStatus
        {
            get => newStatus;
            set => newStatus = value;
        }
        public long AcquisitionTimestamp
        {
            get => acquisitionTimestamp;
            set => acquisitionTimestamp = value;
        }
        public int VaultQuantity
        {
            get => vaultQuantity;
            set => vaultQuantity = value; // Allow negative values for vault debt
        }

        /// <summary>
        /// Total weight of this stack
        /// </summary>
        public float TotalWeight => definition != null ? definition.weight * quantity : 0f;

        /// <summary>
        /// Whether this item can stack
        /// </summary>
        public bool IsStackable => definition != null && definition.stackSize > 1;

        /// <summary>
        /// Whether this can stack with another instance
        /// </summary>
        public bool CanStackWith(GameItemInstance other)
        {
            if (other == null || definition == null || other.definition == null)
                return false;

            return IsStackable &&
                   definition.itemId == other.definition.itemId &&
                   variant == other.variant &&
                   quantity < definition.stackSize;
        }

        // ===== Constructors =====

        public GameItemInstance(ItemDefinition itemDefinition, int count = 1)
        {
            if (itemDefinition == null)
            {
                Debug.LogError("[GameItemInstance] Cannot create instance with null definition");
                return;
            }

            definition = itemDefinition;
            quantity = Mathf.Clamp(count, 1, definition.stackSize);
            condition = 1f;
            variant = 0;
            healthMultiplier = 1f;
            newStatus = NewItemStatus.New;
            acquisitionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vaultQuantity = 0;
        }

        /// <summary>
        /// Constructor for loading from save data
        /// </summary>
        public GameItemInstance(ItemDefinition itemDefinition, GameItemSaveData saveData)
        {
            if (itemDefinition == null || saveData == null)
            {
                Debug.LogError("[GameItemInstance] Cannot load with null definition or save data");
                return;
            }

            definition = itemDefinition;
            quantity = saveData.quantity;
            condition = saveData.condition;
            variant = saveData.variant;
            healthMultiplier = saveData.healthMultiplier;
            newStatus = (NewItemStatus)saveData.newStatus;
            acquisitionTimestamp = saveData.acquisitionTimestamp;
            vaultQuantity = saveData.vaultQuantity;
        }

        // ===== Serialization =====

        public GameItemSaveData GetSaveData()
        {
            return new GameItemSaveData
            {
                itemId = definition != null ? definition.itemId : "",
                quantity = quantity,
                condition = condition,
                variant = variant,
                healthMultiplier = healthMultiplier,
                newStatus = (int)newStatus,
                acquisitionTimestamp = acquisitionTimestamp,
                vaultQuantity = vaultQuantity
            };
        }
    }

    /// <summary>
    /// Serializable save data for game item instance
    /// </summary>
    [System.Serializable]
    public class GameItemSaveData
    {
        public string itemId;
        public int quantity;
        public float condition;
        public int variant;
        public float healthMultiplier;
        public int newStatus;
        public long acquisitionTimestamp;
        public int vaultQuantity;
    }
}

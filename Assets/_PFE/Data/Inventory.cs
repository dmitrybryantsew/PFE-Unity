using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PFE.Data
{
    /// <summary>
    /// Runtime inventory system that holds item instances.
    /// Separates static item definitions (ScriptableObjects) from runtime state (durability, ammo, etc.).
    ///
    /// Design:
    /// - ItemDefinition: Static data from ScriptableObjects (name, base stats, etc.)
    /// - ItemInstance: Runtime data (instance ID, current durability, current ammo)
    /// - Inventory: Collection of ItemInstances with lookup methods
    ///
    /// This allows:
    /// - Multiple instances of the same item type (e.g., 10mm Pistol with 50% durability vs 100%)
    /// - Save/load of runtime state
    /// - Item modification without affecting base definition
    /// </summary>
    [System.Serializable]
    public class Inventory
    {
        [Header("Runtime Item Instances")]
        [SerializeField]
        private List<ItemInstance> items = new List<ItemInstance>();

        /// <summary>
        /// Event fired when an item is added to inventory.
        /// </summary>
        public event Action<ItemInstance> OnItemAdded;

        /// <summary>
        /// Event fired when an item is removed from inventory.
        /// </summary>
        public event Action<ItemInstance> OnItemRemoved;

        /// <summary>
        /// Event fired when an item is modified (durability changed, etc.).
        /// </summary>
        public event Action<ItemInstance> OnItemModified;

        /// <summary>
        /// Get all items in inventory.
        /// </summary>
        public IReadOnlyList<ItemInstance> Items => items;

        /// <summary>
        /// Get total number of items in inventory.
        /// </summary>
        public int Count => items.Count;

        /// <summary>
        /// Add a new item instance to inventory by definition ID.
        /// Creates a new instance with default runtime values.
        /// </summary>
        /// <param name="definitionId">ID of the item definition to instantiate</param>
        /// <returns>The created item instance, or null if definition not found</returns>
        public ItemInstance AddItem(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId))
            {
                Debug.LogWarning("[Inventory] Cannot add item with null or empty definition ID");
                return null;
            }

            // Create new instance
            var instance = new ItemInstance(definitionId);
            items.Add(instance);

            Debug.Log($"[Inventory] Added item: {definitionId} (Instance: {instance.InstanceId})");
            OnItemAdded?.Invoke(instance);

            return instance;
        }

        /// <summary>
        /// Add an existing item instance to inventory.
        /// Useful when loading from save data.
        /// </summary>
        /// < <param name="instance">Item instance to add</param>
        public void AddItem(ItemInstance instance)
        {
            if (instance == null)
            {
                Debug.LogWarning("[Inventory] Cannot add null item instance");
                return;
            }

            items.Add(instance);
            Debug.Log($"[Inventory] Added existing item instance: {instance.DefinitionId} (Instance: {instance.InstanceId})");
            OnItemAdded?.Invoke(instance);
        }

        /// <summary>
        /// Remove an item from inventory by instance ID.
        /// </summary>
        /// <param name="instanceId">Instance ID of item to remove</param>
        /// <returns>True if item was found and removed</returns>
        public bool RemoveItem(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return false;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].InstanceId == instanceId)
                {
                    var removedItem = items[i];
                    items.RemoveAt(i);

                    Debug.Log($"[Inventory] Removed item: {removedItem.DefinitionId} (Instance: {instanceId})");
                    OnItemRemoved?.Invoke(removedItem);

                    return true;
                }
            }

            Debug.LogWarning($"[Inventory] Item not found for removal: {instanceId}");
            return false;
        }

        /// <summary>
        /// Get an item instance by instance ID.
        /// </summary>
        /// <param name="instanceId">Instance ID to search for</param>
        /// <returns>Item instance, or null if not found</returns>
        public ItemInstance GetItem(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return null;

            return items.FirstOrDefault(item => item.InstanceId == instanceId);
        }

        /// <summary>
        /// Check if inventory contains any instance of a specific definition.
        /// </summary>
        /// <param name="definitionId">Definition ID to search for</param>
        /// <returns>True if any item with this definition ID exists in inventory</returns>
        public bool HasItem(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId))
                return false;

            return items.Any(item => item.DefinitionId == definitionId);
        }

        /// <summary>
        /// Get all item instances matching a specific definition ID.
        /// </summary>
        /// <param name="definitionId">Definition ID to search for</param>
        /// <returns>List of all matching item instances</returns>
        public List<ItemInstance> GetItemsByDefinition(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId))
                return new List<ItemInstance>();

            return items.Where(item => item.DefinitionId == definitionId).ToList();
        }

        /// <summary>
        /// Get count of items matching a specific definition ID.
        /// </summary>
        /// <param name="definitionId">Definition ID to count</param>
        /// <returns>Number of items with this definition</returns>
        public int GetItemCount(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId))
                return 0;

            return items.Count(item => item.DefinitionId == definitionId);
        }

        /// <summary>
        /// Notify that an item has been modified.
        /// Call this after changing item properties (durability, ammo, etc.)
        /// </summary>
        /// <param name="instance">Item instance that was modified</param>
        public void NotifyItemModified(ItemInstance instance)
        {
            if (instance == null)
                return;

            OnItemModified?.Invoke(instance);
        }

        /// <summary>
        /// Clear all items from inventory.
        /// Useful for testing or new game setup.
        /// </summary>
        public void Clear()
        {
            items.Clear();
            Debug.Log("[Inventory] Cleared all items");
        }

        /// <summary>
        /// Get inventory data for serialization.
        /// </summary>
        /// <returns>Serializable inventory data</returns>
        public InventorySaveData GetSaveData()
        {
            return new InventorySaveData
            {
                itemInstances = items.Select(item => item.GetSaveData()).ToList()
            };
        }

        /// <summary>
        /// Load inventory from save data.
        /// </summary>
        /// <param name="saveData">Save data to load</param>
        public void LoadSaveData(InventorySaveData saveData)
        {
            if (saveData == null)
                return;

            items.Clear();

            foreach (var itemData in saveData.itemInstances)
            {
                var instance = new ItemInstance(itemData);
                items.Add(instance);
            }

            Debug.Log($"[Inventory] Loaded {items.Count} items from save data");
        }
    }

    /// <summary>
    /// Runtime instance of an item.
    /// Separates instance-specific state (durability, ammo) from static definition data.
    ///
    /// Example:
    /// - Definition: "10mm Pistol" (maxDurability: 100, magazineSize: 12)
    /// - Instance 1: currentDurability: 50, currentAmmo: 6
    /// - Instance 2: currentDurability: 100, currentAmmo: 12
    /// </summary>
    [System.Serializable]
    public class ItemInstance
    {
        [Header("Identification")]
        [SerializeField]
        private string instanceId;

        [SerializeField]
        private string definitionId;

        [Header("Runtime State")]
        [SerializeField]
        private float currentDurability;

        [SerializeField]
        private int currentAmmo;

        [SerializeField]
        private bool isEquipped;

        /// <summary>
        /// Unique instance ID (GUID).
        /// Used to distinguish multiple instances of the same item type.
        /// </summary>
        public string InstanceId => instanceId;

        /// <summary>
        /// Definition ID (references ScriptableObject).
        /// Used to look up static item data from GameDatabase.
        /// </summary>
        public string DefinitionId => definitionId;

        /// <summary>
        /// Current durability of this item instance.
        /// Ranges from 0 to MaxDurability (from definition).
        /// </summary>
        public float CurrentDurability
        {
            get => currentDurability;
            set => currentDurability = Mathf.Clamp(value, 0, MaxDurability);
        }

        /// <summary>
        /// Current ammo in this item instance (if it's a weapon).
        /// Ranges from 0 to MagazineSize (from definition).
        /// </summary>
        public int CurrentAmmo
        {
            get => currentAmmo;
            set => currentAmmo = Mathf.Clamp(value, 0, MagazineSize);
        }

        /// <summary>
        /// Whether this item is currently equipped.
        /// </summary>
        public bool IsEquipped
        {
            get => isEquipped;
            set => isEquipped = value;
        }

        /// <summary>
        /// Max durability (from definition).
        /// TODO: Look up from GameDatabase when needed.
        /// </summary>
        public float MaxDurability { get; set; } = 100f;

        /// <summary>
        /// Magazine size (from definition).
        /// TODO: Look up from GameDatabase when needed.
        /// </summary>
        public int MagazineSize { get; set; } = 12;

        /// <summary>
        /// Durability percentage (0-1).
        /// </summary>
        public float DurabilityPercent => MaxDurability > 0 ? currentDurability / MaxDurability : 0;

        /// <summary>
        /// Ammo percentage (0-1).
        /// </summary>
        public float AmmoPercent => MagazineSize > 0 ? (float)currentAmmo / MagazineSize : 0;

        /// <summary>
        /// Constructor for new item instance.
        /// Generates unique instance ID and initializes with default values.
        /// </summary>
        /// <param name="definitionId">Definition ID to instantiate</param>
        public ItemInstance(string definitionId)
        {
            this.definitionId = definitionId;
            this.instanceId = System.Guid.NewGuid().ToString();
            this.currentDurability = MaxDurability;
            this.currentAmmo = MagazineSize;
            this.isEquipped = false;
        }

        /// <summary>
        /// Constructor for loading from save data.
        /// </summary>
        /// <param name="saveData">Save data to load</param>
        public ItemInstance(ItemInstanceSaveData saveData)
        {
            instanceId = saveData.instanceId;
            definitionId = saveData.definitionId;
            currentDurability = saveData.currentDurability;
            currentAmmo = saveData.currentAmmo;
            isEquipped = saveData.isEquipped;
            MaxDurability = saveData.maxDurability;
            MagazineSize = saveData.magazineSize;
        }

        /// <summary>
        /// Get save data for serialization.
        /// </summary>
        /// <returns>Serializable instance data</returns>
        public ItemInstanceSaveData GetSaveData()
        {
            return new ItemInstanceSaveData
            {
                instanceId = instanceId,
                definitionId = definitionId,
                currentDurability = currentDurability,
                currentAmmo = currentAmmo,
                isEquipped = isEquipped,
                maxDurability = MaxDurability,
                magazineSize = MagazineSize
            };
        }
    }

    /// <summary>
    /// Serializable save data for inventory.
    /// </summary>
    [System.Serializable]
    public class InventorySaveData
    {
        public List<ItemInstanceSaveData> itemInstances = new List<ItemInstanceSaveData>();
    }

    /// <summary>
    /// Serializable save data for a single item instance.
    /// </summary>
    [System.Serializable]
    public class ItemInstanceSaveData
    {
        public string instanceId;
        public string definitionId;
        public float currentDurability;
        public int currentAmmo;
        public bool isEquipped;
        public float maxDurability;
        public int magazineSize;
    }
}

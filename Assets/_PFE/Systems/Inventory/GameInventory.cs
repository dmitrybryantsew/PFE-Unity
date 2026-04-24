using System;
using System.Collections.Generic;
using System.Linq;
using R3;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Full game inventory system matching ActionScript Invent.as behavior.
    ///
    /// Features:
    /// - Dictionary-based storage by item ID (O(1) lookup)
    /// - Separate storage for weapons, armors, and items
    /// - Category-based weight system (4 categories + weapons/magic)
    /// - Favorite slots system (31 slots: 0-30)
    /// - Weapon "respect" states (Active, Hidden, Favorited, SchemeOnly)
    /// - New item tracking
    /// - Vault system for base storage
    /// - Reactive properties for UI binding
    ///
    /// Based on the legacy ActionScript inventory implementation in the original PFE sources
    /// (not tied to a specific local filesystem path).
    /// </summary>
    [System.Serializable]
    public class GameInventory : IAmmoSource
    {
        // ===== Storage Dictionaries =====
        // Matching AS3's associative arrays (items[id], weapons[id], armors[id])

        /// <summary>
        /// Weapon storage by weapon ID
        /// </summary>
        [SerializeField]
        private Dictionary<string, GameWeaponInstance> weapons = new Dictionary<string, GameWeaponInstance>();

        /// <summary>
        /// Armor storage by armor ID
        /// </summary>
        [SerializeField]
        private Dictionary<string, GameArmorInstance> armors = new Dictionary<string, GameArmorInstance>();

        /// <summary>
        /// General items by item ID
        /// </summary>
        [SerializeField]
        private Dictionary<string, GameItemInstance> items = new Dictionary<string, GameItemInstance>();

        /// <summary>
        /// Favorite slots: item/weapon ID -> slot number (0-30)
        /// </summary>
        [SerializeField]
        private Dictionary<string, int> favoriteSlots = new Dictionary<string, int>();

        /// <summary>
        /// Favorite items array (31 slots)
        /// /// Slot mapping:
        /// /// 0-23: Weapons by skill type
        /// /// 24-28: Secondary weapons
        /// /// 29: Thrown weapon/grenade
        /// /// 30: Magic weapon
        /// </summary>
        [SerializeField]
        private GameItemInstance[] favoriteItems = new GameItemInstance[31];

        // ===== Weight Tracking =====

        /// <summary>
        /// Category mass: [cat0, cat1, cat2, cat3]
        /// cat0: Not tracked (money, special)
        /// cat1: Usable items (potions, food, med) -> maxm1 limit
        /// cat2: Ammo and explosives -> maxm2 limit
        /// cat3: General stuff -> maxm3 limit
        /// </summary>
        [SerializeField]
        private float[] categoryMass = new float[4];

        /// <summary>
        /// Physical weapon mass (tip 1-3: Small guns, Big guns, Energy)
        /// </summary>
        public float WeaponMass { get; private set; }

        /// <summary>
        /// Magic weapon mass (tip 5: Spells)
        /// </summary>
        public float MagicMass { get; private set; }

        // ===== Current Selection =====

        /// <summary>
        /// Current selected item index
        /// </summary>
        public int CurrentItemIndex { get; set; } = -1;

        /// <summary>
        /// Current equipped weapon ID
        /// </summary>
        public string CurrentWeaponId { get; set; } = "";

        /// <summary>
        /// Current equipped armor ID
        /// </summary>
        public string CurrentArmorId { get; set; } = "";

        // ===== Reactive Properties =====

        public ReactiveProperty<int> TotalItemCount { get; private set; }
        public ReactiveProperty<float> TotalWeight { get; private set; }
        public ReactiveProperty<int> WeaponCount { get; private set; }
        public ReactiveProperty<int> ArmorCount { get; private set; }

        // ===== Events =====

        public event Action<GameItemInstance> OnItemAdded;
        public event Action<GameItemInstance> OnItemRemoved;
        public event Action<GameWeaponInstance> OnWeaponAdded;
        public event Action<GameWeaponInstance> OnWeaponRemoved;
        public event Action<GameArmorInstance> OnArmorAdded;
        public event Action<GameArmorInstance> OnArmorRemoved;
        public event Action<float> OnWeightChanged;

        // ===== Configuration =====

        /// <summary>
        /// Weight limits for each category [maxm0, maxm1, maxm2, maxm3]
        /// maxm0: Unlimited (not used)
        /// maxm1: Usable items limit
        /// maxm2: Ammo limit
        /// maxm3: Stuff limit
        /// </summary>
        private float[] maxMass = new float[4] { float.MaxValue, 100f, 50f, 200f };

        /// <summary>
        /// Physical weapon weight limit
        /// </summary>
        public float MaxWeaponMass { get; set; } = 100f;

        /// <summary>
        /// Magic weapon weight limit
        /// </summary>
        public float MaxMagicMass { get; set; } = 50f;

        /// <summary>
        /// Whether inventory limits are enforced
        /// </summary>
        public bool HardInventory { get; set; } = false;

        // ===== Constructor =====

        public GameInventory()
        {
            TotalItemCount = new ReactiveProperty<int>(0);
            TotalWeight = new ReactiveProperty<float>(0);
            WeaponCount = new ReactiveProperty<int>(0);
            ArmorCount = new ReactiveProperty<int>(0);

            // Initialize mass array
            for (int i = 0; i < 4; i++)
            {
                categoryMass[i] = 0f;
            }
        }

        // ===== Public API - Item Operations =====

        /// <summary>
        /// Add an item to inventory
        /// Based on AS3 Invent.as take() method
        /// </summary>
        /// <param name="itemDefinition">Item definition to add</param>
        /// <param name="quantity">Quantity to add</param>
        /// <param name="pickupType">Type of pickup (Loot, Trade, Reward)</param>
        /// <returns>True if item was added successfully</returns>
        public bool AddItem(ItemDefinition itemDefinition, int quantity = 1, PickupType pickupType = PickupType.Loot)
        {
            if (itemDefinition == null)
            {
                Debug.LogWarning("[GameInventory] Cannot add null item");
                return false;
            }

            if (quantity <= 0)
            {
                Debug.LogWarning($"[GameInventory] Invalid quantity: {quantity}");
                return false;
            }

            // Get or create item instance
            if (items.TryGetValue(itemDefinition.itemId, out GameItemInstance existingItem))
            {
                // Stack onto existing
                int spaceInStack = itemDefinition.stackSize - existingItem.Quantity;
                int actualQuantity = Mathf.Min(quantity, spaceInStack);

                if (actualQuantity > 0)
                {
                    existingItem.Quantity += actualQuantity;
                    UpdateItemNewStatus(existingItem);
                }
                else
                {
                    Debug.LogWarning($"[GameInventory] Stack full for {itemDefinition.itemId}");
                    return false;
                }
            }
            else
            {
                // Create new item instance
                var newItem = new GameItemInstance(itemDefinition, quantity);
                items[itemDefinition.itemId] = newItem;
                OnItemAdded?.Invoke(newItem);
            }

            // Always calculate mass from current items
            CalculateMass();
            return true;
        }

        /// <summary>
        /// Remove an item from inventory
        /// Based on AS3 Invent.as minusItem() method
        /// </summary>
        /// <param name="itemId">Item ID to remove</param>
        /// <param name="quantity">Quantity to remove</param>
        /// <param name="allowVault">If true and at base, allow going negative into vault</param>
        /// <returns>True if item was removed</returns>
        public bool RemoveItem(string itemId, int quantity = 1, bool allowVault = false)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                Debug.LogWarning("[GameInventory] Invalid item ID");
                return false;
            }

            if (!items.TryGetValue(itemId, out GameItemInstance item))
            {
                Debug.LogWarning($"[GameInventory] Item not found: {itemId}");
                return false;
            }

            if (quantity <= 0)
            {
                Debug.LogWarning($"[GameInventory] Invalid quantity: {quantity}");
                return false;
            }

            // Check if we have enough
            if (item.Quantity < quantity)
            {
                if (allowVault && item.Definition != null)
                {
                    // Allow going negative into vault
                    int shortfall = quantity - item.Quantity;
                    item.VaultQuantity -= shortfall;
                    item.Quantity = 0;
                }
                else
                {
                    Debug.LogWarning($"[GameInventory] Not enough {itemId}: have {item.Quantity}, need {quantity}");
                    return false;
                }
            }
            else
            {
                item.Quantity -= quantity;
            }

            // Remove if empty AND no vault debt
            if (item.Quantity <= 0 && item.VaultQuantity >= 0)
            {
                items.Remove(itemId);
                OnItemRemoved?.Invoke(item);
                SelectNextItem();
            }

            CalculateMass();
            return true;
        }

        // ===== Public API - Weapon Operations =====

        /// <summary>
        /// Add a weapon to inventory
        /// Based on AS3 Invent.as addWeapon() method
        /// </summary>
        /// <param name="weaponId">Weapon ID</param>
        /// <param name="health">Starting health (maxHealth if maxValue)</param>
        /// <param name="ammo">Starting ammo in weapon</param>
        /// <param name="respect">Storage state</param>
        /// <param name="variant">Variant level</param>
        /// <returns>Weapon instance, or null if failed</returns>
        public GameWeaponInstance AddWeapon(string weaponId, float health = float.MaxValue,
                                            int ammo = 0, WeaponRespect respect = WeaponRespect.Active,
                                            int variant = 0)
        {
            if (string.IsNullOrEmpty(weaponId))
            {
                Debug.LogWarning("[GameInventory] Invalid weapon ID");
                return null;
            }

            // Check if already have this weapon
            if (weapons.TryGetValue(weaponId, out GameWeaponInstance existingWeapon))
            {
                // Repair existing weapon
                float repairAmount = (health == float.MaxValue) ? existingWeapon.MaxHealth : health;
                existingWeapon.Repair(repairAmount, 1.0f);
                return existingWeapon;
            }

            // TODO: Get weapon definition from database
            // For now, create placeholder
            ItemDefinition weaponDef = null; // TODO: Lookup from GameDatabase
            if (weaponDef == null)
            {
                Debug.LogError($"[GameInventory] Weapon definition not found: {weaponId}");
                return null;
            }

            // Create new weapon
            var weapon = new GameWeaponInstance(weaponDef, health, ammo, respect, variant);
            weapons[weaponId] = weapon;
            OnWeaponAdded?.Invoke(weapon);

            CalculateWeaponMass();
            return weapon;
        }

        /// <summary>
        /// Remove a weapon from inventory
        /// </summary>
        /// <param name="weaponId">Weapon ID to remove</param>
        /// <returns>True if weapon was removed</returns>
        public bool RemoveWeapon(string weaponId)
        {
            if (!weapons.TryGetValue(weaponId, out GameWeaponInstance weapon))
            {
                return false;
            }

            // Check if learned from scheme
            string schemeId = "s_" + weaponId;
            if (items.ContainsKey(schemeId) && items[schemeId].Quantity > 0)
            {
                // Mark as scheme-only instead of removing
                weapon.Respect = WeaponRespect.SchemeOnly;
            }
            else
            {
                weapons.Remove(weaponId);
                OnWeaponRemoved?.Invoke(weapon);
            }

            CalculateWeaponMass();
            return true;
        }

        /// <summary>
        /// Toggle weapon respect state (Active/Hidden/Favorited)
        /// Based on AS3 Invent.as respectWeapon() method
        /// </summary>
        /// <param name="weaponId">Weapon ID</param>
        /// <returns>New respect state</returns>
        public WeaponRespect ToggleWeaponRespect(string weaponId)
        {
            if (!weapons.TryGetValue(weaponId, out GameWeaponInstance weapon))
            {
                return WeaponRespect.Hidden;
            }

            if (weapon.Respect == WeaponRespect.Active || weapon.Respect == WeaponRespect.Favorited)
            {
                weapon.Respect = WeaponRespect.Hidden;
            }
            else
            {
                weapon.Respect = WeaponRespect.Active;
            }

            // Update current weapon if needed
            if (CurrentWeaponId == weaponId && weapon.Respect == WeaponRespect.Hidden)
            {
                CurrentWeaponId = ""; // Unequip
            }

            CalculateWeaponMass();
            return weapon.Respect;
        }

        // ===== Public API - Armor Operations =====

        /// <summary>
        /// Add armor to inventory
        /// Based on AS3 Invent.as addArmor() method
        /// </summary>
        /// <param name="armorId">Armor ID</param>
        /// <param name="health">Starting health</param>
        /// <param name="level">Armor level</param>
        /// <returns>Armor instance, or null if already exists</returns>
        public GameArmorInstance AddArmor(string armorId, float health = float.MaxValue, int level = 0)
        {
            if (string.IsNullOrEmpty(armorId))
            {
                Debug.LogWarning("[GameInventory] Invalid armor ID");
                return null;
            }

            // Check if already have this armor
            if (armors.ContainsKey(armorId))
            {
                Debug.LogWarning($"[GameInventory] Already have armor: {armorId}");
                return null;
            }

            // TODO: Get armor definition from database
            ItemDefinition armorDef = null; // TODO: Lookup from GameDatabase
            if (armorDef == null)
            {
                Debug.LogError($"[GameInventory] Armor definition not found: {armorId}");
                return null;
            }

            // Create new armor
            var armor = new GameArmorInstance(armorDef, health, level);
            armors[armorId] = armor;
            OnArmorAdded?.Invoke(armor);

            return armor;
        }

        // ===== Public API - Favorites =====

        /// <summary>
        /// Set favorite slot for an item
        /// Based on AS3 Invent.as favItem() method
        /// </summary>
        /// <param name="itemId">Item or weapon ID</param>
        /// <param name="slot">Slot number (0-30)</param>
        /// <returns>True if successful</returns>
        public bool SetFavorite(string itemId, int slot)
        {
            if (slot < 0 || slot >= favoriteItems.Length)
            {
                Debug.LogWarning($"[GameInventory] Invalid favorite slot: {slot}");
                return false;
            }

            // Clear previous assignment
            if (favoriteSlots.TryGetValue(itemId, out int oldSlot))
            {
                favoriteItems[oldSlot] = null;
            }

            // Clear item in target slot
            if (favoriteItems[slot] != null)
            {
                favoriteSlots.Remove(favoriteItems[slot].Definition.itemId);
            }

            // Assign to new slot
            GameItemInstance item = items.GetValueOrDefault(itemId);
            if (item == null)
            {
                // Try weapons
                // TODO: Convert weapon to item reference
                return false;
            }

            favoriteItems[slot] = item;
            favoriteSlots[itemId] = slot;
            return true;
        }

        // ===== Public API - Weight Calculation =====

        /// <summary>
        /// Calculate item mass by category
        /// Based on AS3 Invent.as calcMass() method
        /// </summary>
        public void CalculateMass()
        {
            categoryMass = new float[4];

            foreach (var kvp in items)
            {
                GameItemInstance item = kvp.Value;
                if (item != null && item.Definition != null)
                {
                    // Map actual InventoryCategory to our 4-category system
                    // Original: General=0, Weapons=1, Apparel=2, Aid=3, Misc=4, Ammo=5, Books=6, Keys=7
                    // Our system: NotTracked=0, Usable=1, Ammo=2, Stuff=3
                    int category = MapToWeightCategory(item.Definition.inventoryCategory);
                    if (category >= 0 && category < 4)
                    {
                        categoryMass[category] += item.TotalWeight;
                    }
                }
            }

            // Update reactive property
            TotalWeight.Value = GetTotalMass();
            OnWeightChanged?.Invoke(TotalWeight.Value);
        }

        private int MapToWeightCategory(Data.Definitions.InventoryCategory category)
        {
            switch (category)
            {
                case Data.Definitions.InventoryCategory.General:
                case Data.Definitions.InventoryCategory.Misc:
                case Data.Definitions.InventoryCategory.Books:
                case Data.Definitions.InventoryCategory.Keys:
                    return 0; // NotTracked

                case Data.Definitions.InventoryCategory.Aid:
                    return 1; // Usable

                case Data.Definitions.InventoryCategory.Ammo:
                    return 2; // Ammo

                case Data.Definitions.InventoryCategory.Weapons:
                case Data.Definitions.InventoryCategory.Apparel:
                    return 3; // Stuff

                default:
                    return 3; // Default to Stuff
            }
        }

        /// <summary>
        /// Calculate weapon and magic mass
        /// Based on AS3 Invent.as calcWeaponMass() method
        /// </summary>
        public void CalculateWeaponMass()
        {
            WeaponMass = 0f;
            MagicMass = 0f;

            foreach (var kvp in weapons)
            {
                GameWeaponInstance weapon = kvp.Value;
                if (weapon == null) continue;

                // Physical weapons (tip 1-3)
                if (weapon.Definition != null && weapon.Respect == WeaponRespect.Active || weapon.Respect == WeaponRespect.Favorited)
                {
                    // TODO: Check weapon type from definition
                    // For now, add to weapon mass
                    WeaponMass += weapon.Mass;
                }
            }

            // Update reactive property
            TotalWeight.Value = GetTotalMass();
            OnWeightChanged?.Invoke(TotalWeight.Value);
        }

        /// <summary>
        /// Get mass for a specific category
        /// </summary>
        /// <param name="category">Category (0-3)</param>
        /// <returns>Current mass</returns>
        public float GetCategoryMass(int category)
        {
            if (category >= 0 && category < 4)
            {
                return categoryMass[category];
            }
            return 0f;
        }

        /// <summary>
        /// Get total mass including weapons
        /// </summary>
        /// <returns>Total weight</returns>
        public float GetTotalMass()
        {
            float total = 0f;
            for (int i = 1; i < 4; i++)
            {
                total += categoryMass[i];
            }
            total += WeaponMass + MagicMass;
            return total;
        }

        // ===== Public API - Queries =====

        /// <summary>
        /// Check if inventory contains an item
        /// </summary>
        /// <param name="itemId">Item ID</param>
        /// <param name="quantity">Required quantity</param>
        /// <param name="checkVault">Also check vault if at base</param>
        /// <returns>True if has enough quantity</returns>
        public bool HasItem(string itemId, int quantity = 1, bool checkVault = false)
        {
            if (!items.TryGetValue(itemId, out GameItemInstance item))
            {
                return false;
            }

            if (item.Quantity >= quantity)
            {
                return true;
            }

            if (checkVault)
            {
                return item.Quantity + item.VaultQuantity >= quantity;
            }

            return false;
        }

        /// <summary>
        /// Get item by ID
        /// </summary>
        public GameItemInstance GetItem(string itemId)
        {
            items.TryGetValue(itemId, out GameItemInstance item);
            return item;
        }

        // ── IAmmoSource ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public int GetAmmoCount(string ammoType)
        {
            if (string.IsNullOrEmpty(ammoType)) return 0;
            return items.TryGetValue(ammoType, out var item) ? item.Quantity : 0;
        }

        /// <inheritdoc/>
        public int ConsumeAmmo(string ammoType, int amount)
        {
            if (string.IsNullOrEmpty(ammoType) || amount <= 0) return 0;
            if (!items.TryGetValue(ammoType, out var item)) return 0;
            int actual = Mathf.Min(amount, item.Quantity);
            if (actual > 0)
                RemoveItem(ammoType, actual);
            return actual;
        }

        /// <summary>
        /// Get weapon by ID
        /// </summary>
        public GameWeaponInstance GetWeapon(string weaponId)
        {
            weapons.TryGetValue(weaponId, out GameWeaponInstance weapon);
            return weapon;
        }

        /// <summary>
        /// Get armor by ID
        /// </summary>
        public GameArmorInstance GetArmor(string armorId)
        {
            armors.TryGetValue(armorId, out GameArmorInstance armor);
            return armor;
        }

        // ===== Private Methods =====

        private void UpdateItemNewStatus(GameItemInstance item)
        {
            if (item.Definition != null && item.Definition.itemId != "money")
            {
                if (item.Quantity == 1)
                {
                    item.NewStatus = NewItemStatus.New;
                }
                else if (item.NewStatus == NewItemStatus.None)
                {
                    item.NewStatus = NewItemStatus.Recent;
                }
                item.AcquisitionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        private void SelectNextItem()
        {
            // TODO: Find next usable item
            CurrentItemIndex = -1;
        }
    }
}

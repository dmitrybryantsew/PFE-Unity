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

        // ===== Caps =====

        /// <summary>
        /// The player's caps — AS3 <c>Invent.money.kol</c> (<c>Invent.as:43, 96</c>), granted at
        /// <b>50</b> by <c>addMin</c> (<c>Invent.as:1610</c>).
        ///
        /// <para><b>This is a deliberate divergence, and it is the one place the oracle is not
        /// followed.</b> The oracle holds caps as an ordinary item — <c>Invent.money</c> <i>is</i>
        /// <c>this.items["money"]</c> — so every <c>Item</c>-shaped operation applies to them. The port
        /// keeps them as a plain count here instead, for one reason: <c>GameInventory.AddItem</c> needs an
        /// <see cref="ItemDefinition"/> for every mutation, and there is no item resolver on this class
        /// (only <see cref="ArmorDefinitionResolver"/>), so an item-shaped money would have to be passed
        /// in at every call site. Money is also <i>not</i> item-shaped anywhere the port actually uses it:
        /// category 0 is deliberately untracked for mass (see <c>categoryMass</c>), and nothing else in
        /// the port reads an item's <c>kol</c> for money.</para>
        ///
        /// <para><b>What this costs.</b> The oracle's <c>money</c> row appears in the bag and is subject
        /// to weight/limit rules that a count is not. Nothing in the port asks for either. The research's
        /// T0 named this option "(b) pragmatic" and required it to be written down; this is that
        /// record.</para>
        ///
        /// <para><b>What reads it.</b> The trade page's affordability test and its debit/credit, and the
        /// status monitor's caps row through <c>PlayerPipStatusSource.CapsProvider</c>. Both go through
        /// this property, so there is exactly one place caps can change.</para>
        /// </summary>
        public int Money
        {
            get => money;
            set
            {
                if (value == money) return;
                money = value;
                MoneyChanged?.Invoke(money);
            }
        }

        [SerializeField]
        private int money;

        /// <summary>Raised after <see cref="Money"/> changes, with the new value. The monitor and the
        /// trade page both redraw from this rather than polling.</summary>
        public event Action<int> MoneyChanged;

        /// <summary>Whether the player can pay <paramref name="amount"/> caps.</summary>
        public bool CanAfford(int amount) => amount <= money;

        /// <summary>
        /// Credit caps. The caller is the sale path (AS3 <c>inv.money.kol += floor(kolSell)</c>,
        /// <c>PipPageVend.as:990</c>) and the loot path.
        /// </summary>
        public void AddMoney(int amount)
        {
            if (amount <= 0) return;
            Money = money + amount;
        }

        /// <summary>
        /// Debit caps if the player can afford it. Returns false and changes nothing otherwise — the
        /// caller reports the shortfall. The caller is the purchase path (AS3
        /// <c>inv.money.kol -= ceil(kolBou)</c>, <c>PipPageVend.as:948</c>).
        /// </summary>
        public bool TrySpendMoney(int amount)
        {
            if (amount <= 0) return true;
            if (amount > money) return false;

            Money = money - amount;
            return true;
        }

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
        /// Resolves an armour definition by id. The analogue of AS3's <c>AllData.d.armor</c> table,
        /// which <c>Invent.addArmor()</c> indexes — an <b>armour-only</b> lookup, not a general item one.
        ///
        /// <para><b>Null by default, so behaviour is unchanged until someone sets it.</b> The owner
        /// sets it at boot; <c>GameDatabase.GetItem</c> would work but is deliberately <i>not</i> the
        /// default, because it searches every <see cref="ItemDefinition"/> and would happily build an
        /// "armour" out of a potion. A narrower resolver keeps the caller honest about the table it
        /// means — and lets tests supply a two-line fake.</para>
        ///
        /// <para>Nothing in production constructs a <c>GameInventory</c> yet, so this seam is what
        /// makes <see cref="AddArmor"/> testable now and correct the moment the inventory is stood up.</para>
        /// </summary>
        public Func<string, ItemDefinition> ArmorDefinitionResolver { get; set; }

        /// <summary>
        /// Resolves a weapon definition by id. The analogue of AS3's <c>AllData.d.weapon</c> table, which
        /// <c>Invent.addWeapon()</c> indexes — a <b>weapon-only</b> lookup, not the item one.
        ///
        /// <para><b>This is the field that closes the weapon gap.</b> <see cref="AddWeapon"/> used to
        /// hard-code <c>ItemDefinition weaponDef = null; // TODO: Lookup from GameDatabase</c> and return
        /// <c>null</c> with an error for every id, because the port's weapons are
        /// <see cref="WeaponDefinition"/> assets registered under <c>ContentType.Weapon</c> while the item
        /// registry holds no weapon id at all — so no lookup that existed could ever succeed. The two
        /// tables are disjoint in the Oracle too (<c>d.weapon</c> vs <c>d.item</c>), which is why this is
        /// a second resolver rather than a wider item one: an item resolver handed a weapon id answers
        /// <c>null</c>, correctly and permanently.</para>
        ///
        /// <para><b>Null by default, so behaviour is unchanged until someone sets it.</b> That is also
        /// what keeps the two <c>GameInventoryTests</c> weapon fixtures honest: they build a bare
        /// <c>new GameInventory()</c>, set no resolver, and assert the same
        /// <c>"Weapon definition not found"</c> error and <c>null</c> return as before. An unwired
        /// inventory still refuses; a wired one works. <c>PlayerInventory.Construct</c> is the wiring.</para>
        /// </summary>
        public Func<string, WeaponDefinition> WeaponDefinitionResolver { get; set; }

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
                // Merge onto the existing stack. **There is no ceiling.**
                //
                // AS3 `Invent.plus()` adds unconditionally — `this.items[param1.id].kol += param1.kol`
                // (Invent.as:1343) — and `plusItem()` the same (:1370). Nothing anywhere in the oracle
                // caps an item count: `maxkol` exists only on particle emitters/parts (Part.as:59,
                // Emitter.as:99), never on an item, and the debug grants set `kol = 1000`/`10000`
                // freely (:1646-1717).
                //
                // The port used to cap here at `ItemDefinition.stackSize`, which every one of the 500
                // shipped item assets left at its default of 1 (SimpleDataImporter never read `@kol`).
                // So `Give 300` banked exactly one round and a *second* Give was refused with "Stack
                // full" — the reported "can't get 3000 ammo", and equally "100 eggs → 1".
                existingItem.Quantity += quantity;
                UpdateItemNewStatus(existingItem);
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

            // Resolve through the weapon table. AS3: Invent.addWeapon() indexes AllData.d.weapon — a
            // DIFFERENT table from the one take() reads, which is the whole reason a weapon id never
            // resolved here. A null resolver (a bare `new GameInventory()`, as the fixtures build) fails
            // the same way it always did, with the same message.
            WeaponDefinition weaponDef = WeaponDefinitionResolver?.Invoke(weaponId);
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

            // Resolve through the armour table. AS3: Invent.addArmor() indexes AllData.d.armor.
            ItemDefinition armorDef = ArmorDefinitionResolver?.Invoke(armorId);
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
                    // AS3 Invent.as calcMass() charges each item to mass[item.invCat]. The category is
                    // DERIVED from the item's tip/uses, not read from a UI grouping — see
                    // InventoryCategoryRules, which replaced the fabricated
                    // InventoryCategory -> 0..3 translation that used to live here. That translation
                    // sent every item whose InventoryCategory was still the General default to slot 0
                    // ("not tracked"), and 451 of the 500 imported items are exactly that — so 90% of
                    // the catalogue contributed no mass at all.
                    int category = InventoryCategoryRules.ForItem(item.Definition);
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

                // AS3 Invent.as:1450-1456 charges the two sums by type:
                //   if(tip > 0 && tip < 4 && (respect == 0 || respect == 2)) massW += mass;
                //   if(tip == 5         && (respect == 0 || respect == 2) && …) massM += mass;
                // The respect half of both conditions is already applied by GameWeaponInstance.Mass
                // (which returns 0 unless the weapon is Active or Favorited), so this only has to split
                // the type. tip 0 — the base `Weapon` class, i.e. every turret and drone laser — and
                // tip 4 (thrown) charge NEITHER sum, which is what `tip > 0 && tip < 4` says.
                switch (weapon.WeaponDefinition != null ? weapon.WeaponDefinition.weaponType : WeaponType.Guns)
                {
                    case WeaponType.Melee:      // AS3 tip 1
                    case WeaponType.Guns:       // tip 2
                    case WeaponType.BigGun:     // tip 3
                        WeaponMass += weapon.Mass;
                        break;

                    case WeaponType.Magic:      // tip 5
                        MagicMass += weapon.Mass;
                        break;

                    default:
                        // tip 0 (ranged base class) and tip 4 (thrown) — no contribution, as above. A
                        // weapon with no row at all is bucketed as a physical weapon by the switch
                        // selector, so the `default` is only the two zero-mass tips.
                        break;
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

        // ===== Save / Load Persistence =====

        public IReadOnlyDictionary<string, GameWeaponInstance> Weapons => weapons;
        public IReadOnlyDictionary<string, GameArmorInstance> Armors => armors;
        public IReadOnlyDictionary<string, GameItemInstance> Items => items;
        public IReadOnlyDictionary<string, int> FavoriteSlots => favoriteSlots;
        public float[] CategoryMass => categoryMass;

        public GameInventorySaveData CreateSaveData()
        {
            var data = new GameInventorySaveData
            {
                currentWeaponId = CurrentWeaponId ?? "",
                currentArmorId = CurrentArmorId ?? "",
                categoryMass = (float[])categoryMass.Clone()
            };

            foreach (var kvp in weapons)
            {
                if (kvp.Value != null)
                {
                    data.weapons.Add(kvp.Value.GetSaveData());
                }
            }

            foreach (var kvp in armors)
            {
                if (kvp.Value != null)
                {
                    data.armors.Add(kvp.Value.GetSaveData());
                }
            }

            foreach (var kvp in items)
            {
                if (kvp.Value != null)
                {
                    data.items.Add(kvp.Value.GetSaveData());
                }
            }

            foreach (var kvp in favoriteSlots)
            {
                data.favoriteSlots.Add(new GameInventorySaveData.FavoriteSlotSaveEntry
                {
                    itemId = kvp.Key,
                    slot = kvp.Value
                });
            }

            return data;
        }

        public void RestoreFromSaveData(GameInventorySaveData data, Func<string, ItemDefinition> definitionResolver = null)
        {
            if (data == null) return;

            weapons.Clear();
            armors.Clear();
            items.Clear();
            favoriteSlots.Clear();

            CurrentWeaponId = data.currentWeaponId ?? "";
            CurrentArmorId = data.currentArmorId ?? "";
            if (data.categoryMass != null && data.categoryMass.Length >= 4)
            {
                Array.Copy(data.categoryMass, categoryMass, 4);
            }

            if (data.items != null)
            {
                foreach (var itemData in data.items)
                {
                    if (itemData == null || string.IsNullOrEmpty(itemData.itemId)) continue;
                    ItemDefinition def = definitionResolver?.Invoke(itemData.itemId);
                    if (def == null)
                    {
                        def = ScriptableObject.CreateInstance<ItemDefinition>();
                        def.itemId = itemData.itemId;
                        def.name = itemData.itemId;
                    }
                    var item = new GameItemInstance(def, itemData);
                    items[itemData.itemId] = item;
                }
            }

            if (data.weapons != null)
            {
                foreach (var weaponData in data.weapons)
                {
                    if (weaponData == null || string.IsNullOrEmpty(weaponData.weaponId)) continue;
                    ItemDefinition def = definitionResolver?.Invoke(weaponData.weaponId);
                    if (def == null)
                    {
                        def = ScriptableObject.CreateInstance<ItemDefinition>();
                        def.itemId = weaponData.weaponId;
                        def.name = weaponData.weaponId;
                    }
                    var weapon = new GameWeaponInstance(def, weaponData);
                    weapons[weaponData.weaponId] = weapon;
                }
            }

            if (data.armors != null)
            {
                foreach (var armorData in data.armors)
                {
                    if (armorData == null || string.IsNullOrEmpty(armorData.armorId)) continue;
                    ItemDefinition def = definitionResolver?.Invoke(armorData.armorId);
                    if (def == null)
                    {
                        def = ScriptableObject.CreateInstance<ItemDefinition>();
                        def.itemId = armorData.armorId;
                        def.name = armorData.armorId;
                    }
                    var armor = new GameArmorInstance(def, armorData);
                    armors[armorData.armorId] = armor;
                }
            }

            if (data.favoriteSlots != null)
            {
                foreach (var fav in data.favoriteSlots)
                {
                    if (fav != null && !string.IsNullOrEmpty(fav.itemId))
                    {
                        favoriteSlots[fav.itemId] = fav.slot;
                    }
                }
            }

            CalculateMass();
            CalculateWeaponMass();
        }
    }
}

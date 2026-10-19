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
        /// The item id caps live under — AS3 <c>Invent.money</c> is literally
        /// <c>this.items["money"]</c>, and <c>&lt;item id='money' tip='money'&gt;</c>
        /// (<c>AllData.as:3859</c>) is an ordinary row of the bag.
        /// </summary>
        public const string MoneyItemId = "money";

        /// <summary>
        /// The player's caps — AS3 <c>Invent.money.kol</c> (<c>Invent.as:43, 96</c>), written at
        /// <b>50</b> by <c>addMin</c> (<c>Invent.as:1610</c>).
        ///
        /// <para><b>This used to be a plain count and the two numbers disagreed.</b> The play-test that
        /// found it posted one screenshot with <c>Bag with caps 200005</c> in the bag and
        /// <c>Caps: 45</c> on the same screen. Both were live: caps had been split into a count here
        /// and an item row there, and nothing ever moved one into the other. Loot, quest rewards and
        /// the trade page each wrote one of the two, so which number a player saw depended on which
        /// path had touched it last.</para>
        ///
        /// <para><b>The oracle has exactly one store, and this is it.</b> <c>Invent.money</c> is
        /// <c>items["money"]</c>, and every reader reads that row: <c>PipBuck.as:411</c> mirrors it
        /// into <c>pip.money</c>, <c>PipBuck.as:539</c> draws <c>invent.money.kol</c>,
        /// <c>PipPageVend.as:512</c> spends it, <c>PipPageInv.as:323</c> prints it. So this property is
        /// now a facade over the row rather than a second location. The old divergence record on this
        /// member said "the count is the one place caps can change" — with one store that is finally
        /// true.</para>
        ///
        /// <para><b>What costs what.</b> The bag now carries the caps row, which the Oracle's bag does
        /// too (it lists on the second inventory page alongside <c>valuables</c> — see
        /// <see cref="InventoryPages"/>). It is excluded from carried mass below, because AS3's
        /// <c>money</c> row has <c>mass: 0</c> (<c>Item.mass</c> defaults to 0 and the row carries no
        /// <c>@m</c>) while <see cref="ItemDefinition.weight"/> defaults to 1 for every row imported
        /// without one — a data divergence that is owner-only to close.</para>
        /// </summary>
        public int Money
        {
            get => items.TryGetValue(MoneyItemId, out GameItemInstance item) ? item.Quantity : 0;
            set
            {
                int current = Money;
                if (current == value) return;

                if (items.TryGetValue(MoneyItemId, out GameItemInstance item))
                {
                    // The setter clamps at 0, which is the same floor AS3's `minus` reaches.
                    item.Quantity = value;
                }
                else if (ItemDefinitionResolver != null
                         && ItemDefinitionResolver(MoneyItemId) is ItemDefinition definition)
                {
                    var created = new GameItemInstance(definition, Mathf.Max(0, value));
                    items[MoneyItemId] = created;
                    OnItemAdded?.Invoke(created);
                }
                else
                {
                    // No resolver and no row: the write cannot be made. Reported rather than silently
                    // dropped, because a silently dropped purchase is money the player paid and did not
                    // get, and a silently dropped grant is money they were told they had.
                    Debug.LogWarning("[GameInventory] Caps cannot be written: no '" + MoneyItemId +
                                     "' item row exists and no ItemDefinitionResolver can create one. " +
                                     "Check that PlayerInventory has been given the content registry.");
                    return;
                }

                CalculateMass();
                MoneyChanged?.Invoke(Money);
            }
        }

        /// <summary>
        /// Resolves an item definition by id — AS3's <c>AllData.d.item</c>. The seam the caps row
        /// needs: <see cref="Money"/> can create the row on first write, which is the only reason the
        /// count-vs-item divergence could ever have looked necessary.
        ///
        /// <para>Set by <c>PlayerInventory.Construct</c> from the same content registry that feeds
        /// <see cref="ArmorDefinitionResolver"/>; null until then, and null in a headless fixture.</para>
        /// </summary>
        public Func<string, ItemDefinition> ItemDefinitionResolver { get; set; }

        /// <summary>Raised after <see cref="Money"/> changes, with the new value. The monitor and the
        /// trade page both redraw from this rather than polling.</summary>
        public event Action<int> MoneyChanged;

        /// <summary>Whether the player can pay <paramref name="amount"/> caps.</summary>
        public bool CanAfford(int amount) => amount <= Money;

        /// <summary>
        /// Credit caps. The caller is the sale path (AS3 <c>inv.money.kol += floor(kolSell)</c>,
        /// <c>PipPageVend.as:990</c>) and the loot path.
        /// </summary>
        public void AddMoney(int amount)
        {
            if (amount <= 0) return;
            Money = Money + amount;
        }

        /// <summary>
        /// Debit caps if the player can afford it. Returns false and changes nothing otherwise — the
        /// caller reports the shortfall. The caller is the purchase path (AS3
        /// <c>inv.money.kol -= ceil(kolBou)</c>, <c>PipPageVend.as:948</c>).
        /// </summary>
        public bool TrySpendMoney(int amount)
        {
            if (amount <= 0) return true;
            if (amount > Money) return false;

            Money = Money - amount;
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

        /// <summary>
        /// Fires on <b>every</b> successful <see cref="AddItem"/> — a brand-new stack <i>and</i> a merge
        /// onto an existing one — carrying the instance and the quantity that arrived.
        /// </summary>
        /// <remarks>
        /// <para><b>Why <see cref="OnItemAdded"/> cannot be used for this.</b> It fires only when a new
        /// stack is created, so it is a "this item id appeared" signal, not an "items arrived" one.
        /// The land's <c>eve='take'</c> scripts want the first (<c>LandItemScriptRunner</c>), which is
        /// why that event keeps its meaning; a collect objective wants the second, and used to get
        /// nothing at all after the first pickup — the reported "picking up a cogwheel does not move
        /// the 0/20 counter".</para>
        ///
        /// <para><b>The delta, not the total.</b> A listener that needs the total asks the inventory
        /// (<c>GetItem(id).Quantity</c>); carrying it here would go stale the moment two listeners ran
        /// in the other order.</para>
        /// </remarks>
        public event Action<GameItemInstance, int> ItemQuantityAdded;
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
                ItemQuantityAdded?.Invoke(existingItem, quantity);
            }
            else
            {
                // Create new item instance
                var newItem = new GameItemInstance(itemDefinition, quantity);
                items[itemDefinition.itemId] = newItem;
                OnItemAdded?.Invoke(newItem);
                ItemQuantityAdded?.Invoke(newItem, quantity);
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
                // AS3 `Invent.take`'s two branches for a weapon already held (`Invent.as:1076-1094`):
                //
                //   1. upgrade the tier when the incoming copy is a HIGHER variant
                //      (`if(weapons[id].variant < l.variant) updWeapon(id, l.variant)`), and
                //   2. repair it to the incoming copy's hp.
                //
                // The port only ever did (2). That made a dropped `carbine^1` — the whole point of the
                // raider hero's weapon — indistinguishable from picking up a second plain carbine: the
                // tier was dropped on the floor and the bag kept showing " - " with base stats.
                //
                // The upgrade is gated exactly as `Weapon.updVariant` gates it (`Weapon.as:895-900`): a
                // weapon with no `<com>` (uniq -1) has no tier 1 to upgrade to, so the request is
                // ignored rather than half-applied. `uniq == 0` still upgrades — see
                // `EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon`.
                float uniq = existingWeapon.WeaponDefinition != null ? existingWeapon.WeaponDefinition.uniq : -1f;
                if (PFE.Systems.Loot.EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(
                        existingWeapon.Variant, variant, uniq))
                {
                    existingWeapon.UpgradeVariant(variant);
                }

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
                    // The caps row is massless in the Oracle: `<item id='money' tip='money'
                    // fall='fall_caps'/>` (AllData.as:3859) carries no `@m`, and `Item.mass` defaults
                    // to 0 (Item.as:91) — so `mass[3] += mass * kol` adds 0 no matter how rich the
                    // player is. The port's `ItemDefinition.weight` defaults to 1 for every row
                    // imported without an `@m`, which would charge 200,000+ caps to the bag. Skipped
                    // here rather than by setting `weight: 0` on the asset, because the asset is
                    // owner-only and the same default affects every other unweighted row.
                    if (string.Equals(kvp.Key, MoneyItemId, StringComparison.Ordinal)) continue;

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
            if (item.Definition != null && item.Definition.itemId != MoneyItemId)
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

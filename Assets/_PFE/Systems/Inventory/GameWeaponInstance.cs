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
        /// The weapon row this is an instance <i>of</i> — the port's counterpart to AS3's <c>Weapon</c>
        /// reading its own <c>&lt;weapon&gt;</c> node.
        ///
        /// <para><b>Why there are two definition fields.</b> The port splits content that AS3 kept in one
        /// <c>Item</c>: a weapon id lives in <c>AllData.d.weapon</c> and resolves to a
        /// <see cref="WeaponDefinition"/>, while a consumable or component id lives in
        /// <c>AllData.d.item</c> and resolves to an <see cref="ItemDefinition"/>. The two tables are
        /// disjoint, so a weapon id resolves to <c>null</c> through the item resolver — 100 % of the time,
        /// for every weapon — which is exactly why <c>GameInventory.AddWeapon</c> could not build one and
        /// weapons never reached the inventory. <see cref="definition"/> is kept because the port's save
        /// format and the PipBuck list both read a name and an id off it, and because armour-shaped
        /// callers still hand one in.</para>
        ///
        /// <para>At most one of the two is set. <see cref="ItemId"/> is the single accessor that does not
        /// care which.</para>
        /// </summary>
        [SerializeField]
        private WeaponDefinition weaponDefinition;

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

        /// <summary>
        /// The weapon row, or <c>null</c> for an instance built from an item row. See
        /// <see cref="weaponDefinition"/> for why both exist.
        /// </summary>
        public WeaponDefinition WeaponDefinition => weaponDefinition;

        /// <summary>
        /// The id this instance is keyed by — the weapon id when built from a weapon row, otherwise the
        /// item id, otherwise empty.
        ///
        /// <para><b>Read this, not <c>Definition.itemId</c>.</b> Every caller that wants "what is this"
        /// — the save format, the PipBuck row key, the equipped-weapon comparison — has to work for both
        /// shapes, and <c>Definition</c> is null on the weapon path. Before this existed the weapon path
        /// had no id at all, which is part of why it had no callers.</para>
        /// </summary>
        public string ItemId => weaponDefinition != null ? weaponDefinition.weaponId
                              : definition != null ? definition.itemId
                              : string.Empty;

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
                    // AS3 calcWeaponMass() sums Weapon.mass for respect 0/2 only (Invent.as:1450-1452),
                    // which is the test above. The value itself comes off the weapon row
                    // (Item.as:278-288) — reading an ItemDefinition's `weight` here is how a weapon's
                    // mass came out as 0 for every weapon: a weapon id has no item row, so `definition`
                    // was always null on this path.
                    if (weaponDefinition != null) return weaponDefinition.Mass;
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
            maxHealth = 100f; // No weapon row to read a durability from — see the WeaponDefinition ctor.
            currentHealth = (health == float.MaxValue || health > maxHealth) ? maxHealth : health;
            currentAmmo = ammo;
            loadedAmmoType = ""; // TODO: Get from weapon definition
            respect = respectState;
            variant = variantLevel;
        }

        public GameWeaponInstance(ItemDefinition weaponDefinition, GameWeaponSaveData saveData)
            : this(weaponDefinition, saveData?.currentHealth ?? float.MaxValue, saveData?.currentAmmo ?? 0,
                   (WeaponRespect)(saveData?.respect ?? 0), saveData?.variant ?? 0)
        {
            if (saveData != null)
            {
                loadedAmmoType = saveData.loadedAmmoType ?? "";
            }
        }

        /// <summary>
        /// Build an instance from the weapon row itself — the path every weapon the player owns takes
        /// once it has been resolved out of <c>ContentType.Weapon</c>.
        ///
        /// <para><b>Three fields are real here that the item path has to stub.</b> AS3's <c>Weapon</c>
        /// reads all three off its own <c>&lt;weapon&gt;</c> node: <c>maxhp</c> from
        /// <c>&lt;char maxhp&gt;</c>, <c>hold</c> (the magazine) from <c>&lt;ammo holder&gt;</c>, and
        /// <c>ammo</c> from <c>&lt;ammo&gt;</c> / the base id. The port stores those as
        /// <see cref="WeaponDefinition.maxDurability"/> (the importer reads <c>charT1@maxhp</c>,
        /// <c>WeaponDataImporter.cs:325</c>), <see cref="WeaponDefinition.magazineSize"/> and
        /// <see cref="WeaponDefinition.ammoType"/>. The item-row constructor cannot do this — it has no
        /// weapon row — which is why it still hard-codes <c>maxHealth = 100</c>.</para>
        ///
        /// <para><b>The condition is not invented:</b> <c>maxHealth</c> is the row's real durability, so
        /// r32 reads 200 rather than the item path's flat 100, and all 213 shipped weapon assets carry a
        /// non-zero value. A caller that wants a damaged weapon still passes <paramref name="health"/>,
        /// which is clamped to the row's maximum exactly as before.</para>
        /// </summary>
        public GameWeaponInstance(WeaponDefinition weaponRow, float health = float.MaxValue,
                                  int ammo = 0, WeaponRespect respectState = WeaponRespect.Active,
                                  int variantLevel = 0)
        {
            if (weaponRow == null)
            {
                Debug.LogError("[GameWeaponInstance] Cannot create instance with null definition");
                return;
            }

            weaponDefinition = weaponRow;
            maxHealth = weaponRow.maxDurability;
            currentHealth = (health == float.MaxValue || health > maxHealth) ? maxHealth : health;
            currentAmmo = ammo;
            loadedAmmoType = weaponRow.ammoType ?? "";
            respect = respectState;
            variant = variantLevel;
        }

        public GameWeaponInstance(WeaponDefinition weaponRow, GameWeaponSaveData saveData)
            : this(weaponRow, saveData?.currentHealth ?? float.MaxValue, saveData?.currentAmmo ?? 0,
                   (WeaponRespect)(saveData?.respect ?? 0), saveData?.variant ?? 0)
        {
            if (saveData != null)
            {
                loadedAmmoType = saveData.loadedAmmoType ?? "";
            }
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
        /// Raise this instance to a higher variant tier — AS3 <c>Weapon.updVariant(1)</c>
        /// (<c>Weapon.as:895-912</c>), reached from <c>Invent.updWeapon</c> when a taken weapon's tier
        /// beats the one already held (<c>Invent.as:1076-1085</c>).
        ///
        /// <para><b>One-way, and it is the oracle's one-way.</b> <c>updVariant</c> assigns
        /// <c>variant = param1</c> with no comparison of its own; the "only upward" rule lives in
        /// <c>take</c>. Keeping the guard here as well means a second caller cannot walk a weapon back
        /// down a tier.</para>
        ///
        /// <para><b>Why the health is reset.</b> <c>updVariant</c> re-runs <c>getXmlParam()</c>, and that
        /// method ends with <c>this.hp = this.maxhp</c> (<c>Weapon.as:579</c>) — so an upgrade in the
        /// original game <i>also</i> repairs the weapon to full. That is a real consequence of picking a
        /// unique up, not an invention here.</para>
        ///
        /// <para><b>What is deliberately not done: the tier-1 stats.</b> The oracle's
        /// <c>getXmlParam</c> re-reads <c>char[variant]</c> over the base run, so a
        /// <c>carbine^1</c> fires at 13 damage instead of 11 and holds 40 rounds instead of 30. The
        /// port's <see cref="maxHealth"/> stays the base tier's because the merge that produces those
        /// numbers (<c>WeaponVariantXml.MergeRuns</c>) has no runtime applier yet — see
        /// <c>docs/OnWeaponsSystemImplementation/15_UniqueWeaponVariants_PortResearch_2026-10-13.md</c>
        /// §8.7. The tier is recorded, named and priced correctly; the stat delta is the remaining
        /// half, and it is one gap for the player and the enemy alike.</para>
        /// </summary>
        public void UpgradeVariant(int newVariant)
        {
            if (newVariant <= variant) return;

            variant = newVariant;
            currentHealth = maxHealth;
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
                // ItemId, not Definition.itemId: on the weapon path Definition is null, so the old
                // expression wrote "" and the weapon would come back from a save with no identity at all.
                weaponId = ItemId,
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

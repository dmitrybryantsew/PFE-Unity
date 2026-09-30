using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Runtime instance of armor in the game inventory.
    ///
    /// Based on ActionScript Armor.as from the original game.
    ///
    /// <para><b>This is the item half of the armour model; <see cref="ArmourState"/> is the combat
    /// projection.</b> It owns exactly one thing the projection cannot: the <b>condition</b>. The
    /// ratings live on the definition (AS3 keeps them on the <c>Armor</c> object, but they are read
    /// from XML and never change per-instance, so the port puts them on <c>ItemDefinition.equipment</c>).
    /// <see cref="ToArmourState"/> joins the two, and <see cref="SetIntegrity"/> is the write-back that
    /// keeps <c>Repair()</c> and the save honest.</para>
    /// </summary>
    [System.Serializable]
    public class GameArmorInstance : IArmourItem
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
            maxHealth = ResolveMaxHealth(armorDefinition);
            currentHealth = (health == float.MaxValue || health > maxHealth) ? maxHealth : health;
            level = armorLevel;
        }

        /// <summary>
        /// The item's durability ceiling. AS3 reads it from the armour element's own <c>@hp</c> and
        /// falls back to <c>100</c> (<c>Armor.as:74-76</c> for the field defaults, <c>:105-107</c> for
        /// the parse) — note that is the <b>item's</b> <c>hp</c>, not the unit's <c>armorhp</c>, which
        /// is a different attribute on a different element.
        ///
        /// <para>The previous version hardcoded <c>100f</c> behind a <c>// TODO</c>. The 100 was never
        /// wrong — it is the oracle's default — but it was also never <i>read</i>, so an armour
        /// definition authored with a different durability would have been silently ignored.</para>
        /// </summary>
        private static float ResolveMaxHealth(ItemDefinition armorDefinition)
        {
            float declared = armorDefinition != null ? armorDefinition.armorHP : 0f;
            return declared > 0f ? declared : 100f;
        }

        public GameArmorInstance(ItemDefinition armorDefinition, GameArmorSaveData saveData)
            : this(armorDefinition, saveData?.currentHealth ?? float.MaxValue, saveData?.level ?? 0)
        {
        }

        // ===== Public Methods =====

        /// <summary>
        /// Whether this armour may be repaired at all — <c>!AS3 @norep</c>.
        ///
        /// <para><b>Correction to an earlier note here.</b> This said <c>PipPageWork.as:258</c> "gates the
        /// repair action" on <c>!norep &amp;&amp; !und &amp;&amp; hp &lt; maxhp</c>. It does not gate a call — it
        /// builds the workbench's <i>list of repair candidates</i>, and AS3's <c>Armor.repair()</c> itself
        /// (<c>Armor.as:340-348</c>) is unconditional. So <c>norep</c> means "never offered", not "refuses".
        /// See <see cref="IArmourItem.CanRepair"/> for why the port enforces it at the item instead, and
        /// why that is observably equivalent.</para>
        /// </summary>
        public bool CanRepair => definition == null || !definition.armorNoRepair;

        /// <summary>
        /// Repair this armor.
        ///
        /// <para><b>Refuses outright when the item is <c>@norep</c></b> (<c>Armor.as:117-119</c>).
        /// A no-op rather than a clamp: AS3 does not offer the action at all, so there is no partial
        /// repair to model. <c>tre</c> is the only item in <c>AllData</c> that depends on this —
        /// the other sixteen <c>norep</c> armours are also <c>und</c> and never wear in the first
        /// place.</para>
        /// </summary>
        public void Repair(float amount, float repairMultiplier = 1f)
        {
            if (!CanRepair)
                return;

            float repairAmount = amount * repairMultiplier;
            currentHealth = Mathf.Min(currentHealth + repairAmount, maxHealth);
        }

        // ===== Combat projection (IArmourItem) =====

        /// <summary>
        /// The definition's content id — AS3 <c>Armor.id</c>. Empty when there is no definition,
        /// rather than <c>null</c>, so the visual layer can compare without a null check.
        /// </summary>
        public string Id => definition != null ? definition.itemId : string.Empty;

        /// <summary>AS3 <c>Armor.hideMane</c> — whether this helmet hides the character's mane.</summary>
        public bool HideMane => definition != null && definition.armorHideMane;

        /// <summary>
        /// Join this item's condition to its definition's ratings. AS3 <c>Armor.setArmor()</c>'s read
        /// half (<c>Armor.as:297-311</c>), minus the projection step — the condition factor is applied
        /// by <see cref="ArmourState"/> on read, not baked in here.
        /// </summary>
        /// <remarks>
        /// <b>Ratings are read at the item's upgrade level</b> — AS3 <c>Armor.getXmlParam(this.xml.upd[this.lvl])</c>
        /// (<c>Armor.as:186-193</c>). An armour's <c>armor</c>/<c>marmor</c>/<c>qual</c> all change per
        /// level, so reading the base set regardless of <see cref="Level"/> would report a level-3 plate
        /// with level-0 numbers.
        /// </remarks>
        public ArmourState ToArmourState()
        {
            EquipmentData equipment = definition != null
                ? definition.EquipmentAtLevel(level)
                : default;

            return ArmourState.FromItem(
                integrity: currentHealth,
                maxIntegrity: maxHealth,
                physicalRating: equipment.armor,
                energyRating: equipment.magicArmor,
                reliability: equipment.reliability,
                resists: equipment.resists,
                indestructible: definition != null && definition.armorIndestructible,
                bodyArmour: definition != null && definition.armorTip == 1);
        }

        /// <summary>
        /// Write condition back from the projection after a hit or a repair. The clamp in
        /// <see cref="CurrentHealth"/> is what stops a rounding overshoot from exceeding
        /// <see cref="MaxHealth"/>, which would make the condition factor read above 1.
        /// </summary>
        public void SetIntegrity(float integrity) => CurrentHealth = integrity;

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

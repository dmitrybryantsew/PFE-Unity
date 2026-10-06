using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for item data.
    /// Replaces XML-based item definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Item Definition
    ///
    /// Supports ~500 items including: consumables, components, keys, medical items,
    /// crafting materials, books, equipment, and special items.
    /// </summary>
    [CreateAssetMenu(fileName = "NewItemDef", menuName = "PFE/Item Definition")]
    public class ItemDefinition : ScriptableObject, IGameContent, PFE.Systems.Inventory.IItemStats
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this item")]
        public string itemId;

        // IItemStats
        string PFE.Systems.Inventory.IItemStats.itemId => itemId;
        ItemType PFE.Systems.Inventory.IItemStats.type => type;

        // IGameContent
        string IGameContent.ContentId => itemId;
        ContentType IGameContent.ContentType => ContentType.Item;

        // Legacy property for compatibility
        public string ID => itemId;

#if ODIN_INSPECTOR
        [BoxGroup("Core")]
#else
        [Header("Core")]
#endif
        [Tooltip("Primary type classification")]
        public ItemType type;

        [Tooltip("For variants - references base item ID")]
        public string baseItemId;

        [Tooltip("Modifier type for ammo variants")]
        public int modifierType = 0;

        [Tooltip("Inventory category for UI filtering")]
        public InventoryCategory inventoryCategory = InventoryCategory.General;

        [Tooltip("Hide from inventory UI")]
        public bool isHidden = false;

        [Tooltip("Keep container after using")]
        public bool keepContainer = false;

        [Tooltip("Can only be sold, not used")]
        public bool isSellOnly = false;

#if ODIN_INSPECTOR
        [BoxGroup("Acquisition")]
#else
        [Header("Acquisition")]
#endif
        [Range(0f, 1f)]
        [Tooltip("Drop chance from defeated enemies")]
        public float dropChance = 0.5f;

        [Tooltip("Required story stage to appear")]
        public int requiredStoryStage = 0;

        [Tooltip("Required character level to use")]
        public int requiredLevel = 0;

        [Tooltip("Base purchase price")]
        public int basePrice = 10;

        [Tooltip("Sell price to merchants")]
        public int sellPrice = 5;

        [Tooltip("Weight in inventory")]
        public float weight = 1f;

        [Tooltip("Maximum stack size")]
        public int stackSize = 1;

#if ODIN_INSPECTOR
        [BoxGroup("Usage")]
#else
        [Header("Usage")]
#endif
        [Tooltip("How the item is used")]
        public UsageType usageType = UsageType.None;

        [Tooltip("Sort order in inventory")]
        public int sortOrder = 0;

#if ODIN_INSPECTOR
        [BoxGroup("Presentation")]
#else
        [Header("Presentation")]
#endif
        [Tooltip("Item icon for UI")]
        public Sprite icon;

        [Tooltip("Sound played on pickup")]
        public AudioClip pickupSound;

        [Tooltip("Fallback color if no icon")]
        public Color fallbackColor = Color.white;

#if ODIN_INSPECTOR
        [BoxGroup("Type Specific Data")]
#else
        [Header("Type Specific Data")]
#endif
        [Tooltip("Medical/healing item data")]
        public MedicalData medicalData;

        [Tooltip("Ammo variant data")]
        public AmmoVariantData ammoVariant;

        [Tooltip("Crafting recipe data")]
        public CraftingData crafting;

        [Tooltip("Book/skill book data")]
        public BookData book;

        [Tooltip("Equipment tool data")]
        public EquipmentData equipment;

        // ── Per-item armour fields ────────────────────────────────────────────
        // These are parsed from the <armor> element itself, not from <upd>, so they hold for the whole
        // item and deliberately do NOT live on EquipmentData (which is per upgrade level). AS3 keeps
        // them the same way: Armor.hp / .tip / .hideMane / .und are read once at construction
        // (Armor.as:105-107, :97-103, :170-172, :117-119) while the ratings are re-read per level.

        /// <summary>
        /// Armour durability ceiling — AS3 <c>@hp</c> on the armour element, read once
        /// (<c>Armor.as:105-107</c>), defaulting to <c>100</c> when absent (<c>Armor.as:74-76</c>).
        /// Note this is the <b>item's</b> <c>hp</c>; a unit's pool uses <c>@armorhp</c>, a different
        /// attribute on a different element.
        /// </summary>
        [Tooltip("Armour durability ceiling (AS3 @hp). 0 means 'absent' — GameArmorInstance then uses 100.")]
        public int armorHP;

        /// <summary>
        /// Equipment slot — AS3 <c>Armor.tip</c>, default <c>1</c>. <c>1</c> is body armour and
        /// <c>3</c> is an amulet, and <c>UnitPlayer.changeArmor()</c> dispatches on it
        /// (<c>:3790</c> vs <c>:3824</c>). Note <c>tip == 1</c> is also what makes AS3's constructor
        /// force <c>resist[D_PINK] = -0.5</c> (<c>Armor.as:180-183</c>) — a hardcoded rule, not data.
        /// </summary>
        [Tooltip("Equipment slot: 1 = body armour, 3 = amulet (AS3 @tip).")]
        public int armorTip;

        /// <summary>
        /// AS3 <c>Armor.hideMane</c>, from the armour element's <c>hide='1'</c>
        /// (<c>Armor.as:170-172</c>). Drives <c>Appear.hideMane</c> on equip
        /// (<c>UnitPlayer.changeArmor():3811</c>) — whether this helmet hides the character's mane.
        /// <b>Populated by the importer; read through <c>IArmourItem.HideMane</c> by the equip path</b>
        /// (<c>PlayerCharacterVisual.ApplyArmour</c>).
        /// </summary>
        [Tooltip("Whether this armour hides the character's mane (AS3 @hide).")]
        public bool armorHideMane;

        /// <summary>
        /// AS3 <c>Armor.und</c> — indestructible armour takes <b>no wear at all</b>
        /// (<c>Armor.damage():315-318</c>). <c>pip</c>, <c>socks</c> and every amulet set it.
        /// </summary>
        [Tooltip("Indestructible armour: takes no wear (AS3 @und).")]
        public bool armorIndestructible;

        /// <summary>
        /// AS3 <c>Armor.norep</c> — the armour <b>cannot be repaired</b> (<c>Armor.as:117-119</c>,
        /// consumed by <c>PipPageWork.as:258</c>).
        ///
        /// <para><b>Load-bearing for exactly one item: <c>tre</c>.</b> Sixteen of the seventeen
        /// <c>norep</c> armours are also <c>und</c>, where the flag is redundant; <c>tre</c> is the
        /// only one that takes wear but may never be repaired. Without this field the port's
        /// <see cref="GameArmorInstance.Repair"/> would happily restore it, which the oracle forbids.</para>
        /// </summary>
        [Tooltip("Cannot be repaired (AS3 @norep). Only 'tre' relies on it.")]
        public bool armorNoRepair;

        /// <summary>
        /// Per-upgrade-level equipment stats for armour — AS3's <c>&lt;upd&gt;</c> children, where
        /// <b>index == level</b>.
        ///
        /// <para><b>Why this exists at all.</b> AS3's armour ratings are not one set of numbers: every
        /// <c>&lt;armor&gt;</c> element carries one <c>&lt;upd&gt;</c> per upgrade level, and
        /// <c>Armor.getXmlParam(this.xml.upd[this.lvl])</c> (<c>Armor.as:186-193</c>) re-reads them on
        /// upgrade. <c>kombu</c> goes <c>armor</c> 4 → 5 → 6 and <c>qual</c> 0.6 → 0.7 → 0.8. Storing
        /// only the first level would silently drop two thirds of the ratings in <c>AllData</c>, and
        /// <c>GameArmorInstance.level</c> already exists with nothing to index.</para>
        ///
        /// <para><b>The invariant:</b> when this is non-empty, <c>armourLevels[0]</c> must equal
        /// <see cref="equipment"/>. <see cref="equipment"/> stays the level-0 snapshot so the many
        /// callers that do not care about upgrades keep working unchanged, and
        /// <c>ArmourDataParserTests</c> asserts the two agree on everything the importer produces —
        /// which is what keeps a duplicated value from drifting.</para>
        ///
        /// <para>Empty for non-armour items, and for any armour definition authored by hand without
        /// the importer; <see cref="EquipmentAtLevel"/> falls back to <see cref="equipment"/>.</para>
        /// </summary>
        [Tooltip("Per-upgrade-level stats for armour (index == level). armourLevels[0] mirrors 'equipment'.")]
        public EquipmentData[] armourLevels;

        /// <summary>
        /// The equipment stats at an upgrade level. AS3 <c>Armor.getXmlParam(xml.upd[lvl])</c>.
        ///
        /// <para>Falls back to <see cref="equipment"/> when there is no level table, and clamps to the
        /// last level rather than throwing — AS3 indexes <c>upd[lvl]</c> directly, so an out-of-range
        /// level there is <c>undefined</c> and every field reads as <c>0</c>, which would silently
        /// produce an armour with no ratings at all.</para>
        /// </summary>
        public EquipmentData EquipmentAtLevel(int level)
        {
            if (armourLevels == null || armourLevels.Length == 0)
                return equipment;

            if (level <= 0) return armourLevels[0];
            return level < armourLevels.Length ? armourLevels[level] : armourLevels[armourLevels.Length - 1];
        }

        /// <summary>
        /// Whether this definition is armour — i.e. it carries a per-level table.
        ///
        /// <para><b>Use this rather than <c>type == ItemType.Equipment</c>.</b> Armour is imported with
        /// <c>type = ItemType.Equipment</c>, but so is any other equipment, so that test would let a
        /// caller build an "armour" from a definition with no ratings and no level table — an item that
        /// silently absorbs nothing. <c>armourLevels</c> is written only by <c>ArmourDataParser</c>, so
        /// it is the honest discriminator.</para>
        /// </summary>
        public bool IsArmour => armourLevels != null && armourLevels.Length > 0;

        [Tooltip("Component category")]
        public ComponentData component;

        [Tooltip("Potion/chem data")]
        public PotionData potion;

        /// <summary>
        /// Spell data — AS3's <c>&lt;item tip='spell' …&gt;</c> attributes, every one of them read by
        /// <c>Spell.as:83-131</c>. Populated by <c>SimpleDataImporter</c> for the nine <c>sp_*</c> rows.
        ///
        /// <para><b>Read <see cref="SpellData.IsPopulated"/>, not <c>type == ItemType.Spell</c>.</b>
        /// The type is stamped from <c>tip</c> by two separate passes (the importer and
        /// <c>FixDataImport</c>), while this block is written only by an import that actually read the
        /// attributes — so an asset can legitimately carry the type with an empty block, and a caller
        /// that trusted the type alone would cast a spell with zero cost and zero effect. Same
        /// discriminator discipline as <see cref="IsArmour"/>.</para>
        /// </summary>
        [Tooltip("Spell data (tip='spell' items only). Check IsPopulated before reading.")]
        public SpellData spellData;

#if ODIN_INSPECTOR
        [BoxGroup("Skills")]
#else
        [Header("Skills")]
#endif
        [Tooltip("Skills granted or modified by this item")]
        public SkillModifier[] skillModifiers;

#if ODIN_INSPECTOR
        [BoxGroup("Effects")]
#else
        [Header("Effects")]
#endif
        [Tooltip("Status effects applied when used")]
        public EffectReference[] effects;

#if ODIN_INSPECTOR
        [BoxGroup("Crafting Components")]
#else
        [Header("Crafting Components")]
#endif
        [Tooltip("Components required to craft this item")]
        public ComponentRequirement[] components;

        [Header("Display")]
        [TextArea(3, 10)]
        public string displayName = "Item Name";

        [TextArea(2, 5)]
        public string description = "Item description.";
    }
}

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
    public class ItemDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this item")]
        public string itemId;

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

        [Tooltip("Component category")]
        public ComponentData component;

        [Tooltip("Potion/chem data")]
        public PotionData potion;

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

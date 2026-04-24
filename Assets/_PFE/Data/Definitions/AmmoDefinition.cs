using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for ammunition data.
    /// Replaces XML-based ammo definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Ammo Definition
    ///
    /// Supports ~50 ammo types including: different calibers (10mm, 5.56mm, etc.)
    /// and modifier variants (armor piercing, explosive, incendiary, etc.).
    /// </summary>
    [CreateAssetMenu(fileName = "NewAmmoDef", menuName = "PFE/Ammo Definition")]
    public class AmmoDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this ammo type")]
        public string ammoId;

        // IGameContent
        string IGameContent.ContentId => ammoId;
        ContentType IGameContent.ContentType => ContentType.Ammo;

        // Legacy property for compatibility
        public string ID => ammoId;

#if ODIN_INSPECTOR
        [BoxGroup("Core")]
#else
        [Header("Core")]
#endif
        [Tooltip("Base ID for variant ammo (references standard ammo)")]
        public string baseId;

        [Tooltip("Maximum stack size")]
        public int stackSize = 12;

#if ODIN_INSPECTOR
        [BoxGroup("Acquisition")]
#else
        [Header("Acquisition")]
#endif
        [Range(0f, 1f)]
        [Tooltip("Drop chance from defeated enemies")]
        public float dropChance = 1f;

        [Tooltip("Required story stage to appear")]
        public int requiredStoryStage = 0;

        [Tooltip("Required character level to use")]
        public int requiredLevel = 0;

        [Tooltip("Base purchase price")]
        public int basePrice = 1;

        [Tooltip("Sell price to merchants")]
        public int sellPrice = 0;

        [Tooltip("Weight per unit")]
        public float weight = 0.5f;

#if ODIN_INSPECTOR
        [BoxGroup("Variant Modifiers")]
#else
        [Header("Variant Modifiers")]
#endif
        [Tooltip("Variant modifier type")]
        public AmmoModifier modifier = AmmoModifier.None;

        [Tooltip("Bonus armor piercing")]
        public int armorPiercingBonus = 0;

        [Range(0f, 2f)]
        [Tooltip("Damage multiplier (1.0 = normal)")]
        public float damageMultiplier = 1f;

        [Range(0f, 2f)]
        [Tooltip("Armor effectiveness multiplier")]
        public float armorMultiplier = 1f;

        [Range(0f, 2f)]
        [Tooltip("Knockback multiplier")]
        public float knockbackMultiplier = 1f;

        [Range(0f, 2f)]
        [Tooltip("Precision/accuracy multiplier")]
        public float precisionMultiplier = 1f;

        [Tooltip("Uses extra weapon durability")]
        public bool extraDurabilityCost = false;

        [Tooltip("Fire damage bonus")]
        public int fireDamage = 0;

        [Tooltip("Override damage type")]
        public DamageType damageTypeOverride = DamageType.PhysicalBullet;

        [Header("Display")]
        [TextArea(3, 10)]
        [Tooltip("Display name in UI")]
        public string displayName = "Ammo Name";

        [Tooltip("Icon for inventory")]
        public Sprite icon;
    }
}

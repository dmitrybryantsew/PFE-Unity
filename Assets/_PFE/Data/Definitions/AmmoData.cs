using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Ammunition type definition.
    /// Contains all data for ammo types including variants.
    /// </summary>
    [CreateAssetMenu(fileName = "AmmoData", menuName = "PFE/Data/Ammo")]
    public class AmmoData : VersionedData
    {
        [Header("Identity")]
        [SerializeField]
        private string id;

        [SerializeField]
        private string baseId;

        [Header("Stack Settings")]
        [SerializeField]
        private int stackSize = 12;

        [SerializeField]
        [Range(0f, 1f)]
        private float dropChance = 0.5f;

        [SerializeField]
        private int requiredStoryLevel = 1;

        [SerializeField]
        private int requiredLevel = 0;

        [SerializeField]
        private float weight = 0.5f;

        [Header("Pricing")]
        [SerializeField]
        private int basePrice = 1;

        [SerializeField]
        private int sellPrice = 0;

        [Header("Variant Modifiers")]
        [SerializeField]
        private AmmoModifier modifier = AmmoModifier.None;

        [SerializeField]
        private int armorPiercingBonus = 0;

        [SerializeField]
        private float damageMultiplier = 1f;

        [SerializeField]
        private float armorMultiplier = 1f;

        [SerializeField]
        private float knockbackMultiplier = 1f;

        [SerializeField]
        private float precisionMultiplier = 1f;

        [SerializeField]
        private bool extraDurabilityCost = false;

        [SerializeField]
        private int fireDamage = 0;

        [SerializeField]
        private DamageType damageTypeOverride = DamageType.PhysicalBullet;

        [Header("Display")]
        [SerializeField]
        private Sprite icon;

        [SerializeField]
        private string displayName;

        // Properties
        public override string DataId => id;
        public override string DisplayName => displayName;

        public string Id => id;
        public string BaseId => baseId;
        public int StackSize => stackSize;
        public float DropChance => dropChance;
        public int RequiredStoryLevel => requiredStoryLevel;
        public int RequiredLevel => requiredLevel;
        public float Weight => weight;
        public int BasePrice => basePrice;
        public int SellPrice => sellPrice;
        public AmmoModifier Modifier => modifier;
        public int ArmorPiercingBonus => armorPiercingBonus;
        public float DamageMultiplier => damageMultiplier;
        public float ArmorMultiplier => armorMultiplier;
        public float KnockbackMultiplier => knockbackMultiplier;
        public float PrecisionMultiplier => precisionMultiplier;
        public bool ExtraDurabilityCost => extraDurabilityCost;
        public int FireDamage => fireDamage;
        public DamageType DamageTypeOverride => damageTypeOverride;
        public Sprite Icon => icon;

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning($"AmmoData: ID is null or empty");
                return false;
            }

            if (stackSize <= 0)
            {
                Debug.LogWarning($"AmmoData {id}: Stack size must be positive");
                return false;
            }

            if (basePrice < 0)
            {
                Debug.LogWarning($"AmmoData {id}: Base price cannot be negative");
                return false;
            }

            return true;
        }
    }
}

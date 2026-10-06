using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Pure simulation contract for ammunition statistics.
    /// Decouples simulation calculators and ammo consumers from Unity ScriptableObjects.
    /// </summary>
    public interface IAmmoStats
    {
        string ammoId { get; }
        string baseId { get; }
        int stackSize { get; }
        AmmoModifier modifier { get; }
        int armorPiercingBonus { get; }
        float penetrationBudget { get; }
        float damageMultiplier { get; }
        float armorMultiplier { get; }
        float knockbackMultiplier { get; }
        float precisionMultiplier { get; }
        bool extraDurabilityCost { get; }
        int fireDamage { get; }
        DamageType damageTypeOverride { get; }
        string displayName { get; }

        string AmmoId => ammoId;
        float DamageMultiplier => damageMultiplier;
        float ArmorMultiplier => armorMultiplier;
        float KnockbackMultiplier => knockbackMultiplier;
        float PrecisionMultiplier => precisionMultiplier;
        int FireDamage => fireDamage;
    }

    /// <summary>
    /// Pure C# data container for ammo stats used in simulation, tests, and headless environments.
    /// </summary>
    public class AmmoSpec : IAmmoStats
    {
        public string ammoId { get; set; } = string.Empty;
        public string baseId { get; set; } = string.Empty;
        public int stackSize { get; set; } = 12;
        public AmmoModifier modifier { get; set; } = AmmoModifier.None;
        public int armorPiercingBonus { get; set; } = 0;
        public float penetrationBudget { get; set; } = 0f;
        public float damageMultiplier { get; set; } = 1f;
        public float armorMultiplier { get; set; } = 1f;
        public float knockbackMultiplier { get; set; } = 1f;
        public float precisionMultiplier { get; set; } = 1f;
        public bool extraDurabilityCost { get; set; } = false;
        public int fireDamage { get; set; } = 0;
        public DamageType damageTypeOverride { get; set; } = DamageType.PhysicalBullet;
        public string displayName { get; set; } = string.Empty;
    }
}

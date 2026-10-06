using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Pure simulation contract for unit combat stats.
    /// Decouples damage and critical hit calculators from Unity/MonoBehaviour UnitStats.
    /// </summary>
    public interface IUnitCombatStats
    {
        float critChanceBonus { get; }
        float critChanceBonusAdditional { get; }
        float critDamageBonus { get; }
        float critInvisChance { get; }
        float desintegrChance { get; }
        bool isNonLiving { get; }
        float damageBonus { get; }
        float damageMultiplier { get; }
        int weaponSkillLevel { get; }
        int weaponCurrentDurability { get; }
        ArmourState armour { get; }
        float armorEffectiveness { get; }
        float currentHp { get; }

        ArmourState Armour => armour;
        float ArmorEffectiveness => armorEffectiveness;
        float CurrentHp => currentHp;
        float DamageBonus => damageBonus;
        float DamageMultiplier => damageMultiplier;
    }

    /// <summary>
    /// Pure C# data container for unit combat stats in simulation and tests.
    /// </summary>
    public class UnitCombatStatsSpec : IUnitCombatStats
    {
        public float critChanceBonus { get; set; } = 0f;
        public float critChanceBonusAdditional { get; set; } = 0f;
        public float critDamageBonus { get; set; } = 0f;
        public float critInvisChance { get; set; } = 0f;
        public float desintegrChance { get; set; } = 0f;
        public bool isNonLiving { get; set; } = false;
        public float damageBonus { get; set; } = 0f;
        public float damageMultiplier { get; set; } = 1f;
        public int weaponSkillLevel { get; set; } = 1;
        public int weaponCurrentDurability { get; set; } = 100;
        public ArmourState armour { get; set; } = ArmourState.None;
        public float armorEffectiveness { get; set; } = 1f;
        public float currentHp { get; set; } = 100f;
    }
}

using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for complete damage calculation workflow.
    /// Combines base damage, ammo multipliers, vulnerabilities, armor, and critical hits.
    /// </summary>
    public interface IDamageCalculator
    {
        /// <summary>
        /// Complete damage calculation from weapon to target.
        /// </summary>
        DamageResult CalculateDamage(
            WeaponDefinition weaponDef,
            UnitStats attackerStats,
            UnitStats targetStats,
            AmmoDefinition ammoDef = null,
            bool isBackstab = false,
            bool absolutePierce = false);

        /// <summary>
        /// Simple damage calculation for testing.
        /// Direct formula without full stat system.
        /// </summary>
        float CalculateDamageSimple(
            float baseDamage,
            float damAdd,
            float damMult,
            float weaponSkill,
            float durabilityMultiplier,
            float ammoMultiplier,
            float vulnerability,
            float armor,
            float armorEffectiveness,
            float penetration,
            float critChance,
            float critMultiplier,
            bool absolutePierce = false);
    }
}

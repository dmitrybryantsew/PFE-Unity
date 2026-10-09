using PFE.Core.Rng;
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
        /// Resolve one hit against a target's armour and durability. Pure: no mutation, no static state,
        /// RNG supplied by the caller. The owner applies the returned outcome to its own state.
        /// </summary>
        /// <remarks>
        /// The wear passed in as <c>armourIntegrityDamage</c> comes from
        /// <see cref="ArmourWear"/> — the two tables are split the way AS3 splits them, with the wear
        /// in <c>Armor.damage()</c> and the reduction in <c>Unit.damage()</c>.
        /// </remarks>
        DamageOutcome ResolveDamage(
            float incomingDamage,
            float armourIntegrityDamage,
            in ArmourState armour,
            IRngService rng,
            DamageType damageType = DamageType.PhysicalBullet,
            bool ignoreArmour = false,
            float piercing = 0f,
            float armourMultiplier = 1f,
            float critChance = 0f,
            float critMultiplier = 1f,
            float skinResistance = 0f,
            float durabilityMultiplier = 1f,
            float critInvisChance = 0f,
            float desintegrChance = 0f,
            float targetCurrentHp = -1f,
            bool targetIsNonLiving = false,
            bool targetIsGrounded = true,
            bool targetIsInWater = false,
            float allVulnerabilityMultiplier = 1f,
            float shieldHp = 0f,
            float shieldArmour = 0f);

        /// <summary>
        /// Complete damage calculation from weapon to target.
        /// </summary>
        DamageResult CalculateDamage(
            IWeaponStats weaponDef,
            IUnitCombatStats attackerStats,
            IUnitCombatStats targetStats,
            IAmmoStats ammoDef = null,
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

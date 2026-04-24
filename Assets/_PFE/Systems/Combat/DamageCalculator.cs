using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Handles complete damage calculation workflow.
    /// Combines base damage, ammo multipliers, vulnerabilities, armor, and critical hits.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md (lines 570-595)
    /// Now non-static to support dependency injection and testing.
    /// </summary>
    public class DamageCalculator : IDamageCalculator
    {
        private readonly ICombatCalculator _combatCalculator;

        public DamageCalculator(ICombatCalculator combatCalculator)
        {
            _combatCalculator = combatCalculator;
        }

        /// <summary>
        /// Complete damage calculation from weapon to target.
        /// Implements the full formula chain from documentation.
        /// </summary>
        /// <param name="weaponDef">Weapon definition</param>
        /// <param name="attackerStats">Attacker unit stats</param>
        /// <param name="targetStats">Target unit stats</param>
        /// <param name="ammoDef">Ammo definition (optional)</param>
        /// <param name="isBackstab">Backstab attack (2x damage)</param>
        /// <param name="absolutePierce">Absolute pierce roll (ignores all armor)</param>
        public DamageResult CalculateDamage(
            WeaponDefinition weaponDef,
            UnitStats attackerStats,
            UnitStats targetStats,
            AmmoDefinition ammoDef = null,
            bool isBackstab = false,
            bool absolutePierce = false)
        {
            DamageResult result = new DamageResult();

            // Step 1: Base damage calculation
            float baseDamage = CalculateBaseDamage(weaponDef, attackerStats);
            result.baseDamage = baseDamage;

            // Step 2: Apply ammo multiplier
            float withAmmo = ApplyAmmoMultiplier(baseDamage, ammoDef);
            result.damageAfterAmmo = withAmmo;

            // Step 3: Apply enemy vulnerability/resistance
            float withVulnerability = ApplyVulnerability(withAmmo, weaponDef, targetStats);
            result.damageAfterVulnerability = withVulnerability;

            // Step 4: Apply armor (or absolute pierce)
            float withArmor = ApplyArmor(withVulnerability, weaponDef, targetStats, absolutePierce);
            result.damageAfterArmor = withArmor;

            // Step 5: Apply critical hit
            float finalDamage = withArmor;
            bool isCrit = CheckCriticalHit(weaponDef, attackerStats);
            result.isCritical = isCrit;

            if (isCrit)
            {
                float critMultiplier = _combatCalculator.CalculateCriticalMultiplier(
                    weaponDef.critMultiplier,
                    attackerStats.critDamageBonus);
                finalDamage = withArmor * critMultiplier;
                result.criticalMultiplier = critMultiplier;

                // Apply backstab if applicable
                if (isBackstab)
                {
                    finalDamage *= 2f;
                    result.isBackstab = true;
                }
            }

            result.finalDamage = Mathf.Max(0, finalDamage);
            return result;
        }

        /// <summary>
        /// Step 1: Calculate base damage with all modifiers.
        /// Formula: (baseDamage + damAdd) * damMult * weaponSkill * durabilityPenalty
        /// </summary>
        private float CalculateBaseDamage(WeaponDefinition weaponDef, UnitStats attackerStats)
        {
            float breaking = _combatCalculator.CalculateBreaking(
                weaponDef.maxDurability,
                attackerStats.weaponCurrentDurability);

            float durabilityPenalty = _combatCalculator.CalculateDurabilityDamageMultiplier(breaking);
            float levelPenalty = _combatCalculator.CalculateLevelPenalty(
                weaponDef.weaponLevel,
                attackerStats.weaponSkillLevel);

            return _combatCalculator.CalculateBaseDamage(
                weaponDef.baseDamage,
                attackerStats.damageBonus,
                attackerStats.damageMultiplier,
                levelPenalty,
                1f, // skillPlusDam
                durabilityPenalty);
        }

        /// <summary>
        /// Step 2: Apply ammo damage multiplier.
        /// </summary>
        private float ApplyAmmoMultiplier(float damage, AmmoDefinition ammoDef)
        {
            if (ammoDef == null)
                return damage;

            return damage * ammoDef.damageMultiplier;
        }

        /// <summary>
        /// Step 3: Apply target vulnerability/resistance.
        /// Vulnerability < 1.0 = resistance (reduces damage)
        /// Vulnerability > 1.0 = weakness (increases damage)
        /// </summary>
        private float ApplyVulnerability(
            float damage,
            WeaponDefinition weaponDef,
            UnitStats targetStats)
        {
            // Get vulnerability based on damage type
            // For now, return damage unchanged (vulnerability system needs damage type lookup)
            return damage;
        }

        /// <summary>
        /// Step 4: Apply armor reduction.
        /// Formula: damage - max(0, armor * effectiveness - penetration)
        /// </summary>
        private float ApplyArmor(
            float damage,
            WeaponDefinition weaponDef,
            UnitStats targetStats,
            bool absolutePierce)
        {
            if (absolutePierce)
            {
                // Absolute pierce ignores all armor
                return damage;
            }

            float effectiveArmor = _combatCalculator.CalculateEffectiveArmor(
                targetStats.armor,
                targetStats.armorEffectiveness,
                weaponDef.armorPenetration);

            return _combatCalculator.ApplyArmor(damage, effectiveArmor);
        }

        /// <summary>
        /// Step 5: Check and apply critical hit.
        /// </summary>
        private bool CheckCriticalHit(WeaponDefinition weaponDef, UnitStats attackerStats)
        {
            float critChance = _combatCalculator.CalculateCriticalChance(
                weaponDef.critChance,
                attackerStats.critChanceBonus,
                attackerStats.critChanceBonusAdditional);

            return _combatCalculator.RollCriticalHit(critChance);
        }

        /// <summary>
        /// Simple damage calculation for testing.
        /// Direct formula without full stat system.
        /// </summary>
        public float CalculateDamageSimple(
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
            bool absolutePierce = false)
        {
            // Step 1: Base damage
            float damage = _combatCalculator.CalculateBaseDamage(
                baseDamage, damAdd, damMult, weaponSkill, 1f, durabilityMultiplier);

            // Step 2: Ammo
            damage *= ammoMultiplier;

            // Step 3: Vulnerability
            damage *= vulnerability;

            // Step 4: Armor
            if (!absolutePierce)
            {
                float effectiveArmor = _combatCalculator.CalculateEffectiveArmor(
                    armor, armorEffectiveness, penetration);
                damage = _combatCalculator.ApplyArmor(damage, effectiveArmor);
            }

            // Step 5: Critical
            bool isCrit = _combatCalculator.RollCriticalHit(critChance);
            if (isCrit)
            {
                damage *= critMultiplier;
            }

            return Mathf.Max(0, damage);
        }
    }

    /// <summary>
    /// Result of a damage calculation.
    /// Provides breakdown of damage at each calculation step.
    /// </summary>
    public class DamageResult
    {
        /// <summary>Base damage from weapon calculation</summary>
        public float baseDamage;

        /// <summary>Damage after ammo multiplier</summary>
        public float damageAfterAmmo;

        /// <summary>Damage after vulnerability/resistance</summary>
        public float damageAfterVulnerability;

        /// <summary>Damage after armor reduction</summary>
        public float damageAfterArmor;

        /// <summary>Final damage after critical hit</summary>
        public float finalDamage;

        /// <summary>Whether this was a critical hit</summary>
        public bool isCritical;

        /// <summary>Critical hit multiplier applied</summary>
        public float criticalMultiplier = 1f;

        /// <summary>Whether this was a backstab</summary>
        public bool isBackstab;

        /// <summary>Whether absolute pierce was triggered</summary>
        public bool absolutePierceTriggered;
    }
}

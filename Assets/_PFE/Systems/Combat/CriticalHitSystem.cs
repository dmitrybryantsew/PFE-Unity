using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Handles critical hit calculations including backstab and absolute pierce.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md (lines 600-700)
    /// Now non-static to support dependency injection and testing.
    /// </summary>
    public class CriticalHitSystem : ICriticalHitSystem
    {
        private readonly ICombatCalculator _combatCalculator;

        public CriticalHitSystem(ICombatCalculator combatCalculator)
        {
            _combatCalculator = combatCalculator;
        }

        /// <summary>
        /// Calculate total critical hit chance from all sources.
        /// </summary>
        public float CalculateCriticalChance(
            WeaponDefinition weaponDef,
            UnitStats ownerStats,
            float additionalChance = 0f)
        {
            return _combatCalculator.CalculateCriticalChance(
                weaponDef.critChance,
                ownerStats.critChanceBonus,
                ownerStats.critChanceBonusAdditional + additionalChance);
        }

        /// <summary>
        /// Calculate total critical hit damage multiplier.
        /// </summary>
        public float CalculateCriticalMultiplier(
            WeaponDefinition weaponDef,
            UnitStats ownerStats,
            float additionalMultiplier = 0f)
        {
            return _combatCalculator.CalculateCriticalMultiplier(
                weaponDef.critMultiplier,
                ownerStats.critDamageBonus + additionalMultiplier);
        }

        /// <summary>
        /// Roll for critical hit.
        /// </summary>
        public bool RollCriticalHit(float critChance)
        {
            return _combatCalculator.RollCriticalHit(critChance);
        }

        /// <summary>
        /// Check if attack is a backstab (from behind).
        /// Backstab deals 2x damage.
        /// backstabAngle defines the threshold angle (default 90° = back hemisphere).
        /// </summary>
        public bool IsBackstab(Vector3 attackerPosition, Vector3 targetPosition, Vector3 targetForward, float backstabAngle = 90f)
        {
            Vector3 toAttacker = (attackerPosition - targetPosition).normalized;
            float angle = Vector3.Angle(targetForward, toAttacker);
            return angle > (180f - backstabAngle);
        }

        /// <summary>
        /// Calculate backstab multiplier (2x damage).
        /// </summary>
        public float CalculateBackstabMultiplier(bool isBackstab)
        {
            return isBackstab ? 2f : 1f;
        }

        /// <summary>
        /// Check for absolute pierce (ignores all armor).
        /// absPierRnd > 0 gives chance to set pier = 1000
        /// </summary>
        public bool RollAbsolutePierce(float absolutePierceChance)
        {
            if (absolutePierceChance <= 0)
                return false;

            return Random.value < absolutePierceChance;
        }

        /// <summary>
        /// Calculate final damage with critical hit and backstab.
        /// </summary>
        public float ApplyCriticalAndBackstab(
            float baseDamage,
            float critMultiplier,
            bool isCrit,
            bool isBackstab)
        {
            float damage = baseDamage;

            if (isCrit)
            {
                damage *= critMultiplier;
            }

            if (isBackstab)
            {
                damage *= 2f;
            }

            return damage;
        }
    }
}
